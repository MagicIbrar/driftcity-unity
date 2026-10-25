using System.Collections;
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
    /// PvP round 3 (DriftCity_RE/PVP.md section 14): an offline Channel Battle round is recorded, listed in the replay list
    /// 3410000 and played back in VIEWING; the Ghost Battle (GHOSTJOIN 2000000 / GHOSTEXIT 2010000) is joined from the
    /// replay and won; ROUNDNUMBER 1420000; the result bonus icons (EXPBONUS / TREEBONUS); the faction / arbeit / gate
    /// effect banners; the Oros dust devils' collision sphere with bind_hit; the Real Match looks (visual.xlt).
    /// Writes Preview/pvp3_*.png.
    /// </summary>
    public class PvpReplayTests
    {
        string tempDir;
        float savedIntro;

        [SetUp]
        public void SetUp()
        {
            tempDir = Path.Combine(Path.GetTempPath(), "driftcity_pvp3_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            PlayerProfile.SaveDirOverride = tempDir;
            PlayerProfile.Slot = 0;
            PlayerProfile.Current = null;
            Directory.CreateDirectory("Preview");
            savedIntro = RaceMission.IntroSeconds;
            PvpReplay.SaveFiles = false;
            PvpReplay.Local.Clear();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (PvpReplay.Current) PvpReplay.Current.Stop(false);
            PvpGhost.Exit(null, true);
            PvpGhost.ForceAllowed = false;
            GameEffects.RooOverride = null;
            PvpHud.HideRound();
            PvpResults.Close();
            PvpRewardPick.Close(false);
            PvpWaitRoom.Reset();
            PvpLobby.Close();
            PvpSpectate.EndViewing();
            RaceMission.IntroSeconds = savedIntro;
            PvpReplay.SaveFiles = true;
            PvpReplay.Local.Clear();
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
            Debug.Log("Drift City pvp3: wrote Preview/" + name);
        }

        static GameFlow NewGame()
        {
            var p = new PlayerProfile { Name = "Racer", Level = 12, Mito = 100000 };
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
        public IEnumerator OfflineReplayGhostRoundBonusEffects()
        {
            var flow = NewGame();
            yield return FrontEndSteps.ToCharacterSelect(flow);
            yield return FrontEndSteps.EnterSelected();
            yield return WaitFor(() => flow.InCity && CitySession.Current, 600, "city");
            yield return new WaitForSeconds(1f);
            var session = CitySession.Current;
            var ui = UiManager.Instance;
            var p = PlayerProfile.Current;

            // ---- an offline Channel Battle round (the stand-in team room) is recorded from GO ----
            RaceMission.IntroSeconds = 8f;
            PvpLobby.Open(session);
            PvpLobby.Create(new PvpRoom { Title = "Channel 1 Channel Battle", Team = true, Max = 8, Arena = PvpLobby.Arena });
            yield return null;
            Assert.IsTrue(PvpWaitRoom.IsOpen && PvpWaitRoom.Room.IsChannelBattle, "the stand-in crew room");
            PvpWaitRoom.View.Click("ID_PVP_WAIT16_START");
            yield return null;
            var race = RaceMission.Active;
            Assert.IsTrue(race, "race started");
            yield return WaitFor(() => race.Recorder, 15, "GO: the recorder starts");
            yield return new WaitForSeconds(6f);
            Force(race, 2);
            yield return WaitFor(() => PvpResults.View || PvpRewardPick.IsOpen, 8, "results");
            if (PvpRewardPick.IsOpen) PvpRewardPick.Pick(5);
            yield return WaitFor(() => PvpResults.View, 20, "result board");
            var last = PvpReplay.Last;
            Assert.IsNotNull(last, "the last race is kept (play_record.r0)");
            Assert.AreEqual(1, PvpReplay.Local.Count, "an offline Channel Battle round goes to the replay list");
            Assert.AreEqual(race.Room.Members.Count, last.Cars.Count, "every driver recorded");
            var ai = last.Cars.First(c => c.Ai);
            Assert.Greater(ai.Frames, 20, "AI poses 5 a second");
            Assert.Greater(last.Duration, 5f);
            Assert.AreEqual(2, last.Cars.First(c => c.Name == p.Name).Place, "places from the results");
            Assert.IsTrue((Vector3.Distance(new Vector3(ai.F[1], ai.F[2], ai.F[3]), new Vector3(ai.F[ai.F.Count - 4], ai.F[ai.F.Count - 3], ai.F[ai.F.Count - 2])) > 20f), "the AI car moved");
            yield return WaitFor(() => PvpWaitRoom.IsOpen, 20, "back to the wait room");
            PvpWaitRoom.View.Click("ID_PVP_WAIT16_QUIT");
            yield return null;
            PvpLobby.Close();
            yield return new WaitForSeconds(0.5f);

            // ---- the replay list 3410000 (offline: the rounds raced here) and the replay in VIEWING ----
            Social.ChannelBattle.OpenReplays();
            yield return new WaitForSeconds(0.3f);
            var list = Social.ChannelBattle.ReplayView;
            Assert.IsTrue(list, "replay list open");
            Assert.IsTrue(list.Control("ID_TC_REPLAY_SEL01").gameObject.activeSelf, "the round is listed");
            Shot("pvp3_1_replaylist.png");
            Social.ChannelBattle.WatchReplay(-1);
            yield return null;
            Assert.IsTrue(PvpReplay.Playing, "replay playing");
            var player = PvpReplay.Current;
            player.Speed = 0.25f;   // the recorded race is short: slow it so the ghost battle fits in it
            Assert.IsTrue(PvpSpectate.ViewingView, "VIEWING for the replay");
            Assert.IsTrue(flow.PlayerCar.ExternalControl, "the player's car waits");
            yield return new WaitForSeconds(2.5f);
            var rc = player.CarTransforms.First();
            Assert.IsTrue(rc.gameObject.activeInHierarchy, "replay cars drawn");
            Shot("pvp3_2_replay_viewing.png");
            PvpCameraRig.Instance.SetView(2);
            yield return new WaitForSeconds(1f);
            Shot("pvp3_3_replay_topview.png");
            PvpCameraRig.Instance.SetView(0);

            // ---- Ghost Battle from the replay (a bought replay carries one ticket) ----
            yield return WaitFor(() => PvpGhost.JoinView, 5, "GHOSTJOIN beside the watched row");
            Shot("pvp3_4_ghostjoin.png");
            var order = PvpSpectate.Order;
            int watched = order.FindIndex(d => d.Car && !d.Done);
            Assert.GreaterOrEqual(watched, 0);
            var original = order[watched];
            long mito = p.Mito;
            Assert.IsTrue(PvpGhost.Join(original, PvpSpectate.WatchedTrack), "ghost joined");
            Assert.IsTrue(PvpGhost.Active);
            Assert.IsTrue(PvpGhost.ExitView, "GHOSTEXIT 2010000");
            Assert.IsFalse(PvpSpectate.ViewingView.gameObject.activeSelf, "VIEWING steps aside while racing the ghost");
            Assert.IsFalse(flow.PlayerCar.ExternalControl, "the ghost drives");
            Assert.Less(Vector3.Distance(flow.PlayerCar.transform.position, original.Car.position), 6f, "on the original car");
            StringAssert.Contains("Ghost Battle", PvpGhost.LastText);
            yield return new WaitForSeconds(1.5f);
            Shot("pvp3_5_ghost.png");
            PvpGhost.Runner.ForceFinish();
            Assert.IsTrue(PvpGhost.Runner.Won, "finished before the original");
            yield return WaitFor(() => p.Mito > mito, 5, "ghost reward");
            yield return WaitFor(() => !PvpGhost.Active, PvpGhost.AfterFinish + 3f, "back to watching");
            Assert.IsTrue(PvpSpectate.ViewingView.gameObject.activeSelf, "VIEWING again");
            Assert.IsTrue(flow.PlayerCar.ExternalControl, "the car waits again");
            yield return new WaitForSeconds(1f);
            Assert.Greater(Vector3.Dot(flow.PlayerCar.transform.up, Vector3.up), 0.8f, "back upright where it waited");
            Assert.Less(flow.PlayerCar.GetComponent<Rigidbody>().linearVelocity.magnitude, 2f, "and at rest");
            // the ticket was used in this replay, joining again in it stays allowed (the client's +0x7f0)
            Assert.IsTrue(PvpGhost.Allowed);
            player.Stop(true);
            yield return WaitFor(() => PvpResults.View, 6, "the replay's result board");
            Assert.AreEqual(last.Cars.Count, PvpResults.Current.Entries.Count);
            Assert.IsFalse(PvpReplay.Playing);
            PvpResults.Close();
            Assert.IsFalse(flow.PlayerCar.ExternalControl, "car given back after the replay");

            // ---- ROUNDNUMBER 1420000 ----
            PvpHud.ShowRound(3);
            yield return null;
            Assert.IsTrue(PvpHud.RoundView, "ROUNDNUMBER open");
            Assert.IsTrue(PvpHud.RoundView.Control("ID_TC_ROUNDNUMBER3").gameObject.activeSelf, "the Final");
            Assert.IsFalse(PvpHud.RoundView.Control("ID_TC_ROUNDNUMBER1").gameObject.activeSelf);
            Shot("pvp3_6_roundnumber.png");
            PvpHud.HideRound();

            // ---- bonus icons on the result board ----
            var res = new PvpRaceResult { TrackName = "Test", Team = false };
            res.Entries.Add(new PvpResultEntry { Name = "Rio", Place = 1, Time = 90f, Bonus = Pvp.BonusPcMito });
            res.Entries.Add(new PvpResultEntry { Name = p.Name, Place = 2, Time = 92f, Me = true, Exp = 500, Mito = 3000, Bonus = Pvp.BonusExpItem | Pvp.BonusMitoItem | Pvp.BonusGolden, Tree = 2 });
            PvpResults.ShowAt = 0.1f;
            PvpResults.Show(res, null);
            yield return WaitFor(() => PvpResults.View, 3, "results");
            yield return null;
            Assert.IsTrue(PvpResults.BonusViews.ContainsKey(1500000), "EXPBONUS");
            Assert.IsTrue(Pvp.ById(PvpResults.BonusViews[1500000], 1500033 + 1).gameObject.activeSelf, "EXP + Mito item icon on my row");
            Assert.IsTrue(Pvp.ById(PvpResults.BonusViews[1520000], 1520017 + 1).gameObject.activeSelf, "tree stage 2 (40%)");
            Assert.IsTrue(Pvp.ById(PvpResults.BonusViews[1480000], 1480001).gameObject.activeSelf, "the PC-room mark on another row");
            Assert.IsTrue(GameEffects.ScreenPlaying("goldentime"), "golden time banner");
            yield return new WaitForSeconds(0.5f);
            Shot("pvp3_7_bonus_icons.png");
            PvpResults.Close();
            PvpResults.ShowAt = 3f;

            // ---- effect banners: Team ROO's time attack, the medal, gate effects ----
            Assert.AreEqual("timeattack_com", GameEffects.FactionVariant("arbeit_com", true));
            Assert.AreEqual("timeattack_fail", GameEffects.FactionVariant("arbeit_fail", true));
            Assert.AreEqual("arbeit_com", GameEffects.FactionVariant("arbeit_com", false));
            GameEffects.RooOverride = true;
            GameEffects.Screen(1);
            Assert.IsTrue(GameEffects.ScreenPlaying("timeattack_com"), "id 1 becomes 6 for ROO");
            GameEffects.RooOverride = false;
            GameEffects.ArbeitDone(1);
            Assert.IsTrue(GameEffects.ScreenPlaying("arbeit_com") && GameEffects.ScreenPlaying("arbeit_com_gold"), "arbeit_com + gold");
            yield return new WaitForSeconds(0.6f);
            Shot("pvp3_8_arbeit_gold.png");
            Assert.AreEqual("pvp_untouchable", GameEffects.GateEffect(5));
            Assert.AreEqual("pvp_crash_recover100", GameEffects.GateEffect(4), "4 = repair (the client's UI effect numbers)");
            Assert.IsTrue(GameEffects.PlayGateEffect(3), "pvp_skidrush");
            yield return new WaitForSeconds(0.6f);
            Shot("pvp3_9_skidrush.png");
            GameEffects.Clear();

            // ---- Oros dust devil: the collision sphere plays bind_hit and removes it ----
            var amb = new GameObject("Oros Test").AddComponent<OrosAmbience>();
            amb.enabled = false;   // driven by hand
            var car = flow.PlayerCar;
            var devil = amb.SpawnDevil(car.transform.position, false);
            Assert.IsNotNull(devil, "dust devil on the ground");
            devil.Fx.transform.position = car.transform.position;   // right on the car
            Assert.That(devil.Radius, Is.InRange(OrosAmbience.MinScale * OrosAmbience.RadiusPerScale, OrosAmbience.MaxScale * OrosAmbience.RadiusPerScale));
            int hits = OrosAmbience.HitCount;
            amb.Tick(car, car.transform.position + Vector3.up * 5f - car.transform.forward * 8f, car.transform.forward, 0.02f);
            Assert.AreEqual(hits + 1, OrosAmbience.HitCount, "bind_hit on touch");
            Assert.AreEqual(0, amb.Devils.Count, "a hit dust devil goes");
            Object.Destroy(amb.gameObject);

            // ---- Real Match looks (effects\realmatch\visual.xlt) ----
            var look = Pvp.RealMatchLookFor(0);
            Assert.AreEqual(6, look.Color); Assert.AreEqual(2, look.Decal); Assert.AreEqual(6, look.DecalColor); Assert.AreEqual(5, look.Neon);
            Assert.AreEqual(13, Pvp.RealMatchLookFor(1).Color);
            Assert.AreEqual(6, Pvp.RealMatchLookFor(16).Color, "16 looks, by slot");
            Assert.IsNotNull(Pvp.RealMatchCar(), "the Duel");
        }
    }
}
