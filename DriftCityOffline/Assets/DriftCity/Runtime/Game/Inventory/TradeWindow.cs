using System.Collections.Generic;
using System.Linq;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.Game
{
    /// <summary>
    /// Player-to-player trade on the client's TRADE window (1190000; handler 0x62e710): my 8 slots and Mito (MY_MONEY_1
    /// opens the amount box 3240000, "Please enter the amount to exchange." 11042), the other driver's 8 slots and Mito,
    /// Lock (MY_LOCK, 0x78dbe0) and Trade (MY_TRADE, 0x78de90; refused with "Inventory is full" 10269 when the incoming
    /// items don't fit) and Close (0x78dd40 cancel); the right side is my bag (INVEN_SLOT1..20, sort, pages). The request
    /// pops "%s has requested a deal. Accept?" (10726) on the other driver. Online only: the server (src/trade.js) holds
    /// the offers and swaps atomically once both drivers locked and pressed Trade; offline the window isn't available.
    /// </summary>
    public static class TradeWindow
    {
        static PlayerProfile P => PlayerProfile.Current;
        static UiManager Ui => UiManager.Instance;
        const int MySlots = 8, BagSlots = 20;

        static NuiView view, invite;
        static int tradeId, bagPage, sort;
        static string partner;
        static readonly List<(string key, int count)> offer = new List<(string, int)>();
        static long offerMito;
        static Dictionary<string, object> state;
        static List<BagEntry> bag = new List<BagEntry>();

        public static bool IsOpen => view;
        public static NuiView View => view;
        public static string Partner => partner;
        public static int TradeId => tradeId;
        /// <summary>The last server state (me / them: items, mito, locked, confirmed).</summary>
        public static Dictionary<string, object> State => state;
        /// <summary>Set when a trade finished or was closed, with its reason ("done", "cancelled", "declined", "left", "failed").</summary>
        public static string LastResult { get; private set; }

        /// <summary>Asks another driver in the channel to trade (chat /trade name, the COMMAND menu's Trade).</summary>
        public static void Request(string name)
        {
            if (!OnlineSession.Active)
            {
                UiPopup.Message("Trading with other drivers needs the online server. It isn't available offline.");
                return;
            }
            if (string.IsNullOrEmpty(name)) return;
            OnlineSession.Instance.Net.Request("trade.request", new Dictionary<string, object> { ["name"] = name }, m =>
            {
                if (!m.B("ok")) { UiPopup.Message(m.S("error") ?? ItemCatalog.Text("20046", "Failed to create a trade.")); return; }
                ChatBox.System($"You asked {name} to trade.");
            });
        }

        /// <summary>Server pushes (subscribed by the quick-slot driver to NetClient.Message).</summary>
        public static void OnMessage(string type, Dictionary<string, object> m)
        {
            if (type == null || !type.StartsWith("trade.")) return;
            switch (type)
            {
                case "trade.invite": Invited(m.I("id"), m.S("from")); break;
                case "trade.open": Open(m.I("id"), m.S("with")); break;
                case "trade.state": if (m.I("id") == tradeId) { state = m; Refresh(); } break;
                case "trade.closed": Closed(m); break;
            }
        }

        static void Invited(int id, string from)
        {
            if (invite && Ui) Ui.Close(invite);
            invite = UiPopup.Show(ItemCatalog.Text("10726", "%s has requested a deal. Accept?", from), b =>
            {
                invite = null;
                if (!OnlineSession.Active) return;
                OnlineSession.Instance.Net.Request(b == UiPopup.Accept ? "trade.accept" : "trade.decline", new Dictionary<string, object> { ["id"] = id }, r =>
                {
                    if (!r.B("ok") && b == UiPopup.Accept) UiPopup.Message(r.S("error") ?? ItemCatalog.Text("20046", "Failed to create a trade."));
                });
            }, UiPopup.Accept, UiPopup.Deny);
        }

        static void Open(int id, string with)
        {
            tradeId = id;
            partner = with;
            offer.Clear();
            offerMito = 0;
            state = null;
            bagPage = 0;
            LastResult = null;
            if (view && Ui) Ui.Close(view);
            view = Ui.Open(1190000);
            if (!view) return;
            // the window owns the screen like the inventory does: other menus close
            ListWindow.CloseOpen();
            OriginalMenus.Close();
            Ui.On("ID_TRADE_CLOSE", _ => Cancel());
            Ui.On("ID_TRADE_MY_LOCK", _ => Send("trade.lock", new Dictionary<string, object> { ["lock"] = !Me().B("locked") }));
            Ui.On("ID_TRADE_MY_TRADE", _ => Confirm());
            Ui.On("ID_TRADE_MY_MONEY_1", _ => CountPopup.Show(ItemCatalog.Text("11042", "Please enter the amount to exchange."), (int)System.Math.Min(int.MaxValue, P.Mito), (int)System.Math.Max(1, System.Math.Min(int.MaxValue, offerMito)), n => { offerMito = n; SendOffer(); }));
            Ui.On("ID_TRADE_PREVPAGE", _ => { bagPage = Mathf.Max(0, bagPage - 1); Refresh(); });
            Ui.On("ID_TRADE_NEXTPAGE", _ => { bagPage++; Refresh(); });
            Ui.On("ID_TRADE_SORT_DATE", _ => { sort = 0; Refresh(); });
            Ui.On("ID_TRADE_SORT_TYPE", _ => { sort = 1; Refresh(); });
            Ui.On("ID_TRADE_SORT_MITO", _ => { sort = 2; Refresh(); });
            OriginalMenus.Hide(view, "ID_TRADE_MY_SLOT9", "ID_TRADE_OPP_SLOT9");
            foreach (var id_ in new[] { "ID_TRADE_OPP_LOCK", "ID_TRADE_OPP_TRADE" }) view.SetEnabled(id_, false);
            for (int i = 0; i < MySlots; i++)
            {
                int k = i;
                var mine = view.Control("ID_TRADE_MY_SLOT" + (i + 1));
                if (mine)
                {
                    var s = ItemSlot.On((RectTransform)mine.transform);
                    s.Entry = () => k < offer.Count ? WithCount(ItemCatalog.Find(P, offer[k].key), offer[k].count) : null;
                    s.LeftClick = _ => Withdraw(k);
                    s.RightClick = s.LeftClick;
                    s.Dropped = e => Offer(e);
                    s.Accept = e => true;
                }
                var theirs = view.Control("ID_TRADE_OPP_SLOT" + (i + 1));
                if (theirs) ItemSlot.On((RectTransform)theirs.transform).Entry = () => Theirs(k);
            }
            for (int i = 0; i < BagSlots; i++)
            {
                int k = i;
                var slot = view.Control("ID_TRADE_INVEN_SLOT" + (i + 1));
                if (!slot) continue;
                var s = ItemSlot.On((RectTransform)slot.transform);
                s.Entry = () => BagAt(k);
                s.LeftClick = e => Offer(e);
                s.DoubleClick = s.LeftClick;
            }
            view.gameObject.AddComponent<EscKey>();
            AudioLibrary.PlaySfx("sfx/SID_ITEM_CLICK");
            Refresh();
        }

        /// <summary>Esc closes the count box first, else cancels the trade (the same Esc must not open the quit menu).</summary>
        sealed class EscKey : MonoBehaviour
        {
            void Update()
            {
#if ENABLE_INPUT_SYSTEM
                bool esc = UnityEngine.InputSystem.Keyboard.current != null && UnityEngine.InputSystem.Keyboard.current.escapeKey.wasPressedThisFrame;
#else
                bool esc = Input.GetKeyDown(KeyCode.Escape);
#endif
                if (!esc || UiPopup.IsOpen || ChatBox.Typing) return;
                ListWindow.ClosedFrame = Time.frameCount;
                UiSounds.Cancel();
                if (CountPopup.IsOpen) CountPopup.Close();
                else Cancel();
            }
        }

        static BagEntry WithCount(BagEntry e, int count) { if (e != null && e.Kind != BagKind.Part) e.Count = count; return e; }

        static Dictionary<string, object> Me() => state.O("me") ?? new Dictionary<string, object>();
        static Dictionary<string, object> Them() => state.O("them") ?? new Dictionary<string, object>();

        static BagEntry BagAt(int i)
        {
            int k = bagPage * BagSlots + i;
            return k >= 0 && k < bag.Count ? bag[k] : null;
        }

        /// <summary>The other driver's item k as a display entry (their items aren't in my profile).</summary>
        static BagEntry Theirs(int k)
        {
            var list = state != null ? Them().L("items") : null;
            if (list == null || k >= list.Count || !(list[k] is Dictionary<string, object> o)) return null;
            switch (o.S("kind"))
            {
                case "stack": return new BagEntry { Kind = BagKind.Stack, Id = o.S("id"), Count = o.I("count", 1) };
                case "shop": return new BagEntry { Kind = BagKind.Shop, ItemId = o.I("itemId"), Count = o.I("count", 1) };
                case "part": return new BagEntry { Kind = BagKind.Part, Part = new PerfPart { Uid = o.I("uid"), Type = o.I("type"), Grade = o.I("grade"), Enhance = o.I("enhance") } };
            }
            return null;
        }

        /// <summary>Puts an entry on the table (a stack asks how many first: "Enter the quantity you want to trade." 10394).</summary>
        public static void Offer(BagEntry e)
        {
            if (!view || e == null) return;
            if (!ItemUse.Tradeable(e)) { UiPopup.Message(ItemCatalog.Text("10393", "This item cannot be traded.")); return; }
            if (offer.Any(o => o.key == e.Key)) return;
            if (offer.Count >= MySlots) { UiPopup.Message(ItemCatalog.Text("10392", "You cannot trade anymore.")); return; }
            if (e.Kind != BagKind.Part && e.Count > 1)
            {
                CountPopup.Show(ItemCatalog.Text("10394", "Enter the quantity you want to trade."), e.Count, e.Count, n => { offer.Add((e.Key, n)); SendOffer(); });
                return;
            }
            offer.Add((e.Key, 1));
            SendOffer();
        }

        /// <summary>Offers a stack with an exact count (tests / drag with a count already chosen).</summary>
        public static void Offer(BagEntry e, int count)
        {
            if (!view || e == null || offer.Any(o => o.key == e.Key) || offer.Count >= MySlots) return;
            offer.Add((e.Key, Mathf.Clamp(count, 1, e.Count)));
            SendOffer();
        }

        public static void OfferMito(long mito) { offerMito = System.Math.Max(0, System.Math.Min(mito, P != null ? P.Mito : 0)); SendOffer(); }

        static void Withdraw(int k)
        {
            if (k < 0 || k >= offer.Count) return;
            offer.RemoveAt(k);
            SendOffer();
        }

        static void SendOffer()
        {
            var items = new List<object>();
            foreach (var (key, count) in offer)
            {
                var e = ItemCatalog.Find(P, key);
                if (e != null) items.Add(e.Ref(count));
            }
            AudioLibrary.PlaySfx("sfx/SID_ITEM_DROP");
            Send("trade.offer", new Dictionary<string, object> { ["items"] = items, ["mito"] = offerMito });
        }

        public static void Lock(bool on) => Send("trade.lock", new Dictionary<string, object> { ["lock"] = on });

        public static void Confirm()
        {
            // the client checks the free bag space first (0x62e710: incoming count vs free slots, "Inventory is full" 10269)
            int incoming = state != null ? (Them().L("items")?.Count(o => o is Dictionary<string, object> d && d.S("kind") == "part") ?? 0) : 0;
            if (P != null && P.PerfParts.Count + incoming > PerfRules.MaxParts) { UiPopup.Message(ItemCatalog.Text("10269", "Inventory is full")); return; }
            Send("trade.confirm", null);
        }

        public static void Cancel()
        {
            if (OnlineSession.Active) OnlineSession.Instance.Net.Request("trade.cancel", new Dictionary<string, object>(), _ => { });
            CloseView();
        }

        static void Send(string type, Dictionary<string, object> body)
        {
            if (!OnlineSession.Active) return;
            OnlineSession.Instance.Net.Request(type, body ?? new Dictionary<string, object>(), m =>
            {
                if (!m.B("ok")) UiPopup.Message(m.S("error") ?? "The server refused that.");
            });
        }

        static void Closed(Dictionary<string, object> m)
        {
            if (m.I("id") != tradeId && m.S("reason") != "declined") return;
            string reason = m.S("reason");
            LastResult = reason;
            switch (reason)
            {
                case "done": AudioLibrary.PlaySfx("sfx/SID_ITEM_BUY"); ChatBox.System($"Trade with {partner} completed."); break;
                case "declined": ChatBox.System($"{m.S("by") ?? "The other driver"} declined the trade."); break;
                case "failed": UiPopup.Message(m.S("error") ?? ItemCatalog.Text("20055", "Failed to trade items.")); break;
                default: ChatBox.System("The trade was cancelled."); break;
            }
            CloseView();
            InventoryWindow.RefreshIfOpen();
            QuickSlots.Refresh();
        }

        static void CloseView()
        {
            CountPopup.Close();
            ItemTooltip.Hide();
            if (view && Ui) Ui.Close(view);
            view = null;
        }

        static void Refresh()
        {
            if (!view || P == null) return;
            view.SetText("ID_TRADE_MY_NAME", P.Name);
            view.SetText("ID_TRADE_OPP_NAME", partner ?? "");
            view.SetText("ID_TRADE_MYMITO", $"{P.Mito:N0}");
            var me = state != null ? Me() : null;
            var them = state != null ? Them() : null;
            view.SetText("ID_TRADE_MY_MONEY_TEXT_1", $"{(me != null ? (long)me.N("mito") : offerMito):N0}");
            view.SetText("ID_TRADE_MY_MONEY_TEXT_2", $"{(them != null ? (long)them.N("mito") : 0):N0}");
            bool myLock = me != null && me.B("locked"), theirLock = them != null && them.B("locked");
            OriginalMenus.Pressed(view.Control("ID_TRADE_MY_LOCK"), myLock);
            OriginalMenus.Pressed(view.Control("ID_TRADE_OPP_LOCK"), theirLock);
            OriginalMenus.Pressed(view.Control("ID_TRADE_MY_TRADE"), me != null && me.B("confirmed"));
            OriginalMenus.Pressed(view.Control("ID_TRADE_OPP_TRADE"), them != null && them.B("confirmed"));
            view.SetEnabled("ID_TRADE_MY_TRADE", myLock && theirLock && !(me != null && me.B("confirmed")));
            // my offer as the server has it
            if (me?.L("items") is List<object> mine)
            {
                offer.Clear();
                foreach (var o in mine.OfType<Dictionary<string, object>>())
                {
                    string key = o.S("kind") == "stack" ? "s:" + o.S("id") : o.S("kind") == "shop" ? "i:" + o.I("itemId") : "p:" + o.I("uid");
                    offer.Add((key, Mathf.Max(1, o.I("count", 1))));
                }
                offerMito = (long)me.N("mito");
            }
            for (int i = 0; i < MySlots; i++)
            {
                var mineSlot = view.Control("ID_TRADE_MY_SLOT" + (i + 1));
                var e = i < offer.Count ? WithCount(ItemCatalog.Find(P, offer[i].key), offer[i].count) : null;
                if (mineSlot) Tile(mineSlot, e, new Vector2(21, 20));
                Badge("ID_TRADE_MY_SLOT_UPGRADE" + $"{i + 1:00}", e);
                var theirSlot = view.Control("ID_TRADE_OPP_SLOT" + (i + 1));
                var t = Theirs(i);
                if (theirSlot) Tile(theirSlot, t, new Vector2(21, 20));
                Badge("ID_TRADE_OPP_SLOT_UPGRADE" + $"{i + 1:00}", t);
            }
            // my bag, without what is already on the table
            var offered = offer.GroupBy(o => o.key).ToDictionary(g => g.Key, g => g.Sum(o => o.count));
            bag = new List<BagEntry>();
            foreach (var e in ItemCatalog.Sorted(ItemCatalog.Bag(P), sort))
            {
                if (offered.TryGetValue(e.Key, out int n)) { if (e.Kind == BagKind.Part || e.Count - n <= 0) continue; e.Count -= n; }
                bag.Add(e);
            }
            int pages = Mathf.Max(1, (bag.Count + BagSlots - 1) / BagSlots);
            bagPage = Mathf.Clamp(bagPage, 0, pages - 1);
            view.SetText("ID_TRADE_CURRPAGE", (bagPage + 1).ToString());
            view.SetText("ID_TRADE_MAXPAGE", pages.ToString());
            for (int i = 0; i < BagSlots; i++)
            {
                var slot = view.Control("ID_TRADE_INVEN_SLOT" + (i + 1));
                var e = BagAt(i);
                if (slot) Tile(slot, e, new Vector2(11, 10));
                Badge("ID_TRADE_INVEN_SLOT_UPGRADE" + $"{i + 1:00}", e);
            }
            UiPopup.BringToFront();
        }

        static void Tile(NuiControlView slot, BagEntry e, Vector2 at)
        {
            OriginalMenus.Remove(slot.transform, "item");
            if (e == null) return;
            var tile = OriginalMenus.ItemTile(slot.transform, at, new Vector2(46, 46), e.Icon, e.Name, ItemCatalog.Swatch(e), e.Count);
            foreach (var g in tile.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
        }

        static void Badge(string id, BagEntry e)
        {
            bool on = e != null && e.Kind == BagKind.Part && e.Part.Enhance > 0;
            view.SetVisible(id, on);
            if (on) { view.SetText(id, $"+{e.Part.Enhance}"); view.Control(id)?.transform.SetAsLastSibling(); }
        }
    }
}
