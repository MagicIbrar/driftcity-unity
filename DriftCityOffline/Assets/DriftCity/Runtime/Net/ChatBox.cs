using System.Collections.Generic;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace DriftCity.Net
{
    /// <summary>
    /// The client's chat window (game.nui CHAT ADV 260000, the one the remaster client shows at the left of the
    /// screen): the message list with its System / Normal / Channel / Party / Team / Private filter tabs and the input
    /// line. It is up whenever you drive, offline too, where it carries the system log (rewards, level-ups, items);
    /// online it also carries channel, party and whisper chat (Enter to type, Enter to send, Esc to cancel).
    /// Chat modes (DriftCity_RE/SCREENS.md "Chat"): the label in front of the input (FIX_NORMAL .. FIX_PRIVATE) opens the
    /// DROPDOWN list; a line without a prefix goes out in the chosen mode (0x5bcdd0: 0/1 room or area, 2 channel, 3 party,
    /// 4 crew, 5 whisper, 6 private). The prefixes still win (0x5b7a67): ! channel, &amp; party, * whisper, - private,
    /// @ crew. Whispers (0x5bcf60 / 0x5bd360): "*Name text" sends, the echo shows 10474 "Whispering to [%s]: %s", a
    /// received one 10475 "Whispering from [%s]: %s"; after a whisper the next input opens with "*Name " (keep whispering
    /// the same driver) and a received whisper puts "*sender " there (reply to the last whisperer). The server has no
    /// area range: Normal goes to the channel. Private channels (/cjoin .. /cleave, "-channel text", the Private mode) go
    /// to Game.Services.PrivateChannel (server switch privateChat; offline or switched off they answer 10745).
    /// </summary>
    public sealed class ChatBox : MonoBehaviour
    {
        public static ChatBox Instance { get; private set; }
        /// <summary>True while the input line is open (and on the frame it closes, so Enter/Esc don't also reach hotkeys).</summary>
        public static bool Typing => Instance && Instance.typing || (Instance && Instance.closedFrame == Time.frameCount);
        int closedFrame = -1;
        bool typing;

        const int MaxLines = 11;
        /// <summary>Pixels the window sits above its game.nui position: in the remaster footage its bottom clears the minimap.</summary>
        public const float ChatLift = 190f;
        readonly List<(string scope, string text)> lines = new List<(string, string)>();
        NuiView view;
        Text log;
        InputField input;

        /// <summary>Chat modes in the client's numbering (0x5bcdd0).</summary>
        public enum ChatMode { Normal = 0, Channel = 2, Party = 3, Team = 4, Whisper = 5, Private = 6 }
        static readonly (ChatMode mode, string name)[] Modes =
        {
            (ChatMode.Normal, "NORMAL"), (ChatMode.Channel, "CHANNEL"), (ChatMode.Party, "PARTY"), (ChatMode.Team, "TEAM"), (ChatMode.Whisper, "WHISPER"), (ChatMode.Private, "PRIVATE"),
        };
        ChatMode mode = ChatMode.Normal;
        bool dropdown;
        /// <summary>What the next input line opens with: "*Name " after a whisper sent or received (0x5bd360).</summary>
        string replyPrefill = "";
        public ChatMode Mode => mode;
        public bool DropdownOpen => dropdown;
        /// <summary>The driver the next whisper goes to ("" before any whisper).</summary>
        public string WhisperTarget { get; private set; } = "";
        /// <summary>The text the next input line opens with (tests).</summary>
        public string Prefill => replyPrefill;

        // tab -> message scopes it shows
        static readonly (string id, string[] scopes)[] Tabs =
        {
            ("ID_CHAT_ADV_BUTTON_SYSTEM", new[] { "system", "notice" }),
            ("ID_CHAT_ADV_BUTTON_NORMAL", new[] { "normal", "chat", "" }),
            ("ID_CHAT_ADV_BUTTON_CHANNEL", new[] { "channel" }),
            ("ID_CHAT_ADV_BUTTON_PARTY", new[] { "party" }),
            ("ID_CHAT_ADV_BUTTON_TEAM", new[] { "team" }),
            ("ID_CHAT_ADV_BUTTON_PRIVATE", new[] { "whisper", "private" }),
        };

        public static ChatBox Ensure()
        {
            if (Instance) return Instance;
            var ui = UiManager.Instance;
            if (!ui) return null;
            var go = new GameObject("Chat");
            Instance = go.AddComponent<ChatBox>();
            Instance.Build(ui);
            DriftCity.Social.SocialChat.OpenChatInput = s => Instance?.OpenInputWith(s);
            return Instance;
        }

        /// <summary>A line in the system tab (rewards, level-ups, items), whether or not the window is up yet.</summary>
        public static void System(string text) => Ensure()?.Add("system", "", text);

        void Build(UiManager ui)
        {
            var dialog = UiAssets.Layout.Get(260000);
            if (dialog == null) return;
            view = NuiView.Create(dialog, ui.Root);
            view.name = "Chat Window";
            view.transform.SetSiblingIndex(0);   // under the menus, like the HUD
            // the client keeps the (resizable) window just above the minimap; its file position overlaps the minimap frame
            var origin = (RectTransform)view.transform.GetChild(0);
            origin.anchoredPosition += new Vector2(0f, ChatLift);
            // the mode label in front of the input and its drop-down list (FIX_SELECT is the list's blank frame)
            view.SetVisible("ID_CHAT_ADV_FIX_SELECT", false);
            ShowMode();
            foreach (var (id, _) in Tabs) { var c = view.Control(id); if (c) c.SetChecked(true); }

            // the message list over the CHATLIST box
            var list = view.Control("ID_CHAT_ADV_CHATLIST");
            if (list)
            {
                var rt = NuiView.NewRect("Log", list.transform);
                var size = ((RectTransform)list.transform).sizeDelta;
                rt.anchoredPosition = new Vector2(4, -2);
                rt.sizeDelta = new Vector2(size.x - 8, size.y - 4);
                log = rt.gameObject.AddComponent<Text>();
                log.font = UiAssets.Font("Verdana 12", out _, out _);
                log.fontSize = 12;
                log.alignment = TextAnchor.LowerLeft;
                log.supportRichText = true;
                log.raycastTarget = false;
                log.horizontalOverflow = HorizontalWrapMode.Wrap;
                log.verticalOverflow = VerticalWrapMode.Truncate;
                var shadow = rt.gameObject.AddComponent<Shadow>(); shadow.effectColor = Color.black; shadow.effectDistance = new Vector2(1, -1);
            }
            var inputCtrl = view.Control("ID_CHAT_ADV_INPUT");
            input = inputCtrl ? inputCtrl.Input : null;
            if (input) { input.characterLimit = 160; input.textComponent.color = Color.white; input.interactable = false; }
            NuiView.Clicked += OnClick;

            var s = OnlineSession.Instance;
            if (s) s.ChatReceived += Add;
            Add("system", "", OnlineSession.Active ? "Press Enter to chat.  *name text  whisper   &text  party   @text  crew   /invite name   /trade name" : "Welcome to Drift City.");
        }

        void OnClick(NuiView v, string id)
        {
            if (v != view) return;
            if (id == "ID_CHAT_ADV_INPUTOPEN") OpenInput();
            else if (id.StartsWith("ID_CHAT_ADV_FIX_")) { dropdown = !dropdown; ShowMode(); }
            else if (id.StartsWith("ID_CHAT_ADV_DROPDOWN_"))
            {
                string name = id.Substring("ID_CHAT_ADV_DROPDOWN_".Length);
                foreach (var (m, n) in Modes) if (n == name) SetMode(m);
            }
            Redraw();   // the filter tabs toggled themselves
        }

        void OnDestroy()
        {
            NuiView.Clicked -= OnClick;
            if (OnlineSession.Instance) OnlineSession.Instance.ChatReceived -= Add;
            if (view) Destroy(view.gameObject);
            if (Instance == this) Instance = null;
        }

        public void Add(string scope, string from, string text)
        {
            if (scope == "whisper" && !string.IsNullOrEmpty(from))
            {
                // the server's echo carries "(to Name) " (OnlineSession); the client's 10474 / 10475 lines
                var m = global::System.Text.RegularExpressions.Regex.Match(text ?? "", @"^\(to ([^)]+)\) (.*)$", global::System.Text.RegularExpressions.RegexOptions.Singleline);
                if (m.Success) text = Game.MissionText.Get("10474", m.Groups[1].Value, m.Groups[2].Value);
                else
                {
                    text = Game.MissionText.Get("10475", from, text);
                    WhisperTarget = from;
                    replyPrefill = "*" + from + " ";
                }
                from = "";
            }
            string color = scope switch { "party" => "#7fd8ff", "whisper" => "#ff9de6", "system" => "#f0d27a", "notice" => "#ffd34d", "team" => "#9dff9d", "private" => "#aaaaff", _ => "#ffffff" };
            string who = string.IsNullOrEmpty(from) ? "" : $"<b>{Escape(from)}</b>: ";
            lines.Add((scope ?? "", $"<color={color}>{who}{Escape(text)}</color>"));
            if (lines.Count > 80) lines.RemoveAt(0);
            Redraw();
        }

        /// <summary>Picks a chat mode (the DROPDOWN list); Private needs the server's private chat (else 10745).</summary>
        public void SetMode(ChatMode m)
        {
            dropdown = false;
            if (m == ChatMode.Private && !Game.Services.PrivateChannel.CanUse) Add("system", "", Game.MissionText.Get("10745"));
            else mode = m;
            ShowMode();
        }

        void ShowMode()
        {
            if (!view) return;
            foreach (var (m, n) in Modes)
            {
                view.SetVisible("ID_CHAT_ADV_FIX_" + n, m == mode);
                view.SetVisible("ID_CHAT_ADV_DROPDOWN_" + n, dropdown);
            }
        }

        /// <summary>The line as it goes to the server: a prefix wins, else the chosen mode adds its own (0x5b7a67).</summary>
        public string Outgoing(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return text;
            char c = text[0];
            if (c == '!') return text.Substring(1).TrimStart();   // channel
            if (c == '*' || c == '&' || c == '@' || c == '-' || c == '/') return text;
            switch (mode)
            {
                case ChatMode.Party: return "&" + text;
                case ChatMode.Team: return "@" + text;
                case ChatMode.Whisper: return WhisperTarget.Length > 0 ? "*" + WhisperTarget + " " + text : text;
                default: return text;
            }
        }

        /// <summary>Sends a typed line (Enter): mode prefix, private refusal, whisper bookkeeping.</summary>
        public void Submit(string text)
        {
            var line = Outgoing(text);
            if (string.IsNullOrWhiteSpace(line)) return;
            line = Game.NameFilter.Chat(line);   // slang becomes !@#$%^&* (0x5bcf60)
            if (Game.PvpMissionBattle.ChatCommand(line)) return;   // /missionbattle (0x5b8200)
            // private channels: /cjoin .. /cleave, "-channel text" and the Private mode (0x5b6490; Game/Services/PrivateChannel.cs)
            if (Game.Services.PrivateChannel.TryHandle(line, mode == ChatMode.Private)) return;
            replyPrefill = "";
            if (line.Length > 1 && line[0] == '*')
            {
                // 0x5bcf60: "%15s" name, the rest is the message; the input keeps "*Name " for the next line
                int sp = line.IndexOf(' ');
                if (sp > 1)
                {
                    string to = line.Substring(1, sp - 1);
                    if (to.Length > 15) to = to.Substring(0, 15);
                    WhisperTarget = to;
                    replyPrefill = "*" + to + " ";
                }
            }
            OnlineSession.Instance?.SubmitChat(line);
        }

        static string Escape(string s) => (s ?? "").Replace("<", "‹").Replace(">", "›");

        bool Shown(string scope)
        {
            foreach (var (id, scopes) in Tabs)
                if (global::System.Array.IndexOf(scopes, scope) >= 0)
                {
                    var c = view ? view.Control(id) : null;
                    return !c || c.Checked;
                }
            return true;
        }

        void Redraw()
        {
            if (!log) return;
            var shown = new List<string>();
            for (int i = lines.Count - 1; i >= 0 && shown.Count < MaxLines; i--)
                if (Shown(lines[i].scope)) shown.Add(lines[i].text);
            shown.Reverse();
            log.text = string.Join("\n", shown);
        }

        /// <summary>Opens the input line with <paramref name="s"/> typed (whisper / reply from the friend list).</summary>
        public void OpenInputWith(string s)
        {
            OpenInput();
            if (input && typing) { input.text = s; input.caretPosition = s.Length; }
        }

        void OpenInput()
        {
            if (!input || !OnlineSession.Active || ListWindow.IsOpen) return;
            typing = true;
            input.interactable = true;
            // after a whisper (sent or received) the line opens with "*Name " (0x5bd360)
            string pre = replyPrefill;
            input.text = pre;
            input.Select(); input.ActivateInputField();
            if (pre.Length > 0) input.caretPosition = pre.Length;
        }

        void CloseInput()
        {
            typing = false;
            closedFrame = Time.frameCount;
            if (input) { input.DeactivateInputField(); input.interactable = false; }
            if (UnityEngine.EventSystems.EventSystem.current) UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(null);
        }

        void Update()
        {
            bool enter = false, esc = false;
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current;
            if (k != null) { enter = k.enterKey.wasPressedThisFrame || k.numpadEnterKey.wasPressedThisFrame; esc = k.escapeKey.wasPressedThisFrame; }
#else
            enter = Input.GetKeyDown(KeyCode.Return); esc = Input.GetKeyDown(KeyCode.Escape);
#endif
            if (enter)
            {
                if (!typing) OpenInput();
                else
                {
                    var text = input ? input.text : "";
                    CloseInput();
                    if (!string.IsNullOrWhiteSpace(text) && text.Trim() != replyPrefill.Trim()) Submit(text);
                }
            }
            else if (esc && typing) CloseInput();
            else if (esc && dropdown) { dropdown = false; ShowMode(); }
            if (view) view.gameObject.SetActive(!(Game.GameFlow.Instance && !Game.GameFlow.Instance.InCity && !Game.GameFlow.Instance.InParkingLot));
        }
    }
}
