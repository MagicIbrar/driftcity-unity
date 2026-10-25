using System.Collections;
using System.Linq;
using DriftCity.Game;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace DriftCity.Tests
{
    /// <summary>
    /// The one-shot effect NIFs (GameEffects): every NIF the client plays on game events builds, screen effects play
    /// over their key range on the UI effect camera and then go, the arrest banners queue behind each other, map object
    /// EffectIDs pick the palm tree / hydrant NIFs with the client's lifetimes, and attached effects follow their car.
    /// </summary>
    public class EffectsTests
    {
        [SetUp]
        public void SetUp()
        {
            var root = DriftCityData.ResolveGameRoot(@"D:\Drift_City_Remastered_Alpha_08-2025\Drift City Remastered Alpha");
            Assert.IsNotNull(root, "installed client or imported data found");
            if (DriftCityData.Files == null) DriftCityData.Mount(root);
            GameEffects.Sounds = false;
            GameEffects.Enabled = true;
        }

        [TearDown]
        public void TearDown()
        {
            GameEffects.Clear();
            GameEffects.Sounds = true;
            foreach (var fx in Object.FindObjectsByType<NifEffect>(FindObjectsInactive.Include)) Object.Destroy(fx.gameObject);
        }

        static readonly string[] WorldNifs =
        {
            "levelup", "getitem", "huv_explosion", "palmtree_explosion01", "palmtree_explosion02", "palmtree_explosion03",
            "hydrant_explosion", "jump_up", "jump_down", "graze_l", "graze_r", "damage_smoke",
        };

        static readonly string[] ScreenNifs =
        {
            "ui_levelup", "arrest_success", "arrest_fail", "beat_success", "beat_fail", "pvp_winner", "pvp_finish",
            "congratulation", "critical", "goldentime", "im_success", "im_failed", "arbeit_com", "arbeit_fail",
            "quest_success", "quest_fail", "qs_newrecord", "channel_count",
        };

        [UnityTest]
        public IEnumerator EveryEventNifBuilds()
        {
            foreach (var n in WorldNifs)
            {
                var fx = NifEffect.Create($@"effects\{n}.nif");
                Assert.IsNotNull(fx, n + " loads");
                int meshes = fx.GetComponentsInChildren<MeshRenderer>(true).Length;
                int systems = fx.Particles ? fx.Particles.GetComponentsInChildren<ParticleSystem>().Length : 0;
                Debug.Log($"Drift City effects test: {n} meshes {meshes} particle systems {systems} keys {fx.KeyStart:F3}..{fx.KeyEnd:F3}");
                Assert.Greater(meshes + systems, 0, n + " has something to draw");
                Object.Destroy(fx.gameObject);
            }
            foreach (var n in ScreenNifs)
            {
                var fx = NifEffect.Create($@"effects\ui\{n}.nif");
                Assert.IsNotNull(fx, n + " loads");
                Assert.Greater(fx.KeyEnd, 0.2f, n + " has a key range (its screen time)");
                Debug.Log($"Drift City effects test: ui\\{n} keys {fx.KeyStart:F3}..{fx.KeyEnd:F3}");
                Object.Destroy(fx.gameObject);
            }
            Assert.AreEqual(60, GameEffects.ScreenTable.Length, "the client's UI effect table has 60 entries (0x6b3b00)");
            Assert.AreEqual("arrest_success", GameEffects.ScreenTable[10]);
            Assert.AreEqual("pvp_winner", GameEffects.ScreenTable[30]);
            yield return null;
        }

        [UnityTest]
        public IEnumerator ScreenEffectPlaysOverItsKeyRangeAndQueues()
        {
            var cam = Camera.main;
            if (!cam) { cam = new GameObject("Main Camera") { tag = "MainCamera" }.AddComponent<Camera>(); }
            var fx = GameEffects.Screen("arrest_success");
            Assert.IsNotNull(fx);
            Assert.IsTrue(GameEffects.ScreenCamera.enabled, "the UI effect camera draws while a screen effect plays");
            Assert.IsTrue(GameEffects.ScreenCamera.orthographic);
            Assert.AreEqual(38.4f, GameEffects.ScreenCamera.orthographicSize, 1e-4f);
            Assert.AreEqual(0, cam.cullingMask & (1 << GameEffects.ScreenLayer), "the game camera does not draw the screen layer");
            Assert.AreEqual(GameEffects.ScreenLayer, fx.gameObject.layer);
            Assert.AreEqual(new Vector3(0f, 0f, 100f), fx.transform.localPosition, "placed at Gamebryo (0, 100, 0) before the camera");
            float duration = fx.Duration;
            Assert.AreEqual(fx.KeyEnd, duration, 1e-4f, "screen time = the NIF's key range");

            // a second arrest banner waits for the first (0x69b900)
            Assert.IsNull(GameEffects.Screen("arrest_fail"), "arrest_fail queued while arrest_success shows");
            Assert.IsFalse(GameEffects.ScreenPlaying("arrest_fail"));

            float t = 0f;
            while (GameEffects.ScreenPlaying("arrest_success") && t < duration + 2f) { t += Time.deltaTime; yield return null; }
            Assert.IsFalse(GameEffects.ScreenPlaying("arrest_success"), "gone after its key range");
            Assert.AreEqual(duration, t, 0.25f, "lasted its key range");
            Assert.IsTrue(GameEffects.ScreenPlaying("arrest_fail"), "the queued banner follows");
            GameEffects.Clear();
            yield return null;
            Assert.IsFalse(GameEffects.ScreenCamera.enabled, "camera off with nothing to draw");
        }

        [UnityTest]
        public IEnumerator PropHitsUseTheEffectIdsAndLifetimes()
        {
            var ids = GameEffects.PropEffectIds("moonpalace");
            Assert.IsTrue(ids.ContainsValue(11) && ids.ContainsValue(12) && ids.ContainsValue(13), "moonpalace palm trees carry EffectID 11/12/13");
            Assert.IsTrue(ids.ContainsValue(2), "the fire hydrant carries EffectID 2");
            Assert.AreEqual("palmtree_explosion02", GameEffects.PropEffect(12));
            Assert.IsNull(GameEffects.PropEffect(3), "lamp posts and signs have no explosion NIF");

            var at = new Vector3(10f, 2f, -5f);
            var palm = GameEffects.PropHit(11, at);
            var hydrant = GameEffects.PropHit(2, at + Vector3.right * 5f);
            Assert.IsNotNull(palm); Assert.IsNotNull(hydrant);
            Assert.AreEqual(3.333f, palm.Duration, 1e-3f);
            Assert.AreEqual(8.333f, hydrant.Duration, 1e-3f);
            Assert.AreEqual(at, palm.transform.position);
            Assert.AreEqual(Quaternion.identity, palm.transform.rotation);
            var tree = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Assert.IsNull(GameEffects.MakeBreakable(tree, 3), "only objects with an explosion NIF break");
            var zone = GameEffects.MakeBreakable(tree, 12);
            Assert.IsNotNull(zone);
            Assert.AreEqual(2, zone.gameObject.layer, "the trigger stays out of raycasts");
            yield return null;
            var trigger = zone.GetComponent<CapsuleCollider>();
            Assert.IsTrue(trigger && trigger.isTrigger, "a trigger around the object's foot");
            Object.Destroy(tree);
            // the client keeps at most 10 of a kind
            for (int i = 0; i < 12; i++) GameEffects.PropHit(13, at);
            yield return null;
            Assert.LessOrEqual(Object.FindObjectsByType<NifEffect>(FindObjectsInactive.Exclude).Count(f => f.Nif.Contains("palmtree")), 10);
        }

        [UnityTest]
        public IEnumerator AttachedEffectsFollowTheirCar()
        {
            var car = new GameObject("Car");
            car.transform.SetPositionAndRotation(new Vector3(3f, 0f, 4f), Quaternion.Euler(0f, 90f, 0f));
            var up = GameEffects.Attach("jump_up", car.transform, Vector3.zero, false, 1.333f);
            var graze = GameEffects.Attach("graze_l", car.transform, new Vector3(-0.54f, 0.52f, 1.76f), true);
            Assert.IsNotNull(up); Assert.IsNotNull(graze);
            car.transform.position += new Vector3(5f, 0f, 0f);
            yield return null;
            Assert.AreEqual(car.transform.position.x, up.transform.position.x, 1e-3f, "follows the car's translation");
            Assert.AreEqual(Quaternion.identity, up.transform.rotation, "no rotation, as the client's SetTranslate only");
            Assert.Less(Vector3.Distance(car.transform.TransformPoint(new Vector3(-0.54f, 0.52f, 1.76f)), graze.transform.position), 1e-3f, "graze sits at its offset");
            Assert.Less(Quaternion.Angle(car.transform.rotation, graze.transform.rotation), 0.01f, "and turns with the car");
            float t = 0f;
            while (up && t < 2f) { t += Time.deltaTime; yield return null; }
            Assert.IsFalse(up, "jump_up removed after 1.333 s");
            Assert.AreEqual(1.333f, t, 0.2f);
            Object.Destroy(car);
            yield return null;
            yield return null;
            Assert.IsFalse(graze, "an attached effect goes with its car");
        }
    }
}
