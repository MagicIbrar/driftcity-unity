using System;
using System.IO;
using System.Linq;
using DriftCity.Core;
static class Dialogs
{
    // every dialog in game.nui: id, readable key, control count, and its first few control names
    public static void Run(string root, string filter)
    {
        var doc = NuiDocument.Parse(File.ReadAllBytes(Path.Combine(root, "Data", "GameUI", "game.nui")));
        foreach (var d in doc.Dialogs)
        {
            if (filter.Length > 0 && !(d.Key ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase) && !d.Controls.Any(c => (c.IdName ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase))) continue;
            Console.WriteLine($"{d.Id,9} {d.Key,-28} {d.Controls.Count,4}  {string.Join(" ", d.Controls.Select(c => c.IdName).Where(n => !string.IsNullOrEmpty(n)).Take(filter.Length > 0 ? 2000 : 6))}");
        }
    }
}
