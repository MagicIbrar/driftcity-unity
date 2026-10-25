using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.UI
{
    /// <summary>The game's generic popup (POPUP dialog 3250000): a text plus a chosen set of buttons.</summary>
    public static class UiPopup
    {
        public const string Ok = "ID_POPUP_OK", Yes = "ID_POPUP_YES", No = "ID_POPUP_NO", Accept = "ID_POPUP_ACCEPT", Deny = "ID_POPUP_DENY",
                            Buy = "ID_POPUP_BUY", Sell = "ID_POPUP_SELL", Cancel = "ID_POPUP_CANCEL";

        static readonly string[] AllButtons =
        {
            "ID_POPUP_SPACEBAR", Yes, No, Accept, Deny, Ok, Cancel, Sell, Buy, "ID_POPUP_DELETE", "ID_POPUP_RESELL", "ID_POPUP_EXIT",
            "ID_POPUP_OKONLY", "ID_POPUP_NAMEOFF", "ID_POPUP_WITHDRAW", "ID_POPUP_FRIENDADD_ADD", "ID_POPUP_FRIENDINTERCEPT_INTERCEPT",
        };

        // every popup answers only its own buttons (button ids are shared by all popup instances)
        static readonly Dictionary<NuiView, Action<string>> callbacks = new Dictionary<NuiView, Action<string>>();

        public static bool IsOpen
        {
            get
            {
                foreach (var v in callbacks.Keys) if (v) return true;
                return false;
            }
        }

        /// <summary>Shows text with the given buttons; the callback receives the clicked button id.</summary>
        public static NuiView Show(string text, Action<string> onClick, params string[] buttons)
        {
            var ui = UiManager.Instance;
            if (!ui) return null;
            var v = ui.Open(3250000);
            if (!v) return null;
            Prune();
            if (buttons == null || buttons.Length == 0) buttons = new[] { "ID_POPUP_OKONLY" };
            var wanted = new HashSet<string>(buttons);
            callbacks[v] = onClick ?? (_ => { });
            foreach (var b in AllButtons)
            {
                v.SetVisible(b, wanted.Contains(b));
                if (!wanted.Contains(b)) continue;
                string id = b;
                ui.On(b, view =>
                {
                    if (!view || !callbacks.TryGetValue(view, out var cb)) return;
                    callbacks.Remove(view);
                    ui.Close(view);
                    cb(id);
                });
            }
            v.SetText("ID_POPUP_TEXT", text);
            // modal: a dim full-screen blocker behind the popup keeps clicks off whatever is underneath
            var blocker = NuiView.NewRect("Modal Blocker", v.transform);
            blocker.anchorMin = Vector2.zero; blocker.anchorMax = Vector2.one;
            blocker.offsetMin = new Vector2(-4000, -4000); blocker.offsetMax = new Vector2(4000, 4000);
            blocker.SetAsFirstSibling();
            blocker.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, 0.25f);
            v.transform.SetAsLastSibling();
            return v;
        }

        public static NuiView Message(string text, Action done = null) => Show(text, _ => done?.Invoke(), "ID_POPUP_OKONLY");

        /// <summary>Keeps open popups above windows that were opened after them.</summary>
        public static void BringToFront()
        {
            foreach (var v in callbacks.Keys) if (v) v.transform.SetAsLastSibling();
        }

        static void Prune()
        {
            var dead = new List<NuiView>();
            foreach (var v in callbacks.Keys) if (!v) dead.Add(v);
            foreach (var v in dead) callbacks.Remove(v);
        }
    }
}
