using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// One built effect NIF (meshes and keyframe controllers from NifSceneBuilder, particle systems from
    /// NifParticleEffect) played on its own clock from 0, the way the client clones a loaded effect, calls
    /// StartAnimations(0) and then Update(time) every frame. The clock ends at <see cref="Duration"/> and the effect
    /// removes itself. It can follow a transform: the translation only (the client's SetTranslate) or translation and
    /// rotation, with an offset in the followed object's frame.
    /// </summary>
    public sealed class NifEffect : MonoBehaviour
    {
        /// <summary>Effect clock (seconds of controller time).</summary>
        public float Time;
        /// <summary>Controller time at which the effect ends; 0 = never (removed by its owner).</summary>
        public float Duration;
        /// <summary>Clock rate (the client's UI effects can be squeezed into a given number of seconds).</summary>
        public float Speed = 1f;
        /// <summary>Earliest begin and latest end key time of every controller in the NIF (the client's 0x4cbbc0).</summary>
        public float KeyStart, KeyEnd;
        public Transform Follow;
        public bool FollowRotation;
        /// <summary>Offset in the followed object's frame (Unity axes).</summary>
        public Vector3 Offset;
        /// <summary>Optional position by time in the parent's space (the item pickup's flight).</summary>
        public Func<float, Vector3> Path;
        /// <summary>Called once when the clock passes <see cref="Duration"/>, just before the effect is destroyed.</summary>
        public Action<NifEffect> Ended;

        public NifAnimator Animator { get; private set; }
        public NifParticleEffect Particles { get; private set; }
        public string Nif { get; private set; }
        bool ended;

        /// <summary>Builds <paramref name="path"/> under <paramref name="parent"/> (local origin); null when the file is missing.</summary>
        public static NifEffect Create(string path, Transform parent = null, int layer = -1)
        {
            var fs = DriftCityData.Files;
            if (fs == null || !fs.Exists(path)) return null;
            var go = new GameObject(System.IO.Path.GetFileNameWithoutExtension(path));
            if (parent) go.transform.SetParent(parent, false);
            var fx = go.AddComponent<NifEffect>();
            fx.Nif = path;
            // StartAnimations(0) in the client starts every controller, saved active or not
            var built = new NifSceneBuilder { LogErrors = false, IncludeInactiveControllers = true }.Load(path, go.transform);
            // the client moves the clone's root node with SetTranslate: its own translation is replaced
            foreach (Transform top in built.transform) top.localPosition = Vector3.zero;
            fx.Animator = built.GetComponent<NifAnimator>();
            if (fx.Animator) fx.Animator.enabled = false;   // posed from this clock instead of the game clock
            float lo = float.MaxValue, hi = 0f;
            if (fx.Animator)
                foreach (var c in fx.Animator.Channels) { lo = Mathf.Min(lo, c.Start); hi = Mathf.Max(hi, c.Stop); }
            fx.Particles = NifParticleEffect.Spawn(path, go.transform.position, go.transform.rotation, go.transform);
            if (fx.Particles)
            {
                fx.Particles.transform.localPosition = Vector3.zero;
                fx.Particles.transform.localRotation = Quaternion.identity;
                fx.Particles.Lifetime = 0f;
                var r = fx.Particles.KeyRange;
                if (r.y > r.x) { lo = Mathf.Min(lo, r.x); hi = Mathf.Max(hi, r.y); }
            }
            fx.KeyStart = lo == float.MaxValue ? 0f : lo;
            fx.KeyEnd = hi;
            fx.Duration = hi;
            if (layer >= 0) SetLayer(go, layer);
            fx.Pose(0f);
            return fx;
        }

        internal static void SetLayer(GameObject go, int layer)
        {
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
        }

        /// <summary>Poses the keyframe controllers at a clock time (also used by batch renders).</summary>
        public void Pose(float t)
        {
            Time = t;
            if (Animator) Animator.Sample(t);
            Place();
        }

        void Place()
        {
            if (Path != null) transform.localPosition = Path(Time);
            if (!Follow) return;
            if (FollowRotation) transform.SetPositionAndRotation(Follow.TransformPoint(Offset), Follow.rotation);
            else transform.SetPositionAndRotation(Follow.position + Follow.rotation * Offset, Quaternion.identity);
        }

        void LateUpdate()
        {
            if (ended) return;
            if (Follow == null && followWasSet) { End(); return; }   // what it was attached to is gone
            Pose(Time + UnityEngine.Time.deltaTime * Speed);
            if (Duration > 0f && Time >= Duration) End();
        }

        bool followWasSet;
        public void Attach(Transform target, Vector3 offset, bool rotate)
        {
            Follow = target; Offset = offset; FollowRotation = rotate; followWasSet = target != null;
            Place();
        }

        public void End()
        {
            if (ended) return;
            ended = true;
            var e = Ended; Ended = null;
            e?.Invoke(this);
            if (Application.isPlaying) Destroy(gameObject); else DestroyImmediate(gameObject);
        }
    }

    /// <summary>
    /// The client's one-shot effect NIFs and the game events that play them (DriftCity_RE\EFFECTS.md, "One-shot effect
    /// NIFs"): the level-up pillar, the screen-space result banners (effects\ui), the item pickup, palm tree and fire
    /// hydrant hits, jump take-off and landing, near-miss grazes and the HUV explosion. Sounds are the ones the client
    /// plays with each (sfx ids from 0x5232b0).
    /// </summary>
    public static class GameEffects
    {
        /// <summary>Layer of the screen-space effects; only the screen effect camera draws it.</summary>
        public const int ScreenLayer = 29;
        /// <summary>The client's UI effect table (0x6b3b00, effect manager +0xf78): index = the id 0x69b900 plays.</summary>
        public static readonly string[] ScreenTable =
        {
            "ui_levelup", "arbeit_com", "arbeit_com_gold", "arbeit_com_silver", "arbeit_com_bronze", "arbeit_fail",
            "timeattack_com", "timeattack_fail", "im_success", "arrest_fail", "arrest_success", "quest_fail",
            "quest_success", "3rd_anniversary_fin", "qs_newrecord", "ui_upgrade_s", "ui_upgrade_f", "ui_upgrade_d",
            "pvp_countdownstart", "pvp_finallap", "pvp_finish", "pvp_lap2", "pvp_lap3", "pvp_lap4", "pvp_lap5", "pvp_lap6",
            "pvp_lap7", "pvp_raceover", "pvp_retire", "pvp_tencount", "pvp_winner", "pvp_wrongway", "pvp_boostcharge100",
            "pvp_boostcharge50", "pvp_crash_recover100", "pvp_boostcharget50", "pvp_null", "pvp_untouchable", "pvp_booost",
            "pvp_overheat", "pvp_skidrush", "channel_count", "mitbomb_ui", "goldentime", "pvp_repair_girl_ui",
            "beat_success", "beat_fail", "pvp_repair_girl_plane", "i_d_repair02_5s_left", "i_d_repair02_5s_right",
            "t_ob_event_04", "effects/undercity/changLR", "critical", "party_soonchal", "im_failed", "congratulation",
            "transform_success", "Game/X_tree001/X_tree001_button", "effects/ChocoletWrap/valen_choco_fx_01",
            "effects/GoodHarvest/goodharvest_fx_01",
        };

        // 0x69b900: these four replace each other; a new one waits for the one on screen
        static readonly string[] Exclusive = { "im_success", "im_failed", "arrest_success", "arrest_fail" };

        /// <summary>World effects are skipped when false (tests, the client's effect switch 0x8d2982).</summary>
        public static bool Enabled = true;
        /// <summary>Sounds are skipped when false.</summary>
        public static bool Sounds = true;

        static Transform screenRoot;
        static Camera screenCamera;
        static readonly Dictionary<string, NifEffect> screenPlaying = new Dictionary<string, NifEffect>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string> screenNext = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, List<NifEffect>> pools = new Dictionary<string, List<NifEffect>>();

        // ------------------------------------------------------------------ building blocks

        static string EffectPath(string name)
        {
            name = name.Replace('/', '\\');
            if (!name.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)) name += ".nif";
            return name.StartsWith("effects\\", StringComparison.OrdinalIgnoreCase) ? name : "effects\\" + name;
        }

        static void Sfx(string id)
        {
            if (Sounds) AudioLibrary.PlaySfx("sfx/" + id);
        }

        /// <summary>The client keeps at most 10 of each kind (ring buffers in the effect manager): the oldest goes.</summary>
        static void Track(string pool, NifEffect fx, int max = 10)
        {
            if (!pools.TryGetValue(pool, out var list)) pools[pool] = list = new List<NifEffect>();
            list.RemoveAll(e => !e);
            list.Add(fx);
            while (list.Count > max) { var old = list[0]; list.RemoveAt(0); if (old) old.End(); }
        }

        static int Live(string pool)
        {
            if (!pools.TryGetValue(pool, out var list)) return 0;
            list.RemoveAll(e => !e);
            return list.Count;
        }

        /// <summary>Plays effects\<paramref name="name"/>.nif once at a world position (identity rotation, as the client
        /// only sets the clone's translation). <paramref name="seconds"/> &lt;= 0 plays the NIF's key range.</summary>
        public static NifEffect At(string name, Vector3 position, float seconds = 0f)
        {
            if (!Enabled) return null;
            var fx = NifEffect.Create(EffectPath(name));
            if (!fx) return null;
            fx.transform.SetPositionAndRotation(position, Quaternion.identity);
            if (seconds > 0f) fx.Duration = seconds;
            return fx;
        }

        /// <summary>Plays effects\<paramref name="name"/>.nif on a moving object: translation (and rotation when
        /// <paramref name="rotate"/>) follow it every frame, <paramref name="offset"/> is in its frame (Unity axes).</summary>
        public static NifEffect Attach(string name, Transform target, Vector3 offset, bool rotate, float seconds = 0f)
        {
            if (!Enabled || !target) return null;
            var fx = NifEffect.Create(EffectPath(name));
            if (!fx) return null;
            fx.Attach(target, offset, rotate);
            if (seconds > 0f) fx.Duration = seconds;
            return fx;
        }

        // ------------------------------------------------------------------ screen effects (effects\ui)

        /// <summary>
        /// The orthographic camera the client draws its UI effects with (0x698140, manager +0xdc / +0xe0): at the origin
        /// looking along +Y (Gamebryo), frustum -51.2..51.2 x -38.4..38.4, far 512 (1000 for the item pickup). In Unity
        /// axes it looks along +Z with an orthographic half-height of 38.4; the width follows the screen (the HUD canvas
        /// matches height the same way). It stands far below the city on its own layer.
        /// </summary>
        public static Camera ScreenCamera
        {
            get
            {
                if (!screenCamera) BuildScreen();
                return screenCamera;
            }
        }

        public static Transform ScreenRoot
        {
            get
            {
                if (!screenRoot) BuildScreen();
                return screenRoot;
            }
        }

        static void BuildScreen()
        {
            if (!screenRoot)
            {
                var go = new GameObject("Screen Effects");
                if (Application.isPlaying) UnityEngine.Object.DontDestroyOnLoad(go);
                go.transform.position = new Vector3(0f, -50000f, 0f);
                screenRoot = go.transform;
            }
            if (!screenCamera)
            {
                var cam = new GameObject("Screen Effect Camera").AddComponent<Camera>();
                cam.transform.SetParent(screenRoot, false);
                cam.orthographic = true;
                cam.orthographicSize = 38.4f;
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = 1000f;
                cam.clearFlags = CameraClearFlags.Depth;
                cam.cullingMask = 1 << ScreenLayer;
                cam.depth = 50f;
                cam.allowHDR = false;
                cam.useOcclusionCulling = false;
                cam.enabled = false;
                screenCamera = cam;
            }
        }

        static void HideScreenLayerFromOtherCameras()
        {
            foreach (var c in Camera.allCameras)
                if (c != screenCamera) c.cullingMask &= ~(1 << ScreenLayer);
        }

        static void UpdateScreenCamera()
        {
            if (!screenCamera) return;
            bool any = false;
            foreach (var kv in screenPlaying) if (kv.Value) { any = true; break; }
            if (!any && screenRoot) foreach (Transform t in screenRoot) if (t.GetComponent<NifEffect>()) { any = true; break; }
            screenCamera.enabled = any;
        }

        /// <summary>
        /// Plays a screen effect (effects\ui\<paramref name="name"/>.nif, or a <see cref="ScreenTable"/> name) the way
        /// 0x69b900 / 0x69bf40 do: placed at (0, 100, 0) in front of the UI camera, clock from 0 at speed 1 (or squeezed
        /// into <paramref name="seconds"/> when > 0), gone when the clock passes the NIF's key range. Playing an effect
        /// that is already on screen restarts it; arrest_success/arrest_fail/im_success/im_failed queue behind each other.
        /// </summary>
        public static NifEffect Screen(string name, float seconds = 0f)
        {
            if (string.IsNullOrEmpty(name)) return null;
            name = name.EndsWith(".nif", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name;
            name = FactionVariant(name, RooFaction());
            if (Array.IndexOf(Exclusive, name) >= 0)
                foreach (var other in Exclusive)
                    if (other != name && screenPlaying.TryGetValue(other, out var busy) && busy)
                    {
                        screenNext[other] = name;
                        return null;
                    }
            if (screenPlaying.TryGetValue(name, out var old) && old) { old.Ended = null; old.End(); }
            BuildScreen();
            HideScreenLayerFromOtherCameras();
            string path = name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0 ? name.Replace('/', '\\') + ".nif" : EffectPath("ui\\" + name);
            var fx = NifEffect.Create(path, screenRoot, ScreenLayer);
            if (!fx) return null;
            fx.transform.localPosition = new Vector3(0f, 0f, 100f);   // Gamebryo (0, 100, 0)
            fx.transform.localRotation = Quaternion.identity;
            fx.Duration = fx.KeyEnd - fx.KeyStart > 0f ? fx.KeyEnd : 0.001f;
            if (seconds > 0f && fx.Duration > 0f) fx.Speed = fx.Duration / seconds;
            fx.Pose(0f);
            screenPlaying[name] = fx;
            fx.Ended = e =>
            {
                screenPlaying.Remove(name);
                if (screenNext.TryGetValue(name, out var next)) { screenNext.Remove(name); Screen(next); }
                UpdateScreenCamera();
            };
            screenCamera.enabled = true;
            return fx;
        }

        /// <summary>Plays the client's UI effect number <paramref name="index"/> (the id passed to 0x69b900).</summary>
        public static NifEffect Screen(int index, float seconds = 0f) =>
            index >= 0 && index < ScreenTable.Length ? Screen(ScreenTable[index], seconds) : null;

        public static bool ScreenPlaying(string name) => screenPlaying.TryGetValue(name, out var fx) && fx;

        /// <summary>Tests: forces the faction check of <see cref="FactionVariant"/> (null = the profile's faction).</summary>
        public static bool? RooOverride;

        static bool RooFaction()
        {
            if (RooOverride.HasValue) return RooOverride.Value;
            try { return Factions.IsRoo; } catch { return false; }
        }

        /// <summary>
        /// 0x69b900's faction rule: for Team ROO (character dword +0xed == 1, "Time Attack" is ROO's Delivery Service) the
        /// part-time job banners become the time-attack ones: 1 arbeit_com -> 6 timeattack_com, 5 arbeit_fail -> 7
        /// timeattack_fail. Every other effect is unchanged (the ROO chase banners beat_success / beat_fail are picked by
        /// the caller, 0x6f7d80).
        /// </summary>
        public static string FactionVariant(string name, bool roo)
        {
            if (!roo) return name;
            if (string.Equals(name, "arbeit_com", StringComparison.OrdinalIgnoreCase)) return "timeattack_com";
            if (string.Equals(name, "arbeit_fail", StringComparison.OrdinalIgnoreCase)) return "timeattack_fail";
            return name;
        }

        /// <summary>
        /// A finished part-time job / time attack whose rank list (QSRANKLIST 2830000) closes (0x558800): arbeit_com
        /// (timeattack_com for ROO), then the medal banner by the run's place in the ranking: 1 arbeit_com_gold, 2
        /// arbeit_com_silver, 3 arbeit_com_bronze (none below 3rd).
        /// </summary>
        public static void ArbeitDone(int rank)
        {
            Screen("arbeit_com");
            string medal = rank == 1 ? "arbeit_com_gold" : rank == 2 ? "arbeit_com_silver" : rank == 3 ? "arbeit_com_bronze" : null;
            if (medal != null) Screen(medal);
        }

        /// <summary>A failed part-time job (the server's quest-failed reply 0x6f6db0): arbeit_fail (timeattack_fail for ROO).</summary>
        public static void ArbeitFailed() => Screen("arbeit_fail");

        /// <summary>Durations 0x757750 squeezes the gate effects into (seconds; 0 = the NIF's own length).</summary>
        static readonly float[] GateEffectSeconds = { 0f, 0f, 0f, 30f, 0f, 5f, 30f, 0f };
        static readonly string[] GateEffectNames = { "pvp_null", "pvp_boostcharge50", "pvp_boostcharge100", "pvp_skidrush", "pvp_crash_recover100", "pvp_untouchable", "pvp_booost", "pvp_boostcharget50" };

        /// <summary>The banner of a PvP gate effect type (PlayerEvent 0x50e530 -> 0x757750 / 0x726f20, NewGateEffect
        /// feature 0x40000): 0 pvp_null, 1 pvp_boostcharge50 (half charge), 2 pvp_boostcharge100 (full charge), 3 pvp_skidrush
        /// (30 s), 4 pvp_crash_recover100 (repair), 5 pvp_untouchable (5 s), 6 pvp_booost (30 s), 7 pvp_boostcharget50.</summary>
        public static string GateEffect(int type) => type >= 0 && type < GateEffectNames.Length ? GateEffectNames[type] : null;

        public static NifEffect PlayGateEffect(int type)
        {
            string n = GateEffect(type);
            return n == null ? null : Screen(n, GateEffectSeconds[type]);
        }

        /// <summary>Golden time (result entry bonus flag 8 in 0x588100 / 0x58a070): the goldentime banner.</summary>
        public static NifEffect GoldenTime() => Screen("goldentime");

        // ------------------------------------------------------------------ result banners of the other game screens

        /// <summary>Plays sfx/<paramref name="id"/>, or <paramref name="fallback"/> when this data set lacks the file
        /// (SID_GUARD, SID_PATROL_PARTY_COUNT, SID_VALENTAIN_COMPLETE and SID_XMAS_EVENT_SUCCESS are not in the US data).</summary>
        static void SfxOr(string id, string fallback)
        {
            if (!Sounds || !Application.isPlaying) return;
            if (fallback != null && !AudioLibrary.Wav("sfx/" + id)) id = fallback;
            Sfx(id);
        }

        /// <summary>
        /// An instant mission's end. Completed (packet 0x3e5 -> 0x4f3ff0 -> 0x6f6560): SID_QUEST_SUCCESS and 8 im_success
        /// ("Instant Mission Complete"), the party patrol included. Failed (packet 0x3e6, 0x4efa30): SID_QUEST_FAIL and 54
        /// im_failed. The two queue behind arrest_success / arrest_fail (0x69b900's exclusive set).
        /// </summary>
        public static NifEffect InstantMission(bool success, bool sound = true)
        {
            if (sound) Sfx(success ? "SID_QUEST_SUCCESS" : "SID_QUEST_FAIL");
            return Screen(success ? "im_success" : "im_failed");
        }

        /// <summary>
        /// The party patrol's alert (0x4f4210 on the start packet 0xc24, then the patrol tick 0x61d7be while any of its four
        /// posts is still open): 53 party_soonchal "EMERGENCY PATROL REQUESTED!" squeezed into 2.5 s, replayed when it ends.
        /// Call every frame of a running party patrol: it only starts the banner when it is not on screen.
        /// </summary>
        public static NifEffect PatrolAlert(bool restart = false)
        {
            if (!restart && ScreenPlaying("party_soonchal")) return null;
            return Screen("party_soonchal", 2.5f);
        }

        /// <summary>The party patrol's last seconds (0x61d829: less than 10.5 s left, once): SID_PATROL_PARTY_COUNT and 29
        /// pvp_tencount squeezed into 10 s. <see cref="PatrolCountStop"/> takes it off when time is added again.</summary>
        public static NifEffect PatrolCount()
        {
            SfxOr("SID_PATROL_PARTY_COUNT", "SID_PVP_RACE_COUNT");
            return Screen("pvp_tencount", 10f);
        }

        /// <summary>0x61d86e: the clock went back above 10.5 s (a post's bonus time): the count banner stops (0x698840).</summary>
        public static void PatrolCountStop() => Stop("pvp_tencount");

        /// <summary>Ends a screen effect that is playing (the client's 0x698840).</summary>
        public static void Stop(string name)
        {
            if (screenPlaying.TryGetValue(name, out var fx) && fx) fx.End();
        }

        /// <summary>Upgrade / enhancement result codes of 0x6f4100 / 0x5c8fd0 (0x30000 + n).</summary>
        public const int UpgradeSuccess = 0, UpgradeFail = 1, UpgradeDestroyed = 2;

        /// <summary>
        /// A part modification (enhance) result (packet 0x19e -> 0x6f4100, and the parts shop 0x5c8fd0 / 0x616060): 0x30000
        /// success = 15 ui_upgrade_s "Modification successful" + SID_UPGRADE_SUCCESS (text 10102 / 10765), 0x30001 failure =
        /// 16 ui_upgrade_f + SID_UPGRADE_FAIL (10103 / 10766), 0x30002 destroyed = 17 ui_upgrade_d "Parts Destroyed" +
        /// SID_UPGRADE_DESTROY (10104). A restored part (0x6f42b0, 0x30003 / 0x30004, text 10882) plays the success pair.
        /// </summary>
        public static NifEffect Upgrade(int result)
        {
            result = Mathf.Clamp(result, UpgradeSuccess, UpgradeDestroyed);
            Sfx(result == UpgradeSuccess ? "SID_UPGRADE_SUCCESS" : result == UpgradeFail ? "SID_UPGRADE_FAIL" : "SID_UPGRADE_DESTROY");
            return Screen(15 + result);
        }

        /// <summary>
        /// A part conversion (packet 0x1ab -> 0x4f53a0: 10732 "%s was successfully converted." with SID_GUARD, and 56
        /// transform_success "Converting Successful" for the guaranteed result of OptionCraft_Guarantee). The remake's
        /// combine (two parts + a seed into the next grade) and a successful manufacturing are its conversions.
        /// </summary>
        public static NifEffect Converted()
        {
            SfxOr("SID_GUARD", "SID_UPGRADE_SUCCESS");
            return Screen("transform_success");
        }

        /// <summary>
        /// A Channel Battle round starting (0x723d60, the race start of a crew room: the Team Center object's 0x674e10 shows
        /// the two crews and plays 41 channel_count, the 3-2-1, before the race stage loads).
        /// </summary>
        public static NifEffect ChannelRound() => Screen("channel_count");

        /// <summary>
        /// Mittron Fireworks aimed at the local racer (packet 0x394 -> 0x50a4b0 when the target is me): the booster is set
        /// off for 5 s (0x47beb0(5.0, 1); the remake fills the gauge), 42 mitbomb_ui "MITTRON FIREWORK", and the race
        /// manager's +0x1b8(0, 1.8) [not traced]. The car side (entry 42's second NIF mitbomb_car, shown while the car's
        /// state bit 0x8000 is set, 0x72e9c0) is attached for the same 5 s.
        /// </summary>
        public static NifEffect MittronFirework(DriftCar car = null)
        {
            if (!car) car = LocalCar();
            if (car) { car.BoostGauge = 1f; Attach(@"ui\mitbomb_car", car.transform, Vector3.zero, true, 5f); }
            return Screen("mitbomb_ui");
        }

        /// <summary>
        /// The repair girl (Gio, i_d_repair01; BattleQuickSlots' repair kit). The client: a repair with the character flag
        /// +0xf32 plays 44 pvp_repair_girl_ui + SID_PVP_REPAIR_GIRL (0x4791a0; without the flag 39 pvp_overheat +
        /// SID_PVP_REPAIR); a racer whose state gains bit 0x80000 gets entry 44's car NIF pvp_repair_girl for 3.5 s, the
        /// local one also 47's pvp_repair_girl_plane, and with bit 0x200000 (the 5-second repair, i_d_repair02) 48 / 49
        /// i_d_repair02_5s_left / _right for 4 s (0x72e8df..0x72e9bb). The car NIFs follow the car with its rotation.
        /// </summary>
        public static void RepairGirl(DriftCar car = null, bool fiveSecond = false)
        {
            if (!car) car = LocalCar();
            Sfx("SID_PVP_REPAIR_GIRL");
            Screen("pvp_repair_girl_ui");
            if (!car) return;
            Attach(@"ui\pvp_repair_girl", car.transform, Vector3.zero, true, 3.5f);
            Attach(@"ui\pvp_repair_girl_plane", car.transform, Vector3.zero, true, 3.5f);
            if (fiveSecond)
            {
                Attach(@"ui\i_d_repair02_5s_left", car.transform, Vector3.zero, true, 4f);
                Attach(@"ui\i_d_repair02_5s_right", car.transform, Vector3.zero, true, 4f);
            }
        }

        /// <summary>A Christmas tree decoration given (0x694a5b -> 0x694aa4): SID_XMAS_EVENT_SUCCESS and 57 X_tree001_button.</summary>
        public static NifEffect XmasDecoration()
        {
            SfxOr("SID_XMAS_EVENT_SUCCESS", "SID_ITEM_INSTALL");
            return Screen(57);
        }

        /// <summary>A wrapped Valentine / Easter basket revealed (VALEN, 0x694098, before text 11578): SID_VALENTAIN_COMPLETE
        /// and 58 valen_choco_fx_01.</summary>
        public static NifEffect ChocolateWrapped()
        {
            SfxOr("SID_VALENTAIN_COMPLETE", "SID_ITEM_DROP_GET1");
            return Screen(58);
        }

        /// <summary>A distribution box made in the harvest event (AUTUMN tick 0x692c50 -> 0x692d00 on the result):
        /// 59 goodharvest_fx_01 and SID_VALENTAIN_COMPLETE.</summary>
        public static NifEffect HarvestBoxMade()
        {
            var fx = Screen(59);
            SfxOr("SID_VALENTAIN_COMPLETE", "SID_ITEM_DROP_GET1");
            return fx;
        }

        // ------------------------------------------------------------------ game events

        static DriftCar LocalCar()
        {
            if (CitySession.Current && CitySession.Current.Car) return CitySession.Current.Car;
            return GameFlow.Instance ? GameFlow.Instance.PlayerCar : null;
        }

        /// <summary>
        /// Level up (0x69c040, from the reward replies): SID_LEVELUP, the ui_levelup banner and effects\levelup.nif on
        /// the local car for 2.467 s, following its position with no rotation (0x698e90).
        /// </summary>
        public static void LevelUp(DriftCar car = null)
        {
            if (!car) car = LocalCar();
            Sfx("SID_LEVELUP");
            Screen("ui_levelup");
            if (car) Track("levelup", Attach("levelup", car.transform, Vector3.zero, false, 2.467f), 1);
        }

        /// <summary>
        /// End of an HUV chase (0x6f7d80): arrest_success + SID_HUV_SUCCESS when caught, arrest_fail + SID_HUV_FAIL
        /// when it got away. A caught HUV plays effects\huv_explosion.nif once on its body (0x6f87e0, HUV state 2).
        /// </summary>
        public static void ChaseEnded(bool caught, Transform huv = null)
        {
            Screen(caught ? "arrest_success" : "arrest_fail");
            Sfx(caught ? "SID_HUV_SUCCESS" : "SID_HUV_FAIL");
            if (caught && huv) HuvExplosion(huv);
        }

        /// <summary>effects\huv_explosion.nif on an HUV: position and rotation follow it, played once.</summary>
        public static NifEffect HuvExplosion(Transform huv)
        {
            Sfx("SID_HUV_EXPLOSION");
            return Attach("huv_explosion", huv, Vector3.zero, true);
        }

        /// <summary>
        /// The local player crossing the finish line (0x723fe0): the winner gets pvp_winner + SID_PVP_RACE_WINNER,
        /// everyone else pvp_finish + SID_PVP_RACE_FINISH; a new best time adds qs_newrecord.
        /// </summary>
        public static void RaceFinished(bool won, bool newRecord = false, int rank = 0)
        {
            if (rank > 0) VoiceLines.Rank(rank);   // 0x72407c: the finishing place's voice line
            Screen(won ? "pvp_winner" : "pvp_finish");
            Sfx(won ? "SID_PVP_RACE_WINNER" : "SID_PVP_RACE_FINISH");
            if (newRecord) Screen("qs_newrecord");
        }

        /// <summary>
        /// An item picked up (0x69fdd0 from the drop handlers 0x4f5ca0 / 0x4fb280): SID_ITEM_DROP_GET1, then
        /// effects\getitem.nif drawn with the UI camera at (41, 20, 10) for 1 s, flying to (-40, 20, -35) by 1.5 s
        /// (Gamebryo axes, 0x6a3ca0), then SID_ITEM_DROP_GET2.
        /// </summary>
        public static NifEffect GetItem()
        {
            Sfx("SID_ITEM_DROP_GET1");
            BuildScreen();
            HideScreenLayerFromOtherCameras();
            var fx = NifEffect.Create(EffectPath("getitem"), screenRoot, ScreenLayer);
            if (!fx) return null;
            var from = new Vector3(41f, 10f, 20f);   // Gamebryo (41, 20, 10)
            var to = new Vector3(-40f, -35f, 20f);   // Gamebryo (-40, 20, -35)
            fx.Path = t => t <= 1f ? from : t >= 1.5f ? to : Vector3.Lerp(from, to, (t - 1f) / 0.5f);
            fx.Duration = 1.5f;
            fx.Pose(0f);
            fx.Ended = e => { Sfx("SID_ITEM_DROP_GET2"); UpdateScreenCamera(); };
            screenCamera.enabled = true;
            return fx;
        }

        /// <summary>The effect NIF for a map object's EffectID (modeltable.nxt column 3): 11/12/13 palm trees, 2 fire hydrant.</summary>
        public static string PropEffect(int effectId) =>
            effectId == 11 ? "palmtree_explosion01" : effectId == 12 ? "palmtree_explosion02" : effectId == 13 ? "palmtree_explosion03"
            : effectId == 2 ? "hydrant_explosion" : null;

        /// <summary>
        /// A car knocking over a map object (0x405010 / 0x405170 -> 0x4035f0 / 0x403660 -> 0x69f870 / 0x69f9f0): palm
        /// trees (EffectID 11/12/13) play palmtree_explosion01/02/03 for 3.333 s and SID_HIT_TREE, the fire hydrant
        /// (EffectID 2) hydrant_explosion for 8.333 s and SID_HIT_HYDRANT. <paramref name="basePosition"/> is the
        /// object's placement point (its foot); the client then keeps the object down for 10 s.
        /// </summary>
        public static NifEffect PropHit(int effectId, Vector3 basePosition)
        {
            string nif = PropEffect(effectId);
            if (nif == null) return null;
            Sfx(effectId == 2 ? "SID_HIT_HYDRANT" : "SID_HIT_TREE");
            var fx = At(nif, basePosition, effectId == 2 ? 8.333f : 3.333f);
            if (fx) Track(effectId == 2 ? "hydrant" : "palm", fx);
            return fx;
        }

        /// <summary>Jump take-off (0x4851ae: all four wheels more than 0.5 m up): effects\jump_up.nif on the car's
        /// position, no rotation, 1.333 s (0x69fc90).</summary>
        public static NifEffect JumpUp(DriftCar car)
        {
            if (!car) return null;
            if (RaceMission.Active || OnlineRace.Active) VoiceLines.Jumped();   // 0x4851dc
            var fx = Attach("jump_up", car.transform, Vector3.zero, false, 1.333f);
            if (fx) Track("jump", fx);
            return fx;
        }

        /// <summary>Landing (0x485299): effects\jump_down.nif on the car's position for 1.333 s (0x69fb40) and SID_JUMP_END.</summary>
        public static NifEffect JumpDown(DriftCar car)
        {
            if (!car) return null;
            Sfx("SID_JUMP_END");
            var fx = Attach("jump_down", car.transform, Vector3.zero, false, 1.333f);
            if (fx) Track("jump", fx);
            return fx;
        }

        static readonly Dictionary<DriftCar, int> airState = new Dictionary<DriftCar, int>();

        /// <summary>
        /// Call every physics step for the local car: plays <see cref="JumpUp"/> when all four wheels are more than
        /// 0.5 m above the ground (the client's jump state 1 -> 2) and <see cref="JumpDown"/> on the first touch-down
        /// after the car was airborne (state 1/2 -> 3), as DriftCar's own air state does.
        /// </summary>
        public static void TrackJump(DriftCar car)
        {
            if (!car) return;
            airState.TryGetValue(car, out int s);
            if (car.Airborne)
            {
                if (s == 0) s = 1;
                if (s == 1 && AllWheelsUp(car, 0.5f)) { s = 2; JumpUp(car); }
            }
            else if (s != 0) { s = 0; JumpDown(car); }
            airState[car] = s;
        }

        static readonly RaycastHit[] rayHits = new RaycastHit[8];

        static bool AllWheelsUp(DriftCar car, float height)
        {
            var body = car.GetComponent<Rigidbody>();
            int counted = 0;
            foreach (var w in car.Wheels)
            {
                if (!w) continue;
                counted++;
                if (w.GetGroundHit(out _)) return false;
                int n = Physics.RaycastNonAlloc(w.transform.position, Vector3.down, rayHits, 30f, ~0, QueryTriggerInteraction.Ignore);
                float best = 30f;
                for (int i = 0; i < n; i++)
                    if (rayHits[i].collider.attachedRigidbody != body && rayHits[i].distance < best) best = rayHits[i].distance;
                if (best - w.suspensionDistance - w.radius <= height) return false;
            }
            return counted > 0;
        }

        /// <summary>
        /// A near miss being armed (0x4a6460 -> 0x6a11f0): effects\graze_l.nif / graze_r.nif on the side the traffic
        /// car passes, following the car's position and rotation; played once over the NIF's key range. Another graze
        /// on the same side while one is showing is not restarted.
        /// </summary>
        public static NifEffect Graze(DriftCar car, bool left)
        {
            if (!car) return null;
            string pool = left ? "graze_l" : "graze_r";
            if (Live(pool) > 0) return null;
            var fx = Attach(pool, car.transform, GrazeOffset(CarSize(car), left), true);
            if (fx) Track(pool, fx);
            return fx;
        }

        /// <summary>
        /// The graze position in the car's frame: Gamebryo (-/+0.6 * v[0x220], 0.4 * v[0x21c], 0.4 * v[0x224]) with
        /// the vehicle's width, length and height there (the vehiclelist length/width/height order) = Unity
        /// (-/+0.6 * width, 0.4 * height, 0.4 * length). <paramref name="size"/> = (width, height, length) in metres.
        /// </summary>
        public static Vector3 GrazeOffset(Vector3 size, bool left) =>
            new Vector3((left ? -0.6f : 0.6f) * size.x, 0.4f * size.y, 0.4f * size.z);

        /// <summary>Body width, height and length of a car, from the chassis box CarFactory.AddPhysics fits
        /// (90 % of the width, height - 0.4, 95 % of the length).</summary>
        public static Vector3 CarSize(DriftCar car)
        {
            var box = car ? car.GetComponent<BoxCollider>() : null;
            return box ? new Vector3(box.size.x / 0.9f, box.size.y + 0.4f, box.size.z / 0.95f) : new Vector3(1.8f, 1.4f, 4.4f);
        }

        /// <summary>Which side of <paramref name="car"/> a traffic car passes on (the client's side pick in 0x4a6460).</summary>
        public static bool PassesOnLeft(DriftCar car, Transform traffic)
        {
            var local = car.transform.InverseTransformPoint(traffic.position);
            return local.x < 0f;
        }

        /// <summary>Map object EffectIDs of a city: model id -> EffectID from &lt;city&gt;\modeltable.nxt (column 3,
        /// "이펙트ID"); objects with EffectID 0 are left out.</summary>
        public static Dictionary<int, int> PropEffectIds(string city)
        {
            var map = new Dictionary<int, int>();
            var fs = DriftCityData.Files;
            string path = $@"{city}\modeltable.nxt";
            if (fs == null || !fs.Exists(path)) return map;
            string text = Core.Cp949.Decode(fs.Read(path));   // only the numeric columns are read
            foreach (var line in text.Split('\n'))
            {
                var c = line.TrimEnd('\r').Split('\t');
                if (c.Length < 3 || !int.TryParse(c[0], out int id) || !int.TryParse(c[2], out int fx) || fx == 0) continue;
                map[id] = fx;
            }
            return map;
        }

        /// <summary>Makes a placed map object breakable when its EffectID has an explosion NIF (palm trees, hydrants):
        /// returns the added <see cref="PropHitZone"/> or null.</summary>
        public static PropHitZone MakeBreakable(GameObject prop, int effectId)
        {
            if (!prop || PropEffect(effectId) == null) return null;
            // the trigger lives on a child in Ignore Raycast so road, camera and wheel queries don't see it
            var go = new GameObject("Hit Zone") { layer = 2 };
            go.transform.SetParent(prop.transform, false);
            var zone = go.AddComponent<PropHitZone>();
            zone.EffectId = effectId;
            return zone;
        }

        /// <summary>Removes every playing effect (travel, logout, tests).</summary>
        public static void Clear()
        {
            foreach (var list in pools.Values) foreach (var fx in list) if (fx) fx.End();
            pools.Clear();
            screenNext.Clear();
            foreach (var fx in new List<NifEffect>(screenPlaying.Values)) if (fx) { fx.Ended = null; fx.End(); }
            screenPlaying.Clear();
            airState.Clear();
            if (screenCamera) screenCamera.enabled = false;
        }
    }

    /// <summary>
    /// A breakable map object (modeltable EffectID 11/12/13 palm trees, 2 fire hydrant): a trigger around its foot.
    /// A car driving into it plays <see cref="GameEffects.PropHit"/> at the object's placement point, and the object is
    /// out for 10 s, the client's reset timer (0x4035f0 sets obj+0xc = 10, 0x403fc0 puts the body back) [inferred:
    /// the client knocks the object over as a physics body; here it is hidden for that time].
    /// </summary>
    public sealed class PropHitZone : MonoBehaviour
    {
        public int EffectId;
        public const float ResetSeconds = 10f;
        float downFor;
        Renderer[] renderers;

        void Start()
        {
            var prop = transform.parent ? transform.parent : transform;
            renderers = prop.GetComponentsInChildren<Renderer>();
            // trunk / body sized: the client collides with the object's own geometry
            var col = gameObject.AddComponent<CapsuleCollider>();
            col.isTrigger = true;
            float scale = Mathf.Max(0.01f, transform.lossyScale.y), height = 3f;
            if (renderers.Length > 0)
            {
                var b = renderers[0].bounds;
                foreach (var r in renderers) b.Encapsulate(r.bounds);
                height = Mathf.Clamp(b.size.y, 0.6f, 4f);
            }
            col.radius = (EffectId == 2 ? 0.3f : 0.45f) / scale;
            col.height = height / scale;
            col.center = new Vector3(0f, col.height * 0.5f, 0f);
        }

        void OnTriggerEnter(Collider other)
        {
            if (downFor > 0f || !other.attachedRigidbody || !other.attachedRigidbody.GetComponent<DriftCar>()) return;
            GameEffects.PropHit(EffectId, (transform.parent ? transform.parent : transform).position);
            downFor = ResetSeconds;
            foreach (var r in renderers) if (r) r.enabled = false;
        }

        void Update()
        {
            if (downFor <= 0f) return;
            downFor -= Time.deltaTime;
            if (downFor <= 0f) foreach (var r in renderers) if (r) r.enabled = true;
        }
    }
}
