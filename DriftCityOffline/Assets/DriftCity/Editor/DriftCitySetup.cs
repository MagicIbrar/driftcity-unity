using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace DriftCity.EditorTools
{
    public static class DriftCitySetup
    {
        const string ScenePath = "Assets/Scenes/OfflineFreeRoam.unity";

        [MenuItem("Drift City/Create Offline Free-Roam Scene")]
        public static void CreateScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var boot = new GameObject("Drift City Offline").AddComponent<OfflineBootstrap>();
            var cam = Camera.main;
            if (cam) { cam.farClipPlane = 4000f; cam.gameObject.AddComponent<ChaseCamera>(); }
            var sun = Object.FindObjectsByType<Light>(FindObjectsSortMode.None).FirstOrDefault(l => l.type == LightType.Directional);
            if (sun) { sun.transform.rotation = Quaternion.Euler(50, -30, 0); sun.shadows = LightShadows.Soft; }
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.75f, 0.78f, 0.85f);
            RenderSettings.ambientEquatorColor = new Color(0.55f, 0.55f, 0.55f);
            RenderSettings.ambientGroundColor = new Color(0.3f, 0.3f, 0.3f);
            Directory.CreateDirectory("Assets/Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AlwaysIncludeShader("DriftCity/NiStandard");
            AlwaysIncludeShader("DriftCity/VCAlphaTextureBlender");
            Selection.activeObject = boot;
            Debug.Log("Drift City: created " + ScenePath + " — press Play to drive.");
        }

        static void AlwaysIncludeShader(string name)
        {
            var shader = Shader.Find(name);
            if (!shader) return;
            var gs = AssetDatabase.LoadAssetAtPath<GraphicsSettings>("ProjectSettings/GraphicsSettings.asset");
            var so = new SerializedObject(gs);
            var list = so.FindProperty("m_AlwaysIncludedShaders");
            for (int i = 0; i < list.arraySize; i++)
                if (list.GetArrayElementAtIndex(i).objectReferenceValue == shader) return;
            list.InsertArrayElementAtIndex(list.arraySize);
            list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = shader;
            so.ApplyModifiedProperties();
        }

        /// <summary>Batch-mode check: builds the world in edit mode and renders a few views to Preview/*.png.</summary>
        public static void BatchPreview()
        {
            CreateScene();
            var boot = Object.FindFirstObjectByType<OfflineBootstrap>();
            boot.BuildWorld(false);
            Directory.CreateDirectory("Preview");
            var cam = Camera.main;
            cam.farClipPlane = 6000f;

            var car = GameObject.Find("Player Car");
            var track = GameObject.Find("Track");
            var renderers = track.GetComponentsInChildren<Renderer>();
            var b = renderers[0].bounds; foreach (var r in renderers) b.Encapsulate(r.bounds);
            foreach (var m in TextureLibrary.Missing) Debug.Log("Drift City preview: missing texture " + m);
            foreach (var r in renderers)
                if (r.sharedMaterial && !r.sharedMaterial.GetTexture("_MainTex"))
                    Debug.Log($"Drift City preview: untextured mesh {r.name} size={r.bounds.size}");
            Debug.Log($"Drift City preview: track renderers={renderers.Length} bounds={b} car={(car ? car.transform.position.ToString() : "none")}");

            if (car)
            {
                Shot(cam, car.transform.position + new Vector3(4.5f, 2.2f, -5f), car.transform.position + Vector3.up * 0.6f, "car.png");
                Shot(cam, car.transform.position + new Vector3(-30f, 18f, -30f), car.transform.position, "start_area.png");
            }
            Shot(cam, b.center + new Vector3(0, b.extents.magnitude * 0.9f, -b.extents.z * 0.6f), b.center, "track_overview.png");
        }

        /// <summary>Batch-mode check: builds the world with colliders, settles the car, then holds throttle.</summary>
        public static void BatchPhysicsTest()
        {
            CreateScene();
            var boot = Object.FindAnyObjectByType<OfflineBootstrap>();
            boot.BuildWorld(true);
            var car = boot.Car;
            if (!car) { Debug.LogError("Drift City physics: no car"); return; }
            var rb = car.GetComponent<Rigidbody>();
            var mode = Physics.simulationMode;
            Physics.simulationMode = SimulationMode.Script;
            Vector3 start = car.transform.position;
            void Run(float seconds, float throttle, float steer, bool handbrake)
            {
                for (float t = 0; t < seconds; t += 0.02f) { car.ApplyControls(throttle, steer, handbrake, 0.02f); Physics.Simulate(0.02f); }
            }
            Run(2f, 0, 0, false);
            Vector3 settled = car.transform.position;
            int grounded = car.Wheels.Count(w => w && w.isGrounded);
            Debug.Log($"Drift City physics: settled drop={start.y - settled.y:F2} m, wheels grounded={grounded}/4, up·Y={Vector3.Dot(car.transform.up, Vector3.up):F2}");
            Run(4f, 1, 0, false);
            Debug.Log($"Drift City physics: after 4 s full throttle speed={car.SpeedKmh:F0} km/h, moved {Vector3.Distance(settled, car.transform.position):F1} m, height change {car.transform.position.y - settled.y:F2} m");
            Run(1.5f, 1, 1, true);
            Debug.Log($"Drift City physics: handbrake turn speed={car.SpeedKmh:F0} km/h, yaw rate={rb.angularVelocity.y * Mathf.Rad2Deg:F0} deg/s, slip angle={Vector3.Angle(car.transform.forward, Vector3.ProjectOnPlane(rb.linearVelocity, Vector3.up)):F0} deg");
            Physics.simulationMode = mode;
        }

        /// <summary>Batch-mode check: loads an open city (DC_CITY, default cras) and renders aerial and street views.</summary>
        public static void BatchCityPreview()
        {
            CreateScene();
            var boot = Object.FindAnyObjectByType<OfflineBootstrap>();
            DriftCityData.Mount(boot.GameRoot);
            string city = System.Environment.GetEnvironmentVariable("DC_CITY");
            if (string.IsNullOrEmpty(city)) city = "cras";
            var loader = new DriftCity.Game.CityLoader(city) { Colliders = false };
            var it = loader.Load(null, null);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (it.MoveNext()) { }
            Debug.Log($"Drift City city: {city} built in {sw.ElapsedMilliseconds} ms, terrain={loader.TerrainCells} blocks={loader.Blocks} props={loader.PropInstances} bounds={loader.WorldBounds}");
            Directory.CreateDirectory("Preview");
            var cam = Camera.main;
            cam.farClipPlane = 20000f;
            DriftCity.Game.OriginalLook.Apply();
            var b = loader.WorldBounds;
            Shot(cam, b.center + new Vector3(0, b.extents.magnitude * 0.8f, -b.extents.z * 0.9f), b.center, $"city_{city}_aerial.png");
            // street level near the track23 start (cras coordinates)
            var start = new Vector3(6747.7f, 600f, 4661.4f);
            if (Physics.Raycast(start + Vector3.up * 200, Vector3.down, out var hit, 1000)) start = hit.point;
            Shot(cam, start + new Vector3(-60, 25, -60), start + new Vector3(40, 0, 40), $"city_{city}_street.png");
            Shot(cam, start + new Vector3(0, 300, -350), start + new Vector3(0, 0, 200), $"city_{city}_district.png");
            // the game's own view: chase camera fov 45, near 0.4, far 512, sky dome and fog
            var sky = DriftCity.Game.OriginalLook.BuildSky();
            cam.fieldOfView = 45; cam.nearClipPlane = 0.4f; cam.farClipPlane = DriftCity.Game.OriginalLook.ChaseFar;
            foreach (var (name, offset, look) in new[] { ("a", new Vector3(-8, 3.5f, -8), new Vector3(40, 0, 40)), ("b", new Vector3(8, 3.5f, 8), new Vector3(-40, 0, -40)), ("c", new Vector3(30, 4, -10), new Vector3(-60, 2, 30)) })
            {
                if (sky) sky.transform.position = start + offset;
                Shot(cam, start + offset, start + look, $"city_{city}_ground_{name}.png");
            }
        }

        static void Shot(Camera cam, Vector3 pos, Vector3 look, string file)
        {
            cam.transform.position = pos;
            cam.transform.LookAt(look);
            var rt = new RenderTexture(1600, 900, 24);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            File.WriteAllBytes(Path.Combine("Preview", file), tex.EncodeToPNG());
            cam.targetTexture = null;
            RenderTexture.active = null;
            Object.DestroyImmediate(rt);
            Debug.Log("Drift City preview: wrote Preview/" + file);
        }
    }
}
