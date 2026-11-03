using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Supercent.Util.Editor
{
    // Unpacked prefabs make a much smaller playable but an uneditable scene, so the team edits the
    // normal scene and builds an "<Scene>_NoPrefab" copy with every prefab instance unpacked.
    // The copy is regenerated from the source before every build and Luna's scene list points at it.
    [InitializeOnLoad]
    public static class NoPrefabScenes
    {
        public const string Suffix = "_NoPrefab";
        const string EnabledPref = "SupercentVN.LunaBuildAssistant.NoPrefab.";

        static bool generating;

        static NoPrefabScenes()
        {
            EditorSceneManager.sceneOpened += (scene, mode) => WarnIfGenerated(scene, "opened");
            EditorSceneManager.sceneSaved += scene => WarnIfGenerated(scene, "saved");
        }

        static string EnabledKey => EnabledPref + Directory.GetParent(Application.dataPath).FullName;

        public static bool Enabled
        {
            get => EditorPrefs.GetBool(EnabledKey, false);
            set => EditorPrefs.SetBool(EnabledKey, value);
        }

        public static bool IsGenerated(string scenePath) =>
            Path.GetFileNameWithoutExtension(scenePath ?? string.Empty).EndsWith(Suffix, StringComparison.Ordinal);

        public static string CopyPath(string sourcePath) =>
            IsGenerated(sourcePath) ? sourcePath : Path.ChangeExtension(sourcePath, null) + Suffix + ".unity";

        public static string SourcePath(string scenePath)
        {
            if (!IsGenerated(scenePath))
                return scenePath;
            string withoutExtension = Path.ChangeExtension(scenePath, null);
            return withoutExtension.Substring(0, withoutExtension.Length - Suffix.Length) + ".unity";
        }

        // Regenerates the copies of Luna's scenes and points Luna at them (or back at the sources when disabled).
        // False when the user cancelled saving a modified source scene.
        public static bool Prepare(out string summary)
        {
            List<string> sources = LunaPluginBridge.LunaScenes.Select(SourcePath).Distinct().ToList();
            if (sources.Count == 0)
            {
                summary = "No scene is selected for Luna builds.";
                return true;
            }
            if (!Enabled)
            {
                LunaPluginBridge.SetLunaScenes(sources, sources.Select(CopyPath).Where(p => File.Exists(p)).ToList());
                summary = null;
                return true;
            }

            Scene[] modified = sources.Select(SceneManager.GetSceneByPath).Where(s => s.IsValid() && s.isDirty).ToArray();
            if (modified.Length > 0 && !EditorSceneManager.SaveModifiedScenesIfUserWantsTo(modified))
            {
                summary = null;
                return false;
            }
            if (modified.Any(s => s.isDirty))
            {
                summary = null;
                return false;                               // "Don't Save": building stale scenes would surprise.
            }

            var lines = new List<string>();
            foreach (string source in sources)
                lines.Add(Generate(source));
            List<string> copies = sources.Select(CopyPath).ToList();
            AddToBuildSettings(sources);
            LunaPluginBridge.SetLunaScenes(copies, sources);
            summary = string.Join("\n", lines);
            return true;
        }

        // Copies the scene file (keeping the copy's GUID), then unpacks every prefab instance in the copy.
        public static string Generate(string sourcePath)
        {
            string copyPath = CopyPath(sourcePath);
            Scene loaded = SceneManager.GetSceneByPath(copyPath);
            if (loaded.IsValid() && loaded.isLoaded)
                throw new InvalidOperationException($"Close {Path.GetFileName(copyPath)} before building: it is regenerated from {Path.GetFileName(sourcePath)}.");

            string root = Directory.GetParent(Application.dataPath).FullName;
            File.Copy(Path.Combine(root, sourcePath), Path.Combine(root, copyPath), true);
            AssetDatabase.ImportAsset(copyPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

            generating = true;
            Scene scene = default;
            try
            {
                scene = EditorSceneManager.OpenScene(copyPath, OpenSceneMode.Additive);
                List<GameObject> instances = scene.GetRootGameObjects()
                    .SelectMany(r => r.GetComponentsInChildren<Transform>(true))
                    .Select(t => t.gameObject)
                    .Where(PrefabUtility.IsOutermostPrefabInstanceRoot)
                    .ToList();
                int unpacked = 0, missing = 0;
                foreach (GameObject go in instances)
                {
                    if (PrefabUtility.IsPrefabAssetMissing(go))
                    {
                        missing++;                          // Cannot be unpacked; Luna exports it as it is.
                        continue;
                    }
                    PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                    unpacked++;
                }
                EditorSceneManager.SaveScene(scene);
                Debug.Log($"[LunaBuildAssistant] {copyPath}: unpacked {unpacked} prefab instance(s)" +
                          (missing > 0 ? $", {missing} with a missing prefab asset" : string.Empty));
                return $"{Path.GetFileName(copyPath)}: {unpacked} prefab(s) unpacked" + (missing > 0 ? $", {missing} missing" : string.Empty);
            }
            finally
            {
                if (scene.IsValid())
                    EditorSceneManager.CloseScene(scene, true);
                generating = false;
            }
        }

        // Luna's scene picker only lists scenes in the Build Settings.
        static void AddToBuildSettings(IEnumerable<string> sources)
        {
            List<EditorBuildSettingsScene> scenes = EditorBuildSettings.scenes.ToList();
            bool changed = false;
            foreach (string source in sources)
            {
                string copy = CopyPath(source);
                if (scenes.Any(s => s.path == copy))
                    continue;
                int at = scenes.FindIndex(s => s.path == source);
                scenes.Insert(at < 0 ? scenes.Count : at + 1, new EditorBuildSettingsScene(copy, true));
                changed = true;
            }
            if (changed)
                EditorBuildSettings.scenes = scenes.ToArray();
        }

        static void WarnIfGenerated(Scene scene, string action)
        {
            if (generating || !IsGenerated(scene.path))
                return;
            string message = $"{Path.GetFileName(scene.path)} is regenerated from {Path.GetFileName(SourcePath(scene.path))} before every Luna build. " +
                             "Edit the source scene; changes here will be overwritten.";
            Debug.LogWarning("[LunaBuildAssistant] " + message + $" (scene {action})");
            if (SceneView.lastActiveSceneView != null)
                SceneView.lastActiveSceneView.ShowNotification(new GUIContent("Generated scene: edit " + Path.GetFileName(SourcePath(scene.path))), 4);
        }
    }
}
