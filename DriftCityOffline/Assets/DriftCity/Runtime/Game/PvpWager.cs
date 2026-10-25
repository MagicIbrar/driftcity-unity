using System;
using System.Collections.Generic;
using System.Linq;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// Wager rooms ("Batting", DriftCity_RE/PVP.md "Unity port"): CREATEROOM GRADE_A..D (5000 / 1000 / 500 / 100 Mito; S =
    /// 10000), a driver needs ten times the fee (11066 / 11067), START needs two players (11107), WAIT16 BATTINGTEXT 11077
    /// "%s Rank, %d Mito Entry Fee". The result packet 0x1f3 carries each driver's Mito change and new total; the client
    /// prints 11083 "%s(%dlevel): %d Place,%d Mito Issued" for everyone and 11084 / 11085 / 11086 for itself, and shows
    /// RESULT_SINGLEBB 4080000 (rows by rank, 0x588f10) or RESULT_TEAMBB 4090000 (blue / red lists, 0x58bce0) with the
    /// grade's icon and Mito picture, titles 11078..11081. The payout itself is the server's (remake rule shared with
    /// race.js: pot = every fee, 10% tax, 60 / 30 / 10% to the first three finishers; team: the winners share).
    /// </summary>
    public static class PvpWager
    {
        public const int SingleDialog = 4080000, TeamDialog = 4090000;
        public const float Tax = 0.1f;
        static readonly int[] Shares = { 60, 30, 10 };   // percent (integer maths, as the server)

        /// <summary>The payouts of a settled room: key -> Mito paid back (not counting the fee already paid).</summary>
        public static Dictionary<string, long> Payouts(IList<(string key, int place, int team)> entries, int fee, bool team, int winner)
        {
            long pot = (long)fee * entries.Count * 90 / 100;   // 10% tax
            var outp = entries.ToDictionary(e => e.key, _ => 0L);
            if (team)
            {
                var win = winner == 2 ? entries.Where(e => e.team >= 0).ToList() : entries.Where(e => e.team == winner).ToList();
                foreach (var e in win) outp[e.key] = pot / win.Count;
                return outp;
            }
            var fin = entries.Where(e => e.place > 0).OrderBy(e => e.place).ToList();
            if (fin.Count == 0) return outp;
            long paid = 0;
            for (int i = 1; i < Mathf.Min(3, fin.Count); i++) { long v = pot * Shares[i] / 100; outp[fin[i].key] = v; paid += v; }
            outp[fin[0].key] = pot - paid;
            return outp;
        }

        /// <summary>The chat lines of 0x509810: every driver (11083), then my own (11084 issued / 11085 no change / 11086 lost).</summary>
        public static void ChatLines(PvpRaceResult r)
        {
            foreach (var e in r.Entries) PvpChat.System(Pvp.Text("11083", e.Name, e.Level, e.Place, e.WagerDelta));
            var me = r.Mine;
            if (me == null) return;
            string line = me.WagerDelta > 0 ? Pvp.Text("11084", me.WagerDelta) : me.WagerDelta == 0 ? Pvp.Text("11085") : Pvp.Text("11086", -me.WagerDelta);
            Net.ChatBox.System(line);
            PvpChat.System(line);
        }

        public static NuiView Open(PvpRaceResult r)
        {
            var ui = UiManager.Instance;
            if (!ui) return null;
            var v = ui.Open(r.Team ? TeamDialog : SingleDialog);
            if (!v) return null;
            if (r.Team) FillTeam(v, r); else FillSingle(v, r);
            return v;
        }

        static void Grade(NuiView v, string prefix, int grade)
        {
            for (int g = 1; g <= 5; g++)
            {
                v.SetVisible($"{prefix}GRADE_{Pvp.WagerLetters[g]}", g == grade);
                v.SetVisible($"{prefix}MITO_{Pvp.WagerLetters[g]}", g == grade);
            }
        }

        static string Signed(long d) => Pvp.Text("10214", d);

        static void Text(NuiView v, string id, bool on, string text) { v.SetVisible(id, on); if (on) v.SetText(id, text); }

        /// <summary>RESULT_SINGLEBB 4080000 (0x588e10 / 0x588f10): row = rank - 1, DNF rows after the finishers.</summary>
        static void FillSingle(NuiView v, PvpRaceResult r)
        {
            const string P = "ID_PVP_RESULT_SINGLEBB_";
            Grade(v, P, r.Wager);
            v.SetText(P + "TITLE_RANK", Pvp.Text("11078"));
            v.SetText(P + "TITLE_ID", Pvp.Text("11079"));
            v.SetText(P + "TITLE_MITO", Pvp.Text("11080"));
            v.SetText(P + "COMMISION", Pvp.Text("11081"));
            var rows = r.Entries.Where(e => e.Place > 0).OrderBy(e => e.Place).Concat(r.Entries.Where(e => e.Place <= 0)).ToList();
            for (int i = 0; i < 16; i++)
            {
                string n = (i + 1).ToString("00");
                var e = i < rows.Count ? rows[i] : null;
                bool used = e != null, me = used && e.Me, fin = used && e.Place > 0;
                v.SetVisible(P + "BG" + n, used);
                v.SetVisible(P + "BASE" + n, used && !me);
                v.SetVisible(P + "BASEMINE" + n, me);
                Text(v, P + "MYNAME" + n, me, used ? e.Name : "");
                Text(v, P + "OTHERNAME" + n, used && !me, used ? e.Name : "");
                v.SetVisible(P + "RANK" + n, fin);
                v.SetVisible(P + "RANKRETIRE" + n, used && !fin);
                v.SetVisible(P + "MITOMARK" + n, used);
                Text(v, P + "MITO" + n, used, used ? Signed(e.WagerDelta) : "");
            }
        }

        /// <summary>RESULT_TEAMBB 4090000 (0x58ab20 / 0x58b520): team A blue, team B red, rows by rank, WIN / LOSE marks.</summary>
        static void FillTeam(NuiView v, PvpRaceResult r)
        {
            const string P = "ID_PVP_RESULT_TEAMBB_";
            Grade(v, P, r.Wager);
            v.SetText(P + "COMMITION", Pvp.Text("11081"));
            foreach (var side in new[] { "BLUE", "RED" }) { v.SetText($"{P}TITLE{side}_RANK", Pvp.Text("11078")); v.SetText($"{P}TITLE{side}_NAME", Pvp.Text("11079")); v.SetText($"{P}TITLE{side}_MITO", Pvp.Text("11080")); }
            v.SetVisible(P + "BLUEWIN", r.Winner == 0 || r.Winner == 2);
            v.SetVisible(P + "BLUELOSE", r.Winner == 1);
            v.SetVisible(P + "REDWIN", r.Winner == 1 || r.Winner == 2);
            v.SetVisible(P + "REDLOSE", r.Winner == 0);
            for (int t = 0; t < 2; t++)
            {
                string side = t == 0 ? "BLUE" : "RED";
                var rows = r.Entries.Where(e => e.Team == t).OrderBy(e => e.Place > 0 ? e.Place : 99).Take(8).ToList();
                bool won = r.Winner == t || r.Winner == 2;
                for (int i = 0; i < 8; i++)
                {
                    string n = (i + 1).ToString("00");
                    var e = i < rows.Count ? rows[i] : null;
                    bool used = e != null, me = used && e.Me, fin = used && e.Place > 0;
                    v.SetVisible($"{P}{side}_MYBASE{n}", me);
                    Text(v, $"{P}{side}_NAMEMINE{n}", me, used ? e.Name : "");
                    Text(v, $"{P}{side}_NAMEOTHER{n}", used && !me, used ? e.Name : "");
                    v.SetVisible($"{P}{side}_RANK{n}", fin);
                    v.SetVisible($"{P}{side}_RANKPOS{n}", false);
                    v.SetVisible($"{P}{side}_RANKRETIRE{n}", used && !fin);
                    v.SetVisible($"{P}{side}_MITOMARK{n}", used);
                    Text(v, $"{P}{side}_MITO{n}", used, used ? Signed(e.WagerDelta) : "");
                }
                for (int i = 1; i <= 12; i++) v.SetVisible($"{P}{side}_VICTORY{i:00}", won && i <= rows.Count);
            }
        }
    }
}
