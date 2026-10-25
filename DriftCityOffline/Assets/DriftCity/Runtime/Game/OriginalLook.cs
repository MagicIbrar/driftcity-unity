using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// The game's own lighting, read from init/weather.ini and init/light.ini the way the client does
    /// (DriftCity_RE/decompiled/phys/0x432390.c): a ground light for the world and a car light for vehicles,
    /// each turned by FromEulerAnglesXYZ(-90°, direction, altitude) * UNIT_X, with ambient/diffuse/specular
    /// times the intensity (dimmer); fog color and depth, shadow darkness and the cube map strength. The first
    /// weather ("cloud") is the one the client starts with. Shader globals are read by DCOriginal.cginc.
    /// </summary>
    public static class OriginalLook
    {
        public struct LightDef { public Vector3 Dir; public Color Ambient, Diffuse, Specular; }

        public sealed class Weather
        {
            public string Name;
            public float VisibleRange, FogDepth, ShadowDark, ShadowAffect, EnvStrength, RainRate;
            public bool Fog, LensFlare, Rain;
            public Color FogColor, SkyColor;
            public LightDef Ground, Car;
        }

        /// <summary>Far plane of the chase camera (init/camera.ini [camera4]); the fog depth is a fraction of it.</summary>
        public const float ChaseFar = 512f;

        static List<Weather> weathers;
        static LightDef garageCar, garageCommon;
        static Cubemap envCube;
        static Texture2D rampVehicle, rampObject;

        public static Weather Current { get { Load(); return weathers[Mathf.Clamp(weatherIndex, 0, weathers.Count - 1)]; } }
        public static int WeatherCount { get { Load(); return weathers.Count; } }
        static int weatherIndex;

        /// <summary>Index into init/weather.ini (0 cloud, 1 fog, 2 heavy_rain, 3 fine_sunset in the shipped file).</summary>
        public static int WeatherIndex { get => weatherIndex; set { Load(); weatherIndex = Mathf.Clamp(value, 0, weathers.Count - 1); } }

        public static int FindWeather(string name) { Load(); return weathers.FindIndex(x => x.Name == name); }

        /// <summary>
        /// The weather the server would be sending: the client only reacts to the server's weather packet, so the hour
        /// below is this rebuild's own timetable. It runs on UTC so every player (offline or on any server) sees the same
        /// sky: mostly clear, a long sunset, a short fog and a rain shower each hour.
        /// </summary>
        public static int ScheduledWeather(System.DateTime utc)
        {
            int m = utc.Minute;
            string name = m < 20 ? "cloud" : m < 32 ? "fine_sunset" : m < 44 ? "cloud" : m < 48 ? "fog" : m < 54 ? "heavy_rain" : "cloud";
            int i = FindWeather(name);
            return i < 0 ? 0 : i;
        }

        static void Load()
        {
            if (weathers != null) return;
            weathers = new List<Weather>();
            var w = Ini("init\\weather.ini");
            int count = w.TryGetValue("WeatherInfo", out var info) && info.TryGetValue("weathercnt", out var c) ? (int)Num(c) : 0;
            for (int i = 1; i <= count; i++)
            {
                if (!w.TryGetValue(i.ToString(), out var s)) continue;
                weathers.Add(new Weather
                {
                    Name = Get(s, "name", "cloud"),
                    VisibleRange = Num(Get(s, "visible_range", "350")),
                    Fog = Get(s, "fogenable", "true").Trim().ToLowerInvariant() == "true",
                    LensFlare = Get(s, "lensflare", "false").Trim().ToLowerInvariant() == "true",
                    Rain = Get(s, "rainefx", "false").Trim().ToLowerInvariant() == "true",
                    RainRate = Num(Get(s, "rainrate", "0")),
                    FogDepth = Num(Get(s, "fogdepth", "0.7")),
                    FogColor = Rgb(Get(s, "fogcolor", "176.216.222")),
                    SkyColor = Rgb(Get(s, "skycolor", "100.100.150")),
                    ShadowDark = Num(Get(s, "shadowdark", "0.5")),
                    ShadowAffect = Num(Get(s, "shadowaffect", "0.4")),
                    EnvStrength = Num(Get(s, "envmapstrength", "0.2")),
                    Ground = Light(s, "ground_"),
                    Car = Light(s, "car_"),
                });
            }
            if (weathers.Count == 0)
                weathers.Add(new Weather { Name = "cloud", VisibleRange = 350, Fog = true, FogDepth = 0.7f, FogColor = Rgb("176.216.222"), SkyColor = Rgb("100.100.150"),
                    ShadowDark = 0.5f, ShadowAffect = 0.4f, EnvStrength = 0.2f, Ground = Light(new Dictionary<string, string>(), "ground_"), Car = Light(new Dictionary<string, string>(), "car_") });
            var l = Ini("init\\light.ini");
            garageCar = Light(l.TryGetValue("CAR_LIGHT", out var cl) ? cl : new Dictionary<string, string>(), "");
            garageCommon = Light(l.TryGetValue("COMMON_LIGHT", out var gl) ? gl : new Dictionary<string, string>(), "");
        }

        static LightDef Light(Dictionary<string, string> s, string prefix)
        {
            float alt = Num(Get(s, prefix + "altitude", prefix == "ground_" ? "40" : "89"));
            float dir = Num(Get(s, prefix + "direction", "-45"));
            float dim = Num(Get(s, prefix + "intensity", "1"));
            return new LightDef
            {
                Dir = Direction(alt, dir),
                Ambient = Rgb(Get(s, prefix + "ambient", "128.128.128")) * dim,
                Diffuse = Rgb(Get(s, prefix + "diffuse", "170.170.230")) * dim,
                Specular = Rgb(Get(s, prefix + "specular", "160.160.160")) * dim,
            };
        }

        /// <summary>Gamebryo X(-90°)·Y(direction)·Z(altitude)·UNIT_X = (cos d cos a, -sin d cos a, -sin a), z up; to Unity.</summary>
        public static Vector3 Direction(float altitude, float direction)
        {
            float a = altitude * Mathf.Deg2Rad, d = direction * Mathf.Deg2Rad;
            var g = new Vector3(Mathf.Cos(d) * Mathf.Cos(a), -Mathf.Sin(d) * Mathf.Cos(a), -Mathf.Sin(a));
            return new Vector3(g.x, g.z, g.y).normalized;
        }

        /// <summary>World and car lights, fog, shadows, cube map and toon ramps for the city.</summary>
        public static void Apply()
        {
            ApplyGlobals();
            var w = Current;
            // the sun only casts Unity's shadow maps now; its colour is not used by the game's shaders
            var sun = Object.FindAnyObjectByType<Light>();
            if (!sun || sun.type != LightType.Directional) { sun = new GameObject("Sun").AddComponent<Light>(); sun.type = LightType.Directional; }
            sun.transform.rotation = Quaternion.LookRotation(w.Ground.Dir, Vector3.up);
            sun.color = Color.white;
            sun.intensity = 1f;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 1f;
            QualitySettings.shadowDistance = 150f;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = w.Ground.Ambient;
            RenderSettings.fog = w.Fog;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = w.FogColor;
            RenderSettings.fogEndDistance = ChaseFar;
            RenderSettings.fogStartDistance = ChaseFar * (1f - Mathf.Clamp01(w.FogDepth));
        }

        /// <summary>Only the shader globals of the current weather (cheap; restores them after a garage preview).</summary>
        public static void ApplyGlobals()
        {
            Load();
            var w = Current;
            SetLights(w.Ground, w.Car);
            Shader.SetGlobalFloat("_DCShadowDark", w.ShadowDark);
            Shader.SetGlobalFloat("_DCShadowAffect", w.ShadowAffect);
            Shader.SetGlobalFloat("_DCEnvStrength", w.EnvStrength);
            Shader.SetGlobalFloat("_DCCarShader", 1f);   // CarShader=2 with CarSpecular: AGBody_cube_phong
            Shader.SetGlobalTexture("_DCEnvCube", EnvCube());
            if (!rampVehicle) rampVehicle = Ramp("shader\\texture\\toonramp_vehicle.bmp");
            if (!rampObject) rampObject = Ramp("shader\\texture\\toonramp_object.bmp");
            Shader.SetGlobalTexture("_DCToonRampVehicle", rampVehicle);
            Shader.SetGlobalTexture("_DCToonRampObject", rampObject);
        }

        /// <summary>The garage/shop lights of init/light.ini (car previews on the menus).</summary>
        public static void ApplyGarage()
        {
            ApplyGlobals();
            SetLights(garageCommon, garageCar);
            Shader.SetGlobalFloat("_DCShadowAffect", 0f);
        }

        static void SetLights(LightDef ground, LightDef car)
        {
            Shader.SetGlobalVector("_DCGroundDir", ground.Dir);
            Shader.SetGlobalVector("_DCGroundAmb", ground.Ambient);
            Shader.SetGlobalVector("_DCGroundDiff", ground.Diffuse);
            Shader.SetGlobalVector("_DCGroundSpec", ground.Specular);
            Shader.SetGlobalVector("_DCCarDir", car.Dir);
            Shader.SetGlobalVector("_DCCarAmb", car.Ambient);
            Shader.SetGlobalVector("_DCCarDiff", car.Diffuse);
            Shader.SetGlobalVector("_DCCarSpec", car.Specular);
        }

        /// <summary>vehicle/texture/{left,right,front,back,up,down}.dds as NiSourceCubeMap(+X,-X,+Y,-Y,+Z,-Z) in Gamebryo space.</summary>
        static Cubemap EnvCube()
        {
            if (envCube) return envCube;
            var faces = new[] { (CubemapFace.PositiveX, "left"), (CubemapFace.NegativeX, "right"), (CubemapFace.PositiveY, "front"),
                                (CubemapFace.NegativeY, "back"), (CubemapFace.PositiveZ, "up"), (CubemapFace.NegativeZ, "down") };
            var textures = new List<(CubemapFace, Texture2D)>();
            int size = 0;
            foreach (var (face, name) in faces)
            {
                var t = TextureLibrary.Get($"vehicle\\texture\\{name}.dds", "vehicle\\texture");
                if (!t) continue;
                textures.Add((face, t));
                size = Mathf.Max(size, t.width);
            }
            if (size == 0) return null;
            envCube = new Cubemap(size, TextureFormat.RGBA32, false) { name = "Vehicle env cube" };
            var rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32);
            var read = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var prev = RenderTexture.active;
            foreach (var (face, t) in textures)
            {
                // the face textures may be compressed or unreadable: go through a render target to read them back
                Graphics.Blit(t, rt);
                RenderTexture.active = rt;
                read.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                read.Apply();
                envCube.SetPixels(read.GetPixels(), face);
            }
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            Object.Destroy(read);
            envCube.Apply();
            return envCube;
        }

        static Texture2D Ramp(string path)
        {
            var t = TextureLibrary.Get(path, "shader\\texture");
            if (t) { t.wrapMode = TextureWrapMode.Clamp; t.filterMode = FilterMode.Bilinear; }
            return t;
        }

        /// <summary>The sky dome of the current weather (effects\skydome.nif holds one dome per weather).</summary>
        public static GameObject BuildSky()
        {
            var fs = DriftCityData.Files;
            if (!fs.Exists("effects\\skydome.nif")) return null;
            var sky = new NifSceneBuilder().Load("effects\\skydome.nif");
            sky.name = "Sky Dome";
            var shader = Shader.Find("DriftCity/Sky");
            foreach (var r in sky.GetComponentsInChildren<Renderer>(true))
            {
                if (!shader) continue;
                var m = OwnedAssets.Track(sky, new Material(shader) { name = "sky " + r.name });
                m.mainTexture = r.sharedMaterial.mainTexture;
                r.sharedMaterial = m;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
            sky.AddComponent<SkyFollow>();
            ShowSky(sky);
            return sky;
        }

        /// <summary>Shows the dome of the current weather (skydome.nif carries all of them).</summary>
        public static void ShowSky(GameObject sky)
        {
            if (!sky) return;
            string want = SkyFor(Current.Name);
            foreach (var r in sky.GetComponentsInChildren<Renderer>(true)) r.gameObject.SetActive(r.name.ToLowerInvariant() == want);
        }

        static string SkyFor(string weather)
        {
            switch (weather)
            {
                case "fog": return "skydom_fog";
                case "heavy_rain": return "skydom_rain";
                case "fine_sunset": return "skydom_sunset_cloud";
                default: return "skydom_fine_cloud";
            }
        }

        // ---------------- ini reading ----------------
        public static Dictionary<string, Dictionary<string, string>> Ini(string path)
        {
            var result = new Dictionary<string, Dictionary<string, string>>();
            var fs = DriftCityData.Files;
            if (!fs.Exists(path)) return result;
            var data = fs.Read(path);
            string text = data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE ? System.Text.Encoding.Unicode.GetString(data, 2, data.Length - 2) : Core.Cp949.Decode(data);
            Dictionary<string, string> section = null;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw;
                int cut = line.IndexOf(';'); if (cut >= 0) line = line.Substring(0, cut);
                if (line.TrimStart().StartsWith("//")) continue;
                line = line.Trim();
                if (line.Length == 0) continue;
                if (line[0] == '[') { int end = line.IndexOf(']'); var name = end > 1 ? line.Substring(1, end - 1) : ""; result[name] = section = new Dictionary<string, string>(); continue; }
                int eq = line.IndexOf('=');
                if (eq <= 0 || section == null) continue;
                section[line.Substring(0, eq).Trim().ToLowerInvariant()] = line.Substring(eq + 1).Trim();
            }
            return result;
        }

        static string Get(Dictionary<string, string> s, string key, string fallback) => s.TryGetValue(key, out var v) && v.Length > 0 ? v : fallback;

        public static float Num(string s)
        {
            s = s.Trim().TrimEnd('f', 'F', ';');
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;
        }

        static Color Rgb(string s)
        {
            var p = s.Split('.', ':');
            float C(int i) => i < p.Length && float.TryParse(p[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v / 255f : 1f;
            return new Color(C(0), C(1), C(2), 1f);
        }
    }

    /// <summary>Keeps the sky dome centred on the camera, as the client does.</summary>
    public sealed class SkyFollow : MonoBehaviour
    {
        void LateUpdate()
        {
            var cam = Camera.main;
            if (cam) transform.position = cam.transform.position;
        }
    }

    /// <summary>Car previews on the menus use the garage lights of init/light.ini while this camera renders.</summary>
    [RequireComponent(typeof(Camera))]
    public sealed class GarageLighting : MonoBehaviour
    {
        void OnPreRender() => OriginalLook.ApplyGarage();
        void OnPostRender() => OriginalLook.ApplyGlobals();
    }
}
