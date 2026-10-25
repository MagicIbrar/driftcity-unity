using System;
using System.IO;
using System.Linq;
using DriftCity.Core;
static class Named
{
    // controls whose NM (name) or image matches the filter: dialog, id name, NM, first image
    public static void Run(string root, string filter)
    {
        var doc = NuiDocument.Parse(File.ReadAllBytes(Path.Combine(root, "Data", "GameUI", "game.nui")));
        foreach (var d in doc.Dialogs)
            foreach (var c in d.Controls)
            {
                string nm = c.Name ?? "", img = c.I("I1_1") ?? "";
                if (nm.Contains(filter) || img.Contains(filter)) Console.WriteLine($"{d.Id}\t{c.IdName}\t{nm}\t{img}\t{c.Id}");
            }
    }
}
