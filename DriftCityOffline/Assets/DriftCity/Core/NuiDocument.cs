// Parser for Drift City's UI layout file (Data\GameUI\game.nui): EUC-KR/CP949 XML of
//   <document><FILEINFO/><DIALOGLIST><DIALOG C=id E=anchor G="x y" ...>
//     <CONTROL B=id E=ControlIdName D=group><INFO TYPE=.. NM=.. SZ1=.. I1_1=.. UV1_1=.. T1=.. /><NATION C=0 F=anchor H="x y" .../></CONTROL>
// Layout semantics (inferred): coordinates are on a 1024x768 virtual screen, y down.
// Anchor flags: 1 left, 2 center, 4 right | 8 top, 16 middle, 32 bottom.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace DriftCity.Core
{
    public sealed class NuiControl
    {
        public int Id;              // B
        public string IdName;       // E, e.g. ID_LOGIN_ENTER (null for decoration)
        public string Group;        // D
        public Dictionary<string, string> Info = new Dictionary<string, string>();
        public Dictionary<string, string> Nation = new Dictionary<string, string>();
        public int Type => Int(Info, "TYPE");
        public string Name => Str(Info, "NM");

        public string I(string key) => Str(Info, key);
        public static string Str(Dictionary<string, string> d, string k) => d.TryGetValue(k, out var v) ? v : null;
        public static int Int(Dictionary<string, string> d, string k, int fallback = 0) =>
            d.TryGetValue(k, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : fallback;

        public static bool TryVec2(string v, out float x, out float y)
        {
            x = y = 0;
            if (string.IsNullOrEmpty(v)) return false;
            var p = v.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            return p.Length >= 2 && float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                                 && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y);
        }

        public static float[] Floats(string v)
        {
            if (string.IsNullOrEmpty(v)) return new float[0];
            var p = v.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var r = new float[p.Length];
            for (int i = 0; i < p.Length; i++) float.TryParse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture, out r[i]);
            return r;
        }

        /// <summary>"AARRGGBB" or "RRGGBB" hex.</summary>
        public static bool TryColor(string v, out byte r, out byte g, out byte b, out byte a)
        {
            r = g = b = a = 255;
            if (string.IsNullOrEmpty(v) || !uint.TryParse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint c)) return false;
            if (v.Length > 6) a = (byte)(c >> 24);
            r = (byte)(c >> 16); g = (byte)(c >> 8); b = (byte)c;
            return true;
        }
    }

    public sealed class NuiDialog
    {
        public int Id;              // C
        public Dictionary<string, string> Attr = new Dictionary<string, string>();
        public List<NuiControl> Controls = new List<NuiControl>();
        public int Anchor => NuiControl.Int(Attr, "E", 0);

        /// <summary>Most common control-id prefix, e.g. "LOGIN" for ID_LOGIN_*; used as a readable name.</summary>
        public string Key
        {
            get
            {
                var counts = new Dictionary<string, int>();
                string best = null; int bestCount = 0;
                foreach (var c in Controls)
                {
                    if (string.IsNullOrEmpty(c.IdName) || !c.IdName.StartsWith("ID_")) continue;
                    var parts = c.IdName.Split('_');
                    if (parts.Length < 2) continue;
                    counts.TryGetValue(parts[1], out int n);
                    counts[parts[1]] = ++n;
                    if (n > bestCount) { bestCount = n; best = parts[1]; }
                }
                return best ?? ("DIALOG" + Id);
            }
        }

        public NuiControl Find(string idName)
        {
            foreach (var c in Controls) if (c.IdName == idName) return c;
            return null;
        }
    }

    public sealed class NuiDocument
    {
        public List<NuiDialog> Dialogs = new List<NuiDialog>();
        readonly Dictionary<int, NuiDialog> byId = new Dictionary<int, NuiDialog>();

        public NuiDialog Get(int id) => byId.TryGetValue(id, out var d) ? d : null;

        /// <summary>Finds the dialog that owns a control id name (e.g. "ID_LOGIN_ENTER").</summary>
        public NuiDialog FindByControl(string idName)
        {
            foreach (var d in Dialogs) if (d.Find(idName) != null) return d;
            return null;
        }

        public static NuiDocument Parse(byte[] data)
        {
            var doc = new NuiDocument();
            int p = 0;
            NuiDialog dialog = null;
            NuiControl control = null;
            while (true)
            {
                int lt = Array.IndexOf(data, (byte)'<', p);
                if (lt < 0 || lt + 1 >= data.Length) break;
                if (data[lt + 1] == '?' || data[lt + 1] == '!') { p = SkipTag(data, lt); continue; }
                bool closing = data[lt + 1] == '/';
                int nameStart = lt + (closing ? 2 : 1);
                int q = nameStart;
                while (q < data.Length && IsNameChar(data[q])) q++;
                string tag = Latin1.Decode(data, nameStart, q - nameStart);
                if (closing)
                {
                    if (tag == "CONTROL") control = null;
                    else if (tag == "DIALOG") dialog = null;
                    p = SkipTag(data, lt);
                    continue;
                }
                var attrs = ParseAttributes(data, q, out int end, out bool selfClosing);
                p = end;
                switch (tag)
                {
                    case "DIALOG":
                        dialog = new NuiDialog { Attr = attrs };
                        dialog.Id = NuiControl.Int(attrs, "C");
                        doc.Dialogs.Add(dialog);
                        doc.byId[dialog.Id] = dialog;
                        if (selfClosing) dialog = null;
                        break;
                    case "CONTROL":
                        control = new NuiControl
                        {
                            Id = NuiControl.Int(attrs, "B"),
                            IdName = NuiControl.Str(attrs, "E"),
                            Group = NuiControl.Str(attrs, "D"),
                        };
                        dialog?.Controls.Add(control);
                        if (selfClosing) control = null;
                        break;
                    case "INFO":
                        if (control != null) control.Info = attrs;
                        break;
                    case "NATION":
                        if (control != null) foreach (var kv in attrs) control.Nation[kv.Key] = kv.Value;
                        break;
                }
            }
            return doc;
        }

        static bool IsNameChar(byte b) => b == '_' || b == '-' || b == ':' || (b >= '0' && b <= '9') || (b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z');

        static int SkipTag(byte[] d, int p)
        {
            int gt = Array.IndexOf(d, (byte)'>', p);
            return gt < 0 ? d.Length : gt + 1;
        }

        static Dictionary<string, string> ParseAttributes(byte[] d, int p, out int end, out bool selfClosing)
        {
            var attrs = new Dictionary<string, string>();
            selfClosing = false;
            while (p < d.Length)
            {
                byte b = d[p];
                if (b == '>') { end = p + 1; return attrs; }
                if (b == '/' && p + 1 < d.Length && d[p + 1] == '>') { selfClosing = true; end = p + 2; return attrs; }
                if (!IsNameChar(b)) { p++; continue; }
                int ns = p;
                while (p < d.Length && IsNameChar(d[p])) p++;
                string name = Latin1.Decode(d, ns, p - ns);
                while (p < d.Length && d[p] != '=' && d[p] != '>' && d[p] != '/') p++;
                if (p >= d.Length || d[p] != '=') continue;
                p++;
                while (p < d.Length && d[p] != '"' && d[p] != '\'') p++;
                if (p >= d.Length) break;
                byte quote = d[p++];
                int vs = p;
                while (p < d.Length && d[p] != quote) p++;
                attrs[name] = Unescape(Cp949.Decode(d, vs, p - vs));
                p++;
            }
            end = d.Length;
            return attrs;
        }

        static string Unescape(string s) =>
            s.IndexOf('&') < 0 ? s : s.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&#10;", "\n").Replace("&#13;", "").Replace("&amp;", "&");
    }
}
