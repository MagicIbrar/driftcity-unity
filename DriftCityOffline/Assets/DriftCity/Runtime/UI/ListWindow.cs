using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace DriftCity.UI
{
    /// <summary>
    /// A simple scrolling list window (title, rows with an optional action button, footer) for menus
    /// whose original dialogs depend on server-driven slot data. Esc closes it.
    /// </summary>
    public sealed class ListWindow : MonoBehaviour
    {
        public struct Row
        {
            public string Label, Detail, Action;
            public Action OnAction;
            public bool Enabled;
            public Color? Swatch;
            public Row(string label, string detail = null, string action = null, Action onAction = null, bool enabled = true, Color? swatch = null)
            { Label = label; Detail = detail; Action = action; OnAction = onAction; Enabled = enabled; Swatch = swatch; }
        }

        static ListWindow open;
        Text footer;
        Action onClose;

        static readonly Color Panel = new Color(0.05f, 0.07f, 0.1f, 0.92f), RowA = new Color(1, 1, 1, 0.06f), RowB = new Color(1, 1, 1, 0.02f),
                              Accent = new Color(1f, 0.72f, 0.12f), ButtonCol = new Color(0.95f, 0.62f, 0.08f), Disabled = new Color(0.35f, 0.35f, 0.35f);

        public static bool IsOpen => open;

        /// <summary>Frame on which a list window last closed (so the same Esc press doesn't also open the quit menu).</summary>
        public static int ClosedFrame = -1;
        bool closable = true;

        public static ListWindow Show(string title, IList<Row> rows, string footerText = null, Action closed = null, float width = 620, float height = 520, bool closable = true)
        {
            CloseOpen();
            var ui = UiManager.Instance;
            if (!ui) return null;
            var root = NuiView.NewRect("List: " + title, ui.Root);
            root.anchorMin = root.anchorMax = new Vector2(0.5f, 0.5f);
            root.pivot = new Vector2(0.5f, 0.5f);
            root.sizeDelta = new Vector2(width, height);
            root.anchoredPosition = Vector2.zero;
            var w = root.gameObject.AddComponent<ListWindow>();
            w.onClose = closed;
            w.closable = closable;
            root.gameObject.AddComponent<Image>().color = Panel;
            var outline = root.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(Accent.r, Accent.g, Accent.b, 0.6f);
            outline.effectDistance = new Vector2(2, -2);

            var titleText = MakeText(root, title, 22, FontStyle.Bold, Accent, TextAnchor.MiddleLeft);
            Place(titleText.rectTransform, 18, 10, width - 80, 34);
            if (closable)
            {
                var close = MakeButton(root, "X", () => w.Close(), new Color(0.6f, 0.15f, 0.12f));
                Place(close, width - 50, 10, 34, 30);
            }

            // scroll area
            var viewport = NuiView.NewRect("Viewport", root);
            Place(viewport, 12, 52, width - 24, height - 100);
            viewport.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, 0.25f);
            viewport.gameObject.AddComponent<RectMask2D>();
            var content = NuiView.NewRect("Content", viewport);
            content.anchorMin = new Vector2(0, 1); content.anchorMax = new Vector2(1, 1);
            content.pivot = new Vector2(0.5f, 1);
            var scroll = viewport.gameObject.AddComponent<ScrollRect>();
            scroll.content = content; scroll.horizontal = false; scroll.viewport = viewport;
            scroll.scrollSensitivity = 30f; scroll.movementType = ScrollRect.MovementType.Clamped;

            const float rowH = 46f;
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                var row = NuiView.NewRect("Row " + i, content);
                row.anchorMin = new Vector2(0, 1); row.anchorMax = new Vector2(1, 1);
                row.pivot = new Vector2(0.5f, 1);
                row.offsetMin = new Vector2(0, 0); row.offsetMax = new Vector2(0, 0);
                row.anchoredPosition = new Vector2(0, -i * rowH);
                row.sizeDelta = new Vector2(0, rowH);
                row.gameObject.AddComponent<Image>().color = i % 2 == 0 ? RowA : RowB;
                float x = 10;
                if (r.Swatch.HasValue)
                {
                    var sw = NuiView.NewRect("Swatch", row);
                    Place(sw, 8, 9, 28, 28);
                    sw.gameObject.AddComponent<Image>().color = r.Swatch.Value;
                    x = 44;
                }
                var label = MakeText(row, r.Label, 16, FontStyle.Bold, r.Enabled ? Color.white : new Color(0.6f, 0.6f, 0.6f), TextAnchor.UpperLeft);
                Place(label.rectTransform, x, 4, width - 210 - x, 22);
                if (!string.IsNullOrEmpty(r.Detail))
                {
                    var detail = MakeText(row, r.Detail, 12, FontStyle.Normal, new Color(0.75f, 0.8f, 0.85f), TextAnchor.UpperLeft);
                    Place(detail.rectTransform, x, 24, width - 210 - x, 20);
                }
                if (!string.IsNullOrEmpty(r.Action))
                {
                    var cb = r.OnAction;
                    var btn = MakeButton(row, r.Action, () => { UiSounds.Click(); cb?.Invoke(); }, r.Enabled ? ButtonCol : Disabled);
                    Place(btn, width - 170, 8, 130, 30);
                    btn.GetComponent<Button>().interactable = r.Enabled;
                }
            }
            content.sizeDelta = new Vector2(0, rows.Count * rowH);
            if (rows.Count == 0)
            {
                var empty = MakeText(content, "Nothing here yet.", 16, FontStyle.Italic, new Color(0.7f, 0.7f, 0.7f), TextAnchor.MiddleCenter);
                empty.rectTransform.sizeDelta = new Vector2(width - 24, 60);
            }

            w.footer = MakeText(root, footerText ?? "", 14, FontStyle.Normal, new Color(0.85f, 0.88f, 0.9f), TextAnchor.MiddleLeft);
            Place(w.footer.rectTransform, 18, height - 42, width - 36, 30);
            open = w;
            UiPopup.BringToFront();
            return w;
        }

        public void SetFooter(string text) { if (footer) footer.text = text; }

        public static void CloseOpen() { if (open) open.Close(); }

        public void Close()
        {
            if (open == this) open = null;
            ClosedFrame = Time.frameCount;
            Destroy(gameObject);
            onClose?.Invoke();
        }

        void Update()
        {
            if (!closable || UiPopup.IsOpen || Net.ChatBox.Typing) return;
#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) Close();
#else
            if (Input.GetKeyDown(KeyCode.Escape)) Close();
#endif
        }

        static void Place(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
        }

        static Text MakeText(Transform parent, string s, int size, FontStyle style, Color color, TextAnchor anchor)
        {
            var rt = NuiView.NewRect("Text", parent);
            var t = rt.gameObject.AddComponent<Text>();
            t.font = UiAssets.Font("Verdana " + size, out _, out _);
            t.fontSize = size; t.fontStyle = style; t.color = color; t.alignment = anchor;
            t.horizontalOverflow = HorizontalWrapMode.Wrap; t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false; t.supportRichText = true;
            t.text = s ?? "";
            return t;
        }

        static RectTransform MakeButton(Transform parent, string label, Action onClick, Color color)
        {
            var rt = NuiView.NewRect("Button " + label, parent);
            rt.gameObject.AddComponent<Image>().color = color;
            var b = rt.gameObject.AddComponent<Button>();
            b.onClick.AddListener(() => onClick?.Invoke());
            var t = MakeText(rt, label, 14, FontStyle.Bold, Color.black, TextAnchor.MiddleCenter);
            t.rectTransform.anchorMin = Vector2.zero; t.rectTransform.anchorMax = Vector2.one;
            t.rectTransform.offsetMin = t.rectTransform.offsetMax = Vector2.zero;
            return rt;
        }
    }
}
