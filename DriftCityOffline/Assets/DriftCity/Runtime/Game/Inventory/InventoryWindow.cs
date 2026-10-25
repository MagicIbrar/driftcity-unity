using System.Collections.Generic;
using System.Linq;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace DriftCity.Game
{
    /// <summary>
    /// The client's inventory (DriftCity_RE\INVENTORY.md). The client has two sets and 0x4dd420 picks one by the server
    /// config bit (net+0x294 & 0x600), for either main bar: the NDLG set (UI+0x168, vtable 0x8715b8: NDLGINVEN 4730000 frame,
    /// 4800000 bag, 4810000 parts with the set outlines, 4780000 performance info, 4820000 drift items, 4830000 sticker,
    /// 4840000 auction, 4850000 modify, 4860000 car info, 4870000 set box, the quick-slot panels 4740000-4770000) is the one
    /// this rebuild opens by default (the newest: its texts 11742-11780 sit with the ADV dealer's 11814+); the classic set
    /// stays selectable (<see cref="Classic"/>). The NDLG dialogs are opened with their control ids renamed to the classic
    /// names (ID_NDLGINVEN_MYINVEN_SLOT_1 -> ID_MYINVEN_SLOT_1 ...), so the pages below and the Auction page serve both.
    /// The classic set: the INVENBASE frame (3060000) with its tabs, one tab page and the
    /// item bag MYINVEN (3190000, 20 slots a page). The classic inventory class (vtable 0x8713a8; init 0x5d38e0, per-frame
    /// 0x5d92e0, parts page 0x5c7f10) opens these pages:
    ///   Parts      3130000 PARTSNEW + 3150000 car info + 3160000 / 3170000 points (detail / simple)
    ///   Drift Item 3140000 visual items of the car (5-slot column, car view)
    ///   Licence    3080000 (no tab image in this client; Tab cycles to it, TABKEY_LISENCEIMG)
    ///   Sticker    3100000, Auction 3090000 (AuctionPage: All / Selling / Sold, drop an item to register it)
    ///   Modify     3210000 (the COUPON tab, drawn "Modify"): part + Mittron Enhancer
    /// Bag slots: hover = ITEMINFO tooltip, right click = actions (use / equip, sell, discard), double click = use,
    /// drag = onto a part slot (equip), a quick slot (assign), the trade window (offer) or out of the window (discard).
    /// </summary>
    public static partial class InventoryWindow
    {
        public enum Page { Parts, DriftItem, Licence, Sticker, Auction, Modify }

        static PlayerProfile P => PlayerProfile.Current;
        static UiManager Ui => UiManager.Instance;

        public const int PerPage = 20;
        static readonly string[] TabIds = { "PARTSIMG", "VISUALITEMIMG", null, "STICKERIMG", "EXCHANGEIMG", "COUPON" };
        static readonly Page[] TabCycle = { Page.Parts, Page.DriftItem, Page.Licence, Page.Sticker, Page.Auction, Page.Modify };

        static NuiView frame, bag, pageA, pageB, pageC, setBox, restoreBox;
        static Page page;
        static bool ndlg;
        static string restoreLube;

        /// <summary>The classic inventory set instead of the NDLG one (kept on this PC; the NDLG set is the default).</summary>
        public static bool Classic
        {
            get { try { return PlayerPrefs.GetInt("DriftCity.Inventory.Classic", 0) == 1; } catch { return false; } }
            set { PlayerPrefs.SetInt("DriftCity.Inventory.Classic", value ? 1 : 0); PlayerPrefs.Save(); }
        }

        /// <summary>The open window uses the NDLG dialogs.</summary>
        public static bool IsNdlg => frame && ndlg;

        /// <summary>Classic dialog -> the NDLG one and the control-id prefix it renames (null prefix: opened as it is).</summary>
        static readonly Dictionary<int, (int id, string from, string to)> NdlgOf = new Dictionary<int, (int, string, string)>
        {
            [3060000] = (4730000, "ID_NDLGINVEN_", "ID_INVENBASE_"),
            [3190000] = (4800000, "ID_NDLGINVEN_MYINVEN_", "ID_MYINVEN_"),
            [3150000] = (4860000, "ID_NDLGINVEN_CARINFO_", "ID_INVEN_CARINFO_"),
            [3140000] = (4820000, "ID_NDLGINVEN_VISUALITEM_", "ID_INVEN_VISUALITEM_"),
            [3100000] = (4830000, "ID_NDLGINVEN_STICKER_", "ID_INVEN_STICKER_"),
            [3090000] = (4840000, "ID_NDLGINVEN_EXCHANGE_", "ID_INVEN_EXCHANGE_"),
            [3210000] = (4850000, "ID_NDLGINVEN_UPGRADE_", "ID_INVEN_UPGRADE_"),
            [3180000] = (4870000, "ID_NDLGINVEN_SETPARTS_", "ID_INVEN_SETPARTS_"),
            [3130000] = (4810000, null, null),
            [3160000] = (4780000, null, null),
            [3170000] = (4780000, null, null),
        };

        static readonly Dictionary<int, List<DriftCity.Core.NuiControl>> renamed = new Dictionary<int, List<DriftCity.Core.NuiControl>>();

        /// <summary>
        /// Opens a dialog of the window's set: classic ids as they are, NDLG dialogs with their controls renamed. The layout's
        /// control list is swapped for the renamed copies only while the view is built (synchronously), then put back.
        /// </summary>
        static NuiView OpenSet(int classicId, System.Func<int, NuiView> open)
        {
            if (!ndlg || !NdlgOf.TryGetValue(classicId, out var m)) return open(classicId);
            if (m.from == null) return open(m.id);
            var d = UiAssets.Layout?.Get(m.id);
            if (d == null) return open(classicId);
            if (!renamed.TryGetValue(m.id, out var list))
            {
                list = d.Controls.Select(c => new DriftCity.Core.NuiControl
                {
                    Id = c.Id, Group = c.Group, Info = c.Info, Nation = c.Nation,
                    IdName = c.IdName != null && c.IdName.StartsWith(m.from) ? m.to + c.IdName.Substring(m.from.Length) : c.IdName,
                }).ToList();
                renamed[m.id] = list;
            }
            var saved = d.Controls;
            try { d.Controls = list; return open(m.id); }
            finally { d.Controls = saved; }
        }

        static NuiView OpenPage(int classicId) => OpenSet(classicId, OriginalMenus.Open);

        /// <summary>The dialog id the window shows for a classic page id (tests, previews).</summary>
        public static int DialogFor(int classicId) => ndlg && NdlgOf.TryGetValue(classicId, out var m) ? m.id : classicId;
        static int bagPage, sort, carSlot, driftScroll;
        static bool simplePoints;
        static string selected, modifyPart, modifyEnhancer;
        static CarPreview preview;

        public static bool IsOpen => frame;
        public static Page Current => page;
        public static NuiView Frame => frame;
        public static NuiView Bag => bag;
        public static NuiView PageView => pageA;
        public static string Selected => selected;
        public static int CarSlot => carSlot;

        public static void Open() => Open(Page.Parts);

        public static void Open(Page start)
        {
            if (P == null || P.Cars.Count == 0) return;
            carSlot = Mathf.Clamp(P.CurrentCar, 0, P.Cars.Count - 1);
            bagPage = 0;
            selected = null;
            ndlg = !Classic && UiAssets.Layout?.Get(4730000) != null;
            frame = OpenSet(3060000, id => OriginalMenus.OpenScreen(id, () =>
            {
                if (CountPopup.IsOpen) { CountPopup.Close(); return true; }
                if (restoreBox) { CloseRestore(); return true; }
                return false;
            }));
            if (!frame) return;
            OriginalMenus.OnClose(Cleanup);
            bag = OpenPage(3190000);
            ItemPeriods.Check(true);
            frame.gameObject.AddComponent<Keys>();
            Ui.On("ID_INVENBASE_CLOSE", _ => OriginalMenus.Close());
            // TABKEY_* are invisible Tab-key (J=9) buttons over the tabs; Keys cycles the pages instead
            foreach (var t in new[] { "PARTSIMG", "VISUALITEMIMG", "LISENCEIMG", "STICKERIMG", "EXCHANGEIMG", "COUPON" }) frame.SetVisible("ID_INVENBASE_TABKEY_" + t, false);
            for (int i = 0; i < TabIds.Length; i++)
            {
                if (TabIds[i] == null) continue;
                var p = TabCycle[i];
                Ui.On("ID_INVENBASE_TAB_" + TabIds[i], _ => ShowPage(p));
            }
            Ui.On("ID_MYINVEN_PREV", _ => { bagPage = Mathf.Max(0, bagPage - 1); RefreshBag(); });
            Ui.On("ID_MYINVEN_NEXT", _ => { bagPage++; RefreshBag(); });
            Ui.On("ID_MYINVEN_SORT_DATE", _ => SetSort(0));
            Ui.On("ID_MYINVEN_SORT_TYPE", _ => SetSort(1));
            Ui.On("ID_MYINVEN_SORT_MITO", _ => SetSort(2));
            OriginalMenus.Hide(bag, "ID_MYINVEN_SORT_COMBOBOX", "ID_MYINVEN_SLOT_COVER", "ID_MYINVEN_PAGE_MITOCOVER", "ID_MYINVEN_PAGE_CURR_RED", "ID_MYINVEN_PAGE_MAX_RED", "ID_MYINVEN_EXPIRE_1");
            for (int i = 1; i <= PerPage; i++)
                OriginalMenus.Hide(bag, $"ID_MYINVEN_SLOT_HIGHGLOSS{i:00}", $"ID_MYINVEN_SLOT_RENT{i - 1:00}");
            // the bag's slots: hover, clicks and drags
            for (int i = 0; i < PerPage; i++)
            {
                var slot = bag.Control($"ID_MYINVEN_SLOT_{i + 1}");
                if (!slot) continue;
                int index = i;
                var s = ItemSlot.On((RectTransform)slot.transform);
                s.Entry = () => EntryAt(index);
                s.LeftClick = e => Select(e);
                s.RightClick = e => Actions(e);
                s.DoubleClick = e => ItemUse.Use(e, false, null, carSlot);
                s.DroppedOutside = e => AskDiscard(e);
                s.Window = (RectTransform)frame.transform.Find("origin");
            }
            ShowPage(start);
        }

        /// <summary>Redraws everything after a change made elsewhere (no-op when the window is closed).</summary>
        public static void RefreshIfOpen() { if (frame) Refresh(); }

        public static void Close() { if (frame) OriginalMenus.Close(); }

        static void Cleanup()
        {
            preview?.Release();
            preview = null;
            ItemTooltip.Hide();
            SetBoxHide();
            frame = bag = pageA = pageB = pageC = restoreBox = null;
            selected = modifyPart = modifyEnhancer = null;
        }

        static void SetSort(int s)
        {
            sort = s;
            bagPage = 0;
            AudioLibrary.PlaySfx("sfx/SID_ITEM_ARRAY");
            RefreshBag();
        }

        static List<BagEntry> entries = new List<BagEntry>();
        static BagEntry EntryAt(int slotIndex)
        {
            int i = bagPage * PerPage + slotIndex;
            return i >= 0 && i < entries.Count ? entries[i] : null;
        }

        public static void ShowPage(Page p)
        {
            if (!frame) return;
            ItemTooltip.Hide();
            SetBoxHide();
            CloseRestore();
            preview?.Release();
            preview = null;
            foreach (var v in new[] { pageA, pageB, pageC }) OriginalMenus.CloseView(v);
            pageA = pageB = pageC = null;
            page = p;
            switch (p)
            {
                case Page.Parts:
                    pageA = OpenPage(3130000);
                    pageB = OpenPage(3150000);
                    pageC = OpenPage(simplePoints ? 3170000 : 3160000);
                    SetupParts();
                    break;
                case Page.DriftItem:
                    pageA = OpenPage(3140000);
                    pageB = OpenPage(3150000);
                    SetupDriftItems();
                    break;
                case Page.Licence:
                    pageA = OriginalMenus.Open(3080000);
                    ShiftForNdlg(pageA);
                    LicenceInfo.SetupPage(pageA);
                    break;
                case Page.Sticker:
                    pageA = OpenPage(3100000);
                    OriginalMenus.Hide(pageA, "ID_INVEN_STICKER_ICANSTICKER", "ID_INVEN_STICKER_ICANSTICKER_MITO", "ID_INVEN_STICKER_ICANSTICKEREXPIRE");
                    preview = CarPreview.Create(pageA.Control("ID_INVEN_STICKER_BACKGROUND"), new Vector2(350, 411), "ID_INVEN_STICKER_CARVIEW_");
                    SetupStickers();   // the custom-mark page (Inventory/StickerPage.cs)
                    break;
                case Page.Auction:
                    pageA = OpenPage(3090000);
                    SetupAuction();
                    break;
                case Page.Modify:
                    pageA = OpenPage(3210000);
                    SetupModify();
                    break;
            }
            // the frame and the bag stay in front of nothing: pages draw between them
            if (bag) bag.transform.SetAsLastSibling();
            for (int i = 0; i < TabIds.Length; i++)
                if (TabIds[i] != null) OriginalMenus.Pressed(frame.Control("ID_INVENBASE_TAB_" + TabIds[i]), TabCycle[i] == p);
            UiPopup.BringToFront();
            Refresh();
        }

        static void Note(NuiView v, string anchorId, string text)
        {
            var a = v ? v.Control(anchorId) : null;
            if (!a) return;
            var cell = OriginalMenus.Cell(a, "note", new Vector2(10, 340), new Vector2(330, 60));
            OriginalMenus.Caption(cell, "text", text, "Calibri 13 700", new Color(1f, 0.85f, 0.4f), TextAnchor.MiddleCenter, wrap: true);
        }

        static void Refresh()
        {
            if (!frame || P == null) return;
            carSlot = Mathf.Clamp(carSlot, 0, P.Cars.Count - 1);
            switch (page)
            {
                case Page.Parts: RefreshParts(); break;
                case Page.DriftItem: RefreshDriftItems(); break;
                case Page.Licence: LicenceInfo.RefreshPage(pageA); break;
                case Page.Modify: RefreshModify(); break;
            }
            RefreshBag();
        }

        // ================= the bag =================
        static void RefreshBag()
        {
            if (!bag || P == null) return;
            entries = ItemCatalog.Sorted(ItemCatalog.Bag(P), sort);
            int pages = Mathf.Max(1, (entries.Count + PerPage - 1) / PerPage);
            bagPage = Mathf.Clamp(bagPage, 0, pages - 1);
            bag.SetText("ID_MYINVEN_PAGE_CURR", (bagPage + 1).ToString());
            bag.SetText("ID_MYINVEN_PAGE_MAX", pages.ToString());
            bag.SetEnabled("ID_MYINVEN_PREV", bagPage > 0);
            bag.SetEnabled("ID_MYINVEN_NEXT", bagPage < pages - 1);
            bag.SetText("ID_MYINVEN_MITO", $"{P.Mito:N0}");
            foreach (var (id, s) in new[] { ("ID_MYINVEN_SORT_DATE", 0), ("ID_MYINVEN_SORT_TYPE", 1), ("ID_MYINVEN_SORT_MITO", 2) })
                OriginalMenus.Pressed(bag.Control(id), sort == s);
            var car = P.Cars[carSlot];
            NuiControlView inUse = null;
            for (int i = 0; i < PerPage; i++)
            {
                var slot = bag.Control($"ID_MYINVEN_SLOT_{i + 1}");
                if (!slot) continue;
                var e = EntryAt(i);
                ShowEntry(slot, e, e != null && e.Key == selected);
                bool badge = e != null && e.Kind == BagKind.Part && e.Part.Enhance > 0;
                bag.SetVisible($"ID_MYINVEN_UPGRADE{i + 1:00}", badge);
                if (badge) { bag.SetText($"ID_MYINVEN_UPGRADE{i + 1:00}", $"+{e.Part.Enhance}"); bag.Control($"ID_MYINVEN_UPGRADE{i + 1:00}").transform.SetAsLastSibling(); }
                if (e != null && e.Kind == BagKind.Shop && e.ItemId == car.PaintItem) inUse = slot;
            }
            MarkPeriods();
            // "in use" on the paint the shown car wears
            var using_ = bag.Control("ID_MYINVEN_SLOT_USING");
            if (using_)
            {
                using_.gameObject.SetActive(inUse);
                if (inUse) ((RectTransform)using_.transform).anchoredPosition = ((RectTransform)inUse.transform).anchoredPosition + new Vector2(2, -50);
                using_.transform.SetAsLastSibling();
            }
        }

        /// <summary>A bag slot: the item's icon (paints show their colour), its count, the selection frame.</summary>
        internal static void ShowEntry(NuiControlView slot, BagEntry e, bool isSelected)
        {
            OriginalMenus.Remove(slot.transform, "item");
            foreach (var ri in slot.GetComponentsInChildren<RawImage>(true)) if (ri.transform.parent == slot.transform) ri.enabled = isSelected;
            if (e == null) return;
            var tile = OriginalMenus.ItemTile(slot.transform, new Vector2(13, 11), new Vector2(46, 46), e.Icon, e.Name, ItemCatalog.Swatch(e), e.Count, ItemPeriods.IsExpired(e) ? 0.5f : 1f);
            foreach (var g in tile.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
        }

        static void Select(BagEntry e)
        {
            if (e == null) return;
            AudioLibrary.PlaySfx("sfx/SID_ITEM_CLICK");
            selected = e.Key;
            if (page == Page.Modify)
            {
                if (e.Kind == BagKind.Part) modifyPart = e.Key;
                else if (e.Kind == BagKind.Stack && IsEnhancer(e.Id)) modifyEnhancer = e.Key;
                RefreshModify();
            }
            if (TradeWindow.IsOpen && ItemUse.Tradeable(e)) TradeWindow.Offer(e);
            RefreshBag();
        }

        /// <summary>Right click: what can be done with the item, on the client's popup buttons (Yes = use / equip, Sell, Delete).</summary>
        public static void Actions(BagEntry e)
        {
            if (e == null) return;
            selected = e.Key;
            RefreshBag();
            var buttons = new List<string>();
            bool usable = ItemUse.Usable(e);
            if (usable) buttons.Add(UiPopup.Yes);
            if (ItemUse.SellPrice(e) > 0) buttons.Add(UiPopup.Sell);
            buttons.Add("ID_POPUP_DELETE");
            buttons.Add(UiPopup.Cancel);
            string what = e.Kind == BagKind.Part ? $"Equip it on your {OriginalMenus.CarName(P.Cars[carSlot])}?" : usable ? "Use it?" : "";
            string price = ItemUse.SellPrice(e) > 0 ? $"\nSells for {ItemUse.SellPrice(e):N0} Mito." : "";
            UiPopup.Show($"{e.Name}{(e.Count > 1 ? $"  x{e.Count}" : "")}\n{ItemCatalog.Desc(e)}{price}\n{what}".Trim(), b =>
            {
                if (b == UiPopup.Yes) ItemUse.Use(e, e.Kind != BagKind.Stack, null, carSlot);
                else if (b == UiPopup.Sell) AskSell(e);
                else if (b == "ID_POPUP_DELETE") AskDiscard(e);
            }, buttons.ToArray());
        }

        /// <summary>Discard as the client asks it: one item "Currently selected item will be deleted." (10509), a stack its count first (11126).</summary>
        public static void AskDiscard(BagEntry e)
        {
            if (e == null) return;
            if (e.Count <= 1 || e.Kind == BagKind.Part)
            {
                UiPopup.Show($"{e.Name}\n{ItemCatalog.Text("10509", "Currently selected item will be deleted.")}", b => { if (b == "ID_POPUP_DELETE") ItemUse.Discard(e, 1); }, "ID_POPUP_DELETE", UiPopup.Cancel);
                return;
            }
            CountPopup.Show(ItemCatalog.Text("11126", "Enter the quantity of item to discard.\nPossession Amount %d", e.Count), e.Count, 1, n => ItemUse.Discard(e, n));
        }

        static void AskSell(BagEntry e)
        {
            long unit = ItemUse.UnitSellPrice(e);
            if (e.Count > 1 && e.Kind == BagKind.Stack)
            {
                CountPopup.Show($"{e.Name}: {unit:N0} Mito each. How many do you sell?", e.Count, e.Count, n => ItemUse.Sell(e, n));
                return;
            }
            UiPopup.Show($"Sell {e.Name} for {unit:N0} Mito?", b => { if (b == UiPopup.Yes) ItemUse.Sell(e, 1); }, UiPopup.Yes, UiPopup.No);
        }

        // ================= parts page =================
        static readonly string[] StatKeys = { "SPD", "ACC", "COL", "BST" };

        static void SetupParts()
        {
            Ui.On("ID_INVEN_CARINFO_PREVCAR", _ => { int n = P.Cars.Count; carSlot = (carSlot + n - 1) % n; Refresh(); });
            Ui.On("ID_INVEN_CARINFO_NEXTCAR", _ => { int n = P.Cars.Count; carSlot = (carSlot + 1) % n; Refresh(); });
            if (ndlg) { SetupPartsNdlg(); return; }
            Ui.On("ID_INVEN_POINTDETAIL_SIMPLEVIEW", _ => SwitchPoints(true));
            Ui.On("ID_INVEN_POINTSIMPLE_DETAILVIEW", _ => SwitchPoints(false));
            OriginalMenus.Hide(pageB, "ID_INVEN_CARINFO_RENT");
            for (int i = 1; i <= 8; i++)
                OriginalMenus.Hide(pageA, $"ID_INVEN_PARTSNEW_SLOT_{i:00}", $"ID_INVEN_PARTSNEW_OPTSLOT_{i:00}");
            OriginalMenus.Hide(pageA, "ID_INVEN_PARTSNEW_SETROW_1ST", "ID_INVEN_PARTSNEW_SETROW_2ND");
            // the first slot of each stat column holds the fitted part: drop a part on it, right click takes it off
            for (int t = 0; t < 4; t++)
            {
                var slot = pageA.Control($"ID_INVEN_PARTSNEW_SLOTN_{2 * t + 1:00}");
                if (slot) PartSlot((RectTransform)slot.transform, t);
            }
        }

        /// <summary>
        /// A stat slot of the car: shows the fitted part; drop a part or a set part from the bag on it (set parts start their
        /// period when fitted), double click takes it off, right click takes it off too, except an expired set part, whose
        /// right click opens the restore box (SETACTIVATE 3610000). Hover shows the set box for set parts.
        /// </summary>
        static void PartSlot(RectTransform rt, int type)
        {
            var s = ItemSlot.On(rt);
            s.Entry = () => FittedEntry(type);
            s.RightClick = e =>
            {
                if (e == null) return;
                var row = SetParts.Of(e.Part);
                if (row != null && row.Expired && SetParts.LubeItem(row) != null) { ShowRestore(SetParts.LubeItem(row)); return; }
                ItemUse.Unequip(carSlot, type);
            };
            s.DoubleClick = e => { if (e != null) ItemUse.Unequip(carSlot, type); };
            s.Accept = e => e.Kind == BagKind.Part || e.Kind == BagKind.Stack && SetParts.Is(e.Id);
            s.Dropped = e =>
            {
                int t = e.Kind == BagKind.Part ? e.Part.Type : SetParts.Get(e.Id).Type;
                if (t != type) { UiPopup.Message(ItemCatalog.Text("10933", "You cannot equip this part.")); return; }
                if (e.Kind == BagKind.Part) ItemUse.Equip(e.Part, carSlot);
                else SetParts.Fit(carSlot, e.Id);
            };
            s.Hover = on => { if (on) SetBoxShow(FittedEntry(type)?.Part, rt); else SetBoxHide(); };
        }

        static BagEntry FittedEntry(int type)
        {
            var car = P?.Cars[Mathf.Clamp(carSlot, 0, P.Cars.Count - 1)];
            int uid = car?.PerfSlots != null && type < car.PerfSlots.Length ? car.PerfSlots[type] : -1;
            var part = uid >= 0 ? P.PerfParts.Find(x => x.Uid == uid) : null;
            return part != null ? new BagEntry { Kind = BagKind.Part, Part = part } : null;
        }

        static void SwitchPoints(bool simple)
        {
            if (!frame || page != Page.Parts || simplePoints == simple) return;
            simplePoints = simple;
            OriginalMenus.CloseView(pageC);
            pageC = OriginalMenus.Open(simple ? 3170000 : 3160000);
            if (bag) bag.transform.SetAsLastSibling();
            Refresh();
        }

        static void RefreshParts()
        {
            if (ndlg) { RefreshPartsNdlg(); return; }
            var owned = P.Cars[carSlot];
            var info = GameTables.Car(owned.VehicleIndex);
            OriginalMenus.Plate(pageB, "ID_INVEN_CARINFO_CARNAME_POS", OriginalMenus.CarName(owned), info);
            OriginalMenus.Emblem(pageB, "ID_INVEN_CARINFO_CARGRADE_POS", ShopData.Grade(info, owned.Grade));
            ShowRent(pageB, owned);
            pageB.SetVisible("ID_INVEN_CARINFO_CURCAR", carSlot == P.CurrentCar);
            pageB.SetEnabled("ID_INVEN_CARINFO_PREVCAR", P.Cars.Count > 1);
            pageB.SetEnabled("ID_INVEN_CARINFO_NEXTCAR", P.Cars.Count > 1);

            for (int t = 0; t < 4; t++)
            {
                string first = $"{2 * t + 1:00}", second = $"{2 * t + 2:00}";
                var e = FittedEntry(t);
                pageA.SetVisible("ID_INVEN_PARTSNEW_SLOTX_" + first, false);
                pageA.SetVisible("ID_INVEN_PARTSNEW_SLOTX_" + second, true);
                pageA.SetVisible("ID_INVEN_PARTSNEW_SLOTN_" + second, false);
                var slot = pageA.Control("ID_INVEN_PARTSNEW_SLOTN_" + first);
                if (slot)
                {
                    OriginalMenus.Remove(slot.transform, "item");
                    OriginalMenus.Remove(slot.transform, "caption");
                    if (e != null)
                    {
                        var tile = OriginalMenus.ItemTile(slot.transform, new Vector2(3, 3), new Vector2(48, 48), e.Icon, e.Name);
                        foreach (var g in tile.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
                    }
                }
                pageA.SetVisible("ID_INVEN_PARTSNEW_UPGRADE" + first, e != null && e.Part.Enhance > 0);
                pageA.SetVisible("ID_INVEN_PARTSNEW_UPGRADE" + second, false);
                if (e != null) { pageA.SetText("ID_INVEN_PARTSNEW_UPGRADE" + first, $"+{e.Part.Enhance}"); pageA.Control("ID_INVEN_PARTSNEW_UPGRADE" + first)?.transform.SetAsLastSibling(); }
            }
            // the set row frame around the four parts when they form one set
            var carSet = SetParts.CarSet(P, owned);
            pageA.SetVisible("ID_INVEN_PARTSNEW_SETROW_1ST", carSet.set != null);

            if (simplePoints)
            {
                var st = OriginalMenus.Breakdown(owned);
                int all = 0;
                for (int i = 0; i < 4; i++)
                {
                    int total = st.car[i] + st.parts[i] + st.user[i];
                    pageC.SetText($"ID_INVEN_POINTSIMPLE_{StatKeys[i]}_TOTAL", total.ToString());
                    all += total;
                }
                pageC.SetText("ID_INVEN_POINTSIMPLE_ALL_TOTAL", all.ToString());
                OriginalMenus.Hide(pageC, "ID_INVEN_POINTSIMPLE_UPGRADE_TXT", "ID_INVEN_POINTSIMPLE_SPECIAL1_NUM", "ID_INVEN_POINTSIMPLE_SPECIAL2_NUM",
                     "ID_INVEN_POINTSIMPLE_UPGRADE_TOTAL", "ID_INVEN_POINTSIMPLE_CAR_LEVEL", "ID_INVEN_POINTSIMPLE_UPGRADE_NUM");
            }
            else
            {
                var sums = OriginalMenus.FillStats(pageC, "ID_INVEN_POINTDETAIL_", owned, "PART");
                pageC.SetText("ID_INVEN_POINTDETAIL_ALL_TOTAL", sums.total.ToString());
                pageC.SetText("ID_INVEN_POINTDETAIL_CAR_TOTAL", sums.car.ToString());
                pageC.SetText("ID_INVEN_POINTDETAIL_PART_TOTAL", sums.parts.ToString());
                pageC.SetText("ID_INVEN_POINTDETAIL_USER_TOTAL", sums.user.ToString());
            }
        }

        // ================= drift items (visual parts of the car) =================
        const int DriftRows = 5;

        static void SetupDriftItems()
        {
            Ui.On("ID_INVEN_CARINFO_PREVCAR", _ => { int n = P.Cars.Count; carSlot = (carSlot + n - 1) % n; driftScroll = 0; preview?.Show(P.Cars[carSlot]); Refresh(); });
            Ui.On("ID_INVEN_CARINFO_NEXTCAR", _ => { int n = P.Cars.Count; carSlot = (carSlot + 1) % n; driftScroll = 0; preview?.Show(P.Cars[carSlot]); Refresh(); });
            Ui.On("ID_INVEN_VISUALITEM_SLOTUP", _ => { driftScroll = Mathf.Max(0, driftScroll - 1); RefreshDriftItems(); });
            Ui.On("ID_INVEN_VISUALITEM_SLOTDOWN", _ => { driftScroll++; RefreshDriftItems(); });
            OriginalMenus.Hide(pageB, "ID_INVEN_CARINFO_RENT");
            preview = CarPreview.Create(pageA.Control("ID_INVEN_VISUALITEM_BACKGROUND"), new Vector2(300, 411), "ID_INVEN_VISUALITEM_CARVIEW_");
            preview?.Show(P.Cars[carSlot]);
            var first = pageA.Control("ID_INVEN_VISUALITEM_SLOT1");
            if (!first) return;
            for (int i = 0; i < DriftRows; i++)
            {
                var cell = i == 0 ? (RectTransform)first.transform : OriginalMenus.Cell(first, "drift" + i, new Vector2(0, 42 * i), new Vector2(42, 42));
                int row = i;
                var s = ItemSlot.On(cell);
                s.Visual = () => DriftPart(row);
                s.LeftClick = _ => ToggleDriftPart(row);
                s.RightClick = s.LeftClick;
            }
        }

        static List<PartItem> DriftParts()
        {
            var car = P.Cars[carSlot];
            return car.Parts.Distinct().Select(PartsCatalog.Get).Where(x => x != null).OrderBy(x => x.Slot).ThenBy(x => x.Name).ToList();
        }

        static PartItem DriftPart(int row)
        {
            var list = DriftParts();
            int i = driftScroll + row;
            return i >= 0 && i < list.Count ? list[i] : null;
        }

        static void RefreshDriftItems()
        {
            var owned = P.Cars[carSlot];
            var info = GameTables.Car(owned.VehicleIndex);
            OriginalMenus.Plate(pageB, "ID_INVEN_CARINFO_CARNAME_POS", OriginalMenus.CarName(owned), info);
            OriginalMenus.Emblem(pageB, "ID_INVEN_CARINFO_CARGRADE_POS", ShopData.Grade(info, owned.Grade));
            pageB.SetVisible("ID_INVEN_CARINFO_CURCAR", carSlot == P.CurrentCar);
            var list = DriftParts();
            driftScroll = Mathf.Clamp(driftScroll, 0, Mathf.Max(0, list.Count - DriftRows));
            pageA.SetEnabled("ID_INVEN_VISUALITEM_SLOTUP", driftScroll > 0);
            pageA.SetEnabled("ID_INVEN_VISUALITEM_SLOTDOWN", driftScroll + DriftRows < list.Count);
            var first = pageA.Control("ID_INVEN_VISUALITEM_SLOT1");
            if (!first) return;
            for (int i = 0; i < DriftRows; i++)
            {
                var cell = i == 0 ? first.transform : first.transform.parent.Find("drift" + i);
                if (!cell) continue;
                OriginalMenus.Remove(cell, "item");
                var part = DriftPart(i);
                if (part == null) continue;
                bool on = owned.Fitted(part.Slot) == part.Id;
                var tile = OriginalMenus.ItemTile(cell, new Vector2(2, 2), new Vector2(38, 38), part.Item, part.Name, null, 0, on ? 1f : 0.55f);
                if (on) OriginalMenus.Caption(tile, "on", "ON", "Calibri 10 700", new Color(0.5f, 1f, 0.5f), TextAnchor.LowerRight);
                foreach (var g in tile.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
            }
            if (list.Count == 0) Note(pageA, "ID_INVEN_VISUALITEM_BACKGROUND", "No drift items on this car yet. The custom shop (P) sells aero kits, wheels, wings, neon and more.");
            else if (pageA.Control("ID_INVEN_VISUALITEM_BACKGROUND")) OriginalMenus.Remove(pageA.Control("ID_INVEN_VISUALITEM_BACKGROUND").transform.parent, "note");
        }

        static void ToggleDriftPart(int row)
        {
            var part = DriftPart(row);
            if (part == null) return;
            var owned = P.Cars[carSlot];
            bool on = owned.Fitted(part.Slot) == part.Id;
            if (carSlot == P.CurrentCar && GameFlow.BlockedByMission()) return;
            Progress.FitPart(carSlot, part.Slot, on ? -1 : part.Id, r =>
            {
                if (!r.Ok) return;
                AudioLibrary.PlaySfx("sfx/SID_ITEM_INSTALL");
                if (carSlot == P.CurrentCar) { var flow = GameFlow.Instance; if (flow) { flow.RespawnCar(); flow.ApplyCarStats(); } }
                preview?.Show(P.Cars[carSlot]);
                Refresh();
                if (!on && part.Slot == global::DriftCity.Game.PartSlot.Aero) AeroSet.Offer(P.Cars[carSlot]);   // an aero kit offers its bonus points (AeroSet 3900000)
            });
        }

        // ================= auction (Auction/AuctionPage.cs) =================
        /// <summary>All / Selling / Sold lists of the Auction House; bag items dropped on the page are registered.</summary>
        static void SetupAuction() => AuctionPage.Setup(pageA);

        // ================= modify (part + Mittron Enhancer) =================
        static bool IsEnhancer(string id) => id == "i_n_00003" || id == "i_n_00003_plus" || id == "i_n_00003_deluxe";

        static void SetupModify()
        {
            Ui.On("ID_INVEN_UPGRADE_BUTTON", _ => DoModify());
            // NDLG labels (texts 11742-11747)
            pageA.SetText("ID_INVEN_UPGRADE_TXT_BEFORE", ItemCatalog.Text("11743", "[Default Part Specs]"));
            pageA.SetText("ID_INVEN_UPGRADE_TXT_AFTER", ItemCatalog.Text("11742", "[Part Specs after Modification]"));
            pageA.SetText("ID_INVEN_UPGRADE_TXT_PARTS", ItemCatalog.Text("11745", "Parts"));
            pageA.SetText("ID_INVEN_UPGRADE_TXT_OP", ItemCatalog.Text("11744", "Enhancer"));
            pageA.SetText("ID_INVEN_UPGRADE_TXT_MITO", ItemCatalog.Text("11747", "Modification Cost"));
            foreach (var (id, isPart) in new[] { ("ID_INVEN_UPGRADE_SLOT1", true), ("ID_INVEN_UPGRADE_SLOT2", false) })
            {
                var c = pageA.Control(id);
                if (!c) continue;
                bool part = isPart;
                var s = ItemSlot.On((RectTransform)c.transform);
                s.Entry = () => ItemCatalog.Find(P, part ? modifyPart : modifyEnhancer);
                s.Accept = e => part ? e.Kind == BagKind.Part : e.Kind == BagKind.Stack && IsEnhancer(e.Id);
                s.Dropped = e => { if (part) modifyPart = e.Key; else modifyEnhancer = e.Key; RefreshModify(); };
                s.RightClick = _ => { if (part) modifyPart = null; else modifyEnhancer = null; RefreshModify(); };
            }
            if (modifyEnhancer == null)
            {
                var enh = new[] { "i_n_00003", "i_n_00003_plus", "i_n_00003_deluxe" }.FirstOrDefault(x => P.Count(x) > 0);
                if (enh != null) modifyEnhancer = "s:" + enh;
            }
        }

        static void RefreshModify()
        {
            if (!pageA || page != Page.Modify) return;
            var pe = ItemCatalog.Find(P, modifyPart);
            var ee = ItemCatalog.Find(P, modifyEnhancer);
            if (pe == null) modifyPart = null;
            if (ee == null) modifyEnhancer = null;
            foreach (var (id, e, badge) in new[] { ("ID_INVEN_UPGRADE_SLOT1", pe, "ID_INVEN_UPGRADE_UPGRADE1"), ("ID_INVEN_UPGRADE_SLOT2", ee, "ID_INVEN_UPGRADE_UPGRADE2") })
            {
                var c = pageA.Control(id);
                if (!c) continue;
                OriginalMenus.Remove(c.transform, "item");
                if (e != null)
                {
                    var tile = OriginalMenus.ItemTile(c.transform, new Vector2(10, 10), new Vector2(64, 64), e.Icon, e.Name, null, e.Kind == BagKind.Stack ? e.Count : 0);
                    foreach (var g in tile.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
                }
                bool show = e != null && e.Kind == BagKind.Part && e.Part.Enhance > 0;
                pageA.SetVisible(badge, show);
                if (show) { pageA.SetText(badge, $"+{e.Part.Enhance}"); pageA.Control(badge)?.transform.SetAsLastSibling(); }
            }
            int fee = pe != null ? PerfRules.EnhanceFee(pe.Part, Licences.Bonus(P, "ME_EnhanceFeeDis", "ME_FeeDis")) : 0;
            pageA.SetText("ID_INVEN_UPGRADE_MITO", $"{fee:N0}");
            if (pe != null)
            {
                var after = new PerfPart { Uid = pe.Part.Uid, Type = pe.Part.Type, Grade = pe.Part.Grade, Enhance = Mathf.Min(PerfRules.MaxEnhance, pe.Part.Enhance + 1) };
                pageA.SetText("ID_INVEN_UPGRADE_ORIGINAL", $"{ItemCatalog.PartName(pe.Part)}\n{ItemCatalog.PartStat(pe.Part)}");
                string chance = ee != null ? $"\n{PerfRules.EnhanceChance(pe.Part.Enhance, ee.Id)}% chance" : "";
                pageA.SetText("ID_INVEN_UPGRADE_UPGRADE", pe.Part.Enhance >= PerfRules.MaxEnhance ? "Already at the top level." : $"{ItemCatalog.PartName(after)}\n{ItemCatalog.PartStat(after)}{chance}");
            }
            else
            {
                pageA.SetText("ID_INVEN_UPGRADE_ORIGINAL", "Click a part in your inventory.");
                pageA.SetText("ID_INVEN_UPGRADE_UPGRADE", ee == null ? "You need a Mittron Enhancer." : "");
            }
            pageA.SetEnabled("ID_INVEN_UPGRADE_BUTTON", pe != null && ee != null && pe.Part.Enhance < PerfRules.MaxEnhance && P.Mito >= fee);
        }

        static void DoModify()
        {
            var pe = ItemCatalog.Find(P, modifyPart);
            var ee = ItemCatalog.Find(P, modifyEnhancer);
            if (pe == null || ee == null) return;
            int fee = PerfRules.EnhanceFee(pe.Part, Licences.Bonus(P, "ME_EnhanceFeeDis", "ME_FeeDis"));
            if (P.Mito < fee) { UiPopup.Message(ItemCatalog.Text("10743", "Not enough Mito for modification.")); return; }
            int uid = pe.Part.Uid;
            string enhancer = ee.Id;
            string before = ItemCatalog.PartName(pe.Part);
            System.Action go = () => Progress.Op("perf.enhance", new Dictionary<string, object> { ["uid"] = uid, ["enhancer"] = enhancer }, () =>
            {
                var part = P.PerfParts.Find(x => x.Uid == uid);
                if (part == null) return new Progress.Result { Error = "That part is gone." };
                if (P.Count(enhancer) <= 0) return new Progress.Result { Error = "No enhancer." };
                if (P.Mito < fee) return new Progress.Result { Error = ItemCatalog.Text("10743", "Not enough Mito for modification.") };
                P.Give(enhancer, -1); P.Mito -= fee;
                bool ok = Random.Range(0, 100) < PerfRules.EnhanceChance(part.Enhance, enhancer);
                if (ok) { part.Enhance++; P.MaxEnhance = Mathf.Max(P.MaxEnhance, part.Enhance); }
                else if (part.Enhance >= 5) part.Enhance--;
                return new Progress.Result { Ok = true, Error = ok ? null : "failed" };
            }, r =>
            {
                bool success = r.Ok && r.Error == null;
                if (r.Ok)
                {
                    GameEffects.Upgrade(success ? GameEffects.UpgradeSuccess : GameEffects.UpgradeFail);   // 0x616060
                    UiPopup.Message(success ? ItemCatalog.Text("10765", "Succeed to upgrade %s", before) : ItemCatalog.Text("10766", "Failed to upgrade %s.", before));
                }
                GameFlow.Instance?.ApplyCarStats();
                Refresh();
            });
            if (pe.Part.Enhance >= 5) UiPopup.Show(ItemCatalog.Text("10888", "Modifying may be unsuccessful or result in breaking the part."), b => { if (b == UiPopup.Yes) go(); }, UiPopup.Yes, UiPopup.No);
            else go();
        }

        // ================= keys =================
        /// <summary>Tab cycles the pages (TABKEY_* J=9), F10 / F11 / F12 sort the bag (SORT_TYPE / DATE / MITO J=121..123).</summary>
        sealed class Keys : MonoBehaviour
        {
            void Update()
            {
                if (UiPopup.IsOpen || Net.ChatBox.Typing || CountPopup.IsOpen) return;
#if ENABLE_INPUT_SYSTEM
                var kb = Keyboard.current;
                if (kb == null) return;
                bool tab = kb.tabKey.wasPressedThisFrame, f10 = kb.f10Key.wasPressedThisFrame, f11 = kb.f11Key.wasPressedThisFrame, f12 = kb.f12Key.wasPressedThisFrame;
                bool back = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
#else
                bool tab = Input.GetKeyDown(KeyCode.Tab), f10 = Input.GetKeyDown(KeyCode.F10), f11 = Input.GetKeyDown(KeyCode.F11), f12 = Input.GetKeyDown(KeyCode.F12);
                bool back = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
#endif
                if (tab)
                {
                    int i = System.Array.IndexOf(TabCycle, page);
                    ShowPage(TabCycle[(i + (back ? TabCycle.Length - 1 : 1)) % TabCycle.Length]);
                }
                if (f10) SetSort(1);
                if (f11) SetSort(0);
                if (f12) SetSort(2);
            }
        }
    }

    /// <summary>
    /// An item slot of the original dialogs made interactive: hover tooltip, left / right / double click and drag & drop.
    /// The entry is read when needed (pages and sorting change what a slot holds).
    /// </summary>
    public sealed class ItemSlot : MonoBehaviour, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        public System.Func<BagEntry> Entry;
        /// <summary>A visual-shop part (drift item cells) instead of a bag entry: tooltip only.</summary>
        public System.Func<PartItem> Visual;
        public System.Action<BagEntry> LeftClick, RightClick, DoubleClick, Dropped, DroppedOutside;
        public System.Func<BagEntry, bool> Accept;
        /// <summary>Pointer over / off the slot (the set box beside a set part).</summary>
        public System.Action<bool> Hover;
        /// <summary>The window a drag must leave to count as "dropped outside" (discard).</summary>
        public RectTransform Window;

        static RawImage ghost;
        static BagEntry dragging;
        public static BagEntry Dragging => dragging;

        public static ItemSlot On(RectTransform rt)
        {
            if (!rt) return null;
            if (rt.TryGetComponent<Graphic>(out var g)) g.raycastTarget = true;
            else rt.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, 0.001f);
            if (!rt.TryGetComponent<ItemSlot>(out var s)) s = rt.gameObject.AddComponent<ItemSlot>();
            return s;
        }

        public void OnPointerEnter(PointerEventData e)
        {
            if (dragging != null) return;
            var entry = Entry?.Invoke();
            Hover?.Invoke(true);
            if (entry != null) { ItemTooltip.Show(entry, (RectTransform)transform); return; }
            var part = Visual?.Invoke();
            if (part != null) PartTip(part, (RectTransform)transform);
        }

        public void OnPointerExit(PointerEventData e) { ItemTooltip.Hide(); Hover?.Invoke(false); }

        public void OnPointerClick(PointerEventData e)
        {
            if (e.dragging) return;
            var entry = Entry?.Invoke();
            if (e.button == PointerEventData.InputButton.Right) { ItemTooltip.Hide(); RightClick?.Invoke(entry); return; }
            if (e.button != PointerEventData.InputButton.Left) return;
            if (e.clickCount >= 2 && DoubleClick != null && entry != null) { ItemTooltip.Hide(); DoubleClick(entry); return; }
            if (LeftClick != null) { UiSounds.Click(); LeftClick(entry); }
        }

        public void OnBeginDrag(PointerEventData e)
        {
            if (DroppedOutside == null) return;   // only bag slots start drags
            var entry = Entry?.Invoke();
            if (entry == null) return;
            dragging = entry;
            ItemTooltip.Hide();
            var root = UiManager.Instance ? UiManager.Instance.Root : transform.root;
            var rt = NuiView.NewRect("Drag Item", root);
            rt.sizeDelta = new Vector2(46, 46);
            ghost = rt.gameObject.AddComponent<RawImage>();
            var tex = OriginalMenus.ItemIcon(entry.Icon, out var uv);
            if (!tex) tex = OriginalMenus.NotAvailableIcon(out uv);
            ghost.texture = tex;
            ghost.uvRect = uv;
            var sw = ItemCatalog.Swatch(entry);
            if (sw.HasValue) { ghost.texture = null; ghost.color = sw.Value; }
            ghost.raycastTarget = false;
            ghost.color = new Color(ghost.color.r, ghost.color.g, ghost.color.b, 0.8f);
            OnDrag(e);
        }

        public void OnDrag(PointerEventData e)
        {
            if (!ghost) return;
            var root = (RectTransform)ghost.transform.parent;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(root, e.position, e.pressEventCamera, out var local))
                ((RectTransform)ghost.transform).localPosition = local;
        }

        public void OnEndDrag(PointerEventData e)
        {
            var entry = dragging;
            dragging = null;
            if (ghost) Destroy(ghost.gameObject);
            ghost = null;
            if (entry == null) return;
            var hits = new List<RaycastResult>();
            if (EventSystem.current) EventSystem.current.RaycastAll(e, hits);
            foreach (var h in hits)
            {
                var target = h.gameObject.GetComponentInParent<ItemSlot>();
                if (target && target != this && target.Dropped != null)
                {
                    // licences only go where a slot asks for them (the licence page)
                    if (target.Accept != null ? !target.Accept(entry) : entry.Kind == BagKind.Licence) { UiPopup.Message(ItemCatalog.Text("10933", "You cannot equip this part.")); return; }
                    AudioLibrary.PlaySfx("sfx/SID_ITEM_DROP");
                    target.Dropped(entry);
                    return;
                }
                if (target == this) return;
            }
            bool inside = Window && RectTransformUtility.RectangleContainsScreenPoint(Window, e.position, e.pressEventCamera)
                          || hits.Any(h => h.gameObject.GetComponentInParent<NuiView>());
            if (!inside) DroppedOutside?.Invoke(entry);
        }

        /// <summary>A visual part's tooltip on the ITEMINFO box: name, description, stat bonus.</summary>
        static void PartTip(PartItem part, RectTransform near)
        {
            ItemTooltip.ShowVisual(part, near);
        }
    }

    /// <summary>The count box (TRADE 3240000): text, an edit box, - / + / MAX, Confirm and Cancel.</summary>
    public static class CountPopup
    {
        static NuiView view;
        static System.Action<int> onOk;
        static int max, value;

        public static bool IsOpen => view;
        public static NuiView View => view;

        public static void Show(string text, int maximum, int initial, System.Action<int> ok)
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            Close();
            view = ui.Open(3240000);
            if (!view) return;
            onOk = ok;
            max = Mathf.Max(1, maximum);
            value = Mathf.Clamp(initial, 1, max);
            view.SetText("ID_TRADE_COUNT_TEXT", text);
            view.SetVisible("ID_TRADE_COUNT_COUNT", false);
            var input = view.Control("ID_TRADE_COUNT_EDITBOX")?.Input;
            if (input)
            {
                input.contentType = InputField.ContentType.IntegerNumber;
                input.characterLimit = 10;
                input.onValueChanged.AddListener(s => { if (int.TryParse(s, out int n)) value = Mathf.Clamp(n, 1, max); });
            }
            Set(value);
            ui.On("ID_TRADE_COUNT_SUBTRACT", _ => Set(value - 1));
            ui.On("ID_TRADE_COUNT_ADD", _ => Set(value + 1));
            ui.On("ID_TRADE_COUNT_MAX", _ => Set(max));
            ui.On("ID_TRADE_COUNT_OK", _ => { var cb = onOk; int n = value; Close(); cb?.Invoke(n); });
            ui.On("ID_TRADE_COUNT_CANCEL", _ => Close());
            // modal like the popups
            var blocker = NuiView.NewRect("Modal Blocker", view.transform);
            blocker.anchorMin = Vector2.zero; blocker.anchorMax = Vector2.one;
            blocker.offsetMin = new Vector2(-4000, -4000); blocker.offsetMax = new Vector2(4000, 4000);
            blocker.SetAsFirstSibling();
            blocker.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, 0.25f);
            view.transform.SetAsLastSibling();
        }

        public static int Value => value;

        public static void Set(int n)
        {
            value = Mathf.Clamp(n, 1, max);
            var input = view ? view.Control("ID_TRADE_COUNT_EDITBOX")?.Input : null;
            if (input) input.SetTextWithoutNotify(value.ToString());
            else if (view) view.SetText("ID_TRADE_COUNT_COUNT", value.ToString());
        }

        public static void Close()
        {
            if (view && UiManager.Instance) UiManager.Instance.Close(view);
            view = null;
            onOk = null;
        }
    }

    /// <summary>The car in the Drift Item / Sticker page's view: its own layer far below the city, turned with the CARVIEW arrows.</summary>
    sealed class CarPreview
    {
        const int Layer = 30;
        static readonly Vector3 Base = new Vector3(0, -5000, 0);
        GameObject car, camGo;
        RenderTexture rt;
        RawImage image;
        float yaw = 210f, pitch = 12f;

        public static CarPreview Create(NuiControlView anchor, Vector2 size, string arrowPrefix)
        {
            if (!anchor) return null;
            var p = new CarPreview();
            var cell = OriginalMenus.Cell(anchor, "carview", new Vector2(0, 22), new Vector2(size.x, size.y - 30));
            // above the page's own panel images, under its buttons (arrows, slot column)
            int under = cell.parent.childCount - 1;
            foreach (var c in anchor.transform.parent.GetComponentsInChildren<NuiControlView>(true))
                if (c.transform.parent == cell.parent && c.Data.Type != NuiControlView.Static) under = Mathf.Min(under, c.transform.GetSiblingIndex());
            cell.SetSiblingIndex(under);
            p.image = cell.gameObject.AddComponent<RawImage>();
            p.image.raycastTarget = false;
            p.rt = new RenderTexture(512, 640, 24);
            p.image.texture = p.rt;
            p.camGo = new GameObject("Inventory Car Camera");
            var cam = p.camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.02f, 0.02f, 0.03f);
            cam.targetTexture = p.rt;
            cam.cullingMask = 1 << Layer;
            cam.fieldOfView = 40f;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 60f;
            p.camGo.AddComponent<GarageLighting>();
            var ui = UiManager.Instance;
            ui.On(arrowPrefix + "LEFT", _ => { p.yaw -= 30f; p.Aim(); });
            ui.On(arrowPrefix + "RIGHT", _ => { p.yaw += 30f; p.Aim(); });
            ui.On(arrowPrefix + "UP", _ => { p.pitch = Mathf.Min(60f, p.pitch + 12f); p.Aim(); });
            ui.On(arrowPrefix + "DOWN", _ => { p.pitch = Mathf.Max(0f, p.pitch - 12f); p.Aim(); });
            p.Aim();
            return p;
        }

        public void Show(OwnedCar owned)
        {
            if (car) Object.Destroy(car);
            if (owned == null) return;
            var info = GameTables.Car(owned.VehicleIndex);
            try
            {
                car = CarFactory.BuildVisual(info, owned, Base, Quaternion.identity, out _, out _);
                CarPaint.Apply(car, info, owned);
                foreach (var t in car.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = Layer;
            }
            catch (System.Exception ex) { Debug.LogWarning("inventory car view: " + ex.Message); }
        }

        void Aim()
        {
            if (!camGo) return;
            var dir = Quaternion.Euler(pitch, yaw, 0) * Vector3.back;
            camGo.transform.position = Base + Vector3.up * 0.8f + dir * 7.5f;
            camGo.transform.LookAt(Base + Vector3.up * 0.6f);
        }

        public void Release()
        {
            if (car) Object.Destroy(car);
            if (camGo) Object.Destroy(camGo);
            if (rt) { rt.Release(); Object.Destroy(rt); }
            car = camGo = null;
            rt = null;
        }
    }
}
