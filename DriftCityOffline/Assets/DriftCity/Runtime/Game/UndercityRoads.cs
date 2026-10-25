using System;
using System.Collections.Generic;
using UnityEngine;
using DriftCity.Core;

namespace DriftCity.Game
{
    /// <summary>
    /// The Undercity's roadside HUB towers as the client places them (0x716f80 -> 0x716890, DriftCity_RE/UNDERCITY.md):
    /// the road map of &lt;city&gt;.tcs is walked junction by junction and each road met gets one tower, on the lane
    /// "slot % lanes" of the road's side, in the middle of that lane (0x711a10: the point at (t0 + t1) / 2 of the piece).
    /// <list type="bullet">
    /// <item>0x716890(set, slot, side, start): junctions start, start + 2, ... of the road map's list (+0xd8 into +0x98); per
    /// junction its road links (+0x58 / +0x5c, link +0x10 = the road); a road whose id (+8) is on the city's list is skipped
    /// (1st 9 / 0x5a, 2nd 0xaa / 0xab, 3rd 0x12 / 0x34 / 0x7d, 4th 0x55 / 0x54 / 0x15); side 1 takes the road's lane list
    /// +0x14, side 0 the list +0x20; the walk stops at slot 100. [exact]</item>
    /// <item>0x716f80 calls it four times from slot 10: (side 1, start 1), (side 0, start 0), and while slots are left
    /// (side 0, start 0), (side 1, start 1) again - the slots differ, so "slot % lanes" picks other lanes. [exact]</item>
    /// <item>The .tcs records [exact data]: 0x12200 junctions {idx, node, croad count, croad ids, path count, paths}; 0x50200
    /// CROADs (the road ends at a junction: {idx, lane count, lanes A, lanes B, lane ids}); 0x20200 ROADs {idx, lane count,
    /// lanes A, lanes B, lane ids (0x70300 ids: A runs start -> end node, B back)}, ROAD i = the i-th NDPATH (0x3100:
    /// {node, kind, [extra when kind 1], k, start node, end node, ...}). [inferred] the road map's junction list is the 0x12200 order, a junction's links are its CROADs in order
    /// and a link's road is the ROAD that ends at the junction next to the CROAD's lanes; the road id is its index; side 1
    /// (+0x14) = lanes A.</item>
    /// </list>
    /// </summary>
    public static class UndercityRoads
    {
        public sealed class Road
        {
            public int Index, StartNode = -1, EndNode = -1;
            public int[] LanesA = Array.Empty<int>(), LanesB = Array.Empty<int>();
        }

        public sealed class Junction
        {
            public int Index, Node;
            public readonly List<int> Roads = new List<int>();
        }

        public sealed class Map
        {
            public readonly List<Road> Roads = new List<Road>();
            public readonly List<Junction> Junctions = new List<Junction>();
        }

        /// <summary>0x716890's skipped road ids per city index (1 Moon Palace, 2 Koinonia, 3 Cras, 4 Oros).</summary>
        public static int[] Skipped(int cityIndex) => cityIndex switch
        {
            1 => new[] { 9, 0x5a },
            2 => new[] { 0xaa, 0xab },
            3 => new[] { 0x12, 0x34, 0x7d },
            4 => new[] { 0x55, 0x54, 0x15 },
            _ => Array.Empty<int>(),
        };

        static readonly Dictionary<string, Map> cache = new Dictionary<string, Map>();

        /// <summary>The road map of a city's .tcs (cached); null when the file is missing.</summary>
        public static Map For(string city)
        {
            if (string.IsNullOrEmpty(city)) return null;
            if (cache.TryGetValue(city, out var m)) return m;
            try
            {
                var fs = DriftCityData.Files;
                string path = $@"{city}\{city}.tcs";
                m = fs != null && fs.Exists(path) ? Parse(fs.Read(path)) : null;
            }
            catch (Exception ex) { Debug.LogWarning("Undercity road map: " + ex.Message); m = null; }
            cache[city] = m;
            return m;
        }

        /// <summary>Reads the junctions, CROADs, ROADs and the NDPATH node pairs and links each CROAD to its ROAD.</summary>
        public static Map Parse(byte[] d)
        {
            var map = new Map();
            int body = -1;
            for (int q = 8; q + 16 < d.Length && q < 256; q++)
                if (BitConverter.ToInt32(d, q) == 5 && BitConverter.ToInt32(d, q + 4) == d.Length - q) { body = q + 8; break; }
            if (body < 0) return map;
            var recs = new Dictionary<uint, List<(int at, int len)>>();
            void Walk(int p, int end, int depth)
            {
                while (p + 8 <= end)
                {
                    uint tag = BitConverter.ToUInt32(d, p);
                    int size = BitConverter.ToInt32(d, p + 4);
                    if (size < 8 || p + size > end) return;
                    if ((tag & 0xFFF) == 0 && depth < 3) Walk(p + 8, p + size, depth + 1);
                    else { if (!recs.TryGetValue(tag, out var l)) recs[tag] = l = new List<(int, int)>(); l.Add((p + 8, size - 8)); }
                    p += size;
                }
            }
            Walk(body, d.Length, 0);
            int I(int at, int k) => BitConverter.ToInt32(d, at + 4 * k);
            List<(int at, int len)> Get(uint tag) => recs.TryGetValue(tag, out var l) ? l : new List<(int, int)>();

            // ROADs (0x20200) {-1, idx, n, a, b, ids[n], ...} and their nodes from NDPATH (0x3100) {idx, kind, [extra], ?, start, end}
            foreach (var (at, len) in Get(0x20200))
            {
                if (len < 20) continue;
                int idx = I(at, 1), n = I(at, 2), a = I(at, 3), b = I(at, 4);
                if (n < 0 || n > 64 || a + b != n || 20 + 4 * n > len) continue;
                var r = new Road { Index = idx, LanesA = new int[a], LanesB = new int[b] };
                for (int k = 0; k < a; k++) r.LanesA[k] = I(at, 5 + k);
                for (int k = 0; k < b; k++) r.LanesB[k] = I(at, 5 + a + k);
                map.Roads.Add(r);
            }
            var byIndex = new Dictionary<int, Road>();
            foreach (var r in map.Roads) byIndex[r.Index] = r;
            // the k-th NDPATH belongs to the k-th ROAD record (same order; ROAD indexes have gaps, NDPATH's first int is a node)
            var paths = Get(0x3100);
            for (int k = 0; k < paths.Count && k < map.Roads.Count; k++)
            {
                var (at, len) = paths[k];
                if (len < 24) continue;
                int o = I(at, 1) == 1 ? 3 : 2;
                var r = map.Roads[k];
                r.StartNode = I(at, o + 1); r.EndNode = I(at, o + 2);
            }
            // CROADs (0x50200) {-1, idx, n, a, b, ids[n]}: their lanes
            var croads = new Dictionary<int, int[]>();
            foreach (var (at, len) in Get(0x50200))
            {
                if (len < 20) continue;
                int idx = I(at, 1), n = I(at, 2);
                if (n < 0 || n > 64 || 20 + 4 * n > len) continue;
                var ids = new int[n];
                for (int k = 0; k < n; k++) ids[k] = I(at, 5 + k);
                croads[idx] = ids;
            }
            // lane end points (0x70300 {-1, id, f, n, points}) to tie a CROAD to its road
            var ends = new Dictionary<int, (Vector3 a, Vector3 b)>();
            foreach (var (at, len) in Get(0x70300))
            {
                if (len < 16) continue;
                int id = I(at, 1), n = I(at, 3);
                if (n < 2 || n > 64 || 16 + 12 * n > len) continue;
                Vector3 P(int k) { int o = at + 16 + 12 * k; return new Vector3(BitConverter.ToSingle(d, o), BitConverter.ToSingle(d, o + 4), BitConverter.ToSingle(d, o + 8)); }
                ends[id] = (P(0), P(n - 1));
            }
            // junctions (0x12200) {-1, idx, node, c, croads[c], ...}
            foreach (var (at, len) in Get(0x12200))
            {
                if (len < 16) continue;
                int idx = I(at, 1), node = I(at, 2), c = I(at, 3);
                if (c < 0 || c > 16 || 16 + 4 * c > len) continue;
                var j = new Junction { Index = idx, Node = node };
                for (int k = 0; k < c; k++)
                {
                    int cr = I(at, 4 + k);
                    int road = RoadOf(map, node, croads.TryGetValue(cr, out var cl) ? cl : null, ends);
                    if (road >= 0) j.Roads.Add(road);
                }
                map.Junctions.Add(j);
            }
            return map;
        }

        /// <summary>The road ending at <paramref name="node"/> whose lane ends lie nearest to the CROAD's lanes.</summary>
        static int RoadOf(Map map, int node, int[] croadLanes, Dictionary<int, (Vector3 a, Vector3 b)> ends)
        {
            int best = -1; float bestD = float.MaxValue;
            foreach (var r in map.Roads)
            {
                if (r.StartNode != node && r.EndNode != node) continue;
                if (croadLanes == null || croadLanes.Length == 0) { if (best < 0) best = r.Index; continue; }
                float dmin = float.MaxValue;
                foreach (int cl in croadLanes)
                {
                    if (!ends.TryGetValue(cl, out var ce)) continue;
                    foreach (int rl in r.LanesA) if (ends.TryGetValue(rl, out var re)) dmin = Mathf.Min(dmin, Mathf.Min(Vector3.Distance(ce.a, re.b), Vector3.Distance(ce.b, re.a)));
                    foreach (int rl in r.LanesB) if (ends.TryGetValue(rl, out var re)) dmin = Mathf.Min(dmin, Mathf.Min(Vector3.Distance(ce.a, re.b), Vector3.Distance(ce.b, re.a)));
                }
                if (dmin < bestD) { bestD = dmin; best = r.Index; }
            }
            return best;
        }

        /// <summary>One tower: its slot and the .tcs lane id it stands in the middle of.</summary>
        public struct Pick { public int Slot, LaneId, Road; public bool SideA; }

        /// <summary>0x716f80's four walks of 0x716890 from <paramref name="firstSlot"/> (10) up to slot 99.</summary>
        public static List<Pick> TowerPicks(Map map, int cityIndex, int firstSlot = 10, int lastSlot = 99)
        {
            var picks = new List<Pick>();
            if (map == null) return picks;
            var skip = new HashSet<int>(Skipped(cityIndex));
            var roads = new Dictionary<int, Road>();
            foreach (var r in map.Roads) roads[r.Index] = r;
            int slot = firstSlot;
            void Walk(bool sideA, int start)
            {
                for (int ji = start; ji < map.Junctions.Count && slot <= lastSlot; ji += 2)
                    foreach (int ri in map.Junctions[ji].Roads)
                    {
                        if (!roads.TryGetValue(ri, out var r) || skip.Contains(r.Index)) continue;
                        var lanes = sideA ? r.LanesA : r.LanesB;
                        if (lanes.Length == 0) continue;
                        if (slot > lastSlot) break;
                        picks.Add(new Pick { Slot = slot, LaneId = lanes[slot % lanes.Length], Road = r.Index, SideA = sideA });
                        slot++;
                    }
            }
            Walk(true, 1);
            Walk(false, 0);
            if (slot <= lastSlot) { Walk(false, 0); Walk(true, 1); }
            return picks;
        }
    }
}
