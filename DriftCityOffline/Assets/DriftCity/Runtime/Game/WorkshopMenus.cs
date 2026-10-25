using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.Game
{
    /// <summary>
    /// Licences (L), the parts shop with enhancing / grinding / combining (U) and manufacturing, on the original
    /// NLICENSE, PARTSSHOP and MAKELIST / MAKEDETAIL dialogs. The item bag is the inventory's (OriginalMenus).
    /// </summary>
    public static class WorkshopMenus
    {
        static PlayerProfile P => PlayerProfile.Current;
        static UiManager Ui => UiManager.Instance;
        static Dictionary<string, object> D(params (string k, object v)[] kv) => kv.ToDictionary(x => x.k, x => x.v);
        static Progress.Result Ok => new Progress.Result { Ok = true };
        static Progress.Result Fail(string e) => new Progress.Result { Error = e };

        /// <summary>The client's icon name for a performance part: i_s / i_a / i_c / i_b (speed, accel, durability, booster),
        /// 000001..000003 for N / S / H and the gold H variant for Ultra.</summary>
        static string PartIcon(PerfPart p) => $"i_{"sacb"[p.Type]}_{(p.Grade >= 3 ? "000003_g" : (p.Grade + 1).ToString("000000"))}";

        // ================= licences: the NLICENSE grid (4710000) and its detail popup (4720000) =================
        const int LicRows = 8, LicCols = 13;
        static NuiView licView, licDetail;
        static Licence licShown;

        public static void Licences()
        {
            if (P == null) return;
            licView = OriginalMenus.OpenScreen(4710000, () => { if (!licDetail) return false; CloseLicence(); return true; });
            if (!licView) return;
            OriginalMenus.HideDefaultBacks(licView);
            licDetail = null;
            licShown = null;
            Ui.On("ID_NLICENSE_EXIT", _ => OriginalMenus.Close());
            Ui.On("ID_NLICENSE_POPUP_EXIT", _ => CloseLicence());
            Ui.On("ID_DIALOG_NLICENSE_POPUP_EQUIP_BTN", _ =>
            {
                var l = licShown;
                if (l == null) return;
                if (P.Licences.Contains(l.Id)) { if (P.MountedLicence != l.Id) MountLicence(l); }
                else if (Game.Licences.Met(P, l)) ClaimLicence(l);
            });
            // the money column and its heading price licences in the original; here they're earned
            for (int r = 1; r <= LicRows; r++) licView.SetVisible($"ID_DIALOG_NLICENSE_L{r}MONEY", false);
            OriginalMenus.Hide(licView, "ID_DIALOG_NLICENSE_FIELD1", "ID_DIALOG_NLICENSE_FIELD2", "ID_DIALOG_NLICENSE_ITEMIMAGE_NEW0", "ID_DIALOG_NLICENSE_ITEMIMAGE_NEW1");
            licView.SetText("ID_DIALOG_NLICENSE_TITLE", "Licences");
            for (int i = 0; i < LicRows * LicCols; i++)
            {
                int index = i;
                Ui.On(LicCell("BTN", i), _ => { var all = Game.Licences.All; if (index < all.Count) ShowLicence(all[index]); });
            }
            RefreshLicences();
        }

        // icons and buttons are named row_column, the row 1-based and the column 0-based (ICON1_0 .. ICON8_12)
        static string LicCell(string part, int i) => $"ID_DIALOG_NLICENSE_ITEMIMAGE_{part}{i / LicCols + 1}_{i % LicCols}";

        static void RefreshLicences()
        {
            if (!licView) return;
            var all = Game.Licences.All;
            var cur = P.MountedLicence >= 0 ? Game.Licences.Get(P.MountedLicence) : null;
            licView.SetText("ID_DIALOG_NLICENSE_ITEMMONEY", $"{P.Licences.Count} / {all.Count}");
            licView.SetText("ID_DIALOG_NLICENSE_EXPLAINTEXT",
                $"Licences are earned by meeting their conditions; claim one when it is ready. One licence is mounted at a time for its bonus.\nMounted: {cur?.Name ?? "none"}" +
                (cur != null && !string.IsNullOrEmpty(cur.EffectText) ? $"  ({cur.EffectText})" : ""));
            for (int i = 0; i < LicRows * LicCols; i++)
            {
                // the ICON/BTN controls sit where the client moves them at run time; the cell frames (BACK1_01 ..
                // BACK8_13) are where the grid really is, so the tiles and their clicks go there
                licView.SetVisible(LicCell("ICON", i), false);
                licView.SetVisible(LicCell("BTN", i), false);
                var back = licView.Control($"ID_DIALOG_NLICENSE_ITEMIMAGE_BACK{i / LicCols + 1}_{i % LicCols + 1:00}");
                if (!back) continue;
                var cell = (RectTransform)back.transform;
                if (i >= all.Count) { OriginalMenus.Remove(cell, "item"); continue; }
                var l = all[i];
                bool earned = P.Licences.Contains(l.Id), mounted = P.MountedLicence == l.Id, ready = !earned && Game.Licences.Met(P, l);
                var tile = OriginalMenus.ItemTile(cell, new Vector2(4, 4), new Vector2(48, 48), l.IdName, l.Name, null, 0,
                                                  earned || ready ? 1f : l.Obtainable ? 0.45f : 0.25f);
                OriginalMenus.Caption(tile, "state", mounted ? "ON" : ready ? "CLAIM" : "", "Calibri 11 700",
                                      mounted ? new Color(0.5f, 1f, 0.5f) : new Color(1f, 0.85f, 0.3f), TextAnchor.LowerCenter);
                var shown = l;
                OriginalMenus.Clickable(cell, () => ShowLicence(shown), () => licView.SetText("ID_DIALOG_NLICENSE_EXPLAINTEXT", $"{shown.Name}: {Clean(shown.Info)}"));
                LicenceInfo.AttachTooltip(cell, shown);
            }
            if (licDetail && licShown != null) ShowLicence(licShown);
        }

        static void ShowLicence(Licence l)
        {
            if (!licView) return;
            if (!licDetail) { licDetail = OriginalMenus.Open(4720000); OriginalMenus.HideDefaultBacks(licDetail); }
            if (!licDetail) return;
            licShown = l;
            var v = licDetail;
            bool earned = P.Licences.Contains(l.Id), mounted = P.MountedLicence == l.Id, met = Game.Licences.Met(P, l);
            var icon = v.Control("ID_DIALOG_NLICENSE_POPUP_ICON");
            if (icon) OriginalMenus.ItemTile(icon.transform, Vector2.zero, new Vector2(64, 64), l.IdName, l.Name);
            v.SetText("ID_DIALOG_NLICENSE_POPUP_ICONNAME", l.Name);
            v.SetText("ID_DIALOG_NLICENSE_POPUP_REWARDTIME", mounted ? "Mounted" : earned ? "Earned" : met ? "Ready to claim" : "Not earned");
            v.SetText("ID_DIALOG_NLICENSE_POPUP_EXPLAIN", Clean($"{l.Info}\n{l.Gain}"));
            v.SetText("ID_DIALOG_NLICENSE_POPUP_EFFECT", Clean(l.EffectText));
            // the conditions with their progress, in the popup's list box
            var terms = v.Control("ID_DIALOG_NLICENSE_POPUP_TERMS");
            if (terms)
            {
                foreach (var ri in terms.GetComponentsInChildren<RawImage>(true)) ri.enabled = false;
                string progress = !l.Obtainable ? "Needs a mode this version doesn't have."
                    : string.Join(l.AnyOne ? "\nor\n" : "\n", l.Needs.Select(n => $"{StatName(n.stat)}  {Mathf.Min(Game.Licences.Stat(P, n.stat), n.value):N0} / {n.value:N0}"));
                var text = OriginalMenus.Caption(terms.transform, "terms", progress, "Calibri 12 400", Color.white, TextAnchor.UpperLeft, wrap: true);
                text.rectTransform.offsetMin = new Vector2(8, 4);
                text.rectTransform.offsetMax = new Vector2(-8, -6);
            }
            v.SetEnabled("ID_DIALOG_NLICENSE_POPUP_EQUIP_BTN", earned ? !mounted : met);
        }

        static void CloseLicence()
        {
            OriginalMenus.CloseView(licDetail);
            licDetail = null;
            licShown = null;
        }

        /// <summary>Table text without the client's colour / line-break tags.</summary>
        static string Clean(string s) => string.IsNullOrEmpty(s) ? "" : Regex.Replace(Regex.Replace(s, "<BR>", "\n", RegexOptions.IgnoreCase), "<[^>]*>", "").Trim();

        static string StatName(string stat) => stat.StartsWith("Licence_") ? "Licence " + (Game.Licences.Get(int.Parse(stat.Substring(8)))?.Name ?? stat) : stat switch
        {
            "HuvCaught" => "HUVs caught", "RacesEntered" => "Races", "RacesWon" => "Wins", "Quests" => "Main quests", "RacePoints" => "Race points",
            "RacesCras" => "Cras races", "RacesMoon" => "Moon Palace races", "RacesKoi" => "Koinonia races", "RacesOros" => "Oros races",
            "MaxEnhance" => "Best part enhance", _ => stat,
        };

        static void ClaimLicence(Licence l)
        {
            var needs = l.Needs.Select(n => (object)new Dictionary<string, object> { ["stat"] = n.stat, ["value"] = n.value }).ToList();
            Progress.Op("licence.earn", D(("id", l.Id), ("needs", needs), ("any", l.AnyOne)), () =>
            {
                if (!Game.Licences.Met(P, l)) return Fail("Conditions not met yet.");
                if (!P.Licences.Contains(l.Id)) P.Licences.Add(l.Id);
                return Ok;
            }, r => { if (r.Ok) { AudioLibrary.PlaySfx("sfx/SID_QUEST_SUCCESS"); UiPopup.Message($"Licence earned: {l.Name}\n{l.EffectText}"); } RefreshLicences(); });
        }

        static void MountLicence(Licence l)
        {
            Progress.Op("licence.mount", D(("id", l.Id), ("effect", l.Effect ?? ""), ("value", l.Value)), () =>
            {
                if (!P.Licences.Contains(l.Id)) return Fail("Not earned.");
                P.MountedLicence = l.Id;
                return Ok;
            }, _ => RefreshLicences());
        }

        // ================= parts shop: PARTSSHOP frame (1200000), a tab (buy / upgrade / combine / modify) and the parts box (1280000) =================
        static readonly string[] ShopTabs = { "BUY", "UPGRADE", "COMBI", "MODIFY" };
        static readonly int[] ShopTabDialogs = { 1250000, 1240000, 1260000, 1270000 };
        static readonly string[] Enhancers = { "i_n_00003", "i_n_00003_plus", "i_n_00003_deluxe" };
        const int BoxSlots = 20, BuySlots = 25;
        static NuiView shopFrame, shopTab, shopBox;
        static int shopTabIndex, boxPage, boxSort, enhancer;
        static int selectedUid = -1;
        static bool seedPlus;

        public static void PartsShop()
        {
            if (P?.Car == null) return;
            shopFrame = OriginalMenus.OpenScreen(1200000);
            if (!shopFrame) return;
            shopBox = OriginalMenus.Open(1280000);
            shopTab = null;
            Ui.On("ID_PARTSSHOP_EXIT", _ => OriginalMenus.Close());
            for (int i = 0; i < ShopTabs.Length; i++) { int t = i; Ui.On($"ID_PARTSSHOP_TAB_{ShopTabs[i]}IMG", _ => ShopTab(t)); }
            OriginalMenus.Hide(shopFrame, "ID_PARTSSHOP_TABKEY_BUY", "ID_PARTSSHOP_TABKEY_UPGRADE", "ID_PARTSSHOP_TABKEY_COMBI");   // hotkey overlays
            shopFrame.SetText("ID_PARTSSHOP_NAME", "Parts shop");
            OriginalMenus.Hide(shopBox, "ID_PARTSSHOP_INVEN_COVER", "ID_PARTSSHOP_INVEN_CURPAGE_RED", "ID_PARTSSHOP_INVEN_MAXPAGE_RED");
            Ui.On("ID_PARTSSHOP_INVEN_PREVPAGE", _ => { boxPage--; RefreshShop(); });
            Ui.On("ID_PARTSSHOP_INVEN_NEXTPAGE", _ => { boxPage++; RefreshShop(); });
            Ui.On("ID_PARTSSHOP_INVEN_SORT_TYPE", _ => { boxSort = 0; RefreshShop(); });
            Ui.On("ID_PARTSSHOP_INVEN_SORT_DATE", _ => { boxSort = 1; RefreshShop(); });
            Ui.On("ID_PARTSSHOP_INVEN_SORT_MITO", _ => { boxSort = 2; RefreshShop(); });
            if (selectedUid >= 0 && !P.PerfParts.Exists(x => x.Uid == selectedUid)) selectedUid = -1;
            ShopTab(0);
        }

        static void ShopTab(int t)
        {
            if (!shopFrame) return;
            shopTabIndex = t;
            for (int i = 0; i < ShopTabs.Length; i++) OriginalMenus.Pressed(shopFrame.Control($"ID_PARTSSHOP_TAB_{ShopTabs[i]}IMG"), i == t);
            OriginalMenus.CloseView(shopTab);
            shopTab = OriginalMenus.Open(ShopTabDialogs[t]);
            var v = shopTab;
            if (!v) return;
            switch (t)
            {
                case 0:
                    Ui.On("ID_PARTSSHOP_BUY_PREVPAGE", _ => { });
                    Ui.On("ID_PARTSSHOP_BUY_NEXTPAGE", _ => { });
                    break;
                case 1:
                    Ui.On("ID_PARTSSHOP_UPGRADE_BUTTON", _ => { var p = Selected(); if (p != null) Enhance(p, Enhancers[enhancer], PerfRules.EnhanceFee(p, EnhanceDiscount)); });
                    break;
                case 2:
                    // the "stone" set lies over the parts set: one combine here, with a seed part
                    OriginalMenus.Hide(v, "ID_PARTSSHOP_COMBI_STONE1ST", "ID_PARTSSHOP_COMBI_STONE2ND", "ID_PARTSSHOP_COMBI_STONE3RD", "ID_PARTSSHOP_COMBI_STONE4TH",
                                       "ID_PARTSSHOP_COMBI_STONE", "ID_PARTSSHOP_COMBI_VIEW_BTN");
                    Ui.On("ID_PARTSSHOP_COMBI_PARTS", _ => { var p = Selected(); if (p != null) Combine(p, Mate(p), PerfRules.Seed(p.Grade, seedPlus)); });
                    break;
                case 3:
                    // modifying grinds the part into Parts Dust here; the stat-reroll panels and guards stay hidden
                    OriginalMenus.Hide(v, "ID_PARTSSHOP_MODIFY_SPD", "ID_PARTSSHOP_MODIFY_ACC", "ID_PARTSSHOP_MODIFY_COL", "ID_PARTSSHOP_MODIFY_BOOSTER", "ID_PARTSSHOP_MODIFY_GUARD",
                                       "ID_PARTSSHOP_MODIFY_GUARD_LIGHT1", "ID_PARTSSHOP_MODIFY_GUARD_LIGHT2", "ID_PARTSSHOP_MODIFY_GUARD_LIGHT3", "ID_PARTSSHOP_MODIFY_GUARD_LIGHT4",
                                       "ID_PARTSSHOP_MODIFY_SLOT_LIGHT1", "ID_PARTSSHOP_MODIFY_SLOT_LIGHT2", "ID_PARTSSHOP_MODIFY_SLOT_LIGHT3", "ID_PARTSSHOP_MODIFY_SLOT_LIGHT4",
                                       "ID_PARTSSHOP_MODIFY_GUARD_TOOLTIP");
                    Ui.On("ID_PARTSSHOP_MODIFY_MODIFY", _ =>
                    {
                        var p = Selected();
                        if (p != null) UiPopup.Show($"Grind {PerfRules.Name(p)} into {1 + p.Enhance}x {Crafting.Name(PerfRules.Dust(p.Grade))}?", id => { if (id == UiPopup.Yes) Grind(p); }, UiPopup.Yes, UiPopup.No);
                    });
                    break;
            }
            Say(TabHint());
            RefreshShop();
        }

        static int EnhanceDiscount => Game.Licences.Bonus(P, "ME_EnhanceFeeDis", "ME_FeeDis");

        static PerfPart Selected() => selectedUid >= 0 ? P.PerfParts.Find(x => x.Uid == selectedUid) : null;

        static bool FittedAnywhere(PerfPart p) => P.Cars.Exists(c => c.PerfSlots != null && c.PerfSlots.Contains(p.Uid));

        /// <summary>The second part a combine uses: an unfitted one of the same type and grade.</summary>
        static PerfPart Mate(PerfPart part) => part == null ? null
            : P.PerfParts.FirstOrDefault(x => x.Uid != part.Uid && x.Type == part.Type && x.Grade == part.Grade && !FittedAnywhere(x));

        /// <summary>The NPC's talk box doubles as the shop's info line.</summary>
        static void Say(string text) { if (shopFrame) shopFrame.SetText("ID_PARTSSHOP_TEXT", text); }

        static string TabHint()
        {
            var p = Selected();
            switch (shopTabIndex)
            {
                case 0: return "Buy performance parts and supplies. Click a part in your box to fit it on your current car or take it off.";
                case 1:
                    if (p == null) return "Pick a part from your box to enhance it with a Mittron Enhancer. Click the enhancer to switch kinds.";
                    return p.Enhance >= PerfRules.MaxEnhance ? $"{PerfRules.Name(p)} is fully enhanced."
                        : $"{PerfRules.Name(p)}: {PerfRules.EnhanceChance(p.Enhance, Enhancers[enhancer])}% success with {Crafting.Name(Enhancers[enhancer])} ({P.Count(Enhancers[enhancer])} left)" +
                          (p.Enhance >= 5 ? ". Failing drops a level." : ".");
                case 2:
                    if (p == null) return "Pick a part from your box: two unfitted parts of the same type and grade and a Seed Part make the next grade. Click the seed to switch to PLUS.";
                    if (p.Grade >= 3) return "Ultra parts can't be combined further.";
                    if (FittedAnywhere(p)) return "Take this part off its car first.";
                    var mate = Mate(p);
                    return mate == null ? "Needs a second unfitted part of the same type and grade."
                        : $"With {PerfRules.Name(mate)}: {PerfRules.CombineChance(seedPlus)}% for a {PerfRules.GradeNames[p.Grade + 1]} part; failing loses the weaker part.";
                default:
                    return p == null ? "Pick a part from your box to grind it into Parts Dust for manufacturing."
                        : $"Grinding {PerfRules.Name(p)} gives {1 + p.Enhance}x {Crafting.Name(PerfRules.Dust(p.Grade))}.";
            }
        }

        static List<PerfPart> BoxParts() => (boxSort == 1 ? P.PerfParts.OrderBy(x => x.Uid)
            : boxSort == 2 ? P.PerfParts.OrderByDescending(x => PerfRules.Points(x)).ThenBy(x => x.Type)
            : P.PerfParts.OrderBy(x => x.Type).ThenByDescending(x => x.Grade).ThenByDescending(x => x.Enhance)).ToList();

        static void RefreshShop()
        {
            if (!shopFrame || P == null) return;
            // the parts box
            var parts = BoxParts();
            int pages = Mathf.Max(1, (parts.Count + BoxSlots - 1) / BoxSlots);
            boxPage = Mathf.Clamp(boxPage, 0, pages - 1);
            shopBox.SetText("ID_PARTSSHOP_INVEN_CURPAGE", (boxPage + 1).ToString());
            shopBox.SetText("ID_PARTSSHOP_INVEN_MAXPAGE", pages.ToString());
            shopBox.SetEnabled("ID_PARTSSHOP_INVEN_PREVPAGE", boxPage > 0);
            shopBox.SetEnabled("ID_PARTSSHOP_INVEN_NEXTPAGE", boxPage < pages - 1);
            shopBox.SetText("ID_PARTSSHOP_INVEN_MITO", $"{P.Mito:N0}");
            for (int i = 0; i < BoxSlots; i++)
            {
                var slot = shopBox.Control($"ID_PARTSSHOP_INVEN_SLOT{i + 1}");
                string badge = $"ID_PARTSSHOP_INVEN_UPGRADE{i + 1:00}";
                int index = boxPage * BoxSlots + i;
                var part = index < parts.Count ? parts[index] : null;
                shopBox.SetVisible(badge, part != null && part.Enhance > 0);
                if (!slot) continue;
                OriginalMenus.Remove(slot.transform, "item");
                OriginalMenus.Remove(slot.transform, "selected");
                if (part == null) continue;
                var pp = part;
                if (part.Enhance > 0) shopBox.SetText(badge, $"+{part.Enhance}");
                if (part.Uid == selectedUid) Highlight(slot.transform);
                var tile = OriginalMenus.ItemTile(slot.transform, new Vector2(4, 4), new Vector2(64, 64), PartIcon(part), PerfRules.Name(part));
                int onCar = P.Cars.FindIndex(c => c.PerfSlots != null && c.PerfSlots.Contains(part.Uid));
                OriginalMenus.Caption(tile, "where", onCar < 0 ? "" : onCar == P.CurrentCar ? "ON" : "ON *", "Calibri 11 700", new Color(0.5f, 1f, 0.5f), TextAnchor.LowerLeft);
                OriginalMenus.Clickable(tile, () => PickPart(pp), () => Say(PartText(pp)));
            }

            var v = shopTab;
            if (!v) return;
            var sel = Selected();
            switch (shopTabIndex)
            {
                case 0: RefreshBuy(v); break;
                case 1:
                {
                    int fee = sel != null ? PerfRules.EnhanceFee(sel, EnhanceDiscount) : 0;
                    string e = Enhancers[enhancer];
                    PartSlotTile(v, "ID_PARTSSHOP_UPGRADE_SLOT1", "ID_PARTSSHOP_UPGRADE_UPGRADE1", sel);
                    var es = v.Control("ID_PARTSSHOP_UPGRADE_SLOT2");
                    if (es)
                    {
                        var tile = OriginalMenus.ItemTile(es.transform, new Vector2(10, 10), new Vector2(64, 64), e, Crafting.Name(e), null, P.Count(e), P.Count(e) > 0 ? 1f : 0.45f);
                        OriginalMenus.Clickable(tile, () => { enhancer = (enhancer + 1) % Enhancers.Length; Say(TabHint()); RefreshShop(); }, () => Say($"{Crafting.Name(e)}: you have {P.Count(e)}. Click to switch enhancers."));
                    }
                    v.SetVisible("ID_PARTSSHOP_UPGRADE_UPGRADE2", false);
                    bool maxed = sel != null && sel.Enhance >= PerfRules.MaxEnhance;
                    v.SetText("ID_PARTSSHOP_UPGRADE_ORIGINAL", sel == null ? "" : $"{PerfRules.Name(sel)}\n{PerfRules.Types[sel.Type]} +{PerfRules.Points(sel)}");
                    var next = sel == null ? null : new PerfPart { Type = sel.Type, Grade = sel.Grade, Enhance = sel.Enhance + 1 };
                    v.SetText("ID_PARTSSHOP_UPGRADE_UPGRADE", sel == null ? "" : maxed ? "Fully enhanced"
                        : $"{PerfRules.Name(next)}\n{PerfRules.Types[sel.Type]} +{PerfRules.Points(next)}\n{PerfRules.EnhanceChance(sel.Enhance, e)}% success" + (sel.Enhance >= 5 ? "\nFailing drops a level" : ""));
                    v.SetText("ID_PARTSSHOP_UPGRADE_MITO", sel == null || maxed ? "0" : $"{fee:N0}");
                    v.SetEnabled("ID_PARTSSHOP_UPGRADE_BUTTON", sel != null && !maxed && P.Count(e) > 0 && P.Mito >= fee);
                    break;
                }
                case 2:
                {
                    var mate = Mate(sel);
                    string seed = sel != null ? PerfRules.Seed(Mathf.Min(sel.Grade, 2), seedPlus) : PerfRules.Seed(0, seedPlus);
                    PartSlotTile(v, "ID_PARTSSHOP_COMBI_PARTS1ST", "ID_PARTSSHOP_COMBI_UPGRADE1ST", sel);
                    PartSlotTile(v, "ID_PARTSSHOP_COMBI_PARTS2ND", "ID_PARTSSHOP_COMBI_UPGRADE2ND", mate);
                    var ss = v.Control("ID_PARTSSHOP_COMBI_PARTS3RD");
                    if (ss)
                    {
                        var tile = OriginalMenus.ItemTile(ss.transform, new Vector2(10, 10), new Vector2(64, 64), seed, Crafting.Name(seed), null, P.Count(seed), P.Count(seed) > 0 ? 1f : 0.45f);
                        OriginalMenus.Clickable(tile, () => { seedPlus = !seedPlus; Say(TabHint()); RefreshShop(); }, () => Say($"{Crafting.Name(seed)}: you have {P.Count(seed)}. Click to switch seed parts."));
                    }
                    v.SetVisible("ID_PARTSSHOP_COMBI_UPGRADE3RD", false);
                    var rs = v.Control("ID_PARTSSHOP_COMBI_PARTS4TH");
                    if (rs)
                    {
                        if (sel != null && sel.Grade < 3)
                        {
                            var result = new PerfPart { Type = sel.Type, Grade = sel.Grade + 1 };
                            OriginalMenus.ItemTile(rs.transform, new Vector2(10, 10), new Vector2(64, 64), PartIcon(result), PerfRules.Name(result), null, 0, mate != null ? 1f : 0.45f);
                        }
                        else OriginalMenus.Remove(rs.transform, "item");
                    }
                    v.SetText("ID_PARTSSHOP_COMBI_MITO", "0");
                    v.SetEnabled("ID_PARTSSHOP_COMBI_PARTS", sel != null && sel.Grade < 3 && mate != null && P.Count(seed) > 0 && !FittedAnywhere(sel));
                    break;
                }
                case 3:
                {
                    PartSlotTile(v, "ID_PARTSSHOP_MODIFY_MODIFY1ST", "ID_PARTSSHOP_MODIFY_UPGRADE01", sel);
                    var ds = v.Control("ID_PARTSSHOP_MODIFY_MODIFY2ND");
                    if (ds)
                    {
                        if (sel != null)
                        {
                            string dust = PerfRules.Dust(sel.Grade);
                            OriginalMenus.ItemTile(ds.transform, new Vector2(10, 10), new Vector2(64, 64), dust, Crafting.Name(dust), null, 1 + sel.Enhance);
                        }
                        else OriginalMenus.Remove(ds.transform, "item");
                    }
                    v.SetVisible("ID_PARTSSHOP_MODIFY_UPGRADE02", false);
                    v.SetText("ID_PARTSSHOP_MODIFY_MITO", "0");
                    v.SetEnabled("ID_PARTSSHOP_MODIFY_MODIFY", sel != null);
                    break;
                }
            }
        }

        /// <summary>A part in one of the tab's 84px slots, with its +level badge.</summary>
        static void PartSlotTile(NuiView v, string slotId, string badgeId, PerfPart part)
        {
            var slot = v.Control(slotId);
            v.SetVisible(badgeId, part != null && part.Enhance > 0);
            if (part != null && part.Enhance > 0) v.SetText(badgeId, $"+{part.Enhance}");
            if (!slot) return;
            if (part == null) { OriginalMenus.Remove(slot.transform, "item"); return; }
            var pp = part;
            var tile = OriginalMenus.ItemTile(slot.transform, new Vector2(10, 10), new Vector2(64, 64), PartIcon(part), PerfRules.Name(part));
            OriginalMenus.Clickable(tile, null, () => Say(PartText(pp)));
        }

        static void Highlight(Transform slot)
        {
            var rt = NuiView.NewRect("selected", slot);
            NuiView.Stretch(rt);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = new Color(1f, 0.8f, 0.25f, 0.35f);
            img.raycastTarget = false;
        }

        static string PartText(PerfPart p)
        {
            int onCar = P.Cars.FindIndex(c => c.PerfSlots != null && c.PerfSlots.Contains(p.Uid));
            string where = onCar < 0 ? "Not fitted." : onCar == P.CurrentCar ? "Fitted on your current car." : $"Fitted on your {GameTables.Car(P.Cars[onCar].VehicleIndex).UiName}.";
            return $"{PerfRules.Name(p)}: {PerfRules.Types[p.Type]} +{PerfRules.Points(p)}. {where}";
        }

        /// <summary>A part in the box clicked: the buy tab fits / takes it off, the other tabs work on it.</summary>
        static void PickPart(PerfPart part)
        {
            if (shopTabIndex != 0)
            {
                selectedUid = part.Uid;
                Say(TabHint());
                RefreshShop();
                return;
            }
            int slot = P.CurrentCar;
            bool fitted = P.Car.PerfSlots[part.Type] == part.Uid;
            UiPopup.Show(fitted ? $"Take {PerfRules.Name(part)} off your car?" : $"Fit {PerfRules.Name(part)} on your car?\nIt replaces the car's {PerfRules.Types[part.Type]} part.", id =>
            {
                if (id != UiPopup.Yes) return;
                Progress.Op("perf.fit", D(("slot", slot), ("type", part.Type), ("uid", fitted ? -1 : part.Uid)), () =>
                {
                    foreach (var c in P.Cars) for (int t = 0; t < 4; t++) if (c.PerfSlots[t] == part.Uid) c.PerfSlots[t] = -1;
                    if (!fitted) P.Cars[slot].PerfSlots[part.Type] = part.Uid;
                    return Ok;
                }, r => { GameFlow.Instance?.ApplyCarStats(); RefreshShop(); });
            }, UiPopup.Yes, UiPopup.No);
        }

        // ---------- buy tab: the twelve performance parts, then the counter's supplies ----------
        static void RefreshBuy(NuiView v)
        {
            v.SetText("ID_PARTSSHOP_BUY_MITO", $"{P.Mito:N0}");
            v.SetText("ID_PARTSSHOP_BUY_CURRPAGE", "1");
            v.SetText("ID_PARTSSHOP_BUY_MAXPAGE", "1");
            v.SetEnabled("ID_PARTSSHOP_BUY_PREVPAGE", false);
            v.SetEnabled("ID_PARTSSHOP_BUY_NEXTPAGE", false);
            int n = 0;
            for (int g = 0; g < 3; g++)
                for (int t = 0; t < 4; t++)
                {
                    int type = t, grade = g, price = PerfRules.ShopPrice[g];
                    var sample = new PerfPart { Type = t, Grade = g };
                    bool can = P.Level >= PerfRules.ReqLevel[g];
                    string text = $"{PerfRules.Name(sample)}: +{PerfRules.Points(sample)} {PerfRules.Types[t]}, Lv {PerfRules.ReqLevel[g]}, {price:N0} Mito. Parts fit any of your cars, one per stat.";
                    BuySlot(v, ++n, PartIcon(sample), PerfRules.Name(sample), can ? 1f : 0.35f, () => { if (can) BuyPerf(type, grade); else UiPopup.Message($"Needs level {PerfRules.ReqLevel[grade]}."); }, text);
                }
            foreach (var (id, price) in PerfRules.Counter)
            {
                string item = id; int cost = price;
                BuySlot(v, ++n, id, Crafting.Name(id), 1f, () => BuyCounter(item, cost), $"{Crafting.Name(id)}: {price:N0} Mito (you have {P.Count(id)}).");
            }
            for (++n; n <= BuySlots; n++)
            {
                var empty = v.Control($"ID_PARTSSHOP_BUY_SLOT{n}");
                if (empty) OriginalMenus.Remove(empty.transform, "item");
            }
        }

        static void BuySlot(NuiView v, int n, string icon, string name, float alpha, System.Action click, string text)
        {
            var slot = v.Control($"ID_PARTSSHOP_BUY_SLOT{n}");
            if (!slot) return;
            var tile = OriginalMenus.ItemTile(slot.transform, new Vector2(4, 4), new Vector2(64, 64), icon, name, null, 0, alpha);
            OriginalMenus.Clickable(tile, click, () => Say(text));
        }

        static void BuyPerf(int type, int grade)
        {
            int price = PerfRules.ShopPrice[grade];
            var sample = new PerfPart { Type = type, Grade = grade };
            UiPopup.Show($"Buy a {PerfRules.Name(sample)} for {price:N0} Mito?", id =>
            {
                if (id != UiPopup.Yes) return;
                Progress.Op("perf.buy", D(("type", type), ("grade", grade)), () =>
                {
                    if (P.Level < PerfRules.ReqLevel[grade]) return Fail($"Needs level {PerfRules.ReqLevel[grade]}.");
                    if (P.Mito < price) return Fail($"You need {price - P.Mito:N0} more Mito.");
                    if (P.PerfParts.Count >= PerfRules.MaxParts) return Fail("Your parts box is full.");
                    P.Mito -= price;
                    P.PerfParts.Add(new PerfPart { Uid = P.NextPartUid++, Type = type, Grade = grade });
                    return Ok;
                }, r => RefreshShop());
            }, UiPopup.Yes, UiPopup.No);
        }

        static void BuyCounter(string item, int cost)
        {
            UiPopup.Show($"Buy {Crafting.Name(item)} for {cost:N0} Mito?", id =>
            {
                if (id != UiPopup.Yes) return;
                Progress.Op("item.buy", D(("id", item), ("count", 1)), () =>
                {
                    if (P.Mito < cost) return Fail($"You need {cost - P.Mito:N0} more Mito.");
                    P.Mito -= cost; P.Give(item);
                    return Ok;
                }, r => RefreshShop());
            }, UiPopup.Yes, UiPopup.No);
        }

        static void Enhance(PerfPart part, string enhancer, int fee)
        {
            int uid = part.Uid;
            Progress.Op("perf.enhance", D(("uid", uid), ("enhancer", enhancer)), () =>
            {
                part = P.PerfParts.Find(x => x.Uid == uid);
                if (part == null) return Fail("That part is gone.");
                if (P.Count(enhancer) <= 0) return Fail("No enhancer.");
                if (P.Mito < fee) return Fail($"You need {fee - P.Mito:N0} more Mito.");
                P.Give(enhancer, -1); P.Mito -= fee;
                bool ok = Random.Range(0, 100) < PerfRules.EnhanceChance(part.Enhance, enhancer);
                if (ok) { part.Enhance++; P.MaxEnhance = Mathf.Max(P.MaxEnhance, part.Enhance); }
                else if (part.Enhance >= 5) part.Enhance--;
                return new Progress.Result { Ok = true, Error = ok ? null : "failed" };
            }, r =>
            {
                var now = P.PerfParts.Find(x => x.Uid == uid);
                bool success = r.Ok && r.Error == null;
                // 0x5c8fd0: ui_upgrade_s / ui_upgrade_f with their sounds
                if (r.Ok) GameEffects.Upgrade(success ? GameEffects.UpgradeSuccess : GameEffects.UpgradeFail);
                else AudioLibrary.PlaySfx("sfx/SID_UPGRADE_FAIL");
                GameFlow.Instance?.ApplyCarStats();
                RefreshShop();
                Say(TabHint());
                UiPopup.Message(success ? $"Enhanced! {PerfRules.Name(now)}" : $"Enhancement failed. {(now != null ? PerfRules.Name(now) : "")}");
            });
        }

        static void Grind(PerfPart part)
        {
            Progress.Op("perf.grind", D(("uid", part.Uid)), () =>
            {
                part = P.PerfParts.Find(x => x.Uid == part.Uid);
                if (part == null) return Fail("That part is gone.");
                foreach (var c in P.Cars) for (int t = 0; t < 4; t++) if (c.PerfSlots[t] == part.Uid) c.PerfSlots[t] = -1;
                P.PerfParts.Remove(part);
                P.Give(PerfRules.Dust(part.Grade), 1 + part.Enhance);
                return Ok;
            }, r =>
            {
                GameFlow.Instance?.ApplyCarStats();
                if (!P.PerfParts.Exists(x => x.Uid == selectedUid)) selectedUid = -1;
                RefreshShop();
                Say(TabHint());
            });
        }

        static void Combine(PerfPart a, PerfPart b, string seed)
        {
            if (b == null) return;
            bool plus = seed.Contains("plus");
            Progress.Op("perf.combine", D(("a", a.Uid), ("b", b.Uid), ("seed", seed)), () =>
            {
                a = P.PerfParts.Find(x => x.Uid == a.Uid); b = b == null ? null : P.PerfParts.Find(x => x.Uid == b.Uid);
                if (a == null || b == null) return Fail("Those parts are gone.");
                if (P.Cars.Exists(c => c.PerfSlots.Contains(a.Uid) || c.PerfSlots.Contains(b.Uid))) return Fail("Take the parts off first.");
                if (P.Count(seed) <= 0) return Fail("No seed part.");
                P.Give(seed, -1);
                bool ok = Random.Range(0, 100) < PerfRules.CombineChance(plus);
                var weaker = a.Enhance <= b.Enhance ? a : b;
                if (ok)
                {
                    P.PerfParts.Remove(a); P.PerfParts.Remove(b);
                    P.PerfParts.Add(new PerfPart { Uid = P.NextPartUid++, Type = a.Type, Grade = a.Grade + 1 });
                }
                else P.PerfParts.Remove(weaker);
                return new Progress.Result { Ok = true, Error = ok ? null : "failed" };
            }, r =>
            {
                bool success = r.Ok && r.Error == null;
                // 0x4f53a0: transform_success for the converted part; a failed combine loses a part: ui_upgrade_d
                if (success) GameEffects.Converted();
                else if (r.Ok) GameEffects.Upgrade(GameEffects.UpgradeDestroyed);
                else AudioLibrary.PlaySfx("sfx/SID_UPGRADE_DESTROY");
                if (!P.PerfParts.Exists(x => x.Uid == selectedUid)) selectedUid = -1;
                RefreshShop();
                Say(TabHint());
                UiPopup.Message(success ? $"Combined into a {PerfRules.GradeNames[Mathf.Min(3, a.Grade + 1)]} {PerfRules.Types[a.Type]} part!" : "Combining failed; the weaker part was lost.");
            });
        }

        // ================= manufacturing: MAKELIST (4920000), MAKEDETAIL (4930000) and its result (4940000) =================
        // the list's rows are the skill levels (the client's 1, 10, 20 ... headings); this rebuild's recipes need levels 1-4
        static readonly string[] MakeRows = { "1", "10", "20", "30", "40", "50", "60", "70" };
        static readonly string[] MakeRanks = { "Low", "Mid", "High", "Highest" };
        const int MakeCols = 13;
        const string EmptySlotIcon = "124,337~140,356_ui_contents73.dds";
        static NuiView makeList, makeDetail, makeResult;
        static Recipe makeShown;

        public static void Manufacturing()
        {
            if (P == null) return;
            makeList = OriginalMenus.OpenScreen(4920000, ManufacturingBack);
            if (!makeList) return;
            OriginalMenus.HideDefaultBacks(makeList);
            makeDetail = makeResult = null;
            makeShown = null;
            Ui.On("ID_MAKELIST_CLOSE", _ => OriginalMenus.Close());
            Ui.On("ID_MAKEDETAIL_CLOSE", _ => CloseRecipe());
            Ui.On("ID_MAKEDETAIL_MAKE", _ => MakeOrLearn());
            Ui.On("ID_MAKEDETAIL_BTN_CLOSE", _ => { OriginalMenus.CloseView(makeResult); makeResult = null; });
            OriginalMenus.Hide(makeList, "ID_MAKELIST_ICON_NEW", "ID_MAKELIST_ICON_NEW2", "ID_MAKELIST_MASK1", "ID_MAKELIST_MASK2", "ID_MAKELIST_EXP_PERCENT");
            // every cell's placeholder, named or not: the recipes' own icons go in their place
            foreach (var c in makeList.GetComponentsInChildren<NuiControlView>(true))
                if (c.Data.I("I1_1") == EmptySlotIcon) c.gameObject.SetActive(false);
            makeList.SetText("ID_MAKELIST_TXT_SKILLLEVEL", "Manufacturing");
            makeList.SetText("ID_MAKELIST_TXT_POSSIBLE_LEVEL", "Skill level");
            makeList.SetText("ID_MAKELIST_TXT_ITEM_LIST", "Skill books");
            for (int r = 0; r < MakeRows.Length; r++)
            {
                var label = makeList.Control($"ID_MAKELIST_TXT_LEVEL_{MakeRows[r]}");
                makeList.SetVisible($"ID_MAKELIST_TXT_LEVEL_{MakeRows[r]}_NAME", false);
                if (!label) continue;
                label.gameObject.SetActive(r < MakeRanks.Length);
                if (r >= MakeRanks.Length) continue;
                label.SetText($"Lv {r + 1}");
                var rank = OriginalMenus.Caption(label.transform, "rank", MakeRanks[r], "Calibri 12 400", Color.white, TextAnchor.MiddleCenter);
                rank.rectTransform.offsetMin = new Vector2(0, -23);
                rank.rectTransform.offsetMax = new Vector2(0, -23);
            }
            RefreshManufacturing();
        }

        static bool ManufacturingBack()
        {
            if (makeResult) { OriginalMenus.CloseView(makeResult); makeResult = null; return true; }
            if (makeDetail) { CloseRecipe(); return true; }
            return false;
        }

        static int RecipeRow(Recipe rc) => Mathf.Clamp(rc.ReqLevel - 1, 0, MakeRows.Length - 1);

        static bool CanMake(Recipe rc) => rc.Inputs.All(i => P.Count(i.id) >= i.count) && P.Count(rc.Kit) > 0;

        static void RefreshManufacturing()
        {
            if (!makeList) return;
            int level = Crafting.Level(P.CraftExp);
            var (lo, hi) = Crafting.LevelSpan(P.CraftExp);
            makeList.SetText("ID_MAKELIST_LEVEL", level.ToString());
            makeList.SetText("ID_MAKELIST_EXP", hi > lo ? $"{P.CraftExp:N0} / {hi:N0}" : $"{P.CraftExp:N0}  MAX");
            OriginalMenus.Gauge(makeList.Control("ID_MAKELIST_LEVEL_GAGE"), hi > lo ? (float)(P.CraftExp - lo) / (hi - lo) : 1f);
            makeList.SetText("ID_MAKELIST_TXT_BOTTOM_TEXT", "Learn a skill book, then make its item from the materials and a manufacturing kit. " +
                                                            $"Materials drop from HUV chases; dust comes from grinding parts.  Mito {P.Mito:N0}");
            var rows = Crafting.Recipes.GroupBy(RecipeRow).ToDictionary(g => g.Key, g => g.OrderBy(x => x.ReqLevel).ThenBy(x => x.Name).ToList());
            for (int r = 0; r < MakeRows.Length; r++)
            {
                var anchor = makeList.Control($"ID_MAKELIST_{MakeRows[r]}S_ICON_SLOT");
                if (!anchor) continue;
                rows.TryGetValue(r, out var recipes);
                for (int c = 0; c < MakeCols; c++)
                {
                    var rc = recipes != null && c < recipes.Count ? recipes[c] : null;
                    var cellName = $"recipe {r} {c}";
                    if (rc == null) { OriginalMenus.Remove(anchor.transform.parent, cellName); continue; }
                    bool learned = P.SkillBooks.Contains(rc.Id);
                    var cell = OriginalMenus.Cell(anchor, cellName, new Vector2(61 * c, 0), new Vector2(60, 61));
                    OriginalMenus.ItemTile(cell, new Vector2(6, 6), new Vector2(48, 48), rc.Result, Crafting.Name(rc.Result), null, 0,
                                           !learned ? (level >= rc.ReqLevel ? 0.45f : 0.2f) : 1f);
                    var rr = rc;
                    OriginalMenus.Clickable(cell, () => ShowRecipe(rr), () => makeList.SetText("ID_MAKELIST_TXT_BOTTOM_TEXT", RecipeLine(rr)));
                }
            }
            if (makeDetail && makeShown != null) ShowRecipe(makeShown);
        }

        static string RecipeLine(Recipe rc)
        {
            bool learned = P.SkillBooks.Contains(rc.Id);
            string state = learned ? (CanMake(rc) ? "ready to make" : "missing materials") : $"skill book {PerfRules.SkillBookPrice(rc.Group):N0} Mito";
            return $"{rc.Name}: makes {Crafting.Name(rc.Result)} x{rc.ResultCount}, {rc.Chance}% success, Manufacturing Lv {rc.ReqLevel} ({state}).";
        }

        static void ShowRecipe(Recipe rc)
        {
            if (!makeList) return;
            if (!makeDetail)
            {
                makeDetail = OriginalMenus.Open(4930000);
                OriginalMenus.HideDefaultBacks(makeDetail);
                // a zero-sized copy of the fifth "have" count would print its placeholder digits in a column
                if (makeDetail)
                    foreach (var c in makeDetail.GetComponentsInChildren<NuiControlView>(true))
                        if (string.IsNullOrEmpty(c.Data.IdName) && c.Data.I("SZ1") == "0 0") c.gameObject.SetActive(false);
            }
            var v = makeDetail;
            if (!v) return;
            makeShown = rc;
            int level = Crafting.Level(P.CraftExp);
            bool learned = P.SkillBooks.Contains(rc.Id), can = CanMake(rc);
            int price = PerfRules.SkillBookPrice(rc.Group);
            OriginalMenus.Hide(v, "ID_MAKEDETAIL_LIST_CONDITION", "ID_MAKEDETAIL_TXT_Cover", "ID_MAKEDETAIL_BTN_PLUS", "ID_MAKEDETAIL_BTN_MINUS",
                               "ID_MAKEDETAIL_TXT_CREATE_COUNT", "ID_MAKEDETAIL_TXT_CREATE_COUNT_NUM");
            var icon = v.Control("ID_MAKEDETAIL_ITEM_ICON");
            if (icon) OriginalMenus.ItemTile(icon.transform, Vector2.zero, new Vector2(64, 64), rc.Result, Crafting.Name(rc.Result));
            v.SetText("ID_MAKEDETAIL_TXT_ITEMNAME", $"{Crafting.Name(rc.Result)} x{rc.ResultCount}   ({rc.Name})");
            v.SetText("ID_MAKEDETAIL_TXT_ITEMDESC", Clean(rc.Desc));
            v.SetText("ID_MAKEDETAIL_TXT_SKILLEXP", "Manufacturing EXP");
            v.SetText("ID_MAKEDETAIL_TXT_SUCCEED", "Success");
            v.SetText("ID_MAKEDETAIL_TXT_FAILED", "Failure");
            v.SetText("ID_MAKEDETAIL_TXT_SUCCEEDCOUNT", $"+{rc.SuccessExp:N0}");
            v.SetText("ID_MAKEDETAIL_TXT_FAILEDCOUNT", $"+{rc.FailExp:N0}");
            v.SetText("ID_MAKEDETAIL_TXT_ITEMEFFECT", $"Success rate {rc.Chance}%: makes {Crafting.Name(rc.Result)} x{rc.ResultCount}. The materials and the kit are used up either way.");
            v.SetText("ID_MAKEDETAIL_TXT_ITEMLIMIT", learned ? $"Skill book learned (Manufacturing Lv {rc.ReqLevel})."
                : $"Skill book: Manufacturing Lv {rc.ReqLevel}, {price:N0} Mito" + (level < rc.ReqLevel ? $"  (your level is {level})" : ""));
            v.SetText("ID_MAKEDETAIL_TXT_NEED_HAVE", !learned ? "Make learns the skill book first." : can ? "You have everything it needs." : "Some materials are missing.");
            v.SetText("ID_MAKEDETAIL_TXT_NEEDCOUNT", "[ Need ]");
            v.SetText("ID_MAKEDETAIL_TXT_HAVECOUNT", "[ Have ]");
            // the source row: up to three materials, the kit, then the result
            var sources = rc.Inputs.Take(3).Select(i => (i.id, i.count)).ToList();
            for (int k = 1; k <= 3; k++)
            {
                bool used = k <= sources.Count;
                foreach (var id in new[] { "SOURCE_ITEM", "TXT_SOURCE", "TXT_NEEDCOUNT", "TXT_HAVECOUNT", "TXT_SOURCE_NEEDCOUNT", "TXT_SOURCE_HAVECOUNT" })
                    v.SetVisible($"ID_MAKEDETAIL_{id}{k}", used);
                if (used) Source(v, k, sources[k - 1].id, sources[k - 1].count, true);
            }
            Source(v, 4, rc.Kit, 1, true);
            Source(v, 5, rc.Result, rc.ResultCount, false);
            v.SetText("ID_MAKEDETAIL_TXT_CREATE_RESULT", "Result");
            v.SetEnabled("ID_MAKEDETAIL_MAKE", learned ? can : level >= rc.ReqLevel && P.Mito >= price);
        }

        static void Source(NuiView v, int k, string item, int count, bool have)
        {
            var slot = v.Control($"ID_MAKEDETAIL_SOURCE_ITEM{k}");
            if (slot) OriginalMenus.ItemTile(slot.transform, Vector2.zero, new Vector2(64, 64), item, Crafting.Name(item), null, 0, !have || P.Count(item) >= count ? 1f : 0.45f);
            if (k <= 4) v.SetText($"ID_MAKEDETAIL_TXT_SOURCE{k}", Crafting.Name(item));
            v.SetText($"ID_MAKEDETAIL_TXT_NEEDCOUNT{k}", count.ToString());
            if (have) v.SetText($"ID_MAKEDETAIL_TXT_HAVECOUNT{k}", P.Count(item).ToString());
        }

        static void CloseRecipe()
        {
            OriginalMenus.CloseView(makeResult);
            OriginalMenus.CloseView(makeDetail);
            makeResult = makeDetail = null;
            makeShown = null;
        }

        static void MakeOrLearn()
        {
            var rc = makeShown;
            if (rc == null) return;
            if (P.SkillBooks.Contains(rc.Id)) { Craft(rc); return; }
            int price = PerfRules.SkillBookPrice(rc.Group);
            UiPopup.Show($"Learn the skill book \"{rc.Name}\" for {price:N0} Mito?", id => { if (id == UiPopup.Yes) Learn(rc); }, UiPopup.Yes, UiPopup.No);
        }

        static void Learn(Recipe rc)
        {
            int level = Crafting.Level(P.CraftExp), price = PerfRules.SkillBookPrice(rc.Group);
            Progress.Op("skill.learn", D(("id", rc.Id), ("group", rc.Group), ("reqLv", rc.ReqLevel)), () =>
            {
                if (level < rc.ReqLevel) return Fail($"Needs manufacturing level {rc.ReqLevel}.");
                if (P.Mito < price) return Fail($"You need {price - P.Mito:N0} more Mito.");
                P.Mito -= price; P.SkillBooks.Add(rc.Id);
                return Ok;
            }, r => RefreshManufacturing());
        }

        static void Craft(Recipe rc)
        {
            var inputs = rc.Inputs.Select(i => (object)new List<object> { i.id, i.count }).ToList();
            var body = D(("recipe", new Dictionary<string, object>
            {
                ["id"] = rc.Id, ["inputs"] = inputs, ["kit"] = rc.Kit, ["result"] = rc.Result, ["count"] = rc.ResultCount,
                ["chance"] = rc.Chance, ["exp"] = rc.SuccessExp, ["failExp"] = rc.FailExp,
            }));
            Progress.Op("craft", body, () =>
            {
                if (!rc.Inputs.All(i => P.Count(i.id) >= i.count) || P.Count(rc.Kit) <= 0) return Fail("Missing materials.");
                foreach (var i in rc.Inputs) P.Give(i.id, -i.count);
                P.Give(rc.Kit, -1);
                bool ok = Random.Range(0, 100) < rc.Chance;
                if (ok) P.Give(rc.Result, rc.ResultCount);
                P.CraftExp += ok ? rc.SuccessExp : rc.FailExp;
                return new Progress.Result { Ok = true, Error = ok ? null : "failed" };
            }, r =>
            {
                bool success = r.Ok && r.Error == null;
                if (success) GameEffects.Converted();   // a made item: the conversion banner (0x4f53a0)
                else AudioLibrary.PlaySfx("sfx/SID_UPGRADE_FAIL");
                RefreshManufacturing();
                if (r.Ok && makeList) ShowResult(rc, success);
                else if (r.Ok) UiPopup.Message(success ? $"Made {Crafting.Name(rc.Result)} x{rc.ResultCount}!" : "Manufacturing failed; the materials were used up.");
            });
        }

        static void ShowResult(Recipe rc, bool success)
        {
            OriginalMenus.CloseView(makeResult);
            makeResult = OriginalMenus.Open(4940000);
            var v = makeResult;
            if (!v) return;
            OriginalMenus.HideDefaultBacks(v);
            v.SetVisible("ID_MAKEDETAIL_CREATE_SUCCESS", success);
            v.SetVisible("ID_MAKEDETAIL_CREATE_FAIL", !success);
            OriginalMenus.Hide(v, "ID_MAKEDETAIL_BTN_CANCEL");
            v.SetText("ID_MAKEDETAIL_TXT_CREATE_SUCCEED", success ? "Made" : "Failed");
            v.SetText("ID_MAKEDETAIL_TXT_CREATE_SUCCEED_COUNT", "");
            v.SetText("ID_MAKEDETAIL_TXT_CREATE_SUCCEED_COUNT_NUM", success ? rc.ResultCount.ToString() : "0");
        }

        // ================= item bag =================
        /// <summary>The item bag is the inventory's: usable items are used from there.</summary>
        public static void ItemBag() => OriginalMenus.Inventory();

        internal static void UseItem(string id)
        {
            if ((id == "i_n_upgrade" || id.StartsWith("i_n_roof_")) && GameFlow.BlockedByMission()) return;
            int slot = P.CurrentCar;
            Progress.Op("item.use", D(("id", id), ("slot", slot)), () =>
            {
                if (P.Count(id) <= 0) return Fail("You don't have that.");
                var car = P.Cars[slot];
                var info = GameTables.Car(car.VehicleIndex);
                switch (id)
                {
                    case "i_n_upgrade":
                        if (car.Grade + 1 >= ShopData.Grades(info).Count) return Fail("This car is already at its top grade.");
                        car.Grade++; break;
                    case "i_n_roof_jackbox":
                    case "i_n_roof_xcatbox":
                        int roof = id == "i_n_roof_jackbox" ? 10890 : 20776;
                        if (!car.Parts.Contains(roof)) { car.Parts.Add(roof); car.GiftParts.Add(roof); }
                        car.Roof = roof; break;
                    case "i_bacchus_f": P.AddExp(2000); break;
                    case "i_bacchus_d": P.AddExp(6000); break;
                    default: return Fail("That item can't be used here.");
                }
                P.Give(id, -1);
                return Ok;
            }, r =>
            {
                if (!r.Ok) return;
                var flow = GameFlow.Instance;
                if (flow) { flow.RespawnCar(); flow.RepaintCar(); flow.ApplyCarStats(); flow.RefreshHud(); }
                OriginalMenus.RefreshInventoryIfOpen();
                UiPopup.Message($"Used {Crafting.Name(id)}.");
            });
        }
    }
}
