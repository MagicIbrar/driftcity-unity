using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DriftCity.Core;
using UnityEditor;
using UnityEngine;

namespace DriftCity.EditorTools
{
    /// <summary>
    /// HD textures for the imported game data. "Enhance" upscales every DDS (Lanczos-3 with premultiplied alpha,
    /// then a light unsharp mask) and writes DXT1/DXT5 DDS files with mipmaps to GameData\Enhanced, which the
    /// game loads instead of the originals. A PNG placed at GameData\Textures\Edit\&lt;path&gt;.png (hand-painted
    /// or upscaled with any external tool) is used as the source for that texture instead of the original.
    /// "Export" writes every original as a PNG to GameData\Textures\Original as a starting point for editing.
    /// </summary>
    public static class DriftCityTextures
    {
        public const int DefaultMaxSize = 1024;

        static string Root => DriftCityImport.ProjectGameData;
        static string DataDir => Path.Combine(Root, "Data");
        static string EnhancedDir => Path.Combine(Root, "Enhanced");
        static string EditDir => Path.Combine(Root, "Textures", "Edit");
        static string OriginalPngDir => Path.Combine(Root, "Textures", "Original");

        [MenuItem("Drift City/Textures/Enhance All Textures (HD)")]
        public static void EnhanceMenu() => Run(() => Enhance(DefaultMaxSize, (m, t) => EditorUtility.DisplayCancelableProgressBar("Enhancing textures", m, t)));

        [MenuItem("Drift City/Textures/Export Originals as PNG")]
        public static void ExportMenu() => Run(() => ExportPng((m, t) => EditorUtility.DisplayCancelableProgressBar("Exporting textures", m, t)));

        [MenuItem("Drift City/Textures/Remove HD Textures")]
        public static void RemoveMenu() { if (Directory.Exists(EnhancedDir)) Directory.Delete(EnhancedDir, true); Debug.Log("Drift City: HD textures removed"); }

        /// <summary>Batch: -executeMethod DriftCity.EditorTools.DriftCityTextures.EnhanceBatch [-maxSize 1024] [-only vehicle]</summary>
        public static void EnhanceBatch()
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "-maxSize");
            int max = i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int m) ? m : DefaultMaxSize;
            int o = Array.IndexOf(args, "-only");
            string only = o >= 0 && o + 1 < args.Length ? args[o + 1].Replace('/', Path.DirectorySeparatorChar) : null;
            int last = -1;
            Enhance(max, (msg, t) => { int pct = (int)(t * 100); if (pct / 5 != last / 5) { last = pct; Debug.Log($"enhance {pct}% {msg}"); } return false; }, only);
        }

        public static void ExportBatch() => ExportPng((m, t) => false);

        static void Run(Action a)
        {
            try { a(); }
            finally { EditorUtility.ClearProgressBar(); }
        }

        static string[] Sources(string only) =>
            Directory.EnumerateFiles(DataDir, "*.dds", SearchOption.AllDirectories)
                .Where(f => only == null || f.Substring(DataDir.Length + 1).StartsWith(only, StringComparison.OrdinalIgnoreCase)).ToArray();

        /// <summary>Upscale target: small textures x4, others x2, never beyond maxSize; big ones are left alone.</summary>
        public static int TargetSize(int size, int maxSize) => size >= maxSize ? size : Mathf.Min(maxSize, size <= 128 ? size * 4 : size * 2);

        public static void Enhance(int maxSize, Func<string, float, bool> progress, string only = null)
        {
            var started = DateTime.Now;
            var files = Sources(only);
            int done = 0, skipped = 0, edited = 0;
            long outBytes = 0;
            for (int k = 0; k < files.Length; k++)
            {
                var file = files[k];
                var rel = file.Substring(DataDir.Length + 1);
                if (progress(rel, (float)k / files.Length)) break;
                try
                {
                    // memory order = top row first (how the game uploads DDS rows)
                    if (!Decode(File.ReadAllBytes(file), out var px, out int w, out int h)) { skipped++; continue; }
                    int tw = TargetSize(w, maxSize), th = Mathf.Max(4, h * tw / Mathf.Max(1, w));
                    var editPng = Path.Combine(EditDir, Path.ChangeExtension(rel, ".png"));
                    if (File.Exists(editPng))
                    {
                        var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        t.LoadImage(File.ReadAllBytes(editPng));
                        px = FlipRows(t.GetPixels32(), t.width, t.height); w = t.width; h = t.height;
                        UnityEngine.Object.DestroyImmediate(t);
                        edited++;
                        if (w >= tw) { tw = w; th = h; }
                    }
                    if (tw == w && th == h && !File.Exists(editPng)) { skipped++; continue; }
                    tw = (tw + 3) & ~3; th = (th + 3) & ~3;
                    var up = (tw == w && th == h) ? ToFloat(px) : Resample(ToFloat(px), w, h, tw, th);
                    Sharpen(up, tw, th, 0.45f);
                    var bytes = EncodeDds(FromFloat(up), tw, th);
                    var outFile = Path.Combine(EnhancedDir, Path.ChangeExtension(rel, ".dds"));
                    Directory.CreateDirectory(Path.GetDirectoryName(outFile));
                    File.WriteAllBytes(outFile, bytes);
                    outBytes += bytes.Length;
                    done++;
                }
                catch (Exception ex) { skipped++; Debug.LogWarning($"enhance {rel}: {ex.Message}"); }
            }
            Debug.Log($"Drift City HD textures: {done} written ({edited} from Edit PNGs), {skipped} skipped, {outBytes / 1048576.0:F0} MB in {(DateTime.Now - started).TotalMinutes:F1} min -> {EnhancedDir}");
        }

        public static void ExportPng(Func<string, float, bool> progress)
        {
            var files = Sources(null);
            int n = 0;
            for (int k = 0; k < files.Length; k++)
            {
                var rel = files[k].Substring(DataDir.Length + 1);
                if (progress(rel, (float)k / files.Length)) break;
                if (!Decode(File.ReadAllBytes(files[k]), out var px, out int w, out int h)) continue;
                var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
                t.SetPixels32(FlipRows(px, w, h));   // PNG rows bottom-up in Unity: flip so the file looks upright
                var outFile = Path.Combine(OriginalPngDir, Path.ChangeExtension(rel, ".png"));
                Directory.CreateDirectory(Path.GetDirectoryName(outFile));
                File.WriteAllBytes(outFile, t.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(t);
                n++;
            }
            Debug.Log($"Drift City: exported {n} textures as PNG -> {OriginalPngDir}");
        }

        // ---------------- decode / encode ----------------

        /// <summary>DDS -> RGBA pixels in the game's memory order (first row = top of the image).</summary>
        static bool Decode(byte[] dds, out Color32[] px, out int w, out int h)
        {
            px = null; w = h = 0;
            var img = DdsImage.Load(dds);
            var tex = TextureLibrary.FromDds(img);
            if (!tex) return false;
            w = tex.width; h = tex.height;
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active;
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;
            var read = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
            read.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            read.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            px = read.GetPixels32();
            UnityEngine.Object.DestroyImmediate(read);
            UnityEngine.Object.DestroyImmediate(tex);
            return true;
        }

        static Color32[] FlipRows(Color32[] src, int w, int h)
        {
            var dst = new Color32[src.Length];
            for (int y = 0; y < h; y++) Array.Copy(src, y * w, dst, (h - 1 - y) * w, w);
            return dst;
        }

        static byte[] EncodeDds(Color32[] px, int w, int h)
        {
            bool alpha = px.Any(c => c.a < 250);
            var t = new Texture2D(w, h, TextureFormat.RGBA32, true, false);
            t.SetPixels32(px);
            t.Apply(true);
            EditorUtility.CompressTexture(t, alpha ? TextureFormat.DXT5 : TextureFormat.DXT1, TextureCompressionQuality.Normal);
            var data = t.GetRawTextureData();
            int mips = t.mipmapCount;
            UnityEngine.Object.DestroyImmediate(t);

            var ms = new MemoryStream();
            var bw = new BinaryWriter(ms);
            bw.Write(0x20534444u);                       // "DDS "
            bw.Write(124u);
            bw.Write(0x1u | 0x2u | 0x4u | 0x1000u | 0x20000u | 0x80000u);   // caps, height, width, pixelformat, mipmapcount, linearsize
            bw.Write((uint)h); bw.Write((uint)w);
            bw.Write((uint)(Mathf.Max(1, (w + 3) / 4) * Mathf.Max(1, (h + 3) / 4) * (alpha ? 16 : 8)));
            bw.Write(0u); bw.Write((uint)mips);
            for (int i = 0; i < 11; i++) bw.Write(0u);
            bw.Write(32u); bw.Write(0x4u);               // pixel format: FOURCC
            bw.Write(alpha ? 0x35545844u : 0x31545844u);  // "DXT5" / "DXT1"
            for (int i = 0; i < 5; i++) bw.Write(0u);
            bw.Write(0x1000u | 0x400000u | 0x8u);         // texture, mipmap, complex
            for (int i = 0; i < 4; i++) bw.Write(0u);
            bw.Write(data);
            return ms.ToArray();
        }

        // ---------------- resampling (premultiplied RGBA floats) ----------------

        static float[] ToFloat(Color32[] px)
        {
            var f = new float[px.Length * 4];
            for (int i = 0; i < px.Length; i++)
            {
                float a = px[i].a / 255f;
                f[i * 4] = px[i].r / 255f * a; f[i * 4 + 1] = px[i].g / 255f * a; f[i * 4 + 2] = px[i].b / 255f * a; f[i * 4 + 3] = a;
            }
            return f;
        }

        static Color32[] FromFloat(float[] f)
        {
            var px = new Color32[f.Length / 4];
            for (int i = 0; i < px.Length; i++)
            {
                float a = Mathf.Clamp01(f[i * 4 + 3]);
                float inv = a > 1e-4f ? 1f / a : 0f;
                px[i] = new Color32((byte)(Mathf.Clamp01(f[i * 4] * inv) * 255 + 0.5f), (byte)(Mathf.Clamp01(f[i * 4 + 1] * inv) * 255 + 0.5f),
                                    (byte)(Mathf.Clamp01(f[i * 4 + 2] * inv) * 255 + 0.5f), (byte)(a * 255 + 0.5f));
            }
            return px;
        }

        static float Lanczos(float x)
        {
            x = Mathf.Abs(x);
            if (x < 1e-5f) return 1f;
            if (x >= 3f) return 0f;
            float px = Mathf.PI * x;
            return 3f * Mathf.Sin(px) * Mathf.Sin(px / 3f) / (px * px);
        }

        /// <summary>Separable Lanczos-3, wrapping at the edges (game textures tile).</summary>
        static float[] Resample(float[] src, int w, int h, int tw, int th)
        {
            var tmp = new float[tw * h * 4];
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < tw; x++)
                {
                    float cx = (x + 0.5f) * w / tw - 0.5f;
                    int x0 = Mathf.FloorToInt(cx) - 2;
                    float r = 0, g = 0, b = 0, a = 0, ws = 0;
                    for (int k = 0; k < 6; k++)
                    {
                        int sx = x0 + k;
                        float wgt = Lanczos(cx - sx);
                        int ix = ((sx % w) + w) % w;
                        int o = (y * w + ix) * 4;
                        r += src[o] * wgt; g += src[o + 1] * wgt; b += src[o + 2] * wgt; a += src[o + 3] * wgt; ws += wgt;
                    }
                    int d = (y * tw + x) * 4;
                    tmp[d] = r / ws; tmp[d + 1] = g / ws; tmp[d + 2] = b / ws; tmp[d + 3] = a / ws;
                }
            });
            var dst = new float[tw * th * 4];
            Parallel.For(0, th, y =>
            {
                float cy = (y + 0.5f) * h / th - 0.5f;
                int y0 = Mathf.FloorToInt(cy) - 2;
                for (int x = 0; x < tw; x++)
                {
                    float r = 0, g = 0, b = 0, a = 0, ws = 0;
                    for (int k = 0; k < 6; k++)
                    {
                        int sy = y0 + k;
                        float wgt = Lanczos(cy - sy);
                        int iy = ((sy % h) + h) % h;
                        int o = (iy * tw + x) * 4;
                        r += tmp[o] * wgt; g += tmp[o + 1] * wgt; b += tmp[o + 2] * wgt; a += tmp[o + 3] * wgt; ws += wgt;
                    }
                    int d = (y * tw + x) * 4;
                    dst[d] = Mathf.Max(0, r / ws); dst[d + 1] = Mathf.Max(0, g / ws); dst[d + 2] = Mathf.Max(0, b / ws); dst[d + 3] = Mathf.Clamp01(a / ws);
                }
            });
            return dst;
        }

        /// <summary>Unsharp mask (3x3 blur) on colour only, clamped to the premultiplied range.</summary>
        static void Sharpen(float[] img, int w, int h, float amount)
        {
            var src = (float[])img.Clone();
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    for (int c = 0; c < 3; c++)
                    {
                        float blur = 0;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int sx = ((x + dx) % w + w) % w, sy = ((y + dy) % h + h) % h;
                                blur += src[(sy * w + sx) * 4 + c] * ((dx == 0 ? 2 : 1) * (dy == 0 ? 2 : 1));
                            }
                        blur /= 16f;
                        img[o + c] = Mathf.Clamp(src[o + c] + amount * (src[o + c] - blur), 0, src[o + 3]);
                    }
                }
            });
        }
    }
}
