using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    /// The shops (DriftCity_RE\SHOPS.md): the advanced car dealer AUTOSHOPADV (tabs, filters, the car strip, details,
    /// compare, related items, wish list, buying and selling) and the custom shop's extras (mileage prices and the mileage
    /// checkout, the wish list and its checkout, the trade history); online a gift to a bot through the local server.
    /// Screenshots: Preview/shop_*.png.
    /// </summary>
    public class ShopTests
    {
        string tempSave;
        Process server, bot;

        static void Shot(string name)
        {
            var tex = UiManager.Instance.Capture(Camera.main);
            File.WriteAllBytes(Path.Combine("Preview", name), tex.EncodeToPNG());
            Debug.Log("Drift City shops: wrote Preview/" + name);
        }

        static IEnumerator WaitFor(System.Func<bool> condition, float timeout, string what)
        {
            float t = 0;
            while (!condition() && t < timeout) { t += Time.unscaledDeltaTime; yield return null; }
            Assert.IsTrue(condition(), "timed out waiting for " + what);
        }

        static void CloseAllPopups()
        {
            for (int i = 0; i < 6; i++)
            {
                var v = UiManager.Instance.Find(3250000);
                if (!v) break;
                foreach (var b in new[] { "ID_POPUP_OKONLY", UiPopup.Cancel, UiPopup.No, UiPopup.Deny }) if (v && v.Control(b) && v.Control(b).gameObject.activeSelf) { v.Click(b); break; }
            }
        }

        [TearDown]
        public void TearDown()
        {
            if (OnlineSession.Instance) OnlineSession.Instance.Logout();
            foreach (var p in new[] { bot, server }) try { if (p != null && !p.HasExited) p.Kill(); } catch { }
            bot = server = null;
            PlayerProfile.SaveDirOverride = null;
            foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Exclude).Where(g => g.transform.parent == null)) Object.Destroy(go);
            if (tempSave != null) try { Directory.Delete(tempSave, true); } catch { }
        }

        IEnumerator Offline(string name, int level, long mito)
        {
            tempSave = Path.Combine(Path.GetTempPath(), "driftcity_shops_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempSave);
            PlayerProfile.SaveDirOverride = tempSave;
            PlayerProfile.Slot = 0;
            var p0 = new PlayerProfile { Name = name, Level = level, Mito = mito };
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
        }

        [UnityTest]
        public IEnumerator AdvancedCarDealer()
        {
            yield return Offline("DealerTester", 40, 20_000_000);
            var ui = UiManager.Instance;
            var P = PlayerProfile.Current;

            // globalcontent CarShop = 1: the dealer is the advanced one
            Assert.AreEqual(1, GlobalContent.Int("CarShop", -1), "this client's data selects the advanced dealer");
            Menus.CarShop();
            yield return new WaitForSeconds(0.5f);
            Assert.IsTrue(AdvancedDealer.IsOpen && ui.Find(2640000) && ui.Find(2660000) && ui.Find(2650000) && ui.Find(2670000) && ui.Find(2550000), "BG, menu, detail, price panel, wish list");
            Assert.IsTrue(Menus.DealerOpen && OriginalMenus.IsOpen);
            Assert.IsTrue(GameObject.Find("Dealer Camera"), "the showroom camera");
            Assert.IsTrue(GameObject.Find("autoshop.nif"), "the dealer's own showroom");
            Assert.Greater(AdvancedDealer.Entries.Count, 30, "dealer cars with a Display slot");
            Assert.AreEqual(AdvancedDealer.Tab.Buy, AdvancedDealer.CurrentTab);
            Shot("shop_1_dealer.png");

            // a class filter and the strip
            ui.Find(2660000).Click("ID_AUTOSHOPADVMENU_V3");
            yield return null;
            Assert.IsTrue(AdvancedDealer.Entries.All(e => e.Info.Grade == 3), "V3 cars only");
            ui.Find(2660000).Click("ID_AUTOSHOPADVMENU_BTN01");
            yield return null;
            Assert.AreEqual(1, AdvancedDealer.Entries.ToList().IndexOf(AdvancedDealer.Selection), "the second cell selected");
            ui.Find(2660000).Click("ID_AUTOSHOPADVMENU_RIGHTBTN");
            yield return null;
            Assert.AreEqual(2, AdvancedDealer.Entries.ToList().IndexOf(AdvancedDealer.Selection), "the arrow steps to the next car");
            var sel = AdvancedDealer.Selection;
            StringAssert.Contains(sel.Info.ReqLevel.ToString(), ui.Find(2650000).Control("ID_AUTOSHOPADVDETAIL_LEVEL_TXT").GetComponentInChildren<UnityEngine.UI.Text>(true).text, "Lv. %u+");
            Assert.AreEqual($"{ShopData.CarPrice(sel.Info):N0}", ui.Find(2670000).Control("ID_AUTOSHOPADVMITO_MITO_NUM").GetComponentInChildren<UnityEngine.UI.Text>(true).text, "the price panel");
            yield return new WaitForSeconds(0.5f);
            Shot("shop_2_dealer_v3.png");

            // compare against the car you drive
            ui.Find(2650000).Click("ID_AUTOSHOPADVDETAIL_COMPARE_BTN");
            yield return null;
            Assert.IsTrue(ui.Find(3280000), "AUTOSHOP 3280000 compare");
            Shot("shop_3_compare.png");
            ui.Find(3280000).Click("ID_AUTOSHOP_CARCOMPARE_ESC");
            yield return null;

            // wish list: a right click puts the car on it (remake), its icon goes back to the car
            AdvancedDealer.ToggleWish(0);
            yield return null;
            Assert.AreEqual(1, P.WishCars.Count, "one wished car");

            // buy it in colour 3
            ui.Find(2650000).Click("ID_AUTOSHOPADVDETAIL_CHKCOLOR_3");
            int cars = P.Cars.Count;
            long mito = P.Mito, price = AdvancedDealer.Price(AdvancedDealer.Selection);
            var bought = AdvancedDealer.Selection.Info;
            bool? ok = null;
            AdvancedDealer.Buy(r => ok = r);
            yield return null;
            CloseAllPopups();
            Assert.IsTrue(ok == true, "bought");
            Assert.AreEqual(cars + 1, P.Cars.Count);
            Assert.AreEqual(mito - price, P.Mito);
            Assert.AreEqual(2, P.Cars.Last().Color, "the chosen factory colour");
            Assert.AreEqual(price / Mileage.PerMito, P.Mileage, "mileage: 1 point per 100 Mito");
            Assert.AreEqual(bought.Index, P.Car.VehicleIndex, "the new car is driven");

            // related items for the new car: buy a part with mileage
            AdvancedDealer.OpenRelated();
            yield return null;
            Assert.IsTrue(ui.Find(2680000), "AUTOSHOPADVLIST related items");
            yield return new WaitForSeconds(0.3f);
            Shot("shop_4_related.png");
            AdvancedDealer.CloseRelated();

            // sell tab: the old car (not the one driven) goes back for its price
            ui.Find(2660000).Click("ID_AUTOSHOPADVMENU_SELL");
            yield return null;
            Assert.AreEqual(AdvancedDealer.Tab.Sell, AdvancedDealer.CurrentTab);
            Assert.AreEqual(P.Cars.Count, AdvancedDealer.Entries.Count, "your cars");
            int oldCar = AdvancedDealer.Entries.ToList().FindIndex(e => e.Slot != P.CurrentCar);
            ui.Find(2660000).Click($"ID_AUTOSHOPADVMENU_BTN{oldCar:00}");
            yield return null;
            Assert.IsNull(AdvancedDealer.CheckSell(AdvancedDealer.Selection));
            int curCar = AdvancedDealer.Entries.ToList().FindIndex(e => e.Slot == P.CurrentCar);
            Assert.IsNotNull(AdvancedDealer.CheckSell(AdvancedDealer.Entries[curCar]), "10491: not the car you drive");
            long sellPrice = AdvancedDealer.Price(AdvancedDealer.Selection);
            mito = P.Mito;
            ok = null;
            AdvancedDealer.Sell(r => ok = r);
            yield return null;
            Assert.IsTrue(ok == true, "sold");
            Assert.AreEqual(mito + sellPrice, P.Mito);
            Assert.AreEqual(1, P.Cars.Count);
            Shot("shop_5_sell_tab.png");

            // trade history (buy log)
            ui.Find(2670000).Click("ID_AUTOSHOPADVMITO_HISTORY_BTN");
            yield return null;
            Assert.IsTrue(ShopHistory.IsOpen && ui.Find(2290000), "BUYHISTORY 2290000");
            yield return new WaitForSeconds(0.2f);
            Shot("shop_6_history.png");
            ShopHistory.Close();

            UiManager.Instance.CloseAll();
            yield return null; yield return null;
            Assert.IsFalse(AdvancedDealer.IsOpen);
            Assert.IsFalse(GameObject.Find("Dealer Camera"), "the showroom goes with the screen");
        }

        [UnityTest]
        public IEnumerator CustomShopExtras()
        {
            yield return Offline("VShopTester", 20, 3_000_000);
            var ui = UiManager.Instance;
            var P = PlayerProfile.Current;
            P.Mileage = 50_000;
            Menus.CustomShop();
            yield return new WaitForSeconds(0.5f);
            Assert.IsTrue(CustomShop.IsActive && ui.Find(2540000), "the wish list beside the shop");
            var list = ui.Find(2470000);
            Assert.IsTrue(list.Control("ID_VSHOP_PRODUCTLIST_MILEAGEPRICE_LIST1").gameObject.activeSelf || list.Control("ID_VSHOP_PRODUCTLIST_MILEAGEPRICE_LIST0").gameObject.activeSelf, "mileage prices show");
            yield return new WaitForSeconds(0.5f);
            Shot("shop_7_vshop_mileage.png");

            // a paint on the wish list, then its checkout
            ui.Find(2340000).Click("ID_VSHOP_TOP_SMALL_COLOR");
            yield return null;
            var paint = ShopData.Paints.First(x => x.UniqueId != P.Car.PaintItem);
            VShopWish.Toggle(new ShopProduct { Paint = paint });
            yield return null;
            Assert.Contains(paint.UniqueId, P.WishList, "wished");
            Checkout.ShowWishList();
            yield return null;
            Assert.IsTrue(ui.Find(2620000), "BUYWISHLIST 2620000");
            yield return new WaitForSeconds(0.2f);
            Shot("shop_8_wish_checkout.png");
            long mito = P.Mito;
            int got = -1;
            Checkout.Buy(n => got = n);
            yield return WaitFor(() => got >= 0, 3, "checkout");
            Assert.AreEqual(1, got);
            Assert.AreEqual(paint.UniqueId, P.Car.PaintItem, "the wished paint is on the car");
            Assert.AreEqual(mito - paint.Price, P.Mito);

            // a part with mileage
            var kicker = GameTables.Car(P.Car.VehicleIndex);
            var part = PartsCatalog.For(kicker, PartSlot.Plate).First(x => !x.Stock && !P.Car.Parts.Contains(x.Id));
            var product = new ShopProduct { Part = part };
            long miles = P.Mileage;
            Checkout.Show(new List<ShopProduct> { product }, true);
            yield return null;
            Assert.IsTrue(ui.Find(2630000), "BUYMILEAGE 2630000");
            Shot("shop_9_mileage_checkout.png");
            got = -1;
            Checkout.Buy(n => got = n);
            yield return WaitFor(() => got >= 0, 3, "mileage checkout");
            Assert.AreEqual(1, got);
            Assert.AreEqual(miles - product.MileagePrice, P.Mileage, "paid in mileage (a tenth of the Mito price)");
            Assert.AreEqual(part.Id, P.Car.Plate, "fitted");

            // gifts need the server
            Gifts.Show(product);
            yield return null;
            Assert.IsFalse(Gifts.IsOpen, "offline: no gift dialog");
            CloseAllPopups();

            // the trade history from the bottom bar
            ui.Find(2460000).Click("ID_VSHOP_BOTTOM_BUYLIST");
            yield return null;
            Assert.IsTrue(ShopHistory.IsOpen, "history");
            yield return new WaitForSeconds(0.2f);
            Shot("shop_10_vshop_history.png");
            ShopHistory.Close();
            ui.Find(2260000).Click("ID_VSHOP_QUIT");
            yield return null; yield return null;
            Assert.IsFalse(CustomShop.IsActive);
        }

        // ================= online: a gift for the bot =================
        const int Port = 7877;
        static string Url => $"ws://127.0.0.1:{Port}";
        static string ServerDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "DriftCityServer"));

        static Process Node(string args, Dictionary<string, string> env = null)
        {
            var psi = new ProcessStartInfo("node", args) { WorkingDirectory = ServerDir, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (env != null) foreach (var kv in env) psi.EnvironmentVariables[kv.Key] = kv.Value;
            var p = Process.Start(psi);
            p.OutputDataReceived += (_, e) => { if (e.Data != null) Debug.Log("[node] " + e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) Debug.Log("[node err] " + e.Data); };
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            return p;
        }

        [UnityTest]
        public IEnumerator OnlineGiftAndDealer()
        {
            if (!File.Exists(Path.Combine(ServerDir, "server.js"))) Assert.Ignore("DriftCityServer not found next to the Unity project");
            tempSave = Path.Combine(Path.GetTempPath(), "driftcity_shopon_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempSave);
            PlayerProfile.SaveDirOverride = tempSave;
            PlayerProfile.Slot = 0;
            PlayerProfile.Current = null;
            Directory.CreateDirectory("Preview");
            server = Node("server.js", new Dictionary<string, string> { ["DC_PORT"] = Port.ToString(), ["DC_DB"] = Path.Combine(tempSave, "test.db"), ["DC_GIFT_LEVEL"] = "1" });
            float t = 0; bool up = false;
            while (!up && t < 15f)
            {
                try { using (var c = new System.Net.Sockets.TcpClient()) { c.Connect("127.0.0.1", Port); up = true; } } catch { }
                if (!up) { yield return new WaitForSecondsRealtime(0.25f); t += 0.25f; }
            }
            Assert.IsTrue(up, "server started");
            bot = Node($"tools/bot.js 1 --name GBot --url {Url}");

            if (!Camera.main) new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            var flow = new GameObject("Game").AddComponent<GameFlow>();
            flow.SkipParkingLot = true; flow.SkipIntro = true;
            yield return FrontEndSteps.ToCharacterSelect(flow, Url, "gifter", "secret123");
            yield return WaitFor(() => UiManager.Instance.Find(3250000), 15, "create-account popup");
            UiManager.Instance.Find(3250000).Click(UiPopup.Yes);
            yield return FrontEndSteps.NewCharacter("gifter", 1);
            UiManager.Instance.Find(150000).Click("ID_CREATECAR_NEW_NEXT");
            yield return WaitFor(() => PlayerProfile.Current != null && PlayerProfile.Current.OnlineDriver, 15, "server driver");
            yield return WaitFor(() => UiManager.Instance.Find(320000), 600, "city HUD");
            yield return WaitFor(() => OnlineSession.Instance.Channel != null, 15, "channel join");
            yield return WaitFor(() => OnlineSession.Instance.Remotes && OnlineSession.Instance.Remotes.Count > 0, 20, "the bot in the channel");
            var ui = UiManager.Instance;
            var P = PlayerProfile.Current;

            // the dealer through the server: a cheap car, mileage accrues on the server
            Menus.CarShop();
            yield return new WaitForSeconds(0.5f);
            var cheap = AdvancedDealer.Entries.Select((e, i) => (e, i)).Where(x => x.e.Info.ReqLevel <= P.Level && ShopData.CarPrice(x.e.Info) <= P.Mito && !P.Cars.Any(c => c.VehicleIndex == x.e.Info.Index)).OrderBy(x => ShopData.CarPrice(x.e.Info)).First();
            for (int k = 0; k < cheap.i; k++) AdvancedDealer.Step(1);
            bool? ok = null;
            long price = AdvancedDealer.Price(AdvancedDealer.Selection);
            AdvancedDealer.Buy(r => ok = r);
            yield return WaitFor(() => ok.HasValue, 10, "buy reply");
            CloseAllPopups();
            Assert.IsTrue(ok.Value, "the server sold the car");
            Assert.AreEqual(price / 100, PlayerProfile.Current.Mileage, "server mileage");
            UiManager.Instance.CloseAll();
            yield return null;

            // the custom shop's gift dialog: a paint for the bot (the test server's gift level is 1, DC_GIFT_LEVEL)
            var paint = ShopData.Paints.First();
            PlayerProfile.Current.Level = Mathf.Max(PlayerProfile.Current.Level, Gifts.GiftLevel);   // the client's own check
            Gifts.Show(new ShopProduct { Paint = paint });
            yield return null;
            Assert.IsTrue(Gifts.IsOpen && ui.Find(2840000), "SENDGIFT 2840000");
            yield return new WaitForSeconds(0.2f);
            Shot("shop_11_gift.png");
            long before = PlayerProfile.Current.Mito;
            bool? sent = null;
            Gifts.Send("GBot1", "enjoy", r => sent = r);
            yield return WaitFor(() => sent.HasValue, 10, "gift reply");
            CloseAllPopups();
            Assert.IsTrue(sent.Value, "the server sent the gift");
            Assert.AreEqual(before - paint.Price, PlayerProfile.Current.Mito, "the sender paid the paint's price");
            Assert.IsFalse(Gifts.IsOpen);
            ShopHistory.Open(ShopHistory.Tab.Sent);
            yield return new WaitForSeconds(1f);
            Assert.IsTrue(ui.Find(2290000), "history online");
            bool reloaded = false;
            ShopHistory.Load(() => reloaded = true);
            yield return WaitFor(() => reloaded, 10, "history from the server");
            Shot("shop_12_history_online.png");
            ShopHistory.Close();
        }
    }
}
