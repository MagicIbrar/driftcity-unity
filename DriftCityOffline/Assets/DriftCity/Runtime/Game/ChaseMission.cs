using System;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// A fleeing HUV on the lane network. The player's car damages it on contact: the hit speed in km/h x 0.45, clamped
    /// 6..45 (the 45 cap is the client's, UNDERCITY.md). Used by the HUV chase, Rush Time and the Virtual HUV.
    /// <see cref="SpawnOnRoute"/> gives it the client's HUV AI route (DriftCity_RE/TRAFFIC.md section 15): it drives a road
    /// route through its waypoints (0x447ff0 / 0x44a940), bursts to its top speed when hit or on a turbo roll (0x492830,
    /// BossHuvInfo), and leads its convoy (Convoys.cs). <see cref="Spawn"/> keeps the older erratic driver (random turns).
    /// </summary>
    public sealed class HuvTarget
    {
        public TrafficCar Car;
        public VehicleInfo Info;
        public GameObject Beacon;
        /// <summary>The boss's escorts (bosses 11001 / 11004), null for the others.</summary>
        public Convoy Convoy;
        /// <summary>The BossHuvInfo row of the HUV (bosses and escorts), null for the common HUVs.</summary>
        public HuvRoutes.BossInfo Row;
        /// <summary>The chase's health of this HUV (ChaseMission keeps it; Rush Time and the Virtual HUV keep their own).</summary>
        public float Health, MaxHealth;
        /// <summary>The server's unit index of this HUV (BS_PktChaseBeginConfirm's unit k; kept units follow the units).</summary>
        public int Unit;
        /// <summary>The unit's own AI parameters (0x4f0410: top speed, burst time, turbo %); 0 = BossHuvInfo's row.</summary>
        public float UnitTopKmh, UnitBoostSeconds, UnitTurboPercent;
        public bool Stopped { get; private set; }
        public event Action<float> PlayerHit;
        DriftCar player;
        TrafficSystem traffic;
        string city;
        /// <summary>The points still to pass (waypoint, goal) and the lane that reaches each one.</summary>
        readonly List<Vector3> via = new List<Vector3>();
        readonly List<int> viaLanes = new List<int>();
        float turboClock;

        public static VehicleInfo Pick(bool boss, int level, int index = -1)
        {
            var all = boss && GameTables.BossHuvs.Count > 0 ? GameTables.BossHuvs : GameTables.Huvs;
            // animated bosses list a .kfm (Gamebryo animation) whose model is the .nif of the same name
            var list = all.FindAll(h => BodyNif(h) != null);
            if (list.Count == 0) return null;
            if (index >= 0) return all[index % all.Count] is var x && BodyNif(x) != null ? x : list[index % list.Count];
            var candidates = list.FindAll(h => h.ReqLevel <= level + 5);
            var from = candidates.Count > 0 ? candidates : list;
            return from[UnityEngine.Random.Range(0, from.Count)];
        }

        /// <summary>The HUV's model: vehicle\huv\&lt;file&gt;, a .kfm swapped for the .nif beside it; null when missing.</summary>
        public static string BodyNif(VehicleInfo info)
        {
            if (info == null || string.IsNullOrEmpty(info.FileName)) return null;
            string body = $@"vehicle\huv\{info.FileName}";
            if (body.EndsWith(".kfm", StringComparison.OrdinalIgnoreCase)) body = body.Substring(0, body.Length - 4) + ".nif";
            var fs = DriftCityData.Files;
            return fs == null || fs.Exists(body) ? body : null;
        }

        public static int IndexOf(VehicleInfo info, bool boss)
        {
            var list = boss && GameTables.BossHuvs.Count > 0 ? GameTables.BossHuvs : GameTables.Huvs;
            return Mathf.Max(0, list.IndexOf(info));
        }

        /// <summary>The HUV's body with its tyres, built away from the city (the template TrafficSystem copies).</summary>
        static GameObject Visual(VehicleInfo info)
        {
            string body = BodyNif(info);
            if (body == null) return null;
            string baseName = System.IO.Path.GetFileNameWithoutExtension(info.FileName);
            string tire = $@"vehicle\huv\{baseName}_tire.nif";
            if (DriftCityData.Files == null || !DriftCityData.Files.Exists(tire)) tire = @"vehicle\huv\huv01_a_tire.nif";
            try
            {
                var visual = CarFactory.BuildVisual(body, tire, new Vector3(0, -2000, 0), Quaternion.identity, out _, out _);
                visual.SetActive(false);
                return visual;
            }
            catch (Exception ex) { Debug.LogWarning($"HUV {info.UiName}: {ex.Message}"); return null; }
        }

        /// <summary>The erratic HUV (random turns) on a lane between minDist and maxDist from the player, or the lane nearest
        /// to <paramref name="at"/>. Rush Time and the Virtual HUV use it.</summary>
        public static HuvTarget Spawn(TrafficSystem traffic, DriftCar player, VehicleInfo info, bool boss, float minDist = 120f, float maxDist = 260f, bool beacon = true, Vector3? at = null)
        {
            if (info == null || traffic == null || traffic.Network == null || traffic.Network.Lanes.Count == 0) return null;
            var visual = Visual(info);
            if (!visual) return null;
            bool ok = traffic.TrySpawn(out var car, visual, minDist, maxDist);
            UnityEngine.Object.Destroy(visual);
            if (!ok) return null;
            // a spawn point of chasespawnlist.text: the lane nearest to it
            if (at.HasValue && traffic.NearestLane(at.Value, out int lane, out float along, -1, 150f)) car.Place(lane, along);
            var t = Make(traffic, player, info, boss, car, beacon);
            t.Convoy = boss ? Convoy.Attach(traffic, car, info.UniqueId) : null;   // the boss's escort HUVs
            return t;
        }

        /// <summary>
        /// The client's HUV (TRAFFIC.md section 15): placed on the lane at <paramref name="at"/> running along
        /// <paramref name="heading"/>, it drives the road route through <paramref name="route"/> (a chase HUV: its
        /// waypoint, then the goal; a boss: the points of its Huv00.xipath2 route), then keeps fleeing to spawn points
        /// 1-3 km on (remake rule: the exe's route ends at the goal).
        /// </summary>
        public static HuvTarget SpawnOnRoute(TrafficSystem traffic, DriftCar player, VehicleInfo info, bool boss, Vector3 at, Vector3 heading, IList<Vector3> route, string city, bool beacon = true, bool convoy = true)
        {
            if (info == null || traffic == null || traffic.Network == null || traffic.Network.Lanes.Count == 0) return null;
            var visual = Visual(info);
            if (!visual) return null;
            bool ok = traffic.TrySpawn(out var car, visual, 0f, 2000f);
            UnityEngine.Object.Destroy(visual);
            if (!ok) return null;
            if (HuvRoutes.LaneAt(traffic, at, heading, out int lane, out float along, 200f)) car.Place(lane, along);
            var t = Make(traffic, player, info, boss, car, beacon);
            t.city = city;
            if (route != null) t.via.AddRange(route);
            car.Replan = t.PlanOn;
            t.PlanOn(car);
            if (boss && convoy) t.Convoy = Convoy.Attach(traffic, car, info.UniqueId);
            return t;
        }

        static HuvTarget Make(TrafficSystem traffic, DriftCar player, VehicleInfo info, bool boss, TrafficCar car, bool beacon)
        {
            var t = new HuvTarget { Car = car, Info = info, player = player, traffic = traffic, Row = HuvRoutes.Boss(info.UniqueId) };
            car.name = "HUV " + info.UiName;
            car.Erratic = true;
            // BossHuvInfo's speed (0x4a0f10 -> AI +0x38) for the bosses; the common HUVs' speed was the server's
            car.CruiseKmh = boss && t.Row != null && t.Row.SpeedKmh > 0 ? t.Row.SpeedKmh : Mathf.Clamp(70f + info.Speed * 0.6f, 80f, 140f) * (boss ? 1.15f : 1f);
            // 0x492be0 / 0x492d50: the HUV's pose levels by distance (the hovercraft 11009 stays on its path frame)
            car.HuvPose = true;
            car.PathPoseOnly = info.UniqueId == 11009;
            car.Hit += t.OnHit;
            t.turboClock = UnityEngine.Random.Range(0f, 10f);
            if (beacon)
            {
                t.Beacon = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                UnityEngine.Object.Destroy(t.Beacon.GetComponent<Collider>());
                t.Beacon.name = "HUV Beacon";   // Minimap places ID_MINIMAP_ICON_HUV on it
                var mat = OwnedAssets.Track(t.Beacon, new Material(Shader.Find("Sprites/Default")) { color = new Color(1f, 0.25f, 0.1f, 0.45f) });
                t.Beacon.GetComponent<Renderer>().sharedMaterial = mat;
                t.Beacon.transform.localScale = new Vector3(1.6f, 40f, 1.6f);
            }
            return t;
        }

        /// <summary>
        /// The server's unit (0x4f0410 copies its AI parameters into the HUV): cruise (+0x38), top speed (+0x3c), the burst's
        /// time (+0x44) and the turbo chance (+0x4c); a unit with 0 km/h keeps the BossHuvInfo row / the remake's speed.
        /// </summary>
        public void ApplyUnit(ChaseRules.Unit u)
        {
            if (u == null) return;
            if (u.Kmh > 0 && Car) Car.CruiseKmh = u.Kmh;
            UnitTopKmh = u.TopKmh; UnitBoostSeconds = u.BoostSeconds; UnitTurboPercent = u.TurboPercent;
        }

        float BurstKmh => Row != null ? Row.MaxKmh : UnitTopKmh > 0 ? UnitTopKmh : (Car ? Car.CruiseKmh * 1.2f : 0f);
        float BurstSeconds => Row != null ? Mathf.Max(0.5f, Row.HitBoostSeconds) : UnitBoostSeconds > 0 ? UnitBoostSeconds : 1f;
        float TurboPercent => Row != null ? Row.TurboPercent : UnitTurboPercent;

        /// <summary>The car's Replan: the road route through the points still ahead, else on to a spawn point 1-3 km away.</summary>
        bool PlanOn(TrafficCar car)
        {
            if (!car || traffic == null) return false;
            if (via.Count == 0)
            {
                var pts = ChaseSpawns.For(city);
                var far = pts.FindAll(p => { float d = MissionPosts.Flat(p.Pivot, car.transform.position); return d >= 1000f && d <= 3000f; });
                if (far.Count == 0) far = pts.FindAll(p => MissionPosts.Flat(p.Pivot, car.transform.position) >= 300f);
                if (far.Count == 0) return false;
                via.Add(far[UnityEngine.Random.Range(0, far.Count)].Pivot);
            }
            car.Route.Clear();
            car.Route.AddRange(HuvRoutes.RouteVia(traffic, car.Lane, via, viaLanes));
            return car.Route.Count > 0;
        }

        void OnHit(Collision c)
        {
            if (!player || c.rigidbody != player.GetComponent<Rigidbody>()) return;
            float kmh = c.relativeVelocity.magnitude * 3.6f;
            if (kmh < 12f) return;
            // 0x492830: damage starts the burst for AI +0x44 seconds (BossHuvInfo's time when hit)
            if (Car && !Car.Remote) Car.Boost(BurstKmh, BurstSeconds);
            PlayerHit?.Invoke(Mathf.Clamp(kmh * 0.45f, 6f, 45f));
        }

        /// <summary>Tests and AI stand-ins: a hit as if the player rammed it.</summary>
        public void SimulateHit(float damage) => PlayerHit?.Invoke(damage);

        public void Tick()
        {
            if (!Car) return;
            bool shown = !Car.Remote || Car.HasRemoteState;
            if (Beacon)
            {
                if (Beacon.activeSelf != shown) Beacon.SetActive(shown);
                Beacon.transform.position = Car.transform.position + Vector3.up * 40f;
            }
            if (Car.Remote || Stopped) return;
            // a point of the route is passed once the car is on the lane that reaches it
            while (via.Count > 0 && viaLanes.Count > 0 && (viaLanes[0] < 0 || viaLanes[0] == Car.Lane)) { via.RemoveAt(0); viaLanes.RemoveAt(0); }
            // 0x492830: every 10 s (AI +0x48) a turbo with BossHuvInfo's chance (AI +0x4c)
            turboClock += Time.deltaTime;
            if (turboClock >= 10f)
            {
                turboClock -= 10f;
                if (TurboPercent > 0 && UnityEngine.Random.value * 100f < TurboPercent) Car.Boost(BurstKmh, BurstSeconds);
            }
        }

        /// <summary>Stopped: the HUV stands still and goes after a few seconds (won), or at once.</summary>
        public void Remove(bool caught)
        {
            Stopped = true;
            Convoy?.Release();   // 0x444f30: the escorts drive on alone
            if (Car)
            {
                Car.Hit -= OnHit;
                Car.Replan = null;
                if (Car.Remote) Car.ApplyRemote(Car.transform.position, Car.transform.eulerAngles.y, 0f);   // a copy stops where it is
                if (caught) { Car.CruiseKmh = 0; Car.Speed = 0; Car.Route.Clear(); UnityEngine.Object.Destroy(Car.gameObject, 4f); }
                else UnityEngine.Object.Destroy(Car.gameObject);
            }
            if (Beacon) UnityEngine.Object.Destroy(Beacon);
            Car = null; Beacon = null;
        }

        /// <summary>The HUV and its escorts in a fixed order (what a party HUV's owner sends).</summary>
        public void Cars(List<TrafficCar> into)
        {
            into.Add(Car);
            if (Convoy) into.AddRange(Convoy.Escorts);
        }
    }

    /// <summary>
    /// The HUV chase, rebuilt from DriftCity.exe (DriftCity_RE/CHASES.md, TRAFFIC.md sections 14-15):
    /// <list type="bullet">
    /// <item>The call: CHASING 870000 (EVENTCHASING 880000 in the Christmas season), <see cref="Offer"/>; Tab accepts.</item>
    /// <item>Start (BS_PktChaseBeginConfirm 0x4f2500): chat 10615 "Chase Started", game mode 2 then 3, the time limit from the
    /// server; up to 16 HUVs leave the slots of one chasespawnlist point (0x44a5d0: the nearest point beyond 1 km), each on
    /// the road route to its own waypoint and then the shared goal (HuvRoutes) with the speeds of its unit (0x4f0410); a
    /// boss drives a route of Huv00.xipath2 (0x4a1470) with its convoy; GUIDETEXT HUV_START / HUV_FIND / HUV_CRASH
    /// (GuideText.cs); the city's CHASEINFO map.</item>
    /// <item>Running (mode 3): CHASEPLAYINFO 890000 (time left, HUV count, CANCEL -> 10727).</item>
    /// <item>The kept HUVs (0x4a1860 / 0x4a1d10): at the boss's damage threshold (BossHuvInfo 컨버팅시간 40 % of its health)
    /// 11001's trailer stages run (3 s + 3 s + 2.367 s), Lil' Grimer 11002 leaves at 53.6 m/s as a target of its own and the
    /// truck is finished 10 s later; 11004's baby dozers come out after 5 s (Convoys.cs).</item>
    /// <item>The end (packet 0x4f9dcd): success = mode 6: chat 10617 + 10618 "Earned %d Mitos. Earned %d Exp.", the rising
    /// reward lines (1780000), the arrest banner (UI effect 10, ROO 45) and SID_HUV_SUCCESS; failure = mode 7: chat 10619
    /// "Chase Failed", UI effect 9 (ROO 46), SID_HUV_FAIL (0x6f7d80). A party chase shows PARTYCHASERESULT 980000 with
    /// every member's line (party bonus included, PARTYPLAY.md section 3).</item>
    /// </list>
    /// The server decides the HUVs, their speeds and the time limit (src/huvrules.js; <see cref="ChaseRules"/> is the same
    /// rule offline): online a solo chase asks it with chase.begin (mode 1 until the reply), a party chase gets them in the
    /// start push. The HUVs flee on their routes; each is rammed until its health is gone within <see cref="TimeLimit"/>;
    /// staying over <see cref="EscapeDistance"/> away for <see cref="EscapeSeconds"/> lets them escape (rebuild rules). Online
    /// in a party the leader's accepted call starts the chase for every member in the city (src/partyplay.js chase.party.*):
    /// the HUVs are shared as the client did it (BS_PktPartyHuv 0x4f0940 names an owner): the owner's client runs their AI
    /// and sends their states, the other members show dead-reckoned copies; hits go to the server which keeps each unit's
    /// health, releases the kept HUVs, and pays every member.
    /// </summary>
    public sealed class ChaseMission : MonoBehaviour
    {
        public float TimeLimit = 150f;
        public float EscapeDistance = 650f, EscapeSeconds = 12f;
        /// <summary>CHASEINFO stays up this long at the start (the client's mode 1/2 briefing; rebuild value).</summary>
        public float BriefingSeconds = 5f;
        /// <summary>
        /// Tests: a fixed number of HUVs for a common solo chase (0 = the server's rule, <see cref="ChaseRules"/>). Each takes
        /// its own slot, waypoint and health; all must be stopped.
        /// </summary>
        public static int PackSize = 0;
        /// <summary>How often the party HUV's owner sends its state (s).</summary>
        public const float ShareInterval = 0.2f;
        public static ChaseMission Active { get; private set; }
        /// <summary>A Team ROO quest chase (q_00159/160/161/163/166 set the HUV manager's ROO flag): it ends on the beat_* banners.</summary>
        public bool TeamRoo;
        public event Action<bool> Finished;

        /// <summary>The last result screen's rows (tests).</summary>
        public static List<ChaseScreens.ResultRow> LastRows { get; private set; }

        public bool Boss => boss;
        public bool Party => partyId > 0;
        /// <summary>Party chase: this client runs the shared HUV's AI (the packet's owner, 0x4f0940 "myhuv").</summary>
        public bool IsOwner => !Party || owner;
        /// <summary>The current target's health (the first HUV still running).</summary>
        public float Health => Target != null ? Target.Health : 0f;
        public float MaxHealth => Target != null ? Target.MaxHealth : maxHealth;
        public float TimeLeft => timeLeft;
        public int Hits => hits;
        /// <summary>The first HUV still running (the HUV manager's target, +0xdc).</summary>
        public HuvTarget Target => pack.FirstOrDefault(t => !t.Stopped && t.Car) ?? (pack.Count > 0 && !pack[0].Stopped ? pack[0] : null);
        /// <summary>Every HUV of the chase (stopped ones included).</summary>
        public IReadOnlyList<HuvTarget> Targets => pack;
        /// <summary>HUVs still running (CHASEPLAYINFO's HUV count).</summary>
        public int HuvsLeft => pack.Count(t => !t.Stopped && t.Car);
        /// <summary>The chasespawnlist point the HUVs started from (null when the city has none).</summary>
        public ChaseSpawns.Point SpawnPoint { get; private set; }
        /// <summary>The planned start / waypoints / goal (null for a boss or when the city's points were too few).</summary>
        public HuvRoutes.ChasePlan Plan { get; private set; }
        /// <summary>The units of this chase (the server's, or the offline rule's).</summary>
        public ChaseRules.Plan Units { get; private set; }
        /// <summary>A convoy chase: 16 HUVs in a row from one spawn point (ChaseRules.Convoy).</summary>
        public bool ConvoyChase => Units != null && Units.Convoy;
        /// <summary>The client's game mode (+0x14 of ui+0x15c): 1 requested (waiting for the server), 2 starting, 3 chasing, 6 caught, 7 failed.</summary>
        public int Mode { get; private set; }
        /// <summary>The boss passed its damage threshold: the kept HUVs are on their way (0x4a1860 / 0x4a1d10 stages).</summary>
        public bool SplitStarted => splitStarted;
        /// <summary>Lil' Grimer, once out of the trailer.</summary>
        public HuvTarget LilGrimer => lil;
        /// <summary>The truck was finished 10 s after Lil' Grimer left it (0x4a1860 state 7).</summary>
        public bool TruckFinished => truckFinished;

        DriftCar player;
        TrafficSystem traffic;
        readonly List<HuvTarget> pack = new List<HuvTarget>();
        VehicleInfo info;
        bool boss, owner = true;
        float maxHealth = 100f, timeLeft, lostFor, elapsed, shareTimer;
        int hits;
        int partyId;
        readonly Dictionary<int, float> pendingDamage = new Dictionary<int, float>();
        float sendTimer;
        bool found;
        NuiView playInfo, chaseInfo;
        readonly List<TrafficCar> shared = new List<TrafficCar>();
        // the kept HUVs' timeline (offline / solo; a party chase follows the server's chase.party.split)
        bool splitStarted, lilOut, truckFinished;
        float splitOutIn = -1f, finishIn = -1f;
        HuvTarget lil;
        // online solo: waiting for chase.begin's reply
        bool pending;
        float pendingHp;
        int pendingHuv;

        public static ChaseMission Begin(TrafficSystem traffic, DriftCar player, bool boss = false) => Begin(traffic, player, boss, -1, 0f, 0);

        /// <summary>Starts a chase; <paramref name="huvIndex"/> picks the HUV (party chases), <paramref name="seconds"/> the limit.</summary>
        public static ChaseMission Begin(TrafficSystem traffic, DriftCar player, bool boss, int huvIndex, float seconds, int party, float hp = 0f, bool owner = true, ChaseRules.Plan units = null)
        {
            if (Active) return Active;
            int level = PlayerProfile.Current?.Level ?? 1;
            var pick = HuvTarget.Pick(boss, level, huvIndex);
            if (pick == null) return null;
            var m = new GameObject("HUV Chase").AddComponent<ChaseMission>();
            if (seconds > 0) m.TimeLimit = seconds;
            m.partyId = party;
            m.owner = party <= 0 || owner;
            float quoted = hp > 0 ? hp : boss ? ChaseRules.BossHp : 100f + pick.Crash;
            // online, a solo chase asks the server for its HUVs (the client's chase request -> BS_PktChaseBeginConfirm)
            if (units == null && party <= 0 && PackSize <= 0 && OnlineSession.Active && OnlineSession.Instance && OnlineSession.Instance.Net)
            {
                m.player = player; m.traffic = traffic; m.info = pick; m.boss = boss;
                m.pending = true; m.pendingHp = quoted; m.pendingHuv = HuvTarget.IndexOf(pick, boss);
                m.Mode = 1;
                Active = m;
                MissionNet.Ensure();
                OnlineSession.Instance.Net.Request("chase.begin", new Dictionary<string, object> { ["boss"] = boss, ["huv"] = m.pendingHuv, ["hp"] = (double)quoted }, r => m.OnBegin(r));
                return m;
            }
            units ??= LocalUnits(level, boss, HuvTarget.IndexOf(pick, boss), quoted);
            if (seconds <= 0 && units.Seconds > 0) m.TimeLimit = units.Seconds;
            if (!m.Spawn(traffic, player, pick, boss, units)) { Destroy(m.gameObject); return null; }
            return m;
        }

        /// <summary>The offline rule (<see cref="ChaseRules"/>), or <see cref="PackSize"/> HUVs when a test fixed the count.</summary>
        static ChaseRules.Plan LocalUnits(int level, bool boss, int huv, float hp)
        {
            var plan = ChaseRules.Units(level, 1, boss, huv, hp);
            if (!boss && PackSize > 0)
            {
                var first = plan.Units[0];
                plan.Units.Clear();
                plan.Convoy = false;
                for (int k = 0; k < Mathf.Clamp(PackSize, 1, ChaseRules.MaxUnits); k++)
                    plan.Units.Add(new ChaseRules.Unit { Huv = k == 0 ? first.Huv : -1, Kmh = first.Kmh, TopKmh = first.TopKmh, BoostSeconds = first.BoostSeconds, TurboPercent = first.TurboPercent, Hp = hp });
                plan.Seconds = Mathf.Min(300f, 150f + 30f * (plan.Units.Count - 1));
            }
            return plan;
        }

        /// <summary>chase.begin's reply: the server's units; without one the offline rule.</summary>
        void OnBegin(Dictionary<string, object> r)
        {
            if (!this || !pending) return;
            pending = false;
            if (Active == this) Active = null;
            var plan = r != null && r.B("ok") ? ChaseRules.FromNet(r) : null;
            if (plan == null || plan.Units.Count == 0) plan = LocalUnits(PlayerProfile.Current?.Level ?? 1, boss, pendingHuv, pendingHp);
            if (plan.Seconds > 0) TimeLimit = plan.Seconds;
            if (!Spawn(traffic, player, info, boss, plan))
            {
                UiPopup.Message("No HUV could be found nearby. Try again on a bigger road.");
                Finished = null;
                Destroy(gameObject);
            }
        }

        bool Spawn(TrafficSystem traffic, DriftCar player, VehicleInfo huvInfo, bool isBoss, ChaseRules.Plan units)
        {
            this.player = player; this.traffic = traffic; info = huvInfo; boss = isBoss;
            Units = units;
            var city = GameFlow.Instance ? GameFlow.Instance.City : CitySession.Current ? CitySession.Current.City : null;
            var from = player ? player.transform.position : Vector3.zero;
            maxHealth = units.Units.Count > 0 ? units.Units[0].Hp : isBoss ? ChaseRules.BossHp : 100f + huvInfo.Crash;
            if (isBoss) SpawnBoss(city, from, huvInfo);
            else SpawnPack(city, from, huvInfo, units);
            if (pack.Count == 0)
            {
                // no route could be laid: the older erratic HUV near a spawn point 250-900 m away
                SpawnPoint = player ? ChaseSpawns.Pick(city, from, 250f, 900f) : null;
                var t = HuvTarget.Spawn(traffic, player, huvInfo, isBoss, at: SpawnPoint != null && SpawnPoint.Slots.Count > 0 ? SpawnPoint.Slots[0] : SpawnPoint?.Pivot);
                if (t != null) { t.ApplyUnit(units.Units.FirstOrDefault()); Add(t, maxHealth, 0); }
            }
            if (pack.Count == 0) return false;
            timeLeft = TimeLimit;
            Active = this;
            Mode = 2;
            MissionNet.Ensure();
            ChaseScreens.CloseOffer();
            ChatBox.System(MissionText.Get("10615"));
            AudioLibrary.PlayMusic(this, "bgm/DriftCity_chasing.mp3");
            AudioLibrary.PlaySfx("sfx/SID_BOSSHUV_01");
            chaseInfo = ChaseScreens.Info(city, () => Target != null && Target.Car ? Target.Car.transform.position : (Vector3?)null);
            playInfo = ChaseScreens.OpenPlayInfo(() => Quit());
            ChaseScreens.PlayInfo(playInfo, timeLeft, HuvsLeft);
            if (!IsOwner) MakeRemote();
            return true;
        }

        /// <summary>
        /// 0x44a5d0 / 0x447ed0: the start point beyond 1 km, a goal 1-3 km on, a waypoint per HUV slot; HUV k on slot k with
        /// its unit's speeds. A convoy's 16 HUVs share one waypoint and drive in a row (each keeps out of the one ahead).
        /// </summary>
        void SpawnPack(string city, Vector3 from, VehicleInfo huvInfo, ChaseRules.Plan units)
        {
            Plan = HuvRoutes.PlanChase(ChaseSpawns.For(city), from, new System.Random(UnityEngine.Random.Range(int.MinValue, int.MaxValue)));
            if (Plan == null) return;
            SpawnPoint = Plan.Start;
            int level = PlayerProfile.Current?.Level ?? 1;
            TrafficCar ahead = null;
            for (int k = 0; k < units.Units.Count && k < ChaseRules.MaxUnits; k++)
            {
                var u = units.Units[k];
                var fwd = HuvRoutes.SlotHeading(Plan.Start, k, out var at);
                // the unit's model: the caller's pick for the first, the unit's vehiclelist index for the others (party
                // members spawn the same models)
                var vi = k == 0 ? huvInfo : (u.Huv >= 0 ? HuvTarget.Pick(false, level, u.Huv) : HuvTarget.Pick(false, level)) ?? huvInfo;
                var wp = Plan.Waypoints[(units.Convoy ? 0 : k) % Plan.Waypoints.Length].Pivot;
                var t = HuvTarget.SpawnOnRoute(traffic, player, vi, false, at, fwd, new[] { wp, Plan.Goal.Pivot }, city, beacon: k == 0);
                if (t == null) continue;
                t.ApplyUnit(u);
                // 0x4f0940 -> 0x4912d0(0): a standing start
                t.Car.Speed = 0f;
                if (units.Convoy) { t.Car.Ahead = ahead; ahead = t.Car; }
                Add(t, u.Hp > 0 ? u.Hp : maxHealth, k);
            }
        }

        /// <summary>0x4a1470: the boss starts on a Huv00.xipath2 route (offline: one passing 250-1200 m away) facing along it.</summary>
        void SpawnBoss(string city, Vector3 from, VehicleInfo huvInfo)
        {
            int r = HuvRoutes.PickXiRoute(city, from, 250f, 1200f, out int k);
            if (r < 0) return;
            var route = HuvRoutes.XiPaths(city)[r];
            if (route.Count < 2) return;
            var at = route[k];
            var next = route[(k + 1) % route.Count];
            var t = HuvTarget.SpawnOnRoute(traffic, player, huvInfo, true, at, next - at, HuvRoutes.XiLoop(route, k), city);
            if (t == null) return;
            t.Car.Speed = 0f;   // 0x4a1470 -> 0x4912d0(0, t)
            Add(t, maxHealth, 0);
        }

        void Add(HuvTarget t, float hp, int unit)
        {
            t.MaxHealth = t.Health = hp;
            t.Unit = unit;
            t.PlayerHit += d => OnHit(t, d);
            pack.Add(t);
        }

        HuvTarget UnitTarget(int unit) => pack.FirstOrDefault(t => t.Unit == unit);

        /// <summary>The shared cars in the owner's order: every HUV, each followed by its escorts.</summary>
        List<TrafficCar> SharedCars()
        {
            shared.Clear();
            foreach (var t in pack) if (t.Car || t.Convoy) t.Cars(shared);
            return shared;
        }

        /// <summary>Not the owner: every car of the chase becomes a remote copy, hidden until the owner's first state.</summary>
        void MakeRemote()
        {
            foreach (var c in SharedCars())
            {
                if (!c) continue;
                c.SetRemote(true);
                Show(c, c.HasRemoteState);
            }
        }

        static void Show(TrafficCar c, bool on)
        {
            if (!c) return;
            foreach (var r in c.GetComponentsInChildren<Renderer>(true)) r.enabled = on;
            foreach (var col in c.GetComponentsInChildren<Collider>(true)) col.enabled = on;
        }

        bool ServerHealth => Party && OnlineSession.Active;

        void OnHit(HuvTarget t, float damage)
        {
            if (ended || t.Stopped) return;
            hits++;
            AudioLibrary.PlaySfx("sfx/SID_BOSSHUV_0" + UnityEngine.Random.Range(2, 6));
            if (ServerHealth)
            {
                // the server keeps each unit's health
                pendingDamage.TryGetValue(t.Unit, out float sum);
                pendingDamage[t.Unit] = sum + damage;
                return;
            }
            t.Health -= damage;
            Damaged(t);
            if (t.Health > 0) return;
            t.Health = 0;
            Stop(t);
        }

        /// <summary>One HUV stopped: it stands and goes while the others run on; the last one ends the chase (caught).</summary>
        void Stop(HuvTarget t)
        {
            if (t.Stopped) return;
            // caught before its kept HUV came out: Lil' Grimer stays in the trailer
            if (t == pack.FirstOrDefault() && boss && splitStarted && !lilOut) { splitStarted = false; splitOutIn = -1f; }
            if (pack.Any(x => x != t && !x.Stopped && x.Car))
            {
                GameEffects.ChaseEnded(true, t.Car ? t.Car.transform : null);
                t.Remove(true);
                return;
            }
            End(true);
        }

        int BossId => boss && pack.Count > 0 && pack[0].Info != null ? pack[0].Info.UniqueId : 0;

        /// <summary>
        /// The boss's health share changed: past its damage threshold (BossHuvInfo 컨버팅시간 x 내구력, +0x88) the kept HUVs
        /// start out (11001: Lil' Grimer after the trailer stages; 11004: the baby dozers, Convoys.cs). A party chase follows
        /// the server's chase.party.split instead.
        /// </summary>
        void Damaged(HuvTarget t)
        {
            if (ServerHealth || !boss || t != pack.FirstOrDefault() || t.Health <= 0) return;
            float share = t.MaxHealth > 0 ? t.Health / t.MaxHealth : 0f;
            if (t.Convoy) t.Convoy.OnLeaderHealth(share);
            if (!splitStarted && BossId == Convoy.LeaderA && ChaseRules.SplitDue(Convoy.LeaderA, 1f - share))
            {
                splitStarted = true;
                splitOutIn = Mathf.Max(0.0001f, ChaseRules.KeptOf(Convoy.LeaderA).OutSeconds * ChaseRules.TimeScale);
            }
        }

        /// <summary>0x4a16c0: Lil' Grimer leaves the trailer at the boss and runs at 52.8 / cos 10 deg; the truck is finished 10 s on.</summary>
        HuvTarget LilOut(int unit, float hp)
        {
            if (lilOut) return lil;
            lilOut = true;
            splitOutIn = -1f;
            var bossT = pack.FirstOrDefault();
            if (bossT == null || !bossT.Car) return null;
            var lilInfo = GameTables.BossHuvs.Find(h => h.UniqueId == Convoy.LilGrimer);
            var city = GameFlow.Instance ? GameFlow.Instance.City : CitySession.Current ? CitySession.Current.City : null;
            var bc = bossT.Car.transform;
            var g = lilInfo != null ? HuvTarget.SpawnOnRoute(traffic, player, lilInfo, true, bc.position - bc.forward * 14f, bc.forward, null, city, beacon: false, convoy: false) : null;
            if (g == null) return null;
            g.Car.Speed = Convoy.StartSpeedFor(Convoy.LilGrimer);
            if (hp <= 0)
            {
                // its health against the boss's: BossHuvInfo 내구력 11002 300 of 11001's 1400
                var kept = ChaseRules.KeptOf(Convoy.LeaderA);
                hp = Mathf.Round(bossT.MaxHealth * kept.UnitDurability[0] / kept.Durability * 10f) / 10f;
            }
            Add(g, Mathf.Max(1f, hp), unit);
            lil = g;
            if (!IsOwner) MakeRemote();
            finishIn = ChaseRules.KeptOf(Convoy.LeaderA).FinishSeconds * ChaseRules.TimeScale;
            return g;
        }

        /// <summary>0x4a1860 state 7: the truck is finished (damage = max + 10); Lil' Grimer is out of the trailer for good (+0x724: always the close pose).</summary>
        void FinishTruck()
        {
            if (truckFinished) return;
            truckFinished = true;
            finishIn = -1f;
            if (lil != null && lil.Car) lil.Car.ClosePose = true;
            var bossT = pack.FirstOrDefault();
            if (bossT == null || bossT.Stopped) return;
            bossT.Health = 0;
            Stop(bossT);
        }

        void Update()
        {
            if (ended || pending) return;
            float dt = Time.deltaTime;
            // the kept HUVs' timeline (offline / solo)
            if (splitOutIn > 0f) { splitOutIn -= dt; if (splitOutIn <= 0f && !ServerHealth) LilOut(pack.Count, 0f); }
            if (finishIn > 0f) { finishIn -= dt; if (finishIn <= 0f && !ServerHealth) FinishTruck(); if (ended) return; }
            var target = Target;
            if (target == null || !target.Car || !player) { End(false); return; }
            elapsed += dt;
            timeLeft -= dt;
            if (Mode == 2 && elapsed >= BriefingSeconds)
            {
                Mode = 3;
                if (chaseInfo && UiManager.Instance) UiManager.Instance.Close(chaseInfo);
                chaseInfo = null;
            }
            float dist = Vector3.Distance(target.Car.transform.position, player.transform.position);
            // the HUV starts on a spawn point some way off: escaping counts only once it has been found (within 300 m)
            if (dist < 300f && (!target.Car.Remote || target.Car.HasRemoteState)) found = true;
            lostFor = found && dist > EscapeDistance ? lostFor + dt : 0f;
            if (ServerHealth)
            {
                // the server ends a party chase (time, catch); hits go out at most four times a second, per unit
                sendTimer -= dt;
                if (pendingDamage.Count > 0 && sendTimer <= 0)
                {
                    sendTimer = 0.25f;
                    foreach (var kv in pendingDamage.ToList())
                        OnlineSession.Instance.Net.Request("chase.party.hit", new Dictionary<string, object> { ["damage"] = (double)kv.Value, ["unit"] = kv.Key }, null);
                    pendingDamage.Clear();
                }
                // the owner's client simulates the HUV (0x4f0940 "myhuv") and sends where it is
                shareTimer -= dt;
                if (owner && shareTimer <= 0)
                {
                    shareTimer = ShareInterval;
                    var cars = new List<object>();
                    foreach (var c in SharedCars()) cars.Add(c ? (object)c.NetState().Cast<object>().ToList() : null);
                    OnlineSession.Instance.Net.Send("chase.party.huv", new Dictionary<string, object> { ["cars"] = cars });
                }
            }
            else if (timeLeft <= 0 || lostFor > EscapeSeconds) { End(false); return; }
            foreach (var t in pack) if (!t.Stopped) t.Tick();
            ChaseScreens.PlayInfo(playInfo, Mathf.Max(0, timeLeft), HuvsLeft);
        }

        /// <summary>chase.party.huv: the owner's cars (x, y, z, yaw, speed) in its order.</summary>
        void ApplyShared(List<object> cars)
        {
            if (owner || cars == null) return;
            var list = SharedCars();
            for (int i = 0; i < list.Count && i < cars.Count; i++)
            {
                var c = list[i];
                if (!c || !(cars[i] is List<object> s) || s.Count < 5) continue;
                if (!c.Remote) c.SetRemote(true);
                bool first = !c.HasRemoteState;
                c.ApplyRemote(new Vector3(F(s[0]), F(s[1]), F(s[2])), F(s[3]), F(s[4]));
                if (first) Show(c, true);
            }
        }

        static float F(object o) => o is double d ? (float)d : o is long l ? l : o is int i ? i : 0f;

        /// <summary>chase.party.owner: the owner left; the named member's client takes the HUV over from where it is.</summary>
        void SetOwner(bool mine)
        {
            if (mine == owner) return;
            owner = mine;
            if (!mine) { MakeRemote(); return; }
            foreach (var c in SharedCars())
            {
                if (!c) continue;
                Show(c, true);
                c.SetRemote(false);
            }
        }

        /// <summary>chase.party.split: the server's kept-HUV stages (start, out with the units, the truck finished).</summary>
        void OnSplit(Dictionary<string, object> m)
        {
            var bossT = pack.FirstOrDefault();
            switch (m.S("stage"))
            {
                case "start":
                    splitStarted = true;
                    break;
                case "out":
                    splitStarted = true;
                    foreach (var o in m.L("units") ?? new List<object>())
                        if (o is Dictionary<string, object> u && u.I("huv") == Convoy.LilGrimer) LilOut(u.I("unit"), (float)u.N("hp"));
                    if (bossT != null && bossT.Convoy && !bossT.Convoy.Split && (m.L("escorts")?.Count ?? 0) > 0)
                    {
                        bossT.Convoy.SplitNow();
                        if (!IsOwner) MakeRemote();
                    }
                    break;
                case "finish":
                    if (lil != null && lil.Car) lil.Car.ClosePose = true;
                    truckFinished = true;
                    finishIn = -1f;
                    break;
            }
        }

        bool ended;

        /// <summary>CHASEPLAYINFO CANCEL confirmed (10727): the chase is given up (0x554850 sends the quit packet).</summary>
        public void Quit()
        {
            if (ended) return;
            if (Party && OnlineSession.Active) OnlineSession.Instance.Net.Request("chase.party.quit", new Dictionary<string, object>(), null);
            End(false);
        }

        void End(bool won, List<ChaseScreens.ResultRow> partyRows = null, Dictionary<string, object> driver = null)
        {
            if (ended) return;
            ended = true;
            Mode = won ? 6 : 7;
            if (Active == this) Active = null;
            var ui = UiManager.Instance;
            if (ui) { if (playInfo) ui.Close(playInfo); if (chaseInfo) ui.Close(chaseInfo); }
            var last = Target ?? pack.LastOrDefault(t => t.Car);
            var huvTransform = last != null && last.Car ? last.Car.transform : null;
            if (TeamRoo) StepQuestRunner.RooChaseBanner(won, huvTransform);
            else GameEffects.ChaseEnded(won, huvTransform);   // arrest banner, sound, huv_explosion (0x6f7d80)
            foreach (var t in pack) if (!t.Stopped) t.Remove(won);
            var p = PlayerProfile.Current;
            float seconds = TimeLimit - timeLeft;
            // quest progress first: the reward reply (online) brings back the driver and must already include it
            Finished?.Invoke(won);
            Finished = null;
            if (!won) ChatBox.System(MissionText.Get("10619"));
            if (partyRows != null)
            {
                // the server paid every member (0x4f9dcd: my line in the chat, all lines in PARTYCHASERESULT)
                if (driver != null) OnlineSession.ApplyDriver(driver);
                var mine = partyRows.FirstOrDefault(r => r.Name == p?.Name);
                if (won)
                {
                    ChatBox.System(MissionText.Get("10617"));
                    if (mine != null) { ChatBox.System(MissionText.Get("10618", mine.Mito, mine.Exp)); ChaseScreens.RewardPopup(mine.Exp, mine.Mito); }
                }
                LastRows = partyRows;
                ChaseScreens.PartyResult(seconds, partyRows);
            }
            else if (won && p != null)
            {
                bool isBoss = boss;
                var data = new Dictionary<string, object> { ["boss"] = isBoss, ["seconds"] = (double)seconds, ["huvs"] = pack.Count };
                Progress.Reward("chase", data, () =>
                {
                    long exp = MitaDrink.Apply((isBoss ? 1500 : 300) + 60L * p.Level), mito = (isBoss ? 12000 : 2500) + 400L * p.Level;
                    int before = p.Level;
                    p.AddExp(exp); p.Mito += mito; p.HuvCaught++; if (isBoss) p.BossesCaught++;
                    return new Progress.Result { Ok = true, Exp = exp, Mito = mito, LevelUps = p.Level - before };
                }, r =>
                {
                    if (!r.Ok) { UiPopup.Message($"HUV {info?.UiName} stopped! (no reward: {r.Error})"); return; }
                    ChatBox.System(MissionText.Get("10617"));
                    ChatBox.System(MissionText.Get("10618", r.Mito, r.Exp));
                    ChaseScreens.RewardPopup(r.Exp, r.Mito);
                    var me = PlayerProfile.Current;
                    LastRows = new List<ChaseScreens.ResultRow> { new ChaseScreens.ResultRow { Name = me?.Name, Level = me?.Level ?? 0, Exp = r.Exp, Mito = r.Mito, LevelUps = r.LevelUps } };
                    ChaseScreens.Result(seconds, r.Exp, r.Mito);
                });
            }
            if (GameFlow.Instance) AudioLibrary.PlayMusic(GameFlow.Instance, CityMusic.For(GameFlow.Instance.City));
            Destroy(gameObject, 0.1f);
        }

        /// <summary>Destroyed without finishing (travel, logout, a race taking over): remove everything it made.</summary>
        void OnDestroy()
        {
            if (Active == this) Active = null;
            if (ended) return;
            ended = true;
            if (Party && OnlineSession.Active && OnlineSession.Instance && OnlineSession.Instance.Net)
                OnlineSession.Instance.Net.Request("chase.party.quit", new Dictionary<string, object>(), null);
            foreach (var t in pack)
            {
                if (t.Convoy) Destroy(t.Convoy.gameObject);
                if (!t.Stopped) t.Remove(false);
            }
            var ui = UiManager.Instance;
            if (ui) { if (playInfo) ui.Close(playInfo); if (chaseInfo) ui.Close(chaseInfo); }
        }

        // ---------------- the call (CHASING 870000) ----------------

        /// <summary>
        /// The HUV call (870000 / 880000). Accepting starts the chase - online in a party, the leader's acceptance asks the
        /// server to start it for the whole party.
        /// </summary>
        public static NuiView Offer(CitySession session, bool boss = false, Action refused = null)
        {
            if (Active || !session) return null;
            return ChaseScreens.Offer(() => StartFromCall(session, boss), refused);
        }

        /// <summary>The call's Tab: a party chase online (leader), else a solo chase.</summary>
        public static void StartFromCall(CitySession session, bool boss)
        {
            if (Active || !session) return;
            if (OnlineSession.Active && Social.PartyWindow.InParty)
            {
                if (!Social.PartyWindow.IAmLeader) { UiPopup.Message(MissionText.Get("20022")); return; }
                MissionNet.Ensure();
                int level = PlayerProfile.Current?.Level ?? 1;
                var pick = HuvTarget.Pick(boss, level);
                if (pick == null) return;
                OnlineSession.Instance.Net.Request("chase.party.start", new Dictionary<string, object>
                {
                    ["boss"] = boss, ["huv"] = HuvTarget.IndexOf(pick, boss), ["hp"] = (double)(boss ? ChaseRules.BossHp : 100f + pick.Crash), ["bossId"] = boss ? pick.UniqueId : 0,
                }, r => { if (r == null || !r.B("ok")) UiPopup.Message(r?.S("error", "The party chase could not start.") ?? "No answer from the server."); });
                return;
            }
            session.StartChase(boss);
        }

        // ---------------- party chase pushes (src/partyplay.js) ----------------

        /// <summary>chase.party.start / .hp / .huv / .owner / .split / .end from the server (MissionNet routes them here).</summary>
        public static void OnPartyPush(string t, Dictionary<string, object> m)
        {
            switch (t)
            {
                case "chase.party.start":
                {
                    var session = CitySession.Current;
                    if (!session || !session.Car) return;
                    if (Active && !Active.Party) Destroy(Active.gameObject);
                    // missions that a chase gives up
                    if (DeliveryService.Active) DeliveryService.Active.Abandon();
                    // BS_PktPartyHuv names the HUV's owner (0x4f0940): the remake's server names the leader
                    string ownerName = m.S("owner", m.S("leader"));
                    bool mine = string.IsNullOrEmpty(ownerName) || ownerName == PlayerProfile.Current?.Name;
                    var plan = ChaseRules.FromNet(m);
                    if (plan.Units.Count == 0) plan = null;
                    var c = Begin(session.Traffic, session.Car, m.B("boss"), m.I("huv"), (float)m.N("seconds", 180), m.I("id"), (float)m.N("hp"), mine, plan);
                    if (c) c.Finished += won => QuestTracker.Notify(won ? QuestEvent.HuvCaught : QuestEvent.HuvEscaped);
                    else OnlineSession.Instance?.Net.Request("chase.party.quit", new Dictionary<string, object>(), null);
                    break;
                }
                case "chase.party.hp":
                {
                    var a = Active;
                    if (!a || !a.Party || a.partyId != m.I("id")) return;
                    var target = a.UnitTarget(m.I("unit"));
                    if (target == null) return;
                    target.Health = (float)m.N("hp");
                    target.MaxHealth = Mathf.Max(target.MaxHealth, (float)m.N("max", target.MaxHealth));
                    // a unit the server stopped stands and goes; the others run on (the server ends the chase)
                    if (target.Health <= 0 && !target.Stopped && a.pack.Any(x => x != target && !x.Stopped && x.Car))
                    {
                        GameEffects.ChaseEnded(true, target.Car ? target.Car.transform : null);
                        target.Remove(true);
                    }
                    break;
                }
                case "chase.party.split":
                {
                    var a = Active;
                    if (a && a.Party && a.partyId == m.I("id")) a.OnSplit(m);
                    break;
                }
                case "chase.party.huv":
                {
                    var a = Active;
                    if (a && a.Party && a.partyId == m.I("id")) a.ApplyShared(m.L("cars"));
                    break;
                }
                case "chase.party.owner":
                {
                    var a = Active;
                    if (a && a.Party && a.partyId == m.I("id")) a.SetOwner(m.S("owner") == PlayerProfile.Current?.Name);
                    break;
                }
                case "chase.party.end":
                {
                    var a = Active;
                    var rows = new List<ChaseScreens.ResultRow>();
                    foreach (var o in m.L("rows") ?? new List<object>())
                        if (o is Dictionary<string, object> r)
                            rows.Add(new ChaseScreens.ResultRow { Name = r.S("name"), Level = r.I("level"), Exp = (long)r.N("exp"), Mito = (long)r.N("mito"), LevelUps = r.I("levelUps") });
                    if (a && a.Party && a.partyId == m.I("id")) a.End(m.B("success"), rows, m.O("driver"));
                    else if (m.O("driver") != null) OnlineSession.ApplyDriver(m.O("driver"));
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Routes the server's mission pushes (chase.party.*, rush.*, vhuv.*) to the chase classes: hooks NetClient.Message of the current
    /// online session (re-hooked when the session's connection changes).
    /// </summary>
    public sealed class MissionNet : MonoBehaviour
    {
        static MissionNet instance;
        NetClient hooked;

        public static void Ensure()
        {
            if (instance) return;
            var go = new GameObject("Mission Net");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<MissionNet>();
            instance.Update();
        }

        void Update()
        {
            var s = OnlineSession.Instance;
            var net = s ? s.Net : null;
            if (net == hooked) return;
            if (hooked) hooked.Message -= OnMessage;
            hooked = net;
            if (hooked) hooked.Message += OnMessage;
        }

        void OnDestroy() { if (hooked) hooked.Message -= OnMessage; if (instance == this) instance = null; }

        static void OnMessage(string t, Dictionary<string, object> m)
        {
            if (t == null) return;
            try
            {
                if (t.StartsWith("chase.party.")) ChaseMission.OnPartyPush(t, m);
                else if (t.StartsWith("rush.")) RushTime.OnPush(t, m);
                else if (t.StartsWith("vhuv.")) VirtualChase.OnPush(t, m);
                // a member's speed combo rose (src/partyplay.js party.combo, rise = the tricks they chained): SPEEDCOMBO's
                // PARTYNUM / PARTYCOMBO display (0x4ed43c); PartyWindow keeps the bar's party total
                else if (t == "party.combo" && m.S("name") != PlayerProfile.Current?.Name && DriftCombo.Current)
                    DriftCombo.Current.AddFromParty(Mathf.Clamp(m.I("rise"), 0, 3));
            }
            catch (Exception ex) { Debug.LogException(ex); }
        }
    }
}
