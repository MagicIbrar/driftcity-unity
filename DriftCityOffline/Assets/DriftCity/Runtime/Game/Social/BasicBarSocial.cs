using System;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Game;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.Social
{
    /// <summary>
    /// The friend and note windows of the basic main bar (UI type 0: game+0x24c &amp; 0x18 not ADV; SOCIAL.md 0 / 8.3):
    /// MYFRIEND_LIST 2930000 (tabs Friends / Crew / Users / Block list over one list box with grade, name, crew and
    /// location columns; WHISPER, PARTY, FRIEND_ADD, FRIEND_REMOVE, REFRESH), the inbox MYFRIEND_MSGBOX 2960000 and the
    /// saved box 2970000 (10 rows with check boxes and NEW marks, pages, DELETE, SAVE, the all box, SEND), the reader
    /// MSGREAD 2950000 (REPLY, DELETE) and the shared send dialog 2940000 (Mail.Compose). The data are the ADV windows'
    /// (Friends, Mail, Teams; the server's friend.* / note.* messages), so both bars show the same lists.
    /// </summary>
    public static class BasicBarSocial
    {
        public const int ListDlg = 2930000, ReadDlg = 2950000, InboxDlg = 2960000, SavedDlg = 2970000, Rows = 10;
        const string L = "ID_MYFRIEND_LIST_", B = "ID_MYFRIEND_MSGBOX_", S = "ID_MYFRIEND_SAVE_MSGBOX_";
        static readonly string[] Tabs = { "FRIENDLIST", "RACINGTEAMLIST", "USERLIST", "BLACKLIST" };
        static readonly string[] Nth = { "1ST", "2ND", "3RD", "4TH", "5TH", "6TH", "7TH", "8TH", "9TH", "10TH" };

        public sealed class Row { public string Name = "", Crew = "", Location = ""; public int Grade; public bool Online; }

        static NuiView list, box, read;
        static NuiListBox lb;
        static UiManager hooked;
        static int tab, selected = -1, page;
        static bool savedBox;
        static readonly List<Row> rows = new List<Row>();
        static readonly HashSet<int> ticked = new HashSet<int>();
        static Mail.Note reading;

        public static NuiView ListView => list;
        public static NuiView BoxView => box;
        public static NuiView ReadView => read;
        public static int Tab => tab;
        public static IReadOnlyList<Row> Rows_ => rows;
        public static bool SavedBox => savedBox;
        public static Mail.Note Reading => reading;

        /// <summary>The basic bar's FRIEND button: 2930000 on the friend tab.</summary>
        public static void OpenFriends(int startTab = 0)
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            Hook(ui);
            if (!list) list = ui.Open(ListDlg);
            if (!list) return;
            foreach (var t in Tabs) list.SetVisible(L + "TABKEY_BTN_" + t, false);
            lb = new NuiListBox(list.Control(L + "LB"), 12, 20);
            lb.Selected += i => selected = i;
            lb.Activated += i => { selected = i; Whisper(); };
            ShowTab(startTab);
        }

        /// <summary>The basic bar's MESSAGE button: the inbox 2960000 (or the saved box 2970000).</summary>
        public static void OpenInbox(bool saved = false)
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            Hook(ui);
            SocialUi.Close(ref box);
            savedBox = saved;
            box = ui.Open(saved ? SavedDlg : InboxDlg);
            if (!box) return;
            page = 0; ticked.Clear();
            Mail.Load(FillBox);
            FillBox();
        }

        public static void CloseAll() { SocialUi.Close(ref read); SocialUi.Close(ref box); SocialUi.Close(ref list); reading = null; }

        static void Hook(UiManager ui)
        {
            if (hooked == ui) return;
            hooked = ui;
            ui.On(L + "CLOSE", _ => SocialUi.Close(ref list));
            for (int i = 0; i < Tabs.Length; i++) { int k = i; ui.On(L + "TAB_BTN_" + Tabs[i], _ => ShowTab(k)); }
            ui.On(L + "REFRESH", _ => ShowTab(tab));
            ui.On(L + "WHISPER", _ => Whisper());
            ui.On(L + "PARTY", _ => { var r = SelectedRow; if (r == null) UiPopup.Message(SocialNet.T("10467", "No friend selected.")); else PartyWindow.Invite(r.Name); });
            ui.On(L + "FRIEND_ADD", _ =>
            {
                var r = SelectedRow;
                if (tab == 3) { Friends.Prompt(SocialNet.T("11179", "Block"), name => { if (!string.IsNullOrWhiteSpace(name)) { Friends.Block(name.Trim()); Later(() => ShowTab(tab)); } }); return; }
                if (r != null && tab != 0) { Friends.Add(r.Name); Later(() => ShowTab(tab)); return; }
                Friends.Prompt(SocialNet.T("10203", "Add Friend"), name => { if (!string.IsNullOrWhiteSpace(name)) { Friends.Add(name.Trim()); Later(() => ShowTab(tab)); } });
            });
            ui.On(L + "FRIEND_REMOVE", _ =>
            {
                var r = SelectedRow;
                if (r == null) { UiPopup.Message(SocialNet.T("10537", "No friend selected.")); return; }
                if (tab == 3) UiPopup.Show(SocialNet.T("11180", "Remove %s from the block list?", r.Name), id => { if (id == UiPopup.Yes) { Friends.Unblock(r.Name); Later(() => ShowTab(tab)); } }, UiPopup.Yes, UiPopup.No);
                else if (tab == 0) UiPopup.Show(SocialNet.T("11192", "Remove %s from the friend list?", r.Name), id => { if (id == UiPopup.Yes) { Friends.Remove(r.Name); Later(() => ShowTab(tab)); } }, UiPopup.Yes, UiPopup.No);
            });
            // the note boxes
            foreach (var p in new[] { B, S })
            {
                string pre = p;
                ui.On(pre + "CLOSE", _ => { SocialUi.Close(ref read); SocialUi.Close(ref box); });
                ui.On(pre + "BTN_SEND", _ => Mail.Compose(""));
                ui.On(pre + "PAGE_LEFT", _ => { page = Mathf.Max(0, page - 1); FillBox(); });
                ui.On(pre + "PAGE_RIGHT", _ => { page = Mathf.Min(Pages - 1, page + 1); FillBox(); });
                ui.On(pre + "DELETE", _ => DeleteTicked());
                ui.On(pre + "CB_ALL", v => { bool all = v.Control(pre + "CB_ALL")?.Checked ?? false; ticked.Clear(); if (all) foreach (var n in PageNotes) ticked.Add(n.Id); FillBox(); });
                for (int i = 0; i < Rows; i++)
                {
                    int k = i;
                    ui.On(pre + "CB" + Nth[i], v => { var n = NoteAt(k); if (n == null) return; if (!ticked.Remove(n.Id)) ticked.Add(n.Id); FillBox(); });
                }
            }
            ui.On(B + "SAVE", _ => SaveTicked());
            ui.On("ID_MYFRIEND_SAVE_MSGBOX_BTN", _ => OpenInbox(true));   // the inbox's "saved box" tab
            ui.On("ID_MYFRIEND_MSGBOX_BTN", _ => OpenInbox(false));       // the saved box's "inbox" tab
            ui.On(B.TrimEnd('_'), _ => OpenTicked());
            ui.On(S.TrimEnd('_'), _ => OpenTicked());
            // the reader
            ui.On("ID_MYFRIEND_MSGREAD_REPLY", _ => { if (reading != null && reading.Type == 0) Mail.Compose(reading.Sender); });
            ui.On("ID_MYFRIEND_MSGREAD_DELETE", _ => { if (reading != null) { var id = reading.Id; SocialUi.Close(ref read); reading = null; Mail.Delete(new[] { id }); Later(FillBox); } });
        }

        static void Later(Action a) { if (GameFlow.Instance) GameFlow.Instance.StartCoroutine(After(a)); else a(); }
        static System.Collections.IEnumerator After(Action a) { yield return new WaitForSecondsRealtime(0.3f); a(); }

        // ---------------- 2930000 ----------------
        static Row SelectedRow => selected >= 0 && selected < rows.Count ? rows[selected] : null;

        public static void ShowTab(int t)
        {
            if (!list) return;
            tab = Mathf.Clamp(t, 0, 3);
            selected = -1;
            for (int i = 0; i < Tabs.Length; i++)
            {
                var b = list.Control(L + "TAB_BTN_" + Tabs[i]); if (b) b.Selected = i == tab;
                foreach (var col in new[] { "BASE1ST", "BASE2ND", "BASE3RD", "CARGRADE", "NAME", "RACINGTEAMNAME", "LOCATION" })
                    list.SetVisible($"{L}{Tabs[i]}_{col}", i == tab);
            }
            list.SetText(L + "FRIEND_INFOTEXT", tab == 3 ? SocialNet.T("11179", "Block list") : tab == 2 ? "Drivers online" : tab == 1 ? "Crew members" : "Friends");
            void Show(IEnumerable<Row> rs)
            {
                rows.Clear(); rows.AddRange(rs);
                lb?.SetRows(rows.Select(r => ($"V{Mathf.Max(1, r.Grade),-3} {r.Name}", $"{(string.IsNullOrEmpty(r.Crew) ? "-" : r.Crew)}   {r.Location}")).ToList(), -1);
            }
            switch (tab)
            {
                case 0: Friends.Load(() => Show(Friends.List.Select(FromFriend))); Show(Friends.List.Select(FromFriend)); break;
                case 3: Friends.Load(() => Show(Friends.Blocked.Select(FromFriend))); Show(Friends.Blocked.Select(FromFriend)); break;
                case 1:
                    {
                        var c = Teams.Mine;
                        Show(c == null ? Enumerable.Empty<Row>() : c.Members.Select(m => new Row { Name = m.Name, Crew = c.Name, Grade = Mathf.Max(1, m.Level / 10), Location = m.Online ? (string.IsNullOrEmpty(m.Location) ? "Online" : m.Location) : SocialNet.T("10504", "Offline"), Online = m.Online }));
                        break;
                    }
                default:
                    if (OnlineSession.Active)
                        OnlineSession.Instance.Net.Request("online", null, r =>
                        {
                            if (!list || tab != 2) return;
                            Show((r.L("players") ?? new List<object>()).OfType<Dictionary<string, object>>().Where(o => o.S("name") != PlayerProfile.Current?.Name)
                                .Select(o => new Row { Name = o.S("name"), Grade = Mathf.Max(1, o.I("level") / 10), Location = o.S("channel", "-"), Online = true }));
                        });
                    else
                        Show(SocialStore.AiDrivers.Select(n => { var ai = SocialStore.AiInfo(n); return new Row { Name = n, Grade = ai.grade, Location = $"{SocialUi.CityName(ai.city)} {ai.channel}", Online = true }; }));
                    break;
            }
        }

        static Row FromFriend(Friends.Entry e) => new Row { Name = e.Name, Crew = e.Crew, Grade = Mathf.Max(1, e.Level / 10), Location = e.Online ? e.Location : SocialNet.T("10504", "Offline"), Online = e.Online };

        static void Whisper()
        {
            var r = SelectedRow;
            if (r == null) { UiPopup.Message(SocialNet.T("10467", "No friend selected.")); return; }
            SocialChat.Whisper(r.Name);
        }

        /// <summary>Tests: picks a row of the list.</summary>
        public static void Select(int i) { selected = i; lb?.Select(i, false); }

        // ---------------- 2960000 / 2970000 ----------------
        static List<Mail.Note> Notes => savedBox ? Mail.Saved : Mail.Inbox;
        static int Pages => Math.Max(1, (Notes.Count + Rows - 1) / Rows);
        static IEnumerable<Mail.Note> PageNotes => Notes.Skip(page * Rows).Take(Rows);
        static Mail.Note NoteAt(int row) { int i = page * Rows + row; return i >= 0 && i < Notes.Count ? Notes[i] : null; }
        public static IReadOnlyCollection<int> Ticked => ticked;

        static void FillBox()
        {
            if (!box) return;
            string pre = savedBox ? S : B;
            page = Mathf.Clamp(page, 0, Pages - 1);
            box.SetText(pre + "PAGE_TEXT", $"{page + 1} / {Pages}");
            for (int i = 0; i < Rows; i++)
            {
                var n = NoteAt(i);
                var cb = box.Control(pre + "CB" + Nth[i]);
                if (cb) { cb.gameObject.SetActive(n != null); cb.SetChecked(n != null && ticked.Contains(n.Id)); RowText(cb, n); }
                box.SetVisible($"ID_MYFRIEND_MSGBOX_NEW_{i + 1:00}", !savedBox && n != null && n.Status == "N");
                box.SetVisible($"ID_MYFRIEND_MSGBOX_NEW2_{i + 1:00}", !savedBox && n != null && n.Status == "N" && n.HasItem);
            }
        }

        /// <summary>The row's sender, date and the start of the note beside its check box (the client draws them in code).</summary>
        static void RowText(NuiControlView cb, Mail.Note n)
        {
            var t = cb.transform.Find("row text");
            Text tx;
            if (!t)
            {
                var rt = NuiView.NewRect("row text", cb.transform);
                rt.anchoredPosition = new Vector2(26, 0); rt.sizeDelta = new Vector2(330, ((RectTransform)cb.transform).sizeDelta.y);
                tx = rt.gameObject.AddComponent<Text>();
                tx.font = UiAssets.Font("Trebuchet MS 12", out _, out _); tx.fontSize = 12; tx.alignment = TextAnchor.MiddleLeft;
                tx.color = Color.black; tx.raycastTarget = false; tx.horizontalOverflow = HorizontalWrapMode.Overflow;
            }
            else tx = t.GetComponent<Text>();
            if (!tx) return;
            if (n == null) { tx.text = ""; return; }
            string body = (n.Content ?? "").Replace("\n", " ");
            if (body.Length > 24) body = body.Substring(0, 24) + "...";
            tx.text = $"{n.Sender,-14} {SocialUi.Date(n.SentAt)}  {body}";
        }

        /// <summary>The row area (ID_MYFRIEND_MSGBOX): reads the first ticked note, else the first note of the page.</summary>
        static void OpenTicked()
        {
            var n = Notes.FirstOrDefault(x => ticked.Contains(x.Id)) ?? PageNotes.FirstOrDefault();
            if (n != null) OpenNote(n);
        }

        public static void OpenNote(Mail.Note n)
        {
            var ui = UiManager.Instance;
            if (!ui || n == null) return;
            Hook(ui);
            SocialUi.Close(ref read);
            read = ui.Open(ReadDlg);
            if (!read) return;
            reading = n;
            string head = $"{n.Sender}  {SocialUi.Date(n.SentAt)}\n";
            read.SetText("ID_MYFRIEND_MSGREAD_CONTENT", head + n.Content + (n.Mito > 0 ? $"\n+{n.Mito:N0} Mito" : ""));
            read.SetEnabled("ID_MYFRIEND_MSGREAD_REPLY", n.Type == 0);
            if (n.Status == "N")
            {
                n.Status = "R"; n.ReadAt = SocialUi.NowMs;
                if (SocialNet.Online) SocialNet.Request("note.read", new Dictionary<string, object> { ["id"] = n.Id }, null);
                else { var s = SocialStore.Current.Notes.FirstOrDefault(x => x.Id == n.Id); if (s != null) { s.Status = "R"; s.ReadAt = n.ReadAt; SocialStore.Current.Save(); } }
                FillBox();
            }
        }

        static void DeleteTicked()
        {
            var ids = ticked.ToList();
            if (ids.Count == 0) { UiPopup.Message(SocialNet.T("10537", "No note selected.")); return; }
            ticked.Clear();
            Mail.Delete(ids);
            Later(FillBox);
        }

        /// <summary>SAVE: the ticked inbox notes go to the saved box (note.save; offline the store's status K).</summary>
        static void SaveTicked()
        {
            if (savedBox) return;
            var ids = ticked.Where(id => Mail.Inbox.Any(n => n.Id == id)).ToList();
            if (ids.Count == 0) return;
            void Done() { ticked.Clear(); ChatBox.System(SocialNet.T("11032", "The note has been saved.")); Mail.Load(FillBox); }
            if (SocialNet.Online) { SocialNet.Request("note.save", new Dictionary<string, object> { ["ids"] = ids.Cast<object>().ToList() }, r => { if (r.B("ok")) Done(); else UiPopup.Message(SocialNet.Err(r)); }); return; }
            foreach (var n in SocialStore.Current.Notes.Where(n => ids.Contains(n.Id))) { n.Status = "K"; n.ReadAt = SocialUi.NowMs; }
            SocialStore.Current.Save();
            Done();
        }
    }
}
