using System;
using System.Collections.Generic;
using System.Globalization;
using DriftCity.Core;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// How many traffic cars the client asks for on a PvP track (DriftCity_RE/TRAFFIC.md section 13). At the race start
    /// (race object 0x72d250, state 6) the host picks a density from the room's TRAFFIC buttons (RANDOM: rand() % 3,
    /// NORMAL (0x8): 1, JAM (0x10): 2, anything else incl. IDLE (0x4): 0), looks the count up in data\pvptrafficnum.dat
    /// (0x73ad90 -> 0x71fc40) by city, track, density and the number of drivers in the room, takes the room's own count
    /// instead when it has one (race +0x394), halves it when an option flag is off (+0x1400 of the stage object), and
    /// sends (city, track, count, path file, seed) to the server (0x79b500). The server then runs the cars along the
    /// track's World/&lt;city&gt;/Path/PVP_map&lt;city&gt;_&lt;track&gt;.chpath paths (and, in Taipei, the stop / go points of
    /// PVP_map5_&lt;track&gt;.signal); the client only shows them as remote actors.
    /// </summary>
    public static class PvpTraffic
    {
        public const int MapTrafficIdle = 0x4, MapTrafficNormal = 0x8, MapTrafficJam = 0x10;

        /// <summary>key = city &lt;&lt; 16 | track; three rows of 32 counts (row 0 = jam, 1 = normal, 2 = idle).</summary>
        public static Dictionary<int, int[][]> Table;

        /// <summary>
        /// 0x73aaa0: a header line "%s %d %d" (Track city track), then three lines read with "%s %d x32": the first
        /// number of each line is eaten by the %s, so column n of a row is the line's (n + 2)-th number; missing ones stay 0.
        /// </summary>
        public static Dictionary<int, int[][]> Parse(string text)
        {
            var map = new Dictionary<int, int[][]>();
            int[][] cur = null; int row = 0;
            foreach (var raw in (text ?? "").Replace("\r", "").Split('\n'))
            {
                var t = raw.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length == 0) continue;
                if (cur == null || row == 3)
                {
                    if (t.Length < 3 || !int.TryParse(t[1], out int city) || !int.TryParse(t[2], out int track)) { cur = null; continue; }
                    cur = new[] { new int[32], new int[32], new int[32] };
                    map[city << 16 | track] = cur;
                    row = 0;
                    continue;
                }
                for (int i = 1; i < t.Length && i - 1 < 32; i++)
                    if (int.TryParse(t[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) cur[row][i - 1] = v; else break;
                row++;
            }
            return map;
        }

        static Dictionary<int, int[][]> Load()
        {
            if (Table != null) return Table;
            var fs = DriftCityData.Files;
            Table = fs != null && fs.Exists(@"data\pvptrafficnum.dat")
                ? Parse(System.Text.Encoding.ASCII.GetString(fs.Read(@"data\pvptrafficnum.dat")))
                : new Dictionary<int, int[][]>();
            return Table;
        }

        /// <summary>The density index for the room's map flags (rand() % 3 for RANDOM).</summary>
        public static int DensityFor(int mapFlags, Func<int> rand = null)
        {
            if ((mapFlags & (MapTrafficIdle | MapTrafficNormal | MapTrafficJam)) == 0) return (rand ?? (() => UnityEngine.Random.Range(0, 3)))() % 3;
            if ((mapFlags & MapTrafficNormal) != 0) return 1;
            if ((mapFlags & MapTrafficJam) != 0) return 2;
            return 0;
        }

        /// <summary>
        /// 0x71fc40: the count for a track (city 1..5 = moonpalace, koinonia, cras, oros, taipei; track = the track
        /// number of its PVP_map file), the density 0..2 and the drivers in the room (only the low five bits are used);
        /// (density + 1) x 100 for a track the table does not list.
        /// </summary>
        public static int Count(int city, int track, int drivers, int density, Dictionary<int, int[][]> table = null)
        {
            if (city == 0) return 0;
            table = table ?? Load();
            if (!table.TryGetValue(city << 16 | track, out var rows)) return (density + 1) * 100;
            if (drivers < 0) drivers = 0;
            return rows[((2 - density) % 3 + 3) % 3][drivers & 31];
        }

        /// <summary>The count for a room (online: RANDOM rolled from the race's seed so every client gets the same).</summary>
        public static int CountFor(int city, int track, int drivers, int mapFlags, int seed)
        {
            var rng = new PvpPathTraffic.Rng(seed ^ 0x5eed);
            return Count(city, track, drivers, DensityFor(mapFlags, () => (int)(rng.Next() & 0x7fff)));
        }
    }

    /// <summary>
    /// PvP track traffic (DriftCity_RE/TRAFFIC.md section 13): during a Battle Zone race cars drive the track's
    /// World/&lt;city&gt;/Path/PVP_map&lt;city&gt;_&lt;NN&gt;.chpath paths (open polylines, both directions of each road of the
    /// track, time keys in metres / 27.78). In the client the server ran them from the host's (city, track, count, path
    /// file, seed) and the clients showed remote actors (0x79b500). (remake rule) Here every client runs the same schedule
    /// itself from the seed and the race clock, so all clients see the same cars without network traffic: car i drives
    /// path p_i (drawn by length) from an offset at a speed per path (45-75 km/h), restarting at the path's start at its
    /// end (hidden for the last and first 25 m, where the server's cars appeared and went). Only cars within
    /// <see cref="ShowRadius"/> of the camera's car exist as bodies (the client drew remote actors only within its view).
    /// A car rammed hard is knocked about for a few seconds and then goes back to its scheduled place.
    /// </summary>
    public sealed class PvpPathTraffic : MonoBehaviour
    {
        /// <summary>A small deterministic generator (the same on every client and runtime).</summary>
        public struct Rng
        {
            uint s;
            public Rng(int seed) { s = (uint)seed * 2654435761u ^ 0x9e3779b9u; if (s == 0) s = 1; }
            public uint Next() { s ^= s << 13; s ^= s >> 17; s ^= s << 5; return s; }
            public float Value() => (Next() & 0xffffff) / 16777216f;
            public int Range(int n) => n <= 0 ? 0 : (int)(Next() % (uint)n);
        }

        public sealed class Slot
        {
            public int Path, Model;
            public float Offset;    // time key units (metres / 27.78)
            public float Speed;     // key units per second
            public PathCar Car;
        }

        public const float KeyMetres = 27.7778f;
        public static PvpPathTraffic Active { get; private set; }
        public float ShowRadius = 420f, EdgeMetres = 25f;
        public int MaxBodies = 48;
        public readonly List<TimedPath> Paths = new List<TimedPath>();
        public readonly List<Slot> Slots = new List<Slot>();
        public int Seed { get; private set; }
        public string File { get; private set; }
        /// <summary>Seconds since the race start (the schedule's clock); the default counts from <see cref="Start"/>.</summary>
        public Func<float> Clock;
        TrafficSystem traffic;
        Transform root;
        float startedAt;

        /// <summary>World/&lt;city&gt;/Path/PVP_map&lt;city index&gt;_&lt;track&gt;.chpath (city index 1..5 as PvpTraffic.Count).</summary>
        public static string PathFile(string city, int track) => $@"{city}\path\pvp_map{CityMapInfo.CityIndex(city)}_{track:00}.chpath";

        /// <summary>A seed every client of a race derives alike (the room id, the track, the drivers).</summary>
        public static int SeedFor(int roomId, string track, int drivers)
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + roomId;
                foreach (char c in (track ?? "").ToLowerInvariant()) h = h * 31 + c;
                h = h * 31 + drivers;
                return h == 0 ? 1 : h;
            }
        }

        /// <summary>
        /// Starts the track's traffic: <paramref name="count"/> cars on the paths of <see cref="PathFile"/>, scheduled from
        /// <paramref name="seed"/>. Null when the track has no path file or nothing to drive.
        /// </summary>
        public static PvpPathTraffic Start(TrafficSystem traffic, string city, int track, int count, int seed, Func<float> clock = null)
        {
            if (Active) Destroy(Active.gameObject);
            var fs = DriftCityData.Files;
            string file = PathFile(city, track);
            if (!traffic || count <= 0 || fs == null || !fs.Exists(file)) return null;
            var paths = ChPathFile.Parse(fs.Read(file)).FindAll(p => p.T.Length >= 2 && p.Duration > 1f);
            if (paths.Count == 0) return null;
            var go = new GameObject("PvP Path Traffic");
            go.transform.SetParent(traffic.transform.parent, false);
            var pt = go.AddComponent<PvpPathTraffic>();
            pt.traffic = traffic;
            pt.root = go.transform;
            pt.Seed = seed;
            pt.File = file;
            pt.Paths.AddRange(paths);
            pt.Clock = clock;
            pt.startedAt = Time.time;
            pt.Schedule(count);
            Active = pt;
            Debug.Log($"Drift City: PvP traffic {count} cars on {paths.Count} paths of {file} (seed {seed})");
            return pt;
        }

        /// <summary>The slots of the schedule: path by length, offset, speed per path, the model.</summary>
        void Schedule(int count)
        {
            var rng = new Rng(Seed);
            var pathSpeed = new float[Paths.Count];
            float total = 0;
            for (int i = 0; i < Paths.Count; i++) { pathSpeed[i] = (45f + 30f * rng.Value()) / 3.6f / KeyMetres; total += Paths[i].Duration; }
            int models = traffic ? Mathf.Max(1, traffic.TemplateCount) : 1;
            for (int i = 0; i < count; i++)
            {
                float pick = rng.Value() * total;
                int p = 0;
                while (p < Paths.Count - 1 && pick > Paths[p].Duration) { pick -= Paths[p].Duration; p++; }
                Slots.Add(new Slot { Path = p, Offset = rng.Value() * Paths[p].Duration, Speed = pathSpeed[p], Model = rng.Range(models) });
            }
        }

        public float Now => Clock != null ? Clock() : Time.time - startedAt;

        /// <summary>Where slot <paramref name="i"/> is at race time <paramref name="t"/> (Unity space); false while it is hidden at a path end.</summary>
        public bool Position(int i, float t, out Vector3 pos, out Vector3 fwd)
        {
            var s = Slots[i];
            var path = Paths[s.Path];
            float len = path.Duration;
            float k = (s.Offset + s.Speed * Mathf.Max(0f, t)) % len;
            var p = path.Sample(k, out var dir);
            pos = DriftCityData.ToUnity(p);
            fwd = DriftCityData.ToUnity(dir);
            float edge = EdgeMetres / KeyMetres;
            return k > edge && k < len - edge;
        }

        void Update()
        {
            if (!traffic) { Destroy(gameObject); return; }
            var focus = traffic.Player ? traffic.Player.position : Camera.main ? Camera.main.transform.position : Vector3.zero;
            float t = Now, r2 = ShowRadius * ShowRadius;
            int bodies = 0;
            for (int i = 0; i < Slots.Count; i++)
            {
                var s = Slots[i];
                bool on = Position(i, t, out var pos, out var fwd) && (pos - focus).sqrMagnitude < r2 && bodies < MaxBodies;
                if (on) bodies++;
                if (!on)
                {
                    if (s.Car && s.Car.gameObject.activeSelf && !s.Car.Wrecked) s.Car.gameObject.SetActive(false);
                    continue;
                }
                if (!s.Car)
                {
                    var tpl = traffic.TemplateAt(s.Model);
                    if (!tpl) continue;
                    var go = Instantiate(tpl, root);
                    go.name = "PvP Traffic " + tpl.name;
                    go.SetActive(true);
                    s.Car = go.AddComponent<PathCar>();
                    s.Car.Setup(pos, fwd, s.Speed * KeyMetres);
                }
                else if (!s.Car.gameObject.activeSelf) { s.Car.gameObject.SetActive(true); s.Car.Teleport(pos, fwd); }
                s.Car.Target(pos, fwd, s.Speed * KeyMetres);
            }
        }

        void OnDestroy()
        {
            if (Active == this) Active = null;
        }
    }

    /// <summary>One PvP path car: a kinematic body on its scheduled spot (wheels and ground from TrafficRig).</summary>
    public sealed class PathCar : MonoBehaviour
    {
        Rigidbody rb;
        TrafficRig rig;
        Vector3 pos, fwd = Vector3.forward;
        float speed, wreck;
        Quaternion heading = Quaternion.identity;
        bool placed;
        public bool Wrecked => wreck > 0;

        public void Setup(Vector3 p, Vector3 f, float metresPerSecond)
        {
            rb = gameObject.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.mass = 1500f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            var rs = GetComponentsInChildren<Renderer>();
            var size = new Vector3(2f, 1.6f, 4.6f); var center = Vector3.up * 0.8f;
            if (rs.Length > 0)
            {
                var b = rs[0].bounds; foreach (var r in rs) b.Encapsulate(r.bounds);
                var box = gameObject.AddComponent<BoxCollider>();
                box.center = center = transform.InverseTransformPoint(b.center);
                box.size = size = b.size;
            }
            rig = GetComponent<TrafficRig>();
            if (!rig) { rig = gameObject.AddComponent<TrafficRig>(); rig.CornersFromBox(center, size); }
            Teleport(p, f);
            speed = metresPerSecond;
        }

        public void Teleport(Vector3 p, Vector3 f)
        {
            pos = p; fwd = Flat(f);
            heading = Quaternion.LookRotation(fwd, Vector3.up);
            wreck = 0;
            if (rb) rb.isKinematic = true;
            rig.ResetAt(p, fwd, heading);
            rig.Follow(p, fwd, heading, 0f, 0f, true, out var q, out var r);
            transform.SetPositionAndRotation(q, r);
            if (rb) { rb.position = q; rb.rotation = r; }
            placed = true;
        }

        public void Target(Vector3 p, Vector3 f, float metresPerSecond) { pos = p; fwd = Flat(f); speed = metresPerSecond; }

        static Vector3 Flat(Vector3 v) { v.y = 0; return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward; }

        void FixedUpdate()
        {
            if (!placed || !rb) return;
            if (wreck > 0)
            {
                wreck -= Time.fixedDeltaTime;
                if (wreck <= 0) Teleport(pos, fwd);   // back to its scheduled place
                return;
            }
            heading = Quaternion.Slerp(heading, Quaternion.LookRotation(fwd, Vector3.up), 8f * Time.fixedDeltaTime);
            rig.Follow(pos, fwd, heading, speed, Time.fixedDeltaTime, true, out var q, out var r);
            rb.MovePosition(q);
            rb.MoveRotation(r);
        }

        void OnCollisionEnter(Collision c)
        {
            if (wreck > 0 || !c.rigidbody || c.rigidbody.isKinematic || c.relativeVelocity.magnitude <= 9f) return;
            rb.isKinematic = false;
            rb.linearVelocity = transform.forward * speed;
            rb.AddForceAtPosition(-c.impulse, c.GetContact(0).point, ForceMode.Impulse);
            wreck = 4f;
        }
    }
}
