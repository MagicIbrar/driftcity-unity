using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.UI
{
    /// <summary>Outlined status text on the main canvas (mission timers, race standings).</summary>
    public static class HudText
    {
        public static Text Create(string name, Vector2 anchor, Vector2 offset, Vector2 size, int fontSize, TextAnchor align)
        {
            var ui = UiManager.Instance;
            var rt = NuiView.NewRect(name, ui.Root);
            rt.anchorMin = rt.anchorMax = anchor;
            rt.pivot = anchor;
            rt.anchoredPosition = offset;
            rt.sizeDelta = size;
            var t = rt.gameObject.AddComponent<Text>();
            t.font = UiAssets.Font("Verdana " + fontSize, out _, out _);
            t.fontSize = fontSize;
            t.fontStyle = FontStyle.Bold;
            t.alignment = align;
            t.raycastTarget = false;
            t.color = new Color(1f, 0.85f, 0.2f);
            var o = rt.gameObject.AddComponent<Outline>();
            o.effectColor = Color.black;
            o.effectDistance = new Vector2(2, -2);
            return t;
        }
    }
}
