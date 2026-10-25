using System;
using System.Linq;
using DriftCity.Core;
using DriftCity.Core.Nif;
static class Props
{
    public static void Run(GameFileSystem fs, string file, string meshName)
    {
        var nif = NifFile.Parse(fs.Read(file));
        foreach (var g in nif.Blocks.OfType<NiGeometry>().Where(b => b.Name == meshName).Take(1))
        {
            var gd = nif.Get<NiGeometryData>(g.Data);
            Console.WriteLine($"{g.TypeName} '{g.Name}' shader='{g.ShaderName}' verts={gd?.NumVertices} uvsets={gd?.UVSets.Length} colors={(gd?.Colors != null)}");
            if (gd?.Colors != null) Console.WriteLine("  first colors: " + string.Join(" ", gd.Colors.Take(4).Select(c => $"({c.R:F2},{c.G:F2},{c.B:F2},{c.A:F2})")));
            foreach (var e in g.ExtraData) if (nif.Blocks[e] is NiExtraData x) Console.WriteLine($"  extra {x.TypeName} '{x.Name}' {(x as NiStringExtraData)?.Value}{(x as NiIntegerExtraData)?.Value}");
            foreach (var p in g.Properties)
            {
                var b = nif.Blocks[p];
                Console.Write($"  {b.TypeName}");
                if (b is NiTexturingProperty t)
                {
                    string D(string n, TexDesc d) => d == null ? "" : $" {n}={nif.Get<NiSourceTexture>(d.Source)?.FileName}(uv{d.UVSet})";
                    Console.Write($" apply={t.ApplyMode}{D("base",t.Base)}{D("dark",t.Dark)}{D("detail",t.Detail)}{D("gloss",t.Gloss)}{D("glow",t.Glow)}{D("bump",t.Bump)}");
                    for (int i = 0; i < 4; i++) Console.Write(D("decal"+i, t.Decals[i]));
                }
                if (b is NiMaterialProperty m) Console.Write($" diff=({m.Diffuse.R:F2},{m.Diffuse.G:F2},{m.Diffuse.B:F2}) emis=({m.Emissive.R:F2},{m.Emissive.G:F2},{m.Emissive.B:F2}) a={m.Alpha}");
                if (b is NiVertexColorProperty v) Console.Write($" vmode={v.VertexMode} light={v.LightingMode}");
                if (b is NiAlphaProperty a) Console.Write($" flags=0x{a.Flags:x} thr={a.Threshold}");
                Console.WriteLine();
            }
        }
    }
}
