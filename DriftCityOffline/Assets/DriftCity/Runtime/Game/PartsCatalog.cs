using System.Collections.Generic;
using System.Linq;
using DriftCity.Core;
using UnityEngine;

namespace DriftCity.Game
{
    public enum PartSlot { Aero, Hood, Wing, Wheels, Booster, Neon, Plate, Duct, Roof, Tint, Decal, Horn, Engine }

    /// <summary>A visual-shop part from init\vshopitem.xlt that has a model in the installed client.</summary>
    public sealed class PartItem
    {
        public int Id;
        public string Item, Name, Desc;
        public PartSlot Slot;
        public int Price;
        public int Speed, Accel, Boost, Crash;
        /// <summary>Model base of the only car it fits ("pc_0000c"), or null for parts that fit every car.</summary>
        public string Car;
        /// <summary>Body variant for aero kits / front bumpers, decal number (decal.nxt), or the car whose engine a sound item uses.</summary>
        public int Variant;
        /// <summary>Dummy node the part sits on (generic spoilers, air ducts, roof items).</summary>
        public string Mount;
        /// <summary>Horn sound (audio\sfx).</summary>
        public string Sound;
        public string Nif;
        public Color Tint = Color.white;
        /// <summary>The car's factory part (free, fitted when nothing else is).</summary>
        public bool Stock;

        public string BonusText
        {
            get
            {
                var s = new List<string>();
                if (Speed != 0) s.Add($"SPD +{Speed}");
                if (Accel != 0) s.Add($"ACC +{Accel}");
                if (Crash != 0) s.Add($"DUR +{Crash}");
                if (Boost != 0) s.Add($"BST +{Boost}");
                return string.Join("  ", s);
            }
        }
    }

    /// <summary>
    /// The custom shop's parts: aero kits (whole bodies of "s" cars), front bumpers and hoods ("c" cars),
    /// car-specific wings, wheels, booster flames and underbody neon. Prices are the original 30-day Mito
    /// prices, here paid once for keeps.
    /// </summary>
    public static class PartsCatalog
    {
        static List<PartItem> all;
        static Dictionary<int, PartItem> byId;

        public static string SlotName(PartSlot slot, VehicleInfo car = null) => slot switch
        {
            PartSlot.Aero => car != null && car.ComboBody ? "Front bumper" : "Aero kit",
            PartSlot.Hood => "Hood",
            PartSlot.Wing => "Wing",
            PartSlot.Wheels => "Wheels",
            PartSlot.Booster => "Booster flame",
            PartSlot.Neon => "Neon",
            PartSlot.Plate => "Number plate",
            PartSlot.Duct => "Air duct",
            PartSlot.Roof => "Roof item",
            PartSlot.Tint => "Window tint",
            PartSlot.Decal => "Decal",
            PartSlot.Horn => "Horn",
            _ => "Engine sound",
        };

        /// <summary>visualitem.xlt "Exist" columns: which cars (Unique Id) each item fits.</summary>
        static Dictionary<int, HashSet<int>> fitsCars;

        static void LoadFits()
        {
            fitsCars = new Dictionary<int, HashSet<int>>();
            decalIndex = new Dictionary<int, int>();
            var t = XltTable.Parse(DriftCityData.Files.Read(@"init\visualitem.xlt"));
            var carCols = new List<(int col, int car)>();
            for (int i = 7; i < t.Columns.Length; i++) if (int.TryParse(t.Columns[i].Trim(), out int uid)) carCols.Add((i, uid));
            string category = "";
            foreach (var r in t.Rows)
            {
                if (r.Length > 0 && r[0].Trim().Length > 0) category = r[0].Trim();
                if (r.Length < 8 || !int.TryParse(r[4].Trim(), out int id)) continue;
                var set = new HashSet<int>();
                foreach (var (col, car) in carCols) if (col < r.Length && r[col].Trim() == "1") set.Add(car);
                fitsCars[id] = set;
                if (category == "Decal" && int.TryParse(r[2].Trim(), out int index)) decalIndex[id] = index;
            }
        }
        static Dictionary<int, int> decalIndex;

        public static List<PartItem> All
        {
            get
            {
                if (all != null) return all;
                all = new List<PartItem>();
                byId = new Dictionary<int, PartItem>();
                var fs = DriftCityData.Files;
                LoadFits();
                var spoilerMount = new Dictionary<string, string>();
                var sl = XltTable.Parse(fs.Read(@"init\spoilerlist.xlt"));
                foreach (var r in sl.Rows) spoilerMount[sl.Get(r, "Spoiler ID").Trim()] = sl.Get(r, "Type").Trim() == "2" ? "spoiler2" : "spoiler";
                var t = XltTable.Parse(fs.Read(@"init\vshopitem.xlt"));
                foreach (var r in t.Rows)
                {
                    if (t.Get(r, "Support") != "1") continue;
                    string item = t.Get(r, "ItemName").Trim(), sub = t.Get(r, "Sub Catergory");
                    string lower = item.ToLowerInvariant();
                    var p = new PartItem
                    {
                        Id = t.GetInt(r, "UniqueId", -1), Item = item, Name = t.Get(r, "MarketName"), Desc = t.Get(r, "Desc (Tooltip)"),
                        Speed = t.GetInt(r, "Bonus Speed"), Accel = t.GetInt(r, "Bonus Accel"), Boost = t.GetInt(r, "Bonus Boost"), Crash = t.GetInt(r, "Bonus Crash"),
                        Stock = t.Get(r, "Default Part") == "1",
                    };
                    if (p.Id < 0 || byId.ContainsKey(p.Id)) continue;
                    int price30 = t.GetInt(r, "Mito Price30D"), price7 = t.GetInt(r, "Mito Price7D");
                    p.Price = price30 > 0 ? price30 : price7 * 3;
                    bool ok = false;
                    switch (sub)
                    {
                        case "AERO_SET":         // pc_0003s_02 -> body pc_0003s_02.nif
                        case "AERO_COMBI_BUMPER": // pc_0000c_b02 -> body pc_0000c_b02.nif
                        {
                            var m = System.Text.RegularExpressions.Regex.Match(lower, @"^(pc_\d+[cs])_b?(\d\d)$");
                            if (!m.Success) break;
                            p.Slot = PartSlot.Aero; p.Car = m.Groups[1].Value; p.Variant = int.Parse(m.Groups[2].Value);
                            p.Nif = $@"vehicle\player\{lower}.nif";
                            ok = fs.Exists(p.Nif);
                            if (p.Price <= 0) p.Price = 40000;
                            break;
                        }
                        case "AERO_COMBI_INTERCOOLER": // pc_0000c_i02 -> extra hood mesh
                        {
                            var m = System.Text.RegularExpressions.Regex.Match(lower, @"^(pc_\d+c)_i\d\d$");
                            if (!m.Success) break;
                            p.Slot = PartSlot.Hood; p.Car = m.Groups[1].Value; p.Nif = $@"vehicle\player\{lower}.nif";
                            ok = fs.Exists(p.Nif);
                            if (p.Price <= 0) p.Price = 25000;
                            break;
                        }
                        case "SPOILER_SUB": // pc_0000c_sp01 -> wing mesh in car space; i_d_s_* -> universal spoiler on the "spoiler" dummy
                        {
                            if (lower.StartsWith("i_d_s_"))
                            {
                                p.Slot = PartSlot.Wing; p.Nif = $@"vehicle\spoiler\{lower}.nif"; p.Mount = "spoiler";
                                ok = fs.Exists(p.Nif);
                                if (p.Price <= 0) p.Price = 20000;
                                break;
                            }
                            var m = System.Text.RegularExpressions.Regex.Match(lower, @"^(pc_\d+[cs])_sp\d\d$");
                            if (!m.Success) break;
                            p.Slot = PartSlot.Wing; p.Car = m.Groups[1].Value; p.Nif = $@"vehicle\player\{lower}.nif";
                            ok = fs.Exists(p.Nif);
                            if (p.Price <= 0) p.Price = 15000;
                            break;
                        }
                        case "TIRE_SUB":
                        case "GLOSSTIRE_SUB":
                            p.Slot = PartSlot.Wheels; p.Nif = $@"vehicle\player\wheel\{lower}.nif";
                            ok = (lower.StartsWith("tire_ply") || lower.StartsWith("gtire_ply")) && fs.Exists(p.Nif);
                            if (p.Price <= 0) p.Price = sub == "GLOSSTIRE_SUB" ? 45000 : 15000;
                            break;
                        case "NUMBERPLATE_COLOR":
                        case "NUMBERPLATE_LINE":
                        case "NUMBERPLATE_GRAPHIC":
                            p.Slot = PartSlot.Plate; p.Nif = $@"vehicle\nplate\{lower}.nif";
                            ok = fs.Exists(p.Nif);
                            if (p.Price <= 0) p.Price = 3000;
                            break;
                        case "Air Duct_SUB":
                            p.Slot = PartSlot.Duct; p.Nif = $@"vehicle\airintake\{lower}.nif"; p.Mount = "fc";
                            ok = fs.Exists(p.Nif);
                            if (p.Price <= 0) p.Price = 60000;
                            break;
                        case "ROOF_PSYCHY":
                        case "ROOF_BILLBOARD":
                        case "ROOF_ETC":
                        case "ROOF_EVENT":
                            p.Slot = PartSlot.Roof; p.Nif = $@"vehicle\roof\{lower}.nif"; p.Mount = "light01";
                            ok = fs.Exists(p.Nif);
                            if (p.Price <= 0) p.Price = 80000;
                            break;
                        case "WINDOWTINTING_SUB":
                            // effects\VehicleWindowColor.nxt, with the glass alpha it gives
                            p.Slot = PartSlot.Tint; p.Tint = VehicleColors.ForItem(item, out var wc) ? wc : ShopData.ColorFromName(p.Name) * 0.6f;
                            ok = true;
                            if (p.Price <= 0) p.Price = 10000;
                            break;
                        case "SOUND_HORN":
                        {
                            var m = System.Text.RegularExpressions.Regex.Match(lower, @"^i_d_h_(\d+)$");
                            p.Slot = PartSlot.Horn; p.Sound = m.Success ? $"sfx/SID_I_D_H_{m.Groups[1].Value}" : null;
                            ok = m.Success && AudioLibrary.Exists(p.Sound);
                            if (p.Price <= 0) p.Price = 5000;
                            break;
                        }
                        case "SOUND_ENGINE": // i_d_e_<car Unique Id>: that car's engine
                        {
                            var m = System.Text.RegularExpressions.Regex.Match(lower, @"^i_d_e_(\d+)$");
                            p.Slot = PartSlot.Engine; ok = m.Success;
                            if (ok) p.Variant = int.Parse(m.Groups[1].Value);
                            if (p.Price <= 0) p.Price = 25000;
                            break;
                        }
                        case "BOOSTER_SUB":
                            p.Slot = PartSlot.Booster; p.Nif = $@"vehicle\booster\{lower}.nif";
                            ok = fs.Exists(p.Nif);
                            if (p.Price <= 0) p.Price = 45000;
                            break;
                        case "NEON_COLOR":
                            p.Slot = PartSlot.Neon; p.Tint = VehicleColors.ForItem(item, out var nc) ? nc : ShopData.ColorFromName(p.Name);
                            ok = true;
                            if (p.Price <= 0) p.Price = 20000;
                            break;
                    }
                    if (sub.StartsWith("DECAL_") && decalIndex.TryGetValue(p.Id, out int dIndex))
                    {
                        p.Slot = PartSlot.Decal; p.Variant = dIndex;
                        if (p.Price <= 0) p.Price = 20000;
                        ok = true;
                    }
                    if (!ok) continue;
                    if (p.Slot != PartSlot.Aero && p.Slot != PartSlot.Hood && p.Slot != PartSlot.Wing || p.Mount != null) p.Stock = false;
                    all.Add(p);
                    byId[p.Id] = p;
                }
                Debug.Log($"Drift City: {all.Count} custom shop parts");
                return all;
            }
        }

        public static PartItem Get(int id) { var _ = All; return byId.TryGetValue(id, out var p) ? p : null; }

        public static bool Fits(PartItem p, VehicleInfo car)
        {
            if (p == null || car == null) return false;
            if (p.Car != null) return p.Car == car.ModelBase;
            if (fitsCars != null && fitsCars.TryGetValue(p.Id, out var cars) && cars.Count > 0 && !cars.Contains(car.UniqueId)) return false;
            if (p.Slot == PartSlot.Decal) return CarPaint.HasDecal(car, p.Variant);
            return true;
        }

        public static IEnumerable<PartItem> For(VehicleInfo car, PartSlot slot) => All.Where(p => p.Slot == slot && Fits(p, car));

        /// <summary>The factory part for a slot, if the car has one (bumper/aero kit 1, stock hood, stock wing).</summary>
        public static PartItem Stock(VehicleInfo car, PartSlot slot)
        {
            var list = For(car, slot).ToList();
            if (slot == PartSlot.Aero) return list.FirstOrDefault(p => p.Variant == 1) ?? list.FirstOrDefault(p => p.Stock);
            if (slot == PartSlot.Hood) return list.FirstOrDefault(p => p.Stock) ?? list.FirstOrDefault(p => p.Item.ToLowerInvariant().EndsWith("_i01"));
            return list.FirstOrDefault(p => p.Stock);
        }

        /// <summary>A part being tried on in the custom shop: shown on that car only, never saved.</summary>
        public static OwnedCar PreviewCar;
        public static PartSlot PreviewSlot;
        public static int PreviewItem = -1;

        /// <summary>What is on the car in a slot: the fitted part when it is valid and owned, else the stock part.
        /// Remote players' cars (Parts == null) are trusted as sent by the server.</summary>
        public static PartItem Effective(VehicleInfo car, OwnedCar owned, PartSlot slot)
        {
            int id = owned != null ? owned.Fitted(slot) : -1;
            bool preview = owned != null && owned == PreviewCar && slot == PreviewSlot;
            if (preview) id = PreviewItem;
            var fitted = Get(id);
            bool usable = Fits(fitted, car) && fitted.Slot == slot && (preview || owned.Parts == null || owned.Parts.Contains(id));
            return usable ? fitted : Stock(car, slot);
        }

        public static (int speed, int accel, int crash, int boost) Bonus(VehicleInfo car, OwnedCar owned)
        {
            int s = 0, a = 0, c = 0, b = 0;
            foreach (PartSlot slot in System.Enum.GetValues(typeof(PartSlot)))
            {
                var p = Effective(car, owned, slot);
                if (p == null) continue;
                s += p.Speed; a += p.Accel; c += p.Crash; b += p.Boost;
            }
            return (s, a, c, b);
        }
    }
}
