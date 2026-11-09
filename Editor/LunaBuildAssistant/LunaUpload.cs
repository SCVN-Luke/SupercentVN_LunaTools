using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Supercent.Util.Editor
{
    // Build & upload shared by the window and the MCP tool (unity_build_upload): the checks shown before uploading and
    // the steps that start the plugin's upload. Main thread only; no dialog, the callers decide how to confirm.
    public static class LunaUpload
    {
        public const int MaxListedChanges = 10;

        // Why the upload should be confirmed first; empty when every check is clean.
        public static List<string> Warnings(UploadChecksPanel checks)
        {
            DateTime? last = ProjectBuildState.LastPrivateBuildUtc;
            List<string> changes = ProjectBuildState.ChangesSincePrivateBuild();
            var parts = new List<string>();
            if (!last.HasValue)
                parts.Add("Build Develop has not been run in this project yet.");
            else if (changes.Count > 0)
                parts.Add($"{changes.Count} change(s) since the last Build Develop ({last.Value.ToLocalTime():yyyy-MM-dd HH:mm}):\n- " +
                          string.Join("\n- ", changes.Take(MaxListedChanges)) + (changes.Count > MaxListedChanges ? "\n…" : string.Empty));
            parts.AddRange(checks.Warnings());
            return parts;
        }

        // Unsaved Luna scenes: building them would ask the user to save (a dialog), so agents are refused instead.
        public static List<string> UnsavedLunaScenes()
        {
            var sources = new HashSet<string>(LunaPluginBridge.LunaScenes.Select(NoPrefabScenes.SourcePath));
            var dirty = new List<string>();
            for (int i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                var scene = EditorSceneManager.GetSceneAt(i);
                if (scene.isDirty && sources.Contains(scene.path))
                    dirty.Add(scene.path);
            }
            return dirty;
        }

        // Regenerates the _NoPrefab scenes, clears the SVC_Luna variant lists, saves the target in luna.json and starts
        // the plugin's Build & upload (it runs on without a result; see BuildDiagnostics for what it reports).
        // creativeId -1 creates the concept conceptName. Null when started, otherwise why it was cancelled.
        public static string Start(int appId, string conceptName, int creativeId)
        {
            try
            {
                if (!NoPrefabScenes.Prepare(out string summary))
                    return "Build cancelled: save the modified scene first.";
                if (!string.IsNullOrEmpty(summary))
                    Debug.Log("[LunaBuildAssistant] " + summary);
            }
            catch (InvalidOperationException e)
            {
                return "Build cancelled: " + e.Message;     // e.g. the _NoPrefab scene is open.
            }
            if (!LunaPluginBridge.ClearShaderVariants())
                Debug.LogWarning("[LunaBuildAssistant] Assets/SVC_Luna.asset not found; shader variant lists were not cleared.");
            LunaPluginBridge.SaveUploadTarget(appId, conceptName);
            ProjectBuildState.LastApplicationId = appId;
            LunaPluginBridge.BuildAndUpload(appId, creativeId);
            StrippedCodeCheck.SaveBaseline();       // What this upload strips no longer counts as new.
            return null;
        }
    }
}
