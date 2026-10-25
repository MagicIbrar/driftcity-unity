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
    /// The last HUV, chase and Undercity gaps (DriftCity_RE/TRAFFIC.md section 15, CHASES.md, UNDERCITY.md): the HUV pose
    /// levels (0x492be0 / 0x492d50), the server's chase units and the 16-HUV convoy, the kept escorts' threshold and
    /// timeline (0x4a1860 / 0x4a1d10), the Undercity's road-map towers (0x716890) and the Rush Time refusals (11146 /
    /// 10967); online with bots: the server's solo chase units, a server-run Rush Time and a Virtual HUV with real players
    /// (this driver made the HUV). Screenshots: Preview/huvev_*.png.
    /// </summary>
    public class HuvEventsTests
    {
        string tempSave;
        Process server, bot, bot2;
        const int Port = 7897;
        static string Url => $"ws://127.0.0.1:{Port}";
        static string ServerDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "DriftCityServer"));

        [SetUp]
        public void SetUp()
        {
            tempSave = Path.Combine(Path.GetTempPath(), "driftcity_huvev_" + System.Guid.NewGuid().ToString("N"));
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
            UndercityParty.ResetAll();
            if (OnlineSession.Instance) OnlineSession.Instance.Logout();
            foreach (var p in new[] { bot, bot2, server }) try { if (p != null && !p.HasExited) p.Kill(); } catch { }
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

        IEnumerator EnterCity(int level)
        {
            var p = new PlayerProfile { Name = "Hunter", Level = level, Mito = 100000, City = "cras" };
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
            Debug.Log("Drift City HUV events: wrote Preview/" + name + ".png");
        }

        /// <summary>The game view with its UI (UiManager.Capture: WaitForEndOfFrame never resumes in -batchmode).</summary>
        static IEnumerator ScreenShot(string name)
        {
            yield return null;
            Canvas.ForceUpdateCanvases();
            var tex = UiManager.Instance.Capture(Camera.main);
            File.WriteAllBytes(Path.Combine("Preview", name + ".png"), tex.EncodeToPNG());
            Object.Destroy(tex);
            Debug.Log("Drift City HUV events: wrote Preview/" + name + ".png");
        }

        static void Park(DriftCar car, Vector3 p, bool hold = true)
        {
            var rb = car.GetComponent<Rigidbody>();
            rb.isKinematic = hold;
            rb.position = p + Vector3.up * (hold ? 40f : 1f);
            car.transform.position = rb.position;
        }

        static void Teleport(DriftCar car, Vector3 p)
        {
            var rb = car.GetComponent<Rigidbody>();
            rb.isKinematic = false;
            if (Physics.Raycast(p + Vector3.up * 40f, Vector3.down, out var hit, 120f, ~0, QueryTriggerInteraction.Ignore)) p = hit.point;
            rb.position = p + Vector3.up * 0.8f;
            rb.linearVelocity = Vector3.zero;
            car.transform.position = rb.position;
        }

        static int BossIndex(int id) => GameTables.BossHuvs.FindIndex(h => h.UniqueId == id);

        [UnityTest]
        public IEnumerator OfflinePosesUnitsKeptEscortsTowersRefusals()
        {
            yield return EnterCity(35);
            var session = CitySession.Current;
            var car = session.Car;
            var traffic = session.Traffic;
            traffic.MaxCars = 0; traffic.TrimTo(0);

            // ---------- BossHuvInfo 컨버팅시간 (column 22): the kept escorts' threshold ----------
            Assert.AreEqual(40f, HuvRoutes.Boss(11001).ConvertPercent);
            Assert.AreEqual(40f, HuvRoutes.Boss(11004).ConvertPercent);
            Assert.AreEqual(100f, HuvRoutes.Boss(11003).ConvertPercent);
            Assert.AreEqual(0.4f, ChaseRules.SplitShare(11001), 1e-4f);
            Assert.IsFalse(ChaseRules.SplitDue(11001, 0.39f));
            Assert.IsTrue(ChaseRules.SplitDue(11001, 0.4f));
            Assert.IsFalse(ChaseRules.SplitDue(11003, 0.99f), "bosses without kept escorts");

            // ---------- the chase units (src/huvrules.js mirrored) ----------
            var plan = ChaseRules.Units(65, 1, false, 4, 130f, () => 0.5f);
            Assert.AreEqual(3, plan.Units.Count, "1 + level / 30");
            Assert.AreEqual(210f, plan.Seconds);
            Assert.IsTrue(plan.Units.All(u => u.Kmh == 118f && u.TopKmh == 148f && u.Hp == 130f));
            Assert.AreEqual(16, ChaseRules.Units(45, 1, false, 0, 100f, () => 0.05f).Units.Count, "a convoy");
            Assert.AreEqual(2, ChaseRules.UnitCount(10, 4), "one more for a party of 3-4");

            // a solo chase by the rule: three HUVs on their slots with the units' speeds
            var chase = ChaseMission.Begin(traffic, car, false);
            Assert.IsTrue(chase, "chase started");
            Assert.AreEqual(2, chase.Targets.Count, "the rule's two HUVs at level 35");
            Assert.AreEqual(chase.Units.Seconds, chase.TimeLimit);
            foreach (var t in chase.Targets) Assert.AreEqual(t.Unit, chase.Targets.ToList().IndexOf(t));
            Assert.IsTrue(chase.Targets.All(t => Mathf.Abs(t.Car.CruiseKmh - chase.Units.Units[t.Unit].Kmh) < 0.01f), "0x4f0410: the unit's cruise");

            // ---------- the HUV pose levels (0x492be0 / 0x492d50) ----------
            var hc = chase.Targets[0].Car;
            Assert.IsTrue(hc.HuvPose);
            Assert.AreEqual(2, hc.PoseModeFor(50f, 0.016f));
            Assert.AreEqual(1, hc.PoseModeFor(150f, 0.016f));
            Assert.AreEqual(0, hc.PoseModeFor(250f, 0.016f));
            Assert.AreEqual(0, hc.PoseModeFor(50f, 0.1f), "only while a frame takes under 0.08 s");
            foreach (var t in chase.Targets) { t.Car.CruiseKmh = 0; t.Car.Speed = 0; }
            yield return new WaitForSeconds(0.5f);
            var hp0 = hc.transform.position;
            foreach (var (dist, mode) in new[] { (40f, 2), (150f, 1), (320f, 0) })
            {
                Park(car, hp0 + Vector3.right * dist);
                yield return new WaitForSeconds(0.3f);
                Assert.AreEqual(mode, hc.PoseMode, $"pose mode at {dist} m");
            }
            Park(car, hp0 + Vector3.right * 40f);
            yield return new WaitForSeconds(0.3f);
            Shot("huvev_pose_close", hp0 + Vector3.up * 6f + Vector3.right * 14f, hp0);
            chase.Quit();
            yield return WaitFor(() => !ChaseMission.Active, 5, "chase quit");

            // ---------- the 16-HUV convoy: all 16 slots of the spawn point in a row ----------
            yield return new WaitForSeconds(0.3f);
            var convoyPlan = new ChaseRules.Plan { Convoy = true, Seconds = ChaseRules.ConvoySeconds };
            for (int k = 0; k < 16; k++) convoyPlan.Units.Add(new ChaseRules.Unit { Huv = k, Kmh = 100f, TopKmh = 130f, BoostSeconds = 1f, TurboPercent = 10f, Hp = ChaseRules.ConvoyHp });
            Park(car, car.transform.position);
            var conv = ChaseMission.Begin(traffic, car, false, -1, 0f, 0, 0f, true, convoyPlan);
            Assert.IsTrue(conv && conv.ConvoyChase, "convoy chase");
            Assert.AreEqual(16, conv.Targets.Count, "16 HUVs");
            Assert.AreEqual(16, conv.HuvsLeft);
            Assert.AreEqual(ChaseRules.ConvoySeconds, conv.TimeLimit);
            Assert.IsTrue(conv.Targets.All(t => t.Car.CruiseKmh == 100f && t.MaxHealth == 30f));
            for (int k = 1; k < 16; k++) Assert.AreEqual(conv.Targets[k - 1].Car, conv.Targets[k].Car.Ahead, "each keeps out of the one ahead");
            var sp = conv.SpawnPoint;
            Assert.Less(Vector3.Distance(conv.Targets[15].Car.transform.position, sp.Slots[15]), 60f, "HUV 15 on slot 15");
            Park(car, sp.Pivot);
            yield return new WaitForSeconds(6f);
            var mid = conv.Targets[7].Car.transform.position;
            Shot("huvev_convoy16", mid + Vector3.up * 55f - conv.Targets[7].Car.transform.forward * 60f, mid);
            int moving = conv.Targets.Count(t => t.Car && t.Car.Speed > 3f);
            Debug.Log($"Drift City HUV events: convoy of 16, {moving} moving after 6 s");
            Assert.GreaterOrEqual(moving, 6, "the convoy drives (the row leaves one after the other)");
            foreach (var t in conv.Targets.ToList()) t.SimulateHit(100f);
            yield return WaitFor(() => !ChaseMission.Active, 5, "convoy chase over");
            foreach (var v in UiManager.Instance.OpenViews.ToList()) if (v && v.Dialog.Id == ChaseScreens.ResultId) v.Click("ID_CHASERESULT_OK");

            // ---------- 11001: the threshold, the trailer stages, Lil' Grimer, the truck finished ----------
            ChaseRules.TimeScale = 0.05f;
            yield return new WaitForSeconds(0.3f);
            var boss = ChaseMission.Begin(traffic, car, true, BossIndex(Convoy.LeaderA), 0f, 0);
            Assert.IsTrue(boss);
            var leader = boss.Target;
            Park(car, leader.Car.transform.position + Vector3.right * 30f);
            leader.SimulateHit(boss.MaxHealth * 0.39f);
            Assert.IsFalse(boss.SplitStarted, "39 % is under 컨버팅시간 40 %");
            leader.SimulateHit(boss.MaxHealth * 0.02f);
            Assert.IsTrue(boss.SplitStarted, "the threshold");
            Assert.IsNull(boss.LilGrimer, "not before the trailer stages (8.367 s)");
            yield return WaitFor(() => boss.LilGrimer != null, 3, "Lil' Grimer");
            var lil = boss.LilGrimer;
            Assert.AreEqual(Mathf.Round(boss.Targets[0].MaxHealth * 300f / 1400f * 10f) / 10f, lil.MaxHealth, 0.01f, "내구력 300 of 1400");
            Assert.AreEqual(53.6f, lil.Car.Speed, 0.2f);
            Assert.IsFalse(lil.Car.ClosePose);
            yield return WaitFor(() => boss.TruckFinished, 3, "the truck finished 10 s later");
            Assert.IsTrue(leader.Stopped, "0x4a1860 state 7: damage = max + 10");
            Assert.IsTrue(lil.Car.ClosePose, "+0x724: Lil' Grimer always in the close pose");
            Assert.IsTrue(ChaseMission.Active, "Lil' Grimer still runs");
            lil.SimulateHit(1000f);
            yield return WaitFor(() => !ChaseMission.Active, 5, "boss chase over");
            foreach (var v in UiManager.Instance.OpenViews.ToList()) if (v && v.Dialog.Id == ChaseScreens.ResultId) v.Click("ID_CHASERESULT_OK");

            // caught before Lil' Grimer came out: it stays in the trailer
            yield return new WaitForSeconds(0.3f);
            ChaseRules.TimeScale = 1f;
            var quick = ChaseMission.Begin(traffic, car, true, BossIndex(Convoy.LeaderA), 0f, 0);
            quick.Target.SimulateHit(quick.MaxHealth * 0.5f);
            Assert.IsTrue(quick.SplitStarted);
            quick.Target.SimulateHit(1000f);
            yield return WaitFor(() => !ChaseMission.Active, 5, "caught at once");
            Assert.IsNull(quick.LilGrimer);
            foreach (var v in UiManager.Instance.OpenViews.ToList()) if (v && v.Dialog.Id == ChaseScreens.ResultId) v.Click("ID_CHASERESULT_OK");

            // ---------- the Undercity's towers from the road map (0x716890) ----------
            var map = UndercityRoads.For("cras");
            Assert.IsNotNull(map, "cras.tcs road map");
            Assert.AreEqual(126, map.Roads.Count, "126 ROADs");
            Assert.AreEqual(72, map.Junctions.Count, "72 junctions");
            Assert.IsTrue(map.Roads.All(r => r.StartNode >= 0 && r.EndNode >= 0), "NDPATH nodes");
            Assert.Greater(map.Junctions.Average(j => j.Roads.Count), 1.2f, "each junction's roads (its CROADs)");
            var picks = UndercityRoads.TowerPicks(map, 3);
            var skipped = UndercityRoads.Skipped(3);
            Debug.Log($"Drift City HUV events: cras towers {picks.Count}, {picks.Select(p => p.Road).Distinct().Count()} roads, {picks.Count(p => p.SideA)} on side A");
            Assert.AreEqual(90, picks.Count, "slots 10-99");
            CollectionAssert.AreEqual(Enumerable.Range(10, 90).ToArray(), picks.Select(p => p.Slot).ToArray());
            Assert.IsFalse(picks.Any(p => skipped.Contains(p.Road)), "roads 0x12 / 0x34 / 0x7d skipped");
            var laneIds = new HashSet<int>(traffic.Network.Lanes.Select(l => l.Id));
            Assert.AreEqual(90, picks.Count(p => laneIds.Contains(p.LaneId)), "every pick's lane is in the network");
            foreach (var c in new[] { "moonpalace", "koinonia", "oros" })
            {
                var m = UndercityRoads.For(c);
                int ci = CityMapInfo.CityIndex(c);
                var pk = UndercityRoads.TowerPicks(m, ci);
                Debug.Log($"Drift City HUV events: {c} towers {pk.Count}");
                Assert.AreEqual(90, pk.Count, c);
                Assert.IsFalse(pk.Any(p => UndercityRoads.Skipped(ci).Contains(p.Road)), c + " skipped roads");
            }
            Assert.AreEqual(0f, UndercityData.WaveTimeCut(55f));
            Assert.AreEqual(10f, UndercityData.WaveTimeCut(80f));
            Assert.AreEqual(20f, UndercityData.WaveTimeCut(400f));

            // ---------- Rush Time: an Undercity invite is refused (11146), the V-HUV too (10967) ----------
            var rush = RushTime.Instance;
            Assert.IsTrue(rush);
            rush.MinDrivers = 1;
            rush.Announce();
            Teleport(car, rush.Zone);
            yield return WaitFor(() => RushTime.Participating, 5, "waiting in the zone");
            UndercityParty.OnPush("party.uc.invite", new Dictionary<string, object> { ["room"] = 7.0, ["from"] = "Inviter", ["fee"] = 2100.0, ["seconds"] = 15.0 });
            Assert.AreEqual("Inviter", UndercityParty.LastAutoRefused, "0x50e1d0: refused at once (11146)");
            Assert.IsFalse(UiManager.Instance.Find(UndercityHud.RequestInvite), "no REQUESTINVITE");
            Assert.IsNull(VirtualChase.Register(4), "10967: no V-HUV while in Rush Time");
            yield return ScreenShot("huvev_rush_refusal");
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
            server = Node("server.js", new Dictionary<string, string> { ["DC_PORT"] = Port.ToString(), ["DC_DB"] = Path.Combine(tempSave, "test.db"), ["DC_TEST_EVENTS"] = "1" });
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
            MissionNet.Ensure();
        }

        [UnityTest]
        public IEnumerator OnlineServerUnitsRushTimeAndVirtualHuvWithBots()
        {
            yield return OnlineCity("huvevt", "HuvEvt");
            var ui = UiManager.Instance;
            var session = CitySession.Current;
            var car = session.Car;
            session.Traffic.MaxCars = 0; session.Traffic.TrimTo(0);

            // ---------- a solo chase: the server's units (chase.begin) ----------
            var chase = ChaseMission.Begin(session.Traffic, car, false);
            Assert.IsTrue(chase);
            yield return WaitFor(() => chase && chase.Mode >= 2, 10, "the server's chase start");
            Assert.IsNotNull(chase.Units);
            Assert.AreEqual(1, chase.Units.Units.Count, "level 1: one HUV");
            float kmh = chase.Units.Units[0].Kmh;
            Assert.IsTrue(kmh >= 80f && kmh <= 91f, "85 + level / 2 +-5 km/h: " + kmh);
            Assert.AreEqual(kmh, chase.Target.Car.CruiseKmh, 0.01f);
            Assert.AreEqual(150f, chase.TimeLimit);
            chase.Quit();
            yield return WaitFor(() => !ChaseMission.Active, 5, "chase quit");

            // ---------- Rush Time run by the server, with a real driver (the bot) ----------
            bot = Node($"tools/huvbot.js --url {Url} --name Rb --city {session.City} --rushgm 8");
            var rush = RushTime.Instance;
            Assert.IsTrue(rush);
            yield return WaitFor(() => rush.State == RushTime.Phase.Waiting && rush.Online, 20, "rush.notice");
            Teleport(car, rush.Zone);
            yield return WaitFor(() => ui.Find(RushTime.WaitId), 5, "RUSHWAIT in the zone");
            yield return WaitFor(() => rush.Drivers >= 0 && rush.State == RushTime.Phase.Running, 20, "rush.start");
            Assert.IsTrue(RushTime.Joined);
            Assert.IsTrue(ui.Find(RushTime.InfoId), "RUSHINFO");
            Assert.IsTrue(rush.Boss != null && rush.Boss.Car, "the boss");
            Assert.IsFalse(rush.Boss.Car.Remote, "the first driver in the zone runs the boss");
            float max = rush.MaxHealth;
            Assert.IsTrue(max >= 400f + 80f * 4 && Mathf.Abs((max - 400f) % 80f) < 0.01f, "400 + 80 per driver (two drivers, two or more stand-ins): " + max);
            yield return WaitFor(() => rush.Health < max, 10, "the bot's hits (server health)");
            Park(car, rush.Boss.Car.transform.position + Vector3.right * 25f, false);
            yield return new WaitForSeconds(0.5f);
            yield return ScreenShot("huvev_rush_online");
            while (rush.State == RushTime.Phase.Running)
            {
                rush.Boss?.SimulateHit(45f);
                yield return new WaitForSecondsRealtime(0.3f);
            }
            yield return WaitFor(() => ui.Find(RushTime.ResultId), 10, "RUSHRESULT");
            Assert.IsFalse(string.IsNullOrEmpty(rush.LuckyDriver), "the lucky driver");
            Debug.Log("Drift City HUV events: Rush Time lucky driver " + rush.LuckyDriver);
            yield return ScreenShot("huvev_rush_result");
            try { bot.Kill(); } catch { }
            yield return WaitFor(() => rush.State == RushTime.Phase.Idle, 15, "Rush Time over");

            // ---------- the Virtual HUV with a real driver; this driver made the HUV ----------
            bot2 = Node($"tools/huvbot.js --url {Url} --name Vb --city {session.City} --vhuv 4");
            yield return new WaitForSecondsRealtime(2.5f);
            var vc = VirtualChase.Register(4);
            Assert.IsTrue(vc && vc.Online);
            yield return WaitFor(() => vc && vc.State == VirtualChase.Phase.Running, 25, "vhuv.start");
            Assert.IsTrue(ui.Find(VirtualChase.HudId) && ui.Find(VirtualChase.PlayInfoId));
            Debug.Log($"Drift City HUV events: V-HUV team {vc.Team}, HUVs {vc.WhiteHuv} / {vc.RedHuv}");
            Assert.AreNotEqual(vc.Team, "", "a team");
            OnlineSession.Instance.Net.Request("vhuv.gm", new Dictionary<string, object> { ["huv"] = "HuvEvt", ["seconds"] = 12.0 }, null);
            yield return WaitFor(() => vc.IAmHuv, 5, "made the HUV (10971)");
            Assert.IsTrue(ui.Find(VirtualChase.HudId).Control("ID_VIRTUALCHASING_MY_HUV0").gameObject.activeSelf, "MY_HUV");
            int otherBefore = vc.Team == "white" ? vc.RedCrashes : vc.WhiteCrashes;
            yield return WaitFor(() => (vc.Team == "white" ? vc.RedCrashes : vc.WhiteCrashes) > otherBefore, 8, "the other team crashed me");
            yield return ScreenShot("huvev_vhuv_myhuv");
            yield return WaitFor(() => ui.Find(VirtualChase.ResultId), 20, "VIRTUAL 3810000");
            yield return ScreenShot("huvev_vhuv_result");
            ui.Find(VirtualChase.ResultId).Click("ID_VIRTUAL_CHASING_RESULT_OK");
            try { bot2.Kill(); } catch { }

            // ---------- a party boss chase of 11001: the server releases Lil' Grimer, then finishes the truck ----------
            bot = Node($"tools/bot.js 1 --url {Url} --name Pbot --party 400 --city {session.City}");
            yield return WaitFor(() => OnlineSession.Instance.Remotes && OnlineSession.Instance.Remotes.All.Any(r => r && r.DriverName == "Pbot1"), 20, "bot in the channel");
            PartyWindow.Invite("Pbot1");
            yield return WaitFor(() => ui.Find(PartyWindow.CreateDlg), 3, "party create dialog");
            ui.Find(PartyWindow.CreateDlg).Click("ID_PARTY_CREATE_BUTTON");
            yield return WaitFor(() => PartyWindow.Members.Count == 2, 10, "bot joined the party");
            int bi = BossIndex(Convoy.LeaderA);
            OnlineSession.Instance.Net.Request("chase.party.start", new Dictionary<string, object> { ["boss"] = true, ["huv"] = bi, ["bossId"] = Convoy.LeaderA }, null);
            yield return WaitFor(() => ChaseMission.Active && ChaseMission.Active.Party, 10, "chase.party.start");
            var pc = ChaseMission.Active;
            pc.BriefingSeconds = 0f;
            Assert.IsTrue(pc.IsOwner);
            Assert.AreEqual(Convoy.LeaderA, pc.Target.Info.UniqueId);
            float bossMax = pc.MaxHealth;
            // 40 % of its health: the server's threshold, then the trailer stages
            while (!pc.SplitStarted && ChaseMission.Active) { pc.Target.SimulateHit(45f); yield return new WaitForSecondsRealtime(0.3f); }
            Assert.IsTrue(pc.SplitStarted, "chase.party.split start");
            Assert.LessOrEqual(pc.Targets[0].Health, bossMax * 0.6f + 0.01f);
            yield return WaitFor(() => pc.LilGrimer != null, 15, "chase.party.split out: Lil' Grimer");
            Assert.AreEqual(1, pc.LilGrimer.Unit, "Lil' Grimer is unit 1");
            Assert.AreEqual(64.3f, pc.LilGrimer.MaxHealth, 0.05f, "300 / 1400 of the boss's 300");
            var lp = pc.LilGrimer.Car.transform.position;
            Shot("huvev_party_lilgrimer", lp + Vector3.up * 30f - pc.LilGrimer.Car.transform.forward * 35f, lp);
            yield return WaitFor(() => pc.TruckFinished, 15, "chase.party.split finish: the truck finished");
            yield return WaitFor(() => pc.Targets[0].Stopped, 5, "the truck stopped");
            Assert.IsTrue(ChaseMission.Active, "Lil' Grimer still runs");
            while (ChaseMission.Active) { pc.LilGrimer?.SimulateHit(45f); yield return new WaitForSecondsRealtime(0.3f); }
            yield return WaitFor(() => ui.Find(ChaseScreens.PartyResultId), 10, "PARTYCHASERESULT");
            Assert.AreEqual(2, ChaseMission.LastRows.Count);
        }
    }
}
