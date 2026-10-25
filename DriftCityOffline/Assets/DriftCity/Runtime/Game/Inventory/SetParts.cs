using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>A set / rental part of World/Data/ItemClient.tdf (the client's parts table, 924 rows).</summary>
    public sealed class SetPartRow
    {
        public string Id, Set, Name, SetName, Counterpart, Bonus, Assist, Lube, GradeLetter;
        public int Type, Grade, Level, Points, MaxEnhance, Price, Sell, PeriodHours;
        /// <summary>"_to" rows are the expired form of a period set part: same stats or lower, no set bonus (assist n/a).</summary>
        public bool Expired => Id.EndsWith("_to");
        public bool Rental => Id.StartsWith("r_");
    }

    /// <summary>
    /// Set parts (DriftCity_RE\INVENTORY.md "Set parts"): performance parts that belong to a named set (Kayla's, Yuki's,
    /// Shinoya Alpha, Gotcha Beta, the car-rental sets...). ItemClient.tdf columns [exact]: 0 stat (speed / accel / crash /
    /// boost), 1 set id, 2 item id, 4 name, 5 set name, 6 grade letter, 7 required level, 9 stat points, 11 enhance limit,
    /// 12 price, 13 sell price, 14 the counterpart (si_x <-> si_x_to), 19 the set bonus text, 20 assist id (n/a on the "_to"
    /// rows), 21 period in hours (720 / 180 / 168 / 336, 0 = permanent), 22 restoration lube (Jew_A = Alpha Restoration
    /// Lube i_d_set_ch01, Jew_B = Beta i_d_set_ch02).
    /// The inventory shows the set on the parts page: PARTSNEW SETROW (classic) / SET_PARTS_OUTLINE1 (NDLG) around a row
    /// whose four parts form one set, and the set box INVEN 3180000 / NDLGINVEN 4870000 on hover. An expired part turns into
    /// its "_to" form ("Expired" 10795, "Can be restored using Restoration Lube" 10798); the restore box INVEN 3610000
    /// (SETACTIVATE: lube name, count, BUY / USE) brings the bonus back for another period.
    /// Remake rules (the client's server decided these): a set part from a reward is a bag stack until it is fitted; fitting
    /// turns it into a performance part whose period starts then; the bonus counts when all four stat slots of a car hold
    /// parts of one set and none is expired; its "+Np" terms (Speed / Accel / Durability / Boost) add to the stats, the rest
    /// of the text is shown only. Points: the table's points + one per enhance level per grade step. The lubes cost 100 Mito
    /// per G-Coin of their shop price (25,000 / 35,000). Mirrored in the server's src/items.js.
    /// </summary>
    public static class SetParts
    {
        static Dictionary<string, SetPartRow> rows;
        public const string AlphaLube = "i_d_set_ch01", BetaLube = "i_d_set_ch02";
        public static readonly Dictionary<string, int> LubePrice = new Dictionary<string, int> { [AlphaLube] = 25000, [BetaLube] = 35000 };

        public static IReadOnlyDictionary<string, SetPartRow> All { get { Load(); return rows; } }

        static void Load()
        {
            if (rows != null) return;
            rows = new Dictionary<string, SetPartRow>(System.StringComparer.OrdinalIgnoreCase);
            List<string[]> table = null;
            try { table = MissionTables.Read("itemclient.tdf"); } catch (System.Exception ex) { Debug.LogWarning("itemclient.tdf: " + ex.Message); }
            if (table == null) return;
            foreach (var r in table)
            {
                if (r.Length < 23) continue;
                string set = Cell(r[1]);
                if (set.Length == 0) continue;   // shop parts (i_s_000001 ...) have no set
                var row = new SetPartRow
                {
                    Type = TypeOf(r[0]), Set = set, Id = Cell(r[2]), Name = Cell(r[4]), SetName = Cell(r[5]), GradeLetter = Cell(r[6]),
                    Level = MissionTables.Int(r, 7), Points = MissionTables.Int(r, 9), MaxEnhance = MissionTables.Int(r, 11),
                    Price = MissionTables.Int(r, 12), Sell = MissionTables.Int(r, 13), Counterpart = Cell(r[14]), Bonus = Cell(r[19]),
                    Assist = Cell(r[20]), PeriodHours = MissionTables.Int(r, 21), Lube = Cell(r[22]),
                };
                row.Grade = System.Array.IndexOf(PerfRules.Grades, row.GradeLetter);
                if (row.Grade < 0) row.Grade = 2;   // "Rental" and other words: hyper-class parts
                if (row.Id.Length == 0 || row.Type < 0 || rows.ContainsKey(row.Id)) continue;
                if (row.Counterpart == "0") row.Counterpart = "";
                rows[row.Id] = row;
            }
        }

        static string Cell(string s) { s = (s ?? "").Trim(); return s == "n/a" ? "" : s; }

        static int TypeOf(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "speed": return 0;
                case "accel": return 1;
                case "crash": return 2;
                case "boost": return 3;
                default: return -1;
            }
        }

        public static SetPartRow Get(string id) { Load(); return !string.IsNullOrEmpty(id) && rows.TryGetValue(id.Trim(), out var r) ? r : null; }
        public static bool Is(string id) => Get(id) != null;
        public static SetPartRow Of(PerfPart p) => p == null ? null : Get(p.Item);

        /// <summary>The set's parts (not the expired forms), one per stat.</summary>
        public static List<SetPartRow> Members(string set)
        {
            Load();
            return rows.Values.Where(r => r.Set == set && !r.Expired).GroupBy(r => r.Type).Select(g => g.First()).OrderBy(r => r.Type).ToList();
        }

        public static string LubeItem(SetPartRow r) => r == null ? null : r.Lube == "Jew_A" ? AlphaLube : r.Lube == "Jew_B" ? BetaLube : null;

        /// <summary>Points of a set part (PerfRules.Points calls this for parts with an item id).</summary>
        public static int Points(PerfPart p)
        {
            var r = Of(p);
            if (r == null) return 0;
            return r.Points + p.Enhance * (Mathf.Clamp(p.Grade, 0, 3) + 1);
        }

        public static string PartName(PerfPart p) { var r = Of(p); return r != null ? r.Name : null; }

        // ---------------- the car's set ----------------
        /// <summary>The set the car's four stat slots hold (null when they don't form one) and whether its bonus is on.</summary>
        public static (string set, bool active, List<PerfPart> parts) CarSet(PlayerProfile p, OwnedCar car)
        {
            var parts = new List<PerfPart>();
            if (p == null || car?.PerfSlots == null) return (null, false, parts);
            string set = null;
            for (int t = 0; t < 4 && t < car.PerfSlots.Length; t++)
            {
                var part = p.PerfParts.Find(x => x.Uid == car.PerfSlots[t]);
                var r = Of(part);
                if (r == null || part.Type != t) return (null, false, parts);
                if (set == null) set = r.Set;
                else if (set != r.Set) return (null, false, parts);
                parts.Add(part);
            }
            long now = ItemUse.Now;
            bool active = set != null && parts.All(x => !Of(x).Expired && !ItemPeriods.Expired(x.Until, now));
            return (set, active, parts);
        }

        static readonly Regex BonusTerm = new Regex(@"(Speed|Acceleration|Accel\.?|Durability|Crash|Booster|Boost)\s*\+\s*(\d+)\s*[pP]\b", RegexOptions.IgnoreCase);

        /// <summary>Stat points (speed, accel, durability, booster) of a bonus text's "+Np" terms.</summary>
        public static int[] ParseBonus(string text)
        {
            var sum = new int[4];
            if (string.IsNullOrEmpty(text)) return sum;
            foreach (Match m in BonusTerm.Matches(text))
            {
                string k = m.Groups[1].Value.ToLowerInvariant();
                int t = k.StartsWith("speed") ? 0 : k.StartsWith("acc") ? 1 : k.StartsWith("dura") || k.StartsWith("crash") ? 2 : 3;
                sum[t] += int.Parse(m.Groups[2].Value);
            }
            return sum;
        }

        /// <summary>The active set bonus of a car as stat points (PerfRules.Fitted adds it).</summary>
        public static int[] Bonus(PlayerProfile p, OwnedCar car)
        {
            var (set, active, parts) = CarSet(p, car);
            if (!active || parts.Count == 0) return new int[4];
            return ParseBonus(Of(parts[0]).Bonus);
        }

        /// <summary>Every fitted part of the car whose bonus a lube can restore (expired forms of that lube's sets).</summary>
        public static List<PerfPart> Restorable(PlayerProfile p, OwnedCar car, string lube)
        {
            var list = new List<PerfPart>();
            if (p == null || car?.PerfSlots == null) return list;
            foreach (int uid in car.PerfSlots)
            {
                var part = p.PerfParts.Find(x => x.Uid == uid);
                var r = Of(part);
                if (r == null || LubeItem(r) != lube) continue;
                if (r.Expired && Get(r.Counterpart) != null) list.Add(part);
            }
            return list;
        }

        // ---------------- offline rules (the server's src/items.js does the same online) ----------------
        /// <summary>set.fit: a set part from the bag (stack) becomes a performance part fitted on the car's slot of its stat.</summary>
        public static Progress.Result FitOffline(PlayerProfile p, int slot, string id)
        {
            var r = Get(id);
            if (r == null || p.Count(id) <= 0) return new Progress.Result { Error = "You don't have that." };
            if (slot < 0 || slot >= p.Cars.Count) return new Progress.Result { Error = "No such car." };
            if (r.Level > p.Level) return new Progress.Result { Error = ItemCatalog.Text("10451", "Your level is not high enough to equip this item.") };
            if (p.PerfParts.Count >= PerfRules.MaxParts) return new Progress.Result { Error = ItemCatalog.Text("10269", "Inventory is full.") };
            p.Give(id, -1);
            var part = new PerfPart { Uid = p.NextPartUid++, Type = r.Type, Grade = r.Grade, Item = r.Id, Until = r.PeriodHours > 0 ? ItemUse.Now + r.PeriodHours * 3600_000L : 0 };
            p.PerfParts.Add(part);
            foreach (var c in p.Cars) for (int i = 0; i < c.PerfSlots.Length; i++) if (c.PerfSlots[i] == part.Uid) c.PerfSlots[i] = -1;
            p.Cars[slot].PerfSlots[r.Type] = part.Uid;
            return new Progress.Result { Ok = true };
        }

        /// <summary>set.restore: one lube brings every expired part of its sets on the car back for another period.</summary>
        public static Progress.Result RestoreOffline(PlayerProfile p, int slot, string lube)
        {
            if (slot < 0 || slot >= p.Cars.Count) return new Progress.Result { Error = "No such car." };
            if (p.Count(lube) <= 0) return new Progress.Result { Error = "You need a Restoration Lube." };
            var list = Restorable(p, p.Cars[slot], lube);
            if (list.Count == 0) return new Progress.Result { Error = "Nothing on this car needs that lube." };
            p.Give(lube, -1);
            foreach (var part in list)
            {
                var back = Get(Of(part).Counterpart);
                part.Item = back.Id;
                part.Until = ItemUse.Now + (back.PeriodHours > 0 ? back.PeriodHours : 720) * 3600_000L;
            }
            return new Progress.Result { Ok = true };
        }

        /// <summary>set.buyLube: the restore box's BUY.</summary>
        public static Progress.Result BuyLubeOffline(PlayerProfile p, string lube)
        {
            if (!LubePrice.TryGetValue(lube ?? "", out int price)) return new Progress.Result { Error = "Not sold here." };
            if (p.Mito < price) return new Progress.Result { Error = "Not enough Mito." };
            p.Mito -= price;
            p.Give(lube, 1);
            return new Progress.Result { Ok = true, Mito = -price };
        }

        static Dictionary<string, object> D(params (string k, object v)[] kv) => kv.ToDictionary(x => x.k, x => x.v);

        public static void Fit(int slot, string id, System.Action<bool> done = null)
        {
            var p = PlayerProfile.Current;
            if (p == null) { done?.Invoke(false); return; }
            if (slot == p.CurrentCar && GameFlow.BlockedByMission()) { done?.Invoke(false); return; }
            Progress.Op("set.fit", D(("slot", slot), ("id", id)), () => FitOffline(p, slot, id), r =>
            {
                if (r.Ok) { AudioLibrary.PlaySfx("sfx/SID_ITEM_INSTALL"); GameFlow.Instance?.ApplyCarStats(); }
                InventoryWindow.RefreshIfOpen();
                done?.Invoke(r.Ok);
            });
        }

        public static void Restore(int slot, string lube, System.Action<bool> done = null)
        {
            var p = PlayerProfile.Current;
            if (p == null) { done?.Invoke(false); return; }
            var names = Restorable(p, p.Cars[Mathf.Clamp(slot, 0, p.Cars.Count - 1)], lube).Select(x => Of(x).Name).ToList();
            Progress.Op("set.restore", D(("slot", slot), ("lube", lube)), () => RestoreOffline(p, slot, lube), r =>
            {
                if (r.Ok)
                {
                    GameEffects.Upgrade(GameEffects.UpgradeSuccess);   // 0x6f42b0 (10882): ui_upgrade_s
                    foreach (var n in names) Net.ChatBox.System(ItemCatalog.Text("10882", "%s has been restored.", n));
                    UiPopup.Message(ItemCatalog.Text("10883", "Restoration Successful"));
                    GameFlow.Instance?.ApplyCarStats();
                }
                InventoryWindow.RefreshIfOpen();
                done?.Invoke(r.Ok);
            });
        }

        public static void BuyLube(string lube, System.Action<bool> done = null)
        {
            var p = PlayerProfile.Current;
            if (p == null) { done?.Invoke(false); return; }
            Progress.Op("set.buyLube", D(("lube", lube)), () => BuyLubeOffline(p, lube), r =>
            {
                if (r.Ok) AudioLibrary.PlaySfx("sfx/SID_ITEM_BUY");
                InventoryWindow.RefreshIfOpen();
                done?.Invoke(r.Ok);
            });
        }
    }
}
