// Drift City passerby control system (<city>\passerby.pcs): the city's pedestrians. "PCS\0" + stamp +
// "Passerby-CONTROL-SYSTEM\0" + author strings, u32 version 5, u32 size, then chunks {u32 tag, u32 size incl. header}
// with the names of the exe's reader table at 0x8caee8 (handlers in 0x7e8000-0x7e9400):
//   0x10000 TIMEKEYDATAS / 0x12000 TIMEKEYDATA {u32 id, u32 n, n x "%f %f3 %f3 %f3 %f3 %f4"}: walking lines, a time key
//           t per point (t = metres / 27.7778, i.e. seconds at 100 km/h; 0x7f3a60 converts back), position, three axes
//           and a quaternion (0x7e91e0)
//   0x20000 JOINTS / 0x22000 JOINTDATA {id, ..}: where lines meet (0x7e8560)
//   0x30000 PATHS / 0x32000 PATHDATA {id, timekey, footway, joint a, joint b} (0x7e85f0)
//   0x40000 FOOTWAYS / 0x42000 FOOTWAYDATA {id, kind, path a, path b, link a, link b, owner, owner kind}: a pavement
//           (both walking directions); kind 4/5 are the crossings (0x7e8690, 0x7f3a90)
//   0x50000 CROSSWALKS / 0x52000 CROSSWALKDATA {id, a, b, f, pos} (0x7e8750)
//   0x60000 CROSSES, 0x70000 ROADS: junction and road tables (not needed to walk)
//   0x80000 LCPATHS / 0x82000 LCPATHDATA {i32 kind, f32 length (m), i32 n, n x {f32 start, f32 end, i32 path}}: the
//           closed routes the pedestrians walk, as consecutive pieces of paths in time-key units (0x7e8f70)
using System;
using System.Collections.Generic;
using DriftCity.Core.Nif;

namespace DriftCity.Core
{
    public sealed class PasserbyFile
    {
        /// <summary>Metres per time-key unit (0x7f3a60 / 0x7f3a70 multiply by 27.7777786; speeds are scaled by 0.036).</summary>
        public const float MetresPerUnit = 27.7777786f;

        public sealed class TimeKeyLine
        {
            public int Id;
            public float[] T;
            public Vec3[] Pos;
            public float Duration => T.Length > 0 ? T[T.Length - 1] : 0;
        }
        public struct PathRec { public int Id, TimeKey, Footway, JointA, JointB; }
        public struct Footway { public int Id, Kind, PathA, PathB; }
        public struct Crosswalk { public int Id, A, B; public float F; public Vec3 Pos; }
        public struct Segment { public float Start, End; public int Path; public float LengthM => (End - Start) * MetresPerUnit; }
        public sealed class Route
        {
            public int Kind;
            public float LengthM;
            public Segment[] Segments;
            /// <summary>Route length in time-key units (last segment end).</summary>
            public float End => Segments.Length > 0 ? Segments[Segments.Length - 1].End : 0;
        }

        public readonly List<TimeKeyLine> Lines = new List<TimeKeyLine>();
        public readonly List<PathRec> Paths = new List<PathRec>();
        public readonly List<Footway> Footways = new List<Footway>();
        public readonly List<Crosswalk> Crosswalks = new List<Crosswalk>();
        public readonly List<Route> Routes = new List<Route>();

        public static PasserbyFile Parse(byte[] d)
        {
            var f = new PasserbyFile();
            int body = -1;
            for (int q = 8; q + 8 <= d.Length && q < 256; q++)
                if (BitConverter.ToInt32(d, q) == 5 && BitConverter.ToInt32(d, q + 4) == d.Length - q) { body = q + 8; break; }
            if (body < 0) return f;
            f.Walk(d, body, d.Length, 0);
            return f;
        }

        void Walk(byte[] d, int p, int end, int depth)
        {
            while (p + 8 <= end)
            {
                uint tag = BitConverter.ToUInt32(d, p);
                int size = BitConverter.ToInt32(d, p + 4);
                if (size < 8 || p + size > end) return;
                int b = p + 8, len = size - 8;
                switch (tag)
                {
                    case 0x12000 when len >= 8:
                    {
                        int id = BitConverter.ToInt32(d, b), n = BitConverter.ToInt32(d, b + 4);
                        if (n < 0 || 8 + n * 68 > len) break;
                        var line = new TimeKeyLine { Id = id, T = new float[n], Pos = new Vec3[n] };
                        for (int i = 0; i < n; i++)
                        {
                            int o = b + 8 + i * 68;
                            line.T[i] = BitConverter.ToSingle(d, o);
                            line.Pos[i] = new Vec3(BitConverter.ToSingle(d, o + 4), BitConverter.ToSingle(d, o + 8), BitConverter.ToSingle(d, o + 12));
                        }
                        Lines.Add(line);
                        break;
                    }
                    case 0x32000 when len >= 20:
                        Paths.Add(new PathRec { Id = I(d, b), TimeKey = I(d, b + 4), Footway = I(d, b + 8), JointA = I(d, b + 12), JointB = I(d, b + 16) });
                        break;
                    case 0x42000 when len >= 16:
                        Footways.Add(new Footway { Id = I(d, b), Kind = I(d, b + 4), PathA = I(d, b + 8), PathB = I(d, b + 12) });
                        break;
                    case 0x52000 when len >= 28:
                        Crosswalks.Add(new Crosswalk { Id = I(d, b), A = I(d, b + 4), B = I(d, b + 8), F = BitConverter.ToSingle(d, b + 12),
                                                       Pos = new Vec3(BitConverter.ToSingle(d, b + 16), BitConverter.ToSingle(d, b + 20), BitConverter.ToSingle(d, b + 24)) });
                        break;
                    case 0x82000 when len >= 12:
                    {
                        int n = I(d, b + 8);
                        if (n < 0 || 12 + n * 12 > len) break;
                        var r = new Route { Kind = I(d, b), LengthM = BitConverter.ToSingle(d, b + 4), Segments = new Segment[n] };
                        for (int i = 0; i < n; i++)
                        {
                            int o = b + 12 + i * 12;
                            r.Segments[i] = new Segment { Start = BitConverter.ToSingle(d, o), End = BitConverter.ToSingle(d, o + 4), Path = I(d, o + 8) };
                        }
                        Routes.Add(r);
                        break;
                    }
                    default:
                        if ((tag & 0xFFF) == 0 && (tag & 0xF000) != 0x2000 && depth < 4) Walk(d, b, b + len, depth + 1);
                        break;
                }
                p += size;
            }
        }

        static int I(byte[] d, int o) => BitConverter.ToInt32(d, o);

        /// <summary>0x7f3a90: a route whose first path belongs to a footway of kind 4 or 5 is a road crossing; its
        /// pedestrians walk across, wait, and walk back.</summary>
        public bool IsCrossing(Route r)
        {
            if (r.Segments.Length == 0) return false;
            int path = r.Segments[0].Path;
            if (path < 0 || path >= Paths.Count) return false;
            int fw = Paths[path].Footway;
            if (fw < 0 || fw >= Footways.Count) return false;
            int kind = Footways[fw].Kind;
            return kind == 4 || kind == 5;
        }

        /// <summary>The time-key line a PATHDATA record walks along.</summary>
        public TimeKeyLine LineOf(int path)
        {
            if (path < 0 || path >= Paths.Count) return null;
            int tk = Paths[path].TimeKey;
            return tk >= 0 && tk < Lines.Count ? Lines[tk] : null;
        }

        /// <summary>Position on a path at time key t (linear between keys) and the walking direction there.</summary>
        public Vec3 Sample(int path, float t, out Vec3 dir)
        {
            dir = new Vec3(0, 1, 0);
            var line = LineOf(path);
            if (line == null || line.T.Length == 0) return default;
            var T = line.T;
            int n = T.Length;
            if (n == 1) return line.Pos[0];
            int i = Array.BinarySearch(T, t);
            if (i < 0) i = ~i;
            i = Math.Max(1, Math.Min(n - 1, i));
            var a = line.Pos[i - 1];
            var b = line.Pos[i];
            float span = T[i] - T[i - 1];
            float k = span > 1e-6f ? Math.Max(0, Math.Min(1, (t - T[i - 1]) / span)) : 0;
            float dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
            float l = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (l > 1e-5f) dir = new Vec3(dx / l, dy / l, dz / l);
            return new Vec3(a.X + dx * k, a.Y + dy * k, a.Z + dz * k);
        }

        /// <summary>The segment of a route containing route position <paramref name="pos"/> (time-key units).</summary>
        public static int SegmentAt(Route r, float pos)
        {
            var s = r.Segments;
            for (int i = 0; i < s.Length; i++)
                if (pos < s[i].End) return i;
            return s.Length - 1;
        }

        /// <summary>A world position on a route (time-key units from its start).</summary>
        public Vec3 SampleRoute(Route r, float pos, out Vec3 dir)
        {
            int k = SegmentAt(r, pos);
            var seg = r.Segments[k];
            return Sample(seg.Path, pos - seg.Start, out dir);
        }

        // ---------------- pedestrians and their timetable (0x7e9cf0, 0x7eb0d0, 0x7ea2f0) ----------------

        /// <summary>A pedestrian type (0x7e97b0): vehiclelist id (25002 f00 woman, 25003 m00 man) and the speeds of its
        /// motion slots in m/s: 1 standing (0), 2 walking (1.45), 3 running away (10), 4 (5).</summary>
        public sealed class PedType
        {
            public int VehicleId;
            public float[] Speed = new float[5];
        }
        public static readonly PedType[] Types =
        {
            new PedType { VehicleId = 25002, Speed = new[] { 0f, 0f, 1.45f, 10f, 5f } },
            new PedType { VehicleId = 25003, Speed = new[] { 0f, 0f, 1.45f, 10f, 5f } },
        };

        public sealed class Agent
        {
            public int Index;      // creation order
            public int Route;
            public int Segment;    // segment the start offset lies in
            public float Offset;   // route position at time 0 (time-key units)
            public int Id;         // timetable pattern key (id % 13)
            public int Type;       // 0 woman, 1 man (alternating)
            public bool Crossing;
            /// <summary>Part per slot (0 hair, 1 face, 2 upper body, 3 lower body): 1..3 = f00_parts00..02, 4..6 = m00_parts00..02.</summary>
            public byte[] Parts = new byte[4];
        }

        // 0x8cb078: 21 rows of per-slot indices into the type's part list, used round-robin
        static readonly int[,] PartRows =
        {
            { 0, 0, 0, 0 }, { 1, 0, 1, 1 }, { 2, 0, 2, 2 }, { 2, 0, 0, 1 }, { 0, 0, 0, 2 }, { 1, 0, 1, 0 }, { 0, 0, 2, 0 },
            { 1, 0, 0, 0 }, { 2, 0, 0, 0 }, { 1, 0, 0, 1 }, { 2, 0, 0, 2 }, { 1, 0, 1, 0 }, { 0, 0, 2, 0 }, { 1, 0, 0, 1 },
            { 0, 0, 0, 2 }, { 1, 0, 1, 0 }, { 2, 0, 2, 0 }, { 1, 0, 2, 1 }, { 2, 0, 0, 2 }, { 0, 0, 1, 2 }, { 1, 0, 2, 1 },
        };

        /// <summary>
        /// The pedestrians of the city as 0x7e9cf0 places them. <paramref name="density"/> 0/1/2 spaces them 1500/700/400 m
        /// apart along every route piece of 4 m or more (at least one per piece), shifted 7 m per route kind; crossings get
        /// one or two (at the start and half way) depending on the running count.
        /// </summary>
        public List<Agent> CreateAgents(int density = 0)
        {
            var list = new List<Agent>();
            float spacing = density == 0 ? 1500f : density == 1 ? 700f : 400f;
            int running = 0, partRow = 0;
            void Add(Agent a)
            {
                a.Index = list.Count;
                a.Type = list.Count & 1;
                // 0x7eb0d0: parts from the round-robin row; slot 0 (hair) swaps index 2 -> 0 for men, 1 -> 2 for women
                for (int s = 0; s < 4; s++)
                {
                    int idx = PartRows[partRow, s];
                    if (s == 0) { if (a.Type == 1 && idx == 2) idx = 0; else if (a.Type == 0 && idx == 1) idx = 2; }
                    a.Parts[s] = (byte)((a.Type == 0 ? 1 : 4) + idx);
                }
                partRow = (partRow + 1) % 21;
                list.Add(a);
            }
            for (int e = 0; e < Routes.Count; e++)
            {
                var r = Routes[e];
                if (IsCrossing(r)) continue;
                int c0 = r.Kind == 2 ? 1 : r.Kind == 3 ? 2 : 0;
                for (int k = 0; k < r.Segments.Length; k++)
                {
                    float lenM = r.Segments[k].LengthM;
                    if (lenM < 4f) continue;
                    int count = Math.Max(1, (int)(lenM / spacing));
                    for (int b = 0; b < count; b++)
                        Add(new Agent { Route = e, Segment = k, Offset = b * spacing * 0.036f + c0 * 0.252f + r.Segments[k].Start, Id = running++ });
                }
            }
            int crossing = 0;
            for (int e = 0; e < Routes.Count; e++)
            {
                var r = Routes[e];
                if (!IsCrossing(r)) continue;
                // 0x7f3a70 on the route's pieces: the first piece's end in metres (one walk across)
                float across = r.Segments[0].End * MetresPerUnit;
                bool wide = across * 0.5f > 20f;
                if (wide || (list.Count & 1) == 0)
                {
                    Add(new Agent { Route = e, Segment = 0, Offset = r.Segments[0].Start, Id = crossing, Crossing = true });
                    running++;
                }
                if (wide || (list.Count & 1) == 1)
                {
                    int i = 0; float acc = 0;
                    for (; i < r.Segments.Length; i++) { acc += r.Segments[i].LengthM; if (across * 0.5f < acc) break; }
                    i = Math.Min(i, r.Segments.Length - 1);
                    Add(new Agent { Route = e, Segment = i, Offset = across * 0.5f * 0.036f, Id = crossing, Crossing = true });
                    running++;
                }
                crossing++;
            }
            return list;
        }

        // 0x7ea2f0: walk / stand times by id % 13 (crossings use the first 13 as waiting times)
        static readonly float[] WalkTimes = { 20, 25, 30, 35, 40, 45, 50, 55, 60, 65, 70, 75, 80 };
        static readonly float[] StandTimes = { 5, 6, 7, 8, 9, 10, 10, 5, 5, 10, 10, 10, 10 };
        static readonly float[] CrossWait = { 15, 36, 27, 48, 22, 12, 19, 15, 55, 5, 8, 13, 21 };

        /// <summary>
        /// Where a pedestrian is at game time <paramref name="time"/> (seconds; the client uses the server clock modulo
        /// 10 hours, so every player sees the same pedestrians). Pavement walkers cycle walk A s / stand B s; crossers walk
        /// half the route (one way across) and wait. Returns the route position (time-key units) and whether it is walking.
        /// </summary>
        public float Position(Agent a, float time, out bool walking)
        {
            var r = Routes[a.Route];
            var type = Types[a.Type];
            float v2 = type.Speed[2] * 0.036f, v1 = type.Speed[1] * 0.036f;
            float moveTime, waitTime;
            if (!a.Crossing) { int i = a.Id % 13; moveTime = WalkTimes[i]; waitTime = StandTimes[i]; }
            else { moveTime = r.End / v2 * 0.5f; waitTime = CrossWait[a.Id % 13]; }
            float avg = (v2 * moveTime + v1 * waitTime) / (moveTime + waitTime);
            float phase = time % (moveTime + waitTime);
            float cycleStart = time - phase;
            walking = true;
            if (phase >= moveTime) { phase = moveTime; walking = false; }
            float pos = phase * v2 + cycleStart * avg + a.Offset;
            float end = r.End;
            return end > 0 ? pos % end : 0;
        }
    }
}
