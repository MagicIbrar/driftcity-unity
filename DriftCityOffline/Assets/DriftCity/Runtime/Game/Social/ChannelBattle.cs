using System;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Game;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.EventSystems;

namespace DriftCity.Social
{
    /// <summary>
    /// The Channel Battle screens of the Team Center (DriftCity_RE/SOCIAL.md section 6, social/crews.md section 4).
    ///
    /// Client [exact]: the schedule 0x35e -> 0x35f (TC+0xb8; MAIN TEXT 10319 countdown / 10320 for 7200 s, 0x675ea0); the
    /// channel table 0x35c -> 0x35d (10 major rows TIME_1-10 in 2060000, semi rows in 2070000, the SELCHTAB 2090000 tabs,
    /// the NOTICE 2100000 / CHANNELINFO 2110000 panes: 10322 "%u Ch: %s" or 10324, owner, master, 10570 members, battle
    /// leader, rank, points, READY rows of the channel's rooms with 10321 "(%uCrew)"; ENTERCHANNEL between t2 and t4,
    /// BATTLESETUP for the owner's Master within 24 h of the capture, BATTLENOTIFY when the maps are public); entry
    /// 0x370 / 0x371 (10553 on major channels 1-2: a lost attack costs a part); the lobbies ATK 2130000 + ATKCOVER 2140000
    /// (READY -> 0x384(ch, 1), CANCEL -> 0x384(ch, 0)) and DEF 2150000 (the defender is ready by itself): 8 member rows of
    /// the own side (MEMBERCLOSE for rank &lt; 3: 10571 / 10240 -> kick), the queue of crews, CHANNELNAME 10347, 10318,
    /// STATUSTEXT 10343-10346, REMAINTIME 10072 / 10074 "%02u : %02u", HISTORY 10348-10351, ESC 10326 / 10327; map setup
    /// CHANNELSETUP 2240000 (two tracks, traffic IDLE/NORMAL/JAM/RANDOM, 11387) -> 0x38a, MAPNOTIFY 2250000 (10342); the
    /// part stakes BATTLEITEMGET 2220000 / BATTLEITEMMISS 2230000 (0x391 / 0x392; 10352-10360); spectating SELCH_VIEWING
    /// 3390000 (0x387 -> 0x388: 10201 / 10199 / 11261, 10200), cheering 2170000 (Mittron Fireworks i_n_00011 -> 0x394, 5 s),
    /// replays 3410000 (0x395 -> 0x396: 11259, 11258, price 1000 / 500 / 0, 11256 / 11260); the crew ladder (LADDER
    /// TABTEAM 3960000; class S/A/B/C/D/E/R by rank on MY 3980000).
    /// Remake: the server (src/channelbattle.js) runs the schedule, queue, coupons, stakes, maps, replays and ladder; these
    /// screens show its crew.table / crew.lobby and send crew.enter / crew.ready / crew.setup / crew.cheer / crew.replay.
    /// The lobby is the channel's crew room of race.js: the race itself is the Battle Zone's (OnlineRace), the lobby screen
    /// stands in for WAIT16 while the room waits (the room host's client starts a round once both sides are equal and
    /// ready after the preparation time). Offline the channels are held by AI crews and Enter opens the Battle Zone team
    /// room with AI drivers (TeamBattle's stand-in).
    /// </summary>
    public static class ChannelBattle
    {
        public const int MajorDlg = 2060000, SemiDlg = 2070000, TabDlg = 2090000, NoticeDlg = 2100000, InfoDlg = 2110000,
            AtkDlg = 2130000, AtkCoverDlg = 2140000, DefDlg = 2150000, ItemGetDlg = 2220000, ItemMissDlg = 2230000, SetupDlg = 2240000,
            MapNotifyDlg = 2250000, CheerDlg = 2170000, SelViewDlg = 3390000, ReplayDlg = 3410000, LadderFrame = 3910000, LadderTeamDlg = 3960000;
        public const string Coupon = "i_n_00037", Fireworks = "i_n_00011";
        public const long WindowMs = 7200 * 1000L, SetupMs = 86400 * 1000L;
        static readonly string[] CityNames = { "cras", "moonpalace", "koinonia", "oros" };

        public sealed class Crew { public string Name = "", Master = "", Grade = "C"; public int Mark, Members, Points; }
        public sealed class Map { public string Key = "", Name = ""; public int Traffic; }
        public sealed class Channel
        {
            public int Index, Number;
            public string Key = "", City = "", CityName = "", Label = "", State = "";
            public bool Semi, Active, MapsPublic, Stake;
            public Crew Owner, Holder, Attacker;
            public long Since, SetupUntil, Open, Close, PrepEnd, RoundEnd;
            public int Room, Viewers;
            /// <summary>0x35d: max level / max car grade (0 = none) and the battle leader (DriftCity_RE/SERVICES.md 6).</summary>
            public int MaxLevel, MaxGrade, MinGrade;
            public string Leader = "";
            public readonly List<Map> Maps = new List<Map>();
            public readonly List<Crew> Queue = new List<Crew>();
        }

        static PlayerProfile P => PlayerProfile.Current;
        public static readonly List<Channel> Channels = new List<Channel>();
        public static long Next { get; private set; }
        public static long WindowEnd { get; private set; }
        public static bool WindowOpen { get; private set; }
        static long serverOffset;   // server ms - local ms
        public static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + serverOffset;

        // ================= the table (0x35c / 0x35d) and the schedule (0x35f) =================
        public static void Load(Action done = null)
        {
            if (!SocialNet.Online) { OfflineTable(); done?.Invoke(); return; }
            SocialNet.Request("crew.table", null, r =>
            {
                if (r != null && r.B("ok")) Parse(r);
                done?.Invoke();
            });
        }

        static Crew CrewFrom(Dictionary<string, object> o) => o == null ? null : new Crew { Name = o.S("name"), Master = o.S("master"), Mark = o.I("mark"), Members = o.I("members"), Points = o.I("points"), Grade = o.S("grade", "C") };

        static void Parse(Dictionary<string, object> r)
        {
            serverOffset = r.ContainsKey("now") ? (long)r.N("now") - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : 0;
            Next = (long)r.N("next"); WindowEnd = (long)r.N("windowEnd"); WindowOpen = r.B("open");
            Channels.Clear();
            foreach (var o in (r.L("channels") ?? new List<object>()).OfType<Dictionary<string, object>>())
            {
                var c = new Channel
                {
                    Index = o.I("index"), Number = o.I("number"), Key = o.S("key"), City = o.S("city"), CityName = o.S("cityName"), Label = o.S("label"), Semi = o.B("semi"),
                    Owner = CrewFrom(o.O("owner")), Holder = CrewFrom(o.O("holder")), Attacker = CrewFrom(o.O("attacker")), Since = (long)o.N("since"), SetupUntil = (long)o.N("setupUntil"),
                    Open = (long)o.N("open"), Close = (long)o.N("close"), Active = o.B("active"), MapsPublic = o.B("mapsPublic"), Stake = o.B("stake"), Room = o.I("room"),
                    State = o.S("state"), Viewers = o.I("viewers"), PrepEnd = (long)o.N("prepEnd"), RoundEnd = (long)o.N("roundEnd"),
                    MaxLevel = o.I("maxLevel"), MaxGrade = o.I("maxGrade"), MinGrade = o.I("minGrade"), Leader = o.S("leader", ""),
                };
                foreach (var m in (o.L("maps") ?? new List<object>()).OfType<Dictionary<string, object>>())
                    c.Maps.Add(new Map { Key = m.O("track")?.S("key") ?? "", Name = m.O("track")?.S("name") ?? "", Traffic = m.I("traffic") });
                foreach (var q in (o.L("queue") ?? new List<object>()).OfType<Dictionary<string, object>>()) c.Queue.Add(new Crew { Name = q.S("crew"), Mark = q.I("mark") });
                Channels.Add(c);
            }
        }

        /// <summary>Offline: AI crews hold some channels (the battle window is always open).</summary>
        static void OfflineTable()
        {
            Channels.Clear();
            string[] ai = { "NightOwls", "RedLine", "Apex", "Torque", "Slipstream" };
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), day = now / 86400000 * 86400000;
            for (int i = 0; i < 20; i++)
            {
                bool semi = i >= 10;
                var c = new Channel { Index = i, Semi = semi, Number = semi ? i - 9 : i + 1, City = CityNames[i % 4], Key = $"{CityNames[i % 4]}-{i / 4 + 1}", Active = true, Open = day, Close = day + 86400000, Stake = i < 2 };
                c.Label = semi ? $"Semi {c.Number}" : $"Channel {c.Number}";
                c.CityName = CityLabel(c.City);
                if (semi) { c.MaxLevel = 60; c.MaxGrade = 6; }   // the server's rule (src/channelbattle.js LIMITS)
                if (i % 3 == 0) c.Owner = new Crew { Name = ai[i / 3 % ai.Length], Master = "AI", Mark = 1 + i % 10, Members = 8, Points = 20000 + 1000 * i, Grade = "S" };
                Channels.Add(c);
            }
            Next = day; WindowEnd = day + 86400000; WindowOpen = true; serverOffset = 0;
        }

        public static string CityLabel(string city) => city switch { "cras" => "Cras", "moonpalace" => "Moon Palace", "koinonia" => "Koinonia", "oros" => "Oros", _ => city };
        public static Channel Get(int i) => Channels.FirstOrDefault(c => c.Index == i);

        /// <summary>0x675250: ENTERCHANNEL is off above the channel's max level / max car grade (0 = none) or under its min grade.</summary>
        public static bool WithinLimits(Channel c, PlayerProfile p)
        {
            if (c == null || p == null) return true;
            int grade = (p.Car?.Grade ?? 0) + 1;
            return !(c.MaxLevel > 0 && p.Level > c.MaxLevel) && !(c.MaxGrade > 0 && grade > c.MaxGrade) && !(c.MinGrade > 0 && grade < c.MinGrade);
        }

        /// <summary>MAIN TEXT (0x675ea0): 10319 before the window, 10320 during it, nothing after.</summary>
        public static string ScheduleText()
        {
            long now = NowMs;
            if (WindowOpen) return SocialNet.T("10320", "Channel Battle in progress.");
            if (Next > now)
            {
                var t = TimeSpan.FromMilliseconds(Next - now);
                return SocialNet.T("10319", "The next Channel Battle starts in %d days %02d : %02d : %02d", t.Days, t.Hours, t.Minutes, t.Seconds);
            }
            return "";
        }

        /// <summary>Teams.OpenCenter: the schedule line, refreshed every second.</summary>
        public static void FillCenter(NuiView center)
        {
            if (!center) return;
            Load(() =>
            {
                if (!center) return;
                center.SetText("ID_TC_MAIN_TEXT", ScheduleText());
                var tick = center.GetComponent<Ticker>(); if (!tick) tick = center.gameObject.AddComponent<Ticker>();
                tick.Every = 1f; tick.Run = () => center.SetText("ID_TC_MAIN_TEXT", ScheduleText());
            });
        }

        /// <summary>A small per-dialog clock.</summary>
        public sealed class Ticker : MonoBehaviour
        {
            public Action Run; public float Every = 0.25f; float t;
            void Update() { t += Time.unscaledDeltaTime; if (t < Every) return; t = 0; try { Run?.Invoke(); } catch (Exception ex) { Debug.LogException(ex); } }
        }

        /// <summary>A click on an unnamed control (the semi rows, the queue rows): routed by its game.nui id.</summary>
        sealed class IdClick : MonoBehaviour, IPointerClickHandler
        {
            public Action Click;
            public void OnPointerClick(PointerEventData e) => Click?.Invoke();
        }

        static NuiControlView ById(NuiView v, int id) => v ? v.GetComponentsInChildren<NuiControlView>(true).FirstOrDefault(c => c.Data.Id == id) : null;
        static void SetTextId(NuiView v, int id, string text) { var c = ById(v, id); if (c) { c.gameObject.SetActive(true); c.SetText(text); } }

        // ================= channel select (2060000 / 2070000 / 2090000 / 2100000 / 2110000) =================
        static NuiView major, semi, tab, pane;
        static bool majorTab = true;
        static int selected = -1;
        static UiManager hooked;
        public static NuiView MajorView => major;
        public static NuiView SemiView => semi;
        public static NuiView PaneView => pane;
        public static int Selected => selected;
        public static bool MajorTab => majorTab;

        static void Hook(UiManager ui)
        {
            if (hooked == ui) return;
            hooked = ui;
            ui.On("ID_TC_NOTICE_ESC", _ => { CloseChannels(); Teams.OpenCenter(); });
            ui.On("ID_TC_NOTICE_CHARGE", _ => UiPopup.Message(SocialNet.T("11100", "The Challenge Coupon (Channel Battle Attack Ticket) is sold at the parts shop counter.")));
            for (int i = 1; i <= 10; i++) { int k = i - 1; ui.On("ID_TC_NOTICE_TIME_" + i, _ => Select(k)); }
            ui.On("ID_TC_SEMICHANNEL_1ST", _ => Select(10));
            ui.On("ID_TC_SEMICHANNEL_2ND", _ => Select(11));
            ui.On("ID_TC_SEMICHANNEL_3RD", _ => Select(12));
            ui.On("ID_TC_SELCHTAB_MAJOR", _ => SetTab(true));
            ui.On("ID_TC_SELCHTAB_SEMI", _ => SetTab(false));
            ui.On("ID_TC_SELCHTAB_TABKEY", _ => SetTab(!majorTab));
            ui.On("ID_TC_CHANNELINFO_ENTERCHANNEL", _ => AskEnter(selected));
            ui.On("ID_TC_NOTICE_BATTLESETUP", _ => OpenSetup(selected));
            ui.On("ID_TC_NOTICE_BATTLENOTIFY", _ => OpenMapNotify(selected));
            // lobbies
            ui.On("ID_TC_CHANNELOBBY_ATKCOVER_READY", _ => Ready(true));
            ui.On("ID_TC_CHANNELOBBY_ATK_CANCEL", _ => Ready(false));
            ui.On("ID_TC_CHANNELOBBY_ATK_ESC", _ => AskLeaveLobby());
            ui.On("ID_TC_CHANNELOBBY_DEF_ESC", _ => AskLeaveLobby());
            ui.On("ID_TC_CHANNELOBBY_ATK_VIEWING", _ => WatchRound());
            ui.On("ID_TC_CHANNELOBBY_DEF_VIEWING", _ => WatchRound());
            for (int i = 1; i <= 8; i++)
            {
                int k = i;
                ui.On("ID_TC_CHANNELOBBY_ATK_MEMBERCLOSE_" + k, _ => Kick(k));
                ui.On("ID_TC_CHANNELOBBY_DEF_MEMBERCLOSE_" + k, _ => Kick(k));
            }
            // map setup / notify
            ui.On("ID_TC_CHANNELSETUP_ESC", _ => SocialUi.Close(ref setup));
            ui.On("ID_TC_CHANNELSETUP_OK", _ => SendSetup());
            foreach (var m in new[] { 1, 2 })
            {
                int mm = m;
                ui.On($"ID_TC_CHANNELSETUP_{mm}_PREVMAP", _ => StepMap(mm, -1));
                ui.On($"ID_TC_CHANNELSETUP_{mm}_NEXTMAP", _ => StepMap(mm, +1));
                ui.On($"ID_TC_CHANNELSETUP_{mm}_RANDOM", _ => { setupRandom[mm - 1] = !setupRandom[mm - 1]; FillSetup(); });
                string[] tr = { "IDLE", "NORMAL", "JAM", "RANDOM" };
                for (int t = 0; t < 4; t++) { int tt = t; ui.On($"ID_TC_CHANNELSETUP_{mm}_TRAFFIC_{tr[tt]}", _ => { setupTraffic[mm - 1] = tt; FillSetup(); }); }
            }
            ui.On("ID_TC_CH_MAPNOTIFY_OK", _ => SocialUi.Close(ref notify));
            ui.On("ID_TC_CH_MAPNOTIFY_CLOSE", _ => SocialUi.Close(ref notify));
            // stakes
            ui.On("ID_TC_BATTLEITEMGET_OK", _ => SocialUi.Close(ref itemGet));
            ui.On("ID_TC_BATTLEITEMGET_ESC", _ => SocialUi.Close(ref itemGet));
            ui.On("ID_TC_BATTLEITEMMISS_OK", _ => SocialUi.Close(ref itemMiss));
            // spectating, cheering, replays
            ui.On("ID_SELCH_VIEWING_BACK", _ => { SocialUi.Close(ref selView); Teams.OpenCenter(); });
            for (int i = 1; i <= 10; i++) { int k = i - 1; ui.On($"ID_SELCH_VIEWING_CH{i:00}", _ => Spectate(k)); }
            ui.On("ID_TC_VIEWING_CHEERING_POP", v => { cheerOpen = true; FillCheer(); });
            ui.On("ID_TC_VIEWING_CHEERING_PUSH", v => { cheerOpen = false; FillCheer(); });
            ui.On("ID_TC_VIEWING_CHEERING_USE", _ => Cheer());
            ui.On("ID_TC_REPLAY_CLOSE", _ => SocialUi.Close(ref replay));
            ui.On("ID_TC_REPLAY_LEFT", _ => { replayPage = Math.Max(0, replayPage - 5); LoadReplays(); });
            ui.On("ID_TC_REPLAY_RIGHT", _ => { replayPage += 5; LoadReplays(); });
            for (int i = 1; i <= 5; i++) { int k = i - 1; ui.On($"ID_TC_REPLAY_PAGE{i:00}", _ => { replayPage = replayPage / 5 * 5 + k; LoadReplays(); }); }
            for (int i = 1; i <= 8; i++) { int k = i - 1; ui.On($"ID_TC_REPLAY_SEL{i:00}", _ => AskReplay(k)); }
            // crew ladder
            ui.On("ID_LADDER_TABTEAM_LEFT", _ => { ladderPage = Math.Max(0, ladderPage - 1); LoadLadder(); });
            ui.On("ID_LADDER_TABTEAM_RIGHT", _ => { ladderPage++; LoadLadder(); });
            for (int i = 0; i < 10; i++) { int k = i; ui.On("ID_LADDER_TABTEAM_PAGE" + k, _ => { ladderPage = ladderPage / 10 * 10 + k; LoadLadder(); }); }
            ui.On("ID_LADDER_TABTEAM_SEARCH", _ => LoadLadder());
            ui.On("ID_LADDER_TABTEAM_DETAIL", _ => LadderDetail());
        }

        /// <summary>NOTICECHALLENGE: the channel list (0x35c ctx 0x3ed); the first channel with an open window is selected.</summary>
        public static void OpenChannels()
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            Hook(ui);
            Teams.Load(() => Load(() =>
            {
                CloseChannels();
                tab = ui.Open(TabDlg);
                var first = Channels.FirstOrDefault(c => c.Active && (c.Index < 10) == majorTab) ?? Channels.FirstOrDefault(c => (c.Index < 10) == majorTab);
                SetTab(majorTab, first?.Index ?? (majorTab ? 0 : 10));
            }));
        }

        public static void SetTab(bool isMajor, int select = -1)
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            majorTab = isMajor;
            SocialUi.Close(ref semi);
            // 2060000 is the frame (ESC, chat, CHARGE, the text); its TIME rows are the major channels, the semi rows overlay them
            if (!major) major = ui.Open(MajorDlg);
            if (major) for (int r = 1; r <= 10; r++) major.SetVisible("ID_TC_NOTICE_TIME_" + r, isMajor);
            if (!isMajor)
            {
                semi = ui.Open(SemiDlg);
                // the semi buttons' art (TX_Replay_S1..3.dds) isn't in this install: their label says which they are
                string[] named = { "ID_TC_SEMICHANNEL_1ST", "ID_TC_SEMICHANNEL_2ND", "ID_TC_SEMICHANNEL_3RD" };
                for (int k = 0; k < 3; k++) semi.SetText(named[k], "Semi " + (k + 1));
                // semi 4-10 are the unnamed rows 2070005-2070011
                for (int k = 0; k < 7; k++)
                {
                    var c = ById(semi, 2070005 + k);
                    if (!c) continue;
                    int idx = 13 + k;
                    var ic = c.GetComponent<IdClick>(); if (!ic) ic = c.gameObject.AddComponent<IdClick>(); ic.Click = () => Select(idx);
                    c.SetText("Semi " + (idx - 9));
                    if (!c.GetComponent<UnityEngine.UI.Graphic>()) { var hit = c.gameObject.AddComponent<UnityEngine.UI.Image>(); hit.color = new Color(0, 0, 0, 0.01f); }
                }
            }
            if (tab) { var a = tab.Control("ID_TC_SELCHTAB_MAJOR"); if (a) a.Selected = isMajor; var b = tab.Control("ID_TC_SELCHTAB_SEMI"); if (b) b.Selected = !isMajor; }
            if (major) major.SetText("ID_TC_NOTICE_TEXT", ScheduleText());
            Select(select >= 0 ? select : isMajor ? 0 : 10);
        }

        /// <summary>A row: the NOTICE pane before / after the window, CHANNELINFO with ENTERCHANNEL during it (0x679560).</summary>
        public static void Select(int i)
        {
            var ui = UiManager.Instance;
            var c = Get(i);
            if (!ui || c == null) return;
            selected = i;
            if (major) for (int r = 1; r <= 10; r++) { var b = major.Control("ID_TC_NOTICE_TIME_" + r); if (b) b.Selected = r - 1 == i; }
            SocialUi.Close(ref pane);
            pane = ui.Open(c.Active ? InfoDlg : NoticeDlg);
            if (!pane) return;
            string P0 = c.Active ? "ID_TC_CHANNELINFO_" : "ID_TC_NOTICE_";
            var own = c.Owner ?? c.Holder;
            pane.SetText(P0 + "CHANNELTEAMNAME", c.Semi ? (c.Owner != null ? $"{c.Label}: {c.Owner.Name}" : c.Label)
                : c.Owner != null ? SocialNet.T("10322", "%u Ch: %s", c.Number, c.Owner.Name) : SocialNet.T("10324", "Channel %u", c.Number));
            pane.SetText(P0 + "TEAMNAME", own?.Name ?? "");
            pane.SetText(P0 + "TEAMMASTER", own?.Master ?? "");
            pane.SetText(P0 + "TEAMMEMBERCOUNT", own != null ? SocialNet.T("10570", "%d ", own.Members) : "");
            pane.SetText(P0 + "TEAMBATTLELEADER", own == null ? "" : !string.IsNullOrEmpty(c.Leader) ? c.Leader : own.Master);
            pane.SetText(P0 + "TEAMRANK", own?.Grade ?? "");
            pane.SetText(P0 + "TEAMPOINT", own != null ? own.Points.ToString() : "");
            SocialUi.ShowMark(pane, P0 + "TEAMMARK_POS", own?.Mark ?? 0);
            if (c.Active)
            {
                // the channel's queue (the client: its room list), 10321 "(%uCrew)"
                pane.SetText("ID_TC_CHANNELINFO_READYCOUNT", SocialNet.T("10321", "(%uCrew)", c.Queue.Count));
                for (int r = 0; r < 10; r++)
                {
                    bool used = r < c.Queue.Count;
                    foreach (var id in new[] { 2110068 + r, 2110078 + r, 2110092 + r }) { var cc = ById(pane, id); if (cc) cc.gameObject.SetActive(used); }
                    SocialUi.ShowMark(pane, $"ID_TC_CHANNELINFO_READY_MARK{r:00}", used ? c.Queue[r].Mark : 0);
                    if (!used) continue;
                    SetTextId(pane, 2110068 + r, (r + 1).ToString());
                    SetTextId(pane, 2110078 + r, c.Queue[r].Name);
                    SetTextId(pane, 2110092 + r, "");
                }
                pane.SetEnabled("ID_TC_CHANNELINFO_ENTERCHANNEL", Teams.HasCrew && WithinLimits(c, P));
            }
            else
            {
                bool setupWindow = c.SetupUntil > NowMs && c.Since < NowMs;
                pane.SetVisible("ID_TC_NOTICE_BATTLESETUP", !c.Semi && Teams.IAmMaster && c.Owner != null && c.Owner.Name == Teams.CrewName && setupWindow);
                pane.SetVisible("ID_TC_NOTICE_BATTLENOTIFY", c.MapsPublic && !setupWindow);
                pane.SetText("ID_TC_NOTICE_TEAMRANK_TITLE", SocialNet.T("10563", "Crew Rank"));
            }
            if (major) major.SetText("ID_TC_NOTICE_TEXT", $"{c.Label} - {c.CityName} channel {c.Key.Split('-').Last()}\n{ScheduleText()}");
        }

        public static void CloseChannels()
        {
            SocialUi.Close(ref major); SocialUi.Close(ref semi); SocialUi.Close(ref tab); SocialUi.Close(ref pane);
        }

        // ================= entry (0x370 / 0x371) =================
        /// <summary>ENTERCHANNEL: 10553 first on major channels 1-2 when attacking (a lost attack costs a fitted part).</summary>
        public static void AskEnter(int i)
        {
            var c = Get(i);
            if (c == null) return;
            if (!Teams.HasCrew) { UiPopup.Message(SocialNet.T("10425", "You don't have a Crew")); return; }
            if (!WithinLimits(c, P)) { UiPopup.Message($"{c.Label} is open to drivers up to level {c.MaxLevel} with a V{c.MaxGrade} or lower car."); return; }
            bool defending = c.Owner != null && c.Owner.Name == Teams.CrewName;
            if (c.Stake && !defending)
                UiPopup.Show(SocialNet.T("10553", "Challenging for Major Channel.\nIf you fail, one of your equipped parts will be lost.\nDo you wish to continue?").Replace("\\n", "\n"), b => { if (b == UiPopup.Yes) Enter(i); }, UiPopup.Yes, UiPopup.No);
            else Enter(i);
        }

        static int lobbyChannel = -1;
        static bool lobbyDefending, queued;
        static Dictionary<string, object> lobbyState;
        public static int LobbyChannel => lobbyChannel;
        public static bool Queued => queued;

        public static void Enter(int i, Action<bool> done = null)
        {
            var session = CitySession.Current;
            var c = Get(i);
            if (c == null) { done?.Invoke(false); return; }
            if (!SocialNet.Online)
            {
                // offline stand-in: the Battle Zone team race with AI drivers
                if (!session) { UiPopup.Message("Channel Battles start in a city."); done?.Invoke(false); return; }
                CloseChannels();
                PvpLobby.Open(session);
                PvpLobby.Create(new PvpRoom { Title = $"{c.Label} Channel Battle", Team = true, Max = 8, Arena = PvpLobby.Arena });
                done?.Invoke(true);
                return;
            }
            HookNet();
            SocialNet.Request("crew.enter", new Dictionary<string, object> { ["channel"] = i }, r =>
            {
                if (!r.B("ok")) { UiPopup.Message(SocialNet.Err(r)); done?.Invoke(false); return; }
                if (!r.B("inChannel"))
                {
                    // a battle channel is a city channel: move to it (same city), else say where it is raced
                    string city = r.S("city"), key = r.S("key");
                    if (!session || session.City != city) { UiPopup.Message($"{c.Label} is raced in {CityLabel(city)}, channel {key.Split('-').Last()}. Travel to {CityLabel(city)} first."); done?.Invoke(false); return; }
                    OnlineSession.Instance.Net.Request("enter", new Dictionary<string, object> { ["city"] = city, ["channel"] = key }, j =>
                    {
                        if (!j.B("ok") || j.S("channel") != key) { UiPopup.Message($"Could not move to {key}: {j.S("error", "the channel is full")}"); done?.Invoke(false); return; }
                        Enter(i, done);
                    });
                    return;
                }
                CloseChannels();
                OnlineRace.Listen();
                lobbyChannel = i; lobbyDefending = r.B("defending"); queued = false;
                lobbyState = r.O("lobby");
                OpenLobby();
                if (r.I("room") > 0) SocialNet.Request("race.ready", new Dictionary<string, object> { ["ready"] = true }, null);
                done?.Invoke(true);
            });
        }

        // ================= lobbies ATK 2130000 / ATKCOVER 2140000 / DEF 2150000 =================
        static NuiView lobby, atkCover;
        static bool hiddenWait;
        static float startAsked;
        public static NuiView LobbyView => lobby;
        public static NuiView CoverView => atkCover;

        // the channel's room as race.room pushes it (the lobby stands in for WAIT16, so PvpWaitRoom isn't told to expect it)
        static PvpRoom battleRoom;
        static NetClient hookedNet;
        public static PvpRoom BattleRoom => battleRoom;

        static void HookNet()
        {
            var net = OnlineSession.Instance ? OnlineSession.Instance.Net : null;
            if (!net || hookedNet == net) return;
            if (hookedNet) hookedNet.Message -= OnNet;
            hookedNet = net;
            net.Message += OnNet;
        }

        static void OnNet(string t, Dictionary<string, object> m)
        {
            if (t != "race.room") return;
            try
            {
                var info = m.O("room");
                battleRoom = info == null ? null : PvpWaitRoom.FromServer(info);
                if (battleRoom != null && !battleRoom.Crew) battleRoom = null;
                if (lobby) FillLobby();
            }
            catch (Exception ex) { Debug.LogException(ex); }
        }

        static PvpRoom Room => battleRoom ?? (PvpWaitRoom.Room != null && PvpWaitRoom.Room.Online && PvpWaitRoom.Room.Crew ? PvpWaitRoom.Room : null);
        static bool InRoom => Room != null && Room.Members.Any(m => m.Me);
        static bool Defending => InRoom ? (Room.Me.Slot & 1) == 0 : lobbyDefending;

        static void OpenLobby()
        {
            var ui = UiManager.Instance;
            if (!ui || lobbyChannel < 0) return;
            Hook(ui);
            bool def = Defending;
            if (lobby && lobby.Dialog.Id != (def ? DefDlg : AtkDlg)) SocialUi.Close(ref lobby);
            if (!lobby)
            {
                lobby = ui.Open(def ? DefDlg : AtkDlg);
                if (!lobby) return;
                var t = lobby.gameObject.AddComponent<Ticker>();
                t.Every = 0.25f; t.Run = LobbyTick;
                foreach (var c in lobby.Controls) if (c.Data.IdName != null && (c.Data.IdName.Contains("CHAT_") || c.Data.IdName.EndsWith("FIX_SELECT") || c.Data.IdName.Contains("DROPDOWN"))) c.gameObject.SetActive(false);
            }
            FillLobby();
        }

        static string L => Defending ? "ID_TC_CHANNELOBBY_DEF_" : "ID_TC_CHANNELOBBY_ATK_";

        static void FillLobby()
        {
            if (!lobby) return;
            var s = lobbyState ?? new Dictionary<string, object>();
            var c = Get(lobbyChannel);
            string p = L;
            lobby.SetText(p + "CHANNELNAME", SocialNet.T("10347", "Channel %d", c?.Number ?? lobbyChannel + 1) + (c != null && c.Semi ? " (Semi)" : ""));
            lobby.SetText(p + "DEFTEAMNAME", s.O("def")?.S("name") ?? "");
            lobby.SetText(p + "ATKTEAMNAME", s.O("atk")?.S("name") ?? "");
            lobby.SetText(p + "STATIC_STATUS", SocialNet.T("10318", "Leads the match "));
            int round = s.I("round");
            lobby.SetText(p + "STATUSTEXT", round <= 0 ? SocialNet.T("10343", "Waiting…") : round == 1 ? SocialNet.T("10344", "Round 1") : round == 2 ? SocialNet.T("10345", "Round 2") : SocialNet.T("10346", "Final Round"));
            lobby.SetText(p + "STATUSDEF", "50"); lobby.SetText(p + "STATUSATK", "50");
            // the queue (READYn_1 number, READYn_2 crew, MARKnn)
            var q = (s.L("queue") ?? new List<object>()).OfType<Dictionary<string, object>>().ToList();
            int rows = Defending ? 10 : 11;
            for (int r = 1; r <= rows; r++)
            {
                bool used = r <= q.Count;
                lobby.SetVisible($"{p}READY{r}_1", used); lobby.SetVisible($"{p}READY{r}_2", used);
                if (used) { lobby.SetText($"{p}READY{r}_1", r.ToString()); lobby.SetText($"{p}READY{r}_2", q[r - 1].S("crew")); }
                SocialUi.ShowMark(lobby, $"{p}MARK{r:00}", used ? q[r - 1].I("mark") : 0);
            }
            if (Defending) lobby.SetText(p + "RESERVETEAMCOUNT", SocialNet.T("10321", "(%uCrew)", q.Count));
            else
            {
                int mine = q.FindIndex(x => x.S("crew") == Teams.CrewName);
                queued = mine >= 0;
                for (int k = 1; k <= 4; k++) lobby.SetVisible(p + "MYTEAMOUTLINE" + k, false);
                lobby.SetVisible(p + "MYTEAMORDERLINE", mine >= 0);
                lobby.SetVisible(p + "CANCEL", queued && Teams.Mine != null && Teams.Mine.MyRank < 3);
            }
            // members of the own side (MEMBERn, "Level %d", MEMBERCLOSE for rank < 3)
            var side = InRoom ? Room.Members.Where(m => (m.Slot & 1) == (Defending ? 0 : 1)).OrderBy(m => m.Slot).ToList() : new List<PvpMember>();
            for (int i = 1; i <= 8; i++)
            {
                bool used = i <= side.Count;
                lobby.SetVisible($"{p}MEMBER{i}", used); lobby.SetVisible($"{p}MEMBER{i}LV", used);
                lobby.SetVisible($"{p}MEMBERCLOSE_{i}", used && Teams.Mine != null && Teams.Mine.MyRank < 3);
                if (!used) continue;
                lobby.SetText($"{p}MEMBER{i}", side[i - 1].Name);
                lobby.SetText($"{p}MEMBER{i}LV", $"Level {side[i - 1].Level}");
            }
            // HISTORY (0x390): 10348 / 10349 / 10350 / 10351
            var hist = (s.L("history") ?? new List<object>()).OfType<Dictionary<string, object>>().ToList();
            var lines = new List<string>();
            foreach (var h in hist.Skip(Math.Max(0, hist.Count - 6)))
            {
                var at = DateTimeOffset.FromUnixTimeMilliseconds((long)h.N("at")).ToLocalTime();
                lines.Add($"({at:HH}:{at:mm}) " + (h.I("type") == 1
                    ? SocialNet.T("10351", "Crew %s wins Round %d of the Tournament %d in Channel %d.", h.I("winnerSide") == 1 ? h.S("atk") : h.S("def"), h.I("round"), h.I("match"), (c?.Number ?? 0))
                    : h.I("type") == 2 ? $"{h.S("def")} Crew left the channel." : SocialNet.T("10349", "Defending Crew %s vs Challenger Crew %s", h.S("def"), h.S("atk"))));
            }
            lobby.SetText(p + "HISTORY", string.Join("\n", lines));
            // the cover with READY while the crew isn't queued / in the room (ATK)
            var ui = UiManager.Instance;
            bool cover = !Defending && !queued && !InRoom;
            if (cover && !atkCover && ui) atkCover = ui.Open(AtkCoverDlg);
            if (!cover) SocialUi.Close(ref atkCover);
            lobby.SetVisible(p + "VIEWINGCOVER", !(InRoom && Room.State != "lobby"));
        }

        /// <summary>REMAINTIME (0x67c730): "Preparation:" until prepEnd, "Remaining time:" until roundEnd; the WAIT16 stand-in; the host's start.</summary>
        static void LobbyTick()
        {
            if (!lobby) return;
            var s = lobbyState ?? new Dictionary<string, object>();
            long now = NowMs, prep = (long)s.N("prepEnd"), end = (long)s.N("roundEnd");
            string p = L;
            bool preparing = now < prep;
            long left = preparing ? prep - now : Math.Max(0, end - now);
            lobby.SetText(p + "TIMETITLE", preparing ? SocialNet.T("10072", "Preparation:") : SocialNet.T("10074", "Remaining time:"));
            lobby.SetText(p + "REMAINTIME", SocialNet.T("10073", "%02u : %02u", (int)(left / 60000), (int)(left / 1000 % 60)));
            // the battle room waits behind this lobby: WAIT16 steps aside (the lead's PvpWaitRoom hook makes this clean)
            var w = PvpWaitRoom.View;
            if (w && InRoom && w.gameObject.activeSelf) { w.gameObject.SetActive(false); hiddenWait = true; }
            if (InRoom && Defending != (lobby.Dialog.Id == DefDlg)) { OpenLobby(); return; }
            AutoStart(prep);
            if (atkCover && !InRoom) atkCover.SetEnabled("ID_TC_CHANNELOBBY_ATKCOVER_READY", Teams.Mine != null && Teams.Mine.MyRank < 3);
            if (atkCover) EventBlink(atkCover, "ID_TC_CHANNELOBBY_ATKCOVER_READY");
        }

        static void EventBlink(NuiView v, string id) { var c = v.Control(id); if (c) c.Selected = Time.unscaledTime % 0.8f < 0.4f; }

        /// <summary>The room host starts a round once both sides are equal, everyone is ready and the preparation is over.</summary>
        static void AutoStart(long prepEnd)
        {
            var room = Room;
            if (!InRoom || room.State != "lobby" || !room.IAmHost || NowMs < prepEnd || Time.unscaledTime - startAsked < 3f) return;
            int a = room.Members.Count(m => (m.Slot & 1) == 0), b = room.Members.Count(m => (m.Slot & 1) == 1);
            if (a == 0 || a != b || room.Members.Any(m => !m.Ready && !m.Me)) return;
            startAsked = Time.unscaledTime;
            SocialNet.Request("race.startRace", null, r => { if (!r.B("ok") && r.S("error") != "not everyone is ready") ChatBox.Ensure()?.Add("team", "", SocialNet.Err(r)); });
        }

        /// <summary>ATKCOVER_READY / CANCEL (0x384).</summary>
        public static void Ready(bool on, Action<bool> done = null)
        {
            if (lobbyChannel < 0) { done?.Invoke(false); return; }
            SocialNet.Request("crew.ready", new Dictionary<string, object> { ["channel"] = lobbyChannel, ["on"] = on }, r =>
            {
                if (!r.B("ok")) { UiPopup.Message(SocialNet.Err(r)); done?.Invoke(false); return; }
                queued = r.B("queued");
                RefreshLobby();
                done?.Invoke(true);
            });
        }

        public static void RefreshLobby()
        {
            if (lobbyChannel < 0) return;
            SocialNet.Request("crew.lobby", new Dictionary<string, object> { ["channel"] = lobbyChannel }, r => { if (r.B("ok")) { lobbyState = r; FillLobby(); } });
        }

        static void Kick(int row)
        {
            var room = Room;
            if (!InRoom) return;
            var side = room.Members.Where(m => (m.Slot & 1) == (Defending ? 0 : 1)).OrderBy(m => m.Slot).ToList();
            if (row > side.Count) return;
            var m = side[row - 1];
            if (m.Me) { UiPopup.Message(SocialNet.T("10571", "You cannot kick yourself.")); return; }
            UiPopup.Show(SocialNet.T("10240", "Do you want to kick\n%s?", m.Name).Replace("\\n", "\n"), b => { if (b == UiPopup.Yes) SocialNet.Request("race.kick", new Dictionary<string, object> { ["id"] = m.Id }, null); }, UiPopup.Yes, UiPopup.No);
        }

        /// <summary>ESC: 10326 (an attacker under 3 waiting members drops from the list) / 10327 (no defender left = the channel is lost).</summary>
        static void AskLeaveLobby()
        {
            string text = Defending ? SocialNet.T("10327", "If there are no waiting members, you will lose Channel ownership.\nAre you sure you want to leave?")
                                    : SocialNet.T("10326", "If the number of waiting Crew members is under 3, the Crew will be dropped from the wait list.\nAre you sure you want to leave?");
            UiPopup.Show(text.Replace("\\n", "\n"), b => { if (b == UiPopup.Yes) LeaveLobby(); }, UiPopup.Yes, UiPopup.No);
        }

        public static void LeaveLobby()
        {
            if (InRoom) SocialNet.Request("race.leave", null, null);
            SocialNet.Request("crew.leaveLobby", null, null);
            CloseLobby();
            OpenChannels();
        }

        public static void CloseLobby()
        {
            SocialUi.Close(ref lobby); SocialUi.Close(ref atkCover);
            if (hiddenWait && PvpWaitRoom.View) PvpWaitRoom.View.gameObject.SetActive(true);
            hiddenWait = false;
            lobbyChannel = -1; lobbyState = null; battleRoom = null;
        }

        /// <summary>VIEWING: watch the running round.</summary>
        static void WatchRound()
        {
            var c = Get(lobbyChannel);
            if (c == null || c.Room <= 0) { UiPopup.Message(SocialNet.T("10200", "No Channel Battles in progress at this time.")); return; }
            SocialNet.Request("race.observe", new Dictionary<string, object> { ["id"] = c.Room }, r => { if (!r.B("ok")) UiPopup.Message(SocialNet.Err(r)); });
        }

        // ================= pushes (crew.lobby / crew.turn / crew.items / crew.cheer) =================
        public static void OnPush(string t, Dictionary<string, object> m)
        {
            switch (t)
            {
                case "crew.lobby":
                    if (m.I("channel") == lobbyChannel) { lobbyState = m; FillLobby(); }
                    break;
                case "crew.turn":
                    ChatBox.Ensure()?.Add("team", "", $"{m.S("crew")} Crew: your turn to attack {Get(m.I("channel"))?.Label ?? "the channel"}.");
                    if (m.I("channel") == lobbyChannel && !InRoom) Enter(lobbyChannel);
                    break;
                case "crew.items":
                    ShowItems(m.S("kind") == "get", (m.L("parts") ?? new List<object>()).OfType<Dictionary<string, object>>().ToList());
                    break;
                case "crew.cheer":
                    OnCheer(m.S("from"), m.S("target"));
                    break;
            }
        }

        // ================= map setup 2240000 / notify 2250000 =================
        static NuiView setup, notify;
        static int setupChannel = -1;
        static readonly int[] setupTrack = { 0, 1 }, setupTraffic = { 1, 1 };
        static readonly bool[] setupRandom = { false, false };
        static List<RaceTrack> setupTracks = new List<RaceTrack>();
        public static NuiView SetupView => setup;
        public static NuiView NotifyView => notify;

        public static void OpenSetup(int i)
        {
            var c = Get(i);
            var ui = UiManager.Instance;
            if (c == null || !ui) return;
            Hook(ui);
            SocialUi.Close(ref setup);
            setup = ui.Open(SetupDlg);
            if (!setup) return;
            setupChannel = i;
            setupTracks = RaceMission.TracksFor(c.City).Where(t => t.Line.Count >= 2 && t.CityType == 1).ToList();   // not the tutorial / lap-time tracks
            setupTrack[0] = 0; setupTrack[1] = Math.Min(1, Math.Max(0, setupTracks.Count - 1));
            setup.SetText("ID_TC_CHANNELSETUP_TEAMNAME", SocialNet.T("10342", "%s Crew is defending Channel %d.", Teams.CrewName, c.Number));
            setup.SetText("ID_TC_CHANNELSETUP_DESC", SocialNet.T("11387", "If you don't select a channel battle track within 24 hours after took ownership of a channel, a random track will be selected."));
            // only the channel's city is open (the client: net+0x258 city bits)
            foreach (var m in new[] { 1, 2 })
                foreach (var city in new[] { "MOONPALACE", "KOINONIA", "CRAS", "OROS", "TAI", "ALL" })
                    setup.SetEnabled($"ID_TC_CHANNELSETUP_{m}_CITY_{city}", city == c.City.ToUpperInvariant());
            FillSetup();
        }

        static void StepMap(int m, int dir)
        {
            if (setupTracks.Count == 0) return;
            setupTrack[m - 1] = ((setupTrack[m - 1] + dir) % setupTracks.Count + setupTracks.Count) % setupTracks.Count;
            FillSetup();
        }

        static void FillSetup()
        {
            if (!setup) return;
            string[] tr = { "IDLE", "NORMAL", "JAM", "RANDOM" };
            foreach (var m in new[] { 1, 2 })
            {
                var t = setupTracks.Count > 0 ? setupTracks[setupTrack[m - 1] % setupTracks.Count] : null;
                setup.SetText($"ID_TC_CHANNELSETUP_{m}_MAPNAME", setupRandom[m - 1] ? "Random" : t != null ? Pvp.TrackName(t) : "-");
                for (int k = 0; k < 4; k++) setup.Control($"ID_TC_CHANNELSETUP_{m}_TRAFFIC_{tr[k]}")?.SetChecked(setupTraffic[m - 1] == k);
                setup.Control($"ID_TC_CHANNELSETUP_{m}_RANDOM")?.SetChecked(setupRandom[m - 1]);
            }
        }

        /// <summary>OK (0x38a): two tracks and their traffic; RANDOM traffic = rand() % 3 (0x675bb0).</summary>
        public static void SendSetup(Action<bool> done = null)
        {
            if (!setup || setupTracks.Count == 0) { done?.Invoke(false); return; }
            var maps = new List<object>();
            for (int m = 0; m < 2; m++)
            {
                var t = setupRandom[m] ? setupTracks[UnityEngine.Random.Range(0, setupTracks.Count)] : setupTracks[setupTrack[m] % setupTracks.Count];
                int traffic = setupTraffic[m] == 3 ? UnityEngine.Random.Range(0, 3) : setupTraffic[m];
                maps.Add(new Dictionary<string, object> { ["track"] = new Dictionary<string, object> { ["key"] = Pvp.TrackKey(t), ["name"] = t.Name, ["laps"] = t.Laps, ["length"] = (double)t.Length }, ["traffic"] = traffic });
            }
            SocialNet.Request("crew.setup", new Dictionary<string, object> { ["channel"] = setupChannel, ["maps"] = maps }, r =>
            {
                if (!r.B("ok")) { UiPopup.Message(SocialNet.Err(r)); done?.Invoke(false); return; }
                SocialUi.Close(ref setup);
                Load(() => { if (pane) Select(selected); });
                done?.Invoke(true);
            });
        }

        /// <summary>BATTLENOTIFY (0x38e -> 0x38f): MAPNOTIFY with 10342 and the two maps.</summary>
        public static void OpenMapNotify(int i)
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            Hook(ui);
            Action<string, List<Map>> show = (owner, maps) =>
            {
                SocialUi.Close(ref notify);
                notify = ui.Open(MapNotifyDlg);
                if (!notify) return;
                var c = Get(i);
                notify.SetText("ID_TC_CH_MAPNOTIFY_TEAMNAME", SocialNet.T("10342", "%s Crew is defending Channel %d.", owner, c?.Number ?? i + 1));
                string[] tr = { "IDLE", "NORMAL", "JAM", "RANDOM" };
                for (int m = 1; m <= 2; m++)
                {
                    var map = m <= maps.Count ? maps[m - 1] : null;
                    notify.SetText($"ID_TC_CH_MAPNOTIFY_{m}_MAPNAME", map != null ? map.Name : "Random");
                    for (int k = 0; k < 4; k++) notify.Control($"ID_TC_CH_MAPNOTIFY_{m}_TRAFFIC_{tr[k]}")?.SetChecked(map != null ? map.Traffic == k : k == 3);
                }
            };
            if (!SocialNet.Online) { var c = Get(i); show(c?.Owner?.Name ?? "", c?.Maps ?? new List<Map>()); return; }
            SocialNet.Request("crew.maps", new Dictionary<string, object> { ["channel"] = i }, r =>
            {
                if (!r.B("ok")) { UiPopup.Message(SocialNet.Err(r)); return; }
                var maps = (r.L("maps") ?? new List<object>()).OfType<Dictionary<string, object>>().Select(m => new Map { Key = m.O("track")?.S("key") ?? "", Name = m.O("track")?.S("name") ?? "", Traffic = m.I("traffic") }).ToList();
                show(r.S("owner"), maps);
            });
        }

        // ================= part stakes 2220000 / 2230000 =================
        static NuiView itemGet, itemMiss;
        public static NuiView ItemGetView => itemGet;
        public static NuiView ItemMissView => itemMiss;
        static readonly string[] PartTypes = { "Speed", "Accel", "Durability", "Boost" };
        static readonly string[] Grades = { "N", "S", "H", "U" };

        static string PartName(Dictionary<string, object> p) { string n = $"{Grades[Mathf.Clamp(p.I("grade"), 0, 3)]} {PartTypes[Mathf.Clamp(p.I("type"), 0, 3)]} Part"; return p.I("enhance") > 0 ? $"{n} + {p.I("enhance")}" : n; }

        /// <summary>0x391 BATTLEITEMGET (the parts the defending Master won) / 0x392 BATTLEITEMMISS (the part I lost).</summary>
        public static void ShowItems(bool get, List<Dictionary<string, object>> parts)
        {
            var ui = UiManager.Instance;
            if (!ui || parts.Count == 0) return;
            Hook(ui);
            if (get)
            {
                SocialUi.Close(ref itemGet);
                itemGet = ui.Open(ItemGetDlg);
                if (!itemGet) return;
                for (int i = 1; i <= 12; i++)
                {
                    bool used = i <= parts.Count;
                    itemGet.SetVisible("ID_TC_BATTLEITEMGET_SLOT_" + i, used);
                    if (used) itemGet.SetText("ID_TC_BATTLEITEMGET_SLOT_" + i, PartName(parts[i - 1]));
                }
            }
            else
            {
                SocialUi.Close(ref itemMiss);
                itemMiss = ui.Open(ItemMissDlg);
                if (!itemMiss) return;
                var p = parts[0];
                int type = Mathf.Clamp(p.I("type"), 0, 3);
                string[] descKey = { "10352", "10356", "10354", "10358" }, statKey = { "10353", "10357", "10355", "10359" };
                string[] descFb = { "%sSpeed Part", "%sAcceleration Part", "%sDurability Part", "%sBooster Part" }, statFb = { "Speed + %u", "Acceleration + %u", "Durability + %u", "Booster + %u" };
                itemMiss.SetText("ID_TC_BATTLEITEMMISS_NAME", PartName(p));
                itemMiss.SetText("ID_TC_BATTLEITEMMISS_DESC1", SocialNet.T(descKey[type], descFb[type], Grades[Mathf.Clamp(p.I("grade"), 0, 3)] + " "));
                itemMiss.SetText("ID_TC_BATTLEITEMMISS_DESC2", SocialNet.T(statKey[type], statFb[type], (p.I("grade") + 1) * 5 + p.I("enhance")));
                itemMiss.SetText("ID_TC_BATTLEITEMMISS_DESC3", "");
                itemMiss.SetText("ID_TC_BATTLEITEMMISS_DESC4", "");
            }
            if (GameEffects.Sounds) AudioLibrary.PlaySfx(get ? "sfx/SID_ITEM_DROP_GET1" : "sfx/SID_PVP_LOSE");
        }

        // ================= spectating 3390000, cheering 2170000, replays 3410000 =================
        static NuiView selView, cheer, replay;
        static readonly List<Dictionary<string, object>> liveRows = new List<Dictionary<string, object>>();
        static bool cheerOpen;
        static float cheerAt = -10f;
        public static NuiView SpectateView => selView;
        public static NuiView CheerView => cheer;
        public static NuiView ReplayView => replay;

        /// <summary>TC VIEWING (0x387 -> 0x388): the channels with a battle; none -> replays (or 10200).</summary>
        public static void OpenSpectate()
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            Hook(ui);
            if (!SocialNet.Online) { OpenReplays(); return; }
            SocialNet.Request("crew.spectate", null, r =>
            {
                liveRows.Clear();
                liveRows.AddRange((r.L("rows") ?? new List<object>()).OfType<Dictionary<string, object>>());
                if (liveRows.Count == 0) { OpenReplays(); return; }
                SocialUi.Close(ref selView);
                selView = ui.Open(SelViewDlg);
                if (!selView) return;
                for (int i = 1; i <= 10; i++)
                {
                    var row = liveRows.FirstOrDefault(x => x.I("channel") == i - 1);   // CHnn = Channel nn (0x565cd0)
                    string n = i.ToString("00");
                    selView.SetVisible("ID_SELCH_VIEWING_STATUS_WAIT_" + i, row != null && row.S("state") == "wait");
                    selView.SetVisible("ID_SELCH_VIEWING_STATUS_PLAY_" + i, row != null && row.S("state") == "play");
                    selView.SetVisible("ID_SELCH_VIEWING_OBSERVE" + n, row != null);
                    selView.SetVisible("ID_SELCH_VIEWING_OWNERNAME_" + i, row != null);
                    selView.SetVisible("ID_SELCH_VIEWING_ATKNAME_" + i, row != null);
                    if (row == null) continue;
                    selView.SetText("ID_SELCH_VIEWING_OBSERVE" + n, row.B("spectate") ? SocialNet.T("10201", "%d Spectator(s)", row.I("viewers")) : SocialNet.T("10199", "Not permitted"));
                    selView.SetText("ID_SELCH_VIEWING_OWNERNAME_" + i, row.S("owner"));
                    selView.SetText("ID_SELCH_VIEWING_ATKNAME_" + i, row.S("attacker"));
                }
            });
        }

        /// <summary>A live row (0x38b): watch the channel's room (the spectator moves to its city channel first).</summary>
        public static void Spectate(int k)
        {
            var row = liveRows.FirstOrDefault(x => x.I("channel") == k);
            if (row == null) { UiPopup.Message(SocialNet.T("10200", "No Channel Battles in progress at this time.")); return; }
            var session = CitySession.Current;
            string city = row.S("city"), key = row.S("key");
            Action watch = () =>
            {
                OnlineRace.Listen();
                PvpWaitRoom.ExpectRoom();
                SocialNet.Request("race.observe", new Dictionary<string, object> { ["id"] = row.I("room") }, r => { if (!r.B("ok")) { PvpWaitRoom.CancelExpect(); UiPopup.Message(SocialNet.Err(r)); } else SocialUi.Close(ref selView); });
            };
            if (OnlineSession.Instance && OnlineSession.Instance.Channel == key) { watch(); return; }
            if (!session || session.City != city) { UiPopup.Message($"{row.S("label")} is raced in {CityLabel(city)}. Travel there to watch it."); return; }
            OnlineSession.Instance.Net.Request("enter", new Dictionary<string, object> { ["city"] = city, ["channel"] = key }, j => { if (j.B("ok")) watch(); else UiPopup.Message(SocialNet.Err(j)); });
        }

        /// <summary>The cheering panel over a watched crew race (0x75b980): POP / PUSH, COUNT, USE -> crew.cheer, 5 s apart.</summary>
        public static void UpdateCheer()
        {
            var ui = UiManager.Instance;
            bool show = ui && PvpSpectate.Watching && PvpWaitRoom.Room != null && PvpWaitRoom.Room.Crew;
            if (!show) { SocialUi.Close(ref cheer); return; }
            if (!cheer) { Hook(ui); cheer = ui.Open(CheerDlg); }
            FillCheer();
        }

        static void FillCheer()
        {
            if (!cheer) return;
            cheer.SetVisible("ID_TC_VIEWING_CHEERING_POP", !cheerOpen);
            cheer.SetVisible("ID_TC_VIEWING_CHEERING_PUSH", cheerOpen);
            foreach (var id in new[] { "COVER", "COUNT", "USE" }) cheer.SetVisible("ID_TC_VIEWING_CHEERING_" + id, cheerOpen);
            cheer.SetText("ID_TC_VIEWING_CHEERING_COUNT", (P?.Count(Fireworks) ?? 0).ToString());
            cheer.SetEnabled("ID_TC_VIEWING_CHEERING_USE", Time.unscaledTime - cheerAt >= 5f && (P?.Count(Fireworks) ?? 0) > 0);
        }

        public static void Cheer(Action<bool> done = null)
        {
            string target = PvpSpectate.WatchedName;
            if (string.IsNullOrEmpty(target) || Time.unscaledTime - cheerAt < 5f) { done?.Invoke(false); return; }
            cheerAt = Time.unscaledTime;
            SocialNet.Request("crew.cheer", new Dictionary<string, object> { ["target"] = target }, r =>
            {
                if (!r.B("ok")) { ChatBox.System(SocialNet.Err(r)); done?.Invoke(false); return; }
                FillCheer();
                done?.Invoke(true);
            });
        }

        public static string LastCheer { get; private set; }

        /// <summary>Shows / hides the cheering panel with the spectator view of a crew race.</summary>
        sealed class CheerRunner : MonoBehaviour
        {
            float t;
            void Update() { t += Time.unscaledDeltaTime; if (t < 0.25f) return; t = 0; try { UpdateCheer(); } catch (Exception ex) { Debug.LogException(ex); } }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        public static void EnsureRunner()
        {
            if (UnityEngine.Object.FindAnyObjectByType<CheerRunner>()) return;
            var go = new GameObject("Channel Battle");
            go.AddComponent<CheerRunner>();
            UnityEngine.Object.DontDestroyOnLoad(go);
        }

        /// <summary>0x394 back: the cheered car's fireworks and a line for everyone in the room.</summary>
        static void OnCheer(string from, string target)
        {
            LastCheer = $"{from} -> {target}";
            ChatBox.Ensure()?.Add("team", "", $"{from} cheers {target} with Mittron Fireworks!");
            PvpChat.System($"{from} cheers {target} with Mittron Fireworks!");
            if (GameEffects.Sounds) AudioLibrary.PlaySfx("sfx/SID_FIRECRACKER");
            if (target == P?.Name) GameEffects.MittronFirework();   // 0x50a4b0: booster + mitbomb_ui
        }

        static int replayPage;
        static readonly List<Dictionary<string, object>> replayRows = new List<Dictionary<string, object>>();

        /// <summary>The replay list 3410000 (0x395 -> 0x396), 8 rows a page.</summary>
        public static void OpenReplays()
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            Hook(ui);
            SocialUi.Close(ref replay);
            replay = ui.Open(ReplayDlg);
            replayPage = 0;
            LoadReplays();
        }

        static void LoadReplays()
        {
            if (!replay) return;
            if (!SocialNet.Online)
            {
                // offline: the Channel Battle rounds raced here (PvpReplay.Local), free to watch
                replayRows.Clear();
                var local = Game.PvpReplay.Local;
                for (int i = replayPage * 8; i < local.Count && i < replayPage * 8 + 8; i++)
                {
                    var rec = local[i];
                    replayRows.Add(new Dictionary<string, object>
                    {
                        ["id"] = (double)(-1 - i), ["label"] = (rec.Title ?? "").Replace(" Channel Battle", ""), ["match"] = 1.0, ["round"] = (double)Math.Max(1, rec.Round), ["views"] = 0.0, ["price"] = 0.0,
                        ["owner"] = string.IsNullOrEmpty(rec.Owner) ? "Blue" : rec.Owner, ["challenger"] = string.IsNullOrEmpty(rec.Challenger) ? "Red" : rec.Challenger,
                    });
                }
                FillReplays(local.Count);
                return;
            }
            SocialNet.Request("crew.replays", new Dictionary<string, object> { ["page"] = replayPage }, r =>
            {
                replayRows.Clear();
                replayRows.AddRange((r.L("rows") ?? new List<object>()).OfType<Dictionary<string, object>>());
                FillReplays(r.I("total"));
            });
        }

        static void FillReplays(int total)
        {
            if (!replay) return;
            replay.SetText("ID_TC_REPLAY_TITLE", replayRows.Count == 0 ? SocialNet.T("10200", "No Channel Battles in progress at this time.") : SocialNet.T("11261", "Replay"));
            for (int i = 1; i <= 8; i++)
            {
                var r = i <= replayRows.Count ? replayRows[i - 1] : null;
                string n = i.ToString("00");
                foreach (var id in new[] { "SEL", "MATCHNAME", "PLAYCOUNT", "MITO", "OWNTEAMNAME", "CHALLENGETEAMNAME", "VS" }) replay.SetVisible("ID_TC_REPLAY_" + id + n, r != null);
                SocialUi.ShowMark(replay, "ID_TC_REPLAY_OWNTEAMMARK" + n, r?.I("ownerMark") ?? 0);
                SocialUi.ShowMark(replay, "ID_TC_REPLAY_CHALLENGETEAMMARK" + n, r?.I("challengerMark") ?? 0);
                if (r == null) continue;
                replay.SetText("ID_TC_REPLAY_MATCHNAME" + n, $"{r.S("label")} " + SocialNet.T("11259", "Match %d", r.I("match")) + $" R{r.I("round")}");
                replay.SetText("ID_TC_REPLAY_PLAYCOUNT" + n, SocialNet.T("11258", "%d Views", r.I("views")));
                replay.SetText("ID_TC_REPLAY_MITO" + n, $"{r.I("price")} Mito");
                replay.SetText("ID_TC_REPLAY_OWNTEAMNAME" + n, r.S("owner"));
                replay.SetText("ID_TC_REPLAY_CHALLENGETEAMNAME" + n, r.S("challenger"));
            }
        }

        static void AskReplay(int k)
        {
            if (k >= replayRows.Count) return;
            var r = replayRows[k];
            int price = r.I("price");
            string q = price > 0 ? SocialNet.T("11256", "You need %d Mito to see the selected replay. Do you want to proceed?", price) : SocialNet.T("11260", "Do you want to watch the selected replay?");
            UiPopup.Show(q, b => { if (b == UiPopup.Yes) WatchReplay(r.I("id")); }, UiPopup.Yes, UiPopup.No);
        }

        public static Dictionary<string, object> LastReplay { get; private set; }

        /// <summary>The replay player of the last watched replay (tests).</summary>
        public static Game.ReplayPlayer LastPlayer { get; private set; }

        /// <summary>
        /// A replay (0x73d7b0 -> 0x742af0 -> 0x742f50): its fee, then the recorded race played in VIEWING with the Ghost
        /// Battle offered (one ghost ticket comes with a bought replay); the round's result board when the recording can't
        /// be played here (another city, or a round recorded before the server kept poses). Offline the rounds raced on this
        /// client (ids below 0) play the same way.
        /// </summary>
        public static void WatchReplay(int id, Action<bool> done = null)
        {
            if (id < 0)
            {
                int k = -1 - id;
                var local = Game.PvpReplay.Local;
                if (k >= local.Count) { done?.Invoke(false); return; }
                SocialUi.Close(ref replay);
                if (!Game.PvpReplay.Play(local[k], () => { }, out var why, true)) { UiPopup.Message(why); done?.Invoke(false); return; }
                LastPlayer = Game.PvpReplay.Current;
                done?.Invoke(true);
                return;
            }
            SocialNet.Request("crew.replay", new Dictionary<string, object> { ["id"] = id }, r =>
            {
                if (!r.B("ok")) { UiPopup.Message(SocialNet.Err(r)); done?.Invoke(false); return; }
                var rp = r.O("replay");
                LastReplay = rp;
                var recJson = rp.O("rec");
                if (recJson != null)
                {
                    var rec = Game.ReplayRecording.FromJson(recJson);
                    rec.Owner = rp.S("owner", ""); rec.Challenger = rp.S("challenger", ""); rec.Round = Math.Max(rec.Round, rp.I("round"));
                    if (string.IsNullOrEmpty(rec.Title)) rec.Title = $"{rp.S("label")} " + SocialNet.T("11259", "Match %d", rp.I("match")) + $" R{rp.I("round")}";
                    SocialUi.Close(ref replay);
                    // the Ghost Battle needs the Ghost Pass or the channel's ticket the server gave with the replay
                    if (r.ContainsKey("driver") && r.O("driver") != null) { var tk = r.S("ticket"); if (!string.IsNullOrEmpty(tk)) PlayerProfile.Current?.Give(tk, 1); }
                    if (Game.PvpReplay.Play(rec, () => { }, out var why, false)) { Game.PvpGhost.SetChannelTicket(rp.I("channel")); LastPlayer = Game.PvpReplay.Current; done?.Invoke(true); return; }
                    ChatBox.System(why);
                }
                var lines = (rp.L("results") ?? new List<object>()).OfType<Dictionary<string, object>>().OrderBy(x => x.I("place") == 0 ? 99 : x.I("place"))
                    .Select(x => $"{(x.I("place") > 0 ? Pvp.Ordinal(x.I("place")) : "DNF"),-5} {x.S("name"),-14} {(x.I("team") == 0 ? rp.S("owner") : rp.S("challenger"))}  {TimeSpan.FromSeconds(x.N("time")):mm\\:ss\\.ff}");
                UiPopup.Message($"{rp.S("label")} - " + SocialNet.T("11259", "Match %d", rp.I("match")) + $", Round {rp.I("round")}\n{rp.S("owner")} vs {rp.S("challenger")}: {(string.IsNullOrEmpty(rp.S("winner")) ? "draw" : rp.S("winner") + " won")}\n\n" + string.Join("\n", lines));
                LoadReplays();
                done?.Invoke(true);
            });
        }

        // ================= the crew ladder (LADDER TABTEAM 3960000) =================
        static NuiView ladderFrame, ladder;
        static NuiListBox ladderList;
        static int ladderPage;
        static readonly List<Dictionary<string, object>> ladderRows = new List<Dictionary<string, object>>();
        public static NuiView LadderView => ladder;
        public static IReadOnlyList<Dictionary<string, object>> LadderRows => ladderRows;

        /// <summary>The crew tab of the ladder window: rank, class, crew, points ("T" query 0x518b00; the server ranks).</summary>
        public static void OpenCrewLadder()
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            Hook(ui);
            if (!ladderFrame) ladderFrame = ui.Open(LadderFrame);
            SocialUi.Close(ref ladder);
            ladder = ui.Open(LadderTeamDlg);
            if (!ladder) return;
            ladderList = new NuiListBox(ladder.Control("ID_LADDER_TABTEAM_LB"), 10, 24);
            ladderPage = 0;
            LoadLadder();
        }

        public static void CloseLadder() { SocialUi.Close(ref ladder); SocialUi.Close(ref ladderFrame); }

        static void LoadLadder()
        {
            if (!ladder) return;
            string filter = ladder.GetInput("ID_LADDER_TABTEAM_EB").Trim();
            Action<List<Dictionary<string, object>>> fill = rows =>
            {
                ladderRows.Clear();
                ladderRows.AddRange(rows.Where(x => filter.Length == 0 || x.S("name").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0));
                ladderList?.SetRows(ladderRows.Select(x => ($"{x.I("rank"),3}. {x.S("ladderClass")}  {x.S("name")}", $"{x.I("points"):N0} pts  {x.I("captures")} ch")).ToList(), 0);
                for (int i = 0; i < 10; i++) { var b = ladder.Control("ID_LADDER_TABTEAM_PAGE" + i); if (b) { b.SetText((ladderPage / 10 * 10 + i + 1).ToString()); b.Selected = ladderPage % 10 == i; } }
            };
            if (!SocialNet.Online)
            {
                var me = Teams.Mine;
                var list = new List<Dictionary<string, object>>();
                if (me != null) list.Add(new Dictionary<string, object> { ["rank"] = 1.0, ["ladderClass"] = "S", ["name"] = me.Name, ["points"] = (double)me.Points, ["captures"] = (double)me.Captures });
                fill(list);
                return;
            }
            SocialNet.Request("crew.ranking", new Dictionary<string, object> { ["page"] = ladderPage }, r => fill((r.L("rows") ?? new List<object>()).OfType<Dictionary<string, object>>().ToList()));
        }

        static void LadderDetail()
        {
            var row = ladderRows.FirstOrDefault();
            if (row == null) return;
            UiPopup.Message($"{row.S("name")} Crew\n{SocialNet.T("11332", "Crew Ladder")}: {row.S("ladderClass")}   {SocialNet.T("11333", "Crew Ladder Rank")}: {row.I("rank")}\n" +
                            $"{SocialNet.T("10563", "Crew Rank")}: {row.S("grade")}   {row.I("members")} members\n{row.I("points"):N0} points, {row.I("wins")} wins / {row.I("losses")} losses, {row.I("captures")} captures");
        }
    }
}
