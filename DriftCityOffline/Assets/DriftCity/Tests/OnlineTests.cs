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
    /// Online flow against the real Node server (DriftCityServer, needs node on PATH) with one bot driver:
    /// account creation on the original login dialog, driver creation, channel join, seeing the bot's car,
    /// whisper chat and a server-checked reward. Writes Preview/online_*.png.
    /// </summary>
    public class OnlineTests
    {
        const int Port = 7871;
        static string Url => $"ws://127.0.0.1:{Port}";
        Process server, bot;
        string tempDir;

        static string ServerDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "DriftCityServer"));

        static Process Node(string args, Dictionary<string, string> env = null)
        {
            var psi = new ProcessStartInfo("node", args)
            {
                WorkingDirectory = ServerDir, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            if (env != null) foreach (var kv in env) psi.EnvironmentVariables[kv.Key] = kv.Value;
            var p = Process.Start(psi);
            p.OutputDataReceived += (_, e) => { if (e.Data != null) Debug.Log("[node] " + e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) Debug.Log("[node err] " + e.Data); };
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            return p;
        }

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            if (!File.Exists(Path.Combine(ServerDir, "server.js"))) Assert.Ignore("DriftCityServer not found next to the Unity project");
            tempDir = Path.Combine(Path.GetTempPath(), "driftcity_online_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            PlayerProfile.SaveDirOverride = tempDir;
            PlayerProfile.Slot = 0;
            PlayerProfile.Current = null;
            Directory.CreateDirectory("Preview");
            server = Node("server.js", new Dictionary<string, string> { ["DC_PORT"] = Port.ToString(), ["DC_DB"] = Path.Combine(tempDir, "test.db") });
            float t = 0;
            bool up = false;
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
            foreach (var p in new[] { bot, server }) try { if (p != null && !p.HasExited) p.Kill(); } catch { }
            PlayerProfile.SaveDirOverride = null;
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
            var tex = UiManager.Instance.Capture(Camera.main);
            File.WriteAllBytes(Path.Combine("Preview", name), tex.EncodeToPNG());
        }

        [UnityTest]
        public IEnumerator OnlineDriverJoinsChannelAndSeesBot()
        {
            if (!Camera.main) new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            var flow = new GameObject("Game").AddComponent<GameFlow>();
            flow.SkipParkingLot = true; flow.SkipIntro = true;   // straight to the city; ParkingLotGateEntersCity drives through the parking lot
            yield return FrontEndSteps.ToCharacterSelect(flow, Url, "tester", "secret123");

            // unknown account -> "create a new account?" popup
            yield return WaitFor(() => UiManager.Instance.Find(3250000), 15, "create-account popup");
            UiManager.Instance.Find(3250000).Click(UiPopup.Yes);
            // a new account has no driver: the empty character screen, then CREATECHARACTER
            yield return FrontEndSteps.NewCharacter("tester", 1);
            Assert.IsTrue(OnlineSession.Active, "logged in");

            UiManager.Instance.Find(150000).Click("ID_CREATECAR_NEW_NEXT");
            yield return WaitFor(() => PlayerProfile.Current != null && PlayerProfile.Current.OnlineDriver, 15, "server driver");
            Assert.AreEqual("tester", PlayerProfile.Current.Name);
            Assert.IsFalse(PlayerProfile.Exists, "online driver is not written to the offline save");

            yield return WaitFor(() => UiManager.Instance.Find(320000), 600, "city HUD");
            yield return WaitFor(() => OnlineSession.Instance.Channel != null, 15, "channel join");
            Assert.IsTrue(ChatBox.Instance, "chat box open");

            // the bot orbits us once it has our position
            yield return WaitFor(() => OnlineSession.Instance.Remotes && OnlineSession.Instance.Remotes.Count > 0, 15, "bot listed");
            var me = flow.PlayerCar.transform;
            yield return WaitFor(() => OnlineSession.Instance.Remotes.All.Any(r => r && Vector3.Distance(r.transform.position, me.position) < 80f), 20, "bot car near the player");
            var remote = OnlineSession.Instance.Remotes.All.First();
            Debug.Log($"Drift City online: remote {remote.name} at {remote.transform.position}, me at {me.position}");
            yield return new WaitForSeconds(1.5f);
            Shot("online_2_city_with_bot.png");

            // whisper round trip through the server
            string reply = null;
            System.Action<string, string, string> onChat = (scope, from, text) => { if (scope == "whisper" && from == "Bot1") reply = text; };
            OnlineSession.Instance.ChatReceived += onChat;
            OnlineSession.Instance.SubmitChat("/w Bot1 hello");
            yield return WaitFor(() => reply != null, 10, "bot whisper reply");
            OnlineSession.Instance.ChatReceived -= onChat;
            yield return null;
            Shot("online_3_chat.png");

            // rewards are computed and checked by the server
            long mitoBefore = PlayerProfile.Current.Mito;
            Progress.Result? result = null;
            Progress.Reward("chase", new Dictionary<string, object> { ["boss"] = false, ["seconds"] = 40.0 }, () => new Progress.Result { Ok = false, Error = "offline path used" }, r => result = r);
            yield return WaitFor(() => result.HasValue, 10, "reward reply");
            Assert.IsTrue(result.Value.Ok, result.Value.Error);
            Assert.Greater(PlayerProfile.Current.Mito, mitoBefore, "server granted mito");
            result = null;
            Progress.Reward("chase", new Dictionary<string, object> { ["boss"] = false, ["seconds"] = 40.0 }, () => default, r => result = r);
            yield return WaitFor(() => result.HasValue, 10, "second reward reply");
            Assert.IsFalse(result.Value.Ok, "cooldown enforced by the server");

            // custom shop, counter and parts shop through the server
            var myCar = GameTables.Car(PlayerProfile.Current.Car.VehicleIndex);
            var plate = PartsCatalog.For(myCar, PartSlot.Plate).First();
            Progress.Result? buy = null;
            Progress.BuyPart(PlayerProfile.Current.CurrentCar, plate, r => buy = r);
            yield return WaitFor(() => buy.HasValue, 10, "buy part reply");
            Assert.IsTrue(buy.Value.Ok, buy.Value.Error);
            Assert.AreEqual(plate.Id, PlayerProfile.Current.Car.Plate, "server fitted the plate");

            // the chase reward above may have dropped an enhancer already (35%)
            int enhancers = PlayerProfile.Current.Count("i_n_00003");
            Progress.Result? op = null;
            Progress.Op("item.buy", new Dictionary<string, object> { ["id"] = "i_n_00003", ["count"] = 1 }, () => default, r => op = r);
            yield return WaitFor(() => op.HasValue, 10, "counter reply");
            Assert.IsTrue(op.Value.Ok, op.Value.Error);
            Assert.AreEqual(enhancers + 1, PlayerProfile.Current.Count("i_n_00003"));
            op = null;
            Progress.Op("perf.buy", new Dictionary<string, object> { ["type"] = 1, ["grade"] = 0 }, () => default, r => op = r);
            yield return WaitFor(() => op.HasValue, 10, "parts reply");
            Assert.IsTrue(op.Value.Ok, op.Value.Error);
            var part = PlayerProfile.Current.PerfParts.Single();
            op = null;
            Progress.Op("perf.enhance", new Dictionary<string, object> { ["uid"] = part.Uid, ["enhancer"] = "i_n_00003" }, () => default, r => op = r);
            yield return WaitFor(() => op.HasValue, 10, "enhance reply");
            Assert.AreEqual(1, PlayerProfile.Current.PerfParts.Single().Enhance, "server enhanced the part (first level always succeeds)");
            Assert.AreEqual(enhancers, PlayerProfile.Current.Count("i_n_00003"), "enhancer used up on the server");
        }
    }
}
