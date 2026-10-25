using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// The HUV spawn points of a city: &lt;city&gt;\chasespawnlist.text (text: count, then per point "posID", "x y z //pivot",
    /// "name", the slot count (16) and per slot "x y z //pos" + "rx ry rz //rad", Gamebryo axes) with the names of
    /// init\chasespawn.xlt (Index, CityIdx 0-3, PointID, "pos name": "Gold Wing Memorial Airport" ...). The server's chase
    /// start (BS_PktChaseBeginConfirm 0x4f2500) placed up to 16 HUV units; the slots are those places in a row.
    /// </summary>
    public static class ChaseSpawns
    {
        public sealed class Point
        {
            public int Id;
            public string Name;
            public Vector3 Pivot;
            public readonly List<Vector3> Slots = new List<Vector3>();
        }

        static readonly Dictionary<string, List<Point>> cache = new Dictionary<string, List<Point>>();
        static Dictionary<(int, int), string> names;

        static int CityIndex(string city) => city switch { "moonpalace" => 0, "koinonia" => 1, "cras" => 2, "oros" => 3, _ => -1 };

        public static List<Point> For(string city)
        {
            if (string.IsNullOrEmpty(city)) return new List<Point>();
            if (cache.TryGetValue(city, out var list)) return list;
            list = new List<Point>();
            try
            {
                var fs = DriftCityData.Files;
                string path = $@"{city}\chasespawnlist.text";
                if (fs != null && fs.Exists(path)) list = Parse(fs.Read(path));
                int ci = CityIndex(city);
                foreach (var p in list) if (Names().TryGetValue((ci, p.Id), out var n)) p.Name = n;
            }
            catch (Exception ex) { Debug.LogWarning("chasespawnlist: " + ex.Message); }
            cache[city] = list;
            return list;
        }

        static Dictionary<(int, int), string> Names()
        {
            if (names != null) return names;
            names = new Dictionary<(int, int), string>();
            try
            {
                var fs = DriftCityData.Files;
                if (fs == null || !fs.Exists(@"init\chasespawn.xlt")) return names;
                var t = Core.XltTable.Parse(fs.Read(@"init\chasespawn.xlt"), 4);
                foreach (var r in t.Rows)
                    if (r.Length > 3 && int.TryParse(r[1].Trim(), out int c) && int.TryParse(r[2].Trim(), out int id)) names[(c, id)] = r[3].Trim();
            }
            catch (Exception ex) { Debug.LogWarning("chasespawn.xlt: " + ex.Message); }
            return names;
        }

        static bool Vec(string line, out Vector3 v)
        {
            v = default;
            var parts = line.Split(new[] { "//" }, StringSplitOptions.None)[0].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return false;
            if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
                !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) ||
                !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)) return false;
            v = new Vector3(x, z, y);   // Gamebryo (x, y, z up) -> Unity
            return true;
        }

        static int Num(string line) => int.TryParse(line.Split(new[] { "//" }, StringSplitOptions.None)[0].Trim(), out int n) ? n : -1;

        public static List<Point> Parse(byte[] data)
        {
            var list = new List<Point>();
            var lines = new List<string>();
            foreach (var l in System.Text.Encoding.ASCII.GetString(data).Split('\n')) lines.Add(l.Trim('\r', ' '));
            // header: maker, origin, version, blank, the point count; the first point id is the line before a pivot vector
            int i = 0;
            while (i + 1 < lines.Count && !(Num(lines[i]) >= 0 && !Vec(lines[i], out _) && Vec(lines[i + 1], out _))) i++;
            int count = i > 0 ? Num(lines[i - 1]) : -1;
            if (count < 0) count = 1000;
            for (int p = 0; p < count && i + 3 < lines.Count; p++)
            {
                var pt = new Point { Id = Num(lines[i]) };
                if (!Vec(lines[i + 1], out pt.Pivot)) break;
                pt.Name = "Point " + pt.Id;
                int slots = Num(lines[i + 3]);
                i += 4;
                for (int s = 0; s < slots && i + 1 < lines.Count; s++, i += 2)
                    if (Vec(lines[i], out var v)) pt.Slots.Add(v);
                list.Add(pt);
            }
            return list;
        }

        /// <summary>A spawn point between <paramref name="min"/> and <paramref name="max"/> metres from <paramref name="from"/> (else any).</summary>
        public static Point Pick(string city, Vector3 from, float min = 250f, float max = 1200f)
        {
            var all = For(city);
            if (all.Count == 0) return null;
            var near = all.FindAll(p => { float d = MissionPosts.Flat(p.Pivot, from); return d >= min && d <= max; });
            var src = near.Count > 0 ? near : all;
            return src[UnityEngine.Random.Range(0, src.Count)];
        }
    }
}
