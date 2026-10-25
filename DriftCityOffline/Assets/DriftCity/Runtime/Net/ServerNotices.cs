using System;
using System.Collections.Generic;
using DriftCity.Core;
using DriftCity.Game;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Net
{
    /// <summary>
    /// The original client's reactions to server-originated texts (DriftCity_RE/HANDLERS.md), for our server's
    /// <c>{t:"notice", kind, text|key, args, ...}</c> (src/notices.js), <c>{t:"time", now}</c> and <c>{t:"kicked", reason}</c>:
    /// <list type="bullet">
    /// <item>emergency: EMERGENCY 2770000 in the middle of the screen for 12 s (op 0x093, a channel line starting with
    /// "notice" -> ui+0x284 Show(12.0, text, 0) 0x54d850; flag 1 = ID_EMERGENCY_NOTICE_LARGE_TEXT) and the line in the chat.
    /// Our server's "say"/"announce" chat lines (scope notice) show there too.</item>
    /// <item>scroll: SCROLL 2780000, ID_SCROLL_NOTICE crossing the screen right to left at 70 px/s, one pass per queued
    /// text (op 0x093 scope "bcast", ui+0x288 0x5524d0 / update 0x5523a0).</item>
    /// <item>system: the POPUP 3250000 message box plus the system chat line (op 0x008, 0x50c990).</item>
    /// <item>chat / gm: a system chat line (0x5b5160) / a GM line (op 0x093 scope "GM").</item>
    /// <item>level: 10054 "%s has reached level %d." for another driver (op 0x168 -> 0x697a90).</item>
    /// <item>event: the event reward popup SEASONEVENT 3340000 (op 0x089 generic subtypes); obtain: 10096 "Obtained %d %s"
    /// with SID_ITEM_DROP_GET1 (op 0x089 subtypes 7/14/18).</item>
    /// <item>kicked: the reason, or 10989 "Exit by server." (op 0x09c, 0x4fc330); our reasons map to the original texts.</item>
    /// <item>time: the server clock (op 0x003 kept net+0x118); <see cref="UtcNow"/> is the server's UTC for the weather
    /// and Rush Time clocks.</item>
    /// </list>
    /// Keys are globaltext.xlt entries, else localize\globaltext_server.xlt (20000-21999, the server's own texts).
    /// </summary>
    public sealed class ServerNotices : MonoBehaviour
    {
        public const int EmergencyDialog = 2770000, ScrollDialog = 2780000;
        public const float NoticeSeconds = 12f;     // the 12.0 (0x41400000) op 0x093 passes
        public const float ScrollSpeed = 70f;       // 0x85e758, pixels per second
        /// <summary>GM line colour when the server gives none (op 0x093 takes it from the packet).</summary>
        public const string GmColor = "#7cf2ff";

        public static ServerNotices Instance { get; private set; }

        static TimeSpan clockOffset;
        /// <summary>True once the server sent its clock.</summary>
        public static bool ClockSynced { get; private set; }
        /// <summary>The server's UTC (the local clock until the server sent its own).</summary>
        public static DateTime UtcNow => DateTime.UtcNow + clockOffset;

        /// <summary>The last notice texts shown (tests, tools).</summary>
        public static string LastEmergency { get; private set; }
        public static string LastScroll { get; private set; }
        public static string LastPopup { get; private set; }
        public static string LastKick { get; private set; }

        static bool kickPending;
        NetClient hooked;
        NuiView emergency, scroll;
        float emergencyUntil;
        readonly Queue<string> scrollQueue = new Queue<string>();
        RectTransform scrollRect;
        float scrollBaseX, scrollStartAt;
        bool scrolling;

        // ---------------- hook ----------------
        /// <summary>Called by OnlineSession.Ensure: listens to the server's notice / time messages and the chat.</summary>
        public static ServerNotices Hook(NetClient net)
        {
            var n = Ensure(net ? net.gameObject : null);
            if (n.hooked == net) return n;
            if (n.hooked) n.hooked.Message -= n.OnMessage;
            n.hooked = net;
            if (net) net.Message += n.OnMessage;
            return n;
        }

        public static ServerNotices Ensure(GameObject host = null)
        {
            if (Instance) return Instance;
            if (!host) { host = new GameObject("Server Notices"); DontDestroyOnLoad(host); }
            Instance = host.AddComponent<ServerNotices>();
            return Instance;
        }

        void OnEnable()
        {
            var s = OnlineSession.Instance;
            if (s) s.ChatReceived += OnChat;
        }

        void OnDisable()
        {
            var s = OnlineSession.Instance;
            if (s) s.ChatReceived -= OnChat;
        }

        void OnDestroy()
        {
            if (hooked) hooked.Message -= OnMessage;
            if (emergency) Destroy(emergency.gameObject);
            if (scroll) Destroy(scroll.gameObject);
            if (Instance == this) Instance = null;
        }

        void OnMessage(string t, Dictionary<string, object> m)
        {
            if (t == "notice") Handle(m);
            else if (t == "time") SetClock(m.N("now"));
        }

        /// <summary>Our server's announcements arrive as chat scope "notice": like the client's "notice..." channel lines.</summary>
        void OnChat(string scope, string from, string text)
        {
            if (scope == "notice" && !string.IsNullOrEmpty(text)) Emergency(text, NoticeSeconds, false, chat: false);
        }

        // ---------------- messages ----------------
        /// <summary>Shows one notice message the way the client did (see the class summary).</summary>
        public static void Handle(Dictionary<string, object> m)
        {
            if (m == null) return;
            var n = Ensure();
            string text = Text(m);
            switch (m.S("kind"))
            {
                case "emergency": n.Emergency(text, (float)m.N("seconds", NoticeSeconds), m.B("large")); break;
                case "scroll": n.Scroll(text); break;
                case "system": Popup(text); break;
                case "chat": Chat("system", "", text); break;
                case "gm": Chat("notice", $"<color={GmColor}>GM</color>", text); break;
                case "level": Chat("system", "", Fill("10054", "%s has reached level %d.", m.S("name"), m.I("level"))); break;
                case "event": EventPopup.Show(m.S("icon"), m.S("title", ""), text); break;
                case "obtain":
                    AudioLibrary.PlaySfx("sfx/SID_ITEM_DROP_GET1");
                    Chat("system", "", Fill("10096", "Obtained %d %s", m.I("count", 1), m.S("name")));
                    break;
            }
        }

        public static void SetClock(double serverUtcMs)
        {
            if (serverUtcMs <= 0) return;
            var server = DateTimeOffset.FromUnixTimeMilliseconds((long)serverUtcMs).UtcDateTime;
            clockOffset = server - DateTime.UtcNow;
            ClockSynced = true;
        }

        /// <summary>Back to the local clock (logout of a test, tools).</summary>
        public static void ResetClock() { clockOffset = TimeSpan.Zero; ClockSynced = false; }

        /// <summary>The message text: its key from the client's text tables filled with args, else its plain text.</summary>
        public static string Text(Dictionary<string, object> m)
        {
            string key = m.S("key"), text = m.S("text", "");
            if (string.IsNullOrEmpty(key)) return text;
            var args = new List<object>();
            foreach (var a in m.L("args") ?? new List<object>()) args.Add(a is double d && Math.Abs(d - Math.Round(d)) < 1e-9 ? (object)(long)d : a);
            string fmt = Lookup(key);
            return fmt != null ? MissionText.Printf(fmt, args.ToArray()) : (text.Length > 0 ? text : key);
        }

        static string Fill(string key, string fallback, params object[] args) => MissionText.Printf(Lookup(key) ?? fallback, args);

        static Dictionary<string, string> serverText;

        /// <summary>globaltext.xlt, then globaltext_server.xlt; null when neither has the key (or no game data).</summary>
        public static string Lookup(string key)
        {
            try
            {
                string s = GameTables.Text(key);
                if (!string.IsNullOrEmpty(s) && s != key) return s;
            }
            catch (Exception) { }
            if (serverText == null)
            {
                serverText = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var data = DriftCityData.Files?.Read(@"localize\globaltext_server.xlt");
                    if (data != null)
                    {
                        var t = XltTable.Parse(data);
                        foreach (var r in t.Rows)
                        {
                            string k = t.Get(r, "Key"), v = t.Get(r, "Value_US");
                            if (string.IsNullOrEmpty(v)) v = t.Get(r, "Value_KR");
                            if (!string.IsNullOrEmpty(k) && !string.IsNullOrEmpty(v)) serverText[k] = v;
                        }
                    }
                }
                catch (Exception ex) { Debug.LogWarning("globaltext_server.xlt: " + ex.Message); }
            }
            return serverText.TryGetValue(key, out var sv) ? sv : null;
        }

        static void Chat(string scope, string from, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var box = ChatBox.Ensure();
            if (box) box.Add(scope, from, text);
        }

        /// <summary>Op 0x008: the message box and the same line in the system chat.</summary>
        public static void Popup(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            LastPopup = text;
            Chat("system", "", text);
            UiPopup.Message(text);
        }

        // ---------------- kick (op 0x09c) ----------------
        /// <summary>The original text for our server's kick reasons; the reason itself otherwise, 10989 when empty.</summary>
        public static string KickText(string reason)
        {
            string r = (reason ?? "").Trim();
            string key = r.ToLowerInvariant() switch
            {
                "" => "10989",                                   // Exit by server.
                "logged in from another place" => "20209",       // Someone from a different location is trying to log-in to your account.
                "timed out" => "20335",                          // Exit due to timeout. Please restart your game.
                "login timed out" => "20335",
                "kicked by admin" => "20308",                    // You've been kicked.
                "banned" => "11417",                             // he is blocked and he should contact the support.
                "maintenance" => "10983",                        // Server is currently undergoing maintenance.
                "too many failed logins" => "10050",             // Failed to authenticate account.
                _ => null,
            };
            string fallback = key switch
            {
                "10989" => "Exit by server.",
                "20209" => "Someone from a different location is trying to log-in to your account.",
                "20335" => "Exit due to timeout. Please restart your game.",
                "20308" => "You've been kicked.",
                "11417" => "This account is blocked. Please contact the support.",
                "10983" => "Server is currently undergoing maintenance.",
                "10050" => "Failed to authenticate account.",
                _ => r,
            };
            return key != null ? Lookup(key) ?? fallback : r;
        }

        /// <summary>The server closed the session: the reason in the message box (the client then quit; the remake keeps driving offline).</summary>
        public static void Kicked(string reason)
        {
            string text = KickText(reason);
            LastKick = text;
            kickPending = true;
            kickAt = Time.realtimeSinceStartup;
            Chat("system", "", text);
            UiPopup.Message(text);
        }

        /// <summary>True once after a kick: the disconnect that follows doesn't need its own popup.</summary>
        public static bool ConsumeKick()
        {
            bool k = kickPending && Time.realtimeSinceStartup - kickAt < 10f;
            kickPending = false;
            return k;
        }

        static float kickAt;

        // ---------------- EMERGENCY 2770000 ----------------
        public void Emergency(string text, float seconds = NoticeSeconds, bool large = false, bool chat = true)
        {
            if (string.IsNullOrEmpty(text)) return;
            LastEmergency = text;
            if (chat) Chat("notice", "", text);
            var v = View(ref emergency, EmergencyDialog);
            if (!v) return;
            v.SetText("ID_EMERGENCY_NOTICE_TEXT", text);
            v.SetText("ID_EMERGENCY_NOTICE_LARGE_TEXT", text);
            v.SetVisible("ID_EMERGENCY_NOTICE_TEXT", !large);
            v.SetVisible("ID_EMERGENCY_NOTICE_LARGE_TEXT", large);
            v.transform.SetAsLastSibling();
            v.gameObject.SetActive(true);
            emergencyUntil = Time.unscaledTime + Mathf.Max(0.5f, seconds);
        }

        public bool EmergencyShown => emergency && emergency.gameObject.activeSelf;

        // ---------------- SCROLL 2780000 ----------------
        public void Scroll(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            LastScroll = text;
            Chat("notice", "", text);
            scrollQueue.Enqueue(text);
            if (!scrolling) NextScroll();
        }

        public bool ScrollShown => scroll && scroll.gameObject.activeSelf;
        public int ScrollQueued => scrollQueue.Count;

        void NextScroll()
        {
            scrolling = false;
            if (scrollQueue.Count == 0) { if (scroll) scroll.gameObject.SetActive(false); return; }
            string text = scrollQueue.Dequeue();
            var v = View(ref scroll, ScrollDialog);
            if (!v) { scrollQueue.Clear(); return; }
            var c = v.Control("ID_SCROLL_NOTICE");
            if (!c) { scrollQueue.Clear(); return; }
            if (!scrollRect) { scrollRect = (RectTransform)c.transform; scrollBaseX = scrollRect.anchoredPosition.x; }
            c.SetText(text);
            foreach (var t in c.GetComponentsInChildren<UnityEngine.UI.Text>(true))
            {
                t.alignment = TextAnchor.MiddleLeft;
                t.horizontalOverflow = HorizontalWrapMode.Overflow;
            }
            v.gameObject.SetActive(true);
            scrollStartAt = Time.unscaledTime;
            scrolling = true;
            PlaceScroll();
        }

        float CanvasWidth()
        {
            var ui = UiManager.Instance;
            var rt = ui ? ui.Root as RectTransform : null;
            return rt && rt.rect.width > 0 ? rt.rect.width : 1024f;
        }

        /// <summary>0x5523a0: x = start + screen width - elapsed * 70; the text is done once x passes -screen width.</summary>
        bool PlaceScroll()
        {
            if (!scrollRect) return false;
            float w = CanvasWidth();
            float x = scrollBaseX + w - (Time.unscaledTime - scrollStartAt) * ScrollSpeed;
            var p = scrollRect.anchoredPosition;
            scrollRect.anchoredPosition = new Vector2(x, p.y);
            return x >= scrollBaseX - w;
        }

        NuiView View(ref NuiView v, int dialogId)
        {
            if (v) return v;
            var ui = UiManager.Instance;
            if (!ui) return null;
            NuiDialog d = null;
            try { d = UiAssets.Layout?.Get(dialogId); } catch (Exception ex) { Debug.LogWarning($"dialog {dialogId}: {ex.Message}"); }
            if (d == null) return null;
            v = NuiView.Create(d, ui.Root);
            v.name = dialogId == EmergencyDialog ? "Emergency Notice" : "Scroll Notice";
            foreach (var g in v.GetComponentsInChildren<UnityEngine.UI.Graphic>(true)) g.raycastTarget = false;   // never blocks clicks
            v.gameObject.SetActive(false);
            return v;
        }

        void Update()
        {
            if (emergency && emergency.gameObject.activeSelf && Time.unscaledTime >= emergencyUntil) emergency.gameObject.SetActive(false);
            if (scrolling && !PlaceScroll()) NextScroll();
        }
    }
}
