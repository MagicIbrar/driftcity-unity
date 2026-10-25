using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>A pvptrackinfo gate line (pos_start -> pos_end), Unity x / z at y = 0.</summary>
    public readonly struct PvpGate
    {
        public readonly Vector3 A, B;
        public PvpGate(Vector3 a, Vector3 b) { A = a; B = b; }
        public Vector3 Centre => (A + B) * 0.5f;
        public bool Valid => (A - B).sqrMagnitude > 1e-4f || A.sqrMagnitude > 1e-4f;

        /// <summary>Which side of the gate's line a point is on (horizontal), and its distance from the gate's centre.</summary>
        public float Side(Vector3 p)
        {
            var d = B - A; d.y = 0;
            var n = new Vector3(-d.z, 0f, d.x);
            var q = p - A; q.y = 0;
            return Vector3.Dot(q, n);
        }

        public float Distance(Vector3 p) { var c = Centre; c.y = 0; p.y = 0; return Vector3.Distance(c, p); }
    }

    /// <summary>
    /// The client's checkpoint counter (0x72f3c0, every frame): the next gate counts when the car is within 150 m of it
    /// (d^2 &lt; 22500) and crosses its line (the sign of the dot product changes). The gates of a lap are the track's gate
    /// rows and then the finish line (the end row; the start line on circuits); progress = gates passed, lap = progress /
    /// gates per lap. Every gate is reported to the server (the AllGateReport mode of 0x787ce0).
    /// </summary>
    public sealed class GateTracker
    {
        public const float Radius = 150f;
        readonly RaceTrack track;
        float lastSide;
        bool armed;

        /// <summary>Gates passed over the whole race.</summary>
        public int Count { get; private set; }
        public int Laps => track.GatesPerLap > 0 ? Count / track.GatesPerLap : 0;
        public bool Usable => track != null && track.Gates.Count > 0 && track.End.Valid;
        public int Total => track.GatesPerLap * track.Laps;

        public GateTracker(RaceTrack t) { track = t; }

        /// <summary>The line the car has to cross next.</summary>
        public PvpGate Next
        {
            get
            {
                int k = Count % track.GatesPerLap;
                return k < track.Gates.Count ? track.Gates[k] : track.End;
            }
        }

        /// <summary>Call every frame with the car's position; true when it just passed the next gate.</summary>
        public bool Update(Vector3 pos)
        {
            if (!Usable || Count >= Total) return false;
            var g = Next;
            if (g.Distance(pos) > Radius) { armed = false; return false; }
            float side = g.Side(pos);
            bool crossed = armed && side != 0f && Mathf.Sign(side) != Mathf.Sign(lastSide);
            lastSide = side; armed = side != 0f;
            if (!crossed) return false;
            Count++;
            armed = false;
            return true;
        }

        /// <summary>Tests and resumes: sets the count directly.</summary>
        public void Set(int count) { Count = Mathf.Clamp(count, 0, Total); armed = false; }
    }

    public static class PvpGates
    {
        /// <summary>
        /// A lap-time track (CityType 4) has no aipath.nac: its racing line runs start -> gates -> finish, a point every 8 m,
        /// the height taken from the nearest point of the city's other race lines (the table has no heights).
        /// </summary>
        public static bool BuildLine(RaceTrack t, IEnumerable<RaceTrack> cityTracks)
        {
            if (t.Gates.Count < 2 || !t.Start.Valid) return false;
            var refs = cityTracks.Where(x => x != t && x.Line.Count > 0).SelectMany(x => x.Line).ToList();
            var corners = new List<Vector3> { t.Start.Centre };
            corners.AddRange(t.Gates.Select(g => g.Centre));
            corners.Add(t.End.Centre);
            t.Line.Clear(); t.Cum.Clear(); t.Length = 0f;
            for (int i = 0; i + 1 < corners.Count; i++)
            {
                var a = corners[i]; var b = corners[i + 1];
                int n = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(a, b) / 8f));
                for (int k = 0; k < n; k++) t.Line.Add(Vector3.Lerp(a, b, k / (float)n));
            }
            // circuits close on the start point; a point-to-point line ends on the finish
            if ((t.End.Centre - t.Start.Centre).sqrMagnitude > 1f) t.Line.Add(t.End.Centre);
            for (int i = 0; i < t.Line.Count; i++)
            {
                var p = t.Line[i];
                float y = 0f, best = float.MaxValue;
                foreach (var q in refs)
                {
                    float d = (q.x - p.x) * (q.x - p.x) + (q.z - p.z) * (q.z - p.z);
                    if (d < best) { best = d; y = q.y; }
                }
                t.Line[i] = new Vector3(p.x, y, p.z);
            }
            t.Cum.Add(0f);
            for (int i = 1; i < t.Line.Count; i++) { t.Length += Vector3.Distance(t.Line[i - 1], t.Line[i]); t.Cum.Add(t.Length); }
            return t.Line.Count >= 20;
        }

        /// <summary>The ground under a point whose height is only roughly known: the hit closest to that height.</summary>
        public static Vector3 GroundNear(Vector3 p)
        {
            var hits = Physics.RaycastAll(p + Vector3.up * 300f, Vector3.down, 700f);
            if (hits.Length == 0) return RaceMission.Ground(p);
            var best = hits.OrderBy(h => Mathf.Abs(h.point.y - p.y)).First();
            return best.point + Vector3.up * 0.8f;
        }
    }
}
