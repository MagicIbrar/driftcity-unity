using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Core;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.Game
{
    /// <summary>A PvP track from init\pvptrackinfo.xlt that has an AI racing line.</summary>
    public sealed class RaceTrack
    {
        public string City, Track, Name;
        public int Laps;
        public List<Vector3> Line = new List<Vector3>();
        public float Length;
        /// <summary>pvptrackinfo start-row columns (PVP.md section 11): track id, AI column, New mark, CityType (1 battle,
        /// 3 tutorial, 4 lap-time), Reverse ("_r" variant) and the six per-arena Recommend flags.</summary>
        public int Id, CityType = 1;
        public bool Ai, New, Reverse;
        public string Recommend = "";
        /// <summary>Distance along the line to each point (m), for progress and the DNF distance.</summary>
        public List<float> Cum = new List<float>();
        /// <summary>The pvptrackinfo gate rows (type 2) in order, the start row's line and the end row's line (the finish;
        /// the start line again on circuits), Unity coordinates at y = 0 (the table has no heights).</summary>
        public readonly List<PvpGate> Gates = new List<PvpGate>();
        public PvpGate Start, End;
        /// <summary>Start-row minimap bounds (game x / y: minx, miny, maxx, maxy) for the PvP minimap 1170000.</summary>
        public float MinX, MinY, MaxX, MaxY;
        /// <summary>Gate reports per lap: the gates, then the finish line.</summary>
        public int GatesPerLap => Gates.Count + 1;
        /// <summary>The track's own folder ("moonpalace\track1": camera.nif, spotcam.nxt).</summary>
        public string Dir => $@"{City}\{Track.ToLowerInvariant()}";

        public bool Recommended(int arena) => arena >= 0 && arena < Recommend.Length && Recommend[arena] == '1';
    }

    /// <summary>One row of a race's results (offline race, online race or tournament stage).</summary>
    public sealed class PvpResultEntry
    {
        public string Name;
        public int Level = 1, Place, Team, Points, Ladder;
        public float Time, DnfMeters;
        public bool Me, Ai, LadderShown, LevelUp;
        public long Exp, Mito;
        /// <summary>Bonus icon flags (Pvp.Bonus*) and the Christmas-tree stage 1..4 of the row.</summary>
        public int Bonus, Tree;
        /// <summary>Wager rooms (0x1f3): the driver's Mito change and new total.</summary>
        public long WagerDelta, WagerMito;
    }

    public sealed class PvpRaceResult
    {
        public RaceTrack Track;
        public string TrackName;
        public bool Team, Online;
        public int TeamA, TeamB, Winner = -1;
        public float BestLap;
        /// <summary>Wager grade of the room (results on 4080000 / 4090000), the arena, the reward pick offered (4310000),
        /// and whether I watched as a spectator.</summary>
        public int Wager, Arena = -1;
        public bool Pick, Spectator;
        public List<PvpResultEntry> Entries = new List<PvpResultEntry>();
        public PvpResultEntry Mine => Entries.FirstOrDefault(e => e.Me);
    }

    /// <summary>
    /// A Battle Zone race against AI drivers on an original PvP track inside the open city (the client's PvP stage, PVP.md
    /// sections 6-8): grid by room slot, the "pvpready" grid fly-by, the 6-second countdown with pvp_countdownstart, laps
    /// with pvp_lap2..7 / pvp_finallap, wrong-way warning, the RANK/LAPS/RACETIME/BESTTIME HUD, 10 s after the first
    /// finisher (pvp_tencount), pvp_raceover / pvp_retire, then the results (PvpResults) and back to the wait room. AI cars
    /// use the same physics as the player and follow the track's AI line (aipath.nac).
    /// </summary>
    public sealed class RaceMission : MonoBehaviour
    {
        public static RaceMission Active { get; private set; }
        public int Opponents = 3;

        /// <summary>The countdown (T-6 .. T, 0x75fc10) and, for room races, the time before it from the start of the stage:
        /// the "pvpready" grid camera until T-19.5 s, then "pvpstart" (the track's camera.nif) until the countdown.</summary>
        public const float CountdownSeconds = 6f, ReadyUntil = 19.5f;
        /// <summary>Room races: seconds from the start of the race stage to T (the server's race.start uses the same).</summary>
        public static float IntroSeconds = 22f;
        /// <summary>The grid fly-by of the old quick start (kept for callers; room races use <see cref="IntroSeconds"/>).</summary>
        public const float FlyBySeconds = 3f;
        /// <summary>After the first finisher the others get this long (0x1ef + pvp_tencount: first finish + 10 s).</summary>
        public const float TenCount = 10f;
        /// <summary>Seconds driving against the course before pvp_wrongway (0x72f3c0).</summary>
        public const float WrongWaySeconds = 5f;

        RaceTrack track;
        DriftCar player;
        PvpRoom room;
        Action<PvpRaceResult> done;
        readonly List<Racer> racers = new List<Racer>();
        float raceTime, phaseTime, endAt = -1f, wrongWay;
        enum Phase { FlyBy, Countdown, Racing, Finished }
        Phase phase = Phase.FlyBy;
        Text bigText;
        GameObject hudRoot;
        PvpHud hud;
        PvpMinimap minimap;
        PvpCameraRig rig;
        readonly List<GameObject> gates = new List<GameObject>();
        int prevTraffic = -1;
        bool countdownFx, startCam, spectator, realMatch, mission;
        PvpReplay.Recorder recorder;
        /// <summary>The race being recorded for its replay (PvpReplay).</summary>
        public PvpReplay.Recorder Recorder => recorder;
        float flyBy = FlyBySeconds, countdownLength = CountdownSeconds;
        GateTracker myGates;

        public bool Spectator => spectator;
        public RaceTrack Track => track;
        public PvpRoom Room => room;

        sealed class Racer
        {
            public DriftCar Car;
            public AiDriver Ai;
            public PvpMember Member;
            public string Name;
            public int Index;          // nearest line point
            public int Lap;
            public float Progress;     // laps * points + index
            public bool Done;
            public float FinishTime;
            public float LapStart, BestLap;
            public bool Half;          // passed mid-track since the last lap (the grid sits behind the line)
            public int Place;
        }

        // ---------------- tracks ----------------
        static List<RaceTrack> tracks;

        public static List<RaceTrack> TracksFor(string city)
        {
            if (tracks == null)
            {
                tracks = new List<RaceTrack>();
                var fs = DriftCityData.Files;
                var t = XltTable.Parse(fs.Read(@"init\pvptrackinfo.xlt"));
                var seen = new HashSet<string>();
                int posStart = t.Col("pos_start"), posEnd = t.Col("pos_end");
                PvpGate Line(string[] r)
                {
                    float F(int i) => i >= 0 && i < r.Length && float.TryParse(r[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : 0f;
                    return new PvpGate(new Vector3(F(posStart), 0f, F(posStart + 1)), new Vector3(F(posEnd), 0f, F(posEnd + 1)));
                }
                RaceTrack cur = null;
                var gateless = new List<RaceTrack>();   // lap-time tracks: no aipath, the line runs through the gates
                foreach (var r in t.Rows)
                {
                    if (t.Get(r, "Support") != "1") { cur = null; continue; }
                    int type = t.GetInt(r, "type", -1);
                    if (type == 1 && cur != null) { cur.End = Line(r); continue; }
                    if (type == 2 && cur != null) { cur.Gates.Add(Line(r)); continue; }
                    if (type != 0) continue;
                    cur = null;
                    string c = t.Get(r, "city").Trim().ToLowerInvariant(), tr = t.Get(r, "track").Trim();
                    if (c == "kononia") c = "koinonia";   // a typo in two start rows
                    if (c.Length == 0 || tr.Length == 0 || !seen.Add(c + "/" + tr)) continue;
                    var rt = new RaceTrack
                    {
                        City = c, Track = tr, Name = t.Get(r, "name"), Laps = Mathf.Clamp(t.GetInt(r, "LapCount", 2), 1, 5),
                        Id = t.GetInt(r, "Id", -1), Ai = t.Get(r, "AI") == "1", New = t.Get(r, "New") == "1",
                        Reverse = t.Get(r, "Reverse") == "1", CityType = t.GetInt(r, "CityType", 1),
                        Recommend = (t.Get(r, "Recommend") ?? "").Trim('"', ' '),
                        MinX = t.GetFloat(r, "minx"), MinY = t.GetFloat(r, "miny"), MaxX = t.GetFloat(r, "maxx"), MaxY = t.GetFloat(r, "maxy"),
                    };
                    rt.Start = rt.End = Line(r);
                    if (string.IsNullOrEmpty(rt.Name)) rt.Name = $"{c} {tr}";
                    string path = $@"{c}\{tr.ToLowerInvariant()}\aipath.nac";
                    if (!fs.Exists(path))
                    {
                        // lap-time tracks (4) and the apprentice course "moonpalace Tutorial" (3, ApprenticeCourse) have no
                        // aipath.nac: their line runs through the gates
                        if (rt.CityType == 4 || rt.CityType == 3) { gateless.Add(rt); cur = rt; }
                        continue;
                    }
                    var pts = RacePath.ParseNac(Cp949.Decode(fs.Read(path)));
                    if (pts.Count < 20) continue;
                    foreach (var p in pts) rt.Line.Add(DriftCityData.ToUnity(p));
                    rt.Cum.Add(0f);
                    for (int i = 1; i < rt.Line.Count; i++) { rt.Length += Vector3.Distance(rt.Line[i - 1], rt.Line[i]); rt.Cum.Add(rt.Length); }
                    tracks.Add(rt);
                    cur = rt;
                }
                foreach (var rt in gateless) if (PvpGates.BuildLine(rt, tracks.Where(x => x.City == rt.City))) tracks.Add(rt);
                Debug.Log($"Drift City: {tracks.Count} raceable tracks");
            }
            return tracks.Where(x => x.City == city).ToList();
        }

        /// <summary>The T key and the station's "Battle Zone" button: the Battle Zone lobby (PvpLobby).</summary>
        public static void Offer(CitySession session)
        {
            if (Active || OnlineRace.Active || ChaseMission.Active || LapTimeBattle.Active || session == null) return;
            PvpLobby.Open(session);
        }

        internal static string Pretty(string s) => string.IsNullOrEmpty(s) ? "Track" : char.ToUpperInvariant(s[0]) + s.Substring(1).Replace('_', ' ');

        // ---------------- race ----------------
        /// <summary>Starts a Battle Zone room's race: the room's AI members race the local player on the room's track (a room
        /// the player only watches runs its AI drivers with the spectator views).</summary>
        public static RaceMission StartRoom(PvpRoom room, CitySession session, Action<PvpRaceResult> done)
        {
            if (Active || room == null || room.Track == null || session == null || !session.Car) return null;
            var m = new GameObject("Race").AddComponent<RaceMission>();
            m.flyBy = Mathf.Max(0f, IntroSeconds - CountdownSeconds);
            m.BeginRoom(room.Track, session.Car, session.Traffic, room, done);
            if (room.IsChannelBattle) GameEffects.ChannelRound();   // 0x723d60 -> 0x674e10: channel_count
            return m;
        }

        /// <summary>A quick race against <see cref="Opponents"/> AI drivers (tests and old callers).</summary>
        void Begin(RaceTrack t, DriftCar playerCar, TrafficSystem traffic)
        {
            var r = new PvpRoom { Title = "Race", Max = 8, Arena = Pvp.ArenaFree, Track = t, Practice = true };
            r.ResetSlots();
            var p = PlayerProfile.Current;
            var me = r.Add(new PvpMember { Id = 1, Name = p?.Name ?? "You", Level = p?.Level ?? 1, Me = true, Ready = true }, Opponents);
            r.HostId = me.Id;
            for (int i = 0; i < Opponents; i++) r.Add(Pvp.NewAi(r, p?.Level ?? 1), i);
            // the quick race keeps the short start of the old offline race: no fly-by, 3-2-1-GO
            flyBy = 0f; countdownLength = 4f;
            BeginRoom(t, playerCar, traffic, r, null);
        }

        void BeginRoom(RaceTrack t, DriftCar playerCar, TrafficSystem traffic, PvpRoom pvpRoom, Action<PvpRaceResult> onDone)
        {
            Active = this;
            track = t; player = playerCar; room = pvpRoom; done = onDone;
            spectator = room.Spectating && !room.Members.Any(m => m.Me);
            if (traffic)
            {
                prevTraffic = traffic.MaxCars;
                // data\pvptrafficnum.dat by track, density and drivers (0x71fc40); it counts the whole track, the city traffic
                // only keeps cars near the player, so its own cap still applies
                int trackNo = int.TryParse(new string((t.Track ?? "").Where(char.IsDigit).ToArray()), out var tn) ? tn : 0;
                int cars = PvpTraffic.Count(CityMapInfo.CityIndex(t.City), trackNo, room.Members.Count, PvpTraffic.DensityFor(room.MapFlags));
                if ((room.MapFlags & Pvp.TrafficIdle) != 0) cars = 0;
                // the track's own traffic on PVP_map<c>_<NN>.chpath (PvpTraffic.cs); the city traffic steps aside
                pathTraffic = PvpPathTraffic.Start(traffic, t.City, trackNo, cars, UnityEngine.Random.Range(1, int.MaxValue));
                traffic.MaxCars = pathTraffic ? 0 : Mathf.Min(traffic.MaxCars, cars);
                traffic.TrimTo(traffic.MaxCars);
            }
            // battle quickslot items marked for the lower half of the field (0x580820)
            BattleQuickSlots.InLowerHalf = () => { var o = Order().ToList(); int i = o.FindIndex(r => r.Car == player); return i >= 0 && i + 1 > o.Count / 2; };
            var p = PlayerProfile.Current;

            Vector3 start = t.Line[0], dir = (t.Line[Mathf.Min(3, t.Line.Count - 1)] - start).normalized;
            dir.y = 0; dir.Normalize();
            var rot = Quaternion.LookRotation(dir, Vector3.up);

            // grid: each driver on its room slot (0x725560 -> 0x725fd0(slot)), two abreast behind the line
            foreach (var m in room.Members.OrderBy(x => x.Slot))
            {
                var pos = Ground(GridSlot(t, m.Slot));
                if (m.Me)
                {
                    Teleport(player, pos, rot);
                    racers.Insert(0, new Racer { Car = player, Member = m, Name = m.Name });
                    // a Real Match room: the player's car wears the RM car's look of its slot for the race (PVP.md 14.4)
                    if (room.RealMatch) RealMatchSkin.Apply(player.gameObject, m.Slot);
                    continue;
                }
                if (!m.Ai) continue;
                racers.Add(new Racer { Car = SpawnAi(m, pos, rot, t, room.RealMatch ? m.Slot : -1), Member = m, Name = m.Name });
                racers[racers.Count - 1].Ai = racers[racers.Count - 1].Car.GetComponent<AiDriver>();
            }
            if (!spectator && (racers.Count == 0 || racers[0].Car != player))
            {
                // the room had no local member (should not happen): race from the back of the grid
                Teleport(player, Ground(GridSlot(t, room.Members.Count)), rot);
                racers.Insert(0, new Racer { Car = player, Name = p?.Name ?? "You", Member = new PvpMember { Me = true, Name = p?.Name ?? "You" } });
            }
            player.ExternalControl = true; // frozen until GO (a spectator's car waits where it is)
            if (!spectator)
            {
                myGates = new GateTracker(t);
                // a Real Match room: everyone drives the Real Match car's ratings (RMCARINFO 1150000)
                realMatch = room.RealMatch;
                if (realMatch) { var s = Pvp.RealMatchStats(); player.ApplyStats(s.accel, s.speed, s.crash, s.boost); }
                // a mission-battle room (flag 0x10000, 11479): fixed stats, whatever the car
                mission = room.Mission > 0;
                if (mission) { var s = PvpMissionBattle.FixedStats; player.ApplyStats(s.accel, s.speed, s.crash, s.boost); }
                // R during the race puts the car back on the racing line, facing along it
                player.RespawnOverride = () =>
                {
                    if (track == null || racers.Count == 0) return null;
                    int i = racers[0].Index, n = track.Line.Count;
                    var a = track.Line[i]; var b = track.Line[(i + 3) % n];
                    var f = b - a; f.y = 0;
                    return (Ground(a), Quaternion.LookRotation(f.sqrMagnitude > 1e-4f ? f.normalized : Vector3.forward, Vector3.up));
                };
            }
            BuildHud();
            BuildGates();
            rig = PvpCameraRig.Ensure();
            if (flyBy > 0f) rig.BeginReady(track, room.Members.Count);   // "pvpready": the grid camera
            AudioLibrary.PlayMusic(this, "bgm/DriftCity_1stpvp_01.mp3");
        }

        /// <summary>An AI driver's car on the line; in a Real Match room (<paramref name="realMatchSlot"/> >= 0) it is the Duel
        /// wearing the visual.xlt look of its slot (paint, decal in its decal colour, neon).</summary>
        internal static DriftCar SpawnAi(PvpMember m, Vector3 pos, Quaternion rot, RaceTrack t, int realMatchSlot = -1)
        {
            var model = (realMatchSlot >= 0 ? Pvp.RealMatchCar() : null) ?? m.Car ?? GameTables.PlayerCars.FirstOrDefault();
            var ai = CarFactory.BuildDrivable(model, UnityEngine.Random.Range(0, 4), pos, rot);
            if (realMatchSlot >= 0) CarPaint.ApplyRealMatch(ai.gameObject, model, Pvp.RealMatchLookFor(realMatchSlot));
            ai.name = "AI " + m.Name;
            var g = ShopData.Grade(model, 0);
            if (g != null) ai.ApplyStats(g.Accel, g.Speed, g.Crash, g.Boost);
            ai.ExternalControl = true;
            var driver = ai.gameObject.AddComponent<AiDriver>();
            ai.gameObject.AddComponent<CarEffects>();
            driver.Line = t.Line;
            driver.Skill = m.Skill;
            driver.Hold = true;
            return ai;
        }

        /// <summary>Grid position of a slot: slot 0 front left, two abreast, 9 m between rows.</summary>
        internal static Vector3 GridSlot(RaceTrack t, int slot)
        {
            Vector3 start = t.Line[0], dir = t.Line[Mathf.Min(3, t.Line.Count - 1)] - start;
            dir.y = 0;
            if (dir.sqrMagnitude < 1e-4f) dir = Vector3.forward;
            dir.Normalize();
            var right = Vector3.Cross(Vector3.up, dir);
            return start - dir * (8f + 9f * (slot / 2)) + right * ((slot % 2 == 0) ? -2.6f : 2.6f);
        }

        internal static Vector3 Ground(Vector3 p)
        {
            if (Physics.Raycast(p + Vector3.up * 30f, Vector3.down, out var hit, 80f)) return hit.point + Vector3.up * 0.8f;
            return p + Vector3.up;
        }

        internal static void Teleport(DriftCar car, Vector3 pos, Quaternion rot)
        {
            var rb = car.GetComponent<Rigidbody>();
            rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero;
            car.transform.SetPositionAndRotation(pos, rot);
            rb.position = pos; rb.rotation = rot;
        }

        void BuildGates()
        {
            // no-gate rooms (flag 0x400) draw no checkpoint posts
            if (room != null && room.NoGate) return;
            var shader = Shader.Find("Sprites/Default");
            // the pvptrackinfo gates when the track has them (a post at each end of the line), else posts every ~150 m
            if (track.Gates.Count > 0)
            {
                int k = 0;
                foreach (var g in track.Gates.Concat(new[] { track.End }))
                {
                    bool finish = k++ == track.Gates.Count;
                    foreach (var end in new[] { g.A, g.B })
                    {
                        var post = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                        Destroy(post.GetComponent<Collider>());
                        var ground = PvpGates.GroundNear(new Vector3(end.x, NearestY(end), end.z));
                        post.transform.position = ground + Vector3.up * 5f;
                        post.transform.localScale = new Vector3(finish ? 1.6f : 1f, 6f, finish ? 1.6f : 1f);
                        post.GetComponent<Renderer>().sharedMaterial = OwnedAssets.Track(post, new Material(shader) { color = finish ? new Color(1, 0.85f, 0.1f, 0.5f) : new Color(0.2f, 0.8f, 1f, 0.35f) });
                        gates.Add(post);
                    }
                }
                return;
            }
            float acc = 0;
            for (int i = 0; i < track.Line.Count; i++)
            {
                if (i > 0) acc += Vector3.Distance(track.Line[i - 1], track.Line[i]);
                if (i != 0 && acc < 150f) continue;
                acc = 0;
                var g = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                Destroy(g.GetComponent<Collider>());
                g.transform.position = track.Line[i] + Vector3.up * 6f;
                g.transform.localScale = new Vector3(i == 0 ? 2.5f : 1.2f, 6f, i == 0 ? 2.5f : 1.2f);
                g.GetComponent<Renderer>().sharedMaterial = OwnedAssets.Track(g, new Material(shader) { color = i == 0 ? new Color(1, 0.85f, 0.1f, 0.5f) : new Color(0.2f, 0.8f, 1f, 0.35f) });
                gates.Add(g);
            }
        }

        /// <summary>The height of the race line nearest to a point (gates have no height in the table).</summary>
        float NearestY(Vector3 p)
        {
            float best = float.MaxValue, y = p.y;
            foreach (var q in track.Line) { float d = (q.x - p.x) * (q.x - p.x) + (q.z - p.z) * (q.z - p.z); if (d < best) { best = d; y = q.y; } }
            return y;
        }

        void BuildHud()
        {
            var ui = UiManager.Instance;
            hudRoot = NuiView.NewRect("Race HUD", ui.Root).gameObject;
            NuiView.Stretch((RectTransform)hudRoot.transform);
            bigText = Label(hudRoot.transform, new Vector2(0, 0), new Vector2(1024, 200), 96, TextAnchor.MiddleCenter, true);
            minimap = PvpMinimap.Open(track);
            if (spectator)
            {
                // a spectator gets VIEWING 1960000 instead of the driver's HUD
                PvpSpectate.BeginViewing(room, track, Drivers, () => TeamPoints(), () => { Abort(); PvpWaitRoom.Quit(); }, () => phase == Phase.Racing);
                PvpGhost.BeginRace(false);
                return;
            }
            hud = PvpHud.Open();
        }

        /// <summary>The field in race order for VIEWING.</summary>
        List<PvpSpectate.Driver> Drivers() => Order().Where(r => r.Car).Select((r, i) => new PvpSpectate.Driver
        {
            Name = r.Name, Car = r.Car.transform, Progress = r.Progress, Team = r.Member?.Team ?? 0, Done = r.Done, Place = i + 1,
            Fraction = r.Done ? 1f : Mathf.Clamp01(r.Progress / Mathf.Max(1f, (float)track.Line.Count * track.Laps)),
        }).ToList();

        (int a, int b) TeamPoints()
        {
            int field = racers.Count, a = 0, b = 0;
            foreach (var r in racers.Where(x => x.Done && x.Place > 0)) { if ((r.Member?.Team ?? 0) == 0) a += field - r.Place + 1; else b += field - r.Place + 1; }
            return (a, b);
        }

        static Text Label(Transform parent, Vector2 pos, Vector2 size, int fontSize, TextAnchor anchor, bool centered = false)
        {
            var rt = NuiView.NewRect("text", parent);
            if (centered) { rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f); rt.pivot = new Vector2(0.5f, 0.5f); }
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            var t = rt.gameObject.AddComponent<Text>();
            t.font = UiAssets.Font("Verdana " + fontSize, out _, out _);
            t.fontSize = fontSize; t.fontStyle = FontStyle.Bold; t.alignment = anchor; t.raycastTarget = false;
            t.color = new Color(1f, 0.85f, 0.2f);
            var o = rt.gameObject.AddComponent<Outline>(); o.effectColor = Color.black; o.effectDistance = new Vector2(2, -2);
            return t;
        }

        Racer Me => spectator || racers.Count == 0 ? null : racers[0];

        void Update()
        {
            if (phase == Phase.Finished) return;
            if (!player) { Abort(); return; }   // the player's car was replaced
            hud?.Tick();
            phaseTime += Time.deltaTime;
            if (phase == Phase.FlyBy || phase == Phase.Countdown)
            {
                foreach (var r in racers) { if (!r.Car) continue; var rb = r.Car.GetComponent<Rigidbody>(); rb.linearVelocity = Vector3.Lerp(rb.linearVelocity, Vector3.zero, 0.5f); }
                if (!player.ExternalControl) player.ExternalControl = true;
                if (phase == Phase.FlyBy)
                {
                    // T-19.5 s: "pvpstart" (the track's camera.nif); without one the grid camera goes on
                    if (!startCam && flyBy > 0f && flyBy - phaseTime <= ReadyUntil - CountdownSeconds)
                    {
                        startCam = true;
                        if (rig && !rig.BeginStart(track) && rig.Current != PvpCameraRig.Mode.Ready) rig.BeginReady(track, room.Members.Count);
                    }
                    if (phaseTime >= flyBy) StartCountdown();
                }
                else
                {
                    float left = countdownLength - phaseTime;
                    // the countdown NIF draws the digits; the text stands in when it is missing
                    if (!countdownFx) bigText.text = left > 3f ? "" : left > 0f ? Mathf.CeilToInt(left).ToString() : "GO!";
                    if (left <= 0f) Go();
                }
                UpdateHud();
                return;
            }
            if (bigText.text.Length > 0 && phaseTime > 1f) bigText.text = "";
            raceTime += Time.deltaTime;
            int n = track.Line.Count;
            foreach (var r in racers)
            {
                if (r.Done || !r.Car) continue;
                int next = Nearest(r.Car.transform.position, r.Index, n);
                if (r.Car == player && myGates != null && myGates.Usable)
                {
                    // the player's laps come from the checkpoint gates (0x72f3c0)
                    bool passed = myGates.Update(player.transform.position);
                    // the gate effects (NewGateEffect, PVP.md 15.2): every gate but the finish line of a lap
                    if (passed && myGates.Count % track.GatesPerLap != 0 && room != null && !room.NoGate && !room.Practice)
                        PvpGateEffects.OnGate(player, Order().IndexOf(r) + 1, racers.Count, room.Team, racers.Where(x => x.Car && x.Car != player).Select(x => x.Car.gameObject));
                    if (passed && myGates.Count % track.GatesPerLap == 0)
                    {
                        r.Lap = myGates.Laps;
                        float lapTime = raceTime - r.LapStart;
                        r.LapStart = raceTime;
                        if (r.BestLap <= 0 || lapTime < r.BestLap) r.BestLap = lapTime;
                        if (r.Lap >= track.Laps) Finished(r);
                        else LapEffect(r.Lap + 1, track.Laps);
                    }
                    r.Index = next;
                    r.Progress = r.Lap * n + r.Index;
                    continue;
                }
                if (next > n * 0.4f && next < n * 0.6f) r.Half = true;
                // crossing the line: index wraps from the end back to the start
                if (r.Index > n * 0.8f && next < n * 0.2f && r.Half)
                {
                    r.Half = false;
                    r.Lap++;
                    float lapTime = raceTime - r.LapStart;
                    r.LapStart = raceTime;
                    if (r.BestLap <= 0 || lapTime < r.BestLap) r.BestLap = lapTime;
                    if (r.Lap >= track.Laps) Finished(r);
                    else if (r.Car == player) LapEffect(r.Lap + 1, track.Laps);
                }
                else if (r.Index < n * 0.2f && next > n * 0.8f && r.Lap > 0) { r.Lap--; r.Half = true; } // went backwards over the line
                r.Index = next;
                r.Progress = r.Lap * n + r.Index;
            }
            WrongWay();
            UpdateHud();
            bool allDone = racers.All(r => r.Done || !r.Car);
            if (allDone || (endAt > 0 && raceTime >= endAt) || raceTime > 60f * 12f) EndRace();
        }

        void StartCountdown()
        {
            phase = Phase.Countdown; phaseTime = 0f;
            // the countdown: back to the driving camera (a spectator's views take over)
            if (rig) rig.Release();
            if (spectator && rig) { var d = Drivers(); rig.BeginWatch(d.Count > 0 ? d[0].Car : null, track); }
            // T-6: UI effect 18 pvp_countdownstart (+ SID_PVP_RACE_START)
            countdownFx = countdownLength >= CountdownSeconds && GameEffects.Screen("pvp_countdownstart");
            if (GameEffects.Sounds) AudioLibrary.PlaySfx("sfx/SID_PVP_RACE_START");
        }

        void Go()
        {
            phase = Phase.Racing; phaseTime = 0f;
            bigText.text = countdownFx || spectator ? "" : "GO!";
            if (!spectator) player.ExternalControl = false;
            foreach (var r in racers) { r.LapStart = 0; if (r.Ai) r.Ai.Hold = false; }
            // mission-battle objects along the line (GM "MissionBattle On (UFO / Ring / Rock)", PVP.md 15.1)
            if (room.Mission > 0) PvpMissionBattle.Begin(track, room.Mission, room.Id, spectator ? null : player);
            // the race is recorded from GO for its replay (0x73e1e0 records the client's race)
            var owned = PlayerProfile.Current?.Car;
            recorder = PvpReplay.Recorder.Begin(gameObject, track, room.Title, room.Team, track.Laps, () => racers.Select(r =>
                (r.Name, r.Member?.Team ?? 0, r.Car == player && !spectator ? (owned?.VehicleIndex ?? -1) : (r.Member?.Car?.Index ?? -1),
                 r.Car == player && !spectator ? (owned?.Color ?? 0) : (r.Member?.Slot ?? 0) % 4, r.Ai != null, r.Car ? r.Car.transform : null)));
        }

        /// <summary>Lap change within the race (0x72f3c0): laps 2..total-1 play pvp_lap2..7, the last one pvp_finallap.</summary>
        internal static void LapEffect(int lapNow, int laps)
        {
            if (lapNow >= laps) { GameEffects.Screen("pvp_finallap"); if (GameEffects.Sounds) AudioLibrary.PlaySfx("sfx/SID_PVP_RACE_FINAL"); }
            else if (lapNow >= 2 && lapNow <= 7) { GameEffects.Screen("pvp_lap" + lapNow); if (GameEffects.Sounds) AudioLibrary.PlaySfx("sfx/SID_PVP_RACE_LAPS"); }
        }

        void Finished(Racer r)
        {
            r.Done = true; r.FinishTime = raceTime;
            r.Place = racers.Count(x => x.Done && x != r) + 1;
            if (r.Ai) r.Ai.Hold = true;
            if (endAt < 0f)
            {
                // the first finisher starts the ten count (0x7236a0: state 9 + pvp_tencount)
                endAt = raceTime + TenCount;
                if (racers.Any(x => !x.Done && x.Car == player)) { GameEffects.Screen("pvp_tencount"); if (GameEffects.Sounds) AudioLibrary.PlaySfx("sfx/SID_PVP_RACE_COUNT"); }
            }
            if (r.Car == player)
            {
                var p = PlayerProfile.Current;
                bool record = p != null && r.BestLap > 0 && (p.BestLapSeconds <= 0 || r.BestLap < p.BestLapSeconds);
                GameEffects.RaceFinished(r.Place == 1, record, r.Place);   // 0x723fe0
                bigText.text = r.Place == 1 ? "WIN!" : Pvp.Ordinal(r.Place);
                phaseTime = 0f;
                if (rig) rig.BeginGoalIn(track, 2.5f);
            }
            else
            {
                hud?.GoalIn(r.Place, r.Name);
                // the "pvpgoalin" camera (0x6bf490) for the drivers who are done watching, and for spectators
                bool watching = spectator || (Me != null && Me.Done);
                if (watching && rig && r.Place <= 3) rig.BeginGoalIn(track, 2f);
            }
        }

        void WrongWay()
        {
            var me = Me;
            if (me == null || me.Done || !player) return;
            var rb = player.GetComponent<Rigidbody>();
            var v = rb ? rb.linearVelocity : Vector3.zero; v.y = 0;
            int n = track.Line.Count;
            var course = track.Line[(me.Index + 3) % n] - track.Line[me.Index]; course.y = 0;
            bool against = v.magnitude > 3f && course.sqrMagnitude > 0.01f && Vector3.Dot(v.normalized, course.normalized) <= -0.5f;
            wrongWay = against ? wrongWay + Time.deltaTime : 0f;
            if (wrongWay > WrongWaySeconds)
            {
                wrongWay = 0f;
                GameEffects.Screen("pvp_wrongway");
                if (GameEffects.Sounds) AudioLibrary.PlaySfx("sfx/SID_PVP_RACE_WARNING");
            }
        }

        List<Racer> Order() => racers.OrderByDescending(r => r.Done ? 1e9f - r.FinishTime : r.Progress).ToList();

        void UpdateHud()
        {
            var order = Order();
            if (minimap != null && minimap.IsOpen)
            {
                var me = Me;
                minimap.Draw(me != null ? player.transform : null, room.Team && me != null ? me.Member?.Team ?? 0 : -1,
                    racers.Where(r => r != me && r.Car).Select(r => (r.Car.transform, room.Team ? r.Member?.Team ?? 0 : -1)).ToList());
            }
            if (hud == null || Me == null) return;
            var mine = Me;
            int place = order.IndexOf(mine) + 1;
            hud.Set(place, racers.Count, Mathf.Min(mine.Lap + 1, track.Laps), track.Laps, raceTime, mine.BestLap);
            int n = track.Line.Count;
            hud.SetOrder(order.Select(r => (r.Name, r.Done ? 100 : Mathf.RoundToInt(100f * Mathf.Clamp01(r.Progress / Mathf.Max(1f, (float)n * track.Laps))), r.Car == player)).ToList());
        }

        int Nearest(Vector3 p, int from, int n) => NearestOnLine(track.Line, p, from);

        internal static int NearestOnLine(List<Vector3> line, Vector3 p, int from)
        {
            int n = line.Count;
            // far from the search window (respawn, crash, shortcut): search the whole line once
            if ((line[((from % n) + n) % n] - p).sqrMagnitude > 80f * 80f)
            {
                int bestAll = 0; float bestAllD = float.MaxValue;
                for (int i = 0; i < n; i++) { float d = (line[i] - p).sqrMagnitude; if (d < bestAllD) { bestAllD = d; bestAll = i; } }
                return bestAll;
            }
            int best = from; float bestD = float.MaxValue;
            for (int k = -12; k <= 40; k++)
            {
                int i = ((from + k) % n + n) % n;
                float d = (line[i] - p).sqrMagnitude;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        /// <summary>Metres a driver still had to go (the result rows' "DNF - %4dm").</summary>
        float Remaining(Racer r)
        {
            if (track.Cum.Count != track.Line.Count) return 0f;
            float lapLen = track.Length;
            float driven = r.Lap * lapLen + track.Cum[Mathf.Clamp(r.Index, 0, track.Cum.Count - 1)];
            return Mathf.Max(0f, track.Laps * lapLen - driven);
        }

        /// <summary>Simulated result for this race (tests): every driver finishes in grid order, or as given.</summary>
        internal void ForceFinish(int playerPlace)
        {
            if (phase == Phase.Finished) return;
            if (phase != Phase.Racing) Go();
            raceTime = Mathf.Max(raceTime, track.Length * track.Laps / 40f);
            var others = racers.Where(r => r.Car != player || spectator).ToList();
            int place = 1;
            foreach (var r in others)
            {
                if (!spectator && place == playerPlace) { Finish(racers[0], place++); }
                Finish(r, place++);
            }
            if (!spectator && !racers[0].Done) Finish(racers[0], place);
            EndRace();

            void Finish(Racer r, int pl)
            {
                r.Done = true; r.Place = pl; r.Lap = track.Laps;
                r.FinishTime = raceTime + pl * 1.5f;
                r.BestLap = r.FinishTime / track.Laps;
            }
        }

        /// <summary>Tests: the gate counter of the player.</summary>
        public GateTracker Gates => myGates;

        void EndRace()
        {
            if (phase == Phase.Finished) return;
            phase = Phase.Finished;
            foreach (var r in racers) if (r.Ai) r.Ai.Hold = true;
            if (player && !spectator) player.ExternalControl = false;
            var me = Me;
            // 0x729cc0: pvp_retire when I did not finish, else pvp_raceover
            bool fin = me == null || me.Done;
            GameEffects.Screen(fin ? "pvp_raceover" : "pvp_retire");
            if (GameEffects.Sounds) AudioLibrary.PlaySfx(fin ? "sfx/SID_PVP_RACE_OVER" : "sfx/SID_PVP_RACE_RETIRE");
            var result = BuildResult();
            if (recorder) recorder.Finish(result.Entries, room.IsChannelBattle && !spectator, 0, room.CrewA, room.CrewB);
            var mine = result.Mine;
            if (mine != null) QuestTracker.Notify(mine.Place == 1 ? QuestEvent.RaceWon : QuestEvent.RaceLost);
            PayOut(result);
            var host = GameFlow.Instance;
            if (host) host.StartCoroutine(EndAfter(2.5f)); else Cleanup();
            var cb = done;
            void ShowResults() => PvpResults.Show(result, () => cb?.Invoke(result));
            // the reward pick (4310000, 0x72ea60): EXP arenas, not practice / wager rooms, not for spectators
            if (result.Pick) PvpRewardPick.Open(room.Arena, racers.Where(r => r.Ai).Select(r => r.Name), ShowResults);
            else ShowResults();
        }

        PvpRaceResult BuildResult()
        {
            int field = racers.Count;
            var res = new PvpRaceResult
            {
                Track = track, TrackName = Pvp.TrackName(track), Team = room.Team, BestLap = Me?.BestLap ?? 0f, Wager = room.Wager, Arena = room.Arena,
                Spectator = spectator,
                Pick = !spectator && !room.Practice && room.Wager == 0 && room.Arena >= 0 && room.Arena <= 4 && field >= 2 && done != null,
            };
            var finished = racers.Where(r => r.Done).OrderBy(r => r.FinishTime).ToList();
            var dnf = racers.Where(r => !r.Done).OrderByDescending(r => r.Progress).ToList();
            int place = 1;
            foreach (var r in finished) Add(r, place++);
            foreach (var r in dnf) Add(r, 0);
            if (room.Team)
            {
                // team points (remake rule shared with the server): place p of n scores n - p + 1, DNF 0
                foreach (var e in res.Entries) { e.Points = e.Place > 0 ? field - e.Place + 1 : 0; if (e.Team == 0) res.TeamA += e.Points; else res.TeamB += e.Points; }
                res.Winner = res.TeamA > res.TeamB ? 0 : res.TeamB > res.TeamA ? 1 : 2;
            }
            bool ladder = !spectator && Pvp.LadderCounts(room, field) && racers.Count(r => r.Car != player) > 0;
            foreach (var e in res.Entries) { e.LadderShown = ladder; e.Ladder = ladder ? Pvp.LadderDelta(e.Place, field) : 0; }
            return res;

            void Add(Racer r, int pl)
            {
                res.Entries.Add(new PvpResultEntry
                {
                    Name = r.Name, Level = r.Member?.Level ?? 1, Place = pl, Time = r.Done ? r.FinishTime : 0f, Me = !spectator && r.Car == player,
                    Ai = r.Ai, Team = r.Member?.Team ?? 0, DnfMeters = r.Done ? 0f : Remaining(r),
                });
            }
        }

        /// <summary>
        /// Offline rewards through Progress.Reward("race") (the server's solo-race claim online): the place's reward times the
        /// arena's EXP factor; team mode pays the winning team as 1st and the other as 3rd. Ladder points go to the profile.
        /// A wager room settles the pot (every driver's fee, 10% tax) by the shared rule (PvpWager.Payouts).
        /// </summary>
        void PayOut(PvpRaceResult res)
        {
            var p = PlayerProfile.Current;
            var mine = res.Mine;
            if (p == null || mine == null || racers.Count < 2) return;
            if (res.Wager > 0)
            {
                int fee = Pvp.WagerFee(res.Wager);
                var pay = PvpWager.Payouts(res.Entries.Select(e => (e.Name, e.Place, e.Team)).ToList(), fee, res.Team, res.Winner);
                foreach (var e in res.Entries) e.WagerDelta = pay.TryGetValue(e.Name, out var v) ? v - fee : -fee;
                p.Mito = Math.Max(0, p.Mito + mine.WagerDelta);
                foreach (var e in res.Entries) e.WagerMito = e.Me ? p.Mito : 0;
                p.Save();
                PvpWager.ChatLines(res);
            }
            int paid = res.Team ? (mine.Team == res.Winner || res.Winner == 2 ? 1 : 3) : mine.Place;
            if (paid <= 0) paid = racers.Count;   // DNF: last place
            if (mine.LadderShown)
            {
                p.LadderPoints = Mathf.Max(0, p.LadderPoints + mine.Ladder);
                p.PvpRaces++;
                if (mine.Place == 1) p.PvpWins++;
            }
            float bestLap = racers[0].BestLap;
            var data = new Dictionary<string, object> { ["place"] = Mathf.Clamp(paid, 1, 8), ["bestLap"] = (double)bestLap, ["city"] = track.City, ["arena"] = room.Arena };
            int arena = room.Arena;
            Progress.Reward("race", data, () =>
            {
                var (exp, mito) = Pvp.Reward(p.Level, paid, arena);
                int before = p.Level;
                p.AddExp(exp); p.Mito += mito;
                p.RacesEntered++;
                if (paid == 1) p.RacesWon++;
                if (bestLap > 0 && (p.BestLapSeconds <= 0 || bestLap < p.BestLapSeconds)) p.BestLapSeconds = bestLap;
                return new Progress.Result { Ok = true, Exp = exp, Mito = mito, LevelUps = p.Level - before };
            }, r =>
            {
                if (!r.Ok) return;
                mine.Exp = r.Exp; mine.Mito = r.Mito; mine.LevelUp = r.LevelUps > 0;
                mine.Bonus = Pvp.BonusFlags(p); mine.Tree = Pvp.TreeStage();
                PvpResults.Refresh();
            });
        }

        IEnumerator EndAfter(float seconds)
        {
            yield return new WaitForSeconds(seconds);
            if (this) Cleanup();
        }

        public void Abort() { phase = Phase.Finished; Cleanup(); }

        PvpPathTraffic pathTraffic;

        void Cleanup()
        {
            if (pathTraffic) Destroy(pathTraffic.gameObject);
            BattleQuickSlots.InLowerHalf = null;
            if (Active == this) Active = null;
            foreach (var r in racers) if (r.Ai && r.Car) Destroy(r.Car.gameObject);
            foreach (var g in gates) if (g) Destroy(g);
            if (hudRoot) Destroy(hudRoot);
            hud?.Close(); hud = null;
            minimap?.Close(); minimap = null;
            if (spectator) PvpSpectate.EndViewing();
            if (rig) rig.Release();
            PvpMissionBattle.End();
            if (player)
            {
                PvpGateEffects.Clear(player);
                player.ExternalControl = false; player.RespawnOverride = null;
                if ((realMatch || mission) && GameFlow.Instance) GameFlow.Instance.ApplyCarStats();
                RealMatchSkin.Remove(player.gameObject);
            }
            realMatch = false; mission = false;
            var s = CitySession.Current;
            // give traffic back unless the player changed the density during the race
            if (s && s.Traffic && prevTraffic > 0 && s.Traffic.MaxCars < prevTraffic) s.Traffic.MaxCars = prevTraffic;
            if (GameFlow.Instance) AudioLibrary.PlayMusic(GameFlow.Instance, CityMusic.For(GameFlow.Instance.City));
            Destroy(gameObject);
        }

        void OnDestroy()
        {
            if (Active == this) Active = null;
            hud?.Close();
            minimap?.Close();
            if (rig) rig.Release();
            if ((realMatch || mission) && player && GameFlow.Instance) GameFlow.Instance.ApplyCarStats();
            if (realMatch && player) RealMatchSkin.Remove(player.gameObject);
            PvpMissionBattle.End();
        }
    }

    /// <summary>Drives a DriftCar along a racing line: pure-pursuit steering plus braking for corners ahead.</summary>
    public sealed class AiDriver : MonoBehaviour
    {
        public List<Vector3> Line;
        public float Skill = 0.9f;
        public bool Hold = true;
        DriftCar car;
        int index;
        float stuckTime, driftTap, driftCooldown;

        void Start() { car = GetComponent<DriftCar>(); index = 0; }

        void FixedUpdate()
        {
            if (!car || Line == null || Line.Count < 3) return;
            if (Hold) { car.ApplyControls(0, 0, false, Time.fixedDeltaTime); return; }
            int n = Line.Count;
            var pos = transform.position;
            // advance the tracked index to the nearest point ahead
            for (int k = 0; k < 20; k++)
            {
                int a = index % n, b = (index + 1) % n;
                if ((Line[b] - pos).sqrMagnitude < (Line[a] - pos).sqrMagnitude) index++; else break;
            }
            float speed = car.SpeedKmh / 3.6f;
            float lookAhead = Mathf.Clamp(speed * 0.9f, 8f, 40f);
            Vector3 target = Line[index % n];
            float acc = 0;
            for (int k = 1; k < 60 && acc < lookAhead; k++)
            {
                var a = Line[(index + k - 1) % n]; var b = Line[(index + k) % n];
                acc += Vector3.Distance(a, b);
                target = b;
            }
            var local = transform.InverseTransformPoint(target);
            float steer = Mathf.Clamp(Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg / car.MaxSteer, -1f, 1f);
            // corner speed from the bend over the next stretch
            var d0 = (Line[(index + 5) % n] - Line[index % n]).normalized;
            var d1 = (Line[(index + 25) % n] - Line[(index + 5) % n]).normalized;
            float bend = Vector3.Angle(d0, d1);
            float targetKmh = Mathf.Lerp(car.TopSpeedKmh * Skill, 55f, Mathf.InverseLerp(10f, 90f, bend));
            float throttle = car.SpeedKmh < targetKmh ? 1f : (car.SpeedKmh > targetKmh + 15f ? -1f : 0.2f);
            bool boost = bend < 12f && car.BoostGauge > 0.4f && Skill > 0.88f;
            // tap the drift button at the corner entry, as a player does: holding it keeps the car in the
            // high-drag held-drift state, while steering into the slide after the tap holds the angle
            if (bend > 70f && car.SpeedKmh > 70f && car.DriftState == 0 && driftTap <= 0f && driftCooldown <= 0f) { driftTap = 0.15f; driftCooldown = 1.2f; }
            bool handbrake = driftTap > 0f;
            driftTap -= Time.fixedDeltaTime;
            driftCooldown -= Time.fixedDeltaTime;
            car.ApplyControls(throttle, steer, handbrake, Time.fixedDeltaTime, boost);

            // recover if stuck against a wall
            stuckTime = car.SpeedKmh < 5f ? stuckTime + Time.fixedDeltaTime : 0f;
            if (stuckTime > 4f)
            {
                stuckTime = 0;
                var p = Line[index % n] + Vector3.up * 1.5f;
                var dir = Line[(index + 3) % n] - Line[index % n]; dir.y = 0;
                var rb = car.GetComponent<Rigidbody>();
                rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero;
                transform.SetPositionAndRotation(p, Quaternion.LookRotation(dir.sqrMagnitude > 0.01f ? dir.normalized : transform.forward));
            }
        }
    }
}
