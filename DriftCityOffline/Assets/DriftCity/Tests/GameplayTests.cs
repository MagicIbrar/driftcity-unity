using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using DriftCity.Game;
using DriftCity.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace DriftCity.Tests
{
    /// <summary>
    /// End-to-end gameplay checks in play mode: traffic, HUV chase, race vs AI, every menu, quests and
    /// city travel. Screenshots go to Preview/play_*.png. Needs the installed client.
    /// </summary>
    public class GameplayTests
    {
        string tempSave;

        [SetUp]
        public void SetUp()
        {
            tempSave = Path.Combine(Path.GetTempPath(), "driftcity_play_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempSave);
            PlayerProfile.SaveDirOverride = tempSave;
            PlayerProfile.Slot = 0;
            // a returning driver with money, so shops and travel are exercised directly
            var p = new PlayerProfile { Name = "Tester", Level = 12, Mito = 5_000_000 };
            p.Cars.Add(new OwnedCar { VehicleIndex = 0, Color = 2 });
            p.Save();
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

        static void Shot(string name)
        {
            var tex = UiManager.Instance.Capture(Camera.main);
            File.WriteAllBytes(Path.Combine("Preview", name), tex.EncodeToPNG());
            Debug.Log("Drift City play: wrote Preview/" + name);
        }

        static IEnumerator WaitFor(System.Func<bool> condition, float timeout, string what)
        {
            float t = 0;
            while (!condition() && t < timeout) { t += Time.unscaledDeltaTime; yield return null; }
            Assert.IsTrue(condition(), "timed out waiting for " + what);
        }

        [UnityTest]
        public IEnumerator FullGameplayLoop()
        {
            var cam = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" }.GetComponent<Camera>();
            var flow = new GameObject("Game").AddComponent<GameFlow>();
            flow.SkipParkingLot = true;   // straight to the city; ParkingLotGateEntersCity drives through the parking lot
            yield return FrontEndSteps.ToCharacterSelect(flow);
            yield return FrontEndSteps.EnterSelected();
            yield return WaitFor(() => flow.InCity && CitySession.Current, 600, "city");
            var session = CitySession.Current;
            Assert.IsNotNull(session.Traffic, "traffic system");

            // ---- traffic ----
            yield return WaitFor(() => session.Traffic.Cars.Count >= 10, 30, "traffic cars");
            var tc = session.Traffic.Cars[0];
            var before = tc.transform.position;
            yield return new WaitForSeconds(3f);
            float moved = Vector3.Distance(before, tc.transform.position);
            Debug.Log($"Drift City play: traffic cars={session.Traffic.Cars.Count}, lanes={session.Traffic.Network.Lanes.Count}, first car moved {moved:F1} m in 3 s");
            Assert.Greater(moved, 2f, "traffic moves");
            Shot("play_1_traffic.png");

            // ---- HUV chase ----
            session.StartChase(false);
            yield return WaitFor(() => ChaseMission.Active, 10, "chase start");
            var huv = Object.FindObjectsByType<TrafficCar>(FindObjectsInactive.Exclude).FirstOrDefault(c => c.name.StartsWith("HUV"));
            Assert.IsNotNull(huv, "HUV spawned");
            yield return new WaitForSeconds(1f);
            // put the camera behind the HUV for the screenshot
            cam.GetComponent<ChaseCamera>().Target = huv.transform;
            yield return new WaitForSeconds(1.5f);
            Shot("play_2_chase.png");
            cam.GetComponent<ChaseCamera>().Target = flow.PlayerCar.transform;
            long mitoBefore = PlayerProfile.Current.Mito;
            typeof(ChaseMission).GetMethod("End", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(ChaseMission.Active, new object[] { true, null, null });
            yield return null;
            Assert.Greater(PlayerProfile.Current.Mito, mitoBefore, "chase reward paid");
            Assert.AreEqual(1, PlayerProfile.Current.HuvCaught);
            yield return new WaitForSeconds(0.5f);
            CloseAllPopups();

            // ---- race ----
            var tracks = RaceMission.TracksFor(flow.City);
            Debug.Log($"Drift City play: {tracks.Count} tracks in {flow.City}");
            Assert.Greater(tracks.Count, 0, "race tracks");
            var race = new GameObject("Race").AddComponent<RaceMission>();
            typeof(RaceMission).GetMethod("Begin", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(race, new object[] { tracks[0], flow.PlayerCar, session.Traffic });
            yield return new WaitForSeconds(1.5f);
            Shot("play_3_race_grid.png");
            var ai = Object.FindObjectsByType<AiDriver>(FindObjectsInactive.Exclude);
            Assert.AreEqual(3, ai.Length, "three AI opponents");
            var aiStart = ai[0].transform.position;
            yield return new WaitForSeconds(9f);
            float aiMoved = Vector3.Distance(aiStart, ai[0].transform.position);
            Debug.Log($"Drift City play: AI moved {aiMoved:F1} m after the start, speed {ai[0].GetComponent<DriftCar>().SpeedKmh:F0} km/h");
            Assert.Greater(aiMoved, 30f, "AI drives the racing line");
            cam.GetComponent<ChaseCamera>().Target = ai[0].transform;
            yield return new WaitForSeconds(1f);
            Shot("play_4_race_ai.png");
            cam.GetComponent<ChaseCamera>().Target = flow.PlayerCar.transform;
            race.Abort();
            yield return null;

            // ---- menus ----
            Menus.Garage(); yield return null; Shot("play_5_garage.png");
            Assert.IsTrue(GameObject.Find("Garage Camera"), "garage scene while the garage is open");
            UiManager.Instance.Find(1720000).Click("ID_GARAGE_CARLIST_VIEWENTIRE"); yield return null;
            UiManager.Instance.Find(1750000).Click("ID_GARAGE_CAMERA_RIGHT"); yield return new WaitForSeconds(0.5f); Shot("play_5b_garage_floor.png");
            UiManager.Instance.Find(1690000).Click("ID_GARAGE_SELCAR_SELECT"); yield return null;
            Menus.ClassicCarShop(); yield return new WaitForSeconds(0.5f); Shot("play_6_carshop.png");
            UiManager.Instance.Find(3260000).Click("ID_AUTOSHOP_NEXTCAR"); yield return null;
            UiManager.Instance.Find(3260000).Click("ID_AUTOSHOP_ESC"); yield return null;
            Menus.Tuning(); yield return null; Shot("play_7_tuning.png");
            Menus.PaintShop(); yield return null; Shot("play_8_paint.png");
            Menus.Inventory(); yield return null;
            Menus.MyInfo(); yield return null; Shot("play_9_info.png");
            Menus.Options(); yield return null;
            Menus.CityTravel(); yield return null;
            Menus.Quests(); yield return null; Shot("play_10_quests.png");
            ListWindow.CloseOpen();
            OriginalMenus.Close();   // the options window (original dialogs) is still open under the lists
            yield return null; yield return null;
            Assert.IsFalse(OriginalMenus.IsOpen);
            Assert.IsFalse(GameObject.Find("Garage Camera"), "garage scene removed with the garage");
            Menus.WorldMap(); yield return null; Shot("play_11_map.png");
            Menus.WorldMap(); yield return null;

            // ---- quests ----
            var q = QuestTracker.All.FirstOrDefault();
            Assert.IsNotNull(q, "quests loaded");
            Debug.Log($"Drift City play: {QuestTracker.All.Count} quests; first goal {q.GoalText}");
            PlayerProfile.Current.ActiveQuests.Add(new QuestProgress { QuestId = q.Id });
            for (int i = 0; i < q.GoalCount; i++) QuestTracker.Notify(q.Goal);
            Assert.GreaterOrEqual(PlayerProfile.Current.ActiveQuests[0].Counter, q.GoalCount, "quest progress");
            CloseAllPopups();

            // ---- dealership, custom shop and reseller ----
            var P = PlayerProfile.Current;
            Debug.Log($"Drift City play: {GameTables.PlayerCars.Count} dealership cars, {PartsCatalog.All.Count} custom shop parts");
            Assert.GreaterOrEqual(GameTables.PlayerCars.Count, 100, "every supported car is in the dealership");
            var kicker = GameTables.Car(P.Car.VehicleIndex);
            Assert.GreaterOrEqual(ShopData.Grades(kicker).Count, 8, "all grades V1..V8 load for tuning");
            if (kicker.ComboBody) Assert.IsNotNull(flow.PlayerCar.transform.Find("Hood"), "stock hood fitted on a bumper-body car");

            Menus.ClassicCarShop(); yield return new WaitForSeconds(0.3f);
            UiManager.Instance.Find(3260000).Click("ID_AUTOSHOP_GRADE_9"); yield return new WaitForSeconds(0.3f);
            Shot("play_13_dealer_v9.png");
            UiManager.Instance.Find(3260000).Click("ID_AUTOSHOP_ESC"); yield return null;

            // buy wheels with a stat bonus and fit them
            var wheels = PartsCatalog.For(kicker, PartSlot.Wheels).First(w => w.Speed > 0);
            var statsBefore = ShopData.Stats(kicker, P.Car);
            long mito0 = P.Mito;
            bool? bought = null;
            Progress.BuyPart(P.CurrentCar, wheels, r => bought = r.Ok);
            Assert.IsTrue(bought == true, "wheels bought");
            Assert.AreEqual(wheels.Id, P.Car.Wheels, "bought part is fitted");
            Assert.AreEqual(mito0 - wheels.Price, P.Mito);
            Assert.AreEqual(statsBefore.speed + wheels.Speed, ShopData.Stats(kicker, P.Car).speed, "part bonus counts");

            // custom shop (original VSHOP dialogs) with a neon tried on from its product list
            Menus.CustomShop(); yield return new WaitForSeconds(0.5f);
            Assert.IsNotNull(UiManager.Instance.Find(2470000), "custom shop product list");
            Assert.IsTrue(CustomShop.IsActive, "custom shop open");
            var neon = PartsCatalog.For(kicker, PartSlot.Neon).FirstOrDefault();
            if (neon != null)
            {
                UiManager.Instance.Find(2340000).Click("ID_VSHOP_TOP_SMALL_COLOR"); yield return null;
                UiManager.Instance.Find(2380000).Click("ID_VSHOP_TAB_COLOR_UNDERNEON"); yield return null;
                // row 0 is "No neon"; row 1 the first neon
                UiManager.Instance.Find(2470000).Click("ID_VSHOP_PRODUCTLIST_FOCUS_LIST1"); yield return null;
                Assert.IsTrue(PartsCatalog.PreviewCar == P.Car && PartsCatalog.PreviewSlot == PartSlot.Neon && PartsCatalog.PreviewItem >= 0, "clicking a product tries it on");
            }
            yield return new WaitForSeconds(2f);
            Shot("play_14_customshop_neon.png");
            Assert.IsTrue(neon == null || flow.PlayerCar.transform.Find("Neon"), "neon preview shows");
            UiManager.Instance.Find(2260000).Click("ID_VSHOP_QUIT");
            yield return null; yield return null;
            Assert.IsFalse(CustomShop.IsActive, "custom shop closed");
            Assert.IsNull(PartsCatalog.PreviewCar, "leaving the shop drops the try-on");
            Assert.IsFalse(flow.PlayerCar.transform.Find("Neon"), "unbought neon is gone after leaving");
            Assert.IsFalse(flow.PlayerCar.ExternalControl, "car released after the shop");

            // an aero-kit ("s") car with its kit 2 body
            var sCar = GameTables.PlayerCars.First(c => !c.ComboBody && PartsCatalog.For(c, PartSlot.Aero).Any(a => a.Variant == 2));
            bool? boughtCar = null;
            Progress.BuyCar(sCar, ShopData.CarPrice(sCar), 1, r => boughtCar = r.Ok);
            Assert.IsTrue(boughtCar == true, "second car bought");
            var kit = PartsCatalog.For(sCar, PartSlot.Aero).First(a => a.Variant == 2);
            Progress.BuyPart(P.CurrentCar, kit, _ => { });
            Assert.AreEqual(kit.Id, P.Car.Aero);
            flow.RespawnCar(); flow.RepaintCar(); flow.ApplyCarStats();
            yield return new WaitForSeconds(2f);
            Assert.Greater(flow.PlayerCar.GetComponentsInChildren<Renderer>().Length, 3, "aero-kit car body renders");
            Shot("play_15_aerokit_car.png");

            // reseller
            Menus.ClassicCarShop(sell: true); yield return new WaitForSeconds(0.3f);
            Shot("play_16_reseller.png");
            UiManager.Instance.Find(3260000).Click("ID_AUTOSHOP_ESC"); yield return null;
            int price = ShopData.SellPrice(sCar, P.Car);
            long mito1 = P.Mito;
            bool? sold = null;
            Progress.SellCar(P.CurrentCar, price, r => sold = r.Ok);
            Assert.IsTrue(sold == true, "car sold");
            Assert.AreEqual(1, P.Cars.Count);
            Assert.AreEqual(mito1 + price, P.Mito);
            flow.RespawnCar(); flow.RepaintCar(); flow.ApplyCarStats();
            yield return new WaitForSeconds(0.5f);

            // ---- licences, parts shop, manufacturing, item use ----
            var ws = typeof(WorkshopMenus);
            System.Action<string, object[]> call = (name, args) =>
                ws.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, args);
            WorkshopMenus.Licences(); yield return null; Shot("play_17_licences.png");
            Assert.IsNotNull(UiManager.Instance.Find(4710000), "licence grid");
            UiManager.Instance.Find(4710000).Click("ID_DIALOG_NLICENSE_ITEMIMAGE_BTN1_0"); yield return null;
            Assert.IsNotNull(UiManager.Instance.Find(4720000), "licence detail");
            var hunterLic = Licences.All.First(l => l.Obtainable && l.Needs.All(n => n.stat == "HuvCaught") && !string.IsNullOrEmpty(l.Effect) && l.Effect != "n/a");
            P.HuvCaught = hunterLic.Needs.Max(n => n.value);
            Assert.IsTrue(Licences.Met(P, hunterLic), "licence conditions met");
            call("ClaimLicence", new object[] { hunterLic }); CloseAllPopups();
            Assert.Contains(hunterLic.Id, P.Licences, "licence earned");
            call("MountLicence", new object[] { hunterLic });
            Assert.AreEqual(hunterLic.Id, P.MountedLicence);
            Assert.Greater(Licences.Bonus(P, hunterLic.Effect), 0, "mounted licence gives its bonus");

            var stats0 = ShopData.Stats(GameTables.Car(P.Car.VehicleIndex), P.Car);
            var partA = new PerfPart { Uid = P.NextPartUid++, Type = 0, Grade = 0 };
            var partB = new PerfPart { Uid = P.NextPartUid++, Type = 0, Grade = 0 };
            P.PerfParts.Add(partA); P.PerfParts.Add(partB);
            P.Car.PerfSlots[0] = partA.Uid;
            Assert.AreEqual(stats0.speed + PerfRules.Points(partA), ShopData.Stats(GameTables.Car(P.Car.VehicleIndex), P.Car).speed, "fitted part adds speed");
            P.Give("i_n_00003", 2);
            call("Enhance", new object[] { partA, "i_n_00003", PerfRules.EnhanceFee(partA, 0) }); CloseAllPopups();
            Assert.AreEqual(1, partA.Enhance, "first enhance always succeeds");
            Assert.AreEqual(1, P.MaxEnhance);
            P.Car.PerfSlots[0] = -1;
            P.Give("i_n_Seed_N", 1);
            Random.InitState(7);
            call("Combine", new object[] { partA, partB, "i_n_Seed_N" }); CloseAllPopups();
            Assert.AreEqual(0, P.Count("i_n_Seed_N"), "seed used");
            Assert.IsTrue(P.PerfParts.Count == 1, "combine leaves one part (merged or survivor)");
            call("Grind", new object[] { P.PerfParts[0] }); CloseAllPopups();
            Assert.AreEqual(0, P.PerfParts.Count);
            Assert.Greater(P.Items.Where(i => i.Id.StartsWith("mitrondust_")).Sum(i => i.Count), 0, "grinding gives dust");
            WorkshopMenus.PartsShop(); yield return null; Shot("play_18_partsshop.png");
            Assert.IsNotNull(UiManager.Instance.Find(1250000), "parts shop opens on its buy tab");
            UiManager.Instance.Find(1200000).Click("ID_PARTSSHOP_TAB_UPGRADEIMG"); yield return null;
            Assert.IsNotNull(UiManager.Instance.Find(1240000), "upgrade tab");
            Assert.IsNull(UiManager.Instance.Find(1250000), "one tab at a time");

            var crystal = Crafting.Recipes.First(r => r.Result == "mitron_cristal" && r.Inputs.Count == 1 && r.Inputs[0].id.StartsWith("mitrondust_"));
            P.SkillBooks.Add(crystal.Id);
            P.Give(crystal.Inputs[0].id, crystal.Inputs[0].count); P.Give(crystal.Kit, 1);
            long craftExp = P.CraftExp;
            call("Craft", new object[] { crystal }); CloseAllPopups();
            Assert.Greater(P.CraftExp, craftExp, "manufacturing gives EXP either way");
            Assert.AreEqual(0, P.Count(crystal.Kit), "kit used");
            WorkshopMenus.Manufacturing(); yield return null; Shot("play_19_manufacturing.png");
            Assert.IsNotNull(UiManager.Instance.Find(4920000), "manufacturing list");
            Assert.IsNull(UiManager.Instance.Find(1200000), "the parts shop closed for it");

            P.Give("i_n_roof_jackbox", 1);
            call("UseItem", new object[] { "i_n_roof_jackbox" }); CloseAllPopups();
            yield return new WaitForSeconds(0.5f);
            Assert.AreEqual(10890, P.Car.Roof, "roof box gives the roof item");
            ListWindow.CloseOpen();
            OriginalMenus.Close();   // the manufacturing window (original dialogs)
            yield return null;

            // ---- decal, plate and tint on the car in the city ----
            var me = GameTables.Car(P.Car.VehicleIndex);
            foreach (var slot in new[] { PartSlot.Decal, PartSlot.Plate, PartSlot.Tint, PartSlot.Wing })
            {
                var part = PartsCatalog.For(me, slot).FirstOrDefault(x => !x.Stock);
                if (part == null) continue;
                Progress.BuyPart(P.CurrentCar, part, _ => { });
            }
            flow.RespawnCar(); flow.RepaintCar(); flow.ApplyCarStats();
            yield return new WaitForSeconds(1.5f);
            Assert.IsTrue(flow.PlayerCar.GetComponentsInChildren<Renderer>().Any(r => r.sharedMaterial && r.sharedMaterial.GetTexture("_MainTex") is RenderTexture), "decal paint on the player car");
            Assert.IsTrue(flow.PlayerCar.GetComponentsInChildren<Transform>(true).Any(t => t.name == "Roof"), "roof item on the player car");
            var chaseCam = cam.GetComponent<ChaseCamera>();
            chaseCam.Showroom = true;
            yield return new WaitForSeconds(2.5f);
            Shot("play_20_dressed_in_city.png");
            chaseCam.Showroom = false;

            // ---- upgrade + respawn ----
            PlayerProfile.Current.Car.Grade = 1;
            flow.ApplyCarStats();
            flow.RespawnCar();
            yield return new WaitForSeconds(1f);
            Assert.IsNotNull(flow.PlayerCar);

            // ---- travel ----
            var oldSession = CitySession.Current;
            flow.TravelTo("moonpalace");
            Assert.IsFalse(flow.InCity, "travel leaves the city immediately");
            yield return WaitFor(() => flow.InCity && CitySession.Current && CitySession.Current != oldSession && flow.PlayerCar, 600, "moonpalace");
            yield return new WaitForSeconds(2.5f);
            int grounded = flow.PlayerCar.Wheels.Count(w => w && w.isGrounded);
            Debug.Log($"Drift City play: moonpalace car at {flow.PlayerCar.transform.position}, grounded {grounded}/4");
            Assert.GreaterOrEqual(grounded, 3, "car on the road in moonpalace");
            Shot("play_12_moonpalace.png");
        }

        static void CloseAllPopups()
        {
            var ui = UiManager.Instance;
            foreach (var v in ui.OpenViews.ToList()) if (v && v.Dialog.Id == 3250000) ui.Close(v);
        }
    }
}
