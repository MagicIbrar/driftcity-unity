using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DriftCity.Game;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.Social
{
    /// <summary>
    /// The social layer's link to the server (DriftCity_RE/SOCIAL.md): requests go through the online session, pushed
    /// messages (party.*, friend.*, note.*, crew.*) are routed to the windows. Offline every window uses its offline
    /// rule and the local <see cref="SocialStore"/>.
    /// </summary>
    public static class SocialNet
    {
        static NetClient hooked;

        public static bool Online => OnlineSession.Active;
        /// <summary>True once the social layer listens to the session (then it shows the party invite, not OnlineSession).</summary>
        public static bool Active => hooked && OnlineSession.Instance && OnlineSession.Instance.Net == hooked;

        public static void Hook()
        {
            SocialHost.Ensure();
            var s = OnlineSession.Instance;
            if (!s || !s.Net || hooked == s.Net) return;
            if (hooked) hooked.Message -= OnMessage;
            hooked = s.Net;
            hooked.Message += OnMessage;
        }

        static void OnMessage(string t, Dictionary<string, object> m)
        {
            if (t == null) return;
            try
            {
                if (t.StartsWith("party.")) PartyWindow.OnPush(t, m);
                else if (t.StartsWith("friend.")) Friends.OnPush(t, m);
                else if (t.StartsWith("note.")) Mail.OnPush(t, m);
                else if (t.StartsWith("crew.")) Teams.OnPush(t, m);
                else if (t == "race.results") TeamBattle.OnResults(m);
            }
            catch (Exception ex) { Debug.LogException(ex); }
        }

        /// <summary>A request to the server; offline (or disconnected) the reply is { ok: false, error: "offline" }.</summary>
        public static void Request(string type, Dictionary<string, object> body, Action<Dictionary<string, object>> reply)
        {
            if (!Online) { reply?.Invoke(new Dictionary<string, object> { ["ok"] = false, ["error"] = "offline" }); return; }
            Hook();
            OnlineSession.Instance.Net.Request(type, body ?? new Dictionary<string, object>(), r =>
            {
                if (r != null && r.O("driver") != null) OnlineSession.ApplyDriver(r.O("driver"));
                reply?.Invoke(r);
            });
        }

        /// <summary>globaltext.xlt entry with the client's printf args, or the fallback when the table lacks it.</summary>
        public static string T(string key, string fallback, params object[] args)
        {
            string fmt = GameTables.Text(key);
            if (string.IsNullOrEmpty(fmt) || fmt == key) fmt = fallback;
            return MissionText.Printf(fmt, args);
        }

        public static string Err(Dictionary<string, object> r) => r == null ? "No answer from the server." : r.S("error", "The server refused that.");
    }

    /// <summary>Shared UI helpers for the social dialogs.</summary>
    public static class SocialUi
    {
        /// <summary>Hides the controls the client starts hidden (NATION K=0) for a dialog not in NuiView.HonorHidden.</summary>
        public static void HideStartHidden(NuiView v)
        {
            if (!v) return;
            foreach (var c in v.Controls.ToList())
                if (Core.NuiControl.Int(c.Data.Nation, "K", 1) == 0) c.gameObject.SetActive(false);
        }

        public static void Close(ref NuiView v)
        {
            if (!v) { v = null; return; }
            var ui = UiManager.Instance;
            if (ui) ui.Close(v); else UnityEngine.Object.Destroy(v.gameObject);
            v = null;
        }

        /// <summary>
        /// Centres a dialog whose game.nui layout has no screen anchor (the client places the party popups 410000-450000
        /// at the screen centre at runtime): the controls' bounding box is moved to the middle of the 1024x768 screen.
        /// </summary>
        public static void CenterOnScreen(NuiView v)
        {
            var origin = v ? v.transform.Find("origin") as RectTransform : null;
            if (!origin) return;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (RectTransform c in origin)
            {
                var p = c.anchoredPosition; var sz = c.sizeDelta;
                minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x + sz.x);
                minY = Mathf.Min(minY, p.y - sz.y); maxY = Mathf.Max(maxY, p.y);
            }
            if (minX > maxX) return;
            origin.anchorMin = origin.anchorMax = new Vector2(0.5f, 0.5f);
            origin.anchoredPosition = new Vector2(-(minX + maxX) / 2f, -(minY + maxY) / 2f);
        }

        /// <summary>No longer needed: NuiView draws a check box's element 2 as the client does (slot 1 checked, slot 6
        /// unchecked). Kept for callers.</summary>
        public static void FixCheck(NuiControlView c) { }

        /// <summary>No longer needed: a selected row draws its own FOCUS art (I1_4) through NuiControlView.Selected. Kept for callers.</summary>
        public static void Highlight(NuiControlView row, bool on) { }

        public static void SetInput(NuiView v, string id, string text)
        {
            var c = v ? v.Control(id) : null;
            if (c && c.Input) c.Input.text = text ?? "";
        }

        public static void ReadOnly(NuiView v, string id)
        {
            var c = v ? v.Control(id) : null;
            if (c && c.Input) c.Input.readOnly = true;
        }

        /// <summary>A transparent clickable layer over a static control (item slots are static in game.nui).</summary>
        public static void Clickable(NuiView v, string id, Action onClick)
        {
            var c = v ? v.Control(id) : null;
            if (!c) return;
            var rt = NuiView.NewRect("click", c.transform);
            rt.sizeDelta = ((RectTransform)c.transform).sizeDelta;
            var img = rt.gameObject.AddComponent<Image>();
            img.color = new Color(1, 1, 1, 0.001f);
            rt.gameObject.AddComponent<Button>().onClick.AddListener(() => { UiSounds.Click(); onClick(); });
        }

        static readonly Dictionary<int, Texture2D> marks = new Dictionary<int, Texture2D>();

        /// <summary>Crew mark N (Data\GameUI\teammark\01..10, 32x32 24-bit BMP; 00 = no mark).</summary>
        public static Texture2D Mark(int n)
        {
            n = Mathf.Clamp(n, 0, 10);
            if (marks.TryGetValue(n, out var t) && t) return t;
            t = null;
            try
            {
                var fs = DriftCityData.Files;
                if (fs != null && fs.TryRead($@"GameUI\teammark\{n:00}", out var d)) t = Bmp(d);
            }
            catch (Exception ex) { Debug.LogWarning("crew mark: " + ex.Message); }
            marks[n] = t;
            return t;
        }

        /// <summary>Uncompressed 24/32-bit BMP (bottom-up rows).</summary>
        public static Texture2D Bmp(byte[] d)
        {
            if (d == null || d.Length < 54 || d[0] != 'B' || d[1] != 'M') return null;
            int off = BitConverter.ToInt32(d, 10), w = BitConverter.ToInt32(d, 18), h = BitConverter.ToInt32(d, 22);
            int bpp = BitConverter.ToInt16(d, 28);
            if ((bpp != 24 && bpp != 32) || w <= 0 || w > 256 || Math.Abs(h) > 256) return null;
            bool bottomUp = h > 0; h = Math.Abs(h);
            int bytes = bpp / 8, stride = (w * bytes + 3) & ~3;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                int row = off + (bottomUp ? y : h - 1 - y) * stride;   // Unity rows go bottom-up
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * bytes;
                    if (i + 2 >= d.Length) break;
                    px[y * w + x] = new Color32(d[i + 2], d[i + 1], d[i], 255);
                }
            }
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }

        /// <summary>Shows crew mark N on an image control (keeps the control's size).</summary>
        public static void ShowMark(NuiView v, string id, int mark)
        {
            var c = v ? v.Control(id) : null;
            if (!c) return;
            var tex = mark > 0 ? Mark(mark) : null;
            c.gameObject.SetActive(tex != null);
            var ri = c.GetComponentInChildren<RawImage>(true);
            if (ri && tex) { ri.texture = tex; ri.uvRect = new Rect(0, 0, 1, 1); ri.color = Color.white; ri.enabled = true; }
        }

        public static string Date(long ms) => ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("yyyy-MM-dd") : "";
        public static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public static readonly string[] Cities = { "cras", "moonpalace", "koinonia", "oros" };
        public static string CityName(string city) => city switch { "cras" => "Cras", "moonpalace" => "Moon Palace", "koinonia" => "Koinonia", "oros" => "Oros", _ => city ?? "" };
    }

    /// <summary>
    /// Offline stand-in for the server's social tables: notes (the game's own system notes), AI friends, the party of AI
    /// partners and a one-driver crew, saved next to the driver profile (social_&lt;slot&gt;.json).
    /// </summary>
    [Serializable]
    public sealed class SocialStore
    {
        [Serializable] public sealed class Note { public int Id; public string Sender; public int Type; public string Status = "N"; public string Content; public long Mito; public long SentAt, ReadAt; }
        [Serializable] public sealed class Friend { public string Name; public int Level; public string Status = "F"; }
        [Serializable] public sealed class Crew { public string Name; public int Mark = 1; public long Created; public string State = "A"; public long CloseAt; public int Points; public bool Renamed; }

        public string Driver;
        public bool Welcomed;
        public int NextNote = 1;
        public List<Note> Notes = new List<Note>();
        public List<Friend> Friends = new List<Friend>();
        public Crew MyCrew;

        static SocialStore current;
        static string currentPath;

        public static string PathFor(PlayerProfile p)
        {
            string dir = Path.GetDirectoryName(PlayerProfile.SavePath) ?? Application.persistentDataPath;
            return Path.Combine(dir, $"social_{PlayerProfile.Slot}.json");
        }

        /// <summary>The store of the current offline driver (a new one per driver name), with the welcome note.</summary>
        public static SocialStore Current
        {
            get
            {
                var p = PlayerProfile.Current;
                if (p == null) return current ??= new SocialStore();
                string path = PathFor(p);
                if (current == null || currentPath != path || current.Driver != p.Name)
                {
                    current = null;
                    try { if (File.Exists(path)) current = JsonUtility.FromJson<SocialStore>(File.ReadAllText(path)); }
                    catch (Exception ex) { Debug.LogWarning("social store: " + ex.Message); }
                    if (current == null || current.Driver != p.Name) current = new SocialStore { Driver = p.Name };
                    currentPath = path;
                }
                if (!current.Welcomed)
                {
                    current.Welcomed = true;
                    current.AddNote("Drift City", 1, $"Welcome to Drift City, {p.Name}! Open this note and click the gift slot to take 1000 Mito. Notes from other drivers arrive here when you play online.", 1000);
                    current.Save();
                }
                return current;
            }
        }

        public static void Reset() { current = null; currentPath = null; }

        public Note AddNote(string sender, int type, string content, long mito)
        {
            var n = new Note { Id = NextNote++, Sender = sender, Type = type, Content = content, Mito = mito, SentAt = SocialUi.NowMs };
            Notes.Add(n);
            return n;
        }

        public void Save()
        {
            if (currentPath == null || PlayerProfile.Current == null) return;
            try { File.WriteAllText(currentPath, JsonUtility.ToJson(this, true)); }
            catch (Exception ex) { Debug.LogWarning("social store save: " + ex.Message); }
        }

        /// <summary>The AI drivers the offline game knows (the Battle Zone rivals): offline friends and party partners.</summary>
        public static readonly string[] AiDrivers = { "Rio", "Duke", "Mika", "Jin", "Sora", "Kai", "Nova", "Taro", "Yuna", "Leon", "Hana", "Max", "Zed", "Aki", "Rex", "Luna", "Ivan", "Mei", "Toby", "Ken" };

        public static string AiName(string name) => AiDrivers.FirstOrDefault(n => string.Equals(n, (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

        /// <summary>A steady level / city / channel for an AI driver (seeded by the name).</summary>
        public static (int level, string city, int channel, int grade, int avatar) AiInfo(string name)
        {
            int seed = 0x5a17; foreach (char ch in name ?? "") seed = seed * 31 + ch;
            var rng = new System.Random(seed & 0x7fffffff);
            int baseLevel = PlayerProfile.Current != null ? PlayerProfile.Current.Level : 10;
            int level = Mathf.Clamp(baseLevel + rng.Next(-4, 6), 1, 120);
            return (level, SocialUi.Cities[rng.Next(SocialUi.Cities.Length)], 1 + rng.Next(3), 1 + rng.Next(6), 1 + rng.Next(8));
        }
    }

    /// <summary>
    /// Chat commands of the social layer (the client's prefixes, table at 0x5b7a67): "*Name text" whisper, "&amp;text" party,
    /// "@text" crew, "/party text", "/crew text", "/friend name", "/note name text", "/crew join name", "/block name".
    /// Returns true when the line was handled. OnlineSession.SubmitChat calls this first.
    /// </summary>
    public static class SocialChat
    {
        public static bool TryHandle(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || !OnlineSession.Active) return false;
            text = text.Trim();
            var net = OnlineSession.Instance.Net;
            if (text.Length > 1 && text[0] == '*')
            {
                int sp = text.IndexOf(' ');
                if (sp > 1 && sp < text.Length - 1) { net.Send("chat", new Dictionary<string, object> { ["scope"] = "whisper", ["to"] = text.Substring(1, sp - 1), ["text"] = text.Substring(sp + 1) }); return true; }
                return false;
            }
            if (text.Length > 1 && text[0] == '&') { net.Send("chat", new Dictionary<string, object> { ["scope"] = "party", ["text"] = text.Substring(1).Trim() }); return true; }
            if (text.Length > 1 && text[0] == '@') { net.Send("chat", new Dictionary<string, object> { ["scope"] = "team", ["text"] = text.Substring(1).Trim() }); return true; }
            if (text.StartsWith("/party ")) { net.Send("chat", new Dictionary<string, object> { ["scope"] = "party", ["text"] = text.Substring(7) }); return true; }
            if (text.StartsWith("/crew join ")) { Teams.RequestJoin(text.Substring(11).Trim()); return true; }
            if (text.StartsWith("/crew ")) { net.Send("chat", new Dictionary<string, object> { ["scope"] = "team", ["text"] = text.Substring(6) }); return true; }
            if (text.StartsWith("/invite ")) { PartyWindow.Invite(text.Substring(8).Trim()); return true; }
            if (text.StartsWith("/friend ")) { Friends.Add(text.Substring(8).Trim()); return true; }
            if (text.StartsWith("/block ")) { Friends.Block(text.Substring(7).Trim()); return true; }
            if (text.StartsWith("/unblock ")) { Friends.Unblock(text.Substring(9).Trim()); return true; }
            if (text.StartsWith("/note "))
            {
                var parts = text.Split(new[] { ' ' }, 3);
                if (parts.Length == 3) { Mail.Send(parts[1], parts[2], null); return true; }
            }
            return false;
        }

        /// <summary>The friend list's WHISPER: the client writes "*Name " into the chat input (needs ChatBox's hook).</summary>
        public static Action<string> OpenChatInput;

        public static void Whisper(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            if (OpenChatInput != null) OpenChatInput("*" + name + " ");
            else ChatBox.System($"Whisper to {name}: press Enter and type  *{name} message");
        }
    }

    /// <summary>Per-frame jobs: hooks the session, the party HUD's F12, the main bar's new-note notice.</summary>
    public sealed class SocialHost : MonoBehaviour
    {
        static SocialHost instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot() => Ensure();

        public static void Ensure()
        {
            if (instance) return;
            var go = new GameObject("Social");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<SocialHost>();
        }

        float noticeT = -1f;
        int lastUnread;
        NuiView hookedBar;

        string driverKey;

        void Update()
        {
            SocialNet.Hook();
            // a new driver (login, character select, going online): fetch the inbox for the notice and the crew
            var p = PlayerProfile.Current;
            string key = p == null ? null : (SocialNet.Online ? "on|" : "off|") + p.Name;
            if (key != driverKey)
            {
                driverKey = key;
                Mail.ResetAll(); Friends.List.Clear(); Teams.ResetAll(); PartyWindow.ResetAll();
                if (p != null) { SocialStore.Reset(); Mail.Load(); Teams.Load(); }
            }
            PartyWindow.Tick();
            if (F12()) PartyWindow.ToggleList();
            NoteNotice();
        }

        static bool F12()
        {
            if (ChatBox.Typing) return false;
#if ENABLE_INPUT_SYSTEM
            var kb = UnityEngine.InputSystem.Keyboard.current;
            return kb != null && kb.f12Key.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.F12);
#endif
        }

        /// <summary>
        /// 0x5ecfc0: while an inbox note is unread, NOTE_NOTICE1 fades in (0-1 s), holds (to 3 s) and fades out (to 4 s);
        /// clicking it opens the Community window on the NOTE tab (0x5ed580).
        /// </summary>
        void NoteNotice()
        {
            var ui = UiManager.Instance;
            var bar = ui ? ui.Find(3010000) : null;
            if (!bar) { hookedBar = null; return; }
            if (hookedBar != bar)
            {
                hookedBar = bar;
                ui.On("ID_MAINBUTTON_ADV_NOTE_NOTICE1", _ => Community.Open(Community.Tab.Note));
                ui.On("ID_MAINBUTTON_ADV_NOTE_NOTICE2", _ => Community.Open(Community.Tab.Note));
                noticeT = Mail.Unread > 0 ? 0f : -1f;
            }
            int unread = Mail.Unread;
            if (unread > lastUnread) noticeT = 0f;
            lastUnread = unread;
            var c = bar.Control("ID_MAINBUTTON_ADV_NOTE_NOTICE1");
            if (!c) return;
            bool show = unread > 0 && noticeT >= 0f && noticeT < 4f;
            if (c.gameObject.activeSelf != show) c.gameObject.SetActive(show);
            if (show)
            {
                noticeT += Time.unscaledDeltaTime;
                float a = noticeT < 1f ? noticeT : noticeT < 3f ? 1f : Mathf.Clamp01(4f - noticeT);
                foreach (var g in c.GetComponentsInChildren<Graphic>()) { var col = g.color; col.a = a; g.color = col; }
            }
        }
    }
}
