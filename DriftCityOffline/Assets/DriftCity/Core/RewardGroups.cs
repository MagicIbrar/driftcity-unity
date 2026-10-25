// init\rewardgroup.xlt: the client's reward pools ("rw_xmas_giftbox", "lotto_Natura", "rw_gacha_ob_v3", ...).
// Columns: Idx | GroupId | Group_Description | Reward_Type | Reward_Description | ItemId | Reward_Period | Reward_Count |
// Etc_Value | Etc_Str | Probability. Reward_Type is NormalItem (useitemclient item), SkidItem (vshopitem item), RewardGroup
// (a sub-pool), Exp / Mito (amount in Reward_Count), Recycle, or n/a (the "lotto_00001" Bust card).
// The client only reads the table to list what a pool can give (gacha shop 0x63b9b0); the official server drew from it.
// This rebuild draws with one rule (DriftCity_RE\EVENTS.md, mirrored by the server's src/events.js):
//  - rows with Probability <= 0 never drop;
//  - a pool whose rows are all at 100 gives every row (gift boxes: the car and its trial part; scratch cards: one
//    prize per column), except a pool of sub-pools with rising Etc_Value, which is a level band (rw_rq_koi: the first
//    band whose Etc_Value >= the driver's level);
//  - any other pool gives one row, drawn by Probability as a weight.
using System;
using System.Collections.Generic;

namespace DriftCity.Core
{
    public sealed class RewardRow
    {
        public string Group, Type, ItemId, EtcStr;
        public int Period, Count, Etc;
        public float Probability;
    }

    public enum GrantKind { Item, Exp, Mito, Bust }

    /// <summary>One thing a draw gives: an item stack, EXP, Mito, or the Bust card (nothing).</summary>
    public sealed class RewardGrant
    {
        public GrantKind Kind;
        public string ItemId;      // Item / Bust
        public int Count;          // item count, or the EXP / Mito amount
        public int Days;           // Reward_Period (0 = permanent; this rebuild keeps everything permanently)
        public int Etc;            // Etc_Value of the row (car grade for car keys, basket grade...)
        public bool Skid;          // SkidItem (vshopitem) rather than NormalItem (useitemclient)
        public override string ToString() => Kind == GrantKind.Item ? $"{ItemId} x{Count}" : Kind == GrantKind.Bust ? "bust" : $"{Kind} {Count}";
    }

    public sealed class RewardGroups
    {
        readonly Dictionary<string, List<RewardRow>> groups = new Dictionary<string, List<RewardRow>>(StringComparer.OrdinalIgnoreCase);

        public int Count => groups.Count;
        public bool Has(string id) => id != null && groups.ContainsKey(id.Trim());
        public IReadOnlyList<RewardRow> Rows(string id) => id != null && groups.TryGetValue(id.Trim(), out var l) ? l : (IReadOnlyList<RewardRow>)Array.Empty<RewardRow>();
        public IEnumerable<string> Ids => groups.Keys;

        public static RewardGroups Parse(byte[] data)
        {
            var g = new RewardGroups();
            if (data == null) return g;
            var t = XltTable.Parse(data);
            foreach (var r in t.Rows)
            {
                var row = new RewardRow
                {
                    Group = t.Get(r, "GroupId").Trim(), Type = t.Get(r, "Reward_Type").Trim(), ItemId = t.Get(r, "ItemId").Trim(),
                    Period = Int(t.Get(r, "Reward_Period")), Count = Int(t.Get(r, "Reward_Count")), Etc = Int(t.Get(r, "Etc_Value")),
                    EtcStr = t.Get(r, "Etc_Str").Trim(), Probability = Float(t.Get(r, "Probability")),
                };
                if (row.Group.Length == 0) continue;
                g.Add(row);
            }
            return g;
        }

        public void Add(RewardRow row)
        {
            if (!groups.TryGetValue(row.Group, out var l)) groups[row.Group] = l = new List<RewardRow>();
            l.Add(row);
        }

        /// <summary>Draws pool <paramref name="id"/> for a driver of <paramref name="level"/>; empty when the pool is unknown.</summary>
        public List<RewardGrant> Draw(string id, int level, Random rng)
        {
            var list = new List<RewardGrant>();
            Draw(id, level, rng ?? new Random(), list, 0);
            return list;
        }

        void Draw(string id, int level, Random rng, List<RewardGrant> into, int depth)
        {
            if (depth > 8 || !groups.TryGetValue((id ?? "").Trim(), out var rows)) return;
            var live = rows.FindAll(r => r.Probability > 0);
            if (live.Count == 0) return;
            if (live.TrueForAll(r => r.Probability >= 100f))
            {
                if (IsLevelBand(live))
                {
                    var band = live.Find(r => r.Etc >= level) ?? live[live.Count - 1];
                    Resolve(band, level, rng, into, depth);
                    return;
                }
                foreach (var r in live) Resolve(r, level, rng, into, depth);
                return;
            }
            double total = 0;
            foreach (var r in live) total += r.Probability;
            double roll = rng.NextDouble() * total;
            foreach (var r in live)
            {
                roll -= r.Probability;
                if (roll < 0) { Resolve(r, level, rng, into, depth); return; }
            }
            Resolve(live[live.Count - 1], level, rng, into, depth);
        }

        static bool IsLevelBand(List<RewardRow> rows)
        {
            if (rows.Count < 2) return false;
            for (int i = 0; i < rows.Count; i++)
            {
                if (!rows[i].Type.Equals("RewardGroup", StringComparison.OrdinalIgnoreCase) || rows[i].Etc <= 0) return false;
                if (i > 0 && rows[i].Etc <= rows[i - 1].Etc) return false;
            }
            return true;
        }

        void Resolve(RewardRow r, int level, Random rng, List<RewardGrant> into, int depth)
        {
            switch (r.Type.ToLowerInvariant())
            {
                case "rewardgroup": Draw(r.ItemId, level, rng, into, depth + 1); break;
                case "normalitem":
                case "skiditem":
                    if (r.ItemId.Length == 0 || r.ItemId == "0") break;
                    if (r.ItemId.Equals("lotto_00001", StringComparison.OrdinalIgnoreCase)) { into.Add(new RewardGrant { Kind = GrantKind.Bust, ItemId = r.ItemId }); break; }
                    into.Add(new RewardGrant { Kind = GrantKind.Item, ItemId = r.ItemId, Count = Math.Max(1, r.Count), Days = r.Period, Etc = r.Etc, Skid = r.Type.Equals("SkidItem", StringComparison.OrdinalIgnoreCase) });
                    break;
                case "exp": if (r.Count > 0) into.Add(new RewardGrant { Kind = GrantKind.Exp, Count = r.Count }); break;
                case "mito": if (r.Count > 0) into.Add(new RewardGrant { Kind = GrantKind.Mito, Count = r.Count }); break;
                default:
                    if (r.ItemId.Equals("lotto_00001", StringComparison.OrdinalIgnoreCase)) into.Add(new RewardGrant { Kind = GrantKind.Bust, ItemId = r.ItemId });
                    break;   // Recycle: the parts recycler's own pool, not an event prize
            }
        }

        static int Int(string s) => int.TryParse((s ?? "").Trim(), out int v) ? v : 0;
        static float Float(string s) => float.TryParse((s ?? "").Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : 0f;
    }
}
