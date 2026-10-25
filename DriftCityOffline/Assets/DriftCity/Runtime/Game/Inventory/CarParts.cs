using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>An option or docking part of World/Data/ItemClient.tdf (op_S / op_A / op_C / op_B / op_F, dock_S / A / C / B / D).</summary>
    public sealed class OptionPartRow
    {
        public string Id, Kind, Name, GradeLetter, Desc, Assist;
        public int Level, Points, Price, Sell;
        /// <summary>The stat it docks onto: 0 speed, 1 accel, 2 durability, 3 boost, -1 any (op_F, dock_D).</summary>
        public int Stat;
        public bool Docking => Kind.StartsWith("dock_");
    }

    /// <summary>
    /// The NDLG parts page's second row and its option slots (NDLGINVEN 4810000, DriftCity_RE/INVENTORY.md "Parts page"):
    /// eight part slots, two per stat (SLOTN_01/03/05/07 row 1 and 02/04/06/08 row 2), each with an option slot (OPTSLOTN /
    /// OPTSLOT_nn, the GOPSLOT grade frame) that takes an option part of ItemClient.tdf: the op_* rows carry stat points
    /// (column 9, "Acceleration Points +3") or an assist (column 20: Turbo Launch a_00002, Booster Extension a_00048, Engine
    /// Speeder a_00031 ...), the dock_* rows an assist that improves the part they sit on ("docking_b" +N points,
    /// "docking_p" +N % of the part's points). Texts: "Equippable on N Parts" 10923-10930, option / docking part names
    /// 10918-10922 / 11005-11008. [remake rules] (the client's server kept the slots): row 2 holds a second performance part
    /// of the column's stat whose points add like row 1; an option part needs a fitted part under it, takes the stat of
    /// its letter (S / A / C / B, F and D fit anywhere), is a bag stack until fitted and goes back to the bag when removed
    /// or when a row-2 part under it is taken off (on row 1 it stays on the slot, inactive while the slot is empty).
    /// Mirrored in the server's src/carparts.js.
    /// </summary>
    public static class CarParts
    {
        public const int Slots = 8;
        static Dictionary<string, OptionPartRow> rows;
        static PlayerProfile P => PlayerProfile.Current;

        public static IReadOnlyDictionary<string, OptionPartRow> All { get { Load(); return rows; } }

        public static OptionPartRow Option(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            Load();
            return rows.TryGetValue(id, out var r) ? r : null;
        }

        public static bool IsOption(string id) => Option(id) != null;

        static void Load()
        {
            if (rows != null) return;
            rows = new Dictionary<string, OptionPartRow>(System.StringComparer.OrdinalIgnoreCase);
            List<string[]> table = null;
            try { table = MissionTables.Read("itemclient.tdf"); } catch (System.Exception ex) { Debug.LogWarning("itemclient.tdf: " + ex.Message); }
            if (table == null) return;
            foreach (var r in table)
            {
                if (r.Length < 21) continue;
                string kind = (r[0] ?? "").Trim();
                if (!kind.StartsWith("op_") && !kind.StartsWith("dock_")) continue;
                var row = new OptionPartRow
                {
                    Kind = kind, Id = (r[2] ?? "").Trim(), Name = Cell(r[4]), GradeLetter = Cell(r[6]), Level = MissionTables.Int(r, 7),
                    Points = MissionTables.Int(r, 9), Price = MissionTables.Int(r, 12), Sell = (int)MissionTables.Float(r, 13),
                    Desc = ItemCatalog.Clean(Cell(r[19])), Assist = Cell(r[20]),
                };
                row.Stat = StatOf(kind);
                if (row.Id.Length > 0 && !rows.ContainsKey(row.Id)) rows[row.Id] = row;
            }
        }

        /// <summary>Adds a row by hand (tests).</summary>
        public static void Define(OptionPartRow r) { Load(); if (r?.Id != null) { if (r.Kind != null) r.Stat = StatOf(r.Kind); rows[r.Id] = r; } }

        static string Cell(string s) { s = (s ?? "").Trim(); return s == "n/a" || s == "0" ? "" : s; }

        static int StatOf(string kind)
        {
            char c = kind.Length > 0 ? char.ToUpperInvariant(kind[kind.Length - 1]) : 'F';
            return c == 'S' ? 0 : c == 'A' ? 1 : c == 'C' ? 2 : c == 'B' ? 3 : -1;
        }

        /// <summary>Slot index (0..7) of a stat and row; the NDLG control number is index + 1 in its own order (stat * 2 + row + 1).</summary>
        public static int Index(int stat, int row) => stat * 2 + row;

        static void Normalize(OwnedCar car)
        {
            if (car == null) return;
            if (car.PerfSlots2 == null || car.PerfSlots2.Length != 4) car.PerfSlots2 = new[] { -1, -1, -1, -1 };
            if (car.OptSlots == null || car.OptSlots.Length != Slots) car.OptSlots = Enumerable.Repeat("", Slots).ToArray();
            for (int i = 0; i < Slots; i++) car.OptSlots[i] ??= "";
        }

        /// <summary>The performance part uid in a slot (row 0 = PerfSlots, row 1 = PerfSlots2), -1 = empty.</summary>
        public static int PartUid(OwnedCar car, int stat, int row)
        {
            if (car == null || stat < 0 || stat > 3) return -1;
            Normalize(car);
            var arr = row == 0 ? car.PerfSlots : car.PerfSlots2;
            return arr != null && stat < arr.Length ? arr[stat] : -1;
        }

        public static PerfPart Part(PlayerProfile p, OwnedCar car, int stat, int row)
        {
            int uid = PartUid(car, stat, row);
            return uid >= 0 && p != null ? p.PerfParts.Find(x => x.Uid == uid) : null;
        }

        public static string OptionIn(OwnedCar car, int index)
        {
            if (car == null || index < 0 || index >= Slots) return "";
            Normalize(car);
            return car.OptSlots[index] ?? "";
        }

        /// <summary>Every uid fitted in a second-row slot of any car (the bag leaves them out).</summary>
        public static IEnumerable<int> SecondRowUids(PlayerProfile p) =>
            p == null ? Enumerable.Empty<int>() : p.Cars.Where(c => c.PerfSlots2 != null).SelectMany(c => c.PerfSlots2).Where(u => u >= 0);

        /// <summary>
        /// Stat points the second row and the option slots add (speed, accel, durability, boost): row-2 parts, option parts'
        /// points, docking bonuses on the part under them, and the assists' bonus points (bonusspeed ... from option parts).
        /// PerfRules.Fitted adds these to row 1 and the set bonus.
        /// </summary>
        public static int[] Points(PlayerProfile p, OwnedCar car)
        {
            var sum = new int[4];
            if (p == null || car == null) return sum;
            Normalize(car);
            for (int t = 0; t < 4; t++)
            {
                var part = Part(p, car, t, 1);
                if (part != null && part.Type == t) sum[t] += PerfRules.Points(part);
            }
            for (int i = 0; i < Slots; i++)
            {
                var o = Option(car.OptSlots[i]);
                if (o == null) continue;
                int stat = i / 2, row = i % 2;
                var under = Part(p, car, stat, row);
                if (under == null) continue;
                sum[stat] += o.Points;
                var a = AssistTable.Get(o.Assist);
                if (a == null) continue;
                if (a.Kind == 0x1f) sum[stat] += Mathf.RoundToInt(a.Value);                                  // docking_b: +N points
                else if (a.Kind == 0x20) sum[stat] += Mathf.RoundToInt(PerfRules.Points(under) * a.Value / 100f);   // docking_p: +N %
                else if (a.Kind >= 9 && a.Kind <= 0xc) sum[a.Kind - 9] += Mathf.RoundToInt(a.Value);           // bonusspeed .. bonusboost
            }
            return sum;
        }

        /// <summary>The assist ids active on a car: option parts on fitted parts (not the docking ones, whose effect is points),
        /// and the parts of a complete, running set (their "+Np" rating terms are SetParts.Bonus already, so those are left out).</summary>
        public static IEnumerable<string> AssistIds(PlayerProfile p, OwnedCar car)
        {
            if (p == null || car == null) yield break;
            Normalize(car);
            for (int i = 0; i < Slots; i++)
            {
                var o = Option(car.OptSlots[i]);
                if (o == null || string.IsNullOrEmpty(o.Assist) || Part(p, car, i / 2, i % 2) == null) continue;
                var a = AssistTable.Get(o.Assist);
                if (a == null || a.Kind == 0x1f || a.Kind == 0x20 || (a.Kind >= 9 && a.Kind <= 0xc)) continue;
                yield return o.Assist;
            }
            var (set, active, parts) = SetParts.CarSet(p, car);
            if (set == null || !active) yield break;
            foreach (var part in parts)
            {
                var row = SetParts.Of(part);
                var a = row != null ? AssistTable.Get(row.Assist) : null;
                if (a == null || (a.Kind >= 9 && a.Kind <= 0xc)) continue;
                yield return row.Assist;
            }
        }

        // ================= fitting (online: the server's carparts.js; offline the same rules here) =================
        static Progress.Result Fail(string e) => new Progress.Result { Error = e };

        /// <summary>Puts a performance part from the bag into a car's second-row slot of its stat.</summary>
        public static void FitRow2(int carSlot, int uid, System.Action<Progress.Result> done = null)
        {
            Progress.Op("parts.fit2", new Dictionary<string, object> { ["slot"] = carSlot, ["uid"] = uid }, () => FitRow2Rule(P, carSlot, uid), r => { Changed(); done?.Invoke(r); });
        }

        public static Progress.Result FitRow2Rule(PlayerProfile p, int carSlot, int uid)
        {
            if (p == null || carSlot < 0 || carSlot >= p.Cars.Count) return Fail("No such car.");
            var car = p.Cars[carSlot];
            string rentRefusal = Services.RentalCars.UpgradeRefusal(car);
            if (rentRefusal != null) return Fail(rentRefusal);   // rental cars take no parts (10952)
            Normalize(car);
            var part = p.PerfParts.Find(x => x.Uid == uid);
            if (part == null) return Fail(ItemCatalog.Text("10933", "You cannot equip this part."));
            if (ItemPeriods.IsExpired(new BagEntry { Kind = BagKind.Part, Part = part })) return Fail(ItemCatalog.Text("10454", "This item has already expired."));
            if (p.Cars.Any(c => (c.PerfSlots != null && c.PerfSlots.Contains(uid)) || (c.PerfSlots2 != null && c.PerfSlots2.Contains(uid)))) return Fail("That part is already fitted.");
            int old = car.PerfSlots2[part.Type];
            car.PerfSlots2[part.Type] = uid;
            if (old >= 0) ReturnOption(p, car, Index(part.Type, 1));
            return new Progress.Result { Ok = true };
        }

        /// <summary>Takes the second-row part of a stat off (its option part goes back to the bag).</summary>
        public static void UnfitRow2(int carSlot, int stat, System.Action<Progress.Result> done = null)
        {
            Progress.Op("parts.unfit2", new Dictionary<string, object> { ["slot"] = carSlot, ["type"] = stat }, () => UnfitRow2Rule(P, carSlot, stat), r => { Changed(); done?.Invoke(r); });
        }

        public static Progress.Result UnfitRow2Rule(PlayerProfile p, int carSlot, int stat)
        {
            if (p == null || carSlot < 0 || carSlot >= p.Cars.Count || stat < 0 || stat > 3) return Fail("No such slot.");
            var car = p.Cars[carSlot];
            Normalize(car);
            if (car.PerfSlots2[stat] < 0) return Fail("That slot is empty.");
            car.PerfSlots2[stat] = -1;
            ReturnOption(p, car, Index(stat, 1));
            return new Progress.Result { Ok = true };
        }

        /// <summary>Docks an option part (a bag stack) onto a slot's performance part.</summary>
        public static void FitOption(int carSlot, int index, string id, System.Action<Progress.Result> done = null)
        {
            Progress.Op("opt.fit", new Dictionary<string, object> { ["slot"] = carSlot, ["index"] = index, ["id"] = id }, () => FitOptionRule(P, carSlot, index, id), r => { Changed(); done?.Invoke(r); });
        }

        public static Progress.Result FitOptionRule(PlayerProfile p, int carSlot, int index, string id)
        {
            if (p == null || carSlot < 0 || carSlot >= p.Cars.Count || index < 0 || index >= Slots) return Fail("No such slot.");
            var car = p.Cars[carSlot];
            string rentRefusal = Services.RentalCars.UpgradeRefusal(car);
            if (rentRefusal != null) return Fail(rentRefusal);   // rental cars take no parts (10952)
            Normalize(car);
            var o = Option(id);
            if (o == null) return Fail(ItemCatalog.Text("10449", "This item is not equippable."));
            if (p.Count(id) <= 0) return Fail("You don't have that.");
            if (o.Level > p.Level) return Fail(ItemCatalog.Text("10451", "Your level is too low to use this item."));
            int stat = index / 2;
            if (o.Stat >= 0 && o.Stat != stat) return Fail(ItemCatalog.Text("10933", "You cannot equip this part."));
            if (Part(p, car, stat, index % 2) == null) return Fail("Fit a performance part in this slot first.");
            ReturnOption(p, car, index);
            p.Give(id, -1);
            car.OptSlots[index] = id;
            return new Progress.Result { Ok = true };
        }

        /// <summary>Takes an option part off (back to the bag).</summary>
        public static void RemoveOption(int carSlot, int index, System.Action<Progress.Result> done = null)
        {
            Progress.Op("opt.remove", new Dictionary<string, object> { ["slot"] = carSlot, ["index"] = index }, () => RemoveOptionRule(P, carSlot, index), r => { Changed(); done?.Invoke(r); });
        }

        public static Progress.Result RemoveOptionRule(PlayerProfile p, int carSlot, int index)
        {
            if (p == null || carSlot < 0 || carSlot >= p.Cars.Count || index < 0 || index >= Slots) return Fail("No such slot.");
            var car = p.Cars[carSlot];
            Normalize(car);
            if (string.IsNullOrEmpty(car.OptSlots[index])) return Fail("That slot is empty.");
            ReturnOption(p, car, index);
            return new Progress.Result { Ok = true };
        }

        static void ReturnOption(PlayerProfile p, OwnedCar car, int index)
        {
            string id = car.OptSlots[index];
            if (string.IsNullOrEmpty(id)) return;
            p.Give(id);
            car.OptSlots[index] = "";
        }

        /// <summary>Row 1 lost its part (taken off / replaced): its option part returns to the bag (call from the row-1 code).</summary>
        public static void Row1Changed(PlayerProfile p, OwnedCar car, int stat)
        {
            if (p == null || car == null || stat < 0 || stat > 3) return;
            Normalize(car);
            if (car.PerfSlots[stat] < 0) ReturnOption(p, car, Index(stat, 0));
        }

        /// <summary>Raised after a fit / unfit so the car's handling and the windows refresh.</summary>
        public static event System.Action Changed_;
        static void Changed()
        {
            GameFlow.Instance?.ApplyCarStats();
            try { Changed_?.Invoke(); } catch (System.Exception ex) { Debug.LogException(ex); }
        }
    }
}
