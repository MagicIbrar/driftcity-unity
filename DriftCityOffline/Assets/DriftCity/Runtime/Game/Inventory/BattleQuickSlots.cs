using System.Collections.Generic;
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
    /// The Battle Zone quick slots (DriftCity_RE\INVENTORY.md "Battle quickslot", PVP.md 13.1): two slots used during a PvP race,
    /// PVP 3780000 (tiles BASE_TILE_1ST/2ND, icons BASE_ITEMPOS) + 3790000 (covers, counts, COVER_NUM keys J=49/50), shown when
    /// the server's feature bit BattleZone.BattleQuickSlot (0x10000, 0x580820) is on; they are filled outside the race in the
    /// inventory: DIALOG 3800000 (classic) or NDLGINVEN 4740000 / 4750000 ("BATTLE", the NDLG set; the slot icons at 57 px when
    /// net+0x294 & 0x600 == 0x200, 0x5ce900). The client uses the normal item request 0x78ce10.
    /// Items (vshopitem QUICKSLOT / useitemclient battle_quick, texts from their descriptions): Crash Capsule i_n_00055 (boost
    /// +5 % per traffic crash, 20 s), Boost Recharger i_n_00056 (gauge fills twice as fast, 40 s), Invincible Rush i_n_00058 /
    /// i_b_untouchable_* (10 s), DCR Capsule i_n_00059 / i_b_skidrush_* (Drift City at the next gate, 10 s), Emergency Boost Kit
    /// i_n_00060 / i_b_quickb_* (boost now with the gauge, needs 30 %), Mk.II Battle Quick Repair Kit i_n_00099 (30 % health).
    /// The first four only work "when passing a gate in a low position" (bottom half).
    /// This rebuild: the feature is on; repair and the recharger act on the car here, the others are announced through
    /// <see cref="Used"/> / <see cref="IsActive"/> for the race code (hooks in DriftCity_RE\INVENTORY.md); 3 s cooldown.
    /// </summary>
    public static class BattleQuickSlots
    {
        public const int Slots = 2;
        public const float Cooldown = 3f;
        public static bool Feature = true;

        /// <summary>Effect kind and seconds per battle item.</summary>
        public static readonly Dictionary<string, (string kind, float seconds)> Items = new Dictionary<string, (string, float)>
        {
            ["i_n_00055"] = ("crash", 20f), ["i_n_00056"] = ("recharge", 40f),
            ["i_n_00058"] = ("invincible", 10f), ["i_b_untouchable_10"] = ("invincible", 10f), ["i_b_untouchable_30"] = ("invincible", 10f),
            ["i_n_00059"] = ("dcr", 10f), ["i_b_skidrush_10"] = ("dcr", 10f), ["i_b_skidrush_30"] = ("dcr", 10f),
            ["i_n_00060"] = ("eboost", 0f), ["i_b_quickb_10"] = ("eboost", 0f), ["i_b_quickb_30"] = ("eboost", 0f),
            ["i_n_00099"] = ("repair", 0f),
        };

        static readonly HashSet<string> LowOnly = new HashSet<string> { "crash", "recharge", "invincible", "dcr" };

        /// <summary>Fired when a battle item is used: kind, seconds. The race code applies crash / invincible / dcr / eboost.</summary>
        public static event System.Action<string, float> Used;
        /// <summary>Set by the race HUD: true while the driver is in the bottom half of the field (null = always allowed).</summary>
        public static System.Func<bool> InLowerHalf;

        static readonly Dictionary<string, float> activeUntil = new Dictionary<string, float>();
        public static bool IsActive(string kind) => activeUntil.TryGetValue(kind, out float t) && t > Time.time;

        static PlayerProfile P => PlayerProfile.Current;
        static readonly string[] slots = new string[Slots];
        static readonly float[] coolUntil = new float[Slots];
        static string loadedFor;

        public static bool IsBattleItem(BagEntry e) => e != null && e.Kind == BagKind.Stack && Items.ContainsKey(e.Id);

        static string PrefKey(PlayerProfile p) => $"DriftCity.BattleSlots.{(p.OnlineDriver ? "online" : "offline" + PlayerProfile.Slot)}.{p.Name}";

        static void Load()
        {
            if (P == null) return;
            string key = PrefKey(P);
            if (loadedFor == key) return;
            loadedFor = key;
            var parts = PlayerPrefs.GetString(key, "").Split('|');
            for (int i = 0; i < Slots; i++) slots[i] = i < parts.Length && parts[i].Length > 2 ? parts[i] : null;
        }

        static void Save()
        {
            if (P == null) return;
            PlayerPrefs.SetString(PrefKey(P), string.Join("|", slots.Select(s => s ?? "")));
            PlayerPrefs.Save();
        }

        public static string Get(int i) { Load(); return i >= 0 && i < Slots ? slots[i] : null; }
        public static BagEntry Entry(int i) => ItemCatalog.Find(P, Get(i));
        public static float CooldownLeft(int i) => i >= 0 && i < Slots ? Mathf.Max(0f, coolUntil[i] - Time.time) : 0f;

        public static bool Assign(int i, BagEntry e)
        {
            Load();
            if (i < 0 || i >= Slots || e == null) return false;
            if (!IsBattleItem(e)) { UiPopup.Message("Only Battle Zone items go in the battle slots."); return false; }
            for (int k = 0; k < Slots; k++) if (slots[k] == e.Key) slots[k] = null;
            slots[i] = e.Key;
            Save();
            AudioLibrary.PlaySfx("sfx/SID_ITEM_DROP");
            Refresh();
            return true;
        }

        public static void Clear(int i)
        {
            Load();
            if (i < 0 || i >= Slots) return;
            slots[i] = null;
            Save();
            Refresh();
        }

        /// <summary>Uses slot i in a race: checks the item's condition, spends one (item.use) and applies / announces it.</summary>
        public static bool Use(int i)
        {
            if (P == null || i < 0 || i >= Slots || CooldownLeft(i) > 0f) return false;
            var e = Entry(i);
            if (e == null || !Items.TryGetValue(e.Id, out var fx)) return false;
            var car = GameFlow.Instance ? GameFlow.Instance.PlayerCar : null;
            if (LowOnly.Contains(fx.kind) && InLowerHalf != null && !InLowerHalf())
            {
                ChatBox.System("This item works only while you pass the gates in the lower half of the field.");
                return false;
            }
            if (fx.kind == "eboost" && car && car.BoostGauge < 0.3f)
            {
                ChatBox.System("The Boost Gauge must be 30% or higher.");
                return false;
            }
            coolUntil[i] = Time.time + Cooldown;
            string id = e.Id;
            Progress.Op("item.use", new Dictionary<string, object> { ["id"] = id, ["slot"] = P.CurrentCar }, () =>
            {
                if (P.Count(id) <= 0) return new Progress.Result { Error = "You don't have that." };
                P.Give(id, -1);
                return new Progress.Result { Ok = true };
            }, r =>
            {
                if (r.Ok) Apply(id, fx.kind, fx.seconds);
                Refresh();
            });
            return true;
        }

        static void Apply(string id, string kind, float seconds)
        {
            var car = GameFlow.Instance ? GameFlow.Instance.PlayerCar : null;
            if (seconds > 0f) activeUntil[kind] = Time.time + seconds;
            switch (kind)
            {
                case "repair":
                    if (car) car.Repair(0.3f);       // "Repairs 30% Health" (DriftCar.Durability drives the damage smoke)
                    GameEffects.RepairGirl(car);   // SID_PVP_REPAIR_GIRL, pvp_repair_girl_ui and the car NIFs
                    break;
                case "recharge":
                    if (car && Driver.Instance) Driver.Instance.Recharge(car, seconds);
                    AudioLibrary.PlaySfx("sfx/SID_ITEM_INSTALL");
                    break;
                default:
                    AudioLibrary.PlaySfx("sfx/SID_ITEM_INSTALL");
                    break;
            }
            ChatBox.System($"Used {ItemCatalog.Name(new BagEntry { Kind = BagKind.Stack, Id = id })}.");
            try { Used?.Invoke(kind, seconds); } catch (System.Exception ex) { Debug.LogException(ex); }
        }

        public static void Refresh() { if (Driver.Instance) Driver.Instance.Redraw(); }

        public static NuiView Tiles => Driver.Instance ? Driver.Instance.tiles : null;
        public static NuiView Covers => Driver.Instance ? Driver.Instance.covers : null;
        /// <summary>The set-up panel shown with the inventory (3800000 classic, 4740000 + 4750000 NDLG).</summary>
        public static NuiView SetupView => Driver.Instance ? Driver.Instance.setupA : null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            Canvas.willRenderCanvases -= Tick;
            Canvas.willRenderCanvases += Tick;
        }

        static void Tick()
        {
            if (!Application.isPlaying || Driver.Instance || !UiManager.Instance) return;
            var go = new GameObject("Battle Quick Slots");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<Driver>();
        }

        internal sealed class Driver : MonoBehaviour
        {
            public static Driver Instance { get; private set; }
            internal NuiView tiles, covers, setupA, setupB;
            DriftCar rechargeCar, lastCar;
            bool lastPvp;
            float rechargeUntil, rechargeBase;

            void Awake() { Instance = this; }
            void OnDestroy() { if (Instance == this) Instance = null; CloseRace(); CloseSetup(); EndRecharge(); }

            void Update()
            {
                var ui = UiManager.Instance;
                bool race = Feature && ui && P != null && ui.Find(1380000);
                if (race && !tiles) OpenRace();
                else if (!race && tiles) { CloseRace(); if (BattleItemEffects.Instance) BattleItemEffects.Instance.EndAll(); }
                // the Battle Zone race (RANK 1380000 up) is the car's +0x1a9c mode: battle top speeds and fill, no gauge loss from hits
                var playerCar = GameFlow.Instance ? GameFlow.Instance.PlayerCar : null;
                bool pvp = ui && P != null && ui.Find(1380000);
                // only on the HUD's open / close, so other code (tests, the race code) may set the mode in between
                if (playerCar && (pvp != lastPvp || playerCar != lastCar)) { if (pvp != lastPvp) playerCar.PvpMode = pvp; lastPvp = pvp; lastCar = playerCar; }
                bool setup = Feature && InventoryWindow.IsOpen;
                bool ndlg = InventoryWindow.IsNdlg;
                if (setup && (!setupA || (setupB != null) != ndlg)) { CloseSetup(); OpenSetup(ndlg); }
                else if (!setup && setupA) CloseSetup();
                if (rechargeCar && Time.time > rechargeUntil) EndRecharge();
                if (!tiles) return;
                Load();
                Keys();
                for (int i = 0; i < Slots; i++) Cover(i);
                if (Time.frameCount % 15 == 0) Redraw();
            }

            internal void Recharge(DriftCar car, float seconds)
            {
                EndRecharge();
                rechargeCar = car;
                // "the Boost Gauge will recharge 2x faster": the Battle Zone fill rate +0x1abc = 2 (setter 0x478750 takes
                // only 1 or 2); outside the Battle Zone the fill time is halved instead
                rechargeBase = car.BoostFillSeconds;
                if (car.PvpMode) car.PvpFillRate = 2f; else car.BoostFillSeconds = rechargeBase / 2f;
                rechargeUntil = Time.time + seconds;
            }

            void EndRecharge()
            {
                if (rechargeCar) { rechargeCar.PvpFillRate = 1f; rechargeCar.BoostFillSeconds = rechargeBase; }
                rechargeCar = null;
            }

            void Keys()
            {
                if (ChatBox.Typing || UiPopup.IsOpen) return;
                for (int i = 0; i < Slots; i++)
                {
#if ENABLE_INPUT_SYSTEM
                    var kb = Keyboard.current;
                    if (kb == null) return;
                    bool down = (i == 0 ? kb.digit1Key : kb.digit2Key).wasPressedThisFrame;
#else
                    bool down = Input.GetKeyDown(KeyCode.Alpha1 + i);
#endif
                    if (down) Use(i);
                }
            }

            static readonly string[] Ord = { "1ST", "2ND" };

            void OpenRace()
            {
                var ui = UiManager.Instance;
                tiles = ui.Open(3780000);
                covers = ui.Open(3790000);
                for (int i = 0; i < Slots; i++)
                {
                    int slot = i;
                    var tile = tiles ? tiles.Control("ID_PVP_ITEMQUICKSLOT_BASE_TILE_" + Ord[i]) : null;
                    if (tile)
                    {
                        var s = ItemSlot.On((RectTransform)tile.transform);
                        s.Entry = () => Entry(slot);
                        s.LeftClick = _ => Use(slot);
                    }
                    ui.On("ID_PVP_ITEMQUICKSLOT_COVER_NUM" + (i + 1), _ => Use(slot));
                    ui.On("ID_PVP_ITEMQUICKSLOT_BTN_NUM" + (i + 1), _ => Use(slot));
                }
                // COLTIME_NUM0-9 are the digit pictures; NUMn_10 / NUMn_1 show a slot's cooldown seconds with them
                if (covers) for (int d = 0; d <= 9; d++) covers.SetVisible("ID_PVP_ITEMQUICKSLOT_COLTIME_NUM" + d, false);
                Redraw();
            }

            void CloseRace()
            {
                var ui = UiManager.Instance;
                if (ui) { if (tiles) ui.Close(tiles); if (covers) ui.Close(covers); }
                tiles = covers = null;
            }

            /// <summary>Drop targets of the set-up panel: classic 3800000 (ID_PVP_ITEMQUICKSLOT_1/2), NDLG 4750000 (BATTLE_QSLOT1/2).</summary>
            string SetupSlotId(int i) => setupB ? "ID_NDLGINVEN_BATTLE_QSLOT" + (i + 1) : "ID_PVP_ITEMQUICKSLOT_" + (i + 1);
            NuiView SetupSlotView => setupB ? setupB : setupA;

            void OpenSetup(bool ndlg)
            {
                var ui = UiManager.Instance;
                if (ndlg) { setupA = ui.Open(4740000); setupB = ui.Open(4750000); }
                else { setupA = ui.Open(3800000); setupB = null; }
                if (setupB) foreach (var id in new[] { "SEL1", "SEL2", "COVER1", "COVER2" }) setupB.SetVisible("ID_NDLGINVEN_BATTLE_QSLOT_" + id, false);
                if (setupA && !setupB) foreach (var c in setupA.Controls) if (c.Data.IdName != null && c.Data.IdName.Contains("COVER")) c.gameObject.SetActive(false);
                for (int i = 0; i < Slots; i++)
                {
                    int slot = i;
                    var c = SetupSlotView ? SetupSlotView.Control(SetupSlotId(i)) : null;
                    if (!c) continue;
                    var s = ItemSlot.On((RectTransform)c.transform);
                    s.Entry = () => Entry(slot);
                    s.RightClick = _ => Clear(slot);
                    s.Accept = e => IsBattleItem(e);
                    s.Dropped = e => Assign(slot, e);
                }
                Redraw();
            }

            void CloseSetup()
            {
                var ui = UiManager.Instance;
                if (ui) { if (setupA) ui.Close(setupA); if (setupB) ui.Close(setupB); }
                setupA = setupB = null;
            }

            internal void Redraw()
            {
                Load();
                for (int i = 0; i < Slots; i++)
                {
                    var e = Entry(i);
                    var pos = tiles ? tiles.Control("ID_PVP_ITEMQUICKSLOT_BASE_ITEMPOS_" + Ord[i]) : null;
                    Icon(pos, e, 46);
                    if (covers)
                    {
                        covers.SetVisible("ID_PVP_ITEMQUICKSLOT_COVER_COUNT" + (i + 1), e != null);
                        if (e != null) covers.SetText("ID_PVP_ITEMQUICKSLOT_COVER_COUNT" + (i + 1), e.Count.ToString());
                    }
                    var set = SetupSlotView ? SetupSlotView.Control(SetupSlotId(i)) : null;
                    Icon(set, e, 51);
                    if (setupB)
                    {
                        setupB.SetVisible("ID_NDLGINVEN_BATTLE_QSLOT_COUNT" + (i + 1), e != null);
                        if (e != null) setupB.SetText("ID_NDLGINVEN_BATTLE_QSLOT_COUNT" + (i + 1), e.Count.ToString());
                    }
                }
            }

            static void Icon(NuiControlView c, BagEntry e, float size)
            {
                if (!c) return;
                foreach (var g in c.GetComponentsInChildren<RawImage>(true)) if (g.transform.parent == c.transform || g.transform == c.transform) g.enabled = e == null && c.Data.Type != NuiControlView.Static;
                OriginalMenus.Remove(c.transform, "item");
                if (e == null) return;
                var tile = OriginalMenus.ItemTile(c.transform, Vector2.zero, new Vector2(size, size), e.Icon, e.Name);
                foreach (var g in tile.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
            }

            /// <summary>Copies digit d's picture (COLTIME_NUMd) onto a number control.</summary>
            void Digit(string id, int d)
            {
                var dst = covers ? covers.Control(id) : null;
                var src = covers ? covers.Control("ID_PVP_ITEMQUICKSLOT_COLTIME_NUM" + Mathf.Clamp(d, 0, 9)) : null;
                if (!dst || !src) return;
                var a = src.GetComponentsInChildren<RawImage>(true).FirstOrDefault();
                foreach (var b in dst.GetComponentsInChildren<RawImage>(true)) if (a) { b.texture = a.texture; b.uvRect = a.uvRect; }
            }

            void Cover(int i)
            {
                float secs = CooldownLeft(i);
                int n = Mathf.CeilToInt(secs);
                if (covers)
                {
                    covers.SetVisible($"ID_PVP_ITEMQUICKSLOT_NUM{i + 1}_10", n >= 10);
                    covers.SetVisible($"ID_PVP_ITEMQUICKSLOT_NUM{i + 1}_1", n > 0);
                    if (n >= 10) Digit($"ID_PVP_ITEMQUICKSLOT_NUM{i + 1}_10", n / 10);
                    if (n > 0) Digit($"ID_PVP_ITEMQUICKSLOT_NUM{i + 1}_1", n % 10);
                }
                var c = covers ? covers.Control("ID_PVP_ITEMQUICKSLOT_COVER_" + Ord[i]) : null;
                if (!c) return;
                float left = CooldownLeft(i);
                c.gameObject.SetActive(left > 0f);
                if (left <= 0f) return;
                var e1 = c.transform.Find("e1") as RectTransform;
                if (e1 && DriftCity.Core.NuiControl.TryVec2(c.Data.I("SZ1"), out float w, out float h)) e1.sizeDelta = new Vector2(w, h * left / Cooldown);
            }
        }
    }
}
