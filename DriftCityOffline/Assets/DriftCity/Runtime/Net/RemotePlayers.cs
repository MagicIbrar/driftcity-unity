using System.Collections.Generic;
using DriftCity.Game;
using UnityEngine;

namespace DriftCity.Net
{
    /// <summary>Other drivers in the channel: their car model and paint, a name tag, and interpolated motion.</summary>
    public sealed class RemotePlayers : MonoBehaviour
    {
        public float InterpolationDelay = 0.12f;
        /// <summary>Remote cars without an update for this long are hidden (out of view range, stalled, gone).</summary>
        public float StaleAfter = 1.5f;
        /// <summary>Built-in "Ignore Raycast" layer: remote cars stay out of ground/camera raycasts.</summary>
        const int RemoteLayer = 2;
        const float MaxCoord = 100000f, MaxSpeed = 150f;

        readonly Dictionary<int, RemoteCar> cars = new Dictionary<int, RemoteCar>();
        public int Count => cars.Count;
        public IEnumerable<RemoteCar> All => cars.Values;

        public RemoteCar Get(int id) => cars.TryGetValue(id, out var c) ? c : null;

        public void Add(Dictionary<string, object> info)
        {
            if (info == null) return;
            int id = info.I("id");
            if (cars.TryGetValue(id, out var existing))
            {
                if (existing) return;
                cars.Remove(id);   // destroyed behind our back (scene change): rebuild
            }
            var vehicle = GameTables.Car(info.I("car"));
            if (vehicle == null) return;
            var parts = info.O("parts");
            // Parts = null: what the server says is fitted is shown as is
            var look = new OwnedCar
            {
                VehicleIndex = vehicle.Index, Color = info.I("color"), PaintItem = info.I("paint", -1), Parts = null,
                Aero = parts.I("aero", -1), Hood = parts.I("hood", -1), Wing = parts.I("wing", -1), Wheels = parts.I("wheels", -1),
                Booster = parts.I("booster", -1), Neon = parts.I("neon", -1), Plate = parts.I("plate", -1), Duct = parts.I("duct", -1),
                Roof = parts.I("roof", -1), Tint = parts.I("tint", -1), Decal = parts.I("decal", -1),
            };
            GameObject go;
            Transform[] tires;
            try { go = CarFactory.BuildDressed(vehicle, look, new Vector3(0, -5000, 0), Quaternion.identity, out tires); }
            catch (System.Exception ex) { Debug.LogWarning($"remote car {id}: {ex.Message}"); return; }
            if (!go) return;
            go.name = "Remote " + info.S("name");
            var rb = go.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            var rs = go.GetComponentsInChildren<Renderer>(true);
            if (rs.Length > 0)
            {
                var b = rs[0].bounds; foreach (var r in rs) b.Encapsulate(r.bounds);
                var box = go.AddComponent<BoxCollider>();
                box.center = go.transform.InverseTransformPoint(b.center);
                box.size = b.size;
                // no physical contact: a kinematic car moved by network samples would fling the local car around
                box.isTrigger = true;
            }
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = RemoteLayer;
            var owned = CollectOwnedAssets(go);   // before the name tag (its font material is shared)
            var rc = go.AddComponent<RemoteCar>();
            rc.Init(id, info.S("name"), info.I("level"), tires);
            Game.CustomMarks.Attach(go, vehicle, look, info.S("name"));   // the driver's custom mark (0x4411e0)
            rc.Owned = owned;
            cars[id] = rc;
            var st = info.O("state");
            if (st != null && TryVec(st.L("p"), out var p) && TryNum(st, "r", out var yaw))
            {
                TryVec(st.L("v"), out var v);
                TryNum(st, "s", out var s);
                rc.Push(p, yaw, ClampVel(v), s, st.B("b"), InterpolationDelay, true);
            }
            else rc.gameObject.SetActive(false);   // shown with its first snapshot
        }

        /// <summary>
        /// Materials and meshes built for this one car (CarPaint " paint"/" tint" copies, neon copies, NIF meshes),
        /// destroyed with it. Shared ones (NifSceneBuilder's material cache, font material) are left alone.
        /// </summary>
        static List<Object> CollectOwnedAssets(GameObject go)
        {
            var owned = new List<Object>();
            var seen = new HashSet<Object>();
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                bool neon = UnderNeon(r.transform, go.transform);
                foreach (var m in r.sharedMaterials)
                {
                    if (!m || seen.Contains(m)) continue;
                    if (neon || m.name.EndsWith(" paint") || m.name.EndsWith(" tint")) { seen.Add(m); owned.Add(m); }
                }
            }
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = mf.sharedMesh;
                if (mesh && seen.Add(mesh)) owned.Add(mesh);   // NifSceneBuilder builds a new Mesh per load
            }
            return owned;
        }

        static bool UnderNeon(Transform t, Transform root)
        {
            for (; t && t != root; t = t.parent) if (t.name == "Neon") return true;
            return false;
        }

        static void DestroyCar(RemoteCar c)
        {
            if (!c) return;
            if (c.Owned != null) foreach (var o in c.Owned) if (o) Destroy(o);
            c.Owned = null;
            Destroy(c.gameObject);
        }

        public void Remove(int id)
        {
            if (cars.TryGetValue(id, out var c)) { DestroyCar(c); cars.Remove(id); }
        }

        public void Clear()
        {
            foreach (var c in cars.Values) DestroyCar(c);
            cars.Clear();
        }

        void OnDestroy() => Clear();

        void Update()
        {
            float now = Time.time;
            foreach (var c in cars.Values)
                if (c && c.gameObject.activeSelf && now - c.LastUpdate > StaleAfter) c.gameObject.SetActive(false);
            PickWithRightClick();
        }

        /// <summary>Right click on another driver's car opens the client's player menu (COMMAND 700000).</summary>
        void PickWithRightClick()
        {
#if ENABLE_INPUT_SYSTEM
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null || !mouse.rightButton.wasPressedThisFrame) return;
            Vector2 at = mouse.position.ReadValue();
#else
            if (!Input.GetMouseButtonDown(1)) return;
            Vector2 at = Input.mousePosition;
#endif
            var cam = Camera.main;
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (!cam || (es && es.IsPointerOverGameObject()) || ChatBox.Typing) return;
            RemoteCar best = null;
            float bestD = 70f * 70f;
            foreach (var c in cars.Values)
            {
                if (!c || !c.gameObject.activeSelf) continue;
                var sp = cam.WorldToScreenPoint(c.transform.position + Vector3.up * 0.8f);
                if (sp.z <= 0f || sp.z > 120f) continue;
                float d = ((Vector2)sp - at).sqrMagnitude;
                if (d < bestD) { bestD = d; best = c; }
            }
            if (best && !string.IsNullOrEmpty(best.DriverName)) DriftCity.Social.PlayerMenu.Open(best.DriverName);
        }

        /// <summary>Snapshot rows: [id, [x,y,z], yaw, [vx,vy,vz], speedKmh, boosting].</summary>
        public void ApplySnapshot(List<object> rows)
        {
            if (rows == null) return;
            foreach (var o in rows)
            {
                if (!(o is List<object> row) || row.Count < 3) continue;
                if (!TryNum(row[0], out var idf)) continue;
                int id = (int)idf;
                if (!cars.TryGetValue(id, out var car) || !car) continue;
                if (!TryVec(row[1] as List<object>, out var p) || !TryNum(row[2], out var yaw)) continue;
                Vector3 v = Vector3.zero;
                if (row.Count > 3 && !TryVec(row[3] as List<object>, out v)) v = Vector3.zero;
                float speed = 0;
                if (row.Count > 4 && !TryNum(row[4], out speed)) speed = 0;
                bool boost = row.Count > 5 && TryNum(row[5], out var bf) && bf > 0.5f;
                car.Push(p, yaw, ClampVel(v), Mathf.Clamp(speed, -500f, 1000f), boost, InterpolationDelay, false);
            }
        }

        static Vector3 ClampVel(Vector3 v) => Vector3.ClampMagnitude(v, MaxSpeed);

        static bool TryNum(Dictionary<string, object> d, string k, out float f)
        {
            f = 0;
            return d != null && d.TryGetValue(k, out var o) && TryNum(o, out f);
        }

        /// <summary>A finite, sane number (NaN/Infinity/huge values from a bad packet are rejected).</summary>
        static bool TryNum(object o, out float f)
        {
            f = 0;
            if (!(o is double d) || double.IsNaN(d) || double.IsInfinity(d) || d > MaxCoord || d < -MaxCoord) return false;
            f = (float)d;
            return true;
        }

        static bool TryVec(List<object> l, out Vector3 v)
        {
            v = Vector3.zero;
            if (l == null || l.Count < 3) return false;
            if (!TryNum(l[0], out var x) || !TryNum(l[1], out var y) || !TryNum(l[2], out var z)) return false;
            v = new Vector3(x, y, z);
            return true;
        }
    }

    public sealed class RemoteCar : MonoBehaviour
    {
        public int Id;
        public string DriverName;
        public float SpeedKmh;
        public bool Boosting;
        /// <summary>Time.time of the last snapshot (RemotePlayers hides stale cars).</summary>
        public float LastUpdate;
        /// <summary>Materials/meshes created for this car only; destroyed by RemotePlayers with it.</summary>
        public List<Object> Owned;
        Transform[] tires;
        Rigidbody rb;

        struct Sample { public float t; public Vector3 p; public float yaw; public Vector3 v; }
        readonly List<Sample> buffer = new List<Sample>();
        float delay = 0.12f;
        float wheelSpin;
        const float SnapDistance = 40f, MaxExtrapolation = 0.25f;

        public void Init(int id, string name, int level, Transform[] tireVisuals)
        {
            Id = id; DriverName = name; tires = tireVisuals;
            rb = GetComponent<Rigidbody>();
            // the client's screen-space NAME plate (game.nui 1940000), as over the player's own car
            Game.NamePlate.Attach(transform, name, own: false);
        }

        public void Push(Vector3 p, float yaw, Vector3 v, float speed, bool boost, float interpDelay, bool snap)
        {
            delay = interpDelay;
            float now = Time.time;
            bool wasHidden = !gameObject.activeSelf;
            if (wasHidden) gameObject.SetActive(true);   // updates resumed
            // first sample, back from hidden, or a jump too big to glide over (respawn / teleport): place directly
            if (snap || wasHidden || buffer.Count == 0 || (p - buffer[buffer.Count - 1].p).sqrMagnitude > SnapDistance * SnapDistance)
            {
                buffer.Clear();
                Place(p, Quaternion.Euler(0, yaw, 0));
            }
            buffer.Add(new Sample { t = now, p = p, yaw = yaw, v = v });
            if (buffer.Count > 30) buffer.RemoveAt(0);
            SpeedKmh = speed; Boosting = boost;
            LastUpdate = now;
        }

        void Place(Vector3 p, Quaternion rot)
        {
            transform.SetPositionAndRotation(p, rot);
            if (rb) { rb.position = p; rb.rotation = rot; }
        }

        void Update()
        {
            if (buffer.Count == 0) return;
            float renderTime = Time.time - delay;
            Vector3 pos; float yaw;
            int i = buffer.Count - 1;
            while (i > 0 && buffer[i - 1].t > renderTime) i--;
            if (i == 0 || buffer[i].t <= renderTime)
            {
                // ahead of the newest sample (or before the oldest): extrapolate briefly along the last velocity
                var last = buffer[buffer.Count - 1];
                float ahead = Mathf.Clamp(renderTime - last.t, 0f, MaxExtrapolation);
                pos = last.p + last.v * ahead;
                yaw = last.yaw;
            }
            else
            {
                var a = buffer[i - 1]; var b = buffer[i];
                float t = Mathf.InverseLerp(a.t, b.t, renderTime);
                pos = Vector3.Lerp(a.p, b.p, t);
                yaw = Mathf.LerpAngle(a.yaw, b.yaw, t);
            }
            var rot = Quaternion.Euler(0, yaw, 0);
            if ((pos - transform.position).sqrMagnitude > SnapDistance * SnapDistance) Place(pos, rot);
            else if (rb) { rb.MovePosition(pos); rb.MoveRotation(rot); }
            else transform.SetPositionAndRotation(pos, rot);

            wheelSpin = Mathf.Repeat(wheelSpin + SpeedKmh / 3.6f / 0.32f * Mathf.Rad2Deg * Time.deltaTime, 360f);
            if (tires != null)
                for (int k = 0; k < tires.Length; k++)
                    if (tires[k]) tires[k].localRotation = Quaternion.Euler(wheelSpin * (k % 2 == 0 ? -1 : 1), k % 2 == 0 ? 180 : 0, 0);   // rims face out (see CarFactory)
        }
    }
}
