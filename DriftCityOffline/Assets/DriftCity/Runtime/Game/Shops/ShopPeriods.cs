using System;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Core;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>One way to buy a custom-shop product: <see cref="Days"/> of use (0 = unlimited) for <see cref="Price"/> Mito.</summary>
    public struct PeriodPrice
    {
        public int Days;
        public long Price;
        public bool Unlimited => Days <= 0;
        public override string ToString() => Unlimited ? $"Unlimited {Price}" : $"{Days}D {Price}";
    }

    /// <summary>
    /// The custom shop's period prices (DriftCity_RE\SHOPS.md section 2b).
    ///
    /// Client [exact]: vshopitem.xlt has five price blocks per product, "Period 7D / 30D / 90D / 365D / 0D" with $, Mito,
    /// mileage and bonus Mito prices; the checkout / gift lines (0x64b1a0) fill the period buttons in that order with the
    /// periods the product has (10280 "%d D"), the 0D block as 10288 "Unlimited", and under the price 10281 "Usable for %d
    /// days" or 10282 "Usage Period: Unlimited" for the chosen one. In this client the parts carry 7- and 30-day Mito prices
    /// (spoilers 5,000 / 15,000, aero kits 10,000-30,000 / 30,000-90,000, wheels 15,000 for 7 days ...), never a 0D one.
    ///
    /// Remake rules (server mirror: src/vshop.js periodsFor / shop.buyPeriod):
    ///  - a part offers its table periods at their Mito prices, plus "Unlimited" at <see cref="UnlimitedFactor"/> x the
    ///    30-day price (the price the remake sold parts for before), so earlier permanent parts stay a choice;
    ///    a product without period prices (paints, horns, roof items ...) has only "Unlimited" at its old price;
    ///  - buying a period part again adds the days to what is left ("Usage period will be added to the same item." 11044);
    ///    an unlimited purchase makes it permanent;
    ///  - a period part counts as a gift part (no resale value, OwnedCar.GiftParts); when it runs out it leaves the car
    ///    (taken off if fitted) with "name: This item has already expired." (10454);
    ///  - mileage buys the chosen period for a tenth of its Mito price; gifts are sent unlimited.
    /// </summary>
    public static class ShopPeriods
    {
        public const int UnlimitedFactor = 3;
        static readonly string[] Blocks = { "7D", "30D", "90D", "365D" };
        static Dictionary<int, List<PeriodPrice>> table;

        static PlayerProfile P => PlayerProfile.Current;

        /// <summary>The table's period blocks of a vshopitem UniqueId (days, Mito price), without the remake's unlimited option.</summary>
        public static List<PeriodPrice> TablePeriods(int uniqueId)
        {
            if (table == null) Load();
            return table.TryGetValue(uniqueId, out var l) ? l : new List<PeriodPrice>();
        }

        static void Load()
        {
            table = new Dictionary<int, List<PeriodPrice>>();
            try
            {
                var t = XltTable.Parse(DriftCityData.Files.Read(@"init\vshopitem.xlt"));
                foreach (var r in t.Rows)
                {
                    int id = t.GetInt(r, "UniqueId", -1);
                    if (id < 0 || table.ContainsKey(id)) continue;
                    var list = new List<PeriodPrice>();
                    foreach (var b in Blocks)
                    {
                        int days = t.GetInt(r, "Period " + b), price = t.GetInt(r, "Mito Price" + b);
                        if (days > 0 && price > 0) list.Add(new PeriodPrice { Days = days, Price = price });
                    }
                    if (t.GetInt(r, "Period 0D") > 0 && t.GetInt(r, "Mito Price0D") > 0) list.Add(new PeriodPrice { Days = 0, Price = t.GetInt(r, "Mito Price0D") });
                    if (list.Count > 0) table[id] = list;
                }
            }
            catch (Exception ex) { Debug.LogWarning("shop periods: " + ex.Message); }
        }

        /// <summary>The prices a product is sold at, in the client's button order (shortest first, unlimited last).</summary>
        public static List<PeriodPrice> For(ShopProduct product)
        {
            if (product == null) return new List<PeriodPrice>();
            if (product.Paint != null) return new List<PeriodPrice> { new PeriodPrice { Days = 0, Price = product.Paint.Price } };
            return ForPart(product.Part);
        }

        public static List<PeriodPrice> ForPart(PartItem part)
        {
            var list = new List<PeriodPrice>();
            if (part == null) return list;
            var periods = TablePeriods(part.Id);
            list.AddRange(periods.Where(x => !x.Unlimited));
            var unlimited = periods.FirstOrDefault(x => x.Unlimited);
            long price = unlimited.Price > 0 ? unlimited.Price : list.Count > 0 ? (long)part.Price * UnlimitedFactor : part.Price;
            list.Add(new PeriodPrice { Days = 0, Price = Math.Max(1, price) });
            return list;
        }

        /// <summary>The price of <paramref name="days"/> (0 = unlimited) for a product, or the first option when it has no such period.</summary>
        public static PeriodPrice Pick(ShopProduct product, int days)
        {
            var list = For(product);
            if (list.Count == 0) return new PeriodPrice();
            foreach (var x in list) if (x.Days == days) return x;
            return list[0];
        }

        /// <summary>The period button's caption: "%d D" (10280) or "Unlimited" (10288).</summary>
        public static string Button(PeriodPrice x) => x.Unlimited ? ItemCatalog.Text("10288", "Unlimited") : ItemCatalog.Text("10280", "%d D", x.Days);

        /// <summary>The line under the price: "Usable for %d days" (10281) or "Usage Period: Unlimited" (10282).</summary>
        public static string Line(PeriodPrice x) => x.Unlimited ? ItemCatalog.Text("10282", "Usage Period: Unlimited") : ItemCatalog.Text("10281", "Usable for %d days", x.Days);

        // ---------------- owned period parts ----------------

        static void Ensure(OwnedCar car)
        {
            if (car.TimedParts == null) car.TimedParts = new List<int>();
            if (car.TimedUntil == null) car.TimedUntil = new List<long>();
            while (car.TimedUntil.Count < car.TimedParts.Count) car.TimedUntil.Add(0);
            if (car.TimedUntil.Count > car.TimedParts.Count) car.TimedUntil.RemoveRange(car.TimedParts.Count, car.TimedUntil.Count - car.TimedParts.Count);
            if (car.GiftParts == null) car.GiftParts = new List<int>();
        }

        /// <summary>When a car's period part runs out (Unix ms), 0 when it is permanent or not owned.</summary>
        public static long Until(OwnedCar car, int partId)
        {
            if (car == null) return 0;
            Ensure(car);
            int i = car.TimedParts.IndexOf(partId);
            return i >= 0 ? car.TimedUntil[i] : 0;
        }

        public static bool IsTimed(OwnedCar car, int partId) => Until(car, partId) > 0;

        /// <summary>Days left (rounded up) of a period part, 0 when permanent.</summary>
        public static int DaysLeft(OwnedCar car, int partId, long now)
        {
            long u = Until(car, partId);
            return u <= 0 ? 0 : Mathf.Max(0, Mathf.CeilToInt((u - now) / 86400000f));
        }

        /// <summary>
        /// Offline twin of shop.buyPeriod's grant: a new part (or more days on a period one, or making it permanent); a period
        /// part is a gift part (no resale value). Returns false when the car already owns it for keeps.
        /// </summary>
        public static bool Grant(OwnedCar car, PartItem part, int days, long now)
        {
            if (car == null || part == null) return false;
            Ensure(car);
            int i = car.TimedParts.IndexOf(part.Id);
            bool owned = car.Parts.Contains(part.Id);
            if (owned && i < 0) return false;   // already permanent
            if (days <= 0)
            {
                if (i >= 0) { car.TimedParts.RemoveAt(i); car.TimedUntil.RemoveAt(i); }
                car.GiftParts.Remove(part.Id);
            }
            else if (i >= 0) car.TimedUntil[i] = Math.Max(car.TimedUntil[i], now) + days * 86400000L;
            else
            {
                car.TimedParts.Add(part.Id); car.TimedUntil.Add(now + days * 86400000L);
                if (!car.GiftParts.Contains(part.Id)) car.GiftParts.Add(part.Id);
            }
            if (!owned) car.Parts.Add(part.Id);
            car.Fit(part.Slot, part.Id);
            return true;
        }

        /// <summary>Takes expired period parts off every car (fitted ones go back to stock); returns their names.</summary>
        public static List<string> Sweep(PlayerProfile p, long now)
        {
            var gone = new List<string>();
            if (p == null) return gone;
            foreach (var car in p.Cars)
            {
                Ensure(car);
                for (int i = car.TimedParts.Count - 1; i >= 0; i--)
                {
                    if (car.TimedUntil[i] <= 0 || car.TimedUntil[i] > now) continue;
                    int id = car.TimedParts[i];
                    car.TimedParts.RemoveAt(i); car.TimedUntil.RemoveAt(i);
                    car.Parts.Remove(id); car.GiftParts.Remove(id);
                    var part = PartsCatalog.Get(id);
                    if (part != null && car.Fitted(part.Slot) == id) car.Fit(part.Slot, -1);
                    else foreach (PartSlot s in Enum.GetValues(typeof(PartSlot))) if (car.Fitted(s) == id) car.Fit(s, -1);
                    gone.Add(part?.Name ?? $"custom part {id}");
                }
            }
            return gone;
        }

        public static bool AnyDue(PlayerProfile p, long now) =>
            p != null && p.Cars.Any(c => c.TimedUntil != null && c.TimedUntil.Any(u => u > 0 && u <= now));

        /// <summary>
        /// Buys <paramref name="part"/> for the car in <paramref name="carSlot"/> for <paramref name="days"/> (0 = unlimited)
        /// at the period's price: online shop.buyPeriod (src/vshop.js decides the price), offline the same rule here.
        /// </summary>
        public static void Buy(int carSlot, PartItem part, int days, Action<Progress.Result> done)
        {
            var p = P;
            if (p == null || part == null) { done?.Invoke(new Progress.Result { Error = "no driver" }); return; }
            var pick = Pick(new ShopProduct { Part = part }, days);
            var body = new Dictionary<string, object>
            {
                ["slot"] = carSlot, ["item"] = part.Id, ["days"] = pick.Days, ["price"] = pick.Price,
                ["part"] = part.Slot.ToString().ToLowerInvariant(), ["periods"] = For(new ShopProduct { Part = part }).Select(x => (object)new List<object> { x.Days, x.Price }).ToList(),
            };
            Progress.Op("shop.buyPeriod", body, () =>
            {
                var car = carSlot >= 0 && carSlot < p.Cars.Count ? p.Cars[carSlot] : null;
                if (car == null) return new Progress.Result { Error = "no such car" };
                if (car.Parts.Contains(part.Id) && !IsTimed(car, part.Id)) return new Progress.Result { Error = "already owned" };
                if (p.Mito < pick.Price) return new Progress.Result { Error = "not enough Mito" };
                p.Mito -= pick.Price;
                if (pick.Unlimited) car.Spent += pick.Price;
                Grant(car, part, pick.Days, ItemUse.Now);
                return new Progress.Result { Ok = true, Mito = pick.Price };
            }, r =>
            {
                if (r.Ok) { Mileage.Earn(pick.Price); ShopLog.Add("buy", part.Name + (pick.Unlimited ? "" : $" ({pick.Days} D)"), pick.Price, "mito", ""); }
                done?.Invoke(r);
            });
        }

        // ---------------- expiry ----------------

        static float nextCheck;
        static bool asking;

        /// <summary>Runs out period parts (online shop.partsExpire; the server sweeps and sends the driver) every few seconds.</summary>
        public static void Check(bool force = false)
        {
            var p = P;
            if (p == null || asking) return;
            if (!force && Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 10f;
            long now = ItemUse.Now;
            if (!AnyDue(p, now)) return;
            var names = new List<string>();
            foreach (var car in p.Cars)
                for (int i = 0; car.TimedParts != null && i < car.TimedParts.Count && i < car.TimedUntil.Count; i++)
                    if (car.TimedUntil[i] > 0 && car.TimedUntil[i] <= now) names.Add(PartsCatalog.Get(car.TimedParts[i])?.Name ?? "");
            asking = true;
            Progress.Op("shop.partsExpire", new Dictionary<string, object>(), () => { Sweep(p, now); return new Progress.Result { Ok = true }; }, r =>
            {
                asking = false;
                if (!r.Ok) return;
                foreach (var n in names) ChatBox.System((n.Length > 0 ? n + ": " : "") + ItemCatalog.Text("10454", "This item has already expired."));
                var flow = GameFlow.Instance;
                // the car is rebuilt when it can be (not while racing / chasing)
                if (flow && flow.InCity && !RaceMission.Active && !OnlineRace.Active && !ChaseMission.Active) { flow.RespawnCar(); flow.RepaintCar(); flow.ApplyCarStats(); }
            });
        }

        sealed class Ticker : MonoBehaviour { void Update() => Check(); }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot() => Ensure();

        public static void Ensure()
        {
            if (UnityEngine.Object.FindAnyObjectByType<Ticker>()) return;
            var go = new GameObject("Shop Periods");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<Ticker>();
        }
    }
}
