using System;
using System.Linq;
using DriftCity.Core;
static class Tex
{
    public static void Run(GameFileSystem fs, string[] names)
    {
        foreach (var n in names)
        {
            var bare = System.IO.Path.GetFileNameWithoutExtension(n);
            var hits = fs.AllFiles.Where(f => System.IO.Path.GetFileNameWithoutExtension(f).Equals(bare, StringComparison.OrdinalIgnoreCase)).Take(5).ToList();
            Console.WriteLine($"{n}: {(hits.Count == 0 ? "NOT FOUND" : string.Join(", ", hits))}");
        }
    }
}
