using System;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Core;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// init\vshopcategory.xlt (loader 0x7deea0 from the game init 0x43a239, rows "%d%s %d%s %d%s %d%d %d%d %d%d %s" read by
    /// 0x7de890 into 0x44-byte records: +0 top, +4 main, +8 sub, +0xc Top Menu Enable, +0x10 Enable, +0x14 Multiple
    /// Selection, +0x18 Stackable, +0x1c Coin gift, +0x20 Mito gift, +0x24 name). What the client reads back (DriftCity_RE/
    /// AUDIO.md 7, SHOPS.md):
    /// <list type="bullet">
    /// <item>0x7dea10 (by top id, the first row): Top Menu Enable, which 0x6600f0 writes into the enabled bit (control flag 4)
    ///   of the VSHOP top tabs 0..4 (classic and ADV bars);</item>
    /// <item>0x7de980 / 0x7de9b0 (by sub id): Multiple Selection / Stackable (the product list, wish list and checkout);</item>
    /// <item>0x7de9e0 (by sub id): the sub category's name.</item>
    /// </list>
    /// The Enable column has no reader in the exe.
    /// </summary>
    public static class VShopCategories
    {
        public sealed class Row
        {
            public int Top, Main, Sub;
            public string TopName, MainName, SubName, Name;
            public bool TopMenuEnable, Enable, Multiple, Stackable, CoinGift, MitoGift;
        }

        static List<Row> rows;
        static Dictionary<int, int> subOfItem;

        public static IReadOnlyList<Row> All { get { Load(); return rows; } }

        public static void ResetCache() { rows = null; subOfItem = null; }

        static void Load()
        {
            if (rows != null) return;
            rows = new List<Row>();
            subOfItem = new Dictionary<int, int>();
            var fs = DriftCityData.Files;
            try
            {
                if (fs != null && fs.Exists(@"init\vshopcategory.xlt"))
                {
                    var t = XltTable.Parse(fs.Read(@"init\vshopcategory.xlt"));
                    foreach (var r in t.Rows)
                        rows.Add(new Row
                        {
                            Top = t.GetInt(r, "Top", -1), TopName = t.Get(r, "Top Category").Trim(),
                            Main = t.GetInt(r, "Main", -1), MainName = t.Get(r, "Category").Trim(),
                            Sub = t.GetInt(r, "Sub", -1), SubName = t.Get(r, "Sub Catergory").Trim(),
                            TopMenuEnable = t.GetInt(r, "Top Menu Enable") != 0, Enable = t.GetInt(r, "Enable") != 0,
                            Multiple = t.GetInt(r, "Multiple Selection") != 0, Stackable = t.GetInt(r, "Stackable") != 0,
                            CoinGift = t.GetInt(r, "Allow Coin Gift") != 0, MitoGift = t.GetInt(r, "Allow Mito Gift") != 0,
                            Name = t.Get(r, "Name").Trim(),
                        });
                }
                if (fs != null && fs.Exists(@"init\vshopitem.xlt"))
                {
                    var t = XltTable.Parse(fs.Read(@"init\vshopitem.xlt"));
                    foreach (var r in t.Rows)
                    {
                        int id = t.GetInt(r, "UniqueId", -1), sub = t.GetInt(r, "Sub", -1);
                        if (id >= 0 && sub >= 0 && !subOfItem.ContainsKey(id)) subOfItem[id] = sub;
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning("vshopcategory: " + e.Message); }
        }

        /// <summary>0x7dea10: Top Menu Enable of the first row with this top id (false without one).</summary>
        public static bool TopMenuEnabled(int top) => All.FirstOrDefault(r => r.Top == top)?.TopMenuEnable ?? false;

        static Row BySub(int sub) => All.FirstOrDefault(r => r.Sub == sub);

        /// <summary>0x7de980: Multiple Selection of a sub category.</summary>
        public static bool Multiple(int sub) => BySub(sub)?.Multiple ?? false;
        /// <summary>0x7de9b0: Stackable of a sub category.</summary>
        public static bool Stackable(int sub) => BySub(sub)?.Stackable ?? false;
        /// <summary>0x7de9e0: the sub category's name ("Bumper", "Flame", "Aero Wing" ...), null when unknown.</summary>
        public static string Name(int sub) => BySub(sub)?.Name;

        /// <summary>The vshopitem Sub category of a product (-1 when unknown).</summary>
        public static int SubOf(int uniqueId) { Load(); return subOfItem.TryGetValue(uniqueId, out var s) ? s : -1; }

        /// <summary>The top ids of the classic VSHOP top bar's tabs COLOR, DECORATION, TUNNING, ETC (2340000).</summary>
        public static readonly int[] ClassicTops = { 0, 1, 2, 3 };

        /// <summary>The main category (vshopcategory "Category") a VSHOP sub tab control stands for.</summary>
        public static string MainOfTab(string controlId)
        {
            if (string.IsNullOrEmpty(controlId)) return null;
            string k = controlId.Replace("ID_VSHOP_TAB_", "").Replace("_ADV_", "_");
            int u = k.IndexOf('_');
            string tail = u >= 0 ? k.Substring(u + 1) : k;
            switch (tail)
            {
                case "HIGHGLOSS": return "PAINTHIGHGLOSS";
                case "AEROCOMBI": return "AERO";
                case "AIRINTAKE": return "Air Duct";
                case "OMDPARTSBOX": return "PARTSBOX";
                case "LOTTOSLOT": return "LOTTO";
                case "TIRE": return "TIRE";
                default: return tail;
            }
        }

        /// <summary>The table lists the main category of this sub tab.</summary>
        public static bool HasTab(string controlId)
        {
            var main = MainOfTab(controlId);
            return main != null && All.Any(r => string.Equals(r.MainName, main, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The table's name for the parts a list shows: the sub category of the first product with one.</summary>
        public static string ListName(IEnumerable<int> productIds)
        {
            foreach (var id in productIds)
            {
                var n = Name(SubOf(id));
                if (!string.IsNullOrEmpty(n) && n != "None") return n;
            }
            return null;
        }
    }
}
