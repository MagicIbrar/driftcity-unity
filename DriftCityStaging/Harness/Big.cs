using System;
using System.IO;
using System.Linq;
using DriftCity.Core;
static class Big
{
    public static void Run(string root, int dialogId)
    {
        Cp949.LoadTable(File.ReadAllBytes(@"D:\Drift_City_Remastered_Alpha_08-2025\DriftCityOffline\Assets\DriftCity\Resources\cp949.bytes"));
        var doc = NuiDocument.Parse(File.ReadAllBytes(Path.Combine(root, "Data", "GameUI", "game.nui")));
        var d = doc.Get(dialogId);
        foreach (var c in d.Controls)
        {
            NuiControl.TryVec2(c.I("SZ1"), out float w, out float h);
            if (w >= 300 || h >= 300) Console.WriteLine($"{c.Id} id={c.IdName ?? "-"} type={c.Type} size={w}x{h} img={c.I("I1_1")} H={NuiControl.Str(c.Nation,"H")}");
        }
    }
}
