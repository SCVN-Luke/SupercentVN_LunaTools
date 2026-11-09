#if LUNA_TMP
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace Supercent.Util.Editor
{
    // TextMeshPro font tools. Luna ships a TMP font asset's atlas as it is: a static atlas must already hold every character
    // the texts show (missing ones render as nothing), and fewer fonts / smaller atlases make the playable lighter.
    public static partial class LunaMcp
    {
        static partial void RegisterFontTools()
        {
            Server.Register(new McpServer.Tool
            {
                Name = "unity_font_check",
                Description = "TextMeshPro fonts of the project: each font asset used by a prefab or an open scene (static / dynamic, atlas " +
                              "size, sampling point size, padding, the characters it holds, its source font file and whether that file is " +
                              "force-excluded), the materials texts use with it, characters texts show that the atlas lacks (with the texts), " +
                              "and texts whose material belongs to another font's atlas. Texts set from code are not seen: check the strings " +
                              "the code assigns. Use before replacing a font or rebuilding an atlas (unity_font_atlas_rebuild).",
                ReadOnly = true,
                InputSchema = McpServer.Schema(new Dictionary<string, object>
                {
                    { "font", McpServer.Prop("string", "Only this font asset (path or name)") },
                }),
                Run = args => McpServer.ToolResult.Json(OnMainThread(() => FontCheck(MiniJson.Str(args, "font")), ProjectScanTimeoutMs)),
            });

            Server.Register(new McpServer.Tool
            {
                Name = "unity_font_atlas_rebuild",
                Description = "Rebuilds a static TMP font asset's atlas in place (same asset, atlas and material, so every reference stays) with " +
                              "the given characters: the current ones, plus 'characters', plus (addMissing) those its texts lack. It keeps the " +
                              "atlas size and the padding / point-size ratio and picks the largest sampling point size that fits; materials " +
                              "on the atlas get the new gradient scale. Returns the atlas image. Changes the font asset: only after the user " +
                              "agreed; check the texts in Unity and in Build Develop.",
                ReadOnly = false,
                InputSchema = McpServer.Schema(new Dictionary<string, object>
                {
                    { "font", McpServer.Prop("string", "Font asset path (a static TMP_FontAsset)") },
                    { "characters", McpServer.Prop("string", "Characters to add (or the full set with replace=true), e.g. ABCDEFGHIJKLMNOPQRSTUVWXYZ") },
                    { "replace", McpServer.Prop("boolean", "Use exactly 'characters' instead of adding them to the current set", false) },
                    { "addMissing", McpServer.Prop("boolean", "Also add the characters this font's texts show but the atlas lacks (unity_font_check)", false) },
                    { "maxPointSize", McpServer.Prop("integer", "Largest sampling point size to try (default: the current one)") },
                    { "minPointSize", McpServer.Prop("integer", "Smallest point size accepted before giving up", 16) },
                    { "image", McpServer.Prop("boolean", "Return the rebuilt atlas image", true) },
                }, "font"),
                Run = args =>
                {
                    var data = OnMainThread(() => RebuildAtlas(args), ProjectScanTimeoutMs);
                    string png = data.TryGetValue("imagePng", out object p) ? (string)p : null;
                    data.Remove("imagePng");
                    var result = McpServer.ToolResult.Json(data);
                    return png == null ? result : result.WithImage(png, "image/png");
                },
            });
        }

        // ---- unity_font_check ----

        sealed class FontText
        {
            public string Owner;
            public TMP_Text Text;
        }

        // TMP texts of every prefab (outside Editor folders and TMP's examples) and of the open scenes.
        static List<FontText> ProjectTexts(out List<string> unloadedLunaScenes)
        {
            var texts = new List<FontText>();
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (IsEditorPath(path) || path.Contains("/TextMesh Pro/Examples"))
                    continue;
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null)
                    continue;
                foreach (TMP_Text t in prefab.GetComponentsInChildren<TMP_Text>(true))
                    if (OwnOrOverridden(t))
                        texts.Add(new FontText { Owner = path + " > " + Owner(t), Text = t });
            }
            var loaded = new HashSet<string>();
            for (int i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                var scene = EditorSceneManager.GetSceneAt(i);
                if (!scene.isLoaded)
                    continue;
                loaded.Add(scene.path);
                foreach (GameObject root in scene.GetRootGameObjects())
                    foreach (TMP_Text t in root.GetComponentsInChildren<TMP_Text>(true))
                        if (OwnOrOverridden(t))
                            texts.Add(new FontText { Owner = scene.path + " > " + Owner(t), Text = t });
            }
            unloadedLunaScenes = LunaPluginBridge.LunaScenes.Where(s => !loaded.Contains(s)).ToList();
            return texts;
        }

        const string MainTex = "_MainTex", GradientScale = "_GradientScale";

        // A text from a nested prefab is listed with its own prefab, unless this instance overrides its font, material or text.
        static bool OwnOrOverridden(TMP_Text t)
        {
            var source = PrefabUtility.GetCorrespondingObjectFromSource(t) as TMP_Text;
            return source == null || source.font != t.font || source.fontSharedMaterial != t.fontSharedMaterial || source.text != t.text;
        }

        static TMP_FontAsset FontOf(TMP_Text t) => t.font != null ? t.font : TMP_Settings.defaultFontAsset;

        // Characters a text shows: rich-text tags removed, case styles applied, line breaks and tabs ignored.
        static IEnumerable<char> Shown(TMP_Text t)
        {
            string s = t.text ?? string.Empty;
            if (t.richText)
                s = Regex.Replace(s, "<[^>]*>", string.Empty);
            if ((t.fontStyle & (FontStyles.UpperCase | FontStyles.SmallCaps)) != 0)
                s = s.ToUpperInvariant();
            else if ((t.fontStyle & FontStyles.LowerCase) != 0)
                s = s.ToLowerInvariant();
            return s.Where(c => c != '\n' && c != '\r' && c != '\t' && c != '​').Distinct();
        }

        static bool Holds(TMP_FontAsset font, char c, HashSet<TMP_FontAsset> seen = null)
        {
            if (font == null || !(seen ??= new HashSet<TMP_FontAsset>()).Add(font))
                return false;
            if (font.characterLookupTable != null && font.characterLookupTable.ContainsKey(c))
                return true;
            if (font.atlasPopulationMode == AtlasPopulationMode.Dynamic)
                return true;                            // Added from the source font when shown (in Unity).
            return (font.fallbackFontAssetTable ?? new List<TMP_FontAsset>()).Any(f => Holds(f, c, seen))
                   || (TMP_Settings.fallbackFontAssets ?? new List<TMP_FontAsset>()).Any(f => Holds(f, c, seen));
        }

        static string Characters(TMP_FontAsset font) =>
            new string(font.characterTable.Select(ch => (char)ch.unicode).OrderBy(c => c).ToArray());

        static Font SourceFont(TMP_FontAsset font) =>
            font.sourceFontFile != null ? font.sourceFontFile
                : new SerializedObject(font).FindProperty("m_SourceFontFile_EditorRef")?.objectReferenceValue as Font;

        static Dictionary<string, object> FontCheck(string only)
        {
            List<FontText> texts = ProjectTexts(out List<string> unloaded);
            List<string> excludes = LunaPluginBridge.PathList("assets", "excludes");
            var problems = new List<object>();
            var fonts = new List<object>();
            foreach (var group in texts.GroupBy(t => FontOf(t.Text)).Where(g => g.Key != null).OrderByDescending(g => g.Count()))
            {
                TMP_FontAsset font = group.Key;
                string path = AssetDatabase.GetAssetPath(font);
                if (!string.IsNullOrEmpty(only) && path.IndexOf(only, StringComparison.OrdinalIgnoreCase) < 0 &&
                    !string.Equals(font.name, only, StringComparison.OrdinalIgnoreCase))
                    continue;
                var missing = new Dictionary<char, List<string>>();
                foreach (FontText ft in group)
                {
                    foreach (char c in Shown(ft.Text).Where(c => !Holds(font, c)))
                    {
                        if (!missing.TryGetValue(c, out List<string> owners))
                            missing[c] = owners = new List<string>();
                        if (owners.Count < 5)
                            owners.Add(ft.Owner);
                    }
                    Material m = ft.Text.fontSharedMaterial;
                    if (m != null && m.HasProperty(MainTex) && font.atlasTexture != null && m.GetTexture(MainTex) != font.atlasTexture)
                        problems.Add(new Dictionary<string, object>
                        {
                            { "text", ft.Owner }, { "problem", $"material '{m.name}' samples another atlas than {font.name}" },
                        });
                }
                Font source = SourceFont(font);
                string sourcePath = source != null ? AssetDatabase.GetAssetPath(source) : null;
                fonts.Add(new Dictionary<string, object>
                {
                    { "font", path }, { "texts", group.Count() },
                    { "mode", font.atlasPopulationMode.ToString() },
                    { "atlas", font.atlasTexture != null ? $"{font.atlasTexture.width}x{font.atlasTexture.height}" : null },
                    { "atlasTextures", font.atlasTextures?.Length ?? 0 },
                    { "pointSize", font.faceInfo.pointSize }, { "padding", font.atlasPadding },
                    { "characters", Characters(font) }, { "characterCount", font.characterTable.Count },
                    { "sourceFont", sourcePath },
                    { "sourceFontForceExcluded", sourcePath != null && excludes.Any(e => Matches(sourcePath, e)) },
                    { "materials", group.Select(t => t.Text.fontSharedMaterial).Where(m => m != null).Distinct()
                        .Select(m => (object)$"{m.name} ({AssetDatabase.GetAssetPath(m)})").ToList() },
                    { "missing", missing.Select(kv => (object)new Dictionary<string, object>
                        {
                            { "char", kv.Key.ToString() }, { "code", "U+" + ((int)kv.Key).ToString("X4") }, { "texts", kv.Value },
                        }).ToList() },
                    { "note", font.atlasPopulationMode == AtlasPopulationMode.Static ? null
                        : "Dynamic: characters are added from the source font at run time in Unity; in Luna prefer a static atlas." },
                });
            }
            return new Dictionary<string, object>
            {
                { "fonts", fonts }, { "textCount", texts.Count }, { "problems", problems.Take(40).ToList() },
                { "scenesNotChecked", unloaded },
                { "note", "Texts of prefabs and open scenes only; strings assigned from code are not seen. A nested prefab's text is listed " +
                          "under its own prefab, and again where an instance overrides its font, material or text." },
            };
        }

        // ---- unity_font_atlas_rebuild ----

        static Dictionary<string, object> RebuildAtlas(Dictionary<string, object> args)
        {
            if (LunaBuildAssistantWindow.AgentBuildRunning || SafeIsBuilding())
                throw new InvalidOperationException("A build is running: rebuild the atlas after it finished.");
            string path = MiniJson.Str(args, "font")?.Trim();
            TMP_FontAsset font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path ?? string.Empty)
                ?? throw new InvalidOperationException("No TMP font asset at " + path);
            if (font.atlasPopulationMode != AtlasPopulationMode.Static)
                throw new InvalidOperationException("Only static font assets: a dynamic one adds characters from its source font by itself.");
            Font source = SourceFont(font) ?? throw new InvalidOperationException("The font asset has no source font file to render from.");
            Texture2D atlas = font.atlasTexture ?? throw new InvalidOperationException("The font asset has no atlas texture.");

            string before = Characters(font);
            var set = new StringBuilder(MiniJson.Bool(args, "replace", false) ? string.Empty : before);
            set.Append(MiniJson.Str(args, "characters") ?? string.Empty);
            if (MiniJson.Bool(args, "addMissing", false))
                foreach (FontText ft in ProjectTexts(out _).Where(t => FontOf(t.Text) == font))
                    foreach (char c in Shown(ft.Text))
                        if (!Holds(font, c))
                            set.Append(c);
            string chars = new string(set.ToString().Where(c => c != '\n' && c != '\r' && c != '\t').Distinct().OrderBy(c => c).ToArray());
            if (chars.Length == 0)
                throw new InvalidOperationException("No characters to put in the atlas.");

            int oldPoint = (int)font.faceInfo.pointSize, oldPadding = font.atlasPadding;
            double ratio = oldPoint > 0 ? (double)oldPadding / oldPoint : 0.1;
            int max = (int)MiniJson.Num(args, "maxPointSize", oldPoint), min = Math.Max(8, (int)MiniJson.Num(args, "minPointSize", 16));
            GlyphRenderMode mode = font.atlasRenderMode;
            TMP_FontAsset built = null;
            int point = 0, padding = 0;
            var tried = new List<string>();
            for (int p = max; p >= min && built == null; p -= p > 40 ? 4 : 2)
            {
                int pad = Math.Max(1, (int)Math.Round(p * ratio));
                TMP_FontAsset candidate = TMP_FontAsset.CreateFontAsset(source, p, pad, mode, atlas.width, atlas.height, AtlasPopulationMode.Dynamic, false);
                if (candidate.TryAddCharacters(chars, out string missing) && string.IsNullOrEmpty(missing))
                {
                    built = candidate;
                    point = p;
                    padding = pad;
                }
                else
                {
                    tried.Add($"{p}/{pad} lacks {missing}");
                    DestroyFont(candidate);
                }
            }
            if (built == null)
                throw new InvalidOperationException($"The characters do not fit a {atlas.width}x{atlas.height} atlas down to {min} pt ({string.Join("; ", tried.Take(4))}). " +
                                                    "Use fewer characters or a lower minPointSize.");
            try
            {
                Texture2D from = built.atlasTexture;
                if (from.format != atlas.format)
                    throw new InvalidOperationException($"Atlas format {atlas.format} differs from the rebuilt {from.format}.");
                var src = new SerializedObject(built);
                var dst = new SerializedObject(font);
                foreach (string name in new[] { "m_FaceInfo", "m_GlyphTable", "m_CharacterTable", "m_UsedGlyphRects", "m_FreeGlyphRects",
                                                "m_AtlasPadding", "m_AtlasRenderMode", "m_FontFeatureTable" })
                    dst.CopyFromSerializedProperty(src.FindProperty(name));
                dst.FindProperty("m_CreationSettings.pointSize").intValue = point;
                dst.FindProperty("m_CreationSettings.padding").intValue = padding;
                dst.FindProperty("m_CreationSettings.characterSequence").stringValue = chars;
                dst.ApplyModifiedPropertiesWithoutUndo();
                atlas.LoadRawTextureData(from.GetRawTextureData());
                atlas.Apply(false, false);
                EditorUtility.SetDirty(atlas);
            }
            finally
            {
                DestroyFont(built);
            }
            font.ReadFontAssetDefinition();
            EditorUtility.SetDirty(font);

            // Materials sampling this atlas: the SDF spread is padding + 1 texels.
            float gradient = padding + 1;
            var materials = new List<object>();
            IEnumerable<Material> candidates = AssetDatabase.FindAssets("t:Material", new[] { "Assets" })
                .SelectMany(g => AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GUIDToAssetPath(g)).OfType<Material>())
                .Concat(AssetDatabase.LoadAllAssetsAtPath(path).OfType<Material>()).Distinct();
            foreach (Material m in candidates)
                if (m.HasProperty(MainTex) && m.GetTexture(MainTex) == atlas && m.HasProperty(GradientScale))
                {
                    m.SetFloat(GradientScale, gradient);
                    EditorUtility.SetDirty(m);
                    materials.Add($"{m.name} ({AssetDatabase.GetAssetPath(m)})");
                }
            AssetDatabase.SaveAssets();
            Debug.Log($"[LunaBuildAssistant] Font atlas of {path} rebuilt by an agent: {chars.Length} characters, {point} pt, padding {padding}.");

            var data = new Dictionary<string, object>
            {
                { "font", path }, { "characters", chars }, { "characterCount", chars.Length },
                { "added", new string(chars.Where(c => before.IndexOf(c) < 0).ToArray()) },
                { "removed", new string(before.Where(c => chars.IndexOf(c) < 0).ToArray()) },
                { "pointSize", point }, { "padding", padding }, { "before", $"{oldPoint} pt, padding {oldPadding}" },
                { "atlas", $"{atlas.width}x{atlas.height}" }, { "materials", materials },
                { "hint", "Check the texts in Unity and in Build Develop. A smaller point size than before means softer glyphs; outline " +
                          "widths keep their look because the padding / point ratio is kept." },
            };
            if (MiniJson.Bool(args, "image", true))
                data["imagePng"] = AtlasPng(atlas);
            return data;
        }

        static void DestroyFont(TMP_FontAsset f)
        {
            if (f == null)
                return;
            if (f.atlasTextures != null)
                foreach (Texture2D t in f.atlasTextures)
                    if (t != null)
                        UnityEngine.Object.DestroyImmediate(t);
            if (f.material != null)
                UnityEngine.Object.DestroyImmediate(f.material);
            UnityEngine.Object.DestroyImmediate(f);
        }

        // The atlas as a grey PNG (SDF in the alpha channel).
        static string AtlasPng(Texture2D atlas)
        {
            Color32[] px = atlas.GetPixels32();
            for (int i = 0; i < px.Length; i++)
                px[i] = new Color32(px[i].a, px[i].a, px[i].a, 255);
            var image = new Texture2D(atlas.width, atlas.height, TextureFormat.RGBA32, false);
            try
            {
                image.SetPixels32(px);
                image.Apply();
                return Convert.ToBase64String(image.EncodeToPNG());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(image);
            }
        }
    }
}
#endif
