using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DriftCity.Core;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.Game
{
    public enum BagKind { Stack, Shop, Part, Licence }

    /// <summary>
    /// One slot of the item bag (MYINVEN 3190000): a named stack (PlayerProfile.Items: enhancers, materials, drinks...),
    /// a visual-shop item by UniqueId (PlayerProfile.Inventory: paints) or a performance part not fitted on a car.
    /// </summary>
    public sealed class BagEntry
    {
        public BagKind Kind;
        public string Id;          // Stack: item id (vshopitem ItemName or a manufacturing id)
        public int ItemId;         // Shop: vshopitem UniqueId
        public PerfPart Part;      // Part
        public int LicenceId;      // Licence: an earned licence that isn't mounted (drag it onto the licence page)
        public int Count = 1;
        public int Order;          // position in the profile lists: the client's "date" order

        /// <summary>Stable reference ("s:i_n_00003", "i:10000", "p:12") used by quick slots, trade offers and the server messages.</summary>
        public string Key => Kind == BagKind.Stack ? "s:" + Id : Kind == BagKind.Shop ? "i:" + ItemId : Kind == BagKind.Licence ? "l:" + LicenceId : "p:" + (Part != null ? Part.Uid : -1);
        public string Name => ItemCatalog.Name(this);
        public string Icon => ItemCatalog.Icon(this);

        /// <summary>The bag entry as the server's item messages name it (src/items.js find()).</summary>
        public Dictionary<string, object> Ref(int count = 0)
        {
            var d = new Dictionary<string, object>();
            if (Kind == BagKind.Stack) { d["kind"] = "stack"; d["id"] = Id; d["count"] = count > 0 ? count : Count; }
            else if (Kind == BagKind.Shop) { d["kind"] = "shop"; d["itemId"] = ItemId; d["count"] = count > 0 ? count : Count; }
            else if (Kind == BagKind.Licence) { d["kind"] = "licence"; d["id"] = LicenceId; }
            else { d["kind"] = "part"; d["uid"] = Part.Uid; }
            return d;
        }
    }

    /// <summary>A row of init\vshopitem.xlt (names, tooltip text, category, prices).</summary>
    public sealed class ShopItemRow
    {
        public int UniqueId;
        public string Item, Name, Desc, Top, Category, Sub;
        public int MitoPrice, SellMito;
    }

    /// <summary>
    /// Names, icons, descriptions and the bag listing for every kind of item. Texts come from vshopitem.xlt (MarketName,
    /// "Desc (Tooltip)"), the manufacturing tables (Crafting) and the client's own format strings in globaltext.xlt
    /// (the parts tooltip builder 0x6239a0: "%sSpeed Part" 10352, "Speed + %u" 10353, "Required Level: %d" 10360 ...).
    /// </summary>
    public static class ItemCatalog
    {
        static PlayerProfile P => PlayerProfile.Current;
        static Dictionary<int, ShopItemRow> byId;
        static Dictionary<string, ShopItemRow> byName;

        static void Load()
        {
            if (byId != null) return;
            byId = new Dictionary<int, ShopItemRow>();
            byName = new Dictionary<string, ShopItemRow>(System.StringComparer.OrdinalIgnoreCase);
            byte[] data = null;
            try { data = DriftCityData.Files.Read(@"init\vshopitem.xlt"); } catch (System.Exception) { }
            if (data == null) return;
            var t = XltTable.Parse(data);
            foreach (var r in t.Rows)
            {
                var row = new ShopItemRow
                {
                    UniqueId = t.GetInt(r, "UniqueId"), Item = (t.Get(r, "ItemName") ?? "").Trim(), Name = (t.Get(r, "MarketName") ?? "").Trim(),
                    Desc = t.Get(r, "Desc (Tooltip)") ?? "", Top = t.Get(r, "Top Category"), Category = t.Get(r, "Category"), Sub = t.Get(r, "Sub Catergory"),
                    MitoPrice = Mathf.Max(t.GetInt(r, "Mito Price"), t.GetInt(r, "Mito Price0D")), SellMito = t.GetInt(r, "Sell Mito"),
                };
                if (row.UniqueId > 0 && !byId.ContainsKey(row.UniqueId)) byId[row.UniqueId] = row;
                if (row.Item.Length > 0 && !byName.ContainsKey(row.Item)) byName[row.Item] = row;
            }
        }

        public static ShopItemRow Shop(int uniqueId) { Load(); return byId.TryGetValue(uniqueId, out var r) ? r : null; }
        public static ShopItemRow ShopByName(string item) { Load(); return item != null && byName.TryGetValue(item, out var r) ? r : null; }

        /// <summary>Client format text (globaltext key) or the fallback when the table lacks it.</summary>
        internal static string Text(string key, string fallback, params object[] args)
        {
            var fmt = GameTables.Text(key);
            if (string.IsNullOrEmpty(fmt) || fmt == key) fmt = fallback;
            return MissionText.Printf(fmt, args);
        }

        static readonly string[] PartNameKey = { "10352", "10356", "10354", "10358" };   // speed, accel, durability, booster
        static readonly string[] PartStatKey = { "10353", "10357", "10355", "10359" };
        static readonly string[] PartNameFallback = { "%sSpeed Part", "%sAcceleration Part", "%sDurability Part", "%sBooster Part" };
        static readonly string[] PartStatFallback = { "Speed + %u", "Acceleration + %u", "Durability + %u", "Booster + %u" };

        public static string PartIcon(PerfPart p) => !string.IsNullOrEmpty(p.Item) ? p.Item : $"i_{"sacb"[Mathf.Clamp(p.Type, 0, 3)]}_{(p.Grade >= 3 ? "000003_g" : (p.Grade + 1).ToString("000000"))}";

        /// <summary>"S Speed Part + 3" as the parts tooltip writes it ("%s %s + %d", grade letter first).</summary>
        public static string PartName(PerfPart p)
        {
            var set = SetParts.Of(p);
            if (set != null) return p.Enhance > 0 ? $"{set.Name} + {p.Enhance}" : set.Name;
            string kind = Text(PartNameKey[Mathf.Clamp(p.Type, 0, 3)], PartNameFallback[Mathf.Clamp(p.Type, 0, 3)], "");
            string grade = PerfRules.Grades[Mathf.Clamp(p.Grade, 0, 3)];
            return p.Enhance > 0 ? $"{grade} {kind} + {p.Enhance}" : $"{grade} {kind}";
        }

        public static string PartStat(PerfPart p) => Text(PartStatKey[Mathf.Clamp(p.Type, 0, 3)], PartStatFallback[Mathf.Clamp(p.Type, 0, 3)], PerfRules.Points(p));

        public static string Name(BagEntry e)
        {
            if (e == null) return "";
            switch (e.Kind)
            {
                case BagKind.Part: return e.Part != null ? PartName(e.Part) : "";
                case BagKind.Licence: return Licences.Get(e.LicenceId)?.Name ?? $"Licence #{e.LicenceId}";
                case BagKind.Shop:
                    var paint = ShopData.Paints.FirstOrDefault(x => x.UniqueId == e.ItemId);
                    if (paint != null) return paint.Name;
                    return Shop(e.ItemId)?.Name ?? $"Item #{e.ItemId}";
                default:
                    var setPart = SetParts.Get(e.Id);
                    if (setPart != null) return setPart.Name;
                    var option = CarParts.Option(e.Id);
                    if (option != null && option.Name.Length > 0) return option.Name;
                    var row = ShopByName(e.Id);
                    var crafted = Crafting.Name(e.Id);
                    if (crafted != e.Id) return crafted;
                    return row != null && row.Name.Length > 0 ? row.Name : e.Id;
            }
        }

        public static string Icon(BagEntry e)
        {
            if (e == null) return null;
            if (e.Kind == BagKind.Part) return e.Part != null ? PartIcon(e.Part) : null;
            if (e.Kind == BagKind.Licence) return Licences.Get(e.LicenceId)?.IdName;
            if (e.Kind == BagKind.Shop) return Shop(e.ItemId)?.Item;
            return e.Id;
        }

        /// <summary>Paints carry no icon of their own: their swatch is the colour.</summary>
        public static Color? Swatch(BagEntry e)
        {
            if (e == null || e.Kind != BagKind.Shop) return null;
            var paint = ShopData.Paints.FirstOrDefault(x => x.UniqueId == e.ItemId);
            return paint?.Color;
        }

        public static string Desc(BagEntry e)
        {
            if (e == null) return "";
            if (e.Kind == BagKind.Part) return SetParts.Of(e.Part) != null ? SetDesc(SetParts.Of(e.Part)) : "";
            if (e.Kind == BagKind.Licence) return Clean(Licences.Get(e.LicenceId)?.Info) + "\nDrag it onto the licence page to mount it.";
            if (e.Kind == BagKind.Shop) return Clean(Shop(e.ItemId)?.Desc);
            var setRow = SetParts.Get(e.Id);
            if (setRow != null) return SetDesc(setRow);
            var opt = CarParts.Option(e.Id);
            if (opt != null) return OptionDesc(opt);
            var use = ItemUse.Describe(e.Id);
            if (use != null) return use;
            var row = ShopByName(e.Id);
            if (row != null && row.Desc.Length > 0) return Clean(row.Desc);
            var rc = Crafting.Recipes.FirstOrDefault(r => r.Result == e.Id);
            if (rc != null && !string.IsNullOrEmpty(rc.Desc)) return Clean(rc.Desc);
            return "Material for manufacturing.";
        }

        /// <summary>A set part's lines: "[Set Parts]" (11487), the set and its bonus; the expired form says a lube restores it (10798).</summary>
        public static string SetDesc(SetPartRow r)
        {
            string s = $"{Text("11487", "[Set Parts]")} {r.SetName}\n{Clean(r.Bonus)}";
            if (r.Expired) s += "\n" + Text("10798", "Can be restored using Restoration Lube");
            return s;
        }

        /// <summary>An option / docking part: its effect, the stat it docks onto ("Equippable on N Parts" 10923-10926 by letter)
        /// and the required level (10360).</summary>
        public static string OptionDesc(OptionPartRow r)
        {
            string on = r.Stat < 0 ? "Equippable on all parts" : "Equippable on " + new[] { "Speed", "Acceleration", "Durability", "Booster" }[r.Stat] + " Parts";
            var a = AssistTable.Get(r.Assist);
            string effect = r.Desc.Length > 0 ? r.Desc : a?.Desc ?? "";
            return $"{(r.Docking ? "Docking Part" : "Option Part")}\n{effect}\n{on}\n{Text("10360", "Required Level: %d", r.Level)}";
        }

        /// <summary>Table text without the client's colour / line-break tags.</summary>
        public static string Clean(string s) => string.IsNullOrEmpty(s) ? "" : Regex.Replace(Regex.Replace(s, "<BR>", "\n", RegexOptions.IgnoreCase), "<[^>]*>", "").Trim();

        /// <summary>
        /// The bag as MYINVEN lists it: performance parts that aren't fitted on a car, the named stacks, then the shop items.
        /// Order is the order they were obtained (the client's "date" sort).
        /// </summary>
        public static List<BagEntry> Bag(PlayerProfile p)
        {
            var list = new List<BagEntry>();
            if (p == null) return list;
            var fitted = new HashSet<int>(p.Cars.Where(c => c.PerfSlots != null).SelectMany(c => c.PerfSlots).Where(u => u >= 0));
            fitted.UnionWith(CarParts.SecondRowUids(p));   // the NDLG parts page's second row
            int order = 0;
            foreach (var part in p.PerfParts) if (!fitted.Contains(part.Uid)) list.Add(new BagEntry { Kind = BagKind.Part, Part = part, Order = order++ });
            foreach (var s in p.Items) if (s.Count > 0) list.Add(new BagEntry { Kind = BagKind.Stack, Id = s.Id, Count = s.Count, Order = order++ });
            foreach (var it in p.Inventory) if (it.Count > 0) list.Add(new BagEntry { Kind = BagKind.Shop, ItemId = it.ItemId, Count = it.Count, Order = order++ });
            // earned licences waiting to be mounted (the licence page takes them by drag and drop)
            foreach (int id in p.Licences) if (id != p.MountedLicence && Licences.Get(id) != null) list.Add(new BagEntry { Kind = BagKind.Licence, LicenceId = id, Order = order++ });
            return list;
        }

        /// <summary>The bag entry a key names, as the profile holds it now (null when it is gone).</summary>
        public static BagEntry Find(PlayerProfile p, string key)
        {
            if (p == null || string.IsNullOrEmpty(key) || key.Length < 3) return null;
            string v = key.Substring(2);
            switch (key[0])
            {
                case 's': { int n = p.Count(v); return n > 0 ? new BagEntry { Kind = BagKind.Stack, Id = v, Count = n } : null; }
                case 'i': { if (!int.TryParse(v, out int id)) return null; int n = p.ItemCount(id); return n > 0 ? new BagEntry { Kind = BagKind.Shop, ItemId = id, Count = n } : null; }
                case 'p': { if (!int.TryParse(v, out int uid)) return null; var part = p.PerfParts.Find(x => x.Uid == uid); return part != null ? new BagEntry { Kind = BagKind.Part, Part = part } : null; }
                case 'l': { if (!int.TryParse(v, out int lid)) return null; return p.Licences.Contains(lid) && p.MountedLicence != lid ? new BagEntry { Kind = BagKind.Licence, LicenceId = lid } : null; }
            }
            return null;
        }

        /// <summary>Sort orders of the MYINVEN buttons: 0 date (as obtained), 1 type, 2 Mito value.</summary>
        public static List<BagEntry> Sorted(List<BagEntry> bag, int sort)
        {
            if (sort == 1) return bag.OrderBy(e => (int)e.Kind).ThenBy(e => Category(e)).ThenBy(e => e.Name).ToList();
            if (sort == 2) return bag.OrderByDescending(e => Value(e)).ThenBy(e => e.Order).ToList();
            return bag.OrderBy(e => e.Order).ToList();
        }

        public static string Category(BagEntry e)
        {
            if (e.Kind == BagKind.Part) return SetParts.Of(e.Part) != null ? "SETPARTS" : "PARTS";
            if (e.Kind == BagKind.Licence) return "LICENCE";
            if (e.Kind == BagKind.Stack && SetParts.Is(e.Id)) return "SETPARTS";
            if (e.Kind == BagKind.Shop) return Shop(e.ItemId)?.Category ?? "";
            return ShopByName(e.Id)?.Category ?? (ItemUse.Describe(e.Id) != null ? "USE" : "MATERIAL");
        }

        /// <summary>What an entry is worth in Mito (its sale price, else the shop price): the Mito sort and the tooltip's price line.</summary>
        public static long Value(BagEntry e)
        {
            long sell = ItemUse.SellPrice(e);
            if (sell > 0) return sell;
            if (e.Kind == BagKind.Shop) { var paint = ShopData.Paints.FirstOrDefault(x => x.UniqueId == e.ItemId); return paint?.Price ?? Shop(e.ItemId)?.MitoPrice ?? 0; }
            if (e.Kind == BagKind.Stack) return ShopByName(e.Id)?.MitoPrice ?? 0;
            return 0;
        }
    }

    /// <summary>
    /// The client's hover boxes: ITEMINFO (1320000) for items, LISENCEINFO 1360000 / 1370000 for licences (earned /
    /// not yet). The tooltip manager (0x626920) keeps one of each and fills it on hover; it is drawn beside the slot
    /// over a translucent box (the BG control, colour B4000000) that grows with the lines.
    /// </summary>
    public static class ItemTooltip
    {
        static NuiView view;
        static RectTransform anchor;
        static bool anchored;

        public static bool IsOpen => view;
        public static NuiView View => view;

        /// <summary>Item tooltip: name, required level, period, description, price, binding and up to two stat lines.</summary>
        public static void Show(BagEntry e, RectTransform near)
        {
            if (e == null) { Hide(); return; }
            if (e.Kind == BagKind.Licence) { ShowLicence(Licences.Get(e.LicenceId), near); return; }
            var lines = new List<(string id, string text)>();
            string name = e.Name + (e.Count > 1 ? $" ({e.Count})" : "");
            lines.Add(("ID_ITEMINFO_NAME", name));
            var setRow = e.Kind == BagKind.Part ? SetParts.Of(e.Part) : e.Kind == BagKind.Stack ? SetParts.Get(e.Id) : null;
            if (e.Kind == BagKind.Part && e.Part != null)
            {
                int req = setRow != null ? setRow.Level : PerfRules.ReqLevel[Mathf.Clamp(e.Part.Grade, 0, 2)];
                lines.Add(("ID_ITEMINFO_REQLEVEL", ItemCatalog.Text("10360", "Required Level: %d", req)));
                lines.Add(("ID_ITEMINFO_DESC_ADV1", ItemCatalog.PartStat(e.Part)));
            }
            else if (setRow != null)
            {
                lines.Add(("ID_ITEMINFO_REQLEVEL", ItemCatalog.Text("10360", "Required Level: %d", setRow.Level)));
                lines.Add(("ID_ITEMINFO_DESC_ADV1", ItemCatalog.PartStat(new PerfPart { Type = setRow.Type, Grade = setRow.Grade, Item = setRow.Id })));
            }
            lines.Add(("ID_ITEMINFO_EXPIRE", ItemPeriods.Line(e)));
            string desc = ItemCatalog.Desc(e);
            if (!string.IsNullOrEmpty(desc)) lines.Add(("ID_ITEMINFO_DESC", desc));
            long value = ItemUse.SellPrice(e);
            if (value > 0) lines.Add(("ID_ITEMINFO_MITO", ItemCatalog.Text("11327", "%d Mito", value)));
            if (!ItemUse.Tradeable(e)) lines.Add(("ID_ITEMINFO_BIND", ItemCatalog.Text("10738", "Non-tradeable")));
            if (e.Kind == BagKind.Stack)
            {
                var buff = ItemUse.BuffLine(e.Id);
                if (buff != null) lines.Add(("ID_ITEMINFO_DESC_ADV2", buff));
            }
            Open(1320000, "ID_ITEMINFO_BG", lines, near);
        }

        /// <summary>A custom-shop part (Drift Item cells): name, description and its stat bonus.</summary>
        public static void ShowVisual(PartItem part, RectTransform near)
        {
            if (part == null) { Hide(); return; }
            var lines = new List<(string id, string text)> { ("ID_ITEMINFO_NAME", part.Name), ("ID_ITEMINFO_EXPIRE", Text("10282", "Usage Period: Unlimited")) };
            var desc = ItemCatalog.Clean(part.Desc);
            if (desc.Length > 0) lines.Add(("ID_ITEMINFO_DESC", desc));
            if (part.BonusText.Length > 0) lines.Add(("ID_ITEMINFO_DESC_ADV1", part.BonusText));
            lines.Add(("ID_ITEMINFO_BIND", PartsCatalog.SlotName(part.Slot)));
            Open(1320000, "ID_ITEMINFO_BG", lines, near);
        }

        static string Text(string key, string fallback, params object[] args) => ItemCatalog.Text(key, fallback, args);

        /// <summary>Licence tooltip: name, [Requirement] lines with progress, [Bonus Effect].</summary>
        public static void ShowLicence(Licence l, RectTransform near)
        {
            if (l == null) { Hide(); return; }
            var p = PlayerProfile.Current;
            bool earned = p != null && p.Licences.Contains(l.Id);
            string pre = earned ? "ID_LISENCEINFO_NORMAL_" : "ID_LISENCEINFO_GREY_";
            var lines = new List<(string, string)> { (pre + "TITLE", l.Name) };
            lines.Add((pre + "CONDITIONTITLE", ItemCatalog.Text("11263", "[Requirement]")));
            var conds = LicenceInfo.ConditionLines(l, p);
            string[] ord = { "1ST", "2ND", "3RD" };
            for (int i = 0; i < conds.Count && i < 15; i++) lines.Add((pre + "CONDITION" + (i < 3 ? ord[i] : (i + 1) + "TH"), conds[i]));
            lines.Add((pre + "EFFECTTITLE", ItemCatalog.Text("11264", "[Bonus Effect]")));
            lines.Add((pre + "EFFECT", ItemCatalog.Clean(l.EffectText)));
            Open(earned ? 1360000 : 1370000, pre + "BASE", lines, near);
        }

        public static void Hide()
        {
            if (view && UiManager.Instance) UiManager.Instance.Close(view);
            view = null;
            anchor = null;
            anchored = false;
        }

        static void Open(int dialog, string bgId, List<(string id, string text)> lines, RectTransform near)
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            if (view && view.Dialog.Id != dialog) Hide();
            if (!view) view = ui.Open(dialog);
            if (!view) return;
            anchor = near;
            anchored = near != null;
            if (!view.TryGetComponent<Follower>(out _)) view.gameObject.AddComponent<Follower>();
            var shown = new HashSet<string>();
            float y = 8f, width = 174f;
            foreach (var (id, text) in lines)
            {
                var c = view.Control(id);
                if (!c) continue;
                shown.Add(id);
                c.gameObject.SetActive(true);
                c.SetText(text);
                var rt = (RectTransform)c.transform;
                var label = c.GetComponentInChildren<Text>(true);
                float h = 16f;
                if (label)
                {
                    label.horizontalOverflow = HorizontalWrapMode.Wrap;
                    var lrt = label.rectTransform;
                    float w = Mathf.Max(80f, lrt.sizeDelta.x);
                    h = Mathf.Max(14f, label.cachedTextGenerator.GetPreferredHeight(text ?? "", label.GetGenerationSettings(new Vector2(w, 0))) / Mathf.Max(0.01f, label.pixelsPerUnit));
                    lrt.sizeDelta = new Vector2(w, h);
                    if (lrt != rt) { lrt.anchoredPosition = Vector2.zero; }
                    var e1 = c.transform.Find("e1") as RectTransform;
                    if (e1 && e1 != lrt) e1.sizeDelta = new Vector2(w, h);
                }
                rt.anchoredPosition = new Vector2(rt.anchoredPosition.x, -y);
                rt.sizeDelta = new Vector2(rt.sizeDelta.x, h);
                y += h + 2f;
            }
            foreach (var c in view.Controls)
                if (c.Data.IdName != bgId && !shown.Contains(c.Data.IdName)) c.gameObject.SetActive(false);
            // the translucent box behind the lines
            var bg = view.Control(bgId);
            if (bg)
            {
                bg.gameObject.SetActive(true);
                var brt = (RectTransform)bg.transform;
                brt.anchoredPosition = Vector2.zero;
                brt.sizeDelta = new Vector2(width, y + 6f);
                if (!bg.TryGetComponent<Image>(out var img)) img = bg.gameObject.AddComponent<Image>();
                img.color = NuiControlView.ParseColor(bg.Data.I("C1_1"), new Color(0, 0, 0, 0.7f));
                img.raycastTarget = false;
                bg.transform.SetAsFirstSibling();
            }
            foreach (var g in view.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
            Place(view, near, new Vector2(width, y + 6f));
            view.transform.SetAsLastSibling();
        }

        /// <summary>Puts the box to the right of the anchor (left of it near the screen edge), kept on screen.</summary>
        static void Place(NuiView v, RectTransform near, Vector2 size)
        {
            var origin = v.transform.Find("origin") as RectTransform;
            var root = UiManager.Instance.Root as RectTransform;
            if (!origin || !root) return;
            Vector2 pos;
            if (near)
            {
                var corners = new Vector3[4];
                near.GetWorldCorners(corners);
                var tr = root.InverseTransformPoint(corners[2]);   // top-right in canvas space (centre origin)
                var tl = root.InverseTransformPoint(corners[1]);
                pos = new Vector2(tr.x + 6f, tr.y);
                if (pos.x + size.x > root.rect.xMax) pos.x = tl.x - 6f - size.x;
            }
            else pos = new Vector2(root.rect.xMin + 20f, root.rect.yMax - 20f);
            pos.y = Mathf.Clamp(pos.y, root.rect.yMin + size.y, root.rect.yMax);
            pos.x = Mathf.Clamp(pos.x, root.rect.xMin, root.rect.xMax - size.x);
            origin.position = root.TransformPoint(new Vector3(pos.x, pos.y, 0));
        }

        /// <summary>Closes the box when its slot goes away (window closed, page changed).</summary>
        sealed class Follower : MonoBehaviour
        {
            void LateUpdate()
            {
                if (view != GetComponent<NuiView>()) return;
                if (anchored && (!anchor || !anchor.gameObject.activeInHierarchy)) Hide();
            }
        }
    }
}
