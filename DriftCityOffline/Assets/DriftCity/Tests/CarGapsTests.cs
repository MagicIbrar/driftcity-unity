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
    /// The last car gaps (DriftCity_RE/PHYSICS.md sections 13-14, INVENTORY.md): assist options from option / set parts,
    /// the NDLG parts page's second row and option slots, the performance tips, Battle Zone multipliers, battle item
    /// effects, durability, the jump camera lift, per-car tank / mileage, the simple-model LOD, custom marks and the licence
    /// pages. Screenshots: Preview/car5_*.png.
    /// </summary>
    public class CarGapsTests
    {
        string tempSave;
        bool classicBefore;

        [SetUp]
        public void SetUp()
        {
            classicBefore = InventoryWindow.Classic;
            tempSave = Path.Combine(Path.GetTempPath(), "driftcity_car5_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempSave);
            PlayerProfile.SaveDirOverride = tempSave;
            PlayerProfile.Slot = 0;
            var p = new PlayerProfile { Name = "GapTester", Level = 60, Mito = 1_000_000 };
            p.Cars.Add(new OwnedCar { VehicleIndex = 0, Color = 2 });
            p.PerfParts.Add(new PerfPart { Uid = 1, Type = 0, Grade = 1, Enhance = 0 });
            p.PerfParts.Add(new PerfPart { Uid = 2, Type = 0, Grade = 2, Enhance = 3 });
            p.PerfParts.Add(new PerfPart { Uid = 3, Type = 3, Grade = 0, Enhance = 0 });
            p.NextPartUid = 4;
            p.Cars[0].PerfSlots[0] = 1;
            p.Cars[0].PerfSlots[3] = 3;
            p.Save();
            PlayerProfile.Current = null;
            MittronFuel.Reset();
            MittronFuel.Enabled = true;   // CarGapsInCity turns fuel off for its drive runs
            CustomMarks.ResetCache();
            Directory.CreateDirectory("Preview");
        }

        [TearDown]
        public void TearDown()
        {
            InventoryWindow.Classic = classicBefore;
            PlayerProfile.SaveDirOverride = null;
            MittronFuel.Reset();
            MittronFuel.Enabled = true;   // CarGapsInCity turns fuel off for its drive runs
            CustomMarks.ResetCache();
            foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Exclude).Where(g => g.transform.parent == null)) Object.Destroy(go);
            try { Directory.Delete(tempSave, true); } catch { }
        }

        static void Shot(string name)
        {
            var tex = UiManager.Instance.Capture(Camera.main);
            File.WriteAllBytes(Path.Combine("Preview", name), tex.EncodeToPNG());
            Debug.Log("Drift City car gaps: wrote Preview/" + name);
        }

        static IEnumerator WaitFor(System.Func<bool> condition, float timeout, string what)
        {
            float t = 0;
            while (!condition())
            {
                t += Time.unscaledDeltaTime;
                if (t > timeout) Assert.Fail("timed out waiting for " + what);
                yield return null;
            }
        }

        static void Dismiss()
        {
            var v = UiManager.Instance ? UiManager.Instance.Find(3250000) : null;
            if (!v) return;
            foreach (var b in new[] { "ID_POPUP_OKONLY", UiPopup.Cancel, UiPopup.No, UiPopup.Deny }) if (v && v.Control(b) && v.Control(b).gameObject.activeSelf) { v.Click(b); break; }
        }

        static object Call(object target, string method, params object[] args) =>
            target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Invoke(target, args);

        /// <summary>A w x h PNG of noise or one colour.</summary>
        static byte[] Png(int w, int h, bool noise)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var px = new Color32[w * h];
            var rnd = new System.Random(5);
            for (int i = 0; i < px.Length; i++)
                px[i] = noise ? new Color32((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256), 255)
                              : (i % w < w / 2 ? new Color32(255, 140, 0, 255) : new Color32(20, 60, 230, 255));
            t.SetPixels32(px);
            t.Apply();
            var b = t.EncodeToPNG();
            Object.Destroy(t);
            return b;
        }

        [UnityTest]
        public IEnumerator CarGapsInCity()
        {
            new GameObject("Main Camera", typeof(Camera), typeof(AudioListener), typeof(ChaseCamera)) { tag = "MainCamera" };
            var flow = new GameObject("Game").AddComponent<GameFlow>();
            flow.SkipParkingLot = true;
            yield return FrontEndSteps.ToCharacterSelect(flow);
            yield return FrontEndSteps.EnterSelected();
            yield return WaitFor(() => flow.InCity && CitySession.Current && flow.PlayerCar, 600, "city");
            yield return new WaitForSeconds(1f);
            var ui = UiManager.Instance;
            var P = PlayerProfile.Current;
            InventoryWindow.Classic = false;

            // ================= tables: assists, option parts =================
            var qstart = AssistTable.Get("a_00002");
            Assert.IsNotNull(qstart, "Assist_Client.tdf");
            Assert.AreEqual(2, qstart.Kind, "qstart = 2 (0x6c58e0)");
            var a = new CarAssists();
            a.Add(AssistTable.Get("a_00036"));   // Crash Saving 1: 3 %
            a.Add(AssistTable.Get("a_00075"));   // Over Turbo 5: 0.5 s
            a.Add(qstart);
            Assert.AreEqual(0.97f, a.CrashLoss, 1e-4f);
            Assert.AreEqual(0.5f, a.BoostTimeAdd, 1e-4f);
            Assert.IsTrue(a.TurboStart);
            var launch = CarParts.Option("op_B_0006");
            Assert.IsNotNull(launch, "ItemClient.tdf option rows");
            Assert.AreEqual(3, launch.Stat, "op_B docks on booster parts");
            Assert.AreEqual("a_00002", launch.Assist);
            var accelPlus = CarParts.All.Values.First(o => o.Kind == "op_A" && o.Points > 0 && string.IsNullOrEmpty(o.Assist) && o.Level <= 60);
            var tankOpt = CarParts.All.Values.FirstOrDefault(o => AssistTable.Get(o.Assist)?.Kind == 5 && o.Level <= 60);
            var speedOpt = CarParts.All.Values.First(o => o.Kind == "op_S" && AssistTable.Get(o.Assist)?.Kind == 0xe && o.Level <= 60);

            // ================= second row and option slots (offline rules) =================
            var car = P.Car;
            int[] before = CarParts.Points(P, car);
            Assert.IsTrue(CarParts.FitRow2Rule(P, 0, 2).Ok, "a speed part into row 2");
            Assert.IsFalse(CarParts.FitRow2Rule(P, 0, 1).Ok, "a part fitted in row 1 is refused");
            P.Give(launch.Id, 1);
            P.Give(accelPlus.Id, 1);
            P.Give(speedOpt.Id, 1);
            Assert.IsFalse(CarParts.FitOptionRule(P, 0, CarParts.Index(1, 0), accelPlus.Id).Ok, "no accel part under the accel option slot");
            Assert.IsFalse(CarParts.FitOptionRule(P, 0, CarParts.Index(0, 0), launch.Id).Ok, "a booster option on a speed slot is refused");
            Assert.IsTrue(CarParts.FitOptionRule(P, 0, CarParts.Index(3, 0), launch.Id).Ok, "Turbo Launch on the booster part");
            Assert.IsTrue(CarParts.FitOptionRule(P, 0, CarParts.Index(0, 1), speedOpt.Id).Ok, "Engine Speeder on the row-2 speed part");
            Assert.AreEqual(0, P.Count(launch.Id), "the option left the bag");
            int[] after = CarParts.Points(P, car);
            Assert.AreEqual(before[0] + PerfRules.Points(P.PerfParts.First(x => x.Uid == 2)), after[0], "row 2 adds its part's points");
            Assert.IsFalse(ItemCatalog.Bag(P).Any(e => e.Kind == BagKind.Part && e.Part.Uid == 2), "the row-2 part left the bag");
            var assists = CarAssists.For(P, car);
            Assert.IsTrue(assists.TurboStart, "the option part's assist is active");
            Assert.Greater(assists.SpeedKmh, 0f, "speedup from the Engine Speeder");
            float cap0 = MittronFuel.CapacityOf(car);
            if (tankOpt != null)
            {
                P.PerfParts.Add(new PerfPart { Uid = 9, Type = tankOpt.Stat < 0 ? 2 : tankOpt.Stat, Grade = 0 });
                if (tankOpt.Stat < 0 || tankOpt.Stat == 2) car.PerfSlots[2] = 9; else car.PerfSlots[tankOpt.Stat] = 9;
                P.Give(tankOpt.Id, 1);
                int slot = CarParts.Index(tankOpt.Stat < 0 ? 2 : tankOpt.Stat, 0);
                Assert.IsTrue(CarParts.FitOptionRule(P, 0, slot, tankOpt.Id).Ok, tankOpt.Id);
                Assert.Greater(MittronFuel.CapacityOf(car), cap0, "\"gage\" enlarges the tank (0x47fde0)");
                Debug.Log($"Drift City car gaps: tank {cap0} L -> {MittronFuel.CapacityOf(car)} L with {tankOpt.Id}");
            }
            Assert.AreEqual(100f, cap0, 1e-3f, "the default tank");
            Assert.AreEqual(5f, MittronFuel.MileageOf(new OwnedCar()), 1e-3f, "5 km/L");

            // ================= the NDLG parts page: row 2, option icons, tips =================
            Menus.Inventory();
            yield return null; yield return null;
            Assert.IsTrue(InventoryWindow.IsNdlg && ui.Find(4810000), "NDLG parts page");
            var parts = ui.Find(4810000);
            Assert.IsTrue(parts.Control("ID_NDLGINVEN_SLOTN_02").transform.Find("item"), "row 2 shows its part");
            Assert.IsTrue(parts.Control("ID_NDLGINVEN_OPTSLOTN_07").transform.Find("item"), "the booster option slot shows Turbo Launch");
            Assert.IsTrue(parts.Control("ID_NDLGINVEN_OPTSLOTN_02").transform.Find("item"), "the row-2 speed option slot shows the speeder");
            InventoryWindow.ShowTip(0, (RectTransform)ui.Find(4780000).Control("ID_NDLGINVEN_ATTR_BTN_SPEED").transform);
            yield return null;
            Assert.IsTrue(ui.Find(4790000), "the tip box 4790000");
            StringAssert.Contains("Max Speed", ui.Find(4790000).Control("ID_NDLGINVEN_TIP_TITLE").GetComponentInChildren<UnityEngine.UI.Text>(true).text);
            Shot("car5_parts_row2_options_tip.png");
            InventoryWindow.HideTip();
            Assert.IsFalse(ui.Find(4790000));

            // ================= custom mark: import a file, the sticker page, the car's paint =================
            string file = Path.Combine(CustomMarks.Folder, "test_mark.png");
            File.WriteAllBytes(file, Png(300, 200, false));
            Assert.IsNull(CustomMarks.Convert(Path.Combine(CustomMarks.Folder, "nope.png"), out string err1));
            Assert.IsNotNull(err1);
            File.WriteAllBytes(Path.Combine(CustomMarks.Folder, "bad.gif"), new byte[] { 1, 2, 3 });
            Assert.IsNull(CustomMarks.Convert(Path.Combine(CustomMarks.Folder, "bad.gif"), out string err2), "only PNG / JPG");
            string noisy = CustomMarks.ConvertBytes(Png(512, 512, true), out string err3);
            Assert.IsTrue(noisy == null || System.Convert.FromBase64String(noisy).Length <= CustomMarks.MaxPngBytes, "noise is shrunk or refused: " + err3);
            bool imported = false;
            CustomMarks.Import(file, (ok, e) => { imported = ok; Assert.IsTrue(ok, e); });
            Assert.IsTrue(imported);
            var mark = CustomMarks.Own();
            Assert.IsTrue(mark && mark.width == CustomMarks.Size && mark.height == CustomMarks.Size, "fitted into 128 x 128");
            Assert.IsTrue(System.Convert.FromBase64String(P.CustomMark).Length <= CustomMarks.MaxPngBytes);
            InventoryWindow.ShowPage(InventoryWindow.Page.Sticker);
            yield return new WaitForSeconds(0.5f);
            Shot("car5_sticker_page_mark.png");
            InventoryWindow.Close();
            yield return null;

            // ================= licence pages =================
            var lic = Licences.All.FirstOrDefault(l => l.Obtainable);
            if (lic != null) { P.Licences.Add(lic.Id); P.MountedLicence = lic.Id; }
            LicencePages.ShowOther(P.Name);
            yield return null;
            Assert.IsTrue(ui.Find(LicencePages.OtherBase) && ui.Find(LicencePages.LicencePage) && ui.Find(LicencePages.CardDialog), "OTHERLISENCEBASE + the licence page + the LICENSE card");
            var card = ui.Find(LicencePages.CardDialog);
            Assert.AreEqual(P.Name, card.Control("ID_LICENSE_ID").GetComponentInChildren<UnityEngine.UI.Text>(true).text);
            Assert.IsTrue(card.Control($"ID_LICENSE_CHAR{P.Avatar - 1:00}").gameObject.activeSelf, "the driver's portrait");
            StringAssert.Contains("-", card.Control("ID_LICENSE_ISSUEDATE").GetComponentInChildren<UnityEngine.UI.Text>(true).text);
            Shot("car5_licence_pages.png");
            LicencePages.CloseOther();
            Assert.IsFalse(ui.Find(LicencePages.OtherBase) || ui.Find(LicencePages.CardDialog));

            // ================= physics on a flat pad far below the city =================
            var drive = flow.PlayerCar;
            var rb = drive.GetComponent<Rigidbody>();
            if (CitySession.Current.Traffic) CitySession.Current.Traffic.MaxCars = 0;
            var pad = GameObject.CreatePrimitive(PrimitiveType.Cube);
            pad.name = "Test Pad";
            pad.transform.position = new Vector3(0, -5000.5f, 0);
            pad.transform.localScale = new Vector3(6000, 1, 6000);
            void Place(Vector3 at, float yaw, float kmh = 0f)
            {
                var rot = Quaternion.Euler(0, yaw, 0);
                rb.position = at; rb.rotation = rot;
                drive.transform.SetPositionAndRotation(at, rot);
                rb.linearVelocity = rot * Vector3.forward * (kmh / 3.6f); rb.angularVelocity = Vector3.zero;
                drive.SetSpawn(at, rot);
            }
            var roadState = drive.GetComponent<CarRoadState>();
            if (roadState) roadState.enabled = false;
            MittronFuel.Enabled = false;
            drive.ExternalControl = true;
            float dt = Time.fixedDeltaTime;
            IEnumerator Run(float seconds, float throttle, float steer = 0f, bool drift = false, bool boost = false)
            {
                for (float t = 0; t < seconds; t += dt) { drive.ApplyControls(throttle, steer, drift, dt, boost); yield return new WaitForFixedUpdate(); }
            }
            flow.ApplyCarStats();
            Assert.IsTrue(drive.Assists.TurboStart, "the player's car took its assists with ApplyCarStats");

            // ---- Turbo Start: drift tapped, then the forward key while standing -> 1 s mini boost ----
            Place(new Vector3(-2500, -4999.4f, -2500), 45);
            yield return Run(1f, 0f);
            drive.Assists = new CarAssists();
            yield return Run(1f, 1f);
            float plain = drive.SpeedKmh;
            Place(new Vector3(-2400, -4999.4f, -2500), 45);
            yield return Run(1f, 0f);
            var withStart = new CarAssists(); withStart.Add(qstart);
            drive.Assists = withStart;
            yield return Run(0.1f, 0f, 0f, true);
            drive.ApplyControls(1f, 0f, false, dt);
            Assert.Greater(drive.MiniBoostTimeLeft, 0.5f, "the mini boost started (in[0x21b] = 1 s)");
            yield return Run(1f - dt, 1f);
            float launched = drive.SpeedKmh;
            Debug.Log($"Drift City car gaps: 1 s from standstill {plain:F1} km/h plain, {launched:F1} km/h with Turbo Start");
            Assert.Greater(launched, plain * 1.3f, "the boost force acts during the mini boost");

            // ---- speed add: the limiter holds off 0.8 x the km/h more ----
            drive.Assists = new CarAssists();
            float top = drive.TopSpeedKmh;
            Place(new Vector3(-2000, -4999.4f, -2000), 45, top + 8f);
            yield return Run(1.5f, 1f);
            float limited = drive.SpeedKmh;
            var fast = new CarAssists(); fast.SpeedKmh = 25f;
            drive.Assists = fast;
            Place(new Vector3(-1900, -4999.4f, -2000), 45, top + 8f);
            yield return Run(1.5f, 1f);
            Debug.Log($"Drift City car gaps: {top + 8f:F0} km/h start, top {top:F0}: {limited:F1} plain, {drive.SpeedKmh:F1} with +25 km/h speedup");
            Assert.Less(limited, top + 3f, "the limiter pulls the car down to its top speed");
            Assert.Greater(drive.SpeedKmh, top + 7f, "+25 km/h x 0.8 = 20 km/h more top speed");

            // ---- Battle Zone: the gauge multiplier and the Boost Recharger's fill rate ----
            drive.Assists = new CarAssists();
            Place(new Vector3(-1500, -4999.4f, -1500), 45);
            drive.BoostGauge = 0f;
            yield return Run(2f, 0f);
            float field = drive.BoostGauge;
            drive.PvpMode = true; drive.PvpGaugeGain = 1f; drive.PvpFillRate = 2f;
            drive.BoostGauge = 0f;
            yield return Run(2f, 0f);
            float battle = drive.BoostGauge;
            drive.PvpMode = false; drive.PvpFillRate = 1f;
            Debug.Log($"Drift City car gaps: gauge after 2 s {field:F3} field, {battle:F3} Battle Zone with the recharger");
            Assert.AreEqual(field * 2f, battle, field * 0.15f, "+0x1abc = 2 doubles the fill");

            // ---- the Crash Capsule: +5 gauge per traffic hit (0x47e510) ----
            drive.BoostGauge = 0.2f;
            BattleItemEffects.Ensure().Apply(drive, "crash", 20f);
            Assert.IsTrue(drive.CrashCapsule);
            Call(drive, "TrafficHit");
            Assert.AreEqual(0.25f, drive.BoostGauge, 1e-3f, "+5 of 100");

            // ---- Emergency Boost Kit: a boost now with the current gauge ----
            drive.BoostGauge = 0.5f;
            Place(new Vector3(-1000, -4999.4f, -1000), 45, 60f);
            BattleItemEffects.Instance.Apply(drive, "eboost", 0f);
            yield return Run(0.5f, 1f);
            Assert.IsTrue(drive.Boosting, "the boost runs (0.2 s delay, then the used gauge's share)");
            yield return Run(3f, 0f);

            // ---- Invincible Rush: no speed loss, no durability loss; durability from a wall hit otherwise ----
            drive.Repair(1f);
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = "Test Wall";
            wall.transform.position = new Vector3(-500, -4998f, -440);
            wall.transform.localScale = new Vector3(60, 6, 2);
            Place(new Vector3(-500, -4999.4f, -470), 0f, 120f);
            yield return Run(1.2f, 1f);
            float dura = drive.Durability;
            Debug.Log($"Drift City car gaps: durability after a 120 km/h wall hit {dura:F3}");
            Assert.Less(dura, 1f, "a wall hit costs durability (remake rule: v / vmax x Crash 4005 / 1000)");
            drive.Repair(1f);
            BattleItemEffects.Instance.Apply(drive, "invincible", 10f);
            Assert.Greater(drive.InvincibleTime, 9f);
            Place(new Vector3(-500, -4999.4f, -470), 0f, 120f);
            yield return Run(1.2f, 1f);
            Assert.AreEqual(1f, drive.Durability, 1e-4f, "Invincible Rush: hits cost nothing");
            BattleItemEffects.Instance.EndAll();
            drive.InvincibleTime = 0f;
            Object.Destroy(wall);

            // ---- the jump camera lift: a long fall raises the camera, landing zeroes it ----
            var cam = Camera.main.GetComponent<ChaseCamera>();
            cam.Target = drive.transform;
            Place(new Vector3(0, -4960f, 0), 0f, 50f);
            float maxLift = 0f;
            for (float t = 0; t < 2.6f; t += dt)
            {
                drive.ApplyControls(0.5f, 0f, false, dt);
                yield return new WaitForFixedUpdate();
                maxLift = Mathf.Max(maxLift, cam.JumpLift);
            }
            yield return Run(1.5f, 0f);
            yield return null;
            Debug.Log($"Drift City car gaps: jump lift peak {maxLift:F2} m, after landing {cam.JumpLift:F2} m");
            Assert.Greater(maxLift, 0.3f, "over a jump longer than 0.6 s the camera rises");
            Assert.AreEqual(0f, cam.JumpLift, 1e-4f, "landing zeroes the lift");

            // ---- the simple model as distance LOD ----
            var lod = drive.GetComponent<LODGroup>();
            Assert.IsTrue(lod && lod.lodCount == 2, "LOD0 full car, LOD1 the _s simple model");
            Assert.IsTrue(drive.transform.Find("Simple Model"), "vehicle\\player\\<model>_s.nif loaded");
            Dismiss();
        }
    }
}
