using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Supercent.Util.Editor
{
    // Per-asset Luna export settings (luna.json assets.rules), edited the way the plugin's Assets settings do:
    // each kind has a @default rule and overridesDictionary keyed by asset or folder path; the effective rule is
    // the override whose key prefixes the asset path (longest first), else the default.
    // The form edits a draft copy; Apply gives the asset its own override (copied from the effective rule when
    // it has none) with the draft's values and saves luna.json, Revert drops the draft.
    public static class LunaAssetRules
    {
        public enum Kind { Texture, Sound, Mesh, Font, Animation }

        const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        static readonly int[] Bitrates = { 32, 48, 64, 96, 128, 160, 192, 256 };
        // Max size presets, most used first: 256 sizes, then 512, 128, 64; 1024+ makes the playable heavy.
        // Anything else is Custom (typed in). null = separator.
        static readonly Vector2Int?[] TextureSizes =
        {
            new Vector2Int(256, 256), new Vector2Int(256, 512), new Vector2Int(256, 128), null,
            new Vector2Int(512, 512), new Vector2Int(512, 256), new Vector2Int(512, 128), null,
            new Vector2Int(128, 128), new Vector2Int(128, 256), new Vector2Int(128, 512), null,
            new Vector2Int(64, 64), null,
            new Vector2Int(1024, 1024), new Vector2Int(2048, 2048),
        };
        // Font atlases are square.
        static readonly Vector2Int?[] AtlasSizes =
        {
            new Vector2Int(256, 256), new Vector2Int(512, 512), new Vector2Int(1024, 1024), new Vector2Int(2048, 2048),
        };
        const int HeavySize = 1024;
        // Size fields switched to Custom while their values still match a preset (rule key + field).
        static readonly HashSet<string> CustomSizes = new HashSet<string>();
        static readonly HashSet<int> CompressedFormats = new HashSet<int> { 0, 1, 2 };   // PNG-8/24/32: PNGQuant/PNGCrush.
        static readonly HashSet<int> QualityFormats = new HashSet<int> { 3, 4 };         // JPEG, WEBP.

        static GUIStyle wrappedText;
        static GUIStyle WrappedText => wrappedText ??= new GUIStyle(EditorStyles.textArea) { wordWrap = true };

        public sealed class Rule
        {
            public Kind Kind;
            public string AssetPath;
            public object Values;               // The rule object the plugin reads (default or override).
            public object Draft;                // What the form edits; Apply writes it into Values.
            public string Owner;                // Override key in effect; null = the kind's default rule.
            public bool IsOwn => Owner == AssetPath;     // Also true for the kind's default rule (both null).
            public bool IsDefault => AssetPath == null;
            public bool IsDirty => JsonUtility.ToJson(Draft) != JsonUtility.ToJson(Values);
        }

        // Size breakdown kind (stats.json type title) -> rule kind.
        public static Kind? KindOf(string statsKind)
        {
            switch (statsKind)
            {
                case "textures": return Kind.Texture;
                case "sounds": return Kind.Sound;
                case "meshes": return Kind.Mesh;
                case "fonts": return Kind.Font;
                case "animation-clips": return Kind.Animation;
                default: return null;
            }
        }

        // The kind's @default rule; Apply writes it back like an asset's own settings.
        public static Rule GetDefault(Kind kind)
        {
            object values = GetField(Container(kind), "default");
            return new Rule { Kind = kind, Values = values, Draft = Clone(values) };
        }

        // Asset and folder paths that have their own settings.
        public static List<string> OverrideKeys(Kind kind) =>
            Overrides(Container(kind)).Keys.Cast<string>().OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

        // Gives an asset or folder its own settings, starting from the rule it has now.
        public static void AddOverride(Kind kind, string path)
        {
            Rule rule = Get(kind, path);
            if (!rule.IsOwn)
                Apply(rule);
        }

        public static void RemoveOverride(Kind kind, string path)
        {
            Overrides(Container(kind)).Remove(path);
            LunaPluginBridge.SaveConfig();
        }

        // One line for a rule list: "256×256 · PNG32 · PNGQuant", "96 kbps · Mono"...
        public static string Summary(Kind kind, object values)
        {
            switch (kind)
            {
                case Kind.Texture:
                    LunaTextureEncoder.Settings t = TextureSettings(values);
                    int format = (int)GetPath(values, "currentFormat");
                    string compression = CompressedFormats.Contains(format) ? " · " + TextureLabel(values, "textureCompressions", t.Compression)
                        : QualityFormats.Contains(format) ? " · q" + t.Quality : string.Empty;
                    return $"{t.MaxWidth}×{t.MaxHeight} · {TextureLabel(values, "textureFormats", t.Format)}{compression}";
                case Kind.Sound:
                    string[] channels = Options(values.GetType(), "soundChannels");
                    int channel = (int)GetPath(values, "channels");
                    return SoundBitrate(values) + " kbps" + (channel >= 0 && channel < channels.Length ? " · " + channels[channel] : string.Empty);
                case Kind.Font:
                    return $"glyph {GetPath(values, "data.size")} · {GetPath(values, "data.textureWidth")}×{GetPath(values, "data.textureHeight")}";
                case Kind.Mesh:
                    return Flags(values, ("halfPrecision", "half-precision"), ("useSimplification", "reduced"));
                default:
                    return Flags(values, ("halfPrecision", "half-precision"), ("stripCurves", "no redundant keys"));
            }
        }

        // Summary of a path's own settings, read from the live rule (no draft copy).
        public static string OverrideSummary(Kind kind, string path) => Summary(kind, Overrides(Container(kind))[path]);

        static string Flags(object values, params (string field, string label)[] flags)
        {
            string[] on = flags.Where(f => HasPath(values, f.field) && (bool)GetPath(values, f.field)).Select(f => f.label).ToArray();
            return on.Length == 0 ? "full precision" : string.Join(" · ", on);
        }

        public static Rule Get(Kind kind, string assetPath)
        {
            object container = Container(kind);
            IDictionary overrides = Overrides(container);
            string owner = overrides.Keys.Cast<string>()
                .Where(k => assetPath.StartsWith(k, StringComparison.Ordinal))
                .OrderByDescending(k => k.Length).FirstOrDefault();
            object values = owner != null ? overrides[owner] : GetField(container, "default");
            return new Rule { Kind = kind, AssetPath = assetPath, Owner = owner, Values = values, Draft = Clone(values) };
        }

        // Writes the draft as this asset's own settings and saves luna.json.
        public static void Apply(Rule rule)
        {
            object own = OwnValues(rule);
            CopyFields(rule.Draft, own);
            LunaPluginBridge.SaveConfig();
            rule.Draft = Clone(own);
        }

        public static void Revert(Rule rule) => rule.Draft = Clone(rule.Values);

        // Back to the folder/default rule.
        public static void Reset(Rule rule)
        {
            Overrides(Container(rule.Kind)).Remove(rule.AssetPath);
            LunaPluginBridge.SaveConfig();
        }

        // Texture settings of a rule object (usually the draft), in luna.json terms, for LunaTextureEncoder.
        public static LunaTextureEncoder.Settings TextureSettings(object values)
        {
            string[] formats = Options(values.GetType(), "textureFormats", value: true);
            string[] compressions = Options(values.GetType(), "textureCompressions", value: true);
            int format = (int)GetPath(values, "currentFormat"), compression = (int)GetPath(values, "currentCompression");
            return new LunaTextureEncoder.Settings
            {
                MaxWidth = (int)GetPath(values, "maxWidth"),
                MaxHeight = (int)GetPath(values, "maxHeight"),
                Quality = (int)GetPath(values, "quality"),
                Format = format >= 0 && format < formats.Length ? formats[format] : null,
                Compression = compression >= 0 && compression < compressions.Length ? compressions[compression] : null,
            };
        }

        // Puts encoder settings (e.g. a compared variant) into the draft.
        public static void SetTextureSettings(Rule rule, LunaTextureEncoder.Settings s)
        {
            int format = Array.IndexOf(Options(rule.Draft.GetType(), "textureFormats", value: true), s.Format);
            int compression = Array.IndexOf(Options(rule.Draft.GetType(), "textureCompressions", value: true), s.Compression);
            SetPath(rule.Draft, "maxWidth", s.MaxWidth);
            SetPath(rule.Draft, "maxHeight", s.MaxHeight);
            SetPath(rule.Draft, "quality", s.Quality);
            if (format >= 0)
                SetPath(rule.Draft, "currentFormat", format);
            if (compression >= 0)
                SetPath(rule.Draft, "currentCompression", compression);
        }

        public static int SoundBitrate(object values) => (int)GetPath(values, "bitrate");

        // luna.json values a texture rule accepts (format: png8, png32, webp...; compression: quantSize, crush...).
        public static string[] TextureFormatValues(Rule rule) => Options(rule.Draft.GetType(), "textureFormats", value: true);
        public static string[] TextureCompressionValues(Rule rule) => Options(rule.Draft.GetType(), "textureCompressions", value: true);
        public static string[] SoundChannelNames(Rule rule) => Options(rule.Draft.GetType(), "soundChannels");

        // Puts sound settings into the draft; channels is a display name ("Mono"...). Returns why a value is refused, or null.
        public static string SetSoundSettings(Rule rule, int? bitrate, string channels)
        {
            if (bitrate.HasValue)
            {
                if (bitrate.Value < 8 || bitrate.Value > 320)
                    return "bitrate must be 8-320 kbps (the window offers " + string.Join(", ", Bitrates) + ").";
                SetPath(rule.Draft, "bitrate", bitrate.Value);
            }
            if (!string.IsNullOrEmpty(channels))
            {
                string[] names = SoundChannelNames(rule);
                int i = Array.FindIndex(names, n => string.Equals(n, channels, StringComparison.OrdinalIgnoreCase));
                if (i < 0)
                    return "channels must be one of: " + string.Join(", ", names) + ".";
                SetPath(rule.Draft, "channels", i);
            }
            return null;
        }

        // Display name of a texture setting value ("quantSize" -> "PNGQuant Small Size").
        public static string TextureLabel(object values, string table, string value)
        {
            int i = Array.IndexOf(Options(values.GetType(), table, value: true), value);
            return i >= 0 ? Options(values.GetType(), table)[i] : value;
        }

        // The settings form for one asset, like the plugin's "<asset> settings" panel. true when something changed.
        public static bool Draw(Rule rule)
        {
            EditorGUI.BeginChangeCheck();
            switch (rule.Kind)
            {
                case Kind.Texture: DrawTexture(rule); break;
                case Kind.Sound: DrawSound(rule); break;
                case Kind.Mesh: DrawMesh(rule); break;
                case Kind.Font: DrawFont(rule); break;
                case Kind.Animation: DrawAnimation(rule); break;
            }
            return EditorGUI.EndChangeCheck();
        }

        public static string Describe(Rule rule) =>
            rule.IsDefault ? "Default rule" : rule.IsOwn ? "Custom for this asset" : rule.Owner == null ? "Default rule" : "Folder rule: " + rule.Owner;

        static void DrawTexture(Rule rule)
        {
            SizeField(rule, new GUIContent("Max size", "Maximum allowed size (width × height); larger textures are scaled down to fit before encoding."),
                      "maxWidth", "maxHeight", TextureSizes);
            string[] formats = Options(rule.Draft.GetType(), "textureFormats");
            int format = Edit(rule, "currentFormat", (int v) => EditorGUILayout.Popup(
                new GUIContent("Format", "PNG32 (default) is lossless with RGB and alpha. WebP requires iOS 14 or higher."), v, formats));
            if (CompressedFormats.Contains(format))
            {
                string[] compressions = Options(rule.Draft.GetType(), "textureCompressions");
                Edit(rule, "currentCompression", (int v) => EditorGUILayout.Popup(
                    new GUIContent("PNG Compression", "Compress the PNG to reduce its size while keeping the image quality."), v, compressions));
            }
            if (QualityFormats.Contains(format))
                Edit(rule, "quality", (int v) => EditorGUILayout.IntSlider(new GUIContent("Quality", "Quality in percent, for JPEG/WebP textures."), v, 1, 100));
        }

        static void DrawSound(Rule rule)
        {
            string[] channels = Options(rule.Draft.GetType(), "soundChannels");
            Edit(rule, "channels", (int v) => EditorGUILayout.Popup("Channels", v, channels));
            Edit(rule, "bitrate", (int v) =>
            {
                int[] values = Bitrates.Contains(v) ? Bitrates : Bitrates.Append(v).OrderBy(b => b).ToArray();
                // No "/" in popup entries: Unity turns it into a submenu.
                return EditorGUILayout.IntPopup("Bitrate", v, values.Select(b => b + " kbps").ToArray(), values);
            });
            GUILayout.Label("Luna always encodes sounds as MP3.", AssistantUI.Caption);
        }

        static void DrawMesh(Rule rule)
        {
            Edit(rule, "halfPrecision", (bool v) => EditorGUILayout.ToggleLeft(new GUIContent("Store mesh in Half-precision floating-point format",
                "Use half-precision floats: vertex data takes 50% less space."), v));
            bool simplify = Edit(rule, "useSimplification", (bool v) => EditorGUILayout.ToggleLeft(new GUIContent("Reduce mesh complexity",
                "Lower the number of vertices while trying to keep the mesh's original shape."), v));
            if (!simplify)
                return;
            using (new EditorGUI.IndentLevelScope())
            {
                Edit(rule, "simplificationPercentage", (int v) => EditorGUILayout.IntSlider(new GUIContent("Reduction %", "Target percentage reduction of the vertex count"), v, 0, 100));
                Edit(rule, "linkCloseVertices", (bool v) => EditorGUILayout.ToggleLeft(new GUIContent("Link close vertices",
                    "During reduction, treat vertices that are close to each other as one."), v));
            }
        }

        static void DrawFont(Rule rule)
        {
            Edit(rule, "data.size", (int v) => EditorGUILayout.DelayedIntField(new GUIContent("Glyph size", "Glyph size in the generated atlas"), v));
            SizeField(rule, new GUIContent("Texture size", "Size of the generated glyph atlas"), "data.textureWidth", "data.textureHeight", AtlasSizes);
            GUILayout.Label(new GUIContent("Alphabet", "Only these characters are baked into the font atlas"));
            Edit(rule, "data.alphabet", (string v) => EditorGUILayout.DelayedTextField(v ?? string.Empty, WrappedText, GUILayout.MinHeight(52)));
        }

        static void DrawAnimation(Rule rule)
        {
            Edit(rule, "halfPrecision", (bool v) => EditorGUILayout.ToggleLeft(new GUIContent("Store animation in Half-precision floating-point format",
                "Use half-precision floats: animation data takes 50% less space."), v));
            Edit(rule, "stripCurves", (bool v) => EditorGUILayout.ToggleLeft(new GUIContent("Remove redundant keyframes",
                "Drop keys and curves that do not change the animation."), v));
        }

        // Width × height as a preset combo; Custom (or values no preset has) shows the two number fields too.
        static void SizeField(Rule rule, GUIContent label, string widthPath, string heightPath, Vector2Int?[] presets)
        {
            if (!HasPath(rule.Draft, widthPath) || !HasPath(rule.Draft, heightPath))
                return;
            var size = new Vector2Int((int)GetPath(rule.Draft, widthPath), (int)GetPath(rule.Draft, heightPath));
            string key = rule.Kind + "|" + rule.AssetPath + "|" + widthPath;
            int preset = CustomSizes.Contains(key) ? -1 : Array.IndexOf(presets, (Vector2Int?)size);

            // Popup entries: presets ("" = separator), then Custom.
            var labels = new List<string>();
            var indexOf = new List<int>();
            for (int i = 0; i < presets.Length; i++)
            {
                labels.Add(presets[i] is Vector2Int p ? $"{p.x} × {p.y}" + (Mathf.Max(p.x, p.y) > HeavySize ? "  (heavy)" : string.Empty) : string.Empty);
                indexOf.Add(i);
            }
            labels.Add(string.Empty);
            indexOf.Add(-2);
            labels.Add("Custom…");
            indexOf.Add(-1);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(label);
                int shown = indexOf.IndexOf(preset);
                int picked = EditorGUILayout.Popup(shown, labels.ToArray(), GUILayout.Width(120));
                if (picked != shown && picked >= 0 && indexOf[picked] != -2)
                {
                    GUI.FocusControl(null);
                    if (indexOf[picked] == -1)
                        CustomSizes.Add(key);
                    else
                    {
                        CustomSizes.Remove(key);
                        size = presets[indexOf[picked]].Value;
                        SetPath(rule.Draft, widthPath, size.x);
                        SetPath(rule.Draft, heightPath, size.y);
                        GUI.changed = true;
                    }
                    preset = indexOf[picked];
                }
                if (preset < 0)
                {
                    GUILayout.Space(6);
                    Edit(rule, widthPath, (int v) => Mathf.Max(1, EditorGUILayout.DelayedIntField(v, GUILayout.Width(52))));
                    GUILayout.Label("x", GUILayout.Width(10));
                    Edit(rule, heightPath, (int v) => Mathf.Max(1, EditorGUILayout.DelayedIntField(v, GUILayout.Width(52))));
                    GUILayout.Label("pixels", AssistantUI.Caption, GUILayout.ExpandWidth(false));
                }
                GUILayout.FlexibleSpace();
            }
            if (preset < 0 && (!Mathf.IsPowerOfTwo(size.x) || !Mathf.IsPowerOfTwo(size.y)))
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(EditorGUIUtility.labelWidth);
                    GUILayout.Label("Not a power of two", AssistantUI.Caption);
                }
        }

        // Draws one draft field. Fields this plugin version does not have are skipped (e.g. mesh simplification before Luna 7.1).
        static T Edit<T>(Rule rule, string path, Func<T, T> draw)
        {
            if (!HasPath(rule.Draft, path))
                return default;
            T current = (T)GetPath(rule.Draft, path);
            T next = draw(current);
            if (!Equals(next, current))
                SetPath(rule.Draft, path, next);
            return next;
        }

        static object OwnValues(Rule rule)
        {
            if (rule.IsOwn)
                return rule.Values;
            IDictionary overrides = Overrides(Container(rule.Kind));
            Type type = overrides.GetType().GetGenericArguments()[1];
            object own = Activator.CreateInstance(type);
            CopyFields(rule.Values, own);
            SetPath(own, "name", rule.AssetPath);
            overrides[rule.AssetPath] = own;
            rule.Values = own;
            rule.Owner = rule.AssetPath;
            return own;
        }

        // Value fields are shared as is; nested [Serializable] data (FontData) is cloned.
        static object Copy(object value) =>
            value == null || value is string || value.GetType().IsValueType ? value : JsonUtility.FromJson(JsonUtility.ToJson(value), value.GetType());

        static object Clone(object values)
        {
            object copy = Activator.CreateInstance(values.GetType());
            CopyFields(values, copy);
            if (FindField(values.GetType(), "name") != null)
                SetPath(copy, "name", GetPath(values, "name"));
            return copy;
        }

        // Every instance field but the override key, including private serialized ones of base classes.
        static void CopyFields(object from, object to)
        {
            for (Type t = from.GetType(); t != null && t != typeof(object); t = t.BaseType)
                foreach (FieldInfo f in t.GetFields(Instance))
                    if (f.Name != "name" && !f.IsLiteral && !f.IsStatic && FindField(to.GetType(), f.Name) != null)
                        f.SetValue(to, Copy(f.GetValue(from)));
        }

        static object Container(Kind kind)
        {
            string field = kind == Kind.Texture ? "texture" : kind == Kind.Sound ? "sound" : kind == Kind.Mesh ? "meshes" : kind == Kind.Font ? "font" : "animations";
            return GetField(LunaPluginBridge.AssetRules(), field);
        }

        static IDictionary Overrides(object container) => (IDictionary)GetField(container, "overridesDictionary");

        // An option table such as Texture.textureFormats: each entry holds a display name then the luna.json value.
        static string[] Options(Type ruleType, string field, bool value = false)
        {
            for (Type t = ruleType; t != null; t = t.BaseType)
            {
                FieldInfo table = t.GetField(field, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (table?.GetValue(null) is Array entries)
                {
                    FieldInfo[] strings = entries.GetType().GetElementType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .Where(f => f.FieldType == typeof(string)).ToArray();
                    FieldInfo pick = strings[value && strings.Length > 1 ? 1 : 0];
                    return entries.Cast<object>().Select(e => (string)pick.GetValue(e)).ToArray();
                }
            }
            throw new MissingFieldException(ruleType.Name, field);
        }

        static bool HasPath(object target, string path)
        {
            foreach (string part in path.Split('.'))
            {
                FieldInfo f = FindField(target.GetType(), part);
                if (f == null)
                    return false;
                target = f.GetValue(target);
            }
            return true;
        }

        static object GetPath(object target, string path)
        {
            foreach (string part in path.Split('.'))
                target = GetField(target, part);
            return target;
        }

        static void SetPath(object target, string path, object value)
        {
            string[] parts = path.Split('.');
            for (int i = 0; i < parts.Length - 1; i++)
                target = GetField(target, parts[i]);
            FieldOf(target.GetType(), parts[parts.Length - 1]).SetValue(target, value);
        }

        static object GetField(object target, string name) => FieldOf(target.GetType(), name).GetValue(target);

        static FieldInfo FieldOf(Type type, string name) => FindField(type, name) ?? throw new MissingFieldException(type.Name, name);

        static FieldInfo FindField(Type type, string name)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, Instance);
                if (f != null)
                    return f;
            }
            return null;
        }
    }
}
