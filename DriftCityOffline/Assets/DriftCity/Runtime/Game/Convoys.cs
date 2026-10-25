using System.Collections.Generic;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// Boss HUV convoys (DriftCity_RE/TRAFFIC.md sections 11 and 15), as the client's HUV AI runs them:
    /// <list type="bullet">
    /// <item>The server creates the escorts after their boss (0x4a1470): 10001-10004 at once for boss 11001 (0x4a12e0), the
    /// "baby dozers" 11005 / 11006 of boss 11004 and Lil' Grimer 11002 of 11001 are kept in the boss (+0x1e54 / +0x1e78) and
    /// come out when the boss is damaged enough (0x4a1d10 / 0x4a1860 -> 0x4a16c0). Each is created at the boss's position
    /// and heading (0x4a0f10, "Create Convoy Fail: %u" when that fails).</item>
    /// <item>Its AI joins the first boss AI that leads nobody's group (0x444fb0: leader +0x548, followers +0x550 / +0x554)
    /// and copies the boss's route (0x445090: route index +0xac, the route lists, the road element and lane the boss is on);
    /// the boss's update re-syncs every follower whenever it re-plans (0x445130). Then 0x4912d0(v): AI state 2 (started;
    /// the next frame 3, driving, 0x492830) with the current speed v = 40 (Lil' Grimer: 52.8 / cos 10 deg = 53.6); the
    /// target speed is the escort's own BossHuvInfo speed (150 km/h), not a gap to the car ahead.</item>
    /// <item>When the boss is caught or escapes (0x492830 states 5 / 4) its followers are let go (0x444f30): each plans a
    /// route of its own (0x4442b0) at its own speed and drives on alone.</item>
    /// </list>
    /// The remake's escorts drive the boss's lanes (its lane trail = the copied route) at their own speed and brake for what
    /// is ahead, so they queue up behind it. (remake rule) They spawn one car length apart behind the boss on its lane instead
    /// of inside it; released escorts drive on for <see cref="ReleasedSeconds"/> and are removed (no server removes them).
    /// </summary>
    public sealed class Convoy : MonoBehaviour
    {
        public const int LeaderA = 11001, LeaderB = 11004, LilGrimer = 11002;
        /// <summary>0x4a12e0 / 0x4a16c0: the escort's starting speed (the mover's +0x16c), m/s.</summary>
        public const float StartSpeed = 40f;
        /// <summary>(remake rule) spacing of the escorts behind the boss at the spawn, metres.</summary>
        public const float SpawnSpacing = 12f;
        /// <summary>Old fixed share (half health); the threshold is now the boss's BossHuvInfo row, <see cref="ChaseRules.SplitShare"/>.</summary>
        public const float SplitAt = 0.5f;
        public static float ReleasedSeconds = 20f;
        /// <summary>Kept for old callers: the escort start speed reused as metres.</summary>
        public const float FollowDistance = StartSpeed;

        public TrafficCar Leader;
        public readonly List<TrafficCar> Escorts = new List<TrafficCar>();
        public int LeaderId;
        public bool Released { get; private set; }
        /// <summary>The kept escorts (11005 / 11006) came out.</summary>
        public bool Split { get; private set; }
        /// <summary>The boss passed its damage threshold (+0x88) and the kept escorts are on their way out (0x4a1d10 stages).</summary>
        public bool SplitStarted { get; private set; }
        /// <summary>Kept escorts are spawned here; false for a party member's copy (the owner's states drive them).</summary>
        public System.Action<List<TrafficCar>> KeptOut;
        TrafficSystem traffic;
        float releasedFor, splitIn = -1f;

        /// <summary>The escorts created with the boss (0x4a1470 -> 0x4a12e0): 11001 leads 10001-10004 at once.</summary>
        public static int[] EscortIds(int leaderId) =>
            leaderId == LeaderA ? new[] { 10001, 10002, 10003, 10004 } : new int[0];

        /// <summary>The escorts kept in the boss until it is damaged (0x4a1d10): 11004's 11005 and 11006.</summary>
        public static int[] KeptIds(int leaderId) => leaderId == LeaderB ? new[] { 11005, 11006 } : new int[0];

        /// <summary>0x4a12e0 / 0x4a16c0: the starting speed handed to the AI (40; 52.8 / cos 10 deg for 11002), m/s.</summary>
        public static float StartSpeedFor(int escortId) => escortId == LilGrimer ? 52.8f / Mathf.Cos(10f * Mathf.Deg2Rad) : StartSpeed;
        /// <summary>Old name of <see cref="StartSpeedFor"/>.</summary>
        public static float FollowDistanceFor(int escortId) => StartSpeedFor(escortId);

        /// <summary>The escort's cruise: its BossHuvInfo speed (km/h); 150 when the table is missing.</summary>
        public static float CruiseFor(int id)
        {
            var b = HuvRoutes.Boss(id);
            return b != null && b.SpeedKmh > 0 ? b.SpeedKmh : 150f;
        }

        /// <summary>
        /// Gives a boss HUV its convoy (null for bosses that lead none). The first escorts come at once; 11004's kept ones
        /// come with <see cref="OnLeaderHealth"/>.
        /// </summary>
        public static Convoy Attach(TrafficSystem traffic, TrafficCar leader, int leaderId)
        {
            if (!traffic || !leader || (EscortIds(leaderId).Length == 0 && KeptIds(leaderId).Length == 0)) return null;
            var c = new GameObject("Convoy " + leaderId).AddComponent<Convoy>();
            c.transform.SetParent(traffic.transform, false);
            c.traffic = traffic;
            c.Leader = leader;
            c.LeaderId = leaderId;
            c.SpawnEscorts(EscortIds(leaderId));
            Debug.Log($"Drift City: convoy of boss {leaderId} with {c.Escorts.Count} escorts");
            return c;
        }

        /// <summary>
        /// The boss's health share changed: at its damage threshold (+0x88 = BossHuvInfo 컨버팅시간 40 % of 내구력, 0x4a0f10)
        /// 11004's kept escorts start out of the boss (0x4a1d10: 2 s + 3 s of stages, 0x49f4d0 / 0x49f570) and come out.
        /// </summary>
        public void OnLeaderHealth(float fraction)
        {
            if (SplitStarted || Released || KeptIds(LeaderId).Length == 0 || !ChaseRules.SplitDue(LeaderId, 1f - fraction)) return;
            SplitStarted = true;
            var kept = ChaseRules.KeptOf(LeaderId);
            splitIn = (kept != null ? kept.OutSeconds : 0f) * ChaseRules.TimeScale;
            if (splitIn <= 0f) SplitNow();
        }

        /// <summary>The kept escorts come out now (the stages ran, or the party chase's server said so).</summary>
        public void SplitNow()
        {
            if (Split || Released) return;
            SplitStarted = true;
            Split = true;
            splitIn = -1f;
            var made = SpawnEscorts(KeptIds(LeaderId));
            KeptOut?.Invoke(made);
        }

        /// <summary>Creates escorts at the boss (one car length apart behind it on its lane) following it.</summary>
        public List<TrafficCar> SpawnEscorts(int[] ids)
        {
            var made = new List<TrafficCar>();
            if (!traffic || !Leader || ids == null) return made;
            // clear of the boss's body (11001 is a truck with its trailer), then a car length each
            var box = Leader.GetComponent<BoxCollider>();
            float behind = (box ? box.size.z * 0.5f : 3f) + SpawnSpacing * 0.5f;
            foreach (int id in ids)
            {
                var info = GameTables.Huvs.Find(h => h.UniqueId == id) ?? GameTables.BossHuvs.Find(h => h.UniqueId == id);
                if (info == null) { Debug.LogWarning($"Create Convoy Fail: {id}"); continue; }
                string nif = HuvTarget.BodyNif(info) ?? $@"vehicle\huv\{info.FileName}";
                behind += SpawnSpacing;
                int lane = Leader.Lane; float along = Leader.Along - behind;
                var chain = new List<int>();   // the lanes from the spawn lane up to the boss's
                while (along < 0 && lane >= 0)
                {
                    int prev = PreviousLane(traffic, lane);
                    if (prev < 0) { along = 0; break; }
                    chain.Insert(0, lane);
                    lane = prev;
                    along += traffic.Network.Lanes[lane].Length;
                }
                var car = traffic.SpawnModel(nif, lane, Mathf.Max(0, along), CruiseFor(id));
                if (!car) { Debug.LogWarning($"Create Convoy Fail: {id}"); continue; }
                car.Route.AddRange(chain);
                car.name = "Convoy " + info.UiName;
                car.HuvPose = true;   // 0x492be0 / 0x492d50: the HUV pose levels
                car.Erratic = Leader.Erratic;
                // 0x445090: the boss's route; 0x4912d0: driving at once at the start speed
                car.FollowTarget = Leader;
                car.FollowGap = 0f;
                car.Ahead = Escorts.Count == 0 ? Leader : Escorts[Escorts.Count - 1];
                car.Speed = StartSpeedFor(id);
                Escorts.Add(car);
                made.Add(car);
            }
            return made;
        }

        /// <summary>0x444f30: the boss was caught or escaped; the escorts drive on alone.</summary>
        public void Release()
        {
            if (Released) return;
            Released = true;
            foreach (var e in Escorts)
            {
                if (!e) continue;
                e.FollowTarget = null;
                e.Ahead = null;
                e.Erratic = true;
            }
        }

        static int PreviousLane(TrafficSystem traffic, int lane)
        {
            var lanes = traffic.Network.Lanes;
            for (int i = 0; i < lanes.Count; i++) if (lanes[i].Next.Contains(lane)) return i;
            return -1;
        }

        void Update()
        {
            if (splitIn > 0f && !Split && !Released) { splitIn -= Time.deltaTime; if (splitIn <= 0f) SplitNow(); }
            if (!Leader && !Released) Release();
            if (!Released) return;
            releasedFor += Time.deltaTime;
            if (releasedFor < ReleasedSeconds) return;
            foreach (var e in Escorts) if (e) Destroy(e.gameObject);
            Destroy(gameObject);
        }

        void OnDestroy()
        {
            // the chase was torn down (travel, logout): nothing of the convoy stays behind
            foreach (var e in Escorts) if (e) Destroy(e.gameObject);
        }
    }
}
