using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Supercent.Util.Editor
{
    // Keeps Luna's code strip list from growing behind your back.
    // Build Develop sessions merge what one play called into the plugin's LunaData (luna.json unity.unusedMethods /
    // unusedClasses), and a short or partial play can mark late-game code unused, so the next cloud build strips it
    // (the "stripped but still called" warnings). Every few seconds (in memory, before luna.json on disk shows it) and
    // right before an upload, every entry marked excluded that was not excluded at the last upload (or in the committed
    // luna.json before the first one) is kept again. The strip list can then only shrink; "Strip OK" in the upload
    // checks adds entries to that baseline on purpose.
    [InitializeOnLoad]
    public static class StripGuard
    {
        const string EnabledKey = "SupercentVN.LunaTools.StripGuard";
        const string MenuPath = "Tools/Luna/Lock Code Strip List";
        const double CheckInterval = 5.0;

        static double nextCheck;
        static HashSet<string> cachedBaseline;
        static string cachedSource;
        static DateTime cachedBaselineWrite = DateTime.MaxValue;

        static StripGuard() => EditorApplication.update += Tick;

        public static bool Enabled
        {
            get => EditorPrefs.GetBool(EnabledKey, true);
            set => EditorPrefs.SetBool(EnabledKey, value);
        }

        [MenuItem(MenuPath, priority = 200)]
        static void ToggleMenu()
        {
            Enabled = !Enabled;
            if (Enabled)
                Enforce();
        }

        [MenuItem(MenuPath, true)]
        static bool ToggleMenuValidate()
        {
            Menu.SetChecked(MenuPath, Enabled);
            return true;
        }

        static void Tick()
        {
            if (EditorApplication.timeSinceStartup < nextCheck)
                return;
            nextCheck = EditorApplication.timeSinceStartup + CheckInterval;
            if (!Enabled || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            // Leave LunaData alone while the plugin builds; the next tick after the build catches up.
            try
            {
                if (LunaPluginBridge.TryGetProgress(out _, out _))
                    return;
            }
            catch (Exception)
            {
                // Plugin shape not readable: still guard.
            }
            Enforce();
        }

        // The saved baseline changes only on upload / "Strip OK"; reread it then (the git fallback runs once per reload).
        static HashSet<string> Baseline(out string source)
        {
            string path = StrippedCodeCheck.BaselinePath;
            DateTime write = System.IO.File.Exists(path) ? System.IO.File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (write != cachedBaselineWrite)
            {
                cachedBaseline = StrippedCodeCheck.LoadBaseline(out cachedSource);
                cachedBaselineWrite = write;
            }
            source = cachedSource;
            return cachedBaseline;
        }

        // Keeps every entry stripped now but not in the baseline. Returns how many were kept.
        public static int Enforce()
        {
            if (!Enabled)
                return 0;
            try
            {
                HashSet<string> stripped = LunaPluginBridge.StrippedCode();
                if (stripped == null)
                    return 0;                           // LunaData not loaded (no luna.json yet).
                HashSet<string> baseline = Baseline(out string source);
                if (baseline == null)
                    return 0;                           // Nothing to compare with (no upload yet, no git).
                List<string> extra = stripped.Where(e => !baseline.Contains(e)).ToList();
                if (extra.Count == 0)
                    return 0;
                int kept = LunaPluginBridge.KeepCode(new HashSet<string>(extra, StringComparer.Ordinal));
                if (kept > 0)
                    Debug.Log($"[LunaBuildAssistant] Strip guard: kept {kept} luna.json code entr{(kept == 1 ? "y" : "ies")} a Build Develop session " +
                              $"marked unused but {source} did not strip (e.g. {string.Join(", ", extra.Take(3))}). Turn off: {MenuPath}.");
                return kept;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[LunaBuildAssistant] Strip guard could not check luna.json: " + e.Message);
                return 0;
            }
        }
    }
}
