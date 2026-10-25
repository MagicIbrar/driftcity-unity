using System;
using System.Collections.Generic;
using DriftCity.Core;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.Game
{
    /// <summary>
    /// The help book, HELPADV 1930000 (DriftCity_RE/OPTIONS.md, built by 0x5448f0). A tree of six categories
    /// (helpindex.xlt Category0-5, collapsible with the MINUS/PLUS bars) over 21 chapters (Index0-20, one button each);
    /// a chapter is localize\h{n}.xlt: Title, ShowPicture / PictureIndex (0 the key chart IMG_KEYCTRL, 1 the combo chart
    /// IMG_COMBO) and MaxLine lines Line0.. shown in the CONTENTS box. PREV / NEXT step through the chapters.
    /// </summary>
    public static class HelpWindow
    {
        public const int Dialog = 1930000;

        /// <summary>The chapter buttons in index order (Index0..20 = h1..h21), as 0x5448f0 binds them.</summary>
        static readonly string[] Buttons =
        {
            "CAR_CONTROL", "CAR_CHANGE",
            "COMM", "COMM_CHAT", "COMM_EMOT", "COMM_PARTY", "COMM_TMCREATE", "COMM_TEAMJOIN",
            "MISSION_QUEST", "MISSION_QUICK", "MISSION_CHASE",
            "GAME_COMBO", "GAME_BATTLEZONE", "GAME_CHANNELPLAY", "ITEM_CHANNELBATTLE", "GAME_AUCTION",
            "ITEM_REMODEL", "ITEM_PART",
            "SHOP_USE", "SHOP_CAR", "SHOP_USE_PARTSHOP",
        };
        /// <summary>First chapter of each category (Category0..5) and the end.</summary>
        static readonly int[] CategoryStart = { 0, 2, 8, 11, 16, 18, 21 };

        public sealed class Chapter
        {
            public string Title = "";
            public bool Picture;
            public int PictureIndex;
            public List<string> Lines = new List<string>();
        }

        static List<Chapter> chapters;
        static string[] indexTitles, categoryTitles;
        static readonly bool[] collapsed = new bool[6];
        static NuiView view;
        static int current;

        public static bool IsOpen => view;
        public static int Current => current;
        public static NuiView View => view;
        public static int ChapterCount => Buttons.Length;

        public static Chapter Get(int index) { Load(); return index >= 0 && index < chapters.Count ? chapters[index] : null; }

        static string Value(XltTable t, string[] row)
        {
            var v = t.Get(row, "Value_US");
            if (string.IsNullOrWhiteSpace(v)) v = t.Get(row, "Value");
            return v ?? "";
        }

        static Dictionary<string, string> ReadKeys(string file)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (DriftCityData.Files == null || !DriftCityData.Files.Exists(file)) return d;
                var t = XltTable.Parse(DriftCityData.Files.Read(file));
                foreach (var r in t.Rows)
                {
                    var k = t.Get(r, "Key");
                    if (!string.IsNullOrEmpty(k)) d[k.Trim()] = Value(t, r);
                }
            }
            catch (Exception ex) { Debug.LogWarning(file + ": " + ex.Message); }
            return d;
        }

        static void Load()
        {
            if (chapters != null) return;
            var index = ReadKeys(@"localize\helpindex.xlt");
            indexTitles = new string[Buttons.Length];
            for (int i = 0; i < Buttons.Length; i++) indexTitles[i] = index.TryGetValue("Index" + i, out var s) ? s.Trim() : Buttons[i];
            categoryTitles = new string[6];
            for (int i = 0; i < 6; i++) categoryTitles[i] = index.TryGetValue("Category" + i, out var s) ? s.Trim() : "";
            chapters = new List<Chapter>();
            for (int i = 0; i < Buttons.Length; i++)
            {
                var keys = ReadKeys($@"localize\h{i + 1}.xlt");
                var c = new Chapter { Title = keys.TryGetValue("Title", out var t) ? t.Trim() : indexTitles[i] };
                c.Picture = keys.TryGetValue("ShowPicture", out var sp) && sp.Trim() == "1";
                c.PictureIndex = keys.TryGetValue("PictureIndex", out var pi) && int.TryParse(pi.Trim(), out int n) ? n : 0;
                int max = keys.TryGetValue("MaxLine", out var ml) && int.TryParse(ml.Trim(), out int m) ? m : 0;
                for (int l = 0; l < max; l++) c.Lines.Add(keys.TryGetValue("Line" + l, out var line) ? line.TrimEnd() : "");
                while (c.Lines.Count > 0 && string.IsNullOrWhiteSpace(c.Lines[c.Lines.Count - 1])) c.Lines.RemoveAt(c.Lines.Count - 1);
                chapters.Add(c);
            }
        }

        /// <summary>Opens the help on a chapter (the quit menu's Help button, F1 in the client).</summary>
        public static void Open(int chapter = 0)
        {
            Load();
            if (!view)
            {
                view = OriginalMenus.OpenScreen(Dialog);
                if (!view) return;
                OriginalMenus.OnClose(() => view = null);
                var ui = UiManager.Instance;
                ui.On("ID_HELPADV_CLOSE", _ => Close());
                ui.On("ID_HELPADV_PREV", _ => Show(current - 1));
                ui.On("ID_HELPADV_NEXT", _ => Show(current + 1));
                for (int i = 0; i < Buttons.Length; i++) { int n = i; ui.On("ID_HELPADV_" + Buttons[i], _ => Show(n)); }
                for (int c = 0; c < 6; c++)
                {
                    int cat = c;
                    ui.On("ID_HELPADV_MINUS" + c, _ => { collapsed[cat] = true; Layout(); });
                    ui.On("ID_HELPADV_PLUS" + c, _ => { collapsed[cat] = false; Layout(); });
                }
                view.SetVisible("ID_HELPADV_MENU_SCROLL", false);   // the whole tree fits the menu column
                for (int c = 0; c < 6; c++)
                {
                    var head = view.Control("ID_HELPADV_INDEX0" + (c + 1));
                    if (head && !string.IsNullOrEmpty(categoryTitles[c])) head.SetText(categoryTitles[c]);
                }
                // one line per button (176 px): long titles keep their first part ("Auction House / Auction Registration")
                for (int i = 0; i < Buttons.Length; i++)
                {
                    var title = indexTitles[i];
                    if (title.Length > 26 && title.Contains(" / ")) title = title.Substring(0, title.IndexOf(" / "));
                    view.SetText("ID_HELPADV_" + Buttons[i], "   " + title);
                }
            }
            Layout();
            Show(chapter);
        }

        public static void Close()
        {
            if (!view) return;
            OriginalMenus.Close();
            view = null;
        }

        /// <summary>Shows a chapter: title, its picture or its lines, the tree selection and PREV / NEXT.</summary>
        public static void Show(int chapter)
        {
            if (!view) return;
            chapter = Mathf.Clamp(chapter, 0, Buttons.Length - 1);
            current = chapter;
            var c = chapters[chapter];
            // a chapter in a collapsed category opens it
            for (int k = 0; k < 6; k++) if (chapter >= CategoryStart[k] && chapter < CategoryStart[k + 1] && collapsed[k]) { collapsed[k] = false; Layout(); }
            view.SetText("ID_HELPADV_CHAPTER", c.Title);
            view.SetVisible("ID_HELPADV_IMG_KEYCTRL", c.Picture && c.PictureIndex == 0);
            view.SetVisible("ID_HELPADV_IMG_COMBO", c.Picture && c.PictureIndex == 1);
            var box = view.Control("ID_HELPADV_CONTENTS");
            if (box)
            {
                // the CONTENTS list box draws one row per line (LI=7) in Calibri 12; light text on the ADV book's dark page
                foreach (var t in box.GetComponentsInChildren<Text>(true)) if (t.name != "help text") t.text = "";
                var label = OriginalMenus.Caption(box.transform, "help text", c.Picture ? "" : string.Join("\n", c.Lines), "Calibri 12 400", new Color(0.87f, 0.85f, 0.8f), TextAnchor.UpperLeft, true);
                var rt = (RectTransform)label.transform;
                var size = ((RectTransform)box.transform).sizeDelta;
                rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
                rt.pivot = new Vector2(0, 1);
                rt.anchoredPosition = new Vector2(8, -12);
                rt.sizeDelta = new Vector2(Mathf.Max(200f, size.x > 0 ? size.x - 16 : 605f), Mathf.Max(200f, size.y > 0 ? size.y - 16 : 404f));
                label.lineSpacing = 1.15f;
                label.verticalOverflow = VerticalWrapMode.Truncate;
            }
            for (int i = 0; i < Buttons.Length; i++)
            {
                var b = view.Control("ID_HELPADV_" + Buttons[i]);
                if (b) b.Selected = i == chapter;
            }
            view.SetEnabled("ID_HELPADV_PREV", chapter > 0);
            view.SetEnabled("ID_HELPADV_NEXT", chapter < Buttons.Length - 1);
        }

        /// <summary>
        /// Lays the tree out top to bottom: each category's MINUS (open) or PLUS (closed) bar with its title, its
        /// chapter buttons 17 px apart when open, and a LINE separator after it (close to the dialog's own spacing, which
        /// has 18 / 27 px but one chapter fewer in the tree: Channel Battle lies outside it in game.nui).
        /// </summary>
        static void Layout()
        {
            if (!view) return;
            float y = 68f;
            for (int c = 0; c < 6; c++)
            {
                bool open = !collapsed[c];
                Place("ID_HELPADV_MINUS" + c, -1, y, open);
                Place("ID_HELPADV_PLUS" + c, -1, y, !open);
                Place("ID_HELPADV_INDEX0" + (c + 1), 21, y + 4, true);
                float last = y + 4;
                for (int i = CategoryStart[c]; i < CategoryStart[c + 1]; i++)
                {
                    float by = last + 17;
                    Place("ID_HELPADV_" + Buttons[i], 2, by, open);
                    if (open) last = by;
                }
                if (c < 5) Place("ID_HELPADV_LINE" + c, 0, last + 20, true);
                y = last + 25 + (open ? 0 : 4);
            }
        }

        static void Place(string id, float x, float y, bool visible)
        {
            var c = view.Control(id);
            if (!c) return;
            c.gameObject.SetActive(visible);
            ((RectTransform)c.transform).anchoredPosition = new Vector2(x, -y);
        }
    }
}
