using System.Collections.Generic;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.Game
{
    /// <summary>
    /// The in-race PvP HUD (PVP.md section 7): RANK 1380000 (my position / field and the driver list), LAPS 1390000,
    /// RACETIME 1400000, BESTTIME 1410000 and the goal-in banner 1640000. Digits are the dialogs' own glyph controls: the
    /// rank and lap digits are one control per glyph at its screen position (one is shown), the times copy the glyph's
    /// image into the MIN/SEC/MS placeholders. Used by the offline race (RaceMission) and the online race (OnlineRace).
    /// </summary>
    public sealed class PvpHud
    {
        NuiView rank, laps, time, best, goal;
        float goalHide;
        readonly List<string> lastNames = new List<string>();

        static NuiView roundView;
        public static NuiView RoundView => roundView;

        /// <summary>
        /// ROUNDNUMBER 1420000 (object vtable 0x87412c: binder 0x673aa0 takes 1420002..1420004 = ROUNDNUMBER1..3, 0x673af0
        /// shows the one of the round, 0x673b40 keeps it on the room's round (room info +0x74) while the race stage runs):
        /// the Channel Battle round of the race, 1 Round 1, 2 Round 2, 3 the Final. 0 or below closes it.
        /// </summary>
        public static void ShowRound(int round)
        {
            var ui = UiManager.Instance;
            if (round <= 0 || round > 3 || !ui) { HideRound(); return; }
            if (!roundView) roundView = ui.Open(1420000);
            if (!roundView) return;
            for (int i = 1; i <= 3; i++) roundView.SetVisible("ID_TC_ROUNDNUMBER" + i, i == round);
        }

        public static void HideRound()
        {
            if (roundView) { if (UiManager.Instance) UiManager.Instance.Close(roundView); else Object.Destroy(roundView.gameObject); }
            roundView = null;
        }

        public static PvpHud Open()
        {
            var ui = UiManager.Instance;
            if (!ui) return null;
            var h = new PvpHud
            {
                rank = ui.Open(1380000), laps = ui.Open(1390000), time = ui.Open(1400000), best = ui.Open(1410000),
            };
            h.Prepare();
            return h;
        }

        void Prepare()
        {
            // the rank list's unnamed placeholder rows (designer text) and every glyph start hidden; Set* reveals what it uses
            Pvp.HideUnnamedTexts(rank);
            foreach (var v in new[] { rank, laps, time, best })
            {
                if (!v) continue;
                foreach (var c in v.GetComponentsInChildren<NuiControlView>(true))
                {
                    string id = c.Data.IdName ?? "";
                    if (id.Contains("_NUM") || id.Contains("1ST_") || id.Contains("2ND_") || id.Contains("_ON") || id.Contains("_OFF") ||
                        id.Contains("CHKSPEC") || id.Contains("MYICON") || id.Contains("_SKIP") || id.Contains("TOGO") || id.Contains("TOTAL"))
                        c.gameObject.SetActive(false);
                }
            }
            Pvp.Show(rank ? rank.Control("ID_PVP_RANK_POS") : null, true);
            Pvp.Show(rank ? rank.Control("ID_PVP_RANK_SLASH") : null, true);
            foreach (var id in new[] { "ID_PVP_RACETIME", "ID_PVP_RACETIME_MINUTE", "ID_PVP_RACETIME_SECOND", "ID_PVP_RACETIME_MIN2ND", "ID_PVP_RACETIME_SEC1ST",
                                        "ID_PVP_RACETIME_SEC2ND", "ID_PVP_RACETIME_MS1ST", "ID_PVP_RACETIME_MS2ND", "ID_PVP_RACETIME_MS3TH" })
                Pvp.Show(time ? time.Control(id) : null, true);
            Pvp.Show(time ? time.Control("ID_PVP_RACETIME_MIN1ST") : null, false);
            foreach (var id in new[] { "ID_PVP_BESTTIME", "ID_PVP_BESTTIME_MINUTE", "ID_PVP_BESTTIME_SECOND", "ID_PVP_BESTTIME_MIN2ND", "ID_PVP_BESTTIME_SEC1ST",
                                        "ID_PVP_BESTTIME_SEC2ND", "ID_PVP_BESTTIME_MS1ST", "ID_PVP_BESTTIME_MS2ND", "ID_PVP_BESTTIME_MS3TH" })
                Pvp.Show(best ? best.Control(id) : null, true);
            Pvp.Show(best ? best.Control("ID_PVP_BESTTIME_MIN1ST") : null, false);
        }

        public bool IsOpen => rank;

        /// <summary>My place of the field, the lap I'm on, race time and best lap.</summary>
        public void Set(int place, int field, int lap, int totalLaps, float raceTime, float bestLap)
        {
            TwoDigits(rank, "ID_PVP_RANK_MY1ST_", "ID_PVP_RANK_MY2ND_", place);
            TwoDigits(rank, "ID_PVP_RANK_ALL1ST_", "ID_PVP_RANK_ALL2ND_", field);
            // laps: the 1ST glyph sets sit off-screen in the layout (single digit laps), the 2ND sets are drawn
            OneDigit(laps, "ID_PVP_LAPS_TOGO2ND_", Mathf.Clamp(lap, 0, 9));
            OneDigit(laps, "ID_PVP_LAPS_TOTAL2ND_", Mathf.Clamp(totalLaps, 0, 9));
            Clock(time, "ID_PVP_RACETIME", raceTime);
            Clock(best, "ID_PVP_BESTTIME", bestLap);
        }

        /// <summary>The driver list in race order: "name(progress%)"; my row uses the highlighted "ON" label and MYICON.</summary>
        public void SetOrder(IList<(string name, int percent, bool me)> rows)
        {
            if (!rank) return;
            for (int i = 0; i < 16; i++)
            {
                bool used = i < rows.Count;
                var on = i < 8 ? rank.Control("ID_PVP_RANK_ID_ON" + (i + 1)) : rank.Control("ID_PVP_RANK_ID_CHKSPEC" + (i + 1));
                var off = i < 8 ? rank.Control("ID_PVP_RANK_ID_OFF" + (i + 1)) : null;
                var icon = rank.Control($"ID_PVP_RANK_MYICON{i:00}");
                bool me = used && rows[i].me;
                string text = used ? $"{rows[i].name}({Mathf.Clamp(rows[i].percent, 0, 100)}%)" : "";
                if (off)
                {
                    Pvp.Show(on, me); Pvp.Show(off, used && !me);
                    if (me) on.SetText(text); else if (used) off.SetText(text);
                }
                else { Pvp.Show(on, used); if (used) on.SetText(text); }
                Pvp.Show(icon, me);
            }
        }

        /// <summary>Goal-in banner (1640000) for another driver crossing the line: rank + name for 2 s.</summary>
        public void GoalIn(int place, string name)
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            if (!goal) goal = ui.Open(1640000);
            if (!goal) return;
            goal.gameObject.SetActive(true);
            Pvp.Show(goal.Control("ID_PVP_GOALIN_1"), place == 1);
            Pvp.Show(goal.Control("ID_PVP_GOALIN_ST"), place == 1);
            goal.SetText("ID_PVP_GOALIN_ID", place == 1 ? name : $"{Pvp.Ordinal(place)}  {name}");
            goalHide = Time.time + 2f;
        }

        public void Tick()
        {
            if (goal && goal.gameObject.activeSelf && Time.time > goalHide) goal.gameObject.SetActive(false);
        }

        public void Close()
        {
            var ui = UiManager.Instance;
            foreach (var v in new[] { rank, laps, time, best, goal }) if (v) { if (ui) ui.Close(v); else Object.Destroy(v.gameObject); }
            rank = laps = time = best = goal = null;
        }

        // ---------------- glyphs ----------------
        static void OneDigit(NuiView v, string prefix, int digit)
        {
            if (!v) return;
            for (int d = 0; d <= 9; d++) Pvp.Show(v.Control(prefix + d), d == digit);
        }

        /// <summary>Tens (1ST, glyphs 1..9 where present) and units (2ND) of a number up to 99.</summary>
        static void TwoDigits(NuiView v, string tens, string units, int n)
        {
            if (!v) return;
            n = Mathf.Clamp(n, 0, 99);
            for (int d = 0; d <= 9; d++) Pvp.Show(v.Control(tens + d), n >= 10 && d == n / 10);
            OneDigit(v, units, n % 10);
        }

        static RawImage Image(NuiView v, string id)
        {
            var c = v ? v.Control(id) : null;
            return c ? c.GetComponentInChildren<RawImage>(true) : null;
        }

        /// <summary>M'SS"mmm into the MIN2ND / SEC1ST / SEC2ND / MS1ST..MS3TH placeholders from the NUM0..9 glyphs.</summary>
        static void Clock(NuiView v, string prefix, float seconds)
        {
            if (!v) return;
            int ms = Mathf.Clamp(Mathf.FloorToInt(Mathf.Max(0f, seconds) * 1000f), 0, 9 * 60000 + 59999);
            int m = ms / 60000, s = ms / 1000 % 60, f = ms % 1000;
            var cells = new (string id, int digit)[]
            {
                ("_MIN2ND", m), ("_SEC1ST", s / 10), ("_SEC2ND", s % 10), ("_MS1ST", f / 100), ("_MS2ND", f / 10 % 10), ("_MS3TH", f % 10),
            };
            foreach (var (id, digit) in cells)
            {
                var dst = Image(v, prefix + id);
                var src = Image(v, prefix + "_NUM" + digit);
                if (!dst || !src) continue;
                dst.texture = src.texture; dst.uvRect = src.uvRect; dst.color = src.color; dst.enabled = src.texture;
            }
        }
    }
}
