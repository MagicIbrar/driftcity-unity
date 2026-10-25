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
    /// The original driving model on a flat test pad: acceleration and top speed from the Speed/Accel ratings, a
    /// tapped drift (slide, held angle, recovery), the booster above top speed, braking, and the effects a drift
    /// leaves (skid marks, tyre smoke). Logs the numbers and writes Preview/drive_*.png.
    /// </summary>
    public class DrivingTests
    {
        string tempSave;

        [SetUp]
        public void SetUp()
        {
            tempSave = Path.Combine(Path.GetTempPath(), "driftcity_drive_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempSave);
            PlayerProfile.SaveDirOverride = tempSave;
            PlayerProfile.Slot = 0;
            var p = new PlayerProfile { Name = "Driver", Level = 1, Mito = 100000 };
            p.Cars.Add(new OwnedCar { VehicleIndex = 0, Color = 2 });
            p.Save();
            PlayerProfile.Current = null;
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

        static void Shot(string file)
        {
            Directory.CreateDirectory("Preview");
            File.WriteAllBytes(Path.Combine("Preview", file), UiManager.Instance.Capture(Camera.main).EncodeToPNG());
            Debug.Log("Drift City drive: wrote Preview/" + file);
        }

        [UnityTest]
        public IEnumerator OriginalDrivingModel()
        {
            new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            var flow = new GameObject("Game").AddComponent<GameFlow>();
            flow.SkipParkingLot = true;   // straight to the city; ParkingLotGateEntersCity drives through the parking lot
            yield return FrontEndSteps.ToCharacterSelect(flow);
            yield return FrontEndSteps.EnterSelected();
            yield return WaitFor(() => flow.InCity && CitySession.Current && flow.PlayerCar, 600, "city");
            var car = flow.PlayerCar;
            var rb = car.GetComponent<Rigidbody>();
            Assert.IsTrue(Object.FindAnyObjectByType<DriftCombo>(), "the SPEEDCOMBO HUD runs alongside the speedometer");
            if (CitySession.Current.Traffic) CitySession.Current.Traffic.MaxCars = 0;

            // a flat pad far below the city
            var pad = GameObject.CreatePrimitive(PrimitiveType.Cube);
            pad.name = "Test Pad";
            pad.transform.position = new Vector3(0, -5000.5f, 0);
            pad.transform.localScale = new Vector3(6000, 1, 6000);
            pad.GetComponent<Renderer>().sharedMaterial = new Material(Shader.Find("Legacy Shaders/Diffuse")) { color = new Color(0.35f, 0.36f, 0.38f) };
            void Place(Vector3 at, float yaw)
            {
                rb.position = at; rb.rotation = Quaternion.Euler(0, yaw, 0);
                car.transform.SetPositionAndRotation(at, Quaternion.Euler(0, yaw, 0));
                rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero;
                car.SetSpawn(at, Quaternion.Euler(0, yaw, 0));
            }
            Place(new Vector3(-2500, -4999.4f, -2500), 45);
            // the pad is outside the city's road network, which the client treats as off road (150 km/h cap):
            // measure the bare car model here, the road rules are checked on Cras's sand below
            var roadState = car.GetComponent<CarRoadState>();
            if (roadState) roadState.enabled = false;
            car.ExternalControl = true;
            float dt = Time.fixedDeltaTime;
            for (int i = 0; i < 60; i++) { car.ApplyControls(0, 0, false, dt); yield return new WaitForFixedUpdate(); }

            // ---- acceleration and top speed ----
            float t = 0, t100 = -1, top = 0;
            while (t < 14f)
            {
                car.ApplyControls(1, 0, false, dt);
                yield return new WaitForFixedUpdate();
                t += dt;
                if (t100 < 0 && car.SpeedKmh >= 100f) t100 = t;
                top = Mathf.Max(top, car.SpeedKmh);
            }
            Debug.Log($"Drift City drive: 0-100 km/h in {t100:F2} s, {top:F0} km/h after 14 s (rated top {car.TopSpeedKmh:F0}), gear {car.Gear}, rpm {car.Rpm:F0}");
            Shot("drive_1_topspeed.png");
            Assert.Greater(t100, 0f, "reaches 100 km/h");
            Assert.Less(t100, 8f, "0-100 within 8 s");
            Assert.Greater(top, car.TopSpeedKmh * 0.85f, "close to the rated top speed");
            Assert.Less(top, car.TopSpeedKmh * 1.08f, "the limiter holds top speed without the booster");

            // ---- drift: tap the button while steering, then hold the steering into the slide ----
            float maxSlip = 0; bool slid = false, recovered = false;
            var effects = car.GetComponent<CarEffects>();
            int smokeSeen = 0;
            for (t = 0; t < 3.5f; t += dt)
            {
                bool tap = t < 0.15f;
                float steer = t < 2.2f ? 1f : 0f;
                car.ApplyControls(1, steer, tap, dt);
                yield return new WaitForFixedUpdate();
                maxSlip = Mathf.Max(maxSlip, Mathf.Abs(car.SlipAngle));
                if (car.DriftState != 0) slid = true;
                if (slid && car.DriftState == 0) recovered = true;
                var ps = GameObject.Find("Tyre smoke");
                if (ps && ps.TryGetComponent<ParticleSystem>(out var smoke)) smokeSeen = Mathf.Max(smokeSeen, smoke.particleCount);
                if (Mathf.Abs(t - 1.0f) < dt * 0.5f) Shot("drive_2_drift.png");
            }
            int markVerts = Object.FindObjectsByType<MeshFilter>(FindObjectsInactive.Exclude)
                .Where(m => m.sharedMesh && m.transform.root.name.EndsWith(" effects")).Sum(m => m.sharedMesh.vertexCount);
            Debug.Log($"Drift City drive: drift max slip {maxSlip:F0} deg (SlipAngle units), slid={slid}, recovered={recovered}, speed after {car.SpeedKmh:F0} km/h, skid-mark vertices {markVerts}, smoke particles {smokeSeen}");
            Assert.IsTrue(slid, "tapping drift while steering at speed starts a slide");
            Assert.IsTrue(recovered, "the slide ends once the steering straightens");
            Assert.IsNotNull(effects, "drift effects on the player car");
            Assert.Greater(markVerts, 0, "the slide lays skid marks");
            Assert.Greater(smokeSeen, 0, "the slide makes tyre smoke");

            // ---- booster: above top speed while boosting ----
            Place(new Vector3(-2000, -4999.4f, -2000), 45);
            for (int i = 0; i < 30; i++) { car.ApplyControls(0, 0, false, dt); yield return new WaitForFixedUpdate(); }
            for (t = 0; t < 10f; t += dt) { car.ApplyControls(1, 0, false, dt); yield return new WaitForFixedUpdate(); }
            float before = car.SpeedKmh, boosted = 0;
            car.BoostGauge = 1f;
            for (t = 0; t < 4f; t += dt)
            {
                car.ApplyControls(1, 0, false, dt, true);
                yield return new WaitForFixedUpdate();
                boosted = Mathf.Max(boosted, car.SpeedKmh);
                if (Mathf.Abs(t - 2.5f) < dt * 0.5f) Shot("drive_3_boost.png");
            }
            Debug.Log($"Drift City drive: booster {before:F0} -> {boosted:F0} km/h, gauge after {car.BoostGauge:F2}");
            Assert.Greater(boosted, before + 15f, "the booster pushes past top speed");

            // ---- braking ----
            float stop = 0;
            for (t = 0; t < 10f && car.SpeedKmh > 1f; t += dt) { car.ApplyControls(-1, 0, false, dt); yield return new WaitForFixedUpdate(); stop = t; }
            Debug.Log($"Drift City drive: stopped from boost speed in {stop:F2} s");
            Assert.Less(stop, 8f, "full brake stops the car");

            // ---- Cras's sand: the road state reads the zone and the wheels throw sand (sand.hit, 0x6b61a0) ----
            var roads = RoadMapQuery.For(flow.City);
            Assert.IsNotNull(roads, "road map loaded for the city");
            Vector3? sandSpot = null;
            for (float x = 2330; x < 6610 && sandSpot == null; x += 15)
                for (float z = 6050; z < 7420 && sandSpot == null; z += 15)
                    if (roads.InSand(new Vector3(x, 0, z)) && Physics.Raycast(new Vector3(x, 2000, z), Vector3.down, out var hit, 4000) && roads.InSand(hit.point))
                        sandSpot = hit.point;
            Debug.Log($"Drift City drive: sand spot {(sandSpot.HasValue ? sandSpot.Value.ToString() : "none")}");
            if (sandSpot.HasValue)
            {
                Place(sandSpot.Value + Vector3.up * 0.6f, 0);
                if (roadState) roadState.enabled = true;
                var state = car.GetComponent<CarRoadState>();
                int sandSeen = 0;
                for (t = 0; t < 3f; t += dt)
                {
                    car.ApplyControls(car.SpeedKmh < 40f ? 1f : 0f, 0, false, dt);
                    yield return new WaitForFixedUpdate();
                    var sp = GameObject.Find("Sand splash");
                    if (sp && sp.TryGetComponent<ParticleSystem>(out var ps)) sandSeen = Mathf.Max(sandSeen, ps.particleCount);
                    if (Mathf.Abs(t - 2f) < dt * 0.5f) Shot("drive_4_sand.png");
                }
                Debug.Log($"Drift City drive: on sand: state.Sand={state && state.Sand} speed {car.SpeedKmh:F0} km/h, sand splash particles {sandSeen}");
                Assert.IsTrue(state && state.Sand, "the road state knows the car is on sand");
                Assert.Greater(sandSeen, 0, "the wheels throw sand");
            }
            car.ExternalControl = false;
        }
    }
}
