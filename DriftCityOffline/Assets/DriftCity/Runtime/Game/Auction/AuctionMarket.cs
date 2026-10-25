using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>What a listing sells: a bag stack, a shop item (paint), a performance part or a whole car (src/goods.js).</summary>
    [Serializable]
    public sealed class AuctionGoods
    {
        public string Kind = "stack";   // stack | shop | part | car
        public string Id;               // stack item id
        public int ItemId;              // shop: vshopitem UniqueId
        public int Count = 1;
        public int Type, Grade, Enhance; // part
        public OwnedCar Car;            // car

        public bool IsCar => Kind == "car";

        /// <summary>The goods as a bag entry, for names, icons and the item tooltip (cars have none).</summary>
        public BagEntry Entry()
        {
            switch (Kind)
            {
                case "stack": return new BagEntry { Kind = BagKind.Stack, Id = Id, Count = Count };
                case "shop": return new BagEntry { Kind = BagKind.Shop, ItemId = ItemId, Count = Count };
                case "part": return new BagEntry { Kind = BagKind.Part, Part = new PerfPart { Uid = -1, Type = Type, Grade = Grade, Enhance = Enhance } };
            }
            return null;
        }

        public VehicleInfo CarInfo => Car != null ? GameTables.Car(Car.VehicleIndex) : null;

        public string Name()
        {
            if (IsCar) { var info = CarInfo; return info == null ? "Car" : (ShopData.Grade(info, Car.Grade)?.UiName ?? info.UiName); }
            var e = Entry();
            return e == null ? "" : e.Name;
        }

        /// <summary>The window's category numbers: items 1 speed, 2 durability, 3 accel, 4 booster parts, 5 general items (10494-10498);
        /// cars their class 1-9.</summary>
        public int Category => IsCar ? Mathf.Clamp(CarInfo?.Grade ?? 1, 1, 9) : Kind == "part" ? new[] { 1, 3, 2, 4 }[Mathf.Clamp(Type, 0, 3)] : 5;

        public int Level => IsCar ? (CarInfo?.ReqLevel ?? 1) : Kind == "part" ? (Grade < PerfRules.ReqLevel.Length ? PerfRules.ReqLevel[Grade] : 60) : 1;

        /// <summary>What the goods are worth to the offline market (the shops' prices).</summary>
        public long Value()
        {
            switch (Kind)
            {
                case "car": { var info = CarInfo; return info == null ? 100000 : ShopData.CarPrice(info) + (ShopData.Grade(info, Car.Grade)?.Sell ?? 0); }
                case "part": return (long)(PerfRules.ShopPrice[Mathf.Clamp(Grade, 0, 2)] * (Grade >= 3 ? 3f : 1f) * (1f + 0.15f * Enhance));
                case "shop": return Math.Max(1000, ItemCatalog.Value(Entry())) * Math.Max(1, Count);
                default:
                    foreach (var (id, price) in PerfRules.Counter) if (id == Id) return (long)price * Math.Max(1, Count);
                    long unit = ItemCatalog.ShopByName(Id)?.MitoPrice ?? 0;
                    if (unit <= 0) unit = Math.Max(1500, ItemUse.UnitSellPrice(new BagEntry { Kind = BagKind.Stack, Id = Id, Count = 1 }) * 2);
                    return unit * Math.Max(1, Count);
            }
        }

        public Dictionary<string, object> ToJson()
        {
            var d = new Dictionary<string, object> { ["kind"] = Kind, ["count"] = Count };
            if (Kind == "stack") d["id"] = Id;
            else if (Kind == "shop") d["itemId"] = ItemId;
            else if (Kind == "part") { d["type"] = Type; d["grade"] = Grade; d["enhance"] = Enhance; }
            return d;
        }

        public static AuctionGoods FromJson(Dictionary<string, object> o)
        {
            if (o == null) return null;
            var g = new AuctionGoods { Kind = o.S("kind", "stack"), Id = o.S("id"), ItemId = o.I("itemId"), Count = Math.Max(1, o.I("count", 1)), Type = o.I("type"), Grade = o.I("grade"), Enhance = o.I("enhance") };
            if (g.IsCar)
            {
                var c = o.O("car");
                g.Car = new OwnedCar { VehicleIndex = c.I("VehicleIndex"), Grade = c.I("Grade"), Color = c.I("Color"), PaintItem = c.I("PaintItem", -1), BodyVariant = c.I("BodyVariant", 1) };
            }
            return g;
        }
    }

    /// <summary>One listing of the Auction House (a row of the AUCTION window or of the inventory's Auction page).</summary>
    [Serializable]
    public sealed class AuctionListing
    {
        public int Id;
        public string Seller = "";
        public bool Npc;
        public string Name = "";
        public int Category, Level = 1, Count = 1;
        public long Price, Start, Bid;
        public string Bidder = "";
        public long Fee, CreatedAt, EndsAt;
        public string Status = "open";   // open | sold | expired | won | cancelled | taken
        public string Buyer = "";
        public long SoldPrice, ClosedAt;
        public int Credit;
        public AuctionGoods Goods;
        /// <summary>Offline: when an AI buyer takes the player's listing (0 = nobody will).</summary>
        public long SellAt;

        public bool Mine => !Npc && PlayerProfile.Current != null && Seller == PlayerProfile.Current.Name;
        /// <summary>Goods that wait for this driver to take them (expired, cancelled with a full bag, won).</summary>
        public bool Waiting => Status == "expired" || Status == "cancelled" || Status == "won";

        public static AuctionListing FromJson(Dictionary<string, object> o) => new AuctionListing
        {
            Id = o.I("id"), Seller = o.S("seller", ""), Npc = o.B("npc"), Name = o.S("name", ""), Category = o.I("category"), Level = o.I("level", 1), Count = o.I("count", 1),
            Price = (long)o.N("price"), Start = (long)o.N("start"), Bid = (long)o.N("bid"), Bidder = o.S("bidder", ""), Fee = (long)o.N("fee"),
            CreatedAt = (long)o.N("createdAt"), EndsAt = (long)o.N("endsAt"), Status = o.S("status", "open"), Buyer = o.S("buyer", ""),
            SoldPrice = (long)o.N("soldPrice"), ClosedAt = (long)o.N("closedAt"), Credit = o.I("credit"), Goods = AuctionGoods.FromJson(o.O("goods")),
        };
    }

    /// <summary>
    /// The Auction House's data (DriftCity_RE\SHOPS.md section 3). Online every call goes to the server (src/auction.js),
    /// which holds the goods and the Mito. Offline an AI market stands in: AI drivers list parts, materials, paints and cars
    /// from the game's tables at the shops' prices give or take, and buy the player's listings when the price is fair
    /// (the cheaper, the sooner); the Mito arrives as a note from the Auction House like online. The offline market is saved
    /// next to the driver profile (auction_&lt;slot&gt;.json).
    /// </summary>
    public static class AuctionMarket
    {
        static PlayerProfile P => PlayerProfile.Current;
        public static bool Online => OnlineSession.Active;

        public const int PageRows = 13;          // 0x5990c0
        public const int MaxListings = 60;       // 0x5c9c62 (10440)
        public const long MaxPrice = 2000000000; // 0x5517ce
        public const int Days = 7;               // 10444
        public const int MinLevel = 1;           // globalcontent AuctionMinLevel
        const long Hour = 3600000L, Day = 24 * Hour;

        public struct Query
        {
            public bool Vehicles;
            public int Category, LevelFrom, LevelTo, Page;
            public string Seller, Sort;
            public bool Desc;
        }

        public struct Page { public List<AuctionListing> Rows; public int Index, Count, Total; }

        public static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        /// <summary>The registration tax in percent: 10 less the mounted licence's fee discount (0x5c9e61).</summary>
        public static float FeePct => 10f - 10f * (P != null ? Licences.Bonus(P, "ME_AuctionFeeDis", "ME_FeeDis") : 0) / 100f;
        public static long Fee(long price) => (long)Math.Floor(price * FeePct / 100f);

        static void Reply(Dictionary<string, object> r, Action<bool, string> done) => done?.Invoke(r != null && r.B("ok"), r?.S("error"));

        static NetClient hooked;
        /// <summary>Listens to the server's auction / gift pushes once the market is used (sale, outbid, gift arrived: chat lines).</summary>
        public static void Hook()
        {
            var s = OnlineSession.Instance;
            if (!s || !s.Net || hooked == s.Net) return;
            if (hooked) hooked.Message -= OnPush;
            hooked = s.Net;
            hooked.Message += OnPush;
        }

        static void OnPush(string t, Dictionary<string, object> m)
        {
            if (t == "auction.sold") ChatBox.System($"Your {m.S("name", "item")} was sold to {m.S("buyer", "")} for {(long)m.N("price"):N0} Mito at the Auction House. The Mito is in your notes.");
            else if (t == "auction.outbid") ChatBox.System($"You were outbid on {m.S("name", "an item")}: {(long)m.N("refund"):N0} Mito came back by note.");
            else if (t == "gift.new") ChatBox.System($"{m.S("from", "Someone")} sent you a gift: {m.S("name", "")}. Accept it from the custom shop's trade history (Received Gifts).");
        }

        // ================= window queries =================
        public static void List(Query q, Action<Page> done)
        {
            if (Online)
            {
                Hook();
                var body = new Dictionary<string, object> { ["vehicles"] = q.Vehicles, ["category"] = q.Category, ["levelFrom"] = q.LevelFrom, ["levelTo"] = q.LevelTo, ["seller"] = q.Seller ?? "", ["page"] = q.Page, ["sort"] = q.Sort ?? "", ["desc"] = q.Desc };
                DriftCity.Social.SocialNet.Request("auction.list", body, r =>
                {
                    var rows = (r.L("rows") ?? new List<object>()).OfType<Dictionary<string, object>>().Select(AuctionListing.FromJson).ToList();
                    if (r.B("ok")) offset = (long)r.N("now") - Now;
                    done?.Invoke(new Page { Rows = rows, Index = r.I("page"), Count = Math.Max(1, r.I("pages", 1)), Total = r.I("total") });
                });
                return;
            }
            var st = Offline.Load();
            st.Simulate();
            IEnumerable<AuctionListing> all = st.Listings.Where(l => l.Status == "open" && (l.Goods?.IsCar ?? false) == q.Vehicles);
            if (q.Category > 0) all = all.Where(l => l.Category == q.Category);
            if (q.LevelFrom > 0) all = all.Where(l => l.Level >= q.LevelFrom);
            if (q.LevelTo > 0) all = all.Where(l => l.Level <= q.LevelTo);
            // USER_SEARCHTEXT ("User ID/Item Name"): the seller or the item's name
            if (!string.IsNullOrWhiteSpace(q.Seller)) all = all.Where(l => l.Seller.IndexOf(q.Seller.Trim(), StringComparison.OrdinalIgnoreCase) >= 0 || l.Name.IndexOf(q.Seller.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
            var credit = st.Credits();
            foreach (var l in st.Listings) l.Credit = credit.TryGetValue(l.Seller, out int n) ? n : 0;
            Func<AuctionListing, IComparable> key = q.Sort switch
            {
                "no" => l => l.Id, "level" => l => l.Level, "name" => l => l.Name, "price" => l => l.Price, "count" => l => l.Count,
                "user" => l => l.Seller, "credit" => l => l.Credit, _ => l => -l.Id,
            };
            var list = (q.Desc ? all.OrderByDescending(key) : all.OrderBy(key)).ThenByDescending(l => l.Id).ToList();
            int pages = Math.Max(1, (list.Count + PageRows - 1) / PageRows);
            int page = Mathf.Clamp(q.Page, 0, pages - 1);
            done?.Invoke(new Page { Rows = list.Skip(page * PageRows).Take(PageRows).ToList(), Index = page, Count = pages, Total = list.Count });
        }

        static long offset;
        /// <summary>The market's clock (the server's online).</summary>
        public static long MarketNow => Now + (Online ? offset : 0);

        /// <summary>The inventory's Auction page: this driver's listings and the goods waiting for them.</summary>
        public static void Mine(Action<List<AuctionListing>> done)
        {
            if (Online)
            {
                Hook();
                DriftCity.Social.SocialNet.Request("auction.mine", null, r =>
                {
                    if (r.B("ok")) offset = (long)r.N("now") - Now;
                    done?.Invoke((r.L("rows") ?? new List<object>()).OfType<Dictionary<string, object>>().Select(AuctionListing.FromJson).ToList());
                });
                return;
            }
            var st = Offline.Load();
            st.Simulate();
            done?.Invoke(st.Listings.Where(l => l.Mine && l.Status != "taken" || l.Waiting && l.Buyer == P?.Name).OrderByDescending(l => l.Id).ToList());
        }

        // ================= actions =================
        /// <summary>AUCTIONITEM OK: a bag entry (count of a stack) or a car (slot) for a price; the tax is paid now.</summary>
        public static void Register(BagEntry e, int carSlot, int count, long price, Action<bool, string> done)
        {
            string label = e != null ? e.Name : null;
            if (Online)
            {
                Dictionary<string, object> item;
                if (e != null) item = e.Ref(count);
                else item = new Dictionary<string, object> { ["kind"] = "car", ["slot"] = carSlot };
                DriftCity.Social.SocialNet.Request("auction.register", new Dictionary<string, object> { ["item"] = item, ["count"] = count, ["price"] = price, ["hours"] = Days * 24, ["name"] = label ?? "" }, r =>
                {
                    if (r.B("ok")) { AudioLibrary.PlaySfx("sfx/SID_AUCTION_APPLY"); ChatBox.System(MissionText.Get("10117", r.S("name", label ?? "")) ); }
                    Reply(r, done);
                });
                return;
            }
            var err = CheckRegister(e, carSlot, count, price, out var goods);
            if (err != null) { done?.Invoke(false, err); return; }
            long fee = Fee(price);
            // take the goods out of the profile (the escrow), then list them
            if (goods.IsCar)
            {
                P.Cars.RemoveAt(carSlot);
                if (P.CurrentCar == carSlot) P.CurrentCar = 0; else if (P.CurrentCar > carSlot) P.CurrentCar--;
            }
            else if (e.Kind == BagKind.Stack) P.Give(e.Id, -count);
            else if (e.Kind == BagKind.Shop) { var it = P.Inventory.Find(i => i.ItemId == e.ItemId); it.Count -= count; if (it.Count <= 0) P.Inventory.Remove(it); }
            else P.PerfParts.Remove(e.Part);
            P.Mito -= fee;
            P.Save();
            var st = Offline.Load();
            var l = st.Add(P.Name, false, goods, price, fee, Days * Day);
            l.SellAt = st.BuyerTime(l);
            st.Save();
            AudioLibrary.PlaySfx("sfx/SID_AUCTION_APPLY");
            ChatBox.System(MissionText.Get("10117", l.Name));
            GameFlow.Instance?.RefreshHud();
            done?.Invoke(true, null);
        }

        /// <summary>The client's own checks before the dialog's OK (0x5c9c00, 0x5d0490) and the car rules 10577-10586.</summary>
        public static string CheckRegister(BagEntry e, int carSlot, int count, long price, out AuctionGoods goods)
        {
            goods = null;
            if (P == null) return "";
            if (P.Level < MinLevel) return MissionText.Get("11828", MinLevel);
            int open = Online ? 0 : Offline.Load().Listings.Count(l => l.Mine && l.Status == "open");
            if (open >= MaxListings) return MissionText.Get("10440");
            if (e == null)
            {
                if (carSlot < 0 || carSlot >= P.Cars.Count) return MissionText.Get("10445");
                var car = P.Cars[carSlot];
                if (P.Cars.Count <= 1) return "You can't sell your last car.";
                if (carSlot == P.CurrentCar) return MissionText.Get("10577");
                bool fitted = Enum.GetValues(typeof(PartSlot)).Cast<PartSlot>().Any(s => car.Fitted(s) >= 0) || (car.PerfSlots != null && car.PerfSlots.Any(u => u >= 0));
                if (fitted) return MissionText.Get("10578");
                if (Offline.Load().FromAuction.Contains(CarKey(car))) return MissionText.Get("10586");
                goods = new AuctionGoods { Kind = "car", Car = car, Count = 1 };
                count = 1;
            }
            else
            {
                if (!ItemUse.Tradeable(e)) return MissionText.Get("10445");
                if (e.Kind == BagKind.Part && P.Cars.Any(c => c.PerfSlots != null && c.PerfSlots.Contains(e.Part.Uid))) return MissionText.Get("10445");
                if (count <= 0) return MissionText.Get("10441");
                if (count > e.Count) return MissionText.Get("10443");
                goods = e.Kind == BagKind.Part ? new AuctionGoods { Kind = "part", Type = e.Part.Type, Grade = e.Part.Grade, Enhance = e.Part.Enhance }
                    : e.Kind == BagKind.Shop ? new AuctionGoods { Kind = "shop", ItemId = e.ItemId, Count = count }
                    : new AuctionGoods { Kind = "stack", Id = e.Id, Count = count };
            }
            if (price <= 0) return MissionText.Get("10442");
            if (price > MaxPrice) return MissionText.Get("10447");
            if (P.Mito < Fee(price)) return e == null ? MissionText.Get("10599") : MissionText.Get("10500");
            return null;
        }

        static string CarKey(OwnedCar c) => $"{c.VehicleIndex}:{c.Grade}:{c.Color}:{c.Spent}";

        /// <summary>The window's Buy (0x599ac0): the listed price, now.</summary>
        public static void Buy(AuctionListing l, Action<bool, string> done)
        {
            if (Online)
            {
                DriftCity.Social.SocialNet.Request("auction.buy", new Dictionary<string, object> { ["id"] = l.Id }, r => Reply(r, done));
                return;
            }
            var st = Offline.Load();
            var row = st.Listings.FirstOrDefault(x => x.Id == l.Id && x.Status == "open");
            string err = row == null ? "This item has already been sold." : row.Mine ? MissionText.Get("10499") : CanTake(row.Goods, true, row);
            if (err == null && P.Mito < row.Price) err = MissionText.Get("10500");
            if (err != null) { done?.Invoke(false, err); return; }
            P.Mito -= row.Price;
            Give(row.Goods);
            row.Status = "sold"; row.Buyer = P.Name; row.SoldPrice = row.Price; row.ClosedAt = Now;
            if (row.Goods.IsCar) st.FromAuction.Add(CarKey(P.Cars[P.Cars.Count - 1]));
            P.Save(); st.Save();
            GameFlow.Instance?.RefreshHud();
            done?.Invoke(true, null);
        }

        /// <summary>The inventory page's cancel: the goods come back, the tax doesn't (10118).</summary>
        public static void Cancel(AuctionListing l, Action<bool, string> done)
        {
            if (Online)
            {
                DriftCity.Social.SocialNet.Request("auction.cancel", new Dictionary<string, object> { ["id"] = l.Id }, r =>
                {
                    if (r.B("ok")) { AudioLibrary.PlaySfx("sfx/SID_AUCTION_CANCEL"); ChatBox.System(MissionText.Get("10118")); }
                    Reply(r, done);
                });
                return;
            }
            var st = Offline.Load();
            var row = st.Listings.FirstOrDefault(x => x.Id == l.Id);
            if (row == null || !row.Mine || row.Status != "open") { done?.Invoke(false, "This item has already been sold."); return; }
            row.Status = "cancelled"; row.ClosedAt = Now;
            st.Save();
            AudioLibrary.PlaySfx("sfx/SID_AUCTION_CANCEL");
            ChatBox.System(MissionText.Get("10118"));
            Take(row, (ok, e) => done?.Invoke(true, null));
        }

        /// <summary>Expired, cancelled or won goods into the bag or the garage.</summary>
        public static void Take(AuctionListing l, Action<bool, string> done)
        {
            if (Online)
            {
                DriftCity.Social.SocialNet.Request("auction.take", new Dictionary<string, object> { ["id"] = l.Id }, r => Reply(r, done));
                return;
            }
            var st = Offline.Load();
            var row = st.Listings.FirstOrDefault(x => x.Id == l.Id && x.Waiting);
            string err = row == null ? "Nothing to take." : CanTake(row.Goods, false, row);
            if (err != null) { done?.Invoke(false, err); return; }
            Give(row.Goods);
            row.Status = "taken"; row.ClosedAt = Now;
            P.Save(); st.Save();
            done?.Invoke(true, null);
        }

        static string CanTake(AuctionGoods g, bool buying, AuctionListing row)
        {
            if (g == null) return MissionText.Get("10445");
            if (g.IsCar)
            {
                if (P.Cars.Count >= 40) return MissionText.Get("10582");
                if (buying && P.Level < row.Level) return MissionText.Get("10580", row.Category, row.Level);
            }
            if (g.Kind == "part" && P.PerfParts.Count >= PerfRules.MaxParts) return MissionText.Get("10269");
            return null;
        }

        static void Give(AuctionGoods g)
        {
            switch (g.Kind)
            {
                case "stack": P.Give(g.Id, g.Count); break;
                case "shop": P.AddItem(g.ItemId, g.Count); break;
                case "part": P.PerfParts.Add(new PerfPart { Uid = P.NextPartUid++, Type = g.Type, Grade = g.Grade, Enhance = g.Enhance }); break;
                case "car":
                    var c = g.Car;
                    P.Cars.Add(new OwnedCar { VehicleIndex = c.VehicleIndex, Grade = c.Grade, Color = c.Color, PaintItem = c.PaintItem, BodyVariant = c.BodyVariant, Parts = new List<int>(c.Parts ?? new List<int>()), GiftParts = new List<int>(c.GiftParts ?? new List<int>()), Spent = 0 });
                    break;
            }
        }

        /// <summary>Offline: settles the AI buyers' purchases and expiries now (the window and the inventory page call it).</summary>
        public static void Tick() { if (!Online && P != null) Offline.Load().Simulate(); }

        // ================= the offline market =================
        [Serializable]
        public sealed class Offline
        {
            public string Driver;
            public int NextId = 1;
            public List<AuctionListing> Listings = new List<AuctionListing>();
            /// <summary>Cars bought here (10586: they can't be listed again).</summary>
            public List<string> FromAuction = new List<string>();

            const int AiListings = 36;
            static Offline current;
            static string currentPath;

            public static string PathFor() => Path.Combine(Path.GetDirectoryName(PlayerProfile.SavePath) ?? Application.persistentDataPath, $"auction_{PlayerProfile.Slot}.json");

            public static Offline Load()
            {
                var p = PlayerProfile.Current;
                string path = PathFor();
                if (current != null && currentPath == path && p != null && current.Driver == p.Name) return current;
                current = null;
                try { if (File.Exists(path)) current = JsonUtility.FromJson<Offline>(File.ReadAllText(path)); }
                catch (Exception ex) { Debug.LogWarning("auction store: " + ex.Message); }
                if (current == null || p == null || current.Driver != p?.Name) current = new Offline { Driver = p?.Name };
                currentPath = path;
                return current;
            }

            public static void Reset() { current = null; currentPath = null; }

            public void Save()
            {
                if (currentPath == null || PlayerProfile.Current == null) return;
                try { File.WriteAllText(currentPath, JsonUtility.ToJson(this)); }
                catch (Exception ex) { Debug.LogWarning("auction store save: " + ex.Message); }
            }

            public Dictionary<string, int> Credits()
            {
                var d = new Dictionary<string, int>();
                foreach (var l in Listings) if ((l.Status == "sold" || l.Status == "taken") && l.SoldPrice > 0) d[l.Seller] = (d.TryGetValue(l.Seller, out int n) ? n : 0) + 1;
                return d;
            }

            public AuctionListing Add(string seller, bool npc, AuctionGoods g, long price, long fee, long life)
            {
                var l = new AuctionListing { Id = NextId++, Seller = seller, Npc = npc, Goods = g, Name = g.Name() + (g.Count > 1 && !g.IsCar ? $" x{g.Count}" : ""), Category = g.Category, Level = g.Level, Count = g.Count, Price = price, Fee = fee, CreatedAt = Now, EndsAt = Now + life };
                Listings.Add(l);
                return l;
            }

            /// <summary>When an AI buyer takes a listing: the further under its value, the sooner; over 140% of it, nobody does.</summary>
            public long BuyerTime(AuctionListing l)
            {
                double ratio = l.Price / Math.Max(1.0, l.Goods.Value());
                if (ratio > 1.4) return 0;
                double minutes = ratio <= 0.7 ? UnityEngine.Random.Range(1f, 5f) : ratio <= 1.0 ? UnityEngine.Random.Range(5f, 45f) : UnityEngine.Random.Range(60f, 600f) * (float)ratio;
                return Now + (long)(minutes * 60000);
            }

            /// <summary>AI buyers take due listings (the Mito comes by note), unsold ones expire, AI listings are restocked.</summary>
            public void Simulate()
            {
                var p = PlayerProfile.Current;
                if (p == null) return;
                long now = Now;
                bool changed = false;
                foreach (var l in Listings)
                {
                    if (l.Status != "open") continue;
                    if (!l.Npc && l.SellAt > 0 && l.SellAt <= now && l.SellAt <= l.EndsAt)
                    {
                        l.Status = "sold"; l.Buyer = DriftCity.Social.SocialStore.AiDrivers[UnityEngine.Random.Range(0, DriftCity.Social.SocialStore.AiDrivers.Length)]; l.SoldPrice = l.Price; l.ClosedAt = l.SellAt;
                        var st = DriftCity.Social.SocialStore.Current;
                        st.AddNote("Auction House", 1, $"Your {l.Name} was sold to {l.Buyer} for {l.Price:N0} Mito at the Auction House. Click the gift slot to take the Mito.", l.Price);
                        st.Save();
                        ChatBox.System($"Your {l.Name} was sold at the Auction House for {l.Price:N0} Mito. The Mito is in your notes.");
                        changed = true;
                    }
                    else if (l.EndsAt <= now)
                    {
                        l.Status = l.Npc ? "taken" : "expired"; l.ClosedAt = l.EndsAt;
                        if (!l.Npc)
                        {
                            var st = DriftCity.Social.SocialStore.Current;
                            st.AddNote("Auction House", 1, $"Your auction of {l.Name} expired unsold. Take it back from the Auction page of your inventory.", 0);
                            st.Save();
                        }
                        changed = true;
                    }
                }
                // forget old history (keep the driver's last 60 finished rows)
                int before = Listings.Count;
                Listings.RemoveAll(l => l.Npc && l.Status != "open");
                var done = Listings.Where(l => !l.Npc && (l.Status == "sold" || l.Status == "taken")).OrderByDescending(l => l.ClosedAt).Skip(60).ToList();
                foreach (var l in done) Listings.Remove(l);
                changed |= Listings.Count != before;
                int ai = Listings.Count(l => l.Npc && l.Status == "open");
                for (; ai < AiListings; ai++) { AiListing(p); changed = true; }
                if (changed) Save();
            }

            /// <summary>An AI driver's listing: goods from the game's tables near the player's level, at their value give or take.</summary>
            void AiListing(PlayerProfile p)
            {
                AuctionGoods g;
                float roll = UnityEngine.Random.value;
                if (roll < 0.40f)
                {
                    int grade = p.Level >= 45 && UnityEngine.Random.value < 0.35f ? 2 : p.Level >= 20 && UnityEngine.Random.value < 0.5f ? 1 : 0;
                    g = new AuctionGoods { Kind = "part", Type = UnityEngine.Random.Range(0, 4), Grade = grade, Enhance = UnityEngine.Random.Range(0, 7) };
                }
                else if (roll < 0.62f)
                {
                    var (id, _) = PerfRules.Counter[UnityEngine.Random.Range(0, PerfRules.Counter.Length)];
                    g = new AuctionGoods { Kind = "stack", Id = id, Count = UnityEngine.Random.Range(1, 11) };
                }
                else if (roll < 0.80f)
                    g = new AuctionGoods { Kind = "stack", Id = Crafting.ChaseDrops[UnityEngine.Random.Range(0, Crafting.ChaseDrops.Length)], Count = UnityEngine.Random.Range(1, 6) };
                else if (roll < 0.90f && ShopData.Paints.Count > 0)
                    g = new AuctionGoods { Kind = "shop", ItemId = ShopData.Paints[UnityEngine.Random.Range(0, Mathf.Min(80, ShopData.Paints.Count))].UniqueId, Count = 1 };
                else
                {
                    var cars = GameTables.PlayerCars.Where(c => c.ReqLevel <= p.Level + 15 && c.ReqLevel >= p.Level - 30).ToList();
                    if (cars.Count == 0) cars = GameTables.PlayerCars;
                    var info = cars[UnityEngine.Random.Range(0, cars.Count)];
                    g = new AuctionGoods { Kind = "car", Car = new OwnedCar { VehicleIndex = info.Index, Color = UnityEngine.Random.Range(0, 4) }, Count = 1 };
                }
                long value = Math.Max(500, g.Value());
                long price = Math.Max(100, (long)Math.Round(value * UnityEngine.Random.Range(0.7f, 1.3f) / 100.0) * 100);
                var l = Add(DriftCity.Social.SocialStore.AiDrivers[UnityEngine.Random.Range(0, DriftCity.Social.SocialStore.AiDrivers.Length)], true, g, price, 0, (long)(UnityEngine.Random.Range(6f, 72f) * Hour));
                l.CreatedAt -= (long)(UnityEngine.Random.Range(0f, 48f) * Hour);
            }
        }
    }
}
