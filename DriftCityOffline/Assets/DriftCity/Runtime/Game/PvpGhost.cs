using System;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// The Ghost Battle (DriftCity_RE/PVP.md 14.1): while watching a race (a Channel Battle broadcast or replay, feature
    /// net+0x274 bit 2 "BattleZone.GhostBattle", or any Battle Zone race with bit 3 "BattleZone.PvpGhostBattle"; bit 4
    /// "GhostBattleOnOff" shows the lobby / wait-room marks) a spectator presses GHOSTJOIN 2000000 (key G) beside the
    /// watched driver's row in VIEWING and races from that driver's place on the track against the "original" car:
    /// 11194 "You've entered the Ghost Battle. If you win the battle with selected user, Mito and EXP will be rewarded."
    /// GHOSTEXIT 2010000 (ESC) goes back to watching (11195); 11196 when it cannot start, 11200 when the original driver
    /// leaves. Client: 0x759060 (join: the watched car must be racing and the race started; the first join of a race
    /// needs ch_ghostpass or uses up a ch_ghost_%02d ticket of the channel, 0x78ce10), 0x759360 (request 0x212 with the
    /// watched car's id and look), 0x761580 (reply 0x213: the own car takes the original's look, position, heading and
    /// speed, state 3), 0x757500 (exit), 0x75fc10 (a request unanswered for 10 s gives up), 0x75e520 (the button's rules).
    /// Remake rules: the ghost is local (the server is not asked; online the car's state goes out as usual); the player's
    /// own car keeps its look and drives through the race cars; the ghost wins by finishing the laps the original still
    /// had before the original finishes (the race reward of a Free-arena 1st place); a replay bought from the replay list
    /// carries one ghost ticket (the client's per-channel tickets are not in the item tables), ch_ghostpass (Ch. Replay
    /// Ghost Pass) allows it anywhere.
    /// </summary>
    public static class PvpGhost
    {
        public const int JoinDialog = 2000000, ExitDialog = 2010000;
        public const string PassItem = "ch_ghostpass";
        public const int PassItemId = 10825;
        /// <summary>The ghost stays this long on the result of its race before returning to the viewer.</summary>
        public const float AfterFinish = 3f;

        static NuiView joinView, exitView;
        static GhostRunner runner;
        static bool ticket, usedThisRace;
        static UiManager hooked;
        /// <summary>Tests: allow joining without a pass or ticket.</summary>
        public static bool ForceAllowed;
        public static string LastText { get; private set; }

        public static bool Active => runner;
        public static GhostRunner Runner => runner;
        public static NuiView JoinView => joinView;
        public static NuiView ExitView => exitView;

        /// <summary>A new watched race (or replay); <paramref name="replayTicket"/>: the replay was bought from the list.</summary>
        public static void BeginRace(bool replayTicket)
        {
            Exit(null, true);
            ticket = replayTicket;
            usedThisRace = false;
            ChannelTicket = null; ticketChannel = -1;
        }

        /// <summary>A Channel Battle replay's ticket item (ch_ghost_01..20, useitem "Ch NN. Battle Ghost Ticket"; the server
        /// gives one with a bought replay, 0x397): the first join of the replay uses it up (0x759060 item use).</summary>
        public static string ChannelTicket { get; private set; }
        static int ticketChannel = -1;
        public static void SetChannelTicket(int channel)
        {
            ChannelTicket = channel >= 0 && channel < 20 ? $"ch_ghost_{channel + 1:00}" : null;
            ticketChannel = channel;
        }
        static bool HasChannelTicket(PlayerProfile p) => p != null && ChannelTicket != null && p.Count(ChannelTicket) > 0;

        public static bool HasPass(PlayerProfile p) => p != null && (p.Count(PassItem) > 0 || p.ItemCount(PassItemId) > 0);

        /// <summary>The client's first-join check: a pass, or a ticket for this race, or one already used in this race.</summary>
        public static bool Allowed => ForceAllowed || usedThisRace || ticket || HasPass(PlayerProfile.Current) || HasChannelTicket(PlayerProfile.Current);

        static void Hook(UiManager ui)
        {
            if (hooked == ui) return;
            hooked = ui;
            ui.On("ID_PVP_GHOSTJOIN", _ => JoinWatched());
            ui.On("ID_PVP_GHOSTEXIT", _ => Exit("11195"));
        }

        /// <summary>
        /// Called by VIEWING every frame: GHOSTJOIN shows beside the watched driver's row while that driver is racing, the
        /// race runs, no ghost is out and the player may join (0x75e520).
        /// </summary>
        public static void Draw(NuiView viewing, PvpSpectate.Driver? watched, bool raceRunning, RectTransform watchedRow)
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            Hook(ui);
            bool show = viewing && viewing.gameObject.activeSelf && watched.HasValue && watched.Value.Car && !watched.Value.Done && raceRunning && !Active && Allowed;
            if (!show) { Close(ref joinView); return; }
            if (!joinView) joinView = ui.Open(JoinDialog);
            if (!joinView) return;
            var btn = joinView.Control("ID_PVP_GHOSTJOIN");
            if (btn && watchedRow)
            {
                // the client moves the button to the watched row (0x75e520: y = row - 0x26)
                var t = btn.transform;
                t.position = new Vector3(t.position.x, watchedRow.position.y, t.position.z);
            }
#if ENABLE_INPUT_SYSTEM
            var k = UnityEngine.InputSystem.Keyboard.current;
            if (k != null && k.gKey.wasPressedThisFrame && !ChatFocused()) JoinWatched();
#endif
        }

        static bool ChatFocused()
        {
            var focus = UnityEngine.EventSystems.EventSystem.current ? UnityEngine.EventSystems.EventSystem.current.currentSelectedGameObject : null;
            return focus && focus.GetComponent<UnityEngine.UI.InputField>();
        }

        static void JoinWatched()
        {
            var order = PvpSpectate.Order;
            int i = PvpSpectate.Selected;
            if (i < 0 || i >= order.Count) { Say("11196"); return; }
            Join(order[i], PvpSpectate.WatchedTrack);
        }

        /// <summary>Starts the Ghost Battle against <paramref name="original"/> (false with 11196 when it cannot).</summary>
        public static bool Join(PvpSpectate.Driver original, RaceTrack track)
        {
            var car = GameFlow.Instance ? GameFlow.Instance.PlayerCar : null;
            if (Active || !car || track == null || !original.Car || original.Done || !Allowed) { Say("11196"); return false; }
            var p = PlayerProfile.Current;
            if (!usedThisRace && !ForceAllowed && !ticket && !HasPass(p) && HasChannelTicket(p))
            {
                // the first join uses up the channel's ticket (online the server spends it: crew.ghost)
                if (Net.OnlineSession.Active) Net.OnlineSession.Instance.Net.Request("crew.ghost", new System.Collections.Generic.Dictionary<string, object> { ["channel"] = ticketChannel }, _ => { });
                p.Give(ChannelTicket, -1);
            }
            usedThisRace = true;
            Close(ref joinView);
            runner = new GameObject("Ghost Battle").AddComponent<GhostRunner>();
            runner.Begin(car, original, track);
            var ui = UiManager.Instance;
            if (ui) exitView = ui.Open(ExitDialog);
            if (PvpSpectate.ViewingView) PvpSpectate.ViewingView.gameObject.SetActive(false);
            Say("11194");
            return true;
        }

        /// <summary>Back to watching (GHOSTEXIT, the end of the race, the original leaving); <paramref name="text"/> is the line shown.</summary>
        public static void Exit(string text, bool silent = false)
        {
            Close(ref exitView);
            Close(ref joinView);
            if (!runner) return;
            var r = runner;
            runner = null;
            r.End();
            UnityEngine.Object.Destroy(r.gameObject);
            if (PvpSpectate.ViewingView) PvpSpectate.ViewingView.gameObject.SetActive(true);
            PvpSpectate.ResumeWatching();
            if (!silent && text != null) Say(text);
        }

        static void Say(string key)
        {
            string t = Pvp.Text(key);
            if (string.IsNullOrWhiteSpace(t) || t == key)
                t = key switch
                {
                    "11194" => "You've entered the Ghost Battle.\nIf you win the battle with selected user, Mito and EXP will be rewarded.",
                    "11195" => "You've exited the Ghost Battle.",
                    "11196" => "You cannot join the Ghost Battle at this time.\nPlease try again later.",
                    "11200" => "Selected Ghost User has been disconnected. Moving back to the lobby.",
                    _ => key,
                };
            LastText = t;
            ChatBox.System(t.Replace("\n", " "));
            PvpChat.System(t.Replace("\n", " "));
        }

        static void Close(ref NuiView v)
        {
            if (v) { if (UiManager.Instance) UiManager.Instance.Close(v); else UnityEngine.Object.Destroy(v.gameObject); }
            v = null;
        }
    }

    /// <summary>The player's car in a Ghost Battle: placed on the original, its laps counted on the racing line.</summary>
    public sealed class GhostRunner : MonoBehaviour
    {
        DriftCar car;
        Vector3 savedPos;
        Quaternion savedRot;
        bool savedExternal;
        RaceTrack track;
        int lap, index;
        bool half;
        float finishedAt = -1f, clock;
        readonly List<(Collider a, Collider b)> ignored = new List<(Collider, Collider)>();
        public string Original { get; private set; }
        public bool Finished => finishedAt >= 0f;
        public bool Won { get; private set; }
        public int Lap => lap;
        public float Clock => clock;
        public DriftCar Car => car;

        internal void Begin(DriftCar c, PvpSpectate.Driver original, RaceTrack t)
        {
            car = c; track = t; Original = original.Name;
            savedPos = c.transform.position; savedRot = c.transform.rotation; savedExternal = c.ExternalControl;
            int n = Mathf.Max(1, t.Line.Count);
            index = RaceMission.NearestOnLine(t.Line, original.Car.position, 0);
            lap = Mathf.Clamp(Mathf.FloorToInt(Mathf.Clamp01(original.Fraction) * t.Laps + 1e-4f), 0, Mathf.Max(0, t.Laps - 1));
            half = index > n * 0.5f;
            // 0x761580: the own car takes the original's position, heading and speed
            var rb = c.GetComponent<Rigidbody>();
            Vector3 v = Vector3.zero;
            var orb = original.Car.GetComponent<Rigidbody>();
            if (orb && !orb.isKinematic) v = orb.linearVelocity;
            else if (PvpReplay.Current) { var rc = PvpReplay.Current.Recording.Cars.FirstOrDefault(x => x.Name == original.Name); if (rc != null) v = rc.Velocity(PvpReplay.Current.Clock); }
            RaceMission.Teleport(c, original.Car.position + Vector3.up * 0.3f, Quaternion.Euler(0f, original.Car.eulerAngles.y, 0f));
            if (rb) rb.linearVelocity = v;
            if (OnlineSession.Active && OnlineSession.Instance) OnlineSession.Instance.MarkTeleport();
            c.ExternalControl = false;
            // a ghost drives through the race cars
            var mine = c.GetComponentsInChildren<Collider>(true);
            foreach (var d in PvpSpectate.Order)
                if (d.Car && d.Car != c.transform)
                    foreach (var other in d.Car.GetComponentsInChildren<Collider>(true))
                        foreach (var m in mine) { Physics.IgnoreCollision(m, other, true); ignored.Add((m, other)); }
            var rig = PvpCameraRig.Instance;
            if (rig) rig.Release();
        }

        void Update()
        {
            if (!car) { PvpGhost.Exit(null, true); return; }
            clock += Time.deltaTime;
#if ENABLE_INPUT_SYSTEM
            // GHOSTEXIT's key (NATION J = 27, ESC)
            var k = UnityEngine.InputSystem.Keyboard.current;
            if (k != null && k.escapeKey.wasPressedThisFrame && !UiPopup.IsOpen) { PvpGhost.Exit("11195"); return; }
#endif
            var order = PvpSpectate.Order;
            int oi = order.FindIndex(d => d.Name == Original);
            if (oi < 0 || !order[oi].Car)
            {
                if (!Finished) { PvpGhost.Exit("11200"); return; }
            }
            bool originalDone = oi >= 0 && order[oi].Done;
            if (Finished)
            {
                if (clock - finishedAt >= PvpGhost.AfterFinish) PvpGhost.Exit("11195");
                return;
            }
            int n = track.Line.Count;
            int next = RaceMission.NearestOnLine(track.Line, car.transform.position, index);
            if (next > n * 0.4f && next < n * 0.6f) half = true;
            if (index > n * 0.8f && next < n * 0.2f && half)
            {
                half = false; lap++;
                if (lap >= track.Laps) { Finish(!originalDone); return; }
                RaceMission.LapEffect(lap + 1, track.Laps);
            }
            else if (index < n * 0.2f && next > n * 0.8f && lap > 0) { lap--; half = true; }
            index = next;
            // the original crossed first: the ghost lost
            if (originalDone) Finish(false);
        }

        static readonly System.Reflection.MethodInfo respawn = typeof(DriftCar).GetMethod("Respawn", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        /// <summary>Puts the car back where it waited with its driving state cleared (DriftCar's own respawn: velocity model,
        /// gear, air state), not only the rigidbody, so a crash during the ghost race does not carry over.</summary>
        static void Warp(DriftCar c, Vector3 pos, Quaternion rot)
        {
            if (respawn == null) { RaceMission.Teleport(c, pos, rot); return; }
            var prev = c.RespawnOverride;
            c.RespawnOverride = () => (pos - Vector3.up * 0.5f, rot);
            try { respawn.Invoke(c, null); }
            catch { RaceMission.Teleport(c, pos, rot); }
            finally { c.RespawnOverride = prev; }
        }

        /// <summary>Tests: the ghost crosses the line now.</summary>
        public void ForceFinish() { if (!Finished) Finish(!(PvpSpectate.Order.FirstOrDefault(d => d.Name == Original).Done)); }

        void Finish(bool won)
        {
            finishedAt = clock;
            Won = won;
            car.ExternalControl = true;
            GameEffects.RaceFinished(won);
            if (!won) return;
            var p = PlayerProfile.Current;
            if (p == null) return;
            var data = new Dictionary<string, object> { ["place"] = 1, ["bestLap"] = 0.0, ["city"] = track.City, ["arena"] = Pvp.ArenaFree };
            Progress.Reward("race", data, () =>
            {
                var (exp, mito) = Pvp.Reward(p.Level, 1, Pvp.ArenaFree);
                int before = p.Level;
                p.AddExp(exp); p.Mito += mito;
                return new Progress.Result { Ok = true, Exp = exp, Mito = mito, LevelUps = p.Level - before };
            }, r =>
            {
                if (r.Ok) ChatBox.System($"Ghost Battle won: EXP +{r.Exp}, Mito +{r.Mito}");
            });
        }

        internal void End()
        {
            foreach (var (a, b) in ignored) if (a && b) Physics.IgnoreCollision(a, b, false);
            ignored.Clear();
            if (!car) return;
            Warp(car, savedPos, savedRot);
            if (OnlineSession.Active && OnlineSession.Instance) OnlineSession.Instance.MarkTeleport();
            car.ExternalControl = true;   // back to watching: the spectator's car waits (savedExternal is the same for spectators)
        }
    }
}
