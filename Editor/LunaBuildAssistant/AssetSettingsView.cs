using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Kind = Supercent.Util.Editor.LunaAssetRules.Kind;

namespace Supercent.Util.Editor
{
    // Assets page: the plugin's Assets section (luna.json) in one place.
    //   Assets: force-include / force-exclude asset lists and the excluded scripts (plugin's Code → Exclusions).
    //   Textures, Sounds, Fonts, Meshes, Animations: the kind's default settings, then per-asset/folder settings.
    // Settings are drafts until Apply (LunaAssetRules); list edits are saved right away, like the plugin.
    public sealed class AssetSettingsView
    {
        static readonly string[] Tabs = { "Assets", "Textures", "Sounds", "Fonts", "Meshes", "Animations" };
        static readonly Kind[] TabKinds = { default, Kind.Texture, Kind.Sound, Kind.Font, Kind.Mesh, Kind.Animation };

        sealed class PathList
        {
            public string Section, List, Title, Hint;
            public string Pattern = string.Empty;   // Typed path or glob waiting for Add.
        }

        readonly PathList[] lists =
        {
            new PathList { Section = "assets", List = "includes", Title = "Force-include Assets",
                Hint = "Assets always included in the build, even when nothing references them. A folder includes every asset in it; force-include always wins." },
            new PathList { Section = "assets", List = "excludes", Title = "Force-exclude Assets",
                Hint = "Assets left out of the build. A folder excludes every asset in it. Paths or globs such as **/Editor/." },
            new PathList { Section = "scripts", List = "excludes", Title = "Excluded scripts",
                Hint = "The plugin's Code → Exclusions: C# files or folders (paths or globs) left out of the playable. By default all scripts are included." },
        };

        int tab;
        Vector2 scroll;
        LunaAssetRules.Rule defaultRule;
        LunaAssetRules.Rule selectedRule;
        string selectedKey;
        string error;
        string note;
        Action deferred;                        // List changes wait for the next Layout so a pass never sees half a list.

        Kind TabKind => TabKinds[tab];

        // Picks up changes made in the plugin window; unapplied drafts are kept.
        public void Reload()
        {
            if (defaultRule != null && !SafeDirty(defaultRule))
                defaultRule = null;
            if (selectedRule != null && !SafeDirty(selectedRule))
                selectedRule = null;
        }

        public void Draw()
        {
            if (Event.current.type == EventType.Layout && deferred != null)
            {
                Action run = deferred;
                deferred = null;
                Guard(run);
            }

            AssistantUI.Header("Assets", "Luna export settings saved in luna.json, the same as the plugin's Assets section.\n" +
                                         "They take effect on the next Build Develop / upload.");
            int newTab = AssistantUI.Segmented(tab, Tabs);
            if (newTab != tab && ConfirmLeave())
            {
                tab = newTab;
                defaultRule = selectedRule = null;
                selectedKey = null;
                note = null;
                GUI.FocusControl(null);
            }
            if (error != null)
            {
                EditorGUILayout.HelpBox(error, MessageType.Error);
                if (GUILayout.Button("Retry", GUILayout.Width(80)))
                {
                    error = null;
                    defaultRule = selectedRule = null;
                }
                return;
            }

            scroll = EditorGUILayout.BeginScrollView(scroll);
            if (tab == 0)
                foreach (PathList list in lists)
                    DrawList(list);
            else
                DrawRules(TabKind);
            EditorGUILayout.EndScrollView();
        }

        // ---- Assets tab ----

        void DrawList(PathList list)
        {
            AssistantUI.SubHeader(list.Title, list.Hint);
            List<string> paths = null;
            Guard(() => paths = LunaPluginBridge.PathList(list.Section, list.List));
            if (paths == null)
                return;

            using (var box = new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (paths.Count == 0)
                    GUILayout.Label("Empty. Drop assets or folders here.", AssistantUI.Caption);
                for (int i = 0; i < paths.Count; i++)
                {
                    string path = paths[i];
                    using (var row = AssistantUI.Row(i))
                    {
                        bool exists = Exists(path);
                        GUIContent icon = IsPattern(path) ? AssistantUI.Icon("FilterByType", "Glob pattern")
                            : exists ? new GUIContent(AssetDatabase.GetCachedIcon(path)) : AssistantUI.Icon("console.warnicon.sml", "Not found in the project");
                        GUILayout.Label(icon, GUILayout.Width(18), GUILayout.Height(18));
                        GUILayout.Label(new GUIContent(path, path), AssistantUI.RowLabel);
                        if (paths.IndexOf(path) != i)
                            GUILayout.Label("duplicate", AssistantUI.Caption, GUILayout.ExpandWidth(false));
                        if (GUILayout.Button(new GUIContent("×", "Remove"), EditorStyles.miniButton, GUILayout.Width(22)))
                        {
                            int index = i;
                            deferred += () => Change(() => paths.RemoveAt(index));
                        }
                        if (exists && Event.current.type == EventType.MouseDown && row.rect.Contains(Event.current.mousePosition))
                        {
                            EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(path));
                            Event.current.Use();
                        }
                    }
                }
                HandleDrop(box.rect, p => true, p => AddPath(list, p));
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                UnityEngine.Object picked = EditorGUILayout.ObjectField(null, typeof(UnityEngine.Object), false, GUILayout.Width(170));
                if (picked != null)
                    AddPath(list, AssetDatabase.GetAssetPath(picked));
                if (GUILayout.Button(new GUIContent("Add selected", "Add the assets/folders selected in the Project window"), EditorStyles.miniButton, GUILayout.Width(90)))
                    foreach (string path in SelectedPaths())
                        AddPath(list, path);
                GUILayout.Space(10);
                list.Pattern = EditorGUILayout.TextField(list.Pattern);
                using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(list.Pattern)))
                    if (GUILayout.Button(new GUIContent("Add", "Add this path or glob, e.g. **/Editor/"), EditorStyles.miniButton, GUILayout.Width(40)))
                    {
                        AddPath(list, list.Pattern.Trim());
                        list.Pattern = string.Empty;
                        GUI.FocusControl(null);
                    }
            }
            EditorGUILayout.Space(14);
        }

        // Like the plugin's Project window menu: force-including an asset takes it out of force-exclude and back.
        void AddPath(PathList list, string path)
        {
            if (string.IsNullOrEmpty(path))
                return;
            deferred += () =>
            {
                List<string> paths = LunaPluginBridge.PathList(list.Section, list.List);
                if (paths.Contains(path))
                    return;
                Change(() =>
                {
                    paths.Add(path);
                    if (list.Section == "assets")
                        LunaPluginBridge.PathList("assets", list.List == "includes" ? "excludes" : "includes").Remove(path);
                });
            };
        }

        static void Change(Action edit)
        {
            edit();
            LunaPluginBridge.SaveConfig();
        }

        // ---- Settings tabs ----

        void DrawRules(Kind kind)
        {
            string noun = Tabs[tab].Substring(0, Tabs[tab].Length - 1);
            if (defaultRule == null)
                Guard(() => defaultRule = LunaAssetRules.GetDefault(kind));
            if (defaultRule == null)
                return;

            AssistantUI.SubHeader($"Default {noun} settings", DefaultHint(kind));
            DrawForm(defaultRule, $"Default {noun.ToLowerInvariant()} settings applied to luna.json.");
            EditorGUILayout.Space(14);

            AssistantUI.SubHeader($"Per-{noun.ToLowerInvariant()} settings",
                $"Settings for single {noun.ToLowerInvariant()}s, e.g. a logo that needs a larger size. A folder applies to every asset in it.\n" +
                "Drop assets or folders on the list, or pick one below.");
            List<string> keys = null;
            Guard(() => keys = LunaAssetRules.OverrideKeys(kind));
            if (keys == null)
                return;
            if (selectedKey != null && !keys.Contains(selectedKey))
            {
                selectedKey = null;
                selectedRule = null;
            }

            using (var box = new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (keys.Count == 0)
                    GUILayout.Label($"Every {noun.ToLowerInvariant()} uses the default settings. Drop assets or folders here.", AssistantUI.Caption);
                for (int i = 0; i < keys.Count; i++)
                    DrawOverrideRow(kind, keys[i], i);
                HandleDrop(box.rect, p => Accepts(kind, p), p => AddOverride(kind, p));
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                UnityEngine.Object picked = EditorGUILayout.ObjectField(null, typeof(UnityEngine.Object), false, GUILayout.Width(170));
                if (picked != null)
                    AddOverride(kind, AssetDatabase.GetAssetPath(picked));
                if (GUILayout.Button(new GUIContent("Add selected", $"Give the {noun.ToLowerInvariant()}s/folders selected in the Project window their own settings"),
                                     EditorStyles.miniButton, GUILayout.Width(90)))
                    foreach (string path in SelectedPaths())
                        AddOverride(kind, path);
                if (note != null)
                    GUILayout.Label(note, AssistantUI.Caption);
                GUILayout.FlexibleSpace();
            }

            if (selectedKey == null)
                return;
            if (selectedRule == null)
                Guard(() => selectedRule = LunaAssetRules.Get(kind, selectedKey));
            if (selectedRule == null)
                return;
            EditorGUILayout.Space(10);
            using (new EditorGUILayout.HorizontalScope())
            {
                AssistantUI.SubHeader($"{Path.GetFileName(selectedKey.TrimEnd('/'))} {noun} settings");
                if (Exists(selectedKey) && GUILayout.Button("Select", EditorStyles.miniButton, GUILayout.Width(50)))
                    Selection.activeObject = AssetDatabase.LoadMainAssetAtPath(selectedKey);
            }
            GUILayout.Label(selectedKey, AssistantUI.Caption);
            DrawForm(selectedRule, null);
        }

        void DrawOverrideRow(Kind kind, string key, int index)
        {
            bool selected = key == selectedKey;
            using (var row = AssistantUI.Row(index, selected))
            {
                bool exists = Exists(key);
                GUIContent icon = exists ? new GUIContent(AssetDatabase.GetCachedIcon(key))
                    : key.StartsWith("Assets/", StringComparison.Ordinal) ? AssistantUI.Icon("console.warnicon.sml", "Not found in the project")
                    : AssistantUI.Icon("d_UnityLogo", "Built-in asset");
                GUILayout.Label(icon, GUILayout.Width(18), GUILayout.Height(18));
                string name = Path.GetFileName(key.TrimEnd('/')) + (AssetDatabase.IsValidFolder(key) ? "/" : string.Empty);
                bool dirty = selected && selectedRule != null && SafeDirty(selectedRule);
                GUILayout.Label(new GUIContent(name + (dirty ? " *" : string.Empty), key), selected ? AssistantUI.SelectedRowLabel : AssistantUI.RowLabel,
                                GUILayout.MinWidth(120), GUILayout.MaxWidth(260));
                GUILayout.Label(new GUIContent(Path.GetDirectoryName(key)?.Replace('\\', '/'), key), AssistantUI.Caption, GUILayout.MinWidth(40));
                string summary = null;
                Guard(() => summary = LunaAssetRules.OverrideSummary(kind, key));
                GUILayout.Label(summary, AssistantUI.Caption, GUILayout.ExpandWidth(false));
                if (GUILayout.Button(new GUIContent("×", "Remove: back to the folder or default settings"), EditorStyles.miniButton, GUILayout.Width(22)))
                    deferred += () =>
                    {
                        LunaAssetRules.RemoveOverride(kind, key);
                        if (key == selectedKey)
                        {
                            selectedKey = null;
                            selectedRule = null;
                        }
                    };
                if (Event.current.type == EventType.MouseDown && row.rect.Contains(Event.current.mousePosition))
                {
                    if (!selected && ConfirmLeave(selectedRule))
                    {
                        selectedKey = key;
                        selectedRule = null;
                        GUI.FocusControl(null);
                    }
                    if (exists && Event.current.clickCount == 2)
                        EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(key));
                    Event.current.Use();
                }
            }
        }

        void AddOverride(Kind kind, string path)
        {
            if (string.IsNullOrEmpty(path))
                return;
            if (!Accepts(kind, path))
            {
                note = $"{Path.GetFileName(path)} is not a {Tabs[tab].Substring(0, Tabs[tab].Length - 1).ToLowerInvariant()} or folder.";
                return;
            }
            note = null;
            deferred += () =>
            {
                if (!ConfirmLeave(selectedRule))
                    return;
                LunaAssetRules.AddOverride(kind, path);
                selectedKey = path;
                selectedRule = null;
            };
        }

        // Settings form with Revert / Apply, like the Build size details.
        void DrawForm(LunaAssetRules.Rule rule, string appliedLog)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                Guard(() => LunaAssetRules.Draw(rule));
            bool dirty = SafeDirty(rule);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(dirty ? "Changed, not applied" : string.Empty, AssistantUI.Caption);
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(!dirty))
                {
                    if (GUILayout.Button(new GUIContent("Revert", "Drop the changes"), GUILayout.Width(70)))
                    {
                        LunaAssetRules.Revert(rule);
                        GUI.FocusControl(null);
                    }
                    if (AssistantUI.PrimaryButton("Apply", 22))
                    {
                        Guard(() => LunaAssetRules.Apply(rule));
                        GUI.FocusControl(null);
                        Debug.Log("[LunaBuildAssistant] " + (appliedLog ?? $"Luna settings applied to {rule.AssetPath}."));
                    }
                }
            }
        }

        // ---- Helpers ----

        // Asks before an unapplied draft is lost; false keeps the current state.
        public bool ConfirmLeave() => ConfirmLeave(defaultRule) && ConfirmLeave(selectedRule);

        bool ConfirmLeave(LunaAssetRules.Rule rule)
        {
            if (rule == null || !SafeDirty(rule))
                return true;
            string what = rule.IsDefault ? "the default settings" : Path.GetFileName(rule.AssetPath);
            int choice = EditorUtility.DisplayDialogComplex("Unapplied Luna settings", $"Apply the changed Luna settings of {what}?", "Apply", "Cancel", "Discard");
            if (choice == 1)
                return false;
            if (choice == 0)
                Guard(() => LunaAssetRules.Apply(rule));
            else
                LunaAssetRules.Revert(rule);
            return true;
        }

        static string DefaultHint(Kind kind)
        {
            switch (kind)
            {
                case Kind.Texture: return "Textures often take most of the build size. Used for every texture without its own settings.";
                case Kind.Sound: return "Sounds are encoded as MP3. Used for every sound without its own settings.";
                case Kind.Font: return "Fonts are baked into a glyph atlas texture; atlases are square, 1024 × 1024 by default.";
                case Kind.Mesh: return "Vertex data is exported as floats to stay as precise as the original art.";
                default: return "Animations are mostly simple curves; half precision halves their size.";
            }
        }

        static bool Accepts(Kind kind, string path)
        {
            if (AssetDatabase.IsValidFolder(path))
                return true;
            UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath(path);
            switch (kind)
            {
                case Kind.Texture: return assets.Any(a => a is Texture2D);
                case Kind.Sound: return assets.Any(a => a is AudioClip);
                case Kind.Font: return assets.Any(a => a is Font);
                case Kind.Mesh: return assets.Any(a => a is Mesh);
                default: return assets.Any(a => a is AnimationClip);
            }
        }

        static void HandleDrop(Rect area, Func<string, bool> accepts, Action<string> add)
        {
            Event e = Event.current;
            if ((e.type != EventType.DragUpdated && e.type != EventType.DragPerform) || !area.Contains(e.mousePosition))
                return;
            string[] paths = DragAndDrop.paths.Where(p => !string.IsNullOrEmpty(p) && accepts(p)).ToArray();
            DragAndDrop.visualMode = paths.Length > 0 ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
            if (e.type == EventType.DragPerform && paths.Length > 0)
            {
                DragAndDrop.AcceptDrag();
                foreach (string path in paths)
                    add(path);
            }
            e.Use();
        }

        static IEnumerable<string> SelectedPaths() =>
            Selection.assetGUIDs.Select(AssetDatabase.GUIDToAssetPath).Where(p => !string.IsNullOrEmpty(p));

        static bool IsPattern(string path) => path.IndexOfAny(new[] { '*', '?' }) >= 0;

        static bool Exists(string path) =>
            !IsPattern(path) && (AssetDatabase.IsValidFolder(path.TrimEnd('/')) || !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path)) && File.Exists(path));

        bool SafeDirty(LunaAssetRules.Rule rule)
        {
            try { return rule.IsDirty; }
            catch (Exception) { return false; }
        }

        void Guard(Action action)
        {
            try
            {
                action();
            }
            catch (ExitGUIException)
            {
                throw;
            }
            catch (Exception e)
            {
                error = "Luna settings could not be read from the plugin: " + e.Message;
                Debug.LogException(e);
            }
        }
    }
}
