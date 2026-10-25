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
    /// The inventory screens (DriftCity_RE\INVENTORY.md): INVENBASE + MYINVEN with every page, ITEMINFO / LISENCEINFO
    /// tooltips, item use (drink, repair, cheque), equip, discard with the count box, sell, quick slots on the HUD, the
    /// licence page; offline the trade window says it needs the server. The online test trades with a bot
    /// (tools/bot.js --trade) through the local server. Screenshots: Preview/inv_*.png.
    /// </summary>
    public class InventoryTests
    {
        string tempSave;
        bool classicBefore;

        [SetUp]
        public void SetUp()
        {
            classicBefore = InventoryWindow.Classic;
            tempSave = Path.Combine(Path.GetTempPath(), "driftcity_inv_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempSave);
            PlayerProfile.SaveDirOverride = tempSave;
            PlayerProfile.Slot = 0;
            var p = new PlayerProfile { Name = "InvTester", Level = 25, Mito = 1_000_000 };
            p.Cars.Add(new OwnedCar { VehicleIndex = 0, Color = 2 });
            p.Give("i_d_drk_01", 2);
            p.Give("i_d_repair01", 1);
            p.Give("i_d_m_10k", 1);
            p.Give("i_n_00003", 3);
            p.Give("mitron_cristal", 2);
            p.PerfParts.Add(new PerfPart { Uid = 1, Type = 0, Grade = 0, Enhance = 2 });
            p.PerfParts.Add(new PerfPart { Uid = 2, Type = 1, Grade = 1, Enhance = 0 });
            p.NextPartUid = 3;
            p.AddItem(10007);
            p.Save();
            PlayerProfile.Current = null;
            PlayerPrefs.DeleteKey("DriftCity.QuickSlots.offline0.InvTester");
            PlayerPrefs.DeleteKey("DriftCity.QuickSlots.offline0.InvTester.auto");
            PlayerPrefs.DeleteKey("DriftCity.BattleSlots.offline0.InvTester");
            Directory.CreateDirectory("Preview");
        }

        [TearDown]
        public void TearDown()
        {
            InventoryWindow.Classic = classicBefore;
            PlayerProfile.SaveDirOverride = null;
            foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Exclude).Where(g => g.transform.parent == null)) Object.Destroy(go);
            try { Directory.Delete(tempSave, true); } catch { }
        }

        static void Shot(string name)
        {
            var tex = UiManager.Instance.Capture(Camera.main);
            File.WriteAllBytes(Path.Combine("Preview", name), tex.EncodeToPNG());
            Debug.Log("Drift City inventory: wrote Preview/" + name);
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

        static NuiControlView Slot(int i) => InventoryWindow.Bag.Control($"ID_MYINVEN_SLOT_{i + 1}");

        static int BagIndex(System.Func<BagEntry, bool> pred)
        {
            var list = ItemCatalog.Sorted(ItemCatalog.Bag(PlayerProfile.Current), 0);
            return list.FindIndex(e => pred(e));
        }

        [UnityTest]
        public IEnumerator InventoryPagesItemsAndQuickSlots()
        {
            new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            var flow = new GameObject("Game").AddComponent<GameFlow>();
            flow.SkipParkingLot = true;
            yield return FrontEndSteps.ToCharacterSelect(flow);
            yield return FrontEndSteps.EnterSelected();
            yield return WaitFor(() => flow.InCity && CitySession.Current, 600, "city");
            var ui = UiManager.Instance;
            var P = PlayerProfile.Current;
            yield return new WaitForSeconds(1f);
            InventoryWindow.Classic = true;   // this test walks the classic set; NdlgInventory... the NDLG one

            // ---- the window: frame, bag, parts page ----
            Menus.Inventory();
            yield return null;
            Assert.IsTrue(InventoryWindow.IsOpen && ui.Find(3060000) && ui.Find(3190000), "INVENBASE + MYINVEN");
            Assert.IsTrue(ui.Find(3130000) && ui.Find(3150000) && ui.Find(3160000), "parts page, car info, points");
            Assert.IsTrue(OriginalMenus.IsOpen, "the inventory is an original-menu screen (Esc / MenuOpen)");
            var bag = ItemCatalog.Bag(P);
            // two unfitted parts, five stacks, one paint (the city may add event drops, e.g. the Halloween drink)
            foreach (var key in new[] { "p:1", "p:2", "s:i_d_drk_01", "s:i_d_repair01", "s:i_d_m_10k", "s:i_n_00003", "s:mitron_cristal", "i:10007" })
                Assert.IsTrue(bag.Any(e => e.Key == key), "in the bag: " + key);
            Assert.IsTrue(Slot(0).transform.Find("item"), "first bag slot shows its item");
            Assert.AreEqual("+2", InventoryWindow.Bag.Control("ID_MYINVEN_UPGRADE01").GetComponentInChildren<UnityEngine.UI.Text>(true).text, "enhance badge of the +2 part");

            // ---- tooltip ----
            int drink = BagIndex(e => e.Key == "s:i_d_drk_01");
            ItemTooltip.Show(ItemCatalog.Bag(P).First(e => e.Key == "s:i_d_drk_01"), (RectTransform)Slot(drink).transform);
            yield return null;
            Assert.IsTrue(ui.Find(1320000), "ITEMINFO open");
            var name = ui.Find(1320000).Control("ID_ITEMINFO_NAME").GetComponentInChildren<UnityEngine.UI.Text>(true).text;
            StringAssert.Contains("EXP", name);
            Shot("inv_1_parts_tooltip.png");
            ItemTooltip.Hide();

            // ---- equip a part on the car (drag target: the stat column's first slot) ----
            ItemUse.Equip(P.PerfParts.First(x => x.Uid == 1), InventoryWindow.CarSlot);
            yield return null;
            Assert.AreEqual(1, P.Car.PerfSlots[0], "speed part fitted");
            Assert.AreEqual(bag.Count - 1, ItemCatalog.Bag(P).Count, "the fitted part left the bag");
            Assert.IsTrue(InventoryWindow.PageView.Control("ID_INVEN_PARTSNEW_SLOTN_01").transform.Find("item"), "part shown in the car slot");
            ItemUse.Unequip(InventoryWindow.CarSlot, 0);
            yield return null;
            Assert.AreEqual(-1, P.Car.PerfSlots[0]);

            // ---- use: EXP drink, repair, Mito cheque ----
            ItemUse.Use(ItemCatalog.Find(P, "s:i_d_drk_01"), quiet: true);
            yield return null;
            Assert.IsTrue(ItemUse.HasBuff(P, "exp"), "EXP drink active");
            Assert.AreEqual(1, P.Count("i_d_drk_01"));
            Assert.AreEqual(ItemUse.ExpDrinkPct, ItemUse.ExpBonus(P, "chase"));
            Assert.AreEqual(0, ItemUse.ExpBonus(P, "quest"), "drinks don't count on quests");
            ItemUse.Use(ItemCatalog.Find(P, "s:i_d_drk_01"), quiet: true);
            yield return null;
            Assert.AreEqual(1, P.Count("i_d_drk_01"), "a second EXP drink is refused while the first works (11059)");
            CloseAllPopups();
            var effects = flow.PlayerCar.GetComponentInChildren<CarEffects>();
            if (effects) effects.Damage01 = 0.8f;
            ItemUse.Use(ItemCatalog.Find(P, "s:i_d_repair01"), quiet: true);
            yield return null;
            Assert.AreEqual(0, P.Count("i_d_repair01"));
            if (effects) Assert.AreEqual(0f, effects.Damage01, "Gio repaired the car");

            // ---- discard a stack through the count box ----
            int enh = BagIndex(e => e.Key == "s:i_n_00003");
            InventoryWindow.AskDiscard(ItemCatalog.Find(P, "s:i_n_00003"));
            yield return null;
            Assert.IsTrue(CountPopup.IsOpen && ui.Find(3240000), "count box for a stack");
            CountPopup.Set(2);
            Shot("inv_2_discard_count.png");
            ui.Find(3240000).Click("ID_TRADE_COUNT_OK");
            yield return null;
            Assert.AreEqual(1, P.Count("i_n_00003"), "two enhancers thrown away");
            Assert.GreaterOrEqual(enh, 0);

            // ---- right-click actions and selling a part ----
            InventoryWindow.Actions(ItemCatalog.Find(P, "p:2"));
            yield return null;
            var pop = ui.Find(3250000);
            Assert.IsTrue(pop && pop.Control(UiPopup.Sell).gameObject.activeSelf, "a part can be sold");
            long mito = P.Mito;
            pop.Click(UiPopup.Sell);
            yield return null;
            ui.Find(3250000).Click(UiPopup.Yes);
            yield return null;
            Assert.IsFalse(P.PerfParts.Any(x => x.Uid == 2), "part sold");
            Assert.AreEqual(mito + 32000, P.Mito);

            // ---- pages ----
            InventoryWindow.ShowPage(InventoryWindow.Page.DriftItem);
            yield return new WaitForSeconds(0.5f);
            Assert.IsTrue(ui.Find(3140000), "drift item page");
            Shot("inv_3_drift_items.png");
            InventoryWindow.ShowPage(InventoryWindow.Page.Modify);
            yield return null;
            Assert.IsTrue(ui.Find(3210000), "modify page");
            InventoryWindow.Bag.GetComponentsInChildren<ItemSlot>(true).First(s => s.Entry() != null && s.Entry().Key == "p:1").LeftClick(ItemCatalog.Find(P, "p:1"));
            yield return null;
            Assert.IsTrue(ui.Find(3210000).Control("ID_INVEN_UPGRADE_SLOT1").transform.Find("item"), "part placed in the modify slot");
            Shot("inv_4_modify.png");
            InventoryWindow.ShowPage(InventoryWindow.Page.Sticker);
            yield return null;
            Assert.IsTrue(ui.Find(3100000), "sticker page");
            InventoryWindow.ShowPage(InventoryWindow.Page.Auction);
            yield return null;
            Assert.IsTrue(ui.Find(3090000), "auction page");

            // ---- licence page and licence info boxes ----
            var earned = Licences.All.First(l => l.Obtainable);
            P.Licences.Add(earned.Id);
            P.MountedLicence = earned.Id;
            InventoryWindow.ShowPage(InventoryWindow.Page.Licence);
            yield return null;
            Assert.IsTrue(ui.Find(3080000), "licence page");
            Assert.AreEqual(earned.Name, ui.Find(3080000).Control("ID_INVEN_LICENSE_TITLE").GetComponentInChildren<UnityEngine.UI.Text>(true).text);
            ItemTooltip.ShowLicence(earned, (RectTransform)ui.Find(3080000).Control("ID_INVEN_LICENSE_POSITEM").transform);
            yield return null;
            Assert.IsTrue(ui.Find(1360000), "LISENCEINFO (earned)");
            Shot("inv_5_licence.png");
            var other = Licences.All.First(l => !P.Licences.Contains(l.Id) && l.Obtainable);
            ItemTooltip.ShowLicence(other, null);
            yield return null;
            Assert.IsTrue(ui.Find(1370000) && !ui.Find(1360000), "LISENCEINFO grey for a licence not earned");
            Assert.IsTrue(LicenceInfo.ConditionLines(other, P).Count > 0);
            ItemTooltip.Hide();

            // ---- quick slots ----
            yield return WaitFor(() => QuickSlots.Tiles, 5, "quick slot HUD");
            Assert.IsTrue(ui.Find(360000) && ui.Find(390000), "ITEMQUICKSLOT tiles and covers");
            Assert.IsTrue(QuickSlots.Assign(0, ItemCatalog.Find(P, "s:i_d_m_10k")), "cheque assigned");
            Assert.IsFalse(QuickSlots.Assign(1, ItemCatalog.Find(P, "s:mitron_cristal")), "materials don't go in a quick slot (10170)");
            CloseAllPopups();
            Assert.IsTrue(QuickSlots.Assign(1, ItemCatalog.Find(P, "s:i_d_drk_01")));
            InventoryWindow.Close();
            yield return null;
            Assert.AreEqual("1", QuickSlots.Covers.Control("ID_ITEMQUICKSLOT_COVER_COUNT1").GetComponentInChildren<UnityEngine.UI.Text>(true).text);
            mito = P.Mito;
            Assert.IsTrue(QuickSlots.Use(0), "number key 1");
            yield return null;
            Assert.AreEqual(mito + 10000, P.Mito, "cheque paid out");
            Assert.IsNull(QuickSlots.Entry(0), "used up");
            QuickSlots.Use(1);
            yield return null;
            Assert.AreEqual(1, P.Count("i_d_drk_01"), "the EXP drink is still refused while one works");
            CloseAllPopups();
            yield return new WaitForSeconds(0.3f);
            Shot("inv_6_quickslots.png");

            // ---- trade offline ----
            TradeWindow.Request("Somebody");
            yield return null;
            Assert.IsTrue(ui.Find(3250000), "offline: the trade needs the server");
            Assert.IsFalse(TradeWindow.IsOpen);
            CloseAllPopups();
        }

        static NuiControlView NSlot(int i) => InventoryWindow.Bag.Control($"ID_MYINVEN_SLOT_{i + 1}");

        /// <summary>
        /// The NDLG set (the default): frame 4730000 + bag 4800000 + parts 4810000 / car info 4860000 / performance 4780000,
        /// the field and battle panels beside it; set parts (fit, set outline and box, expiry to the "_to" form, the restore
        /// box with BUY / USE), period items (mark, tooltip line, no discard), a licence dragged onto the licence page, the
        /// slot extension and the auto quick slot on the HUD, and the battle slots in a race HUD. Screenshots Preview/inv_n*.png.
        /// </summary>
        [UnityTest]
        public IEnumerator NdlgInventorySetPartsPeriodsAndSlots()
        {
            new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            var flow = new GameObject("Game").AddComponent<GameFlow>();
            flow.SkipParkingLot = true;
            yield return FrontEndSteps.ToCharacterSelect(flow);
            yield return FrontEndSteps.EnterSelected();
            yield return WaitFor(() => flow.InCity && CitySession.Current, 600, "city");
            var ui = UiManager.Instance;
            var P = PlayerProfile.Current;
            yield return new WaitForSeconds(1f);
            InventoryWindow.Classic = false;
            Assert.IsNotNull(SetParts.Get("si_s01_003"), "ItemClient.tdf set rows");
            P.Level = 60;
            foreach (var id in new[] { "si_s01_001", "si_s01_002", "si_s01_003", "si_s01_004" }) P.Give(id, 1);
            ItemPeriods.GiveTimed(P, QuickSlots.AutoItem, 1, 7 * 24);
            P.Give(QuickSlots.ExtensionItem, 1);
            P.Give("i_n_00099", 3);
            P.Give("i_n_00056", 2);
            P.PerfParts.Add(new PerfPart { Uid = 50, Type = 3, Grade = 1, Item = "si_s01_001_to" });   // an expired set part in the bag
            P.NextPartUid = 51;
            var lic = Licences.All.Where(l => l.Obtainable).Take(2).ToList();
            foreach (var l in lic) P.Licences.Add(l.Id);
            P.MountedLicence = -1;

            // ---- the NDLG window ----
            Menus.Inventory();
            yield return null;
            yield return null;
            Assert.IsTrue(InventoryWindow.IsNdlg, "NDLG is the default set");
            Assert.IsTrue(ui.Find(4730000) && ui.Find(4800000) && ui.Find(4810000) && ui.Find(4860000) && ui.Find(4780000), "NDLG frame, bag, parts, car info, performance");
            Assert.IsFalse(ui.Find(3060000) || ui.Find(3190000), "not the classic dialogs");
            Assert.IsTrue(InventoryWindow.Bag.Control("ID_MYINVEN_SLOT_1"), "the NDLG bag answers to the classic control names");
            yield return WaitFor(() => QuickSlots.PanelBase && BattleQuickSlots.SetupView, 3, "field and battle panels beside the window");
            Assert.IsTrue(ui.Find(4760000) && ui.Find(4770000) && ui.Find(4740000) && ui.Find(4750000));
            var bag = ItemCatalog.Bag(P);
            Assert.AreEqual(2, bag.Count(e => e.Kind == BagKind.Licence), "earned licences wait in the bag");
            int autoIdx = ItemCatalog.Sorted(bag, 0).FindIndex(e => e.Key == "s:" + QuickSlots.AutoItem);
            Assert.IsTrue(autoIdx >= 0 && autoIdx < InventoryWindow.PerPage && InventoryWindow.Bag.Control($"ID_MYINVEN_SLOT_RENT{autoIdx:00}").gameObject.activeSelf, "period label on the timed item");
            Assert.AreEqual(1, InventoryWindow.ExpiredMarks, "the NDLG expiry mark on the expired set part only");
            StringAssert.Contains("hours", ItemPeriods.Line(ItemCatalog.Find(P, "s:" + QuickSlots.AutoItem)));
            Assert.IsNotNull(ItemUse.DiscardRefusal(ItemCatalog.Find(P, "s:" + QuickSlots.AutoItem)), "10455: no discarding a running period item");
            Assert.IsFalse(ItemUse.Tradeable(ItemCatalog.Find(P, "s:" + QuickSlots.AutoItem)), "period items stay with their owner");
            Shot("inv_n1_ndlg_parts.png");

            // ---- set parts: fit the four, the set outline and its bonus ----
            int[] before = PerfRules.Fitted(P, P.Car);
            foreach (var id in new[] { "si_s01_001", "si_s01_002", "si_s01_003", "si_s01_004" }) SetParts.Fit(InventoryWindow.CarSlot, id);
            yield return null;
            var set = SetParts.CarSet(P, P.Car);
            Assert.AreEqual("kayla", set.set);
            Assert.IsTrue(set.active, "four Kayla parts: the set bonus is on");
            var setParts = P.PerfParts.Where(x => !string.IsNullOrEmpty(x.Item) && x.Uid != 50).ToList();
            Assert.AreEqual(4, setParts.Count, "the stacks became parts");
            Assert.IsTrue(setParts.All(x => x.Until > ItemUse.Now + 700L * 3600_000), "720 h from the fit");
            int[] after = PerfRules.Fitted(P, P.Car);
            Assert.AreEqual(before[0] + 44 + 44, after[0], "speed: the part's 44 points and the set's Speed +44p");
            Assert.IsTrue(ui.Find(4810000).Control("ID_NDLGINVEN_SET_PARTS_OUTLINE1").gameObject.activeSelf, "set outline around row 1");
            Assert.IsTrue(ui.Find(4810000).Control("ID_NDLGINVEN_SLOTN_01").transform.Find("item"), "part in the speed slot");
            Assert.IsTrue(ui.Find(4810000).Control("ID_NDLGINVEN_S1_GLINE_BLUE").gameObject.activeSelf, "blue grade line for a set part");
            var speedSlot = ui.Find(4810000).Control("ID_NDLGINVEN_SLOTN_01").GetComponent<ItemSlot>();
            speedSlot.Hover(true);
            yield return null;
            Assert.IsTrue(InventoryWindow.SetBox && ui.Find(4870000), "the set box (NDLG 4870000)");
            StringAssert.Contains("Kayla", InventoryWindow.SetBox.Control("ID_INVEN_SETPARTS_TITLE").GetComponentInChildren<UnityEngine.UI.Text>(true).text);
            Shot("inv_n2_set_box.png");
            speedSlot.Hover(false);

            // ---- expiry: two parts run out and turn into the "_to" form ----
            setParts[0].Until = ItemUse.Now - 1;
            setParts[1].Until = ItemUse.Now - 1;
            ItemPeriods.Check(true);
            yield return null;
            Assert.IsTrue(setParts[0].Item.EndsWith("_to") && setParts[1].Item.EndsWith("_to"), "expired set parts");
            Assert.IsFalse(SetParts.CarSet(P, P.Car).active, "no bonus while a part is expired");
            var expiredEntry = new BagEntry { Kind = BagKind.Part, Part = setParts[0] };
            StringAssert.Contains("xpired", ItemPeriods.Line(expiredEntry), "10795 Expired");
            // right click on an expired part: the restore box; BUY then USE
            var expiredType = setParts[0].Type;
            ui.Find(4810000).Control($"ID_NDLGINVEN_SLOTN_{2 * expiredType + 1:00}").GetComponent<ItemSlot>().RightClick(ItemCatalog.Find(P, "p:" + setParts[0].Uid));
            yield return null;
            Assert.IsTrue(InventoryWindow.RestoreBox && ui.Find(3610000), "SETACTIVATE 3610000");
            long mito = P.Mito;
            ui.Find(3610000).Click("ID_INVEN_SETACTIVATE_BUY");
            yield return null;
            ui.Find(3250000).Click(UiPopup.Yes);
            yield return null;
            Assert.AreEqual(1, P.Count(SetParts.AlphaLube), "a lube bought");
            Assert.AreEqual(mito - 25000, P.Mito);
            Shot("inv_n3_restore.png");
            ui.Find(3610000).Click("ID_INVEN_SETACTIVATE_USE");
            yield return null;
            ui.Find(3250000).Click(UiPopup.Yes);
            yield return null;
            CloseAllPopups();
            Assert.AreEqual(0, P.Count(SetParts.AlphaLube), "the lube is spent");
            Assert.IsTrue(SetParts.CarSet(P, P.Car).active, "restored: the bonus is back");
            Assert.IsFalse(InventoryWindow.RestoreBox, "the box closes after USE");

            // ---- a licence dragged onto the licence page ----
            InventoryWindow.ShowPage(InventoryWindow.Page.Licence);
            yield return null;
            var drop = ui.Find(3080000).Control("ID_INVEN_LICENSE_POSITEM").GetComponent<ItemSlot>();
            var licEntry = ItemCatalog.Find(P, "l:" + lic[0].Id);
            Assert.IsTrue(drop.Accept(licEntry) && !drop.Accept(ItemCatalog.Find(P, "s:i_d_drk_01")), "only licences drop there");
            drop.Dropped(licEntry);
            yield return null;
            Assert.AreEqual(lic[0].Id, P.MountedLicence, "dragged licence mounted");
            Assert.IsNull(ItemCatalog.Find(P, "l:" + lic[0].Id), "the mounted licence left the bag");
            Assert.IsNotNull(ItemCatalog.Find(P, "l:" + lic[1].Id));
            Shot("inv_n4_licence.png");

            // ---- the other NDLG pages ----
            InventoryWindow.ShowPage(InventoryWindow.Page.Modify);
            yield return null;
            Assert.IsTrue(ui.Find(4850000), "NDLG modify page");
            InventoryWindow.ShowPage(InventoryWindow.Page.Auction);
            yield return null;
            Assert.IsTrue(ui.Find(4840000) && ui.Find(4840000).Control("ID_INVEN_EXCHANGE_TAB_ALL"), "NDLG auction page with the Auction page's control names");
            InventoryWindow.ShowPage(InventoryWindow.Page.DriftItem);
            yield return null;
            Assert.IsTrue(ui.Find(4820000), "NDLG drift item page");
            InventoryWindow.ShowPage(InventoryWindow.Page.Parts);
            yield return null;

            // ---- quick slots: the extension gives a third slot; the panel takes drops ----
            Assert.AreEqual(3, QuickSlots.Unlocked, "one extension: three slots");
            Assert.IsTrue(QuickSlots.Assign(2, ItemCatalog.Find(P, "s:i_d_drk_01")), "the third slot");
            Assert.IsFalse(QuickSlots.Assign(3, ItemCatalog.Find(P, "s:i_d_m_10k")), "no fourth slot");
            Assert.IsTrue(QuickSlots.PanelSlots.Control("ID_NDLGINVEN_FIELD_QSLOT3").gameObject.activeSelf && !QuickSlots.PanelSlots.Control("ID_NDLGINVEN_FIELD_QSLOT4").gameObject.activeSelf);
            Assert.IsTrue(BattleQuickSlots.Assign(0, ItemCatalog.Find(P, "s:i_n_00099")), "repair kit in battle slot 1");
            Assert.IsTrue(BattleQuickSlots.Assign(1, ItemCatalog.Find(P, "s:i_n_00056")), "recharger in battle slot 2");
            Assert.IsFalse(BattleQuickSlots.Assign(1, ItemCatalog.Find(P, "s:i_d_drk_01")), "only battle items");
            CloseAllPopups();
            yield return null;
            Shot("inv_n5_panels.png");
            InventoryWindow.Close();
            yield return null;
            Assert.IsFalse(QuickSlots.PanelBase || BattleQuickSlots.SetupView, "the panels go with the window");

            // ---- the HUD: three tiles, the auto box ----
            yield return WaitFor(() => QuickSlots.Tiles, 5, "quick slot HUD");
            Assert.IsTrue(QuickSlots.Tiles.Control("ID_ITEMQUICKSLOT_3").gameObject.activeSelf && !QuickSlots.Tiles.Control("ID_ITEMQUICKSLOT_4").gameObject.activeSelf, "three tiles");
            yield return null;
            Assert.IsTrue(QuickSlots.Covers.Control("ID_ITEMQUICKSLOT_CHK_NUM3").gameObject.activeSelf, "auto box on the drink's slot (i_autoquick in the bag)");
            int drinks = P.Count("i_d_drk_01");
            Assert.IsTrue(QuickSlots.SetAuto(2, true), "auto on");
            yield return null;
            Assert.AreEqual(drinks - 1, P.Count("i_d_drk_01"), "ticking uses it at once");
            Assert.IsTrue(ItemUse.HasBuff(P, "exp"));
            // the drink runs out: auto drinks the next one
            P.Buffs.Clear();
            yield return new WaitForSeconds(QuickSlots.Cooldown + 0.3f);
            yield return WaitFor(() => P.Count("i_d_drk_01") == drinks - 2, 3, "auto re-use");
            Shot("inv_n6_hud_auto.png");
            // without the Auto Quick-Slot item: 10970
            P.Give(QuickSlots.AutoItem, -P.Count(QuickSlots.AutoItem));
            Assert.IsFalse(QuickSlots.SetAuto(0, true));
            yield return null;
            Assert.IsTrue(ui.Find(3250000), "10970 popup");
            CloseAllPopups();

            // ---- the battle slots in a race HUD ----
            var rank = ui.Open(1380000);
            yield return WaitFor(() => BattleQuickSlots.Tiles, 3, "battle slots");
            Assert.IsTrue(ui.Find(3780000) && ui.Find(3790000));
            var effects = flow.PlayerCar.GetComponentInChildren<CarEffects>();
            if (effects) effects.Damage01 = 0.5f;
            string usedKind = null;
            System.Action<string, float> onUsed = (k, s) => usedKind = k;
            BattleQuickSlots.Used += onUsed;
            Assert.IsTrue(BattleQuickSlots.Use(0));
            yield return null;
            Assert.AreEqual(2, P.Count("i_n_00099"));
            Assert.AreEqual("repair", usedKind);
            if (effects) Assert.AreEqual(0.2f, effects.Damage01, 0.05f, "30 % repaired");
            float fill = flow.PlayerCar.BoostFillSeconds;
            Assert.IsTrue(BattleQuickSlots.Use(1));
            yield return null;
            // in the Battle Zone race the car's +0x1abc fill rate goes to 2 (PHYSICS.md 13.2); elsewhere the fill time halves
            if (flow.PlayerCar.PvpMode) Assert.AreEqual(2f, flow.PlayerCar.PvpFillRate, 1e-4f, "the recharger doubles the fill speed");
            else Assert.AreEqual(fill / 2f, flow.PlayerCar.BoostFillSeconds, 0.01f, "the recharger doubles the fill speed");
            Assert.IsTrue(BattleQuickSlots.IsActive("recharge"));
            BattleQuickSlots.Used -= onUsed;
            yield return new WaitForSeconds(0.3f);
            Shot("inv_n7_battle_slots.png");
            ui.Close(rank);
            yield return null;
        }
    }

    /// <summary>Trade with a bot (tools/bot.js --trade 1000) through the local server: offer, lock, confirm, the swap.</summary>
    public class InventoryOnlineTests
    {
        const int Port = 7873;
        static string Url => $"ws://127.0.0.1:{Port}";
        Process server, bot;
        string tempDir;
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

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            if (!File.Exists(Path.Combine(ServerDir, "server.js"))) Assert.Ignore("DriftCityServer not found next to the Unity project");
            tempDir = Path.Combine(Path.GetTempPath(), "driftcity_invonline_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            PlayerProfile.SaveDirOverride = tempDir;
            PlayerProfile.Slot = 0;
            PlayerProfile.Current = null;
            Directory.CreateDirectory("Preview");
            server = Node("server.js", new Dictionary<string, string> { ["DC_PORT"] = Port.ToString(), ["DC_DB"] = Path.Combine(tempDir, "test.db") });
            float t = 0;
            bool up = false;
            while (!up && t < 15f)
            {
                try { using (var c = new System.Net.Sockets.TcpClient()) { c.Connect("127.0.0.1", Port); up = true; } } catch { }
                if (!up) { yield return new WaitForSecondsRealtime(0.25f); t += 0.25f; }
            }
            Assert.IsTrue(up, "server started");
            bot = Node($"tools/bot.js 1 --trade 1000 --name TBot --url {Url}");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (OnlineSession.Instance) OnlineSession.Instance.Logout();
            foreach (var p in new[] { bot, server }) try { if (p != null && !p.HasExited) p.Kill(); } catch { }
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

        [UnityTest]
        public IEnumerator TradeWithBot()
        {
            if (!Camera.main) new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            var flow = new GameObject("Game").AddComponent<GameFlow>();
            flow.SkipParkingLot = true; flow.SkipIntro = true;
            yield return FrontEndSteps.ToCharacterSelect(flow, Url, "trader", "secret123");
            yield return WaitFor(() => UiManager.Instance.Find(3250000), 15, "create-account popup");
            UiManager.Instance.Find(3250000).Click(UiPopup.Yes);
            yield return FrontEndSteps.NewCharacter("trader", 1);
            UiManager.Instance.Find(150000).Click("ID_CREATECAR_NEW_NEXT");
            yield return WaitFor(() => PlayerProfile.Current != null && PlayerProfile.Current.OnlineDriver, 15, "server driver");
            yield return WaitFor(() => UiManager.Instance.Find(320000), 600, "city HUD");
            yield return WaitFor(() => OnlineSession.Instance.Channel != null, 15, "channel join");
            yield return WaitFor(() => OnlineSession.Instance.Remotes && OnlineSession.Instance.Remotes.Count > 0, 20, "bot in the channel");

            // two enhancers from the counter (server-checked) to trade one of them
            Progress.Result? op = null;
            Progress.Op("item.buy", new Dictionary<string, object> { ["id"] = "i_n_00003", ["count"] = 2 }, () => default, r => op = r);
            yield return WaitFor(() => op.HasValue, 10, "counter reply");
            Assert.IsTrue(op.Value.Ok, op.Value.Error);
            int enhancers = PlayerProfile.Current.Count("i_n_00003");
            long mito = PlayerProfile.Current.Mito;

            Menus.Inventory();
            yield return null;
            TradeWindow.Request("TBot1");
            yield return WaitFor(() => TradeWindow.IsOpen, 10, "trade window");
            Assert.IsFalse(InventoryWindow.IsOpen, "the trade window replaces the inventory");
            Assert.AreEqual("TBot1", TradeWindow.Partner);
            TradeWindow.Offer(ItemCatalog.Find(PlayerProfile.Current, "s:i_n_00003"), 1);
            yield return WaitFor(() => TradeWindow.State != null && TradeWindow.State.O("them") != null && (long)TradeWindow.State.O("them").N("mito") == 1000
                                       && TradeWindow.State.O("me").L("items").Count == 1, 10, "both offers on the table");
            yield return new WaitForSeconds(0.3f);
            var tex = UiManager.Instance.Capture(Camera.main);
            File.WriteAllBytes(Path.Combine("Preview", "inv_7_trade.png"), tex.EncodeToPNG());
            TradeWindow.Lock(true);
            yield return WaitFor(() => TradeWindow.State.O("them").B("locked") && TradeWindow.State.O("me").B("locked"), 10, "both locked");
            TradeWindow.Confirm();
            yield return WaitFor(() => TradeWindow.LastResult != null, 10, "trade closed");
            Assert.AreEqual("done", TradeWindow.LastResult);
            yield return WaitFor(() => PlayerProfile.Current.Mito == mito + 1000, 5, "the bot's Mito arrived");
            Assert.AreEqual(enhancers - 1, PlayerProfile.Current.Count("i_n_00003"), "one enhancer went to the bot");
            Assert.IsFalse(TradeWindow.IsOpen);
        }
    }
}
