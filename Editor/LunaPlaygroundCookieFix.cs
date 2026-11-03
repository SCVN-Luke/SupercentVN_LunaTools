using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace Supercent.Util.Editor
{
    // A stale cookie in this Editor's UnityWebRequest jar makes Playground answer 401 to the
    // Playworks plugin, so "Upload to Creative Library" shows no Available Applications and its
    // Refresh button keeps failing. Clear the cookies, then trigger the plugin's own refresh.
    // The plugin DLL is obfuscated, so it is reached by reflection on stable type shapes only.
    public static class LunaPlaygroundCookieFix
    {
        const string PluginAssembly = "EditorScripts";
        const string BuildDialogueType = "CreateHubBuildDialogue";   // Field type unique to the upload tab.

        [MenuItem("Tools/Luna/Fix Empty Playground Applications")]
        static void FixEmptyApplications()
        {
            // The per-URI overload misses the bad cookie, so clear the whole jar.
            UnityWebRequest.ClearCookieCache();

            MethodInfo refresh = FindRefreshMethod();
            if (refresh == null)
            {
                Debug.LogWarning("[LunaPlaygroundCookieFix] Cookies cleared, but the Playworks refresh method was not found. " +
                                 "Press the Refresh button next to Available Applications.");
                return;
            }

            refresh.Invoke(null, null);
            Debug.Log("[LunaPlaygroundCookieFix] Cookies cleared and Available Applications refresh started. " +
                      "Re-select the app if luna.json applicationID was reset to 0.");
        }

        // Upload tab = the plugin type holding CreateHubBuildDialogue fields;
        // its refresh = the only parameterless static async void method on it.
        static MethodInfo FindRefreshMethod()
        {
            Assembly asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == PluginAssembly);
            if (asm == null)
                return null;

            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }

            const BindingFlags instanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags staticMethods = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            Type uploadTab = types.FirstOrDefault(t => t.GetFields(instanceFields).Any(f => f.FieldType.Name == BuildDialogueType));
            if (uploadTab == null)
                return null;

            MethodInfo[] candidates = uploadTab.GetMethods(staticMethods)
                .Where(m => m.ReturnType == typeof(void)
                         && m.GetParameters().Length == 0
                         && m.IsDefined(typeof(AsyncStateMachineAttribute), false))
                .ToArray();
            return candidates.Length == 1 ? candidates[0] : null;
        }
    }
}
