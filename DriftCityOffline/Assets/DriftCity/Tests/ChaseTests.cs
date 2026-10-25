using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    /// The chase round (DriftCity_RE/CHASES.md, MISSIONS.md sections 6-8), offline in Cras: the HUV call (CHASING), the
    /// chase with CHASEINFO / CHASEPLAYINFO / CHASERESULT and the rising reward lines, giving up (10727), PARTYCHASERESULT,
    /// Rush Time (RUSHWAIT / RUSHINFO / RUSHRESULT), the Virtual HUV (3830000 / 900000 / 910000 / 3810000), Auto-Drive
    /// (AUTODRIVING), a Mita 500 drink, the party digits of SPEEDCOMBO, EX / event deliveries with ARBEITPLAYINFONEW and the
    /// no-medal tier, QUESTPLAYINFO and the basic HUD's running list (230000). Online against the local server with a
    /// party bot: a party chase paid by the server (PARTYCHASERESULT with the party bonus) and a Mita 500 bottle.
    /// Writes Preview/mc_*.png.
    /// </summary>
    public class ChaseTests
    {
        string tempSave;
        Process server, bot;
        const int Port = 7883;
        static string Url => $"ws://127.0.0.1:{Port}";
        static string ServerDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "DriftCityServer"));

        [SetUp]
        public void SetUp()
        {
            tempSave = Path.Combine(Path.GetTempPath(), "driftcity_chases_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempSave);
            PlayerProfile.SaveDirOverride = tempSave;
            PlayerProfile.Slot = 0;
            PlayerProfile.Current = null;
            MissionRecords.Reset();
            SeasonEvents.Override = EventSeason.None;
            SocialStore.Reset();
            Directory.CreateDirectory("Preview");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            SeasonEvents.Override = null;
            ChaseMission.PackSize = 0;
            DeliveryService.NewLayout = false;
            MissionTracker.Classic = null;
            PartyWindow.ResetAll();
            if (OnlineSession.Instance) OnlineSession.Instance.Logout();
            foreach (var p in new[] { bot, server }) try { if (p != null && !p.HasExited) p.Kill(); } catch { }
            PlayerProfile.SaveDirOverride = null;
            MissionRecords.Reset();
            SocialStore.Reset();
            foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Exclude).Where(g => g.transform.parent == null)) Object.Destroy(go);
            yield return new WaitForSecondsRealtime(0.5f);
            try { Directory.Delete(tempSave, true); } catch { }
        }

        static void Shot(string name)
        {
            Canvas.ForceUpdateCanvases();
            var tex = UiManager.Instance.Capture(Camera.main);
            File.WriteAllBytes(Path.Combine("Preview", name), tex.EncodeToPNG());
            Object.Destroy(tex);
            Debug.Log("Drift City chases: wrote Preview/" + name);
        }

        static IEnumerator WaitFor(System.Func<bool> condition, float timeout, string what)
        {
            float t = 0;
            while (!condition() && t < timeout) { t += Time.unscaledDeltaTime; yield return null; }
            Assert.IsTrue(condition(), "timed out waiting for " + what);
        }

        static void Teleport(DriftCar car, Vector3 p, Vector3 lookAt)
        {
            var dir = lookAt - p; dir.y = 0;
            var pos = Physics.Raycast(p + Vector3.up * 200f, Vector3.down, out var hit, 400f) ? hit.point + Vector3.up * 0.6f : p + Vector3.up * 0.6f;
            var rb = car.GetComponent<Rigidbody>();
            rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero;
            var rot = Quaternion.LookRotation(dir.sqrMagnitude > 0.01f ? dir.normalized : car.transform.forward, Vector3.up);
            car.transform.SetPositionAndRotation(pos, rot);
            rb.position = pos; rb.rotation = rot;
        }

        static string Text(NuiView v, string id) => v ? v.Control(id)?.GetComponentInChildren<UnityEngine.UI.Text>(true)?.text : null;
        static bool Shown(NuiView v, string id) { var c = v ? v.Control(id) : null; return c && c.gameObject.activeInHierarchy; }

        static void Popup(bool yes)
        {
            foreach (var v in UiManager.Instance.OpenViews.Where(v => v && v.Dialog.Id == 3250000).ToList())
            {
                if (yes && v.Control(UiPopup.Yes) && v.Control(UiPopup.Yes).gameObject.activeSelf) v.Click(UiPopup.Yes);
                else if (v.Control(UiPopup.No) && v.Control(UiPopup.No).gameObject.activeSelf) v.Click(UiPopup.No);
                else v.Click("ID_POPUP_OKONLY");
            }
        }

        IEnumerator City(GameFlow flow)
        {
            yield return FrontEndSteps.ToCharacterSelect(flow);
            yield return FrontEndSteps.EnterSelected();
            yield return WaitFor(() => flow.InCity && CitySession.Current && flow.PlayerCar, 600, "city");
        }

        [UnityTest]
        public IEnumerator OfflineChasesRushVirtualAndMissionScreens()
        {
            var prof = new PlayerProfile { Name = "Chaser", Level = 30, Mito = 200_000, City = "cras" };
            prof.Cars.Add(new OwnedCar { VehicleIndex = 0, Color = 1 });
            prof.Save();
            new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            var flow = new GameObject("Game").AddComponent<GameFlow>();
            flow.SkipParkingLot = true;
            yield return City(flow);
            var P = PlayerProfile.Current;
            var session = CitySession.Current;
            var ui = UiManager.Instance;
            var car = flow.PlayerCar;
            session.MissionOfferInterval = 1e6f;
            if (InstantMissions.Instance) InstantMissions.Instance.OfferInterval = InstantMissions.Instance.FirstOffer = 1e6f;
            MissionExtras.Attach(session, flow.City);   // the lead's GameFlow hook, done here until it lands
            Assert.AreEqual("cras", flow.City);

            // ---------------- tables ----------------
            var spawns = ChaseSpawns.For("cras");
            Assert.GreaterOrEqual(spawns.Count, 30, "chasespawnlist.text points");
            Assert.AreEqual(16, spawns[0].Slots.Count, "16 HUV slots per point");
            Assert.IsFalse(spawns[0].Name.StartsWith("Point "), "names from init\\chasespawn.xlt: " + spawns[0].Name);
            Assert.AreEqual(500, MitaDrink.Spots("cras").Count, "mitaquest.qpos");
            Assert.AreEqual(1, ChaseScreens.BlinkState(0.05f)); Assert.AreEqual(3, ChaseScreens.BlinkState(0.2f)); Assert.AreEqual(0, ChaseScreens.BlinkState(0.5f));
            Assert.AreEqual("1\"15\"30", ChaseScreens.ResultTime(75.5f));

            // ---------------- the call -> the chase ----------------
            // ChaseRules gives a level-30 driver two HUVs (1 + level / 30); these screens are checked with one
            ChaseMission.PackSize = 1;
            var offer = ChaseMission.Offer(session);
            Assert.IsTrue(offer && offer.Dialog.Id == ChaseScreens.OfferId, "CHASING 870000");
            yield return new WaitForSeconds(0.1f);
            Assert.AreEqual(1, Enumerable.Range(1, 4).Count(i => Shown(offer, "ID_CHASING_NOTIFY" + i)), "one NOTIFY art at a time");
            Shot("mc_chase_1_call.png");
            offer.Click("ID_CHASING_TABBUTTON");
            yield return null;
            var chase = ChaseMission.Active;
            Assert.IsTrue(chase, "Tab started the chase");
            Assert.AreEqual(2, chase.Mode);
            Assert.IsTrue(ui.Find(630000), "CHASEINFO (Cras) for the briefing");
            Assert.IsTrue(ui.Find(ChaseScreens.PlayInfoId), "CHASEPLAYINFO");
            Assert.IsTrue(chase.SpawnPoint != null, "the HUV started on a chasespawnlist point");
            yield return new WaitForSeconds(0.5f);
            Shot("mc_chase_2_info.png");
            chase.BriefingSeconds = 0f;
            yield return null; yield return null;
            Assert.AreEqual(3, chase.Mode);
            Assert.IsFalse(ui.Find(630000), "CHASEINFO goes with the briefing");
            var play = ui.Find(ChaseScreens.PlayInfoId);
            Assert.IsTrue(Shown(play, "ID_CHASEPLAYINFO_TIME_5") && Shown(play, "ID_CHASEPLAYINFO_HUVCOUNT1"), "time and HUV count digits");
            Assert.IsFalse(Shown(play, "ID_CHASEPLAYINFO_TIME_NUM_3"), "the digit art itself stays hidden");
            Shot("mc_chase_3_playinfo.png");
            long mito = P.Mito; int caught = P.HuvCaught;
            chase.Target.SimulateHit(1000f);
            yield return WaitFor(() => ui.Find(ChaseScreens.ResultId), 5, "CHASERESULT");
            Assert.IsFalse(ChaseMission.Active);
            Assert.AreEqual(caught + 1, P.HuvCaught);
            Assert.Greater(P.Mito, mito);
            Assert.IsTrue(ui.Find(ChaseScreens.RewardId), "the rising reward lines (1780000)");
            Assert.AreEqual(MissionText.Printf("%d Mito", ChaseMission.LastRows[0].Mito), Text(ui.Find(ChaseScreens.ResultId), "ID_CHASERESULT_MITO"));
            yield return new WaitForSeconds(0.4f);
            Shot("mc_chase_4_result.png");
            ui.Find(ChaseScreens.ResultId).Click("ID_CHASERESULT_OK");

            // giving up: CANCEL -> 10727 -> the chase ends failed
            session.StartChase(false);
            Assert.IsTrue(ChaseMission.Active);
            ui.Find(ChaseScreens.PlayInfoId).Click("ID_CHASEPLAYINFO_CANCEL");
            yield return null;
            Popup(true);
            yield return null;
            Assert.IsFalse(ChaseMission.Active, "quit after 10727");

            // PARTYCHASERESULT with four lines
            ChaseScreens.PartyResult(92.25f, new List<ChaseScreens.ResultRow>
            {
                new ChaseScreens.ResultRow { Name = "Chaser", Level = 30, Exp = 2310, Mito = 14500, LevelUps = 1 },
                new ChaseScreens.ResultRow { Name = "Rio", Level = 28, Exp = 2200, Mito = 13700 },
                new ChaseScreens.ResultRow { Name = "Duke", Level = 31, Exp = 2400, Mito = 14900 },
            });
            var pr = ui.Find(ChaseScreens.PartyResultId);
            Assert.AreEqual("Rio", Text(pr, "ID_PARTYCHASERESULT_USER2"));
            Assert.IsTrue(Shown(pr, "ID_PARTYCHASERESULT_LVUP1") && !Shown(pr, "ID_PARTYCHASERESULT_LVUP2"));
            yield return null;
            Shot("mc_chase_5_partyresult.png");
            pr.Click("ID_PARTYCHASERESULT_OK");

            // ---------------- Rush Time ----------------
            var rush = RushTime.Instance;
            Assert.IsTrue(rush, "Rush Time on the city session");
            rush.MinDrivers = 1;
            rush.Announce();
            Assert.AreEqual(RushTime.Phase.Waiting, rush.State);
            Teleport(car, rush.Zone, rush.Zone + Vector3.forward * 10f);
            yield return WaitFor(() => ui.Find(RushTime.WaitId), 5, "RUSHWAIT in the zone");
            yield return new WaitForSeconds(0.3f);
            StringAssert.Contains("/", Text(ui.Find(RushTime.WaitId), "ID_RUSHWAIT_WAIT_INFO"));
            Shot("mc_rush_1_wait.png");
            rush.StartNow();
            yield return WaitFor(() => rush.State == RushTime.Phase.Running, 5, "Rush Time running");
            Assert.IsTrue(ui.Find(RushTime.InfoId), "RUSHINFO");
            yield return new WaitForSeconds(0.3f);
            Shot("mc_rush_2_info.png");
            int enhancers = P.Items.Where(i => i.Id == "i_n_00003").Sum(i => i.Count);
            rush.Boss.SimulateHit(5000f);
            yield return WaitFor(() => ui.Find(RushTime.ResultId), 5, "RUSHRESULT");
            Assert.AreEqual("Chaser", rush.LuckyDriver);
            Assert.AreEqual(enhancers + 3, P.Items.Where(i => i.Id == "i_n_00003").Sum(i => i.Count), "three Mittron Enhancers");
            StringAssert.Contains("5000", Text(ui.Find(RushTime.ResultId), "ID_RUSHRESULT_TEXT"));
            Shot("mc_rush_3_result.png");
            yield return WaitFor(() => rush.State == RushTime.Phase.Idle, 10, "Rush Time over");

            // ---------------- Virtual HUV ----------------
            var vc = VirtualChase.Register(4);
            Assert.IsTrue(vc);
            Assert.IsTrue(ui.Find(VirtualChase.WaitId), "3830000 waiting");
            yield return new WaitForSeconds(0.2f);
            Shot("mc_vhuv_1_wait.png");
            vc.StartMatch();
            vc.CrashCooldown = 0f;
            yield return new WaitForSeconds(0.3f);
            Assert.IsTrue(ui.Find(VirtualChase.HudId) && ui.Find(VirtualChase.PlayInfoId), "VIRTUALCHASING + VIRTUALCHASEPLAYINFO");
            Shot("mc_vhuv_2_match.png");
            for (int i = 0; i < 12; i++) vc.Crash();
            Assert.AreEqual(12, vc.PlayerCrashes);
            vc.FinishNow();
            yield return WaitFor(() => ui.Find(VirtualChase.ResultId), 5, "VIRTUAL 3810000");
            Assert.AreNotEqual("1000000", Text(ui.Find(VirtualChase.ResultId), "ID_VIRTUAL_CHASING_RESULT_EXP"));
            Shot("mc_vhuv_3_result.png");
            ui.Find(VirtualChase.ResultId).Click("ID_VIRTUAL_CHASING_RESULT_OK");
            yield return WaitFor(() => !VirtualChase.Active, 3, "match closed");

            // ---------------- Mita 500 ----------------
            var spots = MitaSpots.Instance;
            yield return WaitFor(() => spots.Live.Count == MitaDrink.SpotCount, 5, "live Mita spots");
            long beforeMita = P.Mito;
            var spot = spots.Live[0];
            Teleport(car, spot.Position, spot.Position + Vector3.forward * 5f);
            yield return WaitFor(() => MissionUi.ScriptOpen, 5, "the salesman's line");
            Assert.AreEqual(150, MitaDrink.Value);
            Assert.AreEqual(beforeMita - MitaDrink.Price, P.Mito);
            Assert.AreEqual(1, MitaDrink.BoughtToday);
            Assert.Greater(MitaDrink.Apply(1000), 1000);
            Shot("mc_mita_1_drink.png");
            MissionUi.CloseScript();

            // ---------------- SPEEDCOMBO party digits ----------------
            var combo = DriftCombo.Current;
            Assert.IsTrue(combo, "the HUD's speed combo");
            // on a lane: a car off the road resets the chain every physics step (0x481910)
            if (session.Traffic.Sample(0, 5f, out var lanePos, out var laneFwd)) Teleport(car, lanePos, lanePos + laneFwd * 10f);
            yield return new WaitForSeconds(0.3f);
            combo.AddFromParty(3);
            yield return null;
            var sc = ui.Find(300000);
            Assert.AreEqual(3, combo.PartyAdded);
            Assert.AreEqual(0, combo.Count, "the party's tricks are not the driver's own count");
            Assert.IsTrue(Shown(sc, "ID_SPEEDCOMBO_PARTYCOMBO") && Shown(sc, "ID_SPEEDCOMBO_PARTYIMG") && !Shown(sc, "ID_SPEEDCOMBO_COMBO"));
            Shot("mc_speedcombo_party.png");

            // ---------------- EX and event deliveries ----------------
            var exJob = DeliveryService.Jobs.FirstOrDefault(j => j.Kind == DeliveryJob.JobKind.Ex && j.City == "cras");
            Assert.IsNotNull(exJob, "an EX loop in Cras");
            StringAssert.StartsWith("ex_", exJob.Key);
            Assert.Contains(exJob, DeliveryService.JobsAt(exJob.StartPost, 30), "EX loops for every level");
            var evJob = DeliveryService.Jobs.FirstOrDefault(j => j.Season == EventSeason.Valentine && j.City == "cras");
            Assert.IsNotNull(evJob, "an Easter egg delivery in Cras");
            Assert.IsFalse(DeliveryService.JobsAt(evJob.StartPost, 30).Contains(evJob), "events only in their season");
            SeasonEvents.Override = EventSeason.Valentine;
            Assert.IsTrue(DeliveryService.JobsAt(evJob.StartPost, 30).Contains(evJob));
            var delivery = DeliveryService.Instance;
            DeliveryService.NewLayout = true;
            delivery.Begin(exJob);
            yield return null;
            Assert.IsTrue(ui.Find(DeliveryService.NewInfoId) && ui.Find(DeliveryService.NewAdvId), "ARBEITPLAYINFONEW + ADV");
            Assert.AreEqual(exJob.Title, Text(ui.Find(DeliveryService.NewInfoId), "ID_ARBEITPLAYINFONEW_TITLE"));
            Shot("mc_delivery_1_newlayout.png");
            delivery.Abandon();
            DeliveryService.NewLayout = false;
            // an event job finished slower than bronze pays cols 37/38 (the Halloween delivery of Cras Station 1: 16 EXP / 24 Mito)
            evJob = DeliveryService.Jobs.FirstOrDefault(j => j.Kind == DeliveryJob.JobKind.Event && j.City == "cras" && j.ExpNone + j.MitoNone > 0);
            Assert.IsNotNull(evJob, "a Cras event job with a fourth tier");
            SeasonEvents.Override = evJob.Season == EventSeason.None ? EventSeason.None : evJob.Season;
            delivery.Begin(evJob);
            Assert.AreEqual(evJob.Points.Count, delivery.Markers().Count(), "event jobs show every stop");
            long expBefore = P.Exp + P.Level * 1_000_000L, mitoBefore = P.Mito;
            for (int i = 0; i < evJob.Points.Count; i++)
            {
                if (i == evJob.Points.Count - 1) delivery.Elapsed = evJob.Time[2] + 5f;
                var pt = MissionPosts.Find(evJob.Points[i]);
                Teleport(car, pt.Position, pt.Position + Vector3.forward);
                // a roof or bridge over the stop: put the car on the post's own height
                if (Mathf.Abs(car.transform.position.y - pt.Position.y) > 30f) { car.transform.position = pt.Position + Vector3.up; car.GetComponent<Rigidbody>().position = car.transform.position; }
                yield return WaitFor(() => delivery.Next > i || !delivery.Running, 5, "stop " + i);
            }
            yield return null;
            Assert.AreEqual(mitoBefore + evJob.MitoNone, P.Mito, "the no-medal Mito");
            Shot("mc_delivery_2_nomedal.png");
            Popup(false);
            SeasonEvents.Override = EventSeason.None;

            // ---------------- QUEST 1770000 / 1780000 ----------------
            ChaseScreens.NewRecord(83.456f);
            ChaseScreens.RewardPopup(12345, 67890);
            yield return new WaitForSeconds(0.35f);
            Assert.IsTrue(ui.Find(ChaseScreens.NewRecordId) && ui.Find(ChaseScreens.RewardId));
            Shot("mc_quest_newrecord_reward.png");

            // ---------------- QUESTPLAYINFO 720000 ----------------
            var q = QuestTracker.All[0];
            P.ActiveQuests.Add(new QuestProgress { QuestId = q.Id, Counter = 0 });
            yield return WaitFor(() => ui.Find(QuestPlayInfo.DialogId), 3, "QUESTPLAYINFO");
            StringAssert.Contains(q.Title, Text(ui.Find(QuestPlayInfo.DialogId), "ID_QUESTPLAYINFO_CONTENT"));
            Shot("mc_questplayinfo.png");
            P.ActiveQuests.Clear();
            yield return WaitFor(() => !ui.Find(QuestPlayInfo.DialogId), 3, "QUESTPLAYINFO closed");

            // ---------------- INSTANTMISSION 230000 (basic main UI) ----------------
            MissionTracker.Classic = true;
            var instants = InstantMissions.Instance;
            instants.Begin(InstantMissions.Eligible(P.Level)[0]);
            yield return null;
            var classic = ui.Find(MissionTracker.ClassicId);
            Assert.IsTrue(classic, "230000 with the basic HUD");
            Assert.IsFalse(ui.Find(MissionTracker.AdvId));
            StringAssert.Contains("/", Text(classic, "ID_INSTANTMISSION_INMISSION_CONTENT2ND"));
            Shot("mc_instant_classic.png");
            instants.GiveUp();
            MissionTracker.Classic = null;

            // ---------------- Auto-Drive ----------------
            var st = CityMapInfo.Stations("cras");
            // start on a lane some way from the stations (11124 refuses Auto-Drive at a station)
            Vector3 from = st[0].Position, fromFwd = Vector3.forward;
            for (int lane = 0; lane < session.Traffic.Network.Lanes.Count; lane += 7)
                if (session.Traffic.Sample(lane, 10f, out var lp, out var lf) && st.All(x => MissionPosts.Flat(x.Position, lp) > 200f) && MissionPosts.Flat(lp, st[1].Position) < 1500f)
                { from = lp; fromFwd = lf; break; }
            Teleport(car, from, from + fromFwd * 10f);
            yield return new WaitForSeconds(0.5f);
            var auto = AutoDrive.Start(st[1].Position);
            Assert.IsTrue(auto, "Auto-Drive started: " + AutoDrive.Refusal());
            Assert.Greater(auto.Path.Count, 5, "a way over the lanes");
            Assert.IsTrue(ui.Find(AutoDrive.DialogId), "AUTODRIVING");
            var start = car.transform.position;
            yield return new WaitForSeconds(6f);
            Assert.Greater(Vector3.Distance(start, car.transform.position), 15f, "the car drives itself");
            Shot("mc_autodrive.png");
            ui.Find(AutoDrive.DialogId).Click("ID_AUTODRIVING_STOP");
            yield return null;
            Popup(true);
            yield return null;
            Assert.IsFalse(AutoDrive.Active);
            Assert.IsFalse(car.ExternalControl, "the car is given back");
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

        [UnityTest]
        public IEnumerator OnlinePartyChaseAndMitaWithBot()
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
            yield return FrontEndSteps.ToCharacterSelect(flow, Url, "mctester", "secret123");
            yield return WaitFor(() => UiManager.Instance.Find(3250000), 15, "create-account popup");
            UiManager.Instance.Find(3250000).Click(UiPopup.Yes);
            yield return FrontEndSteps.NewCharacter("MCTester", 1);
            UiManager.Instance.Find(150000).Click("ID_CREATECAR_NEW_NEXT");
            yield return WaitFor(() => UiManager.Instance.Find(320000), 600, "city HUD");
            yield return WaitFor(() => OnlineSession.Instance.Channel != null, 15, "channel join");
            var ui = UiManager.Instance;
            var session = CitySession.Current;
            session.MissionOfferInterval = 1e6f;
            if (InstantMissions.Instance) InstantMissions.Instance.OfferInterval = InstantMissions.Instance.FirstOffer = 1e6f;
            MissionExtras.Attach(session, flow.City);
            bot = Node($"tools/bot.js 1 --url {Url} --name Cbot --party 400 --city {session.City}");
            yield return WaitFor(() => OnlineSession.Instance.Remotes && OnlineSession.Instance.Remotes.Count >= 1, 20, "bot in the channel");
            PartyWindow.Invite("Cbot1");
            yield return WaitFor(() => ui.Find(PartyWindow.CreateDlg), 3, "party create dialog");
            ui.Find(PartyWindow.CreateDlg).Click("ID_PARTY_CREATE_BUTTON");
            yield return WaitFor(() => PartyWindow.Members.Count == 2, 10, "bot joined the party");

            // the leader's Tab on the call starts the party chase on the server; the hits wear the shared health down
            var P = PlayerProfile.Current;
            long mito = P.Mito;
            var offer = ChaseMission.Offer(session);
            offer.Click("ID_CHASING_TABBUTTON");
            yield return WaitFor(() => ChaseMission.Active && ChaseMission.Active.Party, 10, "chase.party.start");
            var chase = ChaseMission.Active;
            chase.BriefingSeconds = 0f;
            yield return new WaitForSeconds(0.5f);
            Shot("mc_online_partychase_play.png");
            float waited = 0;
            while (ChaseMission.Active && waited < 30f)
            {
                chase.Target?.SimulateHit(40f);
                yield return new WaitForSecondsRealtime(0.3f);
                waited += 0.3f;
            }
            yield return WaitFor(() => ui.Find(ChaseScreens.PartyResultId), 10, "PARTYCHASERESULT");
            var pr = ui.Find(ChaseScreens.PartyResultId);
            Assert.AreEqual(2, ChaseMission.LastRows.Count, "both members' lines");
            Assert.AreEqual("MCTester", Text(pr, "ID_PARTYCHASERESULT_USER1"));
            Assert.AreEqual("Cbot1", Text(pr, "ID_PARTYCHASERESULT_USER2"));
            var mine = ChaseMission.LastRows[0];
            long baseExp = 300 + 60L * mine.Level;
            Assert.GreaterOrEqual(mine.Exp, baseExp + baseExp / 10, "the party bonus (+10 % for two members at or below level 80)");
            Assert.Greater(PlayerProfile.Current.Mito, mito, "paid by the server (the driver of the result push)");
            yield return new WaitForSeconds(0.3f);
            Shot("mc_online_partychase_result.png");
            pr.Click("ID_PARTYCHASERESULT_OK");

            // a Mita 500 bottle through the server
            long before = PlayerProfile.Current.Mito;
            bool? ok = null;
            MitaDrink.Buy(7, r => ok = r);
            yield return WaitFor(() => ok.HasValue, 10, "mita reply");
            Assert.IsTrue(ok.Value);
            Assert.AreEqual(before - MitaDrink.Price, PlayerProfile.Current.Mito, "the server took 500 Mito");
            MissionUi.CloseScript();
        }
    }
}
