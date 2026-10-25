using System;
using System.IO;
using System.Linq;
using UnityEngine;

namespace DriftCity
{
    /// <summary>
    /// Offline free-roam: mounts the installed client's data, builds a track from its NIF
    /// files, assembles a player car and hands control to the player. No server involved.
    /// </summary>
    public sealed class OfflineBootstrap : MonoBehaviour
    {
        [Tooltip("Folder containing DriftCity.exe and the Data folder")]
        public string GameRoot = @"D:\Drift_City_Remastered_Alpha_08-2025\Drift City Remastered Alpha";
        public string[] TrackFiles = { @"cras\track23\terrain.nif" };
        [Tooltip("NIF whose first mesh marks the start position")]
        public string StartMarkerFile = @"cras\track23\point.nif";
        public string CarBody = @"vehicle\player\pc_0000c_b01.nif";
        public string Tire = @"vehicle\player\wheel\tire_ply0001.nif";
        public float StartYaw = 0f;
        [Tooltip("Tire NIFs are authored small and scaled by the game at runtime; this is the target radius in meters")]
        public float TireRadius = 0.32f;

        public GameObject World { get; private set; }
        public DriftCar Car { get; private set; }

        void Start()
        {
            BuildWorld(true);
            var cam = Camera.main;
            if (cam && Car)
            {
                if (!cam.TryGetComponent<ChaseCamera>(out var chase)) chase = cam.gameObject.AddComponent<ChaseCamera>();
                chase.Target = Car.transform;
                cam.farClipPlane = 4000f;
            }
        }

        public void BuildWorld(bool withPhysics)
        {
            DriftCityData.Mount(GameRoot);
            if (World) DestroyImmediate(World);
            World = new GameObject("Drift City World");

            var track = new GameObject("Track");
            track.transform.SetParent(World.transform, false);
            var builder = new NifSceneBuilder { AddColliders = withPhysics };
            foreach (var f in TrackFiles)
            {
                try { builder.Load(f, track.transform); }
                catch (Exception ex) { Debug.LogError($"track {f}: {ex.Message}"); }
            }

            Vector3 start = FindStart(track);
            Car = BuildCar(start, Quaternion.Euler(0, StartYaw, 0), withPhysics);
            if (Car) Car.transform.SetParent(World.transform, true);
        }

        Vector3 FindStart(GameObject track)
        {
            Vector3 p = Vector3.zero;
            bool found = false;
            if (!string.IsNullOrEmpty(StartMarkerFile) && DriftCityData.Files.Exists(StartMarkerFile))
            {
                var marker = new NifSceneBuilder().Load(StartMarkerFile, World.transform);
                var r = marker.GetComponentInChildren<Renderer>();
                if (r) { p = r.bounds.center; found = true; }
                marker.SetActive(false);
            }
            if (!found)
            {
                var renderers = track.GetComponentsInChildren<Renderer>();
                if (renderers.Length > 0) { var b = renderers[0].bounds; foreach (var r in renderers) b.Encapsulate(r.bounds); p = b.center; }
            }
            Physics.SyncTransforms();
            if (Physics.Raycast(p + Vector3.up * 50f, Vector3.down, out var hit, 500f)) p = hit.point;
            return p + Vector3.up * 1.0f;
        }

        DriftCar BuildCar(Vector3 position, Quaternion rotation, bool withPhysics)
        {
            var fs = DriftCityData.Files;
            if (!fs.Exists(CarBody)) { Debug.LogError("car body not found: " + CarBody); return null; }
            var carRoot = new GameObject("Player Car");
            carRoot.transform.SetPositionAndRotation(position, rotation);
            var body = new NifSceneBuilder().Load(CarBody, carRoot.transform);

            // wheel attachment nodes "01".."04" (FL, FR, RL, RR) live under "wheels"
            var wheelNodes = new Transform[4];
            foreach (var t in body.GetComponentsInChildren<Transform>(true))
                if (t.parent && t.parent.name == "wheels" && int.TryParse(t.name, out int i) && i >= 1 && i <= 4)
                    wheelNodes[i - 1] = t;

            var visuals = new Transform[4];
            float radius = TireRadius;
            for (int i = 0; i < 4; i++)
            {
                if (!wheelNodes[i] || !fs.Exists(Tire)) continue;
                var tire = new NifSceneBuilder().Load(Tire, carRoot.transform);
                tire.transform.position = wheelNodes[i].position;
                tire.transform.rotation = carRoot.transform.rotation * (i % 2 == 1 ? Quaternion.Euler(0, 180, 0) : Quaternion.identity);
                var rs = tire.GetComponentsInChildren<Renderer>();
                if (rs.Length > 0)
                {
                    var b = rs[0].bounds; foreach (var r in rs) b.Encapsulate(r.bounds);
                    float meshRadius = Mathf.Max(b.extents.y, b.extents.z);
                    if (meshRadius > 0.01f) tire.transform.localScale = Vector3.one * (TireRadius / meshRadius);
                }
                visuals[i] = tire.transform;
            }
            if (!withPhysics) return null;

            var rb = carRoot.AddComponent<Rigidbody>();
            rb.mass = 1250f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            // chassis collider from the body's renderers, lifted so it doesn't scrape the road
            var bodyRenderers = body.GetComponentsInChildren<Renderer>();
            if (bodyRenderers.Length > 0)
            {
                var bounds = bodyRenderers[0].bounds; foreach (var r in bodyRenderers) bounds.Encapsulate(r.bounds);
                var box = carRoot.AddComponent<BoxCollider>();
                var local = carRoot.transform.InverseTransformPoint(bounds.center);
                box.center = local + Vector3.up * 0.15f;
                box.size = new Vector3(bounds.size.x * 0.9f, Mathf.Max(0.3f, bounds.size.y - 0.4f), bounds.size.z * 0.95f);
                rb.centerOfMass = new Vector3(local.x, 0.25f, local.z);
            }

            var car = carRoot.AddComponent<DriftCar>();
            for (int i = 0; i < 4; i++)
            {
                if (!wheelNodes[i]) continue;
                var wgo = new GameObject("WheelCollider " + (i + 1));
                wgo.transform.SetParent(carRoot.transform, false);
                wgo.transform.position = wheelNodes[i].position + carRoot.transform.up * 0.15f;
                var wc = wgo.AddComponent<WheelCollider>();
                wc.radius = radius;
                wc.suspensionDistance = 0.18f;
                wc.mass = 20f;
                var spring = wc.suspensionSpring; spring.spring = 38000f; spring.damper = 4200f; spring.targetPosition = 0.45f; wc.suspensionSpring = spring;
                var fwd = wc.forwardFriction; fwd.stiffness = 1.6f; wc.forwardFriction = fwd;
                car.Wheels[i] = wc;
                car.WheelVisuals[i] = visuals[i];
            }
            car.SetSpawn(position, rotation);
            return car;
        }
    }
}
