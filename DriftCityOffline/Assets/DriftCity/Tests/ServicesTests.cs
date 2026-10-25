using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DriftCity.Game;
using DriftCity.Game.Services;
using DriftCity.Net;
using DriftCity.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;

namespace DriftCity.Tests
{
    /// <summary>
    /// The original server's remaining systems (DriftCity_RE/SERVICES.md, Game/Services/*): private channels and the chat's
    /// Private mode, rental cars, driver rename (NICNAME 3770000), recommend-a-driver, crew-owned Mittron stations, the
    /// station's INSTANT button / event delivery pair, the VIEWMAP city pages, the personal shop. Offline in Cras, then
    /// online against the local server with a bot. Writes Preview/services_*.png.
    /// </summary>
    public class ServicesTests
    {
        string tempSave;

        [SetUp]
        public void SetUp()
        {
            tempSave = Path.Combine(Path.GetTempPath(), "driftcity_services_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempSave);
            PlayerProfile.SaveDirOverride = tempSave;
            PlayerProfile.Slot = 0;
            var p = new PlayerProfile { Name = "Servicer", Level = 30, Mito = 9000000, City = "cras", Avatar = 2 };
            p.Cars.Add(new OwnedCar { VehicleIndex = 0, Color = 1 });
            p.Save();
            PlayerProfile.Current = null;
            MittronFuel.Reset();
            PrivateChannel.Reset();
            RentalCars.ResetCatalog();
            Directory.CreateDirectory("Preview");
        }

        [TearDown]
        public void TearDown()
        {
            PlayerProfile.SaveDirOverride = null;
            MittronFuel.Reset();
            QuestUnlocks.Enabled = true;
            foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Exclude).Where(g => g.transform.parent == null)) Object.Destroy(go);
            try { Directory.Delete(tempSave, true); } catch { }
        }

        static IEnumerator WaitFor(System.Func<bool> condition, float timeout, string what)
        {
            float t = 0;
            while (!condition() && t < timeout) { t += Time.unscaledDeltaTime; yield return null; }
            Assert.IsTrue(condition(), "timed out waiting for " + what);
        }

        static void Shot(string name)
        {
            var tex = UiManager.Instance.Capture(Camera.main);
            File.WriteAllBytes(Path.Combine("Preview", name + ".png"), tex.EncodeToPNG());
            Object.Destroy(tex);
            Debug.Log("Drift City services: wrote Preview/" + name + ".png");
        }

        static string Text(NuiView v, string id) => v.Control(id)?.GetComponentInChildren<UnityEngine.UI.Text>(true)?.text ?? "";

        static void ClosePopups()
        {
            var ui = UiManager.Instance;
            for (int i = 0; i < 5; i++) { var pop = ui.Find(3250000); if (!pop) break; ui.Close(pop); }
        }

        [UnityTest]
        public IEnumerator OfflineServicesInCras()
        {
            new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            var flow = new GameObject("Game").AddComponent<GameFlow>();
            flow.SkipParkingLot = true;
            yield return FrontEndSteps.ToCharacterSelect(flow);
            yield return FrontEndSteps.EnterSelected();
            yield return WaitFor(() => flow.InCity && CitySession.Current && flow.PlayerCar, 600, "city");
            if (CitySession.Current.Traffic) CitySession.Current.Traffic.MaxCars = 0;
            yield return new WaitForSeconds(1f);
            var ui = UiManager.Instance;
            var p = PlayerProfile.Current;
            ServicesHost.Ensure();

            // ---------------- switches offline ----------------
            Assert.IsFalse(ServerSwitches.PrivateChat, "private chat needs a server");
            Assert.IsTrue(ServerSwitches.Instant);
            Assert.IsTrue(ServerSwitches.Rentals);

            // ---------------- private channel offline: 10745 ----------------
            var chat = ChatBox.Ensure();
            chat.Submit("/cjoin Room1");
            Assert.AreEqual("", PrivateChannel.Current);
            chat.SetMode(ChatBox.ChatMode.Private);
            Assert.AreNotEqual(ChatBox.ChatMode.Private, chat.Mode, "offline the Private mode answers 10745");
            Assert.IsFalse(PrivateChannel.TryHandle("hello", false), "an ordinary line is not a private one");

            // ---------------- the filling station of an owned channel ----------------
            MittronFuel.Set(40f);
            var ch = MittronCharger.Open();
            Assert.IsNotNull(ch);
            Social.ChannelBattle.Load();
            var cras1 = Social.ChannelBattle.Channels.First(c => c.Key == "cras-1");
            Assert.IsNotNull(cras1.Owner, "offline AI crews hold every third channel (cras-1 is channel 1)");
            StringAssert.Contains(cras1.Owner.Name, Text(ch, "ID_CHARGER_OWNTEAM"), "10168 Owned by: %s Crew");
            yield return null;
            Shot("services_charger_owned");
            MittronCharger.Close();

            // ---------------- rental cars ----------------
            Assert.Greater(RentalCars.Catalog.Count, 30, "visualitem RENTCAR rows with vshopitem prices");
            var time = RentalCars.Catalog.First(r => r.Kind == 1);
            var usage = RentalCars.Catalog.First(r => r.Kind == 2);
            Assert.AreEqual(600000, time.Periods.First(x => x.days == 7).mito, "$600 x 1000 Mito");
            RentalCars.Open();
            yield return null;
            Assert.IsTrue(ListWindow.IsOpen, "the rental categories");
            Shot("services_rental_tabs");
            ListWindow.CloseOpen();
            int cars = p.Cars.Count;
            bool? rented = null;
            RentalCars.Rent(time, 7, ok => rented = ok);
            Assert.IsTrue(rented.Value);
            Assert.AreEqual(cars + 1, p.Cars.Count);
            Assert.AreEqual(1, p.Car.RentKind);
            Assert.AreEqual(time.Grade, p.Car.Grade);
            Assert.AreEqual(9000000 - 600000, p.Mito);
            Assert.IsNotNull(RentalCars.SellRefusal(p.Car), "10950");
            Assert.IsNull(RentalCars.RefuelRefusal(p.Car), "10959: a time-limited rental refuels");
            RentalCars.Rent(usage, usage.Periods[0].days, ok => rented = ok);
            Assert.AreEqual(2, p.Car.RentKind);
            Assert.IsNull(MittronCharger.Open(), "10953: a mileage-limited rental is never refilled");
            ClosePopups();
            // the time-limited rental runs out (not the current car): 10969
            p.Cars.First(c => c.RentKind == 1).RentUntil = 1;
            yield return new WaitForSeconds(6.5f);
            Assert.AreEqual(time.Name, RentalCars.LastReturned);
            Assert.IsFalse(p.Cars.Any(c => c.RentKind == 1));
            yield return null;
            Shot("services_rental_returned");
            ClosePopups();
            // the mileage-limited one goes back when its tank is dry: 10968 (the driver's car)
            MittronFuel.Set(0f);
            yield return new WaitForSeconds(1.5f);
            Assert.AreEqual(usage.Name, RentalCars.LastReturned);
            Assert.IsFalse(p.Cars.Any(c => c.RentKind > 0));
            Assert.AreEqual(cars, p.Cars.Count);
            ClosePopups();
            MittronFuel.Set(100f);

            // ---------------- driver rename (NICNAME 3770000) ----------------
            Assert.IsNull(DriverRename.Open(), "no kit: the buy question");
            ClosePopups();
            bool? kit = null;
            DriverRename.BuyKit(ok => kit = ok);
            Assert.IsTrue(kit.Value);
            Assert.AreEqual(1, p.Count(DriverRename.Kit));
            var nic = DriverRename.Open();
            Assert.IsNotNull(nic);
            DriverRename.Type("New_Name");
            Assert.IsFalse(nic.Control("ID_NICNAME_ACCEPT").Interactable, "ACCEPT waits for the check");
            yield return null;
            Shot("services_nicname");
            bool? checkedOk = null;
            DriverRename.Check("New_Name", ok => checkedOk = ok);
            Assert.IsTrue(checkedOk.Value, "10605");
            ClosePopups();
            Assert.IsTrue(nic.Control("ID_NICNAME_ACCEPT").Interactable);
            bool? renamed = null;
            DriverRename.Accept(ok => renamed = ok);
            Assert.IsTrue(renamed.Value);
            Assert.AreEqual("New_Name", p.Name);
            Assert.AreEqual(0, p.Count(DriverRename.Kit), "the kit is spent");
            Assert.Greater(DriverRename.LockedDays, 58, "60 days before the next change");
            ClosePopups();

            // ---------------- the VIEWMAP city pages ----------------
            QuestUnlocks.Enabled = false;   // every city open
            Menus.WorldMap();
            yield return null;
            Assert.IsNotNull(ui.Find(500000), "Cras's page");
            ui.Find(500000).Click("ID_VIEWMAP_CR_RIGHT");
            yield return null;
            Assert.IsNotNull(ui.Find(510000), "RIGHT: Oros");
            Assert.AreEqual("oros", WorldMapPages.Current);
            ui.Find(510000).Click("ID_VIEWMAP_OR_LEFT");
            ui.Find(500000).Click("ID_VIEWMAP_CR_LEFT");
            yield return null;
            Assert.IsNotNull(ui.Find(490000), "LEFT twice: Koinonia");
            Shot("services_viewmap_koinonia");
            Menus.WorldMap();
            yield return null;
            Assert.IsNull(ui.Find(490000), "M again closes the map");
            QuestUnlocks.Enabled = true;
            p.Level = 1; p.StepQuestsDone.Clear();
            Assert.IsNull(WorldMapPages.Step("moonpalace", +1), "Koinonia locked for a level 1 driver: 0x4e8b, the page stays");
            Assert.AreEqual("moonpalace", WorldMapPages.Step("koinonia", -1), "LEFT has no lock");
            p.Level = 30;

            // ---------------- personal shop offline ----------------
            PersonalShop.Open("rio");
            yield return null;
            Assert.IsTrue(ListWindow.IsOpen);
            ListWindow.CloseOpen();
        }
    }

    /// <summary>The services against the local server with a bot.</summary>
    public class ServicesOnlineTests
    {
        const int Port = 7881;
        static string Url => $"ws://127.0.0.1:{Port}";
        Process server, bot;
        string tempDir;
        static string ServerDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "DriftCityServer"));

        static Process Node(string args, Dictionary<string, string> env = null)
        {
            var psi = new ProcessStartInfo("node", args) { WorkingDirectory = ServerDir, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (env != null) foreach (var kv in env) psi.EnvironmentVariables[kv.Key] = kv.Value;
            var pr = Process.Start(psi);
            pr.OutputDataReceived += (_, e) => { if (e.Data != null) Debug.Log("[node] " + e.Data); };
            pr.ErrorDataReceived += (_, e) => { if (e.Data != null) Debug.Log("[node err] " + e.Data); };
            pr.BeginOutputReadLine(); pr.BeginErrorReadLine();
            return pr;
        }

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            if (!File.Exists(Path.Combine(ServerDir, "server.js"))) Assert.Ignore("DriftCityServer not found next to the Unity project");
            tempDir = Path.Combine(Path.GetTempPath(), "driftcity_services_online_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            PlayerProfile.SaveDirOverride = tempDir;
            PlayerProfile.Slot = 0;
            PlayerProfile.Current = null;
            MittronFuel.Reset();
            PrivateChannel.Reset();
            Recommend.Reset();
            Directory.CreateDirectory("Preview");
            server = Node("server.js", new Dictionary<string, string> { ["DC_PORT"] = Port.ToString(), ["DC_DB"] = Path.Combine(tempDir, "test.db"), ["DC_EVENT_DELIVERY"] = "on" });
            float t = 0; bool up = false;
            while (!up && t < 15f)
            {
                try { using (var c = new System.Net.Sockets.TcpClient()) { c.Connect("127.0.0.1", Port); up = true; } } catch { }
                if (!up) { yield return new WaitForSecondsRealtime(0.25f); t += 0.25f; }
            }
            Assert.IsTrue(up, "server started");
            bot = Node($"tools/bot.js 1 --url {Url}");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (OnlineSession.Instance) OnlineSession.Instance.Logout();
            foreach (var pr in new[] { bot, server }) try { if (pr != null && !pr.HasExited) pr.Kill(); } catch { }
            PlayerProfile.SaveDirOverride = null;
            MittronFuel.Reset();
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

        static IEnumerator Reply(string type, Dictionary<string, object> body, List<Dictionary<string, object>> into)
        {
            Dictionary<string, object> got = null;
            ServicesNet.Request(type, body, r => got = r ?? new Dictionary<string, object>());
            float t = 0;
            while (got == null && t < 10f) { t += Time.unscaledDeltaTime; yield return null; }
            Assert.IsNotNull(got, "reply to " + type);
            into.Add(got);
        }

        [UnityTest]
        public IEnumerator OnlineServices()
        {
            if (!Camera.main) new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            var flow = new GameObject("Game").AddComponent<GameFlow>();
            flow.SkipParkingLot = true; flow.SkipIntro = true;
            yield return FrontEndSteps.ToCharacterSelect(flow, Url, "svctester", "secret123");
            yield return WaitFor(() => UiManager.Instance.Find(3250000), 15, "create-account popup");
            UiManager.Instance.Find(3250000).Click(UiPopup.Yes);
            yield return FrontEndSteps.NewCharacter("svctester", 1);
            UiManager.Instance.Find(150000).Click("ID_CREATECAR_NEW_NEXT");
            yield return WaitFor(() => PlayerProfile.Current != null && PlayerProfile.Current.OnlineDriver, 15, "server driver");
            yield return WaitFor(() => UiManager.Instance.Find(320000), 600, "city HUD");
            yield return WaitFor(() => OnlineSession.Instance.Channel != null, 15, "channel join");
            var ui = UiManager.Instance;
            ServicesHost.Ensure();

            // ---------------- switches from the server ----------------
            ServerSwitches.Refresh();
            yield return WaitFor(() => ServerSwitches.Known, 10, "features");
            Assert.IsTrue(ServerSwitches.PrivateChat);
            Assert.IsTrue(ServerSwitches.EventDelivery, "DC_EVENT_DELIVERY=on");

            // ---------------- recommend-a-driver: the MSGBOX on the first city ----------------
            yield return WaitFor(() => Recommend.Prompting && MsgBox.IsOpen, 10, "the recommend prompt (10181)");
            StringAssert.Contains("recommended", MsgBox.View.Control("ID_MSGBOX_TYPE1_INFO").GetComponentInChildren<UnityEngine.UI.Text>(true).text);
            yield return null;
            File.WriteAllBytes(Path.Combine("Preview", "services_recommend.png"), ui.Capture(Camera.main).EncodeToPNG());
            MsgBox.Close();

            // ---------------- private channel through the chat ----------------
            var chat = ChatBox.Ensure();
            var lines = new List<string>();
            System.Action<string, string, string> onChat = (scope, from, text) => { if (scope == "private") lines.Add(from + ":" + text); };
            OnlineSession.Instance.ChatReceived += onChat;
            chat.Submit("/cjoin Room7");
            yield return WaitFor(() => PrivateChannel.Current == "Room7", 10, "joined Room7");
            chat.SetMode(ChatBox.ChatMode.Private);
            Assert.AreEqual(ChatBox.ChatMode.Private, chat.Mode, "the Private mode with the server's private chat");
            chat.Submit("hello private");
            yield return WaitFor(() => lines.Any(l => l.EndsWith("hello private")), 10, "own private line");
            chat.Submit("-Room7 dash line");
            yield return WaitFor(() => lines.Any(l => l.EndsWith("dash line")), 10, "-channel line");
            chat.Submit("/cwho");
            yield return WaitFor(() => PrivateChannel.LastWho.Count == 1, 10, "/cwho");
            Assert.AreEqual(PlayerProfile.Current.Name, PrivateChannel.LastWho[0]);
            chat.Submit("/cjoin Other");
            Assert.AreEqual("Room7", PrivateChannel.Current, "11175: one channel at a time");
            yield return null;
            File.WriteAllBytes(Path.Combine("Preview", "services_private_chat.png"), ui.Capture(Camera.main).EncodeToPNG());
            chat.Submit("/cleave");
            yield return WaitFor(() => PrivateChannel.Current == "", 10, "left");
            OnlineSession.Instance.ChatReceived -= onChat;
            chat.SetMode(ChatBox.ChatMode.Normal);

            // ---------------- the station: station.info / station.fill ----------------
            MittronFuel.Set(50f);
            long mito = PlayerProfile.Current.Mito;
            var chg = MittronCharger.Open();
            Assert.IsNotNull(chg);
            yield return new WaitForSecondsRealtime(0.5f);
            chg.Click("ID_CHARGER_20L");
            bool? bought = null;
            MittronCharger.Buy(ok => bought = ok);
            yield return WaitFor(() => bought.HasValue, 10, "station.fill");
            Assert.IsTrue(bought.Value);
            Assert.AreEqual(mito - 200, PlayerProfile.Current.Mito, "the server charged 10 Mito a litre");
            MittronCharger.Close();

            // ---------------- rentals, rename check, personal shop ----------------
            var rr = new List<Dictionary<string, object>>();
            yield return Reply("rental.list", null, rr);
            Assert.IsTrue(rr[0].B("ok"));
            Assert.Greater(rr[0].L("rentals").Count, 30);
            bool? check = null;
            DriverRename.Check("Bot1", ok => check = ok);
            yield return WaitFor(() => check.HasValue, 10, "rename.check");
            Assert.IsFalse(check.Value, "10604: the bot has that name");
            ui.Close(ui.Find(3250000));
            bool? listed = null;
            PersonalShop.Load("Bot1", ok => listed = ok);
            yield return WaitFor(() => listed.HasValue, 10, "pshop.list");
            Assert.IsTrue(listed.Value);
            Assert.AreEqual("Bot1", PersonalShop.LastName);
        }
    }
}
