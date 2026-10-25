using System;
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
    /// The QuestClient.tdf step quests in the city (DriftCity_RE/MISSIONS.md section 11). The client's running quest entry
    /// (0x702f30, 0x5c bytes: +0x10 step, +0x24 done, +0x30 time left, +0x40 crashes, +0x44/+0x45 trail state) is driven by
    /// the post enter handler 0x6fc7b0 (step kinds 0-6), the frame check 0x6fcf50 (speed range, trail distance, time) and
    /// the crash counter 0x6ff1d0; the HUD is QUESTPLAYINFO 720000 (QuestPlayInfo) with 730000 time, 740000 crashes, 750000
    /// speed range, 760000 trail gauge, 770000 the "suspicious" bubble and QUESTPLAYEFFECT 780000. Quests are taken at their
    /// start post (a station's QUEST / SECRETMISSION button, a quest giver's marker in the city) in QUESTDIALOG with RECV,
    /// and handed in at the end post with REWARD. The server judged the steps and paid; here the client runs them offline
    /// and reports them online (server src/quests.js, reward kind "stepquest").
    /// </summary>
    public sealed class StepQuestRunner : MonoBehaviour
    {
        public static StepQuestRunner Instance { get; private set; }
        public CitySession Session;
        public string City;
        /// <summary>A post counts within this distance (the client uses the post object's radius +0x60 and |dz| &lt; 7).</summary>
        public float PostRadius = 25f;
        /// <summary>A quest giver in the city opens its lines when the car stops this close.</summary>
        public float GiverRadius = 18f;
        /// <summary>The trail / speed grace: out of range this long fails the step (0x6fcf50: 10 - elapsed).</summary>
        public const float GraceSeconds = 10f;
        /// <summary>Type 1 steps: under this speed counts as stopped (0x6faf80: speed &lt; 11).</summary>
        public const float StopKmh = 11f;
        /// <summary>A stake-out ends 2 s after the target came this close (0x6fc7b0 case 4, 0x6f7150; the radius is the rebuild's).</summary>
        public float StakeoutRadius = 45f;

        public const int TimeId = 730000, CrashId = 740000, SpeedId = 750000, FollowId = 760000, QuestionId = 770000, EffectId = 780000;

        static PlayerProfile P => PlayerProfile.Current;

        // the step's state (the client's entry fields)
        float timeLeft, speedBad, followBad, stakeoutPassed, crashCooldown, effectHide, nextGiverCheck, beaconCheck;
        int crashes;
        bool stepStarted, waitingChase, chaseWon, chaseLost, targetSpawned;
        /// <summary>A step report is on its way to the server: nothing advances until the reply.</summary>
        static bool pending;
        /// <summary>When the last accept / step went out: the server wants a second between step reports (src/quests.js).</summary>
        static float lastReportAt = -10f;
        public const float MinStepGap = 1.2f;
        string declinedPost;
        float declinedUntil;
        QuestTrailCar target;
        MissionBeacon beacon;
        NuiView timeView, crashView, speedView, followView, questionView, effectView;
        QuestCrashSensor sensor;

        public static StepQuestRunner Attach(CitySession session, string city)
        {
            if (!session) return null;
            if (!session.TryGetComponent(out StepQuestRunner r)) r = session.gameObject.AddComponent<StepQuestRunner>();
            r.Session = session;
            r.City = city;
            Instance = r;
            pending = false;
            return r;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            ClearStep();
            if (beacon) Destroy(beacon.gameObject);
            if (sensor) Destroy(sensor);
        }

        public StepQuest Quest => StepQuests.Running(P);
        public int Step => P?.StepQuest?.Step ?? 0;
        public bool Done => P?.StepQuest != null && P.StepQuest.QuestId > 0 && P.StepQuest.Counter > 0;
        public QuestStep CurrentStep { get { var q = Quest; int s = Step; return q != null && s >= 0 && s < q.Steps.Count ? q.Steps[s] : null; } }
        /// <summary>The step before the current one (its kind decides trail / chase rules, 0x5592d0 tests step - 1).</summary>
        public QuestStep PreviousStep { get { var q = Quest; int s = Step - 1; return q != null && s >= 0 && s < q.Steps.Count ? q.Steps[s] : null; } }
        public float TimeLeft => timeLeft;
        /// <summary>The bonus count of the running quest (times started over; the server's n: StepQuests.TimeBonus).</summary>
        public static int Tries => P?.StepQuestTries ?? 0;
        public int Crashes => crashes;
        public QuestTrailCar Target => target;
        public bool Trailing => target && PreviousStep != null && (PreviousStep.Kind == StepKind.Trail || PreviousStep.Kind == StepKind.TrailFar);

        DriftCar Car => GameFlow.Instance ? GameFlow.Instance.PlayerCar : (Session ? Session.Car : null);

        // ---------------- taking a quest ----------------

        /// <summary>A station's QUEST / SECRETMISSION button: its quest (PREVNEXT for more), the running quest's end, or 10191.</summary>
        public static void OpenStation(CityMapInfo.Icon station, Action closed = null)
        {
            var p = P;
            if (station == null || p == null) { closed?.Invoke(); return; }
            var running = StepQuests.Running(p);
            if (running != null)
            {
                if (p.StepQuest.Counter > 0 && string.Equals(running.EndPost, station.Id, StringComparison.OrdinalIgnoreCase)) { Instance?.OpenEnd(closed); return; }
                UiPopup.Message(MissionText.Get("10192") + "\n" + running.Title, closed);
                return;
            }
            var list = StepQuests.AvailableAt(station.Id, p);
            if (list.Count == 0) { UiPopup.Message(MissionText.Get("10191"), closed); return; }
            Offer(list, 0, closed);
        }

        /// <summary>QUESTDIALOG with the quest's start lines and RECV; PREVNEXT pages through the other quests of the post.</summary>
        public static void Offer(List<StepQuest> list, int index, Action closed = null)
        {
            if (list == null || list.Count == 0) return;
            index = (index % list.Count + list.Count) % list.Count;
            var q = list[index];
            Action prev = list.Count > 1 ? () => Offer(list, index - 1, closed) : (Action)null;
            Action next = list.Count > 1 ? () => Offer(list, index + 1, closed) : (Action)null;
            OfferedQuest = q;
            MissionUi.Script(q.StartScript, q.Title, true, () => { MissionUi.CloseScript(); Accept(q); closed?.Invoke(); }, () => closed?.Invoke(),
                             "ID_QUESTDIALOG_RECV", prev, next);
        }

        /// <summary>The quest the open QUESTDIALOG offers (tests).</summary>
        public static StepQuest OfferedQuest { get; private set; }

        /// <summary>RECV: the quest starts at step 0 (online the server records it first; 10122 "[%s] mission accepted.").</summary>
        public static void Accept(StepQuest q)
        {
            var p = P;
            if (p == null || q == null) return;
            if (StepQuests.Running(p) != null) { UiPopup.Message(MissionText.Get("10192")); return; }
            if (ChaseMission.Active || DeliveryService.Active || PatrolMission.Active || RaceMission.Active || OnlineRace.Active) { UiPopup.Message(MissionText.Get("10186")); return; }
            var why = StepQuests.Refusal(p, q);
            if (why != null) { UiPopup.Message(why); return; }
            int number = q.Number;
            lastReportAt = Time.unscaledTime;
            Progress.Op("quest.accept", new Dictionary<string, object> { ["number"] = number }, () =>
            {
                p.StepQuest = new QuestProgress { QuestId = number, Step = 0, Counter = 0 };
                p.StepQuestTries = 0;
                return new Progress.Result { Ok = true };
            }, r =>
            {
                if (!r.Ok) return;
                AudioLibrary.PlaySfx("sfx/SID_ARBEIT_ACCEPT");
                ChatBox.System(MissionText.Get("10122", q.Title));
                Instance?.BeginStep();
            });
        }

        /// <summary>Give up (the quest log's button, a failed step's "no"): 10124 "[%s] abandoned.".</summary>
        public static void Abandon()
        {
            var q = StepQuests.Running(P);
            if (q == null) return;
            Instance?.ClearStep();
            Progress.Op("quest.abandon", new Dictionary<string, object> { ["number"] = q.Number }, () =>
            {
                P.StepQuest = new QuestProgress();
                P.StepQuestTries = 0;
                return new Progress.Result { Ok = true };
            }, r => { if (r.Ok) ChatBox.System(MissionText.Get("10124", q.Title)); Instance?.Refresh(); });
        }

        /// <summary>QUESTPLAYINFO RETRY / a failed step's "yes": the quest starts over from step 0.</summary>
        public static void Retry()
        {
            var q = StepQuests.Running(P);
            if (q == null) return;
            Instance?.ClearStep();
            Report(q, 0, false, () => Instance?.BeginStep());
        }

        static void Report(StepQuest q, int step, bool done, Action after)
        {
            pending = true;
            lastReportAt = Time.unscaledTime;
            Progress.Op("quest.step", new Dictionary<string, object> { ["number"] = q.Number, ["step"] = step, ["done"] = done }, () =>
            {
                P.StepQuest.Step = step;
                P.StepQuest.Counter = done ? 1 : 0;
                // starting over raises the bonus count (the server's per-quest n: time / crash / distance bonuses)
                if (step == 0) P.StepQuestTries = Mathf.Min(StepQuests.MaxTries, P.StepQuestTries + 1);
                return new Progress.Result { Ok = true };
            }, r => { pending = false; if (r.Ok) after?.Invoke(); });
        }

        // ---------------- the steps ----------------

        /// <summary>Starts the running quest's current step: time limit, crash count, beacon, the HUD panels.</summary>
        public void BeginStep()
        {
            ClearStep();
            var q = Quest;
            if (q == null) { Refresh(); return; }
            stepStarted = true;
            var st = CurrentStep;
            timeLeft = StepQuests.StepSeconds(st, Tries);
            crashes = 0;
            if (!sensor && Car) sensor = Car.gameObject.AddComponent<QuestCrashSensor>();
            Refresh();
        }

        /// <summary>Removes the step's objects and panels (the trail car, the chase hook, the HUD parts).</summary>
        public void ClearStep()
        {
            stepStarted = false;
            waitingChase = chaseWon = chaseLost = targetSpawned = false;
            speedBad = followBad = stakeoutPassed = 0;
            if (target) Destroy(target.gameObject);
            target = null;
            var ui = UiManager.Instance;
            if (ui) foreach (var v in new[] { timeView, crashView, speedView, followView, questionView, effectView }) if (v) ui.Close(v);
            timeView = crashView = speedView = followView = questionView = effectView = null;
        }

        void Refresh()
        {
            var q = Quest;
            if (q == null) { if (beacon) beacon.Hide(); return; }
            string post = Done ? q.EndPost : CurrentStep?.Post;
            var icon = MissionPosts.Find(post);
            if (!beacon) beacon = MissionBeacon.Create("Quest Beacon", MarkerNif(q, Done ? null : CurrentStep), new Color(0.3f, 0.9f, 1f, 0.35f));
            var car = Car;
            // the column hides once the car stands in the post (the camera would sit inside it)
            bool near = car && icon != null && MissionPosts.Flat(car.transform.position, icon.Position) < PostRadius;
            if (icon != null && MissionPosts.CityOf(post) == City && !near) beacon.MoveTo(icon.Position); else beacon.Hide();
        }

        /// <summary>game\mq_{start,normal,pass,stop,end}_{omd,roo}.nif for the main story, sq_*_cityhall for the others; a
        /// marker column that names a model ("sq_pass_cityhall") is used as it is.</summary>
        public static string MarkerNif(StepQuest q, QuestStep st)
        {
            string kind = st == null ? "end" : st.Kind == StepKind.Pass ? "pass" : st.Kind == StepKind.Stop || st.Kind == StepKind.Stakeout ? "stop" : "normal";
            if (st != null && !string.IsNullOrEmpty(st.Marker) && (st.Marker.StartsWith("mq_") || st.Marker.StartsWith("sq_"))) return $@"game\{st.Marker}.nif";
            return q.IsMain ? $@"game\mq_{kind}_{Factions.MarkerSide}.nif" : $@"game\sq_{kind}_cityhall.nif";
        }

        void Update()
        {
            var car = Car;
            var p = P;
            if (!car || p == null) return;
            if (sensor == null || sensor.gameObject != car.gameObject) { if (sensor) Destroy(sensor); sensor = car.gameObject.AddComponent<QuestCrashSensor>(); }
            var q = Quest;
            if (q == null) { if (stepStarted) ClearStep(); if (beacon) beacon.Hide(); Givers(car); return; }
            if (pending) return;
            if (!stepStarted) BeginStep();
            beaconCheck -= Time.deltaTime;
            if (beaconCheck <= 0) { beaconCheck = 0.25f; Refresh(); }
            if (Done) { EndPost(car, q); return; }
            var st = CurrentStep;
            if (st == null) return;
            bool paused = MissionUi.ScriptOpen || UndercityStage.Active || (GameFlow.Instance && GameFlow.Instance.InParkingLot);
            float dt = Time.deltaTime;
            Vector3 pos = car.transform.position;

            // a chase step's follow-up ends with the chase (0x6fc7b0: no advance on the post after a type 5 step)
            if (waitingChase)
            {
                if (chaseWon) { waitingChase = false; Advance(q); return; }
                if (chaseLost) { waitingChase = false; Fail(q); return; }
            }

            if (!paused)
            {
                // the step's time (0x555570); 0 = none
                if (st.Seconds > 0 && !waitingChase)
                {
                    timeLeft -= dt;
                    if (timeLeft < 0) { Fail(q); return; }
                }
                // the speed range: out of it for 10 s fails (0x6fcf50)
                if (st.SpeedMin > 0 || st.SpeedMax > 0)
                {
                    float kmh = car.SpeedKmh;
                    bool bad = kmh < st.SpeedMin || (st.SpeedMax > 0 && kmh > st.SpeedMax);
                    speedBad = bad ? speedBad + dt : 0;
                    if (speedBad > GraceSeconds) { Fail(q); return; }
                }
                // the trail: keep the target between the step's two distances (0x6f72b0, 0x553520 / 0x553560)
                if (Trailing)
                {
                    float d = MissionPosts.Flat(pos, target.transform.position);
                    int far = StepQuests.StepFollowMax(st, Tries);
                    bool bad = d < st.FollowMin || (far > 0 && d > far);
                    followBad = bad ? followBad + dt : 0;
                    if (followBad > GraceSeconds) { Fail(q); return; }
                }
                // crashes (0x6ff1d0): one a second at most, the limit fails; hitting the trail target fails at once
                if (sensor && sensor.TakeHit(out bool hitTarget, target ? target.gameObject : null))
                {
                    if (hitTarget && Trailing) { Fail(q); return; }
                    if (crashCooldown <= 0)
                    {
                        crashCooldown = 1f;
                        crashes = Mathf.Min(9, crashes + 1);
                        int limit = StepQuests.StepCrashes(st, Tries);
                        if (limit > 0) { ShowCrashEffect(limit); if (crashes >= limit) { Fail(q); return; } }
                    }
                }
                crashCooldown -= dt;
            }

            // the post (0x6fc7b0: the car's current post must be the step's)
            var icon = MissionPosts.Find(st.Post);
            bool atPost = icon != null && MissionPosts.Flat(pos, icon.Position) < PostRadius && Mathf.Abs(pos.y - icon.Position.y) < 40f;
            if (st.Kind == StepKind.Stakeout && targetSpawned)
            {
                // a stake-out: leaving the post fails (0x6fcf50 type 4), the target's pass ends the step 2 s later
                if (!atPost && !paused) { Fail(q); return; }
                if (target && (MissionPosts.Flat(pos, target.transform.position) < StakeoutRadius || target.Finished)) stakeoutPassed += dt;
                if (!target || stakeoutPassed > 2f) { Advance(q); return; }
            }
            else if (atPost && !paused && !waitingChase && !(PreviousStep != null && PreviousStep.Kind == StepKind.Chase) &&
                     Time.unscaledTime - lastReportAt >= MinStepGap) ArriveAt(q, st, icon, car);
            UpdateHud(q, st, car);
        }

        void ArriveAt(StepQuest q, QuestStep st, CityMapInfo.Icon icon, DriftCar car)
        {
            switch (st.Kind)
            {
                case StepKind.Arrive:
                    Arrived("10126", q, icon); PlayStepScript(q, st); Advance(q); break;
                case StepKind.Stop:
                    if (car.SpeedKmh >= StopKmh) return;
                    Arrived("10126", q, icon); PlayStepScript(q, st); Advance(q); break;
                case StepKind.Pass:
                    Arrived("10127", q, icon); PlayStepScript(q, st); Advance(q); break;
                case StepKind.Trail:
                case StepKind.TrailFar:
                    if (!SpawnTarget(q)) { Arrived("10126", q, icon); Advance(q); break; }
                    Arrived("10128", q, icon); PlayStepScript(q, st); Advance(q, keepTarget: true); break;
                case StepKind.Stakeout:
                    if (targetSpawned) return;
                    targetSpawned = true;
                    SpawnTarget(q);
                    Arrived("10129", q, icon); PlayStepScript(q, st);
                    break;
                case StepKind.Chase:
                    StartChase(q);
                    Arrived("10126", q, icon); PlayStepScript(q, st);
                    Advance(q, keepTarget: false, chase: true);
                    break;
            }
        }

        static void Arrived(string text, StepQuest q, CityMapInfo.Icon icon)
        {
            AudioLibrary.PlaySfx("sfx/SID_CALLSIGN_ACCEPT");
            ChatBox.System(MissionText.Get(text, q.Title, icon?.Name ?? ""));
        }

        /// <summary>0x6fc750: the arrived step's lines (columns 29-33) in QUESTDIALOG.</summary>
        static void PlayStepScript(StepQuest q, QuestStep st) => PlayScript(st.Script, q.Title);

        static readonly System.Text.RegularExpressions.Regex JumpTag =
            new System.Text.RegularExpressions.Regex(@"<jump\s+(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s*>");

        /// <summary>
        /// A step / end script: its &lt;jump x y z&gt; tag puts the car at that point (Gamebryo x, y, z: the tutorial's start,
        /// Infiltration!'s way home), then the lines show in QUESTDIALOG (none when the script has no text).
        /// </summary>
        public static void PlayScript(string script, string title)
        {
            if (string.IsNullOrEmpty(script)) return;
            var m = JumpTag.Match(script);
            var car = GameFlow.Instance ? GameFlow.Instance.PlayerCar : null;
            if (m.Success && car)
            {
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                var at = new Vector3(float.Parse(m.Groups[1].Value, ci), float.Parse(m.Groups[3].Value, ci), float.Parse(m.Groups[2].Value, ci));
                RaceMission.Teleport(car, RaceMission.Ground(at), car.transform.rotation);
            }
            // <ending ROO|OMD>: the storyline's end plays the ending and credits (0x4dd4b0..0x4e11e0) after the lines
            var ending = EndingTag.Match(script);
            System.Action roll = ending.Success ? () => EndingCredits.Play(ending.Groups[1].Value.ToUpperInvariant() == "ROO", null) : (System.Action)null;
            if (script.Contains("<text") || script.Contains("<sub_text")) MissionUi.Script(script, title, false, null, roll);
            else roll?.Invoke();
        }

        static readonly System.Text.RegularExpressions.Regex EndingTag =
            new System.Text.RegularExpressions.Regex(@"<ending\s+(ROO|OMD)\s*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>0x702b30 / 0x702ad0: the next step, or "done" after the last one (sound 0x4f06, report to the server).</summary>
        void Advance(StepQuest q, bool keepTarget = false, bool chase = false)
        {
            int next = Step + 1;
            bool done = next >= q.Steps.Count;
            var keep = keepTarget ? target : null;
            if (keep) target = null;
            ClearStep();
            if (keep) target = keep;
            waitingChase = chase && !done;
            stepStarted = true;
            AudioLibrary.PlaySfx("sfx/SID_QUEST_SUCCESS");
            Report(q, done ? q.Steps.Count : next, done, () =>
            {
                timeLeft = !done && next < q.Steps.Count ? StepQuests.StepSeconds(q.Steps[next], Tries) : 0;
                crashes = 0;
                if (done && target) { Destroy(target.gameObject); target = null; }
                Refresh();
            });
        }

        /// <summary>The step failed (state 3, packet 0x789100): 10137, quest_fail, then 10125 "attempt again?".</summary>
        void Fail(StepQuest q)
        {
            bool chasing = waitingChase;
            ClearStep();
            if (ChaseMission.Active && chasing) ChaseMission.Active.Quit();
            AudioLibrary.PlaySfx("sfx/SID_QUEST_CANCEL");
            GameEffects.Screen("quest_fail");
            ChatBox.System(MissionText.Get("10137", q.Title));
            stepStarted = true;   // nothing runs until the answer
            Failed = true;
            MissionUi.Confirm(MissionText.Get("10125"), () => { Failed = false; Retry(); }, () => { Failed = false; Abandon(); });
        }

        /// <summary>A failed step waits for the 10125 answer (tests).</summary>
        public bool Failed { get; private set; }

        /// <summary>Test hook: completes the running step as an arrival would (reported to the server online).</summary>
        public void ForceAdvance() { var q = Quest; if (q != null && !Done && !pending) Advance(q); }

        /// <summary>Test hook: fails the running step as the frame check would.</summary>
        public void FailNow() { var q = Quest; if (q != null) Fail(q); }

        // ---------------- trail / stake-out target and chases ----------------

        bool SpawnTarget(StepQuest q)
        {
            if (target) return true;
            target = QuestTrailCar.Spawn(City, q.PathId, q.Vehicle);
            if (!target) { ChatBox.System(MissionText.Get("10142", q.PathId)); return false; }
            return true;
        }

        /// <summary>
        /// Case 5: the HUV of column 71 (vehiclelist Unique Id) is chased; the HUV manager's Team ROO flag (+0xe0) comes from
        /// the quest (the five ROO leader arrests), so their chase ends on beat_success / beat_fail.
        /// </summary>
        void StartChase(StepQuest q)
        {
            chaseWon = chaseLost = false;
            var car = Car;
            int index = GameTables.Huvs.FindIndex(h => h.UniqueId == q.Vehicle);
            bool boss = false;
            if (index < 0) { index = GameTables.BossHuvs.FindIndex(h => h.UniqueId == q.Vehicle); boss = index >= 0; }
            var m = Session && car ? ChaseMission.Begin(Session.Traffic, car, boss, index, 0f, 0) : null;
            if (!m) { chaseWon = true; return; }   // no HUV model here: the step is passed (offline fallback)
            // ChaseMission.TeamRoo (hook line in the report): set it once the field exists
            typeof(ChaseMission).GetField("TeamRoo")?.SetValue(m, q.RooTarget);
            m.Finished += won => { if (won) chaseWon = true; else chaseLost = true; };
        }

        /// <summary>Team ROO's chase banners (UI effects 0x2d beat_success / 0x2e beat_fail, 0x6f7d80) with the HUV sounds.</summary>
        public static void RooChaseBanner(bool caught, Transform huv = null)
        {
            GameEffects.Screen(caught ? "beat_success" : "beat_fail");
            AudioLibrary.PlaySfx(caught ? "sfx/SID_HUV_SUCCESS" : "sfx/SID_HUV_FAIL");
            if (caught && huv) GameEffects.HuvExplosion(huv);
        }

        // ---------------- the end post: lines and reward ----------------

        void EndPost(DriftCar car, StepQuest q)
        {
            if (MissionUi.ScriptOpen || Failed) return;
            var icon = MissionPosts.Find(q.EndPost);
            if (icon == null) return;
            if (MissionPosts.Flat(car.transform.position, icon.Position) < PostRadius && car.SpeedKmh < 30f && !(declinedPost == q.EndPost && Time.time < declinedUntil))
                OpenEnd(() => { declinedPost = q.EndPost; declinedUntil = Time.time + 20f; });
        }

        /// <summary>The end NPC's lines with REWARD: the reward claim (kind "stepquest") and the quest's unlock.</summary>
        public void OpenEnd(Action closed = null)
        {
            var q = Quest;
            if (q == null || !Done) { closed?.Invoke(); return; }
            MissionUi.Script(q.EndScript, q.Title, true, () => { MissionUi.CloseScript(); Claim(q); }, closed, "ID_QUESTDIALOG_REWARD", null, null);
        }

        /// <summary>Hands the quest in: columns 75 / 76 EXP and Mito, the items of 77-79, the unlock (OMD / ROO joins the side).</summary>
        public static void Claim(StepQuest q)
        {
            var p = P;
            if (p == null || q == null) return;
            var data = new Dictionary<string, object> { ["number"] = q.Number };
            Progress.Reward("stepquest", data, () =>
            {
                if (p.StepQuest == null || p.StepQuest.QuestId != q.Number || p.StepQuest.Counter <= 0) return new Progress.Result { Error = "quest not finished" };
                int before = p.Level;
                p.AddExp(q.Exp); p.Mito += q.Mito;
                foreach (var it in q.Items) p.Give(it);
                if (!p.StepQuestsDone.Contains(q.Number)) p.StepQuestsDone.Add(q.Number);
                if (q.Unlock == QuestUnlock.Roo) p.Faction = Factions.Roo;
                if (q.Unlock == QuestUnlock.Omd) p.Faction = Factions.Omd;
                p.StepQuest = new QuestProgress();
                p.StepQuestTries = 0;
                return new Progress.Result { Ok = true, Exp = q.Exp, Mito = q.Mito, LevelUps = p.Level - before };
            }, r =>
            {
                if (!r.Ok) { UiPopup.Message(MissionText.Get("10190") + (string.IsNullOrEmpty(r.Error) ? "" : "\n" + r.Error)); return; }
                AudioLibrary.PlaySfx("sfx/SID_QUEST_SUCCESS");
                GameEffects.Screen("quest_success");
                ChatBox.System(MissionText.Get("10121", q.Title));
                foreach (var it in q.Items) ChatBox.System($"Obtained {Crafting.Name(it)}.");
                if (q.Unlock == QuestUnlock.Roo || q.Unlock == QuestUnlock.Omd)
                    ChatBox.System(MissionText.Get(q.Unlock == QuestUnlock.Roo ? "10676" : "10671"));   // the side's motto
                ChaseScreens.RewardPopup(r.Exp, r.Mito);
                Instance?.ClearStep();
                Instance?.Refresh();
            });
        }

        // ---------------- quest givers in the city ----------------

        void Givers(DriftCar car)
        {
            if (Time.time < nextGiverCheck) return;
            nextGiverCheck = Time.time + 0.3f;
            if (MissionUi.ScriptOpen || GameFlow.MissionActive || DeliveryService.Active || PatrolMission.Active || car.SpeedKmh > 30f) return;
            if (GameFlow.Instance && (GameFlow.Instance.MenuOpen || GameFlow.Instance.InParkingLot)) return;
            var pos = car.transform.position;
            foreach (var q in StepQuests.GiversIn(City, P))
            {
                // the stations' quests go through the station panel's button
                var icon = MissionPosts.Find(q.StartPost);
                if (icon == null || icon.IsStation || Factions.IsRooPost(icon.Id)) continue;
                if (MissionPosts.Flat(pos, icon.Position) > GiverRadius || Mathf.Abs(pos.y - icon.Position.y) > 40f) continue;
                if (declinedPost == icon.Id && Time.time < declinedUntil) continue;
                string post = icon.Id;
                Offer(StepQuests.AvailableAt(post, P), 0, () => { declinedPost = post; declinedUntil = Time.time + 60f; });
                return;
            }
        }

        /// <summary>Minimap markers: the running step's post (MQ/SQ_NORMAL), the end post (MQ/SQ_END), the quest givers (MQ/SQ_START, REPEATQUEST).</summary>
        public IEnumerable<MissionMarkers.Marker> Markers()
        {
            var p = P;
            var q = Quest;
            if (q != null)
            {
                string post = Done ? q.EndPost : CurrentStep?.Post;
                var icon = MissionPosts.Find(post);
                if (icon != null && MissionPosts.CityOf(post) == City)
                    yield return new MissionMarkers.Marker { Icon = q.IsMain ? (Done ? "ID_MINIMAP_ICON_MQ_END" : "ID_MINIMAP_ICON_MQ_NORMAL") : (Done ? "ID_MINIMAP_ICON_SQ_END" : "ID_MINIMAP_ICON_SQ_NORMAL"), Position = icon.Position, Label = icon.Name };
                if (target) yield return new MissionMarkers.Marker { Icon = "ID_MINIMAP_ICON_HUV", Position = target.transform.position, Label = q.Title };
                yield break;
            }
            if (p == null) yield break;
            foreach (var g in StepQuests.GiversIn(City, p))
            {
                var icon = MissionPosts.Find(g.StartPost);
                if (icon == null) continue;
                string kind = g.IsMain ? "ID_MINIMAP_ICON_MQ_START" : g.IsRepeat ? "ID_MINIMAP_ICON_REPEATQUEST" : "ID_MINIMAP_ICON_SQ_START";
                yield return new MissionMarkers.Marker { Icon = kind, Position = icon.Position, Label = g.Title };
            }
        }

        // ---------------- the HUD panels 730000-780000 ----------------

        void UpdateHud(StepQuest q, QuestStep st, DriftCar car)
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            bool hide = DeliveryService.Active || UndercityStage.Active || (GameFlow.Instance && GameFlow.Instance.InParkingLot);
            // 730000: the step's time left, mm ' ss , ttt (0x5559b0)
            timeView = Panel(timeView, TimeId, !hide && st.Seconds > 0);
            if (timeView) Digits(timeView, "ID_QUESTPLAYINFO_TIME_", "ID_QUESTPLAYINFO_TIME_NUM", Mathf.Max(0, timeLeft));
            // 740000: crashes used / allowed (0x555600)
            int crashLimit = StepQuests.StepCrashes(st, Tries);
            crashView = Panel(crashView, CrashId, !hide && crashLimit > 0);
            if (crashView) for (int d = 0; d <= 9; d++)
                {
                    crashView.SetVisible("ID_QUESTPLAYINFO_CRASH_MAX_" + d, d == crashLimit);
                    crashView.SetVisible("ID_QUESTPLAYINFO_CRASH_" + d, d == crashes);
                }
            // 750000: the speed range, red outside / yellow within 15 km/h of a limit (blinking) / green (0x5592d0)
            speedView = Panel(speedView, SpeedId, !hide && (st.SpeedMin > 0 || st.SpeedMax > 0));
            if (speedView)
            {
                float kmh = car.SpeedKmh;
                speedView.SetText("ID_QUESTPLAYINFO_SPEED_MIN", st.SpeedMin.ToString("000"));
                speedView.SetText("ID_QUESTPLAYINFO_SPEED_MAX", st.SpeedMax.ToString("000"));
                bool outside = kmh < st.SpeedMin || (st.SpeedMax > 0 && kmh > st.SpeedMax);
                bool near = !outside && (kmh < st.SpeedMin + 15 || (st.SpeedMax > 0 && kmh > st.SpeedMax - 15));
                bool blinkOn = outside ? Mathf.Repeat(Time.time, 0.333f) < 0.1667f : near ? Mathf.Repeat(Time.time, 0.5f) < 0.25f : true;
                Color c = outside ? Color.red : near ? Color.yellow : Color.green;
                foreach (var id in new[] { "ID_QUESTPLAYINFO_SPEED_MIN", "ID_QUESTPLAYINFO_SPEED_MAX" })
                {
                    var t = speedView.Control(id) ? speedView.Control(id).GetComponentInChildren<Text>(true) : null;
                    if (t) { t.color = c; t.enabled = blinkOn; }
                }
            }
            // 760000 / 770000: the trail gauge and the "suspicious" bubble when too close
            bool trailing = Trailing && !hide;
            followView = Panel(followView, FollowId, trailing);
            questionView = Panel(questionView, QuestionId, false);
            if (followView)
            {
                float d = MissionPosts.Flat(car.transform.position, target.transform.position);
                FollowGauge(followView, st, d, StepQuests.StepFollowMax(st, Tries));
                questionView = Panel(questionView, QuestionId, d < st.FollowMin + 10);
            }
            if (effectView && Time.time > effectHide) { ui.Close(effectView); effectView = null; }
        }

        static NuiView Panel(NuiView v, int id, bool show)
        {
            var ui = UiManager.Instance;
            if (!show) { if (v && ui) ui.Close(v); return null; }
            if (v && ui.IsOpen(v)) return v;
            v = ui.Open(id);
            if (!v) return null;
            MissionUi.ApplyDialogOffset(v);
            // the digit templates are only copied (K default visible in the data)
            for (int d = 0; d <= 9; d++) v.SetVisible("ID_QUESTPLAYINFO_TIME_NUM" + d, false);
            return v;
        }

        /// <summary>Digit art: TIME_6 TIME_5 minutes, TIME_4 TIME_3 seconds, TIME_2..0 thousandths, copied from the NUMn images.</summary>
        public static void Digits(NuiView v, string slot, string num, float seconds)
        {
            int ms = Mathf.Clamp(Mathf.FloorToInt(seconds * 1000f), 0, 99 * 60000 + 59999);
            int m = ms / 60000, s = ms / 1000 % 60, f = ms % 1000;
            int[] d = { f % 10, f / 10 % 10, f / 100, s % 10, s / 10, m % 10, m / 10 };
            for (int k = 0; k < 7; k++)
            {
                var dst = MissionUi.Image(v, slot + k);
                var src = MissionUi.Image(v, num + d[k]);
                if (!dst || !src) continue;
                dst.texture = src.texture; dst.uvRect = src.uvRect; dst.enabled = src.texture;
            }
        }

        /// <summary>
        /// 0x555f30: FOLLOW_DISTANCE "%dm"; the magenta segments mark the allowed range on the grey bar; the arrow and the
        /// two icons place the target (left) and the driver along it [inferred scale: the bar spans 0 .. max + 20 m].
        /// </summary>
        static void FollowGauge(NuiView v, QuestStep st, float d, int followMax)
        {
            v.SetText("ID_QUESTPLAYINFO_FOLLOW_DISTANCE", $"{Mathf.RoundToInt(d)}m");
            float span = Mathf.Max(20f, followMax + 20f);
            for (int i = 0; i < 10; i++)
            {
                float a = span * i / 10f, b = span * (i + 1) / 10f;
                v.SetVisible("ID_QUESTPLAYINFO_FOLLOW_MAGENTA" + i, b > st.FollowMin && a < (followMax > 0 ? followMax : span));
            }
            var arrow = v.Control("ID_QUESTPLAYINFO_FOLLOW_ARROW");
            var bar = v.Control("ID_QUESTPLAYINFO_FOLLOW_GREY");
            if (arrow && bar)
            {
                var br = (RectTransform)bar.transform;
                var ar = (RectTransform)arrow.transform;
                float x = br.anchoredPosition.x + br.sizeDelta.x * Mathf.Clamp01(d / span);
                ar.anchoredPosition = new Vector2(x - ar.sizeDelta.x / 2f, ar.anchoredPosition.y);
            }
        }

        /// <summary>QUESTPLAYEFFECT 780000: the crash count in large digits for a moment after a crash.</summary>
        void ShowCrashEffect(int max)
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            if (!effectView || !ui.IsOpen(effectView)) { effectView = ui.Open(EffectId); if (effectView) MissionUi.ApplyDialogOffset(effectView); }
            if (!effectView) return;
            for (int d = 0; d <= 9; d++)
            {
                effectView.SetVisible("ID_QUESTPLAYEFFECT_CUR_" + d, d == crashes);
                effectView.SetVisible("ID_QUESTPLAYEFFECT_MAX_" + d, d == Mathf.Min(9, max));
            }
            effectHide = Time.time + 1.5f;
            AudioLibrary.PlaySfx("sfx/SID_CALLSIGN_DENY");
        }

        // ---------------- the quest log ----------------

        /// <summary>QUESTPLAYINFO VIEWQUEST: the running step quest with Give up, the finished ones, then the main quest board.</summary>
        public static void ShowLog()
        {
            var p = P;
            if (p == null) return;
            var rows = new List<ListWindow.Row>();
            var q = StepQuests.Running(p);
            if (q != null)
            {
                bool done = p.StepQuest.Counter > 0;
                string step = done ? GameTables.Text("10178") : q.Steps.Count > p.StepQuest.Step ? $"({p.StepQuest.Step + 1}/{q.Steps.Count}) {q.Steps[p.StepQuest.Step].Text}" : "";
                rows.Add(new ListWindow.Row(MissionText.Get("10183", q.Title), step, done ? "-" : "Give up", () =>
                {
                    if (done) return;
                    ListWindow.CloseOpen();
                    MissionUi.Confirm(MissionText.Get("10194"), Abandon);
                }, !done));
            }
            foreach (var n in p.StepQuestsDone.AsEnumerable().Reverse().Take(30))
            {
                var d = StepQuests.Find(n);
                if (d != null) rows.Add(new ListWindow.Row(MissionText.Get("10195", d.Title), $"{d.Category}  EXP {d.Exp:N0}  Mito {d.Mito:N0}", "", null, false));
            }
            rows.Add(new ListWindow.Row("Mother quests", "The remake's main quest board", "Open", () => { ListWindow.CloseOpen(); QuestTracker.ShowBoard(); }));
            ListWindow.Show("Quests", rows, $"{p.StepQuestsDone.Count} step quests completed   {(Factions.Of(p) == Factions.Roo ? "Team ROO" : "OMD")}");
        }
    }

    /// <summary>
    /// The trail / stake-out car of a step quest: the vehiclelist car of column 71 (a traffic model 20xxx or an HUV 10xxx)
    /// driving path column 69 of &lt;city&gt;\path\path_&lt;city&gt;.chpath (time keys in seconds, played in real time; the client
    /// drives it with 0x4bb980 at speed factor 1.0, the manager's +0x120 for kind 6). A kinematic body: the player can hit it.
    /// </summary>
    public sealed class QuestTrailCar : MonoBehaviour
    {
        public TimedPath Path;
        public float T;
        public float SpeedScale = 1f;
        public bool Finished => Path == null || T >= Path.Duration;

        static readonly Dictionary<string, List<TimedPath>> cache = new Dictionary<string, List<TimedPath>>();

        public static List<TimedPath> Paths(string city)
        {
            if (string.IsNullOrEmpty(city)) return new List<TimedPath>();
            if (cache.TryGetValue(city, out var list)) return list;
            list = new List<TimedPath>();
            var fs = DriftCityData.Files;
            string file = $@"{city}\path\path_{city}.chpath";
            try { if (fs != null && fs.Exists(file)) list = ChPathFile.Parse(fs.Read(file)); }
            catch (Exception ex) { Debug.LogWarning($"{file}: {ex.Message}"); }
            cache[city] = list;
            return list;
        }

        public static QuestTrailCar Spawn(string city, int pathId, int vehicleUid)
        {
            var paths = Paths(city);
            if (pathId < 0 || pathId >= paths.Count || paths[pathId].Pos.Length < 2) return null;
            var go = new GameObject("Quest Trail Car");
            var t = go.AddComponent<QuestTrailCar>();
            t.Path = paths[pathId];
            try { t.BuildVisual(vehicleUid); } catch (Exception ex) { Debug.LogWarning($"trail car {vehicleUid}: {ex.Message}"); }
            t.Place();
            return t;
        }

        void BuildVisual(int uid)
        {
            var fs = DriftCityData.Files;
            GameObject visual = null;
            var huv = GameTables.Huvs.Concat(GameTables.BossHuvs).FirstOrDefault(h => h.UniqueId == uid);
            string body = huv != null ? HuvTarget.BodyNif(huv) : null;
            if (body != null)
            {
                string baseName = System.IO.Path.GetFileNameWithoutExtension(huv.FileName);
                string tire = $@"vehicle\huv\{baseName}_tire.nif";
                if (fs == null || !fs.Exists(tire)) tire = @"vehicle\huv\huv01_a_tire.nif";
                visual = CarFactory.BuildVisual(body, tire, transform.position, transform.rotation, out _, out _);
            }
            else
            {
                string nif = TrafficModel(uid);
                if (nif != null) visual = new NifSceneBuilder { LogErrors = false }.Load(nif, transform);
            }
            if (!visual) { visual = GameObject.CreatePrimitive(PrimitiveType.Cube); visual.transform.localScale = new Vector3(1.9f, 1.4f, 4.5f); visual.transform.localPosition = Vector3.up * 0.7f; }
            visual.transform.SetParent(transform, false);
            foreach (var c in visual.GetComponentsInChildren<Collider>()) Destroy(c);
            var box = gameObject.AddComponent<BoxCollider>();
            box.size = new Vector3(2f, 1.5f, 4.6f); box.center = new Vector3(0, 0.8f, 0);
            var rb = gameObject.AddComponent<Rigidbody>();
            rb.isKinematic = true;
        }

        /// <summary>vehicle\traffic\&lt;file&gt; of a vehiclelist "Traffic" row by Unique Id.</summary>
        static string TrafficModel(int uid)
        {
            var fs = DriftCityData.Files;
            if (fs == null || !fs.Exists(@"init\vehiclelist.xlt")) return null;
            var vl = XltTable.Parse(fs.Read(@"init\vehiclelist.xlt"), headerRow: 3);
            foreach (var r in vl.Rows)
                if (vl.GetInt(r, "Unique Id") == uid)
                {
                    string path = $@"vehicle\traffic\{vl.Get(r, "File Name")}";
                    return fs.Exists(path) ? path : null;
                }
            return null;
        }

        void Place()
        {
            if (Path == null) return;
            var p = DriftCityData.ToUnity(Path.Sample(Mathf.Min(T, Path.Duration), out var d));
            var dir = DriftCityData.ToUnity(d); dir.y = 0;
            if (Physics.Raycast(p + Vector3.up * 30f, Vector3.down, out var hit, 80f, ~0, QueryTriggerInteraction.Ignore) && hit.collider.transform.root != transform) p = hit.point;
            transform.position = p;
            if (dir.sqrMagnitude > 1e-4f) transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
        }

        void Update()
        {
            if (Path == null) return;
            if (!Finished) T += Time.deltaTime * SpeedScale;
            Place();
        }
    }

    /// <summary>Counts the player car's crashes for the quest steps (0x6ff1d0): side contacts with a real impulse.</summary>
    public sealed class QuestCrashSensor : MonoBehaviour
    {
        bool hit, hitTarget;
        GameObject lastOther;

        void OnCollisionEnter(Collision c)
        {
            for (int i = 0; i < c.contactCount; i++)
            {
                var n = c.GetContact(i).normal;
                if (n.y > 0.6f) continue;   // the road
                if (Mathf.Abs(Vector3.Dot(c.relativeVelocity, n)) < 3f) continue;   // a scrape, not a crash
                hit = true;
                lastOther = c.collider ? c.collider.attachedRigidbody ? c.collider.attachedRigidbody.gameObject : c.collider.gameObject : null;
                return;
            }
        }

        /// <summary>A crash since the last call; <paramref name="isTarget"/> when it was the given object.</summary>
        public bool TakeHit(out bool isTarget, GameObject targetObject)
        {
            isTarget = hit && targetObject && lastOther && (lastOther == targetObject || lastOther.transform.IsChildOf(targetObject.transform));
            bool h = hit;
            hit = false; lastOther = null;
            return h;
        }

        /// <summary>Test hook: as if the car had crashed.</summary>
        public void Simulate() => hit = true;
    }
}
