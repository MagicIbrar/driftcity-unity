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
    /// Party play (DriftCity_RE/PARTYPLAY.md). Offline: the Undercity with the offline party's AI driver first and AI
    /// partners in the empty slots, PARTYUC's running counters, the party checks, no party bonus; the online screens
    /// (REQUESTINVITE, the PARTYPATROL timer and "Post %u", 3890000 with several lines) driven by the server's messages.
    /// Online against the local server with a party bot (tools/bot.js --party): the party bonus strip, the Undercity
    /// request -> the bot accepts -> the shared run in the private channel with the bot's car -> per-member rewards ->
    /// back in the city channel, then a party patrol with shared posts and the leader quitting (11159).
    /// Writes Preview/partyplay_*.png.
    /// </summary>
    public class PartyPlayTests
    {
        string tempDir;
        Process server, bot;
        const int Port = 7879;
        static string Url => $"ws://127.0.0.1:{Port}";
        static string ServerDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "DriftCityServer"));

        [SetUp]
        public void SetUp()
        {
            tempDir = Path.Combine(Path.GetTempPath(), "driftcity_party_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            PlayerProfile.SaveDirOverride = tempDir;
            PlayerProfile.Slot = 0;
            PlayerProfile.Current = null;
            SocialStore.Reset();
            SocialHost.Ensure();
            UndercityStage.GlobalTimeScale = 0.05f;
            Directory.CreateDirectory("Preview");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            UndercityStage.GlobalTimeScale = 1f;
            PartyWindow.ResetAll();
            UndercityParty.ResetAll();
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
            Object.Destroy(tex);
            Debug.Log("Drift City party play: wrote Preview/" + name);
        }

        static string Text(NuiView v, string id) => v ? v.Control(id)?.GetComponentInChildren<UnityEngine.UI.Text>(true)?.text : null;
        static bool Shown(NuiView v, string id) { var c = v ? v.Control(id) : null; return c && c.gameObject.activeInHierarchy; }

        static Dictionary<string, object> Msg(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1] is int n ? (double)n : kv[i + 1];
            return d;
        }

        static InstantRow PartyRow(string city, int level) =>
            PatrolMission.RowsFor(city, level).FirstOrDefault(r => r.IsParty) ??
            InstantRow.All.FirstOrDefault(r => r.IsPatrol && r.Kind == "party_move" && r.Posts.Count > 1 && MissionPosts.CityOf(r.Posts[0]) == city && r.Posts.Skip(1).Any(id => MissionPosts.Find(id) != null));

        [UnityTest]
        public IEnumerator OfflinePartyUndercityAndPartyScreens()
        {
            var prof = new PlayerProfile { Name = "Partier", Level = 85, Mito = 100_000, City = "cras" };
            prof.Cars.Add(new OwnedCar { VehicleIndex = 0, Color = 1 });
            prof.Save();
            new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            var flow = new GameObject("Game").AddComponent<GameFlow>();
            flow.SkipParkingLot = true; flow.SkipIntro = true;
            yield return FrontEndSteps.ToCharacterSelect(flow);
            yield return FrontEndSteps.EnterSelected();
            yield return WaitFor(() => flow.InCity && CitySession.Current && flow.PlayerCar, 600, "city");
            var session = CitySession.Current;
            session.MissionOfferInterval = 1e6f;
            var ui = UiManager.Instance;
            string me = PlayerProfile.Current.Name;

            // the client's party checks (UC_Err_PlayTogether without a party)
            Assert.AreEqual("UC_Err_PlayTogether", UndercityParty.CheckParty(flow.City));
            PartyWindow.Invite("Rio");
            yield return WaitFor(() => ui.Find(PartyWindow.CreateDlg), 3, "party create dialog");
            ui.Find(PartyWindow.CreateDlg).Click("ID_PARTY_CREATE_BUTTON");
            yield return WaitFor(() => PartyWindow.InParty, 3, "offline AI party");
            Assert.IsNull(UndercityParty.CheckParty(flow.City), "a party of two in this city");
            Assert.AreEqual("UC_Err_SameCity", UndercityParty.CheckParty("oros"));
            // no party bonus offline (0x4fe050 needs the server's flag)
            PartyWindow.UpdateBonus();
            Assert.AreEqual(0, PartyWindow.BonusExp + PartyWindow.BonusMito);
            Assert.IsFalse(PartyWindow.BonusView, "no PARTYBONUS strip offline");

            // ---- the Undercity with the offline party: Rio first, AI partners in the empty slots ----
            var stage = UndercityStage.Begin(session, 3);
            Assert.IsTrue(stage);
            yield return WaitFor(() => stage.Phase == UndercityStage.State.HubAccept, 60, "UC_HUB_NeedAccept");
            Assert.AreEqual(3, stage.Party.Count);
            Assert.AreEqual("Rio", stage.Party[0].Name, "the party member takes the first slot");
            Assert.IsFalse(stage.Party.Skip(1).Any(p => p.Name == "Rio"));
            Assert.IsFalse(stage.Online);
            stage.Accept();
            yield return WaitFor(() => stage.Phase == UndercityStage.State.Hubs, 30, "HUB phase");
            stage.PartnersPaused = true;
            var towers = stage.Hubs.Where(h => h && h.Alive && h.Kind == UndercityStage.HubKind.Tower).Take(3).ToList();
            foreach (var h in towers) stage.Destroyed(h, 1);
            yield return null;
            // PARTYUC: running counters per member (HUBs, red HUBs, barriers, HUV hits; member +0x44)
            var partyUc = ui.Find(UndercityHud.PartyUc);
            Assert.IsTrue(partyUc);
            Assert.AreEqual("3", Text(partyUc, "ID_PARTYUC_TEXTP2"));
            Assert.AreEqual(3, stage.Party[0].Score.PartyUc);
            yield return new WaitForSeconds(0.3f);
            Shot("partyplay_uc_offline_party.png");
            stage.Quit();
            yield return WaitFor(() => !UndercityStage.Active, 30, "run over");

            // ---- the online screens, driven by the server's messages ----
            // REQUESTINVITE 3540000: 10770 with the inviter, 10772 with the fee, 15 dots
            UndercityParty.OnPush("party.uc.invite", Msg("room", 7, "from", "Rio", "fee", UndercityData.EntryFee, "seconds", 15));
            yield return WaitFor(() => UndercityParty.Host && UndercityParty.Host.InviteView, 3, "REQUESTINVITE");
            var inv = UndercityParty.Host.InviteView;
            StringAssert.Contains("Rio", Text(inv, "ID_UC_REQUESTINVITE_TEXT"));
            StringAssert.Contains(UndercityData.EntryFee.ToString(), Text(inv, "ID_UC_REQUESTINVITE_TEXTLOWER"));
            Assert.IsTrue(Shown(inv, "ID_UC_REQUESTINVITE_14"), "15 dots at the start");
            yield return new WaitForSeconds(0.2f);
            Shot("partyplay_uc_requestinvite.png");
            inv.Click("ID_UC_REQUESTINVITE_DENY");
            yield return null;
            Assert.IsFalse(UndercityParty.Host && UndercityParty.Host.InviteView, "DENY closes it");
            UndercityParty.OnPush("party.uc.cancel", Msg("room", 7, "text", "Registration canceled due to party being unable to accept or rejecting the invitation."));
            yield return null;
            StringAssert.Contains("Registration canceled", UndercityParty.LastCancel);
            for (int i = 0; i < 3 && ui.Find(3250000); i++) { ui.Find(3250000).Click("ID_POPUP_OKONLY"); yield return null; }

            // PARTYPATROL: the timer 3880000, the shared posts ("Post %u" on the bar and the list), 3890000 with every member
            var patrol = PatrolMission.Instance;
            Assert.IsTrue(patrol, "patrols attached to the city");
            var row = PartyRow(flow.City, 85);
            Assert.IsNotNull(row, "a party patrol row in " + flow.City);
            PatrolMission.OnPartyPush("party.patrol.start", Msg("id", 1, "row", row.Index, "leader", me, "members", new List<object> { me, "Rio" }, "posts", row.MaxPosts, "seconds", 300));
            yield return null;
            Assert.IsTrue(PatrolMission.Active && patrol.PartyRun, "party patrol running");
            Assert.IsTrue(patrol.TimerView, "PARTYPATROL timer");
            Assert.IsTrue(PartyWindow.PatrolShown);
            PatrolMission.OnPartyPush("party.patrol.post", Msg("id", 1, "index", 0, "by", me, "posts", 1, "total", 1, "seconds", 312, "bonus", 15,
                "counts", new List<object> { Msg("name", me, "posts", 1), Msg("name", "Rio", "posts", 0) }));
            yield return null;
            Assert.AreEqual(1, patrol.Visited);
            Assert.AreEqual("Post 1", Text(PartyWindow.BarView, "ID_PARTY_COMBOCOUNT"));
            Assert.AreEqual("Post 1", Text(PartyWindow.ListView, "ID_PARTYLIST_POST01"));
            Assert.IsTrue(Shown(PartyWindow.ListView, "ID_PARTYLIST_POST02"));
            Assert.IsTrue(Shown(patrol.TimerView, "ID_PARTYPATROL_BONUSTIME02"), "+15: tens digit 1");
            Assert.IsTrue(Shown(patrol.TimerView, "ID_PARTYPATROL_BONUSTIME16"), "+15: ones digit 5");
            Assert.Greater(patrol.PartyClock, 300f);
            yield return new WaitForSeconds(0.2f);
            Shot("partyplay_patrol_timer.png");
            PatrolMission.OnPartyPush("party.patrol.end", Msg("id", 1, "complete", false, "reason", "leader", "seconds", 75,
                "rows", new List<object> { Msg("name", me, "posts", 1, "exp", 0, "mito", 13), Msg("name", "Rio", "posts", 0, "exp", 0, "mito", 0) }));
            yield return null;
            Assert.IsFalse(PatrolMission.Active);
            Assert.IsFalse(PartyWindow.PatrolShown);
            var res = ui.Find(PatrolMission.PartyResultId);
            Assert.IsTrue(res, "3890000");
            Assert.AreEqual(me, Text(res, "ID_PARTYPATROL_RESULT_NAME1ST"));
            Assert.AreEqual("Rio", Text(res, "ID_PARTYPATROL_RESULT_NAME2ND"));
            Assert.IsFalse(Shown(res, "ID_PARTYPATROL_RESULT_NAME3RD"));
            yield return new WaitForSeconds(0.2f);
            Shot("partyplay_patrol_result.png");
            res.Click("ID_PARTYPATROL_RESULT_OK");
        }

        [UnityTest]
        public IEnumerator OnlinePartyUndercityAndPatrolWithBot()
        {
            if (!File.Exists(Path.Combine(ServerDir, "server.js"))) Assert.Ignore("DriftCityServer not found next to the Unity project");
            server = Node("server.js", new Dictionary<string, string> { ["DC_PORT"] = Port.ToString(), ["DC_DB"] = Path.Combine(tempDir, "test.db"), ["DC_UC_TIMESCALE"] = "0.05" }, true);
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
            yield return FrontEndSteps.ToCharacterSelect(flow, Url, "pptester", "secret123");
            yield return WaitFor(() => UiManager.Instance.Find(3250000), 15, "create-account popup");
            UiManager.Instance.Find(3250000).Click(UiPopup.Yes);
            yield return FrontEndSteps.NewCharacter("PTester", 1);
            UiManager.Instance.Find(150000).Click("ID_CREATECAR_NEW_NEXT");
            yield return WaitFor(() => UiManager.Instance.Find(320000), 600, "city HUD");
            yield return WaitFor(() => OnlineSession.Instance.Channel != null, 15, "channel join");
            var ui = UiManager.Instance;
            var session = CitySession.Current;
            session.MissionOfferInterval = 1e6f;
            string homeChannel = OnlineSession.Instance.Channel;
            bot = Node($"tools/bot.js 1 --url {Url} --name Pbot --party 400 --city {session.City}");
            yield return WaitFor(() => OnlineSession.Instance.Remotes && OnlineSession.Instance.Remotes.Count >= 1, 20, "bot in the channel");

            // party: the bot accepts; two members at or below PartyBonus_Level: +10 % EXP in the HUV modes (3870000)
            PartyWindow.Invite("Pbot1");
            yield return WaitFor(() => ui.Find(PartyWindow.CreateDlg), 3, "party create dialog");
            ui.Find(PartyWindow.CreateDlg).Click("ID_PARTY_CREATE_BUTTON");
            yield return WaitFor(() => PartyWindow.Members.Count == 2, 10, "bot joined the party");
            Assert.AreEqual(10, PartyWindow.BonusExp);
            Assert.AreEqual(0, PartyWindow.BonusMito);
            Assert.IsTrue(PartyWindow.BonusView, "PARTYBONUS strip");
            Assert.AreEqual("10", Text(PartyWindow.BonusView, "ID_PARTYBONUS_EXP"));
            yield return null;
            Shot("partyplay_online_party_bonus.png");

            // ---- the Undercity: the request (WAITPLAYER) -> the bot accepts (REQUESTINVITE on its side) -> 0xff ----
            var entrance = UndercityEntrance.Instance;
            Assert.IsTrue(entrance, "Undercity entrance in the city");
            entrance.RequestOnline();
            yield return WaitFor(() => UndercityStage.Active && UndercityStage.Active.Online, 20, "the party's run");
            var stage = UndercityStage.Active;
            Assert.AreEqual(0, stage.MySlot, "the requester is P1, the run leader");
            Assert.IsTrue(stage.RunLeader);
            Assert.AreEqual(4, stage.Slots.Count);
            Assert.AreEqual("Pbot1", stage.Slots[1].Name);
            Assert.IsTrue(stage.Slots[2].Ai && stage.Slots[3].Ai, "AI partners in the empty slots");
            yield return WaitFor(() => OnlineSession.Instance.Channel != null && OnlineSession.Instance.Channel != homeChannel, 10, "the stage's private channel");
            StringAssert.IsMatch("^" + session.City + @"-1\d\d$", OnlineSession.Instance.Channel, "channel 100 + room (the friend list's \"Undercity\")");
            yield return WaitFor(() => stage.Phase == UndercityStage.State.HubAccept, 60, "UC_HUB_NeedAccept");
            Assert.AreEqual("UC_HUB_NeedAccept", stage.Hud.AlertKey);
            stage.Accept();
            yield return WaitFor(() => stage.Phase == UndercityStage.State.Hubs, 30, "HUB phase");
            // the bot's car drives in the stage (the private channel's remote player)
            yield return WaitFor(() => OnlineSession.Instance.Remotes && OnlineSession.Instance.Remotes.Count >= 1, 10, "bot car in the stage");
            int total = stage.CountOf(UndercityStage.HubKind.Tower, UndercityStage.HubKind.RedTower);
            // the bot takes HUBs too (shared, counted for it on PARTYUC)
            yield return WaitFor(() => stage.Party[0].Score.Hubs + stage.Party[0].Score.Jumps > 0, 20, "a HUB taken by the bot");
            var partyUc = ui.Find(UndercityHud.PartyUc);
            Assert.IsTrue(partyUc);
            Assert.AreNotEqual("0", Text(partyUc, "ID_PARTYUC_TEXTP2"), "the bot's PARTYUC counter");
            yield return new WaitForSeconds(0.3f);
            Shot("partyplay_online_uc_hubs.png");
            stage.DebugDestroy(0, UndercityStage.HubKind.Tower, UndercityStage.HubKind.RedTower);
            yield return WaitFor(() => stage.ServerPhase == "main" || stage.ServerPhase == "hubsCleared", 30, "all HUBs down (server)");
            Assert.AreEqual(total, stage.Score.Hubs + stage.Score.Jumps + stage.Party.Sum(p => p.Score.Hubs + p.Score.Jumps), "every HUB counted once");
            yield return WaitFor(() => stage.Phase == UndercityStage.State.MainHub, 30, "Main HUB phase");
            Assert.AreEqual(15, stage.CountOf(UndercityStage.HubKind.Barrier), "barriers for a party of four");
            stage.DebugDestroy(0, UndercityStage.HubKind.Barrier);
            yield return WaitFor(() => stage.AliveCount(UndercityStage.HubKind.Barrier) == 0, 15, "barriers down");
            stage.DebugDestroy(0, UndercityStage.HubKind.Main);
            yield return WaitFor(() => stage.ServerPhase == "mainCleared" || stage.ServerPhase == "wave", 15, "Main HUB down");
            // HUV waves: shared health, hits through the server
            float waveTime = 0;
            bool bossShot = false;
            while (!(stage.ServerPhase == "ending" || stage.ServerPhase == "failed" || stage.OnlineResult != null) && waveTime < 120f)
            {
                stage.DebugHitHuv();
                yield return new WaitForSecondsRealtime(0.25f);
                waveTime += 0.25f;
                if (stage.Phase == UndercityStage.State.BossWave && !bossShot) { bossShot = true; Shot("partyplay_online_uc_boss.png"); }
            }
            yield return WaitFor(() => stage.OnlineResult != null, 30, "the server's result");
            Assert.IsTrue(stage.OnlineResult.B("cleared"), "cleared");
            yield return WaitFor(() => stage.ResultView, 10, "UCRESULT");
            var rows = stage.OnlineResult.L("rows");
            Assert.AreEqual(4, rows.Count);
            var mine = (Dictionary<string, object>)rows[0];
            Assert.Greater(mine.N("exp"), 0, "paid by the server");
            Assert.AreEqual("PTester", Text(stage.ResultView, "ID_UCRESULT_MYNAME1ST"));
            Assert.AreEqual("Pbot1", Text(stage.ResultView, "ID_UCRESULT_YOURNAME2ND"));
            yield return null;
            Shot("partyplay_online_uc_result.png");
            yield return WaitFor(() => !UndercityStage.Active, 20, "back in the city");
            yield return WaitFor(() => OnlineSession.Instance.Channel == homeChannel, 15, "back in the city channel");

            // ---- party patrol: level 85 for Eugene's World Patrol rows, the leader starts it for both ----
            server.StandardInput.WriteLine("setlevel PTester 85");
            yield return WaitFor(() => PlayerProfile.Current.Level == 85, 10, "level 85");
            var row = PartyRow(session.City, 85);
            if (row == null) { Debug.LogWarning("no party patrol row in " + session.City); yield break; }
            var patrol = PatrolMission.Instance;
            yield return new WaitForSecondsRealtime(1f);
            patrol.Begin(row);
            yield return WaitFor(() => patrol.PartyRun, 10, "party patrol started by the server");
            Assert.IsTrue(PartyWindow.PatrolShown);
            Assert.IsTrue(patrol.TimerView, "3880000");
            var post = patrol.Posts.Count > 0 ? patrol.Posts.IndexOf(patrol.NextPost) : 0;
            patrol.DebugPost(post);
            yield return WaitFor(() => patrol.Visited >= 1 && PartyWindow.Members.Any(m => m.Me && m.Posts == 1), 10, "my post (shared counter)");
            // the bot reports a post of the loop's far end every 3.2 s
            yield return WaitFor(() => PartyWindow.Members.Any(m => !m.Me && m.Posts >= 1), 15, "the bot's post");
            Assert.AreEqual($"Post {PartyWindow.Members.Sum(m => m.Posts)}", Text(PartyWindow.BarView, "ID_PARTY_COMBOCOUNT"));
            yield return new WaitForSeconds(0.2f);
            Shot("partyplay_online_patrol.png");
            patrol.GiveUp();   // the leader quits: 11159 for everyone
            yield return WaitFor(() => patrol.LastPartyEnd != null, 10, "the server ended the party patrol");
            Assert.AreEqual("leader", patrol.LastPartyEnd.S("reason"));
            var res = ui.Find(PatrolMission.PartyResultId);
            Assert.IsTrue(res);
            Assert.AreEqual("Pbot1", Text(res, "ID_PARTYPATROL_RESULT_NAME2ND"));
            yield return null;
            Shot("partyplay_online_patrol_result.png");
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
