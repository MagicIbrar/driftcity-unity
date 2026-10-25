using System.Collections.Generic;
using System.Globalization;
using DriftCity.Core;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.Game
{
    /// <summary>
    /// Stand-ins for event art this install lacks (EVENTS.md 12): some event dialogs point at textures that were never
    /// shipped with the US data (ui_thanksgiving.dds, ui_contents762.dds for THANKSGIVING 4680000, ...), so their panels and
    /// buttons would draw nothing. <see cref="Apply"/> looks at every control's normal image; when it doesn't load, a big
    /// static (a backdrop) gets a dark panel in the event's colour behind it, a button a dark bar with its name, and small
    /// decorations stay empty. Dialogs whose art exists are left alone. The list of what was replaced is kept for tests.
    /// </summary>
    public static class EventArtFallback
    {
        public const float PanelMinWidth = 160f, PanelMinHeight = 90f;
        static readonly Dictionary<int, List<string>> replaced = new Dictionary<int, List<string>>();

        /// <summary>The controls of a dialog that got a stand-in (tests).</summary>
        public static IReadOnlyList<string> Replaced(int dialog) => replaced.TryGetValue(dialog, out var l) ? l : new List<string>();

        /// <summary>The event's colour for a dialog id: harvest brown, Easter pink, Christmas green, else slate.</summary>
        public static Color Tint(int dialog)
        {
            switch (dialog)
            {
                case 4670000: case 4680000: return new Color(0.16f, 0.12f, 0.07f, 0.94f);   // AUTUMN, THANKSGIVING
                case 4660000: case 4690000: case 4700000: return new Color(0.22f, 0.10f, 0.16f, 0.94f);   // VALEN, EVENT
                case 4440000: return new Color(0.06f, 0.16f, 0.10f, 0.94f);   // XMASTREEEVENT
                default: return new Color(0.10f, 0.11f, 0.14f, 0.94f);
            }
        }

        /// <summary>True when the control's normal image (I1_1) is named but this install can't load it.</summary>
        public static bool Missing(NuiControl c)
        {
            var img = c?.I("I1_1");
            if (string.IsNullOrEmpty(img)) return false;
            try { return UiAssets.Texture(img) == null; } catch { return true; }
        }

        /// <summary>Gives a dialog's controls with missing art their stand-ins (idempotent).</summary>
        public static int Apply(NuiView v)
        {
            if (!v || v.Dialog == null) return 0;
            int id = v.Dialog.Id;
            if (!replaced.TryGetValue(id, out var list)) replaced[id] = list = new List<string>();
            int n = 0;
            foreach (var c in v.GetComponentsInChildren<NuiControlView>(true))
            {
                if (c.transform.Find("art stand-in") || !Missing(c.Data)) continue;
                // a sibling sheet with the same cell first (AEROSET's ui_contents534 -> ui_contents53 has the panel)
                if (UseAlternate(c)) { list.Add((c.Data.IdName ?? c.Data.Id.ToString()) + " (alternate sheet)"); n++; continue; }
                var rt = (RectTransform)c.transform;
                var size = rt.sizeDelta;
                bool button = c.Data.Type == NuiControlView.Button || c.Data.Type == NuiControlView.Check;
                if (!button && (size.x < PanelMinWidth || size.y < PanelMinHeight)) continue;
                var panel = NuiView.NewRect("art stand-in", c.transform);
                panel.SetAsFirstSibling();
                NuiControl.TryVec2(c.Data.I("POS1"), out float px, out float py);
                panel.anchoredPosition = new Vector2(px, -py);
                panel.sizeDelta = size;
                var img = panel.gameObject.AddComponent<Image>();
                img.raycastTarget = false;
                var tint = Tint(id);
                img.color = button ? new Color(tint.r * 1.8f, tint.g * 1.8f, tint.b * 1.8f, 0.95f) : tint;
                if (button && size.x >= 30f && size.y >= 14f && !HasText(c))
                {
                    var trt = NuiView.NewRect("label", panel);
                    trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.offsetMin = trt.offsetMax = Vector2.zero;
                    var t = trt.gameObject.AddComponent<Text>();
                    t.font = UiAssets.Font("Verdana 12", out int fs, out _);
                    t.fontSize = Mathf.Clamp(Mathf.RoundToInt(size.y * 0.45f), 9, 14);
                    t.alignment = TextAnchor.MiddleCenter; t.color = new Color(1f, 0.92f, 0.75f); t.raycastTarget = false;
                    t.text = Label(c.Data.IdName);
                }
                list.Add(c.Data.IdName ?? c.Data.Id.ToString());
                n++;
            }
            return n;
        }

        /// <summary>"x,y~x2,y2_ui_contents534.dds" -> the same cell of ui_contents533 / 532 / 53 when one of those loads.</summary>
        public static string Alternate(string reference)
        {
            if (string.IsNullOrEmpty(reference)) return null;
            int us = reference.IndexOf('_');
            if (us < 0 || !reference.EndsWith(".dds")) return null;
            string cell = reference.Substring(0, us + 1), file = reference.Substring(us + 1, reference.Length - us - 5);
            int digits = 0;
            while (digits < file.Length && char.IsDigit(file[file.Length - 1 - digits])) digits++;
            if (digits < 3) return null;   // ui_contents53 itself
            string stem = file.Substring(0, file.Length - digits), num = file.Substring(file.Length - digits);
            string sheet = num.Substring(0, 2);
            for (int k = int.Parse(num.Substring(2)) - 1; k >= 1; k--)
            {
                string alt = cell + stem + sheet + (k == 1 ? "" : k.ToString()) + ".dds";
                try { if (UiAssets.Texture(alt)) return alt; } catch { }
            }
            return null;
        }

        static bool UseAlternate(NuiControlView c)
        {
            var alt = Alternate(c.Data.I("I1_1"));
            if (alt == null) return false;
            var e1 = c.transform.Find("e1");
            var ri = e1 ? e1.GetComponent<RawImage>() : null;
            if (!ri) return false;
            ri.texture = UiAssets.Texture(alt);
            ri.color = Color.white;
            ri.enabled = true;
            return true;
        }

        static bool HasText(NuiControlView c)
        {
            foreach (var t in c.GetComponentsInChildren<Text>(true)) if (t.transform.parent && t.transform.parent.name != "label" && !string.IsNullOrEmpty(t.text)) return true;
            return false;
        }

        /// <summary>"ID_THANKSGIVING_EXCHAGE" -> "Exchage" (the last word of the id, as a stand-in label).</summary>
        public static string Label(string idName)
        {
            if (string.IsNullOrEmpty(idName)) return "";
            var parts = idName.Split('_');
            string w = parts[parts.Length - 1];
            if (w.Length <= 2 && parts.Length > 2) w = parts[parts.Length - 2] + " " + w;
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(w.ToLowerInvariant());
        }
    }
}
