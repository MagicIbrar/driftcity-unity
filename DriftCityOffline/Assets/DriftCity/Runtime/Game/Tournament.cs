using System.Collections.Generic;
using System.Linq;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// GT Championship (PVP.md section 10), offline against AI drivers in the TOUR lobby 1010000. Four classes (Rookie /
    /// Touring B / Touring A / GT Master, channels 7..10 in the client) and the client's stages: Preliminaries (time attack,
    /// ranked by best lap) -> Quarter-finals 1 and 2 (normal battle) -> Semi-finals (two rounds, points) -> Finals. The
    /// client's tournament is run by the server on a schedule; the remake's bracket (remake rule, documented in PVP.md):
    /// 16 entrants, the 8 best laps go through, two quarter-final heats of 4 with the top 2 going on, a 4-driver semi-final
    /// over two rounds (4/3/2/1 points) and a 1-on-1 final. Races the player is not in are simulated from the AI skills.
    /// </summary>
    public static class Tournament
    {
        const int Dialog = 1010000;
        public enum Stage { Preliminaries, QuarterFinals, SemiFinals, Final, Over }
        static readonly string[] ClassButtons = { "ID_TOUR_LOBBY_CH_ROOKIE", "ID_TOUR_LOBBY_CH_TOUR_B", "ID_TOUR_LOBBY_CH_TOUR_A", "ID_TOUR_LOBBY_CH_MASTER" };
        static readonly string[] ClassNames = { "Rookie", "Touring B", "Touring A", "GT Master" };
        static readonly string[] StageText = { "11492", "11493", "11494", "11495" };
        static readonly string[] Steps = { "ID_TOUR_LOBBY_STEP_1", "ID_TOUR_LOBBY_STEP_2", "ID_TOUR_LOBBY_STEP_SEMI_FINAL", "ID_TOUR_LOBBY_STEP_FINAL" };

        public sealed class Entrant { public PvpMember Member; public float BestLap; public int Points; public bool Out; }

        static NuiView view;
        static CitySession session;
        static UiManager hooked;
        static int cls = -1;
        static Stage stage;
        static int semiRound;
        static readonly List<Entrant> field = new List<Entrant>();
        static List<Entrant> heat = new List<Entrant>();
        static RaceTrack track;
        static string lastMessage = "";
        static int finalPlace;

        public static bool IsOpen => view;
        public static NuiView View => view;
        public static Stage Current => online != null ? (Stage)Mathf.Clamp(online.I("stageIndex"), 0, 4) : stage;
        public static int Class => online != null ? online.I("class") : cls;
        public static IReadOnlyList<Entrant> Field => field;
        public static bool Running => online != null ? online.S("state") != "over" : cls >= 0 && stage != Stage.Over;

        // ---------------- online: the server's bracket (src/pvptour.js; race.tour / race.tourReward) ----------------
        static Dictionary<string, object> online;
        /// <summary>The server's championship state (online), or null.</summary>
        public static Dictionary<string, object> OnlineState => online;

        /// <summary>race.tour: the bracket changed.</summary>
        internal static void OnServer(Dictionary<string, object> t)
        {
            online = t;
            if (t != null && !string.IsNullOrEmpty(t.S("message"))) lastMessage = t.S("message");
            Refresh();
        }

        /// <summary>race.tourReward: the championship is over for me - the stage reached and the reward.</summary>
        internal static void OnReward(Dictionary<string, object> m)
        {
            int reached = m.I("reached");
            var p = PlayerProfile.Current;
            if (p != null) p.TourBest = Mathf.Max(p.TourBest, reached);
            lastMessage = (reached >= 4 ? $"{ClassNames[Mathf.Clamp(m.I("class"), 0, 3)]} class champion!" : $"Final rank {Pvp.Ordinal(m.I("rank"))}.") + $"  Reward: EXP {m.I("exp"):N0}, Mito {m.I("mito"):N0}.";
            Net.ChatBox.System($"Exp. {m.I("exp"):N0}  Mito. {m.I("mito"):N0}");
            foreach (var it in (m.L("items") ?? new List<object>()).OfType<string>()) Net.ChatBox.System($"Obtained {it}.");
            Refresh();
        }
        static Entrant Me => field.FirstOrDefault(e => e.Member.Me);

        /// <summary>Class entry rule (remake rule from the class names and the arena limits): Rookie up to MaxBattle3XLevel,
        /// Touring B cars up to grade 6, Touring A up to grade 8, GT Master open.</summary>
        public static bool ClassOpen(int c)
        {
            int grade = Pvp.CarGrade(), level = PlayerProfile.Current?.Level ?? 1;
            switch (c)
            {
                case 0: return level <= Pvp.Config("MaxBattle3XLevel", 40);
                case 1: return grade <= 6;
                case 2: return grade <= 8;
                default: return true;
            }
        }

        public static void Open(CitySession s)
        {
            if (Pvp.Config("Tournament", 1) == 0) { PvpLobby.Unavailable(); return; }
            session = s ? s : PvpLobby.Session;
            var ui = UiManager.Instance;
            if (!ui) return;
            PvpLobby.Close();
            if (!view) view = ui.Open(Dialog);
            if (!view) return;
            PvpLobby.Freeze(true);
            if (hooked != ui)
            {
                hooked = ui;
                ui.On("ID_TOUR_LOBBY_CH_CLOSE", _ => Close(true));
                for (int i = 0; i < ClassButtons.Length; i++) { int c = i; ui.On(ClassButtons[i], _ => Enter(c)); }
                for (int r = 0; r < 10; r++)
                {
                    int row = r;
                    foreach (var p in new[] { "ID_TOUR_LOBBY_TITLE_", "ID_TOUR_LOBBY_SINGLEMODE_", "ID_TOUR_LOBBY_TEAMMODE_", "ID_TOUR_LOBBY_JOINGAME_" })
                        ui.On($"{p}{r:00}", _ => Row(row));
                }
                foreach (var id in new[] { "ID_TOUR_LOBBY_STATE_BTN", "ID_TOUR_LOBBY_VIEW_MYINFO_BTN" })
                    ui.On(id, _ => UiPopup.Message(Summary()));
                ui.On("ID_TOUR_LOBBY_VIEW_PASS_BTN", _ => OpenRewards(cls >= 0 ? cls : 3));
                ui.On("ID_TOUR_REWARD_CLOSE", _ => CloseRewards());
                for (int i = 0; i < RewardTabs.Length; i++)
                {
                    int c = i;
                    ui.On($"ID_TOUR_REWARD_{RewardTabs[i]}_BTN", _ => OpenRewards(c));
                    ui.On($"ID_TOUR_REWARD_{RewardTabs[i]}_TAB", _ => OpenRewards(c));
                }
            }
            foreach (var c in view.GetComponentsInChildren<NuiControlView>(true))
            {
                var id = c.Data.IdName ?? "";
                if (id.Contains("CHAT") || id.Contains("ARROW") || id.Contains("CLICKIMAGE") || id.Contains("VIEWLINE") || id.Contains("CHARGE") ||
                    id.Contains("TABKEY") || id.Contains("OBSERVE") || id.Contains("SECRET") || id.Contains("NOGATE") || id.Contains("_PREV") || id.Contains("_NEXT"))
                    c.gameObject.SetActive(false);
            }
            if (Net.OnlineSession.Active)
            {
                OnlineRace.Listen();
                Net.OnlineSession.Instance.Net.Request("tour.state", null, r => { if (r.B("ok")) OnServer(r.O("tour")); });
            }
            Refresh();
        }

        public static void Close(bool toLobby)
        {
            var ui = UiManager.Instance;
            if (view) { if (ui) ui.Close(view); else Object.Destroy(view.gameObject); }
            view = null;
            if (toLobby && session) PvpLobby.Open(session); else PvpLobby.Freeze(false);
        }

        /// <summary>Starts a class: 16 entrants (the player and 15 AI drivers), preliminaries first.</summary>
        public static void Enter(int c)
        {
            if (Net.OnlineSession.Active)
            {
                // the server's bracket: the players of the channel who enter during the sign-up, filled with AI entrants
                var s0 = session ? session : PvpLobby.Session;
                var keys = Pvp.MapList(s0 ? s0.City : "cras", 0, Pvp.ArenaFree).Select(Pvp.TrackKey).Cast<object>().ToList();
                OnlineRace.Listen();
                Net.OnlineSession.Instance.Net.Request("tour.enter", new Dictionary<string, object> { ["class"] = c, ["tracks"] = keys }, r =>
                {
                    if (!r.B("ok")) { UiPopup.Message(r.S("error")); return; }
                    OnServer(r.O("tour"));
                });
                return;
            }
            if (Running && c != cls) { UiPopup.Message($"You are entered in the {ClassNames[cls]} class."); return; }
            if (Running) { Refresh(); return; }
            if (!ClassOpen(c)) { UiPopup.Message($"Your car or level does not meet the {ClassNames[c]} class limits."); return; }
            cls = c; stage = Stage.Preliminaries; semiRound = 0; finalPlace = 0;
            field.Clear();
            var p = PlayerProfile.Current;
            var room = new PvpRoom();
            room.ResetSlots();
            field.Add(new Entrant { Member = new PvpMember { Id = 1, Name = p?.Name ?? "Driver", Level = p?.Level ?? 1, Me = true, Ready = true } });
            for (int i = 0; i < 15; i++)
            {
                var ai = Pvp.NewAi(room, p?.Level ?? 1);
                ai.Skill = Mathf.Clamp(ai.Skill + 0.02f * c, 0.8f, 0.99f);
                room.Members.Add(ai);
                field.Add(new Entrant { Member = ai });
            }
            var maps = Pvp.MapList(session ? session.City : "cras", 0, Pvp.ArenaFree);
            track = maps.Count > 0 ? maps[Random.Range(0, maps.Count)] : null;
            lastMessage = Pvp.Text("11538");   // "Time Attack"
            Refresh();
        }

        // ---------------- the hub ----------------
        static void Refresh()
        {
            if (!view) return;
            if (online != null) { RefreshOnline(); return; }
            for (int i = 0; i < ClassButtons.Length; i++)
            {
                var b = view.Control(ClassButtons[i]);
                if (b) { b.Selected = i == cls; b.Interactable = ClassOpen(i) && (!Running || i == cls); }
                view.SetVisible(ClassButtons[i] + "_CLOSE", !ClassOpen(i) || (Running && i != cls));
            }
            for (int i = 0; i < Steps.Length; i++)
            {
                var b = view.Control(Steps[i]);
                if (b) { b.Selected = Running && (int)stage == i; b.Interactable = true; }
            }
            view.SetVisible("ID_TOUR_LOBBY_VIEW_STEP2_1", Running && stage == Stage.QuarterFinals);
            view.SetVisible("ID_TOUR_LOBBY_VIEW_STEP2_2", false);
            var rows = Rows();
            for (int r = 0; r < 10; r++)
            {
                bool used = r < rows.Count;
                string n = r.ToString("00");
                view.SetVisible("ID_TOUR_LOBBY_SINGLEMODE_" + n, used);
                view.SetVisible("ID_TOUR_LOBBY_TEAMMODE_" + n, false);
                view.SetVisible("ID_TOUR_LOBBY_JOINGAME_" + n, false);
                Label("ID_TOUR_LOBBY_TITLE_" + n, used, used ? rows[r].title : "");
                Label("ID_TOUR_LOBBY_MAP_" + n, used, used ? rows[r].map : "");
                Label("ID_TOUR_LOBBY_PLAYERCNT_" + n, used, used ? rows[r].count : "");
                if (used) view.SetEnabled("ID_TOUR_LOBBY_SINGLEMODE_" + n, rows[r].mine);
            }
            string clsName = cls >= 0 ? ClassNames[cls] : "GT Championship";
            view.SetText("ID_TOUR_LOBBY_TITLE1", $"{clsName} Class Match Information");
            view.SetText("ID_TOUR_LOBBY_T1_RIGHT_TXT1", !Running ? (cls >= 0 ? "Finished" : "-") : Pvp.Text(StageText[(int)stage]));
            view.SetText("ID_TOUR_LOBBY_T1_RIGHT_TXT2", track != null ? Pvp.TrackName(track) : "-");
            view.SetText("ID_TOUR_LOBBY_T1_RIGHT_TXT3", stage == Stage.SemiFinals ? $"Round {semiRound + 1} / 2" : "-");
            view.SetText("ID_TOUR_LOBBY_T1_RIGHT_TXT4", $"{field.Count(e => !e.Out)} Players");
            view.SetText("ID_TOUR_LOBBY_TITLE2", Running ? $"{Pvp.Text(StageText[(int)stage])} Winning Requirements" : "Result");
            view.SetText("ID_TOUR_LOBBY_T2_TXT1", Requirement());
            var me = Me;
            view.SetText("ID_TOUR_LOBBY_T3_LEFT_TXT1", "Best lap");
            view.SetText("ID_TOUR_LOBBY_T3_RIGHT_TXT1", me != null && me.BestLap > 0 ? Pvp.Time(me.BestLap).Trim() : "--:--.---");
            view.SetText("ID_TOUR_LOBBY_T3_LEFT_TXT2", stage == Stage.SemiFinals ? "Points" : "Status");
            view.SetText("ID_TOUR_LOBBY_T3_RIGHT_TXT2", me == null ? "-" : stage == Stage.SemiFinals ? me.Points.ToString() : me.Out ? "Out" : Running ? "In" : finalPlace == 1 ? "Champion" : "-");
            view.SetText("ID_TOUR_LOBBY_T3_HELP_TXT", string.IsNullOrEmpty(lastMessage) ? (cls < 0 ? "Choose a class to enter the GT Championship." : "") : lastMessage);
        }

        /// <summary>The TOUR lobby from the server's bracket: the stage's heats (mine clickable), the field, my status.</summary>
        static void RefreshOnline()
        {
            var t = online;
            int c = t.I("class"), st = Mathf.Clamp(t.I("stageIndex"), 0, 4);
            bool signup = t.S("state") == "signup", over = t.S("state") == "over";
            for (int i = 0; i < ClassButtons.Length; i++)
            {
                var b = view.Control(ClassButtons[i]);
                if (b) { b.Selected = i == c; b.Interactable = over || i == c; }
                view.SetVisible(ClassButtons[i] + "_CLOSE", !over && i != c);
            }
            for (int i = 0; i < Steps.Length; i++) { var b = view.Control(Steps[i]); if (b) { b.Selected = !signup && !over && st == i; b.Interactable = true; } }
            var tr = t.O("track");
            string map = tr != null ? Pvp.Text("10243", Pvp.Text(tr.S("name"))) : "";
            var heats = (t.L("heats") ?? new List<object>()).OfType<Dictionary<string, object>>().ToList();
            for (int r = 0; r < 10; r++)
            {
                bool used = r < heats.Count;
                string n = r.ToString("00");
                view.SetVisible("ID_TOUR_LOBBY_SINGLEMODE_" + n, used);
                view.SetVisible("ID_TOUR_LOBBY_TEAMMODE_" + n, false);
                view.SetVisible("ID_TOUR_LOBBY_JOINGAME_" + n, false);
                if (!used) { Label("ID_TOUR_LOBBY_TITLE_" + n, false, ""); Label("ID_TOUR_LOBBY_MAP_" + n, false, ""); Label("ID_TOUR_LOBBY_PLAYERCNT_" + n, false, ""); continue; }
                var h = heats[r];
                var names = (h.L("names") ?? new List<object>()).OfType<string>().ToList();
                string title = st == 3 ? $"Final: {string.Join(" vs ", names)}" : $"{Pvp.Text(StageText[Mathf.Min(st, 3)])} - Heat {r + 1}{(h.B("done") ? " (done)" : "")}";
                Label("ID_TOUR_LOBBY_TITLE_" + n, true, title);
                Label("ID_TOUR_LOBBY_MAP_" + n, true, map);
                Label("ID_TOUR_LOBBY_PLAYERCNT_" + n, true, $"({names.Count,2}/{names.Count,2})");
                view.SetEnabled("ID_TOUR_LOBBY_SINGLEMODE_" + n, h.B("mine") && !h.B("done"));
            }
            int alive = (t.L("entrants") ?? new List<object>()).OfType<Dictionary<string, object>>().Count(e => !e.B("out"));
            view.SetText("ID_TOUR_LOBBY_TITLE1", $"{ClassNames[Mathf.Clamp(c, 0, 3)]} Class Match Information");
            view.SetText("ID_TOUR_LOBBY_T1_RIGHT_TXT1", signup ? $"Sign-up ({Mathf.CeilToInt((float)t.N("signupIn") / 1000f)} s)" : over ? "Finished" : Pvp.Text(StageText[Mathf.Min(st, 3)]));
            view.SetText("ID_TOUR_LOBBY_T1_RIGHT_TXT2", tr != null ? Pvp.Text(tr.S("name")) : "-");
            view.SetText("ID_TOUR_LOBBY_T1_RIGHT_TXT3", st == 2 ? $"Round {t.I("round") + 1} / 2" : "-");
            view.SetText("ID_TOUR_LOBBY_T1_RIGHT_TXT4", $"{alive} Players");
            view.SetText("ID_TOUR_LOBBY_TITLE2", over ? "Result" : $"{Pvp.Text(StageText[Mathf.Min(st, 3)])} Winning Requirements");
            view.SetText("ID_TOUR_LOBBY_T2_TXT1", signup ? "Waiting for drivers to enter" : st == 0 ? "The 8 best laps go through" : st == 1 ? "Top 2 of each heat go through" : st == 2 ? "Top 2 on points (2 rounds)" : st == 3 ? "Win the final" : "-");
            var me = (t.L("entrants") ?? new List<object>()).OfType<Dictionary<string, object>>().FirstOrDefault(e => e.B("me"));
            view.SetText("ID_TOUR_LOBBY_T3_LEFT_TXT1", "Best lap");
            view.SetText("ID_TOUR_LOBBY_T3_RIGHT_TXT1", me != null && me.N("lap") > 0 ? Pvp.Time((float)me.N("lap")).Trim() : "--:--.---");
            view.SetText("ID_TOUR_LOBBY_T3_LEFT_TXT2", st == 2 ? "Points" : "Status");
            view.SetText("ID_TOUR_LOBBY_T3_RIGHT_TXT2", me == null ? "-" : st == 2 ? me.I("points").ToString() : me.B("out") ? "Out" : "In");
            view.SetText("ID_TOUR_LOBBY_T3_HELP_TXT", lastMessage);
        }

        static string Requirement()
        {
            switch (stage)
            {
                case Stage.Preliminaries: return "The 8 best laps go through";
                case Stage.QuarterFinals: return "Top 2 of each heat go through";
                case Stage.SemiFinals: return "Top 2 on points (2 rounds)";
                case Stage.Final: return "Win the final";
                default: return finalPlace == 1 ? "Champion!" : "Eliminated";
            }
        }

        static void Label(string id, bool on, string text) { view.SetVisible(id, on); if (on) view.SetText(id, text); }

        /// <summary>The stage's rooms in the list: the heats, the player's one clickable.</summary>
        static List<(string title, string map, string count, bool mine)> Rows()
        {
            var list = new List<(string, string, string, bool)>();
            if (!Running) return list;
            string map = track != null ? Pvp.Text("10243", Pvp.TrackName(track)) : "";
            var alive = field.Where(e => !e.Out).ToList();
            switch (stage)
            {
                case Stage.Preliminaries:
                    list.Add(("Preliminaries - Time Attack", map, $"({alive.Count,2}/16)", true));
                    break;
                case Stage.QuarterFinals:
                    list.Add(("Quarter-finals 1", map, "( 4/ 4)", true));
                    list.Add(("Quarter-finals 2", map, "( 4/ 4)", false));
                    break;
                case Stage.SemiFinals:
                    list.Add(($"Semi-finals - Round {semiRound + 1}", map, "( 4/ 4)", true));
                    break;
                case Stage.Final:
                    var other = alive.FirstOrDefault(e => !e.Member.Me);
                    list.Add(($"Final: {Me?.Member.Name} vs {other?.Member.Name}", map, "( 2/ 2)", true));
                    break;
            }
            return list;
        }

        static void Row(int row)
        {
            if (online != null)
            {
                // my heat: the server starts it once its players are ready (the race comes as race.start)
                var heats = (online.L("heats") ?? new List<object>()).OfType<Dictionary<string, object>>().ToList();
                if (row >= heats.Count || !heats[row].B("mine") || heats[row].B("done")) return;
                Net.OnlineSession.Instance.Net.Request("tour.race", null, r =>
                {
                    if (!r.B("ok")) { UiPopup.Message(r.S("error")); return; }
                    if (r.B("waiting")) { lastMessage = "Waiting for the other drivers of your heat..."; Refresh(); }
                });
                return;
            }
            if (!Running || row != 0 || Me == null || Me.Out) return;
            var s = session ? session : PvpLobby.Session;
            if (!s || !s.Car || track == null) { UiPopup.Message(Pvp.Text("10204")); return; }
            // the heat: the player against the other entrants of the player's race
            var alive = field.Where(e => !e.Out).ToList();
            List<Entrant> rivals;
            switch (stage)
            {
                case Stage.Preliminaries: rivals = alive.Where(e => !e.Member.Me).OrderBy(_ => Random.value).Take(3).ToList(); break;
                case Stage.QuarterFinals: rivals = alive.Where(e => !e.Member.Me).Take(3).ToList(); break;
                case Stage.SemiFinals: rivals = alive.Where(e => !e.Member.Me).ToList(); break;
                default: rivals = alive.Where(e => !e.Member.Me).Take(1).ToList(); break;
            }
            heat = new List<Entrant> { Me };
            heat.AddRange(rivals);
            var room = new PvpRoom { Title = "GT Championship", Max = 8, Arena = Pvp.ArenaFree, Track = track, MapFlags = Pvp.TrafficIdle | Pvp.MapCityAll, Practice = true };
            room.ResetSlots();
            for (int i = 0; i < heat.Count; i++) { var m = heat[i].Member; m.Ready = true; room.Add(m, heat.Count - 1 - i); }
            room.HostId = Me.Member.Id;
            if (view) { UiManager.Instance.Close(view); view = null; }
            PvpLobby.Freeze(false);
            var race = RaceMission.StartRoom(room, s, OnRaceDone);
            if (!race) Open(s);
        }

        /// <summary>Applies a finished heat to the bracket (tests call it with a made-up result).</summary>
        public static void OnRaceDone(PvpRaceResult r)
        {
            if (!Running || r == null) { Open(session); return; }
            var byName = r.Entries.ToDictionary(e => e.Name, e => e);
            float LapOf(Entrant e) => byName.TryGetValue(e.Member.Name, out var x) && x.Place > 0 ? (r.BestLap > 0 && x.Me ? r.BestLap : x.Time / Mathf.Max(1, track.Laps)) : 0f;
            switch (stage)
            {
                case Stage.Preliminaries:
                {
                    foreach (var e in field) { float l = heat.Contains(e) ? LapOf(e) : 0f; e.BestLap = l > 0 ? l : Simulated(e); }
                    var ranked = field.OrderBy(e => e.BestLap).ToList();
                    for (int i = 0; i < ranked.Count; i++) ranked[i].Out = i >= 8;
                    int myRank = ranked.IndexOf(Me) + 1;
                    lastMessage = $"Your best lap ranked {Pvp.Ordinal(myRank)} of 16.";
                    Next(Stage.QuarterFinals);
                    break;
                }
                case Stage.QuarterFinals:
                {
                    // heat 1 = the player's race (top 2 go on), heat 2 = the other four, simulated
                    var mine = heat.OrderBy(e => Place(r, e)).ToList();
                    for (int i = 0; i < mine.Count; i++) mine[i].Out = i >= 2;
                    var other = field.Where(e => !e.Out && !heat.Contains(e)).OrderByDescending(e => e.Member.Skill + Random.Range(-0.05f, 0.05f)).ToList();
                    for (int i = 0; i < other.Count; i++) other[i].Out = i >= 2;
                    lastMessage = Me.Out ? "Eliminated in the quarter-finals." : $"Through to the semi-finals ({Pvp.Ordinal(Place(r, Me))} in your heat).";
                    Next(Stage.SemiFinals);
                    break;
                }
                case Stage.SemiFinals:
                {
                    var order = heat.OrderBy(e => Place(r, e)).ToList();
                    for (int i = 0; i < order.Count; i++) order[i].Points += 4 - i;
                    if (semiRound == 0) { semiRound = 1; lastMessage = $"Round 1: {Pvp.Ordinal(Place(r, Me))}, {Me.Points} points."; break; }
                    var ranked = field.Where(e => !e.Out).OrderByDescending(e => e.Points).ThenBy(e => Place(r, e)).ToList();
                    for (int i = 0; i < ranked.Count; i++) ranked[i].Out = i >= 2;
                    lastMessage = Me.Out ? $"Eliminated in the semi-finals ({Me.Points} points)." : $"Through to the final with {Me.Points} points.";
                    Next(Stage.Final);
                    break;
                }
                case Stage.Final:
                {
                    finalPlace = Place(r, Me) == 1 ? 1 : 2;
                    lastMessage = finalPlace == 1 ? $"{ClassNames[cls]} class champion!" : "Runner-up in the final.";
                    Finish(4 - (finalPlace == 1 ? 0 : 1));
                    break;
                }
            }
            if (Me.Out && stage != Stage.Over) Finish((int)stage - 1);   // eliminated in the stage before the one now running
            Open(session);
        }

        static int Place(PvpRaceResult r, Entrant e)
        {
            var x = r.Entries.FirstOrDefault(y => y.Name == e.Member.Name);
            return x == null ? 99 : x.Place > 0 ? x.Place : 50 + r.Entries.IndexOf(x);
        }

        /// <summary>An AI entrant's time-attack lap: track length over a skill-scaled average speed (about 100 km/h).</summary>
        static float Simulated(Entrant e)
        {
            float speed = 30f * e.Member.Skill * Random.Range(0.95f, 1.05f);
            return (track != null ? track.Length : 3000f) / Mathf.Max(5f, speed);
        }

        static void Next(Stage s)
        {
            stage = s;
            foreach (var e in field) e.Points = 0;
            semiRound = 0;
        }

        /// <summary>
        /// End of the player's championship: the reward by the stage reached (reached: 1 quarter-finals .. 3 final,
        /// 4 champion; remake amounts, x1 / x1.5 / x2 / x3 by class) and the profile's best result.
        /// </summary>
        static void Finish(int reached)
        {
            stage = Stage.Over;
            var p = PlayerProfile.Current;
            if (p == null) return;
            int[] exp = { 200, 500, 1000, 2000, 4000 }, mito = { 1000, 3000, 6000, 12000, 25000 };
            float f = new[] { 1f, 1.5f, 2f, 3f }[Mathf.Clamp(cls, 0, 3)];
            int k = Mathf.Clamp(reached, 0, 4);
            long e = (long)(exp[k] * f), m = (long)(mito[k] * f);
            p.TourBest = Mathf.Max(p.TourBest, k);
            // the class's reward table (TOUR_REWARD 4510000, rewardgroup.xlt rw_gtc_<class>_a_<bracket>): the final rank
            // from the stage reached (champion 1, runner-up 2, semi-final 3-4, quarter-final 5-8, preliminaries 9-16)
            int rank = k >= 4 ? 1 : k == 3 ? 2 : k == 2 ? 3 : k == 1 ? 5 : 9;
            foreach (var row in RewardsFor(cls, rank).Where(x => !string.IsNullOrEmpty(x.Item) && x.Type != "RewardGroup"))
            {
                p.Give(row.Item, Mathf.Max(1, row.Count));
                Net.ChatBox.System($"Obtained {row.Desc}.");
            }
            Progress.Reward("tournament", new Dictionary<string, object> { ["class"] = cls, ["reached"] = k }, () =>
            {
                int before = p.Level;
                p.AddExp(e); p.Mito += m;
                return new Progress.Result { Ok = true, Exp = e, Mito = m, LevelUps = p.Level - before };
            }, r => { if (r.Ok) lastMessage += $"  Reward: EXP {r.Exp:N0}, Mito {r.Mito:N0}."; Refresh(); });
        }

        static string Summary()
        {
            if (cls < 0) return "Choose a class to enter the GT Championship.";
            var alive = field.Where(e => !e.Out).Select(e => e.Member.Name + (stage == Stage.SemiFinals ? $" ({e.Points})" : ""));
            return $"{ClassNames[cls]}: {(Running ? Pvp.Text(StageText[(int)stage]) : "finished")}\n{string.Join(", ", alive)}";
        }

        /// <summary>Forget the championship (tests, logout).</summary>
        public static void Reset() { cls = -1; stage = Stage.Preliminaries; field.Clear(); heat.Clear(); lastMessage = ""; online = null; CloseRewards(); if (view) Close(false); }

        // ---------------- TOUR_REWARD 4510000 (0x58e570): the classes' reward table ----------------
        const int RewardDialog = 4510000;
        static readonly string[] RewardTabs = { "ROOKIE", "TOURINGB", "TOURINGA", "GTMASTER" };
        static readonly string[] RewardKeys = { "rookie", "touringb", "touringa", "gtmaster" };
        static NuiView rewards;
        public static NuiView RewardView => rewards;

        /// <summary>init\rewardgroup.xlt bracket of a final rank: 01, 02, 03, 0408, 0916.</summary>
        public static string Bracket(int rank) => rank <= 1 ? "01" : rank == 2 ? "02" : rank == 3 ? "03" : rank <= 8 ? "0408" : "0916";

        /// <summary>The reward rows of a class and rank: rw_gtc_&lt;class&gt;_a_&lt;bracket&gt; (the A type the server sets by default).</summary>
        public static List<Pvp.RewardRow> RewardsFor(int c, int rank) => Pvp.RewardGroup($"rw_gtc_{RewardKeys[Mathf.Clamp(c, 0, 3)]}_{PvpTourRewardGm.Letter(c)}_{Bracket(rank)}");

        /// <summary>The reward table for ranks 1..16 of a class (class tabs; the GM controls stay hidden).</summary>
        public static void OpenRewards(int c)
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            if (!rewards) rewards = ui.Open(RewardDialog);
            if (!rewards) return;
            // the GM panel (0x58e430: authority byte < 'N'): PvpTourRewardGm shows it for admins and redraws on APPLY
            PvpTourRewardGm.Attach(rewards, c, () => { if (rewards) OpenRewards(c); });
            for (int i = 0; i < RewardTabs.Length; i++)
            {
                var b = rewards.Control($"ID_TOUR_REWARD_{RewardTabs[i]}_BTN"); if (b) b.Selected = i == c;
                var t = rewards.Control($"ID_TOUR_REWARD_{RewardTabs[i]}_TAB"); if (t) t.Selected = i == c;
            }
            for (int rank = 1; rank <= 16; rank++)
            {
                var rows = RewardsFor(c, rank);
                string n = rank.ToString("00");
                rewards.SetText($"ID_TOUR_REWARD_{n}_TXT1", Pvp.Ordinal(rank));
                rewards.SetText($"ID_TOUR_REWARD_{n}_TXT2", ClassNames[Mathf.Clamp(c, 0, 3)]);
                for (int k = 0; k < 3; k++)
                {
                    bool on = k < rows.Count;
                    rewards.SetText($"ID_TOUR_REWARD_{n}_ITEM_NAME{k + 1}", on ? rows[k].Desc : "-");
                    foreach (var img in new[] { $"ID_TOUR_REWARD_{rank}_ITEM_IMG{k + 1}", $"ID_TOUR_REWARD_{n}_ITEM_IMG{k + 1}" })
                        if (rewards.Control(img)) EventUi.Icon(rewards, img, on ? rows[k].Item : null, on);
                }
            }
        }

        public static void CloseRewards()
        {
            var ui = UiManager.Instance;
            if (rewards) { if (ui) ui.Close(rewards); else Object.Destroy(rewards.gameObject); }
            rewards = null;
        }
    }
}
