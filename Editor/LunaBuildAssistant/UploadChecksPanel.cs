using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Supercent.Util.Editor
{
    // The Build Assistant's Checks page: stripped code still called by running code, and Luna lint.
    // Reports are recomputed outside OnGUI (Refresh from OnInspectorUpdate) so a frame's Layout and
    // Repaint always draw the same rows.
    public sealed class UploadChecksPanel
    {
        const string PlayworksMenu = "Tools/Unity Playworks Plugin";
        const string StrippedHelp =
            "Luna strips every method that never ran in Build Develop. Listed here: methods newly stripped since the " +
            "last upload that running code still calls, usually from a branch nobody tested.\n\n" +
            "Play that path in Build Develop, Keep it (never stripped), or mark it Strip OK.";
        const string LintHelp = "Code and scene patterns that behave differently on Luna than in Unity.\n\n" +
                                "Hints (collapsed, listed last) are differences that only matter on some playables; " +
                                "they are not counted in the badge or the upload warnings.";

        StrippedCodeCheck.Report stripped;
        LunaLint.Result lint;
        bool strippedStale = true, lintStale = true;
        DateTime checkedLunaJsonTime;
        bool showUnreferenced;
        readonly HashSet<string> expandedHints = new HashSet<string>();     // Hint rules the user opened (collapsed by default).
        Vector2 scroll;

        public int Tab;                                 // 0 = stripped code, 1 = Luna checks.
        public Action<Exception> OnError;

        public int StrippedRiskCount => stripped?.Referenced.Count ?? 0;
        public int LintIssueCount => lint?.WarningCount ?? 0;     // Warnings only: hints never count.
        public int LintHintCount => lint?.HintCount ?? 0;
        public int IssueCount => StrippedRiskCount + LintIssueCount;

        public void Invalidate()
        {
            strippedStale = true;
            lintStale = true;
        }

        // Recomputes stale reports; true when something changed and the window should repaint.
        public bool Refresh()
        {
            bool changed = false;
            DateTime lunaJsonTime = File.Exists(StrippedCodeCheck.LunaJsonPath) ? File.GetLastWriteTimeUtc(StrippedCodeCheck.LunaJsonPath) : default;
            if (strippedStale || checkedLunaJsonTime != lunaJsonTime)
            {
                stripped = Guard(StrippedCodeCheck.Run);
                checkedLunaJsonTime = lunaJsonTime;
                strippedStale = false;
                changed = true;
            }
            if (lintStale)
            {
                lint = Guard(LunaLint.Run);
                lintStale = false;
                changed = true;
            }
            return changed;
        }

        // Lines for the confirmation dialog before uploading; empty when every check is clean.
        public List<string> Warnings()
        {
            Refresh();
            var lines = new List<string>();
            if (StrippedRiskCount > 0)
                lines.Add($"{StrippedRiskCount} stripped method(s) still called by running code (e.g. {stripped.Referenced[0].ShortLabel}).");
            if (LintIssueCount > 0)
                lines.Add($"{LintIssueCount} Luna check issue(s) (e.g. {lint.Warnings.First().Message}).");
            return lines;
        }

        public string StrippedSummary => stripped == null ? "Checking…" : StrippedRiskCount == 0 ? "Nothing new is missing" : $"{StrippedRiskCount} still called";
        public string LintSummary => lint == null ? "Checking…"
            : (LintIssueCount == 0 ? "No known issue" : $"{LintIssueCount} issue(s)") + (LintHintCount > 0 ? $", {LintHintCount} hint(s)" : string.Empty);

        public void Draw()
        {
            AssistantUI.Header("Checks");
            Tab = AssistantUI.Segmented(Tab, $"Stripped code ({StrippedRiskCount})", $"Luna checks ({LintIssueCount})");
            scroll = EditorGUILayout.BeginScrollView(scroll);
            if (Tab == 0)
                DrawStripped();
            else
                DrawLint();
            EditorGUILayout.EndScrollView();
        }

        void DrawStripped()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                AssistantUI.SubHeader("Stripped but still called", StrippedHelp);
                if (GUILayout.Button(new GUIContent("Runtime Analysis", "Every stripped class and method, in the plugin"), EditorStyles.miniButtonLeft))
                    EditorApplication.ExecuteMenuItem(PlayworksMenu);
                if (GUILayout.Button(AssistantUI.Icon("Refresh", "Check again"), EditorStyles.miniButtonRight, GUILayout.Width(26)))
                    strippedStale = true;
            }
            if (stripped == null)
            {
                GUILayout.Label(strippedStale ? "Checking…" : "Check failed, see the Console.", AssistantUI.Caption);
                return;
            }
            GUILayout.Label(stripped.BaselineSource == null ? "No upload yet: showing all stripped code" : "Compared with " + stripped.BaselineSource, AssistantUI.Caption);
            EditorGUILayout.Space(4);

            if (stripped.Referenced.Count == 0)
                Empty("Nothing new is stripped while still being called.");
            else
            {
                for (int i = 0; i < stripped.Referenced.Count; i++)
                    DrawFinding(stripped.Referenced[i], i, true);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(new GUIContent("Keep all", "Never strip these"), EditorStyles.miniButtonLeft, GUILayout.Width(70)))
                        Keep(stripped.Referenced);
                    if (GUILayout.Button(new GUIContent("Strip OK all", "They really are unused on the cloud build"), EditorStyles.miniButtonRight, GUILayout.Width(80)))
                        Accept(stripped.Referenced);
                }
            }

            if (stripped.Unreferenced.Count > 0)
            {
                EditorGUILayout.Space(8);
                showUnreferenced = EditorGUILayout.Foldout(showUnreferenced,
                    new GUIContent($"Newly stripped, not called ({stripped.Unreferenced.Count})", "Nothing that runs calls these: stripping them is safe."), true);
                if (showUnreferenced)
                {
                    for (int i = 0; i < stripped.Unreferenced.Count; i++)
                        DrawFinding(stripped.Unreferenced[i], i, false);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button("Strip OK all", EditorStyles.miniButton, GUILayout.Width(80)))
                            Accept(stripped.Unreferenced);
                    }
                }
            }
        }

        void DrawFinding(StrippedCodeCheck.Finding f, int index, bool referenced)
        {
            using (AssistantUI.Row(index))
            {
                GUILayout.Label(AssistantUI.StatusIcon(!referenced), GUILayout.Width(18), GUILayout.Height(18));
                string tooltip = f.Label + (referenced ? "\n\nCalled by:\n" + string.Join("\n", f.ReferencedBy) : string.Empty);
                GUILayout.Label(new GUIContent(f.ShortLabel, tooltip), AssistantUI.RowLabel, GUILayout.MinWidth(80));
                if (referenced)
                    GUILayout.Label(new GUIContent("← " + Short(f.ReferencedBy[0]), tooltip), AssistantUI.Caption, GUILayout.MinWidth(60));
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(AssistantUI.Icon("editicon.sml", "Open the code"), EditorStyles.miniButtonLeft, GUILayout.Width(26)))
                    ProjectCode.OpenSource(f.Type, f.Member);
                if (GUILayout.Button(new GUIContent("Keep", "Never strip it (same as unticking it in Runtime Analysis)"), EditorStyles.miniButtonMid, GUILayout.Width(44)))
                    Keep(new[] { f });
                if (GUILayout.Button(new GUIContent("Strip OK", "It really is unused on the cloud build: stop flagging it"), EditorStyles.miniButtonRight, GUILayout.Width(60)))
                    Accept(new[] { f });
            }
        }

        void DrawLint()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                AssistantUI.SubHeader("Known Luna differences", LintHelp);
                if (lint != null && lint.Ignored > 0
                    && GUILayout.Button(new GUIContent($"Ignored ({lint.Ignored})", "Show ignored issues again"), EditorStyles.miniButtonLeft))
                {
                    LunaLint.ClearIgnored();
                    lintStale = true;
                }
                if (GUILayout.Button(AssistantUI.Icon("Refresh", "Check again"), EditorStyles.miniButtonRight, GUILayout.Width(26)))
                    lintStale = true;
            }
            if (lint == null)
            {
                GUILayout.Label(lintStale ? "Checking…" : "Check failed, see the Console.", AssistantUI.Caption);
                return;
            }
            foreach (string error in lint.RuleErrors)
                EditorGUILayout.HelpBox("Rule failed: " + error, MessageType.Error);
            EditorGUILayout.Space(4);

            // Warnings first (most issues first), then hints.
            foreach (ILunaLintRule rule in lint.Rules.OrderBy(LunaLint.IsHint).ThenByDescending(r => lint.Issues.Count(i => i.RuleId == r.Id)))
            {
                List<LunaLint.Issue> issues = lint.Issues.Where(i => i.RuleId == rule.Id).ToList();
                bool hint = LunaLint.IsHint(rule);
                bool expanded = !hint || expandedHints.Contains(rule.Id);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(hint && issues.Count > 0 ? AssistantUI.Icon("console.infoicon.sml", "Hint: worth a look, not a bug on every playable")
                        : AssistantUI.StatusIcon(issues.Count == 0), GUILayout.Width(18), GUILayout.Height(18));
                    if (hint && issues.Count > 0)
                    {
                        var title = new GUIContent(rule.Title, rule.Fix);
                        Rect rect = GUILayoutUtility.GetRect(title, AssistantUI.CaptionFoldout,
                            GUILayout.Width(AssistantUI.CaptionFoldout.CalcSize(title).x + 4));     // Width of the text, so the help icon follows it.
                        bool open = EditorGUI.Foldout(rect, expanded, title, true, AssistantUI.CaptionFoldout);
                        if (open != expanded)
                        {
                            if (open)
                                expandedHints.Add(rule.Id);
                            else
                                expandedHints.Remove(rule.Id);
                            expanded = open;
                        }
                    }
                    else
                        GUILayout.Label(new GUIContent(rule.Title, rule.Fix), hint ? AssistantUI.Caption : EditorStyles.boldLabel);
                    GUILayout.Label(AssistantUI.Icon("_Help", rule.Id + "\n\n" + rule.Fix), GUILayout.Width(18), GUILayout.Height(18));
                    GUILayout.FlexibleSpace();
                    if (issues.Count > 0 && rule is ILunaLintFixable fixable
                        && GUILayout.Button(new GUIContent(fixable.FixLabel, fixable.FixConfirm), EditorStyles.miniButton)
                        && EditorUtility.DisplayDialog(rule.Title, fixable.FixConfirm, fixable.FixLabel, "Cancel"))
                    {
                        try
                        {
                            fixable.Apply(issues);
                        }
                        catch (Exception e)
                        {
                            Debug.LogException(e);
                        }
                        lintStale = true;
                        GUIUtility.ExitGUI();
                    }
                    GUILayout.Label(issues.Count == 0 ? "OK" : hint ? $"Hint · {issues.Count}" : issues.Count.ToString(), AssistantUI.Caption);
                }
                for (int i = 0; expanded && i < issues.Count; i++)
                {
                    LunaLint.Issue issue = issues[i];
                    using (AssistantUI.Row(i))
                    {
                        GUILayout.Space(22);
                        GUILayout.Label(new GUIContent(issue.Message, issue.Message), AssistantUI.RowLabel, GUILayout.MinWidth(80));
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button(AssistantUI.Icon("editicon.sml", "Open"), EditorStyles.miniButtonLeft, GUILayout.Width(26)))
                            issue.Open();
                        if (GUILayout.Button(new GUIContent("Ignore", "Hide this issue on this machine"), EditorStyles.miniButtonRight, GUILayout.Width(50)))
                        {
                            LunaLint.Ignore(issue);
                            lintStale = true;
                        }
                    }
                }
                EditorGUILayout.Space(8);
            }
        }

        static void Empty(string text)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                GUILayout.Label(AssistantUI.StatusIcon(true), GUILayout.Width(18), GUILayout.Height(18));
                GUILayout.Label(text);
            }
        }

        static string Short(string qualified)
        {
            string[] parts = qualified.Split('.');
            return parts.Length <= 2 ? qualified : parts[parts.Length - 2] + "." + parts[parts.Length - 1];
        }

        void Keep(IEnumerable<StrippedCodeCheck.Finding> findings)
        {
            try
            {
                int kept = LunaPluginBridge.KeepCode(new HashSet<string>(findings.SelectMany(f => f.Entries)));
                Debug.Log($"[LunaBuildAssistant] Kept {kept} luna.json entr{(kept == 1 ? "y" : "ies")} out of code stripping.");
            }
            catch (Exception e)
            {
                OnError?.Invoke(e);
            }
            strippedStale = true;
        }

        void Accept(IEnumerable<StrippedCodeCheck.Finding> findings)
        {
            StrippedCodeCheck.Accept(findings.SelectMany(f => f.Entries).ToList());
            strippedStale = true;
        }

        static T Guard<T>(Func<T> run) where T : class
        {
            try
            {
                return run();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return null;
            }
        }
    }
}
