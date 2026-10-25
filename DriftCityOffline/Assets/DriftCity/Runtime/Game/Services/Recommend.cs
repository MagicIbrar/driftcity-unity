using System.Collections.Generic;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game.Services
{
    /// <summary>
    /// Recommend-a-driver (DriftCity_RE/SERVICES.md section 5; server src/drivers.js).
    /// Client [exact]: op 0x097 sets net+0x3b518; entering a city (0x74ca28 / 0x76dbf9, with net+0x3b500 == 1) opens MSGBOX
    /// 3230000 (0x554240) with 10181 "Name of the recommended character."; OK sends the name; op 0x099 answers 1 -> POPUP
    /// 10042 "Recommended!", the prompt closes and the flag clears; another answer -> 10043 (an empty text), the prompt stays.
    /// Remake rules: the server's recommend.state decides (switch <c>recommend</c>, level 10 or below, nobody named yet);
    /// asked on the first city of a session, as soon as no other prompt, popup or menu is up (within a minute); Cancel asks
    /// again next session; offline nothing is asked.
    /// </summary>
    public static class Recommend
    {
        static bool askedThisSession, pending;
        static float pendingSince;
        static NetClient sessionNet;
        public static bool Prompting { get; private set; }

        /// <summary>ServicesHost: a city was entered.</summary>
        public static void OnCityEntered()
        {
            if (!ServicesNet.Online) return;
            var net = OnlineSession.Instance ? OnlineSession.Instance.Net : null;
            if (net != sessionNet) { sessionNet = net; askedThisSession = false; }
            if (askedThisSession || PlayerProfile.Current == null || PlayerProfile.Current.RecommendDone) return;
            askedThisSession = true;
            ServicesNet.Request("recommend.state", null, r => { if (r != null && r.B("ask")) { pending = true; pendingSince = Time.unscaledTime; } });
        }

        /// <summary>ServicesHost, once a second: opens the waiting prompt when nothing else asks the player.</summary>
        public static void Tick()
        {
            if (!pending) return;
            if (!ServicesNet.Online || Time.unscaledTime - pendingSince > 60f) { pending = false; return; }
            var flow = GameFlow.Instance;
            if (MsgBox.IsOpen || UiPopup.IsOpen || ListWindow.IsOpen || (flow && (flow.MenuOpen || !flow.InCity))) return;
            pending = false;
            Ask();
        }

        /// <summary>MSGBOX with 10181; OK sends the name (recommend.send).</summary>
        public static void Ask()
        {
            Prompting = true;
            MsgBox.Prompt(MissionText.Get("10181"), Send, "", MsgBox.Confirm.Ok, 16, () => Prompting = false);
        }

        public static void Send(string name)
        {
            ServicesNet.Request("recommend.send", new Dictionary<string, object> { ["name"] = name }, r =>
            {
                if (r != null && r.B("ok")) { Prompting = false; UiPopup.Message(MissionText.Get("10042")); return; }
                // 0x099 with another answer: 10043 (empty in the English client: the server's reason instead); ask again
                UiPopup.Message(ServicesNet.Text(r), Ask);
            });
        }

        /// <summary>Tests: ask again in this session.</summary>
        public static void Reset() { askedThisSession = false; pending = false; Prompting = false; }
    }
}
