using System;
using System.IO;
using System.Linq;
using DriftCity.Core;
static class Ids
{
    public static void Run(string root, string[] keys)
    {
        Cp949.LoadTable(File.ReadAllBytes(@"D:\Drift_City_Remastered_Alpha_08-2025\DriftCityOffline\Assets\DriftCity\Resources\cp949.bytes"));
        var doc = NuiDocument.Parse(File.ReadAllBytes(Path.Combine(root, "Data", "GameUI", "game.nui")));
        if (keys.Length > 0 && keys[0] == "full")
        {
            foreach (var k in keys.Skip(1))
                foreach (var d in doc.Dialogs.Where(x => x.Key == k || x.Id.ToString() == k))
                {
                    Console.WriteLine($"[{d.Key} #{d.Id}] " + string.Join(" ", d.Attr.Select(a => a.Key + "=" + a.Value)));
                    foreach (var c in d.Controls)
                        Console.WriteLine($"  {c.Id} {c.IdName ?? "-"} grp={c.Group} " + string.Join(" ", c.Info.Where(a => !a.Key.StartsWith("UV")).Select(a => a.Key + "=" + a.Value)) + " || " + string.Join(" ", c.Nation.Select(a => a.Key + "=" + a.Value)));
                }
            return;
        }
        if (keys.Length > 0 && keys[0] == "list")
        {
            foreach (var d in doc.Dialogs) Console.WriteLine($"{d.Id}	{d.Key}	{d.Controls.Count}");
            return;
        }
        foreach (var k in keys)
            foreach (var d in doc.Dialogs.Where(x => x.Key == k))
            {
                var ids = d.Controls.Where(c => c.IdName != null).Select(c => $"{c.IdName}:{c.Type}").ToList();
                Console.WriteLine($"[{k} #{d.Id}] {string.Join(" ", ids)}");
            }
    }
}
