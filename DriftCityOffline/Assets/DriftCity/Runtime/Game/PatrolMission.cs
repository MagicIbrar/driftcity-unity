using System;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// Patrols: the "patrol" rows of World/Data/Instant_Client.tdf (kinds move 8, move_event 9, move_ROO 10 and the
    /// party patrols party_move 0x20 / party_move_ROO 0x21, see <see cref="InstantRow"/>).
    /// <list type="bullet">
    /// <item>A row's first post is the officer next to a station ("Heather Mission_001..004" stand beside Station 1..4;
    /// the World Patrol rows start at post_Nth_WPE_*), the following column-15 posts (40, 80 or 160) are the patrol
    /// loop around the city ("Officer Heather's Patrol Instant Mission-1.." or post_Nth_WP001..160).</item>
    /// <item>Every post reached prints 10127 "[%s] Destination &lt;%s&gt; patrolled." (0x6fae40) and is worth
    /// column 12 EXP (+0x1130); finishing the loop adds column 14 (+0x1138): 10462 "%dEXP point per post, %dEXP when
    /// all completed." in the patrol's confirm dialog (0x5c4f90 / 0x5c5330 fill PATROL 3380000: description, reward
    /// lines, current / max posts at +0x1e0 / +0x1e4, Abandon and Close).</item>
    /// <item>In the running-mission list (240000) the patrol shows "%d/%d achieved" under the "Patrol" tab (11871).</item>
    /// </list>
    /// The server picked and paid patrols; offline the officer offers the patrol through the "Call Mission" notice
    /// when the car comes within <see cref="OfferRadius"/> of the officer's post, a post counts within
    /// <see cref="PostRadius"/> (both this rebuild's own), and the EXP (column 13 as Mito per post, an inference) is
    /// paid when the patrol ends: completed, abandoned, or left with the city. Party patrols (PARTYPATROL, party_move rows)
    /// online in a party (DriftCity_RE/PARTYPLAY.md): the leader takes the row and the server starts it for every member
    /// (11156; client packets 0xc24 / 0xc26 / 0xc27 / 0xc1e), the posts are shared (each post counts once, for whoever
    /// reached it: "Post %u" on the party bar and the list's POST01-04), the PARTYPATROL timer 3880000 counts down and every
    /// post adds bonus seconds (BONUSTIME, sound 0x4ece), and the server pays every member and sends 3890000 with up to four
    /// lines (11157 completed, 11158 the time ran out, 11159 the leader quit). Offline (or outside a party) party rows run
    /// solo and end on 3890000 with one line. Team ROO drivers get the ROO rows instead (KindFor). Server mirror: economy.js kind
    /// "patrol", src/partyplay.js for the party patrol.
    /// </summary>
    public sealed class PatrolMission : MonoBehaviour
    {
        public const int ConfirmId = 3380000, PartyResultId = 3890000, PartyTimerId = 3880000;
        public float OfferRadius = 60f, PostRadius = 30f, RefuseCooldown = 120f;

        /// <summary>The patrol being driven, or null.</summary>
        public static PatrolMission Active { get; private set; }
        public static PatrolMission Instance { get; private set; }

        public CitySession Session;
        public string City;
        public InstantRow Row { get; private set; }
        public List<CityMapInfo.Icon> Posts { get; private set; } = new List<CityMapInfo.Icon>();
        public int Visited { get; private set; }
        public float Elapsed;
        public bool Running => running;
        /// <summary>A party patrol through the server (shared posts, the 3880000 timer).</summary>
        public bool PartyRun { get; private set; }
        /// <summary>Seconds left on the party patrol's clock (the server's).</summary>
        public float PartyClock;
        public NuiView TimerView => timerView;
        public Dictionary<string, object> LastPartyEnd { get; private set; }

        NuiView timerView;
        float bonusShow;
        int bonusSeconds;
        readonly HashSet<int> sentPosts = new HashSet<int>();

        bool running;
        bool[] done;
        int next;
        MissionBeacon beacon;
        readonly Dictionary<int, float> refused = new Dictionary<int, float>();
        float offerCheck;
        NuiView call;
        InstantRow offered;
        float offerAge;

        public static PatrolMission Attach(CitySession session, string city)
        {
            if (!session) return null;
            if (!session.TryGetComponent(out PatrolMission m)) m = session.gameObject.AddComponent<PatrolMission>();
            m.Session = session;
            m.City = city;
            Instance = m;
            return m;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (call && UiManager.Instance) UiManager.Instance.Close(call);
            // leaving the city ends the patrol: the posts driven so far are still paid
            if (running && PartyRun) { Social.SocialNet.Request("patrol.party.quit", null, null); Social.PartyWindow.SetPosts(null); }
            else if (running) Finish(false, quiet: true);
            Cleanup();
        }

        /// <summary>
        /// The patrols offered in <paramref name="city"/> to a driver of <paramref name="level"/>: per officer post the
        /// row with the highest level requirement the driver meets (solo kinds, then the party patrols).
        /// </summary>
        public static List<InstantRow> RowsFor(string city, int level) => RowsFor(city, level, Factions.Current);

        /// <summary>
        /// The offer 0x6fb8d0 gives Team ROO drivers only the move_ROO / party_move_ROO rows (kinds 10 / 0x21: Monica's
        /// Scouting, Officer Min, Eugene's Treasure Hunt IV) and OMD drivers only move / party_move (8 / 0x20); move_event
        /// rows go to both.
        /// </summary>
        public static bool KindFor(string kind, int faction) =>
            kind == "move_event" || (faction == Factions.Roo ? kind == "move_ROO" || kind == "party_move_ROO" : kind == "move" || kind == "party_move");

        public static List<InstantRow> RowsFor(string city, int level, int faction)
        {
            var rows = InstantRow.All.Where(r => r.IsPatrol && KindFor(r.Kind, faction) &&
                                                 r.Posts.Count > 1 && r.MaxPosts > 0 && r.FitsLevel(level) && MissionPosts.CityOf(r.Posts[0]) == city).ToList();
            return rows.GroupBy(r => (r.Posts[0], r.IsParty))
                       .Select(g => g.OrderByDescending(r => r.MinLevel).ThenByDescending(r => r.CompleteExp).First())
                       .Where(r => MissionPosts.Find(r.Posts[0]) != null)
                       .ToList();
        }

        // ---------------- offer ----------------

        void Update()
        {
            if (running) { Run(); return; }
            if (pendingParty && Time.unscaledTime > pendingUntil) pendingParty = false;
            if (pendingParty) return;
            if (call)
            {
                offerAge += Time.deltaTime;
                if (MissionUi.TabPressed()) { UiSounds.Click(); call.Click("ID_INSTANTMISSION_TABBUTTON"); }
                else if (offerAge > 15f) { UiManager.Instance?.Close(call); call = null; Refuse(offered); }
                return;
            }
            offerCheck -= Time.deltaTime;
            if (offerCheck > 0) return;
            offerCheck = 0.5f;
            var flow = GameFlow.Instance;
            var car = flow ? flow.PlayerCar : null;
            if (!car || GameFlow.MissionActive || ChaseMission.Active || DeliveryService.Active || (flow && flow.MenuOpen) || MissionUi.ScriptOpen ||
                DeliveryService.BoardOpen || (InstantMissions.Instance && InstantMissions.Instance.Current != null) ||
                StepQuests.Running(PlayerProfile.Current) != null) return;   // a step quest owns the car meanwhile
            var ui = UiManager.Instance;
            if (!ui || ui.Find(InstantMissions.CallId) || ui.Find(4400000)) return;
            var p = car.transform.position;
            foreach (var r in RowsFor(City, PlayerProfile.Current?.Level ?? 1))
            {
                if (refused.TryGetValue(r.Index, out float until) && Time.time < until) continue;
                // in a party online, only the leader takes the party patrol for everyone
                if (r.IsParty && PartyOnline && !Social.PartyWindow.IAmLeader) continue;
                var officer = MissionPosts.Find(r.Posts[0]);
                if (officer == null || MissionPosts.Flat(p, officer.Position) > OfferRadius) continue;
                Offer(r);
                return;
            }
        }

        /// <summary>The officer's "Call Mission" notice for a patrol.</summary>
        public bool Offer(InstantRow r)
        {
            if (running || r == null) return false;
            offered = r;
            offerAge = 0;
            call = MissionUi.CallMission(() => { call = null; Begin(offered); }, () => { call = null; Refuse(offered); });
            return call;
        }

        void Refuse(InstantRow r) { if (r != null) refused[r.Index] = Time.time + RefuseCooldown; offered = null; }

        // ---------------- run ----------------

        /// <summary>Online in a party of 2+: party rows are played by the whole party through the server.</summary>
        static bool PartyOnline => Social.SocialNet.Online && Social.PartyWindow.InParty;
        bool pendingParty;
        float pendingUntil;

        public void Begin(InstantRow r)
        {
            if (r == null) return;
            if (r.IsParty && PartyOnline) { RequestParty(r); return; }
            Cleanup();
            Row = r;
            Posts = r.Posts.Skip(1).Take(r.MaxPosts).Select(MissionPosts.Find).Where(i => i != null).ToList();
            if (Posts.Count == 0) { Row = null; return; }
            done = new bool[Posts.Count];
            Visited = 0;
            Elapsed = 0;
            // the loop is driven from the post nearest the officer
            var officer = MissionPosts.Find(r.Posts[0]);
            next = 0;
            if (officer != null)
            {
                float best = float.MaxValue;
                for (int i = 0; i < Posts.Count; i++) { float d = MissionPosts.Flat(Posts[i].Position, officer.Position); if (d < best) { best = d; next = i; } }
            }
            running = true;
            Active = this;
            Net.ChatBox.System(r.IsParty ? GameTables.Text("11156") : MissionText.Get("10122", r.Title));
            MissionUi.Script(r.Ment, r.Title, false, null, null);
            beacon = MissionBeacon.Create("Patrol Beacon", @"game\cylinder_blue.nif", new Color(0.2f, 0.6f, 1f, 0.35f));
            beacon.MoveTo(Posts[next].Position);
            UpdateTracker();
        }

        void Run()
        {
            var car = GameFlow.Instance ? GameFlow.Instance.PlayerCar : null;
            if (!car) return;
            Elapsed += Time.deltaTime;
            if (PartyRun) { TickTimer(); PatrolBanners(); }
            if (ChaseMission.Active || RaceMission.Active || OnlineRace.Active) { GiveUp(); return; }
            var p = car.transform.position;
            for (int i = 0; i < Posts.Count; i++)
            {
                if (done[i] || MissionPosts.Flat(p, Posts[i].Position) > PostRadius || Mathf.Abs(p.y - Posts[i].Position.y) > 40f) continue;
                if (PartyRun)
                {
                    // 0xc26: the server counts the post once, for whoever reached it first
                    if (sentPosts.Add(i)) ReportPost(i);
                    continue;
                }
                done[i] = true;
                Visited++;
                AudioLibrary.PlaySfx("sfx/SID_CALLSIGN_ACCEPT");
                Net.ChatBox.System(MissionText.Get("10127", Row.Title, Posts[i].Name));
                if (Visited >= Posts.Count) { Finish(true); return; }
                if (i == next || done[next]) next = NextOpen(i);
                beacon.MoveTo(Posts[next].Position);
                UpdateTracker();
                RefreshConfirm();
            }
        }

        int NextOpen(int from)
        {
            for (int k = 1; k <= Posts.Count; k++) { int i = (from + k) % Posts.Count; if (!done[i]) return i; }
            return from;
        }

        string ProgressText() => MissionText.Get("10461", Visited, Posts.Count);

        void UpdateTracker()
        {
            if (!running) { MissionTracker.Remove(this); return; }
            MissionTracker.Set(this, GameTables.Text("11871"), Row.Title, ProgressText(), ShowConfirm);
        }

        /// <summary>PATROL 3380000: description, reward lines, posts so far / posts, Abandon and Close.</summary>
        public void ShowConfirm()
        {
            if (!running) return;
            var ui = UiManager.Instance;
            var old = ui.Find(ConfirmId);
            if (old) ui.Close(old);
            var v = ui.Open(ConfirmId);
            if (!v) return;
            RefreshConfirm();
            ui.On("ID_PATROL_CONFIRM_ESC", view => ui.Close(view));
            ui.On("ID_PATROL_CONFIRM_GIVEUP", view => MissionUi.Confirm(MissionText.Get("10194"), () => { ui.Close(view); GiveUp(); }));
        }

        void RefreshConfirm()
        {
            var v = UiManager.Instance ? UiManager.Instance.Find(ConfirmId) : null;
            if (!v || Row == null) return;
            var speaker = Core.NpcScript.Parse(Row.Ment).Select(pg => pg.Speaker).FirstOrDefault(s => !string.IsNullOrEmpty(s));
            v.SetText("ID_PATROL_CONFIRM_DESC", Row.Title + (speaker != null ? "\n" + speaker : ""));
            v.SetText("ID_PATROL_CONFIRM_REWARD1", MissionText.Get("10462", Row.PostExp, Row.CompleteExp));
            v.SetText("ID_PATROL_CONFIRM_REWARD2", Row.PostMito > 0 ? MissionText.Printf("%d Mito", Row.PostMito) + " / post" : "");
            v.SetText("ID_PATROL_CONFIRM_CUR", Visited.ToString("000"));
            v.SetText("ID_PATROL_CONFIRM_MAX", Posts.Count.ToString("000"));
        }

        public void GiveUp()
        {
            if (!running) return;
            if (PartyRun) { Social.SocialNet.Request("patrol.party.quit", null, null); return; }   // the server ends it (0xc1e)
            Finish(false);
        }

        /// <summary>Test / tool hook: post <paramref name="i"/> of a party patrol reached (reported to the server).</summary>
        public void DebugPost(int i)
        {
            if (!running || !PartyRun || done == null || i < 0 || i >= done.Length || done[i]) return;
            sentPosts.Add(i);
            ReportPost(i);
        }

        /// <summary>Pays the posts driven (plus the completion EXP for the whole loop) and ends the patrol.</summary>
        void Finish(bool complete, bool quiet = false)
        {
            var r = Row;
            int posts = Visited;
            float seconds = Elapsed;
            running = false;
            Cleanup();
            var rec = MissionRecords.Current;
            rec.PatrolPosts += posts;
            if (complete) rec.PatrolsCompleted++;
            rec.Save();
            AudioLibrary.PlaySfx(complete ? "sfx/SID_QUEST_SUCCESS" : "sfx/SID_QUEST_CANCEL");
            if (complete) GameEffects.InstantMission(true, false);   // 0x6f6560: im_success
            Net.ChatBox.System(r.IsParty ? GameTables.Text(complete ? "11157" : "11158") : MissionText.Get(complete ? "10123" : "10124", r.Title));
            var p = PlayerProfile.Current;
            if (p == null || posts == 0) return;
            var data = new Dictionary<string, object> { ["row"] = r.Index, ["posts"] = posts, ["seconds"] = (double)seconds };
            Progress.Reward("patrol", data, () =>
            {
                long exp = (long)r.PostExp * posts + (posts >= r.MaxPosts ? r.CompleteExp : 0), mito = (long)r.PostMito * posts;
                int before = p.Level;
                p.AddExp(exp); p.Mito += mito;
                return new Progress.Result { Ok = true, Exp = exp, Mito = mito, LevelUps = p.Level - before };
            }, res =>
            {
                if (quiet || !res.Ok) return;
                if (r.IsParty) ShowPartyResult(posts, res.Exp, seconds);
                else UiPopup.Message($"{r.Title}\n{MissionText.Get("10461", posts, r.MaxPosts)}\nEXP +{res.Exp}" + (res.Mito > 0 ? $"   Mito +{res.Mito}" : "") +
                                     (res.LevelUps > 0 ? $"\nLevel up! You are now level {PlayerProfile.Current.Level}." : ""));
            });
        }

        /// <summary>PARTYPATROL 3890000 for a party of one: name, posts, EXP, clear time.</summary>
        static void ShowPartyResult(int posts, long exp, float seconds) =>
            ShowPartyResult(new List<(string, int, long)> { (PlayerProfile.Current?.Name ?? "Driver", posts, exp) }, seconds);

        /// <summary>PARTYPATROL 3890000 (0xc1e): up to four lines of name, posts, rescues, EXP and items; clear and wait time.</summary>
        public static NuiView ShowPartyResult(IList<(string name, int posts, long exp)> rows, float seconds, int left = 0)
        {
            var ui = UiManager.Instance;
            if (!ui) return null;
            var old = ui.Find(PartyResultId);
            if (old) ui.Close(old);
            var v = ui.Open(PartyResultId);
            if (!v) return null;
            string[] slot = { "1ST", "2ND", "3RD", "4TH" };
            for (int i = 0; i < 4; i++)
            {
                bool on = i < rows.Count;
                foreach (var part in new[] { "NAME", "POST", "RESCUE", "EXP", "NOITEM" }) v.SetVisible($"ID_PARTYPATROL_RESULT_{part}{slot[i]}", on);
                foreach (var part in new[] { "ITEMBASE", "POSITEM", "ITEMCNT" }) v.SetVisible($"ID_PARTYPATROL_RESULT_{part}{slot[i]}", false);
                if (!on) continue;
                v.SetText($"ID_PARTYPATROL_RESULT_NAME{slot[i]}", rows[i].name);
                v.SetText($"ID_PARTYPATROL_RESULT_POST{slot[i]}", rows[i].posts.ToString());
                v.SetText($"ID_PARTYPATROL_RESULT_RESCUE{slot[i]}", "0");
                v.SetText($"ID_PARTYPATROL_RESULT_EXP{slot[i]}", rows[i].exp.ToString());
            }
            v.SetText("ID_PARTYPATROL_RESULT_CLEARTIME", (Mathf.FloorToInt(seconds) / 60).ToString());
            v.SetText("ID_PARTYPATROL_RESULT_CLEARTIME_SEC", (Mathf.FloorToInt(seconds) % 60).ToString("00"));
            v.SetText("ID_PARTYPATROL_RESULT_WAITTIME", Mathf.Max(0, left).ToString());   // "Time Left": the clock's seconds
            ui.On("ID_PARTYPATROL_RESULT_OK", view => ui.Close(view));
            ui.On("ID_PARTYPATROL_RESULT_CLOSE", view => ui.Close(view));
            return v;
        }

        // ---------------- party patrol (online) ----------------

        /// <summary>The leader takes the party row from the officer: the server starts it for every member (0xc24).</summary>
        void RequestParty(InstantRow r)
        {
            if (!Social.PartyWindow.IAmLeader) { Net.ChatBox.System("Not a group leader"); return; }   // 20022
            pendingParty = true;
            pendingUntil = Time.unscaledTime + 10f;
            Social.SocialNet.Request("patrol.party.start", new Dictionary<string, object> { ["row"] = r.Index, ["posts"] = r.MaxPosts }, res =>
            {
                if (res != null && res.B("ok")) return;
                pendingParty = false;
                string err = Social.SocialNet.Err(res);
                Net.ChatBox.System(err);
                UiPopup.Message(err);
            });
        }

        /// <summary>"party.patrol.*" from the server (src/partyplay.js).</summary>
        public static void OnPartyPush(string t, Dictionary<string, object> m)
        {
            var me = Instance;
            if (!me) return;
            switch (t)
            {
                case "party.patrol.start": me.BeginParty(m); break;
                case "party.patrol.post": me.PartyPost(m); break;
                case "party.patrol.left": Net.ChatBox.System($"{m.S("name")} has left the Party Patrol."); break;
                case "party.patrol.end": me.EndParty(m); break;
            }
        }

        void BeginParty(Dictionary<string, object> m)
        {
            pendingParty = false;
            var r = InstantRow.All.FirstOrDefault(x => x.Index == m.I("row"));
            if (r == null) { Social.SocialNet.Request("patrol.party.quit", null, null); return; }
            if (running) { running = false; Cleanup(); }
            if (call && UiManager.Instance) { UiManager.Instance.Close(call); call = null; }
            Cleanup();
            Row = r;
            Posts = r.Posts.Skip(1).Take(r.MaxPosts).Select(MissionPosts.Find).Where(i => i != null).ToList();
            if (Posts.Count == 0) { Row = null; Social.SocialNet.Request("patrol.party.quit", null, null); return; }
            done = new bool[Posts.Count];
            sentPosts.Clear();
            Visited = 0;
            Elapsed = 0;
            PartyRun = true;
            PartyClock = (float)m.N("seconds", 300);
            LastPartyEnd = null;
            var officer = MissionPosts.Find(r.Posts[0]);
            next = 0;
            var car = GameFlow.Instance ? GameFlow.Instance.PlayerCar : null;
            // each driver starts from the post nearest to them (the officer for the leader)
            var from = car ? car.transform.position : officer != null ? officer.Position : Vector3.zero;
            float best = float.MaxValue;
            for (int i = 0; i < Posts.Count; i++) { float d = MissionPosts.Flat(Posts[i].Position, from); if (d < best) { best = d; next = i; } }
            running = true;
            Active = this;
            Net.ChatBox.System(GameTables.Text("11156"));   // "Party Patrol Start!"
            countShown = false;
            GameEffects.PatrolAlert(true);   // 0x4f4210: party_soonchal (2.5 s)
            if (Social.PartyWindow.IAmLeader) MissionUi.Script(r.Ment, r.Title, false, null, null);
            beacon = MissionBeacon.Create("Patrol Beacon", @"game\cylinder_blue.nif", new Color(0.2f, 0.6f, 1f, 0.35f));
            beacon.MoveTo(Posts[next].Position);
            var counts = new Dictionary<string, int>();
            foreach (var o in m.L("members") ?? new List<object>()) counts[o.ToString()] = 0;
            Social.PartyWindow.SetPosts(counts);
            OpenTimer();
            UpdateTracker();
        }

        void ReportPost(int index)
        {
            Social.SocialNet.Request("patrol.party.post", new Dictionary<string, object> { ["index"] = index }, res =>
            {
                // refused (too fast, already taken): it may be reported again when the car comes back
                if (res != null && !res.B("ok") && index < done.Length && !done[index]) sentPosts.Remove(index);
            });
        }

        /// <summary>0xc26 / 0xc27: a post of the loop is done; the member's "Post %u", the shared count, bonus seconds.</summary>
        void PartyPost(Dictionary<string, object> m)
        {
            if (!running || !PartyRun || done == null) return;
            int i = m.I("index");
            if (i < 0 || i >= done.Length || done[i]) return;
            done[i] = true;
            Visited = m.I("total", Visited + 1);
            bool mine = m.S("by") == PlayerProfile.Current?.Name;
            if (mine)
            {
                AudioLibrary.PlaySfx("sfx/SID_CALLSIGN_ACCEPT");
                Net.ChatBox.System(MissionText.Get("10127", Row.Title, Posts[i].Name));
            }
            PartyClock = (float)m.N("seconds", PartyClock);
            bonusSeconds = m.I("bonus");
            bonusShow = 3f;
            AudioLibrary.PlaySfx("sfx/SID_PVP_RACE_LAPS");   // 0x4ece with the bonus time (0x4f43ea / 0x501d79)
            var counts = new Dictionary<string, int>();
            foreach (var o in (m.L("counts") ?? new List<object>()).OfType<Dictionary<string, object>>()) counts[o.S("name")] = o.I("posts");
            Social.PartyWindow.SetPosts(counts);
            if (i == next || done[next]) next = NextOpen(i);
            if (beacon && !done[next]) beacon.MoveTo(Posts[next].Position);
            UpdateTracker();
            RefreshConfirm();
        }

        /// <summary>0xc1e: the server ended the party patrol and paid everyone; 3890000 with the members' lines.</summary>
        void EndParty(Dictionary<string, object> m)
        {
            if (!PartyRun) return;
            LastPartyEnd = m;
            bool complete = m.B("complete");
            string reason = m.S("reason");
            running = false;
            PartyRun = false;
            Cleanup();
            Social.PartyWindow.SetPosts(null);
            var rows = new List<(string, int, long)>();
            int myPosts = 0;
            foreach (var o in (m.L("rows") ?? new List<object>()).OfType<Dictionary<string, object>>())
            {
                rows.Add((o.S("name"), o.I("posts"), (long)o.N("exp")));
                if (o.S("name") == PlayerProfile.Current?.Name) myPosts = o.I("posts");
            }
            var rec = MissionRecords.Current;
            rec.PatrolPosts += myPosts;
            if (complete) rec.PatrolsCompleted++;
            rec.Save();
            if (m.O("driver") != null) Net.OnlineSession.ApplyDriver(m.O("driver"));
            AudioLibrary.PlaySfx(complete ? "sfx/SID_QUEST_SUCCESS" : "sfx/SID_QUEST_CANCEL");
            // 11157 completed, 11159 the leader quit, 11158 failed (the time ran out, or I left)
            Net.ChatBox.System(GameTables.Text(complete ? "11157" : reason == "leader" ? "11159" : "11158"));
            // 0x6f6560 im_success on the reward; 0x4efa30 (0x3e6) im_failed when the patrol failed (not when the leader quit)
            if (complete || reason != "leader") GameEffects.InstantMission(complete, false);
            ShowPartyResult(rows, (float)m.N("seconds"), m.I("left"));
        }

        // ---------------- PARTYPATROL timer 3880000 ----------------

        void OpenTimer()
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            if (timerView) ui.Close(timerView);
            timerView = ui.Open(PartyTimerId);
            if (!timerView) return;
            foreach (var c in timerView.Controls)
            {
                string id = c.Data.IdName ?? "";
                // TIMENUM01-10 are the digit art (0-9); BONUSTIME01-10 / 11-20 the bonus digits (tens / ones)
                if (id.Contains("TIMENUM") || id.Contains("BONUSTIME") || id.Contains("BASE2ND") || id.Contains("BASE3TH")) c.gameObject.SetActive(false);
                else c.gameObject.SetActive(true);
            }
            TickTimer();
        }

        /// <summary>The clock as digit art (TIME1ST/2ND minutes, TIME3RD/4TH seconds, COMMA, 5TH-7TH thousandths) and the
        /// bonus seconds of the last post (BONUSTIME tens 01-10 and ones 11-20) for 3 s.</summary>
        void TickTimer()
        {
            PartyClock = Mathf.Max(0f, PartyClock - Time.deltaTime);
            if (!timerView) return;
            int ms = Mathf.Clamp(Mathf.FloorToInt(PartyClock * 1000f), 0, 99 * 60000 + 59999);
            int mm = ms / 60000, ss = ms / 1000 % 60, f = ms % 1000;
            int[] d = { mm / 10, mm % 10, ss / 10, ss % 10, f / 100, f / 10 % 10, f % 10 };
            string[] slots = { "ID_PARTYPATROL_TIME1ST", "ID_PARTYPATROL_2ND", "ID_PARTYPATROL_TIME3RD", "ID_PARTYPATROL_TIME4TH", "ID_PARTYPATROL_5TH", "ID_PARTYPATROL_TIME6TH", "ID_PARTYPATROL_TIME7TH" };
            for (int k = 0; k < slots.Length; k++)
            {
                var dst = MissionUi.Image(timerView, slots[k]);
                var src = MissionUi.Image(timerView, $"ID_PARTYPATROL_TIMENUM{d[k] + 1:00}");
                if (!dst || !src) continue;
                dst.texture = src.texture; dst.uvRect = src.uvRect; dst.enabled = src.texture;
            }
            bool showBonus = bonusShow > 0 && bonusSeconds > 0;
            if (bonusShow > 0) bonusShow -= Time.deltaTime;
            int b = Mathf.Clamp(bonusSeconds, 0, 99);
            timerView.SetVisible("ID_PARTYPATROL_BASE2ND", showBonus);
            for (int k = 1; k <= 20; k++)
            {
                bool on = showBonus && (k <= 10 ? k - 1 == b / 10 : k - 11 == b % 10);
                timerView.SetVisible($"ID_PARTYPATROL_BONUSTIME{k:00}", on);
            }
        }

        bool countShown;

        /// <summary>0x61d7be (the party patrol's tick): party_soonchal again whenever it ends while posts are open; below
        /// 10.5 s left SID_PATROL_PARTY_COUNT + pvp_tencount (10 s) once, taken off when a post's bonus time lifts the clock.</summary>
        void PatrolBanners()
        {
            if (Visited < Posts.Count) GameEffects.PatrolAlert();
            if (PartyClock < 10.5f && !countShown) { countShown = true; GameEffects.PatrolCount(); }
            else if (PartyClock >= 10.5f && countShown) { countShown = false; GameEffects.PatrolCountStop(); }
        }

        void Cleanup()
        {
            if (GameEffects.ScreenPlaying("party_soonchal") || countShown) { GameEffects.Stop("party_soonchal"); GameEffects.PatrolCountStop(); countShown = false; }
            running = false;
            if (Active == this) Active = null;
            MissionTracker.Remove(this);
            var ui = UiManager.Instance;
            if (ui) { var c = ui.Find(ConfirmId); if (c) ui.Close(c); }
            if (timerView && ui) ui.Close(timerView);
            timerView = null;
            if (beacon) Destroy(beacon.gameObject);
            beacon = null;
        }

        /// <summary>The next three open posts for the minimap (ID_MINIMAP_ICON_PATROL).</summary>
        public IEnumerable<MissionMarkers.Marker> Markers()
        {
            if (!running) yield break;
            int i = next, shown = 0;
            for (int k = 0; k < Posts.Count && shown < 3; k++, i = (i + 1) % Posts.Count)
            {
                if (done[i]) continue;
                shown++;
                yield return new MissionMarkers.Marker { Icon = "ID_MINIMAP_ICON_PATROL", Position = Posts[i].Position, Label = Posts[i].Name };
            }
        }

        /// <summary>The post the beacon stands on.</summary>
        public CityMapInfo.Icon NextPost => running ? Posts[next] : null;
    }
}
