using System.IO;
using System.Linq;
using DriftCity.Game;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DriftCity.EditorTools
{
    /// <summary>
    /// Batch renders of player cars with the game's shaders, for comparing against the original client:
    /// DC_CARS = comma-separated vehicle indexes (default 0,2,3). Writes Preview/car_&lt;index&gt;_&lt;view&gt;.png
    /// under the garage lights (light.ini) and the city weather lights (weather.ini).
    /// </summary>
    public static class DriftCityCarShots
    {
        public static void Render()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            DriftCityData.Mount(DriftCityData.ResolveGameRoot(@"D:\Drift_City_Remastered_Alpha_08-2025\Drift City Remastered Alpha"));
            GameTables.Load();
            Directory.CreateDirectory("Preview");
            var cam = new GameObject("Shot Camera").AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.55f, 0.58f, 0.62f);
            cam.fieldOfView = 40;
            cam.nearClipPlane = 0.1f;
            string list = System.Environment.GetEnvironmentVariable("DC_CARS");
            if (string.IsNullOrEmpty(list)) list = "0,2,3";
            foreach (var s in list.Split(','))
            {
                if (!int.TryParse(s.Trim(), out int index)) continue;
                var v = GameTables.Car(index);
                var car = CarFactory.BuildDressed(v, new OwnedCar { VehicleIndex = v.Index, Color = 2 }, Vector3.zero, Quaternion.identity, out _);
                foreach (var (name, pos) in new[] { ("front", new Vector3(3.6f, 1.6f, 4.8f)), ("rear", new Vector3(-3.2f, 1.8f, -5.2f)), ("side", new Vector3(6.2f, 1.0f, 0.2f)), ("close", new Vector3(1.6f, 1.1f, 3.0f)) })
                {
                    OriginalLook.ApplyGarage();
                    Shot(cam, pos, new Vector3(0, 0.6f, 0), $"car_{index}_{name}_garage.png");
                    OriginalLook.ApplyGlobals();
                    Shot(cam, pos, new Vector3(0, 0.6f, 0), $"car_{index}_{name}_city.png");
                }
                // the booster flames at a few moments of their animation
                foreach (var flame in car.GetComponentsInChildren<Transform>(true).Where(t => t.name == "Booster Flame"))
                    flame.gameObject.SetActive(true);
                foreach (var t in new[] { 0.1f, 0.4f, 0.8f })
                {
                    foreach (var a in car.GetComponentsInChildren<NifAnimator>(true)) a.Sample(t);
                    foreach (var r in car.GetComponentsInChildren<Renderer>(true).Where(r => r.GetComponentsInParent<Transform>(true).Any(p => p.name == "Booster Flame")))
                        Debug.Log($"Drift City shots: t={t} {r.name} active={r.gameObject.activeInHierarchy} enabled={r.enabled} scale={r.transform.lossyScale.x:F3} size={r.bounds.size} " +
                                  $"alpha={(r.sharedMaterial && r.sharedMaterial.HasProperty("_Color") ? r.sharedMaterial.color.a : -1):F2} shader={r.sharedMaterial?.shader.name} tex={r.sharedMaterial?.mainTexture?.name} src={r.sharedMaterial?.GetFloat("_SrcBlend")} dst={r.sharedMaterial?.GetFloat("_DstBlend")}");
                    foreach (var a in car.GetComponentsInChildren<NifAnimator>(true))
                        foreach (var ch in a.Channels.OfType<NifAnimator.TransformChannel>())
                            Debug.Log($"Drift City shots: {ch.Path} scales [{string.Join(" ", ch.Data.Scales.Times.Zip(ch.Data.Scales.Values, (tt, v) => $"{tt:F2}:{v:F2}"))}] trans {ch.Data.Translations.Count} rot {ch.Data.Rotations.Count} euler {ch.Data.Euler != null}");
                    foreach (var a in car.GetComponentsInChildren<NifAnimator>(true)) Debug.Log($"Drift City shots: animator on {a.name}: {a.Channels.Count} channels ({string.Join(",", a.Channels.Select(c => c.GetType().Name + ":" + c.Path + $"[{c.Start}-{c.Stop} f{c.Frequency}]"))})");
                    OriginalLook.ApplyGlobals();
                    Shot(cam, new Vector3(-1.4f, 1.0f, -4.2f), new Vector3(0, 0.4f, -1.8f), $"car_{index}_booster_{t:F1}.png");
                }
                Object.DestroyImmediate(car);
            }
        }

        /// <summary>Traffic and HUV models with their wheels (TrafficRig) on a ground plane: Preview/traffic_&lt;model&gt;.png.</summary>
        public static void RenderTraffic()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            DriftCityData.Mount(DriftCityData.ResolveGameRoot(@"D:\Drift_City_Remastered_Alpha_08-2025\Drift City Remastered Alpha"));
            GameTables.Load();
            OriginalLook.ApplyGlobals();
            Directory.CreateDirectory("Preview");
            var cam = new GameObject("Shot Camera").AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.55f, 0.58f, 0.62f);
            cam.fieldOfView = 40;
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.transform.localScale = Vector3.one * 5;
            var tires = new GameObject("Tire Models"); tires.SetActive(false);
            var loaded = new System.Collections.Generic.Dictionary<string, GameObject>();
            GameObject Tire(string p) { if (!loaded.TryGetValue(p, out var g)) loaded[p] = g = new NifSceneBuilder { LogErrors = false }.Load(p, tires.transform); return g; }
            foreach (var nif in new[] { @"vehicle\traffic\c_t_0_012.nif", @"vehicle\traffic\c_t_0_004.nif", @"vehicle\traffic\c_t_0_010.nif", @"vehicle\huv\huv01_b.nif" })
            {
                if (!DriftCityData.Files.Exists(nif)) continue;
                var car = TrafficRig.Build(nif, null, Tire);
                car.SetActive(true);
                var b = new Bounds(car.transform.position, Vector3.zero);
                foreach (var r in car.GetComponentsInChildren<Renderer>()) b.Encapsulate(r.bounds);
                float d = Mathf.Max(5f, b.extents.magnitude * 2.2f);
                Shot(cam, b.center + new Vector3(d * 0.7f, d * 0.25f, d * 0.6f), b.center, $"traffic_{Path.GetFileNameWithoutExtension(nif)}.png");
                Shot(cam, b.center + new Vector3(d, 0.3f, 0), b.center, $"traffic_{Path.GetFileNameWithoutExtension(nif)}_side.png");
                Object.DestroyImmediate(car);
            }
        }

        /// <summary>The sky dome of each weather seen from its centre: Preview/sky_&lt;weather&gt;_{horizon,up}.png.</summary>
        public static void RenderSky()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            DriftCityData.Mount(DriftCityData.ResolveGameRoot(@"D:\Drift_City_Remastered_Alpha_08-2025\Drift City Remastered Alpha"));
            Directory.CreateDirectory("Preview");
            var cam = new GameObject("Main Camera").AddComponent<Camera>();
            cam.tag = "MainCamera";
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.magenta;   // anything magenta is not covered by the dome
            cam.fieldOfView = 60;
            cam.farClipPlane = OriginalLook.ChaseFar;
            var sky = OriginalLook.BuildSky();
            foreach (var r in sky.GetComponentsInChildren<Renderer>(true))
                Debug.Log($"Drift City sky: {r.name} bounds {r.bounds.center} size {r.bounds.size} mat {r.sharedMaterial?.shader.name} tex {r.sharedMaterial?.mainTexture?.name}");
            for (int i = 0; i < OriginalLook.WeatherCount; i++)
            {
                OriginalLook.WeatherIndex = i;
                OriginalLook.ShowSky(sky);
                sky.transform.position = Vector3.zero;
                Shot(cam, Vector3.zero, new Vector3(0, 0.05f, 1), $"sky_{OriginalLook.Current.Name}_horizon.png");
                Shot(cam, Vector3.zero, new Vector3(0, 1f, 0.6f), $"sky_{OriginalLook.Current.Name}_up.png");
            }
        }

        static void Shot(Camera cam, Vector3 pos, Vector3 look, string file)
        {
            cam.transform.position = pos;
            cam.transform.LookAt(look);
            var rt = new RenderTexture(1024, 768, 24);
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
            Object.DestroyImmediate(tex);
        }
    }
}
