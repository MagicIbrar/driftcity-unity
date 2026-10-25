using System;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// Rush Time (DriftCity_RE/CHASES.md section 3): a timed city event where every driver in the Rush Time zone chases one
    /// boss HUV together.
    /// <list type="bullet">
    /// <item>Waiting (state 3 of 0x4e2270 "Waiting Rush Time"): RUSHWAIT 2910000 (0x562ec0) while the server's countdown runs:
    /// TIME "%02u:%02u" until the start, the boss's image (HUVIMG01/03/04/07/08), name and description, WAIT_INFO "%2u/%2u"
    /// drivers waiting / the minimum (server flag net+600 &gt;&gt; 13), labels 11135 "Time Left" / 11136 "Participant",
    /// title 11137 "Rush Time Notice" and 11138 "If there are %d or more users in the Rush Time Zone, Rush Time will be
    /// started in 1 mins." (11139 once one runs). The zone is game\cylinder_rushtime.nif and ID_MINIMAP_ICON_RUSHTIME.</item>
    /// <item>Running (state 2 "Rush Time"): RUSHINFO 2900000 (0x5629e0 on message 0x2001): the time as digit art m m " s s .
    /// t t t (0x531eb0 groups), the HUV count, the drivers in the zone (mode +0x54, three digits), the boss's health gauge
    /// (HUVHP_GAUGE scaled by the health fraction); ESC asks 10566 "Abandon Rush Time?" and leaves (packet 0x79ba90, UI
    /// effect 9).</item>
    /// <item>The end (state 4): RUSHRESULT 2920000 for 6 s - TITLE "Rush Time HUV Lucky Driver", TEAM "%s %s" (the driver who
    /// stopped the boss), TEXT 10893 "3 Mittron Enhancers, %d Mito." (the lucky driver's prize).</item>
    /// </list>
    /// The server schedules Rush Time and counts the drivers (src/huvevents.js rush.*): online the city's drivers get the
    /// notice (the zone is chasespawnlist point <c>spot</c>), report the zone, and the drivers in it at the start chase one
    /// boss together: the first driver's client runs it (party HUV, TRAFFIC.md section 14) and the others see its copy, hits
    /// go to the server (shared health, AI stand-ins ram too), which names the lucky driver and pays everyone. Offline one is
    /// announced every <see cref="Interval"/> s at a chase spawn point, AI drivers join the zone, the boss gets 400 + 80 per
    /// driver health, the AI drivers ram it every 2.5-4.5 s for 5-12, and every driver who stayed is paid; the final blow
    /// wins three Mittron Enhancers (i_n_00003) and 5000 Mito. Server mirror: economy.js "rush".
    /// </summary>
    public sealed class RushTime : MonoBehaviour
    {
        public enum Phase { Idle, Waiting, Running, Result }

        public const int InfoId = 2900000, WaitId = 2910000, ResultId = 2920000;
        public float FirstDelay = 240f, Interval = 900f, WaitSeconds = 120f, RunSeconds = 300f, ResultSeconds = 6f;
        public float ZoneRadius = 150f;
        public int MinDrivers = 3;

        public static RushTime Instance { get; private set; }
        /// <summary>The player is in a running Rush Time.</summary>
        public static bool Joined => Instance && Instance.phase == Phase.Running && Instance.joined;
        /// <summary>
        /// Waiting in the zone or in a running Rush Time: the client refuses an Undercity invite (11146) or a V-HUV request
        /// (11145) on its own (0x50e1d0 answers 0xfe).
        /// </summary>
        public static bool Participating => Instance && (Joined || (Instance.phase == Phase.Waiting && Instance.PlayerInZone));
        /// <summary>Run by the server (rush.* pushes) rather than the offline schedule.</summary>
        public bool Online => OnlineSession.Active;

        public Phase State => phase;
        public Vector3 Zone => zone;
        public int Drivers => drivers;
        public float Health => health;
        public float MaxHealth => maxHealth;
        public float TimeLeft => timer;
        public HuvTarget Boss => boss;
        /// <summary>The last lucky driver (tests).</summary>
        public string LuckyDriver { get; private set; }

        public CitySession Session;
        public string City;
        Phase phase;
        float timer, nextAi;
        Vector3 zone;
        int drivers, playerHits;
        float health, maxHealth, playerDamage, elapsed;
        bool joined;
        HuvTarget boss;
        // online (src/huvevents.js): the event's id, the drivers the server counts, the boss's owner, batched hits
        int netId, netDrivers;
        bool netOwner, reportedIn;
        float netDamage, netHitTimer, netShareTimer;
        int netHits;
        MissionBeacon zoneMarker;
        NuiView info, wait, result;
        readonly List<float> aiClocks = new List<float>();

        public static RushTime Attach(CitySession session, string city)
        {
            if (!session) return null;
            if (!session.TryGetComponent(out RushTime r)) r = session.gameObject.AddComponent<RushTime>();
            r.Session = session; r.City = city;
            r.timer = r.FirstDelay;
            Instance = r;
            return r;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            Cleanup();
        }

        DriftCar Car => GameFlow.Instance ? GameFlow.Instance.PlayerCar : Session ? Session.Car : null;
        public bool PlayerInZone => Car && MissionPosts.Flat(Car.transform.position, zone) < ZoneRadius;
        static bool OtherMission => ChaseMission.Active || DeliveryService.Active || PatrolMission.Active || RaceMission.Active || OnlineRace.Active || UndercityStage.Active;

        void Update()
        {
            float dt = Time.deltaTime;
            timer -= dt;
            if (Online && netId > 0) { NetUpdate(dt); return; }
            switch (phase)
            {
                case Phase.Idle:
                    // online the server announces (rush.notice)
                    if (timer <= 0 && !Online) Announce();
                    else if (timer <= 0) timer = 60f;
                    break;
                case Phase.Waiting:
                    // the AI drivers drift into the zone during the countdown
                    if (drivers < MinDrivers + 5 && UnityEngine.Random.value < dt / 12f) drivers++;
                    UpdateWait();
                    if (timer <= 0) StartRush();
                    break;
                case Phase.Running:
                    elapsed += dt;
                    if (boss == null || !boss.Car) { Fail(); break; }
                    boss.Tick();
                    for (int i = 0; i < aiClocks.Count; i++)
                    {
                        aiClocks[i] -= dt;
                        if (aiClocks[i] > 0) continue;
                        aiClocks[i] = UnityEngine.Random.Range(2.5f, 4.5f);
                        Damage(UnityEngine.Random.Range(5f, 12f), SocialAi(i));
                        if (phase != Phase.Running) return;
                    }
                    Info();
                    if (timer <= 0) Fail();
                    break;
                case Phase.Result:
                    if (timer <= 0) { Cleanup(); phase = Phase.Idle; timer = Interval; }
                    break;
            }
        }

        static string SocialAi(int i) => Social.SocialStore.AiDrivers[i % Social.SocialStore.AiDrivers.Length];

        /// <summary>Starts the countdown now (tests, or a forced event).</summary>
        public void Announce()
        {
            Cleanup();
            var car = Car;
            var pt = car ? ChaseSpawns.Pick(City, car.transform.position, 300f, 1500f) : null;
            if (pt != null) zone = pt.Pivot;
            else if (car && Session && Session.Traffic && Session.Traffic.TryPickLane(300f, 1500f, out int lane, out float along) && Session.Traffic.Sample(lane, along, out var pos, out _)) zone = pos;
            else { timer = 60f; return; }
            phase = Phase.Waiting;
            timer = WaitSeconds;
            drivers = UnityEngine.Random.Range(0, 2);
            zoneMarker = MissionBeacon.Create("Rush Time Zone", @"game\cylinder_rushtime.nif", new Color(1f, 0.4f, 0.1f, 0.12f));
            zoneMarker.MoveTo(zone);
            ChatBox.System(MissionText.Get("11137"));
            ChatBox.System(MissionText.Get("11138", MinDrivers));
        }

        /// <summary>Ends the countdown now (tests; a GM start).</summary>
        public void StartNow() { if (phase == Phase.Waiting) timer = 0f; }

        void UpdateWait()
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            bool show = PlayerInZone;
            if (!show) { if (wait) ui.Close(wait); wait = null; return; }
            if (!wait)
            {
                wait = ui.Open(WaitId);
                if (!wait) return;
                wait.SetText("ID_RUSHWAIT_FREETIME", MissionText.Get("11135"));
                wait.SetText("ID_RUSHWAIT_WAITCOUNT", MissionText.Get("11136"));
                wait.SetVisible("ID_RUSHWAIT_QUESTION", false);
                var bossInfo = BossInfo();
                int img = BossImage(bossInfo);
                foreach (int n in new[] { 1, 3, 4, 7, 8 }) wait.SetVisible($"ID_RUSHWAIT_HUVIMG{n:00}", n == img);
                wait.SetText("ID_RUSHWAIT_HUVNAME", bossInfo?.UiName ?? "");
            }
            // 0x562ec0: while the zone lacks drivers WAIT_INFO "%2u/%2u" under "Participant" and 11138; once enough wait, the
            // countdown TIME "%02u:%02u" under "Time Left" and the boss's name
            int waiting = Online && netId > 0 ? Mathf.Max(netDrivers, 1) : drivers + 1;
            bool enough = waiting >= MinDrivers;
            int t = Mathf.Max(0, Mathf.CeilToInt(timer));
            wait.SetText("ID_RUSHWAIT_TIME", MissionText.Printf("%02u:%02u", t / 60, t % 60));
            wait.SetText("ID_RUSHWAIT_WAIT_INFO", MissionText.Printf("%2u/%2u", waiting, MinDrivers));
            wait.SetVisible("ID_RUSHWAIT_TIME", enough);
            wait.SetVisible("ID_RUSHWAIT_FREETIME", enough);
            wait.SetVisible("ID_RUSHWAIT_WAIT_INFO", !enough);
            wait.SetVisible("ID_RUSHWAIT_WAITCOUNT", !enough);
            wait.SetText("ID_RUSHWAIT_HUVDESC", enough ? (BossInfo() != null ? $"{BossInfo().Maker} {BossInfo().CarName}".Trim() : "") : MissionText.Get("11138", MinDrivers));
        }

        VehicleInfo bossPick;
        VehicleInfo BossInfo()
        {
            if (bossPick == null) bossPick = HuvTarget.Pick(true, PlayerProfile.Current?.Level ?? 1);
            return bossPick;
        }

        /// <summary>The RUSHWAIT image of a boss: HUV_BOSS01/03/04/07/08 by its position in the boss list.</summary>
        static int BossImage(VehicleInfo b)
        {
            int[] imgs = { 1, 3, 4, 7, 8 };
            int i = b != null ? Mathf.Max(0, GameTables.BossHuvs.IndexOf(b)) : 0;
            return imgs[i % imgs.Length];
        }

        void StartRush()
        {
            var ui = UiManager.Instance;
            if (wait && ui) ui.Close(wait);
            wait = null;
            joined = PlayerInZone && !OtherMission;
            if (!joined || drivers + 1 < MinDrivers) { Cleanup(); phase = Phase.Idle; timer = Interval; return; }
            boss = HuvTarget.Spawn(Session ? Session.Traffic : null, Car, BossInfo(), true, 30f, 260f, true, zone);
            if (boss == null) { Cleanup(); phase = Phase.Idle; timer = 60f; return; }
            boss.PlayerHit += d => { playerHits++; Damage(d, PlayerProfile.Current?.Name ?? "Driver", true); };
            maxHealth = health = 400f + 80f * (drivers + 1);
            playerHits = 0; playerDamage = 0; elapsed = 0;
            aiClocks.Clear();
            for (int i = 0; i < drivers; i++) aiClocks.Add(UnityEngine.Random.Range(2.5f, 4.5f));
            phase = Phase.Running;
            timer = RunSeconds;
            var rec = MissionRecords.Current; rec.RushesJoined++; rec.Save();
            AudioLibrary.PlayMusic(this, "bgm/DriftCity_chasing.mp3");
            AudioLibrary.PlaySfx("sfx/SID_BOSSHUV_01");
            OpenInfo();
            Info();
        }

        void OpenInfo()
        {
            var ui = UiManager.Instance;
            info = ui ? ui.Open(InfoId) : null;
            if (!info) return;
            // the digit art (2900004-13 time, 2900022-31 counts) is only copied onto the slots
            foreach (var c in info.GetComponentsInChildren<NuiControlView>(true))
            {
                int n = c.Data.Id;
                if (n >= 2900004 && n <= 2900013 || n >= 2900022 && n <= 2900031) c.gameObject.SetActive(false);
            }
            ui.On("ID_RUSHINFO_ESC", _ => MissionUi.Confirm(MissionText.Get("10566"), Leave));
        }

        /// <summary>Damage to the boss; the hit that stops it decides the lucky driver.</summary>
        public void Damage(float amount, string by, bool player = false)
        {
            if (phase != Phase.Running || amount <= 0) return;
            if (Online && netId > 0) { if (player) { netDamage += amount; netHits++; } return; }   // the server keeps the health
            health -= amount;
            if (player) { playerDamage += amount; AudioLibrary.PlaySfx("sfx/SID_BOSSHUV_0" + UnityEngine.Random.Range(2, 6)); }
            if (health <= 0) Win(by, player);
        }

        /// <summary>The digit art of RUSHINFO: unnamed NUM art 2900004-13 (TIMECOUNT_0 first), HUVCOUNT_0.. for the counts.</summary>
        void Info()
        {
            if (!info) return;
            int ms = Mathf.Max(0, Mathf.FloorToInt(timer * 1000f));
            int m = ms / 60000 % 100, s = ms / 1000 % 60, f = ms % 1000;
            int[] time = { m / 10, m % 10, s / 10, s % 10, f / 100, f / 10 % 10, f % 10 };
            string[] slots = { "HOURPOS_10", "HOURPOS_1", "MINUTEPOS_10", "MINUTEPOS_1", "SECONDPOS_10", "SECONDPOS_1", "SECONDPOS_0" };
            for (int k = 0; k < 7; k++) Art(info, "ID_RUSHINFO_" + slots[k], TimeArt(time[k]));
            Art(info, "ID_RUSHINFO_HUVCOUNT_POS", CountArt(health > 0 ? 1 : 0));
            int n = Mathf.Clamp(Online && netId > 0 ? netDrivers : drivers + 1, 0, 999);
            Art(info, "ID_RUSHINFO_DRIVERCOUNTPOS_100", CountArt(n / 100), n >= 100);
            Art(info, "ID_RUSHINFO_DRIVERCOUNTPOS_10", CountArt(n / 10 % 10), n >= 10);
            Art(info, "ID_RUSHINFO_DRIVERCOUNTPOS_1", CountArt(n % 10));
            var gauge = info.Control("ID_RUSHINFO_HUVHP_GAUGE");
            if (gauge)
            {
                var rt = (RectTransform)gauge.transform;
                if (gaugeWidth <= 0) gaugeWidth = rt.sizeDelta.x;
                rt.sizeDelta = new Vector2(gaugeWidth * Mathf.Clamp01(maxHealth > 0 ? health / maxHealth : 0), rt.sizeDelta.y);
                foreach (var img in gauge.GetComponentsInChildren<UnityEngine.UI.RawImage>(true))
                {
                    if (gaugeUv.width <= 0) gaugeUv = img.uvRect;
                    float k = Mathf.Clamp01(maxHealth > 0 ? health / maxHealth : 0);
                    img.uvRect = new Rect(gaugeUv.x, gaugeUv.y, gaugeUv.width * k, gaugeUv.height);
                    img.rectTransform.sizeDelta = rt.sizeDelta;
                }
            }
        }
        float gaugeWidth;
        Rect gaugeUv;

        // digit art: TIMECOUNT_0 (2900004) and the unnamed 2900005-13 are 0..9; HUVCOUNT_0 (2900022) and 2900023-31
        UnityEngine.UI.RawImage TimeArt(int d) => ArtByNumber(2900004 + d);
        UnityEngine.UI.RawImage CountArt(int d) => ArtByNumber(2900022 + d);

        UnityEngine.UI.RawImage ArtByNumber(int controlNumber)
        {
            if (!info) return null;
            foreach (var c in info.GetComponentsInChildren<NuiControlView>(true))
                if (c.Data.Id == controlNumber) return c.GetComponentInChildren<UnityEngine.UI.RawImage>(true);
            return null;
        }

        static void Art(NuiView v, string slot, UnityEngine.UI.RawImage src, bool show = true)
        {
            var c = v.Control(slot);
            if (!c) return;
            c.gameObject.SetActive(show);
            var dst = c.GetComponentInChildren<UnityEngine.UI.RawImage>(true);
            if (!show || !dst || !src) return;
            dst.texture = src.texture; dst.uvRect = src.uvRect; dst.enabled = src.texture;
        }

        /// <summary>RUSHINFO ESC confirmed (10566): out of this Rush Time (UI effect 9), no reward.</summary>
        public void Leave()
        {
            if (phase != Phase.Running) return;
            if (Online && netId > 0) { NetSend("rush.leave"); netId = 0; }
            GameEffects.Screen(9);
            Cleanup();
            phase = Phase.Idle;
            timer = Interval;
        }

        void Win(string by, bool player)
        {
            LuckyDriver = by;
            var huv = boss != null && boss.Car ? boss.Car.transform : null;
            GameEffects.ChaseEnded(true, huv);
            boss?.Remove(true);
            boss = null;
            CloseInfo();
            float seconds = elapsed;
            int lvl = PlayerProfile.Current?.Level ?? 1;
            long prize = 5000;
            ShowResult(by, prize);
            phase = Phase.Result;
            timer = ResultSeconds;
            var p = PlayerProfile.Current;
            if (p == null || !joined) return;
            int hits = Mathf.Min(playerHits, 40);
            if (player) { var rec = MissionRecords.Current; rec.RushFinalBlows++; rec.Save(); }
            var data = new Dictionary<string, object> { ["hits"] = hits, ["finalBlow"] = player, ["drivers"] = drivers + 1, ["seconds"] = (double)seconds };
            Progress.Reward("rush", data, () =>
            {
                long exp = MitaDrink.Apply(Reward.Exp(lvl, hits)), mito = Reward.Mito(lvl, player);
                int before = p.Level;
                p.AddExp(exp); p.Mito += mito; p.HuvCaught++; p.BossesCaught++;
                if (player) p.Give("i_n_00003", 3);
                return new Progress.Result { Ok = true, Exp = exp, Mito = mito, LevelUps = p.Level - before, Drop = player ? "i_n_00003" : null };
            }, r => { if (r.Ok) ChaseScreens.RewardPopup(r.Exp, r.Mito); });
        }

        /// <summary>The rebuild's Rush Time pay (economy.js "rush"): per driver who stayed, plus the lucky driver's prize.</summary>
        public static class Reward
        {
            public static long Exp(int level, int hits) => 200 + 40L * level + 10L * Mathf.Clamp(hits, 0, 40);
            public static long Mito(int level, bool finalBlow) => 2000 + 300L * level + (finalBlow ? 5000 : 0);
        }

        void ShowResult(string lucky, long prize)
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            result = ui.Open(ResultId);
            if (!result) return;
            result.SetText("ID_RUSHRESULT_TEAM", MissionText.Printf("%s %s", lucky, ""));
            result.SetText("ID_RUSHRESULT_TEXT", MissionText.Get("10893", prize));
        }

        void Fail()
        {
            var huv = boss != null && boss.Car ? boss.Car.transform : null;
            if (joined) GameEffects.ChaseEnded(false, huv);
            Cleanup();
            phase = Phase.Idle;
            timer = Interval;
        }

        void CloseInfo()
        {
            var ui = UiManager.Instance;
            if (info && ui) ui.Close(info);
            info = null;
            if (GameFlow.Instance) AudioLibrary.PlayMusic(GameFlow.Instance, CityMusic.For(GameFlow.Instance.City));
        }

        void Cleanup()
        {
            var ui = UiManager.Instance;
            if (ui)
            {
                if (info) { ui.Close(info); if (GameFlow.Instance) AudioLibrary.PlayMusic(GameFlow.Instance, CityMusic.For(GameFlow.Instance.City)); }
                if (wait) ui.Close(wait);
                if (result) ui.Close(result);
            }
            info = wait = result = null;
            boss?.Remove(false);
            boss = null;
            bossPick = null;
            if (zoneMarker) Destroy(zoneMarker.gameObject);
            zoneMarker = null;
            joined = false;
        }

        // ---------------- online (src/huvevents.js) ----------------

        /// <summary>rush.* pushes from the server (MissionNet routes them here).</summary>
        public static void OnPush(string t, Dictionary<string, object> m)
        {
            var r = Instance;
            if (!r) return;
            try { r.Handle(t, m); } catch (Exception ex) { Debug.LogException(ex); }
        }

        void Handle(string t, Dictionary<string, object> m)
        {
            if (t != "rush.hp" && t != "rush.huv") Debug.Log($"Drift City Rush Time: {t} id {m.I("id")} (mine {netId}, {phase})");
            switch (t)
            {
                case "rush.notice":
                {
                    Cleanup();
                    var pts = ChaseSpawns.For(City);
                    if (pts.Count == 0) return;
                    netId = m.I("id");
                    zone = pts[Mathf.Abs(m.I("spot")) % pts.Count].Pivot;
                    MinDrivers = Mathf.Max(1, m.I("min", MinDrivers));
                    bossPick = HuvTarget.Pick(true, PlayerProfile.Current?.Level ?? 1, Mathf.Max(0, m.I("boss")));
                    phase = Phase.Waiting;
                    timer = (float)m.N("seconds", WaitSeconds);
                    netDrivers = 0;
                    reportedIn = false;
                    zoneMarker = MissionBeacon.Create("Rush Time Zone", @"game\cylinder_rushtime.nif", new Color(1f, 0.4f, 0.1f, 0.12f));
                    zoneMarker.MoveTo(zone);
                    ChatBox.System(MissionText.Get("11137"));
                    ChatBox.System(MissionText.Get("11138", MinDrivers));
                    break;
                }
                case "rush.wait":
                    if (m.I("id") != netId) return;
                    netDrivers = m.I("drivers");
                    timer = (float)m.N("seconds", timer);
                    break;
                case "rush.start":
                    if (m.I("id") != netId) return;
                    NetStart(m);
                    break;
                case "rush.closed":
                    // started without me (not in the zone): the zone goes
                    if (m.I("id") != netId) return;
                    Cleanup(); phase = Phase.Idle; netId = 0;
                    break;
                case "rush.hp":
                    if (m.I("id") != netId || phase != Phase.Running) return;
                    health = (float)m.N("hp");
                    maxHealth = Mathf.Max(maxHealth, (float)m.N("max", maxHealth));
                    netDrivers = m.I("drivers", netDrivers);
                    if (m.S("by") != PlayerProfile.Current?.Name) AudioLibrary.PlaySfx("sfx/SID_BOSSHUV_0" + UnityEngine.Random.Range(2, 6));
                    break;
                case "rush.huv":
                    if (m.I("id") != netId || boss == null || !boss.Car || netOwner) return;
                    if (m.L("cars") is List<object> cars && cars.Count > 0 && cars[0] is List<object> s && s.Count >= 5)
                    {
                        bool first = !boss.Car.HasRemoteState;
                        boss.Car.ApplyRemote(new Vector3(F(s[0]), F(s[1]), F(s[2])), F(s[3]), F(s[4]));
                        if (first) ShowCar(boss.Car, true);
                    }
                    break;
                case "rush.owner":
                    if (m.I("id") != netId || boss == null || !boss.Car) return;
                    netOwner = m.S("owner") == PlayerProfile.Current?.Name;
                    if (netOwner) { ShowCar(boss.Car, true); boss.Car.SetRemote(false); }
                    break;
                case "rush.end":
                    if (m.I("id") != netId) return;
                    NetEnd(m);
                    break;
            }
        }

        static float F(object o) => o is double d ? (float)d : o is long l ? l : o is int i ? i : 0f;

        static void ShowCar(TrafficCar c, bool on)
        {
            if (!c) return;
            foreach (var r in c.GetComponentsInChildren<Renderer>(true)) r.enabled = on;
            foreach (var col in c.GetComponentsInChildren<Collider>(true)) col.enabled = on;
        }

        static void NetSend(string t) { if (OnlineSession.Active) OnlineSession.Instance.Net.Request(t, new Dictionary<string, object>(), null); }

        /// <summary>Online frame: the zone report, batched hits (4 a second), the owner's boss states (5 a second).</summary>
        void NetUpdate(float dt)
        {
            var net = OnlineSession.Instance ? OnlineSession.Instance.Net : null;
            if (!net) return;
            switch (phase)
            {
                case Phase.Waiting:
                {
                    bool inZone = PlayerInZone && !OtherMission;
                    if (inZone != reportedIn)
                    {
                        reportedIn = inZone;
                        net.Request("rush.zone", new Dictionary<string, object> { ["id"] = netId, ["in"] = inZone }, r =>
                        {
                            if (r != null && !r.B("ok") && inZone && r.S("error") != null) ChatBox.System(r.S("error"));
                            else if (r != null) netDrivers = r.I("drivers", netDrivers);
                        });
                    }
                    UpdateWait();
                    break;
                }
                case Phase.Running:
                    elapsed += dt;
                    if (boss == null || !boss.Car) break;
                    if (!boss.Car.Remote) boss.Tick();
                    else if (boss.Beacon) boss.Beacon.transform.position = boss.Car.transform.position + Vector3.up * 40f;
                    netHitTimer -= dt;
                    if (netDamage > 0 && netHitTimer <= 0)
                    {
                        netHitTimer = 0.25f;
                        float d = netDamage; int h = netHits; netDamage = 0; netHits = 0;
                        net.Request("rush.hit", new Dictionary<string, object> { ["damage"] = (double)d, ["hits"] = h }, null);
                    }
                    netShareTimer -= dt;
                    if (netOwner && netShareTimer <= 0)
                    {
                        netShareTimer = ChaseMission.ShareInterval;
                        net.Send("rush.huv", new Dictionary<string, object> { ["cars"] = new List<object> { boss.Car.NetState().Cast<object>().ToList() } });
                    }
                    Info();
                    break;
                case Phase.Result:
                    if (timer <= 0) { Cleanup(); phase = Phase.Idle; netId = 0; }
                    break;
            }
        }

        /// <summary>rush.start: I am one of the drivers; the boss at the zone, the first driver runs it.</summary>
        void NetStart(Dictionary<string, object> m)
        {
            var ui = UiManager.Instance;
            if (wait && ui) ui.Close(wait);
            wait = null;
            joined = true;
            netOwner = m.S("owner") == PlayerProfile.Current?.Name;
            netDrivers = m.I("drivers");
            boss = HuvTarget.Spawn(Session ? Session.Traffic : null, Car, BossInfo(), true, 30f, 260f, true, zone);
            if (boss == null) { Debug.LogWarning("Drift City Rush Time: no boss could be spawned at the zone"); NetSend("rush.leave"); Cleanup(); phase = Phase.Idle; netId = 0; return; }
            if (!netOwner) { boss.Car.SetRemote(true); ShowCar(boss.Car, false); }
            // my hits go to the server in batches; the hit that stops the boss is the server's call
            boss.PlayerHit += d => { playerHits++; playerDamage += d; netDamage += d; netHits++; AudioLibrary.PlaySfx("sfx/SID_BOSSHUV_0" + UnityEngine.Random.Range(2, 6)); };
            maxHealth = (float)m.N("max", 400f); health = (float)m.N("hp", maxHealth);
            playerHits = 0; playerDamage = 0; elapsed = 0;
            aiClocks.Clear();
            phase = Phase.Running;
            timer = (float)m.N("seconds", RunSeconds);
            var rec = MissionRecords.Current; rec.RushesJoined++; rec.Save();
            AudioLibrary.PlayMusic(this, "bgm/DriftCity_chasing.mp3");
            AudioLibrary.PlaySfx("sfx/SID_BOSSHUV_01");
            OpenInfo();
            Info();
        }

        /// <summary>rush.end: the lucky driver, my pay (RUSHRESULT 2920000), or the event not held / failed.</summary>
        void NetEnd(Dictionary<string, object> m)
        {
            if (m.B("cancelled")) { Cleanup(); phase = Phase.Idle; netId = 0; return; }
            var huv = boss != null && boss.Car ? boss.Car.transform : null;
            if (m.O("driver") != null) OnlineSession.ApplyDriver(m.O("driver"));
            if (!m.B("success"))
            {
                if (joined) GameEffects.ChaseEnded(false, huv);
                Cleanup(); phase = Phase.Idle; netId = 0;
                return;
            }
            LuckyDriver = m.S("lucky");
            GameEffects.ChaseEnded(true, huv);
            boss?.Remove(true);
            boss = null;
            CloseInfo();
            ShowResult(LuckyDriver, (long)m.N("prize", 5000));
            phase = Phase.Result;
            timer = ResultSeconds;
            string me = PlayerProfile.Current?.Name;
            if (LuckyDriver == me) { var rec = MissionRecords.Current; rec.RushFinalBlows++; rec.Save(); }
            foreach (var o in m.L("rows") ?? new List<object>())
                if (o is Dictionary<string, object> row && row.S("name") == me) ChaseScreens.RewardPopup((long)row.N("exp"), (long)row.N("mito"));
        }

        /// <summary>The zone on the minimap (ID_MINIMAP_ICON_RUSHTIME) while a Rush Time waits or runs.</summary>
        public IEnumerable<MissionMarkers.Marker> Markers()
        {
            if (phase == Phase.Waiting || phase == Phase.Running)
                yield return new MissionMarkers.Marker { Icon = "ID_MINIMAP_ICON_RUSHTIME", Position = zone, Label = MissionText.Get("11137") };
        }
    }
}
