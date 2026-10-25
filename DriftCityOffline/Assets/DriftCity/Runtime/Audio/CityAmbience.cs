using System;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Core;
using DriftCity.Game;
using UnityEngine;

namespace DriftCity
{
    /// <summary>
    /// One row of init\env.xlt (loader 0x7d4360 -> 0x7d3d00, 0xb8-byte records at the env manager game+0x154): the sound id
    /// (40001 + group index, 0x520920 / 0x521a70), the wav under audio\env, the city number (1 moonpalace, 2 koinonia, 3 cras,
    /// 4 oros), the point (Gamebryo x y z), the two radii, the type, the misc (cluster) number and the repeat interval.
    /// </summary>
    public sealed class EnvSound
    {
        public int Row, Group, City, Type, Misc, Interval;
        public string Sid, File;
        /// <summary>The point in Unity axes (Gamebryo (x, y, z) -> (x, z, y)).</summary>
        public Vector3 Point;
        public float RadMin, RadMax;
        /// <summary>The 0x9c41 + index sound id the client registers (0x520920).</summary>
        public int SoundId => 0x9c41 + Group;
    }

    /// <summary>
    /// A cluster of a type 2 / 3 / 4 group (0x521a70): the rows of one group with the same misc number. With three or more
    /// points 0x51f0f0 builds a triangle fan (point 0, i - 1, i) on the ground plane; the listener inside one of its
    /// triangles (0x51e6f0) is "inside". Fewer than three points build nothing (never inside).
    /// </summary>
    public sealed class EnvCluster
    {
        public int Misc;
        public readonly List<Vector2> Points = new List<Vector2>();

        /// <summary>0x51e6f0: the ground point (x, z) inside the fan.</summary>
        public bool Contains(Vector2 p)
        {
            if (Points.Count < 3) return false;
            for (int i = 2; i < Points.Count; i++)
                if (InTriangle(p, Points[0], Points[i - 1], Points[i])) return true;
            return false;
        }

        static bool InTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Cross(p, a, b), d2 = Cross(p, b, c), d3 = Cross(p, c, a);
            bool neg = d1 < 0f || d2 < 0f || d3 < 0f, pos = d1 > 0f || d2 > 0f || d3 > 0f;
            return !(neg && pos);
        }

        static float Cross(Vector2 p, Vector2 a, Vector2 b) => (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);
    }

    /// <summary>A type 2 / 3 / 4 group of a city (0x521a70: one sound per group index, its clusters, the interval timer).</summary>
    public sealed class EnvGroup
    {
        public int Group, Type, Interval;
        public string File;
        public readonly List<EnvCluster> Clusters = new List<EnvCluster>();
        public bool Contains(Vector2 p) => Clusters.Any(c => c.Contains(p));
    }

    /// <summary>init\env.xlt: the 155 positional ambient sounds (DriftCity_RE/AUDIO.md 1).</summary>
    public static class EnvTable
    {
        static List<EnvSound> rows;

        public static IReadOnlyList<EnvSound> All
        {
            get
            {
                if (rows != null) return rows;
                rows = new List<EnvSound>();
                var fs = DriftCityData.Files;
                try
                {
                    if (fs != null && fs.Exists(@"init\env.xlt")) rows = Parse(XltTable.Parse(fs.Read(@"init\env.xlt")));
                }
                catch (Exception e) { Debug.LogWarning("env.xlt: " + e.Message); }
                return rows;
            }
        }

        public static void ResetCache() => rows = null;

        /// <summary>
        /// The client's format "%d%s %s%s %d %f%f%f %f%f %d%d%d %d" (0x7d3d00) over the columns after the row number: group
        /// index, SID, local name, file, city, point x y z, radMIN, radMAX, type, misc, Interval. The xlt repeats the column
        /// names Index / Y / Z, so the cells are read by position from SID.
        /// </summary>
        public static List<EnvSound> Parse(XltTable t)
        {
            var list = new List<EnvSound>();
            int sid = t.Col("SID");
            if (sid < 1) return list;
            float F(string[] r, int i) => i < r.Length && float.TryParse(r[i].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0f;
            int I(string[] r, int i) => i < r.Length && int.TryParse(r[i].Trim(), out var v) ? v : 0;
            for (int n = 0; n < t.Rows.Count; n++)
            {
                var r = t.Rows[n];
                if (r.Length <= sid + 12) continue;
                list.Add(new EnvSound
                {
                    Row = n,
                    Group = I(r, sid - 1),
                    Sid = r[sid].Trim(),
                    File = r[sid + 2].Trim(),
                    City = I(r, sid + 3),
                    Point = new Vector3(F(r, sid + 4), F(r, sid + 6), F(r, sid + 5)),
                    RadMin = F(r, sid + 7),
                    RadMax = F(r, sid + 8),
                    Type = I(r, sid + 9),
                    Misc = I(r, sid + 10),
                    Interval = I(r, sid + 11),
                });
            }
            return list;
        }

        /// <summary>The city number of 0x753870 (net+0x3b4ec: 1 moonpalace, 2 koinonia, 3 cras, 4 oros, 5 taipei).</summary>
        public static int CityNumber(string city) => CityMapInfo.CityIndex(city);

        /// <summary>0x5209f0: the type 1 rows of a city (one 3D loop each).</summary>
        public static List<EnvSound> Points(int city) => All.Where(s => s.City == city && s.Type == 1).ToList();

        /// <summary>0x521a70: the type 2 / 3 / 4 rows of a city grouped by index, each group's rows clustered by misc.</summary>
        public static List<EnvGroup> Groups(int city)
        {
            var groups = new List<EnvGroup>();
            foreach (var s in All.Where(s => s.City == city && (s.Type == 2 || s.Type == 3 || s.Type == 4)))
            {
                var g = groups.FirstOrDefault(x => x.Group == s.Group);
                if (g == null) { g = new EnvGroup { Group = s.Group, Type = s.Type, Interval = s.Interval, File = s.File }; groups.Add(g); }
                var c = g.Clusters.FirstOrDefault(x => x.Misc == s.Misc);
                if (c == null) { c = new EnvCluster { Misc = s.Misc }; g.Clusters.Add(c); }
                c.Points.Add(new Vector2(s.Point.x, s.Point.z));
            }
            return groups;
        }
    }

    /// <summary>
    /// The city ambience (DriftCity_RE/AUDIO.md 1): the env.xlt sounds of the city being driven, as the client's env sound
    /// manager runs them (init 0x521fd0 on a city stage, 0x753870; update 0x51f400 every frame), plus the rain loop of the
    /// weather (SID_RAIN, 0x433cea). Self-starting (a DontDestroyOnLoad host): it follows GameFlow's city.
    /// - type 1: a 3D loop at the point with min / max distance (0x520920 mode 1); it starts when the listener comes within
    ///   radMAX and fades out over 1 s once outside (stopped below 5 % of the volume).
    /// - type 2: a 2D one-shot every Interval seconds while the listener is inside one of the group's clusters (the timer is
    ///   reset outside, so entering plays it at once; a playing sound is not restarted).
    /// - type 3: a 2D loop while inside: fades in over 1.5 s, out over 3 s once outside.
    /// - type 4: as type 2, but "inside" is the car being off the road and on neither sand nor water (0x51f681).
    /// Volume: the effects volume (manager +0x168), 0 with the sound off (option byte 0x8d28c6).
    /// </summary>
    public sealed class CityAmbience : MonoBehaviour
    {
        public const float PointFadeOut = 1f, AreaFadeIn = 1.5f, AreaFadeOut = 3f, StopBelow = 0.05f;

        sealed class PointVoice
        {
            public EnvSound Row;
            public AudioSource Source;
            public bool Fading, Playing;
            public float Volume;
        }

        sealed class GroupVoice
        {
            public EnvGroup Group;
            public AudioSource Source;
            public float Timer, Volume, Ends;
            public bool Inside, FadingOut, Playing;
        }

        static CityAmbience instance;
        readonly List<PointVoice> points = new List<PointVoice>();
        readonly List<GroupVoice> groups = new List<GroupVoice>();
        AudioSource rain;
        string city;
        bool wasInCity;

        /// <summary>The city the sounds are set up for (null outside a city).</summary>
        public static string City => instance ? instance.city : null;
        public static CityAmbience Instance => instance;
        /// <summary>Tests: overrides the listener's ground point (null = the car / camera).</summary>
        public static Vector3? ListenerOverride;
        /// <summary>Tests: overrides the type 4 off-road gate (null = the car's CarRoadState).</summary>
        public static bool? OffRoadOverride;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot() => Ensure();

        public static CityAmbience Ensure()
        {
            if (instance) return instance;
            var go = new GameObject("City Ambience");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<CityAmbience>();
            return instance;
        }

        /// <summary>Point sounds playing now (tests; the player's own state, so it holds without an audio device).</summary>
        public int PlayingPoints => points.Count(p => p.Playing);
        /// <summary>Group sounds playing now (tests).</summary>
        public int PlayingGroups => groups.Count(g => g.Playing);
        public bool RainPlaying { get; private set; }
        public IEnumerable<string> PlayingFiles =>
            points.Where(p => p.Playing).Select(p => p.Row.File).Concat(groups.Where(g => g.Playing).Select(g => g.Group.File));
        /// <summary>Tests: the city to run (null = GameFlow's).</summary>
        public static string CityOverride;
        public int PointCount => points.Count;
        public int GroupCount => groups.Count;

        static float Volume => AudioLibrary.SoundOn ? AudioLibrary.SfxVolume : 0f;

        void Update()
        {
            var flow = GameFlow.Instance;
            bool inCity = flow && flow.InCity && !flow.InParkingLot;
            string want = CityOverride ?? (inCity ? flow.City : null);
            if (want != city || (CityOverride == null && inCity != wasInCity))
            {
                wasInCity = inCity;
                SetCity(want);
            }
            if (city == null) return;
            var listener = ListenerPoint(flow);
            Tick(listener, OffRoad(flow), Time.deltaTime);
        }

        AudioListener listenerCache;
        WeatherClock clockCache;
        float nextFind;

        /// <summary>The scene's listener and weather clock, looked up once a second.</summary>
        void FindScene()
        {
            if (Time.unscaledTime < nextFind && listenerCache) return;
            nextFind = Time.unscaledTime + 1f;
            listenerCache = FindFirstObjectByType<AudioListener>();
            clockCache = FindFirstObjectByType<WeatherClock>();
        }

        Vector3 ListenerPoint(GameFlow flow)
        {
            if (ListenerOverride.HasValue) return ListenerOverride.Value;
            FindScene();
            var l = listenerCache;
            if (l) return l.transform.position;
            if (flow && flow.PlayerCar) return flow.PlayerCar.transform.position;
            return Camera.main ? Camera.main.transform.position : Vector3.zero;
        }

        static bool OffRoad(GameFlow flow)
        {
            if (OffRoadOverride.HasValue) return OffRoadOverride.Value;
            var car = flow ? flow.PlayerCar : null;
            var road = car ? car.GetComponent<CarRoadState>() : null;
            return road && road.HasRoads && road.OffRoad && !road.Sand && !road.Water;
        }

        /// <summary>0x521fd0: drops the old city's sounds (0x5204e0 / 0x521d20) and sets up the new one's (0x5209f0, 0x521a70).</summary>
        public void SetCity(string newCity)
        {
            Clear();
            city = newCity;
            if (string.IsNullOrEmpty(newCity) || DriftCityData.Files == null) { city = null; return; }
            int n = EnvTable.CityNumber(newCity);
            foreach (var row in EnvTable.Points(n))
            {
                var clip = AudioLibrary.Wav("env/" + row.File);
                if (!clip) continue;
                var go = new GameObject("env " + row.Sid);
                go.transform.SetParent(transform, false);
                go.transform.position = row.Point;
                var src = go.AddComponent<AudioSource>();
                src.clip = clip;
                src.loop = true;
                src.playOnAwake = false;
                src.spatialBlend = 1f;
                // NiAudio's min / max distance: full volume inside radMIN, silent at radMAX (remake: a linear roll-off, so the
                // client's stop at radMAX makes no jump)
                src.rolloffMode = AudioRolloffMode.Linear;
                src.minDistance = Mathf.Max(1f, row.RadMin);
                src.maxDistance = Mathf.Max(src.minDistance + 1f, row.RadMax);
                src.dopplerLevel = 0f;
                points.Add(new PointVoice { Row = row, Source = src });
            }
            foreach (var g in EnvTable.Groups(n))
            {
                var clip = AudioLibrary.Wav("env/" + g.File);
                if (!clip) continue;
                var src = gameObject.AddComponent<AudioSource>();
                src.clip = clip;
                src.loop = g.Type == 3;   // 0x520920: types 2 and 4 are not looped
                src.playOnAwake = false;
                src.spatialBlend = 0f;
                groups.Add(new GroupVoice { Group = g, Source = src });
            }
            rain = gameObject.AddComponent<AudioSource>();
            rain.clip = AudioLibrary.Wav("sfx/SID_RAIN");
            rain.loop = true;
            rain.playOnAwake = false;
            rain.spatialBlend = 0f;
        }

        void Clear()
        {
            foreach (var p in points) if (p.Source) Destroy(p.Source.gameObject);
            foreach (var g in groups) if (g.Source) Destroy(g.Source);
            if (rain) Destroy(rain);
            points.Clear();
            groups.Clear();
            rain = null;
            RainPlaying = false;
        }

        /// <summary>One frame of 0x51f400 (tests call it directly).</summary>
        public void Tick(Vector3 listener, bool offRoad, float dt)
        {
            float vol = Volume;
            foreach (var p in points)
            {
                float max = p.Row.RadMax;
                bool inside = (p.Row.Point - listener).sqrMagnitude <= max * max;
                if (p.Playing)
                {
                    if (inside) { p.Fading = false; p.Volume = vol; }
                    else
                    {
                        // 0x51f400: fade over 1 s (vfunc 0xc4), stopped once under 5 % of the volume (0xdc)
                        p.Fading = true;
                        p.Volume = Mathf.MoveTowards(p.Volume, 0f, vol / PointFadeOut * dt);
                        if (p.Volume < vol * StopBelow || vol <= 0f) { p.Playing = false; if (p.Source) p.Source.Stop(); }
                    }
                }
                else if (inside) { p.Volume = vol; p.Playing = true; p.Fading = false; if (p.Source) p.Source.Play(); }
                if (p.Source) p.Source.volume = p.Volume;
            }

            var ground = new Vector2(listener.x, listener.z);
            float now = Time.unscaledTime;
            foreach (var g in groups)
            {
                bool inside = g.Group.Type == 4 ? offRoad : g.Group.Contains(ground);
                g.Inside = inside;
                switch (g.Group.Type)
                {
                    case 2:
                    case 4:
                        // a one-shot: playing until its clip has run
                        if (g.Playing && now >= g.Ends) g.Playing = false;
                        if (!inside) { g.Timer = 0f; break; }
                        g.Timer -= dt;
                        if (g.Timer < 0f && !g.Playing)
                        {
                            g.Timer = g.Group.Interval;
                            g.Volume = vol;
                            g.Playing = true;
                            g.Ends = now + (g.Source && g.Source.clip ? g.Source.clip.length : 0f);
                            if (g.Source) { g.Source.time = 0f; g.Source.volume = vol; g.Source.Play(); }
                        }
                        break;
                    case 3:
                        if (g.Playing)
                        {
                            if (inside)
                            {
                                g.FadingOut = false;
                                g.Volume = Mathf.MoveTowards(g.Volume, vol, vol / AreaFadeIn * dt);
                            }
                            else
                            {
                                g.FadingOut = true;
                                g.Volume = Mathf.MoveTowards(g.Volume, 0f, vol / AreaFadeOut * dt);
                                if (g.Volume < vol * StopBelow || vol <= 0f) { g.Playing = false; if (g.Source) g.Source.Stop(); }
                            }
                        }
                        else if (inside) { g.Volume = 0f; g.Playing = true; g.FadingOut = false; if (g.Source) g.Source.Play(); }
                        if (g.Source) g.Source.volume = g.Volume;
                        break;
                }
            }

            // rain: the weather's own loop (0x433cea), only under an open sky
            var clock = clockCache;
            bool raining = OriginalLook.Current.Rain && !(clock && clock.Indoors) && vol > 0f;
            if (raining != RainPlaying)
            {
                RainPlaying = raining;
                if (rain && rain.clip) { if (raining) rain.Play(); else rain.Stop(); }
            }
            if (rain) rain.volume = vol;
        }

        void OnDestroy()
        {
            if (instance == this) instance = null;
        }
    }
}
