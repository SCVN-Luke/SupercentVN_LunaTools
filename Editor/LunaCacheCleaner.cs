using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Supercent.Util.Editor
{
    // Stale Luna caches break the next build, so clear them before building.
    public static class LunaCacheCleaner
    {
        const string LunaTempFolder = "LunaTemp";             // Next to Assets.
        const string SvcLunaAsset = "Assets/SVC_Luna.asset";

        [MenuItem("Tools/Luna/Clear Build Cache")]
        public static void ClearBuildCache()
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string lunaTemp = Path.Combine(projectRoot, LunaTempFolder);
            bool tempDeleted = false, svcDeleted = false;

            try
            {
                if (Directory.Exists(lunaTemp))
                {
                    Directory.Delete(lunaTemp, true);
                    tempDeleted = true;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[LunaCacheCleaner] Failed to delete {lunaTemp}: {e.Message}");
            }

            if (AssetDatabase.LoadMainAssetAtPath(SvcLunaAsset) != null)
                svcDeleted = AssetDatabase.DeleteAsset(SvcLunaAsset);
            else if (File.Exists(Path.Combine(projectRoot, SvcLunaAsset)))
            {
                File.Delete(Path.Combine(projectRoot, SvcLunaAsset));
                File.Delete(Path.Combine(projectRoot, SvcLunaAsset + ".meta"));
                svcDeleted = true;
            }

            AssetDatabase.Refresh();
            Debug.Log($"[LunaCacheCleaner] {LunaTempFolder}: {(tempDeleted ? "deleted" : "not found")}, " +
                      $"{SvcLunaAsset}: {(svcDeleted ? "deleted" : "not found")}");
        }
    }
}
