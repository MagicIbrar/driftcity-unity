using System;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Net;
using DriftCity.UI;

namespace DriftCity.Game.Services
{
    /// <summary>
    /// Crew-owned Mittron filling stations (DriftCity_RE/SERVICES.md section 6; server src/stations.js).
    /// Client [exact]: CHARGER's prices come from op 0x2d4 (0x4ff020 -> 0x54fec0): list / sale price per litre and OWNTEAM:
    /// the channel index (0x476630(net+0x3b4e8) - 1, 0..23) picks the Team Center's channel record -> 10168 "Owned by: %s
    /// Crew" (the crew mark at OWNTEAMMARK), else 10169 "Owned by: None". The owner's Master was paid the station fees
    /// (20268 / 20288).
    /// Remake rules: a station belongs to the crew that owns the Channel Battle channel of the driver's world channel
    /// (battle channel i = "&lt;city&gt;-&lt;n&gt;"); 10 Mito a litre everywhere; online 20 % of a fill at an owned station goes to the
    /// owner's fee pool, paid to its Master by note every hour; the fill is station.fill. Offline the AI crews of the
    /// offline channel table own the stations of their channels (channel 1 of each city in the offline world).
    /// </summary>
    public static class StationOwners
    {
        /// <summary>The owner crew of the open station ("" = none) and its mark.</summary>
        public static string Owner { get; private set; } = "";
        public static int OwnerMark { get; private set; }

        /// <summary>The world channel the driver is in (offline: channel 1 of the city).</summary>
        public static string ChannelKey
        {
            get
            {
                if (ServicesNet.Online && OnlineSession.Instance && !string.IsNullOrEmpty(OnlineSession.Instance.Channel)) return OnlineSession.Instance.Channel;
                var flow = GameFlow.Instance;
                return (flow ? flow.City : "cras") + "-1";
            }
        }

        /// <summary>CHARGER.Open: OWNTEAM 10168 / 10169 and the mark (online station.info; offline the offline channel table).</summary>
        public static void Fill(NuiView view, string prefix, Action done = null)
        {
            void Show(string owner, int mark)
            {
                Owner = owner ?? ""; OwnerMark = mark;
                if (!view) { done?.Invoke(); return; }
                view.SetText(prefix + "OWNTEAM", Owner.Length > 0 ? MissionText.Get("10168", Owner) : MissionText.Get("10169"));
                Social.SocialUi.ShowMark(view, prefix + "OWNTEAMMARK", Owner.Length > 0 ? mark : 0);
                done?.Invoke();
            }
            if (ServicesNet.Online)
            {
                ServicesNet.Request("station.info", null, r => Show(r != null && r.B("ok") ? r.S("owner", "") : "", r?.I("ownerMark") ?? 0));
                return;
            }
            Social.ChannelBattle.Load();
            var ch = Social.ChannelBattle.Channels.FirstOrDefault(c => c.Key == ChannelKey);
            Show(ch?.Owner?.Name ?? "", ch?.Owner?.Mark ?? 0);
        }

        /// <summary>The fill request: station.fill online (the server pays the owner's pool), the old fuel.buy shape.</summary>
        public const string FillOp = "station.fill";
    }
}
