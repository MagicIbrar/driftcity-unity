using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// The Undercity (stage 0x13; DriftCity_RE/UNDERCITY.md): a party mission in an underground version of the city
    /// the party stands in (Skydome_und_sky.nif, init\weatherunder.ini). The client's manager object (game +0x16c,
    /// state machine 0x7190c0 driven by 0x71a420 every frame) runs four phases:
    /// <list type="number">
    /// <item>HUBs: 100 HUB towers (0x716f80): 10 red HUBs beside jump ramps on the city's Red_HUB / Jump posts and 90
    /// roadside towers spread over the lanes. Ram them (red ones from a jump); PLAY1ST counts them.</item>
    /// <item>Main HUB (0x719ae0): barrier HUBs (5 per player beyond the first) shield the Main HUB at the city's MCC
    /// post; UFOs patrol the roads. Three attempts against the jamming clock (UC_MainHUB, UC_Fail, UC_Fail2).</item>
    /// <item>HUV waves (0x718e40 / 0x71a170): the init\uchuvparam.xlt groups of the city, stages 1-4, then the boss
    /// (stage 5), against the clock.</item>
    /// <item>Result (0x716480): UCRESULT for 7 s, then back to the city where the party came from (0x716bc0).</item>
    /// </list>
    /// The timings of the scripted steps are the client's (19.2 s briefing, 5.6 s start animation, 60 s to accept, 6 s
    /// countdown, 5 s briefings, 3 s after a success, 6 s before the result). What the server decided (time limits,
    /// rewards, the HUBs a partner takes) follows the documented rules of <see cref="UndercityData"/>. Offline the party
    /// is the player plus AI partners, who don't drive cars: they take HUBs, barrier HUBs and HUV health on their own
    /// clock and are counted like party members.
    /// </summary>
    public sealed partial class UndercityStage : MonoBehaviour
    {
        /// <summary>The client's states (0x7190c0 cases) this stage goes through.</summary>
        public enum State
        {
            Idle = 0, Ready = 2, StartAnimation = 3, HubAccept = 4, Countdown = 5, Hubs = 6, HubsCleared = 7,
            MainIntro = 8, MainBriefing = 9, MainHub = 10, HuvRequest = 11, HuvWave = 12, BossWave = 13, Ending = 14, Result = 15, Left = 16,
        }

        public enum HubKind { Tower = 0, RedTower = 1, Barrier = 2, Ufo = 3, Main = 4, Jump = 5 }

        public static UndercityStage Active { get; private set; }

        public State Phase { get; private set; }
        public string City { get; private set; }
        public UndercityData.Layout Layout { get; private set; }
        public UndercityHud Hud { get; private set; }
        /// <summary>Multiplies the scripted waits (tests shorten them; 1 = the client's timings).</summary>
        public float TimeScale = 1f;
        /// <summary>Applied to every run's <see cref="TimeScale"/> (tests and tools).</summary>
        public static float GlobalTimeScale = 1f;
        /// <summary>AI partners (0-3); the client needs a party of 2-4 (UC_Err_PlayTogether).</summary>
        public int Partners = 3;
        /// <summary>The jamming clock of the running phase, seconds.</summary>
        public float Clock;
        public int Attempt { get; private set; }
        public int Wave { get; private set; }
        public bool Cleared { get; private set; }
        public bool Failed { get; private set; }
        public float MissionSeconds { get; private set; }
        public UndercityData.Score Score;
        public int UfoBonus { get; private set; }
        public NuiView ResultView { get; private set; }
        public UndercityData.Message LastAlert { get; private set; }
        public IReadOnlyList<Hub> Hubs => hubs;
        public IReadOnlyList<Huv> Huvs => huvs;
        public IReadOnlyList<Partner> Party => partners;
        public bool MainHubDown { get; private set; }

        public sealed class Partner
        {
            public string Name;
            public int Level;
            public UndercityData.Score Score;
            public float Next;
        }

        public sealed class Huv
        {
            public TrafficCar Car;
            public VehicleInfo Info;
            public int Level;
            public float Health, Max;
            public bool Boss;
            public bool Alive => Car && Health > 0;
        }

        CitySession session;
        GameFlow flow;
        DriftCar car;
        Transform root;
        readonly List<Hub> hubs = new List<Hub>();
        readonly List<Huv> huvs = new List<Huv>();
        readonly List<Partner> partners = new List<Partner>();
        Vector3 returnPos;
        Quaternion returnRot;
        float savedOfferInterval;
        int savedMaxCars = -1;
        bool environmentApplied, savedFrozen, leaving;
        GameObject underSky;
        readonly List<GameObject> hiddenSkies = new List<GameObject>();
        ChaseCamera chase;
        int hubsPlaced;
        bool hurry, hurry2, hurry3, warned60, warned15, critical11, accepted;
        float startTime;
        int waveTotal, waveKilled;
        /// <summary>The server's refusal of the run's reward (online), if any.</summary>
        public string RewardError { get; private set; }

        // ---------------- entry points ----------------

        /// <summary>Starts the Undercity of the session's city (the entrance's Accept; also the lead's hook).</summary>
        public static UndercityStage Begin(CitySession session, int partners = 3, float timeScale = 1f)
        {
            if (Active || !session || !GameFlow.Instance) return null;
            if (UndercityData.For(session.City) == null) return null;
            var s = session.gameObject.AddComponent<UndercityStage>();
            s.Partners = Mathf.Clamp(partners, 0, 3);
            s.TimeScale = timeScale * GlobalTimeScale;
            s.StartCoroutine(s.Enter(GameFlow.Instance, session));
            return s;
        }

        /// <summary>The whole run: party wait, loading, the four phases, the result and the way back.</summary>
        public IEnumerator Enter(GameFlow owner, CitySession citySession)
        {
            Active = this;
            flow = owner;
            session = citySession;
            car = owner ? owner.PlayerCar : null;
            City = session ? session.City : owner.City;
            Layout = UndercityData.For(City);
            if (Layout == null || !car) { Finish(); yield break; }
            Hud = gameObject.AddComponent<UndercityHud>();
            returnPos = car.transform.position;
            returnRot = Quaternion.Euler(0, car.transform.eulerAngles.y, 0);
            if (session) { savedOfferInterval = session.MissionOfferInterval; session.MissionOfferInterval = 1e9f; }
            MakeParty();
            root = new GameObject("Undercity").transform;
            if (session && session.Traffic && session.Traffic.transform.parent) root.SetParent(session.Traffic.transform.parent, false);

            // the party is asked (WAITPLAYER); offline the partners answer at once
            Hud.ShowWaiting(true);
            yield return Wait(2f);
            Hud.ShowWaiting(false);

            // GAMELOADING with the Undercity picture, then the underground city (0x76d770 "UnderCity Stage Enter")
            var ui = UiManager.Instance;
            NuiView loading = null, bar = null;
            if (ui)
            {
                loading = ui.Open(160000);
                bar = ui.Open(180000);
                if (loading)
                {
                    for (int i = 0; i < 9; i++) loading.SetVisible("ID_GAMELOADING_TIP0" + i, false);
                    loading.SetVisible("ID_GAMELOADING_MISSIONBATTLE", false);
                    loading.SetVisible("ID_GAMELOADING_UNDERCITY", true);
                }
            }
            yield return null;
            ApplyEnvironment();
            if (bar) for (int i = 0; i < 10; i++) bar.SetVisible("ID_GAMELOADINGBAR_GAUGE0" + i, i < 5);
            yield return null;
            BuildHubPhase();
            TeleportToStart();
            car.ExternalControl = true;
            if (bar) for (int i = 0; i < 10; i++) bar.SetVisible("ID_GAMELOADINGBAR_GAUGE0" + i, true);
            yield return Wait(0.5f);
            if (ui) { ui.Close(loading); ui.Close(bar); }
            AudioLibrary.PlayMusic(this, CityMusic.For(City));
            startTime = Time.time;
            if (UiManager.Instance) UiManager.Instance.On("ID_UC_PLAYEXIT_EXIT", _ => AskQuit());

            // ---- state 2: UC_Ready, 19.2 s (0x4b00 ms) ----
            SetState(State.Ready);
            Alert("UC_Ready");
            yield return Wait(19.2f);
            // ---- state 3: uc_startani, 5.6 s ----
            SetState(State.StartAnimation);
            yield return StartAnimation(5.6f);
            // ---- state 4: UC_HUB_NeedAccept (the party leader) with Accept, 60 s ----
            SetState(State.HubAccept);
            accepted = false;
            Alert("UC_HUB_NeedAccept", 60f, () => accepted = true);
            float wait = 60f * TimeScale;
            while (!accepted && wait > 0) { wait -= Time.deltaTime; yield return null; }
            Hud.CloseAlert();
            // ---- state 5: SID_PVP_RACE_START (0x4ecd) and pvp_countdownstart (UI effect 0x12, 7.5 s), 6 s ----
            SetState(State.Countdown);
            AudioLibrary.PlaySfx("sfx/SID_PVP_RACE_START");
            GameEffects.Screen(0x12, 7.5f);
            yield return Wait(6f);
            car.ExternalControl = false;

            // ---- state 6: the HUBs ----
            SetState(State.Hubs);
            Hud.ShowPhase(0);
            Hud.ShowTip("JUMP");
            Clock = UndercityData.HubPhaseSeconds;
            ResetPartners();
            while (AliveCount(HubKind.Tower, HubKind.RedTower) > 0 && Clock > 0 && !Failed)
            {
                float dt = Time.deltaTime;
                Clock -= dt;
                StepPartners(dt);
                // UC_Hurry / UC_Hurry2 come from the server; here when time runs short
                if (!hurry && Clock < 60f) { hurry = true; Alert("UC_Hurry", 3f); }
                if (!hurry2 && Clock < 20f) { hurry2 = true; Alert("UC_Hurry2", 3f); }
                UpdateHud();
                yield return null;
            }
            if (Failed) yield break;
            if (AliveCount(HubKind.Tower, HubKind.RedTower) == 0)
            {
                // 0x716aa0: UC_HUBSuccess1, state 7 after 3 s
                Alert("UC_HUBSuccess1");
                SetState(State.HubsCleared);
                yield return Wait(3f);
            }
            ClearHubs(HubKind.Tower, HubKind.RedTower, HubKind.Jump);

            // ---- states 8-10: the Main HUB, three attempts ----
            Attempt = 0;
            while (Attempt < UndercityData.MainHubAttempts && !MainHubDown)
            {
                Attempt++;
                SetState(State.MainIntro);
                car.ExternalControl = true;
                ClearHubs(HubKind.Barrier, HubKind.Main, HubKind.Ufo);
                BuildMainHub();
                TeleportToStart();
                yield return StartAnimation(5.6f);
                SetState(State.MainBriefing);
                Hud.ShowPhase(1);
                Alert(Attempt == 1 ? "UC_MainHUB" : Attempt == 2 ? "UC_Fail" : "UC_Fail2");
                Clock = UndercityData.MainHubSeconds;
                UpdateHud();
                yield return Wait(5f);
                car.ExternalControl = false;
                Hud.ShowTip("BARRIER");
                SetState(State.MainHub);
                ResetPartners();
                warned60 = warned15 = critical11 = false;
                while (!MainHubDown && Clock > 0 && !Failed)
                {
                    float dt = Time.deltaTime;
                    Clock -= dt;
                    StepPartners(dt);
                    TickWarnings("UC_Hurry4", "UC_Hurry5");
                    UpdateHud();
                    yield return null;
                }
                if (Failed) yield break;
            }
            if (!MainHubDown) { yield return Fail(); yield break; }
            // 0x718590: UC_HUBSuccess2, the HUV waves after 3 s
            Alert("UC_HUBSuccess2");
            GameEffects.Screen("critical");
            yield return Wait(3f);
            ClearHubs(HubKind.Barrier, HubKind.Main, HubKind.Ufo);

            // ---- states 11-13: the HUV waves of init\uchuvparam.xlt, then the boss ----
            SetState(State.HuvRequest);
            Hud.ShowTip("HUV");
            Clock = UndercityData.HuvWaveSeconds;
            AudioLibrary.PlayMusic(this, "bgm/DriftCity_chasing.mp3");
            string[] waveAlerts = { "UC_HUV", "UC_HUVAgain", "UC_HUVAgain2", "UC_HUVAgain3", "UC_BossHUV" };
            float lastWaveSeconds = 0f;
            for (Wave = 1; Wave <= UndercityData.BossStage; Wave++)
            {
                bool boss = Wave == UndercityData.BossStage;
                var rows = UndercityData.Wave(Layout.CityIndex, Wave);
                if (rows.Count == 0) continue;
                SpawnWave(rows, boss);
                if (huvs.Count == 0) continue;
                SetState(boss ? State.BossWave : State.HuvWave);
                Hud.ShowPhase(boss ? 3 : 2);
                Alert(waveAlerts[Wave - 1]);
                // UC_HUVTimeReduce (0xc3 with a negative change): a slow group costs the next one time (remake rule)
                float cut = !boss && Wave >= 2 ? UndercityData.WaveTimeCut(lastWaveSeconds) : 0f;
                if (cut > 0f) { Clock = Mathf.Max(1f, Clock - cut); LastTimeCut = cut; StartCoroutine(AlertLater("UC_HUVTimeReduce", 2.5f)); }
                // UC_TIP_JUMP: "Jump Attack will increase the BossHUV time!"
                if (boss) Clock = UndercityData.BossSeconds + UndercityData.BossSecondsPerJump * (Score.Jumps + partners.Sum(p => p.Score.Jumps));
                ResetPartners();
                warned60 = warned15 = critical11 = false;
                lastWaveSeconds = 0f;
                while (huvs.Any(h => h.Alive) && Clock > 0 && !Failed)
                {
                    float dt = Time.deltaTime;
                    Clock -= dt;
                    lastWaveSeconds += dt;
                    StepPartners(dt);
                    KeepHuvsNear();
                    if (boss) TickWarnings("UC_TimeCiritical", "UC_TimeCiritical2");
                    else TickWarnings(null, null);
                    UpdateHud();
                    yield return null;
                }
                if (Failed) yield break;
                if (huvs.Any(h => h.Alive)) { yield return Fail(); yield break; }
                ClearHuvs();
            }
            Cleared = true;
            MissionSeconds = Time.time - startTime;

            // ---- state 14: 6 s, state 15: UCRESULT for 7 s, then the city (0x716bc0) ----
            SetState(State.Ending);
            Hud.HidePlay();
            Hud.SetHuvHealth(null, 0);
            Hud.SetParty(null);
            GameEffects.Screen("congratulation");
            AudioLibrary.PlaySfx("sfx/SID_QUEST_SUCCESS");
            yield return Wait(6f);
            yield return ResultAndLeave();
        }

        // ---------------- party (offline AI partners) ----------------

        static readonly string[] PartnerNames = { "Rio", "Duke", "Mika" };

        void MakeParty()
        {
            partners.Clear();
            int lv = PlayerProfile.Current?.Level ?? 1;
            // the offline party (PartyWindow's AI drivers) comes first; AI partners fill the empty slots
            foreach (var m in Social.PartyWindow.Members)
                if (!m.Me && partners.Count < Partners) partners.Add(new Partner { Name = m.Name, Level = Mathf.Max(1, m.Level) });
            for (int i = 0; partners.Count < Partners && i < PartnerNames.Length; i++)
                if (!partners.Any(p => p.Name == PartnerNames[i])) partners.Add(new Partner { Name = PartnerNames[i], Level = Mathf.Max(1, lv + i - 1) });
        }

        void ResetPartners()
        {
            foreach (var p in partners) p.Next = Partner0Delay();
        }

        float Partner0Delay() => Phase == State.Hubs ? UnityEngine.Random.Range(7f, 11f) : Phase == State.MainHub ? UnityEngine.Random.Range(14f, 20f) : UnityEngine.Random.Range(4f, 7f);

        /// <summary>Holds the AI partners (tests, tools).</summary>
        public bool PartnersPaused;

        void StepPartners(float dt)
        {
            if (PartnersPaused || Online) return;   // online the server runs the AI partners
            for (int i = 0; i < partners.Count; i++)
            {
                var p = partners[i];
                p.Next -= dt;
                if (p.Next > 0) continue;
                p.Next = Partner0Delay();
                if (Phase == State.Hubs)
                {
                    // a partner takes a HUB somewhere else in the city (far from the player first)
                    var mine = car ? car.transform.position : Vector3.zero;
                    var pick = hubs.Where(h => h && h.Alive && (h.Kind == HubKind.Tower || h.Kind == HubKind.RedTower))
                                   .OrderByDescending(h => (h.transform.position - mine).sqrMagnitude + UnityEngine.Random.Range(0f, 250000f)).FirstOrDefault();
                    if (pick) Destroyed(pick, i + 1);
                }
                else if (Phase == State.MainHub)
                {
                    var pick = hubs.FirstOrDefault(h => h && h.Alive && h.Kind == HubKind.Barrier);
                    if (pick) Destroyed(pick, i + 1);
                }
                else if (Phase == State.HuvWave || Phase == State.BossWave)
                {
                    var alive = huvs.Where(h => h.Alive).ToList();
                    if (alive.Count > 0) Damage(alive[UnityEngine.Random.Range(0, alive.Count)], 10f, i + 1);
                }
            }
        }

        // ---------------- HUBs ----------------

        /// <summary>One CCenter (0x711a10 / 0x711b40): model, collision box, minimap icon.</summary>
        public sealed class Hub : MonoBehaviour
        {
            public HubKind Kind;
            public int Index;
            public bool Alive = true;
            public bool Shielded;
            public UndercityStage Stage;
            public Vector3 PatrolA, PatrolB;
            float patrolT, patrolDir = 1f;

            float lastTouch = -10f;

            void OnTriggerEnter(Collider other) => Touch(other);

            // a car that stays inside (thrown back by the shield, or parked against a red HUB) keeps touching
            void OnTriggerStay(Collider other)
            {
                if (Time.time - lastTouch > 0.3f) Touch(other);
            }

            void Touch(Collider other)
            {
                if (!Alive || !Stage || Kind == HubKind.Jump) return;
                var rb = other.attachedRigidbody;
                if (!rb || !Stage.car || rb.gameObject != Stage.car.gameObject) return;
                lastTouch = Time.time;
                Stage.Touched(this);
            }

            void Update()
            {
                if (Kind != HubKind.Ufo || !Alive) return;
                // UFOs float back and forth over their stretch of road (0x711110, state 1)
                patrolT += patrolDir * Time.deltaTime * 0.12f;
                if (patrolT > 1f || patrolT < 0f) { patrolDir = -patrolDir; patrolT = Mathf.Clamp01(patrolT); }
                transform.position = Vector3.Lerp(PatrolA, PatrolB, patrolT);
                transform.Rotate(0, 60f * Time.deltaTime, 0, Space.World);
            }
        }

        static readonly string[] Models =
        {
            @"effects\undercity\ob_h_uc_00001a_01_idle01.nif", @"effects\undercity\ob_h_uc_00001a_02.nif", @"effects\undercity\ob_h_uc_00001a_03.nif",
            @"effects\undercity\ufo.nif", @"effects\undercity\ob_h_uc_main_00001a_01.nif", @"effects\undercity\track_jump01_01.nif",
        };
        static readonly string[] TowerIdle =
        {
            @"effects\undercity\ob_h_uc_00001a_01_idle01.nif", @"effects\undercity\ob_h_uc_00001a_01_idle02.nif", @"effects\undercity\ob_h_uc_00001a_01_idle03.nif",
        };

        Hub MakeHub(HubKind kind, int index, Vector3 pos, Quaternion rot)
        {
            string model = kind == HubKind.Tower ? TowerIdle[index % TowerIdle.Length] : Models[(int)kind];
            var go = new GameObject($"UC {kind} {index}");
            go.transform.SetParent(root, false);
            go.transform.SetPositionAndRotation(Ground(pos, kind == HubKind.Ufo ? 0f : 0.05f), rot);
            if (kind == HubKind.Ufo) go.transform.position += Vector3.up * 6f;
            var fs = DriftCityData.Files;
            if (fs != null && fs.Exists(model))
            {
                try
                {
                    var visual = new NifSceneBuilder { LogErrors = false }.Load(model, go.transform);
                    foreach (var c in visual.GetComponentsInChildren<Collider>()) Destroy(c);
                }
                catch (Exception ex) { Debug.LogWarning($"Undercity {model}: {ex.Message}"); }
            }
            var hub = go.AddComponent<Hub>();
            hub.Kind = kind; hub.Index = index; hub.Stage = this;
            if (kind == HubKind.Jump)
            {
                // the ramp is driven on: solid collision from its own mesh
                foreach (var mf in go.GetComponentsInChildren<MeshFilter>())
                    if (mf.sharedMesh && mf.GetComponent<Renderer>() && mf.GetComponent<Renderer>().enabled) mf.gameObject.AddComponent<MeshCollider>().sharedMesh = mf.sharedMesh;
            }
            else
            {
                // collision boxes of 0x711a10 / 0x711b40 (Gamebryo x, y, z): 6x6x8 UFOs, 22x16x8 the Main HUB, 4x4x8 the rest
                var box = go.AddComponent<BoxCollider>();
                box.isTrigger = true;
                box.size = kind == HubKind.Main ? new Vector3(22f, 8f, 16f) : kind == HubKind.Ufo ? new Vector3(6f, 8f, 6f) : new Vector3(4f, 8f, 4f);
                box.center = new Vector3(0, kind == HubKind.Ufo ? -4f : 4f, 0);
            }
            hubs.Add(hub);
            return hub;
        }

        static Vector3 Ground(Vector3 p, float lift)
        {
            Physics.SyncTransforms();
            var hits = Physics.RaycastAll(p + Vector3.up * 40f, Vector3.down, 120f, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue; Vector3 at = p;
            foreach (var h in hits)
            {
                if (h.collider.attachedRigidbody) continue;
                float d = Mathf.Abs(h.point.y - p.y);
                if (d < best) { best = d; at = h.point; }
            }
            return at + Vector3.up * lift;
        }

        /// <summary>The road map's tower picks of the last HUB phase (tests; empty when the lanes were sampled instead).</summary>
        public List<UndercityRoads.Pick> TowerPicks { get; private set; } = new List<UndercityRoads.Pick>();

        /// <summary>0x716f80: red HUBs 0-9 on the Red_HUB posts, their ramps (slots 100-109) facing them, then the towers
        /// of the road map up to slot 99 (0x716890, UndercityRoads).</summary>
        void BuildHubPhase()
        {
            for (int i = 0; i < UndercityData.RedHubCount; i++)
            {
                var red = Layout.RedHubs[i]; var jump = Layout.Jumps[i];
                if (red == null) continue;
                MakeHub(HubKind.RedTower, i, red.Position, Quaternion.identity);
                if (jump != null) MakeRamp(100 + i, jump.Position, red.Position);
            }
            hubsPlaced = UndercityData.RedHubCount;
            var traffic = session ? session.Traffic : null;
            var net = traffic ? traffic.Network : null;
            // 0x716890: the road map's junctions, one tower per road met on lane "slot % lanes" of its side, in the
            // middle of that lane, the city's skipped roads left out (UndercityRoads)
            var picks = net != null && net.Lanes.Count > 0 ? UndercityRoads.TowerPicks(UndercityRoads.For(City), Layout.CityIndex, UndercityData.RedHubCount, UndercityData.TowerSlots - 1) : new List<UndercityRoads.Pick>();
            if (picks.Count > 0)
            {
                var laneOf = new Dictionary<int, int>();
                for (int i = 0; i < net.Lanes.Count; i++) if (!laneOf.ContainsKey(net.Lanes[i].Id)) laneOf[net.Lanes[i].Id] = i;
                foreach (var pk in picks)
                {
                    if (!laneOf.TryGetValue(pk.LaneId, out int lane)) continue;
                    if (!traffic.Sample(lane, net.Lanes[lane].Length * 0.5f, out var pos, out var fwd)) continue;
                    fwd.y = 0;
                    MakeHub(HubKind.Tower, pk.Slot, pos, fwd.sqrMagnitude > 0.001f ? Quaternion.LookRotation(fwd.normalized, Vector3.up) : Quaternion.identity);
                    hubsPlaced = Mathf.Max(hubsPlaced, pk.Slot + 1);
                }
                TowerPicks = picks;
            }
            else if (net != null && net.Lanes.Count > 0)
            {
                // no road map (a city without one): the lanes sampled evenly so the 90 towers cover the whole city
                var lanes = Enumerable.Range(0, net.Lanes.Count).Where(i => net.Lanes[i].Length >= 40f).ToList();
                var taken = new List<Vector3>();
                foreach (var h in hubs) taken.Add(h.transform.position);
                int want = UndercityData.TowerSlots - UndercityData.RedHubCount;
                int stride = Mathf.Max(1, lanes.Count / want);
                for (int offset = 0; offset < stride && hubsPlaced < UndercityData.TowerSlots; offset++)
                    for (int j = offset; j < lanes.Count && hubsPlaced < UndercityData.TowerSlots; j += stride)
                    {
                        int lane = lanes[j];
                        float len = net.Lanes[lane].Length;
                        // the segment of the lane follows the slot number, as the exe's "slot % segments"
                        float along = len * (0.2f + 0.6f * (hubsPlaced * 37 % 100) / 100f);
                        if (!traffic.Sample(lane, along, out var pos, out var fwd)) continue;
                        if (taken.Any(t => (t - pos).sqrMagnitude < 60f * 60f)) continue;
                        if (Layout.Starts[0] != null && (Layout.Starts[0].Position - pos).sqrMagnitude < 40f * 40f) continue;
                        taken.Add(pos);
                        fwd.y = 0;
                        MakeHub(HubKind.Tower, hubsPlaced++, pos, fwd.sqrMagnitude > 0.001f ? Quaternion.LookRotation(fwd.normalized, Vector3.up) : Quaternion.identity);
                    }
            }
            HubTotal = CountOf(HubKind.Tower, HubKind.RedTower);
        }

        void MakeRamp(int index, Vector3 at, Vector3 towards)
        {
            var dir = towards - at; dir.y = 0;
            var rot = dir.sqrMagnitude > 0.01f ? Quaternion.LookRotation(dir.normalized, Vector3.up) : Quaternion.identity;
            var ramp = MakeHub(HubKind.Jump, index, at, rot);
            // the ramp model rises along its long axis: turn it so the high end points at the HUB
            var rs = ramp.GetComponentsInChildren<MeshFilter>().Where(m => m.sharedMesh).ToList();
            if (rs.Count == 0) return;
            float hiZ = 0, hiY = float.MinValue;
            foreach (var mf in rs)
                foreach (var v in mf.sharedMesh.vertices)
                {
                    var w = ramp.transform.InverseTransformPoint(mf.transform.TransformPoint(v));
                    if (w.y > hiY + 0.01f) { hiY = w.y; hiZ = w.z; }
                }
            if (hiZ < 0f) ramp.transform.rotation = rot * Quaternion.Euler(0, 180f, 0);
        }

        /// <summary>0x719ae0: the Main HUB on its post facing MCC_Direction, the barrier HUBs for the party size and
        /// UFOs over the roads (the lane objects of type 3).</summary>
        void BuildMainHub()
        {
            MainHubDown = false;
            var rot = UndercityData.Layout.Facing(Layout.Mcc, Layout.MccDirection);
            var main = MakeHub(HubKind.Main, 0, Layout.Mcc.Position, rot);
            main.Shielded = true;
            // the shield effect stays while barrier HUBs stand
            if (DriftCityData.Files.Exists(@"effects\undercity\ob_h_uc_main_00001a_01_fx.nif"))
            {
                try
                {
                    var fx = new NifSceneBuilder { LogErrors = false }.Load(@"effects\undercity\ob_h_uc_main_00001a_01_fx.nif", main.transform);
                    fx.name = "Shield";
                }
                catch (Exception ex) { Debug.LogWarning("Undercity shield: " + ex.Message); }
            }
            var barriers = Layout.BarriersFor(1 + partners.Count);
            for (int i = 0; i < barriers.Count; i++) MakeHub(HubKind.Barrier, 1 + i, barriers[i].Position, Quaternion.identity);
            UfoBonus = 0;
            var traffic = session ? session.Traffic : null;
            var net = traffic ? traffic.Network : null;
            if (net == null || net.Lanes.Count == 0) return;
            int placed = 0;
            for (int i = 0; i < net.Lanes.Count && placed < 16; i += Mathf.Max(1, net.Lanes.Count / 16))
            {
                var lane = net.Lanes[i];
                if (lane.Length < 80f) continue;
                if (!traffic.Sample(i, lane.Length * 0.3f, out var a, out _) || !traffic.Sample(i, lane.Length * 0.3f + 60f, out var b, out _)) continue;
                var ufo = MakeHub(HubKind.Ufo, 200 + placed++, a, Quaternion.identity);
                ufo.PatrolA = ufo.transform.position;
                ufo.PatrolB = Ground(b, 0f) + Vector3.up * 6f;
            }
        }

        /// <summary>HUBs and red HUBs placed for the HUB phase (PLAY1ST's total; a destroyed HUB's object goes away).</summary>
        public int HubTotal { get; private set; }

        public int AliveCount(params HubKind[] kinds) => hubs.Count(h => h && h.Alive && kinds.Contains(h.Kind));
        public int CountOf(params HubKind[] kinds) => hubs.Count(h => h && kinds.Contains(h.Kind));

        void ClearHubs(params HubKind[] kinds)
        {
            foreach (var h in hubs.Where(h => h && kinds.Contains(h.Kind)).ToList()) Destroy(h.gameObject);
            hubs.RemoveAll(h => !h || kinds.Contains(h.Kind));
        }

        /// <summary>The player's car touched a HUB's box.</summary>
        void Touched(Hub hub)
        {
            if (!hub.Alive) return;
            if (Online) { TouchedOnline(hub); return; }
            switch (hub.Kind)
            {
                case HubKind.Tower:
                case HubKind.Barrier:
                    if (Phase == State.Hubs || Phase == State.MainHub) Destroyed(hub, 0);
                    break;
                case HubKind.RedTower:
                    // red HUBs stand beside a jump ramp: they fall to a car coming off it (airborne, or just landed)
                    if (Phase == State.Hubs && (car.Airborne || Time.time - lastAirborne < 2.5f)) Destroyed(hub, 0);
                    else Bounce(hub.transform.position, 8f);
                    break;
                case HubKind.Ufo:
                    if (Phase != State.MainHub) break;
                    Destroyed(hub, 0);
                    Clock += UndercityData.UfoBonusSeconds;
                    UfoBonus += (int)UndercityData.UfoBonusSeconds;
                    Alert("UC_TimeSave");
                    break;
                case HubKind.Main:
                    if (Phase != State.MainHub) break;
                    if (AliveCount(HubKind.Barrier) > 0)
                    {
                        // the shield throws the car back (0x7137b0: 20000 along the line from the HUB)
                        Bounce(hub.transform.position, 18f);
                        GameEffects.Screen(51);   // effects/undercity/changLR
                        AudioLibrary.PlaySfx("sfx/SID_CHANGE_CRASH");
                    }
                    else Destroyed(hub, 0);
                    break;
            }
        }

        float lastAirborne = -10f;

        void FixedUpdate()
        {
            if (car && car.Airborne) lastAirborne = Time.time;
        }

        void Bounce(Vector3 from, float speed)
        {
            if (!car) return;
            var rb = car.GetComponent<Rigidbody>();
            var away = car.transform.position - from; away.y = 0;
            if (away.sqrMagnitude < 0.01f) away = -car.transform.forward;
            rb.linearVelocity = away.normalized * speed + Vector3.up * 2f;
        }

        /// <summary>A HUB goes down (the server's 0xbbd / 0xbc7 / 0xbc8 replies): count it for its driver (0 = the
        /// player, 1-3 partners), explode it, update the panels.</summary>
        public void Destroyed(Hub hub, int who)
        {
            if (!hub || !hub.Alive) return;
            hub.Alive = false;
            var s = who == 0 ? Score : partners[who - 1].Score;
            switch (hub.Kind)
            {
                case HubKind.Tower: s.Hubs++; break;
                case HubKind.RedTower: s.Jumps++; break;
                case HubKind.Barrier: s.Barriers++; break;
                case HubKind.Main: s.MainHub++; MainHubDown = true; break;
            }
            if (who == 0) Score = s; else partners[who - 1].Score = s;
            var pos = hub.transform.position;
            if (hub.Kind != HubKind.Ufo) GameEffects.At(@"undercity\ob_h_uc_00001a_01_explo", pos);
            if (who == 0 && car) GameEffects.Attach(@"undercity\explo_car_center", car.transform, Vector3.zero, true);
            AudioLibrary.PlaySfx(who == 0 ? "sfx/SID_HUV_EXPLOSION" : "sfx/SID_HIT_HUV1", who == 0 ? 1f : 0.35f);
            if (hub.Kind == HubKind.Main) Hud.Flash(true);
            else if (who == 0) Hud.Flash(false, 1.2f);
            Destroy(hub.gameObject, 0.05f);
            if (hub.Kind == HubKind.Barrier && AliveCount(HubKind.Barrier) == 0)
                foreach (var m in hubs.Where(h => h && h.Kind == HubKind.Main))
                {
                    m.Shielded = false;
                    var shield = m.transform.Find("Shield");
                    if (shield) Destroy(shield.gameObject);
                }
            // 0x718370: UC_Hurry3 once 80 of the HUBs are down
            int down = HubTotal - AliveCount(HubKind.Tower, HubKind.RedTower);
            if (Phase == State.Hubs && !hurry3 && down >= 80) { hurry3 = true; Alert("UC_Hurry3"); }
            UpdateHud();
        }

        // ---------------- HUVs ----------------

        void SpawnWave(List<UndercityData.HuvRow> rows, bool boss)
        {
            ClearHuvs();
            waveKilled = 0;
            var traffic = session ? session.Traffic : null;
            if (!traffic) return;
            float hubsMissed = 1f - Mathf.Clamp01((Score.Hubs + Score.Jumps + partners.Sum(p => p.Score.Hubs + p.Score.Jumps)) / (float)Mathf.Max(1, hubsPlaced));
            foreach (var row in rows)
            {
                var info = UndercityData.Huv(row.HuvId);
                for (int n = 0; n < row.Num; n++)
                {
                    var h = info != null ? SpawnHuv(traffic, info, row.Level, boss) : null;
                    // Boss Huv080 (Cras, huv_boss09.kfm) has no NIF body in this install: another Undercity boss stands in
                    if (h == null && boss)
                        foreach (var alt in new[] { 11009, 11010, 11011 }.Select(UndercityData.Huv).Where(a => a != null).Concat(GameTables.BossHuvs))
                            if ((h = SpawnHuv(traffic, alt, row.Level, true)) != null) break;
                    if (h == null && !boss && GameTables.Huvs.Count > 0)
                        h = SpawnHuv(traffic, GameTables.Huvs[UnityEngine.Random.Range(0, GameTables.Huvs.Count)], row.Level, false);
                    if (h == null) continue;
                    // UC_TIP_HUV: "The less HUBs destroyed, the more HUV HP will replenish"
                    h.Max = h.Health = (boss ? 200f + 2f * row.Level : 30f + row.Level * 0.5f) * (1f + 0.5f * hubsMissed);
                    huvs.Add(h);
                }
            }
            waveTotal = huvs.Count;
        }

        Huv SpawnHuv(TrafficSystem traffic, VehicleInfo info, int level, bool boss)
        {
            string file = info.FileName ?? "";
            // huv_boss09.kfm: the body is the NIF beside the animation file
            if (file.EndsWith(".kfm", StringComparison.OrdinalIgnoreCase)) file = System.IO.Path.ChangeExtension(file, ".nif");
            string body = $@"vehicle\huv\{file}";
            if (!DriftCityData.Files.Exists(body)) return null;
            string baseName = System.IO.Path.GetFileNameWithoutExtension(file);
            string tire = $@"vehicle\huv\{baseName}_tire.nif";
            if (!DriftCityData.Files.Exists(tire)) tire = @"vehicle\huv\huv01_a_tire.nif";
            GameObject visual;
            try { visual = CarFactory.BuildVisual(body, tire, new Vector3(0, -2000, 0), Quaternion.identity, out _, out _); }
            catch (Exception ex) { Debug.LogWarning($"Undercity HUV {body}: {ex.Message}"); return null; }
            visual.SetActive(false);
            bool ok = traffic.TrySpawn(out var tc, visual, 80f, 260f);
            Destroy(visual);
            if (!ok) return null;
            tc.transform.SetParent(root, true);
            tc.name = "HUV " + info.UiName;
            tc.Erratic = true;
            tc.CruiseKmh = Mathf.Clamp(70f + level * 0.8f, 80f, 140f) * (boss ? 1.1f : 1f);
            var h = new Huv { Car = tc, Info = info, Level = level, Boss = boss };
            tc.Hit += c => OnHuvHit(h, c);
            return h;
        }

        void OnHuvHit(Huv h, Collision c)
        {
            if (!h.Alive || !car || c.rigidbody != car.GetComponent<Rigidbody>()) return;
            float kmh = c.relativeVelocity.magnitude * 3.6f;
            if (kmh < 12f) return;
            AudioLibrary.PlaySfx("sfx/SID_BOSSHUV_0" + UnityEngine.Random.Range(2, 6));
            if (Online) { ReportHit(h, Mathf.Clamp(kmh * 0.45f, 6f, 45f)); return; }
            Damage(h, Mathf.Clamp(kmh * 0.45f, 6f, 45f), 0);
        }

        /// <summary>Takes health off an HUV; at zero it explodes and counts for <paramref name="who"/> (0 = the player).</summary>
        public void Damage(Huv h, float amount, int who)
        {
            if (h == null || !h.Alive) return;
            // 0xc0 HuvHit: every hit counts on the member's PARTYUC counter
            if (who == 0) Score.Hits++; else if (who - 1 < partners.Count) partners[who - 1].Score.Hits++;
            h.Health -= amount;
            if (h.Health > 0) return;
            h.Health = 0;
            waveKilled++;
            var s = who == 0 ? Score : partners[who - 1].Score;
            if (h.Boss) s.Boss++; else s.Huvs++;
            if (who == 0) Score = s; else partners[who - 1].Score = s;
            GameEffects.HuvExplosion(h.Car.transform);
            if (h.Boss && DriftCityData.Files.Exists(@"effects\undercity\huv_boss06_capsule_explo.nif"))
                GameEffects.At(@"undercity\huv_boss06_capsule_explo", h.Car.transform.position);
            Hud.Flash(false, 2f);
            h.Car.CruiseKmh = 0; h.Car.Speed = 0;
            Destroy(h.Car.gameObject, 3f);
            UpdateHud();
        }

        /// <summary>HUVs that wandered far are put back on a lane near the party.</summary>
        void KeepHuvsNear()
        {
            if (!car || !session || !session.Traffic) return;
            foreach (var h in huvs)
            {
                if (!h.Alive) continue;
                if ((h.Car.transform.position - car.transform.position).sqrMagnitude < 900f * 900f) continue;
                if (session.Traffic.TryPickLane(150f, 350f, out int lane, out float along)) h.Car.Place(lane, along);
            }
        }

        void ClearHuvs()
        {
            foreach (var h in huvs) if (h.Car) Destroy(h.Car.gameObject);
            huvs.Clear();
        }

        // ---------------- panels, warnings, alerts ----------------

        void UpdateHud()
        {
            if (!Hud) return;
            switch (Phase)
            {
                case State.Hubs:
                case State.HubsCleared:
                    Hud.SetHubs(AliveCount(HubKind.Tower, HubKind.RedTower), HubTotal);
                    Hud.SetParty(PartyCounts(s => s.PartyUc));
                    break;
                case State.MainBriefing:
                case State.MainHub:
                    // +NN (HUD +0x358): the barriers I destroyed myself
                    Hud.SetMainHub(Clock, CountOf(HubKind.Barrier) - AliveCount(HubKind.Barrier), CountOf(HubKind.Barrier), Score.Barriers);
                    Hud.SetParty(PartyCounts(s => s.PartyUc));
                    break;
                case State.HuvWave:
                case State.BossWave:
                    float total = huvs.Sum(h => h.Max), left = huvs.Sum(h => Mathf.Max(0, h.Health));
                    if (Phase == State.BossWave) Hud.SetBoss(Clock, huvs.Count(h => h.Alive), waveTotal, total > 0 ? left / total : 0);
                    else Hud.SetHuvs(Clock, waveKilled, waveTotal, total > 0 ? left / total : 0);
                    Hud.SetParty(PartyCounts(s => s.PartyUc));
                    var near = car ? huvs.Where(h => h.Alive).OrderBy(h => (h.Car.transform.position - car.transform.position).sqrMagnitude).FirstOrDefault() : null;
                    if (near != null && (near.Car.transform.position - car.transform.position).sqrMagnitude < 200f * 200f) Hud.SetHuvHealth(near.Info.CarName ?? near.Info.UiName, near.Health / near.Max);
                    else Hud.SetHuvHealth(null, 0);
                    break;
            }
        }

        List<int> PartyCounts(Func<UndercityData.Score, int> f)
        {
            // PARTYUC (0x6850d0): one running counter per party member in the party's order (P1 = members[0])
            if (Online) return SlotCounts();
            var list = new List<int> { f(Score) };
            foreach (var p in partners) list.Add(f(p.Score));
            return list;
        }

        /// <summary>0x71a420 states 10 / 13: the alerts under 60 and 15 s, and under 11 s SID_PVP_RACE_COUNT (0x4ed3)
        /// with pvp_tencount (UI effect 0x1d, 10 s).</summary>
        void TickWarnings(string at60, string at15)
        {
            if (!warned60 && Clock < 60f) { warned60 = true; if (at60 != null) Alert(at60); }
            if (!warned15 && Clock < 15f) { warned15 = true; if (at15 != null) Alert(at15); }
            if (!critical11 && Clock < 11f)
            {
                critical11 = true;
                GameEffects.Screen(0x1d, 10f);
                AudioLibrary.PlaySfx("sfx/SID_PVP_RACE_COUNT");
            }
        }

        /// <summary>The seconds the last UC_HUVTimeReduce took off the clock (tests).</summary>
        public float LastTimeCut { get; private set; }

        /// <summary>An alert after the current one had its seconds (UC_HUVTimeReduce follows UC_HUVAgain*).</summary>
        IEnumerator AlertLater(string key, float seconds)
        {
            float t = seconds * Mathf.Max(0.05f, TimeScale);
            while (t > 0 && !leaving) { t -= Time.deltaTime; yield return null; }
            if (!leaving && !Failed) Alert(key);
        }

        void Alert(string key, float seconds = 0f, Action accept = null)
        {
            var m = UndercityData.Text(key);
            LastAlert = m;
            if (Hud) Hud.Alert(m, seconds, accept);
            Net.ChatBox.System("[Undercity] " + string.Join(" ", m.Pages.SelectMany(p => p)));
        }

        /// <summary>Presses Accept on the "mission is ready" alert (the button, Enter or Tab).</summary>
        public void Accept()
        {
            if (Hud && !Hud.PressAccept() && Phase == State.HubAccept) accepted = true;
        }

        void SetState(State s)
        {
            Phase = s;
            Debug.Log($"Undercity {City}: state {(int)s} {s}");
            UpdateHud();
        }

        IEnumerator Wait(float seconds)
        {
            float t = seconds * Mathf.Max(0f, TimeScale);
            while (t > 0 && !Failed && !leaving) { t -= Time.deltaTime; yield return null; }
        }

        // ---------------- start positions and the start animation ----------------

        /// <summary>The start post of this driver (0x718260 puts party member n on Start n+1).</summary>
        public int StartIndex;

        void TeleportToStart()
        {
            int k = Mathf.Clamp(StartIndex, 0, 3);
            var s = Layout.Starts[k] ?? Layout.Starts[0];
            if (s == null || !car) return;
            var rot = UndercityData.Layout.Facing(s, Layout.Directions[k] ?? Layout.Directions[0]);
            var pos = Ground(s.Position, 0.8f);
            RaceMission.Teleport(car, pos, rot);
            car.SetSpawn(pos, rot);
            if (Net.OnlineSession.Instance) Net.OnlineSession.Instance.MarkTeleport();
        }

        /// <summary>camera.ini "uc_startani": effects\UnderCity\uc_cam&lt;city&gt;.nif carries the camera node; it looks at the party.</summary>
        IEnumerator StartAnimation(float seconds)
        {
            var cam = Camera.main;
            if (!cam || !car) { yield return Wait(seconds); yield break; }
            chase = cam.GetComponent<ChaseCamera>();
            if (chase) chase.enabled = false;
            Transform camNode = null;
            GameObject anim = null;
            string path = $@"effects\undercity\uc_cam{Layout.CityIndex}.nif";
            if (DriftCityData.Files.Exists(path))
            {
                try
                {
                    anim = new NifSceneBuilder { LogErrors = false }.Load(path, root);
                    camNode = anim.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Cam");
                }
                catch (Exception ex) { Debug.LogWarning("Undercity start camera: " + ex.Message); }
            }
            float t = 0, total = seconds * Mathf.Max(0f, TimeScale);
            var target = car.transform.position + Vector3.up * 1.2f;
            var orbitFrom = target + car.transform.forward * 18f + Vector3.up * 8f;
            while (t < total && !Failed && !leaving)
            {
                float k = total > 0 ? t / total : 1;
                Vector3 p = camNode ? camNode.position : Vector3.zero;
                if (!camNode || (p - target).sqrMagnitude > 400f * 400f)
                    p = Quaternion.AngleAxis(140f * k, Vector3.up) * (orbitFrom - target) + target;
                cam.transform.position = p;
                cam.transform.rotation = Quaternion.LookRotation((target - p).sqrMagnitude > 0.01f ? (target - p).normalized : Vector3.forward, Vector3.up);
                t += Time.deltaTime;
                yield return null;
            }
            if (anim) Destroy(anim);
            if (chase) chase.enabled = true;
        }

        // ---------------- environment (Skydome_und_sky.nif, init\weatherunder.ini) ----------------

        void ApplyEnvironment()
        {
            if (environmentApplied) return;
            environmentApplied = true;
            savedFrozen = WeatherClock.Frozen;
            WeatherClock.Frozen = true;
            if (session && session.Traffic) { savedMaxCars = session.Traffic.MaxCars; session.Traffic.MaxCars = Mathf.Min(savedMaxCars, 16); session.Traffic.TrimTo(16); }
            // the weather's dome goes, the underground dome of the city comes (skybox_01..04 = 1st..4th city)
            foreach (var f in FindObjectsByType<SkyFollow>(FindObjectsSortMode.None))
                if (f && f.gameObject.activeSelf) { hiddenSkies.Add(f.gameObject); f.gameObject.SetActive(false); }
            if (DriftCityData.Files.Exists(@"effects\skydome_und_sky.nif"))
            {
                try
                {
                    underSky = new NifSceneBuilder().Load(@"effects\skydome_und_sky.nif", root);
                    underSky.name = "Undercity Sky";
                    var shader = Shader.Find("DriftCity/Sky");
                    foreach (var r in underSky.GetComponentsInChildren<Renderer>(true))
                    {
                        if (shader)
                        {
                            var m = OwnedAssets.Track(underSky, new Material(shader) { name = "sky " + r.name });
                            m.mainTexture = r.sharedMaterial ? r.sharedMaterial.mainTexture : null;
                            r.sharedMaterial = m;
                        }
                        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                        r.receiveShadows = false;
                    }
                    string want = $"skybox_{Layout.CityIndex:00}";
                    foreach (Transform c in underSky.transform) c.gameObject.SetActive(c.name.Equals(want, StringComparison.OrdinalIgnoreCase));
                    underSky.AddComponent<SkyFollow>();
                }
                catch (Exception ex) { Debug.LogWarning("Undercity sky: " + ex.Message); }
            }
            ApplyUnderWeather();
        }

        /// <summary>The first weather of init\weatherunder.ini, set the way OriginalLook.Apply sets init\weather.ini's.</summary>
        static void ApplyUnderWeather()
        {
            var ini = OriginalLook.Ini(@"init\weatherunder.ini");
            if (!ini.TryGetValue("1", out var w)) return;
            string G(string k, string d) => w.TryGetValue(k, out var v) && v.Length > 0 ? v : d;
            Color Rgb(string s)
            {
                var p = s.Split('.', ':');
                float C(int i) => i < p.Length && float.TryParse(p[i].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v / 255f : 1f;
                return new Color(C(0), C(1), C(2), 1f);
            }
            void Light(string prefix, string a, string d)
            {
                float alt = OriginalLook.Num(G(prefix + "altitude", a)), dir = OriginalLook.Num(G(prefix + "direction", "-45")), dim = OriginalLook.Num(G(prefix + "intensity", "1"));
                string key = prefix == "ground_" ? "Ground" : "Car";
                Shader.SetGlobalVector($"_DC{key}Dir", OriginalLook.Direction(alt, dir));
                Shader.SetGlobalVector($"_DC{key}Amb", Rgb(G(prefix + "ambient", "128.128.128")) * dim);
                Shader.SetGlobalVector($"_DC{key}Diff", Rgb(G(prefix + "diffuse", d)) * dim);
                Shader.SetGlobalVector($"_DC{key}Spec", Rgb(G(prefix + "specular", "160.160.160")) * dim);
                if (prefix == "ground_") RenderSettings.ambientLight = Rgb(G(prefix + "ambient", "128.128.128")) * dim;
            }
            Light("ground_", "40", "170.170.230");
            Light("car_", "89", "170.170.230");
            Shader.SetGlobalFloat("_DCShadowDark", OriginalLook.Num(G("shadowdark", "0.5")));
            Shader.SetGlobalFloat("_DCShadowAffect", OriginalLook.Num(G("shadowaffect", "0.4")));
            Shader.SetGlobalFloat("_DCEnvStrength", OriginalLook.Num(G("envmapstrength", "0.2")));
            RenderSettings.fog = G("fogenable", "true").Trim().ToLowerInvariant() == "true";
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = Rgb(G("fogcolor", "46.65.114"));
            RenderSettings.fogEndDistance = OriginalLook.ChaseFar;
            RenderSettings.fogStartDistance = OriginalLook.ChaseFar * (1f - Mathf.Clamp01(OriginalLook.Num(G("fogdepth", "0.7"))));
            if (Camera.main) Camera.main.backgroundColor = RenderSettings.fogColor;
        }

        void RestoreEnvironment()
        {
            if (!environmentApplied) return;
            environmentApplied = false;
            WeatherClock.Frozen = savedFrozen;
            foreach (var s in hiddenSkies) if (s) s.SetActive(true);
            hiddenSkies.Clear();
            if (underSky) Destroy(underSky);
            if (session && session.Traffic && savedMaxCars > 0) session.Traffic.MaxCars = savedMaxCars;
            var clock = FindAnyObjectByType<WeatherClock>();
            if (clock) clock.Refresh();
            else if (GameFlow.Instance) GameFlow.Instance.ReapplyLighting();
        }

        // ---------------- failure, result, leaving ----------------

        IEnumerator Fail()
        {
            Failed = true;
            MissionSeconds = startTime > 0 ? Time.time - startTime : 0f;
            Hud.HidePlay();
            Hud.SetHuvHealth(null, 0);
            Hud.SetParty(null);
            Alert("UC_Fail", 4f);
            GameEffects.Screen("quest_fail");
            AudioLibrary.PlaySfx("sfx/SID_QUEST_FAIL");
            leaving = false;
            float t = 3f * Mathf.Max(0f, TimeScale);
            while (t > 0) { t -= Time.deltaTime; yield return null; }
            yield return ResultAndLeave();
        }

        /// <summary>PLAYEXIT's button ("Esc Exit Undercity"): asks, then <see cref="Quit"/>. GameFlow's Esc can call it too.</summary>
        public void AskQuit()
        {
            if (Failed || Phase < State.Hubs || Phase >= State.Ending) return;
            MissionUi.Confirm("Leave the Undercity? The mission fails.", Quit);
        }

        /// <summary>The city went away under the run (travel, logout): clean up without the way back.</summary>
        void LateUpdate()
        {
            if (Active == this && (!session || !car) && Phase != State.Idle) { StopAllCoroutines(); Finish(); return; }
            // online: the connection went away under the run - back to the city
            if (Active == this && Online && !leaving && !Social.SocialNet.Online && Phase != State.Idle) { StopAllCoroutines(); Leave(); }
        }

        /// <summary>Leaves through PLAYEXIT (confirmed): the run fails.</summary>
        public void Quit()
        {
            if (Failed || Phase >= State.Ending) return;
            StopAllCoroutines();
            StartCoroutine(Online ? QuitOnline() : Fail());
        }

        IEnumerator ResultAndLeave()
        {
            SetState(State.Result);
            ClearHuvs();
            ClearHubs(Enum.GetValues(typeof(HubKind)).Cast<HubKind>().ToArray());
            var p = PlayerProfile.Current;
            int level = p?.Level ?? 1;
            var (exp, mito) = UndercityData.Reward(level, Score, Cleared, UndercityData.FeeFor(City));
            if (p != null && p.Mito + mito < 0) mito = -p.Mito;
            int attempts = Mathf.Clamp(Attempt, 1, UndercityData.MainHubAttempts);
            int cls = UndercityData.Class(Cleared, attempts, MissionSeconds, MainHubDown);
            var rows = new List<UndercityHud.ResultRow>
            {
                new UndercityHud.ResultRow { Name = p?.Name ?? "Driver", Level = level, Self = true, Score = Score, Exp = exp, Mito = mito },
            };
            // the badges (offline: my drink and my car's set) and 11114 for the 120 % (0xbcc +2 == 1)
            var badges = UndercityData.Badges(p);
            rows[0].Boost120 = badges.boost120; rows[0].Set = badges.set;
            if (badges.boost120) Net.ChatBox.System(MissionText.Get("11114"));
            foreach (var pt in partners)
            {
                var r = UndercityData.Reward(pt.Level, pt.Score, Cleared, UndercityData.FeeFor(City));
                rows.Add(new UndercityHud.ResultRow { Name = pt.Name, Level = pt.Level, Score = pt.Score, Exp = r.exp, Mito = System.Math.Max(0, r.mito) });
            }
            bool got = false;
            if (p != null)
            {
                var data = new Dictionary<string, object>
                {
                    ["city"] = City, ["cleared"] = Cleared, ["seconds"] = (double)MissionSeconds, ["attempts"] = attempts,
                    ["hubs"] = Score.Hubs, ["jumps"] = Score.Jumps, ["barriers"] = Score.Barriers, ["mainHub"] = Score.MainHub,
                    ["huvs"] = Score.Huvs, ["boss"] = Score.Boss, ["partners"] = partners.Count,
                };
                var score = Score; bool cleared = Cleared;
                Progress.Reward("undercity", data, () =>
                {
                    var (e, m) = UndercityData.Reward(p.Level, score, cleared, UndercityData.FeeFor(City));
                    if (p.Mito + m < 0) m = -p.Mito;
                    int before = p.Level;
                    p.AddExp(e); p.Mito += m; p.HuvCaught += score.Huvs + score.Boss; p.BossesCaught += score.Boss;
                    var res = new Progress.Result { Ok = true, Exp = e, Mito = m, LevelUps = p.Level - before };
                    if (cleared)
                    {
                        res.Drop = UnityEngine.Random.value < 0.35f ? "i_n_00003" : Crafting.ChaseDrops[UnityEngine.Random.Range(0, Crafting.ChaseDrops.Length)];
                        p.Give(res.Drop);
                    }
                    return res;
                }, r =>
                {
                    got = true;
                    if (!r.Ok) { RewardError = r.Error; return; }
                    rows[0].Exp = r.Exp; rows[0].Mito = r.Mito; rows[0].Item = r.Drop;
                });
            }
            float wait = 5f;
            while (!got && wait > 0 && p != null) { wait -= Time.unscaledDeltaTime; yield return null; }
            Hud.CloseAlert();
            ResultView = UndercityHud.ShowResult(Cleared, cls, MissionSeconds, attempts, rows);
            float t = 7f * Mathf.Max(0.1f, TimeScale);
            while (t > 0) { t -= Time.deltaTime; yield return null; }
            Leave();
        }

        /// <summary>0x716bc0: back to the city and the spot the party left from.</summary>
        void Leave()
        {
            leaving = true;
            SetState(State.Left);
            if (ResultView && UiManager.Instance) UiManager.Instance.Close(ResultView);
            ResultView = null;
            if (car)
            {
                car.ExternalControl = false;
                RaceMission.Teleport(car, returnPos, returnRot);
                car.SetSpawn(returnPos, returnRot);
                if (Net.OnlineSession.Instance) Net.OnlineSession.Instance.MarkTeleport();
            }
            if (GameFlow.Instance) AudioLibrary.PlayMusic(GameFlow.Instance, CityMusic.For(City));
            Finish();
        }

        void Finish()
        {
            if (Online) UndercityParty.Ended(this);
            Cleanup();
            if (Active == this) Active = null;
            Destroy(this);
        }

        void Cleanup()
        {
            RestoreEnvironment();
            ClearHuvs();
            if (root) Destroy(root.gameObject);
            hubs.Clear();
            if (Hud) { Hud.CloseAll(); Destroy(Hud); }
            if (session && savedOfferInterval > 0) { session.MissionOfferInterval = savedOfferInterval; savedOfferInterval = 0; }
            if (UiManager.Instance) UiManager.Instance.Off("ID_UC_PLAYEXIT_EXIT");
            if (chase) chase.enabled = true;
            if (car) car.ExternalControl = false;
        }

        void OnDestroy()
        {
            if (Active == this) Active = null;
            Cleanup();
        }

        // ---------------- minimap ----------------

        /// <summary>
        /// The minimap icons of the run (game.nui 190000): UCTOWER for roadside HUBs, UCTOWER_RED, UCTOWER_BARRIER,
        /// UCTOWER_MAIN and UCTOWER_TRAFFIC for the UFOs. Far roadside HUBs are left out so the rim stays readable.
        /// </summary>
        public List<MissionMarkers.Marker> Markers()
        {
            var list = new List<MissionMarkers.Marker>();
            var me = car ? car.transform.position : Vector3.zero;
            foreach (var h in hubs)
            {
                if (!h || !h.Alive || h.Kind == HubKind.Jump) continue;
                if (h.Kind == HubKind.Tower && (h.transform.position - me).sqrMagnitude > 500f * 500f) continue;
                string icon = h.Kind switch
                {
                    HubKind.RedTower => "ID_MINIMAP_ICON_UCTOWER_RED",
                    HubKind.Barrier => "ID_MINIMAP_ICON_UCTOWER_BARRIER",
                    HubKind.Main => "ID_MINIMAP_ICON_UCTOWER_MAIN",
                    HubKind.Ufo => "ID_MINIMAP_ICON_UCTOWER_TRAFFIC",
                    _ => "ID_MINIMAP_ICON_UCTOWER",
                };
                list.Add(new MissionMarkers.Marker { Icon = icon, Position = h.transform.position, Label = h.Kind.ToString() });
            }
            return list;
        }

        // ---------------- test and tool hooks ----------------

        /// <summary>Takes every HUB of the kinds for the player (as if rammed), e.g. to run a session quickly.</summary>
        public void DebugDestroy(int who, params HubKind[] kinds)
        {
            // online the server decides: every HUB of the kinds is reported as rammed by this driver
            if (Online) { DebugReport(kinds); return; }
            foreach (var h in hubs.Where(h => h && h.Alive && kinds.Contains(h.Kind)).ToList()) Destroyed(h, who);
        }

        /// <summary>Destroys the running wave's HUVs for the player.</summary>
        public void DebugKillHuvs()
        {
            foreach (var h in huvs.Where(h => h.Alive).ToList()) Damage(h, 1e6f, 0);
        }
    }
}
