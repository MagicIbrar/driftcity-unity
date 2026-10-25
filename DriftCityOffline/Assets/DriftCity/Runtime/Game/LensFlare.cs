using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// The weather's lens flare (lensflare = true in init\weather.ini), as the client draws it
    /// (DriftCity_RE/decompiled/phys/0x401130.c reads init\flare.ini, 0x4013e0.c places the spots): the sun is
    /// projected to the screen, every spot sits on the line from the screen centre through the sun at its
    /// "Length", sized by "Scale" and tinted by "Color"; all fade out as the sun goes up to 300 px off screen
    /// and vanish when it is behind the camera.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public sealed class LensFlare : MonoBehaviour
    {
        struct Spot { public int Image; public float Length, Scale; public Color Color; }

        readonly List<Spot> spots = new List<Spot>();
        readonly List<Material> images = new List<Material>();
        Camera cam;
        bool loaded;

        public static void Attach(Camera camera)
        {
            if (camera && !camera.GetComponent<LensFlare>()) camera.gameObject.AddComponent<LensFlare>();
        }

        void Load()
        {
            loaded = true;
            var fs = DriftCityData.Files;
            var shader = Shader.Find("DriftCity/Flare");
            if (!shader || !fs.Exists(@"init\flare.ini")) return;
            var data = fs.Read(@"init\flare.ini");
            string section = null;
            var values = new Dictionary<string, Dictionary<string, string>>();
            foreach (var raw in Core.Cp949.Decode(data).Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("[")) { section = line.Trim('[', ']'); values[section] = new Dictionary<string, string>(); continue; }
                int eq = line.IndexOf('=');
                if (eq > 0 && section != null) values[section][line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim().TrimEnd('f');
            }
            if (!values.TryGetValue("LensFlare", out var head)) return;
            int imageCount = Int(head, "ImageCnt"), spotCount = Int(head, "SpotCnt");
            for (int i = 0; i < imageCount; i++)
            {
                var t = TextureLibrary.Get($@"effects\flare{i}.dds", "effects");
                images.Add(t ? OwnedAssets.Track(gameObject, new Material(shader) { mainTexture = t }) : null);
            }
            for (int i = 0; i < spotCount; i++)
            {
                if (!values.TryGetValue("Spot" + i, out var s)) continue;
                var c = (s.TryGetValue("Color", out var cs) ? cs : "255:255:255").Split(':');
                float C(int k) => k < c.Length && float.TryParse(c[k], NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v / 255f : 1f;
                spots.Add(new Spot { Image = Int(s, "Image"), Length = Float(s, "Length"), Scale = Float(s, "Scale"), Color = new Color(C(0), C(1), C(2), 1f) });
            }
        }

        static int Int(Dictionary<string, string> s, string k) => s.TryGetValue(k, out var v) && int.TryParse(v, out int i) ? i : 0;
        static float Float(Dictionary<string, string> s, string k) => s.TryGetValue(k, out var v) && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0f;

        void OnPostRender()
        {
            if (!cam) cam = GetComponent<Camera>();
            if (!loaded) Load();
            var w = OriginalLook.Current;
            if (spots.Count == 0 || !w.LensFlare || !GameFlow.Instance || !GameFlow.Instance.InCity) return;
            // the sun: far along the direction the ground light comes from
            var sunWorld = cam.transform.position - w.Ground.Dir * 10000f;
            var sp = cam.WorldToScreenPoint(sunWorld);
            if (sp.z <= 0) return;
            float W = cam.pixelWidth, H = cam.pixelHeight;
            float off = Mathf.Max(sp.x < 0 ? -sp.x : sp.x > W ? sp.x - W : 0, sp.y < 0 ? -sp.y : sp.y > H ? sp.y - H : 0);
            float alpha = 1f - Mathf.Min(off, 300f) / 300f;
            if (alpha <= 0) return;
            // the sun hidden behind a building: no flare
            if (Physics.Raycast(cam.transform.position, -w.Ground.Dir, 3000f, ~0, QueryTriggerInteraction.Ignore)) return;
            var center = new Vector2(W * 0.5f, H * 0.5f);
            var sun = new Vector2(sp.x, sp.y);
            GL.PushMatrix();
            GL.LoadPixelMatrix(0, W, 0, H);
            foreach (var s in spots)
            {
                if (s.Image < 0 || s.Image >= images.Count || !images[s.Image]) continue;
                var pos = center + (sun - center) * s.Length;
                float half = s.Scale * H * 0.15f;
                images[s.Image].SetPass(0);
                GL.Begin(GL.QUADS);
                GL.Color(new Color(s.Color.r, s.Color.g, s.Color.b, alpha));
                GL.TexCoord2(0, 0); GL.Vertex3(pos.x - half, pos.y - half, 0);
                GL.TexCoord2(0, 1); GL.Vertex3(pos.x - half, pos.y + half, 0);
                GL.TexCoord2(1, 1); GL.Vertex3(pos.x + half, pos.y + half, 0);
                GL.TexCoord2(1, 0); GL.Vertex3(pos.x + half, pos.y - half, 0);
                GL.End();
            }
            GL.PopMatrix();
        }
    }
}
