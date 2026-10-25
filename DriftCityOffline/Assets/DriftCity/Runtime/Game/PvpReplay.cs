using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>One driver of a recorded race: frames of (t, x, y, z, yaw) every <see cref="PvpReplay.Interval"/> seconds.</summary>
    public sealed class ReplayCar
    {
        public string Name = "";
        public int Team, Car = -1, Paint, Place;
        public float Time;
        public bool Ai;
        /// <summary>Flat frames: t, x, y, z, yaw (Unity axes, degrees) repeated.</summary>
        public readonly List<float> F = new List<float>();
        public int Frames => F.Count / 5;

        public void Add(float t, Vector3 p, float yaw)
        {
            F.Add(Round(t, 100f)); F.Add(Round(p.x, 10f)); F.Add(Round(p.y, 10f)); F.Add(Round(p.z, 10f)); F.Add(Round(yaw, 1f));
        }

        static float Round(float v, float k) => Mathf.Round(v * k) / k;

        /// <summary>The pose at time <paramref name="t"/>, interpolated between the two frames around it.</summary>
        public bool Sample(float t, out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero; rot = Quaternion.identity;
            int n = Frames;
            if (n == 0) return false;
            if (t <= F[0] || n == 1) { pos = P(0); rot = Quaternion.Euler(0f, F[4], 0f); return true; }
            int lo = 0, hi = n - 1;
            if (t >= F[hi * 5]) { pos = P(hi); rot = Quaternion.Euler(0f, F[hi * 5 + 4], 0f); return true; }
            while (hi - lo > 1) { int mid = (lo + hi) / 2; if (F[mid * 5] <= t) lo = mid; else hi = mid; }
            float t0 = F[lo * 5], t1 = F[hi * 5], k = t1 > t0 ? (t - t0) / (t1 - t0) : 0f;
            pos = Vector3.Lerp(P(lo), P(hi), k);
            rot = Quaternion.Slerp(Quaternion.Euler(0f, F[lo * 5 + 4], 0f), Quaternion.Euler(0f, F[hi * 5 + 4], 0f), k);
            return true;
        }

        Vector3 P(int i) => new Vector3(F[i * 5 + 1], F[i * 5 + 2], F[i * 5 + 3]);

        /// <summary>Velocity at <paramref name="t"/> from the neighbouring frames (the ghost battle starts with it).</summary>
        public Vector3 Velocity(float t)
        {
            if (!Sample(t - 0.1f, out var a, out _) || !Sample(t + 0.1f, out var b, out _)) return Vector3.zero;
            return (b - a) / 0.2f;
        }
    }

    /// <summary>A recorded Battle Zone race (the remake's play_record.r0).</summary>
    public sealed class ReplayRecording
    {
        public string City = "", Track = "", Title = "", Owner = "", Challenger = "", Label = "";
        public int Laps = 1, Round, Channel = -1, Match;
        public bool Team;
        public float Duration;
        public long At;
        public readonly List<ReplayCar> Cars = new List<ReplayCar>();

        public string TrackKey => $"{City}/{(Track ?? "").ToLowerInvariant()}";

        public RaceTrack FindTrack() =>
            string.IsNullOrEmpty(City) ? null : RaceMission.TracksFor(City).FirstOrDefault(t => Pvp.TrackKey(t) == TrackKey);

        public Dictionary<string, object> ToJson()
        {
            var cars = new List<object>();
            foreach (var c in Cars)
                cars.Add(new Dictionary<string, object>
                {
                    ["name"] = c.Name, ["team"] = (double)c.Team, ["car"] = (double)c.Car, ["paint"] = (double)c.Paint, ["place"] = (double)c.Place,
                    ["time"] = (double)c.Time, ["ai"] = c.Ai, ["f"] = c.F.Select(x => (object)(double)x).ToList(),
                });
            return new Dictionary<string, object>
            {
                ["city"] = City, ["track"] = Track, ["title"] = Title, ["laps"] = (double)Laps, ["team"] = Team, ["round"] = (double)Round,
                ["duration"] = (double)Duration, ["owner"] = Owner, ["challenger"] = Challenger, ["channel"] = (double)Channel, ["at"] = (double)At,
                ["cars"] = cars,
            };
        }

        public static ReplayRecording FromJson(Dictionary<string, object> d)
        {
            if (d == null) return null;
            var r = new ReplayRecording
            {
                City = d.S("city", ""), Track = d.S("track", ""), Title = d.S("title", ""), Laps = Mathf.Max(1, d.I("laps", 1)), Team = d.B("team"),
                Round = d.I("round"), Duration = (float)d.N("duration"), Owner = d.S("owner", ""), Challenger = d.S("challenger", ""),
                Channel = d.I("channel", -1), At = (long)d.N("at"),
            };
            foreach (var o in (d.L("cars") ?? new List<object>()).OfType<Dictionary<string, object>>())
            {
                var c = new ReplayCar { Name = o.S("name", ""), Team = o.I("team"), Car = o.I("car", -1), Paint = o.I("paint"), Place = o.I("place"), Time = (float)o.N("time"), Ai = o.B("ai") };
                foreach (var x in o.L("f") ?? new List<object>()) c.F.Add(x is double v ? (float)v : 0f);
                if (c.F.Count % 5 != 0) c.F.RemoveRange(c.F.Count - c.F.Count % 5, c.F.Count % 5);
                r.Cars.Add(c);
            }
            if (r.Duration <= 0f) r.Duration = r.Cars.Select(c => c.Frames > 0 ? c.F[(c.Frames - 1) * 5] : c.Time).DefaultIfEmpty(0f).Max();
            return r;
        }

        /// <summary>The recording's result board (finishers by place, then the rest).</summary>
        public PvpRaceResult ToResult(RaceTrack track)
        {
            var res = new PvpRaceResult { Track = track, TrackName = track != null ? Pvp.TrackName(track) : Track, Team = Team, Spectator = true };
            foreach (var c in Cars.OrderBy(c => c.Place > 0 ? c.Place : 99))
                res.Entries.Add(new PvpResultEntry { Name = c.Name, Place = c.Place, Time = c.Time, Team = c.Team, Ai = c.Ai });
            if (Team)
            {
                int field = Cars.Count;
                foreach (var e in res.Entries) { e.Points = e.Place > 0 ? field - e.Place + 1 : 0; if (e.Team == 0) res.TeamA += e.Points; else res.TeamB += e.Points; }
                res.Winner = res.TeamA > res.TeamB ? 0 : res.TeamB > res.TeamA ? 1 : 2;
            }
            return res;
        }
    }

    /// <summary>
    /// Race replays (DriftCity_RE/PVP.md 14.2). The client records the race it is in as a packet stream: 0x73e1e0 opens
    /// play_record.r0 and writes the room info 0x26d, the room reply 0x1be with every racer and each racer's car record
    /// 0x329, then the race's packets go into a 256 KB buffer (0x73d5e0) that 0x73d530 flushes and closes at the end
    /// ("replay size=%d"). A Channel Battle replay bought from the replay list 3410000 (0x73d7b0: price 1000 / 500 / 0 by
    /// channel, free with ch_replaypass, confirm 11256 / 11260) arrives in chunks (0x742af0: {total, offset, length, data},
    /// "ReplayData(%d,%d,%d)"); once complete 0x742f50 starts the replay stage ("replay", text 10057 "Replaying."), which
    /// reads the packets back on their recorded clock (0x73d9c0) and plays the race as a spectator would see it.
    /// The remake records poses instead of packets: every driver's position and heading 5 times a second
    /// (<see cref="Recorder"/>), kept as the last race (<see cref="Last"/>, saved as play_record.r0.json), as the offline
    /// Channel Battle rounds (<see cref="Local"/>) and, online, on the server for every crew round (race.js). The player
    /// draws the cars on the track, opens VIEWING 1960000 with its six views, shows the round (1420000) and the result
    /// board at the end; the Ghost Battle can be started from it (PvpGhost).
    /// </summary>
    public static class PvpReplay
    {
        public const float Interval = 0.2f;
        public const int MaxLocal = 40;
        /// <summary>The last race recorded on this client (the client's play_record.r0).</summary>
        public static ReplayRecording Last { get; private set; }
        /// <summary>Offline Channel Battle rounds, newest first (the offline replay list).</summary>
        public static readonly List<ReplayRecording> Local = new List<ReplayRecording>();
        public static ReplayPlayer Current { get; private set; }
        public static bool Playing => Current;
        /// <summary>Tests: no file is written when false.</summary>
        public static bool SaveFiles = true;

        public static string SavePath => Path.Combine(Application.persistentDataPath, "play_record.r0.json");

        internal static void SetLast(ReplayRecording r, bool crewRound)
        {
            if (r == null) return;
            Last = r;
            if (crewRound && !OnlineSession.Active)
            {
                Local.Insert(0, r);
                if (Local.Count > MaxLocal) Local.RemoveRange(MaxLocal, Local.Count - MaxLocal);
            }
            if (!SaveFiles) return;
            try { File.WriteAllText(SavePath, MiniJson.Write(r.ToJson())); } catch (Exception e) { Debug.LogWarning("replay not saved: " + e.Message); }
        }

        /// <summary>The saved play_record.r0.json (null when there is none).</summary>
        public static ReplayRecording LoadSaved()
        {
            try { return File.Exists(SavePath) ? ReplayRecording.FromJson(MiniJson.ParseObject(File.ReadAllText(SavePath))) : null; }
            catch { return null; }
        }

        /// <summary>Plays a recording in the current city. False (nothing started) when its track is not in this city or it has
        /// no frames; <paramref name="why"/> says which.</summary>
        public static bool Play(ReplayRecording rec, Action done, out string why, bool ghostTicket = false)
        {
            why = null;
            if (rec == null || rec.Cars.Count == 0 || rec.Cars.All(c => c.Frames == 0)) { why = "This replay has no recorded race."; return false; }
            var session = CitySession.Current;
            if (!session || session.City != rec.City)
            {
                why = $"This race was run in {Social.SocialUi.CityName(rec.City)}. Travel there to watch its replay.";
                return false;
            }
            var track = rec.FindTrack();
            if (track == null) { why = "This replay's track isn't available here."; return false; }
            if (Current) Current.Stop(false);
            var go = new GameObject("PvP Replay");
            Current = go.AddComponent<ReplayPlayer>();
            Current.Begin(rec, track, done, ghostTicket);
            return true;
        }

        internal static void Ended(ReplayPlayer p) { if (Current == p) Current = null; }

        // ---------------- recording ----------------
        /// <summary>
        /// Samples the race's cars every <see cref="Interval"/> s from the start (GO) until <see cref="Finish"/>. The source
        /// gives each driver's name, team, car (vehiclelist index), paint slot, AI flag and transform (null = not drawn now).
        /// </summary>
        public sealed class Recorder : MonoBehaviour
        {
            public Func<IEnumerable<(string name, int team, int car, int paint, bool ai, Transform t)>> Source;
            public ReplayRecording Recording { get; private set; }
            readonly Dictionary<string, ReplayCar> cars = new Dictionary<string, ReplayCar>();
            float clock, next;
            bool stopped;

            public static Recorder Begin(GameObject host, RaceTrack track, string title, bool team, int laps,
                Func<IEnumerable<(string name, int team, int car, int paint, bool ai, Transform t)>> source)
            {
                if (!host || track == null) return null;
                var r = host.AddComponent<Recorder>();
                r.Source = source;
                r.Recording = new ReplayRecording { City = track.City, Track = track.Track, Title = title ?? "", Team = team, Laps = Mathf.Max(1, laps), At = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                r.Sample();
                return r;
            }

            void Update()
            {
                if (stopped) return;
                clock += Time.deltaTime;
                if (clock >= next) Sample();
                // a cap like the client's 256 KB buffer: 15 minutes
                if (clock > 900f) stopped = true;
            }

            void Sample()
            {
                next = clock + Interval;
                if (Source == null) return;
                foreach (var (name, team, car, paint, ai, t) in Source())
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!cars.TryGetValue(name, out var c)) { cars[name] = c = new ReplayCar { Name = name, Team = team, Car = car, Paint = paint, Ai = ai }; Recording.Cars.Add(c); }
                    if (t) c.Add(clock, t.position, t.eulerAngles.y);
                }
            }

            /// <summary>Ends the recording with the race's results (places and times by name) and keeps it as the last race.</summary>
            public ReplayRecording Finish(IEnumerable<PvpResultEntry> results, bool crewRound, int round = 0, string owner = "", string challenger = "")
            {
                if (!stopped) Sample();
                stopped = true;
                var rec = Recording;
                rec.Duration = clock;
                rec.Round = round; rec.Owner = owner ?? ""; rec.Challenger = challenger ?? "";
                if (results != null)
                    foreach (var e in results)
                    {
                        if (!cars.TryGetValue(e.Name ?? "", out var c)) { cars[e.Name ?? ""] = c = new ReplayCar { Name = e.Name ?? "", Team = e.Team, Ai = e.Ai }; rec.Cars.Add(c); }
                        c.Place = e.Place; c.Time = e.Time;
                    }
                SetLast(rec, crewRound);
                return rec;
            }
        }
    }

    /// <summary>Draws a recording: one visual car per driver, VIEWING 1960000, the round, then the result board.</summary>
    public sealed class ReplayPlayer : MonoBehaviour
    {
        public ReplayRecording Recording { get; private set; }
        public RaceTrack Track { get; private set; }
        public float Clock { get; private set; }
        public float Speed = 1f;
        readonly List<(ReplayCar rec, GameObject go, int lap, int index, bool half)> cars = new List<(ReplayCar, GameObject, int, int, bool)>();
        Action done;
        PvpRoom room;
        DriftCar player;
        bool finished, heldPlayer;

        public IEnumerable<Transform> CarTransforms => cars.Where(c => c.go).Select(c => c.go.transform);

        internal void Begin(ReplayRecording rec, RaceTrack track, Action onDone, bool ghostTicket)
        {
            Recording = rec; Track = track; done = onDone;
            player = GameFlow.Instance ? GameFlow.Instance.PlayerCar : null;
            if (player && !player.ExternalControl) { player.ExternalControl = true; heldPlayer = true; }
            room = new PvpRoom { Title = string.IsNullOrEmpty(rec.Title) ? Pvp.Text("10057") : rec.Title, Team = rec.Team, Max = Mathf.Max(rec.Cars.Count, 1), Spectating = true };
            for (int i = 0; i < rec.Cars.Count; i++) room.Members.Add(new PvpMember { Id = 1000 + i, Name = rec.Cars[i].Name, Slot = rec.Team ? (rec.Cars[i].Team == 0 ? i * 2 % 16 : (i * 2 + 1) % 16) : i, Ai = rec.Cars[i].Ai });
            foreach (var c in rec.Cars)
            {
                var v = c.Car >= 0 ? GameTables.Car(c.Car) : GameTables.PlayerCars.FirstOrDefault();
                GameObject go = null;
                if (v != null)
                {
                    c.Sample(0f, out var p, out var r);
                    go = CarFactory.BuildVisual(v, Mathf.Clamp(c.Paint, 0, 3), p, r);
                    go.name = "Replay " + c.Name;
                    foreach (var col in go.GetComponentsInChildren<Collider>(true)) col.enabled = false;
                    var rb = go.GetComponent<Rigidbody>();
                    if (rb) rb.isKinematic = true;
                    go.SetActive(c.Frames > 0 || c.Time > 0f);
                }
                cars.Add((c, go, 0, 0, false));
            }
            PvpSpectate.BeginViewing(room, track, Drivers, TeamPoints, () => Stop(true), () => Clock > 0f && Clock < Recording.Duration);
            PvpGhost.BeginRace(ghostTicket);
            if (PvpSpectate.ViewingView) PvpSpectate.ViewingView.SetText("ID_PVP_VIEWING_ROOMNAME", room.Title + "  " + Pvp.Text("10057"));
            PvpHud.ShowRound(rec.Round);
            ChatBox.System(Pvp.Text("10057"));
            AudioLibrary.PlayMusic(this, "bgm/DriftCity_1stpvp_01.mp3");
            Pose(0f);
        }

        void Update()
        {
            if (finished) return;
            Clock += Time.deltaTime * Speed;
            Pose(Clock);
            if (Clock >= Recording.Duration + 2f) Stop(true);
        }

        /// <summary>Moves every car to its recorded pose at <paramref name="t"/> (drivers without frames, the server's AI,
        /// run along the line at their average speed up to their finish).</summary>
        public void Pose(float t)
        {
            int n = Track.Line.Count;
            for (int i = 0; i < cars.Count; i++)
            {
                var (rec, go, lap, index, half) = cars[i];
                if (!go) continue;
                Vector3 pos; Quaternion rot;
                if (rec.Frames > 0) rec.Sample(t, out pos, out rot);
                else if (rec.Time > 0f)
                {
                    float total = Track.Length * Track.Laps, d = Mathf.Clamp01(t / rec.Time) * total;
                    float along = d % Mathf.Max(1f, Track.Length);
                    int k = Track.Cum.FindIndex(c => c >= along); if (k < 0) k = n - 1;
                    pos = RaceMission.Ground(Track.Line[k]) - Vector3.up * 0.3f;
                    var dir = Track.Line[(k + 2) % n] - Track.Line[k]; dir.y = 0;
                    rot = dir.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(dir.normalized, Vector3.up) : go.transform.rotation;
                }
                else continue;
                go.transform.SetPositionAndRotation(pos, rot);
                // laps from the line index, as the offline race counts AI drivers
                int next = RaceMission.NearestOnLine(Track.Line, pos, index);
                if (next > n * 0.4f && next < n * 0.6f) half = true;
                if (index > n * 0.8f && next < n * 0.2f && half) { half = false; lap++; }
                else if (index < n * 0.2f && next > n * 0.8f && lap > 0) { lap--; half = true; }
                cars[i] = (rec, go, lap, next, half);
            }
        }

        /// <summary>Drivers in race order for VIEWING (finished drivers by place once their time has passed).</summary>
        List<PvpSpectate.Driver> Drivers()
        {
            int n = Mathf.Max(1, Track.Line.Count);
            return cars.Where(c => c.go).Select(c =>
            {
                bool doneNow = c.rec.Place > 0 && c.rec.Time > 0f && Clock >= c.rec.Time;
                float prog = doneNow ? 1e6f - c.rec.Time : c.lap * n + c.index;
                return new PvpSpectate.Driver { Name = c.rec.Name, Car = c.go.transform, Progress = prog, Team = c.rec.Team, Done = doneNow, Place = doneNow ? c.rec.Place : 0,
                    Fraction = doneNow ? 1f : Mathf.Clamp01((c.lap * n + c.index) / Mathf.Max(1f, (float)n * Track.Laps)) };
            }).OrderByDescending(d => d.Progress).ToList();
        }

        (int a, int b) TeamPoints()
        {
            int field = cars.Count, a = 0, b = 0;
            foreach (var c in cars) if (c.rec.Place > 0 && c.rec.Time > 0f && Clock >= c.rec.Time) { int pts = field - c.rec.Place + 1; if (c.rec.Team == 0) a += pts; else b += pts; }
            return (a, b);
        }

        /// <summary>Ends the replay; <paramref name="showResults"/> opens the race's result board first.</summary>
        public void Stop(bool showResults)
        {
            if (finished) return;
            finished = true;
            PvpGhost.Exit(null, true);
            PvpSpectate.EndViewing();
            PvpHud.HideRound();
            foreach (var c in cars) if (c.go) Destroy(c.go);
            cars.Clear();
            if (heldPlayer && player) player.ExternalControl = false;
            if (GameFlow.Instance) AudioLibrary.PlayMusic(GameFlow.Instance, CityMusic.For(GameFlow.Instance.City));
            PvpReplay.Ended(this);
            var cb = done;
            if (showResults && UiManager.Instance) PvpResults.Show(Recording.ToResult(Track), () => cb?.Invoke());
            else cb?.Invoke();
            Destroy(gameObject);
        }

        void OnDestroy()
        {
            if (!finished)
            {
                foreach (var c in cars) if (c.go) Destroy(c.go);
                if (heldPlayer && player) player.ExternalControl = false;
            }
            PvpReplay.Ended(this);
        }
    }
}
