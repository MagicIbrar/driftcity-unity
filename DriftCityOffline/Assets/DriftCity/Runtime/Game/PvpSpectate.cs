using System;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// Spectating a Battle Zone room (DriftCity_RE/PVP.md "Unity port"):
    /// - Lobby OBSERVE_r (0x57a250, observe request 0x1de): a room without a password, or a password room that allows
    ///   spectators (flag 0x80), is watched at once; otherwise PASSWORD 1650000 asks first.
    /// - In the wait room a spectator sees the observer popup 1990000 (10870 "You can only spectate in this room." when
    ///   the room is full, 10858 in practice rooms), JOINGAME (0x1dc: become a player while the room waits) instead of
    ///   READY / START, and OBSERVE_CNT 10244 "%d Spectators" for everyone. A guest who is not ready can OBSERVE (0x1db).
    /// - During the race VIEWING 1960000 (0x75e520 per frame): room name, "%d/%d", single / team, the drivers by progress
    ///   (team colours 0xff3399ff / 0xffff3300), "%d" + st/nd/rd/th, PLAYER_SEL beside the watched driver, team points,
    ///   six views (NORMAL auto director, TPVIEW, TOPVIEW, QUARTER, SIDE, FPVIEW; keys 1..6, Left / Right cycle them), Up /
    ///   Down the previous / next driver, a click on a row watches that driver, ESC leaves the room.
    /// Offline the AI drivers' rooms can be watched the same way.
    /// </summary>
    public static class PvpSpectate
    {
        public const int ViewDialog = 1960000, ObserverDialog = 1990000;
        static readonly string[] ViewButtons = { "ID_PVP_VIEWING_NORMALVIEW", "ID_PVP_VIEWING_TPVIEW", "ID_PVP_VIEWING_TOPVIEW", "ID_PVP_VIEWING_QUARTERVIEW", "ID_PVP_VIEWING_SIDEVIEW", "ID_PVP_VIEWING_FPVIEW" };

        public struct Driver { public string Name; public Transform Car; public float Progress; public int Team; public bool Done; public int Place; public float Fraction; }

        static NuiView viewing, popup;
        static Viewer viewer;
        static UiManager hooked;

        public static NuiView ViewingView => viewing;
        public static NuiView PopupView => popup;
        /// <summary>True while a race is being watched (the lead's GameFlow.MissionActive hook).</summary>
        public static bool Watching => viewer;
        /// <summary>The watched race's order (Pick'em reads it).</summary>
        public static List<Driver> Order => viewer ? viewer.Order : new List<Driver>();
        public static int Selected => viewer ? viewer.Index : -1;
        public static string WatchedName => viewer ? viewer.WatchedName : null;
        /// <summary>The watched race's track (the Ghost Battle drives it).</summary>
        public static RaceTrack WatchedTrack => viewer ? viewer.Track : null;

        /// <summary>Back to the spectator views on the watched driver (after a Ghost Battle).</summary>
        public static void ResumeWatching()
        {
            if (!viewer) return;
            var rig = PvpCameraRig.Ensure();
            var d = viewer.Order;
            rig.BeginWatch(d.Count > viewer.Index ? d[viewer.Index].Car : null, viewer.Track);
            viewer.SetView(0);
        }

        // ---------------- lobby: OBSERVE ----------------
        public static void Observe(PvpRoom room)
        {
            if (room == null) return;
            if (room.Players == 0) return;
            if (room.HasPassword && !room.Spectate) { PvpLobby.AskPassword(room, true); return; }
            DoObserve(room, "");
        }

        internal static void DoObserve(PvpRoom room, string password)
        {
            if (room.Online)
            {
                OnlineRace.Listen();
                PvpWaitRoom.ExpectRoom();
                OnlineSession.Instance.Net.Request("race.observe", new Dictionary<string, object> { ["id"] = room.Id, ["password"] = password }, r =>
                {
                    if (!r.B("ok")) { PvpWaitRoom.CancelExpect(); UiPopup.Message(PvpLobby.ServerError(r.S("error"))); }
                });
                return;
            }
            if (!room.Online && room.HasPassword && !room.Spectate && password != room.Password) { UiPopup.Message(Pvp.Text("10503")); return; }
            room.Spectating = true;
            room.Observers++;
            room.ObserverNames.Add(PlayerProfile.Current?.Name ?? "Driver");
            if (GameEffects.Sounds) AudioLibrary.PlaySfx("sfx/SID_PVP_ROOMENTER");
            PvpLobby.OpenWaitRoom(room);
            if (!room.Waiting) PvpWaitRoom.WatchRunningOffline(room);
        }

        // ---------------- wait room ----------------
        /// <summary>The spectator parts of WAIT16 (0x57b060) and the observer popup 1990000.</summary>
        public static void DrawWaitRoom(NuiView view, PvpRoom room)
        {
            const string P = "ID_PVP_WAIT16_";
            view.SetVisible(P + "OBSERVE_CNT", true);
            view.SetText(P + "OBSERVE_CNT", Pvp.Text("10244", room.Observers));
            var me = room.Me;
            bool guest = me != null && !room.IAmHost;
            view.SetVisible(P + "OBSERVE", !room.Spectating && guest && !me.Ready && room.Spectate && room.Waiting);
            bool full = room.Players >= room.Max || room.OpenSlot() < 0;
            view.SetVisible(P + "JOINGAME", room.Spectating && !full && !room.Practice);
            view.SetEnabled(P + "JOINGAME", room.Spectating && room.Waiting && !room.Practice);
            view.SetEnabled(P + "INVITE", !room.Spectating && !room.Practice && PvpSocial.InviteReady);
            if (room.Spectating)
            {
                view.SetVisible(P + "START", false);
                view.SetVisible(P + "READY", false);
                view.SetEnabled(P + "QUIT", true);
            }
            var ui = UiManager.Instance;
            if (room.Spectating && ui)
            {
                if (!popup) popup = ui.Open(ObserverDialog);
                if (popup)
                {
                    string text = room.Practice ? Pvp.Text("10858") : full ? Pvp.Text("10870") : Pvp.Text("10235");
                    if (string.IsNullOrWhiteSpace(text) || text == "10235") text = "Waiting to spectate the battle.";
                    popup.SetText("ID_PVP_WAIT_OBSERVE_POPUP_MSG", text);
                    // the popup's dark base would cover the wait room: only the message window shows
                    popup.SetVisible("ID_PVP_WAIT_OBSERVE_BASE", false);
                }
            }
            else ClosePopup();
        }

        public static void ClosePopup()
        {
            var ui = UiManager.Instance;
            if (popup) { if (ui) ui.Close(popup); else UnityEngine.Object.Destroy(popup.gameObject); }
            popup = null;
        }

        /// <summary>JOINGAME (0x1dc): the spectator takes an open slot while the room waits (wager rooms need 10x the fee).</summary>
        public static void JoinGame()
        {
            var room = PvpWaitRoom.Room;
            if (room == null || !room.Spectating || !room.Waiting) return;
            var p = PlayerProfile.Current;
            if (room.Wager > 0 && p != null && p.Mito < Pvp.WagerNeed(room.Wager)) { UiPopup.Message(Pvp.Text("11067", Pvp.WagerNeed(room.Wager))); return; }
            if (room.Online) { Request("race.join", new Dictionary<string, object> { ["id"] = room.Id }); return; }
            if (room.OpenSlot() < 0) { UiPopup.Message(Pvp.Text("10030")); return; }
            room.Spectating = false;
            room.Observers = Mathf.Max(0, room.Observers - 1);
            room.ObserverNames.Remove(p?.Name ?? "Driver");
            room.Add(new PvpMember { Id = 1, Name = p?.Name ?? "Driver", Level = p?.Level ?? 1, Me = true });
            ClosePopup();
            PvpWaitRoom.Refresh();
        }

        /// <summary>OBSERVE (0x1db): a guest who is not ready becomes a spectator.</summary>
        public static void BecomeSpectator()
        {
            var room = PvpWaitRoom.Room;
            var me = room?.Me;
            if (room == null || me == null || room.IAmHost || me.Ready || !room.Waiting) return;
            if (room.Online) { Request("race.observe", new Dictionary<string, object>()); return; }
            room.Remove(me);
            room.Spectating = true;
            room.Observers++;
            room.ObserverNames.Add(me.Name);
            PvpWaitRoom.Refresh();
        }

        static void Request(string type, Dictionary<string, object> body) =>
            OnlineSession.Instance.Net.Request(type, body, r => { if (!r.B("ok")) UiPopup.Message(PvpLobby.ServerError(r.S("error"))); });

        // ---------------- the race: VIEWING 1960000 ----------------
        /// <summary>Opens VIEWING for a race watched as a spectator; drivers() gives the field in race order every frame.</summary>
        public static void BeginViewing(PvpRoom room, RaceTrack track, Func<List<Driver>> drivers, Func<(int a, int b)> teamPoints, Action leave, Func<bool> running = null)
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            EndViewing();
            Hook(ui);
            viewing = ui.Open(ViewDialog);
            if (!viewing) return;
            foreach (var c in viewing.GetComponentsInChildren<NuiControlView>(true))
            {
                var id = c.Data.IdName ?? "";
                if (id.StartsWith("ID_PVP_VIEWING_PLAYER_") || id.StartsWith("ID_PVP_VIEWING_TABKEY_") || id == "ID_PVP_OB_GT") c.gameObject.SetActive(false);
            }
            viewing.SetVisible("ID_PVP_OB_BATTLE", true);
            viewing.SetText("ID_PVP_VIEWING_ROOMNAME", room?.Title ?? "");
            viewing.SetText("ID_PVP_VIEWING_ROOMMEMBER", room != null ? $"{room.Players}/{room.Max}" : "");
            viewing.SetVisible("ID_PVP_VIEWING_ROOMTYPE_SINGLE", room == null || !room.Team);
            viewing.SetVisible("ID_PVP_VIEWING_ROOMTYPE_TEAM", room != null && room.Team);
            viewing.SetText("ID_PVP_VIEWING_VIEWERCOUNT", room != null ? room.Observers.ToString() : "");
            foreach (var id in new[] { "ID_PVP_VIEWING_TP_BG", "ID_PVP_VIEWING_TP_ATEAMPOINT", "ID_PVP_VIEWING_TP_BTEAMPOINT", "ID_PVP_VIEWING_TP_AMARK", "ID_PVP_VIEWING_TP_BMARK" })
                viewing.SetVisible(id, room != null && room.Team);
            // COVER is the "connecting" picture over the view while the race loads: the 3D view shows once it runs
            viewing.SetVisible("ID_PVP_VIEWING_COVER", false);
            // a spectator has no car of their own: the speedometer and the button bar step aside (VIEWING is the HUD)
            foreach (var id in new[] { 320000, 3010000 })
            {
                var v = ui.Find(id);
                if (v && v.gameObject.activeSelf) { v.gameObject.SetActive(false); hiddenHud.Add(v.gameObject); }
            }
            PvpChat.Attach(viewing, "ID_PVP_VIEWING_CHAT_LIST", "ID_PVP_VIEWING_CHAT_INPUT");
            viewer = new GameObject("PvP Viewer").AddComponent<Viewer>();
            viewer.Init(room, track, drivers, teamPoints, leave);
            viewer.Running = running;
        }

        static void Hook(UiManager ui)
        {
            if (hooked == ui) return;
            hooked = ui;
            for (int i = 0; i < ViewButtons.Length; i++) { int v = i; ui.On(ViewButtons[i], _ => { if (viewer) viewer.SetView(v); }); }
            ui.On("ID_PVP_VIEWING_ESC", _ => { if (viewer) viewer.Leave(); });
            for (int r = 1; r <= 16; r++) { int row = r - 1; ui.On("ID_PVP_VIEWING_PLAYER_RANK" + r, _ => { if (viewer) viewer.Watch(row); }); }
        }

        public static void Cycle(int dir) { if (viewer) viewer.Watch(viewer.Index + dir); }

        static readonly List<GameObject> hiddenHud = new List<GameObject>();

        public static void EndViewing()
        {
            PvpGhost.Exit(null, true);
            foreach (var g in hiddenHud) if (g) g.SetActive(true);
            hiddenHud.Clear();
            if (viewer) UnityEngine.Object.Destroy(viewer.gameObject);
            viewer = null;
            var ui = UiManager.Instance;
            if (viewing) { PvpChat.Detach(viewing); if (ui) ui.Close(viewing); else UnityEngine.Object.Destroy(viewing.gameObject); }
            viewing = null;
            var rig = PvpCameraRig.Instance;
            if (rig && rig.Current == PvpCameraRig.Mode.Watch) rig.Release();
        }

        sealed class Viewer : MonoBehaviour
        {
            PvpRoom room;
            RaceTrack track;
            Func<List<Driver>> drivers;
            Func<(int a, int b)> points;
            Action leave;
            List<Driver> order = new List<Driver>();
            public List<Driver> Order => order;
            public int Index;
            public string WatchedName;
            public RaceTrack Track => track;
            public Func<bool> Running;
            int view;

            public void Init(PvpRoom r, RaceTrack t, Func<List<Driver>> d, Func<(int, int)> p, Action l)
            {
                room = r; track = t; drivers = d; points = p; leave = l;
                var rig = PvpCameraRig.Ensure();
                order = drivers?.Invoke() ?? new List<Driver>();
                Index = 0;
                WatchedName = order.Count > 0 ? order[0].Name : null;
                rig.BeginWatch(order.Count > 0 ? order[0].Car : null, track);
                SetView(0);
            }

            public void SetView(int v)
            {
                view = Mathf.Clamp(v, 0, ViewButtons.Length - 1);
                var rig = PvpCameraRig.Instance;
                if (rig) rig.SetView(view);
                if (!viewing) return;
                for (int i = 0; i < ViewButtons.Length; i++) { var c = viewing.Control(ViewButtons[i]); if (c) c.Selected = i == view; }
            }

            /// <summary>Up / Down stop at the ends of the list (0x75fc10).</summary>
            public void Watch(int i)
            {
                if (order.Count == 0) return;
                Index = Mathf.Clamp(i, 0, order.Count - 1);
                WatchedName = order[Index].Name;
                var rig = PvpCameraRig.Instance;
                if (rig) { if (rig.Current != PvpCameraRig.Mode.Watch && rig.Current != PvpCameraRig.Mode.GoalIn) rig.BeginWatch(order[Index].Car, track); rig.WatchTarget = order[Index].Car; }
            }

            public void Leave() { var l = leave; EndViewing(); l?.Invoke(); }

            void Update()
            {
                order = drivers?.Invoke() ?? order;
                // keep watching the same driver as the order changes
                int i = order.FindIndex(d => d.Name == WatchedName);
                if (i < 0 && order.Count > 0) i = 0;
                Index = Mathf.Max(0, i);
                var rig = PvpCameraRig.Instance;
                if (rig && order.Count > Index && rig.WatchTarget != order[Index].Car) rig.WatchTarget = order[Index].Car;
                Keys();
                Draw();
                // the Ghost Battle button beside the watched row (0x75e520)
                var row = viewing ? viewing.Control($"ID_PVP_VIEWING_PLAYER_LISTBASE{Index + 1:00}") : null;
                PvpGhost.Draw(viewing, order.Count > Index ? order[Index] : (Driver?)null, Running == null || Running(), row ? (RectTransform)row.transform : null);
            }

            void Keys()
            {
                if (UiPopup.IsOpen || PvpGhost.Active) return;
#if ENABLE_INPUT_SYSTEM
                var k = UnityEngine.InputSystem.Keyboard.current;
                if (k == null) return;
                var focus = UnityEngine.EventSystems.EventSystem.current ? UnityEngine.EventSystems.EventSystem.current.currentSelectedGameObject : null;
                if (focus && focus.GetComponent<UnityEngine.UI.InputField>()) return;
                if (k.upArrowKey.wasPressedThisFrame) Watch(Index - 1);
                if (k.downArrowKey.wasPressedThisFrame) Watch(Index + 1);
                if (k.leftArrowKey.wasPressedThisFrame) SetView((view + ViewButtons.Length - 1) % ViewButtons.Length);
                if (k.rightArrowKey.wasPressedThisFrame || k.tabKey.wasPressedThisFrame) SetView((view + 1) % ViewButtons.Length);
                // the view buttons' own keys (NATION J = '1'..'6')
                var digits = new[] { k.digit1Key, k.digit2Key, k.digit3Key, k.digit4Key, k.digit5Key, k.digit6Key };
                for (int i = 0; i < digits.Length; i++) if (digits[i].wasPressedThisFrame) SetView(i);
#endif
            }

            void Draw()
            {
                if (!viewing) return;
                for (int r = 0; r < 16; r++)
                {
                    bool used = r < order.Count;
                    string n = (r + 1).ToString();
                    var rank = viewing.Control("ID_PVP_VIEWING_PLAYER_RANK" + n);
                    var num = viewing.Control("ID_PVP_VIEWING_PLAYER_RANKNUM" + n);
                    viewing.SetVisible($"ID_PVP_VIEWING_PLAYER_LISTBASE{r + 1:00}", used);
                    Pvp.Show(rank, used); Pvp.Show(num, used);
                    if (!used) continue;
                    var d = order[r];
                    rank.SetText(d.Name);
                    num.SetText(Pvp.Ordinal(r + 1));
                    var txt = rank.GetComponentInChildren<UnityEngine.UI.Text>(true);
                    if (txt) txt.color = room != null && room.Team ? (d.Team == 0 ? new Color32(0x33, 0x99, 0xff, 0xff) : new Color32(0xff, 0x33, 0x00, 0xff)) : Color.white;
                }
                var sel = viewing.Control("ID_PVP_VIEWING_PLAYER_SEL");
                if (sel)
                {
                    sel.gameObject.SetActive(order.Count > 0);
                    var row = viewing.Control($"ID_PVP_VIEWING_PLAYER_LISTBASE{Index + 1:00}");
                    if (row) { var rt = (RectTransform)sel.transform; rt.anchoredPosition = new Vector2(rt.anchoredPosition.x, ((RectTransform)row.transform).anchoredPosition.y + 5f); }
                }
                if (room != null && room.Team && points != null)
                {
                    var (a, b) = points();
                    viewing.SetText("ID_PVP_VIEWING_TP_ATEAMPOINT", a.ToString());
                    viewing.SetText("ID_PVP_VIEWING_TP_BTEAMPOINT", b.ToString());
                }
                if (room != null) viewing.SetText("ID_PVP_VIEWING_VIEWERCOUNT", room.Observers.ToString());
            }
        }
    }
}
