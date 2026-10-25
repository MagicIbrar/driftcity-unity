using System;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Game;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Social
{
    /// <summary>
    /// The friend manager MYFRIEND 4000000, the "Walkie-Talkie" tab of the Community window (SOCIAL.md section 2): friend
    /// list / deny list tabs, 10 rows (level, name, crew + mark, location) with pages, Add (MSGBOX name prompt, 10305),
    /// Remove (11192), Block (11179), Un-Block (11180), Refresh, Whisper ("*Name "), Party (410000), New Message (2940000).
    /// Pending entries show 10545 "&lt;Friend not approved&gt;" / 10546 "&lt;Friend approval request&gt;" in the location column.
    /// Offline the friends are the offline game's AI drivers.
    /// </summary>
    public static class Friends
    {
        public const int Dialog = 4000000, Rows = 10;

        public sealed class Entry { public string Name, Crew = "", Status = "F", Location = "Offline"; public int Level; public bool Online; public int Mark; }

        public static readonly List<Entry> List = new List<Entry>();
        public static readonly List<Entry> Blocked = new List<Entry>();
        public static bool DenyTab { get; private set; }
        public static int Selected { get; private set; } = -1;
        static int page;
        static NuiView view;
        static UiManager hooked;

        public static NuiView View => view;
        static List<Entry> Shown => DenyTab ? Blocked : List;

        static void Hook()
        {
            var ui = UiManager.Instance;
            if (!ui || hooked == ui) return;
            hooked = ui;
            ui.On("ID_MYFRIEND_MANAGER_TABKEY_FRIENDLIST", _ => { DenyTab = false; page = 0; Selected = -1; Fill(); });
            ui.On("ID_MYFRIEND_MANAGER_TABKEY_DENYLIST", _ => { DenyTab = true; page = 0; Selected = -1; Fill(); });
            ui.On("ID_MYFRIEND_MANAGER_REFRESH", _ => Load());
            ui.On("ID_MYFRIEND_MANAGER_ADD_FRIEND", _ => Prompt(SocialNet.T("10305", "Enter the character name you wish to add to your Friend List."), Add));
            ui.On("ID_MYFRIEND_MANAGER_REMOVE_FRIEND", _ => RemoveSelected());
            ui.On("ID_MYFRIEND_MANAGER_DENY_FRIEND", _ =>
            {
                var sel = SelectedEntry;
                Prompt(SocialNet.T("11179", "Enter the name of the character you want to block."), Block, sel != null && !DenyTab ? sel.Name : "");
            });
            ui.On("ID_MYFRIEND_MANAGER_DENYCANCEL_FRIEND", _ =>
            {
                var sel = SelectedEntry;
                if (sel == null || !DenyTab) { UiPopup.Message(SocialNet.T("10537", "No friend selected.")); return; }
                UiPopup.Show(SocialNet.T("11180", "Remove %s from the block list?", sel.Name), id => { if (id == UiPopup.Yes) Unblock(sel.Name); }, UiPopup.Yes, UiPopup.No);
            });
            ui.On("ID_MYFRIEND_MANAGER_WHISPER", _ => { var s = SelectedEntry; if (s == null) UiPopup.Message(SocialNet.T("10467", "No friend selected.")); else SocialChat.Whisper(s.Name); });
            ui.On("ID_MYFRIEND_MANAGER_PARTY", _ => { var s = SelectedEntry; if (s == null) UiPopup.Message(SocialNet.T("10467", "No friend selected.")); else PartyWindow.Invite(s.Name); });
            ui.On("ID_MYFRIEND_MANAGER_SENDNOTE", _ => Mail.Compose(SelectedEntry?.Name ?? ""));
            ui.On("ID_MYFRIEND_MANAGER_LEFT", _ => Page(page - 10));
            ui.On("ID_MYFRIEND_MANAGER_RIGHT", _ => Page(page + 10));
            for (int i = 0; i < 10; i++)
            {
                int k = i;
                ui.On($"ID_MYFRIEND_MANAGER_PAGE{k:00}", _ => Page(page / 10 * 10 + k));
                ui.On($"ID_MYFRIEND_MANAGER_BTN{k:00}", _ => Select(page * Rows + k));
            }
        }

        static Entry SelectedEntry => Selected >= 0 && Selected < Shown.Count ? Shown[Selected] : null;

        /// <summary>Opens the manager as the Community window's content.</summary>
        public static void Show()
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            Hook();
            if (!view) { view = ui.Open(Dialog); SocialUi.HideStartHidden(view); }
            Load();
        }

        public static void Hide() => SocialUi.Close(ref view);

        static void Select(int index)
        {
            if (index < 0 || index >= Shown.Count) return;
            Selected = index;
            Fill();
        }

        static void Page(int p)
        {
            int pages = Math.Max(1, (Shown.Count + Rows - 1) / Rows);
            page = Mathf.Clamp(p, 0, pages - 1);
            Fill();
        }

        /// <summary>The list from the server (friend.list) or the offline store.</summary>
        public static void Load(Action done = null)
        {
            if (SocialNet.Online)
            {
                SocialNet.Request("friend.list", null, r =>
                {
                    if (!r.B("ok")) { done?.Invoke(); return; }
                    List.Clear(); Blocked.Clear();
                    foreach (var o in (r.L("friends") ?? new List<object>()).OfType<Dictionary<string, object>>()) List.Add(FromJson(o));
                    foreach (var o in (r.L("blocked") ?? new List<object>()).OfType<Dictionary<string, object>>()) Blocked.Add(FromJson(o));
                    Fill();
                    done?.Invoke();
                });
                return;
            }
            var st = SocialStore.Current;
            List.Clear(); Blocked.Clear();
            foreach (var f in st.Friends.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                var info = SocialStore.AiInfo(f.Name);
                var e = new Entry { Name = f.Name, Level = info.level, Status = f.Status, Online = f.Status == "F" };
                e.Location = f.Status == "F" ? $"Channel {info.channel,2} - {SocialUi.CityName(info.city)}" : "";
                (f.Status == "B" ? Blocked : List).Add(e);
            }
            Fill();
            done?.Invoke();
        }

        static Entry FromJson(Dictionary<string, object> o)
        {
            var loc = o.O("location");
            return new Entry { Name = o.S("name"), Level = o.I("level"), Status = o.S("status", "F"), Crew = o.S("crew", ""), Online = o.B("online"), Location = loc != null ? loc.S("text", "Offline") : "Offline" };
        }

        public static IReadOnlyList<string[]> RowTexts => view ? Enumerable.Range(0, Rows).Select(i => new[]
        {
            view.Control($"ID_MYFRIEND_MANAGER_CAR_LEVEL{i:00}")?.GetComponentInChildren<UnityEngine.UI.Text>(true)?.text,
            view.Control($"ID_MYFRIEND_MANAGER_USER_NAME{i:00}")?.GetComponentInChildren<UnityEngine.UI.Text>(true)?.text,
            view.Control($"ID_MYFRIEND_MANAGER_LOCATE{i:00}")?.GetComponentInChildren<UnityEngine.UI.Text>(true)?.text,
        }).ToList() : new List<string[]>();

        static void Fill()
        {
            if (!view) return;
            var list = Shown;
            view.Control("ID_MYFRIEND_MANAGER_TABKEY_FRIENDLIST").Selected = !DenyTab;
            view.Control("ID_MYFRIEND_MANAGER_TABKEY_DENYLIST").Selected = DenyTab;
            for (int i = 0; i < Rows; i++)
            {
                int idx = page * Rows + i;
                bool used = idx < list.Count;
                var e = used ? list[idx] : null;
                string n = i.ToString("00");
                foreach (var c in new[] { "CAR_LEVEL", "USER_NAME", "TEAM_NAME", "LOCATE" }) view.SetVisible($"ID_MYFRIEND_MANAGER_{c}{n}", used);
                var row = view.Control("ID_MYFRIEND_MANAGER_BTN" + n);
                if (row) { row.Selected = used && idx == Selected; row.Interactable = used; SocialUi.Highlight(row, row.Selected); }
                if (!used) { view.SetVisible("ID_MYFRIEND_MANAGER_POS_TMARK" + n, false); continue; }
                view.SetText("ID_MYFRIEND_MANAGER_CAR_LEVEL" + n, e.Level > 0 ? e.Level.ToString() : "-");
                view.SetText("ID_MYFRIEND_MANAGER_USER_NAME" + n, e.Name);
                view.SetText("ID_MYFRIEND_MANAGER_TEAM_NAME" + n, e.Crew ?? "");
                view.SetText("ID_MYFRIEND_MANAGER_LOCATE" + n, e.Status switch
                {
                    "C" => SocialNet.T("10545", "<Friend not approved>"),
                    "R" => SocialNet.T("10546", "<Friend approval request>"),
                    "B" => "",
                    _ => e.Online ? e.Location : SocialNet.T("10504", "Offline"),
                });
                SocialUi.ShowMark(view, "ID_MYFRIEND_MANAGER_POS_TMARK" + n, string.IsNullOrEmpty(e.Crew) ? 0 : e.Mark);
            }
            int first = page / 10 * 10, pages = Math.Max(1, (list.Count + Rows - 1) / Rows);
            for (int i = 0; i < 10; i++)
            {
                var c = view.Control($"ID_MYFRIEND_MANAGER_PAGE{i:00}");
                if (!c) continue;
                bool on = first + i < pages;
                c.gameObject.SetActive(on);
                if (on) { c.SetText((first + i + 1).ToString("000")); c.Selected = first + i == page; }
            }
        }

        // ---------------- actions ----------------
        /// <summary>MSGBOX 3230000 as the name prompt (0x60e520).</summary>
        public static void Prompt(string info, Action<string> ok, string prefill = "")
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            var box = ui.Open(3230000);
            if (!box) return;
            box.SetText("ID_MSGBOX_TYPE1_INFO", info);
            SocialUi.SetInput(box, "ID_MSGBOX_TYPE1_TEXT", prefill);
            foreach (var id in new[] { "ID_MSGBOX_TYPE1_DELETE", "ID_MSGBOX_TYPE1_FRIEND_REMOVE", "ID_MSGBOX_TYPE1_FRIEND_ADD" }) box.SetVisible(id, false);
            ui.On("ID_MSGBOX_TYPE1_OK", v => { var name = v.GetInput("ID_MSGBOX_TYPE1_TEXT").Trim(); ui.Close(v); if (name.Length > 0) ok(name); });
            ui.On("ID_MSGBOX_TYPE1_CANCEL", v => ui.Close(v));
            var input = box.Control("ID_MSGBOX_TYPE1_TEXT");
            if (input && input.Input) { input.Input.Select(); input.Input.ActivateInputField(); }
        }

        public static void Add(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            if (SocialNet.Online)
            {
                SocialNet.Request("friend.add", new Dictionary<string, object> { ["name"] = name }, r =>
                {
                    if (!r.B("ok")) { UiPopup.Message(SocialNet.Err(r)); return; }
                    ChatBox.System(r.B("pending") ? $"Friend request sent to {name}." : $"{name} is now on your Friend List.");
                    Load();
                });
                return;
            }
            var ai = SocialStore.AiName(name);
            var st = SocialStore.Current;
            if (ai == null) { UiPopup.Message($"{name} does not exist."); return; }
            var f = st.Friends.FirstOrDefault(x => x.Name == ai);
            if (f != null && f.Status == "F") { UiPopup.Message($"{ai} is already on your Friend List."); return; }
            if (f == null) st.Friends.Add(f = new SocialStore.Friend { Name = ai });
            f.Status = "F";   // the AI drivers accept at once
            st.Save();
            ChatBox.System(ai + SocialNet.T("10810", " has accepted your friend request."));
            Load();
        }

        static void RemoveSelected()
        {
            var sel = SelectedEntry;
            if (sel == null || DenyTab) { UiPopup.Message(SocialNet.T("10537", "No friend selected.")); return; }
            UiPopup.Show(SocialNet.T("11192", "Delete %s from your Friend List?", sel.Name), id => { if (id == UiPopup.Yes) Remove(sel.Name); }, UiPopup.Yes, UiPopup.No);
        }

        public static void Remove(string name)
        {
            if (SocialNet.Online)
            {
                SocialNet.Request("friend.remove", new Dictionary<string, object> { ["name"] = name }, r =>
                {
                    if (!r.B("ok")) { UiPopup.Message(SocialNet.Err(r)); return; }
                    ChatBox.System(SocialNet.T("10833", "%s has been deleted from your Friend List.", name));
                    Selected = -1; Load();
                });
                return;
            }
            var st = SocialStore.Current;
            st.Friends.RemoveAll(x => x.Name == name && x.Status != "B");
            st.Save();
            ChatBox.System(SocialNet.T("10833", "%s has been deleted from your Friend List.", name));
            Selected = -1; Load();
        }

        public static void Block(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            if (string.Equals(name, PlayerProfile.Current?.Name, StringComparison.OrdinalIgnoreCase)) { UiPopup.Message(SocialNet.T("11182", "You cannot block yourself.")); return; }
            if (SocialNet.Online)
            {
                SocialNet.Request("friend.block", new Dictionary<string, object> { ["name"] = name }, r =>
                {
                    if (!r.B("ok")) { UiPopup.Message(SocialNet.Err(r)); return; }
                    ChatBox.System(SocialNet.T("11274", "%s has been blocked.", name));
                    Selected = -1; Load();
                });
                return;
            }
            var ai = SocialStore.AiName(name);
            if (ai == null) { UiPopup.Message($"{name} does not exist."); return; }
            var st = SocialStore.Current;
            var f = st.Friends.FirstOrDefault(x => x.Name == ai);
            if (f != null && f.Status == "B") { UiPopup.Message(SocialNet.T("11183", "%s is already blocked.", ai)); return; }
            if (f == null) st.Friends.Add(f = new SocialStore.Friend { Name = ai });
            f.Status = "B";
            st.Save();
            Selected = -1; Load();
        }

        public static void Unblock(string name)
        {
            if (SocialNet.Online)
            {
                SocialNet.Request("friend.unblock", new Dictionary<string, object> { ["name"] = name }, r =>
                {
                    if (!r.B("ok")) { UiPopup.Message(SocialNet.Err(r)); return; }
                    ChatBox.System(SocialNet.T("11181", "%s has been removed from the block list.", name));
                    Selected = -1; Load();
                });
                return;
            }
            var st = SocialStore.Current;
            st.Friends.RemoveAll(x => x.Name == name && x.Status == "B");
            st.Save();
            Selected = -1; Load();
        }

        // ---------------- server pushes ----------------
        public static void OnPush(string t, Dictionary<string, object> m)
        {
            switch (t)
            {
                case "friend.request":
                {
                    string from = m.S("from");
                    // message box type 7: name + 10569, Yes / No -> 0xef
                    UiPopup.Show(from + SocialNet.T("10569", " has requested to add you as their friend. Add them to your Friend List?"), id =>
                        SocialNet.Request("friend.answer", new Dictionary<string, object> { ["name"] = from, ["accept"] = id == UiPopup.Yes }, _ => { if (view) Load(); }),
                        UiPopup.Yes, UiPopup.No);
                    break;
                }
                case "friend.answer": ChatBox.System(m.S("from") + (m.B("accept") ? SocialNet.T("10810", " has accepted your friend request.") : SocialNet.T("10811", " has declined your friend request."))); if (view) Load(); break;
                case "friend.removed": ChatBox.System(SocialNet.T("10832", "%s has deleted %s from the Friend List.", m.S("from"), PlayerProfile.Current?.Name ?? "you")); if (view) Load(); break;
                case "friend.online": if (m.B("online") && GameOptions.FriendTeamNotify) ChatBox.System(SocialNet.T("11412", "[Friend] %s has logged on.", m.S("name"))); if (view) Load(); break;
                case "friend.changed": if (view) Load(); break;
            }
        }
    }
}
