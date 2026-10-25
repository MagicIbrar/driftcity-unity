using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
    /// Round 4 (DriftCity_RE/PVP.md 15, EVENTS.md 12, SCREENS.md 8, SOCIAL.md 9), offline in Cras: a mission-battle room
    /// (GM "/missionbattle 1 1" = UFO) raced with its objects, a collected object and the t_ob_event_04 ink hit; the gate
    /// effects on the player's car; the Real Match skin's lamps and flames; TOUR_REWARD's GM panel; a Channel Battle ghost
    /// ticket; the Pick'em reels; the event art stand-ins and 3rd_anniversary_fin; AEROSET; AUTOFINDER; the classic option
    /// pages; the basic bar's friend / note windows; the crew mark fit. Writes Preview/r4_*.png.
    /// </summary>
    public class Round4Tests
    {
        string tempDir;
        float savedIntro;

        [SetUp]
        public void SetUp()
        {
            tempDir = Path.Combine(Path.GetTempPath(), "driftcity_r4_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            PlayerProfile.SaveDirOverride = tempDir;
            PlayerProfile.Slot = 0;
            PlayerProfile.Current = null;
            Directory.CreateDirectory("Preview");
            savedIntro = RaceMission.IntroSeconds;
            PvpReplay.SaveFiles = false;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            PvpMissionBattle.Mode = 0;
            PvpMissionBattle.End();
            PvpTourRewardGm.ForceGm = false;
            PvpTourRewardGm.Reset();
            PickEm.Field = null;
            PickEm.Reset();
            OriginalMenus.Close();
            BasicBarSocial.CloseAll();
            PvpResults.Close();
            PvpRewardPick.Close(false);
            PvpWaitRoom.Reset();
            PvpLobby.Close();
            Tournament.CloseRewards();
            RaceMission.IntroSeconds = savedIntro;
            PvpReplay.SaveFiles = true;
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
            Debug.Log("Drift City r4: wrote Preview/" + name);
        }

        static GameFlow NewGame()
        {
            var p = new PlayerProfile { Name = "Racer", Level = 30, Mito = 200000 };
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
        public IEnumerator OfflineRound4()
        {
            var flow = NewGame();
            yield return FrontEndSteps.ToCharacterSelect(flow);
            yield return FrontEndSteps.EnterSelected();
            yield return WaitFor(() => flow.InCity && CitySession.Current, 600, "city");
            yield return new WaitForSeconds(1f);
            var session = CitySession.Current;
            var ui = UiManager.Instance;
            var p = PlayerProfile.Current;
            var car = flow.PlayerCar;

            // ---- GM "/missionbattle 1 1": MissionBattle On(UFO); the next room is a mission-battle room ----
            Assert.IsTrue(PvpMissionBattle.ChatCommand("/missionbattle 1 1"));
            Assert.AreEqual(PvpMissionBattle.Ufo, PvpMissionBattle.Mode);
            Assert.AreEqual("MissionBattle On(UFO)", PvpMissionBattle.LastText);
            Assert.IsFalse(PvpMissionBattle.ChatCommand("/mission"), "other lines go to the chat");
            PvpMissionBattle.ChatCommand("/missionbattle 0 0");
            Assert.AreEqual(0, PvpMissionBattle.Mode, "MissionBattle Off");
            PvpMissionBattle.ChatCommand("/missionbattle 1 9");
            Assert.AreEqual(PvpMissionBattle.On, PvpMissionBattle.Mode, "plain On: the event objects with the ink object");

            RaceMission.IntroSeconds = 8f;
            PvpLobby.Open(session);
            PvpLobby.Create(new PvpRoom { Title = "Mission", Max = 8, Arena = 0, Mission = PvpMissionBattle.Mode });   // an EXP arena: AI drivers join
            yield return null;
            Assert.IsTrue(PvpWaitRoom.IsOpen && PvpWaitRoom.Room.Mission > 0, "mission-battle room");
            Assert.IsTrue(PvpWaitRoom.View.Control("ID_PVP_WAIT16_MISSIONBATTLE_TEXT").gameObject.activeSelf, "11479 shown");
            Shot("r4_1_mission_waitroom.png");
            PvpWaitRoom.View.Click("ID_PVP_WAIT16_START");
            yield return null;
            var race = RaceMission.Active;
            Assert.IsTrue(race, "race started");
            var fixedStats = PvpMissionBattle.FixedStats;
            yield return WaitFor(() => PvpMissionBattle.Run, 20, "GO: the mission objects");
            var run = PvpMissionBattle.Run;
            Assert.Greater(run.Objects.Count, 5, "objects along the line");
            Assert.IsTrue(run.Objects.Any(o => o.Kind == 4), "ink objects (t_ob_event_03) in the plain set");
            // collect one, hit an ink object
            car.BoostGauge = 0f;
            run.Hit(run.Objects.First(o => o.Kind != 4));
            Assert.AreEqual(1, PvpMissionBattle.Collected);
            Assert.AreEqual(0.25f, car.BoostGauge, 0.01f, "+25 % booster");
            var ink = run.Objects.First(o => o.Kind == 4);
            run.Hit(ink);
            Assert.AreEqual(1, PvpMissionBattle.Hits);
            Assert.IsTrue(ink.Down, "knocked over");
            Assert.IsTrue(GameEffects.ScreenPlaying("t_ob_event_04") || !GameEffects.Enabled, "the ink splats (UI effect 50)");
            yield return new WaitForSeconds(0.3f);
            Shot("r4_2_mission_ink.png");

            // ---- gate effects on the player's car (the race is running) ----
            car.BoostGauge = 0f;
            PvpGateEffects.Apply(car, PvpGateEffects.Charge100);
            Assert.AreEqual(1f, car.BoostGauge, 0.001f, "boostcharge100");
            car.BoostGauge = 0.2f;
            PvpGateEffects.Apply(car, PvpGateEffects.Charge50);
            Assert.AreEqual(0.7f, car.BoostGauge, 0.001f, "boostcharge50");
            var rival = Object.FindObjectsByType<AiDriver>(FindObjectsSortMode.None).First().gameObject;
            PvpGateEffects.Apply(car, PvpGateEffects.Untouchable, new[] { rival });
            var st = car.GetComponent<GateEffectState>();
            Assert.IsTrue(st && st.UntouchableLeft > 4f, "untouchable 5 s");
            var mine = car.GetComponents<BoxCollider>().First();
            var theirs = rival.GetComponents<BoxCollider>().First();
            Assert.IsTrue(Physics.GetIgnoreCollision(mine, theirs), "no collisions with the race cars");
            PvpGateEffects.Apply(car, PvpGateEffects.Booost);
            Assert.Greater(st.BooostLeft, 29f);
            int draws = Enumerable.Range(0, 100).Count(i => PvpGateEffects.Draw(1, 8, false, i / 100f) == PvpGateEffects.Null);
            Assert.AreEqual(60, draws, "the leader's 60 % nothing");
            Force(race, 1);
            yield return WaitFor(() => PvpResults.View || PvpRewardPick.IsOpen, 10, "results");
            if (PvpRewardPick.IsOpen) PvpRewardPick.Pick(5);
            yield return WaitFor(() => !RaceMission.Active, 10, "race over");
            Assert.IsFalse(PvpMissionBattle.Run, "objects removed");
            Assert.IsFalse(car.GetComponent<GateEffectState>(), "effects ended");
            if (rival && theirs) Assert.IsFalse(Physics.GetIgnoreCollision(mine, theirs), "collisions back");
            yield return WaitFor(() => PvpWaitRoom.IsOpen, 20, "wait room");
            PvpWaitRoom.View.Click("ID_PVP_WAIT16_QUIT");
            yield return null;
            PvpLobby.Close();
            PvpResults.Close();
            PvpMissionBattle.Set(0);

            // ---- Real Match skin: its lamps / flames follow the car, the hidden car's flames are off ----
            var skin = RealMatchSkin.Apply(car.gameObject, 3);
            if (skin)
            {
                yield return null;
                if (skin.HostFlames) Assert.IsTrue(skin.HostFlames.Flames.All(f => !f || !f.activeSelf) && !skin.HostFlames.enabled, "the hidden car's flames and particles off");
                Assert.IsFalse(skin.FlamesOn, "no flame while not boosting");
                Assert.IsFalse(skin.BrakeLampOn);
                RealMatchSkin.Remove(car.gameObject);
                yield return null;
                var host = car.GetComponent<BoosterFlames>();
                if (host) Assert.IsTrue(host.enabled, "the car's own flames back");
            }
            else Debug.Log("Drift City r4: no Real Match car in the data, skin checks skipped");

            // ---- TOUR_REWARD GM panel ----
            Tournament.OpenRewards(1);
            yield return null;
            var rw = Tournament.RewardView;
            Assert.IsTrue(rw);
            Assert.IsFalse(rw.Control("ID_TOUR_REWARD_GM_APPLY").gameObject.activeSelf, "hidden for players");
            PvpTourRewardGm.ForceGm = true;
            Tournament.OpenRewards(1);
            yield return null;
            Assert.IsTrue(rw.Control("ID_TOUR_REWARD_GM_APPLY").gameObject.activeSelf, "the GM panel");
            rw.Click("ID_TOUR_REWARD_GM_RIGHTBTN");
            rw.Click("ID_TOUR_REWARD_GM_RIGHTBTN");
            rw.Click("ID_TOUR_REWARD_GM_APPLY");
            Assert.AreEqual("c", PvpTourRewardGm.Letter(1), "Touring B pays reward table c");
            Assert.AreEqual("a", PvpTourRewardGm.Letter(3));
            yield return null;
            Shot("r4_3_tour_reward_gm.png");
            Tournament.CloseRewards();

            // ---- Channel Battle ghost ticket ----
            PvpGhost.BeginRace(false);
            PvpGhost.SetChannelTicket(0);
            Assert.AreEqual("ch_ghost_01", PvpGhost.ChannelTicket);
            Assert.AreEqual(PvpGhost.HasPass(p) , PvpGhost.Allowed, "no ticket yet");
            p.Give("ch_ghost_01", 1);
            Assert.IsTrue(PvpGhost.Allowed, "the channel's ticket allows the ghost");

            // ---- Pick'em reels: spinning before half race, landing on the picks' symbols ----
            float lead = 0.1f;
            var names = new[] { "Racer", "Rio", "Duke", "Mika", "Jin" };
            PickEm.Field = () => names.Select((n, i) => new PvpSpectate.Driver { Name = n, Progress = lead - i * 0.02f, Place = i + 1 }).ToList();
            p.Give(PickEm.Ticket, 1);
            yield return WaitFor(() => PickEm.PromptShown, 5, "the Pick'em prompt");
            PickEm.Accept();
            yield return WaitFor(() => PickEm.Active, 5, "Pick'em started");
            yield return new WaitForSeconds(0.4f);
            Assert.IsTrue(PickEm.ReelSpinning(0), "the reels run before half race");
            Shot("r4_4_pickem_spin.png");
            lead = 0.6f;
            yield return new WaitForSeconds(2.5f);
            for (int k = 0; k < 3; k++)
            {
                int row = names.ToList().IndexOf(PickEm.Picks[k]);
                Assert.IsFalse(PickEm.ReelSpinning(k), "reel stopped " + k);
                Assert.AreEqual(PickEm.Symbols[row], PickEm.ReelShows(k), "reel " + k + " shows its pick's symbol");
            }
            Shot("r4_5_pickem_stopped.png");
            PickEm.Field = null;
            yield return null;

            // ---- event art stand-ins (THANKSGIVING) and 3rd_anniversary_fin ----
            HarvestScreens.OpenThanks();
            yield return null;
            Assert.IsTrue(HarvestScreens.ThanksView, "THANKSGIVING open");
            Debug.Log($"Drift City r4: art stand-ins in 4680000: {string.Join(", ", EventArtFallback.Replaced(4680000))}");
            Shot("r4_6_thanksgiving_fallback.png");
            HarvestScreens.CloseThanks();
            Assert.AreEqual("Exchage", EventArtFallback.Label("ID_THANKSGIVING_EXCHAGE"));
            Assert.IsNotNull(GameEffects.Screen("3rd_anniversary_fin"), "UI effect 13");
            yield return new WaitForSeconds(0.5f);
            Shot("r4_7_anniversary.png");

            // ---- AEROSET 3900000 ----
            var aeroCar = new OwnedCar { VehicleIndex = Enumerable.Range(0, 400).First(AeroSet.CarTakesAeroSet), Aero = 1 };
            p.Cars.Add(aeroCar);
            AeroSet.Open(aeroCar);
            yield return null;
            Assert.IsTrue(AeroSet.View, "AEROSET open");
            Assert.AreEqual(AeroSet.Budget, AeroSet.Free);
            AeroSet.View.Click("ID_AEROSET_OPTION1_UP"); AeroSet.View.Click("ID_AEROSET_OPTION1_UP"); AeroSet.View.Click("ID_AEROSET_OPTION2_UP");
            Assert.AreEqual(AeroSet.Budget - 3, AeroSet.Free);
            AeroSet.Ok(true);
            Assert.AreEqual(MissionText.Get("11014"), AeroSet.LastText, "points left");
            UiPopup.BringToFront();
            var popups = ui.OpenViews.Where(v => v && v.Dialog.Id == 3250000).ToList(); foreach (var v in popups) ui.Close(v);
            AeroSet.Up(2); AeroSet.Up(3); AeroSet.Up(3);
            Assert.AreEqual(0, AeroSet.Free);
            AeroSet.Up(0);   // none left: the stat's points go back (the client's rule)
            Assert.AreEqual(0, AeroSet.Points[0]);
            AeroSet.Up(0); AeroSet.Up(0);
            Shot("r4_8_aeroset.png");
            AeroSet.Ok(true);
            Assert.AreEqual(new[] { 2, 1, 1, 2 }, aeroCar.AeroPoints, "saved");
            Assert.AreEqual((2, 1, 1, 2), AeroSet.Bonus(aeroCar));
            Assert.IsFalse(AeroSet.View, "closed");

            // ---- AUTOFINDER 3420000 (what the server asks) ----
            AutoFinder.Ask(7, "What is the fuel in Drift City called?", new List<string> { "Mittron", "Gas", "Diesel", "Nitro" });
            yield return null;
            Assert.IsTrue(AutoFinder.View, "AUTOFINDER open");
            var shown = Enumerable.Range(0, 4).Select(k => AutoFinder.Order[k]).ToList();
            CollectionAssert.AreEquivalent(new[] { 0, 1, 2, 3 }, shown, "the answers shuffled over the buttons");
            Shot("r4_9_autofinder.png");
            AutoFinder.Pick(0);
            Assert.IsFalse(AutoFinder.View);

            // ---- classic OPTION 2980000 / VIDEOADV 2990000 with the basic main UI ----
            int ui0 = GameOptions.MainUIType;
            GameOptions.MainUIType = 0;
            OriginalMenus.Options();
            yield return null;
            Assert.IsTrue(OriginalMenus.ClassicView, "classic options");
            OriginalMenus.ClassicTab(0);
            Assert.IsTrue(OriginalMenus.ClassicView.Control("ID_OPTION_BTN_VIDEOADV").gameObject.activeSelf);
            Assert.IsFalse(OriginalMenus.ClassicView.Control("ID_OPTION_SLIDE_BGM").gameObject.activeSelf, "audio rows hidden on VIDEO");
            Shot("r4_10_classic_video.png");
            OriginalMenus.ClassicTab(1);
            Assert.IsTrue(OriginalMenus.ClassicView.Control("ID_OPTION_SLIDE_BGM").gameObject.activeSelf);
            Shot("r4_11_classic_audio.png");
            OriginalMenus.ClassicTab(3);
            Assert.IsTrue(OriginalMenus.ClassicView.Control("ID_TAB_GAME_CB_DENYPARTY").gameObject.activeSelf, "the window's own game rows");
            Assert.IsFalse(OriginalMenus.ClassicView.Control("ID_OPTION_SLIDE_BGM").gameObject.activeSelf);
            Shot("r4_11b_classic_game.png");
            OriginalMenus.ClassicTab(0);
            OriginalMenus.OpenClassicAdv();
            yield return null;
            Assert.IsTrue(OriginalMenus.ClassicVideoAdvView, "VIDEOADV");
            Assert.IsTrue(OriginalMenus.ClassicVideoAdvView.Control("ID_VIDEOADV_SLIDE_CAR").gameObject.activeSelf);
            Assert.IsFalse(OriginalMenus.ClassicVideoAdvView.Control("ID_VIDEOADV_CB_CARSHADOW_REAL").gameObject.activeSelf);
            Shot("r4_12_videoadv.png");
            OriginalMenus.ClassicAdvTab(1);
            Assert.IsTrue(OriginalMenus.ClassicVideoAdvView.Control("ID_VIDEOADV_CB_CARSHADOW_REAL").gameObject.activeSelf);
            Shot("r4_12b_videoadv_screen.png");
            OriginalMenus.Close();
            GameOptions.MainUIType = ui0;
            OriginalMenus.Options();
            yield return null;
            Assert.IsFalse(OriginalMenus.ClassicView, "ADV UI: the remaster's pages");
            OriginalMenus.Close();

            // ---- the basic bar's friend list and notes ----
            BasicBarSocial.OpenFriends(2);
            yield return null;
            Assert.IsTrue(BasicBarSocial.ListView, "MYFRIEND_LIST 2930000");
            Assert.Greater(BasicBarSocial.Rows_.Count, 5, "drivers on the user tab");
            Shot("r4_13_basic_friends.png");
            BasicBarSocial.OpenInbox();
            yield return new WaitForSeconds(0.2f);
            Assert.IsTrue(BasicBarSocial.BoxView, "MYFRIEND_MSGBOX 2960000");
            if (Mail.Inbox.Count > 0)
            {
                BasicBarSocial.OpenNote(Mail.Inbox[0]);
                yield return null;
                Assert.IsTrue(BasicBarSocial.ReadView, "MSGREAD 2950000");
            }
            Shot("r4_14_basic_inbox.png");
            BasicBarSocial.CloseAll();

            // ---- crew mark fit (0x6107c0: 32x32, 4000 bytes) ----
            var src = new Texture2D(100, 60);
            var px = Enumerable.Range(0, 6000).Select(i => new Color32((byte)(i % 255), (byte)(i / 30), 80, 255)).ToArray();
            src.SetPixels32(px); src.Apply();
            var png = CrewMarkUpload.Fit(src.EncodeToPNG());
            Assert.IsNotNull(png, CrewMarkUpload.LastError);
            Assert.LessOrEqual(png.Length, CrewMarkUpload.MaxBytes);
            var back = new Texture2D(2, 2); back.LoadImage(png);
            Assert.AreEqual(32, back.width); Assert.AreEqual(32, back.height);
        }
    }
}
