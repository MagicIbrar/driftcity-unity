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
    /// Entry point for event items used from the inventory (Inventory/ItemUse.cs finds this class by name:
    /// <c>LuckyBag.Open(itemId, done)</c> and <c>LuckyBag.IsBag(itemId)</c>), and the LUCKYBAG 4180000 screen that opens
    /// gift boxes and lucky bags.
    ///
    /// Client [exact] (DriftCity_RE\EVENTS.md): object ui+0x170 (vtable 0x871858, creator 0x5e50e0, handler 0x5e4cb0). Using a
    /// "luckybag" item (type 0x2d) checks free slots, stores the bag slot (+0x84) and shows the dialog (0x5e4c20: GETPRIZE and
    /// CANCEL enabled, texts 10978 "What item will you win?"). GETPRIZE disables itself and sends op 0x5b4 {bag slot}; the
    /// server answers op 0x5b5 with up to 6 prizes {type, index, count}; slot i is named at t &gt; i+1 s ("Obtained %d %s" 10096,
    /// or the Bust card's text) and fades in during [i, i+1]; CANCEL comes back at t &gt; 6 s. Plain gift boxes (types giftbox,
    /// giftbox_lv, giftbox_car) sent the generic use packet 0x1a1 and had no result screen; this rebuild shows their prizes in
    /// the same dialog. Pools: the item's column 3 names its reward group (rw_xmas_giftbox, rw_patrol_giftbox, rw_package_lv5...).
    /// </summary>
    public static class LuckyBag
    {
        static PlayerProfile P => PlayerProfile.Current;
        public const int Dialog = 4180000;
        static readonly string[] BoxTypes = { "giftbox", "giftbox_lv", "giftbox_car", "luckybag", "chocolet_item" };
        static readonly string[] TokenTypes = { "day_mission" };

        static NuiView view;
        static string bagItem;
        static Action<bool> onDone;
        static List<RewardGrant> prizes;
        static bool got;

        public static NuiView View => view;
        public static bool IsOpen => view;
        public static IReadOnlyList<RewardGrant> LastPrizes => prizes;

        /// <summary>True for every item this module opens (the inventory sends these here instead of its own use rules).</summary>
        public static bool IsBag(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return false;
            var type = EventItems.Type(itemId);
            if (BoxTypes.Contains(type) && EventItems.Pools.Has(EventItems.Pool(itemId))) return true;
            if (type == "lotto" || type == "coupon" || type == "event_box_recipe" || type == "recipe_item" || type == "deliverybox_item" || type == "event_autumn") return true;
            if (type == "gacha" && itemId.StartsWith("gacha_observe_v", StringComparison.OrdinalIgnoreCase)) return true;
            if (type == "gacha_key") return true;   // Key Parts: the compound (GachaInven.Compound)
            if (TokenTypes.Contains(type) || itemId == "i_n_00096" || itemId == "repeat_token") return true;
            return Harvest.PieceSet(itemId) != null;
        }

        /// <summary>Uses an event item: opens its screen. done(true) once something was obtained or spent.</summary>
        public static void Open(string itemId, Action<bool> done)
        {
            var type = EventItems.Type(itemId);
            if (P == null || P.Count(itemId) <= 0) { done?.Invoke(false); return; }
            if (type == "lotto") { Lotto.Open(itemId, done); return; }
            if (type == "gacha") { Gacha.OpenSlot(itemId, done); return; }
            if (type == "gacha_key") { GachaInven.Compound(itemId, done); return; }
            if (type == "chocolet_item") { HarvestScreens.OpenBasket(itemId, done); return; }   // VALEN basket mode: EAT / SEND
            if (type == "coupon") { CouponBook.UseCouponItem(itemId); done?.Invoke(false); return; }
            if (TokenTypes.Contains(type) || itemId == "i_n_00049") { TokenBook.Open(0); done?.Invoke(false); return; }
            if (itemId == "i_n_00096" || itemId == "repeat_token") { TokenBook.Open(1); done?.Invoke(false); return; }
            if (type == "event_box_recipe") { Harvest.CraftBox(itemId, done); return; }
            if (type == "recipe_item") { Harvest.CraftBasket(itemId, done); return; }
            if (type == "deliverybox_item" || type == "event_autumn") { UiPopup.Message(EventItems.Desc(itemId)); done?.Invoke(false); return; }
            if (Harvest.PieceSet(itemId) != null) { Harvest.Combine(itemId, done); return; }
            var row = EventItems.Row(itemId);
            if (row != null && row.Value > P.Level) { UiPopup.Message(EventUi.T("10297", "Your current level is not high enough.")); done?.Invoke(false); return; }
            Show(itemId, done);
        }

        static void Show(string itemId, Action<bool> done)
        {
            var ui = UiManager.Instance;
            if (!ui) { done?.Invoke(false); return; }
            Close();
            view = ui.Open(Dialog);
            EventArtFallback.Apply(view);   // stand-ins for art this install lacks
            if (!view) { done?.Invoke(false); return; }
            bagItem = itemId; onDone = done; prizes = null; got = false;
            view.gameObject.AddComponent<EventAnim>().Tick = Tick;
            for (int i = 1; i <= 6; i++)
            {
                EventUi.Icon(view, "ID_LUCKYBAG_ICON_" + i, null);
                view.SetText("ID_LUCKYBAG_ITEMTEXT_" + i, EventUi.T("10978", "What item will you win?"));
            }
            view.SetEnabled("ID_LUCKYBAG_GETPRIZE", true);
            view.SetEnabled("ID_LUCKYBAG_CANCEL", true);
            ui.On("ID_LUCKYBAG_GETPRIZE", _ => GetPrize());
            ui.On("ID_LUCKYBAG_CANCEL", _ => Close());
            EventUi.Sfx("SID_SYSWIN_CREATE");
        }

        /// <summary>GETPRIZE: the server (or the offline rule) spends the bag and draws its pool.</summary>
        public static void GetPrize()
        {
            if (!view || got) return;
            got = true;
            view.SetEnabled("ID_LUCKYBAG_GETPRIZE", false);
            view.SetEnabled("ID_LUCKYBAG_CANCEL", false);
            string item = bagItem;
            EventNet.Request("event.box", new Dictionary<string, object> { ["item"] = item }, () => OpenOffline(P, item, EventNet.Rng), r =>
            {
                if (!r.Ok) { UiPopup.Message(r.Error ?? "That didn't work."); if (view) view.SetEnabled("ID_LUCKYBAG_CANCEL", true); onDone?.Invoke(false); onDone = null; return; }
                prizes = r.Grants.Take(6).ToList();
                if (view) view.GetComponent<EventAnim>().Restart();
                EventUi.Sfx("SID_ITEM_DROP_GET1");
                EventNet.Log(r.Grants);
                onDone?.Invoke(true); onDone = null;
            });
        }

        /// <summary>Offline rule (server: src/events.js openBox): spend one, draw the item's pool for the driver's level.</summary>
        public static EventReply OpenOffline(PlayerProfile p, string item, System.Random rng)
        {
            var pool = EventItems.Pool(item);
            if (!EventItems.Pools.Has(pool)) return EventReply.Fail("This item can't be opened.");
            var row = EventItems.Row(item);
            if (row != null && row.Value > p.Level) return EventReply.Fail(EventUi.T("10297", "Your current level is not high enough."));
            if (!EventNet.Take(p, item)) return EventReply.Fail("You don't have that item.");
            var grants = EventItems.Pools.Draw(pool, p.Level, rng);
            EventNet.Apply(p, grants);
            return new EventReply { Ok = true, Grants = grants };
        }

        static void Tick(float t)
        {
            if (!view || prizes == null) return;
            for (int i = 0; i < 6; i++)
            {
                string icon = "ID_LUCKYBAG_ICON_" + (i + 1), text = "ID_LUCKYBAG_ITEMTEXT_" + (i + 1);
                if (i >= prizes.Count) { view.SetText(text, ""); continue; }
                float a = Mathf.Clamp01(t - i);
                EventUi.Icon(view, icon, a > 0 ? EventItems.Icon(prizes[i]) : null);
                EventUi.Alpha(view, icon, a);
                if (t > i + 1) view.SetText(text, prizes[i].Kind == GrantKind.Item ? EventUi.T("10096", "Obtained %d %s", prizes[i].Count, EventItems.Describe(new RewardGrant { Kind = GrantKind.Item, ItemId = prizes[i].ItemId, Count = 1, Etc = prizes[i].Etc })) : EventItems.Describe(prizes[i]));
            }
            if (t > Mathf.Min(6f, prizes.Count + 0.01f)) view.SetEnabled("ID_LUCKYBAG_CANCEL", true);
        }

        public static void Close()
        {
            if (view && UiManager.Instance) UiManager.Instance.Close(view);
            view = null;
            if (onDone != null) { var d = onDone; onDone = null; d(false); }
        }
    }

    /// <summary>Per-frame clock for a dialog's animation (time since Restart, unscaled).</summary>
    public sealed class EventAnim : MonoBehaviour
    {
        public Action<float> Tick;
        public float T;
        public float Speed = 1f;
        public void Restart() => T = 0;
        void Update() { T += Time.unscaledDeltaTime * Speed * EventTime.Scale; Tick?.Invoke(T); }
    }

    /// <summary>Tests shorten the event animations.</summary>
    public static class EventTime { public static float Scale = 1f; }
}
