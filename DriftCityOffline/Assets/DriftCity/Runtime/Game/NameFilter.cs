using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DriftCity.Core;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// The client's word filter (DriftCity_RE/AUDIO.md 6; object at game+0x13c, vtable 0x887780, made at 0x43b3d1): two
    /// lists read at start, both lowercased (0x7e0340 / 0x7e0200: MultiByteToWideChar, _wcslwr):
    /// <list type="bullet">
    /// <item>init\slang.xlt (6 rows: "=w=)", "=w=", "=)", "=/", DEL, "=w=)/") at +8;</item>
    /// <item>init\operatormisrepresent.xlt (201 rows: ADM, DEV, GM, admin, the staff names, '[' ']', the Latin-1 letters
    ///   U+00C0..U+00FF...) at +0x18.</item>
    /// </list>
    /// Checks are case-insensitive substring searches. vtable +0xc / +0x14 answer "contains" (0x7df5d0 slang, 0x7df6c0
    /// operator names); +0x10 / +0x18 censor (0x7dfcd0 / 0x7dfed0): every character of a match is replaced with a random one
    /// of "!@#$%^&amp;*". Callers: character creation 0x749f02 (slang, then operator names -> 10606 "That ID is not allowed.
    /// Please use a different name."), room titles 0x57a3ee (slang -> 10608 "That Room name is not allowed..."), the chat
    /// line sent (0x5bcf60, slang censored), a received system chat line containing slang is dropped (0x5b4b70), notes
    /// (0x670c50, censored), party invites (0x61e100), custom-shop gift texts (0x64f9c0, operator names + both censors).
    /// The server runs the same rules (DriftCityServer/src/namefilter.js).
    /// </summary>
    public static class NameFilter
    {
        static List<string> slang, operatorNames;
        const string Symbols = "!@#$%^&*";
        static readonly System.Random rng = new System.Random();

        /// <summary>Text 10606 "That ID is not allowed.  Please use a different name."</summary>
        public const string NameRefused = "10606";
        /// <summary>Text 10607 "That Crew name is not allowed.  Please use a different name."</summary>
        public const string CrewRefused = "10607";
        /// <summary>Text 10608 "That Room name is not allowed.  Please use a different name."</summary>
        public const string RoomRefused = "10608";

        /// <summary>The filter is on in the game; the PlayMode suite turns it off (its drivers are called "Tester"...) and the
        /// filter's own tests turn it back on, as the server does under the test runner.</summary>
        public static bool Enabled = true;

        public static IReadOnlyList<string> Slang { get { Load(); return slang; } }
        public static IReadOnlyList<string> OperatorNames { get { Load(); return operatorNames; } }

        public static void ResetCache() { slang = null; operatorNames = null; }

        static void Load()
        {
            if (slang != null) return;
            slang = Read(@"init\slang.xlt");
            operatorNames = Read(@"init\operatormisrepresent.xlt");
        }

        /// <summary>The second column of each row, lowercased (the loaders read "%d" then "%s").</summary>
        static List<string> Read(string path)
        {
            var list = new List<string>();
            var fs = DriftCityData.Files;
            try
            {
                if (fs == null || !fs.Exists(path)) return list;
                var t = XltTable.Parse(fs.Read(path));
                foreach (var r in t.Rows)
                {
                    if (r.Length < 2) continue;
                    // "%s" stops at white space: the row's word is its first token
                    var w = r[1].Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    if (!string.IsNullOrEmpty(w)) list.Add(w.ToLowerInvariant());
                }
            }
            catch (Exception e) { Debug.LogWarning(path + ": " + e.Message); }
            return list;
        }

        static bool Contains(IReadOnlyList<string> words, string text)
        {
            if (!Enabled || string.IsNullOrEmpty(text)) return false;
            string low = text.ToLowerInvariant();
            foreach (var w in words) if (low.Contains(w)) return true;
            return false;
        }

        /// <summary>vtable +0xc (0x7df5d0): the text holds a slang entry.</summary>
        public static bool HasSlang(string text) => Contains(Slang, text);

        /// <summary>vtable +0x14 (0x7df6c0): the text holds an operator / reserved name.</summary>
        public static bool HasOperatorName(string text) => Contains(OperatorNames, text);

        /// <summary>Character creation (0x749f02): neither list may match. Returns the refusal text id or null.</summary>
        public static string CheckDriverName(string name) => HasSlang(name) || HasOperatorName(name) ? NameRefused : null;

        /// <summary>Crew names: the same two lists (10607).</summary>
        public static string CheckCrewName(string name) => HasSlang(name) || HasOperatorName(name) ? CrewRefused : null;

        /// <summary>CREATEROOM OK (0x57a3ee): the slang list only. Returns 10608 or null.</summary>
        public static string CheckRoomTitle(string title) => HasSlang(title) ? RoomRefused : null;

        /// <summary>The refusal as the game shows it (globaltext).</summary>
        public static string Text(string id) => GameTables.Text(id);

        /// <summary>vtable +0x10 (0x7dfcd0): every matched character replaced with a random one of "!@#$%^&amp;*".</summary>
        public static string CensorSlang(string text) => Censor(Slang, text);

        /// <summary>vtable +0x18 (0x7dfed0): the same over the operator names.</summary>
        public static string CensorOperatorNames(string text) => Censor(OperatorNames, text);

        /// <summary>The chat line sent (0x5bcf60): slang censored.</summary>
        public static string Chat(string text) => CensorSlang(text);

        static string Censor(IReadOnlyList<string> words, string text)
        {
            if (!Enabled || string.IsNullOrEmpty(text)) return text;
            var sb = new StringBuilder(text);
            string low = text.ToLowerInvariant();
            foreach (var w in words)
            {
                if (w.Length == 0) continue;
                int i = low.IndexOf(w, StringComparison.Ordinal);
                while (i >= 0)
                {
                    for (int k = 0; k < w.Length && i + k < sb.Length; k++) sb[i + k] = Symbols[rng.Next(Symbols.Length)];
                    i = low.IndexOf(w, i + w.Length, StringComparison.Ordinal);
                }
            }
            return sb.ToString();
        }
    }
}
