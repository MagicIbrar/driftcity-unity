using System;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Core;
using DriftCity.Core.Nif;

// VCAlphaTextureBlender meshes of a city's terrain: their two textures and vertex alpha statistics
static class BlendCheck
{
    public static void Run(GameFileSystem fs, string city, int maxCells)
    {
        var terr = CityTerrain.Parse(fs.Read($@"{city}\terrain0.lf"));
        var stats = new Dictionary<string, (int meshes, int verts, int noColor, double sum, int low)>();
        foreach (var cell in terr.Cells.Take(maxCells))
        {
            var nif = NifFile.Parse(CityFormats.Slice(terr.Data, cell.nif));
            foreach (var b in nif.Blocks.OfType<NiGeometry>())
            {
                if (b.ShaderName != "VCAlphaTextureBlender") continue;
                var tex = b.Properties.Select(p => nif.Get<NiTexturingProperty>(p)).FirstOrDefault(t => t != null);
                int t1 = 0, t2 = 1;
                foreach (var e in b.ExtraData)
                    if (nif.Get<NiIntegerExtraData>(e) is NiIntegerExtraData x)
                    { if (x.Name == "Texture1Index") t1 = (int)x.Value; else if (x.Name == "Texture2Index") t2 = (int)x.Value; }
                string Name(int i) => tex != null && i >= 0 && i < tex.ShaderTextures.Count && tex.ShaderTextures[i] != null ? nif.Get<NiSourceTexture>(tex.ShaderTextures[i].Source)?.FileName ?? "?" : "-";
                string baseName = tex?.Base != null ? nif.Get<NiSourceTexture>(tex.Base.Source)?.FileName ?? "?" : "-";
                string key = $"tex1[{t1}]={Name(t1)}  tex2[{t2}]={Name(t2)}  base={baseName}";
                var d = nif.Get<NiGeometryData>(b.Data);
                var s = stats.TryGetValue(key, out var v) ? v : (meshes: 0, verts: 0, noColor: 0, sum: 0.0, low: 0);
                s.meshes++;
                if (d?.Colors == null || d.Colors.Length == 0) s.noColor++;
                else { s.verts += d.Colors.Length; s.sum += d.Colors.Sum(c => c.A); s.low += d.Colors.Count(c => c.A < 0.5f); }
                stats[key] = s;
            }
        }
        // the other terrain meshes: base / dark / detail / decal layers, vertex colours and the shader name
        var other = new Dictionary<string, int>();
        foreach (var cell in terr.Cells.Take(maxCells))
        {
            var nif = NifFile.Parse(CityFormats.Slice(terr.Data, cell.nif));
            foreach (var b in nif.Blocks.OfType<NiGeometry>())
            {
                if (b.ShaderName == "VCAlphaTextureBlender") continue;
                var tex = b.Properties.Select(p => nif.Get<NiTexturingProperty>(p)).FirstOrDefault(t => t != null);
                string N(TexDesc t) => t == null ? "-" : nif.Get<NiSourceTexture>(t.Source)?.FileName ?? "?";
                var d = nif.Get<NiGeometryData>(b.Data);
                bool vc = d?.Colors != null && d.Colors.Length > 0;
                string vcs = vc ? $"vc(minRGB {d.Colors.Min(c => Math.Min(c.R, Math.Min(c.G, c.B))):F2} meanRGB {d.Colors.Average(c => (c.R + c.G + c.B) / 3):F2})" : "novc";
                var props = string.Join(",", b.Properties.Select(p => nif.Blocks[p]?.GetType().Name.Replace("Ni", "").Replace("Property", "")).Where(x => x != null));
                string key = $"shader={b.ShaderName ?? "-"} base={N(tex?.Base)} dark={N(tex?.Dark)} detail={N(tex?.Detail)} decal0={N(tex?.Decals[0])} apply={tex?.ApplyMode} {(vc ? "vc" : "novc")} [{props}]";
                other[key] = other.TryGetValue(key, out var n) ? n + 1 : 1;
                if (N(tex?.Base).Contains("road") && other[key] == 1) Console.WriteLine("  sample " + key + " " + vcs);
            }
        }
        foreach (var kv in other.OrderByDescending(k => k.Value).Take(25)) Console.WriteLine($"{kv.Value,5}  {kv.Key}");
        foreach (var kv in stats.OrderByDescending(k => k.Value.meshes))
            Console.WriteLine($"{kv.Value.meshes,4} meshes  noColor {kv.Value.noColor,3}  verts {kv.Value.verts,6}  meanA {(kv.Value.verts > 0 ? kv.Value.sum / kv.Value.verts : 0):F2}  A<0.5 {(kv.Value.verts > 0 ? 100.0 * kv.Value.low / kv.Value.verts : 0):F0}%  {kv.Key}");
    }
}
