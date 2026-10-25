using System;
using System.Collections.Generic;
using DriftCity.Core;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace DriftCity.UI
{
    /// <summary>
    /// A game.nui dialog rebuilt as uGUI. Layout rules (inferred from the data):
    /// 1024x768 virtual screen, y down; anchors are flags 1/2/4 (left/center/right) | 8/16/32 (top/middle/bottom).
    /// A dialog's origin is its anchor (E) plus offset (G); controls sit at NATION H from that origin,
    /// or from their own screen anchor when NATION F is present. Each control has EC elements, each with
    /// SZk size, POSk offset, Ik_state image, UVk_state corners, Ck_state tint and optional Tk text.
    /// </summary>
    public sealed class NuiView : MonoBehaviour
    {
        public NuiDialog Dialog { get; private set; }
        public string Key => Dialog?.Key;
        readonly Dictionary<string, NuiControlView> byId = new Dictionary<string, NuiControlView>();
        RectTransform origin;

        public static event Action<NuiView, string> Clicked;

        public static NuiView Create(NuiDialog dialog, Transform canvas)
        {
            var go = new GameObject($"Dialog {dialog.Id} {dialog.Key}", typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(canvas, false);
            Stretch(rt);
            var view = go.AddComponent<NuiView>();
            view.Build(dialog);
            return view;
        }

        void Build(NuiDialog dialog)
        {
            Dialog = dialog;
            origin = NewRect("origin", transform);
            int anchor = dialog.Anchor == 0 ? 9 : dialog.Anchor;
            SetAnchor(origin, anchor);
            NuiControl.TryVec2(NuiControl.Str(dialog.Attr, "G"), out float gx, out float gy);
            origin.anchoredPosition = new Vector2(gx, -gy);

            foreach (var c in dialog.Controls)
            {
                Transform parent = origin;
                RectTransform rt;
                int f = NuiControl.Int(c.Nation, "F", 0);
                if (f != 0)
                {
                    rt = NewRect(c.Name ?? c.Id.ToString(), transform);
                    SetAnchor(rt, f);
                }
                else rt = NewRect(c.Name ?? c.Id.ToString(), parent);
                NuiControl.TryVec2(NuiControl.Str(c.Nation, "H"), out float hx, out float hy);
                rt.anchoredPosition = new Vector2(hx, -hy);
                var cv = rt.gameObject.AddComponent<NuiControlView>();
                cv.Build(this, c);
                if (!string.IsNullOrEmpty(c.IdName)) byId[c.IdName] = cv;
                if (NuiControl.Int(c.Nation, "K", 1) == 0) startHidden.Add(cv);
            }

            // NATION K=0 controls start hidden in the client, which reveals them at runtime. Our screens were written
            // against fully visible layouts, so K=0 is honoured in full only for dialogs listed in HonorHidden; elsewhere
            // just the hover cues are: a hidden "<button>TEXT/_TEXT/_TOOLTIP/_LIGHT/_ONMOUSE" shows while <button> is hovered.
            bool all = HonorHidden.Contains(dialog.Id);
            foreach (var cv in startHidden)
            {
                var m = string.IsNullOrEmpty(cv.Data.IdName) ? null : HoverCue.Match(cv.Data.IdName);
                var partner = m != null && m.Success ? Control(m.Groups[1].Value) : null;
                if (partner && partner.Data.Type == NuiControlView.Static) partner = null;
                if (!all && !partner) continue;
                cv.gameObject.SetActive(false);
                if (partner) { var cue = cv; partner.Hovered += on => { if (cue) cue.gameObject.SetActive(on); }; }
            }
            startHidden.Clear();
        }

        readonly List<NuiControlView> startHidden = new List<NuiControlView>();
        static readonly System.Text.RegularExpressions.Regex HoverCue = new System.Text.RegularExpressions.Regex(@"^(.*?)(_TOOLTIP|TOOLTIP|_TEXT|TEXT|_LIGHT|_ONMOUSE\d*)$");

        /// <summary>Dialogs whose K=0 (start hidden) controls are all honoured: the front-end screens rebuilt to the client's logic.</summary>
        public static readonly HashSet<int> HonorHidden = new HashSet<int>
        {
            4120000, 4130000, 4140000, 4150000,
            // Battle Zone (PvpLobby / PvpWaitRoom / PvpHud): lobby, create room, wait room, rank list
            1000000, 1020000, 1100000, 1380000,
        };

        /// <summary>
        /// The visible, enabled control bound to a key (NATION J = Windows virtual-key code: 13 Enter, 8 Backspace,
        /// 27 Esc, 49.. digits), or null.
        /// </summary>
        public NuiControlView Hotkey(int virtualKey)
        {
            foreach (var cv in byId.Values)
                if (cv && cv.isActiveAndEnabled && cv.Interactable && cv.Data.Type != NuiControlView.Static && NuiControl.Int(cv.Data.Nation, "J", 0) == virtualKey)
                    return cv;
            return null;
        }

        public NuiControlView Control(string idName) => idName != null && byId.TryGetValue(idName, out var c) ? c : null;
        public IEnumerable<NuiControlView> Controls => byId.Values;

        public void SetText(string idName, string text) { var c = Control(idName); if (c) c.SetText(text); }
        public string GetInput(string idName) => Control(idName)?.Input?.text ?? "";
        public void SetVisible(string idName, bool visible) { var c = Control(idName); if (c) c.gameObject.SetActive(visible); }
        public void SetEnabled(string idName, bool enabled) { var c = Control(idName); if (c) c.Interactable = enabled; }

        internal void RaiseClick(string idName) => Clicked?.Invoke(this, idName);

        /// <summary>Simulates a click on a control (automation / tests).</summary>
        public void Click(string idName) { var c = Control(idName); if (c && c.Data.Type == NuiControlView.Check) c.SetChecked(!c.Checked); RaiseClick(idName); }

        public void Close() => Destroy(gameObject);

        // ---------- layout helpers ----------
        internal static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.sizeDelta = Vector2.zero;
            return rt;
        }

        internal static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            rt.pivot = new Vector2(0, 1);
        }

        static void SetAnchor(RectTransform rt, int flags)
        {
            float x = (flags & 2) != 0 ? 0.5f : (flags & 4) != 0 ? 1f : 0f;
            float y = (flags & 16) != 0 ? 0.5f : (flags & 32) != 0 ? 0f : 1f;
            rt.anchorMin = rt.anchorMax = new Vector2(x, y);
            rt.pivot = new Vector2(0, 1);
        }
    }

    /// <summary>
    /// One game.nui control: its image/text elements plus button/checkbox/edit behaviour. Element states follow the
    /// client (DriftCity_RE/UI.md): game.nui slot s = state s-1 of the editor table 0x87fc10, 1 NORMAL, 2 OVER,
    /// 3 PRESSED, 4 FOCUS, 5 DISABLED, 6 HIDDEN; a check box's element 2 is 1 "Checked" / 6 "UnChecked".
    /// </summary>
    public sealed class NuiControlView : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler, IPointerClickHandler
    {
        public NuiControl Data { get; private set; }
        public InputField Input { get; private set; }
        public bool Checked { get; private set; }
        public bool Interactable { get => interactable; set { interactable = value; Refresh(); } }
        /// <summary>
        /// The selected look of tabs, filters and list rows: the client sets the control's FOCUS flag every frame
        /// (vtable +0x14 = 0x7b62d0, after clearing the hover flag; e.g. the dealer tabs 0x5a2ba0), so it draws slot 4.
        /// </summary>
        public bool Selected { get => selected; set { selected = value; Refresh(); } }

        NuiView owner;
        bool selected;
        RawImage[] images;
        Text[] texts;
        Vector2 shift;
        bool hover, pressed, interactable = true;

        // control TYPE values (factory 0x7b1e60): 1 static, 2 button, 3 check box (derives from button), 4 unused,
        // 5 combo box, 6 slider, 7 nine-image frame (unused in game.nui), 8 edit, 9 list box, 10 scroll bar, 11 tree list
        public const int Static = 1, Button = 2, Check = 3, Combo = 5, Slider = 6, Edit = 8, ListBox = 9, ScrollBar = 10, Tree = 11;
        /// <summary>game.nui state slots (client state index + 1).</summary>
        public const int SlotNormal = 1, SlotOver = 2, SlotPressed = 3, SlotFocus = 4, SlotDisabled = 5, SlotHidden = 6;

        public void Build(NuiView owner, NuiControl c)
        {
            this.owner = owner;
            Data = c;
            int count = Mathf.Max(1, NuiControl.Int(c.Info, "EC", 1));
            images = new RawImage[count + 1];
            texts = new Text[count + 1];
            var rt = (RectTransform)transform;
            NuiControl.TryVec2(c.I("SZ1"), out float w, out float h);
            rt.sizeDelta = new Vector2(w, h);

            for (int k = 1; k <= count; k++)
            {
                if (!NuiControl.TryVec2(c.I("SZ" + k), out float ew, out float eh)) { ew = w; eh = h; }
                NuiControl.TryVec2(c.I("POS" + k), out float px, out float py);
                string img = c.I($"I{k}_1");
                string text = c.I("T" + k);
                // interactive controls draw an element in any state that has an image or a colour (no image = a plain
                // colour quad, e.g. the help menu's hover/selected bars); statics only with their normal image
                bool drawn = img != null || c.Type != Static && HasStateArt(c, k);
                if (!drawn && text == null && k > 1 && c.I($"C{k}_1") == null) continue;

                var el = NuiView.NewRect("e" + k, transform);
                el.anchoredPosition = new Vector2(px, -py);
                el.sizeDelta = new Vector2(ew, eh);
                if (drawn)
                {
                    var ri = el.gameObject.AddComponent<RawImage>();
                    ri.raycastTarget = false;
                    images[k] = ri;
                }
                if (text != null || c.Type == Edit && k == 1)
                {
                    var trt = drawn ? NuiView.NewRect("text", el) : el;
                    if (drawn) trt.sizeDelta = new Vector2(ew, eh);
                    var t = trt.gameObject.AddComponent<Text>();
                    t.font = UiAssets.Font(c.I("FN" + k) ?? c.I("FN1"), out int size, out FontStyle style);
                    t.fontSize = size;
                    t.fontStyle = style;
                    t.alignment = Align(NuiControl.Int(c.Info, "TA" + k, NuiControl.Int(c.Info, "TA1", 0)));
                    t.horizontalOverflow = HorizontalWrapMode.Wrap;
                    t.verticalOverflow = VerticalWrapMode.Overflow;
                    t.raycastTarget = false;
                    t.supportRichText = false;
                    t.color = ParseColor(c.I($"FC{k}_1") ?? c.I("FC1_1"), Color.white);
                    // designers left font names as placeholder text in runtime-filled fields
                    t.text = text == null || System.Text.RegularExpressions.Regex.IsMatch(text, "^(Verdana|Calibri|Arial|Tahoma)+$") ? "" : text;
                    texts[k] = t;
                }
            }

            if (c.Type == Edit)
            {
                var field = gameObject.AddComponent<InputField>();
                var t = texts[1];
                if (t == null)
                {
                    t = NuiView.NewRect("text", transform).gameObject.AddComponent<Text>();
                    t.font = UiAssets.Font(c.I("FN1"), out int s, out _); t.fontSize = s;
                    ((RectTransform)t.transform).sizeDelta = new Vector2(w, h);
                }
                t.alignment = TextAnchor.MiddleLeft;
                t.color = ParseColor(c.I("FC1_1"), Color.black);
                t.supportRichText = false;
                field.textComponent = t;
                field.lineType = InputField.LineType.SingleLine;
                if ((c.IdName ?? "").Contains("PASS")) field.contentType = InputField.ContentType.Password;
                var hit = gameObject.AddComponent<Image>();
                hit.color = new Color(1, 1, 1, 0.001f);
                Input = field;
            }
            else if (c.Type != Static)
            {
                // pointer events over element 1's rect, also in states whose art is transparent (hover-only rows,
                // invisible hotkey twins): the client hit-tests element rects whatever their colour (0x7b9e80)
                var hrt = NuiView.NewRect("hit", transform);
                hrt.SetAsFirstSibling();
                NuiControl.TryVec2(c.I("POS1"), out float hx, out float hy);
                hrt.anchoredPosition = new Vector2(hx, -hy);
                hrt.sizeDelta = new Vector2(w, h);
                var hit = hrt.gameObject.AddComponent<Image>(); hit.color = new Color(0, 0, 0, 0.001f);
            }
            Refresh();
        }

        public void SetText(string text)
        {
            foreach (var t in texts) if (t) { t.text = text; return; }
            var el = NuiView.NewRect("text", transform);
            el.sizeDelta = ((RectTransform)transform).sizeDelta;
            var nt = el.gameObject.AddComponent<Text>();
            nt.font = UiAssets.Font(Data.I("FN1"), out int size, out _);
            nt.fontSize = size;
            nt.alignment = TextAnchor.MiddleCenter;
            nt.raycastTarget = false;
            nt.text = text;
            texts[0] = nt;
        }

        public void SetChecked(bool value) { Checked = value; Refresh(); }

        /// <summary>
        /// The control's state slot, in the client's order (button update 0x7c9270, check box 0x7c8f70): disabled, else
        /// pressed, else focus (our Selected; the client clears the hover flag of a focused tab), else over, else normal.
        /// </summary>
        public int State => Data.Type == Static ? SlotNormal : !interactable ? SlotDisabled : pressed ? SlotPressed : selected ? SlotFocus : hover ? SlotOver : SlotNormal;

        void Refresh()
        {
            int state = State;
            for (int k = 1; k < images.Length; k++)
            {
                var ri = images[k];
                if (!ri) continue;
                if (Data.Type != Button && Data.Type != Check) { DrawOther(ri, k, k == 1 ? state : SlotNormal); continue; }
                // buttons move every element with the control state; a check box only its box (element 1), its element 2 is
                // the mark: slot 1 "Checked" (alpha 0x70 while disabled) or slot 6 "UnChecked"; further elements stay normal
                int s = Data.Type == Button || k == 1 ? state : k == 2 ? (Checked ? SlotNormal : SlotHidden) : SlotNormal;
                string img = Data.I($"I{k}_{s}");
                Color32 col = StateColor(Data, k, s);
                if (Data.Type == Check && k == 2 && Checked && !interactable) col.a = 0x70;
                // the client draws an element only while its state colour has alpha; a state without an image is a plain quad
                var tex = img != null ? UiAssets.Texture(img) : null;
                ri.texture = tex;
                ri.enabled = col.a != 0 && (img == null || tex != null);
                ri.uvRect = img != null ? UvRect(Data.I($"UV{k}_{s}") ?? Data.I($"UV{k}_1")) : new Rect(0, 0, 1, 1);
                ri.color = col;
            }
            if (Data.Type != Button && Data.Type != Check) return;
            // text colour follows element 1's state (FC1_s, client default white; 0x7b7033)
            var t = texts[1];
            if (t && (Data.I("FC1_1") != null || Data.I($"FC1_{state}") != null))
                t.color = state == SlotNormal ? ParseColor(Data.I("FC1_1"), Color.white) : ParseColor(Data.I($"FC1_{state}"), Color.white);
            // a button's elements sit 1 px up-left while hovered and 1 px down-right while pressed (0x7c9270)
            if (Data.Type == Button)
            {
                var want = pressed && interactable ? new Vector2(1, -1) : hover && interactable && !selected ? new Vector2(-1, 1) : Vector2.zero;
                if (want != shift)
                {
                    for (int k = 1; k < images.Length; k++)
                    {
                        var el = images[k] ? (RectTransform)images[k].transform : texts[k] && texts[k].transform.parent == transform ? (RectTransform)texts[k].transform : null;
                        if (el) el.anchoredPosition += want - shift;
                    }
                    shift = want;
                }
            }
        }

        /// <summary>Statics (normal image only) and the list/combo/slider/edit/scroll kinds, whose own updates are not
        /// rebuilt: element 1 takes the state image when there is one, else the normal one, with an opaque tint.</summary>
        void DrawOther(RawImage ri, int k, int s)
        {
            var tex = UiAssets.Texture(Data.I($"I{k}_{s}") ?? Data.I($"I{k}_1"));
            ri.texture = tex;
            ri.enabled = tex != null;
            ri.uvRect = UvRect(Data.I($"UV{k}_{s}") ?? Data.I($"UV{k}_1"));
            ri.color = ParseColor(Data.I($"C{k}_{s}") ?? Data.I($"C{k}_1"), Color.white);
        }

        static bool HasStateArt(NuiControl c, int k)
        {
            for (int s = 1; s <= 6; s++) if (c.I($"I{k}_{s}") != null || c.I($"C{k}_{s}") != null) return true;
            return false;
        }

        /// <summary>
        /// Ck_s as the client reads it (0x7c6ae0): "%X" into ARGB, so a six-digit value has alpha 0 (not drawn); missing ->
        /// FFFFFFFF, but C8FFFFFF for PRESSED and 00FFFFFF for HIDDEN.
        /// </summary>
        public static Color32 StateColor(NuiControl c, int k, int s)
        {
            string v = c.I($"C{k}_{s}");
            uint argb = s == SlotPressed ? 0xC8FFFFFFu : s == SlotHidden ? 0x00FFFFFFu : 0xFFFFFFFFu;
            if (v != null && uint.TryParse(v, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint p)) argb = p;
            return new Color32((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (byte)(argb >> 24));
        }

        /// <summary>UV corners "u0 v0 u1 v0 u0 v1 u1 v1" (top-left, top-right, bottom-left, bottom-right) in D3D
        /// convention; DDS rows were uploaded top-down, so V is flipped with a negative-height rect.</summary>
        static Rect UvRect(string uv)
        {
            var f = NuiControl.Floats(uv);
            if (f.Length < 8) return new Rect(0, 1, 1, -1);
            float u0 = Mathf.Min(f[0], f[2]), u1 = Mathf.Max(f[0], f[2]);
            float v0 = Mathf.Min(f[1], f[5]), v1 = Mathf.Max(f[1], f[5]);
            return new Rect(u0, v1, u1 - u0, v0 - v1);
        }

        static TextAnchor Align(int ta)
        {
            int hz = ta & 3, vt = (ta >> 2) & 3;
            if (vt == 1) return hz == 1 ? TextAnchor.MiddleCenter : hz == 2 ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft;
            if (vt == 2) return hz == 1 ? TextAnchor.LowerCenter : hz == 2 ? TextAnchor.LowerRight : TextAnchor.LowerLeft;
            return hz == 1 ? TextAnchor.UpperCenter : hz == 2 ? TextAnchor.UpperRight : TextAnchor.UpperLeft;
        }

        public static Color ParseColor(string hex, Color fallback)
        {
            if (!NuiControl.TryColor(hex, out byte r, out byte g, out byte b, out byte a)) return fallback;
            return new Color32(r, g, b, a);
        }

        /// <summary>Pointer entered (true) or left (false) the control.</summary>
        public event Action<bool> Hovered;

        public void OnPointerEnter(PointerEventData e) { hover = true; Refresh(); Hovered?.Invoke(true); }
        public void OnPointerExit(PointerEventData e) { hover = false; pressed = false; Refresh(); Hovered?.Invoke(false); }
        public void OnPointerDown(PointerEventData e) { if (interactable) { pressed = true; Refresh(); } }
        public void OnPointerUp(PointerEventData e) { pressed = false; Refresh(); }
        public void OnPointerClick(PointerEventData e)
        {
            if (!interactable || Data.Type == Static) return;
            if (Data.Type == Check) { Checked = !Checked; Refresh(); }
            UiSounds.Click();
            if (!string.IsNullOrEmpty(Data.IdName)) owner.RaiseClick(Data.IdName);
        }
    }
}
