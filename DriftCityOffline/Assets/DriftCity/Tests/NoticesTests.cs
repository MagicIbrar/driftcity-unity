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
using Object = UnityEngine.Object;

namespace DriftCity.Tests
{
    /// <summary>
    /// Server notices (DriftCity_RE/HANDLERS.md, Net/ServerNotices.cs): EMERGENCY 2770000 for 12 s, the SCROLL 2780000
    /// marquee, the system message box, the level-up line, server texts (globaltext_server.xlt), kick texts and the
    /// server clock; online against the real Node server, driven from its console (stdin admin commands).
    /// Writes Preview/notices_*.png.
    /// </summary>
    public class NoticesTests
    {
        const int Port = 7881;
        Process server;
        string tempDir;

        static string ServerDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "DriftCityServer"));

        [SetUp]
        public void SetUp()
        {
            var root = DriftCityData.ResolveGameRoot(@"D:\Drift_City_Remastered_Alpha_08-2025\Drift City Remastered Alpha");
            if (root == null) Assert.Ignore("game data not found");
            DriftCityData.Mount(root);
            GameTables.Load();
            UiManager.Ensure();
            if (!Camera.main) new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            tempDir = Path.Combine(Path.GetTempPath(), "driftcity_notices_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            PlayerProfile.SaveDirOverride = tempDir;
            PlayerProfile.Slot = 0;
            PlayerProfile.Current = null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (OnlineSession.Instance) OnlineSession.Instance.Logout();
            try { if (server != null && !server.HasExited) server.Kill(); } catch { }
            server = null;
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
            Directory.CreateDirectory("Preview");
            var tex = UiManager.Instance.Capture(Camera.main);
            File.WriteAllBytes(Path.Combine("Preview", name), tex.EncodeToPNG());
            Object.Destroy(tex);
        }

        static Dictionary<string, object> Msg(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        [UnityTest]
        public IEnumerator NoticeKindsShowLikeTheClient()
        {
            ChatBox.Ensure();
            var n = ServerNotices.Ensure();

            // server texts: globaltext.xlt first, then globaltext_server.xlt (20000-21999)
            Assert.AreEqual("Chat has been blocked..", ServerNotices.Lookup("20305"));
            Assert.AreEqual("Exit by server.", ServerNotices.Lookup("10989"));
            Assert.AreEqual("Bob has reached level 7.", MissionText.Printf(ServerNotices.Lookup("10054"), "Bob", 7));
            Assert.AreEqual("50 Channel Tournament completed.", ServerNotices.Text(Msg("key", "20210", "args", new List<object> { 50.0 })));
            Assert.AreEqual("plain", ServerNotices.Text(Msg("key", "NO_SUCH_KEY", "text", "plain")));

            // EMERGENCY 2770000: the small text for 12 s (flag 0), the large one with flag 1
            ServerNotices.Handle(Msg("kind", "emergency", "text", "The server will restart in 5 minutes.", "seconds", 12.0));
            Assert.IsTrue(n.EmergencyShown, "EMERGENCY shown");
            Assert.AreEqual("The server will restart in 5 minutes.", ServerNotices.LastEmergency);
            yield return null;
            Shot("notices_emergency.png");
            ServerNotices.Handle(Msg("kind", "emergency", "text", "Double EXP!", "seconds", 1.0, "large", true));
            yield return null;
            Shot("notices_emergency_large.png");
            yield return WaitFor(() => !n.EmergencyShown, 3f, "notice timeout");

            // SCROLL 2780000: one pass per text, right to left
            ServerNotices.Handle(Msg("kind", "scroll", "text", "Welcome to Drift City! Rush Time starts at 20:00."));
            ServerNotices.Handle(Msg("kind", "scroll", "text", "Second line"));
            Assert.IsTrue(n.ScrollShown, "SCROLL shown");
            Assert.AreEqual(1, n.ScrollQueued, "second text waits");
            yield return new WaitForSecondsRealtime(3f);
            Shot("notices_scroll.png");

            // op 0x008: message box + system line; op 0x168: the level-up line
            ServerNotices.Handle(Msg("kind", "system", "key", "20307"));
            Assert.AreEqual("You've received new Items!", ServerNotices.LastPopup);
            Assert.IsTrue(UiManager.Instance.Find(3250000), "message box");
            ServerNotices.Handle(Msg("kind", "level", "name", "Bob", "level", 7.0));
            yield return null;
            Shot("notices_popup.png");
            UiManager.Instance.Find(3250000).Click("ID_POPUP_OKONLY");

            // op 0x09c texts for our kick reasons
            Assert.AreEqual("Exit by server.", ServerNotices.KickText(""));
            Assert.AreEqual("Someone from a different location is trying to log-in to your account.", ServerNotices.KickText("logged in from another place"));
            Assert.AreEqual("You've been kicked.", ServerNotices.KickText("kicked by admin"));
            Assert.AreEqual("Server is currently undergoing maintenance.", ServerNotices.KickText("maintenance"));
            Assert.AreEqual("too many connections from your address", ServerNotices.KickText("too many connections from your address"));

            // the server clock
            ServerNotices.SetClock((System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) + 3_600_000.0);
            Assert.IsTrue(ServerNotices.ClockSynced);
            Assert.That((ServerNotices.UtcNow - System.DateTime.UtcNow).TotalMinutes, Is.InRange(59.0, 61.0));
            ServerNotices.SetClock(System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        [UnityTest]
        public IEnumerator ServerConsoleNoticesReachTheClient()
        {
            if (!File.Exists(Path.Combine(ServerDir, "server.js"))) Assert.Ignore("DriftCityServer not found next to the Unity project");
            var psi = new ProcessStartInfo("node", "server.js")
            {
                WorkingDirectory = ServerDir, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            };
            psi.EnvironmentVariables["DC_PORT"] = Port.ToString();
            psi.EnvironmentVariables["DC_DB"] = Path.Combine(tempDir, "test.db");
            server = Process.Start(psi);
            server.OutputDataReceived += (_, e) => { if (e.Data != null) Debug.Log("[node] " + e.Data); };
            server.ErrorDataReceived += (_, e) => { if (e.Data != null) Debug.Log("[node err] " + e.Data); };
            server.BeginOutputReadLine(); server.BeginErrorReadLine();
            bool up = false;
            for (float t = 0; !up && t < 15f; t += 0.25f)
            {
                try { using (var c = new System.Net.Sockets.TcpClient()) { c.Connect("127.0.0.1", Port); up = true; } } catch { }
                if (!up) yield return new WaitForSecondsRealtime(0.25f);
            }
            Assert.IsTrue(up, "server started");

            ChatBox.Ensure();
            ServerNotices.ResetClock();
            var s = OnlineSession.Ensure();
            bool? logged = null, created = null;
            s.ConnectAndLogin($"ws://127.0.0.1:{Port}", "noticer", "secret123", true, (ok, err, hasDriver) => logged = ok);
            yield return WaitFor(() => logged.HasValue, 15, "login");
            Assert.IsTrue(logged.Value, "registered");
            s.CreateDriver("Noticer", 0, 1, 0, (ok, err) => created = ok);
            yield return WaitFor(() => created.HasValue, 15, "driver");
            Assert.IsTrue(created.Value, "driver created");
            Assert.IsTrue(ServerNotices.Instance, "hooked by OnlineSession.Ensure");

            yield return WaitFor(() => ServerNotices.ClockSynced, 5, "server clock");
            void Console(string line) { server.StandardInput.WriteLine(line); server.StandardInput.Flush(); }
            Console("notice Server maintenance at 06:00 UTC.");
            yield return WaitFor(() => ServerNotices.LastEmergency == "Server maintenance at 06:00 UTC.", 5, "emergency notice");
            Assert.IsTrue(ServerNotices.Instance.EmergencyShown);
            Console("scroll Rush Time tonight!");
            yield return WaitFor(() => ServerNotices.LastScroll == "Rush Time tonight!", 5, "scroll notice");
            Console("say Hello from the console");   // world.announce: chat scope notice -> EMERGENCY too
            yield return WaitFor(() => ServerNotices.LastEmergency == "Hello from the console", 5, "announce -> emergency");
            yield return new WaitForSecondsRealtime(1f);
            Shot("notices_online.png");
            Console("popup @Noticer Please read your mail.");
            yield return WaitFor(() => ServerNotices.LastPopup == "Please read your mail.", 5, "popup");
            UiManager.Instance.Find(3250000)?.Click("ID_POPUP_OKONLY");
            Console("kick Noticer");
            yield return WaitFor(() => ServerNotices.LastKick == "You've been kicked.", 5, "kick text");
            yield return WaitFor(() => !OnlineSession.Active, 5, "disconnected");
        }
    }
}
