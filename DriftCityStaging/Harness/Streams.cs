using System;
using System.Linq;
using DriftCity.Core;
using DriftCity.Core.Nif;
static class Streams
{
    // lane0.llf / guardrail0.lgf: cells with content, how many parse, and a sample cell's tree
    public static void Run(GameFileSystem fs, string city)
    {
        foreach (var f in new[] { "lane0.llf", "guardrail0.lgf" })
        {
            var path = $@"{city}\{f}";
            if (!fs.Exists(path)) { Console.WriteLine($"{path}: missing"); continue; }
            var d = fs.Read(path);
            var cells = CityFormats.CellStream(d);
            int ok = 0, tris = 0; string sample = null;
            foreach (var c in cells)
            {
                var nif = NifFile.Parse(CityFormats.Slice(d, c));
                if (nif.ParsedBlocks == nif.Blocks.Length) ok++;
                tris += nif.Blocks.OfType<NiTriShape>().Count() + nif.Blocks.OfType<NiTriStrips>().Count();
                if (sample == null) sample = string.Join(" ", nif.Blocks.OfType<NiSourceTexture>().Select(s => s.FileName).Distinct()) + " | " + string.Join(" ", nif.Blocks.Where(b => b != null).Select(b => b.GetType().Name).Distinct());
            }
            Console.WriteLine($"{path}: {cells.Count} cells with content, {ok} parse, {tris} shapes; sample: {sample}");
        }
    }
}
