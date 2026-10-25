using System;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// The Battle Zone lobby (PVP.md sections 2-4): RMLOBBY 1000000 with the arena tabs, ten room rows per page, quick
    /// join, CREATEROOM 1020000 and the password prompt 1650000. Offline the rooms are AI drivers' rooms made up for the
    /// player's city (plus the ones the player creates); online they are the channel's server rooms (race.list).
    /// Entry point for the T key and the station / parking-lot "Battle Zone": <see cref="Open"/>.
    /// </summary>
    public static class PvpLobby
    {
        const int Dialog = 1000000, CreateDialog = 1020000, PasswordDialog = 1650000;
        static readonly string[] RowKeys = { "A", "B", "C", "D", "E", "F", "G", "H", "I", "J" };

        static NuiView lobby, create, password;
        static CitySession session;
        static int arena = -1, page;
        static List<PvpRoom> rooms = new List<PvpRoom>();
        static readonly Dictionary<int, List<PvpRoom>> offlineRooms = new Dictionary<int, List<PvpRoom>>();
        static string offlineCity;
        static int nextOfflineId = 1;
        static UiManager hooked;
        // create-room dialog state
        static bool crTeam, crPractice, crObserve = true, crNoGate;
        static int crMax = 16, crWager;

        public static bool IsOpen => lobby || create || password;
        /// <summary>True while any Battle Zone screen is up (lobby, wait room, results) - the lead's GameFlow.MenuOpen hook.</summary>
        public static bool AnyOpen => IsOpen || PvpWaitRoom.IsOpen || Tournament.IsOpen || Ladder.IsOpen || PvpSocial.FindView || PvpSocial.InfoView || PvpRewardPick.IsOpen;
        public static NuiView View => lobby;
        public static NuiView CreateView => create;
        public static int Arena => arena;
        public static IReadOnlyList<PvpRoom> Rooms => rooms;
        public static CitySession Session => session ? session : CitySession.Current;

        // ---------------- open / close ----------------
        public static void Open(CitySession s)
        {
            if (s == null || !s.Car) return;
            if (RaceMission.Active || OnlineRace.Active || ChaseMission.Active || PvpResults.IsOpen) return;
            session = s;
            if (PvpWaitRoom.IsOpen) return;
            var ui = UiManager.Instance;
            if (!ui) return;
            if (lobby) { Refresh(); return; }
            Hook();
            lobby = ui.Open(Dialog);
            if (!lobby) return;
            Freeze(true);
            if (arena < 0 || !Pvp.ArenaOpen(arena, Pvp.CarGrade(), PlayerProfile.Current?.Level ?? 1)) arena = Pvp.FirstOpenArena();
            page = 0;
            foreach (var id in new[] { "ID_PVP_RMLOBBY_MISSION_BACK", "ID_PVP_RMLOBBY_MISSION_BTN", "ID_PVP_RMLOBBY_MISSION_TXT", "ID_PVP_RMLOBBY_F1_EVENT", "ID_PVP_RMLOBBY_TOUR_BG" })
                lobby.SetVisible(id, false);
            lobby.SetEnabled("ID_PVP_RMLOBBY_LAPTIMEBATTLE", Pvp.LapTimeBattle && LapTimeBattle.TrackFor(s.City) != null);
            lobby.SetVisible("ID_PVP_RMLOBBY_OBSERVE_TOOLTIP", false);
            // lobby chat (scope "room": the drivers looking at the same arena)
            PvpChat.Attach(lobby, "ID_PVP_RMLOBBY_CHAT_LIST", "ID_PVP_RMLOBBY_CHAT_INPUT");
            if (GameEffects.Sounds) AudioLibrary.PlaySfx("sfx/SID_PVP_ROBBY_IN");
            Load();
            ApprenticeCourse.BattleZone(false);   // an apprentice hears Director Jason (row 1)
        }

        public static void Close()
        {
            var ui = UiManager.Instance;
            if (lobby) PvpChat.Detach(lobby);
            if (lobby && OnlineSession.Active && !PvpWaitRoom.IsOpen) OnlineSession.Instance.Net.Request("race.lobby", new Dictionary<string, object> { ["arena"] = -1 }, null);
            PvpSocial.CloseAll();
            foreach (var v in new[] { lobby, create, password }) if (v) { if (ui) ui.Close(v); else UnityEngine.Object.Destroy(v.gameObject); }
            bool was = lobby;
            lobby = create = password = null;
            if (was && GameEffects.Sounds) AudioLibrary.PlaySfx("sfx/SID_PVP_ROBBY_OUT");
            if (!PvpWaitRoom.IsOpen) Freeze(false);
        }

        /// <summary>Leaving the city or logging out (GameFlow.ClearCity): every Battle Zone screen closes, an offline room is left.</summary>
        public static void CloseAll()
        {
            PvpResults.Close();
            PvpRewardPick.Close(false);
            PvpSpectate.EndViewing();
            if (LapTimeBattle.Active) LapTimeBattle.Reset();
            PvpWaitRoom.Reset();
            PvpChat.Reset();
            if (Tournament.IsOpen) Tournament.Close(false);
            if (Ladder.IsOpen) Ladder.Close();
            Close();
        }

        /// <summary>The Battle Zone is its own stage in the client: the car waits on the street while its screens are up.</summary>
        internal static void Freeze(bool on)
        {
            var car = Session ? Session.Car : null;
            if (!car || RaceMission.Active || OnlineRace.Active) return;
            car.ExternalControl = on;
            if (on) { var rb = car.GetComponent<Rigidbody>(); if (rb) rb.linearVelocity = Vector3.zero; }
        }

        static void Hook()
        {
            var ui = UiManager.Instance;
            if (!ui || hooked == ui) return;
            ui.On("ID_PVP_RMLOBBY_TUTORIAL", _ => ApprenticeCourse.TutorialButton());   // 0x57ea5e: 11340, then the course
            hooked = ui;
            ui.On("ID_PVP_RMLOBBY_CLOSE", _ => Close());
            for (int i = 0; i < Pvp.ArenaTabs.Length; i++)
            {
                int tab = i;
                ui.On(Pvp.ArenaTabs[i], _ => SelectArena(tab));
            }
            ui.On("ID_PVP_RMLOBBY_PREV", _ => { if (page > 0) { page--; Refresh(); } });
            ui.On("ID_PVP_RMLOBBY_NEXT", _ => { if ((page + 1) * 10 < rooms.Count) { page = (page + 1) % 200; Refresh(); } });
            ui.On("ID_PVP_RMLOBBY_CREATEROOM", _ => OpenCreate(false));
            ui.On("ID_PVP_RMLOBBY_FILTER_SINGLE_CHK", _ => QuickJoin(false));
            ui.On("ID_PVP_RMLOBBY_FILTER_TEAM_CHK", _ => QuickJoin(true));
            ui.On("ID_PVP_RMLOBBY_CS_ENTER", _ => Tournament.Open(Session));   // online: the server's bracket (src/pvptour.js)
            ui.On("ID_PVP_RMLOBBY_CS_INFO", _ => UiPopup.Message(Pvp.Text("Championship_infotext2") + "\n" + Pvp.Text("Championship_infotext3")));
            ui.On("ID_PVP_RMLOBBY_LAPTIMEBATTLE", _ => { var s = Session; if (s) LapTimeBattle.Enter(s); });
            ui.On("ID_PVP_RMLOBBY_FINDFRIENDS", _ => PvpSocial.OpenFind());
            foreach (var id in new[] { "ID_PVP_RMLOBBY_CHARGE" })
                ui.On(id, _ => Unavailable());
            for (int r = 0; r < RowKeys.Length; r++)
            {
                int row = r;
                foreach (var prefix in new[] { "ID_PVP_RMLOBBY_TITLE_", "ID_PVP_RMLOBBY_SINGLEMODE_", "ID_PVP_RMLOBBY_TEAMMODE_" })
                    ui.On(prefix + RowKeys[r], _ => JoinRow(row));
                ui.On("ID_PVP_RMLOBBY_OBSERVE_" + RowKeys[r], _ => ObserveRow(row));
                ui.On($"ID_PVP_RMLOBBY_OBSERVE_GROUP{r:00}", _ => ObserveRow(row));
            }
            // CREATEROOM
            ui.On("ID_PVP_CREATEROOM_SELECTSINGLE", _ => { crTeam = false; crPractice = false; RefreshCreate(); });
            ui.On("ID_PVP_CREATEROOM_SELECTTEAM", _ => { crTeam = true; crPractice = false; RefreshCreate(); });
            ui.On("ID_PVP_CREATEROOM_SELECTPRACTICE", _ => { crTeam = false; crPractice = true; crNoGate = false; RefreshCreate(); });
            ui.On("ID_PVP_CREATEROOM_CHK8", _ => { crMax = 8; RefreshCreate(); });
            ui.On("ID_PVP_CREATEROOM_CHK16", _ => { if (arena != 0) crMax = 16; RefreshCreate(); });
            ui.On("ID_PVP_CREATEROOM_ALLOWOBSERVE", _ => { crObserve = !crObserve; RefreshCreate(); });
            ui.On("ID_PVP_CREATEROOM_NOGATE", _ => { if (!crPractice) crNoGate = !crNoGate; RefreshCreate(); });
            // wager grades A..D = 2..5 (net feature 0x20000 BattingJackpot); the selected one again clears it
            for (int g = 2; g <= 5; g++) { int grade = g; ui.On("ID_PVP_CREATEROOM_GRADE_" + Pvp.WagerLetters[g], _ => ToggleWager(grade)); }
            ui.On("ID_PVP_CREATEROOM_OK", _ => CreateOk());
            ui.On("ID_PVP_CREATEROOM_CANCEL", _ => CloseView(ref create));
            ui.On("ID_PVP_PASSWORD_CANCEL", _ => CloseView(ref password));
        }

        /// <summary>10745 "This feature is currently unavailable."</summary>
        internal static void Unavailable() => UiPopup.Message(Pvp.Text("10745"));

        static void CloseView(ref NuiView v)
        {
            if (v) { if (UiManager.Instance) UiManager.Instance.Close(v); else UnityEngine.Object.Destroy(v.gameObject); }
            v = null;
        }

        // ---------------- arenas and the room list ----------------
        public static void SelectArena(int tab)
        {
            if (tab < 0 || tab >= Pvp.ArenaTabs.Length) return;
            if (!Pvp.ArenaOpen(tab, Pvp.CarGrade(), PlayerProfile.Current?.Level ?? 1)) return;
            arena = tab; page = 0;
            Load();
        }

        /// <summary>Fetches the arena's rooms (the client's list request 0x518a50(tab, page)).</summary>
        static void Load()
        {
            if (OnlineSession.Active)
            {
                OnlineRace.Listen();
                OnlineSession.Instance.Net.Request("race.list", new Dictionary<string, object> { ["arena"] = arena }, r =>
                {
                    if (!lobby) return;
                    if (!r.B("ok")) { UiPopup.Message(r.S("error", "Could not get the race list.")); return; }
                    SetServerRooms(r.L("rooms"));
                });
                rooms = new List<PvpRoom>();
                Refresh();
                return;
            }
            rooms = OfflineRooms(arena);
            Refresh();
        }

        /// <summary>race.rooms broadcast / race.list reply: the channel's rooms in the selected arena.</summary>
        internal static void SetServerRooms(List<object> list)
        {
            rooms = (list ?? new List<object>()).OfType<Dictionary<string, object>>()
                .Select(PvpWaitRoom.FromServer).Where(r => r.Arena == arena).ToList();
            if (page * 10 >= rooms.Count) page = Mathf.Max(0, (rooms.Count - 1) / 10);
            Refresh();
        }

        /// <summary>The AI drivers' rooms of an arena: made once per city and arena, the player's own rooms added on top.</summary>
        static List<PvpRoom> OfflineRooms(int tab)
        {
            var city = Session ? Session.City : "cras";
            if (offlineCity != city) { offlineRooms.Clear(); offlineCity = city; }
            if (!offlineRooms.TryGetValue(tab, out var list))
            {
                list = new List<PvpRoom>();
                var maps = Pvp.MapList(city, 0, tab);
                int level = PlayerProfile.Current?.Level ?? 1;
                int count = maps.Count == 0 ? 0 : UnityEngine.Random.Range(4, 8);
                for (int i = 0; i < count; i++)
                {
                    var room = new PvpRoom
                    {
                        Id = nextOfflineId++, Title = Pvp.RandomTitle(), Team = UnityEngine.Random.value < 0.3f, Arena = tab,
                        Max = tab == 0 || UnityEngine.Random.value < 0.5f ? 8 : 16, NoGate = UnityEngine.Random.value < 0.2f,
                        Track = maps[UnityEngine.Random.Range(0, maps.Count)],
                        // now and then a wager room (grades A..D)
                        Wager = Pvp.WagerEnabled && tab != Pvp.ArenaRealMatch && UnityEngine.Random.value < 0.2f ? UnityEngine.Random.Range(2, 6) : 0,
                    };
                    room.ResetSlots();
                    var host = room.Add(Pvp.NewAi(room, level));
                    room.HostId = host.Id;
                    int others = UnityEngine.Random.Range(1, Mathf.Min(6, room.Max - 1));
                    for (int k = 0; k < others; k++) room.Add(Pvp.NewAi(room, level));
                    if (i == count - 1 && count > 4) room.State = "racing";   // one room is mid-race
                    list.Add(room);
                }
                offlineRooms[tab] = list;
            }
            return list.Where(r => r.Members.Count > 0 && !r.Members.Any(m => m.Me)).ToList();
        }

        /// <summary>Puts a room the player made (and left) back on the offline list while AI drivers remain in it.</summary>
        internal static void ReturnOfflineRoom(PvpRoom room)
        {
            if (room == null || room.Online || room.Practice) return;
            if (!offlineRooms.TryGetValue(room.Arena, out var list)) return;
            if (room.Members.Count == 0) list.Remove(room);
            else if (!list.Contains(room)) list.Add(room);
        }

        public static void Refresh()
        {
            if (!lobby) return;
            int grade = Pvp.CarGrade(), level = PlayerProfile.Current?.Level ?? 1;
            for (int i = 0; i < Pvp.ArenaTabs.Length; i++)
            {
                var tab = lobby.Control(Pvp.ArenaTabs[i]);
                if (!tab) continue;
                tab.Interactable = Pvp.ArenaOpen(i, grade, level);
                tab.Selected = i == arena;
            }
            for (int r = 0; r < RowKeys.Length; r++)
            {
                int index = page * 10 + r;
                var room = index < rooms.Count ? rooms[index] : null;
                string k = RowKeys[r];
                bool used = room != null, waiting = used && room.Waiting;
                lobby.SetVisible("ID_PVP_RMLOBBY_SINGLEMODE_" + k, used && !room.Team);
                lobby.SetVisible("ID_PVP_RMLOBBY_TEAMMODE_" + k, used && room.Team);
                lobby.SetEnabled("ID_PVP_RMLOBBY_SINGLEMODE_" + k, waiting);
                lobby.SetEnabled("ID_PVP_RMLOBBY_TEAMMODE_" + k, waiting);
                Label("ID_PVP_RMLOBBY_TITLE_" + k, used, used ? Pvp.Text("10242", room.Title) : "");
                Label("ID_PVP_RMLOBBY_MAP_" + k, used, used ? Pvp.Text("10243", room.MapName) : "");
                Label("ID_PVP_RMLOBBY_PLAYERCNT_" + k, used, used ? $"({room.Players,2}/{room.Max,2})" : "");
                lobby.SetVisible("ID_PVP_RMLOBBY_SECRETENABLE_" + k, used && room.HasPassword && waiting && !room.Team);
                lobby.SetVisible($"ID_PVP_RMLOBBY_SECRETENABLE_GROUP{r:00}", used && room.HasPassword && waiting && room.Team);
                lobby.SetVisible("ID_PVP_RMLOBBY_SECRETDISABLE_" + k, used && room.HasPassword && !waiting);
                lobby.SetVisible("ID_PVP_RMLOBBY_NOGATE_" + k, used && room.NoGate);
                lobby.SetVisible("ID_PVP_RMLOBBY_OBSERVE_" + k, used && room.Players > 0 && room.Spectate);
                lobby.SetVisible($"ID_PVP_RMLOBBY_OBSERVE_GROUP{r:00}", used && room.Players > 0 && room.Spectate && room.Team);
                // the wager grade icon of the row (GRADE_S..D, room flags 0x7000)
                for (int g = 1; g <= 5; g++) lobby.SetVisible($"ID_PVP_RMLOBBY_GRADE_{Pvp.WagerLetters[g]}{r + 1:00}", used && room.Wager == g);
            }
            lobby.SetEnabled("ID_PVP_RMLOBBY_PREV", page > 0);
            lobby.SetEnabled("ID_PVP_RMLOBBY_NEXT", (page + 1) * 10 < rooms.Count);
        }

        static void Label(string id, bool on, string text)
        {
            lobby.SetVisible(id, on);
            if (on) lobby.SetText(id, text);
        }

        // ---------------- joining ----------------
        static void JoinRow(int row)
        {
            int index = page * 10 + row;
            if (index >= rooms.Count) return;
            // mission-battle rows (flag 0x10000) can't be joined from the list (an invite brings drivers in)
            if (rooms[index].Mission > 0) { UiPopup.Message(Pvp.Text("10030")); return; }
            Join(rooms[index]);
        }

        public static void Join(PvpRoom room)
        {
            if (room == null) return;
            if (!room.Waiting) { UiPopup.Message(Pvp.Text("10030")); return; }   // "You cannot enter the room anymore."
            if (room.Players >= room.Max || room.OpenSlot() < 0) { UiPopup.Message(Pvp.Text("10030")); return; }
            // 11067 "Not enough Mito. In order to participate, you must have %d Mito."
            if (room.Wager > 0 && (PlayerProfile.Current?.Mito ?? 0) < Pvp.WagerNeed(room.Wager)) { UiPopup.Message(Pvp.Text("11067", Pvp.WagerNeed(room.Wager))); return; }
            var why = PvpMissionBattle.Refusal(room);   // 11480: not in a party
            if (why != null) { UiPopup.Message(why); return; }
            if (room.HasPassword) { AskPassword(room); return; }
            DoJoin(room, "");
        }

        /// <summary>OBSERVE_r (0x57a250): spectate the row's room.</summary>
        static void ObserveRow(int row)
        {
            int index = page * 10 + row;
            if (index < rooms.Count) PvpSpectate.Observe(rooms[index]);
        }

        /// <summary>PASSWORD 1650000: to join the room, or (observe) to spectate it.</summary>
        internal static void AskPassword(PvpRoom room, bool observe = false)
        {
            var ui = UiManager.Instance;
            CloseView(ref password);
            password = ui.Open(PasswordDialog);
            if (!password) return;
            password.SetVisible("ID_PVP_PASSWORD_INVALID", false);
            var edit = password.Control("ID_PVP_PASSWORD_EDIT");
            if (edit && edit.Input) { edit.Input.characterLimit = 8; edit.Input.ActivateInputField(); }
            ui.On("ID_PVP_PASSWORD_OK", _ =>
            {
                if (!password) return;
                string pw = password.GetInput("ID_PVP_PASSWORD_EDIT");
                if (pw.Length < 1 || pw.Length > 8) { password.SetVisible("ID_PVP_PASSWORD_INPUT", false); password.SetVisible("ID_PVP_PASSWORD_INVALID", true); password.SetText("ID_PVP_PASSWORD_INVALID", Pvp.Text("10503")); return; }
                if (!room.Online && pw != room.Password)
                {
                    password.SetVisible("ID_PVP_PASSWORD_INPUT", false); password.SetVisible("ID_PVP_PASSWORD_INVALID", true);
                    password.SetText("ID_PVP_PASSWORD_INVALID", Pvp.Text("10503"));
                    return;
                }
                CloseView(ref password);
                if (observe) PvpSpectate.DoObserve(room, pw); else DoJoin(room, pw);
            });
        }

        static void DoJoin(PvpRoom room, string pw)
        {
            if (room.Online)
            {
                OnlineRace.Listen();
                PvpWaitRoom.ExpectRoom();
                OnlineSession.Instance.Net.Request("race.join", new Dictionary<string, object> { ["id"] = room.Id, ["password"] = pw }, r =>
                {
                    if (!r.B("ok")) { PvpWaitRoom.CancelExpect(); UiPopup.Message(ServerError(r.S("error"))); }
                });
                return;
            }
            var p = PlayerProfile.Current;
            var me = room.Add(new PvpMember { Id = 1, Name = p?.Name ?? "Driver", Level = p?.Level ?? 1, Me = true });
            if (me == null) { UiPopup.Message(Pvp.Text("10030")); return; }
            if (GameEffects.Sounds) AudioLibrary.PlaySfx("sfx/SID_PVP_ROOMENTER");
            OpenWaitRoom(room);
        }

        internal static string ServerError(string e) =>
            e == "race already started" || e == "race is full" ? Pvp.Text("10030") : e == "race not found" ? Pvp.Text("10031") : e == "wrong password" ? Pvp.Text("10503") : e;

        internal static void OpenWaitRoom(PvpRoom room)
        {
            var ui = UiManager.Instance;
            CloseView(ref create); CloseView(ref password);
            if (lobby) { ui.Close(lobby); lobby = null; }
            PvpWaitRoom.Show(room);
        }

        /// <summary>FILTER_SINGLE / FILTER_TEAM (0x574e90): join the first open room of the mode, else create one.</summary>
        static void QuickJoin(bool team)
        {
            if (OnlineSession.Active)
            {
                OnlineRace.Listen();
                PvpWaitRoom.ExpectRoom();
                OnlineSession.Instance.Net.Request("race.quick", new Dictionary<string, object> { ["team"] = team, ["arena"] = arena }, r =>
                {
                    if (r.B("ok")) return;
                    PvpWaitRoom.CancelExpect();
                    OpenCreate(team);
                });
                return;
            }
            var room = rooms.FirstOrDefault(x => x.Team == team && x.Waiting && !x.HasPassword && x.OpenSlot() >= 0);
            if (room != null) DoJoin(room, ""); else OpenCreate(team);
        }

        // ---------------- CREATEROOM (0x576620 / 0x57c810 / 0x57a3a0) ----------------
        public static void OpenCreate(bool team)
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            Hook();
            CloseView(ref create);
            create = ui.Open(CreateDialog);
            if (!create) return;
            crTeam = team; crPractice = false; crObserve = true; crNoGate = false; crMax = arena == 0 ? 8 : 16; crWager = 0;
            var title = create.Control("ID_PVP_CREATEROOM_TITLE");
            if (title && title.Input) { title.Input.characterLimit = 24; title.Input.text = Pvp.RandomTitle(); }
            var pw = create.Control("ID_PVP_CREATEROOM_PASSWORD");
            if (pw && pw.Input)
            {
                pw.Input.characterLimit = 8;
                pw.Input.onValueChanged.AddListener(_ => RefreshCreate());
            }
            RefreshCreate();
        }

        /// <summary>GRADE_A..D (0x57c810): needs the server's wager feature; the selected grade again clears it.</summary>
        static void ToggleWager(int grade)
        {
            if (!Pvp.WagerEnabled) { Unavailable(); return; }
            if (crPractice) return;
            crWager = crWager == grade ? 0 : grade;
            RefreshCreate();
        }

        static void RefreshCreate()
        {
            if (!create) return;
            create.Control("ID_PVP_CREATEROOM_SELECTSINGLE").Selected = !crTeam && !crPractice;
            create.Control("ID_PVP_CREATEROOM_SELECTTEAM").Selected = crTeam;
            create.Control("ID_PVP_CREATEROOM_SELECTPRACTICE").Selected = crPractice;
            create.Control("ID_PVP_CREATEROOM_CHK8").SetChecked(crPractice || crMax == 8);
            create.Control("ID_PVP_CREATEROOM_CHK16").SetChecked(!crPractice && crMax == 16);
            create.SetEnabled("ID_PVP_CREATEROOM_CHK16", !crPractice && arena != 0);
            create.SetEnabled("ID_PVP_CREATEROOM_CHK8", !crPractice);
            create.SetVisible("ID_PVP_CREATEROOM_TEXT8", true);
            create.SetVisible("ID_PVP_CREATEROOM_TEXT16", true);
            create.SetText("ID_PVP_CREATEROOM_TEXT8", Pvp.Text("10233"));
            create.SetText("ID_PVP_CREATEROOM_TEXT16", Pvp.Text("10234"));
            // ALLOWOBSERVE (0x57ca12): without a password a room can always be spectated (checked, disabled); typing a
            // password lets the host choose
            bool hasPw = create.GetInput("ID_PVP_CREATEROOM_PASSWORD").Length > 0;
            create.Control("ID_PVP_CREATEROOM_ALLOWOBSERVE").SetChecked(!hasPw || crObserve);
            create.SetEnabled("ID_PVP_CREATEROOM_ALLOWOBSERVE", hasPw);
            create.Control("ID_PVP_CREATEROOM_NOGATE").SetChecked(crNoGate);
            create.SetEnabled("ID_PVP_CREATEROOM_NOGATE", !crPractice);
            // the wager grade: the arrow under the chosen button and its line of text (NONE without a wager)
            for (int g = 2; g <= 5; g++)
            {
                string l = Pvp.WagerLetters[g];
                create.SetVisible("ID_PVP_CREATEROOM_GRADEARROW_" + l, crWager == g);
                create.SetVisible("ID_PVP_CREATEROOM_GRADETEXT_" + l, crWager == g);
                var b = create.Control("ID_PVP_CREATEROOM_GRADE_" + l);
                if (b) b.Interactable = !crPractice;
            }
            // NONE carries a copy of grade A's line in the shipped data: without a wager the line stays empty
            create.SetVisible("ID_PVP_CREATEROOM_GRADETEXT_NONE", false);
        }

        /// <summary>The create dialog's current settings as a room (title, mode, players, password, spectate, no gate).</summary>
        static PvpRoom CreateSettings()
        {
            string title = create.GetInput("ID_PVP_CREATEROOM_TITLE").Trim();
            if (title.Length == 0) title = Pvp.RandomTitle();
            if (title.Length > 24) title = title.Substring(0, 24);
            { var bad = NameFilter.CheckRoomTitle(title); if (bad != null) { UiPopup.Message(NameFilter.Text(bad)); return null; } }   // 10608 (0x57a3ee)
            string pw = create.GetInput("ID_PVP_CREATEROOM_PASSWORD");
            var room = new PvpRoom
            {
                Title = title, Team = crTeam && !crPractice, Practice = crPractice, Max = crPractice ? 8 : crMax, Password = pw,
                Spectate = pw.Length == 0 || crObserve, NoGate = crNoGate && !crPractice, Arena = arena, Wager = crPractice ? 0 : crWager,
                // the GM's MissionBattle switch (0x5b8200) makes mission-battle rooms (flag 0x10000)
                Mission = crPractice || crWager > 0 ? 0 : PvpMissionBattle.Mode,
            };
            return room;
        }

        static void CreateOk()
        {
            if (!create) return;
            string pw = create.GetInput("ID_PVP_CREATEROOM_PASSWORD");
            if (pw.Length > 8) { UiPopup.Message(Pvp.Text("10503")); return; }
            // 11066 "If you want to select this wager level, you must have %d Mito."
            if (crWager > 0 && !crPractice && (PlayerProfile.Current?.Mito ?? 0) < Pvp.WagerNeed(crWager)) { UiPopup.Message(Pvp.Text("11066", Pvp.WagerNeed(crWager))); return; }
            var room = CreateSettings();
            Create(room);
        }

        /// <summary>Creates a room from settings: online through race.host (practice rooms stay local), offline with AI drivers.</summary>
        public static void Create(PvpRoom room)
        {
            var s = Session;
            var maps = Pvp.MapList(s ? s.City : "cras", room.MapFlags, room.Arena);
            if (maps.Count == 0) { UiPopup.Message(Pvp.Text("10204")); return; }   // "No maps available."
            var mb = PvpMissionBattle.Refusal(room);   // 11480 "You cannot perform Mission Battles while in a Party."
            if (mb != null) { UiPopup.Message(mb); return; }
            room.Track = maps[0];
            if (GameEffects.Sounds) AudioLibrary.PlaySfx("sfx/SID_PVP_ROOMCREATE");
            if (OnlineSession.Active && !room.Practice)
            {
                OnlineRace.Listen();
                PvpWaitRoom.ExpectRoom();
                var t = room.Track;
                var body = new Dictionary<string, object>
                {
                    ["track"] = new Dictionary<string, object> { ["key"] = Pvp.TrackKey(t), ["name"] = t.Name, ["laps"] = t.Laps, ["length"] = (double)t.Length },
                    ["room"] = new Dictionary<string, object>
                    {
                        ["title"] = room.Title, ["team"] = room.Team, ["max"] = room.Max, ["password"] = room.Password, ["spectate"] = room.Spectate,
                        ["noGate"] = room.NoGate, ["arena"] = room.Arena, ["mapFlags"] = room.MapFlags, ["crew"] = room.Crew, ["wager"] = room.Wager,
                        ["mission"] = room.Mission,
                    },
                };
                OnlineSession.Instance.Net.Request("race.host", body, r =>
                {
                    if (!r.B("ok")) { PvpWaitRoom.CancelExpect(); UiPopup.Message(ServerError(r.S("error"))); }
                });
                return;
            }
            room.Id = nextOfflineId++;
            room.ResetSlots();
            var p = PlayerProfile.Current;
            var me = room.Add(new PvpMember { Id = 1, Name = p?.Name ?? "Driver", Level = p?.Level ?? 1, Me = true, Ready = true });
            room.HostId = me.Id;
            // offline rooms start with three AI drivers (the host adds or removes more with AI_BTN / AI_CLOSED); wager and Real
            // Match rooms have no AI slots, so AI drivers sit in them as rivals (the offline stand-in for other players)
            if (Pvp.AiAllowed(room) || room.Wager > 0 || room.RealMatch) for (int i = 0; i < 3; i++) room.Add(Pvp.NewAi(room, p?.Level ?? 1));
            if (!room.Practice)
            {
                if (!offlineRooms.TryGetValue(room.Arena, out var list)) offlineRooms[room.Arena] = list = new List<PvpRoom>();
                list.Add(room);
            }
            OpenWaitRoom(room);
        }

        /// <summary>Back from the wait room (QUIT): the lobby again on the same arena.</summary>
        internal static void Reopen()
        {
            var s = Session;
            if (s) Open(s); else Freeze(false);
        }
    }
}
