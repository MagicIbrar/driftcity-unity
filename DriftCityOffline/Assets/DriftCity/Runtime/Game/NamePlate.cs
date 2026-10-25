using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.Game
{
    /// <summary>
    /// The client's name plate over a car (game.nui NAME 1940000): a grey box in three pieces (BGLEFT, BG stretched to
    /// the name, BGRIGHT), the OMD team mark, the driver name and a short stem (BGDOWN) whose lower end sits on the
    /// car's position on screen. Drawn in screen space at the UI's pixel size like the client, behind the menus.
    /// </summary>
    public sealed class NamePlate : MonoBehaviour
    {
        public Transform Target;
        /// <summary>Plates of other cars fade out beyond this distance; the player's own plate always shows.</summary>
        public float MaxDistance = 80f;
        public bool Own;

        NuiView view;
        RectTransform origin;
        float stemX;
        const float StemBottom = 46f;   // BGDOWN: H (42, 24), 22 px tall

        public static NamePlate Attach(Transform target, string driver, bool own)
        {
            if (!target || !UiManager.Instance) return null;
            var plate = target.gameObject.AddComponent<NamePlate>();
            plate.Target = target;
            plate.Own = own;
            plate.Build(driver);
            return plate;
        }

        void Build(string driver)
        {
            var dialog = UiAssets.Layout.Get(1940000);
            if (dialog == null) return;
            view = NuiView.Create(dialog, UiManager.Instance.Root);
            view.name = "Name Plate " + driver;
            view.transform.SetSiblingIndex(0);   // under every dialog
            foreach (var c in view.Controls)
            {
                string id = c.Data.IdName;
                bool keep = id == "ID_NAME_BGLEFT" || id == "ID_NAME_BG" || id == "ID_NAME_BGRIGHT" || id == "ID_NAME_NAME" || id == "ID_NAME_BGDOWN" || id == "ID_NAME_OMD";
                c.gameObject.SetActive(keep);
                foreach (var g in c.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
            }
            foreach (var g in view.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
            view.SetText("ID_NAME_NAME", driver);
            origin = (RectTransform)view.transform.GetChild(0);
            origin.anchorMin = origin.anchorMax = new Vector2(0.5f, 0.5f);

            // stretch the box to the name: text starts right of the team mark
            var name = view.Control("ID_NAME_NAME");
            float textW = 60f;
            if (name)
            {
                var t = name.GetComponentInChildren<Text>(true);
                if (t) { t.alignment = TextAnchor.MiddleLeft; t.horizontalOverflow = HorizontalWrapMode.Overflow; textW = t.preferredWidth; }
                var rt = (RectTransform)name.transform;
                rt.anchoredPosition = new Vector2(38f, rt.anchoredPosition.y);
                foreach (var r in name.GetComponentsInChildren<RectTransform>(true)) r.sizeDelta = new Vector2(textW + 4f, r.sizeDelta.y);
            }
            float right = Mathf.Max(106f, 38f + textW + 6f);
            var bg = view.Control("ID_NAME_BG");
            if (bg) foreach (var r in bg.GetComponentsInChildren<RectTransform>(true)) r.sizeDelta = new Vector2(right - 16f + 1f, r.sizeDelta.y);
            var bgRight = view.Control("ID_NAME_BGRIGHT");
            if (bgRight) { var rt = (RectTransform)bgRight.transform; rt.anchoredPosition = new Vector2(right, rt.anchoredPosition.y); }
            stemX = (right + 9f) * 0.5f;
            var stem = view.Control("ID_NAME_BGDOWN");
            if (stem) { var rt = (RectTransform)stem.transform; rt.anchoredPosition = new Vector2(stemX - 1f, rt.anchoredPosition.y); }
        }

        void LateUpdate()
        {
            if (!view) return;
            var cam = Camera.main;
            bool show = cam && Target && Target.gameObject.activeInHierarchy;
            Vector3 sp = default;
            if (show)
            {
                // the stem points at the ground behind the car: in the client's footage the plate sits on the rear bumper
                if (!measured) { anchor = RearGround(Target); measured = true; }
                sp = cam.WorldToScreenPoint(Target.TransformPoint(anchor));
                show = sp.z > 0.5f && (Own || sp.z < MaxDistance);
            }
            if (view.gameObject.activeSelf != show) view.gameObject.SetActive(show);
            if (!show) return;
            var canvas = (RectTransform)UiManager.Instance.Root;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(canvas, sp, null, out var local)) return;
            // the stem's lower end on the car
            origin.anchoredPosition = new Vector2(Mathf.Round(local.x - stemX), Mathf.Round(local.y + StemBottom));
        }

        Vector3 anchor;
        bool measured;

        /// <summary>The bottom of the car's rear end in its own space (from the visible body, wheels included).</summary>
        static Vector3 RearGround(Transform car)
        {
            float low = float.MaxValue, rear = float.MaxValue;
            foreach (var r in car.GetComponentsInChildren<Renderer>())
            {
                if (r is ParticleSystemRenderer || !r.enabled) continue;
                var c = car.InverseTransformPoint(r.bounds.center);
                low = Mathf.Min(low, c.y - r.bounds.extents.y);
                rear = Mathf.Min(rear, c.z - r.bounds.extents.z);
            }
            return low == float.MaxValue ? Vector3.zero : new Vector3(0f, low, rear);
        }

        void OnDestroy() { if (view) Destroy(view.gameObject); }
    }
}
