using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DriftCity.Core;
using DriftCity.Game;
using DriftCity.Net;
using DriftCity.Social;
using DriftCity.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace DriftCity.Tests
{
    /// <summary>
    /// The leftover event screens and the Channel Battle screens offline (DriftCity_RE/EVENTS.md, SOCIAL.md section 6):
    /// GACHAINVEN pages, sale and compound; AUTUMN / THANKSGIVING / VALEN / "+N EXP" / the gift screen; the coupon stamp
    /// book; OBEVENT; the props NPC; the Halloween Drink; Pick'em over a spectated field; the channel list, panes, map
    /// setup / notify, part stakes, replays and the crew ladder. No city is loaded (the screens don't need one).
    /// Writes Preview/cbev_*.png.
    /// </summary>
    public class ChannelEventTests
    {
        string tempDir;

        [SetUp]
        public void SetUp()
        {
            var root = DriftCityData.ResolveGameRoot(@"D:\Drift_City_Remastered_Alpha_08-2025\Drift City Remastered Alpha");
            if (root == null) Assert.Ignore("game data not found");
            DriftCityData.Mount(root);
            GameTables.Load();
            UiManager.Ensure();
            if (!Camera.main) new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            tempDir = Path.Combine(Path.GetTempPath(), "driftcity_cbev_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            PlayerProfile.SaveDirOverride = tempDir;
            PlayerProfile.Slot = 0;
            var p = new PlayerProfile { Name = "Tester", Level = 40, Mito = 5_000_000, City = "cras" };
            p.Cars.Add(new OwnedCar { VehicleIndex = 0, Color = 1 });
            p.Save();
            PlayerProfile.Current = p;
            EventTime.Scale = 3f;
            EventNet.Rng = new System.Random(77);
            SeasonEvents.Override = EventSeason.Harvest | EventSeason.Valentine | EventSeason.Halloween | EventSeason.Indy4;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            SeasonEvents.Override = null;
            EventTime.Scale = 1f;
            PickEm.Field = null;
            PlayerProfile.SaveDirOverride = null;
            PlayerProfile.Current = null;
            foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Exclude).Where(g => g.transform.parent == null)) Object.Destroy(go);
            yield return new WaitForSecondsRealtime(0.3f);
            try { Directory.Delete(tempDir, true); } catch { }
        }

        static void Shot(string name)
        {
            Directory.CreateDirectory("Preview");
            var tex = UiManager.Instance.Capture(Camera.main);
            File.WriteAllBytes(Path.Combine("Preview", name), tex.EncodeToPNG());
            Object.Destroy(tex);
        }

        static IEnumerator WaitFor(System.Func<bool> condition, float timeout, string what)
        {
            float t = 0;
            while (!condition() && t < timeout) { t += Time.unscaledDeltaTime; yield return null; }
            Assert.IsTrue(condition(), "timed out waiting for " + what);
        }

        static void CloseAll()
        {
            for (int i = 0; i < 10 && UiPopup.IsOpen; i++) { var v = UiManager.Instance.Find(3250000); if (!v) break; UiManager.Instance.Close(v); }
            MissionUi.CloseScript(); EventPopup.CloseAll();
        }

        [UnityTest]
        public IEnumerator EventScreens()
        {
            var P = PlayerProfile.Current;
            yield return null;

            // ---------------- GACHAINVEN ----------------
            GachaInven.Open(0);
            yield return null;
            Assert.IsTrue(GachaInven.BaseView && GachaInven.PageView, "GACHAINVEN 4570000 + MAIN 4580000");
            Assert.AreEqual(GachaInven.MainDialog, GachaInven.PageView.Dialog.Id);
            Shot("cbev_gachainven_main.png");
            GachaInven.Turn(+1);
            yield return null;
            Assert.AreEqual(GachaInven.TuneDialog, GachaInven.PageView.Dialog.Id, "TUNNING");
            Shot("cbev_gachainven_tune.png");
            GachaInven.Turn(+1);
            Assert.AreEqual(GachaInven.MainDialog, GachaInven.PageView.Dialog.Id, "PartsGotchaClose: only MAIN and TUNNING");
            P.Give("i_n_dcbadge_01", 2);
            GachaInven.Open(3);
            yield return null;
            Assert.AreEqual(GachaInven.BadgeDialog, GachaInven.PageView.Dialog.Id);
            Shot("cbev_gachainven_badge.png");
            long mito = P.Mito;
            bool? sold = null;
            GachaInven.Sell("i_n_dcbadge_01", 2, ok => sold = ok);
            yield return WaitFor(() => sold.HasValue, 5, "badge sale");
            Assert.IsTrue(sold.Value);
            Assert.AreEqual(0, P.Count("i_n_dcbadge_01"));
            Assert.AreEqual(mito + 2L * (EventItems.Row("i_n_dcbadge_01")?.Sell ?? 0), P.Mito, "the resale price");
            GachaInven.Close();
            // the compound: parts A-D + Key Parts -> the gift box (column 3 of the key)
            foreach (var x in GachaInven.PartsFor("shadow")) P.Give(x, 1);
            P.Give("i_n_shadow_k_v5_05", 1);
            Assert.IsTrue(LuckyBag.IsBag("i_n_shadow_k_v5_05"), "Key Parts open through the events module");
            bool? combined = null;
            LuckyBag.Open("i_n_shadow_k_v5_05", ok => combined = ok);
            yield return WaitFor(() => combined.HasValue, 5, "compound");
            Assert.IsTrue(combined.Value);
            Assert.AreEqual(1, P.Count("i_n_shadow_box_v5"));
            Assert.AreEqual(0, P.Count("i_n_shadow_A"));
            CloseAll();

            // ---------------- AUTUMN 4670000 ----------------
            foreach (var (id, n) in Harvest.BoxIngredients) P.Give(id, n);
            P.Give(Harvest.Recipe, 1); P.Give(Harvest.NormalBox, 1);
            bool? made = null;
            LuckyBag.Open(Harvest.Recipe, ok => made = ok);
            yield return null;
            Assert.IsTrue(HarvestScreens.AutumnView, "AUTUMN opens from the Box Recipe");
            Shot("cbev_autumn.png");
            HarvestScreens.Make();
            yield return WaitFor(() => made.HasValue, 8, "box result");
            Assert.IsTrue(made.Value);
            int grade = HarvestScreens.AutumnGrade;
            Assert.IsTrue(HarvestScreens.AutumnView.Control("ID_AUTUMN_RESULT_" + new[] { "BAD", "GOOD", "BEST" }[grade]).gameObject.activeSelf);
            Assert.AreEqual(1, P.Count(Harvest.Boxes[2 - grade]), "the distribution box");
            Shot("cbev_autumn_result.png");
            HarvestScreens.CloseAutumn();

            // ---------------- THANKSGIVING 4680000 ----------------
            P.Give(Harvest.Receipt, 2);
            HarvestScreens.OpenThanks();
            yield return null;
            Assert.IsTrue(HarvestScreens.ThanksView);
            Shot("cbev_thanksgiving.png");
            HarvestScreens.Exchange();
            yield return WaitFor(() => HarvestScreens.Spinning, 5, "spin");
            yield return new WaitForSecondsRealtime(0.15f);
            Shot("cbev_thanksgiving_spin.png");
            yield return WaitFor(() => !HarvestScreens.Spinning, 8, "spin end");
            Assert.AreEqual(1, P.Count(Harvest.Receipt), "a receipt spent");
            yield return null;
            Shot("cbev_thanksgiving_prize.png");
            CloseAll();
            HarvestScreens.CloseThanks();

            // ---------------- VALEN 4660000, +N EXP 4690000, gift 4700000 ----------------
            P.Give("s_choco_recipe", 1); P.Give("black_chocolete", 3); P.Give("white_chocolete", 3); P.Give("pink_chocolete", 4); P.Give("careful_wrapping", 1);
            bool? wrapped = null;
            LuckyBag.Open("s_choco_recipe", ok => wrapped = ok);
            yield return null;
            Assert.IsTrue(HarvestScreens.ValenView, "VALEN craft mode");
            Shot("cbev_valen_recipe.png");
            HarvestScreens.Wrap();
            yield return WaitFor(() => wrapped.HasValue, 8, "basket");
            Assert.IsTrue(wrapped.Value);
            var (rc, paint, g) = HarvestScreens.Decode(HarvestScreens.ValenCode);
            Assert.AreEqual(2, rc, "an Upscale basket (s)"); Assert.AreEqual(0, paint, "Colorful Paint");
            Assert.IsTrue(g >= 1 && g <= 4);
            Assert.AreEqual(1, P.Count("s_make_giftbox"));
            Shot("cbev_valen_wrapped.png");
            HarvestScreens.CloseValen();
            CloseAll();
            bool? eaten = null;
            LuckyBag.Open("s_make_giftbox", ok => eaten = ok);
            yield return null;
            Assert.IsTrue(HarvestScreens.ValenView, "VALEN basket mode");
            Shot("cbev_valen_basket.png");
            HarvestScreens.OpenGift("s_make_giftbox");
            yield return null;
            Assert.IsTrue(HarvestScreens.GiftView, "EVENT 4700000");
            Shot("cbev_gift.png");
            HarvestScreens.CloseGift();
            HarvestScreens.Eat();
            yield return WaitFor(() => HarvestScreens.ExpView, 8, "+N EXP");
            Assert.AreEqual(0, P.Count("s_make_giftbox"));
            Shot("cbev_valen_exp.png");
            HarvestScreens.CloseValen();
            CloseAll();

            // ---------------- COUPONSTAMP 1910000 ----------------
            CouponStamp.Open();
            yield return null;
            Assert.IsTrue(CouponStamp.View);
            Assert.GreaterOrEqual(CouponStamp.RowCategories.Count, 8, "one row per category with coupons");
            var panda = CouponBook.Rows.First(r => r.CouponItem == "i_n_c0001");
            EventNet.State(P).Coupon[panda.Category] = panda.Need + 5;
            CouponStamp.Refresh();
            int row = CouponStamp.RowCategories.ToList().IndexOf(panda.Category), col = -1;
            for (int c = 0; c < 5; c++) if (CouponStamp.Cell(panda.Category, c) == panda) col = c;
            Assert.GreaterOrEqual(col, 0);
            Assert.IsTrue(CouponStamp.View.Control($"ID_COUPONSTAMP_SLOT{row}_COUPON_POS{col}").gameObject.activeSelf, "an earned coupon");
            Shot("cbev_couponstamp.png");
            bool? claimed = null;
            CouponStamp.Claim(row, col, ok => claimed = ok);
            yield return WaitFor(() => claimed.HasValue, 5, "stamp claim");
            Assert.IsTrue(claimed.Value);
            Assert.IsTrue(CouponStamp.View.Control($"ID_COUPONSTAMP_SLOT{row}_STAMP{col}").gameObject.activeSelf, "stamped");
            Assert.AreEqual(1, P.Count("i_n_c0001"));
            CouponStamp.Close();
            CloseAll();

            // ---------------- OBEVENT 3330000 ----------------
            Assert.AreEqual(39, ObEvent.Table.Length);
            ObEvent.Queue(16); ObEvent.Queue(32);
            yield return null;
            Assert.AreEqual(16, ObEvent.Showing);
            Shot("cbev_obevent.png");
            ObEvent.View.Click("ID_OBEVENT_OK");
            Assert.AreEqual(32, ObEvent.Showing, "OK shows the next queued prize");
            ObEvent.View.Click("ID_OBEVENT_OK");
            Assert.IsFalse(ObEvent.View, "and closes after the last");

            // ---------------- the props NPC (Indy4) ----------------
            Assert.AreEqual(80, PropEvent.Pool("cras").Count);
            var picked = PropEvent.Pick("koinonia", new System.Random(3));
            Assert.AreEqual(3, picked.Distinct().Count());
            for (int i = 0; i < 3; i++)
            {
                bool? f = null;
                PropEvent.Found(i, ok => f = ok);
                yield return WaitFor(() => f.HasValue, 5, "prop " + i);
                Assert.IsTrue(f.Value);
                CloseAll(); ObEvent.Close();
            }
            bool? npc = null;
            PropEvent.AtNpc(ok => npc = ok);
            yield return WaitFor(() => npc.HasValue, 5, "props NPC");
            Assert.IsTrue(npc.Value);
            Assert.AreEqual(1, P.Count("i_n_00054"), "the raffle ticket");
            Assert.AreEqual(0, P.Count("i_n_00051"));
            yield return null;
            Shot("cbev_props_npc.png");
            CloseAll(); ObEvent.Close();

            // ---------------- the Halloween Drink ----------------
            P.Give("i_n_halloween", 1);
            Assert.AreEqual(SpecialDrink.Describe("i_n_halloween"), ItemUse.Describe("i_n_halloween"));
            bool? drunk = null;
            ItemUse.Use(new BagEntry { Kind = BagKind.Stack, Id = "i_n_halloween", Count = 1 }, true, ok => drunk = ok);
            yield return WaitFor(() => drunk.HasValue, 5, "drink");
            Assert.IsTrue(drunk.Value);
            Assert.AreEqual(200, MitaDrink.Value, "the Special Drink effect");
            Assert.AreEqual(0, P.Count("i_n_halloween"));
        }

        [UnityTest]
        public IEnumerator PickEmOverASpectatedField()
        {
            var P = PlayerProfile.Current;
            P.Give(PickEm.Ticket, 1);
            var field = new List<PvpSpectate.Driver>();
            for (int i = 0; i < 6; i++) field.Add(new PvpSpectate.Driver { Name = "Racer" + i, Progress = 0.05f * (6 - i) });
            PickEm.Field = () => field;
            PickEm.Ensure();
            yield return WaitFor(() => PickEm.PromptShown, 5, "the Pick'em prompt");
            Shot("cbev_pickem_prompt.png");
            PickEm.Accept();
            yield return WaitFor(() => PickEm.Active, 5, "Pick'em running");
            Assert.AreEqual(0, P.Count(PickEm.Ticket), "the ticket is spent");
            Assert.AreEqual(6, PickEm.Symbols.Count);
            Assert.AreEqual(new[] { 1, 2, 3 }, PickEm.Symbols.Where(s => s > 0).OrderBy(s => s).ToArray(), "one gold, silver, bronze for 6 racers");
            CollectionAssert.AreEqual(new[] { "Racer0", "Racer1", "Racer2" }, PickEm.Picks.ToArray(), "the first three are picked");
            yield return null;
            PickEm.View.Click("ID_VIEWING_GACHA_SYMBOL_IDSELECTCHECK03");
            PickEm.View.Click("ID_VIEWING_GACHA_SYMBOL_IDSELECTCHECK05");
            CollectionAssert.AreEqual(new[] { "Racer0", "Racer1", "Racer4" }, PickEm.Picks.ToArray(), "unpick 3rd, pick 5th");
            yield return new WaitForSecondsRealtime(0.3f);
            Shot("cbev_pickem.png");
            // the finish: places 1..6 in list order
            int expect = PickEm.Grade(PickEm.Symbols.ToArray(), PickEm.Picks.ToList(), n => int.Parse(n.Substring(5)) + 1);
            for (int i = 0; i < field.Count; i++) field[i] = new PvpSpectate.Driver { Name = field[i].Name, Progress = 1f, Done = true, Place = i + 1 };
            yield return WaitFor(() => PickEm.LastGrade > 0, 5, "the slot ticket");
            Assert.AreEqual(expect, PickEm.LastGrade);
            Assert.AreEqual(1, P.Count("gacha_observe_v" + expect));
            Shot("cbev_pickem_done.png");
        }

        [UnityTest]
        public IEnumerator ChannelBattleScreensOffline()
        {
            var st = SocialStore.Current;
            st.MyCrew = new SocialStore.Crew { Name = "Testers", Mark = 3, Created = SocialUi.NowMs, State = "A" };
            bool loaded = false;
            Teams.Load(() => loaded = true);
            yield return WaitFor(() => loaded, 5, "crew");
            ChannelBattle.OpenChannels();
            yield return WaitFor(() => ChannelBattle.MajorView && ChannelBattle.PaneView, 5, "channel list");
            Assert.AreEqual(20, ChannelBattle.Channels.Count);
            Assert.AreEqual("cras-1", ChannelBattle.Get(0).Key);
            Assert.AreEqual(ChannelBattle.InfoDlg, ChannelBattle.PaneView.Dialog.Id, "the window is open: CHANNELINFO with ENTERCHANNEL");
            Shot("cbev_cb_channels.png");
            ChannelBattle.SetTab(false);
            yield return null;
            Assert.IsTrue(ChannelBattle.SemiView);
            Assert.AreEqual(10, ChannelBattle.Selected);
            Shot("cbev_cb_semi.png");
            StringAssert.Contains("Channel Battle in progress", ChannelBattle.ScheduleText());
            ChannelBattle.CloseChannels();
            ChannelBattle.OpenSetup(0);
            yield return null;
            Assert.IsTrue(ChannelBattle.SetupView, "CHANNELSETUP 2240000");
            Shot("cbev_cb_setup.png");
            ChannelBattle.SetupView.Close();
            ChannelBattle.OpenMapNotify(0);
            yield return null;
            Assert.IsTrue(ChannelBattle.NotifyView, "MAPNOTIFY 2250000");
            Shot("cbev_cb_mapnotify.png");
            ChannelBattle.NotifyView.Close();
            var part = new Dictionary<string, object> { ["type"] = 2.0, ["grade"] = 2.0, ["enhance"] = 3.0 };
            ChannelBattle.ShowItems(true, new List<Dictionary<string, object>> { part, part });
            ChannelBattle.ShowItems(false, new List<Dictionary<string, object>> { part });
            yield return null;
            Assert.IsTrue(ChannelBattle.ItemGetView && ChannelBattle.ItemMissView);
            Shot("cbev_cb_items.png");
            ChannelBattle.ItemGetView.Close(); ChannelBattle.ItemMissView.Close();
            ChannelBattle.OpenReplays();
            yield return null;
            Assert.IsTrue(ChannelBattle.ReplayView);
            Shot("cbev_cb_replays.png");
            ChannelBattle.ReplayView.Close();
            ChannelBattle.OpenCrewLadder();
            yield return null;
            Assert.IsTrue(ChannelBattle.LadderView);
            Assert.AreEqual("Testers", ChannelBattle.LadderRows[0].S("name"));
            Shot("cbev_cb_ladder.png");
            ChannelBattle.CloseLadder();
        }
    }

    /// <summary>
    /// Online against the local Node server (DC_CB_SCHEDULE=always, DC_EVENTS=halloween,indy4): a crew holds a vacant
    /// channel from the DEF lobby (WAIT16 steps aside), the table and the spectate list show it; the Halloween Drink from
    /// the daily attendance through event.drink; the props NPC through props.find / props.claim. Writes Preview/cbev_online_*.png.
    /// </summary>
    public class ChannelEventOnlineTests
    {
        const int Port = 7891;
        static string Url => $"ws://127.0.0.1:{Port}";
        Process server;
        string tempDir;
        static string ServerDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "DriftCityServer"));

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            if (!File.Exists(Path.Combine(ServerDir, "server.js"))) Assert.Ignore("DriftCityServer not found next to the Unity project");
            tempDir = Path.Combine(Path.GetTempPath(), "driftcity_cbon_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            PlayerProfile.SaveDirOverride = tempDir;
            PlayerProfile.Slot = 0;
            PlayerProfile.Current = null;
            EventTime.Scale = 2f;
            var psi = new ProcessStartInfo("node", "server.js") { WorkingDirectory = ServerDir, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
            psi.EnvironmentVariables["DC_PORT"] = Port.ToString();
            psi.EnvironmentVariables["DC_DB"] = Path.Combine(tempDir, "cb.db");
            psi.EnvironmentVariables["DC_EVENTS"] = "halloween,indy4";
            psi.EnvironmentVariables["DC_CB_SCHEDULE"] = "always";
            server = Process.Start(psi);
            server.OutputDataReceived += (_, e) => { if (e.Data != null) Debug.Log("[node] " + e.Data); };
            server.ErrorDataReceived += (_, e) => { if (e.Data != null) Debug.Log("[node err] " + e.Data); };
            server.BeginOutputReadLine(); server.BeginErrorReadLine();
            float t = 0; bool up = false;
            while (!up && t < 15f)
            {
                try { using (var c = new System.Net.Sockets.TcpClient()) { c.Connect("127.0.0.1", Port); up = true; } } catch { }
                if (!up) { yield return new WaitForSecondsRealtime(0.25f); t += 0.25f; }
            }
            Assert.IsTrue(up, "server started");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            EventTime.Scale = 1f;
            if (OnlineSession.Instance) OnlineSession.Instance.Logout();
            try { if (server != null && !server.HasExited) server.Kill(); } catch { }
            PlayerProfile.SaveDirOverride = null;
            foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Exclude).Where(g => g.transform.parent == null)) Object.Destroy(go);
            yield return new WaitForSecondsRealtime(0.5f);
            try { Directory.Delete(tempDir, true); } catch { }
        }

        void Console(string line) { server.StandardInput.WriteLine(line); server.StandardInput.Flush(); }

        static IEnumerator WaitFor(System.Func<bool> condition, float timeout, string what)
        {
            float t = 0;
            while (!condition() && t < timeout) { t += Time.unscaledDeltaTime; yield return null; }
            Assert.IsTrue(condition(), "timed out waiting for " + what);
        }

        static void Shot(string name)
        {
            Directory.CreateDirectory("Preview");
            var tex = UiManager.Instance.Capture(Camera.main);
            File.WriteAllBytes(Path.Combine("Preview", name), tex.EncodeToPNG());
            Object.Destroy(tex);
        }

        static void CloseScripts()
        {
            for (int i = 0; i < 10 && UiPopup.IsOpen; i++) { var v = UiManager.Instance.Find(3250000); if (!v) break; UiManager.Instance.Close(v); }
            MissionUi.CloseScript(); ObEvent.Close();
        }

        [UnityTest]
        public IEnumerator HoldAVacantChannelAndEventItems()
        {
            if (!Camera.main) new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            var flow = new GameObject("Game").AddComponent<GameFlow>();
            flow.SkipParkingLot = true; flow.SkipIntro = true;
            yield return FrontEndSteps.ToCharacterSelect(flow, Url, "cbtester", "secret123");
            yield return WaitFor(() => UiManager.Instance.Find(3250000), 15, "create-account popup");
            UiManager.Instance.Find(3250000).Click(UiPopup.Yes);
            yield return FrontEndSteps.NewCharacter("cbtester", 1);
            UiManager.Instance.Find(150000).Click("ID_CREATECAR_NEW_NEXT");
            yield return WaitFor(() => PlayerProfile.Current != null && PlayerProfile.Current.OnlineDriver, 15, "server driver");
            yield return WaitFor(() => UiManager.Instance.Find(320000) && OnlineSession.Instance.Channel != null, 600, "city HUD");
            CitySession.Current.MissionOfferInterval = 1e6f;
            System.Func<PlayerProfile> P = () => PlayerProfile.Current;
            string name = P().Name;
            Console($"setlevel {name} 25");
            Console($"givemito {name} 100000");
            yield return WaitFor(() => P().Level == 25 && P().Mito >= 100000, 10, "admin level / Mito");

            // a crew (level 20, 2500 Mito)
            bool? made = null;
            Teams.Create("CBTesters", 3, ok => made = ok);
            yield return WaitFor(() => made.HasValue, 10, "crew.create");
            Assert.IsTrue(made.Value);
            CloseScripts();
            var madeView = UiManager.Instance.Find(Teams.MadeDlg);
            if (madeView) UiManager.Instance.Close(madeView);

            // the channel list from crew.table; the city channel we stand in
            ChannelBattle.OpenChannels();
            yield return WaitFor(() => ChannelBattle.PaneView, 10, "channel list");
            Assert.AreEqual(20, ChannelBattle.Channels.Count);
            var ch = ChannelBattle.Channels.First(c => c.Key == OnlineSession.Instance.Channel);
            if (ch.Semi) ChannelBattle.SetTab(false, ch.Index); else ChannelBattle.Select(ch.Index);
            Assert.IsTrue(ch.Active && ChannelBattle.WindowOpen, "DC_CB_SCHEDULE=always");
            Shot("cbev_online_channels.png");

            // entering the vacant channel: the crew hosts its room and holds it (DEF lobby; WAIT16 steps aside)
            bool? entered = null;
            ChannelBattle.Enter(ch.Index, ok => entered = ok);
            yield return WaitFor(() => entered.HasValue, 10, "crew.enter");
            Assert.IsTrue(entered.Value);
            yield return WaitFor(() => ChannelBattle.LobbyView && ChannelBattle.BattleRoom != null, 10, "the battle room");
            Assert.AreEqual(ChannelBattle.DefDlg, ChannelBattle.LobbyView.Dialog.Id, "the holder defends");
            yield return new WaitForSecondsRealtime(0.6f);
            Assert.IsFalse(PvpWaitRoom.View && PvpWaitRoom.View.gameObject.activeSelf, "WAIT16 steps aside");
            Shot("cbev_online_lobby.png");
            bool loaded = false;
            ChannelBattle.Load(() => loaded = true);
            yield return WaitFor(() => loaded, 10, "crew.table");
            Assert.AreEqual("CBTesters", ChannelBattle.Get(ch.Index).Holder?.Name, "the vacant channel's holder");
            Assert.AreEqual(ChannelBattle.BattleRoom.Id, ChannelBattle.Get(ch.Index).Room);
            // the spectate list (0x388) shows the channel
            ChannelBattle.OpenSpectate();
            yield return WaitFor(() => ChannelBattle.SpectateView, 10, "SELCH_VIEWING");
            Shot("cbev_online_spectate.png");
            ChannelBattle.SpectateView.Close();
            ChannelBattle.LeaveLobby();
            yield return WaitFor(() => ChannelBattle.BattleRoom == null, 10, "left the room");
            ChannelBattle.CloseChannels();

            // the Halloween Drink from the attendance gift -> event.drink -> the Special Drink effect
            yield return WaitFor(() => P().Count("i_n_halloween") > 0, 30, "the Halloween Drink");
            bool? drunk = null;
            SpecialDrink.Use("i_n_halloween", ok => drunk = ok);
            yield return WaitFor(() => drunk.HasValue, 10, "event.drink");
            Assert.IsTrue(drunk.Value);
            Assert.AreEqual(200, MitaDrink.Value);
            Assert.AreEqual(0, P().Count("i_n_halloween"));

            // the props NPC (indy4): the server gives the props and the reward
            for (int i = 0; i < 3; i++)
            {
                bool? f = null;
                PropEvent.Found(i, ok => f = ok);
                yield return WaitFor(() => f.HasValue, 10, "props.find");
                Assert.IsTrue(f.Value);
                CloseScripts();
            }
            bool? claimed = null;
            PropEvent.AtNpc(ok => claimed = ok);
            yield return WaitFor(() => claimed.HasValue, 10, "props.claim");
            Assert.IsTrue(claimed.Value);
            Assert.AreEqual(1, P().Count("i_n_00054"));
            CloseScripts();
        }
    }
}
