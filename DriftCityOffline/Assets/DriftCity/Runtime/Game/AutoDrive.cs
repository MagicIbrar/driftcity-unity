using System;
using System.Collections.Generic;
using DriftCity.Net;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// Auto-Drive (the "auto finder" of the code map; DriftCity_RE/CHASES.md section 5): an Auto-Drive item drives the car
    /// by itself to a place picked on the full map (11112 "Please use the "Go" button to initiate Auto-Drive.").
    /// <list type="bullet">
    /// <item>AUTODRIVING 550000 (0x59b370 / 0x59b3a0): BASE "driving" plate and STOP, which asks 11072 "Do you want to stop the
    /// Auto-Drive?" (0x59b2c0); arrival prints 11073 "Arrived at the destination.".</item>
    /// <item>Refusals (item use dispatcher 0x5d52d0): 11103 battle dome, 11104 battle zone, 11108 Undercity, 11109 / 11132
    /// Virtual HUV, 11117 a mission, 11118 a delivery (11119 Time Attack), 11120 a patrol, 11121 Rush Time, 11122 in a
    /// party, 11124 at a station; 11129 / 11130 party invitations are refused while it drives.</item>
    /// </list>
    /// The client's route follower is not traced; the remake plans the shortest way over the traffic lanes (Dijkstra on
    /// the .tcs lane graph) and steers the car along it (pure pursuit, 70 km/h on straights, slower in bends), giving the
    /// car back on arrival (within 25 m), on STOP or when it is stuck for 8 s. AUTOFINDER 3420000 is not this: it is the
    /// server's anti-macro quiz (0x681050 / 0x681150: a question with four answers and a 30 s clock, 10556 "Correct." /
    /// 10557 "Wrong.") and is not rebuilt.
    /// </summary>
    public sealed class AutoDrive : MonoBehaviour
    {
        public const int DialogId = 550000;
        public float CruiseKmh = 70f, ArriveRadius = 25f;

        public static AutoDrive Active { get; private set; }
        public Vector3 Destination { get; private set; }
        public List<Vector3> Path { get; private set; } = new List<Vector3>();
        public bool Arrived { get; private set; }

        DriftCar car;
        NuiView view;
        int index;
        float stuck;

        /// <summary>The refusal text (globaltext id) for starting now, or null when Auto-Drive may start.</summary>
        public static string Refusal()
        {
            var flow = GameFlow.Instance;
            if (MittronFuel.Empty) return "11116";
            if (UndercityStage.Active) return "11108";
            if (VirtualChase.Active) return VirtualChase.Active.State == VirtualChase.Phase.Running ? "11132" : "11131";
            if (RushTime.Joined) return "11121";
            if (DeliveryService.Active) return Factions.DeliveryText("11118");
            if (StepQuests.Running(PlayerProfile.Current) != null) return "11117";
            if (PatrolMission.Active) return "11120";
            if (ChaseMission.Active || RaceMission.Active || OnlineRace.Active || LapTimeBattle.Active) return "11117";
            if (Social.PartyWindow.InParty) return "11122";
            if (flow && flow.PlayerCar && CitySession.Current)
                foreach (var st in CityMapInfo.Stations(CitySession.Current.City))
                    if (MissionPosts.Flat(st.Position, flow.PlayerCar.transform.position) < 30f) return "11124";
            return null;
        }

        /// <summary>Starts driving to <paramref name="destination"/>; null with the refusal shown when it can't.</summary>
        public static AutoDrive Start(Vector3 destination)
        {
            var refuse = Refusal();
            if (refuse != null) { UiPopup.Message(MissionText.Get(refuse)); return null; }
            var session = CitySession.Current;
            var car = GameFlow.Instance ? GameFlow.Instance.PlayerCar : session ? session.Car : null;
            if (!car || !session || !session.Traffic || session.Traffic.Network == null) return null;
            var path = Plan(session.Traffic, car.transform.position, destination);
            if (path.Count < 2) { UiPopup.Message(MissionText.Get("11112")); return null; }
            if (Active) Active.Stop(false);
            var a = new GameObject("Auto-Drive").AddComponent<AutoDrive>();
            a.car = car; a.Destination = destination; a.Path = path;
            car.ExternalControl = true;
            Active = a;
            var ui = UiManager.Instance;
            a.view = ui ? ui.Open(DialogId) : null;
            if (a.view) ui.On("ID_AUTODRIVING_STOP", _ => MissionUi.Confirm(MissionText.Get("11072"), () => { if (Active) Active.Stop(false); }));
            return a;
        }

        /// <summary>The way from <paramref name="from"/> to <paramref name="to"/> over the lane graph (shortest by lane length).</summary>
        public static List<Vector3> Plan(TrafficSystem traffic, Vector3 from, Vector3 to)
        {
            var result = new List<Vector3>();
            var net = traffic.Network;
            if (!traffic.NearestLane(from, out int a, out float alongA, -1, 200f) || !traffic.NearestLane(to, out int b, out float alongB, -1, 300f)) return result;
            int n = net.Lanes.Count;
            var dist = new float[n]; var prev = new int[n];
            for (int i = 0; i < n; i++) { dist[i] = float.MaxValue; prev[i] = -1; }
            var open = new SortedSet<(float, int)>();
            dist[a] = 0; open.Add((0, a));
            while (open.Count > 0)
            {
                var (d, u) = open.Min; open.Remove(open.Min);
                if (u == b) break;
                if (d > dist[u]) continue;
                foreach (int v in net.Lanes[u].Next)
                {
                    float nd = d + Mathf.Max(1f, net.Lanes[v].Length);
                    if (nd < dist[v]) { if (dist[v] < float.MaxValue) open.Remove((dist[v], v)); dist[v] = nd; prev[v] = u; open.Add((nd, v)); }
                }
            }
            if (a != b && prev[b] < 0) return result;
            var lanes = new List<int>();
            for (int l = b; l >= 0; l = l == a ? -1 : prev[l]) lanes.Add(l);
            lanes.Reverse();
            for (int k = 0; k < lanes.Count; k++)
            {
                var pts = net.Lanes[lanes[k]].Points;
                float acc = 0;
                for (int i = 0; i < pts.Length; i++)
                {
                    var w = DriftCityData.ToUnity(pts[i]);
                    if (i > 0) acc += Vector3.Distance(DriftCityData.ToUnity(pts[i - 1]), w);
                    if (k == 0 && acc < alongA) continue;
                    if (k == lanes.Count - 1 && acc > alongB + 1f) break;
                    if (result.Count == 0 || (result[result.Count - 1] - w).sqrMagnitude > 1f) result.Add(w);
                }
            }
            return result;
        }

        void FixedUpdate()
        {
            if (!car || Arrived) return;
            car.ExternalControl = true;
            int count = Path.Count;
            var pos = car.transform.position;
            if (MissionPosts.Flat(pos, Destination) < ArriveRadius || (count > 0 && MissionPosts.Flat(pos, Path[count - 1]) < ArriveRadius && index >= count - 3))
            {
                Arrived = true;
                ChatBox.System(MissionText.Get("11073"));
                Stop(true);
                return;
            }
            for (int k = 0; k < 20 && index + 1 < count; k++)
                if ((Path[index + 1] - pos).sqrMagnitude < (Path[index] - pos).sqrMagnitude) index++; else break;
            float speed = car.SpeedKmh / 3.6f;
            float look = Mathf.Clamp(speed * 0.9f, 8f, 30f), acc = 0;
            var target = Path[Mathf.Min(index, count - 1)];
            for (int k = index + 1; k < count && acc < look; k++) { acc += Vector3.Distance(Path[k - 1], Path[k]); target = Path[k]; }
            var local = car.transform.InverseTransformPoint(target);
            float steer = Mathf.Clamp(Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg / car.MaxSteer, -1f, 1f);
            var d0 = (Path[Mathf.Min(index + 3, count - 1)] - Path[Mathf.Min(index, count - 1)]).normalized;
            var d1 = (Path[Mathf.Min(index + 15, count - 1)] - Path[Mathf.Min(index + 3, count - 1)]).normalized;
            float bend = d0.sqrMagnitude > 0 && d1.sqrMagnitude > 0 ? Vector3.Angle(d0, d1) : 0f;
            float targetKmh = Mathf.Lerp(CruiseKmh, 30f, Mathf.InverseLerp(10f, 90f, bend));
            float throttle = car.SpeedKmh < targetKmh ? 0.8f : car.SpeedKmh > targetKmh + 12f ? -0.6f : 0.1f;
            car.ApplyControls(throttle, steer, false, Time.fixedDeltaTime);
            stuck = car.SpeedKmh < 3f ? stuck + Time.fixedDeltaTime : 0f;
            if (stuck > 8f) Stop(false);
        }

        /// <summary>Gives the car back (STOP, arrival, stuck).</summary>
        public void Stop(bool arrived)
        {
            if (car) car.ExternalControl = false;
            var ui = UiManager.Instance;
            if (view && ui) ui.Close(view);
            view = null;
            if (Active == this) Active = null;
            Destroy(gameObject);
        }

        void OnDestroy()
        {
            if (Active == this) Active = null;
            if (car) car.ExternalControl = false;
            var ui = UiManager.Instance;
            if (view && ui) ui.Close(view);
        }
    }
}
