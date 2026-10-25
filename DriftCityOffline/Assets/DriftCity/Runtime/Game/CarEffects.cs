using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Random = UnityEngine.Random;

namespace DriftCity.Game
{
    /// <summary>
    /// The original client's driving effects, rebuilt from its effect managers (DriftCity_RE/EFFECTS.md):
    /// rear-wheel skid marks (effects/skidmark.dds) with the white "skid flame" streaks (skidflame.dds), tyre smoke
    /// (tiresmoke.dds) from hard cornering or hard acceleration/braking, collision sparks (sparkle.dds), the
    /// effects\crash0N_front/side/block.nif particle bursts, the damage_smoke.nif engine smoke and the sand / water
    /// splash (sand.dds / water_tm.dds) while CarRoadState reports a sand.hit / water.hit zone (ROADS.md).
    /// Reads the car only through DriftCar's public state, its Rigidbody and the wheel visuals, so it does not
    /// depend on the handling model behind them.
    /// </summary>
    public sealed class CarEffects : MonoBehaviour
    {
        [Header("Tyre smoke triggers (client constants, m/s^2)")]
        public float LateralSmokeAccel = 18f;
        public float LateralSmokeMax = 30f;
        public float LongSmokeAccel = 10f;
        public float LongSmokeMax = 30f;
        [Tooltip("Durability lost, 0..1: the engine smokes above 0.6 like the original.")]
        public float Damage01;
        [Tooltip("The client's second boost state: double-width black marks instead of skid flames.")]
        public bool HeavyBoost;
        [Tooltip("The client draws sparks and crash bursts for the local player's car only.")]
        public bool CrashEffects = true;

        // skid marks: 5 s life, full alpha for the first 30%, a new point every 0.5 m, 500 points per wheel
        const float SkidLife = 5f, SkidFadeFrom = 0.7f, SkidStep = 0.5f, GroundLift = 0.03f;
        const int SkidMaxPoints = 500;
        // skid flames: 3 m long, height 0.04 * speed (m/s) shrinking 5 m/s, 2 s life, 400 at most
        const float FlameLength = 3f, FlameHeightPerSpeed = 0.04f, FlameShrink = 5f, FlameLife = 2f;
        const int FlameMax = 400;
        // tyre smoke particles: 0.7 s, radius 0.1..0.4 growing 0.5 m/s, 500 at most
        const float SmokeLife = 0.7f;
        // crash bursts stay this long: front, side, block
        static readonly float[] CrashLife = { 0.5f, 2f, 5f };
        static readonly string[] CrashKind = { "front", "side", "block" };
        static readonly int[] TailCorners = { 2, 3, 6, 7 };

        sealed class SkidPoint
        {
            public float Remaining = SkidLife, Alpha = 1f;
            public Vector3 Center, Left, Right;
            public Vector2 Uv0, Uv1;
            public float SegLen, Total;
            public bool Connected;
            public SkidPoint Copy() => (SkidPoint)MemberwiseClone();
        }

        sealed class Flame { public Vector3 P0, P1; public float Remaining, Height; public int Row; }

        sealed class Spark
        {
            public float Life, Remaining, Alpha = 1f;
            public Vector3 Pos, Vel;
            public readonly Vector3[] Offsets = new Vector3[8];
        }

        sealed class Crash { public NifParticleEffect Fx; public int Kind; public float Age; }

        /// <summary>A world-space dynamic mesh of independent quads (vertex colour carries the fade).</summary>
        sealed class QuadBatch
        {
            readonly List<Vector3> v = new List<Vector3>();
            readonly List<Vector2> uv = new List<Vector2>();
            readonly List<Color32> c = new List<Color32>();
            readonly List<int> t = new List<int>();
            readonly Mesh mesh;

            public QuadBatch(GameObject root, string name, Material mat)
            {
                var go = new GameObject(name);
                go.transform.SetParent(root.transform, false);
                mesh = OwnedAssets.Track(root, new Mesh { name = name });
                mesh.MarkDynamic();
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.lightProbeUsage = LightProbeUsage.Off;
                mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            }

            public void Clear() { v.Clear(); uv.Clear(); c.Clear(); t.Clear(); }

            /// <summary>Corners in the client's order: triangles (0,3,2) and (0,1,3).</summary>
            public void Quad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector2 u0, Vector2 u1, Vector2 u2, Vector2 u3, float a0, float a1, float a2, float a3)
            {
                int b = v.Count;
                v.Add(p0); v.Add(p1); v.Add(p2); v.Add(p3);
                uv.Add(u0); uv.Add(u1); uv.Add(u2); uv.Add(u3);
                c.Add(White(a0)); c.Add(White(a1)); c.Add(White(a2)); c.Add(White(a3));
                t.Add(b); t.Add(b + 3); t.Add(b + 2);
                t.Add(b); t.Add(b + 1); t.Add(b + 3);
            }

            static Color32 White(float a) => new Color32(255, 255, 255, (byte)Mathf.RoundToInt(Mathf.Clamp01(a) * 255f));

            public void Apply()
            {
                mesh.Clear();
                if (v.Count == 0) return;
                mesh.SetVertices(v);
                mesh.SetUVs(0, uv);
                mesh.SetColors(c);
                mesh.SetTriangles(t, 0, true);
            }
        }

        DriftCar car;
        Rigidbody body;
        GameObject fx;   // world-space container: marks, flames and sparks stay where they were laid
        QuadBatch skidMesh, flameMesh, sparkMesh;
        ParticleSystem smoke, sandSplash, waterSplash;
        CarRoadState road;
        readonly List<SkidPoint>[] skids = { new List<SkidPoint>(), new List<SkidPoint>(), new List<SkidPoint>(), new List<SkidPoint>() };
        readonly bool[] laying = new bool[4];
        readonly List<Flame> flames = new List<Flame>();
        readonly List<Spark> sparks = new List<Spark>();
        readonly List<Crash> crashes = new List<Crash>();
        readonly float[] tyreWidth = { 0.22f, 0.22f, 0.22f, 0.22f };
        readonly float[] wheelRadius = { 0.33f, 0.33f, 0.33f, 0.33f };
        readonly RaycastHit[] hits = new RaycastHit[8];
        readonly ContactPoint[] contacts = new ContactPoint[16];
        readonly bool[] ground = new bool[4];
        readonly Vector3[] groundPoint = new Vector3[4], groundNormal = new Vector3[4];
        NifParticleEffect damageSmoke;

        Vector3 prevVel, accel, lastCarPos;
        bool havePrevVel;
        float ignoreAccelUntil;
        float smokeAccum;
        bool smokeHadPrev;
        Vector3 smokePrevPos;
        float splashAccum;
        bool splashHadPrev;
        Vector3 splashPrevPos;

        void Start()
        {
            car = GetComponent<DriftCar>();
            body = GetComponent<Rigidbody>();
            fx = new GameObject(name + " effects");
            var skidTex = TextureLibrary.Get("effects/skidmark.dds", "effects");
            var flameTex = TextureLibrary.Get("effects/skidflame.dds", "effects");
            // only the effects use this texture; the client draws it with clamped UVs
            if (flameTex) flameTex.wrapMode = TextureWrapMode.Clamp;
            var skidMat = EffectMaterials.Create(fx, skidTex, false);
            skidMat.renderQueue = 2501; // marks lie on the road under the other transparent effects
            skidMesh = new QuadBatch(fx, "Skid marks", skidMat);
            flameMesh = new QuadBatch(fx, "Skid flames", EffectMaterials.Create(fx, flameTex, false));
            sparkMesh = new QuadBatch(fx, "Sparks", EffectMaterials.Create(fx, TextureLibrary.Get("effects/sparkle.dds", "effects"), true, glow: true));
            // smoke radius grows 0.5 m/s: over 0.7 s that is 1 + 1.4 times a mean 0.25 m start radius
            smoke = CreatePuffs(fx, "Tyre smoke", TextureLibrary.Get("effects/tiresmoke.dds", "effects"), false, 2.4f);
            // surface splash (0x6b4280): sand alpha-blended, growing 0.8 m/s over 0.4 s from a mean 0.5 m;
            // water additive, 3 m/s over 0.3 s from a mean 0.6 m
            sandSplash = CreatePuffs(fx, "Sand splash", TextureLibrary.Get("effects/sand.dds", "effects"), false, 1.64f);
            waterSplash = CreatePuffs(fx, "Water splash", TextureLibrary.Get("effects/water_tm.dds", "effects"), true, 2.5f);
            if (!TryGetComponent(out road)) road = gameObject.AddComponent<CarRoadState>();
            if (car)
                for (int i = 0; i < 4; i++) MeasureWheel(i, car.WheelVisuals[i]);
            lastCarPos = transform.position;
        }

        void OnDestroy()
        {
            if (fx) Destroy(fx);
        }

        void MeasureWheel(int i, Transform w)
        {
            if (!w) return;
            bool any = false;
            var b = new Bounds();
            var toCar = transform.worldToLocalMatrix;
            foreach (var mf in w.GetComponentsInChildren<MeshFilter>())
            {
                if (!mf.sharedMesh) continue;
                var mb = mf.sharedMesh.bounds;
                var m = toCar * mf.transform.localToWorldMatrix;
                for (int k = 0; k < 8; k++)
                {
                    var corner = mb.center + Vector3.Scale(mb.extents, new Vector3((k & 1) == 0 ? -1 : 1, (k & 2) == 0 ? -1 : 1, (k & 4) == 0 ? -1 : 1));
                    var p = m.MultiplyPoint3x4(corner);
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; }
                    else b.Encapsulate(p);
                }
            }
            if (!any) return;
            tyreWidth[i] = Mathf.Clamp(b.size.x, 0.12f, 0.45f);
            wheelRadius[i] = Mathf.Clamp(b.size.y * 0.5f, 0.2f, 0.6f);
        }

        /// <param name="endScale">size at the end of life over the start size (the client grows the radius linearly)</param>
        static ParticleSystem CreatePuffs(GameObject root, string name, Texture tex, bool additive, float endScale)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.playOnAwake = false;
            main.loop = true;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 500;
            main.startSpeed = 0f;
            main.gravityModifier = 0f;
            var em = ps.emission; em.enabled = false;
            var shape = ps.shape; shape.enabled = false;
            // alpha: in over the first 10% of life, out over the last 30%
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                      new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.1f), new GradientAlphaKey(1f, 0.7f), new GradientAlphaKey(0f, 1f) });
            var col = ps.colorOverLifetime; col.enabled = true; col.color = new ParticleSystem.MinMaxGradient(g);
            var sol = ps.sizeOverLifetime; sol.enabled = true; sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, endScale));
            var r = ps.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.sharedMaterial = EffectMaterials.Create(root, tex, additive);
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            ps.Play(false);
            return ps;
        }

        void FixedUpdate()
        {
            if (!body) return;
            // the client keys its smoke on the tyre model's longitudinal/lateral acceleration; here it is measured
            // from the body (collision impulses excluded, lightly smoothed against physics jitter)
            var v = body.linearVelocity;
            float dt = Time.fixedDeltaTime;
            if (havePrevVel && Time.time >= ignoreAccelUntil && dt > 0f)
            {
                var a = transform.InverseTransformDirection((v - prevVel) / dt);
                accel = Vector3.Lerp(accel, a, 1f - Mathf.Exp(-dt / 0.06f));
            }
            prevVel = v;
            havePrevVel = true;
        }

        void LateUpdate()
        {
            float dt = Time.deltaTime;
            Vector3 carPos = transform.position;
            Vector3 carDelta = carPos - lastCarPos;
            lastCarPos = carPos;
            bool boosting = car && car.Boosting;

            int grounded = 0;
            for (int i = 0; i < 4; i++)
            {
                ground[i] = Probe(i, out groundPoint[i], out groundNormal[i]);
                if (ground[i]) grounded++;
            }

            int state = SmokeState(grounded >= 2 && !boosting, out float interval, out float latDrift);
            UpdateSmoke(dt, state, interval, latDrift);
            UpdateSplash(dt);
            // mode 1: boosting (marks + skid flames, no smoke); mode 2: the heavy boost (double-width black marks)
            int mode = boosting ? (HeavyBoost ? 2 : 1) : 0;
            UpdateSkids(dt, state != 0 || mode != 0, mode, ground, groundPoint, groundNormal);
            UpdateSparks(dt, carDelta);
            UpdateCrashes(dt, carDelta);
            UpdateDamageSmoke();
        }

        bool Probe(int i, out Vector3 point, out Vector3 normal)
        {
            point = normal = Vector3.zero;
            var w = car ? car.WheelVisuals[i] : null;
            if (!w) return false;
            Vector3 up = transform.up;
            float reach = wheelRadius[i] + 0.5f;
            int n = Physics.RaycastNonAlloc(w.position + up * 0.2f, -up, hits, reach + 0.2f, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            bool ok = false;
            for (int k = 0; k < n; k++)
            {
                var h = hits[k];
                if (h.collider.transform.IsChildOf(transform) || h.distance >= best) continue;
                best = h.distance; point = h.point; normal = h.normal; ok = true;
            }
            return ok;
        }

        // ------------------------------------------------------------------ tyre smoke

        /// <summary>
        /// The client's smoke state: 2 when |lateral accel| (capped 30) reaches 18, spawning every 0.06 - a/600 s;
        /// else 1 when |longitudinal accel| (capped 30) reaches 10, every 0.14 - 0.004 a s. It also gates skid marks.
        /// </summary>
        int SmokeState(bool allowed, out float interval, out float latDrift)
        {
            interval = 0f; latDrift = 0f;
            if (!allowed) return 0;
            float lat = Mathf.Min(Mathf.Abs(accel.x), LateralSmokeMax);
            if (lat >= LateralSmokeAccel)
            {
                latDrift = lat;
                interval = 0.06f - lat / 600f;
                return 2;
            }
            float lon = Mathf.Min(Mathf.Abs(accel.z), LongSmokeMax);
            if (lon >= LongSmokeAccel)
            {
                interval = 0.14f - 0.004f * lon;
                return 1;
            }
            return 0;
        }

        void UpdateSmoke(float dt, int state, float interval, float latDrift)
        {
            if (state == 0 || !car || !smoke) { smokeHadPrev = false; smokeAccum = 0f; return; }
            smokeAccum += dt;
            Vector3 carPos = transform.position;
            if (smokeHadPrev)
            {
                // at least 3 puffs per metre travelled
                float dist = (smokePrevPos - carPos).magnitude;
                if (dist > 1e-4f)
                {
                    float perMetre = smokeAccum / interval / dist;
                    if (perMetre < 3f) interval /= 3f / perMetre;
                }
            }
            interval = Mathf.Max(interval, 0.01f);
            Vector3 back = smokeHadPrev ? smokePrevPos - carPos : Vector3.zero;
            Vector3 push = SmokePush();
            float total = smokeAccum, t = 0f;
            for (int guard = 0; smokeAccum > interval && guard < 32; guard++)
            {
                for (int i = 0; i < 4; i++)
                {
                    var w = car.WheelVisuals[i];
                    if (!w) continue;
                    bool left = (i & 1) == 0;
                    // drifting (lateral >= 18) smokes only the wheels on the side the acceleration points to
                    if (latDrift > LateralSmokeAccel && (left ? accel.x > 0f : accel.x < 0f)) continue;
                    Vector3 pos = w.position + transform.rotation * new Vector3(left ? -0.2f : 0.2f, -0.2f, 0f);
                    if (total > 0f) pos += back * (t / total);
                    EmitSmoke(pos, push);
                }
                t += interval;
                smokeAccum -= interval;
            }
            smokeHadPrev = true;
            smokePrevPos = carPos;
        }

        /// <summary>Sideways push from the slip (the client's FUN_006b9610): away from the slide, plus the slip angle forward.</summary>
        Vector3 SmokePush()
        {
            if (!body) return Vector3.zero;
            var local = transform.InverseTransformDirection(body.linearVelocity);
            float flat = new Vector2(local.x, local.z).magnitude;
            if (flat < 1f) return Vector3.zero;
            float s = Mathf.Clamp(local.x / flat, -1f, 1f);
            float a = Mathf.Asin(s);
            if (a == 0f) return Vector3.zero;
            return transform.rotation * new Vector3(s < 0f ? 1f : -1f, 0f, a).normalized;
        }

        void EmitSmoke(Vector3 pos, Vector3 push)
        {
            pos += new Vector3(Random.Range(-0.1f, 0.1f), Random.Range(0f, 0.1f), Random.Range(-0.1f, 0.1f));
            var dir = new Vector3(Random.Range(-1f, 1f), Random.Range(0.3f, 0.5f), Random.Range(-0.5f, 0.5f)).normalized + push;
            var ep = new ParticleSystem.EmitParams
            {
                position = pos,
                velocity = dir * 0.5f, // the client moves smoke by velocity * 0.5 per second
                startSize = 2f * Random.Range(0.1f, 0.4f),
                startLifetime = SmokeLife,
                startColor = Color.white,
                rotation = Random.Range(-3f, 3f) * Mathf.Rad2Deg,
                angularVelocity = Random.Range(-1f, 1f) * Mathf.Rad2Deg,
            };
            smoke.Emit(ep, 1);
        }

        // ------------------------------------------------------------------ sand / water splash

        /// <summary>
        /// The client's FUN_006b61a0 / FUN_006b5c90: while the car is in a sand.hit zone (sand.dds) or else a
        /// water.hit zone (water_tm.dds) and doing at least 5 m/s, every wheel throws a puff each
        /// max(0.02 - 0.0003 v, 0.003) s, spread back along the frame's movement. Only cras has the zones.
        /// </summary>
        void UpdateSplash(float dt)
        {
            int kind = !road ? 0 : road.Sand ? 1 : road.Water ? 2 : 0;
            float speed = car ? car.SpeedKmh / 3.6f : 0f;
            var ps = kind == 1 ? sandSplash : waterSplash;
            if (kind == 0 || speed < 5f || !ps) { splashHadPrev = false; splashAccum = 0f; return; }
            float interval = Mathf.Max(0.02f - speed * 0.0003f, 0.003f);
            splashAccum += dt;
            Vector3 carPos = transform.position;
            Vector3 back = splashHadPrev ? splashPrevPos - carPos : Vector3.zero;
            Vector3 push = SmokePush();
            float total = splashAccum, t = 0f;
            for (int guard = 0; splashAccum > interval && guard < 64; guard++)
            {
                for (int i = 0; i < 4; i++)
                {
                    var w = car.WheelVisuals[i];
                    if (!w) continue;
                    Vector3 pos = w.position + transform.rotation * new Vector3((i & 1) == 0 ? -0.2f : 0.2f, -0.2f, 0f);
                    if (total > 0f) pos += back * (t / total);
                    EmitSplash(ps, kind, pos, push);
                }
                t += interval;
                splashAccum -= interval;
            }
            splashHadPrev = true;
            splashPrevPos = carPos;
        }

        static void EmitSplash(ParticleSystem ps, int kind, Vector3 pos, Vector3 push)
        {
            bool sand = kind == 1;
            pos += new Vector3(Random.Range(-0.1f, 0.1f), Random.Range(0f, 0.1f), Random.Range(-0.1f, 0.1f));
            // water is thrown up much steeper than sand
            var dir = new Vector3(Random.Range(-1f, 1f), sand ? Random.Range(0.3f, 0.5f) : Random.Range(1f, 1.2f), Random.Range(-0.5f, 0.5f)).normalized + push;
            var ep = new ParticleSystem.EmitParams
            {
                position = pos,
                velocity = dir * 0.5f,
                startSize = 2f * (sand ? Random.Range(0.2f, 0.8f) : Random.Range(0.4f, 0.8f)),
                startLifetime = sand ? 0.4f : 0.3f,
                startColor = Color.white,
                rotation = Random.Range(-3f, 3f) * Mathf.Rad2Deg,
                angularVelocity = Random.Range(-1f, 1f) * Mathf.Rad2Deg,
            };
            ps.Emit(ep, 1);
        }

        // ------------------------------------------------------------------ skid marks and skid flames

        void UpdateSkids(float dt, bool lay, int mode, bool[] ground, Vector3[] point, Vector3[] normal)
        {
            // rear wheels only (the client loops from wheel index 2)
            for (int i = 2; i < 4; i++)
            {
                if (lay && ground[i])
                {
                    Vector3 c = point[i] + normal[i] * GroundLift;
                    float halfWidth = tyreWidth[i] * 0.5f * (mode == 2 ? 2f : 1f);
                    if (AddSkidPoint(i, c, halfWidth, !laying[i], mode == 2, out var back) && mode != 2) AddFlame(c, back);
                    laying[i] = true;
                }
                else laying[i] = false;
            }

            skidMesh.Clear();
            for (int w = 0; w < 4; w++)
            {
                var list = skids[w];
                for (int k = 0; k < list.Count; k++)
                {
                    var p = list[k];
                    p.Remaining -= dt;
                    float fadeFrom = SkidLife * SkidFadeFrom;
                    p.Alpha = p.Remaining >= fadeFrom ? 1f : 1f - (fadeFrom - p.Remaining) / fadeFrom;
                }
                list.RemoveAll(p => p.Remaining <= 0f);
                for (int k = 1; k < list.Count; k++)
                {
                    var a = list[k - 1]; var b = list[k];
                    if (!b.Connected) continue;
                    skidMesh.Quad(a.Left, b.Left, a.Right, b.Right, a.Uv0, b.Uv0, a.Uv1, b.Uv1, a.Alpha, b.Alpha, a.Alpha, b.Alpha);
                }
            }
            skidMesh.Apply();

            flameMesh.Clear();
            for (int k = 0; k < flames.Count; k++)
            {
                var f = flames[k];
                f.Remaining -= dt;
                f.Height = Mathf.Max(0f, f.Height - FlameShrink * dt);
            }
            flames.RemoveAll(f => f.Remaining <= 0f);
            foreach (var f in flames)
            {
                if (f.Height <= 0f) continue;
                float alpha = Mathf.Min(f.Height, 1f);
                float bottom = (f.Row + 1) * 0.25f, top = f.Row * 0.25f + 0.01f;
                // the client re-rolls the top edge by +-0.1 every frame, which makes the streaks flicker
                Vector3 up0 = Vector3.up * (f.Height + Random.Range(-0.1f, 0.1f)), up1 = Vector3.up * (f.Height + Random.Range(-0.1f, 0.1f));
                flameMesh.Quad(f.P0, f.P1, f.P0 + up0, f.P1 + up1,
                               new Vector2(1f, bottom), new Vector2(0f, bottom), new Vector2(1f, top), new Vector2(0f, top),
                               alpha, alpha, alpha, alpha);
            }
            flameMesh.Apply();
        }

        /// <summary>
        /// The client's FUN_006b80b0: continues the wheel's strip when the last point used the same texture half,
        /// re-aims the last point's edge when 0.5 m have been covered and otherwise just moves the last point along.
        /// Returns true when a new connected point was pushed (that is when a skid flame is born), with the
        /// direction back along the strip.
        /// </summary>
        bool AddSkidPoint(int wheel, Vector3 c, float halfWidth, bool newStrip, bool heavy, out Vector3 back)
        {
            back = Vector3.zero;
            var list = skids[wheel];
            var last = list.Count > 0 ? list[list.Count - 1] : null;
            float row0 = heavy ? 0.5f : 0f, row1 = heavy ? 1f : 0.5f; // skidmark.dds: faint marks on top, heavy below
            var p = new SkidPoint { Center = c };
            if (!newStrip && last != null && last.Uv0.y == row0)
            {
                Vector3 d = c - last.Center;
                Vector3 perp = Vector3.Cross(Vector3.up, new Vector3(d.x, 0f, d.z));
                perp = perp.sqrMagnitude > 1e-10f ? perp.normalized : Vector3.zero;
                float seg = d.magnitude;
                p.Total = seg + last.Total;
                p.Left = c - perp * halfWidth;
                p.Right = c + perp * halfWidth;
                // the texture repeats every 4 half-widths along the strip; right wheels start 0.3 in
                float u = p.Total / (4f * halfWidth) + ((wheel & 1) != 0 ? 0.3f : 0f);
                p.Uv0 = new Vector2(u, row0);
                p.Uv1 = new Vector2(u, row1);
                p.Connected = true;
                if (seg + last.SegLen < SkidStep)
                {
                    p.SegLen = seg + last.SegLen;
                    p.Connected = last.Connected;
                    list[list.Count - 1] = p;
                    return false;
                }
                p.SegLen = seg;
                last.Left = last.Center - perp * halfWidth;
                last.Right = last.Center + perp * halfWidth;
            }
            else
            {
                p.Uv0 = new Vector2(0f, row0);
                p.Uv1 = new Vector2(0f, row1);
                p.Left = p.Right = c; // set when the strip's second point arrives
            }
            list.Add(p);
            while (list.Count > SkidMaxPoints) list.RemoveAt(0);
            if (!p.Connected || list.Count < 2) return false;
            back = list[list.Count - 2].Center - c;
            back = back.sqrMagnitude > 1e-10f ? back.normalized : Vector3.zero;
            return true;
        }

        void AddFlame(Vector3 p0, Vector3 back)
        {
            float speedMs = car ? car.SpeedKmh / 3.6f : 0f;
            flames.Add(new Flame
            {
                P0 = p0,
                P1 = p0 + back * FlameLength,
                Remaining = FlameLife,
                Height = FlameHeightPerSpeed * speedMs,
                Row = Random.Range(0, 4),
            });
            while (flames.Count > FlameMax) flames.RemoveAt(0);
        }

        // ------------------------------------------------------------------ collisions

        void OnCollisionEnter(Collision c)
        {
            ignoreAccelUntil = Time.time + 0.1f;
            if (!CrashEffects) return;
            // the client's strength is the impact speed in km/h (int m/s * 3.6)
            int strength = (int)((int)c.relativeVelocity.magnitude * 3.6f);
            if (strength <= 0) return;
            // "block" bursts are for the world (walls, buildings); vehicles get front/side by the contact normal
            var rb = c.rigidbody;
            bool block = !rb || (rb.isKinematic && !rb.GetComponent<TrafficCar>() && !rb.GetComponent<Net.RemoteCar>() && !rb.GetComponent<DriftCar>());
            int n = c.GetContacts(contacts);
            var seen = new List<Vector3>();
            for (int k = 0; k < n; k++)
            {
                var cp = contacts[k];
                bool dup = false;
                foreach (var s in seen) if ((s - cp.point).sqrMagnitude < 1e-6f) dup = true;
                if (dup) continue;
                seen.Add(cp.point);
                // the burst sits 0.3..0.9 m above the car's origin
                Vector3 pt = cp.point;
                float dy = pt.y - transform.position.y;
                pt.y = transform.position.y + Mathf.Clamp(dy, 0.3f, 0.9f);
                if (strength >= 50) SpawnSpark(pt);
                int kind = block ? 2 : (-Vector3.Dot(transform.forward, cp.normal) < 0.7f ? 1 : 0);
                SpawnCrash(kind, strength, pt);
            }
        }

        void SpawnCrash(int kind, int strength, Vector3 pt)
        {
            // one burst of a kind per half second
            foreach (var cr in crashes) if (cr.Kind == kind && cr.Fx && cr.Age < 0.5f) return;
            int level = strength < 100 ? 1 : strength < 200 ? 2 : 3;
            var fxObj = NifParticleEffect.Spawn($@"effects\crash0{level}_{CrashKind[kind]}.nif", pt, transform.rotation, fx.transform);
            if (!fxObj) return;
            fxObj.Lifetime = CrashLife[kind];
            crashes.Add(new Crash { Fx = fxObj, Kind = kind });
        }

        void UpdateCrashes(float dt, Vector3 carDelta)
        {
            foreach (var cr in crashes)
            {
                cr.Age += dt;
                // bursts ride along with the car (position only, as the client does)
                if (cr.Fx) cr.Fx.transform.position += carDelta;
            }
            crashes.RemoveAll(cr => !cr.Fx || cr.Age > CrashLife[cr.Kind]);
        }

        /// <summary>
        /// The client's FUN_006c2d30: one streak per contact point flying back from the car at 20 m/s,
        /// 0.04..0.11 m thick and 2..2.8 m long, as two crossed quads, bright end leading.
        /// </summary>
        void SpawnSpark(Vector3 pt)
        {
            bool reversing = body && Vector3.Dot(body.linearVelocity, transform.forward) < -0.5f;
            var gb = new Vector3(Random.Range(-0.05f, 0.11f), reversing ? 1f : -1f, Random.Range(-0.06f, 0.2f)) * 20f;
            var s = new Spark
            {
                Life = 0.5f + Random.Range(0f, 0.4f),
                Pos = pt,
                Vel = transform.rotation * new Vector3(gb.x, gb.z, gb.y),
            };
            s.Remaining = s.Life;
            float w = 0.04f + Random.Range(0f, 0.07f), len = 2f + Random.Range(0f, 0.8f);
            Vector3 dir = s.Vel.normalized, up = transform.up, right = transform.right;
            s.Offsets[0] = -up * w; s.Offsets[1] = up * w;
            s.Offsets[2] = s.Offsets[0] + dir * len; s.Offsets[3] = s.Offsets[1] + dir * len;
            s.Offsets[4] = -right * w; s.Offsets[5] = right * w;
            s.Offsets[6] = s.Offsets[4] + dir * len; s.Offsets[7] = s.Offsets[5] + dir * len;
            sparks.Add(s);
            while (sparks.Count > 100) sparks.RemoveAt(0);
        }

        void UpdateSparks(float dt, Vector3 carDelta)
        {
            sparkMesh.Clear();
            foreach (var s in sparks)
            {
                s.Remaining -= dt;
                float fadeFrom = s.Life * 0.95f;
                s.Alpha = s.Remaining <= fadeFrom ? Mathf.Max(0f, s.Remaining / fadeFrom) : 1f;
                s.Pos += carDelta;
                s.Vel.y -= 0.7f * dt;
                s.Pos += s.Vel * dt;
                // the far ends droop at 1 m/s
                foreach (int k in TailCorners) s.Offsets[k].y -= dt;
            }
            sparks.RemoveAll(s => s.Remaining <= 0f);
            foreach (var s in sparks)
            {
                var o = s.Offsets; var p = s.Pos; float a = s.Alpha;
                sparkMesh.Quad(p + o[0], p + o[1], p + o[2], p + o[3], new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 0f), new Vector2(1f, 1f), a, a, a, a);
                sparkMesh.Quad(p + o[4], p + o[5], p + o[6], p + o[7], new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 0f), new Vector2(1f, 1f), a, a, a, a);
            }
            sparkMesh.Apply();
        }

        // ------------------------------------------------------------------ damage smoke

        void UpdateDamageSmoke()
        {
            bool want = Damage01 > 0.6f;
            if (want && !damageSmoke)
            {
                // over the engine: client offset (0, 1.5, 0.38) in the car's frame
                damageSmoke = NifParticleEffect.Spawn(@"effects\damage_smoke.nif", transform.TransformPoint(new Vector3(0f, 0.38f, 1.5f)), transform.rotation, transform);
                if (damageSmoke) damageSmoke.transform.localPosition = new Vector3(0f, 0.38f, 1.5f);
            }
            else if (!want && damageSmoke)
            {
                Destroy(damageSmoke.gameObject);
                damageSmoke = null;
            }
        }
    }
}
