using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DriftCity.Core;
using DriftCity.Game;
using NUnit.Framework;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.TestTools;

namespace DriftCity.Tests
{
    /// <summary>
    /// The baked ground shadows (main\shadow.dat) and the client's VisibleRange window over the terrain grid
    /// (DriftCity_RE/TRAFFIC.md section 12). Enters Cras, checks the shadow pieces and the window, measures frame time and
    /// draw calls with the window off and at VisibleRange 5 / 3 / 1, and writes Preview/streaming_*.png.
    /// Needs the installed client (or GameData).
    /// </summary>
    public class CityStreamingTests
    {
        string tempSave;

        [TearDown]
        public void TearDown()
        {
            CityStreaming.Enabled = true;
            CityStreaming.FadeEnabled = true;
            CityStreaming.ForceFadeAlpha = -1f;
            PlayerProfile.SaveDirOverride = null;
            foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Exclude).Where(g => g.transform.parent == null)) Object.Destroy(go);
            try { if (tempSave != null) Directory.Delete(tempSave, true); } catch { }
        }

        [Test]
        public void ShadowDatHeaderAndWindowMath()
        {
            var root = DriftCityData.ResolveGameRoot(@"D:\Drift_City_Remastered_Alpha_08-2025\Drift City Remastered Alpha");
            Assert.IsNotNull(root, "installed client or imported data found");
            if (DriftCityData.Files == null) DriftCityData.Mount(root);
            var fs = DriftCityData.Files;
            foreach (var city in new[] { "cras", "koinonia", "moonpalace", "oros", "taipei" })
            {
                string path = $@"{city}\main\shadow.dat";
                if (!fs.Exists(path)) continue;
                int off = CityLoader.ShadowDatNifOffset(fs.Read(path), out uint stamp, out int count);
                Debug.Log($"Drift City streaming: {path} stamp 0x{stamp:x8}, {count} pieces, NIF at {off}");
                Assert.Greater(off, 0, path);
                Assert.Greater(count, 0, path);
            }
            Assert.AreEqual(100f, CityStreaming.ActorDrawDistance(1));
            Assert.AreEqual(900f, CityStreaming.ActorDrawDistance(5));
            Assert.IsTrue(CityStreaming.InWindow(new Vector2Int(12, 7), new Vector2Int(10, 5), 2));
            Assert.IsFalse(CityStreaming.InWindow(new Vector2Int(13, 5), new Vector2Int(10, 5), 2));
        }

        static IEnumerator WaitFor(System.Func<bool> condition, float timeout, string what)
        {
            float t = 0;
            while (!condition() && t < timeout) { t += Time.unscaledDeltaTime; yield return null; }
            Assert.IsTrue(condition(), "timed out waiting for " + what);
        }

        IEnumerator EnterCity(string city)
        {
            tempSave = Path.Combine(Path.GetTempPath(), "driftcity_stream_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempSave);
            PlayerProfile.SaveDirOverride = tempSave;
            PlayerProfile.Slot = 0;
            var p = new PlayerProfile { Name = "Streamer", Level = 10, Mito = 100000, City = city };
            p.Cars.Add(new OwnedCar { VehicleIndex = 0, Color = 2 });
            p.Save();
            PlayerProfile.Current = null;
            Directory.CreateDirectory("Preview");
            new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            var flow = new GameObject("Game").AddComponent<GameFlow>();
            flow.SkipParkingLot = true;
            yield return FrontEndSteps.ToCharacterSelect(flow);
            yield return FrontEndSteps.EnterSelected();
            yield return WaitFor(() => flow.InCity && CitySession.Current && flow.PlayerCar, 600, "city " + city);
            yield return new WaitForSeconds(1f);
        }

        static void Shot(string name, Camera from, Vector3? eye = null, Vector3? at = null)
        {
            var go = new GameObject("Shot Camera");
            var cam = go.AddComponent<Camera>();
            cam.CopyFrom(from);
            if (eye.HasValue) { go.transform.position = eye.Value; go.transform.LookAt(at.Value); }
            var rt = new RenderTexture(1024, 640, 24);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            cam.targetTexture = null;
            File.WriteAllBytes(Path.Combine("Preview", name + ".png"), tex.EncodeToPNG());
            Object.Destroy(tex); rt.Release(); Object.Destroy(go);
            Debug.Log("Drift City streaming: wrote Preview/" + name + ".png");
        }

        struct Sample { public string Label; public float Ms, RenderMs, RenderMaxMs; public int Visible; }

        /// <summary>
        /// Frame time over 240 frames (the editor's batch-mode player loop is paced near 60 Hz, so this mostly shows the
        /// pacing) and the CPU time of one synchronous Camera.Render of the game camera (culling + draw submission;
        /// the median of 60 renders into a render texture), which is what the window saves.
        /// </summary>
        static IEnumerator Measure(string label, List<Sample> into, CityStreaming window, Camera cam)
        {
            for (int i = 0; i < 30; i++) yield return null;   // settle after the switch
            const int frames = 240;
            float total = 0;
            for (int i = 0; i < frames; i++) { yield return null; total += Time.unscaledDeltaTime; }
            var go = new GameObject("Perf Camera");
            var pc = go.AddComponent<Camera>();
            pc.CopyFrom(cam);
            var rt = new RenderTexture(1280, 720, 24);
            pc.targetTexture = rt;
            pc.Render();   // warm-up
            var times = new List<double>();
            var sw = new System.Diagnostics.Stopwatch();
            for (int i = 0; i < 60; i++) { sw.Restart(); pc.Render(); sw.Stop(); times.Add(sw.Elapsed.TotalMilliseconds); }
            pc.targetTexture = null; rt.Release(); Object.Destroy(go);
            times.Sort();
            var s = new Sample { Label = label, Ms = total / frames * 1000f, RenderMs = (float)times[times.Count / 2], RenderMaxMs = (float)times[times.Count - 1], Visible = window.VisibleCount };
            into.Add(s);
            Debug.Log($"Drift City streaming perf [{label}]: frame {s.Ms:F2} ms, Camera.Render median {s.RenderMs:F2} ms (max {s.RenderMaxMs:F2}), city renderers drawn {s.Visible}/{window.RendererCount}");
        }

        [UnityTest]
        public IEnumerator CrasGroundShadowsAndWindow()
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            yield return EnterCity("cras");
            var shadows = GameObject.Find("Ground Shadows");
            Assert.IsNotNull(shadows, "Ground Shadows loaded from cras\\main\\shadow.dat");
            var pieces = shadows.GetComponentsInChildren<Renderer>();
            Assert.Greater(pieces.Length, 50, "shadow pieces");
            var window = Object.FindAnyObjectByType<CityStreaming>();
            Assert.IsNotNull(window, "the city has the VisibleRange window");
            Assert.Greater(window.RendererCount, 1000);
            Debug.Log($"Drift City streaming: {pieces.Length} shadow renderers, {window.RendererCount} city renderers, grid origin {window.Origin}, cell {window.CellSize}");

            var loader = Object.FindAnyObjectByType<CityStreaming>().transform;
            foreach (var r in pieces.Take(6))
            {
                var mr = r.sharedMaterial;
                Debug.Log($"Drift City streaming: piece '{r.transform.parent?.name}/{r.name}' bounds {r.bounds.center} size {r.bounds.size}, material {mr?.name} tex {(mr && mr.mainTexture ? mr.mainTexture.width + "x" + mr.mainTexture.height + " " + ((Texture2D)mr.mainTexture).format : "none")} src {mr?.GetFloat("_SrcBlend")} dst {mr?.GetFloat("_DstBlend")} zwrite {mr?.GetFloat("_ZWrite")} queue {mr?.renderQueue}");
            }
            // the first piece's embedded texture, colour and alpha side by side
            var t0 = pieces[0].sharedMaterial.mainTexture;
            if (t0)
            {
                var rtt = RenderTexture.GetTemporary(t0.width, t0.height, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(t0, rtt);
                RenderTexture.active = rtt;
                var rd = new Texture2D(t0.width, t0.height, TextureFormat.RGBA32, false);
                rd.ReadPixels(new Rect(0, 0, t0.width, t0.height), 0, 0); rd.Apply();
                RenderTexture.active = null; RenderTexture.ReleaseTemporary(rtt);
                var px = rd.GetPixels32();
                var both = new Texture2D(t0.width * 2, t0.height, TextureFormat.RGB24, false);
                for (int y = 0; y < t0.height; y++)
                    for (int x = 0; x < t0.width; x++)
                    {
                        var c = px[y * t0.width + x];
                        both.SetPixel(x, y, new Color32(c.r, c.g, c.b, 255));
                        both.SetPixel(x + t0.width, y, new Color32(c.a, c.a, c.a, 255));
                    }
                both.Apply();
                File.WriteAllBytes(Path.Combine("Preview", "streaming_shadow_texture.png"), both.EncodeToPNG());
                Debug.Log($"Drift City streaming: texture 0 avg rgb {px.Average(c => (c.r + c.g + c.b) / 3.0):F1} alpha {px.Average(c => (double)c.a):F1} min a {px.Min(c => c.a)} max a {px.Max(c => c.a)}");
            }
            var terrainRoot = loader.Find("Terrain");
            if (terrainRoot) { var tb = terrainRoot.GetComponentsInChildren<Renderer>().Select(r => r.bounds).Aggregate((x, y) => { x.Encapsulate(y); return x; }); Debug.Log($"Drift City streaming: terrain bounds {tb.center} size {tb.size}"); }

            // a shadow piece seen from above at an angle: one near the player
            var carPos = CitySession.Current.Car.transform.position;
            var b = pieces.OrderBy(r => (r.bounds.center - carPos).sqrMagnitude).First().bounds;
            var cam = Camera.main;
            // the window follows the game camera; open it around the shot's eye for the free shots
            var eye1 = b.center + new Vector3(-40, 70, -40);
            float span = Mathf.Max(b.size.x, b.size.z);
            var eye2 = b.center + new Vector3(0, span * 1.2f + 30, -1);
            CityStreaming.Enabled = false; window.Apply(eye1);
            Shot("streaming_ground_shadows", cam, eye1, b.center);
            Shot("streaming_ground_shadows_top", cam, eye2, b.center);
            shadows.SetActive(false);
            Shot("streaming_ground_shadows_off", cam, eye1, b.center);
            shadows.SetActive(true);
            CityStreaming.Enabled = true;

            // performance: window off, then VisibleRange 5 / 3 / 1 from the player's camera
            var samples = new List<Sample>();
            CityStreaming.Enabled = false;
            shadows.SetActive(false);
            yield return Measure("before: no ground shadows, no window", samples, window, cam);
            shadows.SetActive(true);
            yield return Measure("ground shadows, window off", samples, window, cam);
            Shot("streaming_view_all", cam);
            CityStreaming.Enabled = true;
            foreach (int r in new[] { 5, 3, 1 })
            {
                window.RangeOverride = r;
                yield return Measure($"VisibleRange {r}", samples, window, cam);
                Shot($"streaming_view_range{r}", cam);
                Assert.LessOrEqual(window.VisibleCount, window.RendererCount);
            }
            window.RangeOverride = -1;
            var off = samples[0]; var r1 = samples.Last();
            Assert.Less(r1.Visible, off.Visible, "VisibleRange 1 draws fewer city renderers");
            File.WriteAllLines(Path.Combine("Preview", "streaming_perf.txt"), samples.Select(s =>
                $"{s.Label}\tframe {s.Ms:F2} ms\tCamera.Render median {s.RenderMs:F2} ms max {s.RenderMaxMs:F2} ms\trenderers {s.Visible}/{window.RendererCount}"));
        }

        /// <summary>
        /// The fade-in of cells coming into view (VisibleRange &gt; 2): turning the camera away for more than 2 s and back
        /// starts fades; the cost of the dither is measured with the fade off, idle (no cell fading) and with every
        /// window cell held at alpha 0.5 (worst case), and a half-faded view is written to Preview/streaming_fade_*.png.
        /// </summary>
        [UnityTest]
        public IEnumerator CrasFadeIn()
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            yield return EnterCity("cras");
            var window = Object.FindAnyObjectByType<CityStreaming>();
            Assert.IsNotNull(window);
            var cam = Camera.main;
            window.RangeOverride = 5;
            yield return null;
            yield return null;

            // look away (the chase camera is held) for 2.5 s, then back: the cells ahead fade in again
            var chase = cam.GetComponent<ChaseCamera>();
            if (chase) chase.enabled = false;
            var fwd = cam.transform.rotation;
            int before = window.FadesStarted;
            cam.transform.rotation = Quaternion.LookRotation(Vector3.up, Vector3.forward);   // straight up: no cell in view
            yield return new WaitForSeconds(2.5f);
            cam.transform.rotation = fwd;
            yield return null;
            yield return null;
            int started = window.FadesStarted - before;
            Debug.Log($"Drift City streaming fade: {started} cells faded in after looking away, fading now {window.FadingCount}");
            Assert.Greater(started, 0, "cells coming back into view fade in");
            yield return new WaitForSeconds(0.5f);
            Assert.AreEqual(0, window.FadingCount, "a fade lasts 1 / 5 s");
            // VisibleRange 2: no fade (the client fades only above 2)
            window.RangeOverride = 2;
            yield return null;
            before = window.FadesStarted;
            cam.transform.rotation = Quaternion.LookRotation(Vector3.up, Vector3.forward);
            yield return new WaitForSeconds(2.5f);
            cam.transform.rotation = fwd;
            yield return null;
            Assert.AreEqual(before, window.FadesStarted, "no fade at VisibleRange 2");
            window.RangeOverride = 5;
            if (chase) chase.enabled = true;
            yield return null;

            var samples = new List<Sample>();
            CityStreaming.FadeEnabled = false;
            yield return Measure("fade off", samples, window, cam);
            CityStreaming.FadeEnabled = true;
            yield return Measure("fade on, idle", samples, window, cam);
            double idleCpu = window.FadeUpdateMs;
            CityStreaming.ForceFadeAlpha = 0.5f;
            yield return Measure("fade on, every cell at alpha 0.5", samples, window, cam);
            double fadingCpu = window.FadeUpdateMs;
            Shot("streaming_fade_half", cam);
            CityStreaming.ForceFadeAlpha = 0.15f;
            yield return null;
            Shot("streaming_fade_015", cam);
            CityStreaming.ForceFadeAlpha = -1f;
            yield return null;
            yield return null;
            Shot("streaming_fade_done", cam);
            var lines = samples.Select(s => $"{s.Label}\tframe {s.Ms:F2} ms\tCamera.Render median {s.RenderMs:F2} ms max {s.RenderMaxMs:F2} ms\trenderers {s.Visible}/{window.RendererCount}").ToList();
            lines.Add($"fade per-frame CPU (frustum tests + upload): idle {idleCpu:F4} ms, fading {fadingCpu:F4} ms");
            File.WriteAllLines(Path.Combine("Preview", "streaming_fade_perf.txt"), lines);
            foreach (var l in lines) Debug.Log("Drift City streaming fade perf: " + l);
            Assert.Less(idleCpu, 0.5, "fade bookkeeping stays cheap");
        }
    }
}
