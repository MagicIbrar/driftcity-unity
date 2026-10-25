using System;
using System.Collections.Generic;
using DriftCity.Net;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// The HUVs of a chase as the server decides them (DriftCity_RE/CHASES.md section 2; server src/huvrules.js, which this
    /// mirrors for the offline game - keep the two in step). BS_PktChaseBeginConfirm (0x4f2500) brought up to 16 HUV units
    /// of 0x30 bytes, each with its own AI parameters (0x4f0410: +0xc speed km/h, +0x10 top speed, +0x14 / +0x18 the burst on
    /// a hit and its time, +0x1c the turbo %), and the time limit. The remake's rule (the original server's is lost):
    /// <list type="bullet">
    /// <item>count 1 + level / 30 (+1 for a party of 3-4), at most 16; a boss chase is one boss (its escorts are its
    /// convoy);</item>
    /// <item>from level 40 one chase in ten is a convoy: 16 HUVs leave all 16 slots of the spawn point in a row at one
    /// speed, 30 health each, 240 s;</item>
    /// <item>speed 85 + level / 2 km/h (at most 140) +-5, top speed +30, a 1 s burst when hit, a 10 % turbo; a boss unit
    /// says 0 and the client reads BossHuvInfo (0x4a0f10);</item>
    /// <item>health: the quoted 100 + Crash bounded to 100..250, bosses 300; time 150 s + 30 s per HUV beyond the first (at
    /// most 300 s), bosses 180 s.</item>
    /// </list>
    /// </summary>
    public static class ChaseRules
    {
        public const int MaxUnits = 16, ConvoyLevel = 40;
        public const float ConvoyChance = 0.1f, ConvoyHp = 30f, ConvoySeconds = 240f, BossHp = 300f, BossSeconds = 180f;
        /// <summary>Scales the kept escorts' stage delays (tests shorten them; 1 in the game).</summary>
        public static float TimeScale = 1f;

        /// <summary>One HUV of the chase (the server's unit).</summary>
        public sealed class Unit
        {
            /// <summary>vehiclelist index the model is picked with (HuvTarget.Pick), or a boss's vehiclelist id for kept units.</summary>
            public int Huv;
            /// <summary>Cruise and top speed (km/h); 0 = the BossHuvInfo row.</summary>
            public float Kmh, TopKmh;
            public float BoostSeconds, TurboPercent;
            public float Hp;
        }

        public sealed class Plan
        {
            public readonly List<Unit> Units = new List<Unit>();
            public float Seconds;
            public bool Convoy;
        }

        public static int UnitCount(int level, int members = 1, bool boss = false)
        {
            if (boss) return 1;
            return Mathf.Clamp(1 + Mathf.Max(0, level) / 30 + (members >= 3 ? 1 : 0), 1, MaxUnits);
        }

        static Unit Speeds(int level, Func<float> rng)
        {
            float kmh = Mathf.Round(Mathf.Clamp(85f + level / 2f, 85f, 140f) + (rng() * 10f - 5f));
            return new Unit { Kmh = kmh, TopKmh = kmh + 30f, BoostSeconds = 1f, TurboPercent = 10f };
        }

        /// <summary>The offline rule (the server's when online): units, time limit, convoy.</summary>
        public static Plan Units(int level, int members, bool boss, int huv, float hp, Func<float> rng = null)
        {
            rng ??= () => UnityEngine.Random.value;
            int lv = Mathf.Clamp(level, 1, 200);
            var plan = new Plan();
            if (boss)
            {
                plan.Units.Add(new Unit { Huv = Mathf.Max(0, huv), Hp = BossHp });
                plan.Seconds = BossSeconds;
                return plan;
            }
            float quoted = Mathf.Clamp(hp > 0 ? hp : 100f, 100f, 250f);
            plan.Convoy = lv >= ConvoyLevel && rng() < ConvoyChance;
            int count = plan.Convoy ? MaxUnits : UnitCount(lv, members);
            var shared = Speeds(lv, rng);
            for (int k = 0; k < count; k++)
            {
                var s = plan.Convoy || k == 0 ? shared : Speeds(lv, rng);
                plan.Units.Add(new Unit
                {
                    Huv = k == 0 ? Mathf.Max(0, huv) : Mathf.FloorToInt(rng() * 1000f), Kmh = s.Kmh, TopKmh = s.TopKmh,
                    BoostSeconds = s.BoostSeconds, TurboPercent = s.TurboPercent, Hp = plan.Convoy ? ConvoyHp : quoted,
                });
            }
            plan.Seconds = plan.Convoy ? ConvoySeconds : Mathf.Min(300f, 150f + 30f * (count - 1));
            return plan;
        }

        /// <summary>The server's units (chase.begin reply / chase.party.start push).</summary>
        public static Plan FromNet(Dictionary<string, object> m)
        {
            var plan = new Plan { Seconds = (float)m.N("seconds", 150), Convoy = m.B("convoy") };
            foreach (var o in m.L("units") ?? new List<object>())
                if (o is Dictionary<string, object> u)
                    plan.Units.Add(new Unit { Huv = u.I("huv"), Kmh = (float)u.N("kmh"), TopKmh = (float)u.N("top"), BoostSeconds = (float)u.N("boostSeconds"), TurboPercent = (float)u.N("turbo"), Hp = (float)u.N("hp", 100) });
            return plan;
        }

        // ---------------- the kept escorts (0x4a1860 / 0x4a1d10) ----------------

        /// <summary>
        /// A boss's kept HUVs: 11001 keeps Lil' Grimer 11002 (a target of its own), 11004 the baby dozers 11005 / 11006 (escorts).
        /// The threshold is BossHuvInfo 컨버팅시간 (40 %) of the boss's health taken off; then the trailer stages run (11001:
        /// 3 s + 3 s + 2.367 s, 0x49f2a0 / 0x49f340 / 0x49f3c0; 11004: 2 s + 3 s, 0x49f4d0 / 0x49f570) before they come out
        /// (0x4a16c0), and 10 s after Lil' Grimer left the truck is finished (0x4a1860 state 7: damage = max + 10).
        /// </summary>
        public sealed class Kept
        {
            public int BossId;
            public float OutSeconds, FinishSeconds;
            public int[] Units = Array.Empty<int>(), Escorts = Array.Empty<int>();
            /// <summary>내구력 of the boss and of each kept unit (the kept unit's health against the boss's).</summary>
            public float Durability;
            public float[] UnitDurability = Array.Empty<float>();
        }

        public static Kept KeptOf(int bossId) => bossId switch
        {
            Convoy.LeaderA => new Kept { BossId = bossId, OutSeconds = 8.367f, FinishSeconds = 10f, Units = new[] { Convoy.LilGrimer }, Durability = Durability(Convoy.LeaderA, 1400), UnitDurability = new[] { Durability(Convoy.LilGrimer, 300) } },
            Convoy.LeaderB => new Kept { BossId = bossId, OutSeconds = 5f, Escorts = new[] { 11005, 11006 }, Durability = Durability(Convoy.LeaderB, 600) },
            _ => null,
        };

        static float Durability(int id, float fallback) { var b = HuvRoutes.Boss(id); return b != null && b.Durability > 0 ? b.Durability : fallback; }

        /// <summary>The share of the boss's health to take off before the kept HUVs come out (컨버팅시간 x 0.01).</summary>
        public static float SplitShare(int bossId)
        {
            var b = HuvRoutes.Boss(bossId);
            float pct = b != null && b.ConvertPercent > 0 ? b.ConvertPercent : (bossId == Convoy.LeaderA || bossId == Convoy.LeaderB ? 40f : 100f);
            return Mathf.Clamp01(pct / 100f);
        }

        /// <summary>True when <paramref name="damageShare"/> (0..1 of the health taken off) reached the boss's threshold.</summary>
        public static bool SplitDue(int bossId, float damageShare) => KeptOf(bossId) != null && damageShare >= SplitShare(bossId) - 1e-4f;
    }
}
