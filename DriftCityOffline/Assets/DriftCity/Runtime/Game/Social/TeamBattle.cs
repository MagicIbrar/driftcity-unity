using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Game;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Social
{
    /// <summary>
    /// Channel Battles (crew battles, SOCIAL.md section 5). In the client a challenger crew races the crew that owns a
    /// channel in Battle Zone team races (room flag 0x8): the attacker has to win 3 rounds in a row (Round 1, Round 2,
    /// Final, 0x674290) and takes the channel; one lost round ends the attack. The remake runs them as crew rooms of the
    /// Battle Zone (race.js: the host's crew on the even slots, the other crew on the odd ones, equal sides) and shows
    /// RESULT2 2200000 (WIN / LOSE / PERFECT) with the round texts 10361-10377. Offline the Battle Zone's team race
    /// against AI drivers stands in (no crew records).
    /// </summary>
    public static class TeamBattle
    {
        public const int ResultDlg = 2200000;
        static NuiView result;
        public static NuiView ResultView => result;
        public static Dictionary<string, object> LastResult { get; private set; }

        /// <summary>Crew Center "Enter Channel Battle": the channel list (ChannelBattle; offline its Enter opens the AI team room).</summary>
        public static void Enter() => ChannelBattle.OpenChannels();

        /// <summary>race.results with a crew block (src/race.js): RESULT2 and the round texts.</summary>
        public static void OnResults(Dictionary<string, object> m)
        {
            var crew = m.O("crew");
            if (crew == null) return;
            LastResult = crew;
            string me = PlayerProfile.Current?.Name;
            var mine = (m.L("results") ?? new List<object>()).OfType<Dictionary<string, object>>().FirstOrDefault(x => x.S("name") == me);
            int mySide = mine != null ? mine.I("team") : -1;
            int winner = crew.I("winnerSide");
            bool won = mySide >= 0 && winner == mySide;
            ShowResult(won, crew.B("perfect") && won, RoundLine(crew, mySide));
        }

        static string Crew(Dictionary<string, object> c, int side) => side == c.I("attackerSide") ? c.S("attacker") : c.S("defender");

        /// <summary>The client's round texts seen from my side (10364-10377).</summary>
        static string RoundLine(Dictionary<string, object> c, int mySide)
        {
            bool attacking = mySide == c.I("attackerSide");
            int round = c.I("round");
            string other = Crew(c, 1 - mySide);
            int chan = c.S("channel", "x-1").Split('-').Select(s => int.TryParse(s, out var n) ? n : 0).LastOrDefault();
            if (c.B("friendly") || round == 0) return c.I("winnerSide") == mySide ? $"Your Crew won against {other} Crew." : $"Your Crew lost against {other} Crew.";
            if (c.B("captured"))
                return attacking ? SocialNet.T("10377", "Since you won all Channel Battles, ownership of Channel %d has become yours.", chan)
                                 : SocialNet.T("10370", "Since your Crew was defeated in all Channel Battles, ownership of Channel %d has to be transferred to %s Crew.", chan, other);
            if (c.B("attackerWon"))
                return attacking ? (round == 1 ? SocialNet.T("10374", "Your Crew defeated %s Crew in the Channel Battle.  Start the 2nd round.", other) : SocialNet.T("10375", "Your Crew defeated %s Crew in the Channel Battle.  Start the last round.", other))
                                 : (round == 1 ? SocialNet.T("10366", "Your Crew was defeated by %s Crew in the Channel Battle.  Start the 2nd round.", other) : SocialNet.T("10367", "Your Crew was defeated by %s Crew in the Channel Battle.  Start the final round.", other));
            return attacking ? SocialNet.T("10371", "%sCrew (Attacking) defeated!", c.S("attacker")) + " " + SocialNet.T("10372", "If you are defeated even once, you cannot rechallenge in the same channel.")
                             : SocialNet.T("10364", "Your Crew won the Channel Battle against %s Crew. The next Crew for the next Channel Battle is waiting.", other);
        }

        /// <summary>The crew chat line for a crew.notice "battle" (members who didn't race see it too).</summary>
        public static string NoticeLine(Dictionary<string, object> c)
        {
            if (c.B("captured")) return SocialNet.T("10376", "Obtained Channel %d Ownership!", c.S("channel", "x-1").Split('-').Select(s => int.TryParse(s, out var n) ? n : 0).LastOrDefault()) + $" ({c.S("attacker")})";
            if (c.B("attackerWon")) return SocialNet.T("10373", "%sCrew (Attacking) %dwon!", c.S("attacker"), c.I("round"));
            if (c.I("round") > 0) return SocialNet.T("10363", "%sCrew (Defending) won!", c.S("defender"));
            return $"{Crew(c, c.I("winnerSide"))} Crew won a friendly match.";
        }

        /// <summary>RESULT2 2200000 for a few seconds over the results.</summary>
        public static void ShowResult(bool won, bool perfect, string text)
        {
            var ui = UiManager.Instance;
            ChatBox.Ensure()?.Add("team", "", text);
            if (!ui) return;
            SocialUi.Close(ref result);
            result = ui.Open(ResultDlg);
            if (!result) return;
            result.SetVisible("ID_TC_RESULT2_PERFECT", perfect);
            result.SetVisible("ID_TC_RESULT2_WIN", won && !perfect);
            result.SetVisible("ID_TC_RESULT2_LOSE", !won);
            result.SetVisible("ID_TC_RESULT2_TEXT_WIN", won);
            result.SetVisible("ID_TC_RESULT2_TEXT_LOSE1", !won);
            result.SetVisible("ID_TC_RESULT2_TEXT_LOSE2", false);
            result.SetText(won ? "ID_TC_RESULT2_TEXT_WIN" : "ID_TC_RESULT2_TEXT_LOSE1", text);
            result.SetText("ID_TC_RESULT2_DESCRIPTION", "");
            if (GameEffects.Sounds) AudioLibrary.PlaySfx(won ? "sfx/SID_PVP_WIN" : "sfx/SID_PVP_LOSE");
            var host = Object.FindAnyObjectByType<SocialHost>();
            if (host) host.StartCoroutine(CloseLater(result, 5f));
        }

        static IEnumerator CloseLater(NuiView v, float s)
        {
            yield return new WaitForSecondsRealtime(s);
            if (v && v == result) SocialUi.Close(ref result);
        }
    }
}
