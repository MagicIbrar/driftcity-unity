using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.Game
{
    /// <summary>
    /// The race results (PVP.md section 8), driven like the client's 13 s result timer: 3 s after the results the single
    /// race shows RESULT16 1470000 (team: OURRESULT16 1540000 + TEAMPOINT 1630000, switched to the WIN/LOSE screen 1550000
    /// at 8 s), and at 13 s everything closes and the race returns to the wait room (the callback).
    /// </summary>
    public static class PvpResults
    {
        public static float ShowAt = 3f, TeamSwapAt = 8f, CloseAt = 13f;

        static NuiView main, teamPoint, teamFinal;
        static PvpRaceResult current;
        static Runner runner;

        public static bool IsOpen => runner;
        public static PvpRaceResult Current => current;
        public static NuiView View => main ? main : teamFinal;

        sealed class Runner : MonoBehaviour { }

        public static void Show(PvpRaceResult result, Action done)
        {
            Close();
            current = result;
            runner = new GameObject("PvP Results").AddComponent<Runner>();
            runner.StartCoroutine(Run(result, done));
        }

        static IEnumerator Run(PvpRaceResult r, Action done)
        {
            float t0 = Time.time;
            while (Time.time - t0 < ShowAt) yield return null;
            var ui = UiManager.Instance;
            if (ui && r.Wager > 0)
            {
                // wager rooms: RESULT_SINGLEBB 4080000 / RESULT_TEAMBB 4090000 (0x724120 with room flags 0x7000)
                if (GameEffects.Sounds) AudioLibrary.PlaySfx("sfx/SID_PVP_RESULT");
                main = PvpWager.Open(r);
                while (Time.time - t0 < CloseAt) yield return null;
                Close();
                done?.Invoke();
                yield break;
            }
            if (ui)
            {
                if (GameEffects.Sounds) AudioLibrary.PlaySfx(r.Team ? "sfx/SID_PVP_TEAMPOINT" : "sfx/SID_PVP_RESULT");
                if (r.Team) { CloseBonuses(); main = ui.Open(1540000); teamPoint = ui.Open(1630000); }
                else main = ui.Open(1470000);
                Refresh();
            }
            if (r.Team)
            {
                while (Time.time - t0 < TeamSwapAt) yield return null;
                if (ui)
                {
                    CloseView(ref main); CloseView(ref teamPoint); CloseBonuses();
                    teamFinal = ui.Open(1550000);
                    var mine = r.Mine;
                    bool won = mine != null && (r.Winner == mine.Team || r.Winner == 2);
                    if (GameEffects.Sounds) AudioLibrary.PlaySfx(won ? "sfx/SID_PVP_RESULT_TEAMWIN" : "sfx/SID_PVP_RESULT_TEAMLOSE");
                    Refresh();
                }
            }
            while (Time.time - t0 < CloseAt) yield return null;
            Close();
            done?.Invoke();
        }

        /// <summary>Re-draws the open result screen (the reward reply may arrive after it opened).</summary>
        public static void Refresh()
        {
            if (current == null) return;
            if (main && main.Dialog.Id == 1470000) { Single(main, current); Bonuses(current, false); }
            if (main && main.Dialog.Id == 1540000) { TeamList(main, current); Bonuses(current, true); }
            if (!main) CloseBonuses();
            if (teamPoint) TeamPoints(teamPoint, current);
            if (teamFinal) TeamFinal(teamFinal, current);
        }

        public static void Close()
        {
            CloseBonuses();
            goldenShown = false;
            CloseView(ref main); CloseView(ref teamPoint); CloseView(ref teamFinal);
            if (runner) UnityEngine.Object.Destroy(runner.gameObject);
            runner = null;
        }

        static void CloseView(ref NuiView v)
        {
            if (v) { if (UiManager.Instance) UiManager.Instance.Close(v); else UnityEngine.Object.Destroy(v.gameObject); }
            v = null;
        }

        // ---------------- bonus icons (0x588100 / 0x58a070, binder 0x588910..0x588d6a) ----------------
        static readonly Dictionary<int, NuiView> bonusViews = new Dictionary<int, NuiView>();
        static bool goldenShown;
        public static IReadOnlyDictionary<int, NuiView> BonusViews => bonusViews;

        /// <summary>
        /// The bonus icons beside the result rows. Own row (single): PCROOMBONUS 1490000 (flag 1 -> 1490017 + row, flag 2
        /// -> 1490001 + row), EXPBONUS 1500000 (0x10 -> 1500001 + row, 0x20 -> 1500017 + row, both -> 1500033 + row),
        /// SANTABONUS 1510000 (0x200 / 0x400 / both -> 1510001 / 1510017 / 1510033 + row), TREEBONUS 1520000 (stage 1..4
        /// -> 1520001 / 1520017 / 1520033 / 1520049 + row), 3RDBONUS 1530000 (0x800 -> 1530001 + row); another driver's
        /// row shows only the PC-room mark 1480001 + row (flag 1). Flag 8 plays goldentime. The team list uses the team
        /// dialogs 1560000..1610000 with the same layout [inferred from the matching control lists].
        /// </summary>
        static void Bonuses(PvpRaceResult r, bool team)
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            int baseId = team ? 1560000 : 1480000;
            int pcOther = baseId, pcMine = baseId + 10000, item = baseId + 20000, santa = baseId + 30000, tree = baseId + 40000, third = baseId + 50000;
            var show = new List<int>();
            for (int i = 0; i < r.Entries.Count && i < 16; i++)
            {
                var e = r.Entries[i];
                int f = e.Bonus;
                if (!e.Me) { if ((f & Pvp.BonusPcMito) != 0) show.Add(pcOther + 1 + i); continue; }
                if ((f & Pvp.BonusPcMito) != 0 && e.Mito > 0) show.Add(pcMine + 17 + i);
                if ((f & Pvp.BonusPcExp) != 0 && e.Exp > 0) show.Add(pcMine + 1 + i);
                bool ie = (f & Pvp.BonusExpItem) != 0, im = (f & Pvp.BonusMitoItem) != 0;
                if (ie && im) show.Add(item + 33 + i); else if (ie) show.Add(item + 1 + i); else if (im) show.Add(item + 17 + i);
                bool s25 = (f & Pvp.BonusSanta25) != 0, s50 = (f & Pvp.BonusSanta50) != 0;
                if (s25 && s50) show.Add(santa + 33 + i); else if (s25) show.Add(santa + 1 + i); else if (s50) show.Add(santa + 17 + i);
                if (e.Tree >= 1 && e.Tree <= 4) show.Add(tree + 1 + 16 * (e.Tree - 1) + i);
                if ((f & Pvp.BonusAnniversary) != 0) show.Add(third + 1 + i);
                if ((f & Pvp.BonusGolden) != 0 && !goldenShown) { goldenShown = true; GameEffects.GoldenTime(); }
            }
            var dialogs = new HashSet<int>(show.Select(id => id / 10000 * 10000));
            foreach (var d in bonusViews.Keys.ToList()) if (!dialogs.Contains(d)) { var v = bonusViews[d]; CloseView(ref v); bonusViews.Remove(d); }
            foreach (var d in dialogs)
            {
                if (!bonusViews.TryGetValue(d, out var v) || !v) { v = ui.Open(d); if (!v) continue; bonusViews[d] = v; }
                foreach (var c in v.GetComponentsInChildren<NuiControlView>(true))
                    c.gameObject.SetActive(c.Data != null && show.Contains(c.Data.Id));
            }
        }

        static void CloseBonuses()
        {
            foreach (var d in bonusViews.Keys.ToList()) { var v = bonusViews[d]; CloseView(ref v); }
            bonusViews.Clear();
        }

        /// <summary>Finishers "%2d:%02d.%03d"; DNF rows 10215 "DNF - %4dm" / 10216 "DNF - %4.1fkm" from the distance left.</summary>
        static string Row(PvpResultEntry e)
        {
            if (e.Place > 0) return Pvp.Time(e.Time);
            if (e.DnfMeters >= 1000f) return $"DNF - {e.DnfMeters / 1000f,4:0.0}km";
            var t = Pvp.Text("10215", Mathf.RoundToInt(e.DnfMeters));
            return t.StartsWith("DNF") ? t : $"DNF - {Mathf.RoundToInt(e.DnfMeters),4}m";
        }

        static string Ladder(PvpResultEntry e) => e.Ladder > 0 ? $"+{e.Ladder}" : e.Ladder.ToString();

        // ---------------- RESULT16 single (0x588100) ----------------
        static void Single(NuiView v, PvpRaceResult r)
        {
            Pvp.HideUnnamedTexts(v);
            var rows = r.Entries;
            bool ladderOn = rows.Any(e => e.LadderShown);
            Pvp.Show(v.Control("ID_PVP_RESULT16_SINGLE_LADDERRANKTITLE"), ladderOn);
            for (int i = 0; i < 16; i++)
            {
                var e = i < rows.Count ? rows[i] : null;
                bool used = e != null, me = used && e.Me, fin = used && e.Place > 0;
                Pvp.Show(Pvp.ById(v, 1470011 + i), me);                    // SELF_BASETILE
                Pvp.Show(Pvp.ById(v, 1470027 + i), fin);                   // RANK badge of the row
                Pvp.Show(Pvp.ById(v, 1470043 + i), used && !fin);          // RETIRE
                Pvp.Show(Pvp.ById(v, 1470059 + i), me);                    // SELF_EXP box
                Pvp.Show(Pvp.ById(v, 1470075 + i), me);                    // EXP picture
                Pvp.Show(Pvp.ById(v, 1470091 + i), me);                    // MITO picture
                Pvp.Show(Pvp.ById(v, 1470107 + i), me && e.LevelUp);       // LVLUP
                Text(v, 1470123 + i, used && !me, used ? $"Lv {e.Level}" : "");
                Text(v, 1470139 + i, me, used ? $"Lv {e.Level}" : "");
                Text(v, 1470155 + i, used && !me, used ? e.Name : "");
                Text(v, 1470171 + i, me, used ? e.Name : "");
                Text(v, 1470187 + i, used && !me, used ? Row(e) : "");
                Text(v, 1470203 + i, me, used ? Row(e) : "");
                Text(v, 1470219 + i, me, used ? e.Exp.ToString("N0") : "");
                Text(v, 1470235 + i, me, used ? e.Mito.ToString("N0") : "");
                Text(v, 1470251 + i, used && e.LadderShown, used ? Ladder(e) : "");
                Pvp.Show(Pvp.ById(v, 1470268 + i), used && e.LadderShown && e.Ladder > 0);
                Pvp.Show(Pvp.ById(v, 1470284 + i), used && e.LadderShown && e.Ladder < 0);
            }
        }

        static void Text(NuiView v, int id, bool on, string text)
        {
            var c = Pvp.ById(v, id);
            if (!c) return;
            c.gameObject.SetActive(on);
            if (on) c.SetText(text);
        }

        // ---------------- OURRESULT16 team list (1540000) ----------------
        static void TeamList(NuiView v, PvpRaceResult r)
        {
            Pvp.HideUnnamedTexts(v);
            var rows = r.Entries;
            bool ladderOn = rows.Any(e => e.LadderShown);
            Pvp.Show(v.Control("ID_DIALOG_PVP_OURRESULT16_LADDERTITLE"), ladderOn);
            for (int i = 0; i < 16; i++)
            {
                var e = i < rows.Count ? rows[i] : null;
                bool used = e != null, fin = used && e.Place > 0;
                Pvp.Show(Pvp.ById(v, 1540008 + i), used && e.Me);
                Pvp.Show(Pvp.ById(v, 1540024 + i), used && e.Team == 0);
                Pvp.Show(Pvp.ById(v, 1540040 + i), used && e.Team == 1);
                Pvp.Show(Pvp.ById(v, 1540056 + i), fin);
                Pvp.Show(Pvp.ById(v, 1540072 + i), used && !fin);
                Text(v, 1540088 + i, used, used ? e.Name : "");
                Text(v, 1540104 + i, used, used ? Row(e) : "");
                Text(v, 1540120 + i, used, used ? e.Points.ToString() : "");
                Text(v, 1540136 + i, used && e.LadderShown, used ? Ladder(e) : "");
                Pvp.Show(Pvp.ById(v, 1540153 + i), used && e.LadderShown && e.Ladder < 0);
                Pvp.Show(Pvp.ById(v, 1540169 + i), used && e.LadderShown && e.Ladder > 0);
            }
        }

        /// <summary>TEAMPOINT 1630000: the two team totals as three glyphs each (NUM0..9 = 1630009 + digit).</summary>
        static void TeamPoints(NuiView v, PvpRaceResult r)
        {
            Digits(v, "ID_PVP_TEAMPOINT_A_", r.TeamA);
            Digits(v, "ID_PVP_TEAMPOINT_B_", r.TeamB);
            for (int d = 0; d <= 9; d++) Pvp.Show(Pvp.ById(v, 1630009 + d), false);
        }

        static void Digits(NuiView v, string prefix, int n)
        {
            n = Mathf.Clamp(n, 0, 999);
            var cells = new[] { ("3RD", n / 100), ("2ND", n / 10 % 10), ("1ST", n % 10) };
            foreach (var (pos, d) in cells)
            {
                var dst = v.Control(prefix + pos);
                var src = Pvp.ById(v, 1630009 + d);
                var di = dst ? dst.GetComponentInChildren<RawImage>(true) : null;
                var si = src ? src.GetComponentInChildren<RawImage>(true) : null;
                if (!di || !si) continue;
                di.texture = si.texture; di.uvRect = si.uvRect; di.enabled = si.texture;
                dst.gameObject.SetActive(pos != "3RD" || n >= 100);
            }
        }

        /// <summary>Team RESULT16 1550000: WIN / LOSE / PERFECT banner, team A rows left (0..7), team B right (8..15).</summary>
        static void TeamFinal(NuiView v, PvpRaceResult r)
        {
            Pvp.HideUnnamedTexts(v);
            var mine = r.Mine;
            bool won = mine != null && (r.Winner == mine.Team || r.Winner == 2);
            bool perfect = won && r.Entries.Where(e => e.Place > 0).Take(r.Entries.Count(e => e.Team == mine.Team)).All(e => e.Team == mine.Team);
            Pvp.Show(v.Control("ID_DIALOG_PVP_RESULT16_WIN"), won && !perfect);
            Pvp.Show(v.Control("ID_DIALOG_PVP_RESULT16_PERFECT"), perfect);
            Pvp.Show(v.Control("ID_DIALOG_PVP_RESULT16_LOSE"), !won);
            Pvp.Show(v.Control("ID_PVP_RESULT16_ATEAM_WINPIC1"), r.Winner == 0 || r.Winner == 2);
            Pvp.Show(v.Control("ID_PVP_RESULT16_ATEAM_LOSEPIC1"), r.Winner == 1);
            Pvp.Show(v.Control("ID_PVP_RESULT16_BTEAM_WINPIC1"), r.Winner == 1 || r.Winner == 2);
            Pvp.Show(v.Control("ID_PVP_RESULT16_BTEAM_LOSEPIC1"), r.Winner == 0);
            var a = r.Entries.Where(e => e.Team == 0).Take(8).ToList();
            var b = r.Entries.Where(e => e.Team == 1).Take(8).ToList();
            for (int i = 0; i < 16; i++)
            {
                var e = i < 8 ? (i < a.Count ? a[i] : null) : (i - 8 < b.Count ? b[i - 8] : null);
                bool used = e != null, me = used && e.Me;
                Pvp.Show(Pvp.ById(v, 1550018 + i), me);
                Pvp.Show(Pvp.ById(v, 1550034 + i), me);
                Text(v, 1550056 + i, used, used ? e.Name : "");
                Text(v, 1550072 + i, used, used ? $"Lv {e.Level}" : "");
                Pvp.Show(Pvp.ById(v, 1550088 + i), me && e.LevelUp);
                Text(v, 1550104 + i, me, me ? e.Exp.ToString("N0") : "");
                Pvp.Show(Pvp.ById(v, 1550125 + i), me);
                Pvp.Show(Pvp.ById(v, 1550141 + i), me);
                Text(v, 1550157 + i, me, me ? e.Mito.ToString("N0") : "");
            }
        }
    }
}
