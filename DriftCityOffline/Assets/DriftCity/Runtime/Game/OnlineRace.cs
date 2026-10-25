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
    /// Player-vs-player race through the online server (race.js). The Battle Zone screens (PvpLobby, PvpWaitRoom) drive
    /// the rooms; this class routes the server's race messages, runs the race itself (grid by the server's slot order, the
    /// "pvpready" / "pvpstart" cameras and the countdown, the checkpoint gates reported to the server, the PvP HUD and
    /// minimap, goal-in banners and camera, the end-time ten count) and shows the server's results (the reward pick, then
    /// PvpResults or the wager screens) before going back to the wait room. A spectator watches with VIEWING 1960000; the
    /// server's AI drivers are drawn by local AI cars on the racing line (their places are the server's).
    /// </summary>
    public sealed class OnlineRace : MonoBehaviour
    {
        public static OnlineRace Active { get; private set; }

        RaceTrack track;
        DriftCar player;
        int myId, lap, index;
        bool half, running, done, finishedSent, countdownFx, countdownStarted, spectator, gated, realMatch, startCam;
        float countdown, raceTime, sendTimer, lapStart, bestLap, endAt = -1f, wrongWay;
        List<object> standings;
        Text bigText;
        GameObject hudRoot;
        PvpHud hud;
        PvpMinimap minimap;
        PvpCameraRig rig;
        GateTracker gates;
        readonly List<GameObject> gatePosts = new List<GameObject>();
        readonly List<Material> gateMaterials = new List<Material>();
        readonly Dictionary<int, string> names = new Dictionary<int, string>();
        readonly Dictionary<int, int> teams = new Dictionary<int, int>();
        readonly Dictionary<int, DriftCar> aiCars = new Dictionary<int, DriftCar>();
        readonly Dictionary<int, int> aiCarIndex = new Dictionary<int, int>();
        /// <summary>Real Match rooms: the other human drivers' room slots by name (their cars wear that slot's RM look).</summary>
        readonly Dictionary<string, int> rmSlots = new Dictionary<string, int>();
        readonly List<GameObject> rmSkinned = new List<GameObject>();
        float rmScan;
        /// <summary>The race being recorded for its replay (PvpReplay).</summary>
        public PvpReplay.Recorder Recorder => recorder;
        System.Func<(Vector3 pos, Quaternion rot)?> respawnFn;
        bool team;
        int arena = -1, wager, drivers, round, mission, roomId;
        PvpReplay.Recorder recorder;
        /// <summary>The Channel Battle round of this race (race.start "round": 1, 2, 3 = Final; 0 = not a crew round).</summary>
        public int Round => round;
        Dictionary<string, object> lastWager;
        PvpRoom watchedRoom;

        /// <summary>Seconds after the expected start at which the race starts locally if race.go never arrives.</summary>
        const float GoFallback = 2f;
        /// <summary>Farther than this from the line near the last index, the nearest point is searched on the whole line.</summary>
        const float LostDistance = 80f;

        static OnlineSession S => OnlineSession.Instance;
        static int PlayerIdHint;

        /// <summary>True while the player is in a race room (wait room) or racing.</summary>
        public static bool InLobby => (PvpWaitRoom.Room != null && PvpWaitRoom.Room.Online) || Active;
        public bool Spectator => spectator;
        public GateTracker Gates => gates;

        // ---------------- lobby ----------------
        /// <summary>The Battle Zone lobby with the channel's server rooms.</summary>
        public static void ShowLobby(CitySession session)
        {
            if (!OnlineSession.Active || session == null) return;
            Listen();
            PvpLobby.Open(session);
        }

        static bool listening;
        internal static void Listen()
        {
            if (listening || !S) return;
            listening = true;
            S.RaceMessage += OnRaceMessage;
            S.Net.Request("race.rm", null, r => { if (r.B("ok")) Pvp.SetRealMatch(r.B("open"), r.N("endsIn"), r.N("startsIn")); });
        }

        static Dictionary<string, object> pendingWager;

        static void OnRaceMessage(string t, Dictionary<string, object> m)
        {
            if (t.StartsWith("race.lap")) { LapTimeBattle.OnMessage(t, m); return; }
            switch (t)
            {
                case "race.room": PvpWaitRoom.OnServerRoom(m.O("room")); break;
                case "race.rooms": if (PvpLobby.IsOpen && PvpLobby.View) PvpLobby.SetServerRooms(m.L("rooms")); break;
                case "race.kicked": PvpWaitRoom.OnKicked(m.S("reason")); break;
                case "race.start": Begin(m); break;
                case "race.go": if (Active) Active.Go(); break;
                case "race.gateEffect": if (Active) Active.OnGateEffect(m); break;
                case "race.standings": if (Active) Active.standings = m.L("racers"); break;
                case "race.finished": if (Active) Active.OnFinished(m); break;
                case "race.endTime": if (Active) Active.endAt = Active.raceTime + (float)m.N("in", 30000) / 1000f; break;
                case "race.rejected": UiPopup.Message("Race result rejected by the server: " + m.S("reason")); break;
                case "race.chat": PvpChat.Receive(m.S("from"), m.S("text")); break;
                case "race.invite": PvpSocial.OnInvited(m); break;
                case "race.picked": PvpRewardPick.OnPicked(m); break;
                case "race.wager": pendingWager = m; break;
                case "race.rm": OnRealMatch(m); break;
                case "race.tour": Tournament.OnServer(m.O("tour")); break;
                case "race.tourReward": Tournament.OnReward(m); break;
                case "race.results":
                    if (Active) Active.Results(m); else ShowResults(m, null);
                    break;
            }
        }

        /// <summary>race.rm (0x1c8): Real Match time and its notices (10843 / 10844 "Real Match will end in %d ...", 10845).</summary>
        static void OnRealMatch(Dictionary<string, object> m)
        {
            bool open = m.B("open");
            Pvp.SetRealMatch(open, m.N("endsIn"), m.N("startsIn"));
            string notice = m.S("notice");
            string line = null;
            if (notice == "end") line = Pvp.Text("10845");
            else if (notice == "ending")
            {
                int secs = Mathf.RoundToInt((float)m.N("endsIn") / 1000f);
                line = secs < 60 ? Pvp.Text("10843", secs) : Pvp.Text("10844", Mathf.CeilToInt(secs / 60f));
            }
            else if (notice == "start") line = "Real Match has started.";
            if (string.IsNullOrEmpty(line)) return;
            ChatBox.System(line);
            PvpChat.System(line);
            if (PvpLobby.IsOpen) PvpLobby.Refresh();
        }

        /// <summary>
        /// Leaves the current race room (or aborts the race) and tells the server. Call when travelling to another city
        /// or logging out (OnlineSession.LeaveCity / Logout already do). immediate = send right away (socket about to close).
        /// </summary>
        public static void LeaveLobby(bool immediate = false)
        {
            bool inRoom = PvpWaitRoom.Room != null && PvpWaitRoom.Room.Online;
            if (inRoom || PvpWaitRoom.IsOpen) PvpWaitRoom.Reset();
            if (LapTimeBattle.Active) LapTimeBattle.Reset();
            if (Active) { Active.Abort(immediate); return; }
            if (inRoom) SendLeave(immediate);
        }

        static void SendLeave(bool immediate)
        {
            var net = S ? S.Net : null;
            if (!net || !net.Connected) return;
            if (immediate) net.SendNow("race.leave", new Dictionary<string, object>());
            else net.Request("race.leave", null, null);
        }

        /// <summary>The server's results as result rows (finishers first, then DNF), shown by PvpResults.</summary>
        static PvpRaceResult ToResult(Dictionary<string, object> m, RaceTrack t, float myBestLap, bool watched)
        {
            var me = PlayerProfile.Current?.Name;
            var res = new PvpRaceResult { Track = t, Online = true, BestLap = myBestLap, Spectator = watched, Wager = m.I("wager"), Arena = m.ContainsKey("arena") ? m.I("arena") : -1 };
            var tr = m.O("track");
            res.TrackName = t != null ? Pvp.TrackName(t) : tr != null ? Pvp.Text(tr.S("name")) : "";
            var tp = m.O("teamPoints");
            if (tp != null) { res.Team = true; res.TeamA = tp.I("a"); res.TeamB = tp.I("b"); res.Winner = m.I("winner"); }
            var wagerBy = new Dictionary<string, Dictionary<string, object>>();
            if (pendingWager != null && pendingWager.I("id") == m.I("id"))
                foreach (var e in (pendingWager.L("entries") ?? new List<object>()).OfType<Dictionary<string, object>>()) wagerBy[e.S("name")] = e;
            foreach (var o in (m.L("results") ?? new List<object>()).OfType<Dictionary<string, object>>().OrderBy(x => x.I("place") == 0 ? 99 : x.I("place")))
            {
                var rw = o.O("reward");
                var e = new PvpResultEntry
                {
                    Name = o.S("name"), Level = Mathf.Max(1, o.I("level")), Place = o.B("dnf") ? 0 : o.I("place"), Time = (float)o.N("time"),
                    Me = !watched && o.S("name") == me, Team = o.I("team"), Points = o.I("points"), Ladder = o.I("ladder"), LadderShown = o.ContainsKey("ladder"),
                    Exp = rw != null ? rw.I("exp") : 0, Mito = rw != null ? rw.I("mito") : 0, LevelUp = rw != null && rw.I("levelUps") > 0, Ai = o.B("ai"),
                };
                if (wagerBy.TryGetValue(e.Name, out var w)) { e.WagerDelta = w.I("delta"); e.WagerMito = (long)w.N("mito"); }
                // bonus icons: the server's flags when it sends them, else the driver's own buffs [remake]
                if (o.ContainsKey("bonus")) e.Bonus = o.I("bonus");
                else if (e.Me && rw != null) e.Bonus = Pvp.BonusFlags(PlayerProfile.Current);
                if (e.Me && rw != null) e.Tree = Pvp.TreeStage();
                res.Entries.Add(e);
            }
            res.Pick = !watched && m.O("pick") != null;
            return res;
        }

        static void ShowResults(Dictionary<string, object> m, RaceTrack t, float myBestLap = 0f, bool watched = false)
        {
            var res = ToResult(m, t, myBestLap, watched);
            if (res.Wager > 0 && pendingWager != null) PvpWager.ChatLines(res);
            pendingWager = null;
            // a GT Championship heat goes back to the TOUR lobby (the bracket moved on meanwhile)
            bool tour = m.B("tour");
            void Show() => PvpResults.Show(res, () => { PvpWaitRoom.OnRaceOver(); if (tour) Tournament.Open(CitySession.Current ?? PvpLobby.Session); });
            if (res.Pick) PvpRewardPick.Open(res.Arena, null, Show); else Show();
        }

        // ---------------- race ----------------
        static void Begin(Dictionary<string, object> m)
        {
            var session = CitySession.Current ?? PvpLobby.Session;
            var key = m.O("track").S("key");
            var tr = session ? RaceMission.TracksFor(session.City).FirstOrDefault(x => Pvp.TrackKey(x) == key) : null;
            var car = GameFlow.Instance ? GameFlow.Instance.PlayerCar : null;
            if (tr == null || tr.Line.Count < 2 || !car)
            {
                UiPopup.Message("This race's track isn't available here.");
                PvpWaitRoom.Reset();
                SendLeave(false);
                return;
            }
            if (Active) Active.Cleanup();
            if (RaceMission.Active) RaceMission.Active.Abort();
            if (ChaseMission.Active) Destroy(ChaseMission.Active.gameObject);
            if (LapTimeBattle.Active) LapTimeBattle.Reset();
            var watched = PvpWaitRoom.Room;
            PvpLobby.Close();
            PvpWaitRoom.CancelExpect();
            PvpWaitRoom.OnRaceStarting();
            // the race takes over the screen: a list window the player had open would sit on top of it
            ListWindow.CloseOpen();
            int slot = -1;
            var me = PlayerProfile.Current?.Name;
            var race = new GameObject("Online Race").AddComponent<OnlineRace>();
            var aiSlots = new List<(int id, PvpMember m, int slot)>();
            foreach (var o in m.L("slots") ?? new List<object>())
            {
                if (!(o is Dictionary<string, object> s)) continue;
                race.names[s.I("id")] = s.S("name");
                race.teams[s.I("id")] = s.I("team");
                if (s.B("ai")) aiSlots.Add((s.I("id"), new PvpMember { Id = s.I("id"), Name = s.S("name"), Ai = true, Level = s.I("level"), Car = GameTables.Car(s.I("car")), Skill = (float)s.N("skill", 0.9) }, s.I("slot")));
                else if (s.S("name") == me) { slot = s.I("slot"); PlayerIdHint = s.I("id"); }
                else if (!string.IsNullOrEmpty(s.S("name"))) race.rmSlots[s.S("name")] = s.I("slot");
            }
            race.team = m.B("team");
            race.arena = m.ContainsKey("arena") ? m.I("arena") : -1;
            race.wager = m.I("wager");
            race.spectator = slot < 0;
            race.gated = m.B("gates");
            race.realMatch = m.B("realMatch");
            race.drivers = race.names.Count;
            race.watchedRoom = watched;
            race.round = m.I("round");
            race.mission = m.I("mission");
            race.roomId = m.I("id");
            if (race.round > 0) GameEffects.ChannelRound();   // 0x723d60 -> 0x674e10: channel_count
            race.Setup(tr, car, Mathf.Max(0, slot), (float)m.N("startIn", 5000) / 1000f, aiSlots);
            // the track's traffic, the same on every client: seeded from the room and driven by the race clock
            int trackNo = int.TryParse(new string((tr.Track ?? "").Where(char.IsDigit).ToArray()), out var tn) ? tn : 0;
            int seed = PvpPathTraffic.SeedFor(m.I("id"), key, race.drivers);
            int flags = watched != null ? watched.MapFlags : 0;
            if (session && session.Traffic && (flags & Pvp.TrafficIdle) == 0)
            {
                race.pathTraffic = PvpPathTraffic.Start(session.Traffic, tr.City, trackNo, PvpTraffic.CountFor(CityMapInfo.CityIndex(tr.City), trackNo, race.drivers, flags, seed), seed, () => race ? race.raceTime : 0f);
                if (race.pathTraffic) { race.prevTraffic = session.Traffic.MaxCars; session.Traffic.MaxCars = 0; session.Traffic.TrimTo(0); }
            }
        }

        PvpPathTraffic pathTraffic;
        int prevTraffic = -1;

        void Setup(RaceTrack t, DriftCar car, int slot, float startIn, List<(int id, PvpMember m, int slot)> aiSlots)
        {
            Active = this;
            track = t; player = car; myId = PlayerIdHint; countdown = Mathf.Max(0f, startIn);
            Vector3 start = t.Line[0], dir = t.Line[Mathf.Min(3, t.Line.Count - 1)] - start;
            dir.y = 0;
            if (dir.sqrMagnitude < 0.0001f) dir = Vector3.forward;
            dir.Normalize();
            var rot = Quaternion.LookRotation(dir, Vector3.up);
            player.ExternalControl = true;
            if (!spectator)
            {
                var pos = RaceMission.Ground(RaceMission.GridSlot(t, slot));
                RaceMission.Teleport(player, pos, rot);
                S.MarkTeleport();
                respawnFn = RespawnPoint;
                player.RespawnOverride = respawnFn;
                gates = new GateTracker(t);
                if (realMatch) { var s = Pvp.RealMatchStats(); player.ApplyStats(s.accel, s.speed, s.crash, s.boost); }
                if (mission > 0) { var s = PvpMissionBattle.FixedStats; player.ApplyStats(s.accel, s.speed, s.crash, s.boost); }
                // the RM car's look of the player's slot (PVP.md 14.4)
                if (realMatch) RealMatchSkin.Apply(player.gameObject, slot);
            }
            if (!realMatch) rmSlots.Clear();
            index = 0;
            // the server's AI drivers: local cars on the racing line (visual; their places come from the server)
            foreach (var (id, m, aiSlot) in aiSlots)
            {
                var c = RaceMission.SpawnAi(m, RaceMission.Ground(RaceMission.GridSlot(t, aiSlot)), rot, t, realMatch ? aiSlot : -1);
                aiCars[id] = c;
                aiCarIndex[id] = m.Car?.Index ?? -1;
            }

            var ui = UiManager.Instance;
            hudRoot = NuiView.NewRect("Online Race HUD", ui.Root).gameObject;
            NuiView.Stretch((RectTransform)hudRoot.transform);
            bigText = Label(Vector2.zero, new Vector2(1024, 200), 96, TextAnchor.MiddleCenter, true);
            minimap = PvpMinimap.Open(t);
            if (spectator) { PvpSpectate.BeginViewing(watchedRoom, t, Drivers, TeamPoints, () => { Abort(); PvpLobby.Reopen(); }, () => running); PvpGhost.BeginRace(false); }
            else hud = PvpHud.Open();
            BuildGatePosts(t);
            PvpHud.ShowRound(round);
            rig = PvpCameraRig.Ensure();
            // the client's timeline before T: "pvpready" until T-19.5 s, "pvpstart" until T-6 s (a short server countdown skips them)
            if (!spectator && countdown > RaceMission.CountdownSeconds + 1f) rig.BeginReady(t, drivers);
            AudioLibrary.PlayMusic(this, "bgm/DriftCity_1stpvp_01.mp3");
        }

        void BuildGatePosts(RaceTrack t)
        {
            var shader = Shader.Find("Sprites/Default");
            var startMat = new Material(shader) { color = new Color(1, 0.85f, 0.1f, 0.5f) };
            var gateMat = new Material(shader) { color = new Color(0.2f, 0.8f, 1f, 0.35f) };
            gateMaterials.Add(startMat); gateMaterials.Add(gateMat);
            if (watchedRoom != null && watchedRoom.NoGate) return;
            if (t.Gates.Count > 0)
            {
                int k = 0;
                foreach (var g in t.Gates.Concat(new[] { t.End }))
                {
                    bool finish = k++ == t.Gates.Count;
                    foreach (var end in new[] { g.A, g.B })
                    {
                        float y = 0f, best = float.MaxValue;
                        foreach (var q in t.Line) { float d = (q.x - end.x) * (q.x - end.x) + (q.z - end.z) * (q.z - end.z); if (d < best) { best = d; y = q.y; } }
                        var post = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                        Destroy(post.GetComponent<Collider>());
                        post.transform.position = PvpGates.GroundNear(new Vector3(end.x, y, end.z)) + Vector3.up * 5f;
                        post.transform.localScale = new Vector3(finish ? 1.6f : 1f, 6f, finish ? 1.6f : 1f);
                        post.GetComponent<Renderer>().sharedMaterial = finish ? startMat : gateMat;
                        gatePosts.Add(post);
                    }
                }
                return;
            }
            float acc = 0;
            for (int i = 0; i < t.Line.Count; i++)
            {
                if (i > 0) acc += Vector3.Distance(t.Line[i - 1], t.Line[i]);
                if (i != 0 && acc < 150f) continue;
                acc = 0;
                var g = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                Destroy(g.GetComponent<Collider>());
                g.transform.position = t.Line[i] + Vector3.up * 6f;
                g.transform.localScale = new Vector3(i == 0 ? 2.5f : 1.2f, 6f, i == 0 ? 2.5f : 1.2f);
                g.GetComponent<Renderer>().sharedMaterial = i == 0 ? startMat : gateMat;
                gatePosts.Add(g);
            }
        }

        /// <summary>R during the race: back onto the racing line at the current point, facing the next one.</summary>
        (Vector3 pos, Quaternion rot)? RespawnPoint()
        {
            if (track == null || track.Line.Count < 2 || done) return null;
            int n = track.Line.Count;
            int i = Mathf.Clamp(index, 0, n - 1);
            Vector3 here = track.Line[i], dir = Vector3.zero;
            for (int k = 1; k < n && dir.sqrMagnitude < 0.01f; k++) { dir = track.Line[(i + k) % n] - here; dir.y = 0; }
            if (dir.sqrMagnitude < 0.01f) dir = player ? player.transform.forward : Vector3.forward;
            dir.y = 0;
            if (S) S.MarkTeleport();
            // Ground() already lifts the point 0.8 m; DriftCar adds its own 0.5 m
            return (RaceMission.Ground(here) - Vector3.up * 0.5f, Quaternion.LookRotation(dir.normalized, Vector3.up));
        }

        Text Label(Vector2 pos, Vector2 size, int fontSize, TextAnchor anchor, bool centered)
        {
            var rt = NuiView.NewRect("text", hudRoot.transform);
            if (centered) { rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f); rt.pivot = new Vector2(0.5f, 0.5f); }
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            var tx = rt.gameObject.AddComponent<Text>();
            tx.font = UiAssets.Font("Verdana " + fontSize, out _, out _);
            tx.fontSize = fontSize; tx.fontStyle = FontStyle.Bold; tx.alignment = anchor; tx.raycastTarget = false;
            tx.color = new Color(1f, 0.85f, 0.2f);
            var o = rt.gameObject.AddComponent<Outline>(); o.effectColor = Color.black; o.effectDistance = new Vector2(2, -2);
            return tx;
        }

        void Go()
        {
            if (running || done) return;
            running = true;
            countdown = 1f;
            if (bigText) bigText.text = countdownFx || spectator ? "" : "GO!";
            if (player && !spectator) player.ExternalControl = false;
            foreach (var c in aiCars.Values) if (c) { var d = c.GetComponent<AiDriver>(); if (d) d.Hold = false; }
            if (mission > 0) PvpMissionBattle.Begin(track, mission, roomId, spectator ? null : player);
            // the race is recorded from GO for its replay (the client's play_record.r0, 0x73e1e0)
            var owned = PlayerProfile.Current?.Car;
            recorder = PvpReplay.Recorder.Begin(gameObject, track, watchedRoom?.Title ?? "", team, track.Laps, () => names.Select(kv =>
                (kv.Value, teams.TryGetValue(kv.Key, out var tm) ? tm : 0,
                 kv.Key == myId && !spectator ? (owned?.VehicleIndex ?? -1) : aiCarIndex.TryGetValue(kv.Key, out var ci) ? ci : -1,
                 kv.Key == myId && !spectator ? (owned?.Color ?? 0) : 0, aiCars.ContainsKey(kv.Key), CarOf(kv.Key, kv.Value))));
        }

        void OnFinished(Dictionary<string, object> m)
        {
            int id = m.I("id");
            if (aiCars.TryGetValue(id, out var ai) && ai) { var d = ai.GetComponent<AiDriver>(); if (d) d.Hold = true; }
            if (id == myId && !spectator) return;
            hud?.GoalIn(m.I("place"), m.S("name"));
            // the "pvpgoalin" camera (0x6bf490) for the drivers who are done, and spectators
            if ((spectator || finishedSent) && rig && m.I("place") <= 3) rig.BeginGoalIn(track, 2f);
        }

        void Update()
        {
            if (done) return;
            if (!player) { Abort(); return; }   // car destroyed mid-race: tell the server we're out
            if (!OnlineSession.Active) { Cleanup(); return; }
            hud?.Tick();
            if (rmSlots.Count > 0 && (rmScan -= Time.deltaTime) <= 0f) { rmScan = 0.5f; SkinRemotes(); }
            if (!running)
            {
                countdown -= Time.deltaTime;
                // T-19.5: "pvpstart" (the track's camera.nif) when the countdown is long enough
                if (!startCam && !spectator && countdown <= RaceMission.ReadyUntil && countdown > RaceMission.CountdownSeconds + 1f)
                {
                    startCam = true;
                    if (rig) rig.BeginStart(track);
                }
                // T-6: the countdown effect 18 (a shorter server countdown skips it and shows the digits)
                if (!countdownStarted && countdown <= RaceMission.CountdownSeconds)
                {
                    countdownStarted = true;
                    if (rig && !spectator) rig.Release();
                    if (spectator && rig && rig.Current != PvpCameraRig.Mode.Watch) { var d = Drivers(); rig.BeginWatch(d.Count > 0 ? d[0].Car : null, track); }
                    countdownFx = countdown >= RaceMission.CountdownSeconds - 0.5f && GameEffects.Screen("pvp_countdownstart");
                    if (GameEffects.Sounds) AudioLibrary.PlaySfx("sfx/SID_PVP_RACE_START");
                }
                if (!countdownFx && !spectator) bigText.text = countdown > 0 && countdown <= RaceMission.CountdownSeconds ? Mathf.CeilToInt(countdown).ToString() : "";
                // something else (e.g. a closing shop) may have released the car: keep it on the grid
                if (!player.ExternalControl) player.ExternalControl = true;
                if (!spectator)
                {
                    var rb = player.GetComponent<Rigidbody>();
                    if (rb) rb.linearVelocity = Vector3.Lerp(rb.linearVelocity, Vector3.zero, 0.5f);
                }
                // race.go lost or late: start locally a little after the announced start
                if (countdown <= -GoFallback) Go();
                UpdateHud();
                return;
            }
            if (countdown > 0) { countdown -= Time.deltaTime; if (countdown <= 0) bigText.text = ""; }
            float prevTime = raceTime;
            raceTime += Time.deltaTime;
            // the server's end time (0x1ef): pvp_tencount for the last 10 s
            if (endAt > 0 && !finishedSent && !spectator && prevTime < endAt - RaceMission.TenCount && raceTime >= endAt - RaceMission.TenCount)
            {
                GameEffects.Screen("pvp_tencount");
                if (GameEffects.Sounds) AudioLibrary.PlaySfx("sfx/SID_PVP_RACE_COUNT");
            }
            if (!finishedSent && !spectator) Drive();
            UpdateHud();
        }

        void Drive()
        {
            int n = track.Line.Count;
            var pos = player.transform.position;
            int next = RaceMission.NearestOnLine(track.Line, pos, index);
            if ((track.Line[next] - pos).sqrMagnitude > LostDistance * LostDistance) next = NearestOnWholeLine(track.Line, pos);
            bool lapChanged = false;
            if (gated && gates != null && gates.Usable)
            {
                // the checkpoint gates (0x72f3c0 -> 0x787ce0): every gate goes to the server, which decides laps and the finish
                if (gates.Update(pos))
                {
                    S.Net.Send("race.gate", new Dictionary<string, object> { ["n"] = gates.Count });
                    if (gates.Count % track.GatesPerLap == 0)
                    {
                        lap = gates.Laps; lapChanged = true;
                        LapDone();
                    }
                }
            }
            else
            {
                if (next > n * 0.4f && next < n * 0.6f) half = true;
                if (index > n * 0.8f && next < n * 0.2f && half)
                {
                    half = false; lap++; lapChanged = true;
                    LapDone();
                }
                else if (index < n * 0.2f && next > n * 0.8f && lap > 0) { lap--; half = true; lapChanged = true; }
            }
            index = next;
            WrongWay();
            sendTimer -= Time.deltaTime;
            if (sendTimer <= 0 || lapChanged)
            {
                sendTimer = 0.5f;
                S.Net.Send("race.progress", new Dictionary<string, object> { ["lap"] = lap, ["idx"] = index, ["n"] = n, ["finished"] = finishedSent });
            }
        }

        void LapDone()
        {
            float lt = raceTime - lapStart; lapStart = raceTime;
            if (bestLap <= 0 || lt < bestLap) bestLap = lt;
            if (lap >= track.Laps)
            {
                finishedSent = true;
                int place = Place();
                var p = PlayerProfile.Current;
                GameEffects.RaceFinished(place == 1, p != null && bestLap > 0 && (p.BestLapSeconds <= 0 || bestLap < p.BestLapSeconds), place);
                bigText.text = place == 1 ? "WIN!" : "FINISH!";
                if (rig) rig.BeginGoalIn(track, 2.5f);
            }
            else RaceMission.LapEffect(lap + 1, track.Laps);
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

        /// <summary>A racer's progress 0..1: the server's "p" (gates / line / AI clock), else lap and index.</summary>
        float Progress(Dictionary<string, object> r)
        {
            int n = Mathf.Max(1, track.Line.Count);
            float total = Mathf.Max(1f, (float)n * track.Laps);
            if (r.B("finished")) return 1f;
            if (r.ContainsKey("p")) return (float)r.N("p");
            return Mathf.Clamp01((r.I("lap") * n + r.I("idx")) / total);
        }

        float MyProgress()
        {
            int n = Mathf.Max(1, track.Line.Count);
            if (finishedSent) return 1f;
            return Mathf.Clamp01((lap * n + index) / Mathf.Max(1f, (float)n * track.Laps));
        }

        Transform CarOf(int id, string name)
        {
            if (aiCars.TryGetValue(id, out var ai) && ai) return ai.transform;
            if (id == myId && !spectator && player) return player.transform;
            var remotes = S ? S.Remotes : null;
            if (remotes) foreach (var c in remotes.All) if (c && c.DriverName == name) return c.transform;
            return null;
        }

        /// <summary>The field in race order for VIEWING and the minimap.</summary>
        List<PvpSpectate.Driver> Drivers()
        {
            var list = new List<PvpSpectate.Driver>();
            if (standings != null)
                foreach (var o in standings.OfType<Dictionary<string, object>>())
                    list.Add(new PvpSpectate.Driver { Name = o.S("name"), Car = CarOf(o.I("id"), o.S("name")), Progress = Progress(o), Fraction = Progress(o), Team = o.I("team"), Done = o.B("finished") });
            else
                foreach (var kv in names) list.Add(new PvpSpectate.Driver { Name = kv.Value, Car = CarOf(kv.Key, kv.Value), Team = teams.TryGetValue(kv.Key, out var tm) ? tm : 0 });
            return list.OrderByDescending(d => d.Progress).ToList();
        }

        (int a, int b) TeamPoints()
        {
            int a = 0, b = 0;
            if (standings == null) return (0, 0);
            var fin = standings.OfType<Dictionary<string, object>>().Where(o => o.B("finished")).OrderBy(o => (float)o.N("time")).ToList();
            int field = standings.Count;
            for (int i = 0; i < fin.Count; i++) { if (fin[i].I("team") == 0) a += field - i; else b += field - i; }
            return (a, b);
        }

        void UpdateHud()
        {
            if (track == null) return;
            if (minimap != null && minimap.IsOpen)
            {
                var rivals = new List<(Transform, int)>();
                foreach (var kv in names)
                {
                    if (kv.Key == myId && !spectator) continue;
                    var c = CarOf(kv.Key, kv.Value);
                    if (c) rivals.Add((c, team ? (teams.TryGetValue(kv.Key, out var tm) ? tm : 0) : -1));
                }
                minimap.Draw(spectator ? null : player.transform, team && !spectator ? (teams.TryGetValue(myId, out var mt) ? mt : 0) : -1, rivals);
            }
            if (hud == null) return;
            int field = Mathf.Max(1, standings?.Count ?? names.Count);
            hud.Set(Place(), field, Mathf.Min(lap + 1, track.Laps), track.Laps, raceTime, bestLap);
            var rows = new List<(string, int, bool, float)>();
            if (standings != null)
                foreach (var o in standings)
                {
                    if (!(o is Dictionary<string, object> r)) continue;
                    bool me = r.I("id") == myId;
                    float prog = me ? MyProgress() : Progress(r);
                    rows.Add((r.S("name"), Mathf.RoundToInt(100f * Mathf.Clamp01(prog)), me, prog));
                }
            else rows.Add((PlayerProfile.Current?.Name ?? "", Mathf.RoundToInt(100f * MyProgress()), true, 0f));
            hud.SetOrder(rows.OrderByDescending(x => x.Item4).Select(x => (x.Item1, x.Item2, x.Item3)).ToList());
        }

        static int NearestOnWholeLine(List<Vector3> line, Vector3 p)
        {
            int best = 0; float bestD = float.MaxValue;
            for (int i = 0; i < line.Count; i++)
            {
                float d = (line[i] - p).sqrMagnitude;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        int Place()
        {
            if (standings == null) return 1;
            float mine = finishedSent ? 2f : MyProgress();
            int place = 1;
            foreach (var o in standings)
            {
                if (!(o is Dictionary<string, object> r)) continue;
                if (r.I("id") == myId) continue;
                float theirs = r.B("finished") ? 1.5f : Progress(r);
                if (theirs > mine) place++;
            }
            return place;
        }

        void Results(Dictionary<string, object> m)
        {
            done = true;
            if (recorder) recorder.Finish(ToResult(m, track, bestLap, spectator).Entries, round > 0, round);
            if (Active == this) Active = null;   // a new race may start while this one shows its result
            ReleasePlayer();
            if (spectator)
            {
                PvpSpectate.EndViewing();
                GameEffects.Screen("pvp_raceover");
                Invoke(nameof(Cleanup), 2.5f);
                ShowResults(m, track, 0f, true);
                return;
            }
            var me = PlayerProfile.Current?.Name;
            var mine = (m.L("results") ?? new List<object>()).OfType<Dictionary<string, object>>().FirstOrDefault(x => x.S("name") == me && !x.B("ai"));
            int place = mine != null && !mine.B("dnf") ? mine.I("place") : 0;
            if (place == 1) QuestTracker.Notify(QuestEvent.RaceWon); else QuestTracker.Notify(QuestEvent.RaceLost);
            var p = PlayerProfile.Current;
            if (p != null && bestLap > 0 && (p.BestLapSeconds <= 0 || bestLap < p.BestLapSeconds)) p.BestLapSeconds = bestLap;
            // 0x729cc0: pvp_retire when I did not finish, else pvp_raceover
            GameEffects.Screen(place > 0 ? "pvp_raceover" : "pvp_retire");
            if (GameEffects.Sounds) AudioLibrary.PlaySfx(place > 0 ? "sfx/SID_PVP_RACE_OVER" : "sfx/SID_PVP_RACE_RETIRE");
            if (bigText) bigText.text = place == 1 ? "WIN!" : place > 0 ? Pvp.Ordinal(place) : "DNF";
            Invoke(nameof(Cleanup), 2.5f);
            ShowResults(m, track, bestLap);
        }

        /// <summary>Tests: report the remaining gates (or laps) and the finish now (the test server accepts any time).</summary>
        internal void ForceFinish() => StartCoroutine(FinishSoon());

        IEnumerator FinishSoon()
        {
            if (!running) Go();
            if (gated && gates != null && gates.Usable)
            {
                while (gates.Count < gates.Total && !done)
                {
                    gates.Set(gates.Count + 1);
                    S.Net.Send("race.gate", new Dictionary<string, object> { ["n"] = gates.Count });
                    if (gates.Count % track.GatesPerLap == 0) { lap = gates.Laps; if (lap >= track.Laps) finishedSent = true; }
                    yield return new WaitForSeconds(0.1f);
                }
                yield break;
            }
            for (int l = lap + 1; l <= track.Laps && !done; l++)
            {
                lap = l; half = false;
                bool fin = l >= track.Laps;
                if (fin) finishedSent = true;
                S.Net.Send("race.progress", new Dictionary<string, object> { ["lap"] = l, ["idx"] = 0, ["finished"] = fin });
                yield return new WaitForSeconds(0.25f);
            }
        }

        public void Abort() => Abort(false);

        /// <summary>Real Match: remote drivers' cars (they appear with their first snapshot) wear their slot's RM look.</summary>
        void SkinRemotes()
        {
            var remotes = S ? S.Remotes : null;
            if (!remotes) return;
            foreach (var c in remotes.All)
            {
                if (!c || !rmSlots.TryGetValue(c.DriverName ?? "", out int slot) || c.GetComponent<RealMatchSkin>()) continue;
                if (RealMatchSkin.Apply(c.gameObject, slot)) rmSkinned.Add(c.gameObject);
            }
        }

        void UnskinRemotes()
        {
            foreach (var go in rmSkinned) RealMatchSkin.Remove(go);
            rmSkinned.Clear();
            rmSlots.Clear();
        }

        void Abort(bool immediate)
        {
            if (!done) SendLeave(immediate);
            PvpWaitRoom.Reset();
            Cleanup();
        }

        /// <summary>Gives the car back (controls, respawn point, ratings) unless a newer race already owns it.</summary>
        void ReleasePlayer()
        {
            if (!player || (Active != null && Active != this)) return;
            if (player.RespawnOverride == respawnFn) player.RespawnOverride = null;
            player.ExternalControl = false;
            PvpGateEffects.Clear(player);
            if ((realMatch || mission > 0) && GameFlow.Instance) GameFlow.Instance.ApplyCarStats();
            if (realMatch) RealMatchSkin.Remove(player.gameObject);
            realMatch = false; mission = 0;
        }

        /// <summary>race.gateEffect {name, type} (PlayerEvent 0x50e530): my car's effect, or the watched car's banner.</summary>
        void OnGateEffect(Dictionary<string, object> m)
        {
            int type = m.I("type");
            string who = m.S("name");
            if (!spectator && who == PlayerProfile.Current?.Name)
                PvpGateEffects.Apply(player, type, aiCars.Values.Where(c => c).Select(c => c.gameObject)
                    .Concat(FindObjectsByType<Net.RemoteCar>(FindObjectsSortMode.None).Select(c => c.gameObject)));
            else if (spectator && PvpSpectate.WatchedName == who) PvpGateEffects.Watched(type);
        }

        void Cleanup()
        {
            CancelInvoke();
            if (pathTraffic) Destroy(pathTraffic.gameObject);
            var cs = CitySession.Current; if (cs && cs.Traffic && prevTraffic > 0) cs.Traffic.MaxCars = prevTraffic;
            bool newer = Active != null && Active != this;
            done = true;
            if (Active == this) Active = null;
            foreach (var g in gatePosts) if (g) Destroy(g);
            gatePosts.Clear();
            foreach (var mat in gateMaterials) if (mat) Destroy(mat);
            gateMaterials.Clear();
            foreach (var c in aiCars.Values) if (c) Destroy(c.gameObject);
            aiCars.Clear();
            UnskinRemotes();
            if (hudRoot) Destroy(hudRoot);
            hud?.Close(); hud = null;
            minimap?.Close(); minimap = null;
            if (!newer) { PvpHud.HideRound(); PvpMissionBattle.End(); }
            if (recorder) Destroy(recorder);
            if (spectator) PvpSpectate.EndViewing();
            if (!newer)
            {
                if (rig) rig.Release();
                ReleasePlayer();
                if (GameFlow.Instance) AudioLibrary.PlayMusic(GameFlow.Instance, CityMusic.For(GameFlow.Instance.City));
            }
            if (this) Destroy(gameObject);
        }

        void OnDestroy()
        {
            // destroyed without Cleanup (scene teardown): don't leave the static pointer or the materials behind
            if (Active == this) Active = null;
            foreach (var mat in gateMaterials) if (mat) Destroy(mat);
            gateMaterials.Clear();
            hud?.Close();
            minimap?.Close();
            foreach (var c in aiCars.Values) if (c) Destroy(c.gameObject);
            UnskinRemotes();
            if (player && player.RespawnOverride == respawnFn) player.RespawnOverride = null;
        }

        /// <summary>Forget lobby state on logout / disconnect.</summary>
        public static void Reset()
        {
            PvpWaitRoom.Reset();
            PvpResults.Close();
            PvpRewardPick.Close(false);
            PvpChat.Reset();
            if (PvpLobby.IsOpen) PvpLobby.Close();
            if (LapTimeBattle.Active) LapTimeBattle.Reset();
            if (listening && S) S.RaceMessage -= OnRaceMessage;
            listening = false;
            if (Active) Active.Cleanup();
        }
    }
}
