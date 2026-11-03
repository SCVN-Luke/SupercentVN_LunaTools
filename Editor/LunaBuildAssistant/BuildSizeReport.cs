using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Supercent.Util.Editor
{
    // The playable's size per asset for one ad network, exactly as the plugin's Size breakdown shows it:
    // the plugin's statistics step (LunaPluginBridge.EstimateSize) writes LunaTemp/stage3/stats.json with
    // each asset's size inside the final playable (encoded, base64) for that network.
    // Snapshots are kept per network so a build can be compared with the one before.
    public static class BuildSizeReport
    {
        const string StatsFile = "LunaTemp/stage3/stats.json";
        const string BuildOutput = "LunaTemp/stage3";
        const string HistoryFolder = "Library/SupercentVN/LunaBuildSize";
        const string NetworkPref = "SupercentVN.LunaBuildAssistant.SizeNetwork";
        public const string DefaultNetwork = "ironsource";

        static readonly Regex DataName = new Regex("\"data\":\\[\"(?:[^\"\\\\]|\\\\.)*\",");

        [Serializable] class Stats { public StatsType[] types; public double total, scripts, preloader, other; }
        [Serializable] class StatsType { public string title; public double size; public StatsAsset[] assets; }
        [Serializable] class StatsAsset { public string title; public double size; public string path; public string lunaPath; }

        [Serializable]
        public class Entry
        {
            public string title;
            public string path;                 // Asset path, or empty for built-in/generated content (shaders, settings).
            public string kind;                 // textures, meshes, sounds, ...
            public long bytes;                  // Size inside the playable for the network.
            public long rawBytes;               // Size of the exported file before packing (LunaTemp/stage2).
            public string lunaPath;             // That exported file: what Luna ships (e.g. the encoded texture).
            public string hash;                 // Same hash = same content under another name.
            public string Label => string.IsNullOrEmpty(path) ? title : path;
        }

        [Serializable]
        public class Snapshot
        {
            public string network;
            public long builtUtcTicks;
            public long totalBytes;
            public Entry[] entries;
            public DateTime BuiltLocal => new DateTime(builtUtcTicks, DateTimeKind.Utc).ToLocalTime();
        }

        public sealed class Duplicate
        {
            public string Kind;
            public long Bytes;
            public string[] Paths;
            public long Wasted => Bytes * (Paths.Length - 1);
        }

        static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;
        static string HistoryPath(string network, string name) => Path.Combine(ProjectRoot, HistoryFolder, network + "-" + name + ".json");

        public static string Network
        {
            get => EditorPrefs.GetString(NetworkPref, DefaultNetwork);
            set => EditorPrefs.SetString(NetworkPref, value);
        }

        public static bool HasBuildOutput => Directory.Exists(Path.Combine(ProjectRoot, BuildOutput));

        public static Snapshot Latest => Load(Network, "latest");
        public static Snapshot Previous => Load(Network, "previous");

        // Runs the plugin's statistics step for the selected network and stores the result.
        public static async System.Threading.Tasks.Task<Snapshot> Estimate()
        {
            if (!HasBuildOutput)
                throw new InvalidOperationException("No build output in LunaTemp: run Build Develop first.");
            string network = Network;
            string stats = Path.Combine(ProjectRoot, StatsFile);
            DateTime before = File.Exists(stats) ? File.GetLastWriteTimeUtc(stats) : default;
            string output = await LunaPluginBridge.EstimateSize(network);
            if (!File.Exists(stats) || File.GetLastWriteTimeUtc(stats) == before)
                throw new InvalidOperationException("The plugin did not write a new size breakdown. See the Console and LunaTemp/luna.log.");
            int start = output?.IndexOf('{') ?? -1, end = output?.LastIndexOf('}') ?? -1;   // Log lines around the JSON.
            return Capture(network, start >= 0 && end > start ? output.Substring(start, end - start + 1) : File.ReadAllText(stats));
        }

        // Parses the stats JSON; the latest snapshot of the network becomes the previous one.
        static Snapshot Capture(string network, string json)
        {
            string path = Path.Combine(ProjectRoot, StatsFile);
            Stats stats = JsonUtility.FromJson<Stats>(json);
            var entries = new List<Entry>();
            foreach (var part in new[] { ("scripts", "Scripts", stats.scripts), ("preloader", "Preloader", stats.preloader), ("other", "Other", stats.other) })
                if (part.Item3 > 0)
                    entries.Add(new Entry { kind = part.Item1, title = part.Item2, path = string.Empty, bytes = (long)part.Item3 });
            foreach (StatsType type in stats.types ?? new StatsType[0])
                foreach (StatsAsset asset in type.assets ?? new StatsAsset[0])
                {
                    var entry = new Entry { title = asset.title, path = asset.path ?? string.Empty, kind = type.title, bytes = (long)asset.size };
                    if (!string.IsNullOrEmpty(asset.lunaPath) && File.Exists(asset.lunaPath))
                    {
                        entry.rawBytes = new FileInfo(asset.lunaPath).Length;
                        entry.lunaPath = asset.lunaPath;
                        entry.hash = ContentHash(asset.lunaPath);
                    }
                    entries.Add(entry);
                }

            var snapshot = new Snapshot
            {
                network = network,
                builtUtcTicks = File.GetLastWriteTimeUtc(path).Ticks,
                totalBytes = (long)stats.total,
                entries = entries.OrderByDescending(e => e.bytes).ToArray(),
            };
            Snapshot latest = Load(network, "latest");
            if (latest != null && latest.builtUtcTicks != snapshot.builtUtcTicks)
                Save(network, "previous", latest);
            Save(network, "latest", snapshot);
            return snapshot;
        }

        public static List<Duplicate> Duplicates(Snapshot s) =>
            s.entries.Where(e => e.hash != null && e.path.StartsWith("Assets/", StringComparison.Ordinal))
                .GroupBy(e => e.kind + "|" + e.hash)
                .Where(g => g.Count() > 1)
                .Select(g => new Duplicate { Kind = g.First().kind, Bytes = g.First().bytes, Paths = g.Select(e => e.path).OrderBy(p => p).ToArray() })
                .OrderByDescending(d => d.Wasted)
                .ToList();

        public static string FormatBytes(long bytes)
        {
            double abs = Math.Abs(bytes);
            return abs >= 1 << 20 ? (bytes / 1048576.0).ToString("0.00") + " MB" : abs >= 1024 ? (bytes / 1024.0).ToString("0.0") + " KB" : bytes + " B";
        }

        public static string FormatDelta(long delta) => delta == 0 ? "±0" : (delta > 0 ? "+" : "−") + FormatBytes(Math.Abs(delta));

        // Exported JSON assets carry their name first in "data"; drop it so renamed copies hash the same.
        static string ContentHash(string file)
        {
            byte[] bytes = File.ReadAllBytes(file);
            if (file.EndsWith(".json", StringComparison.Ordinal))
            {
                string text = System.Text.Encoding.UTF8.GetString(bytes);
                int data = text.IndexOf("\"data\":", StringComparison.Ordinal);
                if (data >= 0)
                    bytes = System.Text.Encoding.UTF8.GetBytes(DataName.Replace(text.Substring(data), "\"data\":[", 1));
            }
            using (MD5 md5 = MD5.Create())
                return BitConverter.ToString(md5.ComputeHash(bytes)).Replace("-", string.Empty);
        }

        static readonly Dictionary<string, KeyValuePair<DateTime, Snapshot>> loaded = new Dictionary<string, KeyValuePair<DateTime, Snapshot>>();

        // Cached per file write time: the Build Assistant reads the latest size on every repaint.
        static Snapshot Load(string network, string name)
        {
            string path = HistoryPath(network, name);
            DateTime time = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
            if (!loaded.TryGetValue(path, out KeyValuePair<DateTime, Snapshot> cached) || cached.Key != time)
            {
                cached = new KeyValuePair<DateTime, Snapshot>(time, time == default ? null : JsonUtility.FromJson<Snapshot>(File.ReadAllText(path)));
                loaded[path] = cached;
            }
            return cached.Value;
        }

        static void Save(string network, string name, Snapshot s)
        {
            string path = HistoryPath(network, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(s));
        }
    }
}
