using System;
using System.Linq;
using DriftCity.Core;
using DriftCity.Core.Nif;
static class CityCheck
{
    public static void Run(GameFileSystem fs, string[] cities)
    {
        foreach (var c in cities)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var terr = CityTerrain.Parse(fs.Read($@"{c}\terrain0.lf"));
            int tok = terr.Cells.Count(cell => { var n = NifFile.Parse(CityFormats.Slice(terr.Data, cell.nif)); return n.ParsedBlocks == n.Blocks.Length; });
            var lbf = fs.Read($@"{c}\blockobj0.lbf");
            var blocks = CityFormats.BlockNifs(lbf);
            int bok = 0; var names = new System.Collections.Generic.List<string>();
            foreach (var b in blocks) { var n = NifFile.Parse(CityFormats.Slice(lbf, b)); if (n.ParsedBlocks == n.Blocks.Length) bok++; var r = n.RootObjects().FirstOrDefault(); if (names.Count < 3 && r != null) names.Add(r.Name); }
            var lof = fs.Read($@"{c}\modeltable0.lof");
            var models = CityFormats.Models(lof);
            int mok = models.Count(m => m.Offset >= 0 && CityFormats.IsNif(lof, m.Offset) && NifFile.Parse(CityFormats.Slice(lof, m)) is var n && n.ParsedBlocks == n.Blocks.Length);
            var inst = CityFormats.Instances(fs.Read($@"{c}\main\object0.loi"), terr.Cells.Count);
            var listPath = fs.Exists($@"{c}\modeltable.nxt") ? $@"{c}\modeltable.nxt" : $@"{c}\{c}.xlt";
            var byId = CityFormats.ModelsById(lof, fs.Read(listPath));
            int resolvedAsId = inst.Count(i => byId.ContainsKey(i.Model)), resolvedAsIndex = inst.Count(i => byId.ContainsKey(i.Model + 1));
            int idParsed = byId.Values.Count(m => NifFile.Parse(CityFormats.Slice(lof, m)) is var nn && nn.ParsedBlocks == nn.Blocks.Length);
            int badModel = inst.Count - resolvedAsId;
            Console.WriteLine($"   models by id: {byId.Count} (parsed {idParsed}); instances resolved as ID {resolvedAsId}, as index+1 {resolvedAsIndex} of {inst.Count}");
            float maxX = inst.Count == 0 ? 0 : inst.Max(i => Math.Abs(i.Translation.X)), maxY = inst.Count == 0 ? 0 : inst.Max(i => Math.Abs(i.Translation.Y));
            Console.WriteLine($"{c}: grid {terr.Cols}x{terr.Rows} cell={terr.CellSize}  terrain {tok}/{terr.Cells.Count}  blocks {bok}/{blocks.Count} ({string.Join(",", names)})  models {mok}/{models.Count}  instances {inst.Count} badModelIdx={badModel} max|tx|={maxX:F0} max|ty|={maxY:F0}  {sw.ElapsedMilliseconds} ms");
        }
    }
}
