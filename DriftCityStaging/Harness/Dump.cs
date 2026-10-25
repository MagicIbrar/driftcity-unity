using System;
using System.Linq;
using DriftCity.Core;
using DriftCity.Core.Nif;

static class Dump
{
    public static void Run(GameFileSystem fs, string name, int maxDepth)
    {
        var nif = NifFile.Parse(fs.Read(name));
        Console.WriteLine($"== {name}  blocks={nif.Blocks.Length} err={nif.Error ?? "none"}");
        foreach (var root in nif.RootObjects()) Walk(nif, root, 0, maxDepth);
    }
    public static void RunFile(string path, int maxDepth)
    {
        var nif = NifFile.Parse(System.IO.File.ReadAllBytes(path));
        Console.WriteLine($"== {path}  blocks={nif.Blocks.Length} err={nif.Error ?? "none"}");
        foreach (var root in nif.RootObjects()) Walk(nif, root, 0, maxDepth);
    }
    static void Walk(NifFile nif, NiAVObject o, int depth, int maxDepth)
    {
        string pad = new string(' ', depth * 2);
        string extra = "";
        if (o is NiGeometry g && nif.Get<NiGeometryData>(g.Data) is NiGeometryData gd && gd.Vertices != null)
        {
            var xs = gd.Vertices; extra = $" verts={gd.NumVertices} uv={gd.UVSets.Length} col={(gd.Colors != null)} bbox=({xs.Min(v=>v.X):F2},{xs.Min(v=>v.Y):F2},{xs.Min(v=>v.Z):F2})..({xs.Max(v=>v.X):F2},{xs.Max(v=>v.Y):F2},{xs.Max(v=>v.Z):F2})";
            foreach (var p in g.Properties) if (nif.Blocks[p] is NiMaterialProperty mp) extra += $" mat='{mp.Name}' a={mp.Alpha:F2} d=({mp.Diffuse.R:F2},{mp.Diffuse.G:F2},{mp.Diffuse.B:F2})"; else if (nif.Blocks[p] is NiAlphaProperty ap) extra += $" alpha={(ap.Blend ? "blend" : "")}{(ap.Test ? "test" : "")}";
            foreach (var p in g.Properties) if (nif.Blocks[p] is NiTexturingProperty tp && tp.Base != null && nif.Get<NiSourceTexture>(tp.Base.Source) is NiSourceTexture st) extra += $" tex={st.FileName}{(tp.Dark!=null?" +dark":"")}{(tp.Glow!=null?" +glow":"")}";
        }
        if (o is NiGeometry sg && sg.ShaderName != null) extra += $" shader={sg.ShaderName}";
        foreach (var e in o.ExtraData) if (nif.Get<NiExtraData>(e) is NiExtraData x) extra += $" x:{x.GetType().Name.Replace("Ni", "").Replace("ExtraData", "")}'{x.Name}'" + (x is NiColorExtraData cx ? $"=({cx.Value.R:F2},{cx.Value.G:F2},{cx.Value.B:F2})" : x is NiStringExtraData sx ? $"={sx.Value}" : x is NiIntegerExtraData ix ? $"={ix.Value}" : x is NiFloatExtraData fx ? $"={fx.Value}" : "");
        Console.WriteLine($"{pad}{o.TypeName} '{o.Name}' t=({o.Translation.X:F1},{o.Translation.Y:F1},{o.Translation.Z:F1}) s={o.Scale:F2}{(o.Hidden?" HIDDEN":"")}{extra}");
        if (depth >= maxDepth) return;
        if (o is NiNode n) foreach (var c in n.Children) if (nif.Get<NiAVObject>(c) is NiAVObject child) Walk(nif, child, depth + 1, maxDepth);
    }
}
