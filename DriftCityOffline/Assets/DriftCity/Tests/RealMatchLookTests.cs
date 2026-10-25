using System.Collections;
using System.IO;
using System.Linq;
using DriftCity.Game;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace DriftCity.Tests
{
    /// <summary>
    /// Real Match looks (DriftCity_RE/PVP.md 14.4): the 16 visual.xlt looks on the RM car (paint, decal in its decal
    /// colour, neon) and the skin that dresses the player's own / a remote car as its slot's RM car for the race.
    /// Writes Preview/rm_looks.png and Preview/rm_skin_{off,on}.png. Needs the installed client (or GameData).
    /// </summary>
    public class RealMatchLookTests
    {
        [TearDown]
        public void TearDown()
        {
            foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Exclude).Where(g => g.transform.parent == null)) Object.Destroy(go);
        }

        static void Mount()
        {
            var root = DriftCityData.ResolveGameRoot(@"D:\Drift_City_Remastered_Alpha_08-2025\Drift City Remastered Alpha");
            Assert.IsNotNull(root, "installed client or imported data found");
            if (DriftCityData.Files == null) DriftCityData.Mount(root);
            GameTables.Load();
        }

        static Texture2D Shot(string name, Vector3 eye, Vector3 at, int w = 1280, int h = 640)
        {
            var go = new GameObject("RM Shot Camera");
            var cam = go.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.32f, 0.36f, 0.42f);
            cam.fieldOfView = 35f;
            go.transform.position = eye; go.transform.LookAt(at);
            var rt = new RenderTexture(w, h, 24);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            cam.targetTexture = null;
            Directory.CreateDirectory("Preview");
            File.WriteAllBytes(Path.Combine("Preview", name + ".png"), tex.EncodeToPNG());
            rt.Release(); Object.Destroy(go);
            Debug.Log("Drift City RM: wrote Preview/" + name + ".png");
            return tex;
        }

        static void Floor()
        {
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "RM Floor";
            floor.transform.localScale = Vector3.one * 6f;
            floor.GetComponent<Renderer>().sharedMaterial = new Material(Shader.Find("Unlit/Color")) { color = new Color(0.18f, 0.18f, 0.2f) };
        }

        [UnityTest]
        public IEnumerator LooksAndSkin()
        {
            Mount();
            OriginalLook.ApplyGarage();
            var duel = Pvp.RealMatchCar();
            Assert.IsNotNull(duel, "the RM car (Duel)");

            // every look's decal exists for the Duel, and its colours are in the colour tables
            int decals = 0, neons = 0, decalColors = 0;
            for (int slot = 0; slot < 16; slot++)
            {
                var look = Pvp.RealMatchLookFor(slot);
                if (CarPaint.HasDecal(duel, look.Decal)) decals++;
                if (VehicleColors.TryGet(VehicleColors.Table.Neon, look.Neon, out _)) neons++;
                if (VehicleColors.TryGet(VehicleColors.Table.Decal, look.DecalColor, out _)) decalColors++;
            }
            Debug.Log($"Drift City RM: Duel unique id {duel.UniqueId}, model {duel.ModelBase}: decal layouts {decals}/16, neon colours {neons}/16, decal colours {decalColors}/16");
            Assert.AreEqual(16, decals, "visual.xlt decals are the Duel's decal.nxt layouts");
            Assert.AreEqual(16, neons, "neon colour ids");
            Assert.AreEqual(16, decalColors, "decal colour ids");

            // four Duels in slots 0..3 side by side
            Floor();
            var duels = new System.Collections.Generic.List<GameObject>();
            for (int slot = 0; slot < 4; slot++)
            {
                var car = CarFactory.BuildVisual(duel, null, new Vector3((slot - 1.5f) * 2.6f, 0, 0), Quaternion.Euler(0, 150, 0), out _, out _);
                duels.Add(car);
                CarPaint.ApplyRealMatch(car, duel, Pvp.RealMatchLookFor(slot));
                Assert.IsTrue(car.GetComponentsInChildren<Renderer>().Any(r => r.sharedMaterial && r.sharedMaterial.GetTexture("_MainTex") is RenderTexture), $"slot {slot}: decal composited into the paint");
                Assert.IsNotNull(car.transform.Find("Neon"), $"slot {slot}: underbody neon");
                if (slot == 0)
                {
                    var look0 = Pvp.RealMatchLookFor(0);
                    var layers = CarPaint.Layers(duel, look0.Decal);
                    Debug.Log($"Drift City RM: slot 0 decal {look0.Decal}: {layers.Count} stamps, sheet colours " + string.Join(", ", layers.Select(l => $"{l.Texture} part {l.Part} {l.Color}")));
                    foreach (var nr in car.transform.Find("Neon").GetComponentsInChildren<Renderer>())
                    {
                        var nm = nr.sharedMaterial;
                        Debug.Log($"Drift City RM: neon '{nr.name}' shader {nm.shader.name} tex {(nm.mainTexture ? nm.mainTexture.name : "-")} queue {nm.renderQueue} " +
                                  (nm.HasProperty("_Unlit") ? $"unlit {nm.GetFloat("_Unlit")} vc {nm.GetFloat("_UseVertexColor")} mode {nm.GetFloat("_VertexMode")} src {nm.GetFloat("_SrcBlend")} dst {nm.GetFloat("_DstBlend")} color {nm.GetColor("_Color")} emis {nm.GetColor("_EmissionColor")}" : ""));
                    }
                    var rt = car.GetComponentsInChildren<Renderer>().Select(r => r.sharedMaterial ? r.sharedMaterial.GetTexture("_MainTex") as RenderTexture : null).First(t => t);
                    RenderTexture.active = rt;
                    var paint = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                    paint.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); paint.Apply();
                    RenderTexture.active = null;
                    Directory.CreateDirectory("Preview");
                    File.WriteAllBytes(Path.Combine("Preview", "rm_paint_slot0.png"), paint.EncodeToPNG());
                }
            }
            yield return null;
            Shot("rm_looks", new Vector3(0, 3.2f, -9.5f), new Vector3(0, 0.4f, 0));
            foreach (var c in duels) Object.Destroy(c);
            yield return null;

            // the player's own car (not a Duel) dressed as slot 5's RM car, then undressed
            var own = GameTables.PlayerCars.First(v => v != duel && v.ReqLevel <= 1);
            var player = CarFactory.BuildDrivable(own, 0, Vector3.zero, Quaternion.Euler(0, 150, 0));
            player.GetComponent<Rigidbody>().isKinematic = true;
            var ownRenderers = player.GetComponentsInChildren<MeshRenderer>(true).Where(r => !r.forceRenderingOff).ToList();
            yield return null;
            var off = Shot("rm_skin_off", new Vector3(0, 2.4f, -6.5f), new Vector3(0, 0.4f, 0), 960, 540);
            var skin = RealMatchSkin.Apply(player.gameObject, 5);
            Assert.IsNotNull(skin, "skin built");
            Assert.IsNotNull(skin.Skin, "the Duel visual under the car");
            Assert.IsTrue(ownRenderers.All(r => r.forceRenderingOff), "the car's own meshes are hidden");
            Assert.AreSame(skin, RealMatchSkin.Apply(player.gameObject, 5), "same slot: no rebuild");
            yield return null;
            yield return null;
            var on = Shot("rm_skin_on", new Vector3(0, 2.4f, -6.5f), new Vector3(0, 0.4f, 0), 960, 540);
            // the wheels follow the car's wheel visuals
            Assert.IsTrue(skin.Skin.GetComponentsInChildren<Renderer>().Length > 5);
            int diff = 0;
            var a = off.GetPixels32(); var b = on.GetPixels32();
            for (int i = 0; i < a.Length; i += 7) if (Mathf.Abs(a[i].r - b[i].r) + Mathf.Abs(a[i].g - b[i].g) + Mathf.Abs(a[i].b - b[i].b) > 30) diff++;
            Debug.Log($"Drift City RM: skin changed {diff * 7 * 100f / a.Length:F1}% of the picture");
            Assert.Greater(diff, a.Length / 7 / 50, "the skinned car looks different");
            RealMatchSkin.Remove(player.gameObject);
            yield return null;
            Assert.IsNull(player.GetComponent<RealMatchSkin>());
            Assert.IsNull(player.transform.Find("Real Match Skin"), "skin destroyed");
            Assert.IsTrue(ownRenderers.All(r => !r || !r.forceRenderingOff), "own meshes back");
        }
    }
}
