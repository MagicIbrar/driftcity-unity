using System.Collections.Generic;
using System.Linq;
using DriftCity.Game;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Social
{
    /// <summary>
    /// The party (SOCIAL.md section 1): the party bar PARTY 2870000 (name, member count, list toggle F12), the member list
    /// PARTYLIST 2890000 (portrait, "Lv%u", "(V%u)" car grade, P1-P4, kick for the leader, LEAVE), and the dialogs of the
    /// invite flow: 410000 create/invite (target, party name, message), 420000 invite received (10415), 430000 ask to join
    /// a party (10416 + 10417), 440000 a join request (10524), 450000 a refusal. Up to 4 members; the leader is members[0];
    /// bar and list hide with one member or less (0x61c0b0). Online the server owns the party (world.js); offline the
    /// party is made of AI drivers who accept at once.
    /// </summary>
    public static class PartyWindow
    {
        public const int Bar = 2870000, List = 2890000, CreateDlg = 410000, InviteDlg = 420000, RequestDlg = 430000, AcceptDlg = 440000, RefusedDlg = 450000;
        public const int MaxMembers = 4;
        static readonly float[] RowY = { 32, 84, 136, 187 };

        public sealed class Member { public string Name; public int Level, Grade = 1, Avatar = 1; public bool Leader, Me; public string City; public int Channel; public int Combo, Posts; }

        public const int BonusDlg = 3870000;
        /// <summary>globalcontent PartyBonus_Level (US column): members above it add Mito, the others EXP (0x4fe050).</summary>
        public const int PartyBonusLevel = 80;
        /// <summary>Percent per member (10 with the server flag net+0x288 &amp; 0x800, not used by the remake).</summary>
        public const int PartyBonusStep = 5;
        /// <summary>The party combo total (0x23c / 0x242: net+0x3b614), shown on the bar's COMBOCOUNT.</summary>
        public static int ComboTotal { get; private set; }
        /// <summary>A party patrol is running: COMBOCOUNT shows "Post %u" (the members' posts) and the list POST01-04 (0x61bf60).</summary>
        public static bool PatrolShown { get; private set; }
        public static int BonusExp { get; private set; }
        public static int BonusMito { get; private set; }

        public static string Name { get; private set; } = "";
        public static readonly List<Member> Members = new List<Member>();
        public static bool InParty => Members.Count > 1;
        public static bool IAmLeader => Members.Count > 0 && Members[0].Me;
        public static bool ListShown { get; private set; } = true;

        static NuiView bar, list, dialog;
        static readonly List<GameObject> portraits = new List<GameObject>();
        static UiManager hooked;
        static string pendingFrom;   // 420000 / 440000: who is waiting for an answer

        public static NuiView BarView => bar;
        public static NuiView ListView => list;
        public static NuiView Dialog => dialog;
        public static NuiView BonusView => bonusView;
        static NuiView bonusView;

        static string MyName => PlayerProfile.Current?.Name ?? "";

        static void Hook()
        {
            var ui = UiManager.Instance;
            if (!ui || hooked == ui) return;
            hooked = ui;
            ui.On("ID_PARTY_TOGGLELIST", _ => ToggleList());
            ui.On("ID_PARTYLIST_TOGGLELIST", _ => ToggleList());
            ui.On("ID_PARTYLIST_LEAVE", _ => Leave());
            for (int i = 1; i <= MaxMembers; i++) { int k = i - 1; ui.On("ID_PARTYLIST_MEMBERBAN" + i, _ => KickRow(k)); }
            ui.On("ID_PARTY_CREATE_BUTTON", _ => SendInvite());
            ui.On("ID_PARTY_CREATE_CANCEL", _ => SocialUi.Close(ref dialog));
            ui.On("ID_PARTY_INVITE_RECV_ACCEPT", _ => AnswerInvite(true));
            ui.On("ID_PARTY_INVITE_RECV_CANCEL", _ => AnswerInvite(false));
            ui.On("ID_PARTY_REQUEST_SEND_BUTTON", _ => SendRequest());
            ui.On("ID_PARTY_REQUEST_SEND_CANCEL", _ => SocialUi.Close(ref dialog));
            ui.On("ID_PARTY_ACCEPT_ACCEPT", _ => AnswerRequest(true));
            ui.On("ID_PARTY_ACCEPT_CANCEL", _ => AnswerRequest(false));
            ui.On("ID_PARTY_DUPLICATE_OK", _ => SocialUi.Close(ref dialog));
        }

        // ---------------- invite ----------------
        /// <summary>COMMAND 700006 / the friend list's PARTY / "/invite name": opens 410000 for the target.</summary>
        public static void Invite(string target)
        {
            if (string.IsNullOrWhiteSpace(target)) return;
            target = target.Trim();
            if (string.Equals(target, MyName, System.StringComparison.OrdinalIgnoreCase)) return;
            if (InParty && Members.Any(m => string.Equals(m.Name, target, System.StringComparison.OrdinalIgnoreCase))) { Refused(SocialNet.T("10748", "Already in a party.")); return; }
            if (Members.Count >= MaxMembers) { Refused(SocialNet.T("20219", "The party is full.")); return; }
            var ui = UiManager.Instance;
            if (!ui) return;
            Hook();
            SocialUi.Close(ref dialog);
            dialog = ui.Open(CreateDlg);
            SocialUi.CenterOnScreen(dialog);
            if (!dialog) return;
            SocialUi.SetInput(dialog, "ID_PARTY_CREATE_TARGETNAME", target);
            // the name field is the inviter's only while no party exists yet (11266 / 11268)
            SocialUi.SetInput(dialog, "ID_PARTY_CREATE_PARTYNAME", InParty ? Name : SocialNet.T("10395", "Party"));
            SocialUi.SetInput(dialog, "ID_PARTY_CREATE_MESSAGE", SocialNet.T("10396", "Join us!"));
            dialog.SetEnabled("ID_PARTY_CREATE_PARTYNAME", !InParty);
            var pn = dialog.Control("ID_PARTY_CREATE_PARTYNAME");
            if (pn && pn.Input) pn.Input.interactable = !InParty;
            dialog.SetText("ID_PARTY_CREATE_TXT_PARTYNAME", InParty ? SocialNet.T("11268", "Party Name") : SocialNet.T("11266", "Please enter a party name."));
            dialog.SetText("ID_PARTY_CREATE_TXT_MESSAGE", "Message");
        }

        static void SendInvite()
        {
            if (!dialog) return;
            string target = dialog.GetInput("ID_PARTY_CREATE_TARGETNAME").Trim();
            string pname = dialog.GetInput("ID_PARTY_CREATE_PARTYNAME").Trim();
            string msg = dialog.GetInput("ID_PARTY_CREATE_MESSAGE").Trim();
            if (pname.Length == 0) { UiPopup.Message(SocialNet.T("10470", "Enter the name of Party.")); return; }
            SocialUi.Close(ref dialog);
            if (SocialNet.Online)
            {
                SocialNet.Request("party.invite", new Dictionary<string, object> { ["name"] = target, ["party"] = pname, ["message"] = msg }, r =>
                {
                    if (r.B("ok")) { ChatBox.System($"Party invitation sent to {target}."); return; }
                    if (r.B("canRequest")) { OpenRequest(target); return; }
                    Refused(SocialNet.Err(r));
                });
                return;
            }
            OfflineInvite(target, pname);
        }

        /// <summary>Offline: the AI drivers of the offline game accept at once (others aren't there).</summary>
        static void OfflineInvite(string target, string pname)
        {
            var ai = SocialStore.AiName(target);
            if (ai == null) { Refused($"{target} is not online."); return; }
            if (!InParty)
            {
                Members.Clear();
                Name = pname;
                Members.Add(Me(true));
            }
            var info = SocialStore.AiInfo(ai);
            Members.Add(new Member { Name = ai, Level = info.level, Grade = info.grade, Avatar = info.avatar, City = CitySession.Current ? CitySession.Current.City : info.city, Channel = 1 });
            ChatBox.System(SocialNet.T("10826", "%s has joined the party.", ai));
            Refresh();
        }

        static Member Me(bool leader)
        {
            var p = PlayerProfile.Current;
            return new Member { Name = MyName, Level = p?.Level ?? 1, Grade = (p?.Car?.Grade ?? 0) + 1, Avatar = p?.Avatar ?? 1, Leader = leader, Me = true, City = CitySession.Current ? CitySession.Current.City : null, Channel = 1 };
        }

        /// <summary>430000: the target is in another party - ask its leader instead (0xf6).</summary>
        static void OpenRequest(string target)
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            Hook();
            SocialUi.Close(ref dialog);
            dialog = ui.Open(RequestDlg);
            SocialUi.CenterOnScreen(dialog);
            if (!dialog) return;
            pendingFrom = target;
            dialog.SetText("ID_PARTY_REQUEST_SEND_CURRSTATE", SocialNet.T("10416", "%s is already a member of another party.", target));
            dialog.SetText("ID_PARTY_REQUEST_SEND_TARGETMSG", SocialNet.T("10417", "Do you want to form a party with %s?", target));
        }

        static void SendRequest()
        {
            string target = pendingFrom;
            SocialUi.Close(ref dialog);
            SocialNet.Request("party.request", new Dictionary<string, object> { ["name"] = target }, r =>
            {
                if (r.B("ok")) ChatBox.System($"Party request sent to {target}'s party leader.");
                else Refused(SocialNet.Err(r));
            });
        }

        /// <summary>420000: "%s of %s Party has requested for you to join the party." (10415) with the leader's message.</summary>
        public static void ShowInvite(string from, string party, string message)
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            Hook();
            if (dialog && dialog.Dialog.Id == InviteDlg)
            {
                ChatBox.System($"{from} also invited you to a party (answer the open invite first).");
                return;
            }
            SocialUi.Close(ref dialog);
            dialog = ui.Open(InviteDlg);
            SocialUi.CenterOnScreen(dialog);
            if (!dialog) return;
            pendingFrom = from;
            dialog.SetText("ID_PARTY_INVITE_RECV_TEXT", SocialNet.T("10415", "%s of %s Party has requested for you to join the party.", from, party));
            dialog.SetText("ID_PARTY_INVITE_RECV_STATIC_MSG", SocialNet.T("10397", "From the Party Leader"));
            SocialUi.SetInput(dialog, "ID_PARTY_INVITE_RECV_LEADERMSG", message);
            SocialUi.ReadOnly(dialog, "ID_PARTY_INVITE_RECV_LEADERMSG");
            if (GameEffects.Sounds) AudioLibrary.PlaySfx("sfx/SID_CLICK_YES");
        }

        static void AnswerInvite(bool accept)
        {
            SocialUi.Close(ref dialog);
            if (!SocialNet.Online) return;
            SocialNet.Request(accept ? "party.accept" : "party.decline", null, r => { if (!r.B("ok")) UiPopup.Message(SocialNet.Err(r)); });
        }

        /// <summary>440000: "Allow %s to join the Party?" (10524).</summary>
        static void ShowRequest(string from)
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            Hook();
            SocialUi.Close(ref dialog);
            dialog = ui.Open(AcceptDlg);
            SocialUi.CenterOnScreen(dialog);
            if (!dialog) return;
            pendingFrom = from;
            dialog.SetText("ID_PARTY_ACCEPT_TEXT", SocialNet.T("10524", "Allow %s to join the Party?", from));
        }

        static void AnswerRequest(bool accept)
        {
            string from = pendingFrom;
            SocialUi.Close(ref dialog);
            SocialNet.Request("party.answer", new Dictionary<string, object> { ["name"] = from, ["accept"] = accept }, r => { if (!r.B("ok")) UiPopup.Message(SocialNet.Err(r)); });
        }

        /// <summary>450000: the refusal text (10412 / 10413 / 10472 / free text from the refuser).</summary>
        public static void Refused(string text)
        {
            var ui = UiManager.Instance;
            if (!ui) { ChatBox.System(text); return; }
            Hook();
            SocialUi.Close(ref dialog);
            dialog = ui.Open(RefusedDlg);
            SocialUi.CenterOnScreen(dialog);
            if (dialog) dialog.SetText("ID_PARTY_DUPLICATE_TEXT", text);
            else ChatBox.System(text);
        }

        // ---------------- leave / kick ----------------
        public static void Leave()
        {
            if (!InParty) return;
            if (SocialNet.Online) { SocialNet.Request("party.leave", null, null); return; }
            Members.Clear(); Name = "";
            ChatBox.System(SocialNet.T("10827", "You've left the party."));
            Refresh();
        }

        static void KickRow(int row)
        {
            if (!IAmLeader || row <= 0 || row >= Members.Count) return;
            var target = Members[row].Name;
            if (SocialNet.Online) { SocialNet.Request("party.kick", new Dictionary<string, object> { ["name"] = target }, r => { if (!r.B("ok")) UiPopup.Message(SocialNet.Err(r)); }); return; }
            Members.RemoveAt(row);
            ChatBox.System(SocialNet.T("10830", "%s has been kicked from the party.", target));
            if (Members.Count <= 1) { Members.Clear(); Name = ""; ChatBox.System(SocialNet.T("10831", "Party has been disbanned.")); }
            Refresh();
        }

        // ---------------- server pushes ----------------
        public static void OnPush(string t, Dictionary<string, object> m)
        {
            // party play (DriftCity_RE/PARTYPLAY.md): the Undercity run, the party patrol, the party combo
            if (t.StartsWith("party.uc.")) { UndercityParty.OnPush(t, m); return; }
            if (t.StartsWith("party.patrol.")) { PatrolMission.OnPartyPush(t, m); return; }
            switch (t)
            {
                case "party.combo":
                {
                    ComboTotal = m.I("total");
                    var mem = Members.FirstOrDefault(x => x.Name == m.S("name"));
                    if (mem != null) mem.Combo = m.I("combo");
                    Refresh();
                    break;
                }
                case "party.invite": ShowInvite(m.S("from"), m.S("party", "Party"), m.S("message", "")); break;
                case "party.request": ShowRequest(m.S("from")); break;
                case "party.declined": Refused(m.S("message", $"{m.S("from")} has refused your Party invitation.")); break;
                case "party.update": Apply(m.O("party")); break;
                case "party.notice":
                {
                    string n = m.S("name");
                    switch (m.S("type"))
                    {
                        case "joined": ChatBox.System(SocialNet.T("10826", "%s has joined the party.", n)); break;
                        case "left": ChatBox.System(SocialNet.T("10828", "%s has left the party.", n)); break;
                        case "kicked": ChatBox.System(m.B("you") ? SocialNet.T("10829", "You've been kicked from the party.") : SocialNet.T("10830", "%s has been kicked from the party.", n)); break;
                        case "disbanded": ChatBox.System(SocialNet.T("10831", "Party has been disbanned.")); break;
                    }
                    break;
                }
            }
        }

        /// <summary>0xf3: the server's party (null = none).</summary>
        public static void Apply(Dictionary<string, object> party)
        {
            bool was = InParty;
            Members.Clear();
            Name = "";
            if (party != null)
            {
                Name = party.S("name", "Party");
                foreach (var o in (party.L("members") ?? new List<object>()).OfType<Dictionary<string, object>>())
                    Members.Add(new Member
                    {
                        Name = o.S("name"), Level = Mathf.Max(1, o.I("level")), Grade = Mathf.Max(1, o.I("grade", 1)), Avatar = Mathf.Clamp(o.I("avatar", 1), 1, 8),
                        Leader = o.B("leader"), Me = o.S("name") == MyName, City = o.S("city"), Channel = o.I("channel"),
                    });
            }
            if (!was && InParty) ChatBox.System(SocialNet.T("10825", "You've joined a party."));
            ComboTotal = party != null ? party.I("combo") : 0;
            UpdateBonus();
            Refresh();
        }

        // ---------------- party bonus (0x4fe050) ----------------

        /// <summary>
        /// 0x4fe050: only with more than one member; per member at or below PartyBonus_Level +5 % EXP, above it +5 % Mito,
        /// in the HUV game modes (the server applies it to the chase and Undercity rewards). Chat 10998 (both), 10996 (EXP),
        /// 10997 (Mito; its US text says Exp. too) and the PARTYBONUS strip 3870000. Offline there is no party bonus.
        /// </summary>
        public static void UpdateBonus()
        {
            int low = 0, high = 0;
            if (InParty && SocialNet.Online)
                foreach (var m in Members) { if (m.Level > PartyBonusLevel) high++; else low++; }
            int exp = PartyBonusStep * low, mito = PartyBonusStep * high;
            bool changed = exp != BonusExp || mito != BonusMito;
            BonusExp = exp; BonusMito = mito;
            if (changed && (exp > 0 || mito > 0))
            {
                string text = exp > 0 && mito > 0 ? SocialNet.T("10998", "Party Bonus Applied!!! Exp. and Mito are increased by %d%%, %d%% respectively when playing in HUV gamemode.", exp, mito)
                            : exp > 0 ? SocialNet.T("10996", "Party Bonus Applied!!! Exp. is increased by %d%% when playing HUV gamemode.", exp)
                            : SocialNet.T("10997", "Party Bonus Applied!!! Mito is increased by %d%% when playing HUV gamemode.", mito);
                ChatBox.System(text);
            }
            var ui = UiManager.Instance;
            if (!ui) return;
            if (exp == 0 && mito == 0) { SocialUi.Close(ref bonusView); return; }
            if (!bonusView)
            {
                bonusView = ui.Open(BonusDlg);
                // 3870000 has no screen position of its own: the ADV main bar's object places it at runtime (0x5e6670);
                // here it sits at the top of the screen, left of the party bar
                var origin = bonusView ? bonusView.transform.Find("origin") as RectTransform : null;
                if (origin) { origin.anchorMin = origin.anchorMax = new Vector2(1f, 1f); origin.anchoredPosition = new Vector2(-300f - 232f, -3f); }
            }
            if (!bonusView) return;
            bonusView.SetVisible("ID_PARTYBONUS_BASE", true);
            bonusView.SetVisible("ID_PARTYBONUS_DISABLED", false);
            bonusView.SetText("ID_PARTYBONUS_EXP", exp.ToString());
            bonusView.SetText("ID_PARTYBONUS_MITO", mito.ToString());
        }

        // ---------------- party patrol posts (0xc27 -> 0x61bf60) ----------------

        /// <summary>The members' posts during a party patrol (null ends the display).</summary>
        public static void SetPosts(IDictionary<string, int> posts)
        {
            PatrolShown = posts != null;
            foreach (var m in Members) m.Posts = posts != null && posts.TryGetValue(m.Name, out int n) ? n : 0;
            Refresh();
        }

        // ---------------- party combo (0x514350) ----------------
        static DriftCombo combo;
        static float comboFind, comboSend;
        static int comboSent;

        /// <summary>Online in a party: the speed combo's count goes to the server (it sums the party's total).</summary>
        static void TickCombo()
        {
            if (!InParty || !SocialNet.Online) return;
            if (!combo && Time.unscaledTime > comboFind) { comboFind = Time.unscaledTime + 2f; combo = Object.FindAnyObjectByType<DriftCombo>(); }
            if (!combo) return;
            int n = combo.Count;
            if (n == 0) { comboSent = 0; return; }
            if (n == comboSent || Time.unscaledTime < comboSend) return;
            comboSend = Time.unscaledTime + 0.25f;
            comboSent = n;
            SocialNet.Request("party.combo", new Dictionary<string, object> { ["count"] = n }, null);
        }

        /// <summary>Offline: nothing to tick. Online: keeps the bar's own member's level in step.</summary>
        public static void Tick()
        {
            if (!InParty && (bar || list)) Refresh();
            if (!InParty && bonusView) UpdateBonus();
            TickCombo();
        }

        // ---------------- bar and list ----------------
        public static void ToggleList()
        {
            if (!InParty) return;
            ListShown = !ListShown;
            Refresh();
        }

        public static void Refresh()
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            Hook();
            if (!InParty)
            {
                SocialUi.Close(ref bar);
                SocialUi.Close(ref list);
                portraits.Clear();
                return;
            }
            if (!bar) { bar = ui.Open(Bar); SocialUi.HideStartHidden(bar); }
            if (bar)
            {
                foreach (var id in new[] { "ID_PARTY_NAME", "ID_PARTY_MEMBERCOUNT" }) bar.SetVisible(id, true);
                bar.SetText("ID_PARTY_NAME", Name);
                bar.SetText("ID_PARTY_MEMBERCOUNT", Members.Count.ToString());
                // COMBOCOUNT: "Post %u" (the members' posts) in a party patrol, else the party combo total "%u" (online only)
                bool showCount = PatrolShown || SocialNet.Online;
                bar.SetVisible("ID_PARTY_COMBOCOUNT", showCount);
                if (showCount) bar.SetText("ID_PARTY_COMBOCOUNT", PatrolShown ? $"Post {Members.Sum(x => x.Posts)}" : ComboTotal.ToString());
            }
            if (!ListShown) { SocialUi.Close(ref list); portraits.Clear(); return; }
            if (!list) { list = ui.Open(List); SocialUi.HideStartHidden(list); portraits.Clear(); }
            if (!list) return;
            foreach (var go in portraits) if (go) Object.Destroy(go);
            portraits.Clear();
            for (int k = 1; k <= 8; k++) list.SetVisible("ID_PARTYLIST_CHARIMG" + k, false);
            for (int i = 0; i < MaxMembers; i++)
            {
                bool used = i < Members.Count;
                var m = used ? Members[i] : null;
                int n = i + 1;
                list.SetVisible("ID_PARTYLIST_MEMBERNAME" + n, used);
                list.SetVisible("ID_PARTYLIST_MEMBERLEVEL" + n, used);
                list.SetVisible("ID_PARTYLIST_LEVEL" + n, used);
                list.SetVisible("ID_PARTYLIST_P" + n, used);
                list.SetVisible("ID_PARTYLIST_MEMBERBAN" + n, used && IAmLeader && !m.Me);
                // MEMBERPOS / MEMBERCOMBO / OFFLINE stay hidden in this build (0x61c750); POSTnn shows "Post %u" in a party patrol
                foreach (var hidden in new[] { "ID_PARTYLIST_MEMBERPOS" + n, "ID_PARTYLIST_MEMBERCOMBO" + n, "ID_PARTYLIST_OFFLINE" + n })
                    list.SetVisible(hidden, false);
                list.SetVisible($"ID_PARTYLIST_POST{n:00}", used && PatrolShown);
                if (!used) continue;
                if (PatrolShown) list.SetText($"ID_PARTYLIST_POST{n:00}", $"Post {m.Posts}");
                list.SetText("ID_PARTYLIST_MEMBERCOMBO" + n, m.Combo.ToString());   // filled but hidden, as the client does
                list.SetText("ID_PARTYLIST_MEMBERNAME" + n, m.Name);
                list.SetText("ID_PARTYLIST_MEMBERLEVEL" + n, "Lv" + m.Level);
                list.SetText("ID_PARTYLIST_LEVEL" + n, $"(V{m.Grade})");
                // portrait N is CHARIMG_N; the client moves the member's portrait onto the row
                var src = list.Control("ID_PARTYLIST_CHARIMG" + Mathf.Clamp(m.Avatar, 1, 8));
                if (src)
                {
                    var copy = Object.Instantiate(src.gameObject, src.transform.parent);
                    copy.name = "portrait " + n;
                    copy.SetActive(true);
                    var rt = (RectTransform)copy.transform;
                    rt.anchoredPosition = new Vector2(-287, -RowY[i]);
                    copy.transform.SetSiblingIndex(src.transform.GetSiblingIndex());
                    portraits.Add(copy);
                }
            }
            list.SetVisible("ID_PARTYLIST_LEAVE", true);
            // in the Undercity both buttons are disabled (0x61bff5)
            bool uc = UndercityStage.Active;
            list.SetEnabled("ID_PARTYLIST_LEAVE", !uc);
            for (int i = 1; i <= MaxMembers; i++) list.SetEnabled("ID_PARTYLIST_MEMBERBAN" + i, !uc);
        }

        /// <summary>Tests: forget the party and close the windows.</summary>
        public static void ResetAll()
        {
            Members.Clear(); Name = ""; ListShown = true;
            ComboTotal = 0; PatrolShown = false; BonusExp = BonusMito = 0;
            SocialUi.Close(ref dialog);
            SocialUi.Close(ref bonusView);
            UndercityParty.ResetAll();
            Refresh();
        }
    }
}
