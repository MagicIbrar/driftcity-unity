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
    /// Notes, the "Messages" tab of the Community window (SOCIAL.md section 3): the list NOTE 4010000 (inbox / saved box
    /// "(unread/total)", 10 rows with check boxes, sender, "YYYY-MM-DD", the viewable period, NEW, pages, Delete Selection,
    /// Save, New Message), the read panel NOTE 4020000 (content, Reply, Delete, the gift slot) and the send dialog 2940000
    /// (receivers separated by ',', up to 80 characters, 50 Mito per receiver). Online the server keeps the notes;
    /// offline the inbox holds the game's own system notes.
    /// </summary>
    public static class Mail
    {
        public const int ListDlg = 4010000, ReadDlg = 4020000, SendDlg = 2940000, Rows = 10;
        public const int MaxContent = 80, MaxReceivers = 10, CostPerReceiver = 50, SavedMax = 10;
        const long Day = 86400000L, UnreadTtl = 30 * Day, ReadTtl = Day;

        public sealed class Note { public int Id, Type; public string Sender = "", Status = "N", Content = "", ItemName = ""; public bool HasItem; public long Mito, SentAt, ReadAt; }

        public static readonly List<Note> Inbox = new List<Note>();
        public static readonly List<Note> Saved = new List<Note>();
        public static bool SavedTab { get; private set; }
        public static Note Reading { get; private set; }
        public static int Unread => Inbox.Count(n => n.Status == "N");
        static long serverOffset;   // server clock - local clock (viewable period)
        static int page;
        static NuiView list, read, send;
        static UiManager hooked;
        static readonly HashSet<int> checkedIds = new HashSet<int>();

        public static NuiView ListView => list;
        public static NuiView ReadView => read;
        public static NuiView SendView => send;
        static List<Note> Shown => SavedTab ? Saved : Inbox;

        static void Hook()
        {
            var ui = UiManager.Instance;
            if (!ui || hooked == ui) return;
            hooked = ui;
            ui.On("ID_NOTE_MSGBOX", _ => { SavedTab = false; page = 0; checkedIds.Clear(); CloseRead(); Fill(); });
            ui.On("ID_NOTE_SAVE_MSGBOX", _ => { SavedTab = true; page = 0; checkedIds.Clear(); CloseRead(); Fill(); });
            ui.On("ID_NOTE_DELETE", _ => DeleteChecked());
            ui.On("ID_NOTE_SAVE", _ => SaveChecked());
            ui.On("ID_NOTE_SEND", _ => Compose(""));
            ui.On("ID_NOTE_LEFT", _ => Page(page - 10));
            ui.On("ID_NOTE_RIGHT", _ => Page(page + 10));
            ui.On("ID_NOTE_CB_ALL", v =>
            {
                bool on = v.Control("ID_NOTE_CB_ALL").Checked;
                foreach (var n in Shown.Skip(page * Rows).Take(Rows)) if (on) checkedIds.Add(n.Id); else checkedIds.Remove(n.Id);
                Fill();
            });
            for (int i = 0; i < 10; i++)
            {
                int k = i;
                ui.On($"ID_NOTE_PAGE{k:00}", _ => Page(page / 10 * 10 + k));
                ui.On($"ID_NOTE_BTN{k:00}", _ => Open(page * Rows + k));
                ui.On($"ID_NOTE_CB{k:00}ST", v =>
                {
                    int idx = page * Rows + k;
                    if (idx >= Shown.Count) return;
                    if (v.Control($"ID_NOTE_CB{k:00}ST").Checked) checkedIds.Add(Shown[idx].Id); else checkedIds.Remove(Shown[idx].Id);
                });
            }
            ui.On("ID_NOTE_MSGREAD_REPLY", _ => { if (Reading != null && Reading.Type == 0) Compose(Reading.Sender); else if (Reading != null) UiPopup.Message("You cannot reply to a system note."); });
            ui.On("ID_NOTE_MSGREAD_DELETE", _ =>
            {
                var n = Reading;
                if (n == null) return;
                if (n.Mito > 0 || n.HasItem) UiPopup.Show(SocialNet.T("11185", "This note has an attachment. Delete it anyway?"), id => { if (id == UiPopup.Yes) Delete(new[] { n.Id }); }, UiPopup.Yes, UiPopup.No);
                else Delete(new[] { n.Id });
            });
            ui.On("ID_MYFRIEND_MSGSEND_BTN_SEND", _ => SendFromDialog());
            ui.On("ID_MYFRIEND_MSGSEND_BTN_CLOSE", _ => SocialUi.Close(ref send));
        }

        // ---------------- list ----------------
        public static void Show()
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            Hook();
            if (!list)
            {
                list = ui.Open(ListDlg);
                SocialUi.HideStartHidden(list);
                // the check boxes draw their tick with element 2
                foreach (var c in list.Controls) if (c.Data.Type == NuiControlView.Check) SocialUi.FixCheck(c);
            }
            Load();
        }

        public static void Hide() { CloseRead(); SocialUi.Close(ref list); }

        static void CloseRead() { SocialUi.Close(ref read); Reading = null; }

        static void Page(int p)
        {
            int pages = Math.Max(1, (Shown.Count + Rows - 1) / Rows);
            page = Mathf.Clamp(p, 0, pages - 1);
            Fill();
        }

        public static void Load(Action done = null)
        {
            if (SocialNet.Online)
            {
                SocialNet.Request("note.list", null, r =>
                {
                    if (!r.B("ok")) { done?.Invoke(); return; }
                    serverOffset = (long)r.N("now") - SocialUi.NowMs;
                    Inbox.Clear(); Saved.Clear();
                    foreach (var o in (r.L("inbox") ?? new List<object>()).OfType<Dictionary<string, object>>()) Inbox.Add(FromJson(o));
                    foreach (var o in (r.L("saved") ?? new List<object>()).OfType<Dictionary<string, object>>()) Saved.Add(FromJson(o));
                    Fill();
                    done?.Invoke();
                });
                return;
            }
            LoadOffline();
            Fill();
            done?.Invoke();
        }

        /// <summary>Offline: the store's notes, expired ones dropped like the server does.</summary>
        static void LoadOffline()
        {
            var st = SocialStore.Current;
            long now = SocialUi.NowMs;
            if (st.Notes.RemoveAll(n => n.Mito == 0 && ((n.Status == "N" && now - n.SentAt > UnreadTtl) || (n.Status == "R" && now - n.ReadAt > ReadTtl))) > 0) st.Save();
            serverOffset = 0;
            Inbox.Clear(); Saved.Clear();
            foreach (var n in st.Notes)
                (n.Status == "K" ? Saved : Inbox).Add(new Note { Id = n.Id, Type = n.Type, Sender = n.Sender, Status = n.Status, Content = n.Content, Mito = n.Mito, SentAt = n.SentAt, ReadAt = n.ReadAt });
        }

        static Note FromJson(Dictionary<string, object> o) => new Note
        {
            Id = o.I("id"), Type = o.I("type"), Sender = o.S("sender", ""), Status = o.S("status", "N"), Content = o.S("content", ""),
            Mito = (long)o.N("mito"), SentAt = (long)o.N("sentAt"), ReadAt = (long)o.N("readAt"),
            ItemName = o.S("itemName", ""), HasItem = o.O("item") != null,   // gifts / auction goods (src/goods.js)
        };

        /// <summary>FREE_TIME (0x610e10): unread 30 days from sending, read 24 hours from reading, saved "Unlimited" (11082).</summary>
        public static string Period(Note n)
        {
            if (n.Status == "K") return SocialNet.T("11082", "Unlimited");
            long now = SocialUi.NowMs + serverOffset;
            if (n.Status == "N") return $"{Math.Max(0, (n.SentAt + UnreadTtl - now) / Day + 1)} Day(s)";
            long left = n.ReadAt + ReadTtl - now;
            return left >= 3600000 ? $"{left / 3600000} Hour(s)" : $"{Math.Max(1, left / 60000)} Minute(s)";
        }

        static void Fill()
        {
            if (!list) return;
            var shown = Shown;
            list.SetText("ID_NOTE_MSGBOX", $"({Inbox.Count(n => n.Status == "N"):000}/{Inbox.Count:000})");
            list.SetText("ID_NOTE_SAVE_MSGBOX", $"({Saved.Count:000}/{SavedMax:000})");
            list.Control("ID_NOTE_MSGBOX").Selected = !SavedTab;
            list.Control("ID_NOTE_SAVE_MSGBOX").Selected = SavedTab;
            list.SetEnabled("ID_NOTE_SAVE", !SavedTab);
            foreach (var c in list.Controls.ToList())
                if (c.Data.IdName.Contains("EVENT")) c.gameObject.SetActive(false);   // Christmas gift-box marker (attachment text "get_giftbox")
            for (int i = 0; i < Rows; i++)
            {
                int idx = page * Rows + i;
                bool used = idx < shown.Count;
                var n = used ? shown[idx] : null;
                string k = i.ToString("00");
                list.SetVisible($"ID_NOTE_SENDER{k}", used);
                list.SetVisible($"ID_NOTE_SEND_DATE{k}", used);
                list.SetVisible($"ID_NOTE_FREE_TIME{k}", used);
                list.SetVisible($"ID_NOTE_CB{k}ST", used);
                list.SetVisible($"ID_NOTE_NEW_{k}", used && n.Status == "N");
                var row = list.Control($"ID_NOTE_BTN{k}");
                if (row) { row.Interactable = used; row.Selected = used && Reading != null && Reading.Id == n.Id; SocialUi.Highlight(row, row.Selected); }
                if (!used) continue;
                list.SetText($"ID_NOTE_SENDER{k}", n.Sender);
                list.SetText($"ID_NOTE_SEND_DATE{k}", SocialUi.Date(n.SentAt));
                list.SetText($"ID_NOTE_FREE_TIME{k}", (n.Status == "N" ? "           " : "") + Period(n));   // clear of the NEW marker
                var cb = list.Control($"ID_NOTE_CB{k}ST");
                if (cb) cb.SetChecked(checkedIds.Contains(n.Id));
            }
            int first = page / 10 * 10, pages = Math.Max(1, (shown.Count + Rows - 1) / Rows);
            for (int i = 0; i < 10; i++)
            {
                var c = list.Control($"ID_NOTE_PAGE{i:00}");
                if (!c) continue;
                bool on = first + i < pages;
                c.gameObject.SetActive(on);
                if (on) { c.SetText((first + i + 1).ToString("000")); c.Selected = first + i == page; }
            }
        }

        public static IReadOnlyList<string> SendersShown => list ? Enumerable.Range(0, Rows)
            .Select(i => list.Control($"ID_NOTE_SENDER{i:00}")).Where(c => c && c.gameObject.activeSelf)
            .Select(c => c.GetComponentInChildren<UnityEngine.UI.Text>(true)?.text).ToList() : new List<string>();

        // ---------------- read ----------------
        /// <summary>Clicking a row opens the read panel; an unread note is marked read (0x5c8).</summary>
        public static void Open(int index)
        {
            if (index < 0 || index >= Shown.Count) return;
            var n = Shown[index];
            Reading = n;
            var ui = UiManager.Instance;
            if (!ui) return;
            Hook();
            if (!read)
            {
                read = ui.Open(ReadDlg);
                SocialUi.HideStartHidden(read);
                SocialUi.Clickable(read, "ID_NOTE_MSGREAD_MY_SLOT01", () => Take(Reading));
            }
            FillRead();
            if (n.Status == "N")
            {
                n.Status = "R"; n.ReadAt = SocialUi.NowMs + serverOffset;
                if (SocialNet.Online) SocialNet.Request("note.read", new Dictionary<string, object> { ["id"] = n.Id }, null);
                else { var s = SocialStore.Current.Notes.FirstOrDefault(x => x.Id == n.Id); if (s != null) { s.Status = "R"; s.ReadAt = n.ReadAt; SocialStore.Current.Save(); } }
            }
            Fill();
        }

        static void FillRead()
        {
            if (!read || Reading == null) return;
            read.SetText("ID_NOTE_MSGREAD_CONTENT", $"[{Reading.Sender}]  {Reading.Content}");
            // one attachment block per note: slot 1 shows the gift (Mito), slots 2-3 stay empty
            bool gift = Reading.Mito > 0 || Reading.HasItem;
            read.SetVisible("ID_NOTE_MSGREAD_MY_SLOTX01", !gift);
            read.SetVisible("ID_NOTE_MSGREAD_SLOT_UPGRADE01", gift);
            if (gift) read.SetText("ID_NOTE_MSGREAD_SLOT_UPGRADE01", Reading.HasItem ? Reading.ItemName : Reading.Mito >= 10000 ? $"{Reading.Mito / 1000}K" : Reading.Mito.ToString());
            for (int s = 2; s <= 3; s++) { read.SetVisible("ID_NOTE_MSGREAD_MY_SLOTX0" + s, true); read.SetVisible("ID_NOTE_MSGREAD_SLOT_UPGRADE0" + s, false); }
            read.SetEnabled("ID_NOTE_MSGREAD_REPLY", Reading.Type == 0);
        }

        /// <summary>0x5d0: the gift slot hands over the attached Mito (11036).</summary>
        public static void Take(Note n)
        {
            if (n == null || (n.Mito <= 0 && !n.HasItem)) return;
            if (SocialNet.Online)
            {
                SocialNet.Request("note.take", new Dictionary<string, object> { ["id"] = n.Id }, r =>
                {
                    if (!r.B("ok")) { UiPopup.Message(SocialNet.Err(r)); return; }
                    if (r.N("mito") > 0) ChatBox.System($"You received {(long)r.N("mito"):N0} Mito.");
                    if (n.HasItem) { ChatBox.System($"You received {r.S("itemName", n.ItemName)}."); OriginalMenus.RefreshInventoryIfOpen(); GameFlow.Instance?.RespawnCar(); }
                    n.Mito = 0; n.HasItem = false; FillRead(); Fill();
                });
                return;
            }
            var st = SocialStore.Current;
            var s = st.Notes.FirstOrDefault(x => x.Id == n.Id);
            long amount = s != null ? s.Mito : 0;
            if (amount <= 0) return;
            var p = PlayerProfile.Current;
            if (p != null) { p.Mito += amount; p.Save(); GameFlow.Instance?.RefreshHud(); }
            s.Mito = 0; n.Mito = 0;
            st.Save();
            ChatBox.System($"You received {amount:N0} Mito.");
            FillRead(); Fill();
        }

        // ---------------- delete / save ----------------
        static void DeleteChecked()
        {
            var ids = checkedIds.Where(id => Shown.Any(n => n.Id == id)).ToList();
            if (ids.Count == 0) return;
            if (Shown.Any(n => ids.Contains(n.Id) && (n.Mito > 0 || n.HasItem)))
                UiPopup.Show(SocialNet.T("11190", "Some of the selected notes have attachments. Delete them anyway?"), id => { if (id == UiPopup.Yes) Delete(ids); }, UiPopup.Yes, UiPopup.No);
            else Delete(ids);
        }

        public static void Delete(IEnumerable<int> idList)
        {
            var ids = idList.ToList();
            void Done() { foreach (var id in ids) checkedIds.Remove(id); if (Reading != null && ids.Contains(Reading.Id)) CloseRead(); ChatBox.System(SocialNet.T("11031", "The note has been deleted.")); Load(); }
            if (SocialNet.Online) { SocialNet.Request("note.delete", new Dictionary<string, object> { ["ids"] = ids.Cast<object>().ToList() }, r => { if (r.B("ok")) Done(); else UiPopup.Message(SocialNet.Err(r)); }); return; }
            SocialStore.Current.Notes.RemoveAll(n => ids.Contains(n.Id));
            SocialStore.Current.Save();
            Done();
        }

        static void SaveChecked()
        {
            if (SavedTab) return;
            var ids = checkedIds.Where(id => Inbox.Any(n => n.Id == id)).ToList();
            if (ids.Count == 0) return;
            if (Saved.Count + ids.Count > SavedMax) { UiPopup.Message(SocialNet.T("11023", "The saved note box is full.")); return; }
            void Done() { checkedIds.Clear(); ChatBox.System(SocialNet.T("11032", "The note has been saved.")); Load(); }
            if (SocialNet.Online) { SocialNet.Request("note.save", new Dictionary<string, object> { ["ids"] = ids.Cast<object>().ToList() }, r => { if (r.B("ok")) Done(); else UiPopup.Message(SocialNet.Err(r)); }); return; }
            foreach (var n in SocialStore.Current.Notes.Where(n => ids.Contains(n.Id))) { n.Status = "K"; n.ReadAt = SocialUi.NowMs; }
            SocialStore.Current.Save();
            Done();
        }

        // ---------------- send ----------------
        /// <summary>2940000 with the receiver filled in (REPLY, the friend list's New Message, a crew note).</summary>
        public static void Compose(string to)
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            Hook();
            if (!send)
            {
                send = ui.Open(SendDlg);
                SocialUi.HideStartHidden(send);
                var content = send.Control("ID_MYFRIEND_MSGSEND_EDIT_CONTENT");
                if (content && content.Input) { content.Input.characterLimit = MaxContent; content.Input.onValueChanged.AddListener(_ => FillSend()); }
                var rec = send.Control("ID_MYFRIEND_MSGSEND_EDIT_RECIEVER");
                if (rec && rec.Input) rec.Input.onValueChanged.AddListener(_ => FillSend());
            }
            SocialUi.SetInput(send, "ID_MYFRIEND_MSGSEND_EDIT_RECIEVER", to);
            SocialUi.SetInput(send, "ID_MYFRIEND_MSGSEND_EDIT_CONTENT", "");
            send.SetText("ID_MYFRIEND_MSGSEND_TXT_INFO", $"Up to {MaxReceivers} people, separated by ','.");
            send.SetText("ID_MYFRIEND_MSGSEND_TXT_CNT", $"/{MaxContent}");
            for (int s = 1; s <= 3; s++) { send.SetVisible("ID_MSG_SEND_MY_SLOTX0" + s, true); send.SetVisible("ID_MSG_SEND_MY_SLOT0" + s, false); send.SetVisible("ID_MSG_SEND_SLOT_UPGRADE0" + s, false); }
            FillSend();
        }

        static List<string> Receivers(string text) => (text ?? "").Split(',', ';').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

        /// <summary>TEXTLEN shows the content length; MITO the cost, 50 per receiver (0x6704a0).</summary>
        static void FillSend()
        {
            if (!send) return;
            send.SetText("ID_MYFRIEND_MSGSEND_TEXTLEN", send.GetInput("ID_MYFRIEND_MSGSEND_EDIT_CONTENT").Length.ToString());
            send.SetText("ID_MYFRIEND_MSGSEND_MITO", (CostPerReceiver * Math.Max(1, Receivers(send.GetInput("ID_MYFRIEND_MSGSEND_EDIT_RECIEVER")).Count)).ToString());
        }

        static void SendFromDialog()
        {
            if (!send) return;
            string to = send.GetInput("ID_MYFRIEND_MSGSEND_EDIT_RECIEVER"), content = send.GetInput("ID_MYFRIEND_MSGSEND_EDIT_CONTENT");
            Send(to, content, ok => { if (ok) SocialUi.Close(ref send); });
        }

        /// <summary>The client's checks in order (0x6713a0), then note.send; offline other drivers can't be reached.</summary>
        public static void Send(string to, string content, Action<bool> done)
        {
            content = (content ?? "").Trim();
            var names = Receivers(to);
            string error = null;
            if (content.Length == 0) error = SocialNet.T("11010", "Please enter the content of the note.");
            else if (names.Count == 0) error = SocialNet.T("11009", "Please enter the receiver.");
            else if (content.Length > MaxContent) error = $"A note can have up to {MaxContent} characters.";
            else if ((PlayerProfile.Current?.Mito ?? 0) < CostPerReceiver * names.Count) error = SocialNet.T("11013", "You do not have enough Mito.");
            else if (names.GroupBy(x => x.ToLowerInvariant()).Any(g => g.Count() > 1)) error = SocialNet.T("11037", "%s is entered twice.", names.GroupBy(x => x.ToLowerInvariant()).First(g => g.Count() > 1).Key);
            else if (names.Any(x => string.Equals(x, PlayerProfile.Current?.Name, StringComparison.OrdinalIgnoreCase))) error = SocialNet.T("11021", "You cannot send a note to yourself.");
            else if (names.Count > MaxReceivers) error = SocialNet.T("11022", "You can send a note to up to %d people at once.", MaxReceivers);
            if (error != null) { UiPopup.Message(error); done?.Invoke(false); return; }
            if (!SocialNet.Online) { UiPopup.Message("Notes reach other drivers only on the online server."); done?.Invoke(false); return; }
            SocialNet.Request("note.send", new Dictionary<string, object> { ["to"] = names.Cast<object>().ToList(), ["content"] = content }, r =>
            {
                if (!r.B("ok")) { UiPopup.Message(SocialNet.Err(r)); done?.Invoke(false); return; }
                ChatBox.System(SocialNet.T("11033", "The note has been sent.") + $" (-{(long)r.N("cost"):N0} Mito)");
                done?.Invoke(true);
            });
        }

        // ---------------- server pushes ----------------
        public static void OnPush(string t, Dictionary<string, object> m)
        {
            if (t != "note.new") return;
            var n = FromJson(m.O("note"));
            if (Inbox.All(x => x.Id != n.Id)) Inbox.Add(n);
            if (list) Fill();
        }

        /// <summary>At login (online) the inbox is fetched so the main bar can show the new-note notice.</summary>
        public static void Prefetch() => Load();

        public static void ResetAll() { Inbox.Clear(); Saved.Clear(); checkedIds.Clear(); SavedTab = false; page = 0; Hide(); SocialUi.Close(ref send); }
    }
}
