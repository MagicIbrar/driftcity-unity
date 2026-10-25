// Text formats used by gameplay:
//  - aipath.nac (CP949 text): nested { } blocks; each POS block lists numeric lines, the 6th holding
//    "x y z dist dirx diry dirz" (the racing line point).
//  - quest/NPC scripts in motherquest.xlt: <pimg portrait> <name Speaker> <text line> <br> <next> markup.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using DriftCity.Core.Nif;

namespace DriftCity.Core
{
    public static class RacePath
    {
        public static List<Vec3> ParseNac(string text)
        {
            var points = new List<Vec3>();
            var lines = text.Split('\n');
            bool inPos = false;
            int depth = 0, posDepth = 0, numeric = 0;
            foreach (var raw in lines)
            {
                var line = raw;
                int c = line.IndexOf("//", StringComparison.Ordinal);
                if (c >= 0) line = line.Substring(0, c);
                line = line.Trim();
                if (line.Length == 0) continue;
                if (line == "POS") { inPos = true; numeric = 0; continue; }
                if (line == "{") { depth++; if (inPos && posDepth == 0) posDepth = depth; continue; }
                if (line == "}") { if (inPos && depth == posDepth) { inPos = false; posDepth = 0; } depth--; continue; }
                if (!inPos) continue;
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0 || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _)) continue;
                numeric++;
                if (numeric == 6 && parts.Length >= 3 &&
                    float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                    float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                    float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                    points.Add(new Vec3(x, y, z));
            }
            return points;
        }
    }

    public sealed class ScriptPage
    {
        public string Portrait, Speaker;
        public List<string> Lines = new List<string>();
    }

    public static class NpcScript
    {
        static readonly Regex Tag = new Regex(@"<(\w+)\s*([^>]*)>", RegexOptions.Compiled);

        /// <summary>Splits a script into pages at &lt;next&gt;, collecting speaker, portrait and text lines.</summary>
        public static List<ScriptPage> Parse(string script)
        {
            var pages = new List<ScriptPage>();
            var page = new ScriptPage();
            string portrait = null, speaker = null;
            foreach (Match m in Tag.Matches(script ?? ""))
            {
                string tag = m.Groups[1].Value.ToLowerInvariant(), arg = m.Groups[2].Value.Trim();
                switch (tag)
                {
                    case "pimg": portrait = arg; page.Portrait = arg; break;
                    case "name": speaker = arg; page.Speaker = arg; break;
                    case "text": page.Lines.Add(arg); break;
                    case "next":
                        if (page.Lines.Count > 0) pages.Add(page);
                        page = new ScriptPage { Portrait = portrait, Speaker = speaker };
                        break;
                }
            }
            if (page.Lines.Count > 0) pages.Add(page);
            foreach (var p in pages) { p.Portrait = p.Portrait ?? portrait; p.Speaker = p.Speaker ?? speaker; }
            return pages;
        }
    }
}
