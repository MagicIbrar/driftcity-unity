using System;
using System.Collections.Generic;
using System.IO;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// Player stickers / custom marks (DriftCity_RE/PHYSICS.md section 14 "Custom marks"). The client draws marks from
    /// effects\realmatch\real_%05d.dds (128 x 128 DXT3, 0x4411e0); an id above 99999 (or a non-zero second argument) is a
    /// player's own mark, which it asked the server for (request 0x3f6 via 0x43fd80) instead of reading the file. The
    /// remake's version: a driver imports an image file from disk (no web service), it is fitted into 128 x 128 px and
    /// kept as a PNG of at most <see cref="MaxPngBytes"/>; offline it is saved in the profile (CustomMark), online the
    /// server keeps it (mark.set / mark.get, src/carparts.js) and others fetch it by name. The mark is stamped on both
    /// sides of the car's paint layout like a decal (CarPaint), on the player's car and on remote players' cars.
    /// Limits [remake]: files up to 4 MB and 4096 px, PNG / JPG; the stored mark is 128 px and 32 KB at most.
    /// </summary>
    public static class CustomMarks
    {
        public const int Size = 128;
        public const int MaxPngBytes = 32 * 1024;
        public const long MaxFileBytes = 4L * 1024 * 1024;
        public const int MaxSourcePixels = 4096;

        static PlayerProfile P => PlayerProfile.Current;
        static readonly Dictionary<string, Texture2D> cache = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, float> fetchedAt = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, List<Action<Texture2D>>> waiting = new Dictionary<string, List<Action<Texture2D>>>(StringComparer.OrdinalIgnoreCase);

        public static event Action Changed;

        /// <summary>The crew-mark upload (Social/CrewMarkUpload) takes its picture from the same local import.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void HookCrewMark() => Social.CrewMarkUpload.ImportHook = PickFileBytes;

        /// <summary>The image to import without a file dialog: crewmark.png / .jpg in the Stickers folder, else the newest
        /// image there; null when there is none (the crew code then falls back to its own save-folder file).</summary>
        public static byte[] PickFileBytes()
        {
            try
            {
                var files = FolderImages();
                var named = files.Find(f => Path.GetFileNameWithoutExtension(f).Equals("crewmark", StringComparison.OrdinalIgnoreCase));
                string pick = named ?? (files.Count > 0 ? files[0] : null);
                if (pick == null || new FileInfo(pick).Length > MaxFileBytes) return null;
                return File.ReadAllBytes(pick);
            }
            catch (Exception) { return null; }
        }

        /// <summary>The folder the sticker page lists images from (Documents-like place next to the saves).</summary>
        public static string Folder
        {
            get
            {
                string dir = Path.Combine(PlayerProfile.SaveDirOverride ?? Application.persistentDataPath, "Stickers");
                try { Directory.CreateDirectory(dir); } catch (Exception) { }
                return dir;
            }
        }

        /// <summary>Images in <see cref="Folder"/> (png / jpg / jpeg), newest first.</summary>
        public static List<string> FolderImages()
        {
            var list = new List<string>();
            try
            {
                foreach (var f in Directory.GetFiles(Folder))
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext == ".png" || ext == ".jpg" || ext == ".jpeg") list.Add(f);
                }
                list.Sort((a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));
            }
            catch (Exception) { }
            return list;
        }

        /// <summary>
        /// Checks and converts an image file into the mark PNG (base64): null and an error text when it can't be used.
        /// </summary>
        public static string Convert(string path, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(path)) { error = "Choose an image file."; return null; }
            path = path.Trim().Trim('"');
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".png" && ext != ".jpg" && ext != ".jpeg") { error = "The mark must be a PNG or JPG image."; return null; }
            byte[] bytes;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) { error = "That file does not exist."; return null; }
                if (info.Length > MaxFileBytes) { error = "The image file is too large (at most 4 MB)."; return null; }
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex) { error = "The image could not be read: " + ex.Message; return null; }
            return ConvertBytes(bytes, out error);
        }

        /// <summary>Image bytes (PNG / JPG) to the mark PNG as base64, fitted into 128 x 128 with clear borders.</summary>
        public static string ConvertBytes(byte[] bytes, out string error)
        {
            error = null;
            var src = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (bytes == null || !src.LoadImage(bytes)) { error = "The image could not be read."; return null; }
                if (src.width > MaxSourcePixels || src.height > MaxSourcePixels) { error = "The image is too large (at most 4096 x 4096 pixels)."; return null; }
                foreach (int size in new[] { Size, 96, 64, 48 })
                {
                    var png = Fit(src, size).EncodeToPNG();
                    if (png.Length <= MaxPngBytes) return System.Convert.ToBase64String(png);
                }
                error = "The image has too much detail to fit 32 KB.";
                return null;
            }
            finally { UnityEngine.Object.Destroy(src); }
        }

        /// <summary>The image scaled to fit a size x size square (aspect kept, clear borders), bilinear.</summary>
        static Texture2D Fit(Texture2D src, int size)
        {
            var dst = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var px = new Color32[size * size];
            float scale = Mathf.Min((float)size / src.width, (float)size / src.height);
            float w = src.width * scale, h = src.height * scale, ox = (size - w) * 0.5f, oy = (size - h) * 0.5f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f - ox) / w, v = (y + 0.5f - oy) / h;
                    px[y * size + x] = u < 0f || u > 1f || v < 0f || v > 1f ? new Color32(0, 0, 0, 0) : (Color32)src.GetPixelBilinear(u, v);
                }
            dst.SetPixels32(px);
            dst.Apply(false);
            return dst;
        }

        /// <summary>Imports an image file as the driver's mark (offline: the profile; online: mark.set).</summary>
        public static void Import(string path, Action<bool, string> done = null)
        {
            string b64 = Convert(path, out string error);
            if (b64 == null) { done?.Invoke(false, error); return; }
            Set(b64, done);
        }

        /// <summary>Stores a converted mark (base64 PNG).</summary>
        public static void Set(string b64, Action<bool, string> done = null)
        {
            if (P == null) { done?.Invoke(false, "No driver."); return; }
            Progress.Op("mark.set", new Dictionary<string, object> { ["png"] = b64 }, () =>
            {
                if (string.IsNullOrEmpty(b64) || b64.Length > (MaxPngBytes + 2) / 3 * 4 + 4) return new Progress.Result { Error = "The image is too large (at most 32 KB)." };
                P.CustomMark = b64;
                P.CustomMarkVer++;
                return new Progress.Result { Ok = true };
            }, r =>
            {
                if (r.Ok) { Remember(P.Name, b64); Refresh(); }
                done?.Invoke(r.Ok, r.Error);
            });
        }

        /// <summary>Removes the driver's mark.</summary>
        public static void Clear(Action<bool> done = null)
        {
            if (P == null) { done?.Invoke(false); return; }
            Progress.Op("mark.clear", new Dictionary<string, object>(), () =>
            {
                P.CustomMark = "";
                P.CustomMarkVer++;
                return new Progress.Result { Ok = true };
            }, r =>
            {
                if (r.Ok) { Forget(P.Name); Refresh(); }
                done?.Invoke(r.Ok);
            });
        }

        static void Refresh()
        {
            GameFlow.Instance?.RepaintCar();
            try { Changed?.Invoke(); } catch (Exception ex) { Debug.LogException(ex); }
        }

        /// <summary>Caches a mark texture for a driver (and the own one in a file next to the saves, for online drivers).</summary>
        static Texture2D Remember(string name, string b64)
        {
            if (string.IsNullOrEmpty(name)) return null;
            Forget(name);
            if (string.IsNullOrEmpty(b64)) return null;
            Texture2D tex = null;
            try
            {
                var bytes = System.Convert.FromBase64String(b64);
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "custom mark " + name, wrapMode = TextureWrapMode.Clamp };
                if (!tex.LoadImage(bytes)) { UnityEngine.Object.Destroy(tex); return null; }
                if (P != null && string.Equals(name, P.Name, StringComparison.OrdinalIgnoreCase) && P.OnlineDriver)
                    File.WriteAllBytes(OwnFile(name), bytes);
            }
            catch (Exception ex) { Debug.LogWarning("custom mark: " + ex.Message); }
            if (tex) cache[name] = tex;
            fetchedAt[name] = Time.realtimeSinceStartup;
            return tex;
        }

        static void Forget(string name)
        {
            if (cache.TryGetValue(name, out var old) && old) UnityEngine.Object.Destroy(old);
            cache.Remove(name);
            fetchedAt.Remove(name);
        }

        static string OwnFile(string name) => Path.Combine(Folder, ".mine_" + System.Text.RegularExpressions.Regex.Replace(name ?? "driver", @"[^A-Za-z0-9_-]", "_") + ".png");

        /// <summary>The driver's own mark texture right now (null = none), without waiting.</summary>
        public static Texture2D Own()
        {
            var p = P;
            if (p == null) return null;
            if (cache.TryGetValue(p.Name, out var t) && t) return t;
            if (!string.IsNullOrEmpty(p.CustomMark)) return Remember(p.Name, p.CustomMark);
            if (p.OnlineDriver && p.CustomMarkVer > 0)
            {
                try { string f = OwnFile(p.Name); if (File.Exists(f)) return Remember(p.Name, System.Convert.ToBase64String(File.ReadAllBytes(f))); } catch (Exception) { }
            }
            return null;
        }

        /// <summary>A driver's mark: the own one at once, others' from the server (cached for a minute), offline none.</summary>
        public static void Get(string name, Action<Texture2D> done)
        {
            if (string.IsNullOrWhiteSpace(name)) { done?.Invoke(null); return; }
            if (P != null && string.Equals(name, P.Name, StringComparison.OrdinalIgnoreCase)) { done?.Invoke(Own()); return; }
            if (cache.TryGetValue(name, out var t) && t && fetchedAt.TryGetValue(name, out float at) && Time.realtimeSinceStartup - at < 60f) { done?.Invoke(t); return; }
            if (!OnlineSession.Active) { done?.Invoke(null); return; }
            if (waiting.TryGetValue(name, out var list)) { list.Add(done); return; }
            waiting[name] = new List<Action<Texture2D>> { done };
            OnlineSession.Instance.Net.Request("mark.get", new Dictionary<string, object> { ["name"] = name }, r =>
            {
                Texture2D tex = null;
                if (r != null && r.B("ok")) tex = Remember(name, r.S("png", ""));
                else fetchedAt[name] = Time.realtimeSinceStartup;
                if (waiting.TryGetValue(name, out var cbs)) { waiting.Remove(name); foreach (var cb in cbs) { try { cb?.Invoke(tex); } catch (Exception ex) { Debug.LogException(ex); } } }
            });
        }

        /// <summary>Paints a remote player's car with its mark once it arrives (RemotePlayers builds the car first).</summary>
        public static void Attach(GameObject car, VehicleInfo v, OwnedCar look, string driver)
        {
            if (!car || v == null || string.IsNullOrEmpty(driver)) return;
            Get(driver, tex => { if (tex && car) CarPaint.Apply(car, v, look, null, driver); });
        }

        /// <summary>Texture key CarPaint resolves for a driver's mark.</summary>
        public static string Key(string driver) => "custommark:" + driver;

        /// <summary>The texture behind a <see cref="Key"/> (cached only; null when not loaded).</summary>
        public static Texture2D Resolve(string key)
        {
            if (key == null || !key.StartsWith("custommark:")) return null;
            string name = key.Substring("custommark:".Length);
            if (P != null && string.Equals(name, P.Name, StringComparison.OrdinalIgnoreCase)) return Own();
            return cache.TryGetValue(name, out var t) ? t : null;
        }

        /// <summary>Tests: forget every cached mark.</summary>
        public static void ResetCache()
        {
            foreach (var t in cache.Values) if (t) UnityEngine.Object.Destroy(t);
            cache.Clear(); fetchedAt.Clear(); waiting.Clear();
        }
    }
}
