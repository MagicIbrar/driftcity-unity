using System.Collections.Generic;
using System.Linq;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// Custom shop on the original visual shop (VSHOP) dialogs: category tabs, the tab's part types, the product list,
    /// the car panel (buy, restore, camera) and the car's points. Clicking a product tries it on your parked car, seen from
    /// the client's camera preset for that part; Buy keeps it, Restore puts the old part back. Bought parts stay with that
    /// car and can be swapped any time.
    /// Extras (Shops/*, DriftCity_RE\SHOPS.md section 2): each row's Gift button (SENDGIFT 2840000) and mileage price and
    /// Mileage button (BUYMILEAGE 2630000), a right click on a row adds it to the wish list (WISHLIST 2540000, its icons open
    /// the checkout BUYWISHLIST 2620000), and the bottom bar's list button opens the trade history (BUYHISTORY 2290000)
    /// with the received gifts to accept. No G-Coin: Mito and mileage only.
    /// Period prices (Shops/ShopPeriods.cs, SHOPS.md 2b): Buy opens the checkout BUYWISHLIST 2620000 with the product's
    /// periods (7 / 30 days at the table's Mito prices, or unlimited); a period part shows its days left and can be
    /// bought again for more days; it leaves the car when its time is up.
    /// </summary>
    public static class CustomShop
    {
        static PlayerProfile P => PlayerProfile.Current;
        static UiManager Ui => UiManager.Instance;
        static bool active;
        public static bool IsActive => active;

        // top bar, category tabs, the category's part types, car panel, product list, Mito bar, car points
        static NuiView top, tabs, types, center, list, bottom, points;
        static int tab, type, variant, page;
        const int RowsPerPage = 7;

        /// <summary>A part-type tab of the VSHOP: the slots it lists (the list header's arrows switch between them).</summary>
        sealed class Category
        {
            public string Id;
            public PartSlot[] Slots;
            public bool Paint;
            public System.Func<PartItem, bool> Filter;
            public bool NoStock;
        }

        static Category C(string id, params PartSlot[] slots) => new Category { Id = id, Slots = slots };

        static readonly string[] TabIds = { "COLOR", "DECORATION", "TUNNING", "ETC" };
        static readonly int[] TypeDialogs = { 2380000, 2390000, 2410000, 2430000 };
        // the sub-tabs this rebuild has parts for; the others (high gloss, stickers, remodelling, the etc. shop) stay greyed out
        static readonly Category[][] Categories =
        {
            new[] { new Category { Id = "ID_VSHOP_TAB_COLOR_PAINT", Paint = true, Slots = new PartSlot[0] }, C("ID_VSHOP_TAB_COLOR_UNDERNEON", PartSlot.Neon), C("ID_VSHOP_TAB_COLOR_WINDOWTINTING", PartSlot.Tint) },
            new[] { C("ID_VSHOP_TAB_DECORATION_NUMBERPLATE", PartSlot.Plate), C("ID_VSHOP_TAB_DECORATION_DECAL", PartSlot.Decal), C("ID_VSHOP_TAB_DECORATION_ROOF", PartSlot.Roof),
                    C("ID_VSHOP_TAB_DECORATION_SOUND", PartSlot.Horn, PartSlot.Engine) },
            new[] { C("ID_VSHOP_TAB_TUNNING_AEROCOMBI", PartSlot.Aero, PartSlot.Hood), C("ID_VSHOP_TAB_TUNNING_BOOSTER", PartSlot.Booster),
                    new Category { Id = "ID_VSHOP_TAB_TUNNING_TIRE", Slots = new[] { PartSlot.Wheels }, Filter = p => !p.Item.StartsWith("GTire", System.StringComparison.OrdinalIgnoreCase) },
                    new Category { Id = "ID_VSHOP_TAB_TUNNING_GLOSSTIRE", Slots = new[] { PartSlot.Wheels }, Filter = p => p.Item.StartsWith("GTire", System.StringComparison.OrdinalIgnoreCase), NoStock = true },
                    C("ID_VSHOP_TAB_TUNNING_SPOILER", PartSlot.Wing), C("ID_VSHOP_TAB_TUNNING_AIRINTAKE", PartSlot.Duct) },
            new Category[0],
        };

        public static void Open()
        {
            var flow = GameFlow.Instance;
            if (!flow || !flow.InCity || P?.Car == null) return;
            if (RaceMission.Active || OnlineRace.Active || ChaseMission.Active) { UiPopup.Message("Finish what you're doing before visiting the custom shop."); return; }
            top = OriginalMenus.OpenScreen(2260000, Back);
            if (!top) return;
            tabs = OriginalMenus.Open(2340000);
            center = OriginalMenus.Open(2300000);
            list = OriginalMenus.Open(2470000);
            bottom = OriginalMenus.Open(2460000);
            points = OriginalMenus.Open(2610000);
            types = null;
            LeftAlign(center);
            LeftAlign(bottom);
            OriginalMenus.OnClose(End);
            active = true;
            AuctionMarket.Hook();   // gift / sale notices from the server
            Park();

            Ui.On("ID_VSHOP_QUIT", _ => OriginalMenus.Close());
            Ui.On("ID_VSHOP_HOME", _ => ShowTab(2));
            for (int i = 0; i < TabIds.Length; i++) { int t = i; Ui.On("ID_VSHOP_TOP_SMALL_" + TabIds[i], _ => ShowTab(t)); }
            foreach (var k in TabIds) tabs.SetVisible("ID_VSHOP_TOP_SMALL_TABKEY_" + k, false);   // invisible hotkey buttons over the tabs

            // one car: the car you drive (the selector arrows would switch cars in the original)
            OriginalMenus.Hide(center, "ID_VSHOP_CENTER_CARSEL_LEFT", "ID_VSHOP_CENTER_CARSEL_RIGHT");
            Ui.On("ID_VSHOP_CENTER_BUY", _ => { var p = Tried(); if (p != null) Buy(p); });
            Ui.On("ID_VSHOP_CENTER_RESTORE", _ => { ClearPreview(); Refresh(); });
            Ui.On("ID_VSHOP_CENTER_CAM_L", _ => Turn(-0.35f, 0, 0));
            Ui.On("ID_VSHOP_CENTER_CAM_R", _ => Turn(0.35f, 0, 0));
            Ui.On("ID_VSHOP_CENTER_CAM_T", _ => Turn(0, -0.15f, 0));
            Ui.On("ID_VSHOP_CENTER_CAM_B", _ => Turn(0, 0.15f, 0));
            Ui.On("ID_VSHOP_CENTER_MAGNIFY_PLUS", _ => Turn(0, 0, -0.2f));
            Ui.On("ID_VSHOP_CENTER_MAGNIFY_MINUS", _ => Turn(0, 0, 0.2f));

            // no paid currency: the G-Coin balance and recharge stay hidden; the list button opens the trade history
            OriginalMenus.Hide(bottom, "ID_VSHOP_BOTTOM_HANCOIN", "ID_VSHOP_BOTTOM_CHRAGE_HANCOIN");
            Ui.On("ID_VSHOP_BOTTOM_BUYLIST", _ => ShopHistory.Open());

            // Mito and mileage prices: no G-Coin icon, no event / new / rebate badges
            for (int i = 0; i < RowsPerPage; i++)
            {
                int row = i;
                OriginalMenus.Hide(list, $"ID_VSHOP_PRODUCTLIST_ICON_LIST{i}", $"ID_VSHOP_PRODUCTLIST_EVENT1_{i + 1}", $"ID_VSHOP_PRODUCTLIST_EVENT2_{i + 1}");
                Ui.On($"ID_VSHOP_PRODUCTLIST_BTN_PRESENT_LIST{i}", _ => { var e = EntryAt(row); var pr = Product(e); if (pr != null) Gifts.Show(pr); });
                Ui.On($"ID_VSHOP_PRODUCTLIST_BTN_MILEAGE_LIST{i}", _ => { var e = EntryAt(row); var pr = Product(e); if (pr != null) Checkout.Show(new List<ShopProduct> { pr }, true, () => { Rebuild(); Refresh(); }); });
                var focus = list.Control($"ID_VSHOP_PRODUCTLIST_FOCUS_LIST{i}");
                if (focus) focus.gameObject.AddComponent<RowRightClick>().Action = () => VShopWish.Toggle(Product(EntryAt(row)));
                string n = Ordinal(i + 1);
                OriginalMenus.Hide(list, $"ID_VSHOP_PRODUCTLIST_NEW1_{n}", $"ID_VSHOP_PRODUCTLIST_NEW2_{n}", $"ID_VSHOP_PRODUCTLIST_REBATE1_{n}", $"ID_VSHOP_PRODUCTLIST_REBATE2_{n}");
                Ui.On($"ID_VSHOP_PRODUCTLIST_FOCUS_LIST{i}", _ => Pick(row, false));
                // the row is a checkbox drawn by its second element; the first is a placeholder cut
                var tick = list.Control($"ID_VSHOP_PRODUCTLIST_CB_LIST{i}")?.transform.Find("e1");
                if (tick) tick.gameObject.SetActive(false);
                Ui.On($"ID_VSHOP_PRODUCTLIST_BTN_BUY_LIST{i}", _ => Pick(row, true));
            }
            Ui.On("ID_VSHOP_PRODUCTLIST_BTN_LEFT", _ => SwitchVariant(-1));
            Ui.On("ID_VSHOP_PRODUCTLIST_BTN_RIGHT", _ => SwitchVariant(1));
            Ui.On("ID_VSHOP_PRODUCTLIST_BTN_PAGE_PREV", _ => { page--; Refresh(); });
            Ui.On("ID_VSHOP_PRODUCTLIST_BTN_PAGE_NEXT", _ => { page++; Refresh(); });
            Ui.On("ID_VSHOP_PRODUCTLIST_BTN_PAGE_FIRST", _ => { page = 0; Refresh(); });
            Ui.On("ID_VSHOP_PRODUCTLIST_BTN_PAGE_LAST", _ => { page = int.MaxValue; Refresh(); });
            VShopWish.Open();
            VShopWish.Changed -= Refresh;
            VShopWish.Changed += Refresh;
            ShowTab(Categories[2].Any(Has) ? 2 : 0);   // paints are always there
        }

        sealed class RowRightClick : MonoBehaviour, UnityEngine.EventSystems.IPointerClickHandler
        {
            public System.Action Action;
            public void OnPointerClick(UnityEngine.EventSystems.PointerEventData e) { if (e.button == UnityEngine.EventSystems.PointerEventData.InputButton.Right) Action?.Invoke(); }
        }

        /// <summary>The product a row shows (null for the stock / "no part" rows).</summary>
        static ShopProduct Product(Entry e) => e == null ? null : e.Paint != null ? new ShopProduct { Paint = e.Paint } : e.Part != null && !e.Part.Stock ? new ShopProduct { Part = e.Part } : null;

        static Entry EntryAt(int row) { var entries = Entries(); int i = page * RowsPerPage + row; return i < entries.Count ? entries[i] : null; }

        /// <summary>Some VSHOP panels carry no offset from their top-centre anchor (the client places them); they share the
        /// other panels' layout from the screen's left edge, as the colour tabs' G=-512 0 shows.</summary>
        static void LeftAlign(NuiView v)
        {
            if (!v || v.Dialog.Anchor != 10 || v.Dialog.Attr.ContainsKey("G")) return;
            ((RectTransform)v.transform).anchoredPosition = new Vector2(-512, 0);
        }

        static string Ordinal(int n) => n == 1 ? "1ST" : n == 2 ? "2ND" : n == 3 ? "3RD" : n + "TH";

        static VehicleInfo Car => GameTables.Car(P.Car.VehicleIndex);

        static Dictionary<string, Dictionary<string, string>> views;

        /// <summary>Points the shop camera with the client's preset for this part (init\vshop_camera.ini).</summary>
        static void View(string preset)
        {
            if (views == null)
            {
                views = new Dictionary<string, Dictionary<string, string>>();
                foreach (var s in OriginalLook.Ini(@"init\vshop_camera.ini").Values)
                    if (s.TryGetValue("name", out var n)) views[n] = s;
            }
            if (!views.TryGetValue(preset, out var v) && !views.TryGetValue("default", out v)) return;
            float F(string k) => v.TryGetValue(k, out var x) ? OriginalLook.Num(x) : 0f;
            ChaseCamera.ShowroomView = (F("yaw"), F("pitch"), F("zoom"));
        }

        /// <summary>The car panel's camera buttons nudge the preset (yaw / pitch in radians, zoom within the camera's range).</summary>
        static void Turn(float yaw, float pitch, float zoom)
        {
            var (y, p, z) = ChaseCamera.ShowroomView;
            ChaseCamera.ShowroomView = (y + yaw, Mathf.Clamp(p + pitch, -1.2f, 0.1f), Mathf.Clamp(z + zoom, -1.6f, 0.4f));
        }

        static string PresetFor(PartSlot slot) => slot switch
        {
            PartSlot.Wing => "rearwing", PartSlot.Wheels => "wheel", PartSlot.Plate => "numberplate", PartSlot.Duct => "intercooler",
            PartSlot.Tint => "windowtint", PartSlot.Roof => "roof", PartSlot.Aero => "bumper", PartSlot.Hood => "bumper", PartSlot.Decal => "painting",
            _ => "default",
        };

        static string StockName(PartSlot slot) => slot switch
        {
            PartSlot.Wheels => "Stock wheels", PartSlot.Booster => "Standard flame", PartSlot.Neon => "No neon",
            PartSlot.Plate => "Standard plate", PartSlot.Duct => "No air duct", PartSlot.Roof => "No roof item", PartSlot.Tint => "Clear glass",
            PartSlot.Decal => "No decal", PartSlot.Horn => "Stock horn", PartSlot.Engine => "Stock engine", _ => "Stock",
        };

        /// <summary>The part id really fitted in a slot (-1 = stock), ignoring any try-on.</summary>
        static int FittedId(PartSlot slot)
        {
            int id = P.Car.Fitted(slot);
            return id >= 0 && P.Car.Parts.Contains(id) && PartsCatalog.Fits(PartsCatalog.Get(id), Car) ? id : -1;
        }

        static bool Trying(int id, PartSlot slot) => PartsCatalog.PreviewCar == P.Car && PartsCatalog.PreviewSlot == slot && PartsCatalog.PreviewItem == id;

        /// <summary>The part being tried on (null when none).</summary>
        static PartItem Tried() => PartsCatalog.PreviewCar == P.Car && PartsCatalog.PreviewItem >= 0 ? PartsCatalog.Get(PartsCatalog.PreviewItem) : null;

        // ---------------- tabs ----------------
        static IEnumerable<PartItem> PartsOf(Category c, PartSlot slot) => PartsCatalog.For(Car, slot).Where(p => c.Filter == null || c.Filter(p));

        /// <summary>
        /// The top tab's Top Menu Enable of initshopcategory.xlt (0x7dea10 -> 0x6600f0, VShopCategories). The US table turns
        /// TOP_COLOR off: the remaster's ADV shop shows paint / neon / tint under Decoration (2400000). The remake keeps the
        /// classic tab dialogs, whose Decoration page has no neon or tint, so COLOR stays on [remake]; the other tops follow
        /// the table. Without the table every tab is on.
        /// </summary>
        public static bool TopAllowed(int t)
        {
            if (t == 0 || VShopCategories.All.Count == 0) return true;
            return t < VShopCategories.ClassicTops.Length && VShopCategories.TopMenuEnabled(VShopCategories.ClassicTops[t]);
        }

        /// <summary>A sub tab whose main category the table lists (all the classic tabs are listed in the US data).</summary>
        static bool TabListed(string id) => VShopCategories.All.Count == 0 || VShopCategories.HasTab(id);

        static bool Has(Category c) => c.Paint || c.Slots.Any(s => PartsOf(c, s).Any());

        static void ShowTab(int t)
        {
            if (!top) return;
            int first = System.Array.FindIndex(Categories[t], Has);
            if (first < 0 || !TopAllowed(t)) return;   // nothing for this car in that category, or the table turns the tab off
            tab = t;
            for (int i = 0; i < TabIds.Length; i++)
            {
                var k = TabIds[i];
                tabs.SetVisible($"ID_VSHOP_TOP_SMALL_{k}_TILE1ST", i == tab);
                tabs.SetVisible($"ID_VSHOP_TOP_SMALL_{k}_TILE2ND", i == tab);
                var c = tabs.Control("ID_VSHOP_TOP_SMALL_" + k);
                OriginalMenus.Pressed(c, i == tab);
                OriginalMenus.Dim(c, !Categories[i].Any(Has) || !TopAllowed(i), 0.45f);
            }
            OriginalMenus.CloseView(types);
            types = OriginalMenus.Open(TypeDialogs[tab]);
            LeftAlign(types);
            if (types)
            {
                foreach (var c in types.Controls.ToList())
                {
                    var id = c.Data.IdName;
                    if (id.StartsWith("ID_VSHOP_TABKEY_")) { c.gameObject.SetActive(false); continue; }   // hotkey overlays
                    if (!id.StartsWith("ID_VSHOP_TAB_")) continue;
                    int k = System.Array.FindIndex(Categories[tab], x => x.Id == id);
                    OriginalMenus.Dim(c, k < 0 || !Has(Categories[tab][k]) || !TabListed(id), 0.45f);
                    if (k >= 0) Ui.On(id, _ => ShowType(k));
                }
            }
            ShowType(first);
        }

        static void ShowType(int k)
        {
            ClearPreview();
            type = k;
            variant = 0;
            var c = Categories[tab][type];
            // start on the first slot that has parts (a "c" car's hood before an empty bumper list, say)
            while (variant < c.Slots.Length - 1 && !PartsOf(c, c.Slots[variant]).Any()) variant++;
            page = 0;
            if (types) foreach (var cat in Categories[tab]) OriginalMenus.Pressed(types.Control(cat.Id), cat == c);
            View(c.Paint ? "painting" : PresetFor(c.Slots[variant]));
            Refresh();
        }

        static void SwitchVariant(int delta)
        {
            var c = Categories[tab][type];
            if (c.Slots.Length < 2) return;
            ClearPreview();
            // skip slots this car has nothing for (a hood list on an aero-kit car, say)
            for (int n = 0; n < c.Slots.Length; n++)
            {
                variant = (variant + delta + c.Slots.Length) % c.Slots.Length;
                if (PartsOf(c, c.Slots[variant]).Any()) break;
            }
            page = 0;
            View(PresetFor(c.Slots[variant]));
            Refresh();
        }

        // ---------------- products ----------------
        sealed class Entry
        {
            public PartItem Part;     // null = the slot's stock part / nothing fitted
            public PaintItem Paint;
            public PartSlot Slot;
        }

        static List<Entry> Entries()
        {
            var c = Categories[tab][type];
            var entries = new List<Entry>();
            if (c.Paint)
            {
                foreach (var paint in ShopData.Paints.Take(80)) entries.Add(new Entry { Paint = paint });
                return entries;
            }
            var slot = c.Slots[variant];
            var parts = PartsOf(c, slot).OrderByDescending(p => p.Stock).ThenByDescending(p => P.Car.Parts.Contains(p.Id)).ThenBy(p => p.Price).Take(120).ToList();
            if (!c.NoStock && !PartsCatalog.For(Car, slot).Any(p => p.Stock)) entries.Add(new Entry { Slot = slot });
            foreach (var p in parts) entries.Add(new Entry { Part = p, Slot = slot });
            return entries;
        }

        static bool Owns(Entry e) => e.Paint != null ? P.Car.PaintItem == e.Paint.UniqueId : e.Part == null || e.Part.Stock || P.Car.Parts.Contains(e.Part.Id);

        static bool Fitted(Entry e)
        {
            if (e.Paint != null) return P.Car.PaintItem == e.Paint.UniqueId;
            int fitted = FittedId(e.Slot);
            return e.Part == null || e.Part.Stock ? fitted == -1 : fitted == e.Part.Id;
        }

        static void Refresh()
        {
            if (!list || P?.Car == null) return;
            var car = Car;
            var c = Categories[tab][type];
            var entries = Entries();
            int pages = Mathf.Max(1, (entries.Count + RowsPerPage - 1) / RowsPerPage);
            page = Mathf.Clamp(page, 0, pages - 1);
            // the list header: the table's sub category of the listed parts (0x7de9e0: "Bumper", "Aero set", "Flame" ...)
            list.SetText("ID_VSHOP_PRODUCTLIST_CATEGORY", c.Paint ? "Paint"
                : VShopCategories.ListName(entries.Where(e => e.Part != null && !e.Part.Stock).Select(e => e.Part.Id)) ?? PartsCatalog.SlotName(c.Slots[variant], car));
            bool several = c.Slots.Count(sl => PartsOf(c, sl).Any()) > 1;
            list.SetEnabled("ID_VSHOP_PRODUCTLIST_BTN_LEFT", several);
            list.SetEnabled("ID_VSHOP_PRODUCTLIST_BTN_RIGHT", several);
            list.SetText("ID_VSHOP_PRODUCTLIST_PAGE_NUM1ST", (page + 1).ToString());
            list.SetText("ID_VSHOP_PRODUCTLIST_PAGE_NUM2ND", pages.ToString());
            list.SetEnabled("ID_VSHOP_PRODUCTLIST_BTN_PAGE_PREV", page > 0);
            list.SetEnabled("ID_VSHOP_PRODUCTLIST_BTN_PAGE_FIRST", page > 0);
            list.SetEnabled("ID_VSHOP_PRODUCTLIST_BTN_PAGE_NEXT", page < pages - 1);
            list.SetEnabled("ID_VSHOP_PRODUCTLIST_BTN_PAGE_LAST", page < pages - 1);
            var tried = Tried();
            for (int i = 0; i < RowsPerPage; i++)
            {
                int index = page * RowsPerPage + i;
                var e = index < entries.Count ? entries[index] : null;
                ShowRow(i, e, tried);
            }

            // car panel: the car you drive and the part on trial
            OriginalMenus.Plate(center, "ID_VSHOP_CENTER_CARNAME_XL", OriginalMenus.CarName(P.Car), GameTables.Car(P.Car.VehicleIndex));
            OriginalMenus.Emblem(center, "ID_VSHOP_CENTER_CARGRADE_L", ShopData.Grade(car, P.Car.Grade));
            center.SetEnabled("ID_VSHOP_CENTER_BUY", tried != null && (!P.Car.Parts.Contains(tried.Id) || ShopPeriods.IsTimed(P.Car, tried.Id)));
            center.SetEnabled("ID_VSHOP_CENTER_RESTORE", tried != null);

            bottom.SetText("ID_VSHOP_BOTTOM_MITO", $"{P.Mito:N0}");
            // points with the part on trial counted in
            var sums = OriginalMenus.FillStats(points, "ID_VSHOP_CARPOINT_", P.Car);
            points.SetText("ID_VSHOP_CARPOINT_CAR_TOTAL", sums.car.ToString());
            points.SetText("ID_VSHOP_CARPOINT_PARTS_TOTAL", sums.parts.ToString());
            points.SetText("ID_VSHOP_CARPOINT_USER_TOTAL", sums.user.ToString());
        }

        static void ShowRow(int i, Entry e, PartItem tried)
        {
            string[] ids = { "CB_LIST", "FOCUS_LIST", "SAMPLE_LIST", "NAME_LIST", "ICONMITO_LIST", "PRICE_LIST", "BTN_BUY_LIST",
                             "ICONMILEAGE_LIST", "MILEAGEPRICE_LIST", "BTN_PRESENT_LIST", "BTN_MILEAGE_LIST" };
            foreach (var id in ids) list.SetVisible($"ID_VSHOP_PRODUCTLIST_{id}{i}", e != null);
            var sample = list.Control($"ID_VSHOP_PRODUCTLIST_SAMPLE_LIST{i}");
            if (sample) OriginalMenus.Remove(sample.transform, "item");
            if (e == null) return;

            bool owns = Owns(e), fitted = Fitted(e);
            bool trying = e.Part != null && Trying(e.Part.Id, e.Slot);
            string name = e.Paint?.Name ?? e.Part?.Name ?? StockName(e.Slot);
            // the first period's price (ShopPeriods: 7 / 30 days, unlimited last); a period part shows its days left
            var first = Product(e)?.Period ?? new PeriodPrice();
            long price = first.Price;
            bool timed = e.Part != null && ShopPeriods.IsTimed(P.Car, e.Part.Id);
            int daysLeft = timed ? ShopPeriods.DaysLeft(P.Car, e.Part.Id, ItemUse.Now) : 0;
            // the sample shows the client's item icon; paints, neon and tints show their colour
            Color? swatch = e.Paint != null ? e.Paint.Color : e.Part != null && (e.Slot == PartSlot.Neon || e.Slot == PartSlot.Tint) ? e.Part.Tint : (Color?)null;
            if (sample)
            {
                sample.SetText("");
                // the sample lies over the row's click area, so it takes the row's click
                var tile = OriginalMenus.ItemTile(sample.transform, Vector2.zero, new Vector2(64, 64), e.Part?.Item, e.Part == null && e.Paint == null ? "Stock" : name, swatch);
                OriginalMenus.Clickable(tile, () => Pick(i, false));
            }
            var bonus = e.Part?.BonusText;
            list.SetText($"ID_VSHOP_PRODUCTLIST_NAME_LIST{i}", string.IsNullOrEmpty(bonus) ? name : $"{name}\n{bonus}");
            list.SetVisible($"ID_VSHOP_PRODUCTLIST_ICONMITO_LIST{i}", !owns && price > 0);
            string left = timed ? $" ({ShopPeriods.Button(new PeriodPrice { Days = daysLeft })})" : "";
            list.SetText($"ID_VSHOP_PRODUCTLIST_PRICE_LIST{i}", fitted && !trying ? "Fitted" + left : trying ? "Trying on" : owns ? (e.Part != null && e.Part.Stock ? "Stock" : e.Part == null && e.Paint == null ? "Free" : "Owned" + left)
                : $"{price:N0}");   // the first period's price (the checkout lists them all)
            list.SetVisible($"ID_VSHOP_PRODUCTLIST_BTN_BUY_LIST{i}", !owns || timed);
            list.SetEnabled($"ID_VSHOP_PRODUCTLIST_BTN_BUY_LIST{i}", P.Mito >= price);
            // mileage price and buttons (no paid currency: mileage = a tenth of the Mito price), gifts online
            var product = Product(e);
            bool sold = product != null && (!owns || timed) && price > 0;
            list.SetVisible($"ID_VSHOP_PRODUCTLIST_ICONMILEAGE_LIST{i}", sold);
            list.SetVisible($"ID_VSHOP_PRODUCTLIST_MILEAGEPRICE_LIST{i}", sold);
            if (sold) list.SetText($"ID_VSHOP_PRODUCTLIST_MILEAGEPRICE_LIST{i}", $"{product.MileagePrice:N0}");
            list.SetVisible($"ID_VSHOP_PRODUCTLIST_BTN_MILEAGE_LIST{i}", sold);
            list.SetEnabled($"ID_VSHOP_PRODUCTLIST_BTN_MILEAGE_LIST{i}", sold && P.Mileage >= product.MileagePrice);
            list.SetVisible($"ID_VSHOP_PRODUCTLIST_BTN_PRESENT_LIST{i}", product != null && price > 0);
            list.SetEnabled($"ID_VSHOP_PRODUCTLIST_BTN_PRESENT_LIST{i}", Net.OnlineSession.Active);
            if (product != null && VShopWish.Contains(product)) list.SetText($"ID_VSHOP_PRODUCTLIST_NAME_LIST{i}", (string.IsNullOrEmpty(bonus) ? name : name + "\n" + bonus) + "  [Wish]");
            // the row's selected look: the part on trial, else the one fitted
            bool slotTrying = tried != null && tried.Slot == e.Slot;
            OriginalMenus.StateOverlay(list.Control($"ID_VSHOP_PRODUCTLIST_CB_LIST{i}"), 2, 6, trying || fitted && !slotTrying);
        }

        /// <summary>A product row clicked: try it on (or fit it when owned); its Buy button buys it.</summary>
        static void Pick(int row, bool buy)
        {
            var entries = Entries();
            int index = page * RowsPerPage + row;
            if (index >= entries.Count) return;
            var e = entries[index];
            if (e.Paint != null)
            {
                if (!Owns(e)) BuyPaint(e.Paint);
                return;
            }
            var p = e.Part;
            if (buy && p != null && ShopPeriods.IsTimed(P.Car, p.Id)) { Buy(p); return; }   // more days on a period part
            if (p == null || p.Stock || P.Car.Parts.Contains(p.Id)) { if (!buy && !Fitted(e)) Fit(e.Slot, p == null || p.Stock ? -1 : p.Id); return; }
            if (buy) Buy(p);
            else if (Trying(p.Id, e.Slot)) { ClearPreview(); Refresh(); }
            else TryOn(p);
        }

        static void TryOn(PartItem p)
        {
            PartsCatalog.PreviewCar = P.Car; PartsCatalog.PreviewSlot = p.Slot; PartsCatalog.PreviewItem = p.Id;
            Rebuild();
            if (p.Slot == PartSlot.Horn) GameFlow.Instance?.Horn();
            Refresh();
        }

        /// <summary>Buy: the checkout BUYWISHLIST 2620000 with this one product and its period buttons (ShopPeriods).</summary>
        static void Buy(PartItem p)
        {
            var product = new ShopProduct { Part = p };
            long cheapest = ShopPeriods.For(product).Min(x => x.Price);
            if (P.Mito < cheapest) { UiPopup.Message($"You need {cheapest - P.Mito:N0} more Mito."); return; }
            ClearPreview();
            Checkout.Show(new List<ShopProduct> { product }, false, () => { Rebuild(); Refresh(); });
        }

        /// <summary>Paints have no try-on: the paint shop's purchase, asked first.</summary>
        static void BuyPaint(PaintItem paint)
        {
            if (P.Mito < paint.Price) { UiPopup.Message($"You need {paint.Price - P.Mito:N0} more Mito."); return; }
            UiPopup.Show($"Paint your car {paint.Name} for {paint.Price:N0} Mito?", id =>
            {
                if (id != UiPopup.Yes) return;
                Progress.Paint(P.CurrentCar, paint.UniqueId, paint.Price, r =>
                {
                    if (!r.Ok) return;
                    Mileage.Earn(paint.Price);
                    ShopLog.Add("buy", paint.Name, paint.Price, "mito", "");
                    GameFlow.Instance?.RepaintCar();
                    Refresh();
                });
            }, UiPopup.Yes, UiPopup.No);
        }

        static void Fit(PartSlot slot, int item)
        {
            ClearPreview();
            Progress.FitPart(P.CurrentCar, slot, item, r =>
            {
                Rebuild();
                Refresh();
                if (slot == PartSlot.Aero && item >= 0) AeroSet.Offer(P.Car);   // AEROSET bonus points after an aero kit
            });
        }

        static void ClearPreview()
        {
            bool had = PartsCatalog.PreviewCar != null;
            PartsCatalog.PreviewCar = null; PartsCatalog.PreviewItem = -1;
            if (had) Rebuild();
        }

        static void Rebuild()
        {
            var flow = GameFlow.Instance;
            if (!flow) return;
            flow.RespawnCar();
            flow.RepaintCar();
            flow.ApplyCarStats();
            if (active) Park();
        }

        /// <summary>Holds the car still and swings the camera round it.</summary>
        static void Park()
        {
            var car = GameFlow.Instance ? GameFlow.Instance.PlayerCar : null;
            if (car)
            {
                car.ExternalControl = true;
                var rb = car.GetComponent<Rigidbody>();
                if (rb) { rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
            }
            var cam = Camera.main ? Camera.main.GetComponent<ChaseCamera>() : null;
            if (cam) cam.Showroom = true;
        }

        /// <summary>Esc takes a part off trial first, then leaves the shop.</summary>
        static bool Back()
        {
            if (Tried() == null) return false;
            ClearPreview();
            Refresh();
            return true;
        }

        /// <summary>Leaves the shop at once (a race starting, travel, logout).</summary>
        public static void Abort()
        {
            if (!active) return;
            if (top) OriginalMenus.Close();
            End();
        }

        static void End()
        {
            if (!active) return;
            active = false;
            VShopWish.Changed -= Refresh;
            Gifts.Close(); Checkout.Close(); ShopHistory.Close();
            top = tabs = types = center = list = bottom = points = null;
            ClearPreview();
            var car = GameFlow.Instance ? GameFlow.Instance.PlayerCar : null;
            if (car) car.ExternalControl = false;
            var cam = Camera.main ? Camera.main.GetComponent<ChaseCamera>() : null;
            if (cam) cam.Showroom = false;
            GameFlow.Instance?.ApplyCarStats();
        }
    }
}
