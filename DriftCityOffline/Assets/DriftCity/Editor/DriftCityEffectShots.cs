using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DriftCity.Game;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DriftCity.EditorTools
{
    /// <summary>
    /// Batch renders of the one-shot effect NIFs (GameEffects) at a few points of their clock:
    /// Preview/fx_&lt;name&gt;_&lt;t&gt;.png, 1024x768. World effects stand on a grey ground next to a car-sized box
    /// (1.8 x 1.3 x 4.4 m, facing +Z); screen effects are drawn with the UI effect camera over a dark backdrop.
    /// DC_FX_ONLY = comma-separated effect names to render only those; DC_FX_TIMES = comma-separated seconds to use
    /// instead of the default fractions of each effect's duration.
    /// Run: -executeMethod DriftCity.EditorTools.DriftCityEffectShots.Render (without -nographics).
    /// </summary>
    public static class DriftCityEffectShots
    {
        enum Kind { World, Car, CarRotated, Prop, Screen, Item }

        sealed class Spec
        {
            public string Name; public Kind Kind; public Vector3 Eye, Look; public Vector3 Offset; public int EffectId;
            public Spec(string name, Kind kind, Vector3 eye, Vector3 look) { Name = name; Kind = kind; Eye = eye; Look = look; }
        }

        static readonly float[] Fractions = { 0.08f, 0.3f, 0.55f, 0.85f };

        public static void Render()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            DriftCityData.Mount(DriftCityData.ResolveGameRoot(@"D:\Drift_City_Remastered_Alpha_08-2025\Drift City Remastered Alpha"));
            Directory.CreateDirectory("Preview");
            GameEffects.Sounds = false;

            var near = new Vector3(5f, 3f, -8f);
            var specs = new List<Spec>
            {
                new Spec("levelup", Kind.Car, new Vector3(0f, 6f, -18f), new Vector3(0f, 4f, 0f)),
                new Spec("jump_up", Kind.Car, near, new Vector3(0f, 0.8f, 0f)),
                new Spec("jump_down", Kind.Car, near, new Vector3(0f, 0.5f, 0f)),
                new Spec("graze_l", Kind.CarRotated, new Vector3(-7f, 1.6f, -3f), new Vector3(0f, 0.6f, -1f)),
                new Spec("graze_r", Kind.CarRotated, new Vector3(7f, 1.6f, -3f), new Vector3(0f, 0.6f, -1f)),
                new Spec("huv_explosion", Kind.CarRotated, new Vector3(0f, 5f, -16f), new Vector3(0f, 2.5f, 0f)),
                new Spec("palmtree_explosion01", Kind.Prop, new Vector3(0f, 9f, -26f), new Vector3(0f, 8f, 0f)) { EffectId = 11 },
                new Spec("palmtree_explosion02", Kind.Prop, new Vector3(0f, 9f, -26f), new Vector3(0f, 8f, 0f)) { EffectId = 12 },
                new Spec("palmtree_explosion03", Kind.Prop, new Vector3(0f, 9f, -26f), new Vector3(0f, 8f, 0f)) { EffectId = 13 },
                new Spec("hydrant_explosion", Kind.Prop, new Vector3(0f, 6f, -20f), new Vector3(0f, 5f, 0f)) { EffectId = 2 },
                new Spec("getitem", Kind.Item, Vector3.zero, Vector3.zero),
            };
            foreach (var n in new[] { "arrest_success", "arrest_fail", "beat_success", "beat_fail", "pvp_winner", "pvp_finish",
                                      "ui_levelup", "congratulation", "critical", "goldentime", "im_success", "im_failed",
                                      "arbeit_com", "arbeit_fail", "quest_success", "quest_fail", "qs_newrecord", "channel_count",
                                      "pvp_countdownstart", "christ_gift" })
                specs.Add(new Spec(n, Kind.Screen, Vector3.zero, Vector3.zero));

            string only = System.Environment.GetEnvironmentVariable("DC_FX_ONLY");
            if (!string.IsNullOrEmpty(only))
            {
                var set = new HashSet<string>(only.Split(',').Select(s => s.Trim()), System.StringComparer.OrdinalIgnoreCase);
                // names not in the list play as screen effects (an effects\ui name or a ScreenTable path)
                foreach (var n in set)
                    if (!specs.Any(x => string.Equals(x.Name, n, System.StringComparison.OrdinalIgnoreCase))) specs.Add(new Spec(n, Kind.Screen, Vector3.zero, Vector3.zero));
                specs = specs.Where(s => set.Contains(s.Name)).ToList();
            }
            float[] fixedTimes = null;
            string env = System.Environment.GetEnvironmentVariable("DC_FX_TIMES");
            if (!string.IsNullOrEmpty(env)) fixedTimes = env.Split(',').Select(s => float.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray();

            // world stage
            var stage = new GameObject("FX Stage");
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.transform.SetParent(stage.transform, false);
            ground.transform.localScale = new Vector3(20f, 1f, 20f);
            ground.GetComponent<Renderer>().sharedMaterial = new Material(Shader.Find("Unlit/Color")) { color = new Color(0.32f, 0.33f, 0.34f) };
            var car = GameObject.CreatePrimitive(PrimitiveType.Cube);
            car.name = "Car";
            car.transform.SetParent(stage.transform, false);
            car.transform.localPosition = new Vector3(0f, 0.65f, 0f);
            car.transform.localScale = new Vector3(1.8f, 1.3f, 4.4f);
            car.GetComponent<Renderer>().sharedMaterial = new Material(Shader.Find("Unlit/Color")) { color = new Color(0.15f, 0.22f, 0.45f) };
            // the effects follow a car root at ground level, as DriftCar's transform is
            var carRoot = new GameObject("Car Root");
            carRoot.transform.SetParent(stage.transform, false);
            var box = carRoot.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, 0.65f, 0f);
            box.size = new Vector3(1.8f, 1.3f, 4.4f);
            var light = new GameObject("Sun").AddComponent<Light>();
            light.type = LightType.Directional;
            light.transform.rotation = Quaternion.Euler(50f, 30f, 0f);
            var cam = new GameObject("FX Camera").AddComponent<Camera>();
            cam.fieldOfView = 50f;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 600f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.47f, 0.6f, 0.75f);
            cam.cullingMask = ~(1 << GameEffects.ScreenLayer);

            var screenCam = GameEffects.ScreenCamera;
            screenCam.clearFlags = CameraClearFlags.SolidColor;
            screenCam.backgroundColor = new Color(0.16f, 0.18f, 0.22f);

            foreach (var s in specs)
            {
                NifEffect fx = null;
                switch (s.Kind)
                {
                    case Kind.World: fx = GameEffects.At(s.Name, Vector3.zero); break;
                    case Kind.Car: fx = GameEffects.Attach(s.Name, carRoot.transform, Vector3.zero, false); break;
                    case Kind.CarRotated:
                        var offset = s.Name.StartsWith("graze") ? GameEffects.GrazeOffset(new Vector3(1.8f, 1.3f, 4.4f), s.Name == "graze_l") : Vector3.zero;
                        fx = GameEffects.Attach(s.Name, carRoot.transform, offset, true);
                        break;
                    case Kind.Prop: fx = GameEffects.PropHit(s.EffectId, Vector3.zero); break;
                    case Kind.Screen: fx = GameEffects.Screen(s.Name); break;
                    case Kind.Item: fx = GameEffects.GetItem(); break;
                }
                if (!fx) { Debug.LogWarning($"Drift City effect shots: {s.Name} did not load"); continue; }
                car.SetActive(s.Kind == Kind.Car || s.Kind == Kind.CarRotated);
                bool screen = s.Kind == Kind.Screen || s.Kind == Kind.Item;
                float duration = fx.Duration > 0f ? fx.Duration : 2f;
                var times = fixedTimes ?? Fractions.Select(f => f * duration).ToArray();
                Debug.Log($"Drift City effect shots: {s.Name} keys {fx.KeyStart:F3}..{fx.KeyEnd:F3} duration {fx.Duration:F3} channels {(fx.Animator ? fx.Animator.Channels.Count : 0)} " +
                          $"particle systems {(fx.Particles ? fx.Particles.GetComponentsInChildren<ParticleSystem>().Length : 0)} renderers {fx.GetComponentsInChildren<MeshRenderer>(true).Length}" +
                          (fx.Animator ? " [" + string.Join(" ", fx.Animator.Channels.Select(c => $"{c.GetType().Name.Replace("Channel", "")}:{c.Path}:{c.Start:F2}-{c.Stop:F2}/c{c.Cycle}")) + "]" : ""));
                if (!screen) { cam.transform.position = s.Eye; cam.transform.LookAt(s.Look); }
                float t = 0f;
                const float step = 1f / 30f;
                foreach (float target in times.OrderBy(x => x))
                {
                    while (t + step <= target + 1e-4f) { Step(fx, step); t += step; }
                    fx.Pose(target);
                    Shot(screen ? screenCam : cam, $"fx_{Path.GetFileName(s.Name.Replace('\\', '/'))}_{target.ToString("0.00", CultureInfo.InvariantCulture)}.png", fx);
                }
                fx.Ended = null;
                fx.End();
            }
            GameEffects.Clear();
            Object.DestroyImmediate(stage);
        }

        static void Step(NifEffect fx, float dt)
        {
            fx.Pose(fx.Time + dt);
            if (!fx.Particles) return;
            fx.Particles.Advance(dt);
            foreach (var ps in fx.Particles.GetComponentsInChildren<ParticleSystem>())
                if (!ps.transform.parent || !ps.transform.parent.GetComponent<ParticleSystem>())
                    ps.Simulate(dt, true, false, false);
        }

        static void Shot(Camera cam, string file, NifEffect fx)
        {
            const int w = 1024, h = 768;
            var rt = new RenderTexture(w, h, 24);
            cam.targetTexture = rt;
            bool was = cam.enabled;
            cam.Render();
            cam.targetTexture = null;
            cam.enabled = was;
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            File.WriteAllBytes(Path.Combine("Preview", file), tex.EncodeToPNG());
            RenderTexture.active = null;
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(tex);
            int particles = fx.Particles ? fx.Particles.GetComponentsInChildren<ParticleSystem>().Sum(p => p.particleCount) : 0;
            var visible = fx.GetComponentsInChildren<MeshRenderer>().Where(r => r.enabled && r.sharedMaterial && r.sharedMaterial.color.a > 0.01f)
                .Select(r => $"{r.transform.parent?.name}/{r.name}:{r.sharedMaterial.color.a:F2}@{r.bounds.center.x:F1},{r.bounds.center.y:F1},{r.bounds.center.z:F1}");
            Debug.Log($"Drift City effect shots: {file} t={fx.Time:F2} particles {particles} visible [{string.Join(" ", visible)}]");
        }
    }
}
