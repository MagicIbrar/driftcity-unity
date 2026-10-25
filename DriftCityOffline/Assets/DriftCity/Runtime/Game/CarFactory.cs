using System.Collections.Generic;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>Assembles a player car from its body NIF, tire NIFs and the wheel nodes "01".."04".</summary>
    public static class CarFactory
    {
        public const float DefaultTireRadius = 0.32f;

        /// <summary>Fallback paint slots when the colour tables are missing (the real ones are VehicleInfo.DefaultColors).</summary>
        public static readonly Color[] Paints =
        {
            new Color(0.12f, 0.32f, 0.85f), new Color(0.92f, 0.92f, 0.92f), new Color(0.82f, 0.10f, 0.10f), new Color(0.10f, 0.10f, 0.11f),
        };

        public static GameObject BuildVisual(string bodyNif, string tireNif, Vector3 position, Quaternion rotation,
                                             out Transform[] wheelNodes, out Transform[] tires, float tireRadius = DefaultTireRadius, string dummyNif = null)
        {
            var fs = DriftCityData.Files;
            var root = new GameObject("Car " + System.IO.Path.GetFileNameWithoutExtension(bodyNif));
            root.transform.SetPositionAndRotation(position, rotation);
            var body = new NifSceneBuilder().Load(bodyNif, root.transform);
            body.name = "Body";

            wheelNodes = FindWheels(body.transform);
            // <model>_dm.nif holds the car's mount points (wheels, exhausts, plates, spoiler, roof); aero-kit bodies
            // have no wheel nodes of their own
            if (dummyNif != null && fs.Exists(dummyNif))
            {
                var dummies = new NifSceneBuilder().Load(dummyNif, root.transform);
                dummies.name = "Dummies";
                foreach (var r in dummies.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
                if (System.Array.IndexOf(wheelNodes, null) >= 0) wheelNodes = FindWheels(dummies.transform);
            }
            if (System.Array.IndexOf(wheelNodes, null) >= 0)
            {
                // some bodies ship without wheel nodes: place wheels at the corners of the body's footprint
                var rs = body.GetComponentsInChildren<Renderer>();
                if (rs.Length > 0)
                {
                    var b = BodyBounds(root.transform, rs);
                    var local = b.center;
                    float hx = b.extents.x * 0.82f, hz = b.extents.z * 0.68f, y = local.y - b.extents.y + 0.32f;
                    var corners = new[] { new Vector3(-hx, y, hz), new Vector3(hx, y, hz), new Vector3(-hx, y, -hz), new Vector3(hx, y, -hz) };
                    for (int k = 0; k < 4; k++)
                    {
                        if (wheelNodes[k]) continue;
                        var node = new GameObject("wheel " + (k + 1)).transform;
                        node.SetParent(root.transform, false);
                        node.localPosition = new Vector3(local.x, 0, local.z) + corners[k];
                        wheelNodes[k] = node;
                    }
                }
            }

            tires = new Transform[4];
            for (int i = 0; i < 4; i++)
            {
                if (!wheelNodes[i] || !fs.Exists(tireNif)) continue;
                var tire = new NifSceneBuilder().Load(tireNif, root.transform);
                tire.transform.position = wheelNodes[i].position;
                // the wheel models carry the rim on their +x face: left-side wheels (01, 03) are turned to face out
                tire.transform.rotation = root.transform.rotation * (i % 2 == 0 ? Quaternion.Euler(0, 180, 0) : Quaternion.identity);
                var rs = tire.GetComponentsInChildren<Renderer>();
                if (rs.Length > 0)
                {
                    var b = LocalBounds(tire.transform, rs);
                    float meshRadius = Mathf.Max(b.extents.y, b.extents.z) * tire.transform.localScale.y;
                    if (meshRadius > 0.01f) tire.transform.localScale = Vector3.one * (tireRadius / meshRadius);
                }
                tires[i] = tire.transform;
            }
            return root;
        }

        /// <summary>Bounds of the renderers' meshes expressed in <paramref name="space"/>'s local coordinates.</summary>
        public static Bounds LocalBounds(Transform space, Renderer[] renderers)
        {
            bool any = false;
            var b = new Bounds();
            foreach (var r in renderers)
            {
                if (!r.TryGetComponent<MeshFilter>(out var mf) || !mf.sharedMesh) continue;
                var mb = mf.sharedMesh.bounds;
                var m = space.worldToLocalMatrix * r.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    var corner = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var p = m.MultiplyPoint3x4(corner);
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p);
                }
            }
            return b;
        }

        /// <summary>The car body's outline mesh (its full silhouette) when present, else all body meshes; car-local.</summary>
        static Bounds BodyBounds(Transform root, Renderer[] renderers)
        {
            var outline = System.Array.FindAll(renderers, r => r.name == "OutlineMesh");
            return LocalBounds(root, outline.Length > 0 ? outline : renderers);
        }

        /// <summary>A named node on the body, else in the car's dummy file.</summary>
        public static Transform FindNode(GameObject car, string name)
        {
            foreach (var part in new[] { "Body", "Dummies" })
            {
                var root = car.transform.Find(part);
                if (!root) continue;
                foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name == name) return t;
            }
            return null;
        }

        static Transform[] FindWheels(Transform under)
        {
            var nodes = new Transform[4];
            foreach (var t in under.GetComponentsInChildren<Transform>(true))
                if (t.parent && t.parent.name == "wheels" && int.TryParse(t.name, out int i) && i >= 1 && i <= 4)
                    nodes[i - 1] = t;
            return nodes;
        }

        /// <summary>One of the car's four factory colours (vehiclelist Default_Color ids in effects\VehicleColor.nxt).</summary>
        public static Color FactoryColor(VehicleInfo v, int slot)
        {
            slot = Mathf.Clamp(slot, 0, 3);
            return v != null && VehicleColors.TryGet(VehicleColors.Table.Paint, v.DefaultColors[slot], out var c) ? c : Paints[slot];
        }

        public static GameObject BuildVisual(VehicleInfo v, int paint, Vector3 position, Quaternion rotation)
        {
            var go = BuildVisual(v, null, position, rotation, out _, out _);
            ApplyPaint(go, FactoryColor(v, paint));
            return go;
        }

        /// <summary>A car with its parts, paint, decal and tint (remote players, shop previews).</summary>
        public static GameObject BuildDressed(VehicleInfo v, OwnedCar owned, Vector3 position, Quaternion rotation, out Transform[] tires)
        {
            var go = BuildVisual(v, owned, position, rotation, out _, out tires);
            CarPaint.Apply(go, v, owned);
            return go;
        }

        /// <summary>A car with its fitted custom-shop parts (owned null = factory spec); paint is applied by the caller.</summary>
        public static GameObject BuildVisual(VehicleInfo v, OwnedCar owned, Vector3 position, Quaternion rotation, out Transform[] wheelNodes, out Transform[] tires)
        {
            var fs = DriftCityData.Files;
            var aero = PartsCatalog.Effective(v, owned, PartSlot.Aero);
            string body = v.BodyNif(aero?.Variant ?? 1);
            if (!fs.Exists(body)) body = v.BodyNif();
            // no body model: the simple model stands in, as 0x45fa60 falls back to 0x45cd60
            if (!fs.Exists(body) && fs.Exists($@"vehicle\player\{v.ModelBase}_s.nif")) body = $@"vehicle\player\{v.ModelBase}_s.nif";
            var wheels = PartsCatalog.Effective(v, owned, PartSlot.Wheels);
            var go = BuildVisual(body, wheels?.Nif ?? GameTables.TireNif(v), position, rotation, out wheelNodes, out tires, v.TireRadius, $@"vehicle\player\{v.ModelBase}_dm.nif");
            foreach (var slot in new[] { PartSlot.Hood, PartSlot.Wing, PartSlot.Duct, PartSlot.Roof })
            {
                var part = PartsCatalog.Effective(v, owned, slot);
                if (part?.Nif == null || !fs.Exists(part.Nif)) continue;
                if (part.Mount == null) { new NifSceneBuilder().Load(part.Nif, go.transform).name = slot.ToString(); continue; }
                // universal parts sit on the car's dummy node (spoiler / fc / light01)
                var node = FindNode(go, part.Mount) ?? (slot == PartSlot.Roof ? FindNode(go, "fc") : null);
                if (!node) continue;
                var holder = new GameObject(slot.ToString()).transform;
                holder.SetParent(node, false);
                if (slot == PartSlot.Roof)
                {
                    // the roof-light node is off to one side; roof items are centred on the roof
                    var local = go.transform.InverseTransformPoint(node.position);
                    holder.position = go.transform.TransformPoint(new Vector3(0, local.y, local.z));
                }
                new NifSceneBuilder().Load(part.Nif, holder);
            }
            var plate = PartsCatalog.Effective(v, owned, PartSlot.Plate);
            if (plate?.Nif != null && fs.Exists(plate.Nif))
                foreach (var (nodeName, turn) in new[] { ("num_front", 0f), ("num_back", 180f) })
                {
                    var node = FindNode(go, nodeName);
                    if (!node) continue;
                    var holder = new GameObject("Plate " + nodeName).transform;
                    holder.SetParent(node, false);
                    holder.rotation = go.transform.rotation * Quaternion.Euler(0, turn, 0);
                    new NifSceneBuilder().Load(plate.Nif, holder);
                }
            // a bought neon in its colour (CarPaint.AttachNeon lights the emissive-only mesh with the tint)
            var neon = PartsCatalog.Effective(v, owned, PartSlot.Neon);
            if (neon != null) CarPaint.AttachNeon(go, v, neon.Tint);
            AttachBoosterFlames(go, PartsCatalog.Effective(v, owned, PartSlot.Booster)?.Nif ?? @"vehicle\booster\i_d_f_0000.nif");
            CarLights.Attach(go, $@"vehicle\player\{v.ModelBase}_light.nif");
            AttachSimpleModel(go, v, tires);
            return go;
        }

        /// <summary>Distance LOD with the simple model (tests and previews can turn it off).</summary>
        public static bool SimpleLod = true;
        /// <summary>Screen heights of the LOD switches: the simple model's LODDistance = 100 m (its NiStringExtraData) for a
        /// car about 1.5 m high under the chase camera's 47 degree view, culled past the 512 m fog.</summary>
        public const float SimpleLodHeight = 0.0172f, CullHeight = 0.0034f;

        /// <summary>
        /// The car's simple model, vehicle\player\&lt;model&gt;_s.nif (one vertex-coloured mesh, "LODDistance = 100.0"): the
        /// client builds it in 0x45cd60, the fallback of 0x45fa60 when the car's model info can't be read ("carmodel GetInfo
        /// Fail"), cached per model and cloned. The remake uses it as that fallback (body NIF missing, BuildVisual) and as
        /// the distance LOD beyond 100 m [remake]: LOD0 = the full car, LOD1 = the simple body with the wheels.
        /// </summary>
        public static GameObject AttachSimpleModel(GameObject car, VehicleInfo v, Transform[] tires = null)
        {
            var fs = DriftCityData.Files;
            string nif = v != null ? $@"vehicle\player\{v.ModelBase}_s.nif" : null;
            if (!SimpleLod || !car || nif == null || fs == null || !fs.Exists(nif) || car.GetComponent<LODGroup>()) return null;
            GameObject simple;
            try { simple = new NifSceneBuilder().Load(nif, car.transform); }
            catch (System.Exception ex) { Debug.LogWarning($"{nif}: {ex.Message}"); return null; }
            simple.name = "Simple Model";
            var lod1 = new List<Renderer>(simple.GetComponentsInChildren<Renderer>(true));
            foreach (var r in lod1) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var lod0 = new List<Renderer>();
            foreach (var r in car.GetComponentsInChildren<Renderer>(true))
                if (!r.transform.IsChildOf(simple.transform)) lod0.Add(r);
            if (tires != null)
                foreach (var t in tires) if (t) lod1.AddRange(t.GetComponentsInChildren<Renderer>(true));
            if (lod1.Count == 0 || lod0.Count == 0) { Object.Destroy(simple); return null; }
            var group = car.AddComponent<LODGroup>();
            group.SetLODs(new[] { new LOD(SimpleLodHeight, lod0.ToArray()), new LOD(CullHeight, lod1.ToArray()) });
            group.RecalculateBounds();
            return simple;
        }

        /// <summary>Booster flame effects on the body's boost01/boost02 exhaust nodes, shown only while boosting.</summary>
        static void AttachBoosterFlames(GameObject car, string nif)
        {
            if (!DriftCityData.Files.Exists(nif)) return;
            var nodes = new List<Transform>();
            foreach (var name in new[] { "boost01", "boost02" }) { var n = FindNode(car, name); if (n) nodes.Add(n); }
            if (nodes.Count == 0) return;
            var fx = car.AddComponent<BoosterFlames>();
            foreach (var node in nodes)
            {
                var flame = new NifSceneBuilder().Load(nif, node);
                flame.name = "Booster Flame";
                // the flame's planes fade, scale and flip through their keyframe controllers (NifAnimator)
                foreach (var r in flame.GetComponentsInChildren<Renderer>(true)) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                if (flame.TryGetComponent<NifAnimator>(out var anim)) anim.LocalClock = true;   // the burst plays from each boost start
                // the flame's particle part (PArray01 etc.) plays while the flame object is shown
                NifParticleEffect.Spawn(nif, flame.transform.position, flame.transform.rotation, flame.transform);
                flame.SetActive(false);
                fx.Flames.Add(flame);
            }
        }

        /// <summary>Tints the body panels that use the shared car paint texture (car_color*).</summary>
        public static void ApplyPaint(GameObject car, int paint) => ApplyPaint(car, Paints[Mathf.Clamp(paint, 0, Paints.Length - 1)]);

        public static void ApplyPaint(GameObject car, Color color)
        {
            foreach (var r in car.GetComponentsInChildren<Renderer>(true))
            {
                var m = r.sharedMaterial;
                var tex = m ? m.GetTexture("_MainTex") : null;
                if (!tex || !tex.name.ToLowerInvariant().Contains("car_color")) continue;
                // repainting reuses this car's own copy instead of stacking new ones
                var copy = m.name.EndsWith(" paint") ? m : OwnedAssets.Track(car, new Material(m) { name = m.name + " paint" });
                SetPaint(copy, color);
                r.sharedMaterial = copy;
            }
        }

        /// <summary>
        /// The car shader takes the paint as its MaterialColor (shown where the paint texture's alpha is 0), as the
        /// client sets it on "body0" meshes; fixed-function panels take it as their diffuse colour.
        /// </summary>
        public static void SetPaint(Material m, Color color)
        {
            if (m.HasProperty("_MaterialColor")) m.SetColor("_MaterialColor", color);
            else m.SetColor("_Color", color);
        }

        /// <summary>Adds rigidbody, chassis collider, WheelColliders and the drift controller.</summary>
        public static DriftCar AddPhysics(GameObject root, Transform[] wheelNodes, Transform[] tires, float tireRadius = DefaultTireRadius)
        {
            var rb = root.AddComponent<Rigidbody>();
            rb.mass = 1250f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            var body = root.transform.Find("Body");
            var renderers = (body ? body.gameObject : root).GetComponentsInChildren<Renderer>();
            if (renderers.Length > 0)
            {
                // measured in the car's own space: world bounds of a car built at an angle would be far too wide
                var bounds = BodyBounds(root.transform, renderers);
                var box = root.AddComponent<BoxCollider>();
                var local = bounds.center;
                box.center = local + Vector3.up * 0.15f;
                box.size = new Vector3(bounds.size.x * 0.9f, Mathf.Max(0.3f, bounds.size.y - 0.4f), bounds.size.z * 0.95f);
                rb.centerOfMass = new Vector3(local.x, 0.25f, local.z);
            }

            var car = root.AddComponent<DriftCar>();
            for (int i = 0; i < 4; i++)
            {
                if (!wheelNodes[i]) continue;
                var wgo = new GameObject("WheelCollider " + (i + 1));
                wgo.transform.SetParent(root.transform, false);
                wgo.transform.position = wheelNodes[i].position + root.transform.up * 0.15f;
                var wc = wgo.AddComponent<WheelCollider>();
                wc.radius = tireRadius;
                wc.suspensionDistance = 0.18f;
                wc.mass = 20f;
                var spring = wc.suspensionSpring; spring.spring = 38000f; spring.damper = 4200f; spring.targetPosition = 0.45f; wc.suspensionSpring = spring;
                var fwd = wc.forwardFriction; fwd.stiffness = 1.6f; wc.forwardFriction = fwd;
                car.Wheels[i] = wc;
                car.WheelVisuals[i] = tires[i];
                if (tires[i]) car.WheelVisualOffsets[i] = Quaternion.Inverse(root.transform.rotation) * tires[i].rotation;
            }
            car.SetSpawn(root.transform.position, root.transform.rotation);
            return car;
        }

        public static DriftCar BuildDrivable(VehicleInfo v, int paint, Vector3 position, Quaternion rotation)
        {
            var go = BuildVisual(v, null, position, rotation, out var nodes, out var tires);
            ApplyPaint(go, FactoryColor(v, paint));
            go.name = "Player Car";
            var drivable = AddPhysics(go, nodes, tires, v.TireRadius);
            drivable.CamN = v.CamN; drivable.CamH = v.CamH;
            return drivable;
        }

        /// <summary>The player's own car with its fitted parts (paint is applied by GameFlow.RepaintCar).</summary>
        public static DriftCar BuildDrivable(VehicleInfo v, OwnedCar owned, Vector3 position, Quaternion rotation)
        {
            var go = BuildVisual(v, owned, position, rotation, out var nodes, out var tires);
            go.name = "Player Car";
            var drivable = AddPhysics(go, nodes, tires, v.TireRadius);
            drivable.CamN = v.CamN; drivable.CamH = v.CamH;
            return drivable;
        }
    }

    /// <summary>Shows the fitted booster flames while the car (local or remote) is boosting.</summary>
    public sealed class BoosterFlames : MonoBehaviour
    {
        public readonly List<GameObject> Flames = new List<GameObject>();
        DriftCar car;
        Net.RemoteCar remote;
        bool shown;

        void Start() { car = GetComponent<DriftCar>(); remote = GetComponent<Net.RemoteCar>(); }

        void Update()
        {
            bool on = car ? car.Boosting : remote && remote.Boosting;
            if (on == shown) return;
            shown = on;
            foreach (var f in Flames) if (f) f.SetActive(on);
        }
    }
}
