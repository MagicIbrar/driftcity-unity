using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DriftCity.Game;
using DriftCity.Net;
using DriftCity.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace DriftCity.Tests
{
    /// <summary>
    /// The UI-table banners wired this round (GameEffects: instant missions, the party patrol alert and count, part
    /// modification / conversion, the Channel Battle 3-2-1, Mittron Fireworks, the repair girl, the event effects 57-59),
    /// the calendar multipliers (Multipliers: golden time, Santa, the 3rd anniversary) with the HUD badge strip 3030000, and
    /// the custom shop's period prices (ShopPeriods, the checkout's period buttons). Screenshots: Preview/fx5_*.png.
    /// </summary>
    public class BannersPeriodsTests
    {
        string tempSave;

        static void Mount()
        {
            var root = DriftCityData.ResolveGameRoot(@"D:\Drift_City_Remastered_Alpha_08-2025\Drift City Remastered Alpha");
            Assert.IsNotNull(root, "installed client or imported data found");
            if (DriftCityData.Files == null) DriftCityData.Mount(root);
        }

        static void Shot(string name)
        {
            var tex = UiManager.Instance.Capture(Camera.main);
            File.WriteAllBytes(Path.Combine("Preview", name), tex.EncodeToPNG());
            Debug.Log("Drift City banners: wrote Preview/" + name);
        }

        static IEnumerator WaitFor(Func<bool> condition, float timeout, string what)
        {
            float t = 0;
            while (!condition() && t < timeout) { t += Time.unscaledDeltaTime; yield return null; }
            Assert.IsTrue(condition(), "timed out waiting for " + what);
        }

        [TearDown]
        public void TearDown()
        {
            GameEffects.Clear();
            GameEffects.Sounds = true;
            Multipliers.GoldenOverride = false;   // the other tests run with the calendar bonuses off (TestDefaults)
            Multipliers.Now = null;
            SeasonEvents.Override = null;
            PlayerProfile.SaveDirOverride = null;
            foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Exclude).Where(g => g.transform.parent == null && g.name != "Bonus Badges" && g.name != "Shop Periods")) Object.Destroy(go);
            if (tempSave != null) try { Directory.Delete(tempSave, true); } catch { }
            tempSave = null;
        }

        PlayerProfile TempProfile(long mito)
        {
            tempSave = Path.Combine(Path.GetTempPath(), "driftcity_fx5_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempSave);
            PlayerProfile.SaveDirOverride = tempSave;
            PlayerProfile.Slot = 0;
            var p = new PlayerProfile { Name = "Fx5Tester", Level = 20, Mito = mito };
            p.Cars.Add(new OwnedCar { VehicleIndex = 0, Color = 2 });
            PlayerProfile.Current = p;
            return p;
        }

        [UnityTest]
        public IEnumerator BannersOfTheGameScreens()
        {
            Mount();
            GameEffects.Sounds = false;
            GameEffects.Enabled = true;
            Assert.IsNotNull(GameEffects.InstantMission(true), "im_success");
            Assert.IsTrue(GameEffects.ScreenPlaying("im_success"));
            Assert.IsNull(GameEffects.InstantMission(false), "im_failed waits behind im_success (0x69b900's exclusive set)");
            GameEffects.Clear();

            var alert = GameEffects.PatrolAlert(true);
            Assert.IsNotNull(alert, "party_soonchal");
            Assert.AreEqual(alert.Duration / 2.5f, alert.Speed, 1e-3f, "squeezed into 2.5 s (0x40200000)");
            Assert.IsNull(GameEffects.PatrolAlert(), "not restarted while it plays");
            var count = GameEffects.PatrolCount();
            Assert.IsNotNull(count, "pvp_tencount");
            Assert.AreEqual(count.Duration / 10f, count.Speed, 1e-3f, "10 s");
            GameEffects.PatrolCountStop();
            yield return null;
            Assert.IsFalse(GameEffects.ScreenPlaying("pvp_tencount"), "taken off when time was added");

            foreach (var (code, name) in new[] { (GameEffects.UpgradeSuccess, "ui_upgrade_s"), (GameEffects.UpgradeFail, "ui_upgrade_f"), (GameEffects.UpgradeDestroyed, "ui_upgrade_d") })
            {
                Assert.IsNotNull(GameEffects.Upgrade(code), name);
                Assert.IsTrue(GameEffects.ScreenPlaying(name), name + " plays");
            }
            Assert.IsNotNull(GameEffects.Converted(), "transform_success");
            Assert.IsNotNull(GameEffects.ChannelRound(), "channel_count");
            Assert.IsNotNull(GameEffects.MittronFirework(), "mitbomb_ui");
            GameEffects.RepairGirl();
            Assert.IsTrue(GameEffects.ScreenPlaying("pvp_repair_girl_ui"), "pvp_repair_girl_ui");
            Assert.IsNotNull(GameEffects.XmasDecoration(), "57 X_tree001_button");
            Assert.IsNotNull(GameEffects.ChocolateWrapped(), "58 valen_choco_fx_01");
            Assert.IsNotNull(GameEffects.HarvestBoxMade(), "59 goodharvest_fx_01");
            // the repair girl's car NIFs (entries 44 / 47 / 48 / 49's second names) exist in the data
            foreach (var n in new[] { "pvp_repair_girl", "pvp_repair_girl_plane", "i_d_repair02_5s_left", "i_d_repair02_5s_right", "mitbomb_car" })
                Assert.IsTrue(DriftCityData.Files.Exists($@"effects\ui\{n}.nif"), n + ".nif");
            yield return null;
        }

        [UnityTest]
        public IEnumerator CalendarMultipliers()
        {
            Mount();
            Assert.AreEqual((20, 22), Multipliers.ParseHours("20-22"));
            Assert.AreEqual((-1, -1), Multipliers.ParseHours("off"));
            Assert.IsTrue(Multipliers.InHours(1, Multipliers.ParseHours("22-2")) && !Multipliers.InHours(2, Multipliers.ParseHours("22-2")));
            Multipliers.Now = new DateTime(2026, 12, 24, 12, 0, 0, DateTimeKind.Utc);
            Multipliers.GoldenOverride = true;
            SeasonEvents.Override = EventSeason.Christmas | EventSeason.Anniversary;
            var race = Multipliers.For("race");
            Assert.AreEqual(50 + 50 + 200, race.Exp, "golden + Santa 50 % (Dec 24) + anniversary");
            Assert.AreEqual(50 + 200, race.Mito, "Santa is EXP only");
            Assert.AreEqual(Multipliers.FlagGolden | Multipliers.FlagSanta50 | Multipliers.FlagAnniversary, race.Flags);
            Assert.AreEqual(200, Multipliers.For("quest").Exp, "quests: the anniversary only");
            Assert.AreEqual(0, Multipliers.For("combo").Exp, "no calendar on the combo drop");
            Multipliers.Now = new DateTime(2026, 12, 10, 12, 0, 0, DateTimeKind.Utc);
            Assert.AreEqual(Multipliers.FlagSanta25, Multipliers.For("chase").Flags & (Multipliers.FlagSanta25 | Multipliers.FlagSanta50), "Santa 25 % in the season");
            Assert.AreEqual(Multipliers.FlagGolden | Multipliers.FlagSanta25 | Multipliers.FlagAnniversary, Pvp.BonusFlags(TempProfile(0)) & 0xe08, "the result row's flags");

            // offline Progress.Reward adds them (golden 50 + Santa 25 + anniversary 200 on a chase's EXP)
            SeasonEvents.Override = EventSeason.Christmas | EventSeason.Anniversary;
            var p = PlayerProfile.Current;
            p.Level = 30;
            Progress.Result got = default;
            bool done = false;
            Progress.Reward("chase", new Dictionary<string, object>(), () => { p.AddExp(1000); p.Mito += 1000; return new Progress.Result { Ok = true, Exp = 1000, Mito = 1000 }; }, r => { got = r; done = true; });
            Assert.IsTrue(done);
            int trees = SeasonEvents.XmasBonusPct;
            Assert.AreEqual(1000 + 1000 * (275 + trees) / 100, got.Exp, "EXP +275 % (+ any finished trees)");
            Assert.AreEqual(1000 + 1000 * (250 + trees) / 100, got.Mito, "Mito +250 %");
            yield return null;
        }

        [UnityTest]
        public IEnumerator PeriodPricesOffline()
        {
            Mount();
            var wing = PartsCatalog.Get(10182);   // TB wing: 7 D 5,000 / 30 D 15,000 (vshopitem)
            Assert.IsNotNull(wing, "TB wing in the parts table");
            var options = ShopPeriods.ForPart(wing);
            CollectionAssert.AreEqual(new[] { 7, 30, 0 }, options.Select(x => x.Days).ToArray(), "7 D, 30 D, unlimited");
            CollectionAssert.AreEqual(new long[] { 5000, 15000, 45000 }, options.Select(x => x.Price).ToArray(), "the table's prices, unlimited 3 x 30 D");
            StringAssert.Contains("7", ShopPeriods.Button(options[0]), "10280 \"%d D\"");
            var paint = ShopData.Paints.First();
            Assert.AreEqual(1, ShopPeriods.For(new ShopProduct { Paint = paint }).Count, "paints: unlimited only");

            var p = TempProfile(100000);
            bool done = false; Progress.Result res = default;
            ShopPeriods.Buy(0, wing, 7, r => { res = r; done = true; });
            Assert.IsTrue(done && res.Ok, res.Error);
            var car = p.Cars[0];
            Assert.AreEqual(100000 - 5000, p.Mito);
            Assert.IsTrue(ShopPeriods.IsTimed(car, wing.Id) && car.GiftParts.Contains(wing.Id) && car.Wing == wing.Id, "a fitted period part (no resale value)");
            long until = ShopPeriods.Until(car, wing.Id);
            done = false;
            ShopPeriods.Buy(0, wing, 30, r => { res = r; done = true; });
            Assert.IsTrue(done && res.Ok, res.Error);
            Assert.AreEqual(until + 30 * 86400000L, ShopPeriods.Until(car, wing.Id), "the days are added (11044)");
            Assert.AreEqual(37, ShopPeriods.DaysLeft(car, wing.Id, ItemUse.Now));
            var gone = ShopPeriods.Sweep(p, ShopPeriods.Until(car, wing.Id));
            CollectionAssert.AreEqual(new[] { wing.Name }, gone);
            Assert.IsFalse(car.Parts.Contains(wing.Id) || car.Wing == wing.Id, "expired: off the car");
            done = false;
            ShopPeriods.Buy(0, wing, 0, r => { res = r; done = true; });
            Assert.IsTrue(done && res.Ok, res.Error);
            Assert.IsTrue(car.Parts.Contains(wing.Id) && !ShopPeriods.IsTimed(car, wing.Id) && !car.GiftParts.Contains(wing.Id), "unlimited: for keeps");
            Assert.AreEqual(100000 - 5000 - 15000 - 45000, p.Mito);
            yield return null;
        }

        /// <summary>In the city: the HUD badge strip (drink + anniversary) and the custom shop's checkout with period buttons.</summary>
        [UnityTest]
        public IEnumerator CityBadgesAndPeriodCheckout()
        {
            tempSave = Path.Combine(Path.GetTempPath(), "driftcity_fx5_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempSave);
            PlayerProfile.SaveDirOverride = tempSave;
            PlayerProfile.Slot = 0;
            var p0 = new PlayerProfile { Name = "Fx5City", Level = 20, Mito = 500000 };
            p0.Cars.Add(new OwnedCar { VehicleIndex = 0, Color = 2 });
            p0.Save();
            PlayerProfile.Current = null;
            Directory.CreateDirectory("Preview");
            new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            var flow = new GameObject("Game").AddComponent<GameFlow>();
            flow.SkipParkingLot = true;
            yield return FrontEndSteps.ToCharacterSelect(flow);
            yield return FrontEndSteps.EnterSelected();
            yield return WaitFor(() => flow.InCity && CitySession.Current, 600, "city");
            yield return new WaitForSeconds(1f);
            var ui = UiManager.Instance;
            var P = PlayerProfile.Current;

            // badges: the EXP drink and the anniversary
            SeasonEvents.Override = EventSeason.Anniversary;
            if (P.Buffs == null) P.Buffs = new List<ItemBuff>();
            P.Buffs.Add(new ItemBuff { Id = "exp", Until = ItemUse.Now + 600000 });
            BonusBadges.RefreshNow();
            yield return null;
            CollectionAssert.AreEqual(new[] { "ID_MAINBUTTON_EXP_ONLYEXP1", "ID_MAINBUTTON_EXP_3RD" }, BonusBadges.Shown, "drink, then the anniversary (0x5e6090's order)");
            var strip = ui.Find(BonusBadges.Dialog);
            Assert.IsTrue(strip && strip.Control("ID_MAINBUTTON_EXP_3RD").gameObject.activeSelf, "3030000 with the 3RD badge");
            yield return new WaitForSeconds(0.3f);
            Shot("fx5_badges.png");

            // the custom shop: a wing's checkout with its period buttons, 30 days picked
            Menus.CustomShop();
            yield return new WaitForSeconds(0.5f);
            Assert.IsTrue(CustomShop.IsActive, "custom shop");
            var info = GameTables.Car(P.Car.VehicleIndex);
            var wing = PartsCatalog.For(info, PartSlot.Wing).First(x => !x.Stock && ShopPeriods.TablePeriods(x.Id).Count >= 2);
            var product = new ShopProduct { Part = wing };
            Checkout.Show(new List<ShopProduct> { product }, false);
            yield return null;
            var view = ui.Find(2620000);
            Assert.IsTrue(view, "BUYWISHLIST 2620000");
            Assert.IsTrue(view.Control("ID_VSHOP_BUYWISHLIST_BTN_PERIOD1ST_3RD").gameObject.activeSelf && !view.Control("ID_VSHOP_BUYWISHLIST_BTN_PERIOD1ST_4TH").gameObject.activeSelf, "three periods: 7 D, 30 D, unlimited");
            Checkout.PickPeriod(0, 1);
            yield return null;
            var price30 = ShopPeriods.TablePeriods(wing.Id)[1];
            Assert.AreEqual(price30.Price, product.Price, "30 D picked");
            yield return new WaitForSeconds(0.3f);
            Shot("fx5_period_checkout.png");
            long mito = P.Mito;
            int got = -1;
            Checkout.Buy(n => got = n);
            yield return WaitFor(() => got >= 0, 3, "checkout");
            Assert.AreEqual(1, got);
            Assert.AreEqual(mito - price30.Price, P.Mito);
            Assert.AreEqual(price30.Days, ShopPeriods.DaysLeft(P.Car, wing.Id, ItemUse.Now), "a 30-day wing");
            yield return new WaitForSeconds(0.5f);
            Shot("fx5_period_owned.png");
            ui.Find(2260000).Click("ID_VSHOP_QUIT");
            yield return null;
        }
    }
}
