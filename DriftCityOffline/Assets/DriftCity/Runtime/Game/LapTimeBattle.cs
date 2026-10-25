using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.Game
{
    /// <summary>
    /// The lap-time battle (lobby LAPTIMEBATTLE, net feature 0x4 "LapTime"; DriftCity_RE/PVP.md "Lap-time battle"; lap
    /// manager game+0x174, state machine 0x71eae0): the city's lap-time track (pvptrackinfo CityType 4, 5 laps) is driven
    /// against the clock while the round's other drivers set their own records. A run starts 10 s after the start time
    /// (0x52a), the countdown effect at T-6 s; every gate is reported (0x52b), every lap (0x52c) updates the best list and
    /// pays the lap reward ("Exp +%d Mito +%d"); a personal record shows LAPTIMENOTICE 3450000 for 5 s. The round ends at
    /// the server's end time (0x52d): LAPTIMERET 3460000 shows the top three with their rewards and my row 1 s later for
    /// 7 s, then a new round starts. HUD LAPTIMEPLAY 3440000 (0x71c220): laps, running time, driver count, the leader, my
    /// rank, a window of the best list (4 rows show), my last 5 laps, the lap grade mark (POOR / BAD / GOOD / COOL / GREAT
    /// by the change from the previous lap). EXIT asks 10724 "Exit Lap Time?".
    /// Offline the round's rivals are AI drivers with simulated laps and the round is shorter.
    /// </summary>
    public sealed class LapTimeBattle : MonoBehaviour
    {
        public const int PlayDialog = 3440000, NoticeDialog = 3450000, ResultDialog = 3460000;
        public static float StartDelay = 10f, CountdownAt = 6f, OfflineRoundSeconds = 300f, NextRunDelay = 3f;

        public static LapTimeBattle Active { get; private set; }
        public static bool IsOpen => Active;

        public sealed class Rival { public string Name; public int Level; public float Best; public float Skill; public float NextAt; public bool Me; public readonly List<float> Laps = new List<float>(); }

        RaceTrack track;
        DriftCar player;
        CitySession session;
        bool online;
        enum State { Joining, Countdown, Driving, RunOver, Results }
        State state = State.Joining;
        GateTracker gates;
        float startAt, lapStart, roundEndsAt, noticeHide, markHide, resultsAt;
        int index;
        float wrongWay;
        readonly List<float> myLaps = new List<float>();
        float myBest;
        int myRank, lastRank;
        bool countdownFx;
        readonly Dictionary<string, Rival> field = new Dictionary<string, Rival>();
        NuiView play, notice, result;
        Func<(Vector3 pos, Quaternion rot)?> respawn;
        UiManager hooked;
        Text big;

        public RaceTrack Track => track;
        public int Laps => track?.Laps ?? 5;
        public int LapsDone => gates?.Laps ?? 0;
        public float Best => myBest;
        public int Rank => myRank;
        public IReadOnlyList<float> MyLaps => myLaps;
        public NuiView PlayView => play;
        public NuiView ResultView => result;
        public NuiView NoticeView => notice;
        public List<Rival> Ranking => field.Values.Where(r => r.Best > 0).OrderBy(r => r.Best).ToList();
        public int Drivers => field.Count;
        public bool Driving => state == State.Driving;

        /// <summary>The city's lap-time track (CityType 4), or null.</summary>
        public static RaceTrack TrackFor(string city) => RaceMission.TracksFor(city).FirstOrDefault(t => t.CityType == 4);

        // ---------------- enter / leave ----------------
        public static void Enter(CitySession s)
        {
            if (Active || s == null || !s.Car) return;
            var t = TrackFor(s.City);
            if (t == null) { PvpLobby.Unavailable(); return; }
            if (OnlineSession.Active)
            {
                OnlineSession.Instance.Net.Request("lap.enter", null, r =>
                {
                    if (!r.B("ok")) { UiPopup.Message(r.I("code") == 10745 ? Pvp.Text("10745") : r.S("error")); return; }
                    var lt = Begin(s, t, true);
                    if (!lt) return;
                    lt.roundEndsAt = Time.time + (float)r.N("endsIn", 600000) / 1000f;
                    lt.SetPlayers(r.L("players"));
                    lt.StartRun();
                });
                return;
            }
            var b = Begin(s, t, false);
            if (b) b.StartRun();
        }

        static LapTimeBattle Begin(CitySession s, RaceTrack t, bool online)
        {
            if (Active) return null;
            PvpLobby.Close();
            var go = new GameObject("Lap Time Battle");
            var lt = go.AddComponent<LapTimeBattle>();
            lt.session = s; lt.track = t; lt.player = s.Car; lt.online = online;
            Active = lt;
            lt.Setup();
            return lt;
        }

        void Setup()
        {
            gates = new GateTracker(track);
            var me = PlayerProfile.Current;
            field.Clear();
            field[Me] = new Rival { Name = Me, Level = me?.Level ?? 1, Me = true };
            if (!online)
            {
                roundEndsAt = Time.time + OfflineRoundSeconds;
                var room = new PvpRoom();
                int n = UnityEngine.Random.Range(5, 9);
                for (int i = 0; i < n; i++)
                {
                    var ai = Pvp.NewAi(room, me?.Level ?? 1);
                    room.Members.Add(ai);
                    field[ai.Name] = new Rival { Name = ai.Name, Level = ai.Level, Skill = ai.Skill, NextAt = Time.time + StartDelay + LapOf(ai.Skill) };
                }
            }
            respawn = RespawnPoint;
            player.RespawnOverride = respawn;
            var ui = UiManager.Instance;
            if (ui && hooked != ui)
            {
                hooked = ui;
                ui.On("ID_LAPTIMEPLAY_EXIT", _ => MissionUi.Confirm(Pvp.Text("10724"), () => { if (Active) Active.Leave(); }));
            }
            play = ui ? ui.Open(PlayDialog) : null;
            HideAll(play, "ID_LAPTIMEPLAY_EXIT");
            var rt = NuiView.NewRect("lap text", ui.Root);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(1024, 200);
            big = rt.gameObject.AddComponent<Text>();
            big.font = UiAssets.Font("Verdana 96", out _, out _); big.fontSize = 96; big.fontStyle = FontStyle.Bold; big.alignment = TextAnchor.MiddleCenter;
            big.color = new Color(1f, 0.85f, 0.2f); big.raycastTarget = false;
            rt.gameObject.AddComponent<Outline>().effectColor = Color.black;
            if (GameEffects.Sounds) AudioLibrary.PlaySfx("sfx/SID_PVP_ROOMENTER");
            AudioLibrary.PlayMusic(this, "bgm/DriftCity_1stpvp_01.mp3");
        }

        static string Me => PlayerProfile.Current?.Name ?? "Driver";

        /// <summary>An AI rival's lap: the line's length over a skill-scaled average speed (the tournament's 30 m/s rule).</summary>
        float LapOf(float skill) => track.Length / Mathf.Max(5f, 30f * skill * UnityEngine.Random.Range(0.95f, 1.05f));

        static void HideAll(NuiView v, params string[] keep)
        {
            if (!v) return;
            foreach (var c in v.GetComponentsInChildren<NuiControlView>(true))
            {
                var id = c.Data.IdName ?? "";
                c.gameObject.SetActive(keep.Contains(id));
            }
        }

        /// <summary>EXIT (3440001 -> 10724): back to the street.</summary>
        public void Leave()
        {
            if (online && OnlineSession.Active) OnlineSession.Instance.Net.Request("lap.leave", null, null);
            Cleanup();
        }

        void Cleanup()
        {
            if (Active == this) Active = null;
            var ui = UiManager.Instance;
            foreach (var v in new[] { play, notice, result }) if (v) { if (ui) ui.Close(v); else Destroy(v.gameObject); }
            play = notice = result = null;
            if (big) Destroy(big.gameObject);
            if (player) { if (player.RespawnOverride == respawn) player.RespawnOverride = null; player.ExternalControl = false; }
            if (GameFlow.Instance) AudioLibrary.PlayMusic(GameFlow.Instance, CityMusic.For(GameFlow.Instance.City));
            Destroy(gameObject);
        }

        void OnDestroy() { if (Active == this) Active = null; }

        /// <summary>Logout / travel.</summary>
        public static void Reset() { if (Active) Active.Cleanup(); }

        // ---------------- runs ----------------
        /// <summary>A run (0x529 -> 0x52a): the car on the start, the clock 10 s later, the countdown at T-6.</summary>
        void StartRun()
        {
            gates = new GateTracker(track);
            index = 0; wrongWay = 0f; countdownFx = false;
            var dir = PvpCameraRig.TrackForward(track);
            var pos = PvpGates.GroundNear(RaceMission.GridSlot(track, 0));
            RaceMission.Teleport(player, pos, Quaternion.LookRotation(dir, Vector3.up));
            if (OnlineSession.Active && OnlineSession.Instance) OnlineSession.Instance.MarkTeleport();
            player.ExternalControl = true;
            state = State.Countdown;
            startAt = Time.time + StartDelay;
            if (online)
            {
                OnlineSession.Instance.Net.Request("lap.start", null, r =>
                {
                    if (!r.B("ok")) return;
                    startAt = Time.time + (float)r.N("startIn", StartDelay * 1000) / 1000f;
                });
            }
        }

        void Update()
        {
            if (!player) { Cleanup(); return; }
            if (online && !OnlineSession.Active) { Cleanup(); return; }
            float now = Time.time;
            if (!online) Simulate(now);
            switch (state)
            {
                case State.Countdown:
                {
                    player.ExternalControl = true;
                    var rb = player.GetComponent<Rigidbody>();
                    if (rb) rb.linearVelocity = Vector3.Lerp(rb.linearVelocity, Vector3.zero, 0.5f);
                    float left = startAt - now;
                    if (left <= CountdownAt && !countdownFx)
                    {
                        countdownFx = true;
                        bool fx = GameEffects.Screen("pvp_countdownstart");
                        if (GameEffects.Sounds) AudioLibrary.PlaySfx("sfx/SID_PVP_RACE_START");
                        if (!fx) countdownFx = true;
                    }
                    if (big) big.text = left <= 3f && left > 0f && !GameEffects.ScreenPlaying("pvp_countdownstart") ? Mathf.CeilToInt(left).ToString() : "";
                    if (left <= 0f) { state = State.Driving; lapStart = now; player.ExternalControl = false; if (big) big.text = ""; }
                    break;
                }
                case State.Driving:
                    Drive(now);
                    break;
            }
            if (state != State.Results && now >= roundEndsAt && !online) EndRoundOffline();
            if (state == State.Results && now >= resultsAt) NewRound();
            if (notice && now > noticeHide) { UiManager.Instance?.Close(notice); notice = null; }
            Hud(now);
        }

        void Drive(float now)
        {
            var pos = player.transform.position;
            index = RaceMission.NearestOnLine(track.Line, pos, index);
            WrongWay();
            if (!gates.Update(pos)) return;
            int n = gates.Count;
            if (online) OnlineSession.Instance.Net.Send("lap.gate", new Dictionary<string, object> { ["n"] = n });
            if (n % track.GatesPerLap != 0) return;
            // 0x52c: a lap
            float lap = now - lapStart;
            lapStart = now;
            int lapNo = n / track.GatesPerLap;
            float prev = myLaps.Count > 0 ? myLaps[myLaps.Count - 1] : -1f;
            myLaps.Add(lap);
            ShowMark(prev, lap, now);
            if (!online) OnMyLap(lap, OfflineLapReward());
            if (lapNo >= track.Laps)
            {
                state = State.RunOver;
                if (GameEffects.Sounds) AudioLibrary.PlaySfx("sfx/SID_PVP_RACE_OVER");
                GameEffects.Screen("pvp_finish");
                StartCoroutine(NextRun());
            }
            else RaceMission.LapEffect(lapNo + 1, track.Laps);
        }

        IEnumerator NextRun()
        {
            yield return new WaitForSeconds(NextRunDelay);
            if (state == State.RunOver) StartRun();
        }

        void WrongWay()
        {
            var rb = player.GetComponent<Rigidbody>();
            var v = rb ? rb.linearVelocity : Vector3.zero; v.y = 0;
            int n = track.Line.Count;
            var course = track.Line[(index + 3) % n] - track.Line[index]; course.y = 0;
            bool against = v.magnitude > 3f && course.sqrMagnitude > 0.01f && Vector3.Dot(v.normalized, course.normalized) <= -0.5f;
            wrongWay = against ? wrongWay + Time.deltaTime : 0f;
            if (wrongWay > RaceMission.WrongWaySeconds)
            {
                wrongWay = 0f;
                GameEffects.Screen("pvp_wrongway");
                if (GameEffects.Sounds) AudioLibrary.PlaySfx("sfx/SID_PVP_RACE_WARNING");
            }
        }

        (Vector3 pos, Quaternion rot)? RespawnPoint()
        {
            if (track == null || track.Line.Count < 2) return null;
            int n = track.Line.Count;
            var a = track.Line[index % n]; var f = track.Line[(index + 3) % n] - a; f.y = 0;
            if (OnlineSession.Active && OnlineSession.Instance) OnlineSession.Instance.MarkTeleport();
            return (PvpGates.GroundNear(a) - Vector3.up * 0.5f, Quaternion.LookRotation(f.sqrMagnitude > 1e-4f ? f.normalized : Vector3.forward, Vector3.up));
        }

        /// <summary>The lap grade (MARKPOOR..GREAT) by the change from the previous lap: > 1 s POOR, (0, 1] BAD, first lap
        /// GOOD, [-1, 0) COOL, &lt; -1 GREAT.</summary>
        public static string Mark(float prev, float lap)
        {
            if (prev < 0f) return "GOOD";
            float d = lap - prev;
            return d > 1f ? "POOR" : d > 0f ? "BAD" : d >= -1f ? (d == 0f ? "GOOD" : "COOL") : "GREAT";
        }

        string mark = "";
        void ShowMark(float prev, float lap, float now) { mark = Mark(prev, lap); markHide = now + 3f; }

        // ---------------- records ----------------
        (long exp, long mito) OfflineLapReward()
        {
            var p = PlayerProfile.Current;
            if (p == null) return (0, 0);
            long exp = 10 + p.Level, mito = 50;
            int before = p.Level;
            p.AddExp(exp); p.Mito += mito;
            p.Save();
            GameFlow.Instance?.RefreshHud();
            if (p.Level > before) { ChatBox.System("Congratulations! You have leveled up!"); GameEffects.LevelUp(); }
            return (exp, mito);
        }

        /// <summary>My lap (0x7209d0 for my own echo): best list, rank, "Exp +%d Mito +%d", the notice on a record.</summary>
        void OnMyLap(float lap, (long exp, long mito) reward)
        {
            var me = field[Me];
            me.Laps.Add(lap);
            bool record = myBest <= 0f || lap < myBest;
            int prevRank = myRank;
            if (record) { myBest = lap; me.Best = lap; }
            myRank = RankOf(Me);
            if (reward.exp > 0 || reward.mito > 0) ChatBox.System($"Exp +{reward.exp} Mito +{reward.mito}");
            if (record) ShowNotice(lap, prevRank, myRank);
            var p = PlayerProfile.Current;
            if (p != null && (p.BestLapSeconds <= 0 || lap < p.BestLapSeconds)) { p.BestLapSeconds = lap; p.Save(); }
        }

        int RankOf(string name) { var r = Ranking; int i = r.FindIndex(x => x.Name == name); return i < 0 ? 0 : i + 1; }

        /// <summary>LAPTIMENOTICE 3450000 for 5 s: the record, 10711 no rank / 10712 new 1st record / 10714 no change / 10713 up.</summary>
        void ShowNotice(float lap, int prevRank, int rank)
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            if (!notice) notice = ui.Open(NoticeDialog);
            if (!notice) return;
            foreach (var c in notice.GetComponentsInChildren<NuiControlView>(true)) c.gameObject.SetActive(true);
            notice.SetText("ID_LAPTIMENOTICE_UPPERRECORD", LapText(lap));
            string lower = rank == 0 ? Pvp.Text("10711") : rank == 1 ? Pvp.Text("10712") : prevRank > 0 && prevRank <= rank ? Pvp.Text("10714", rank) : Pvp.Text("10713", rank);
            notice.SetText("ID_LAPTIMENOTICE_LOWERTEXT", lower);
            noticeHide = Time.time + 5f;
            if (GameEffects.Sounds) AudioLibrary.PlaySfx("sfx/SID_PVP_RESULT");
        }

        /// <summary>"%2d'%2d.%3d" (the HUD and the notice).</summary>
        public static string LapText(float s)
        {
            int ms = Mathf.Max(0, Mathf.RoundToInt(s * 1000f));
            return $"{ms / 60000,2}'{ms / 1000 % 60,2}.{ms % 1000,3}";
        }

        /// <summary>"%02d'%02d.%03d" (the best list and the results).</summary>
        public static string ListText(float s)
        {
            int ms = Mathf.Max(0, Mathf.RoundToInt(s * 1000f));
            return $"{ms / 60000:00}'{ms / 1000 % 60:00}.{ms % 1000:000}";
        }

        void Simulate(float now)
        {
            if (state == State.Results) return;
            foreach (var r in field.Values.Where(x => !x.Me))
            {
                if (now < r.NextAt) continue;
                float lap = LapOf(r.Skill);
                r.Laps.Add(lap);
                if (r.Best <= 0 || lap < r.Best) r.Best = lap;
                r.NextAt = now + lap;
            }
            int rank = RankOf(Me);
            if (rank != myRank) { lastRank = myRank; myRank = rank; }
        }

        // ---------------- server messages (lap.*) ----------------
        internal static void OnMessage(string t, Dictionary<string, object> m)
        {
            var a = Active;
            if (!a || !a.online) return;
            switch (t)
            {
                case "race.lapPlayers": a.SetPlayers(m.L("players")); break;
                case "race.lapRound": a.roundEndsAt = Time.time + (float)m.N("endsIn") / 1000f; break;
                case "race.lapLap": a.OnServerLap(m); break;
                case "race.lapResults": a.ShowResults(m); break;
                case "race.lapRejected": UiPopup.Message("Lap rejected by the server: " + m.S("reason")); break;
                case "race.lapStarting": ChatBox.System($"{m.S("name")} starts a run."); break;
            }
        }

        void SetPlayers(List<object> list)
        {
            if (list == null) return;
            var names = new HashSet<string>();
            foreach (var o in list.OfType<Dictionary<string, object>>())
            {
                string n = o.S("name");
                names.Add(n);
                if (!field.TryGetValue(n, out var r)) field[n] = r = new Rival { Name = n, Me = n == Me };
                r.Level = Mathf.Max(1, o.I("level"));
                r.Best = (float)o.N("best");
            }
            foreach (var k in field.Keys.Where(k => !names.Contains(k) && k != Me).ToList()) field.Remove(k);
            myRank = RankOf(Me);
        }

        void OnServerLap(Dictionary<string, object> m)
        {
            string n = m.S("name");
            if (!field.TryGetValue(n, out var r)) field[n] = r = new Rival { Name = n, Me = n == Me };
            r.Best = (float)m.N("best");
            if (n == Me) OnMyLap((float)m.N("time"), (m.I("exp"), m.I("mito")));
            else myRank = RankOf(Me);
        }

        // ---------------- the round's end: LAPTIMERET 3460000 ----------------
        void EndRoundOffline()
        {
            var ranking = Ranking;
            var top = new List<object>();
            for (int i = 0; i < Mathf.Min(3, ranking.Count); i++)
            {
                var r = ranking[i];
                var rewards = Rewards(i + 1);
                top.Add(new Dictionary<string, object> { ["name"] = r.Name, ["level"] = (double)r.Level, ["time"] = (double)r.Best, ["rewards"] = rewards });
                if (r.Me) PayTop(rewards);
            }
            int me = RankOf(Me);
            ShowResults(new Dictionary<string, object> { ["top"] = top, ["me"] = new Dictionary<string, object> { ["rank"] = (double)me, ["time"] = (double)myBest, ["rewards"] = me >= 1 && me <= 3 ? Rewards(me) : new List<object>() } });
        }

        /// <summary>The round's top-three rewards (remake amounts, shared with src/laptime.js): Mito and EXP.</summary>
        static List<object> Rewards(int rank)
        {
            int[] mito = { 3000, 2000, 1000 }, exp = { 500, 300, 200 };
            if (rank < 1 || rank > 3) return new List<object>();
            return new List<object>
            {
                new Dictionary<string, object> { ["type"] = 3.0, ["amount"] = (double)mito[rank - 1] },
                new Dictionary<string, object> { ["type"] = 5.0, ["amount"] = (double)exp[rank - 1] },
            };
        }

        static void PayTop(List<object> rewards)
        {
            var p = PlayerProfile.Current;
            if (p == null) return;
            int before = p.Level;
            foreach (var o in rewards.OfType<Dictionary<string, object>>()) { if (o.I("type") == 3) p.Mito += o.I("amount"); else if (o.I("type") == 5) p.AddExp(o.I("amount")); }
            p.Save();
            GameFlow.Instance?.RefreshHud();
            if (p.Level > before) GameEffects.LevelUp();
        }

        void ShowResults(Dictionary<string, object> m)
        {
            state = State.Results;
            player.ExternalControl = true;
            resultsAt = Time.time + 8f;
            if (GameEffects.Sounds) AudioLibrary.PlaySfx("sfx/SID_PVP_RACE_OVER");
            GameEffects.Screen("pvp_raceover");
            StartCoroutine(OpenResults(m));
        }

        IEnumerator OpenResults(Dictionary<string, object> m)
        {
            yield return new WaitForSeconds(1f);
            var ui = UiManager.Instance;
            if (!ui || state != State.Results) yield break;
            if (!result) result = ui.Open(ResultDialog);
            if (!result) yield break;
            foreach (var c in result.GetComponentsInChildren<NuiControlView>(true)) c.gameObject.SetActive(true);
            string[] pos = { "1ST", "2ND", "3RD", "4TH" };
            var top = (m.L("top") ?? new List<object>()).OfType<Dictionary<string, object>>().ToList();
            var me = m.O("me");
            for (int i = 0; i < 4; i++)
            {
                Dictionary<string, object> row = i < 3 ? (i < top.Count ? top[i] : null)
                    : new Dictionary<string, object> { ["name"] = Me, ["level"] = (double)(PlayerProfile.Current?.Level ?? 1), ["time"] = me != null ? me.N("time") : 0.0, ["rewards"] = me?.L("rewards") };
                bool used = row != null && (i == 3 || row.I("level") > 0);
                foreach (var k in new[] { "LEVEL", "NAME", "RECORD" }) result.SetVisible($"ID_LAPTIMERET_{k}{pos[i]}", used);
                if (used)
                {
                    result.SetText($"ID_LAPTIMERET_LEVEL{pos[i]}", $"Lv {row.I("level"),2}");
                    result.SetText($"ID_LAPTIMERET_NAME{pos[i]}", row.S("name"));
                    result.SetText($"ID_LAPTIMERET_RECORD{pos[i]}", row.N("time") > 0 ? LapText((float)row.N("time")) : "    -");
                }
                var rewards = used ? (row.L("rewards") ?? new List<object>()).OfType<Dictionary<string, object>>().ToList() : new List<Dictionary<string, object>>();
                for (int k = 0; k < 3; k++)
                {
                    bool on = k < rewards.Count;
                    result.SetVisible($"ID_LAPTIMERET_REWARDBASE{pos[i]}_{k + 1}", on);
                    string icon = on ? (rewards[k].I("type") == 3 ? "i_d_m_1m" : rewards[k].I("type") == 5 ? "exp_icon" : rewards[k].S("item")) : null;
                    EventUi.Icon(result, $"ID_LAPTIMERET_REWARDICON{pos[i]}_{k + 1}", icon, on);
                }
            }
            int rank = me != null ? me.I("rank") : 0;
            // MYRANK digits: tens at POSMYRANK1ST, units at POSMYRANK2ND (copied from the MYRANK0..9 glyphs)
            for (int d = 0; d <= 9; d++) result.SetVisible("ID_LAPTIMERET_MYRANK" + d, false);
            Glyph(result, "ID_LAPTIMERET_MYRANK", "ID_LAPTIMERET_POSMYRANK1ST", rank >= 10 ? rank / 10 % 10 : -1);
            Glyph(result, "ID_LAPTIMERET_MYRANK", "ID_LAPTIMERET_POSMYRANK2ND", rank > 0 ? rank % 10 : -1);
        }

        void NewRound()
        {
            var ui = UiManager.Instance;
            if (result) { if (ui) ui.Close(result); result = null; }
            myLaps.Clear(); myBest = 0f; myRank = 0;
            foreach (var r in field.Values) { r.Best = 0f; r.Laps.Clear(); r.NextAt = Time.time + StartDelay + (r.Me ? 0f : LapOf(r.Skill)); }
            if (!online) roundEndsAt = Time.time + OfflineRoundSeconds;
            StartRun();
        }

        /// <summary>Tests: ends the offline round now.</summary>
        internal void ForceRoundEnd() { if (!online) EndRoundOffline(); }

        /// <summary>Tests: counts the next gates as passed (each lap closes on its last one).</summary>
        internal void ForceLap()
        {
            if (state == State.Countdown) { state = State.Driving; lapStart = Time.time - 1f; player.ExternalControl = false; }
            if (state != State.Driving) return;
            int target = (gates.Laps + 1) * track.GatesPerLap;
            lapStart = Time.time - (75f + UnityEngine.Random.Range(-3f, 3f));
            while (gates.Count < target)
            {
                gates.Set(gates.Count + 1);
                if (online) OnlineSession.Instance.Net.Send("lap.gate", new Dictionary<string, object> { ["n"] = gates.Count });
            }
            float lap = Time.time - lapStart;
            lapStart = Time.time;
            float prev = myLaps.Count > 0 ? myLaps[myLaps.Count - 1] : -1f;
            myLaps.Add(lap);
            ShowMark(prev, lap, Time.time);
            if (!online) OnMyLap(lap, OfflineLapReward());
            if (gates.Laps >= track.Laps) { state = State.RunOver; StartCoroutine(NextRun()); }
        }

        // ---------------- HUD: LAPTIMEPLAY 3440000 ----------------
        static RawImage Img(NuiView v, string id) { var c = v ? v.Control(id) : null; return c ? c.GetComponentInChildren<RawImage>(true) : null; }

        /// <summary>Copies glyph <paramref name="set"/> + digit into a placeholder control (digit -1 hides it).</summary>
        static void Glyph(NuiView v, string set, string placeholder, int digit)
        {
            var dst = v ? v.Control(placeholder) : null;
            if (!dst) return;
            dst.gameObject.SetActive(digit >= 0);
            if (digit < 0) return;
            var src = Img(v, set + digit);
            var di = dst.GetComponentInChildren<RawImage>(true);
            if (src && di) { di.texture = src.texture; di.uvRect = src.uvRect; di.color = src.color; di.enabled = src.texture; }
        }

        void Hud(float now)
        {
            var v = play;
            if (!v) return;
            const string P = "ID_LAPTIMEPLAY_";
            v.SetVisible(P + "EXIT", true);
            // laps: current / total
            int cur = Mathf.Min(LapsDone + 1, Laps);
            Glyph(v, P + "LAPCNT_CUR", P + "LAPCNT_POS2ND", cur % 10);
            Glyph(v, P + "LAPCNT_TOT", P + "LAPTOT_POS2ND", Laps % 10);
            v.SetVisible(P + "LAPCNT_POSSLASH", true);
            // running time (the BESTTIME glyphs): MM'SS.mmm
            float t = state == State.Driving ? now - lapStart : 0f;
            int ms = Mathf.Clamp(Mathf.FloorToInt(t * 1000f), 0, 99 * 60000);
            int m = ms / 60000, s = ms / 1000 % 60, f = ms % 1000;
            Glyph(v, P + "BESTTIME_NUM", P + "CURTIME_MIN1ST", m / 10 % 10);
            Glyph(v, P + "BESTTIME_NUM", P + "CURTIME_MIN2ND", m % 10);
            Glyph(v, P + "BESTTIME_NUM", P + "CURTIME_SEC1ST", s / 10);
            Glyph(v, P + "BESTTIME_NUM", P + "CURTIME_SEC2ND", s % 10);
            Glyph(v, P + "BESTTIME_NUM", P + "CURTIME_MS1ST", f / 100);
            Glyph(v, P + "BESTTIME_NUM", P + "CURTIME_MS2ND", f / 10 % 10);
            Glyph(v, P + "BESTTIME_NUM", P + "CURTIME_MS3TH", f % 10);
            v.SetVisible(P + "CURTIME_COMMA", true);
            // drivers in the round
            int count = Drivers;
            v.SetVisible(P + "PLAYERCNTICON", true);
            Glyph(v, P + "PLAYERCNT_NUM", P + "PLAYERCNT_POSTEN", count >= 10 ? count / 10 % 10 : -1);
            Glyph(v, P + "PLAYERCNT_NUM", P + "PLAYERCNT_POSONE", count % 10);
            // the leader
            var ranking = Ranking;
            var lead = ranking.FirstOrDefault();
            v.SetVisible(P + "1STPLAYER_NAME", lead != null); v.SetVisible(P + "1STPLAYER_TIME", lead != null); v.SetVisible(P + "1STPLAYER_MARK", lead != null);
            if (lead != null) { v.SetText(P + "1STPLAYER_NAME", lead.Name); v.SetText(P + "1STPLAYER_TIME", LapText(lead.Best)); }
            // my rank (RANKTEN1..3 / RANKONE0..9 glyphs at their places, st / nd / rd / th), NORANK
            int rank = myRank;
            v.SetVisible(P + "NORANK", rank == 0);
            for (int d = 0; d <= 9; d++) { v.SetVisible(P + "RANKONE" + d, rank > 0 && rank % 10 == d); v.SetVisible(P + "RANKTEN" + d, rank >= 10 && rank / 10 % 10 == d); }
            int teen = rank % 100;
            int last = teen >= 11 && teen <= 13 ? 0 : rank % 10;
            v.SetVisible(P + "RANK_ST", rank > 0 && last == 1); v.SetVisible(P + "RANK_ND", rank > 0 && last == 2);
            v.SetVisible(P + "RANK_RD", rank > 0 && last == 3); v.SetVisible(P + "RANK_TH", rank > 0 && (last == 0 || last > 3));
            if (rank != lastRank && rank > 0 && GameEffects.Sounds) AudioLibrary.PlaySfx("sfx/SID_PVP_ROBBY_TEAM");
            lastRank = rank;
            // the best list window from max(0, myRank - 3); rows 4 and 5 stay hidden (0x681be0 / 0x681c30)
            int start = Mathf.Max(0, rank - 3);
            for (int i = 0; i < 6; i++)
            {
                int k = start + i;
                bool used = i < 4 && k < ranking.Count;
                v.SetVisible(P + "LISTBASE" + i, used);
                v.SetVisible(P + "LISTNAME" + i, used);
                v.SetVisible(P + "LISTRECORD" + i, used);
                if (!used) continue;
                var r = ranking[k];
                v.SetText(P + "LISTNAME" + i, $"{Pvp.Ordinal(k + 1)} {r.Name}");
                v.SetText(P + "LISTRECORD" + i, ListText(r.Best));
                var b = v.Control(P + "LISTBASE" + i); if (b) b.Selected = r.Me;
            }
            // my best and my last five laps (MYBESTn on the best one)
            v.SetVisible(P + "MYRECBASE", true);
            v.SetVisible(P + "MYBESTRECORD", true);
            v.SetText(P + "MYBESTRECORD", myBest > 0 ? LapText(myBest) : "    -");
            string[] ord = { "1ST", "2ND", "3RD", "4TH", "5TH" };
            var lastFive = myLaps.Skip(Mathf.Max(0, myLaps.Count - 5)).ToList();
            for (int i = 0; i < 5; i++)
            {
                bool used = i < lastFive.Count;
                int lapNo = myLaps.Count - lastFive.Count + i + 1;
                bool best = used && Mathf.Approximately(lastFive[i], myBest);
                v.SetVisible(P + "MYREC" + ord[i], used && !best);
                v.SetVisible(P + "MYBEST" + ord[i], used && best);
                if (used) v.SetText(P + (best ? "MYBEST" : "MYREC") + ord[i], $"{lapNo,2}Lap {LapText(lastFive[i])}");
            }
            foreach (var mk in new[] { "POOR", "BAD", "GOOD", "COOL", "GREAT" }) v.SetVisible(P + "MARK" + mk, now < markHide && mark == mk);
        }
    }
}
