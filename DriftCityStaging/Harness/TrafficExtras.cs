using System;
using System.Linq;
using DriftCity.Core;

/// <summary>Harness mode "extras": trains (Taipei chpath + signal timetable) and pedestrians (passerby.pcs).</summary>
static class TrafficExtras
{
    public static void Run(GameFileSystem fs, string[] args)
    {
        // trains
        var paths = ChPathFile.Parse(fs.Read(@"taipei\path\trainpath.chpath"));
        var signals = SignalFile.Parse(fs.Read(@"taipei\path\trainpath.signal"));
        Console.WriteLine($"trainpath: {paths.Count} paths, signal lists {signals.Count}");
        for (int i = 0; i < paths.Count; i++)
        {
            var p = paths[i];
            var s = new TrainSchedule(p, i < signals.Count ? signals[i] : null);
            Console.WriteLine($"  path {i}: {p.T.Length} keys, end {p.Duration:F2} (hdr {p.EndTime:F2}), {p.LengthMetres():F0} m, stops [{string.Join(", ", s.Stops.Select(x => x.ToString("F1")))}], period {s.Period:F1} s");
            for (float t = 0; t <= s.Period; t += s.Period / 24f)
            {
                float pos = s.Position(0, t, out float v, out bool st);
                var w = p.Sample(pos, out _);
                Console.WriteLine($"     t {t,7:F1}  pos {pos,7:F2}  v {v * 3.6f,5:F0} km/h {(st ? "STOP" : "")}  at ({w.X:F0},{w.Y:F0},{w.Z:F1})");
            }
        }
        // continuity check: no position jump larger than cruise speed allows except the wrap at the period end
        foreach (var city in new[] { "cras", "koinonia", "moonpalace", "oros" })
        {
            var pcs = PasserbyFile.Parse(fs.Read($@"{city}\passerby.pcs"));
            var agents = pcs.CreateAgents(0);
            int crossings = pcs.Routes.Count(pcs.IsCrossing);
            Console.WriteLine($"{city}: {pcs.Lines.Count} lines, {pcs.Paths.Count} paths, {pcs.Footways.Count} footways, {pcs.Crosswalks.Count} crosswalks, {pcs.Routes.Count} routes ({crossings} crossings) -> {agents.Count} pedestrians ({agents.Count(a => a.Crossing)} on crossings)");
            // segment vs line duration check
            int bad = 0;
            foreach (var r in pcs.Routes) foreach (var sg in r.Segments) { var l = pcs.LineOf(sg.Path); if (l == null || Math.Abs(l.Duration - (sg.End - sg.Start)) > 0.02f) bad++; }
            Console.WriteLine($"   segments whose line length differs from the piece: {bad}");
            float maxJump = 0;
            foreach (var a in agents.Take(400))
            {
                var r = pcs.Routes[a.Route];
                var prev = pcs.SampleRoute(r, pcs.Position(a, 1000f, out _), out _);
                for (float t = 1000.5f; t < 1100; t += 0.5f)
                {
                    var p = pcs.SampleRoute(r, pcs.Position(a, t, out _), out _);
                    float dx = p.X - prev.X, dy = p.Y - prev.Y; float j = (float)Math.Sqrt(dx * dx + dy * dy);
                    if (j > maxJump) maxJump = j;
                    prev = p;
                }
            }
            Console.WriteLine($"   largest 0.5 s step of the first 400 pedestrians: {maxJump:F2} m");
            foreach (var a in agents.Take(3).Concat(agents.Where(x => x.Crossing).Take(2)))
            {
                var r = pcs.Routes[a.Route];
                float pos = pcs.Position(a, 1234f, out bool walking);
                var w = pcs.SampleRoute(r, pos, out _);
                Console.WriteLine($"   ped {a.Index} type {a.Type} parts {string.Join(",", a.Parts)} route {a.Route} id {a.Id} crossing {a.Crossing} pos {pos:F2}/{r.End:F2} {(walking ? "walking" : "standing")} at ({w.X:F1},{w.Y:F1},{w.Z:F1})");
            }
        }
    }
}
