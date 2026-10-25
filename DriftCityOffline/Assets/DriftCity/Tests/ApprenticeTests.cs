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
    /// The apprentice course (TUTORIAL.md), offline, start to reward: a new driver arrives in the parking lot (walkie-talkie +
    /// Erina, line 0), the Battle Zone (Director Jason, line 1, then 11340), the trip to Moon Palace, Duffy's lesson (line 2),
    /// a failed drift (line 3) and the retry (line 8), the drift and booster tests (lines 5, 6), the full course with the
    /// APPRENTICE HUD, the grade (line 9), the reward pick CREATECAR 4160000, and Erina's hand-over (line 12) back in the
    /// parking lot. Writes Preview/tutorial_*.png.
    /// </summary>
    public class ApprenticeTests
    {
        string tempDir;
        float savedCountdown;

        [SetUp]
        public void SetUp()
        {
            tempDir = Path.Combine(Path.GetTempPath(), "driftcity_tutorial_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            PlayerProfile.SaveDirOverride = tempDir;
            PlayerProfile.Slot = 0;
            PlayerProfile.Current = null;
            Directory.CreateDirectory("Preview");
            savedCountdown = ApprenticeCourse.CountdownSeconds;
            ApprenticeHost.ResetSession();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (ApprenticeCourse.Active) ApprenticeCourse.Active.Abort();
            ApprenticeRewardPick.Close();
            MissionUi.CloseScript();
            ApprenticeCourse.CountdownSeconds = savedCountdown;
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
            Debug.Log("Drift City tutorial: wrote Preview/" + name);
        }

        static string Speaker() => UiManager.Instance.Find(600000)?.Control("ID_QUESTDIALOG_NAME")?.GetComponentInChildren<UnityEngine.UI.Text>(true)?.text ?? "";
        static string Lines() => UiManager.Instance.Find(600000)?.Control("ID_QUESTDIALOG_CONTENT_1ST")?.GetComponentInChildren<UnityEngine.UI.Text>(true)?.text ?? "";

        static IEnumerator CloseTalk()
        {
            var v = UiManager.Instance.Find(600000);
            Assert.IsTrue(v, "QUESTDIALOG open");
            v.Click("ID_QUESTDIALOG_DONE");
            yield return null;
        }

        /// <summary>Puts the car on one side of the next gate's line, then on the other (the client's crossing test).</summary>
        static IEnumerator CrossNext(ApprenticeCourse c, DriftCar car)
        {
            var g = c.Gates.Next;
            var d = g.B - g.A; d.y = 0;
            var n = new Vector3(-d.z, 0f, d.x).normalized;
            float y = 0f, best = float.MaxValue;
            foreach (var q in c.Track.Line) { float e = (q.x - g.Centre.x) * (q.x - g.Centre.x) + (q.z - g.Centre.z) * (q.z - g.Centre.z); if (e < best) { best = e; y = q.y; } }
            var centre = PvpGates.GroundNear(new Vector3(g.Centre.x, y, g.Centre.z));
            int before = c.Gates.Count;
            foreach (var side in new[] { -5f, 5f })
            {
                var pos = centre + n * side;
                var rb = car.GetComponent<Rigidbody>();
                rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero;
                var rot = Quaternion.LookRotation(n, Vector3.up);
                car.transform.SetPositionAndRotation(pos, rot); rb.position = pos; rb.rotation = rot;
                yield return null; yield return null;
            }
            Assert.AreEqual(before + 1, c.Gates.Count, "gate counted");
        }

        [Test]
        public void DataFollowsTheClient()
        {
            if (DriftCityData.Files == null) DriftCityData.Mount(DriftCityData.ResolveGameRoot(@"D:\Drift_City_Remastered_Alpha_08-2025\Drift City Remastered Alpha"));
            GameTables.Load();
            Apprentice.ResetCache();
            Assert.AreEqual(13, Apprentice.ScriptCount, "Apprentice_Client.tdf: 13 scripts");
            StringAssert.Contains("Instructor Duffy", Apprentice.Script(Apprentice.Lesson));
            StringAssert.Contains("Operator Erina", Apprentice.Script(Apprentice.Erina));
            CollectionAssert.AreEqual(new[] { 130f, 135f, 140f }, Apprentice.GradeSeconds, "globalcontent ApprenticeLapResult* (US)");
            Assert.AreEqual(new[] { 0, 1, 2, 3 }, new[] { 129f, 131f, 139f, 141f }.Select(Apprentice.Grade).ToArray());
            var track = Apprentice.Track;
            Assert.IsNotNull(track, "moonpalace Tutorial track");
            Assert.AreEqual(74, track.Id);
            Assert.AreEqual(7, track.Gates.Count);
            Assert.AreEqual(1, track.Laps);
            Assert.Greater(track.Length, 4000f, "point-to-point line through the gates");
            CollectionAssert.AreEqual(new[] { 2, 3, 8, 0 }, Apprentice.RewardCars.Select(c => c.car.Index).ToArray(), "Advance, Ironia, Drifter, Kicker");
            Assert.IsTrue(Apprentice.Allowed(0, 10001) && !Apprentice.Allowed(1, 10001) && Apprentice.Allowed(3, 40001) && !Apprentice.Allowed(3, 30001));
            StringAssert.Contains("2 minutes and 15 seconds", Apprentice.LockText(2, 20001));
            Assert.IsFalse(Pvp.MapList("moonpalace", 0, 0).Contains(track), "the course is no Battle Zone map");
        }

        [UnityTest]
        public IEnumerator NewDriverRunsTheCourseToTheReward()
        {
            var p = new PlayerProfile { Name = "Rookie", Level = 1, Mito = 50000, Apprentice = true, City = "cras" };
            p.Cars.Add(new OwnedCar { VehicleIndex = 0, Color = 0 });
            p.Save();
            new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            var flow = new GameObject("Game").AddComponent<GameFlow>();
            flow.SkipIntro = true;
            ApprenticeHost.Ensure();
            ApprenticeCourse.CountdownSeconds = 1f;

            // ---- the parking lot: the walkie-talkie and Erina (line 0) ----
            yield return FrontEndSteps.ToCharacterSelect(flow);
            var ui = UiManager.Instance;
            yield return FrontEndSteps.EnterSelected();
            yield return WaitFor(() => flow.InParkingLot, 600, "parking lot");
            yield return WaitFor(() => MissionUi.ScriptOpen, 10, "line 0");
            StringAssert.Contains("Walkie-Talkie", Speaker() + Lines() + Apprentice.Script(0));
            yield return new WaitForSeconds(0.5f);
            Shot("tutorial_1_lot_intro.png");
            yield return CloseTalk();

            // ---- the Battle Zone: Director Jason (line 1), then 11340 ----
            Assert.IsTrue(ApprenticeCourse.BattleZone(true));
            yield return null;
            StringAssert.Contains("Director Jason", Speaker());
            Shot("tutorial_2_jason.png");
            yield return CloseTalk();
            yield return WaitFor(() => ui.Find(3250000), 5, "11340 popup");
            Shot("tutorial_3_confirm.png");
            ui.Find(3250000).Click(UiPopup.Yes);

            // ---- Moon Palace, Duffy's lesson (line 2) ----
            yield return WaitFor(() => ApprenticeCourse.Active, 900, "the course in Moon Palace");
            var c = ApprenticeCourse.Active;
            var car = flow.PlayerCar;
            Assert.AreEqual("moonpalace", CitySession.Current.City);
            Assert.AreEqual(Apprentice.Lesson, c.Step);
            Assert.IsTrue(MissionUi.ScriptOpen && Speaker().Contains("Duffy"), "line 2");
            Assert.IsTrue(ui.Find(830000) && ui.Find(840000) && ui.Find(850000), "APPRENTICE HUD");
            yield return new WaitForSeconds(0.5f);
            Shot("tutorial_4_lesson.png");
            yield return CloseTalk();
            yield return WaitFor(() => c.Racing, 10, "GO");
            yield return new WaitForSeconds(0.3f);
            Shot("tutorial_5_drift_test.png");

            // ---- no drift at the first gate: line 3, the run starts over with line 8 ----
            yield return CrossNext(c, car);
            Assert.AreEqual(Apprentice.DriftFailed, c.Step);
            StringAssert.Contains("failed the Drift test", Lines());
            Shot("tutorial_6_drift_failed.png");
            c.AddTime(Apprentice.Gate1Restart);
            yield return null; yield return null;
            Assert.AreEqual(Apprentice.Lesson, c.Step, "back to the lesson");
            Assert.AreEqual(0, c.Gates.Count, "from the start");
            StringAssert.Contains("take the test again", Lines());
            yield return CloseTalk();
            yield return WaitFor(() => c.Racing, 10, "GO again");

            // ---- the drift test (gates 1 and 2) and the booster test (gate 3) ----
            c.SimulateDrift(0.8f);
            yield return CrossNext(c, car);
            Assert.AreEqual(Apprentice.Lesson, c.Step, "drift passed at gate 1");
            c.SimulateDrift(0.8f);
            yield return CrossNext(c, car);
            Assert.AreEqual(Apprentice.BoosterLesson, c.Step, "line 5: the booster");
            Assert.AreEqual(Apprentice.BoosterGauge, car.BoostGauge, 0.001f, "gauge set to 97");
            Shot("tutorial_7_booster.png");
            yield return CloseTalk();
            car.BoostGauge = 0.2f;   // the booster was used
            yield return CrossNext(c, car);
            Assert.AreEqual(Apprentice.BoosterPassed, c.Step, "line 6");
            yield return CloseTalk();
            yield return new WaitForSeconds(1f);

            // ---- the full course ----
            Assert.AreEqual(Apprentice.FullCourse, c.Step, "the full course (step 8)");
            yield return WaitFor(() => c.Racing, 10, "GO for the full course");
            Assert.IsFalse(MissionUi.ScriptOpen, "no lines on the full course");
            for (int i = 0; i < 4; i++) yield return CrossNext(c, car);
            c.AddTime(60f);
            yield return new WaitForSeconds(0.3f);
            Shot("tutorial_8_full_course.png");
            for (int i = 0; i < 3; i++) yield return CrossNext(c, car);
            c.AddTime(124f - c.Clock);
            yield return CrossNext(c, car);   // the finish line
            Assert.AreEqual(0, ApprenticeCourse.LastGrade, "under 130 s");
            Assert.AreEqual(Apprentice.Excellent, c.Step);
            StringAssert.Contains("Excellent", Lines());
            Shot("tutorial_9_excellent.png");
            yield return CloseTalk();

            // ---- the reward pick CREATECAR 4160000 ----
            yield return WaitFor(() => ApprenticeRewardPick.IsOpen, 5, "reward pick");
            Assert.IsFalse(ApprenticeRewardPick.Locked, "grade 0 takes the Advance");
            Assert.IsTrue(ApprenticeRewardPick.Pick(2));
            yield return new WaitForSeconds(0.5f);
            Shot("tutorial_10_reward_pick.png");
            ApprenticeRewardPick.Confirm();
            yield return null;
            Assert.IsFalse(ApprenticeRewardPick.IsOpen);
            var me = PlayerProfile.Current;
            Assert.IsFalse(me.Apprentice, "the course is done");
            Assert.AreEqual(2, me.Car.VehicleIndex, "the Advance is the current car");
            Assert.AreEqual(124f, me.ApprenticeBest, 0.5f);

            // ---- back in the parking lot: Erina sends the driver to a city (line 12) ----
            yield return WaitFor(() => flow.InParkingLot, 600, "parking lot again");
            yield return WaitFor(() => MissionUi.ScriptOpen, 10, "line 12");
            StringAssert.Contains("Erina", Speaker());
            Assert.AreEqual("cras", me.City, "the saved city is back");
            yield return new WaitForSeconds(0.5f);
            Shot("tutorial_11_erina.png");
            yield return CloseTalk();

            // ---- later: TUTORIAL offers a practice run (11469), no second reward ----
            ApprenticeCourse.TutorialButton();
            yield return null;
            Assert.IsTrue(ui.Find(3250000), "11469 popup");
            ui.Find(3250000).Click(UiPopup.No);
            var again = Apprentice.ClaimOffline(me, 1000f, 0, 40001, 0);
            Assert.IsFalse(again.Ok, "one reward per driver");
        }
    }
}
