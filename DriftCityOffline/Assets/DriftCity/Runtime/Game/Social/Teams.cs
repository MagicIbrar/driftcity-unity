using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DriftCity.Game;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Social
{
    /// <summary>
    /// Crews (the client's TEAM, SOCIAL.md section 4): the "My Crew" page MY 3980000 in the Community window (crew name
    /// and mark, members / points / grade, owned channels, 6 member cards per page with search and sorting, Crew Members /
    /// Manage Crew (rank popup 2790000, kick 10389) / Leave Crew (10378 / 10379) / Crew Message / Crew Mark (3990000)),
    /// the Crew Center 590000 (create / delete a crew, channel battles), Create Crew 2020000 (terms, level 20 and fee,
    /// name check, 10 marks) with its result 2030000, Delete Crew 2040000 and the pending-deletion page 2050000.
    /// Online the server (teams.js) decides; offline the player can found a one-driver crew under the same rules.
    /// </summary>
    public static class Teams
    {
        public const int PageDlg = 3980000, CenterDlg = 590000, MakeDlg = 2020000, MadeDlg = 2030000, DestroyDlg = 2040000, PendingDlg = 2050000, RankDlg = 2790000, MarkDlg = 3990000;
        public const int CreateLevel = 20, CreateFee = 2500, MasterLevel = 20, Marks = 10, Cards = 6;
        public static readonly string[] RankNames = { "", "Crew Master", "1st Class", "2nd Class" };

        public sealed class Member { public string Name = "", RankName = "", Location = ""; public int Level, Rank = 3; public long Joined; public bool Online, Master; }
        public sealed class Crew
        {
            public string Name = "", State = "A", Master = "", Grade = "C";
            public int Mark = 1, Points, Wins, Losses, Captures, MyRank;
            /// <summary>The crew ladder (0x608770): rank by the server's ranking, class S/A/B/C/D/E/R.</summary>
            public int LadderRank;
            public string LadderClass = "-";
            public long Created, CloseAt;
            public bool Renamed;
            public readonly List<Member> Members = new List<Member>();
            public readonly List<string> Channels = new List<string>();
            public readonly List<string> Requests = new List<string>();
        }

        /// <summary>The player's crew (null = none), kept from the last crew.info / offline store.</summary>
        public static Crew Mine { get; private set; }
        public static bool HasCrew => Mine != null;
        /// <summary>The Crew Center (590000) is up: the Driver Dome holds the car while it is.</summary>
        public static bool CenterOpen => center;
        /// <summary>Esc: the top Crew Center dialog closes (Create Crew first, then the center).</summary>
        public static void CloseTop() { if (make) SocialUi.Close(ref make); else SocialUi.Close(ref center); }
        public static bool IAmMaster => Mine != null && Mine.MyRank == 1;

        static NuiView page, center, make, made, destroy, rank, markDlg;
        static UiManager hooked;
        static int cardPage;
        static bool manage, sortByName, sortDesc;
        static string search = "";
        static int makeMark, newMark;
        static string checkedName;
        static string rankTarget;

        public static NuiView PageView => page;
        public static NuiView CenterView => center;
        public static NuiView MakeView => make;
        public static NuiView MadeView => made;
        public static NuiView DestroyView => destroy;

        static PlayerProfile P => PlayerProfile.Current;

        static void Hook()
        {
            var ui = UiManager.Instance;
            if (!ui || hooked == ui) return;
            hooked = ui;
            // My Crew page
            ui.On("ID_MY_TEAM_TAB_MEMBER", _ => { manage = false; FillPage(); });
            ui.On("ID_MY_TEAM_TAB_MEMBERMANAGE", _ => { if (!IAmMaster) { UiPopup.Message(SocialNet.T("10383", "Crew Master only.")); return; } manage = true; FillPage(); });
            ui.On("ID_MY_TEAM_TAB_WITHDRAW", _ => AskLeave());
            ui.On("ID_MY_TEAM_TAB_BOARD", _ => UiPopup.Message("The crew bulletin board was a web page of the original service; it isn't part of Drift City Offline."));
            ui.On("ID_MY_TEAM_TAB_SENDNOTE", _ => { if (Mine != null) Mail.Compose(string.Join(", ", Mine.Members.Where(m => m.Name != P?.Name).Take(Mail.MaxReceivers).Select(m => m.Name))); });
            ui.On("ID_MY_TEAM_TAB_TEAMMARK", _ => OpenMark());
            ui.On("ID_MY_TEAM_MEMBER_SEARCH", v => { search = v.GetInput("ID_MY_TEAM_MEMBER_SEARCHTEXT").Trim(); cardPage = 0; FillPage(); });
            foreach (var id in new[] { "ID_MY_TEAM_MEMBER_FILTER_TEAMPOS", "ID_MY_TEAM_MEMBER_FILTER_TEAMPOS2" }) ui.On(id, _ => { if (sortByName) sortDesc = false; else sortDesc = !sortDesc; sortByName = false; FillPage(); });
            foreach (var id in new[] { "ID_MY_TEAM_MEMBER_FILTER_NAME", "ID_MY_TEAM_MEMBER_FILTER_NAME2" }) ui.On(id, _ => { if (!sortByName) sortDesc = false; else sortDesc = !sortDesc; sortByName = true; FillPage(); });
            ui.On("ID_MY_TEAM_LEFT", _ => { cardPage = Math.Max(0, cardPage - 10); FillPage(); });
            ui.On("ID_MY_TEAM_RIGHT", _ => { cardPage += 10; FillPage(); });
            for (int i = 0; i < 10; i++) { int k = i; ui.On($"ID_MY_TEAM_PAGE{k:00}", _ => { cardPage = cardPage / 10 * 10 + k; FillPage(); }); }
            for (int i = 0; i < Cards; i++)
            {
                int k = i;
                ui.On($"ID_MY_TEAM_MEMBER_SLOT{k}_DETAILVIEW", _ => Detail(k));
                ui.On($"ID_MY_TEAM_MEMBER_SLOT{k}_UPDATE", _ => OpenRank(k));
                ui.On($"ID_MY_TEAM_MEMBER_SLOT{k}_BAN", _ => AskKick(k));
            }
            // Crew Center
            ui.On("ID_TC_MAIN_ESC", _ => SocialUi.Close(ref center));
            ui.On("ID_TC_MAIN_TEAMMAKEDESTROY", _ => MakeOrDestroy());
            ui.On("ID_TC_MAIN_NOTICECHALLENGE", _ => { SocialUi.Close(ref center); ChannelBattle.OpenChannels(); });
            ui.On("ID_TC_MAIN_VIEWING", _ => { SocialUi.Close(ref center); ChannelBattle.OpenSpectate(); });
            // Create Crew
            ui.On("ID_TC_TEAMMAKE_ESC", _ => SocialUi.Close(ref make));
            ui.On("ID_TC_TEAMMAKE_CANCEL", _ => SocialUi.Close(ref make));
            ui.On("ID_TC_TEAMMAKE_NAMECHECK", _ => CheckName());
            ui.On("ID_TC_TEAMMAKE_MAKE", _ => Make());
            for (int i = 1; i <= Marks; i++) { int k = i; ui.On("ID_TC_TEAMMAKE_MARK" + k, _ => { makeMark = k; FillMake(); }); }
            ui.On("ID_TC_TEAMOK_OK", _ => { SocialUi.Close(ref made); Community.Open(Community.Tab.MyTeam); });
            // Delete Crew / pending deletion
            ui.On("ID_TC_TEAMDESTROY_ESC", _ => SocialUi.Close(ref destroy));
            ui.On("ID_TC_TEAMDESTROY_CANCEL", _ => SocialUi.Close(ref destroy));
            ui.On("ID_TC_TEAMDESTROY_OK", v =>
            {
                if (!v.Control("ID_TC_TEAMDESTROY_AGREEMENTCHECK").Checked) { UiPopup.Message(SocialNet.T("10330", "Please agree to the terms.")); return; }
                UiPopup.Show(SocialNet.T("10336", "Do you really want to delete your Crew?"), id => { if (id == UiPopup.Yes) Disband(false); }, UiPopup.Yes, UiPopup.No);
            });
            ui.On("ID_TC_TEAMDESTROYCANCEL_ESC", _ => SocialUi.Close(ref destroy));
            ui.On("ID_TC_TEAMDESTROYCANCEL_CANCEL", _ => SocialUi.Close(ref destroy));
            ui.On("ID_TC_TEAMDESTROYCANCEL_OK", _ => UiPopup.Show(SocialNet.T("10337", "Cancel the request to delete your Crew?"), id => { if (id == UiPopup.Yes) Disband(true); }, UiPopup.Yes, UiPopup.No));
            // rank popup
            ui.On("ID_MYTEAM_MEMBERPOPUP_ESC", _ => SocialUi.Close(ref rank));
            for (int i = 1; i <= 3; i++) { int k = i; ui.On("ID_MYTEAM_MEMBERPOPUP_TEAMPOS" + k, v => { for (int j = 1; j <= 3; j++) v.Control("ID_MYTEAM_MEMBERPOPUP_TEAMPOS" + j)?.SetChecked(j == k); }); }
            ui.On("ID_MYTEAM_MEMBERPOPUP_OK", v =>
            {
                int pick = Enumerable.Range(1, 3).FirstOrDefault(j => v.Control("ID_MYTEAM_MEMBERPOPUP_TEAMPOS" + j)?.Checked == true);
                string target = rankTarget;
                SocialUi.Close(ref rank);
                // TEAMPOS1 = 1st Class, TEAMPOS2 = 2nd Class, TEAMPOS3 = make Master (0x606500)
                if (pick > 0) SetRank(target, pick == 1 ? 2 : pick == 2 ? 3 : 1);
            });
            // crew mark
            // LOADFILE: a picture (CrewMarkUpload: the image import, or crewmark.png in the save folder) becomes the custom
            // mark (0x6107c0, 32x32, 4000 bytes); without one it steps through the 10 built-in marks
            ui.On("ID_MYTEAM_MARK_LOADFILE", _ =>
            {
                if (CrewMarkUpload.ImportHook != null || CrewMarkUpload.Pick() != null) CrewMarkUpload.Upload(ok => { FillMark(); FillPage(); });
                else { newMark = newMark % Marks + 1; FillMark(); }
            });
            ui.On("ID_MYTEAM_MARK_ESC", _ => SocialUi.Close(ref markDlg));
            ui.On("ID_MYTEAM_MARK_OK", _ => { int m = newMark; SocialUi.Close(ref markDlg); SetMark(m); });
        }

        // ---------------- data ----------------
        public static void Load(Action done = null)
        {
            if (SocialNet.Online)
            {
                SocialNet.Request("crew.info", null, r =>
                {
                    if (r.B("ok")) Mine = FromJson(r.O("crew"));
                    FillPage();
                    done?.Invoke();
                });
                return;
            }
            Mine = Offline();
            FillPage();
            done?.Invoke();
        }

        static Crew FromJson(Dictionary<string, object> o)
        {
            if (o == null) return null;
            var c = new Crew
            {
                Name = o.S("name", ""), Mark = o.I("mark", 1), Created = (long)o.N("created"), State = o.S("state", "A"), CloseAt = (long)o.N("closeAt"),
                Master = o.S("master", ""), Points = o.I("points"), Wins = o.I("wins"), Losses = o.I("losses"), Captures = o.I("captures"),
                Grade = o.S("grade", "C"), Renamed = o.B("renamed"), MyRank = o.I("myRank"), LadderRank = o.I("ladderRank"), LadderClass = o.S("ladderClass", "-"),
            };
            foreach (var m in (o.L("members") ?? new List<object>()).OfType<Dictionary<string, object>>())
                c.Members.Add(new Member { Name = m.S("name"), Level = m.I("level"), Rank = m.I("rank", 3), RankName = m.S("rankName", ""), Joined = (long)m.N("joined"), Online = m.B("online"), Master = m.B("master"), Location = m.O("location")?.S("text") ?? "" });
            foreach (var ch in o.L("channels") ?? new List<object>()) c.Channels.Add(ch as string);
            foreach (var rq in o.L("requests") ?? new List<object>()) c.Requests.Add(rq as string);
            return c;
        }

        /// <summary>Offline: the store's crew with the player as its only member (deleted once its 7 days are up).</summary>
        static Crew Offline()
        {
            var st = SocialStore.Current;
            var sc = st.MyCrew;
            if (sc == null || string.IsNullOrEmpty(sc.Name)) return null;
            if (sc.State == "C" && SocialUi.NowMs >= sc.CloseAt) { st.MyCrew = null; st.Save(); ChatBox.System(SocialNet.T("10816", "%s Crew has been deleted.", sc.Name)); return null; }
            var c = new Crew { Name = sc.Name, Mark = sc.Mark, Created = sc.Created, State = sc.State, CloseAt = sc.CloseAt, Points = sc.Points, Renamed = sc.Renamed, MyRank = 1, Master = P?.Name ?? "" };
            c.Grade = Grade(c.Points, 0);
            c.Members.Add(new Member { Name = P?.Name ?? "", Level = P?.Level ?? 1, Rank = 1, RankName = RankNames[1], Joined = sc.Created, Online = true, Master = true });
            return c;
        }

        /// <summary>Crew grade (0x514530): S with a channel capture or 500000 points, A 100000, B 20000, else C.</summary>
        public static string Grade(int points, int captures) => captures > 0 || points >= 500000 ? "S" : points >= 100000 ? "A" : points >= 20000 ? "B" : "C";

        public static string CrewName => Mine?.Name ?? "";

        // ---------------- My Crew page ----------------
        public static void ShowPage()
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            Hook();
            if (!page) { page = ui.Open(PageDlg); SocialUi.HideStartHidden(page); }
            manage = false; cardPage = 0;
            Load();
        }

        public static void HidePage() => SocialUi.Close(ref page);

        static List<Member> Filtered()
        {
            if (Mine == null) return new List<Member>();
            IEnumerable<Member> q = Mine.Members.Where(m => search.Length == 0 || m.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
            q = sortByName ? (sortDesc ? q.OrderByDescending(m => m.Name, StringComparer.OrdinalIgnoreCase) : q.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
                           : (sortDesc ? q.OrderByDescending(m => m.Rank).ThenBy(m => m.Name) : q.OrderBy(m => m.Rank).ThenBy(m => m.Name));
            return q.ToList();
        }

        static Member CardMember(int k) { var f = Filtered(); int i = cardPage * Cards + k; return i < f.Count ? f[i] : null; }

        public static void FillPage()
        {
            if (!page) return;
            var c = Mine;
            page.SetText("ID_MY_TEAM_TEAMNAME", c?.Name ?? "");
            SocialUi.ShowMark(page, "ID_MY_TEAM_MARK_POS", c?.Mark ?? 0);
            if (c != null) CrewMarkUpload.Show(page, "ID_MY_TEAM_MARK_POS", c.Name);
            page.SetText("ID_MY_TEAM_MEMBERCOUNT", c != null ? c.Members.Count.ToString() : "0");
            page.SetText("ID_MY_TEAM_TEAMPOINT", c != null ? c.Points.ToString("N0") : "0");
            page.SetText("ID_MY_TEAM_TEAMRECORD", c?.Grade ?? "-");
            page.SetVisible("ID_MY_TEAM_TEAMURL", false);
            // the crew ladder (0x608770): class letter and rank ("--" without a rank)
            page.SetText("ID_MY_TEAM_TEAM_LEATHER_CLASS", c != null ? c.LadderClass : "-");
            page.SetText("ID_MY_TEAM_LEATHER_RANK", c != null && c.LadderRank > 0 ? c.LadderRank.ToString() : "--");
            // owned channels: the 24 cells are cities x channels 1..6 in the remake (the original had 10 major + 10 semi)
            for (int i = 0; i < 24; i++) page.SetVisible($"ID_MY_TEAM_OWNCHANNEL{i:00}", c != null && c.Channels.Any(ch => ChannelCell(ch) == i));
            page.SetEnabled("ID_MY_TEAM_TAB_MEMBERMANAGE", IAmMaster);
            page.Control("ID_MY_TEAM_TAB_MEMBER").Selected = !manage;
            page.Control("ID_MY_TEAM_TAB_MEMBERMANAGE").Selected = manage;
            page.SetVisible("ID_MY_TEAM_MEMBER_FILTER_TEAMPOS", !sortByName && !sortDesc || sortByName);
            page.SetVisible("ID_MY_TEAM_MEMBER_FILTER_TEAMPOS2", !sortByName && sortDesc);
            page.SetVisible("ID_MY_TEAM_MEMBER_FILTER_NAME", !sortByName || !sortDesc);
            page.SetVisible("ID_MY_TEAM_MEMBER_FILTER_NAME2", sortByName && sortDesc);
            var list = Filtered();
            int pages = Math.Max(1, (list.Count + Cards - 1) / Cards);
            cardPage = Mathf.Clamp(cardPage, 0, pages - 1);
            for (int k = 0; k < Cards; k++)
            {
                var m = CardMember(k);
                bool used = m != null;
                string s = $"ID_MY_TEAM_MEMBER_SLOT{k}_";
                foreach (var f in new[] { "LEVEL", "NAME", "TEAMPOS", "DATE", "BG" }) page.SetVisible(s + f, used);
                page.SetVisible(s + "OFFLINE", used && !m.Online);
                page.SetVisible(s + "DETAILVIEW", used && !manage);
                page.SetVisible(s + "UPDATE", used && manage && !m.Master);
                page.SetVisible(s + "BAN", used && manage && !m.Master);
                if (!used) continue;
                page.SetText(s + "LEVEL", m.Level.ToString());
                page.SetText(s + "NAME", m.Name);
                page.SetText(s + "TEAMPOS", m.RankName.Length > 0 ? m.RankName : RankNames[Mathf.Clamp(m.Rank, 1, 3)]);
                page.SetText(s + "DATE", m.Joined > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(m.Joined).ToLocalTime().ToString("yy.MM.dd") : "");
            }
            int first = cardPage / 10 * 10;
            for (int i = 0; i < 10; i++)
            {
                var b = page.Control($"ID_MY_TEAM_PAGE{i:00}");
                if (!b) continue;
                bool on = first + i < pages;
                b.gameObject.SetActive(on);
                if (on) { b.SetText((first + i + 1).ToString("000")); b.Selected = first + i == cardPage; }
            }
        }

        /// <summary>"cras-2" -> cell 1 (cities in CITIES order, 6 channels each).</summary>
        public static int ChannelCell(string channel)
        {
            if (string.IsNullOrEmpty(channel)) return -1;
            var parts = channel.Split('-');
            int city = Array.IndexOf(SocialUi.Cities, parts[0]);
            int n = parts.Length > 1 && int.TryParse(parts[1], out var x) ? x : 1;
            return city < 0 || n < 1 || n > 6 ? -1 : city * 6 + n - 1;
        }

        static void Detail(int k)
        {
            var m = CardMember(k);
            if (m == null) return;
            if (!m.Online) { UiPopup.Message(SocialNet.T("10725", "%s is offline.", m.Name)); return; }
            UiPopup.Message($"{m.Name}  Lv {m.Level}\n{(m.RankName.Length > 0 ? m.RankName : RankNames[Mathf.Clamp(m.Rank, 1, 3)])}\n{m.Location}");
        }

        static void AskLeave()
        {
            if (Mine == null) return;
            if (IAmMaster) { UiPopup.Message(SocialNet.T("10378", "The Crew Master cannot leave the Crew.")); return; }
            UiPopup.Show(SocialNet.T("10379", "Do you want to leave the Crew?"), id =>
            {
                if (id != UiPopup.Yes) return;
                SocialNet.Request("crew.leave", null, r =>
                {
                    if (!r.B("ok")) { UiPopup.Message(SocialNet.Err(r)); return; }
                    ChatBox.System(SocialNet.T("10018", "You have left %s's crew.", Mine?.Name ?? ""));
                    Mine = null;
                    Community.Open(Community.Tab.MyFriend);
                });
            }, UiPopup.Yes, UiPopup.No);
        }

        static void AskKick(int k)
        {
            var m = CardMember(k);
            if (m == null || !IAmMaster) return;
            if (m.Name == P?.Name) { UiPopup.Message(SocialNet.T("10388", "You cannot kick yourself.")); return; }
            UiPopup.Show(SocialNet.T("10389", "Are you sure you want to kick %s / from the Crew?", m.Name).Replace(" / ", " "), id =>
            {
                if (id == UiPopup.Yes) SocialNet.Request("crew.kick", new Dictionary<string, object> { ["name"] = m.Name }, r => { if (!r.B("ok")) UiPopup.Message(SocialNet.Err(r)); else Load(); });
            }, UiPopup.Yes, UiPopup.No);
        }

        /// <summary>2790000: "Your current Rank" + the member's rank; TEAMPOS1-3 pick 1st Class / 2nd Class / Master.</summary>
        static void OpenRank(int k)
        {
            var m = CardMember(k);
            var ui = UiManager.Instance;
            if (m == null || !ui || !IAmMaster) return;
            SocialUi.Close(ref rank);
            rank = ui.Open(RankDlg);
            if (!rank) return;
            rankTarget = m.Name;
            rank.SetText("ID_MYTEAM_MEMBERPOPUP_DESC1", $"{m.Name}'s current Rank");
            rank.SetText("ID_MYTEAM_MEMBERPOPUP_DESC2", RankNames[Mathf.Clamp(m.Rank, 1, 3)]);
            for (int j = 1; j <= 3; j++) { var cb = rank.Control("ID_MYTEAM_MEMBERPOPUP_TEAMPOS" + j); if (cb) { cb.SetChecked(false); SocialUi.FixCheck(cb); } }
        }

        public static void SetRank(string name, int r)
        {
            SocialNet.Request("crew.rank", new Dictionary<string, object> { ["name"] = name, ["rank"] = r }, res =>
            {
                if (!res.B("ok")) { UiPopup.Message(SocialNet.Err(res)); return; }
                Mine = FromJson(res.O("crew")) ?? Mine;
                FillPage();
            });
        }

        // ---------------- crew mark ----------------
        static void OpenMark()
        {
            var ui = UiManager.Instance;
            if (Mine == null || !ui) return;
            if (!IAmMaster) { UiPopup.Message(SocialNet.T("10383", "Crew Master only.")); return; }
            SocialUi.Close(ref markDlg);
            markDlg = ui.Open(MarkDlg);
            newMark = Mine.Mark;
            FillMark();
        }

        static void FillMark()
        {
            if (!markDlg || Mine == null) return;
            // the original uploads a 32x32 BMP; the remake picks one of the 10 built-in marks (LOADFILE = next mark)
            markDlg.SetText("ID_MYTEAM_MARK_FILENAME", $"Crew mark {newMark} of {Marks}");
            SocialUi.ShowMark(markDlg, "ID_MYTEAM_MARK_BEFORE", Mine.Mark);
            CrewMarkUpload.Show(markDlg, "ID_MYTEAM_MARK_BEFORE", Mine.Name);
            SocialUi.ShowMark(markDlg, "ID_MYTEAM_MARK_AFTER", newMark);
        }

        public static void SetMark(int mark)
        {
            if (!SocialNet.Online)
            {
                var st = SocialStore.Current;
                if (st.MyCrew != null) { st.MyCrew.Mark = Mathf.Clamp(mark, 1, Marks); st.Save(); }
                Load();
                return;
            }
            SocialNet.Request("crew.mark", new Dictionary<string, object> { ["mark"] = mark }, r => { if (!r.B("ok")) UiPopup.Message(SocialNet.Err(r)); else { Mine = FromJson(r.O("crew")) ?? Mine; FillPage(); } });
        }

        // ---------------- Crew Center ----------------
        /// <summary>The Crew Center menu (Operator Erina's TC 590000): create / delete, channel battles, spectating.</summary>
        public static void OpenCenter()
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            Hook();
            SocialUi.Close(ref center);
            center = ui.Open(CenterDlg);
            if (!center) return;
            ChannelBattle.FillCenter(center);   // MAIN TEXT: the schedule (10319 / 10320, 0x675ea0)
            Load(() => { if (center) center.SetEnabled("ID_TC_MAIN_TEAMMAKEDESTROY", Mine == null || IAmMaster); });
        }

        static void MakeOrDestroy()
        {
            if (Mine == null) { SocialUi.Close(ref center); OpenMake(); return; }
            if (!IAmMaster) { UiPopup.Message(SocialNet.T("10383", "Crew Master only.")); return; }
            SocialUi.Close(ref center);
            OpenDestroy();
        }

        // ---------------- Create Crew ----------------
        public static void OpenMake()
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            Hook();
            SocialUi.Close(ref make);
            make = ui.Open(MakeDlg);
            if (!make) return;
            makeMark = 0; checkedName = null;
            // the URL row belongs to the Korean service only (0x6756e0)
            foreach (var id in new[] { "ID_TC_TEAMMAKE_STATIC_TEAMURL", "ID_TC_TEAMMAKE_URL", "ID_TC_TEAMMAKE_URLCHECK", "ID_TC_TEAMMAKE_URL_PREFIX" }) make.SetVisible(id, false);
            make.SetVisible("ID_TC_TEAMMAKE_COVER", false);
            AgreementText(make, "ID_TC_TEAMMAKE_AGREEMENT", "aggrement_teammake.txt");
            make.SetText("ID_TC_TEAMMAKE_STATIC_AGREE", "I agree to the terms.");
            make.SetText("ID_TC_TEAMMAKE_LIMIT_MITO", SocialNet.T("10313", "To start a new Crew, a fee of %d Mito is required.", CreateFee));
            make.SetText("ID_TC_TEAMMAKE_LIMIT_LEVEL", SocialNet.T("10314", "To start a new Crew, the Crew Master's level should be level %u or higher.", CreateLevel));
            make.SetText("ID_TC_TEAMMAKE_STATIC_TEAMMAKE", SocialNet.T("10311", "Enter the desired Crew name, then click the \"Check Availability\" button."));
            make.SetText("ID_TC_TEAMMAKE_STATIC_TEAMMARK", SocialNet.T("10312", "Select a Crew Mark.  You can change your Crew Mark by clicking on the \"My Crew\" button."));
            make.Control("ID_TC_TEAMMAKE_AGREEMENTCHECK")?.SetChecked(false);
            // the check boxes draw their mark with element 2 (state 1 checked, state 6 empty); the marks are in the background
            foreach (var c in make.Controls) if (c.Data.Type == NuiControlView.Check) SocialUi.FixCheck(c);
            FillMake();
        }

        static void FillMake()
        {
            if (!make) return;
            var p = P;
            long mito = p?.Mito ?? 0; int level = p?.Level ?? 1;
            make.SetText("ID_TC_TEAMMAKE_MYMITO", $"My Mito : {mito:N0}  Mito");
            make.SetText("ID_TC_TEAMMAKE_MYLEVEL", $"My level : {level}");
            make.SetText("ID_TC_TEAMMAKE_MITOCOND", mito >= CreateFee ? SocialNet.T("10624", "(OK)") : SocialNet.T("10625", "(Impossible)"));
            make.SetText("ID_TC_TEAMMAKE_LEVELCOND", level >= CreateLevel ? SocialNet.T("10624", "(OK)") : SocialNet.T("10625", "(Impossible)"));
            for (int i = 1; i <= Marks; i++) make.Control("ID_TC_TEAMMAKE_MARK" + i)?.SetChecked(i == makeMark);
        }

        /// <summary>4-12 letters, digits, - or _ (10602 / 10603), then the server's availability check (10328 / 10329).</summary>
        static bool ValidName(string name, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(name)) error = SocialNet.T("10601", "Enter the desired Crew name.");
            else if (name.Length < 4 || name.Length > 12) error = SocialNet.T("10602", "Use 4 to 12 characters.");
            else if (!name.All(ch => ch < 128 && (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_'))) error = SocialNet.T("10603", "Use letters, digits, - or _ only.");
            else if (NameFilter.CheckCrewName(name) is string bad) error = NameFilter.Text(bad);   // the word filter (10607)
            return error == null;
        }

        static void CheckName()
        {
            if (!make) return;
            string name = make.GetInput("ID_TC_TEAMMAKE_NAME").Trim();
            if (!ValidName(name, out var err)) { UiPopup.Message(err); return; }
            if (!SocialNet.Online) { checkedName = name; UiPopup.Message(SocialNet.T("10328", "Available Crew name.")); return; }
            SocialNet.Request("crew.check", new Dictionary<string, object> { ["name"] = name }, r =>
            {
                if (r.B("ok")) { checkedName = name; UiPopup.Message(SocialNet.T("10328", "Available Crew name.")); }
                else { checkedName = null; UiPopup.Message(SocialNet.T("10329", "Unavailable Crew name.")); }
            });
        }

        /// <summary>MAKE (0x6753f0): agreed (10330) -> name checked (10331) -> mark chosen (10332), then the server's rules.</summary>
        static void Make()
        {
            if (!make) return;
            string name = make.GetInput("ID_TC_TEAMMAKE_NAME").Trim();
            if (!make.Control("ID_TC_TEAMMAKE_AGREEMENTCHECK").Checked) { UiPopup.Message(SocialNet.T("10330", "Please agree to the Crew Terms of Use.")); return; }
            if (checkedName == null || checkedName != name) { UiPopup.Message(SocialNet.T("10331", "Please check the availability of your desired Crew name.")); return; }
            if (makeMark < 1) { UiPopup.Message(SocialNet.T("10332", "Select a Crew Mark.")); return; }
            Create(name, makeMark, ok => { if (ok) { SocialUi.Close(ref make); OpenMade(); } });
        }

        public static void Create(string name, int mark, Action<bool> done)
        {
            if (SocialNet.Online)
            {
                SocialNet.Request("crew.create", new Dictionary<string, object> { ["name"] = name, ["mark"] = mark }, r =>
                {
                    if (!r.B("ok")) { UiPopup.Message(SocialNet.Err(r)); done?.Invoke(false); return; }
                    Mine = FromJson(r.O("crew"));
                    ChatBox.System(SocialNet.T("10813", "You've created %s Crew.", name));
                    done?.Invoke(true);
                });
                return;
            }
            var p = P;
            string error = null;
            if (Mine != null) error = "You are already in a Crew.";
            else if (p == null || p.Level < CreateLevel) error = SocialNet.T("10314", "To start a new Crew, the Crew Master's level should be level %u or higher.", CreateLevel);
            else if (p.Mito < CreateFee) error = SocialNet.T("10313", "To start a new Crew, a fee of %d Mito is required.", CreateFee);
            else if (!ValidName(name, out var e)) error = e;
            if (error != null) { UiPopup.Message(error); done?.Invoke(false); return; }
            p.Mito -= CreateFee;
            p.Save();
            GameFlow.Instance?.RefreshHud();
            var st = SocialStore.Current;
            st.MyCrew = new SocialStore.Crew { Name = name, Mark = mark, Created = SocialUi.NowMs };
            st.Save();
            Mine = Offline();
            ChatBox.System(SocialNet.T("10813", "You've created %s Crew.", name));
            done?.Invoke(true);
        }

        /// <summary>2030000: 10333-10335 with the master, crew name, grade and today's date.</summary>
        static void OpenMade()
        {
            var ui = UiManager.Instance;
            if (!ui || Mine == null) return;
            SocialUi.Close(ref made);
            made = ui.Open(MadeDlg);
            if (!made) return;
            made.SetText("ID_TC_TEAMOK_NOTICE1", SocialNet.T("10333", "%s's %s Crew has been", P?.Name ?? "", Mine.Name));
            made.SetText("ID_TC_TEAMOK_NOTICE2", SocialNet.T("10334", "successfully registered"));
            made.SetText("ID_TC_TEAMOK_NOTICE3", SocialNet.T("10335", "with the OMD government."));
            made.SetText("ID_TC_TEAMOK_TEAMMASTER", "Crew Master : " + (P?.Name ?? ""));
            made.SetText("ID_TC_TEAMOK_TEAMNAME", "Crew Name : " + Mine.Name);
            made.SetText("ID_TC_TEAMOK_TEAMLEVEL", "Crew Rank : " + Mine.Grade);
            made.SetText("ID_TC_TEAMOK_TEAMDATE", DateTime.Now.ToString("yyyy.MM.dd"));
        }

        static void AgreementText(NuiView v, string id, string file)
        {
            var c = v.Control(id);
            if (!c) return;
            string text = "";
            try { var fs = DriftCityData.Files; if (fs != null && fs.TryRead(file, out var d)) text = System.Text.Encoding.UTF8.GetString(d); }
            catch (Exception ex) { Debug.LogWarning("crew terms: " + ex.Message); }
            var sz = ((RectTransform)c.transform).sizeDelta;
            var t = MissionUi.Label(c.transform, new Vector2(8, -6), new Vector2(sz.x - 16, sz.y - 12), 12, new Color(0.15f, 0.12f, 0.08f));
            t.verticalOverflow = VerticalWrapMode.Truncate;
            t.text = text.Replace("\r", "");
        }

        // ---------------- delete ----------------
        static void OpenDestroy()
        {
            var ui = UiManager.Instance;
            if (!ui || Mine == null) return;
            SocialUi.Close(ref destroy);
            bool pending = Mine.State == "C";
            destroy = ui.Open(pending ? PendingDlg : DestroyDlg);
            if (!destroy) return;
            string pre = pending ? "ID_TC_TEAMDESTROYCANCEL_" : "ID_TC_TEAMDESTROY_";
            destroy.SetText(pre + "TITLE_TEAMNAME", SocialNet.T("10559", "Crew Name"));
            destroy.SetText(pre + "TITLE_MEMBERCOUNT", SocialNet.T("10560", "Number of Crew Members"));
            destroy.SetText(pre + "TITLE_OWNCHANNEL", SocialNet.T("10561", "Crew Ownership"));
            destroy.SetText(pre + "TITLE_TEAMPOINT", SocialNet.T("10562", "Crew Points"));
            destroy.SetText(pre + "TITLE_TEAMLEVEL", SocialNet.T("10563", "Crew Rank"));
            destroy.SetText(pre + "TITLE_TEAMRECORD", SocialNet.T("10564", "Crew Winning Ratio"));
            destroy.SetText(pre + "TEAMNAME", Mine.Name);
            destroy.SetText(pre + "MEMBERCOUNT", Mine.Members.Count.ToString());
            destroy.SetText(pre + "OWNCHANNEL", Mine.Channels.Count == 0 ? "-" : string.Join(", ", Mine.Channels));
            destroy.SetText(pre + "TEAMPOINT", Mine.Points.ToString("N0"));
            destroy.SetText(pre + "TEAMLEVEL", Mine.Grade);
            destroy.SetText(pre + "TEAMRECORD", SocialNet.T("10340", "%d total channel captures", Mine.Captures));
            foreach (var s in new[] { "TEAMLADDERGRADE", "TEAMLADDERRANK", "TEAMLADDERGRADE_BASE", "TEAMLADDERRANK_BASE", "TEAMLADDERGRADE_POINT", "TEAMLADDERRANK_POINT", "TEAMLADDERGRADE_TITLE", "TEAMLADDERRANK_TITLE" })
                destroy.SetVisible(pre + s, false);   // the crew ladder shows only with Main.LadderDisplay
            if (pending)
            {
                var at = DateTimeOffset.FromUnixTimeMilliseconds(Mine.CloseAt).ToLocalTime();
                var left = at - DateTimeOffset.Now;
                destroy.SetText("ID_TC_TEAMDESTROYCANCEL_WARNING", SocialNet.T("10317", "You can cancel the Crew delete request within 7 days of deletion."));
                destroy.SetText("ID_TC_TEAMDESTROYCANCEL_TEXT1", SocialNet.T("10341", "%s Crew is applying for deletion", Mine.Name));
                destroy.SetText("ID_TC_TEAMDESTROYCANCEL_TEXT2", SocialNet.T("10740", "Crew Closing Time is %04d/%02d/%02d %02d o'clock,", at.Year, at.Month, at.Day, at.Hour));
                destroy.SetText("ID_TC_TEAMDESTROYCANCEL_TEXT3", SocialNet.T("10821", "%s Crew is being deleted. The Crew will be deleted after %d day(s) and %d hour(s). ", Mine.Name, Math.Max(0, left.Days), Math.Max(0, left.Hours)));
                destroy.SetText("ID_TC_TEAMDESTROYCANCEL_TEXT4", SocialNet.T("10742", "During the Crew Closing Time, you can cancel the request."));
            }
            else
            {
                destroy.SetText("ID_TC_TEAMDESTROY_STATIC_WARNING", SocialNet.T("10316", "Before deleting your Crew, please take some time to reconsider."));
                destroy.SetText("ID_TC_TEAMDESTROY_STATIC_AGREE", "I agree to the terms.");
                AgreementText(destroy, "ID_TC_TEAMDESTROY_AGREEMENT", "aggrement_teamdestroy.txt");
                destroy.Control("ID_TC_TEAMDESTROY_AGREEMENTCHECK")?.SetChecked(false);
                var agree = destroy.Control("ID_TC_TEAMDESTROY_AGREEMENTCHECK");
                if (agree) SocialUi.FixCheck(agree);
            }
        }

        public static void Disband(bool cancel)
        {
            void Done(string chat) { SocialUi.Close(ref destroy); ChatBox.System(chat); Load(); }
            if (!SocialNet.Online)
            {
                var st = SocialStore.Current;
                if (st.MyCrew == null) return;
                st.MyCrew.State = cancel ? "A" : "C";
                st.MyCrew.CloseAt = cancel ? 0 : SocialUi.NowMs + 7 * 86400000L;
                st.Save();
                Done(cancel ? SocialNet.T("10815", "%s Crew's Crew Leader has canceled the request to delete the Crew.", st.MyCrew.Name) : SocialNet.T("10814", "%s Crew's Crew Leader has requested to delete the Crew.", st.MyCrew.Name));
                return;
            }
            SocialNet.Request(cancel ? "crew.cancelDisband" : "crew.disband", null, r =>
            {
                if (!r.B("ok")) { UiPopup.Message(SocialNet.Err(r)); return; }
                Mine = FromJson(r.O("crew")) ?? Mine;
                Done(cancel ? "The request to delete the Crew was cancelled." : "Your Crew will be deleted in 7 days unless you cancel.");
            });
        }

        // ---------------- join requests ----------------
        /// <summary>"/crew join name" or the player menu: ask to join the crew of a driver (or a crew by name).</summary>
        public static void RequestJoin(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            if (!SocialNet.Online) { UiPopup.Message("Joining another driver's crew needs the online server."); return; }
            SocialNet.Request("crew.request", new Dictionary<string, object> { ["name"] = name }, r =>
            {
                if (r.B("ok")) ChatBox.System(SocialNet.T("10163", "You have requested to join %s Crew.", r.S("crew", name)));
                else UiPopup.Message(SocialNet.Err(r));
            });
        }

        // ---------------- server pushes ----------------
        public static void OnPush(string t, Dictionary<string, object> m)
        {
            if (t == "crew.lobby" || t == "crew.turn" || t == "crew.items" || t == "crew.cheer") { ChannelBattle.OnPush(t, m); return; }
            if (t == "crew.request")
            {
                string from = m.S("from");
                UiPopup.Show(SocialNet.T("10020", "Do you approve %s to join your crew?", from), id =>
                    SocialNet.Request("crew.answer", new Dictionary<string, object> { ["name"] = from, ["accept"] = id == UiPopup.Yes }, r => { if (!r.B("ok")) UiPopup.Message(SocialNet.Err(r)); else Load(); }),
                    UiPopup.Yes, UiPopup.No);
                return;
            }
            if (t != "crew.notice") return;
            string crew = m.S("crew"), name = m.S("name");
            bool me = name == P?.Name;
            string line = m.S("type") switch
            {
                "joined" => me ? SocialNet.T("10016", "You have joined %s's crew.", crew) : SocialNet.T("10817", "%s has joined the %s Crew.", name, crew),
                "denied" => SocialNet.T("10017", "Your request to join %s's crew has been denied.", crew),
                "left" => me ? SocialNet.T("10018", "You have left %s's crew.", crew) : SocialNet.T("10818", "%s has left the %s Crew.", name, crew),
                "kicked" => me ? SocialNet.T("10023", "You have been kicked from %s's crew. ", crew) : SocialNet.T("10819", "%s has been kicked from the %s Crew.", name, crew),
                "leader" => SocialNet.T("10820", "%s has been chosen as the new Crew Leader of the %s Crew.", name, crew),
                "promoted" => m.I("rank") == 2 ? SocialNet.T("10838", "%s has been promoted to First Class.", name) : m.I("rank") == 3 ? SocialNet.T("10839", "%s has been promoted to Second Class.", name) : $"{name} is now {m.S("rankName")} of the {crew} Crew.",
                "renamed" => SocialNet.T("11305", "%s has changed the team name to %s.", name, crew),
                "deleteRequested" => SocialNet.T("10814", "%s Crew's Crew Leader has requested to delete the Crew.", crew),
                "deleteCancelled" => SocialNet.T("10815", "%s Crew's Crew Leader has canceled the request to delete the Crew.", crew),
                "deleted" => SocialNet.T("10816", "%s Crew has been deleted.", crew),
                "login" => SocialNet.T("11413", "[Team] %s has logged on.", name),
                "battle" => m.B("forfeit") ? $"{m.S("defender")} Crew left {m.S("label")} empty and lost its ownership." : TeamBattle.NoticeLine(m),
                "captured" => SocialNet.T("10376", "Obtained Channel %d Ownership!", ChannelBattle.Channels.FirstOrDefault(x => x.Key == m.S("channel"))?.Number ?? 0) + $" ({crew}, {m.S("label")})",
                _ => null,
            };
            if (!string.IsNullOrEmpty(line)) ChatBox.Ensure()?.Add("team", "", line);
            if (m.S("type") != "login" && m.S("type") != "battle") Load();
            if (m.S("type") == "battle" || m.S("type") == "captured") ChannelBattle.Load();
        }

        public static void ResetAll()
        {
            Mine = null; search = ""; sortByName = sortDesc = manage = false; cardPage = 0;
            SocialUi.Close(ref page); SocialUi.Close(ref center); SocialUi.Close(ref make); SocialUi.Close(ref made);
            SocialUi.Close(ref destroy); SocialUi.Close(ref rank); SocialUi.Close(ref markDlg);
        }
    }
}
