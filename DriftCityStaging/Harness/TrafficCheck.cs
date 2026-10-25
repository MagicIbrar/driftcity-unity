using System;
using System.Linq;
using DriftCity.Core;
static class TrafficCheck
{
    public static void Run(GameFileSystem fs, string[] cities)
    {
        foreach (var c in cities)
        {
            var path = $@"{c}\{c}.tcs";
            if (!fs.Exists(path)) { Console.WriteLine($"{c}: no tcs"); continue; }
            var net = TrafficNetwork.Parse(fs.Read(path));
            int linked = net.Lanes.Count(l => l.Next.Count > 0);
            float avgLen = net.Lanes.Count == 0 ? 0 : net.Lanes.Average(l => l.Length);
            Console.WriteLine($"{c}: nodes={net.Nodes.Count} lanes={net.Lanes.Count} withSuccessor={linked} avgLen={avgLen:F1}m maxNext={(net.Lanes.Count==0?0:net.Lanes.Max(l=>l.Next.Count))}");
        }
    }
}
