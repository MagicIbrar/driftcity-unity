using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DriftCity.Core;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.Game
{
    /// <summary>globalcontent.xlt (localize): the client's feature switches and constants (CarShop, AuctionMinLevel, ...).</summary>
    public static class GlobalContent
    {
        static Dictionary<string, string> values;

        public static string Get(string key)
        {
            if (values == null)
            {
                values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var t = XltTable.Parse(DriftCityData.Files.Read(@"localize\globalcontent.xlt"));
                    foreach (var r in t.Rows)
                    {
                        var k = t.Get(r, "Key");
                        var v = t.Get(r, "Value_US");
                        if (string.IsNullOrEmpty(v)) v = t.Get(r, "Value_KR");
                        if (!string.IsNullOrEmpty(k)) values[k.Trim()] = (v ?? "").Trim();
                    }
                }
                catch (Exception ex) { Debug.LogWarning("globalcontent: " + ex.Message); }
            }
            return values.TryGetValue(key, out var s) ? s : null;
        }

        public static int Int(string key, int fallback) => int.TryParse(Get(key), out int n) ? n : fallback;
    }

    /// <summary>A custom-shop product (a part for a car or a paint) as the gift, mileage and wish-list dialogs show it.</summary>
    public sealed class ShopProduct
    {
        public PartItem Part;
        public PaintItem Paint;
        public string Name => Paint?.Name ?? Part?.Name ?? "";
        public string Icon => Part?.Item ?? Paint?.Item;
        public Color? Swatch => Paint != null ? Paint.Color : Part != null && (Part.Slot == PartSlot.Neon || Part.Slot == PartSlot.Tint) ? Part.Tint : (Color?)null;
        /// <summary>The chosen period in days (0 = unlimited, -1 = the product's first option; ShopPeriods).</summary>
        public int Days = -1;
        /// <summary>The chosen way to buy it: period and Mito price (ShopPeriods.For lists them).</summary>
        public PeriodPrice Period { get { var l = ShopPeriods.For(this); return Days < 0 ? (l.Count > 0 ? l[0] : new PeriodPrice()) : ShopPeriods.Pick(this, Days); } }
        public long Price => Period.Price;
        public int Id => Paint?.UniqueId ?? Part?.Id ?? -1;
        public long MileagePrice => Mileage.Price(Price);

        public static ShopProduct ById(int id)
        {
            var paint = ShopData.Paints.FirstOrDefault(x => x.UniqueId == id);
            if (paint != null) return new ShopProduct { Paint = paint };
            var part = PartsCatalog.Get(id);
            return part != null ? new ShopProduct { Part = part } : null;
        }

        /// <summary>The goods reference src/vshop.js takes (gift.send, shop.mileage).</summary>
        public Dictionary<string, object> Ref() => Paint != null
            ? new Dictionary<string, object> { ["kind"] = "shop", ["itemId"] = Paint.UniqueId, ["price"] = Paint.Price, ["name"] = Paint.Name }
            : new Dictionary<string, object> { ["kind"] = "vpart", ["item"] = Part.Id, ["price"] = Price, ["name"] = Part.Name, ["slot"] = Part.Slot.ToString().ToLowerInvariant(),
                ["days"] = Period.Days, ["periods"] = ShopPeriods.For(this).Select(x => (object)new List<object> { x.Days, x.Price }).ToList() };
    }

    /// <summary>
    /// Mileage (the dealer's MILLAGE line, the custom shop's mileage prices and BUYMILEAGE 2630000). The client sold G-Coin
    /// items for mileage; this rebuild has no paid currency, so (remake rules, src/vshop.js): 1 point per 100 Mito spent in
    /// the car dealer and the custom shop, and a product costs a tenth of its Mito price in points (rounded up to 10).
    /// </summary>
    public static class Mileage
    {
        public const int PerMito = 100, Rate = 10;
        static PlayerProfile P => PlayerProfile.Current;

        public static long Price(long mito) => Math.Max(10, (long)Math.Ceiling(mito / (double)Rate / 10.0) * 10);

        /// <summary>Offline twin of the server's accrual (the server adds it online).</summary>
        public static void Earn(long mitoSpent)
        {
            if (OnlineSession.Active || P == null || mitoSpent <= 0) return;
            P.Mileage += mitoSpent / PerMito;
            P.Save();
        }

        /// <summary>Buys a product with mileage: a part for the car in <paramref name="carSlot"/> (fitted) or a paint (applied).</summary>
        public static void Buy(ShopProduct product, int carSlot, Action<bool> done)
        {
            if (P == null || product == null) { done?.Invoke(false); return; }
            long cost = product.MileagePrice;
            if (P.Mileage < cost) { UiPopup.Message("You do not have enough mileage."); done?.Invoke(false); return; }
            if (OnlineSession.Active)
            {
                DriftCity.Social.SocialNet.Request("shop.mileage", new Dictionary<string, object> { ["slot"] = carSlot, ["item"] = product.Ref() }, r =>
                {
                    bool ok = r.B("ok");
                    if (!ok) UiPopup.Message(DriftCity.Social.SocialNet.Err(r));
                    else ShopLog.Add("buy", product.Name, cost, "mileage", "");
                    done?.Invoke(ok);
                });
                return;
            }
            var car = carSlot >= 0 && carSlot < P.Cars.Count ? P.Cars[carSlot] : null;
            if (product.Part != null)
            {
                // the chosen period (ShopPeriods): more days on a period part, or a new / permanent one
                if (car == null || !ShopPeriods.Grant(car, product.Part, product.Period.Days, ItemUse.Now)) { UiPopup.Message("already owned"); done?.Invoke(false); return; }
            }
            else
            {
                P.AddItem(product.Paint.UniqueId);
                if (car != null) car.PaintItem = product.Paint.UniqueId;
            }
            P.Mileage -= cost;
            P.Save();
            ShopLog.Add("buy", product.Name, cost, "mileage", "");
            done?.Invoke(true);
        }
    }

    /// <summary>The offline buy / gift history of the trade-history window (online the server keeps it, shop.history).</summary>
    [Serializable]
    public sealed class ShopLog
    {
        [Serializable] public sealed class Entry { public string Kind, Item, Currency, Other; public long Price, At; }
        public string Driver;
        public List<Entry> Entries = new List<Entry>();

        static ShopLog current;
        static string currentPath;

        static string PathFor() => Path.Combine(Path.GetDirectoryName(PlayerProfile.SavePath) ?? Application.persistentDataPath, $"shops_{PlayerProfile.Slot}.json");

        public static ShopLog Current
        {
            get
            {
                var p = PlayerProfile.Current;
                string path = PathFor();
                if (current != null && currentPath == path && current.Driver == p?.Name) return current;
                current = null;
                try { if (File.Exists(path)) current = JsonUtility.FromJson<ShopLog>(File.ReadAllText(path)); }
                catch (Exception ex) { Debug.LogWarning("shop log: " + ex.Message); }
                if (current == null || current.Driver != p?.Name) current = new ShopLog { Driver = p?.Name };
                currentPath = path;
                return current;
            }
        }

        /// <summary>Records a purchase offline (online the server's shop_log is the record).</summary>
        public static void Add(string kind, string item, long price, string currency, string other)
        {
            if (OnlineSession.Active || PlayerProfile.Current == null) return;
            var log = Current;
            log.Entries.Insert(0, new Entry { Kind = kind, Item = item, Price = price, Currency = currency, Other = other, At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
            if (log.Entries.Count > 200) log.Entries.RemoveRange(200, log.Entries.Count - 200);
            try { File.WriteAllText(currentPath, JsonUtility.ToJson(log)); } catch (Exception ex) { Debug.LogWarning("shop log save: " + ex.Message); }
        }
    }

    /// <summary>
    /// The custom shop's gift dialog SENDGIFT 2840000 (0x65aae0): the product, the receiver's name (Search checks it), a
    /// message, the price against the driver's money; Gift sends it (src/vshop.js gift.send: the sender pays the Mito price,
    /// the receiver gets a note carrying the item). Gifts are sent unlimited: the one period button reads "Unlimited".
    /// Gifts need other drivers, so they work online only.
    /// </summary>
    public static class Gifts
    {
        static PlayerProfile P => PlayerProfile.Current;
        static NuiView view;
        static ShopProduct product;
        public static bool IsOpen => view;
        public static NuiView View => view;
        public const int GiftLevel = 5;   // remake rule (Etc.VShopGiftLevel was a server setting)

        public static void Show(ShopProduct p)
        {
            if (p == null || P == null) return;
            if (!OnlineSession.Active) { UiPopup.Message("Gifts are sent to other drivers: play online to send one."); return; }
            if (P.Level < GiftLevel) { UiPopup.Message($"You need level {GiftLevel} to send gifts."); return; }
            var ui = UiManager.Instance;
            Close();
            view = ui.Open(2840000);
            if (!view) return;
            // gifts go unlimited (remake rule): the one period button reads "Unlimited" (10288)
            p = new ShopProduct { Part = p.Part, Paint = p.Paint, Days = 0 };
            product = p;
            for (int i = 0; i < 4; i++)
            {
                string n = new[] { "1ST", "2ND", "3RD", "4TH" }[i];
                view.SetVisible("ID_VSHOP_SENDGIFT_BTN_PERIOD_" + n, i == 0);
                view.SetVisible("ID_VSHOP_SENDGIFT_TXT_PERIOD_" + n, i == 0);
            }
            view.SetText("ID_VSHOP_SENDGIFT_TXT_PERIOD_1ST", ShopPeriods.Button(p.Period));
            var only = view.Control("ID_VSHOP_SENDGIFT_BTN_PERIOD_1ST");
            if (only) OriginalMenus.Pressed(only, true);
            view.SetText("ID_VSHOP_SENDGIFT_SAMPLE_NAME", p.Name);
            var sample = view.Control("ID_VSHOP_SENDGIFT_SAMPLE_POS");
            if (sample) { sample.SetText(""); OriginalMenus.ItemTile(sample.transform, Vector2.zero, new Vector2(64, 64), p.Icon, p.Name, p.Swatch); }
            view.SetText("ID_VSHOP_SENDGIFT_SAMPLE_PRICE", $"{p.Price:N0}");
            view.SetText("ID_VSHOP_SENDGIFT_TXT_EXPIREDATE", ShopPeriods.Line(p.Period));   // Usage Period: Unlimited
            view.SetText("ID_VSHOP_SENDGIFT_TXT_PRICE", $"{p.Price:N0} Mito");
            view.SetText("ID_VSHOP_SENDGIFT_TXT_OWN", $"{P.Mito:N0} Mito");
            view.SetText("ID_VSHOP_SENDGIFT_TXT_INSUFFIECIENT", P.Mito >= p.Price ? "0 Mito" : $"{p.Price - P.Mito:N0} Mito");
            view.SetText("ID_VSHOP_SENDGIFT_TXT_INFO", "The receiver gets a note with the item. Paints go to the bag, parts onto the car the receiver drives (it must fit).");
            var comment = view.Control("ID_VSHOP_SENDGIFT_EDIT_COMMENT")?.Input;
            if (comment) comment.characterLimit = 80;
            var name = view.Control("ID_VSHOP_SENDGIFT_EDIT_USERNAME")?.Input;
            if (name) name.characterLimit = 16;
            ui.On("ID_VSHOP_SENDGIFT_BTN_SEARCH", _ => Search());
            ui.On("ID_VSHOP_SENDGIFT_BTN_GIFT", _ => Send(view.GetInput("ID_VSHOP_SENDGIFT_EDIT_USERNAME"), view.GetInput("ID_VSHOP_SENDGIFT_EDIT_COMMENT")));
            ui.On("ID_VSHOP_SENDGIFT_BTN_CANCEL", _ => Close());
        }

        /// <summary>Search: is the receiver a driver (the friend list answers for friends; the send itself is the final check).</summary>
        static void Search()
        {
            string to = view ? view.GetInput("ID_VSHOP_SENDGIFT_EDIT_USERNAME").Trim() : "";
            if (to.Length == 0) { UiPopup.Message(MissionText.Get("11009")); return; }
            if (string.Equals(to, P.Name, StringComparison.OrdinalIgnoreCase)) { UiPopup.Message("You cannot send a gift to yourself."); return; }
            ChatBox.System($"{to} will receive the gift if that driver exists.");
        }

        public static void Send(string to, string comment, Action<bool> done = null)
        {
            to = (to ?? "").Trim();
            if (to.Length == 0) { UiPopup.Message(MissionText.Get("11009")); done?.Invoke(false); return; }
            if (P.Mito < product.Price) { UiPopup.Message(MissionText.Get("10500")); done?.Invoke(false); return; }
            var p = product;
            DriftCity.Social.SocialNet.Request("gift.send", new Dictionary<string, object> { ["to"] = to, ["item"] = p.Ref(), ["comment"] = comment ?? "" }, r =>
            {
                bool ok = r.B("ok");
                if (!ok) UiPopup.Message(DriftCity.Social.SocialNet.Err(r));
                else { AudioLibrary.PlaySfx("sfx/SID_ITEM_BUY"); ChatBox.System($"You sent {p.Name} to {r.S("to", to)} (-{(long)r.N("price"):N0} Mito)."); Close(); }
                GameFlow.Instance?.RefreshHud();
                done?.Invoke(ok);
            });
        }

        public static void Close() { if (view) UiManager.Instance?.Close(view); view = null; }
    }

    /// <summary>
    /// The trade history BUYHISTORY 2290000 (0x6474b0): Purchase History / Sent Gifts / Received Gifts tabs, 13 rows a page
    /// with 8 page buttons, a Get Gift button on each received gift that still waits; RECVGIFT 2850000 shows one gift (sender,
    /// date, message) with Get Gift / Get Later. Online from src/vshop.js (shop.history, gift.list, gift.accept); offline from
    /// the local purchase log.
    /// </summary>
    public static class ShopHistory
    {
        static PlayerProfile P => PlayerProfile.Current;
        static NuiView view, recv;
        public enum Tab { Bought, Sent, Received }
        static Tab tab;
        static int page;
        public const int Rows = 14;   // TABRECVGIFT_GET00..13, 32 px apart under the header
        public sealed class Row { public string Item, Other, Currency, Content, Sender; public long Price, At; public int NoteId; public bool Pending; }
        static List<Row> bought = new List<Row>(), sent = new List<Row>(), received = new List<Row>();
        public static bool IsOpen => view;
        public static NuiView View => view;
        public static IReadOnlyList<Row> Received => received;

        public static void Open(Tab start = Tab.Bought)
        {
            AuctionMarket.Hook();
            var ui = UiManager.Instance;
            Close();
            view = ui.Open(2290000);
            if (!view) return;
            // a full-screen page anchored top-centre with offsets from the left edge (like the VSHOP panels), whose backdrop
            // and row stripes are colour-only boxes (C1_1, no image)
            ((RectTransform)view.transform.Find("origin")).anchoredPosition = new Vector2(-512, 0);
            foreach (var c in view.GetComponentsInChildren<NuiControlView>(true))
            {
                if (c.Data.Type != NuiControlView.Static || c.Data.I("I1_1") != null || c.Data.I("C1_1") == null || c.GetComponent<Graphic>()) continue;
                if (!NuiControl.TryVec2(c.Data.I("SZ1"), out float w, out float h)) continue;
                var box = NuiView.NewRect("box", c.transform);
                box.sizeDelta = new Vector2(w, h);
                box.SetAsFirstSibling();
                var img = box.gameObject.AddComponent<Image>();
                img.color = NuiControlView.ParseColor(c.Data.I("C1_1"), Color.gray);
                img.raycastTarget = false;
            }
            tab = start; page = 0;
            ui.On("ID_VSHOP_BUYHISTORY_TAB_BUYHISTORY", _ => ShowTab(Tab.Bought));
            ui.On("ID_VSHOP_BUYHISTORY_TAB_SENDGIFT", _ => ShowTab(Tab.Sent));
            ui.On("ID_VSHOP_BUYHISTORY_TAB_RECVGIFT", _ => ShowTab(Tab.Received));
            ui.On("ID_VSHOP_BUYHISTORY_PAGEPREV", _ => { page = Mathf.Max(0, page - 1); Fill(); });
            ui.On("ID_VSHOP_BUYHISTORY_PAGENEXT", _ => { page++; Fill(); });
            ui.On("ID_VSHOP_BUYHISTORY_QUIT", _ => Close());
            string[] n = { "1ST", "2ND", "3RD", "4TH", "5TH", "6TH", "7TH", "8TH" };
            for (int i = 0; i < n.Length; i++) { int k = i; ui.On("ID_VSHOP_BUYHISTORY_PAGE" + n[i], _ => { page = page / 8 * 8 + k; Fill(); }); }
            for (int i = 0; i < Rows; i++) { int row = i; ui.On($"ID_VSHOP_BUYHISTORY_TABRECVGIFT_GET{i:00}", _ => { var r = At(row); if (r != null && r.Pending) ShowGift(r); }); }
            Load();
        }

        static List<Row> Current => tab == Tab.Bought ? bought : tab == Tab.Sent ? sent : received;
        static Row At(int row) { int i = page * Rows + row; return i < Current.Count ? Current[i] : null; }

        public static void Load(Action done = null)
        {
            if (OnlineSession.Active)
            {
                DriftCity.Social.SocialNet.Request("shop.history", null, h =>
                {
                    Row FromJson(Dictionary<string, object> o) => new Row { Item = o.S("item", ""), Price = (long)o.N("price"), Currency = o.S("currency", "mito"), Other = o.S("other", ""), At = (long)o.N("at") };
                    bought = (h.L("bought") ?? new List<object>()).OfType<Dictionary<string, object>>().Select(FromJson).ToList();
                    sent = (h.L("sent") ?? new List<object>()).OfType<Dictionary<string, object>>().Select(FromJson).ToList();
                    DriftCity.Social.SocialNet.Request("gift.list", null, g =>
                    {
                        var pending = (g.L("pending") ?? new List<object>()).OfType<Dictionary<string, object>>()
                            .Select(o => new Row { Item = o.S("itemName", ""), Sender = o.S("sender", ""), Other = o.S("sender", ""), At = (long)o.N("sentAt"), Content = o.S("content", ""), NoteId = o.I("id"), Pending = true }).ToList();
                        var history = (g.L("received") ?? new List<object>()).OfType<Dictionary<string, object>>().Select(FromJson).ToList();
                        received = pending.Concat(history).ToList();
                        Fill();
                        done?.Invoke();
                    });
                });
                return;
            }
            var log = ShopLog.Current;
            Row R(ShopLog.Entry e) => new Row { Item = e.Item, Price = e.Price, Currency = e.Currency, Other = e.Other, At = e.At };
            bought = log.Entries.Where(e => e.Kind == "buy").Select(R).ToList();
            sent = log.Entries.Where(e => e.Kind == "send").Select(R).ToList();
            received = log.Entries.Where(e => e.Kind == "recv").Select(R).ToList();
            Fill();
            done?.Invoke();
        }

        public static void ShowTab(Tab t) { tab = t; page = 0; Fill(); }

        static void Fill()
        {
            if (!view) return;
            int pages = Mathf.Max(1, (Current.Count + Rows - 1) / Rows);
            page = Mathf.Clamp(page, 0, pages - 1);
            foreach (var (id, t) in new[] { ("BUYHISTORY", Tab.Bought), ("SENDGIFT", Tab.Sent), ("RECVGIFT", Tab.Received) }) OriginalMenus.Pressed(view.Control("ID_VSHOP_BUYHISTORY_TAB_" + id), t == tab);
            foreach (var h in new[] { "DATE", "ITEM", "PRICE" }) view.SetVisible("ID_VSHOP_BUYHISTORY_TABBUY_" + h, tab == Tab.Bought);
            foreach (var h in new[] { "DATE", "ITEM", "PRICE", "RECIEVER" }) view.SetVisible("ID_VSHOP_BUYHISTORY_TABSENDGIFT_" + h, tab == Tab.Sent);
            foreach (var h in new[] { "DATE", "ITEM", "SENDER" }) view.SetVisible("ID_VSHOP_BUYHISTORY_TABRECVGIFT_" + h, tab == Tab.Received);
            string[] n = { "1ST", "2ND", "3RD", "4TH", "5TH", "6TH", "7TH", "8TH" };
            int b = page / 8 * 8;
            for (int i = 0; i < n.Length; i++)
            {
                var id = "ID_VSHOP_BUYHISTORY_PAGE" + n[i];
                view.SetVisible(id, b + i < pages);
                view.SetText(id, (b + i + 1).ToString());
                var c = view.Control(id); if (c) c.Selected = b + i == page;
            }
            // rows over the list box, in the header columns
            var lb = view.Control("ID_VSHOP_BUYHISTORY_LB");
            if (!lb) return;
            OriginalMenus.Remove(lb.transform, "rows");
            var holder = NuiView.NewRect("rows", lb.transform);
            holder.sizeDelta = new Vector2(801, 457);
            float x0 = ((RectTransform)lb.transform).anchoredPosition.x;
            float X(string id) { var c = view.Control(id); return c ? ((RectTransform)c.transform).anchoredPosition.x - x0 : 0; }
            float W(string id) { var c = view.Control(id); return c ? ((RectTransform)c.transform).sizeDelta.x : 100; }
            string p = tab == Tab.Bought ? "TABBUY" : tab == Tab.Sent ? "TABSENDGIFT" : "TABRECVGIFT";
            string[] cols = tab == Tab.Bought ? new[] { "DATE", "ITEM", "PRICE" } : tab == Tab.Sent ? new[] { "DATE", "ITEM", "PRICE", "RECIEVER" } : new[] { "DATE", "ITEM", "SENDER" };
            for (int r = 0; r < Rows; r++)
            {
                var row = At(r);
                view.SetVisible($"ID_VSHOP_BUYHISTORY_TABRECVGIFT_GET{r:00}", tab == Tab.Received && row != null && row.Pending);
                if (row == null) continue;
                foreach (var col in cols)
                {
                    string id = $"ID_VSHOP_BUYHISTORY_{p}_{col}";
                    var cell = NuiView.NewRect(col + r, holder);
                    cell.anchoredPosition = new Vector2(X(id) + 6, -(5 + 32 * r));
                    cell.sizeDelta = new Vector2(W(id) - 12, 30);
                    string text = col switch
                    {
                        "DATE" => DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(0, row.At)).ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                        "ITEM" => row.Item,
                        "PRICE" => row.Currency == "mileage" ? $"{row.Price:N0} mileage" : $"{row.Price:N0} Mito",
                        _ => row.Other,
                    };
                    var label = OriginalMenus.Caption(cell, "t", text, "Trebuchet MS 14 700", new Color(0.08f, 0.1f, 0.14f), col == "ITEM" ? TextAnchor.MiddleLeft : TextAnchor.MiddleCenter);
                    if (label.TryGetComponent<Shadow>(out var sh)) sh.enabled = false;
                }
            }
        }

        /// <summary>RECVGIFT 2850000: one waiting gift; Get Gift takes it (gift.accept), Get Later closes.</summary>
        public static void ShowGift(Row r)
        {
            var ui = UiManager.Instance;
            if (recv) ui.Close(recv);
            recv = ui.Open(2850000);
            if (!recv) return;
            recv.SetText("ID_VSHOP_RECVGIFT_SAMPLE_NAME", r.Item);
            recv.SetText("ID_VSHOP_RECVGIFT_TXT_SENDER", r.Sender);
            recv.SetText("ID_VSHOP_RECVGIFT_TXT_SENDDATE", DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(0, r.At)).ToLocalTime().ToString("yyyy-MM-dd"));
            recv.SetText("ID_VSHOP_RECVGIFT_TXT_COMMENT", r.Content);
            recv.SetText("ID_VSHOP_RECVGIFT_SAMPLE_DATE", "");
            recv.SetText("ID_VSHOP_RECVGIFT_SAMPLE_EXPIREDATE", MissionText.Get("10282"));
            var sample = recv.Control("ID_VSHOP_RECVGIFT_SAMPLE_POS");
            if (sample) { sample.SetText(""); OriginalMenus.ItemTile(sample.transform, Vector2.zero, new Vector2(64, 64), null, r.Item); }
            ui.On("ID_VSHOP_RECVGIFT_ACCEPT", _ => Accept(r, null));
            ui.On("ID_VSHOP_RECVGIFT_REMINDLATER", _ => { if (recv) ui.Close(recv); recv = null; });
        }

        public static void Accept(Row r, Action<bool> done)
        {
            DriftCity.Social.SocialNet.Request("gift.accept", new Dictionary<string, object> { ["id"] = r.NoteId }, res =>
            {
                bool ok = res.B("ok");
                if (!ok) UiPopup.Message(DriftCity.Social.SocialNet.Err(res));
                else
                {
                    AudioLibrary.PlaySfx("sfx/SID_ITEM_DROP_GET1");
                    ChatBox.System($"You accepted {r.Item} from {r.Sender}.");
                    var flow = GameFlow.Instance;
                    if (flow) { flow.RespawnCar(); flow.RepaintCar(); flow.ApplyCarStats(); }
                    if (recv) UiManager.Instance?.Close(recv);
                    recv = null;
                }
                Load();
                done?.Invoke(ok);
            });
        }

        public static void Close()
        {
            var ui = UiManager.Instance;
            if (recv && ui) ui.Close(recv);
            if (view && ui) ui.Close(view);
            view = recv = null;
        }
    }
}
