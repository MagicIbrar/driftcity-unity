using System.Collections.Generic;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// The PvP gate effects (NewGateEffect, net feature 0x40000; DriftCity_RE/PVP.md 14.5 / 15.2). The client gets a
    /// PlayerEvent {charId, type, value} (handler 0x50e530) after a gate and plays the type's banner (0x726f20 for itself,
    /// 0x757750 for a spectator's watched car): 0 pvp_null, 1 pvp_boostcharge100, 2 pvp_boostcharge50, 3 pvp_skidrush
    /// (30 s), 5 pvp_untouchable (5 s), 6 pvp_booost (30 s), 7 pvp_boostcharget50; 4 none. Which gate gives what was the
    /// server's: the remake's rule ([remake], mirrored by race.js <c>gateEffectFor</c>) is a catch-up draw by the driver's
    /// place when it passes a gate (not the finish line), see <see cref="Draw"/>; the effects act on the local car.
    /// </summary>
    public static class PvpGateEffects
    {
        public const int Null = 0, Charge50 = 1, Charge100 = 2, SkidRush = 3, Repair = 4, Untouchable = 5, Booost = 6, TeamCharge50 = 7;   // the UI effect numbers

        /// <summary>The room feature (0x40000); tests and the server option turn it off.</summary>
        public static bool Enabled = true;
        /// <summary>NewGateEffectFactor (bits 19..28): scales the timed effects (1 = the banners' 30 / 5 s).</summary>
        public static float TimeFactor = 1f;

        public static int LastType { get; private set; } = -1;
        public static int Given { get; private set; }

        /// <summary>
        /// The remake's gate rule: place p of n -> f = (p - 1) / (n - 1) (0 = leading, 1 = last). Leaders mostly get
        /// nothing, the back of the field the strong effects. Team rooms turn a half charge into the team's half charge.
        /// r is uniform in [0, 1).
        /// </summary>
        public static int Draw(int place, int count, bool team, float r)
        {
            float f = count > 1 ? Mathf.Clamp01((place - 1) / (float)(count - 1)) : 0f;
            int t;
            if (f < 0.34f) t = r < 0.6f ? Null : r < 0.85f ? Charge50 : SkidRush;
            else if (f <= 0.66f) t = r < 0.3f ? Null : r < 0.6f ? Charge50 : r < 0.8f ? Charge100 : SkidRush;
            else t = r < 0.1f ? Null : r < 0.3f ? Charge50 : r < 0.6f ? Charge100 : r < 0.8f ? Booost : Untouchable;
            if (team && t == Charge50) t = TeamCharge50;
            return t;
        }

        /// <summary>Offline: the local driver passed a gate in place <paramref name="place"/> of <paramref name="count"/>.</summary>
        public static int OnGate(DriftCar car, int place, int count, bool team, IEnumerable<GameObject> rivals = null)
        {
            if (!Enabled || !car) return -1;
            int t = Draw(place, count, team, Random.value);
            Apply(car, t, rivals);
            return t;
        }

        /// <summary>Plays the banner and starts the effect on <paramref name="car"/> (online: race.gateEffect).</summary>
        public static void Apply(DriftCar car, int type, IEnumerable<GameObject> rivals = null)
        {
            VoiceLines.GateEffect(type);   // 0x7275c8: the gate's voice line
            LastType = type; Given++;
            GameEffects.PlayGateEffect(type);
            if (!car) return;
            var s = car.TryGetComponent(out GateEffectState haveGateEffectState) ? haveGateEffectState : car.gameObject.AddComponent<GateEffectState>();
            switch (type)
            {
                case Charge100: car.BoostGauge = 1f; break;
                case Charge50: case TeamCharge50: car.BoostGauge = Mathf.Min(1f, car.BoostGauge + 0.5f); break;
                case SkidRush: s.SkidRushLeft = 30f * TimeFactor; break;
                case Untouchable: s.Untouchable(5f * TimeFactor, rivals); break;
                case Booost: s.BooostLeft = 30f * TimeFactor; break;
            }
        }

        /// <summary>A spectator's watched car got an effect (0x757750): the banner only.</summary>
        public static void Watched(int type) { LastType = type; GameEffects.PlayGateEffect(type); }

        /// <summary>Ends every running effect (race over).</summary>
        public static void Clear(DriftCar car)
        {
            if (car && car.TryGetComponent<GateEffectState>(out var s)) Object.Destroy(s);
        }
    }

    /// <summary>The timed gate effects on a car: skid rush (drifting fills the gauge 3x), booost (the gauge fills 3x while
    /// driving), untouchable (no collisions with the other race cars).</summary>
    public sealed class GateEffectState : MonoBehaviour
    {
        public float SkidRushLeft, BooostLeft, UntouchableLeft;
        DriftCar car;
        readonly List<(Collider a, Collider b)> ignored = new List<(Collider, Collider)>();

        void Awake() => car = GetComponent<DriftCar>();

        public void Untouchable(float seconds, IEnumerable<GameObject> rivals)
        {
            UntouchableLeft = Mathf.Max(UntouchableLeft, seconds);
            if (rivals == null) return;
            var mine = GetComponentsInChildren<Collider>();
            foreach (var r in rivals)
            {
                if (!r || r == gameObject) continue;
                foreach (var b in r.GetComponentsInChildren<Collider>())
                    foreach (var a in mine)
                    {
                        if (!a || !b || a is WheelCollider || b is WheelCollider) continue;
                        Physics.IgnoreCollision(a, b, true);
                        ignored.Add((a, b));
                    }
            }
        }

        void Update()
        {
            if (!car) { Destroy(this); return; }
            float dt = Time.deltaTime;
            float fill = dt / Mathf.Max(0.1f, car.BoostFillSeconds);
            if (SkidRushLeft > 0f) { SkidRushLeft -= dt; if (car.DriftState != 0 && !car.Boosting) car.BoostGauge = Mathf.Min(1f, car.BoostGauge + fill * 2f); }
            if (BooostLeft > 0f) { BooostLeft -= dt; if (!car.Boosting) car.BoostGauge = Mathf.Min(1f, car.BoostGauge + fill * 2f); }
            if (UntouchableLeft > 0f) { UntouchableLeft -= dt; if (UntouchableLeft <= 0f) Restore(); }
            if (SkidRushLeft <= 0f && BooostLeft <= 0f && UntouchableLeft <= 0f && ignored.Count == 0) Destroy(this);
        }

        void Restore()
        {
            foreach (var (a, b) in ignored) if (a && b) Physics.IgnoreCollision(a, b, false);
            ignored.Clear();
        }

        void OnDestroy() => Restore();
    }
}
