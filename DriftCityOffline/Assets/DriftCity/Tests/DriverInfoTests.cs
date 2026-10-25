using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DriftCity.Game;
using DriftCity.Net;
using DriftCity.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;

namespace DriftCity.Tests
{
    /// <summary>
    /// Driver info (USERINFO 1810000 / USERINFO2ND 1830000), the chat modes and whispers, MSGBOX 3230000, the Mittron
    /// fuel tank with the filling station (CHARGER 940000) and the Battle Zone charger (CHARGERPVP 960000)
    /// (DriftCity_RE/SCREENS.md). Writes Preview/screens_*.png.
    /// </summary>
    public class DriverInfoTests
    {
        string tempSave;

        [SetUp]
        public void SetUp()
        {
            tempSave = Path.Combine(Path.GetTempPath(), "driftcity_screens_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempSave);
            PlayerProfile.SaveDirOverride = tempSave;
            PlayerProfile.Slot = 0;
            var p = new PlayerProfile { Name = "Infoer", Level = 23, Exp = 123456, Mito = 50000, City = "cras", Avatar = 6, PvpRaces = 41, PvpWins = 17, DistanceKm = 812.4f, MountedLicence = -1 };
            p.Cars.Add(new OwnedCar { VehicleIndex = 0, Color = 2 });
            p.Save();
            PlayerProfile.Current = null;
            MittronFuel.Reset();
            MissionRecords.Reset();
            Directory.CreateDirectory("Preview");
        }

        [TearDown]
        public void TearDown()
        {
            PlayerProfile.SaveDirOverride = null;
            MittronFuel.Reset();
            MissionRecords.Reset();
            PvpCharger.Declined = false;
            MittronCharger.GateLayout = false;
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
            Debug.Log("Drift City screens: wrote Preview/" + name + ".png");
        }

        static string Text(NuiView v, string id) => v.Control(id)?.GetComponentInChildren<UnityEngine.UI.Text>(true)?.text ?? "";

        [Test]
        public void PvpChargerSpendsSmallestFirstLikeTheClient()
        {
            // 0x5c2a10: need / litres + 1 of the first kind that covers it, else all of it and go on
            CollectionAssert.AreEqual(new[] { 3, 0, 0 }, PvpCharger.Plan(5, new[] { 10, 4, 1 }), "5 L: 5/2+1 = 3 small");
            CollectionAssert.AreEqual(new[] { 2, 3, 0 }, PvpCharger.Plan(30, new[] { 2, 9, 1 }), "2 small (4 L), then 26/10+1 = 3 large");
            CollectionAssert.AreEqual(new[] { 0, 0, 1 }, PvpCharger.Plan(90, new[] { 0, 0, 2 }), "nothing spent yet: the 300 L can covers 90 L");
            CollectionAssert.AreEqual(new[] { 1, 0, 0 }, PvpCharger.Plan(90, new[] { 1, 0, 2 }), "after a small can, 300 L no longer fits the 88 L missing");
        }

        [UnityTest]
        public IEnumerator DriverInfoChatMsgBoxAndChargersInCras()
        {
            new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            var flow = new GameObject("Game").AddComponent<GameFlow>();
            flow.SkipParkingLot = true;
            yield return FrontEndSteps.ToCharacterSelect(flow);
            yield return FrontEndSteps.EnterSelected();
            yield return WaitFor(() => flow.InCity && CitySession.Current && flow.PlayerCar, 600, "city");
            if (CitySession.Current.Traffic) CitySession.Current.Traffic.MaxCars = 0;
            yield return new WaitForSeconds(2f);
            GuideTextDriver.Ensure();
            MittronFuelMeter.Ensure();
            var ui = UiManager.Instance;
            var p = PlayerProfile.Current;

            // ---------------- USERINFO 1810000 ----------------
            Menus.MyInfo();
            yield return null;
            var own = ui.Find(DriverInfoScreen.OwnDialog);
            Assert.IsNotNull(own, "MYINFO opens USERINFO");
            Assert.AreEqual("Infoer", Text(own, "ID_USERINFO_NAME"));
            Assert.AreEqual(" 23", Text(own, "ID_USERINFO_LEVEL"), "%3u");
            Assert.AreEqual("123,456", Text(own, "ID_USERINFO_EXP"));
            StringAssert.Contains("41", Text(own, "ID_USERINFO_BATTLERECORD_1ST"));
            StringAssert.Contains("17", Text(own, "ID_USERINFO_BATTLERECORD_1ST"));
            StringAssert.Contains("(Ind.)", Text(own, "ID_USERINFO_BATTLERECORD_1ST"), "10159");
            StringAssert.Contains("(Team)", Text(own, "ID_USERINFO_BATTLERECORD_2ND"), "10160");
            StringAssert.Contains("812", Text(own, "ID_USERINFO_TOTALKM"), "10729 %u Km");
            Assert.AreEqual("-", Text(own, "ID_USERINFO_LADDERRECORD"), "no crew: '-' under the cover");
            Assert.IsTrue(own.Control("ID_USERINFO_LADDERRECORD_COVER").gameObject.activeSelf);
            Assert.IsFalse(own.Control("ID_USERINFO_TEAMROO").gameObject.activeSelf, "OMD driver");
            Assert.IsFalse(own.Control("ID_USERINFO_RACINGTEAM").gameObject.activeSelf, "no crew name");
            var portrait = own.Control("ID_USERINFO_IMG_POS").transform.Find("portrait").GetComponent<UnityEngine.UI.RawImage>();
            Assert.IsNotNull(portrait.texture, "the USERCHAR cell over IMG_POS");
            StringAssert.Contains("256,128~384,256", portrait.texture.name, "portrait 6 = USERCHAR INDEX05");
            yield return null;
            Shot("screens_userinfo_own");
            OriginalMenus.Close();
            yield return null;
            // a Team ROO driver gets the ROO strips
            p.Faction = Factions.Roo;
            Menus.MyInfo();
            yield return null;
            own = ui.Find(DriverInfoScreen.OwnDialog);
            Assert.IsTrue(own.Control("ID_USERINFO_TEAMROO").gameObject.activeSelf);
            Assert.IsTrue(own.Control("ID_USERINFO_TIMEATTACK").gameObject.activeSelf);
            yield return null;
            Shot("screens_userinfo_roo");
            OriginalMenus.Close();
            p.Faction = Factions.Omd;
            yield return null;

            // ---------------- USERINFO2ND 1830000 (an AI driver offline) ----------------
            bool shown = false;
            DriverInfoScreen.Other("rio", ok => shown = ok);
            Assert.IsTrue(shown);
            var other = ui.Find(DriverInfoScreen.OtherDialog);
            Assert.IsNotNull(other);
            Assert.AreEqual("Rio", Text(other, "ID_USERINFO2ND_NAME"));
            Assert.AreEqual("***,***,***", Text(other, "ID_USERINFO2ND_MITO"), "another driver's Mito is masked (0x54c770)");
            Assert.IsFalse(other.Control("ID_USERINFO2ND_JOIN_RANCINGTEAM").Interactable, "no crew to join");
            Assert.IsTrue(DriverInfoScreen.CarShown, "the driver's car in the dark panel");
            other.Click("ID_USERINFO2ND_CAMERA_LEFT");
            yield return null;
            yield return null;
            Shot("screens_userinfo_other");
            // ADDFRIEND asks 10162 first
            other.Click("ID_USERINFO2ND_ADDFRIEND");
            yield return null;
            var confirm = ui.Find(3250000);
            Assert.IsNotNull(confirm, "10162 Add to Friend List?");
            confirm.Click(UiPopup.No);
            other.Click("ID_USERINFO2ND_CLOSE");
            yield return null;
            Assert.IsNull(ui.Find(DriverInfoScreen.OtherDialog));
            Assert.IsFalse(DriverInfoScreen.CarShown, "the car view goes with the card");

            // ---------------- MSGBOX 3230000 ----------------
            string named = null;
            var box = MsgBox.Prompt(MissionText.Get("10539"), s => named = s, "", MsgBox.Confirm.FriendAdd);
            Assert.IsNotNull(box);
            Assert.IsTrue(box.Control("ID_MSGBOX_TYPE1_FRIEND_ADD").gameObject.activeSelf);
            Assert.IsFalse(box.Control("ID_MSGBOX_TYPE1_OK").gameObject.activeSelf);
            box.Control("ID_MSGBOX_TYPE1_TEXT").Input.text = "  Duke ";
            yield return null;
            Shot("screens_msgbox");
            box.Click("ID_MSGBOX_TYPE1_FRIEND_ADD");
            yield return null;
            Assert.AreEqual("Duke", named);
            Assert.IsFalse(MsgBox.IsOpen);

            // ---------------- chat modes and whispers ----------------
            var chat = ChatBox.Instance;
            Assert.IsNotNull(chat);
            var cv = ui.Root.GetComponentsInChildren<NuiView>(true).First(v => v.Dialog != null && v.Dialog.Id == 260000);
            Assert.IsTrue(cv.Control("ID_CHAT_ADV_FIX_NORMAL").gameObject.activeSelf);
            cv.Click("ID_CHAT_ADV_FIX_NORMAL");
            yield return null;
            Assert.IsTrue(chat.DropdownOpen);
            Assert.IsTrue(cv.Control("ID_CHAT_ADV_DROPDOWN_PARTY").gameObject.activeSelf);
            yield return null;
            Shot("screens_chat_dropdown");
            cv.Click("ID_CHAT_ADV_DROPDOWN_PARTY");
            yield return null;
            Assert.AreEqual(ChatBox.ChatMode.Party, chat.Mode);
            Assert.IsFalse(chat.DropdownOpen);
            Assert.IsTrue(cv.Control("ID_CHAT_ADV_FIX_PARTY").gameObject.activeSelf);
            Assert.IsFalse(cv.Control("ID_CHAT_ADV_FIX_NORMAL").gameObject.activeSelf);
            Assert.AreEqual("&hello", chat.Outgoing("hello"));
            Assert.AreEqual("hello", chat.Outgoing("!hello"), "! = channel");
            Assert.AreEqual("*Rio hi", chat.Outgoing("*Rio hi"), "a prefix wins over the mode");
            chat.SetMode(ChatBox.ChatMode.Team);
            Assert.AreEqual("@go", chat.Outgoing("go"));
            chat.SetMode(ChatBox.ChatMode.Private);
            Assert.AreEqual(ChatBox.ChatMode.Team, chat.Mode, "private channels: 10745, mode unchanged");
            chat.Add("whisper", "Mika", "hey there");
            Assert.AreEqual("*Mika ", chat.Prefill, "reply to the last whisperer");
            Assert.AreEqual("Mika", chat.WhisperTarget);
            chat.SetMode(ChatBox.ChatMode.Whisper);
            Assert.AreEqual("*Mika see you", chat.Outgoing("see you"));
            chat.Add("whisper", "Infoer", "(to Mika) see you");
            chat.SetMode(ChatBox.ChatMode.Normal);
            yield return null;
            Shot("screens_chat_whisper");

            // ---------------- the fuel tank ----------------
            Assert.AreEqual(MittronFuel.DefaultCapacity, MittronFuel.Litres, 0.01f, "a car never seen comes full");
            MittronFuel.Consume(5000f);
            Assert.AreEqual(99f, MittronFuel.Litres, 0.01f, "5 km at 5 km/L");
            MittronFuel.Set(0f);
            Assert.IsTrue(MittronFuel.Empty);
            Assert.AreEqual(MittronFuel.EmptyTopSpeedKmh, MittronFuel.TopSpeedCapKmh(flow.PlayerCar), "empty tank: 150 km/h (0x476f90)");
            MittronFuel.Set(30f);
            Assert.AreEqual(float.MaxValue, MittronFuel.TopSpeedCapKmh(flow.PlayerCar));
            // driving burns it
            float before = MittronFuel.Litres;
            var car = flow.PlayerCar;
            var start = car.transform.position;
            float drive = 0f;
            var rb = car.GetComponent<Rigidbody>();
            while (drive < 2f)
            {
                if (rb) rb.linearVelocity = car.transform.forward * 12f; else car.transform.position += car.transform.forward * 0.2f;
                drive += Time.deltaTime;
                yield return null;
            }
            Assert.Less(MittronFuel.Litres, before, "the meter burns fuel while the car moves");

            if (rb) { rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
            yield return new WaitForSeconds(0.5f);

            // ---------------- CHARGER 940000 ----------------
            MittronFuel.Set(30f);
            long mito = p.Mito;
            var ch = MittronCharger.Open();
            Assert.IsNotNull(ch);
            Assert.IsFalse(ch.Control("ID_CHARGER_CHRAGE").Interactable, "nothing to buy yet (0x54f900)");
            ch.Click("ID_CHARGER_50L");
            ch.Click("ID_CHARGER_50L");
            Assert.AreEqual(70, MittronCharger.Amount, "clamped to the room left");
            ch.Click("ID_CHARGER_MINUS");
            Assert.AreEqual(69, MittronCharger.Amount);
            Assert.AreEqual("30L (+69L) / 100L", Text(ch, "ID_CHARGER_GAUGETEXT"));
            Assert.AreEqual("690", Text(ch, "ID_CHARGER_SELL_PRICE"));
            StringAssert.Contains("Owned by", Text(ch, "ID_CHARGER_OWNTEAM"), "10169");
            yield return null;
            Shot("screens_charger");
            ch.Click("ID_CHARGER_CHRAGE");
            yield return null;
            Assert.AreEqual(99f, MittronFuel.Litres, 0.01f);
            Assert.AreEqual(mito - 690, p.Mito);
            ch.Click("ID_CHARGER_FULL");
            Assert.AreEqual(1, MittronCharger.Amount);
            ch.Click("ID_CHARGER_ESC");
            yield return null;
            Assert.IsFalse(MittronCharger.IsOpen);
            // the Gate skin
            MittronCharger.GateLayout = true;
            MittronFuel.Set(40f);
            ch = MittronCharger.Open();
            ch.Click("ID_CHARGER4GATE_20L");
            yield return null;
            Shot("screens_charger4gate");
            MittronCharger.Close();
            MittronCharger.GateLayout = false;

            // ---------------- CHARGERPVP 960000 ----------------
            MittronFuel.Set(15f);
            Assert.IsTrue(MittronFuel.Low);
            Assert.IsFalse(PvpCharger.OfferIfLow(), "no fuel items: no dialog");
            yield return WaitFor(() => GuideText.Shown == GuideKind.FuelWarning, 2f, "FUELWARNING instead (0x5c2c30)");
            p.Give("i_n_00004", 3);
            p.Give("i_n_00005", 9);
            p.Give("i_n_00006", 1);
            Assert.IsTrue(PvpCharger.OfferIfLow());
            var pvp = ui.Find(PvpCharger.Dialog);
            Assert.AreEqual("Small X 3", Text(pvp, "ID_CHARGERPVP_COUNT_S"));
            Assert.AreEqual("Large X 9", Text(pvp, "ID_CHARGERPVP_COUNT_L"));
            Assert.AreEqual("Super large X 1", Text(pvp, "ID_CHARGERPVP_COUNT_XL"));
            yield return null;
            Shot("screens_charger_pvp");
            pvp.Click("ID_CHARGERPVP_USE");
            yield return null;
            // 85 L missing: 3 small (6 L) then 79/10+1 = 8 large
            Assert.AreEqual(0, p.Count("i_n_00004"));
            Assert.AreEqual(1, p.Count("i_n_00005"));
            Assert.AreEqual(1, p.Count("i_n_00006"));
            Assert.AreEqual(100f, MittronFuel.Litres, 0.01f);
            MittronFuel.Set(10f);
            Assert.IsTrue(PvpCharger.OfferIfLow());
            ui.Find(PvpCharger.Dialog).Click("ID_CHARGERPVP_ESC");
            Assert.IsTrue(PvpCharger.Declined);
            Assert.IsFalse(PvpCharger.OfferIfLow(), "ESC: not asked again this session");
            MittronFuel.Set(100f);
        }
    }

    /// <summary>The same screens against the local server: driver.info for the bot, fuel.buy charged by the server, whispers.</summary>
    public class DriverInfoOnlineTests
    {
        const int Port = 7873;
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
            tempDir = Path.Combine(Path.GetTempPath(), "driftcity_screens_online_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            PlayerProfile.SaveDirOverride = tempDir;
            PlayerProfile.Slot = 0;
            PlayerProfile.Current = null;
            MittronFuel.Reset();
            Directory.CreateDirectory("Preview");
            server = Node("server.js", new Dictionary<string, string> { ["DC_PORT"] = Port.ToString(), ["DC_DB"] = Path.Combine(tempDir, "test.db") });
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

        [UnityTest]
        public IEnumerator OnlineDriverInfoFuelAndWhisper()
        {
            if (!Camera.main) new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            var flow = new GameObject("Game").AddComponent<GameFlow>();
            flow.SkipParkingLot = true; flow.SkipIntro = true;
            yield return FrontEndSteps.ToCharacterSelect(flow, Url, "infotester", "secret123");
            yield return WaitFor(() => UiManager.Instance.Find(3250000), 15, "create-account popup");
            UiManager.Instance.Find(3250000).Click(UiPopup.Yes);
            yield return FrontEndSteps.NewCharacter("infotester", 1);
            UiManager.Instance.Find(150000).Click("ID_CREATECAR_NEW_NEXT");
            yield return WaitFor(() => PlayerProfile.Current != null && PlayerProfile.Current.OnlineDriver, 15, "server driver");
            yield return WaitFor(() => UiManager.Instance.Find(320000), 600, "city HUD");
            yield return WaitFor(() => OnlineSession.Instance.Channel != null, 15, "channel join");
            yield return WaitFor(() => OnlineSession.Instance.Remotes && OnlineSession.Instance.Remotes.Count > 0, 15, "bot listed");
            var ui = UiManager.Instance;

            // the bot's card from the server
            bool? shown = null;
            DriverInfoScreen.Other("Bot1", ok => shown = ok);
            yield return WaitFor(() => shown.HasValue, 10, "driver.info reply");
            Assert.IsTrue(shown.Value);
            var other = ui.Find(DriverInfoScreen.OtherDialog);
            Assert.IsNotNull(other);
            Assert.AreEqual("Bot1", DriverInfoScreen.Shown.Name);
            Assert.IsTrue(DriverInfoScreen.Shown.Online);
            yield return null;
            File.WriteAllBytes(Path.Combine("Preview", "screens_userinfo_online.png"), ui.Capture(Camera.main).EncodeToPNG());
            DriverInfoScreen.CloseOther();

            // fuel.buy: the server takes the Mito
            MittronFuel.Set(50f);
            long mito = PlayerProfile.Current.Mito;
            var ch = MittronCharger.Open();
            ch.Click("ID_CHARGER_20L");
            bool? bought = null;
            MittronCharger.Buy(ok => bought = ok);
            yield return WaitFor(() => bought.HasValue, 10, "fuel.buy reply");
            Assert.IsTrue(bought.Value);
            Assert.AreEqual(mito - 20 * MittronFuel.PricePerLitre, PlayerProfile.Current.Mito, "server charged 10 Mito a litre");
            Assert.AreEqual(70f, MittronFuel.Litres, 0.01f);
            MittronCharger.Close();

            // whisper round trip through the chat box: echo 10474, reply 10475, the input keeps "*Bot1 "
            var chat = ChatBox.Instance;
            string got = null;
            System.Action<string, string, string> onChat = (scope, from, text) => { if (scope == "whisper" && from == "Bot1") got = text; };
            OnlineSession.Instance.ChatReceived += onChat;
            chat.Submit("*Bot1 hello");
            Assert.AreEqual("*Bot1 ", chat.Prefill);
            Assert.AreEqual("Bot1", chat.WhisperTarget);
            yield return WaitFor(() => got != null, 10, "bot whisper reply");
            Assert.AreEqual("*Bot1 ", chat.Prefill, "reply to the last whisperer");
            OnlineSession.Instance.ChatReceived -= onChat;
            yield return null;
            File.WriteAllBytes(Path.Combine("Preview", "screens_chat_online.png"), ui.Capture(Camera.main).EncodeToPNG());
        }
    }
}
