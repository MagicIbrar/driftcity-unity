using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.Game
{
    /// <summary>
    /// The NDLG parts page, the set box and the restore box of the inventory (DriftCity_RE\INVENTORY.md).
    /// NDLGINVEN 4810000 (parts page, init 0x5e1ba0 / page 0x5db0a0): two rows of four stat slots over the car plan
    /// (SLOTN_01/03/05/07 = speed / accel / durability / booster of row 1, 02/04/06/08 row 2), each with its option slot
    /// (OPTSLOTN), the grade line in the part's colour ({S,A,D,B}{1,2}_GLINE/GSLOT/GOPSLOT_WHITE/GREEN/ORANGE/VIOLET/BLUE,
    /// GLINE_BLACK when empty), RENTn "renting", UPGRADEn +N, and SET_PARTS_OUTLINE1/2 around a row that holds one set.
    /// NDLGINVEN 4780000 under it: the performance table (texts 11756-11769: point type / total / vehicle / parts / level /
    /// assist; max speed, accel time, chase damage, boost time), its rows' tips in NDLGINVEN 4790000. Row 1 is the car's
    /// PerfSlots, row 2 its PerfSlots2, the option slots its OptSlots (Inventory/CarParts.cs: drop a part / an option part
    /// from the bag, right click takes it off).
    /// </summary>
    public static partial class InventoryWindow
    {
        static readonly string[] StatLetter = { "S", "A", "D", "B" };
        static readonly string[] GradeColour = { "WHITE", "GREEN", "ORANGE", "VIOLET" };
        static readonly string[] AllColours = { "WHITE", "GREEN", "ORANGE", "VIOLET", "BLUE" };

        static void SetupPartsNdlg()
        {
            if (!pageA) return;
            for (int n = 1; n <= 8; n++)
            {
                pageA.SetVisible($"ID_NDLGINVEN_SEL{n}", false);
                pageA.SetVisible($"ID_NDLGINVEN_OPSEL{n}", false);
                pageA.SetVisible($"ID_NDLGINVEN_OPTSLOT_{n:00}", false);
                int type = (n - 1) / 2, row = (n - 1) % 2;
                pageA.SetText($"ID_NDLGINVEN_SLOTX_{n:00}", StatName(type));
                var slot = pageA.Control($"ID_NDLGINVEN_SLOTN_{n:00}");
                if (slot)
                {
                    if (row == 0) PartSlot((RectTransform)slot.transform, type);
                    else SecondRowSlot((RectTransform)slot.transform, type);
                }
                var opt = pageA.Control($"ID_NDLGINVEN_OPTSLOTN_{n:00}");
                if (opt) OptionSlot((RectTransform)opt.transform, CarParts.Index(type, row), n);
            }
            pageA.SetVisible("ID_NDLGINVEN_SET_PARTS_OUTLINE2", false);
            if (pageC)
            {
                // the four table rows show the tip box 4790000 on hover (texts 11730-11741)
                string[] btns = { "SPEED", "ACCEL", "DAMAGE", "BOOST" };
                for (int i = 0; i < 4; i++)
                {
                    int stat = i;
                    var b = pageC.Control("ID_NDLGINVEN_ATTR_BTN_" + btns[i]);
                    if (!b) continue;
                    var s = ItemSlot.On((RectTransform)b.transform);
                    s.Hover = on => { if (on) ShowTip(stat, (RectTransform)b.transform); else HideTip(); };
                }
                foreach (var id in new[] { "ID_NDLGINVEN_ATTR_TXT_BLOCKING_1", "ID_NDLGINVEN_ATTR_TXT_BLOCKING_2", "ID_NDLGINVEN_ATTR_TXT_BLOCKING_3", "ID_NDLGINVEN_ATTR_TXT_BLOCKING_4", "ID_NDLGINVEN_ATTR_TXT_BLOCKING_TXT" })
                    pageC.SetVisible(id, false);
                foreach (var (id, key, fb) in new[]
                {
                    ("TOP_INFO", "11756", "Vehicle Performance Information"), ("TOP_POINT", "11757", "Point Type"), ("TOP_SUM", "11758", "Total"),
                    ("TOP_CAR", "11759", "Vehicle"), ("TOP_PARTS", "11760", "Parts"), ("TOP_LEVEL", "11761", "Level"), ("TOP_ASSIST", "11762", "Assist Option"),
                    ("SPD", "11764", "Max Speed"), ("ACC", "11765", "Accel Time"), ("DMG", "11766", "Chase Damage"), ("BST", "11767", "Boost Time"),
                    ("POINT_SPD", "11751", "Speed"), ("POINT_ACC", "11752", "Accel"), ("POINT_DMG", "11753", "Durability"), ("POINT_BST", "11754", "Boost"),
                    ("ASSISTDMG", "11768", "Damage Bonus"), ("ASSISTBST", "11769", "sec Boost Time"),
                })
                    pageC.SetText("ID_NDLGINVEN_ATTR_TXT_" + id, ItemCatalog.Text(key, fb));
            }
        }

        static string StatName(int type) => ItemCatalog.Text((11751 + type).ToString(), new[] { "Speed", "Accel", "Durability", "Boost" }[type]);

        static void RefreshPartsNdlg()
        {
            if (!pageA) return;
            var owned = P.Cars[carSlot];
            var info = GameTables.Car(owned.VehicleIndex);
            if (pageB)
            {
                OriginalMenus.Plate(pageB, "ID_INVEN_CARINFO_CARNAME_POS", OriginalMenus.CarName(owned), info);
                OriginalMenus.Emblem(pageB, "ID_INVEN_CARINFO_CARGRADE_POS", ShopData.Grade(info, owned.Grade));
                pageB.SetVisible("ID_INVEN_CARINFO_CURCAR", carSlot == P.CurrentCar);
                ShowRent(pageB, owned);
                pageB.SetEnabled("ID_INVEN_CARINFO_PREVCAR", P.Cars.Count > 1);
                pageB.SetEnabled("ID_INVEN_CARINFO_NEXTCAR", P.Cars.Count > 1);
            }
            for (int n = 1; n <= 8; n++)
            {
                int type = (n - 1) / 2, row = (n - 1) % 2;
                var e = row == 0 ? FittedEntry(type) : SecondRowEntry(type);
                // the option part docked on this slot (OPTSLOTN_nn frame, OPTSLOT_nn = its 32 px icon place)
                var optFrame = pageA.Control($"ID_NDLGINVEN_OPTSLOTN_{n:00}");
                if (optFrame)
                {
                    OriginalMenus.Remove(optFrame.transform, "item");
                    var oe = OptionEntry(CarParts.Index(type, row));
                    if (oe != null)
                    {
                        var tile = OriginalMenus.ItemTile(optFrame.transform, new Vector2(1.5f, 1.5f), new Vector2(32, 32), oe.Icon, oe.Name, null, 0, e != null ? 1f : 0.4f);
                        foreach (var g in tile.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
                    }
                }
                var slot = pageA.Control($"ID_NDLGINVEN_SLOTN_{n:00}");
                if (slot)
                {
                    OriginalMenus.Remove(slot.transform, "item");
                    if (e != null)
                    {
                        var tile = OriginalMenus.ItemTile(slot.transform, new Vector2(7, 7), new Vector2(49, 49), e.Icon, e.Name, null, 0, ItemPeriods.IsExpired(e) ? 0.5f : 1f);
                        foreach (var g in tile.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
                    }
                }
                bool badge = e != null && e.Part.Enhance > 0;
                pageA.SetVisible($"ID_NDLGINVEN_UPGRADE{n}", badge);
                if (badge) { pageA.SetText($"ID_NDLGINVEN_UPGRADE{n}", $"+{e.Part.Enhance}"); pageA.Control($"ID_NDLGINVEN_UPGRADE{n}")?.transform.SetAsLastSibling(); }
                pageA.SetVisible($"ID_NDLGINVEN_RENT{n}", e != null && (e.Part.Until > 0 || SetParts.Of(e.Part)?.Rental == true));
                // the grade line: one colour per grade, blue for set parts, black when the slot is empty
                string pre = $"ID_NDLGINVEN_{StatLetter[type]}{row + 1}_";
                string colour = e == null ? null : SetParts.Of(e.Part) != null ? "BLUE" : GradeColour[Mathf.Clamp(e.Part.Grade, 0, 3)];
                foreach (var c in AllColours)
                    foreach (var k in new[] { "GLINE_", "GSLOT_", "GOPSLOT_" })
                        pageA.SetVisible(pre + k + c, c == colour);
                pageA.SetVisible(pre + "GLINE_BLACK", colour == null);
            }
            pageA.SetVisible("ID_NDLGINVEN_SET_PARTS_OUTLINE1", SetParts.CarSet(P, owned).set != null);
            FillAttr(owned);
        }

        /// <summary>The car info's RENT mark while the shown car is a rental (Services.RentalCars), with its time left.</summary>
        internal static void ShowRent(NuiView v, OwnedCar owned)
        {
            if (!v) return;
            bool rent = Services.RentalCars.IsRental(owned);
            v.SetVisible("ID_INVEN_CARINFO_RENT", rent);
            var c = v.Control("ID_INVEN_CARINFO_RENT");
            if (!c) return;
            if (rent) OriginalMenus.Caption(c.transform, "rent left", Services.RentalCars.TimeLeft(owned), "Calibri 11 700", new Color(1f, 0.85f, 0.4f), TextAnchor.LowerCenter);
            else OriginalMenus.Remove(c.transform, "rent left");
        }

        static MethodInfo levelTable;
        /// <summary>The car model's level curves (DriftCar's LevelTable: 2000 top speed, 3000 engine force, 5000 boost time).</summary>
        static float Level(int id, int level)
        {
            if (levelTable == null)
                levelTable = typeof(DriftCar).GetNestedType("LevelTable", BindingFlags.NonPublic | BindingFlags.Public)?.GetMethod("Get", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            try { return levelTable != null ? (float)levelTable.Invoke(null, new object[] { id, level }) : 0f; } catch { return 0f; }
        }

        /// <summary>
        /// The performance table: per stat the total = vehicle (grade + custom parts) + parts (performance parts and the set
        /// bonus) + level (0: this rebuild has no level points). Max speed and boost time come from the car model's curves;
        /// accel time is the model's 0-100 km/h estimate, chase damage the chase's hit range (6 - 45) [remake].
        /// </summary>
        static void FillAttr(OwnedCar owned)
        {
            if (!pageC) return;
            var (car, part, user) = OriginalMenus.Breakdown(owned);
            string[] keys = { "SPEED", "ACCEL", "DAMAGE", "BOOST" };
            var total = new int[4];
            for (int i = 0; i < 4; i++)
            {
                total[i] = car[i] + part[i] + user[i];
                pageC.SetText($"ID_NDLGINVEN_ATTR_{keys[i]}_POINT", total[i].ToString());
                pageC.SetText($"ID_NDLGINVEN_ATTR_{keys[i]}_POINT_CAR", (car[i] + part[i]).ToString());
                pageC.SetText($"ID_NDLGINVEN_ATTR_{keys[i]}_POINT_PARTS", user[i].ToString());
                pageC.SetText($"ID_NDLGINVEN_ATTR_{keys[i]}_POINT_LEVEL", "0");
            }
            float top = Level(2000, total[0]);
            pageC.SetText("ID_NDLGINVEN_ATTR_MAXSPEED", top > 0 ? Mathf.RoundToInt(top).ToString() : "-");
            float force = Level(3000, total[1]);
            pageC.SetText("ID_NDLGINVEN_ATTR_ACCELTIME", force > 0 ? Mathf.Clamp(27.8f / (force / 60f), 2f, 30f).ToString("00.00") : "-");
            pageC.SetText("ID_NDLGINVEN_ATTR_DAMAGE", "6 - 45");
            float boost = Level(5000, total[3]);
            pageC.SetText("ID_NDLGINVEN_ATTR_BOOSTTIME", boost > 0 ? boost.ToString("00.00") : "-");
            // assist options (CarAssists: option parts and an active set): km/h on the top speed (x 0.8 for the Drift City
            // service, 0x47fde0), the set's name, the HUV damage bonus and the boost seconds added (overturbo)
            var (set, active, parts) = SetParts.CarSet(P, owned);
            var assists = CarAssists.For(P, owned);
            float kmhAdd = assists.SpeedKmh * DriftCar.ServiceSpeedFactor;
            if (active && parts.Count > 0)
            {
                var m = Regex.Match(SetParts.Of(parts[0]).Bonus ?? "", @"\+\s*(\d+(\.\d+)?)\s*km/?h", RegexOptions.IgnoreCase);
                if (m.Success && float.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float setKmh) && assists.SpeedKmh <= 0f) kmhAdd = setKmh;
            }
            pageC.SetText("ID_NDLGINVEN_ATTR_ASSIST_SPEED", "+ " + kmhAdd.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
            string named = active ? SetParts.Of(parts[0]).SetName : assists.Rows.Count > 0 ? assists.Rows[0].Name : "None";
            pageC.SetText("ID_NDLGINVEN_ATTR_ASSIST_ACCEL", named);
            pageC.SetText("ID_NDLGINVEN_ATTR_ASSIST_DAMAGE", "+ " + Mathf.RoundToInt(assists.HuvDamage * 100f));
            pageC.SetText("ID_NDLGINVEN_ATTR_ASSIST_BOOST", "+ " + assists.BoostTimeAdd.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            if (top > 0) pageC.SetText("ID_NDLGINVEN_ATTR_MAXSPEED", Mathf.RoundToInt(top + kmhAdd).ToString());
            if (boost > 0) pageC.SetText("ID_NDLGINVEN_ATTR_BOOSTTIME", (boost + assists.BoostTimeAdd).ToString("00.00"));
        }

        // ================= the second row and the option slots (Inventory/CarParts.cs) =================
        static BagEntry SecondRowEntry(int type)
        {
            var car = P?.Cars[Mathf.Clamp(carSlot, 0, P.Cars.Count - 1)];
            var part = CarParts.Part(P, car, type, 1);
            return part != null ? new BagEntry { Kind = BagKind.Part, Part = part } : null;
        }

        static BagEntry OptionEntry(int index)
        {
            var car = P?.Cars[Mathf.Clamp(carSlot, 0, P.Cars.Count - 1)];
            string id = CarParts.OptionIn(car, index);
            return string.IsNullOrEmpty(id) ? null : new BagEntry { Kind = BagKind.Stack, Id = id };
        }

        /// <summary>A second-row slot: drop a performance part of the column's stat; right / double click takes it off.</summary>
        static void SecondRowSlot(RectTransform rt, int type)
        {
            var s = ItemSlot.On(rt);
            s.Entry = () => SecondRowEntry(type);
            s.RightClick = e => { if (e != null) CarParts.UnfitRow2(carSlot, type, _ => RefreshIfOpen()); };
            s.DoubleClick = e => { if (e != null) CarParts.UnfitRow2(carSlot, type, _ => RefreshIfOpen()); };
            s.Accept = e => e.Kind == BagKind.Part;
            s.Dropped = e =>
            {
                if (e.Part.Type != type) { UiPopup.Message(ItemCatalog.Text("10933", "You cannot equip this part.")); return; }
                CarParts.FitRow2(carSlot, e.Part.Uid, _ => RefreshIfOpen());
            };
        }

        /// <summary>An option slot: drop an option / docking part from the bag; right click takes it off.</summary>
        static void OptionSlot(RectTransform rt, int index, int n)
        {
            var s = ItemSlot.On(rt);
            s.Entry = () => OptionEntry(index);
            s.RightClick = e => { if (e != null) CarParts.RemoveOption(carSlot, index, _ => RefreshIfOpen()); };
            s.DoubleClick = e => { if (e != null) CarParts.RemoveOption(carSlot, index, _ => RefreshIfOpen()); };
            s.Accept = e => e.Kind == BagKind.Stack && CarParts.IsOption(e.Id);
            s.Dropped = e => CarParts.FitOption(carSlot, index, e.Id, _ => RefreshIfOpen());
            s.Hover = on => pageA?.SetVisible($"ID_NDLGINVEN_OPSEL{n}", on);
        }

        // ================= the performance tips (NDLGINVEN 4790000) =================
        static NuiView tipBox;
        public static NuiView TipBox => tipBox;

        /// <summary>
        /// The table row's tip (4790000: TIP_TITLE / TIP_TEXT / TIP_DETAIL): 11730-11733 "■ Max Speed" .., 11734-11737 the
        /// one-line explanation, 11738-11741 the detail (Speed / Acceleration / Durability / Boost), placed under the row.
        /// </summary>
        public static void ShowTip(int stat, RectTransform near)
        {
            stat = Mathf.Clamp(stat, 0, 3);
            if (!tipBox) tipBox = UiManager.Instance ? UiManager.Instance.Open(4790000) : null;
            if (!tipBox) return;
            string[] title = { "■ Max Speed", "■ Time to reach", "■ Crash Damage", "■ Boost Time" };
            string[] text = { "Your vehicle's max speed on a flat road.", "Time to reach vehicle's max speed.", "Damage range you deal to a HUV.", "Time length of your boost." };
            tipBox.SetText("ID_NDLGINVEN_TIP_TITLE", ItemCatalog.Text((11730 + stat).ToString(), title[stat]));
            tipBox.SetText("ID_NDLGINVEN_TIP_TEXT", ItemCatalog.Text((11734 + stat).ToString(), text[stat]));
            tipBox.SetText("ID_NDLGINVEN_TIP_DETAIL", ItemCatalog.Clean(ItemCatalog.Text((11738 + stat).ToString(), "")).Trim('"'));
            foreach (var g in tipBox.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
            var root = UiManager.Instance.Root as RectTransform;
            if (root && near)
            {
                var corners = new Vector3[4];
                near.GetWorldCorners(corners);
                var bl = root.InverseTransformPoint(corners[0]);
                PlaceAt(tipBox, new Vector2(bl.x, bl.y - 4f));
            }
            tipBox.transform.SetAsLastSibling();
        }

        public static void HideTip()
        {
            if (tipBox && UiManager.Instance) UiManager.Instance.Close(tipBox);
            tipBox = null;
        }

        // ================= the set box (INVEN 3180000 / NDLGINVEN 4870000) =================
        /// <summary>The set a part belongs to: title, its bonus, "complete the set" line and the four parts (lit when fitted).</summary>
        static void SetBoxShow(PerfPart part, RectTransform near)
        {
            var row = SetParts.Of(part);
            if (row == null || !frame) { SetBoxHide(); return; }
            if (!setBox) setBox = OpenSet(3180000, id => UiManager.Instance.Open(id));
            if (!setBox) return;
            var owned = P.Cars[carSlot];
            var (set, active, _) = SetParts.CarSet(P, owned);
            setBox.SetText("ID_INVEN_SETPARTS_TITLE", row.SetName);
            setBox.SetText("ID_INVEN_SETPARTS_DESC1ST", ItemCatalog.Clean(row.Bonus));
            setBox.SetText("ID_INVEN_SETPARTS_IFYOUGET1ST", set == row.Set ? (active ? "Set bonus active." : ItemCatalog.Text("10798", "Can be restored using Restoration Lube"))
                                                                             : "Fit all four parts of the set to get its bonus.");
            var members = SetParts.Members(row.Set);
            string[] ord = { "1ST", "2ND", "3RD", "4TH" };
            for (int i = 0; i < 4; i++)
            {
                var m = i < members.Count ? members[i] : null;
                var pos = setBox.Control("ID_INVEN_SETPARTS_PARTSPOS" + ord[i]);
                bool fitted = m != null && owned.PerfSlots[m.Type] >= 0 && SetParts.Of(P.PerfParts.Find(x => x.Uid == owned.PerfSlots[m.Type]))?.Set == row.Set;
                setBox.SetText("ID_INVEN_SETPARTS_PARTSTEXT" + ord[i], m?.Name ?? "");
                if (pos)
                {
                    OriginalMenus.Remove(pos.transform, "item");
                    if (m != null) OriginalMenus.ItemTile(pos.transform, Vector2.zero, new Vector2(24, 24), m.Id, m.Name, null, 0, fitted ? 1f : 0.35f);
                }
            }
            foreach (var g in setBox.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
            // under the slot (the client places the box at run time; its layout sits far off its origin)
            var root = UiManager.Instance.Root as RectTransform;
            if (root && near)
            {
                var corners = new Vector3[4];
                near.GetWorldCorners(corners);
                var bl = root.InverseTransformPoint(corners[0]);
                PlaceAt(setBox, new Vector2(bl.x, bl.y - 6f));
            }
            setBox.transform.SetAsLastSibling();
        }

        /// <summary>
        /// Moves a dialog so the top-left corner of its controls sits at a canvas point (centre origin, y up), kept on screen.
        /// For boxes the client positions at run time (the set box, the restore box).
        /// </summary>
        static void PlaceAt(NuiView v, Vector2 topLeft)
        {
            var origin = v ? v.transform.Find("origin") as RectTransform : null;
            var root = UiManager.Instance ? UiManager.Instance.Root as RectTransform : null;
            if (!origin || !root) return;
            float minX = float.MaxValue, maxY = float.MinValue, maxX = float.MinValue, minY = float.MaxValue;
            foreach (RectTransform c in origin)
            {
                if (!c.gameObject.activeSelf) continue;
                var p = c.anchoredPosition;
                minX = Mathf.Min(minX, p.x); maxY = Mathf.Max(maxY, p.y);
                maxX = Mathf.Max(maxX, p.x + c.sizeDelta.x); minY = Mathf.Min(minY, p.y - c.sizeDelta.y);
            }
            if (minX == float.MaxValue) return;
            Vector2 size = new Vector2(maxX - minX, maxY - minY);
            topLeft.x = Mathf.Clamp(topLeft.x, root.rect.xMin, root.rect.xMax - size.x);
            topLeft.y = Mathf.Clamp(topLeft.y, root.rect.yMin + size.y, root.rect.yMax);
            var local = root.InverseTransformPoint(origin.position);
            var shift = topLeft - new Vector2(local.x + minX, local.y + maxY);
            origin.position = root.TransformPoint(new Vector3(local.x + shift.x, local.y + shift.y, 0));
        }

        /// <summary>The classic dialogs both sets share (licence page) sit 40 px left and 62 px up in the NDLG frame.</summary>
        static void ShiftForNdlg(NuiView v)
        {
            if (!ndlg || !v) return;
            var origin = v.transform.Find("origin") as RectTransform;
            if (origin) origin.anchoredPosition += new Vector2(-40f, 62f);
        }

        static void SetBoxHide()
        {
            if (setBox && UiManager.Instance) UiManager.Instance.Close(setBox);
            setBox = null;
        }

        public static NuiView SetBox => setBox;

        // ================= the restore box (INVEN 3610000 SETACTIVATE) =================
        /// <summary>
        /// The lube for expired set parts: its name, how many the driver has, the price; USE restores every expired part of
        /// its sets on the shown car (set.restore), BUY buys one (set.buyLube). Right click on an expired set part, or using a
        /// lube from the bag, opens it.
        /// </summary>
        public static void ShowRestore(string lube)
        {
            if (!frame) { Open(Page.Parts); if (!frame) return; }
            restoreLube = lube;
            if (!restoreBox)
            {
                restoreBox = OriginalMenus.Open(3610000);
                if (!restoreBox) return;
                Ui.On("ID_INVEN_SETACTIVATE_USE", _ =>
                {
                    if (P.Count(restoreLube) <= 0) { UiPopup.Message("You need a Restoration Lube. BUY gets one."); return; }
                    if (SetParts.Restorable(P, P.Cars[carSlot], restoreLube).Count == 0) { UiPopup.Message("Nothing on this car needs this lube."); return; }
                    UiPopup.Show(ItemCatalog.Text("10884", "Part will be restored."), b => { if (b == UiPopup.Yes) SetParts.Restore(carSlot, restoreLube, ok => { if (ok) CloseRestore(); }); }, UiPopup.Yes, UiPopup.No);
                });
                Ui.On("ID_INVEN_SETACTIVATE_BUY", _ =>
                {
                    int price = SetParts.LubePrice.TryGetValue(restoreLube, out int v) ? v : 0;
                    UiPopup.Show($"{ItemCatalog.Name(new BagEntry { Kind = BagKind.Stack, Id = restoreLube })}\nBuy one for {price:N0} Mito?",
                        b => { if (b == UiPopup.Yes) SetParts.BuyLube(restoreLube, _ => RefreshRestore()); }, UiPopup.Yes, UiPopup.No);
                });
            }
            RefreshRestore();
            // beside the bag, at its top (the client moves the box at run time; its layout is at the screen corner)
            var slot1 = bag ? bag.Control("ID_MYINVEN_SLOT_1") : null;
            var root = UiManager.Instance.Root as RectTransform;
            if (slot1 && root)
            {
                var corners = new Vector3[4];
                ((RectTransform)slot1.transform).GetWorldCorners(corners);
                var tl = root.InverseTransformPoint(corners[1]);
                PlaceAt(restoreBox, new Vector2(tl.x - 160f, tl.y));
            }
            restoreBox.transform.SetAsLastSibling();
            UiPopup.BringToFront();
        }

        static void RefreshRestore()
        {
            if (!restoreBox) return;
            bool alpha = restoreLube == SetParts.AlphaLube;
            string name = ItemCatalog.Name(new BagEntry { Kind = BagKind.Stack, Id = restoreLube });
            restoreBox.SetText("ID_INVEN_SETACTIVATE_NAME", name);
            restoreBox.SetText("ID_INVEN_SETACTIVATE_COUNT", $"Owned: {P.Count(restoreLube)}");
            int price = SetParts.LubePrice.TryGetValue(restoreLube, out int v) ? v : 0;
            restoreBox.SetText("ID_INVEN_SETACTIVATE_DESC", $"{ItemCatalog.Clean(ItemCatalog.ShopByName(restoreLube)?.Desc)}\n{price:N0} Mito");
            // the four icons share one place: the G-Coin lubes (1ST / 2ND) and the Mito ones (3RD / 4TH); this rebuild sells for Mito
            restoreBox.SetVisible("ID_INVEN_SETACTIVATE_POSITEM1ST", false);
            restoreBox.SetVisible("ID_INVEN_SETACTIVATE_POSITEM2ND", false);
            restoreBox.SetVisible("ID_INVEN_SETACTIVATE_POSITEM3RD", alpha);
            restoreBox.SetVisible("ID_INVEN_SETACTIVATE_POSITEM4TH", !alpha);
            restoreBox.SetEnabled("ID_INVEN_SETACTIVATE_USE", P.Count(restoreLube) > 0);
        }

        static void CloseRestore()
        {
            OriginalMenus.CloseView(restoreBox);
            restoreBox = null;
        }

        public static NuiView RestoreBox => restoreBox;

        // ================= period marks in the bag =================
        /// <summary>
        /// Period items in the bag: SLOT_RENTnn on a slot whose item has a running period; the NDLG bag also has an expiry mark
        /// per slot (EXPIRE_1 and the unnamed 4800095-4800113, group "기간만료_마크", the red no-entry picture) for expired
        /// items. The classic bag has no expiry mark: RENT and the dimmed tile show it there.
        /// </summary>
        static void MarkPeriods()
        {
            if (!bag) return;
            var marks = bag.GetComponentsInChildren<NuiControlView>(true)
                .Where(c => (c.Data.I("I1_1") ?? "").Contains("864,772~910,818")).OrderBy(c => c.Data.Id).ToList();
            for (int i = 0; i < PerPage; i++)
            {
                var e = EntryAt(i);
                bool expired = e != null && ItemPeriods.IsExpired(e);
                bool running = e != null && ItemPeriods.IsTimed(e) && !expired;
                bag.SetVisible($"ID_MYINVEN_SLOT_RENT{i:00}", running || expired && marks.Count == 0);
                var rent = bag.Control($"ID_MYINVEN_SLOT_RENT{i:00}");
                if (rent && rent.gameObject.activeSelf) rent.transform.SetAsLastSibling();
                if (i < marks.Count)
                {
                    marks[i].gameObject.SetActive(expired);
                    if (expired) marks[i].transform.SetAsLastSibling();
                    foreach (var g in marks[i].GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
                }
            }
        }

        /// <summary>Bag slots marked as expired (NDLG expiry marks shown), for tests.</summary>
        public static int ExpiredMarks => bag ? bag.GetComponentsInChildren<NuiControlView>(false).Count(c => (c.Data.I("I1_1") ?? "").Contains("864,772~910,818")) : 0;
    }
}
