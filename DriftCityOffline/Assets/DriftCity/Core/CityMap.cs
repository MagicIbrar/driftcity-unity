// Readers for Drift City's open-city formats (Data\World\<city>.agt). All share a 16-byte
// header: char[4] tag ("LF", "LBF", "LOF", "LOI"), "kjc\0ag\0\0", u32 build stamp.
//
// terrain0.lf   : u32 ?, u32 cellCount, ..., at 0x4C u32 cols, u32 rows, u32 cellCount, f32 cellSize,
//                 then from 0x6C cellCount x {u32 index, u32 gx, u32 gy, u32 offset, u32 size, u32 flag},
//                 each pointing at an embedded NIF already in world coordinates (the grid only indexes cells).
// blockobj0.lbf : per-cell building NIFs named "Block <cellIndex>" (world coordinates), interleaved with
//                 collision data; located by scanning for embedded NIF headers.
// modeltable0.lof: u32 modelCount, then records starting with 0x0082 + model name and an
//                 offset/size pair to an embedded NIF (model space). Record order = model index.
// object0.loi   : u32 cellCount, then per cell {u32 cell, u32 count, count x 96-byte instance}:
//                 +24 u32 model id (see modeltable.nxt / <city>.xlt), +28 vec3 world translation, +40 3x3 rotation, +76 scale.
using System;
using System.Collections.Generic;
using System.Text;
using DriftCity.Core.Nif;

namespace DriftCity.Core
{
    public struct EmbeddedNif { public int Offset, Size; public string Name; }

    public sealed class CityTerrain
    {
        public int Cols, Rows;
        public float CellSize = 200f;
        public byte[] Data;
        public List<(int index, int gx, int gy, EmbeddedNif nif)> Cells = new List<(int, int, int, EmbeddedNif)>();

        public static CityTerrain Parse(byte[] d)
        {
            var t = new CityTerrain { Data = d };
            int count = BitConverter.ToInt32(d, 0x14);
            t.Cols = BitConverter.ToInt32(d, 0x4C);
            t.Rows = BitConverter.ToInt32(d, 0x50);
            float size = BitConverter.ToSingle(d, 0x58);
            if (size > 1 && size < 10000) t.CellSize = size;
            // the record table normally starts at 0x6C; locate it from the first embedded NIF to be safe
            int tableStart = 0x6C;
            var nifs = CityFormats.FindNifs(d);
            if (nifs.Count > 0)
            {
                int first = nifs[0];
                for (int q = 0x18; q + 4 <= first; q += 4)
                    if (BitConverter.ToInt32(d, q) == first) { tableStart = q - 12; break; }
            }
            for (int i = 0; i < count; i++)
            {
                int p = tableStart + 24 * i;
                if (p + 24 > d.Length) break;
                int offset = BitConverter.ToInt32(d, p + 12), len = BitConverter.ToInt32(d, p + 16);
                if (offset <= 0 || len <= 0 || offset + len > d.Length || !CityFormats.IsNif(d, offset)) continue;
                t.Cells.Add((BitConverter.ToInt32(d, p), BitConverter.ToInt32(d, p + 4), BitConverter.ToInt32(d, p + 8),
                             new EmbeddedNif { Offset = offset, Size = len }));
            }
            return t;
        }
    }

    public struct CityInstance
    {
        public int Cell, Model, Id;
        public Vec3 Translation;
        public Mat3 Rotation;
        public float Scale;
    }

    public static class CityFormats
    {
        static readonly byte[] NifMagic = Encoding.ASCII.GetBytes("Gamebryo File Format");

        /// <summary>Offsets of every embedded NIF header in a blob.</summary>
        public static List<int> FindNifs(byte[] d)
        {
            var list = new List<int>();
            int i = 0;
            while ((i = IndexOf(d, NifMagic, i)) >= 0) { list.Add(i); i += NifMagic.Length; }
            return list;
        }

        public static bool IsNif(byte[] d, int offset)
        {
            if (offset < 0 || offset + NifMagic.Length > d.Length) return false;
            for (int k = 0; k < NifMagic.Length; k++) if (d[offset + k] != NifMagic[k]) return false;
            return true;
        }

        static int IndexOf(byte[] d, byte[] pat, int from)
        {
            for (int i = from; i <= d.Length - pat.Length; i++)
            {
                if (d[i] != pat[0]) continue;
                int k = 1;
                while (k < pat.Length && d[i + k] == pat[k]) k++;
                if (k == pat.Length) return i;
            }
            return -1;
        }

        /// <summary>Building blocks: each embedded NIF runs until the next one (trailing collision data is ignored by the parser).</summary>
        public static List<EmbeddedNif> BlockNifs(byte[] lbf)
        {
            var starts = FindNifs(lbf);
            var list = new List<EmbeddedNif>(starts.Count);
            for (int i = 0; i < starts.Count; i++)
            {
                int end = i + 1 < starts.Count ? starts[i + 1] : lbf.Length;
                list.Add(new EmbeddedNif { Offset = starts[i], Size = end - starts[i] });
            }
            return list;
        }

        /// <summary>
        /// Per-cell world streams (lane0.llf road markings, guardrail0.lgf guard rails): a 4-byte tag ("LLF\0"/"LGF\0"),
        /// "kjc\0", "ag\0\0", u32 stamp, u32 ?, u32 cell count at 0x14, u32 0, then count x {u32 offset, u32 size,
        /// u32 cell} and the NIF blobs (cells without content hold a tiny empty NIF).
        /// </summary>
        public static List<EmbeddedNif> CellStream(byte[] d, int minSize = 512)
        {
            var list = new List<EmbeddedNif>();
            if (d == null || d.Length < 28) return list;
            int count = BitConverter.ToInt32(d, 0x14);
            for (int i = 0; i < count && 28 + 12 * i + 12 <= d.Length; i++)
            {
                int offset = BitConverter.ToInt32(d, 28 + 12 * i), size = BitConverter.ToInt32(d, 32 + 12 * i);
                if (size < minSize || offset < 0 || offset + size > d.Length || !IsNif(d, offset)) continue;
                list.Add(new EmbeddedNif { Offset = offset, Size = size, Name = BitConverter.ToInt32(d, 36 + 12 * i).ToString() });
            }
            return list;
        }

        /// <summary>Model table: index -> embedded NIF, using each record's offset field matched against real NIF starts.</summary>
        public static List<EmbeddedNif> Models(byte[] lof)
        {
            var starts = new HashSet<int>(FindNifs(lof));
            int tableEnd = int.MaxValue;
            foreach (var s in starts) tableEnd = Math.Min(tableEnd, s);
            var models = new List<EmbeddedNif>();
            int p = 0x18;
            while (p + 4 < tableEnd)
            {
                // record marker 0x82 0x00 followed by a printable model name
                if (lof[p] == 0x82 && lof[p + 1] == 0x00 && lof[p + 2] >= 0x20 && lof[p + 2] < 0x7F)
                {
                    int ns = p + 2, ne = ns;
                    while (ne < tableEnd && lof[ne] >= 0x20 && lof[ne] < 0x7F) ne++;
                    string name = Encoding.ASCII.GetString(lof, ns, ne - ns);
                    var model = new EmbeddedNif { Name = name, Offset = -1 };
                    for (int q = ne; q + 8 <= Math.Min(tableEnd, ne + 80); q++)
                    {
                        int off = BitConverter.ToInt32(lof, q);
                        if (starts.Contains(off))
                        {
                            model.Offset = off;
                            model.Size = BitConverter.ToInt32(lof, q + 4);
                            if (model.Size <= 0 || off + model.Size > lof.Length) model.Size = lof.Length - off;
                            break;
                        }
                    }
                    models.Add(model);
                    p = ne;
                    continue;
                }
                p++;
            }
            return models;
        }

        /// <summary>
        /// Model library keyed by model ID. modeltable.nxt (CP949 TSV: ID, category, type, description,
        /// file name, flags...) gives ID -> file name; the file name is then located in the .lof record
        /// table, whose record carries the offset/size of the embedded NIF.
        /// </summary>
        public static Dictionary<int, EmbeddedNif> ModelsById(byte[] lof, byte[] nameList) =>
            ModelsById(lof, ModelNames(nameList));

        /// <summary>(id, file name) pairs from either modeltable.nxt (CP949 TSV) or a city .xlt (UTF-16 TSV).</summary>
        public static List<(int id, string file)> ModelNames(byte[] list)
        {
            string text = list.Length >= 2 && list[0] == 0xFF && list[1] == 0xFE ? Encoding.Unicode.GetString(list, 2, list.Length - 2) : Cp949.Decode(list);
            var result = new List<(int, string)>();
            foreach (var line in text.Split('\n'))
            {
                var cols = line.TrimEnd('\r').Split('\t');
                if (cols.Length < 2 || !int.TryParse(cols[0].Trim(), out int id)) continue;
                foreach (var c in cols)
                {
                    var n = c.Trim().Trim('"');
                    if (n.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)) { result.Add((id, n)); break; }
                }
            }
            return result;
        }

        /// <summary>
        /// Model library keyed by model ID: each listed file name is located in the .lof record table,
        /// whose record carries the offset/size of the embedded NIF.
        /// </summary>
        public static Dictionary<int, EmbeddedNif> ModelsById(byte[] lof, List<(int id, string file)> names)
        {
            var starts = new HashSet<int>(FindNifs(lof));
            int tableEnd = int.MaxValue;
            foreach (var s in starts) tableEnd = Math.Min(tableEnd, s);
            if (tableEnd == int.MaxValue) tableEnd = lof.Length;

            var byName = new Dictionary<string, EmbeddedNif>(StringComparer.OrdinalIgnoreCase);
            for (int p = 0x18; p + 4 <= tableEnd; p++)
            {
                if (lof[p] != '.' || lof[p + 1] != 'n' || lof[p + 2] != 'i' || lof[p + 3] != 'f') continue;
                int ns = p;
                while (ns > 0x18 && lof[ns - 1] >= 0x21 && lof[ns - 1] < 0x7F) ns--;
                string name = Encoding.ASCII.GetString(lof, ns, p + 4 - ns);
                for (int q = p + 4; q + 8 <= Math.Min(tableEnd, p + 96); q++)
                {
                    int off = BitConverter.ToInt32(lof, q);
                    if (!starts.Contains(off)) continue;
                    int size = BitConverter.ToInt32(lof, q + 4);
                    if (size <= 0 || off + size > lof.Length) size = lof.Length - off;
                    if (!byName.ContainsKey(name)) byName[name] = new EmbeddedNif { Name = name, Offset = off, Size = size };
                    break;
                }
            }

            var result = new Dictionary<int, EmbeddedNif>();
            foreach (var (id, file) in names)
                if (byName.TryGetValue(System.IO.Path.GetFileName(file), out var nif)) result[id] = nif;
            return result;
        }

        public static List<CityInstance> Instances(byte[] loi, int cellCount)
        {
            var list = new List<CityInstance>();
            int p = 0x14, last = -1;
            while (p + 8 <= loi.Length)
            {
                int cell = BitConverter.ToInt32(loi, p), count = BitConverter.ToInt32(loi, p + 4);
                if (cell != last + 1 || count < 0 || count > 100000 || cell >= cellCount) break; // end of the cell section
                last = cell;
                p += 8;
                for (int k = 0; k < count && p + 96 <= loi.Length; k++, p += 96)
                {
                    var r = new NifReader(loi) { Pos = p + 28 };
                    var inst = new CityInstance
                    {
                        Cell = cell,
                        Id = BitConverter.ToInt32(loi, p + 16),
                        Model = BitConverter.ToInt32(loi, p + 24),
                        Translation = r.V3(),
                        Rotation = r.M33(),
                    };
                    inst.Scale = r.F32();
                    list.Add(inst);
                }
            }
            return list;
        }

        public static byte[] Slice(byte[] d, EmbeddedNif n)
        {
            var b = new byte[n.Size];
            Buffer.BlockCopy(d, n.Offset, b, 0, n.Size);
            return b;
        }
    }
}
