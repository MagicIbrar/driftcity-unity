using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// Switches the city between the four weathers of init/weather.ini (cloud, fog, heavy_rain, fine_sunset) on the
    /// timetable of <see cref="OriginalLook.ScheduledWeather"/>: lights, fog and shadows, the matching sky dome of
    /// effects\skydome.nif, the lens flare (weathers with lensflare = true) and, for rainefx weathers, rain streaks
    /// around the camera at the file's rainrate. The client's rain_efx.nif is a NiMeshParticleSystem dropping
    /// rain_02.tga planes inside an 8.7 m box over the camera; it is rebuilt here as a Unity particle system.
    /// </summary>
    public sealed class WeatherClock : MonoBehaviour
    {
        public GameObject Sky;
        /// <summary>Under a roof (the parking lot's dome): lights and sky follow the weather, the rain doesn't fall.</summary>
        public bool Indoors;
        /// <summary>Set to stop following the clock (tests, tools): the weather then stays where it is put.</summary>
        public static bool Frozen;

        ParticleSystem rain;
        float nextCheck;

        public static WeatherClock Attach(GameObject world, GameObject sky, bool indoors = false)
        {
            var clock = world.AddComponent<WeatherClock>();
            clock.Sky = sky;
            clock.Indoors = indoors;
            if (!Frozen) OriginalLook.WeatherIndex = OriginalLook.ScheduledWeather(Net.ServerNotices.UtcNow);
            clock.Refresh();
            return clock;
        }

        void Update()
        {
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 2f;
            if (Frozen) return;
            int want = OriginalLook.ScheduledWeather(Net.ServerNotices.UtcNow);
            if (want == OriginalLook.WeatherIndex) return;
            OriginalLook.WeatherIndex = want;
            Refresh();
        }

        /// <summary>Re-applies the current weather (also after it was changed from outside).</summary>
        public void Refresh()
        {
            if (GameFlow.Instance) GameFlow.Instance.ReapplyLighting();
            else OriginalLook.Apply();
            OriginalLook.ShowSky(Sky);
            var w = OriginalLook.Current;
            if (w.Rain && !Indoors) StartRain(); else if (rain) rain.gameObject.SetActive(false);
        }

        void LateUpdate()
        {
            if (rain && rain.gameObject.activeSelf && Camera.main) rain.transform.position = Camera.main.transform.position + Vector3.up * 12f;
        }

        void StartRain()
        {
            // Christmas with a finished tree: the rain weathers snow instead (effects\x_snow.nif, christ_snow.dds)
            bool snow = SeasonEvents.Is(EventSeason.Christmas) && SeasonEvents.XmasCitiesDone > 0;
            if (rain && rain.name != (snow ? "Snow" : "Rain")) { Destroy(rain.gameObject); rain = null; }
            if (!rain) rain = snow ? BuildSnow(transform) : BuildRain(transform);
            if (rain) rain.gameObject.SetActive(true);
        }

        static ParticleSystem BuildSnow(Transform parent)
        {
            var tex = TextureLibrary.Get("effects/christ_snow.dds", "effects");
            var go = new GameObject("Snow");
            go.transform.SetParent(parent, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = true;
            main.startLifetime = 9f;
            main.startSpeed = 0f;
            main.startSize = new ParticleSystem.MinMaxCurve(0.08f, 0.16f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new Color(1f, 1f, 1f, 0.85f);
            main.maxParticles = 5000;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            var vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;
            vel.x = new ParticleSystem.MinMaxCurve(-0.8f, 0.8f);
            vel.y = new ParticleSystem.MinMaxCurve(-2.2f, -1.4f);
            vel.z = new ParticleSystem.MinMaxCurve(-0.8f, 0.8f);
            var noise = ps.noise;
            noise.enabled = true;
            noise.strength = 0.6f;
            noise.frequency = 0.3f;
            var emission = ps.emission;
            emission.rateOverTime = 500f;
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(40f, 1f, 40f);
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.maxParticleSize = 0.02f;
            var shader = Shader.Find("Legacy Shaders/Particles/Alpha Blended") ?? Shader.Find("Sprites/Default");
            var m = OwnedAssets.Track(go, new Material(shader) { name = "snow" });
            if (tex) m.mainTexture = tex;
            r.sharedMaterial = m;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            ps.Play();
            return ps;
        }

        static ParticleSystem BuildRain(Transform parent)
        {
            var tex = TextureLibrary.Get("effects/rain_02.tga", "effects");
            var go = new GameObject("Rain");
            go.transform.SetParent(parent, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = true;
            main.startLifetime = 1.1f;
            main.startSpeed = 0f;
            main.startSize = new ParticleSystem.MinMaxCurve(0.025f, 0.04f);
            main.startColor = new Color(0.78f, 0.82f, 0.9f, 0.35f);
            main.maxParticles = 4000;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            var vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;
            vel.x = new ParticleSystem.MinMaxCurve(-0.6f, 0.6f);
            vel.y = new ParticleSystem.MinMaxCurve(-24f, -20f);
            vel.z = new ParticleSystem.MinMaxCurve(-0.6f, 0.6f);
            var emission = ps.emission;
            // the file's rainrate (400 drops a second) inside the client's small box; a wider box needs more to look as dense
            emission.rateOverTime = Mathf.Max(400f, OriginalLook.Current.RainRate) * 6f;
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(40f, 1f, 40f);
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Stretch;
            r.velocityScale = 0.05f;
            r.lengthScale = 1f;
            // drops passing right by the lens would fill the screen: keep every streak hairline-thin on screen
            r.maxParticleSize = 0.004f;
            var shader = Shader.Find("Legacy Shaders/Particles/Alpha Blended") ?? Shader.Find("Sprites/Default");
            var m = OwnedAssets.Track(go, new Material(shader) { name = "rain" });
            if (tex) m.mainTexture = tex;
            r.sharedMaterial = m;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            ps.Play();
            return ps;
        }
    }
}
