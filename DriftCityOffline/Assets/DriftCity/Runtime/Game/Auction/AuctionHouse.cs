using System.Collections.Generic;
using System.Linq;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.Game
{
    /// <summary>
    /// The Auction House window AUCTION (game.nui 1300000; DriftCity_RE\SHOPS.md section 3), opened by the Driver Dome's
    /// auction building (parking-lot area 4, 0x763840 -> 0x598f60) and the shop list:
    ///  - CHK_ITEM / CHK_VEHICLE switch between items and cars (0x598930); CATEGORY is the item combo All Items, Speed /
    ///    Durability / Acceleration / Booster Parts, General Items (10493-10498), or All / V1..V9 for cars;
    ///  - INPUT_LEVELFROM / LEVELTO, USEABLEITEM (only what the driver's level allows), USER_SEARCHTEXT + SEARCHBUTTON and
    ///    REFRESH search again (0x598670); the SORT_ header buttons sort by No / level, item name, price, quantity, seller,
    ///    credit (a second click reverses);
    ///  - ITEMLIST shows 13 rows a page (0x5990c0): required level (red when above the driver's, FF8F0700 / FF301C08), name
    ///    ("+N" / broken, options), price, quantity, seller, credit; PAGE1..10 and PREV/NEXTPAGE page through;
    ///  - hovering a row shows ITEMINFO (items) or AUCTIONKEYINFO 1350000 (cars, 0x624320); ITEMMITO shows the selected
    ///    row's price, MYMITO the driver's Mito; BUTTON_BUY (or a double click) asks 10505 / 10506 and buys (0x599ac0:
    ///    inventory 10269 / 10581, Mito 10500, your own listing 10499).
    /// Registering happens from the inventory's Auction page (InventoryWindow / AuctionPage) through AUCTIONITEM 1760000.
    /// </summary>
    public static class AuctionHouse
    {
        static PlayerProfile P => PlayerProfile.Current;
        static UiManager Ui => UiManager.Instance;

        static NuiView view;
        static readonly List<Row> rows = new List<Row>();
        static List<AuctionListing> shown = new List<AuctionListing>();
        static AuctionMarket.Query query;
        static int selected = -1, pages = 1, total;
        static bool vehicles;
        static float lastClick;
        static int lastClicked = -1;

        public static bool IsOpen => view;
        public static NuiView View => view;
        public static IReadOnlyList<AuctionListing> Shown => shown;
        public static int Selected => selected;
        public static int Pages => pages;
        public static int Total => total;

        static readonly string[] ItemCategories = { "10493", "10494", "10495", "10496", "10497", "10498" };
        static readonly Color LevelHigh = NuiControlView.ParseColor("FF8F0700", Color.red), LevelOk = NuiControlView.ParseColor("FF301C08", Color.black);
        static readonly Color TextColor = new Color(0.19f, 0.11f, 0.03f), SelectedBack = NuiControlView.ParseColor("FFF5C725", Color.yellow);

        sealed class Row
        {
            public RectTransform Rt;
            public Image Back;
            public Text Level, Name, Price, Count, User, Credit;
            public RectTransform Icon;
        }

        public static void Open()
        {
            if (P == null) return;
            if (P.Level < AuctionMarket.MinLevel) { UiPopup.Message(MissionText.Get("11828", AuctionMarket.MinLevel)); return; }
            view = OriginalMenus.OpenScreen(1300000);
            if (!view) return;
            OriginalMenus.OnClose(() => { view = null; rows.Clear(); shown.Clear(); AuctionKeyInfo.Hide(); ItemTooltip.Hide(); });
            AudioLibrary.PlaySfx("sfx/SID_AUCTION_ENTRANCE");
            vehicles = false;
            query = new AuctionMarket.Query { Sort = "", Page = 0 };
            selected = -1;
            // the NPC at the counter (globaltext B0..B2_IntroName / IntroText)
            int npc = Random.Range(0, 3);
            view.SetText("ID_AUCTION_CHARNAME", MissionText.Get($"B{npc}_IntroName"));
            view.SetText("ID_AUCTION_CHARTALK", MissionText.Get($"B{npc}_IntroText"));
            view.SetVisible("ID_AUCTION_CHARIMG", true);

            Ui.On("ID_AUCTION_BUTTON_EXIT", _ => OriginalMenus.Close());
            Ui.On("ID_AUCTION_BUTTON_REFRESH", _ => { AudioLibrary.PlaySfx("sfx/SID_AUCTION_REFRESH"); Search(query.Page); });
            Ui.On("ID_AUCTION_USER_SEARCHBUTTON", _ => Search(0));
            Ui.On("ID_AUCTION_BUTTON_PREVPAGE", _ => { if (query.Page > 0) Search(query.Page - 1); });
            Ui.On("ID_AUCTION_BUTTON_NEXTPAGE", _ => { if (query.Page < pages - 1) Search(query.Page + 1); });
            for (int i = 1; i <= 10; i++) { int k = i - 1; Ui.On("ID_AUCTION_BUTTON_PAGE" + i, _ => Search(PageBase() + k)); }
            Ui.On("ID_AUCTION_BUTTON_BUY", _ => AskBuy());
            Ui.On("ID_AUCTION_CHK_ITEM", _ => SetVehicles(false));
            Ui.On("ID_AUCTION_CHK_VEHICLE", _ => SetVehicles(true));
            Ui.On("ID_AUCTION_USEABLEITEM", _ => Search(0));
            Ui.On("ID_AUCTION_CATEGORY", _ => { query.Category = (query.Category + 1) % CategoryCount; ShowCategory(); Search(0); });
            foreach (var (id, sort) in new[] { ("NO", "no"), ("GRADE", "level"), ("ITEMNAME", "name"), ("PRICE", "price"), ("ITEMCOUNT", "count"), ("USER", "user"), ("CREDIT", "credit") })
            {
                string s = sort;
                Ui.On("ID_AUCTION_SORT_" + id, _ => { query.Desc = query.Sort == s && !query.Desc; query.Sort = s; Search(query.Page); });
            }
            // AuctionCheckType = 0 (globalcontent): the level column, not the running number
            view.SetVisible("ID_AUCTION_SORT_NO", false);
            foreach (var id in new[] { "ID_AUCTION_INPUT_LEVELFROM", "ID_AUCTION_INPUT_LEVELTO" })
            {
                var input = view.Control(id)?.Input;
                if (input) { input.contentType = InputField.ContentType.IntegerNumber; input.characterLimit = 3; }
            }
            var search = view.Control("ID_AUCTION_USER_SEARCHTEXT")?.Input;
            if (search) search.characterLimit = 16;
            BuildRows();
            SetVehicles(false);
        }

        static int CategoryCount => vehicles ? 10 : ItemCategories.Length;
        static int PageBase() => query.Page / 10 * 10;

        static void SetVehicles(bool cars)
        {
            vehicles = cars;
            query.Vehicles = cars;
            query.Category = 0;
            Check("ID_AUCTION_CHK_ITEM", !cars);
            Check("ID_AUCTION_CHK_VEHICLE", cars);
            ShowCategory();
            Search(0);
        }

        static void Check(string id, bool on)
        {
            var c = view ? view.Control(id) : null;
            if (!c) return;
            c.SetChecked(on);
        }

        static void ShowCategory()
        {
            var c = view ? view.Control("ID_AUCTION_CATEGORY") : null;
            if (!c) return;
            string text = vehicles ? (query.Category == 0 ? MissionText.Get("10493") : $"V{query.Category}") : MissionText.Get(ItemCategories[Mathf.Clamp(query.Category, 0, ItemCategories.Length - 1)]);
            var label = OriginalMenus.Caption(c.transform, "caption", text, "Calibri 13 700", Color.white, TextAnchor.MiddleLeft);
            label.rectTransform.offsetMin = new Vector2(8, 0);
        }

        static int Int(string id) => int.TryParse(view ? view.GetInput(id) : "", out int n) ? Mathf.Max(0, n) : 0;

        /// <summary>The window's search (0x598670): category, level range (the usable-items box caps it at the driver's level), seller.</summary>
        public static void Search(int page)
        {
            if (!view) return;
            query.Page = Mathf.Max(0, page);
            query.LevelFrom = Int("ID_AUCTION_INPUT_LEVELFROM");
            query.LevelTo = Int("ID_AUCTION_INPUT_LEVELTO");
            var useable = view.Control("ID_AUCTION_USEABLEITEM");
            if (useable && useable.Checked && (query.LevelTo == 0 || query.LevelTo > P.Level)) query.LevelTo = P.Level;
            query.Seller = view.GetInput("ID_AUCTION_USER_SEARCHTEXT");
            AuctionMarket.List(query, page2 =>
            {
                if (!view) return;
                shown = page2.Rows;
                pages = page2.Count;
                total = page2.Total;
                query.Page = page2.Index;
                selected = shown.Count > 0 ? Mathf.Clamp(selected, 0, shown.Count - 1) : -1;
                Refresh();
            });
        }

        // ---------------- rows ----------------
        static void BuildRows()
        {
            rows.Clear();
            var list = view.Control("ID_AUCTION_ITEMLIST");
            if (!list) return;
            // the header buttons give the columns (dialog x): level 344, name 397-646, price 646, quantity 720, seller 815, credit 900
            float x0 = ((RectTransform)list.transform).anchoredPosition.x;
            float Col(string id, float fallback) { var c = view.Control(id); return c ? ((RectTransform)c.transform).anchoredPosition.x - x0 : fallback; }
            float level = Col("ID_AUCTION_SORT_GRADE", 2), price = Col("ID_AUCTION_SORT_PRICE", 304), count = Col("ID_AUCTION_SORT_ITEMCOUNT", 378),
                  user = Col("ID_AUCTION_SORT_USER", 473), credit = Col("ID_AUCTION_SORT_CREDIT", 558);
            for (int i = 0; i < AuctionMarket.PageRows; i++)
            {
                var rt = NuiView.NewRect("row" + i, list.transform);
                rt.anchoredPosition = new Vector2(0, -(2 + 31 * i));
                rt.sizeDelta = new Vector2(651, 30);
                var row = new Row { Rt = rt, Back = rt.gameObject.AddComponent<Image>() };
                row.Back.color = new Color(0, 0, 0, 0.001f);
                Text Cell(string name, float x, float w, TextAnchor a)
                {
                    var c = NuiView.NewRect(name, rt);
                    c.anchoredPosition = new Vector2(x, 0);
                    c.sizeDelta = new Vector2(w, 30);
                    var t = OriginalMenus.Caption(c, "t", "", "Calibri 12 700", TextColor, a);
                    if (t.TryGetComponent<Shadow>(out var sh)) sh.enabled = false;
                    return t;
                }
                row.Level = Cell("level", level, 53, TextAnchor.MiddleCenter);
                row.Icon = NuiView.NewRect("icon", rt);
                row.Icon.anchoredPosition = new Vector2(level + 58, -2);
                row.Icon.sizeDelta = new Vector2(26, 26);
                row.Name = Cell("name", level + 88, price - level - 92, TextAnchor.MiddleLeft);
                row.Price = Cell("price", price - 4, count - price + 2, TextAnchor.MiddleRight);
                row.Count = Cell("count", count, user - count, TextAnchor.MiddleCenter);
                row.User = Cell("user", user, credit - user, TextAnchor.MiddleCenter);
                row.Credit = Cell("credit", credit, 651 - credit, TextAnchor.MiddleCenter);
                int index = i;
                OriginalMenus.Clickable(rt, () => ClickRow(index), () => HoverRow(index, rt));
                var exit = rt.gameObject.AddComponent<UnityEngine.EventSystems.EventTrigger>();
                var entry = new UnityEngine.EventSystems.EventTrigger.Entry { eventID = UnityEngine.EventSystems.EventTriggerType.PointerExit };
                entry.callback.AddListener(_ => { ItemTooltip.Hide(); AuctionKeyInfo.Hide(); });
                exit.triggers.Add(entry);
                rows.Add(row);
            }
        }

        /// <summary>Clicks a row (tests use it too): selects it; a second click within half a second buys.</summary>
        public static void ClickRow(int i)
        {
            if (i < 0 || i >= shown.Count) return;
            bool again = i == lastClicked && Time.unscaledTime - lastClick < 0.5f;
            lastClick = Time.unscaledTime; lastClicked = i;
            selected = i;
            Refresh();
            if (again) AskBuy();
        }

        static void HoverRow(int i, RectTransform near)
        {
            if (i < 0 || i >= shown.Count) return;
            var l = shown[i];
            if (l.Goods == null) return;
            if (l.Goods.IsCar) AuctionKeyInfo.Show(l, near);
            else ItemTooltip.Show(l.Goods.Entry(), near);
        }

        static void Refresh()
        {
            if (!view) return;
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var l = i < shown.Count ? shown[i] : null;
                row.Rt.gameObject.SetActive(l != null);
                if (l == null) continue;
                bool sel = i == selected;
                row.Back.color = sel ? SelectedBack : new Color(0, 0, 0, 0.001f);
                row.Level.text = l.Level.ToString();
                row.Level.color = P.Level < l.Level ? LevelHigh : LevelOk;
                row.Name.text = l.Goods != null ? (l.Goods.IsCar ? $"{l.Goods.Name()} V{l.Goods.Car.Grade + 1}" : l.Goods.Name()) : l.Name;
                row.Price.text = l.Price > 0 ? $"{l.Price:N0}" : $"{System.Math.Max(l.Bid, l.Start):N0}*";
                row.Count.text = l.Count.ToString();
                row.User.text = l.Seller;
                row.Credit.text = l.Credit.ToString();
                foreach (var t in new[] { row.Name, row.Price, row.Count, row.User, row.Credit }) t.color = TextColor;
                var e = l.Goods?.Entry();
                if (l.Goods != null && l.Goods.IsCar)
                {
                    OriginalMenus.Remove(row.Icon, "item");
                    var tex = OriginalMenus.CarImage(OriginalMenus.CarKeys, l.Goods.CarInfo, out var uv);
                    var rt = NuiView.NewRect("item", row.Icon);
                    rt.sizeDelta = new Vector2(26, 26);
                    var ri = rt.gameObject.AddComponent<RawImage>();
                    ri.texture = tex; ri.uvRect = uv; ri.raycastTarget = false;
                    ri.enabled = tex;
                }
                else OriginalMenus.ItemTile(row.Icon, Vector2.zero, new Vector2(26, 26), e?.Icon, "", e != null ? ItemCatalog.Swatch(e) : null).GetComponent<Graphic>().raycastTarget = false;
            }
            view.SetText("ID_AUCTION_MYMITO", $"{P.Mito:N0}");
            var cur = selected >= 0 && selected < shown.Count ? shown[selected] : null;
            view.SetText("ID_AUCTION_ITEMMITO", cur != null ? $"{(cur.Price > 0 ? cur.Price : System.Math.Max(cur.Bid, cur.Start)):N0}" : "0");
            view.SetEnabled("ID_AUCTION_BUTTON_BUY", cur != null && !cur.Mine && cur.Price > 0);
            view.SetEnabled("ID_AUCTION_BUTTON_PREVPAGE", query.Page > 0);
            view.SetEnabled("ID_AUCTION_BUTTON_NEXTPAGE", query.Page < pages - 1);
            int b = PageBase();
            for (int i = 1; i <= 10; i++)
            {
                int pg = b + i - 1;
                string id = "ID_AUCTION_BUTTON_PAGE" + i;
                view.SetVisible(id, pg < pages);
                view.SetText(id, (pg + 1).ToString());
                var c = view.Control(id);
                if (c) c.Selected = pg == query.Page;
            }
        }

        // ---------------- buying ----------------
        static void AskBuy()
        {
            var l = selected >= 0 && selected < shown.Count ? shown[selected] : null;
            if (l == null) return;
            if (l.Mine) { UiPopup.Message(MissionText.Get("10499")); return; }
            if (l.Price <= 0) { UiPopup.Message("This item can only be bid on."); return; }
            if (P.Mito < l.Price) { UiPopup.Message(MissionText.Get("10500")); return; }
            if (l.Goods != null && l.Goods.IsCar && P.Level < l.Level) { UiPopup.Message(MissionText.Get("10580", l.Category, l.Level)); return; }
            if (l.Goods != null && l.Goods.IsCar && P.Cars.Count >= 40) { UiPopup.Message(MissionText.Get("10582")); return; }
            if (l.Goods != null && l.Goods.Kind == "part" && P.PerfParts.Count >= PerfRules.MaxParts) { UiPopup.Message(MissionText.Get("10269")); return; }
            string text = MissionText.Get(l.Goods != null && l.Goods.IsCar ? "10506" : "10505") + $"\n{l.Name}  {l.Price:N0} Mito";
            UiPopup.Show(text, id =>
            {
                if (id != UiPopup.Yes) return;
                AuctionMarket.Buy(l, (ok, err) =>
                {
                    if (!ok) { UiPopup.Message(string.IsNullOrEmpty(err) ? "The purchase failed." : err); Search(query.Page); return; }
                    AudioLibrary.PlaySfx("sfx/SID_ITEM_BUY");
                    ChatBox.System($"Bought {l.Name} for {l.Price:N0} Mito at the Auction House.");
                    if (l.Goods != null && l.Goods.IsCar) ChatBox.System("The car is waiting in your garage.");
                    OriginalMenus.RefreshInventoryIfOpen();
                    GameFlow.Instance?.RefreshHud();
                    Search(query.Page);
                });
            }, UiPopup.Yes, UiPopup.No);
        }

        /// <summary>Buys the selected row without the confirmation (tests).</summary>
        public static void BuySelected(System.Action<bool, string> done)
        {
            var l = selected >= 0 && selected < shown.Count ? shown[selected] : null;
            if (l == null) { done?.Invoke(false, "nothing selected"); return; }
            AuctionMarket.Buy(l, (ok, err) => { Search(query.Page); done?.Invoke(ok, err); });
        }

        public static void Select(int i) { selected = i; Refresh(); }
    }

    /// <summary>AUCTIONKEYINFO 1350000: the car-key hover box of the auction lists (0x626d67): name, price, required level,
    /// factory colour, whether it may be listed again, the car's stats and bonus, distance.</summary>
    public static class AuctionKeyInfo
    {
        static NuiView view;

        public static void Show(AuctionListing l, RectTransform near)
        {
            Hide();
            var ui = UiManager.Instance;
            if (!ui || l?.Goods?.Car == null) return;
            view = ui.Open(1350000);
            if (!view) return;
            var car = l.Goods.Car;
            var info = l.Goods.CarInfo;
            var g = ShopData.Grade(info, car.Grade);
            view.SetText("ID_AUCTIONKEYINFO_NAME", $"{l.Goods.Name()} V{car.Grade + 1}");
            view.SetText("ID_AUCTIONKEYINFO_RIDESTATE", $"{(l.Price > 0 ? l.Price : l.Start):N0} mito");
            view.SetText("ID_AUCTIONKEYINFO_REQLEVEL", MissionText.Get("10486", l.Level));
            view.SetText("ID_AUCTIONKEYINFO_ABLETOREGISTER", MissionText.Get("10585"));   // cars bought here can't be listed again
            var st = ShopData.Stats(info, null);
            view.SetText("ID_AUCTIONKEYINFO_SPECINFO", $"{MissionText.Get("10800")} {st.speed}  {MissionText.Get("10802")} {st.accel}");
            view.SetText("ID_AUCTIONKEYINFO_SLOTINFO", $"{MissionText.Get("10801")} {st.crash}  {MissionText.Get("10803")} {st.boost}");
            view.SetText("ID_AUCTIONKEYINFO_TOTALKM", g != null ? $"Class {g.GradeType}{g.GradeLvl}" : "");
            view.SetText("ID_AUCTIONKEYINFO_PRICE", $"{l.Seller}");
            var sw = view.Control("ID_AUCTIONKEYINFO_COLOR");
            if (sw) foreach (var ri in sw.GetComponentsInChildren<RawImage>()) ri.color = CarFactory.FactoryColor(info, car.Color);
            // beside the row, inside the screen
            var root = (RectTransform)view.transform.Find("origin");
            if (root && near)
            {
                var corners = new Vector3[4];
                near.GetWorldCorners(corners);
                root.position = corners[2] + new Vector3(8, 0, 0);
            }
        }

        public static void Hide() { if (view) UiManager.Instance?.Close(view); view = null; }
    }
}
