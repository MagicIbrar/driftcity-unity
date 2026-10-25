using System.Collections.Generic;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// The race side of the Battle Zone quick-slot items (DriftCity_RE/INVENTORY.md "Battle quick slots", PHYSICS.md 13.5):
    /// <see cref="BattleQuickSlots.Used"/> fires with the item's kind and seconds and this applies it to the player's car.
    /// - crash (Crash Capsule i_n_00055, 20 s): the car's +0x1ad0 flag, read by the contact callback 0x47e510: +5 gauge for
    ///   every traffic car hit (<see cref="DriftCar.CrashCapsule"/>).
    /// - invincible (Invincible Rush i_n_00058 / i_b_untouchable_*, 10 s): hits cost no speed or durability, the other race
    ///   cars pass through (the gate effect's untouchable state) and untouchable_shield.nif rides on the car.
    /// - dcr (DCR Capsule i_n_00059 / i_b_skidrush_*, 10 s): "Drift City will be initiated as you pass a gate": the next gate
    ///   passed within the 10 s gives the pvp_skidrush effect (PvpGateEffects SkidRush, drifting fills the gauge).
    /// - eboost (Emergency Boost Kit i_n_00060 / i_b_quickb_*): a boost now with the current gauge (30 % or more).
    /// The repair kit and the Boost Recharger are applied by BattleQuickSlots itself (durability +30 %, +0x1abc = 2).
    /// </summary>
    public sealed class BattleItemEffects : MonoBehaviour
    {
        static BattleItemEffects instance;
        public static BattleItemEffects Instance => instance;

        float crashUntil, invincibleUntil, dcrUntil;
        int dcrGateMark = -1;
        DriftCar crashCar;
        NifEffect shield;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot() => Ensure();

        public static BattleItemEffects Ensure()
        {
            if (instance) return instance;
            var go = new GameObject("Battle Item Effects");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<BattleItemEffects>();
            return instance;
        }

        void OnEnable() { BattleQuickSlots.Used -= OnUsed; BattleQuickSlots.Used += OnUsed; }
        void OnDisable() { BattleQuickSlots.Used -= OnUsed; }
        void OnDestroy() { if (instance == this) instance = null; EndAll(); }

        static DriftCar Car => GameFlow.Instance ? GameFlow.Instance.PlayerCar : null;

        /// <summary>The effect of one used item on <paramref name="car"/> (the player's car when null).</summary>
        public void OnUsed(string kind, float seconds) => Apply(Car, kind, seconds);

        public void Apply(DriftCar car, string kind, float seconds)
        {
            if (!car) return;
            switch (kind)
            {
                case "crash":
                    crashCar = car;
                    car.CrashCapsule = true;
                    crashUntil = Time.time + Mathf.Max(0.1f, seconds);
                    break;
                case "invincible":
                    car.InvincibleTime = Mathf.Max(car.InvincibleTime, seconds);
                    invincibleUntil = Time.time + seconds;
                    var state = car.TryGetComponent(out GateEffectState haveGateEffectState) ? haveGateEffectState : car.gameObject.AddComponent<GateEffectState>();
                    state.Untouchable(seconds, Rivals(car));
                    if (shield) Destroy(shield.gameObject);
                    shield = GameEffects.Attach("untouchable_shield", car.transform, Vector3.zero, true, seconds);
                    break;
                case "dcr":
                    dcrUntil = Time.time + Mathf.Max(0.1f, seconds);
                    dcrGateMark = PvpGateEffects.Given;
                    break;
                case "eboost":
                    car.StartBoostNow();
                    break;
            }
        }

        /// <summary>The other cars of the race: remote players and AI racers (any DriftCar or RemoteCar but ours).</summary>
        static IEnumerable<GameObject> Rivals(DriftCar mine)
        {
            foreach (var c in FindObjectsByType<DriftCar>(FindObjectsSortMode.None)) if (c && c != mine) yield return c.gameObject;
            foreach (var r in FindObjectsByType<Net.RemoteCar>(FindObjectsSortMode.None)) if (r) yield return r.gameObject;
        }

        public bool CrashCapsuleOn => crashCar && crashCar.CrashCapsule;
        public bool DcrPending => dcrUntil > Time.time;

        void Update()
        {
            if (crashCar && Time.time > crashUntil) { crashCar.CrashCapsule = false; crashCar = null; }
            if (shield && Time.time > invincibleUntil) { Destroy(shield.gameObject); shield = null; }
            if (dcrUntil > 0f)
            {
                if (Time.time > dcrUntil) { dcrUntil = 0f; dcrGateMark = -1; }
                else if (PvpGateEffects.Given != dcrGateMark && Car)
                {
                    // a gate was passed while the capsule waited: Drift City starts (on top of what the gate gave)
                    dcrUntil = 0f;
                    PvpGateEffects.Apply(Car, PvpGateEffects.SkidRush);
                    dcrGateMark = -1;
                }
            }
        }

        /// <summary>The race is over: every running item effect ends.</summary>
        public void EndAll()
        {
            if (crashCar) crashCar.CrashCapsule = false;
            crashCar = null;
            dcrUntil = 0f;
            if (shield) Destroy(shield.gameObject);
            shield = null;
        }
    }
}
