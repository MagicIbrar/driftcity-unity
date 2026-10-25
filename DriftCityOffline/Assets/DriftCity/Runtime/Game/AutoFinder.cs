using System.Collections.Generic;
using System.Linq;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// AUTOFINDER 3420000, the server's anti-macro quiz (op 0x51e; init 0x681050, show 0x681150, clock 0x680f50, answer
    /// 0x680ea0 -> 0x799cf0; DriftCity_RE/SCREENS.md 5 / 8.4) [exact for the screen]: the server's question from
    /// init\autofinder.xlt, a random base pattern (BASE_PATTERN1ST-4TH), the four answers shuffled over the buttons, the box
    /// at a random spot, a 30 s clock "%d %s" (10555 "Sec.", closes at 0); a pick goes to the server, 10556 "Correct." /
    /// 10557 "Wrong.". The server side is off by default (DriftCityServer/src/smallscreens.js, env DC_AUTOFINDER=1): this
    /// only shows what the server asks (autofinder.ask {id, question, answers, seconds}); offline nothing opens.
    /// </summary>
    public static class AutoFinder
    {
        public const int Dialog = 3420000;
        const string P = "ID_AUTOFINDER_";
        static readonly string[] Nth = { "1ST", "2ND", "3RD", "4TH" };
        static NuiView view;
        static UiManager hooked;
        static int askId;
        static float until;
        static readonly int[] order = { 0, 1, 2, 3 };   // button k shows answer order[k]
        static NetClient subscribed;

        public static NuiView View => view;
        public static string Question { get; private set; }
        public static IReadOnlyList<int> Order => order;
        public static string LastText { get; private set; }

        sealed class Runner : MonoBehaviour { void Update() => Tick(); }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Ensure()
        {
            if (Object.FindAnyObjectByType<Runner>()) return;
            var go = new GameObject("AutoFinder");
            go.AddComponent<Runner>();
            if (Application.isPlaying) Object.DontDestroyOnLoad(go);
        }

        static void Tick()
        {
            // subscribe to the session's pushes (a new NetClient after a reconnect)
            var net = OnlineSession.Instance ? OnlineSession.Instance.Net : null;
            if (net != subscribed)
            {
                if (subscribed) subscribed.Message -= OnMessage;
                subscribed = net;
                if (net) net.Message += OnMessage;
            }
            if (!view) return;
            float left = until - Time.unscaledTime;
            view.SetText(P + "TIME", $"{Mathf.Max(0, Mathf.CeilToInt(left))} {MissionText.Get("10555")}");
            if (left <= 0f) Close();   // the server counts the miss
        }

        static void OnMessage(string t, Dictionary<string, object> m)
        {
            if (t == "autofinder.ask") Ask(m.I("id"), m.S("question"), (m.L("answers") ?? new List<object>()).Select(x => x?.ToString() ?? "").ToList(), m.I("seconds") > 0 ? m.I("seconds") : 30);
            else if (t == "autofinder.result") { LastText = MissionText.Get(m.B("right") ? "10556" : "10557"); ChatBox.System(LastText); Close(); }
        }

        /// <summary>Shows a question (0x681150): pattern, shuffled answers, a random spot, the clock.</summary>
        public static void Ask(int id, string question, List<string> answers, int seconds = 30)
        {
            var ui = UiManager.Instance;
            if (!ui || answers == null || answers.Count < 4) return;
            Hook(ui);
            if (!view) view = ui.Open(Dialog);
            if (!view) return;
            askId = id; Question = question;
            until = Time.unscaledTime + seconds;
            int pattern = Random.Range(0, 4);
            for (int i = 0; i < 4; i++) view.SetVisible(P + "BASE_PATTERN" + Nth[i], i == pattern);
            for (int i = 0; i < 4; i++) order[i] = i;
            for (int i = 3; i > 0; i--) { int j = Random.Range(0, i + 1); (order[i], order[j]) = (order[j], order[i]); }
            view.SetText(P + "QUESTION", question);
            for (int k = 0; k < 4; k++) view.SetText(P + "ANSWER" + Nth[k], $"{k + 1}. {answers[order[k]]}");
            // the box at a random spot on the screen (0x681150)
            var rt = (RectTransform)view.transform;
            rt.anchoredPosition = new Vector2(Random.Range(-220f, 220f), Random.Range(-150f, 150f));
            AudioLibrary.PlaySfx("sfx/SID_SYSWIN_CREATE");
        }

        static void Hook(UiManager ui)
        {
            if (hooked == ui) return;
            hooked = ui;
            for (int k = 0; k < 4; k++) { int b = k; ui.On(P + "BTN" + Nth[k], _ => Pick(b)); }
        }

        /// <summary>A button: its answer (in the server's order) goes to the server (0x799cf0).</summary>
        public static void Pick(int button)
        {
            if (!view) return;
            int choice = order[Mathf.Clamp(button, 0, 3)];
            int id = askId;
            Close();
            if (!OnlineSession.Active) return;
            OnlineSession.Instance.Net.Request("autofinder.answer", new Dictionary<string, object> { ["id"] = id, ["choice"] = choice }, r =>
            {
                LastText = MissionText.Get(r.B("right") ? "10556" : "10557");
                ChatBox.System(LastText);
            });
        }

        static void Close()
        {
            var ui = UiManager.Instance;
            if (view) { if (ui) ui.Close(view); else Object.Destroy(view.gameObject); }
            view = null;
        }
    }
}
