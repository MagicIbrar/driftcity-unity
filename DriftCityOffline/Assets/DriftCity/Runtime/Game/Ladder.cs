using System.Collections.Generic;
using System.Linq;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.Game
{
    /// <summary>
    /// The PvP ladder ranking (PVP.md section 9): the LADDER frame 3910000 with the server tab 3930000 (LADDER list
    /// 0x68e2f0 / 0x68e990: 10 rows per page, PAGE0..9, LEFT / RIGHT). Online the rows come from the server (race.ladder,
    /// the drivers' LadderPoints); offline the list is the player and the AI rivals met in the Battle Zone.
    /// Columns: Ranking, Name, Crew (none in the remake), Battle Count (races / wins), License (shows the ladder points).
    /// </summary>
    public static class Ladder
    {
        const int Frame = 3910000, Dialog = 3930000, Rows = 10;
        static NuiView frame, view;
        static int page, total;
        static readonly List<Text[]> cells = new List<Text[]>();
        static UiManager hooked;
        static readonly float[] Columns = { 18, 144, 312, 520, 748 };

        public static bool IsOpen => view;
        public static NuiView View => view;

        public static void Open()
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            if (!view)
            {
                frame = ui.Open(Frame);
                view = ui.Open(Dialog);
                if (!view) return;
                Build();
            }
            if (hooked != ui)
            {
                hooked = ui;
                ui.On("ID_LADDER_RANKTAB_CLOSE", _ => Close());
                ui.On("ID_LADDER_RANKTAB_TABBTN5TH", _ => Social.ChannelBattle.OpenCrewLadder());   // the crew ladder (0x608770)
                ui.On("ID_LADDER_TABSERVER_LEFT", _ => Page(page - 1));
                ui.On("ID_LADDER_TABSERVER_RIGHT", _ => Page(page + 1));
                for (int i = 0; i < 10; i++) { int k = i; ui.On("ID_LADDER_TABSERVER_PAGE" + i, _ => Page(page / 10 * 10 + k)); }
                ui.On("ID_LADDER_TABSERVER_SEARCH", _ => Load());
                ui.On("ID_LADDER_TABSERVER_DETAIL", _ => PvpLobby.Unavailable());
            }
            // the page with the player's own rank (offline; online the first page)
            page = 0;
            if (!OnlineSession.Active)
            {
                var me = PlayerProfile.Current?.Name;
                int rank = OfflineTable().FindIndex(x => x.name == me);
                if (rank >= 0) page = rank / Rows;
            }
            Load();
        }

        public static void Close()
        {
            var ui = UiManager.Instance;
            Social.ChannelBattle.CloseLadder();
            foreach (var v in new[] { frame, view }) if (v) { if (ui) ui.Close(v); else Object.Destroy(v.gameObject); }
            frame = view = null;
            cells.Clear();
        }

        static void Build()
        {
            cells.Clear();
            var lb = view.Control("ID_LADDER_TABSERVER_LB");
            if (!lb) return;
            for (int r = 0; r < Rows; r++)
            {
                var row = new Text[Columns.Length];
                for (int c = 0; c < Columns.Length; c++)
                {
                    float w = c + 1 < Columns.Length ? Columns[c + 1] - Columns[c] - 8 : 90;
                    row[c] = MissionUi.Label(lb.transform, new Vector2(Columns[c], -(r * 41 + 10)), new Vector2(w, 24), 14, new Color(0.16f, 0.08f, 0f), TextAnchor.MiddleLeft);
                }
                cells.Add(row);
            }
        }

        static void Page(int p)
        {
            int pages = Mathf.Max(1, (total + Rows - 1) / Rows);
            page = Mathf.Clamp(p, 0, Mathf.Min(199, pages - 1));
            Load();
        }

        public static IReadOnlyList<string[]> Shown => cells.Select(r => r.Select(t => t.text).ToArray()).ToList();

        static void Load()
        {
            if (OnlineSession.Active)
            {
                OnlineSession.Instance.Net.Request("race.ladder", new Dictionary<string, object> { ["page"] = page }, r =>
                {
                    if (!view || !r.B("ok")) return;
                    total = r.I("total");
                    var rows = (r.L("rows") ?? new List<object>()).OfType<Dictionary<string, object>>()
                        .Select(x => (x.I("rank"), x.S("name"), x.I("races"), x.I("wins"), x.I("points"))).ToList();
                    Fill(rows);
                });
                return;
            }
            var all = OfflineTable();
            total = all.Count;
            Fill(all.Skip(page * Rows).Take(Rows).ToList());
        }

        /// <summary>Offline: the player plus the AI drivers' names with steady points (seeded by the name).</summary>
        static List<(int rank, string name, int races, int wins, int points)> OfflineTable()
        {
            var p = PlayerProfile.Current;
            var list = new List<(string name, int races, int wins, int points)>();
            if (p != null) list.Add((p.Name, p.PvpRaces, p.PvpWins, p.LadderPoints));
            foreach (var n in new[] { "Rio", "Duke", "Mika", "Jin", "Sora", "Kai", "Nova", "Taro", "Yuna", "Leon", "Hana", "Max", "Zed", "Aki", "Rex", "Luna", "Ivan", "Mei", "Toby", "Ken" })
            {
                int seed = 0x5a17; foreach (char ch in n) seed = seed * 31 + ch;
                var rng = new System.Random(seed & 0x7fffffff);
                int races = rng.Next(10, 300), wins = rng.Next(0, races / 3);
                list.Add((n, races, wins, 880 + rng.Next(0, 420)));
            }
            return list.OrderByDescending(x => x.points).ThenBy(x => x.name).Select((x, i) => (i + 1, x.name, x.races, x.wins, x.points)).ToList();
        }

        static void Fill(List<(int rank, string name, int races, int wins, int points)> rows)
        {
            for (int r = 0; r < cells.Count; r++)
            {
                bool used = r < rows.Count;
                var row = cells[r];
                row[0].text = used ? rows[r].rank.ToString() : "";
                row[1].text = used ? rows[r].name : "";
                row[2].text = used ? "-" : "";
                row[3].text = used ? $"{rows[r].races} ({rows[r].wins} W)" : "";
                row[4].text = used ? $"{rows[r].points} pt" : "";
            }
            int first = page / 10 * 10, pages = Mathf.Max(1, (total + Rows - 1) / Rows);
            for (int i = 0; i < 10; i++)
            {
                var c = view.Control("ID_LADDER_TABSERVER_PAGE" + i);
                if (!c) continue;
                bool on = first + i < pages;
                c.gameObject.SetActive(on);
                if (on) { c.SetText((first + i + 1).ToString("000")); c.Selected = first + i == page; }
            }
            view.SetEnabled("ID_LADDER_TABSERVER_LEFT", page > 0);
            view.SetEnabled("ID_LADDER_TABSERVER_RIGHT", page + 1 < pages);
        }
    }
}
