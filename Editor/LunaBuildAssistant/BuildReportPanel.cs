using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Supercent.Util.Editor
{
    // The last build's problems, in the Assistant: errors in a red box with where to fix them,
    // the plugin's warnings as one quiet, collapsed line that never blocks anything.
    public sealed class BuildReportPanel
    {
        const int MaxDetailLines = 4;
        const int MaxWarnings = 60;
        const float BarWidth = 3f;

        bool showWarnings;

        public Action OpenPlayworks;     // Playground sign-in only exists in the plugin window.

        // False when the user dismissed the report.
        public bool Draw(BuildReport report)
        {
            if (report == null || report.IsEmpty)
                return true;
            if (report.Failed && !DrawErrors(report))
                return false;
            DrawWarnings(report);
            return true;
        }

        bool DrawErrors(BuildReport report)
        {
            bool keep = true;
            using (var box = new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (Event.current.type == EventType.Repaint)
                    EditorGUI.DrawRect(new Rect(box.rect.x, box.rect.y, BarWidth, box.rect.height), AssistantUI.Bad);

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(BarWidth + 2);
                    GUILayout.Label(AssistantUI.Icon("console.erroricon.sml"), GUILayout.Width(18), GUILayout.Height(18));
                    GUILayout.Label(report.Summary, EditorStyles.boldLabel);
                    GUILayout.FlexibleSpace();
                    if (report.LogPath != null && GUILayout.Button(new GUIContent("luna.log", "The full log of the Luna build"), EditorStyles.miniButton, GUILayout.Width(64)))
                        EditorUtility.OpenWithDefaultApp(report.LogPath);
                    if (GUILayout.Button(new GUIContent("×", "Dismiss"), EditorStyles.miniButton, GUILayout.Width(22)))
                        keep = false;
                }

                foreach (BuildIssue issue in report.Errors)
                    DrawError(issue);

                if (report.NeedsSignIn)
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Space(BarWidth + 24);
                        if (GUILayout.Button("Sign in…", GUILayout.Width(90)))
                            OpenPlayworks?.Invoke();
                        GUILayout.FlexibleSpace();
                    }
                else if (report.Errors.Count == 0)
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Space(BarWidth + 24);
                        GUILayout.Label(report.LogPath != null
                            ? "The plugin reported no error details. Open luna.log for the full build output."
                            : "The plugin reported no error details and wrote no luna.log. See the Console.", AssistantUI.WrappedCaption);
                    }
                GUILayout.Space(2);
            }
            EditorGUILayout.Space(8);
            return keep;
        }

        static void DrawError(BuildIssue issue)
        {
            EditorGUILayout.Space(2);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(BarWidth + 24);
                using (new EditorGUILayout.VerticalScope())
                {
                    GUILayout.Label(issue.Message, AssistantUI.WrappedLabel);
                    if (!string.IsNullOrEmpty(issue.Details))
                        GUILayout.Label(new GUIContent(Clip(issue.Details), issue.Details), AssistantUI.WrappedCaption);
                    string meta = string.Join("  ·  ", new[] { issue.Code, issue.Location }.Where(s => !string.IsNullOrEmpty(s)));
                    if (meta.Length > 0 || issue.CanOpen)
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            if (meta.Length > 0)
                                GUILayout.Label(meta, AssistantUI.Caption);
                            if (issue.CanOpen && EditorGUILayout.LinkButton("Open"))
                                issue.Open();
                            GUILayout.FlexibleSpace();
                        }
                }
            }
        }

        // One caption-sized foldout; opened, a compact list. Never blocks a build.
        void DrawWarnings(BuildReport report)
        {
            if (report.Warnings.Count == 0)
                return;
            showWarnings = EditorGUILayout.Foldout(showWarnings,
                new GUIContent(BuildReport.Plural(report.Warnings.Count, "Playworks warning") + " (do not block the build)",
                               "Project Diagnostics warnings of the last build: performance, ad network and best-practice hints."),
                true, AssistantUI.CaptionFoldout);
            if (showWarnings)
            {
                foreach (BuildIssue issue in report.Warnings.Take(MaxWarnings))
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Space(14);
                        string tooltip = string.Join("\n", new[] { issue.Details, issue.Code, issue.Location }.Where(s => !string.IsNullOrEmpty(s)));
                        GUILayout.Label(new GUIContent("•  " + issue.Message, tooltip), AssistantUI.WrappedCaption);
                        if (issue.CanOpen && EditorGUILayout.LinkButton("Open", GUILayout.ExpandWidth(false)))
                            issue.Open();
                    }
                if (report.Warnings.Count > MaxWarnings)
                    GUILayout.Label("      … " + (report.Warnings.Count - MaxWarnings) + " more", AssistantUI.Caption);
            }
            EditorGUILayout.Space(6);
        }

        static string Clip(string text)
        {
            string[] lines = text.Split('\n');
            return lines.Length <= MaxDetailLines ? text : string.Join("\n", lines.Take(MaxDetailLines)) + "\n…";
        }
    }
}
