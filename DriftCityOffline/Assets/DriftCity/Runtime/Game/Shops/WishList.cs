using System;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.Game
{
    /// <summary>
    /// The custom shop's wish list WISHLIST 2540000 (0x667db0): nine 42 px icons down the right edge with an X each and up /
    /// down arrows. A right click on a product row puts it on the list (remake: the client's own add button isn't identified);
    /// clicking an icon opens the checkout BUYWISHLIST 2620000 with every wished item that fits the car. Saved in the
    /// profile (WishList); online through src/vshop.js wish.set.
    /// </summary>
    public static class VShopWish
    {
        static PlayerProfile P => PlayerProfile.Current;
        static NuiView view;
        static int scroll;
        public static bool IsOpen => view;
        public static NuiView View => view;
        public static event Action Changed;

        public static void Open()
        {
            var ui = UiManager.Instance;
            view = OriginalMenus.Open(2540000);
            if (!view) return;
            scroll = 0;
            ui.On("ID_VSHOP_WISHLIST_BTN_UP", _ => { scroll = Mathf.Max(0, scroll - 1); Refresh(); });
            ui.On("ID_VSHOP_WISHLIST_BTN_DN", _ => { scroll++; Refresh(); });
            for (int i = 0; i < 9; i++)
            {
                int k = i;
                ui.On("ID_VSHOP_WISHLIST_LIST" + i, _ => Checkout.ShowWishList());
                var x = view.Control("ID_VSHOP_WISHLIST_CLOSE" + i);
                if (x) OriginalMenus.Clickable((RectTransform)x.transform, () => { var l = Products(); if (scroll + k < l.Count) Toggle(l[scroll + k]); });
            }
            Refresh();
        }

        public static void Close() { OriginalMenus.CloseView(view); view = null; }

        public static List<ShopProduct> Products() => P == null ? new List<ShopProduct>() : P.WishList.Select(ShopProduct.ById).Where(p => p != null).ToList();

        public static bool Contains(ShopProduct p) => p != null && P != null && P.WishList.Contains(p.Id);

        public static void Toggle(ShopProduct p)
        {
            if (p == null || P == null) return;
            var list = new List<int>(P.WishList);
            if (list.Contains(p.Id)) list.Remove(p.Id);
            else
            {
                if (list.Count >= 50) { UiPopup.Message("The wish list is full."); return; }
                list.Add(p.Id);
                ChatBox.System($"{p.Name} added to your wish list.");
            }
            P.WishList = list;
            if (OnlineSession.Active) DriftCity.Social.SocialNet.Request("wish.set", new Dictionary<string, object> { ["items"] = list.Cast<object>().ToList() }, null);
            else P.Save();
            Refresh();
            Changed?.Invoke();
        }

        public static void Refresh()
        {
            if (!view) return;
            var list = Products();
            scroll = Mathf.Clamp(scroll, 0, Mathf.Max(0, list.Count - 9));
            for (int i = 0; i < 9; i++)
            {
                var p = scroll + i < list.Count ? list[scroll + i] : null;
                view.SetVisible("ID_VSHOP_WISHLIST_CLOSE" + i, p != null);
                var slot = view.Control("ID_VSHOP_WISHLIST_LIST" + i);
                if (!slot) continue;
                OriginalMenus.Remove(slot.transform, "item");
                if (p != null) OriginalMenus.ItemTile(slot.transform, Vector2.zero, new Vector2(42, 42), p.Icon, p.Name, p.Swatch).GetComponent<Graphic>().raycastTarget = false;
            }
            view.SetEnabled("ID_VSHOP_WISHLIST_BTN_UP", scroll > 0);
            view.SetEnabled("ID_VSHOP_WISHLIST_BTN_DN", scroll < list.Count - 9);
        }
    }

    /// <summary>
    /// The drift shop checkouts: BUYWISHLIST 2620000 (Mito: the wished items, each with a check box, totals, Mito to pay /
    /// held / missing) and BUYMILEAGE 2630000 (the same paid with mileage, 0x649cc0). Two rows show at a time with a scroll
    /// bar. Each row's period buttons (0x64b1a0) show the product's periods ("%d D" 10280, "Unlimited" 10288) and pick its
    /// price, with "Usable for %d days" (10281) / "Usage Period: Unlimited" (10282) under it (ShopPeriods). The G-Coin lines
    /// stay hidden (no paid currency). The custom shop's Buy button opens this checkout with the one product.
    /// </summary>
    public static class Checkout
    {
        static PlayerProfile P => PlayerProfile.Current;
        static NuiView view;
        static List<ShopProduct> items = new List<ShopProduct>();
        static HashSet<int> unchecked_ = new HashSet<int>();
        static bool mileage;
        static int scroll;
        static Action done;
        public static bool IsOpen => view;
        public static NuiView View => view;
        static readonly string[] N = { "1ST", "2ND" };
        static readonly string[] Cols = { "1ST", "2ND", "3RD", "4TH" };

        /// <summary>A row's period button <paramref name="col"/>: that period's price for item <paramref name="i"/>.</summary>
        public static void PickPeriod(int i, int col)
        {
            if (i < 0 || i >= items.Count) return;
            var options = ShopPeriods.For(items[i]);
            if (col < 0 || col >= options.Count) return;
            items[i].Days = options[col].Days;
            Refresh();
        }

        /// <summary>The products on the checkout (tests).</summary>
        public static IReadOnlyList<ShopProduct> Items => items;

        static string Px => mileage ? "ID_VSHOP_BUYMILEAGE_" : "ID_VSHOP_BUYWISHLIST_";

        /// <summary>The wished items that the car can take (paints always), for Mito.</summary>
        public static void ShowWishList(Action after = null)
        {
            var car = P?.Car;
            if (car == null) return;
            var info = GameTables.Car(car.VehicleIndex);
            var list = VShopWish.Products().Where(p => p.Paint != null ? car.PaintItem != p.Paint.UniqueId
                : PartsCatalog.Fits(p.Part, info) && (!car.Parts.Contains(p.Part.Id) || ShopPeriods.IsTimed(car, p.Part.Id))).ToList();
            if (list.Count == 0) { UiPopup.Message("Nothing on your wish list fits this car (or you own it all)."); return; }
            Show(list, false, after);
        }

        public static void Show(List<ShopProduct> products, bool withMileage, Action after = null)
        {
            var ui = UiManager.Instance;
            Close();
            mileage = withMileage;
            view = ui.Open(withMileage ? 2630000 : 2620000);
            if (!view) return;
            items = products;
            unchecked_.Clear();
            scroll = 0;
            done = after;
            foreach (var row in N)
            {
                int k = Array.IndexOf(N, row);
                for (int c = 0; c < Cols.Length; c++)
                {
                    int col = c;
                    view.SetVisible($"{Px}BTN_PERIOD{row}_{Cols[c]}", false);
                    view.SetVisible($"{Px}TXT_PERIOD{row}_{Cols[c]}", false);
                    ui.On($"{Px}BTN_PERIOD{row}_{Cols[c]}", _ => PickPeriod(scroll + k, col));
                }
                view.SetVisible($"ID_VSHOP_BUYWISHLIST_ICON_HANCOIN{row}", false);
                ui.On($"{Px}CB{row}", _ => { int i = scroll + k; if (i < items.Count) { if (!unchecked_.Remove(i)) unchecked_.Add(i); } Refresh(); });
            }
            // G-Coin lines hidden; the mileage checkout's TOTAL_HANCOIN line is its "Miles" total
            foreach (var id in new[] { "TOTAL_HANCOIN", "HANCOIN_PAY", "HANCOIN_OWN", "HANCOIN_MINUS", "HANCOIN_INFORM" }) view.SetVisible(Px + id, mileage && id == "TOTAL_HANCOIN");
            // (the G-Coin box is part of the frame art (ui_contents17); its lines stay empty)
            var bar = view.Control(Px + "SCROLL");
            if (bar)
            {
                var up = OriginalMenus.Cell(bar, "up", Vector2.zero, new Vector2(13, 20));
                OriginalMenus.Clickable(up, () => { scroll = Mathf.Max(0, scroll - 1); Refresh(); });
                var down = OriginalMenus.Cell(bar, "down", new Vector2(0, 131), new Vector2(13, 20));
                OriginalMenus.Clickable(down, () => { scroll = Mathf.Min(Mathf.Max(0, items.Count - 2), scroll + 1); Refresh(); });
            }
            ui.On(Px + "BUY", _ => Buy(null));
            ui.On(Px + "CANCEL", _ => Close());
            Refresh();
        }

        static List<ShopProduct> Chosen => items.Where((p, i) => !unchecked_.Contains(i)).ToList();
        static long Total => Chosen.Sum(p => mileage ? p.MileagePrice : p.Price);
        static long Held => mileage ? P.Mileage : P.Mito;

        static void Refresh()
        {
            if (!view) return;
            for (int r = 0; r < 2; r++)
            {
                int i = scroll + r;
                var p = i < items.Count ? items[i] : null;
                string n = N[r];
                foreach (var id in new[] { "PL_TILE", "CB", "POS_ITEM", "ITEMNAME", "TXT_EXPIREDATE" }) view.SetVisible(Px + id + n, p != null);
                view.SetVisible(mileage ? $"{Px}ICON_MILEAGE{n}" : $"ID_VSHOP_BUYWISHLIST_ICON_MITO{n}", p != null);
                view.SetVisible(mileage ? $"{Px}TXT_MILEAGE{n}" : $"{Px}TXT_HANCOIN{n}", p != null);
                var sample = view.Control(Px + "POS_ITEM" + n);
                if (sample) { sample.SetText(""); OriginalMenus.Remove(sample.transform, "item"); }
                if (p == null) continue;
                view.SetText(Px + "ITEMNAME" + n, p.Name);
                view.SetText(mileage ? $"{Px}TXT_MILEAGE{n}" : $"{Px}TXT_HANCOIN{n}", $"{(mileage ? p.MileagePrice : p.Price):N0}");
                // the period buttons in the client's order (0x64b1a0): the product's periods, unlimited last
                var options = ShopPeriods.For(p);
                var chosen = p.Period;
                for (int c = 0; c < Cols.Length; c++)
                {
                    bool on = c < options.Count;
                    view.SetVisible($"{Px}BTN_PERIOD{n}_{Cols[c]}", on);
                    view.SetVisible($"{Px}TXT_PERIOD{n}_{Cols[c]}", on);
                    if (!on) continue;
                    view.SetText($"{Px}TXT_PERIOD{n}_{Cols[c]}", ShopPeriods.Button(options[c]));
                    var btn = view.Control($"{Px}BTN_PERIOD{n}_{Cols[c]}");
                    if (btn) OriginalMenus.Pressed(btn, options[c].Days == chosen.Days);
                }
                view.SetText(Px + "TXT_EXPIREDATE" + n, ShopPeriods.Line(chosen));
                if (sample) OriginalMenus.ItemTile(sample.transform, Vector2.zero, new Vector2(64, 64), p.Icon, p.Name, p.Swatch);
                var cb = view.Control(Px + "CB" + n);
                if (cb) cb.SetChecked(!unchecked_.Contains(i));
            }
            view.SetText(Px + "TOTAL_QUANTITY", Chosen.Count.ToString());
            if (!mileage) view.SetText(Px + "TOTAL_MITO", $"{Total:N0} Mito");
            else view.SetText(Px + "TOTAL_HANCOIN", $"{Total:N0}");
            string pay = mileage ? "MILEAGE" : "MITO";
            view.SetText($"{Px}{pay}_PAY", $"{Total:N0}");
            view.SetText($"{Px}{pay}_OWN", $"{Held:N0}");
            view.SetText($"{Px}{pay}_MINUS", Held >= Total ? "0" : $"{Total - Held:N0}");
            view.SetEnabled(Px + "BUY", Chosen.Count > 0 && Held >= Total);
        }

        /// <summary>Buys every checked item for the car you drive, one after the other (parts are fitted, paints applied).</summary>
        public static void Buy(Action<int> finished)
        {
            var list = Chosen;
            if (list.Count == 0 || Held < Total) { if (Held < Total) UiPopup.Message(mileage ? "You do not have enough mileage." : MissionText.Get("10500")); finished?.Invoke(0); return; }
            bool byMileage = mileage;
            Close();
            int slot = P.CurrentCar, bought = 0;
            void Next(int i)
            {
                if (i >= list.Count)
                {
                    var flow = GameFlow.Instance;
                    if (flow) { flow.RespawnCar(); flow.RepaintCar(); flow.ApplyCarStats(); }
                    if (bought > 0) { AudioLibrary.PlaySfx("sfx/SID_ITEM_BUY"); ChatBox.System($"Bought {bought} item(s)."); }
                    done?.Invoke();
                    finished?.Invoke(bought);
                    return;
                }
                var p = list[i];
                if (byMileage) { Mileage.Buy(p, slot, ok => { if (ok) bought++; Next(i + 1); }); return; }
                if (p.Paint != null)
                    Progress.Paint(slot, p.Paint.UniqueId, p.Paint.Price, r => { if (r.Ok) { bought++; Mileage.Earn(p.Price); ShopLog.Add("buy", p.Name, p.Price, "mito", ""); } Next(i + 1); });
                else
                    ShopPeriods.Buy(slot, p.Part, p.Period.Days, r => { if (r.Ok) { bought++; QuestTracker.Notify(QuestEvent.CarUpgraded); } Next(i + 1); });
            }
            Next(0);
        }

        public static void Close() { if (view) UiManager.Instance?.Close(view); view = null; }
    }
}
