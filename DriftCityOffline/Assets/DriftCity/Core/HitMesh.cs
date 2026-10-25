// Drift City collision / zone meshes (<city>\main\floor_m.hit, wall_m.hit, exclude.hit, sand.hit, water.hit,
// track*\area.hit), read by the client's FUN_004e5650 (DriftCity_RE/ROADS.md):
//   char[4] "hit\0", i32 version, i32 indexCount, i32 indices[indexCount] (triangle list),
//   i32 vertexCount, then the vertices:
//     version <  0x1321a30             : 16 bytes {x, y, z, w}
//     version in [0x1321a30, 0x1328f05): 12 bytes {x, y, z}, w = 0
//     version >= 0x1328f05             : 16 bytes {x, y, z, w} and every triangle's 2nd/3rd index swapped on load.
// Gamebryo world coordinates (z up). w is a per-vertex attribute: the client reads the w of a hit triangle's first
// vertex as a surface class (taipei floor: -1, 0, 1, 2, 3; 2 and 3 count as off the road).
// The zone files (exclude/sand/water) are loaded flattened (z = 0) and tested with a vertical ray, i.e. a 2D
// point-in-polygon test; the client builds an ODE trimesh from each file and casts 100000-long rays at it.
using System;
using System.Collections.Generic;
using System.Text;
using DriftCity.Core.Nif;

namespace DriftCity.Core
{
    public sealed class HitMesh
    {
        public int Version;
        public Vec3[] Vertices = new Vec3[0];
        public float[] Attributes = new float[0];
        public int[] Indices = new int[0];
        public int TriangleCount => Indices.Length / 3;

        // uniform grid over the triangles' x/y bounds (cells hold triangle numbers)
        float minX, minY, cell = 16f;
        int cols, rows;
        int[] cellStart, cellTris;

        public static HitMesh Parse(byte[] d, bool flatten = false)
        {
            var m = new HitMesh();
            // (the parking lot's files say "HIT")
            if (d == null || d.Length < 16 || !string.Equals(Encoding.ASCII.GetString(d, 0, 3), "hit", StringComparison.OrdinalIgnoreCase)) return m;
            m.Version = BitConverter.ToInt32(d, 4);
            int ni = BitConverter.ToInt32(d, 8);
            int p = 12;
            if (ni < 0 || p + ni * 4L + 4 > d.Length) return m;
            var idx = new int[ni - ni % 3];
            for (int i = 0; i < idx.Length; i++) idx[i] = BitConverter.ToInt32(d, p + i * 4);
            p += ni * 4;
            int nv = BitConverter.ToInt32(d, p); p += 4;
            bool packed = m.Version >= 0x1321a30 && m.Version < 0x1328f05;
            int stride = packed ? 12 : 16;
            if (nv < 0 || p + (long)nv * stride > d.Length) return m;
            m.Vertices = new Vec3[nv];
            m.Attributes = new float[nv];
            for (int i = 0; i < nv; i++, p += stride)
            {
                float z = flatten ? 0f : BitConverter.ToSingle(d, p + 8);
                m.Vertices[i] = new Vec3(BitConverter.ToSingle(d, p), BitConverter.ToSingle(d, p + 4), z);
                if (!packed) m.Attributes[i] = BitConverter.ToSingle(d, p + 12);
            }
            if (m.Version >= 0x1328f05)
                for (int i = 0; i + 2 < idx.Length; i += 3) { int t = idx[i + 1]; idx[i + 1] = idx[i + 2]; idx[i + 2] = t; }
            for (int i = 0; i < idx.Length; i++) if ((uint)idx[i] >= (uint)nv) idx[i] = 0;
            m.Indices = idx;
            m.BuildGrid();
            return m;
        }

        void BuildGrid()
        {
            int nt = TriangleCount;
            if (nt == 0 || Vertices.Length == 0) return;
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (var v in Vertices)
            {
                if (v.X < x0) x0 = v.X; if (v.X > x1) x1 = v.X;
                if (v.Y < y0) y0 = v.Y; if (v.Y > y1) y1 = v.Y;
            }
            // keep the grid around a million cells for the big floors
            cell = Math.Max(8f, (float)Math.Sqrt((x1 - x0) * (double)(y1 - y0) / 1_000_000.0));
            minX = x0; minY = y0;
            cols = Math.Max(1, (int)((x1 - x0) / cell) + 1);
            rows = Math.Max(1, (int)((y1 - y0) / cell) + 1);
            var count = new int[cols * rows + 1];
            for (int pass = 0; pass < 2; pass++)
            {
                if (pass == 1)
                {
                    // count[k] becomes the start of cell k and then the write cursor
                    for (int i = 1; i < count.Length; i++) count[i] += count[i - 1];
                    cellStart = (int[])count.Clone();
                    cellTris = new int[count[count.Length - 1]];
                }
                for (int t = 0; t < nt; t++)
                {
                    Vec3 a = Vertices[Indices[t * 3]], b = Vertices[Indices[t * 3 + 1]], c = Vertices[Indices[t * 3 + 2]];
                    int cx0 = CellX(Math.Min(a.X, Math.Min(b.X, c.X))), cx1 = CellX(Math.Max(a.X, Math.Max(b.X, c.X)));
                    int cy0 = CellY(Math.Min(a.Y, Math.Min(b.Y, c.Y))), cy1 = CellY(Math.Max(a.Y, Math.Max(b.Y, c.Y)));
                    for (int y = cy0; y <= cy1; y++)
                        for (int x = cx0; x <= cx1; x++)
                        {
                            int k = y * cols + x;
                            if (pass == 0) count[k + 1]++;
                            else cellTris[count[k]++] = t;
                        }
                }
            }
        }

        int CellX(float x) => Math.Min(cols - 1, Math.Max(0, (int)((x - minX) / cell)));
        int CellY(float y) => Math.Min(rows - 1, Math.Max(0, (int)((y - minY) / cell)));

        bool Cell(float x, float y, out int start, out int end)
        {
            start = end = 0;
            if (cellStart == null) return false;
            float fx = (x - minX) / cell, fy = (y - minY) / cell;
            if (fx < 0f || fy < 0f || fx >= cols || fy >= rows) return false;
            int k = (int)fy * cols + (int)fx;
            start = cellStart[k]; end = cellStart[k + 1];
            return end > start;
        }

        /// <summary>
        /// Zone test (FUN_004e53d0 on a flattened mesh): the client casts a ray from (x, y, 10) straight down, so a
        /// point is inside when it lies in any triangle's x/y projection.
        /// </summary>
        public bool Contains(float x, float y)
        {
            if (!Cell(x, y, out int s, out int e)) return false;
            for (int i = s; i < e; i++)
            {
                int t = cellTris[i];
                if (Barycentric(Vertices[Indices[t * 3]], Vertices[Indices[t * 3 + 1]], Vertices[Indices[t * 3 + 2]], x, y, out _, out _)) return true;
            }
            return false;
        }

        /// <summary>
        /// FUN_004e5980: a 100000-long ray from (x, y, fromZ) straight down (two-sided, as the client's ray has
        /// back-face culling off). Returns the highest hit, and the w attribute of that triangle's first vertex.
        /// </summary>
        public bool RayDown(float x, float y, float fromZ, out float z, out int attribute)
        {
            z = fromZ; attribute = 0;
            if (!Cell(x, y, out int s, out int e)) return false;
            bool hit = false;
            float best = -1e6f;
            for (int i = s; i < e; i++)
            {
                int t = cellTris[i];
                int ia = Indices[t * 3];
                Vec3 a = Vertices[ia], b = Vertices[Indices[t * 3 + 1]], c = Vertices[Indices[t * 3 + 2]];
                if (!Barycentric(a, b, c, x, y, out float u, out float v)) continue;
                float hz = a.Z + (b.Z - a.Z) * u + (c.Z - a.Z) * v;
                if (hz > fromZ || hz < fromZ - 100000f || hz <= best) continue;
                best = hz; hit = true;
                attribute = (int)Attributes[ia];
            }
            if (hit) z = best;
            return hit;
        }

        /// <summary>x/y barycentric coordinates of (x, y) in triangle abc (u along ab, v along ac); edges count as inside.</summary>
        internal static bool Barycentric(Vec3 a, Vec3 b, Vec3 c, float x, float y, out float u, out float v)
        {
            u = v = 0f;
            float e1x = b.X - a.X, e1y = b.Y - a.Y, e2x = c.X - a.X, e2y = c.Y - a.Y;
            float det = e1x * e2y - e1y * e2x;
            if (Math.Abs(det) < 1e-12f) return false;
            float px = x - a.X, py = y - a.Y;
            u = (px * e2y - py * e2x) / det;
            v = (e1x * py - e1y * px) / det;
            return u >= 0f && v >= 0f && u + v <= 1f;
        }
    }
}
