using System.Collections.Generic;
using System.Linq;
using DriftCity.Net;

namespace DriftCity.Game.Services
{
    /// <summary>
    /// Private channels and the chat's Private mode (DriftCity_RE/SERVICES.md section 2; server src/privchan.js).
    /// Client [exact]: the chat input 0x5b6490 reads the commands of globaltext 11160-11165 ("/cjoin ", "/csay ", "/ckick ",
    /// "/cauth ", "/cwho", "/cleave"): /cjoin refuses with 11175 while the client already has a channel (ui+0x160+0x138),
    /// the others answer 11168 "Not in a private channel." without one; /ckick and /cauth skip your own name (0x5bd190 /
    /// 0x5bd1f0); "-&lt;channel&gt; text" (0x5bcd40) and mode 6 (0x5bcf60 "private") send to the channel. The pushes: joined
    /// 0x08f (11166 self, 11169 others), left 0x091 (11167 / 11170, the channel is cleared for yourself), kicked 0xa29
    /// (11174 / 11173), admin 0xa2b (11177 / 11178), users 0xa2d (one line per name, then 11171), colour 0xFFAAAAFF.
    /// Remake: the server switch <c>privateChat</c>; offline or switched off every private command answers 10745.
    /// </summary>
    public static class PrivateChannel
    {
        /// <summary>The private channel the driver is in ("" = none).</summary>
        public static string Current { get; private set; } = "";
        public static bool Admin { get; private set; }
        /// <summary>The last /cwho list (tests).</summary>
        public static readonly List<string> LastWho = new List<string>();

        public static bool CanUse => ServicesNet.Online && ServerSwitches.PrivateChat;

        static string Cmd(string key, string fallback)
        {
            string t = GameTables.Text(key);
            return string.IsNullOrEmpty(t) || t == key ? fallback : t;
        }

        static string Me => PlayerProfile.Current?.Name ?? "";
        static void Line(string text) => ChatBox.Ensure()?.Add("private", "", text);
        static void Sys(string key, params object[] args) => Line(MissionText.Get(key, args));

        /// <summary>The chat's private command check; true when the line was a private-channel line (sent or refused).</summary>
        public static bool TryHandle(string line, bool privateMode)
        {
            line = (line ?? "").Trim();
            if (line.Length == 0) return false;
            string join = Cmd("11160", "/cjoin "), say = Cmd("11161", "/csay "), kick = Cmd("11162", "/ckick "), auth = Cmd("11163", "/cauth "),
                who = Cmd("11164", "/cwho").Trim(), leave = Cmd("11165", "/cleave").Trim();
            bool Starts(string cmd) => line.Length > cmd.Length && line.StartsWith(cmd, System.StringComparison.OrdinalIgnoreCase);
            bool Is(string cmd) => string.Equals(line, cmd, System.StringComparison.OrdinalIgnoreCase);
            bool isCmd = Starts(join) || Starts(say) || Starts(kick) || Starts(auth) || Is(who) || Is(leave);
            bool dash = line[0] == '-';
            bool modeLine = privateMode && !isCmd && "/*&@!".IndexOf(line[0]) < 0;
            if (!isCmd && !dash && !modeLine) return false;
            if (!CanUse) { ChatBox.Ensure()?.Add("system", "", MissionText.Get("10745")); return true; }

            if (Starts(join))
            {
                if (Current.Length > 0) { Sys("11175"); return true; }
                Send("pchan.join", new Dictionary<string, object> { ["name"] = line.Substring(join.Length).Trim() }, r => { if (r.B("ok")) { Current = r.S("channel", ""); Admin = r.B("admin"); } });
                return true;
            }
            if (dash)
            {
                // "-<channel> text": the first word names the channel
                string rest = line.Substring(1);
                int sp = rest.IndexOf(' ');
                if (sp <= 0) return true;
                return Say(rest.Substring(sp + 1), rest.Substring(0, sp));
            }
            if (Current.Length == 0) { Sys("11168"); return true; }
            if (Starts(say)) return Say(line.Substring(say.Length), null);
            if (modeLine) return Say(line, null);
            if (Starts(kick) || Starts(auth))
            {
                bool k = Starts(kick);
                string name = line.Substring((k ? kick : auth).Length).Trim();
                if (name.Length == 0 || string.Equals(name, Me, System.StringComparison.OrdinalIgnoreCase)) return true;
                Send(k ? "pchan.kick" : "pchan.auth", new Dictionary<string, object> { ["name"] = name }, null);
                return true;
            }
            if (Is(who))
            {
                Send("pchan.who", null, r =>
                {
                    if (!r.B("ok")) return;
                    var names = (r.L("names") ?? new List<object>()).Select(x => x.ToString()).ToList();
                    LastWho.Clear(); LastWho.AddRange(names);
                    foreach (var n in names) Line(n);
                    Sys("11171", names.Count, r.S("channel", Current));
                });
                return true;
            }
            Send("pchan.leave", null, r => { if (r.B("ok")) { Current = ""; Admin = false; } });
            return true;
        }

        static bool Say(string text, string channel)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return true;
            var body = new Dictionary<string, object> { ["text"] = text };
            if (!string.IsNullOrEmpty(channel)) body["channel"] = channel;
            Send("pchan.say", body, null);
            return true;
        }

        static void Send(string type, Dictionary<string, object> body, System.Action<Dictionary<string, object>> ok)
        {
            ServicesNet.Request(type, body, r =>
            {
                if (r == null || !r.B("ok")) { Line(ServicesNet.Text(r)); return; }
                ok?.Invoke(r);
            });
        }

        /// <summary>The server's pchan pushes (joined / left / kicked / admin) as the client's chat lines.</summary>
        public static void OnPush(Dictionary<string, object> m)
        {
            string kind = m.S("kind"), ch = m.S("channel", ""), name = m.S("name", "");
            bool self = string.Equals(name, Me, System.StringComparison.OrdinalIgnoreCase);
            switch (kind)
            {
                case "joined":
                    Current = ch;
                    if (self) Sys("11166", ch); else Sys("11169", name, ch);
                    break;
                case "left":
                    if (self) { Sys("11167", ch); Current = ""; Admin = false; } else Sys("11170", name, ch);
                    break;
                case "kicked":
                    if (self) { Sys("11174", ch); Current = ""; Admin = false; } else Sys("11173", name, ch);
                    break;
                case "admin":
                    Admin = self;
                    Sys(self ? "11178" : "11177", name, ch);
                    break;
            }
        }

        /// <summary>A new session starts outside every private channel.</summary>
        public static void Reset() { Current = ""; Admin = false; LastWho.Clear(); }
    }
}
