using System.Collections;
using System.IO;
using System.Linq;
using DriftCity.Game;
using DriftCity.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace DriftCity.Tests
{
    /// <summary>
    /// The HUD minimap in Cras: the client's _cras.dds street image on the masked disc, centred on the car through the
    /// exe's city bounds (the pixel under the arrow must be road), zoom steps, rotation, the full map marker and the
    /// icon table. Writes Preview/minimap_cras*.png (whole screen and an enlarged crop of the disc).
    /// </summary>
    public class MinimapTests
    {
        string tempSave;

        [SetUp]
        public void SetUp()
        {
            tempSave = Path.Combine(Path.GetTempPath(), "driftcity_minimap_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempSave);
            PlayerProfile.SaveDirOverride = tempSave;
            PlayerProfile.Slot = 0;
            var p = new PlayerProfile { Name = "Mapper", Level = 1, Mito = 1000, City = "cras" };
            p.Cars.Add(new OwnedCar { VehicleIndex = 0, Color = 2 });
            p.Save();
            PlayerProfile.Current = null;
            Directory.CreateDirectory("Preview");
        }

        [TearDown]
        public void TearDown()
        {
            PlayerProfile.SaveDirOverride = null;
            foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Exclude).Where(g => g.transform.parent == null)) Object.Destroy(go);
            try { Directory.Delete(tempSave, true); } catch { }
        }

        static IEnumerator WaitFor(System.Func<bool> condition, float timeout, string what)
        {
            float t = 0;
            while (!condition() && t < timeout) { t += Time.unscaledDeltaTime; yield return null; }
            Assert.IsTrue(condition(), "timed out waiting for " + what);
        }

        /// <summary>Whole screen plus a 3x crop of the minimap disc.</summary>
        static void Shot(string name, Minimap mm)
        {
            var tex = UiManager.Instance.Capture(Camera.main);
            File.WriteAllBytes(Path.Combine("Preview", name + ".png"), tex.EncodeToPNG());
            // the MINIMAP frame sits at the bottom left of the 1024x768 capture (BASE at 29,511 size 210 in the layout)
            const int x0 = 10, w = 250, h = 250, s = 3;
            int y0 = tex.height - 745;   // texture rows count from the bottom
            var crop = new Texture2D(w * s, h * s, TextureFormat.RGB24, false);
            for (int y = 0; y < h * s; y++)
                for (int x = 0; x < w * s; x++)
                    crop.SetPixel(x, y, tex.GetPixel(x0 + x / s, y0 + y / s));
            crop.Apply();
            File.WriteAllBytes(Path.Combine("Preview", name + "_crop.png"), crop.EncodeToPNG());
            Object.Destroy(crop);
            Object.Destroy(tex);
            Debug.Log("Drift City minimap: wrote Preview/" + name + ".png");
        }

        /// <summary>The texture's pixels (GPU textures are not readable): row 0 is the image's top row (uploaded top-down).</summary>
        static Texture2D Readable(Texture src)
        {
            var rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(src, rt);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var t = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
            t.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
            t.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            return t;
        }

        /// <summary>A drawn road near the uv: opaque and light (roads are ~200-255 grey, land is transparent).</summary>
        static bool RoadNear(Texture2D img, Vector2 uv, int radius)
        {
            int cx = Mathf.RoundToInt(uv.x * img.width), cy = Mathf.RoundToInt(uv.y * img.height);
            for (int dy = -radius; dy <= radius; dy++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    var c = img.GetPixel(Mathf.Clamp(cx + dx, 0, img.width - 1), Mathf.Clamp(cy + dy, 0, img.height - 1));
                    if (c.a > 0.8f && (c.r + c.g + c.b) / 3f > 0.65f) return true;
                }
            return false;
        }

        [UnityTest]
        public IEnumerator CrasMinimapUsesTheClientImageAndMapping()
        {
            new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            var flow = new GameObject("Game").AddComponent<GameFlow>();
            flow.SkipParkingLot = true;
            yield return FrontEndSteps.ToCharacterSelect(flow);
            yield return FrontEndSteps.EnterSelected();
            yield return WaitFor(() => flow.InCity && CitySession.Current && flow.PlayerCar, 600, "city");
            Assert.AreEqual("cras", flow.City);
            if (CitySession.Current.Traffic) CitySession.Current.Traffic.MaxCars = 0;
            yield return new WaitForSeconds(2f);

            var mm = flow.GetComponent<Minimap>();
            Assert.IsNotNull(mm, "minimap component");
            Assert.IsNotNull(mm.Map, "Cras has a client map");
            Assert.AreEqual("_cras.dds", mm.Map.Image);
            Assert.AreEqual(1, mm.ZoomIndex, "0x5f2050 starts a city at zoom step 1");
            Assert.IsFalse(mm.Rotate, "shipped goption.ini has RotateMinimap=false");
            Assert.IsTrue(mm.Disc && mm.Disc.enabled && mm.Disc.MapTexture, "disc shows the city image");
            Assert.AreEqual("DriftCity/MinimapDisc", mm.Disc.material.shader.name, "mask shader found");
            Assert.AreEqual(2f * CityMapInfo.DiscRadius, mm.Disc.rectTransform.rect.width, 0.01f);

            // the uv under the arrow is the car's position through the exe tables
            var car = flow.PlayerCar;
            var expected = mm.Map.TexUv(car.transform.position);
            Assert.AreEqual(expected.x, mm.Disc.Centre.x, 1e-4f);
            Assert.AreEqual(expected.y, mm.Disc.Centre.y, 1e-4f);
            Assert.AreEqual(2f * CityMapInfo.UvPerZoom, mm.Disc.UvRadius, 1e-6f);

            // and that spot on _cras.dds is a drawn road (the car spawns on one)
            var img = Readable(mm.Disc.MapTexture);
            Debug.Log($"Drift City minimap: car at {car.transform.position} -> uv {expected} (px {expected.x * 1024:F0},{expected.y * 1024:F0})");
            Assert.IsTrue(RoadNear(img, expected, 3), $"road on _cras.dds under the car (uv {expected})");

            // a lane far from the spawn must land on a road too
            var lanes = CitySession.Current.Traffic ? CitySession.Current.Traffic.Network : null;
            int checkedLanes = 0, onRoad = 0;
            if (lanes != null)
                foreach (var lane in lanes.Lanes.Take(400))
                {
                    if (lane.Points == null || lane.Points.Length == 0) continue;
                    var w = DriftCityData.ToUnity(lane.Points[lane.Points.Length / 2]);
                    checkedLanes++;
                    if (RoadNear(img, mm.Map.TexUv(w), 3)) onRoad++;
                }
            Debug.Log($"Drift City minimap: {onRoad}/{checkedLanes} traffic lane midpoints fall on drawn roads");
            if (checkedLanes > 20) Assert.Greater(onRoad, checkedLanes * 0.8f, "lane midpoints on the image's roads");
            Object.Destroy(img);

            yield return null; yield return null;
            Shot("minimap_cras", mm);

            // zoom steps: + goes to step 0 (closest), - out to step 6 and no further
            UiManager.Instance.Find(190000).Click("ID_MINIMAP_PLUS");
            Assert.AreEqual(0, mm.ZoomIndex);
            UiManager.Instance.Find(190000).Click("ID_MINIMAP_PLUS");
            Assert.AreEqual(0, mm.ZoomIndex, "no step below 0");
            yield return null; yield return null;
            Shot("minimap_cras_zoom0", mm);
            for (int i = 0; i < 10; i++) mm.ZoomOut();
            Assert.AreEqual(6, mm.ZoomIndex);
            yield return null; yield return null;
            Assert.AreEqual(10f * CityMapInfo.UvPerZoom, mm.Disc.UvRadius, 1e-6f);
            Shot("minimap_cras_zoom6", mm);
            mm.ZoomIndex = 1;

            // north-up: the arrow carries the heading; rotating: the disc turns by it and the arrow points up.
            // Turn the car to face east-north-east first so the rotation is visible.
            var rb = car.GetComponent<Rigidbody>();
            var turned = Quaternion.Euler(0, 60f, 0);
            rb.rotation = turned; car.transform.rotation = turned;
            rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero;
            yield return null; yield return null;
            Shot("minimap_cras_heading60", mm);
            float bearing = Mathf.Atan2(car.transform.forward.x, car.transform.forward.z) * Mathf.Rad2Deg;
            Assert.AreEqual(0f, Mathf.DeltaAngle(-bearing, mm.Arrow.localEulerAngles.z), 0.5f, "arrow turned to the car's heading");
            UiManager.Instance.Find(190000).Click("ID_MINIMAP_BUTTON_MAPROT");
            Assert.IsTrue(mm.Rotate);
            yield return null; yield return null;
            Assert.AreEqual(0f, Mathf.DeltaAngle(0f, mm.Arrow.localEulerAngles.z), 0.5f, "arrow fixed upward");
            Assert.AreEqual(bearing * Mathf.Deg2Rad, mm.Disc.Rotation, 0.01f);
            Shot("minimap_cras_rotate", mm);
            UiManager.Instance.Find(190000).Click("ID_MINIMAP_BUTTON_MAPFIX");
            Assert.IsFalse(mm.Rotate);

            // the full map marker sits on the DUMMY region at the bounds fraction
            Menus.WorldMap();
            yield return null; yield return null;
            var full = UiManager.Instance.Find(500000);
            Assert.IsNotNull(full, "Cras full map");
            var marker = full.Control("ID_VIEWMAP_CR_DUMMY").transform.Find("Player") as RectTransform;
            Assert.IsNotNull(marker, "player marker under the DUMMY region");
            var f = mm.Map.Fraction(car.transform.position);
            Assert.AreEqual(f.x * 775f - 6f, marker.anchoredPosition.x, 0.5f);
            Assert.AreEqual(-f.y * 625f + 6f, marker.anchoredPosition.y, 0.5f);
            Shot("minimap_cras_worldmap", mm);
            Menus.WorldMap();

            // icon table: the four stations
            var stations = CityMapInfo.Stations("cras");
            Assert.AreEqual(4, stations.Count, "Station 1..4 in Icon_Craspeda_Client.tdf");
            Assert.AreEqual(6617.7f, stations[0].Position.x, 0.1f);
            Assert.AreEqual(5432.7f, stations[0].Position.z, 0.1f);
            Debug.Log("Drift City minimap: " + CityMapInfo.Icons("cras").Count + " Cras icons; stations " + string.Join(", ", stations.Select(s => s.ToString())));
        }

        [Test]
        public void MapTablesMatchTheExe()
        {
            var cras = CityMapInfo.ForCity("cras");
            Assert.AreEqual(new Vector2(0.5f, 0.5f), cras.TexUv(new Vector3(5000, 0, 4031.5f)));
            Assert.AreEqual(0f, cras.TexUv(new Vector3(0, 0, 8063)).y, 1e-6f, "north edge is the image top");
            var tp = CityMapInfo.ForCity("taipei");
            Assert.AreEqual(0f, tp.TexUv(new Vector3(410f / 1024f * 19900f, 0, 0)).x, 1e-4f, "Taipei's crop starts at x0 410");
            var lot = CityMapInfo.ForParkingImage("_parkingB1.dds");
            Assert.AreEqual(714f, lot.Width); Assert.AreEqual(760f, lot.Height);
            Assert.AreEqual(3, lot.DefaultZoom);
            // icon pixel scale equals the disc's (103.5 px per uv radius)
            var ppu = cras.PixelsPerUnit(1);
            Assert.AreEqual(CityMapInfo.DiscRadius / (cras.UvRadius(1) * cras.Width), ppu.x, 1e-5f);
        }
    }
}
