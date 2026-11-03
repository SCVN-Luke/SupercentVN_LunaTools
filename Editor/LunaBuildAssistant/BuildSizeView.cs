using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Supercent.Util.Editor
{
    // Build Assistant "Build size" page: the plugin's Size breakdown for one network (per-asset size in the
    // final playable), change since the build before and duplicated content. Selecting an asset opens
    // AssetDetailsPanel beside the list: preview, its Luna export settings and (textures) format comparison.
    public sealed class BuildSizeView
    {
        const string AllKinds = "All kinds";
        const string Rest = "rest";
        const int PageSize = 200;
        const int ColoredKinds = 6;
        const float DetailsWidth = 340f;
        const string Help =
            "Same numbers as the plugin's Size breakdown: each asset's size inside the final playable for the chosen network.\n" +
            "Estimated automatically after every Build Develop; Estimate re-runs it on the last build (e.g. after switching network).";

        BuildSizeReport.Snapshot latest, previous;
        Dictionary<string, long> previousBytes = new Dictionary<string, long>();
        List<BuildSizeReport.Duplicate> duplicates = new List<BuildSizeReport.Duplicate>();
        List<KeyValuePair<string, long>> kindTotals = new List<KeyValuePair<string, long>>();
        KeyValuePair<string, string>[] networks;
        string[] kinds = { AllKinds };
        int kindIndex, tab;
        string search = string.Empty;
        bool changedOnly, estimating, loaded;
        readonly AssetDetailsPanel details = new AssetDetailsPanel();
        public float AvailableWidth;            // Page width, set by the window: IMGUI does not stretch the list column itself.
        string error;
        Vector2 scroll;

        public Action Repaint;
        public bool Estimating => estimating;

        public void Reload()
        {
            loaded = true;
            latest = BuildSizeReport.Latest;
            previous = BuildSizeReport.Previous;
            previousBytes = previous?.entries.GroupBy(Key).ToDictionary(g => g.Key, g => g.First().bytes) ?? new Dictionary<string, long>();
            duplicates = latest == null ? new List<BuildSizeReport.Duplicate>() : BuildSizeReport.Duplicates(latest);
            kindTotals = latest?.entries.GroupBy(e => e.kind).Select(g => new KeyValuePair<string, long>(g.Key, g.Sum(e => e.bytes)))
                             .OrderByDescending(k => k.Value).ToList() ?? new List<KeyValuePair<string, long>>();
            kinds = new[] { AllKinds }.Concat(kindTotals.Select(k => k.Key)).ToArray();
            kindIndex = Mathf.Clamp(kindIndex, 0, kinds.Length - 1);
            BuildSizeReport.Entry selected = details.Entry == null ? null
                : latest?.entries.FirstOrDefault(e => e.kind == details.Entry.kind && e.Label == details.Entry.Label);
            if (selected != null)
                details.UpdateEntry(selected);      // Same asset, numbers from the new estimate; the draft stays.
            else
                details.Clear();
        }

        public void Close() => details.Clear();

        // Background texture encodes of the details panel; true = repaint.
        public bool Update() => details.Update();

        // Runs the plugin's size estimate for the selected network; used by the page and after Build Develop.
        public async System.Threading.Tasks.Task Estimate()
        {
            if (estimating)
                return;
            estimating = true;
            error = null;
            Repaint?.Invoke();
            try
            {
                await BuildSizeReport.Estimate();
            }
            catch (Exception e)
            {
                error = e.Message;
                Debug.LogException(e);
            }
            finally
            {
                estimating = false;
                Reload();
                Repaint?.Invoke();
            }
        }

        public void Draw()
        {
            if (!loaded)
                Reload();
            DrawHeader();
            if (!string.IsNullOrEmpty(error))
                EditorGUILayout.HelpBox(error, MessageType.Error);
            if (latest == null)
            {
                EditorGUILayout.HelpBox(estimating ? "Estimating…" : "No size estimate for this network yet. Run Build Develop, or press Estimate.", MessageType.Info);
                return;
            }

            DrawTiles();
            DrawKindBar();
            EditorGUILayout.Space(8);
            tab = AssistantUI.Segmented(tab, "Assets", $"Duplicates ({duplicates.Count})");
            if (tab == 0)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    // From the window width, never from the content: a column sized from what it drew can only grow.
                    float listWidth = details.Entry == null ? AvailableWidth : Mathf.Max(320f, AvailableWidth - DetailsWidth - 8f);
                    using (new EditorGUILayout.VerticalScope(GUILayout.Width(listWidth)))     // Takes what details leave.
                    {
                        DrawAssetToolbar();
                        scroll = EditorGUILayout.BeginScrollView(scroll);
                        DrawEntries();
                        EditorGUILayout.EndScrollView();
                    }
                    if (details.Entry != null)
                    {
                        GUILayout.Space(8);
                        using (new EditorGUILayout.VerticalScope(GUILayout.Width(DetailsWidth)))   // Pinned to the right edge.
                        {
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                GUILayout.FlexibleSpace();
                                if (GUILayout.Button(AssistantUI.Icon("winbtn_win_close", "Close details"), EditorStyles.label, GUILayout.Width(18))
                                    && details.ConfirmLeave())
                                {
                                    details.Clear();
                                    GUIUtility.ExitGUI();
                                }
                            }
                            details.Draw(previous);
                        }
                    }
                }
            }
            else
            {
                scroll = EditorGUILayout.BeginScrollView(scroll);
                DrawDuplicates();
                EditorGUILayout.EndScrollView();
            }
        }

        void DrawHeader()
        {
            networks ??= LunaPluginBridge.SizeNetworks;
            using (new EditorGUILayout.HorizontalScope())
            {
                AssistantUI.Header("Build size", Help);
                int current = Array.FindIndex(networks, n => n.Key == BuildSizeReport.Network);
                int chosen = EditorGUILayout.Popup(Math.Max(0, current), networks.Select(n => n.Value).ToArray(), GUILayout.Width(120), GUILayout.Height(22));
                if (chosen != current && chosen >= 0)
                {
                    BuildSizeReport.Network = networks[chosen].Key;
                    Reload();
                }
                using (new EditorGUI.DisabledScope(estimating || !LunaPluginBridge.CanEstimateSize || !BuildSizeReport.HasBuildOutput))
                    if (GUILayout.Button(new GUIContent(estimating ? " Estimating…" : " Estimate", EditorGUIUtility.IconContent("Refresh").image,
                                         "Re-run the size breakdown for this network on the last build"), GUILayout.Height(22), GUILayout.Width(110)))
                        _ = Estimate();
            }
            if (!LunaPluginBridge.CanEstimateSize)
                GUILayout.Label("This plugin version has no size breakdown estimate.", AssistantUI.Caption);
        }

        void DrawTiles()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                long delta = previous == null ? 0 : latest.totalBytes - previous.totalBytes;
                string network = networks.FirstOrDefault(n => n.Key == latest.network).Value ?? latest.network;
                AssistantUI.Tile("Total · " + network, BuildSizeReport.FormatBytes(latest.totalBytes),
                                 delta == 0 ? null : BuildSizeReport.FormatDelta(delta), AssistantUI.DeltaColor(delta), "Playable size for " + network);
                foreach (KeyValuePair<string, long> kind in kindTotals.Take(2))
                    AssistantUI.Tile(kind.Key, BuildSizeReport.FormatBytes(kind.Value), $"{100.0 * kind.Value / Math.Max(1, latest.totalBytes):0}%", null);
                AssistantUI.Tile("Estimated", AssistantUI.Ago(latest.BuiltLocal), previous == null ? null : "vs " + previous.BuiltLocal.ToString("MM-dd HH:mm"),
                                 null, latest.BuiltLocal.ToString("yyyy-MM-dd HH:mm"));
            }
        }

        // One stacked bar of the asset kinds, with a legend; click a legend entry to filter the list.
        void DrawKindBar()
        {
            long total = kindTotals.Sum(k => k.Value);
            if (total == 0)
                return;
            int colored = AvailableWidth > 860f ? ColoredKinds : 4;            // The legend does not wrap: fewer entries when narrow.
            List<KeyValuePair<string, long>> shown = kindTotals.Take(colored).ToList();
            long rest = total - shown.Sum(k => k.Value);
            if (rest > 0)
                shown.Add(new KeyValuePair<string, long>(Rest, rest));

            Rect bar = GUILayoutUtility.GetRect(0, 14, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
            {
                float x = bar.x;
                for (int i = 0; i < shown.Count; i++)
                {
                    float w = bar.width * shown[i].Value / total;
                    EditorGUI.DrawRect(new Rect(x, bar.y, Mathf.Max(1f, w - 1f), bar.height), AssistantUI.KindColor(i));
                    x += w;
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                for (int i = 0; i < shown.Count; i++)
                {
                    Rect swatch = GUILayoutUtility.GetRect(10, 10, GUILayout.Width(10), GUILayout.Height(16));
                    EditorGUI.DrawRect(new Rect(swatch.x, swatch.y + 4, 8, 8), AssistantUI.KindColor(i));
                    string label = $"{shown[i].Key} {BuildSizeReport.FormatBytes(shown[i].Value)}";
                    if (GUILayout.Button(new GUIContent(label, "Show only " + shown[i].Key), AssistantUI.Caption) && shown[i].Key != Rest)
                    {
                        kindIndex = Array.IndexOf(kinds, shown[i].Key);
                        tab = 0;
                    }
                }
                GUILayout.FlexibleSpace();
            }
        }

        void DrawAssetToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                kindIndex = EditorGUILayout.Popup(kindIndex, kinds, EditorStyles.toolbarPopup, GUILayout.Width(130));
                changedOnly = GUILayout.Toggle(changedOnly, new GUIContent("Changed", "Only assets whose size changed since the previous estimate"), EditorStyles.toolbarButton, GUILayout.Width(64));
                GUILayout.FlexibleSpace();
                search = GUILayout.TextField(search, EditorStyles.toolbarSearchField, GUILayout.MinWidth(120), GUILayout.MaxWidth(240));
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Size", AssistantUI.Caption, GUILayout.Width(68));
                GUILayout.Label("Change", AssistantUI.Caption, GUILayout.Width(56));
                GUILayout.Label("Kind", AssistantUI.Caption, GUILayout.Width(80));
                GUILayout.Label("Asset", AssistantUI.Caption);
            }
        }

        void DrawEntries()
        {
            string kind = kinds[kindIndex];
            IEnumerable<BuildSizeReport.Entry> rows = latest.entries
                .Where(e => kind == AllKinds || e.kind == kind)
                .Where(e => search.Length == 0 || e.Label.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
                            || (e.title ?? string.Empty).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
            if (changedOnly)
                rows = rows.Where(e => !previousBytes.TryGetValue(Key(e), out long before) || before != e.bytes);

            int index = 0;
            foreach (BuildSizeReport.Entry e in rows)
            {
                if (index >= PageSize)
                {
                    GUILayout.Label("Narrow the list with the kind filter or search to see more.", AssistantUI.Caption);
                    break;
                }
                bool selected = details.Entry != null && details.Entry.kind == e.kind && details.Entry.Label == e.Label;
                using (AssistantUI.Row(index++, selected))
                {
                    GUILayout.Label(BuildSizeReport.FormatBytes(e.bytes), GUILayout.Width(68));
                    DrawDelta(e);
                    GUILayout.Label(e.kind, AssistantUI.Caption, GUILayout.Width(80));
                    if (GUILayout.Button(new GUIContent(e.title, e.Label + "\n\nClick for preview and Luna settings"),
                                         selected ? AssistantUI.SelectedRowLabel : AssistantUI.RowLabel, GUILayout.MinWidth(80), GUILayout.ExpandWidth(true)))
                    {
                        Ping(e.path);
                        details.Select(e);
                        GUIUtility.ExitGUI();               // The details column appears: start a fresh layout pass.
                    }
                }
            }
        }

        void DrawDelta(BuildSizeReport.Entry e)
        {
            string text = string.Empty;
            long delta = 0;
            if (previous != null)
            {
                if (!previousBytes.TryGetValue(Key(e), out long before))
                    text = "new";
                else if (before != e.bytes)
                {
                    delta = e.bytes - before;
                    text = BuildSizeReport.FormatDelta(delta);
                }
            }
            Color color = GUI.contentColor;
            if (delta != 0 || text == "new")
                GUI.contentColor = text == "new" ? AssistantUI.Warn : AssistantUI.DeltaColor(delta);
            GUILayout.Label(text, EditorStyles.miniBoldLabel, GUILayout.Width(56));
            GUI.contentColor = color;
        }

        void DrawDuplicates()
        {
            if (duplicates.Count == 0)
            {
                GUILayout.Label("No asset is shipped twice with the same content.", AssistantUI.Caption);
                return;
            }
            GUILayout.Label($"Same content shipped under different names: merging saves {BuildSizeReport.FormatBytes(duplicates.Sum(d => d.Wasted))}.", AssistantUI.Caption);
            EditorGUILayout.Space(4);
            foreach (BuildSizeReport.Duplicate d in duplicates)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label($"{d.Kind} · {d.Paths.Length} × {BuildSizeReport.FormatBytes(d.Bytes)}", EditorStyles.boldLabel);
                        GUILayout.FlexibleSpace();
                        GUILayout.Label("−" + BuildSizeReport.FormatBytes(d.Wasted), EditorStyles.miniBoldLabel);
                    }
                    foreach (string path in d.Paths)
                        if (GUILayout.Button(new GUIContent(path.StartsWith("Assets/", StringComparison.Ordinal) ? path.Substring(7) : path, path), EditorStyles.linkLabel))
                            Ping(path);
                }
            }
        }

        static string Key(BuildSizeReport.Entry e) => e.kind + "|" + e.Label;

        static void Ping(string path)
        {
            UnityEngine.Object asset = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadMainAssetAtPath(path);
            if (asset != null)
                EditorGUIUtility.PingObject(asset);
        }
    }
}
