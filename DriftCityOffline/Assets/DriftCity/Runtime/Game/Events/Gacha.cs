using System;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Core;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.Game
{
    /// <summary>
    /// The "Gotcha!" capsule machines (DriftCity_RE\EVENTS.md):
    ///  - GACHA shop 4560000 + prize popup 4620000 (object ui+0x33c, vtable 0x873054; buy 0x639c80 sends op 0xdac
    ///    {isCash, price, category, coins}; the won item comes back as op 0x89 sub 23/24 -> popup SetResult 0x63c670 and the
    ///    popup opens when the 3.3 s capsule animation (ANISTART, 30 fps flip-book of the gacha_00NN frames) ends);
    ///  - the slot machine GACHA 4100000 + GACHARESULT 4110000 for the "Slot Ticket Grade 1-10" items (object ui+0x3f0,
    ///    creator 0x68fb70, handler 0x68f570, Start 0x68fff0: lever keys, three reels of 20+r random symbols easing to the
    ///    result, the 0.06 s win flash; GIFTBUTTON sends the use-item packet 0x1a1; op 0x83a carries the three symbols, Mito
    ///    and EXP; op 0x89 sub 10 shows the result popup).
    /// Remake rules (the official server decided them): capsules cost Mito only. The Mito tab uses globalcontent
    /// Gacha_Price_CarMito (20,000); the Coin tab's G-Coin price (Gacha_Price_CarCash 100) is charged as 1,000 Mito per
    /// coin. No real money exists anywhere. PartsGotchaClose = 1 leaves the two car modes, as in the client. The season
    /// (the server's Etc.GotchaSeason bits) follows the month: <see cref="Season"/>. Pools: rw_gacha_car_{cash|mito}_NN.
    /// </summary>
    public static class Gacha
    {
        static PlayerProfile P => PlayerProfile.Current;
        public const int ShopDialog = 4560000, PopupDialog = 4620000, SlotDialog = 4100000, ResultDialog = 4110000;
        public const long MitoPerCoin = 1000;
        const int PerPage = 7;

        // ---------------- rules shared with the server (src/events.js) ----------------

        /// <summary>The capsule season 1..11 (rw_gacha_car_*_01 .. _11): one per month, cycling.</summary>
        public static int Season => (DateTime.UtcNow.Year * 12 + DateTime.UtcNow.Month - 1) % 11 + 1;

        /// <summary>mode: 0 Car/Coin, 1 Car/Mito, 2 Parts/Coin, 3 Parts/Mito (the client's +0xa4).</summary>
        public static string PoolFor(int mode)
        {
            bool cash = mode == 0 || mode == 2;
            string id = mode < 2 ? $"rw_gacha_car_{(cash ? "cash" : "mito")}_{Season:00}" : $"rw_gacha_parts_{(cash ? "cash" : "mito")}_99";
            if (!EventItems.Pools.Has(id)) id = $"rw_gacha_item_{(cash ? "cash" : "mito")}_99";
            return id;
        }

        public static long Price(int mode)
        {
            bool cash = mode == 0 || mode == 2;
            string key = (mode < 2 ? "Gacha_Price_Car" : "Gacha_Price_Parts") + (cash ? "Cash" : "Mito");
            int v = Pvp.Config(key, cash ? 100 : 20000);
            return cash ? v * MitoPerCoin : v;
        }

        public static int Modes => Pvp.Config("PartsGotchaClose", 1) == 1 ? 2 : 4;

        /// <summary>Offline twin of the server's gacha.buy: pay, draw one item from the mode's pool.</summary>
        public static EventReply BuyOffline(PlayerProfile p, int mode, System.Random rng)
        {
            if (mode < 0 || mode >= Modes) return EventReply.Fail("That capsule machine is closed.");
            long price = Price(mode);
            if (p.Mito < price) return EventReply.Fail(EventUi.T("10293", "You do not have enough Mito for this transaction."));
            var pool = PoolFor(mode);
            if (!EventItems.Pools.Has(pool)) return EventReply.Fail("The capsule machine is empty.");
            p.Mito -= price;
            var grants = EventItems.Pools.Draw(pool, p.Level, rng).Take(1).ToList();
            grants.AddRange(Bonus(rng));
            EventNet.Apply(p, grants);
            return new EventReply { Ok = true, Grants = grants };
        }

        /// <summary>
        /// Remake rule (src/events.js gachaBonus): a capsule sometimes adds one of the season car's Vehicle Parts A-D (1 in 8)
        /// and rarely its Key Parts V1-V4 (1 in 40), the pieces GACHAINVEN counts.
        /// </summary>
        public static List<RewardGrant> Bonus(System.Random rng)
        {
            var g = new List<RewardGrant>();
            string car = GachaInven.CarFor(Season);
            if (rng.NextDouble() < 1.0 / 8) g.Add(new RewardGrant { Kind = GrantKind.Item, ItemId = GachaInven.PartsFor(car)[rng.Next(4)], Count = 1 });
            if (rng.NextDouble() < 1.0 / 40)
            {
                int grade = 1 + rng.Next(4);
                string suffix = car switch { "shadow" => "05", "hunter" => "06", "fate" => "07", "monza" => "08", "winner" => "09", _ => "" };
                string key = $"i_n_{car}_k_v{grade}_{suffix}";
                g.Add(new RewardGrant { Kind = GrantKind.Item, ItemId = EventItems.Row(key) != null ? key : $"i_n_{car}_k_v{grade}", Count = 1 });
            }
            return g;
        }

        /// <summary>The extras of the last capsule (parts / key parts beside the prize).</summary>
        public static IReadOnlyList<RewardGrant> LastExtras => extras;
        static List<RewardGrant> extras = new List<RewardGrant>();

        /// <summary>Everything a pool can give, flattened like the client's list builder 0x63b9b0 (sub-pools expanded).</summary>
        public static List<RewardRow> PoolItems(string pool)
        {
            var list = new List<RewardRow>();
            void Walk(string id, int depth)
            {
                if (depth > 6) return;
                foreach (var r in EventItems.Pools.Rows(id))
                {
                    if (r.Type.Equals("RewardGroup", StringComparison.OrdinalIgnoreCase)) Walk(r.ItemId, depth + 1);
                    else if (r.Probability > 0 && (r.Type == "NormalItem" || r.Type == "SkidItem") && !list.Exists(x => x.ItemId == r.ItemId)) list.Add(r);
                }
            }
            Walk(pool, 0);
            return list;
        }

        // ---------------- GACHA shop 4560000 ----------------

        static NuiView shop, popup;
        static int mode = 1, page;
        static List<RewardRow> shown = new List<RewardRow>();
        static float aniStart = -1f;
        static List<RewardGrant> won;
        static bool buying;

        public static NuiView ShopView => shop;
        public static NuiView PopupView => popup;
        public static bool Busy => buying || aniStart >= 0;
        public static IReadOnlyList<RewardGrant> LastWon => won;
        public static int Mode => mode;

        /// <summary>Opens the capsule shop (HUD ADV_GOTCHA button, NPC, menu).</summary>
        public static void Open()
        {
            var ui = UiManager.Instance;
            if (!ui || P == null) return;
            CloseShop();
            shop = ui.Open(ShopDialog);
            if (!shop) return;
            mode = 1; page = 0; aniStart = -1f; buying = false;
            shop.gameObject.AddComponent<EventAnim>().Tick = ShopTick;
            // the K=0 art (animation frames, alternative layouts) starts hidden; the client reveals by code
            foreach (var c in shop.Controls)
            {
                string n = c.Data.Name ?? "";
                if (n.StartsWith("gacha_0") || NuiControl.Int(c.Data.Nation, "K", 1) == 0) c.gameObject.SetActive(false);
            }
            foreach (Transform t in Frames()) t.gameObject.SetActive(false);
            foreach (var id in new[] { "ID_GACHA_SHOP_GACHA_BTN", "ID_GACHA_SHOP_GACHA_BTNMITO", "ID_GACHA_SHOP_COIN_BTN", "ID_GACHA_SHOP_MITO_BTN", "ID_GACHA_SHOP_HANCOIN" })
                shop.SetVisible(id, false);
            shop.SetVisible("ID_GACHA_SHOP_ANISTART", true);
            shop.SetVisible("ID_GACHA_SHOP_MITO", true);
            shop.SetVisible("ID_GACHA_SHOP_GACHA_COST", true);
            foreach (var id in new[] { "ID_GACHA_SHOP_GACHA_COST", "ID_GACHA_SHOP_MITO" }) { var c = shop.Control(id); if (c) { c.transform.SetParent(shop.transform, true); c.transform.SetAsLastSibling(); } }   // above the F-anchored bottom bar art
            shop.SetVisible("ID_GACHA_SHOP_SELTYPE", true);
            shop.SetVisible("ID_GACHA_SHOP_TYPECOUNT", true);
            shop.SetVisible("ID_GACHA_SHOP_CLOSEPATRS", false);
            shop.SetText("ID_GACHA_SHOP_SELTYPE", EventUi.T("11602", "Gotcha Select"));
            ui.On("ID_GACHA_SHOP_EXIT", _ => { EventUi.Sfx("SID_SYSWIN_DESTROY"); CloseShop(); });
            ui.On("ID_GACHA_SHOP_LEFT_BTN", _ => Page(-1));
            ui.On("ID_GACHA_SHOP_RIGHT_BTN", _ => Page(+1));
            ui.On("ID_GACHA_SHOP_TYPESEL_LEFT", _ => SetMode((mode + Modes - 1) % Modes));
            ui.On("ID_GACHA_SHOP_TYPESEL_RIGHT", _ => SetMode((mode + 1) % Modes));
            ui.On("ID_GACHA_SHOP_TAB", _ => SetMode((mode + 1) % Modes));
            foreach (var (id, m) in new[] { ("ID_GACHA_SHOP_CARCOIN", 0), ("ID_GACHA_SHOP_CARMITO", 1), ("ID_GACHA_SHOP_PARTSCOIN", 2), ("ID_GACHA_SHOP_PARTSMITO", 3) })
            {
                int mm = m;
                ui.On(id, _ => { SetMode(mm); Buy(); });
            }
            ui.On("ID_GACHA_SHOP_GACHAINVENNEW", _ => GachaInven.Toggle());   // GACHAINVEN 4570000-4610000 (GachaInven.cs)
            SetMode(1);
            EventUi.Sfx("SID_SYSWIN_CREATE");
        }

        static IEnumerable<Transform> Frames()
        {
            if (!shop) yield break;
            foreach (var t in shop.GetComponentsInChildren<Transform>(true))
                if (t.name.StartsWith("gacha_0") && t.name.Length >= 10) yield return t;
        }

        public static void SetMode(int m)
        {
            if (!shop || Busy) return;
            mode = Mathf.Clamp(m, 0, Modes - 1);
            page = 0;
            string[] sel = { "ID_GACHA_SHOP_CARCOIN", "ID_GACHA_SHOP_CARMITO", "ID_GACHA_SHOP_PARTSCOIN", "ID_GACHA_SHOP_PARTSMITO" };
            string[] title = { "ID_GACHA_SHOP_TITLE_CARCOIN", "ID_GACHA_SHOP_TITLE_CARMITO", "ID_GACHA_SHOP_TITLE_PARTSCOIN", "ID_GACHA_SHOP_TITLE_PARTSMITO" };
            for (int i = 0; i < 4; i++) { shop.SetVisible(sel[i], i == mode); shop.SetVisible(title[i], i == mode); }
            shop.SetVisible("ID_GACHA_SHOP_TAB_COIN", mode == 0 || mode == 2);
            shop.SetVisible("ID_GACHA_SHOP_TAB_MITO", mode == 1 || mode == 3);
            shown = PoolItems(PoolFor(mode));
            shop.SetText("ID_GACHA_SHOP_TYPECOUNT", $"({mode + 1:00} / {Modes:00})");
            Refresh();
        }

        static void Page(int d)
        {
            if (!shop) return;
            int pages = Mathf.Max(1, (shown.Count + PerPage - 1) / PerPage);
            page = (page + d + pages) % pages;
            EventUi.Sfx("SID_ITEM_CLICK");
            Refresh();
        }

        static void Refresh()
        {
            if (!shop) return;
            int pages = Mathf.Max(1, (shown.Count + PerPage - 1) / PerPage);
            for (int i = 0; i < PerPage; i++)
            {
                int k = page * PerPage + i;
                var r = k < shown.Count ? shown[k] : null;
                shop.SetText($"ID_GACHA_SHOP_ITEM_NAME{i:00}", r != null ? EventItems.Name(r.ItemId) : "");
                shop.SetText($"ID_GACHA_SHOP_TAB_OWNCOUNT{i}", r == null ? "" : r.Type == "NormalItem" ? $"( {P.Count(r.ItemId)} )" : "( - )");
                EventUi.Icon(shop, $"ID_GACHA_SHOP_ITEM_IMAGE{i:00}", r?.ItemId, true);
            }
            shop.SetText("ID_GACHA_SHOP_THISPAGE", (page + 1).ToString("00"));
            shop.SetText("ID_GACHA_SHOP_TOTALPAGE", pages.ToString("00"));
            shop.SetText("ID_GACHA_SHOP_MITO", P.Mito.ToString("N0"));
            bool cash = mode == 0 || mode == 2;
            shop.SetText("ID_GACHA_SHOP_GACHA_COST", cash ? $"{Price(mode) / MitoPerCoin:N0} {EventUi.T("11702", "Coin")} = {Price(mode):N0} Mito" : $"{Price(mode):N0} {EventUi.T("10511", "Mito")}");
        }

        /// <summary>Buys one capsule of the current mode: the draw happens first, the prize shows when the animation ends.</summary>
        public static void Buy()
        {
            if (!shop || Busy || P == null) return;
            if (P.Mito < Price(mode)) { UiPopup.Message(EventUi.T("10293", "You do not have enough Mito for this transaction.")); return; }
            buying = true;
            ClosePopup();
            EventUi.Sfx("SID_ITEM_BUY");
            int m = mode;
            EventNet.Request("gacha.buy", new Dictionary<string, object> { ["mode"] = m }, () => BuyOffline(P, m, EventNet.Rng), r =>
            {
                buying = false;
                if (!r.Ok) { UiPopup.Message(r.Error ?? "That didn't work."); return; }
                won = r.Grants.Take(1).ToList();
                extras = r.Grants.Skip(1).ToList();
                aniStart = 0f;
                if (shop) { shop.GetComponent<EventAnim>().Restart(); Refresh(); }
            });
        }

        static void ShopTick(float t)
        {
            if (!shop) return;
            if (aniStart < 0) return;
            const float AnimSeconds = 3.3f;
            int frame = Mathf.FloorToInt(t * 30f);
            shop.SetVisible("ID_GACHA_SHOP_ANISTART", false);
            foreach (var f in Frames())
                if (int.TryParse(f.name.Substring(6, 4), out int k)) f.gameObject.SetActive(k <= frame);
            if (t < AnimSeconds) return;
            aniStart = -1f;
            foreach (var f in Frames()) f.gameObject.SetActive(false);
            shop.SetVisible("ID_GACHA_SHOP_ANISTART", true);
            ShowPopup(won);
            EventNet.Log(won);
            EventNet.Log(extras);
            Refresh();
        }

        static void ShowPopup(List<RewardGrant> grants)
        {
            var ui = UiManager.Instance;
            if (!ui || grants == null) return;
            ClosePopup();
            popup = ui.Open(PopupDialog);
            if (!popup) return;
            var g = grants.FirstOrDefault();
            foreach (var c in popup.Controls) c.gameObject.SetActive(true);
            popup.SetText("ID_GACHA_SHOP_POPUP_ITEMNAME", g != null ? EventItems.Describe(g) : "");
            EventUi.Icon(popup, "ID_GACHA_SHOP_POPUP_ITEMPOS", g != null ? EventItems.Icon(g) : null);
            ui.On("ID_GACHA_SHOP_POPUP_OK", _ => ClosePopup());
            ui.On("ID_GACHA_SHOP_POPUP_EXIT", _ => ClosePopup());
            EventUi.Sfx("SID_ITEM_DROP_GET2");
        }

        /// <summary>GACHAINVEN (4570000-4610000) isn't rebuilt: a list of the capsule parts and keys the driver holds.</summary>
        public static void ClosePopup() { if (popup && UiManager.Instance) UiManager.Instance.Close(popup); popup = null; }

        public static void CloseShop()
        {
            ClosePopup();
            GachaInven.Close();
            if (shop && UiManager.Instance) UiManager.Instance.Close(shop);
            shop = null; aniStart = -1f;
        }

        // ---------------- slot machine 4100000 / 4110000 ----------------

        static NuiView slot, result;
        static string ticket;
        static Action<bool> slotDone;
        static int[] symbols = { 0, 0, 0 };
        static int slotExp, slotMito;
        static List<RewardGrant> slotWon;
        static bool spinning, spinPending;
        static float spinEnd;
        static readonly string[] SymbolNames = { "BOMB", "GOLD", "SILVER", "BRONZE" };
        static readonly RawImage[,] reelImg = new RawImage[3, 2];
        static readonly Vector2[] reelPos = new Vector2[3];
        static readonly List<(float start, float dur, int sym)>[] strips = { new List<(float, float, int)>(), new List<(float, float, int)>(), new List<(float, float, int)>() };

        public static NuiView SlotView => slot;
        public static NuiView ResultView => result;
        public static bool Spinning => spinning || spinPending;
        public static IReadOnlyList<int> Symbols => symbols;
        public static IReadOnlyList<RewardGrant> SlotWon => slotWon;

        /// <summary>The slot ticket grade 1..10 of a gacha_observe_vN item (0 for anything else).</summary>
        public static int TicketGrade(string item) =>
            item != null && item.StartsWith("gacha_observe_v", StringComparison.OrdinalIgnoreCase) && int.TryParse(item.Substring(15), out int n) && n >= 1 && n <= 10 ? n : 0;

        /// <summary>
        /// Offline twin of the server's gacha.slot: spend the ticket, three random symbols (0 bomb, 1 gold, 2 silver, 3 bronze),
        /// a prize from rw_gacha_ob_vN, EXP 100 x grade, and a Mito bonus for three alike (gold 10,000 / silver 5,000 /
        /// bronze 2,000 x grade).
        /// </summary>
        public static EventReply SpinOffline(PlayerProfile p, string item, System.Random rng)
        {
            int grade = TicketGrade(item);
            if (grade == 0) return EventReply.Fail("That isn't a slot ticket.");
            if (!EventNet.Take(p, item)) return EventReply.Fail("You don't have that ticket.");
            var sym = new[] { rng.Next(4), rng.Next(4), rng.Next(4) };
            var grants = EventItems.Pools.Draw("rw_gacha_ob_v" + grade, p.Level, rng).Take(1).ToList();
            int exp = 100 * grade, mito = SlotMito(sym, grade);
            grants.Add(new RewardGrant { Kind = GrantKind.Exp, Count = exp });
            if (mito > 0) grants.Add(new RewardGrant { Kind = GrantKind.Mito, Count = mito });
            EventNet.Apply(p, grants);
            var r = new EventReply { Ok = true, Grants = grants };
            r.Data["symbols"] = sym.Select(x => (object)(double)x).ToList();
            r.Data["exp"] = (double)exp; r.Data["mito"] = (double)mito;
            return r;
        }

        public static int SlotMito(int[] sym, int grade) => sym[0] == sym[1] && sym[1] == sym[2] && sym[0] != 0 ? new[] { 0, 10000, 5000, 2000 }[sym[0]] * grade : 0;

        public static void OpenSlot(string item, Action<bool> done)
        {
            var ui = UiManager.Instance;
            if (!ui || P == null || TicketGrade(item) == 0) { done?.Invoke(false); return; }
            CloseSlot();
            slot = ui.Open(SlotDialog);
            if (!slot) { done?.Invoke(false); return; }
            ticket = item; slotDone = done; slotWon = null; spinning = spinPending = false;
            FixBackground();
            BuildReels();
            slot.gameObject.AddComponent<EventAnim>().Tick = SlotTick;
            for (int i = 1; i <= 10; i++) slot.SetVisible($"ID_GACHA_ITEMRANK{i:00}", i == TicketGrade(item));
            foreach (var s in SymbolNames) for (int f = 1; f <= 5; f++) slot.SetVisible($"ID_GACHA_{s}_FRAME{f}", false);
            for (int i = 1; i <= 4; i++) slot.SetVisible("ID_GACHA_STICK_FRAME" + i, i == 1);
            for (int i = 1; i <= 5; i++) slot.SetVisible("ID_GACHA_LIGHT" + i, false);
            symbols = new[] { 0, 0, 0 };
            slotExp = slotMito = 0;
            StartReels(true);
            ShowAmounts();
            slot.SetVisible("ID_GACHA_GIFTBUTTON", true);
            ui.On("ID_GACHA_GIFTBUTTON", _ => Spin());
            ui.On("ID_GACHA_CLOSE", _ => CloseSlot());
            ui.On("ID_GACHARESULT_CLOSE", _ => CloseSlot());
            EventUi.Sfx("SID_SYSWIN_CREATE");
        }

        /// <summary>BG carries NATION F=18 (screen centre); in the client it sits with the other controls at H (275, 47).</summary>
        static void FixBackground() => Reanchor(slot, "ID_GACHA_BG", "ID_GACHA_LIGHT1", new Vector2(275, 47));

        /// <summary>Moves a control with NATION F (its own screen anchor) back beside a sibling, at its H position (dialog pixels).</summary>
        static void Reanchor(NuiView v, string id, string sibling, Vector2 h)
        {
            var c = v ? v.Control(id) : null;
            var any = v ? v.Control(sibling) : null;
            if (!c || !any) return;
            var rt = (RectTransform)c.transform;
            rt.SetParent(any.transform.parent, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(h.x, -h.y);
            rt.SetAsFirstSibling();
        }

        /// <summary>Each reel: a clipped window over the GOLD1 / SILVER1 / BRONZE1 placement with two symbol images.</summary>
        static void BuildReels()
        {
            string[] anchors = { "ID_GACHA_GOLD_FRAME1", "ID_GACHA_SILVER_FRAME1", "ID_GACHA_BRONZE_FRAME1" };
            for (int r = 0; r < 3; r++)
            {
                var a = slot.Control(anchors[r]);
                if (!a) continue;
                var art = (RectTransform)a.transform;
                reelPos[r] = new Vector2(art.anchoredPosition.x, art.anchoredPosition.y);
                var win = NuiView.NewRect("reel " + r, art.parent);
                win.anchoredPosition = reelPos[r] + new Vector2(0, -5);
                win.sizeDelta = new Vector2(120, 157);
                win.gameObject.AddComponent<RectMask2D>();
                for (int k = 0; k < 2; k++)
                {
                    var img = NuiView.NewRect("symbol " + k, win);
                    img.sizeDelta = new Vector2(120, 167);
                    var ri = img.gameObject.AddComponent<RawImage>();
                    ri.raycastTarget = false;
                    reelImg[r, k] = ri;
                }
            }
        }

        static void SetSymbol(RawImage ri, int sym, int frame, float yOffset)
        {
            if (!ri) return;
            var src = MissionUi.Image(slot, $"ID_GACHA_{SymbolNames[sym & 3]}_FRAME{Mathf.Clamp(frame, 1, 5)}");
            ri.enabled = src && src.texture;
            if (!ri.enabled) return;
            ri.texture = src.texture; ri.uvRect = src.uvRect;
            ri.rectTransform.anchoredPosition = new Vector2(0, 5 - yOffset);
        }

        /// <summary>The reel strips of Start 0x68fff0: element i lasts d_i = max(0.1, 0.3(1 - sin(i pi / n))), crossing the
        /// window from -120 to +120 px over 2 d_i; the next starts when it reaches the centre; the last stops there.</summary>
        static void StartReels(bool still)
        {
            var rng = new System.Random();
            for (int r = 0; r < 3; r++)
            {
                var s = strips[r];
                s.Clear();
                int n = 20 + r;
                float t0 = 1.1f;
                for (int i = 0; i < n; i++)
                {
                    int sym = i == n - 1 ? symbols[r] : rng.Next(4);
                    float d = i == 0 ? 0.1f : Mathf.Max(0.1f, 0.3f * (1f - Mathf.Sin(i * Mathf.PI / n)));
                    s.Add((t0, d, sym));
                    t0 += d;
                }
                if (still) { s.Clear(); s.Add((0f, 0f, symbols[r])); }
            }
            spinEnd = still ? 0f : strips[2][strips[2].Count - 1].start + strips[2][strips[2].Count - 1].dur + 0.2f + 4 * 0.06f;
        }

        /// <summary>GIFTBUTTON: spend the ticket; the reels run on the server's symbols.</summary>
        public static void Spin()
        {
            if (!slot || Spinning) return;
            spinPending = true;
            slot.SetVisible("ID_GACHA_GIFTBUTTON", false);
            CloseResult();
            string item = ticket;
            EventNet.Request("gacha.slot", new Dictionary<string, object> { ["item"] = item }, () => SpinOffline(P, item, EventNet.Rng), r =>
            {
                spinPending = false;
                if (!r.Ok) { UiPopup.Message(r.Error ?? "That didn't work."); if (slot) slot.SetVisible("ID_GACHA_GIFTBUTTON", P.Count(ticket) > 0); return; }
                var sym = r.Data.L("symbols");
                for (int i = 0; i < 3; i++) symbols[i] = sym != null && i < sym.Count ? Convert.ToInt32(sym[i]) & 3 : 0;
                slotExp = r.Data.I("exp"); slotMito = r.Data.I("mito");
                slotWon = r.Grants;
                spinning = true;
                StartReels(false);
                if (slot) slot.GetComponent<EventAnim>().Restart();
                slotDone?.Invoke(true); slotDone = null;
            });
        }

        static void ShowAmounts()
        {
            if (!slot) return;
            slot.SetText("ID_GACHA_EXP", "+" + slotExp.ToString("000"));
            slot.SetText("ID_GACHA_MITO", slotMito.ToString("N0"));
        }

        static void SlotTick(float t)
        {
            if (!slot) return;
            if (!spinning)
            {
                for (int r = 0; r < 3; r++) { SetSymbol(reelImg[r, 0], symbols[r], 1, 0); if (reelImg[r, 1]) reelImg[r, 1].enabled = false; }
                return;
            }
            // lever: STICK1 0-0.5, 2 -0.55, 3 -0.65, 4 -0.95, 3 -1.05, 2 -1.1, then 1
            int stick = t < 0.5f ? 1 : t < 0.55f ? 2 : t < 0.65f ? 3 : t < 0.95f ? 4 : t < 1.05f ? 3 : t < 1.1f ? 2 : 1;
            for (int i = 1; i <= 4; i++) slot.SetVisible("ID_GACHA_STICK_FRAME" + i, i == stick);
            // lights flicker during the first half of every second
            bool flick = t % 1f < 0.5f && Mathf.FloorToInt(t * 10) % 2 == 0;
            for (int i = 1; i <= 5; i++) slot.SetVisible("ID_GACHA_LIGHT" + i, flick);
            float lastStop = strips[2][strips[2].Count - 1].start + strips[2][strips[2].Count - 1].dur;
            for (int r = 0; r < 3; r++)
            {
                var s = strips[r];
                var last = s[s.Count - 1];
                float stop = last.start + last.dur;
                if (t >= stop)
                {
                    // win flash: frames 2..5 at 0.06 s once the last reel stopped + 0.2 s, then frame 1
                    float f = t - (lastStop + 0.2f);
                    int frame = f >= 0 && f < 0.24f ? 2 + Mathf.FloorToInt(f / 0.06f) : 1;
                    SetSymbol(reelImg[r, 0], last.sym, frame, 0);
                    if (reelImg[r, 1]) reelImg[r, 1].enabled = false;
                    continue;
                }
                if (t < s[0].start) { SetSymbol(reelImg[r, 0], s[0].sym, 1, 0); if (reelImg[r, 1]) reelImg[r, 1].enabled = false; continue; }
                int cur = s.FindLastIndex(e => e.start <= t);
                var e0 = s[cur];
                float y0 = -120f + 240f * (t - e0.start) / (2f * e0.dur);
                SetSymbol(reelImg[r, 0], e0.sym, 1, y0);
                if (cur > 0)
                {
                    var e1 = s[cur - 1];
                    float y1 = -120f + 240f * (t - e1.start) / (2f * e1.dur);
                    if (y1 < 167f) SetSymbol(reelImg[r, 1], e1.sym, 1, y1); else if (reelImg[r, 1]) reelImg[r, 1].enabled = false;
                }
            }
            if (t >= spinEnd)
            {
                spinning = false;
                for (int i = 1; i <= 5; i++) slot.SetVisible("ID_GACHA_LIGHT" + i, false);
                ShowAmounts();
                ShowResult(slotWon?.FirstOrDefault(g => g.Kind == GrantKind.Item));
                EventNet.Log(slotWon ?? new List<RewardGrant>());
                slot.SetVisible("ID_GACHA_GIFTBUTTON", P.Count(ticket) > 0);
            }
        }

        static void ShowResult(RewardGrant g)
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            CloseResult();
            result = ui.Open(ResultDialog);
            if (!result) return;
            Reanchor(result, "ID_GACHARESULT_BG", "ID_GACHARESULT_ITEMPOS", new Vector2(305, 523));
            result.SetText("ID_GACHARESULT_ITEMNAME", g != null ? EventItems.Describe(g) : EventItems.Describe(new RewardGrant { Kind = GrantKind.Bust }));
            EventUi.Icon(result, "ID_GACHARESULT_ITEMPOS", g != null ? EventItems.Icon(g) : "lotto_00001");
            EventUi.Sfx("SID_ITEM_DROP_GET2");
        }

        static void CloseResult() { if (result && UiManager.Instance) UiManager.Instance.Close(result); result = null; }

        public static void CloseSlot()
        {
            CloseResult();
            if (slot && UiManager.Instance) UiManager.Instance.Close(slot);
            slot = null; spinning = spinPending = false;
            if (slotDone != null) { var d = slotDone; slotDone = null; d(false); }
        }
    }
}
