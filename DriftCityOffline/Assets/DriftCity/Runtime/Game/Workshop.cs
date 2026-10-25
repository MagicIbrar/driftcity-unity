using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DriftCity.Core;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// Performance parts (the parts shop): four stat types in N/S/H/U grades, enhanced with Mittron Enhancers,
    /// ground into Parts Dust, combined with Seed Parts into the next grade. The client never shipped the
    /// parts' stat table, so points, prices and odds here are this rebuild's own (mirrored in the server's
    /// economy.js); item names, recipes and crafting odds come from the game tables.
    /// </summary>
    public static class PerfRules
    {
        public static readonly string[] Types = { "Speed", "Acceleration", "Durability", "Booster" };
        public static readonly string[] Grades = { "N", "S", "H", "U" };
        public static readonly string[] GradeNames = { "Normal", "Super", "Hyper", "Ultra" };
        static readonly int[] BasePoints = { 4, 8, 14, 22 }, PointsPerLevel = { 1, 2, 3, 4 };
        public static readonly int[] ShopPrice = { 20000, 80000, 300000 };
        public static readonly int[] ReqLevel = { 1, 20, 45 };
        public const int MaxEnhance = 12, MaxParts = 60;

        public static int Points(PerfPart p) => !string.IsNullOrEmpty(p.Item) ? SetParts.Points(p) : BasePoints[p.Grade] + p.Enhance * PointsPerLevel[p.Grade];   // set parts: Inventory/SetParts.cs
        public static string Name(PerfPart p) => (SetParts.PartName(p) ?? $"{GradeNames[p.Grade]} {Types[p.Type]} part") + (p.Enhance > 0 ? $" +{p.Enhance}" : "");

        /// <summary>Success chance in percent for enhancing from the part's current level.</summary>
        public static int EnhanceChance(int level, string enhancer)
        {
            int c = level == 0 ? 100 : Mathf.Clamp(100 - 8 * level, 10, 100);
            if (enhancer == "i_n_00003_plus") c += 10;
            if (enhancer == "i_n_00003_deluxe") c += 20;
            return Mathf.Min(level == 0 ? 100 : 95, c);
        }

        public static int EnhanceFee(PerfPart p, int discountPercent) => 1000 * (p.Enhance + 1) * (1 << p.Grade) * (100 - Mathf.Clamp(discountPercent, 0, 90)) / 100;
        public static int CombineChance(bool seedPlus) => seedPlus ? 75 : 50;
        public static string Dust(int grade) => "mitrondust_" + Grades[grade];
        public static string Seed(int grade, bool plus) => (plus ? "i_n_Seedplus_" : "i_n_Seed_") + Grades[grade];

        /// <summary>The parts-shop counter (fixed prices, the same table on the server).</summary>
        public static readonly (string id, int price)[] Counter =
        {
            ("i_n_00003", 5000), ("i_n_Seed_N", 10000), ("i_n_Seed_S", 40000), ("i_n_Seed_H", 120000),
            ("user_create_kit1", 3000), ("user_create_kit2", 6000), ("user_create_kit3", 12000),
        };

        public static int SkillBookPrice(string group) => group switch { "low" => 20000, "med" => 60000, "high" => 150000, _ => 400000 };

        /// <summary>Total stat points of the performance parts fitted on a car (speed, accel, durability, booster).</summary>
        public static int[] Fitted(PlayerProfile p, OwnedCar car)
        {
            var sum = new int[4];
            if (p == null || car?.PerfSlots == null) return sum;
            for (int t = 0; t < 4 && t < car.PerfSlots.Length; t++)
            {
                var part = p.PerfParts.Find(x => x.Uid == car.PerfSlots[t]);
                if (part != null && part.Type == t) sum[t] += Points(part);
            }
            var set = SetParts.Bonus(p, car);   // a complete set's "+Np" bonus (Inventory/SetParts.cs)
            for (int t = 0; t < 4; t++) sum[t] += set[t];
            var extra = CarParts.Points(p, car);   // the NDLG second parts row and option parts (Inventory/CarParts.cs)
            for (int t = 0; t < 4; t++) sum[t] += extra[t];
            return sum;
        }
    }

    public sealed class Recipe
    {
        public string Id, Name, Group, Result, Desc;
        public int ReqLevel, ResultCount, Chance, SuccessExp, FailExp;
        public List<(string id, int count)> Inputs = new List<(string, int)>();
        public string Kit;
    }

    /// <summary>Manufacturing (init\alchemy_skillbook.xlt, alchemy_levelmodify.xlt) and item names.</summary>
    public static class Crafting
    {
        static List<Recipe> recipes;
        static long[] levelExp;
        static readonly Dictionary<string, string> names = new Dictionary<string, string>
        {
            ["i_n_00003"] = "Mittron Enhancer", ["i_n_00003_plus"] = "Mittron Enhancer PLUS", ["i_n_00003_deluxe"] = "Mittron Enhancer DELUXE",
            ["mitron_cristal"] = "Mittron Crystal", ["user_create_kit1"] = "Normal Manufacturing Kit", ["user_create_kit2"] = "Advanced Manufacturing Kit",
            ["user_create_kit3"] = "Special Manufacturing Kit", ["mitrondust_N"] = "Parts Dust N", ["mitrondust_S"] = "Parts Dust S",
            ["mitrondust_H"] = "Parts Dust H", ["mitrondust_U"] = "Parts Dust U", ["i_n_Seed_N"] = "Seed Parts N", ["i_n_Seed_S"] = "Seed Parts S",
            ["i_n_Seed_H"] = "Seed Parts H", ["i_n_Seed_U"] = "Ultra Seed Parts",
        };

        /// <summary>Materials HUV chases can drop (ingredients that no recipe or shop makes).</summary>
        public static readonly string[] ChaseDrops =
        {
            "modipart_piece", "i_n_00015", "i_n_00016", "i_n_00019", "i_n_00020", "i_n_00021", "i_n_00031", "i_n_00032", "i_n_00033",
            "i_n_00040", "i_n_00041", "i_n_00042", "i_n_00096", "i_n_warpengine", "i_n_gacha_A", "i_n_gacha_D", "i_n_00025",
            "pvp_roof_box_1", "pvp_roof_box_2", "pvp_roof_box_3",
        };

        public static string Name(string id)
        {
            var _ = Recipes;
            return names.TryGetValue(id ?? "", out var n) ? n : id;
        }

        public static List<Recipe> Recipes
        {
            get
            {
                if (recipes != null) return recipes;
                recipes = new List<Recipe>();
                var t = XltTable.Parse(DriftCityData.Files.Read(@"init\alchemy_skillbook.xlt"));
                foreach (var r in t.Rows)
                {
                    if (r.Length < 28) continue;
                    var rc = new Recipe
                    {
                        Id = r[1].Trim(), ReqLevel = Int(r[2]), Group = r[3].Trim(), Name = r[4].Trim(), Kit = r[12].Trim(),
                        Result = r[14].Trim(), ResultCount = Mathf.Max(1, Int(r[15])), Chance = Int(r[25]), SuccessExp = Int(r[26]), FailExp = Int(r[27]),
                        Desc = r[28].Trim(),
                    };
                    // event recipes need event-only keys
                    if (!rc.Kit.StartsWith("user_create_kit") || rc.Result.Length == 0) continue;
                    for (int i = 6; i <= 10; i += 2) if (r[i].Trim().Length > 0) rc.Inputs.Add((r[i].Trim(), Mathf.Max(1, Int(r[i + 1]))));
                    // names: "Item Name : X" for the result; "<How to obtain>" lines name the inputs and kit in order
                    var text = string.Join("\n", r.Skip(28));
                    var m = Regex.Match(text, @"Item Name\s*:\s*(?:</COLOR>)?\s*([^<\n|]+)");
                    if (m.Success && !names.ContainsKey(rc.Result)) names[rc.Result] = m.Groups[1].Value.Trim();
                    var how = text.IndexOf("<How", System.StringComparison.Ordinal);
                    if (how >= 0)
                    {
                        var ids = rc.Inputs.Select(x => x.id).Append(rc.Kit).ToList();
                        int k = 0;
                        foreach (var line in text.Substring(how).Split('\n'))
                        {
                            var lm = Regex.Match(line, @"^\s*([^:<>|]+?)\s*:\s*\S");
                            if (!lm.Success || k >= ids.Count) continue;
                            if (!names.ContainsKey(ids[k])) names[ids[k]] = lm.Groups[1].Value.Trim();
                            k++;
                        }
                    }
                    recipes.Add(rc);
                }
                return recipes;
            }
        }

        static int Int(string s) => int.TryParse(s?.Trim(), out int v) ? v : 0;

        /// <summary>Manufacturing level from crafting EXP (alchemy_levelmodify EXP_MAX per level).</summary>
        public static int Level(long exp)
        {
            LoadLevels();
            int lv = 1;
            foreach (var max in levelExp) if (exp >= max) lv++;
            return Mathf.Min(lv, levelExp.Length);
        }

        static void LoadLevels()
        {
            if (levelExp != null) return;
            var t = XltTable.Parse(DriftCityData.Files.Read(@"init\alchemy_levelmodify.xlt"));
            levelExp = t.Rows.Select(r => (long)t.GetInt(r, "EXP_MAX")).Where(x => x > 0).ToArray();
        }

        /// <summary>The EXP span of the level <paramref name="exp"/> falls in (the manufacturing gauge); lo == hi past the last level.</summary>
        public static (long lo, long hi) LevelSpan(long exp)
        {
            LoadLevels();
            long lo = 0;
            foreach (var max in levelExp) { if (exp < max) return (lo, max); lo = max; }
            return (lo, lo);
        }

        /// <summary>What a crafted item does when used here (null = it's a material).</summary>
        public static string UseText(string id) => id switch
        {
            "i_n_upgrade" => "Raises your current car one grade for free",
            "i_n_roof_jackbox" => "Gives your current car the Jack O' Lantern roof item",
            "i_n_roof_xcatbox" => "Gives your current car the X-Cat roof item",
            "i_bacchus_f" => "Drink: +2,000 EXP",
            "i_bacchus_d" => "Drink: +6,000 EXP",
            _ => null,
        };
    }

    public sealed class Licence
    {
        public int Id;
        /// <summary>LicenseIDName (l_ladder_01...), also the licence's name in the client's icon table.</summary>
        public string IdName;
        public string Category, Name, Info, Gain, EffectText, Effect;
        public int Value;
        public bool AnyOne;
        public List<(string stat, int value)> Needs = new List<(string, int)>();
        public bool Obtainable => Needs.Count > 0;
    }

    /// <summary>Licences (init\license.xlt): earned from tracked achievements; one is mounted for its bonus.</summary>
    public static class Licences
    {
        static List<Licence> all;

        /// <summary>Gain-condition ids that this rebuild tracks, mapped to profile counters.</summary>
        public static int Stat(PlayerProfile p, string stat)
        {
            if (stat.StartsWith("Licence_")) return int.TryParse(stat.Substring(8), out int id) && p.Licences.Contains(id) ? 1 : 0;
            return stat switch
            {
                "HuvCaught" => p.HuvCaught, "RacesEntered" => p.RacesEntered, "RacesWon" => p.RacesWon, "Quests" => p.CompletedQuests.Count,
                "RacePoints" => p.RacePoints, "RacesCras" => p.RacesCras, "RacesMoon" => p.RacesMoon, "RacesKoi" => p.RacesKoi,
                "RacesOros" => p.RacesOros, "MaxEnhance" => p.MaxEnhance, _ => 0,
            };
        }

        static string MapCondition(string gc, out int scale)
        {
            scale = 1;
            if (gc.StartsWith("GC_LicenseGain_")) return "Licence_" + gc.Substring(15);
            if (gc.StartsWith("GC_LadderPoint_")) return "RacePoints";
            return gc switch
            {
                "GC_ChasingClear" => "HuvCaught",
                "GC_BattlePersonal" => "RacesEntered",
                "GC_BattleTrackMoon" => "RacesMoon", "GC_BattleTrackKoi" => "RacesKoi", "GC_BattleTrackCras" => "RacesCras", "GC_BattleTrackOros" => "RacesOros",
                "GC_MQuestClear" => "Quests",
                "GC_PartsEnhanceX" => "MaxEnhance", "GC_PartsEnhanceXII" => "MaxEnhance",
                _ => null,
            };
        }

        public static List<Licence> All
        {
            get
            {
                if (all != null) return all;
                all = new List<Licence>();
                var t = XltTable.Parse(DriftCityData.Files.Read(@"init\license.xlt"));
                foreach (var r in t.Rows)
                {
                    if (t.Get(r, "Support") != "1" || t.Get(r, "Visible") == "0") continue;
                    var l = new Licence
                    {
                        Id = t.GetInt(r, "LicenseID"), IdName = t.Get(r, "LicenseIDName")?.Trim(), Category = t.Get(r, "CategoryID"), Name = t.Get(r, "LicenseName"), Info = t.Get(r, "LicenseInfo"),
                        Gain = t.Get(r, "Postfix"), EffectText = t.Get(r, "MountEffectText"), Effect = t.Get(r, "MountEffectID"), Value = t.GetInt(r, "MountEffectValue"),
                    };
                    l.AnyOne = (t.Get(r, "GainConditionText") + l.Gain).Contains("any one");
                    for (int i = 1; i <= 12; i++)
                    {
                        var c = t.Get(r, "Condition_" + i);
                        if (string.IsNullOrEmpty(c) || c == "n/a") continue;
                        var parts = c.Split(':');
                        var stat = MapCondition(parts[0], out _);
                        if (stat == null) continue;   // a mode this rebuild doesn't have: the tracked conditions decide
                        int.TryParse(parts.Length > 1 ? parts[1] : "1", out int v);
                        if (parts[0] == "GC_PartsEnhanceX") v = 10;
                        if (parts[0] == "GC_PartsEnhanceXII") v = 12;
                        l.Needs.Add((stat, Mathf.Max(1, v)));
                    }
                    // the same counter listed per arena/track collapses to one requirement
                    l.Needs = l.Needs.GroupBy(n => n.stat).Select(g => (g.Key, l.AnyOne ? g.Min(n => n.value) : g.Max(n => n.value))).ToList();
                    all.Add(l);
                }
                return all;
            }
        }

        public static Licence Get(int id) => All.FirstOrDefault(l => l.Id == id);

        public static bool Met(PlayerProfile p, Licence l) =>
            l.Obtainable && (l.AnyOne ? l.Needs.Any(n => Stat(p, n.stat) >= n.value) : l.Needs.All(n => Stat(p, n.stat) >= n.value));

        /// <summary>Bonus percent of the mounted licence for an effect id (e.g. ME_ChasingExpMito).</summary>
        public static int Bonus(PlayerProfile p, params string[] effects)
        {
            var l = p != null && p.MountedLicence >= 0 && p.Licences.Contains(p.MountedLicence) ? Get(p.MountedLicence) : null;
            return l != null && effects.Contains(l.Effect) ? Mathf.Clamp(l.Value, 0, 20) : 0;
        }

        public static string TrackEffect(string city) => city switch
        {
            "moonpalace" => "ME_MoonTrackExp", "koinonia" => "ME_KoiTrackExp", "cras" => "ME_CrasTrackExp", "oros" => "ME_OrosTrackExp", _ => "",
        };
    }
}
