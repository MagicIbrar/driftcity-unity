using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DriftCity.Net;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>The seasonal events the client data supports (the server's op 0x1e config bits Etc.Event*).</summary>
    [Flags]
    public enum EventSeason
    {
        None = 0,
        Christmas = 1,     // Etc.EventChristmas / SnowEffectOn / EventChristmasChase: trees, Santa Gift Boxes, snow
        Valentine = 2,     // Etc.EventValentine: egg baskets (the Easter version of the Valentine chocolates)
        Halloween = 4,     // Etc.EventHalloween: Mita500's Halloween drink lines
        Harvest = 8,       // Etc.EventGoodHarvest: ingredient boxes, restaurants, distribution centres
        Summer = 16,       // the summer festival box event (op 0x89 sub 35 / SEASONEVENT 3340000)
        Anniversary = 32,  // Etc.Event3Year: the attendance greeting and gift
        Indy4 = 64,        // Etc.Indy4Event: the "collect 3 props" NPC (2008 promotion; only by list, never by date)
    }

    /// <summary>
    /// Which seasonal events run. The official server switched them with config bits (DriftCity_RE\EVENTS.md); this rebuild
    /// decides by date, overridable so every event can be tested any day:
    ///  - online: the server's list (event.state, env DC_EVENTS on the server);
    ///  - offline: <see cref="Override"/> (tests), else the command line "-events christmas,harvest", env DC_EVENTS, or
    ///    "events=..." in events.cfg next to the save (Application.persistentDataPath) or the exe; "auto" = by date;
    ///    "date=2025-12-24" picks the events of that day.
    /// Calendar (UTC): Christmas Dec 1 - Jan 6, Valentine/Easter Feb 1 - Apr 30, Summer festival Jul 1 - Aug 31,
    /// Fall harvest Sep 15 - Nov 15, Halloween Oct 15 - Nov 7, anniversary Jun 1 - Jun 14.
    /// </summary>
    public static class SeasonEvents
    {
        /// <summary>Tests: force a set (null = normal rules).</summary>
        public static EventSeason? Override;

        /// <summary>The server's answer to event.state (null offline or before the first answer).</summary>
        public static Dictionary<string, object> ServerState { get; private set; }
        static EventSeason serverSeasons;
        static float nextPoll;

        public static EventSeason Active
        {
            get
            {
                if (Override.HasValue) return Override.Value;
                if (OnlineSession.Active && ServerState != null) return serverSeasons;
                return Configured() ?? ForDate(DateTime.UtcNow);
            }
        }

        public static bool Is(EventSeason s) => (Active & s) != 0;

        public static EventSeason ForDate(DateTime d)
        {
            var s = EventSeason.None;
            int md = d.Month * 100 + d.Day;
            if (md >= 1201 || md <= 106) s |= EventSeason.Christmas;
            if (md >= 201 && md <= 430) s |= EventSeason.Valentine;
            if (md >= 701 && md <= 831) s |= EventSeason.Summer;
            if (md >= 915 && md <= 1115) s |= EventSeason.Harvest;
            if (md >= 1015 && md <= 1107) s |= EventSeason.Halloween;
            if (md >= 601 && md <= 614) s |= EventSeason.Anniversary;
            return s;
        }

        public static EventSeason Parse(string list)
        {
            var s = EventSeason.None;
            foreach (var part in (list ?? "").Split(new[] { ',', ' ', ';', '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var w = part.Trim().ToLowerInvariant();
                if (w == "all") return (EventSeason)127;
                if (w == "easter") w = "valentine";
                if (w == "xmas") w = "christmas";
                if (w == "anni" || w == "3year") w = "anniversary";
                foreach (EventSeason e in Enum.GetValues(typeof(EventSeason)))
                    if (e != EventSeason.None && e.ToString().ToLowerInvariant() == w) s |= e;
            }
            return s;
        }

        static string configCache;
        static float configRead = -100f;

        /// <summary>The offline setting, or null for "by date".</summary>
        static EventSeason? Configured()
        {
            if (Time.realtimeSinceStartup - configRead > 5f || configRead < 0) { configCache = ReadConfig(); configRead = Time.realtimeSinceStartup; }
            var c = configCache;
            if (string.IsNullOrWhiteSpace(c) || c.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)) return null;
            if (c.StartsWith("date=", StringComparison.OrdinalIgnoreCase) && DateTime.TryParse(c.Substring(5), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d))
                return ForDate(d);
            return Parse(c);
        }

        /// <summary>A "key=value" line of events.cfg (next to the save or the exe), or env DC_&lt;KEY&gt;; null when unset.</summary>
        public static string ConfigValue(string key)
        {
            var env = Environment.GetEnvironmentVariable("DC_" + key.ToUpperInvariant());
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            foreach (var dir in new[] { Application.persistentDataPath, Path.GetDirectoryName(Application.dataPath) })
            {
                try
                {
                    var f = Path.Combine(dir ?? "", "events.cfg");
                    if (!File.Exists(f)) continue;
                    foreach (var line in File.ReadAllLines(f))
                    {
                        var l = line.Trim();
                        if (l.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) return l.Substring(key.Length + 1).Trim();
                    }
                }
                catch (Exception) { }
            }
            return null;
        }

        static string ReadConfig()
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "-events") return args[i + 1];
            var env = Environment.GetEnvironmentVariable("DC_EVENTS");
            if (!string.IsNullOrWhiteSpace(env)) return env;
            foreach (var dir in new[] { Application.persistentDataPath, Path.GetDirectoryName(Application.dataPath) })
            {
                try
                {
                    var f = Path.Combine(dir ?? "", "events.cfg");
                    if (!File.Exists(f)) continue;
                    foreach (var line in File.ReadAllLines(f))
                    {
                        var l = line.Trim();
                        if (l.StartsWith("#") || l.Length == 0) continue;
                        if (l.StartsWith("events=", StringComparison.OrdinalIgnoreCase)) return l.Substring(7);
                        if (l.StartsWith("date=", StringComparison.OrdinalIgnoreCase)) return l;
                    }
                }
                catch (Exception) { }
            }
            return null;
        }

        /// <summary>Asks the server for the event state (seasons, scratch-card gauge, Christmas trees, harvest total).</summary>
        public static void Refresh(Action done = null)
        {
            if (!OnlineSession.Active) { done?.Invoke(); return; }
            OnlineSession.Instance.Net.Request("event.state", new Dictionary<string, object>(), m =>
            {
                if (m.B("ok")) { ServerState = m; serverSeasons = Parse(string.Join(",", (m.L("seasons") ?? new List<object>()).Select(x => x?.ToString()))); }
                done?.Invoke();
            });
        }

        /// <summary>Polled by EventObjects: the online state every 20 s.</summary>
        public static void Tick()
        {
            if (!OnlineSession.Active) { ServerState = null; return; }
            if (Time.unscaledTime < nextPoll) return;
            nextPoll = Time.unscaledTime + 20f;
            Refresh();
        }

        /// <summary>Server-wide state value (online) or null.</summary>
        public static Dictionary<string, object> Shared(string key) => ServerState?.O(key);

        /// <summary>Cities with a finished Christmas tree (0-4): each adds to the EXP / Mito badge (30/40/50/60 %).</summary>
        public static int XmasCitiesDone
        {
            get
            {
                if (!Is(EventSeason.Christmas)) return 0;
                var masks = XmasTree.Masks();
                return masks.Count(m => (m & 0x3ff) == 0x3ff);
            }
        }

        /// <summary>The Christmas bonus the HUD badge (ID_MAINBUTTON_EXP_MITO_XMAS1-4) shows: 30/40/50/60 % for 1-4 trees.</summary>
        public static int XmasBonusPct => XmasCitiesDone == 0 ? 0 : 20 + 10 * XmasCitiesDone;
    }
}
