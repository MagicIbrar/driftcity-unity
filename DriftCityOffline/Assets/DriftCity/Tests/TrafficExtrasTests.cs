using System.Collections;
using System.IO;
using System.Linq;
using DriftCity.Core;
using DriftCity.Game;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace DriftCity.Tests
{
    /// <summary>
    /// Trains, convoys and pedestrians (DriftCity_RE/TRAFFIC.md sections 9-11): Cras pedestrians appear around the car,
    /// walk their pcs routes on the timetable and jump out of the car's way; a boss 11001 gets its four escorts which
    /// follow it; the Taipei MRT runs its timetable along trainpath.chpath and stops at the stations. Screenshots go to
    /// Preview/extras_*.png. Needs the installed client (or GameData).
    /// </summary>
    public class TrafficExtrasTests
    {
        string tempSave;

        [SetUp]
        public void SetUp() { }

        void Profile(string city)
        {
            tempSave = Path.Combine(Path.GetTempPath(), "driftcity_extras_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempSave);
            PlayerProfile.SaveDirOverride = tempSave;
            PlayerProfile.Slot = 0;
            var p = new PlayerProfile { Name = "Walker", Level = 10, Mito = 100000, City = city };
            p.Cars.Add(new OwnedCar { VehicleIndex = 0, Color = 2 });
            p.Save();
            PlayerProfile.Current = null;
            Directory.CreateDirectory("Preview");
        }

        [TearDown]
        public void TearDown()
        {
            Trains.ClockOverride = null;
            Trains.ViewOption = 0;   // back to goption.ini VisibleRange
            PlayerProfile.SaveDirOverride = null;
            foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Exclude).Where(g => g.transform.parent == null)) Object.Destroy(go);
            try { if (tempSave != null) Directory.Delete(tempSave, true); } catch { }
        }

        static IEnumerator WaitFor(System.Func<bool> condition, float timeout, string what)
        {
            float t = 0;
            while (!condition() && t < timeout) { t += Time.unscaledDeltaTime; yield return null; }
            Assert.IsTrue(condition(), "timed out waiting for " + what);
        }

        IEnumerator EnterCity(string city)
        {
            Profile(city);
            new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            var flow = new GameObject("Game").AddComponent<GameFlow>();
            flow.SkipParkingLot = true;
            yield return FrontEndSteps.ToCharacterSelect(flow);
            yield return FrontEndSteps.EnterSelected();
            yield return WaitFor(() => flow.InCity && CitySession.Current && flow.PlayerCar, 600, "city " + city);
            Assert.AreEqual(city, flow.City);
            yield return new WaitForSeconds(1f);
        }

        /// <summary>Renders the scene from a free camera (same settings as the game camera) to Preview/name.png.</summary>
        static void Shot(string name, Vector3 eye, Vector3 at, float fov = 50f)
        {
            var go = new GameObject("Shot Camera");
            var cam = go.AddComponent<Camera>();
            if (Camera.main) cam.CopyFrom(Camera.main);
            cam.fieldOfView = fov;
            cam.nearClipPlane = 0.1f;
            go.transform.position = eye;
            go.transform.LookAt(at);
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
            Debug.Log("Drift City extras: wrote Preview/" + name + ".png");
        }

        [UnityTest]
        public IEnumerator CrasPedestriansAndConvoy()
        {
            yield return EnterCity("cras");
            var session = CitySession.Current;
            var car = session.Car;

            // ---------- pedestrians ----------
            var pb = Passerby.Attach(session, "cras");
            Assert.IsNotNull(pb, "cras has passerby.pcs");
            Assert.AreEqual(2366, pb.Data.Lines.Count, "time-key lines");
            Assert.AreEqual(587, pb.Data.Routes.Count, "routes");
            Assert.Greater(pb.Agents.Count, 1000, "pedestrians placed on the routes");
            // put the car next to the first pavement walker so some are around
            var first = pb.Agents.First(a => !a.Crossing);
            var spot = pb.SchedulePosition(first, Trains.Now, out var fdir, out _);
            var rb = car.GetComponent<Rigidbody>();
            var side = Vector3.Cross(Vector3.up, fdir.normalized);
            rb.position = spot + side * 12f + Vector3.up * 1.5f; rb.linearVelocity = Vector3.zero;
            car.transform.position = rb.position;
            yield return new WaitForSeconds(1.5f);
            yield return WaitFor(() => pb.Active.Count > 0, 10, "pedestrians near the car");
            Debug.Log($"Drift City extras: {pb.Active.Count} pedestrians within {Passerby.Radius} m");

            // they sit on their timetable spots and move along their routes
            var peds = pb.Active.Values.Where(p => p.State == 0).Take(8).ToList();
            var before = peds.Select(p => p.transform.position).ToList();
            yield return new WaitForSeconds(2f);
            float t = Trains.Now;
            int moved = 0;
            for (int i = 0; i < peds.Count; i++)
            {
                var p = peds[i];
                if (!p || !p.gameObject.activeSelf || p.State != 0) continue;
                var want = pb.SchedulePosition(p.Agent, t, out _, out bool walking);
                Assert.Less(Vector3.Distance(p.transform.position, want), 0.6f, "pedestrian on its timetable spot");
                float d = Vector3.Distance(p.transform.position, before[i]);
                if (walking && d > 1.5f) moved++;
                Assert.Less(d, 4.5f, "walking pace (1.45 m/s)");
                Assert.IsNotNull(p.Clip, "animation playing");
            }
            Assert.Greater(moved, 0, "someone walked");
            var skinned = pb.GetComponentsInChildren<SkinnedMeshRenderer>().Where(s => s.enabled && s.sharedMesh).ToList();
            Assert.Greater(skinned.Count, 0, "skinned bodies");

            foreach (var q in pb.Active.Values.Take(6))
            {
                bool g = Passerby.Ground(q.transform.position, out float gy);
                var feet = q.GetComponentsInChildren<SkinnedMeshRenderer>().Where(r => r.enabled).Select(r => r.bounds.min.y).DefaultIfEmpty(0).Min();
                Debug.Log($"Drift City extras: ped {q.Agent.Index} path y {q.transform.position.y:F2} ground {(g ? gy.ToString("F2") : "none")} mesh bottom {feet:F2}");
            }
            var ped = pb.Active.Values.First(p => p.State == 0);
            var pp = ped.transform.position;
            pb.SchedulePosition(ped.Agent, Trains.Now, out var fwd, out _);   // walking direction (the model faces it)
            fwd.y = 0; fwd.Normalize();
            Assert.Greater(Vector3.Dot(-ped.transform.forward, fwd), 0.9f, "body faces its walking direction (the bipeds face -Y)");
            Shot("extras_pedestrian_close", pp + fwd * 4f + Vector3.up * 1.0f, pp + Vector3.up * 0.9f, 40f);
            Shot("extras_pedestrian_side", pp + Vector3.Cross(Vector3.up, fwd) * 4f + Vector3.up * 1.0f, pp + Vector3.up * 0.9f, 40f);
            Shot("extras_pedestrians_street", Vector3.Lerp(car.transform.position, pp, 0.3f) + Vector3.up * 4f - fwd * 6f, pp + Vector3.up, 60f);

            // a car coming at a pedestrian sends it aside, then it runs back
            Trains.ClockOverride = Trains.Now;  // freeze the timetable so the target stays put
            yield return null;
            ped = pb.Active.Values.First(p => p.State == 0);
            pp = ped.transform.position;
            var approach = (pp - car.transform.position); approach.y = 0;
            rb.isKinematic = true;
            rb.position = pp - fwd * 1.6f + Vector3.up * 0.3f;
            rb.rotation = Quaternion.LookRotation(fwd, Vector3.up);
            car.transform.SetPositionAndRotation(rb.position, rb.rotation);
            yield return WaitFor(() => ped.State == 1, 2, "pedestrian jumps aside");
            yield return new WaitForSeconds(0.4f);
            float away = Vector3.Distance(ped.transform.position, pp);
            Debug.Log($"Drift City extras: dodged {away:F1} m, state {ped.State}");
            Shot("extras_pedestrian_dodge", pp + Vector3.up * 6f - fwd * 9f, pp, 60f);
            Assert.Greater(away, 2f, "jumped away from the car");
            rb.position = pp + Vector3.up * 0.3f + side * 40f;
            car.transform.position = rb.position;
            yield return WaitFor(() => ped.State == 0, 6, "pedestrian back on its route");
            rb.isKinematic = false;
            Trains.ClockOverride = null;

            // ---------- convoy ----------
            var traffic = session.Traffic;
            // the convoy on its own (city traffic would make the escorts brake behind other cars)
            traffic.MaxCars = 0; traffic.TrimTo(0);
            Assert.IsTrue(traffic.NearestLane(car.transform.position, out int lane, out float along, -1, 400f), "lane near the car");
            var boss = GameTables.BossHuvs.Find(h => h.UniqueId == Convoy.LeaderA);
            Assert.IsNotNull(boss, "boss 11001 in vehiclelist");
            var leader = traffic.SpawnModel($@"vehicle\huv\{boss.FileName}", lane, along + 180f > traffic.Network.Lanes[lane].Length ? along : along + 180f, 60f);
            Assert.IsNotNull(leader, "leader spawned");
            var convoy = Convoy.Attach(traffic, leader, Convoy.LeaderA);
            Assert.IsNotNull(convoy);
            Assert.AreEqual(4, convoy.Escorts.Count, "11001 leads 10001-10004");
            rb.isKinematic = true;
            rb.position = leader.transform.position + Vector3.up * 30f; car.transform.position = rb.position;
            yield return new WaitForSeconds(10f);
            var lp = leader.transform.position;
            for (int i = 0; i < convoy.Escorts.Count; i++)
            {
                var e = convoy.Escorts[i];
                Assert.IsTrue(e, "escort alive");
                // the exe's escorts drive the boss's route at their own speed (0x445090 / 0x4912d0, TRAFFIC.md section 15)
                float gap = Vector3.Distance(e.FollowTarget.transform.position, e.transform.position);
                Debug.Log($"Drift City extras: escort {i} {gap:F1} m from the boss, speed {e.Speed * 3.6f:F0} km/h lane {e.Lane}, boss on {e.FollowTarget.Lane}");
                Assert.Less(gap, 250f, "escort keeps up with the boss");
            }
            Assert.Greater(leader.Speed, 1f, "leader drives");
            var mid = convoy.Escorts[1].transform.position;
            Shot("extras_convoy", mid + Vector3.up * 35f - leader.transform.forward * 60f, mid, 55f);
        }

        [UnityTest]
        public IEnumerator TaipeiTrainsRunTheTimetable()
        {
            // the timetable itself (no city needed)
            Profile("taipei");
            yield return null;
            yield return EnterCityTaipei();
        }

        IEnumerator EnterCityTaipei()
        {
            new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            var flow = new GameObject("Game").AddComponent<GameFlow>();
            flow.SkipParkingLot = true;
            yield return FrontEndSteps.ToCharacterSelect(flow);
            yield return FrontEndSteps.EnterSelected();
            yield return WaitFor(() => flow.InCity && CitySession.Current && flow.PlayerCar, 600, "city taipei");
            Assert.AreEqual("taipei", flow.City);
            yield return new WaitForSeconds(1f);

            var trains = Trains.Attach(CitySession.Current, "taipei");
            Assert.IsNotNull(trains, "taipei has trainpath.chpath");
            Assert.AreEqual(2, trains.Lines.Count, "two directions");
            Assert.AreEqual(6, trains.All.Count, "three trains per direction");
            Assert.AreEqual(3, trains.Lines[0].Stops.Length, "three stations each way");
            Assert.AreEqual(33.38f, trains.Lines[0].Stops[0], 0.01f);
            Assert.IsNull(Trains.Attach("cras", null, null), "no trains in cras");

            // cruising: 100 km/h = one time key per second
            var line = trains.Lines[0];
            float p0 = line.Position(0, 5f, out float v0, out _), p1 = line.Position(0, 15f, out _, out _);
            Assert.AreEqual(10f, p1 - p0, 0.01f, "cruise 27.78 m/s");
            Assert.AreEqual(27.78f, v0, 0.05f);
            // stopped at the first station for 8 s
            float brake = 2f * 300f / 27.7777786f;
            float atStation = (33.3798f - 10.8f) + brake;   // cruise to the brake point, then brake
            Assert.AreEqual(33.38f, line.Position(0, atStation + 4f, out float vs, out bool stopped), 0.01f);
            Assert.IsTrue(stopped); Assert.AreEqual(0f, vs, 1e-4f);
            Assert.Greater(line.Position(0, atStation + 8f + 5f, out _, out _), 33.38f, "leaves after the dwell");

            // the cars follow the path in the world
            Trains.ViewOption = 50;   // show every train for the check
            Trains.ClockOverride = 5f;
            yield return new WaitForFixedUpdate(); yield return new WaitForFixedUpdate();
            var tr = trains.All[0];
            Assert.IsTrue(tr.Visible && tr.Root.activeSelf, "train shown");
            var head0 = tr.Cars[0].position;
            var want = DriftCityData.ToUnity(line.Path.Sample(tr.Position, out _));
            Assert.Less(Vector3.Distance(head0, want), 0.5f, "first car on the path");
            Assert.AreEqual(TrainSchedule.CarSpacing, Vector3.Distance(tr.Cars[0].position, tr.Cars[1].position), 1.0f, "cars 12 m apart");
            Assert.IsTrue(tr.Cars[0].isKinematic && tr.Cars[0].GetComponent<BoxCollider>(), "solid, unstoppable cars");
            Trains.ClockOverride = 15f;   // still cruising (braking for the first station starts at 22.6 s)
            yield return new WaitForFixedUpdate(); yield return new WaitForFixedUpdate(); yield return new WaitForFixedUpdate();
            float moved = Vector3.Distance(tr.Cars[0].position, head0);
            float keys = tr.Position - line.Position(0, 5f, out _, out _);
            Debug.Log($"Drift City extras: train 0 moved {keys:F2} keys ({moved:F0} m straight line) in 10 s");
            Assert.AreEqual(10f, keys, 0.05f, "10 s at 100 km/h = 10 time keys (277.8 m along the path)");
            Assert.Less(Vector3.Distance(tr.Cars[0].position, DriftCityData.ToUnity(line.Path.Sample(tr.Position, out _))), 0.5f, "on the path at the new time");

            // pictures: a train at the first station and one on the line
            Trains.ClockOverride = atStation + 3f;
            yield return new WaitForFixedUpdate(); yield return new WaitForFixedUpdate();
            var c0 = tr.Cars[0].transform; var c3 = tr.Cars[3].transform;
            Shot("extras_train_station", c3.position + c3.right * 28f + Vector3.up * 10f, c3.position, 55f);
            Shot("extras_train_close", c0.position + c0.forward * 18f + c0.right * 8f + Vector3.up * 4f, c0.position, 55f);
            Trains.ClockOverride = 120f;
            yield return new WaitForFixedUpdate(); yield return new WaitForFixedUpdate();
            c3 = trains.All[1].Cars[3].transform;
            Shot("extras_train_line", c3.position + c3.right * 45f + Vector3.up * 25f, c3.position, 55f);
        }

        /// <summary>PvP track traffic counts (TRAFFIC.md section 13): data\pvptrafficnum.dat read the client's way.</summary>
        [Test]
        public void PvpTrafficCounts()
        {
            var root = DriftCityData.ResolveGameRoot(@"D:\Drift_City_Remastered_Alpha_08-2025\Drift City Remastered Alpha");
            Assert.IsNotNull(root, "installed client or imported data found");
            if (DriftCityData.Files == null) DriftCityData.Mount(root);
            PvpTraffic.Table = null;
            // "Track 1 1": jam row "112 109 105 102 ...", normal "56 54 53 51 ...", idle "28 27 26 25 ..."; the first number is eaten
            Assert.AreEqual(109, PvpTraffic.Count(1, 1, 0, 2));
            Assert.AreEqual(102, PvpTraffic.Count(1, 1, 2, 2));
            Assert.AreEqual(51, PvpTraffic.Count(1, 1, 2, 1));
            Assert.AreEqual(25, PvpTraffic.Count(1, 1, 2, 0));
            Assert.AreEqual(300, PvpTraffic.Count(9, 99, 4, 2), "unlisted track: (density + 1) x 100");
            Assert.AreEqual(0, PvpTraffic.Count(0, 1, 4, 2), "city 0: none");
            Assert.AreEqual(1, PvpTraffic.DensityFor(PvpTraffic.MapTrafficNormal));
            Assert.AreEqual(2, PvpTraffic.DensityFor(PvpTraffic.MapTrafficJam));
            Assert.AreEqual(0, PvpTraffic.DensityFor(PvpTraffic.MapTrafficIdle));
            Assert.AreEqual(2, PvpTraffic.DensityFor(0, () => 5), "RANDOM: rand() % 3");
            Debug.Log($"Drift City extras: pvptrafficnum.dat lists {PvpTraffic.Table.Count} tracks");
        }
    }
}
