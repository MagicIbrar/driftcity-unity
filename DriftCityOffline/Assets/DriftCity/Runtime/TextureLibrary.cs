using System;
using System.Collections.Generic;
using System.IO;
using DriftCity.Core;
using DriftCity.Core.Nif;
using UnityEngine;

namespace DriftCity
{
    /// <summary>Loads and caches textures referenced by NIF files.</summary>
    public static class TextureLibrary
    {
        static readonly Dictionary<string, Texture2D> cache = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string> resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public static IEnumerable<string> Missing => missing;

        const string HdPref = "dc_hd_textures";
        /// <summary>Use HD versions from &lt;game data&gt;\Enhanced when they exist (Drift City > Textures > Enhance).</summary>
        public static bool UseEnhanced
        {
            get => PlayerPrefs.GetInt(HdPref, 1) == 1;
            set { PlayerPrefs.SetInt(HdPref, value ? 1 : 0); PlayerPrefs.Save(); PendingReload = true; }
        }

        /// <summary>Set when the HD setting changed; the next city load drops the cache (textures in use stay valid until then).</summary>
        public static bool PendingReload;

        /// <summary>The HD replacement of a game file path, or null.</summary>
        public static string EnhancedFile(string path)
        {
            var fs = DriftCityData.Files;
            if (fs == null || !UseEnhanced || string.IsNullOrEmpty(path)) return null;
            var file = Path.Combine(fs.Root, "Enhanced", Path.ChangeExtension(path, ".dds").Replace('\\', Path.DirectorySeparatorChar));
            return File.Exists(file) ? file : null;
        }

        public static int EnhancedLoaded { get; private set; }

        public static void Clear()
        {
            foreach (var t in cache.Values) if (t) UnityEngine.Object.Destroy(t);
            cache.Clear();
            missing.Clear();
            resolved.Clear();
        }

        /// <summary>
        /// Resolves a texture name the way the exported NIFs reference it: names often say .bmp/.tga
        /// while the archives ship .dds, and many are stored as bare names or ".\data\..." paths.
        /// </summary>
        public static Texture2D Get(string fileName, string nifDirectory)
        {
            if (string.IsNullOrEmpty(fileName)) return null;
            string norm = NayaPack.NormalizePath(fileName.Replace(".\\", "").Replace("./", ""));
            string key = norm + "|" + nifDirectory;
            if (resolved.TryGetValue(key, out var known) && cache.TryGetValue(known, out var hit) && hit) return hit;
            if (missing.Contains(key)) return null;

            var fs = DriftCityData.Files;
            string path = null;
            string dds = Path.ChangeExtension(norm, ".dds");
            foreach (var candidate in new[] { norm, dds, "data\\" + dds })
                if (fs.Exists(candidate)) { path = candidate; break; }
            if (path == null) path = fs.Resolve(Path.GetFileName(dds), nifDirectory) ?? fs.Resolve(Path.GetFileName(norm), nifDirectory);
            if (path == null) { missing.Add(key); return null; }
            resolved[key] = path;
            // the same file referenced from several folders is loaded once
            if (cache.TryGetValue(path, out var shared) && shared) return shared;

            Texture2D tex = null;
            try
            {
                var hd = EnhancedFile(path);
                var bytes = hd != null ? File.ReadAllBytes(hd) : fs.Read(path);
                if (hd != null) EnhancedLoaded++;
                if (hd != null || path.EndsWith(".dds")) tex = FromDds(DdsImage.Load(bytes));
                else if (path.EndsWith(".tga")) tex = FromTga(bytes);
                else
                {
                    tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                    if (!tex.LoadImage(bytes)) { UnityEngine.Object.Destroy(tex); tex = null; }
                }
            }
            catch (Exception ex) { Debug.LogWarning($"texture {path}: {ex.Message}"); }
            if (tex == null) { missing.Add(key); return null; }
            tex.name = Path.GetFileName(path);
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.anisoLevel = 8;
            tex.filterMode = tex.mipmapCount > 1 ? FilterMode.Trilinear : FilterMode.Bilinear;
            cache[path] = tex;
            return tex;
        }

        /// <summary>Truecolour TGA (types 2 and 10, 24/32 bit), as a few effects ship (effects\rain_02.tga). Rows are
        /// stored top-down like the DDS files, so the image's origin flag decides whether to flip.</summary>
        public static Texture2D FromTga(byte[] d)
        {
            if (d == null || d.Length < 18) return null;
            int idLen = d[0], type = d[2], w = d[12] | d[13] << 8, h = d[14] | d[15] << 8, bpp = d[16], desc = d[17];
            if ((type != 2 && type != 10) || (bpp != 24 && bpp != 32) || w <= 0 || h <= 0) return null;
            int bytes = bpp / 8, p = 18 + idLen + (d[1] != 0 ? (d[5] | d[6] << 8) * ((d[7] + 7) / 8) : 0);
            var px = new Color32[w * h];
            int n = 0;
            Color32 Read(int at) => new Color32(d[at + 2], d[at + 1], d[at], bytes == 4 ? d[at + 3] : (byte)255);
            while (n < px.Length && p + bytes <= d.Length)
            {
                if (type == 2) { px[n++] = Read(p); p += bytes; continue; }
                int head = d[p++], count = (head & 0x7f) + 1;
                if ((head & 0x80) != 0) { var c = Read(p); p += bytes; for (int k = 0; k < count && n < px.Length; k++) px[n++] = c; }
                else for (int k = 0; k < count && n < px.Length && p + bytes <= d.Length; k++) { px[n++] = Read(p); p += bytes; }
            }
            // TGA rows run bottom-up unless bit 5 is set; flip to top-down to match the DDS uploads
            if ((desc & 0x20) == 0)
                for (int y = 0; y < h / 2; y++)
                    for (int x = 0; x < w; x++) { var t = px[y * w + x]; px[y * w + x] = px[(h - 1 - y) * w + x]; px[(h - 1 - y) * w + x] = t; }
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true);
            tex.SetPixels32(px);
            tex.Apply(true);
            return tex;
        }

        /// <summary>DDS rows are uploaded in file order: texel (u, v) in D3D convention lands where
        /// Unity samples (u, v), so UVs from the NIF can be used unchanged.</summary>
        public static Texture2D FromDds(DdsImage img)
        {
            TextureFormat fmt = img.Format == Core.PixelFormat.DXT1 ? TextureFormat.DXT1
                              : img.Format == Core.PixelFormat.DXT5 ? TextureFormat.DXT5
                              : TextureFormat.RGBA32;
            bool blockCompressed = fmt != TextureFormat.RGBA32;
            if (blockCompressed && (img.Width % 4 != 0 || img.Height % 4 != 0))
                return null;
            var tex = new Texture2D(img.Width, img.Height, fmt, img.MipCount, false);
            tex.LoadRawTextureData(img.Data);
            tex.Apply(false, true); // upload and free the CPU copy
            return tex;
        }

        public static Texture2D FromPixelData(NiPixelData pd, NifFile nif)
        {
            if (pd.Mips == null || pd.Mips.Length == 0) return null;
            var (w, h, _) = pd.Mips[0];
            int width = (int)w, height = (int)h;
            try
            {
                switch (pd.PixelFormat)
                {
                    case 4: // DXT1
                    case 6: // DXT5
                    {
                        var fmt = pd.PixelFormat == 4 ? TextureFormat.DXT1 : TextureFormat.DXT5;
                        if (width % 4 != 0 || height % 4 != 0) return null;
                        var tex = new Texture2D(width, height, fmt, pd.Mips.Length, false);
                        tex.LoadRawTextureData(pd.Pixels);
                        tex.Apply(false, true);
                        return tex;
                    }
                    case 0: // RGB24
                    case 1: // RGBA32
                    {
                        int bpp = pd.PixelFormat == 0 ? 3 : 4;
                        var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
                        var px = new Color32[width * height];
                        for (int i = 0; i < px.Length && (i + 1) * bpp <= pd.Pixels.Length; i++)
                            px[i] = new Color32(pd.Pixels[i * bpp], pd.Pixels[i * bpp + 1], pd.Pixels[i * bpp + 2], bpp == 4 ? pd.Pixels[i * bpp + 3] : (byte)255);
                        tex.SetPixels32(px);
                        tex.Apply(true, true);
                        return tex;
                    }
                    case 2: // 8-bit palette
                    case 3:
                    {
                        var pal = nif.Get<NiPalette>(pd.Palette);
                        if (pal == null) return null;
                        var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
                        var px = new Color32[width * height];
                        for (int i = 0; i < px.Length && i < pd.Pixels.Length; i++)
                        {
                            int p = pd.Pixels[i] * 4;
                            px[i] = new Color32(pal.Rgba[p], pal.Rgba[p + 1], pal.Rgba[p + 2], pd.PixelFormat == 3 ? pal.Rgba[p + 3] : (byte)255);
                        }
                        tex.SetPixels32(px);
                        tex.Apply(true, true);
                        return tex;
                    }
                }
            }
            catch (Exception ex) { Debug.LogWarning("embedded texture: " + ex.Message); }
            return null;
        }
    }
}
