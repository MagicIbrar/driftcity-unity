using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Net;
using DriftCity.Social;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// The online party run (DriftCity_RE/PARTYPLAY.md). Same stage, screens and scripted steps as the offline run, but
    /// the server's Undercity room (DriftCityServer/src/undercity.js) is the authority, as the exe's server was:
    /// <list type="bullet">
    /// <item>phases and clocks come from "party.uc.phase" (the exe's 0xfb state timings, 0xbbe HUB phase over, 0xbc3 Main HUB
    /// attempt with its clock, 0xbc8 Main HUB down, 0xbc9 stage over); the run leader's Accept (UC_HUB_NeedAccept) is sent to it;</item>
    /// <item>a HUB, red HUB, barrier, UFO or the Main HUB the car touches is reported (0xbbc / 0xbc6) and falls when the server
    /// says so, for whoever took it ("party.uc.event", 0xbbd / 0xbc7 / 0xbc8), with the PARTYUC counters;</item>
    /// <item>each client spawns the wave's HUVs (uchuvparam rows of the city) and reports them; their health is shared and
    /// hits go through the server (0xc0 HuvHit);</item>
    /// <item>the other members' cars are the online session's remote players: the server moved the party into a private
    /// channel of the city, so they drive in this stage and nobody else does; AI partners fill the empty slots on the server;</item>
    /// <item>UCRESULT shows the server's rows; every member was paid by the server (0xbcc).</item>
    /// </list>
    /// </summary>
    public sealed partial class UndercityStage
    {
        /// <summary>True for a party run through the server.</summary>
        public bool Online { get; private set; }
        public int Room { get; private set; }
        /// <summary>My slot in the run (0 = P1, the run leader); also my start post.</summary>
        public int MySlot { get; private set; }
        public string ServerPhase { get; private set; } = "";
        float pendingTimeCut;
        public int ServerAttempt { get; private set; }
        public int ServerWave { get; private set; }
        public Dictionary<string, object> OnlineResult => result;

        public sealed class Slot { public string Name; public int Level; public bool Ai, Gone; }
        /// <summary>The run's party in the server's order (P1..P4), AI partners last.</summary>
        public readonly List<Slot> Slots = new List<Slot>();

        List<int> slotCounts;
        float[] waveHealth;
        int reportedWave;
        Dictionary<string, object> result;
        readonly Dictionary<Hub, float> pendingHubs = new Dictionary<Hub, float>();
        float lastHitSent = -10f;

        /// <summary>The first member still in the run reports the leader's things (the exe's +0x3c).</summary>
        public bool RunLeader
        {
            get
            {
                for (int i = 0; i < Slots.Count; i++) if (!Slots[i].Ai && !Slots[i].Gone) return i == MySlot;
                return false;
            }
        }

        /// <summary>Server 0xff ("party.uc.start"): the party's run in this city.</summary>
        public static UndercityStage BeginOnline(CitySession session, Dictionary<string, object> start, float timeScale = 1f)
        {
            if (Active || !session || !GameFlow.Instance || start == null) return null;
            if (UndercityData.For(session.City) == null) return null;
            var s = session.gameObject.AddComponent<UndercityStage>();
            s.Online = true;
            s.Room = start.I("room");
            s.MySlot = Mathf.Clamp(start.I("slot"), 0, 3);
            foreach (var o in (start.L("members") ?? new List<object>()).OfType<Dictionary<string, object>>())
                s.Slots.Add(new Slot { Name = o.S("name"), Level = Mathf.Max(1, o.I("level", 1)), Ai = o.B("ai") });
            s.Partners = Mathf.Max(0, s.Slots.Count - 1);
            s.StartIndex = s.MySlot;
            s.TimeScale = timeScale * GlobalTimeScale;
            s.ServerPhase = "ready";
            s.StartCoroutine(s.EnterOnline(GameFlow.Instance, session));
            return s;
        }

        /// <summary>partners[who - 1] of a server slot (0 = me).</summary>
        int WhoOf(int slot) => slot == MySlot ? 0 : slot < MySlot ? slot + 1 : slot;

        List<int> SlotCounts()
        {
            if (slotCounts != null && slotCounts.Count == Slots.Count) return slotCounts;
            var list = new List<int>();
            for (int i = 0; i < Slots.Count; i++)
            {
                int who = WhoOf(i);
                list.Add(who == 0 ? Score.PartyUc : who - 1 < partners.Count ? partners[who - 1].Score.PartyUc : 0);
            }
            return list;
        }

        bool InWave => ServerPhase == "wave" || ServerPhase == "boss";

        IEnumerator EnterOnline(GameFlow owner, CitySession citySession)
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
            partners.Clear();
            for (int i = 0; i < Slots.Count; i++) if (i != MySlot) partners.Add(new Partner { Name = Slots[i].Name, Level = Slots[i].Level });
            root = new GameObject("Undercity").transform;
            if (session && session.Traffic && session.Traffic.transform.parent) root.SetParent(session.Traffic.transform.parent, false);

            // GAMELOADING with the Undercity picture, then the underground city
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
            yield return null;
            BuildHubPhase();
            TeleportToStart();
            car.ExternalControl = true;
            yield return Wait(0.5f);
            if (ui) { ui.Close(loading); ui.Close(bar); }
            AudioLibrary.PlayMusic(this, CityMusic.For(City));
            startTime = Time.time;
            if (UiManager.Instance) UiManager.Instance.On("ID_UC_PLAYEXIT_EXIT", _ => AskQuit());
            Social.PartyWindow.Refresh();   // LEAVE / MEMBERBAN disabled in the Undercity (0x61bff5)

            // ---- state 2 / 3: UC_Ready (19.2 s) and the start animation; the server's clock runs the same steps ----
            SetState(State.Ready);
            Alert("UC_Ready");
            float t = 19.2f * Mathf.Max(0f, TimeScale);
            while (t > 0 && ServerPhase == "ready" && !leaving) { t -= Time.deltaTime; yield return null; }
            SetState(State.StartAnimation);
            yield return StartAnimation(5.6f);
            // ---- state 4: the run leader sees UC_HUB_NeedAccept with Accept, the others UC_HUB ----
            SetState(State.HubAccept);
            if (ServerPhase == "ready" || ServerPhase == "accept")
            {
                if (RunLeader) Alert("UC_HUB_NeedAccept", 60f, SendAccept);
                else Alert("UC_HUB", 60f);
            }
            while ((ServerPhase == "ready" || ServerPhase == "accept") && !leaving) yield return null;
            Hud.CloseAlert();
            // ---- state 5: the countdown ----
            if (ServerPhase == "countdown")
            {
                SetState(State.Countdown);
                AudioLibrary.PlaySfx("sfx/SID_PVP_RACE_START");
                GameEffects.Screen(0x12, 7.5f);
                while (ServerPhase == "countdown" && !leaving) yield return null;
            }
            car.ExternalControl = false;

            // ---- state 6: the HUBs, until the server says they are all down or the clock ran out ----
            if (ServerPhase == "hubs")
            {
                SetState(State.Hubs);
                Hud.ShowPhase(0);
                Hud.ShowTip("JUMP");
                if (Clock <= 0) Clock = UndercityData.HubPhaseSeconds;
                while (ServerPhase == "hubs" && !leaving)
                {
                    Clock = Mathf.Max(0, Clock - Time.deltaTime);
                    if (!hurry && Clock < 60f) { hurry = true; Alert("UC_Hurry", 3f); }
                    if (!hurry2 && Clock < 20f) { hurry2 = true; Alert("UC_Hurry2", 3f); }
                    UpdateHud();
                    yield return null;
                }
            }
            if (ServerPhase == "hubsCleared")
            {
                Alert("UC_HUBSuccess1");
                SetState(State.HubsCleared);
                while (ServerPhase == "hubsCleared" && !leaving) yield return null;
            }
            ClearHubs(HubKind.Tower, HubKind.RedTower, HubKind.Jump);

            // ---- states 8-10: the Main HUB attempts (0xbc3 brings the attempt and its clock) ----
            while (ServerPhase == "main" && !leaving)
            {
                Attempt = Mathf.Max(1, ServerAttempt);
                SetState(State.MainIntro);
                car.ExternalControl = true;
                ClearHubs(HubKind.Barrier, HubKind.Main, HubKind.Ufo);
                BuildMainHub();
                TeleportToStart();
                yield return StartAnimation(5.6f);
                SetState(State.MainBriefing);
                Hud.ShowPhase(1);
                Alert(Attempt == 1 ? "UC_MainHUB" : Attempt == 2 ? "UC_Fail" : "UC_Fail2");
                UpdateHud();
                yield return Wait(5f);
                car.ExternalControl = false;
                Hud.ShowTip("BARRIER");
                SetState(State.MainHub);
                warned60 = warned15 = critical11 = false;
                while (ServerPhase == "main" && ServerAttempt == Attempt && !leaving)
                {
                    Clock = Mathf.Max(0, Clock - Time.deltaTime);
                    TickWarnings("UC_Hurry4", "UC_Hurry5");
                    UpdateHud();
                    yield return null;
                }
            }
            if (ServerPhase == "mainCleared")
            {
                MainHubDown = true;
                Alert("UC_HUBSuccess2");
                GameEffects.Screen("critical");
                while (ServerPhase == "mainCleared" && !leaving) yield return null;
            }
            ClearHubs(HubKind.Barrier, HubKind.Main, HubKind.Ufo);

            // ---- states 11-13: the HUV waves, then the boss ----
            if (InWave)
            {
                SetState(State.HuvRequest);
                Hud.ShowTip("HUV");
                AudioLibrary.PlayMusic(this, "bgm/DriftCity_chasing.mp3");
            }
            string[] waveAlerts = { "UC_HUV", "UC_HUVAgain", "UC_HUVAgain2", "UC_HUVAgain3", "UC_BossHUV" };
            while (InWave && !leaving)
            {
                Wave = Mathf.Clamp(ServerWave, 1, UndercityData.BossStage);
                bool boss = ServerPhase == "boss";
                SpawnWave(UndercityData.Wave(Layout.CityIndex, Wave), boss);
                ReportWave();
                ShareWave();
                SetState(boss ? State.BossWave : State.HuvWave);
                Hud.ShowPhase(boss ? 3 : 2);
                Alert(waveAlerts[Wave - 1]);
                if (pendingTimeCut > 0f) { pendingTimeCut = 0f; StartCoroutine(AlertLater("UC_HUVTimeReduce", 2.5f)); }
                warned60 = warned15 = critical11 = false;
                while (InWave && ServerWave == Wave && !leaving)
                {
                    Clock = Mathf.Max(0, Clock - Time.deltaTime);
                    KeepHuvsNear();
                    TickHuvShare();
                    if (boss) TickWarnings("UC_TimeCiritical", "UC_TimeCiritical2");
                    else TickWarnings(null, null);
                    UpdateHud();
                    yield return null;
                }
                ClearHuvs();
            }
            if (leaving) yield break;

            // ---- state 14 (cleared) or the failure, then UCRESULT with the server's rows ----
            Hud.HidePlay();
            Hud.SetHuvHealth(null, 0);
            Hud.SetParty(null);
            if (ServerPhase == "failed" || (result != null && !result.B("cleared")))
            {
                Failed = true;
                Alert("UC_Fail", 4f);
                GameEffects.Screen("quest_fail");
                AudioLibrary.PlaySfx("sfx/SID_QUEST_FAIL");
            }
            else
            {
                Cleared = true;
                SetState(State.Ending);
                GameEffects.Screen("congratulation");
                AudioLibrary.PlaySfx("sfx/SID_QUEST_SUCCESS");
            }
            yield return OnlineResultAndLeave(30f);
        }

        /// <summary>UC_HUB_NeedAccept's Accept: the run leader starts the mission, with the HUB slots this stage placed.</summary>
        void SendAccept()
        {
            var slots = hubs.Where(h => h && (h.Kind == HubKind.Tower || h.Kind == HubKind.RedTower)).Select(h => (object)h.Index).ToList();
            SocialNet.Request("uc.phase", new Dictionary<string, object> { ["phase"] = "accept", ["slots"] = slots }, r =>
            {
                if (r != null && !r.B("ok")) Debug.Log("Undercity accept: " + SocialNet.Err(r));
            });
        }

        /// <summary>The wave this stage spawned (levels, the boss); the server keeps the first report as the wave's HUVs.</summary>
        void ReportWave()
        {
            if (reportedWave == Wave) return;
            reportedWave = Wave;
            var list = huvs.Select(h => (object)new Dictionary<string, object> { ["level"] = h.Level, ["boss"] = h.Boss }).ToList();
            SocialNet.Request("uc.wave", new Dictionary<string, object> { ["wave"] = Wave, ["huvs"] = list }, null);
            if (waveHealth != null) ApplyWaveHealth();
        }

        // ---- the wave's HUVs are party HUVs (TRAFFIC.md section 14): the run leader's client runs them ----

        /// <summary>The HUVs run on this client (the run leader, who asked for the wave: BS_PktPartyHuv's owner, 0x4f0940).</summary>
        public bool HuvOwner => !Online || RunLeader;
        float huvShareTimer;
        bool huvsRemote;

        /// <summary>A new wave: off the run leader's client the HUVs are remote copies, hidden until its first state.</summary>
        void ShareWave()
        {
            huvsRemote = !HuvOwner;
            if (!huvsRemote) return;
            foreach (var h in huvs)
            {
                if (!h.Car) continue;
                h.Car.SetRemote(true);
                ShowHuv(h.Car, false);
            }
        }

        /// <summary>Every frame of a wave: the owner sends the HUVs' states; a member who became the run leader takes them over.</summary>
        void TickHuvShare()
        {
            if (!Online) return;
            if (huvsRemote && HuvOwner)
            {
                // the run leader left: this client runs the wave from where the copies are
                huvsRemote = false;
                foreach (var h in huvs) if (h.Car) { ShowHuv(h.Car, true); h.Car.SetRemote(false); }
            }
            if (!HuvOwner) return;
            huvShareTimer -= Time.deltaTime;
            if (huvShareTimer > 0 || huvs.Count == 0) return;
            huvShareTimer = ChaseMission.ShareInterval;
            var cars = huvs.Select(h => h.Car && h.Alive ? (object)h.Car.NetState().Cast<object>().ToList() : null).ToList();
            if (OnlineSession.Instance && OnlineSession.Instance.Net) OnlineSession.Instance.Net.Send("uc.huvpos", new Dictionary<string, object> { ["wave"] = Wave, ["cars"] = cars });
        }

        /// <summary>party.uc.huvpos: the run leader's HUV states, in the wave's order.</summary>
        void ApplyHuvPositions(Dictionary<string, object> m)
        {
            if (HuvOwner || m.I("wave") != ServerWave || Wave != ServerWave) return;
            var cars = m.L("cars");
            if (cars == null) return;
            for (int i = 0; i < huvs.Count && i < cars.Count; i++)
            {
                var c = huvs[i].Car;
                if (!c || !(cars[i] is List<object> s) || s.Count < 5) continue;
                if (!c.Remote) c.SetRemote(true);
                bool first = !c.HasRemoteState;
                c.ApplyRemote(new Vector3(Num(s[0]), Num(s[1]), Num(s[2])), Num(s[3]), Num(s[4]));
                if (first) ShowHuv(c, true);
            }
        }

        static float Num(object o) => o is double d ? (float)d : o is long l ? l : o is int i ? i : 0f;

        static void ShowHuv(TrafficCar c, bool on)
        {
            foreach (var r in c.GetComponentsInChildren<Renderer>(true)) r.enabled = on;
            foreach (var col in c.GetComponentsInChildren<Collider>(true)) col.enabled = on;
        }

        /// <summary>Test hook: the wave's HUVs that are remote copies with a state from the run leader.</summary>
        public int RemoteHuvsShown => huvs.Count(h => h.Car && h.Car.Remote && h.Car.HasRemoteState);

        void ApplyWaveHealth()
        {
            for (int i = 0; i < huvs.Count; i++)
            {
                if (i >= waveHealth.Length) { huvs[i].Max = huvs[i].Health = Mathf.Max(1f, huvs[i].Max); continue; }
                huvs[i].Max = huvs[i].Health = Mathf.Max(0.01f, waveHealth[i]);
            }
            waveTotal = Mathf.Max(huvs.Count, waveHealth.Length);
        }

        /// <summary>The car touched a HUB: the server decides (0xbbc / 0xbc6).</summary>
        void TouchedOnline(Hub hub)
        {
            if (pendingHubs.TryGetValue(hub, out float at) && Time.time - at < 1f) return;
            switch (hub.Kind)
            {
                case HubKind.Tower:
                    if (Phase == State.Hubs) Report(hub, "hub");
                    break;
                case HubKind.Barrier:
                    if (Phase == State.MainHub) Report(hub, "barrier");
                    break;
                case HubKind.RedTower:
                    if (Phase == State.Hubs && (car.Airborne || Time.time - lastAirborne < 2.5f)) Report(hub, "jump");
                    else Bounce(hub.transform.position, 8f);
                    break;
                case HubKind.Ufo:
                    if (Phase == State.MainHub) Report(hub, "ufo");
                    break;
                case HubKind.Main:
                    if (Phase != State.MainHub) break;
                    if (AliveCount(HubKind.Barrier) > 0)
                    {
                        Bounce(hub.transform.position, 18f);
                        GameEffects.Screen(51);
                        AudioLibrary.PlaySfx("sfx/SID_CHANGE_CRASH");
                    }
                    else Report(hub, "main");
                    break;
            }
        }

        void Report(Hub hub, string kind)
        {
            pendingHubs[hub] = Time.time;
            SocialNet.Request("uc.event", new Dictionary<string, object> { ["kind"] = kind, ["slot"] = hub.Index }, null);
        }

        /// <summary>Test / tool hook: reports every standing HUB of the kinds as rammed by this driver.</summary>
        void DebugReport(HubKind[] kinds)
        {
            foreach (var h in hubs.Where(h => h && h.Alive && kinds.Contains(h.Kind)).ToList())
            {
                string kind = h.Kind == HubKind.Tower ? "hub" : h.Kind == HubKind.RedTower ? "jump" : h.Kind == HubKind.Barrier ? "barrier" : h.Kind == HubKind.Ufo ? "ufo" : h.Kind == HubKind.Main ? "main" : null;
                if (kind != null) Report(h, kind);
            }
        }

        /// <summary>Test / tool hook: one full-strength hit (45) on the first HUV of the wave still standing.</summary>
        public bool DebugHitHuv()
        {
            if (!Online || waveHealth == null) return false;
            var h = huvs.FirstOrDefault(x => x.Alive);
            if (h == null) return false;
            lastHitSent = -10f;
            ReportHit(h, 45f);
            return true;
        }

        /// <summary>A hit on an HUV of the wave (0xc0 HuvHit): the shared health is the server's.</summary>
        void ReportHit(Huv h, float damage)
        {
            int index = huvs.IndexOf(h);
            if (index < 0 || waveHealth == null || index >= waveHealth.Length || Time.time - lastHitSent < 0.2f) return;
            lastHitSent = Time.time;
            SocialNet.Request("uc.event", new Dictionary<string, object> { ["kind"] = "hit", ["slot"] = index, ["damage"] = (double)damage }, null);
        }

        /// <summary>"party.uc.phase / event / huvs / left / result" for this run.</summary>
        public void OnServer(string t, Dictionary<string, object> m)
        {
            switch (t)
            {
                case "party.uc.phase":
                {
                    ServerPhase = m.S("phase", ServerPhase);
                    ServerAttempt = m.I("attempt");
                    int wave = m.I("wave");
                    if (InWave && wave != ServerWave) waveHealth = null;
                    ServerWave = wave;
                    float c = (float)m.N("clock");
                    if (c > 0) Clock = c;
                    // UC_HUVTimeReduce: the new wave's clock came with a negative change (0x4f2a20)
                    if (m.N("clockChange") < 0) { pendingTimeCut = -(float)m.N("clockChange"); LastTimeCut = pendingTimeCut; }
                    break;
                }
                case "party.uc.event": ApplyEvent(m); break;
                case "party.uc.huvpos": ApplyHuvPositions(m); break;
                case "party.uc.huvs":
                    if (m.I("wave") != ServerWave) break;
                    waveHealth = (m.L("health") ?? new List<object>()).Select(o => o is double d ? (float)d : 0f).ToArray();
                    if (reportedWave == ServerWave) ApplyWaveHealth();
                    break;
                case "party.uc.left":
                {
                    var s = Slots.FirstOrDefault(x => x.Name == m.S("name"));
                    if (s != null) s.Gone = true;
                    ChatBox.System($"[Undercity] {m.S("name")} left the Rescue Mission.");
                    break;
                }
                case "party.uc.result":
                    result = m;
                    if (m.O("driver") != null) OnlineSession.ApplyDriver(m.O("driver"));
                    break;
            }
        }

        void ApplyEvent(Dictionary<string, object> m)
        {
            int who = WhoOf(m.I("who"));
            int slot = m.I("slot");
            string kind = m.S("kind");
            var counts = m.L("counts");
            if (counts != null) slotCounts = counts.Select(o => o is double d ? (int)d : 0).ToList();
            float c = (float)m.N("clock");
            if (c > 0) Clock = c;
            Hub hub = null;
            switch (kind)
            {
                case "hub":
                case "jump":
                    hub = hubs.FirstOrDefault(h => h && h.Alive && h.Index == slot && (h.Kind == HubKind.Tower || h.Kind == HubKind.RedTower));
                    if (hub) Destroyed(hub, who);
                    else AddCount(who, kind == "jump" ? 1 : 0);
                    break;
                case "barrier":
                    hub = hubs.FirstOrDefault(h => h && h.Alive && h.Index == slot && h.Kind == HubKind.Barrier);
                    if (hub) Destroyed(hub, who);
                    else AddCount(who, 2);
                    break;
                case "ufo":
                    hub = hubs.FirstOrDefault(h => h && h.Alive && h.Index == slot && h.Kind == HubKind.Ufo);
                    if (hub) Destroyed(hub, who);
                    UfoBonus += (int)UndercityData.UfoBonusSeconds;
                    Alert("UC_TimeSave");
                    break;
                case "main":
                    hub = hubs.FirstOrDefault(h => h && h.Alive && h.Kind == HubKind.Main);
                    if (hub) Destroyed(hub, who);
                    else { AddCount(who, 3); MainHubDown = true; }
                    break;
                case "hit":
                {
                    int index = m.I("index");
                    float health = (float)m.N("health");
                    bool killed = m.B("killed");
                    var h = index >= 0 && index < huvs.Count ? huvs[index] : null;
                    if (h != null && h.Alive)
                    {
                        float amount = killed ? h.Health + 1f : Mathf.Max(0.0001f, h.Health - health);
                        Damage(h, amount, who);
                        if (!killed && h.Alive) h.Health = health;
                    }
                    else
                    {
                        var s = who == 0 ? Score : partners[who - 1].Score;
                        s.Hits++;
                        if (killed) { if (m.B("boss")) s.Boss++; else s.Huvs++; waveKilled++; }
                        if (who == 0) Score = s; else partners[who - 1].Score = s;
                    }
                    break;
                }
            }
            if (hub) pendingHubs.Remove(hub);
            UpdateHud();
        }

        /// <summary>Counts a HUB this stage doesn't have (0 tower, 1 red, 2 barrier, 3 Main HUB) for its driver.</summary>
        void AddCount(int who, int what)
        {
            if (who > partners.Count) return;
            var s = who == 0 ? Score : partners[who - 1].Score;
            if (what == 0) s.Hubs++; else if (what == 1) s.Jumps++; else if (what == 2) s.Barriers++; else s.MainHub++;
            if (who == 0) Score = s; else partners[who - 1].Score = s;
        }

        /// <summary>PLAYEXIT online: the server ends my run (paid for my counts) and the party goes on.</summary>
        IEnumerator QuitOnline()
        {
            Failed = true;
            Hud.HidePlay();
            Hud.SetHuvHealth(null, 0);
            Hud.SetParty(null);
            Alert("UC_Fail", 4f);
            GameEffects.Screen("quest_fail");
            AudioLibrary.PlaySfx("sfx/SID_QUEST_FAIL");
            result = null;
            SocialNet.Request("uc.leave", null, null);
            yield return OnlineResultAndLeave(10f);
        }

        /// <summary>0x716480 with the server's rows (0xbcc: every member was paid by the server), 7 s, then the city (0x716bc0).</summary>
        IEnumerator OnlineResultAndLeave(float wait)
        {
            while (result == null && wait > 0) { wait -= Time.unscaledDeltaTime; yield return null; }
            SetState(State.Result);
            ClearHuvs();
            ClearHubs(Enum.GetValues(typeof(HubKind)).Cast<HubKind>().ToArray());
            var rows = new List<UndercityHud.ResultRow>();
            int attempts = Mathf.Clamp(Attempt, 1, UndercityData.MainHubAttempts);
            if (result != null)
            {
                Cleared = result.B("cleared");
                MissionSeconds = (float)result.N("seconds", Time.time - startTime);
                attempts = Mathf.Clamp(result.I("attempts", attempts), 1, UndercityData.MainHubAttempts);
                int mine = result.B("quit") ? 0 : result.I("slot", MySlot);
                var list = (result.L("rows") ?? new List<object>()).OfType<Dictionary<string, object>>().ToList();
                for (int i = 0; i < list.Count && i < 4; i++)
                {
                    var o = list[i];
                    var sc = o.O("score");
                    rows.Add(new UndercityHud.ResultRow
                    {
                        Name = o.S("name"), Level = Mathf.Max(1, o.I("level", 1)), Self = i == mine,
                        Score = new UndercityData.Score { Hubs = sc.I("hubs"), Jumps = sc.I("jumps"), Barriers = sc.I("barriers"), MainHub = sc.I("mainHub"), Huvs = sc.I("huvs"), Boss = sc.I("boss"), Hits = sc.I("hits") },
                        Exp = (long)o.N("exp"), Mito = (long)o.N("mito"), Item = o.S("item"),
                        Boost120 = o.B("boost120"), Set = o.B("set"),
                    });
                }
                // 0xbcc +2 == 1: 11114 "Undercity 120% item has been applied."
                if (mine >= 0 && mine < rows.Count && rows[mine].Boost120) ChatBox.System(MissionText.Get("11114"));
            }
            else
            {
                MissionSeconds = Time.time - startTime;
                rows.Add(new UndercityHud.ResultRow { Name = PlayerProfile.Current?.Name ?? "Driver", Level = PlayerProfile.Current?.Level ?? 1, Self = true, Score = Score });
                RewardError = "No result from the server.";
            }
            int cls = UndercityData.Class(Cleared, attempts, MissionSeconds, MainHubDown || (result != null && result.B("mainHub")));
            Hud.CloseAlert();
            ResultView = UndercityHud.ShowResult(Cleared, cls, MissionSeconds, attempts, rows);
            float t = 7f * Mathf.Max(0.1f, TimeScale);
            while (t > 0) { t -= Time.deltaTime; yield return null; }
            Leave();
        }
    }
}
