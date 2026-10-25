// Reader for Drift City ".xlt" tables: UTF-16 tab-separated text exported from Excel.
//   row 0: "Preset" header, row 1: "Value" metadata, row 2: column names, rows 3+: data.
// Quoted cells may contain tabs/newlines ("" escapes a quote), as in Excel's TSV export.
using System;
using System.Collections.Generic;
using System.Text;

namespace DriftCity.Core
{
    public sealed class XltTable
    {
        public string[] Columns = new string[0];
        public string[] Meta = new string[0];
        public List<string[]> Rows = new List<string[]>();
        readonly Dictionary<string, int> columnIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public static XltTable Parse(byte[] data, int headerRow = 2)
        {
            string text;
            if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) text = Encoding.Unicode.GetString(data, 2, data.Length - 2);
            else if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF) text = Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
            else text = Encoding.UTF8.GetString(data);
            var records = Split(text);
            var t = new XltTable();
            if (records.Count > 1) t.Meta = records[1];
            if (records.Count > headerRow)
            {
                t.Columns = records[headerRow];
                for (int i = 0; i < t.Columns.Length; i++)
                {
                    var name = t.Columns[i].Trim();
                    if (name.Length > 0 && !t.columnIndex.ContainsKey(name)) t.columnIndex[name] = i;
                }
                for (int r = headerRow + 1; r < records.Count; r++)
                    if (records[r].Length > 1 || (records[r].Length == 1 && records[r][0].Length > 0)) t.Rows.Add(records[r]);
            }
            return t;
        }

        static List<string[]> Split(string text)
        {
            var records = new List<string[]>();
            var cells = new List<string>();
            var cell = new StringBuilder();
            bool quoted = false, atCellStart = true;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                        else quoted = false;
                    }
                    else cell.Append(c);
                    continue;
                }
                if (c == '"' && atCellStart) { quoted = true; atCellStart = false; continue; }
                if (c == '\t') { cells.Add(cell.ToString()); cell.Clear(); atCellStart = true; continue; }
                if (c == '\r') continue;
                if (c == '\n')
                {
                    cells.Add(cell.ToString()); cell.Clear(); atCellStart = true;
                    records.Add(cells.ToArray()); cells.Clear();
                    continue;
                }
                cell.Append(c); atCellStart = false;
            }
            if (cell.Length > 0 || cells.Count > 0) { cells.Add(cell.ToString()); records.Add(cells.ToArray()); }
            return records;
        }

        public int Col(string name) => columnIndex.TryGetValue(name, out var i) ? i : -1;
        public bool Has(string name) => columnIndex.ContainsKey(name);

        public string Get(string[] row, string column, string fallback = "")
        {
            int i = Col(column);
            return i >= 0 && i < row.Length ? row[i] : fallback;
        }

        public int GetInt(string[] row, string column, int fallback = 0) =>
            int.TryParse(Get(row, column).Trim(), out var v) ? v : fallback;

        public float GetFloat(string[] row, string column, float fallback = 0f) =>
            float.TryParse(Get(row, column).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }
}
