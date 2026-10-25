using System.Collections.Generic;
using System.Linq;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.Game
{
    /// <summary>In-city menus: garage, car dealer, tuning, paint shop, inventory, driver info, quests, map, travel, options.</summary>
    public static class Menus
    {
        static PlayerProfile P => PlayerProfile.Current;
        static UiManager Ui => UiManager.Instance;

        // ---------------- garage (original GARAGE dialogs + garage.nif scene, OriginalMenus.cs) ----------------
        /// <summary>Your cars on the garage turntable: browse, get on, stats; dealer / reseller / custom shop stay in the shop list (P).</summary>
        public static void Garage() => OriginalMenus.Garage();

        // ---------------- shop chooser ----------------
        public static void Shop()
        {
            OriginalMenus.Close();   // the main bar stays clickable around the inventory / driver info windows
            ListWindow.Show("Shops", new[]
            {
                new ListWindow.Row("Car dealer", "Buy new cars for your garage (all classes V1-V9)", "Open", () => CarShop()),
                new ListWindow.Row("Reseller", "Sell cars back to the dealer", "Open", () => CarShop(sell: true)),
                new ListWindow.Row("Rental cars", "Rent a car for a few days", "Open", () => { ListWindow.CloseOpen(); Services.RentalCars.Open(); }),
                new ListWindow.Row("Name change kit", "Change your driver name (i_changenick)", "Buy", () => { ListWindow.CloseOpen(); Services.DriverRename.BuyKit(); }),
                new ListWindow.Row("Auction House", "Buy from other drivers; register items from the inventory's Auction page", "Open", () => { ListWindow.CloseOpen(); AuctionHouse.Open(); }),
                new ListWindow.Row("Custom shop", "Aero kits, bumpers, hoods, wings, wheels, booster flames, neon", "Open", () => CustomShop()),
                new ListWindow.Row("Tuning shop", "Upgrade your current car to its next grade", "Open", () => Tuning()),
                new ListWindow.Row("Parts shop", "Performance parts: buy, enhance, grind, combine", "Open", () => WorkshopMenus.PartsShop()),
                new ListWindow.Row("Manufacturing", "Skill books and crafting from materials, dust and kits", "Open", () => WorkshopMenus.Manufacturing()),
                new ListWindow.Row("Licences", "Earn licences and mount one for its bonus", "Open", () => WorkshopMenus.Licences()),
                new ListWindow.Row("Paint shop", "Repaint your current car (solid, two-tone and glossy)", "Open", () => PaintShop()),
                new ListWindow.Row("Garage", "Switch between your cars", "Open", () => Garage()),
            }, $"Mito {P.Mito:N0}");
        }

        // ---------------- car dealer (original AUTOSHOP dialog + 3D preview) ----------------
        static List<VehicleInfo> dealerCars;
        static int dealerIndex, dealerPaint, dealerClass;   // class 0 = all, 1..9 = V1..V9 cars
        static bool dealerSell;
        static GameObject dealerPreview, dealerShowroom;
        static Camera dealerCam;
        static readonly Vector3 DealerBase = new Vector3(0, -3000, 0);
        static RenderTexture dealerRt;
        static RawImage dealerLayer;
        static NuiView dealerView;

        public static bool DealerOpen => dealerView || AdvancedDealer.IsOpen;
        const int PreviewLayer = 30;

        static void SetLayer(GameObject go, int layer) { foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer; }

        /// <summary>Destroys the dealer's preview car, camera, layer and render target whichever way the dialog went away.</summary>
        sealed class DealerCleanup : MonoBehaviour { void OnDestroy() => ReleaseDealerScene(); }

        static void ReleaseDealerScene()
        {
            if (dealerPreview) Object.Destroy(dealerPreview);
            if (dealerShowroom) Object.Destroy(dealerShowroom);
            if (dealerCam) Object.Destroy(dealerCam.gameObject);
            if (dealerLayer) Object.Destroy(dealerLayer.gameObject);
            if (dealerRt) { dealerRt.Release(); Object.Destroy(dealerRt); }
            dealerPreview = null; dealerShowroom = null; dealerCam = null; dealerLayer = null; dealerRt = null;
        }

        /// <summary>
        /// The car dealer. The autoshop stage (0x7438a0) opens the advanced dealer (AUTOSHOPADV, Shops/AdvancedDealer.cs) when
        /// globalcontent CarShop is set, which this client's data does (1); otherwise the classic AUTOSHOP 3260000 below.
        /// </summary>
        public static void CarShop(bool sell = false)
        {
            if (GlobalContent.Int("CarShop", 1) != 0) { ListWindow.CloseOpen(); CloseDealer(); AdvancedDealer.Open(sell); return; }
            ClassicCarShop(sell);
        }

        /// <summary>The classic dealer AUTOSHOP 3260000 (globalcontent CarShop = 0).</summary>
        public static void ClassicCarShop(bool sell = false)
        {
            ListWindow.CloseOpen();
            CloseDealer();
            dealerSell = sell; dealerClass = 0;
            FilterDealer();
            dealerIndex = 0; dealerPaint = 0;
            dealerView = Ui.Open(3260000);
            dealerView.gameObject.AddComponent<DealerCleanup>();
            dealerView.SetVisible("ID_AUTOSHOP_CARCOMPARE_BUTTON", false);
            dealerView.SetVisible("ID_AUTOSHOP_GROWTHGRAPH_BUTTON", false);
            dealerView.SetVisible("ID_AUTOSHOP_ASSIST", false);
            dealerView.SetVisible("ID_AUTOSHOP_NEEDCOUPON", false);
            dealerView.SetVisible("ID_AUTOSHOP_CARRIDESTATUS", false);
            // the showroom fills the screen: no alpha channel, so nothing behind it shows through
            dealerRt = new RenderTexture(1024, 768, 24, SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RGB111110Float) ? RenderTextureFormat.RGB111110Float : RenderTextureFormat.RGB565);
            dealerCam = new GameObject("Dealer Camera").AddComponent<Camera>();
            dealerCam.clearFlags = CameraClearFlags.SolidColor;
            dealerCam.backgroundColor = Color.black;
            dealerCam.targetTexture = dealerRt;
            dealerCam.gameObject.AddComponent<GarageLighting>();
            // the showroom (autoshop\autoshop.nif) seen from init/camera.ini [camera3] "autoshop": a static camera
            // at (0,-8,2) looking at (-1.1,0,1.2) in Gamebryo space, fov 60, 0.4 .. 64
            dealerCam.fieldOfView = 60;
            dealerCam.nearClipPlane = 0.4f;
            dealerCam.farClipPlane = 64f;
            dealerCam.transform.position = DealerBase + new Vector3(0f, 2f, -8f);
            dealerCam.transform.LookAt(DealerBase + new Vector3(-1.1f, 1.2f, 0f));
            if (DriftCityData.Files.Exists(@"autoshop\autoshop.nif"))
            {
                dealerShowroom = new NifSceneBuilder { LogErrors = false }.Load(@"autoshop\autoshop.nif");
                dealerShowroom.transform.position = DealerBase;
                SetLayer(dealerShowroom, PreviewLayer);
            }
            // the showroom stands in its own layer far below the city
            dealerCam.cullingMask = 1 << PreviewLayer;
            // directly behind the dealer dialog, covering the city and the HUD as the original's shop scene does
            dealerLayer = Ui.Layer("Dealer Preview", dealerView.transform.GetSiblingIndex());
            dealerLayer.texture = dealerRt;
            RefreshDealer();
            Ui.On("ID_AUTOSHOP_PREVCAR", _ => { int n = DealerCount; if (n > 0) dealerIndex = (dealerIndex + n - 1) % n; RefreshDealer(); });
            Ui.On("ID_AUTOSHOP_NEXTCAR", _ => { int n = DealerCount; if (n > 0) dealerIndex = (dealerIndex + 1) % n; RefreshDealer(); });
            for (int i = 1; i <= 4; i++) { int slot = i - 1; Ui.On("ID_AUTOSHOP_CARCOLOR_" + i, _ => { dealerPaint = slot; RefreshDealer(); }); }
            Ui.On("ID_AUTOSHOP_BUY_BUTTON", _ => BuyDealerCar());
            Ui.On("ID_AUTOSHOP_SELL_BUTTON", _ => SellDealerCar());
            Ui.On("ID_AUTOSHOP_TAB_BUY", _ => { dealerSell = false; dealerIndex = 0; FilterDealer(); RefreshDealer(); });
            Ui.On("ID_AUTOSHOP_TAB_SELL", _ => { dealerSell = true; dealerIndex = 0; RefreshDealer(); });
            Ui.On("ID_AUTOSHOP_GRADE_ALL", _ => { dealerClass = 0; dealerIndex = 0; FilterDealer(); RefreshDealer(); });
            for (int g = 1; g <= 9; g++) { int cls = g; Ui.On("ID_AUTOSHOP_GRADE_" + g, _ => { dealerClass = cls; dealerIndex = 0; FilterDealer(); RefreshDealer(); }); }
            Ui.On("ID_AUTOSHOP_ESC", _ => CloseDealer());
        }

        static int DealerCount => dealerSell ? P.Cars.Count : dealerCars.Count;

        static void FilterDealer()
        {
            dealerCars = GameTables.PlayerCars.Where(c => dealerClass == 0 || c.Grade == dealerClass)
                .OrderBy(c => c.Grade).ThenBy(c => c.ReqLevel).ThenBy(c => ShopData.CarPrice(c)).ToList();
        }

        static void RefreshDealer()
        {
            var v = dealerView;
            v.SetVisible("ID_AUTOSHOP_BUY_BUTTON", !dealerSell);
            v.SetVisible("ID_AUTOSHOP_BUYPRICEIMG", !dealerSell);
            v.SetVisible("ID_AUTOSHOP_BUYREQUIRELEV", !dealerSell);
            v.SetVisible("ID_AUTOSHOP_SELL_BUTTON", dealerSell);
            v.SetVisible("ID_AUTOSHOP_SELLPRICEIMG", dealerSell);
            for (int i = 1; i <= 4; i++) { v.SetVisible("ID_AUTOSHOP_CARCOLOR_" + i, !dealerSell); v.SetVisible("ID_AUTOSHOP_BUYCARCOLOR_" + i, !dealerSell); }
            v.SetVisible("ID_AUTOSHOP_GRADE_ALL", !dealerSell);
            for (int g = 1; g <= 9; g++) v.SetVisible("ID_AUTOSHOP_GRADE_" + g, !dealerSell);
            v.SetText("ID_AUTOSHOP_MYMITO", $"{P.Mito:N0}");
            if (dealerSell) { RefreshReseller(); return; }
            if (dealerCars.Count == 0)
            {
                OriginalMenus.Plate(v, "ID_AUTOSHOP_CARNAMEPOS", "No cars in this class");
                foreach (var id in new[] { "PRICE", "BUYREQUIRELEV", "GRADE" }) v.SetText("ID_AUTOSHOP_" + id, "");
                foreach (var id in new[] { "CARGRADEPOS", "CARNAMEPOS_PREV", "CARNAMEPOS_NEXT", "CARGRADEPOS_PREV", "CARGRADEPOS_NEXT", "CAREMBLEMPOS", "CARHAVESTATUS" }) v.SetVisible("ID_AUTOSHOP_" + id, false);
                ShowStats(v, (0, 0, 0, 0));
                v.SetEnabled("ID_AUTOSHOP_BUY_BUTTON", false);
                if (dealerPreview) Object.Destroy(dealerPreview);
                return;
            }
            dealerIndex = Mathf.Clamp(dealerIndex, 0, dealerCars.Count - 1);
            var car = dealerCars[dealerIndex];
            int price = ShopData.CarPrice(car);
            var prevCar = dealerCars[(dealerIndex + dealerCars.Count - 1) % dealerCars.Count];
            var nextCar = dealerCars[(dealerIndex + 1) % dealerCars.Count];
            DealerPlates(v, car, ShopData.Grade(car, 0), prevCar, nextCar);
            v.SetText("ID_AUTOSHOP_PRICE", $"{price:N0}");
            v.SetText("ID_AUTOSHOP_BUYREQUIRELEV", $"Lv {car.ReqLevel}");
            bool owned = P.Cars.Any(c => c.VehicleIndex == car.Index);
            // the dialog's own "Purchased" label shows for cars already in the garage
            v.SetText("ID_AUTOSHOP_CARHAVESTATUS", "");
            v.SetVisible("ID_AUTOSHOP_CARHAVESTATUS", owned);
            v.SetText("ID_AUTOSHOP_GRADE", $"Class V{car.Grade}   {dealerIndex + 1}/{dealerCars.Count}");
            ShowStats(v, ShopData.Stats(car, null));
            for (int i = 1; i <= 4; i++)
            {
                var sw = v.Control("ID_AUTOSHOP_BUYCARCOLOR_" + i);
                if (sw) foreach (var ri in sw.GetComponentsInChildren<RawImage>()) ri.color = CarFactory.FactoryColor(car, i - 1);
                var chk = v.Control("ID_AUTOSHOP_CARCOLOR_" + i);
                if (chk) foreach (var ri in chk.GetComponentsInChildren<RawImage>()) ri.color = new Color(1, 1, 1, i - 1 == dealerPaint ? 1f : 0.2f);
            }
            v.SetEnabled("ID_AUTOSHOP_BUY_BUTTON", !owned && P.Level >= car.ReqLevel && P.Mito >= price);
            if (dealerPreview) Object.Destroy(dealerPreview);
            dealerPreview = CarFactory.BuildVisual(car, dealerPaint, DealerBase, Quaternion.Euler(0, 210, 0));
            SetLayer(dealerPreview, PreviewLayer);
        }

        /// <summary>The original name plates, grade badges and maker emblem for the shown car and its neighbours.</summary>
        static void DealerPlates(NuiView v, VehicleInfo car, CarGrade grade, VehicleInfo prev, VehicleInfo next)
        {
            v.SetVisible("ID_AUTOSHOP_CARGRADEPOS", true);
            v.SetVisible("ID_AUTOSHOP_CAREMBLEMPOS", true);
            OriginalMenus.Plate(v, "ID_AUTOSHOP_CARNAMEPOS", grade?.UiName ?? car.UiName, car);
            OriginalMenus.Emblem(v, "ID_AUTOSHOP_CARGRADEPOS", grade);
            OriginalMenus.MakerEmblem(v, "ID_AUTOSHOP_CAREMBLEMPOS", car.Maker);
            foreach (var (side, other) in new[] { ("PREV", prev), ("NEXT", next) })
            {
                v.SetVisible($"ID_AUTOSHOP_CARNAMEPOS_{side}", other != null);
                v.SetVisible($"ID_AUTOSHOP_CARGRADEPOS_{side}", other != null);
                if (other == null) continue;
                OriginalMenus.Plate(v, $"ID_AUTOSHOP_CARNAMEPOS_{side}", other.UiName, other);
                OriginalMenus.Emblem(v, $"ID_AUTOSHOP_CARGRADEPOS_{side}", ShopData.Grade(other, 0));
            }
        }

        static void ShowStats(NuiView v, (int speed, int accel, int crash, int boost) st)
        {
            foreach (var s in new[] { ("SPEED", st.speed), ("ACCEL", st.accel), ("CRASH", st.crash), ("BOOST", st.boost) })
            {
                SetBar(v.Control("ID_AUTOSHOP_GAUGE_" + s.Item1), s.Item2 / 300f);
                v.SetText($"ID_AUTOSHOP_GAUGE_{s.Item1}TEXT", $"({s.Item2})");
            }
        }

        /// <summary>Reseller tab: your cars with the price the dealer pays for each.</summary>
        static void RefreshReseller()
        {
            var v = dealerView;
            dealerIndex = Mathf.Clamp(dealerIndex, 0, P.Cars.Count - 1);
            var owned = P.Cars[dealerIndex];
            var car = GameTables.Car(owned.VehicleIndex);
            var grade = ShopData.Grade(car, owned.Grade);
            int price = ShopData.SellPrice(car, owned);
            int n = P.Cars.Count;
            DealerPlates(v, car, grade, n > 1 ? GameTables.Car(P.Cars[(dealerIndex + n - 1) % n].VehicleIndex) : null,
                         n > 1 ? GameTables.Car(P.Cars[(dealerIndex + 1) % n].VehicleIndex) : null);
            v.SetText("ID_AUTOSHOP_PRICE", $"{price:N0}");
            v.SetText("ID_AUTOSHOP_CARHAVESTATUS", "");
            v.SetVisible("ID_AUTOSHOP_CARHAVESTATUS", true);
            v.SetText("ID_AUTOSHOP_GRADE", $"Your car {dealerIndex + 1}/{n}   {owned.Parts.Count} part(s)");
            ShowStats(v, ShopData.Stats(car, owned));
            v.SetEnabled("ID_AUTOSHOP_SELL_BUTTON", n > 1);
            if (dealerPreview) Object.Destroy(dealerPreview);
            dealerPreview = CarFactory.BuildVisual(car, owned, DealerBase, Quaternion.Euler(0, 210, 0), out _, out _);
            SetLayer(dealerPreview, PreviewLayer);
            PaintVisual(dealerPreview, owned);
        }

        static void SellDealerCar()
        {
            if (P.Cars.Count <= 1) { UiPopup.Message("You can't sell your last car."); return; }
            if (dealerIndex == P.CurrentCar && GameFlow.BlockedByMission()) return;
            int slot = dealerIndex;
            var owned = P.Cars[slot];
            var car = GameTables.Car(owned.VehicleIndex);
            int price = ShopData.SellPrice(car, owned);
            UiPopup.Show($"Sell the {car.UiName} for {price:N0} Mito?\nIts parts go with it.", id =>
            {
                if (id != UiPopup.Yes) return;
                bool wasDriving = slot == P.CurrentCar;
                Progress.SellCar(slot, price, r =>
                {
                    if (!r.Ok) return;
                    dealerIndex = 0;
                    RefreshDealer();
                    if (wasDriving) GameFlow.Instance?.RespawnCar();
                    UiPopup.Message($"Sold for {price:N0} Mito.");
                });
            }, UiPopup.Yes, UiPopup.No);
        }

        /// <summary>Paint for a car built outside the city (dealer preview): bought paint wins over the creation colour.</summary>
        static void PaintVisual(GameObject go, OwnedCar owned)
        {
            CarPaint.Apply(go, GameTables.Car(owned.VehicleIndex), owned);
        }

        static void BuyDealerCar()
        {
            if (dealerCars.Count == 0) return;
            var car = dealerCars[dealerIndex];
            int price = ShopData.CarPrice(car);
            if (P.Cars.Any(c => c.VehicleIndex == car.Index)) { UiPopup.Message($"You already own the {car.UiName}."); return; }
            if (GameFlow.BlockedByMission()) return;
            if (P.Level < car.ReqLevel) { UiPopup.Message($"You need level {car.ReqLevel} to buy the {car.UiName}."); return; }
            if (P.Mito < price) { UiPopup.Message($"You need {price - P.Mito:N0} more Mito."); return; }
            // coupon cars need their coupon (Init/Coupon; 10489); the server checks and takes it online
            if (!CouponBook.CanBuy(P, car)) { UiPopup.Message(MissionText.Get("10489")); return; }
            UiPopup.Show($"Buy the {car.UiName} for {price:N0} Mito?", id =>
            {
                if (id != UiPopup.Yes) return;
                Progress.BuyCar(car, price, dealerPaint, r =>
                {
                    if (!r.Ok) return;
                    if (!Net.OnlineSession.Active) CouponBook.SpendCoupon(P, car);
                    QuestTracker.Notify(QuestEvent.CarBought);
                    RefreshDealer();
                    UiPopup.Message($"The {car.UiName} is yours. It's waiting outside.", () => { CloseDealer(); GameFlow.Instance?.RespawnCar(); });
                });
            }, UiPopup.Yes, UiPopup.No);
        }

        static void CloseDealer()
        {
            if (dealerView) Ui.Close(dealerView);
            dealerView = null;
            ReleaseDealerScene();
        }

        static void SetBar(NuiControlView gauge, float fill)
        {
            if (!gauge) return;
            fill = Mathf.Clamp01(fill);
            foreach (Transform el in gauge.transform)
            {
                if (!el.TryGetComponent<RawImage>(out var img)) continue;
                var rt = (RectTransform)el;
                if (!el.TryGetComponent<BarState>(out var st)) { st = el.gameObject.AddComponent<BarState>(); st.Width = rt.sizeDelta.x; st.Uv = img.uvRect; }
                rt.sizeDelta = new Vector2(st.Width * fill, rt.sizeDelta.y);
                var uv = st.Uv; uv.width *= fill; img.uvRect = uv;
            }
        }

        sealed class BarState : MonoBehaviour { public float Width; public Rect Uv; }

        public static void CustomShop() => DriftCity.Game.CustomShop.Open();

        // ---------------- tuning (grades) ----------------
        /// <summary>The garage's tuning panel on the car you drive (next grade through Progress.Upgrade).</summary>
        public static void Tuning() => OriginalMenus.Tuning();

        // ---------------- paint shop ----------------
        public static void PaintShop()
        {
            var rows = new List<ListWindow.Row>();
            var owned = P.Car;
            foreach (var paint in ShopData.Paints.Take(80))
            {
                var pi = paint;
                bool current = owned.PaintItem == paint.UniqueId;
                rows.Add(new ListWindow.Row(paint.Name + (current ? "   (current)" : ""), $"{paint.Price:N0} Mito", current ? null : "Buy", () =>
                {
                    if (P.Mito < pi.Price) { UiPopup.Message($"You need {pi.Price - P.Mito:N0} more Mito."); return; }
                    Progress.Paint(P.CurrentCar, pi.UniqueId, pi.Price, r =>
                    {
                        if (!r.Ok) return;
                        GameFlow.Instance?.RepaintCar();
                        PaintShop();
                    });
                }, P.Mito >= paint.Price, paint.Color));
            }
            ListWindow.Show("Paint shop", rows, $"Mito {P.Mito:N0}");
        }

        // ---------------- inventory (Inventory/InventoryWindow.cs) / info (USERINFO, OriginalMenus.cs) ----------------
        /// <summary>The client's inventory: parts, drift items, licence, sticker, auction and modify pages beside the item bag.</summary>
        public static void Inventory() => InventoryWindow.Open();

        /// <summary>Driver info: name, level, EXP, Mito, records and the current car's stats.</summary>
        public static void MyInfo() => OriginalMenus.DriverInfo();

        public static void Quests() => QuestTracker.ShowBoard();

        // ---------------- map / travel ----------------
        static readonly Dictionary<string, (int dialog, string prefix)> MapDialogs = new Dictionary<string, (int, string)>
        {
            { "moonpalace", (480000, "MP") }, { "koinonia", (490000, "KO") }, { "cras", (500000, "CR") }, { "oros", (510000, "OR") }, { "taipei", (530000, "TP") },
        };

        public static void WorldMap() => WorldMapPage(null);

        /// <summary>
        /// The full map's page of <paramref name="page"/> (null = the city the driver is in; a second call closes it). LEFT /
        /// RIGHT turn the city pages with the quest-unlock locks (0x5f5c40 / 0x5f3170, Services/WorldMapPages.cs).
        /// </summary>
        public static void WorldMapPage(string page)
        {
            var flow = GameFlow.Instance;
            if (page == null && flow && flow.InParkingLot && ParkingLotStage.Current) { ParkingLotMap(ParkingLotStage.Current); return; }
            if (!flow || !MapDialogs.TryGetValue(page ?? flow.City, out var m)) return;
            bool toggled = false;
            foreach (var d in MapDialogs.Values) { var open = Ui.Find(d.dialog); if (open) { Ui.Close(open); toggled = true; } }
            if (toggled && page == null) return;
            var v = Ui.Open(m.dialog);
            if (!v) return;
            Ui.On($"ID_VIEWMAP_{m.prefix}_CLOSE", view => Ui.Close(view));
            string city = page ?? flow.City;
            Services.WorldMapPages.Hook(v, m.prefix, city, WorldMapPage);
            if (city != flow.City) return;   // another city's page: no player marker
            // player marker: the client places full-map icons on the DUMMY control (0x5f3e90): left + width * x fraction,
            // top + height * (1 - y fraction) of the city bounds (0x871a48); DUMMY has the bounds' aspect in every city
            var region = v.Control($"ID_VIEWMAP_{m.prefix}_DUMMY") ?? v.Control($"ID_VIEWMAP_{m.prefix}_MAPREGION") ?? v.Control($"ID_VIEWMAP_{m.prefix}_BASE");
            var info = CityMapInfo.ForCity(flow.City);
            if (region && info != null)
            {
                region.gameObject.SetActive(true);
                // DUMMY is a hidden (K=0) layout box; Taipei's carries a placeholder image
                if (region.Data.IdName.EndsWith("_DUMMY"))
                    foreach (var ri in region.GetComponentsInChildren<RawImage>(true)) ri.enabled = false;
                var marker = NuiView.NewRect("Player", region.transform);
                marker.sizeDelta = new Vector2(12, 12);
                marker.gameObject.AddComponent<Image>().color = new Color(1f, 0.2f, 0.1f);
                var mm = marker.gameObject.AddComponent<MapMarker>();
                mm.Region = (RectTransform)region.transform;
                mm.Map = info;
                // Auto-Drive (CHASES.md section 5): a place clicked on the full map, then Go (11112)
                var pick = region.gameObject.AddComponent<MapPick>();
                if (!region.GetComponent<Graphic>()) region.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0f);   // catches the click
                pick.Map = info;
            }
        }

        /// <summary>Click on the city map: Auto-Drive to that place after a confirm (the client's map "Go").</summary>
        sealed class MapPick : MonoBehaviour, UnityEngine.EventSystems.IPointerClickHandler
        {
            public CityMapInfo Map;

            public void OnPointerClick(UnityEngine.EventSystems.PointerEventData e)
            {
                var rt = (RectTransform)transform;
                if (Map == null || !RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, e.position, e.pressEventCamera, out var local)) return;
                var r = rt.rect;
                float fx = (local.x - r.xMin) / r.width, fy = 1f - (local.y - r.yMin) / r.height;   // y from the north edge
                var target = new Vector3(Map.XMin + fx * Map.Width, 0f, Map.YMin + (1f - fy) * Map.Height);
                MissionUi.Confirm("Auto-Drive to this place?", () => { Ui.Close(GetComponentInParent<NuiView>()); AutoDrive.Start(target); });
            }
        }

        /// <summary>The parking lot's full map (VIEWMAP_P_1 570000, B1: 580000); its DUMMY region is _parking.dds scaled to 614 px.</summary>
        static void ParkingLotMap(ParkingLotStage lot)
        {
            int dialog = lot.MapDialog;
            var existing = Ui.Find(dialog);
            if (existing) { Ui.Close(existing); return; }
            var v = Ui.Open(dialog);
            if (!v) return;
            string prefix = dialog == 580000 ? "ID_VIEWMAP_P_B1" : "ID_VIEWMAP_P_1";
            Ui.On(prefix + "_CLOSE", view => Ui.Close(view));
            var region = v.Control(prefix + "_DUMMY") ?? v.Control(prefix + "_BASE");
            if (!region) return;
            var marker = NuiView.NewRect("Player", region.transform);
            marker.sizeDelta = new Vector2(12, 12);
            marker.gameObject.AddComponent<Image>().color = new Color(1f, 0.2f, 0.1f);
            var mm = marker.gameObject.AddComponent<MapMarker>();
            mm.Region = (RectTransform)region.transform;
            mm.Map = CityMapInfo.ForParkingImage(lot.MapImage);   // 1F +-461, B1 x +-357 y +-380 (0x5f2d80)
        }

        sealed class MapMarker : MonoBehaviour
        {
            public RectTransform Region;
            /// <summary>The map whose world bounds the region spans (0x5f3e90 maps them onto the region rectangle).</summary>
            public CityMapInfo Map;
            void Update()
            {
                var flow = GameFlow.Instance;
                var car = flow ? flow.PlayerCar : null;
                if (!car || !Region || Map == null) return;
                var f = Map.Fraction(car.transform.position);   // x from the west edge, y from the north edge
                var size = Region.sizeDelta;
                ((RectTransform)transform).anchoredPosition = new Vector2(f.x * size.x - 6, -f.y * size.y + 6);
            }
        }

        /// <summary>
        /// Key C. The client's way between cities is the parking lot: a city's RQ house sends the driver back there and an
        /// exit + gate leads to the next city. The list offers that, and each gate directly as a shortcut.
        /// </summary>
        public static void CityTravel()
        {
            var flow = GameFlow.Instance;
            var rows = new List<ListWindow.Row>
            {
                new ListWindow.Row("Driver Dome", "Back to the parking lot: garage, dealer, parts shop and the city gates", flow && flow.InParkingLot ? null : "Go", () =>
                {
                    ListWindow.CloseOpen();
                    GameFlow.Instance?.ReturnToParkingLot();
                }),
            };
            var gates = ParkingLotStage.Gates;
            for (int i = 0; i < gates.Length; i++)
            {
                string city = gates[i].City;
                bool here = flow && flow.InCity && flow.City == city;
                rows.Add(new ListWindow.Row($"Gate {i + 1} - {gates[i].Name}" + (here ? "   (you are here)" : ""), null, here ? null : "Travel", () =>
                {
                    var locked = QuestUnlocks.CityRefusal(city);
                    if (locked != null) { UiPopup.Message(locked); return; }
                    ListWindow.CloseOpen();
                    GameFlow.Instance?.TravelTo(city);
                }));
            }
            ListWindow.Show("Travel", rows, "Loading a city takes a few seconds.");
        }

        // ---------------- options (original OPTION dialogs, OriginalMenus.cs) ----------------
        /// <summary>Audio volumes, graphics quality, HD textures (advanced: texture quality), arcade look (advanced: FOV Arcade/Real), traffic density (advanced: view distance).</summary>
        public static void Options() => OriginalMenus.Options();
    }
}
