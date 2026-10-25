using UnityEngine;

namespace DriftCity.Game
{
    public enum RoadSurface { Road, Sand, Water }

    /// <summary>
    /// The car's road flags as the client keeps them in the car update (DriftCity_RE/ROADS.md): wrong way
    /// (in[0x229]), off road (in[0x22a]), on sand (in[0x22b], sand.hit) and in water (in[0x22c], water.hit).
    /// With a .tcs road map they are re-evaluated every 10 physics steps (5 after an unmeasured spot), with an
    /// AREACHECK map every step, both tested at the car's position. The per-wheel surface is an extra for effects.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CarRoadState : MonoBehaviour
    {
        public bool WrongWay { get; private set; }
        public bool OffRoad { get; private set; }
        public bool Sand { get; private set; }
        public bool Water { get; private set; }
        /// <summary>The client's speed cap trigger: above 150 km/h on these it drops the engine force and adds
        /// -5000 N (0x481cf5); DriftCar applies it.</summary>
        public bool SlowSurface => OffRoad || Sand || Water;
        public bool HasRoads => map != null && map.HasRoads;
        public RoadMapQuery Map => map;
        public readonly RoadSurface[] Wheels = new RoadSurface[4];

        DriftCar car;
        RoadMapQuery map;
        int wait;

        void Start() => car = GetComponent<DriftCar>();

        void FixedUpdate()
        {
            var session = CitySession.Current;
            string city = session ? session.City : null;
            if (string.IsNullOrEmpty(city)) { Clear(); return; }
            if (map == null || map.City != city) { map = RoadMapQuery.For(city); wait = 0; Clear(); }
            if (wait > 0) { wait--; return; }

            var pos = transform.position;
            bool wrong = WrongWay, off = OffRoad;
            wait = map.Evaluate(pos, transform.forward, ref wrong, ref off);
            WrongWay = wrong; OffRoad = off;
            // the zones are only read on the .tcs path (the AREACHECK path leaves them alone)
            if (map.Areas == null)
            {
                Sand = map.InSand(pos);
                Water = map.InWater(pos);
            }
            for (int i = 0; i < 4; i++)
            {
                var w = car && car.WheelVisuals[i] ? car.WheelVisuals[i].position : pos;
                Wheels[i] = SurfaceAt(w);
            }
        }

        public RoadSurface SurfaceAt(Vector3 p) =>
            map == null ? RoadSurface.Road : map.InSand(p) ? RoadSurface.Sand : map.InWater(p) ? RoadSurface.Water : RoadSurface.Road;

        void Clear()
        {
            WrongWay = OffRoad = Sand = Water = false;
            for (int i = 0; i < 4; i++) Wheels[i] = RoadSurface.Road;
        }
    }
}
