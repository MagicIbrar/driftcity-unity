using System;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.Game
{
    /// <summary>
    /// Lobby and wait-room chat (the chat component of binder 0x5b3340 in RMLOBBY 1000000 and WAIT16 1100000: log
    /// CHAT_LIST 1000147 / CHATBOX 1100034, input CHAT_INPUT 1000149 / CHAT 1100035). Lines read "[%s]: %s"; the send scope
    /// is "room" (0x5bcdd0): the room's players and spectators in the wait room, the drivers looking at the same arena in
    /// the lobby (server race.chat). Emoticons are chat commands ("/Hi", init\emoticon.xlt): in the wait room they are
    /// drawn over the sender's slot for 5 s (1130000) and not printed (0x594120). The Battle Zone is its own stage in the
    /// client: while its screens are up the city chat window steps aside and Enter talks here. Offline the AI drivers
    /// answer a greeting now and then.
    /// </summary>
    public static class PvpChat
    {
        public const int MaxLength = 70, Lines = 6;

        sealed class Box
        {
            public NuiView View;
            public Text Log;
            public InputField Input;
            public readonly List<string> Lines = new List<string>();
        }

        static readonly List<Box> boxes = new List<Box>();
        static Runner runner;
        static Behaviour chatBox;
        static readonly List<GameObject> hiddenChat = new List<GameObject>();

        /// <summary>Every line shown (tests).</summary>
        public static readonly List<string> History = new List<string>();

        sealed class Runner : MonoBehaviour { void Update() => Tick(); }

        /// <summary>Hooks a dialog's chat log and input; the city chat steps aside while any is attached.</summary>
        public static void Attach(NuiView v, string logId, string inputId)
        {
            if (!v) return;
            boxes.RemoveAll(b => !b.View || b.View == v);
            var box = new Box { View = v };
            var logCtl = v.Control(logId);
            if (logCtl)
            {
                logCtl.gameObject.SetActive(true);
                var rt = NuiView.NewRect("chat log", logCtl.transform);
                var size = ((RectTransform)logCtl.transform).sizeDelta;
                rt.anchoredPosition = new Vector2(6, -4);
                rt.sizeDelta = new Vector2(size.x - 12, size.y - 8);
                var t = rt.gameObject.AddComponent<Text>();
                t.font = UiAssets.Font(logCtl.Data.I("FN1") ?? "Calibri 12 700", out int s, out var st);
                t.fontSize = s; t.fontStyle = st;
                t.alignment = TextAnchor.LowerLeft;
                t.horizontalOverflow = HorizontalWrapMode.Wrap; t.verticalOverflow = VerticalWrapMode.Truncate;
                t.raycastTarget = false; t.supportRichText = true;
                t.color = Color.white;
                rt.gameObject.AddComponent<Outline>().effectColor = new Color(0, 0, 0, 0.8f);
                box.Log = t;
            }
            var inCtl = v.Control(inputId);
            if (inCtl && inCtl.Input)
            {
                inCtl.gameObject.SetActive(true);
                box.Input = inCtl.Input;
                box.Input.characterLimit = MaxLength;
                box.Input.lineType = InputField.LineType.SingleLine;
            }
            // the scope indicator shows "normal"; the other scopes of the city chat are not offered here
            foreach (var c in v.GetComponentsInChildren<NuiControlView>(true))
            {
                var id = c.Data.IdName ?? "";
                if (id.Contains("_DROPDOWN_") || (id.Contains("FIX_") && !id.EndsWith("FIX_NORMAL")) || id.Contains("_CHAT_SYSTEM") || id.Contains("_CHAT_NORMAL") ||
                    id.Contains("_CHAT_CHANNEL") || id.Contains("_CHAT_PARTY") || id.Contains("_CHAT_TEAM") || id.Contains("_CHAT_PRIVATE"))
                    c.gameObject.SetActive(false);
                else if (id.EndsWith("FIX_NORMAL")) { c.gameObject.SetActive(true); c.Interactable = false; }
            }
            boxes.Add(box);
            if (!runner) runner = new GameObject("PvP Chat").AddComponent<Runner>();
            SuppressCityChat(true);
            Redraw(box);
        }

        public static void Detach(NuiView v)
        {
            boxes.RemoveAll(b => !b.View || b.View == v);
            if (boxes.Count == 0) SuppressCityChat(false);
        }

        static Box Current => boxes.LastOrDefault(b => b.View && b.View.isActiveAndEnabled);
        public static NuiView CurrentView => Current?.View;

        /// <summary>The city chat window (CHAT ADV) and its Enter key step aside for the Battle Zone screens.</summary>
        static void SuppressCityChat(bool on)
        {
            if (on)
            {
                var cb = ChatBox.Instance;
                if (cb && cb.enabled) { cb.enabled = false; chatBox = cb; }
                var ui = UiManager.Instance;
                if (ui)
                {
                    foreach (var id in new[] { 260000, 250000, 270000, 280000 })
                    {
                        var v = ui.Find(id);
                        if (v && v.gameObject.activeSelf) { v.gameObject.SetActive(false); hiddenChat.Add(v.gameObject); }
                    }
                    // the city chat window (CHAT ADV 260000) is made outside the UI manager's list
                    var w = ui.Root.Find("Chat Window");
                    if (w && w.gameObject.activeSelf) { w.gameObject.SetActive(false); hiddenChat.Add(w.gameObject); }
                }
            }
            else
            {
                if (chatBox) chatBox.enabled = true;
                chatBox = null;
                foreach (var g in hiddenChat) if (g) g.SetActive(true);
                hiddenChat.Clear();
            }
        }

        static void Tick()
        {
            boxes.RemoveAll(b => !b.View);
            if (boxes.Count == 0) { SuppressCityChat(false); return; }
            bool enter;
#if ENABLE_INPUT_SYSTEM
            var k = UnityEngine.InputSystem.Keyboard.current;
            enter = k != null && (k.enterKey.wasPressedThisFrame || k.numpadEnterKey.wasPressedThisFrame);
#else
            enter = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);
#endif
            if (!enter || UiPopup.IsOpen) return;
            var box = Current;
            if (box == null || !box.Input) return;
            if (box.Input.isFocused || box.Input.text.Length > 0)
            {
                string text = box.Input.text;
                box.Input.text = "";
                if (!string.IsNullOrWhiteSpace(text)) Send(text);
                box.Input.DeactivateInputField();
                if (UnityEngine.EventSystems.EventSystem.current) UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(null);
            }
            else { box.Input.Select(); box.Input.ActivateInputField(); }
        }

        /// <summary>Sends a line in the current dialog's scope (online race.chat; offline straight to the log).</summary>
        public static void Send(string text)
        {
            text = (text ?? "").Replace("\n", " ").Trim();
            if (text.Length == 0) return;
            if (text.Length > MaxLength) text = text.Substring(0, MaxLength);
            var me = PlayerProfile.Current?.Name ?? "Driver";
            if (OnlineSession.Active)
            {
                var body = new Dictionary<string, object> { ["text"] = text };
                if (PvpLobby.IsOpen && !PvpWaitRoom.IsOpen) body["arena"] = PvpLobby.Arena;
                OnlineSession.Instance.Net.Request("race.chat", body, r =>
                {
                    if (!r.B("ok")) System(r.I("code") == 10891 ? Pvp.Text("10891") : r.S("error"));
                });
                return;
            }
            Receive(me, text);
            OfflineAnswer(text);
        }

        /// <summary>A chat line (server race.chat or offline): an emoticon goes over the sender's slot in the wait room.</summary>
        public static void Receive(string from, string text)
        {
            var icon = Pvp.Emoticon(text);
            if (icon != null && PvpWaitRoom.IsOpen && PvpWaitRoom.ShowEmoticon(from, icon)) { History.Add($"[{from}]: {text}"); return; }
            Add($"[{Escape(from)}]: {Escape(text)}", $"[{from}]: {text}");
        }

        /// <summary>A system line (yellow).</summary>
        public static void System(string text) => Add($"<color=#ffd52e>{Escape(text)}</color>", text);

        static string Escape(string s) => (s ?? "").Replace("<", "‹").Replace(">", "›");

        static void Add(string rich, string plain)
        {
            History.Add(plain);
            if (History.Count > 200) History.RemoveAt(0);
            foreach (var b in boxes)
            {
                if (!b.View) continue;
                b.Lines.Add(rich);
                while (b.Lines.Count > Lines) b.Lines.RemoveAt(0);
                Redraw(b);
            }
        }

        static void Redraw(Box b) { if (b.Log) b.Log.text = string.Join("\n", b.Lines); }

        // ---------------- offline: the AI drivers answer now and then ----------------
        static readonly string[] Greetings = { "hi", "hello", "hey", "yo", "gl", "gg" };
        static readonly string[] Answers = { "/Hi", "/GG", "/Thanks", "gl hf", "/ready", "Let's go!" };

        static void OfflineAnswer(string text)
        {
            var room = PvpWaitRoom.Room;
            if (room == null || room.Online || !runner) return;
            var t = text.Trim().TrimStart('/').ToLowerInvariant();
            if (!Greetings.Any(g => t.StartsWith(g))) return;
            var ais = room.Members.Where(m => m.Ai).ToList();
            if (ais.Count == 0) return;
            var who = ais[UnityEngine.Random.Range(0, ais.Count)];
            var say = Answers[UnityEngine.Random.Range(0, Answers.Length)];
            runner.StartCoroutine(Later(1.2f, () => { if (PvpWaitRoom.Room == room) Receive(who.Name, say); }));
        }

        static System.Collections.IEnumerator Later(float s, Action a) { yield return new WaitForSeconds(s); a(); }

        /// <summary>Forget everything (logout).</summary>
        public static void Reset()
        {
            boxes.Clear();
            SuppressCityChat(false);
            History.Clear();
        }
    }
}
