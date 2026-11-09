using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Supercent.Util.Editor
{
    // Project tools: the Editor Console, why an asset ships (who references it, up to a Luna scene), and the plugin's
    // force-include / force-exclude lists. Agents read and change these without a general-purpose Unity bridge.
    public static partial class LunaMcp
    {
        const int ProjectScanTimeoutMs = 120000;

        static void RegisterProjectTools()
        {
            Server.Register(new McpServer.Tool
            {
                Name = "unity_console",
                Description = "Unity Editor Console entries, newest first, identical messages collapsed with a count: errors (also compile, " +
                              "import and exceptions), warnings, logs. Optional text filter and stack traces.",
                ReadOnly = true,
                InputSchema = McpServer.Schema(new Dictionary<string, object>
                {
                    { "types", McpServer.Prop("string", "Comma-separated: error, warning, log", "error,warning") },
                    { "count", McpServer.Prop("integer", "Distinct messages to return (max 200)", 30) },
                    { "filter", McpServer.Prop("string", "Only messages containing this text (case-insensitive)") },
                    { "stack", McpServer.Prop("boolean", "Include stack traces", false) },
                }),
                Run = args => McpServer.ToolResult.Json(OnMainThread(() => ReadConsole(MiniJson.Str(args, "types", "error,warning"),
                    Math.Max(1, Math.Min(200, (int)MiniJson.Num(args, "count", 30))), MiniJson.Str(args, "filter"), MiniJson.Bool(args, "stack", false)))),
            });

            Server.Register(new McpServer.Tool
            {
                Name = "unity_asset_references",
                Description = "Why an asset is in the playable: the assets that reference it (with the GameObject / component / property " +
                              "when the referencer is a prefab, material or other asset, including prefab overrides) and reference chains up " +
                              "to a Luna scene or a force-include. Also says when the asset is force-excluded or reaches no Luna scene. " +
                              "Use it before removing or replacing an asset to shrink the build.",
                ReadOnly = true,
                InputSchema = McpServer.Schema(new Dictionary<string, object>
                {
                    { "asset", McpServer.Prop("string", "Asset path (Assets/...) or GUID") },
                    { "chains", McpServer.Prop("integer", "Reference chains to list (max 20)", 5) },
                }, "asset"),
                Run = args => McpServer.ToolResult.Json(OnMainThread(() => AssetReferences(MiniJson.Str(args, "asset"),
                    Math.Max(1, Math.Min(20, (int)MiniJson.Num(args, "chains", 5)))), ProjectScanTimeoutMs)),
            });

            Server.Register(new McpServer.Tool
            {
                Name = "unity_luna_asset_lists",
                Description = "The Luna plugin's asset lists in luna.json: force-include (always shipped), force-exclude (never shipped) and " +
                              "excluded scripts. Without add / remove it only reads. add / remove change luna.json (only after the user " +
                              "agreed); a build is needed to see the effect.",
                ReadOnly = false,
                InputSchema = McpServer.Schema(new Dictionary<string, object>
                {
                    { "list", McpServer.Prop("string", "List to change", null, new[] { "include", "exclude", "scripts" }) },
                    { "add", new Dictionary<string, object> { { "type", "array" }, { "items", McpServer.Prop("string", "Asset / folder path or glob") }, { "description", "Paths or globs to add" } } },
                    { "remove", new Dictionary<string, object> { { "type", "array" }, { "items", McpServer.Prop("string", "Entry to remove") }, { "description", "Entries to remove" } } },
                }),
                Run = args => McpServer.ToolResult.Json(OnMainThread(() => AssetLists(MiniJson.Str(args, "list"), Strings(args, "add"), Strings(args, "remove")))),
            });
        }

        static List<string> Strings(Dictionary<string, object> args, string key) =>
            args.TryGetValue(key, out object v) && v is List<object> list ? list.OfType<string>().Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList()
            : args.TryGetValue(key, out v) && v is string one && !string.IsNullOrWhiteSpace(one) ? new List<string> { one.Trim() } : new List<string>();

        // ---- unity_console ----

        // LogEntry.mode bits (UnityEditor internal ConsoleWindow.Mode).
        const int ErrorModes = 1 | 2 | 16 | 64 | 256 | 2048 | 8192 | 131072 | 1048576 | 2097152 | 4194304;
        const int WarningModes = 128 | 512 | 4096;

        static Dictionary<string, object> ReadConsole(string types, int count, string filter, bool stack)
        {
            Assembly editor = typeof(EditorWindow).Assembly;
            Type entries = editor.GetType("UnityEditor.LogEntries") ?? throw new NotSupportedException("UnityEditor.LogEntries not found in this Unity version");
            Type entryType = editor.GetType("UnityEditor.LogEntry");
            const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            FieldInfo message = entryType.GetField("message", fields) ?? entryType.GetField("condition", fields);
            FieldInfo mode = entryType.GetField("mode", fields), file = entryType.GetField("file", fields), line = entryType.GetField("line", fields);
            var wanted = new HashSet<string>((types ?? "error,warning").Split(',').Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0));

            var rows = new List<Dictionary<string, object>>();
            var byKey = new Dictionary<string, Dictionary<string, object>>();
            var totals = new Dictionary<string, int> { { "error", 0 }, { "warning", 0 }, { "log", 0 } };
            entries.GetMethod("StartGettingEntries", statics).Invoke(null, null);
            try
            {
                int n = (int)entries.GetMethod("GetCount", statics).Invoke(null, null);
                object entry = Activator.CreateInstance(entryType);
                MethodInfo get = entries.GetMethod("GetEntryInternal", statics);
                for (int i = n - 1; i >= 0; i--)
                {
                    get.Invoke(null, new[] { (object)i, entry });
                    int m = (int)mode.GetValue(entry);
                    string type = (m & ErrorModes) != 0 ? "error" : (m & WarningModes) != 0 ? "warning" : "log";
                    totals[type]++;
                    if (!wanted.Contains(type))
                        continue;
                    string text = (string)message.GetValue(entry) ?? string.Empty;
                    if (!string.IsNullOrEmpty(filter) && text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    int cut = text.IndexOf('\n');
                    string first = cut < 0 ? text : text.Substring(0, cut);
                    string key = type + "|" + first;
                    if (byKey.TryGetValue(key, out var seen))
                    {
                        seen["count"] = (int)seen["count"] + 1;
                        continue;
                    }
                    if (rows.Count >= count)
                        continue;
                    var row = new Dictionary<string, object> { { "type", type }, { "message", Clip(first, 600) }, { "count", 1 } };
                    string at = (string)file?.GetValue(entry);
                    if (!string.IsNullOrEmpty(at))
                        row["at"] = at + ":" + line?.GetValue(entry);
                    if (stack && cut >= 0)
                        row["stack"] = Clip(text.Substring(cut + 1).Trim(), 2000);
                    rows.Add(row);
                    byKey[key] = row;
                }
            }
            finally
            {
                entries.GetMethod("EndGettingEntries", statics).Invoke(null, null);
            }
            return new Dictionary<string, object>
            {
                { "totals", totals },
                { "entries", rows.Cast<object>().ToList() },
                { "note", "Newest first; count = identical messages collapsed. The Console keeps what Unity has not cleared (Clear on Play / compile)." },
            };
        }

        static string Clip(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";

        // ---- unity_asset_references ----

        static Dictionary<string, List<string>> referencedBy;        // Asset -> assets that reference it directly.

        static Dictionary<string, List<string>> ReferencedBy()
        {
            if (referencedBy != null)
                return referencedBy;
            var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (string path in AssetDatabase.GetAllAssetPaths())
            {
                if (!path.StartsWith("Assets/", StringComparison.Ordinal) || AssetDatabase.IsValidFolder(path) || IsEditorPath(path))
                    continue;
                foreach (string dep in AssetDatabase.GetDependencies(path, false))
                {
                    if (dep == path)
                        continue;
                    if (!map.TryGetValue(dep, out List<string> list))
                        map[dep] = list = new List<string>();
                    list.Add(path);
                }
            }
            EditorApplication.projectChanged -= ForgetReferences;
            EditorApplication.projectChanged += ForgetReferences;
            return referencedBy = map;
        }

        static void ForgetReferences() => referencedBy = null;

        static bool IsEditorPath(string path) => path.IndexOf("/Editor/", StringComparison.OrdinalIgnoreCase) >= 0;

        static Dictionary<string, object> AssetReferences(string asset, int maxChains)
        {
            if (string.IsNullOrWhiteSpace(asset))
                throw new InvalidOperationException("asset is required.");
            string path = asset.Trim();
            if (!path.Contains("/") && AssetDatabase.GUIDToAssetPath(path) is string fromGuid && fromGuid.Length > 0)
                path = fromGuid;
            if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path)))
                throw new InvalidOperationException("No asset at " + path);

            List<string> scenes = LunaPluginBridge.LunaScenes;
            List<string> includes = LunaPluginBridge.PathList("assets", "includes"), excludes = LunaPluginBridge.PathList("assets", "excludes");
            string Root(string p) =>
                scenes.Contains(p) ? "Luna scene" : includes.Any(i => Matches(p, i)) ? "force-include" : p.Contains("/Resources/") ? "Resources folder (Luna ships it only if loaded by name; check unity_build_size)" : null;

            Dictionary<string, List<string>> map = ReferencedBy();
            var data = new Dictionary<string, object> { { "asset", path } };
            string excludedBy = excludes.FirstOrDefault(e => Matches(path, e));
            if (excludedBy != null)
                data["forceExcluded"] = excludedBy;
            if (IsEditorPath(path))
                data["editorOnly"] = "In an Editor folder: never shipped.";
            string root = Root(path);
            if (root != null)
                data["root"] = root;

            List<string> direct = map.TryGetValue(path, out List<string> d) ? d : new List<string>();
            data["referencedBy"] = direct.Take(30).Select(r => (object)new Dictionary<string, object>
            {
                { "asset", r }, { "root", Root(r) }, { "where", Where(r, path) },
            }).ToList();
            data["referencedByCount"] = direct.Count;

            // Breadth-first up the reverse references: shortest chains to a root first.
            var chains = new List<object>();
            var parent = new Dictionary<string, string> { { path, null } };
            var queue = new Queue<string>();
            queue.Enqueue(path);
            while (queue.Count > 0 && chains.Count < maxChains)
            {
                string current = queue.Dequeue();
                if (current != path && Root(current) != null)
                {
                    var chain = new List<object>();
                    for (string p = current; p != null; p = parent[p])
                        chain.Add(p);
                    chains.Add(new Dictionary<string, object> { { "root", Root(current) }, { "chain", chain } });
                    continue;                           // A root ships everything below it: no need to climb further.
                }
                if (!map.TryGetValue(current, out List<string> up))
                    continue;
                foreach (string next in up)
                    if (!parent.ContainsKey(next))
                    {
                        parent[next] = current;
                        queue.Enqueue(next);
                    }
            }
            data["chains"] = chains;
            if (chains.Count == 0 && root == null)
                data["note"] = "No Luna scene, force-include or Resources folder reaches this asset through asset references: it ships only " +
                               "if loaded another way (project settings, code by name) or not at all. unity_build_size lists what ships.";
            else
                data["note"] = "Chains run from the root down to the asset. Scene references are counted from the scene file; open the " +
                               "scene in Unity to see the objects.";
            return data;
        }

        // Where a referencer points at the asset: GameObject path / component / property (prefab overrides included).
        static List<object> Where(string referencer, string target)
        {
            var found = new List<object>();
            string guid = AssetDatabase.AssetPathToGUID(target);
            if (referencer.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                string text = File.ReadAllText(referencer);
                found.Add($"{Regex.Matches(text, guid).Count} reference(s) in the scene file");
                return found;
            }
            var targets = new HashSet<UnityEngine.Object>(AssetDatabase.LoadAllAssetsAtPath(target));
            foreach (UnityEngine.Object o in AssetDatabase.LoadAllAssetsAtPath(referencer))
            {
                if (o == null || found.Count >= 12)
                    continue;
                var so = new SerializedObject(o);
                SerializedProperty p = so.GetIterator();
                while (p.Next(true) && found.Count < 12)
                    if (p.propertyType == SerializedPropertyType.ObjectReference && p.objectReferenceValue != null && targets.Contains(p.objectReferenceValue))
                        found.Add($"{Owner(o)} / {o.GetType().Name}.{p.propertyPath}");
            }
            if (found.Count == 0)
                found.Add("referenced (dependency) but no serialized field found: e.g. a stale prefab override or a nested asset");
            return found;
        }

        static string Owner(UnityEngine.Object o)
        {
            Transform t = o is Component c ? c.transform : o is GameObject g ? g.transform : null;
            if (t == null)
                return o.name;
            var names = new List<string>();
            for (; t != null; t = t.parent)
                names.Insert(0, t.name);
            return string.Join("/", names);
        }

        // Luna list entries are paths, folders (prefix) or globs (*, **).
        static bool Matches(string path, string entry)
        {
            if (string.IsNullOrEmpty(entry))
                return false;
            if (entry.IndexOf('*') < 0)
                return path == entry || path.StartsWith(entry.TrimEnd('/') + "/", StringComparison.Ordinal);
            string pattern = "^" + Regex.Escape(entry).Replace(@"\*\*/", "(?:.*/)?").Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*");
            return Regex.IsMatch(path, pattern + (entry.EndsWith("/") ? string.Empty : "(?:$|/)"));
        }

        // ---- unity_luna_asset_lists ----

        static Dictionary<string, object> AssetLists(string list, List<string> add, List<string> remove)
        {
            var lists = new Dictionary<string, List<string>>
            {
                { "include", LunaPluginBridge.PathList("assets", "includes") },
                { "exclude", LunaPluginBridge.PathList("assets", "excludes") },
                { "scripts", LunaPluginBridge.PathList("scripts", "excludes") },
            };
            var changes = new List<object>();
            if (add.Count > 0 || remove.Count > 0)
            {
                if (string.IsNullOrEmpty(list) || !lists.ContainsKey(list))
                    throw new InvalidOperationException("list must be include, exclude or scripts to add or remove entries.");
                if (LunaBuildAssistantWindow.AgentBuildRunning || SafeIsBuilding() || !string.IsNullOrEmpty(SessionState.GetString(UploadKey, null)))
                    throw new InvalidOperationException("A build or an upload is running: change luna.json after it finished.");
                List<string> target = lists[list];
                foreach (string entry in add)
                {
                    bool exists = entry.IndexOf('*') >= 0 || AssetDatabase.IsValidFolder(entry.TrimEnd('/')) || File.Exists(entry);
                    if (!exists)
                        throw new InvalidOperationException($"'{entry}' is not an asset, folder or glob of this project.");
                    if (target.Contains(entry))
                        continue;
                    target.Add(entry);
                    changes.Add("added " + entry);
                    if (list != "scripts")              // Like the plugin: an asset is either force-included or force-excluded.
                        lists[list == "include" ? "exclude" : "include"].Remove(entry);
                }
                foreach (string entry in remove)
                    if (target.Remove(entry))
                        changes.Add("removed " + entry);
                    else
                        throw new InvalidOperationException($"'{entry}' is not in the {list} list.");
                if (changes.Count > 0)
                {
                    LunaPluginBridge.SaveConfig();
                    Debug.Log($"[LunaBuildAssistant] Luna {list} list changed by an agent: {string.Join("; ", changes)}.");
                }
            }
            var data = lists.ToDictionary(l => l.Key, l => (object)l.Value.ToList());
            if (changes.Count > 0)
            {
                data["changes"] = changes;
                data["hint"] = "Saved in luna.json; takes effect in the next build. Tell the user luna.json changed.";
            }
            return data;
        }
    }
}
