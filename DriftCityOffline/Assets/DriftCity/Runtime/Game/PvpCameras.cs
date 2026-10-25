using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DriftCity.Core;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// The PvP stage's scripted and watch cameras (init\camera.ini, DriftCity_RE/PVP.md "Unity port"): "pvpready"
    /// (camera23, watch/program, fov 60: the grid dolly of 0x75ae60), "pvpstart" (camera9, effect/animation, fov 45: the
    /// track folder's camera.nif played forward, then backwards at 7x, 0x4dc500), "pvpgoalin" (camera21: the static
    /// camera of init\camera_gi.dat at the finish, 0x4d9390), and the spectator views (0x756e60): the auto director
    /// ("spotwatch" camera20 near the track's spotcam.nxt points, else a random "externalcam" camera31 preset), the
    /// externalcam presets and the first-person "frontview" (camera14). FOVs in camera.ini are horizontal (4:3): the
    /// loader stores 2 atan(0.75 tan(fov / 2)) (0x4da210).
    /// While a mode runs the rig owns Camera.main and the chase camera is switched off.
    /// </summary>
    public sealed class PvpCameraRig : MonoBehaviour
    {
        public enum Mode { None, Ready, Start, GoalIn, Watch }
        public Mode Current { get; private set; }

        public static PvpCameraRig Instance { get; private set; }

        Camera cam;
        ChaseCamera chase;
        float savedFov;
        bool owns;

        // pvpready
        RaceTrack track;
        int slotA, slotB;
        float dist;
        bool reverse;
        // pvpstart
        GameObject startNif;
        Transform startNode;
        List<NifAnimator.Channel> channels;
        float startLength, startTime;
        bool backwards;
        public bool StartDone { get; private set; }
        // pvpgoalin
        Vector3 giPos, giTarget;
        float giUntil;
        Mode afterGoal;
        // watch
        public Transform WatchTarget;
        public int View { get; private set; }
        int autoPreset = 3;
        float autoSwitch;
        List<Vector3> spots = new List<Vector3>();

        /// <summary>Vertical FOV of a camera.ini (horizontal, 4:3) angle.</summary>
        public static float VerticalFov(float horizontal) => 2f * Mathf.Atan(0.75f * Mathf.Tan(horizontal * 0.5f * Mathf.Deg2Rad)) * Mathf.Rad2Deg;

        public static PvpCameraRig Ensure()
        {
            if (Instance) return Instance;
            Instance = new GameObject("PvP Cameras").AddComponent<PvpCameraRig>();
            return Instance;
        }

        void OnDestroy() { Release(); if (Instance == this) Instance = null; DestroyStart(); }

        void Take()
        {
            cam = Camera.main;
            if (!cam) return;
            if (!owns) { savedFov = cam.fieldOfView; owns = true; }
            chase = cam.GetComponent<ChaseCamera>();
            if (chase) chase.enabled = false;
        }

        /// <summary>Gives the camera back to the chase camera.</summary>
        public void Release()
        {
            Current = Mode.None;
            if (!owns) return;
            owns = false;
            if (cam) cam.fieldOfView = savedFov > 1f ? savedFov : 60f;
            if (chase) chase.enabled = true;
        }

        // ---------------- pvpready (0x75ae60) ----------------
        /// <summary>
        /// The grid dolly: two grid slots A -> B (14 -> 15, or 6 -> 7 in a room of up to 8), the camera 7 m up and 7 m out on
        /// both axes of the start line from A, moving 3 m/s along the pair and looking at the grid point that moves with it;
        /// at the end of the pair it turns round.
        /// </summary>
        public void BeginReady(RaceTrack t, int drivers)
        {
            Take();
            track = t; dist = 0f; reverse = false;
            if (drivers <= 8) { slotA = 6; slotB = 7; } else { slotA = 14; slotB = 15; }
            Current = Mode.Ready;
            if (cam) cam.fieldOfView = VerticalFov(60f);
        }

        void UpdateReady(float dt)
        {
            if (track == null || !cam) return;
            Vector3 a = RaceMission.Ground(RaceMission.GridSlot(track, reverse ? slotB : slotA));
            Vector3 b = RaceMission.Ground(RaceMission.GridSlot(track, reverse ? slotA : slotB));
            var along = b - a; along.y = 0;
            float span = Mathf.Max(1f, along.magnitude);
            var dir = along / span;
            dist += 3f * dt;
            if (dist > span) { dist = 0f; reverse = !reverse; }
            // the start line's axes: across the track (the pair's direction) and back down the grid
            var fwd = TrackForward(track);
            var back = -fwd;
            var point = a + dir * dist;
            var pos = point + (dir * (reverse ? -1f : 1f) + back) * 7f + Vector3.up * 7f;
            cam.transform.position = pos;
            cam.transform.rotation = Quaternion.LookRotation(point + Vector3.up - pos, Vector3.up);
        }

        internal static Vector3 TrackForward(RaceTrack t)
        {
            var d = t.Line[Mathf.Min(3, t.Line.Count - 1)] - t.Line[0]; d.y = 0;
            return d.sqrMagnitude > 1e-4f ? d.normalized : Vector3.forward;
        }

        // ---------------- pvpstart (camera.nif, 0x4d75c0 / 0x4dc500) ----------------
        /// <summary>Plays the track's camera.nif; false when the track has none (the grid dolly then goes on).</summary>
        public bool BeginStart(RaceTrack t)
        {
            DestroyStart();
            StartDone = false;
            if (t == null) return false;
            string path = t.Dir + @"\camera.nif";
            if (!DriftCityData.Files.Exists(path)) return false;
            try { startNif = new NifSceneBuilder { LogErrors = false }.Load(path); }
            catch (Exception e) { Debug.LogWarning("Drift City: " + path + ": " + e.Message); return false; }
            if (!startNif) return false;
            startNif.name = "pvpstart " + t.Dir;
            foreach (var r in startNif.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
            channels = new List<NifAnimator.Channel>();
            foreach (var an in startNif.GetComponentsInChildren<NifAnimator>(true)) { an.enabled = false; channels.AddRange(an.Channels); }
            var moving = channels.OfType<NifAnimator.TransformChannel>().Where(c => c.Target).ToList();
            // the camera node (Cam01 / Cam03): the animated node named "Cam..", else the first animated node
            startNode = moving.Select(c => c.Target).FirstOrDefault(x => x.name.StartsWith("Cam", StringComparison.OrdinalIgnoreCase))
                        ?? startNif.GetComponentsInChildren<Transform>(true).FirstOrDefault(x => x.name.StartsWith("Cam", StringComparison.OrdinalIgnoreCase))
                        ?? moving.Select(c => c.Target).FirstOrDefault();
            startLength = channels.Count > 0 ? channels.Max(c => c.Stop) : 0f;
            if (!startNode || startLength <= 0.1f) { DestroyStart(); return false; }
            Take();
            startTime = 0f; backwards = false;
            Current = Mode.Start;
            if (cam) cam.fieldOfView = VerticalFov(45f);
            ApplyStart();
            return true;
        }

        /// <summary>Seconds the pvpstart camera runs: forward once, then back at 7x.</summary>
        public float StartSeconds => startLength * (1f + 1f / 7f);

        void UpdateStart(float dt)
        {
            if (!startNode) { StartDone = true; return; }
            if (!backwards) { startTime += dt; if (startTime >= startLength) { startTime = startLength; backwards = true; } }
            else { startTime -= 7f * dt; if (startTime <= 0f) { startTime = 0f; StartDone = true; } }
            ApplyStart();
        }

        void ApplyStart()
        {
            if (channels == null || !cam) return;
            foreach (var c in channels) c.Apply(startTime);
            // the exported camera node looks down its local -X axis with Y up (checked against the files: at t = 0 the -X
            // axis of every track's camera.nif points straight at the start line's centre); Unity: -right, forward as up
            cam.transform.position = startNode.position;
            cam.transform.rotation = Quaternion.LookRotation(-startNode.right, startNode.forward);
        }

        void DestroyStart()
        {
            if (startNif) Destroy(startNif);
            startNif = null; startNode = null; channels = null;
        }

        // ---------------- pvpgoalin (camera_gi.dat, 0x6bf490 / 0x4d9390) ----------------
        static Dictionary<string, (Vector3 pos, Vector3 target)> goalCams;

        /// <summary>camera_gi.dat key: M / K / C / O / T + "_" + the track folder ("M_track1").</summary>
        public static bool GoalCamera(RaceTrack t, out Vector3 pos, out Vector3 target)
        {
            pos = target = Vector3.zero;
            if (t == null) return false;
            if (goalCams == null)
            {
                goalCams = new Dictionary<string, (Vector3, Vector3)>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var text = Cp949.Decode(DriftCityData.Files.Read(@"init\camera_gi.dat"));
                    foreach (var line in text.Split('\n'))
                    {
                        var f = line.Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                        if (f.Length < 7) continue;
                        var v = new float[6];
                        bool ok = true;
                        for (int i = 0; i < 6; i++) ok &= float.TryParse(f[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]);
                        if (ok) goalCams[f[0]] = (new Vector3(v[0], v[2], v[1]), new Vector3(v[3], v[5], v[4]));
                    }
                }
                catch (Exception e) { Debug.LogWarning("Drift City: camera_gi.dat: " + e.Message); }
            }
            string prefix = t.City == "moonpalace" ? "M" : t.City == "koinonia" ? "K" : t.City == "cras" ? "C" : t.City == "oros" ? "O" : "T";
            if (!goalCams.TryGetValue(prefix + "_" + t.Track, out var g)) return false;
            pos = g.pos; target = g.target;
            return true;
        }

        /// <summary>The finish camera for a few seconds, then back to what ran before (watch views) or the chase camera.</summary>
        public bool BeginGoalIn(RaceTrack t, float seconds)
        {
            if (!GoalCamera(t, out giPos, out giTarget)) return false;
            if (Current != Mode.GoalIn) afterGoal = Current;
            Take();
            Current = Mode.GoalIn;
            giUntil = Time.time + seconds;
            if (cam) cam.fieldOfView = VerticalFov(60f);
            return true;
        }

        // ---------------- spectator views (0x756e60) ----------------
        /// <summary>View names of VIEWING 1960000's TABKEY order: NORMAL (auto), TPVIEW, TOPVIEW, QUARTER, SIDE, FPVIEW.</summary>
        public static readonly string[] Views = { "NORMAL", "TPVIEW", "TOPVIEW", "QUARTER", "SIDE", "FPVIEW" };
        /// <summary>externalcam presets (F look-ahead, N distance, H height; negative N = in front looking back), m1..m7.</summary>
        static readonly Vector3[] External =
        {
            new Vector3(30f, 5.5f, 1.85f), new Vector3(30f, 5.5f, 1.85f), new Vector3(10f, 8f, 3f), new Vector3(20f, 16f, 6f),
            new Vector3(-20f, -16f, 6f), new Vector3(30f, 3f, 2f), new Vector3(-30f, -4f, 2f),
        };

        public void BeginWatch(Transform target, RaceTrack t)
        {
            Take();
            WatchTarget = target;
            Current = Mode.Watch;
            spots.Clear();
            if (t != null)
            {
                try
                {
                    string path = t.Dir + @"\spotcam.nxt";
                    if (DriftCityData.Files.Exists(path))
                        foreach (var line in Cp949.Decode(DriftCityData.Files.Read(path)).Split('\n'))
                        {
                            var f = line.Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                            if (f.Length >= 4 && float.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                                float.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                                float.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                                spots.Add(new Vector3(x, z, y));
                        }
                }
                catch (Exception e) { Debug.LogWarning("Drift City: spotcam.nxt: " + e.Message); }
            }
        }

        /// <summary>Left / right (0x7b / 0x7c): the next or previous view, wrapping 0..5.</summary>
        public void CycleView(int dir) { View = ((View + dir) % Views.Length + Views.Length) % Views.Length; autoSwitch = 0f; }
        public void SetView(int v) { View = Mathf.Clamp(v, 0, Views.Length - 1); autoSwitch = 0f; }

        void UpdateWatch(float dt)
        {
            if (!cam || !WatchTarget) return;
            var t = WatchTarget;
            var fwd = t.forward; fwd.y = 0;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            if (View == 5)
            {
                // frontview (camera14): on the bonnet looking ahead
                cam.fieldOfView = VerticalFov(45f);
                cam.transform.position = t.position + Vector3.up * 1.3f + fwd * 0.6f;
                cam.transform.rotation = Quaternion.LookRotation(fwd, Vector3.up);
                return;
            }
            int preset;
            if (View == 0)
            {
                // the director: spotwatch when the car is within 80 m of a spotcam point, else a random externalcam preset
                var spot = spots.Where(s => (s - t.position).sqrMagnitude < 80f * 80f).OrderBy(s => (s - t.position).sqrMagnitude).Select(s => (Vector3?)s).FirstOrDefault();
                if (spot.HasValue)
                {
                    cam.fieldOfView = VerticalFov(60f);
                    cam.transform.position = spot.Value + Vector3.up * 2f;
                    cam.transform.rotation = Quaternion.LookRotation(t.position + Vector3.up - cam.transform.position, Vector3.up);
                    return;
                }
                autoSwitch -= dt;
                if (autoSwitch <= 0f)
                {
                    int next;
                    do next = UnityEngine.Random.Range(1, 7); while (next == autoPreset);
                    autoPreset = next; autoSwitch = 6f;
                }
                preset = autoPreset;
            }
            else preset = View == 1 ? 3 : View == 2 ? 2 : View == 3 ? 1 : 4;   // TPVIEW m4, TOPVIEW m3, QUARTER m2, SIDE m5
            var p = External[Mathf.Clamp(preset, 0, External.Length - 1)];
            cam.fieldOfView = VerticalFov(60f);
            var desired = t.position - fwd * p.y + Vector3.up * p.z;
            cam.transform.position = Vector3.Lerp(cam.transform.position, desired, 1f - Mathf.Exp(-6f * dt));
            var look = t.position + fwd * (Mathf.Sign(p.y) * Mathf.Min(Mathf.Abs(p.x), 12f) * 0.1f) + Vector3.up;
            cam.transform.rotation = Quaternion.LookRotation(look - cam.transform.position, Vector3.up);
        }

        void LateUpdate()
        {
            float dt = Time.deltaTime;
            switch (Current)
            {
                case Mode.Ready: UpdateReady(dt); break;
                case Mode.Start: UpdateStart(dt); break;
                case Mode.Watch: UpdateWatch(dt); break;
                case Mode.GoalIn:
                    if (cam)
                    {
                        cam.transform.position = giPos;
                        cam.transform.rotation = Quaternion.LookRotation(giTarget - giPos, Vector3.up);
                    }
                    if (Time.time >= giUntil)
                    {
                        if (afterGoal == Mode.Watch && WatchTarget) Current = Mode.Watch; else Release();
                    }
                    break;
            }
        }
    }
}
