using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace Supercent.Util.Editor
{
    // Runtime calls into the Playworks plugin through the members LunaPluginShapes finds.
    // Any failure blocks the bridge until Reset(), so a plugin change never half-runs a build.
    public static class LunaPluginBridge
    {
        public static readonly string[] TestedVersions = { "6.2.1", "6.3.0", "7.1.0", "7.2.0" };

        const string DevelopNetwork = "develop";                      // CLI network name of "Build Develop".
        const string SvcAssetPath = "Assets/SVC_Luna.asset";
        const string LunaJsonFile = "luna.json";                       // Next to Assets.
        const string SaveDefine = "SUPERCENT_BUILD_ASSISTANT_SAVE";   // Never defined: removing it only saves luna.json.

        public struct App { public int Id; public string Name; }
        public struct Concept { public int Id; public string Name; }

        static LunaPluginShapes.Result resolved;
        static string version;
        static string packagePath;
        static string blockedReason;

        static LunaPluginShapes.Result Shapes { get { Resolve(); return resolved; } }

        public static string Version { get { Resolve(); return version; } }
        public static bool IsTestedVersion => TestedVersions.Contains(Version);
        public static bool IsAvailable { get { Resolve(); return blockedReason == null; } }
        public static string BlockedReason { get { Resolve(); return blockedReason; } }
        public static string PackagePath { get { Resolve(); return packagePath; } }   // The package's "scripts" folder.

        public static void Reset()
        {
            resolved = null;
            version = null;
            packagePath = null;
            sizeNetworks = null;
            blockedReason = null;
        }

        public static void Block(Exception e)
        {
            blockedReason = Unwrap(e).Message;
        }

        public static bool IsSignedIn => (bool)Call(Shapes.IsSignedIn, null);
        public static bool IsBuilding => (bool)Call(Shapes.IsBuilding, null);

        public static App[] Apps => Items(Shapes.Apps).Select(a => new App { Id = (int)Field(a, "id"), Name = ((string)Field(a, "name"))?.Trim() }).ToArray();
        public static Concept[] Concepts => Items(Shapes.Concepts).Select(c => new Concept { Id = (int)Field(c, "id"), Name = ((string)Field(c, "name"))?.Trim() }).ToArray();

        public static Task LoadConcepts(int appId) => (Task)Call(Shapes.LoadConcepts, null, appId);

        // An empty list usually means a stale UnityWebRequest cookie got the request a 401 (see LunaPlaygroundCookieFix).
        public static async Task LoadApps()
        {
            await (Task)Call(Shapes.LoadApps, null);
            if (Apps.Length > 0)
                return;
            UnityWebRequest.ClearCookieCache();
            await (Task)Call(Shapes.LoadApps, null);
        }

        public static int SavedApplicationId => Config() is object config ? (int)Field(config, "applicationID") : 0;

        public static void SaveUploadTarget(int appId, string creativeName)
        {
            object config = Config() ?? throw new InvalidOperationException("LunaData (luna.json) not found");
            SetField(config, "applicationID", appId);
            SetField(config, "creativeName", creativeName);
            Call(Shapes.SaveConfig, null, SaveDefine);

            if (!Regex.IsMatch(File.ReadAllText(LunaJsonPath), "\"applicationID\"\\s*:\\s*" + appId + "\\b"))
                throw new InvalidOperationException("luna.json was not updated with applicationID " + appId);
        }

        // luna.json assets.rules, the live object the plugin's Assets settings edit (see LunaAssetRules).
        internal static object AssetRules() =>
            Field(Field(Config() ?? throw new InvalidOperationException("LunaData (luna.json) not found"), "assets"), "rules");

        // luna.json unity.assets.includes / unity.assets.excludes / unity.scripts.excludes, live lists the plugin's
        // Assets tab and Code Exclusions edit; SaveConfig after changing one.
        internal static List<string> PathList(string section, string list)
        {
            object holder = Field(Field(Config() ?? throw new InvalidOperationException("LunaData (luna.json) not found"), "unity"), section);
            if (Field(holder, list) is List<string> paths)
                return paths;
            var created = new List<string>();
            SetField(holder, list, created);
            return created;
        }

        internal static void SaveConfig() => Call(Shapes.SaveConfig, null, SaveDefine);

        // Scenes ticked in the plugin's scene list (luna.json unity.scenes), exported in this order.
        public static List<string> LunaScenes =>
            Config() is object config ? ((IEnumerable)Field(Field(config, "unity"), "scenes") ?? new string[0]).Cast<string>().ToList() : new List<string>();

        // The scene the playable opens with (luna.json unity.startupScene, an index into unity.scenes).
        public static string LunaStartupScene
        {
            get
            {
                List<string> scenes = LunaScenes;
                if (scenes.Count == 0)
                    return null;
                int index = (int)Field(Field(Config(), "unity"), "startupScene");
                return index >= 0 && index < scenes.Count ? scenes[index] : scenes[0];
            }
        }

        public static void SetLunaScenes(List<string> scenes, List<string> disabled, string startup)
        {
            object config = Config() ?? throw new InvalidOperationException("LunaData (luna.json) not found");
            object unity = Field(config, "unity");
            var currentDisabled = ((IEnumerable)Field(unity, "disabledScenes") ?? new string[0]).Cast<string>().ToList();
            List<string> newDisabled = currentDisabled.Except(scenes).Union(disabled).ToList();
            int startupIndex = Math.Max(0, scenes.IndexOf(startup));
            if (LunaScenes.SequenceEqual(scenes) && (int)Field(unity, "startupScene") == startupIndex
                && currentDisabled.OrderBy(s => s).SequenceEqual(newDisabled.OrderBy(s => s)))
                return;
            SetField(unity, "scenes", new List<string>(scenes));
            SetField(unity, "disabledScenes", newDisabled);
            SetField(unity, "startupScene", startupIndex);
            Call(Shapes.SaveConfig, null, SaveDefine);
        }

        // Same as unticking entries in the plugin's code Analysis tree: the cloud build keeps them.
        // Entries are "Class" (unusedClasses) or "Class#member" (unusedMethods); excluded only ever goes
        // true -> false when the plugin merges a play session, so a kept entry stays kept.
        public static int KeepCode(ICollection<string> entries)
        {
            object config = Config() ?? throw new InvalidOperationException("LunaData (luna.json) not found");
            object unity = Field(config, "unity");
            int kept = 0;
            foreach (object c in (IEnumerable)Field(unity, "unusedClasses"))
                if (entries.Contains((string)Field(c, "name")) && (bool)Field(c, "excluded"))
                {
                    SetField(c, "excluded", false);
                    kept++;
                }
            foreach (object type in (IEnumerable)Field(unity, "unusedMethods"))
            {
                string className = (string)Field(type, "className");
                foreach (string list in new[] { "instanceMethods", "staticMethods" })
                    foreach (object m in (IEnumerable)Field(type, list) ?? new object[0])
                        if (entries.Contains(className + "#" + (string)Field(m, "name")) && (bool)Field(m, "excluded"))
                        {
                            SetField(m, "excluded", false);
                            kept++;
                        }
            }
            if (kept > 0)
                Call(Shapes.SaveConfig, null, SaveDefine);
            return kept;
        }

        public static async Task<bool> BuildDevelop()
        {
            object controller = Shapes.Controller.GetValue(null);
            Type source = Shapes.BuildForNetwork.GetParameters()[1].ParameterType;
            object anySource = Enum.GetValues(source).GetValue(0);   // Only an analytics label.

            // The plugin locks assembly reloads around its own builds too.
            EditorApplication.LockReloadAssemblies();
            try
            {
                return await (Task<bool>)Call(Shapes.BuildForNetwork, controller, DevelopNetwork, anySource);
            }
            finally
            {
                EditorApplication.UnlockReloadAssemblies();
            }
        }

        // The label and fraction the plugin toolbar shows for the running build; false when idle.
        public static bool TryGetProgress(out string label, out float fraction)
        {
            label = null;
            fraction = 0f;
            object controller = Shapes.Controller.GetValue(null);
            int stage = Convert.ToInt32(Shapes.CurrentStage.GetValue(controller));
            if (stage < 0)
                return false;

            object step;
            try
            {
                step = Call(Shapes.ProgressStep, null, Shapes.CurrentCommand.GetValue(controller), stage);
            }
            catch (IndexOutOfRangeException)
            {
                return true;     // A stage newer than the plugin's own table: still building, no label.
            }
            label = (string)Shapes.ProgressLabel.GetValue(step);
            fraction = (float)Shapes.ProgressValue.GetValue(step);
            return true;
        }

        // Networks of the plugin's Size breakdown (CLI name -> display name), read from the plugin when possible.
        static readonly KeyValuePair<string, string>[] DefaultSizeNetworks =
        {
            new KeyValuePair<string, string>("ironsource", "ironSource"), new KeyValuePair<string, string>("facebook", "Facebook"),
            new KeyValuePair<string, string>("unityads", "Unity Ads"), new KeyValuePair<string, string>("applovin", "AppLovin"),
            new KeyValuePair<string, string>("vungle", "Vungle"), new KeyValuePair<string, string>("mintegral", "Mintegral"),
            new KeyValuePair<string, string>("tencent", "Tencent"), new KeyValuePair<string, string>("tiktok", "TikTok"),
            new KeyValuePair<string, string>("mraid", "Mraid"),
        };

        static KeyValuePair<string, string>[] sizeNetworks;

        public static KeyValuePair<string, string>[] SizeNetworks => sizeNetworks ??= FindSizeNetworks();

        static KeyValuePair<string, string>[] FindSizeNetworks()
        {
            try
            {
                const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
                Type section = Shapes.EstimateSize?.DeclaringType.Assembly.GetTypes()
                    .FirstOrDefault(t => t.GetFields(statics).Any(f => f.FieldType.Name == "Stats"));
                var networks = section?.GetFields(statics)
                    .Where(f => f.FieldType == typeof(Dictionary<string, string>))
                    .Select(f => f.GetValue(null) as Dictionary<string, string>)
                    .FirstOrDefault(d => d != null && d.ContainsKey(BuildSizeReport.DefaultNetwork));
                return networks?.ToArray() ?? DefaultSizeNetworks;
            }
            catch (Exception)
            {
                return DefaultSizeNetworks;             // Only the network list; the estimate itself still runs.
            }
        }

        public static bool CanEstimateSize => Shapes.EstimateSize != null;

        // The plugin's Size breakdown statistics step over the last build output; writes LunaTemp/stage3/stats.json.
        // Returns the CLI output: the stats JSON, which (unlike stats.json) also carries scripts/preloader/other and the full total.
        public static async Task<string> EstimateSize(string network)
        {
            MethodInfo estimate = Shapes.EstimateSize ?? throw new NotSupportedException("This plugin version has no Size breakdown estimate");
            using (new AssemblyReloadLock())
                return await (Task<string>)Call(estimate, Shapes.Controller.GetValue(null), network);
        }

        sealed class AssemblyReloadLock : IDisposable
        {
            public AssemblyReloadLock() => EditorApplication.LockReloadAssemblies();
            public void Dispose() => EditorApplication.UnlockReloadAssemblies();
        }

        // One Project Diagnostics entry. Description/Details are usually keys of the plugin's localization table,
        // filled with DescriptionValues/DetailsValues (see BuildDiagnostics).
        public struct Diagnostic
        {
            public bool IsError;
            public int Stage;
            public string Code, Description, FilePath, ClassName, MemberName;
            public string[] Details, DescriptionValues, DetailsValues;
            public int Line;
        }

        const byte ErrorSeverity = 1;    // The plugin turns its Project Diagnostics badge red on Severity == 1.

        // The Project Diagnostics report of the plugin's last build. id changes with every new report, so a
        // report left over from an earlier build can be told apart. False when the plugin keeps none yet.
        public static bool TryGetDiagnostics(out string id, out Diagnostic[] entries)
        {
            id = null;
            entries = new Diagnostic[0];
            object report = Shapes.DiagnosticsReport?.GetValue(null);
            if (report == null)
                return false;

            var logs = (Array)Loose(report, "Logs");
            id = Loose(report, "BuildID") + "|" + Loose(report, "StartTime") + "|" + (logs?.Length ?? 0);
            if (logs == null)
                return true;
            entries = logs.Cast<object>().Where(e => e != null).Select(e => new Diagnostic
            {
                IsError = Convert.ToByte(Loose(e, "Severity") ?? (byte)0) == ErrorSeverity,
                Stage = Convert.ToInt32(Loose(e, "Stage") ?? 0),
                Code = Loose(e, "ErrorCode") as string,
                Description = Loose(e, "Description") as string,
                Details = Loose(e, "Details") as string[],
                DescriptionValues = Loose(e, "DescriptionValues") as string[],
                DetailsValues = Loose(e, "DetailsValues") as string[],
                FilePath = Loose(e, "FilePath") as string,
                Line = Convert.ToInt32(Loose(e, "LinePosition") ?? 0),
                ClassName = Loose(e, "ClassName") as string,
                MemberName = Loose(e, "MemberName") as string,
            }).ToArray();
            return true;
        }

        // Report fields are plain data: a missing one reads as null instead of blocking the tool.
        static object Loose(object target, string name) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target);

        // Starts the plugin's local develop server if needed and opens it, like "Open in browser".
        public static void OpenInBrowser() => Call(Shapes.OpenInBrowser, null, false);

        // Same, without opening a browser (the plugin opens one only when the argument is false).
        public static void StartDevelopServer() => Call(Shapes.OpenInBrowser, null, true);

        // Same call as the plugin's "Build & upload"; creativeId -1 creates a concept named after luna.json creativeName.
        public static void BuildAndUpload(int appId, int creativeId) => Call(Shapes.BuildAndUpload, Shapes.Controller.GetValue(null), appId, creativeId);

        // Same as the SVC_Luna inspector's trash buttons on "used on scenes" and "( Runtime Analysis )".
        public static bool ClearShaderVariants()
        {
            UnityEngine.Object svc = AssetDatabase.LoadMainAssetAtPath(SvcAssetPath);
            if (svc == null || svc.GetType().FullName != LunaPluginShapes.SvcTypeName)
                return false;

            ((IList)Get(Shapes.SvcSceneVariants, svc)).Clear();
            ((IList)Get(Shapes.SvcRuntimeVariants, svc)).Clear();
            Call(Shapes.SvcInspectorUpdate, svc);
            EditorUtility.SetDirty(svc);
            AssetDatabase.SaveAssetIfDirty(svc);
            return true;
        }

        static void Resolve()
        {
            if (resolved != null)
                return;

            resolved = new LunaPluginShapes.Result();
            Assembly asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == LunaPluginShapes.AssemblyName);
            if (asm == null)
            {
                blockedReason = "Unity Playworks Plugin is not installed";
                return;
            }

            try
            {
                resolved = LunaPluginShapes.Resolve(asm);
                if (resolved.NetworkNames != null && !resolved.NetworkNames.Any(toName =>
                        Enum.GetValues(toName.GetParameters()[0].ParameterType).Cast<object>()
                            .Any(network => (string)Call(toName, null, network) == DevelopNetwork)))
                    resolved.Errors.Add($"Plugin has no \"{DevelopNetwork}\" build target");
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(asm);
                packagePath = package?.resolvedPath;
                version = package?.version ?? (Config() is object config ? Field(config, "version") as string : null);
            }
            catch (Exception e)
            {
                resolved.Errors.Add("Plugin scan failed: " + Unwrap(e).Message);
            }
            if (resolved.Errors.Count > 0)
                blockedReason = string.Join("\n", resolved.Errors);
        }

        // The holder exposes the project config and a default one; the project one has luna.json's projectId.
        static object Config()
        {
            Match id = Regex.Match(File.Exists(LunaJsonPath) ? File.ReadAllText(LunaJsonPath) : string.Empty, "\"projectId\"\\s*:\\s*\"([^\"]*)\"");
            foreach (MethodInfo getter in resolved.ConfigGetters ?? new MethodInfo[0])
            {
                object config = Call(getter, null);
                if (config != null && id.Success && Field(config, "projectId") as string == id.Groups[1].Value)
                    return config;
            }
            return null;
        }

        static string LunaJsonPath => Path.Combine(Directory.GetParent(Application.dataPath).FullName, LunaJsonFile);

        static object[] Items(FieldInfo arrayField) => ((Array)arrayField.GetValue(null))?.Cast<object>().ToArray() ?? new object[0];

        static object Field(object target, string name) => FieldOf(target, name).GetValue(target);
        static void SetField(object target, string name, object value) => FieldOf(target, name).SetValue(target, value);

        static FieldInfo FieldOf(object target, string name) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingFieldException(target.GetType().Name, name);

        static object Get(PropertyInfo property, object target) => property.GetValue(target, null);

        static object Call(MethodInfo method, object target, params object[] args)
        {
            try
            {
                return method.Invoke(target, args);
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }

        static Exception Unwrap(Exception e) => e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
    }
}
