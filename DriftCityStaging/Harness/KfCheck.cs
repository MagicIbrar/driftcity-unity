using System;
using System.Linq;
using DriftCity.Core;
using DriftCity.Core.Nif;

/// <summary>Harness mode "kf": samples a character .kf at its start and prints it next to the rest pose of a part file.</summary>
static class KfCheck
{
    public static int args_n = 12;
    public static void Run(string kfPath, string nifPath)
    {
        var kf = NifFile.Parse(System.IO.File.ReadAllBytes(kfPath));
        var nif = NifFile.Parse(System.IO.File.ReadAllBytes(nifPath));
        var seq = kf.Blocks.OfType<NiControllerSequence>().First();
        Console.WriteLine($"seq {seq.Name} {seq.StartTime}..{seq.StopTime} freq {seq.Frequency} cycle {seq.CycleType} accum {seq.AccumRootName} blocks {seq.Blocks.Length}");
        var buf = new float[4];
        foreach (var b in seq.Blocks.Take(args_n))
        {
            var pal = kf.Get<NiStringPalette>(b.Palette >= 0 ? b.Palette : seq.StringPalette);
            string name = pal?.At(b.NodeOffset);
            var node = nif.Blocks.OfType<NiAVObject>().FirstOrDefault(o => o.Name == name);
            var ip = kf.Get<NiObject>(b.Interpolator);
            string s = $"{name,-22} {ip?.GetType().Name}";
            if (node != null) s += $"\n    rest T=({node.Translation.X:F3},{node.Translation.Y:F3},{node.Translation.Z:F3}) R=[{node.Rotation.M00:F2} {node.Rotation.M01:F2} {node.Rotation.M02:F2} / {node.Rotation.M10:F2} {node.Rotation.M11:F2} {node.Rotation.M12:F2} / {node.Rotation.M20:F2} {node.Rotation.M21:F2} {node.Rotation.M22:F2}]";
            if (ip is NiBSplineCompTransformInterpolator c)
            {
                var sd = kf.Get<NiBSplineData>(c.SplineData); var bd = kf.Get<NiBSplineBasisData>(c.BasisData);
                s += $"\n    ip {c.StartTime}..{c.StopTime} cp {bd?.ControlPoints} handles T{c.TranslationHandle} R{c.RotationHandle} S{c.ScaleHandle} const T=({c.Translation.X:F3},{c.Translation.Y:F3},{c.Translation.Z:F3}) Q=({c.Rotation.W:F3},{c.Rotation.X:F3},{c.Rotation.Y:F3},{c.Rotation.Z:F3}) offs T {c.TranslationOffset:F3}/{c.TranslationHalfRange:F3} R {c.RotationOffset:F3}/{c.RotationHalfRange:F3} compact {sd?.Compact.Length} floats {sd?.Floats.Length}";
                if (NiBSplineTransformInterpolator.Valid(c.RotationHandle)) { BSpline.Sample(sd.Compact, c.RotationHandle, bd.ControlPoints, 4, c.RotationOffset, c.RotationHalfRange, 0f, buf); s += $"\n    Q(0)=({buf[0]:F3},{buf[1]:F3},{buf[2]:F3},{buf[3]:F3})"; Mat(buf, ref s); }
                if (NiBSplineTransformInterpolator.Valid(c.TranslationHandle)) { BSpline.Sample(sd.Compact, c.TranslationHandle, bd.ControlPoints, 3, c.TranslationOffset, c.TranslationHalfRange, 0f, buf); s += $"\n    T(0)=({buf[0]:F3},{buf[1]:F3},{buf[2]:F3})"; }
            }
            if (ip is NiTransformInterpolator ti)
            {
                s += $"\n    const T=({ti.Translation.X:F3},{ti.Translation.Y:F3},{ti.Translation.Z:F3}) Q=({ti.Rotation.W:F3},{ti.Rotation.X:F3},{ti.Rotation.Y:F3},{ti.Rotation.Z:F3})";
                var q0 = new[] { ti.Rotation.W, ti.Rotation.X, ti.Rotation.Y, ti.Rotation.Z }; if (ti.Rotation.W > -1e30f) Mat(q0, ref s);
                var td = kf.Get<NiTransformData>(ti.Data);
                if (td != null) { s += $"\n    keys R{td.Rotations.Count} euler {(td.Euler != null)} T{td.Translations?.Count}"; if (td.Rotations.Count > 0) { var q = td.Rotations.Values[0]; Mat(new[] { q.W, q.X, q.Y, q.Z }, ref s); } if (td.Translations?.Count > 0) { var tt = td.Translations.Values[0]; var tl = td.Translations.Values[td.Translations.Count - 1]; s += $" T0=({tt.X:F3},{tt.Y:F3},{tt.Z:F3}) Tn=({tl.X:F3},{tl.Y:F3},{tl.Z:F3})"; } }
            }
            Console.WriteLine(s);
        }
    }
    static void Mat(float[] q, ref string s)
    {
        float w = q[0], x = q[1], y = q[2], z = q[3];
        float l = (float)Math.Sqrt(w*w+x*x+y*y+z*z); w/=l;x/=l;y/=l;z/=l;
        s += $"  M=[{1-2*(y*y+z*z):F2} {2*(x*y+w*z):F2} {2*(x*z-w*y):F2} / {2*(x*y-w*z):F2} {1-2*(x*x+z*z):F2} {2*(y*z+w*x):F2} / {2*(x*z+w*y):F2} {2*(y*z-w*x):F2} {1-2*(x*x+y*y):F2}] |q|={l:F3}";
    }
}
