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
    /// The harvest and Easter screens (DriftCity_RE\EVENTS.md section 8): AUTUMN 4670000 (Box Recipe), THANKSGIVING 4680000
    /// (distribution centre), VALEN 4660000 (egg baskets), EVENT 4690000 ("+N EXP") and EVENT 4700000 (gifting a basket).
    ///
    /// AUTUMN [exact] (ui+0x17c, vtable 0x874d24, handler 0x692e30, tick 0x692c50): opened by using the Box Recipe; FOOD1-4 and
    /// BOX1/2 counts "%02d/%02d" (11580 brown / 11581 red), needs vegetable 12, fish 6, fruit 4, meat 3 and a Normal or Ice
    /// box; MAKE (0x692810) sends 0x1a1 and turns off; op 0x89 sub 26 b (0/1/2) -> after 1.4 s RESULT_BAD/GOOD/BEST, an
    /// effect, a 4 s centre notice 11909-11911; the S/M/L stars twinkle one pair per frame.
    /// THANKSGIVING [exact] (ui+0x180, vtable 0x874a84, handler 0x692130, tick 0x692060): GHDistribution, then 0xe14 -> 0xe15
    /// {total}: GAGE width x the step's ratio, PERCENT "%d%%", STAR_01-05 by the step (GHEvent_MAXRECEIPT_LV1..4), 1TH..4TH
    /// 11903-11906 and NAME_01-04 reward_step_0{step}_{1st..4th}; EXCHAGE needs a receipt (11912) and sends 0x1a1; op 0x89
    /// sub 36 {item, rank} spins three reels (rank 1 beef x3, 2 cherry x3, 3 fish x3, 4 vegetable x3, else a mixed row;
    /// a reel step every 0.03 s, the reels stop at 37 / 45 / 53 steps), then the prize (11907 / 11908 or 11898).
    /// VALEN [exact] (ui+0x178, vtable 0x874ea8, handler 0x6942a0): a recipe (g/h/s_choco_recipe) opens the craft mode with
    /// the egg and paint counts (Sparkle Paint preferred), WRAP sends 0x1a1, sub 26 b = 1..24 (grade (b-1)%4+1, paint
    /// ((b-1)/4)&amp;1, recipe (b-1)/8) shows &lt;recipe&gt;_CHOCOLET1/4/6/10 after 1.4 s with 11578; a basket (g/h/s_make_giftbox)
    /// opens the basket mode: EAT (sub 27 exp -> EVENT 4690000 after 1 s) or SEND (EVENT 4700000).
    /// EVENT 4700000 [exact] (ui+0x188, vtable 0x8749c0): EDIT_RECIEVER, SEARCHBUTTON (0x51a -> 0x51b: 10107 / 10106),
    /// EDIT_CONTENT, ITEMINFO 11576 (11606 after the event), INFO 11577, the basket on SLOTX; GIFTBUTTON checks 10285,
    /// 10286, 11010, 10287, 10450, then the note 0x5ce with the basket attached (0x5cf: 11033 ...) and 0xe10 for the bonus.
    /// Remake: the same screens over the server's harvest.craft / harvest.exchange / event.basket / event.box and the new
    /// valen.check / valen.gift (src/events.js: the basket arrives as "Gifted ... Basket" in a note, an Easter Rabbit Doll for
    /// both during the event). Offline a gift has no one to go to (the screen says so).
    /// </summary>
    public static class HarvestScreens
    {
        public const int AutumnDialog = 4670000, ThanksDialog = 4680000, ValenDialog = 4660000, ExpDialog = 4690000, GiftDialog = 4700000;
        static PlayerProfile P => PlayerProfile.Current;
        static UiManager hooked;

        /// <summary>"%02d/%02d" (11580 brown when enough, 11581 red when short): the client's COLOR tag as the text colour.</summary>
        static void SetCount(NuiView v, string id, int have, int need)
        {
            var c = v ? v.Control(id) : null;
            if (!c) return;
            string fmt = EventUi.T(have < need ? "11581" : "11580", "%02d/%02d", have % 100, need);
            c.SetText(System.Text.RegularExpressions.Regex.Replace(fmt, "</?COLOR[^>]*>", ""));
            var col = have < need ? new Color32(0x9d, 0x0a, 0x0e, 0xff) : new Color32(0x60, 0x39, 0x13, 0xff);
            foreach (var t in c.GetComponentsInChildren<UnityEngine.UI.Text>(true)) t.color = col;
        }

        /// <summary>The client's &lt;COLOR #AARRGGBB&gt; tags as Unity rich text.</summary>
        public static string RichColor(string s) => System.Text.RegularExpressions.Regex.Replace(s ?? "", "<COLOR #([0-9A-Fa-f]{2})([0-9A-Fa-f]{6})>", "<color=#$2$1>").Replace("</COLOR>", "</color>");

        static void Notice(string text)
        {
            ChatBox.System(text);
            var n = ServerNotices.Ensure();
            if (n) n.Emergency(text, 4f, false, chat: false);
        }

        static void Hook(UiManager ui)
        {
            if (hooked == ui) return;
            hooked = ui;
            ui.On("ID_AUTUMN_CLOSE", _ => CloseAutumn());
            ui.On("ID_AUTUMN_MAKE_BUTTON", _ => Make());
            ui.On("ID_THANKSGIVING_EXIT", _ => CloseThanks());
            ui.On("ID_THANKSGIVING_EXCHAGE", _ => Exchange());
            ui.On("ID_VALEN_CLOSE", _ => CloseValen());
            ui.On("ID_VALEN_WRAP", _ => Wrap());
            ui.On("ID_VALEN_EAT", _ => Eat());
            ui.On("ID_VALEN_SEND", _ => OpenGift(basket));
            ui.On("ID_EVENT_GIFT_CLOSE", _ => CloseGift());
            ui.On("ID_EVENT_GIFT_SEARCHBUTTON", _ => Search());
            ui.On("ID_EVENT_GIFTBUTTON", _ => SendGift());
        }

        static NuiView Open(int id)
        {
            var ui = UiManager.Instance;
            if (!ui || P == null) return null;
            Hook(ui);
            var v = ui.Open(id);
            if (v) { EventUi.Sfx("SID_SYSWIN_CREATE"); EventArtFallback.Apply(v); }
            return v;
        }

        static void Close(ref NuiView v) { if (v && UiManager.Instance) UiManager.Instance.Close(v); v = null; }

        // ================= AUTUMN 4670000 =================
        static NuiView autumn;
        static Action<bool> autumnDone;
        static int autumnState;        // 0 idle, 1 waiting 1.4 s for the result, 2 shown
        static float autumnAt;
        static int autumnGrade = -1;
        public static NuiView AutumnView => autumn;
        public static int AutumnGrade => autumnGrade;

        public static void OpenAutumn(string recipe, Action<bool> done)
        {
            CloseAutumn();
            autumn = Open(AutumnDialog);
            if (!autumn) { done?.Invoke(false); return; }
            autumnDone = done; autumnState = 0; autumnGrade = -1;
            autumn.gameObject.AddComponent<EventAnim>().Tick = AutumnTick;
            foreach (var id in new[] { "BAD", "GOOD", "BEST" }) autumn.SetVisible("ID_AUTUMN_RESULT_" + id, false);
            autumn.SetVisible("ID_AUTUMN_RESULT_NO", true);
            RefreshAutumn();
        }

        static void RefreshAutumn()
        {
            if (!autumn) return;
            bool foods = true;
            for (int i = 0; i < 4; i++)
            {
                var (id, n) = Harvest.BoxIngredients[i];
                SetCount(autumn, $"ID_AUTUMN_FOOD{i + 1}_COUNT", P.Count(id), n);
                foods &= P.Count(id) >= n;
            }
            SetCount(autumn, "ID_AUTUMN_BOX1_COUNT", P.Count(Harvest.NormalBox), 1);
            SetCount(autumn, "ID_AUTUMN_BOX2_COUNT", P.Count(Harvest.IceBox), 1);
            autumn.SetEnabled("ID_AUTUMN_MAKE_BUTTON", foods && (P.Count(Harvest.NormalBox) > 0 || P.Count(Harvest.IceBox) > 0) && autumnState != 1);
        }

        /// <summary>MAKE (0x692810): the craft with the Ice Box when there is one, else the Normal box.</summary>
        public static void Make()
        {
            if (!autumn || autumnState == 1) return;
            bool ice = P.Count(Harvest.IceBox) > 0;
            autumn.SetEnabled("ID_AUTUMN_MAKE_BUTTON", false);
            EventUi.Sfx("SID_ITEM_INSTALL");
            autumnState = 1;
            EventNet.Request("harvest.craft", new Dictionary<string, object> { ["ice"] = ice }, () => Harvest.CraftBoxOffline(P, ice, EventNet.Rng), r =>
            {
                if (!r.Ok) { autumnState = 0; UiPopup.Message(r.Error); RefreshAutumn(); return; }
                autumnGrade = Mathf.Clamp(r.Grants.FirstOrDefault()?.Etc ?? 0, 0, 2);
                autumnAt = autumn ? autumn.GetComponent<EventAnim>().T : 0f;
                EventNet.Log(r.Grants);
            });
        }

        static void AutumnTick(float t)
        {
            if (!autumn) return;
            // the stars: S, S+M, S+M+L on both sides, then again (0x692c50 steps per frame; ~7 steps a second here)
            int step = Mathf.FloorToInt(t * 7f) % 4;
            string[] size = { "S", "M", "L" };
            for (int i = 0; i < 3; i++) { autumn.SetVisible($"ID_AUTUMN_STAR_{size[i]}_LEFT", i < step); autumn.SetVisible($"ID_AUTUMN_STAR_{size[i]}_RIGHT", i < step); }
            if (autumnState == 1 && autumnGrade >= 0 && t - autumnAt >= 1.4f)
            {
                autumnState = 2;
                autumn.SetVisible("ID_AUTUMN_RESULT_NO", false);
                string[] res = { "BAD", "GOOD", "BEST" };
                for (int i = 0; i < 3; i++) autumn.SetVisible("ID_AUTUMN_RESULT_" + res[i], i == autumnGrade);
                GameEffects.HarvestBoxMade();   // 0x692d00: goodharvest_fx_01 + SID_VALENTAIN_COMPLETE
                Notice(EventUi.T(new[] { "11909", "11910", "11911" }[autumnGrade], new[] { "Small distribution box manufacturing succeeded", "Medium distribution box manufacturing succeeded", "Large distribution box manufacturing succeeded" }[autumnGrade]));
                var d = autumnDone; autumnDone = null; d?.Invoke(true);
                RefreshAutumn();
            }
        }

        public static void CloseAutumn() { var d = autumnDone; autumnDone = null; Close(ref autumn); d?.Invoke(autumnState == 2); }

        // ================= THANKSGIVING 4680000 =================
        static NuiView thanks;
        static float gaugeWidth = -1f;
        static int spinRank = -1;
        static float spinStart;
        static EventReply spinReply;
        static readonly string[] ReelItems = { "event_meat_n", "event_fruit_n", "event_fish_n", "event_vegetable_n" };   // beef, cherry, fish, vegetable
        static readonly int[][] Mixed = { new[] { 0, 1, 2 }, new[] { 1, 2, 3 }, new[] { 2, 3, 0 }, new[] { 3, 0, 1 }, new[] { 0, 0, 1 }, new[] { 1, 1, 2 }, new[] { 2, 2, 3 }, new[] { 3, 3, 0 }, new[] { 0, 2, 0 }, new[] { 1, 3, 1 } };
        static int[] reelTarget = { 0, 0, 0 };
        public static NuiView ThanksView => thanks;
        public static bool Spinning => spinRank >= 0;

        /// <summary>The distribution centre after GHDistribution (0xe14 -> 0xe15 {total}).</summary>
        public static void OpenThanks()
        {
            CloseThanks();
            thanks = Open(ThanksDialog);
            if (!thanks) return;
            var g = thanks.Control("ID_THANKSGIVING_GAGE");
            gaugeWidth = g ? ((RectTransform)g.transform).sizeDelta.x : 0f;
            // the Base_UI art (ui_contents762.dds) isn't in this install: a dark panel behind the dialog at BACK's rect
            var back = thanks.Control("ID_THANKSGIVING_BACK");
            if (back)
            {
                var brt = (RectTransform)back.transform;
                var panel = NuiView.NewRect("event backdrop", brt.parent);
                panel.anchorMin = brt.anchorMin; panel.anchorMax = brt.anchorMax; panel.pivot = brt.pivot;
                panel.anchoredPosition = brt.anchoredPosition; panel.sizeDelta = brt.sizeDelta;
                panel.SetAsFirstSibling();
                var img = panel.gameObject.AddComponent<UnityEngine.UI.Image>();
                img.color = new Color(0.16f, 0.12f, 0.07f, 0.94f);
                img.raycastTarget = true;
            }
            foreach (var id in new[] { "ID_THANKSGIVING_MACHINE_ITEM_01", "ID_THANKSGIVING_MACHINE_ITEM_02", "ID_THANKSGIVING_MACHINE_ITEM_03", "ID_THANKSGIVING_ITEM_01", "ID_THANKSGIVING_ITEM_02", "ID_THANKSGIVING_ITEM_03", "ID_THANKSGIVING_ITEM_04" })
                thanks.SetVisible(id, false);
            thanks.gameObject.AddComponent<EventAnim>().Tick = ThanksTick;
            spinRank = -1;
            for (int k = 0; k < 3; k++) EventUi.Icon(thanks, $"ID_THANKSGIVING_DEF{k + 1}", ReelItems[k]);
            RefreshThanks();
        }

        public static void RefreshThanks()
        {
            if (!thanks) return;
            long total = Harvest.ReceiptTotal;
            int scale = OnlineSession.Active ? 1000 : 10000;
            int step = Harvest.Step(total, scale);
            float ratio = 1f;
            if (step <= 4)
            {
                long lo = step == 1 ? 0 : Harvest.Steps[step - 2] / scale, hi = Harvest.Steps[step - 1] / scale;
                ratio = hi > lo ? Mathf.Clamp01((float)(total - lo) / (hi - lo)) : 1f;
            }
            var g = thanks.Control("ID_THANKSGIVING_GAGE");
            if (g) { var rt = (RectTransform)g.transform; rt.sizeDelta = new Vector2(gaugeWidth * ratio, rt.sizeDelta.y); }
            thanks.SetText("ID_THANKSGIVING_PERCENT", $"{Mathf.RoundToInt(ratio * 100)}%");
            for (int i = 1; i <= 5; i++) thanks.SetVisible($"ID_THANKSGIVING_STAR_{i:00}", step > i - 1);
            string[] ord = { "1st", "2nd", "3rd", "4th" };
            for (int i = 1; i <= 4; i++)
            {
                thanks.SetText($"ID_THANKSGIVING_{i}TH", EventUi.T((11902 + i).ToString(), new[] { "First", "Second", "Third", "Fourth" }[i - 1]));
                thanks.SetText($"ID_THANKSGIVING_NAME_{i:00}", EventUi.T($"reward_step_{Mathf.Min(step, 5):00}_{ord[i - 1]}", ""));
            }
            thanks.SetEnabled("ID_THANKSGIVING_EXCHAGE", spinRank < 0);
            thanks.SetEnabled("ID_THANKSGIVING_EXIT", spinRank < 0);
        }

        /// <summary>EXCHAGE: a receipt (11912 without), the spin, then the prize.</summary>
        public static void Exchange()
        {
            if (!thanks || spinRank >= 0) return;
            if (P.Count(Harvest.Receipt) <= 0) { ChatBox.System(EventUi.T("11912", "Need more receipt item.\nDistribute ingredients to restaurants to obtain receipt.")); return; }
            thanks.SetEnabled("ID_THANKSGIVING_EXCHAGE", false);
            thanks.SetEnabled("ID_THANKSGIVING_EXIT", false);
            EventNet.Request("harvest.exchange", new Dictionary<string, object>(), () => Harvest.ExchangeOffline(P, EventNet.Rng), r =>
            {
                if (!r.Ok) { UiPopup.Message(r.Error); RefreshThanks(); return; }
                spinReply = r;
                spinRank = Mathf.Clamp(r.Data.I("rank"), 0, 4);
                reelTarget = spinRank >= 1 ? new[] { spinRank - 1, spinRank - 1, spinRank - 1 } : Mixed[EventNet.Rng.Next(Mixed.Length)];
                spinStart = thanks ? thanks.GetComponent<EventAnim>().T : 0f;
                EventUi.Sfx("SID_GACHA_START");
            });
        }

        static void ThanksTick(float t)
        {
            if (!thanks || spinRank < 0) return;
            int c = Mathf.FloorToInt((t - spinStart) / 0.03f);
            int[] stop = { 37, 45, 53 };
            for (int k = 0; k < 3; k++) EventUi.Icon(thanks, $"ID_THANKSGIVING_DEF{k + 1}", ReelItems[c < stop[k] ? (c + k) % 4 : reelTarget[k]]);
            if (c < stop[2]) return;
            var r = spinReply; int rank = spinRank;
            spinRank = -1; spinReply = null;
            var g = r.Grants.FirstOrDefault();
            string title = EventUi.T("11900", "Fall Harvest Event: Reward for Distribution center Receipt");
            if (g == null) { EventPopup.Show("lotto_00001", title, EventUi.T("11898", "Boom ! Try again")); EventUi.Sfx("SID_PVP_LOSE"); }
            else
            {
                ChatBox.System(EventUi.T("11907", "You obtained 1 %s item by exchanging receipts.", EventItems.Describe(g)));
                EventPopup.Show(EventItems.Icon(g), title, EventUi.T("11908", "Congrats!\nYou've obtained %s", EventItems.Describe(g)));
                EventUi.Sfx("SID_ITEM_DROP_GET1");
            }
            EventNet.Log(r.Grants);
            LastExchangeRank = rank;
            if (OnlineSession.Active) SeasonEvents.Refresh(RefreshThanks); else RefreshThanks();
        }

        public static int LastExchangeRank { get; private set; } = -1;
        public static void CloseThanks() { spinRank = -1; Close(ref thanks); }

        // ================= VALEN 4660000 =================
        static NuiView valen;
        static string recipe, basket;
        static Action<bool> valenDone;
        static int valenCode, valenState;      // state: 0 idle, 1 waiting for the craft result, 2 shown
        static float valenAt;
        static long eatenExp = -1;
        public static NuiView ValenView => valen;
        public static int ValenCode => valenCode;

        /// <summary>A result code 1..24 -> (recipe 0 g / 1 h / 2 s, paint 0 Colorful / 1 Sparkle, grade 1-4).</summary>
        public static (int recipe, int paint, int grade) Decode(int b) => b < 1 ? (0, 0, 0) : ((b - 1) / 8, ((b - 1) / 4) & 1, (b - 1) % 4 + 1);
        static readonly int[] GradeEggs = { 0, 1, 4, 6, 10 };

        public static void OpenRecipe(string recipeItem, Action<bool> done)
        {
            if (!Harvest.BasketNeeds.ContainsKey(recipeItem)) { done?.Invoke(false); return; }
            OpenValen(done);
            if (!valen) return;
            recipe = recipeItem; basket = null; valenCode = 0;
            ValenMode(true);
            RefreshValen();
        }

        public static void OpenBasket(string basketItem, Action<bool> done)
        {
            OpenValen(done);
            if (!valen) return;
            recipe = null; basket = basketItem;
            valenCode = StoredCode(basketItem);
            ValenMode(false);
            ShowBasket(valenCode, basketItem);
        }

        static void OpenValen(Action<bool> done)
        {
            CloseValen();
            valen = Open(ValenDialog);
            if (!valen) { done?.Invoke(false); return; }
            valenDone = done; valenState = 0; eatenExp = -1;
            valen.gameObject.AddComponent<EventAnim>().Tick = ValenTick;
            foreach (var c in valen.Controls) if (c.Data.IdName != null && c.Data.IdName.Contains("CHOCOLET")) c.gameObject.SetActive(false);
        }

        static void ValenMode(bool craft)
        {
            valen.SetVisible("ID_VALEN_RECIPE_BACK", craft);
            valen.SetVisible("ID_VALEN_BOX_BACK", !craft);
            valen.SetVisible("ID_VALEN_WRAP", craft);
            valen.SetVisible("ID_VALEN_EAT", !craft);
            valen.SetVisible("ID_VALEN_SEND", !craft);
            foreach (var id in new[] { "WRAP1_COUNT", "WRAP2_COUNT", "BLACK_COUNT", "WHITE_COUNT", "PINK_COUNT" }) valen.SetVisible("ID_VALEN_" + id, craft);
            foreach (var id in new[] { "TITLE_H", "TITLE_S", "TITLE_CAREFUL", "TITLE_TWINK" }) valen.SetVisible("ID_VALEN_" + id, false);
        }

        static void RefreshValen()
        {
            if (!valen || recipe == null) return;
            var n = Harvest.BasketNeeds[recipe];
            bool sparkle = P.Count("twinkling_wrapping") > 0, colorful = P.Count("careful_wrapping") > 0;
            int needSparkle = sparkle ? 1 : colorful ? 0 : 1, needColorful = sparkle ? 0 : 1;
            SetCount(valen, "ID_VALEN_WRAP2_COUNT", P.Count("twinkling_wrapping"), needSparkle);
            SetCount(valen, "ID_VALEN_WRAP1_COUNT", P.Count("careful_wrapping"), needColorful);
            SetCount(valen, "ID_VALEN_BLACK_COUNT", P.Count("black_chocolete"), n.normal);
            SetCount(valen, "ID_VALEN_WHITE_COUNT", P.Count("white_chocolete"), n.silver);
            SetCount(valen, "ID_VALEN_PINK_COUNT", P.Count("pink_chocolete"), n.golden);
            char r = recipe[0];
            valen.SetVisible("ID_VALEN_TITLE_H", r == 'h');
            valen.SetVisible("ID_VALEN_TITLE_S", r == 's');
            if (valenState != 2)
            {
                valen.SetVisible("ID_VALEN_G_CHOCOLET_NO", r == 'g');
                valen.SetVisible("ID_VALEN_H_CHOCOLET_N", r == 'h');
                valen.SetVisible("ID_VALEN_S_CHOCOLET_N", r == 's');
            }
            bool ok = (sparkle || colorful) && P.Count("black_chocolete") >= n.normal && P.Count("white_chocolete") >= n.silver && P.Count("pink_chocolete") >= n.golden && P.Count(recipe) > 0;
            valen.SetEnabled("ID_VALEN_WRAP", ok && valenState != 1);
        }

        /// <summary>WRAP (0x693960): the basket from the recipe pool; its code b shows after 1.4 s with 11578.</summary>
        public static void Wrap()
        {
            if (!valen || recipe == null || valenState == 1) return;
            valen.SetEnabled("ID_VALEN_WRAP", false);
            EventUi.Sfx("SID_ITEM_INSTALL");
            valenState = 1;
            string rc = recipe;
            EventNet.Request("event.basket", new Dictionary<string, object> { ["recipe"] = rc }, () => Harvest.BasketOffline(P, rc, EventNet.Rng), r =>
            {
                if (!r.Ok) { valenState = 0; UiPopup.Message(r.Error); RefreshValen(); return; }
                var g = r.Grants.FirstOrDefault();
                valenCode = g?.Etc ?? 0;
                if (g != null) StoreCode(g.ItemId, valenCode);
                valenAt = valen ? valen.GetComponent<EventAnim>().T : 0f;
                EventNet.Log(r.Grants);
            });
        }

        static void ShowBasket(int code, string basketItem)
        {
            if (!valen) return;
            var (rc, paint, grade) = Decode(code);
            if (code < 1 && basketItem != null) { rc = Math.Max(0, "ghs".IndexOf(basketItem[0])); grade = 1; }
            string[] pre = { "G", "H", "S" };
            foreach (var p in pre) foreach (var e in new[] { "1", "4", "6", "10" }) valen.SetVisible($"ID_VALEN_{p}_CHOCOLET{e}", false);
            valen.SetVisible("ID_VALEN_G_CHOCOLET_NO", false); valen.SetVisible("ID_VALEN_H_CHOCOLET_N", false); valen.SetVisible("ID_VALEN_S_CHOCOLET_N", false);
            if (grade >= 1) valen.SetVisible($"ID_VALEN_{pre[rc]}_CHOCOLET{GradeEggs[grade]}", true);
            if (basketItem != null && recipe == null)
            {
                valen.SetVisible("ID_VALEN_TITLE_CAREFUL", paint == 0);
                valen.SetVisible("ID_VALEN_TITLE_TWINK", paint == 1);
            }
        }

        static void ValenTick(float t)
        {
            if (!valen) return;
            // the stars grow every 0.5 s: S, S+M, S+M+L, then again
            int step = Mathf.FloorToInt(t / 0.5f) % 4;
            string[] size = { "S", "M", "L" };
            for (int i = 0; i < 3; i++) { valen.SetVisible($"ID_VALEN_STAR_{size[i]}_LEFT", i < step); valen.SetVisible($"ID_VALEN_STAR_{size[i]}_RIGHT", i < step); }
            if (valenState == 1 && valenCode > 0 && t - valenAt >= 1.4f)
            {
                valenState = 2;
                ShowBasket(valenCode, null);
                GameEffects.ChocolateWrapped();   // 0x694098: SID_VALENTAIN_COMPLETE + valen_choco_fx_01
                Notice(EventUi.T("11578", "You have painted %d eggs successfully!", GradeEggs[Decode(valenCode).grade]));
                var d = valenDone; valenDone = null; d?.Invoke(true);
                RefreshValen();
            }
            if (eatenExp >= 0 && t - valenAt >= 1f) { ShowExp(eatenExp); eatenExp = -1; }
        }

        /// <summary>EAT (0x6939e0): the basket opens for its EXP (sub 27) -> "+N EXP" after 1 s.</summary>
        public static void Eat()
        {
            if (!valen || basket == null || P.Count(basket) <= 0) return;
            valen.SetEnabled("ID_VALEN_EAT", false); valen.SetEnabled("ID_VALEN_SEND", false);
            EventUi.Sfx("SID_ITEM_USE");
            string b = basket;
            EventNet.Request("event.box", new Dictionary<string, object> { ["item"] = b }, () => LuckyBag.OpenOffline(P, b, EventNet.Rng), r =>
            {
                if (!r.Ok) { UiPopup.Message(r.Error); valen.SetEnabled("ID_VALEN_EAT", true); valen.SetEnabled("ID_VALEN_SEND", true); return; }
                foreach (var c in valen.Controls) if (c.Data.IdName != null && c.Data.IdName.Contains("CHOCOLET")) c.gameObject.SetActive(false);
                eatenExp = r.Grants.Where(g => g.Kind == GrantKind.Exp).Sum(g => (long)g.Count);
                valenAt = valen.GetComponent<EventAnim>().T;
                EventNet.Log(r.Grants);
                var d = valenDone; valenDone = null; d?.Invoke(true);
            });
        }

        /// <summary>Sub 30: the basket is gone (eaten or gifted).</summary>
        static void BasketGone()
        {
            if (!valen) return;
            valen.SetEnabled("ID_VALEN_EAT", false); valen.SetEnabled("ID_VALEN_SEND", false);
            foreach (var c in valen.Controls) if (c.Data.IdName != null && c.Data.IdName.Contains("CHOCOLET")) c.gameObject.SetActive(false);
        }

        public static void CloseValen() { var d = valenDone; valenDone = null; CloseExp(); CloseGift(); Close(ref valen); d?.Invoke(valenState == 2); }

        static int StoredCode(string item)
        {
            var e = EventNet.State(P).BasketCodes.LastOrDefault(x => x.StartsWith(item + "="));
            return e != null && int.TryParse(e.Substring(item.Length + 1), out int c) ? c : 0;
        }

        static void StoreCode(string item, int code)
        {
            if (string.IsNullOrEmpty(item)) return;
            var list = EventNet.State(P).BasketCodes;
            list.RemoveAll(x => x.StartsWith(item + "="));
            list.Add($"{item}={code}");
        }

        // ================= EVENT 4690000 "+N EXP" =================
        static NuiView exp;
        public static NuiView ExpView => exp;

        public static void ShowExp(long n)
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            if (!exp) exp = ui.Open(ExpDialog);
            if (!exp) return;
            n = Math.Max(0, Math.Min(9999, n));
            int[] place = { 1, 10, 100, 1000 };
            int[] digit = { (int)(n % 10), (int)(n / 10 % 10), (int)(n / 100 % 10), (int)(n / 1000 % 10) };
            for (int p = 0; p < 4; p++)
            {
                bool show = p < 2 || (p == 2 && (digit[2] != 0 || digit[3] != 0)) || (p == 3 && digit[3] != 0);
                for (int d = 0; d < 10; d++) exp.SetVisible($"ID_EVENT_E_EXP_NUMBER_{place[p]}_{d}", show && d == digit[p]);
            }
            exp.SetVisible("ID_EVENT_E_EXP_PLUS", true);
            exp.SetVisible("ID_EVENT_E_EXP", true);
            exp.SetVisible("ID_EVENT_E_EXP_COMMA", digit[3] != 0);
            EventUi.Sfx("SID_LEVEL_UP");
        }

        static void CloseExp() => Close(ref exp);

        // ================= EVENT 4700000 gift =================
        static NuiView gift;
        static string giftItem, checkedName;
        public static NuiView GiftView => gift;

        public static void OpenGift(string basketItem)
        {
            if (basketItem == null || P.Count(basketItem) <= 0) return;
            CloseGift();
            gift = Open(GiftDialog);
            if (!gift) return;
            giftItem = basketItem; checkedName = null;
            foreach (var id in new[] { "ID_EVENT_GIFT_SLOT0", "ID_EVENT_GIFT_SLOT1" }) gift.SetVisible(id, false);
            EventUi.Icon(gift, "ID_EVENT_GIFT_SLOTX", basketItem);
            gift.SetText("ID_EVENT_GIFT_ITEMINFO", SeasonEvents.Is(EventSeason.Valentine)
                ? EventUi.T("11576", "[Easter Bonus Reward]\nExchange Egg Baskets with your friends and receive three additional items : Easter Rabbit Doll, Battle Zone EXP Ticket, and Passive Assist.")
                : EventUi.T("11606", "The event period has ended.\nBonus rewards for gift exchanges will now be reduced."));
            gift.SetText("ID_EVENT_GIFT_INFO", EventUi.T("11577", "Please enter a message for this gift."));
        }

        static void Search()
        {
            if (!gift) return;
            string name = gift.GetInput("ID_EVENT_GIFT_EDIT_RECIEVER").Trim();
            if (name.Length < 2) { ChatBox.System(EventUi.T("10285", "Incorrect character name.")); return; }
            if (!OnlineSession.Active) { checkedName = null; ChatBox.System("Gifts travel through the online server: there is no one to send to offline."); return; }
            EventNet.Request("valen.check", new Dictionary<string, object> { ["name"] = name }, () => EventReply.Fail("offline"), r =>
            {
                checkedName = r.Ok ? r.Data.S("name") : null;
                ChatBox.System(r.Ok ? EventUi.T("10107", "Correct character name.") : EventUi.T("10106", "Incorrect character name."));
            });
        }

        /// <summary>GIFTBUTTON (0x690980) checks in the client's order, then valen.gift (0x5ce + 0xe10).</summary>
        public static void SendGift(Action<bool> done = null)
        {
            if (!gift) { done?.Invoke(false); return; }
            string name = gift.GetInput("ID_EVENT_GIFT_EDIT_RECIEVER").Trim();
            string text = gift.GetInput("ID_EVENT_GIFT_EDIT_CONTENT").Trim();
            string err = name.Length < 2 ? EventUi.T("10285", "Incorrect character name.")
                : !string.Equals(name, checkedName, StringComparison.OrdinalIgnoreCase) ? EventUi.T("10286", "Press the 'Search' button to check the availability of driver name.")
                : text.Length == 0 ? EventUi.T("11010", "Type your message here.")
                : string.Equals(name, P.Name, StringComparison.OrdinalIgnoreCase) ? EventUi.T("10287", "You cannot send a gift to yourself.") : null;
            if (err != null) { ChatBox.System(err); done?.Invoke(false); return; }
            EventNet.Request("valen.gift", new Dictionary<string, object> { ["item"] = giftItem, ["to"] = name, ["message"] = text }, () => EventReply.Fail("offline"), r =>
            {
                if (!r.Ok) { ChatBox.System(r.Error); done?.Invoke(false); return; }
                ChatBox.System(EventUi.T("11033", "The message has been sent to %s.", r.Data.S("to", name)));
                EventNet.Log(r.Grants);
                EventUi.Sfx("SID_ITEM_SEND");
                CloseGift();
                BasketGone();
                done?.Invoke(true);
            });
        }

        public static void CloseGift() => Close(ref gift);
    }
}
