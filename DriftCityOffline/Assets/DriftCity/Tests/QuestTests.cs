using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DriftCity.Game;
using DriftCity.Net;
using DriftCity.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;

namespace DriftCity.Tests
{
    /// <summary>
    /// The QuestClient.tdf step quests (StepQuests / StepQuestRunner, MISSIONS.md section 11), the factions and the
    /// delivery rankings (QSRANKLIST, section 12): offline in Moon Palace (the tutorial quest from the station's QUEST button
    /// to its reward, a crash-limited repeat quest failing and retried, the time / speed / trail panels, the Team ROO
    /// labels), a Team ROO station in Oros, and online against the local server (the server keeps the steps and pays).
    /// </summary>
    public class QuestTests
    {
        string tempSave;

        void NewSave(string city, int level)
        {
            tempSave = Path.Combine(Path.GetTempPath(), "driftcity_quests_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempSave);
            PlayerProfile.SaveDirOverride = tempSave;
            PlayerProfile.Slot = 0;
            MissionRecords.Reset();
            // no delivery event season: the station shows ARBEIT / TIMEATTACK, not the event pair (ServerSwitches.EventDelivery)
            SeasonEvents.Override = EventSeason.None;
            var p = new PlayerProfile { Name = "Rookie", Level = level, Mito = 100_000, City = city };
            p.Cars.Add(new OwnedCar { VehicleIndex = 0, Color = 1 });
            p.Save();
            PlayerProfile.Current = null;
        }

        [TearDown]
        public void TearDown()
        {
            SeasonEvents.Override = null;
            QuestUnlocks.Enabled = true;
            PlayerProfile.SaveDirOverride = null;
            MissionRecords.Reset();
            foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Exclude).Where(g => g.transform.parent == null)) Object.Destroy(go);
            if (tempSave != null) try { Directory.Delete(tempSave, true); } catch { }
        }

        static void Shot(string name)
        {
            Directory.CreateDirectory("Preview");
            var tex = UiManager.Instance.Capture(Camera.main);
            File.WriteAllBytes(Path.Combine("Preview", name), tex.EncodeToPNG());
            Object.Destroy(tex);
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

        static void TeleportTo(DriftCar car, string post)
        {
            var icon = MissionPosts.Find(post);
            Assert.IsNotNull(icon, post);
            Teleport(car, icon.Position, icon.Position + Vector3.forward * 10f);
        }

        /// <summary>Pages the open QUESTDIALOG to the end (NEXT) so its last-page button shows.</summary>
        static IEnumerator LastPage()
        {
            for (int i = 0; i < 40; i++)
            {
                var d = UiManager.Instance.Find(600000);
                if (!d) yield break;
                var next = d.Control("ID_QUESTDIALOG_NEXT");
                if (!next || !next.gameObject.activeSelf) yield break;
                d.Click("ID_QUESTDIALOG_NEXT");
                yield return null;
            }
        }

        /// <summary>Closes a step's lines (DONE on the last page).</summary>
        static IEnumerator CloseLines()
        {
            for (int k = 0; k < 4 && UiManager.Instance.Find(600000); k++)
            {
                yield return LastPage();
                var d = UiManager.Instance.Find(600000);
                if (d && d.Control("ID_QUESTDIALOG_DONE") && d.Control("ID_QUESTDIALOG_DONE").gameObject.activeSelf) d.Click("ID_QUESTDIALOG_DONE");
                yield return null;
            }
        }

        static void ClosePopups()
        {
            foreach (var v in UiManager.Instance.OpenViews.Where(v => v && v.Dialog.Id == 3250000).ToList())
            {
                if (v.Control(UiPopup.No) && v.Control(UiPopup.No).gameObject.activeSelf) v.Click(UiPopup.No);
                else v.Click("ID_POPUP_OKONLY");
            }
        }

        static bool Shown(NuiView v, string id) => v && v.Control(id) && v.Control(id).gameObject.activeInHierarchy;

        IEnumerator EnterCity(string city)
        {
            new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            var flow = new GameObject("Game").AddComponent<GameFlow>();
            flow.SkipParkingLot = true;
            yield return FrontEndSteps.ToCharacterSelect(flow);
            yield return FrontEndSteps.EnterSelected();
            yield return WaitFor(() => flow.InCity && CitySession.Current && flow.PlayerCar, 600, "city");
            CitySession.Current.MissionOfferInterval = 1e6f;
            if (InstantMissions.Instance) InstantMissions.Instance.OfferInterval = InstantMissions.Instance.FirstOffer = 1e6f;
            if (PatrolMission.Instance) PatrolMission.Instance.OfferRadius = 0f;
            Assert.AreEqual(city, flow.City);
            // the hooks land through MissionExtras / StationPanel; attach here if the lead's GameFlow doesn't call them yet
            if (!StepQuestRunner.Instance) StepQuestRunner.Attach(CitySession.Current, flow.City);
            if (!QuestPlayInfo.Instance) QuestPlayInfo.Attach(CitySession.Current);
        }

        [UnityTest]
        public IEnumerator StepQuestsAndFactions()
        {
            NewSave("moonpalace", 1);
            yield return EnterCity("moonpalace");
            var flow = GameFlow.Instance;
            var P = PlayerProfile.Current;
            var car = flow.PlayerCar;
            var runner = StepQuestRunner.Instance;

            // ---------------- the table (loader 0x7093c0) ----------------
            Assert.AreEqual(320, StepQuests.All.Count);
            var tutorial = StepQuests.Find(1);
            Assert.AreEqual("q_00004", tutorial.Id);
            Assert.AreEqual("post_1st_00052", tutorial.StartPost);
            CollectionAssert.AreEqual(new[] { StepKind.Pass, StepKind.Pass, StepKind.Stop }, tutorial.Steps.Select(s => s.Kind).ToArray());
            Assert.AreEqual(700, tutorial.Exp); Assert.AreEqual(300, tutorial.Mito);
            CollectionAssert.AreEqual(new[] { "i_n_start01" }, tutorial.Items);
            Assert.AreEqual(QuestUnlock.Roo, StepQuests.Find(127).Unlock);
            Assert.AreEqual(QuestUnlock.Omd, StepQuests.Find(145).Unlock);
            Assert.AreEqual(Factions.Roo, StepQuests.Branch(StepQuests.Find(144)));
            Assert.AreEqual(Factions.Omd, StepQuests.Branch(StepQuests.Find(162)));
            Assert.IsTrue(StepQuests.FindId("q_00159").RooTarget);
            Assert.IsTrue(Factions.IsRooPost("post_4rd_00007") && Factions.IsRooPost("post_4rd_00009"));
            Assert.IsFalse(Factions.IsRooPost("post_4rd_00008"));
            Assert.AreEqual("[Moon Palace] Destination <Station 1> start trailing.", MissionText.Get("10128", "Moon Palace", "Station 1"));
            Assert.IsTrue(MissionMarkers.All().Any(m => m.Icon == "ID_MINIMAP_ICON_MQ_START"), "the tutorial's giver on the minimap");

            // ---------------- the station's QUEST button -> QUESTDIALOG -> RECV ----------------
            var station = CityMapInfo.Stations("moonpalace").First(s => s.Id == "post_1st_00052");
            Teleport(car, station.Position, station.Position + Vector3.forward * 20f);
            yield return WaitFor(() => UiManager.Instance.Find(400000), 5, "station panel");
            var panel = UiManager.Instance.Find(400000);
            // a rookie (quest manager +0x108 off) gets the beginner panel (0x561f80): BEGINNERQUEST only
            Assert.IsTrue(Shown(panel, "ID_ENTRANCE_RQHOUSE_BEGINNERQUEST"), "BEGINNERQUEST");
            Assert.IsFalse(Shown(panel, "ID_ENTRANCE_RQHOUSE_QUEST") || Shown(panel, "ID_ENTRANCE_RQHOUSE_ARBEIT") || Shown(panel, "ID_ENTRANCE_RQHOUSE_CHASING"), "the beginner panel");
            Assert.IsFalse(Shown(panel, "ID_ENTRANCE_RQHOUSE_SECRETMISSION"), "an OMD station");
            yield return WaitFor(() => panel.Control("ID_ENTRANCE_RQHOUSE_BEGINNERQUEST").Interactable, 2, "BEGINNERQUEST enabled");
            yield return new WaitForSeconds(0.3f);
            Shot("quests_station_beginner.png");
            panel.Click("ID_ENTRANCE_RQHOUSE_BEGINNERQUEST");
            yield return null;
            Assert.IsTrue(UiManager.Instance.Find(600000), "QUESTDIALOG");
            Assert.AreEqual(1, StepQuestRunner.OfferedQuest.Number);
            yield return LastPage();
            var dlg = UiManager.Instance.Find(600000);
            Assert.IsTrue(Shown(dlg, "ID_QUESTDIALOG_RECV"), "RECV on the last page");
            Assert.IsFalse(Shown(dlg, "ID_QUESTDIALOG_ACCEPT"));
            yield return new WaitForSeconds(0.2f);
            Shot("quests_dialog_recv.png");
            dlg.Click("ID_QUESTDIALOG_RECV");
            yield return null;
            Assert.AreEqual(1, P.StepQuest.QuestId, "the tutorial runs");
            // step 0 passes the station itself; its script jumps the car to the course
            yield return WaitFor(() => P.StepQuest.Step >= 1, 5, "step 0 passed");
            yield return CloseLines();
            yield return WaitFor(() => QuestPlayInfo.Instance && QuestPlayInfo.Instance.View, 3, "QUESTPLAYINFO");
            string content = QuestPlayInfo.Instance.View.Control("ID_QUESTPLAYINFO_CONTENT").GetComponentInChildren<UnityEngine.UI.Text>(true).text;
            StringAssert.Contains(tutorial.Steps[P.StepQuest.Step].Text, content);
            Assert.IsTrue(MissionMarkers.All().Any(m => m.Icon == "ID_MINIMAP_ICON_MQ_NORMAL"), "the step's post on the minimap");
            yield return new WaitForSeconds(0.5f);
            Shot("quests_step_bar.png");
            // step 1: through post_1st_t2_001; step 2: stop at Station 1
            TeleportTo(car, tutorial.Steps[1].Post);
            yield return WaitFor(() => P.StepQuest.Step >= 2, 5, "step 1 passed");
            yield return CloseLines();
            TeleportTo(car, tutorial.Steps[2].Post);
            yield return WaitFor(() => P.StepQuest.Counter > 0, 8, "step 2: stopped at the station");
            yield return CloseLines();
            System.Func<string> barText = () => QuestPlayInfo.Instance.View ? QuestPlayInfo.Instance.View.Control("ID_QUESTPLAYINFO_CONTENT").GetComponentInChildren<UnityEngine.UI.Text>(true).text : "";
            yield return WaitFor(() => barText() == GameTables.Text("10178"), 2, "Mission Completed. on the bar (the bar polls 4 times a second)");
            // the end post: the end NPC's lines with REWARD
            ClosePopups();
            if (UiManager.Instance.Find(400000)) UiManager.Instance.Find(400000).gameObject.SetActive(false);
            TeleportTo(car, tutorial.EndPost);
            yield return WaitFor(() => UiManager.Instance.Find(600000), 5, "end lines");
            yield return LastPage();
            dlg = UiManager.Instance.Find(600000);
            Assert.IsTrue(Shown(dlg, "ID_QUESTDIALOG_REWARD"), "REWARD");
            yield return new WaitForSeconds(0.2f);
            Shot("quests_reward.png");
            long mito0 = P.Mito; int level0 = P.Level; long exp0 = P.Exp;
            dlg.Click("ID_QUESTDIALOG_REWARD");
            yield return WaitFor(() => P.StepQuestsDone.Contains(1), 5, "reward");
            Assert.AreEqual(mito0 + 300, P.Mito);
            Assert.IsTrue(P.Level > level0 || P.Exp >= exp0 + 700);
            Assert.AreEqual(1, P.Count("i_n_start01"));
            Assert.AreEqual(0, P.StepQuest.QuestId);
            Assert.IsNotNull(StepQuests.Refusal(P, tutorial), "done");
            ClosePopups();
            MissionUi.CloseScript();   // the bakery is a quest giver too: its offer may be up

            // ---------------- crashes (740000 / 780000), the failure and RETRY ----------------
            P.Level = 25;
            var money = StepQuests.Find(10007);
            Assert.AreEqual(2, money.Steps[1].Crashes);
            StepQuestRunner.Accept(money);
            yield return null;
            Assert.AreEqual(10007, P.StepQuest.QuestId);
            TeleportTo(car, money.Steps[0].Post);
            yield return WaitFor(() => P.StepQuest.Step == 1, 5, "Money Transporter step 0 (stop)");
            yield return CloseLines();
            yield return WaitFor(() => UiManager.Instance.Find(StepQuestRunner.CrashId), 3, "740000");
            var sensor = car.GetComponent<QuestCrashSensor>();
            Assert.IsNotNull(sensor);
            sensor.Simulate();
            yield return WaitFor(() => runner.Crashes == 1, 2, "one crash");
            Assert.IsTrue(UiManager.Instance.Find(StepQuestRunner.EffectId), "780000 crash digits");
            Assert.IsTrue(Shown(UiManager.Instance.Find(StepQuestRunner.CrashId), "ID_QUESTPLAYINFO_CRASH_1"));
            Assert.IsTrue(Shown(UiManager.Instance.Find(StepQuestRunner.CrashId), "ID_QUESTPLAYINFO_CRASH_MAX_2"));
            yield return WaitFor(() => barText().Contains(money.Steps[1].Text), 2, "the bar on the running step");
            Shot("quests_crash_hud.png");
            yield return new WaitForSeconds(1.2f);
            sensor.Simulate();
            yield return WaitFor(() => runner.Failed, 3, "second crash fails the step");
            Assert.IsTrue(UiManager.Instance.Find(3250000), "10125 attempt again?");
            Shot("quests_failed.png");
            TeleportTo(car, "post_1st_00052");   // away from step 0's post, which would pass again at once
            UiManager.Instance.Find(3250000).Click(UiPopup.Yes);
            yield return null;
            Assert.AreEqual(0, P.StepQuest.Step, "RETRY starts over");
            Assert.AreEqual(10007, P.StepQuest.QuestId);
            StepQuestRunner.Abandon();
            yield return null;
            Assert.AreEqual(0, P.StepQuest.QuestId, "abandoned");

            // ---------------- the time limit (730000) ----------------
            var rookie2 = StepQuests.Find(4);
            P.Level = 4; P.StepQuestsDone.AddRange(new[] { 2, 3 });
            StepQuestRunner.Accept(rookie2);
            yield return WaitFor(() => UiManager.Instance.Find(StepQuestRunner.TimeId), 3, "730000");
            float t0 = runner.TimeLeft;
            Assert.AreEqual(35f, t0, 0.6f);
            yield return new WaitForSeconds(1f);
            Assert.Less(runner.TimeLeft, t0);
            Shot("quests_timer.png");
            StepQuestRunner.Abandon();
            yield return null;

            // ---------------- the speed range (750000) and the trail (760000) ----------------
            // (Suspicious Group, Koinonia: 100-300 km/h on steps 1-3; the panel shows wherever the post is)
            var speedQuest = StepQuests.All.FirstOrDefault(q => q.Steps.Any(s => s.SpeedMin > 0 || s.SpeedMax > 0));
            Assert.IsNotNull(speedQuest);
            if (speedQuest != null)
            {
                int k = speedQuest.Steps.FindIndex(s => s.SpeedMin > 0 || s.SpeedMax > 0);
                P.StepQuest = new QuestProgress { QuestId = speedQuest.Number, Step = k };
                runner.BeginStep();
                yield return WaitFor(() => UiManager.Instance.Find(StepQuestRunner.SpeedId), 3, "750000");
                var minText = UiManager.Instance.Find(StepQuestRunner.SpeedId).Control("ID_QUESTPLAYINFO_SPEED_MIN").GetComponentInChildren<UnityEngine.UI.Text>(true);
                yield return WaitFor(() => minText.text == speedQuest.Steps[k].SpeedMin.ToString("000"), 2, "SPEED_MIN");
                yield return WaitFor(() => minText.enabled, 2, "the blink's on phase");
                Shot("quests_speed.png");
                P.StepQuest = new QuestProgress();
                runner.ClearStep();
            }
            var trail = StepQuests.Find(14);
            Assert.AreEqual(StepKind.Trail, trail.Steps[0].Kind);
            P.StepQuest = new QuestProgress { QuestId = 14 };
            runner.BeginStep();
            TeleportTo(car, trail.Steps[0].Post);
            yield return WaitFor(() => P.StepQuest.Step == 1 && runner.Target, 5, "the trail car");
            yield return CloseLines();
            Assert.IsTrue(runner.Trailing);
            var target = runner.Target;
            float tt = target.T;
            var behind = target.transform.position - target.transform.forward * 35f;
            Teleport(car, behind, target.transform.position);
            yield return WaitFor(() => UiManager.Instance.Find(StepQuestRunner.FollowId), 3, "760000");
            yield return new WaitForSeconds(0.6f);
            Assert.Greater(target.T, tt, "the target drives its path");
            Shot("quests_trail.png");
            StepQuestRunner.Abandon();
            yield return null;
            Assert.IsFalse(runner.Target, "the trail car leaves with the quest");

            // ---------------- Team ROO: patrol rows, delivery labels, banners, the rank list ----------------
            Assert.IsFalse(PatrolMission.RowsFor("oros", 50, Factions.Omd).Any(r => r.Kind == "move_ROO"));
            Assert.IsTrue(PatrolMission.RowsFor("oros", 50, Factions.Roo).Any(r => r.Kind == "move_ROO"), "Monica's Scouting for Team ROO");
            Assert.IsFalse(PatrolMission.RowsFor("oros", 50, Factions.Roo).Any(r => r.Kind == "move"));
            P.Faction = Factions.Roo;
            Assert.AreEqual("10659", Factions.DeliveryText("10133"));
            if (UiManager.Instance.Find(400000)) UiManager.Instance.Find(400000).gameObject.SetActive(true);
            Teleport(car, station.Position, station.Position + Vector3.forward * 20f);
            yield return new WaitForSeconds(0.5f);
            DeliveryService.OpenBoard(CitySession.Current, station);
            yield return null;
            Assert.IsTrue(DeliveryService.BoardOpen);
            Assert.IsTrue(Shown(DeliveryService.Board, "ID_ARBEIT_TIMEATTACK_TITLE"), "Time Attack title for Team ROO");
            Assert.IsTrue(Shown(DeliveryService.Board, "ID_ARBEIT_SHOWWEEK_RANKLIST"), "the ranking buttons");
            yield return new WaitForSeconds(0.2f);
            Shot("quests_timeattack_board.png");
            var job = DeliveryService.BoardJobs[0];
            DeliveryService.CloseBoard();
            MissionRecords.Current.AddTime(job.Key, 80f, 1);
            QsRankList.Show(job, 0, 80f);
            yield return null;
            Assert.IsTrue(QsRankList.View, "QSRANKLIST");
            Assert.AreEqual(1, QsRankList.Rows.Count);
            Assert.IsTrue(Shown(QsRankList.View, "ID_QSRANKLIST_TIMEATTACK_TITLE"));
            yield return new WaitForSeconds(0.2f);
            Shot("quests_ranklist.png");
            QsRankList.Close();
            // Team ROO's chase banners play on the screen-effect camera (not in Camera.main's capture)
            StepQuestRunner.RooChaseBanner(true);
            yield return null;
            Assert.IsTrue(GameEffects.ScreenPlaying("beat_success"), "beat_success (UI effect 0x2d)");
            P.Faction = Factions.Omd;
        }

        [UnityTest]
        public IEnumerator RooStationInOros()
        {
            NewSave("oros", 60);
            yield return EnterCity("oros");
            var P = PlayerProfile.Current;
            var car = GameFlow.Instance.PlayerCar;
            // the storyline up to the choice at #126
            foreach (var q in StepQuests.All.Where(q => q.IsMain && q.Number <= 126)) P.StepQuestsDone.Add(q.Number);
            P.Faction = Factions.Roo;
            var hq = MissionPosts.Find("post_4rd_00009");
            Assert.AreEqual("Team Roo HQ", hq.Name);
            Teleport(car, hq.Position, hq.Position + Vector3.forward * 20f);
            yield return WaitFor(() => UiManager.Instance.Find(400000), 5, "Team Roo HQ panel");
            var panel = UiManager.Instance.Find(400000);
            Assert.IsTrue(Shown(panel, "ID_ENTRANCE_RQHOUSE_TEAMROO") && Shown(panel, "ID_ENTRANCE_RQHOUSE_SECRETMISSION") && Shown(panel, "ID_ENTRANCE_RQHOUSE_TIMEATTACK"));
            Assert.IsFalse(Shown(panel, "ID_ENTRANCE_RQHOUSE_QUEST") || Shown(panel, "ID_ENTRANCE_RQHOUSE_ARBEIT"));
            Assert.IsTrue(StepQuests.AvailableAt("post_4rd_00009", P).Any(q => q.Number == 127), "Team ROO Headquarters offered at the HQ");
            yield return new WaitForSeconds(0.4f);
            Shot("quests_roo_station.png");
            panel.Click("ID_ENTRANCE_RQHOUSE_SECRETMISSION");
            yield return null;
            Assert.AreEqual(127, StepQuestRunner.OfferedQuest.Number);
            yield return LastPage();
            yield return new WaitForSeconds(0.2f);
            Shot("quests_roo_join.png");
            MissionUi.CloseScript();
            // after joining Team ROO the OMD branch is closed
            P.StepQuestsDone.Add(127);
            Assert.IsNotNull(StepQuests.Refusal(P, StepQuests.Find(145)));
            Assert.IsNull(StepQuests.Refusal(P, StepQuests.Find(128)));
        }

        /// <summary>
        /// The unlocks of QuestClient.tdf column 5 as 0x703d20 sets them: the station's beginner panel (BEGINNERQUEST /
        /// ING_BEGINNERQUES) until the ARBEIT quest, then the full one; the Delivery Service list (10182), the HUV calls,
        /// the city gates (10746); the OMD finale switching the quest button off; the retry bonuses; an event quest.
        /// </summary>
        [UnityTest]
        public IEnumerator BeginnerPanelAndUnlocks()
        {
            NewSave("moonpalace", 1);
            yield return EnterCity("moonpalace");
            var P = PlayerProfile.Current;
            var car = GameFlow.Instance.PlayerCar;
            var runner = StepQuestRunner.Instance;

            var u = QuestUnlocks.Of(P);
            Assert.IsFalse(u.Rookie || u.Arbeit || u.Chase || u.City2 || u.City3 || u.City4 || u.City5, "a level-1 rookie has nothing open");
            Assert.IsFalse(QuestUnlocks.ChaseOffer(P), "no HUV calls before CHASE");
            Assert.AreEqual(GameTables.Text("10746"), QuestUnlocks.CityRefusal(P, "koinonia"));
            Assert.IsNull(QuestUnlocks.CityRefusal(P, "moonpalace"));
            Assert.AreEqual(GameTables.Text("10066"), QuestUnlocks.BattleZoneRefusal(P));

            // the beginner panel; the Delivery Service list stays shut (10182)
            var station = CityMapInfo.Stations("moonpalace").First(s => s.Id == "post_1st_00052");
            Teleport(car, station.Position, station.Position + Vector3.forward * 20f);
            yield return WaitFor(() => UiManager.Instance.Find(400000), 5, "station panel");
            var panel = UiManager.Instance.Find(400000);
            var stationPanel = CitySession.Current.GetComponent<StationPanel>();
            Assert.IsTrue(stationPanel && stationPanel.Beginner, "the beginner panel (0x561f80)");
            Assert.IsTrue(Shown(panel, "ID_ENTRANCE_RQHOUSE_BEGINNERQUEST"));
            DeliveryService.OpenBoard(CitySession.Current, station);
            yield return null;
            Assert.IsFalse(DeliveryService.BoardOpen, "10182");
            Assert.IsTrue(UiManager.Instance.Find(3250000), "the 10182 popup");
            Shot("quests_delivery_locked.png");
            ClosePopups();

            // a quest running: ING_BEGINNERQUES, BEGINNERQUEST off (Rookie Mission 1/3's first post is elsewhere)
            P.Level = 2; P.StepQuestsDone.AddRange(new[] { 1, 2 });
            P.StepQuest = new QuestProgress { QuestId = 3 };
            yield return WaitFor(() => Shown(panel, "ID_ENTRANCE_RQHOUSE_ING_BEGINNERQUES"), 2, "ING_BEGINNERQUES");
            Assert.IsFalse(panel.Control("ID_ENTRANCE_RQHOUSE_BEGINNERQUEST").Interactable);
            yield return new WaitForSeconds(0.3f);
            Shot("quests_beginner_running.png");
            P.StepQuest = new QuestProgress(); runner.ClearStep();

            // the rookie missions done (#5, ARBEIT): the full panel
            P.StepQuestsDone.AddRange(new[] { 3, 4, 5 });
            yield return WaitFor(() => { var v = UiManager.Instance.Find(400000); return v && Shown(v, "ID_ENTRANCE_RQHOUSE_QUEST"); }, 3, "the full panel");
            panel = UiManager.Instance.Find(400000);
            Assert.IsFalse(stationPanel.Beginner);
            Assert.IsTrue(Shown(panel, "ID_ENTRANCE_RQHOUSE_ARBEIT") && Shown(panel, "ID_ENTRANCE_RQHOUSE_CHASING"));
            Assert.IsFalse(Shown(panel, "ID_ENTRANCE_RQHOUSE_BEGINNERQUEST"));
            yield return WaitFor(() => panel.Control("ID_ENTRANCE_RQHOUSE_ARBEIT").Interactable, 2, "ARBEIT open");
            yield return new WaitForSeconds(0.3f);
            Shot("quests_station_full.png");

            // CHASE and the cities
            Assert.IsFalse(QuestUnlocks.Of(P).Chase);
            P.StepQuestsDone.Add(9);
            Assert.IsTrue(QuestUnlocks.ChaseOffer(P), "Start Chasing! (CHASE)");
            P.StepQuestsDone.Add(34);
            Assert.IsNull(QuestUnlocks.CityRefusal(P, "koinonia"), "Report (CITY_2ND)");
            Assert.IsNotNull(QuestUnlocks.CityRefusal(P, "cras"));
            P.Level = 29;
            Assert.IsNull(QuestUnlocks.CityRefusal(P, "cras"), "Cras from level 29");
            Assert.IsNotNull(QuestUnlocks.CityRefusal(P, "taipei"));

            // the OMD finale (q_00167): the station's quest button stays off
            P.StepQuestsDone.Add(162);
            Assert.IsTrue(QuestUnlocks.StoryOver(P));
            yield return WaitFor(() => !panel.Control("ID_ENTRANCE_RQHOUSE_QUEST").Interactable, 2, "QUEST off after the finale");
            P.StepQuestsDone.Remove(162);

            // the server's bonuses: two start-overs give 6 s more and one crash more
            var timed = StepQuests.All.First(q => q.Steps.Count > 1 && q.Steps[0].Seconds > 0 && q.Steps[0].Post != station.Id && StepQuests.Available(P, q));
            StepQuestRunner.Accept(timed);
            yield return null;
            Assert.AreEqual(timed.Number, P.StepQuest.QuestId);
            Assert.AreEqual(0, P.StepQuestTries);
            StepQuestRunner.Retry(); yield return null;
            StepQuestRunner.Retry(); yield return null;
            Assert.AreEqual(2, P.StepQuestTries);
            Assert.AreEqual(timed.Steps[0].Seconds + 6f, runner.TimeLeft, 0.6f, "the time bonus n*3 s");
            Assert.AreEqual(3, StepQuests.StepCrashes(StepQuests.Find(10007).Steps[1], 2));
            Assert.AreEqual(9, StepQuests.StepCrashes(new QuestStep { Crashes = 9 }, 5), "capped at 9");
            StepQuestRunner.Abandon();
            yield return null;
            Assert.AreEqual(0, P.StepQuestTries);

            // an event quest (kind 99) with its season: Independence Day in the summer festival
            var indy = StepQuests.Find(10045);
            SeasonEvents.Override = EventSeason.None;
            Assert.IsNotNull(StepQuests.Refusal(P, indy));
            SeasonEvents.Override = EventSeason.Summer;
            Assert.IsNull(StepQuests.Refusal(P, indy));
            Assert.AreEqual(EventSeason.Harvest, StepQuests.EventSeasonOf(StepQuests.Find(10046)));
            ClosePopups();
            TeleportTo(car, indy.StartPost);
            yield return WaitFor(() => UiManager.Instance.Find(600000), 5, "the event quest giver");
            Assert.AreEqual(10045, StepQuestRunner.OfferedQuest.Number);
            yield return new WaitForSeconds(0.3f);
            Shot("quests_event_offer.png");
            MissionUi.CloseScript();

            // QSRANKLIST: MYTEAM without a crew
            var job = DeliveryService.JobsAt(station.Id, P.Level).FirstOrDefault();
            if (job != null)
            {
                QsRankList.Show(job, 0);
                yield return null;
                Assert.AreEqual("", QsRankList.MyTeam);
                QsRankList.Close();
            }
        }

        // ---------------- online ----------------

        const int Port = 7891;
        static string Url => $"ws://127.0.0.1:{Port}";
        static string ServerDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "DriftCityServer"));

        [UnityTest]
        public IEnumerator StepQuestOnline()
        {
            if (!File.Exists(Path.Combine(ServerDir, "server.js"))) Assert.Ignore("DriftCityServer not found");
            tempSave = Path.Combine(Path.GetTempPath(), "driftcity_quests_online_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempSave);
            PlayerProfile.SaveDirOverride = tempSave; PlayerProfile.Slot = 0; PlayerProfile.Current = null;
            var psi = new ProcessStartInfo("node", "server.js") { WorkingDirectory = ServerDir, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.EnvironmentVariables["DC_PORT"] = Port.ToString();
            psi.EnvironmentVariables["DC_DB"] = Path.Combine(tempSave, "test.db");
            var server = Process.Start(psi);
            server.OutputDataReceived += (_, e) => { if (e.Data != null) Debug.Log("[node] " + e.Data); };
            server.ErrorDataReceived += (_, e) => { if (e.Data != null) Debug.Log("[node err] " + e.Data); };
            server.BeginOutputReadLine(); server.BeginErrorReadLine();
            try
            {
                float t = 0; bool up = false;
                while (!up && t < 15f)
                {
                    try { using (var c = new System.Net.Sockets.TcpClient()) { c.Connect("127.0.0.1", Port); up = true; } } catch { }
                    if (!up) { yield return new WaitForSecondsRealtime(0.25f); t += 0.25f; }
                }
                Assert.IsTrue(up, "server started");
                new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
                var flow = new GameObject("Game").AddComponent<GameFlow>();
                flow.SkipParkingLot = true; flow.SkipIntro = true;
                yield return FrontEndSteps.ToCharacterSelect(flow, Url, "quester", "secret123");
                yield return WaitFor(() => UiManager.Instance.Find(3250000), 15, "create-account popup");
                UiManager.Instance.Find(3250000).Click(UiPopup.Yes);
                yield return FrontEndSteps.NewCharacter("quester", 1);
                UiManager.Instance.Find(150000).Click("ID_CREATECAR_NEW_NEXT");
                yield return WaitFor(() => PlayerProfile.Current != null && PlayerProfile.Current.OnlineDriver, 15, "server driver");
                yield return WaitFor(() => flow.InCity && CitySession.Current && flow.PlayerCar, 600, "city");
                if (!StepQuestRunner.Instance) StepQuestRunner.Attach(CitySession.Current, flow.City);
                var runner = StepQuestRunner.Instance;
                var q = StepQuests.Find(1);

                // the server refuses what the table forbids (#2 needs level 2 and #1)
                StepQuestRunner.Accept(StepQuests.Find(2));
                yield return new WaitForSeconds(0.5f);
                Assert.AreEqual(0, PlayerProfile.Current.StepQuest.QuestId);
                ClosePopups();

                StepQuestRunner.Accept(q);
                yield return WaitFor(() => PlayerProfile.Current.StepQuest.QuestId == 1, 10, "server accepted the tutorial");
                for (int k = 1; k <= q.Steps.Count; k++)
                {
                    yield return new WaitForSeconds(1.3f);
                    runner.ForceAdvance();
                    int want = k;
                    yield return WaitFor(() => PlayerProfile.Current.StepQuest.Step == want, 10, $"server step {k}");
                }
                Assert.AreEqual(1, PlayerProfile.Current.StepQuest.Counter, "every step done (server copy)");
                yield return new WaitForSeconds(6f);   // three seconds a step at least
                long mito0 = PlayerProfile.Current.Mito;
                StepQuestRunner.Claim(q);
                yield return WaitFor(() => PlayerProfile.Current.StepQuestsDone.Contains(1), 10, "server paid the quest");
                Assert.AreEqual(mito0 + 300, PlayerProfile.Current.Mito);
                Assert.AreEqual(1, PlayerProfile.Current.Count("i_n_start01"));
                Assert.AreEqual(0, PlayerProfile.Current.StepQuest.QuestId);

                // QSRANKLIST through the server
                Dictionary<string, object> rank = null;
                OnlineSession.Instance.Net.Request("qs.rank", new Dictionary<string, object> { ["job"] = "a_00041", ["period"] = 0 }, m => rank = m);
                yield return WaitFor(() => rank != null, 10, "qs.rank reply");
                Assert.IsTrue(rank.B("ok"));
                yield return new WaitForSeconds(0.3f);
                Shot("quests_online.png");
            }
            finally
            {
                if (OnlineSession.Instance) OnlineSession.Instance.Logout();
                try { if (!server.HasExited) server.Kill(); } catch { }
            }
        }
    }
}
