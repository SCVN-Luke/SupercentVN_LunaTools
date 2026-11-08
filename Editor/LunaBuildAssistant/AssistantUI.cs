using System;
using UnityEditor;
using UnityEngine;

namespace Supercent.Util.Editor
{
    // Shared look of the Build Assistant, modelled on the Playworks plugin window:
    // dark sidebar navigation, large page headers with a (?) tooltip, segmented sub-tabs, stat tiles.
    public static class AssistantUI
    {
        static bool Pro => EditorGUIUtility.isProSkin;

        public static Color Sidebar => Pro ? new Color(0.15f, 0.15f, 0.15f) : new Color(0.74f, 0.74f, 0.74f);
        public static Color Selected => Pro ? new Color(0.27f, 0.27f, 0.27f) : new Color(0.64f, 0.64f, 0.64f);
        public static Color Selection => Pro ? new Color(0.17f, 0.36f, 0.53f) : new Color(0.23f, 0.45f, 0.83f);
        public static Color RowAlt => Pro ? new Color(1f, 1f, 1f, 0.035f) : new Color(0f, 0f, 0f, 0.05f);
        public static Color Track => Pro ? new Color(1f, 1f, 1f, 0.06f) : new Color(0f, 0f, 0f, 0.08f);
        public static readonly Color Accent = new Color(0.27f, 0.52f, 0.86f);
        static readonly Color PrimaryTint = new Color(0.55f, 0.85f, 1.9f);     // Multiplies the button texture: a clear blue.
        public static readonly Color Good = new Color(0.40f, 0.75f, 0.42f);
        public static readonly Color Bad = new Color(0.92f, 0.42f, 0.36f);
        public static readonly Color Warn = new Color(0.96f, 0.74f, 0.26f);

        // Distinct, readable colors for asset kinds in the size bar.
        static readonly Color[] Palette =
        {
            new Color(0.27f, 0.52f, 0.86f), new Color(0.40f, 0.75f, 0.42f), new Color(0.96f, 0.74f, 0.26f),
            new Color(0.80f, 0.45f, 0.85f), new Color(0.35f, 0.78f, 0.80f), new Color(0.92f, 0.52f, 0.36f),
            new Color(0.62f, 0.62f, 0.66f),
        };

        static GUIStyle title, navItem, caption, tileValue, rowLabel, logo, barValue, selectedRowLabel;

        public static GUIStyle Title => title ??= new GUIStyle(EditorStyles.largeLabel) { fontSize = 16, margin = new RectOffset(4, 4, 6, 6) };
        public static GUIStyle Logo => logo ??= new GUIStyle(EditorStyles.boldLabel) { fontSize = 15, wordWrap = true };
        public static GUIStyle NavItem => navItem ??= new GUIStyle(EditorStyles.label) { padding = new RectOffset(20, 8, 0, 0), alignment = TextAnchor.MiddleLeft };
        public static GUIStyle Caption => caption ??= new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = Pro ? new Color(0.62f, 0.62f, 0.62f) : new Color(0.35f, 0.35f, 0.35f) } };
        public static GUIStyle TileValue => tileValue ??= new GUIStyle(EditorStyles.boldLabel) { fontSize = 15 };
        public static GUIStyle RowLabel => rowLabel ??= new GUIStyle(EditorStyles.label) { clipping = TextClipping.Clip };
        public static GUIStyle WrappedLabel => wrappedLabel ??= new GUIStyle(EditorStyles.label) { wordWrap = true, richText = false };
        public static GUIStyle WrappedCaption => wrappedCaption ??= new GUIStyle(Caption) { wordWrap = true };
        public static GUIStyle CaptionFoldout => captionFoldout ??= new GUIStyle(EditorStyles.foldout)
        {
            fontSize = EditorStyles.miniLabel.fontSize,
            normal = { textColor = Caption.normal.textColor },
            onNormal = { textColor = Caption.normal.textColor },
        };

        static GUIStyle wrappedLabel, wrappedCaption, captionFoldout;

        public static Color KindColor(int index) => Palette[Mathf.Clamp(index, 0, Palette.Length - 1)];

        public static GUIContent Icon(string name, string tooltip = null)
        {
            GUIContent c = EditorGUIUtility.IconContent(name);
            return new GUIContent(c) { tooltip = tooltip };
        }

        public static GUIContent StatusIcon(bool ok, string tooltip = null) =>
            Icon(ok ? "TestPassed" : "console.warnicon.sml", tooltip);

        // Page title with an optional (?) whose tooltip carries the explanation.
        public static void Header(string text, string help = null)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(text, Title);
                if (help != null)
                    GUILayout.Label(Icon("_Help", help), GUILayout.Width(20), GUILayout.Height(26));
                GUILayout.FlexibleSpace();
            }
        }

        public static void SubHeader(string text, string help = null)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(text, EditorStyles.boldLabel);
                if (help != null)
                    GUILayout.Label(Icon("_Help", help), GUILayout.Width(18), GUILayout.Height(18));
                GUILayout.FlexibleSpace();
            }
        }

        // Code | Shaders style switch.
        public static int Segmented(int selected, params string[] labels)
        {
            int result = GUILayout.Toolbar(selected, labels, GUILayout.Height(22));
            EditorGUILayout.Space(6);
            return result;
        }

        // Sidebar entry; badge > 0 shows a warning count like the plugin's "Build & upload ⚠ 8".
        public static bool NavButton(string text, bool selected, int badge)
        {
            Rect r = GUILayoutUtility.GetRect(GUIContent.none, NavItem, GUILayout.Height(28), GUILayout.ExpandWidth(true));
            if (selected)
                EditorGUI.DrawRect(r, Selected);
            GUI.Label(r, text, NavItem);
            if (badge > 0)
            {
                var badgeRect = new Rect(r.xMax - 46, r.y + 5, 40, 18);
                GUI.Label(badgeRect, new GUIContent(" " + badge, EditorGUIUtility.IconContent("console.warnicon.sml").image), EditorStyles.miniLabel);
            }
            EditorGUIUtility.AddCursorRect(r, MouseCursor.Link);
            return GUI.Button(r, GUIContent.none, GUIStyle.none);
        }

        // Caption above, value below, optional colored note (e.g. a size delta).
        public static void Tile(string captionText, string value, string note = null, Color? noteColor = null, string tooltip = null)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.MinWidth(110), GUILayout.Height(54)))
            {
                GUILayout.Label(new GUIContent(captionText, tooltip), Caption);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(value, TileValue);
                    if (!string.IsNullOrEmpty(note))
                    {
                        Color previous = GUI.contentColor;
                        if (noteColor.HasValue)
                            GUI.contentColor = noteColor.Value;
                        GUILayout.Label(note, EditorStyles.miniBoldLabel, GUILayout.ExpandWidth(false));
                        GUI.contentColor = previous;
                    }
                    GUILayout.FlexibleSpace();
                }
            }
        }

        public static Color DeltaColor(long delta) => delta > 0 ? Bad : Good;

        // Alternating list row: a horizontal group with a faint background on odd rows.
        // selected: Unity's selection blue with an accent bar on the left, like a selected Project/Hierarchy row.
        public static EditorGUILayout.HorizontalScope Row(int index, bool selected = false)
        {
            var scope = new EditorGUILayout.HorizontalScope(GUILayout.MinHeight(EditorGUIUtility.singleLineHeight + 2));
            if (Event.current.type == EventType.Repaint)
            {
                if (selected)
                {
                    EditorGUI.DrawRect(scope.rect, Selection);
                    EditorGUI.DrawRect(new Rect(scope.rect.x, scope.rect.y, 3f, scope.rect.height), Accent);
                }
                else if (index % 2 == 1)
                    EditorGUI.DrawRect(scope.rect, RowAlt);
            }
            return scope;
        }

        public static GUIStyle SelectedRowLabel => selectedRowLabel ??= new GUIStyle(EditorStyles.boldLabel)
        {
            clipping = TextClipping.Clip,
            normal = { textColor = Color.white },
        };

        // Horizontal bar: filled share of the track, with a label drawn on top.
        public static void Bar(string label, float fraction, Color color, string valueText, float height = 18f)
        {
            Rect r = GUILayoutUtility.GetRect(0, height, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(r, Track);
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width * Mathf.Clamp01(fraction), r.height), color);
            GUI.Label(new Rect(r.x + 6, r.y, r.width - 12, r.height), label, EditorStyles.miniBoldLabel);
            GUI.Label(new Rect(r.x + 6, r.y, r.width - 12, r.height), valueText, barValue ??= new GUIStyle(EditorStyles.miniBoldLabel) { alignment = TextAnchor.MiddleRight });
        }

        public static bool PrimaryButton(string text, float height = 32f)
        {
            Color previous = GUI.backgroundColor;
            GUI.backgroundColor = PrimaryTint;
            bool clicked = GUILayout.Button(text, GUILayout.Height(height));
            GUI.backgroundColor = previous;
            return clicked;
        }

        public static void Separator()
        {
            Rect r = GUILayoutUtility.GetRect(0, 1, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(r, RowAlt);
            EditorGUILayout.Space(4);
        }

        public static string Ago(DateTime local)
        {
            TimeSpan age = DateTime.Now - local;
            if (age.TotalMinutes < 1) return "just now";
            if (age.TotalHours < 1) return (int)age.TotalMinutes + " min ago";
            if (age.TotalDays < 1) return (int)age.TotalHours + " h ago";
            return local.ToString("MM-dd HH:mm");
        }
    }
}
