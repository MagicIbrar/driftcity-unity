using System;
using System.Collections.Generic;
using System.IO;
using DriftCity.Core;
using DriftCity.Core.Nif;
using UnityEngine;
using UnityEngine.Rendering;
using Random = UnityEngine.Random;

namespace DriftCity.Game
{
    /// <summary>
    /// Plays the NiParticleSystems of an effect NIF (effects\crash0N_*.nif, damage_smoke.nif) with Unity particles.
    /// NifSceneBuilder skips particle systems and the shared NIF parser only reads past the emitter/modifier fields,
    /// so they are re-read here from the raw blocks. Particles are emitted by script the way NiPSysEmitter does it
    /// (declination/planar angle around the emitter object's Z axis, speed/radius/life plus half their variation),
    /// and the grow/fade, colour, rotation, gravity and spawn-on-death modifiers are mapped onto Unity modules.
    /// Texture flip controllers swap the material's texture over time, as they animate the shared texturing property.
    /// </summary>
    public sealed class NifParticleEffect : MonoBehaviour
    {
        /// <summary>Seconds until the effect removes itself; 0 keeps it until destroyed by its owner.</summary>
        public float Lifetime;

        sealed class SystemDef
        {
            public string Name;
            public Vector3 EmitterPos;          // emitter object in NIF-root space (Unity axes)
            public Quaternion EmitterRot = Quaternion.identity;
            public float EmitterScale = 1f, Scale = 1f; // Scale: particle-system space -> NIF-root units
            public bool WorldSpace;
            public float Speed, SpeedVar, Decl, DeclVar, Planar, PlanarVar, Radius, RadiusVar, Life, LifeVar;
            public Color InitialColor = Color.white;
            public int Shape; public Vector3 ShapeSize; // 0 box (w,h,d), 1 sphere (r), 2 cylinder (r,h)
            public float BirthRate;
            public float CtlrStart, CtlrStop; public int CtlrCycle;
            public float[] EmitKeyTimes; public bool[] EmitKeyOn;
            public float Grow, Fade; public int GrowGen, FadeGen = -1;
            public Gradient Colors;
            public float RotSpeed, RotSpeedVar, RotAngle, RotAngleVar; public bool RotRandomSign;
            public Vector3 Gravity;             // acceleration in NIF-root space, already scaled
            public bool SpawnOnDeath; public int SpawnMin, SpawnMax; public float SpawnPct, SpawnDirChaos, SpawnLife, SpawnLifeVar;
            public string Texture;
            public string[] FlipFrames; public float[] FlipTimes; public int[] FlipIndex;
            public float FlipStart, FlipStop; public int FlipCycle;
            public bool Additive, Glow;
        }

        sealed class EffectDef { public string Dir; public readonly List<SystemDef> Systems = new List<SystemDef>(); }

        sealed class Live
        {
            public SystemDef Def;
            public ParticleSystem Ps, Child;
            public Material Mat;
            public Texture2D[] Frames;
            public int Frame = -1;
            public float Accum;
        }

        static readonly Dictionary<string, EffectDef> cache = new Dictionary<string, EffectDef>(StringComparer.OrdinalIgnoreCase);

        EffectDef def;
        readonly List<Live> live = new List<Live>();
        float time;

        /// <summary>Spawns <paramref name="path"/> at a pose; returns null when the file has no playable particle systems.</summary>
        public static NifParticleEffect Spawn(string path, Vector3 position, Quaternion rotation, Transform parent = null)
        {
            var d = Load(path);
            if (d == null || d.Systems.Count == 0) return null;
            var go = new GameObject(Path.GetFileName(path));
            if (parent) go.transform.SetParent(parent, false);
            go.transform.SetPositionAndRotation(position, rotation);
            var fx = go.AddComponent<NifParticleEffect>();
            fx.Build(d);
            return fx;
        }

        void Build(EffectDef d)
        {
            def = d;
            foreach (var s in d.Systems)
            {
                var go = new GameObject(s.Name);
                go.transform.SetParent(transform, false);
                var ps = go.AddComponent<ParticleSystem>();
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                var l = new Live { Def = s, Ps = ps };
                l.Frames = LoadFrames(s, d.Dir);
                var tex = l.Frames != null && l.Frames.Length > 0 ? l.Frames[0] : TextureLibrary.Get(s.Texture, d.Dir);
                l.Mat = EffectMaterials.Create(gameObject, tex, s.Additive, s.Glow);
                Configure(ps, s, l.Mat, generation: 0);
                // particles are emitted by Emit(); the system only has to be playing to simulate them
                var em = ps.emission; em.enabled = false;
                if (s.SpawnOnDeath && s.SpawnMax > 0) l.Child = AddSpawnChild(ps, s, l.Mat);
                ps.Play(false);
                live.Add(l);
            }
        }

        static void Configure(ParticleSystem ps, SystemDef s, Material mat, int generation)
        {
            var main = ps.main;
            main.playOnAwake = false;
            main.loop = true;
            main.startSpeed = 0f;
            main.startColor = Color.white;
            main.gravityModifier = 0f;
            main.maxParticles = 1000;
            main.simulationSpace = s.WorldSpace ? ParticleSystemSimulationSpace.World : ParticleSystemSimulationSpace.Local;
            main.scalingMode = ParticleSystemScalingMode.Local;
            var shape = ps.shape; shape.enabled = false;

            if (s.Colors != null)
            {
                var col = ps.colorOverLifetime; col.enabled = true; col.color = new ParticleSystem.MinMaxGradient(s.Colors);
            }
            var curve = GrowFadeCurve(s, generation);
            if (curve != null)
            {
                var sol = ps.sizeOverLifetime; sol.enabled = true; sol.size = new ParticleSystem.MinMaxCurve(1f, curve);
            }
            if (s.Gravity.sqrMagnitude > 0f)
            {
                var f = ps.forceOverLifetime; f.enabled = true;
                f.space = ParticleSystemSimulationSpace.Local;
                f.x = s.Gravity.x; f.y = s.Gravity.y; f.z = s.Gravity.z;
            }
            var r = ps.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }

        /// <summary>NiPSysAgeDeathModifier spawning on death: a Unity death sub-emitter bursting the spawn modifier's count.</summary>
        static ParticleSystem AddSpawnChild(ParticleSystem parent, SystemDef s, Material mat)
        {
            var go = new GameObject(parent.name + " spawn");
            go.transform.SetParent(parent.transform, false);
            var child = go.AddComponent<ParticleSystem>();
            child.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            Configure(child, s, mat, generation: 1);
            var main = child.main;
            main.loop = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(Mathf.Max(0.02f, s.SpawnLife - s.SpawnLifeVar * 0.5f), Mathf.Max(0.03f, s.SpawnLife + s.SpawnLifeVar * 0.5f));
            main.startSize = 2f * s.Radius * s.Scale;
            main.startSpeed = Mathf.Abs(s.Speed) * s.Scale * s.SpawnDirChaos;
            main.startRotation = new ParticleSystem.MinMaxCurve(s.RotAngle - s.RotAngleVar, s.RotAngle + s.RotAngleVar);
            var em = child.emission;
            em.enabled = true;
            em.rateOverTime = 0f;
            var burst = new ParticleSystem.Burst(0f, (short)Mathf.Max(1, s.SpawnMin), (short)Mathf.Max(1, s.SpawnMax)) { probability = Mathf.Clamp01(s.SpawnPct) };
            em.SetBursts(new[] { burst });
            var shape = child.shape; shape.enabled = true; shape.shapeType = ParticleSystemShapeType.Sphere; shape.radius = 0.01f;
            var iv = child.inheritVelocity; iv.enabled = true; iv.mode = ParticleSystemInheritVelocityMode.Initial; iv.curve = 1f;
            if (s.RotSpeed != 0f || s.RotSpeedVar != 0f)
            {
                var rot = child.rotationOverLifetime; rot.enabled = true;
                rot.z = new ParticleSystem.MinMaxCurve(s.RotSpeed - s.RotSpeedVar, s.RotSpeed + s.RotSpeedVar);
            }
            var subs = parent.subEmitters;
            subs.enabled = true;
            subs.AddSubEmitter(child, ParticleSystemSubEmitterType.Death, ParticleSystemSubEmitterProperties.InheritNothing);
            return child;
        }

        /// <summary>NiPSysGrowFadeModifier: grow from 0 over Grow seconds, shrink to 0 over the last Fade seconds
        /// (absolute times, approximated on the mean lifetime because Unity curves run over normalized age).</summary>
        static AnimationCurve GrowFadeCurve(SystemDef s, int generation)
        {
            float grow = s.GrowGen == generation ? s.Grow : 0f, fade = s.FadeGen == generation ? s.Fade : 0f;
            if (grow <= 0f && fade <= 0f) return null;
            float life = generation == 0 ? s.Life : s.SpawnLife, var = generation == 0 ? s.LifeVar : s.SpawnLifeVar;
            float lo = Mathf.Max(0.02f, life - var * 0.5f), hi = Mathf.Max(lo, life + var * 0.5f);
            float mean = (lo + hi) * 0.5f;
            var keys = new Keyframe[9];
            for (int i = 0; i < keys.Length; i++)
            {
                float t = i / 8f, age = t * mean;
                float g = grow > 0f ? Mathf.Clamp01(age / grow) : 1f;
                float f = fade > 0f ? Mathf.Clamp01((mean - age) / fade) : 1f;
                keys[i] = new Keyframe(t, g * f);
            }
            var c = new AnimationCurve(keys);
            for (int i = 0; i < keys.Length; i++) c.SmoothTangents(i, 0f);
            return c;
        }

        static Texture2D[] LoadFrames(SystemDef s, string dir)
        {
            if (s.FlipFrames == null || s.FlipFrames.Length == 0) return null;
            var frames = new Texture2D[s.FlipFrames.Length];
            for (int i = 0; i < frames.Length; i++) frames[i] = TextureLibrary.Get(s.FlipFrames[i], dir);
            return frames;
        }

        void Update() => Advance(Time.deltaTime);

        /// <summary>Earliest controller start and latest stop of the emitters and flip-books (the effect's key range).</summary>
        public Vector2 KeyRange
        {
            get
            {
                float lo = float.MaxValue, hi = 0f;
                if (def != null)
                    foreach (var s in def.Systems)
                    {
                        lo = Mathf.Min(lo, s.CtlrStart); hi = Mathf.Max(hi, s.CtlrStop);
                        if (s.FlipFrames != null) { lo = Mathf.Min(lo, s.FlipStart); hi = Mathf.Max(hi, s.FlipStop); }
                    }
                return lo == float.MaxValue ? Vector2.zero : new Vector2(lo, hi);
            }
        }

        /// <summary>One step of the effect clock (Update; batch renders step it by hand and then simulate the systems).</summary>
        public void Advance(float dt)
        {
            time += dt;
            foreach (var l in live)
            {
                if (!l.Ps) continue;
                var s = l.Def;
                float t = CtlrTime(time, s.CtlrStart, s.CtlrStop, s.CtlrCycle);
                if (s.BirthRate > 0f && EmitterOn(s, t))
                {
                    l.Accum += s.BirthRate * dt;
                    int n = Mathf.Min(100, (int)l.Accum);
                    l.Accum -= (int)l.Accum;
                    for (int i = 0; i < n; i++) Emit(l);
                }
                if (l.Frames != null && l.Frames.Length > 0 && s.FlipTimes != null && s.FlipTimes.Length > 0)
                {
                    float ft = CtlrTime(time, s.FlipStart, s.FlipStop, s.FlipCycle);
                    int k = 0;
                    for (int i = 0; i < s.FlipTimes.Length; i++) if (s.FlipTimes[i] <= ft) k = i;
                    int frame = Mathf.Clamp(s.FlipIndex[k], 0, l.Frames.Length - 1);
                    if (frame != l.Frame && l.Frames[frame]) { l.Frame = frame; l.Mat.mainTexture = l.Frames[frame]; }
                }
                if (s.WorldSpace && s.Gravity.sqrMagnitude > 0f)
                {
                    // world-space particles take the gravity axis as the effect is turned now
                    var g = transform.TransformDirection(s.Gravity);
                    SetWorldForce(l.Ps, g);
                    SetWorldForce(l.Child, g);
                }
            }
            if (Lifetime > 0f && time >= Lifetime) Destroy(gameObject);
        }

        static void SetWorldForce(ParticleSystem ps, Vector3 g)
        {
            if (!ps) return;
            var f = ps.forceOverLifetime;
            f.space = ParticleSystemSimulationSpace.World;
            f.x = g.x; f.y = g.y; f.z = g.z;
        }

        /// <summary>Controller time for cycle types loop (0), reverse (1) and clamp (2).</summary>
        static float CtlrTime(float t, float start, float stop, int cycle)
        {
            float span = stop - start;
            if (span <= 0f) return start;
            switch (cycle)
            {
                case 0: return start + Mathf.Repeat(t, span);
                case 1: return start + Mathf.PingPong(t, span);
                default: return Mathf.Min(start + t, stop);
            }
        }

        static bool EmitterOn(SystemDef s, float t)
        {
            if (s.EmitKeyTimes == null || s.EmitKeyTimes.Length == 0) return true;
            bool on = s.EmitKeyOn[0];
            for (int i = 0; i < s.EmitKeyTimes.Length; i++) if (s.EmitKeyTimes[i] <= t) on = s.EmitKeyOn[i];
            return on;
        }

        static float Sym() => Random.value * 2f - 1f;

        void Emit(Live l)
        {
            var s = l.Def;
            float life = s.Life + s.LifeVar * (Random.value - 0.5f);
            if (life <= 0.01f) return; // NiPSysEmitter lets these die on their first update
            float radius = (s.Radius + s.RadiusVar * (Random.value - 0.5f)) * s.Scale;
            if (radius <= 0f) return;
            float decl = s.Decl + s.DeclVar * Sym(), planar = s.Planar + s.PlanarVar * Sym();
            float sd = Mathf.Sin(decl);
            var gb = new Vector3(Mathf.Cos(planar) * sd, Mathf.Sin(planar) * sd, Mathf.Cos(decl)); // Gamebryo: Z is the emitter axis
            Vector3 dir = s.EmitterRot * new Vector3(gb.x, gb.z, gb.y);
            float speed = (s.Speed + s.SpeedVar * (Random.value - 0.5f)) * s.Scale;
            Vector3 local;
            switch (s.Shape)
            {
                case 1: local = Random.insideUnitSphere * s.ShapeSize.x; break;
                case 2:
                {
                    var disc = Random.insideUnitCircle * s.ShapeSize.x;
                    local = new Vector3(disc.x, s.ShapeSize.y * (Random.value - 0.5f), disc.y); // Gamebryo Z (height) -> Unity Y
                    break;
                }
                default:
                    local = new Vector3(s.ShapeSize.x * (Random.value - 0.5f), s.ShapeSize.z * (Random.value - 0.5f), s.ShapeSize.y * (Random.value - 0.5f));
                    break;
            }
            Vector3 pos = s.EmitterPos + s.EmitterRot * (local * s.EmitterScale);
            Vector3 vel = dir * speed;
            if (s.WorldSpace) { pos = transform.TransformPoint(pos); vel = transform.TransformDirection(vel); }
            float spin = (s.RotSpeed + s.RotSpeedVar * Sym()) * Mathf.Rad2Deg;
            if (s.RotRandomSign && Random.value < 0.5f) spin = -spin;
            var ep = new ParticleSystem.EmitParams
            {
                position = pos,
                velocity = vel,
                startSize = 2f * radius, // Gamebryo quads extend one radius either side of the particle
                startLifetime = life,
                startColor = s.InitialColor,
                rotation = (s.RotAngle + s.RotAngleVar * Sym()) * Mathf.Rad2Deg,
                angularVelocity = spin,
            };
            l.Ps.Emit(ep, 1);
        }

        // ---------------------------------------------------------------- parsing

        static EffectDef Load(string path)
        {
            var key = NayaPack.NormalizePath(path);
            if (cache.TryGetValue(key, out var hit)) return hit;
            EffectDef d = null;
            try { d = Parse(key); }
            catch (Exception ex) { Debug.LogWarning($"{key}: particle effect not loaded ({ex.Message})"); }
            cache[key] = d;
            return d;
        }

        static EffectDef Parse(string path)
        {
            var fs = DriftCityData.Files;
            if (fs == null || !fs.Exists(path)) return null;
            byte[] data = fs.Read(path);

            // same walk as NifFile.Parse, keeping each block's start so skipped fields can be read again
            var r = new NifReader(data);
            r.Line();
            r.Version = r.U32();
            r.U8();
            r.UserVersion = r.U32();
            uint count = r.Count(1 << 20);
            if (r.UserVersion >= 3) { r.U32(); for (int k = 0; k < 3; k++) r.Skip(r.U8()); }
            int numTypes = r.U16();
            var types = new string[numTypes];
            for (int i = 0; i < numTypes; i++) types[i] = r.Str();
            var typeIndex = r.U16s((int)count);
            r.Skip((int)r.U32() * 4);
            var blocks = new NiObject[count];
            var offsets = new int[count];
            for (int i = 0; i < count; i++)
            {
                string type = types[typeIndex[i] & 0x7FFF];
                var b = NifTypes.Create(type);
                if (b == null) break;
                b.Index = i; b.TypeName = type;
                offsets[i] = r.Pos;
                try { b.Read(r); } catch { break; }
                blocks[i] = b;
            }

            var parent = new Dictionary<int, int>();
            for (int i = 0; i < count; i++)
                if (blocks[i] is NiNode n)
                    foreach (var c in n.Children) if (c >= 0 && c < count) parent[c] = i;

            var effect = new EffectDef { Dir = Path.GetDirectoryName(path) };
            for (int i = 0; i < count; i++)
            {
                if (!(blocks[i] is NiParticleSystem psys)) continue;
                var s = ReadSystem(data, blocks, offsets, parent, psys);
                if (s != null) effect.Systems.Add(s);
            }
            return effect;
        }

        static SystemDef ReadSystem(byte[] data, NiObject[] blocks, int[] offsets, Dictionary<int, int> parent, NiParticleSystem psys)
        {
            T Get<T>(int i) where T : NiObject => i >= 0 && i < blocks.Length ? blocks[i] as T : null;
            NifReader At(int i) => new NifReader(data) { Pos = offsets[i] };
            void SkipModifierBase(NifReader rd) { rd.Str(); rd.U32(); rd.Ref(); rd.Bool(); }

            var s = new SystemDef { Name = string.IsNullOrEmpty(psys.Name) ? "Particles" : psys.Name, WorldSpace = psys.WorldSpace };
            WorldOf(blocks, parent, psys.Index, out _, out _, out s.Scale);
            bool haveEmitter = false;
            int spawnMod = -1;
            foreach (int m in psys.Modifiers)
            {
                var mod = Get<NiPSysModifier>(m);
                if (mod == null) continue;
                var rd = At(m);
                SkipModifierBase(rd);
                switch (mod)
                {
                    case NiPSysMeshEmitter _:
                        break; // emits from a mesh's vertices: not used by the car effects
                    case NiPSysVolumeEmitter _:
                    {
                        s.Speed = rd.F32(); s.SpeedVar = rd.F32(); s.Decl = rd.F32(); s.DeclVar = rd.F32(); s.Planar = rd.F32(); s.PlanarVar = rd.F32();
                        var c = rd.C4();
                        s.InitialColor = new Color(c.R, c.G, c.B, c.A);
                        s.Radius = rd.F32(); s.RadiusVar = rd.F32(); s.Life = rd.F32(); s.LifeVar = rd.F32();
                        int emitterObj = rd.Ref();
                        if (mod is NiPSysBoxEmitter) { s.Shape = 0; s.ShapeSize = new Vector3(rd.F32(), rd.F32(), rd.F32()); }
                        else if (mod is NiPSysSphereEmitter) { s.Shape = 1; s.ShapeSize = new Vector3(rd.F32(), 0f, 0f); }
                        else { s.Shape = 2; s.ShapeSize = new Vector3(rd.F32(), rd.F32(), 0f); }
                        WorldOf(blocks, parent, emitterObj >= 0 ? emitterObj : psys.Index, out s.EmitterPos, out s.EmitterRot, out s.EmitterScale);
                        haveEmitter = true;
                        break;
                    }
                    case NiPSysGrowFadeModifier _:
                        s.Grow = rd.F32(); s.GrowGen = rd.U16(); s.Fade = rd.F32(); s.FadeGen = rd.U16();
                        break;
                    case NiPSysAgeDeathModifier _:
                        s.SpawnOnDeath = rd.Bool(); spawnMod = rd.Ref();
                        break;
                    case NiPSysRotationModifier _:
                        s.RotSpeed = rd.F32(); s.RotSpeedVar = rd.F32(); s.RotAngle = rd.F32(); s.RotAngleVar = rd.F32(); s.RotRandomSign = rd.Bool();
                        break;
                    case NiPSysGravityModifier _:
                    {
                        int obj = rd.Ref(); var axis = rd.V3(); rd.F32(); float strength = rd.F32();
                        WorldOf(blocks, parent, obj >= 0 ? obj : psys.Index, out _, out var grot, out _);
                        // planar force along the gravity object's axis, in particle-system units
                        s.Gravity += (grot * DriftCityData.ToUnity(axis)).normalized * strength * s.Scale;
                        break;
                    }
                    case NiPSysColorModifier _:
                    {
                        int colorData = rd.Ref();
                        if (Get<NiColorData>(colorData) != null) s.Colors = ReadColorKeys(At(colorData));
                        break;
                    }
                }
            }
            if (!haveEmitter) return null;
            if (s.SpawnOnDeath && Get<NiPSysSpawnModifier>(spawnMod) != null)
            {
                var rd = At(spawnMod);
                SkipModifierBase(rd);
                rd.U16(); s.SpawnPct = rd.F32(); s.SpawnMin = rd.U16(); s.SpawnMax = rd.U16();
                rd.F32(); s.SpawnDirChaos = rd.F32(); s.SpawnLife = rd.F32(); s.SpawnLifeVar = rd.F32();
            }
            else s.SpawnOnDeath = false;

            // birth rate and on/off keys come from the emitter controller on this system
            for (int i = 0; i < blocks.Length; i++)
            {
                if (!(blocks[i] is NiPSysEmitterCtlr ctlr) || ctlr.Target != psys.Index) continue;
                s.CtlrStart = ctlr.StartTime; s.CtlrStop = ctlr.StopTime; s.CtlrCycle = (ctlr.Flags >> 1) & 3;
                var rate = Get<NiFloatInterpolator>(ctlr.Interpolator);
                s.BirthRate = rate != null ? rate.Value : 0f;
                if (rate != null && Get<NiFloatData>(rate.Data) != null)
                {
                    // keyed birth rate: the first key stands in for the curve
                    var kd = At(rate.Data);
                    if (kd.U32() > 0) { kd.U32(); kd.F32(); s.BirthRate = kd.F32(); }
                }
                if (float.IsNaN(s.BirthRate) || s.BirthRate < 0f || s.BirthRate > 10000f) s.BirthRate = 0f;
                var rd = At(i);
                rd.Ref(); rd.U16(); rd.F32(); rd.F32(); rd.F32(); rd.F32(); rd.Ref(); // time controller
                rd.Ref(); rd.Str();                                                   // interpolator, modifier name
                var vis = Get<NiBoolInterpolator>(rd.Ref());
                if (vis != null && Get<NiBoolData>(vis.Data) != null) ReadBoolKeys(At(vis.Data), out s.EmitKeyTimes, out s.EmitKeyOn);
                break;
            }

            // material: properties inherit down the tree, the system's own win
            NiTexturingProperty texProp = null; NiAlphaProperty alpha = null;
            var chain = new List<int>();
            for (int i = psys.Index, guard = 0; i >= 0 && guard < 64; i = parent.TryGetValue(i, out var p) ? p : -1, guard++) chain.Add(i);
            for (int k = chain.Count - 1; k >= 0; k--)
                if (blocks[chain[k]] is NiAVObject o)
                    foreach (var pi in o.Properties)
                    {
                        if (Get<NiTexturingProperty>(pi) is NiTexturingProperty tp) texProp = tp;
                        if (Get<NiAlphaProperty>(pi) is NiAlphaProperty ap) alpha = ap;
                    }
            s.Additive = alpha != null && alpha.Blend && alpha.DstBlend == 0; // ALPHA_ONE
            if (texProp != null)
            {
                s.Texture = Get<NiSourceTexture>(texProp.Base?.Source ?? -1)?.FileName;
                s.Glow = texProp.Glow != null;
                for (int ci = texProp.Controller, guard = 0; ci >= 0 && guard < 16; guard++)
                {
                    var tc = Get<NiTimeController>(ci);
                    if (tc == null) break;
                    if (tc is NiFlipController flip && flip.TextureSlot == 0 && flip.Sources.Length > 0)
                    {
                        s.FlipFrames = new string[flip.Sources.Length];
                        for (int f = 0; f < flip.Sources.Length; f++) s.FlipFrames[f] = Get<NiSourceTexture>(flip.Sources[f])?.FileName ?? s.Texture;
                        s.FlipStart = flip.StartTime; s.FlipStop = flip.StopTime; s.FlipCycle = (flip.Flags >> 1) & 3;
                        var interp = Get<NiFloatInterpolator>(flip.Interpolator);
                        if (interp != null && Get<NiFloatData>(interp.Data) != null) ReadFloatKeys(At(interp.Data), out s.FlipTimes, out s.FlipIndex);
                        else if (interp != null) { s.FlipTimes = new[] { 0f }; s.FlipIndex = new[] { (int)interp.Value }; }
                        break;
                    }
                    ci = tc.Next;
                }
            }
            return s;
        }

        /// <summary>NiColorData keys (normalized particle age) as a Unity gradient.</summary>
        static Gradient ReadColorKeys(NifReader rd)
        {
            uint n = rd.U32();
            if (n == 0) return null;
            uint kt = rd.U32();
            var times = new List<float>(); var cols = new List<Color>();
            for (int i = 0; i < n; i++)
            {
                float t = rd.F32(); var c = rd.C4();
                if (kt == 2) rd.Skip(32); else if (kt == 3) rd.Skip(12);
                times.Add(t); cols.Add(new Color(c.R, c.G, c.B, c.A));
            }
            // Unity gradients hold up to 8 keys
            int step = Mathf.Max(1, Mathf.CeilToInt(times.Count / 8f));
            var ck = new List<GradientColorKey>(); var ak = new List<GradientAlphaKey>();
            for (int i = 0; i < times.Count; i += step)
            {
                ck.Add(new GradientColorKey(cols[i], Mathf.Clamp01(times[i])));
                ak.Add(new GradientAlphaKey(cols[i].a, Mathf.Clamp01(times[i])));
            }
            if (step > 1 && (times.Count - 1) % step != 0)
            {
                ck[ck.Count - 1] = new GradientColorKey(cols[times.Count - 1], Mathf.Clamp01(times[times.Count - 1]));
                ak[ak.Count - 1] = new GradientAlphaKey(cols[times.Count - 1].a, Mathf.Clamp01(times[times.Count - 1]));
            }
            var g = new Gradient();
            g.SetKeys(ck.ToArray(), ak.ToArray());
            return g;
        }

        static void ReadBoolKeys(NifReader rd, out float[] times, out bool[] values)
        {
            uint n = rd.U32();
            times = new float[n]; values = new bool[n];
            if (n == 0) return;
            uint kt = rd.U32();
            for (int i = 0; i < n; i++)
            {
                times[i] = rd.F32(); values[i] = rd.U8() != 0;
                if (kt == 2) rd.Skip(2); else if (kt == 3) rd.Skip(12);
            }
        }

        static void ReadFloatKeys(NifReader rd, out float[] times, out int[] values)
        {
            uint n = rd.U32();
            times = new float[n]; values = new int[n];
            if (n == 0) return;
            uint kt = rd.U32();
            for (int i = 0; i < n; i++)
            {
                times[i] = rd.F32(); values[i] = Mathf.RoundToInt(rd.F32());
                if (kt == 2) rd.Skip(8); else if (kt == 3) rd.Skip(12);
            }
        }

        /// <summary>Pose of a block relative to the NIF root, composed down from the top node (Unity axes).</summary>
        static void WorldOf(NiObject[] blocks, Dictionary<int, int> parent, int index, out Vector3 pos, out Quaternion rot, out float scale)
        {
            pos = Vector3.zero; rot = Quaternion.identity; scale = 1f;
            var chain = new List<NiAVObject>();
            for (int i = index, guard = 0; i >= 0 && i < blocks.Length && guard < 64; i = parent.TryGetValue(i, out var p) ? p : -1, guard++)
                if (blocks[i] is NiAVObject o) chain.Add(o);
            for (int k = chain.Count - 1; k >= 0; k--)
            {
                var o = chain[k];
                pos += rot * (DriftCityData.ToUnity(o.Translation) * scale);
                rot *= DriftCityData.ToUnity(o.Rotation);
                scale *= o.Scale;
            }
        }
    }

    /// <summary>Materials for the original's fixed-function effects: unlit, texture times vertex colour, no depth write.</summary>
    public static class EffectMaterials
    {
        /// <summary>
        /// Alpha (SRCALPHA/INVSRCALPHA) or additive (SRCALPHA/ONE) blending. A glow map of the same texture, as the
        /// sparkle and crash effects use, adds the texture on top, so it draws at double brightness.
        /// </summary>
        public static Material Create(GameObject owner, Texture tex, bool additive, bool glow = false)
        {
            var shader = Shader.Find(additive ? "Legacy Shaders/Particles/Additive" : "Legacy Shaders/Particles/Alpha Blended");
            if (!shader) shader = Shader.Find("Sprites/Default");
            var m = OwnedAssets.Track(owner, new Material(shader) { name = (tex ? tex.name : "effect") + (additive ? " add" : " blend") });
            if (tex) m.mainTexture = tex;
            // the legacy particle shaders output 2 * tint * vertex colour * texture
            if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", glow ? new Color(1f, 1f, 1f, 0.5f) : new Color(0.5f, 0.5f, 0.5f, 0.5f));
            return m;
        }
    }
}
