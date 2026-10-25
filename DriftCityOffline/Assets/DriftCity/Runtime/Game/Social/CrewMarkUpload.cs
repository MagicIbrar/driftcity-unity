using System;
using System.Collections.Generic;
using System.IO;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.Social
{
    /// <summary>
    /// The custom crew mark of MYTEAM_MARK 3990000 (SOCIAL.md 5 / 8.2): the Master's LOADFILE picks a picture that becomes the
    /// crew's 32x32 mark, at most 4000 bytes (0x6107c0); the client drew the stored marks with the built-in ones. The remake
    /// takes the picture through <see cref="ImportHook"/> when the car-visuals image import provides one, else the first of
    /// crewmark.png / .jpg / .bmp in the save folder (the stand-in for the file dialog); the picture is fitted to 32x32,
    /// sent as a PNG (`crew.markImage {data}`, Master only, the server keeps it) and drawn over the built-in mark where the
    /// crew's own mark shows (MY 3980000, 3990000). Offline the PNG is kept beside the save (crewmark_&lt;crew&gt;.png).
    /// </summary>
    public static class CrewMarkUpload
    {
        public const int Size = 32, MaxBytes = 4000;

        /// <summary>The image import of the car-visuals work (returns the picked file's bytes, or null when cancelled).
        /// Assign it from that code; null = the save-folder stand-in.</summary>
        public static Func<byte[]> ImportHook;

        static readonly Dictionary<string, Texture2D> cache = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        public static string LastError { get; private set; }

        public static string SaveFolder => Application.persistentDataPath;

        /// <summary>The picture to upload: the import hook's, else crewmark.(png|jpg|bmp) in the save folder; null = none.</summary>
        public static byte[] Pick()
        {
            if (ImportHook != null) { try { return ImportHook(); } catch (Exception ex) { Debug.LogWarning("crew mark import: " + ex.Message); return null; } }
            foreach (var ext in new[] { "png", "jpg", "bmp" })
            {
                var path = Path.Combine(SaveFolder, "crewmark." + ext);
                if (File.Exists(path)) return File.ReadAllBytes(path);
            }
            return null;
        }

        /// <summary>Any picture (PNG / JPG / 24- or 32-bit BMP) -> the 32x32 mark as PNG bytes; null with <see cref="LastError"/>.</summary>
        public static byte[] Fit(byte[] data)
        {
            LastError = null;
            if (data == null || data.Length == 0) { LastError = "No picture."; return null; }
            Texture2D src = data.Length > 2 && data[0] == 'B' && data[1] == 'M' ? SocialUi.Bmp(data) : null;
            if (!src)
            {
                src = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!src.LoadImage(data)) { UnityEngine.Object.Destroy(src); LastError = "The picture can't be read (PNG, JPG or BMP)."; return null; }
            }
            var dst = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            var px = new Color32[Size * Size];
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                    px[y * Size + x] = src.GetPixelBilinear((x + 0.5f) / Size, (y + 0.5f) / Size);
            dst.SetPixels32(px); dst.Apply();
            var png = dst.EncodeToPNG();
            UnityEngine.Object.Destroy(dst); UnityEngine.Object.Destroy(src);
            if (png.Length > MaxBytes) { LastError = $"The mark is over {MaxBytes} bytes."; return null; }
            return png;
        }

        /// <summary>LOADFILE: picks, fits and uploads the crew's custom mark (Master only, 10383).</summary>
        public static void Upload(Action<bool> done = null)
        {
            var crew = Teams.Mine;
            if (crew == null || !Teams.IAmMaster) { UiPopup.Message(SocialNet.T("10383", "Crew Master only.")); done?.Invoke(false); return; }
            var raw = Pick();
            if (raw == null) { UiPopup.Message($"Put a 32x32 picture named crewmark.png (or .bmp / .jpg) in\n{SaveFolder}"); done?.Invoke(false); return; }
            UploadBytes(raw, done);
        }

        public static void UploadBytes(byte[] raw, Action<bool> done = null)
        {
            var crew = Teams.Mine;
            var png = Fit(raw);
            if (png == null || crew == null) { UiPopup.Message(LastError ?? "No crew."); done?.Invoke(false); return; }
            if (!SocialNet.Online)
            {
                try { File.WriteAllBytes(OfflinePath(crew.Name), png); } catch (Exception ex) { UiPopup.Message(ex.Message); done?.Invoke(false); return; }
                Remember(crew.Name, png);
                done?.Invoke(true);
                return;
            }
            SocialNet.Request("crew.markImage", new Dictionary<string, object> { ["data"] = Convert.ToBase64String(png) }, r =>
            {
                if (!r.B("ok")) { UiPopup.Message(SocialNet.Err(r)); done?.Invoke(false); return; }
                Remember(crew.Name, png);
                done?.Invoke(true);
            });
        }

        static string OfflinePath(string crew) => Path.Combine(SaveFolder, "crewmark_" + string.Concat((crew ?? "").Split(Path.GetInvalidFileNameChars())) + ".png");

        static void Remember(string crew, byte[] png)
        {
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            if (t.LoadImage(png)) cache[crew] = t; else UnityEngine.Object.Destroy(t);
        }

        /// <summary>The crew's custom mark when it has one (loaded / fetched once; online `crew.markImageGet`).</summary>
        public static Texture2D For(string crew, Action<Texture2D> fetched = null)
        {
            if (string.IsNullOrEmpty(crew)) return null;
            if (cache.TryGetValue(crew, out var t)) return t;
            if (!SocialNet.Online)
            {
                var path = OfflinePath(crew);
                if (File.Exists(path)) { Remember(crew, File.ReadAllBytes(path)); cache.TryGetValue(crew, out t); return t; }
                cache[crew] = null;
                return null;
            }
            cache[crew] = null;   // asked once
            SocialNet.Request("crew.markImageGet", new Dictionary<string, object> { ["name"] = crew }, r =>
            {
                var d = r.S("data");
                if (!r.B("ok") || string.IsNullOrEmpty(d)) return;
                try { Remember(crew, Convert.FromBase64String(d)); } catch { return; }
                if (cache.TryGetValue(crew, out var tex) && tex) fetched?.Invoke(tex);
            });
            return null;
        }

        /// <summary>Draws the crew's custom mark over a mark control (after SocialUi.ShowMark drew the built-in one).</summary>
        public static void Show(NuiView v, string id, string crew)
        {
            var c = v ? v.Control(id) : null;
            if (!c) return;
            void Put(Texture2D tex)
            {
                if (!tex || !c) return;
                c.gameObject.SetActive(true);
                var ri = c.GetComponentInChildren<RawImage>(true);
                if (ri) { ri.texture = tex; ri.uvRect = new Rect(0, 0, 1, 1); ri.color = Color.white; ri.enabled = true; }
            }
            Put(For(crew, Put));
        }

        public static void ClearCache() => cache.Clear();
    }
}
