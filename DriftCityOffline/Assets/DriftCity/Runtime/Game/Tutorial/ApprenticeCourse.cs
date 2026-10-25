using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Core;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.Game
{
    /// <summary>
    /// The client's "Tutorial mode", the apprentice course (DriftCity_RE/TUTORIAL.md): the data and rules.
    /// World/Data/Apprentice_Client.tdf (loader 0x706d70 -> quest manager +0xdc, 0x57c2d0 picks a row) holds the 13 scripts;
    /// the course is the pvptrackinfo row "moonpalace Tutorial" (Id 74, CityType 3, 7 gates, point to point, 1 lap); the
    /// grades come from globalcontent ApprenticeLapResultGood / Normal / NotBad (US 130 / 135 / 140 s, read at 0x72b398);
    /// the reward cars are the vehiclelist rows with Sellable 10001..40001 (CREATECAR 4160000, list 0x749530).
    /// </summary>
    public static class Apprentice
    {
        /// <summary>Script rows (the step value the client stores at apprentice object +0x48 when it shows them, 0x72b600).</summary>
        public const int Intro = 0, Jason = 1, Lesson = 2, DriftFailed = 3, DriftPassed = 4, BoosterLesson = 5, BoosterPassed = 6,
            BoosterFailed = 7, Retry = 8, Excellent = 9, Impressive = 10, Training = 11, Erina = 12;
        /// <summary>The full-course run (no script; 0x72f190 turns step 6 into 8 when the course restarts).</summary>
        public const int FullCourse = 8;
        /// <summary>A skipped course is claimed with this time and grade 3 (0x57cdd0: +0x60 = 3, +0x64 = 1000.0).</summary>
        public const float SkippedSeconds = 1000f;
        /// <summary>A drift counts when it lasted this long (0x72fe00 compares +0x54 with 0.5001).</summary>
        public const float DriftSeconds = 0.5001f;
        /// <summary>The booster test sets the gauge to 97 at gate 2 and passes when it is below 97 at gate 3 (0x72fe00).</summary>
        public const float BoosterGauge = 0.97f;
        /// <summary>Lesson timeouts (0x7300a0): gate 1 by 12 s, gate 2 by 22 s; after a failure the run starts over at 14 / 24 s.</summary>
        public const float Gate1Limit = 12f, Gate2Limit = 22f, Gate1Restart = 14f, Gate2Restart = 24f;
        /// <summary>After the booster lines close the course waits this long (0x7300a0 steps 6 / 7: 0.6 s).</summary>
        public const float AfterDialog = 0.6f;
        public const string City = "moonpalace";

        static List<string> scripts;
        static float[] grades;
        static List<(VehicleInfo car, int sellable)> rewardCars;

        /// <summary>The script row's markup (empty when the table is missing).</summary>
        public static string Script(int row)
        {
            if (scripts == null)
            {
                scripts = new List<string>();
                foreach (var r in MissionTables.Read("apprentice_client.tdf")) scripts.Add(MissionTables.Str(r, 0));
            }
            return row >= 0 && row < scripts.Count ? scripts[row] : "";
        }

        public static int ScriptCount { get { Script(0); return scripts.Count; } }

        /// <summary>ApprenticeLapResultGood / Normal / NotBad (Value_US: 130 / 135 / 140).</summary>
        public static float[] GradeSeconds
        {
            get
            {
                if (grades != null) return grades;
                grades = new[] { 130f, 135f, 140f };
                try
                {
                    var fs = DriftCityData.Files;
                    if (fs != null && fs.Exists(@"localize\globalcontent.xlt"))
                    {
                        var t = XltTable.Parse(fs.Read(@"localize\globalcontent.xlt"));
                        string[] keys = { "ApprenticeLapResultGood", "ApprenticeLapResultNormal", "ApprenticeLapResultNotBad" };
                        foreach (var r in t.Rows)
                        {
                            int k = Array.IndexOf(keys, t.Get(r, "Key"));
                            if (k >= 0 && float.TryParse(t.Get(r, "Value_US"), out float v) && v > 0) grades[k] = v;
                        }
                    }
                }
                catch (Exception ex) { Debug.LogWarning("apprentice grades: " + ex.Message); }
                return grades;
            }
        }

        /// <summary>0x72ab10: under Good 0, under Normal 1, under NotBad 2, else 3.</summary>
        public static int Grade(float seconds)
        {
            var g = GradeSeconds;
            return seconds < g[0] ? 0 : seconds < g[1] ? 1 : seconds < g[2] ? 2 : 3;
        }

        /// <summary>The result lines: 9 "Excellent!", 10 "not as impressive" (grades 1 and 2), 11 "not everyone has talent".</summary>
        public static int GradeScript(int grade) => grade <= 0 ? Excellent : grade >= 3 ? Training : Impressive;

        /// <summary>The reward cars in the order of the pick screen (0x749530 keeps the rows with Sellable &gt;= 10000, sorted).</summary>
        public static List<(VehicleInfo car, int sellable)> RewardCars
        {
            get
            {
                if (rewardCars != null) return rewardCars;
                rewardCars = new List<(VehicleInfo, int)>();
                try
                {
                    GameTables.Load();
                    var vl = XltTable.Parse(DriftCityData.Files.Read(@"init\vehiclelist.xlt"), headerRow: 3);
                    foreach (var r in vl.Rows)
                    {
                        if (vl.Get(r, "Desc") != "PlayerCar" || vl.Get(r, "Support") != "1") continue;
                        int sell = vl.GetInt(r, "Sellable");
                        if (sell < 10000) continue;
                        var car = GameTables.PlayerCars.FirstOrDefault(v => v.Index == vl.GetInt(r, "Index"));
                        if (car != null && rewardCars.All(x => x.car.Index != car.Index)) rewardCars.Add((car, sell));
                    }
                    rewardCars.Sort((a, b) => a.sellable.CompareTo(b.sellable));
                }
                catch (Exception ex) { Debug.LogWarning("apprentice reward cars: " + ex.Message); }
                return rewardCars;
            }
        }

        /// <summary>
        /// 0x747bb0: what keeps a reward car from a grade, or null. Grade 0 takes any car; grade 1 not the 1xxxx car ("Tutorial
        /// Record: Less than 2 minutes and 10 seconds", 11299), grade 2 nor the 2xxxx car (11300), grade 3 nor the 3xxxx car
        /// (11301): each with 11298 "%s Please try again." (the NEXT button is disabled then).
        /// </summary>
        public static string LockText(int grade, int sellable)
        {
            string key = null;
            if (grade >= 1 && sellable < 20000) key = "11299";
            else if (grade >= 2 && sellable < 30000) key = "11300";
            else if (grade >= 3 && sellable < 40000) key = "11301";
            if (key == null) return null;
            // 11298 is "%s Please try again." in the KR column; the US text drops the %s
            string again = GameTables.Text("11298"), why = GameTables.Text(key);
            return again.Contains("%s") ? again.Replace("%s", why) : why + "\n" + again;
        }

        public static bool Allowed(int grade, int sellable) => sellable >= 10000 && LockText(grade, sellable) == null;

        /// <summary>The course: the CityType 3 row of Moon Palace (its line runs start -> gates -> finish).</summary>
        public static RaceTrack Track => RaceMission.TracksFor(City).FirstOrDefault(t => t.CityType == 3);

        /// <summary>The client's apprentice bit (net+0x24c &amp; 0x80000): a new driver until the reward car is picked.</summary>
        public static bool IsApprentice(PlayerProfile p) => p != null && p.Apprentice;

        /// <summary>
        /// 0x6cb790: the current car is the tutorial car (record kind 3, `char + 0xd54 * 0x31 + 0x125`). The server lent it to a
        /// new US driver (ApprenticeFlowType 1); the remake has no separate loan car, so the apprentice's first car (the one
        /// picked at creation) is the tutorial car until the reward replaces it [remake].
        /// </summary>
        public static bool OnTutorialCar(PlayerProfile p) => IsApprentice(p) && p.Cars != null && p.Cars.Count > 0 && p.CurrentCar == 0;

        /// <summary>
        /// 0x763f30: on the tutorial car every parking-lot zone but the Battle Zone building refuses with 11285 "You cannot
        /// access during Tutorial" and the car goes back to its last free spot (0x7629b0).
        /// </summary>
        public static bool Refuses(PlayerProfile p, ParkingLotStage.Area area) =>
            area != ParkingLotStage.Area.None && area != ParkingLotStage.Area.Battle && OnTutorialCar(p);

        /// <summary>Text 11285 "You cannot access during Tutorial".</summary>
        public static string RefusalText => GameTables.Text("11285");

        /// <summary>
        /// 0x762f70's special arrival spots (Gamebryo x, y and heading) for a driver on the tutorial car: (0.5, -0.8) heading
        /// 0.165 pi (the dome centre, facing the Battle Zone building at about (102, 57)); with step 3 or 7 (a failed lesson,
        /// 0x762ca0) (75, 45), by the Battle Zone. Null for everyone else (the six gate arrivals).
        /// </summary>
        public static (Vector2 pos, float height, float heading)? ArrivalSpot(PlayerProfile p, int step)
        {
            if (!OnTutorialCar(p)) return null;
            if (step == DriftFailed || step == BoosterFailed)
                return (new Vector2(75f, 45f), 0f, Mathf.Atan2(57f - 45f, 102f - 75f));   // heading toward the building [inferred]
            return (new Vector2(0.5f, -0.8f), 2.5f, Mathf.PI * 0.165f);
        }

        /// <summary>"m:ss" as the pick screen's "My Record" (11321 with %d:%02d; nothing for a skipped course).</summary>
        public static string Record(float seconds)
        {
            if (seconds > 999f) return "";
            int s = Mathf.FloorToInt(seconds);
            return $"{GameTables.Text("11321")} {s / 60}:{s % 60:00}";
        }

        /// <summary>
        /// The reward car (the client's packet 0x79f0a0 with the time, car and colour; the server answers with the car, 0x4ef1a0).
        /// Online the server checks it (src/economy.js kind "apprentice": once per driver, the grade from the time, the car
        /// allowed by the grade); offline the same rule runs here.
        /// </summary>
        public static void Claim(float seconds, VehicleInfo car, int sellable, int color, Action<Progress.Result> done)
        {
            var data = new Dictionary<string, object> { ["seconds"] = (double)seconds, ["vehicleIndex"] = car.Index, ["color"] = color };
            Progress.Reward("apprentice", data, () => ClaimOffline(PlayerProfile.Current, seconds, car.Index, sellable, color), done);
        }

        public static Progress.Result ClaimOffline(PlayerProfile p, float seconds, int vehicleIndex, int sellable, int color)
        {
            if (p == null) return new Progress.Result { Error = "no driver" };
            if (!p.Apprentice) return new Progress.Result { Error = "You have already received the tutorial reward." };
            int grade = seconds >= SkippedSeconds - 1f ? 3 : Grade(seconds);
            if (!Allowed(grade, sellable)) return new Progress.Result { Error = LockText(grade, sellable) ?? "That car is not a tutorial reward." };
            if (p.Cars.Count >= 40) return new Progress.Result { Error = "garage full" };
            p.Cars.Add(new OwnedCar { VehicleIndex = vehicleIndex, Color = Mathf.Clamp(color, 0, 3) });
            p.CurrentCar = p.Cars.Count - 1;
            p.Apprentice = false;
            if (seconds < SkippedSeconds - 1f && (p.ApprenticeBest <= 0 || seconds < p.ApprenticeBest)) p.ApprenticeBest = seconds;
            return new Progress.Result { Ok = true };
        }

        /// <summary>Online: the server notes when the course starts (a claimed time can't be shorter than the drive).</summary>
        public static void ReportStart()
        {
            if (!OnlineSession.Active) return;
            OnlineSession.Instance.Net.Request("reward", new Dictionary<string, object> { ["kind"] = "apprentice", ["data"] = new Dictionary<string, object> { ["start"] = true } }, null);
        }

        /// <summary>Tests: forget the cached tables.</summary>
        public static void ResetCache() { scripts = null; grades = null; rewardCars = null; }
    }

    /// <summary>
    /// The apprentice course run (the client runs it as a one-driver local PvP race on the tutorial track: 0x578d90 joins the
    /// track, 0x72f190 starts it, 0x72f3c0 counts the gates and asks 0x72fe00 which line to show, 0x7300a0 checks the lesson
    /// each frame, 0x72ab10 grades the finish). Steps (apprentice object +0x48): 2 lesson, 3 drift failed, 5 booster lesson,
    /// 6 booster passed, 7 booster failed, 8 full course, 9..11 graded; then the reward pick (CREATECAR 4160000) and back to the
    /// parking lot, where Erina sends the driver to a city. The HUD is APPRENTICE 830000 (time and drift), 840000 (metres to
    /// the finish) and 850000 (key hints).
    /// </summary>
    public sealed class ApprenticeCourse : MonoBehaviour
    {
        public static ApprenticeCourse Active { get; private set; }
        /// <summary>Seconds of the countdown before GO (the client's room countdown is 6 s; tests shorten it).</summary>
        public static float CountdownSeconds = 6f;
        /// <summary>For the lead's GameFlow.OnLoginComplete hook: a new driver arrives in the parking lot, as the client's US
        /// flow (ApprenticeFlowType 1), instead of Cras.</summary>
        public static bool SendNewDriversToLot = true;
        /// <summary>The last finished run (tests): time and grade.</summary>
        public static float LastSeconds { get; private set; }
        public static int LastGrade { get; private set; } = -1;
        /// <summary>Set once a run ended with the reward: the next parking-lot arrival shows Erina's line 12.</summary>
        public static bool ErinaPending { get; internal set; }

        public int Step { get; private set; }
        public bool Lesson { get; private set; }
        public float Clock { get; private set; }
        public bool Racing => phase == Phase.Racing;
        public RaceTrack Track => track;
        public GateTracker Gates => gates;
        /// <summary>The longest drift (s) since the last gate, as the client's +0x54.</summary>
        public float DriftBest => driftBest;

        enum Phase { Script, Countdown, Racing, Done }
        Phase phase = Phase.Script;
        RaceTrack track;
        DriftCar car;
        CitySession session;
        GateTracker gates;
        float phaseTime, driftNow, driftBest, dialogClosedFor;
        bool failGate1, failGate2, countdownFx;
        int prevTraffic = -1;
        string prevCity;
        NuiView timeView, distView, keyView;
        PvpHud hud;
        PvpMinimap minimap;
        Text bigText;
        GameObject hudRoot;
        readonly List<GameObject> posts = new List<GameObject>();

        // ---------------- entry points ----------------

        /// <summary>
        /// The TUTORIAL button of the Battle Zone lobby (RMLOBBY 1000000, handler 0x57ea5e). An apprentice gets 11340 "After
        /// completing Tutorial Mission, you will be rewarded with a vehicle according to your time record. Do you want to
        /// proceed?" (Yes: the course; No: straight to the reward pick with grade 3); any other driver 11469 "Would you like to
        /// play the Tutorial?" (a practice run of the full course, no reward).
        /// </summary>
        public static void TutorialButton()
        {
            if (Active || GameFlow.MissionActive) return;
            var p = PlayerProfile.Current;
            if (p == null) return;
            if (Apprentice.IsApprentice(p))
                MissionUi.Confirm(GameTables.Text("11340"), () => Begin(true), Skip);
            else
                MissionUi.Confirm(GameTables.Text("11469"), () => Begin(false));
        }

        /// <summary>
        /// The Battle Zone opened for an apprentice (0x57c420): Director Jason's line 1 ("Please press the Tutorial button.").
        /// In the parking lot, where the remake's Battle Zone building has no lobby, the TUTORIAL question follows the line.
        /// Returns true when it took over.
        /// </summary>
        public static bool BattleZone(bool thenAsk)
        {
            var p = PlayerProfile.Current;
            if (!Apprentice.IsApprentice(p) || Active) return false;
            ApprenticeHost.Ensure().Talk(Apprentice.Jason, thenAsk ? TutorialButton : (Action)null);
            return true;
        }

        /// <summary>11340 "No": the reward pick at once with grade 3 and time 1000 (0x57cdd0 -> stage 6).</summary>
        public static void Skip()
        {
            if (PvpLobby.IsOpen) PvpLobby.Close();
            ApprenticeRewardPick.Open(3, Apprentice.SkippedSeconds, ok => { if (ok) ApprenticeHost.Ensure().AfterReward(); });
        }

        /// <summary>Starts the course: in Moon Palace at once, else after travelling there (the course lies in its streets).</summary>
        public static void Begin(bool lesson)
        {
            if (Active) return;
            if (PvpLobby.IsOpen) PvpLobby.Close();
            ApprenticeHost.Ensure().StartCourse(lesson);
        }

        internal static ApprenticeCourse Create(CitySession s, bool lesson, string prevCity)
        {
            var t = Apprentice.Track;
            if (t == null || !s || !s.Car) { UiPopup.Message(GameTables.Text("10745")); return null; }
            var c = new GameObject("Apprentice Course").AddComponent<ApprenticeCourse>();
            c.prevCity = prevCity;
            c.Setup(s, t, lesson);
            return c;
        }

        // ---------------- run ----------------

        void Setup(CitySession s, RaceTrack t, bool lesson)
        {
            Active = this;
            session = s; car = s.Car; track = t; Lesson = lesson;
            if (s.Traffic)
            {
                // the remake keeps the course clear of city traffic while it runs
                prevTraffic = s.Traffic.MaxCars;
                s.Traffic.MaxCars = 0;
                s.Traffic.TrimTo(0);
            }
            BuildPosts();
            BuildHud();
            Apprentice.ReportStart();
            AudioLibrary.PlayMusic(this, "bgm/DriftCity_1stpvp_01.mp3");
            // 0x72f190: a lesson shows Duffy's line 2 at the start; a practice run (step 6) goes straight to the course (8)
            Restart(lesson ? Apprentice.Lesson : -1);
        }

        /// <summary>Back to the start line (the client restarts the race: vfunc 0x134 / 0x104 + 0xd0), then the line, then GO.</summary>
        void Restart(int script)
        {
            failGate1 = failGate2 = false;
            driftNow = driftBest = 0f; dialogClosedFor = 0f;
            Clock = 0f;
            gates = new GateTracker(track);
            Vector3 a = track.Line[0], b = track.Line[Mathf.Min(3, track.Line.Count - 1)];
            var dir = b - a; dir.y = 0;
            if (dir.sqrMagnitude < 1e-4f) dir = Vector3.forward;
            dir.Normalize();
            var pos = RaceMission.Ground(a - dir * 8f);
            RaceMission.Teleport(car, pos, Quaternion.LookRotation(dir, Vector3.up));
            car.ExternalControl = true;
            car.BoostGauge = 0f;
            if (Step != Apprentice.FullCourse) Step = Lesson ? Apprentice.Lesson : Apprentice.FullCourse;
            if (script >= 0) { phase = Phase.Script; Show(script); }
            else StartCountdown();
        }

        void StartCountdown()
        {
            phase = Phase.Countdown; phaseTime = 0f;
            countdownFx = CountdownSeconds >= 6f && GameEffects.Screen("pvp_countdownstart");
            if (GameEffects.Sounds) AudioLibrary.PlaySfx("sfx/SID_PVP_RACE_START");
        }

        /// <summary>Shows a script row in QUESTDIALOG (0x72b600: the step becomes the row) with sound 0x4ec3 SID_PVP_ALERT.</summary>
        void Show(int row)
        {
            if (row != Apprentice.Lesson && row != Apprentice.Retry) Step = row;
            dialogClosedFor = 0f;
            if (GameEffects.Sounds) AudioLibrary.PlaySfx("sfx/SID_PVP_ALERT");
            ApprenticeHost.Ensure().Talk(row, null);
        }

        void Update()
        {
            if (phase == Phase.Done) return;
            if (!car) { End(false); return; }
            hud?.Tick();
            bool talking = MissionUi.ScriptOpen;
            dialogClosedFor = talking ? 0f : dialogClosedFor + Time.deltaTime;
            switch (phase)
            {
                case Phase.Script:
                    car.ExternalControl = true;
                    if (!talking) StartCountdown();
                    break;
                case Phase.Countdown:
                {
                    phaseTime += Time.deltaTime;
                    var rb = car.GetComponent<Rigidbody>();
                    if (rb) rb.linearVelocity = Vector3.Lerp(rb.linearVelocity, Vector3.zero, 0.5f);
                    float left = CountdownSeconds - phaseTime;
                    if (bigText) bigText.text = countdownFx ? "" : left > 3f ? "" : left > 0f ? Mathf.CeilToInt(left).ToString() : "GO!";
                    if (left <= 0f) { phase = Phase.Racing; phaseTime = 0f; car.ExternalControl = false; }
                    break;
                }
                case Phase.Racing:
                    phaseTime += Time.deltaTime;
                    if (bigText && phaseTime > 1f) bigText.text = "";
                    Tick(Time.deltaTime);
                    break;
            }
            UpdateHud();
        }

        /// <summary>One racing frame: the clock, the drift measure, the gates (0x72f3c0) and the lesson checks (0x7300a0).</summary>
        void Tick(float dt)
        {
            Clock += dt;
            // +0x54: the longest drift so far; a drift shorter than 0.5 s is forgotten when it ends
            driftNow = car.DriftState != 0 ? driftNow + dt : 0f;
            if (driftNow > driftBest) driftBest = driftNow;
            if (driftNow == 0f && driftBest < Apprentice.DriftSeconds) driftBest = 0f;
            if (gates.Update(car.transform.position)) OnGate(gates.Count - 1);
            if (phase != Phase.Racing) return;
            Check();
        }

        /// <summary>Tests: a drift of this length just happened.</summary>
        public void SimulateDrift(float seconds) { driftBest = Mathf.Max(driftBest, seconds); }
        /// <summary>Tests: time passes on the course clock (the lesson timeouts).</summary>
        public void AddTime(float seconds) { Clock += seconds; }

        /// <summary>A gate passed: index = gates passed before it (0x72fe00), the end line finishes the full course.</summary>
        void OnGate(int index)
        {
            if (index >= track.Gates.Count)
            {
                if (Step == Apprentice.FullCourse) Finish();
                return;
            }
            if (GameEffects.Sounds) AudioLibrary.PlaySfx("sfx/SID_PVP_RACE_LAPS");
            if (Step > 7) return;   // the full course shows no lesson lines
            switch (index)
            {
                case 0:
                    if (Step == Apprentice.DriftFailed) return;
                    if (driftBest < Apprentice.DriftSeconds) { failGate1 = true; Show(Apprentice.DriftFailed); return; }
                    driftBest = 0f;
                    return;
                case 1:
                    if (Step == Apprentice.DriftFailed) return;
                    if (driftBest < Apprentice.DriftSeconds) { failGate2 = true; Show(Apprentice.DriftFailed); return; }
                    driftBest = 0f;
                    car.BoostGauge = Apprentice.BoosterGauge;   // +0x1a68 = 97.0: "fill up the Booster gauge and use a booster"
                    Show(Apprentice.BoosterLesson);
                    return;
                case 2:
                    // still below 97: the booster was used
                    Show(car.BoostGauge < Apprentice.BoosterGauge ? Apprentice.BoosterPassed : Apprentice.BoosterFailed);
                    return;
            }
        }

        /// <summary>0x7300a0: the lesson timeouts and the restarts after the lines.</summary>
        void Check()
        {
            int passed = gates.Count;
            switch (Step)
            {
                case Apprentice.Lesson:
                    if (passed == 0 && Clock >= Apprentice.Gate1Limit) { failGate1 = true; Show(Apprentice.DriftFailed); }
                    else if (passed == 1 && Clock >= Apprentice.Gate2Limit) { failGate2 = true; Show(Apprentice.DriftFailed); }
                    break;
                case Apprentice.DriftFailed:
                {
                    // the run starts over once the failed section's time has run out
                    bool first = passed == 0 || failGate1;
                    bool second = !first && (passed == 1 || failGate2);
                    if ((first && Clock >= Apprentice.Gate1Restart) || (second && Clock >= Apprentice.Gate2Restart))
                    { Step = Apprentice.Lesson; Restart(Apprentice.Retry); }
                    break;
                }
                case Apprentice.BoosterFailed:
                    if (dialogClosedFor > Apprentice.AfterDialog) { Step = Apprentice.Lesson; Restart(Apprentice.Retry); }
                    break;
                case Apprentice.BoosterPassed:
                    // "Now, go drive the full course": the race starts over as the full course (step 6 -> 8)
                    if (dialogClosedFor > Apprentice.AfterDialog) { Step = Apprentice.FullCourse; Restart(-1); }
                    break;
            }
        }

        /// <summary>0x72ab10: the finish of the full course: +0x64 = time, +0x60 = grade, the grade's line; then the reward pick.</summary>
        void Finish()
        {
            float t = Clock;
            int grade = Apprentice.Grade(t);
            LastSeconds = t; LastGrade = grade;
            car.ExternalControl = true;
            var rb = car.GetComponent<Rigidbody>();
            if (rb) rb.linearVelocity = Vector3.zero;
            GameEffects.Screen("pvp_raceover");
            if (GameEffects.Sounds) AudioLibrary.PlaySfx("sfx/SID_PVP_RACE_OVER");
            var p = PlayerProfile.Current;
            if (p != null && t < Apprentice.SkippedSeconds && (p.ApprenticeBest <= 0 || t < p.ApprenticeBest) && !OnlineSession.Active) { p.ApprenticeBest = t; p.Save(); }
            phase = Phase.Done;
            Step = Apprentice.GradeScript(grade);
            ApprenticeHost.Ensure().Talk(Step, () =>
            {
                // 0x725330: an apprentice goes to the pick (stage 6); a practice run back to the parking lot (stage 9)
                if (Apprentice.IsApprentice(PlayerProfile.Current))
                    ApprenticeRewardPick.Open(grade, t, ok => End(ok));
                else End(false);
            });
        }

        /// <summary>Leaves the course: the parking lot (stage 9); after the reward Erina's line waits there.</summary>
        void End(bool rewarded)
        {
            phase = Phase.Done;
            ReturnStep = 0;
            var host = ApprenticeHost.Ensure();
            string city = prevCity;
            Cleanup();
            if (rewarded) host.AfterReward(city);
            else host.BackToLot(city);
        }

        /// <summary>Tests and logout: stops the course where it is (its step is kept for the next parking-lot arrival).</summary>
        public void Abort() { ReturnStep = Step; phase = Phase.Done; Cleanup(); }

        /// <summary>The apprentice step the next parking-lot arrival sees (0x762f70 reads +0x48; 3 / 7 = a failed lesson).</summary>
        public static int ReturnStep;

        void Cleanup()
        {
            if (Active == this) Active = null;
            foreach (var g in posts) if (g) Destroy(g);
            posts.Clear();
            var ui = UiManager.Instance;
            foreach (var v in new[] { timeView, distView, keyView }) if (v) { if (ui) ui.Close(v); else Destroy(v.gameObject); }
            timeView = distView = keyView = null;
            hud?.Close(); hud = null;
            minimap?.Close(); minimap = null;
            if (hudRoot) Destroy(hudRoot);
            if (car) car.ExternalControl = false;
            if (session && session.Traffic && prevTraffic > 0) session.Traffic.MaxCars = prevTraffic;
            if (GameFlow.Instance) AudioLibrary.PlayMusic(GameFlow.Instance, CityMusic.For(GameFlow.Instance.City));
            Destroy(gameObject);
        }

        void OnDestroy() { if (Active == this) Active = null; hud?.Close(); minimap?.Close(); }

        // ---------------- course posts and HUD ----------------

        void BuildPosts()
        {
            var shader = Shader.Find("Sprites/Default");
            int k = 0;
            foreach (var g in track.Gates.Concat(new[] { track.End }))
            {
                bool finish = k++ == track.Gates.Count;
                foreach (var end in new[] { g.A, g.B })
                {
                    var post = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    Destroy(post.GetComponent<Collider>());
                    float y = 0f, best = float.MaxValue;
                    foreach (var q in track.Line) { float d = (q.x - end.x) * (q.x - end.x) + (q.z - end.z) * (q.z - end.z); if (d < best) { best = d; y = q.y; } }
                    post.transform.position = PvpGates.GroundNear(new Vector3(end.x, y, end.z)) + Vector3.up * 5f;
                    post.transform.localScale = new Vector3(finish ? 1.6f : 1f, 6f, finish ? 1.6f : 1f);
                    post.GetComponent<Renderer>().sharedMaterial = OwnedAssets.Track(post, new Material(shader) { color = finish ? new Color(1, 0.85f, 0.1f, 0.5f) : new Color(0.2f, 0.8f, 1f, 0.35f) });
                    posts.Add(post);
                }
            }
        }

        void BuildHud()
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            hudRoot = NuiView.NewRect("Apprentice HUD", ui.Root).gameObject;
            NuiView.Stretch((RectTransform)hudRoot.transform);
            var rt = NuiView.NewRect("text", hudRoot.transform);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(1024, 200);
            bigText = rt.gameObject.AddComponent<Text>();
            bigText.font = UiAssets.Font("Verdana 96", out _, out _);
            bigText.fontSize = 96; bigText.fontStyle = FontStyle.Bold; bigText.alignment = TextAnchor.MiddleCenter; bigText.raycastTarget = false;
            bigText.color = new Color(1f, 0.85f, 0.2f);
            rt.gameObject.AddComponent<Outline>().effectColor = Color.black;
            hud = PvpHud.Open();
            minimap = PvpMinimap.Open(track);
            timeView = ui.Open(830000);
            distView = ui.Open(840000);
            keyView = ui.Open(850000);
            foreach (var v in new[] { timeView, distView, keyView })
            {
                if (!v) continue;
                MissionUi.ApplyDialogOffset(v);
                // the digit sources stay hidden: the client only copies their images into the display slots
                foreach (var c in v.GetComponentsInChildren<NuiControlView>(true))
                    if (c.Data.IdName != null && (c.Data.IdName.Contains("_NUMBER") || c.Data.IdName.EndsWith("NUMBERMINUS"))) c.gameObject.SetActive(false);
            }
            if (distView)
            {
                // POS4TH (and POINT) carry their own anchor F=10; the remake lines the thousands up left of POS3RD
                var c4 = distView.Control("ID_APPRENTICE_DISTANCE_POS4TH");
                var c3 = distView.Control("ID_APPRENTICE_DISTANCE_POS3RD");
                var c2 = distView.Control("ID_APPRENTICE_DISTANCE_POS2ND");
                if (c4 && c3 && c2)
                {
                    RectTransform r4 = (RectTransform)c4.transform, r3 = (RectTransform)c3.transform, r2 = (RectTransform)c2.transform;
                    r4.anchorMin = r3.anchorMin; r4.anchorMax = r3.anchorMax; r4.pivot = r3.pivot;
                    r4.anchoredPosition = r3.anchoredPosition + (r3.anchoredPosition - r2.anchoredPosition);
                }
            }
        }

        static readonly string[] LapSlots = { "LAP6TH", "LAP5TH", "LAP4TH", "LAP3RD", "LAP2ND", "LAP1ST" };
        static readonly string[] DriftSlots = { "DRIFT06", "DRIFT05", "DRIFT04", "DRIFT03", "DRIFT02", "DRIFT01" };

        /// <summary>The time as m'ss.fff in the six slots (0x5680f0: 6TH minutes, 5TH 4TH seconds, 3RD..1ST thousandths).</summary>
        static void Digits(NuiView v, string prefix, string[] slots, float seconds)
        {
            if (!v) return;
            int ms = Mathf.Clamp(Mathf.FloorToInt(seconds * 1000f), 0, 9 * 60000 + 59999);
            int m = ms / 60000, s = ms / 1000 % 60, f = ms % 1000;
            int[] d = { m % 10, s / 10, s % 10, f / 100, f / 10 % 10, f % 10 };
            for (int k = 0; k < 6; k++) Copy(v, prefix + slots[k], $"{prefix}NUMBER{d[k] + 1:00}");
        }

        static void Copy(NuiView v, string dstId, string srcId)
        {
            var dst = MissionUi.Image(v, dstId);
            var src = MissionUi.Image(v, srcId);
            if (!dst || !src) return;
            dst.texture = src.texture; dst.uvRect = src.uvRect; dst.enabled = src.texture;
        }

        void UpdateHud()
        {
            float t = phase == Phase.Racing || phase == Phase.Done ? Clock : 0f;
            hud?.Set(1, 1, 1, 1, t, 0f);
            if (minimap != null && minimap.IsOpen && car) minimap.Draw(car.transform, -1, new List<(Transform, int)>());
            if (timeView)
            {
                Digits(timeView, "ID_APPRENTICE_TIME_", LapSlots, t);
                // the drift row: the lesson's drift (the client shows it while gates 0 and 1 are ahead)
                bool drift = Step <= 7 && gates != null && gates.Count < 2;
                foreach (var id in DriftSlots.Concat(new[] { "DRIFTSECOND", "DRIFTMS", "DRIFTMARK", "DRIFTICON" })) timeView.SetVisible("ID_APPRENTICE_TIME_" + id, drift);
                if (drift)
                {
                    // the drift row's slots borrow the time digits (the dialog has one set of digit art)
                    int ms = Mathf.Clamp(Mathf.FloorToInt(driftBest * 1000f), 0, 599999);
                    int s = ms / 1000 % 60, f = ms % 1000;
                    int[] d = { ms / 60000 % 10, s / 10, s % 10, f / 100, f / 10 % 10, f % 10 };
                    for (int k = 0; k < 6; k++) Copy(timeView, "ID_APPRENTICE_TIME_" + DriftSlots[k], $"ID_APPRENTICE_TIME_NUMBER{d[k] + 1:00}");
                }
            }
            if (distView)
            {
                // 0x568540: the metres to the finish (+0xfc), blinking within 1200 m
                float meters = car ? Vector3.Distance(Flat(car.transform.position), Flat(track.End.Centre)) : 0f;
                int m = Mathf.Clamp(Mathf.RoundToInt(meters), 0, 9999);
                string[] pos = { "POS4TH", "POS3RD", "POS2ND", "POS1ST" };
                int[] d = { m / 1000, m / 100 % 10, m / 10 % 10, m % 10 };
                bool blink = m <= 1200 && Mathf.Repeat(Time.time, 0.5f) > 0.25f;
                for (int k = 0; k < 4; k++)
                {
                    Copy(distView, "ID_APPRENTICE_DISTANCE_" + pos[k], $"ID_APPRENTICE_DISTANCE_NUMBER{d[k] + 1:00}");
                    // leading zeros stay blank (0x568540 draws the thousands over 1000, the hundreds over 100)
                    bool shown = (k == 0 ? m >= 1000 : k == 1 ? m >= 100 : true) && !blink;
                    distView.SetVisible("ID_APPRENTICE_DISTANCE_" + pos[k], shown);
                }
                distView.SetVisible("ID_APPRENTICE_DISTANCE_POINT", false);
                distView.SetVisible("ID_APPRENTICE_DISTANCE_METER", true);
            }
            if (keyView)
            {
                // 0x568540: Shift + Right before gate 1, Shift + Left before gate 2; the remake shows Ctrl + Up for the booster
                int ahead = gates != null ? gates.Count : 0;
                bool lesson = Step <= 7 && phase != Phase.Done;
                bool driftKeys = lesson && ahead < 2, boostKeys = lesson && ahead == 2;
                keyView.SetVisible("ID_APPRENTICE_KEY_SHIFT", driftKeys);
                keyView.SetVisible("ID_APPRENTICE_KEY_CTRL", boostKeys);
                keyView.SetVisible("ID_APPRENTICE_KEY_PLUS", driftKeys || boostKeys);
                keyView.SetVisible("ID_APPRENTICE_KEY_RIGHT", driftKeys && ahead == 0);
                keyView.SetVisible("ID_APPRENTICE_KEY_LEFT", driftKeys && ahead == 1);
                keyView.SetVisible("ID_APPRENTICE_KEY_UP", boostKeys);
            }
        }

        static Vector3 Flat(Vector3 v) { v.y = 0; return v; }
    }
}
