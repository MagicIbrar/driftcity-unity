using DriftCity.Core;
using System.Linq;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace DriftCity.Game
{
    /// <summary>
    /// Item quick slots while driving: ITEMQUICKSLOT 360000 (tiles and item icons) + 390000 (cooldown covers, counts, the
    /// number keys J=49/50..., the auto checkboxes CHK_NUMn). The client's quick-slot class (vtable 0x86d7f0; show 0x54e150,
    /// slot reset 0x54ea30, cooldown cover 0x54eab0, mouse use 0x54e970 -> 0x54ded0, 390000 handler 0x54f180) shows 2 slots
    /// plus the extension count (+0x194: 0..2, tiles below count + 2 are enabled); with the NDLG inventory open the panel
    /// 4760000 / 4770000 ("FIELD", 0x54e3a0: 122 / 191 / 260 px high for 2 / 3 / 4 slots) takes their place for drag and drop.
    /// Items are dragged from the bag onto a tile; the number key or a click uses one, right click empties the tile.
    /// Only consumables go here ("Permanent effect items cannot be used in this area." 10170 for the rest).
    /// Auto quick slot [exact]: with i_autoquick in the bag (0x512730 sets char+0xf39) the CHK_NUMn boxes show on slots
    /// whose item has a duration; ticking one (0x54e8f0) uses it at once and the slot re-uses it whenever it may again;
    /// without the item: "In order to enable the Auto Quick Slot option, please purchase the item in the Drift Shop." (10970).
    /// This rebuild: slots and auto flags are kept per driver on this PC, 3 s cooldown between uses of a slot; auto re-uses a
    /// drink when its effect has run out; the extension count is the driver's "qslot_ext" stack (0..2, the server's to give:
    /// the client tables have no extension product, the count came with the character).
    /// </summary>
    public static class QuickSlots
    {
        public const int MaxSlots = 4;
        /// <summary>Remake id of the slot extension (one per extra slot, at most two).</summary>
        public const string ExtensionItem = "qslot_ext";
        public const string AutoItem = "i_autoquick";
        public static int Extension => P == null ? 0 : Mathf.Clamp(P.Count(ExtensionItem), 0, 2);
        /// <summary>The client's count (0x54e3a0: extension + 2).</summary>
        public static int Unlocked => 2 + Extension;
        public const float Cooldown = 3f;

        static PlayerProfile P => PlayerProfile.Current;
        static readonly string[] slots = new string[MaxSlots];
        static readonly bool[] auto = new bool[MaxSlots];
        static readonly float[] coolUntil = new float[MaxSlots];
        static string loadedFor;

        static string PrefKey(PlayerProfile p) => $"DriftCity.QuickSlots.{(p.OnlineDriver ? "online" : "offline" + PlayerProfile.Slot)}.{p.Name}";

        static void Load()
        {
            if (P == null) return;
            string key = PrefKey(P);
            if (loadedFor == key) return;
            loadedFor = key;
            var parts = PlayerPrefs.GetString(key, "").Split('|');
            for (int i = 0; i < MaxSlots; i++) slots[i] = i < parts.Length && parts[i].Length > 2 ? parts[i] : null;
            var flags = PlayerPrefs.GetString(key + ".auto", "").Split('|');
            for (int i = 0; i < MaxSlots; i++) auto[i] = i < flags.Length && flags[i] == "1";
        }

        static void Save()
        {
            if (P == null) return;
            PlayerPrefs.SetString(PrefKey(P), string.Join("|", slots.Select(s => s ?? "")));
            PlayerPrefs.SetString(PrefKey(P) + ".auto", string.Join("|", auto.Select(a => a ? "1" : "0")));
            PlayerPrefs.Save();
        }

        /// <summary>The bag key in a slot ("s:i_d_drk_01"), or null.</summary>
        public static string Get(int i) { Load(); return i >= 0 && i < MaxSlots ? slots[i] : null; }

        /// <summary>The bag entry in a slot as the profile holds it now (null when empty or used up).</summary>
        public static BagEntry Entry(int i) => ItemCatalog.Find(P, Get(i));

        public static bool Assign(int i, BagEntry e)
        {
            Load();
            if (i < 0 || i >= Unlocked || e == null) return false;
            if (!ItemUse.QuickUsable(e))
            {
                UiPopup.Message(ItemCatalog.Text("10170", "Permanent effect items cannot be used in this area."));
                return false;
            }
            for (int k = 0; k < MaxSlots; k++) if (slots[k] == e.Key) { slots[k] = null; auto[k] = false; }
            slots[i] = e.Key;
            auto[i] = false;
            Save();
            AudioLibrary.PlaySfx("sfx/SID_ITEM_DROP");
            Refresh();
            return true;
        }

        public static void Clear(int i)
        {
            Load();
            if (i < 0 || i >= MaxSlots) return;
            slots[i] = null;
            auto[i] = false;
            Save();
            Refresh();
        }

        public static float CooldownLeft(int i) => i >= 0 && i < MaxSlots ? Mathf.Max(0f, coolUntil[i] - Time.time) : 0f;

        /// <summary>Uses the item in slot i (number key / click): no popups, the chat log says what happened.</summary>
        public static bool Use(int i)
        {
            if (P == null || i < 0 || i >= Unlocked) return false;
            if (CooldownLeft(i) > 0f) return false;
            var e = Entry(i);
            if (e == null) return false;
            if (UndercityStage.Active) { ChatBox.System(ItemCatalog.Text("11478", "This item cannot be used in Undercity. Please move to the city and make the request again.")); return false; }
            coolUntil[i] = Time.time + Cooldown;
            ItemUse.Use(e, true, _ => Refresh());
            return true;
        }

        // ---------------- auto quick slot ----------------
        /// <summary>The Auto Quick-Slot item (i_autoquick, a 7 / 30 day period item) is in the bag and still running.</summary>
        public static bool HasAuto
        {
            get
            {
                if (P == null || P.Count(AutoItem) <= 0) return false;
                return !ItemPeriods.Expired(P.Items.Find(x => x.Id == AutoItem)?.Until ?? 0);
            }
        }

        /// <summary>Items with a duration can be re-used automatically (the drinks; the client checks the item's time field).</summary>
        public static bool AutoCapable(BagEntry e) => e != null && e.Kind == BagKind.Stack && ItemUse.Drinks.ContainsKey(e.Id);

        public static bool Auto(int i) { Load(); return i >= 0 && i < MaxSlots && auto[i]; }

        /// <summary>CHK_NUMn: ticking uses the item now and keeps re-using it; needs the Auto Quick-Slot item (10970).</summary>
        public static bool SetAuto(int i, bool on)
        {
            Load();
            if (i < 0 || i >= Unlocked) return false;
            if (on && !HasAuto)
            {
                UiPopup.Message(ItemCatalog.Text("10970", "In order to enable the Auto Quick Slot option, please purchase the item in the Drift Shop."));
                on = false;
            }
            if (on && !AutoCapable(Entry(i))) on = false;
            auto[i] = on;
            Save();
            if (on) Use(i);
            Refresh();
            return on;
        }

        /// <summary>Re-uses auto slots whose drink has run out (and drops the flag when the item or the Auto item is gone).</summary>
        static void AutoTick()
        {
            if (P == null) return;
            for (int i = 0; i < Unlocked; i++)
            {
                if (!auto[i]) continue;
                var e = Entry(i);
                if (e == null || !HasAuto || !AutoCapable(e)) { auto[i] = false; Save(); Refresh(); continue; }
                if (CooldownLeft(i) > 0f || UndercityStage.Active) continue;
                if (ItemUse.Drinks[e.Id].Any(k => ItemUse.HasBuff(P, k))) continue;
                Use(i);
            }
        }

        public static void Refresh() { if (Driver.Instance) Driver.Instance.Redraw(); }

        /// <summary>The HUD views, when shown.</summary>
        public static NuiView Tiles => Driver.Instance ? Driver.Instance.tiles : null;
        public static NuiView Covers => Driver.Instance ? Driver.Instance.covers : null;
        /// <summary>The NDLG inventory's field panel (4760000 frame, 4770000 slots), while the NDLG inventory is open.</summary>
        public static NuiView PanelBase => Driver.Instance ? Driver.Instance.panelBase : null;
        public static NuiView PanelSlots => Driver.Instance ? Driver.Instance.panelSlots : null;

        // ---------------- runtime driver ----------------
        // Started for every play session without a scene object: Canvas.willRenderCanvases ticks once a frame whenever the
        // UI exists, and (re)creates the driver after tests or scene changes destroyed it. It shows the slots together with
        // the city HUD (the speedometer 320000), hides them in PvP races (the Battle Zone has its own item slots, 3780000).
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            Canvas.willRenderCanvases -= Tick;
            Canvas.willRenderCanvases += Tick;
        }

        static void Tick()
        {
            if (!Application.isPlaying || Driver.Instance || !UiManager.Instance) return;
            var go = new GameObject("Quick Slots");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<Driver>();
        }

        internal sealed class Driver : MonoBehaviour
        {
            public static Driver Instance { get; private set; }
            internal NuiView tiles, covers, panelBase, panelSlots;
            Net.NetClient attached;
            int shownCount = -1;

            void Awake() { Instance = this; }
            void OnDestroy() { if (Instance == this) Instance = null; Close(); ClosePanel(); if (attached) attached.Message -= TradeWindow.OnMessage; }

            bool ShouldShow()
            {
                var ui = UiManager.Instance;
                if (!ui || P == null || !CitySession.Current) return false;
                return ui.Find(320000) && !ui.Find(1380000);
            }

            void Update()
            {
                // trade messages from the server go to the trade window (no edit to OnlineSession needed)
                var net = OnlineSession.Instance ? OnlineSession.Instance.Net : null;
                if (net != attached)
                {
                    if (attached) attached.Message -= TradeWindow.OnMessage;
                    attached = net;
                    if (attached) attached.Message += TradeWindow.OnMessage;
                }

                if (P != null) ItemPeriods.Check();
                bool panel = InventoryWindow.IsNdlg;
                if (panel && !panelBase) OpenPanel();
                else if (!panel && panelBase) ClosePanel();

                bool show = ShouldShow();
                if (show && tiles && shownCount != Unlocked) Close();   // the extension changed
                if (show && !tiles) Open();
                else if (!show && tiles) Close();
                if (!tiles && !panelBase) return;
                Load();
                if (tiles) Keys();
                if (tiles && P != null && CitySession.Current) AutoTick();
                for (int i = 0; i < Unlocked; i++) Cover(i);
                if (Time.frameCount % 15 == 0) Counts();
            }

            void Keys()
            {
                var ui = UiManager.Instance;
                if (ChatBox.Typing || UiPopup.IsOpen || CountPopup.IsOpen || ui.Find(400000)) return;   // the station panel uses the digits
                var focused = UnityEngine.EventSystems.EventSystem.current ? UnityEngine.EventSystems.EventSystem.current.currentSelectedGameObject : null;
                if (focused && focused.GetComponent<InputField>()) return;
                for (int i = 0; i < Unlocked; i++)
                {
#if ENABLE_INPUT_SYSTEM
                    var kb = Keyboard.current;
                    if (kb == null) return;
                    var k = i == 0 ? kb.digit1Key : i == 1 ? kb.digit2Key : i == 2 ? kb.digit3Key : kb.digit4Key;
                    bool down = k.wasPressedThisFrame;
#else
                    bool down = Input.GetKeyDown(KeyCode.Alpha1 + i);
#endif
                    if (down) Use(i);
                }
            }

            void Open()
            {
                var ui = UiManager.Instance;
                tiles = ui.Open(360000);
                covers = ui.Open(390000);
                shownCount = Unlocked;
                foreach (var v in new[] { tiles, covers })
                {
                    if (!v) continue;
                    foreach (var c in v.Controls)
                    {
                        int k = SlotOf(c.Data.IdName);
                        if (k < 0) continue;
                        // the client places tiles 3 and 4 at run time below tile 2 (their layout repeats tile 2's place)
                        if (k >= 2)
                        {
                            var first = v.Control(Rename(c.Data.IdName, 0));
                            if (first) ((RectTransform)c.transform).anchoredPosition = ((RectTransform)first.transform).anchoredPosition + new Vector2(0, -73 * k);
                        }
                        c.gameObject.SetActive(k < Unlocked);
                    }
                }
                if (covers)
                    foreach (var c in covers.Controls)
                    {
                        string id = c.Data.IdName;
                        if (id.Contains("_COVER_NUM")) c.gameObject.SetActive(false);
                    }
                for (int i = 0; i < Unlocked; i++)
                {
                    int slot = i;
                    var tile = tiles ? tiles.Control("ID_ITEMQUICKSLOT_" + (i + 1)) : null;
                    if (!tile) continue;
                    var s = ItemSlot.On((RectTransform)tile.transform);
                    s.Entry = () => Entry(slot);
                    s.LeftClick = _ => Use(slot);
                    s.RightClick = _ => Clear(slot);
                    s.Dropped = e => Assign(slot, e);
                    s.Accept = e => true;
                    var num = covers ? covers.Control("ID_ITEMQUICKSLOT_BTN_NUM" + (i + 1)) : null;
                    if (num) UiManager.Instance.On(num.Data.IdName, _ => Use(slot));
                    if (i >= 2) DigitLabel(num, i + 1);
                    string chk = "ID_ITEMQUICKSLOT_CHK_NUM" + (i + 1);
                    UiManager.Instance.On(chk, v => { var c = v.Control(chk); bool on = SetAuto(slot, c && c.Checked); if (c && c.Checked != on) c.SetChecked(on); });
                }
                Redraw();
            }

            void Close()
            {
                var ui = UiManager.Instance;
                if (ui) { if (tiles) ui.Close(tiles); if (covers) ui.Close(covers); }
                tiles = covers = null;
            }

            // ---------------- the NDLG inventory's field panel ----------------
            void OpenPanel()
            {
                var ui = UiManager.Instance;
                panelBase = ui.Open(4760000);
                panelSlots = ui.Open(4770000);
                int n = Unlocked, ext = n - 2;
                if (panelBase)
                {
                    // 0x54e3a0: the frame is 122 / 191 / 260 px high, its bottom edge follows; slots 3 and 4 go below slot 2
                    var b = panelBase.Control("ID_NDLGINVEN_FIELD_QSLOT_SET_BASE");
                    if (b) { var rt = (RectTransform)b.transform; rt.sizeDelta = new Vector2(rt.sizeDelta.x, 122 + 69 * ext); var e1 = b.transform.Find("e1") as RectTransform; if (e1) e1.sizeDelta = new Vector2(e1.sizeDelta.x, 122 + 69 * ext); }
                    var bottom = panelBase.Control("ID_NDLGINVEN_FIELD_QSLOT_SET_BASEBOTTOM");
                    if (bottom) ((RectTransform)bottom.transform).anchoredPosition += new Vector2(0, -69 * ext);
                }
                foreach (var v in new[] { panelBase, panelSlots })
                {
                    if (!v) continue;
                    foreach (var c in v.Controls)
                    {
                        int k = PanelSlotOf(c.Data.IdName);
                        if (k < 0) continue;
                        if (k >= 2)
                        {
                            var first = v.Control(PanelRename(c.Data.IdName, 0));
                            if (first) ((RectTransform)c.transform).anchoredPosition = ((RectTransform)first.transform).anchoredPosition + new Vector2(0, -68 * k);
                        }
                        c.gameObject.SetActive(k < n && !c.Data.IdName.Contains("_SEL"));
                    }
                }
                for (int i = 0; i < n; i++)
                {
                    int slot = i;
                    var tile = panelSlots ? panelSlots.Control("ID_NDLGINVEN_FIELD_QSLOT" + (i + 1)) : null;
                    if (!tile) continue;
                    var s = ItemSlot.On((RectTransform)tile.transform);
                    s.Entry = () => Entry(slot);
                    s.RightClick = _ => Clear(slot);
                    s.Dropped = e => Assign(slot, e);
                    s.Accept = e => true;
                    UiManager.Instance.On("ID_NDLGINVEN_FIELD_QSLOT_BTN_NUM" + (i + 1), _ => Use(slot));
                    if (i >= 2 && panelSlots) DigitLabel(panelSlots.Control("ID_NDLGINVEN_FIELD_QSLOT_BTN_NUM" + (i + 1)), i + 1);
                }
                Redraw();
            }

            void ClosePanel()
            {
                var ui = UiManager.Instance;
                if (ui) { if (panelBase) ui.Close(panelBase); if (panelSlots) ui.Close(panelSlots); }
                panelBase = panelSlots = null;
            }

            static int PanelSlotOf(string id)
            {
                if (string.IsNullOrEmpty(id) || !id.StartsWith("ID_NDLGINVEN_FIELD_QSLOT")) return -1;
                if (id.EndsWith("SET_BASE") || id.EndsWith("SET_BASEBOTTOM")) return -1;
                char last = id[id.Length - 1];
                return last >= '1' && last <= '4' ? last - '1' : -1;
            }

            static string PanelRename(string id, int k) => id.Substring(0, id.Length - 1) + (k + 1);

            static readonly string[] Ord = { "1ST", "2ND", "3RD", "4TH" };

            /// <summary>Number keys 3 and 4 repeat key 2's picture in the layout (the client swaps it at run time): drawn as text here.</summary>
            static void DigitLabel(NuiControlView c, int digit)
            {
                if (!c) return;
                foreach (var ri in c.GetComponentsInChildren<RawImage>(true)) ri.enabled = false;
                var t = OriginalMenus.Caption(c.transform, "digit", digit.ToString(), "Calibri 11 700", Color.white, TextAnchor.MiddleCenter);
                if (t) t.raycastTarget = false;
            }

            /// <summary>Slot index from a control id ("..._2ND", "..._COUNT3", "ID_ITEMQUICKSLOT_4"), -1 for none.</summary>
            static int SlotOf(string id)
            {
                if (string.IsNullOrEmpty(id)) return -1;
                for (int k = 0; k < 4; k++) if (id.EndsWith("_" + Ord[k])) return k;
                char last = id[id.Length - 1];
                if (last >= '1' && last <= '4' && !char.IsDigit(id[id.Length - 2])) return last - '1';
                return -1;
            }

            static string Rename(string id, int k)
            {
                foreach (var o in Ord) if (id.EndsWith("_" + o)) return id.Substring(0, id.Length - o.Length) + Ord[k];
                return id.Substring(0, id.Length - 1) + (k + 1);
            }

            internal void Redraw()
            {
                Load();
                for (int i = 0; i < Unlocked; i++)
                {
                    var e = Entry(i);
                    var pos = tiles ? tiles.Control("ID_ITEMQUICKSLOT_BASE_ITEMPOS_" + Ord[i]) : null;
                    if (pos)
                    {
                        var ri = pos.GetComponentsInChildren<RawImage>(true).FirstOrDefault();
                        if (ri)
                        {
                            Rect uv = default;
                            var tex = e != null ? OriginalMenus.ItemIcon(e.Icon, out uv) : null;
                            if (e != null && !tex) tex = OriginalMenus.NotAvailableIcon(out uv);
                            ri.enabled = tex;
                            if (tex) { ri.texture = tex; ri.uvRect = uv; }
                            ri.raycastTarget = false;
                        }
                    }
                    // the auto box: only with the Auto Quick-Slot item and an item that lasts
                    var chk = covers ? covers.Control("ID_ITEMQUICKSLOT_CHK_NUM" + (i + 1)) : null;
                    if (chk)
                    {
                        chk.gameObject.SetActive(HasAuto && AutoCapable(e));
                        if (chk.Checked != auto[i]) chk.SetChecked(auto[i]);
                        chk.transform.SetAsLastSibling();
                    }
                    var itm = panelBase ? panelBase.Control("ID_NDLGINVEN_FIELD_QSLOT_ITM" + (i + 1)) : null;
                    if (itm)
                    {
                        foreach (var g in itm.GetComponentsInChildren<RawImage>(true)) if (g.transform.parent == itm.transform || g.transform == itm.transform) g.enabled = false;
                        OriginalMenus.Remove(itm.transform, "item");
                        if (e != null)
                        {
                            var tile = OriginalMenus.ItemTile(itm.transform, Vector2.zero, new Vector2(51, 51), e.Icon, e.Name, ItemCatalog.Swatch(e));
                            foreach (var g in tile.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
                        }
                    }
                }
                Counts();
            }

            void Counts()
            {
                for (int i = 0; i < Unlocked; i++)
                {
                    var e = Entry(i);
                    if (covers)
                    {
                        covers.SetVisible("ID_ITEMQUICKSLOT_COVER_COUNT" + (i + 1), e != null);
                        if (e != null) { covers.SetText("ID_ITEMQUICKSLOT_COVER_COUNT" + (i + 1), e.Count.ToString()); covers.Control("ID_ITEMQUICKSLOT_COVER_COUNT" + (i + 1))?.transform.SetAsLastSibling(); }
                    }
                    if (panelSlots)
                    {
                        panelSlots.SetVisible("ID_NDLGINVEN_FIELD_QSLOT_COUNT" + (i + 1), e != null);
                        if (e != null) panelSlots.SetText("ID_NDLGINVEN_FIELD_QSLOT_COUNT" + (i + 1), e.Count.ToString());
                    }
                }
            }

            /// <summary>The blue cover over a cooling slot shrinks as the cooldown runs out (0x54eab0 scales it by the percent left).</summary>
            void Cover(int i)
            {
                float left = CooldownLeft(i);
                foreach (var c in new[] { covers ? covers.Control("ID_ITEMQUICKSLOT_COVER_" + Ord[i]) : null, panelSlots ? panelSlots.Control("ID_NDLGINVEN_FIELD_QSLOT_COVER" + (i + 1)) : null })
                {
                    if (!c) continue;
                    c.gameObject.SetActive(left > 0f);
                    if (left <= 0f) continue;
                    var e1 = c.transform.Find("e1") as RectTransform;
                    if (e1 && NuiControl.TryVec2(c.Data.I("SZ1"), out float w, out float h)) e1.sizeDelta = new Vector2(w, h * left / Cooldown);
                }
            }
        }
    }
}
