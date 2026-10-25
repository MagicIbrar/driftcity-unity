using System;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Core;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// The "collect 3 props" NPC event (DriftCity_RE\EVENTS.md section 8): Indiana Jones 4 (2008; the code's Greenday /
    /// Sony variants have empty texts and no logic).
    ///
    /// Client [exact]: tick 0x6fcc00 (from 0x701bdd), switch Etc.Indy4Event (net+0x284 bit 31), below 10 km/h within 10 m,
    /// cities 1-4. The NPC stands at post_1st_00658 (Moon Palace) / post_2nd_00595 (Koinonia) / post_3rd_00607 (Cras) /
    /// post_4rd_00618 (Oros); 0x6eca00 picks three distinct posts by rand() % 80 from the city's pool (Moon post_1st_00666-
    /// 00745, Koinonia odd post_2nd_00603-00761, Cras odd post_3rd_00615-00773, Oros odd post_4rd_00185-00343) for the
    /// fedora i_n_00051, the whip i_n_00052 and the crystal skull i_n_00053 (a prop already held counts as found). At a prop
    /// its holder speaks (Irina Spalko Event_Indy4_01, Marion _03, Mud Williams _02); at the NPC: done today ->
    /// Event_Indy4_already; all three found -> 0x494 -> 0x495 Event_Indy4_clear (new props are picked); else Event_Indy4_ing.
    /// Remake rules (the server decided): finding a prop gives its item (props.find), the NPC takes the three back for the
    /// Indiana Jones 4 raffle ticket i_n_00054 and 5,000 Mito once a UTC day (props.claim, src/events.js); OBEVENT 32-35
    /// announce them like the 2008 campaign. The event runs only when listed (DC_EVENTS / events.cfg "indy4").
    /// </summary>
    public static class PropEvent
    {
        public static readonly string[] Props = { "i_n_00051", "i_n_00052", "i_n_00053" };
        static readonly string[] PropText = { "Event_Indy4_01", "Event_Indy4_03", "Event_Indy4_02" };
        static readonly int[] PropOb = { 32, 33, 34 };
        static PlayerProfile P => PlayerProfile.Current;

        public static string NpcPost(string city) => city switch
        {
            "moonpalace" => "post_1st_00658", "koinonia" => "post_2nd_00595", "cras" => "post_3rd_00607", "oros" => "post_4rd_00618", _ => null,
        };

        /// <summary>The city's prop pool (0x6eca00): 80 posts.</summary>
        public static List<string> Pool(string city)
        {
            switch (city)
            {
                case "moonpalace": return Enumerable.Range(666, 80).Select(n => $"post_1st_{n:00000}").ToList();
                case "koinonia": return Enumerable.Range(0, 80).Select(i => $"post_2nd_{603 + 2 * i:00000}").ToList();
                case "cras": return Enumerable.Range(0, 80).Select(i => $"post_3rd_{615 + 2 * i:00000}").ToList();
                case "oros": return Enumerable.Range(0, 80).Select(i => $"post_4rd_{185 + 2 * i:00000}").ToList();
                default: return new List<string>();
            }
        }

        /// <summary>Three distinct posts for the props (client-side, like the original: the server never knew them).</summary>
        public static string[] Pick(string city, System.Random rng)
        {
            var pool = Pool(city);
            if (pool.Count < 3) return new string[0];
            var picked = new List<string>();
            while (picked.Count < 3) { var p = pool[rng.Next(pool.Count)]; if (!picked.Contains(p)) picked.Add(p); }
            return picked.ToArray();
        }

        static void Say(string key, Action after = null)
        {
            var ment = EventUi.Ment(key);
            if (string.IsNullOrEmpty(ment)) { after?.Invoke(); return; }
            MissionUi.Script(ment.Replace("$name", P?.Name ?? "Driver"), "Indiana Jones", false, null, after);
        }

        /// <summary>At a prop: its holder's line and the item (props.find).</summary>
        public static void Found(int idx, Action<bool> done = null)
        {
            if (idx < 0 || idx > 2) { done?.Invoke(false); return; }
            if (P.Count(Props[idx]) > 0) { done?.Invoke(false); return; }
            EventNet.Request("props.find", new Dictionary<string, object> { ["idx"] = idx }, () => FindOffline(P, idx), r =>
            {
                if (!r.Ok) { ChatBox.System(r.Error); done?.Invoke(false); return; }
                EventNet.Log(r.Grants);
                Say(PropText[idx], () => ObEvent.Queue(PropOb[idx]));
                done?.Invoke(true);
            });
        }

        /// <summary>At the NPC: already today / the reward for the three props / the request.</summary>
        public static void AtNpc(Action<bool> done = null)
        {
            var st = EventNet.State(P);
            if (st.PropsDay == EventNet.Today) { Say("Event_Indy4_already"); done?.Invoke(false); return; }
            if (Props.Any(x => P.Count(x) <= 0)) { Say("Event_Indy4_ing"); done?.Invoke(false); return; }
            EventNet.Request("props.claim", new Dictionary<string, object>(), () => ClaimOffline(P), r =>
            {
                if (!r.Ok) { Say(r.Error == "already" ? "Event_Indy4_already" : "Event_Indy4_ing"); done?.Invoke(false); return; }
                EventNet.State(P).PropsDay = EventNet.Today;
                EventNet.Log(r.Grants);
                Say("Event_Indy4_clear", () => ObEvent.Queue(35));
                EventObjects.Instance?.RepickProps();
                done?.Invoke(true);
            });
        }

        public static EventReply FindOffline(PlayerProfile p, int idx)
        {
            if (!SeasonEvents.Is(EventSeason.Indy4)) return EventReply.Fail("The event isn't running.");
            var g = new List<RewardGrant>();
            if (p.Count(Props[idx]) <= 0) g.Add(new RewardGrant { Kind = GrantKind.Item, ItemId = Props[idx], Count = 1 });
            EventNet.Apply(p, g);
            return new EventReply { Ok = true, Grants = g };
        }

        public static EventReply ClaimOffline(PlayerProfile p)
        {
            if (!SeasonEvents.Is(EventSeason.Indy4)) return EventReply.Fail("The event isn't running.");
            var st = EventNet.State(p);
            if (st.PropsDay == EventNet.Today) return EventReply.Fail("already");
            if (Props.Any(x => p.Count(x) <= 0)) return EventReply.Fail("ing");
            foreach (var x in Props) p.Give(x, -1);
            st.PropsDay = EventNet.Today;
            var g = new List<RewardGrant> { new RewardGrant { Kind = GrantKind.Item, ItemId = "i_n_00054", Count = 1 }, new RewardGrant { Kind = GrantKind.Mito, Count = 5000 } };
            EventNet.Apply(p, g);
            return new EventReply { Ok = true, Grants = g };
        }
    }

    /// <summary>
    /// The buff_item drinks (DriftCity_RE\EVENTS.md section 8): the Halloween Drink i_n_halloween (and the Bocchus Drink
    /// i_n_buff). Client [exact] (item use 0x5d620c): refused with 11059 while the Special Drink effect runs at 250 % or more,
    /// else op 0x1a1; the server's 0x481 sets the effect (11058 "[%d%% Special Drink effect] applied", HUD icon by percent
    /// 150 / 200 / 250 / 500, about 30 minutes; 10894 / 11701 when it ends). Remake: the drink starts MitaDrink's Special Drink
    /// effect (mission EXP) at 200 % (Halloween) / 150 % (Bocchus) for 30 minutes, never lowering a stronger one; server
    /// event.drink (src/events.js, economy.js session.mita).
    /// </summary>
    public static class SpecialDrink
    {
        public static readonly Dictionary<string, int> Values = new Dictionary<string, int> { ["i_n_halloween"] = 200, ["i_n_buff"] = 150 };
        public static bool Is(string id) => id != null && Values.ContainsKey(id);
        public static string Describe(string id) => Is(id) ? $"Special Drink {Values[id]}% effect for 30 minutes (mission EXP)." : null;

        /// <summary>ItemUse's entry for a buff_item drink.</summary>
        public static void Use(string id, Action<bool> done)
        {
            var p = PlayerProfile.Current;
            if (p == null || !Is(id) || p.Count(id) <= 0) { done?.Invoke(false); return; }
            if (MitaDrink.Value >= 250) { ChatBox.System(EventUi.T("11059", "Having too much Special Drink will cause serious health problem. Please wait until the effect wears off.")); done?.Invoke(false); return; }
            EventNet.Request("event.drink", new Dictionary<string, object> { ["item"] = id }, () => DrinkOffline(p, id), r =>
            {
                if (!r.Ok) { ChatBox.System(r.Error); done?.Invoke(false); return; }
                int value = r.Data.ContainsKey("value") ? r.Data.I("value") : Values[id];
                var rec = MissionRecords.Current;
                rec.MitaValue = Math.Max(MitaDrink.Value, value);
                rec.MitaUntil = DateTime.UtcNow.AddSeconds(MitaDrink.BuffSeconds).ToString("o");
                rec.Save();
                ChatBox.System(EventUi.T("11058", "[%d%% Special Drink effect] applied", rec.MitaValue - 100));
                EventUi.Sfx("SID_ITEM_INSTALL");
                done?.Invoke(true);
            });
        }

        public static EventReply DrinkOffline(PlayerProfile p, string id)
        {
            if (MitaDrink.Value >= 250) return EventReply.Fail(EventUi.T("11059", "Having too much Special Drink will cause serious health problem. Please wait until the effect wears off."));
            if (!EventNet.Take(p, id)) return EventReply.Fail("You don't have that.");
            var r = new EventReply { Ok = true };
            r.Data["value"] = (double)Math.Max(MitaDrink.Value, Values[id]);
            return r;
        }
    }
}
