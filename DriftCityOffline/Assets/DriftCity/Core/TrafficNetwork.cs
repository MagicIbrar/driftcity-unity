// Drift City traffic control system (<city>.tcs): "TCS\0" + stamp + "TRAFFIC-CONTROL-SYSTEM\0" +
// author strings, then u32 version, u32 size, and a tree of chunks {u32 tag, u32 size incl. header}.
//   0x1000/0x2100  intersection nodes {u32 id, vec3 pos, ...}
//   0x1000/0x3100  roads: centre-line polylines
//   0x70000/0x70300 lanes {i32 -1, u32 id, f32, u32 n, vec3 points[n]} (coarse control points, n = 4)
//   0x90000/0x90020 dense lane samples {u32 lane, u32 n, n x 17 floats: t, pos, dir, right, up, 4 extra}
// Lane successors are derived geometrically (start of B near the end of A, similar heading).
using System;
using System.Collections.Generic;
using DriftCity.Core.Nif;

namespace DriftCity.Core
{
    public sealed class TrafficLane
    {
        public int Id;
        public Vec3[] Points;
        public List<int> Next = new List<int>();
        public float Length;
    }

    public sealed class TrafficNetwork
    {
        public List<TrafficLane> Lanes = new List<TrafficLane>();
        public List<Vec3> Nodes = new List<Vec3>();
        readonly Dictionary<int, Vec3[]> dense = new Dictionary<int, Vec3[]>();

        public static TrafficNetwork Parse(byte[] d)
        {
            var net = new TrafficNetwork();
            int p = FindBody(d);
            if (p < 0) return net;
            int end = Math.Min(d.Length, p - 8 + BitConverter.ToInt32(d, p - 4));
            Walk(d, p, end, 0, 0, net);
            // prefer the dense samples (true lane geometry) over the coarse control points
            for (int i = 0; i < net.Lanes.Count; i++)
                if (net.dense.TryGetValue(i, out var pts) || net.dense.TryGetValue(net.Lanes[i].Id, out pts))
                {
                    var lane = net.Lanes[i];
                    lane.Points = pts;
                    lane.Length = 0;
                    for (int k = 1; k < pts.Length; k++) lane.Length += Dist(pts[k - 1], pts[k]);
                }
            net.Link(45f);
            return net;
        }

        // body starts after the NUL-terminated header strings, at u32 version (5), u32 size
        static int FindBody(byte[] d)
        {
            for (int q = 8; q + 16 < d.Length && q < 256; q++)
            {
                int version = BitConverter.ToInt32(d, q), size = BitConverter.ToInt32(d, q + 4);
                if (version == 5 && size == d.Length - q) return q + 8;
            }
            return -1;
        }

        static void Walk(byte[] d, int p, int end, uint parent, int depth, TrafficNetwork net)
        {
            while (p + 8 <= end)
            {
                uint tag = BitConverter.ToUInt32(d, p);
                int size = BitConverter.ToInt32(d, p + 4);
                if (size < 8 || p + size > end) return;
                int body = p + 8, len = size - 8;
                if (parent == 0x1000 && tag == 0x2100 && len >= 16)
                {
                    var r = new NifReader(d) { Pos = body + 4 };
                    net.Nodes.Add(r.V3());
                }
                else if (parent == 0x70000 && tag == 0x70300 && len >= 16)
                {
                    int id = BitConverter.ToInt32(d, body + 4);
                    int n = BitConverter.ToInt32(d, body + 12);
                    if (n >= 2 && n <= 64 && 16 + n * 12 <= len)
                    {
                        var r = new NifReader(d) { Pos = body + 16 };
                        var lane = new TrafficLane { Id = id, Points = r.V3s(n) };
                        for (int i = 1; i < n; i++) lane.Length += Dist(lane.Points[i - 1], lane.Points[i]);
                        net.Lanes.Add(lane);
                    }
                }
                else if (parent == 0x90000 && tag == 0x90020 && len >= 8)
                {
                    int laneIndex = BitConverter.ToInt32(d, body);
                    int n = BitConverter.ToInt32(d, body + 4);
                    if (n >= 2 && n <= 512 && 8 + n * 68 <= len)
                    {
                        var pts = new Vec3[n];
                        for (int i = 0; i < n; i++) { var r = new NifReader(d) { Pos = body + 8 + i * 68 + 4 }; pts[i] = r.V3(); }
                        net.dense[laneIndex] = pts;
                    }
                }
                else if (depth < 3 && len >= 8 && (tag & 0xFFF) == 0 && tag != 0x2100 && tag != 0x70300)
                {
                    Walk(d, body, body + len, tag, depth + 1, net);
                }
                p += size;
            }
        }

        static float Dist(Vec3 a, Vec3 b)
        {
            float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>Lane B follows lane A when B starts within <paramref name="tolerance"/> metres of A's end,
        /// ahead of it and not turning back (intersections separate lane ends from the next lane starts).</summary>
        void Link(float tolerance)
        {
            const float cell = 50f;
            var grid = new Dictionary<long, List<int>>();
            long Key(Vec3 v) => ((long)Math.Floor(v.X / cell) << 32) ^ (long)(Math.Floor(v.Y / cell) + 1_000_000);
            for (int i = 0; i < Lanes.Count; i++)
            {
                var k = Key(Lanes[i].Points[0]);
                if (!grid.TryGetValue(k, out var list)) grid[k] = list = new List<int>();
                list.Add(i);
            }
            for (int i = 0; i < Lanes.Count; i++)
            {
                var pts = Lanes[i].Points;
                var end = pts[pts.Length - 1];
                var before = pts[pts.Length - 2];
                float hx = end.X - before.X, hy = end.Y - before.Y;
                float hl = (float)Math.Sqrt(hx * hx + hy * hy) + 1e-5f; hx /= hl; hy /= hl;
                for (int gx = -1; gx <= 1; gx++)
                    for (int gy = -1; gy <= 1; gy++)
                    {
                        var probe = new Vec3(end.X + gx * cell, end.Y + gy * cell, end.Z);
                        if (!grid.TryGetValue(Key(probe), out var list)) continue;
                        foreach (int j in list)
                        {
                            if (j == i || Lanes[i].Next.Contains(j)) continue;
                            var s0 = Lanes[j].Points[0];
                            var s1 = Lanes[j].Points[1];
                            float dx = s0.X - end.X, dy = s0.Y - end.Y;
                            float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                            if (dist > tolerance || Math.Abs(s0.Z - end.Z) > 6f) continue;
                            // the next lane must start ahead of us and not head back the way we came
                            if (dist > 2f && (dx * hx + dy * hy) / dist < -0.2f) continue;
                            float nx = s1.X - s0.X, ny = s1.Y - s0.Y, nl = (float)Math.Sqrt(nx * nx + ny * ny) + 1e-5f;
                            if ((nx * hx + ny * hy) / nl < -0.3f) continue;
                            Lanes[i].Next.Add(j);
                        }
                    }
            }
        }
    }
}
