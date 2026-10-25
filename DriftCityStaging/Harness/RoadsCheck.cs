using System;
using System.Diagnostics;
using System.Linq;
using DriftCity.Core;
using DriftCity.Core.Nif;

/// <summary>"roads" mode: parses every city's road data (.tcs roads, total.hit, zone .hit files) and runs the
/// client's road queries along the traffic lanes, which should read as on-road and not wrong-way.</summary>
static class RoadsCheck
{
    static string Diagnose(RoadNetwork net, Vec3 p)
    {
        float best = float.MaxValue; int br = -1; float bz = 0, bt = 0;
        for (int ri = 0; ri < net.Roads.Count; ri++)
        {
            var pts = net.Roads[ri].Points;
            for (int k = 0; k + 1 < pts.Length; k++)
            {
                Vec3 a = pts[k], b = pts[k + 1];
                float dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy; if (l2 < 1e-8f) continue;
                float t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2; float tc = Math.Max(0, Math.Min(1, t));
                float px = a.X + dx * tc - p.X, py = a.Y + dy * tc - p.Y, d = (float)Math.Sqrt(px * px + py * py);
                if (d < best) { best = d; br = ri; bz = a.Z + (b.Z - a.Z) * tc - p.Z; bt = t; }
            }
        }
        var nn = net.Nodes.Values.OrderBy(n => (n.Pos.X - p.X) * (n.Pos.X - p.X) + (n.Pos.Y - p.Y) * (n.Pos.Y - p.Y)).First();
        float nd = (float)Math.Sqrt((nn.Pos.X - p.X) * (nn.Pos.X - p.X) + (nn.Pos.Y - p.Y) * (nn.Pos.Y - p.Y));
        return br < 0 ? "none" : $"at ({p.X:F0},{p.Y:F0},{p.Z:F0}) nearest road {br} d2D={best:F1} half={net.Roads[br].StripHalfWidth:F1} dz={bz:F1} t={bt:F2} node {nn.Id} links={nn.Links} r={nn.Radius:F0} d={nd:F1}";
    }

    public static void Run(GameFileSystem fs, string[] cities)
    {
        if (cities.Length == 0) cities = new[] { "moonpalace", "koinonia", "cras", "oros", "taipei" };
        foreach (var c in cities)
        {
            Console.WriteLine($"== {c}");
            foreach (var f in new[] { "floor_m", "wall_m", "exclude", "sand", "water" })
            {
                var path = $@"{c}\main\{f}.hit";
                if (!fs.Exists(path)) continue;
                var sw = Stopwatch.StartNew();
                var m = HitMesh.Parse(fs.Read(path), f != "floor_m" && f != "wall_m");
                var attrs = string.Join(",", m.Attributes.GroupBy(a => a).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}"));
                Console.WriteLine($"  {f}.hit v=0x{m.Version:x} verts={m.Vertices.Length} tris={m.TriangleCount} attrs[{attrs}] {sw.ElapsedMilliseconds} ms");
                if (f == "sand" || f == "water" || f == "exclude")
                {
                    // centroid of the first triangles must test inside
                    int inside = 0, n = Math.Min(200, m.TriangleCount);
                    for (int t = 0; t < n; t++)
                    {
                        Vec3 a = m.Vertices[m.Indices[t * 3]], b = m.Vertices[m.Indices[t * 3 + 1]], cc = m.Vertices[m.Indices[t * 3 + 2]];
                        if (m.Contains((a.X + b.X + cc.X) / 3, (a.Y + b.Y + cc.Y) / 3)) inside++;
                    }
                    Console.WriteLine($"    centroids inside {inside}/{n}; bounds x {m.Vertices.Min(v => v.X):F0}..{m.Vertices.Max(v => v.X):F0} y {m.Vertices.Min(v => v.Y):F0}..{m.Vertices.Max(v => v.Y):F0}");
                }
            }
            var total = $@"{c}\main\total.hit";
            if (fs.Exists(total))
            {
                var rm = RoadMap.Parse(fs.Read(total));
                int along = 0, against = 0, other = 0, n = Math.Min(5000, rm.Triangles.Length);
                for (int i = 0; i < n; i++)
                {
                    var t = rm.Triangles[i * (rm.Triangles.Length / n)];
                    Vec3 a = rm.Vertices[t.A], b = rm.Vertices[t.B], cc = rm.Vertices[t.C];
                    float x = (a.X + b.X + cc.X) / 3, y = (a.Y + b.Y + cc.Y) / 3, z = (a.Z + b.Z + cc.Z) / 3;
                    int r = rm.Query(x, y, z, t.DirX, t.DirY, out _);
                    int r2 = rm.Query(x, y, z, -t.DirX, -t.DirY, out _);
                    if (r == RoadMap.Along) along++; else other++;
                    if (r2 == RoadMap.Against) against++;
                }
                Console.WriteLine($"  total.hit v=0x{rm.Version:x} verts={rm.Vertices.Length} tris={rm.Triangles.Length} nodes={rm.Nodes.Length}: centroid along={along} other={other}, reversed against={against} of {n}");
            }
            var tcs = $@"{c}\{c}.tcs";
            if (!fs.Exists(tcs)) continue;
            var data = fs.Read(tcs);
            var roads = RoadNetwork.Parse(data);
            var lanes = TrafficNetwork.Parse(data);
            Console.WriteLine($"  tcs: roads={roads.Roads.Count} nodes={roads.Nodes.Count} lanes={lanes.Lanes.Count}; lanes {string.Join(",", roads.Roads.GroupBy(r => (r.LanesA, r.LanesB)).OrderBy(g => g.Key).Select(g => $"{g.Key.LanesA}+{g.Key.LanesB}:{g.Count()}"))}");
            int samples = 0, found = 0, junction = 0, outside = 0, wrong = 0, off = 0, rwrong = 0, rcount = 0;
            int miss = 0;
            foreach (var lane in lanes.Lanes)
                for (int i = 0; i + 1 < lane.Points.Length; i += 3)
                {
                    Vec3 p = lane.Points[i], q = lane.Points[i + 1];
                    float hx = q.X - p.X, hy = q.Y - p.Y, hl = (float)Math.Sqrt(hx * hx + hy * hy);
                    if (hl < 1e-3f) continue;
                    hx /= hl; hy /= hl;
                    samples++;
                    var h = roads.Query(p.X, p.Y, p.Z, hx, hy);
                    if (!h.Found) { off++; if (miss++ % 50 == 0) Console.WriteLine("    miss " + Diagnose(roads, p)); continue; }
                    found++;
                    if (h.Junction) { junction++; continue; }
                    if (h.Outside) { outside++; continue; }
                    var road = roads.Roads[h.Road];
                    if (!h.RightSide && h.Distance < road.CarriagewayHalf) wrong++;
                    if (h.Distance >= road.OffRoadDistance) off++;
                    var hr = roads.Query(p.X, p.Y, p.Z, -hx, -hy);
                    if (hr.Found && !hr.Junction && !hr.Outside) { rcount++; if (!hr.RightSide && hr.Distance < roads.Roads[hr.Road].CarriagewayHalf) rwrong++; }
                }
            Console.WriteLine($"  lane samples={samples} found={found} junction={junction} outside={outside} wrong={wrong} off={off}; reversed heading wrong={rwrong}/{rcount}");
        }
    }
}
