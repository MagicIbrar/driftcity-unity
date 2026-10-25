using System;
using System.Collections.Generic;
using System.IO;
using DriftCity.Core;
using UnityEngine;

namespace DriftCity.UI
{
    /// <summary>Lazily loaded UI data from the installed client: layout, cut images, fonts, strings.</summary>
    public static class UiAssets
    {
        static NuiDocument layout;
        static NtxBundle images;
        static readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();

        public static NuiDocument Layout
        {
            get
            {
                if (layout != null) return layout;
                EnsureCp949();
                var path = Path.Combine(DriftCityData.Files.Root, "Data", "GameUI", "game.nui");
                layout = NuiDocument.Parse(File.ReadAllBytes(path));
                return layout;
            }
        }

        public static void EnsureCp949()
        {
            if (Cp949.Loaded) return;
            var table = Resources.Load<TextAsset>("cp949");
            if (table) Cp949.LoadTable(table.bytes);
        }

        public static NtxBundle Images => images ?? (images = NtxBundle.LoadFrom(DriftCityData.Files));

        /// <summary>Image reference as written in game.nui ("x1,y1~x2,y2_name.dds" or a plain file name).</summary>
        public static Texture2D Texture(string reference)
        {
            if (string.IsNullOrEmpty(reference)) return null;
            if (textures.TryGetValue(reference, out var t)) return t;
            byte[] dds = Images.Get(reference);
            if (dds == null)
            {
                // whole textures referenced by plain name live in the regular archives
                var fs = DriftCityData.Files;
                var path = fs.Resolve(Path.GetFileName(reference));
                var hd = TextureLibrary.EnhancedFile(path);
                if (path != null) dds = hd != null ? File.ReadAllBytes(hd) : fs.Read(path);
            }
            try { t = dds != null ? TextureLibrary.FromDds(DdsImage.Load(dds)) : null; }
            catch (Exception ex) { Debug.LogWarning($"UI image {reference}: {ex.Message}"); t = null; }
            if (t) { t.name = reference; t.wrapMode = TextureWrapMode.Clamp; t.filterMode = FilterMode.Bilinear; }
            textures[reference] = t;
            return t;
        }

        /// <summary>Font spec from game.nui, e.g. "Calibri 14 700" (face, pixel size, weight).</summary>
        public static Font Font(string spec, out int size, out FontStyle style)
        {
            size = 12; style = FontStyle.Normal;
            string face = "Arial";
            if (!string.IsNullOrEmpty(spec))
            {
                var parts = spec.Split(' ');
                int n = parts.Length;
                // face names may contain spaces: trailing numeric tokens are size and weight
                int weight = 400;
                if (n >= 3 && int.TryParse(parts[n - 1], out int w) && int.TryParse(parts[n - 2], out int s)) { weight = w; size = s; face = string.Join(" ", parts, 0, n - 2); }
                else if (n >= 2 && int.TryParse(parts[n - 1], out int s2)) { size = s2; face = string.Join(" ", parts, 0, n - 1); }
                else face = spec;
                if (weight >= 600) style = FontStyle.Bold;
            }
            if (!fonts.TryGetValue(face, out var font))
            {
                font = UnityEngine.Font.CreateDynamicFontFromOSFont(new[] { face, "Calibri", "Verdana", "Malgun Gothic", "Arial" }, 16);
                fonts[face] = font;
            }
            return font;
        }

        public static void Clear()
        {
            foreach (var t in textures.Values) if (t) UnityEngine.Object.Destroy(t);
            textures.Clear();
            layout = null;
            images = null;
        }
    }
}
