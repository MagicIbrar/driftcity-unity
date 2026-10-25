using System.Collections.Generic;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.Game
{
    /// <summary>
    /// Chat balloons over the cars, BALLOON 3300000 (DriftCity_RE/OPTIONS.md): a nine-piece frame (LT T RT / L BG R /
    /// LB BL B BR RB, 9 px corners tinted 8CD8DC, B the 26x24 tail) around the text (Trebuchet 12, black) with the
    /// driver's name above it (Trebuchet 14 bold, white with a black outline). Shown while goption.ini
    /// ShowDialogBallon is on (OPTION GAME's balloon switch). One balloon per car; a new line replaces the old one.
    /// </summary>
    public sealed class ChatBalloon : MonoBehaviour
    {
        public const int Dialog = 3300000;
        /// <summary>Seconds a balloon stays (remake value; the client's timer isn't in the notes).</summary>
        public static float Seconds = 6f;
        const float MaxTextWidth = 200f, Corner = 9f, TailW = 26f, TailH = 24f;

        static readonly Dictionary<Transform, ChatBalloon> live = new Dictionary<Transform, ChatBalloon>();

        Transform target;
        NuiView view;
        RectTransform origin;
        float until;
        Vector2 size;   // whole balloon including the tail

        public static int Count { get { int n = 0; foreach (var kv in live) if (kv.Value) n++; return n; } }
        public NuiView View => view;

        /// <summary>Shows "name: text" over a car (the chat hook calls this for the speaker's car).</summary>
        public static ChatBalloon Say(Transform car, string name, string text, float seconds = -1f)
        {
            if (!car || string.IsNullOrEmpty(text) || !GameOptions.ShowDialogBallon || !UiManager.Instance) return null;
            if (live.TryGetValue(car, out var old) && old) Destroy(old.gameObject);
            var go = new GameObject("Chat Balloon " + name);
            var b = go.AddComponent<ChatBalloon>();
            b.target = car;
            b.until = Time.unscaledTime + (seconds > 0 ? seconds : Seconds);
            if (!b.Build(name, text)) { Destroy(go); return null; }
            live[car] = b;
            return b;
        }

        /// <summary>
        /// Online chat in the normal scope becomes a balloon over the speaker's car (a remote car by driver name, or the
        /// player's own car for the server's echo of the player's line). Listens to OnlineSession.ChatReceived.
        /// </summary>
        static void OnChat(string scope, string from, string text)
        {
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(text)) return;
            var sc = (scope ?? "").ToLowerInvariant();
            if (sc != "" && sc != "normal" && sc != "chat" && sc != "channel") return;
            Transform car = null;
            var me = PlayerProfile.Current;
            var flow = GameFlow.Instance;
            if (me != null && string.Equals(me.Name, from, System.StringComparison.OrdinalIgnoreCase)) car = flow && flow.PlayerCar ? flow.PlayerCar.transform : null;
            else
            {
                var session = Net.OnlineSession.Instance;
                if (session && session.Remotes)
                    foreach (var rc in session.Remotes.All)
                        if (rc && string.Equals(rc.DriverName, from, System.StringComparison.OrdinalIgnoreCase)) { car = rc.transform; break; }
            }
            if (car) Say(car, from, text);
        }

        static Net.OnlineSession hooked;

        /// <summary>Keeps the chat listener on the current online session.</summary>
        sealed class Listener : MonoBehaviour
        {
            float next;
            void Update()
            {
                if (Time.unscaledTime < next) return;
                next = Time.unscaledTime + 1f;
                var s = Net.OnlineSession.Instance;
                if (s == hooked) return;
                if (hooked) hooked.ChatReceived -= OnChat;
                hooked = s;
                if (s) s.ChatReceived += OnChat;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            hooked = null;
            var go = new GameObject("Drift City Chat Balloons");
            DontDestroyOnLoad(go);
            go.AddComponent<Listener>();
        }

        public static void ClearAll()
        {
            foreach (var kv in live) if (kv.Value) Destroy(kv.Value.gameObject);
            live.Clear();
        }

        bool Build(string name, string text)
        {
            view = UiManager.Instance.Open(Dialog);
            if (!view) return false;
            view.transform.SetAsFirstSibling();   // under the HUD and the windows
            foreach (var g in view.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
            origin = view.transform.Find("origin") as RectTransform;

            // the text wraps at MaxTextWidth; the frame grows to fit it
            var tc = view.Control("ID_BALLOON_TEXT");
            view.SetText("ID_BALLOON_TEXT", text);
            var label = tc ? tc.GetComponentInChildren<Text>(true) : null;
            float w = 40f, h = 16f;
            if (label)
            {
                label.horizontalOverflow = HorizontalWrapMode.Wrap;
                label.verticalOverflow = VerticalWrapMode.Overflow;
                label.alignment = TextAnchor.UpperCenter;
                w = Mathf.Clamp(label.cachedTextGeneratorForLayout.GetPreferredWidth(text, label.GetGenerationSettings(Vector2.zero)) / label.pixelsPerUnit, 24f, MaxTextWidth);
                var settings = label.GetGenerationSettings(new Vector2(w, 0));
                h = Mathf.Max(14f, label.cachedTextGeneratorForLayout.GetPreferredHeight(text, settings) / label.pixelsPerUnit);
            }
            float inner = Mathf.Max(w + 4f, TailW + 8f);
            Place("ID_BALLOON_LT", 0, 0, Corner, Corner);
            Place("ID_BALLOON_T", Corner, 0, inner, Corner);
            Place("ID_BALLOON_RT", Corner + inner, 0, Corner, Corner);
            Place("ID_BALLOON_L", 0, Corner, Corner, h);
            Place("ID_BALLOON_BG", Corner, Corner, inner, h);
            Place("ID_BALLOON_R", Corner + inner, Corner, Corner, h);
            Place("ID_BALLOON_LB", 0, Corner + h, Corner, Corner);
            float tailX = Corner + (inner - TailW) * 0.5f;
            Place("ID_BALLOON_BL", Corner, Corner + h, tailX - Corner, Corner);
            Place("ID_BALLOON_B", tailX, Corner + h, TailW, TailH);
            Place("ID_BALLOON_BR", tailX + TailW, Corner + h, Corner + inner - tailX - TailW, Corner);
            Place("ID_BALLOON_RB", Corner + inner, Corner + h, Corner, Corner);
            Place("ID_BALLOON_TEXT", Corner + (inner - w) * 0.5f, Corner, w, h);
            float nameW = Mathf.Max(122f, inner + 2 * Corner);
            Place("ID_BALLOON_NAME", (inner + 2 * Corner - nameW) * 0.5f, -17f, nameW, 16f);
            view.SetText("ID_BALLOON_NAME", name ?? "");
            size = new Vector2(inner + 2 * Corner, Corner + h + TailH);
            Follow();
            return true;
        }

        /// <summary>Moves a piece to (x, y) in balloon pixels and stretches its image / text to w x h.</summary>
        void Place(string id, float x, float y, float w, float h)
        {
            var c = view.Control(id);
            if (!c) return;
            var rt = (RectTransform)c.transform;
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
            foreach (Transform child in rt)
                if (child is RectTransform e) { e.anchoredPosition = Vector2.zero; e.sizeDelta = new Vector2(w, h); }
        }

        void Follow()
        {
            var cam = Camera.main;
            if (!cam || !target || !origin) { if (view) view.gameObject.SetActive(false); return; }
            var world = target.position + Vector3.up * 2.4f;
            var sp = cam.WorldToScreenPoint(world);
            bool show = sp.z > 0.5f && sp.z < 120f;
            view.gameObject.SetActive(show);
            if (!show) return;
            // canvas units: the UI scales to 768 px high (CanvasScaler match height); the tail's tip sits on the car
            float k = 768f / Mathf.Max(1, Screen.height);
            origin.anchoredPosition = new Vector2(sp.x * k - size.x * 0.5f, -((Screen.height - sp.y) * k - size.y));
        }

        void LateUpdate()
        {
            if (!target || Time.unscaledTime > until || !GameOptions.ShowDialogBallon) { Destroy(gameObject); return; }
            Follow();
        }

        void OnDestroy()
        {
            if (view && UiManager.Instance) UiManager.Instance.Close(view);
            if (target && live.TryGetValue(target, out var b) && b == this) live.Remove(target);
        }
    }
}
