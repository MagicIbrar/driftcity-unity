using System.Collections.Generic;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// Mission-battle rooms (room flag 0x10000; DriftCity_RE/PVP.md 14.6 / 15.1). The client: rows with 0x10000 can't be joined
    /// from the list; WAIT16 MISSIONBATTLE_TEXT 11479 ("fixed stats"); 11480 refuses a driver in a party; the GM chat
    /// command "/missionbattle %d %d" (0x5b8200) switches the effect manager's object set (+0x1c: on flag +0x54, kind +0x58:
    /// 1 UFO, 2 Ring, 3 Rock, else plain "On") and prints "MissionBattle Off / On / On(UFO) / On(Ring) / On(Rock)". The
    /// object loader (0x737b97..0x7382ee) builds game/pvp/t_ob_00012, t_ob_event_01..03, effects/undercity/ufo.nif,
    /// traffic_check_light_b/r, traffic_rock and traffic_meteor objects with a kind (+0x18), a weight (+0x1c) and a
    /// once-only flag (+0x24 2 / 3); the hit (0x734a00): weight > 0 slows the car by weight x k, kind 1 plays the object's
    /// animation, kind 4 plays UI effect 50 t_ob_event_04 (the ink splats), knocks the object over and puts the car in a
    /// 2 s state (vehicle +0x590 |= 1, +0x594 = 2.0).
    ///
    /// Where the objects stand and what they score was the server's mission. The remake's rule ([remake]): objects every
    /// 90 m of the racing line from 150 m after the start, alternating left / centre / right (3 m), the same on every
    /// client (the line decides). UFO and Ring objects are collected (+25 % booster gauge, the object is gone for the
    /// lap); Rocks and the ink objects (t_ob_event_03, kind 4: every 4th object in the mixed set) are hit: t_ob_event_04,
    /// the 2 s state (speed x 0.6 at the hit, no booster) and the object falls over. The HUD line counts collected / hits;
    /// everyone races the fixed stats (the Real Match Duel's ratings).
    /// </summary>
    public static class PvpMissionBattle
    {
        public const int Off = 0, On = 1, Ufo = 2, Ring = 3, Rock = 4;
        public const float Spacing = 90f, FirstAt = 150f, Reach = 3.5f, InkSeconds = 2f;
        public static readonly string[] ModeText = { "MissionBattle Off", "MissionBattle On", "MissionBattle On(UFO)", "MissionBattle On(Ring)", "MissionBattle On(Rock)" };

        /// <summary>The GM toggle (effect manager +0x1c). Rooms created while it is on are mission-battle rooms.</summary>
        public static int Mode;
        public static int Collected { get; private set; }
        public static int Hits { get; private set; }
        public static int ObjectCount => run ? run.Objects.Count : 0;
        public static MissionBattleRun Run => run;
        public static string LastText { get; private set; }
        static MissionBattleRun run;

        public static (int accel, int speed, int crash, int boost) FixedStats => Pvp.RealMatchStats();

        /// <summary>
        /// "/missionbattle a b" (0x5b8200, the first 14 characters compared): a = 0 off, else on with kind b (1 UFO, 2 Ring,
        /// 3 Rock, else plain on). Offline it switches at once; online only an admin may (race.gm). True when the line was
        /// this command (ChatBox does not send it then).
        /// </summary>
        public static bool ChatCommand(string line)
        {
            line = (line ?? "").Trim();
            if (!line.StartsWith("/missionbattle", System.StringComparison.OrdinalIgnoreCase)) return false;
            var parts = line.Substring(14).Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
            int a = parts.Length > 0 && int.TryParse(parts[0], out var x) ? x : 0;
            int b = parts.Length > 1 && int.TryParse(parts[1], out var y) ? y : 0;
            int mode = a == 0 ? Off : b == 1 ? Ufo : b == 2 ? Ring : b == 3 ? Rock : On;
            if (OnlineSession.Active)
            {
                OnlineSession.Instance.Net.Request("race.gm", new Dictionary<string, object> { ["op"] = "missionbattle", ["mode"] = mode }, r =>
                {
                    if (r.B("ok")) Set(mode); else ChatBox.System(MissionText.Get("10745"));
                });
                return true;
            }
            Set(mode);
            return true;
        }

        public static void Set(int mode)
        {
            Mode = Mathf.Clamp(mode, Off, Rock);
            LastText = ModeText[Mode];
            ChatBox.System(LastText);
        }

        /// <summary>11480 "You cannot perform Mission Battles while in a Party." (0x5c69c0 / 0x5d52d0); null when allowed.</summary>
        public static string Refusal(PvpRoom room) => room != null && room.Mission > 0 && Social.PartyWindow.InParty ? MissionText.Get("11480") : null;

        /// <summary>The objects of a mission kind, in placement order (kind, nif).</summary>
        public static (int kind, string nif) ObjectFor(int mode, int i)
        {
            switch (mode)
            {
                case Ufo: return (1, @"effects\undercity\ufo.nif");
                case Ring: return (1, @"game\pvp\t_ob_00012.nif");
                case Rock: return (4, @"game\pvp\traffic_rock.nif");
                default:
                    // the plain "On" set: the event objects, every 4th one the ink object (kind 4)
                    if (i % 4 == 3) return (4, @"game\pvp\t_ob_event_03.nif");
                    return (1, i % 2 == 0 ? @"game\pvp\t_ob_event_01.nif" : @"game\pvp\t_ob_event_02.nif");
            }
        }

        /// <summary>Places the objects along the track's line for a race (local car = the one that collects / is hit).</summary>
        public static MissionBattleRun Begin(RaceTrack track, int mode, int seed, DriftCar car)
        {
            End();
            if (track == null || track.Line.Count < 20 || mode <= Off) return null;
            Collected = Hits = 0;
            run = new GameObject("Mission Battle").AddComponent<MissionBattleRun>();
            run.Build(track, mode, car);
            return run;
        }

        public static void End()
        {
            if (run) Object.Destroy(run.gameObject);
            run = null;
        }

        internal static void Count(bool collected) { if (collected) Collected++; else Hits++; }
    }

    /// <summary>The objects of one mission-battle race and the local car's pickups / hits.</summary>
    public sealed class MissionBattleRun : MonoBehaviour
    {
        public sealed class Obj
        {
            public GameObject Go;
            public int Kind;
            public Vector3 Pos;
            public int GoneLap = -1;   // collected objects come back next lap
            public bool Down;          // knocked-over objects stay down
        }

        public readonly List<Obj> Objects = new List<Obj>();
        public int Mode { get; private set; }
        DriftCar car;
        Rigidbody body;
        RaceTrack track;
        float inkLeft, savedBoost = -1f;
        int lap, lastIndex;
        UnityEngine.UI.Text label;

        internal void Build(RaceTrack t, int mode, DriftCar c)
        {
            track = t; Mode = mode; car = c; body = c ? c.GetComponent<Rigidbody>() : null;
            var line = t.Line;
            float acc = 0f, next = PvpMissionBattle.FirstAt;
            int i = 0;
            for (int k = 1; k < line.Count; k++)
            {
                acc += Vector3.Distance(line[k - 1], line[k]);
                if (acc < next) continue;
                if (acc > t.Length - 40f) break;   // nothing on the start / finish straight
                next += PvpMissionBattle.Spacing;
                var dir = line[(k + 1) % line.Count] - line[k - 1]; dir.y = 0;
                if (dir.sqrMagnitude < 1e-4f) continue;
                dir.Normalize();
                var right = Vector3.Cross(Vector3.up, dir);
                float side = (i % 3 - 1) * 3f;
                var (kind, nif) = PvpMissionBattle.ObjectFor(mode, i);
                var p = RaceMission.Ground(line[k] + right * side) - Vector3.up * 0.8f;
                Objects.Add(new Obj { Go = Make(nif, kind, p, dir), Kind = kind, Pos = p });
                i++;
            }
            if (c) BuildLabel();
        }

        GameObject Make(string nif, int kind, Vector3 p, Vector3 dir)
        {
            GameObject go = null;
            if (DriftCityData.Files != null && DriftCityData.Files.Exists(nif))
            {
                try { go = new NifSceneBuilder { LogErrors = false }.Load(nif, transform); } catch { go = null; }
            }
            if (!go)
            {
                // a stand-in when the object's NIF is missing from this install
                go = GameObject.CreatePrimitive(kind == 4 ? PrimitiveType.Cube : PrimitiveType.Sphere);
                Destroy(go.GetComponent<Collider>());
                go.transform.SetParent(transform, false);
                go.transform.localScale = Vector3.one * 1.5f;
            }
            foreach (var col in go.GetComponentsInChildren<Collider>()) col.enabled = false;
            go.transform.SetPositionAndRotation(p + Vector3.up * (Mode == PvpMissionBattle.Ufo ? 2.5f : 0f), Quaternion.LookRotation(dir, Vector3.up));
            return go;
        }

        void BuildLabel()
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            var rt = NuiView.NewRect("Mission Battle HUD", ui.Root);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f); rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0, -96); rt.sizeDelta = new Vector2(420, 28);
            label = rt.gameObject.AddComponent<UnityEngine.UI.Text>();
            label.font = UiAssets.Font("Verdana 16", out _, out _);
            label.fontSize = 16; label.fontStyle = FontStyle.Bold; label.alignment = TextAnchor.MiddleCenter; label.raycastTarget = false;
            label.color = new Color(1f, 0.85f, 0.2f);
            rt.gameObject.AddComponent<UnityEngine.UI.Outline>().effectColor = Color.black;
            Refresh();
        }

        void Refresh()
        {
            if (label) label.text = $"{PvpMissionBattle.ModeText[Mathf.Clamp(Mode, 0, 4)].Replace("MissionBattle", "Mission Battle")}   {PvpMissionBattle.Collected} / {Objects.FindAll(o => o.Kind != 4).Count}" + (PvpMissionBattle.Hits > 0 ? $"   Hits {PvpMissionBattle.Hits}" : "");
        }

        void Update()
        {
            foreach (var o in Objects)
            {
                if (!o.Go || o.Down || o.GoneLap >= 0) continue;
                if (Mode == PvpMissionBattle.Ufo) o.Go.transform.Rotate(0f, 90f * Time.deltaTime, 0f, Space.World);
            }
            if (!car) return;
            // laps: the line index wrapping back to the start brings collected objects back
            int idx = RaceMission.NearestOnLine(track.Line, car.transform.position, lastIndex);
            if (lastIndex > track.Line.Count * 0.8f && idx < track.Line.Count * 0.2f) { lap++; foreach (var o in Objects) if (o.GoneLap >= 0 && o.GoneLap < lap && o.Go) { o.GoneLap = -1; o.Go.SetActive(true); } }
            lastIndex = idx;
            var pos = car.transform.position;
            foreach (var o in Objects)
            {
                if (!o.Go || o.Down || o.GoneLap >= 0) continue;
                var d = o.Pos - pos; d.y = 0;
                if (d.sqrMagnitude > PvpMissionBattle.Reach * PvpMissionBattle.Reach) continue;
                Hit(o);
            }
            if (inkLeft > 0f)
            {
                inkLeft -= Time.deltaTime;
                if (savedBoost >= 0f) car.BoostGauge = Mathf.Min(car.BoostGauge, savedBoost);
                if (inkLeft <= 0f) savedBoost = -1f;
            }
        }

        /// <summary>0x734a00 for the local car: kind 4 = the ink hit, others are collected.</summary>
        public void Hit(Obj o)
        {
            if (o.Kind == 4)
            {
                PvpMissionBattle.Count(false);
                // UI effect 50 t_ob_event_04 and the 2 s state; the object falls over the way the car went
                GameEffects.Screen("t_ob_event_04");
                if (GameEffects.Sounds) AudioLibrary.PlaySfx("sfx/SID_UFOCRASH");
                if (inkLeft < 1f) { inkLeft = PvpMissionBattle.InkSeconds; savedBoost = car ? car.BoostGauge : -1f; }
                if (body) body.linearVelocity *= 0.6f;
                o.Down = true;
                var away = car ? car.transform.forward : Vector3.forward; away.y = 0;
                o.Go.transform.rotation = Quaternion.FromToRotation(Vector3.up, away.sqrMagnitude > 0.01f ? away.normalized : Vector3.forward) * o.Go.transform.rotation;
                o.Go.transform.position -= Vector3.up * 0.4f;
            }
            else
            {
                PvpMissionBattle.Count(true);
                if (GameEffects.Sounds) AudioLibrary.PlaySfx(Mode == PvpMissionBattle.Ufo ? "sfx/SID_UFO" : "sfx/SID_GETITEM");
                if (car) car.BoostGauge = Mathf.Min(1f, car.BoostGauge + 0.25f);
                o.GoneLap = lap;
                o.Go.SetActive(false);
            }
            Refresh();
        }

        void OnDestroy() { if (label) Destroy(label.gameObject); }
    }
}
