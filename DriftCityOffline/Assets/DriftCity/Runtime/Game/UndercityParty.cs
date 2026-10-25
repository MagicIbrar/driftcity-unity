using System;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Net;
using DriftCity.Social;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// The online party Undercity (DriftCity_RE/PARTYPLAY.md): the client's party checks at the entrance
    /// (UC_Err_PlayTogether: a party of 2-4, UC_Err_MaxPlayerCnt, UC_Err_SameCity: everyone in this city), the request
    /// (client 0xfc; the requester becomes the run leader, UC manager +0x3c), WAITPLAYER 3530000 for the requester and
    /// REQUESTINVITE 3540000 for the others (server 0xfc, answers 0xfd / 0xfe relayed by the server, 10943 when the run is
    /// cancelled), then the stage itself (server 0xff -> <see cref="UndercityStage.BeginOnline"/>). The server's Undercity
    /// room (DriftCityServer/src/undercity.js) keeps the shared score, the phase clock and pays every member. Pushes
    /// "party.uc.*" reach this class through <see cref="PartyWindow.OnPush"/>.
    /// </summary>
    public static class UndercityParty
    {
        /// <summary>The server's room of the run being asked for or played (0 = none).</summary>
        public static int Room { get; private set; }
        /// <summary>A request is out (WAITPLAYER / REQUESTINVITE) and the stage hasn't started.</summary>
        public static bool Pending => Room != 0 && !(UndercityStage.Active && UndercityStage.Active.Online);
        public static string LastCancel { get; private set; }
        public static string LastInviteFrom { get; private set; }
        /// <summary>The inviter of the last invite refused on its own during Rush Time (11146).</summary>
        public static string LastAutoRefused { get; private set; }
        /// <summary>The dialogs before the stage (WAITPLAYER, REQUESTINVITE, the waiting alert).</summary>
        public static UndercityHud Host => host;

        static UndercityHud host;

        static UndercityHud EnsureHost()
        {
            if (host) return host;
            var go = new GameObject("Undercity Party");
            host = go.AddComponent<UndercityHud>();
            return host;
        }

        static void CloseHost()
        {
            if (!host) return;
            host.CloseAll();
            UnityEngine.Object.Destroy(host.gameObject);
            host = null;
        }

        /// <summary>The client's checks before the request (null = fine, else the UC text key to show).</summary>
        public static string CheckParty(string city)
        {
            if (!PartyWindow.InParty) return "UC_Err_PlayTogether";
            if (PartyWindow.Members.Count > PartyWindow.MaxMembers) return "UC_Err_MaxPlayerCnt";
            if (PartyWindow.Members.Any(m => !m.Me && !string.Equals(m.City, city, StringComparison.OrdinalIgnoreCase))) return "UC_Err_SameCity";
            return null;
        }

        /// <summary>UC_NeedMito's Accept online: asks the party through the server. <paramref name="done"/> gets the UC text key of a refusal (null = asked).</summary>
        public static void Request(string city, Action<string, string> done)
        {
            if (!SocialNet.Online) { done?.Invoke("offline", "offline"); return; }
            SocialNet.Request("uc.request", new Dictionary<string, object> { ["city"] = city }, r =>
            {
                if (r != null && r.B("ok")) { Room = r.I("room"); done?.Invoke(null, null); return; }
                string code = r?.S("code") ?? "";
                string key = code == "PlayTogether" ? "UC_Err_PlayTogether" : code == "SameCity" ? "UC_Err_SameCity" : code == "MaxPlayerCnt" ? "UC_Err_MaxPlayerCnt" : code == "NeedMito" ? "UC_Err_NeedMito" : null;
                done?.Invoke(key ?? "error", SocialNet.Err(r));
            });
        }

        /// <summary>REQUESTINVITE's answer (0xfd accept / 0xfe deny).</summary>
        public static void Answer(bool accept)
        {
            if (!SocialNet.Online || Room == 0) return;
            SocialNet.Request("uc.answer", new Dictionary<string, object> { ["accept"] = accept }, r =>
            {
                if (r != null && !r.B("ok") && r.S("code") == "NeedMito") ChatBox.System("[Undercity] " + UndercityData.Text("UC_Err_NeedMito").AllText.Replace("\n", " "));
            });
            if (accept)
            {
                // 0xfd for my own id: WAITPLAYER until everyone answered
                var h = EnsureHost();
                h.ShowWaiting(true);
            }
        }

        /// <summary>"party.uc.*" from the server.</summary>
        public static void OnPush(string t, Dictionary<string, object> m)
        {
            var stage = UndercityStage.Active;
            switch (t)
            {
                case "party.uc.wait":
                {
                    // the requester: WAITPLAYER and UC_Emer_WaitParty
                    Room = m.I("room");
                    var h = EnsureHost();
                    h.ShowWaiting(true);
                    h.Alert(UndercityData.Text("UC_Emer_WaitParty"), m.I("seconds", 15));
                    ChatBox.System("[Undercity] " + UndercityData.Text("UC_Emer_WaitParty").AllText.Replace("\n", " "));
                    return;
                }
                case "party.uc.invite":
                {
                    Room = m.I("room");
                    LastInviteFrom = m.S("from");
                    // 0x50e1d0: in Rush Time (waiting in the zone or running) the invite is refused at once (0xfe) with 11146
                    if (RushTime.Participating)
                    {
                        ChatBox.System(MissionText.Get("11146", LastInviteFrom));
                        LastAutoRefused = LastInviteFrom;
                        Answer(false);
                        return;
                    }
                    var h = EnsureHost();
                    h.ShowRequestInvite(LastInviteFrom, m.I("fee", UndercityData.FeeFor(GameFlow.Instance ? GameFlow.Instance.City : null)), m.I("seconds", 15), Answer);
                    return;
                }
                case "party.uc.autorefuse":
                    // the server's Rush Time (src/huvevents.js) answered for me
                    LastAutoRefused = m.S("from");
                    ChatBox.System(MissionText.Get("11146", LastAutoRefused));
                    return;
                case "party.uc.answer":
                    if (m.S("name") != PlayerProfile.Current?.Name) ChatBox.System($"[Undercity] {m.S("name")} {(m.B("accept") ? "accepted" : "refused")} the Rescue Mission.");
                    return;
                case "party.uc.cancel":
                    LastCancel = m.S("text", "Registration canceled due to party being unable to accept or rejecting the invitation.");   // 10943
                    Room = 0;
                    CloseHost();
                    ChatBox.System("[Undercity] " + LastCancel);
                    UiPopup.Message(LastCancel);
                    return;
                case "party.uc.start":
                    Start(m);
                    return;
            }
            if (stage && stage.Online && stage.Room == m.I("room")) stage.OnServer(t, m);
        }

        /// <summary>0xff: the stage of the city for everyone (the running missions are given up, as UC_NeedMito says).</summary>
        static void Start(Dictionary<string, object> m)
        {
            CloseHost();
            Room = m.I("room");
            if (UndercityStage.Active) return;
            if (ChaseMission.Active) UnityEngine.Object.Destroy(ChaseMission.Active.gameObject);
            if (PatrolMission.Active) PatrolMission.Active.GiveUp();
            var session = CitySession.Current;
            var stage = session ? UndercityStage.BeginOnline(session, m) : null;
            if (!stage)
            {
                // no city to run it in: leave the server's run at once
                SocialNet.Request("uc.leave", null, null);
                Room = 0;
            }
        }

        /// <summary>The stage ended (result shown, left, or the city went away).</summary>
        public static void Ended(UndercityStage stage)
        {
            if (stage && stage.Room == Room) Room = 0;
        }

        /// <summary>Tests / logout: forget the run.</summary>
        public static void ResetAll()
        {
            Room = 0; LastCancel = null; LastInviteFrom = null;
            CloseHost();
        }
    }
}
