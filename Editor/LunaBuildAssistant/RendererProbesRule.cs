using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Supercent.Util.Editor
{
    // Luna recomputes a renderer's light probe and reflection probes every time its bounds change while
    // Light Probes is not Off (engine: synchronizeWithEntity -> Renderer.UpdateProbes). Stacked items, characters
    // and anything that moves pay for it every frame. When the playable has no Light Probe Group, Reflection Probe
    // or baked lighting, Off renders the same (ambient light and skybox reflection, in Unity too).
    public sealed class RendererProbesRule : ILunaLintFixable
    {
        static readonly Regex ProbeObjects = new Regex(@"^--- !u!(220|215) ", RegexOptions.Multiline);     // LightProbeGroup, ReflectionProbe
        static readonly Regex LightingData = new Regex(@"m_LightingDataAsset: \{fileID: [1-9]");

        public string Id => "LUNA005";
        public string Title => "Renderers recompute light probes every time they move";
        public string Fix => "Luna interpolates light probes and reflection probes for a renderer each time it moves while its " +
                             "Light Probes setting is not Off, even with no probe in the scene. This playable has none, so setting " +
                             "Light Probes and Reflection Probes to Off on Mesh / Skinned Mesh Renderers looks the same and removes " +
                             "that per-frame work.";
        public string FixLabel => "Turn probes off";
        public string FixConfirm => "Sets Light Probes and Reflection Probes to Off on the Mesh and Skinned Mesh Renderers of the listed " +
                                    "prefabs (renderers of nested models get an override) and of the open scenes listed (save them afterwards).\n\n" +
                                    "The playable has no light or reflection probe, so it renders the same.";

        public IEnumerable<LunaLint.Issue> Run()
        {
            List<string> scenes = BuildScenes();
            if (scenes.Count == 0)
                yield break;
            string[] shipped = AssetDatabase.GetDependencies(scenes.Concat(ResourcesPrefabs()).ToArray(), true);
            if (UsesProbes(shipped))
                yield break;

            string[] shared = SharedFolders();
            foreach (string path in shipped.Where(p => p.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) && Owned(p, shared)).OrderBy(p => p))
            {
                var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                int count = root == null ? 0 : root.GetComponentsInChildren<Renderer>(true).Count(NeedsFix);
                if (count > 0)
                    yield return new LunaLint.Issue { AssetPath = path, Message = $"{path}: {count} renderer(s)" };
            }
            foreach (string path in scenes)
            {
                Scene scene = SceneManager.GetSceneByPath(path);
                if (!scene.isLoaded)
                    continue;
                int count = SceneRenderers(scene).Count(NeedsFix);
                if (count > 0)
                    yield return new LunaLint.Issue { AssetPath = path, Message = $"{path} (open): {count} renderer(s)" };
            }
        }

        public void Apply(IList<LunaLint.Issue> issues)
        {
            List<string> prefabs = DependencyOrder(issues.Select(i => i.AssetPath)
                .Where(p => p != null && p.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)).Distinct().ToList());
            int renderers = 0, files = 0;
            try
            {
                for (int i = 0; i < prefabs.Count; i++)
                {
                    EditorUtility.DisplayProgressBar(Title, prefabs[i], (float)i / Math.Max(1, prefabs.Count));
                    GameObject root = PrefabUtility.LoadPrefabContents(prefabs[i]);
                    try
                    {
                        int changed = TurnOff(root.GetComponentsInChildren<Renderer>(true), false);
                        if (changed > 0)
                        {
                            PrefabUtility.SaveAsPrefabAsset(root, prefabs[i]);
                            renderers += changed;
                            files++;
                        }
                    }
                    finally
                    {
                        PrefabUtility.UnloadPrefabContents(root);
                    }
                }

                // Scene objects and prefab instance overrides left in the open scenes (inner prefabs are fixed by now).
                foreach (string path in issues.Select(i => i.AssetPath).Where(p => p != null && p.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)).Distinct())
                {
                    Scene scene = SceneManager.GetSceneByPath(path);
                    if (!scene.isLoaded)
                        continue;
                    int changed = TurnOff(SceneRenderers(scene), true);
                    if (changed > 0)
                    {
                        EditorSceneManager.MarkSceneDirty(scene);
                        renderers += changed;
                        files++;
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            Debug.Log($"[LunaBuildAssistant] Light / reflection probes turned off on {renderers} renderer(s) in {files} prefab(s) and scene(s). Save the open scenes if any changed.");
        }

        static bool NeedsFix(Renderer r) =>
            (r is MeshRenderer || r is SkinnedMeshRenderer)
            && (r.lightProbeUsage != LightProbeUsage.Off || r.reflectionProbeUsage != ReflectionProbeUsage.Off);

        static int TurnOff(IEnumerable<Renderer> renderers, bool inScene)
        {
            int count = 0;
            foreach (Renderer r in renderers.Where(NeedsFix).ToList())
            {
                if (inScene)
                    Undo.RecordObject(r, "Turn probes off");
                r.lightProbeUsage = LightProbeUsage.Off;
                r.reflectionProbeUsage = ReflectionProbeUsage.Off;
                if (inScene && PrefabUtility.IsPartOfPrefabInstance(r))
                    PrefabUtility.RecordPrefabInstancePropertyModifications(r);
                count++;
            }
            return count;
        }

        static IEnumerable<Renderer> SceneRenderers(Scene scene) =>
            scene.GetRootGameObjects().SelectMany(go => go.GetComponentsInChildren<Renderer>(true));

        // Scenes Luna builds (the plugin's list, else the Build Settings), with generated _NoPrefab copies mapped back
        // to their source scene: the copies are regenerated from the source and its prefabs before every build.
        static List<string> BuildScenes()
        {
            List<string> scenes;
            try
            {
                scenes = LunaPluginBridge.IsAvailable ? LunaPluginBridge.LunaScenes : new List<string>();
            }
            catch (Exception)
            {
                scenes = new List<string>();
            }
            if (scenes.Count == 0)
                scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToList();
            return scenes.Select(s => File.Exists(NoPrefabScenes.SourcePath(s)) ? NoPrefabScenes.SourcePath(s) : s)
                .Where(File.Exists).Distinct().ToList();
        }

        // Luna also exports everything under Resources.
        static IEnumerable<string> ResourcesPrefabs() =>
            AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => p.IndexOf("/Resources/", StringComparison.OrdinalIgnoreCase) >= 0);

        static bool UsesProbes(IEnumerable<string> assets)
        {
            foreach (string path in assets)
            {
                bool scene = path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase);
                if (!scene && !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                    continue;
                string text;
                try
                {
                    text = File.ReadAllText(path);
                }
                catch (IOException)
                {
                    continue;
                }
                if (ProbeObjects.IsMatch(text) || (scene && LightingData.IsMatch(text)))
                    return true;
            }
            return false;
        }

        // Git submodules inside Assets (shared code and art): left to their own repositories.
        static string[] SharedFolders()
        {
            string project = Directory.GetParent(Application.dataPath).FullName.Replace('\\', '/');
            for (string dir = project; dir != null; dir = Path.GetDirectoryName(dir)?.Replace('\\', '/'))
            {
                string modules = Path.Combine(dir, ".gitmodules");
                if (!File.Exists(modules))
                    continue;
                string prefix = project.Length > dir.Length ? project.Substring(dir.Length).TrimStart('/') + "/" : string.Empty;
                return Regex.Matches(File.ReadAllText(modules), @"^\s*path\s*=\s*(.+?)\s*$", RegexOptions.Multiline)
                    .Cast<Match>()
                    .Select(m => m.Groups[1].Value.Replace('\\', '/'))
                    .Where(p => p.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(p => p.Substring(prefix.Length).TrimEnd('/') + "/")
                    .ToArray();
            }
            return new string[0];
        }

        static bool Owned(string path, string[] shared) =>
            path.StartsWith("Assets/", StringComparison.Ordinal) && !shared.Any(s => path.StartsWith(s, StringComparison.Ordinal));

        // Inner and base prefabs first, so an outer prefab sees them already fixed and gets no redundant override.
        static List<string> DependencyOrder(List<string> prefabs)
        {
            var set = new HashSet<string>(prefabs);
            var ordered = new List<string>();
            var visited = new HashSet<string>();
            void Visit(string path)
            {
                if (!visited.Add(path))
                    return;
                foreach (string dependency in AssetDatabase.GetDependencies(path, false))
                    if (dependency != path && set.Contains(dependency))
                        Visit(dependency);
                ordered.Add(path);
            }
            foreach (string path in prefabs)
                Visit(path);
            return ordered;
        }
    }
}
