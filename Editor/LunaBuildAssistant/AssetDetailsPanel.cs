using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Supercent.Util.Editor
{
    // Right-hand panel of the Build size page for the selected asset: size facts, a preview, the asset's
    // Luna export settings as a draft (Apply writes them to luna.json, Revert drops them) and, for textures,
    // the format comparison. Texture drafts are encoded in the background with Luna's own tools
    // (LunaTextureEncoder) so the "New settings" preview and size show the result before applying.
    public sealed class AssetDetailsPanel
    {
        const float PreviewHeight = 220f;
        const double DraftEncodeDelay = 0.4;    // Seconds after the last edit.

        enum View { Source, Luna, Draft }

        BuildSizeReport.Entry entry;
        UnityEngine.Object asset;
        UnityEditor.Editor preview;
        Texture2D lunaOutput, draftImage;
        View view;
        LunaAssetRules.Rule rule;
        string ruleError;
        Vector2 scroll;

        // Texture work done off the main thread; images are loaded once a task finished (Update).
        string source;
        string draftKey;                        // Settings the draft image shows.
        double draftDueAt = -1;
        Task<LunaTextureEncoder.Encoded> draftTask;
        LunaTextureEncoder.Encoded draftResult;
        Task<List<LunaTextureEncoder.Encoded>> compareTask;
        List<LunaTextureEncoder.Encoded> variants;

        public BuildSizeReport.Entry Entry => entry;

        public void Select(BuildSizeReport.Entry selected)
        {
            if (!ConfirmLeave())
                return;
            Clear();
            entry = selected;
            asset = Load(selected);
            if (asset != null)
                preview = UnityEditor.Editor.CreateEditor(asset);
            // Textures and fonts (their glyph atlas) are exported as images: show what Luna ships.
            if ((selected.kind == "textures" || selected.kind == "fonts") && !string.IsNullOrEmpty(selected.lunaPath) && File.Exists(selected.lunaPath))
                lunaOutput = LoadImage(selected.lunaPath);
            if (selected.kind == "textures")
            {
                source = LunaTextureEncoder.SourceFor(selected);
                LunaTextureEncoder.Prepare();
            }
            LoadRule();
        }

        // New size numbers for the same asset (after an estimate): keeps the draft and preview.
        public void UpdateEntry(BuildSizeReport.Entry updated)
        {
            entry = updated;
            DestroyImage(ref lunaOutput);
            if (!string.IsNullOrEmpty(updated.lunaPath) && File.Exists(updated.lunaPath))
                lunaOutput = LoadImage(updated.lunaPath);
            if (updated.kind == "textures")
                source = LunaTextureEncoder.SourceFor(updated);
        }

        // Asks before an unapplied draft is lost; false keeps the current selection.
        public bool ConfirmLeave()
        {
            if (rule == null || !SafeDirty())
                return true;
            int choice = EditorUtility.DisplayDialogComplex("Unapplied Luna settings",
                $"Apply the changed Luna settings of {entry.title}?", "Apply", "Cancel", "Discard");
            if (choice == 1)
                return false;
            if (choice == 0)
                Guard(() => LunaAssetRules.Apply(rule));
            return true;
        }

        public void Clear()
        {
            if (preview != null)
            {
                // Around a domain reload Unity's ModelInspector/Editor OnDisable can throw on their own cleared state.
                try { UnityEngine.Object.DestroyImmediate(preview); }
                catch (Exception) { }
            }
            DestroyImage(ref lunaOutput);
            DestroyImage(ref draftImage);
            preview = null;
            entry = null;
            asset = null;
            rule = null;
            ruleError = null;
            source = null;
            draftKey = null;
            draftDueAt = -1;
            draftTask = null;
            draftResult = null;
            compareTask = null;
            variants = null;
            view = View.Source;
        }

        // Called from the window's OnInspectorUpdate: starts due draft encodes, picks up finished ones. true = repaint.
        public bool Update()
        {
            bool changed = false;
            if (draftDueAt >= 0 && EditorApplication.timeSinceStartup >= draftDueAt && draftTask == null)
            {
                draftDueAt = -1;
                StartDraftEncode();
                changed = true;
            }
            if (draftTask != null && draftTask.IsCompleted)
            {
                draftResult = draftTask.Result;
                draftTask = null;
                DestroyImage(ref draftImage);
                if (draftResult.Error == null)
                {
                    draftImage = LoadImage(draftResult.PreviewFile);
                    view = View.Draft;
                }
                changed = true;
            }
            if (compareTask != null && compareTask.IsCompleted)
            {
                variants = compareTask.Result;
                compareTask = null;
                changed = true;
            }
            return changed;
        }

        public void Draw(BuildSizeReport.Snapshot previous)
        {
            if (entry == null)
                return;
            scroll = EditorGUILayout.BeginScrollView(scroll);
            DrawHeader(previous);
            EditorGUILayout.Space(4);
            DrawPreview();
            EditorGUILayout.Space(8);
            DrawRule();
            if (IsTexture && source != null)
            {
                EditorGUILayout.Space(8);
                DrawCompare();
            }
            EditorGUILayout.EndScrollView();
        }

        bool IsTexture => entry.kind == "textures" && rule != null && rule.Kind == LunaAssetRules.Kind.Texture;

        // In-playable size per encoded byte for this asset (base64 and packing), from the size breakdown.
        double PlayableRatio => entry.rawBytes > 0 ? (double)entry.bytes / entry.rawBytes : 4.0 / 3.0;

        void DrawHeader(BuildSizeReport.Snapshot previous)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(new GUIContent(entry.title, entry.Label), AssistantUI.Title);
                GUILayout.FlexibleSpace();
                if (asset != null && GUILayout.Button(AssistantUI.Icon("d_Search Icon", "Show in the Project window"), EditorStyles.miniButton, GUILayout.Width(26)))
                    EditorGUIUtility.PingObject(asset);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                long before = previous?.entries.FirstOrDefault(e => e.kind == entry.kind && e.Label == entry.Label)?.bytes ?? entry.bytes;
                long delta = entry.bytes - before;
                AssistantUI.Tile("In playable", BuildSizeReport.FormatBytes(entry.bytes), delta == 0 ? null : BuildSizeReport.FormatDelta(delta),
                                 AssistantUI.DeltaColor(delta), "Size inside the playable for the chosen network");
                long? estimate = DraftEstimate();
                if (estimate.HasValue)
                {
                    long change = estimate.Value - entry.bytes;
                    AssistantUI.Tile("New settings", "≈ " + BuildSizeReport.FormatBytes(estimate.Value), change == 0 ? null : BuildSizeReport.FormatDelta(change),
                                     AssistantUI.DeltaColor(change), "Estimated size in the playable with the settings below (not applied yet)");
                }
                else
                    AssistantUI.Tile("Exported", entry.rawBytes > 0 ? BuildSizeReport.FormatBytes(entry.rawBytes) : "—", null, null,
                                     "Luna's exported file before packing (LunaTemp/stage2)");
            }
            string facts = Facts();
            if (facts != null)
                GUILayout.Label(facts, AssistantUI.Caption);
        }

        // Size the draft would have in the playable: encoded texture, or bitrate × length for sounds.
        long? DraftEstimate()
        {
            if (rule == null || !SafeDirty())
                return null;
            if (IsTexture && draftResult != null && draftResult.Error == null && draftKey == CurrentDraftKey())
                return (long)(draftResult.Bytes * PlayableRatio);
            if (rule.Kind == LunaAssetRules.Kind.Sound && asset is AudioClip clip)
            {
                int bitrate = LunaAssetRules.SoundBitrate(rule.Draft);
                return (long)(bitrate * 1000L / 8 * clip.length * PlayableRatio);
            }
            return null;
        }

        string Facts()
        {
            switch (asset)
            {
                case Texture2D _ when AssetImporter.GetAtPath(entry.path) is TextureImporter importer:
                    importer.GetSourceTextureWidthAndHeight(out int w, out int h);
                    return $"Source {w}×{h}" + (lunaOutput != null ? $" · shipped {lunaOutput.width}×{lunaOutput.height}" : string.Empty);
                case Mesh m:
                    return $"{m.vertexCount:N0} vertices · {m.triangles.Length / 3:N0} triangles";
                case AudioClip c:
                    return $"{c.length:0.00}s · {(c.channels == 1 ? "mono" : c.channels + " channels")} · {c.frequency} Hz";
                default:
                    return entry.kind;
            }
        }

        void DrawPreview()
        {
            bool unityPreview = preview != null && preview.HasPreviewGUI();
            if (!unityPreview && lunaOutput == null)
            {
                GUILayout.Label(asset == null ? "No preview: built-in or generated content." : "No preview for this asset.", AssistantUI.Caption);
                return;
            }
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (entry.kind == "textures")
                {
                    view = Toggle(view, View.Source, "Source", "The texture in Unity", unityPreview);
                    view = Toggle(view, View.Luna, "Luna output", "What Luna ships now", lunaOutput != null);
                    view = Toggle(view, View.Draft, draftTask != null || draftDueAt >= 0 ? "Encoding…" : "New settings",
                                  "The settings below encoded with Luna's tools (not applied yet)", draftImage != null);
                }
                GUILayout.FlexibleSpace();
                if (unityPreview && view == View.Source)
                    preview.OnPreviewSettings();
            }
            Rect rect = GUILayoutUtility.GetRect(0, PreviewHeight, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(rect, AssistantUI.Track);
            Texture2D image = view == View.Draft ? draftImage : view == View.Luna || !unityPreview ? lunaOutput : null;
            if (image != null)
            {
                EditorGUI.DrawTextureTransparent(rect, image, ScaleMode.ScaleToFit);
                GUI.Label(new Rect(rect.x + 4, rect.yMax - 18, rect.width - 8, 16), $"{image.width}×{image.height}", AssistantUI.Caption);
            }
            else if (unityPreview)
                preview.OnInteractivePreviewGUI(rect, GUIStyle.none);
        }

        static View Toggle(View current, View target, string label, string tooltip, bool enabled)
        {
            using (new EditorGUI.DisabledScope(!enabled))
                return GUILayout.Toggle(current == target, new GUIContent(label, tooltip), EditorStyles.toolbarButton) && enabled ? target : current;
        }

        void DrawRule()
        {
            LunaAssetRules.Kind? kind = LunaAssetRules.KindOf(entry.kind);
            if (kind == null || string.IsNullOrEmpty(entry.path))
                return;
            using (new EditorGUILayout.HorizontalScope())
            {
                AssistantUI.SubHeader("Luna settings", "The same per-asset settings as the plugin's Assets tab. Changes stay a draft until Apply; " +
                                                       "they take effect on the next Build Develop / upload.");
                if (rule != null && rule.IsOwn && GUILayout.Button(new GUIContent("Reset", "Remove this asset's own settings: back to the folder or default rule"),
                                                                   EditorStyles.miniButton, GUILayout.Width(50)))
                {
                    Guard(() => LunaAssetRules.Reset(rule));
                    LoadRule();
                    ScheduleDraftEncode();
                    GUIUtility.ExitGUI();
                }
            }
            if (ruleError != null)
            {
                EditorGUILayout.HelpBox(ruleError, MessageType.Error);
                return;
            }
            bool dirty = SafeDirty();
            GUILayout.Label(LunaAssetRules.Describe(rule) + (dirty ? " · changed, not applied" : string.Empty), AssistantUI.Caption);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                bool changed = false;
                Guard(() => changed = LunaAssetRules.Draw(rule));
                if (changed && IsTexture)
                    ScheduleDraftEncode();
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(!dirty))
                {
                    if (GUILayout.Button(new GUIContent("Revert", "Drop the changes"), GUILayout.Width(70)))
                    {
                        LunaAssetRules.Revert(rule);
                        view = lunaOutput != null && IsTexture ? View.Luna : View.Source;
                        GUI.FocusControl(null);
                    }
                    if (AssistantUI.PrimaryButton(rule.IsOwn ? "Apply" : "Apply to this asset", 22))
                    {
                        Guard(() => LunaAssetRules.Apply(rule));
                        GUI.FocusControl(null);
                        Debug.Log($"[LunaBuildAssistant] Luna settings applied to {entry.path}. Build Develop to see the new size.");
                    }
                }
            }
        }

        void DrawCompare()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                AssistantUI.SubHeader("Compare formats", "Encodes the texture with Luna's tools for each option; sizes are in the playable. " +
                                                         "Click one to put it into the settings above, then Apply.");
                using (new EditorGUI.DisabledScope(compareTask != null))
                    if (GUILayout.Button(compareTask != null ? "Encoding…" : variants == null ? "Compare" : "Again", EditorStyles.miniButton, GUILayout.Width(70)))
                        StartCompare();
            }
            if (variants == null)
                return;
            long max = variants.Where(v => v.Error == null).Select(v => v.Bytes).DefaultIfEmpty(1).Max();
            string current = CurrentDraftKey();
            foreach (LunaTextureEncoder.Encoded v in variants)
            {
                if (v.Error != null)
                {
                    GUILayout.Label($"{v.Label}: {v.Error}", AssistantUI.Caption);
                    continue;
                }
                bool shipped = v.Bytes == entry.rawBytes;
                bool chosen = v.Settings.Key == current;
                Color color = chosen ? AssistantUI.Accent : v.Bytes < entry.rawBytes ? AssistantUI.Good : AssistantUI.Track;
                string label = v.Label + (shipped ? "  · current" : string.Empty);
                AssistantUI.Bar(label, (float)v.Bytes / max, color, "≈ " + BuildSizeReport.FormatBytes((long)(v.Bytes * PlayableRatio)));
                Rect bar = GUILayoutUtility.GetLastRect();
                EditorGUIUtility.AddCursorRect(bar, MouseCursor.Link);
                if (GUI.Button(bar, new GUIContent(string.Empty, "Use these settings (then Apply)"), GUIStyle.none))
                    UseVariant(v);
                GUILayout.Space(2);
            }
            if (GUILayout.Button(new GUIContent("Show files", "Open the encoded files"), EditorStyles.miniButton, GUILayout.Width(80)))
                EditorUtility.RevealInFinder(variants.FirstOrDefault(v => v.Error == null)?.File ?? LunaTextureEncoder.FolderFor(entry.path));
        }

        void UseVariant(LunaTextureEncoder.Encoded v)
        {
            Guard(() => LunaAssetRules.SetTextureSettings(rule, v.Settings));
            draftKey = v.Settings.Key;
            draftResult = v;
            DestroyImage(ref draftImage);
            draftImage = LoadImage(v.PreviewFile);
            view = View.Draft;
            GUI.FocusControl(null);
        }

        void ScheduleDraftEncode()
        {
            if (source != null)
                draftDueAt = EditorApplication.timeSinceStartup + DraftEncodeDelay;
        }

        void StartDraftEncode()
        {
            if (rule == null || source == null)
                return;
            LunaTextureEncoder.Settings settings = LunaAssetRules.TextureSettings(rule.Draft);
            draftKey = settings.Key;
            string src = source, folder = LunaTextureEncoder.FolderFor(entry.path);
            draftTask = Task.Run(() => LunaTextureEncoder.Encode(src, settings, folder, "draft"));
        }

        void StartCompare()
        {
            if (rule == null || source == null)
                return;
            LunaTextureEncoder.Settings current = LunaAssetRules.TextureSettings(rule.Values);
            object labels = rule.Draft;
            string src = source, folder = LunaTextureEncoder.FolderFor(entry.path);
            List<LunaTextureEncoder.Settings> options = LunaTextureEncoder.Alternatives(current, LunaTextureEncoder.IsOpaque(src));
            var names = options.ToDictionary(o => o.Key, o => Describe(labels, o));
            compareTask = Task.Run(() => options.Select((o, i) =>
            {
                LunaTextureEncoder.Encoded e = LunaTextureEncoder.Encode(src, o, folder, "compare" + i);
                e.Label = names[o.Key];
                return e;
            }).ToList());
        }

        static string Describe(object values, LunaTextureEncoder.Settings s) =>
            LunaAssetRules.TextureLabel(values, "textureFormats", s.Format) +
            (s.IsPng ? " · " + LunaAssetRules.TextureLabel(values, "textureCompressions", s.Compression) : s.UsesQuality ? " q" + s.Quality : string.Empty);

        string CurrentDraftKey()
        {
            try { return rule == null || !IsTexture ? null : LunaAssetRules.TextureSettings(rule.Draft).Key; }
            catch (Exception) { return null; }
        }

        bool SafeDirty()
        {
            try { return rule != null && rule.IsDirty; }
            catch (Exception) { return false; }
        }

        void LoadRule()
        {
            LunaAssetRules.Kind? kind = LunaAssetRules.KindOf(entry.kind);
            rule = null;
            ruleError = null;
            if (kind != null && !string.IsNullOrEmpty(entry.path))
                Guard(() => rule = LunaAssetRules.Get(kind.Value, entry.path));
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
                ruleError = "These settings could not be read from the plugin: " + e.Message;
                Debug.LogException(e);
            }
        }

        static Texture2D LoadImage(string file)
        {
            if (string.IsNullOrEmpty(file) || !File.Exists(file))
                return null;
            var image = new Texture2D(2, 2) { hideFlags = HideFlags.HideAndDontSave };
            if (image.LoadImage(File.ReadAllBytes(file)))       // PNG/JPEG; WEBP drafts come with a decoded PNG.
                return image;
            UnityEngine.Object.DestroyImmediate(image);
            return null;
        }

        static void DestroyImage(ref Texture2D image)
        {
            if (image != null)
                UnityEngine.Object.DestroyImmediate(image);
            image = null;
        }

        // Meshes are sub-assets of their model file: pick the one named like the size breakdown entry.
        static UnityEngine.Object Load(BuildSizeReport.Entry e)
        {
            if (string.IsNullOrEmpty(e.path) || !e.path.StartsWith("Assets/", StringComparison.Ordinal))
                return null;
            if (e.kind == "meshes")
                return AssetDatabase.LoadAllAssetsAtPath(e.path).OfType<Mesh>().FirstOrDefault(m => m.name == e.title)
                       ?? AssetDatabase.LoadAllAssetsAtPath(e.path).OfType<Mesh>().FirstOrDefault()
                       ?? AssetDatabase.LoadMainAssetAtPath(e.path);
            if (e.kind == "animation-clips")
                return AssetDatabase.LoadAllAssetsAtPath(e.path).OfType<AnimationClip>().FirstOrDefault(c => c.name == e.title)
                       ?? AssetDatabase.LoadMainAssetAtPath(e.path);
            return AssetDatabase.LoadMainAssetAtPath(e.path);
        }
    }
}
