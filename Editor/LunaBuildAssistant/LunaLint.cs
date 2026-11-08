using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Supercent.Util.Editor
{
    // Known places where Luna's engine behaves differently from Unity. Each rule is a class implementing
    // ILunaLintRule anywhere in an Editor assembly; the runner finds them through TypeCache, so adding
    // a newly found Luna quirk means adding one rule class with its fix text.
    public interface ILunaLintRule
    {
        string Id { get; }
        string Title { get; }
        string Fix { get; }                                 // Shown under the rule: the team's known workaround.
        IEnumerable<LunaLint.Issue> Run();
    }

    // A rule whose issues the Assistant can fix in place (Checks page: "Fix all").
    public interface ILunaLintFixable : ILunaLintRule
    {
        string FixLabel { get; }                            // Button text, e.g. "Turn off probes".
        string FixConfirm { get; }                          // Dialog text before applying.
        void Apply(IList<LunaLint.Issue> issues);
    }

    public static class LunaLint
    {
        const string IgnoreFile = "Library/SupercentVN/LunaLintIgnore.txt";

        public sealed class Issue
        {
            public string RuleId;
            public string Message;
            public string AssetPath;                        // Asset to open, or null when Type is set.
            public Type Type;
            public string Member;
            public string Key => RuleId + "|" + (AssetPath ?? (Type == null ? string.Empty : ProjectCode.ClassName(Type))) + "|" + Member + "|" + Message;

            public void Open()
            {
                if (Type != null && ProjectCode.OpenSource(Type, Member))
                    return;
                UnityEngine.Object asset = AssetPath == null ? null : AssetDatabase.LoadMainAssetAtPath(AssetPath);
                if (asset != null)
                {
                    EditorGUIUtility.PingObject(asset);
                    AssetDatabase.OpenAsset(asset);
                }
            }
        }

        public sealed class Result
        {
            public readonly List<ILunaLintRule> Rules = new List<ILunaLintRule>();
            public readonly List<Issue> Issues = new List<Issue>();
            public readonly List<string> RuleErrors = new List<string>();
            public int Ignored;
        }

        static string IgnorePath => Path.Combine(Directory.GetParent(Application.dataPath).FullName, IgnoreFile);

        public static Result Run()
        {
            var result = new Result();
            HashSet<string> ignored = LoadIgnored();
            foreach (Type t in TypeCache.GetTypesDerivedFrom<ILunaLintRule>().Where(t => !t.IsAbstract).OrderBy(t => t.FullName))
            {
                ILunaLintRule rule;
                try
                {
                    rule = (ILunaLintRule)Activator.CreateInstance(t);
                    result.Rules.Add(rule);
                    foreach (Issue issue in rule.Run())
                    {
                        issue.RuleId = rule.Id;
                        if (ignored.Contains(issue.Key))
                            result.Ignored++;
                        else
                            result.Issues.Add(issue);
                    }
                }
                catch (Exception e)
                {
                    result.RuleErrors.Add(t.Name + ": " + e.Message);
                    Debug.LogException(e);
                }
            }
            return result;
        }

        public static void Ignore(Issue issue)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(IgnorePath));
            File.AppendAllText(IgnorePath, issue.Key.Replace('\n', ' ') + "\n");
        }

        public static void ClearIgnored()
        {
            if (File.Exists(IgnorePath))
                File.Delete(IgnorePath);
        }

        static HashSet<string> LoadIgnored() =>
            new HashSet<string>(File.Exists(IgnorePath) ? File.ReadAllLines(IgnorePath) : new string[0], StringComparer.Ordinal);
    }

    // Luna sometimes loses a coroutine started from inside another coroutine without being awaited.
    public sealed class NestedCoroutineRule : ILunaLintRule
    {
        public string Id => "LUNA001";
        public string Title => "Coroutine started inside a coroutine without yield return";
        public string Fix => "Luna does not always run StartCoroutine(B()) called inside coroutine A. " +
                             "Use yield return StartCoroutine(B()) (A then waits for B), or start B from a non-coroutine method.";

        public IEnumerable<LunaLint.Issue> Run()
        {
            foreach (Type t in ProjectCode.Types.Where(t => ProjectCode.IsGenerated(t) && typeof(System.Collections.IEnumerator).IsAssignableFrom(t)))
            {
                MethodInfo moveNext = t.GetMethod("MoveNext", ProjectCode.AllDeclared);
                if (moveNext == null || !ProjectCode.TryOwner(moveNext, out Type owner, out string member))
                    continue;

                int count = 0;
                IlReader.Instruction previous = default;
                foreach (IlReader.Instruction ins in IlReader.Read(moveNext))
                {
                    if (ins.OpCode == OpCodes.Pop && previous.Operand is MethodInfo call
                        && call.Name == "StartCoroutine" && call.DeclaringType == typeof(MonoBehaviour))
                        count++;                    // Result thrown away: B runs detached from A.
                    previous = ins;
                }
                if (count > 0)
                    yield return new LunaLint.Issue
                    {
                        Type = owner,
                        Member = member,
                        Message = $"{ProjectCode.ShortClassName(owner)}.{member}: {count} StartCoroutine call(s) not yielded",
                    };
            }
        }
    }

    // LineRenderer drawing in local space disappears on Luna once its transform leaves the camera view.
    public sealed class LineRendererLocalSpaceRule : ILunaLintRule
    {
        static readonly Regex Block = new Regex(@"^--- !u!(?<class>\d+) &(?<id>-?\d+)[^\n]*\n(?<body>(?:(?!^--- ).*\n?)*)", RegexOptions.Multiline);
        static readonly Regex GameObjectRef = new Regex(@"m_GameObject: \{fileID: (?<id>-?\d+)\}");
        static readonly Regex Name = new Regex(@"^\s*m_Name: (?<name>.*)$", RegexOptions.Multiline);
        const string LineRendererClassId = "120";
        const string GameObjectClassId = "1";

        public string Id => "LUNA002";
        public string Title => "LineRenderer in local space";
        public string Fix => "Luna does not draw a LineRenderer whose transform pivot is far from or outside the camera view. " +
                             "Tick Use World Space, set world positions, and keep the object's pivot inside the camera view.";

        public IEnumerable<LunaLint.Issue> Run()
        {
            string root = Directory.GetParent(Application.dataPath).FullName;
            foreach (string file in StrippedCodeCheck.AssetFiles(new[] { "*.unity", "*.prefab" }))
            {
                string text = File.ReadAllText(file);
                if (!text.Contains("--- !u!" + LineRendererClassId + " "))
                    continue;

                Dictionary<string, string> names = null;
                foreach (Match block in Block.Matches(text))
                {
                    if (block.Groups["class"].Value != LineRendererClassId || !block.Groups["body"].Value.Contains("m_UseWorldSpace: 0"))
                        continue;
                    names ??= Block.Matches(text).Cast<Match>()
                        .Where(b => b.Groups["class"].Value == GameObjectClassId)
                        .ToDictionary(b => b.Groups["id"].Value, b => Name.Match(b.Groups["body"].Value).Groups["name"].Value.Trim());
                    string goId = GameObjectRef.Match(block.Groups["body"].Value).Groups["id"].Value;
                    string path = file.Substring(root.Length + 1).Replace('\\', '/');
                    yield return new LunaLint.Issue
                    {
                        AssetPath = path,
                        Message = $"{path}: {(names.TryGetValue(goId, out string go) ? go : "LineRenderer")} uses local space",
                    };
                }
            }
        }
    }

    // Luna applies AnimatorOverrideController clips to plain states only: a clip inside a blend tree keeps
    // the base controller's clip, so the overridden model stands still there (BulldozerMaster Boar Idle/Run).
    public sealed class OverrideClipInBlendTreeRule : ILunaLintRule
    {
        public string Id => "LUNA003";
        public string Title => "AnimatorOverrideController overrides a clip inside a blend tree";
        public string Fix => "Luna keeps the base controller's clip inside blend trees, so those states play the wrong clip " +
                             "(no matching bones: the model slides in its last or default pose). Duplicate the base controller " +
                             "and assign the clips directly, or make those states plain states (e.g. Idle/Run switched by a bool).";

        public IEnumerable<LunaLint.Issue> Run()
        {
            foreach (string guid in AssetDatabase.FindAssets("t:AnimatorOverrideController", new[] { "Assets" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var overrideController = AssetDatabase.LoadAssetAtPath<AnimatorOverrideController>(path);
                RuntimeAnimatorController source = overrideController != null ? overrideController.runtimeAnimatorController : null;
                while (source is AnimatorOverrideController nested)
                    source = nested.runtimeAnimatorController;
                if (!(source is UnityEditor.Animations.AnimatorController controller))
                    continue;

                var blendTreeOf = new Dictionary<AnimationClip, string>();
                foreach (UnityEditor.Animations.AnimatorControllerLayer layer in controller.layers)
                    CollectBlendTreeClips(layer.stateMachine, blendTreeOf);
                if (blendTreeOf.Count == 0)
                    continue;

                var overrides = new List<KeyValuePair<AnimationClip, AnimationClip>>();
                overrideController.GetOverrides(overrides);
                string[] lost = overrides
                    .Where(o => o.Key != null && o.Value != null && o.Value != o.Key && blendTreeOf.ContainsKey(o.Key))
                    .Select(o => $"{blendTreeOf[o.Key]}: {o.Key.name} -> {o.Value.name}")
                    .ToArray();
                if (lost.Length > 0)
                    yield return new LunaLint.Issue
                    {
                        AssetPath = path,
                        Message = $"{path} (base {controller.name}): {string.Join(", ", lost)}",
                    };
            }
        }

        static void CollectBlendTreeClips(UnityEditor.Animations.AnimatorStateMachine machine, Dictionary<AnimationClip, string> blendTreeOf)
        {
            foreach (UnityEditor.Animations.ChildAnimatorState child in machine.states)
                if (child.state.motion is UnityEditor.Animations.BlendTree tree)
                    CollectClips(tree, child.state.name, blendTreeOf);
            foreach (UnityEditor.Animations.ChildAnimatorStateMachine sub in machine.stateMachines)
                CollectBlendTreeClips(sub.stateMachine, blendTreeOf);
        }

        static void CollectClips(UnityEditor.Animations.BlendTree tree, string state, Dictionary<AnimationClip, string> blendTreeOf)
        {
            foreach (UnityEditor.Animations.ChildMotion child in tree.children)
            {
                if (child.motion is UnityEditor.Animations.BlendTree inner)
                    CollectClips(inner, state, blendTreeOf);
                else if (child.motion is AnimationClip clip && !blendTreeOf.ContainsKey(clip))
                    blendTreeOf[clip] = state;
            }
        }
    }

    // At speed 0 Luna's Animator.AddTime calls CurveSampler.reapply instead of apply, and reapply skips the
    // missing-target check: a clip curve whose bone path does not exist throws every frame (BulldozerMaster end card).
    public sealed class AnimatorSpeedZeroRule : ILunaLintRule
    {
        public string Id => "LUNA004";
        public string Title => "Animator.speed set to 0";
        public string Fix => "On Luna an Animator at speed 0 re-applies its pose without skipping curves whose target is missing, " +
                             "and throws \"Cannot read properties of null (reading 'm_LocalRotation')\" every frame (the Editor does not). " +
                             "To freeze a character, set animator.enabled = false instead: the pose stays where it is.";

        public IEnumerable<LunaLint.Issue> Run()
        {
            foreach (Type t in ProjectCode.Types)
                foreach (MethodBase method in t.GetMethods(ProjectCode.AllDeclared).Cast<MethodBase>().Concat(t.GetConstructors(ProjectCode.AllDeclared)))
                {
                    int count = CountZeroSpeed(method);
                    if (count == 0 || !ProjectCode.TryOwner(method, out Type owner, out string member))
                        continue;
                    yield return new LunaLint.Issue
                    {
                        Type = owner,
                        Member = member,
                        Message = $"{ProjectCode.ShortClassName(owner)}.{member}: {count} Animator.speed = 0",
                    };
                }
        }

        // Literal 0 stored straight into Animator.speed (speed computed at runtime is not flagged).
        public static int CountZeroSpeed(MethodBase method)
        {
            int count = 0;
            IlReader.Instruction previous = default;
            foreach (IlReader.Instruction ins in IlReader.Read(method))
            {
                if (ins.Operand is MethodInfo call && call.Name == "set_speed" && call.DeclaringType == typeof(Animator)
                    && previous.OpCode == OpCodes.Ldc_R4 && previous.Operand is float value && value == 0f)
                    count++;
                previous = ins;
            }
            return count;
        }
    }
}
