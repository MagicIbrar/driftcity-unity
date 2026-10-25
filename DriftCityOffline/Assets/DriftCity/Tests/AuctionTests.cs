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
    /// The Auction House (DriftCity_RE\SHOPS.md section 3): the AUCTION window 1300000 with the offline AI market (search,
    /// categories, cars, buying), the register dialog AUCTIONITEM 1760000 (tax, quantity), the inventory's Auction page
    /// 3090000 (cancel, take back, AI buyers paying by note) and, online, a sale and a purchase with a bot
    /// (tools/bot.js --auction) through the local server. Screenshots: Preview/auction_*.png.
    /// </summary>
    public class AuctionTests
    {
        string tempSave;

        static void Shot(string name)
        {
            var tex = UiManager.Instance.Capture(Camera.main);
            File.WriteAllBytes(Path.Combine("Preview", name), tex.EncodeToPNG());
            Debug.Log("Drift City auction: wrote Preview/" + name);
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

        static void ClickYes()
        {
            var v = UiManager.Instance.Find(3250000);
            Assert.IsTrue(v, "a confirmation popup");
            v.Click(UiPopup.Yes);
        }

        [TearDown]
        public void TearDown()
        {
            if (OnlineSession.Instance) OnlineSession.Instance.Logout();
            foreach (var p in new[] { bot, server }) try { if (p != null && !p.HasExited) p.Kill(); } catch { }
            bot = server = null;
            PlayerProfile.SaveDirOverride = null;
            AuctionMarket.Offline.Reset();
            DriftCity.Social.SocialStore.Reset();
            foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Exclude).Where(g => g.transform.parent == null)) Object.Destroy(go);
            if (tempSave != null) try { Directory.Delete(tempSave, true); } catch { }
        }

        [UnityTest]
        public IEnumerator OfflineAuctionHouse()
        {
            tempSave = Path.Combine(Path.GetTempPath(), "driftcity_auction_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempSave);
            PlayerProfile.SaveDirOverride = tempSave;
            PlayerProfile.Slot = 0;
            AuctionMarket.Offline.Reset();
            DriftCity.Social.SocialStore.Reset();
            var p0 = new PlayerProfile { Name = "AucTester", Level = 30, Mito = 5_000_000 };
            p0.Cars.Add(new OwnedCar { VehicleIndex = 0, Color = 2 });
            p0.Cars.Add(new OwnedCar { VehicleIndex = 2, Color = 1 });
            p0.Give("i_n_00003", 6);
            p0.PerfParts.Add(new PerfPart { Uid = 1, Type = 1, Grade = 1, Enhance = 4 });
            p0.NextPartUid = 2;
            p0.Save();
            PlayerProfile.Current = null;
            Directory.CreateDirectory("Preview");

            new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            var flow = new GameObject("Game").AddComponent<GameFlow>();
            flow.SkipParkingLot = true;
            yield return FrontEndSteps.ToCharacterSelect(flow);
            yield return FrontEndSteps.EnterSelected();
            yield return WaitFor(() => flow.InCity && CitySession.Current, 600, "city");
            var ui = UiManager.Instance;
            var P = PlayerProfile.Current;
            yield return new WaitForSeconds(1f);

            // ---- the window with the AI market ----
            AuctionHouse.Open();
            yield return null;
            Assert.IsTrue(AuctionHouse.IsOpen && ui.Find(1300000), "AUCTION 1300000 open");
            Assert.IsTrue(OriginalMenus.IsOpen, "an original-menu screen (Esc closes it)");
            Assert.Greater(AuctionHouse.Total, 0, "AI drivers have items listed");
            Assert.LessOrEqual(AuctionHouse.Shown.Count, AuctionMarket.PageRows, "13 rows a page");
            Assert.IsTrue(AuctionHouse.Shown.All(l => !(l.Goods?.IsCar ?? true)), "the item tab lists items");
            yield return new WaitForSeconds(0.3f);
            Shot("auction_1_house.png");

            // categories: Speed parts only (10494)
            ui.Find(1300000).Click("ID_AUCTION_CATEGORY");
            yield return null;
            Assert.IsTrue(AuctionHouse.Shown.All(l => l.Category == 1), "category 1 = speed parts");
            for (int i = 0; i < 5; i++) ui.Find(1300000).Click("ID_AUCTION_CATEGORY");   // back to All Items
            yield return null;
            // cars tab
            ui.Find(1300000).Click("ID_AUCTION_CHK_VEHICLE");
            yield return null;
            Assert.IsTrue(AuctionHouse.Shown.All(l => l.Goods != null && l.Goods.IsCar), "the vehicle tab lists cars");
            ui.Find(1300000).Click("ID_AUCTION_CHK_ITEM");
            yield return null;

            // buy the cheapest AI part: the parts categories (1 speed .. 4 booster), sorted by price
            int row = -1;
            for (int cat = 1; cat <= 4 && row < 0; cat++)
            {
                ui.Find(1300000).Click("ID_AUCTION_CATEGORY");
                yield return null;
                if (cat == 1) { ui.Find(1300000).Click("ID_AUCTION_SORT_PRICE"); yield return null; }
                row = AuctionHouse.Shown.ToList().FindIndex(l => l.Goods.Kind == "part" && l.Price <= P.Mito);
            }
            Assert.GreaterOrEqual(row, 0, "an affordable part on the page");
            var target = AuctionHouse.Shown[row];
            AuctionHouse.Select(row);
            long mito = P.Mito;
            int parts = P.PerfParts.Count;
            ui.Find(1300000).Click("ID_AUCTION_BUTTON_BUY");
            yield return null;
            Assert.IsTrue(ui.Find(3250000), "10505 purchase confirmation");
            ClickYes();
            yield return null;
            Assert.AreEqual(mito - target.Price, P.Mito, "the price is paid");
            Assert.AreEqual(parts + 1, P.PerfParts.Count, "the part is in the bag");
            Assert.IsFalse(AuctionHouse.Shown.Any(l => l.Id == target.Id), "sold listings leave the list");
            OriginalMenus.Close();
            yield return null;

            // ---- the inventory's Auction page: register by dropping a bag item ----
            InventoryWindow.Open(InventoryWindow.Page.Auction);
            yield return null;
            Assert.IsTrue(ui.Find(InventoryWindow.DialogFor(3090000)), "INVEN 3090000 auction page");
            var stack = ItemCatalog.Find(P, "s:i_n_00003");
            AuctionRegister.Show(stack, -1);
            yield return null;
            Assert.IsTrue(AuctionRegister.IsOpen && ui.Find(1760000), "AUCTIONITEM 1760000");
            Shot("auction_2_register.png");
            mito = P.Mito;
            AuctionRegister.Submit(4, 40000, true);
            yield return null;
            Assert.IsTrue(ui.Find(3250000), "10444 tax confirmation");
            ClickYes();
            yield return null;
            Assert.AreEqual(mito - 4000, P.Mito, "10% registration tax");
            Assert.AreEqual(2, P.Count("i_n_00003"), "four enhancers in escrow");
            yield return WaitFor(() => AuctionPage.Mine.Any(l => l.Status == "open"), 3, "the listing on the page");
            // the part, at a price nobody pays (AI buyers skip it)
            var part = ItemCatalog.Find(P, "p:1");
            AuctionRegister.Show(part, -1);
            AuctionRegister.Submit(1, 1_000_000, false);   // far above its value: AI buyers skip it
            yield return null;
            Assert.IsFalse(P.PerfParts.Any(x => x.Uid == 1), "the part left the bag");
            // a car (not the one driven, nothing fitted)
            Assert.IsNull(AuctionMarket.CheckRegister(null, 1, 1, 150000, out _), "the second car may be listed");
            Assert.IsNotNull(AuctionMarket.CheckRegister(null, 0, 1, 150000, out _), "10577: not the car you drive");
            AuctionRegister.Show(null, 1);
            AuctionRegister.Submit(1, 150000, false);
            yield return null;
            Assert.AreEqual(1, P.Cars.Count, "the car went to the auction");
            InventoryWindow.ShowPage(InventoryWindow.Page.Auction);
            yield return WaitFor(() => AuctionPage.Mine.Count(l => l.Status == "open") == 3, 3, "three open listings");
            yield return new WaitForSeconds(0.3f);
            Shot("auction_3_inventory_page.png");

            // cancel the car: it comes back, the tax doesn't
            int carRow = AuctionPage.Mine.Where(l => l.Status != "taken").ToList().FindIndex(l => l.Goods.IsCar);
            ui.Find(InventoryWindow.DialogFor(3090000)).Click($"ID_INVEN_EXCHANGE_SLOT_{carRow + 1}_C");
            yield return null;
            ClickYes();
            yield return null;
            CloseAllPopups();
            Assert.AreEqual(2, P.Cars.Count, "the cancelled car is back");

            // an AI buyer takes the enhancers: due now -> sold, the Mito arrives as a note
            var store = AuctionMarket.Offline.Load();
            var enh = store.Listings.First(l => l.Mine && l.Status == "open" && l.Goods.Kind == "stack");
            enh.SellAt = AuctionMarket.Now - 1000;
            AuctionMarket.Tick();
            Assert.AreEqual("sold", enh.Status);
            var note = DriftCity.Social.SocialStore.Current.Notes.Last();
            Assert.AreEqual(40000, note.Mito, "the price comes by note from the Auction House");
            StringAssert.Contains("sold", note.Content);
            // the overpriced part expires and waits for its seller
            var exp = store.Listings.First(l => l.Mine && l.Status == "open" && l.Goods.Kind == "part");
            exp.EndsAt = AuctionMarket.Now - 1000;
            AuctionMarket.Tick();
            Assert.AreEqual("expired", exp.Status);
            AuctionPage.Load();
            yield return null;
            int expRow = AuctionPage.Mine.Where(l => l.Status != "taken").ToList().FindIndex(l => l.Id == exp.Id);
            ui.Find(InventoryWindow.DialogFor(3090000)).Click($"ID_INVEN_EXCHANGE_SLOT_{expRow + 1}_E");
            yield return null;
            Assert.IsTrue(P.PerfParts.Any(x => x.Type == 1 && x.Grade == 1 && x.Enhance == 4), "the expired part is back");
            AuctionPage.ShowTab(AuctionPage.Tab.Sold);
            yield return null;
            Shot("auction_4_sold_tab.png");
            InventoryWindow.Close();
            yield return null;
        }

        // ================= online: a sale and a purchase with a bot =================
        const int Port = 7876;
        static string Url => $"ws://127.0.0.1:{Port}";
        static string ServerDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "DriftCityServer"));
        Process server, bot;

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
        public IEnumerator OnlineAuctionWithBot()
        {
            if (!File.Exists(Path.Combine(ServerDir, "server.js"))) Assert.Ignore("DriftCityServer not found next to the Unity project");
            tempSave = Path.Combine(Path.GetTempPath(), "driftcity_aucon_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempSave);
            PlayerProfile.SaveDirOverride = tempSave;
            PlayerProfile.Slot = 0;
            PlayerProfile.Current = null;
            Directory.CreateDirectory("Preview");
            server = Node("server.js", new Dictionary<string, string> { ["DC_PORT"] = Port.ToString(), ["DC_DB"] = Path.Combine(tempSave, "test.db"), ["DC_AUCTION_NPC"] = "0" });
            try
            {
                float t = 0; bool up = false;
                while (!up && t < 15f)
                {
                    try { using (var c = new System.Net.Sockets.TcpClient()) { c.Connect("127.0.0.1", Port); up = true; } } catch { }
                    if (!up) { yield return new WaitForSecondsRealtime(0.25f); t += 0.25f; }
                }
                Assert.IsTrue(up, "server started");
                bot = Node($"tools/bot.js 1 --auction 100000 --name ABot --url {Url}");

                if (!Camera.main) new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
                var flow = new GameObject("Game").AddComponent<GameFlow>();
                flow.SkipParkingLot = true; flow.SkipIntro = true;
                yield return FrontEndSteps.ToCharacterSelect(flow, Url, "auctioneer", "secret123");
                yield return WaitFor(() => UiManager.Instance.Find(3250000), 15, "create-account popup");
                UiManager.Instance.Find(3250000).Click(UiPopup.Yes);
                yield return FrontEndSteps.NewCharacter("auctioneer", 1);
                UiManager.Instance.Find(150000).Click("ID_CREATECAR_NEW_NEXT");
                yield return WaitFor(() => PlayerProfile.Current != null && PlayerProfile.Current.OnlineDriver, 15, "server driver");
                yield return WaitFor(() => UiManager.Instance.Find(320000), 600, "city HUD");
                yield return WaitFor(() => OnlineSession.Instance.Channel != null, 15, "channel join");
                var ui = UiManager.Instance;

                // the bot's listing (3 enhancers for 9,000)
                AuctionHouse.Open();
                yield return WaitFor(() => AuctionHouse.Shown.Any(l => l.Seller == "ABot1"), 20, "the bot's listing");
                yield return new WaitForSeconds(0.3f);
                Shot("auction_5_online_house.png");
                int row = AuctionHouse.Shown.ToList().FindIndex(l => l.Seller == "ABot1");
                AuctionHouse.Select(row);
                long mito = PlayerProfile.Current.Mito;
                bool? ok = null;
                AuctionHouse.BuySelected((r, e) => { ok = r; if (!r) Debug.LogWarning(e); });
                yield return WaitFor(() => ok.HasValue, 10, "buy reply");
                Assert.IsTrue(ok.Value, "bought from the bot");
                Assert.AreEqual(mito - 9000, PlayerProfile.Current.Mito, "the server took the price");
                Assert.AreEqual(3, PlayerProfile.Current.Count("i_n_00003"), "the enhancers arrived");
                OriginalMenus.Close();
                yield return null;

                // our listing: the bot buys it, the Mito comes by note
                ok = null;
                AuctionMarket.Register(ItemCatalog.Find(PlayerProfile.Current, "s:i_n_00003"), -1, 2, 20000, (r, e) => { ok = r; if (!r) Debug.LogWarning(e); });
                yield return WaitFor(() => ok.HasValue, 10, "register reply");
                Assert.IsTrue(ok.Value, "listed");
                Assert.AreEqual(1, PlayerProfile.Current.Count("i_n_00003"));
                long afterTax = PlayerProfile.Current.Mito;
                Assert.AreEqual(mito - 9000 - 2000, afterTax, "10% tax on the server");
                List<AuctionListing> mine = null;
                yield return WaitFor(() =>
                {
                    AuctionMarket.Mine(l => mine = l);
                    return mine != null && mine.Any(l => l.Status == "sold" && l.Buyer == "ABot1");
                }, 20, "the bot bought our listing");
                InventoryWindow.Open(InventoryWindow.Page.Auction);
                AuctionPage.ShowTab(AuctionPage.Tab.Sold);
                yield return new WaitForSeconds(0.5f);
                Shot("auction_6_online_sold.png");
                InventoryWindow.Close();
                bool loaded = false;
                DriftCity.Social.Mail.Load(() => loaded = true);
                yield return WaitFor(() => loaded, 10, "notes");
                var note = DriftCity.Social.Mail.Inbox.FirstOrDefault(n => n.Mito == 20000);
                Assert.IsNotNull(note, "the sale's note carries the price");
                DriftCity.Social.Mail.Take(note);
                yield return WaitFor(() => PlayerProfile.Current.Mito == afterTax + 20000, 10, "the price taken from the note");
            }
            finally
            {
                foreach (var p in new[] { bot, server }) try { if (p != null && !p.HasExited) p.Kill(); } catch { }
            }
        }
    }
}
