using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DriftCity.Game;
using DriftCity.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.TestTools;

namespace DriftCity.Tests
{
    /// <summary>
    /// Checks for bugs found in the full review: editor fake-null components, dealer cleanup, popups answering only
    /// their own buttons, mission guards, frozen cars holding their brakes, car-local collider bounds, assets freed on
    /// rebuild, atomic saves with backup recovery, ADPCM audio, duplicate purchases, interrupted chases.
    /// </summary>
    public class RegressionTests
    {
        string tempSave;

        [SetUp]
        public void SetUp()
        {
            tempSave = Path.Combine(Path.GetTempPath(), "driftcity_reg_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempSave);
            PlayerProfile.SaveDirOverride = tempSave;
            PlayerProfile.Slot = 0;
            var p = new PlayerProfile { Name = "Tester", Level = 30, Mito = 5_000_000 };
            p.Cars.Add(new OwnedCar { VehicleIndex = 0, Color = 2 });
            p.Save();
            PlayerProfile.Current = null;
        }

        [TearDown]
        public void TearDown()
        {
            PlayerProfile.SaveDirOverride = null;
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

        [UnityTest]
        public IEnumerator ReviewFindingsStayFixed()
        {
            new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            var flow = new GameObject("Game").AddComponent<GameFlow>();
            flow.SkipParkingLot = true;   // straight to the city; ParkingLotGateEntersCity drives through the parking lot
            yield return FrontEndSteps.ToCharacterSelect(flow);
            yield return FrontEndSteps.EnterSelected();
            yield return WaitFor(() => flow.InCity && CitySession.Current && flow.PlayerCar, 600, "city");
            var P = PlayerProfile.Current;

            // editor fake-null: the HUD and minimap components really exist and drive their dialogs
            Assert.IsTrue(flow.TryGetComponent<Hud>(out var hud) && hud.Car == flow.PlayerCar, "HUD component added in the editor");
            Assert.IsTrue(flow.TryGetComponent<Minimap>(out _), "minimap component added in the editor");

            // ADPCM sound effects decode (89 of the game's effects use IMA ADPCM)
            var click = AudioLibrary.Wav("sfx/SID_CLICK_YES");
            Assert.IsNotNull(click, "ADPCM click sound decodes");
            Assert.Greater(click.samples, 100);
            Assert.IsNotNull(AudioLibrary.Wav("sfx/SID_DRIFT"), "drift skid sound decodes");
            Assert.IsTrue(flow.PlayerCar.TryGetComponent<CarSounds>(out _), "car sounds attached");

            // the game's own shaders: car bodies (ActionGameCartoonFX) take the paint as MaterialColor, buildings use
            // ActionGameTree, terrain VCAlphaTextureBlender; the original look is the default
            var bodyRenderers = flow.PlayerCar.GetComponentsInChildren<Renderer>().Where(r => r.sharedMaterial && r.sharedMaterial.shader.name == "DriftCity/AGCar").ToList();
            Assert.IsNotEmpty(bodyRenderers, "car body uses the car shader");
            Assert.IsTrue(bodyRenderers.Any(r => r.sharedMaterial.GetColor("_MaterialColor") == CarPaint.PaintColor(P.Car)), "paint goes into MaterialColor");
            var worldShaders = new HashSet<string>(Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude).Where(r => r.sharedMaterial).Select(r => r.sharedMaterial.shader.name));
            Assert.IsTrue(worldShaders.Contains("DriftCity/AGTree"), "buildings use ActionGameTree");
            Assert.IsTrue(worldShaders.Contains("DriftCity/VCAlphaTextureBlender"), "terrain uses VCAlphaTextureBlender");
            Assert.IsTrue(GameObject.Find("Sky Dome"), "weather sky dome");

            // the driving camera starts in goption.ini's DefaultPlayerCamera = 2: normalview2, 8 m behind, 3 m up, aimed at the car
            var chase = Camera.main.GetComponent<ChaseCamera>();
            Assert.AreEqual(2, chase.View, "normalview2 is the shipped default view");
            Assert.AreEqual(8f, chase.Distance, 0.01f);
            Assert.AreEqual(3f, chase.Height, 0.01f);
            yield return new WaitForSeconds(0.5f);
            float camPitch = Vector3.Angle(Vector3.ProjectOnPlane(Camera.main.transform.forward, Vector3.up), Camera.main.transform.forward);
            Assert.That(camPitch, Is.InRange(8f, 22f), "the high view looks down onto the car (the client's ~14 degrees)");

            // a mission station: driving up opens the ENTRANCE panel, driving away closes it
            var station = CityMapInfo.Stations(flow.City)[0];
            var carBody = flow.PlayerCar.GetComponent<Rigidbody>();
            var parked = flow.PlayerCar.transform.position;
            if (Physics.Raycast(station.Position + Vector3.up * 200f, Vector3.down, out var stationGround, 400f)) carBody.position = stationGround.point + Vector3.up * 0.5f;
            else carBody.position = station.Position + Vector3.up * 0.5f;
            carBody.linearVelocity = Vector3.zero;
            yield return new WaitForSeconds(1f);
            Assert.IsNotNull(UiManager.Instance.Find(400000), "station panel at " + station.Name);
            Shot("reg_station.png");
            carBody.position = parked; carBody.linearVelocity = Vector3.zero;
            yield return new WaitForSeconds(1f);
            Assert.IsNull(UiManager.Instance.Find(400000), "station panel closes when driving away");

            // the client's NAME plate (1940000) over the player's car, in screen space
            Assert.IsTrue(flow.PlayerCar.GetComponent<NamePlate>(), "name plate on the player's car");
            Assert.IsTrue(UiManager.Instance.Root.GetComponentsInChildren<NuiView>().Any(v => v.Dialog.Id == 1940000 && v.gameObject.activeInHierarchy), "name plate drawn");

            // weathers: rain brings its own dome, fog colour and rain streaks; back to clear afterwards
            var clock = Object.FindAnyObjectByType<WeatherClock>();
            Assert.IsTrue(clock, "weather clock in the city");
            WeatherClock.Frozen = true;
            try
            {
                OriginalLook.WeatherIndex = OriginalLook.FindWeather("heavy_rain");
                clock.Refresh();
                yield return null;
                Assert.IsTrue(GameObject.Find("Rain"), "rain streaks in heavy rain");
                Assert.IsTrue(GameObject.Find("Sky Dome").GetComponentsInChildren<Renderer>().Any(r => r.name.ToLowerInvariant() == "skydom_rain"), "rain sky dome shown");
                Assert.AreEqual(OriginalLook.Current.FogColor, RenderSettings.fogColor, "rain fog colour");
                yield return new WaitForSeconds(0.5f);
                Shot("reg_weather_rain.png");
                OriginalLook.WeatherIndex = OriginalLook.FindWeather("fine_sunset");
                clock.Refresh();
                yield return null;
                Assert.IsTrue(GameObject.Find("Sky Dome").GetComponentsInChildren<Renderer>().Any(r => r.name.ToLowerInvariant() == "skydom_sunset_cloud"), "sunset sky dome shown");
                Shot("reg_weather_sunset.png");
                OriginalLook.WeatherIndex = OriginalLook.FindWeather("cloud");
                clock.Refresh();
                yield return null;
                Assert.IsFalse(GameObject.Find("Rain"), "rain stops with the clear weather");
            }
            finally { WeatherClock.Frozen = false; }

            // dealer preview goes away with the dialog, however it is closed
            Menus.CarShop();
            yield return null;
            Assert.IsTrue(GameObject.Find("Dealer Camera"), "dealer camera while open");
            var dealerCamera = GameObject.Find("Dealer Camera").GetComponent<Camera>();
            Assert.IsFalse(GraphicsFormatUtility.HasAlphaChannel(dealerCamera.targetTexture.graphicsFormat), "the showroom covers the city: no alpha");
            Assert.IsTrue(GameObject.Find("autoshop.nif"), "original showroom scene");
            UiManager.Instance.CloseAll();
            yield return null; yield return null;
            Assert.IsFalse(GameObject.Find("Dealer Camera"), "dealer camera removed after CloseAll");
            Assert.IsFalse(Menus.DealerOpen);

            // popups only answer their own buttons
            string got = "";
            var first = UiPopup.Show("first", id => got += "A", UiPopup.Yes, UiPopup.No);
            var second = UiPopup.Show("second", id => got += "B", UiPopup.Yes, UiPopup.No);
            first.Click(UiPopup.Yes);
            Assert.AreEqual("A", got, "the first popup's Yes runs the first popup's callback");
            second.Click(UiPopup.No);
            Assert.AreEqual("AB", got);
            Assert.IsFalse(UiPopup.IsOpen);

            // freezing the car drops throttle and holds the brakes (no runaway car while chatting / in countdowns)
            var car = flow.PlayerCar;
            for (int i = 0; i < 20; i++) { car.ApplyControls(1f, 0f, false, Time.fixedDeltaTime, true); yield return new WaitForFixedUpdate(); }
            car.ExternalControl = true;
            Assert.IsTrue(car.Wheels.Where(w => w).All(w => w.motorTorque == 0f && w.brakeTorque > 0f), "frozen car holds its brakes");
            Assert.IsFalse(car.Boosting, "frozen car stops boosting");
            car.ExternalControl = false;

            // the chassis collider is measured in car space: a car built facing east is still long along its own z
            var info = GameTables.Car(P.Car.VehicleIndex);
            var sideways = CarFactory.BuildDrivable(info, P.Car, new Vector3(0, -2500, 0), Quaternion.Euler(0, 90, 0));
            var box = sideways.GetComponent<BoxCollider>();
            Debug.Log($"Drift City regression: collider size {box.size} for a car built at 90 degrees");
            Assert.Greater(box.size.z, box.size.x * 1.5f, "collider is long along the car, not across it");
            Object.Destroy(sideways.gameObject);

            // rebuilding the car frees its meshes (no growth over ten rebuilds)
            yield return null;
            int meshesBefore = Resources.FindObjectsOfTypeAll<Mesh>().Length;
            for (int i = 0; i < 10; i++) { flow.RespawnCar(); flow.RepaintCar(); yield return null; }
            yield return null; yield return null;
            int meshesAfter = Resources.FindObjectsOfTypeAll<Mesh>().Length;
            Debug.Log($"Drift City regression: meshes {meshesBefore} -> {meshesAfter} after 10 rebuilds");
            Assert.Less(meshesAfter - meshesBefore, 40, "car rebuilds don't leak meshes");

            // mission guard: no car swaps during a race
            var race = new GameObject("Race").AddComponent<RaceMission>();
            typeof(RaceMission).GetMethod("Begin", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .Invoke(race, new object[] { RaceMission.TracksFor(flow.City)[0], flow.PlayerCar, CitySession.Current.Traffic });
            yield return null;
            Assert.IsTrue(GameFlow.MissionActive);
            Assert.IsTrue(GameFlow.BlockedByMission(), "car swaps are refused during a race");
            foreach (var v in UiManager.Instance.OpenViews.ToList()) if (v && v.Dialog.Id == 3250000) UiManager.Instance.Close(v);
            Assert.IsNotNull(flow.PlayerCar.RespawnOverride, "R respawns on the racing line during a race");
            race.Abort();
            yield return null;
            Assert.IsNull(flow.PlayerCar.RespawnOverride, "track respawn removed after the race");
            Assert.IsFalse(flow.PlayerCar.ExternalControl, "car released after the race");

            // an interrupted chase removes its beacon and status text
            CitySession.Current.StartChase(false);
            yield return WaitFor(() => ChaseMission.Active, 10, "chase");
            Assert.IsTrue(GameObject.Find("HUV Beacon"));
            Object.Destroy(ChaseMission.Active.gameObject);
            yield return null; yield return null;
            Assert.IsFalse(GameObject.Find("HUV Beacon"), "beacon removed with the chase");
            Assert.IsFalse(ChaseMission.Active);

            // offline purchases: no duplicates
            Progress.Result? dup = null;
            Progress.BuyCar(info, ShopData.CarPrice(info), 0, r => dup = r);
            Assert.IsFalse(dup.Value.Ok, "can't buy a car you already own");

            // saves: written atomically with a backup, and the backup restores a damaged save
            P.Mito = 1234; P.Save();
            P.Mito = 5678; P.Save();
            Assert.IsTrue(File.Exists(PlayerProfile.SavePath + ".bak"), "previous save kept as .bak");
            File.WriteAllText(PlayerProfile.SavePath, "{ broken");
            var loaded = PlayerProfile.Load();
            Assert.IsNotNull(loaded, "damaged save falls back to the backup");
            Assert.AreEqual(1234, loaded.Mito, "backup holds the previous save");
            Assert.AreEqual(4, loaded.Car.PerfSlots.Length);
        }
    }
}
