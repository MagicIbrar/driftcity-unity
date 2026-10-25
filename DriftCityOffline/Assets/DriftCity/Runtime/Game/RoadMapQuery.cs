using DriftCity.Core;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// The client's road map and surface zones for one city (DriftCity_RE/ROADS.md), loaded from the exact paths it
    /// uses: &lt;city&gt;\&lt;city&gt;.tcs roads (all open cities), &lt;city&gt;\main\total.hit (the AREACHECK map, taipei only,
    /// which then takes over), floor_m.hit attributes (only consulted with total.hit), and the flattened zones
    /// exclude.hit / sand.hit / water.hit (cras has all three, the others exclude.hit only).
    /// Positions are Unity world space; the data is Gamebryo (x, z, y).
    /// </summary>
    public sealed class RoadMapQuery
    {
        public static RoadMapQuery Current { get; private set; }

        public readonly string City;
        public RoadMap Areas;
        public HitMesh Floor;
        public RoadNetwork Roads;
        public HitMesh Exclude, Sand, Water;

        public bool HasRoads => Areas != null || (Roads != null && Roads.Roads.Count > 0);
        public bool HasSurfaces => Sand != null || Water != null;

        RoadMapQuery(string city) { City = city; }

        public static RoadMapQuery Load(string city)
        {
            var q = new RoadMapQuery(city);
            var fs = DriftCityData.Files;
            if (fs != null && !string.IsNullOrEmpty(city))
            {
                // exact paths only: GameFileSystem.Resolve would hand out another city's file of the same name
                byte[] Read(string p) { try { return fs.Exists(p) ? fs.Read(p) : null; } catch (System.Exception) { return null; } }
                var areas = Read($@"{city}\main\total.hit");
                if (areas != null)
                {
                    q.Areas = RoadMap.Parse(areas);
                    var floor = Read($@"{city}\main\floor_m.hit");
                    if (floor != null) q.Floor = HitMesh.Parse(floor);
                }
                var tcs = Read($@"{city}\{city}.tcs");
                if (tcs != null) q.Roads = RoadNetwork.Parse(tcs);
                HitMesh Zone(string name) { var d = Read($@"{city}\main\{name}.hit"); return d != null ? HitMesh.Parse(d, true) : null; }
                q.Exclude = Zone("exclude");
                q.Sand = Zone("sand");
                q.Water = Zone("water");
                Debug.Log($"Drift City: road map for {city}: {(q.Areas != null ? "AREACHECK " + q.Areas.Triangles.Length + " tris, " : "")}{q.Roads?.Roads.Count ?? 0} roads, zones{(q.Exclude != null ? " exclude" : "")}{(q.Sand != null ? " sand" : "")}{(q.Water != null ? " water" : "")}");
            }
            Current = q;
            return q;
        }

        public static RoadMapQuery For(string city) => Current != null && Current.City == city ? Current : Load(city);

        /// <summary>
        /// One evaluation of the car update's road test (0x485e64..0x486209), updating the persistent wrong-way /
        /// off-road flags (in[0x229] / in[0x22a]) and returning after how many physics steps the next one is due.
        /// </summary>
        public int Evaluate(Vector3 pos, Vector3 forward, ref bool wrongWay, ref bool offRoad)
        {
            float x = pos.x, y = pos.z, z = pos.y;
            var h = new Vector2(forward.x, forward.z);
            if (h.sqrMagnitude < 1e-8f) h = Vector2.up;
            h.Normalize();
            if (Areas != null)
            {
                // every step: AREACHECK triangle under the car; off its triangles the floor's w class decides
                wrongWay = true; offRoad = false;
                switch (Areas.Query(x, y, z, h.x, h.y, out _))
                {
                    case RoadMap.Along: wrongWay = false; break;
                    case RoadMap.Against: wrongWay = true; break;
                    case RoadMap.OffRoad3:
                    case RoadMap.OffRoad4: wrongWay = false; offRoad = true; break;
                    default:
                        int cls = 1;
                        if (Floor != null && Floor.RayDown(x, y, z + 5f, out _, out int a)) cls = a;
                        if (cls > 1 && cls < 4) { wrongWay = false; offRoad = true; }
                        break;
                }
                return 0;
            }
            if (Roads == null) return 10;
            var hit = Roads.Query(x, y, z, h.x, h.y);
            if (!hit.Found) { wrongWay = false; offRoad = true; }
            else if (hit.Junction || hit.Outside) offRoad = false;   // the wrong-way flag keeps its last value
            else
            {
                var road = Roads.Roads[hit.Road];
                wrongWay = !hit.RightSide && hit.Distance < road.CarriagewayHalf;
                offRoad = hit.Distance >= road.OffRoadDistance;
            }
            if (offRoad && InExclude(pos)) offRoad = false;
            return hit.Found && !hit.Junction && hit.Outside ? 5 : 10;
        }

        /// <summary>Stateless: is the car on the road by the client's rules (no junction memory)?</summary>
        public bool OnRoad(Vector3 pos, Vector3 forward)
        {
            bool wrong = false, off = false;
            Evaluate(pos, forward, ref wrong, ref off);
            return !off;
        }

        /// <summary>Stateless wrong-way test (in junctions and on unmeasured spots this says no).</summary>
        public bool WrongWay(Vector3 pos, Vector3 forward)
        {
            bool wrong = false, off = false;
            Evaluate(pos, forward, ref wrong, ref off);
            return wrong;
        }

        public bool InExclude(Vector3 pos) => Exclude != null && Exclude.Contains(pos.x, pos.z);
        public bool InSand(Vector3 pos) => Sand != null && Sand.Contains(pos.x, pos.z);
        public bool InWater(Vector3 pos) => Water != null && Water.Contains(pos.x, pos.z);
    }
}
