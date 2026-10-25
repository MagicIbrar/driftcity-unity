using System;
using System.IO;
using System.Linq;
using DriftCity.Core;
static class ControlDump
{
    // every attribute of the controls whose id name contains the filter
    public static void Run(string root, string filter)
    {
        var doc = NuiDocument.Parse(File.ReadAllBytes(Path.Combine(root, "Data", "GameUI", "game.nui")));
        foreach (var d in doc.Dialogs)
            foreach (var c in d.Controls.Where(c => (c.IdName ?? "").Contains(filter)))
                Console.WriteLine($"{d.Id} {c.IdName}: " + string.Join(" | ", c.Info.Select(kv => $"{kv.Key}={(kv.Value.Length > 300 ? kv.Value.Substring(0, 300) + "..." : kv.Value)}")) + " || NATION " + string.Join(" ", c.Nation.Select(kv => $"{kv.Key}={kv.Value}")));
    }
}
