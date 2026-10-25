using System;
using System.Linq;
using DriftCity.Core;
using DriftCity.Core.Nif;
static class Blocks
{
    // block type histogram of one NIF
    public static void Run(GameFileSystem fs, string name)
    {
        var nif = NifFile.Parse(fs.Read(name));
        foreach (var g in nif.Blocks.Where(b => b != null).GroupBy(b => b.GetType().Name).OrderByDescending(g => g.Count())) Console.WriteLine($"{g.Count(),4} {g.Key}");
        Console.WriteLine($"parsed {nif.ParsedBlocks}/{nif.Blocks.Length} err={nif.Error}");
        foreach (var px in nif.Blocks.OfType<NiPixelData>()) Console.WriteLine($"  pixeldata #{px.Index} format={px.PixelFormat} bpp={px.BitsPerPixel} mips={px.Mips?.Length} size={(px.Mips?.Length > 0 ? px.Mips[0].w + "x" + px.Mips[0].h : "-")} bytes={px.Pixels?.Length}");
        foreach (var st in nif.Blocks.OfType<NiSourceTexture>()) Console.WriteLine($"  source #{st.Index} '{st.FileName}' external={st.External} pixel={st.PixelData}");
    }
}
