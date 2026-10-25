using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DriftCity.Core;
static class UiCheck
{
    public static void GenerateCp949(string outPath)
    {
        var enc = Encoding.GetEncoding(949);
        using var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        int n = 0;
        for (int lead = 0x81; lead <= 0xFE; lead++)
            for (int trail = 0x41; trail <= 0xFE; trail++)
            {
                var s = enc.GetString(new[] { (byte)lead, (byte)trail });
                if (s.Length == 1 && s[0] != '?' && s[0] != '\uFFFD') { bw.Write((ushort)((lead << 8) | trail)); bw.Write((ushort)s[0]); n++; }
            }
        Directory.CreateDirectory(Path.GetDirectoryName(outPath));
        File.WriteAllBytes(outPath, ms.ToArray());
        Console.WriteLine($"cp949 table: {n} entries -> {outPath}");
        Cp949.LoadTable(ms.ToArray());
    }
    public static void Run(GameFileSystem fs, string root)
    {
        var nuiBytes = File.ReadAllBytes(Path.Combine(root, "Data", "GameUI", "game.nui"));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var doc = NuiDocument.Parse(nuiBytes);
        int controls = doc.Dialogs.Sum(d => d.Controls.Count);
        Console.WriteLine($"NUI: {doc.Dialogs.Count} dialogs, {controls} controls in {sw.ElapsedMilliseconds} ms");
        var login = doc.FindByControl("ID_LOGIN_ENTER");
        Console.WriteLine($"login dialog id={login?.Id} key={login?.Key} text sample='{login?.Find("ID_LOGIN_STATIC_TITLE")?.I("T1")}'");
        var ntx = NtxBundle.LoadFrom(fs);
        var refs = new HashSet<string>();
        foreach (var d in doc.Dialogs) foreach (var c in d.Controls) foreach (var kv in c.Info) if (kv.Key.StartsWith("I") && kv.Value.EndsWith(".dds")) refs.Add(kv.Value);
        int found = refs.Count(r => ntx.Contains(r));
        Console.WriteLine($"NTX: {ntx.Count} images; NUI references {refs.Count} distinct, {found} found");
        foreach (var r in refs.Where(r => !ntx.Contains(r)).Take(5)) Console.WriteLine("   missing " + r);
        int ok = 0, bad = 0;
        foreach (var f in fs.AllFiles.Where(f => f.EndsWith(".xlt")))
        {
            try { var t = XltTable.Parse(fs.Read(f)); if (t.Columns.Length > 0) ok++; else bad++; }
            catch { bad++; }
        }
        Console.WriteLine($"XLT tables parsed: {ok} ok, {bad} without header");
        var vl = XltTable.Parse(fs.Read(@"init\vehiclelist.xlt"));
        Console.WriteLine($"vehiclelist: {vl.Rows.Count} rows, cols: {string.Join(" | ", vl.Columns.Take(17))}");
    }
}
