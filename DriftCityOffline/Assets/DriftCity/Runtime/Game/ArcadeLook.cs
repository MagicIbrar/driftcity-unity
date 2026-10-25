using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// The arcade toon look on the main camera: cel-shaded lighting (shader globals), ink outlines, punchy
    /// colour, a cartoon sky, speed lines and a wider field of view at speed. Off = the original look.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public sealed class ArcadeLook : MonoBehaviour
    {
        const string Pref = "dc_arcade_look";
        public static bool Enabled
        {
            get => PlayerPrefs.GetInt(Pref, 0) == 1;
            set { PlayerPrefs.SetInt(Pref, value ? 1 : 0); PlayerPrefs.Save(); ApplyGlobals(); ApplyEnvironment(); }
        }

        public float Saturation = 1.35f, Contrast = 1.06f, EdgeStrength = 0.85f, EdgeFar = 0.25f, Vignette = 0.28f, InkWidth = 1.2f;
        public float BaseFov = 60f, SpeedFov = 12f;

        Camera cam;
        Material post;
        float speed01;
        static Material sky, originalSky;

        public static void Attach(Camera camera)
        {
            if (!camera) return;
            if (!camera.GetComponent<ArcadeLook>()) camera.gameObject.AddComponent<ArcadeLook>();
            ApplyGlobals();
        }

        void OnEnable()
        {
            cam = GetComponent<Camera>();
            cam.depthTextureMode |= DepthTextureMode.DepthNormals;
            ApplyGlobals();
        }

        /// <summary>Shader globals the cel lighting reads (ToonLighting.cginc).</summary>
        public static void ApplyGlobals()
        {
            Shader.SetGlobalFloat("_DCToon", 0f);   // the game's own shaders do the shading now
            Shader.SetGlobalFloat("_DCToonShade", 0.64f);
            Shader.SetGlobalFloat("_DCToonRim", 0.65f);
            Shader.SetGlobalColor("_DCToonShadeTint", new Color(0.74f, 0.80f, 1.0f));
        }

        /// <summary>Sky, sun, ambient and fog for the toon look (called after the city's lighting is set up).</summary>
        public static void ApplyEnvironment()
        {
            var sun = FindAnyObjectByType<Light>();
            var cam = Camera.main;
            if (!Enabled)
            {
                if (sky && RenderSettings.skybox == sky) RenderSettings.skybox = originalSky;
                // the weather's sky dome writes no depth: a skybox (drawn after the opaque pass) would paint over it
                if (cam) { cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = OriginalLook.Current.FogColor; }
                return;
            }
            if (cam) cam.clearFlags = CameraClearFlags.Skybox;
            if (!sky)
            {
                var shader = Shader.Find("DriftCity/ToonSky");
                if (shader) sky = new Material(shader) { name = "Toon Sky" };
            }
            if (sky)
            {
                if (RenderSettings.skybox != sky) originalSky = RenderSettings.skybox;
                RenderSettings.skybox = sky;
                if (sun && sun.type == LightType.Directional) sky.SetVector("_SunDir", -sun.transform.forward);
            }
            if (sun && sun.type == LightType.Directional) { sun.color = new Color(1f, 0.96f, 0.88f); sun.intensity = 0.95f; sun.shadowStrength = 0.75f; }
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.50f, 0.58f, 0.76f);
            RenderSettings.ambientEquatorColor = new Color(0.46f, 0.49f, 0.56f);
            RenderSettings.ambientGroundColor = new Color(0.30f, 0.29f, 0.33f);
            RenderSettings.fogColor = new Color(0.70f, 0.86f, 1.0f);
            RenderSettings.fogStartDistance = 500f;
            RenderSettings.fogEndDistance = 2600f;
        }

        void LateUpdate()
        {
            var flow = GameFlow.Instance;
            var car = flow ? flow.PlayerCar : null;
            float target = 0f;
            if (car && Enabled) target = Mathf.Clamp01((car.SpeedKmh - 120f) / 120f) * 0.7f + (car.Boosting ? 0.6f : 0f);
            speed01 = Mathf.MoveTowards(speed01, Mathf.Clamp01(target), Time.deltaTime * 2.5f);
            if (cam && Enabled && GetComponent<ChaseCamera>())
                cam.fieldOfView = Mathf.Lerp(cam.fieldOfView, BaseFov + (Enabled ? speed01 * SpeedFov : 0f), 1 - Mathf.Exp(-5f * Time.deltaTime));
        }

        void OnRenderImage(RenderTexture src, RenderTexture dst)
        {
            // the full-screen shop and garage scenes cover the city: its ink edges would draw over them
            // (the custom shop's dialogs leave the car in the city in view, so it keeps the look)
            if (!Enabled || Menus.DealerOpen || OriginalMenus.IsOpen && !CustomShop.IsActive) { Graphics.Blit(src, dst); return; }
            if (!post)
            {
                var shader = Shader.Find("Hidden/DriftCity/ArcadePost");
                if (!shader) { Graphics.Blit(src, dst); return; }
                post = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            post.SetFloat("_Saturation", Saturation);
            post.SetFloat("_Contrast", Contrast);
            post.SetFloat("_EdgeStrength", EdgeStrength);
            post.SetFloat("_EdgeFar", EdgeFar);
            post.SetFloat("_Vignette", Vignette);
            post.SetFloat("_InkWidth", InkWidth);
            post.SetFloat("_Speed", speed01);
            post.SetColor("_InkColor", new Color(0.06f, 0.05f, 0.09f));
            Graphics.Blit(src, dst, post);
        }

        void OnDestroy() { if (post) Destroy(post); }
    }
}
