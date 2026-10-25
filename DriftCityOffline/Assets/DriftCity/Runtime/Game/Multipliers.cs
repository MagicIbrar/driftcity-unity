using System;
using System.Collections.Generic;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// EXP / Mito multipliers that come from the time and the event calendar (DriftCity_RE\PROGRESS.md).
    ///
    /// Client [exact]: the result rows carry the server's bonus flags (entry +0x8c, PVP.md 14.8): 1 / 2 PC room Mito / EXP,
    /// 8 golden time (the goldentime banner), 0x10 / 0x20 EXP / Mito item, 0x200 / 0x400 Santa EXP 25 % / 50 %, 0x800 the 3rd
    /// anniversary; the tree icons follow the finished Christmas trees. The HUD badge strip MAINBUTTON_EXP_* (3030000, laid
    /// out by 0x5e6090) shows the EXP drink (ONLYEXP1 "EXP+30%"), the Mito drink (ONLYMITO1 "Mito+50%"), the item drink
    /// (ITEM2X), EXPX2 (2X_XMAS), the tree stage (EXP_MITO_XMAS1-4, 30-60 %), the Mita 500 drink (MITABASE + X150/X200/X250/
    /// X500 %, the _HALLO set during Halloween, a two-digit count) and the 3rd anniversary "ExpMito300%" (3RD, while the
    /// bag holds an active a_em_300 item). How much golden time, Santa and the anniversary gave was the server's.
    ///
    /// Remake rules (the server mirrors them in src/multipliers.js and is authoritative online):
    ///  - golden time: every day from <see cref="GoldenFrom"/> to <see cref="GoldenTo"/> o'clock (machine time offline,
    ///    the server's online; events.cfg "golden=20-22" or env DC_GOLDEN, "off" disables): race, chase and Undercity
    ///    rewards +<see cref="GoldenPct"/> % EXP and Mito, result flag 8;
    ///  - Santa: during the Christmas season race and chase EXP +25 % (flag 0x200), +50 % on Dec 24-25 (flag 0x400);
    ///  - 3rd anniversary: during the anniversary season every reward but the combo drop gets EXP and Mito x3
    ///    (+<see cref="AnniversaryPct"/> %, the badge's "300%"), flag 0x800 and the 3RD badge.
    /// They add to the licence, drink, tree, scratch-card and party percents (Progress.Extras / workshop.rewardExtras).
    /// </summary>
    public static class Multipliers
    {
        public const int GoldenPct = 50, SantaPct = 25, SantaDayPct = 50, AnniversaryPct = 200;
        public const int FlagGolden = 8, FlagSanta25 = 0x200, FlagSanta50 = 0x400, FlagAnniversary = 0x800;

        /// <summary>Tests: force golden time on / off (null = by the clock).</summary>
        public static bool? GoldenOverride;
        /// <summary>Tests: the clock (null = now).</summary>
        public static DateTime? Now;

        static DateTime LocalNow => Now ?? DateTime.Now;
        static DateTime UtcNow => Now.HasValue ? Now.Value : DateTime.UtcNow;

        static string goldenCfg;
        static float goldenRead = -100f;

        /// <summary>The golden hours "from-to" (24 h clock, to exclusive); (-1, -1) when switched off.</summary>
        public static (int from, int to) GoldenHours
        {
            get
            {
                var server = ServerState;
                if (server != null) return ParseHours(server.S("goldenHours", "20-22"));
                if (Time.realtimeSinceStartup - goldenRead > 5f || goldenRead < 0f) { goldenCfg = SeasonEvents.ConfigValue("golden"); goldenRead = Time.realtimeSinceStartup; }
                return ParseHours(goldenCfg ?? "20-22");
            }
        }

        public static int GoldenFrom => GoldenHours.from;
        public static int GoldenTo => GoldenHours.to;

        public static (int from, int to) ParseHours(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return (20, 22);
            text = text.Trim().ToLowerInvariant();
            if (text == "off" || text == "none" || text == "0") return (-1, -1);
            var p = text.Split('-');
            if (p.Length == 2 && int.TryParse(p[0], out int a) && int.TryParse(p[1], out int b) && a >= 0 && a <= 24 && b >= 0 && b <= 24 && a != b)
                return (a % 24, b % 24);
            return (20, 22);
        }

        public static bool InHours(int hour, (int from, int to) h) =>
            h.from >= 0 && (h.from < h.to ? hour >= h.from && hour < h.to : hour >= h.from || hour < h.to);

        /// <summary>The server's multiplier state (event.state "multipliers"), or null offline.</summary>
        static Dictionary<string, object> ServerState => OnlineSession.Active ? SeasonEvents.ServerState?.O("multipliers") : null;

        public static bool Golden
        {
            get
            {
                if (GoldenOverride.HasValue) return GoldenOverride.Value;
                var server = ServerState;
                if (server != null && !Now.HasValue) return server.B("golden");
                return InHours(LocalNow.Hour, GoldenHours);
            }
        }

        /// <summary>Santa's EXP percent: 25 in the Christmas season, 50 on Dec 24-25 (UTC, as the season calendar), else 0.</summary>
        public static int Santa
        {
            get
            {
                if (!SeasonEvents.Is(EventSeason.Christmas)) return 0;
                var d = UtcNow;
                return d.Month == 12 && (d.Day == 24 || d.Day == 25) ? SantaDayPct : SantaPct;
            }
        }

        public static bool Anniversary => SeasonEvents.Is(EventSeason.Anniversary);

        static bool GoldenKind(string kind) => kind == "race" || kind == "chase" || kind == "undercity";
        static bool SantaKind(string kind) => kind == "race" || kind == "chase";
        static bool AnniversaryKind(string kind) => !string.IsNullOrEmpty(kind) && kind != "combo";

        public struct Bonus { public int Exp, Mito, Flags; }

        /// <summary>The calendar percents for a reward kind (offline twin of src/multipliers.js bonus()).</summary>
        public static Bonus For(string kind)
        {
            var b = new Bonus();
            try
            {
                if (GoldenKind(kind) && Golden) { b.Exp += GoldenPct; b.Mito += GoldenPct; b.Flags |= FlagGolden; }
                int santa = SantaKind(kind) ? Santa : 0;
                if (santa > 0) { b.Exp += santa; b.Flags |= santa >= SantaDayPct ? FlagSanta50 : FlagSanta25; }
                if (AnniversaryKind(kind) && Anniversary) { b.Exp += AnniversaryPct; b.Mito += AnniversaryPct; b.Flags |= FlagAnniversary; }
            }
            catch (Exception) { }
            return b;
        }

        /// <summary>The bonus flags of the driver's own race result row (golden time, Santa, anniversary).</summary>
        public static int ResultFlags() => For("race").Flags;

        /// <summary>Chat line when golden time starts / ends (remake: the client had the server's notices for this).</summary>
        public static string GoldenText(bool on) => on
            ? $"Golden Time! Races, chases and the Undercity give {GoldenPct}% more EXP and Mito until {GoldenTo:00}:00."
            : "Golden Time is over.";
    }

    /// <summary>
    /// The HUD badge strip MAINBUTTON_EXP_* (dialog 3030000; init 0x5e9920, layout 0x5e6090): while the city HUD is up, the
    /// active bonuses show right to left from their authored spot, each moved left by the width of the ones before it
    /// (the first by 40): EXP drink, Mito drink, item drink, EXPX2, tree stage, Mita 500 (Halloween art in its season),
    /// the 3rd anniversary. EXPX2 is not given by any remake rule and stays hidden.
    /// </summary>
    public sealed class BonusBadges : MonoBehaviour
    {
        public const int Dialog = 3030000, MainBar = 3010000, BasicBar = 3020000;
        static BonusBadges instance;
        NuiView view;
        float next;
        bool wasGolden;
        bool goldenKnown;
        readonly Dictionary<string, float> baseX = new Dictionary<string, float>();

        /// <summary>The badges shown on the last update, in layout order (tests).</summary>
        public static readonly List<string> Shown = new List<string>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot() => Ensure();

        public static void Ensure()
        {
            if (instance) return;
            var go = new GameObject("Bonus Badges");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<BonusBadges>();
        }

        /// <summary>Forces an update now (tests, after a drink).</summary>
        public static void RefreshNow() { if (instance) { instance.next = 0f; instance.Tick(); } }

        void Update()
        {
            if (Time.unscaledTime < next) return;
            next = Time.unscaledTime + 1f;
            Tick();
        }

        void Tick()
        {
            var ui = UiManager.Instance;
            var p = PlayerProfile.Current;
            bool hud = ui && p != null && (ui.Find(MainBar) || ui.Find(BasicBar));
            // golden time starting / ending while driving: a chat line [remake]
            if (hud)
            {
                bool g = Multipliers.Golden;
                if (goldenKnown && g != wasGolden) ChatBox.System(Multipliers.GoldenText(g));
                wasGolden = g; goldenKnown = true;
            }
            // not over a full-screen menu (the shops, the garage ...): the strip belongs to the driving HUD
            var badges = hud && !OriginalMenus.IsOpen ? Wanted(p) : new List<(string[] ids, int width)>();
            Shown.Clear();
            if (badges.Count == 0) { if (view && ui) ui.Close(view); view = null; baseX.Clear(); return; }
            if (!view)
            {
                view = ui.Open(Dialog);
                baseX.Clear();
                if (!view) return;
                foreach (var c in view.Controls) { var rt = c.transform as RectTransform; if (rt) baseX[c.Data.IdName ?? ""] = rt.anchoredPosition.x; }
            }
            foreach (var c in view.Controls) c.gameObject.SetActive(false);
            float shift = 40f;
            foreach (var (ids, width) in badges)
            {
                foreach (var id in ids)
                {
                    var c = view.Control(id);
                    if (!c) continue;
                    c.gameObject.SetActive(true);
                    var rt = c.transform as RectTransform;
                    if (rt && baseX.TryGetValue(id, out float x)) rt.anchoredPosition = new Vector2(x - shift, rt.anchoredPosition.y);
                }
                Shown.Add(ids[0]);
                shift += width;
            }
        }

        const string P = "ID_MAINBUTTON_EXP_";

        /// <summary>The badges to show now (control ids, width), in 0x5e6090's order.</summary>
        public static List<(string[] ids, int width)> Wanted(PlayerProfile p)
        {
            var list = new List<(string[], int)>();
            if (p == null) return list;
            try
            {
                if (ItemUse.ExpBonus(p, "race") > 0) list.Add((new[] { P + "ONLYEXP1" }, 78));
                if (ItemUse.MitoBonus(p, "race") > 0) list.Add((new[] { P + "ONLYMITO1" }, 78));
                if (ItemUse.DoubleDrops(p)) list.Add((new[] { P + "ITEM2X" }, 53));
                int tree = Mathf.Clamp(SeasonEvents.XmasCitiesDone, 0, 4);
                if (tree > 0) list.Add((new[] { P + "MITO_XMAS" + tree }, 111));
                int mita = MitaDrink.Value;
                if (mita > 100)
                {
                    bool hallo = SeasonEvents.Is(EventSeason.Halloween);
                    string sfx = hallo ? "_HALLO" : "";
                    // X150 / X200 / X250 / X500 % (MITAX2/3/4 and X10, X5 in the Halloween set)
                    string x = mita >= 500 ? (hallo ? "X5" : "X10") : mita >= 250 ? "X4" : mita >= 200 ? "X3" : "X2";
                    list.Add((new[] { P + "MITABASE" + sfx, P + "MITATEXT" + sfx, P + "MITA" + x + sfx }, (hallo ? 94 : 90) + 3));
                }
                if (Multipliers.Anniversary) list.Add((new[] { P + "3RD" }, 111));
            }
            catch (Exception) { }
            return list;
        }

        void LateUpdate()
        {
            // the Mita count: minutes of the drink left (the client prints a "%2d" count there)
            if (!view || !view.gameObject.activeInHierarchy) return;
            int left = MitaMinutesLeft();
            view.SetText(P + "MITATEXT", left.ToString("0"));
            view.SetText(P + "MITATEXT_HALLO", left.ToString("0"));
        }

        static int MitaMinutesLeft()
        {
            try
            {
                var r = MissionRecords.Current;
                if (!DateTime.TryParse(r.MitaUntil, null, System.Globalization.DateTimeStyles.RoundtripKind, out var until)) return 0;
                return Mathf.Clamp(Mathf.CeilToInt((float)(until - DateTime.UtcNow).TotalMinutes), 0, 99);
            }
            catch (Exception) { return 0; }
        }
    }
}
