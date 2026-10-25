using System;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// The Virtual HUV ("V-HUV" 4:4 and 8:8; DriftCity_RE/CHASES.md section 4): two teams in the city, players of one team
    /// are made the "virtual HUVs" (10971 "You've been selected to be the HUV") and the other team crashes into them.
    /// <list type="bullet">
    /// <item>Registration: QUESTDIALOG's VC44 / VC88 buttons, 10940 / 10941 / 10945 (rules), 10942 "Registration submitted.
    /// It will be started when set amount of users register.", the waiting counts VIRTUAL 3830000 (WAIT44_TEXT /
    /// WAIT88_TEXT, 11003 "Virtual HUV %dvs%d Waiting Players : %d"); 3820000 (TEXT "Virtual HUV Battle Party.", OK) is
    /// the party notice of REQUESTINVITE mode 1 (packet 0x11c).</item>
    /// <item>The match: VIRTUALCHASEPLAYINFO 900000 (the time as digit art, 0x556c30) and VIRTUALCHASING 910000 (0x547d10):
    /// the distance to the HUV as five digits + "m" (LENGTH), white digit art (WHITE_CRASH_0-9) within 500 m, red
    /// (RED_CRASH_0-9) beyond ("500m 이상되면"), with the NEXT_HUV / MY_HUV marker; the minimap's VIRTUAL_CHASING circles.</item>
    /// <item>The end: 10973 "Virtual HUV Battle Won!" / 10974 "...Lost!", VIRTUAL 3810000 (RESULT_EXP / RESULT_MITO, four item
    /// slots or SLOTX, OK; 0x548350).</item>
    /// </list>
    /// The server matches the players and judges the crashes (src/huvevents.js vhuv.*): online the registered drivers are
    /// split White / Red (AI stand-ins fill the teams after 12 s), moved into a private channel of the city, and each team has
    /// one virtual HUV at a time - a driver (10971; their own car is the HUV the other team crashes into, MY_HUV) or a stand-in
    /// whose car the match's first driver runs; the role passes on after 3 crashes taken or 45 s (NEXT_HUV); the server
    /// checks each crash (the other team's HUV, 1.5 s apart, within 25 m) and pays every driver. Offline the player's team
    /// (White) and the other team (Red) are filled with AI drivers; the Red team's virtual HUV is an AI car on the lanes that
    /// the player rams (a crash counts at most every 1.5 s), the AI drivers of both teams score on timers, and the team with
    /// more crashes after <see cref="MatchSeconds"/> wins (the player is never made the HUV offline). Server mirror:
    /// economy.js "vhuv".
    /// </summary>
    public sealed class VirtualChase : MonoBehaviour
    {
        public const int PlayInfoId = 900000, HudId = 910000, ResultId = 3810000, NoticeId = 3820000, WaitId = 3830000;
        public float MatchSeconds = 180f, CrashCooldown = 1.5f, FillSeconds = 12f;

        public static VirtualChase Active { get; private set; }
        public enum Phase { Waiting, Running, Done }
        public Phase State => phase;
        /// <summary>4 (4:4) or 8 (8:8).</summary>
        public int TeamSize { get; private set; }
        public int WhiteCrashes => white;
        public int RedCrashes => red;
        public int PlayerCrashes => mine;
        public float TimeLeft => timer;
        public HuvTarget Huv => huv;
        public int Waiting => waiting;

        Phase phase;
        float timer, cooldown, fillClock;
        int white, red, mine, waiting;
        HuvTarget huv;
        NuiView play, hud, waitView, notice;
        float noticeLeft;
        readonly List<float> whiteAi = new List<float>(), redAi = new List<float>();

        /// <summary>Registers for a match of <paramref name="teamSize"/> per team (QUESTDIALOG VC44 / VC88).</summary>
        public static VirtualChase Register(int teamSize)
        {
            if (Active) return Active;
            if (ChaseMission.Active || RushTime.Joined || DeliveryService.Active || PatrolMission.Active || UndercityStage.Active) { UiPopup.Message(MissionText.Get("10966")); return null; }
            // 10967: not while waiting in a Rush Time zone
            if (RushTime.Participating) { UiPopup.Message(MissionText.Get("10967")); return null; }
            var v = new GameObject("Virtual HUV").AddComponent<VirtualChase>();
            v.TeamSize = teamSize >= 8 ? 8 : 4;
            v.phase = Phase.Waiting;
            v.waiting = 1;
            v.Online = OnlineSession.Active;
            Active = v;
            if (v.Online)
            {
                // the server's waiting list (0x11c registration); its pushes run the match
                MissionNet.Ensure();
                OnlineSession.Instance.Net.Request("vhuv.register", new Dictionary<string, object> { ["size"] = v.TeamSize }, r =>
                {
                    if (!v) return;
                    if (r == null || !r.B("ok")) { UiPopup.Message(r?.S("error", "Registration failed.") ?? "No answer from the server."); v.phase = Phase.Done; Destroy(v.gameObject); return; }
                    v.waiting = r.I("waiting", 1);
                    v.ShowWait();
                });
            }
            ChatBox.System(MissionText.Get("10942"));
            v.ShowWait();
            return v;
        }

        void ShowWait()
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            if (!waitView) waitView = ui.Open(WaitId);
            if (!waitView) return;
            waitView.SetVisible("ID_VIRTUAL_CHASING_WAIT44_TEXT", TeamSize == 4);
            waitView.SetVisible("ID_VIRTUAL_CHASING_WAIT88_TEXT", TeamSize == 8);
            waitView.SetText(TeamSize == 4 ? "ID_VIRTUAL_CHASING_WAIT44_TEXT" : "ID_VIRTUAL_CHASING_WAIT88_TEXT", MissionText.Printf("%d", waiting));
        }

        void Update()
        {
            float dt = Time.deltaTime;
            if (Online) { NetUpdate(dt); return; }
            switch (phase)
            {
                case Phase.Waiting:
                    // AI drivers register until both teams are full
                    fillClock += dt;
                    int want = 1 + Mathf.FloorToInt(fillClock / FillSeconds * (TeamSize * 2 - 1));
                    if (want != waiting) { waiting = Mathf.Min(want, TeamSize * 2); ShowWait(); }
                    // the teams are full: the party notice (3820000) for a moment, then the match
                    if (waiting >= TeamSize * 2)
                    {
                        if (!notice) { var ui = UiManager.Instance; notice = ui ? ui.Open(NoticeId) : null; if (notice) ui.On("ID_VIRTUAL_CHASING_OK", _ => StartMatch()); noticeLeft = 3f; }
                        noticeLeft -= dt;
                        if (noticeLeft <= 0) StartMatch();
                    }
                    break;
                case Phase.Running:
                    timer -= dt;
                    cooldown -= dt;
                    if (huv == null || !huv.Car) { SpawnHuv(); if (huv == null) { Finish(); return; } }
                    huv.Tick();
                    Tick(whiteAi, ref white, dt);
                    Tick(redAi, ref red, dt);
                    Hud();
                    if (timer <= 0) Finish();
                    break;
            }
        }

        void Tick(List<float> clocks, ref int score, float dt)
        {
            for (int i = 0; i < clocks.Count; i++)
            {
                clocks[i] -= dt;
                if (clocks[i] > 0) continue;
                clocks[i] = UnityEngine.Random.Range(9f, 16f);
                score++;
            }
        }

        /// <summary>Starts at once (tests); the waiting list is filled with AI drivers.</summary>
        public void StartMatch()
        {
            if (phase != Phase.Waiting) return;
            var ui = UiManager.Instance;
            if (waitView && ui) ui.Close(waitView);
            waitView = null;
            phase = Phase.Running;
            timer = MatchSeconds;
            white = red = mine = 0;
            whiteAi.Clear(); redAi.Clear();
            for (int i = 0; i < TeamSize - 1; i++) whiteAi.Add(UnityEngine.Random.Range(9f, 16f));
            for (int i = 0; i < TeamSize; i++) redAi.Add(UnityEngine.Random.Range(9f, 16f));
            var rec = MissionRecords.Current; rec.VirtualPlayed++; rec.Save();
            OpenScreens();
            AudioLibrary.PlayMusic(this, "bgm/DriftCity_chasing.mp3");
            SpawnHuv();
            Hud();
        }

        void OpenScreens()
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            if (notice) ui.Close(notice);
            notice = null;
            play = ui.Open(PlayInfoId);
            if (play) for (int d = 0; d < 10; d++) play.SetVisible("ID_VIRTUALCHASEPLAYINFO_TIME_NUM_" + d, false);
            hud = ui.Open(HudId);
            if (hud)
                foreach (var c in hud.Controls)
                {
                    var id = c.Data.IdName ?? "";
                    if (id.Contains("_CRASH_") || id.Contains("_HUV")) c.gameObject.SetActive(false);
                }
        }

        void CloseScreens()
        {
            var ui = UiManager.Instance;
            if (ui) { if (play) ui.Close(play); if (hud) ui.Close(hud); }
            play = hud = null;
            whiteCar?.Remove(false); redCar?.Remove(false);
            whiteCar = redCar = null;
        }

        void SpawnHuv()
        {
            var session = CitySession.Current;
            var car = GameFlow.Instance ? GameFlow.Instance.PlayerCar : session ? session.Car : null;
            huv?.Remove(false);
            huv = HuvTarget.Spawn(session ? session.Traffic : null, car, HuvTarget.Pick(false, PlayerProfile.Current?.Level ?? 1), false, 80f, 220f);
            if (huv != null) huv.PlayerHit += _ => Crash();
        }

        /// <summary>The player rammed the Red team's virtual HUV (at most one crash per 1.5 s).</summary>
        public void Crash()
        {
            if (phase != Phase.Running || cooldown > 0) return;
            cooldown = CrashCooldown;
            if (Online)
            {
                // the server judges it (the other team's HUV, 1.5 s apart, within 25 m) and pushes the score
                string target = TargetName;
                if (string.IsNullOrEmpty(target) || !OnlineSession.Active) return;
                OnlineSession.Instance.Net.Request("vhuv.crash", new Dictionary<string, object> { ["target"] = target }, r => { if (r != null && r.B("ok")) mine++; });
                return;
            }
            white++; mine++;
            AudioLibrary.PlaySfx("sfx/SID_BOSSHUV_0" + UnityEngine.Random.Range(2, 6));
        }

        void Hud()
        {
            if (play)
            {
                int whole = Mathf.Max(0, Mathf.FloorToInt(timer));
                int ms = Mathf.Clamp(Mathf.FloorToInt((timer - whole) * 1000f), 0, 999);
                int m = whole % 3600 / 60, s = whole % 60;
                const string N = "ID_VIRTUALCHASEPLAYINFO_TIME_NUM_", T = "ID_VIRTUALCHASEPLAYINFO_TIME_";
                ChaseScreens.CopyArt(play, T + "6", N + (m / 10 % 10), m / 10 != 0);
                ChaseScreens.CopyArt(play, T + "5", N + (m % 10));
                ChaseScreens.CopyArt(play, T + "4", N + (s / 10));
                ChaseScreens.CopyArt(play, T + "3", N + (s % 10));
                ChaseScreens.CopyArt(play, T + "2", N + (ms / 100));
                ChaseScreens.CopyArt(play, T + "1", N + (ms % 100 / 10));
                ChaseScreens.CopyArt(play, T + "0", N + (ms % 10));
            }
            if (hud)
            {
                var car = GameFlow.Instance ? GameFlow.Instance.PlayerCar : null;
                var target = Online ? TargetTransform() : huv != null && huv.Car ? huv.Car.transform : null;
                int dist = target && car ? Mathf.Clamp(Mathf.RoundToInt(Vector3.Distance(target.position, car.transform.position)), 0, 99999) : 0;
                bool far = dist >= 500;
                string art = far ? "ID_VIRTUALCHASING_RED_CRASH_" : "ID_VIRTUALCHASING_WHITE_CRASH_";
                int[] d = { dist / 10000 % 10, dist / 1000 % 10, dist / 100 % 10, dist / 10 % 10, dist % 10 };
                bool leading = true;
                for (int k = 0; k < 5; k++)
                {
                    if (d[k] != 0 || k == 4) leading = false;
                    ChaseScreens.CopyArt(hud, "ID_VIRTUALCHASING_WHITE_NUMPOS" + k, art + d[k], !far && !leading);
                    ChaseScreens.CopyArt(hud, "ID_VIRTUALCHASING_RED_NUMPOS" + k, art + d[k], far && !leading);
                }
                hud.SetVisible("ID_VIRTUALCHASING_LENGTH0", !far);
                hud.SetVisible("ID_VIRTUALCHASING_LENGTH1", far);
                // MY_HUV while I am my team's HUV, else NEXT_HUV (the one to crash)
                hud.SetVisible("ID_VIRTUALCHASING_NEXT_HUV0", !IAmHuv);
                hud.SetVisible("ID_VIRTUALCHASING_MY_HUV0", IAmHuv);
            }
        }

        void Finish()
        {
            if (phase == Phase.Done) return;
            phase = Phase.Done;
            bool won = white > red;
            var ui = UiManager.Instance;
            if (ui) { if (play) ui.Close(play); if (hud) ui.Close(hud); }
            play = hud = null;
            huv?.Remove(false);
            huv = null;
            if (GameFlow.Instance) AudioLibrary.PlayMusic(GameFlow.Instance, CityMusic.For(GameFlow.Instance.City));
            ChatBox.System(MissionText.Get(won ? "10973" : "10974"));
            GameEffects.Screen(won ? 10 : 9);
            if (won) { var rec = MissionRecords.Current; rec.VirtualWins++; rec.Save(); }
            var p = PlayerProfile.Current;
            int lvl = p?.Level ?? 1, crashes = Mathf.Min(mine, 30), size = TeamSize;
            float seconds = MatchSeconds - Mathf.Max(0, timer);
            if (p == null) { Destroy(gameObject); return; }
            var data = new Dictionary<string, object> { ["won"] = won, ["crashes"] = crashes, ["size"] = size, ["seconds"] = (double)seconds };
            Progress.Reward("vhuv", data, () =>
            {
                long exp = MitaDrink.Apply(Reward.Exp(lvl, crashes, won)), mito = Reward.Mito(lvl, won);
                int before = p.Level;
                p.AddExp(exp); p.Mito += mito;
                return new Progress.Result { Ok = true, Exp = exp, Mito = mito, LevelUps = p.Level - before };
            }, r =>
            {
                ShowResult(r.Ok ? r.Exp : 0, r.Ok ? r.Mito : 0, r.Drop);
                Destroy(gameObject, 0.1f);
            });
        }

        /// <summary>The rebuild's pay (economy.js "vhuv"): a share per crash, half again for the winning team.</summary>
        public static class Reward
        {
            public static long Exp(int level, int crashes, bool won) { long e = 300 + 50L * level + 30L * Mathf.Clamp(crashes, 0, 30); return won ? e * 3 / 2 : e; }
            public static long Mito(int level, bool won) { long m = 2000 + 250L * level; return won ? m * 3 / 2 : m; }
        }

        /// <summary>VIRTUAL 3810000: EXP, Mito, the item slots (X when empty), OK.</summary>
        public static NuiView ShowResult(long exp, long mito, string drop)
        {
            var ui = UiManager.Instance;
            if (!ui) return null;
            var v = ui.Open(ResultId);
            if (!v) return null;
            v.SetText("ID_VIRTUAL_CHASING_RESULT_EXP", MissionText.Printf("%d", exp));
            v.SetText("ID_VIRTUAL_CHASING_RESULT_MITO", MissionText.Printf("%d", mito));
            for (int i = 0; i < 4; i++)
            {
                bool item = i == 0 && !string.IsNullOrEmpty(drop);
                v.SetVisible("ID_VIRTUAL_CHASING_RESULT_ITEM" + i, item);
                v.SetVisible("ID_VIRTUAL_CHASING_SLOTX_" + i, !item);
            }
            foreach (var k in new[] { "0", "1", "3", "4" }) v.SetVisible("ID_VC_RESULT_ITEM_COUNT" + k, false);
            ui.On("ID_VIRTUAL_CHASING_RESULT_OK", view => ui.Close(view));
            return v;
        }

        // ---------------- online (src/huvevents.js vhuv.*) ----------------

        /// <summary>Run by the server: real drivers in two teams, the HUV roles, checked crashes.</summary>
        public bool Online { get; private set; }
        /// <summary>My team online ("white" / "red").</summary>
        public string Team { get; private set; } = "white";
        /// <summary>The current virtual HUV of each team (names) and whether it is an AI stand-in.</summary>
        public string WhiteHuv { get; private set; }
        public string RedHuv { get; private set; }
        /// <summary>I am my team's virtual HUV (10971 "You've been selected to be the HUV").</summary>
        public bool IAmHuv => Online && (Team == "white" ? WhiteHuv : RedHuv) == PlayerProfile.Current?.Name;
        /// <summary>The other team's HUV, the one I crash.</summary>
        public string TargetName => Team == "white" ? RedHuv : WhiteHuv;
        int netId;
        bool netOwner, whiteIsAi, redIsAi;
        float netShareTimer;
        HuvTarget whiteCar, redCar;

        /// <summary>vhuv.* pushes from the server (MissionNet routes them here).</summary>
        public static void OnPush(string t, Dictionary<string, object> m)
        {
            var v = Active;
            if (!v || !v.Online) return;
            try { v.Handle(t, m); } catch (Exception ex) { Debug.LogException(ex); }
        }

        void Handle(string t, Dictionary<string, object> m)
        {
            switch (t)
            {
                case "vhuv.wait":
                    if (phase != Phase.Waiting) return;
                    waiting = m.I("waiting", waiting);
                    ShowWait();
                    break;
                case "vhuv.start":
                    NetStart(m);
                    break;
                case "vhuv.score":
                    if (m.I("id") != netId) return;
                    white = m.I("white"); red = m.I("red");
                    AudioLibrary.PlaySfx("sfx/SID_BOSSHUV_0" + UnityEngine.Random.Range(2, 6));
                    break;
                case "vhuv.huv.next":
                case "vhuv.huv":
                    if (m.I("id") != netId) return;
                    SetHuvs(m.O("huv"));
                    if (t == "vhuv.huv" && !netOwner && m.L("cars") is List<object> cars)
                    {
                        ApplyCar(whiteCar, cars.Count > 0 ? cars[0] : null);
                        ApplyCar(redCar, cars.Count > 1 ? cars[1] : null);
                    }
                    break;
                case "vhuv.owner":
                    if (m.I("id") != netId) return;
                    netOwner = m.S("owner") == PlayerProfile.Current?.Name;
                    if (netOwner) foreach (var c in new[] { whiteCar, redCar }) if (c != null && c.Car) { ShowCar(c.Car, true); c.Car.SetRemote(false); }
                    break;
                case "vhuv.end":
                    if (m.I("id") != netId) return;
                    NetFinish(m);
                    break;
            }
        }

        static float F(object o) => o is double d ? (float)d : o is long l ? l : o is int i ? i : 0f;

        static void ShowCar(TrafficCar c, bool on)
        {
            if (!c) return;
            foreach (var r in c.GetComponentsInChildren<Renderer>(true)) r.enabled = on;
            foreach (var col in c.GetComponentsInChildren<Collider>(true)) col.enabled = on;
        }

        static void ApplyCar(HuvTarget t, object state)
        {
            if (t == null || !t.Car || !(state is List<object> s) || s.Count < 5) return;
            if (!t.Car.Remote) t.Car.SetRemote(true);
            bool first = !t.Car.HasRemoteState;
            t.Car.ApplyRemote(new Vector3(F(s[0]), F(s[1]), F(s[2])), F(s[3]), F(s[4]));
            if (first) ShowCar(t.Car, true);
        }

        /// <summary>vhuv.start: the teams, my team, the HUVs, the private channel (the world shows the other drivers).</summary>
        void NetStart(Dictionary<string, object> m)
        {
            netId = m.I("id");
            TeamSize = m.I("size", TeamSize);
            Team = m.S("team", "white");
            netOwner = m.S("owner") == PlayerProfile.Current?.Name;
            var ui = UiManager.Instance;
            if (waitView && ui) ui.Close(waitView);
            waitView = null;
            phase = Phase.Running;
            timer = (float)m.N("seconds", MatchSeconds);
            white = red = mine = 0;
            var rec = MissionRecords.Current; rec.VirtualPlayed++; rec.Save();
            OpenScreens();
            AudioLibrary.PlayMusic(this, "bgm/DriftCity_chasing.mp3");
            SetHuvs(m.O("huv"));
        }

        /// <summary>The HUV roles changed: 10971 when it is me; the stand-in HUVs' cars come and go.</summary>
        void SetHuvs(Dictionary<string, object> h)
        {
            if (h == null) return;
            bool wasHuv = IAmHuv;
            WhiteHuv = h.S("white"); RedHuv = h.S("red");
            whiteIsAi = h.B("whiteAi"); redIsAi = h.B("redAi");
            if (IAmHuv && !wasHuv) ChatBox.System(MissionText.Get("10971"));
            whiteCar = HuvCar(whiteCar, whiteIsAi);
            redCar = HuvCar(redCar, redIsAi);
        }

        /// <summary>A stand-in HUV's car (the owner runs it, the others mirror it); none for a driver who is the HUV.</summary>
        HuvTarget HuvCar(HuvTarget car, bool ai)
        {
            if (!ai) { car?.Remove(false); return null; }
            if (car != null && car.Car) return car;
            var session = CitySession.Current;
            var me = GameFlow.Instance ? GameFlow.Instance.PlayerCar : session ? session.Car : null;
            var t = HuvTarget.Spawn(session ? session.Traffic : null, me, HuvTarget.Pick(false, PlayerProfile.Current?.Level ?? 1, 0), false, 80f, 220f);
            if (t == null) return null;
            if (!netOwner) { t.Car.SetRemote(true); ShowCar(t.Car, false); }
            t.PlayerHit += _ => { if (t == (Team == "white" ? redCar : whiteCar)) Crash(); };
            return t;
        }

        /// <summary>The car I chase: the other team's stand-in HUV, or that driver's car in the channel.</summary>
        Transform TargetTransform()
        {
            var ai = Team == "white" ? redCar : whiteCar;
            if (ai != null && ai.Car) return ai.Car.transform;
            var name = TargetName;
            var remotes = OnlineSession.Instance ? OnlineSession.Instance.Remotes : null;
            if (remotes == null || string.IsNullOrEmpty(name)) return null;
            foreach (var rc in remotes.All) if (rc && rc.DriverName == name) return rc.transform;
            return null;
        }

        void NetUpdate(float dt)
        {
            if (phase != Phase.Running) return;
            timer -= dt;
            cooldown -= dt;
            foreach (var c in new[] { whiteCar, redCar }) if (c != null && c.Car && !c.Car.Remote) c.Tick();
            // a driver who is the other team's HUV: touching their car is the crash (the server checks the distance)
            var car = GameFlow.Instance ? GameFlow.Instance.PlayerCar : CitySession.Current ? CitySession.Current.Car : null;
            var target = TargetTransform();
            bool aiTarget = (Team == "white" ? redCar : whiteCar) != null;
            if (!aiTarget && car && target && Vector3.Distance(car.transform.position, target.position) < 5.5f) Crash();
            netShareTimer -= dt;
            if (netOwner && netShareTimer <= 0 && OnlineSession.Active && (whiteCar != null || redCar != null))
            {
                netShareTimer = ChaseMission.ShareInterval;
                object S(HuvTarget t) => t != null && t.Car ? (object)t.Car.NetState().Cast<object>().ToList() : null;
                OnlineSession.Instance.Net.Send("vhuv.huv", new Dictionary<string, object> { ["cars"] = new List<object> { S(whiteCar), S(redCar) } });
            }
            Hud();
        }

        /// <summary>vhuv.end: the score, my pay (VIRTUAL 3810000), back to the channel I came from.</summary>
        void NetFinish(Dictionary<string, object> m)
        {
            if (phase == Phase.Done) return;
            phase = Phase.Done;
            bool won = m.B("won");
            white = m.I("white"); red = m.I("red");
            CloseScreens();
            if (GameFlow.Instance) AudioLibrary.PlayMusic(GameFlow.Instance, CityMusic.For(GameFlow.Instance.City));
            ChatBox.System(MissionText.Get(won ? "10973" : "10974"));
            GameEffects.Screen(won ? 10 : 9);
            if (won) { var rec = MissionRecords.Current; rec.VirtualWins++; rec.Save(); }
            if (m.O("driver") != null) OnlineSession.ApplyDriver(m.O("driver"));
            long exp = 0, mitoGot = 0; string drop = null;
            foreach (var o in m.L("rows") ?? new List<object>())
                if (o is Dictionary<string, object> row && row.S("name") == PlayerProfile.Current?.Name) { exp = (long)row.N("exp"); mitoGot = (long)row.N("mito"); drop = row.S("drop"); mine = row.I("crashes"); }
            ShowResult(exp, mitoGot, drop);
            Destroy(gameObject, 0.1f);
        }

        /// <summary>Ends the match now (tests).</summary>
        public void FinishNow() { if (phase == Phase.Running) timer = 0f; }

        /// <summary>Leaves the waiting list or the match (logout, travel).</summary>
        public void Cancel()
        {
            if (phase == Phase.Done) return;
            if (Online && OnlineSession.Active) OnlineSession.Instance.Net.Request(phase == Phase.Waiting ? "vhuv.cancel" : "vhuv.leave", new Dictionary<string, object>(), null);
            phase = Phase.Done;
            Destroy(gameObject);
        }

        void OnDestroy()
        {
            if (Active == this) Active = null;
            var ui = UiManager.Instance;
            if (ui) { if (play) ui.Close(play); if (hud) ui.Close(hud); if (waitView) ui.Close(waitView); if (notice) ui.Close(notice); }
            huv?.Remove(false);
            whiteCar?.Remove(false); redCar?.Remove(false);
        }
    }
}
