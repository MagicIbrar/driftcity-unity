using System;
using System.Collections.Generic;
using System.Globalization;
using DriftCity.Core;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// Where the client's HUVs drive (DriftCity_RE/TRAFFIC.md section 15):
    /// <list type="bullet">
    /// <item>Boss HUVs (0x4a1470) start at the first point of one of the city's routes in <c>&lt;city&gt;\Huv00.xipath2</c>
    /// (the server's path index modulo the route count) facing the second point, and drive that route
    /// (0x44ab40 hands the route to the AI). The file: "XiPATH2\0" + stamp + "NHN-AG\0jc\0", u32 version 5, u32 size, chunk
    /// 0x1000 {u32 route count}, then per route a chunk 0x1100 {u32 n, n x (x, y, z)} (Gamebryo, z up). Cras has 5 routes.</item>
    /// <item>A chase start (0x4f2500 -> 0x44a5d0 / 0x447ed0): the spawn points of chasespawnlist.text sorted by distance from
    /// the player; the start is the nearest one beyond 1000 m, the candidates the following ones up to 3000 m (5000 m when
    /// that leaves fewer than two); the goal is a random candidate (taken out of the pool) and every HUV slot gets a random
    /// waypoint among the rest. HUV k leaves slot k of the start point and drives the road route slot -> its waypoint ->
    /// the goal (0x447ff0 plans both legs on the road graph with the slot's heading, 0x44a940 joins them).</item>
    /// <item>BossHuvInfo (init\bosshuvinfo.xlt, 0x452b10, copied into the AI by 0x4a0f10): speed and top speed (km/h),
    /// the acceleration when hit (x, seconds), the turbo chance per 10 s, the avoid levels.</item>
    /// </list>
    /// The remake's lanes (&lt;city&gt;.tcs) are the client's road graph; routes are lane lists planned by shortest length.
    /// </summary>
    public static class HuvRoutes
    {
        // ---------------- Huv00.xipath2 ----------------

        static readonly Dictionary<string, List<List<Vector3>>> xiCache = new Dictionary<string, List<List<Vector3>>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The city's boss routes (Unity space); empty when the city has no Huv00.xipath2 (Taipei).</summary>
        public static List<List<Vector3>> XiPaths(string city)
        {
            if (string.IsNullOrEmpty(city)) return new List<List<Vector3>>();
            if (xiCache.TryGetValue(city, out var list)) return list;
            list = new List<List<Vector3>>();
            try
            {
                var fs = DriftCityData.Files;
                string path = $@"{city}\huv00.xipath2";
                if (fs != null && fs.Exists(path)) list = ParseXiPath(fs.Read(path));
            }
            catch (Exception ex) { Debug.LogWarning("Huv00.xipath2: " + ex.Message); }
            xiCache[city] = list;
            return list;
        }

        /// <summary>The 0x4e9000 reader with the chunk table 0x8be130 (0x1000 the count -> 0x4e8d90, 0x1100 a route -> 0x4e9120).</summary>
        public static List<List<Vector3>> ParseXiPath(byte[] d)
        {
            var routes = new List<List<Vector3>>();
            if (d == null || d.Length < 24) return routes;
            int body = -1;
            for (int q = 8; q + 8 <= d.Length && q < 128; q++)
                if (BitConverter.ToInt32(d, q) == 5 && BitConverter.ToInt32(d, q + 4) == d.Length - q) { body = q + 8; break; }
            if (body < 0) return routes;
            int p = body;
            while (p + 8 <= d.Length)
            {
                uint tag = BitConverter.ToUInt32(d, p);
                int size = BitConverter.ToInt32(d, p + 4);
                if (size < 8 || p + size > d.Length) break;
                if (tag == 0x1100 && size >= 12)
                {
                    int n = BitConverter.ToInt32(d, p + 8);
                    var pts = new List<Vector3>();
                    for (int i = 0; i < n && p + 12 + i * 12 + 12 <= p + size; i++)
                    {
                        int o = p + 12 + i * 12;
                        pts.Add(DriftCityData.ToUnity(new Core.Nif.Vec3(BitConverter.ToSingle(d, o), BitConverter.ToSingle(d, o + 4), BitConverter.ToSingle(d, o + 8))));
                    }
                    routes.Add(pts);
                }
                p += size;
            }
            return routes;
        }

        // ---------------- BossHuvInfo ----------------

        /// <summary>One row of init\bosshuvinfo.xlt (the columns 0x4a0f10 copies into the HUV AI).</summary>
        public sealed class BossInfo
        {
            public int Id;
            public string Name;
            /// <summary>내구력 (hits) and 경험치.</summary>
            public int Durability, Exp;
            /// <summary>속도 / 최고속도 (km/h): the AI's cruise (+0x38) and top speed (+0x3c).</summary>
            public float SpeedKmh = 150f, MaxKmh = 180f;
            /// <summary>부딪혔을 때 가속 (x) and its time (s): the burst after a hit (0x492830, AI +0x44).</summary>
            public float HitBoost = 1f, HitBoostSeconds = 1f;
            /// <summary>터보사용빈도: the chance in % of a turbo, rolled every 10 s (AI +0x48 = 10, +0x4c = % x 0.01).</summary>
            public float TurboPercent;
            /// <summary>앞 쪽 플레이어카 / 트래픽카 / 뒤 쪽 플레이어카 피하기 (0 weak, 1 medium, 2 strong).</summary>
            public int AvoidFrontPlayer, AvoidTraffic, AvoidRearPlayer;
            /// <summary>
            /// 컨버팅시간 (column 22, record +0x98): 0x4a0f10 sets the boss object's damage threshold +0x88 = this x 내구력
            /// (+0x54) x 0.01, so it is the share (in %) of the boss's health that must be taken off before its kept escorts
            /// come out (0x4a1860 / 0x4a1d10): 40 for 11001 and 11004, 100 (never before the catch) for the others.
            /// </summary>
            public float ConvertPercent = 100f;
        }

        static Dictionary<int, BossInfo> bossInfo;

        /// <summary>The BossHuvInfo row of a vehiclelist id (bosses 11001-11008, escorts 10001-10004); null when unknown.</summary>
        public static BossInfo Boss(int id)
        {
            if (bossInfo == null)
            {
                bossInfo = new Dictionary<int, BossInfo>();
                try
                {
                    var fs = DriftCityData.Files;
                    if (fs != null && fs.Exists(@"init\bosshuvinfo.xlt")) foreach (var b in ParseBossInfo(fs.Read(@"init\bosshuvinfo.xlt"))) bossInfo[b.Id] = b;
                }
                catch (Exception ex) { Debug.LogWarning("bosshuvinfo.xlt: " + ex.Message); }
            }
            return bossInfo.TryGetValue(id, out var r) ? r : null;
        }

        /// <summary>The first block of bosshuvinfo.xlt (Idx, ID, String ID, name, Support, durability, exp, speed, top speed, ...).</summary>
        public static List<BossInfo> ParseBossInfo(byte[] data)
        {
            var list = new List<BossInfo>();
            string text = data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE ? System.Text.Encoding.Unicode.GetString(data, 2, data.Length - 2) : Cp949.Decode(data);
            // rows start "<idx>\t<id>\t<string id>"; the header cells hold quoted line breaks, so look for the row shape
            foreach (var raw in text.Split('\n'))
            {
                var c = raw.TrimEnd('\r').Split('\t');
                if (c.Length < 17 || !int.TryParse(c[0].Trim(), out _) || !int.TryParse(c[1].Trim(), out int id) || id < 10000) continue;
                float F(int i) => float.TryParse(c[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;
                list.Add(new BossInfo
                {
                    Id = id, Name = c[3].Trim(), Durability = (int)F(5), Exp = (int)F(6), SpeedKmh = F(7), MaxKmh = F(8),
                    HitBoost = F(9), HitBoostSeconds = F(10), TurboPercent = F(11),
                    AvoidFrontPlayer = (int)F(12), AvoidTraffic = (int)F(13), AvoidRearPlayer = (int)F(14),
                    ConvertPercent = c.Length > 22 && F(22) > 0 ? F(22) : 100f,
                });
            }
            return list;
        }

        // ---------------- chase start (0x44a5d0 / 0x447ed0 / 0x447ff0) ----------------

        public sealed class ChasePlan
        {
            public ChaseSpawns.Point Start, Goal;
            /// <summary>One waypoint per HUV slot (16).</summary>
            public ChaseSpawns.Point[] Waypoints = new ChaseSpawns.Point[16];
        }

        /// <summary>
        /// 0x44a5d0 + 0x447ed0: start = the nearest spawn point beyond <paramref name="near"/> (1000 m), candidates = the next
        /// ones up to 3000 m (5000 m when fewer than two), goal = a random candidate, 16 random waypoints from the rest.
        /// Null when the city has too few points ("Tool Small Spawn %d").
        /// </summary>
        public static ChasePlan PlanChase(List<ChaseSpawns.Point> points, Vector3 player, System.Random rng, float near = 1000f, float far = 3000f, float farther = 5000f)
        {
            if (points == null || points.Count < 3) return null;
            rng = rng ?? new System.Random();
            var sorted = new List<(float d, ChaseSpawns.Point p)>();
            foreach (var p in points) sorted.Add(((p.Pivot - player).sqrMagnitude, p));
            sorted.Sort((a, b) => a.d.CompareTo(b.d));
            int first = sorted.FindIndex(x => x.d > near * near);
            if (first < 0) return null;
            int lo = first + 1, hi = lo;
            while (hi < sorted.Count && sorted[hi].d <= far * far) hi++;
            if (hi - lo < 2) { hi = lo; while (hi < sorted.Count && sorted[hi].d <= farther * farther) hi++; }
            if (hi - lo < 2) return null;
            var pool = new List<ChaseSpawns.Point>();
            for (int i = lo; i < hi; i++) pool.Add(sorted[i].p);
            var plan = new ChasePlan { Start = sorted[first].p };
            int g = rng.Next(pool.Count);
            plan.Goal = pool[g];
            pool.RemoveAt(g);
            for (int k = 0; k < plan.Waypoints.Length; k++) plan.Waypoints[k] = pool[rng.Next(pool.Count)];
            return plan;
        }

        /// <summary>Slot k of a spawn point and its heading (the slots stand in a row, slot 0 at the front).</summary>
        public static Vector3 SlotHeading(ChaseSpawns.Point p, int k, out Vector3 pos)
        {
            pos = p.Slots.Count > 0 ? p.Slots[Mathf.Clamp(k, 0, p.Slots.Count - 1)] : p.Pivot;
            if (p.Slots.Count < 2) return Vector3.forward;
            int a = Mathf.Clamp(k, 1, p.Slots.Count - 1);
            var d = p.Slots[a - 1] - p.Slots[a];
            d.y = 0;
            return d.sqrMagnitude > 1e-4f ? d.normalized : Vector3.forward;
        }

        // ---------------- lanes ----------------

        /// <summary>The lane point nearest to <paramref name="pos"/> on a lane running along <paramref name="fwd"/> (any lane when none does).</summary>
        public static bool LaneAt(TrafficSystem traffic, Vector3 pos, Vector3 fwd, out int lane, out float along, float maxDistance = 150f, int except = -1)
        {
            lane = -1; along = 0;
            if (!traffic || traffic.Network == null) return false;
            fwd.y = 0;
            bool any = fwd.sqrMagnitude < 1e-4f;
            if (!any) fwd.Normalize();
            float best = maxDistance * maxDistance, bestAny = best;
            int anyLane = -1; float anyAlong = 0;
            var lanes = traffic.Network.Lanes;
            for (int i = 0; i < lanes.Count; i++)
            {
                // lanes that lead nowhere (dead ends of the network) are only taken when nothing else is near
                if (i == except || lanes[i].Next.Count == 0) continue;
                var pts = lanes[i].Points;
                float acc = 0;
                var prev = DriftCityData.ToUnity(pts[0]);
                for (int k = 0; k < pts.Length; k++)
                {
                    var w = k == 0 ? prev : DriftCityData.ToUnity(pts[k]);
                    if (k > 0) acc += Vector3.Distance(prev, w);
                    float d = (w - pos).sqrMagnitude;
                    if (d < bestAny) { bestAny = d; anyLane = i; anyAlong = acc; }
                    if (!any && d < best)
                    {
                        var nx = k + 1 < pts.Length ? DriftCityData.ToUnity(pts[k + 1]) - w : w - prev;
                        nx.y = 0;
                        if (nx.sqrMagnitude > 1e-4f && Vector3.Dot(nx.normalized, fwd) > 0.3f) { best = d; lane = i; along = acc; }
                    }
                    prev = w;
                }
            }
            if (lane < 0) { lane = anyLane; along = anyAlong; }
            return lane >= 0;
        }

        /// <summary>The lanes from <paramref name="from"/> to <paramref name="to"/> over the lane graph, both included (shortest
        /// by lane length); empty when <paramref name="to"/> cannot be reached.</summary>
        public static List<int> LanePath(TrafficNetwork net, int from, int to)
        {
            var result = new List<int>();
            if (net == null || from < 0 || to < 0 || from >= net.Lanes.Count || to >= net.Lanes.Count) return result;
            if (from == to) { result.Add(from); return result; }
            int n = net.Lanes.Count;
            var dist = new float[n]; var prev = new int[n];
            for (int i = 0; i < n; i++) { dist[i] = float.MaxValue; prev[i] = -1; }
            var open = new SortedSet<(float, int)>();
            dist[from] = 0; open.Add((0, from));
            while (open.Count > 0)
            {
                var (d, u) = open.Min; open.Remove(open.Min);
                if (u == to) break;
                if (d > dist[u]) continue;
                foreach (int v in net.Lanes[u].Next)
                {
                    float nd = d + Mathf.Max(1f, net.Lanes[v].Length);
                    if (nd < dist[v]) { if (dist[v] < float.MaxValue) open.Remove((dist[v], v)); dist[v] = nd; prev[v] = u; open.Add((nd, v)); }
                }
            }
            if (prev[to] < 0) return result;
            for (int l = to; l >= 0; l = l == from ? -1 : prev[l]) result.Add(l);
            result.Reverse();
            return result;
        }

        /// <summary>The lanes to drive after <paramref name="fromLane"/> to pass every point of <paramref name="via"/> in order
        /// (each matched to the lane nearest to it running in the direction of travel); legs that cannot be reached are skipped.</summary>
        public static List<int> RouteVia(TrafficSystem traffic, int fromLane, IList<Vector3> via, List<int> targets = null)
        {
            var route = new List<int>();
            targets?.Clear();
            if (!traffic || traffic.Network == null || fromLane < 0 || via == null) return route;
            int cur = fromLane;
            var lanes = traffic.Network.Lanes;
            var last = DriftCityData.ToUnity(lanes[cur].Points[lanes[cur].Points.Length - 1]);
            foreach (var p in via)
            {
                if (!LaneAt(traffic, p, p - last, out int target, out _, 250f)) { targets?.Add(-1); continue; }
                var leg = LanePath(traffic.Network, cur, target);
                if (leg.Count < 2) { targets?.Add(target == cur ? cur : -1); if (target == cur) last = p; continue; }
                for (int i = 1; i < leg.Count; i++) route.Add(leg[i]);
                targets?.Add(target);
                cur = target;
                last = p;
            }
            return route;
        }

        /// <summary>
        /// A boss route of Huv00.xipath2 (0x4a1470 / 0x44ab40): the route whose points pass nearest to a spot between
        /// <paramref name="min"/> and <paramref name="max"/> metres from the player (offline there is no server to pick
        /// the index), starting at that point. -1 when the city has no routes.
        /// </summary>
        public static int PickXiRoute(string city, Vector3 player, float min, float max, out int startPoint, System.Random rng = null)
        {
            startPoint = 0;
            var routes = XiPaths(city);
            if (routes.Count == 0) return -1;
            rng = rng ?? new System.Random();
            var options = new List<(int r, int k)>();
            for (int r = 0; r < routes.Count; r++)
                for (int k = 0; k < routes[r].Count; k++)
                {
                    float d = MissionPosts.Flat(routes[r][k], player);
                    if (d >= min && d <= max) options.Add((r, k));
                }
            if (options.Count == 0)
            {
                // nothing in the window: the point nearest to it
                float best = float.MaxValue; int br = 0, bk = 0;
                for (int r = 0; r < routes.Count; r++)
                    for (int k = 0; k < routes[r].Count; k++)
                    {
                        float d = Mathf.Abs(MissionPosts.Flat(routes[r][k], player) - (min + max) * 0.5f);
                        if (d < best) { best = d; br = r; bk = k; }
                    }
                startPoint = bk;
                return br;
            }
            var o = options[rng.Next(options.Count)];
            startPoint = o.k;
            return o.r;
        }

        /// <summary>The points of an xipath2 route from <paramref name="start"/> on, then round again from its first point
        /// (the route's end joins its start: the remake's boss keeps driving its route).</summary>
        public static List<Vector3> XiLoop(List<Vector3> route, int start)
        {
            var pts = new List<Vector3>();
            if (route == null || route.Count == 0) return pts;
            for (int i = start + 1; i < route.Count; i++) pts.Add(route[i]);
            for (int i = 0; i <= start && i < route.Count; i++) pts.Add(route[i]);
            return pts;
        }
    }
}
