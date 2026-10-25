using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DriftCity.Core;
using DriftCity.Game;
using DriftCity.Net;
using DriftCity.Social;
using DriftCity.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;

namespace DriftCity.Tests
{
    /// <summary>
    /// The HUV AI and the traffic still open in DriftCity_RE/TRAFFIC.md sections 13-15, in Cras:
    /// Huv00.xipath2 and BossHuvInfo, the chase start's spawn point / goal / waypoints (0x44a5d0), the chase HUVs on their
    /// road routes (a pack of three), a boss on its xipath2 route with its convoy (start speed 40, the boss's lanes, let go
    /// when the boss is caught), 11004's kept escorts and Lil' Grimer at half health, the remote (dead-reckoned) copy of a
    /// car, PvP path traffic on PVP_map3_01.chpath from a seed. Online with a bot: the shared party HUV both ways (this
    /// client as its owner, and as the member mirroring the bot's HUV). Screenshots: Preview/huv_*.png.
    /// </summary>
    public class HuvAiTests
    {
        string tempSave;
        Process server, bot;
        const int Port = 7895;
        static string Url => $"ws://127.0.0.1:{Port}";
        static string ServerDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "DriftCityServer"));

        [SetUp]
        public void SetUp()
        {
            tempSave = Path.Combine(Path.GetTempPath(), "driftcity_huvai_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempSave);
            PlayerProfile.SaveDirOverride = tempSave;
            PlayerProfile.Slot = 0;
            PlayerProfile.Current = null;
            Directory.CreateDirectory("Preview");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            ChaseMission.PackSize = 0;
            ChaseRules.TimeScale = 1f;
            PartyWindow.ResetAll();
            if (OnlineSession.Instance) OnlineSession.Instance.Logout();
            foreach (var p in new[] { bot, server }) try { if (p != null && !p.HasExited) p.Kill(); } catch { }
            PlayerProfile.SaveDirOverride = null;
            foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Exclude).Where(g => g.transform.parent == null)) Object.Destroy(go);
            yield return new WaitForSecondsRealtime(0.5f);
            try { Directory.Delete(tempSave, true); } catch { }
        }

        static IEnumerator WaitFor(System.Func<bool> condition, float timeout, string what)
        {
            float t = 0;
            while (!condition() && t < timeout) { t += Time.unscaledDeltaTime; yield return null; }
            Assert.IsTrue(condition(), "timed out waiting for " + what);
        }

        IEnumerator EnterCity()
        {
            var p = new PlayerProfile { Name = "Hunter", Level = 40, Mito = 100000, City = "cras" };
            p.Cars.Add(new OwnedCar { VehicleIndex = 0, Color = 2 });
            p.Save();
            new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            var flow = new GameObject("Game").AddComponent<GameFlow>();
            flow.SkipParkingLot = true;
            yield return FrontEndSteps.ToCharacterSelect(flow);
            yield return FrontEndSteps.EnterSelected();
            yield return WaitFor(() => flow.InCity && CitySession.Current && flow.PlayerCar, 600, "city");
            var session = CitySession.Current;
            session.MissionOfferInterval = 1e6f;
            if (InstantMissions.Instance) InstantMissions.Instance.OfferInterval = InstantMissions.Instance.FirstOffer = 1e6f;
            yield return new WaitForSeconds(1f);
        }

        /// <summary>Renders the scene from a free camera to Preview/name.png.</summary>
        static void Shot(string name, Vector3 eye, Vector3 at, float fov = 55f)
        {
            var go = new GameObject("Shot Camera");
            var cam = go.AddComponent<Camera>();
            if (Camera.main) cam.CopyFrom(Camera.main);
            cam.fieldOfView = fov;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 2000f;
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
            Debug.Log("Drift City HUV AI: wrote Preview/" + name + ".png");
        }

        static void Park(DriftCar car, Vector3 p)
        {
            var rb = car.GetComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.position = p + Vector3.up * 40f;
            car.transform.position = rb.position;
        }

        static int BossIndex(int id) => GameTables.BossHuvs.FindIndex(h => h.UniqueId == id);

        [UnityTest]
        public IEnumerator OfflineRoutesConvoysAndPathTraffic()
        {
            yield return EnterCity();
            var session = CitySession.Current;
            var car = session.Car;
            var traffic = session.Traffic;

            // ---------- the data ----------
            var xi = HuvRoutes.XiPaths("cras");
            CollectionAssert.AreEqual(new[] { 17, 14, 15, 9, 12 }, xi.Select(r => r.Count).ToArray(), "Huv00.xipath2: five routes");
            Assert.AreEqual(4, new[] { "moonpalace", "koinonia", "cras", "oros" }.Count(c => HuvRoutes.XiPaths(c).Count > 0), "every city but Taipei has HUV routes");
            var b1 = HuvRoutes.Boss(11001);
            Assert.IsNotNull(b1, "BossHuvInfo 11001");
            Assert.AreEqual(150f, b1.SpeedKmh); Assert.AreEqual(180f, b1.MaxKmh); Assert.AreEqual(1400, b1.Durability);
            Assert.AreEqual(20, HuvRoutes.Boss(10001).Durability, "Escort Car 0");
            Assert.AreEqual(1, HuvRoutes.Boss(10001).AvoidFrontPlayer);
            Assert.AreEqual(6, HuvRoutes.Boss(11002).TurboPercent, "Lil' Grimer's turbo chance");
            Assert.AreEqual(180f, HuvRoutes.Boss(11002).SpeedKmh);
            Assert.AreEqual(53.6f, Convoy.StartSpeedFor(Convoy.LilGrimer), 0.05f);
            Assert.AreEqual(40f, Convoy.StartSpeedFor(10003));

            // ---------- the chase start (0x44a5d0 / 0x447ed0) ----------
            var from = car.transform.position;
            var plan = HuvRoutes.PlanChase(ChaseSpawns.For("cras"), from, new System.Random(7));
            Assert.IsNotNull(plan);
            var sorted = ChaseSpawns.For("cras").OrderBy(p => (p.Pivot - from).sqrMagnitude).ToList();
            Assert.AreEqual(sorted.First(p => (p.Pivot - from).magnitude > 1000f), plan.Start, "the nearest point beyond 1 km");
            float gd = (plan.Goal.Pivot - from).magnitude;
            Assert.IsTrue(gd > 1000f && gd <= 5000f, "goal 1-5 km away: " + gd);
            Assert.AreEqual(16, plan.Waypoints.Length);
            Assert.IsTrue(plan.Waypoints.All(w => w != plan.Goal && w != plan.Start), "waypoints from the rest of the pool");

            // ---------- a chase pack on its routes ----------
            traffic.MaxCars = 0; traffic.TrimTo(0);
            ChaseMission.PackSize = 3;
            var chase = ChaseMission.Begin(traffic, car, false);
            Assert.IsTrue(chase, "chase started");
            Assert.AreEqual(3, chase.Targets.Count, "three HUVs");
            Assert.AreEqual(3, chase.HuvsLeft);
            Assert.AreEqual(chase.Plan.Start, chase.SpawnPoint);
            foreach (var t in chase.Targets) Assert.Greater(t.Car.Route.Count, 3, "each HUV has its road route: " + t.Car.name);
            var startPos = chase.Targets.Select(t => t.Car.transform.position).ToList();
            Assert.Less(Vector3.Distance(startPos[0], chase.SpawnPoint.Slots[0]), 60f, "HUV 0 on slot 0");
            Park(car, chase.SpawnPoint.Pivot);
            yield return new WaitForSeconds(1.5f);
            Shot("huv_chase_pack_start", chase.SpawnPoint.Pivot + Vector3.up * 45f + Vector3.forward * 40f, chase.Targets[0].Car.transform.position);
            yield return new WaitForSeconds(6f);
            for (int i = 0; i < 3; i++)
            {
                var t = chase.Targets[i];
                float moved = Vector3.Distance(startPos[i], t.Car.transform.position);
                Debug.Log($"Drift City HUV AI: HUV {i} moved {moved:F0} m, {t.Car.Speed * 3.6f:F0} km/h, {t.Car.Route.Count} lanes left");
                Assert.Greater(moved, 40f, "HUV drives its route");
            }
            // one of the pack stopped: the others run on, the count drops
            chase.Targets[1].SimulateHit(1000f);
            Assert.AreEqual(2, chase.HuvsLeft);
            Assert.IsTrue(ChaseMission.Active, "the chase goes on");
            chase.Targets[0].SimulateHit(1000f);
            chase.Targets[2].SimulateHit(1000f);
            yield return WaitFor(() => !ChaseMission.Active, 5, "chase over");
            ChaseMission.PackSize = 1;
            foreach (var v in UiManager.Instance.OpenViews.ToList()) if (v && v.Dialog.Id == ChaseScreens.ResultId) v.Click("ID_CHASERESULT_OK");

            // ---------- a boss on its xipath2 route with its convoy ----------
            yield return new WaitForSeconds(0.5f);
            int bi = BossIndex(Convoy.LeaderA);
            Assert.GreaterOrEqual(bi, 0);
            var boss = ChaseMission.Begin(traffic, car, true, bi, 0f, 0);
            Assert.IsTrue(boss, "boss chase");
            var leader = boss.Target;
            Assert.AreEqual(Convoy.LeaderA, leader.Info.UniqueId);
            Assert.AreEqual(150f, leader.Car.CruiseKmh, "BossHuvInfo speed");
            Assert.IsTrue(leader.Convoy, "11001 leads a convoy");
            Assert.AreEqual(4, leader.Convoy.Escorts.Count, "10001-10004");
            foreach (var e in leader.Convoy.Escorts)
            {
                Assert.AreEqual(40f, e.Speed, 0.01f, "0x4912d0: driving at 40 at once");
                Assert.AreEqual(150f, e.CruiseKmh, "the escort's own BossHuvInfo speed");
                Assert.AreEqual(leader.Car, e.FollowTarget);
            }
            float onRoute = xi.SelectMany(r => r).Min(p => MissionPosts.Flat(p, leader.Car.transform.position));
            Assert.Less(onRoute, 120f, "the boss starts on an xipath2 point");
            Assert.Greater(leader.Car.Route.Count, 3, "the boss drives the lanes of its xipath2 route");
            var bossStart = leader.Car.transform.position;
            Park(car, leader.Car.transform.position);
            for (int s = 0; s < 5; s++)
            {
                yield return new WaitForSeconds(2f);
                var lc = leader.Car;
                Debug.Log($"Drift City HUV AI: t={2 * s + 2}s boss lane {lc.Lane} along {lc.Along:F0} {lc.Speed * 3.6f:F0} km/h route {lc.Route.Count}; escorts " +
                          string.Join(" | ", leader.Convoy.Escorts.Select(e => $"lane {e.Lane} {e.Speed * 3.6f:F0} km/h {Vector3.Dot(e.transform.position - lc.transform.position, lc.transform.forward):F0} m ahead")));
            }
            var trail = new HashSet<int>();
            foreach (var e in leader.Convoy.Escorts)
            {
                float gap = Vector3.Distance(e.transform.position, leader.Car.transform.position);
                Debug.Log($"Drift City HUV AI: escort {e.name} {gap:F0} m from the boss, lane {e.Lane}, {e.Speed * 3.6f:F0} km/h");
                Assert.Less(gap, 250f, "escorts stay with the boss");
                Assert.Greater(gap, 4f, "escorts drive behind the boss, not inside it");
            }
            Assert.Greater(leader.Car.Speed, 3f, "the boss drives");
            Assert.Greater(Vector3.Distance(bossStart, leader.Car.transform.position), 40f, "the boss went along its route (from a standing start, slowing for its escorts at turns)");
            var mid = leader.Convoy.Escorts[1].transform.position;
            Shot("huv_convoy", mid + Vector3.up * 40f - leader.Car.transform.forward * 70f, mid);
            // the damage threshold (BossHuvInfo 컨버팅시간 40 %): the trailer stages, then Lil' Grimer leaves (0x4a1860 -> 0x4a16c0)
            ChaseRules.TimeScale = 0.05f;
            leader.SimulateHit(boss.MaxHealth * 0.55f);
            Assert.IsTrue(boss.SplitStarted);
            yield return WaitFor(() => boss.Targets.Count == 2, 3, "Lil' Grimer out of the trailer");
            Assert.AreEqual(Convoy.LilGrimer, boss.Targets[1].Info.UniqueId);
            Assert.AreEqual(53.6f, boss.Targets[1].Car.Speed, 0.1f);
            var convoy = leader.Convoy;
            // 10 s on the truck is finished (0x4a1860 state 7) and its escorts are let go (0x444f30)
            yield return WaitFor(() => boss.TruckFinished, 3, "the truck finished");
            Assert.IsTrue(leader.Stopped);
            Assert.IsTrue(convoy.Released, "0x444f30: the escorts are let go when the boss is caught");
            Assert.IsTrue(convoy.Escorts.All(e => !e || e.FollowTarget == null));
            boss.Targets[1].SimulateHit(1000f);
            yield return WaitFor(() => !ChaseMission.Active, 5, "boss chase over");
            foreach (var v in UiManager.Instance.OpenViews.ToList()) if (v && v.Dialog.Id == ChaseScreens.ResultId) v.Click("ID_CHASERESULT_OK");

            // 11004: the kept escorts come out at half health
            int bd = BossIndex(Convoy.LeaderB);
            if (bd >= 0)
            {
                yield return new WaitForSeconds(0.3f);
                var dozer = ChaseMission.Begin(traffic, car, true, bd, 0f, 0);
                Assert.IsTrue(dozer);
                var dc = dozer.Target.Convoy;
                Assert.IsTrue(dc, "11004 keeps two escorts");
                Assert.AreEqual(0, dc.Escorts.Count, "none at the start");
                dozer.Target.SimulateHit(dozer.MaxHealth * 0.6f);
                Assert.IsTrue(dc.SplitStarted, "the threshold (40 %)");
                yield return WaitFor(() => dc.Split, 3, "the baby dozers out (2 s + 3 s of stages)");
                Assert.AreEqual(2, dc.Escorts.Count, "11005 and 11006 (0x4a1d10)");
                dozer.Quit();
                yield return null;
            }

            // ---------- a remote copy (dead reckoning) ----------
            Assert.IsTrue(traffic.NearestLane(car.transform.position, out int lane, out float along, -1, 600f));
            var copy = traffic.SpawnModel(@"vehicle\huv\" + GameTables.Huvs[0].FileName, lane, along, 60f);
            Assert.IsTrue(copy);
            copy.SetRemote(true);
            var p0 = copy.transform.position + Vector3.right * 15f;
            copy.ApplyRemote(p0, 90f, 20f);
            Assert.IsTrue(copy.HasRemoteState);
            Assert.Less(Vector3.Distance(copy.transform.position, p0), 3f, "snapped to the first state");
            copy.Place(lane, along);
            Assert.Less(Vector3.Distance(copy.transform.position, p0), 3f, "local code does not move a remote copy");
            yield return new WaitForSeconds(1f);
            float ahead = copy.transform.position.x - p0.x;
            Debug.Log($"Drift City HUV AI: the copy carried on {ahead:F1} m at 20 m/s");
            Assert.IsTrue(ahead > 12f && ahead < 30f, "dead-reckoned along its heading: " + ahead);
            copy.SetRemote(false);
            Assert.IsFalse(copy.Remote);
            Object.Destroy(copy.gameObject);

            // ---------- PvP path traffic from a seed ----------
            Assert.AreEqual(@"cras\path\pvp_map3_01.chpath", PvpPathTraffic.PathFile("cras", 1));
            int count = PvpTraffic.Count(3, 1, 2, 1);
            Assert.Greater(count, 0);
            float clock = 30f;
            var pt = PvpPathTraffic.Start(traffic, "cras", 1, count, 1234, () => clock);
            Assert.IsTrue(pt, "PVP_map3_01.chpath");
            Assert.AreEqual(24, pt.Paths.Count, "24 paths");
            Assert.AreEqual(count, pt.Slots.Count);
            // the same seed gives every client the same cars
            var schedule = pt.Slots.Select(s => (s.Path, s.Offset, s.Speed, s.Model)).ToList();
            var bodiesBefore = pt.Slots.Count(s => s.Car);
            pt.Position(5, 42f, out var at42, out _);
            Object.Destroy(pt.gameObject);
            yield return null;
            var pt2 = PvpPathTraffic.Start(traffic, "cras", 1, count, 1234, () => clock);
            CollectionAssert.AreEqual(schedule, pt2.Slots.Select(s => (s.Path, s.Offset, s.Speed, s.Model)).ToList(), "same schedule from the same seed");
            pt2.Position(5, 42f, out var again, out _);
            Assert.Less(Vector3.Distance(at42, again), 0.01f);
            Assert.AreNotEqual(schedule[0], PvpPathTraffic.Start(traffic, "cras", 1, count, 99, () => clock).Slots.Select(s => (s.Path, s.Offset, s.Speed, s.Model)).First(), "another seed, other cars");
            pt2 = PvpPathTraffic.Start(traffic, "cras", 1, count, 1234, () => clock);
            // drive to a path car: bodies appear near the car and move with the clock
            pt2.Position(0, clock, out var c0, out var f0);
            Park(car, c0 + Vector3.Cross(Vector3.up, f0) * 25f);
            yield return new WaitForSeconds(0.5f);
            Assert.Greater(pt2.Slots.Count(s => s.Car && s.Car.gameObject.activeSelf), 0, "path cars near the player");
            var body = pt2.Slots[0].Car;
            Assert.IsTrue(body && body.gameObject.activeSelf, "slot 0 shown");
            var b0 = body.transform.position;
            Shot("huv_pvp_path_traffic", c0 + Vector3.up * 30f - f0 * 50f, c0);
            for (int i = 0; i < 30; i++) { clock += 0.1f; yield return new WaitForSeconds(0.1f); }
            float drove = Vector3.Distance(b0, body.transform.position);
            Debug.Log($"Drift City HUV AI: path car drove {drove:F1} m in 3 s of race clock ({pt2.Slots[0].Speed * PvpPathTraffic.KeyMetres * 3.6f:F0} km/h)");
            Assert.Greater(drove, 20f, "path car follows the race clock");
            Object.Destroy(pt2.gameObject);
        }

        static Process Node(string args, Dictionary<string, string> env = null)
        {
            var psi = new ProcessStartInfo("node", args)
            {
                WorkingDirectory = ServerDir, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            if (env != null) foreach (var kv in env) psi.EnvironmentVariables[kv.Key] = kv.Value;
            var p = Process.Start(psi);
            p.OutputDataReceived += (_, e) => { if (e.Data != null) Debug.Log("[node] " + e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) Debug.Log("[node err] " + e.Data); };
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            return p;
        }

        IEnumerator OnlineCity(string account, string name)
        {
            if (!File.Exists(Path.Combine(ServerDir, "server.js"))) Assert.Ignore("DriftCityServer not found next to the Unity project");
            server = Node("server.js", new Dictionary<string, string> { ["DC_PORT"] = Port.ToString(), ["DC_DB"] = Path.Combine(tempSave, "test.db") });
            float t = 0; bool up = false;
            while (!up && t < 15f)
            {
                try { using (var c = new System.Net.Sockets.TcpClient()) { c.Connect("127.0.0.1", Port); up = true; } } catch { }
                if (!up) { yield return new WaitForSecondsRealtime(0.25f); t += 0.25f; }
            }
            Assert.IsTrue(up, "server started");
            new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            SocialHost.Ensure();
            var flow = new GameObject("Game").AddComponent<GameFlow>();
            flow.SkipParkingLot = true; flow.SkipIntro = true;
            yield return FrontEndSteps.ToCharacterSelect(flow, Url, account, "secret123");
            yield return WaitFor(() => UiManager.Instance.Find(3250000), 15, "create-account popup");
            UiManager.Instance.Find(3250000).Click(UiPopup.Yes);
            yield return FrontEndSteps.NewCharacter(name, 1);
            UiManager.Instance.Find(150000).Click("ID_CREATECAR_NEW_NEXT");
            yield return WaitFor(() => UiManager.Instance.Find(320000), 600, "city HUD");
            yield return WaitFor(() => OnlineSession.Instance.Channel != null, 15, "channel join");
            var session = CitySession.Current;
            session.MissionOfferInterval = 1e6f;
            if (InstantMissions.Instance) InstantMissions.Instance.OfferInterval = InstantMissions.Instance.FirstOffer = 1e6f;
        }

        [UnityTest]
        public IEnumerator OnlineSharedPartyHuvWithBots()
        {
            yield return OnlineCity("huvtester", "HuvTester");
            var ui = UiManager.Instance;
            var session = CitySession.Current;
            var car = session.Car;

            // ---------- this client owns the HUV: the bot member gets its states (and hits it once to show it) ----------
            bot = Node($"tools/bot.js 1 --url {Url} --name Hbot --party 400 --city {session.City}");
            yield return WaitFor(() => OnlineSession.Instance.Remotes && OnlineSession.Instance.Remotes.Count >= 1, 20, "bot in the channel");
            PartyWindow.Invite("Hbot1");
            yield return WaitFor(() => ui.Find(PartyWindow.CreateDlg), 3, "party create dialog");
            ui.Find(PartyWindow.CreateDlg).Click("ID_PARTY_CREATE_BUTTON");
            yield return WaitFor(() => PartyWindow.Members.Count == 2, 10, "bot joined the party");
            ChaseMission.StartFromCall(session, false);
            yield return WaitFor(() => ChaseMission.Active && ChaseMission.Active.Party, 10, "chase.party.start");
            var chase = ChaseMission.Active;
            chase.BriefingSeconds = 0f;
            Assert.IsTrue(chase.IsOwner, "the leader's client runs the HUV (0x4f0940 \"myhuv\")");
            Assert.IsFalse(chase.Target.Car.Remote);
            float max = chase.MaxHealth;
            // the bot hits once on its first chase.party.huv: the shared health drops by 1
            yield return WaitFor(() => ChaseMission.Active && chase.Health < max - 0.5f, 10, "the bot saw the owner's HUV states");
            Debug.Log($"Drift City HUV AI: the member got the owner's HUV ({chase.Health}/{max})");
            chase.Quit();
            yield return WaitFor(() => !ChaseMission.Active, 5, "chase quit");
            PartyWindow.Leave();
            yield return WaitFor(() => !PartyWindow.InParty, 10, "left the party");
            try { bot.Kill(); } catch { }
            yield return new WaitForSecondsRealtime(1f);

            // ---------- the bot owns the HUV: this client shows a dead-reckoned copy of it ----------
            bot = Node($"tools/bot.js 1 --url {Url} --name Lbot --partylead HuvTester --city {session.City}");
            yield return WaitFor(() => ui.Find(PartyWindow.InviteDlg), 25, "the bot's party invitation (420000)");
            ui.Find(PartyWindow.InviteDlg).Click("ID_PARTY_INVITE_RECV_ACCEPT");
            yield return WaitFor(() => PartyWindow.Members.Count == 2, 10, "joined the bot's party");
            yield return WaitFor(() => ChaseMission.Active && ChaseMission.Active.Party, 15, "the bot started the party chase");
            chase = ChaseMission.Active;
            chase.BriefingSeconds = 0f;
            Assert.IsFalse(chase.IsOwner, "the owner is the bot (\"Ohuv\")");
            var huv = chase.Target.Car;
            Assert.IsTrue(huv.Remote, "a remote copy");
            yield return WaitFor(() => huv && huv.HasRemoteState, 10, "the owner's HUV states");
            yield return new WaitForSeconds(1.5f);
            float d = MissionPosts.Flat(huv.transform.position, car.transform.position);
            Debug.Log($"Drift City HUV AI: the bot's HUV is {d:F1} m from this car (it circles at 30 m), {huv.Speed:F1} m/s");
            Assert.IsTrue(d > 15f && d < 50f, "the copy is where the owner says (30 m circle): " + d);
            var r0 = huv.GetComponentInChildren<Renderer>();
            Assert.IsTrue(r0 && r0.enabled, "shown once a state arrived");
            var before = huv.transform.position;
            yield return new WaitForSeconds(1f);
            Assert.Greater(Vector3.Distance(before, huv.transform.position), 5f, "it moves with the owner's states");
            var hp = huv.transform.position;
            Shot("huv_party_remote_copy", hp + Vector3.up * 25f + (car.transform.position - hp).normalized * -20f, hp);
            // hits still go to the server's shared health; the server ends it for both
            while (ChaseMission.Active)
            {
                chase.Target?.SimulateHit(45f);
                yield return new WaitForSecondsRealtime(0.3f);
            }
            yield return WaitFor(() => ui.Find(ChaseScreens.PartyResultId), 10, "PARTYCHASERESULT");
            Assert.AreEqual(2, ChaseMission.LastRows.Count);
        }
    }
}
