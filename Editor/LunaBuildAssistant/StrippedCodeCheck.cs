using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Supercent.Util.Editor
{
    // Which code a cloud build will strip that it should not.
    // Luna strips every luna.json unity.unusedMethods/unusedClasses entry still marked excluded:true
    // (never called during any private-server session). Most of those are rightly stripped, so only
    // entries that are new since the last upload AND are still referenced by code that does run
    // (IL calls, overrides, delegates, string Invoke/SendMessage, UnityEvents, animation events) are flagged.
    public static class StrippedCodeCheck
    {
        const string LunaJsonFile = "luna.json";
        const string BaselineFile = "Library/SupercentVN/LunaStripBaseline.json";
        const int MaxCallers = 5;

        // Bridge-generated members that have no user-written C# counterpart.
        static readonly HashSet<string> GeneratedMembers = new HashSet<string> { "init", "$clone", "equals", "getHashCode", "toString" };
        static readonly HashSet<string> StringDispatch = new HashSet<string>
        {
            "Invoke", "InvokeRepeating", "IsInvoking", "StartCoroutine", "SendMessage", "SendMessageUpwards", "BroadcastMessage",
        };
        static readonly string[] EventAssetPatterns = { "*.unity", "*.prefab" };
        static readonly string[] AnimationAssetPatterns = { "*.anim", "*.fbx.meta", "*.FBX.meta" };
        static readonly Regex PersistentCall = new Regex(@"m_TargetAssemblyTypeName: (?<type>[^,\r\n]*)[^\r\n]*\r?\n\s*m_MethodName: (?<method>[\w.]+)|m_MethodName: (?<method>[\w.]+)");
        static readonly Regex AnimationEvent = new Regex(@"functionName: (?<method>\w+)");

        public sealed class Finding
        {
            public string ClassName;
            public string Member;                   // Luna member base; null = the whole class is stripped.
            public string[] Entries;                // Exact luna.json entries ("Class" or "Class#name").
            public Type Type;                       // Null when the class is not project code.
            public readonly List<string> ReferencedBy = new List<string>();
            public string Label => Member == null ? ClassName + " (whole class)" : ClassName + "." + Member;
            public string ShortLabel
            {
                get
                {
                    string ns = Type?.Namespace;
                    string label = Label;
                    return string.IsNullOrEmpty(ns) || !label.StartsWith(ns + ".", StringComparison.Ordinal) ? label : label.Substring(ns.Length + 1);
                }
            }
        }

        public sealed class Report
        {
            public readonly List<Finding> Referenced = new List<Finding>();
            public readonly List<Finding> Unreferenced = new List<Finding>();
            public int StrippedCount;
            public string BaselineSource;           // Null when there is nothing to compare against.
        }

        [Serializable] class LunaJson { public UnityPart unity; }
        [Serializable] class UnityPart { public CodeEntry[] unusedClasses; public ClassMethods[] unusedMethods; }
        [Serializable] class CodeEntry { public string name; public bool excluded; }
        [Serializable] class ClassMethods { public string className; public CodeEntry[] instanceMethods; public CodeEntry[] staticMethods; }
        [Serializable] class Baseline { public string source; public long savedUtcTicks; public string[] entries; }

        static readonly Dictionary<string, KeyValuePair<DateTime, List<string>>> assetRefCache = new Dictionary<string, KeyValuePair<DateTime, List<string>>>();

        static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;
        public static string LunaJsonPath => Path.Combine(ProjectRoot, LunaJsonFile);

        public static Report Run()
        {
            var report = new Report();
            HashSet<string> stripped = StrippedEntries(File.Exists(LunaJsonPath) ? File.ReadAllText(LunaJsonPath) : null, out HashSet<string> ran);
            report.StrippedCount = stripped.Count;
            HashSet<string> baseline = LoadBaseline(out report.BaselineSource);

            Dictionary<string, Finding> findings = NewFindings(stripped, baseline);
            if (findings.Count > 0)
                FindReferences(findings, ran);

            foreach (Finding f in findings.Values.OrderBy(f => f.Label, StringComparer.Ordinal))
                (f.ReferencedBy.Count > 0 ? report.Referenced : report.Unreferenced).Add(f);
            return report;
        }

        // Called when an upload starts: what it strips now counts as accepted.
        public static void SaveBaseline()
        {
            string json = File.Exists(LunaJsonPath) ? File.ReadAllText(LunaJsonPath) : null;
            WriteBaseline("last upload", StrippedEntries(json, out _));
        }

        // "Strip OK": stop flagging these entries until they change.
        public static void Accept(IEnumerable<string> entries)
        {
            HashSet<string> baseline = LoadBaseline(out _) ?? new HashSet<string>();
            baseline.UnionWith(entries);
            WriteBaseline(ReadSaved()?.source ?? "reviewed", baseline);
        }

        // "Class" for stripped classes, "Class#member" for stripped methods; ran = member groups called in some session.
        static HashSet<string> StrippedEntries(string json, out HashSet<string> ran)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            ran = new HashSet<string>(StringComparer.Ordinal);
            UnityPart unity = string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<LunaJson>(json)?.unity;
            if (unity == null)
                return result;
            foreach (CodeEntry c in unity.unusedClasses ?? new CodeEntry[0])
                if (c.excluded)
                    result.Add(c.name);
            foreach (ClassMethods c in unity.unusedMethods ?? new ClassMethods[0])
            {
                foreach (CodeEntry m in (c.instanceMethods ?? new CodeEntry[0]).Concat(c.staticMethods ?? new CodeEntry[0]))
                    if (m.excluded)
                        result.Add(c.className + "#" + m.name);
                    else
                        ran.Add(GroupKey(c.className + "#" + m.name));
            }
            return result;
        }

        static Dictionary<string, Finding> NewFindings(HashSet<string> stripped, HashSet<string> baseline)
        {
            Dictionary<string, Type> typesByName = ProjectTypesByLunaName();
            var findings = new Dictionary<string, Finding>(StringComparer.Ordinal);
            foreach (IGrouping<string, string> group in stripped
                         .Where(e => baseline == null || !baseline.Contains(e))
                         .GroupBy(GroupKey))
            {
                if (group.Key == null)
                    continue;
                int hash = group.Key.IndexOf('#');
                string className = hash < 0 ? group.Key : group.Key.Substring(0, hash);
                if (!typesByName.TryGetValue(className, out Type type))
                    continue;                           // Engine/package code: not the project's to fix.
                findings[group.Key] = new Finding
                {
                    ClassName = className,
                    Member = hash < 0 ? null : group.Key.Substring(hash + 1),
                    Entries = group.ToArray(),
                    Type = type,
                };
            }
            return findings;
        }

        // "Class#Foo$1" and "Class#Foo" share "Class#Foo": overloads cannot be told apart from C#.
        static string GroupKey(string entry)
        {
            int hash = entry.IndexOf('#');
            if (hash < 0)
                return entry;
            string member = ProjectCode.MemberBase(entry.Substring(hash + 1));
            return GeneratedMembers.Contains(member) ? null : entry.Substring(0, hash + 1) + member;
        }

        // Callers count only when Luna saw them run: code it never compiled (UNITY_EDITOR blocks, excluded
        // folders) or never called cannot reach a stripped member on the cloud build either.
        static void FindReferences(Dictionary<string, Finding> findings, HashSet<string> ran)
        {
            var overrides = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            void Hit(string key, string caller)
            {
                if (key == null || key == caller)
                    return;
                if (key.StartsWith("*#", StringComparison.Ordinal))
                {
                    string member = key.Substring(2);
                    foreach (Finding f in findings.Values.Where(f => f.Member == member))
                        AddCaller(f, caller);
                }
                else if (findings.TryGetValue(key, out Finding f))
                    AddCaller(f, caller);
            }

            foreach (Type type in ProjectCode.Types)
            {
                foreach (MethodBase method in type.GetMethods(ProjectCode.AllDeclared).Cast<MethodBase>().Concat(type.GetConstructors(ProjectCode.AllDeclared)))
                {
                    if (!ProjectCode.TryOwner(method, out Type owner, out string member))
                        continue;
                    string ownerClass = ProjectCode.ClassName(owner);
                    string caller = ownerClass + "#" + member;
                    if (!ran.Contains(caller) && !(member == "ctor" && ran.Contains(ownerClass + "#init")))
                        continue;
                    List<string> literals = null;
                    bool dispatchesByName = false;
                    foreach (IlReader.Instruction ins in IlReader.Read(method))
                    {
                        switch (ins.Operand)
                        {
                            case MethodBase callee:
                                if (ProjectCode.TryOwner(callee, out Type calleeOwner, out string calleeMember))
                                {
                                    string calleeClass = ProjectCode.ClassName(calleeOwner);
                                    Hit(calleeClass, caller);
                                    Hit(calleeClass + "#" + calleeMember, caller);
                                }
                                if (IsOverridable(callee))
                                    foreach (string key in Overrides(callee, overrides))
                                        Hit(key, caller);
                                foreach (Type arg in GenericArguments(callee))
                                    Hit(ProjectCode.ClassName(arg), caller);
                                if (StringDispatch.Contains(callee.Name) && callee.GetParameters().Any(p => p.ParameterType == typeof(string)))
                                    dispatchesByName = true;
                                break;
                            case FieldInfo field when field.DeclaringType != null && !ProjectCode.IsGenerated(field.DeclaringType):
                                Hit(ProjectCode.ClassName(field.DeclaringType), caller);
                                break;
                            case Type t when !ProjectCode.IsGenerated(t):
                                Hit(ProjectCode.ClassName(t), caller);
                                break;
                            case string s when ins.OpCode == OpCodes.Ldstr:
                                (literals ??= new List<string>()).Add(s);
                                break;
                        }
                    }
                    if (dispatchesByName && literals != null)
                        foreach (string name in literals)
                            Hit("*#" + name, caller);
                }
            }

            foreach (KeyValuePair<string, string> reference in AssetReferences())
                Hit(reference.Key, reference.Value);
        }

        static void AddCaller(Finding f, string caller)
        {
            int hash = caller.IndexOf('#');
            if (hash > 0)
                caller = caller.Substring(0, hash) + "." + caller.Substring(hash + 1);
            if (f.ReferencedBy.Count < MaxCallers && !f.ReferencedBy.Contains(caller))
                f.ReferencedBy.Add(caller);
        }

        static bool IsOverridable(MethodBase m) =>
            (m.IsVirtual && !m.IsFinal) || m.IsAbstract || (m.DeclaringType != null && m.DeclaringType.IsInterface);

        // Keys of every project override/implementation a virtual or interface call can land in.
        static List<string> Overrides(MethodBase callee, Dictionary<string, List<string>> cache)
        {
            Type declaring = callee.DeclaringType.IsGenericType ? callee.DeclaringType.GetGenericTypeDefinition() : callee.DeclaringType;
            string cacheKey = declaring.AssemblyQualifiedName + "|" + callee.Name;
            if (cache.TryGetValue(cacheKey, out List<string> keys))
                return keys;
            keys = new List<string>();
            foreach (Type t in ProjectCode.Types)
            {
                if (t == declaring || ProjectCode.IsGenerated(t) || !Derives(t, declaring))
                    continue;
                foreach (MethodInfo m in t.GetMethods(ProjectCode.AllDeclared))
                    if (m.Name == callee.Name || m.Name.EndsWith("." + callee.Name, StringComparison.Ordinal))
                        keys.Add(ProjectCode.ClassName(t) + "#" + ProjectCode.MemberKey(m.Name));
            }
            cache[cacheKey] = keys;
            return keys;
        }

        static bool Derives(Type t, Type baseDefinition)
        {
            if (baseDefinition.IsInterface)
                return t.GetInterfaces().Any(i => (i.IsGenericType ? i.GetGenericTypeDefinition() : i) == baseDefinition);
            for (Type b = t.BaseType; b != null; b = b.BaseType)
                if ((b.IsGenericType ? b.GetGenericTypeDefinition() : b) == baseDefinition)
                    return true;
            return false;
        }

        static IEnumerable<Type> GenericArguments(MethodBase m)
        {
            IEnumerable<Type> args = m.IsGenericMethod && !m.IsGenericMethodDefinition ? m.GetGenericArguments() : Enumerable.Empty<Type>();
            if (m.DeclaringType != null && m.DeclaringType.IsGenericType && !m.DeclaringType.IsGenericTypeDefinition)
                args = args.Concat(m.DeclaringType.GetGenericArguments());
            return args.Where(a => !a.IsGenericParameter);
        }

        // Inspector-wired UnityEvent calls and animation events, as (target key, asset path).
        static IEnumerable<KeyValuePair<string, string>> AssetReferences()
        {
            foreach (string file in AssetFiles(EventAssetPatterns.Concat(AnimationAssetPatterns)))
            {
                DateTime time = File.GetLastWriteTimeUtc(file);
                if (!assetRefCache.TryGetValue(file, out KeyValuePair<DateTime, List<string>> cached) || cached.Key != time)
                {
                    cached = new KeyValuePair<DateTime, List<string>>(time, ParseAssetReferences(file));
                    assetRefCache[file] = cached;
                }
                string relative = file.Substring(ProjectRoot.Length + 1).Replace('\\', '/');
                foreach (string key in cached.Value)
                    yield return new KeyValuePair<string, string>(key, relative);
            }
        }

        static List<string> ParseAssetReferences(string file)
        {
            string text = File.ReadAllText(file);
            var keys = new List<string>();
            if (file.EndsWith(".unity", StringComparison.Ordinal) || file.EndsWith(".prefab", StringComparison.Ordinal))
            {
                foreach (Match m in PersistentCall.Matches(text))
                {
                    string type = m.Groups["type"].Success ? m.Groups["type"].Value.Trim() : string.Empty;
                    string member = ProjectCode.MemberKey(m.Groups["method"].Value);
                    keys.Add(type.Length > 0 ? LunaNameFromReflection(type) + "#" + member : "*#" + member);
                }
            }
            else
            {
                foreach (Match m in AnimationEvent.Matches(text))
                    keys.Add("*#" + m.Groups["method"].Value);
            }
            return keys.Distinct().ToList();
        }

        // "Ns.Outer+Inner`1" (UnityEvent target type) -> "Ns.Outer.Inner$1".
        static string LunaNameFromReflection(string typeName) => typeName.Replace('+', '.').Replace('`', '$');

        internal static IEnumerable<string> AssetFiles(IEnumerable<string> patterns)
        {
            string assets = Application.dataPath;
            return patterns.SelectMany(p => Directory.EnumerateFiles(assets, p, SearchOption.AllDirectories)).Distinct();
        }

        static Dictionary<string, Type> ProjectTypesByLunaName()
        {
            var map = new Dictionary<string, Type>(StringComparer.Ordinal);
            foreach (Type t in ProjectCode.Types)
                if (!ProjectCode.IsGenerated(t))
                    map[ProjectCode.ClassName(t)] = t;
            return map;
        }

        static HashSet<string> LoadBaseline(out string source)
        {
            Baseline saved = ReadSaved();
            if (saved?.entries != null)
            {
                source = $"{saved.source} ({new DateTime(saved.savedUtcTicks, DateTimeKind.Utc).ToLocalTime():yyyy-MM-dd HH:mm})";
                return new HashSet<string>(saved.entries, StringComparer.Ordinal);
            }

            // No upload made with this tool yet: compare with the committed luna.json.
            string committed = GitShow("HEAD:./" + LunaJsonFile);
            source = committed == null ? null : "luna.json in the last commit";
            return committed == null ? null : StrippedEntries(committed, out _);
        }

        static Baseline ReadSaved()
        {
            string path = Path.Combine(ProjectRoot, BaselineFile);
            return File.Exists(path) ? JsonUtility.FromJson<Baseline>(File.ReadAllText(path)) : null;
        }

        static void WriteBaseline(string source, HashSet<string> entries)
        {
            string path = Path.Combine(ProjectRoot, BaselineFile);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var data = new Baseline { source = source, savedUtcTicks = DateTime.UtcNow.Ticks, entries = entries.OrderBy(e => e, StringComparer.Ordinal).ToArray() };
            File.WriteAllText(path, JsonUtility.ToJson(data));
        }

        static string GitShow(string spec)
        {
            try
            {
                var info = new ProcessStartInfo("git", "show " + spec)
                {
                    WorkingDirectory = ProjectRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using (Process git = Process.Start(info))
                {
                    string output = git.StandardOutput.ReadToEnd();
                    git.WaitForExit(10000);
                    return git.ExitCode == 0 ? output : null;
                }
            }
            catch (Exception)
            {
                return null;                            // git not installed / not a repository.
            }
        }
    }
}
