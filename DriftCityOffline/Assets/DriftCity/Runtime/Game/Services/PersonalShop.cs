using System.Collections.Generic;
using System.Linq;
using DriftCity.Net;
using DriftCity.UI;

namespace DriftCity.Game.Services
{
    /// <summary>
    /// USERINFO2ND BUY_GOODS, the "personal shop" (DriftCity_RE/SERVICES.md section 7; server src/pshop.js).
    /// Client [exact]: the button 1830022 is named "착용물품구매" ("buy the goods being worn") in game.nui, but the card's handler
    /// 0x54d0b0 has no case for it: the click does nothing in this build. Remake rule [inferred from the name]: the custom
    /// parts fitted on the other driver's current car, with the custom shop's period prices, bought for your current car
    /// through the shop itself (ShopPeriods.Buy / shop.buyPeriod): nothing passes between the drivers. Parts of another car
    /// model are listed greyed out ("doesn't fit"). Server switch <c>personalShop</c>; offline the other drivers are AI
    /// drivers on stock cars, so there is nothing to buy.
    /// </summary>
    public static class PersonalShop
    {
        public sealed class Goods { public int Item; public string Slot = ""; public bool Fits, Owned; public readonly List<(int days, long price)> Periods = new List<(int, long)>(); }

        /// <summary>The last list (tests).</summary>
        public static readonly List<Goods> Last = new List<Goods>();
        public static string LastName { get; private set; } = "";

        /// <summary>BUY_GOODS on <paramref name="driver"/>'s card.</summary>
        public static void Open(string driver)
        {
            if (!ServerSwitches.PersonalShop) { UiPopup.Message(MissionText.Get("10745")); return; }
            Load(driver, ok => { if (ok) Show(); });
        }

        public static void Load(string driver, System.Action<bool> done)
        {
            Last.Clear(); LastName = driver ?? "";
            if (!ServicesNet.Online) { done?.Invoke(true); return; }
            ServicesNet.Request("pshop.list", new Dictionary<string, object> { ["name"] = driver ?? "" }, r =>
            {
                if (r == null || !r.B("ok")) { UiPopup.Message(ServicesNet.Text(r)); done?.Invoke(false); return; }
                LastName = r.S("name", driver);
                foreach (var o in (r.L("goods") ?? new List<object>()).OfType<Dictionary<string, object>>())
                {
                    var g = new Goods { Item = o.I("item"), Slot = o.S("slot", ""), Fits = o.B("fits"), Owned = o.B("owned") };
                    foreach (var p in (o.L("periods") ?? new List<object>()).OfType<Dictionary<string, object>>()) g.Periods.Add((p.I("days"), (long)p.N("price")));
                    Last.Add(g);
                }
                done?.Invoke(true);
            });
        }

        static void Show()
        {
            var p = PlayerProfile.Current;
            var rows = new List<ListWindow.Row>();
            foreach (var g in Last)
            {
                var part = PartsCatalog.Get(g.Item);
                string name = part?.Name ?? $"custom part {g.Item}";
                if (!g.Fits) { rows.Add(new ListWindow.Row(name, "Doesn't fit your car.", null, null, false)); continue; }
                if (g.Owned) { rows.Add(new ListWindow.Row(name, "Owned", null, null, false)); continue; }
                foreach (var (days, price) in g.Periods)
                {
                    int d = days; long pr = price; var pt = part;
                    string when = d == 0 ? ItemCatalog.Text("10288", "Unlimited") : ItemCatalog.Text("10280", "%d D", d);
                    rows.Add(new ListWindow.Row($"{name}  ({when})", $"{pr:N0} Mito", "Buy", () =>
                    {
                        if (pt == null || p == null) return;
                        ShopPeriods.Buy(p.CurrentCar, pt, d, r => { if (r.Ok) { ListWindow.CloseOpen(); UiPopup.Message($"{pt.Name}: bought for your car."); } });
                    }, p != null && p.Mito >= pr));
                }
            }
            if (rows.Count == 0) rows.Add(new ListWindow.Row($"{LastName} wears no custom parts.", null, null, null, false));
            ListWindow.Show($"{LastName}'s goods", rows, $"Mito {p?.Mito ?? 0:N0}   Bought for your current car at the custom shop's prices.");
        }
    }
}
