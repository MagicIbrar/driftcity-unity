using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DriftCity.Core;
using DriftCity.Game;
using DriftCity.UI;
using DriftCity.Net;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace DriftCity.Tests
{
    /// <summary>
    /// Events, gacha, lotto, coupons offline (DriftCity_RE/EVENTS.md): the reward-group draw rule, the capsule shop with its
    /// 3.3 s capsule animation and prize popup, the slot machine for a Slot Ticket with the reel animation and GACHARESULT,
    /// a gift box in LUCKYBAG, a scratch card in LOTTO, the coupon book (a counter reaching Panda's 1000 drifts, the notify,
    /// the claim and the stamp), the token book, and in Cras the Christmas tree post (drops from a chase, the tree dialog,
    /// a donation) and the harvest posts.
    /// </summary>
    public class EventsTests
    {
        string tempSave;

        [SetUp]
        public void SetUp()
        {
            tempSave = Path.Combine(Path.GetTempPath(), "driftcity_ev_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempSave);
            PlayerProfile.SaveDirOverride = tempSave;
            PlayerProfile.Slot = 0;
            var p = new PlayerProfile { Name = "Santa", Level = 30, Mito = 5_000_000, City = "cras" };
            p.Cars.Add(new OwnedCar { VehicleIndex = 0, Color = 1 });
            p.Give("i_n_00085", 2);                  // Mysterious Gift Box (rw_patrol_giftbox)
            p.Give("lotto_scratchies_Natura", 1);
            p.Give("gacha_observe_v3", 2);           // Slot Ticket Grade 3
            p.Save();
            PlayerProfile.Current = null;
            SeasonEvents.Override = EventSeason.Christmas | EventSeason.Harvest;
            EventTime.Scale = 2f;
            EventNet.Rng = new System.Random(1234);
        }

        [TearDown]
        public void TearDown()
        {
            SeasonEvents.Override = null;
            EventTime.Scale = 1f;
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

        static void Teleport(DriftCar car, Vector3 p)
        {
            var rb = car.GetComponent<Rigidbody>();
            rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero;
            car.transform.position = p; rb.position = p;
        }

        [UnityTest]
        public IEnumerator GachaLottoBoxCouponsAndTree()
        {
            new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            var flow = new GameObject("Game").AddComponent<GameFlow>();
            flow.SkipParkingLot = true;
            yield return FrontEndSteps.ToCharacterSelect(flow);
            yield return FrontEndSteps.EnterSelected();
            yield return WaitFor(() => flow.InCity && CitySession.Current && flow.PlayerCar, 600, "city");
            CitySession.Current.MissionOfferInterval = 1e6f;
            EventObjects.Ensure();   // another fixture's teardown may have destroyed every root object
            var P = PlayerProfile.Current;
            yield return new WaitForSeconds(1f);

            // ---------------- data ----------------
            var pools = EventItems.Pools;
            Assert.Greater(pools.Count, 500, "init\\rewardgroup.xlt groups");
            Assert.AreEqual("lotto_Natura", EventItems.Pool("lotto_scratchies_Natura"));
            Assert.AreEqual(3, pools.Draw("lotto_Natura", 30, new System.Random(1)).Count, "a scratch card: one prize per column");
            Assert.AreEqual(2, pools.Draw("rw_Natura_v1", 30, new System.Random(1)).Count, "a car gift box: car and trial part");
            Assert.AreEqual(1, pools.Draw("rw_patrol_giftbox", 30, new System.Random(1)).Count, "a weighted pool gives one");
            Assert.IsTrue(LuckyBag.IsBag("i_n_00085") && LuckyBag.IsBag("lotto_scratchies_Natura") && LuckyBag.IsBag("gacha_observe_v3") && LuckyBag.IsBag("i_n_00049"));
            Assert.IsFalse(LuckyBag.IsBag("i_n_00003"), "an enhancer isn't an event item");
            Assert.IsNotNull(EventItems.CarForKey("pc_0063s"), "Natura's key names a car");
            Assert.Greater(CouponBook.Rows.Count, 30, "init\\coupon.xlt");
            var panda = CouponBook.Rows.First(r => r.CouponItem == "i_n_c0001");
            Assert.AreEqual((int)CouponBook.Cat.Drift, panda.Category); Assert.AreEqual(1000, panda.Need);
            Assert.GreaterOrEqual(TokenBook.Rows.Count, 10, "init\\tokenbook.xlt");
            Assert.AreEqual(EventSeason.Christmas, SeasonEvents.ForDate(new System.DateTime(2025, 12, 24)));

            // ---------------- capsule shop ----------------
            Gacha.Open();
            yield return null;
            Assert.IsTrue(Gacha.ShopView, "GACHA 4560000");
            Shot("events_gacha_shop.png");
            long mito = P.Mito;
            int items = P.Items.Sum(i => i.Count);
            Gacha.SetMode(1);
            Gacha.Buy();
            yield return new WaitForSeconds(0.5f);
            Shot("events_gacha_capsule.png");
            yield return WaitFor(() => Gacha.PopupView, 10, "capsule prize popup");
            Assert.AreEqual(mito - Gacha.Price(1), P.Mito, "Mito tab price (Gacha_Price_CarMito)");
            Assert.AreEqual(1, Gacha.LastWon.Count);
            Assert.AreEqual(items + Gacha.LastWon[0].Count + Gacha.LastExtras.Sum(g => g.Count), P.Items.Sum(i => i.Count), "the prize (and any season part) is in the bag");
            Shot("events_gacha_prize.png");
            Gacha.CloseShop();

            // ---------------- slot machine ----------------
            bool used = false;
            LuckyBag.Open("gacha_observe_v3", ok => used = ok);
            yield return null;
            Assert.IsTrue(Gacha.SlotView, "GACHA 4100000");
            Assert.IsTrue(Gacha.SlotView.Control("ID_GACHA_ITEMRANK03").gameObject.activeSelf, "ticket grade row");
            Gacha.Spin();
            yield return new WaitForSeconds(0.9f);
            Shot("events_slot_spin.png");
            yield return WaitFor(() => Gacha.ResultView, 20, "slot result");
            Assert.IsTrue(used);
            Assert.AreEqual(1, P.Count("gacha_observe_v3"), "one ticket spent");
            Shot("events_slot_result.png");
            Gacha.CloseSlot();

            // ---------------- gift box (LUCKYBAG 4180000) ----------------
            LuckyBag.Open("i_n_00085", null);
            yield return null;
            Assert.IsTrue(LuckyBag.View, "LUCKYBAG 4180000");
            LuckyBag.GetPrize();
            yield return WaitFor(() => LuckyBag.LastPrizes != null && LuckyBag.View.Control("ID_LUCKYBAG_CANCEL").Interactable, 15, "lucky bag reveal");
            Assert.AreEqual(1, P.Count("i_n_00085"), "one box spent");
            Assert.AreEqual(1, LuckyBag.LastPrizes.Count);
            Shot("events_luckybag.png");
            LuckyBag.Close();

            // ---------------- scratch card (LOTTO 3860000) ----------------
            LuckyBag.Open("lotto_scratchies_Natura", null);
            yield return null;
            Assert.IsTrue(Lotto.View, "LOTTO 3860000");
            Lotto.Scratch();
            yield return new WaitForSeconds(1.2f);
            Shot("events_lotto_scratching.png");
            yield return WaitFor(() => Lotto.Done, 15, "scratched");
            Assert.AreEqual(3, Lotto.LastPrizes.Count);
            Assert.AreEqual(0, P.Count("lotto_scratchies_Natura"));
            Assert.AreEqual(1, Lotto.Gauge().cards, "today's gauge");
            Shot("events_lotto.png");
            Lotto.Close();
            mito = P.Mito;
            Lotto.BuyCard("lotto_scratchies_Dew");
            Assert.AreEqual(mito - Lotto.CardPrice("lotto_scratchies_Dew"), P.Mito);
            Assert.AreEqual(1, P.Count("lotto_scratchies_Dew"));

            // ---------------- coupon book ----------------
            CouponBook.Count(CouponBook.Cat.Drift, 1000);
            yield return null;
            Assert.IsTrue(CouponBook.NotifyView, "COUPON 1850000 notify");
            Shot("events_coupon_notify.png");
            CouponBook.NotifyView.Click("ID_COUPON_NOTIFY_OK");
            CouponBook.Open();
            yield return null;
            var page = panda.Grade - 2;
            int slot = Enumerable.Range(1, 5).First(j => CouponBook.Slot(page, j) == panda);
            Assert.IsTrue(CouponBook.View.Control($"ID_COUPONBOOK_V{panda.Grade}_BTN{slot:00}").Interactable, "Panda claimable");
            CouponBook.View.Click($"ID_COUPONBOOK_V{panda.Grade}_BTN{slot:00}");
            yield return null;
            Assert.AreEqual(1, P.Count("i_n_c0001"), "Panda coupon");
            Assert.IsTrue(CouponBook.View.Control($"ID_COUPONBOOK_STAMP{page * 5 + slot:00}").gameObject.activeSelf, "stamped");
            Shot("events_couponbook.png");
            CouponBook.Close();

            // ---------------- token book ----------------
            P.Give(TokenBook.MissionToken, 1);
            TokenBook.Open(0);
            yield return null;
            Assert.IsTrue(TokenBook.View.Control("ID_TOKENBOOK_BUTTON01").Interactable);
            Shot("events_tokenbook.png");
            TokenBook.Exchange(0);
            Assert.AreEqual(0, P.Count(TokenBook.MissionToken));
            Assert.IsTrue(TokenBook.Rows[0].Gifts.All(g => P.Count(g.item) >= g.count), "token gifts");
            TokenBook.Close();

            // ---------------- the city: drops, Christmas tree, harvest ----------------
            var ev = EventObjects.Instance;
            Assert.IsTrue(ev, "events component (started with the game)");
            yield return WaitFor(() => ev.Spots.Any(s => s.Kind == EventObjects.Kind.Tree), 10, "tree post");
            Assert.IsTrue(ev.Spots.Any(s => s.Kind == EventObjects.Kind.Restaurant) && ev.Spots.Any(s => s.Kind == EventObjects.Kind.Distribution), "harvest posts");
            Assert.IsTrue(ev.Spots.Any(s => s.Kind == EventObjects.Kind.Coupon), "coupon post");
            var tree = ev.Spots.First(s => s.Kind == EventObjects.Kind.Tree);
            Assert.AreEqual("post_3rd_xmas_01", tree.Post);
            Assert.IsTrue(tree.Visual, "X_tree001 model");
            // a chase caught: a Santa Gift Box and the city's ingredient box
            P.HuvCaught += 1;
            yield return WaitFor(() => P.Count(XmasTree.GiftBox) > 0, 6, "Santa Gift Box drop");
            Assert.AreEqual(1, P.Count("event_giftbox_cras"), "harvest drop");
            // the tree opens when the car stops beside it
            Teleport(flow.PlayerCar, tree.Position + new Vector3(3, 2, 0));
            yield return WaitFor(() => XmasTree.View, 10, "XMASTREEEVENT 4440000");
            Assert.IsTrue(XmasTree.View.Control("ID_XMASTREEEVENT_BTN_ICON1").gameObject.activeSelf, "decorations offered while holding a gift box");
            Assert.IsFalse(XmasTree.View.Control("ID_XMASTREEEVENT_BTN_ICON10").gameObject.activeSelf, "the star waits for 9");
            yield return new WaitForSeconds(0.6f);
            Shot("events_xmastree.png");
            XmasTree.View.Click("ID_XMASTREEEVENT_BTN_ICON1");
            yield return null;
            Assert.AreEqual(1, XmasTree.Mask("cras") & 1, "decoration 1 on the Cras tree");
            Assert.AreEqual(1, P.Count(XmasTree.Ticket), "Battle EXP ticket");
            XmasTree.Close();
            // the tree and a harvest post from a distance
            var car = flow.PlayerCar;
            var back = tree.Position + new Vector3(0, 3, -40);
            Teleport(car, back);
            car.transform.rotation = Quaternion.LookRotation(new Vector3(tree.Position.x - back.x, 0, tree.Position.z - back.z));
            car.GetComponent<Rigidbody>().rotation = car.transform.rotation;
            yield return new WaitForSeconds(2f);
            Shot("events_tree_world.png");
            var rest = ev.Spots.First(s => s.Kind == EventObjects.Kind.Restaurant);
            Assert.IsTrue(rest.Visual, "restaurant beacon");
        }
    }

    /// <summary>
    /// The same screens online against the real server (src/events.js; needs node): codes give the items, the server
    /// draws the capsule, the gift box, the slot and the scratch card, and keeps the Christmas tree for everyone.
    /// </summary>
    public class EventsOnlineTests
    {
        const int Port = 7873;
        static string Url => $"ws://127.0.0.1:{Port}";
        Process server;
        string tempDir;
        static string ServerDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "DriftCityServer"));

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            if (!File.Exists(Path.Combine(ServerDir, "server.js"))) Assert.Ignore("DriftCityServer not found next to the Unity project");
            tempDir = Path.Combine(Path.GetTempPath(), "driftcity_evon_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            PlayerProfile.SaveDirOverride = tempDir;
            PlayerProfile.Slot = 0;
            PlayerProfile.Current = null;
            EventTime.Scale = 2f;
            var psi = new ProcessStartInfo("node", "server.js") { WorkingDirectory = ServerDir, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.EnvironmentVariables["DC_PORT"] = Port.ToString();
            psi.EnvironmentVariables["DC_DB"] = Path.Combine(tempDir, "ev.db");
            psi.EnvironmentVariables["DC_EVENTS"] = "christmas";
            psi.EnvironmentVariables["DC_COUPON_CODES"] = "XMAS=i_n_00088:3,SLOT=gacha_observe_v2:1,CARD=lotto_scratchies_Natura:1";
            server = Process.Start(psi);
            server.OutputDataReceived += (_, e) => { if (e.Data != null) Debug.Log("[node] " + e.Data); };
            server.ErrorDataReceived += (_, e) => { if (e.Data != null) Debug.Log("[node err] " + e.Data); };
            server.BeginOutputReadLine(); server.BeginErrorReadLine();
            float t = 0; bool up = false;
            while (!up && t < 15f)
            {
                try { using (var c = new System.Net.Sockets.TcpClient()) { c.Connect("127.0.0.1", Port); up = true; } } catch { }
                if (!up) { yield return new WaitForSecondsRealtime(0.25f); t += 0.25f; }
            }
            Assert.IsTrue(up, "server started");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            EventTime.Scale = 1f;
            if (OnlineSession.Instance) OnlineSession.Instance.Logout();
            try { if (server != null && !server.HasExited) server.Kill(); } catch { }
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

        static void ClosePopup() { var popup = UiManager.Instance.Find(3250000); if (popup) popup.Click("ID_POPUP_OKONLY"); }

        [UnityTest]
        public IEnumerator ServerDrawsAndSharedTree()
        {
            if (!Camera.main) new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            var flow = new GameObject("Game").AddComponent<GameFlow>();
            flow.SkipParkingLot = true; flow.SkipIntro = true;
            yield return FrontEndSteps.ToCharacterSelect(flow, Url, "evtester", "secret123");
            yield return WaitFor(() => UiManager.Instance.Find(3250000), 15, "create-account popup");
            UiManager.Instance.Find(3250000).Click(UiPopup.Yes);
            yield return FrontEndSteps.NewCharacter("evtester", 1);
            UiManager.Instance.Find(150000).Click("ID_CREATECAR_NEW_NEXT");
            yield return WaitFor(() => PlayerProfile.Current != null && PlayerProfile.Current.OnlineDriver, 15, "server driver");
            yield return WaitFor(() => UiManager.Instance.Find(320000), 600, "city HUD");
            System.Func<PlayerProfile> P = () => PlayerProfile.Current;

            // the server's seasons
            bool state = false;
            SeasonEvents.Refresh(() => state = true);
            yield return WaitFor(() => state, 10, "event.state");
            Assert.AreEqual(EventSeason.Christmas, SeasonEvents.Active, "DC_EVENTS=christmas");

            // codes from DC_COUPON_CODES
            foreach (var code in new[] { "XMAS", "SLOT", "CARD" })
            {
                bool? ok = null;
                CouponBook.RedeemCode(code, r => ok = r);
                yield return WaitFor(() => ok.HasValue, 10, "code " + code);
                Assert.IsTrue(ok.Value, code);
                ClosePopup();
            }
            Assert.AreEqual(3, P().Count(XmasTree.GiftBox));
            bool? again = null;
            CouponBook.RedeemCode("XMAS", r => again = r);
            yield return WaitFor(() => again.HasValue, 10, "code reuse");
            Assert.IsFalse(again.Value, "a code works once");
            ClosePopup();

            // capsule: the server charges and draws
            long mito = P().Mito;
            Gacha.Open();
            Gacha.SetMode(1);
            Gacha.Buy();
            yield return WaitFor(() => Gacha.PopupView, 15, "capsule prize");
            Assert.AreEqual(mito - 20000, P().Mito, "server price");
            Gacha.CloseShop();

            // gift box, slot ticket, scratch card
            LuckyBag.Open(XmasTree.GiftBox, null);
            yield return null;
            LuckyBag.GetPrize();
            yield return WaitFor(() => LuckyBag.LastPrizes != null, 10, "box prizes");
            Assert.AreEqual(2, P().Count(XmasTree.GiftBox));
            LuckyBag.Close();
            LuckyBag.Open("gacha_observe_v2", null);
            yield return null;
            Gacha.Spin();
            yield return WaitFor(() => Gacha.ResultView, 20, "slot result");
            Assert.AreEqual(0, P().Count("gacha_observe_v2"));
            Gacha.CloseSlot();
            LuckyBag.Open("lotto_scratchies_Natura", null);
            yield return null;
            Lotto.Scratch();
            yield return WaitFor(() => Lotto.Done, 15, "scratched");
            Assert.AreEqual(3, Lotto.LastPrizes.Count);
            Lotto.Close();

            // the tree: the donation is the server's, everyone sees it
            bool? donated = null;
            XmasTree.Donate("oros", 2, r => donated = r);
            yield return WaitFor(() => donated.HasValue, 10, "donation");
            Assert.IsTrue(donated.Value);
            state = false;
            SeasonEvents.Refresh(() => state = true);
            yield return WaitFor(() => state, 10, "event.state");
            Assert.AreEqual(4, XmasTree.Mask("oros"), "server mask");
            Assert.AreEqual(1, P().Count(XmasTree.Ticket));
        }
    }
}
