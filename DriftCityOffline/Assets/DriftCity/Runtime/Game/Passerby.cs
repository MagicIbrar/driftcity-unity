using System.Collections.Generic;
using DriftCity.Core;
using DriftCity.Core.Nif;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// The city's pedestrians ("passersby", DriftCity_RE/TRAFFIC.md section 10). The client reads &lt;city&gt;\passerby.pcs
    /// in the open city (game state 14, 0x449ac0), places its walkers on the pavement routes and crossings (0x7e9cf0) and
    /// moves them on a timetable of the server clock (0x7ea2f0: walk / stand cycles, crossers walk over, wait, walk
    /// back), so every player sees the same people. Only those within 100 + 15 x view option metres are made into
    /// actors (0x49a200, 0x49a500): skinned vehicle\npc\f00 / m00 bodies built from three part files (0x7eb0d0,
    /// 0x45f660) playing their .kf walk cycles. They never collide: a car coming at one makes it jump aside (10 m/s up
    /// to 9 m away from the car's line), then run back to its route (0x49aad0).
    /// </summary>
    public sealed class Passerby : MonoBehaviour
    {
        /// <summary>0x49b6e0 / 0x49a200: actors are kept within 100 + 15 x the view-distance option metres.</summary>
        public static float Radius => 100f + 15f * Trains.ViewOption;
        /// <summary>The client's density setting for 0x7e9cf0 (0 = 1500 m spacing, 1 = 700 m, 2 = 400 m).</summary>
        public static int Density = 0;
        public int MaxActors = 48;   // (inferred) the client caps its actor count; 48 covers a crowded corner

        public PasserbyFile Data { get; private set; }
        public List<PasserbyFile.Agent> Agents { get; private set; }
        public Transform Player;
        public IReadOnlyDictionary<int, Pedestrian> Active => active;

        readonly Dictionary<int, Pedestrian> active = new Dictionary<int, Pedestrian>();
        readonly List<int> nearby = new List<int>();
        readonly List<int> remove = new List<int>();
        readonly Stack<Pedestrian>[] pool = { new Stack<Pedestrian>(), new Stack<Pedestrian>() };
        HumanKit[] kits;
        float scanTimer;

        public static Passerby Attach(CitySession session, string city) => Attach(city, session ? session.Car.transform : null, session ? session.transform : null);

        public static Passerby Attach(string city, Transform player, Transform parent)
        {
            var fs = DriftCityData.Files;
            string path = $@"{city}\passerby.pcs";
            if (fs == null || !fs.Exists(path)) return null;
            var go = new GameObject("Passersby");
            if (parent) go.transform.SetParent(parent, false);
            var p = go.AddComponent<Passerby>();
            p.Player = player;
            p.Data = PasserbyFile.Parse(fs.Read(path));
            p.Agents = p.Data.CreateAgents(Density);
            p.kits = new HumanKit[2];
            for (int t = 0; t < 2; t++)
            {
                try { p.kits[t] = new HumanKit(t == 0 ? "f00" : "m00", go.transform); }
                catch (System.Exception ex) { Debug.LogWarning($"passerby model {t}: {ex.Message}"); }
            }
            Debug.Log($"Drift City: {p.Agents.Count} pedestrians on {p.Data.Routes.Count} routes in {city}");
            return p;
        }

        /// <summary>World position (Unity) of a pedestrian's timetable spot.</summary>
        public Vector3 SchedulePosition(PasserbyFile.Agent a, float time, out Vector3 dir, out bool walking)
        {
            var r = Data.Routes[a.Route];
            float pos = Data.Position(a, time, out walking);
            var w = Data.SampleRoute(r, pos, out var d);
            dir = DriftCityData.ToUnity(d);
            return DriftCityData.ToUnity(w);
        }

        void Update()
        {
            if (Data == null || !Player) return;
            float time = Trains.Now;
            var p = Player.position;
            float radius = Radius;
            // coarse scan twice a second for who is near (walkers cover < 1 m in that time, runners return to their spot)
            scanTimer -= Time.deltaTime;
            if (scanTimer <= 0)
            {
                scanTimer = 0.5f;
                nearby.Clear();
                float r2 = (radius + 20f) * (radius + 20f);
                for (int i = 0; i < Agents.Count; i++)
                {
                    var w = SchedulePosition(Agents[i], time, out _, out _);
                    if ((w - p).sqrMagnitude < r2) nearby.Add(i);
                }
            }
            float in2 = radius * radius, out2 = (radius + 10f) * (radius + 10f);
            foreach (int i in nearby)
            {
                if (active.Count >= MaxActors) break;
                if (active.ContainsKey(i)) continue;
                var w = SchedulePosition(Agents[i], time, out _, out _);
                if ((w - p).sqrMagnitude < in2) Spawn(i);
            }
            remove.Clear();
            foreach (var kv in active)
                if ((kv.Value.transform.position - p).sqrMagnitude > out2) remove.Add(kv.Key);
            foreach (int i in remove) Despawn(i);
            foreach (var ped in active.Values) ped.Tick(this, time, Time.deltaTime);
        }

        void Spawn(int index)
        {
            var a = Agents[index];
            var kit = kits[a.Type];
            if (kit == null) return;
            Pedestrian ped = pool[a.Type].Count > 0 ? pool[a.Type].Pop() : kit.Create(transform);
            ped.Dress(kit, a);
            ped.gameObject.SetActive(true);
            ped.Begin(this, a, Trains.Now);
            active[index] = ped;
        }

        void Despawn(int index)
        {
            if (!active.TryGetValue(index, out var ped)) return;
            active.Remove(index);
            if (!ped) return;
            ped.gameObject.SetActive(false);
            pool[ped.Agent.Type].Push(ped);
        }

        /// <summary>The cars pedestrians get out of the way of: the local player and the other drivers.</summary>
        internal readonly List<(Transform t, float speed, float halfLength, float halfWidth)> Cars = new List<(Transform, float, float, float)>();
        int carsFrame = -1;
        float remoteScan;
        Net.RemoteCar[] remotes;
        internal List<(Transform t, float speed, float halfLength, float halfWidth)> CarsNow()
        {
            if (carsFrame == Time.frameCount) return Cars;
            carsFrame = Time.frameCount;
            Cars.Clear();
            if (Player)
            {
                var dc = Player.GetComponent<DriftCar>();
                Cars.Add((Player, dc ? dc.SpeedKmh / 3.6f : 0f, 2.2f, 0.9f));
            }
            if (Time.time >= remoteScan) { remoteScan = Time.time + 1f; remotes = FindObjectsByType<Net.RemoteCar>(FindObjectsSortMode.None); }
            if (remotes != null)
                foreach (var rc in remotes)
                    if (rc) Cars.Add((rc.transform, rc.SpeedKmh / 3.6f, 2.2f, 0.9f));
            return Cars;
        }

        /// <summary>Ground under a point within the client's 7 m query (0x4e5980), ignoring cars.</summary>
        public static bool Ground(Vector3 p, out float y)
        {
            y = p.y;
            var hits = Physics.RaycastAll(p + Vector3.up * 7f, Vector3.down, 14f, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            foreach (var h in hits)
            {
                if (h.rigidbody) continue;
                float d = Mathf.Abs(h.point.y - p.y);
                if (d < best) { best = d; y = h.point.y; }
            }
            return best < float.MaxValue;
        }
    }

    /// <summary>One pedestrian actor: a skinned body on its own skeleton, following its route or getting out of the way.</summary>
    public sealed class Pedestrian : MonoBehaviour
    {
        public PasserbyFile.Agent Agent;
        /// <summary>0 on the route, 1 jumping aside, 2 running back (0x49aad0 state at agent+0x44).</summary>
        public int State;
        public string Clip;
        internal Transform Skeleton;
        internal Dictionary<string, Transform> Bones;
        readonly SkinnedMeshRenderer[] parts = new SkinnedMeshRenderer[4];
        HumanKit kit;
        HumanClip clip;
        Transform[] clipBones;
        float clipTime;
        Vector3 dodgeFrom, dodgeDir;
        float dodgeStart, dodgeElapsed;
        float yaw;

        internal void Setup(Transform skeleton, Dictionary<string, Transform> bones)
        {
            Skeleton = skeleton; Bones = bones;
            for (int s = 0; s < 4; s++)
            {
                var go = new GameObject("Part " + s);
                go.transform.SetParent(transform, false);
                parts[s] = go.AddComponent<SkinnedMeshRenderer>();
                parts[s].updateWhenOffscreen = false;
            }
        }

        /// <summary>The four parts 0x7eb0d0 picked (hair, face, upper and lower body), each from its part file.</summary>
        internal void Dress(HumanKit k, PasserbyFile.Agent a)
        {
            kit = k; Agent = a;
            for (int s = 0; s < 4; s++)
            {
                var part = k.Part(a.Parts[s], s);
                var smr = parts[s];
                if (part == null) { smr.enabled = false; continue; }
                smr.enabled = true;
                smr.sharedMesh = part.Mesh;
                smr.sharedMaterials = part.Materials;
                var bones = new Transform[part.BoneNames.Length];
                for (int b = 0; b < bones.Length; b++) Bones.TryGetValue(part.BoneNames[b], out bones[b]);
                smr.bones = bones;
                smr.rootBone = transform;
                smr.localBounds = new Bounds(new Vector3(0, 0.9f, 0), new Vector3(2f, 2.2f, 2f));
            }
            clip = null; Clip = null;
        }

        internal void Begin(Passerby sys, PasserbyFile.Agent a, float time)
        {
            State = 0;
            var p = sys.SchedulePosition(a, time, out var dir, out _);
            transform.position = p;
            yaw = Yaw(dir, 0);
            transform.rotation = Quaternion.Euler(0, yaw, 0);
            clipTime = Random.value * 2f;
        }

        static float Yaw(Vector3 dir, float fallback)
        {
            dir.y = 0;
            return dir.sqrMagnitude > 1e-6f ? Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg : fallback;
        }

        internal void Tick(Passerby sys, float time, float dt)
        {
            var a = Agent;
            var target = sys.SchedulePosition(a, time, out var dir, out bool walking);
            var type = PasserbyFile.Types[a.Type];
            string want;
            switch (State)
            {
                default:
                    transform.position = target;
                    yaw = Yaw(dir, yaw);
                    want = walking ? "walk01" : "idle01";
                    CheckCars(sys, time);
                    break;
                case 1:
                {
                    // jump aside: away from the car's line at the type's slot-3 speed until 9 m out (0x49aad0 state 1)
                    dodgeElapsed += dt;
                    var p = dodgeFrom + dodgeDir * type.Speed[3] * dodgeElapsed;
                    if (!Passerby.Ground(p, out float gy)) { State = 0; goto default; }
                    p.y = gy;
                    transform.position = p;
                    yaw = Yaw(dodgeDir, yaw);
                    want = kit.Has("avoid01") ? "avoid01" : "run01";
                    if ((p - dodgeFrom).sqrMagnitude > 81f) State = 2;
                    break;
                }
                case 2:
                {
                    // run back to the timetable spot (state 2), then walk on
                    var p = transform.position;
                    var to = target - p; to.y = 0;
                    float step = type.Speed[3] * dt;
                    if (to.magnitude <= step) { State = 0; transform.position = target; want = "walk01"; break; }
                    p += to.normalized * step;
                    if (Passerby.Ground(p, out float gy)) p.y = gy;
                    transform.position = p;
                    yaw = Yaw(to, yaw);
                    want = "run01";
                    break;
                }
            }
            transform.rotation = Quaternion.Euler(0, yaw + kit.FacingOffset, 0);
            Play(want, dt);
        }

        /// <summary>0x49aad0 state 0: a car whose front reaches within half its length + 0.7 m (or ahead of it, within half
        /// its width + 2 m sideways) sends the pedestrian off sideways, away from the car's line.</summary>
        void CheckCars(Passerby sys, float time)
        {
            var me = transform.position;
            foreach (var (t, speed, halfLength, halfWidth) in sys.CarsNow())
            {
                if (!t) continue;
                var fwd = t.forward; fwd.y = 0;
                if (fwd.sqrMagnitude < 1e-6f) continue;
                fwd.Normalize();
                var off = me - t.position; off.y = 0;
                float d = off.magnitude;
                if (Mathf.Abs(me.y - t.position.y) > 3f) continue;
                float near = halfLength + 0.2f + 0.5f;                 // (inferred) car half length + type[5] x type[3] + 0.5
                float ahead = speed * 0.5f + near;                      // (inferred) look-ahead of half a second at the car's speed
                float along = Vector3.Dot(off, fwd);
                float lateral = Mathf.Sqrt(Mathf.Max(0, d * d - along * along));
                if (d < near || (d < ahead && along > 0 && lateral < halfWidth + 2f))
                {
                    var side = Vector3.Cross(Vector3.up, fwd);
                    if (Vector3.Dot(side, off) < 0) side = -side;
                    dodgeDir = side.normalized;
                    dodgeFrom = me;
                    dodgeStart = time;
                    dodgeElapsed = 0;
                    State = 1;
                    return;
                }
            }
        }

        void Play(string name, float dt)
        {
            if (Clip != name)
            {
                var c = kit.Clip(name) ?? kit.Clip("walk01");
                if (c != clip)
                {
                    clip = c;
                    clipBones = c?.Bind(Bones);
                    clipTime = 0;
                }
                Clip = name;
            }
            if (clip == null) return;
            clipTime += dt;
            clip.Apply(clipBones, clipTime);
        }
    }

    /// <summary>
    /// The model pieces of one pedestrian type (vehicle\npc\f00 or m00): the skeleton, the skinned meshes of its three
    /// part files (hair, face, ubody, lbody in each) and the .kf animations.
    /// </summary>
    public sealed class HumanKit
    {
        public sealed class SkinnedPart { public Mesh Mesh; public Material[] Materials; public string[] BoneNames; }

        static readonly string[] SlotMesh = { "hair", "face", "ubody", "lbody" };   // (inferred) slots 0xc..0xf
        readonly string prefix;
        readonly Transform holder;
        readonly GameObject skeletonTemplate;
        readonly Dictionary<(int, int), SkinnedPart> parts = new Dictionary<(int, int), SkinnedPart>();
        readonly Dictionary<string, HumanClip> clips = new Dictionary<string, HumanClip>();
        /// <summary>Degrees added to the walking direction so the model faces where it goes (the biped bodies face -Y).</summary>
        public float FacingOffset = 180f;

        public HumanKit(string prefix, Transform parent)
        {
            this.prefix = prefix;
            var fs = DriftCityData.Files;
            var h = new GameObject("Passerby kit " + prefix);
            h.transform.SetParent(parent, false);
            h.SetActive(false);
            holder = h.transform;
            for (int file = 0; file < 3; file++)
            {
                string nifPath = $@"vehicle\npc\{prefix}_parts{file:00}.nif";
                if (!fs.Exists(nifPath)) continue;
                var nif = NifFile.Parse(fs.Read(nifPath));
                var built = new NifSceneBuilder { LogErrors = false }.Load(nifPath, holder);
                if (file == 0) skeletonTemplate = built;
                for (int s = 0; s < 4; s++)
                {
                    var part = BuildPart(nif, built.transform, SlotMesh[s]);
                    if (part != null) parts[(file, s)] = part;
                }
            }
            if (!skeletonTemplate) throw new System.IO.FileNotFoundException($"vehicle\\npc\\{prefix}_parts00.nif");
            foreach (var name in new[] { "walk01", "walk02", "idle01", "idle02", "run01", "avoid01", "hit01" })
            {
                string kf = $@"vehicle\npc\{prefix}_{name}.kf";
                if (fs.Exists(kf)) { var c = HumanClip.Load(fs.Read(kf)); if (c != null) clips[name] = c; }
            }
        }

        public bool Has(string clip) => clips.ContainsKey(clip);
        public HumanClip Clip(string name) => clips.TryGetValue(name, out var c) ? c : null;

        /// <summary>Part number p (1..3 woman, 4..6 man = part file (p - 1) % 3) of a slot; a file without that mesh (m00_parts02
        /// has no face) falls back to part file 0.</summary>
        public SkinnedPart Part(int p, int slot)
        {
            int file = Mathf.Clamp((p - 1) % 3, 0, 2);
            if (parts.TryGetValue((file, slot), out var part)) return part;
            return parts.TryGetValue((0, slot), out part) ? part : null;
        }

        /// <summary>A copy of the shape's mesh with bone weights from its NiSkinData and bind poses from the file's rest pose.</summary>
        static SkinnedPart BuildPart(NifFile nif, Transform built, string meshName)
        {
            NiGeometry geom = null;
            foreach (var b in nif.Blocks) if (b is NiGeometry g && g.Name == meshName && g.SkinInstance >= 0) { geom = g; break; }
            if (geom == null) return null;
            var skin = nif.Get<NiSkinInstance>(geom.SkinInstance);
            var data = skin != null ? nif.Get<NiSkinData>(skin.Data) : null;
            if (data == null) return null;
            Transform shape = null;
            foreach (var t in built.GetComponentsInChildren<Transform>(true))
                if (t.name == meshName && t.GetComponent<MeshFilter>()) { shape = t; break; }
            if (!shape) return null;
            var src = shape.GetComponent<MeshFilter>().sharedMesh;
            var mesh = Object.Instantiate(src);
            mesh.name = meshName;
            int n = mesh.vertexCount;

            var names = new string[skin.Bones.Length];
            var bind = new Matrix4x4[names.Length];
            var nodes = new Dictionary<string, Transform>();
            foreach (var t in built.GetComponentsInChildren<Transform>(true)) if (!nodes.ContainsKey(t.name)) nodes[t.name] = t;
            for (int i = 0; i < names.Length; i++)
            {
                names[i] = nif.Get<NiAVObject>(skin.Bones[i])?.Name ?? "";
                bind[i] = nodes.TryGetValue(names[i], out var bt) ? bt.worldToLocalMatrix * shape.localToWorldMatrix : Matrix4x4.identity;
            }
            // up to four strongest influences per vertex
            var infl = new List<(int bone, float w)>[n];
            for (int b = 0; b < data.Bones.Length && b < names.Length; b++)
            {
                var db = data.Bones[b];
                for (int k = 0; k < db.Vertices.Length; k++)
                {
                    int v = db.Vertices[k];
                    if (v >= n) continue;
                    (infl[v] ??= new List<(int, float)>()).Add((b, db.Weights[k]));
                }
            }
            var weights = new BoneWeight[n];
            for (int v = 0; v < n; v++)
            {
                var l = infl[v];
                if (l == null || l.Count == 0) { weights[v] = new BoneWeight { boneIndex0 = 0, weight0 = 1 }; continue; }
                l.Sort((x, y) => y.w.CompareTo(x.w));
                float sum = 0; for (int k = 0; k < l.Count && k < 4; k++) sum += l[k].w;
                if (sum <= 0) sum = 1;
                var w = new BoneWeight();
                if (l.Count > 0) { w.boneIndex0 = l[0].bone; w.weight0 = l[0].w / sum; }
                if (l.Count > 1) { w.boneIndex1 = l[1].bone; w.weight1 = l[1].w / sum; }
                if (l.Count > 2) { w.boneIndex2 = l[2].bone; w.weight2 = l[2].w / sum; }
                if (l.Count > 3) { w.boneIndex3 = l[3].bone; w.weight3 = l[3].w / sum; }
                weights[v] = w;
            }
            mesh.boneWeights = weights;
            mesh.bindposes = bind;
            return new SkinnedPart { Mesh = mesh, Materials = shape.GetComponent<MeshRenderer>().sharedMaterials, BoneNames = names };
        }

        /// <summary>A new pedestrian with its own copy of the skeleton (the biped helper shapes removed).</summary>
        public Pedestrian Create(Transform parent)
        {
            var go = new GameObject("Pedestrian " + prefix);
            go.transform.SetParent(parent, false);
            Transform bip = null;
            foreach (var t in skeletonTemplate.GetComponentsInChildren<Transform>(true)) if (t.name == "Bip01") { bip = t; break; }
            // the skeleton keeps its pose relative to the file's root (Scene Root may carry a transform)
            var skel = Object.Instantiate(bip.gameObject, go.transform).transform;
            skel.name = "Bip01";
            var rel = skeletonTemplate.transform.worldToLocalMatrix * bip.localToWorldMatrix;
            skel.localPosition = rel.GetColumn(3);
            skel.localRotation = rel.rotation;
            skel.localScale = bip.lossyScale;
            foreach (var r in skel.GetComponentsInChildren<Renderer>(true)) Object.Destroy(r.gameObject);
            var bones = new Dictionary<string, Transform>();
            foreach (var t in skel.GetComponentsInChildren<Transform>(true)) if (!bones.ContainsKey(t.name)) bones[t.name] = t;
            var ped = go.AddComponent<Pedestrian>();
            ped.Setup(skel, bones);
            go.SetActive(false);
            return ped;
        }
    }

    /// <summary>A character .kf animation: one NiControllerSequence whose blocks drive bones by name, mostly through
    /// compressed B-spline interpolators (NiBSplineCompTransformInterpolator).</summary>
    public sealed class HumanClip
    {
        public string Name, AccumRoot;
        public float Start, Stop, Frequency = 1f;
        readonly List<(string bone, NiObject interp, NiBSplineData spline, int count, NiTransformData keys)> tracks = new List<(string, NiObject, NiBSplineData, int, NiTransformData)>();
        readonly float[] buf = new float[4];

        public static HumanClip Load(byte[] kf)
        {
            var nif = NifFile.Parse(kf);
            NiControllerSequence seq = null;
            foreach (var b in nif.Blocks) if (b is NiControllerSequence s) { seq = s; break; }
            if (seq == null) return null;
            var clip = new HumanClip { Name = seq.Name, AccumRoot = seq.AccumRootName, Start = seq.StartTime, Stop = seq.StopTime, Frequency = seq.Frequency > 0 ? seq.Frequency : 1f };
            foreach (var blk in seq.Blocks)
            {
                var pal = nif.Get<NiStringPalette>(blk.Palette >= 0 ? blk.Palette : seq.StringPalette);
                string bone = pal?.At(blk.NodeOffset);
                if (string.IsNullOrEmpty(bone)) continue;
                var ip = nif.Get<NiObject>(blk.Interpolator);
                if (ip is NiBSplineInterpolator bs)
                {
                    var sd = nif.Get<NiBSplineData>(bs.SplineData);
                    var bd = nif.Get<NiBSplineBasisData>(bs.BasisData);
                    if (sd != null && bd != null) clip.tracks.Add((bone, ip, sd, bd.ControlPoints, null));
                }
                else if (ip is NiTransformInterpolator ti)
                    clip.tracks.Add((bone, ip, null, 0, nif.Get<NiTransformData>(ti.Data)));
            }
            return clip;
        }

        public Transform[] Bind(Dictionary<string, Transform> bones)
        {
            var r = new Transform[tracks.Count];
            for (int i = 0; i < r.Length; i++) bones.TryGetValue(tracks[i].bone, out r[i]);
            return r;
        }

        public void Apply(Transform[] bones, float time)
        {
            float span = Stop - Start;
            float t = span > 1e-5f ? Start + Mathf.Repeat(time * Frequency, span) : Start;
            for (int i = 0; i < tracks.Count; i++)
            {
                var bone = bones[i];
                if (!bone) continue;
                var (name, ip, spline, count, keys) = tracks[i];
                // the accumulation root (Bip01) carries the actor's own motion in Gamebryo (its track is a constant
                // identity at the origin): it takes the track's rotation but keeps its rest height, the actor moves
                bool accum = name == AccumRoot;
                if (ip is NiBSplineCompTransformInterpolator c)
                {
                    float u = c.StopTime - c.StartTime > 1e-5f ? (t - c.StartTime) / (c.StopTime - c.StartTime) : 0f;
                    Vec3 tr = c.Translation; Quat q = c.Rotation; bool hasT = Valid(c.Translation.X), hasR = Valid(c.Rotation.W);
                    if (NiBSplineTransformInterpolator.Valid(c.TranslationHandle))
                    {
                        BSpline.Sample(spline.Compact, c.TranslationHandle, count, 3, c.TranslationOffset, c.TranslationHalfRange, u, buf);
                        tr = new Vec3(buf[0], buf[1], buf[2]); hasT = true;
                    }
                    if (NiBSplineTransformInterpolator.Valid(c.RotationHandle))
                    {
                        BSpline.Sample(spline.Compact, c.RotationHandle, count, 4, c.RotationOffset, c.RotationHalfRange, u, buf);
                        q = new Quat { W = buf[0], X = buf[1], Y = buf[2], Z = buf[3] }; hasR = true;
                    }
                    Set(bone, tr, hasT, q, hasR, accum);
                }
                else if (ip is NiTransformInterpolator ti)
                {
                    Vec3 tr = ti.Translation; Quat q = ti.Rotation;
                    bool hasT = Valid(tr.X), hasR = Valid(q.W);
                    if (keys != null)
                    {
                        if (keys.Translations != null && keys.Translations.Count > 0) { tr = SampleV(keys.Translations, t); hasT = true; }
                        if (keys.Rotations.Count > 0) { q = SampleQ(keys.Rotations, t); hasR = true; }
                    }
                    Set(bone, tr, hasT, q, hasR, accum);
                }
            }
        }

        static bool Valid(float f) => f > -1e30f && f < 1e30f;

        /// <summary>The accumulation root (Bip01) keeps its rest position across the ground: the client moves the actor
        /// itself, so only the bob up and down is played there.</summary>
        static void Set(Transform bone, Vec3 tr, bool hasT, Quat q, bool hasR, bool accum)
        {
            if (hasT)
            {
                var p = DriftCityData.ToUnity(tr);
                if (!accum) bone.localPosition = p;
            }
            if (hasR)
            {
                float l = Mathf.Sqrt(q.W * q.W + q.X * q.X + q.Y * q.Y + q.Z * q.Z);
                if (l > 1e-6f) { q.W /= l; q.X /= l; q.Y /= l; q.Z /= l; }
                // the .kf quaternions give the transpose of the matrices the NIFs store (checked against the part files'
                // rest pose: walk01 at t = 0 equals the rest rotation of every bone once transposed; harness mode "kf")
                bone.localRotation = DriftCityData.ToUnity(Transpose(ToMatrix(q)));
            }
        }

        static Vec3 SampleV(KeyGroup<Vec3> g, float t)
        {
            int n = g.Count;
            if (n == 1 || t <= g.Times[0]) return g.Values[0];
            if (t >= g.Times[n - 1]) return g.Values[n - 1];
            int i = 1; while (i < n - 1 && g.Times[i] < t) i++;
            float k = (t - g.Times[i - 1]) / Mathf.Max(1e-6f, g.Times[i] - g.Times[i - 1]);
            var a = g.Values[i - 1]; var b = g.Values[i];
            return new Vec3(Mathf.Lerp(a.X, b.X, k), Mathf.Lerp(a.Y, b.Y, k), Mathf.Lerp(a.Z, b.Z, k));
        }

        static Quat SampleQ(KeyGroup<Quat> g, float t)
        {
            int n = g.Count;
            if (n == 1 || t <= g.Times[0]) return g.Values[0];
            if (t >= g.Times[n - 1]) return g.Values[n - 1];
            int i = 1; while (i < n - 1 && g.Times[i] < t) i++;
            float k = (t - g.Times[i - 1]) / Mathf.Max(1e-6f, g.Times[i] - g.Times[i - 1]);
            var a = g.Values[i - 1]; var b = g.Values[i];
            var q = Quaternion.Slerp(new Quaternion(a.X, a.Y, a.Z, a.W), new Quaternion(b.X, b.Y, b.Z, b.W), k);
            return new Quat { W = q.w, X = q.x, Y = q.y, Z = q.z };
        }

        static Mat3 Transpose(Mat3 m) => new Mat3 { M00 = m.M00, M01 = m.M10, M02 = m.M20, M10 = m.M01, M11 = m.M11, M12 = m.M21, M20 = m.M02, M21 = m.M12, M22 = m.M22 };

        /// <summary>Gamebryo's NiQuaternion::ToRotation (same as NifAnimator).</summary>
        static Mat3 ToMatrix(Quat q)
        {
            float w = q.W, x = q.X, y = q.Y, z = q.Z;
            return new Mat3
            {
                M00 = 1 - 2 * (y * y + z * z), M01 = 2 * (x * y + w * z), M02 = 2 * (x * z - w * y),
                M10 = 2 * (x * y - w * z), M11 = 1 - 2 * (x * x + z * z), M12 = 2 * (y * z + w * x),
                M20 = 2 * (x * z + w * y), M21 = 2 * (y * z - w * x), M22 = 1 - 2 * (x * x + y * y),
            };
        }
    }
}
