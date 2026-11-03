using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Supercent.Util.Editor
{
    // Finds the Playworks plugin (EditorScripts.dll) members the Build Assistant calls.
    // The DLL is obfuscated and names change per release, so every rule matches stable shapes
    // (unobfuscated type names, signatures, field types) and compares types by name only.
    // Name-only comparison also lets the same rules run against other plugin versions
    // loaded in a MetadataLoadContext, which is how compatibility is checked offline.
    public static class LunaPluginShapes
    {
        public const string AssemblyName = "EditorScripts";
        public const string MediatorTypeName = "Luna.Unity.Tutorial.TutorialMediator";
        public const string SvcTypeName = "Luna.Unity.Utils.Shaders.LunaSVC";

        const BindingFlags StaticMembers = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        public sealed class Result
        {
            // Public, unobfuscated plugin API.
            public MethodInfo IsSignedIn;               // TutorialMediator.isUserSignedIn
            public MethodInfo IsBuilding;               // TutorialMediator.IsBuildInProcess
            public MethodInfo SaveConfig;               // TutorialMediator.RemoveExternalDefine: always rewrites luna.json.
            public PropertyInfo SvcSceneVariants;       // "Shader variants used on scenes"
            public PropertyInfo SvcRuntimeVariants;     // "Shader variants ( Runtime Analysis )"
            public MethodInfo SvcInspectorUpdate;       // What the SVC inspector's trash buttons call.

            // Obfuscated members.
            public FieldInfo Apps;                      // static ApplicationData[]
            public FieldInfo Concepts;                  // static CreativeItem[]
            public MethodInfo LoadApps;                 // static Task()
            public MethodInfo LoadConcepts;             // static Task(int appId)
            public MethodInfo[] ConfigGetters;          // static LunaData() (current and default config)
            public FieldInfo Controller;                // build controller singleton
            public MethodInfo BuildAndUpload;           // void(int appId, int creativeId), -1 = new concept
            public MethodInfo BuildForNetwork;          // Task<bool>(string network, BuildSource)
            public MethodInfo[] NetworkNames;           // static string(Network): enum -> CLI name ("develop")
            public MethodInfo OpenInBrowser;            // static void(bool serverOnly)
            public FieldInfo CurrentCommand;            // controller: running CLI command, -1 when idle
            public FieldInfo CurrentStage;              // controller: build stage, -1 when idle
            public MethodInfo ProgressStep;             // static Step(Command, int stage): the plugin toolbar's progress bar
            public FieldInfo ProgressLabel;             // Step: string
            public FieldInfo ProgressValue;             // Step: float 0..1

            // Optional: a missing member disables only its feature, never the whole tool.
            public MethodInfo EstimateSize;             // controller Task<string>(string network): Size breakdown statistics

            public readonly List<string> Errors = new List<string>();
        }

        public static Result Resolve(Assembly asm)
        {
            var r = new Result();
            Type[] types = GetTypes(asm);

            Type mediator = asm.GetType(MediatorTypeName);
            if (mediator == null)
                r.Errors.Add(MediatorTypeName + " not found");
            else
            {
                r.IsSignedIn = mediator.GetProperty("isUserSignedIn", BindingFlags.Static | BindingFlags.Public)?.GetGetMethod();
                r.IsBuilding = mediator.GetProperty("IsBuildInProcess", BindingFlags.Static | BindingFlags.Public)?.GetGetMethod();
                r.SaveConfig = mediator.GetMethods(BindingFlags.Static | BindingFlags.Public)
                    .FirstOrDefault(m => m.Name == "RemoveExternalDefine" && ParamsAre(m, "System.String"));
                Require(r, r.IsSignedIn, "TutorialMediator.isUserSignedIn");
                Require(r, r.IsBuilding, "TutorialMediator.IsBuildInProcess");
                Require(r, r.SaveConfig, "TutorialMediator.RemoveExternalDefine");
            }

            Type svc = asm.GetType(SvcTypeName);
            if (svc == null)
                r.Errors.Add(SvcTypeName + " not found");
            else
            {
                r.SvcSceneVariants = svc.GetProperty("ShaderVariants", BindingFlags.Instance | BindingFlags.Public);
                r.SvcRuntimeVariants = svc.GetProperty("UsedByLunaBuild", BindingFlags.Instance | BindingFlags.Public);
                r.SvcInspectorUpdate = svc.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(m => m.Name == "OnSvcInspectorUpdate" && m.GetParameters().Length == 0);
                Require(r, r.SvcSceneVariants, "LunaSVC.ShaderVariants");
                Require(r, r.SvcRuntimeVariants, "LunaSVC.UsedByLunaBuild");
                Require(r, r.SvcInspectorUpdate, "LunaSVC.OnSvcInspectorUpdate");
            }

            // Playground cache: the only type holding a static ApplicationData[].
            Type cache = Single(r, "Playground cache type", types.Where(t => StaticFieldOfArray(t, "ApplicationData") != null));
            if (cache != null)
            {
                r.Apps = StaticFieldOfArray(cache, "ApplicationData");
                r.Concepts = StaticFieldOfArray(cache, "CreativeItem");
                Require(r, r.Concepts, "CreativeItem[] cache");
                r.LoadApps = Single(r, "applications loader", cache.GetMethods(StaticMembers)
                    .Where(m => Is(m.ReturnType, "System.Threading.Tasks.Task") && m.GetParameters().Length == 0));
                r.LoadConcepts = Single(r, "creatives loader", cache.GetMethods(StaticMembers)
                    .Where(m => Is(m.ReturnType, "System.Threading.Tasks.Task") && ParamsAre(m, "System.Int32")));
            }

            // Config holder: the only static class with a static LunaData field.
            Type config = Single(r, "LunaData holder", types.Where(t => IsStaticClass(t)
                && t.GetFields(StaticMembers).Any(f => f.FieldType.Name == "LunaData")));
            if (config != null)
            {
                r.ConfigGetters = config.GetMethods(StaticMembers)
                    .Where(m => m.ReturnType.Name == "LunaData" && m.GetParameters().Length == 0).ToArray();
                if (r.ConfigGetters.Length == 0)
                    r.Errors.Add("LunaData getter not found");
            }

            // Build controller: a singleton with an (int appId, int creativeId) build & upload entry.
            Type controller = Single(r, "build controller", types.Where(t =>
                t.GetFields(StaticMembers).Any(f => f.FieldType == t)
                && t.GetMethods(InstanceMembers).Any(m => m.IsPublic && IsVoid(m) && ParamsAre(m, "System.Int32", "System.Int32"))));
            if (controller != null)
            {
                r.Controller = controller.GetFields(StaticMembers).First(f => f.FieldType == controller);
                r.BuildAndUpload = Single(r, "build & upload", controller.GetMethods(InstanceMembers)
                    .Where(m => m.IsPublic && IsVoid(m) && ParamsAre(m, "System.Int32", "System.Int32")));
                r.BuildForNetwork = Single(r, "build for network", controller.GetMethods(InstanceMembers)
                    .Where(m => IsTaskOfBool(m.ReturnType) && m.GetParameters().Length == 2
                             && Is(m.GetParameters()[0].ParameterType, "System.String")
                             && m.GetParameters()[1].ParameterType.IsEnum));
                // Size breakdown: runs the CLI statistics step for a network over the last build output.
                MethodInfo[] estimate = controller.GetMethods(InstanceMembers)
                    .Where(m => m.IsPublic && IsTaskOf(m.ReturnType, "System.String") && ParamsAre(m, "System.String")).Take(2).ToArray();
                r.EstimateSize = estimate.Length == 1 ? estimate[0] : null;
                if (r.BuildForNetwork != null)
                {
                    // Sibling overload Task<bool>(Network, BuildSource) names the network enum.
                    Type source = r.BuildForNetwork.GetParameters()[1].ParameterType;
                    Type network = Single(r, "network enum", controller.GetMethods(InstanceMembers)
                        .Where(m => IsTaskOfBool(m.ReturnType) && m.GetParameters().Length == 2
                                 && m.GetParameters()[0].ParameterType.IsEnum
                                 && m.GetParameters()[1].ParameterType == source)
                        .Select(m => m.GetParameters()[0].ParameterType).Distinct());
                    if (network != null)
                        r.NetworkNames = types.SelectMany(t => t.GetMethods(StaticMembers))
                            .Where(m => Is(m.ReturnType, "System.String") && m.GetParameters().Length == 1
                                     && m.GetParameters()[0].ParameterType == network).ToArray();
                }
            }

            // Build progress, as the plugin toolbar draws it. The stage enum is what the parser of
            // the CLI's "[ Stage1 ] ..." log lines returns; the step lookup maps (command, stage) to label + fraction.
            Type stage = Single(r, "build stage enum", types
                .Where(t => IsStaticClass(t) && t.GetFields(StaticMembers).Any(f => f.IsLiteral
                         && Is(f.FieldType, "System.String") && ((string)f.GetRawConstantValue()).StartsWith("[ Stage1 ]", StringComparison.Ordinal)))
                .SelectMany(t => t.GetMethods(StaticMembers))
                .Where(m => m.ReturnType.IsEnum && ParamsAre(m, "System.String"))
                .Select(m => m.ReturnType).Distinct());
            r.ProgressStep = Single(r, "progress step lookup", types.SelectMany(t => t.GetMethods(StaticMembers))
                .Where(m => m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType.IsEnum
                         && Is(m.GetParameters()[1].ParameterType, "System.Int32") && IsLabelAndFraction(m.ReturnType)));
            if (r.ProgressStep != null)
            {
                FieldInfo[] stepFields = r.ProgressStep.ReturnType.GetFields(BindingFlags.Instance | BindingFlags.Public);
                r.ProgressLabel = stepFields.First(f => Is(f.FieldType, "System.String"));
                r.ProgressValue = stepFields.First(f => Is(f.FieldType, "System.Single"));
            }
            if (controller != null && stage != null && r.ProgressStep != null)
            {
                Type command = r.ProgressStep.GetParameters()[0].ParameterType;
                r.CurrentCommand = Single(r, "current command field", controller.GetFields(InstanceMembers).Where(f => f.FieldType == command));
                r.CurrentStage = Single(r, "current stage field", controller.GetFields(InstanceMembers).Where(f => f.FieldType == stage));
            }

            // Local develop server: static class queueing Tuple<object, ...> server events,
            // with a static void(bool) "start and open" entry.
            Type server = Single(r, "develop server", types.Where(t => IsStaticClass(t)
                && t.GetFields(StaticMembers).Any(f => IsQueueOfTupleOfObject(f.FieldType))
                && t.GetMethods(StaticMembers).Any(m => m.IsPublic && IsVoid(m) && ParamsAre(m, "System.Boolean"))));
            if (server != null)
                r.OpenInBrowser = server.GetMethods(StaticMembers).First(m => m.IsPublic && IsVoid(m) && ParamsAre(m, "System.Boolean"));

            return r;
        }

        static Type[] GetTypes(Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null).ToArray(); }
        }

        static T Single<T>(Result r, string what, IEnumerable<T> candidates) where T : class
        {
            T[] found = candidates.Take(2).ToArray();
            if (found.Length == 1)
                return found[0];
            r.Errors.Add(found.Length == 0 ? what + " not found" : what + " is ambiguous");
            return null;
        }

        static void Require(Result r, object member, string what)
        {
            if (member == null)
                r.Errors.Add(what + " not found");
        }

        static FieldInfo StaticFieldOfArray(Type t, string elementName) =>
            t.GetFields(StaticMembers).FirstOrDefault(f => f.FieldType.IsArray && f.FieldType.GetElementType().Name == elementName);

        static bool IsStaticClass(Type t) => t.IsClass && t.IsAbstract && t.IsSealed;
        static bool Is(Type t, string fullName) => t.FullName == fullName;
        static bool IsVoid(MethodInfo m) => Is(m.ReturnType, "System.Void");

        static bool ParamsAre(MethodInfo m, params string[] fullNames)
        {
            ParameterInfo[] ps = m.GetParameters();
            return ps.Length == fullNames.Length && ps.Select((p, i) => Is(p.ParameterType, fullNames[i])).All(x => x);
        }

        static bool IsTaskOfBool(Type t) => IsTaskOf(t, "System.Boolean");

        static bool IsTaskOf(Type t, string resultFullName) =>
            t.IsGenericType && t.GetGenericTypeDefinition().FullName == "System.Threading.Tasks.Task`1"
            && Is(t.GetGenericArguments()[0], resultFullName);

        static bool IsLabelAndFraction(Type t)
        {
            if (!t.IsClass || t.IsArray)
                return false;
            FieldInfo[] fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public);
            return fields.Length == 2 && fields.Count(f => Is(f.FieldType, "System.String")) == 1
                && fields.Count(f => Is(f.FieldType, "System.Single")) == 1;
        }

        static bool IsQueueOfTupleOfObject(Type t)
        {
            if (!t.IsGenericType || t.GetGenericTypeDefinition().FullName != "System.Collections.Concurrent.ConcurrentQueue`1")
                return false;
            Type item = t.GetGenericArguments()[0];
            return item.IsGenericType && item.GetGenericTypeDefinition().FullName == "System.Tuple`2"
                && Is(item.GetGenericArguments()[0], "System.Object");
        }
    }
}
