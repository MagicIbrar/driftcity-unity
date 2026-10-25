using System;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Core;
using DriftCity.Core.Nif;
static class Shaders
{
    // counts geometry shader names, extra data and material names across a city's terrain, blocks and models
    public static void Run(GameFileSystem fs, string city)
    {
        var count = new Dictionary<string, int>();
        void Add(string k) { count.TryGetValue(k, out int n); count[k] = n + 1; }
        void Scan(byte[] data, string kind)
        {
            var nif = NifFile.Parse(data);
            foreach (var b in nif.Blocks)
            {
                if (b is NiGeometry g)
                {
                    Add($"{kind} shader={g.ShaderName ?? "-"}");
                    foreach (var e in g.ExtraData) if (nif.Get<NiExtraData>(e) is NiExtraData x) Add($"{kind} extra {x.GetType().Name}:{x.Name}" + (x is NiIntegerExtraData ix ? "=" + ix.Value : x is NiFloatExtraData fx ? "=" + fx.Value : ""));
                    foreach (var p in g.Properties) if (nif.Blocks[p] is NiProperty pr) Add($"{kind} prop {pr.GetType().Name}");
                    if (g.ShaderName == "VCAlphaTextureBlender")
                        foreach (var p in g.Properties) if (nif.Blocks[p] is NiTexturingProperty tp)
                            for (int i = 0; i < tp.ShaderTextures.Count; i++) if (tp.ShaderTextures[i] is TexDesc d) Add($"{kind} vcab shadertex{i} uv{d.UVSet} tile=({d.Tiling.X},{d.Tiling.Y})");
                }
            }
        }
        var terr = CityTerrain.Parse(fs.Read($@"{city}\terrain0.lf"));
        foreach (var cell in terr.Cells) Scan(CityFormats.Slice(terr.Data, cell.nif), "terrain");
        var lbf = fs.Read($@"{city}\blockobj0.lbf");
        foreach (var b in CityFormats.BlockNifs(lbf)) Scan(CityFormats.Slice(lbf, b), "block");
        var lof = fs.Read($@"{city}\modeltable0.lof");
        foreach (var m in CityFormats.Models(lof)) if (m.Offset >= 0 && CityFormats.IsNif(lof, m.Offset)) Scan(CityFormats.Slice(lof, m), "model");
        foreach (var kv in count.OrderBy(k => k.Key)) Console.WriteLine($"{kv.Value,7} {kv.Key}");
    }
}
