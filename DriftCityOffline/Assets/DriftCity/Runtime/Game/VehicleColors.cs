using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// The client's colour tables (effects\Vehicle*Color.nxt, read by 0x4b4b8b..0x4b4cc1): ColorID, name, "r:g:b[:a]".
    /// Visual-shop items name their colour by number: paint i_d_c_000NN and glossy paint i_g_c_000NN use paint
    /// colour NN, neon i_d_n_000NN neon colour NN, window tint i_d_w_000NN window colour NN (with its alpha);
    /// a car's four factory colours are the paint ids in vehiclelist's Default_Color columns.
    /// </summary>
    public static class VehicleColors
    {
        public enum Table { Paint, Neon, Window, Booster, Decal }

        static readonly Dictionary<Table, Dictionary<int, Color>> tables = new Dictionary<Table, Dictionary<int, Color>>();

        static Dictionary<int, Color> Get(Table table)
        {
            if (tables.TryGetValue(table, out var t)) return t;
            tables[table] = t = new Dictionary<int, Color>();
            string file = table switch
            {
                Table.Neon => "vehicleneoncolor", Table.Window => "vehiclewindowcolor", Table.Booster => "vehicleboostercolor",
                Table.Decal => "vehicledecalcolor", _ => "vehiclecolor",
            };
            var fs = DriftCityData.Files;
            string path = $@"effects\{file}.nxt";
            if (fs == null || !fs.Exists(path)) return t;
            var data = fs.Read(path);
            string text = data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE ? System.Text.Encoding.Unicode.GetString(data, 2, data.Length - 2) : Core.Cp949.Decode(data);
            foreach (var line in text.Split('\n'))
            {
                var c = line.TrimEnd('\r').Split('\t');
                if (c.Length < 3 || !int.TryParse(c[0], out int id)) continue;
                var v = c[2].Split(':');
                float F(int i, float fallback) => i < v.Length && float.TryParse(v[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f / 255f : fallback;
                t[id] = new Color(F(0, 1), F(1, 1), F(2, 1), F(3, 1));
            }
            return t;
        }

        public static bool TryGet(Table table, int id, out Color color) => Get(table).TryGetValue(id, out color);

        public static Color Paint(int id) => TryGet(Table.Paint, id, out var c) ? c : Color.white;

        /// <summary>The colour a visual-shop item names (paint, glossy paint, neon, tint), if it is a coloured item.</summary>
        public static bool ForItem(string itemName, out Color color)
        {
            color = Color.white;
            var m = Regex.Match(itemName ?? "", @"^i_([dg])_([cnw])_0*(\d+)$", RegexOptions.IgnoreCase);
            if (!m.Success) return false;
            var table = char.ToLowerInvariant(m.Groups[2].Value[0]) switch { 'n' => Table.Neon, 'w' => Table.Window, _ => Table.Paint };
            return TryGet(table, int.Parse(m.Groups[3].Value), out color);
        }
    }
}
