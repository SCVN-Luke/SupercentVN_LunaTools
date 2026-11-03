using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Supercent.Util.Editor
{
    // Which scenes Luna builds (luna.json unity.scenes) and which one starts (unity.startupScene, an index into scenes).
    // Usually one scene: a combo box that also makes it the start-up scene. A tick switches to the plugin's
    // layout (ticked list + start-up combo) for playables that change scene.
    // Only source scenes are listed: NoPrefabScenes swaps in the generated copies at build time.
    public sealed class ScenePickerPanel
    {
        const string SeveralScenesPref = "SupercentVN.LunaBuildAssistant.SeveralScenes.";

        List<string> candidates;            // Build Settings scenes, in that order: the plugin's scene list.
        string[] candidateLabels;
        string startup;

        public Action<Exception> OnError;
        public Action Repaint;
        public List<string> Selected { get; private set; }

        static string SeveralScenesKey => SeveralScenesPref + Directory.GetParent(Application.dataPath).FullName;

        // More than one scene selected (e.g. in the plugin window) always shows the list: a combo box would hide them.
        bool SeveralScenes
        {
            get => Selected.Count > 1 || EditorPrefs.GetBool(SeveralScenesKey, false);
            set => EditorPrefs.SetBool(SeveralScenesKey, value);
        }

        // Reads luna.json, so called on a timer and after a change, never from OnGUI.
        public void Refresh()
        {
            try
            {
                Selected = LunaPluginBridge.LunaScenes.Select(NoPrefabScenes.SourcePath).Distinct().ToList();
                startup = NoPrefabScenes.SourcePath(LunaPluginBridge.LunaStartupScene);
                candidates = EditorBuildSettings.scenes.Select(s => NoPrefabScenes.SourcePath(s.path))
                    .Where(File.Exists).Union(Selected).ToList();
                candidateLabels = candidates.Select(Label).ToArray();
            }
            catch (Exception e)
            {
                OnError?.Invoke(e);
            }
        }

        public void Draw()
        {
            AssistantUI.SubHeader("Scenes", "Saved to luna.json: Build & upload uses the same scenes.\n\nThe list comes from File > Build Settings.");
            if (candidates == null)
            {
                GUILayout.Label("…", AssistantUI.Caption);
                return;
            }
            if (candidates.Count == 0)
            {
                EditorGUILayout.HelpBox("No scene in Build Settings. Add the playable's scene there first.", MessageType.Warning);
                if (GUILayout.Button("Open Build Settings", GUILayout.Width(160)))
                    BuildPlayerWindow.ShowBuildPlayerWindow();
                return;
            }

            bool several = SeveralScenes;
            if (several)
                DrawList();
            else
                DrawCombo();
            if (Selected.Count == 0)
                EditorGUILayout.HelpBox("No scene is selected: Luna cannot build.", MessageType.Warning);

            bool wanted = EditorGUILayout.ToggleLeft(new GUIContent("Build several scenes",
                "For playables that change scene: tick the scenes to build and choose the start-up scene.\n" +
                "Unticking keeps only the start-up scene."), several);
            if (wanted != several)
                Change(() =>
                {
                    SeveralScenes = wanted;
                    if (!wanted && Selected.Count > 1)
                        Save(new List<string> { StartupOrFirst(Selected) }, startup);
                });
        }

        void DrawCombo()
        {
            int current = candidates.IndexOf(startup);
            int chosen = EditorGUILayout.Popup(new GUIContent("Scene", "The scene to build; it is also the start-up scene."), current, candidateLabels);
            if (chosen != current)
                Change(() => Save(new List<string> { candidates[chosen] }, candidates[chosen]));
        }

        void DrawList()
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                string scene = candidates[i];
                bool ticked = Selected.Contains(scene);
                using (AssistantUI.Row(i))
                using (new EditorGUI.DisabledScope(ticked && Selected.Count == 1))      // Luna cannot build without a scene.
                {
                    bool now = EditorGUILayout.ToggleLeft(new GUIContent(candidateLabels[i], scene), ticked);
                    if (now != ticked)
                        Change(() =>
                        {
                            List<string> scenes = candidates.Where(s => s == scene ? now : Selected.Contains(s)).ToList();
                            Save(scenes, StartupOrFirst(scenes));
                        });
                }
            }

            EditorGUILayout.Space(4);
            int current = Selected.IndexOf(startup);
            int chosen = EditorGUILayout.Popup(new GUIContent("Start-up scene", "The scene the playable opens with."), current, Selected.Select(Label).ToArray());
            if (chosen != current)
                Change(() => Save(Selected, Selected[chosen]));
        }

        string StartupOrFirst(List<string> scenes) => scenes.Contains(startup) ? startup : scenes[0];

        // Every other scene goes to disabledScenes: the plugin auto-ticks a Build Settings scene it finds in neither list.
        void Save(List<string> scenes, string startupScene)
        {
            List<string> others = candidates.SelectMany(s => new[] { s, NoPrefabScenes.CopyPath(s) })
                .Except(scenes).Where(File.Exists).ToList();
            LunaPluginBridge.SetLunaScenes(scenes, others, startupScene);
        }

        // Applied after this GUI event, so Layout and Repaint keep seeing the same controls.
        void Change(Action change)
        {
            EditorApplication.delayCall += () =>
            {
                try
                {
                    change();
                }
                catch (Exception e)
                {
                    OnError?.Invoke(e);
                }
                Refresh();
                Repaint?.Invoke();
            };
        }

        // File name; same-named scenes also show their folder (no '/': it would make a submenu in the popup).
        string Label(string scene)
        {
            string name = Path.GetFileNameWithoutExtension(scene);
            return candidates.Count(s => Path.GetFileNameWithoutExtension(s) == name) > 1
                ? name + "  (" + Path.GetDirectoryName(scene).Replace('\\', '/').Replace("/", " › ") + ")"
                : name;
        }
    }
}
