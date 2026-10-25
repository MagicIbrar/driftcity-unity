using System;
using DriftCity.UI;

namespace DriftCity.Game.Services
{
    /// <summary>
    /// The VIEWMAP city pages (DriftCity_RE/SERVICES.md section 9; MISSIONS.md 14.1).
    /// Client [exact]: the full map keeps a page number 1..5 (+0x174: 1 Moon Palace 480000, 2 Koinonia 490000, 3 Cras
    /// 500000, 4 Oros 510000, 5 Taipei 530000); RIGHT (0x5f5c40 with 0) goes to the next page unless its city is locked by
    /// the quest manager's flags (+0x10b Koinonia, +0x10c Cras, +0x10d Oros, +0x10e Taipei) - then sound 0x4e8b and the page
    /// stays; past page 5 the same sound; LEFT goes back down to page 1 (no sound). 0x5f3170 enables the arrows: LEFT off on
    /// page 1, RIGHT off when the next page is locked (Cras / Oros also need their server city bits, net+0x258 &amp; 4 / 8)
    /// and on page 5; in stage 0x13 every arrow is on.
    /// Remake: <see cref="QuestUnlocks.CityOpen"/> is the flag (every server city bit on); <see cref="Menus.WorldMapPage"/>
    /// opens a page; the player marker shows on the page of the city the driver is in.
    /// </summary>
    public static class WorldMapPages
    {
        public static readonly string[] Pages = { "moonpalace", "koinonia", "cras", "oros", "taipei" };

        /// <summary>The page shown (tests).</summary>
        public static string Current { get; private set; } = "";

        public static int IndexOf(string city) => Array.IndexOf(Pages, city);

        /// <summary>A page may be shown: Moon Palace always, the others with their unlock flag.</summary>
        public static bool Open(string city) => city == "moonpalace" || QuestUnlocks.CityOpen(PlayerProfile.Current, city);

        /// <summary>Wires LEFT / RIGHT of an open page; <paramref name="show"/> opens another page.</summary>
        public static void Hook(NuiView v, string prefix, string city, Action<string> show)
        {
            Current = city;
            var ui = UiManager.Instance;
            if (!v || !ui) return;
            int i = IndexOf(city);
            v.SetEnabled($"ID_VIEWMAP_{prefix}_LEFT", i > 0);
            v.SetEnabled($"ID_VIEWMAP_{prefix}_RIGHT", i >= 0 && i < Pages.Length - 1 && Open(Pages[i + 1]));
            ui.On($"ID_VIEWMAP_{prefix}_LEFT", _ => { var to = Step(city, -1); if (to != null) show(to); });
            ui.On($"ID_VIEWMAP_{prefix}_RIGHT", _ => { var to = Step(city, +1); if (to != null) show(to); });
        }

        /// <summary>The page after / before <paramref name="city"/>, or null (a locked or missing page: sound 0x4e8b on RIGHT).</summary>
        public static string Step(string city, int dir)
        {
            int i = IndexOf(city);
            if (i < 0) return null;
            int j = i + dir;
            if (dir < 0) return j >= 0 ? Pages[j] : null;
            if (j >= Pages.Length || !Open(Pages[j])) { UiSounds.Cancel(); return null; }
            return Pages[j];
        }
    }
}
