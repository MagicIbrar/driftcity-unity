using System.Collections.Generic;
using DriftCity.Core.Nif;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// Wheels and ground following of a traffic car or HUV, after the client's vehicle actor (DriftCity_RE/TRAFFIC.md):
    /// tirelist models on the wheel nodes of &lt;model&gt;_dm.nif, the body lifted so the hubs sit one tyre radius above
    /// the road, wheels turning at speed / radius, and the car laid on the plane through four ray-cast corner heights,
    /// each softened by the client's wheel spring. Trailers swing behind their hitch.
    /// </summary>
    public sealed class TrafficRig : MonoBehaviour
    {
        // built into the template; serialized so Instantiate copies (and remaps) them
        [SerializeField] Transform[] wheels = new Transform[0];
        [SerializeField] Vector3[] rest = new Vector3[0];
        [SerializeField] int frontCount;
        [SerializeField] float radius = 0.35f;
        [SerializeField] float wheelbase = 2.6f;
        [SerializeField] Vector3[] corners;          // FL, FR, RL, RR ground probe points (car space, y unused)
        [SerializeField] Transform trailer;
        [SerializeField] Quaternion trailerRest = Quaternion.identity;
        [SerializeField] float trailerLength;

        const float ProbeUp = 2f, ProbeDown = 4f;     // the client's ground query reaches 6 m (0x4a3730 -> 0x475bd0)
        const float SubStep = 0.01f;                   // the client steps actors in <= 10 ms slices (0x4b0fc0)

        readonly float[] height = new float[4], climb = new float[4], ground = new float[4];
        readonly RaycastHit[] hits = new RaycastHit[8];
        float groundPitch, lastLaneY, spin, steer, steerVel;
        bool placed, tracking, trailerPlaced;
        Vector3 lastPos, lastFwd, trailerAxle;

        public bool HasCorners => corners != null && corners.Length == 4;

        /// <summary>Loads a traffic / HUV body with its wheels as a spawn template (throws if the body can't be read).</summary>
        public static GameObject Build(string bodyNif, Transform parent, System.Func<string, GameObject> tireModel)
        {
            var root = new GameObject(System.IO.Path.GetFileName(bodyNif));
            root.transform.SetParent(parent, false);
            var pivot = new GameObject("Body Pivot").transform;
            pivot.SetParent(root.transform, false);
            try
            {
                var body = new NifSceneBuilder { LogErrors = false }.Load(bodyNif, pivot);
                body.name = "Body";
                root.AddComponent<TrafficRig>().Assemble(bodyNif, pivot, body.transform, tireModel);
            }
            catch { Destroy(root); throw; }
            return root;
        }

        void Assemble(string bodyNif, Transform pivot, Transform body, System.Func<string, GameObject> tireModel)
        {
            var fs = DriftCityData.Files;
            var spec = GameTables.TrafficWheels(bodyNif);
            // the client reads the wheel nodes from <model>_dm.nif (0x464cb0 swaps ".nif"/".kfm" for "_dm.nif")
            string dm = System.IO.Path.ChangeExtension(bodyNif, null) + "_dm.nif";
            var front = new List<Vector3?>();
            var back = new List<Vector3?>();
            int n0 = spec != null ? spec.TireCount - spec.RearCount : -1;
            if (!ReadWheelNodes(dm, n0, front, back, out var hitch, out var hitchRot, out float trailLen))
                ReadWheelNodes(bodyNif, n0, front, back, out hitch, out hitchRot, out trailLen);

            int count = spec != null ? spec.TireCount : front.Count + back.Count;
            frontCount = spec != null ? spec.TireCount - spec.RearCount : front.Count;
            var bodyBounds = CarFactory.LocalBounds(transform, body.GetComponentsInChildren<Renderer>());

            // trailer bodies hang from a node at the "wheels1" position (c_t_0_010 LODGroup02); their wheels go with it
            if (count > frontCount && hitch.sqrMagnitude > 0.1f)
                foreach (var node in body.GetComponentsInChildren<Transform>(true))
                    if (node != body && (body.InverseTransformPoint(node.position) - hitch).sqrMagnitude < 0.0625f && node.GetComponentInChildren<Renderer>(true))
                    { trailer = node; break; }

            var wheelList = new List<Transform>();
            var restList = new List<Vector3>();
            float r0 = 0f;
            for (int i = 0; i < count; i++)
            {
                Vector3? p = i < frontCount ? (i < front.Count ? front[i] : null) : (i - frontCount < back.Count ? back[i - frontCount] : null);
                if (p == null) { wheelList.Add(null); restList.Add(Vector3.zero); continue; }
                var w = new GameObject("Wheel " + (i + 1).ToString("00")).transform;
                w.SetParent(pivot, false);
                w.localPosition = p.Value;
                var tire = spec != null ? GameTables.Tire(spec.TireId(i)) : null;
                if (tire != null && fs.Exists(tire.Nif))
                {
                    GameObject model = null;
                    try { model = tireModel(tire.Nif); }
                    catch (System.Exception ex) { Debug.LogWarning($"traffic tire {tire.Nif}: {ex.Message}"); }
                    if (model)
                    {
                        // tirelist sizes match the traffic / HUV meshes, which the client draws unscaled; even
                        // (left) wheels are turned half way round about the vertical (0x4b0480: Rz(pi))
                        var t = Instantiate(model, w, false).transform;
                        t.name = "Tire";
                        t.localPosition = Vector3.zero;
                        t.localRotation = i % 2 == 0 ? Quaternion.Euler(0f, 180f, 0f) : Quaternion.identity;
                        if (i == 0) r0 = tire.Radius > 0.05f ? tire.Radius : MeshRadius(t);
                    }
                }
                if (trailer && i >= frontCount) w.SetParent(trailer, true);
                wheelList.Add(w);
                restList.Add(w.localPosition);
            }
            wheels = wheelList.ToArray();
            rest = restList.ToArray();
            if (trailer) { trailerRest = trailer.localRotation; trailerLength = trailLen; }

            // 0x4ae640: v_body sits (radius - wheel 1 height) up, which puts every hub one radius above the ground
            if (r0 > 0.05f) radius = r0;
            if (wheels.Length > 0 && wheels[0] && r0 > 0.05f)
                pivot.localPosition = new Vector3(0f, Mathf.Clamp(radius - rest[0].y, -0.5f, 2f), 0f);
            Corners(bodyBounds);
        }

        /// <summary>Corner probes: wheels 1, 2, n-1, n of the tractor moved 0.1 m outwards (0x4a3730), else the body's footprint.</summary>
        void Corners(Bounds b)
        {
            int n = Mathf.Min(frontCount, wheels.Length);
            Vector3 Outward(int i) { var p = rest[i]; p.x += p.x > 0f ? 0.1f : -0.1f; return p; }
            float hx = Mathf.Max(0.3f, b.extents.x * 0.8f), hz = Mathf.Max(0.5f, b.extents.z * 0.65f), cx = b.center.x, cz = b.center.z;
            var c = new[] { new Vector3(cx - hx, 0, cz + hz), new Vector3(cx + hx, 0, cz + hz), new Vector3(cx - hx, 0, cz - hz), new Vector3(cx + hx, 0, cz - hz) };
            if (n >= 2 && wheels[0] && wheels[1] && wheels[n - 2] && wheels[n - 1])
            {
                var w = new[] { Outward(0), Outward(1), Outward(n - 2), Outward(n - 1) };
                // keep the wheel layout unless it is degenerate (a bike's wheels on the centre line, one axle only)
                bool track = Mathf.Abs(w[1].x - w[0].x) > 0.3f && Mathf.Abs(w[3].x - w[2].x) > 0.3f;
                bool axles = (w[0].z + w[1].z) * 0.5f - (w[2].z + w[3].z) * 0.5f > 0.5f;
                for (int k = 0; k < 4; k++)
                    c[k] = new Vector3(track ? w[k].x : c[k].x, 0f, axles ? w[k].z : c[k].z);
            }
            corners = c;
            wheelbase = Mathf.Max(0.5f, (c[0].z + c[1].z - c[2].z - c[3].z) * 0.5f);
        }

        /// <summary>Corner probes from the collision box, for cars built elsewhere (no wheel data).</summary>
        public void CornersFromBox(Vector3 center, Vector3 size)
        {
            var b = new Bounds(center, size);
            if (wheels == null) wheels = new Transform[0];
            if (rest == null) rest = new Vector3[0];
            frontCount = 0;
            Corners(b);
        }

        static float MeshRadius(Transform tire)
        {
            var b = CarFactory.LocalBounds(tire, tire.GetComponentsInChildren<Renderer>());
            return Mathf.Max(b.extents.y, b.extents.z);
        }

        /// <summary>
        /// The client's wheel groups (0x469ba0): "wheels" children "01".."NN" in body space; "wheels1" (trailer) children
        /// numbered on from the tractor's count, placed by the group's own translation and rotation.
        /// </summary>
        static bool ReadWheelNodes(string path, int tractorCount, List<Vector3?> front, List<Vector3?> back,
                                   out Vector3 hitch, out Quaternion hitchRot, out float trailerLength)
        {
            hitch = Vector3.zero; hitchRot = Quaternion.identity; trailerLength = 0f;
            front.Clear(); back.Clear();
            var fs = DriftCityData.Files;
            if (!fs.Exists(path)) return false;
            NifFile nif;
            try { nif = NifFile.Parse(fs.Read(path)); }
            catch { return false; }
            NiNode group = null;
            foreach (var root in nif.RootObjects()) if ((group = FindNode(nif, root, "wheels")) != null) break;
            if (group == null) return false;
            int n = group.Children?.Length ?? 0;
            for (int k = 1; k <= n; k++)
                front.Add(FindChild(nif, group, k.ToString("00")) is NiAVObject o ? DriftCityData.ToUnity(o.Translation) : (Vector3?)null);
            NiNode trail = null;
            foreach (var root in nif.RootObjects()) if ((trail = FindNode(nif, root, "wheels1")) != null) break;
            if (trail != null)
            {
                hitch = DriftCityData.ToUnity(trail.Translation);
                hitchRot = DriftCityData.ToUnity(trail.Rotation);
                int first = (tractorCount >= 0 ? tractorCount : n) + 1;
                int m = trail.Children?.Length ?? 0;
                float sum = 0f; int found = 0;
                for (int j = 0; j < m; j++)
                {
                    if (!(FindChild(nif, trail, (first + j).ToString("00")) is NiAVObject o)) { back.Add(null); continue; }
                    var local = DriftCityData.ToUnity(o.Translation);
                    back.Add(hitch + hitchRot * local);
                    // 0x4ad000: the trailer's length is the mean distance of its wheels behind the hitch
                    sum -= local.z; found++;
                }
                if (found > 0) trailerLength = sum / found;
            }
            return true;
        }

        /// <summary>A node by name anywhere under <paramref name="o"/> (Gamebryo GetObjectByName).</summary>
        static NiNode FindNode(NifFile nif, NiAVObject o, string name)
        {
            if (!(o is NiNode node)) return null;
            if (node.Name == name) return node;
            if (node.Children == null) return null;
            foreach (int c in node.Children)
                if (nif.Get<NiAVObject>(c) is NiAVObject child && FindNode(nif, child, name) is NiNode hit) return hit;
            return null;
        }

        /// <summary>Any object by name below <paramref name="group"/>.</summary>
        static NiAVObject FindChild(NifFile nif, NiNode group, string name)
        {
            if (group.Children == null) return null;
            foreach (int c in group.Children)
            {
                if (!(nif.Get<NiAVObject>(c) is NiAVObject child)) continue;
                if (child.Name == name) return child;
                if (child is NiNode sub && FindChild(nif, sub, name) is NiAVObject deep) return deep;
            }
            return null;
        }

        // ---------------- ground following ----------------

        /// <summary>Settles the springs on the ground at a new spot (spawn, recycle, recovery from a crash).</summary>
        public void ResetAt(Vector3 lanePos, Vector3 laneFwd, Quaternion yaw)
        {
            if (!HasCorners) CornersFromBox(new Vector3(0f, 0.75f, 0f), new Vector3(2f, 1.5f, 4.5f));
            float slope = Slope(laneFwd);
            for (int i = 0; i < 4; i++) { ground[i] = Probe(lanePos, yaw, i, slope); height[i] = 0f; climb[i] = 0f; }
            lastLaneY = lanePos.y;
            groundPitch = GroundPitch();
            placed = true;
            tracking = false;
        }

        /// <summary>
        /// One step of the kinematic car at <paramref name="lanePos"/> heading <paramref name="yaw"/>: probes the ground
        /// under the four corners (when <paramref name="probe"/>, else carries it along the lane), runs the client's
        /// per-wheel spring and returns the pose on the plane through the corner heights.
        /// </summary>
        public void Follow(Vector3 lanePos, Vector3 laneFwd, Quaternion yaw, float speed, float dt, bool probe, out Vector3 pos, out Quaternion rot)
        {
            if (!placed || !HasCorners) ResetAt(lanePos, laneFwd, yaw);
            float slope = Slope(laneFwd), laneRise = lanePos.y - lastLaneY;
            lastLaneY = lanePos.y;
            // 0x4a3730: a wheel's height drops by the ground's rise and rises with the climb rate along the last ground
            // plane (its pitch, 0x4a23d0), so the springs only see changes of slope (crests, dips, kerbs)
            float carried = Mathf.Sin(groundPitch) * speed * dt;
            for (int i = 0; i < 4; i++)
            {
                float g = probe ? Probe(lanePos, yaw, i, slope) : ground[i] + laneRise;
                height[i] += carried - (g - ground[i]);
                ground[i] = g;
            }
            groundPitch = GroundPitch();
            if (dt > 0.1f) for (int i = 0; i < 4; i++) climb[i] = 0f;
            int steps = Mathf.Clamp(Mathf.CeilToInt(dt / SubStep), 1, 10);
            float h = dt / steps;
            for (int s = 0; s < steps; s++)
                for (int i = 0; i < 4; i++)
                {
                    float x = height[i], v = climb[i], a;
                    if (x >= 0.01f) a = -10f * (i < 2 ? 1.4f : 1f);   // off the ground: falls, the nose a little faster
                    else if (x > -0.1f) a = -30f * x - 10f * v;        // spring m = 1, d = 10, k = 30
                    else a = -45f * x - 10f * v;                       // pressed in further: k = 45, floor at -0.2
                    v += a * h; x += v * h;
                    if (x < -0.2f) { x = -0.2f; if (v < 0f) v = 0f; }
                    height[i] = x; climb[i] = v;
                }

            // 0x4a2f70: the body plane through the corner heights, read at the axle and side centre lines
            var c = corners;
            float y0 = ground[0] + Mathf.Max(height[0], -0.2f), y1 = ground[1] + Mathf.Max(height[1], -0.2f);
            float y2 = ground[2] + Mathf.Max(height[2], -0.2f), y3 = ground[3] + Mathf.Max(height[3], -0.2f);
            float yF = AtZero(c[0].x, y0, c[1].x, y1), zF = AtZero(c[0].x, c[0].z, c[1].x, c[1].z);
            float yR = AtZero(c[2].x, y2, c[3].x, y3), zR = AtZero(c[2].x, c[2].z, c[3].x, c[3].z);
            float yL = AtZero(c[0].z, y0, c[2].z, y2), xL = AtZero(c[0].z, c[0].x, c[2].z, c[2].x);
            float yRt = AtZero(c[1].z, y1, c[3].z, y3), xRt = AtZero(c[1].z, c[1].x, c[3].z, c[3].x);
            var fwd = new Vector3(0f, yF - yR, Mathf.Max(0.1f, zF - zR)).normalized;
            var right = new Vector3(Mathf.Max(0.1f, xRt - xL), yRt - yL, 0f).normalized;
            var tilt = Quaternion.LookRotation(fwd, Vector3.Cross(fwd, right));
            pos = new Vector3(lanePos.x, AtZero(zR, yR, zF, yF), lanePos.z);
            rot = yaw * tilt;
        }

        /// <summary>Pitch of the plane through the probed ground points (nose up positive).</summary>
        float GroundPitch()
        {
            var c = corners;
            float zF = AtZero(c[0].x, c[0].z, c[1].x, c[1].z), zR = AtZero(c[2].x, c[2].z, c[3].x, c[3].z);
            float gF = AtZero(c[0].x, ground[0], c[1].x, ground[1]), gR = AtZero(c[2].x, ground[2], c[3].x, ground[3]);
            return Mathf.Atan2(gF - gR, Mathf.Max(0.1f, zF - zR));
        }

        /// <summary>The value at 0 of the line through (a, va) and (b, vb).</summary>
        static float AtZero(float a, float va, float b, float vb) =>
            Mathf.Abs(b - a) < 1e-4f ? (va + vb) * 0.5f : (va * b - vb * a) / (b - a);

        static float Slope(Vector3 laneFwd) => laneFwd.y / Mathf.Sqrt(Mathf.Max(1e-4f, 1f - laneFwd.y * laneFwd.y));

        /// <summary>Ground height under corner <paramref name="i"/>: the nearest static surface, else the lane's own slope.</summary>
        float Probe(Vector3 lanePos, Quaternion yaw, int i, float slope)
        {
            var c = corners[i];
            var p = lanePos + yaw * new Vector3(c.x, 0f, c.z);
            float guess = lanePos.y + c.z * slope;
            int n = Physics.RaycastNonAlloc(new Vector3(p.x, guess + ProbeUp, p.z), Vector3.down, hits, ProbeUp + ProbeDown, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue, y = guess;
            for (int k = 0; k < n; k++)
            {
                // cars (ours included) all carry rigidbodies; the city does not
                if (hits[k].collider.attachedRigidbody || hits[k].distance >= best) continue;
                best = hits[k].distance;
                y = hits[k].point.y;
            }
            return y;
        }

        // ---------------- wheel animation ----------------

        void LateUpdate()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;
            var t = transform;
            var fwd = t.forward;
            if (!tracking || (t.position - lastPos).sqrMagnitude > 100f)
            {
                tracking = true;
                trailerPlaced = false;
                lastPos = t.position; lastFwd = fwd;
            }
            // speed and turn rate from the car's own (interpolated) motion, so crashed cars roll too
            float v = Vector3.Dot(t.position - lastPos, fwd) / dt;
            float yawRate = Vector3.SignedAngle(Flat(lastFwd), Flat(fwd), Vector3.up) * Mathf.Deg2Rad / dt;
            lastPos = t.position; lastFwd = fwd;

            // 0x4b0480: spin += speed / radius * dt (radius of wheel 1, both axles)
            spin = Mathf.Repeat(spin + v / Mathf.Max(0.05f, radius) * dt, Mathf.PI * 2f);
            // the client turns wheels 1 and 2 by a steering value smoothed over 0.1 s (SmoothCD 0x476520); traffic has no
            // steering input of its own, so the angle comes from the turn rate (Ackermann: atan(wheelbase * rate / speed))
            float target = Mathf.Abs(v) > 1f ? Mathf.Clamp(Mathf.Atan(wheelbase * yawRate / v), -0.6f, 0.6f) : steer;
            steer = Mathf.SmoothDamp(steer, target, ref steerVel, 0.1f, Mathf.Infinity, dt);

            var roll = Quaternion.Euler(spin * Mathf.Rad2Deg, 0f, 0f);
            var turned = Quaternion.Euler(0f, steer * Mathf.Rad2Deg, 0f) * roll;
            for (int i = 0; i < wheels.Length; i++)
            {
                var w = wheels[i];
                if (!w) continue;
                var p = rest[i];
                if (i < frontCount)
                {
                    // tractor wheels follow their corner's spring by up to 10 cm (corner 3/4 for the rear axles)
                    int k = i <= 3 ? i : 2 + (i & 1);
                    p.y -= Mathf.Clamp(height[k], -0.1f, 0.1f);
                }
                w.localPosition = p;
                w.localRotation = i < 2 ? turned : roll;
            }
            if (trailer && trailerLength > 0.5f) Trailer();
        }

        /// <summary>The trailer's axle is dragged behind the hitch on a bar of the trailer's length (0x497a30).</summary>
        void Trailer()
        {
            var t = transform;
            var hitchPos = trailer.position;
            if (!trailerPlaced) { trailerAxle = hitchPos - t.forward * trailerLength; trailerPlaced = true; }
            var d = trailerAxle - hitchPos;
            d -= Vector3.Project(d, t.up);
            if (d.sqrMagnitude < 1e-4f) d = -t.forward;
            d = d.normalized;
            trailerAxle = hitchPos + d * trailerLength;
            var local = trailer.parent.InverseTransformDirection(-d);
            float yaw = Mathf.Clamp(Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg, -80f, 80f);
            trailer.localRotation = Quaternion.AngleAxis(yaw, Vector3.up) * trailerRest;
        }

        static Vector3 Flat(Vector3 v) { v.y = 0f; return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward; }
    }
}
