using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Supercent.Util.Editor
{
    // Size tools: what makes the playable heavy (the Build Assistant's Build size page), what other Luna export settings
    // would save (encoded with Luna's own tools, nothing applied), and applying a setting (writes luna.json).
    public static partial class LunaMcp
    {
        const int EstimateWaitMs = 100000;          // The plugin's statistics step takes seconds to a minute.
        const int MaxImages = 4;
        const long MaxImageBytes = 1500000;         // Larger previews are listed by file only.

        static Task<BuildSizeReport.Snapshot> estimateTask;

        static readonly string[] TextureArgs = { "maxSize", "maxWidth", "maxHeight", "format", "compression", "quality" };
        static readonly string[] SoundArgs = { "bitrate", "channels" };

        static Dictionary<string, object> SettingProps(Dictionary<string, object> props)
        {
            props["maxSize"] = McpServer.Prop("integer", "Texture: max width and height in pixels (larger images are scaled down to fit)");
            props["maxWidth"] = McpServer.Prop("integer", "Texture: max width (instead of maxSize)");
            props["maxHeight"] = McpServer.Prop("integer", "Texture: max height (instead of maxSize)");
            props["format"] = McpServer.Prop("string", "Texture format (luna.json value): png8, png24, png32, jpeg, webp (webp needs iOS 14+)");
            props["compression"] = McpServer.Prop("string", "PNG compression: quantSize (smallest), quantQuality, crush (lossless), none");
            props["quality"] = McpServer.Prop("integer", "JPEG / WebP quality 1-100");
            props["bitrate"] = McpServer.Prop("integer", "Sound: MP3 bitrate in kbps (32, 48, 64, 96, 128...)");
            props["channels"] = McpServer.Prop("string", "Sound: channels by name, e.g. Mono or Stereo");
            return props;
        }

        static void RegisterSizeTools()
        {
            Server.Register(new McpServer.Tool
            {
                Name = "unity_build_size",
                Description = "What makes the playable heavy, from the Luna plugin's Size breakdown of the last Build Develop (size inside the " +
                              "final playable for one ad network): total and change since the previous breakdown, size per kind, the largest " +
                              "assets with their Luna export settings (luna.json rule) and source facts (texture size / alpha, sound length), " +
                              "the biggest changes, and assets shipped twice under different names. Build Develop runs the breakdown; " +
                              "estimate=true runs it again over the last build output. Use it to suggest optimizations; try settings with " +
                              "unity_asset_size_try.",
                ReadOnly = true,
                InputSchema = McpServer.Schema(new Dictionary<string, object>
                {
                    { "network", McpServer.Prop("string", "Ad network of the breakdown (default: the one picked in the Build Assistant), e.g. ironsource, applovin, facebook") },
                    { "estimate", McpServer.Prop("boolean", "Run the plugin's size breakdown again over the last build output", false) },
                    { "top", McpServer.Prop("integer", "How many of the largest assets to list (max 200)", 25) },
                    { "kind", McpServer.Prop("string", "Only assets of this kind: textures, sounds, text-assets, fonts, meshes, animation-clips...") },
                }),
                Run = args =>
                {
                    string network = OnMainThread(() => ResolveNetwork(MiniJson.Str(args, "network")));
                    bool estimate = MiniJson.Bool(args, "estimate", false)
                                    || OnMainThread(() => BuildSizeReport.LatestOf(network) == null && BuildSizeReport.HasBuildOutput && LunaPluginBridge.CanEstimateSize);
                    if (estimate && !AwaitEstimate(network))
                        return McpServer.ToolResult.Json(new Dictionary<string, object>
                        {
                            { "state", "estimating" },
                            { "hint", "The size breakdown is still running: call unity_build_size again (without estimate) in 30 s." },
                        });
                    int top = Math.Max(1, Math.Min(200, (int)MiniJson.Num(args, "top", 25)));
                    return McpServer.ToolResult.Json(OnMainThread(() => SizeReport(network, top, MiniJson.Str(args, "kind"))));
                },
            });

            Server.Register(new McpServer.Tool
            {
                Name = "unity_asset_size_try",
                Description = "Estimates an asset's size in the playable with other Luna export settings, without applying them: textures are " +
                              "encoded with Luna's own tools (ImageMagick, pngquant, cwebp) from Luna's exported image of the last build; sounds " +
                              "are bitrate x length. Give new settings, or compare=true for Luna's usual alternatives (PNG compressions, WebP, " +
                              "JPEG for opaque images) at the current or given max size. images=true returns the encoded images to judge quality " +
                              "(show them to the user before suggesting a lossy setting). Assets come from unity_build_size.",
                ReadOnly = true,
                InputSchema = McpServer.Schema(SettingProps(new Dictionary<string, object>
                {
                    { "asset", McpServer.Prop("string", "Asset path as unity_build_size lists it, e.g. Assets/Art/bg.png") },
                    { "compare", McpServer.Prop("boolean", "Texture: also encode Luna's usual alternatives", false) },
                    { "images", McpServer.Prop("boolean", "Texture: return the current and tried images (at most 4)", false) },
                }), "asset"),
                Run = TrySize,
            });

            Server.Register(new McpServer.Tool
            {
                Name = "unity_asset_settings_apply",
                Description = "Writes Luna export settings for an asset or a folder into luna.json, like Apply in the Build Assistant's Build size " +
                              "and Assets pages (the asset gets its own rule, starting from the rule it has now). Only after the user agreed to the " +
                              "exact change. reset=true removes the path's own rule (back to the folder or default rule). Takes effect in the next " +
                              "build: unity_build_develop, then unity_build_size to measure. Changes luna.json, which the user commits or reverts.",
                ReadOnly = false,
                InputSchema = McpServer.Schema(SettingProps(new Dictionary<string, object>
                {
                    { "asset", McpServer.Prop("string", "Asset path, or a folder path (then 'kind' is required)") },
                    { "kind", McpServer.Prop("string", "Rule kind: texture or sound (default: from the asset)", null, new[] { "texture", "sound" }) },
                    { "reset", McpServer.Prop("boolean", "Remove this path's own rule instead", false) },
                }), "asset"),
                Run = args => McpServer.ToolResult.Json(OnMainThread(() => ApplySettings(args))),
            });
        }

        // ---- unity_build_size ----

        static string ResolveNetwork(string wanted)
        {
            if (string.IsNullOrWhiteSpace(wanted))
                return BuildSizeReport.Network;
            var match = LunaPluginBridge.SizeNetworks.FirstOrDefault(n => string.Equals(n.Key, wanted.Trim(), StringComparison.OrdinalIgnoreCase)
                                                                          || string.Equals(n.Value, wanted.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match.Key == null)
                throw new InvalidOperationException("Unknown network '" + wanted + "'. Networks: " + string.Join(", ", LunaPluginBridge.SizeNetworks.Select(n => n.Key)));
            return match.Key;
        }

        // Starts the plugin's statistics step (or joins the running one) and waits for it from the request thread. false = still running.
        static bool AwaitEstimate(string network)
        {
            Task<BuildSizeReport.Snapshot> task = OnMainThread(() =>
            {
                if (estimateTask != null && !estimateTask.IsCompleted)
                    return estimateTask;
                if (LunaBuildAssistantWindow.AgentBuildRunning || SafeIsBuilding())
                    throw new InvalidOperationException("A build is running: Build Develop estimates the size when it finishes (unity_build_report).");
                if (!LunaPluginBridge.CanEstimateSize)
                    throw new InvalidOperationException("This Luna plugin version has no Size breakdown.");
                if (!BuildSizeReport.HasBuildOutput)
                    throw new InvalidOperationException("No build output in LunaTemp: run unity_build_develop first.");
                return estimateTask = BuildSizeReport.Estimate(network);
            });
            DateTime until = DateTime.UtcNow.AddMilliseconds(EstimateWaitMs);
            while (!task.IsCompleted && DateTime.UtcNow < until)
                Thread.Sleep(200);
            if (!task.IsCompleted)
                return false;
            if (task.IsFaulted)
                throw new InvalidOperationException("Size breakdown failed: " + task.Exception?.GetBaseException().Message);
            return true;
        }

        static Dictionary<string, object> SizeReport(string network, int top, string kind)
        {
            BuildSizeReport.Snapshot now = BuildSizeReport.LatestOf(network), before = BuildSizeReport.PreviousOf(network);
            var data = new Dictionary<string, object>
            {
                { "network", network },
                { "networks", LunaPluginBridge.SizeNetworks.Select(n => (object)n.Key).ToList() },
            };
            if (now == null)
            {
                data["state"] = "none";
                data["hint"] = "No size breakdown for this network yet: unity_build_develop runs one; after a build, call again with estimate=true.";
                return data;
            }

            DateTime built = new DateTime(now.builtUtcTicks, DateTimeKind.Utc);
            DateTime? lastBuild = ProjectBuildState.LastPrivateBuildUtc;
            data["breakdownAt"] = built.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            data["lastBuildDevelop"] = lastBuild?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            if (lastBuild.HasValue && lastBuild.Value > built)
                data["stale"] = "The last Build Develop is newer than this breakdown: call again with estimate=true.";
            data["total"] = BuildSizeReport.FormatBytes(now.totalBytes);
            data["totalBytes"] = now.totalBytes;
            if (before != null)
            {
                data["changeSincePrevious"] = BuildSizeReport.FormatDelta(now.totalBytes - before.totalBytes);
                data["previousAt"] = new DateTime(before.builtUtcTicks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            }

            var beforeByKey = before?.entries.GroupBy(Key).ToDictionary(g => g.Key, g => g.First()) ?? new Dictionary<string, BuildSizeReport.Entry>();
            var beforeByKind = before?.entries.GroupBy(e => e.kind).ToDictionary(g => g.Key, g => g.Sum(e => e.bytes)) ?? new Dictionary<string, long>();
            data["byKind"] = now.entries.GroupBy(e => e.kind)
                .Select(g => new { Kind = g.Key, Bytes = g.Sum(e => e.bytes), Count = g.Count() })
                .OrderByDescending(k => k.Bytes)
                .Select(k =>
                {
                    var row = new Dictionary<string, object>
                    {
                        { "kind", k.Kind }, { "size", BuildSizeReport.FormatBytes(k.Bytes) }, { "bytes", k.Bytes }, { "count", k.Count },
                        { "percent", Percent(k.Bytes, now.totalBytes) },
                    };
                    if (before != null)
                        row["change"] = BuildSizeReport.FormatDelta(k.Bytes - (beforeByKind.TryGetValue(k.Kind, out long b) ? b : 0));
                    return (object)row;
                }).ToList();

            IEnumerable<BuildSizeReport.Entry> listed = now.entries;
            if (!string.IsNullOrWhiteSpace(kind))
                listed = listed.Where(e => string.Equals(e.kind, kind.Trim(), StringComparison.OrdinalIgnoreCase));
            data["assets"] = listed.OrderByDescending(e => e.bytes).Take(top)
                .Select(e => (object)Describe(e, now.totalBytes, before == null ? null : beforeByKey.TryGetValue(Key(e), out var b) ? b : null, before != null))
                .ToList();

            if (before != null)
            {
                var nowByKey = now.entries.GroupBy(Key).ToDictionary(g => g.Key, g => g.First());
                data["biggestChanges"] = nowByKey.Keys.Union(beforeByKey.Keys)
                    .Select(k => new
                    {
                        Key = k,
                        Now = nowByKey.TryGetValue(k, out var n) ? n : null,
                        Before = beforeByKey.TryGetValue(k, out var b) ? b : null,
                    })
                    .Select(x => new { x.Now, x.Before, Delta = (x.Now?.bytes ?? 0) - (x.Before?.bytes ?? 0) })
                    .Where(x => x.Delta != 0)
                    .OrderByDescending(x => Math.Abs(x.Delta)).Take(10)
                    .Select(x => (object)new Dictionary<string, object>
                    {
                        { "asset", (x.Now ?? x.Before).Label }, { "kind", (x.Now ?? x.Before).kind },
                        { "change", BuildSizeReport.FormatDelta(x.Delta) }, { "state", x.Now == null ? "removed" : x.Before == null ? "added" : "changed" },
                    }).ToList();
            }

            data["duplicates"] = BuildSizeReport.Duplicates(now).Take(10).Select(d => (object)new Dictionary<string, object>
            {
                { "kind", d.Kind }, { "sizeEach", BuildSizeReport.FormatBytes(d.Bytes) }, { "wasted", BuildSizeReport.FormatBytes(d.Wasted) },
                { "assets", d.Paths.ToList() },
            }).ToList();
            data["hint"] = "Textures and sounds: try settings with unity_asset_size_try, then suggest them to the user (quality matters in an " +
                           "ad: show lossy results). Duplicates ship the same content twice: suggest sharing one asset. Text assets (e.g. " +
                           "Spine JSON) and scripts have no Luna export setting: suggest content changes (fewer animations, unused code).";
            return data;
        }

        static string Key(BuildSizeReport.Entry e) => e.kind + "|" + e.Label;

        static double Percent(long part, long total) => total > 0 ? Math.Round(100.0 * part / total, 1) : 0;

        static Dictionary<string, object> Describe(BuildSizeReport.Entry e, long total, BuildSizeReport.Entry before, bool hasPrevious)
        {
            var row = new Dictionary<string, object>
            {
                { "asset", e.Label }, { "kind", e.kind }, { "size", BuildSizeReport.FormatBytes(e.bytes) }, { "bytes", e.bytes },
                { "percent", Percent(e.bytes, total) },
            };
            if (hasPrevious)
                row["change"] = before == null ? "new" : BuildSizeReport.FormatDelta(e.bytes - before.bytes);
            if (e.rawBytes > 0)
                row["exportedBytes"] = e.rawBytes;
            if (!e.path.StartsWith("Assets/", StringComparison.Ordinal))
                return row;
            LunaAssetRules.Kind? ruleKind = LunaAssetRules.KindOf(e.kind);
            if (ruleKind.HasValue)
            {
                try
                {
                    LunaAssetRules.Rule rule = LunaAssetRules.Get(ruleKind.Value, e.path);
                    row["lunaSettings"] = LunaAssetRules.Summary(ruleKind.Value, rule.Values);
                    row["lunaRule"] = LunaAssetRules.Describe(rule);
                }
                catch (Exception)
                {
                    // Rule shapes of another plugin version: the size alone is still useful.
                }
            }
            string facts = SourceFacts(e.path);
            if (facts != null)
                row["source"] = facts;
            return row;
        }

        static string SourceFacts(string path)
        {
            switch (AssetImporter.GetAtPath(path))
            {
                case TextureImporter t:
                    t.GetSourceTextureWidthAndHeight(out int w, out int h);
                    return $"{w}x{h}{(t.DoesSourceTextureHaveAlpha() ? " with alpha" : " opaque")}";
                case AudioImporter _:
                    AudioClip clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                    return clip == null ? null : $"{clip.length:0.0}s, {(clip.channels == 1 ? "mono" : clip.channels + " channels")}, {clip.frequency} Hz";
                default:
                    return null;
            }
        }

        // ---- unity_asset_size_try ----

        sealed class TryJob
        {
            public BuildSizeReport.Entry Entry;
            public LunaAssetRules.Kind Kind;
            public string Network, Source, Folder, Current;
            public LunaTextureEncoder.Settings CurrentSettings;
            public List<LunaTextureEncoder.Settings> Tries = new List<LunaTextureEncoder.Settings>();
            public bool Compare;
            public float SoundLength;
            public int CurrentBitrate, TriedBitrate;
        }

        static McpServer.ToolResult TrySize(Dictionary<string, object> args)
        {
            string asset = MiniJson.Str(args, "asset")?.Trim();
            bool images = MiniJson.Bool(args, "images", false);
            TryJob job = OnMainThread(() => PrepareTry(asset, args));
            double ratio = job.Entry.rawBytes > 0 ? (double)job.Entry.bytes / job.Entry.rawBytes : 4.0 / 3.0;
            var data = new Dictionary<string, object>
            {
                { "asset", job.Entry.Label }, { "kind", job.Entry.kind }, { "network", job.Network },
                { "current", new Dictionary<string, object> { { "settings", job.Current }, { "inPlayable", BuildSizeReport.FormatBytes(job.Entry.bytes) }, { "bytes", job.Entry.bytes } } },
            };

            if (job.Kind == LunaAssetRules.Kind.Sound)
            {
                long estimate = (long)(job.TriedBitrate * 1000L / 8 * job.SoundLength * ratio);
                data["tries"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        { "settings", job.TriedBitrate + " kbps" }, { "estimatedInPlayable", BuildSizeReport.FormatBytes(estimate) },
                        { "estimatedBytes", estimate }, { "change", BuildSizeReport.FormatDelta(estimate - job.Entry.bytes) },
                    },
                };
                data["note"] = "Luna encodes sounds as MP3 at a fixed bitrate: size = bitrate x length; channels barely change it.";
                return McpServer.ToolResult.Json(data);
            }

            // Textures: encoded on this request thread (the encoder is thread-safe), so Unity keeps running.
            bool opaque = LunaTextureEncoder.IsOpaque(job.Source);
            var settings = new List<LunaTextureEncoder.Settings>(job.Tries);
            if (job.Compare)
                foreach (LunaTextureEncoder.Settings s in LunaTextureEncoder.Alternatives(job.Tries.Count > 0 ? job.Tries[0] : job.CurrentSettings, opaque))
                    if (settings.All(t => t.Key != s.Key) && s.Key != job.CurrentSettings.Key)
                        settings.Add(s);
            LunaTextureEncoder.Encoded current = LunaTextureEncoder.Encode(job.Source, job.CurrentSettings, job.Folder, "mcp-current");
            var tried = settings.Select((s, i) => LunaTextureEncoder.Encode(job.Source, s, job.Folder, "mcp-try" + i)).ToList();
            data["opaque"] = opaque;
            if (current.Error == null)
                ((Dictionary<string, object>)data["current"])["shipped"] = $"{current.Width}x{current.Height}";
            data["tries"] = tried.Select(t => (object)TryRow(t, job.Entry.bytes, ratio)).OrderBy(r => ((Dictionary<string, object>)r).TryGetValue("estimatedBytes", out object b) ? (long)b : long.MaxValue).ToList();
            data["note"] = "Estimates scale the encoded file by this asset's packing ratio in the last build (base64 and archive). " +
                           "Apply a setting with unity_asset_settings_apply only after the user agreed.";

            var result = McpServer.ToolResult.Json(data);
            if (images)
            {
                int shown = 0;
                // Lossless PNG (none / crush) looks like the source: show the smallest lossy results instead.
                var lossy = tried.Where(t => t.Error == null && !(t.Settings.IsPng && (t.Settings.Compression == "none" || t.Settings.Compression == "crush")))
                    .OrderBy(t => t.Bytes);
                foreach (LunaTextureEncoder.Encoded e in new[] { current }.Concat(lossy))
                {
                    if (shown >= MaxImages || e.Error != null || e.PreviewFile == null || !File.Exists(e.PreviewFile))
                        continue;
                    if (new FileInfo(e.PreviewFile).Length > MaxImageBytes)
                        continue;
                    result.Content.Add(new Dictionary<string, object> { { "type", "text" }, { "text", (shown == 0 ? "Current: " : "Tried: ") + Label(e.Settings) } });
                    result.WithImage(Convert.ToBase64String(File.ReadAllBytes(e.PreviewFile)), "image/png");
                    shown++;
                }
            }
            return result;
        }

        static TryJob PrepareTry(string asset, Dictionary<string, object> args)
        {
            if (string.IsNullOrEmpty(asset))
                throw new InvalidOperationException("asset is required (a path from unity_build_size).");
            BuildSizeReport.Snapshot now = BuildSizeReport.Latest
                ?? throw new InvalidOperationException("No size breakdown yet: run unity_build_develop (or unity_build_size with estimate=true).");
            BuildSizeReport.Entry entry = now.entries.FirstOrDefault(e => string.Equals(e.Label, asset, StringComparison.Ordinal))
                ?? now.entries.FirstOrDefault(e => string.Equals(e.Label, asset, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"'{asset}' is not in the last size breakdown ({BuildSizeReport.Network}): not shipped, or built before it was added.");
            LunaAssetRules.Kind kind = LunaAssetRules.KindOf(entry.kind) is LunaAssetRules.Kind k && (k == LunaAssetRules.Kind.Texture || k == LunaAssetRules.Kind.Sound)
                ? k : throw new InvalidOperationException($"Only textures and sounds can be tried ({entry.kind}). For other kinds, apply a setting and measure with a build.");
            LunaAssetRules.Rule rule = LunaAssetRules.Get(kind, entry.path);
            var job = new TryJob { Entry = entry, Kind = kind, Network = BuildSizeReport.Network, Current = LunaAssetRules.Summary(kind, rule.Values), Compare = MiniJson.Bool(args, "compare", false) };

            if (kind == LunaAssetRules.Kind.Sound)
            {
                job.CurrentBitrate = LunaAssetRules.SoundBitrate(rule.Values);
                job.TriedBitrate = args.ContainsKey("bitrate") ? (int)MiniJson.Num(args, "bitrate", 0) : 0;
                if (job.TriedBitrate <= 0)
                    throw new InvalidOperationException("Give the bitrate to try (kbps); the current one is " + job.CurrentBitrate + ".");
                AudioClip clip = AssetDatabase.LoadAssetAtPath<AudioClip>(entry.path) ?? throw new InvalidOperationException("Cannot load the AudioClip " + entry.path);
                job.SoundLength = clip.length;
                return job;
            }

            if (!LunaTextureEncoder.Available)
                throw new InvalidOperationException("Luna's texture tools (ImageMagick) were not found in the Luna package.");
            job.Source = LunaTextureEncoder.SourceFor(entry)
                ?? throw new InvalidOperationException("Luna's exported image of this texture is missing (LunaTemp/stage1): run unity_build_develop first.");
            job.Folder = LunaTextureEncoder.FolderFor(entry.path);
            job.CurrentSettings = LunaAssetRules.TextureSettings(rule.Values);
            LunaTextureEncoder.Prepare();
            if (TextureArgs.Any(args.ContainsKey))
            {
                string refused = MergeTexture(rule, job.CurrentSettings, args, out LunaTextureEncoder.Settings s);
                if (refused != null)
                    throw new InvalidOperationException(refused);
                job.Tries.Add(s);
            }
            else if (!job.Compare)
                throw new InvalidOperationException("Give settings to try (maxSize, format, compression, quality) or compare=true.");
            return job;
        }

        static Dictionary<string, object> TryRow(LunaTextureEncoder.Encoded e, long currentBytes, double ratio)
        {
            var row = new Dictionary<string, object> { { "settings", Label(e.Settings) } };
            if (e.Error != null)
            {
                row["error"] = e.Error;
                return row;
            }
            long estimate = (long)(e.Bytes * ratio);
            row["estimatedInPlayable"] = BuildSizeReport.FormatBytes(estimate);
            row["estimatedBytes"] = estimate;
            row["change"] = BuildSizeReport.FormatDelta(estimate - currentBytes);
            row["shipped"] = $"{e.Width}x{e.Height}";
            row["values"] = new Dictionary<string, object>
            {
                { "maxWidth", e.Settings.MaxWidth }, { "maxHeight", e.Settings.MaxHeight }, { "format", e.Settings.Format },
                { "compression", e.Settings.IsPng ? e.Settings.Compression : null }, { "quality", e.Settings.UsesQuality ? (object)e.Settings.Quality : null },
            };
            row["file"] = e.PreviewFile;
            return row;
        }

        static string Label(LunaTextureEncoder.Settings s) =>
            $"{s.MaxWidth}x{s.MaxHeight} {s.Format}" + (s.IsPng ? " " + s.Compression : s.UsesQuality ? " q" + s.Quality : string.Empty);

        // The current settings with the given arguments on top. Returns why a value is refused, or null.
        static string MergeTexture(LunaAssetRules.Rule rule, LunaTextureEncoder.Settings start, Dictionary<string, object> args, out LunaTextureEncoder.Settings s)
        {
            s = start;
            if (args.ContainsKey("maxSize"))
                s.MaxWidth = s.MaxHeight = (int)MiniJson.Num(args, "maxSize", 0);
            if (args.ContainsKey("maxWidth"))
                s.MaxWidth = (int)MiniJson.Num(args, "maxWidth", 0);
            if (args.ContainsKey("maxHeight"))
                s.MaxHeight = (int)MiniJson.Num(args, "maxHeight", 0);
            if (s.MaxWidth < 1 || s.MaxHeight < 1 || s.MaxWidth > 8192 || s.MaxHeight > 8192)
                return "Max size must be 1-8192 pixels.";
            string format = MiniJson.Str(args, "format"), compression = MiniJson.Str(args, "compression");
            if (format != null)
            {
                string[] formats = LunaAssetRules.TextureFormatValues(rule);
                s.Format = formats.FirstOrDefault(f => string.Equals(f, format.Trim(), StringComparison.OrdinalIgnoreCase))
                           ?? (format.Trim().Equals("jpg", StringComparison.OrdinalIgnoreCase) ? formats.FirstOrDefault(f => f == "jpeg") : null);
                if (s.Format == null)
                    return "format must be one of: " + string.Join(", ", formats) + ".";
            }
            if (compression != null)
            {
                string[] compressions = LunaAssetRules.TextureCompressionValues(rule);
                s.Compression = compressions.FirstOrDefault(c => string.Equals(c, compression.Trim(), StringComparison.OrdinalIgnoreCase));
                if (s.Compression == null)
                    return "compression must be one of: " + string.Join(", ", compressions) + ".";
            }
            if (args.ContainsKey("quality"))
            {
                s.Quality = (int)MiniJson.Num(args, "quality", 0);
                if (s.Quality < 1 || s.Quality > 100)
                    return "quality must be 1-100.";
            }
            return null;
        }

        // ---- unity_asset_settings_apply ----

        static Dictionary<string, object> ApplySettings(Dictionary<string, object> args)
        {
            if (LunaBuildAssistantWindow.AgentBuildRunning || SafeIsBuilding() || !string.IsNullOrEmpty(SessionState.GetString(UploadKey, null)))
                throw new InvalidOperationException("A build or an upload is running: change luna.json after it finished.");
            string path = MiniJson.Str(args, "asset")?.Trim().TrimEnd('/');
            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets", StringComparison.Ordinal))
                throw new InvalidOperationException("asset must be a project path starting with Assets/.");
            bool folder = AssetDatabase.IsValidFolder(path);
            if (!folder && string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path)))
                throw new InvalidOperationException("No asset or folder at " + path);
            LunaAssetRules.Kind kind = RuleKind(MiniJson.Str(args, "kind"), folder ? null : path);

            LunaAssetRules.Rule rule = LunaAssetRules.Get(kind, path);
            string before = LunaAssetRules.Summary(kind, rule.Values), beforeRule = LunaAssetRules.Describe(rule);
            if (MiniJson.Bool(args, "reset", false))
            {
                if (!rule.IsOwn)
                    throw new InvalidOperationException(path + " has no rule of its own (" + beforeRule + ").");
                LunaAssetRules.Reset(rule);
            }
            else
            {
                string refused;
                if (kind == LunaAssetRules.Kind.Texture)
                {
                    if (!TextureArgs.Any(args.ContainsKey))
                        throw new InvalidOperationException("Give texture settings: maxSize / maxWidth / maxHeight, format, compression, quality.");
                    refused = MergeTexture(rule, LunaAssetRules.TextureSettings(rule.Draft), args, out LunaTextureEncoder.Settings s);
                    if (refused == null)
                        LunaAssetRules.SetTextureSettings(rule, s);
                }
                else
                {
                    if (!SoundArgs.Any(args.ContainsKey))
                        throw new InvalidOperationException("Give sound settings: bitrate, channels.");
                    refused = LunaAssetRules.SetSoundSettings(rule, args.ContainsKey("bitrate") ? (int?)MiniJson.Num(args, "bitrate", 0) : null,
                                                              MiniJson.Str(args, "channels"));
                }
                if (refused != null)
                    throw new InvalidOperationException(refused);
                if (!rule.IsDirty)
                    return new Dictionary<string, object> { { "asset", path }, { "changed", false }, { "settings", before }, { "rule", beforeRule } };
                LunaAssetRules.Apply(rule);
            }

            LunaAssetRules.Rule after = LunaAssetRules.Get(kind, path);
            Debug.Log($"[LunaBuildAssistant] Luna settings of {path} changed by an agent: {before} -> {LunaAssetRules.Summary(kind, after.Values)}.");
            return new Dictionary<string, object>
            {
                { "asset", path }, { "kind", kind.ToString().ToLowerInvariant() }, { "changed", true },
                { "before", before }, { "beforeRule", beforeRule },
                { "after", LunaAssetRules.Summary(kind, after.Values) }, { "rule", LunaAssetRules.Describe(after) },
                { "hint", "Saved in luna.json; takes effect in the next build. unity_build_develop, then unity_build_size to measure. " +
                          "Tell the user luna.json changed (they commit or revert it)." },
            };
        }

        static LunaAssetRules.Kind RuleKind(string wanted, string assetPath)
        {
            if (!string.IsNullOrWhiteSpace(wanted))
            {
                switch (wanted.Trim().ToLowerInvariant())
                {
                    case "texture": case "textures": return LunaAssetRules.Kind.Texture;
                    case "sound": case "sounds": return LunaAssetRules.Kind.Sound;
                    default: throw new InvalidOperationException("kind must be texture or sound.");
                }
            }
            if (assetPath == null)
                throw new InvalidOperationException("kind (texture or sound) is required for a folder.");
            switch (AssetImporter.GetAtPath(assetPath))
            {
                case TextureImporter _: return LunaAssetRules.Kind.Texture;
                case AudioImporter _: return LunaAssetRules.Kind.Sound;
                default: throw new InvalidOperationException(assetPath + " is not a texture or a sound: only those settings can be applied here.");
            }
        }
    }
}
