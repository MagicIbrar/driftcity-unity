using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// Drives the original speedometer (320000) and the main-button bar from the player car. The bar follows goption.ini's
    /// MainUIType: 1 (the remaster's default) the ADV bar (MAINBUTTON 3010000: Esc and F1-F9 buttons over a full-width EXP
    /// bar), 0 the basic bar (3020000: SHOP -> ITEMSHOP 4070000, NOTEBOOK -> NOTEBOOK 4060000, F-key buttons, car audio);
    /// its buttons answer their NATION J keys (F1 = 112 ... F9 = 120, K = 75). HudPanels.cs: Notebook, ItemShop, SpeedItems.
    /// </summary>
    public sealed class Hud : MonoBehaviour
    {
        public DriftCar Car;
        NuiView speedo, mainBar;
        RectTransform needle;
        ArcFillImage booster;

        // measured on the original dial art: hub, RPM scale (0 at 196 deg, 10k at -44 deg) and the booster arc
        const float RpmZeroDeg = 196f, RpmFullDeg = -44f;
        static readonly Vector2 BoosterHubPx = new Vector2(117.5f, 129.5f);
        const float BoosterStartDeg = 205f, BoosterEndDeg = 23f;

        public void Show()
        {
            var ui = UiManager.Instance;
            speedo = ui.Open(320000);   // SPEEDMETER (basic)
            barType = GameOptions.MainUIType;
            mainBar = ui.Open(barType == 0 ? BasicBar : AdvBar);   // MAINBUTTON: basic (MainUIType 0) or ADV
            // the needle control's origin is the dial hub (its image is offset by POS1), so it rotates in place
            var arrow = speedo ? speedo.Control("ID_SPEEDMETER_RPMARROW") : null;
            if (arrow) needle = (RectTransform)arrow.transform;
            // GAUGE_2 is the lit booster arc drawn over the dial; it is revealed along the arc as the gauge fills
            var gauge = speedo ? speedo.Control("ID_SPEEDMETER_GAUGE_2") : null;
            var lit = gauge ? gauge.GetComponentInChildren<UnityEngine.UI.RawImage>() : null;
            if (lit)
            {
                booster = NuiView.NewRect("Booster Fill", lit.transform.parent).gameObject.AddComponent<ArcFillImage>();
                var rt = booster.rectTransform;
                rt.anchoredPosition = lit.rectTransform.anchoredPosition;
                rt.sizeDelta = lit.rectTransform.sizeDelta;
                booster.Texture = lit.texture;
                booster.UvRect = lit.uvRect;
                booster.color = lit.color;
                booster.raycastTarget = false;
                booster.CenterPx = BoosterHubPx;
                booster.StartDeg = BoosterStartDeg;
                booster.EndDeg = BoosterEndDeg;
                lit.gameObject.SetActive(false);
            }
            if (speedo)
                foreach (var id in new[] { "ID_SPEEDMETER_BETALABEL" })
                    speedo.SetVisible(id, false);
            SetupBar(ui);
            UpdateProfile();
            CarAudio.Show();
        }

        public const int AdvBar = 3010000, BasicBar = 3020000;
        int barType;
        /// <summary>"ID_MAINBUTTON_ADV_" on the ADV bar, "ID_MAINBUTTON_" on the basic one.</summary>
        string Bar => barType == 0 ? "ID_MAINBUTTON_" : "ID_MAINBUTTON_ADV_";
        public NuiView MainBar => mainBar;

        /// <summary>
        /// The main bar's labels and buttons. ADV (3010000, handler 0x5f11a1): MYINFO, INVENTORY, SHOP (the custom shop,
        /// 0x5ee1d0; the remake keeps its shop chooser there), COUPON, LADDER, QUEST, GOTCHA, LICENSE, COMM. Basic (3020000,
        /// handler 0x5e8e0b..0x5e92ef): MYINFO, INVENTORY, SHOP -> ITEMSHOP 4070000, NOTEBOOK -> NOTEBOOK 4060000, the F-key
        /// buttons HOTKEY_COMM / MYINFO / INVENTORY / SHOP (the custom shop) / COUPON / LADDER / QUEST / GACHA / LICENCE /
        /// SKILLBOOK (3020001..3020010) and CARAUDIO_OPEN (DriftCity_RE/AUDIO.md 8).
        /// </summary>
        void SetupBar(UiManager ui)
        {
            if (!mainBar) return;
            // the bar's *TEXT labels are hover tooltips in the original
            foreach (var c in mainBar.Controls)
                if (c.Data.IdName.EndsWith("TEXT") || c.Data.IdName.EndsWith("_TEXT") || c.Data.IdName.Contains("NOTE_NOTICE") || c.Data.IdName.Contains("NOTIFY"))
                    c.gameObject.SetActive(false);
            // level and EXP rate sit on the bar; the exact EXP figure is the hover box (EXPNUM) the client opens on demand
            foreach (var id in new[] { "EXPBASE", "LEVNUM", "EXPRATE" }) mainBar.SetVisible(Bar + id, true);
            foreach (var id in new[] { "EXPNUM", "EXPNUMBG" }) mainBar.SetVisible(Bar + id, false);
            var exp = mainBar.Control(Bar + "EXP");
            expFill = exp ? exp.GetComponentInChildren<UnityEngine.UI.RawImage>() : null;
            if (expFill) { expUv = expFill.uvRect; expWidth = expFill.rectTransform.sizeDelta.x; }
            // ADV bar
            ui.On("ID_MAINBUTTON_ADV_MYINFO", _ => Menus.MyInfo());
            ui.On("ID_MAINBUTTON_ADV_INVENTORY", _ => Menus.Inventory());
            ui.On("ID_MAINBUTTON_ADV_SHOP", _ => Menus.Shop());
            ui.On("ID_MAINBUTTON_ADV_QUEST", _ => Menus.Quests());
            ui.On("ID_MAINBUTTON_ADV_LICENSE", _ => WorkshopMenus.Licences());
            ui.On("ID_MAINBUTTON_ADV_SKILLBOOK", _ => WorkshopMenus.Manufacturing());
            ui.On("ID_MAINBUTTON_ADV_LADDER", _ => Ladder.Open());
            ui.On("ID_MAINBUTTON_ADV_COMM", _ => DriftCity.Social.Community.Toggle());
            ui.On("ID_MAINBUTTON_ADV_COUPON", _ => CouponBook.Open());
            ui.On("ID_MAINBUTTON_ADV_GOTCHA", _ => Gacha.Open());
            // basic bar
            ui.On("ID_MAINBUTTON_MYINFO", _ => Menus.MyInfo());
            ui.On("ID_MAINBUTTON_INVENTORY", _ => Menus.Inventory());
            ui.On("ID_MAINBUTTON_SHOP", _ => ItemShop.Toggle());
            ui.On("ID_MAINBUTTON_NOTEBOOK", _ => Notebook.Toggle());
            ui.On("ID_MAINBUTTON_HOTKEY_COMM", _ => DriftCity.Social.Community.Toggle());
            ui.On("ID_MAINBUTTON_HOTKEY_MYINFO", _ => Menus.MyInfo());
            ui.On("ID_MAINBUTTON_HOTKEY_INVENTORY", _ => Menus.Inventory());
            ui.On("ID_MAINBUTTON_HOTKEY_SHOP", _ => ItemShop.VShop());
            ui.On("ID_MAINBUTTON_HOTKEY_COUPON", _ => CouponBook.Open());
            ui.On("ID_MAINBUTTON_HOTKEY_LADDER", _ => Ladder.Open());
            ui.On("ID_MAINBUTTON_HOTKEY_QUEST", _ => Menus.Quests());
            ui.On("ID_MAINBUTTON_HOTKEY_GACHA", _ => Gacha.Open());
            ui.On("ID_MAINBUTTON_HOTKEY_LICENCE", _ => WorkshopMenus.Licences());
            ui.On("ID_MAINBUTTON_HOTKEY_SKILLBOOK", _ => WorkshopMenus.Manufacturing());
            ui.On("ID_MAINBUTTON_CARAUDIO_OPEN", _ => CarAudio.Toggle());
            // the old bar's friend / note / option buttons (the Battle Zone round's windows); the US game.nui's 3020000 names no
            // such controls, so these answer only a layout that has them
            ui.On("ID_MAINBUTTON_FRIEND", _ => DriftCity.Social.BasicBarSocial.OpenFriends());
            ui.On("ID_MAINBUTTON_MESSAGE", _ => DriftCity.Social.BasicBarSocial.OpenInbox());
            ui.On("ID_MAINBUTTON_OPTION", _ => OriginalMenus.Options());
        }

        /// <summary>The option page switched MainUIType: the other bar replaces this one.</summary>
        void SwapBarIfChanged()
        {
            if (!mainBar || GameOptions.MainUIType == barType) return;
            var ui = UiManager.Instance;
            if (!ui) return;
            ui.Close(mainBar);
            Notebook.Close(); ItemShop.Close();
            barType = GameOptions.MainUIType;
            mainBar = ui.Open(barType == 0 ? BasicBar : AdvBar);
            SetupBar(ui);
            UpdateProfile();
        }

        public void Hide()
        {
            UiManager.Instance?.Close(speedo);
            UiManager.Instance?.Close(mainBar);
            Notebook.Close(); ItemShop.Close(); SpeedItems.Clear();
            CarAudio.Hide();
        }

        public void UpdateProfile()
        {
            var p = PlayerProfile.Current;
            if (!mainBar || p == null) return;
            float rate = p.ExpToNext > 0 ? Mathf.Clamp01((float)p.Exp / p.ExpToNext) : 0f;
            mainBar.SetText(Bar + "LEVNUM", "Lv " + p.Level);
            mainBar.SetText(Bar + "EXPNUM", $"{p.Exp} / {p.ExpToNext}");
            mainBar.SetText(Bar + "EXPRATE", $"{100f * rate:F1}%");
            if (expFill)
            {
                // the orange bar grows from the left with the EXP gathered toward the next level
                var rt = expFill.rectTransform;
                rt.sizeDelta = new Vector2(expWidth * rate, rt.sizeDelta.y);
                expFill.uvRect = new Rect(expUv.x, expUv.y, expUv.width * rate, expUv.height);
                expFill.enabled = rate > 0f;
            }
        }

        UnityEngine.UI.RawImage expFill;
        Rect expUv;
        float expWidth;
        float nextProfile;

        /// <summary>The bar's function keys (NATION J: 112 F1 ... 123 F12, 75 K; 3xx codes are the F-key + 200).</summary>
        void BarHotkeys()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null || !mainBar) return;
            if (Net.ChatBox.Typing || (GameFlow.Instance && GameFlow.Instance.MenuOpen)) return;
            UnityEngine.InputSystem.Controls.KeyControl[] fkeys = { kb.f1Key, kb.f2Key, kb.f3Key, kb.f4Key, kb.f5Key, kb.f6Key, kb.f7Key, kb.f8Key, kb.f9Key, kb.f10Key, kb.f11Key, kb.f12Key };
            int vk = 0;
            for (int i = 0; i < fkeys.Length; i++) if (fkeys[i].wasPressedThisFrame) vk = 112 + i;
            if (vk == 0) return;
            foreach (var c in mainBar.Controls)
            {
                int j = Core.NuiControl.Int(c.Data.Nation, "J", 0);
                if (j >= 300) j -= 200;
                if (j == vk && c.isActiveAndEnabled) { UiSounds.Click(); mainBar.Click(c.Data.IdName); return; }
            }
#endif
        }

        void Update()
        {
            BarHotkeys();
            if (Time.unscaledTime >= nextProfile) { nextProfile = Time.unscaledTime + 0.5f; SwapBarIfChanged(); UpdateProfile(); }
            if (!speedo || !Car) return;
            int kmh = Mathf.Clamp(Mathf.RoundToInt(Car.SpeedKmh), 0, 999);
            SetDigit("ID_SPEEDMETER_NUM_100_", kmh / 100, kmh >= 100);
            SetDigit("ID_SPEEDMETER_NUM_10_", kmh / 10 % 10, kmh >= 10);
            SetDigit("ID_SPEEDMETER_NUM_1_", kmh % 10, true);
            // the car model's own automatic 5-speed box (-1 = reverse) and engine RPM
            bool reverse = Car.Gear < 0;
            int gear = Mathf.Clamp(Car.Gear, 1, 5);
            speedo.SetVisible("ID_SPEEDMETER_GEAR_R", reverse);
            for (int g = 1; g <= 6; g++) speedo.SetVisible("ID_SPEEDMETER_GEAR_" + g, !reverse && g == gear);
            speedo.SetVisible("ID_SPEEDMETER_GEAR_AT", true);
            speedo.SetVisible("ID_SPEEDMETER_GEAR_MT", false);
            if (needle)
            {
                // the dial reads 0..10 x1000 rpm; at a standstill the needle rests at idle
                float rpm01 = Mathf.Clamp01(Mathf.Max(Car.Rpm, 800f) / 10000f);
                smoothRpm = Mathf.Lerp(smoothRpm, rpm01, 1f - Mathf.Exp(-10f * Time.deltaTime));
                float deg = Mathf.Lerp(RpmZeroDeg, RpmFullDeg, smoothRpm);
                needle.localEulerAngles = new Vector3(0, 0, deg - 90f);   // the needle art points up
            }
            if (booster)
            {
                booster.SetFill(Car.BoostGauge);
                speedo.SetVisible("ID_SPEEDMETER_MITRONWARNING", MittronFuel.Low);   // the fuel tank at 20 % or less
                speedo.SetVisible("ID_SPEEDMETER_MITRONWARNING_L", MittronFuel.Low);
                // pulse while boosting or when full, like the original "booster full" effect
                float a = Car.Boosting ? 0.75f + 0.25f * Mathf.Sin(Time.time * 18f) : Car.BoostGauge >= 0.999f ? 0.8f + 0.2f * Mathf.Sin(Time.time * 4f) : 1f;
                var col = booster.color; col.a = a; booster.color = col;
            }
        }

        float smoothRpm;

        void SetDigit(string prefix, int digit, bool visible)
        {
            for (int d = 0; d < 10; d++) speedo.SetVisible(prefix + d, visible && d == digit);
        }
    }
}
