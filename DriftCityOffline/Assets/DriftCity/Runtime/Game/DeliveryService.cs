using System;
using System.Collections.Generic;
using System.Linq;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.Game
{
    /// <summary>One row of World/Data/ArbeitClient.tdf as the client's loader (0x7093c0, 0x70a361) reads it.</summary>
    public sealed class DeliveryJob
    {
        public enum JobKind { Regular, Ex, Event }

        public string Id;              // col 0 "a_00001"; 'e' = event, 'g' = seasonal (loader type 1 / 2)
        /// <summary>The job's own key: the id, or "ex_" + id for the EX loops whose ids repeat regular jobs (a_00048-59).</summary>
        public string Key;
        public JobKind Kind;
        /// <summary>The season an event job belongs to (rebuild: Easter eggs, Fall Harvest, Halloween; None = always).</summary>
        public EventSeason Season;
        public int MaxLevel;           // col 1: 43 Easy, 61 Medium, 110 Hard/Special, 0 events (inferred: highest level offered)
        public string Item;            // col 2 "a_i_0001": the cargo model game\a_i_%04u.nif
        public string StartPost;       // col 3: the station (RQ house) post that offers it
        public string Title, Desc, Ment;   // cols 4-6
        public List<string> Points = new List<string>();   // cols 8-27, the route
        public readonly int[] Exp = new int[3], Mito = new int[3];   // gold, silver, bronze
        public readonly float[] Time = new float[3];                 // seconds to beat for each medal
        /// <summary>Cols 37 / 38: the fourth reward tier (record +0x224c[3] / +0x225c[3]), paid for a finish without a medal.</summary>
        public int ExpNone, MitoNone;
        public int Index;

        public string City => MissionPosts.CityOf(StartPost);
        public bool IsEvent => Kind != JobKind.Regular;

        /// <summary>1 gold, 2 silver, 3 bronze, 0 slower than the bronze time.</summary>
        public int MedalFor(float seconds) => seconds <= Time[0] ? 1 : seconds <= Time[1] ? 2 : seconds <= Time[2] ? 3 : 0;
    }

    /// <summary>
    /// The Delivery Service ("ARBEIT", later "Quick Service") of the mission stations, rebuilt from DriftCity.exe:
    /// <list type="bullet">
    /// <item>The station's ARBEIT button opens the delivery worker's lines in QUESTDIALOG (600000) beside the ARBEIT list
    /// (2810000): ID_ARBEIT_DESC lists the station's jobs, selecting one (0x55c600, message 0x702) shows the three medal
    /// lines "%d:%02d:%02d %dExp %dMito" (0x5582a0, format 0x86dbb4) and the three best times (10184). QUESTDIALOG's
    /// Accept (control 600009, 0x55e1e3) takes the selected job (0x55b6a0): refused with 10185 while one runs and 10186
    /// while another mission runs.</item>
    /// <item>Accepted (handler 0x6f6af0: SID_ARBEIT_ACCEPT, "[%s] Deliver Service accepted" 10133) the run shows
    /// ARBEITPLAYINFO (790000: title, best record, Restart, Detail) and the time-attack clock (820000).</item>
    /// <item>Each destination reached prints 10126 "[%s] Destination &lt;%s&gt; arrived." with SID_CALLSIGN_ACCEPT
    /// (0x6f4ef0); the last one completes the job (0x6f6ba0: SID_QUEST_SUCCESS, 10123, medal line 10134-10136);
    /// a failure prints 10137 (0x6f6db0) and offers a retry (10125, 0x6f6750).</item>
    /// <item>Detail opens MYARBEIT (2820000) whose Abandon asks 10189 and prints 10124.</item>
    /// </list>
    /// The online server judged arrivals, medals and payouts; offline the table's medal times and rewards apply, a
    /// destination counts within <see cref="ArrivalRadius"/>, and a run fails at <see cref="FailFactor"/> times the
    /// bronze time (both this rebuild's own values). Server mirror: DriftCityServer/src/economy.js kind "delivery".
    /// </summary>
    public sealed class DeliveryService : MonoBehaviour
    {
        public const int BoardId = 2810000, MyBoardId = 2820000, InfoId = 790000, ClockId = 820000;
        public float ArrivalRadius = 25f;
        public float FailFactor = 2f;

        /// <summary>The delivery being driven, or null.</summary>
        public static DeliveryService Active { get; private set; }
        /// <summary>The service of the current city (one per CitySession).</summary>
        public static DeliveryService Instance { get; private set; }

        public CitySession Session;
        public string City;
        public DeliveryJob Job { get; private set; }
        public int Next { get; private set; }
        /// <summary>Seconds since the job was accepted (the clock of 820000).</summary>
        public float Elapsed;
        public List<CityMapInfo.Icon> Route { get; private set; } = new List<CityMapInfo.Icon>();
        public bool Running => running;

        bool running;
        NuiView info, clock;
        Text routeText;
        MissionBeacon beacon;
        float routeRefresh;

        // ---------------- table ----------------
        static List<DeliveryJob> jobs;

        /// <summary>The regular jobs of ArbeitClient.tdf (events and the "EX" item jobs left out).</summary>
        public static List<DeliveryJob> Jobs
        {
            get
            {
                if (jobs != null) return jobs;
                jobs = new List<DeliveryJob>();
                var rows = MissionTables.Read("arbeitclient.tdf");
                for (int r = 0; r < rows.Count; r++)
                {
                    var row = rows[r];
                    if (row.Length < 39 || string.IsNullOrEmpty(row[0])) continue;
                    var j = new DeliveryJob
                    {
                        Index = r, Id = row[0].Trim(), MaxLevel = MissionTables.Int(row, 1), Item = row[2], StartPost = row[3].Trim(),
                        Title = row[4].Trim(), Desc = row[5].Trim(), Ment = row[6],
                    };
                    j.Kind = KindOf(j);
                    j.Key = j.Kind == DeliveryJob.JobKind.Ex ? "ex_" + j.Id : j.Id;
                    j.Season = SeasonOf(j);
                    if (j.Kind == DeliveryJob.JobKind.Regular)
                    {
                        // the 20 route columns are padded with a default point ("post_1st_y1101"): the route is the run of
                        // points sharing the first one's route prefix (post_Nth_yRR), stopping at a repeat
                        string first = row[8].Trim();
                        string prefix = first.Length > 2 && first.Contains("_y") ? first.Substring(0, first.Length - 2) : null;
                        for (int c = 8; c < 28; c++)
                        {
                            string p = row[c].Trim();
                            if (p.Length == 0 || j.Points.Contains(p)) break;
                            if (prefix != null && !p.StartsWith(prefix)) break;
                            j.Points.Add(p);
                        }
                    }
                    else
                    {
                        // event and EX rows: the server sent the destination count (0x7051b0); their times fit five stops, so
                        // the route is the first five distinct points of the station's city
                        for (int c = 8; c < 28 && j.Points.Count < 5; c++)
                        {
                            string p = row[c].Trim();
                            if (p.Length == 0 || j.Points.Contains(p) || MissionPosts.CityOf(p) != MissionPosts.CityOf(j.StartPost)) continue;
                            if (MissionPosts.Find(p) != null) j.Points.Add(p);
                        }
                    }
                    for (int m = 0; m < 3; m++)
                    {
                        j.Exp[m] = (int)MissionTables.Float(row, 28 + m * 3);
                        j.Mito[m] = (int)MissionTables.Float(row, 29 + m * 3);
                        j.Time[m] = MissionTables.Float(row, 30 + m * 3);
                    }
                    // the loader reads integers: "10.5" counts 10 (the server's parseInt does the same)
                    j.ExpNone = (int)MissionTables.Float(row, 37);
                    j.MitoNone = (int)MissionTables.Float(row, 38);
                    // a station start and a route that exists in the icon table; regular jobs also need their level cap
                    if (j.Points.Count == 0 || j.Time[2] <= 0) continue;
                    if (j.Kind == DeliveryJob.JobKind.Regular && j.MaxLevel <= 0) continue;
                    if (MissionPosts.Find(j.StartPost) == null || j.Points.Any(p => MissionPosts.Find(p) == null)) continue;
                    jobs.Add(j);
                }
                return jobs;
            }
        }

        /// <summary>A job by its key (the id; "ex_" + id for the EX loops).</summary>
        public static DeliveryJob FindJob(string key) => Jobs.FirstOrDefault(j => j.Key == key);

        /// <summary>Event (e/g ids, "[Event]" titles), EX (col 1 = 0, "... Delivery EX") or a regular job.</summary>
        public static DeliveryJob.JobKind KindOf(DeliveryJob j)
        {
            if (j.Id.StartsWith("e") || j.Id.StartsWith("g") || j.Title.StartsWith("[Event]")) return DeliveryJob.JobKind.Event;
            if (j.MaxLevel <= 0 && j.Title.EndsWith(" EX")) return DeliveryJob.JobKind.Ex;
            return j.MaxLevel <= 0 ? DeliveryJob.JobKind.Event : DeliveryJob.JobKind.Regular;
        }

        /// <summary>
        /// Rebuild rule for when an event job is offered: the Easter egg deliveries (e_v_*) with the Easter / Valentine
        /// season, Fall Harvest (g_event_autumn_*) with the harvest, Halloween (a_halloween*) with Halloween, the other events
        /// (Vicki's Mittron Cleanup a_00115-119, col 1 = 110 in the old data\arbeitclient.tdf) always.
        /// </summary>
        public static EventSeason SeasonOf(DeliveryJob j)
        {
            if (j.Kind != DeliveryJob.JobKind.Event) return EventSeason.None;
            if (j.Id.StartsWith("e_v_")) return EventSeason.Valentine;
            if (j.Id.StartsWith("g_event_autumn")) return EventSeason.Harvest;
            if (j.Id.StartsWith("a_halloween")) return EventSeason.Halloween;
            return EventSeason.None;
        }

        /// <summary>
        /// The jobs a station offers to a driver of <paramref name="level"/>: regular jobs up to their level cap, the EX loops
        /// for everyone, event jobs while their season runs; regular jobs first (the client sorts types 1/2 after type 0 with
        /// 0x702800). The exe's accept path 0x7051b0 refuses col 1 &lt; level, so this build never started the col 1 = 0 rows;
        /// the official server enabled the events with a data patch.
        /// </summary>
        public static List<DeliveryJob> JobsAt(string stationPost, int level) =>
            Jobs.Where(j => j.StartPost == stationPost && (j.MaxLevel <= 0 || level <= j.MaxLevel) &&
                            (j.Season == EventSeason.None || SeasonEvents.Is(j.Season)))
                .OrderBy(j => j.Kind == DeliveryJob.JobKind.Regular ? 0 : 1).ToList();

        // ---------------- entry point ----------------

        /// <summary>Adds the Delivery Service to a city session (GameFlow.LoadCity, after StationPanel.Attach).</summary>
        public static DeliveryService Attach(CitySession session, string city)
        {
            if (!session) return null;
            if (!session.TryGetComponent(out DeliveryService d)) d = session.gameObject.AddComponent<DeliveryService>();
            d.Session = session;
            d.City = city;
            Instance = d;
            return d;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            CloseBoard();
            Cleanup();
        }

        // ---------------- the station board: QUESTDIALOG + ARBEIT list ----------------
        static NuiView board;
        static List<DeliveryJob> boardJobs = new List<DeliveryJob>();
        static int selected;
        static Action boardClosed;
        static CityMapInfo.Icon boardStation;
        static readonly List<Image> rowBacks = new List<Image>();

        public static bool BoardOpen => board && UiManager.Instance && UiManager.Instance.IsOpen(board);
        public static NuiView Board => board;
        public static IReadOnlyList<DeliveryJob> BoardJobs => boardJobs;
        public static int Selected => selected;

        /// <summary>ENTRANCE "Delivery Service" (ID_ENTRANCE_RQHOUSE_ARBEIT): the station's job list.</summary>
        public static void OpenBoard(CitySession session, CityMapInfo.Icon station, Action closed = null)
        {
            if (Active) { Active.ShowDetail(closed); return; }
            if (ChaseMission.Active) { UiPopup.Message(MissionText.Get(Factions.DeliveryText("10621"))); closed?.Invoke(); return; }
            if (RaceMission.Active || OnlineRace.Active || PatrolMission.Active || RushTime.Joined || (VirtualChase.Active && VirtualChase.Active.State == VirtualChase.Phase.Running)) { UiPopup.Message(MissionText.Get(Factions.DeliveryText("10186"))); closed?.Invoke(); return; }
            // 0x55b320: before the Delivery Service is unlocked (quest manager +0x109: level 4, or the ARBEIT quest) the list
            // refuses with 10182 "You didn't complete the Delivery Service mission."
            var locked = QuestUnlocks.DeliveryRefusal(PlayerProfile.Current);
            if (locked != null) { UiPopup.Message(locked); closed?.Invoke(); return; }
            int level = PlayerProfile.Current?.Level ?? 1;
            var list = station == null ? new List<DeliveryJob>() : JobsAt(station.Id, level);
            if (list.Count == 0) { UiPopup.Message(MissionText.Get("10191")); closed?.Invoke(); return; }
            var ui = UiManager.Instance;
            CloseBoard();
            boardJobs = list;
            boardClosed = closed;
            boardStation = station;
            selected = 0;
            board = ui.Open(BoardId);
            if (!board) return;
            PrepareList(board, "ID_ARBEIT_");
            AudioLibrary.PlaySfx("sfx/SID_SYSWIN_CREATE");
            ui.On("ID_ARBEIT_CLOSE", _ => { AudioLibrary.PlaySfx("sfx/SID_SYSWIN_DESTROY"); CloseBoard(); });
            BuildRows(board, "ID_ARBEIT_DESC", boardJobs, i => Select(i));
            MissionUi.Script(list[0].Ment, list[0].Title, true, () => AcceptSelected(session), CloseBoard);
            Select(0);
        }

        static void Select(int i)
        {
            if (!board || boardJobs.Count == 0) return;
            selected = Mathf.Clamp(i, 0, boardJobs.Count - 1);
            for (int k = 0; k < rowBacks.Count; k++) if (rowBacks[k]) rowBacks[k].color = k == selected ? new Color32(0xC8, 0xC0, 0xB0, 0xFF) : new Color(1, 1, 1, 0.12f);
            var j = boardJobs[selected];
            ShowJob(board, "ID_ARBEIT_", j);
            MissionUi.ScriptLines(j.Ment, j.Title);
        }

        static void AcceptSelected(CitySession session)
        {
            if (boardJobs.Count == 0) return;
            var job = boardJobs[selected];
            if (Active) { UiPopup.Message(MissionText.Get("10185")); return; }
            if (ChaseMission.Active || RaceMission.Active || OnlineRace.Active || PatrolMission.Active || RushTime.Joined || (VirtualChase.Active && VirtualChase.Active.State == VirtualChase.Phase.Running) ||
                StepQuests.Running(PlayerProfile.Current) != null) { UiPopup.Message(MissionText.Get(Factions.DeliveryText("10186"))); return; }
            CloseBoard();
            var d = Instance ? Instance : Attach(session ? session : CitySession.Current, job.City);
            if (d) d.Begin(job);
        }

        public static void CloseBoard()
        {
            MissionUi.CloseScript();
            if (board && UiManager.Instance) UiManager.Instance.Close(board);
            board = null;
            rowBacks.Clear();
            var cb = boardClosed; boardClosed = null;
            cb?.Invoke();
        }

        /// <summary>Hides what our offline list doesn't use: online rankings, the hall of fame, the medal art templates.</summary>
        static void PrepareList(NuiView v, string p)
        {
            foreach (var id in new[] { "HALLOFFAME", "MEDAL_GOLD", "MEDAL_SILVER", "MEDAL_BRONZE" })
                v.SetVisible(p + id, false);
            // the rankings (2810013-15 week / month / all, 0x557540): QSRANKLIST for the selected job
            var ui = UiManager.Instance;
            string[] periods = { "SHOWWEEK_RANKLIST", "SHOWMONTH_RANKLIST", "SHOWENTIRE_RANKLIST" };
            for (int k = 0; k < 3; k++)
            {
                int period = k;
                if (!v.Control(p + periods[k])) continue;
                v.SetVisible(p + periods[k], true);
                ui.On(p + periods[k], _ =>
                {
                    var j = p == "ID_MYARBEIT_" ? Active?.Job : boardJobs.Count > 0 ? boardJobs[Mathf.Clamp(selected, 0, boardJobs.Count - 1)] : null;
                    if (j != null) QsRankList.Show(j, period);
                });
            }
        }

        /// <summary>Fills the ID_*_DESC list box (334 x 80, 22 px rows, LI=3) with job rows that scroll with the wheel.</summary>
        static void BuildRows(NuiView v, string listId, List<DeliveryJob> list, Action<int> click)
        {
            rowBacks.Clear();
            var box = v.Control(listId);
            if (!box) return;
            var viewport = NuiView.NewRect("Rows", box.transform);
            viewport.anchoredPosition = new Vector2(2, -2);
            viewport.sizeDelta = new Vector2(330, 76);
            viewport.gameObject.AddComponent<RectMask2D>();
            var hit = viewport.gameObject.AddComponent<Image>(); hit.color = new Color(0, 0, 0, 0.001f);
            var content = NuiView.NewRect("Content", viewport);
            content.sizeDelta = new Vector2(312, Mathf.Max(76, list.Count * 24));
            var scroll = viewport.gameObject.AddComponent<ScrollRect>();
            scroll.viewport = viewport; scroll.content = content; scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped; scroll.scrollSensitivity = 24f;
            var records = MissionRecords.Current;
            var medalArt = new[] { MissionUi.Image(v, "ID_ARBEIT_MEDAL_GOLD"), MissionUi.Image(v, "ID_ARBEIT_MEDAL_SILVER"), MissionUi.Image(v, "ID_ARBEIT_MEDAL_BRONZE") };
            for (int i = 0; i < list.Count; i++)
            {
                int index = i;
                var row = NuiView.NewRect("Row " + list[i].Id, content);
                row.anchoredPosition = new Vector2(0, -i * 24);
                row.sizeDelta = new Vector2(312, 22);
                var back = row.gameObject.AddComponent<Image>();
                back.color = new Color(1, 1, 1, 0.12f);
                rowBacks.Add(back);
                var btn = row.gameObject.AddComponent<Button>();
                btn.transition = Selectable.Transition.None;
                btn.onClick.AddListener(() => { UiSounds.Click(); click(index); });
                var label = MissionUi.Label(row, new Vector2(6, -2), new Vector2(270, 20), 12, Color.black, TextAnchor.MiddleLeft);
                label.text = list[i].Title;
                int medal = records.For(list[i].Key).Medal;
                if (medal > 0 && medalArt[medal - 1])
                {
                    var m = NuiView.NewRect("Medal", row);
                    m.anchoredPosition = new Vector2(290, 0); m.sizeDelta = new Vector2(17, 22);
                    var img = m.gameObject.AddComponent<RawImage>();
                    img.texture = medalArt[medal - 1].texture; img.uvRect = medalArt[medal - 1].uvRect; img.raycastTarget = false;
                }
            }
        }

        /// <summary>0x5582a0: the medal lines (time limit, EXP, Mito) and the three best times of a job.</summary>
        static void ShowJob(NuiView v, string p, DeliveryJob j)
        {
            string[] medals = { "GOLD", "SILVER", "BRONZE" };
            for (int m = 0; m < 3; m++)
            {
                int ms = Mathf.RoundToInt(j.Time[m] * 1000f), s = ms / 1000;
                v.SetText(p + "REWARDTEXT_MEDAL_" + medals[m], MissionText.Printf("%d:%02d:%02d %dExp %dMito", s / 60, s % 60, (ms % 1000) * 3 / 50, j.Exp[m], j.Mito[m]));
            }
            var best = MissionRecords.Current.For(j.Key).Best;
            string name = PlayerProfile.Current?.Name ?? "Driver";
            for (int i = 0; i < 3; i++)
            {
                string text = "-";
                if (i < best.Count) { int s = Mathf.FloorToInt(best[i]); text = MissionText.Get("10184", name, s / 60, s % 60); }
                v.SetText(p + "BEST" + i, text);
            }
            // "Time Attack" is Team ROO's name for the service (0x55b320, faction 0x6cb0c0()+0xed)
            v.SetVisible(p + "TIMEATTACK_TITLE", Factions.IsRoo);
        }

        // ---------------- the run ----------------

        public void Begin(DeliveryJob job)
        {
            Cleanup();
            Job = job;
            Route = job.Points.Select(MissionPosts.Find).Where(i => i != null).ToList();
            if (Route.Count == 0) { Job = null; return; }
            Next = 0;
            Elapsed = 0;
            running = true;
            Active = this;
            var rec = MissionRecords.Current;
            rec.For(job.Key).Runs++;
            rec.Save();
            AudioLibrary.PlaySfx("sfx/SID_ARBEIT_ACCEPT");
            Net.ChatBox.System(MissionText.Get(Factions.DeliveryText("10133"), job.Title));

            var ui = UiManager.Instance;
            if (NewLayout) { OpenNewLayout(job, rec); }
            else info = ui.Open(InfoId);
            if (info && !NewLayout)
            {
                info.SetVisible("ID_ARBEITPLAYINFO_TIMEATTACK", Factions.IsRoo);
                info.SetVisible("ID_ARBEITPLAYINFO_ARBEIT", !Factions.IsRoo);
                info.SetText("ID_ARBEITPLAYINFO_TITLE", job.Title);
                var best = rec.For(job.Key).Best;
                info.SetText("ID_ARBEITPLAYINFO_BEST", best.Count > 0 ? $"{PlayerProfile.Current?.Name} {MissionText.Clock(best[0])}" : "-");
                var box = info.Control("ID_ARBEITPLAYINFO_BOXBASE");
                if (box) routeText = MissionUi.Label(box.transform, new Vector2(8, -6), new Vector2(165, 118), 12, Color.white);
                ui.On("ID_ARBEITPLAYINFO_RETRY", _ => MissionUi.Confirm(MissionText.Get("10125"), Restart));
                ui.On("ID_ARBEITPLAYINFO_VIEWQUEST", _ => ShowDetail());
            }
            clock = ui.Open(ClockId);
            if (clock) for (int d = 0; d < 10; d++) clock.SetVisible("ID_ARBEITPLAYINFO_TIME_NUM_" + d, false);
            MissionUi.TimeDigits(clock, 0);
            beacon = MissionBeacon.Create("Delivery Beacon", @"game\cylinder_yellow.nif", new Color(1f, 0.8f, 0.1f, 0.35f));
            beacon.MoveTo(Route[0].Position);
            RefreshRoute();
        }

        void Update()
        {
            var car = GameFlow.Instance ? GameFlow.Instance.PlayerCar : (Session ? Session.Car : null);
            // driving away from the station puts the list away, as the station panel goes
            if (!running && car && boardStation != null && BoardOpen && MissionPosts.Flat(car.transform.position, boardStation.Position) > 80f) CloseBoard();
            if (!running || !car) return;
            // a chase or race took the car over: the delivery is given up
            if (ChaseMission.Active || RaceMission.Active || OnlineRace.Active || RushTime.Joined) { Abandon(); return; }
            Elapsed += Time.deltaTime;
            MissionUi.TimeDigits(clock, Elapsed);
            var target = Route[Next];
            var p = car.transform.position;
            if (MissionPosts.Flat(p, target.Position) < ArrivalRadius && Mathf.Abs(p.y - target.Position.y) < 40f) { Arrive(); return; }
            if (Elapsed > Job.Time[2] * FailFactor) { Fail(); return; }
            routeRefresh -= Time.deltaTime;
            if (routeRefresh <= 0) RefreshRoute();
        }

        void RefreshRoute()
        {
            routeRefresh = 0.25f;
            if (!routeText || Job == null) return;
            var car = GameFlow.Instance ? GameFlow.Instance.PlayerCar : null;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < Route.Count; i++)
            {
                string mark = i < Next ? "v " : i == Next ? "> " : "   ";
                sb.Append(mark).Append(Route[i].Name);
                if (i == Next && car) sb.Append($"  {MissionPosts.Flat(car.transform.position, Route[i].Position):F0} m");
                sb.Append('\n');
            }
            routeText.text = sb.ToString();
        }

        void Arrive()
        {
            var reached = Route[Next];
            AudioLibrary.PlaySfx("sfx/SID_CALLSIGN_ACCEPT");
            Net.ChatBox.System(MissionText.Get("10126", Job.Title, reached.Name));
            Next++;
            if (Next >= Route.Count) { Complete(); return; }
            beacon.MoveTo(Route[Next].Position);
            RefreshRoute();
        }

        void Complete()
        {
            running = false;
            var job = Job;
            float seconds = Elapsed;
            int medal = job.MedalFor(seconds);
            var rec = MissionRecords.Current;
            bool newBest = rec.AddTime(job.Key, seconds, medal);
            rec.Save();
            AudioLibrary.PlaySfx("sfx/SID_QUEST_SUCCESS");
            Net.ChatBox.System(MissionText.Get("10123", job.Title));
            RefreshRoute();
            if (beacon) beacon.Hide();
            string time = $"\nTime {MissionText.Clock(seconds)}" + (newBest ? "  (new best)" : "");
            // a new record: QUEST 1770000 with qs_newrecord (0x5531e0; the server's rank < 2, here the driver's own best)
            if (newBest && rec.For(job.Key).Completed > 1) ChaseScreens.NewRecord(seconds);
            // no medal: cols 37/38, the fourth tier ("Exp : %d Mito : %d" for medal 0, 0x6f6ba0); nothing when they are 0
            bool paid = medal > 0 || job.ExpNone > 0 || job.MitoNone > 0;
            if (!paid || PlayerProfile.Current == null)
            {
                UiPopup.Message(MissionText.Get("10123", job.Title) + "\n" + MissionText.Printf("Exp. %d Mito. %d", 0, 0) + time +
                                $"\nNo medal: slower than {MissionText.Clock(job.Time[2])}.");
                // packet 0x146 -> 0x4f4990: the job's rank list comes up with the run's time (QSRANKLIST 2830000)
                if (ShowRankOnFinish) QsRankList.Show(job, 0, seconds);
            }
            else
            {
                var p = PlayerProfile.Current;
                var data = new Dictionary<string, object> { ["job"] = job.Key, ["seconds"] = (double)seconds, ["medal"] = medal };
                CouponBook.Count(CouponBook.Cat.Delivery);
                Progress.Reward("delivery", data, () =>
                {
                    long exp = medal > 0 ? job.Exp[medal - 1] : job.ExpNone, mito = medal > 0 ? job.Mito[medal - 1] : job.MitoNone;
                    exp = MitaDrink.Apply(exp);
                    int before = p.Level;
                    p.AddExp(exp); p.Mito += mito;
                    return new Progress.Result { Ok = true, Exp = exp, Mito = mito, LevelUps = p.Level - before };
                }, r =>
                {
                    if (r.Ok) ChaseScreens.RewardPopup(r.Exp, r.Mito);   // 0x6f6430 / 0x6f6560 -> 0x5612a0
                    // packet 0x146 -> 0x4f4990: the job's rank list (QSRANKLIST 2830000), after the server has the time
                    if (ShowRankOnFinish) QsRankList.Show(job, 0, seconds);
                    string medalLine = medal == 0 ? MissionText.Printf("Exp : %d Mito : %d", r.Exp, r.Mito) : MissionText.Get(medal == 1 ? "10134" : medal == 2 ? "10135" : "10136", r.Exp, r.Mito);
                    UiPopup.Message(r.Ok
                        ? MissionText.Get("10123", job.Title) + "\n" + medalLine + time +
                          (r.LevelUps > 0 ? $"\nLevel up! You are now level {PlayerProfile.Current.Level}." : "")
                        : MissionText.Get("10123", job.Title) + $"\n(no reward: {r.Error})" + time);
                });
            }
            Finished?.Invoke(medal);
            // the clock stays on the final time for a moment, as the client's result screen
            Invoke(nameof(Cleanup), 3f);
        }

        /// <summary>QSRANKLIST after every finished job (the server's packet 0x146); tests may turn it off.</summary>
        public static bool ShowRankOnFinish = true;

        /// <summary>Raised when a job completes, with its medal (0 = none).</summary>
        public event Action<int> Finished;

        void Fail()
        {
            var job = Job;
            AudioLibrary.PlaySfx("sfx/SID_QUEST_CANCEL");
            Net.ChatBox.System(MissionText.Get("10137", job.Title));
            GameEffects.ArbeitFailed();   // arbeit_fail (Team ROO: timeattack_fail)
            Cleanup();
            MissionUi.Confirm(MissionText.Get("10137", job.Title) + "\n" + MissionText.Get("10125"), () => { RestartAt(job); });
        }

        /// <summary>ARBEITPLAYINFO Restart: back to the station and the clock from zero.</summary>
        public void Restart()
        {
            if (Job == null) return;
            RestartAt(Job);
        }

        void RestartAt(DeliveryJob job)
        {
            var car = GameFlow.Instance ? GameFlow.Instance.PlayerCar : null;
            var start = MissionPosts.Find(job.StartPost);
            var first = MissionPosts.Find(job.Points[0]);
            if (car && start != null)
            {
                var dir = first != null ? first.Position - start.Position : car.transform.forward;
                dir.y = 0;
                RaceMission.Teleport(car, RaceMission.Ground(start.Position), Quaternion.LookRotation(dir.sqrMagnitude > 1 ? dir.normalized : Vector3.forward, Vector3.up));
            }
            Begin(job);
        }

        /// <summary>Gives the running job up (MYARBEIT Abandon, or a chase/race taking over).</summary>
        public void Abandon()
        {
            if (Job == null) return;
            AudioLibrary.PlaySfx("sfx/SID_QUEST_CANCEL");
            Net.ChatBox.System(MissionText.Get("10124", Job.Title));
            Cleanup();
        }

        /// <summary>ARBEITPLAYINFO Detail: MYARBEIT (2820000) with the running job, Abandon and Close.</summary>
        public void ShowDetail(Action closed = null)
        {
            if (Job == null) { closed?.Invoke(); return; }
            var ui = UiManager.Instance;
            var old = ui.Find(MyBoardId);
            if (old) ui.Close(old);
            var v = ui.Open(MyBoardId);
            if (!v) { closed?.Invoke(); return; }
            // the station panel shares the right side of the screen: it steps aside while the log is open
            var entrance = ui.Find(400000);
            if (entrance && entrance.gameObject.activeSelf && closed == null)
            {
                entrance.gameObject.SetActive(false);
                closed = () => { if (entrance) entrance.gameObject.SetActive(true); };
            }
            detailClosed = closed;
            PrepareList(v, "ID_MYARBEIT_");
            ShowJob(v, "ID_MYARBEIT_", Job);
            BuildRows(v, "ID_MYARBEIT_DESC", new List<DeliveryJob> { Job }, _ => { });
            if (rowBacks.Count > 0) rowBacks[0].color = new Color32(0xC8, 0xC0, 0xB0, 0xFF);
            ui.On("ID_MYARBEIT_CLOSE", view => { AudioLibrary.PlaySfx("sfx/SID_SYSWIN_DESTROY"); ui.Close(view); DetailClosed(); });
            ui.On("ID_MYARBEIT_GIVEUP", view =>
            {
                if (!running) { UiPopup.Message(MissionText.Get(Factions.DeliveryText("10188"))); return; }
                MissionUi.Confirm(MissionText.Get(Factions.DeliveryText("10189")), () => { ui.Close(view); Abandon(); });
            });
        }

        Action detailClosed;

        void DetailClosed()
        {
            var cb = detailClosed; detailClosed = null;
            cb?.Invoke();
        }

        void Cleanup()
        {
            CancelInvoke(nameof(Cleanup));
            running = false;
            if (Active == this) Active = null;
            var ui = UiManager.Instance;
            if (ui)
            {
                if (info) ui.Close(info);
                if (adv) ui.Close(adv);
                if (clock) ui.Close(clock);
                var my = ui.Find(MyBoardId);
                if (my) ui.Close(my);
            }
            DetailClosed();
            info = clock = adv = null;
            routeText = null;
            if (beacon) Destroy(beacon.gameObject);
            beacon = null;
        }

        /// <summary>
        /// The next destination for the minimap (ID_MINIMAP_ICON_ARBEIT); an event job shows every stop still to reach with
        /// the event icon (type 0xd instead of 6, 0x5fdf40 / 0x6005b2).
        /// </summary>
        public IEnumerable<MissionMarkers.Marker> Markers()
        {
            if (!running || Job == null || Next >= Route.Count) yield break;
            if (Job.Kind == DeliveryJob.JobKind.Event)
            {
                for (int i = Next; i < Route.Count; i++)
                    yield return new MissionMarkers.Marker { Icon = "ID_MINIMAP_ICON_EVENT", Position = Route[i].Position, Label = Route[i].Name };
                yield break;
            }
            yield return new MissionMarkers.Marker { Icon = "ID_MINIMAP_ICON_ARBEIT", Position = Route[Next].Position, Label = Route[Next].Name };
        }

        // ---------------- ARBEITPLAYINFONEW 800000 + ARBEITPLAYINFOADV 810000 ----------------

        /// <summary>
        /// The newer run HUD (0x55d950 with the server flag net+0x267 &amp; 1): 800000 (QS_BASE "Quick Service" header, or
        /// TA_BASE for Team ROO; TITLE; the cargo BASE box; RETRY asks 10908; DETAIL toggles MYARBEIT) with 810000's best
        /// record line. Off by default (the flag is the server's); the clock 820000 is shared.
        /// </summary>
        public static bool NewLayout;
        public const int NewInfoId = 800000, NewAdvId = 810000;
        NuiView adv;

        void OpenNewLayout(DeliveryJob job, MissionRecords rec)
        {
            var ui = UiManager.Instance;
            info = ui.Open(NewInfoId);
            MissionUi.ApplyDialogOffset(info);
            if (info)
            {
                info.SetVisible("ID_ARBEITPLAYINFONEW_QS_BASE", !Factions.IsRoo);
                info.SetVisible("ID_ARBEITPLAYINFONEW_TA_BASE", Factions.IsRoo);
                info.SetText("ID_ARBEITPLAYINFONEW_TITLE", job.Title);
                var box = info.Control("ID_ARBEITPLAYINFONEW_BASE");
                if (box) routeText = MissionUi.Label(box.transform, new Vector2(6, -4), new Vector2(150, 112), 11, Color.white);
                ui.On("ID_ARBEITPLAYINFONEW_RETRY", _ => MissionUi.Confirm(MissionText.Get("10908"), Restart));
                ui.On("ID_ARBEITPLAYINFONEW_DETAIL", _ =>
                {
                    var open = ui.Find(MyBoardId);
                    if (open) { AudioLibrary.PlaySfx("sfx/SID_SYSWIN_DESTROY"); ui.Close(open); DetailClosed(); }
                    else { AudioLibrary.PlaySfx("sfx/SID_SYSWIN_CREATE"); ShowDetail(); }
                });
            }
            adv = ui.Open(NewAdvId);
            MissionUi.ApplyDialogOffset(adv);
            if (adv)
            {
                var best = rec.For(job.Key).Best;
                adv.SetText("ID_ARBEITPLAYINFOADV_BEST_FRONT", best.Count > 0 ? PlayerProfile.Current?.Name ?? "" : "-");
                adv.SetText("ID_ARBEITPLAYINFOADV_BEST", best.Count > 0 ? MissionText.Clock(best[0]) : "00'00\"00");
            }
        }
    }
}
