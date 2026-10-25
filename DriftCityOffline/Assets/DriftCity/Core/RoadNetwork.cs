// The road side of <city>.tcs (DriftCity_RE/ROADS.md): the client's own "road map" in the open cities. Its
// wrong-way / off-road test (FUN_007fb4d0, called from the car update at 0x48604b) finds the road strip under
// the car, measures the distance to the road's centre line and compares it with the road's lane layout.
//   0x1000/0x2100 node   {i32 id, f32 x, y, z, i32 ?, i32 linkCount, ...}
//   0x1000/0x3100 road   {i32 index, i32 kind, [i32 extra when kind == 1], i32 ?, i32 startNode, i32 endNode,
//                         f32 length, f32 ?, i32 ?, f32 ?, f32 speedLimit (m/s), f32 margin, f32 shoulderL,
//                         f32 sidewalkL, f32 curbL, f32 shoulderR, f32 sidewalkR, f32 curbR, f32 laneWidth, f32 ?,
//                         i32 lanesA, i32 lanesB, i32 ?, f32 startSetback, f32 endSetback, i32 n,
//                         n x {f32 x, y, z, f32 medianL, medianR}}
// (field order from the NDPATH reader 0x803640 with its "%d%d%d%f" style format strings; names inferred from use).
using System;
using System.Collections.Generic;
using DriftCity.Core.Nif;

namespace DriftCity.Core
{
    public sealed class TcsRoad
    {
        public int StartNode, EndNode;
        public float SpeedLimit, LaneWidth, Margin, StartSetback, EndSetback;
        public float ShoulderL, SidewalkL, ShoulderR, SidewalkR;
        public int LanesA, LanesB;
        public float Median;      // client param [0]: the widest per-point median half-width
        public Vec3[] Points = new Vec3[0];

        /// <summary>Half-width of the road's collision strip (the mesh built by FUN_007fb050).</summary>
        public float StripHalfWidth => Median + Margin + (LanesA + LanesB) * LaneWidth * 0.5f + (ShoulderL + SidewalkL + ShoulderR + SidewalkR) * 0.5f;
        /// <summary>Within this distance of the centre line the car is on the carriageway (wrong-way zone on the left).</summary>
        public float CarriagewayHalf => Median + LaneWidth * ((LanesA + LanesB) / 2);
        /// <summary>At or beyond this distance of the centre line the car is off the road (sidewalk and beyond).</summary>
        public float OffRoadDistance => Median + (LanesA + LanesB + 1) * 0.5f * LaneWidth;
    }

    public sealed class TcsNode { public int Id, Links; public Vec3 Pos; public float Radius = 1f; }

    /// <summary>Result of the client's lane query, see <see cref="RoadNetwork.Query"/>.</summary>
    public struct RoadHit
    {
        public bool Found;      // a road strip is under the car
        public bool Junction;   // within a junction (node with more than two roads) radius + 1 m
        public bool Outside;    // no perpendicular foot on the centre line near the hit segment
        public bool RightSide;  // right of the centre line, looking along the lane that matches the heading
        public float Distance;  // to the centre line (3D)
        public int Road, Segment;
    }

    public sealed class RoadNetwork
    {
        public List<TcsRoad> Roads = new List<TcsRoad>();
        public Dictionary<int, TcsNode> Nodes = new Dictionary<int, TcsNode>();

        const float Cell = 32f;
        readonly Dictionary<long, List<int>> grid = new Dictionary<long, List<int>>();   // road * 65536 + segment

        public static RoadNetwork Parse(byte[] d)
        {
            var net = new RoadNetwork();
            int p = FindBody(d);
            if (p < 0) return net;
            Walk(d, p, Math.Min(d.Length, p - 8 + BitConverter.ToInt32(d, p - 4)), 0, 0, net);
            foreach (var r in net.Roads)
            {
                if (net.Nodes.TryGetValue(r.StartNode, out var a)) a.Radius = Math.Max(a.Radius, r.StartSetback);
                if (net.Nodes.TryGetValue(r.EndNode, out var b)) b.Radius = Math.Max(b.Radius, r.EndSetback);
            }
            net.BuildGrid();
            return net;
        }

        static int FindBody(byte[] d)
        {
            for (int q = 8; q + 16 < d.Length && q < 256; q++)
                if (BitConverter.ToInt32(d, q) == 5 && BitConverter.ToInt32(d, q + 4) == d.Length - q) return q + 8;
            return -1;
        }

        static void Walk(byte[] d, int p, int end, uint parent, int depth, RoadNetwork net)
        {
            while (p + 8 <= end)
            {
                uint tag = BitConverter.ToUInt32(d, p);
                int size = BitConverter.ToInt32(d, p + 4);
                if (size < 8 || p + size > end) return;
                int body = p + 8, len = size - 8;
                if (parent == 0x1000 && tag == 0x2100 && len >= 24)
                {
                    var n = new TcsNode { Id = BitConverter.ToInt32(d, body), Pos = V3(d, body + 4), Links = BitConverter.ToInt32(d, body + 20) };
                    net.Nodes[n.Id] = n;
                }
                else if (parent == 0x1000 && tag == 0x3100 && len >= 0x64)
                {
                    var r = ReadRoad(d, body, len);
                    if (r != null) net.Roads.Add(r);
                }
                else if (depth < 3 && len >= 8 && (tag & 0xFFF) == 0)
                    Walk(d, body, body + len, tag, depth + 1, net);
                p += size;
            }
        }

        static TcsRoad ReadRoad(byte[] d, int body, int len)
        {
            int kind = BitConverter.ToInt32(d, body + 4);
            int q = body + (kind == 1 ? 12 : 8);
            int end = body + len;
            if (q + 0x5c > end) return null;
            float F(int o) => BitConverter.ToSingle(d, q + o);
            int I(int o) => BitConverter.ToInt32(d, q + o);
            var r = new TcsRoad
            {
                StartNode = I(4), EndNode = I(8),
                SpeedLimit = F(0x1c),
                Margin = F(0x20),
                ShoulderL = F(0x24), SidewalkL = F(0x28),
                ShoulderR = F(0x30), SidewalkR = F(0x34),
                LaneWidth = F(0x3c),
                LanesA = I(0x44), LanesB = I(0x48),
                StartSetback = F(0x50), EndSetback = F(0x54),
            };
            int n = I(0x58);
            q += 0x5c;
            if (n < 2 || q + n * 20L > end) return null;
            r.Points = new Vec3[n];
            for (int i = 0; i < n; i++, q += 20)
            {
                r.Points[i] = V3(d, q);
                r.Median = Math.Max(r.Median, Math.Max(BitConverter.ToSingle(d, q + 12), BitConverter.ToSingle(d, q + 16)));
            }
            return r;
        }

        static Vec3 V3(byte[] d, int o) => new Vec3(BitConverter.ToSingle(d, o), BitConverter.ToSingle(d, o + 4), BitConverter.ToSingle(d, o + 8));

        static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;

        void BuildGrid()
        {
            for (int ri = 0; ri < Roads.Count; ri++)
            {
                var r = Roads[ri];
                float h = r.StripHalfWidth;
                for (int k = 0; k + 1 < r.Points.Length && k < 65535; k++)
                {
                    Vec3 a = r.Points[k], b = r.Points[k + 1];
                    int x0 = (int)Math.Floor((Math.Min(a.X, b.X) - h) / Cell), x1 = (int)Math.Floor((Math.Max(a.X, b.X) + h) / Cell);
                    int y0 = (int)Math.Floor((Math.Min(a.Y, b.Y) - h) / Cell), y1 = (int)Math.Floor((Math.Max(a.Y, b.Y) + h) / Cell);
                    for (int x = x0; x <= x1; x++)
                        for (int y = y0; y <= y1; y++)
                        {
                            if (!grid.TryGetValue(Key(x, y), out var list)) grid[Key(x, y)] = list = new List<int>();
                            list.Add(ri * 65536 + k);
                        }
                }
            }
        }

        /// <summary>
        /// FUN_007fb4d0. (x, y, z) Gamebryo position, (hx, hy) heading. The client casts a ray from z + 7 down onto
        /// the road strips; the highest strip at or below that height is taken here. Then it measures the 3D
        /// distance to the centre line over the segments around the hit (a few metres back, two ahead), picks the
        /// side against the lane running the car's way, and flags junctions by the nearer end node.
        /// </summary>
        public RoadHit Query(float x, float y, float z, float hx, float hy)
        {
            var hit = new RoadHit { Road = -1, Segment = -1 };
            if (!grid.TryGetValue(Key((int)Math.Floor(x / Cell), (int)Math.Floor(y / Cell)), out var list)) return hit;
            float top = z + 7f, bestZ = float.MinValue;
            foreach (int code in list)
            {
                int ri = code >> 16, k = code & 0xFFFF;
                var r = Roads[ri];
                Vec3 a = r.Points[k], b = r.Points[k + 1];
                float dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
                if (l2 < 1e-8f) continue;
                float t = ((x - a.X) * dx + (y - a.Y) * dy) / l2;
                // the strip is continuous through the polyline's bends (round joins stand in for its corners)
                if (t < 0f || t > 1f)
                {
                    bool inner = t > 1f ? k + 2 < r.Points.Length : k > 0;
                    if (!inner) continue;
                    t = t > 1f ? 1f : 0f;
                }
                float px = a.X + dx * t - x, py = a.Y + dy * t - y;
                float hw = r.StripHalfWidth;
                if (px * px + py * py > hw * hw) continue;
                float sz = a.Z + (b.Z - a.Z) * t;
                if (sz > top || sz <= bestZ) continue;
                bestZ = sz; hit.Road = ri; hit.Segment = k;
            }
            if (hit.Road < 0) return hit;
            hit.Found = true;
            var road = Roads[hit.Road];
            var pts = road.Points;
            int seg = hit.Segment;

            // the lane running the car's way: the road's own direction, or reversed when heading . dir <= 0
            Vec3 s0 = pts[seg], s1 = pts[seg + 1];
            float sign = (s1.X - s0.X) * hx + (s1.Y - s0.Y) * hy <= 0f ? -1f : 1f;

            // FUN_007fefd0: nearest perpendicular foot on the centre line, about 10 m back to two segments ahead
            int first = seg, back = 0;
            float run = 0f;
            while (first > 0 && back < 4 && run <= 10f) { run += Dist(pts[first - 1], pts[first]); first--; back++; }
            int last = Math.Min(pts.Length - 2, seg + 2);
            float best = float.MaxValue;
            int footSeg = -1;
            for (int k = first; k <= last; k++)
            {
                Vec3 a = pts[k], b = pts[k + 1];
                float ex = b.X - a.X, ey = b.Y - a.Y, ez = b.Z - a.Z, el = (float)Math.Sqrt(ex * ex + ey * ey + ez * ez);
                if (el < 1e-5f) continue;
                ex /= el; ey /= el; ez /= el;
                float t = (x - a.X) * ex + (y - a.Y) * ey + (z - a.Z) * ez;
                if (t < 0f || t > el) continue;
                float fx = x - (a.X + ex * t), fy = y - (a.Y + ey * t), fz = z - (a.Z + ez * t);
                float d2 = fx * fx + fy * fy + fz * fz;
                if (d2 < best)
                {
                    best = d2; footSeg = k;
                    // right of the lane direction (z up): (pos - foot) x dir has a positive z the other way round
                    float tx = ex * sign, ty = ey * sign;
                    hit.RightSide = fx * ty - fy * tx > 0f;
                }
            }
            if (footSeg < 0) hit.Outside = true;
            else
            {
                hit.Distance = (float)Math.Sqrt(best);
                float full = 2f * road.Median + (road.LanesA + road.LanesB) * road.LaneWidth + road.ShoulderL + road.SidewalkL + road.ShoulderR + road.SidewalkR;
                if (full < hit.Distance) hit.Outside = true;
            }

            int nodeId = 2 * seg < pts.Length ? road.StartNode : road.EndNode;
            if (Nodes.TryGetValue(nodeId, out var node) && node.Links > 2)
            {
                float jx = x - node.Pos.X, jy = y - node.Pos.Y, jz = z - node.Pos.Z, jr = node.Radius + 1f;
                hit.Junction = jx * jx + jy * jy + jz * jz < jr * jr;
            }
            return hit;
        }

        static float Dist(Vec3 a, Vec3 b)
        {
            float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }
}
