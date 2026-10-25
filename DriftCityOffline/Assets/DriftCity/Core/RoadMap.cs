// Drift City road map "AREACHECK" (<city>\main\total.hit; only taipei and its tracks ship one), read by the
// client's FUN_004d6cd0 into the terrain object's road map and queried by FUN_004d6a50 (DriftCity_RE/ROADS.md):
//   char[16] "AREACHECK", u32 version (0x1328f01), i32 vertexCount, i32 triangleCount, i32 nodeCount,
//   vertices: vertexCount x {f32 x, y, z}
//   triangles: {i32 type, i32 v0, v1, v2} + {f32 dirX, dirY} when type == 1 (unit driving direction)
//   quadtree nodes: {f32 minX, minY, (maxX), f32 maxX, maxY, (i32 level?), i32 child[4], i32 count, i32 tri[count]}
// The client reads a node's 12-byte min and 12-byte max over each other, so only min/max x/y survive; child 0
// means none (node 0 is the root).
using System;
using DriftCity.Core.Nif;

namespace DriftCity.Core
{
    public sealed class RoadMap
    {
        public struct Triangle { public int Type, A, B, C; public float DirX, DirY; }
        public struct Node { public float MinX, MinY, MaxX, MaxY; public int C0, C1, C2, C3; public int[] Tris; }

        public uint Version;
        public Vec3[] Vertices = new Vec3[0];
        public Triangle[] Triangles = new Triangle[0];
        public Node[] Nodes = new Node[0];

        /// <summary>FUN_004d6a50 results.</summary>
        public const int NoRoad = 0, Along = 1, Against = 2, OffRoad3 = 3, OffRoad4 = 4, Unknown = -1;

        public static RoadMap Parse(byte[] d)
        {
            var m = new RoadMap();
            if (d == null || d.Length < 32) return m;
            m.Version = BitConverter.ToUInt32(d, 16);
            int nv = BitConverter.ToInt32(d, 20), nt = BitConverter.ToInt32(d, 24), nn = BitConverter.ToInt32(d, 28);
            int p = 32;
            if (nv < 0 || nt < 0 || nn < 0 || p + (long)nv * 12 > d.Length) return m;
            m.Vertices = new Vec3[nv];
            for (int i = 0; i < nv; i++, p += 12)
                m.Vertices[i] = new Vec3(BitConverter.ToSingle(d, p), BitConverter.ToSingle(d, p + 4), BitConverter.ToSingle(d, p + 8));
            m.Triangles = new Triangle[nt];
            for (int i = 0; i < nt && p + 16 <= d.Length; i++)
            {
                var t = new Triangle
                {
                    Type = BitConverter.ToInt32(d, p), A = BitConverter.ToInt32(d, p + 4),
                    B = BitConverter.ToInt32(d, p + 8), C = BitConverter.ToInt32(d, p + 12),
                };
                p += 16;
                if (t.Type == 1 && p + 8 <= d.Length) { t.DirX = BitConverter.ToSingle(d, p); t.DirY = BitConverter.ToSingle(d, p + 4); p += 8; }
                if ((uint)t.A >= (uint)nv || (uint)t.B >= (uint)nv || (uint)t.C >= (uint)nv) t.A = t.B = t.C = 0;
                m.Triangles[i] = t;
            }
            m.Nodes = new Node[nn];
            for (int i = 0; i < nn && p + 44 <= d.Length; i++)
            {
                var n = new Node
                {
                    MinX = BitConverter.ToSingle(d, p), MinY = BitConverter.ToSingle(d, p + 4),
                    MaxX = BitConverter.ToSingle(d, p + 12), MaxY = BitConverter.ToSingle(d, p + 16),
                    C0 = BitConverter.ToInt32(d, p + 24), C1 = BitConverter.ToInt32(d, p + 28),
                    C2 = BitConverter.ToInt32(d, p + 32), C3 = BitConverter.ToInt32(d, p + 36),
                };
                int count = BitConverter.ToInt32(d, p + 40);
                p += 44;
                count = Math.Max(0, Math.Min(count, (d.Length - p) / 4));
                n.Tris = new int[count];
                for (int k = 0; k < count; k++, p += 4) n.Tris[k] = BitConverter.ToInt32(d, p);
                m.Nodes[i] = n;
            }
            return m;
        }

        /// <summary>
        /// FUN_004d6a50: walks the quadtree from the root; children first (the largest child result wins), then the
        /// node's own triangles in order, hit by a ray from (x, y, z + 5) straight down. The first triangle hit
        /// decides: type 0 -> 0, type 1 -> 1 when heading . dir > 0 else 2 (against the road), type 2 -> 3,
        /// type 3 -> 4, other -> -1. 0 also when nothing is hit.
        /// </summary>
        public int Query(float x, float y, float z, float headingX, float headingY, out float height)
        {
            height = z;
            return Nodes.Length == 0 ? NoRoad : QueryNode(0, x, y, z, headingX, headingY, ref height, 0);
        }

        int QueryNode(int index, float x, float y, float z, float hx, float hy, ref float height, int depth)
        {
            if ((uint)index >= (uint)Nodes.Length || depth > 64) return NoRoad;
            ref Node n = ref Nodes[index];
            if (x < n.MinX || y < n.MinY || x > n.MaxX || y > n.MaxY) return NoRoad;
            int best = 0;
            float bestHeight = height;
            foreach (int c in new[] { n.C0, n.C1, n.C2, n.C3 })
            {
                if (c <= 0) continue;
                float h = height;
                int r = QueryNode(c, x, y, z, hx, hy, ref h, depth + 1);
                if (best < r) { best = r; bestHeight = h; }
            }
            if (best != 0) { height = bestHeight; return best; }
            float oz = z + 5f;
            foreach (int ti in n.Tris)
            {
                if ((uint)ti >= (uint)Triangles.Length) continue;
                var t = Triangles[ti];
                Vec3 a = Vertices[t.A], b = Vertices[t.B], c = Vertices[t.C];
                if (!HitMesh.Barycentric(a, b, c, x, y, out float u, out float v)) continue;
                float hz = a.Z + (b.Z - a.Z) * u + (c.Z - a.Z) * v;
                if (hz > oz) continue;   // D3DXIntersectTri only counts hits in front of the ray origin
                height = hz;
                switch (t.Type)
                {
                    case 0: return NoRoad;
                    case 1: return hx * t.DirX + t.DirY * hy <= 0f ? Against : Along;
                    case 2: return OffRoad3;
                    case 3: return OffRoad4;
                    default: return Unknown;
                }
            }
            return NoRoad;
        }
    }
}
