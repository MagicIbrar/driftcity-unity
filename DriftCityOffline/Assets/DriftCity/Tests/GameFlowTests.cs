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
    /// Clicks through the offline flow on the original dialogs (login -> intro -> car creation -> city)
    /// in real play mode and writes screenshots to Preview/flow_*.png. Needs the installed client.
    /// </summary>
    public class GameFlowTests
    {
        string tempSave;

        [SetUp]
        public void SetUp()
        {
            tempSave = Path.Combine(Path.GetTempPath(), "driftcity_test_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempSave);
            PlayerProfile.SaveDirOverride = tempSave;
            PlayerProfile.Slot = 0;
            PlayerProfile.Current = null;
            Directory.CreateDirectory("Preview");
        }

        [TearDown]
        public void TearDown()
        {
            PlayerProfile.SaveDirOverride = null;
            foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Exclude).Where(g => g.transform.parent == null)) Object.Destroy(go);
            try { Directory.Delete(tempSave, true); } catch { }
        }

        static Camera EnsureCamera()
        {
            var cam = Camera.main;
            if (cam) return cam;
            var go = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            cam = go.GetComponent<Camera>();
            cam.clearFlags = CameraClearFlags.Skybox;
            new GameObject("Sun", typeof(Light)).GetComponent<Light>().type = LightType.Directional;
            return cam;
        }

        static void Shot(string name)
        {
            var tex = UiManager.Instance.Capture(Camera.main);
            File.WriteAllBytes(Path.Combine("Preview", name), tex.EncodeToPNG());
            Debug.Log("Drift City flow: wrote Preview/" + name);
        }

        static IEnumerator WaitFor(System.Func<bool> condition, float timeout, string what)
        {
            float t = 0;
            while (!condition() && t < timeout) { t += Time.unscaledDeltaTime; yield return null; }
            Assert.IsTrue(condition(), "timed out waiting for " + what);
        }

        [UnityTest]
        public IEnumerator NewDriverReachesCity()
        {
            EnsureCamera();
            var flow = new GameObject("Game").AddComponent<GameFlow>();
            flow.SkipParkingLot = true; flow.SkipIntro = true;   // straight to the city; ParkingLotGateEntersCity drives through the parking lot
            // the original front end: LOGIN -> server select (offline row) -> character select -> new character
            yield return FrontEndSteps.ToCharacterSelect(flow, shot: Shot);
            yield return FrontEndSteps.NewCharacter("Tester", 2, Shot);
            Shot("flow_2_carselect.png");

            var create = UiManager.Instance.Find(150000);
            create.Click("ID_CREATECAR_NEW_SELCARNEXT");
            create.Click("ID_CREATECAR_NEW_COLORCHECK3");
            yield return null; yield return null;
            Shot("flow_3_createcar.png");

            create.Click("ID_CREATECAR_NEW_NEXT");
            Assert.IsNotNull(PlayerProfile.Current, "profile created");
            Assert.AreEqual("Tester", PlayerProfile.Current.Name);
            Assert.AreEqual(3, PlayerProfile.Current.Avatar, "portrait picked with the down arrow twice");
            Assert.IsFalse(string.IsNullOrEmpty(PlayerProfile.Current.Created), "licence date");
            Assert.IsTrue(PlayerProfile.Exists, "profile saved");

            yield return WaitFor(() => UiManager.Instance.Find(320000), 600, "city HUD");
            var car = Object.FindAnyObjectByType<DriftCar>();
            Assert.IsNotNull(car, "player car spawned");
            yield return new WaitForSeconds(2.5f);
            int grounded = car.Wheels.Count(w => w && w.isGrounded);
            Debug.Log($"Drift City flow: car grounded wheels={grounded}/4 at {car.transform.position}");
            Assert.GreaterOrEqual(grounded, 3, "car resting on the road");
            Shot("flow_4_city.png");
            ArcadeLook.Enabled = false; flow.ReapplyLighting();
            yield return null; yield return null;
            Shot("flow_4_city_original.png");
            ArcadeLook.Enabled = true; ArcadeLook.ApplyEnvironment();
            yield return null; yield return null;

            // speedometer: booster arc fill and the RPM needle while accelerating
            car.BoostGauge = 0.4f;
            car.ExternalControl = true;
            for (float t = 0; t < 2.5f; t += Time.fixedDeltaTime) { car.ApplyControls(1f, 0f, false, Time.fixedDeltaTime); yield return new WaitForFixedUpdate(); car.BoostGauge = 0.4f; }
            yield return null;
            Shot("flow_hud_boost40.png");
            car.BoostGauge = 1f;
            yield return null; yield return null;
            Shot("flow_hud_boostfull.png");
            // boosting at speed: speed lines and the wider view
            for (float t = 0; t < 2.5f; t += Time.fixedDeltaTime) { car.ApplyControls(1f, 0f, false, Time.fixedDeltaTime, true); yield return new WaitForFixedUpdate(); car.BoostGauge = 1f; }
            yield return null;
            Shot("flow_arcade_boost.png");
            car.ExternalControl = false;
            ArcadeLook.Enabled = false;   // the original look is the default; don't leave the editor in arcade mode

            var flowType = typeof(GameFlow);
            flowType.GetMethod("ToggleQuitMenu", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(flow, null);
            yield return WaitFor(() => UiManager.Instance.Find(4040000), 5, "quit menu");
            Shot("flow_5_quitmenu.png");
        }

        static void Teleport(DriftCar car, Vector3 p)
        {
            var rb = car.GetComponent<Rigidbody>();
            if (Physics.Raycast(p + Vector3.up * 3f, Vector3.down, out var hit, 20f)) p = hit.point;
            p += Vector3.up * 0.8f;
            rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero;
            car.transform.position = p;
            rb.position = p;
        }

        /// <summary>The client's flow: login -> parking lot (stage 9) -> zones -> exit -> SELGATE -> city, and back.</summary>
        [UnityTest]
        public IEnumerator ParkingLotGateEntersCity()
        {
            EnsureCamera();
            var p = new PlayerProfile { Name = "Tester", Level = 29, Mito = 200_000 };   // Cras opens above level 28 (0x703d20); B1 stays closed below 31
            p.Cars.Add(new OwnedCar { VehicleIndex = 0, Color = 1 });
            p.Save();
            PlayerProfile.Current = null;
            var flow = new GameObject("Game").AddComponent<GameFlow>();
            yield return FrontEndSteps.ToCharacterSelect(flow);
            yield return FrontEndSteps.EnterSelected(Shot);

            yield return WaitFor(() => flow.InParkingLot && ParkingLotStage.Current && flow.PlayerCar, 600, "parking lot");
            Assert.IsFalse(flow.InCity, "the parking lot is not a city");
            Assert.IsFalse(CitySession.Current, "no city session (traffic) in the parking lot");
            Assert.IsNotNull(UiManager.Instance.Find(320000), "speedometer in the parking lot");
            Assert.IsNotNull(UiManager.Instance.Find(190000), "minimap in the parking lot");
            Assert.IsTrue(ParkingLotStage.BoostGiven, "ParkingLotBoost filled the booster");
            var stage = ParkingLotStage.Current;
            var car = flow.PlayerCar;
            yield return new WaitForSeconds(2.5f);
            int grounded = car.Wheels.Count(w => w && w.isGrounded);
            Debug.Log($"Drift City lot: car at {car.transform.position}, grounded {grounded}/4, area {stage.CurrentArea}");
            Assert.GreaterOrEqual(grounded, 3, "car resting on floor_p.hit");
            Assert.AreEqual(ParkingLotStage.Area.None, stage.CurrentArea, "arrival point is outside every zone");
            Shot("flow_lot_1_arrival.png");

            // a closed building: "Restricted Area" and the car is put back outside
            var auction = stage.PointIn(ParkingLotStage.Area.Battle);
            Assert.IsTrue(auction.HasValue, "battle zone building in parkinglot_fx.nif");
            Teleport(car, auction.Value);
            yield return WaitFor(() => UiPopup.IsOpen, 5, "restricted-area popup");
            Assert.AreEqual(ParkingLotStage.Area.None, stage.AreaAt(car.transform.position), "car bounced out of the zone");
            UiManager.Instance.Find(3250000).Click("ID_POPUP_OKONLY");
            yield return WaitFor(() => !UiPopup.IsOpen, 5, "popup closed");

            // the garage building opens the garage; closing it releases the car outside
            var garage = stage.PointIn(ParkingLotStage.Area.Garage);
            Assert.IsTrue(garage.HasValue, "garage zone in parkinglot_fx.nif");
            Teleport(car, garage.Value);
            yield return WaitFor(() => OriginalMenus.IsOpen, 5, "garage");
            Assert.IsTrue(car.ExternalControl, "car parked while in the garage");
            yield return null;
            Shot("flow_lot_2_garage.png");
            OriginalMenus.Close();
            yield return WaitFor(() => !car.ExternalControl, 5, "car released after the garage");
            Assert.AreEqual(ParkingLotStage.Area.None, stage.AreaAt(car.transform.position), "back outside the garage zone");

            // an exit: the gate list; PREVIOUS returns, then a gate into cras
            var exit = stage.PointIn(ParkingLotStage.Area.Exit);
            Assert.IsTrue(exit.HasValue, "area_exit in parkinglot_a.nif");
            Teleport(car, exit.Value);
            yield return WaitFor(() => UiManager.Instance.Find(3630000), 5, "gate select");
            Assert.IsTrue(ParkingLotStage.GateOpen);
            yield return null;
            Shot("flow_lot_3_selgate.png");
            UiManager.Instance.Find(3680000).Click("ID_SELGATE_PREVIOUS");
            yield return WaitFor(() => !ParkingLotStage.GateOpen && !car.ExternalControl, 5, "back in the parking lot");
            Assert.AreEqual(ParkingLotStage.Area.None, stage.AreaAt(car.transform.position), "PREVIOUS puts the car back inside");
            Teleport(car, exit.Value);
            yield return WaitFor(() => UiManager.Instance.Find(3630000), 5, "gate select again");
            Assert.AreEqual("cras", ParkingLotStage.Gates[2].City);
            UiManager.Instance.Find(3630000).Click("ID_SELGATE_MAIN_BTN3RD");
            yield return WaitFor(() => flow.InCity && CitySession.Current && flow.PlayerCar, 600, "city through the gate");
            Assert.AreEqual("cras", flow.City);
            Assert.AreEqual("cras", PlayerProfile.Current.City, "the gate's city is remembered");
            Assert.IsFalse(flow.InParkingLot);
            yield return new WaitForSeconds(1f);
            Shot("flow_lot_4_city.png");

            // and back to the parking lot (the RQ house's "parking lot" button)
            flow.ReturnToParkingLot();
            Assert.IsFalse(flow.InCity, "leaving the city at once");
            yield return WaitFor(() => flow.InParkingLot && flow.PlayerCar, 600, "parking lot again");
            Assert.IsFalse(CitySession.Current, "city session gone");
            Menus.WorldMap(); yield return null;
            Assert.IsNotNull(UiManager.Instance.Find(570000), "the parking lot's full map (VIEWMAP_P_1)");
            Shot("flow_lot_5_map.png");
            Menus.WorldMap(); yield return null;
        }

        [UnityTest]
        public IEnumerator FirstDriverSeesTheIntro()
        {
            EnsureCamera();
            var flow = new GameObject("Game").AddComponent<GameFlow>();
            flow.SkipParkingLot = true;
            yield return FrontEndSteps.ToCharacterSelect(flow);
            int camerasBefore = Object.FindObjectsByType<Camera>(FindObjectsInactive.Exclude).Length;
            bool fogBefore = RenderSettings.fog;
            // no licence yet: Create Character plays the start-up story (63.4 s), then the licence registration follows
            UiManager.Instance.Find(4140000).Click("ID_CREATE_CHARACTER_ENTER");
            yield return new WaitForSeconds(18f);
            Assert.Greater(Object.FindObjectsByType<Camera>(FindObjectsInactive.Exclude).Length, camerasBefore, "the intro runs on its own camera");
            Assert.IsNull(UiManager.Instance.Find(140000), "licence registration waits for the intro");
            yield return WaitFor(() => UiManager.Instance.Find(140000), 60, "licence registration after the intro");
            yield return null;
            Assert.AreEqual(camerasBefore, Object.FindObjectsByType<Camera>(FindObjectsInactive.Exclude).Length, "the intro cleaned up its cameras");
            Assert.AreEqual(fogBefore, RenderSettings.fog, "fog restored after the intro");
        }
    }
}
