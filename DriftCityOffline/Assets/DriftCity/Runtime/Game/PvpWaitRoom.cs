using System;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// The 16-player wait room (PVP.md section 5): WAIT16 1100000 with the slot grid, host START / guest READY, team
    /// select, the host's slot buttons (close / reopen / add AI / remove AI / kick), map browsing with the city tabs,
    /// RANDOM / REVERSE / RECOMMEND and the traffic buttons, and the track picture SELMAP 1160000. Offline the AI drivers
    /// are always ready and an AI host starts once the player is ready; online every change goes through the server
    /// (race.settings / race.slot / race.team / race.kick / race.ready / race.startRace) and the room is redrawn from its
    /// race.room messages. After a race the results run and everyone comes back here.
    /// </summary>
    public static class PvpWaitRoom
    {
        const int Dialog = 1100000, MapDialog = 1160000, EmoticonDialog = 1130000, RmCarDialog = 1140000, RmInfoDialog = 1150000;
        public const float EmoticonSeconds = 5f;
        static NuiView emoticons, rmCar, rmInfo;
        static readonly List<(GameObject icon, float until)> shownEmoticons = new List<(GameObject, float)>();
        static float kickCount = -1f;
        static int kickWarned;
        const string P = "ID_PVP_WAIT16_";
        static readonly string[] CityTabs = { "CITY_1ST", "CITY_2ND", "CITY_3RD", "CITY_4TH", "CITY_5TH" };
        static readonly string[] Cities = { "moonpalace", "koinonia", "cras", "oros", "taipei" };

        static NuiView view, selmap;
        static PvpRoom room;
        static Runner runner;
        static UiManager hooked;
        static bool expect;          // online: the next race.room opens the wait room
        static bool racing;          // a race (or its results) of this room is running
        static Dictionary<string, object> pendingServerRoom;
        static float aiStartAt = -1f, readyDebounce;

        public static bool IsOpen => view;
        public static PvpRoom Room => room;
        public static NuiView View => view;
        public static NuiView MapView => selmap;
        public static bool Racing => racing;

        sealed class Runner : MonoBehaviour { void Update() => Tick(); }

        // ---------------- server rooms ----------------
        /// <summary>A server race room (race.js info()) as a PvpRoom.</summary>
        public static PvpRoom FromServer(Dictionary<string, object> m)
        {
            var r = new PvpRoom { Online = true };
            r.Id = m.I("id");
            r.HostId = m.I("host");
            r.State = m.S("state", "lobby");
            r.Title = m.S("title", "");
            r.Team = m.B("team");
            r.Max = Mathf.Clamp(m.I("max") > 0 ? m.I("max") : 8, 1, 16);
            r.Locked = m.B("locked");
            r.Spectate = m.B("spectate");
            r.NoGate = m.B("noGate");
            var crew = m.O("crew");
            if (crew != null) { r.Crew = true; r.CrewA = crew.S("a", ""); r.CrewB = crew.S("b", ""); }
            r.Wager = m.I("wager");
            r.Mission = m.I("mission");
            r.Observers = m.I("observers");
            r.ObserverNames = (m.L("observerNames") ?? new List<object>()).OfType<string>().ToList();
            r.Spectating = r.ObserverNames.Contains(PlayerProfile.Current?.Name ?? "\u0001");
            r.Arena = m.ContainsKey("arena") ? m.I("arena") : Pvp.ArenaFree;
            r.MapFlags = m.ContainsKey("mapFlags") ? m.I("mapFlags") : Pvp.MapCityAll;
            var t = m.O("track");
            if (t != null)
            {
                r.TrackKey = t.S("key"); r.TrackName = t.S("name"); r.Laps = t.I("laps");
                var city = (r.TrackKey ?? "").Split('/')[0];
                r.Track = Pvp.FindTrack(city, r.TrackKey);
            }
            var slots = m.L("slots");
            r.ResetSlots();
            if (slots != null) for (int i = 0; i < PvpRoom.SlotCount && i < slots.Count; i++) r.Slots[i] = Convert.ToInt32(slots[i]);
            string me = PlayerProfile.Current?.Name;
            int n = 0;
            foreach (var o in m.L("racers") ?? new List<object>())
            {
                if (!(o is Dictionary<string, object> d)) continue;
                bool ai = d.B("ai");
                var mem = new PvpMember
                {
                    Id = d.I("id"), Name = d.S("name"), Level = Mathf.Max(1, d.I("level")), Ready = d.B("ready") || ai,
                    Slot = d.ContainsKey("slot") ? d.I("slot") : n, Me = !ai && d.S("name") == me, Ai = ai,
                    Car = ai ? GameTables.Car(d.I("car")) : null, Skill = ai ? (float)d.N("skill", 0.9) : 0.9f,
                };
                n++;
                r.Members.Add(mem);
                if (slots == null && mem.Slot < PvpRoom.SlotCount) r.Slots[mem.Slot] = mem.Id;
            }
            return r;
        }

        internal static void ExpectRoom() => expect = true;
        internal static void CancelExpect() => expect = false;

        /// <summary>race.room from the server: null = we left / were removed.</summary>
        internal static void OnServerRoom(Dictionary<string, object> info)
        {
            if (info == null)
            {
                expect = false;
                pendingServerRoom = null;
                if (room != null && room.Online) { room = null; CloseViews(); if (!racing) PvpLobby.Reopen(); }
                return;
            }
            if (racing) { pendingServerRoom = info; return; }   // shown again once the results close
            var r = FromServer(info);
            if (Social.ChannelBattle.LobbyView && r.Crew) return;   // a Channel Battle room: its ATK/DEF lobby shows it
            if (r.State != "lobby") return;
            if (view && room != null && room.Online) { room = r; Refresh(); return; }
            if (expect) { expect = false; PvpLobby.OpenWaitRoom(r); }
        }

        internal static void OnKicked(string reason)
        {
            UiPopup.Message(reason == "host did not start" ? "You were removed from the room: the race was not started in time." : reason == "kicked by the host" ? "The host removed you from the room." : reason ?? "");
        }

        /// <summary>The online race started (race.start): the wait room steps aside until the results are done.</summary>
        internal static void OnRaceStarting()
        {
            racing = true;
            CloseViews();
        }

        /// <summary>The online results closed: back to the room the server sent meanwhile.</summary>
        internal static void OnRaceOver()
        {
            racing = false;
            var info = pendingServerRoom;
            pendingServerRoom = null;
            if (info != null && OnlineSession.Active)
            {
                var r = FromServer(info);
                if (Social.ChannelBattle.LobbyView && r.Crew) { racing = false; return; }
                if (r.Members.Any(x => x.Me) || r.Spectating) { Show(r); return; }
            }
            // the room is gone (a legacy room closes after its race): back to the street
            if (room != null && room.Online) room = null;
            PvpLobby.Freeze(false);
        }

        // ---------------- open / close ----------------
        public static void Show(PvpRoom r)
        {
            var ui = UiManager.Instance;
            if (!ui || r == null) return;
            room = r;
            Hook(ui);
            if (!view) view = ui.Open(Dialog);
            if (!selmap) selmap = ui.Open(MapDialog);
            if (!runner) runner = new GameObject("PvP Wait Room").AddComponent<Runner>();
            PvpLobby.Freeze(true);
            PvpCharger.OfferIfLow();   // CHARGERPVP 960000: the client's 20 % fuel check on its PvP stages
            foreach (var c in view.GetComponentsInChildren<NuiControlView>(true))
            {
                var id = c.Data.IdName ?? "";
                if (id.Contains("MISSION") || id.Contains("TABKEY") || id.Contains("CARROTATE")) c.gameObject.SetActive(false);
            }
            // mission-battle rooms (flag 0x10000): 11479 "You will be performing the mission with fixed stats ..."
            view.SetVisible(P + "MISSIONBATTLE_TEXTBG", r.Mission > 0);
            view.SetVisible(P + "MISSIONBATTLE_TEXT", r.Mission > 0);
            if (r.Mission > 0) view.SetText(P + "MISSIONBATTLE_TEXT", Pvp.Text("11479"));
            foreach (var id in new[] { "OBSERVE", "JOINGAME", "MAP_SLIDER", "MAP_BONUS100", "MAP_BONUS200" }) view.SetVisible(P + id, false);
            // the room chat (and its emoticons over the slots, 1130000)
            PvpChat.Attach(view, P + "CHATBOX", P + "CHAT");
            if (!emoticons) emoticons = ui.Open(EmoticonDialog);
            if (emoticons) foreach (var c in emoticons.GetComponentsInChildren<NuiControlView>(true)) c.gameObject.SetActive(false);
            // wager rooms: 11077 "%s Rank, %d Mito Entry Fee"
            view.SetVisible(P + "BATTINGTEXT", r.Wager > 0);
            if (r.Wager > 0) view.SetText(P + "BATTINGTEXT", Pvp.Text("11077", Pvp.WagerLetters[r.Wager], Pvp.WagerFee(r.Wager)));
            // Real Match rooms: the RM car panel 1140000 replaces DETAILVIEW (0x57be30)
            view.SetVisible(P + "DETAILVIEW", !r.RealMatch);
            if (r.RealMatch && !rmCar) rmCar = ui.Open(RmCarDialog);
            if (!r.RealMatch) CloseView(ref rmCar);
            if (rmCar) { rmCar.SetVisible("ID_PVP_WAIT16_RMCAR_DUEL", true); rmCar.SetVisible("ID_PVP_WAIT16_RMCAR_F1", false); }
            aiStartAt = -1f;
            Refresh();
        }

        static void CloseView(ref NuiView v)
        {
            var ui = UiManager.Instance;
            if (v) { if (ui) ui.Close(v); else UnityEngine.Object.Destroy(v.gameObject); }
            v = null;
        }

        static void CloseViews()
        {
            if (view) PvpChat.Detach(view);
            PvpSpectate.ClosePopup();
            PvpSocial.CloseAll();
            CloseView(ref view); CloseView(ref selmap); CloseView(ref emoticons); CloseView(ref rmCar); CloseView(ref rmInfo);
            shownEmoticons.Clear();
            if (runner) UnityEngine.Object.Destroy(runner.gameObject);
            runner = null;
        }

        /// <summary>QUIT: leave the room and go back to the lobby.</summary>
        public static void Quit()
        {
            if (room == null) return;
            var me = room.Me;
            if (me != null && me.Ready && !room.IAmHost && !racing) return;   // a guest who is ready cannot quit (0x57b060)
            racing = false;
            if (room.Online)
            {
                var net = OnlineSession.Instance ? OnlineSession.Instance.Net : null;
                if (net) net.Request("race.leave", null, null);
                room = null;
                CloseViews();
                PvpLobby.Reopen();
                return;
            }
            if (room.Spectating)
            {
                room.Spectating = false;
                room.Observers = Mathf.Max(0, room.Observers - 1);
                room.ObserverNames.Remove(PlayerProfile.Current?.Name ?? "Driver");
            }
            room.Remove(me);
            PvpLobby.ReturnOfflineRoom(room);
            room = null;
            CloseViews();
            PvpLobby.Reopen();
        }

        /// <summary>Leaves everything (logout, travel).</summary>
        public static void Reset()
        {
            room = null; expect = false; racing = false; pendingServerRoom = null;
            CloseViews();
        }

        static void Hook(UiManager ui)
        {
            if (hooked == ui) return;
            hooked = ui;
            ui.On(P + "QUIT", _ => Quit());
            ui.On(P + "READY", _ => Ready());
            ui.On(P + "START", _ => Start());
            ui.On(P + "SELECT_A", _ => TeamSelect(0));
            ui.On(P + "SELECT_B", _ => TeamSelect(1));
            ui.On(P + "MAP_LEFT", _ => Browse(-1));
            ui.On(P + "MAP_RIGHT", _ => Browse(1));
            ui.On(P + "MAP_RANDOM", _ => ToggleMap(Pvp.MapRandom));
            ui.On(P + "MAP_REVERSE", _ => ToggleMap(Pvp.MapReverse));
            ui.On(P + "MAP_RECOMMEND", _ => ToggleMap(Pvp.MapRecommend));
            ui.On(P + "TRAFFIC_IDLE", _ => Traffic(Pvp.TrafficIdle));
            ui.On(P + "TRAFFIC_NORMAL", _ => Traffic(Pvp.TrafficNormal));
            ui.On(P + "TRAFFIC_JAM", _ => Traffic(Pvp.TrafficJam));
            ui.On(P + "TRAFFIC_RANDOM", _ => Traffic(Pvp.TrafficRandom));
            ui.On(P + "CITY_ALL", _ => City(-1));
            for (int i = 0; i < CityTabs.Length; i++) { int c = i; ui.On(P + CityTabs[i], _ => City(c)); }
            ui.On(P + "INVITE", _ => PvpSocial.OpenInvite());
            ui.On(P + "OBSERVE", _ => PvpSpectate.BecomeSpectator());
            ui.On(P + "JOINGAME", _ => PvpSpectate.JoinGame());
            ui.On(P + "DETAILVIEW", _ => PvpLobby.Unavailable());
            ui.On("ID_PVP_WAIT16_RMCAR_SHOWINFO", _ => ToggleRmInfo());
            ui.On("ID_PVP_RMCARINFO_CLOSE", _ => CloseView(ref rmInfo));
            for (int s = 0; s < PvpRoom.SlotCount; s++)
            {
                int slot = s;
                ui.On($"{P}BASE{s:00}", _ => OpenSlotClick(slot));
                ui.On($"{P}LOCK_BTN{Btn(s):00}", _ => SlotOp(slot, "close"));
                ui.On($"{P}AI_BTN{Btn(s):00}", _ => AddAi(slot));
                ui.On($"{P}AI_CLOSED{Btn(s):00}", _ => RemoveAi(slot));
                ui.On($"{P}CLOSED{s:00}", _ => SlotOp(slot, "open"));
                ui.On($"{P}SLOT{s:00}_CLOSE", _ => Kick(slot));
                ui.On($"{P}SLOTSINGLE{s:00}", _ => Info(slot));
                ui.On($"{P}SLOTTEAM{s:00}", _ => Info(slot));
            }
        }

        // ---------------- drawing ----------------
        public static void Refresh()
        {
            if (!view || room == null) return;
            var me = room.Me;
            bool host = room.IAmHost;
            view.SetText(P + "TITLE", room.Title);
            view.SetText(P + "NUMOFPERSON", $"({room.Players}/{room.Max})");
            view.SetVisible(P + "SINGLE", !room.Team);
            view.SetVisible(P + "TEAM", room.Team);
            view.SetVisible(P + "SECRET", room.HasPassword);
            view.SetVisible(P + "NOGATE", room.NoGate);
            view.SetVisible(P + "BEGINNERCOVER", room.Arena == 0 && room.Max <= 8);
            var p = PlayerProfile.Current;
            view.SetText(P + "USERINFO_ID", $"{p?.Name ?? me?.Name} (Lv {p?.Level ?? me?.Level ?? 1})");
            // START (host) and READY (guest) share one place (0x576c60)
            view.SetVisible(P + "START", host);
            view.SetVisible(P + "READY", !host);
            view.SetEnabled(P + "START", host && room.CanStart);
            if (view.Control(P + "READY")) view.Control(P + "READY").Selected = me != null && me.Ready;
            view.SetEnabled(P + "QUIT", host || me == null || !me.Ready);
            view.SetVisible(P + "SELECT_A", room.Team);
            view.SetVisible(P + "SELECT_B", room.Team);
            if (room.Team && me != null)
            {
                view.Control(P + "SELECT_A").Selected = me.Team == 0;
                view.Control(P + "SELECT_B").Selected = me.Team == 1;
                view.SetEnabled(P + "SELECT_A", host || !me.Ready);
                view.SetEnabled(P + "SELECT_B", host || !me.Ready);
            }
            bool ai = Pvp.AiAllowed(room) && Pvp.TrackAllowsAi(room.Track);
            for (int s = 0; s < PvpRoom.SlotCount; s++) DrawSlot(s, host, ai);
            DrawMap(host);
            PvpSpectate.DrawWaitRoom(view, room);
        }

        // ---------------- emoticons (1130000, 0x594120 / 0x5940a0) ----------------
        /// <summary>A chat command's emoticon over the sender's slot (EMOTICON_00..15) for 5 s; false when the sender has no slot.</summary>
        public static bool ShowEmoticon(string from, string icon)
        {
            if (!view || room == null || !emoticons) return false;
            var m = room.Members.FirstOrDefault(x => x.Name == from);
            if (m == null || m.Slot < 0 || m.Slot >= PvpRoom.SlotCount) return false;
            var pos = emoticons.Control($"ID_PVP_WAIT16_EMOTICON_{m.Slot:00}");
            var src = emoticons.GetComponentsInChildren<NuiControlView>(true).FirstOrDefault(c => string.Equals(c.Data.Name, icon, StringComparison.OrdinalIgnoreCase));
            if (!pos || !src) return false;
            var copy = UnityEngine.Object.Instantiate(src.gameObject, src.transform.parent);
            copy.name = $"emoticon {from}";
            var rt = (RectTransform)copy.transform;
            rt.anchoredPosition = ((RectTransform)pos.transform).anchoredPosition;
            copy.SetActive(true);
            foreach (var t in copy.GetComponentsInChildren<Transform>(true)) t.gameObject.SetActive(true);
            // one emoticon per slot: the newer replaces the older
            foreach (var (old, _) in shownEmoticons.Where(e => e.icon && e.icon.name == copy.name).ToList()) UnityEngine.Object.Destroy(old);
            shownEmoticons.RemoveAll(e => !e.icon || e.icon.name == copy.name);
            shownEmoticons.Add((copy, Time.time + EmoticonSeconds));
            return true;
        }

        /// <summary>The emoticons up now (tests).</summary>
        public static int EmoticonsShown => shownEmoticons.Count(e => e.icon);

        // ---------------- Real Match car (1140000 / 1150000) ----------------
        static void ToggleRmInfo()
        {
            if (rmInfo) { CloseView(ref rmInfo); return; }
            var ui = UiManager.Instance;
            if (!ui) return;
            rmInfo = ui.Open(RmInfoDialog);
            if (!rmInfo) return;
            // the server's set parts and options (0x58ee80) are not in the remake: the designer's placeholder lines are
            // cleared and the Real Match car's ratings go in the first option row of each kind
            foreach (var c in rmInfo.GetComponentsInChildren<NuiControlView>(true))
            {
                var id = c.Data.IdName ?? "";
                if (id.Contains("SETPARTS_NAME") || id.Contains("_OPTION_")) c.SetText("");
            }
            var (accel, speed, crash, boost) = Pvp.RealMatchStats();
            rmInfo.SetText("ID_PVP_RMCARINFO_OPTION_SPD1ST", $"Speed {speed}");
            rmInfo.SetText("ID_PVP_RMCARINFO_OPTION_ACC1ST", $"Acceleration {accel}");
            rmInfo.SetText("ID_PVP_RMCARINFO_OPTION_COL1ST", $"Durability {crash}");
            rmInfo.SetText("ID_PVP_RMCARINFO_OPTION_BST1ST", $"Booster {boost}");
        }

        /// <summary>
        /// The LOCK_BTN / AI_BTN / AI_CLOSED buttons are numbered down the columns (00..07 the left column, 08..15 the
        /// right one) while the slots are numbered across the rows (00 left, 01 right, ...): button index of a slot.
        /// </summary>
        public static int Btn(int slot) => slot % 2 == 0 ? slot / 2 : 8 + slot / 2;

        static void DrawSlot(int s, bool host, bool aiAllowed)
        {
            string n = s.ToString("00"), b = Btn(s).ToString("00");
            var m = room.At(s);
            int v = room.Slots[s];
            bool open = v == 0 && s < room.Max, closed = v < 0 || s >= room.Max;
            bool player = m != null && !m.Ai, ai = m != null && m.Ai;
            view.SetVisible($"{P}SLOTSINGLE{n}", player && !room.Team);
            view.SetVisible($"{P}SLOTTEAM{n}", player && room.Team);
            view.SetVisible($"{P}BASE{n}", open);
            view.SetVisible($"{P}LOCK_BTN{b}", open && host);
            view.SetVisible($"{P}AI_BTN{b}", open && host && aiAllowed);
            // an AI driver's slot (slot array value >= 0x10000): the A.I. plate, which the host clicks to remove it
            view.SetVisible($"{P}AI_CLOSED{b}", ai);
            view.SetEnabled($"{P}AI_CLOSED{b}", host);
            view.SetVisible($"{P}CLOSED{n}", closed);
            view.SetEnabled($"{P}CLOSED{n}", host && s < room.Max && !room.Practice);
            // labels: " Lv.%02d %s" (0x57b060); the crew line stays empty (no crews in the remake)
            Label($"{P}SLOT{n}_ID", m != null, m != null ? $" Lv.{m.Level:00} {m.Name}" : "");
            Label($"{P}SLOT{n}_PT", false, "");
            view.SetVisible($"{P}SLOT{n}_SINGLE_ID", false);
            view.SetVisible($"{P}SLOT{n}_SINGLE_PT", false);
            view.SetVisible($"{P}SLOT{n}_HOST", m != null && m.Id == room.HostId);
            view.SetVisible($"{P}SLOT{n}_START", m != null && m.Id == room.HostId);
            view.SetVisible($"{P}SLOT{n}_READY", m != null && m.Id != room.HostId && m.Ready);
            view.SetVisible($"{P}SLOT{n}_ME", m != null && m.Me);
            view.SetVisible($"{P}SLOT{n}_CLOSE", player && host && !m.Me);
            view.SetVisible($"{P}SLOT{n}_INPRATICE", false);
            view.SetVisible($"{P}SLOT{n}_LICENSEPOS", false);
            view.SetVisible($"{P}SLOT{n}_TEAMMARKPOS", false);
            if (ai)
            {
                // the plate is drawn above the slot's labels in the layout: lift the name and READY over it
                foreach (var id in new[] { $"{P}SLOT{n}_ID", $"{P}SLOT{n}_READY", $"{P}SLOT{n}_HOST", $"{P}SLOT{n}_START" })
                {
                    var c = view.Control(id);
                    if (c) c.transform.SetAsLastSibling();
                }
            }
        }

        static void Label(string id, bool on, string text)
        {
            view.SetVisible(id, on);
            if (on) view.SetText(id, text);
        }

        static string CurrentCity => room?.Track?.City ?? (PvpLobby.Session ? PvpLobby.Session.City : "cras");

        static void DrawMap(bool host)
        {
            int f = room.MapFlags;
            string city = CurrentCity;
            int cityIndex = Array.IndexOf(Cities, city);
            var all = view.Control(P + "CITY_ALL");
            // the tabs stay enabled for guests and their clicks are ignored (0x592540 checks the host on each click)
            if (all) all.Selected = (f & Pvp.MapCityAll) != 0;
            for (int i = 0; i < CityTabs.Length; i++)
            {
                var tab = view.Control(P + CityTabs[i]);
                if (!tab) continue;
                // races run in the open city: its tab is the only city that can be picked (the client's server city bits)
                tab.Selected = (f & Pvp.MapCityAll) == 0 && i == cityIndex;
            }
            Check("MAP_RANDOM", (f & Pvp.MapRandom) != 0, host && !room.Practice);
            Check("MAP_REVERSE", (f & Pvp.MapReverse) != 0, host);
            Check("MAP_RECOMMEND", (f & Pvp.MapRecommend) != 0, host && !room.Practice);
            Tab("TRAFFIC_IDLE", (f & Pvp.TrafficIdle) != 0, host);
            Tab("TRAFFIC_NORMAL", (f & Pvp.TrafficNormal) != 0, host);
            Tab("TRAFFIC_JAM", (f & Pvp.TrafficJam) != 0, host);
            Tab("TRAFFIC_RANDOM", (f & (Pvp.TrafficIdle | Pvp.TrafficNormal | Pvp.TrafficJam)) == 0, host);
            view.SetEnabled(P + "MAP_LEFT", host && (f & Pvp.MapRandom) == 0);
            view.SetEnabled(P + "MAP_RIGHT", host && (f & Pvp.MapRandom) == 0);
            bool random = (f & Pvp.MapRandom) != 0;
            var t = room.Track;
            string name = random ? Pvp.Text("10237") : room.MapName;
            view.SetText(P + "MAP_NAME", t != null && !random ? $"{name}   {t.Laps} lap(s), {t.Length / 1000f:F1} km" : name);
            if (!selmap) return;
            // SELMAP: the track's picture (control named after the pvptrackinfo name), or the random picture of the city
            string pic = random ? ((f & Pvp.MapReverse) != 0 ? $"ID_PVP_SELMAP_RANDOM_REVERSE_CITY{cityIndex + 1}" : $"ID_PVP_SELMAP_RANDOM_CITY{cityIndex + 1}") : null;
            foreach (var c in selmap.GetComponentsInChildren<NuiControlView>(true))
            {
                string id = c.Data.IdName ?? "", nm = c.Data.Name ?? "";
                if (id.StartsWith("ID_PVP_SELMAP_TRACK_") || id.StartsWith("ID_PVP_SELMAP_AIMARK")) continue;
                bool isPicture = id.Length == 0 || id.StartsWith("ID_PVP_SELMAP_RANDOM") || id.StartsWith("ID_PVP_SELMAP_RECOMMEND") || id.StartsWith("ID_PVP_SELMAP_1ST_TRACK");
                if (!isPicture) continue;
                bool on = pic != null ? id == pic : t != null && string.Equals(nm, t.Name, StringComparison.OrdinalIgnoreCase);
                c.gameObject.SetActive(on);
            }
            selmap.SetVisible("ID_PVP_SELMAP_TRACK_COVER", true);
            selmap.SetText("ID_PVP_SELMAP_TRACK_NAME", name);
            bool aiOk = t != null && Pvp.TrackAllowsAi(t);
            selmap.SetVisible("ID_PVP_SELMAP_AIMARK", aiOk && !random);
            selmap.SetVisible("ID_PVP_SELMAP_AIMARK_NO", !aiOk && !random);
            selmap.SetVisible("ID_PVP_SELMAP_AIMARK_NO_TEXT", !aiOk && !random);
            selmap.SetVisible("ID_PVP_WAIT16_MAP_NEWMARK", t != null && t.New && !random);
            selmap.SetVisible("ID_PVP_WAIT16_MAP_BONUS100", false);
            selmap.SetVisible("ID_PVP_WAIT16_MAP_BONUS200", false);
        }

        static void Check(string id, bool on, bool enabled)
        {
            var c = view.Control(P + id);
            if (!c) return;
            c.SetChecked(on); c.Interactable = enabled;
        }

        static void Tab(string id, bool on, bool enabled)
        {
            var c = view.Control(P + id);
            if (!c) return;
            c.Selected = on;   // guests: enabled, clicks ignored (Traffic checks the host)
        }

        // ---------------- host map settings (0x592540) ----------------
        static List<RaceTrack> Maps() => Pvp.MapList(CurrentCity, room.MapFlags, room.Arena);

        static void Browse(int dir)
        {
            if (room == null || !room.IAmHost) return;
            var maps = Maps();
            if (maps.Count == 0) { UiPopup.Message(Pvp.Text("10204")); return; }
            int i = room.Track != null ? maps.IndexOf(room.Track) : -1;
            i = i < 0 ? 0 : ((i + dir) % maps.Count + maps.Count) % maps.Count;
            SetTrack(maps[i]);
        }

        static void ToggleMap(int bit)
        {
            if (room == null || !room.IAmHost) return;
            int f = room.MapFlags ^ bit;
            // RECOMMEND clears random / reverse and selects all cities; RANDOM / REVERSE clear RECOMMEND
            if (bit == Pvp.MapRecommend && (f & Pvp.MapRecommend) != 0) f = (f & ~(Pvp.MapRandom | Pvp.MapReverse)) | Pvp.MapCityAll;
            if (bit != Pvp.MapRecommend && (f & bit) != 0) f &= ~Pvp.MapRecommend;
            room.MapFlags = f;
            // reverse / recommend switch the list: keep the paired track ("_r" added or removed) when it is in the new list
            var maps = Maps();
            RaceTrack next = room.Track;
            if (bit == Pvp.MapReverse && room.Track != null)
            {
                string want = room.Track.Reverse ? room.Track.Name.Replace("_r", "") : room.Track.Name + "_r";
                next = maps.FirstOrDefault(x => string.Equals(x.Name, want, StringComparison.OrdinalIgnoreCase));
            }
            if (next == null || !maps.Contains(next)) next = maps.FirstOrDefault() ?? room.Track;
            if (GameEffects.Sounds) AudioLibrary.PlaySfx("sfx/SID_PVP_ROBBY_MAP");
            SetTrack(next);
        }

        static void Traffic(int bit)
        {
            if (room == null || !room.IAmHost) return;
            room.MapFlags = (room.MapFlags & ~(Pvp.TrafficIdle | Pvp.TrafficNormal | Pvp.TrafficJam)) | bit;
            SendSettings(false);
            Refresh();
        }

        static void City(int index)
        {
            if (room == null || !room.IAmHost) return;
            int cityIndex = Array.IndexOf(Cities, CurrentCity);
            if (index >= 0 && index != cityIndex) { UiPopup.Message(Pvp.Text("10204")); return; }
            int f = room.MapFlags & ~(Pvp.MapCityAll | 0x7c0);
            room.MapFlags = index < 0 ? f | Pvp.MapCityAll : f | (Pvp.MapCity1 << index);
            SendSettings(false);
            Refresh();
        }

        static void SetTrack(RaceTrack t)
        {
            if (t == null) return;
            room.Track = t;
            if (room.Online) { room.TrackKey = Pvp.TrackKey(t); room.TrackName = t.Name; room.Laps = t.Laps; }
            // a track without AI support removes the AI drivers (11344, 0x7234c0)
            if (!Pvp.TrackAllowsAi(t) && room.Members.Any(m => m.Ai))
            {
                foreach (var m in room.Members.Where(x => x.Ai).ToList()) room.Remove(m);
                UiPopup.Message(Pvp.Text("11344"));
            }
            SendSettings(true);
            Refresh();
        }

        static void SendSettings(bool track)
        {
            if (room == null || !room.Online) return;
            var body = new Dictionary<string, object> { ["mapFlags"] = room.MapFlags };
            var t = room.Track;
            if (track && t != null) body["track"] = new Dictionary<string, object> { ["key"] = Pvp.TrackKey(t), ["name"] = t.Name, ["laps"] = t.Laps, ["length"] = (double)t.Length };
            OnlineSession.Instance.Net.Request("race.settings", body, r => { if (!r.B("ok")) UiPopup.Message(r.S("error")); });
        }

        // ---------------- slots (0x57d9f0) ----------------
        static void OpenSlotClick(int slot)
        {
            if (room == null) return;
            if (room.IAmHost) { SlotOp(slot, "close"); return; }
            // a guest moves to the open slot
            if (room.Online) { Request("race.slot", new Dictionary<string, object> { ["slot"] = slot, ["op"] = "move" }); return; }
            var me = room.Me;
            if (me != null && !me.Ready) { room.Move(me, slot); Refresh(); }
        }

        static void SlotOp(int slot, string op)
        {
            if (room == null || !room.IAmHost) return;
            if (room.Online) { Request("race.slot", new Dictionary<string, object> { ["slot"] = slot, ["op"] = op }); return; }
            if (op == "close" && room.Slots[slot] == 0) room.Slots[slot] = -1;
            else if (op == "open" && room.Slots[slot] == -1 && slot < room.Max) room.Slots[slot] = 0;
            Refresh();
        }

        static void AddAi(int slot)
        {
            if (room == null || !room.IAmHost || !Pvp.AiAllowed(room) || room.Slots[slot] != 0) return;
            // online the server adds and drives the AI driver (race.js: slot array value >= 0x10000)
            if (room.Online) { Request("race.slot", new Dictionary<string, object> { ["slot"] = slot, ["op"] = "ai" }); return; }
            room.Add(Pvp.NewAi(room, PlayerProfile.Current?.Level ?? 1), slot);
            Refresh();
        }

        static void RemoveAi(int slot)
        {
            if (room == null || !room.IAmHost) return;
            var m = room.At(slot);
            if (m == null || !m.Ai) return;
            if (room.Online) { Request("race.slot", new Dictionary<string, object> { ["slot"] = slot, ["op"] = "noai" }); return; }
            room.Remove(m); Refresh();
        }

        static void Kick(int slot)
        {
            if (room == null || !room.IAmHost) return;
            var m = room.At(slot);
            if (m == null || m.Me) return;
            MissionUi.Confirm(Pvp.Text("10240", m.Name), () =>
            {
                if (room.Online) Request("race.kick", new Dictionary<string, object> { ["id"] = m.Id });
                else { room.Remove(m); Refresh(); }
            });
        }

        static void Info(int slot)
        {
            var m = room?.At(slot);
            if (m != null && !m.Me) UiPopup.Message($"{m.Name} (Lv {m.Level})");
        }

        static void TeamSelect(int team)
        {
            if (room == null || !room.Team) return;
            var me = room.Me;
            if (me == null || me.Team == team) return;
            if (me.Ready && !room.IAmHost) return;
            if (GameEffects.Sounds) AudioLibrary.PlaySfx("sfx/SID_PVP_ROBBY_TEAM");
            if (room.Online) { Request("race.team", new Dictionary<string, object> { ["team"] = team }); return; }
            int s = room.OpenSlot(team);
            if (s >= 0) { room.Move(me, s); Refresh(); }
        }

        static void Request(string type, Dictionary<string, object> body) =>
            OnlineSession.Instance.Net.Request(type, body, r => { if (!r.B("ok")) UiPopup.Message(r.S("error")); });

        // ---------------- ready / start ----------------
        /// <summary>The wager and Real Match checks of START / READY (0x579560 / 0x579740); null when fine.</summary>
        static string StartBlock(bool start)
        {
            var p = PlayerProfile.Current;
            if (room.Wager > 0 && start && room.Members.Count(m => !m.Ai || !room.Online) < 2) return Pvp.Text("11107");
            if (room.Wager > 0 && p != null && p.Mito < Pvp.WagerNeed(room.Wager)) return Pvp.Text("11067", Pvp.WagerNeed(room.Wager));
            if (room.RealMatch && !Pvp.RealMatchOpen) return Pvp.Text("10850");
            return null;
        }

        public static void Ready()
        {
            if (room == null || room.IAmHost || Time.time < readyDebounce) return;
            readyDebounce = Time.time + 1f;
            var me = room.Me;
            if (me == null) return;
            if (!me.Ready) { var block = StartBlock(false); if (block != null) { UiPopup.Message(block); return; } }
            if (room.Online) { Request("race.ready", new Dictionary<string, object> { ["ready"] = !me.Ready }); return; }
            me.Ready = !me.Ready;
            aiStartAt = room.CanStart && room.Host != null && room.Host.Ai ? Time.time + 3f : -1f;
            Refresh();
        }

        /// <summary>START (0x579560): 1 s debounce; online the server checks the room (everyone ready, two drivers or more).</summary>
        public static void Start()
        {
            if (room == null || !room.IAmHost || Time.time < readyDebounce) return;
            readyDebounce = Time.time + 1f;
            var block = StartBlock(true);
            if (block != null) { UiPopup.Message(block); return; }
            if (room.Online)
            {
                OnlineSession.Instance.Net.Request("race.startRace", null, r =>
                {
                    if (r.B("ok")) return;
                    int code = r.I("code");
                    UiPopup.Message(code == 10850 ? Pvp.Text("10850") : code == 11107 ? Pvp.Text("11107") : code == 11067 ? Pvp.Text("11067", r.I("need")) : r.S("error"));
                });
                return;
            }
            if (!room.CanStart) return;
            BeginOffline();
        }

        static void Tick()
        {
            // emoticons last 5 s
            for (int i = shownEmoticons.Count - 1; i >= 0; i--)
                if (!shownEmoticons[i].icon || Time.time >= shownEmoticons[i].until) { if (shownEmoticons[i].icon) UnityEngine.Object.Destroy(shownEmoticons[i].icon); shownEmoticons.RemoveAt(i); }
            if (room == null || racing) return;
            HostKickWarning();
            if (room.Online) return;
            // an AI host starts a few seconds after the player got ready (the client's host-kick warning runs 11..15 s); with
            // only a spectator watching it starts by itself
            if (aiStartAt < 0f && room.Spectating && room.Host != null && room.Host.Ai && room.CanStart) aiStartAt = Time.time + 6f;
            if (aiStartAt > 0f && Time.time >= aiStartAt)
            {
                aiStartAt = -1f;
                if (room.CanStart && room.Host != null && room.Host.Ai) BeginOffline();
            }
        }

        /// <summary>
        /// 0x57a6c0: while the host can start, a 1 s counter runs; at 11..15 the system line 10241 counts down to the server's
        /// kick (16 s after everyone is ready; not in rooms with flag 0x200).
        /// </summary>
        static void HostKickWarning()
        {
            if (!room.Online || !room.IAmHost || !room.CanStart || !room.Waiting) { kickCount = -1f; kickWarned = 0; return; }
            if (kickCount < 0f) { kickCount = Time.time; kickWarned = 10; }
            int count = Mathf.FloorToInt(Time.time - kickCount);
            if (count > kickWarned && count >= 11 && count <= 15)
            {
                kickWarned = count;
                PvpChat.System(Pvp.Text("10241", 16 - count));
            }
        }

        /// <summary>Offline: a room listed as racing is watched from a fresh start of its race.</summary>
        internal static void WatchRunningOffline(PvpRoom r)
        {
            if (room != r || r.Online) return;
            r.State = "lobby";
            BeginOffline();
        }

        static void BeginOffline()
        {
            var s = PvpLobby.Session;
            if (!s || !s.Car) return;
            // RANDOM: the track is drawn at the start from the browsable list
            if ((room.MapFlags & Pvp.MapRandom) != 0)
            {
                var maps = Maps();
                if (maps.Count > 0) room.Track = maps[UnityEngine.Random.Range(0, maps.Count)];
            }
            if (room.Track == null) { UiPopup.Message(Pvp.Text("10204")); return; }
            var r = room;
            racing = true;
            CloseViews();
            PvpLobby.Freeze(false);
            var race = RaceMission.StartRoom(r, s, _ =>
            {
                racing = false;
                foreach (var m in r.Members) if (!m.Ai && m.Id != r.HostId) m.Ready = false;
                if (room == r) Show(r);
            });
            if (!race) { racing = false; Show(r); }
        }
    }
}
