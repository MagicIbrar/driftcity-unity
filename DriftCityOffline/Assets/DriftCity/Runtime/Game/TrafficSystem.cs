using System.Collections.Generic;
using System.Linq;
using DriftCity.Core;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// City traffic on the lane network from &lt;city&gt;.tcs. Cars are kinematic lane followers that
    /// brake for whatever is ahead; when hit hard they become physical wrecks for a while.
    /// Cars far from the player are recycled onto lanes near the player.
    /// </summary>
    public sealed class TrafficSystem : MonoBehaviour
    {
        public int MaxCars = 28;
        public float SpawnMin = 70f, SpawnMax = 320f, DespawnDistance = 480f;
        public Transform Player;
        public TrafficNetwork Network { get; private set; }

        readonly List<TrafficCar> cars = new List<TrafficCar>();
        readonly List<GameObject> templates = new List<GameObject>();
        readonly Dictionary<string, GameObject> tireModels = new Dictionary<string, GameObject>(System.StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, GameObject> builtTemplates = new Dictionary<string, GameObject>(System.StringComparer.OrdinalIgnoreCase);
        GameObject templateRoot;

        public void Init(string city, Transform player)
        {
            Player = player;
            var fs = DriftCityData.Files;
            var path = $@"{city}\{city}.tcs";
            Network = fs.Exists(path) ? TrafficNetwork.Parse(fs.Read(path)) : new TrafficNetwork();
            templateRoot = new GameObject("Traffic Templates");
            templateRoot.transform.SetParent(transform, false);
            templateRoot.SetActive(false);
            var (models, count) = GameTables.TrafficFor(city);
            if (models.Count > 0)
            {
                // the city's own traffic list; a model's spawn frequency is how many template slots it gets
                if (count > 0) MaxCars = count;
                foreach (var (nif, weight) in models)
                {
                    var built = Template(nif);
                    if (!built) continue;
                    for (int i = 0; i < weight; i++) templates.Add(built);
                }
            }
            else
                foreach (var f in GameTables.TrafficModels.OrderBy(_ => Random.value).Take(10))
                {
                    var built = Template(f);
                    if (built) templates.Add(built);
                }
            Debug.Log($"Drift City: traffic on {Network.Lanes.Count} lanes with {templates.Count} models");
        }

        /// <summary>A traffic / HUV body with its wheels, built once per model.</summary>
        GameObject Template(string nif)
        {
            if (builtTemplates.TryGetValue(nif, out var built) && built) return built;
            try { built = TrafficRig.Build(nif, templateRoot.transform, TireModel); }
            catch (System.Exception ex) { Debug.LogWarning($"traffic model {nif}: {ex.Message}"); return null; }
            builtTemplates[nif] = built;
            return built;
        }

        /// <summary>One loaded copy of each tire model; the wheels are instances of it (sharing its meshes).</summary>
        GameObject TireModel(string nif)
        {
            if (tireModels.TryGetValue(nif, out var model) && model) return model;
            model = new NifSceneBuilder { LogErrors = false }.Load(nif, templateRoot.transform);
            tireModels[nif] = model;
            return model;
        }

        /// <summary>
        /// A car handed in by a mission (an HUV assembled by CarFactory) is rebuilt from its own NIF with the client's
        /// wheel setup when that file is known; anything else is used as given.
        /// </summary>
        GameObject Rigged(GameObject template)
        {
            if (!templateRoot || template.GetComponent<TrafficRig>() || !template.transform.Find("Body")) return template;
            string model = template.name.StartsWith("Car ") ? template.name.Substring(4) : template.name;
            foreach (var folder in new[] { "huv", "traffic" })
            {
                string nif = $@"vehicle\{folder}\{model}.nif";
                if (DriftCityData.Files.Exists(nif) && GameTables.TrafficWheels(nif) != null)
                    return Template(nif) ?? template;
            }
            return template;
        }

        void Update()
        {
            if (Network == null || Network.Lanes.Count == 0 || templates.Count == 0 || !Player) return;
            for (int i = cars.Count - 1; i >= 0; i--)
            {
                var c = cars[i];
                if (!c) { cars.RemoveAt(i); continue; }
                if ((c.transform.position - Player.position).sqrMagnitude > DespawnDistance * DespawnDistance) Recycle(c);
            }
            if (cars.Count < MaxCars) TrySpawn(out _);
        }

        public bool TrySpawn(out TrafficCar car, GameObject template = null, float minDist = -1, float maxDist = -1)
        {
            car = null;
            if (!TryPickLane(minDist < 0 ? SpawnMin : minDist, maxDist < 0 ? SpawnMax : maxDist, out int lane, out float along)) return false;
            var tpl = template ? Rigged(template) : templates[Random.Range(0, templates.Count)];
            var go = Instantiate(tpl, transform);
            go.SetActive(true);
            go.name = "Traffic " + tpl.name;
            car = go.AddComponent<TrafficCar>();
            car.Setup(this, lane, along, Random.Range(38f, 62f));
            if (!template) cars.Add(car);
            return true;
        }

        /// <summary>A car of a given traffic / HUV body (with the client's wheel setup) at a lane position; not counted in
        /// the city's traffic (convoy escorts, scripted cars).</summary>
        public TrafficCar SpawnModel(string nif, int lane, float along, float cruiseKmh)
        {
            if (Network == null || lane < 0 || lane >= Network.Lanes.Count) return null;
            if (!templateRoot) { templateRoot = new GameObject("Traffic Templates"); templateRoot.transform.SetParent(transform, false); templateRoot.SetActive(false); }
            var tpl = Template(nif);
            if (!tpl) return null;
            var go = Instantiate(tpl, transform);
            go.SetActive(true);
            go.name = "Traffic " + tpl.name;
            var car = go.AddComponent<TrafficCar>();
            car.Setup(this, lane, along, cruiseKmh);
            return car;
        }

        void Recycle(TrafficCar c)
        {
            if (TryPickLane(SpawnMin, SpawnMax, out int lane, out float along)) c.Place(lane, along);
        }

        /// <summary>A lane point between minDist and maxDist from the player (and not right in front of the camera).</summary>
        public bool TryPickLane(float minDist, float maxDist, out int lane, out float along)
        {
            var lanes = Network.Lanes;
            var p = Player.position;
            // random probes first, then widen the ring until something qualifies
            for (int pass = 0; pass < 4; pass++)
            {
                float lo = pass == 0 ? minDist : minDist * 0.5f, hi = maxDist * (1f + pass);
                for (int attempt = 0; attempt < 400; attempt++)
                {
                    int i = Random.Range(0, lanes.Count);
                    var pts = lanes[i].Points;
                    int k = Random.Range(0, pts.Length - 1);
                    var w = DriftCityData.ToUnity(pts[k]);
                    float d = Vector3.Distance(w, p);
                    if (d < lo || d > hi) continue;
                    lane = i;
                    along = 0;
                    for (int s = 1; s <= k; s++) along += Vector3.Distance(DriftCityData.ToUnity(pts[s - 1]), DriftCityData.ToUnity(pts[s]));
                    return true;
                }
            }
            lane = -1; along = 0;
            return false;
        }

        public IReadOnlyList<TrafficCar> Cars => cars;

        /// <summary>The city's traffic bodies in their fixed order (one entry per spawn weight; PvP path traffic picks by index).</summary>
        public int TemplateCount => templates.Count;
        public GameObject TemplateAt(int index) => templates.Count == 0 ? null : templates[((index % templates.Count) + templates.Count) % templates.Count];

        /// <summary>Sends cars beyond <paramref name="max"/> away (farthest from the player first), e.g. to clear a race.</summary>
        public void TrimTo(int max)
        {
            if (!Player) return;
            cars.RemoveAll(c => !c);
            cars.Sort((a, b) => Vector3.SqrMagnitude(b.transform.position - Player.position).CompareTo(Vector3.SqrMagnitude(a.transform.position - Player.position)));
            while (cars.Count > Mathf.Max(0, max))
            {
                Destroy(cars[0].gameObject);
                cars.RemoveAt(0);
            }
        }

        /// <summary>The lane point closest to <paramref name="pos"/> (optionally on a different lane than <paramref name="exceptLane"/>).</summary>
        public bool NearestLane(Vector3 pos, out int lane, out float along, int exceptLane = -1, float maxDistance = 120f)
        {
            lane = -1; along = 0;
            float best = maxDistance * maxDistance;
            var lanes = Network.Lanes;
            for (int i = 0; i < lanes.Count; i++)
            {
                if (i == exceptLane) continue;
                var pts = lanes[i].Points;
                float acc = 0;
                for (int k = 0; k < pts.Length; k++)
                {
                    var w = DriftCityData.ToUnity(pts[k]);
                    if (k > 0) acc += Vector3.Distance(DriftCityData.ToUnity(pts[k - 1]), w);
                    float d = (w - pos).sqrMagnitude;
                    if (d < best) { best = d; lane = i; along = acc; }
                }
            }
            return lane >= 0;
        }

        /// <summary>Position and heading at a distance along a lane (Unity space).</summary>
        public bool Sample(int lane, float along, out Vector3 pos, out Vector3 fwd)
        {
            var pts = Network.Lanes[lane].Points;
            pos = DriftCityData.ToUnity(pts[0]);
            fwd = Vector3.forward;
            for (int i = 1; i < pts.Length; i++)
            {
                var a = DriftCityData.ToUnity(pts[i - 1]);
                var b = DriftCityData.ToUnity(pts[i]);
                float seg = Vector3.Distance(a, b);
                if (along <= seg || i == pts.Length - 1)
                {
                    float t = seg > 0.001f ? Mathf.Clamp01(along / seg) : 0;
                    pos = Vector3.Lerp(a, b, t);
                    fwd = (b - a).sqrMagnitude > 1e-6f ? (b - a).normalized : fwd;
                    return along <= seg;
                }
                along -= seg;
            }
            return false;
        }

        /// <summary>Successor preferring the straightest continuation (with some randomness).</summary>
        public int NextLane(int lane, bool erratic = false)
        {
            var l = Network.Lanes[lane];
            if (l.Next.Count == 0) return -1;
            if (erratic) return l.Next[Random.Range(0, l.Next.Count)];
            var endDir = Dir(l.Points, l.Points.Length - 2);
            int best = l.Next[0]; float bestScore = float.MinValue;
            foreach (int n in l.Next)
            {
                float score = Vector3.Dot(endDir, Dir(Network.Lanes[n].Points, 0)) + Random.Range(0f, 0.8f);
                if (score > bestScore) { bestScore = score; best = n; }
            }
            return best;
        }

        static Vector3 Dir(Core.Nif.Vec3[] pts, int i)
        {
            var a = DriftCityData.ToUnity(pts[Mathf.Clamp(i, 0, pts.Length - 2)]);
            var b = DriftCityData.ToUnity(pts[Mathf.Clamp(i + 1, 1, pts.Length - 1)]);
            return (b - a).normalized;
        }
    }

    /// <summary>One lane-following vehicle (traffic or a fleeing HUV).</summary>
    public sealed class TrafficCar : MonoBehaviour
    {
        public float CruiseKmh = 50f;
        public bool Erratic;          // HUVs pick random turns
        public float Speed;           // m/s
        public int Lane;
        public float Along;
        public bool Wrecked => wreckTimer > 0;
        public event System.Action<Collision> Hit;
        /// <summary>Convoy escort: drive the leader's lanes (0x445090 copies the leader's route); with <see cref="FollowGap"/>
        /// above 0 also hold that many metres behind it (a remake option; the exe's escorts drive at their own speed).</summary>
        public TrafficCar FollowTarget;
        public float FollowGap = 0f;
        /// <summary>The car this one drives behind in a convoy (kept out of, whatever the cast sees).</summary>
        public TrafficCar Ahead;
        static readonly RaycastHit[] castHits = new RaycastHit[16];
        /// <summary>Lanes this car drove, newest last (kept while someone follows it).</summary>
        internal readonly List<int> LaneTrail = new List<int>();
        /// <summary>
        /// Planned lanes to take next, front first (an HUV's route, TRAFFIC.md section 15: the client's AI drives a road
        /// route planned to a destination, 0x44a940 / 0x4442b0); each lane end takes the front one.
        /// </summary>
        public readonly List<int> Route = new List<int>();
        /// <summary>Called when the planned route ran out or broke (a crash, a dead end): return true when it planned a new one.</summary>
        public System.Func<TrafficCar, bool> Replan;
        /// <summary>A short burst of speed (the HUV AI's turbo / on-hit acceleration, 0x492830 -> 0x4934c0).</summary>
        public float BoostKmh;
        float boostLeft;
        /// <summary>
        /// A dead-reckoned copy of a car another client simulates (party HUVs, TRAFFIC.md section 14: the owner's client runs
        /// the AI, the other members show a remote actor, 0x4af980). No lane following, never knocked out of its line.
        /// </summary>
        public bool Remote { get; private set; }
        /// <summary>
        /// An HUV's pose levels (0x492be0 picks the mode +0x728 from the distance to the player's car, 0x492d50 applies it;
        /// only while a frame takes under 0.08 s): under 100 m mode 2 (0x491720: the body on the plane through the four
        /// wheels' ground hits, 0x4a2960 / 0x4a23d0, with the wheel suspension), 100-200 m mode 1 (0x492180: one ground probe
        /// under the centre, 0x4a22a0, the body aligned to that normal with its heading), beyond 200 m mode 0 (0x49ea00: the
        /// path follower's own frame, no ground probe). The exe's modes 1 / 2 are these pose levels, not a dodging driver.
        /// </summary>
        public bool HuvPose;
        /// <summary>Always the close pose (0x492be0: 11002 out of the trailer, AI +0x724).</summary>
        public bool ClosePose;
        /// <summary>Never the close poses (0x492d50 leaves 11009, the hovercraft 0x2b01, on its path frame).</summary>
        public bool PathPoseOnly;
        /// <summary>The HUV pose mode of the last step (0 path frame, 1 centre probe, 2 four wheels).</summary>
        public int PoseMode { get; private set; } = 2;
        public const float ClosePoseDistance = 100f, MidPoseDistance = 200f;
        /// <summary>True once a remote state arrived (a remote car waits hidden until then).</summary>
        public bool HasRemoteState => hasRemote;
        bool hasRemote;
        Vector3 remotePos;
        Quaternion remoteRot = Quaternion.identity;
        float remoteSpeed, remoteAt;

        TrafficSystem sys;
        Rigidbody rb;
        TrafficRig rig;
        Quaternion heading = Quaternion.identity;
        int probeTick;
        float wreckTimer;
        Vector3 halfExtents = new Vector3(1, 0.8f, 2.3f);

        // cars further than this from the player probe the ground every 4th step only
        const float NearProbe = 160f;

        public void Setup(TrafficSystem system, int lane, float along, float cruiseKmh)
        {
            sys = system;
            CruiseKmh = cruiseKmh;
            rb = gameObject.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.mass = 1500f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            var rs = GetComponentsInChildren<Renderer>();
            if (rs.Length > 0)
            {
                var b = rs[0].bounds; foreach (var r in rs) b.Encapsulate(r.bounds);
                var box = gameObject.AddComponent<BoxCollider>();
                box.center = transform.InverseTransformPoint(b.center);
                box.size = b.size;
                halfExtents = b.extents;
            }
            rig = GetComponent<TrafficRig>();
            if (!rig)
            {
                rig = gameObject.AddComponent<TrafficRig>();
                var box = GetComponent<BoxCollider>();
                rig.CornersFromBox(box ? box.center : Vector3.zero, box ? box.size : halfExtents * 2f);
            }
            probeTick = Random.Range(0, 4);
            Place(lane, along);
        }

        /// <summary>The lane this car took after <paramref name="lane"/>, if it is in its recent trail.</summary>
        public int NextAfter(int lane)
        {
            for (int i = LaneTrail.Count - 2; i >= 0; i--)
                if (LaneTrail[i] == lane) return LaneTrail[i + 1];
            // not driven yet: the lane it plans to take after it (a follower copies the leader's route, 0x445090)
            if (lane == Lane && Route.Count > 0) return Route[0];
            int k = Route.IndexOf(lane);
            return k >= 0 && k + 1 < Route.Count ? Route[k + 1] : -1;
        }

        public void Place(int lane, float along)
        {
            // a remote copy goes where its owner says, not where local code puts it
            if (Remote && hasRemote) return;
            Lane = lane; Along = along;
            LaneTrail.Clear();
            LaneTrail.Add(lane);
            wreckTimer = 0;
            rb.isKinematic = true;
            Speed = CruiseKmh / 3.6f * 0.6f;
            if (sys.Sample(lane, along, out var pos, out var fwd))
            {
                heading = Quaternion.LookRotation(Flat(fwd), Vector3.up);
                rig.ResetAt(pos, fwd, heading);
                rig.Follow(pos, fwd, heading, 0f, 0f, true, out var p, out var r);
                transform.SetPositionAndRotation(p, r);
            }
            // a planned route starts from the lane the car was on: plan again from here
            if (Route.Count > 0 || Replan != null) { Route.Clear(); Replan?.Invoke(this); }
        }

        /// <summary>The burst of speed of the HUV AI (turbo, on hit): at least <paramref name="kmh"/> for <paramref name="seconds"/>.</summary>
        public void Boost(float kmh, float seconds)
        {
            if (kmh <= 0 || seconds <= 0) return;
            BoostKmh = boostLeft > 0 ? Mathf.Max(BoostKmh, kmh) : kmh;
            boostLeft = Mathf.Max(boostLeft, seconds);
        }

        /// <summary>
        /// Makes this a remote copy (dead-reckoned from <see cref="ApplyRemote"/>) or hands it back to the local AI, which
        /// carries on from where the copy is (the party HUV's owner left: the next member simulates it).
        /// </summary>
        public void SetRemote(bool on)
        {
            if (on == Remote) return;
            Remote = on;
            if (on)
            {
                wreckTimer = 0;
                if (rb) rb.isKinematic = true;
                Route.Clear();
                return;
            }
            float v = remoteSpeed;
            hasRemote = false;
            if (sys && sys.Network != null && sys.NearestLane(transform.position, out int lane, out float along, -1, 150f)) Place(lane, along);
            Speed = Mathf.Max(Speed, v);
        }

        /// <summary>The owner's latest state of a remote copy: Unity position, yaw (degrees) and speed (m/s).</summary>
        public void ApplyRemote(Vector3 pos, float yawDeg, float speed)
        {
            bool first = !hasRemote;
            remotePos = pos;
            remoteRot = Quaternion.Euler(0f, yawDeg, 0f);
            remoteSpeed = Mathf.Max(0f, speed);
            remoteAt = Time.time;
            hasRemote = true;
            if (!first) return;
            heading = remoteRot;
            var f = remoteRot * Vector3.forward;
            if (rig)
            {
                rig.ResetAt(pos, f, heading);
                rig.Follow(pos, f, heading, 0f, 0f, true, out var p, out var r);
                transform.SetPositionAndRotation(p, r);
            }
            else transform.SetPositionAndRotation(pos, remoteRot);
            if (rb) { rb.position = transform.position; rb.rotation = transform.rotation; }
        }

        /// <summary>What the owner sends for this car: x, y, z (Unity), yaw (degrees), speed (m/s).</summary>
        public double[] NetState()
        {
            var p = transform.position;
            return new double[] { System.Math.Round(p.x, 2), System.Math.Round(p.y, 2), System.Math.Round(p.z, 2), System.Math.Round(heading.eulerAngles.y, 1), System.Math.Round(Speed, 2) };
        }

        /// <summary>Remote copy: the owner's last state carried forward at its speed, pulled in smoothly, on the ground.</summary>
        void RemoteStep(float dt)
        {
            if (!hasRemote || !rb) return;
            float age = Mathf.Min(Time.time - remoteAt, 1.5f);
            var dir = remoteRot * Vector3.forward;
            var predicted = remotePos + dir * remoteSpeed * age;
            var cur = transform.position;
            var target = (predicted - cur).sqrMagnitude > 40f * 40f ? predicted : Vector3.Lerp(cur, predicted, 1f - Mathf.Exp(-6f * dt));
            target.y = predicted.y;
            heading = Quaternion.Slerp(heading, remoteRot, 1f - Mathf.Exp(-8f * dt));
            Speed = remoteSpeed;
            Vector3 p; Quaternion rot;
            if (HuvPose) HuvFollow(target, heading * Vector3.forward, dt, out p, out rot);
            else
            {
                bool probe = (probeTick++ & 3) == 0 || !sys || !sys.Player || (target - sys.Player.position).sqrMagnitude < NearProbe * NearProbe;
                rig.Follow(target, heading * Vector3.forward, heading, Speed, dt, probe, out p, out rot);
            }
            rb.MovePosition(p);
            rb.MoveRotation(rot);
        }

        /// <summary>0x492be0: the pose mode for the distance to the player's car (and the frame time).</summary>
        public int PoseModeFor(float distance, float frameSeconds)
        {
            if (PathPoseOnly) return 0;
            int mode = 0;
            if (frameSeconds < 0.08f) mode = distance < ClosePoseDistance ? 2 : distance < MidPoseDistance ? 1 : 0;
            if (ClosePose) mode = 2;
            return mode;
        }

        static readonly RaycastHit[] poseHits = new RaycastHit[8];

        /// <summary>0x492d50: the HUV's pose by its mode (four wheels / centre probe / the path's own frame).</summary>
        void HuvFollow(Vector3 pos, Vector3 fwd, float dt, out Vector3 p, out Quaternion rot)
        {
            float dist = sys && sys.Player ? Vector3.Distance(pos, sys.Player.position) : 0f;
            int mode = PoseModeFor(dist, Time.smoothDeltaTime);
            if (mode == 2)
            {
                // 0x491720: the plane through the four wheels' ground hits, every step, with the wheel springs
                if (PoseMode != 2) rig.ResetAt(pos, fwd, heading);
                rig.Follow(pos, fwd, heading, Speed, dt, true, out p, out rot);
            }
            else if (mode == 1)
            {
                // 0x492180 / 0x4a22a0: one probe under the centre (6 m, then far), the body on that normal facing its heading
                var n = Vector3.up; float y = pos.y;
                int k = Physics.RaycastNonAlloc(pos + Vector3.up * 6f, Vector3.down, poseHits, 26f, ~0, QueryTriggerInteraction.Ignore);
                float best = float.MaxValue;
                for (int i = 0; i < k; i++)
                {
                    if (poseHits[i].collider.attachedRigidbody || poseHits[i].distance >= best) continue;
                    best = poseHits[i].distance; y = poseHits[i].point.y; n = poseHits[i].normal;
                }
                var f = Vector3.ProjectOnPlane(heading * Vector3.forward, n);
                p = new Vector3(pos.x, y, pos.z);
                rot = Quaternion.LookRotation(f.sqrMagnitude > 1e-6f ? f.normalized : heading * Vector3.forward, n);
            }
            else
            {
                // 0x49ea00: the path follower's frame (the lane's point and slope), no probe
                var flat = heading * Vector3.forward;
                float rise = Mathf.Clamp(fwd.y, -0.5f, 0.5f);
                p = pos;
                rot = Quaternion.LookRotation(flat * Mathf.Sqrt(1f - rise * rise) + Vector3.up * rise, Vector3.up);
            }
            PoseMode = mode;
        }

        static Vector3 Flat(Vector3 v) { v.y = 0; return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward; }

        void FixedUpdate()
        {
            if (Remote) { RemoteStep(Time.fixedDeltaTime); return; }
            if (wreckTimer > 0)
            {
                wreckTimer -= Time.fixedDeltaTime;
                if (wreckTimer <= 0) Recover();
                return;
            }
            float target = CruiseKmh / 3.6f;
            if (boostLeft > 0) { boostLeft -= Time.fixedDeltaTime; target = Mathf.Max(target, BoostKmh / 3.6f); }
            if (FollowTarget && !FollowTarget.Wrecked && FollowGap > 0)
            {
                // (remake option) hold a gap behind the car ahead: its speed plus a correction for the distance error
                float gapNow = Vector3.Distance(transform.position, FollowTarget.transform.position);
                target = Mathf.Clamp(FollowTarget.Speed + (gapNow - FollowGap) * 0.5f, 0f, FollowTarget.CruiseKmh / 3.6f * 1.6f);
            }
            // brake for any car in front of us (the nearest body hit; the road or a wall in the cast, on a rise, does not
            // hide the cars behind it)
            var origin = transform.position + Vector3.up * 0.8f;
            float look = 6f + Speed * 1.4f;
            int n = Physics.BoxCastNonAlloc(origin, new Vector3(halfExtents.x * 0.8f, 0.4f, 0.5f), transform.forward, castHits, transform.rotation, halfExtents.z + look);
            float nearest = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var h = castHits[i];
                if (h.distance <= 0f || !h.rigidbody || h.rigidbody == rb) continue;
                if (h.distance < nearest) nearest = h.distance;
            }
            if (nearest < float.MaxValue) target = Mathf.Min(target, Mathf.Max(0, nearest - halfExtents.z - 4f) * 0.6f);
            // a convoy keeps out of the car it drives behind (the exe's escorts bump into it physically)
            if (Ahead && Ahead != this && !Ahead.Remote)
            {
                var d = Ahead.transform.position - transform.position;
                float ahead = Vector3.Dot(d, transform.forward);
                var side = d - transform.forward * ahead; side.y = 0;
                if (ahead > 0 && ahead < look + 30f && side.magnitude < 6f)
                {
                    float gap = ahead - halfExtents.z - Ahead.halfExtents.z;
                    target = Mathf.Min(target, Ahead.Speed + Mathf.Max(0, gap - 6f) * 0.6f);
                    if (gap < 3f) target = Mathf.Min(target, Mathf.Max(0, Ahead.Speed - 2f));
                    // too close to brake in time: the bump takes the speed off at once (an escort starts at 40 m/s)
                    float closing = Speed - Ahead.Speed;
                    if (closing > 0 && gap < closing * closing / (2f * 9f) + 2f) Speed = Mathf.Min(Speed, Ahead.Speed + Mathf.Max(0f, gap - 2f));
                }
            }
            Speed = Mathf.MoveTowards(Speed, target, (target < Speed ? 9f : 3f) * Time.fixedDeltaTime);
            Along += Speed * Time.fixedDeltaTime;
            if (!sys.Sample(Lane, Along, out var pos, out var fwd))
            {
                int next = FollowTarget ? FollowTarget.NextAfter(Lane) : -1;
                // the planned route (an HUV's way to its destination); plan again when it ran out or no longer fits
                if (next < 0 && Route.Count > 0 && !sys.Network.Lanes[Lane].Next.Contains(Route[0])) Route.Clear();
                if (next < 0 && Route.Count == 0 && Replan != null) Replan(this);
                if (next < 0 && Route.Count > 0 && sys.Network.Lanes[Lane].Next.Contains(Route[0])) { next = Route[0]; Route.RemoveAt(0); }
                if (next < 0) next = sys.NextLane(Lane, Erratic);
                if (next < 0)
                {
                    // dead end: HUVs carry on along the nearest other lane rather than jumping somewhere else in town
                    bool found = Erratic ? HuvRoutes.LaneAt(sys, transform.position + transform.forward * 8f, transform.forward, out int nl, out float na, 120f, Lane)
                                         : sys.TryPickLane(sys.SpawnMin, sys.SpawnMax, out nl, out na);
                    if (found) Place(nl, na);
                    return;
                }
                Along -= sys.Network.Lanes[Lane].Length;
                if (Along < 0) Along = 0;
                Lane = next;
                LaneTrail.Add(Lane);
                if (LaneTrail.Count > 256) LaneTrail.RemoveAt(0);
                sys.Sample(Lane, Along, out pos, out fwd);
            }
            heading = Quaternion.Slerp(heading, Quaternion.LookRotation(Flat(fwd), Vector3.up), 8f * Time.fixedDeltaTime);
            // pitch and roll from the ground under the wheels; distant cars probe less often; an HUV by its pose mode
            Vector3 p; Quaternion rot;
            if (HuvPose) HuvFollow(pos, fwd, Time.fixedDeltaTime, out p, out rot);
            else
            {
                bool probe = (probeTick++ & 3) == 0 || !sys.Player || (pos - sys.Player.position).sqrMagnitude < NearProbe * NearProbe;
                rig.Follow(pos, fwd, heading, Speed, Time.fixedDeltaTime, probe, out p, out rot);
            }
            rb.MovePosition(p);
            rb.MoveRotation(rot);
        }

        /// <summary>After a crash: back onto the lane nearest to where the car ended up, once nothing is in the way.</summary>
        void Recover()
        {
            if (!sys.NearestLane(transform.position, out int lane, out float along)) { lane = Lane; along = Along; }
            if (sys.Sample(lane, along, out var pos, out var fwd))
            {
                // don't snap back into a car (usually the player who rammed us): stay physical a little longer
                var rot = Quaternion.LookRotation(Flat(fwd), Vector3.up);
                foreach (var hit in Physics.OverlapBox(pos + Vector3.up * halfExtents.y, halfExtents, rot))
                    if (hit.attachedRigidbody && hit.attachedRigidbody != rb) { wreckTimer = 0.5f; return; }
            }
            Place(lane, along);
        }

        void OnCollisionEnter(Collision c)
        {
            Hit?.Invoke(c);
            if (Remote) return;   // the owner's copy decides where it goes
            if (c.relativeVelocity.magnitude > 9f && c.rigidbody && !c.rigidbody.isKinematic && !Wrecked)
            {
                // knocked out of the lane: physical for a few seconds, then back to traffic
                rb.isKinematic = false;
                rb.linearVelocity = transform.forward * Speed;
                rb.AddForceAtPosition(-c.impulse, c.GetContact(0).point, ForceMode.Impulse);
                wreckTimer = 4f;
                Speed = 0;
            }
        }
    }
}
