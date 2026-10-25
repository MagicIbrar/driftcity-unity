using System.Collections.Generic;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// Oros' desert ambience (0x6a6d20, run while the city number is 4 and the stage is free roam 0xE; models loaded by
    /// 0x6af9e0: +0x114 bind.nif looped, +0x140 bind_hit.nif once, +0x16c / +0x184 tornado.nif / tornado02.nif looped).
    /// - Dust devils (bind.nif): every 5 s while fewer than 3 are up, 150 m + half the car's speed ahead of the camera,
    ///   then rand(-20, 20) m on each world axis; a random turn and a scale of 0.7..1.7. Each one stands on the ground
    ///   raised by its radius 0.63 x scale (record +0x2c) and owns a collision sphere of that radius (0x697af0 puts it in
    ///   the effect manager's collision space +0x1198, an octree of depth 7 over 20000 x 20000 x 2000; 0x697b30 moves it
    ///   every frame). 0x6a4e50 collides the local car's geoms with that space every frame; a touch (0x6a4db0 ->
    ///   0x6a3e50) plays bind_hit.nif once at the dust devil and marks it hit (+0x34), and a hit dust devil is removed on
    ///   the next frame. Otherwise it goes once it is 3 s old and 250 m from the camera.
    /// - Tornadoes: every 15 s when none is up, 170 m ahead and rand(-30, 30) m on each axis, placed on the ground by a
    ///   pick straight down, tornado.nif or tornado02.nif at random, a random turn and a scale of 0.7..1.7; removed at the
    ///   end of their animation.
    /// No sound is attached to either (the earlier "3D sound" reading of 0x697af0 was the collision sphere).
    /// </summary>
    public sealed class OrosAmbience : MonoBehaviour
    {
        public const float DevilEvery = 5f, TornadoEvery = 15f, DevilAhead = 150f, TornadoAhead = 170f, DevilSpread = 20f, TornadoSpread = 30f;
        public const float RadiusPerScale = 0.63f, MinScale = 0.7f, MaxScale = 1.7f;
        public const int MaxDevils = 3;

        public sealed class Devil
        {
            public NifEffect Fx;
            public float Born, Radius;
            public Vector3 Ground;
            public bool Hit;
        }

        readonly List<Devil> devils = new List<Devil>();
        NifEffect tornado;
        float devilClock, tornadoClock;

        public IReadOnlyList<Devil> Devils => devils;
        /// <summary>Dust devils hit by the player's car so far (tests).</summary>
        public static int HitCount { get; private set; }

        public static OrosAmbience Attach(GameObject host, string city)
        {
            if (city != "oros" || !host) return null;
            return host.AddComponent<OrosAmbience>();
        }

        void Update()
        {
            var flow = GameFlow.Instance;
            var car = flow ? flow.PlayerCar : null;
            var cam = Camera.main;
            // free roam only: races, missions, the Undercity and menus run other stages in the client
            if (!car || !cam || GameFlow.MissionActive || flow.InParkingLot) return;
            Tick(car, cam.transform.position, cam.transform.forward, Time.deltaTime);
        }

        /// <summary>One frame of 0x6a6d20 (tests call it directly).</summary>
        public void Tick(DriftCar car, Vector3 eye, Vector3 forward, float dt)
        {
            devilClock += dt;
            tornadoClock += dt;
            devils.RemoveAll(d => !d.Fx);
            Vector3 fwd = forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) return;
            fwd.Normalize();
            // the collision pass first (0x6a4e50 runs before the update), then spawns and removals
            if (car) Collide(car);
            if (devilClock > DevilEvery)
            {
                devilClock = 0f;
                if (devils.Count < MaxDevils) SpawnDevil(eye + fwd * (DevilAhead + (car ? car.SpeedKmh : 0f) * 0.5f));
            }
            if (tornadoClock > TornadoEvery)
            {
                tornadoClock = 0f;
                if (!tornado) tornado = SpawnTornado(eye + fwd * TornadoAhead);
            }
            for (int i = devils.Count - 1; i >= 0; i--)
            {
                var d = devils[i];
                if (d.Hit || (Time.time - d.Born >= 3f && (d.Fx.transform.position - eye).magnitude >= 250f))
                {
                    d.Fx.End();
                    devils.RemoveAt(i);
                }
            }
        }

        /// <summary>0x6a4e50 -> 0x6a3e50: the car's body touching a dust devil's sphere plays bind_hit there once.</summary>
        void Collide(DriftCar car)
        {
            var box = car.GetComponent<BoxCollider>();
            foreach (var d in devils)
            {
                if (d.Hit || !d.Fx) continue;
                var centre = d.Fx.transform.position;
                Vector3 closest = box && box.enabled ? box.ClosestPoint(centre) : car.transform.position;
                if ((closest - centre).sqrMagnitude > d.Radius * d.Radius) continue;
                d.Hit = true;
                HitCount++;
                GameEffects.At("bind_hit", centre);
            }
        }

        /// <summary>A dust devil at <paramref name="ahead"/> plus the random world offset; null when there is no ground.</summary>
        public Devil SpawnDevil(Vector3 ahead, bool scatter = true)
        {
            var p = scatter ? ahead + new Vector3(Random.Range(-DevilSpread, DevilSpread), 0f, Random.Range(-DevilSpread, DevilSpread)) : ahead;
            if (!GroundAt(p, out var ground)) return null;
            float scale = Random.Range(MinScale, MaxScale);
            float radius = scale * RadiusPerScale;
            var fx = GameEffects.At("bind", ground + Vector3.up * radius);
            if (!fx) return null;
            fx.Duration = 0f;   // looped: removed by the rules above
            fx.transform.rotation = Quaternion.Euler(0f, Random.Range(-180f, 180f), 0f);
            fx.transform.localScale = Vector3.one * scale;
            var d = new Devil { Fx = fx, Born = Time.time, Radius = radius, Ground = ground };
            devils.Add(d);
            return d;
        }

        NifEffect SpawnTornado(Vector3 ahead)
        {
            var p = ahead + new Vector3(Random.Range(-TornadoSpread, TornadoSpread), 0f, Random.Range(-TornadoSpread, TornadoSpread));
            if (!GroundAt(p, out var ground)) return null;
            var fx = GameEffects.At(Random.value < 0.5f ? "tornado" : "tornado02", ground);
            if (!fx) return null;
            fx.transform.rotation = Quaternion.Euler(0f, Random.Range(-180f, 180f), 0f);
            fx.transform.localScale = Vector3.one * Random.Range(MinScale, MaxScale);
            return fx;
        }

        static bool GroundAt(Vector3 p, out Vector3 ground)
        {
            ground = p;
            if (!Physics.Raycast(new Vector3(p.x, p.y + 300f, p.z), Vector3.down, out var hit, 800f, ~0, QueryTriggerInteraction.Ignore)) return false;
            ground = hit.point;
            return true;
        }

        void OnDestroy()
        {
            foreach (var d in devils) if (d.Fx) d.Fx.End();
            devils.Clear();
            if (tornado) tornado.End();
        }
    }
}
