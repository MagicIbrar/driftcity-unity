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
    /// The Christmas tree of each city (XMASTREEEVENT 4440000) and its server-wide decorations.
    ///
    /// Client [exact] (DriftCity_RE\EVENTS.md): object ui+0x428 (vtable 0x874fa4, creator 0x694e60, tick 0x694ab0, Show
    /// 0x694930, handler 0x694de0); +8 the city's 10-bit decoration mask, +0xc the number of finished cities. ICON1-10 show
    /// only while the bag holds a Santa Gift Box (i_n_00088); a set bit shows SOLD_ICONn instead; ICON10 (the star topper)
    /// stays hidden until the 9 others are set; the notice cycles 11437/11438/11439 every 10 s. A click sends op 0xcee
    /// {decoration 0-9, gift box} (sound, X_tree001_button effect); op 0xcef {?, citiesDone, mask} updates. The trees stand at
    /// post_&lt;N&gt;_xmas_01 (moon_xmas_tree, koi_xmas_tree, cras_xmas_tree, oros_xmas_tree), open within 7 m below 10 km/h,
    /// and grow X_tree001_01..06.nif with the count (02 at 2, 03 at 4, 04 at 6, 05 at 8, 06 at 10). Finished trees give
    /// everyone the HUD badge EXP_MITO_XMAS1-4 (30/40/50/60 %) and turn rain into snow (x_snow.nif).
    /// Remake rules: one Santa Gift Box per decoration (the 750/1200 donations of globalcontent XmasTreeMaxCount were the
    /// official server's crowd goal); each donation returns a Battle EXP 25 % ticket (11438). Online the masks are shared
    /// by every player on the server (src/events.js); offline each driver decorates their own trees.
    /// </summary>
    public static class XmasTree
    {
        static PlayerProfile P => PlayerProfile.Current;
        public const int Dialog = 4440000;
        public const string GiftBox = "i_n_00088", Ticket = "i_n_battleEXP_25";
        /// <summary>City order of the masks (and the server's): cras, moonpalace, koinonia, oros.</summary>
        public static readonly string[] Cities = { "cras", "moonpalace", "koinonia", "oros" };
        static readonly string[] DoneText = { "11442", "11440", "11441", "11443" };

        static NuiView view;
        static string city;
        static float nextNotice, clickLock;
        public static NuiView View => view;

        public static int CityIndex(string c) => Array.IndexOf(Cities, (c ?? "").ToLowerInvariant());

        public static int[] Masks()
        {
            var shared = SeasonEvents.ServerState?.L("xmas");
            if (OnlineSession.Active && shared != null) return Enumerable.Range(0, 4).Select(i => i < shared.Count ? Convert.ToInt32(shared[i]) : 0).ToArray();
            return (int[])EventNet.State(P).XmasMask.Clone();
        }

        public static int Mask(string c) { int i = CityIndex(c); return i < 0 ? 0 : Masks()[i]; }
        public static int Count(int mask) { int n = 0; for (int b = 0; b < 10; b++) if ((mask & (1 << b)) != 0) n++; return n; }
        /// <summary>The tree model for a decoration count: X_tree001_01..06.</summary>
        public static int Stage(int count) => count >= 10 ? 6 : count >= 8 ? 5 : count >= 6 ? 4 : count >= 4 ? 3 : count >= 2 ? 2 : 1;

        public static void Open(string cityName)
        {
            var ui = UiManager.Instance;
            if (!ui || P == null || CityIndex(cityName) < 0) return;
            Close();
            view = ui.Open(Dialog);
            EventArtFallback.Apply(view);   // stand-ins for art this install lacks
            if (!view) return;
            city = cityName;
            nextNotice = 0;
            view.gameObject.AddComponent<EventAnim>().Tick = Tick;
            for (int i = 1; i <= 10; i++) { int slot = i - 1; ui.On("ID_XMASTREEEVENT_BTN_ICON" + i, _ => Donate(city, slot)); }
            ui.On("ID_XMASTREEEVENT_BTN_EXIT", _ => Close());
            Refresh();
            EventUi.Sfx("SID_SYSWIN_CREATE");
        }

        static void Tick(float t)
        {
            if (!view) return;
            if (t >= nextNotice)
            {
                nextNotice = t + 10f;
                string[] keys = { "11437", "11438", "11439" };
                var text = ItemCatalog.Clean(EventUi.T(keys[UnityEngine.Random.Range(0, 3)], ""));
                view.SetText("ID_XMASTREEEVENT_STATIC_NOTICE", EventUi.T("10037", "Notice"));
                view.SetText("ID_XMASTREEEVENT_STATIC_NOTICE_TEXT", text);
            }
            if (Time.unscaledTime > clickLock) Refresh();
        }

        public static void Refresh()
        {
            if (!view) return;
            int mask = Mask(city);
            bool hasBox = P.Count(GiftBox) > 0 && Time.unscaledTime > clickLock;
            int done = Count(mask & 0x1ff);
            for (int i = 1; i <= 10; i++)
            {
                bool set = (mask & (1 << (i - 1))) != 0;
                view.SetVisible("ID_XMASTREEEVENT_BTN_SOLD_ICON" + i, set);
                bool show = hasBox && !set && (i < 10 || done >= 9);
                view.SetVisible("ID_XMASTREEEVENT_BTN_ICON" + i, show);
            }
        }

        public static void Donate(string cityName, int slot, Action<bool> done = null)
        {
            if (P == null) { done?.Invoke(false); return; }
            clickLock = Time.unscaledTime + 0.5f;
            EventNet.Request("xmas.donate", new Dictionary<string, object> { ["city"] = cityName, ["slot"] = slot }, () => DonateOffline(P, cityName, slot), r =>
            {
                if (!r.Ok) { UiPopup.Message(r.Error ?? "That didn't work."); done?.Invoke(false); return; }
                GameEffects.XmasDecoration();   // 0x694aa4: SID_XMAS_EVENT_SUCCESS + X_tree001_button
                EventNet.Log(r.Grants);
                if (r.Data.B("completed") || Mask(cityName) == 0x3ff) ChatBox.System(EventUi.T(DoneText[Mathf.Max(0, CityIndex(cityName))], "The Christmas tree is now fully decorated. Thank you for your support."));
                SeasonEvents.Refresh(() => { Refresh(); EventObjects.Instance?.RefreshTrees(); });
                Refresh();
                EventObjects.Instance?.RefreshTrees();
                done?.Invoke(true);
            });
        }

        /// <summary>Offline twin of xmas.donate.</summary>
        public static EventReply DonateOffline(PlayerProfile p, string cityName, int slot)
        {
            int c = CityIndex(cityName);
            if (!SeasonEvents.Is(EventSeason.Christmas)) return EventReply.Fail("The Christmas event isn't running.");
            if (c < 0 || slot < 0 || slot > 9) return EventReply.Fail("There's no tree here.");
            var st = EventNet.State(p);
            int mask = st.XmasMask[c];
            if ((mask & (1 << slot)) != 0) return EventReply.Fail("That decoration is already on the tree.");
            if (slot == 9 && Count(mask & 0x1ff) < 9) return EventReply.Fail(ItemCatalog.Clean(EventUi.T("11439", "You can donate Star Tree Topper only after 9 other decorations are added to the tree.")));
            if (!EventNet.Take(p, GiftBox)) return EventReply.Fail("You need a Santa Gift Box.");
            st.XmasMask[c] = mask | (1 << slot);
            var g = new List<RewardGrant> { new RewardGrant { Kind = GrantKind.Item, ItemId = Ticket, Count = 1 } };
            EventNet.Apply(p, g);
            var r = new EventReply { Ok = true, Grants = g };
            r.Data["completed"] = st.XmasMask[c] == 0x3ff;
            return r;
        }

        public static void Close() { if (view && UiManager.Instance) UiManager.Instance.Close(view); view = null; }
    }
}
