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
    /// The fall harvest ("GoodHarvest") and the other crafting events, plus the event reward popup.
    ///
    /// Client [exact] (DriftCity_RE\EVENTS.md): ingredient boxes event_giftbox_&lt;city&gt; (vegetable / fish / fruit / meat);
    /// the Box Recipe (event_box_recipe_n, AUTUMN 4670000, handler 0x692e30) needs vegetable 12, fish 6, fruit 4, meat 3 and a
    /// Normal or Ice Box and yields a Regular / Medium / Large distribution box (op 0x89 sub 26 b = 0/1/2, texts
    /// 11909-11911); the restaurant NPC (post_N_harvest_01, &lt;ghrestaurantimg&gt;, DONE sends 0xe12, reply 0xe13 {result,
    /// grade, receipts}: GHRestaurantAccept1/2/3 or GHRestaurantAcceptFail, then 11899/11902); the distribution centre
    /// (post_N_harvest_02, THANKSGIVING 4680000, 0xe14/0xe15 {total}; the receipt total sets the prize step by
    /// GHEvent_MAXRECEIPT_LV1..4; EXCHAGE spends a receipt, op 0x89 sub 36 {item, rank 1-4 or loss}: 11908 / 11907 or 11898
    /// "Boom ! Try again"). The Easter egg baskets (VALEN 4660000) take a paint and eggs per recipe (g 10 normal, h 3 normal
    /// + 7 silver, s 3 normal + 3 silver + 4 golden).
    /// Remake rules: pools from rewardgroup.xlt (box_recipe_{normal|ice}_result, restaurant_evaluation x the box's value
    /// 1/2/4, receipt_reward_step_0N); the receipt total is server-wide online and its steps are GHEvent_MAXRECEIPT_LVn /
    /// 1,000 (offline: the driver's own total, / 10,000). The AUTUMN / THANKSGIVING / VALEN screens are HarvestScreens.cs.
    /// </summary>
    public static class Harvest
    {
        static PlayerProfile P => PlayerProfile.Current;
        public static readonly (string id, int n)[] BoxIngredients = { ("event_vegetable_n", 12), ("event_fish_n", 6), ("event_fruit_n", 4), ("event_meat_n", 3) };
        public const string Recipe = "event_box_recipe_n", NormalBox = "event_normalbox_n", IceBox = "event_icebox_n", Receipt = "event_receipt_01";
        public static readonly string[] Boxes = { "eventbox_result_03", "eventbox_result_02", "eventbox_result_01" };
        public static readonly long[] Steps = { 1000000, 10000000, 50000000, 100000000 };

        static int StepScale => OnlineSession.Active ? 1000 : 10000;

        // ---------------- distribution box ----------------

        /// <summary>Using the Box Recipe: AUTUMN 4670000 (HarvestScreens).</summary>
        public static void CraftBox(string recipe, Action<bool> done) => HarvestScreens.OpenAutumn(recipe, done);

        public static EventReply CraftBoxOffline(PlayerProfile p, bool ice, System.Random rng)
        {
            if (p.Count(Recipe) <= 0) return EventReply.Fail("You need the Box Recipe.");
            string box = ice ? IceBox : NormalBox;
            if (p.Count(box) <= 0) return EventReply.Fail($"You need a {EventItems.Name(box)}.");
            foreach (var (id, n) in BoxIngredients) if (p.Count(id) < n) return EventReply.Fail($"Not enough {EventItems.Name(id)} ({p.Count(id)}/{n}).");
            foreach (var (id, n) in BoxIngredients) p.Give(id, -n);
            p.Give(box, -1);
            var g = EventItems.Pools.Draw(ice ? "box_recipe_ice_result" : "box_recipe_normal_result", p.Level, rng).Take(1).ToList();
            EventNet.Apply(p, g);
            return new EventReply { Ok = true, Grants = g };
        }

        // ---------------- restaurant ----------------

        public static void Restaurant(string city)
        {
            MissionUi.Script(EventUi.Ment("GHRestaurant"), "Restaurant Chef", true, () =>
            {
                MissionUi.CloseScript();
                if (!Boxes.Any(b => P.Count(b) > 0)) { OfferBoxes(); return; }
                Request("harvest.deliver", new Dictionary<string, object>(), () => DeliverOffline(P, EventNet.Rng), r =>
                {
                    int receipts = r.Grants.Where(g => g.ItemId == Receipt).Sum(g => g.Count);
                    int grade = r.Data.I("grade");
                    MissionUi.Script(EventUi.Ment(grade >= 3 ? "GHRestaurantAccept1" : grade == 2 ? "GHRestaurantAccept2" : "GHRestaurantAccept3"), "Restaurant Chef", false, null,
                        () => EventPopup.Show(Receipt, EventUi.T("11899", "Fall Harvest Event : Reward for restaurant supplement"), EventUi.T("11902", "You obtained %d items for Reward for restaurant supplement.", receipts)));
                }, () => MissionUi.Script(EventUi.Ment("GHRestaurantAcceptFail"), "Restaurant Chef", false, null, null));
            }, null);
        }

        /// <summary>No distribution box: the recipe is free, Normal / Ice boxes cost their table price in Mito (500 / 1,000).</summary>
        static void OfferBoxes()
        {
            long price = Math.Max(1, EventItems.Row(NormalBox)?.Price ?? 500);
            MissionUi.Confirm($"{ItemCatalog.Clean(EventUi.Ment("GHRestaurantAcceptFail")).Split('\n').LastOrDefault()}\n\n{EventItems.Desc(Recipe)}\nBuy a {EventItems.Name(NormalBox)} for {price:N0} Mito?",
                () => Request("harvest.buybox", new Dictionary<string, object> { ["ice"] = false }, () => BuyBoxOffline(P, false), r => ChatBox.System($"Obtained {EventItems.Name(NormalBox)}."), null));
        }

        public static EventReply BuyBoxOffline(PlayerProfile p, bool ice)
        {
            string box = ice ? IceBox : NormalBox;
            long price = Math.Max(1, EventItems.Row(box)?.Price ?? (ice ? 1000 : 500));
            if (p.Mito < price) return EventReply.Fail(EventUi.T("10293", "You do not have enough Mito for this transaction."));
            p.Mito -= price;
            p.Give(box);
            var g = new List<RewardGrant> { new RewardGrant { Kind = GrantKind.Item, ItemId = box, Count = 1 } };
            if (p.Count(Recipe) <= 0) { p.Give(Recipe); g.Add(new RewardGrant { Kind = GrantKind.Item, ItemId = Recipe, Count = 1 }); }
            return new EventReply { Ok = true, Grants = g };
        }

        /// <summary>Offline twin of harvest.deliver: the biggest box goes; receipts = restaurant_evaluation x the box's value.</summary>
        public static EventReply DeliverOffline(PlayerProfile p, System.Random rng)
        {
            if (!SeasonEvents.Is(EventSeason.Harvest)) return EventReply.Fail("The harvest event isn't running.");
            var box = Boxes.FirstOrDefault(b => p.Count(b) > 0);
            if (box == null) return EventReply.Fail("no box");
            p.Give(box, -1);
            int value = Math.Max(1, EventItems.Row(box)?.Value ?? 1);
            var eval = EventItems.Pools.Draw("restaurant_evaluation", p.Level, rng).FirstOrDefault();
            int grade = eval?.Count ?? 1;
            int receipts = grade * value;
            var g = new List<RewardGrant> { new RewardGrant { Kind = GrantKind.Item, ItemId = Receipt, Count = receipts } };
            EventNet.Apply(p, g);
            EventNet.State(p).HarvestReceipts += receipts;
            var r = new EventReply { Ok = true, Grants = g };
            r.Data["grade"] = (double)grade;
            return r;
        }

        // ---------------- distribution centre ----------------

        public static long ReceiptTotal => OnlineSession.Active && SeasonEvents.ServerState != null ? (long)SeasonEvents.ServerState.N("harvestReceipts") : EventNet.State(P).HarvestReceipts;

        /// <summary>Prize step 1..5 from the receipt total (GHEvent_MAXRECEIPT_LV1..4 / scale).</summary>
        public static int Step(long total, int scale) { int s = 1; foreach (var t in Steps) if (total >= t / scale) s++; return Mathf.Min(s, 5); }

        /// <summary>The clerk's GHDistribution line, then THANKSGIVING 4680000 (0xe14 -> 0xe15, HarvestScreens).</summary>
        public static void Distribution(string city)
        {
            MissionUi.Script(EventUi.Ment("GHDistribution"), "Distribution Center Clerk", true, () =>
            {
                MissionUi.CloseScript();
                if (OnlineSession.Active) SeasonEvents.Refresh(HarvestScreens.OpenThanks); else HarvestScreens.OpenThanks();
            }, null);
        }

        /// <summary>Offline twin of harvest.exchange: spend a receipt, draw receipt_reward_step_0N; rank from the sub-pool (Etc 1-4, 0 = loss).</summary>
        public static EventReply ExchangeOffline(PlayerProfile p, System.Random rng)
        {
            if (!EventNet.Take(p, Receipt)) return EventReply.Fail(EventUi.T("11912", "Need more receipt item."));
            int step = Step(EventNet.State(p).HarvestReceipts, 10000);
            var top = EventItems.Pools.Rows($"receipt_reward_step_{step:00}").Where(x => x.Probability > 0).ToList();
            var r = new EventReply { Ok = true };
            if (top.Count == 0) return r;
            double roll = rng.NextDouble() * top.Sum(x => x.Probability);
            var pick = top.Last();
            foreach (var x in top) { roll -= x.Probability; if (roll < 0) { pick = x; break; } }
            r.Data["rank"] = (double)pick.Etc;
            if (pick.Etc >= 1 && pick.Etc <= 4)
            {
                r.Grants = EventItems.Pools.Draw(pick.ItemId, p.Level, rng).Take(1).ToList();
                EventNet.Apply(p, r.Grants);
            }
            return r;
        }

        // ---------------- Easter egg baskets (the Valentine event) ----------------

        public static readonly Dictionary<string, (int normal, int silver, int golden)> BasketNeeds = new Dictionary<string, (int, int, int)>
        {
            ["g_choco_recipe"] = (10, 0, 0), ["h_choco_recipe"] = (3, 7, 0), ["s_choco_recipe"] = (3, 3, 4),
        };

        /// <summary>Using an egg-basket recipe: VALEN 4660000 in its craft mode (HarvestScreens).</summary>
        public static void CraftBasket(string recipe, Action<bool> done) => HarvestScreens.OpenRecipe(recipe, done);

        public static EventReply BasketOffline(PlayerProfile p, string recipe, System.Random rng)
        {
            if (!BasketNeeds.TryGetValue(recipe, out var n) || p.Count(recipe) <= 0) return EventReply.Fail("You need the recipe.");
            string paint = p.Count("twinkling_wrapping") > 0 ? "twinkling_wrapping" : p.Count("careful_wrapping") > 0 ? "careful_wrapping" : null;
            if (paint == null) return EventReply.Fail($"You need {EventItems.Name("careful_wrapping")}.");
            if (p.Count("black_chocolete") < n.normal || p.Count("white_chocolete") < n.silver || p.Count("pink_chocolete") < n.golden) return EventReply.Fail("Not enough eggs.");
            p.Give(paint, -1); p.Give("black_chocolete", -n.normal); if (n.silver > 0) p.Give("white_chocolete", -n.silver); if (n.golden > 0) p.Give("pink_chocolete", -n.golden);
            string pool = $"rw_{recipe[0]}_recipe_{(paint == "twinkling_wrapping" ? "twinkling" : "careful")}";
            var g = EventItems.Pools.Draw(pool, p.Level, rng).Take(1).ToList();
            EventNet.Apply(p, g);
            return new EventReply { Ok = true, Grants = g };
        }

        // ---------------- piece collections (summer festival letters, snow piles) ----------------

        /// <summary>What a piece item combines into, and the pieces it needs (reward_piece: column 4 names the set,
        /// Etc_Str its result; piece: column 4 the result, column 5 the count). Null when it isn't a collectable piece.</summary>
        public static (string result, List<(string id, int n)> needs)? PieceSet(string item)
        {
            var row = EventItems.Row(item);
            if (row == null) return null;
            if (row.Type == "piece" && row.Col4.Length > 0 && int.TryParse(row.Col5, out int cnt) && cnt > 0)
                return (row.Col4, new List<(string, int)> { (item, cnt) });
            if (row.Type == "reward_piece" && EventItems.Pools.Has(row.Col4))
            {
                var rows = EventItems.Pools.Rows(row.Col4);
                var result = rows.Select(r => r.EtcStr).FirstOrDefault(s => !string.IsNullOrEmpty(s) && s != "n/a");
                if (result == null) return null;
                return (result, rows.Select(r => (r.ItemId, Math.Max(1, r.Count))).Distinct().ToList());
            }
            return null;
        }

        public static void Combine(string item, Action<bool> done)
        {
            Request("event.combine", new Dictionary<string, object> { ["item"] = item }, () => CombineOffline(P, item), r =>
            {
                EventPopup.Show(r.Grants.FirstOrDefault()?.ItemId, EventUi.T("sumfest_event1", "Event!!"), EventUi.T("sumfest_event2", "You have used %s and obtained %s %d!", EventItems.Name(item), EventItems.Name(r.Grants.FirstOrDefault()?.ItemId), 1));
                done?.Invoke(true);
            }, () => done?.Invoke(false));
        }

        public static EventReply CombineOffline(PlayerProfile p, string item)
        {
            var set = PieceSet(item);
            if (set == null) return EventReply.Fail("This can't be combined.");
            foreach (var (id, n) in set.Value.needs) if (p.Count(id) < n) return EventReply.Fail(EventUi.T("11901", "Combination Failed. Not all pieces are gathered."));
            foreach (var (id, n) in set.Value.needs) p.Give(id, -n);
            var g = new List<RewardGrant> { new RewardGrant { Kind = GrantKind.Item, ItemId = set.Value.result, Count = 1 } };
            EventNet.Apply(p, g);
            return new EventReply { Ok = true, Grants = g };
        }

        static void Request(string type, Dictionary<string, object> body, Func<EventReply> offline, Action<EventReply> ok, Action fail)
        {
            EventNet.Request(type, body, offline, r =>
            {
                if (!r.Ok) { if (r.Error != "no box" && r.Error != null) UiPopup.Message(r.Error); fail?.Invoke(); return; }
                EventUi.Sfx("SID_ITEM_DROP_GET1");
                EventNet.Log(r.Grants);
                ok?.Invoke(r);
            });
        }
    }

    /// <summary>
    /// The event reward popups: SEASONEVENT 3340000 (ui+0x370, handler 0x673780; TITLE, CONTENTS, the item icon on POS_ITEM,
    /// OK shows the next queued one) and its older twin OBEVENT 3330000 used for the hard-coded open-beta prizes.
    /// </summary>
    public static class EventPopup
    {
        public const int Dialog = 3340000;
        static NuiView view;
        static readonly Queue<(string icon, string title, string text)> queue = new Queue<(string, string, string)>();
        public static NuiView View => view;

        public static void Show(string icon, string title, string text)
        {
            queue.Enqueue((icon, title, text));
            if (!view) Next();
        }

        static void Next()
        {
            var ui = UiManager.Instance;
            if (!ui) { queue.Clear(); return; }
            if (view) ui.Close(view);
            view = null;
            if (queue.Count == 0) return;
            var (icon, title, text) = queue.Dequeue();
            view = ui.Open(Dialog);
            if (!view) return;
            view.SetText("ID_SEASONEVENT_TITLE", title ?? "");
            view.SetText("ID_SEASONEVENT_CONTENTS", text ?? "");
            view.SetVisible("ID_SEASONEVENT_POS_ITEM", true);
            EventUi.Icon(view, "ID_SEASONEVENT_POS_ITEM", string.IsNullOrEmpty(icon) ? "i_d_m_10k" : icon);
            ui.On("ID_SEASONEVENT_OK", _ => Next());
            EventUi.Sfx("SID_SYSWIN_CREATE");
        }

        public static void CloseAll() { queue.Clear(); if (view && UiManager.Instance) UiManager.Instance.Close(view); view = null; }
    }
}
