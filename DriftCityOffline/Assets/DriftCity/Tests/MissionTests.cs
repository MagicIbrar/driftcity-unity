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
    /// The station missions in the open city: the Delivery Service from the station panel to the medal (and its failure,
    /// retry and abandon paths), an instant mission from the Call Mission notice to its reward, and a patrol offered by the
    /// officer beside a station. The car is moved by teleporting, as RegressionTests does at the station.
    /// </summary>
    public class MissionTests
    {
        string tempSave;

        [SetUp]
        public void SetUp()
        {
            tempSave = Path.Combine(Path.GetTempPath(), "driftcity_missions_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempSave);
            PlayerProfile.SaveDirOverride = tempSave;
            PlayerProfile.Slot = 0;
            MissionRecords.Reset();
            var p = new PlayerProfile { Name = "Courier", Level = 30, Mito = 100_000, City = "cras" };
            p.Cars.Add(new OwnedCar { VehicleIndex = 0, Color = 1 });
            p.Save();
            PlayerProfile.Current = null;
        }

        [TearDown]
        public void TearDown()
        {
            PlayerProfile.SaveDirOverride = null;
            MissionRecords.Reset();
            foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Exclude).Where(g => g.transform.parent == null)) Object.Destroy(go);
            try { Directory.Delete(tempSave, true); } catch { }
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

        static void ClosePopups()
        {
            foreach (var v in UiManager.Instance.OpenViews.Where(v => v && v.Dialog.Id == 3250000).ToList())
            {
                if (v.Control(UiPopup.No) && v.Control(UiPopup.No).gameObject.activeSelf) v.Click(UiPopup.No);
                else v.Click("ID_POPUP_OKONLY");
            }
        }

        [UnityTest]
        public IEnumerator DeliveryInstantAndPatrol()
        {
            new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            var flow = new GameObject("Game").AddComponent<GameFlow>();
            flow.SkipParkingLot = true;
            yield return FrontEndSteps.ToCharacterSelect(flow);
            yield return FrontEndSteps.EnterSelected();
            yield return WaitFor(() => flow.InCity && CitySession.Current && flow.PlayerCar, 600, "city");
            var P = PlayerProfile.Current;
            var session = CitySession.Current;
            session.MissionOfferInterval = 1e6f;   // no HUV chase offers during the test
            Assert.AreEqual("cras", flow.City);

            // the lead's GameFlow hooks, done here until they land
            var delivery = DeliveryService.Instance ? DeliveryService.Instance : DeliveryService.Attach(session, flow.City);
            var instants = InstantMissions.Instance ? InstantMissions.Instance : InstantMissions.Attach(session, flow.City);
            var patrol = PatrolMission.Instance ? PatrolMission.Instance : PatrolMission.Attach(session, flow.City);
            instants.OfferInterval = instants.FirstOffer = 1e6f;
            float patrolRadius = patrol.OfferRadius;
            patrol.OfferRadius = 0f;

            // ---------------- tables ----------------
            Assert.GreaterOrEqual(DeliveryService.Jobs.Count, 50, "regular jobs of ArbeitClient.tdf with resolvable routes");
            var a41 = DeliveryService.FindJob("a_00041");
            Assert.IsNotNull(a41, "Cras Station 1 Medium job");
            Assert.AreEqual(5, a41.Points.Count, "the route stops before the padding column");
            CollectionAssert.AreEqual(new[] { 80f, 85f, 95f }, a41.Time);
            Assert.AreEqual(1, a41.MedalFor(79f)); Assert.AreEqual(3, a41.MedalFor(90f)); Assert.AreEqual(0, a41.MedalFor(96f));
            Assert.AreEqual("[Cras] Destination <Delivery Service [1]> arrived.", MissionText.Get("10126", "Cras", "Delivery Service [1]"));
            Assert.AreEqual(138, InstantFormula.Eval("INT(({Level}^2)/7+10)", 30));

            // ---------------- Delivery Service: station panel -> ARBEIT list + QUESTDIALOG ----------------
            var station = CityMapInfo.Stations(flow.City)[0];
            var jobsHere = DeliveryService.JobsAt(station.Id, P.Level);
            Assert.GreaterOrEqual(jobsHere.Count, 3, "jobs at " + station.Name);
            var car = flow.PlayerCar;
            Teleport(car, station.Position, MissionPosts.Find(jobsHere[0].Points[0]).Position);
            yield return WaitFor(() => UiManager.Instance.Find(400000), 5, "station panel");
            UiManager.Instance.Find(400000).Click("ID_ENTRANCE_RQHOUSE_ARBEIT");
            yield return null;
            Assert.IsTrue(DeliveryService.BoardOpen, "ARBEIT list open");
            Assert.IsTrue(UiManager.Instance.Find(600000), "delivery worker's lines in QUESTDIALOG");
            Assert.IsFalse(UiManager.Instance.Find(400000).gameObject.activeSelf, "the station panel steps aside");
            Assert.AreEqual(jobsHere.Count, DeliveryService.BoardJobs.Count);
            // pick the Medium job's row like a click
            int row = DeliveryService.BoardJobs.ToList().FindIndex(j => j.Id == "a_00041");
            Assert.GreaterOrEqual(row, 0);
            GameObject.Find("Row a_00041").GetComponent<UnityEngine.UI.Button>().onClick.Invoke();
            Assert.AreEqual(row, DeliveryService.Selected);
            var gold = DeliveryService.Board.Control("ID_ARBEIT_REWARDTEXT_MEDAL_GOLD").GetComponentInChildren<UnityEngine.UI.Text>(true).text;
            Assert.AreEqual("1:20:00 900Exp 1100Mito", gold, "medal line format 0x86dbb4");
            yield return new WaitForSeconds(0.3f);
            Shot("missions_delivery_board.png");

            UiManager.Instance.Find(600000).Click("ID_QUESTDIALOG_ACCEPT");
            yield return null;
            Assert.IsTrue(DeliveryService.Active, "delivery running");
            Assert.AreEqual("a_00041", delivery.Job.Id);
            Assert.IsFalse(DeliveryService.BoardOpen);
            Assert.IsTrue(UiManager.Instance.Find(DeliveryService.InfoId) && UiManager.Instance.Find(DeliveryService.ClockId), "ARBEITPLAYINFO + clock");
            Assert.AreEqual(1, MissionMarkers.All().Count(m => m.Icon == "ID_MINIMAP_ICON_ARBEIT"), "minimap marker for the destination");
            // look at the first destination's beacon from 70 m
            var first = delivery.Route[0].Position;
            var back = first + (car.transform.position - first).normalized * 70f;
            Teleport(car, back, first);
            yield return new WaitForSeconds(1.2f);
            Assert.Greater(delivery.Elapsed, 1f, "the clock runs");
            Shot("missions_delivery_run.png");

            // drive the route (teleport onto each destination)
            long mito0 = P.Mito;
            for (int i = 0; i < delivery.Route.Count; i++)
            {
                int expect = i + 1;
                var target = delivery.Route[i].Position;
                Teleport(car, target, i + 1 < delivery.Route.Count ? delivery.Route[i + 1].Position : target + Vector3.forward);
                yield return WaitFor(() => delivery.Next >= expect || !delivery.Running, 5, "destination " + expect);
            }
            Assert.IsFalse(delivery.Running, "all destinations reached");
            yield return WaitFor(() => UiPopup.IsOpen, 3, "result popup");
            Assert.AreEqual(mito0 + 1100, P.Mito, "gold medal Mito (the run took seconds)");
            var rec = MissionRecords.Current.For("a_00041");
            Assert.AreEqual(1, rec.Medal);
            Assert.AreEqual(1, rec.Best.Count);
            Assert.IsTrue(File.Exists(MissionRecords.PathFor(P)), "records saved next to the profile");
            Shot("missions_delivery_done.png");
            ClosePopups();
            yield return WaitFor(() => !DeliveryService.Active, 5, "delivery HUD put away");

            // failure: past twice the bronze time -> 10137, retry offered (No)
            delivery.Begin(a41);
            delivery.Elapsed = a41.Time[2] * delivery.FailFactor + 1f;
            yield return null; yield return null;
            Assert.IsFalse(DeliveryService.Active, "failed run ends");
            Assert.IsTrue(UiPopup.IsOpen, "retry question");
            ClosePopups();

            // Restart puts the car back at the station with the clock at zero
            delivery.Begin(a41);
            Teleport(car, delivery.Route[2].Position + Vector3.right * 200f, delivery.Route[2].Position);
            yield return new WaitForSeconds(0.5f);
            delivery.Restart();
            yield return null;
            Assert.Less(delivery.Elapsed, 0.2f);
            Assert.Less(MissionPosts.Flat(car.transform.position, station.Position), 10f, "back at " + station.Name);

            // the station panel stays during a delivery with its "in progress" badge; its button opens the delivery
            // log MYARBEIT -> Abandon (10189) -> 10124
            yield return WaitFor(() => UiManager.Instance.Find(400000), 5, "station panel during the delivery");
            var entrance = UiManager.Instance.Find(400000);
            Assert.IsTrue(entrance.Control("ID_ENTRANCE_RQHOUSE_ING_QUICKSVC").gameObject.activeSelf, "ING_QUICKSVC badge");
            entrance.Click("ID_ENTRANCE_RQHOUSE_ARBEIT");
            yield return null;
            var my = UiManager.Instance.Find(DeliveryService.MyBoardId);
            Assert.IsTrue(my, "MYARBEIT open");
            Assert.IsFalse(entrance.gameObject.activeSelf, "the panel steps aside for the log");
            yield return new WaitForSeconds(0.2f);
            Shot("missions_delivery_detail.png");
            my.Click("ID_MYARBEIT_GIVEUP");
            yield return null;
            UiManager.Instance.OpenViews.Last(v => v && v.Dialog.Id == 3250000).Click(UiPopup.Yes);
            yield return null;
            Assert.IsFalse(DeliveryService.Active, "abandoned");
            Assert.IsFalse(UiManager.Instance.Find(DeliveryService.MyBoardId));
            Assert.IsTrue(!entrance || entrance.gameObject.activeSelf, "the panel comes back");

            // move away from the station so the patrol offer below is the officer's
            var parkAway = station.Position + new Vector3(400, 0, 400);
            Teleport(car, parkAway, station.Position);
            yield return new WaitForSeconds(0.5f);

            // ---------------- instant mission: Call Mission -> accept -> progress -> reward ----------------
            var eligible = InstantMissions.Eligible(P.Level);
            Assert.IsTrue(eligible.All(r => r.Category == "City"), "NewInstant City rows");
            CollectionAssert.IsSubsetOf(new[] { "knife", "combo", "Arrest_HUV" }, eligible.Select(r => r.Kind).Distinct().ToList());
            var arrest = eligible.First(r => r.Kind == "Arrest_HUV");
            Assert.IsTrue(instants.Offer(arrest), "Call Mission shown");
            Assert.IsTrue(UiManager.Instance.Find(InstantMissions.CallId));
            yield return new WaitForSeconds(0.2f);
            Shot("missions_instant_call.png");
            UiManager.Instance.Find(InstantMissions.CallId).Click("ID_INSTANTMISSION_TABBUTTON");
            yield return null;
            Assert.AreSame(arrest, instants.Current, "accepted");
            Assert.IsTrue(UiManager.Instance.Find(InstantMissions.DescId), "description 1790000");
            Assert.IsTrue(MissionTracker.View && UiManager.Instance.IsOpen(MissionTracker.View), "running-mission list 240000");
            Assert.Greater(instants.TimeLeft, 60f, "column 7 minutes");
            yield return new WaitForSeconds(0.3f);
            Shot("missions_instant_run.png");
            instants.ShowConfirm();
            yield return null;
            Assert.IsTrue(UiManager.Instance.Find(InstantMissions.ConfirmId), "confirm 1800000");
            Shot("missions_instant_confirm.png");
            UiManager.Instance.Find(InstantMissions.ConfirmId).Click("ID_INSTANTMISSION_CONFIRM_CLOSE");
            long expBefore = P.Exp, mitoBefore = P.Mito;
            int lvl = P.Level;
            P.HuvCaught += 10;   // chases won elsewhere count through the profile counter
            yield return null;
            Assert.AreEqual(10, instants.Achieved);
            P.HuvCaught += arrest.Count;
            yield return null;
            Assert.IsNull(instants.Current, "completed");
            Assert.AreEqual(mitoBefore + arrest.MitoFor(lvl), P.Mito, "Mito INT(Level^2/7+10)");
            Assert.AreEqual(1, MissionRecords.Current.InstantsCompleted);
            Assert.IsFalse(MissionTracker.View, "list closed with nothing running");
            ClosePopups();
            // give up path
            var knife = eligible.First(r => r.Kind == "knife");
            instants.Begin(knife);
            instants.Report("knife", 3);
            Assert.AreEqual(3, instants.Achieved);
            instants.GiveUp();
            Assert.IsNull(instants.Current);

            // ---------------- patrol: the officer beside Station 1 offers it ----------------
            var rows = PatrolMission.RowsFor(flow.City, P.Level);
            Assert.GreaterOrEqual(rows.Count, 4, "one Officer Heather patrol per station");
            var heather = rows.OrderBy(r => MissionPosts.Flat(MissionPosts.Find(r.Posts[0]).Position, station.Position)).First();
            Assert.AreEqual(160, heather.MaxPosts);
            var officer = MissionPosts.Find(heather.Posts[0]);
            Assert.Less(MissionPosts.Flat(officer.Position, station.Position), 150f, "the officer stands by " + station.Name);
            patrol.OfferRadius = patrolRadius;
            Teleport(car, officer.Position, station.Position);
            yield return WaitFor(() => UiManager.Instance.Find(InstantMissions.CallId), 5, "officer's Call Mission");
            UiManager.Instance.Find(InstantMissions.CallId).Click("ID_INSTANTMISSION_TABBUTTON");
            yield return null;
            Assert.IsTrue(PatrolMission.Active, "patrol running");
            Assert.AreEqual(160, patrol.Posts.Count);
            Assert.AreEqual(3, MissionMarkers.All().Count(m => m.Icon == "ID_MINIMAP_ICON_PATROL"));
            MissionUi.CloseScript();
            var firstPost = patrol.NextPost;
            Teleport(car, firstPost.Position + (officer.Position - firstPost.Position).normalized * 60f, firstPost.Position);
            yield return new WaitForSeconds(1f);
            Shot("missions_patrol_run.png");
            for (int i = 0; i < 3; i++)
            {
                var post = patrol.NextPost;
                int expect = i + 1;
                Teleport(car, post.Position, post.Position + Vector3.forward);
                yield return WaitFor(() => patrol.Visited >= expect, 5, "post " + expect);
            }
            patrol.ShowConfirm();
            yield return null;
            var pc = UiManager.Instance.Find(PatrolMission.ConfirmId);
            Assert.IsTrue(pc, "PATROL 3380000");
            Assert.AreEqual("003", pc.Control("ID_PATROL_CONFIRM_CUR").GetComponentInChildren<UnityEngine.UI.Text>(true).text);
            Assert.AreEqual("160", pc.Control("ID_PATROL_CONFIRM_MAX").GetComponentInChildren<UnityEngine.UI.Text>(true).text);
            yield return new WaitForSeconds(0.2f);
            Shot("missions_patrol_confirm.png");
            long patrolMito = P.Mito;
            pc.Click("ID_PATROL_CONFIRM_GIVEUP");
            yield return null;
            UiManager.Instance.OpenViews.Last(v => v && v.Dialog.Id == 3250000).Click(UiPopup.Yes);
            yield return null;
            Assert.IsFalse(PatrolMission.Active, "patrol ended");
            Assert.AreEqual(patrolMito + 3 * heather.PostMito, P.Mito, "posts paid on giving up");
            Assert.AreEqual(3, MissionRecords.Current.PatrolPosts);
            ClosePopups();
        }
    }
}
