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
    /// GACHAINVEN 4570000-4610000, the "Gotcha! inventory" of the capsule shop (DriftCity_RE\EVENTS.md section 2).
    ///
    /// Client [exact]: object ui+0x344 (vtable 0x872bcc, create 0x638530, Show 0x638f30, refresh 0x638460); opened and closed
    /// by the shop's GACHAINVENNEW (0x63afb0, the shop's lights stop meanwhile). Base 4570000: EXIT (back to the shop), LEFT /
    /// RIGHT page -1 / +1 wrapping over MAIN 4580000, TUNNING 4590000, SETPARTS 4600000, BADGE 4610000, or only MAIN and
    /// TUNNING with globalcontent PartsGotchaClose (1 in the US data). MAIN shows the season's Gotcha car (CARNAME / CARIMAGE
    /// [GotchaSeason]) with its Vehicle Parts A-D and Key Parts counts (s 1-4: SEASON layout, s >= 5: SEASON5TH), the ability
    /// of car Gacha_inform_carstat 140 ("%d (V9)"), texts gacha_inform_car_01/02, 11482/11483/11609/11801/11484, 10800-10803.
    /// TUNNING lists the season's coin / Mito air intake and decal ("%d D" 10280) with a hover tooltip (11481, 10353...);
    /// SETPARTS the set parts (ability, count, 11486/11487); BADGE the DriftCity badges (count, resale price 11488, SELL ->
    /// quantity 10420 -> op 0x197; hover shows licence 7062). The combine itself is not here: using Key Parts (gacha_key,
    /// 0x5d687b) needs every part of the car's compound pool, else gacha_compound_failure.
    /// Remake: the client data has none of the per-season pools this screen asks for, so they are this rebuild's: the car by
    /// season (1/5 Shadow, 2/6 Hunter, 3/4/7 Fate, 8 Monza, 9 Winner), its parts (i_n_shadow_A-D, else i_n_comp_A-D),
    /// key parts i_n_&lt;car&gt;_k_vN; TUNNING shows the first two items of the season's capsule pools; SETPARTS and BADGE use the
    /// rw_gacha_parts_*_set_99 / rw_gacha_dcbadge_*_99 pools. Server: gacha.compound, gacha.sell (src/events.js).
    /// </summary>
    public static class GachaInven
    {
        public const int BaseDialog = 4570000, MainDialog = 4580000, TuneDialog = 4590000, SetDialog = 4600000, BadgeDialog = 4610000;
        public static readonly string[] Cars = { "shadow", "shadow", "hunter", "fate", "fate", "shadow", "hunter", "fate", "monza", "winner" };
        public const int CarStatUnique = 140;    // globalcontent Gacha_inform_carstat
        static readonly int[] Dialogs = { MainDialog, TuneDialog, SetDialog, BadgeDialog };

        static PlayerProfile P => PlayerProfile.Current;
        static NuiView baseView, page;
        static int pageIndex;
        static UiManager hooked;

        public static NuiView BaseView => baseView;
        public static NuiView PageView => page;
        public static int Page => pageIndex;
        public static bool IsOpen => baseView;
        /// <summary>Pages reachable with LEFT / RIGHT: 2 with PartsGotchaClose (the US data), else 4.</summary>
        public static int Pages => Pvp.Config("PartsGotchaClose", 1) != 0 ? 2 : 4;

        // ---------------- data (remake pools) ----------------
        public static string CarFor(int season) => Cars[season <= 9 ? Mathf.Max(1, season) : (season - 1) % 9 + 1];
        public static string[] PartsFor(string car) => new[] { "A", "B", "C", "D" }.Select(x => car == "shadow" ? "i_n_shadow_" + x : "i_n_comp_" + x).ToArray();
        public static bool IsKey(string id, string car) => id != null && id.StartsWith($"i_n_{car}_k_v", StringComparison.OrdinalIgnoreCase);
        public static int KeyCount(string car) => P == null ? 0 : P.Items.Where(i => IsKey(i.Id, car)).Sum(i => i.Count);
        static string CashPool(int s) => $"rw_gacha_car_cash_{s:00}";
        static string MitoPool(int s) => $"rw_gacha_car_mito_{s:00}";
        static List<RewardRow> Rows(string pool) => EventItems.Pools.Rows(pool).Where(r => r.Probability > 0 && !string.IsNullOrEmpty(r.ItemId) && r.ItemId != "0").ToList();

        /// <summary>The CARNAME / CARIMAGE index of a season (labels 1-8; Winner has none: its Shadow-less season shows none).</summary>
        public static int CarLabel(int season) => season <= 8 ? season : CarFor(season) == "winner" ? -1 : Array.IndexOf(Cars, CarFor(season));

        // ---------------- open / close ----------------
        /// <summary>GACHAINVENNEW: opens over the capsule shop, or closes back to it.</summary>
        public static void Toggle() { if (IsOpen) Close(); else Open(); }

        public static void Open(int startPage = -1)
        {
            var ui = UiManager.Instance;
            if (!ui || P == null) return;
            Close();
            Hook(ui);
            baseView = ui.Open(BaseDialog);
            if (!baseView) return;
            if (startPage >= 0) pageIndex = Mathf.Clamp(startPage, 0, 3);
            if (pageIndex >= Pages && startPage < 0) pageIndex = 0;
            ShowPage();
            EventUi.Sfx("SID_SYSWIN_CREATE");
        }

        public static void Close()
        {
            var ui = UiManager.Instance;
            if (page && ui) ui.Close(page);
            if (baseView && ui) ui.Close(baseView);
            page = null; baseView = null;
        }

        static void Hook(UiManager ui)
        {
            if (hooked == ui) return;
            hooked = ui;
            ui.On("ID_GACHAINVEN_BASE_EXIT", _ => { EventUi.Sfx("SID_SYSWIN_DESTROY"); Close(); });
            ui.On("ID_GACHAINVEN_BASE_LEFT", _ => Turn(-1));
            ui.On("ID_GACHAINVEN_BASE_RIGHT", _ => Turn(+1));
            for (int i = 0; i < 6; i++)
            {
                int k = i;
                ui.On($"ID_GACHAINVEN_BADGE_ITEMSELLBTN_COIN{k}", _ => AskSell(BadgeItem(false, k)));
                ui.On($"ID_GACHAINVEN_BADGE_ITEMSELLBTN_MITO{k}", _ => AskSell(BadgeItem(true, k)));
            }
        }

        /// <summary>LEFT / RIGHT (0x638f90): -1 / +1, wrapping at the reachable pages.</summary>
        public static void Turn(int dir)
        {
            EventUi.Sfx("SID_BUTTON_CLICK");
            pageIndex = ((pageIndex + dir) % Pages + Pages) % Pages;
            ShowPage();
        }

        static void ShowPage()
        {
            var ui = UiManager.Instance;
            if (!ui || !baseView) return;
            if (page) ui.Close(page);
            page = ui.Open(Dialogs[pageIndex]);
            if (!page) return;
            int s = Gacha.Season;
            switch (pageIndex)
            {
                case 0: FillMain(s); break;
                case 1: FillTune(s); break;
                case 2: FillSet(); break;
                default: FillBadge(); break;
            }
        }

        // ---------------- MAIN 4580000 (0x634930 / 0x636a20) ----------------
        static void FillMain(int s)
        {
            const string M = "ID_GACHAINVEN_MAIN_";
            string car = CarFor(s);
            var parts = PartsFor(car);
            bool fifth = s >= 5;
            EventUi.HideAll(page, id => false);
            int label = CarLabel(s);
            for (int i = 0; i <= 8; i++)
            {
                string n = i == 0 ? "" : i.ToString("00");
                page.SetVisible("ID_GACHAINVEN_BASE_CARNAME" + n, i == label);
                page.SetVisible(M + "CARIMAGE" + n, i == label);
            }
            foreach (var id in new[] { "ABILITY_GRADETITLE", "ABILITY_SPDTITLE", "ABILITY_ACCTITLE", "ABILITY_COLTITLE", "ABILITY_BSTTITLE", "ABILITY_GRADE", "ABILITY_SPD", "ABILITY_ACC", "ABILITY_COL", "ABILITY_BST", "ABILITY_HOWTOUSE", "DESC" })
                page.SetVisible(M + id, true);
            page.SetText(M + "DESC", EventUi.T("gacha_inform_car_01", "The 'Gotcha!' can only be used to obtain items before a new 'Gotcha!' is added."));
            page.SetText(M + "ABILITY_HOWTOUSE", EventUi.T("gacha_inform_car_02", "Combine a Reward Vehicle Gift Box at a desired level, and then use it to receive a Normal, BT, or ST vehicle at random."));
            page.SetText(M + "ABILITY_GRADETITLE", EventUi.T("11484", "Rank"));
            page.SetText(M + "ABILITY_SPDTITLE", EventUi.T("10800", "Speed"));
            page.SetText(M + "ABILITY_ACCTITLE", EventUi.T("10802", "Accel"));
            page.SetText(M + "ABILITY_COLTITLE", EventUi.T("10801", "Durability"));
            page.SetText(M + "ABILITY_BSTTITLE", EventUi.T("10803", "Boost"));
            page.SetText(M + "ABILITY_GRADE", EventUi.T("gacha_inform_cargrade", "V1 ~ V8"));
            var stat = GameTables.PlayerCars.FirstOrDefault(v => v.UniqueId == CarStatUnique);
            page.SetText(M + "ABILITY_SPD", stat != null ? $"{stat.Speed} (V9)" : "-");
            page.SetText(M + "ABILITY_ACC", stat != null ? $"{stat.Accel} (V9)" : "-");
            page.SetText(M + "ABILITY_COL", stat != null ? $"{stat.Crash} (V9)" : "-");
            page.SetText(M + "ABILITY_BST", stat != null ? $"{stat.Boost} (V9)" : "-");
            int keys = KeyCount(car);
            string keyItem = P.Items.FirstOrDefault(i => IsKey(i.Id, car) && i.Count > 0)?.Id ?? $"i_n_{car}_k_v1";
            if (!fifth)
            {
                foreach (var id in new[] { "SEASON_KEYPARTSIMAGE", "SEASON_KEYIMAGE", "SEASON_COINGACHA_TITLE", "SEASON_MITOGACHA_TITLE", "SEASON_KEYPARTSGACHA_TITLE", "SEASON_EQUAL" })
                    page.SetVisible(M + id, true);
                for (int i = 1; i <= 10; i++) page.SetVisible($"{M}SEASON_ITEMBACK{i:00}", true);
                for (int i = 1; i <= 4; i++) page.SetVisible($"{M}SEASON_PLUS{i}", true);
                page.SetText(M + "SEASON_COINGACHA_TITLE", EventUi.T("11482", "Coin 'Gotcha!'"));
                page.SetText(M + "SEASON_MITOGACHA_TITLE", EventUi.T("11483", "Mito 'Gotcha!'"));
                page.SetText(M + "SEASON_KEYPARTSGACHA_TITLE", EventUi.T("11609", "Key Parts"));
                // coin row: parts A, B; Mito row: parts C, D (the remake's split of the four parts)
                for (int i = 0; i < 2; i++)
                {
                    PartCell(M + "SEASON_ITEMIMAGE_COIN" + i, M + "SEASON_ITEMTEXT_COIN" + i, parts[i]);
                    PartCell(M + "SEASON_ITEMIMAGE_MITO" + i, M + "SEASON_ITEMTEXT_MITO" + i, parts[2 + i]);
                }
                EventUi.Icon(page, M + "SEASON_ITEMIMAGE_KEYPARTS0", keyItem);
                page.SetVisible(M + "SEASON_ITEMTEXT_KEYPARTS0", true);
                page.SetText(M + "SEASON_ITEMTEXT_KEYPARTS0", EventUi.T("11330", "%d", keys));
            }
            else
            {
                foreach (var id in new[] { "SEASON5TH_CARPARTSSET_TITLE", "SEASON5TH_KEYPARTSIMAGE", "SEASON5TH_KEYIMAGE_BG", "SEASON5TH_KEYIMAGE" })
                    page.SetVisible(M + id, true);
                for (int i = 1; i <= 3; i++) page.SetVisible($"{M}SEASON5TH_ITEMBACK{i:00}", true);
                for (int i = 1; i <= 5; i++) page.SetVisible($"{M}SEASON5TH_PLUS{i}", true);
                page.SetText(M + "SEASON5TH_CARPARTSSET_TITLE", EventUi.T("11801", "New Vehicle Parts Set"));
                for (int i = 0; i < 4; i++) PartCell(M + "SEASON5TH_ITEMIMAGE" + i, M + "SEASON5TH_ITEMCNT" + i, parts[i]);
                EventUi.Icon(page, M + "SEASON5TH_ITEMIMAGE4", keyItem);
                page.SetVisible(M + "SEASON5TH_ITEMCNT4", true);
                page.SetText(M + "SEASON5TH_ITEMCNT4", EventUi.T("11330", "%d", keys));
            }
        }

        static void PartCell(string image, string text, string item)
        {
            EventUi.Icon(page, image, item);
            HoverName(image, item);
            page.SetVisible(text, true);
            page.SetText(text, EventUi.T("11330", "%d", P.Count(item)));
        }

        /// <summary>The client's item tooltip (0x620930): here the item name in the chat-free hover of the control.</summary>
        static void HoverName(string control, string item)
        {
            var c = page ? page.Control(control) : null;
            if (!c) return;
            c.Hovered += on => { if (on && page) ShowTip(item); };
        }

        static void ShowTip(string item)
        {
            if (!page) return;
            switch (pageIndex)
            {
                case 1:
                    page.SetText("ID_GACHAINVEN_TUNNING_ITEMDESC_TITLE", EventItems.Name(item));
                    page.SetText("ID_GACHAINVEN_TUNNING_ITEMDESC_SPD", EventUi.T("11481", "Performance Points: ") + "\n" + ItemCatalog.Clean(EventItems.Desc(item)));
                    EventUi.Icon(page, "ID_GACHAINVEN_TUNNING_ITEMDESC_ITEMPOS", item);
                    break;
                case 2:
                    page.SetText("ID_GACHAINVEN_SETPARTS_ITEMDESC_TITLE", EventItems.Name(item));
                    page.SetText("ID_GACHAINVEN_SETPARTS_ITEMDESC_SPD", EventUi.T("11481", "Performance Points: ") + "\n" + ItemCatalog.Clean(EventItems.Desc(item)));
                    EventUi.Icon(page, "ID_GACHAINVEN_SETPARTS_ITEMDESC_ITEMPOS", item);
                    break;
                case 3:
                    page.SetText("ID_GACHAINVEN_BADGE_ITEMTITLE", EventItems.Name(item));
                    page.SetText("ID_GACHAINVEN_BADGE_ITEMDESC", ItemCatalog.Clean(EventItems.Desc(item)));
                    EventUi.Icon(page, "ID_GACHAINVEN_BADGE_ITEMPOS", item);
                    break;
            }
        }

        // ---------------- TUNNING 4590000 (0x637440) ----------------
        static void FillTune(int s)
        {
            const string T = "ID_GACHAINVEN_TUNNING_";
            page.SetText(T + "DESC", EventUi.T("gacha_inform_tune", "This item enhances the overall performance of 'Gotcha!' vehicles when used."));
            int label = CarLabel(s);
            for (int i = 0; i <= 8; i++) page.SetVisible(T + "CARNAME" + (i == 0 ? "" : "_" + i.ToString("00")), i == label);
            bool fifth = s >= 5;
            page.SetText(T + "COINGACHA_TITLE", fifth ? EventUi.T("11718", "Drift Shop\nCash Item") : EventUi.T("11482", "Coin 'Gotcha!'"));
            page.SetText(T + "MITOGACHA_TITLE", fifth ? EventUi.T("11720", "Drift Shop\nMito Item") : EventUi.T("11483", "Mito 'Gotcha!'"));
            page.SetText(T + "COINGACHA_PERIOD", EventUi.T("11490", "Remaining Period"));
            page.SetText(T + "MITOGACHA_PERIOD", EventUi.T("11490", "Remaining Period"));
            page.SetText(T + "ITEMDESC_TITLE1", EventUi.T("11485", "[Item]"));
            page.SetText(T + "ITEMDESC_TITLE", ""); page.SetText(T + "ITEMDESC_SPD", "");
            var cash = Rows(CashPool(s)); var mito = Rows(MitoPool(s));
            for (int i = 0; i < 6; i++)
            {
                TuneCell("COIN", i, i < 2 && i < cash.Count ? cash[i] : null);
                TuneCell("MITO", i, i < 2 && i < mito.Count ? mito[i] : null);
            }
        }

        static void TuneCell(string side, int i, RewardRow row)
        {
            const string T = "ID_GACHAINVEN_TUNNING_";
            bool on = row != null;
            page.SetVisible($"{T}ITEMBTN_{side}{i}", on);
            page.SetVisible($"{T}ITEMPERIOD_{side}{i}", on);
            page.SetVisible($"{T}ITEMCOUNT_{side}{i}", on);
            if (!on) { page.SetVisible($"{T}ITEMIMAGE_{side}{i}", false); return; }
            EventUi.Icon(page, $"{T}ITEMIMAGE_{side}{i}", row.ItemId);
            int owned = OwnedCount(row.ItemId);
            page.SetText($"{T}ITEMPERIOD_{side}{i}", EventUi.T("10280", "%d D", owned > 0 ? row.Period : 0));
            page.SetText($"{T}ITEMCOUNT_{side}{i}", EventUi.T("11330", "%d", owned));
            HoverName($"{T}ITEMBTN_{side}{i}", row.ItemId);
        }

        /// <summary>Owned count of a capsule prize (bag stacks; custom-shop prizes are fitted straight away).</summary>
        static int OwnedCount(string id) => P.Count(id);

        // ---------------- SETPARTS 4600000 (0x6379f0) ----------------
        static void FillSet()
        {
            const string S = "ID_GACHAINVEN_SETPARTS_";
            page.SetText(S + "DESC", EventUi.T("gacha_inform_parts", "Gotcha! set parts guarantee superb performance compared to conventional set parts."));
            page.SetText(S + "COINGACHA_TITLE", EventUi.T("11482", "Coin 'Gotcha!'"));
            page.SetText(S + "MITOGACHA_TITLE", EventUi.T("11483", "Mito 'Gotcha!'"));
            page.SetText(S + "COINGACHA_PERIOD", EventUi.T("11481", "Performance Points: "));
            page.SetText(S + "MITOGACHA_PERIOD", EventUi.T("11481", "Performance Points: "));
            page.SetText(S + "COINGACHA_COUNT", EventUi.T("11486", "Available Amount"));
            page.SetText(S + "MITOGACHA_COUNT", EventUi.T("11486", "Available Amount"));
            page.SetText(S + "ITEMDESC_TITLE1", EventUi.T("11487", "[Set Parts]"));
            page.SetText(S + "ITEMDESC_TITLE", ""); page.SetText(S + "ITEMDESC_SPD", "");
            var cash = Rows("rw_gacha_parts_cash_set_99"); var mito = Rows("rw_gacha_parts_mito_set_99");
            for (int i = 0; i < 6; i++) { SetCell("COIN", i, i < cash.Count ? cash[i] : null); SetCell("MITO", i, i < mito.Count ? mito[i] : null); }
        }

        static void SetCell(string side, int i, RewardRow row)
        {
            const string S = "ID_GACHAINVEN_SETPARTS_";
            bool on = row != null;
            foreach (var c in new[] { "ITEMBTN_", "ITEMABILITY_", "ITEMCOUNT_" }) page.SetVisible(S + c + side + i, on);
            if (!on) { page.SetVisible(S + "ITEMIMAGE_" + side + i, false); return; }
            EventUi.Icon(page, S + "ITEMIMAGE_" + side + i, row.ItemId);
            page.SetText(S + "ITEMABILITY_" + side + i, (EventItems.Row(row.ItemId)?.Value ?? 0).ToString());
            page.SetText(S + "ITEMCOUNT_" + side + i, EventUi.T("11330", "%d", OwnedCount(row.ItemId)));
            HoverName(S + "ITEMBTN_" + side + i, row.ItemId);
        }

        // ---------------- BADGE 4610000 (0x637c20) ----------------
        static readonly string[,] badges = new string[2, 6];

        static string BadgeItem(bool mito, int i) => badges[mito ? 1 : 0, i];

        static void FillBadge()
        {
            const string B = "ID_GACHAINVEN_BADGE_";
            page.SetText(B + "DESC", EventUi.T("gacha_inform_badge", "DriftCity Character Badge Set: Obtain all of these badges to receive a License reward."));
            page.SetText(B + "COINGACHA_TITLE", EventUi.T("11482", "Coin 'Gotcha!'"));
            page.SetText(B + "MITOGACHA_TITLE", EventUi.T("11483", "Mito 'Gotcha!'"));
            page.SetText(B + "COINGACHA_PERIOD", EventUi.T("11488", "Resale Price"));
            page.SetText(B + "MITOGACHA_PERIOD", EventUi.T("11488", "Resale Price"));
            page.SetText(B + "COINGACHA_COUNT", EventUi.T("11486", "Available Amount"));
            page.SetText(B + "MITOGACHA_COUNT", EventUi.T("11486", "Available Amount"));
            page.SetText(B + "ITEMTITLE1", EventUi.T("11489", "[Lisence]"));
            page.SetText(B + "ITEMTITLE", ""); page.SetText(B + "ITEMDESC", "");
            var cash = Rows("rw_gacha_dcbadge_cash_99"); var mito = Rows("rw_gacha_dcbadge_mito_99");
            for (int i = 0; i < 6; i++)
            {
                badges[0, i] = i < cash.Count ? cash[i].ItemId : null;
                badges[1, i] = i < mito.Count ? mito[i].ItemId : null;
                BadgeCell(false, i); BadgeCell(true, i);
            }
        }

        static void BadgeCell(bool mito, int i)
        {
            const string B = "ID_GACHAINVEN_BADGE_";
            string side = mito ? "MITO" : "COIN", item = BadgeItem(mito, i);
            bool on = item != null;
            string countId = mito ? B + "ITEMSELL_COUNT" + i : B + "ITEMCOUNT_COIN" + i;
            foreach (var c in new[] { B + "ITEMBTN_" + side + i, B + "ITEMSELL_" + side + i, countId, B + "ITEMSELLBTN_" + side + i }) page.SetVisible(c, on);
            if (!on) { page.SetVisible(B + "ITEMIMAGE_" + side + i, false); return; }
            EventUi.Icon(page, B + "ITEMIMAGE_" + side + i, item);
            int owned = P.Count(item);
            page.SetText(countId, EventUi.T("11330", "%d", owned));
            page.SetText(B + "ITEMSELL_" + side + i, (EventItems.Row(item)?.Sell ?? 0).ToString("N0"));
            page.SetEnabled(B + "ITEMSELLBTN_" + side + i, owned > 0);
            HoverName(B + "ITEMBTN_" + side + i, item);
        }

        /// <summary>SELL: 10420 asks the quantity (the whole stack here), then op 0x197 (gacha.sell).</summary>
        static void AskSell(string item)
        {
            if (item == null || P == null || P.Count(item) <= 0) return;
            int n = P.Count(item);
            long price = (EventItems.Row(item)?.Sell ?? 0) * (long)n;
            UiPopup.Show($"{EventUi.T("10420", "Enter the quantity you want to sell.")}\n{EventItems.Name(item)} x{n}\n{EventUi.T("10510", "Do you want to sell this item?\nPrice: ")}{price:N0} {EventUi.T("10511", "Mito")}",
                b => { if (b == UiPopup.Yes) Sell(item, n); }, UiPopup.Yes, UiPopup.No);
        }

        public static void Sell(string item, int n, Action<bool> done = null)
        {
            EventNet.Request("gacha.sell", new Dictionary<string, object> { ["item"] = item, ["count"] = n }, () => SellOffline(P, item, n), r =>
            {
                if (!r.Ok) { UiPopup.Message(r.Error); done?.Invoke(false); return; }
                EventUi.Sfx("SID_ITEM_SELL");
                if (page && pageIndex == 3) FillBadge();
                done?.Invoke(true);
            });
        }

        public static EventReply SellOffline(PlayerProfile p, string item, int n)
        {
            if (item == null || !item.Contains("dcbadge")) return EventReply.Fail("This item cannot be sold here.");
            if (p.Count(item) < n || n <= 0) return EventReply.Fail(EventUi.T("10419", "Registration information is not consistent with item information."));
            long price = (EventItems.Row(item)?.Sell ?? 0) * (long)n;
            p.Give(item, -n);
            p.Mito += price;
            var r = new EventReply { Ok = true };
            r.Data["price"] = (double)price;
            return r;
        }

        // ---------------- Key Parts: the compound (0x5d687b) ----------------
        public static bool IsKeyItem(string id) => EventItems.Type(id) == "gacha_key";

        /// <summary>Using Key Parts: parts A-D of the car + the key -> the car's gift box, else gacha_compound_failure in the chat.</summary>
        public static void Compound(string key, Action<bool> done)
        {
            EventNet.Request("gacha.compound", new Dictionary<string, object> { ["item"] = key }, () => CompoundOffline(P, key), r =>
            {
                if (!r.Ok) { ChatBox.System(EventUi.T("gacha_compound_failure", "You have failed to combine a Gotcha vehicle due to insufficient parts.")); done?.Invoke(false); return; }
                EventUi.Sfx("SID_ITEM_DROP_GET1");
                EventNet.Log(r.Grants);
                var g = r.Grants.FirstOrDefault();
                EventPopup.Show(g?.ItemId, EventUi.T("11482", "Coin 'Gotcha!'"), EventUi.T("10096", "Obtained %d %s", 1, EventItems.Name(g?.ItemId)));
                done?.Invoke(true);
            });
        }

        public static EventReply CompoundOffline(PlayerProfile p, string key)
        {
            var row = EventItems.Row(key);
            var m = System.Text.RegularExpressions.Regex.Match(key ?? "", @"^i_n_([a-z]+)_k_v(\d)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (row == null || row.Type != "gacha_key" || !m.Success) return EventReply.Fail("That isn't a Key Parts item.");
            var parts = PartsFor(m.Groups[1].Value.ToLowerInvariant());
            if (p.Count(key) <= 0 || parts.Any(x => p.Count(x) <= 0)) return EventReply.Fail("insufficient parts");
            p.Give(key, -1);
            foreach (var x in parts) p.Give(x, -1);
            string box = !string.IsNullOrEmpty(row.Pool) ? row.Pool : $"i_n_{m.Groups[1].Value.ToLowerInvariant()}_box_v{m.Groups[2].Value}";
            var g = new List<RewardGrant> { new RewardGrant { Kind = GrantKind.Item, ItemId = box, Count = 1 } };
            EventNet.Apply(p, g);
            return new EventReply { Ok = true, Grants = g };
        }
    }
}
