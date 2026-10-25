using System.Collections.Generic;
using System.Linq;
using DriftCity.Core;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace DriftCity.Game
{
    /// <summary>
    /// In-city menus on the original game.nui dialogs: the garage (with its garage.nif scene), driver info,
    /// options and inventory. One of these screens is open at a time; Esc steps back or closes it.
    /// </summary>
    public static partial class OriginalMenus
    {
        static PlayerProfile P => PlayerProfile.Current;
        static UiManager Ui => UiManager.Instance;

        // ================= screen bookkeeping =================
        static readonly List<NuiView> views = new List<NuiView>();
        static ScreenGuard guard;

        public static bool IsOpen
        {
            get
            {
                foreach (var v in views) if (v) return true;
                return false;
            }
        }

        /// <summary>Closes whichever original menu is open (its 3D scene and settings preview go with it).</summary>
        public static void Close()
        {
            if (guard) { var c = guard.Cleanup; guard.Cleanup = null; guard.Back = null; c?.Invoke(); }
            guard = null;
            foreach (var v in views) if (v) { if (Ui) Ui.Close(v); else Object.Destroy(v.gameObject); }
            views.Clear();
        }

        /// <summary>Opens the first dialog of a new screen; the guard on it runs the cleanup however the dialog goes away.</summary>
        internal static NuiView OpenScreen(int dialogId, System.Func<bool> back = null)
        {
            Close();
            ListWindow.CloseOpen();
            var v = Ui ? Ui.Open(dialogId) : null;
            if (!v) return null;
            views.Add(v);
            guard = v.gameObject.AddComponent<ScreenGuard>();
            guard.Back = back;
            return v;
        }

        internal static void OnClose(System.Action cleanup) { if (guard) guard.Cleanup = cleanup; }

        internal static NuiView Open(int dialogId)
        {
            var v = Ui.Open(dialogId);
            if (v) views.Add(v);
            return v;
        }

        internal static void CloseView(NuiView v)
        {
            if (!v) return;
            views.Remove(v);
            Ui.Close(v);
        }

        sealed class ScreenGuard : MonoBehaviour
        {
            public System.Action Cleanup;
            /// <summary>Esc handler: returns true when it stepped back inside the screen instead of closing it.</summary>
            public System.Func<bool> Back;

            void OnDestroy() { var c = Cleanup; Cleanup = null; c?.Invoke(); }

            void Update()
            {
#if ENABLE_INPUT_SYSTEM
                bool esc = Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame;
#else
                bool esc = Input.GetKeyDown(KeyCode.Escape);
#endif
                if (!esc || UiPopup.IsOpen || Net.ChatBox.Typing || ListWindow.IsOpen || ListWindow.ClosedFrame == Time.frameCount) return;
                ListWindow.ClosedFrame = Time.frameCount;   // the same Esc must not also open the quit menu
                UiSounds.Cancel();
                if (Back == null || !Back()) OriginalMenus.Close();
            }
        }

        // ================= widget helpers =================
        internal static void Hide(NuiView v, params string[] ids) { if (v) foreach (var id in ids) v.SetVisible(id, false); }

        /// <summary>Destroys a helper child now-ish (renamed first so a second refresh in the same frame doesn't find it).</summary>
        internal static void Remove(Transform parent, string child)
        {
            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                var t = parent.GetChild(i);
                if (t.name != child) continue;
                t.name = "(removed)";
                t.gameObject.SetActive(false);
                Object.Destroy(t.gameObject);
            }
        }

        internal static Text Caption(Transform parent, string child, string text, string font, Color color, TextAnchor anchor, bool wrap = false)
        {
            var t = parent.Find(child);
            Text label;
            if (t && t.TryGetComponent(out label)) { }
            else
            {
                var rt = NuiView.NewRect(child, parent);
                NuiView.Stretch(rt);
                label = rt.gameObject.AddComponent<Text>();
                label.font = UiAssets.Font(font, out int size, out var style);
                label.fontSize = size;
                label.fontStyle = style;
                label.raycastTarget = false;
                label.supportRichText = false;
                label.verticalOverflow = VerticalWrapMode.Overflow;
                rt.gameObject.AddComponent<Shadow>().effectColor = new Color(0, 0, 0, 0.85f);
            }
            label.horizontalOverflow = wrap ? HorizontalWrapMode.Wrap : HorizontalWrapMode.Overflow;
            label.alignment = anchor;
            label.color = color;
            label.text = text ?? "";
            return label;
        }

        // The client's CMN dialogs are image catalogues: one control per car or grade, named by the key the client looks
        // it up with. Name plates (XL 2720000, M 2730000, SS 2740000) and car keys (1880000) are keyed by the car's old
        // model name (vehiclelist OldFileName, "ac01_01_a") or, for later cars, its model file ("pc_0074s"); grade badges
        // (S 2690000, M 2710000, L 2700000) by grade ("V1".."V9", "Q3"...).
        public const int PlateXL = 2720000, PlateM = 2730000, PlateSS = 2740000, CarKeys = 1880000, GradeS = 2690000, GradeM = 2710000, GradeL = 2700000;
        static readonly Dictionary<int, Dictionary<string, NuiControl>> cmn = new Dictionary<int, Dictionary<string, NuiControl>>();

        /// <summary>The image a CMN catalogue holds for a key, if its texture loads.</summary>
        internal static Texture2D CmnImage(int dialog, string key, out Rect uv)
        {
            uv = new Rect(0, 1, 1, -1);
            if (string.IsNullOrEmpty(key)) return null;
            if (!cmn.TryGetValue(dialog, out var set))
            {
                cmn[dialog] = set = new Dictionary<string, NuiControl>(System.StringComparer.OrdinalIgnoreCase);
                var d = UiAssets.Layout?.Get(dialog);
                if (d != null)
                    foreach (var c in d.Controls)
                    {
                        var k = c.Name?.Trim();
                        if (!string.IsNullOrEmpty(k) && c.I("I1_1") != null && !set.ContainsKey(k)) set[k] = c;
                    }
            }
            if (!set.TryGetValue(key.Trim(), out var cut)) return null;
            uv = UvRect(cut.I("UV1_1"));
            return UiAssets.Texture(cut.I("I1_1"));
        }

        /// <summary>A car's image from a car-keyed catalogue: by its old model name, else by its model file.</summary>
        internal static Texture2D CarImage(int dialog, VehicleInfo car, out Rect uv)
        {
            uv = new Rect(0, 1, 1, -1);
            if (car == null) return null;
            return CmnImage(dialog, car.OldModel, out uv) ?? CmnImage(dialog, car.ModelBase, out uv);
        }

        /// <summary>Shows a catalogue image in a control (its first image), or writes the fallback text when there is none.</summary>
        static void Show(NuiControlView c, Texture2D tex, Rect uv, string fallback)
        {
            bool first = true;
            foreach (var ri in c.GetComponentsInChildren<RawImage>(true))
            {
                ri.enabled = tex && first;
                if (tex && first) { ri.texture = tex; ri.uvRect = uv; ri.color = Color.white; }
                first = false;
            }
            Caption(c.transform, "caption", tex ? "" : fallback, "Trebuchet MS 13 700", Color.white, TextAnchor.MiddleCenter);
        }

        /// <summary>The car's name plate in the size the control is drawn at; the name is written if the plate is missing.</summary>
        internal static void Plate(NuiView v, string id, string text, VehicleInfo car = null)
        {
            var c = v ? v.Control(id) : null;
            if (!c) return;
            float w = ((RectTransform)c.transform).rect.width;
            int set = w >= 150 ? PlateXL : w >= 80 ? PlateM : PlateSS;
            var tex = CarImage(set, car, out var uv);
            Show(c, tex, uv, text);
        }

        public const int MakerEmblems = 2750000;

        /// <summary>The maker's emblem (catalogue 2750000, keyed by vehiclelist Maker).</summary>
        internal static void MakerEmblem(NuiView v, string id, string maker)
        {
            var c = v ? v.Control(id) : null;
            if (!c) return;
            var tex = CmnImage(MakerEmblems, maker, out var uv);
            Show(c, tex, uv, maker ?? "");
        }

        /// <summary>The grade badge in the size the control is drawn at.</summary>
        internal static void Emblem(NuiView v, string id, CarGrade g)
        {
            var c = v ? v.Control(id) : null;
            if (!c) return;
            string key = g != null ? $"{g.GradeType}{g.GradeLvl}" : null;
            float w = ((RectTransform)c.transform).rect.width;
            int set = w >= 50 ? GradeL : w >= 32 ? GradeM : GradeS;
            var tex = CmnImage(set, key, out var uv) ?? CmnImage(GradeM, key, out uv);
            Show(c, tex, uv, key ?? "");
        }

        /// <summary>Greys a control out and keeps clicks off it (survives the control's own hover refreshes).</summary>
        internal static void Dim(Component c, bool dim, float alpha = 0.4f)
        {
            if (!c) return;
            if (!c.TryGetComponent<CanvasGroup>(out var g)) g = c.gameObject.AddComponent<CanvasGroup>();
            g.alpha = dim ? alpha : 1f;
            g.blocksRaycasts = !dim;
            g.interactable = !dim;
        }

        /// <summary>Shows a button in its selected look (active tab / floor): the client's FOCUS state, slot 4 (NuiControlView.Selected).</summary>
        internal static void Pressed(NuiControlView c, bool on)
        {
            if (c) c.Selected = on;
        }

        /// <summary>A checkbox's second element is its tick; it shows only while checked.</summary>
        static void Mark(NuiControlView c, bool on)
        {
            if (!c) return;
            c.SetChecked(on);
            var tick = c.transform.Find("e2");
            if (tick) tick.gameObject.SetActive(on);
        }

        /// <summary>An On/Off pair of checkboxes; changed == null shows it disabled.</summary>
        static void Radio(NuiView v, string onId, string offId, bool value, System.Action<bool> changed)
        {
            var on = v.Control(onId);
            var off = v.Control(offId);
            Mark(on, value); Mark(off, !value);
            Dim(on, changed == null); Dim(off, changed == null);
            if (changed == null) return;
            Ui.On(onId, _ => { Mark(on, true); Mark(off, false); changed(true); });
            Ui.On(offId, _ => { Mark(on, false); Mark(off, true); changed(false); });
        }

        /// <summary>A combo box shown as its current value; clicking the field or its arrow raises the control's click.</summary>
        static void Combo(NuiView v, string id, string text, bool enabled)
        {
            var c = v.Control(id);
            if (!c) return;
            var field = c.transform.Find("e1") as RectTransform;
            Caption(field ? field : c.transform, "value", text, c.Data.I("FN1"), NuiControlView.ParseColor(c.Data.I("FC1_1"), Color.white), TextAnchor.MiddleLeft);
            // the control's own rect sits under the row title; only the field and the arrow take clicks
            if (c.TryGetComponent<Image>(out var hit)) hit.raycastTarget = false;
            if (field && !field.GetComponent<Graphic>()) field.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, 0.001f);
            var arrow = c.transform.Find("e2");
            if (arrow && arrow.TryGetComponent<RawImage>(out var ai)) ai.raycastTarget = true;
            Dim(c, !enabled);
        }

        /// <summary>A game.nui slider (track e1, thumb e2) driven 0..1; changed == null shows it disabled.</summary>
        static NuiSlider Slider(NuiView v, string id, float value, System.Action<float> changed)
        {
            var c = v.Control(id);
            var track = c ? c.transform.Find("e1") as RectTransform : null;
            var thumb = c ? c.transform.Find("e2") as RectTransform : null;
            if (!track || !thumb) return null;
            var s = c.GetComponentInChildren<NuiSlider>(true);
            if (!s)
            {
                var hit = NuiView.NewRect("hit", c.transform);
                hit.anchoredPosition = track.anchoredPosition + new Vector2(-8, 10);
                hit.sizeDelta = new Vector2(track.sizeDelta.x + 16, 24);
                hit.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, 0.001f);
                s = hit.gameObject.AddComponent<NuiSlider>();
                s.Track = track;
                s.Thumb = thumb;
            }
            s.Changed = changed;
            s.Value = value;
            Dim(c, changed == null);
            return s;
        }

        sealed class NuiSlider : MonoBehaviour, IPointerDownHandler, IDragHandler
        {
            public RectTransform Track, Thumb;
            public System.Action<float> Changed;
            float value;

            public float Value
            {
                get => value;
                set
                {
                    this.value = Mathf.Clamp01(value);
                    if (Track && Thumb) Thumb.anchoredPosition = new Vector2(Track.anchoredPosition.x + this.value * Track.sizeDelta.x - Thumb.sizeDelta.x * 0.5f, Thumb.anchoredPosition.y);
                }
            }

            public void OnPointerDown(PointerEventData e) => Drag(e);
            public void OnDrag(PointerEventData e) => Drag(e);

            void Drag(PointerEventData e)
            {
                if (Changed == null || !Track) return;
                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(Track, e.position, e.pressEventCamera, out var local)) return;
                Value = local.x / Mathf.Max(1f, Track.sizeDelta.x);
                Changed(value);
            }
        }

        internal sealed class SlotButton : MonoBehaviour, IPointerClickHandler, IPointerEnterHandler
        {
            public System.Action Click, Hover;
            public void OnPointerClick(PointerEventData e) { if (Click == null) return; UiSounds.Click(); Click(); }
            public void OnPointerEnter(PointerEventData e) => Hover?.Invoke();
        }

        /// <summary>Makes a rect clickable (static slots in game.nui take no clicks of their own).</summary>
        internal static SlotButton Clickable(RectTransform rt, System.Action click, System.Action hover = null)
        {
            if (!rt) return null;
            if (rt.TryGetComponent<Graphic>(out var g)) g.raycastTarget = true;
            else rt.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, 0.001f);
            if (!rt.TryGetComponent<SlotButton>(out var b)) b = rt.gameObject.AddComponent<SlotButton>();
            b.Click = click;
            b.Hover = hover;
            return b;
        }

        /// <summary>A rect at a control's place in its dialog, offset in dialog pixels (cells the dialog draws without naming them).</summary>
        internal static RectTransform Cell(NuiControlView anchor, string name, Vector2 offset, Vector2 size)
        {
            var at = (RectTransform)anchor.transform;
            Remove(at.parent, name);
            var rt = NuiView.NewRect(name, at.parent);
            rt.anchoredPosition = at.anchoredPosition + new Vector2(offset.x, -offset.y);
            rt.sizeDelta = size;
            return rt;
        }

        /// <summary>Lays element k's image for another state (a list row's selected look) over the control; the hover refresh leaves it alone.</summary>
        internal static void StateOverlay(NuiControlView c, int k, int state, bool on)
        {
            if (!c) return;
            Remove(c.transform, "state");
            var src = c.transform.Find("e" + k) as RectTransform;
            var tex = UiAssets.Texture(c.Data.I($"I{k}_{state}"));
            if (!on || !src || !tex) return;
            var rt = NuiView.NewRect("state", c.transform);
            rt.anchoredPosition = src.anchoredPosition;
            rt.sizeDelta = src.sizeDelta;
            var ri = rt.gameObject.AddComponent<RawImage>();
            ri.texture = tex;
            ri.uvRect = UvRect(c.Data.I($"UV{k}_{state}"));
            ri.raycastTarget = false;
        }

        /// <summary>Labels laid out over the editor's default.dds widget sheet (a stand-in, not artwork): only their text is drawn.</summary>
        internal static void HideDefaultBacks(NuiView v)
        {
            if (!v) return;
            foreach (var c in v.GetComponentsInChildren<NuiControlView>(true))
            {
                if (c.Data.Type != NuiControlView.Static || !(c.Data.I("I1_1") ?? "").EndsWith("_default.dds", System.StringComparison.OrdinalIgnoreCase)) continue;
                var e = c.transform.Find("e1");
                if (e && e.TryGetComponent<RawImage>(out var ri)) ri.enabled = false;
            }
        }

        /// <summary>A gauge control's bar drawn to a fraction of its width.</summary>
        internal static void Gauge(NuiControlView c, float fraction)
        {
            var bar = c ? c.transform.Find("e1") as RectTransform : null;
            if (!bar || !NuiControl.TryVec2(c.Data.I("SZ1"), out float w, out float h)) return;
            bar.sizeDelta = new Vector2(w * Mathf.Clamp01(fraction), h);
        }

        /// <summary>game.nui UV corners (top-left, top-right, bottom-left, bottom-right) as a RawImage rect; DDS rows are top-down, hence the flip.</summary>
        static Rect UvRect(string uv)
        {
            var f = NuiControl.Floats(uv);
            if (f == null || f.Length < 8) return new Rect(0, 1, 1, -1);
            float u0 = Mathf.Min(f[0], f[2]), u1 = Mathf.Max(f[0], f[2]);
            float v0 = Mathf.Min(f[1], f[5]), v1 = Mathf.Max(f[1], f[5]);
            return new Rect(u0, v1, u1 - u0, v0 - v1);
        }

        // ---------- item icons ----------
        static Dictionary<string, NuiControl> iconCuts;

        /// <summary>The client's icon for an item name: PARTSLIST (380000) and the visual shop's CMN sheet (2760000) are icon tables, one control per item named after it.</summary>
        internal static Texture2D ItemIcon(string item, out Rect uv)
        {
            uv = new Rect(0, 1, 1, -1);
            if (string.IsNullOrEmpty(item)) return null;
            if (iconCuts == null)
            {
                iconCuts = new Dictionary<string, NuiControl>(System.StringComparer.OrdinalIgnoreCase);
                foreach (int id in new[] { 380000, 2760000 })
                {
                    var d = UiAssets.Layout?.Get(id);
                    if (d == null) continue;
                    foreach (var c in d.Controls)
                    {
                        var key = c.Name?.Trim();
                        if (!string.IsNullOrEmpty(key) && c.I("I1_1") != null && !iconCuts.ContainsKey(key)) iconCuts[key] = c;
                    }
                }
            }
            if (!iconCuts.TryGetValue(item.Trim(), out var cut)) return null;
            uv = UvRect(cut.I("UV1_1"));
            return UiAssets.Texture(cut.I("I1_1"));
        }

        /// <summary>The client's icon for items it has no art for: PARTSLIST's ID_PARTSLIST_NOTAVAILABLE (control 380342,
        /// which the icon manager at 0x5942b0 keeps beside the catalogues).</summary>
        internal static Texture2D NotAvailableIcon(out Rect uv)
        {
            uv = new Rect(0, 1, 1, -1);
            var d = UiAssets.Layout?.Get(380000);
            var c = d?.Controls.Find(x => x.IdName == "ID_PARTSLIST_NOTAVAILABLE");
            if (c == null) return null;
            uv = UvRect(c.I("UV1_1"));
            return UiAssets.Texture(c.I("I1_1"));
        }

        /// <summary>An item in a slot (pos in dialog pixels): the client's icon when the item has one that loads, else a colour swatch or a dark tile with its name.</summary>
        internal static RectTransform ItemTile(Transform parent, Vector2 pos, Vector2 size, string item, string name, Color? swatch = null, int count = 0, float alpha = 1f)
        {
            Remove(parent, "item");
            var rt = NuiView.NewRect("item", parent);
            rt.anchoredPosition = new Vector2(pos.x, -pos.y);
            rt.sizeDelta = size;
            var uv = default(Rect);
            var tex = swatch.HasValue ? null : ItemIcon(item, out uv);
            // items without art (or whose art isn't shipped in this install) get the client's "not available" icon, as the
            // client does; its name is written over it so the slot doesn't read as empty
            bool noArt = false;
            if (!tex && !swatch.HasValue) { tex = NotAvailableIcon(out uv); noArt = tex; }
            if (tex)
            {
                var ri = rt.gameObject.AddComponent<RawImage>();
                ri.texture = tex;
                ri.uvRect = uv;
                ri.color = new Color(1, 1, 1, alpha);
                if (noArt) Caption(rt, "name", name, "Calibri 10 700", new Color(1, 1, 1, alpha), TextAnchor.MiddleCenter, wrap: true);
            }
            else
            {
                var c = swatch ?? new Color(0.16f, 0.2f, 0.27f, 0.92f);
                rt.gameObject.AddComponent<Image>().color = new Color(c.r, c.g, c.b, c.a * alpha);
                rt.gameObject.AddComponent<Outline>().effectColor = new Color(0, 0, 0, 0.7f);
                if (!swatch.HasValue) Caption(rt, "name", name, "Calibri 10 700", new Color(1, 1, 1, alpha), TextAnchor.MiddleCenter, wrap: true);
            }
            if (count > 1)
            {
                var label = Caption(rt, "count", $"x{count}", "Calibri 12 700", Color.white, TextAnchor.LowerRight);
                label.rectTransform.offsetMin = new Vector2(0, -6);
                label.rectTransform.offsetMax = new Vector2(4, 0);
            }
            return rt;
        }

        // ---------- car stats as the dialogs break them down ----------
        static readonly string[] StatKeys = { "SPD", "ACC", "COL", "BST" };

        /// <summary>Per stat (speed, accel, durability, booster): the grade's rating, custom-part bonus and performance parts; they add up to ShopData.Stats.</summary>
        internal static (int[] car, int[] parts, int[] user) Breakdown(OwnedCar owned)
        {
            var info = GameTables.Car(owned.VehicleIndex);
            var g = ShopData.Grade(info, owned.Grade);
            var b = PartsCatalog.Bonus(info, owned);
            var user = P.Cars.Contains(owned) ? PerfRules.Fitted(P, owned) : new int[4];
            return (new[] { g?.Speed ?? info.Speed, g?.Accel ?? info.Accel, g?.Crash ?? info.Crash, g?.Boost ?? info.Boost },
                    new[] { b.speed, b.accel, b.crash, b.boost }, user);
        }

        /// <summary>Fills {prefix}SPD_TOTAL / _CAR / _{parts} / _USER ... and the POINT_ row; returns the column sums (total, car, parts, user).</summary>
        internal static (int total, int car, int parts, int user) FillStats(NuiView v, string prefix, OwnedCar owned, string parts = "PARTS")
        {
            var (car, part, user) = Breakdown(owned);
            int sc = 0, sp = 0, su = 0;
            for (int i = 0; i < 4; i++)
            {
                v.SetText($"{prefix}{StatKeys[i]}_TOTAL", (car[i] + part[i] + user[i]).ToString());
                v.SetText($"{prefix}{StatKeys[i]}_CAR", car[i].ToString());
                v.SetText($"{prefix}{StatKeys[i]}_{parts}", part[i].ToString());
                v.SetText($"{prefix}{StatKeys[i]}_USER", user[i].ToString());
                sc += car[i]; sp += part[i]; su += user[i];
            }
            v.SetText(prefix + "POINT_TOTAL", (sc + sp + su).ToString());
            v.SetText(prefix + "POINT_CAR", sc.ToString());
            v.SetText($"{prefix}POINT_{parts}", sp.ToString());
            v.SetText(prefix + "POINT_USER", su.ToString());
            return (sc + sp + su, sc, sp, su);
        }

        internal static string CarName(OwnedCar owned)
        {
            var info = GameTables.Car(owned.VehicleIndex);
            return ShopData.Grade(info, owned.Grade)?.UiName ?? info.UiName;
        }

        static void SetLayer(GameObject go, int layer) { foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer; }

        static Transform FindNode(GameObject root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name == name) return t;
            return null;
        }

        // ================= garage =================
        // GARAGETOP (title + exit), floors, the floor view's car selector, the car you drive, the tuning panel,
        // the car list bar, the stats slot panel, the tuning slot panel and the camera buttons
        const int GarageLayer = 30;   // the dealer's preview layer; this scene stands elsewhere below the city with its own camera
        static readonly Vector3 GarageBase = new Vector3(0, -4000, 0);
        const int CarsPerFloor = 8;     // garage.nif has eight parking ports on its turntable
        const int GarageCapacity = 40;  // offline Progress.BuyCar refuses more
        static readonly string[] FloorIds = { "1ST", "2ND", "3RD", "4TH", "5TH", "6TH", "7TH", "8TH" };
        // init/camera.ini (Gamebryo z-up, here as Unity x, z, y): [camera1] "garage_closeup" for one car on the
        // turntable centre, [camera0] "garage" over the whole floor
        static readonly Vector3 CloseEye = new Vector3(5f, 2f, -0.9f), CloseLook = Vector3.zero;
        static readonly Vector3 FloorEye = new Vector3(12f, 5f, 0f), FloorLook = new Vector3(3f, 0f, 0f);
        // port1..port8 of garage.nif, used when the nodes can't be found
        static readonly Vector3[] DefaultPorts =
        {
            new Vector3(0.2f, 0, -6.2f), new Vector3(4.3f, 0, -4.4f), new Vector3(6.1f, 0, 0f), new Vector3(4.3f, 0, 4.3f),
            new Vector3(-0.1f, 0, 6.0f), new Vector3(-4.4f, 0, 4.3f), new Vector3(-6.2f, 0, -0.2f), new Vector3(-4.3f, 0, -4.6f),
        };
        const int FrontPort = 2;   // port3 faces the floor camera

        enum GarageMode { Detail, Tuning, Floor }
        static GarageMode garageMode;
        static int garageSel;
        static NuiView gTop, gFloors, gSelect, gDriving, gTunePanel, gList, gSlots, gTuneSlots, gCamera;
        static GarageRig rig;

        static float Yaw(Vector3 p) => Mathf.Atan2(p.x, p.z) * Mathf.Rad2Deg;

        public static void Garage() => OpenGarage(false);

        /// <summary>The garage in tuning mode on the car you drive.</summary>
        public static void Tuning() => OpenGarage(true);

        static void OpenGarage(bool tuning)
        {
            if (P == null || P.Cars.Count == 0) return;
            garageSel = Mathf.Clamp(P.CurrentCar, 0, P.Cars.Count - 1);
            garageMode = tuning ? GarageMode.Tuning : GarageMode.Detail;
            gTop = OpenScreen(1670000, GarageBack);
            if (!gTop) return;
            gFloors = Open(1680000);
            gSelect = Open(1690000);
            gDriving = Open(1700000);
            gTunePanel = Open(1710000);
            gList = Open(1720000);
            gSlots = Open(1730000);
            gTuneSlots = Open(1740000);
            gCamera = Open(1750000);
            BuildGarageScene();
            var myRig = rig;
            OnClose(() => { if (myRig) Object.Destroy(myRig.gameObject); if (rig == myRig) rig = null; });

            Hide(gSelect, "ID_GARAGE_SELCAR_RENT");
            Hide(gDriving, "ID_GARAGE_GETON_PANEL_RENT", "ID_GARAGE_GETON_PANEL_ASSIST");
            Hide(gTuneSlots, "ID_GARAGE_TUNNING_SLOT_AFTER_EXTEND_SDP1");   // (sic) the speed slot's id in game.nui
            foreach (var k in StatKeys) Hide(gTuneSlots, $"ID_GARAGE_TUNNING_SLOT_AFTER_EXTEND_{k}0", $"ID_GARAGE_TUNNING_SLOT_AFTER_EXTEND_{k}1");

            Ui.On("ID_GARAGETOP_EXIT", _ => Close());
            for (int f = 0; f < FloorIds.Length; f++)
            {
                int floor = f;
                Ui.On("ID_GARAGE_FLOOR_" + FloorIds[f], _ => { if (floor * CarsPerFloor < P.Cars.Count) { garageSel = floor * CarsPerFloor; RefreshGarage(); } });
            }
            Ui.On("ID_GARAGE_SELCAR_LEFT", _ => StepGarage(-1));
            Ui.On("ID_GARAGE_SELCAR_RIGHT", _ => StepGarage(1));
            Ui.On("ID_GARAGE_SELCAR_SELECT", _ => { garageMode = GarageMode.Detail; RefreshGarage(); });
            Ui.On("ID_GARAGE_CARLIST_PREV", _ => StepGarage(-1));
            Ui.On("ID_GARAGE_CARLIST_NEXT", _ => StepGarage(1));
            Ui.On("ID_GARAGE_CARLIST_VIEWENTIRE", _ => { garageMode = GarageMode.Floor; RefreshGarage(); });
            Ui.On("ID_GARAGE_CARLIST_TUNNING", _ =>
            {
                if (garageMode == GarageMode.Tuning) UpgradeGarageCar();
                else { garageMode = GarageMode.Tuning; RefreshGarage(); }
            });
            Ui.On("ID_GARAGE_CARLIST_GETON", _ => GetOnGarageCar());
            Ui.On("ID_GARAGE_CAMERA_LEFT", _ => { if (rig) rig.Orbit -= 30f; });
            Ui.On("ID_GARAGE_CAMERA_RIGHT", _ => { if (rig) rig.Orbit += 30f; });
            Ui.On("ID_GARAGE_CAMERA_RESTORE", _ => { if (rig) rig.Orbit = 0f; });
            RefreshGarage();
        }

        static bool GarageBack()
        {
            if (garageMode == GarageMode.Detail) return false;
            garageMode = GarageMode.Detail;
            RefreshGarage();
            return true;
        }

        static void StepGarage(int delta)
        {
            int n = P.Cars.Count;
            if (n == 0) return;
            garageSel = (garageSel + delta + n) % n;
            RefreshGarage();
        }

        static void GetOnGarageCar()
        {
            if (garageSel == P.CurrentCar || garageSel >= P.Cars.Count) return;
            if (GameFlow.BlockedByMission()) return;
            P.CurrentCar = garageSel;
            P.Save();
            GameFlow.Instance?.RespawnCar();
            RefreshGarage();
        }

        static void UpgradeGarageCar()
        {
            int slot = garageSel;
            var owned = P.Cars[slot];
            var info = GameTables.Car(owned.VehicleIndex);
            var grades = ShopData.Grades(info);
            int index = owned.Grade + 1;
            if (index >= grades.Count) { UiPopup.Message($"The {info.UiName} is fully tuned."); return; }
            var g = grades[index];
            int cost = Mathf.Max(grades[index - 1].UpgradeMito, 500);
            if (P.Level < g.ReqLevel) { UiPopup.Message($"You need level {g.ReqLevel}."); return; }
            if (P.Mito < cost) { UiPopup.Message($"You need {cost - P.Mito:N0} more Mito."); return; }
            UiPopup.Show($"Tune the {grades[index - 1].UiName ?? info.UiName} up to {g.UiName} for {cost:N0} Mito?", id =>
            {
                if (id != UiPopup.Yes) return;
                Progress.Upgrade(slot, index, cost, g.ReqLevel, r =>
                {
                    if (!r.Ok) return;
                    QuestTracker.Notify(QuestEvent.CarUpgraded);
                    GameFlow.Instance?.ApplyCarStats();
                    RefreshGarage();
                });
            }, UiPopup.Yes, UiPopup.No);
        }

        static void ShowView(NuiView v, bool on) { if (v) v.gameObject.SetActive(on); }

        static void RefreshGarage()
        {
            if (!gTop || P == null || P.Cars.Count == 0) return;
            int n = P.Cars.Count;
            garageSel = Mathf.Clamp(garageSel, 0, n - 1);
            // tuning is for the car you drive (its button replaces "get on")
            if (garageMode == GarageMode.Tuning && garageSel != P.CurrentCar) garageMode = GarageMode.Detail;
            var owned = P.Cars[garageSel];
            var info = GameTables.Car(owned.VehicleIndex);
            var grades = ShopData.Grades(info);
            var grade = ShopData.Grade(info, owned.Grade);
            bool current = garageSel == P.CurrentCar;
            bool floorView = garageMode == GarageMode.Floor, tuning = garageMode == GarageMode.Tuning;

            ShowView(gFloors, floorView);
            ShowView(gSelect, floorView);
            ShowView(gTunePanel, tuning);
            ShowView(gList, !floorView);
            ShowView(gSlots, garageMode == GarageMode.Detail);
            ShowView(gTuneSlots, tuning);

            // floors: the one shown is pressed, empty ones greyed out
            int floor = garageSel / CarsPerFloor;
            for (int f = 0; f < FloorIds.Length; f++)
            {
                var c = gFloors.Control("ID_GARAGE_FLOOR_" + FloorIds[f]);
                Dim(c, f * CarsPerFloor >= n, 0.35f);
                Pressed(c, f == floor);
            }

            // the floor view's selector over the car at the front of the turntable
            gSelect.SetVisible("ID_GARAGE_SELCAR_GETON", current);
            Plate(gSelect, "ID_GARAGE_SELCAR_IMGPOS", CarName(owned), GameTables.Car(owned.VehicleIndex));
            Emblem(gSelect, "ID_GARAGE_SELCAR_GRADEPOS", grade);
            gSelect.SetEnabled("ID_GARAGE_SELCAR_LEFT", n > 1);
            gSelect.SetEnabled("ID_GARAGE_SELCAR_RIGHT", n > 1);

            // the car you drive
            var cur = P.Car;
            var curInfo = GameTables.Car(cur.VehicleIndex);
            Plate(gDriving, "ID_GARAGE_GETON_PANEL_IMG", CarName(cur), GameTables.Car(cur.VehicleIndex));
            Emblem(gDriving, "ID_GARAGE_GETON_PANEL_GRADE", ShopData.Grade(curInfo, cur.Grade));

            // car list bar
            Plate(gList, "ID_GARAGE_CARLIST_IMG", CarName(owned), GameTables.Car(owned.VehicleIndex));
            Emblem(gList, "ID_GARAGE_CARLIST_CARGRADE", grade);
            gList.SetText("ID_GARAGE_CARLIST_OWNED", n.ToString());
            gList.SetText("ID_GARAGE_CARLIST_TOTAL", GarageCapacity.ToString());
            gList.SetVisible("ID_GARAGE_CARLIST_GETON", !current);
            gList.SetVisible("ID_GARAGE_CARLIST_TUNNING", current);
            gList.SetEnabled("ID_GARAGE_CARLIST_TUNNING", owned.Grade + 1 < grades.Count);
            gList.SetEnabled("ID_GARAGE_CARLIST_PREV", n > 1);
            gList.SetEnabled("ID_GARAGE_CARLIST_NEXT", n > 1);

            // stats slot panel: rating = grade + custom parts + performance parts; one performance slot per stat
            FillStats(gSlots, "ID_GARAGE_GETON_SLOT_", owned);
            gSlots.SetText("ID_GARAGE_GETON_SLOT_ID", P.Name);
            gSlots.SetText("ID_GARAGE_GETON_SLOT_LEVEL", $"Lv {P.Level}");
            gSlots.SetText("ID_GARAGE_GETON_SLOT_EXP", $"{P.Exp:N0}");
            gSlots.SetText("ID_GARAGE_GETON_SLOT_MITO", $"{P.Mito:N0}");
            PerfSlotIcons(gSlots, "ID_GARAGE_GETON_SLOT_", owned);

            if (tuning) RefreshTuning(owned, info, grades);
            UpdateGarageScene();
        }

        /// <summary>EXIST_x0 when a performance part is fitted for that stat; the second slot of each stat stays closed.</summary>
        static void PerfSlotIcons(NuiView v, string prefix, OwnedCar owned)
        {
            for (int t = 0; t < 4; t++)
            {
                bool fitted = owned.PerfSlots != null && t < owned.PerfSlots.Length && owned.PerfSlots[t] >= 0;
                v.SetVisible($"{prefix}EXIST_{StatKeys[t]}0", fitted);
                v.SetVisible($"{prefix}EXIST_{StatKeys[t]}1", false);
                v.SetVisible($"{prefix}CLOSE_{StatKeys[t]}0", false);
                v.SetVisible($"{prefix}CLOSE_{StatKeys[t]}1", true);
            }
        }

        static void RefreshTuning(OwnedCar owned, VehicleInfo info, List<CarGrade> grades)
        {
            var grade = ShopData.Grade(info, owned.Grade);
            var next = owned.Grade + 1 < grades.Count ? grades[owned.Grade + 1] : null;
            int cost = next != null ? Mathf.Max(grades[owned.Grade].UpgradeMito, 500) : 0;
            Plate(gTunePanel, "ID_GARAGE_TUNNING_PANEL_NORMAL_CARIMG", CarName(owned), GameTables.Car(owned.VehicleIndex));
            Emblem(gTunePanel, "ID_GARAGE_TUNNING_PANEL_NORMAL_CARGRADE", grade);
            Plate(gTunePanel, "ID_GARAGE_TUNNING_PANEL_TUNNING_CARIMG", next?.UiName ?? "Fully tuned", GameTables.Car(owned.VehicleIndex));
            Emblem(gTunePanel, "ID_GARAGE_TUNNING_PANEL_TUNNING_CARGRADE", next);

            var before = ShopData.Stats(info, owned);
            int[] b = { before.speed, before.accel, before.crash, before.boost };
            int[] d = next == null || grade == null ? new int[4]
                : new[] { next.Speed - grade.Speed, next.Accel - grade.Accel, next.Crash - grade.Crash, next.Boost - grade.Boost };
            int bt = 0, at = 0;
            for (int i = 0; i < 4; i++)
            {
                gTuneSlots.SetText($"ID_GARAGE_TUNNING_SLOT_BEFORE_{StatKeys[i]}", b[i].ToString());
                gTuneSlots.SetText($"ID_GARAGE_TUNNING_SLOT_AFTER_{StatKeys[i]}", next != null ? (b[i] + d[i]).ToString() : "-");
                bt += b[i]; at += b[i] + d[i];
            }
            gTuneSlots.SetText("ID_GARAGE_TUNNING_SLOT_BEFORE_TOTAL", bt.ToString());
            gTuneSlots.SetText("ID_GARAGE_TUNNING_SLOT_AFTER_TOTAL", next != null ? at.ToString() : "-");
            gTuneSlots.SetText("ID_GARAGE_TUNNING_SLOT_OWNMITO", $"{P.Mito:N0}");
            gTuneSlots.SetText("ID_GARAGE_TUNNING_SLOT_TUNNINGMITO", next != null ? $"{cost:N0}" : "-");
            PerfSlotIcons(gTuneSlots, "ID_GARAGE_TUNNING_SLOT_BEFORE_", owned);
            PerfSlotIcons(gTuneSlots, "ID_GARAGE_TUNNING_SLOT_AFTER_", owned);
        }

        // ---------- the garage.nif scene ----------
        static void BuildGarageScene()
        {
            var go = new GameObject("Garage Camera");
            var cam = go.AddComponent<Camera>();
            rig = go.AddComponent<GarageRig>();
            rig.Cam = cam;
            // fills the screen: no alpha channel, so the city behind never shows through; the screen's aspect so nothing stretches
            int width = Mathf.Clamp(Mathf.RoundToInt(768f * Screen.width / Mathf.Max(1, Screen.height)), 512, 2048);
            rig.Rt = new RenderTexture(width, 768, 24, SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RGB111110Float) ? RenderTextureFormat.RGB111110Float : RenderTextureFormat.RGB565);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.targetTexture = rig.Rt;
            // [camera0]/[camera1] of init/camera.ini: fov 60, 0.8 .. 128
            cam.fieldOfView = 60f;
            cam.nearClipPlane = 0.8f;
            cam.farClipPlane = 128f;
            cam.cullingMask = 1 << GarageLayer;
            go.AddComponent<GarageLighting>();

            if (DriftCityData.Files.Exists(@"garage\garage.nif"))
            {
                rig.Scene = new NifSceneBuilder { LogErrors = false }.Load(@"garage\garage.nif");
                rig.Scene.transform.position = GarageBase;
                SetLayer(rig.Scene, GarageLayer);
            }
            for (int i = 0; i < CarsPerFloor; i++)
            {
                var port = rig.Scene ? FindNode(rig.Scene, "port" + (i + 1)) : null;
                var p = port ? rig.Scene.transform.InverseTransformPoint(port.position) : DefaultPorts[i];
                rig.Ports[i] = new Vector3(p.x, 0f, p.z);
            }
            // the round floor and its painted port numbers turn with the parked cars
            if (rig.Scene)
                foreach (var name in new[] { "floor", "number" })
                {
                    var t = FindNode(rig.Scene, name);
                    if (t) rig.Turntable.Add((t, t.localRotation));
                }
            rig.Ring = new GameObject("Garage Turntable").transform;
            rig.Ring.position = GarageBase;
            rig.Layer = Ui.Layer("Garage View", gTop.transform.GetSiblingIndex());
            rig.Layer.texture = rig.Rt;
            rig.Layer.raycastTarget = true;   // keeps clicks off the HUD underneath
        }

        static void UpdateGarageScene()
        {
            if (!rig) return;
            if (garageMode == GarageMode.Floor)
            {
                int floor = garageSel / CarsPerFloor;
                if (rig.RingFloor != floor) BuildTurntable(floor);
                rig.Ring.gameObject.SetActive(true);
                if (rig.CenterCar) rig.CenterCar.SetActive(false);
                // the turntable brings the selected car to the front port
                rig.RingYaw = Yaw(rig.Ports[FrontPort]) - Yaw(rig.Ports[garageSel % CarsPerFloor]);
                rig.Eye = FloorEye;
                rig.Look = FloorLook;
            }
            else
            {
                if (!rig.CenterCar || rig.CenterSlot != garageSel || rig.CenterOwned != P.Cars[garageSel]) BuildCenterCar(garageSel);
                if (rig.CenterCar) rig.CenterCar.SetActive(true);
                rig.Ring.gameObject.SetActive(false);
                rig.Eye = CloseEye;
                rig.Look = CloseLook;
            }
        }

        static void BuildCenterCar(int slot)
        {
            if (rig.CenterCar) Object.Destroy(rig.CenterCar);
            var owned = P.Cars[slot];
            var info = GameTables.Car(owned.VehicleIndex);
            // facing the close-up camera with a three-quarter turn, as the dealer shows its cars
            rig.CenterCar = CarFactory.BuildDressed(info, owned, GarageBase, Quaternion.Euler(0, Yaw(CloseEye) + 30f, 0), out _);
            if (rig.CenterCar) SetLayer(rig.CenterCar, GarageLayer);
            rig.CenterSlot = slot;
            rig.CenterOwned = owned;
        }

        static void BuildTurntable(int floor)
        {
            foreach (Transform old in rig.Ring) Object.Destroy(old.gameObject);
            for (int k = 0; k < CarsPerFloor; k++)
            {
                int slot = floor * CarsPerFloor + k;
                if (slot >= P.Cars.Count) break;
                var owned = P.Cars[slot];
                var p = rig.Ports[k];
                var car = CarFactory.BuildDressed(GameTables.Car(owned.VehicleIndex), owned, rig.Ring.TransformPoint(p),
                                                  rig.Ring.rotation * Quaternion.Euler(0, Yaw(p) + 30f, 0), out _);
                if (!car) continue;
                car.transform.SetParent(rig.Ring, true);
                SetLayer(car, GarageLayer);
            }
            rig.RingFloor = floor;
        }

        /// <summary>The garage camera, scene, cars and render target; eases the camera orbit and the turntable.</summary>
        sealed class GarageRig : MonoBehaviour
        {
            public Camera Cam;
            public RenderTexture Rt;
            public RawImage Layer;
            public GameObject Scene, CenterCar;
            public OwnedCar CenterOwned;
            public Transform Ring;
            public readonly List<(Transform node, Quaternion rest)> Turntable = new List<(Transform, Quaternion)>();
            public readonly Vector3[] Ports = new Vector3[CarsPerFloor];
            public int CenterSlot = -1, RingFloor = -1;
            public Vector3 Eye = CloseEye, Look = CloseLook;
            public float Orbit, RingYaw;
            Vector3 eye, look;
            float orbit, ringYaw;
            bool placed;

            void LateUpdate()
            {
                if (!placed) { eye = Eye; look = Look; orbit = Orbit; ringYaw = RingYaw; placed = true; }
                float k = 1f - Mathf.Exp(-8f * Time.unscaledDeltaTime);
                eye = Vector3.Lerp(eye, Eye, k);
                look = Vector3.Lerp(look, Look, k);
                orbit = Mathf.LerpAngle(orbit, Orbit, k);
                ringYaw = Mathf.LerpAngle(ringYaw, RingYaw, k);
                if (Cam)
                {
                    // the camera buttons orbit around the point the camera looks at
                    Cam.transform.position = GarageBase + look + Quaternion.Euler(0, orbit, 0) * (eye - look);
                    Cam.transform.LookAt(GarageBase + look);
                }
                var turn = Quaternion.Euler(0, ringYaw, 0);
                if (Ring) Ring.rotation = turn;
                foreach (var (node, rest) in Turntable) if (node) node.localRotation = turn * rest;
            }

            void OnDestroy()
            {
                if (Scene) Destroy(Scene);
                if (CenterCar) Destroy(CenterCar);
                if (Ring) Destroy(Ring.gameObject);
                if (Layer) Destroy(Layer.gameObject);
                if (Cam) Cam.targetTexture = null;
                if (Rt) { Rt.Release(); Destroy(Rt); }
            }
        }

        // ================= driver info =================
        /// <summary>MYINFO: USERINFO 1810000, the driver's own card (DriverInfoScreen.cs, DriftCity_RE/SCREENS.md).</summary>
        public static void DriverInfo() => DriverInfoScreen.Own();

        // ================= options: OPTIONBASE / OPTIONDETAIL / OPTION 3000000 live in Options/OriginalMenus.Options.cs =================

        // ================= inventory: the INVENBASE / MYINVEN screens live in Inventory/InventoryWindow.cs =================
        public static void Inventory() => InventoryWindow.Open();

        /// <summary>Redraws the inventory after an item was used elsewhere (no-op when it isn't open).</summary>
        internal static void RefreshInventoryIfOpen() => InventoryWindow.RefreshIfOpen();
    }
}
