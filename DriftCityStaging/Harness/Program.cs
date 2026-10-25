using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DriftCity.Core;
using DriftCity.Core.Nif;

static class Program
{
    static int Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var root = args.Length > 0 ? args[0] : @"D:\Drift_City_Remastered_Alpha_08-2025\Drift City Remastered Alpha";
        string filter = args.Length > 1 ? args[1] : "";
        int limit = args.Length > 2 ? int.Parse(args[2]) : int.MaxValue;
        if (filter == "dumpfile") { Dump.RunFile(args[3], limit); return 0; }
        if (filter == "kf") { KfCheck.Run(args[3], args[4]); return 0; }
        if (filter == "dialogs") { Dialogs.Run(root, args.Length > 3 ? args[3] : ""); return 0; }
        if (filter == "control") { ControlDump.Run(root, args[3]); return 0; }
        if (filter == "named") { Named.Run(root, args[3]); return 0; }
        if (filter == "big") { Big.Run(root, int.Parse(args[3])); return 0; }
        if (filter == "ids") { Ids.Run(root, args.Skip(3).ToArray()); return 0; }
        using var fs = new GameFileSystem(root) { };
        Cp949.LoadTable(System.IO.File.ReadAllBytes(@"D:\Drift_City_Remastered_Alpha_08-2025\DriftCityOffline\Assets\DriftCity\Resources\cp949.bytes"));
        if (filter == "ui") { UiCheck.GenerateCp949(@"D:\Drift_City_Remastered_Alpha_08-2025\DriftCityOffline\Assets\DriftCity\Resources\cp949.bytes"); UiCheck.Run(fs, root); return 0; }
        if (filter == "blend") { BlendCheck.Run(fs, args[3], args.Length > 4 ? int.Parse(args[4]) : 40); return 0; }
        if (filter == "roads") { RoadsCheck.Run(fs, args.Skip(3).ToArray()); return 0; }
        if (filter == "extras") { TrafficExtras.Run(fs, args.Skip(3).ToArray()); return 0; }
        if (filter == "traffic") { TrafficCheck.Run(fs, args.Skip(3).ToArray()); return 0; }
        if (filter == "city") { CityCheck.Run(fs, args.Skip(3).ToArray()); return 0; }
        if (filter == "shaders") { Shaders.Run(fs, args[3]); return 0; }
        if (filter == "blocks") { Blocks.Run(fs, args[3]); return 0; }
        if (filter == "ntx") { foreach (var n in NtxBundle.LoadFrom(fs).Names.Where(n => n.Contains(args[3])).OrderBy(n => n)) Console.WriteLine(n); return 0; }
        if (filter == "streams") { Streams.Run(fs, args[3]); return 0; }
        if (filter == "props") { Props.Run(fs, args[3], args[4]); return 0; }
        if (filter == "files") { foreach (var f in fs.AllFiles.Where(f => f.Contains(args[3])).OrderBy(f => f)) Console.WriteLine(f); return 0; }
        if (filter == "xlt") { var t = XltTable.Parse(fs.Read(args[3])); int max = args.Length > 4 ? int.Parse(args[4]) : 8; Console.WriteLine($"{t.Rows.Count} rows | " + string.Join(" | ", t.Columns)); foreach (var r in t.Rows.Take(max)) Console.WriteLine("  " + string.Join(" | ", r)); return 0; }
        if (filter == "extract") { var ntx = NtxBundle.LoadFrom(fs); foreach (var n in args.Skip(4)) { var data = ntx.Get(n) ?? (fs.Resolve(n) is string pth ? fs.Read(pth) : null); Console.WriteLine(n + (data == null ? " NOT FOUND" : " ok")); if (data != null) System.IO.File.WriteAllBytes(System.IO.Path.Combine(args[3], System.IO.Path.GetFileName(n.Replace(",", "_").Replace("~", "-"))), data); } return 0; }
        if (filter == "tex") { Tex.Run(fs, args.Skip(3).ToArray()); return 0; }
        if (filter == "dump") { foreach (var n in args.Skip(3)) Dump.Run(fs, n, limit); return 0; }
        Console.WriteLine($"files: {fs.AllFiles.Count}");
        var nifs = fs.AllFiles.Where(f => f.Contains(filter) && (f.EndsWith(".nif") || f.EndsWith(".dat"))).Take(limit).ToList();
        int ok = 0, notNif = 0; var fails = new Dictionary<string, List<string>>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var f in nifs)
        {
            byte[] d;
            try { d = fs.Read(f); } catch (Exception ex) { Console.WriteLine($"READ FAIL {f}: {ex.Message}"); continue; }
            if (!(d.Length > 20 && Encoding.ASCII.GetString(d, 0, 8) == "Gamebryo")) { notNif++; continue; }
            NifFile nif;
            try { nif = NifFile.Parse(d); } catch (Exception ex) { Add(fails, "HEADER " + ex.Message.Split(':')[0], f); continue; }
            if (nif.Error == null) ok++;
            else
            {
                string key = nif.Error.Split(' ')[0];
                if (nif.Error.StartsWith("unsupported")) key = nif.Error.Split(' ')[2];
                else if (nif.Error.StartsWith("footer")) key = "FOOTER after " + (nif.ParsedBlocks > 0 ? nif.Blocks[nif.ParsedBlocks - 1].TypeName : "?");
                Add(fails, key, f + " :: " + nif.Error);
            }
        }
        Console.WriteLine($"NIF ok {ok}/{nifs.Count - notNif}  (non-nif .dat: {notNif})  {sw.ElapsedMilliseconds} ms");
        foreach (var kv in fails.OrderByDescending(k => k.Value.Count))
        {
            Console.WriteLine($"  {kv.Value.Count,5}  {kv.Key}");
            foreach (var s in kv.Value.Take(2)) Console.WriteLine("           " + s);
        }
        if (filter == "" || filter.EndsWith(".dds"))
        {
            int dok = 0, dn = 0; var dfail = new Dictionary<string, int>();
            foreach (var f in fs.AllFiles.Where(x => x.EndsWith(".dds")).Take(Math.Min(limit, 3000)))
            {
                dn++;
                try { var img = DdsImage.Load(fs.Read(f)); dok++; }
                catch (Exception ex) { dfail[ex.Message] = dfail.GetValueOrDefault(ex.Message) + 1; }
            }
            Console.WriteLine($"DDS ok {dok}/{dn}");
            foreach (var kv in dfail) Console.WriteLine($"  {kv.Value} {kv.Key}");
        }
        return 0;
    }
    static void Add(Dictionary<string, List<string>> d, string k, string v) { if (!d.TryGetValue(k, out var l)) d[k] = l = new List<string>(); l.Add(v); }
}
