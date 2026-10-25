using System;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Core;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.Game
{
    /// <summary>
    /// The advanced car dealer "CAR SHOW ROOM" (DriftCity_RE\SHOPS.md section 1). The autoshop stage (0x7438a0) opens it
    /// instead of the classic AUTOSHOP 3260000 when globalcontent CarShop = 1, which this client's data sets: AUTOSHOPADVBG
    /// 2640000 (backdrop, maker emblem and name, the car's grade badge), AUTOSHOPADVMENU 2660000 (tabs Purchase / Sell /
    /// Receive Reward, filters New / Popular / Duffy's Picks / All / V1-V9, a strip of 9 cars with Equipped / Inventory /
    /// Locked badges and arrows, the coupon notice), AUTOSHOPADVDETAIL 2650000 (stats against the car you drive, level,
    /// type, size, colours, Compare 3280000 / Modify, Related Items), AUTOSHOPADVMITO 2670000 (price, Mito, mileage, Buy or
    /// Sell, trade history), AUTOSHOPADVLIST 2680000 (related items: custom parts for the car, buy / gift / mileage) and the
    /// wish list AUTOSHOPADV 2550000. Init 0x5b0500, show 0x5afa60, menu handler 0x5afe50, list 0x5af660, price panel
    /// 0x5addd0. The car stands in the dealer's own showroom (autoshop\autoshop.nif) behind the panels.
    /// G-Coin (the CC price and balance, recharge) is hidden: this rebuild has no paid currency.
    /// </summary>
    public static class AdvancedDealer
    {
        static PlayerProfile P => PlayerProfile.Current;
        static UiManager Ui => UiManager.Instance;

        public enum Tab { Buy, Sell, Reward }
        // filter codes of the menu handler (0x5b0109..): 0 New, 1 Popular, 2 Duffy's Picks, 3 All, 4.. V1..V9
        public const int FilterNew = 0, FilterPopular = 1, FilterDuffy = 2, FilterAll = 3;
        const int Strip = 9;

        static NuiView bg, menu, detail, mito, wish, related;
        static Tab tab;
        static int filter = FilterAll, selected, paint, wishScroll, relatedPage;
        static readonly List<Entry> entries = new List<Entry>();
        static Showroom scene;

        public static bool IsOpen => bg;
        public static Tab CurrentTab => tab;
        public static int Filter => filter;
        public static IReadOnlyList<Entry> Entries => entries;
        public static Entry Selection => selected >= 0 && selected < entries.Count ? entries[selected] : null;
        public static NuiView Menu => menu;
        public static NuiView Detail => detail;
        public static NuiView MitoPanel => mito;
        public static NuiView Related => related;
        public static NuiView WishView => wish;

        /// <summary>A car of the strip: a dealer car (buy / reward) or one of the driver's (sell).</summary>
        public sealed class Entry
        {
            public VehicleInfo Info;
            public OwnedCar Owned;   // sell tab
            public int Slot = -1;    // sell tab: index in P.Cars
        }

        // ================= per-car tables =================
        sealed class CarRow { public int Display, MakeType, Sellable; public float Length; public bool New, Popular, Duffy; }
        static Dictionary<int, CarRow> rows;

        /// <summary>vehiclelist Display (dealer order; 0 = not shown), MakeType, Sellable, length; vehicledisplay New / Popular / Duffy's.</summary>
        static CarRow Row(VehicleInfo v)
        {
            if (rows == null)
            {
                rows = new Dictionary<int, CarRow>();
                try
                {
                    var t = XltTable.Parse(DriftCityData.Files.Read(@"init\vehiclelist.xlt"));
                    foreach (var r in t.Rows)
                    {
                        if (!int.TryParse(t.Get(r, "Index"), out int idx) || rows.ContainsKey(idx)) continue;
                        int.TryParse(t.Get(r, "Display"), out int disp);
                        int.TryParse(t.Get(r, "MakeType"), out int make);
                        int.TryParse(t.Get(r, "Sellable"), out int sell);
                        float.TryParse(t.Get(r, "length"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float len);
                        rows[idx] = new CarRow { Display = disp, MakeType = make, Sellable = sell, Length = len };
                    }
                    var d = XltTable.Parse(DriftCityData.Files.Read(@"init\vehicledisplay.xlt"));
                    var byUid = GameTables.PlayerCars.GroupBy(c => c.UniqueId).ToDictionary(g => g.Key, g => g.ToList());
                    foreach (var r in d.Rows)
                    {
                        if (!int.TryParse(d.Get(r, "Unique Id"), out int uid) || !byUid.TryGetValue(uid, out var cars)) continue;
                        int.TryParse(d.Get(r, "New"), out int n); int.TryParse(d.Get(r, "Popular"), out int pop); int.TryParse(d.Get(r, "Duffy's"), out int duffy);
                        foreach (var c in cars) if (rows.TryGetValue(c.Index, out var row)) { row.New = n > 0; row.Popular = pop > 0; row.Duffy = duffy > 0; }
                    }
                }
                catch (Exception ex) { Debug.LogWarning("dealer tables: " + ex.Message); }
            }
            return v != null && rows.TryGetValue(v.Index, out var cr) ? cr : new CarRow { Display = 1, Sellable = 1, Length = v?.LengthMm ?? 4300 };
        }

        /// <summary>DETAIL TYPE_TXT: 11803 Basic, 11804 Coupon, 11805 Special, 11927 Scratch card vehicle (by MakeType).</summary>
        static string TypeText(VehicleInfo v)
        {
            if (CouponBook.CouponFor(v) != null) return MissionText.Get("11804");
            int t = Row(v).MakeType;
            return MissionText.Get(t == 1 ? "11804" : t == 2 ? "11805" : t >= 3 ? "11927" : "11803");
        }

        // ================= open / close =================
        public static void Open(bool sell = false)
        {
            if (P == null || P.Cars.Count == 0) return;
            ListWindow.CloseOpen();
            bg = OriginalMenus.OpenScreen(2640000, Back);
            if (!bg) return;
            OriginalMenus.OnClose(Cleanup);
            menu = OriginalMenus.Open(2660000);
            detail = OriginalMenus.Open(2650000);
            mito = OriginalMenus.Open(2670000);
            wish = OriginalMenus.Open(2550000);
            related = null;
            scene = new Showroom(bg);
            WireMenu();
            WireDetail();
            WireMito();
            WireWish();
            filter = FilterAll; selected = 0; paint = 0; wishScroll = 0;
            ShowTab(sell ? Tab.Sell : Tab.Buy);
        }

        static bool Back()
        {
            if (related) { CloseRelated(); return true; }
            return false;
        }

        public static void Close() { if (bg) OriginalMenus.Close(); }

        static void Cleanup()
        {
            scene?.Release();
            scene = null;
            bg = menu = detail = mito = wish = related = null;
            entries.Clear();
        }

        static void WireMenu()
        {
            foreach (var k in new[] { "BUY", "SELL", "REWARD" }) menu.SetVisible("ID_AUTOSHOPADVMENU_TABKEY_" + k, false);
            Ui.On("ID_AUTOSHOPADVMENU_BUY", _ => ShowTab(Tab.Buy));
            Ui.On("ID_AUTOSHOPADVMENU_SELL", _ => ShowTab(Tab.Sell));
            Ui.On("ID_AUTOSHOPADVMENU_REWARD", _ => ShowTab(Tab.Reward));
            Ui.On("ID_AUTOSHOPADVMENU_CLOSE", _ => Close());
            Ui.On("ID_AUTOSHOPADVMENU_NEW", _ => SetFilter(FilterNew));
            Ui.On("ID_AUTOSHOPADVMENU_STAR", _ => SetFilter(FilterPopular));
            Ui.On("ID_AUTOSHOPADVMENU_GM", _ => SetFilter(FilterDuffy));
            Ui.On("ID_AUTOSHOPADVMENU_ALL", _ => SetFilter(FilterAll));
            for (int g = 1; g <= 9; g++) { int f = FilterAll + g; Ui.On("ID_AUTOSHOPADVMENU_V" + g, _ => SetFilter(f)); }
            Ui.On("ID_AUTOSHOPADVMENU_LEFTBTN", _ => Step(-1));
            Ui.On("ID_AUTOSHOPADVMENU_RIGHTBTN", _ => Step(1));
            for (int i = 0; i < Strip; i++)
            {
                int cell = i;
                Ui.On($"ID_AUTOSHOPADVMENU_BTN{i:00}", _ => SelectCell(cell));
                // remake: a right click puts the car on (or takes it off) the wish list
                var b = menu.Control($"ID_AUTOSHOPADVMENU_BTN{i:00}");
                if (b) { var rc = b.gameObject.AddComponent<RightClick>(); rc.Action = () => ToggleWish(cell); }
            }
        }

        sealed class RightClick : MonoBehaviour, UnityEngine.EventSystems.IPointerClickHandler
        {
            public Action Action;
            public void OnPointerClick(UnityEngine.EventSystems.PointerEventData e) { if (e.button == UnityEngine.EventSystems.PointerEventData.InputButton.Right) Action?.Invoke(); }
        }

        static void WireDetail()
        {
            for (int i = 1; i <= 4; i++) { int k = i - 1; Ui.On("ID_AUTOSHOPADVDETAIL_CHKCOLOR_" + i, _ => { paint = k; RefreshDetail(); scene?.Show(Selection, paint); }); }
            Ui.On("ID_AUTOSHOPADVDETAIL_COMPARE_BTN", _ => Compare());
            Ui.On("ID_AUTOSHOPADVDETAIL_REMODEL_BTN", _ => Modify());
            Ui.On("ID_AUTOSHOPADVDETAIL_PRODUCT_BTN", _ => OpenRelated());
            // the car bonus rows (vehiclelist "Car Assist") carry no texts in this client's data
            foreach (var id in new[] { "FUNC1_TITLE", "FUNC1_TXT", "FUNC2_TITLE", "FUNC2_TXT" }) detail.SetVisible("ID_AUTOSHOPADVDETAIL_" + id, false);
        }

        static void WireMito()
        {
            // no paid currency: the G-Coin price, balance and recharge are hidden
            foreach (var id in new[] { "CASH_TITLE", "MYCASH_TITLE", "CASH_NUM", "MYCASH_NUM", "CASH_RECHARGE" }) mito.SetVisible("ID_AUTOSHOPADVMITO_" + id, false);
            foreach (var c in mito.Controls) if (c.Data.Id >= 2670003 && c.Data.Id <= 2670005) c.gameObject.SetActive(false);   // the CC box frame
            mito.SetText("ID_AUTOSHOPADVMITO_MITO_TITLE", MissionText.Get("11821"));
            mito.SetText("ID_AUTOSHOPADVMITO_MYMITO_TITLE", MissionText.Get("11822"));
            mito.SetText("ID_AUTOSHOPADVMITO_TOTAL_TITLE", MissionText.Get("11820"));
            mito.SetText("ID_AUTOSHOPADVMITO_MILLAGE_TITLE", MissionText.Get("11824"));
            Ui.On("ID_AUTOSHOPADVMITO_BUY_BTN", _ => AskBuy());
            Ui.On("ID_AUTOSHOPADVMITO_SELL_BTN", _ => AskSell());
            Ui.On("ID_AUTOSHOPADVMITO_HISTORY_BTN", _ => ShopHistory.Open());
        }

        static void WireWish()
        {
            Ui.On("ID_AUTOSHOPADV_WISHLIST_BTN_UP", _ => { wishScroll = Mathf.Max(0, wishScroll - 1); RefreshWish(); });
            Ui.On("ID_AUTOSHOPADV_WISHLIST_BTN_DOWN", _ => { wishScroll++; RefreshWish(); });
            for (int i = 0; i < 9; i++)
            {
                int k = i;
                Ui.On("ID_AUTOSHOPADV_WISHLIST_LIST" + i, _ => PickWish(k));
                var x = wish.Control($"ID_AUTOSHOPADV_WISHLIST_CLOSE{i:00}");
                if (x) OriginalMenus.Clickable((RectTransform)x.transform, () => RemoveWish(k));
            }
        }

        // ================= tabs, filters, strip =================
        public static void ShowTab(Tab t)
        {
            tab = t;
            selected = 0;
            filter = FilterAll;
            CloseRelated();
            Rebuild();
        }

        public static void SetFilter(int f)
        {
            filter = f;
            selected = 0;
            Rebuild();
        }

        static IEnumerable<VehicleInfo> DealerCars()
        {
            // cars with a Display slot, in its order (hundreds = class, 0x5af0b0); 999 = shown last
            return GameTables.PlayerCars.Where(c => Row(c).Display > 0).OrderBy(c => Row(c).Display == 999 ? 1 : 0).ThenBy(c => c.Grade).ThenBy(c => Row(c).Display).ThenBy(c => c.ReqLevel);
        }

        static void Rebuild()
        {
            entries.Clear();
            if (tab == Tab.Sell)
            {
                for (int i = 0; i < P.Cars.Count; i++)
                {
                    var info = GameTables.Car(P.Cars[i].VehicleIndex);
                    if (filter > FilterAll && info.Grade != filter - FilterAll) continue;
                    entries.Add(new Entry { Info = info, Owned = P.Cars[i], Slot = i });
                }
            }
            else
            {
                IEnumerable<VehicleInfo> cars = DealerCars();
                // Receive Reward: the coupon cars (coupon.xlt) and scratch-card vehicles
                cars = tab == Tab.Reward ? cars.Where(c => CouponBook.CouponFor(c) != null || Row(c).MakeType >= 1) : cars.Where(c => CouponBook.CouponFor(c) == null && Row(c).MakeType == 0);
                if (filter == FilterNew) cars = cars.Where(c => Row(c).New);
                else if (filter == FilterPopular) cars = cars.Where(c => Row(c).Popular);
                else if (filter == FilterDuffy) cars = cars.Where(c => Row(c).Duffy);
                else if (filter > FilterAll) cars = cars.Where(c => c.Grade == filter - FilterAll);
                foreach (var c in cars) entries.Add(new Entry { Info = c });
            }
            selected = entries.Count == 0 ? -1 : Mathf.Clamp(selected, 0, entries.Count - 1);
            paint = 0;
            Refresh();
        }

        public static void Step(int d)
        {
            if (entries.Count == 0) return;
            selected = (selected + d + entries.Count) % entries.Count;
            AudioLibrary.PlaySfx("sfx/SID_CLICK_YES");
            paint = 0;
            Refresh();
        }

        static int StripStart => entries.Count <= Strip ? 0 : Mathf.Clamp(selected - Strip / 2, 0, entries.Count - Strip);

        public static void SelectCell(int cell)
        {
            int i = StripStart + cell;
            if (i < 0 || i >= entries.Count) return;
            selected = i;
            paint = 0;
            Refresh();
        }

        public static void Refresh()
        {
            if (!bg) return;
            RefreshMenu();
            RefreshDetail();
            RefreshMito();
            RefreshWish();
            if (related) RefreshRelated();
            scene?.Show(Selection, paint);
        }

        static void RefreshMenu()
        {
            foreach (var (id, t) in new[] { ("BUY", Tab.Buy), ("SELL", Tab.Sell), ("REWARD", Tab.Reward) }) OriginalMenus.Pressed(menu.Control("ID_AUTOSHOPADVMENU_" + id), t == tab);
            // filters: New / Popular / Duffy's only when some car qualifies (0x5af660); the sell tab shows the classes the driver owns
            bool buy = tab != Tab.Sell;
            var pool = buy ? DealerCars().ToList() : new List<VehicleInfo>();
            menu.SetVisible("ID_AUTOSHOPADVMENU_NEW", buy && pool.Any(c => Row(c).New));
            menu.SetVisible("ID_AUTOSHOPADVMENU_STAR", buy && pool.Any(c => Row(c).Popular));
            menu.SetVisible("ID_AUTOSHOPADVMENU_GM", buy && pool.Any(c => Row(c).Duffy));
            OriginalMenus.Pressed(menu.Control("ID_AUTOSHOPADVMENU_NEW"), filter == FilterNew);
            OriginalMenus.Pressed(menu.Control("ID_AUTOSHOPADVMENU_STAR"), filter == FilterPopular);
            OriginalMenus.Pressed(menu.Control("ID_AUTOSHOPADVMENU_GM"), filter == FilterDuffy);
            OriginalMenus.Pressed(menu.Control("ID_AUTOSHOPADVMENU_ALL"), filter == FilterAll);
            for (int g = 1; g <= 9; g++)
            {
                var c = menu.Control("ID_AUTOSHOPADVMENU_V" + g);
                OriginalMenus.Pressed(c, filter == FilterAll + g);
                bool any = buy ? pool.Any(v => v.Grade == g) : P.Cars.Any(o => GameTables.Car(o.VehicleIndex).Grade == g);
                OriginalMenus.Dim(c, !any, 0.45f);
            }
            int start = StripStart;
            for (int i = 0; i < Strip; i++)
            {
                int idx = start + i;
                var e = idx < entries.Count ? entries[idx] : null;
                string n = (i + 1).ToString();
                foreach (var b in new[] { "LOGO", "BOARD", "HOLD", "NOTSELL" }) menu.SetVisible($"ID_AUTOSHOPADVMENU_{b}{n}", false);
                menu.SetVisible($"ID_AUTOSHOPADVMENU_LOCK{i:00}", false);
                menu.SetVisible($"ID_AUTOSHOPADVMENU_BTN{i:00}", e != null);
                StripCell(menu.Control($"ID_AUTOSHOPADVMENU_BTN{i:00}"), idx == selected);
                if (e == null) continue;
                menu.SetVisible("ID_AUTOSHOPADVMENU_LOGO" + n, true);
                OriginalMenus.Plate(menu, "ID_AUTOSHOPADVMENU_LOGO" + n, e.Info.UiName, e.Info);
                // badges (ui_button22): BOARD "Equipped" (the car you drive), HOLD "Inventory" (owned), NOTSELL "Locked" with the
                // LOCK padlock over the plate (level too low; in the sell tab a car the dealer doesn't take back)
                bool riding = tab == Tab.Sell ? e.Slot == P.CurrentCar : P.Car != null && P.Car.VehicleIndex == e.Info.Index;
                bool owned = tab != Tab.Sell && P.Cars.Any(c => c.VehicleIndex == e.Info.Index);
                bool locked = tab != Tab.Sell ? P.Level < e.Info.ReqLevel : Row(e.Info).Sellable == 0;
                if (riding) menu.SetVisible("ID_AUTOSHOPADVMENU_BOARD" + n, true);
                else if (owned) menu.SetVisible("ID_AUTOSHOPADVMENU_HOLD" + n, true);
                else if (locked) { menu.SetVisible("ID_AUTOSHOPADVMENU_NOTSELL" + n, true); menu.SetVisible($"ID_AUTOSHOPADVMENU_LOCK{i:00}", true); }
            }
            menu.SetEnabled("ID_AUTOSHOPADVMENU_LEFTBTN", entries.Count > 1);
            menu.SetEnabled("ID_AUTOSHOPADVMENU_RIGHTBTN", entries.Count > 1);
            // the coupon notice (MSG_TXT1/2: 11808 "Coupon required!", 11809 "Press F5 to check your Coupons.")
            var sel = Selection;
            bool needCoupon = tab != Tab.Sell && sel != null && !CouponBook.CanBuy(P, sel.Info);
            foreach (var id in new[] { "MSGBG1", "MSGBG1_2", "MSGBG2", "MSGBG3", "MSG_TXT1", "MSG_TXT2" }) menu.SetVisible("ID_AUTOSHOPADVMENU_" + id, needCoupon);
            if (needCoupon) { menu.SetText("ID_AUTOSHOPADVMENU_MSG_TXT1", MissionText.Get("11808")); menu.SetText("ID_AUTOSHOPADVMENU_MSG_TXT2", MissionText.Get("11809")); }
            // maker and grade on the backdrop
            if (sel != null)
            {
                OriginalMenus.MakerEmblem(bg, "ID_AUTOSHOPADVBG_MARK", sel.Info.Maker);
                bg.SetText("ID_AUTOSHOPADVBG_MARKNAME", sel.Info.Maker ?? "");
                OriginalMenus.Emblem(bg, "ID_AUTOSHOPADVBG_LEVEL", ShopData.Grade(sel.Info, sel.Owned?.Grade ?? 0));
            }
            bg.SetVisible("ID_AUTOSHOPADVBG_MARK", sel != null);
            bg.SetVisible("ID_AUTOSHOPADVBG_MARKNAME", sel != null);
            bg.SetVisible("ID_AUTOSHOPADVBG_LEVEL", sel != null);
            bg.SetVisible("ID_AUTOSHOPADVBG_VBTN", sel != null);
            bg.SetVisible("ID_AUTOSHOPADVBG_RBTN", false);
        }

        /// <summary>The strip's click cells: their own art is a white stand-in cut (and default.dds for the other states), so it is
        /// hidden and a translucent frame marks the selected car instead.</summary>
        static void StripCell(NuiControlView btn, bool on)
        {
            if (!btn) return;
            foreach (Transform t in btn.transform) if (t.name.StartsWith("e") && t.TryGetComponent<RawImage>(out var ri)) ri.enabled = false;
            if (!btn.TryGetComponent<Image>(out var hit)) { hit = btn.gameObject.AddComponent<Image>(); hit.color = new Color(0, 0, 0, 0.001f); }
            var sel = btn.transform.Find("sel");
            if (!sel)
            {
                var rt = NuiView.NewRect("sel", btn.transform);
                NuiView.Stretch(rt);
                rt.offsetMin = new Vector2(-3, -3); rt.offsetMax = new Vector2(3, 3);
                var img = rt.gameObject.AddComponent<Image>();
                img.color = new Color(1f, 0.82f, 0.1f, 0.45f);
                img.raycastTarget = false;
                rt.SetAsFirstSibling();
                sel = rt;
            }
            sel.gameObject.SetActive(on);
        }

        static readonly string[] StatIds = { "SPEED", "ACCEL", "CRASH", "BOOST" };

        static void RefreshDetail()
        {
            var sel = Selection;
            detail.gameObject.SetActive(!related);
            if (sel == null)
            {
                detail.SetText("ID_AUTOSHOPADVDETAIL_CARNAME", tab == Tab.Sell ? "" : "No cars here");
                return;
            }
            var grade = ShopData.Grade(sel.Info, sel.Owned?.Grade ?? 0);
            string carName = grade?.UiName;
            if (string.IsNullOrEmpty(carName) || carName == "0") carName = sel.Info.UiName;   // some vehicleupgrade rows carry no names
            detail.SetText("ID_AUTOSHOPADVDETAIL_CARNAME", carName);
            var st = ShopData.Stats(sel.Info, sel.Owned);
            // some vehicleupgrade rows are empty (all zero): show vehiclelist's own ratings then
            if (st.speed + st.accel + st.crash + st.boost == 0) st = (sel.Info.Speed, sel.Info.Accel, sel.Info.Crash, sel.Info.Boost);
            var cur = P.Car != null ? ShopData.Stats(GameTables.Car(P.Car.VehicleIndex), P.Car) : (0, 0, 0, 0);
            int[] v = { st.speed, st.accel, st.crash, st.boost }, c = { cur.Item1, cur.Item2, cur.Item3, cur.Item4 };
            for (int i = 0; i < 4; i++)
            {
                string s = StatIds[i];
                detail.SetText($"ID_AUTOSHOPADVDETAIL_{s}_NUM", v[i].ToString("000"));
                OriginalMenus.Gauge(detail.Control($"ID_AUTOSHOPADVDETAIL_{s}_GR"), v[i] / 300f);
                int d = v[i] - c[i];
                // crash uses NUM1 / NUM2 for the +/- labels
                string up = s == "CRASH" ? "NUM1" : "TXT1", down = s == "CRASH" ? "NUM2" : "TXT2";
                detail.SetVisible($"ID_AUTOSHOPADVDETAIL_{s}_{up}", d > 0);
                detail.SetVisible($"ID_AUTOSHOPADVDETAIL_{s}_{down}", d < 0);
                if (d > 0) detail.SetText($"ID_AUTOSHOPADVDETAIL_{s}_{up}", "+" + d);
                if (d < 0) detail.SetText($"ID_AUTOSHOPADVDETAIL_{s}_{down}", d.ToString());
            }
            detail.SetText("ID_AUTOSHOPADVDETAIL_SPEED_TITLE", MissionText.Get("10800"));
            detail.SetText("ID_AUTOSHOPADVDETAIL_ACCEL_TITLE", MissionText.Get("10802"));
            detail.SetText("ID_AUTOSHOPADVDETAIL_CRASH_TITLE", MissionText.Get("10801"));
            detail.SetText("ID_AUTOSHOPADVDETAIL_BOOST_TITLE", MissionText.Get("10803"));
            detail.SetText("ID_AUTOSHOPADVDETAIL_LEVEL_TITLE", MissionText.Get("11814"));
            detail.SetText("ID_AUTOSHOPADVDETAIL_TYPE_TITLE", MissionText.Get("11815"));
            detail.SetText("ID_AUTOSHOPADVDETAIL_SIZE", MissionText.Get("11816"));
            detail.SetText("ID_AUTOSHOPADVDETAIL_COLOR_TITLE", MissionText.Get("11819"));
            detail.SetText("ID_AUTOSHOPADVDETAIL_LEVEL_TXT", MissionText.Get("10484", sel.Info.ReqLevel));
            detail.SetText("ID_AUTOSHOPADVDETAIL_TYPE_TXT", TypeText(sel.Info));
            float len = Row(sel.Info).Length > 0 ? Row(sel.Info).Length : sel.Info.LengthMm;
            int size = len < 4100 ? 0 : len < 4600 ? 1 : 2;
            foreach (var (id, k) in new[] { ("SMALL", 0), ("MID", 1), ("LARGE", 2) }) detail.SetVisible("ID_AUTOSHOPADVDETAIL_SIZE_" + id, k == size);
            bool colors = tab != Tab.Sell;
            for (int i = 1; i <= 4; i++)
            {
                detail.SetVisible("ID_AUTOSHOPADVDETAIL_COLOR_" + i, colors);
                detail.SetVisible("ID_AUTOSHOPADVDETAIL_CHKCOLOR_" + i, colors);
                var sw = detail.Control("ID_AUTOSHOPADVDETAIL_COLOR_" + i);
                if (sw) foreach (var ri in sw.GetComponentsInChildren<RawImage>()) ri.color = CarFactory.FactoryColor(sel.Info, i - 1);
                var chk = detail.Control("ID_AUTOSHOPADVDETAIL_CHKCOLOR_" + i);
                if (chk) chk.SetChecked(i - 1 == paint);
            }
            detail.SetEnabled("ID_AUTOSHOPADVDETAIL_COMPARE_BTN", P.Car != null);
        }

        public static long Price(Entry e) => e == null ? 0 : e.Owned != null ? ShopData.SellPrice(e.Info, e.Owned) : ShopData.CarPrice(e.Info);

        static void RefreshMito()
        {
            var sel = Selection;
            mito.SetText("ID_AUTOSHOPADVMITO_MITO_NUM", $"{Price(sel):N0}");
            mito.SetText("ID_AUTOSHOPADVMITO_MYMITO_NUM", $"{P.Mito:N0}");
            mito.SetText("ID_AUTOSHOPADVMITO_MILLAGE_NUM", $"{P.Mileage:N0}");
            mito.SetVisible("ID_AUTOSHOPADVMITO_BUY_BTN", tab != Tab.Sell);
            mito.SetVisible("ID_AUTOSHOPADVMITO_SELL_BTN", tab == Tab.Sell);
            bool owned = sel != null && tab != Tab.Sell && P.Cars.Any(c => c.VehicleIndex == sel.Info.Index);
            mito.SetEnabled("ID_AUTOSHOPADVMITO_BUY_BTN", sel != null && !owned);
            mito.SetEnabled("ID_AUTOSHOPADVMITO_SELL_BTN", sel != null && P.Cars.Count > 1);
        }

        // ================= buying and selling =================
        /// <summary>The Buy button (0x5addd0 / the classic dealer's rules): level 11807, Mito 11806, coupon 11808, already owned, garage full.</summary>
        public static string CheckBuy(Entry e)
        {
            if (e == null) return "";
            if (P.Cars.Any(c => c.VehicleIndex == e.Info.Index)) return $"You already own the {e.Info.UiName}.";
            if (P.Level < e.Info.ReqLevel) return MissionText.Get("11807");
            if (P.Mito < Price(e)) return MissionText.Get("11806");
            if (!CouponBook.CanBuy(P, e.Info)) return MissionText.Get("11808") + "\n" + MissionText.Get("11809");
            if (P.Cars.Count >= 40) return MissionText.Get("11825");
            return null;
        }

        static void AskBuy()
        {
            var e = Selection;
            var err = CheckBuy(e);
            if (err != null) { if (err.Length > 0) UiPopup.Message(err); return; }
            if (GameFlow.BlockedByMission()) return;
            long price = Price(e);
            UiPopup.Show(MissionText.Get("10506") + $"\n{e.Info.UiName}  {price:N0} Mito", id => { if (id == UiPopup.Yes) Buy(null); }, UiPopup.Yes, UiPopup.No);
        }

        /// <summary>Buys the selected car in the chosen colour (tests call it without the confirmation).</summary>
        public static void Buy(Action<bool> done)
        {
            var e = Selection;
            if (CheckBuy(e) != null) { done?.Invoke(false); return; }
            var car = e.Info;
            int price = (int)Price(e);
            Progress.BuyCar(car, price, paint, r =>
            {
                if (r.Ok)
                {
                    if (!OnlineSession.Active) { CouponBook.SpendCoupon(P, car); Mileage.Earn(price); }
                    ShopLog.Add("buy", car.UiName, price, "mito", "");
                    QuestTracker.Notify(QuestEvent.CarBought);
                    AudioLibrary.PlaySfx("sfx/SID_ITEM_BUY");
                    GameFlow.Instance?.RespawnCar();
                    UiPopup.Message($"The {car.UiName} is yours. It's waiting outside.");
                }
                Rebuild();
                done?.Invoke(r.Ok);
            });
        }

        /// <summary>The Sell button (10508): not the car you drive (10491), no performance parts fitted (10492); its custom parts go with it (11813).</summary>
        public static string CheckSell(Entry e)
        {
            if (e == null || e.Owned == null) return "";
            if (P.Cars.Count <= 1) return "You can't sell your last car.";
            if (e.Slot == P.CurrentCar) return MissionText.Get("10491");
            if (e.Owned.PerfSlots != null && e.Owned.PerfSlots.Any(u => u >= 0)) return MissionText.Get("10492");
            return null;
        }

        static void AskSell()
        {
            var e = Selection;
            var err = CheckSell(e);
            if (err != null) { if (err.Length > 0) UiPopup.Message(err); return; }
            long price = Price(e);
            string extra = e.Owned.Parts.Count > 0 ? "\n" + MissionText.Get("11813") : "";
            UiPopup.Show(MissionText.Get("10508") + $"\n{OriginalMenus.CarName(e.Owned)}  {price:N0} Mito{extra}", id => { if (id == UiPopup.Yes) Sell(null); }, UiPopup.Yes, UiPopup.No);
        }

        public static void Sell(Action<bool> done)
        {
            var e = Selection;
            if (CheckSell(e) != null) { done?.Invoke(false); return; }
            int price = (int)Price(e);
            Progress.SellCar(e.Slot, price, r =>
            {
                if (r.Ok) { AudioLibrary.PlaySfx("sfx/SID_ITEM_SELL"); ChatBox.System($"Sold for {price:N0} Mito."); }
                Rebuild();
                done?.Invoke(r.Ok);
            });
        }

        // ================= detail buttons =================
        /// <summary>Compare (AUTOSHOP 3280000): the car you drive (left) against the shown car (right).</summary>
        static void Compare()
        {
            var sel = Selection;
            if (sel == null || P.Car == null) return;
            var v = Ui.Open(3280000);
            if (!v) return;
            var curInfo = GameTables.Car(P.Car.VehicleIndex);
            var l = ShopData.Stats(curInfo, P.Car);
            var r = ShopData.Stats(sel.Info, sel.Owned);
            void Side(string s, VehicleInfo info, (int speed, int accel, int crash, int boost) st, CarGrade g)
            {
                foreach (var (k, val) in new[] { ("SPEED", st.speed), ("ACCEL", st.accel), ("CRASH", st.crash), ("BOOST", st.boost) }) v.SetText($"ID_AUTOSHOP_CARCOMPARE_{s}_{k}", val.ToString());
                v.SetText($"ID_AUTOSHOP_CARCOMPARE_{s}_TOTAL", (st.speed + st.accel + st.crash + st.boost).ToString());
                v.SetText($"ID_AUTOSHOP_CARCOMPARE_{s}_COMBO", "");
                OriginalMenus.Plate(v, $"ID_AUTOSHOP_CARCOMPARE_{s}_CARNAMEPOS", g?.UiName ?? info.UiName, info);
                OriginalMenus.Emblem(v, $"ID_AUTOSHOP_CARCOMPARE_{s}_CARGRADEPOS", g);
            }
            Side("L", curInfo, l, ShopData.Grade(curInfo, P.Car.Grade));
            Side("R", sel.Info, r, ShopData.Grade(sel.Info, sel.Owned?.Grade ?? 0));
            Ui.On("ID_AUTOSHOP_CARCOMPARE_ESC", view => Ui.Close(view));
        }

        /// <summary>Modify: the tuning panel for a car the driver owns (the client's REMODEL opened the drift shop's car remodelling).</summary>
        static void Modify()
        {
            var sel = Selection;
            int slot = sel?.Owned != null ? sel.Slot : sel != null ? P.Cars.FindIndex(c => c.VehicleIndex == sel.Info.Index) : -1;
            if (slot < 0) { UiPopup.Message("Buy this car first, then modify it."); return; }
            if (slot != P.CurrentCar) { UiPopup.Message("Get on this car in the garage to modify it."); return; }
            Close();
            Menus.Tuning();
        }

        // ================= related items (AUTOSHOPADVLIST) =================
        static List<ShopProduct> RelatedItems()
        {
            var sel = Selection;
            if (sel == null) return new List<ShopProduct>();
            if (tab == Tab.Sell && sel.Owned != null)
                return sel.Owned.Parts.Select(id => PartsCatalog.Get(id)).Where(p => p != null).Select(p => new ShopProduct { Part = p }).ToList();
            return Enum.GetValues(typeof(PartSlot)).Cast<PartSlot>().SelectMany(s => PartsCatalog.For(sel.Info, s)).Where(p => !p.Stock && p.Price > 0 && p.Car != null)
                .OrderBy(p => p.Slot).ThenBy(p => p.Price).Select(p => new ShopProduct { Part = p }).ToList();
        }

        public static void OpenRelated()
        {
            if (related) { CloseRelated(); return; }
            related = OriginalMenus.Open(2680000);
            if (!related) return;
            relatedPage = 0;
            Ui.On("ID_AUTOSHOPADVLIST_LEFT", _ => { relatedPage = Mathf.Max(0, relatedPage - 1); RefreshRelated(); });
            Ui.On("ID_AUTOSHOPADVLIST_RIGHT", _ => { relatedPage++; RefreshRelated(); });
            Ui.On("ID_AUTOSHOPADVLIST_CLOSE", _ => CloseRelated());
            for (int i = 1; i <= 6; i++)
            {
                int row = i - 1;
                related.SetVisible("ID_AUTOSHOPADVLIST_CHK" + i, false);
                related.SetVisible("ID_AUTOSHOPADVLIST_CASHICON" + i, false);
                related.SetVisible("ID_AUTOSHOPADVLIST_CASHNUM" + i, false);
                Ui.On("ID_AUTOSHOPADVLIST_BUY" + i, _ => RelatedBuy(row, false));
                Ui.On("ID_AUTOSHOPADVLIST_MILEAGE" + i, _ => RelatedBuy(row, true));
                Ui.On("ID_AUTOSHOPADVLIST_GIFT" + i, _ => { var p = RelatedAt(row); if (p != null) Gifts.Show(p); });
            }
            detail.gameObject.SetActive(false);
            RefreshRelated();
        }

        public static void CloseRelated()
        {
            if (related) OriginalMenus.CloseView(related);
            related = null;
            if (detail) detail.gameObject.SetActive(true);
        }

        static ShopProduct RelatedAt(int row) { var list = RelatedItems(); int i = relatedPage * 6 + row; return i < list.Count ? list[i] : null; }

        static void RefreshRelated()
        {
            if (!related) return;
            var list = RelatedItems();
            int pages = Mathf.Max(1, (list.Count + 5) / 6);
            relatedPage = Mathf.Clamp(relatedPage, 0, pages - 1);
            related.SetText("ID_AUTOSHOPADVLIST_TITLE", MissionText.Get(tab == Tab.Sell ? "11811" : "11810"));
            related.SetEnabled("ID_AUTOSHOPADVLIST_LEFT", relatedPage > 0);
            related.SetEnabled("ID_AUTOSHOPADVLIST_RIGHT", relatedPage < pages - 1);
            var sel = Selection;
            int slot = sel == null ? -1 : sel.Owned != null ? sel.Slot : P.Cars.FindIndex(c => c.VehicleIndex == sel.Info.Index);
            for (int i = 1; i <= 6; i++)
            {
                var p = RelatedAt(i - 1);
                foreach (var id in new[] { "IMAGE_" + i, "NAME" + i, "MITOICON" + i, "MITO_NUM" + i, "MILEAGEICON" + i, "MILEAGE_NUM" + i, "BUY" + i, "GIFT" + i, "MILEAGE" + i })
                    related.SetVisible("ID_AUTOSHOPADVLIST_" + id, p != null);
                if (p == null) continue;
                bool owns = slot >= 0 && P.Cars[slot].Parts.Contains(p.Id);
                related.SetText("ID_AUTOSHOPADVLIST_NAME" + i, p.Name);
                related.SetText("ID_AUTOSHOPADVLIST_MITO_NUM" + i, $"{p.Price:N0}");
                related.SetText("ID_AUTOSHOPADVLIST_MILEAGE_NUM" + i, $"{p.MileagePrice:N0}");
                var img = related.Control("ID_AUTOSHOPADVLIST_IMAGE_" + i);
                if (img) OriginalMenus.ItemTile(img.transform, Vector2.zero, new Vector2(55, 55), p.Icon, p.Name, p.Swatch).GetComponent<Graphic>().raycastTarget = false;
                bool sellTab = tab == Tab.Sell;
                related.SetVisible("ID_AUTOSHOPADVLIST_BUY" + i, !sellTab);
                related.SetVisible("ID_AUTOSHOPADVLIST_MILEAGE" + i, !sellTab);
                related.SetVisible("ID_AUTOSHOPADVLIST_GIFT" + i, !sellTab);
                related.SetEnabled("ID_AUTOSHOPADVLIST_BUY" + i, slot >= 0 && !owns && P.Mito >= p.Price);
                related.SetEnabled("ID_AUTOSHOPADVLIST_MILEAGE" + i, slot >= 0 && !owns && P.Mileage >= p.MileagePrice);
                related.SetEnabled("ID_AUTOSHOPADVLIST_GIFT" + i, OnlineSession.Active);
            }
        }

        static void RelatedBuy(int row, bool mileage)
        {
            var p = RelatedAt(row);
            var sel = Selection;
            if (p == null || sel == null) return;
            int slot = sel.Owned != null ? sel.Slot : P.Cars.FindIndex(c => c.VehicleIndex == sel.Info.Index);
            if (slot < 0) { UiPopup.Message("Buy this car first: its parts go on it."); return; }
            if (mileage)
            {
                UiPopup.Show($"Buy {p.Name} for {p.MileagePrice:N0} mileage?", id =>
                {
                    if (id != UiPopup.Yes) return;
                    Mileage.Buy(p, slot, ok => { if (ok && slot == P.CurrentCar) { var f = GameFlow.Instance; if (f) { f.RespawnCar(); f.ApplyCarStats(); } } Refresh(); });
                }, UiPopup.Yes, UiPopup.No);
                return;
            }
            UiPopup.Show($"Buy {p.Name} for {p.Price:N0} Mito?", id =>
            {
                if (id != UiPopup.Yes) return;
                Progress.BuyPart(slot, p.Part, r =>
                {
                    if (r.Ok)
                    {
                        Mileage.Earn(p.Price);
                        ShopLog.Add("buy", p.Name, p.Price, "mito", "");
                        if (slot == P.CurrentCar) { var f = GameFlow.Instance; if (f) { f.RespawnCar(); f.ApplyCarStats(); } }
                    }
                    else if (!OnlineSession.Active) UiPopup.Message(r.Error);
                    Refresh();
                });
            }, UiPopup.Yes, UiPopup.No);
        }

        // ================= wish list (AUTOSHOPADV 2550000) =================
        public static void ToggleWish(int cell)
        {
            int i = StripStart + cell;
            if (i < 0 || i >= entries.Count) return;
            int idx = entries[i].Info.Index;
            var list = new List<int>(P.WishCars);
            if (list.Contains(idx)) list.Remove(idx);
            else { if (list.Count >= 50) { UiPopup.Message("The wish list is full."); return; } list.Add(idx); ChatBox.System($"{entries[i].Info.UiName} added to your wish list."); }
            SaveWish(list);
        }

        static void SaveWish(List<int> list)
        {
            if (OnlineSession.Active)
            {
                DriftCity.Social.SocialNet.Request("wish.set", new Dictionary<string, object> { ["cars"] = list.Cast<object>().ToList() }, r => RefreshWish());
                P.WishCars = list;
            }
            else { P.WishCars = list; P.Save(); }
            RefreshWish();
        }

        static void RefreshWish()
        {
            if (!wish) return;
            var list = P.WishCars.Select(GameTables.Car).Where(c => c != null).ToList();
            wishScroll = Mathf.Clamp(wishScroll, 0, Mathf.Max(0, list.Count - 9));
            wish.SetText("ID_AUTOSHOPADV_WISHLIST_UPPERTITLE", "");
            for (int i = 0; i < 9; i++)
            {
                var c = wishScroll + i < list.Count ? list[wishScroll + i] : null;
                var slot = wish.Control("ID_AUTOSHOPADV_WISHLIST_LIST" + i);
                wish.SetVisible($"ID_AUTOSHOPADV_WISHLIST_CLOSE{i:00}", c != null);
                if (!slot) continue;
                OriginalMenus.Remove(slot.transform, "car");
                if (c == null) continue;
                var rt = NuiView.NewRect("car", slot.transform);
                rt.sizeDelta = new Vector2(42, 42);
                var ri = rt.gameObject.AddComponent<RawImage>();
                ri.texture = OriginalMenus.CarImage(OriginalMenus.CarKeys, c, out var uv);
                ri.uvRect = uv; ri.raycastTarget = false; ri.enabled = ri.texture;
                if (!ri.texture) OriginalMenus.Caption(rt, "name", c.UiName, "Calibri 9 700", Color.white, TextAnchor.MiddleCenter, wrap: true);
            }
            wish.SetEnabled("ID_AUTOSHOPADV_WISHLIST_BTN_UP", wishScroll > 0);
            wish.SetEnabled("ID_AUTOSHOPADV_WISHLIST_BTN_DOWN", wishScroll < list.Count - 9);
        }

        static void PickWish(int i)
        {
            var list = P.WishCars.Select(GameTables.Car).Where(c => c != null).ToList();
            int k = wishScroll + i;
            if (k >= list.Count) return;
            if (tab == Tab.Sell) tab = Tab.Buy;
            filter = FilterAll;
            Rebuild();
            int at = entries.FindIndex(e => e.Info.Index == list[k].Index);
            if (at < 0) { tab = Tab.Reward; Rebuild(); at = entries.FindIndex(e => e.Info.Index == list[k].Index); }
            if (at >= 0) { selected = at; Refresh(); }
        }

        static void RemoveWish(int i)
        {
            var list = new List<int>(P.WishCars);
            int k = wishScroll + i;
            if (k < list.Count) { list.RemoveAt(k); SaveWish(list); }
        }

        // ================= the showroom (autoshop\autoshop.nif behind the panels) =================
        sealed class Showroom
        {
            static readonly Vector3 Base = new Vector3(0, -3000, 0);
            const int Layer = 30;
            readonly Camera cam;
            readonly RenderTexture rt;
            readonly RawImage layer;
            readonly GameObject room;
            GameObject car;
            Entry shown; int shownPaint = -1;

            public Showroom(NuiView behind)
            {
                rt = new RenderTexture(1024, 768, 24, SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RGB111110Float) ? RenderTextureFormat.RGB111110Float : RenderTextureFormat.RGB565);
                cam = new GameObject("Dealer Camera").AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.35f, 0.37f, 0.4f);
                cam.targetTexture = rt;
                cam.gameObject.AddComponent<GarageLighting>();
                // init\camera.ini "autoshop": (0,-8,2) looking at (-1.1,0,1.2), fov 60; the panels cover the right third
                cam.fieldOfView = 60; cam.nearClipPlane = 0.4f; cam.farClipPlane = 64f;
                cam.transform.position = Base + new Vector3(1.2f, 2f, -8f);
                cam.transform.LookAt(Base + new Vector3(0.6f, 1.0f, 0f));
                cam.cullingMask = 1 << Layer;
                if (DriftCityData.Files.Exists(@"autoshop\autoshop.nif"))
                {
                    room = new NifSceneBuilder { LogErrors = false }.Load(@"autoshop\autoshop.nif");
                    room.transform.position = Base;
                    SetLayer(room);
                }
                // above the backdrop (BG), below the menu and the panels
                layer = UiManager.Instance.Layer("Dealer Showroom", behind.transform.GetSiblingIndex() + 1);
                layer.texture = rt;
                layer.raycastTarget = false;
            }

            static void SetLayer(GameObject go) { foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = Layer; }

            public void Show(Entry e, int paint)
            {
                if (e == shown && paint == shownPaint && car) return;
                shown = e; shownPaint = paint;
                if (car) UnityEngine.Object.Destroy(car);
                car = null;
                if (e == null) return;
                car = e.Owned != null ? CarFactory.BuildVisual(e.Info, e.Owned, Base, Quaternion.Euler(0, 210, 0), out _, out _) : CarFactory.BuildVisual(e.Info, paint, Base, Quaternion.Euler(0, 210, 0));
                if (!car) return;
                SetLayer(car);
                if (e.Owned != null) CarPaint.Apply(car, e.Info, e.Owned);
            }

            public void Release()
            {
                if (car) UnityEngine.Object.Destroy(car);
                if (room) UnityEngine.Object.Destroy(room);
                if (cam) UnityEngine.Object.Destroy(cam.gameObject);
                if (layer) UnityEngine.Object.Destroy(layer.gameObject);
                if (rt) { rt.Release(); UnityEngine.Object.Destroy(rt); }
            }
        }
    }
}
