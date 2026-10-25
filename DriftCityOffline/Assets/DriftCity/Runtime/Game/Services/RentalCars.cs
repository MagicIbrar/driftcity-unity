using System;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Core;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game.Services
{
    /// <summary>A rental of init\visualitem.xlt type 30 "RENTCAR" with its vshopitem prices.</summary>
    public sealed class RentalRow
    {
        public int Id, Vehicle, Grade, Kind;
        public string Item = "", Name = "", Desc = "", CarName = "", Tab = "ETC";
        public readonly List<(int days, long mito)> Periods = new List<(int, long)>();
        public bool Mileage => Kind == 2;
    }

    /// <summary>
    /// Rental cars (DriftCity_RE/SERVICES.md section 3; server src/rentals.js).
    /// Client [exact]: visualitem type 30 RENTCAR rows "&lt;car unique id&gt; &lt;V level&gt; &lt;kind&gt; [sets]" (kind 1 time-limited
    /// pc_rent_*, 2 mileage-limited pc_rent0_* "until you run out of Mittron Fuel"), sold in the custom shop's RENT tabs
    /// (VSHOP 2450000 SPD / ACC / COL / BST / ETC / SPECIAL, server bits net+0x294 &amp; 0x3c00000) for G-Coin; the texts 10950
    /// (no dealer sale), 10951 (no auction), 10952 (no upgrade), 10953 / 10958 (a mileage-limited car is never refilled),
    /// 10959, 10956; op 0x06d (0x502850): 10968 "It's time to return your rental car. You will automatically be moved to
    /// your garage." (OK -> the garage) or 10969 "Your rental car has been returned because the time expired..." with the
    /// same line in the chat. The garage / inventory mark the rental (GARAGE SELCAR_RENT, GETON_PANEL_RENT, CARINFO_RENT).
    /// Remake rules: no G-Coin - a rental costs its dollar price x 1000 Mito; each rental is a new car at its V level; a
    /// mileage-limited car also goes back when its tank is dry; the rental set parts are not fitted. The shop is a list
    /// (the RENT tabs' categories, then the cars with their 7 / 30-day prices): <see cref="Open"/>.
    /// </summary>
    public static class RentalCars
    {
        public const int MitoPerDollar = 1000;
        const long DayMs = 86400000L;
        static readonly (string tab, string key, string name)[] Tabs =
        {
            ("SPD", "RENTACAR_SPD", "Speed Type Rental Car"), ("ACC", "RENTACAR_ACC", "Accel Type Rental Car"), ("COL", "RENTACAR_COL", "Crash Type Rental Car"),
            ("BST", "RENTACAR_BST", "Boost Type Rental Car"), ("ETC", "RENTACAR_ETC", "General Type Rental Car"),
        };

        static List<RentalRow> catalog;
        static PlayerProfile P => PlayerProfile.Current;
        static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public static bool IsRental(OwnedCar c) => c != null && c.RentKind > 0;

        // ---------------- refusals (the client's texts) ----------------
        public static string SellRefusal(OwnedCar c) => IsRental(c) ? MissionText.Get("10950") : null;
        public static string AuctionRefusal(OwnedCar c) => IsRental(c) ? MissionText.Get("10951") : null;
        public static string UpgradeRefusal(OwnedCar c) => IsRental(c) ? MissionText.Get("10952") : null;
        /// <summary>The filling station: a mileage-limited rental is never refilled (10953); a time-limited one refills (10959).</summary>
        public static string RefuelRefusal(OwnedCar c) => c != null && c.RentKind == 2 ? MissionText.Get("10953") : null;

        /// <summary>"N D" / "Nh" left of a rental (the garage's RENT mark line).</summary>
        public static string TimeLeft(OwnedCar c)
        {
            if (!IsRental(c) || c.RentUntil <= 0) return "";
            var t = TimeSpan.FromMilliseconds(Math.Max(0, c.RentUntil - Now));
            return t.TotalDays >= 1 ? $"{(int)t.TotalDays} D" : $"{(int)t.TotalHours}h {t.Minutes:00}m";
        }

        // ---------------- the catalogue ----------------
        /// <summary>The rentals (offline from the game data; online the server's list replaces it).</summary>
        public static List<RentalRow> Catalog => catalog ??= LoadCatalog();

        static List<RentalRow> LoadCatalog()
        {
            var list = new List<RentalRow>();
            var fs = DriftCityData.Files;
            try
            {
                if (fs == null || !fs.Exists(@"init\visualitem.xlt") || !fs.Exists(@"init\vshopitem.xlt")) return list;
                var shop = new Dictionary<string, RentalRow>();
                var vs = XltTable.Parse(fs.Read(@"init\vshopitem.xlt"));
                foreach (var r in vs.Rows)
                {
                    if (vs.Get(r, "Support") != "1") continue;
                    string item = vs.Get(r, "ItemName").Trim().ToLowerInvariant();
                    if (!item.StartsWith("pc_rent") || shop.ContainsKey(item)) continue;
                    var row = new RentalRow { Id = vs.GetInt(r, "UniqueId"), Item = item, Name = vs.Get(r, "MarketName").Trim(), Desc = vs.Get(r, "Desc (Tooltip)").Trim() };
                    foreach (var (d, pr) in new[] { ("Period 7D", "$Price 7D"), ("Period 30D", "$Price 30D") })
                    {
                        int days = vs.GetInt(r, d), price = vs.GetInt(r, pr);
                        if (days > 0 && price > 0) row.Periods.Add((days, (long)price * MitoPerDollar));
                    }
                    if (row.Periods.Count > 0) shop[item] = row;
                }
                var vi = XltTable.Parse(fs.Read(@"init\visualitem.xlt"), 1);
                foreach (var r in vi.Rows)
                {
                    if (r.Length < 7 || r[1].Trim() != "30") continue;
                    string item = r[3].Trim().ToLowerInvariant();
                    if (!shop.TryGetValue(item, out var row)) continue;
                    var v = r[6].Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (v.Length < 3 || !int.TryParse(v[0], out int uid) || !int.TryParse(v[1], out int vlevel) || !int.TryParse(v[2], out int kind) || (kind != 1 && kind != 2)) continue;
                    var car = GameTables.PlayerCars?.FirstOrDefault(c => c.UniqueId == uid);
                    if (car == null) continue;
                    int grades = Mathf.Max(1, ShopData.Grades(car).Count);
                    row.Vehicle = car.Index; row.CarName = car.UiName ?? car.CarName; row.Kind = kind;
                    row.Grade = Mathf.Clamp(vlevel - 1, 0, grades - 1);
                    char t = item[item.Length - 1];
                    row.Tab = t == 's' ? "SPD" : t == 'a' ? "ACC" : t == 'c' ? "COL" : t == 'b' ? "BST" : "ETC";
                    list.Add(row);
                }
            }
            catch (Exception ex) { Debug.LogWarning("rental cars: " + ex.Message); }
            return list;
        }

        static void FromServer(List<object> rows)
        {
            var list = new List<RentalRow>();
            foreach (var o in (rows ?? new List<object>()).OfType<Dictionary<string, object>>())
            {
                var r = new RentalRow { Id = o.I("id"), Item = o.S("item", ""), Name = o.S("name", ""), Desc = o.S("desc", ""), Vehicle = o.I("vehicle"), CarName = o.S("carName", ""),
                    Grade = o.I("grade"), Kind = o.I("kind"), Tab = o.S("tab", "ETC") };
                foreach (var p in (o.L("periods") ?? new List<object>()).OfType<Dictionary<string, object>>()) r.Periods.Add((p.I("days"), (long)p.N("mito")));
                list.Add(r);
            }
            catalog = list;
        }

        // ---------------- the shop ----------------
        /// <summary>The rental shop: the RENT tabs' categories (server switch <c>rentals</c>, 10745 when off).</summary>
        public static void Open()
        {
            if (!ServerSwitches.Rentals) { UiPopup.Message(MissionText.Get("10745")); return; }
            if (ServicesNet.Online)
            {
                ServicesNet.Request("rental.list", null, r =>
                {
                    if (r == null || !r.B("ok")) { UiPopup.Message(ServicesNet.Text(r)); return; }
                    FromServer(r.L("rentals"));
                    ShowTabs();
                });
                return;
            }
            ShowTabs();
        }

        static void ShowTabs()
        {
            var rows = new List<ListWindow.Row>();
            foreach (var (tab, _, name) in Tabs)
            {
                int n = Catalog.Count(x => x.Tab == tab);
                string t = tab;
                rows.Add(new ListWindow.Row(name, $"{n} cars", n > 0 ? "Open" : null, () => ShowTab(t), n > 0));
            }
            ListWindow.Show("Rent-a-Car", rows, $"Mito {P?.Mito ?? 0:N0}   Rentals are new cars: renting again gives you another car.");
        }

        static void ShowTab(string tab)
        {
            var rows = new List<ListWindow.Row> { new ListWindow.Row("< Back", null, "Back", ShowTabs) };
            foreach (var r in Catalog.Where(x => x.Tab == tab))
                foreach (var (days, mito) in r.Periods)
                {
                    var row = r; int d = days; long m = mito;
                    string kind = r.Mileage ? "until the Mittron runs out (max " + d + " days, no refuelling)" : d + " days";
                    rows.Add(new ListWindow.Row($"{r.Name}  ({d} D)", $"{kind}  -  {m:N0} Mito", "Rent", () => Confirm(row, d, m), (P?.Mito ?? 0) >= m));
                }
            ListWindow.Show(Tabs.First(t => t.tab == tab).name, rows, $"Mito {P?.Mito ?? 0:N0}");
        }

        static void Confirm(RentalRow r, int days, long mito)
        {
            UiPopup.Show($"Rent {r.Name} for {days} days for {mito:N0} Mito?", b =>
            {
                if (b != UiPopup.Yes) return;
                Rent(r, days, ok => { if (ok) { ListWindow.CloseOpen(); UiPopup.Message($"{r.Name}: it's in your garage now ({days} D)."); } });
            }, UiPopup.Yes, UiPopup.No);
        }

        /// <summary>Rents a car (online: rental.rent; offline the same rule on the local save).</summary>
        public static void Rent(RentalRow r, int days, Action<bool> done = null)
        {
            var p = P;
            if (p == null || r == null) { done?.Invoke(false); return; }
            if (ServicesNet.Online)
            {
                ServicesNet.Request("rental.rent", new Dictionary<string, object> { ["id"] = r.Id, ["days"] = days }, m =>
                {
                    bool ok = m != null && m.B("ok");
                    if (!ok) UiPopup.Message(ServicesNet.Text(m));
                    else { GameFlow.Instance?.ApplyCarStats(); GameFlow.Instance?.RefreshHud(); }
                    done?.Invoke(ok);
                });
                return;
            }
            var per = r.Periods.FirstOrDefault(x => x.days == days);
            string err = per.days == 0 ? "This item is not sold for that period." : p.Cars.Count >= 40 ? "garage full" : p.Mito < per.mito ? "not enough Mito" : null;
            if (err != null) { UiPopup.Message(err); done?.Invoke(false); return; }
            p.Mito -= per.mito;
            p.Cars.Add(new OwnedCar { VehicleIndex = r.Vehicle, Grade = r.Grade, RentKind = r.Kind, RentUntil = Now + days * DayMs, RentItem = r.Id, RentName = r.Name });
            p.CurrentCar = p.Cars.Count - 1;
            p.Save();
            GameFlow.Instance?.ApplyCarStats(); GameFlow.Instance?.RefreshHud();
            done?.Invoke(true);
        }

        // ---------------- returns ----------------
        static float nextLocal;
        static bool returning;

        /// <summary>Once a second (ServicesHost): offline expiry; a dry mileage-limited rental goes back.</summary>
        public static void Tick()
        {
            var p = P;
            if (p == null || p.Cars == null) return;
            var cur = p.Car;
            if (cur != null && cur.RentKind == 2 && MittronFuel.Empty && !returning && !GameFlow.MissionActive) ReturnDry(p, cur);
            if (ServicesNet.Online || Time.unscaledTime < nextLocal) return;
            nextLocal = Time.unscaledTime + 5f;
            long now = Now;
            for (int i = p.Cars.Count - 1; i >= 0; i--)
            {
                var c = p.Cars[i];
                if (!(c.RentKind > 0 && c.RentUntil > 0 && c.RentUntil <= now)) continue;
                var (name, current) = Remove(p, i);
                p.Save();
                OnReturned(name, current);
            }
        }

        static void ReturnDry(PlayerProfile p, OwnedCar car)
        {
            int slot = p.Cars.IndexOf(car);
            if (slot < 0) return;
            returning = true;
            if (ServicesNet.Online)
            {
                ServicesNet.Request("rental.return", new Dictionary<string, object> { ["slot"] = slot, ["reason"] = "fuel" }, r =>
                {
                    returning = false;
                    if (r != null && r.B("ok")) OnReturned(r.S("returned", car.RentName ?? "rental car"), r.B("current"));
                });
                return;
            }
            var (name, current) = Remove(p, slot);
            p.Save();
            returning = false;
            OnReturned(name, current);
        }

        /// <summary>Takes car slot <paramref name="i"/> off (the current car moves to the first own car).</summary>
        public static (string name, bool current) Remove(PlayerProfile p, int i)
        {
            var c = p.Cars[i];
            bool current = p.CurrentCar == i;
            p.Cars.RemoveAt(i);
            if (current) p.CurrentCar = Mathf.Max(0, p.Cars.FindIndex(x => !IsRental(x)));
            else if (p.CurrentCar > i) p.CurrentCar--;
            p.CurrentCar = Mathf.Clamp(p.CurrentCar, 0, Mathf.Max(0, p.Cars.Count - 1));
            return (string.IsNullOrEmpty(c.RentName) ? GameTables.Car(c.VehicleIndex)?.UiName ?? "rental car" : c.RentName, current);
        }

        /// <summary>The last return shown (tests).</summary>
        public static string LastReturned { get; private set; }

        /// <summary>op 0x06d: 10968 (the rental you drove; OK -> the garage) or 10969, and the chat line.</summary>
        public static void OnReturned(string name, bool current)
        {
            LastReturned = name;
            string key = current ? "10968" : "10969";
            string text = MissionText.Get(key);
            ChatBox.System(text);
            GameFlow.Instance?.ApplyCarStats(); GameFlow.Instance?.RefreshHud();
            UiPopup.Message(text, () =>
            {
                var flow = GameFlow.Instance;
                if (current && flow && flow.InCity && !GameFlow.MissionActive) flow.ReturnToParkingLot();
            });
        }

        /// <summary>Tests: forget the catalogue (reloaded from the data or the server).</summary>
        public static void ResetCatalog() => catalog = null;
    }
}
