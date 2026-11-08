using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Compilation;

namespace Supercent.Util.Editor
{
    // The project's own player code as Luna sees it: which types, how Luna names them, and where they live.
    public static class ProjectCode
    {
        public const BindingFlags AllDeclared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                                              | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        static readonly Regex GeneratedOwner = new Regex(@"^<+(?<owner>[^<>]+)>");
        static readonly Regex OverloadSuffix = new Regex(@"\$\d+$");

        static Type[] types;
        static Dictionary<string, string[]> sourcesByAssembly;

        // Player assemblies compiled from Assets/ (packages and editor code are not the project's playable code).
        public static Type[] Types
        {
            get
            {
                if (types != null)
                    return types;
                sourcesByAssembly = CompilationPipeline.GetAssemblies(AssembliesType.Player)
                    .Where(a => a.sourceFiles.Any(f => f.StartsWith("Assets/", StringComparison.Ordinal)))
                    .ToDictionary(a => a.name, a => a.sourceFiles);
                types = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => sourcesByAssembly.ContainsKey(a.GetName().Name))
                    .SelectMany(GetTypes)
                    .ToArray();
                return types;
            }
        }

        // Luna/Bridge class name: generic arity as $N, nested types joined with '.'.
        public static string ClassName(Type t)
        {
            if (t.IsGenericType && !t.IsGenericTypeDefinition)
                t = t.GetGenericTypeDefinition();
            string own = t.Name.Replace('`', '$');
            return t.DeclaringType != null ? ClassName(t.DeclaringType) + "." + own : (string.IsNullOrEmpty(t.Namespace) ? own : t.Namespace + "." + own);
        }

        // Luna class name without the namespace, for display.
        public static string ShortClassName(Type t)
        {
            string name = ClassName(t);
            return string.IsNullOrEmpty(t.Namespace) ? name : name.Substring(t.Namespace.Length + 1);
        }

        // Luna member name without its overload suffix: "Foo$2" -> "Foo", "Count#get" stays.
        public static string MemberBase(string lunaName)
        {
            int accessor = lunaName.IndexOf('#');
            string name = accessor >= 0 ? lunaName.Substring(0, accessor) : lunaName;
            return OverloadSuffix.Replace(name, string.Empty) + (accessor >= 0 ? lunaName.Substring(accessor) : string.Empty);
        }

        // C# member name -> Luna member base ("get_Foo" -> "Foo#get", ".ctor" -> "ctor").
        public static string MemberKey(string csName)
        {
            int dot = csName.LastIndexOf('.');
            if (dot > 0)                                   // Explicit interface implementation.
                csName = csName.Substring(dot + 1);
            if (csName == ".ctor" || csName == ".cctor" || csName == "ctor" || csName == "cctor")
                return "ctor";
            if (csName.StartsWith("get_", StringComparison.Ordinal))
                return csName.Substring(4) + "#get";
            if (csName.StartsWith("set_", StringComparison.Ordinal))
                return csName.Substring(4) + "#set";
            return csName;
        }

        // The user-written method a compiled method belongs to: iterator/async state machines,
        // lambdas and local functions are folded into the method that declares them.
        public static bool TryOwner(MethodBase method, out Type owner, out string member)
        {
            owner = method.DeclaringType;
            string name = method.Name;
            while (owner != null && IsGenerated(owner))
            {
                Match m = GeneratedOwner.Match(owner.Name);
                if (m.Success && !name.StartsWith("<", StringComparison.Ordinal))
                    name = m.Groups["owner"].Value;        // MoveNext of <Name>d__N.
                owner = owner.DeclaringType;
            }
            if (name.StartsWith("<", StringComparison.Ordinal))
            {
                Match m = GeneratedOwner.Match(name);
                if (!m.Success)
                {
                    member = null;
                    return false;
                }
                name = m.Groups["owner"].Value;
            }
            member = owner == null ? null : MemberKey(name);
            return owner != null;
        }

        public static bool IsGenerated(Type t) => t.Name.StartsWith("<", StringComparison.Ordinal);

        // Opens the source file declaring the type, at the member when it can be found.
        public static bool OpenSource(Type type, string member)
        {
            if (!TryLocate(type, member, out string file, out int line))
                return false;
            UnityEngine.Object asset = AssetDatabase.LoadMainAssetAtPath(file);
            return asset != null && AssetDatabase.OpenAsset(asset, line);
        }

        // Source file ("Assets/...") and 1-based line of the type, or of the member when it can be found.
        public static bool TryLocate(Type type, string member, out string file, out int line)
        {
            line = 1;
            file = SourceFile(type, out string text);
            if (file == null)
                return false;
            string name = member == null ? null : member.Split('#')[0];
            if (name == "ctor")
                name = ShortName(type);
            Match decl = DeclarationRegex(type).Match(text);
            int start = decl.Success ? decl.Index : 0;
            if (!string.IsNullOrEmpty(name))
            {
                Match m = new Regex(@"\b" + Regex.Escape(name) + @"\b\s*(\(|<|\{|=>|$)", RegexOptions.Multiline).Match(text, start);
                if (m.Success)
                    start = m.Index;
            }
            line += text.Take(start).Count(c => c == '\n');
            return true;
        }

        public static string SourceFile(Type type, out string text)
        {
            text = null;
            while (type.DeclaringType != null)
                type = type.DeclaringType;
            if (Types == null || !sourcesByAssembly.TryGetValue(type.Assembly.GetName().Name, out string[] files))
                return null;
            Regex decl = DeclarationRegex(type);
            string preferred = files.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f) == ShortName(type));
            foreach (string file in preferred != null ? new[] { preferred }.Concat(files) : files)
            {
                string content = File.Exists(file) ? File.ReadAllText(file) : string.Empty;
                if (decl.IsMatch(content))
                {
                    text = content;
                    return file;
                }
            }
            return null;
        }

        public static string ShortName(Type t)
        {
            int tick = t.Name.IndexOf('`');
            return tick >= 0 ? t.Name.Substring(0, tick) : t.Name;
        }

        static Regex DeclarationRegex(Type t) =>
            new Regex(@"\b(class|struct|interface|enum)\s+" + Regex.Escape(ShortName(t)) + @"\b");

        static IEnumerable<Type> GetTypes(System.Reflection.Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
        }
    }
}
