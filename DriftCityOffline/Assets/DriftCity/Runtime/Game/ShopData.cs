using System;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Core;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>One grade of a car (vehicleupgrade.xlt): V1, V2, ... with its stats and prices.</summary>
    public sealed class CarGrade
    {
        public string CarName, UiName;
        public int Accel, Speed, Crash, Boost, Price, Sell, UpgradeMito, ReqLevel, GradeLvl;
        public string GradeType;
    }

    public sealed class PaintItem
    {
        public int UniqueId;
        /// <summary>vshopitem ItemName (i_d_c_000NN, glossy i_g_c_000NN): also the item's icon name.</summary>
        public string Item;
        public string Name;
        public int Price;
        public Color Color;
    }

    /// <summary>Shop-side tables: car grades/prices and paints.</summary>
    public static class ShopData
    {
        static Dictionary<int, List<CarGrade>> grades;
        static List<PaintItem> paints;

        public static List<CarGrade> Grades(VehicleInfo car)
        {
            if (grades == null) LoadGrades();
            return car != null && grades.TryGetValue(car.UniqueId, out var g) ? g : new List<CarGrade>();
        }

        /// <summary>A car's ratings: its grade's stats plus the bonuses of its fitted parts.</summary>
        public static (int speed, int accel, int crash, int boost) Stats(VehicleInfo car, OwnedCar owned)
        {
            var g = owned != null ? Grade(car, owned.Grade) : Grade(car, 0);
            var b = PartsCatalog.Bonus(car, owned);
            var p = PlayerProfile.Current;
            var perf = p != null && owned != null && p.Cars.Contains(owned) ? PerfRules.Fitted(p, owned) : new int[4];
            var a = AeroSet.Bonus(owned);   // AEROSET bonus points (3900000)
            return ((g?.Speed ?? car.Speed) + b.speed + perf[0] + a.speed, (g?.Accel ?? car.Accel) + b.accel + perf[1] + a.accel,
                    (g?.Crash ?? car.Crash) + b.crash + perf[2] + a.crash, (g?.Boost ?? car.Boost) + b.boost + perf[3] + a.boost);
        }

        /// <summary>What the reseller pays for a car: the table's resale value for its grade plus 30% of the parts bought for it.</summary>
        public static int SellPrice(VehicleInfo car, OwnedCar owned)
        {
            var g = Grade(car, owned.Grade);
            int baseValue = g != null && g.Sell > 0 ? g.Sell : CarPrice(car) / 20;
            long parts = 0;
            foreach (var id in owned.Parts)
            {
                if (owned.GiftParts != null && owned.GiftParts.Contains(id)) continue;   // given by an item, not paid for
                var part = PartsCatalog.Get(id); if (part != null) parts += part.Price;
            }
            return (int)System.Math.Min(int.MaxValue, baseValue + parts * 3 / 10);
        }

        public static CarGrade Grade(VehicleInfo car, int index)
        {
            var g = Grades(car);
            return g.Count == 0 ? null : g[Mathf.Clamp(index, 0, g.Count - 1)];
        }

        /// <summary>Purchase price of a car: its first grade's price, or a value derived from its stats.</summary>
        public static int CarPrice(VehicleInfo car)
        {
            var g = Grade(car, 0);
            if (g != null && g.Price > 0) return g.Price;
            return 1000 + (car.Accel + car.Speed + car.Crash + car.Boost) * 250;
        }

        static void LoadGrades()
        {
            // one row per grade (V1, V2, ...); "ID" is the car's Unique Id, the name columns are only on its first row
            grades = new Dictionary<int, List<CarGrade>>();
            var t = XltTable.Parse(DriftCityData.Files.Read(@"init\vehicleupgrade.xlt"));
            string name = null;
            foreach (var r in t.Rows)
            {
                int id = t.GetInt(r, "ID", -1);
                if (id < 0) continue;
                if (!string.IsNullOrEmpty(t.Get(r, "Car Name"))) name = t.Get(r, "Car Name");
                var g = new CarGrade
                {
                    CarName = name, UiName = t.Get(r, "Game UI name"),
                    Accel = t.GetInt(r, "Accel"), Speed = t.GetInt(r, "Speed"), Crash = t.GetInt(r, "Crash"), Boost = t.GetInt(r, "Boost"),
                    Price = t.GetInt(r, "Price"), Sell = t.GetInt(r, "Sell"), UpgradeMito = t.GetInt(r, "UpgradeMito"),
                    ReqLevel = t.GetInt(r, "Req Level"), GradeType = t.Get(r, "GradeType"), GradeLvl = t.GetInt(r, "GradeLvl"),
                };
                if (!grades.TryGetValue(id, out var list)) grades[id] = list = new List<CarGrade>();
                list.Add(g);
            }
        }

        public static List<PaintItem> Paints
        {
            get
            {
                if (paints != null) return paints;
                paints = new List<PaintItem>();
                var t = XltTable.Parse(DriftCityData.Files.Read(@"init\vshopitem.xlt"));
                foreach (var r in t.Rows)
                {
                    string cat = t.Get(r, "Category");
                    if (cat != "PAINT" && cat != "PAINTHIGHGLOSS" || t.Get(r, "Support") != "1") continue;
                    var name = t.Get(r, "MarketName");
                    int price = t.GetInt(r, "Mito Price");
                    if (price <= 0) price = cat == "PAINTHIGHGLOSS" ? 8000 : 3000;
                    // the paint's colour is its number in effects\VehicleColor.nxt (i_d_c_000NN / i_g_c_000NN)
                    string item = t.Get(r, "ItemName").Trim();
                    var color = VehicleColors.ForItem(item, out var c) ? c : ColorFromName(name);
                    paints.Add(new PaintItem { UniqueId = t.GetInt(r, "UniqueId"), Item = item, Name = name, Price = price, Color = color });
                }
                return paints;
            }
        }

        /// <summary>Fallback for items outside the colour tables: paint names are colour words ("Silver", "Dark Blue"...).</summary>
        public static Color ColorFromName(string name)
        {
            string n = (name ?? "").ToLowerInvariant();
            var words = new (string word, Color c)[]
            {
                ("white", new Color(0.95f, 0.95f, 0.95f)), ("pearl", new Color(0.93f, 0.92f, 0.88f)), ("silver", new Color(0.75f, 0.76f, 0.78f)),
                ("gray", new Color(0.45f, 0.46f, 0.48f)), ("grey", new Color(0.45f, 0.46f, 0.48f)), ("black", new Color(0.08f, 0.08f, 0.09f)),
                ("red", new Color(0.8f, 0.08f, 0.08f)), ("wine", new Color(0.45f, 0.05f, 0.12f)), ("pink", new Color(0.95f, 0.45f, 0.65f)),
                ("orange", new Color(0.95f, 0.45f, 0.08f)), ("yellow", new Color(0.95f, 0.82f, 0.1f)), ("gold", new Color(0.83f, 0.66f, 0.22f)),
                ("green", new Color(0.12f, 0.6f, 0.2f)), ("lime", new Color(0.55f, 0.9f, 0.15f)), ("mint", new Color(0.55f, 0.9f, 0.75f)),
                ("sky", new Color(0.45f, 0.72f, 0.95f)), ("blue", new Color(0.12f, 0.3f, 0.85f)), ("navy", new Color(0.06f, 0.1f, 0.35f)),
                ("purple", new Color(0.45f, 0.15f, 0.65f)), ("violet", new Color(0.55f, 0.25f, 0.8f)), ("brown", new Color(0.4f, 0.25f, 0.12f)),
                ("beige", new Color(0.85f, 0.78f, 0.62f)), ("cyan", new Color(0.1f, 0.8f, 0.85f)),
            };
            Color c = Color.white; bool found = false;
            foreach (var (word, col) in words) if (n.Contains(word)) { c = found ? Color.Lerp(c, col, 0.5f) : col; found = true; }
            if (n.Contains("dark")) c *= 0.55f;
            if (n.Contains("light")) c = Color.Lerp(c, Color.white, 0.4f);
            if (!found) c = Color.HSVToRGB(Mathf.Abs(n.GetHashCode() % 360) / 360f, 0.7f, 0.85f);
            c.a = 1;
            return c;
        }
    }
}
