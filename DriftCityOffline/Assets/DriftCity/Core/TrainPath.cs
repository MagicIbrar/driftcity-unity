// Drift City time-keyed paths (<city>\path\*.chpath, "CHPATH\0" + stamp + author strings, u32 version 5, u32 size) and
// their stop lists (*.signal, text "trafficSignal"). The trains use taipei\path\trainpath.chpath / trainpath.signal,
// loaded by 0x4a9b40 when the city index is 5 (Taipei).
//   0x100      {u32 path count}
//   0x20000    one per path: 0x21000 {u32 20010710, u32 1975, u32 0x101}, 0x21100 {u32 2, 3 x u32, f32 end time, ..},
//              0x22000 {u32 n, n x {f32 t, f32 x, f32 y, f32 z}} (t = metres / 27.7778: seconds at 100 km/h)
// signal: "N //Num of path", then per path "M //num of Signal" and M lines "type path time" (type 0 = stop).
using System;
using System.Collections.Generic;
using System.Globalization;
using DriftCity.Core.Nif;

namespace DriftCity.Core
{
    public sealed class TimedPath
    {
        public float[] T = Array.Empty<float>();
        public Vec3[] Pos = Array.Empty<Vec3>();
        public float EndTime;
        public float Duration => T.Length > 0 ? T[T.Length - 1] : 0;

        /// <summary>Position at time key t (linear between keys, clamped) and the direction of travel there.</summary>
        public Vec3 Sample(float t, out Vec3 dir)
        {
            dir = new Vec3(0, 1, 0);
            int n = T.Length;
            if (n == 0) return default;
            if (n == 1) return Pos[0];
            int i = Array.BinarySearch(T, t);
            if (i < 0) i = ~i;
            i = Math.Max(1, Math.Min(n - 1, i));
            var a = Pos[i - 1];
            var b = Pos[i];
            float span = T[i] - T[i - 1];
            float k = span > 1e-6f ? Math.Max(0, Math.Min(1, (t - T[i - 1]) / span)) : 0;
            float dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
            float l = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (l > 1e-5f) dir = new Vec3(dx / l, dy / l, dz / l);
            return new Vec3(a.X + dx * k, a.Y + dy * k, a.Z + dz * k);
        }

        public float LengthMetres()
        {
            float L = 0;
            for (int i = 1; i < Pos.Length; i++)
            {
                float dx = Pos[i].X - Pos[i - 1].X, dy = Pos[i].Y - Pos[i - 1].Y, dz = Pos[i].Z - Pos[i - 1].Z;
                L += (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }
            return L;
        }
    }

    public static class ChPathFile
    {
        public static List<TimedPath> Parse(byte[] d)
        {
            var list = new List<TimedPath>();
            int body = -1;
            for (int q = 8; q + 8 <= d.Length && q < 128; q++)
                if (BitConverter.ToInt32(d, q) == 5 && BitConverter.ToInt32(d, q + 4) == d.Length - q) { body = q + 8; break; }
            if (body < 0) return list;
            Walk(d, body, d.Length, list, null, 0);
            return list;
        }

        static void Walk(byte[] d, int p, int end, List<TimedPath> list, TimedPath current, int depth)
        {
            while (p + 8 <= end)
            {
                uint tag = BitConverter.ToUInt32(d, p);
                int size = BitConverter.ToInt32(d, p + 4);
                if (size < 8 || p + size > end) return;
                int b = p + 8, len = size - 8;
                if (tag == 0x20000 && depth < 3)
                {
                    var path = new TimedPath();
                    list.Add(path);
                    Walk(d, b, b + len, list, path, depth + 1);
                }
                else if (tag == 0x21100 && current != null && len >= 20) current.EndTime = BitConverter.ToSingle(d, b + 16);
                else if (tag == 0x22000 && current != null && len >= 4)
                {
                    int n = BitConverter.ToInt32(d, b);
                    if (n > 0 && 4 + n * 16 <= len)
                    {
                        current.T = new float[n];
                        current.Pos = new Vec3[n];
                        for (int i = 0; i < n; i++)
                        {
                            int o = b + 4 + i * 16;
                            current.T[i] = BitConverter.ToSingle(d, o);
                            current.Pos[i] = new Vec3(BitConverter.ToSingle(d, o + 4), BitConverter.ToSingle(d, o + 8), BitConverter.ToSingle(d, o + 12));
                        }
                    }
                }
                else if ((tag & 0xFFF) == 0 && tag != 0x20000 && depth < 3 && len >= 8) Walk(d, b, b + len, list, current, depth + 1);
                p += size;
            }
        }
    }

    /// <summary>A stop (or signal) on a path: type 0 = stop, the path index and its position as a time key.</summary>
    public struct PathSignal { public int Type, Path; public float Time; }

    public static class SignalFile
    {
        /// <summary>Per path, its signals in file order (text: values first, "//" comments).</summary>
        public static List<List<PathSignal>> Parse(byte[] d)
        {
            var result = new List<List<PathSignal>>();
            var lines = new List<string[]>();
            foreach (var raw in Latin1.Decode(d, 0, d.Length).Split('\n'))
            {
                string l = raw;
                int c = l.IndexOf("//", StringComparison.Ordinal);
                if (c >= 0) l = l.Substring(0, c);
                var parts = l.Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0) lines.Add(parts);
            }
            // trafficSignal, version, path count, then per path: count + entries
            int i = 0;
            if (i < lines.Count && lines[i][0] == "trafficSignal") i++;
            if (i < lines.Count) i++;                         // version (20050508)
            if (i >= lines.Count || !int.TryParse(lines[i][0], out int paths)) return result;
            i++;
            for (int p = 0; p < paths && i < lines.Count; p++)
            {
                var list = new List<PathSignal>();
                if (!int.TryParse(lines[i][0], out int n)) break;
                i++;
                for (int k = 0; k < n && i < lines.Count; k++, i++)
                {
                    var f = lines[i];
                    if (f.Length < 3) continue;
                    int.TryParse(f[0], out int type);
                    int.TryParse(f[1], out int path);
                    float.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float time);
                    list.Add(new PathSignal { Type = type, Path = path, Time = time });
                }
                result.Add(list);
            }
            return result;
        }
    }

    /// <summary>
    /// The Taipei MRT timetable (0x4a9420 constants, 0x4a9c90 schedule). Each path is one direction of the line with
    /// three trains spread over the path's run time. Between stops a train cruises at 27.78 m/s (100 km/h, one time-key
    /// unit per second), brakes linearly over 300 m into each stop, waits 8 s and accelerates out the same way; after
    /// the last stop it runs on to the end of the path and starts over at the beginning.
    /// </summary>
    public sealed class TrainSchedule
    {
        public const float CruiseSpeed = 27.7777786f;   // in[3], m/s
        public const float DwellTime = 8f;              // in[5], s
        public const float BrakeDistance = 300f;        // in[6], m
        public const int TrainsPerPath = 3;             // 0x4aa4b0: 3 trains per path
        public const int CarsPerTrain = 6;              // 0x4a99d6
        public const float CarSpacing = 12f;            // 0x4a99cf, metres between car positions along the path
        public const int VehicleId = 20505;             // 0x4a9140: vehiclelist "mrt", vehicle\traffic\c_t_5_mrt.nif

        public readonly TimedPath Path;
        public readonly float[] Stops;                  // stop positions (time keys) in path order

        public TrainSchedule(TimedPath path, IEnumerable<PathSignal> signals)
        {
            Path = path;
            var s = new List<float>();
            if (signals != null) foreach (var sig in signals) if (sig.Type == 0) s.Add(sig.Time);
            Stops = s.ToArray();
        }

        /// <summary>The path's run time without stops: its last time key (in[2] = the path controller's end key time).</summary>
        public float RunTime => Path.Duration;

        /// <summary>One cycle: run time plus, per stop, the extra time of braking, waiting and accelerating (in[4]).</summary>
        public float Period
        {
            get
            {
                float brakeTime = 2f * BrakeDistance / CruiseSpeed;
                return Stops.Length * (brakeTime + DwellTime) + RunTime;
            }
        }

        /// <summary>
        /// Path position (time key) and speed (m/s) of train <paramref name="index"/> at game time <paramref name="time"/>
        /// seconds. Trains are spaced RunTime / 3 apart in time (0x4a9c90).
        /// </summary>
        public float Position(int index, float time, out float speed, out bool stopped)
        {
            float period = Period;
            float spacing = RunTime / TrainsPerPath;
            float t = Repeat(index * spacing + time, period);
            float d = BrakeDistance * 0.036f;            // braking distance in time keys
            float brakeTime = 2f * BrakeDistance / CruiseSpeed;
            float stopOverhead = 2f * brakeTime + DwellTime;    // brake + wait + accelerate
            float segEnd = 0f, prevEnd = 0f, start = 0f;
            stopped = false;
            for (int k = 0; k < Stops.Length; k++)
            {
                start = k == 0 ? 0f : Stops[k - 1] + d;
                prevEnd = segEnd;
                segEnd += (Stops[k] - start - d) * 27.7777786f / CruiseSpeed + stopOverhead;
                if (t < segEnd)
                {
                    float brakeStart = segEnd - stopOverhead;
                    if (t < brakeStart)
                    {
                        speed = CruiseSpeed;
                        return start + (t - prevEnd) * CruiseSpeed * 0.036f;
                    }
                    if (t < brakeStart + brakeTime)
                    {
                        float rem = brakeTime - (t - brakeStart);
                        speed = rem / brakeTime * CruiseSpeed;
                        return (BrakeDistance - rem * speed * 0.5f) * 0.036f + (Stops[k] - d);
                    }
                    float accelStart = brakeStart + brakeTime + DwellTime;
                    if (t < accelStart) { speed = 0; stopped = true; return Stops[k]; }
                    float e = t - accelStart;
                    speed = CruiseSpeed / brakeTime * e;
                    return e * speed * 0.018f + Stops[k];
                }
            }
            // after the last stop: cruise on to the end of the path
            float from = Stops.Length > 0 ? Stops[Stops.Length - 1] + d : 0f;
            speed = CruiseSpeed;
            return Math.Min(RunTime, from + (t - segEnd) * CruiseSpeed * 0.036f);
        }

        static float Repeat(float t, float len) => len <= 0 ? 0 : t - (float)Math.Floor(t / len) * len;
    }
}
