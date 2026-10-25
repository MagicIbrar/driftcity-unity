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
    /// LOTTO 3860000, the "100% Success Instant Scratch Card" (scratch cards lotto_scratchies_* from the item bag).
    ///
    /// Client [exact] (DriftCity_RE\EVENTS.md): object ui+0x16c (creator 0x5e4900, handler 0x5e4530, Show 0x5e3d80, Update
    /// 0x5e3f70, reveal 0x5e3b90). The server opened it (op 0x5ab -> 0x4f8f70) when the card was used; Show enables GETPRIZE /
    /// CANCEL, writes 10978 "What item will you win?" in the three prize lines, shows frame 1 of each column's six scratch
    /// frames (PRIZESCRATCH_11..16, 21..26, 31..35) and, when BattleZone.LottoPVPReward (net+0x274 &amp; 0x200) is on, the
    /// server-wide gauge with its current / next server prize (lotto_00002..4: Battle Zone EXP +5/10/30 % for everyone).
    /// GETPRIZE disables both buttons and sends op 0x5ac {card slot}; op 0x5ad -> 0x5e3c50 carries the three prizes; then
    /// 6 s of scratching, 2 s per column (a random frame at a random alpha each tick, text 10979 "Scratching the card..."),
    /// each column revealed at its end ("name count", or the Bust card's "Sorry, maybe next time!"), CANCEL back on and
    /// 10977 "Any winning item will be saved in your [F3] inventory." in the chat.
    /// Remake rules: the card's reward group (useitemclient column 3, e.g. lotto_Natura = one prize per column from
    /// lotto_usa_0902 / lotto_usa_mito / lotto_usa_Natura); cards are bought for Mito (<see cref="CardPrice"/>); the gauge
    /// counts the cards scratched today (UTC) server-wide, goals <see cref="Goals"/> online, a tenth of that offline.
    /// </summary>
    public static class Lotto
    {
        static PlayerProfile P => PlayerProfile.Current;
        public const int Dialog = 3860000;
        public static readonly int[] Goals = { 50, 150, 500 };
        public static readonly int[] OfflineGoals = { 5, 15, 50 };
        public static readonly string[] GoalPrizes = { "lotto_00002", "lotto_00003", "lotto_00004" };
        public static readonly int[] GoalExpPct = { 5, 10, 30 };

        static NuiView view;
        static string card;
        static Action<bool> onDone;
        static List<RewardGrant> prizes;
        static bool scratching;

        public static NuiView View => view;
        public static bool IsOpen => view;
        public static IReadOnlyList<RewardGrant> LastPrizes => prizes;
        public static bool Done => view && prizes != null && !scratching;

        /// <summary>Mito price of a scratch card: the item table's price column (G-Coin 500) x 100. No real money anywhere.</summary>
        public static long CardPrice(string cardId) => Math.Max(1, EventItems.Row(cardId)?.Price ?? 500) * 100L;

        public static IEnumerable<string> Cards => new[] { "lotto_scratchies_Natura", "lotto_scratchies_Dew", "lotto_scratchies_Serpens", "lotto_scratchies_SoraGT", "lotto_scratchies_Leopard", "lotto_scratchies_widow" }
            .Where(c => EventItems.Pools.Has(EventItems.Pool(c)));

        /// <summary>Buys a scratch card for Mito (hook for the visual shop / an NPC counter).</summary>
        public static void BuyCard(string cardId, Action<bool> done = null)
        {
            EventNet.Request("lotto.buy", new Dictionary<string, object> { ["item"] = cardId }, () =>
            {
                if (EventItems.Type(cardId) != "lotto") return EventReply.Fail("That isn't a scratch card.");
                long price = CardPrice(cardId);
                if (P.Mito < price) return EventReply.Fail(EventUi.T("10293", "You do not have enough Mito for this transaction."));
                P.Mito -= price; P.Give(cardId);
                return new EventReply { Ok = true };
            }, r =>
            {
                if (!r.Ok) UiPopup.Message(r.Error ?? "That didn't work.");
                else { EventUi.Sfx("SID_ITEM_BUY"); ChatBox.System($"Obtained {EventItems.Name(cardId)}."); }
                done?.Invoke(r.Ok);
            });
        }

        /// <summary>Cards scratched today and the goal reached (0..3); online from the server's event.state.</summary>
        public static (int cards, int[] goals) Gauge()
        {
            var shared = SeasonEvents.ServerState;
            if (OnlineSession.Active && shared != null) return (shared.I("lottoCards"), Goals);
            var st = EventNet.State(P);
            return (st.LottoDay == EventNet.Today ? st.LottoCards : 0, OfflineGoals);
        }

        /// <summary>The Battle Zone EXP bonus the gauge has unlocked for everyone today (hook for race rewards).</summary>
        public static int BattleExpBonusPct
        {
            get { var (n, g) = Gauge(); int pct = 0; for (int i = 0; i < 3; i++) if (n >= g[i]) pct = GoalExpPct[i]; return pct; }
        }

        public static void Open(string cardId, Action<bool> done)
        {
            var ui = UiManager.Instance;
            if (!ui || P == null) { done?.Invoke(false); return; }
            Close();
            view = ui.Open(Dialog);
            EventArtFallback.Apply(view);   // stand-ins for art this install lacks
            if (!view) { done?.Invoke(false); return; }
            card = cardId; onDone = done; prizes = null; scratching = false;
            EventUi.Backdrop(view, new Vector2(12, 128), new Vector2(330, 450));
            view.gameObject.AddComponent<EventAnim>().Tick = Tick;
            view.SetText("ID_LOTTO_TITLE", EventUi.T("10976", "100% Success Instant Scratch Card"));
            for (int k = 1; k <= 3; k++) view.SetText("ID_LOTTO_PRIZETEXT_" + k, EventUi.T("10978", "What item will you win?"));
            for (int col = 1; col <= 3; col++)
                for (int f = 1; f <= 6; f++) { view.SetVisible($"ID_LOTTO_PRIZESCRATCH_{col}{f}", f == 1); EventUi.Alpha(view, $"ID_LOTTO_PRIZESCRATCH_{col}{f}", 1f); }
            view.SetEnabled("ID_LOTTO_GETPRIZE", true);
            view.SetEnabled("ID_LOTTO_CANCEL", true);
            ShowGauge();
            ui.On("ID_LOTTO_GETPRIZE", _ => Scratch());
            ui.On("ID_LOTTO_CANCEL", _ => Close());
            EventUi.Sfx("SID_SYSWIN_CREATE");
        }

        static void ShowGauge()
        {
            if (!view) return;
            var (n, goals) = Gauge();
            int reached = goals.Count(g => n >= g);
            int lo = reached == 0 ? 0 : goals[reached - 1], hi = reached < 3 ? goals[reached] : goals[2];
            float frac = reached >= 3 ? 1f : Mathf.Clamp01((float)(n - lo) / Mathf.Max(1, hi - lo));
            var gauge = view.Control("ID_LOTTO_GAUGE");
            if (gauge)
            {
                foreach (var img in gauge.GetComponentsInChildren<UnityEngine.UI.RawImage>(true))
                {
                    var rt = img.rectTransform;
                    if (!img.gameObject.TryGetComponent<GaugeWidth>(out var gw)) { gw = img.gameObject.AddComponent<GaugeWidth>(); gw.Width = rt.sizeDelta.x; gw.Uv = img.uvRect; }
                    rt.sizeDelta = new Vector2(gw.Width * frac, rt.sizeDelta.y);
                    img.uvRect = new Rect(gw.Uv.x, gw.Uv.y, gw.Uv.width * frac, gw.Uv.height);
                }
            }
            EventUi.Icon(view, "ID_LOTTO_SERVERPRIZE_CURRENT", reached > 0 ? GoalPrizes[reached - 1] : "lotto_00001");
            EventUi.Icon(view, "ID_LOTTO_SERVERPRIZE_NEXT", reached < 3 ? GoalPrizes[reached] : GoalPrizes[2]);
        }

        sealed class GaugeWidth : MonoBehaviour { public float Width; public Rect Uv; }

        /// <summary>GETPRIZE: spend the card; the prizes come back and the scratching starts.</summary>
        public static void Scratch()
        {
            if (!view || scratching || prizes != null) return;
            view.SetEnabled("ID_LOTTO_GETPRIZE", false);
            view.SetEnabled("ID_LOTTO_CANCEL", false);
            string c = card;
            EventNet.Request("lotto.scratch", new Dictionary<string, object> { ["item"] = c }, () => ScratchOffline(P, c, EventNet.Rng), r =>
            {
                if (!r.Ok) { UiPopup.Message(r.Error ?? "That didn't work."); if (view) view.SetEnabled("ID_LOTTO_CANCEL", true); onDone?.Invoke(false); onDone = null; return; }
                prizes = r.Grants.Take(3).ToList();
                while (prizes.Count < 3) prizes.Add(new RewardGrant { Kind = GrantKind.Bust, ItemId = "lotto_00001" });
                scratching = true;
                if (view) { view.GetComponent<EventAnim>().Restart(); ShowGauge(); }
                onDone?.Invoke(true); onDone = null;
            });
        }

        /// <summary>Offline rule (server: src/events.js scratch): spend the card, draw its pool, count it on today's gauge.</summary>
        public static EventReply ScratchOffline(PlayerProfile p, string cardId, System.Random rng)
        {
            if (EventItems.Type(cardId) != "lotto") return EventReply.Fail("That isn't a scratch card.");
            var pool = EventItems.Pool(cardId);
            if (!EventItems.Pools.Has(pool)) return EventReply.Fail("This card can't be scratched.");
            if (!EventNet.Take(p, cardId)) return EventReply.Fail("You don't have that card.");
            var grants = EventItems.Pools.Draw(pool, p.Level, rng);
            EventNet.Apply(p, grants);
            var st = EventNet.State(p);
            if (st.LottoDay != EventNet.Today) { st.LottoDay = EventNet.Today; st.LottoCards = 0; }
            st.LottoCards++;
            return new EventReply { Ok = true, Grants = grants };
        }

        static void Tick(float t)
        {
            if (!view || !scratching || prizes == null) return;
            int col = Mathf.Clamp((int)(t / 2f), 0, 3);
            for (int c = 1; c <= 3; c++)
            {
                int frames = c == 3 ? 5 : 6;
                if (c - 1 < col) { Reveal(c); continue; }
                if (c - 1 == col)
                {
                    int pick = UnityEngine.Random.Range(1, frames + 1);
                    for (int f = 1; f <= 6; f++)
                    {
                        string id = $"ID_LOTTO_PRIZESCRATCH_{c}{f}";
                        view.SetVisible(id, f == pick);
                        if (f == pick) EventUi.Alpha(view, id, UnityEngine.Random.Range(0.4f, 1f));
                    }
                    view.SetText("ID_LOTTO_PRIZETEXT_" + c, EventUi.T("10979", "Scratching the card to see the winning item."));
                }
            }
            if (t >= 6f)
            {
                scratching = false;
                for (int c = 1; c <= 3; c++) Reveal(c);
                view.SetEnabled("ID_LOTTO_CANCEL", true);
                EventUi.Sfx("SID_ITEM_DROP_GET2");
                ChatBox.System(EventUi.T("10977", "Any winning item will be saved in your [F3] inventory."));
                EventNet.Log(prizes);
            }
        }

        static void Reveal(int c)
        {
            if (!view || prizes == null || c > prizes.Count) return;
            var g = prizes[c - 1];
            for (int f = 1; f <= 6; f++) view.SetVisible($"ID_LOTTO_PRIZESCRATCH_{c}{f}", false);
            string anchor = $"ID_LOTTO_PRIZESCRATCH_{c}1";
            view.SetVisible(anchor, true);
            EventUi.Alpha(view, anchor, 1f);
            EventUi.Icon(view, anchor, EventItems.Icon(g));
            view.SetText("ID_LOTTO_PRIZETEXT_" + c, EventItems.Describe(g));
        }

        public static void Close()
        {
            if (view && UiManager.Instance) UiManager.Instance.Close(view);
            view = null; scratching = false;
            if (onDone != null) { var d = onDone; onDone = null; d(false); }
        }
    }
}
