using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DriftCity.Game;
using DriftCity.Net;
using DriftCity.Social;
using DriftCity.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;

namespace DriftCity.Tests
{
    /// <summary>
    /// The social layer (DriftCity_RE/SOCIAL.md): offline - the Community window (notes with the welcome gift, AI friends,
    /// the crew page), the party bar/list with AI partners, the Crew Center and Create Crew, the player menu and the send
    /// dialog; online against the local server with a social bot (tools/bot.js --social) - party invite/accept, friend
    /// request + whisper, note send/receive (kept by the server), crew create/join/leave/delete. Writes Preview/social_*.png.
    /// </summary>
    public class SocialTests
    {
        string tempDir;
        Process server, bot;
        const int Port = 7874;
        static string Url => $"ws://127.0.0.1:{Port}";
        static string ServerDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "DriftCityServer"));

        [SetUp]
        public void SetUp()
        {
            tempDir = Path.Combine(Path.GetTempPath(), "driftcity_social_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            PlayerProfile.SaveDirOverride = tempDir;
            PlayerProfile.Slot = 0;
            PlayerProfile.Current = null;
            SocialStore.Reset();
            SocialHost.Ensure();   // a previous test's teardown destroys every root object
            Directory.CreateDirectory("Preview");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Community.Close();
            PartyWindow.ResetAll();
            Teams.ResetAll();
            Mail.ResetAll();
            PlayerMenu.Close();
            if (OnlineSession.Instance) OnlineSession.Instance.Logout();
            foreach (var p in new[] { bot, server }) try { if (p != null && !p.HasExited) p.Kill(); } catch { }
            PlayerProfile.SaveDirOverride = null;
            SocialStore.Reset();
            foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Exclude).Where(g => g.transform.parent == null)) Object.Destroy(go);
            yield return new WaitForSecondsRealtime(0.5f);
            try { Directory.Delete(tempDir, true); } catch { }
        }

        static IEnumerator WaitFor(System.Func<bool> condition, float timeout, string what)
        {
            float t = 0;
            while (!condition() && t < timeout) { t += Time.unscaledDeltaTime; yield return null; }
            Assert.IsTrue(condition(), "timed out waiting for " + what);
        }

        static void Shot(string name)
        {
            Canvas.ForceUpdateCanvases();
            var tex = UiManager.Instance.Capture(Camera.main);
            File.WriteAllBytes(Path.Combine("Preview", name), tex.EncodeToPNG());
            Debug.Log("Drift City social: wrote Preview/" + name);
        }

        static NuiView Popup => UiManager.Instance.Find(3250000);

        static IEnumerator ClosePopups()
        {
            for (int i = 0; i < 5 && Popup; i++) { var p = Popup; p.Click(p.Control("ID_POPUP_OKONLY") ? "ID_POPUP_OKONLY" : UiPopup.Yes); yield return null; }
        }

        static string Text(NuiView v, string id) => v.Control(id)?.GetComponentInChildren<UnityEngine.UI.Text>(true)?.text;

        [UnityTest]
        public IEnumerator OfflineCommunityPartyCrew()
        {
            var p = new PlayerProfile { Name = "Socialite", Level = 25, Mito = 20000 };
            p.Cars.Add(new OwnedCar { VehicleIndex = 0, Color = 1 });
            p.Save();
            new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            var flow = new GameObject("Game").AddComponent<GameFlow>();
            flow.SkipParkingLot = true; flow.SkipIntro = true;
            yield return FrontEndSteps.ToCharacterSelect(flow);
            yield return FrontEndSteps.EnterSelected();
            yield return WaitFor(() => flow.InCity && CitySession.Current, 600, "city");
            yield return new WaitForSeconds(1f);
            var ui = UiManager.Instance;

            // the welcome note makes the main bar's notice show and Messages the default tab
            yield return WaitFor(() => Mail.Unread == 1, 5, "welcome note");
            Community.Open();
            yield return WaitFor(() => Community.Current == Community.Tab.Note && Mail.ListView, 5, "Messages tab");
            yield return null;
            Assert.AreEqual("Drift City", Mail.SendersShown.FirstOrDefault());
            Assert.AreEqual("(001/001)", Text(Mail.ListView, "ID_NOTE_MSGBOX"));
            Shot("social_1_notes.png");
            Mail.ListView.Click("ID_NOTE_BTN00");
            yield return WaitFor(() => Mail.ReadView, 3, "read panel");
            Assert.AreEqual(0, Mail.Unread, "opening marks it read");
            Assert.That(Text(Mail.ListView, "ID_NOTE_FREE_TIME00"), Does.Contain("Hour"), "read notes last 24 hours");
            long mito = PlayerProfile.Current.Mito;
            Mail.Take(Mail.Reading);
            Assert.AreEqual(mito + 1000, PlayerProfile.Current.Mito, "the gift slot pays the attached Mito");
            yield return null;
            Shot("social_2_note_read.png");
            // save box
            Mail.ListView.Click("ID_NOTE_CB00ST");
            Mail.ListView.Click("ID_NOTE_SAVE");
            yield return null;
            Assert.AreEqual(1, Mail.Saved.Count);

            // friends: the AI drivers accept at once; unknown names don't exist offline
            Community.Select(Community.Tab.MyFriend);
            yield return WaitFor(() => Friends.View, 3, "friend manager");
            Friends.Add("Rio"); Friends.Add("mika"); Friends.Add("Nobody");
            yield return ClosePopups();
            Assert.AreEqual(2, Friends.List.Count);
            Assert.AreEqual("Mika", Friends.RowTexts[0][1], "sorted list, the AI's own spelling");
            Assert.That(Friends.RowTexts[0][2], Does.StartWith("Channel"));
            Friends.View.Click("ID_MYFRIEND_MANAGER_BTN01");
            yield return null;
            Shot("social_3_friends.png");
            Friends.Block("Rio");
            yield return null;
            Assert.AreEqual(1, Friends.Blocked.Count);
            Friends.Unblock("Rio");
            Friends.Add("Rio");
            yield return ClosePopups();

            // party: 410000 -> AI partners join; the leader's list has kick buttons; F12 toggles the list
            Community.Close();
            PartyWindow.Invite("Rio");
            yield return WaitFor(() => ui.Find(PartyWindow.CreateDlg), 3, "party create dialog");
            Assert.AreEqual("Rio", ui.Find(PartyWindow.CreateDlg).GetInput("ID_PARTY_CREATE_TARGETNAME"));
            Shot("social_4_party_invite.png");
            SocialUi.SetInput(ui.Find(PartyWindow.CreateDlg), "ID_PARTY_CREATE_PARTYNAME", "Night Riders");
            ui.Find(PartyWindow.CreateDlg).Click("ID_PARTY_CREATE_BUTTON");
            yield return null;
            Assert.IsTrue(PartyWindow.InParty);
            PartyWindow.Invite("Mika");
            ui.Find(PartyWindow.CreateDlg).Click("ID_PARTY_CREATE_BUTTON");
            yield return null;
            Assert.AreEqual(3, PartyWindow.Members.Count);
            Assert.AreEqual("Night Riders", Text(PartyWindow.BarView, "ID_PARTY_NAME"));
            Assert.AreEqual("Lv25", Text(PartyWindow.ListView, "ID_PARTYLIST_MEMBERLEVEL1"));
            Assert.IsTrue(PartyWindow.ListView.Control("ID_PARTYLIST_MEMBERBAN2").gameObject.activeSelf, "leader can kick");
            Assert.IsFalse(PartyWindow.ListView.Control("ID_PARTYLIST_MEMBERBAN1").gameObject.activeSelf, "not yourself");
            Shot("social_5_party.png");
            PartyWindow.ListView.Click("ID_PARTYLIST_MEMBERBAN3");
            yield return null;
            Assert.AreEqual(2, PartyWindow.Members.Count);
            PartyWindow.ToggleList();
            Assert.IsFalse(PartyWindow.ListView);
            PartyWindow.ToggleList();
            PartyWindow.Leave();
            yield return null;
            Assert.IsFalse(PartyWindow.InParty);
            Assert.IsFalse(PartyWindow.BarView, "the bar hides with one member or less");

            // crews: the Crew Center -> Create Crew (terms, name check, mark) -> result -> My Crew page
            Teams.OpenCenter();
            yield return WaitFor(() => Teams.CenterView, 3, "Crew Center");
            yield return null;
            Shot("social_6_crew_center.png");
            Teams.CenterView.Click("ID_TC_MAIN_TEAMMAKEDESTROY");
            yield return WaitFor(() => Teams.MakeView, 3, "Create Crew");
            var make = Teams.MakeView;
            SocialUi.SetInput(make, "ID_TC_TEAMMAKE_NAME", "Speedsters");
            make.Click("ID_TC_TEAMMAKE_MAKE");
            yield return null;
            Assert.IsTrue(Popup, "10330 terms first");
            yield return ClosePopups();
            make.Click("ID_TC_TEAMMAKE_AGREEMENTCHECK");
            make.Click("ID_TC_TEAMMAKE_NAMECHECK");
            yield return ClosePopups();
            make.Click("ID_TC_TEAMMAKE_MARK3");
            yield return null;
            Shot("social_7_crew_make.png");
            long before = PlayerProfile.Current.Mito;
            make.Click("ID_TC_TEAMMAKE_MAKE");
            yield return WaitFor(() => Teams.MadeView, 3, "crew created");
            Assert.AreEqual(before - Teams.CreateFee, PlayerProfile.Current.Mito);
            Assert.AreEqual("Speedsters", Teams.CrewName);
            Shot("social_8_crew_made.png");
            Teams.MadeView.Click("ID_TC_TEAMOK_OK");
            yield return WaitFor(() => Teams.PageView, 3, "My Crew page");
            yield return null;
            Assert.AreEqual("Speedsters", Text(Teams.PageView, "ID_MY_TEAM_TEAMNAME"));
            Assert.AreEqual("Socialite", Text(Teams.PageView, "ID_MY_TEAM_MEMBER_SLOT0_NAME"));
            Shot("social_9_my_crew.png");
            Teams.Disband(false);
            yield return null;
            Assert.AreEqual("C", Teams.Mine.State);
            Teams.Disband(true);
            Community.Close();

            // the player menu and the send dialog
            PlayerMenu.Open("Rio");
            yield return null;
            Assert.AreEqual("Rio", Text(PlayerMenu.View, "ID_COMMAND_USERNAME"));
            Shot("social_10_player_menu.png");
            PlayerMenu.View.Click("ID_COMMAND_SENDLETTER");
            yield return WaitFor(() => Mail.SendView, 3, "send dialog");
            Assert.AreEqual("Rio", Mail.SendView.GetInput("ID_MYFRIEND_MSGSEND_EDIT_RECIEVER"));
            Shot("social_11_send.png");
            // everything survives a reload of the offline store
            SocialStore.Reset();
            Assert.AreEqual(2, SocialStore.Current.Friends.Count(f => f.Status == "F"));
            Assert.AreEqual("Speedsters", SocialStore.Current.MyCrew.Name);
        }

        [UnityTest]
        public IEnumerator OnlinePartyFriendNoteCrewWithBot()
        {
            if (!File.Exists(Path.Combine(ServerDir, "server.js"))) Assert.Ignore("DriftCityServer not found next to the Unity project");
            server = Node("server.js", new Dictionary<string, string> { ["DC_PORT"] = Port.ToString(), ["DC_DB"] = Path.Combine(tempDir, "test.db") }, true);
            float t = 0; bool up = false;
            while (!up && t < 15f)
            {
                try { using (var c = new System.Net.Sockets.TcpClient()) { c.Connect("127.0.0.1", Port); up = true; } } catch { }
                if (!up) { yield return new WaitForSecondsRealtime(0.25f); t += 0.25f; }
            }
            Assert.IsTrue(up, "server started");

            new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            var flow = new GameObject("Game").AddComponent<GameFlow>();
            flow.SkipParkingLot = true; flow.SkipIntro = true;
            yield return FrontEndSteps.ToCharacterSelect(flow, Url, "soctester", "secret123");
            yield return WaitFor(() => UiManager.Instance.Find(3250000), 15, "create-account popup");
            UiManager.Instance.Find(3250000).Click(UiPopup.Yes);
            yield return FrontEndSteps.NewCharacter("SocTester", 1);
            UiManager.Instance.Find(150000).Click("ID_CREATECAR_NEW_NEXT");
            yield return WaitFor(() => UiManager.Instance.Find(320000), 600, "city HUD");
            yield return WaitFor(() => OnlineSession.Instance.Channel != null, 15, "channel join");
            var ui = UiManager.Instance;
            var session = CitySession.Current;
            var chat = new List<(string scope, string from, string text)>();
            OnlineSession.Instance.ChatReceived += (s, f, x) => chat.Add((s, f, x));
            bot = Node($"tools/bot.js 1 --url {Url} --name Sbot --social --city {session.City}");
            yield return WaitFor(() => OnlineSession.Instance.Remotes && OnlineSession.Instance.Remotes.Count >= 1, 20, "bot in the channel");

            // the server's welcome note
            yield return WaitFor(() => Mail.Inbox.Any(n => n.Sender == "Drift City"), 10, "welcome note");

            // party: 410000 -> the bot accepts (420000 on its side) -> the bar and list
            PartyWindow.Invite("Sbot1");
            yield return WaitFor(() => ui.Find(PartyWindow.CreateDlg), 3, "party create dialog");
            SocialUi.SetInput(ui.Find(PartyWindow.CreateDlg), "ID_PARTY_CREATE_PARTYNAME", "Online Crew Run");
            ui.Find(PartyWindow.CreateDlg).Click("ID_PARTY_CREATE_BUTTON");
            yield return WaitFor(() => PartyWindow.Members.Count == 2, 10, "bot joined the party");
            Assert.AreEqual("Online Crew Run", PartyWindow.Name);
            Assert.IsTrue(PartyWindow.IAmLeader);
            yield return null;
            Shot("social_online_1_party.png");
            PartyWindow.ListView.Click("ID_PARTYLIST_MEMBERBAN2");
            yield return WaitFor(() => !PartyWindow.InParty, 10, "kicked the bot, party disbanded");

            // friends: request -> the bot approves; whisper to the bot -> its answer
            Community.Open(Community.Tab.MyFriend);
            yield return WaitFor(() => Friends.View, 5, "friend manager");
            Friends.Add("Sbot1");
            // the bot approves (10810); the open manager reloads its list
            yield return WaitFor(() => Friends.List.Any(f => f.Name == "Sbot1" && f.Status == "F" && f.Online), 10, "friend accepted");
            yield return null;
            Shot("social_online_2_friends.png");
            OnlineSession.Instance.SubmitChat("*Sbot1 hello there");
            yield return WaitFor(() => chat.Any(c => c.scope == "whisper" && c.from == "Sbot1"), 10, "whisper answer");

            // notes: send (50 Mito) -> the bot answers with a note; the server keeps it
            long mito = PlayerProfile.Current.Mito;
            bool sent = false;
            Mail.Send("Sbot1", "Race me at the harbour", ok => sent = ok);
            yield return WaitFor(() => sent, 10, "note sent");
            yield return WaitFor(() => PlayerProfile.Current.Mito == mito - Mail.CostPerReceiver, 5, "50 Mito charged");
            yield return WaitFor(() => Mail.Inbox.Any(n => n.Sender == "Sbot1"), 10, "the bot's note");
            Mail.ResetAll();
            bool loaded = false;
            Mail.Load(() => loaded = true);
            yield return WaitFor(() => loaded, 10, "note list");
            Assert.IsTrue(Mail.Inbox.Any(n => n.Sender == "Sbot1" && n.Content.StartsWith("Got your note")), "notes kept by the server");
            Community.Select(Community.Tab.Note);
            yield return WaitFor(() => Mail.ListView, 3, "Messages tab");
            yield return null;
            Shot("social_online_3_notes.png");

            // crews: level 20 and the fee through the server console, then create -> the bot asks to join -> approve -> it leaves
            server.StandardInput.WriteLine("setlevel SocTester 25");
            server.StandardInput.WriteLine("givemito SocTester 10000");
            yield return WaitFor(() => PlayerProfile.Current.Level == 25, 10, "level 25");
            bool created = false;
            Teams.Create("NetRacers", 4, ok => created = ok);
            yield return WaitFor(() => created, 10, "crew created");
            OnlineSession.Instance.SubmitChat("*Sbot1 crew SocTester");
            yield return WaitFor(() => Popup && Text(Popup, "ID_POPUP_TEXT") != null && Text(Popup, "ID_POPUP_TEXT").Contains("Sbot1"), 10, "join request (10020)");
            Popup.Click(UiPopup.Yes);
            yield return WaitFor(() => Teams.Mine != null && Teams.Mine.Members.Count == 2, 10, "the bot joined the crew");
            Community.Select(Community.Tab.MyTeam);
            yield return WaitFor(() => Teams.PageView, 5, "My Crew page");
            yield return null;
            Shot("social_online_4_crew.png");
            OnlineSession.Instance.SubmitChat("@crew chat works");
            yield return WaitFor(() => chat.Any(c => c.scope == "team" && c.text == "crew chat works"), 10, "crew chat");
            OnlineSession.Instance.SubmitChat("*Sbot1 leavecrew");
            yield return WaitFor(() => Teams.Mine != null && Teams.Mine.Members.Count == 1, 10, "the bot left the crew");
            Teams.Disband(false);
            yield return WaitFor(() => Teams.Mine != null && Teams.Mine.State == "C", 10, "deletion requested");
        }

        static Process Node(string args, Dictionary<string, string> env = null, bool stdin = false)
        {
            var psi = new ProcessStartInfo("node", args)
            {
                WorkingDirectory = ServerDir, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin,
            };
            if (env != null) foreach (var kv in env) psi.EnvironmentVariables[kv.Key] = kv.Value;
            var p = Process.Start(psi);
            p.OutputDataReceived += (_, e) => { if (e.Data != null) Debug.Log("[node] " + e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) Debug.Log("[node err] " + e.Data); };
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            return p;
        }
    }
}
