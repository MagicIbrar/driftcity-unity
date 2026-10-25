using System;
using System.Collections.Generic;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// TOUR_REWARD 4510000's GM controls (DriftCity_RE/PVP.md 14.7 / 15.3): 0x58e430 shows the GM panel (GM_BACK1-3,
    /// GM_ITEMNAME, GM_LEFTBTN / GM_RIGHTBTN, GM_APPLY; 0x58c370(1)) only when the character's authority byte +0x7c is
    /// below 'N'; APPLY sends 0xd52 {int, int} and 0xd53 answers; 0xd54 {channel} -> 0xd55 {type id, 16 names} carries the
    /// reward type the table shows. The remake reads the pair as {class, reward type}: the GM picks which of the class's
    /// four reward tables (rewardgroup.xlt rw_gtc_&lt;class&gt;_&lt;a..d&gt;_&lt;bracket&gt;) the GT Championship pays [inferred from
    /// the table names]. Online only an admin account sees the panel (race.gm check) and the server keeps the choice
    /// (race.js tourSets, used by src/pvptour.js); offline the panel needs the GM switch (<see cref="ForceGm"/>, env DC_GM=1).
    /// </summary>
    public static class PvpTourRewardGm
    {
        public static readonly string[] Letters = { "a", "b", "c", "d" };
        static readonly int[] sets = new int[4];
        static int pending = -1, shownClass;
        static bool? admin;
        static Action refresh;
        static NuiView view;

        /// <summary>Offline GM switch (tests; env DC_GM=1).</summary>
        public static bool ForceGm = Environment.GetEnvironmentVariable("DC_GM") == "1";
        public static bool IsGm => OnlineSession.Active ? admin == true : ForceGm;
        public static int SetOf(int cls) => sets[Mathf.Clamp(cls, 0, 3)];
        public static string Letter(int cls) => Letters[SetOf(cls)];

        /// <summary>Called by TOUR_REWARD's open (Tournament.OpenRewards): shows or hides the GM panel.</summary>
        public static void Attach(NuiView rewards, int cls, Action redraw)
        {
            view = rewards; shownClass = Mathf.Clamp(cls, 0, 3); refresh = redraw;
            pending = -1;
            Hook();
            if (OnlineSession.Active && admin == null)
            {
                OnlineSession.Instance.Net.Request("race.gm", new Dictionary<string, object> { ["op"] = "check" }, r =>
                {
                    admin = r.B("admin");
                    ReadSets(r);
                    if (view) { Draw(); refresh?.Invoke(); }
                });
            }
            Draw();
        }

        static UiManager hooked;
        static void Hook()
        {
            var ui = UiManager.Instance;
            if (hooked == ui || !ui) return;
            hooked = ui;
            ui.On("ID_TOUR_REWARD_GM_LEFTBTN", _ => Cycle(-1));
            ui.On("ID_TOUR_REWARD_GM_RIGHTBTN", _ => Cycle(1));
            ui.On("ID_TOUR_REWARD_GM_APPLY", _ => Apply());
        }

        static void ReadSets(Dictionary<string, object> r)
        {
            var l = r.L("tourSets");
            if (l == null) return;
            for (int i = 0; i < 4 && i < l.Count; i++) sets[i] = Mathf.Clamp(Convert.ToInt32(l[i]), 0, 3);
        }

        static void Draw()
        {
            if (!view) return;
            bool gm = IsGm;
            foreach (var ctl in view.GetComponentsInChildren<NuiControlView>(true))
                if ((ctl.Data.IdName ?? "").Contains("_GM_")) ctl.gameObject.SetActive(gm);
            if (!gm) return;
            int s = pending >= 0 ? pending : SetOf(shownClass);
            view.SetText("ID_TOUR_REWARD_GM_ITEMNAME", $"Reward type {Letters[s].ToUpperInvariant()}" + (pending >= 0 && pending != SetOf(shownClass) ? " *" : ""));
        }

        public static void Cycle(int dir)
        {
            int s = pending >= 0 ? pending : SetOf(shownClass);
            pending = ((s + dir) % 4 + 4) % 4;
            Draw();
        }

        /// <summary>GM_APPLY (0xd52): the shown class pays the chosen reward table.</summary>
        public static void Apply()
        {
            if (!IsGm || pending < 0) return;
            int cls = shownClass, set = pending;
            if (OnlineSession.Active)
            {
                OnlineSession.Instance.Net.Request("race.gm", new Dictionary<string, object> { ["op"] = "tourReward", ["class"] = cls, ["set"] = set }, r =>
                {
                    if (!r.B("ok")) { UiPopup.Message(MissionText.Get("10745")); return; }
                    ReadSets(r);
                    pending = -1; Draw(); refresh?.Invoke();
                });
                return;
            }
            sets[cls] = set;
            pending = -1;
            Draw();
            refresh?.Invoke();
        }

        /// <summary>Logout / tests.</summary>
        public static void Reset() { admin = null; pending = -1; for (int i = 0; i < 4; i++) sets[i] = 0; }
    }
}
