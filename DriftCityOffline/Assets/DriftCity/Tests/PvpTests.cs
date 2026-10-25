using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
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
    /// Battle Zone: offline lobby (chat) -> create room -> wait room (AI slots, map browsing, emoticons, invite) -> race vs
    /// AI (pvpready / pvpstart cameras, countdown, PvP minimap, gates) -> reward pick -> results -> back to the wait room,
    /// find a friend, spectating an AI driver's room (VIEWING), joining an AI room as a guest; a wager room and its result
    /// screen, a Real Match room, the lap-time battle, the ladder and the GT Championship with its reward table; and online
    /// against the local server with race bots (tools/bot.js --race / --host): gates, the reward pick, room chat,
    /// spectating. Writes Preview/pvp_*.png.
    /// </summary>
    public class PvpTests
    {
        string tempDir;
        Process server, bot, host;
        const int Port = 7872;
        static string Url => $"ws://127.0.0.1:{Port}";
        static string ServerDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "DriftCityServer"));
        float savedIntro;

        [SetUp]
        public void SetUp()
        {
            tempDir = Path.Combine(Path.GetTempPath(), "driftcity_pvp_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            PlayerProfile.SaveDirOverride = tempDir;
            PlayerProfile.Slot = 0;
            PlayerProfile.Current = null;
            Directory.CreateDirectory("Preview");
            Tournament.Reset();
            savedIntro = RaceMission.IntroSeconds;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (OnlineSession.Instance) OnlineSession.Instance.Logout();
            foreach (var p in new[] { bot, host, server }) try { if (p != null && !p.HasExited) p.Kill(); } catch { }
            PvpResults.Close();
            PvpRewardPick.Close(false);
            PvpWaitRoom.Reset();
            PvpLobby.Close();
            LapTimeBattle.Reset();
            PvpSpectate.EndViewing();
            Tournament.Reset();
            Pvp.ForceRealMatch = false;
            RaceMission.IntroSeconds = savedIntro;
            PlayerProfile.SaveDirOverride = null;
            foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Exclude).Where(g => g.transform.parent == null)) Object.Destroy(go);
            yield return new WaitForSecondsRealtime(0.5f);
            try { Directory.Delete(tempDir, true); } catch { }
        }

        static IEnumerator WaitFor(System.Func<bool> condition, float timeout, string what)
        {
            float t = 0;
            while (!condition() && t < timeout) { t += Time.unscaledDeltaTime; yield return null; }
            Assert.IsTrue(condition(), "timed out waiting for " + what);
        }

        static void Shot(string name)
        {
            var tex = UiManager.Instance.Capture(Camera.main);
            File.WriteAllBytes(Path.Combine("Preview", name), tex.EncodeToPNG());
            Debug.Log("Drift City pvp: wrote Preview/" + name);
        }

        static IEnumerator ToCity(GameFlow flow)
        {
            yield return FrontEndSteps.ToCharacterSelect(flow);
            yield return FrontEndSteps.EnterSelected();
            yield return WaitFor(() => flow.InCity && CitySession.Current, 600, "city");
            yield return new WaitForSeconds(1f);
        }

        static GameFlow NewGame(long mito = 100000, int level = 12)
        {
            var p = new PlayerProfile { Name = "Racer", Level = level, Mito = mito };
            p.Cars.Add(new OwnedCar { VehicleIndex = 0, Color = 1 });
            p.Save();
            new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            var flow = new GameObject("Game").AddComponent<GameFlow>();
            flow.SkipParkingLot = true; flow.SkipIntro = true;
            return flow;
        }

        static void Force(object race, int place) =>
            typeof(RaceMission).GetMethod("ForceFinish", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(race, new object[] { place });

        [UnityTest]
        public IEnumerator OfflineRoomRacePickSpectate()
        {
            var flow = NewGame();
            yield return ToCity(flow);
            var session = CitySession.Current;
            var ui = UiManager.Instance;

            // ---- lobby (T key / Battle Zone button) and its chat ----
            RaceMission.Offer(session);
            yield return null;
            var lobby = ui.Find(1000000);
            Assert.IsTrue(lobby, "RMLOBBY open");
            Assert.IsTrue(flow.PlayerCar.ExternalControl, "the car waits while the Battle Zone is up");
            int arena = PvpLobby.Arena;
            Debug.Log($"Drift City pvp: arena {arena}, {PvpLobby.Rooms.Count} rooms, car grade {Pvp.CarGrade()}");
            Assert.IsTrue(Pvp.ArenaOpen(arena, Pvp.CarGrade(), 12), "selected arena is open");
            Assert.Greater(PvpLobby.Rooms.Count, 0, "AI drivers' rooms listed");
            Assert.IsTrue(lobby.Control("ID_PVP_RMLOBBY_TITLE_A").gameObject.activeSelf, "first row filled");
            PvpChat.Send("hello everyone");
            Assert.IsTrue(PvpChat.History.Contains("[Racer]: hello everyone"), "lobby chat line");
            Assert.IsTrue(lobby.Control("ID_PVP_RMLOBBY_LAPTIMEBATTLE").Interactable, "lap-time battle offered in a city with a lap track");
            yield return new WaitForSeconds(0.3f);
            Shot("pvp_1_lobby.png");

            // ---- create room ----
            lobby.Click("ID_PVP_RMLOBBY_CREATEROOM");
            yield return null;
            var create = ui.Find(1020000);
            Assert.IsTrue(create, "CREATEROOM open");
            Assert.IsNotEmpty(create.GetInput("ID_PVP_CREATEROOM_TITLE"), "random title");
            create.Control("ID_PVP_CREATEROOM_TITLE").Input.text = "Test Room";
            yield return null;
            Shot("pvp_2_create.png");
            create.Click("ID_PVP_CREATEROOM_OK");
            yield return null;
            Assert.IsTrue(PvpWaitRoom.IsOpen, "wait room open");
            Assert.IsFalse(ui.Find(1000000), "lobby closed");
            var room = PvpWaitRoom.Room;
            Assert.AreEqual("Test Room", room.Title);
            Assert.IsTrue(room.IAmHost);
            Assert.AreEqual(4, room.Members.Count, "me + three AI drivers");
            var wait = PvpWaitRoom.View;
            Assert.IsTrue(wait.Control("ID_PVP_WAIT16_START").gameObject.activeSelf, "host sees START");
            Assert.IsFalse(wait.Control("ID_PVP_WAIT16_READY").gameObject.activeSelf, "host has no READY");
            Assert.IsTrue(room.CanStart, "AI drivers are ready");
            Assert.IsTrue(wait.Control("ID_PVP_WAIT16_OBSERVE_CNT").gameObject.activeSelf, "spectator count");

            // host slot buttons: add an AI driver to an open slot, remove it, close / reopen a slot
            int open = room.OpenSlot();
            wait.Click($"ID_PVP_WAIT16_AI_BTN{PvpWaitRoom.Btn(open):00}");
            Assert.AreEqual(5, room.Members.Count, "AI added");
            wait.Click($"ID_PVP_WAIT16_AI_CLOSED{PvpWaitRoom.Btn(open):00}");
            Assert.AreEqual(4, room.Members.Count, "AI removed");
            wait.Click($"ID_PVP_WAIT16_LOCK_BTN{PvpWaitRoom.Btn(open):00}");
            Assert.AreEqual(-1, room.Slots[open], "slot closed");
            wait.Click($"ID_PVP_WAIT16_CLOSED{open:00}");
            Assert.AreEqual(0, room.Slots[open], "slot reopened");
            // map browsing
            var maps = Pvp.MapList(session.City, room.MapFlags, room.Arena);
            var first = room.Track;
            wait.Click("ID_PVP_WAIT16_MAP_RIGHT");
            if (maps.Count > 1) Assert.AreNotEqual(first, room.Track, "MAP_RIGHT picks the next track");
            wait.Click("ID_PVP_WAIT16_TRAFFIC_IDLE");
            Assert.AreEqual(Pvp.TrafficIdle, room.MapFlags & (Pvp.TrafficIdle | Pvp.TrafficNormal | Pvp.TrafficJam));
            // room chat and an emoticon over my slot (1130000)
            PvpChat.Send("/Hi");
            Assert.AreEqual(1, PvpWaitRoom.EmoticonsShown, "emoticon over the slot");
            PvpChat.Send("good luck");
            Assert.IsTrue(PvpChat.History.Contains("[Racer]: good luck"));
            yield return new WaitForSeconds(0.3f);
            Shot("pvp_3_waitroom.png");
            // INVITE: FRIENDINVITE 1840000
            wait.Click("ID_PVP_WAIT16_INVITE");
            yield return new WaitForSeconds(0.3f);
            Assert.IsTrue(ui.Find(1840000), "FRIENDINVITE open");
            Shot("pvp_3b_invite.png");
            ui.Find(1840000).Click("ID_FRIENDINVITE_CLOSE");
            yield return null;

            // ---- race: pvpready, pvpstart, countdown ----
            long mitoBefore = PlayerProfile.Current.Mito;
            int ladderBefore = PlayerProfile.Current.LadderPoints;
            wait.Click("ID_PVP_WAIT16_START");
            yield return null;
            var race = RaceMission.Active;
            Assert.IsTrue(race, "race started");
            Assert.IsFalse(PvpWaitRoom.IsOpen, "wait room steps aside");
            Assert.IsTrue(ui.Find(1380000) && ui.Find(1390000) && ui.Find(1400000) && ui.Find(1410000), "PvP HUD dialogs");
            Assert.IsTrue(ui.Find(1170000), "PvP minimap 1170000");
            Assert.IsTrue(race.Gates != null && race.Gates.Usable, "the track's checkpoint gates");
            var rig = PvpCameraRig.Instance;
            Assert.IsTrue(rig && rig.Current == PvpCameraRig.Mode.Ready, "pvpready camera");
            yield return new WaitForSeconds(1.2f);
            Shot("pvp_4_ready.png");
            yield return WaitFor(() => rig.Current != PvpCameraRig.Mode.Ready, RaceMission.IntroSeconds, "pvpstart");
            bool startCam = rig.Current == PvpCameraRig.Mode.Start;
            Debug.Log($"Drift City pvp: pvpstart camera {(startCam ? "playing" : "not available")} for {race.Track.Dir}");
            yield return new WaitForSeconds(2f);
            Shot("pvp_5_start.png");
            yield return WaitFor(() => rig.Current == PvpCameraRig.Mode.None, RaceMission.IntroSeconds, "countdown");
            yield return new WaitForSeconds(1f);
            Shot("pvp_5b_countdown.png");
            var ai = Object.FindObjectsByType<AiDriver>(FindObjectsInactive.Exclude);
            Assert.AreEqual(3, ai.Length, "three AI cars on the grid");
            var aiStart = ai[0].transform.position;
            yield return WaitFor(() => !flow.PlayerCar.ExternalControl, 10, "GO");
            yield return new WaitForSeconds(4f);
            float moved = Vector3.Distance(aiStart, ai[0].transform.position);
            Debug.Log($"Drift City pvp: AI moved {moved:F1} m");
            Assert.Greater(moved, 15f, "AI drives after GO");
            Shot("pvp_6_race_hud.png");

            // the gate counter: the next gate counts when the car crosses its line within 150 m
            var t = race.Track;
            var gt = new GateTracker(t);
            var g0 = t.Gates[0];
            var normal = Vector3.Cross(Vector3.up, (g0.B - g0.A).normalized);
            Assert.IsFalse(gt.Update(g0.Centre - normal * 4f));
            Assert.IsTrue(gt.Update(g0.Centre + normal * 4f), "gate 1 crossed");
            Assert.AreEqual(1, gt.Count);
            Assert.IsFalse(gt.Update(g0.Centre - normal * 4f), "the same gate does not count twice");
            Assert.IsFalse(new GateTracker(t).Update(g0.Centre + normal * 400f), "far from the gate");

            // ---- finish -> reward pick 4310000 -> results ----
            Force(race, 1);
            bool pick = Pvp.LadderCounts(room, 4) || (room.Arena >= 0 && room.Arena <= 4);
            if (pick)
            {
                yield return WaitFor(() => PvpRewardPick.IsOpen, 5, "reward pick");
                yield return new WaitForSeconds(0.5f);
                Shot("pvp_7_pick.png");
                PvpRewardPick.Pick(5);
                Assert.AreEqual(5, PvpRewardPick.MyPick);
                yield return WaitFor(() => PvpRewardPick.AllShown, 12, "every card turned over");
                Assert.IsTrue(PvpRewardPick.Shown.Count(c => c.Picker.Length > 0) >= 4, "the AI drivers picked too");
                yield return new WaitForSeconds(0.3f);
                Shot("pvp_7b_pick_shown.png");
            }
            yield return WaitFor(() => PvpResults.View, 12, "results screen");
            yield return new WaitForSeconds(0.3f);
            Shot("pvp_8_results.png");
            var res = PvpResults.Current;
            Assert.AreEqual(4, res.Entries.Count);
            Assert.AreEqual(1, res.Mine.Place, "player won");
            Assert.Greater(res.Mine.Exp, 0, "reward shown");
            Assert.Greater(PlayerProfile.Current.Mito, mitoBefore, "reward paid");
            if (Pvp.LadderCounts(room, 4)) Assert.AreEqual(ladderBefore + 10, PlayerProfile.Current.LadderPoints, "ladder points for the win");
            Assert.IsTrue(PvpResults.View.Dialog.Id == 1470000, "single race: RESULT16");
            yield return WaitFor(() => PvpWaitRoom.IsOpen, 15, "back to the wait room");
            Assert.AreSame(room, PvpWaitRoom.Room);
            Assert.IsFalse(RaceMission.Active);

            // ---- quit to the lobby; find a friend ----
            PvpWaitRoom.View.Click("ID_PVP_WAIT16_QUIT");
            yield return null;
            lobby = ui.Find(1000000);
            Assert.IsTrue(lobby, "lobby again");
            lobby.Click("ID_PVP_RMLOBBY_FINDFRIENDS");
            yield return new WaitForSeconds(0.3f);
            Assert.IsTrue(ui.Find(1080000), "FINDFRIEND open");
            Shot("pvp_9_findfriend.png");
            ui.Find(1080000).Click("ID_PVP_FINDFRIEND_CANCEL");
            yield return null;

            // ---- spectate an AI driver's room: observer popup, VIEWING during the race ----
            RaceMission.IntroSeconds = 8f;
            var aiRoom = PvpLobby.Rooms.FirstOrDefault(r => r.Waiting && !r.HasPassword && r.Host != null && r.Host.Ai && r.Spectate);
            Assert.IsNotNull(aiRoom, "an AI driver's room");
            PvpSpectate.Observe(aiRoom);
            yield return null;
            Assert.IsTrue(PvpWaitRoom.IsOpen && PvpWaitRoom.Room.Spectating, "spectating the AI room");
            Assert.IsTrue(PvpSpectate.PopupView, "observer popup 1990000");
            Assert.IsTrue(PvpWaitRoom.View.Control("ID_PVP_WAIT16_JOINGAME").gameObject.activeSelf, "JOINGAME for a spectator");
            Assert.IsFalse(PvpWaitRoom.View.Control("ID_PVP_WAIT16_READY").gameObject.activeSelf, "no READY for a spectator");
            yield return new WaitForSeconds(0.3f);
            Shot("pvp_10_spectator_wait.png");
            yield return WaitFor(() => RaceMission.Active, 10, "the AI host starts");
            Assert.IsTrue(RaceMission.Active.Spectator, "watched race");
            Assert.IsTrue(PvpSpectate.ViewingView, "VIEWING 1960000");
            yield return WaitFor(() => RaceMission.Active && PvpCameraRig.Instance.Current == PvpCameraRig.Mode.Watch, 12, "watch camera after the countdown");
            yield return new WaitForSeconds(3f);
            Shot("pvp_11_viewing.png");
            PvpSpectate.Cycle(1);
            Assert.AreEqual(1, PvpSpectate.Selected, "next driver");
            PvpCameraRig.Instance.SetView(1);
            yield return new WaitForSeconds(1f);
            Shot("pvp_11b_viewing_tpview.png");
            Force(RaceMission.Active, 0);
            yield return WaitFor(() => PvpResults.View, 8, "spectator results");
            Assert.IsNull(PvpResults.Current.Mine, "a spectator has no row of its own");
            Assert.IsFalse(PvpRewardPick.IsOpen, "no reward pick for spectators");
            yield return WaitFor(() => PvpWaitRoom.IsOpen, 16, "back to the watched room");
            Assert.IsTrue(PvpWaitRoom.Room.Spectating);
            PvpSpectate.JoinGame();
            Assert.IsFalse(PvpWaitRoom.Room.Spectating, "JOINGAME: a player now");
            Assert.IsNotNull(PvpWaitRoom.Room.Me);
            yield return new WaitForSeconds(1.1f);
            PvpWaitRoom.View.Click("ID_PVP_WAIT16_READY");
            Assert.IsTrue(PvpWaitRoom.Room.Me.Ready);
            yield return WaitFor(() => RaceMission.Active, 6, "the AI host starts with me in it");
            Assert.IsFalse(RaceMission.Active.Spectator);
            RaceMission.Active.Abort();
            yield return null;
            PvpWaitRoom.Reset();
            PvpLobby.Close();
        }

        [UnityTest]
        public IEnumerator OfflineWagerRealMatchLapTimeTournament()
        {
            var flow = NewGame(200000);
            yield return ToCity(flow);
            var session = CitySession.Current;
            var ui = UiManager.Instance;
            RaceMission.IntroSeconds = 7f;

            // ---- a wager room (grade B, 1000 Mito, needs 10000) ----
            RaceMission.Offer(session);
            yield return null;
            var lobby = ui.Find(1000000);
            lobby.Click("ID_PVP_RMLOBBY_CREATEROOM");
            yield return null;
            var create = ui.Find(1020000);
            create.Click("ID_PVP_CREATEROOM_GRADE_B");
            Assert.IsTrue(create.Control("ID_PVP_CREATEROOM_GRADEARROW_B").gameObject.activeSelf, "grade B chosen");
            yield return null;
            Shot("pvp_w1_create_wager.png");
            create.Click("ID_PVP_CREATEROOM_OK");
            yield return null;
            var room = PvpWaitRoom.Room;
            Assert.AreEqual(3, room.Wager, "wager grade B");
            Assert.AreEqual(4, room.Members.Count, "me and three wagering AI drivers");
            Assert.IsTrue(PvpWaitRoom.View.Control("ID_PVP_WAIT16_BATTINGTEXT").gameObject.activeSelf, "11077 entry fee line");
            yield return new WaitForSeconds(0.3f);
            Shot("pvp_w2_waitroom_wager.png");
            long before = PlayerProfile.Current.Mito;
            PvpWaitRoom.View.Click("ID_PVP_WAIT16_START");
            yield return null;
            Assert.IsTrue(RaceMission.Active);
            yield return WaitFor(() => !flow.PlayerCar.ExternalControl, RaceMission.IntroSeconds + 6, "GO");
            Force(RaceMission.Active, 1);
            yield return WaitFor(() => PvpResults.View, 8, "wager results");
            Assert.IsFalse(PvpRewardPick.IsOpen, "no reward pick in wager rooms");
            Assert.AreEqual(4080000, PvpResults.View.Dialog.Id, "RESULT_SINGLEBB");
            // 4 x 1000 = 4000, 10% tax -> 3600: the winner gets 60% + the 3rd place's... 3600 - 1080 - 360 = 2160
            Assert.AreEqual(1160, PvpResults.Current.Mine.WagerDelta, "the winner's Mito change");
            yield return new WaitForSeconds(0.5f);
            Shot("pvp_w3_wager_results.png");
            Assert.AreEqual(before + 1160 + PvpResults.Current.Mine.Mito, PlayerProfile.Current.Mito, "wager and race reward paid");
            yield return WaitFor(() => PvpWaitRoom.IsOpen, 15, "back to the wait room");
            PvpWaitRoom.View.Click("ID_PVP_WAIT16_QUIT");
            yield return null;

            // ---- Real Match: the RM arena in Real Match time, the RM car panel, the Duel's ratings in the race ----
            Pvp.ForceRealMatch = true;
            PvpLobby.Refresh();
            lobby = ui.Find(1000000);
            Assert.IsTrue(lobby.Control("ID_PVP_RMLOBBY_CHN_EXP_RM").Interactable, "RealMatch arena open in RM time");
            PvpLobby.SelectArena(Pvp.ArenaRealMatch);
            Assert.AreEqual(Pvp.ArenaRealMatch, PvpLobby.Arena);
            lobby.Click("ID_PVP_RMLOBBY_CREATEROOM");
            yield return null;
            ui.Find(1020000).Click("ID_PVP_CREATEROOM_OK");
            yield return null;
            Assert.IsTrue(PvpWaitRoom.Room.RealMatch);
            Assert.IsTrue(ui.Find(1140000), "RMCAR 1140000");
            Assert.AreEqual(4, PvpWaitRoom.Room.Members.Count, "AI rivals in an offline Real Match room");
            ui.Find(1140000).Click("ID_PVP_WAIT16_RMCAR_SHOWINFO");
            yield return new WaitForSeconds(0.3f);
            Assert.IsTrue(ui.Find(1150000), "RMCARINFO 1150000");
            Shot("pvp_rm1_waitroom.png");
            ui.Find(1150000).Click("ID_PVP_RMCARINFO_CLOSE");
            float ownTop = flow.PlayerCar.TopSpeedKmh;
            PvpWaitRoom.View.Click("ID_PVP_WAIT16_START");
            yield return null;
            Assert.IsTrue(RaceMission.Active, "RM race");
            var duel = Pvp.RealMatchStats();
            float rmTop = flow.PlayerCar.TopSpeedKmh;
            RaceMission.Active.Abort();
            yield return null;
            Assert.AreEqual(ownTop, flow.PlayerCar.TopSpeedKmh, 0.01f, "own ratings back after the race");
            flow.PlayerCar.ApplyStats(duel.accel, duel.speed, duel.crash, duel.boost);
            Assert.AreEqual(flow.PlayerCar.TopSpeedKmh, rmTop, 0.01f, "the race ran on the Real Match car's ratings");
            flow.ApplyCarStats();
            Pvp.ForceRealMatch = false;
            PvpWaitRoom.Reset();
            PvpLobby.Close();

            // ---- lap-time battle ----
            Assert.IsNotNull(LapTimeBattle.TrackFor(session.City), "the city's lap-time track");
            LapTimeBattle.Enter(session);
            yield return null;
            var lt = LapTimeBattle.Active;
            Assert.IsTrue(lt, "lap-time battle running");
            Assert.IsTrue(ui.Find(3440000), "LAPTIMEPLAY 3440000");
            Assert.Greater(lt.Track.Gates.Count, 5);
            Assert.AreEqual(5, lt.Laps);
            yield return new WaitForSeconds(LapTimeBattle.StartDelay + 1.5f);
            Assert.IsTrue(lt.Driving, "the run started after the countdown");
            Shot("pvp_lap1_hud.png");
            var m = typeof(LapTimeBattle).GetMethod("ForceLap", BindingFlags.NonPublic | BindingFlags.Instance);
            m.Invoke(lt, null);
            yield return null;
            m.Invoke(lt, null);
            yield return null;
            Assert.AreEqual(2, lt.MyLaps.Count);
            Assert.Greater(lt.Best, 0f);
            Assert.Greater(lt.Rank, 0, "on the best list");
            Assert.IsTrue(lt.NoticeView, "LAPTIMENOTICE on a record");
            yield return new WaitForSeconds(0.3f);
            Shot("pvp_lap2_records.png");
            typeof(LapTimeBattle).GetMethod("ForceRoundEnd", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(lt, null);
            yield return WaitFor(() => lt.ResultView, 3, "LAPTIMERET");
            yield return new WaitForSeconds(0.3f);
            Shot("pvp_lap3_results.png");
            lt.Leave();
            yield return null;
            Assert.IsFalse(LapTimeBattle.Active);
            Assert.IsFalse(flow.PlayerCar.ExternalControl);

            // ---- ladder ----
            Ladder.Open();
            yield return null;
            Assert.IsTrue(ui.Find(3930000), "LADDER open");
            Assert.IsTrue(Ladder.Shown.Any(r => r[1] == "Racer"), "the player is on the ladder");
            Shot("pvp_ladder.png");
            Ladder.Close();

            // ---- GT Championship and its reward table ----
            Tournament.Open(session);
            yield return null;
            Assert.IsTrue(ui.Find(1010000), "TOUR lobby open");
            Tournament.Enter(3);
            Assert.IsTrue(Tournament.Running);
            yield return null;
            Shot("pvp_tour1.png");
            Tournament.OpenRewards(3);
            yield return null;
            Assert.IsTrue(ui.Find(4510000), "TOUR_REWARD 4510000");
            Assert.IsNotEmpty(Tournament.RewardsFor(3, 1), "GT Master rank 1 rewards from rewardgroup.xlt");
            Shot("pvp_tour2_rewards.png");
            Tournament.CloseRewards();
            int itemsBefore = PlayerProfile.Current.Items.Sum(i => i.Count);
            for (int stage = 0; stage < 5 && Tournament.Running; stage++)
            {
                var entries = new PvpRaceResult { BestLap = 30f };
                entries.Entries.Add(new PvpResultEntry { Name = "Racer", Me = true, Place = 1, Time = 60f });
                int pl = 2;
                foreach (var e in Tournament.Field.Where(x => !x.Out && !x.Member.Me)) entries.Entries.Add(new PvpResultEntry { Name = e.Member.Name, Place = pl++, Time = 60f + pl });
                typeof(Tournament).GetField("heat", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null,
                    Tournament.Field.Where(x => !x.Out).OrderBy(x => x.Member.Me ? 0 : 1).Take(Tournament.Current == Tournament.Stage.Final ? 2 : 4).ToList());
                Tournament.OnRaceDone(entries);
                yield return null;
            }
            Assert.IsFalse(Tournament.Running, "championship over");
            Assert.AreEqual(4, PlayerProfile.Current.TourBest, "champion");
            Assert.Greater(PlayerProfile.Current.Items.Sum(i => i.Count), itemsBefore, "the class's rank 1 reward items");
            Shot("pvp_tour3_champion.png");
            Tournament.Close(false);
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
        public IEnumerator OnlineRoomRaceWithBot()
        {
            if (!File.Exists(Path.Combine(ServerDir, "server.js"))) Assert.Ignore("DriftCityServer not found next to the Unity project");
            server = Node("server.js", new Dictionary<string, string>
            {
                ["DC_PORT"] = Port.ToString(), ["DC_DB"] = Path.Combine(tempDir, "test.db"),
                ["DC_RACE_COUNTDOWN_MS"] = "2500", ["DC_RACE_MAX_SPEED"] = "100000", ["DC_RACE_PICK_MS"] = "4000", ["DC_RACE_AI_SPEED"] = "3000",
            });
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
            yield return FrontEndSteps.ToCharacterSelect(flow, Url, "pvptester", "secret123");
            yield return WaitFor(() => UiManager.Instance.Find(3250000), 15, "create-account popup");
            UiManager.Instance.Find(3250000).Click(UiPopup.Yes);
            yield return FrontEndSteps.NewCharacter("PvpTester", 1);
            UiManager.Instance.Find(150000).Click("ID_CREATECAR_NEW_NEXT");
            yield return WaitFor(() => UiManager.Instance.Find(320000), 600, "city HUD");
            yield return WaitFor(() => OnlineSession.Instance.Channel != null, 15, "channel join");
            var ui = UiManager.Instance;
            var session = CitySession.Current;
            bot = Node($"tools/bot.js 1 --url {Url} --name Rbot --race 2 --city {session.City}");

            RaceMission.Offer(session);
            yield return WaitFor(() => ui.Find(1000000), 5, "online lobby");
            // an earlier test may have left the lobby on the Real Match arena (no ladder): take the first EXP arena
            PvpLobby.SelectArena(Pvp.FirstOpenArena());
            int arena = PvpLobby.Arena;
            ui.Find(1000000).Click("ID_PVP_RMLOBBY_CREATEROOM");
            yield return null;
            ui.Find(1020000).Click("ID_PVP_CREATEROOM_OK");
            yield return WaitFor(() => PvpWaitRoom.IsOpen && PvpWaitRoom.Room != null && PvpWaitRoom.Room.Online, 10, "server room");
            Assert.IsTrue(PvpWaitRoom.Room.IAmHost, "host of the server room");
            yield return WaitFor(() => PvpWaitRoom.Room.Members.Count == 2 && PvpWaitRoom.Room.CanStart, 20, "bot joined and ready");
            // room chat through the server
            PvpChat.Send("hello bot");
            yield return WaitFor(() => PvpChat.History.Contains("[PvpTester]: hello bot"), 5, "room chat echo");
            yield return null;
            Shot("pvp_online_1_waitroom.png");
            int ladderBefore = PlayerProfile.Current.LadderPoints;
            PvpWaitRoom.View.Click("ID_PVP_WAIT16_START");
            yield return WaitFor(() => OnlineRace.Active, 10, "race.start");
            Assert.IsFalse(PvpWaitRoom.IsOpen);
            Assert.IsTrue(OnlineRace.Active.Gates != null && OnlineRace.Active.Gates.Usable, "gated race");
            Assert.IsTrue(ui.Find(1170000), "PvP minimap");
            yield return WaitFor(() => !flow.PlayerCar.ExternalControl, 10, "race.go");
            yield return new WaitForSeconds(1f);
            Shot("pvp_online_2_race.png");
            yield return new WaitForSeconds(2.5f);   // the bot finishes first
            typeof(OnlineRace).GetMethod("ForceFinish", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(OnlineRace.Active, null);
            bool pick = arena >= 0 && arena <= 4;
            if (pick)
            {
                yield return WaitFor(() => PvpRewardPick.IsOpen, 15, "reward pick");
                yield return new WaitForSeconds(0.3f);
                Shot("pvp_online_3_pick.png");
                PvpRewardPick.Pick(3);
                yield return WaitFor(() => PvpRewardPick.AllShown, 10, "the server turns every card over");
                Assert.AreEqual(3, PvpRewardPick.MyPick);
                Assert.AreEqual("PvpTester", PvpRewardPick.Shown[3].Picker);
            }
            yield return WaitFor(() => PvpResults.View, 15, "results screen");
            yield return new WaitForSeconds(0.3f);
            Shot("pvp_online_4_results.png");
            var res = PvpResults.Current;
            Assert.AreEqual(2, res.Entries.Count);
            Assert.AreEqual("Rbot", res.Entries[0].Name.StartsWith("Rbot") ? "Rbot" : res.Entries[0].Name, "the bot finished first");
            Assert.AreEqual(2, res.Mine.Place);
            Assert.Greater(res.Mine.Exp, 0, "server reward shown");
            yield return WaitFor(() => PvpWaitRoom.IsOpen, 16, "back to the wait room");
            Assert.AreEqual(2, PvpWaitRoom.Room.Members.Count, "both drivers still in the room");
            Assert.IsTrue(PvpWaitRoom.Room.Waiting);
            if (res.Mine.LadderShown) Assert.AreEqual(ladderBefore - 10, PlayerProfile.Current.LadderPoints, "server ladder points");
            Ladder.Open();
            yield return WaitFor(() => Ladder.Shown.Any(r => r[1] == "Rbot1"), 5, "server ladder rows");
            Shot("pvp_online_5_ladder.png");
            Ladder.Close();
            PvpWaitRoom.View.Click("ID_PVP_WAIT16_QUIT");
            yield return WaitFor(() => ui.Find(1000000), 5, "lobby after quit");

            // ---- spectating a bot's room (tools/bot.js --host): VIEWING, the server's AI driver, results ----
            var key = Pvp.TrackKey(Pvp.MapList(session.City, Pvp.MapCityAll, arena)[0]);
            host = Node($"tools/bot.js 1 --url {Url} --name Hbot --host 2 --city {session.City} --track {key}");
            yield return WaitFor(() => PvpLobby.Rooms.Any(r => r.Title.StartsWith("Hbot")), 15, "the bot's room in the lobby");
            var hroom = PvpLobby.Rooms.First(r => r.Title.StartsWith("Hbot"));
            Assert.IsTrue(hroom.Members.Any(x => x.Ai), "the server's AI driver in the room");
            PvpSpectate.Observe(hroom);
            yield return WaitFor(() => PvpWaitRoom.IsOpen && PvpWaitRoom.Room != null && PvpWaitRoom.Room.Spectating, 10, "spectating the bot's room");
            Assert.GreaterOrEqual(PvpWaitRoom.Room.Observers, 1);
            yield return new WaitForSeconds(0.3f);
            Shot("pvp_online_6_spectator_wait.png");
            yield return WaitFor(() => OnlineRace.Active, 15, "the bot starts");
            Assert.IsTrue(OnlineRace.Active.Spectator, "watching, not racing");
            Assert.IsTrue(PvpSpectate.ViewingView, "VIEWING 1960000");
            yield return new WaitForSeconds(3.5f);
            Shot("pvp_online_7_viewing.png");
            yield return WaitFor(() => PvpResults.View, 60, "spectator results");
            Assert.IsNull(PvpResults.Current.Mine);
            Assert.IsTrue(PvpResults.Current.Entries.Any(e => e.Ai), "the AI driver placed");
            yield return WaitFor(() => PvpWaitRoom.IsOpen, 16, "back to the watched room");
            Assert.IsTrue(PvpWaitRoom.Room.Spectating);
            PvpWaitRoom.View.Click("ID_PVP_WAIT16_QUIT");
            yield return WaitFor(() => ui.Find(1000000), 5, "lobby after leaving");

            // ---- the GT Championship online: the server's bracket (sign-up) ----
            ui.Find(1000000).Click("ID_PVP_RMLOBBY_CS_ENTER");
            yield return WaitFor(() => ui.Find(1010000), 5, "TOUR lobby");
            Tournament.Enter(3);
            yield return WaitFor(() => Tournament.OnlineState != null, 5, "the server's championship");
            Assert.AreEqual("signup", Tournament.OnlineState.S("state"));
            yield return new WaitForSeconds(0.3f);
            Shot("pvp_online_8_tour_signup.png");
            Tournament.Close(false);
        }
    }
}
