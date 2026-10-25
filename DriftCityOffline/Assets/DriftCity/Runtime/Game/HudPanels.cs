using System.Collections.Generic;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// NOTEBOOK 4060000 (DriftCity_RE/AUDIO.md 8; the basic main bar's NOTEBOOK button 3020017 toggles it, 0x5e9006: open
    /// SID_SYSWIN_CREATE 0x4e8a, close SID_SYSWIN_DESTROY 0x4e8b). Handler 0x5e9390 (each button hides the notebook
    /// afterwards, 0x7b22d0): COUPONBOOK 4060005 -> the coupon book (0x5e84a0), MISSIONBOOK 4060006 -> the quest list
    /// (0x5e8640), LICENSE 4060007 -> the licences (0x5e89d0, gated by the licence content bit), 4060008 -> the skill book
    /// (0x5e8ad0, no control in the US layout), CLOSE 4060004. The buttons carry the bar's hotkeys (J = 116 F5, 118 F7, 120 F9).
    /// </summary>
    public static class Notebook
    {
        public const int Dialog = 4060000;
        static NuiView view;
        public static bool IsOpen => view;

        public static void Toggle() { if (IsOpen) Close(); else Open(); }

        public static void Open()
        {
            var ui = UiManager.Instance;
            if (!ui || view) return;
            view = ui.Open(Dialog);
            if (!view) return;
            AudioLibrary.PlaySfx("sfx/SID_SYSWIN_CREATE");
            ui.On("ID_NOTEBOOK_COUPONBOOK", _ => { Close(false); CouponBook.Open(); });
            ui.On("ID_NOTEBOOK_MISSIONBOOK", _ => { Close(false); Menus.Quests(); });
            ui.On("ID_NOTEBOOK_LICENSE", _ => { Close(false); WorkshopMenus.Licences(); });
            ui.On("ID_NOTEBOOK_CLOSE", _ => Close());
        }

        public static void Close() => Close(true);

        static void Close(bool sound)
        {
            if (!view) return;
            if (sound) AudioLibrary.PlaySfx("sfx/SID_SYSWIN_DESTROY");
            UiManager.Instance?.Close(view);
            view = null;
        }
    }

    /// <summary>
    /// ITEMSHOP 4070000 (the basic main bar's SHOP button 3020016 toggles it, 0x5e8fa9; handler 0x5e9470): VSHOP 4070005 ->
    /// the custom shop (0x5e81e0, the same call as the ADV bar's SHOP and the bar's F4), CAPSULEBOX 4070006 -> the gacha
    /// capsule shop (0x5e86d0, F8), CLOSE 4070004; each hides the panel.
    /// </summary>
    public static class ItemShop
    {
        public const int Dialog = 4070000;
        static NuiView view;
        public static bool IsOpen => view;

        public static void Toggle() { if (IsOpen) Close(); else Open(); }

        public static void Open()
        {
            var ui = UiManager.Instance;
            if (!ui || view) return;
            view = ui.Open(Dialog);
            if (!view) return;
            AudioLibrary.PlaySfx("sfx/SID_SYSWIN_CREATE");
            ui.On("ID_ITEMSHOP_VSHOP", _ => { Close(false); VShop(); });
            ui.On("ID_ITEMSHOP_CAPSULEBOX", _ => { Close(false); Gacha.Open(); });
            ui.On("ID_ITEMSHOP_CLOSE", _ => Close());
        }

        /// <summary>0x5e81e0: the custom shop (in a city; the parking lot has no custom shop in the remake, so the shop chooser).</summary>
        public static void VShop()
        {
            var flow = GameFlow.Instance;
            if (flow && flow.InCity) CustomShop.Open(); else Menus.Shop();
        }

        public static void Close() => Close(true);

        static void Close(bool sound)
        {
            if (!view) return;
            if (sound) AudioLibrary.PlaySfx("sfx/SID_SYSWIN_DESTROY");
            UiManager.Instance?.Close(view);
            view = null;
        }
    }

    /// <summary>
    /// SPEEDITEM 340000 (basic speedometer) / 350000 (ADV): the "item obtained while driving" ticker beside the speedometer
    /// (vtable 0x87283c, init 0x6292a0 hidden, Show 0x629170, update 0x6299d0). Add 0x62a1b0 (only with the content bit
    /// net+0x28c &amp; 4) takes the item's icon; called by the drop pick-up 0x4fb280, the event obtain 0x6f3df0, the item
    /// popup 0x6f4c30 and the quest rewards 0x6f6ba0. At most six entries (a seventh drops the oldest); an entry pops in over
    /// 0.2 s, stays 3 s and fades out over 1 s (constants of 0x6299d0). The remake lays the entries into the dialog's six
    /// cases ITEM01..06 (newest in 01) instead of the client's arc animation.
    /// </summary>
    public sealed class SpeedItems : MonoBehaviour
    {
        public const int Dialog = 340000, AdvDialog = 350000, Max = 6;
        public const float PopIn = 0.2f, Stay = 3f, FadeOut = 1f;

        sealed class Entry { public string Icon; public float Born; }

        static SpeedItems instance;
        readonly List<Entry> entries = new List<Entry>();
        NuiView view;

        /// <summary>Content bit 4 of net+0x28c: on in the remake.</summary>
        public static bool Enabled = true;
        public static int Count => instance ? instance.entries.Count : 0;
        public static IReadOnlyList<string> Icons
        {
            get
            {
                var l = new List<string>();
                if (instance) foreach (var e in instance.entries) l.Add(e.Icon);
                return l;
            }
        }
        public static NuiView View => instance ? instance.view : null;

        /// <summary>0x62a1b0: an item obtained (its icon name, e.g. "i_n_00067").</summary>
        public static void Add(string icon)
        {
            if (!Enabled || string.IsNullOrEmpty(icon) || !UiManager.Instance) return;
            if (!instance)
            {
                var go = new GameObject("Speed Items");
                DontDestroyOnLoad(go);
                instance = go.AddComponent<SpeedItems>();
            }
            instance.Push(icon);
        }

        public static void Clear()
        {
            if (!instance) return;
            instance.entries.Clear();
            instance.Hide();
        }

        void Push(string icon)
        {
            entries.Insert(0, new Entry { Icon = icon, Born = Time.unscaledTime });
            if (entries.Count > Max) entries.RemoveAt(entries.Count - 1);
            if (!view) view = UiManager.Instance.Open(GameOptions.MainUIType == 0 ? Dialog : AdvDialog);
            if (!view) view = UiManager.Instance.Open(Dialog);
            Layout();
        }

        string P => view && view.Dialog != null && view.Dialog.Id == AdvDialog ? "ID_SPEEDITEM_ADV_" : "ID_SPEEDITEM_";

        void Layout()
        {
            if (!view) return;
            view.SetVisible(P + "ORIGIN", false);
            for (int i = 0; i < Max; i++)
            {
                string n = (i + 1).ToString("00");
                bool on = i < entries.Count;
                view.SetVisible(P + "BGITEM" + n, on);
                if (on) EventUi.Icon(view, P + "ITEM" + n, entries[i].Icon, true);
                else view.SetVisible(P + "ITEM" + n, false);
            }
        }

        void Update()
        {
            if (!view) return;
            float now = Time.unscaledTime;
            bool removed = entries.RemoveAll(e => now - e.Born >= Stay + FadeOut) > 0;
            if (removed) Layout();
            if (entries.Count == 0) { Hide(); return; }
            for (int i = 0; i < entries.Count; i++)
            {
                float age = now - entries[i].Born;
                float a = age < PopIn ? age / PopIn : age > Stay ? 1f - (age - Stay) / FadeOut : 1f;
                string n = (i + 1).ToString("00");
                SetAlpha(view.Control(P + "ITEM" + n), a);
                SetAlpha(view.Control(P + "BGITEM" + n), a);
            }
        }

        static void SetAlpha(NuiControlView c, float a)
        {
            if (!c) return;
            var g = c.GetComponent<CanvasGroup>();
            if (!g) g = c.gameObject.AddComponent<CanvasGroup>();
            g.alpha = Mathf.Clamp01(a);
            g.blocksRaycasts = false;
        }

        void Hide()
        {
            if (view) UiManager.Instance?.Close(view);
            view = null;
        }

        void OnDestroy() { if (instance == this) instance = null; }
    }
}
