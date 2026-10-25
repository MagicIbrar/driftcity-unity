using System;
using System.Collections.Generic;
using DriftCity.Net;
using DriftCity.Social;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.Game
{
    /// <summary>
    /// The licence info pages (code map "Parts shop, manufacturing &amp; licences": LICENSE 710000 and OTHERLISENCEBASE
    /// 3070000; DriftCity_RE/INVENTORY.md "Licence pages").
    /// - LICENSE 710000, the driver's licence card [exact]: binder 0x62ed90 (CHAR00-07 = 0xad572.., ID 0xad57a, LEVEL 0xad57b,
    ///   ISSUEDATE 0xad57c), show 0x62ee30: the eight portraits hidden but the driver's (+0x36), ID = the name (+8), LEVEL =
    ///   text 10390 ("Beginner"), ISSUEDATE = "%d-%d-%d" of today's date (GetLocalTime: year-month-day). Opened by a server
    ///   reply (0x55aa60) and a mission step (0x70d270).
    /// - OTHERLISENCEBASE 3070000 [exact]: the frame around another driver's licence page (the classic page 3080000 kept by
    ///   both inventory sets); CLOSE (3070005) plays 0x4e8b and hides it (0x5c8b80). The remake opens it from the player
    ///   menu's "licence" entry: the page shows that driver's mounted licence (LISENCEINFO box on hover) with their card.
    /// Data: online driver.info (src/driverinfo.js), offline an AI driver's card (DriverInfoScreen.AiCard).
    /// </summary>
    public static class LicencePages
    {
        public const int CardDialog = 710000, OtherBase = 3070000, LicencePage = 3080000;
        static NuiView card, frame, page;
        static DriverInfoScreen.Card shown;

        public static NuiView Card => card;
        public static NuiView Frame => frame;
        public static NuiView Page => page;
        public static DriverInfoScreen.Card Shown => shown;

        /// <summary>The licence card of a driver (the own one when null).</summary>
        public static NuiView ShowCard(DriverInfoScreen.Card c = null)
        {
            c ??= DriverInfoScreen.MyCard();
            if (c == null || !UiManager.Instance) return null;
            if (!card) card = UiManager.Instance.Open(CardDialog);
            if (!card) return null;
            for (int i = 0; i < 8; i++) card.SetVisible($"ID_LICENSE_CHAR{i:00}", i == Mathf.Clamp(c.Avatar, 1, 8) - 1);
            card.SetText("ID_LICENSE_ID", c.Name);
            card.SetText("ID_LICENSE_LEVEL", ItemCatalog.Text("10390", "Beginner"));
            var now = DateTime.Now;
            card.SetText("ID_LICENSE_ISSUEDATE", $"{now.Year}-{now.Month}-{now.Day}");
            card.transform.SetAsLastSibling();
            return card;
        }

        public static void CloseCard() { if (card && UiManager.Instance) UiManager.Instance.Close(card); card = null; }

        /// <summary>Another driver's licences (the player menu): asks the server, or makes an AI card offline.</summary>
        public static void ShowOther(string name, Action<bool> done = null)
        {
            if (string.IsNullOrWhiteSpace(name)) { done?.Invoke(false); return; }
            var p = PlayerProfile.Current;
            if (p != null && string.Equals(name.Trim(), p.Name, StringComparison.OrdinalIgnoreCase)) { ShowOther(DriverInfoScreen.MyCard()); done?.Invoke(true); return; }
            if (SocialNet.Online)
            {
                SocialNet.Request("driver.info", new Dictionary<string, object> { ["name"] = name.Trim() }, r =>
                {
                    if (r == null || !r.B("ok")) { UiPopup.Message(SocialNet.Err(r)); done?.Invoke(false); return; }
                    ShowOther(DriverInfoScreen.FromServer(r));
                    done?.Invoke(true);
                });
                return;
            }
            var ai = SocialStore.AiName(name);
            if (ai == null) { UiPopup.Message($"{name.Trim()} does not exist."); done?.Invoke(false); return; }
            var c = DriverInfoScreen.AiCard(ai);
            // AI drivers mount a licence their level could have earned
            var all = Licences.All;
            if (all.Count > 0) c.Licence = all[(c.Level * 7 + c.Name.Length) % Mathf.Min(all.Count, 12)].Id;
            ShowOther(c);
            done?.Invoke(true);
        }

        /// <summary>OTHERLISENCEBASE + the licence page with the driver's mounted licence, and the driver's card.</summary>
        public static NuiView ShowOther(DriverInfoScreen.Card c)
        {
            if (c == null || !UiManager.Instance) return null;
            CloseOther();
            var ui = UiManager.Instance;
            shown = c;
            frame = ui.Open(OtherBase);
            page = ui.Open(LicencePage);
            if (page) OriginalMenus.Hide(page, "ID_MAINBUTTON_HOTKEY_SKILLBOOK", "ID_INVEN_LICENSE_HOVER1ST", "ID_INVEN_LICENSE_HOVER2ND", "ID_INVEN_LICENSE_HOVER3RD", "ID_INVEN_LICENSE_HOVER4TH");
            ui.On("ID_OTHERLISENCEBASE_CLOSE", _ => { UiSounds.Cancel(); CloseOther(); });
            var l = c.Licence >= 0 ? Licences.Get(c.Licence) : null;
            if (page)
            {
                var pos = page.Control("ID_INVEN_LICENSE_POSITEM");
                if (pos)
                {
                    foreach (var ri in pos.GetComponentsInChildren<RawImage>(true)) if (ri.transform.parent == pos.transform) ri.enabled = l == null;
                    OriginalMenus.Remove(pos.transform, "item");
                    if (l != null)
                    {
                        var tile = OriginalMenus.ItemTile(pos.transform, Vector2.zero, new Vector2(64, 64), l.IdName, l.Name);
                        foreach (var g in tile.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
                        LicenceInfo.AttachTooltip((RectTransform)pos.transform, l);
                    }
                }
                page.SetText("ID_INVEN_LICENSE_TITLE", l != null ? l.Name : $"{c.Name} has no licence mounted.");
                page.SetText("ID_INVEN_LICENSE_DESC", l != null ? ItemCatalog.Clean(l.Info) : "");
                page.SetText("ID_INVEN_LICENSE_EFFECT", l != null ? "- " + ItemCatalog.Clean(l.EffectText) : "");
            }
            ShowCard(c);
            if (frame) frame.transform.SetAsLastSibling();
            if (page) page.transform.SetAsLastSibling();
            if (card) card.transform.SetAsLastSibling();
            frame?.gameObject.AddComponent<EscCloses>();
            return frame;
        }

        public static void CloseOther()
        {
            var ui = UiManager.Instance;
            if (ui) { if (frame) ui.Close(frame); if (page) ui.Close(page); }
            frame = page = null;
            CloseCard();
            shown = null;
            ItemTooltip.Hide();
        }

        sealed class EscCloses : MonoBehaviour
        {
            void Update()
            {
#if ENABLE_INPUT_SYSTEM
                bool esc = UnityEngine.InputSystem.Keyboard.current != null && UnityEngine.InputSystem.Keyboard.current.escapeKey.wasPressedThisFrame;
#else
                bool esc = Input.GetKeyDown(KeyCode.Escape);
#endif
                if (esc && !UiPopup.IsOpen && !ChatBox.Typing) CloseOther();
            }
        }
    }
}
