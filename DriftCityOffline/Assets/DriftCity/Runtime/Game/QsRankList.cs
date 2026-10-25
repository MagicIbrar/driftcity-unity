using System;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Net;
using DriftCity.Social;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.Game
{
    /// <summary>
    /// QSRANKLIST 2830000, the Delivery Service rankings (MISSIONS.md section 12): opened by packet 0x146 (0x4f4990) when a
    /// job is finished and by the ARBEIT list's week / month / all buttons (2810013-15, 0x557540). The server kept the lists;
    /// ours (src/quests.js "qs.rank") ranks every driver's best time per job for this week, this month or this year, with
    /// PREVWEEK for the period before. Offline the list holds the driver's own three best runs. Rows: rank, crew, driver,
    /// best record (the crew column: each driver's crew from the server's crews); the bottom line: my rank (10907 "Rank lower than
    /// %d" past the list), my crew, this run, my best.
    /// TIMEATTACK_TITLE and the Time Attack close button for Team ROO.
    /// </summary>
    public static class QsRankList
    {
        public const int DialogId = 2830000;
        public const int PageSize = 10;

        public sealed class Row { public int Rank; public string Name, Team; public float Seconds; }

        static NuiView view;
        static DeliveryJob job;
        static int period, page;
        static bool previous;
        static float runSeconds;
        static List<Row> rows = new List<Row>();
        static int myRank;
        static float myBest;
        static string myTeam = "";
        static RectTransform content;

        public static NuiView View => view && UiManager.Instance && UiManager.Instance.IsOpen(view) ? view : null;
        public static IReadOnlyList<Row> Rows => rows;
        public static string MyTeam => myTeam;
        public static int Period => period;

        /// <summary>The job's list for a period (0 week, 1 month, 2 year); <paramref name="seconds"/> is the run just finished (0 = none).</summary>
        public static void Show(DeliveryJob j, int periodIndex, float seconds = 0f)
        {
            if (j == null) return;
            job = j; period = Mathf.Clamp(periodIndex, 0, 2); runSeconds = seconds; page = 0; previous = false;
            var ui = UiManager.Instance;
            if (!ui) return;
            if (!View)
            {
                view = ui.Open(DialogId);
                if (!view) return;
                AudioLibrary.PlaySfx("sfx/SID_SYSWIN_CREATE");
                ui.On("ID_QSRANKLIST_CLOSE", _ => Close());
                ui.On("ID_QSRANKLIST_SPACE", _ => Close());
                ui.On("ID_QSRANKLIST_TIMEATTACKSPACE", _ => Close());
                ui.On("ID_QSRANKLIST_PREVWEEK", _ => { previous = !previous; page = 0; Load(); });
                ui.On("ID_QSRANKLIST_PREV", _ => { page = Mathf.Max(0, page - 1); Render(); });
                ui.On("ID_QSRANKLIST_NEXT", _ => { if ((page + 1) * PageSize < rows.Count) page++; Render(); });
            }
            bool roo = Factions.IsRoo;
            view.SetVisible("ID_QSRANKLIST_TIMEATTACK_TITLE", roo);
            view.SetVisible("ID_QSRANKLIST_SPACE", !roo);
            view.SetVisible("ID_QSRANKLIST_TIMEATTACKSPACE", roo);
            view.SetText("ID_QSRANKLIST_TITLE", j.Title);
            Load();
        }

        public static void Close()
        {
            // after a finished job: arbeit_com and the medal by ranking place (0x558800; Team ROO gets timeattack_com)
            if (view && runSeconds > 0f) { GameEffects.ArbeitDone(myRank); runSeconds = 0f; }
            if (view && UiManager.Instance) { AudioLibrary.PlaySfx("sfx/SID_SYSWIN_DESTROY"); UiManager.Instance.Close(view); }
            view = null; content = null;
        }

        /// <summary>The period's label: 10904-10906 "This Week / Month / Year", 10901-10903 "(Last Week / Month / Year)".</summary>
        public static string PeriodText(int index, bool before) => GameTables.Text((before ? 10901 + index : 10904 + index).ToString());

        static void Load()
        {
            if (!View) return;
            view.SetText("ID_QSRANKLIST_PERIOD", PeriodText(period, previous));
            if (OnlineSession.Active)
            {
                var j = job;
                OnlineSession.Instance.Net.Request("qs.rank", new Dictionary<string, object> { ["job"] = j.Key, ["period"] = period, ["previous"] = previous }, m =>
                {
                    if (j != job || !View) return;
                    rows = new List<Row>();
                    foreach (var d in (m.L("rows") ?? new List<object>()).OfType<Dictionary<string, object>>())
                        rows.Add(new Row { Rank = d.I("rank"), Name = d.S("name") ?? "", Team = d.S("team") ?? "", Seconds = (float)d.N("seconds") });
                    myRank = m.I("myRank");
                    myBest = (float)m.N("myBest");
                    myTeam = m.S("myTeam") ?? "";
                    Render();
                });
                return;
            }
            // offline: the driver's own three best runs (MissionRecords), all-time; the earlier period has nothing
            var best = MissionRecords.Current.For(job.Key).Best;
            string me = PlayerProfile.Current?.Name ?? "Driver";
            myTeam = Teams.CrewName;
            rows = previous ? new List<Row>() : best.Select((b, i) => new Row { Rank = i + 1, Name = me, Team = myTeam, Seconds = b }).ToList();
            myRank = rows.Count > 0 ? 1 : 0;
            myBest = best.Count > 0 ? best[0] : 0;
            Render();
        }

        static void Render()
        {
            if (!View) return;
            var box = view.Control("ID_QSRANKLIST_LIST");
            if (box)
            {
                if (content) UnityEngine.Object.Destroy(content.gameObject);
                content = NuiView.NewRect("Rows", box.transform);
                content.anchoredPosition = new Vector2(0, 0);
                content.sizeDelta = new Vector2(540, 410);
                // the column heads of the dialog: rank 21, crew 117, driver 280, best 448 (2830016-19, relative to the list at x 1)
                for (int i = 0; i < PageSize; i++)
                {
                    int k = page * PageSize + i;
                    if (k >= rows.Count) break;
                    var r = rows[k];
                    float y = -i * 36 - 8;
                    bool mine = r.Name == PlayerProfile.Current?.Name;
                    var color = new Color32(0x30, 0x1C, 0x08, 0xFF);   // the list's FC1_1
                    if (mine)
                    {
                        // LIST_MYORDER (2830024): the driver's own row on a gold box
                        var mineBox = NuiView.NewRect("Mine", content);
                        mineBox.anchoredPosition = new Vector2(4, y + 7);
                        mineBox.sizeDelta = new Vector2(532, 36);
                        var img = mineBox.gameObject.AddComponent<Image>();
                        img.color = new Color32(0xF5, 0xC7, 0x25, 0xC0);
                        img.raycastTarget = false;
                    }
                    Cell(content, 20, y, 70, r.Rank.ToString(), color, TextAnchor.MiddleCenter);
                    Cell(content, 116, y, 186, string.IsNullOrEmpty(r.Team) ? "-" : r.Team, color, TextAnchor.MiddleCenter);
                    Cell(content, 279, y, 152, r.Name, color, TextAnchor.MiddleCenter);
                    Cell(content, 447, y, 100, MissionText.Clock(r.Seconds), color, TextAnchor.MiddleCenter);
                }
            }
            view.SetText("ID_QSRANKLIST_MYRANK", myRank > 0 ? myRank.ToString() : MissionText.Get("10907", Mathf.Max(100, rows.Count)));
            // MYTEAM: the driver's crew (the server's crews table online; the crew the client knows offline)
            view.SetText("ID_QSRANKLIST_MYTEAM", string.IsNullOrEmpty(myTeam) ? "-" : myTeam);
            view.SetText("ID_QSRANKLIST_MYRECORD", runSeconds > 0 ? MissionText.Clock(runSeconds) : "-");
            view.SetText("ID_QSRANKLIST_MYBESTRECORD", myBest > 0 ? MissionText.Clock(myBest) : "-");
            view.SetText("ID_QSRANKLIST_MYBESTRANK", myRank > 0 ? myRank.ToString() : "-");
        }

        static void Cell(Transform parent, float x, float y, float w, string text, Color color, TextAnchor align)
        {
            var t = MissionUi.Label(parent, new Vector2(x, y), new Vector2(w, 22), 13, color, align);
            t.fontStyle = FontStyle.Bold;
            t.text = text ?? "";
        }
    }
}
