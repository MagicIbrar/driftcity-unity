using System.Collections.Generic;
using System.Linq;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.Game
{
    /// <summary>
    /// The inventory's Auction page INVEN 3090000 (the classic inventory's page function 0x5d0a60; DriftCity_RE\SHOPS.md
    /// section 3): tabs All / Selling / Sold with their counts (TAB_ALL / SELL / SOLD, the matching TAB_*IMG art), five
    /// rows a view with a scroll bar (SLOT_n: icon, +N badge UPGRADE0n, NAME, MITO, DATE) and one button per row drawn
    /// three ways at the same place: _C "Cancel Auction" on an open listing, _E "Expired" on goods that wait to be taken back
    /// (expired, cancelled with a full bag, won bids) and _D "Sold" on a finished sale. Dropping a bag item on the page registers it (0x5d0490 ->
    /// AUCTIONITEM 1760000); the remake adds a "Register a car" link because its cars aren't car-key items in the bag.
    /// </summary>
    public static class AuctionPage
    {
        static PlayerProfile P => PlayerProfile.Current;
        static UiManager Ui => UiManager.Instance;

        public enum Tab { All, Selling, Sold }
        static NuiView page;
        static Tab tab;
        static int scroll;
        static List<AuctionListing> mine = new List<AuctionListing>();
        public const int Rows = 5;

        public static NuiView View => page;
        public static IReadOnlyList<AuctionListing> Mine => mine;
        public static Tab Current => tab;

        public static void Setup(NuiView v)
        {
            page = v;
            if (!page) return;
            tab = Tab.All;
            scroll = 0;
            Ui.On("ID_INVEN_EXCHANGE_TAB_ALL", _ => ShowTab(Tab.All));
            Ui.On("ID_INVEN_EXCHANGE_TAB_SELL", _ => ShowTab(Tab.Selling));
            Ui.On("ID_INVEN_EXCHANGE_TAB_SOLD", _ => ShowTab(Tab.Sold));
            for (int i = 1; i <= Rows; i++)
            {
                int row = i - 1;
                foreach (var b in new[] { "D", "C", "E" }) Ui.On($"ID_INVEN_EXCHANGE_SLOT_{i}_{b}", _ => RowButton(row));
                // the row takes drops from the bag (register) and shows the goods' tooltip
                var slot = page.Control($"ID_INVEN_EXCHANGE_SLOT_{i}");
                if (!slot) continue;
                var s = ItemSlot.On((RectTransform)slot.transform);
                s.Entry = () => At(row)?.Goods?.Entry();
                s.Accept = e => e != null && ItemUse.Tradeable(e);
                s.Dropped = e => AuctionRegister.Show(e, -1);
            }
            // the page background takes drops too
            var bg = page.Controls.FirstOrDefault(c => c.Data.Id == 3090001);
            if (bg)
            {
                var s = ItemSlot.On((RectTransform)bg.transform);
                s.Accept = e => e != null && ItemUse.Tradeable(e);
                s.Dropped = e => AuctionRegister.Show(e, -1);
            }
            // remake: cars aren't bag items here, so the page offers them directly
            var anchor = page.Control("ID_INVEN_EXCHANGE_TAB_ALL");
            if (anchor)
            {
                var link = OriginalMenus.Cell(anchor, "car link", new Vector2(4, 392), new Vector2(300, 18));
                var t = OriginalMenus.Caption(link, "text", "Register a car...  (drop items here to register them)", "Calibri 12 700", new Color(1f, 0.85f, 0.4f), TextAnchor.MiddleLeft);
                t.raycastTarget = true;
                OriginalMenus.Clickable(link, PickCar);
            }
            var bar = page.Control("ID_INVEN_EXCHANGE_SCROLLBAR");
            if (bar)
            {
                var up = OriginalMenus.Cell(bar, "up", new Vector2(0, 0), new Vector2(22, 24));
                OriginalMenus.Clickable(up, () => { scroll = Mathf.Max(0, scroll - 1); Fill(); });
                var down = OriginalMenus.Cell(bar, "down", new Vector2(0, 310), new Vector2(22, 24));
                OriginalMenus.Clickable(down, () => { scroll++; Fill(); });
            }
            Load();
        }

        static IEnumerable<AuctionListing> Filter(Tab t) => t switch
        {
            Tab.Selling => mine.Where(l => l.Status == "open" && l.Mine),
            Tab.Sold => mine.Where(l => l.Status == "sold" && l.Mine),
            _ => mine.Where(l => l.Status != "taken"),
        };

        static List<AuctionListing> Shown => Filter(tab).ToList();
        static AuctionListing At(int row) { var s = Shown; int i = scroll + row; return i >= 0 && i < s.Count ? s[i] : null; }

        public static void Load()
        {
            AuctionMarket.Mine(list =>
            {
                if (!page) return;
                mine = list;
                Fill();
            });
        }

        public static void ShowTab(Tab t) { tab = t; scroll = 0; Fill(); }

        static void Fill()
        {
            if (!page) return;
            page.SetText("ID_INVEN_EXCHANGE_ALL_COUNT", Filter(Tab.All).Count().ToString());
            page.SetText("ID_INVEN_EXCHANGE_SELL_COUNT", Filter(Tab.Selling).Count().ToString());
            page.SetText("ID_INVEN_EXCHANGE_SOLD_COUNT", Filter(Tab.Sold).Count().ToString());
            foreach (var (id, t) in new[] { ("ALL", Tab.All), ("SELL", Tab.Selling), ("SOLD", Tab.Sold) })
            {
                page.SetVisible($"ID_INVEN_EXCHANGE_TAB_{id}IMG", t == tab);
                page.SetVisible($"ID_INVEN_EXCHANGE_{id}_COUNT", true);
            }
            var shown = Shown;
            scroll = Mathf.Clamp(scroll, 0, Mathf.Max(0, shown.Count - Rows));
            for (int i = 1; i <= Rows; i++)
            {
                var l = At(i - 1);
                string p = $"ID_INVEN_EXCHANGE_SLOT_{i}";
                foreach (var s in new[] { "_NAME", "_MITO", "_DATE" }) page.SetVisible(p + s, l != null);
                var slot = page.Control(p);
                if (slot) OriginalMenus.Remove(slot.transform, "item");
                page.SetVisible($"ID_INVEN_EXCHANGE_UPGRADE{i:00}", l?.Goods != null && l.Goods.Kind == "part" && l.Goods.Enhance > 0);
                // the row button's three looks (ui_contents51): _C "Cancel Auction", _E "Expired" (take back), _D "Sold"
                page.SetVisible(p + "_C", l != null && l.Status == "open" && l.Mine);
                page.SetVisible(p + "_E", l != null && l.Waiting);
                page.SetVisible(p + "_D", l != null && l.Status == "sold");
                if (l == null) continue;
                if (l.Goods != null && l.Goods.Kind == "part") page.SetText($"ID_INVEN_EXCHANGE_UPGRADE{i:00}", "+" + l.Goods.Enhance);
                string state = l.Status switch
                {
                    "open" => "Selling", "sold" => $"Sold to {l.Buyer}", "expired" => "Expired", "won" => "Won", "cancelled" => "Cancelled", _ => l.Status,
                };
                page.SetText(p + "_NAME", l.Name);
                page.SetText(p + "_MITO", l.Status == "sold" ? $"{l.SoldPrice:N0} mito" : $"{l.Price:N0} mito");
                long when = l.Status == "open" ? l.EndsAt : l.ClosedAt;
                var date = System.DateTimeOffset.FromUnixTimeMilliseconds(System.Math.Max(0, when)).ToLocalTime();
                page.SetText(p + "_DATE", l.Status == "open" ? $"{state} - until {date:yyyy-MM-dd HH:mm}" : $"{state} {date:yyyy-MM-dd}");
                if (slot && l.Goods != null)
                {
                    RectTransform tile;
                    if (l.Goods.IsCar)
                    {
                        tile = NuiView.NewRect("item", slot.transform);
                        tile.anchoredPosition = new Vector2(8, -4);
                        tile.sizeDelta = new Vector2(64, 64);
                        var ri = tile.gameObject.AddComponent<RawImage>();
                        ri.texture = OriginalMenus.CarImage(OriginalMenus.CarKeys, l.Goods.CarInfo, out var uv);
                        ri.uvRect = uv;
                        ri.enabled = ri.texture;
                    }
                    else
                    {
                        var e = l.Goods.Entry();
                        tile = OriginalMenus.ItemTile(slot.transform, new Vector2(8, 4), new Vector2(64, 64), e.Icon, e.Name, ItemCatalog.Swatch(e), e.Count);
                    }
                    tile.GetComponent<Graphic>().raycastTarget = false;
                }
            }
        }

        static void RowButton(int row)
        {
            var l = At(row);
            if (l == null) return;
            if (l.Status == "open" && l.Mine)
            {
                UiPopup.Show($"Cancel the auction of {l.Name}?\nThe registration tax ({l.Fee:N0} Mito) is not returned.", id =>
                {
                    if (id != UiPopup.Yes) return;
                    AuctionMarket.Cancel(l, (ok, err) => { if (!ok) UiPopup.Message(err ?? "The server refused that."); Done(); });
                }, UiPopup.Yes, UiPopup.No);
                return;
            }
            if (l.Waiting)
            {
                AuctionMarket.Take(l, (ok, err) =>
                {
                    if (ok) { AudioLibrary.PlaySfx("sfx/SID_ITEM_DROP_GET1"); ChatBox.System($"{l.Name} is back in your {(l.Goods != null && l.Goods.IsCar ? "garage" : "inventory")}."); }
                    else UiPopup.Message(err ?? "The server refused that.");
                    Done();
                });
                return;
            }
            if (l.Status == "sold") UiPopup.Message($"{l.Name} was sold to {l.Buyer} for {l.SoldPrice:N0} Mito.\nThe Mito was sent to your notes (Community window, F1).");
        }

        static void Done()
        {
            InventoryWindow.RefreshIfOpen();
            GameFlow.Instance?.RefreshHud();
            Load();
        }

        static void PickCar()
        {
            if (P == null) return;
            var rows = new List<ListWindow.Row>();
            for (int i = 0; i < P.Cars.Count; i++)
            {
                int slot = i;
                var c = P.Cars[i];
                string why = AuctionMarket.CheckRegister(null, slot, 1, 1, out _);
                rows.Add(new ListWindow.Row(OriginalMenus.CarName(c), why ?? "Can be registered", why == null ? "Register" : null, () => { ListWindow.CloseOpen(); AuctionRegister.Show(null, slot); }, why == null));
            }
            ListWindow.Show("Register a car for auction", rows, $"Registration tax {AuctionMarket.FeePct:0.##}% of the price; unsold cars come back after {AuctionMarket.Days} days.");
        }
    }

    /// <summary>
    /// AUCTIONITEM 1760000 (0x551600; OK runs 0x5c9c00): TEXT1 "Quantity" (10446) over COUNT with +/-, TEXT2 "Price [Max. 2
    /// billion Mito]" (10447) over PRICE. OK checks 10441 / 10442 / 10443 and asks with the tax (10444: 10% less the licence
    /// discount, 7 days), then lists the goods.
    /// </summary>
    public static class AuctionRegister
    {
        static PlayerProfile P => PlayerProfile.Current;
        static NuiView view;
        static BagEntry entry;
        static int carSlot, count, max;

        public static bool IsOpen => view;
        public static NuiView View => view;

        public static void Show(BagEntry e, int car)
        {
            var ui = UiManager.Instance;
            if (!ui || P == null) return;
            if (P.Level < AuctionMarket.MinLevel) { UiPopup.Message(MissionText.Get("11828", AuctionMarket.MinLevel)); return; }
            if (e != null && !ItemUse.Tradeable(e)) { UiPopup.Message(MissionText.Get("10445")); return; }
            Close();
            view = ui.Open(1760000);
            if (!view) return;
            entry = e; carSlot = car;
            max = e != null ? Mathf.Max(1, e.Count) : 1;
            count = max;
            view.SetText("ID_AUCTIONITEM_TEXT1", MissionText.Get("10446"));
            view.SetText("ID_AUCTIONITEM_TEXT2", MissionText.Get("10447"));
            view.SetVisible("ID_AUCTIONITEM_TEXT1", true);
            view.SetVisible("ID_AUCTIONITEM_TEXT2", true);
            var price = view.Control("ID_AUCTIONITEM_PRICE")?.Input;
            if (price) { price.contentType = InputField.ContentType.IntegerNumber; price.characterLimit = 10; price.text = Suggest().ToString(); }
            var cnt = view.Control("ID_AUCTIONITEM_COUNT")?.Input;
            if (cnt)
            {
                cnt.contentType = InputField.ContentType.IntegerNumber;
                cnt.characterLimit = 4;
                cnt.onValueChanged.AddListener(s => { if (int.TryParse(s, out int n)) count = Mathf.Clamp(n, 0, max); });
            }
            SetCount(count);
            view.SetEnabled("ID_AUCTIONITEM_COUNT_PLUS", max > 1);
            view.SetEnabled("ID_AUCTIONITEM_COUNT_MINUS", max > 1);
            ui.On("ID_AUCTIONITEM_COUNT_PLUS", _ => SetCount(count + 1));
            ui.On("ID_AUCTIONITEM_COUNT_MINUS", _ => SetCount(count - 1));
            ui.On("ID_AUCTIONITEM_OK", _ => Ok());
            ui.On("ID_AUCTIONITEM_CANCEL", _ => Close());
        }

        /// <summary>A starting price: what the goods are worth at the shops.</summary>
        static long Suggest()
        {
            if (entry == null) return carSlot >= 0 && carSlot < P.Cars.Count ? new AuctionGoods { Kind = "car", Car = P.Cars[carSlot] }.Value() : 100000;
            var g = entry.Kind == BagKind.Part ? new AuctionGoods { Kind = "part", Type = entry.Part.Type, Grade = entry.Part.Grade, Enhance = entry.Part.Enhance }
                : entry.Kind == BagKind.Shop ? new AuctionGoods { Kind = "shop", ItemId = entry.ItemId, Count = entry.Count } : new AuctionGoods { Kind = "stack", Id = entry.Id, Count = entry.Count };
            return System.Math.Max(100, g.Value());
        }

        static void SetCount(int n)
        {
            count = Mathf.Clamp(n, 1, max);
            var cnt = view ? view.Control("ID_AUCTIONITEM_COUNT")?.Input : null;
            if (cnt) cnt.SetTextWithoutNotify(count.ToString());
        }

        static long Price() => long.TryParse(view ? view.GetInput("ID_AUCTIONITEM_PRICE") : "", out long n) ? n : 0;

        /// <summary>OK with the dialog's values (tests call it with their own).</summary>
        public static void Ok() => Submit(count, Price(), true);

        public static void Submit(int n, long price, bool ask)
        {
            var err = AuctionMarket.CheckRegister(entry, carSlot, n, price, out var goods);
            if (err != null) { UiPopup.Message(err); return; }
            long fee = AuctionMarket.Fee(price);
            var e = entry; int car = carSlot;
            void Go()
            {
                Close();
                AuctionMarket.Register(e, car, n, price, (ok, error) =>
                {
                    if (!ok) UiPopup.Message(string.IsNullOrEmpty(error) ? "The registration failed." : error);
                    InventoryWindow.RefreshIfOpen();
                    if (car >= 0 && ok && car <= (P?.CurrentCar ?? 0)) GameFlow.Instance?.RespawnCar();
                    AuctionPage.Load();
                });
            }
            if (!ask) { Go(); return; }
            // "%2.2f%%" is the client's float format (MissionText.Printf only knows %s %d %u %i)
            string tax = MissionText.Get("10444", fee).Replace("%2.2f", AuctionMarket.FeePct.ToString("0.00"));
            UiPopup.Show($"{goods.Name()}{(n > 1 ? $" x{n}" : "")}  {price:N0} Mito\n{tax}", id => { if (id == UiPopup.Yes) Go(); }, UiPopup.Yes, UiPopup.No);
        }

        public static void Close() { if (view) UiManager.Instance?.Close(view); view = null; }
    }
}
