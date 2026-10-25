using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace DriftCity.Game
{
    /// <summary>
    /// The client's front end, in its order: LOGIN (10000) -> server select (4120000 backdrop + 4130000 list,
    /// 0x76c080) -> character select (4140000, 0x769590) -> start-up story for a first driver (IntroScene) -> licence
    /// registration (50000 + 140000) -> Select Car (150000) -> Cras for a new driver, the parking lot otherwise.
    /// Offline play is one more row in the server list, with three local save slots.
    /// </summary>
    public sealed partial class GameFlow
    {
        bool online, busy, entering;
        string account, password;
        int pendingAvatar = 1;

        /// <summary>A row of the server list: an online server, or offline play (Url null).</summary>
        public sealed class ServerRow
        {
            public string Name, Url;
            public int Players = -1, Capacity;   // -1: not answered (yet)
            public bool Offline => Url == null;
            public float Load => Capacity > 0 && Players >= 0 ? Mathf.Clamp01((float)Players / Capacity) : 0f;
        }

        public List<ServerRow> ServerRows { get; private set; } = new List<ServerRow>();
        int serverIndex;

        // ---------------- login ----------------
        /// <summary>LOGIN: account and password (offline play needs neither), then the server list.</summary>
        void ShowLogin()
        {
            ClearCity();
            ui.CloseAll();
            // always: a login still in flight is cancelled too (its reply is then ignored)
            if (OnlineSession.Instance) OnlineSession.Instance.Logout();
            busy = false;
            online = false;
            var v = ui.Open(10000);
            v.SetText("ID_LOGIN_STATIC_TITLE", "Enter your account and password (offline play needs neither).");
            v.SetText("ID_LOGIN_STATIC_ID", "Account :");
            v.SetText("ID_LOGIN_STATIC_PASS", "Password :");
            var input = v.Control("ID_LOGIN_ID")?.Input;
            if (input) input.text = account ?? PlayerPrefs.GetString("dc_account", "");
            ui.On("ID_LOGIN_QUIT", _ => Quit());
            ui.On("ID_LOGIN_ENTER", view =>
            {
                account = view.GetInput("ID_LOGIN_ID").Trim();
                password = view.GetInput("ID_LOGIN_PASS");
                if (account.Length > 0) PlayerPrefs.SetString("dc_account", account);
                ShowServerSelect();
            });
        }

        // ---------------- server select ----------------
        /// <summary>Server select: 4120000 is the backdrop (crowd gauge, state text, close), 4130000 the list box and Confirm.</summary>
        public void ShowServerSelect()
        {
            ClearCity();
            ui.CloseAll();
            if (OnlineSession.Instance) OnlineSession.Instance.Logout();
            busy = false;
            ServerRows = OnlineSession.Servers().Select(s => new ServerRow { Name = s.Name, Url = s.Url }).ToList();
            ServerRows.Add(new ServerRow { Name = "Offline (this PC)", Url = null });
            serverIndex = Mathf.Clamp(PlayerPrefs.GetInt("dc_server", 0), 0, ServerRows.Count - 1);

            var back = ui.Open(4120000);
            var list = ui.Open(4130000);
            back.SetText("ID_SELECTSERVER_HANDLE_VERSION", "DRIFT CITY REMASTERED");
            list.SetText("ID_SELECETSERVER_LIST_VERSIONINFO", "");
            var state = back.Control("ID_SELECTSERVER_HANDLE__SELSTATE");
            if (state)
            {
                state.SetText("");   // make sure the text element exists before sizing it
                foreach (var rt in state.GetComponentsInChildren<RectTransform>(true)) rt.sizeDelta = new Vector2(260, 20);   // authored 0x0
                foreach (var t in state.GetComponentsInChildren<Text>(true)) { t.horizontalOverflow = HorizontalWrapMode.Overflow; t.alignment = TextAnchor.MiddleLeft; }
            }

            var box = new NuiListBox(list.Control("ID_SELECETSERVER_LIST_LISTBOX"), 8, 27f);
            box.Selected += i => { serverIndex = i; RefreshServerState(back); };
            box.Activated += i => { serverIndex = i; PickServer(); };
            RefreshServerRows(box, back);
            ui.On("ID_SELECETSERVER_LIST_NEXT", _ => PickServer());
            ui.On("ID_SELECTSERVER_HANDLE_CLOSE", _ =>
                UiPopup.Show(GameTables.Text("10093"), id => { if (id == UiPopup.Yes) Quit(); }, UiPopup.Yes, UiPopup.No));
            foreach (var row in ServerRows.Where(r => !r.Offline)) StartCoroutine(ProbeServer(row, () => { if (list) RefreshServerRows(box, back); }));
        }

        void RefreshServerRows(NuiListBox box, NuiView back)
        {
            box.SetRows(ServerRows.Select(r => (r.Name, r.Offline ? "Single player" : r.Players < 0 ? "..." : StateText(r))).ToList(), serverIndex);
            RefreshServerState(back);
        }

        static string StateText(ServerRow r) => r.Players < 0 ? "No answer" : r.Load >= 0.99f ? "Full" : r.Load >= 0.6f ? "Busy" : r.Load >= 0.3f ? "Normal" : "Smooth";

        /// <summary>The backdrop's 15-grain crowd gauge and state line for the selected server.</summary>
        void RefreshServerState(NuiView back)
        {
            if (!back || serverIndex < 0 || serverIndex >= ServerRows.Count) return;
            var r = ServerRows[serverIndex];
            int grains = r.Offline ? 1 : Mathf.Clamp(Mathf.CeilToInt(r.Load * 15f), 1, 15);
            for (int i = 1; i <= 15; i++) back.SetVisible($"ID_SELECTSERVER_HANDLE_GRAIN{i:00}", i <= grains);
            back.SetText("ID_SELECTSERVER_HANDLE__SELSTATE", r.Offline ? "Offline: saved on this PC" : r.Players < 0 ? $"{r.Name}: no answer" : $"{r.Name}: {StateText(r)} ({r.Players}/{r.Capacity})");
        }

        /// <summary>The server's HTTP status page on the same port: {name, online, channels:[{players, capacity}]}.</summary>
        static IEnumerator ProbeServer(ServerRow row, Action done)
        {
            string url = row.Url.Replace("wss://", "https://").Replace("ws://", "http://");
            using (var req = UnityWebRequest.Get(url))
            {
                req.timeout = 3;
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success)
                {
                    var j = MiniJson.ParseObject(req.downloadHandler.text);
                    if (j != null)
                    {
                        row.Players = j.I("online");
                        var ch = j.L("channels");
                        row.Capacity = ch == null ? 0 : ch.OfType<Dictionary<string, object>>().Sum(c => c.I("capacity"));
                        if (row.Capacity <= 0) row.Capacity = 30;
                    }
                }
                else row.Players = -1;
            }
            done?.Invoke();
        }

        /// <summary>Picks a server row by URL (null = offline) as Confirm would; adds the row when it isn't listed (tests, tools).</summary>
        public void ChooseServer(string url, string name = null)
        {
            int i = ServerRows.FindIndex(r => r.Url == url);
            if (i < 0) { ServerRows.Insert(0, new ServerRow { Name = name ?? url, Url = url }); i = 0; }
            serverIndex = i;
            PickServer();
        }

        /// <summary>Confirm on the server list (0x76b800): a full server refuses (text 10987); else log in there.</summary>
        void PickServer()
        {
            if (busy || serverIndex < 0 || serverIndex >= ServerRows.Count) return;
            var row = ServerRows[serverIndex];
            PlayerPrefs.SetInt("dc_server", serverIndex);
            if (row.Offline) { online = false; ShowCharacterSelect(); return; }
            if (row.Players >= 0 && row.Load >= 0.99f) { UiPopup.Message(GameTables.Text("10987")); return; }
            if (string.IsNullOrEmpty(account)) { UiPopup.Message("Online play needs an account: go back and enter an account name and password."); return; }
            ConnectOnline(row, false);
        }

        void ConnectOnline(ServerRow row, bool register)
        {
            online = true;
            busy = true;
            var session = OnlineSession.Ensure();
            var list = ui.Find(4130000);
            session.ConnectAndLogin(row.Url, account, password ?? "", register, (ok, error, hasDriver) =>
            {
                busy = false;
                if (!list) return;   // backed out meanwhile
                if (ok) { ShowCharacterSelect(); return; }
                if (!register && error != null && error.Contains("wrong username"))
                    UiPopup.Show($"No account \"{account}\" with that password on {row.Name}.\nCreate a new account with this name?", id =>
                    {
                        if (id == UiPopup.Yes && !busy) ConnectOnline(row, true);
                    }, UiPopup.Yes, UiPopup.No);
                else UiPopup.Message(error);
            });
        }

        // ---------------- character select ----------------
        // portrait N is CHARIMG_N everywhere: 140000 names them ch_u_000N and 4140000 shows the same ch_u_001.dds cells in that order
        readonly PlayerProfile[] slots = new PlayerProfile[PlayerProfile.Slots];
        int selectedSlot;

        /// <summary>Character select: three licences around Enter; the selected one is drawn large (0x768da0).</summary>
        public void ShowCharacterSelect()
        {
            ClearCity();
            ui.CloseAll();
            busy = false;
            for (int i = 0; i < slots.Length; i++)
                slots[i] = online ? (i == 0 && PlayerProfile.Current != null && PlayerProfile.Current.OnlineDriver ? PlayerProfile.Current : null) : PlayerProfile.Peek(i);
            if (!online) selectedSlot = Mathf.Clamp(PlayerPrefs.GetInt("dc_slot", 0), 0, slots.Length - 1);
            else selectedSlot = 0;
            if (slots[selectedSlot] == null) { int any = Array.FindIndex(slots, s => s != null); if (any >= 0) selectedSlot = any; }

            var v = ui.Open(4140000);
            v.SetText("ID_SELECT_CHARACTER_VERSION", "DRIFT CITY REMASTERED");
            RefreshCharacterSelect(v);
            for (int n = 1; n <= 3; n++)
            {
                int slot = n - 1, k = n;
                ui.On($"ID_SELECT_CHARACTER{n}_COVER", view => { selectedSlot = slot; RefreshCharacterSelect(view); });
                ui.On($"ID_SELECT_CHARACTER{n}_CREATE_LICENSE", view => { selectedSlot = slot; CreateCharacterInSlot(); });
                ui.On($"ID_SELECT_CHARACTER{n}_CREATE_LICENSE_IMG", view => { selectedSlot = slot; CreateCharacterInSlot(); });
                ui.On($"ID_SELECT_CHARACTER{n}_DEL_BTN", view => DeleteCharacter(view, slot));
                ui.On($"ID_SELECT_CHARACTER{n}_DEL_CANCEL", view => CancelDeletion(view, slot));
                var cover = v.Control($"ID_SELECT_CHARACTER{n}_COVER");
                if (cover) cover.Hovered += on => { for (int m = 1; m <= 3; m++) v.SetVisible($"ID_SELECT_CHARACTER{k}_ONMOUSE{m}", on && v.Control($"ID_SELECT_CHARACTER{k}_COVER").isActiveAndEnabled); };
                var del = v.Control($"ID_SELECT_CHARACTER{n}_DEL_BTN");
                if (del) del.Hovered += on => v.SetVisible($"ID_SELECT_CHARACTER{k}_DEL_TOOLTIP", on);
            }
            ui.On("ID_CREATE_CHARACTER_ENTER", _ => { selectedSlot = Array.FindIndex(slots, s => s == null); if (selectedSlot < 0) selectedSlot = 0; CreateCharacterInSlot(); });
            ui.On("ID_SELECT_CHARACTER_ENTER", _ => EnterSelectedCharacter());
            ui.On("ID_SELECT_CHARACTER_BACK", _ => ShowServerSelect());
            StartCoroutine(DeletionClock(v));
        }

        /// <summary>The marked licences' TIME counts down (0x766304 refreshes the rows); a due one gets 11099 once (0x768010).</summary>
        IEnumerator DeletionClock(NuiView v)
        {
            var asked = new bool[slots.Length];
            while (v && ui.IsOpen(v))
            {
                if (!online)
                    for (int i = 0; i < slots.Length; i++)
                    {
                        if (slots[i] == null || !CharacterDeletion.Marked(i)) { asked[i] = false; continue; }
                        string s = "ID_SELECT_CHARACTER" + (i + 1);
                        v.SetText(s + "_TIME", CharacterDeletion.TimeText(CharacterDeletion.Remaining(i)));
                        if (CharacterDeletion.Due(i) && !asked[i] && !UiPopup.IsOpen && !MsgBox.IsOpen)
                        {
                            asked[i] = true;
                            int slot = i;
                            UiPopup.Show(CharacterDeletion.FinalQuestion(slots[slot].Name), id =>
                            {
                                if (id != UiPopup.Yes) return;
                                CharacterDeletion.Delete(slot);
                                slots[slot] = null;
                                if (v) RefreshCharacterSelect(v);
                            }, UiPopup.Yes, UiPopup.No);
                        }
                    }
                yield return new WaitForSecondsRealtime(1f);
            }
        }

        void RefreshCharacterSelect(NuiView v)
        {
            bool none = slots.All(s => s == null);
            // no licence yet: the full-screen cover and the big Create Character button only
            v.SetVisible("ID_SELECT_CHARACTER_COVER", none);
            v.SetVisible("ID_CREATE_CHARACTER_ENTER", none);
            v.SetVisible("ID_SELECT_CHARACTER_ENTER", !none);
            v.SetVisible("ID_SELECT_CHARACTER_RECENT", false);
            for (int n = 1; n <= 3; n++)
            {
                var p = none ? null : slots[n - 1];
                bool large = !none && n - 1 == selectedSlot, filled = p != null;
                string s = "ID_SELECT_CHARACTER" + n;
                // small licence (not selected)
                foreach (var id in new[] { "_OMDCOVER_SMALL", "_NAME_SMALL", "_LEVEL_SMALL", "_TEAMNAME_SMALL", "_DATE_SMALL" }) v.SetVisible(s + id, !none && !large && filled);
                v.SetVisible(s + "_ROOCOVER_SMALL", false);
                v.SetVisible(s + "_TEAMMARK_SMALL", false);
                v.SetVisible(s + "_DEL_MARK_SMALL", false);
                v.SetVisible(s + "_COVER", !none && !large && filled);
                // large licence (selected)
                foreach (var id in new[] { "_OMDCOVER", "_NAME", "_LEVEL", "_TEAMNAME", "_DATE", "_DEL_BTN" }) v.SetVisible(s + id, large && filled);
                v.SetVisible(s + "_ROOCOVER", false);
                v.SetVisible(s + "_TEAMMARK", false);
                foreach (var id in new[] { "_DEL_MARK", "_DEL_COVER", "_DEL_CANCEL", "_TIME", "_DEL_TOOLTIP" }) v.SetVisible(s + id, false);
                // a licence marked for deletion (state 'R'): the mark, the cover, the time left and the cancel button
                bool marked = filled && !online && CharacterDeletion.Marked(n - 1);
                if (marked)
                {
                    foreach (var id in new[] { "_DEL_MARK", "_DEL_COVER", "_DEL_CANCEL", "_TIME" }) v.SetVisible(s + id, large);
                    v.SetVisible(s + "_DEL_MARK_SMALL", !large);
                    v.SetVisible(s + "_DEL_BTN", false);
                    v.SetText(s + "_TIME", CharacterDeletion.TimeText(CharacterDeletion.Remaining(n - 1)));
                }
                // an empty slot offers a new licence
                v.SetVisible(s + "_CREATE_LICENSE", !none && !filled);
                v.SetVisible(s + "_CREATE_LICENSE_IMG", !none && !filled);
                for (int m = 1; m <= 3; m++) { v.SetVisible(s + "_ONMOUSE" + m, false); v.SetVisible(s + "_LARGE_ONMOUSE" + m, large && filled); }
                for (int k = 1; k <= 8; k++)
                {
                    bool mine = filled && k == p.Avatar;
                    v.SetVisible($"ID_SELECT_CHARACTER_CHARIMG{n}_{k:00}", large && mine);
                    v.SetVisible($"ID_SELECT_CHARACTER_CHARIMG{n}_SMALL_{k:00}", !none && !large && mine);
                }
                if (!filled) continue;
                string date = DateTime.TryParse((p.Created ?? "").Replace('.', '-'), out var d) ? d.ToString("yy.MM.dd") : "--.--.--";
                foreach (var suffix in new[] { "", "_SMALL" })
                {
                    v.SetText(s + "_NAME" + suffix, p.Name);
                    v.SetText(s + "_LEVEL" + suffix, p.Level.ToString());
                    v.SetText(s + "_TEAMNAME" + suffix, "-");
                    v.SetText(s + "_DATE" + suffix, date);
                    // the licence fields are light grey boxes: dark ink, as printed on the card
                    foreach (var id in new[] { "_NAME", "_LEVEL", "_TEAMNAME", "_DATE" })
                    {
                        var c = v.Control(s + id + suffix);
                        if (c) foreach (var t in c.GetComponentsInChildren<Text>(true)) t.color = new Color(0.16f, 0.16f, 0.2f);
                    }
                }
            }
        }

        void EnterSelectedCharacter()
        {
            var p = slots[selectedSlot];
            if (p == null) { CreateCharacterInSlot(); return; }   // the client sends an empty slot to character creation (0x768c90)
            if (online)
            {
                if (!string.IsNullOrEmpty(PlayerProfile.Current?.City)) City = PlayerProfile.Current.City;
                OnLoginComplete();
                return;
            }
            PlayerProfile.Slot = selectedSlot;
            PlayerPrefs.SetInt("dc_slot", selectedSlot);
            PlayerProfile.Load();
            if (PlayerProfile.Current == null) { UiPopup.Message("This driver's save couldn't be read."); return; }
            if (!string.IsNullOrEmpty(PlayerProfile.Current.City)) City = PlayerProfile.Current.City;
            OnLoginComplete();
        }

        void CreateCharacterInSlot()
        {
            if (online && slots.Any(s => s != null)) { UiPopup.Message("This server keeps one driver per account."); return; }
            if (slots[selectedSlot] != null) { int free = Array.FindIndex(slots, s => s == null); if (free < 0) return; selectedSlot = free; }
            // a first driver sees the start-up story before registering (the remaster's opening; Esc, Space or Enter skip it)
            if (!SkipIntro && slots.All(s => s == null) && DriftCityData.Files.Exists(@"game\intro\newintro.nif"))
            {
                ui.CloseAll();
                StartCoroutine(IntroScene.Play(this, ShowCreateCharacter));
                return;
            }
            ShowCreateCharacter();
        }

        /// <summary>
        /// DEL_BTN (0x767c80): MSGBOX 10530 "Enter the name of character to delete." with DELETE; the right name marks the
        /// driver for deletion (CharacterDeletion: the time left shows on the licence, 11099 asks once it has run out).
        /// </summary>
        void DeleteCharacter(NuiView v, int slot)
        {
            var p = slots[slot];
            if (p == null) return;
            if (online) { UiPopup.Message("Drivers on a server can't be deleted from the game."); return; }
            if (CharacterDeletion.Marked(slot)) return;
            MsgBox.Prompt(GameTables.Text("10530"), typed =>
            {
                // a different name leaves the driver as it is [inferred]
                if (!string.Equals((typed ?? "").Trim(), p.Name, StringComparison.Ordinal)) return;
                CharacterDeletion.Mark(slot);
                if (v) RefreshCharacterSelect(v);
            }, "", MsgBox.Confirm.Delete, 16);
        }

        /// <summary>DEL_CANCEL: 11098 "Cancel the deletion of this character?"</summary>
        void CancelDeletion(NuiView v, int slot)
        {
            if (online || !CharacterDeletion.Marked(slot)) return;
            UiPopup.Show(GameTables.Text("11098"), id =>
            {
                if (id != UiPopup.Yes) return;
                CharacterDeletion.Cancel(slot);
                if (v) RefreshCharacterSelect(v);
            }, UiPopup.Yes, UiPopup.No);
        }

        // ---------------- new character ----------------
        /// <summary>
        /// Licence registration as the remaster client shows it: the operator's backdrop (CHARCREATESELECT 50000, Erina and
        /// the city) with the OMD driver licence (CREATECHARACTER 140000) on top: portrait arrows, name, availability check
        /// and today's date. Next goes on to the starter car (150000), Previous back to the character select.
        /// </summary>
        void ShowCreateCharacter()
        {
            ui.CloseAll();
            var bg = ui.Open(50000);
            var v = ui.Open(140000);
            bg.SetText("ID_CHARCREATESELECT_BG_STATIC_NPCNAME", "Operator Erina");
            bg.SetText("ID_CHARCREATESELECT_BG_STATIC_NPCTEXT", "Welcome to Drift City Remastered! Please select your license picture and enter your name.");
            v.SetText("ID_CREATECHARACTER_NEW_DATE", DateTime.Now.ToString("yyyy.MM.dd"));
            var input = v.Control("ID_CREATECHARACTER_NEW_NAMEINPUT")?.Input;
            if (input) { input.text = pendingName ?? (online ? account : ""); input.characterLimit = 16; }
            ShowAvatar(v);
            ui.On("ID_CREATECHARACTER_NEW_LEFT", view => { pendingAvatar = pendingAvatar == 1 ? 8 : pendingAvatar - 1; ShowAvatar(view); });
            ui.On("ID_CREATECHARACTER_NEW_RIGHT", view => { pendingAvatar = pendingAvatar == 8 ? 1 : pendingAvatar + 1; ShowAvatar(view); });
            ui.On("ID_CREATECHARACTER_NEW_NAMECONFIRM", view =>
            {
                var error = NameProblem(view.GetInput("ID_CREATECHARACTER_NEW_NAMEINPUT").Trim());
                UiPopup.Message(error ?? "This name is available.");
            });
            ui.On("ID_CHARCREATESELECT_BG_NEXT", _ =>
            {
                var card = ui.Find(140000);
                var name = card ? card.GetInput("ID_CREATECHARACTER_NEW_NAMEINPUT").Trim() : "";
                var error = NameProblem(name);
                if (error != null) { UiPopup.Message(error); return; }
                pendingName = name;
                ShowCreateCar();
            });
            ui.On("ID_CHARCREATESELECT_BG_PREV", _ => ShowCharacterSelect());
            ui.On("ID_CHARCREATESELECT_BG_EXIT", _ => UiPopup.Show(GameTables.Text("10093"), id => { if (id == UiPopup.Yes) Quit(); }, UiPopup.Yes, UiPopup.No));
        }

        void ShowAvatar(NuiView v)
        {
            for (int k = 1; k <= 8; k++) v.SetVisible("ID_CREATECHARACTER_NEW_CHARIMG_" + k, k == pendingAvatar);
        }

        /// <summary>Names follow the server's rule (2-16 letters, digits, _ or -); offline they must differ from the other slots.</summary>
        string NameProblem(string name)
        {
            if (name.Length < 2 || name.Length > 16 || !System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9_-]+$"))
                return "Driver names are 2-16 letters, digits, _ or -.";
            if (!online && slots.Where((s, i) => s != null && i != selectedSlot).Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
                return "Another of your drivers already uses this name.";
            // 0x749f02: the slang and operator-name lists (10606 "That ID is not allowed.")
            var refused = NameFilter.CheckDriverName(name);
            if (refused != null) return GameTables.Text(refused);
            return null;
        }

        /// <summary>Saves a new offline driver into the slot picked on the character screen.</summary>
        void CreateOfflineDriver(int vehicleIndex, int color)
        {
            PlayerProfile.Slot = selectedSlot;
            PlayerPrefs.SetInt("dc_slot", selectedSlot);
            var p = new PlayerProfile { Name = pendingName ?? "Driver", Avatar = pendingAvatar, Created = DateTime.Now.ToString("yyyy.MM.dd"), Apprentice = true };
            p.Cars.Add(new OwnedCar { VehicleIndex = vehicleIndex, Color = color });
            p.Save();
            PlayerProfile.Current = p;
        }
    }

    /// <summary>
    /// A game.nui list box (TYPE 9): rows of text laid over the control, one selected; a second click on the
    /// selected row (or double click) activates it. Rows sit <c>pitch</c> apart from the element-2 offset (POS2).
    /// </summary>
    public sealed class NuiListBox
    {
        public event Action<int> Selected, Activated;
        readonly NuiControlView box;
        readonly List<(RectTransform rt, Image bg, Text left, Text right)> rows = new List<(RectTransform, Image, Text, Text)>();
        readonly Color text, selectedText, highlight;
        int selected = -1;
        float lastClick;

        public NuiListBox(NuiControlView box, int visibleRows, float pitch)
        {
            this.box = box;
            if (!box) return;
            var c = box.Data;
            Core.NuiControl.TryVec2(c.I("SZ2"), out float w, out float h);
            if (w <= 0) { w = 200; h = 24; }
            text = NuiControlView.ParseColor(c.I("FC1_1"), Color.white);
            highlight = NuiControlView.ParseColor(c.I("C2_1"), new Color(0.9f, 0.75f, 0f));
            selectedText = Color.black;
            for (int i = 0; i < visibleRows; i++)
            {
                var rt = NuiView.NewRect("row" + i, box.transform);
                rt.anchoredPosition = new Vector2(4, -(4 + pitch * i));
                rt.sizeDelta = new Vector2(w, h);
                var bg = rt.gameObject.AddComponent<Image>();
                bg.color = new Color(0, 0, 0, 0.001f);
                int index = i;
                var trigger = rt.gameObject.AddComponent<UnityEngine.EventSystems.EventTrigger>();
                var entry = new UnityEngine.EventSystems.EventTrigger.Entry { eventID = UnityEngine.EventSystems.EventTriggerType.PointerClick };
                entry.callback.AddListener(_ => Click(index));
                trigger.triggers.Add(entry);
                var left = Label(rt, c.I("FN2"), TextAnchor.MiddleLeft, new Vector2(8, 0), new Vector2(w * 0.62f, h));
                var right = Label(rt, c.I("FN2"), TextAnchor.MiddleRight, new Vector2(w * 0.62f, 0), new Vector2(w * 0.38f - 8, h));
                rows.Add((rt, bg, left, right));
            }
        }

        static Text Label(RectTransform parent, string font, TextAnchor align, Vector2 pos, Vector2 size)
        {
            var rt = NuiView.NewRect("text", parent);
            rt.anchoredPosition = new Vector2(pos.x, -pos.y);
            rt.sizeDelta = size;
            var t = rt.gameObject.AddComponent<Text>();
            t.font = UiAssets.Font(font ?? "Trebuchet MS 14 400", out int s, out var style);
            t.fontSize = s; t.fontStyle = style;
            t.alignment = align;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.raycastTarget = false;
            rt.gameObject.AddComponent<Outline>().effectColor = Color.black;
            return t;
        }

        public int Count { get; private set; }

        public void SetRows(IList<(string left, string right)> items, int select)
        {
            Count = Mathf.Min(items.Count, rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                bool used = i < Count;
                rows[i].rt.gameObject.SetActive(used);
                if (!used) continue;
                rows[i].left.text = items[i].left;
                rows[i].right.text = items[i].right;
            }
            Select(Mathf.Clamp(select, 0, Count - 1), false);
        }

        public void Select(int i, bool notify = true)
        {
            selected = i;
            for (int r = 0; r < rows.Count; r++)
            {
                bool on = r == selected;
                rows[r].bg.color = on ? highlight : new Color(0, 0, 0, 0.001f);
                rows[r].left.color = rows[r].right.color = on ? selectedText : text;
                rows[r].left.fontStyle = on ? FontStyle.Bold : FontStyle.Normal;
                foreach (var o in rows[r].rt.GetComponentsInChildren<Outline>()) o.enabled = !on;
            }
            if (notify && selected >= 0) Selected?.Invoke(selected);
        }

        /// <summary>Clicks a row (also used by tests): selects it, or activates it when it was already selected (double click).</summary>
        public void Click(int i)
        {
            if (i < 0 || i >= Count) return;
            UiSounds.Click();
            bool again = i == selected && Time.unscaledTime - lastClick < 0.5f;
            lastClick = Time.unscaledTime;
            if (i != selected) Select(i);
            if (again) Activated?.Invoke(i);
        }
    }
}
