using System.Collections.Generic;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>A row of World/Data/Assist_Client.tdf (5,446 rows): grade, id, kind name, name, description, value, unit.</summary>
    public sealed class AssistRow
    {
        public string Id, Key, Name, Desc, Unit;
        public float Value;
        /// <summary>The client's effect number for <see cref="Key"/> (0x6c58e0: "stop" 1, "qstart" 2 ... "expmito" 0x53), 0 = unknown.</summary>
        public int Kind;
    }

    /// <summary>
    /// Assist options ("skill items"; DriftCity_RE/PHYSICS.md section 13). Option parts (ItemClient.tdf op_S / op_A / op_C /
    /// op_B / op_F), docking parts (dock_*) and set parts carry an assist id (column 20) that points into Assist_Client.tdf.
    /// The client maps the row's kind name to an effect number (0x6c58e0) and 0x6d2580 writes the value into the car's
    /// dynamics object; 0x6dac70 resets those fields before the car's assists are applied again (0x6dbc40). This class is
    /// that set of fields: <see cref="DriftCar"/> reads it in its step, the fuel tank its tank / mileage percentages, the
    /// performance table its rating points.
    /// </summary>
    public sealed class CarAssists
    {
        // ---- the dynamics fields 0x6d2580 writes (byte offsets of the 0x1c70-byte XiDynamicControl object) ----
        /// <summary>0x918 "stop": Turbo Stop (drift + brake while moving).</summary>
        public bool TurboStop;
        /// <summary>0x914 "qstart" / "AL_QS": Turbo Start (drift + forward while stopped).</summary>
        public bool TurboStart;
        /// <summary>0x908 "criticalcrash": HUV critical hit chance, percent (read by the chase damage 0x47c6f0).</summary>
        public float CriticalChance;
        /// <summary>0x91c "untouchable": no speed loss from hits while boosting.</summary>
        public bool Untouchable;
        /// <summary>0x924 "crashsaving" / "BA_CS": multiplier on the wall speed loss, 1 - percent / 100.</summary>
        public float CrashLoss = 1f;
        /// <summary>0x928 "overturbo" / "BA_BC": seconds added to a full boost.</summary>
        public float BoostTimeAdd;
        /// <summary>0x92c "bonusreal": reverse torque multiplier - 1.</summary>
        public float ReverseAccel;
        /// <summary>0x930 "bonusdamage" / "CH_DA": HUV damage multiplier - 1.</summary>
        public float HuvDamage;
        /// <summary>0x934 "driftbooster": the post-drift booster.</summary>
        public bool DriftBooster;
        /// <summary>0x940 "field_b_r" (+ "booter_b_r"): the city passive gauge fill multiplier.</summary>
        public float FieldFill = 1f;
        /// <summary>0x93c "field_c_fb": percent chance of a full gauge when hitting traffic in the city.</summary>
        public float FieldCrashFull;
        /// <summary>0x944 "field_b_fr": percent chance of a full gauge again when a boost ends (city).</summary>
        public float FieldBoostRefill;
        /// <summary>0x948 "battle_c_b": gauge added when hitting traffic in the Battle Zone.</summary>
        public float BattleCrashBoost;
        /// <summary>0x94c "battle_b_r" / "bbattle_b_r" (+ "booter_b_r"): the Battle Zone passive fill multiplier.</summary>
        public float BattleFill = 1f;
        /// <summary>0x950 "battle_c_fb": percent chance of a full gauge when hitting traffic in the Battle Zone.</summary>
        public float BattleCrashFull;
        /// <summary>0x954 "battle_c_fr": percent chance of a full repair when hitting traffic in the Battle Zone.</summary>
        public float BattleCrashRepair;
        /// <summary>0x958 "BA_MS" / "BA_NS": Battle Zone top speed, km/h (0x476f90 x 0.8 for the Drift City service).</summary>
        public float BattleSpeedKmh;
        /// <summary>0x95c "BA_MS" / "BA_BM": Battle Zone boost top speed, km/h.</summary>
        public float BattleBoostKmh;
        /// <summary>0x974 "BA_CB": Battle Zone fill per battle combo.</summary>
        public float BattleComboFill;
        /// <summary>0x978 "CH_BT": chase / city fill multiplier (enters the city fill as value - 1).</summary>
        public float ChaseFill = 1f;
        /// <summary>0x968 "CH_AB": gauge added when hitting an HUV.</summary>
        public int HuvHitBoost;

        // ---- adds that come through the car's other fields ----
        /// <summary>"speedup" + "partyspeedup": km/h on the top speed (0x47fde0: car +0x920 = km/h / 3.6 x 0.8).</summary>
        public float SpeedKmh;
        /// <summary>"gage": percent on the Mittron tank capacity (0x47fde0 / 0x480050: (100 + pct) / 100 x the car's litres).</summary>
        public float TankPercent;
        /// <summary>"saving": percent on the mileage (km per litre).</summary>
        public float MileagePercent;
        /// <summary>"endurance": acceleration points while the tank is low.</summary>
        public int LowFuelAccel;
        /// <summary>"bonusspeed" / "bonusaccel" / "bonuscrash" / "bonusboost": rating points (speed, accel, durability, boost).</summary>
        public readonly int[] Points = new int[4];
        /// <summary>"exp" / "mito" / "expmito" / "chance": reward percentages (shown; the reward rules are the server's).</summary>
        public float ExpPercent, MitoPercent, DropPercent;

        /// <summary>The rows that made this set, for the performance table and tooltips.</summary>
        public readonly List<AssistRow> Rows = new List<AssistRow>();

        public bool Any => Rows.Count > 0;

        /// <summary>One assist row into the fields, as 0x6d2580 (percent values divided by the car's 100, 0x15c).</summary>
        public void Add(AssistRow r)
        {
            if (r == null) return;
            Rows.Add(r);
            float v = r.Value, pct = r.Value / 100f;
            switch (r.Kind)
            {
                case 1: TurboStop = true; break;
                case 2: case 0x50: TurboStart = true; break;
                case 3: CriticalChance = v; break;
                case 4: MileagePercent += v; break;
                case 5: TankPercent += v; break;
                case 6: LowFuelAccel += Mathf.RoundToInt(v); break;
                case 7: Untouchable = true; break;
                case 9: Points[0] += Mathf.RoundToInt(v); break;
                case 10: Points[1] += Mathf.RoundToInt(v); break;
                case 0xb: Points[2] += Mathf.RoundToInt(v); break;
                case 0xc: Points[3] += Mathf.RoundToInt(v); break;
                case 0xd: case 0x51: DropPercent += v; break;
                case 0xe: case 0x10: SpeedKmh += v; break;
                case 0xf: case 0x27: CrashLoss -= pct; break;
                case 0x11: ExpPercent += v; break;
                case 0x12: case 0x28: BoostTimeAdd += v; break;
                case 0x13: ReverseAccel += pct; break;
                case 0x14: case 0x2f: HuvDamage += pct; break;
                case 0x16: DriftBooster = true; break;
                case 0x17: BattleCrashBoost = v; break;
                case 0x18: case 0x22: BattleFill += pct; break;
                case 0x19: BattleCrashFull = v; break;
                case 0x1a: BattleCrashRepair = v; break;
                case 0x1c: FieldCrashFull = v; break;
                case 0x1d: FieldFill += pct; break;
                case 0x1e: FieldBoostRefill = v; break;
                case 0x21: BattleFill += pct; FieldFill += pct; break;
                case 0x29: BattleSpeedKmh += v; BattleBoostKmh += v; break;
                case 0x36: BattleComboFill += v; break;
                case 0x37: BattleBoostKmh += v; break;
                case 0x42: HuvHitBoost += Mathf.RoundToInt(v); break;
                case 0x48: ChaseFill += pct; break;
                case 0x4f: BattleSpeedKmh += v; break;
                case 0x52: MitoPercent += v; break;
                case 0x53: ExpPercent += v; MitoPercent += v; break;
            }
            CrashLoss = Mathf.Max(0f, CrashLoss);
        }

        /// <summary>The assists of a car: its option parts, docking parts and an active set's parts (Inventory/CarParts.cs).</summary>
        public static CarAssists For(PlayerProfile p, OwnedCar car)
        {
            var a = new CarAssists();
            if (p == null || car == null) return a;
            foreach (var id in CarParts.AssistIds(p, car)) a.Add(AssistTable.Get(id));
            return a;
        }

        /// <summary>The player's current car.</summary>
        public static CarAssists Current => For(PlayerProfile.Current, PlayerProfile.Current?.Car);
    }

    /// <summary>World/Data/Assist_Client.tdf and the client's kind numbers (0x6c58e0).</summary>
    public static class AssistTable
    {
        static Dictionary<string, AssistRow> rows;

        /// <summary>0x6c58e0: the assist kind names in the order the client numbers them.</summary>
        static readonly Dictionary<string, int> Kinds = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["stop"] = 1, ["qstart"] = 2, ["criticalcrash"] = 3, ["saving"] = 4, ["gage"] = 5, ["endurance"] = 6, ["untouchable"] = 7,
            ["partycrashup"] = 8, ["bonusspeed"] = 9, ["bonusaccel"] = 10, ["bonuscrash"] = 0xb, ["bonusboost"] = 0xc, ["chance"] = 0xd,
            ["speedup"] = 0xe, ["crashsaving"] = 0xf, ["partyspeedup"] = 0x10, ["exp"] = 0x11, ["overturbo"] = 0x12, ["bonusreal"] = 0x13,
            ["bonusdamage"] = 0x14, ["bonustime"] = 0x15, ["driftbooster"] = 0x16, ["battle_c_b"] = 0x17, ["battle_b_r"] = 0x18,
            ["battle_c_fb"] = 0x19, ["battle_c_fr"] = 0x1a, ["field_h_d"] = 0x1b, ["field_c_fb"] = 0x1c, ["field_b_r"] = 0x1d,
            ["field_b_fr"] = 0x1e, ["docking_b"] = 0x1f, ["docking_p"] = 0x20, ["booter_b_r"] = 0x21, ["bbattle_b_r"] = 0x22,
            ["BA_CS"] = 0x27, ["BA_BC"] = 0x28, ["BA_MS"] = 0x29, ["CH_DA"] = 0x2f, ["BA_NS"] = 0x4f, ["BA_CA"] = 0x35, ["BA_CB"] = 0x36,
            ["BA_BM"] = 0x37, ["CH_AD"] = 0x41, ["CH_AB"] = 0x42, ["CH_CC"] = 0x45, ["CH_BT"] = 0x48, ["CH_SD"] = 0x4b, ["AL_QS"] = 0x50,
            ["pluschance"] = 0x51, ["mito"] = 0x52, ["expmito"] = 0x53,
        };

        public static int KindOf(string key) => key != null && Kinds.TryGetValue(key.Trim(), out int k) ? k : 0;

        public static IReadOnlyDictionary<string, AssistRow> All { get { Load(); return rows; } }

        public static AssistRow Get(string id)
        {
            if (string.IsNullOrEmpty(id) || id == "n/a" || id == "0") return null;
            Load();
            return rows.TryGetValue(id.Trim(), out var r) ? r : null;
        }

        /// <summary>Adds a row by hand (tests, data without the table).</summary>
        public static void Define(AssistRow r) { Load(); if (r?.Id != null) { r.Kind = r.Kind != 0 ? r.Kind : KindOf(r.Key); rows[r.Id] = r; } }

        static void Load()
        {
            if (rows != null) return;
            rows = new Dictionary<string, AssistRow>(System.StringComparer.OrdinalIgnoreCase);
            List<string[]> table = null;
            try { table = MissionTables.Read("assist_client.tdf"); } catch (System.Exception ex) { Debug.LogWarning("assist_client.tdf: " + ex.Message); }
            if (table == null) return;
            foreach (var r in table)
            {
                if (r.Length < 7) continue;
                string id = (r[1] ?? "").Trim();
                if (id.Length == 0 || rows.ContainsKey(id)) continue;
                var row = new AssistRow
                {
                    Id = id, Key = (r[2] ?? "").Trim(), Name = (r[3] ?? "").Trim(), Desc = (r[4] ?? "").Trim(),
                    Value = MissionTables.Float(r, 5), Unit = (r[6] ?? "").Trim(),
                };
                row.Kind = KindOf(row.Key);
                rows[id] = row;
            }
        }
    }
}
