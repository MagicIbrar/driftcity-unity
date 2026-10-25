using System;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Net;
using DriftCity.Social;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// Inviting and finding drivers from the Battle Zone (DriftCity_RE/PVP.md "Unity port"):
    /// - WAIT16 INVITE (0x576d10, 5 s cooldown, not for spectators or in practice rooms) opens FRIENDINVITE 1840000 with the
    ///   drivers the server offers (0x1c2 -> 0x1c3: car grade and name); INVITE asks 10468 (10467 without a row) and sends
    ///   the invite (0x1d1). The invited driver sees 10026 "You have been invited by %s to join a Battle Zone match. Do you
    ///   want to join?" and joins without the password (0x1d4).
    /// - Lobby FINDFRIENDS opens 1080000 (tabs 10220 "Find a Friend" / 10221 "Search Crew Members", a name list with the
    ///   status, the name box): FIND looks the driver up (0x296) and shows USERINFO 1090000 (0x297): name, crew, level, EXP,
    ///   Mito, records, ladder, location; ADDFRIEND asks 10203; the join button follows the driver into their room
    ///   (10202 "You cannot do this with a friend." when they are not in one).
    /// Offline the friends come from the social store: an invited friend joins the room as an AI driver.
    /// </summary>
    public static class PvpSocial
    {
        public const int InviteDialog = 1840000, FindDialog = 1080000, InfoDialog = 1090000;
        public const float InviteCooldown = 5f;

        static NuiView invite, find, info;
        static NuiListBox inviteList, findList;
        static readonly List<(string name, int level, int grade)> candidates = new List<(string, int, int)>();
        static readonly List<(string name, string status)> findRows = new List<(string, string)>();
        static int inviteSel = -1;
        static float lastInvite = -99f;
        static bool crewTab;
        static Dictionary<string, object> shownInfo;
        static UiManager hooked;

        public static NuiView InviteView => invite;
        public static NuiView FindView => find;
        public static NuiView InfoView => info;
        public static IReadOnlyList<(string name, int level, int grade)> Candidates => candidates;
        public static bool InviteReady => Time.time - lastInvite >= InviteCooldown;

        static void Hook(UiManager ui)
        {
            if (hooked == ui) return;
            hooked = ui;
            ui.On("ID_FRIENDINVITE_CLOSE", _ => SocialUi.Close(ref invite));
            ui.On("ID_FRIENDINVITE_INVITE", _ => InviteSelected());
            ui.On("ID_PVP_FINDFRIEND_CLOSE", _ => SocialUi.Close(ref find));
            ui.On("ID_PVP_FINDFRIEND_CANCEL", _ => SocialUi.Close(ref find));
            ui.On("ID_PVP_FINDFRIEND_TAB_FRIEND", _ => FindTab(false));
            ui.On("ID_PVP_FINDFRIEND_TAB_RACINGTEAM", _ => FindTab(true));
            ui.On("ID_PVP_FINDFRIEND_COMBOBOX", _ => { if (findList != null) ToggleFindList(); });
            ui.On("ID_PVP_FINDFRIEND_FIND", _ => Find(find ? find.GetInput("ID_PVP_FINDFRIEND_EDITBOX").Trim() : ""));
            ui.On("ID_PVP_USERINFO_CLOSE", _ => SocialUi.Close(ref info));
            ui.On("ID_PVP_USERINFO_ADDFRIEND", _ =>
            {
                string name = shownInfo?.S("name");
                if (string.IsNullOrEmpty(name)) return;
                MissionUi.Confirm(Pvp.Text("10203"), () => Friends.Add(name));
            });
        }

        // ---------------- invite (WAIT16 INVITE -> 1840000) ----------------
        public static void OpenInvite()
        {
            var room = PvpWaitRoom.Room;
            var ui = UiManager.Instance;
            if (room == null || !ui) return;
            if (room.Spectating || room.Practice) return;
            if (!InviteReady) return;
            Hook(ui);
            SocialUi.Close(ref invite);
            invite = ui.Open(InviteDialog);
            if (!invite) return;
            SocialUi.CenterOnScreen(invite);
            inviteList = new NuiListBox(invite.Control("ID_FRIENDINVITE_LISTBOX"), 10, 27);
            inviteList.Selected += i => inviteSel = i;
            inviteSel = -1;
            candidates.Clear();
            Fill();
            if (OnlineSession.Active)
            {
                OnlineSession.Instance.Net.Request("race.invitees", null, r =>
                {
                    if (!invite) return;
                    candidates.Clear();
                    foreach (var o in (r.L("players") ?? new List<object>()).OfType<Dictionary<string, object>>())
                        candidates.Add((o.S("name"), Mathf.Max(1, o.I("level")), Mathf.Max(1, o.I("grade"))));
                    Fill();
                });
                return;
            }
            // offline: the online friends of the social store who are not in this room
            Friends.Load(() =>
            {
                if (!invite) return;
                candidates.Clear();
                foreach (var f in Friends.List.Where(f => f.Online && room.Members.All(m => m.Name != f.Name)))
                    candidates.Add((f.Name, Mathf.Max(1, f.Level), SocialStore.AiInfo(f.Name).grade));
                Fill();
            });
        }

        static void Fill()
        {
            if (!invite || inviteList == null) return;
            inviteList.SetRows(candidates.Select(c => ($"{c.name}  (Lv {c.level})", c.grade.ToString())).ToList(), inviteSel);
        }

        /// <summary>INVITE in 1840000: 10467 without a row, else 10468 and the invite.</summary>
        public static void InviteSelected()
        {
            if (inviteSel < 0 || inviteSel >= candidates.Count) { UiPopup.Message(Pvp.Text("10467")); return; }
            var c = candidates[inviteSel];
            MissionUi.Confirm(Pvp.Text("10468"), () => Invite(c.name));
        }

        public static void Invite(string name)
        {
            var room = PvpWaitRoom.Room;
            if (room == null) return;
            lastInvite = Time.time;
            SocialUi.Close(ref invite);
            if (OnlineSession.Active)
            {
                OnlineSession.Instance.Net.Request("race.invite", new Dictionary<string, object> { ["name"] = name }, r =>
                {
                    if (!r.B("ok")) UiPopup.Message(r.I("code") == 10467 ? Pvp.Text("10467") : r.S("error"));
                });
                return;
            }
            // offline: the friend comes over and races as an AI driver
            var host = PvpLobby.Session ? (MonoBehaviour)PvpLobby.Session : null;
            if (host) host.StartCoroutine(OfflineJoin(room, name));
        }

        static System.Collections.IEnumerator OfflineJoin(PvpRoom room, string name)
        {
            yield return new WaitForSeconds(2f);
            if (PvpWaitRoom.Room != room || !room.Waiting || room.Members.Any(m => m.Name == name) || room.OpenSlot() < 0) yield break;
            var ai = Pvp.NewAi(room, SocialStore.AiInfo(name).level);
            ai.Name = name; ai.Level = SocialStore.AiInfo(name).level;
            room.Add(ai);
            PvpChat.System($"{name} joined the room.");
            PvpWaitRoom.Refresh();
        }

        /// <summary>race.invite from the server: 10026, then the join (a wager room needs 10x its fee, 11067).</summary>
        internal static void OnInvited(Dictionary<string, object> m)
        {
            if (PvpWaitRoom.Room != null || OnlineRace.Active || RaceMission.Active) return;
            string from = m.S("from");
            int id = m.I("id"), wager = m.I("wager");
            MissionUi.Confirm(Pvp.Text("10026", from), () =>
            {
                var p = PlayerProfile.Current;
                if (wager > 0 && p != null && p.Mito < Pvp.WagerNeed(wager)) { UiPopup.Message(Pvp.Text("11067", Pvp.WagerNeed(wager))); return; }
                var s = PvpLobby.Session;
                if (s && !PvpLobby.IsOpen && !PvpWaitRoom.IsOpen) PvpLobby.Open(s);
                OnlineRace.Listen();
                PvpWaitRoom.ExpectRoom();
                OnlineSession.Instance.Net.Request("race.join", new Dictionary<string, object> { ["id"] = id }, r =>
                {
                    if (!r.B("ok")) { PvpWaitRoom.CancelExpect(); UiPopup.Message(PvpLobby.ServerError(r.S("error"))); }
                });
            });
        }

        // ---------------- find a friend (1080000) and the driver's card (1090000) ----------------
        public static void OpenFind()
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            Hook(ui);
            SocialUi.Close(ref find);
            find = ui.Open(FindDialog);
            if (!find) return;
            SocialUi.HideStartHidden(find);
            var box = find.Control("ID_PVP_FINDFRIEND_COMBOBOX");
            findList = box ? new NuiListBox(box, 6, 22) : null;
            if (findList != null)
            {
                findList.Selected += i =>
                {
                    if (i < 0 || i >= findRows.Count || !find) return;
                    var edit = find.Control("ID_PVP_FINDFRIEND_EDITBOX");
                    if (edit && edit.Input) edit.Input.text = findRows[i].name;
                };
                findList.Activated += _ => ToggleFindList();
            }
            var e = find.Control("ID_PVP_FINDFRIEND_EDITBOX");
            if (e && e.Input) e.Input.characterLimit = 16;
            FindTab(false);
        }

        static bool listOpen;
        static void ToggleFindList()
        {
            listOpen = !listOpen;
            var box = find ? find.Control("ID_PVP_FINDFRIEND_COMBOBOX") : null;
            if (!box) return;
            foreach (Transform row in box.transform) if (row.name.StartsWith("row")) row.gameObject.SetActive(listOpen && row.GetSiblingIndex() < findRows.Count + 8);
            if (listOpen) findList.SetRows(findRows.Select(r => (r.name, r.status)).ToList(), -1);
        }

        static void FindTab(bool crew)
        {
            if (!find) return;
            crewTab = crew;
            var a = find.Control("ID_PVP_FINDFRIEND_TAB_FRIEND"); if (a) a.Selected = !crew;
            var b = find.Control("ID_PVP_FINDFRIEND_TAB_RACINGTEAM"); if (b) b.Selected = crew;
            find.SetVisible("ID_PVP_FINDFRIEND_TABKEY_1ST", !crew);
            find.SetVisible("ID_PVP_FINDFRIEND_TABKEY_2ND", crew);
            findRows.Clear();
            void Show()
            {
                if (!find) return;
                listOpen = true;
                ToggleFindList();   // closed until clicked
            }
            if (crew)
            {
                Teams.Load(() =>
                {
                    if (Teams.Mine != null)
                        foreach (var m in Teams.Mine.Members.Where(x => x.Name != PlayerProfile.Current?.Name))
                            findRows.Add((m.Name, m.Online ? m.Location : Pvp.Text("10504")));
                    Show();
                });
                return;
            }
            Friends.Load(() =>
            {
                foreach (var f in Friends.List) findRows.Add((f.Name, f.Online ? f.Location : Pvp.Text("10504")));
                Show();
            });
        }

        /// <summary>FIND: an offline driver gives 10219 "Offline"; else their card.</summary>
        public static void Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            var row = findRows.FirstOrDefault(r => string.Equals(r.name, name, StringComparison.OrdinalIgnoreCase));
            if (OnlineSession.Active)
            {
                OnlineSession.Instance.Net.Request("race.find", new Dictionary<string, object> { ["name"] = name }, r =>
                {
                    if (!r.B("ok")) { UiPopup.Message(r.I("code") == 10219 ? Pvp.Text("10219") : r.S("error")); return; }
                    ShowInfo(r);
                });
                return;
            }
            if (row.name == null || row.status == Pvp.Text("10504")) { UiPopup.Message(Pvp.Text("10219")); return; }
            var ai = SocialStore.AiInfo(name);
            // an offline friend may be sitting in one of the lobby's AI rooms
            var room = PvpLobby.Rooms.FirstOrDefault(x => x.Members.Any(m => m.Name == name));
            ShowInfo(new Dictionary<string, object>
            {
                ["name"] = row.name, ["level"] = (double)ai.level, ["exp"] = 0.0, ["races"] = (double)(ai.level * 7 % 90 + 10), ["wins"] = (double)(ai.level * 3 % 30),
                ["location"] = row.status, ["room"] = room != null ? new Dictionary<string, object> { ["id"] = (double)room.Id, ["title"] = room.Title } : null,
            });
        }

        public static void ShowInfo(Dictionary<string, object> r)
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            Hook(ui);
            shownInfo = r;
            SocialUi.Close(ref info);
            info = ui.Open(InfoDialog);
            if (!info) return;
            SocialUi.CenterOnScreen(info);
            SocialUi.HideStartHidden(info);
            info.SetText("ID_PVP_USERINFO_NAME", r.S("name"));
            info.SetText("ID_PVP_USERINFO_RACINGTEAM", r.S("crew", "-"));
            info.SetText("ID_PVP_USERINFO_LEVEL", $"Lv {r.I("level")}");
            info.SetText("ID_PVP_USERINFO_EXP", r.N("exp").ToString("N0"));
            info.SetText("ID_PVP_USERINFO_MITO", r.ContainsKey("mito") ? r.N("mito").ToString("N0") : "-");
            info.SetText("ID_PVP_USERINFO_BATTLERECORD_1ST", $"{r.I("races")} Races");
            info.SetText("ID_PVP_USERINFO_BATTLERECORD_2ND", $"{r.I("wins")} Wins");
            info.SetText("ID_PVP_USERINFO_QUICKSERVICE_REC", r.N("bestLap") > 0 ? Pvp.Time((float)r.N("bestLap")).Trim() : "-");
            info.SetText("ID_PVP_USERINFO_TOTALKM", "-");
            info.SetText("ID_PVP_USERINFO_LADDERRECORD", r.ContainsKey("ladder") && r["ladder"] != null ? r.I("ladder").ToString() : "-");
            var room = r.O("room");
            info.SetText("ID_PVP_USERINFO_LOCATION", room != null ? $"{r.S("location")}: {room.S("title")}" : r.S("location"));
            // the unnamed join button (1090025) raises no named click: it gets its own handler
            var join = Pvp.ById(info, 1090025);
            if (join)
            {
                join.gameObject.SetActive(true);
                var trigger = join.gameObject.GetComponent<UnityEngine.EventSystems.EventTrigger>() ?? join.gameObject.AddComponent<UnityEngine.EventSystems.EventTrigger>();
                var entry = new UnityEngine.EventSystems.EventTrigger.Entry { eventID = UnityEngine.EventSystems.EventTriggerType.PointerClick };
                entry.callback.AddListener(_ => { UiSounds.Click(); JoinShown(); });
                trigger.triggers.Add(entry);
            }
        }

        /// <summary>The card's join button: into the driver's room (0x1d7 / password 0x?), else 10202.</summary>
        public static void JoinShown()
        {
            var room = shownInfo?.O("room");
            if (room == null) { UiPopup.Message(Pvp.Text("10202")); return; }
            int id = room.I("id");
            var target = PvpLobby.Rooms.FirstOrDefault(x => x.Id == id);
            SocialUi.Close(ref info); SocialUi.Close(ref find);
            if (target != null) { PvpLobby.Join(target); return; }
            if (OnlineSession.Active)
            {
                OnlineRace.Listen();
                PvpWaitRoom.ExpectRoom();
                OnlineSession.Instance.Net.Request("race.join", new Dictionary<string, object> { ["id"] = id }, r =>
                {
                    if (!r.B("ok")) { PvpWaitRoom.CancelExpect(); UiPopup.Message(PvpLobby.ServerError(r.S("error"))); }
                });
            }
            else UiPopup.Message(Pvp.Text("10202"));
        }

        public static void CloseAll() { SocialUi.Close(ref invite); SocialUi.Close(ref find); SocialUi.Close(ref info); }
    }
}
