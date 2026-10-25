using System.Globalization;
using System.IO;
using System.Linq;
using DriftCity.Game;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DriftCity.EditorTools
{
    /// <summary>
    /// Batch renders of the new-account story intro (IntroScene) at fixed times: Preview/intro_&lt;n&gt;.png, 1024x768, with
    /// the subtitles. DC_INTRO_TIMES = comma-separated seconds to override the default list; DC_INTRO_WIDE=1 also writes
    /// 1366x768 frames (intro_wide_&lt;n&gt;.png) to check the client's narrowed viewport on wide screens.
    /// </summary>
    public static class DriftCityIntroShots
    {
        static readonly float[] DefaultTimes =
        {
            1f, 4f, 7.5f, 10f, 12.5f, 14.2f, 15.6f, 17.3f, 18.2f, 20f, 22.5f, 25f, 27.5f, 29.5f, 30.8f, 32.2f, 33.5f,
            36.4f, 37.6f, 39.5f, 41.5f, 43.6f, 44.5f, 45.5f, 48.5f, 50.5f, 53f, 54.5f, 57f, 59.5f, 62f, 63.3f,
        };

        public static void Render()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            DriftCityData.Mount(DriftCityData.ResolveGameRoot(@"D:\Drift_City_Remastered_Alpha_08-2025\Drift City Remastered Alpha"));
            Directory.CreateDirectory("Preview");
            var times = DefaultTimes;
            string env = System.Environment.GetEnvironmentVariable("DC_INTRO_TIMES");
            if (!string.IsNullOrEmpty(env))
                times = env.Split(',').Select(s => float.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray();
            bool wide = System.Environment.GetEnvironmentVariable("DC_INTRO_WIDE") == "1";

            var s = IntroScene.Session.Build(overlayText: false);
            if (s == null) { Debug.LogError("Drift City intro shots: game\\intro\\newintro.nif not found"); return; }
            Debug.Log($"Drift City intro shots: time range {s.Start:F3}..{s.End:F3} s, {s.Root.GetComponentsInChildren<Renderer>(true).Length} renderers");

            var ui = new GameObject("Intro UI Camera").AddComponent<Camera>();
            ui.transform.SetParent(s.Root.transform, false);
            ui.transform.localPosition = new Vector3(0f, 0f, -1000f);
            ui.clearFlags = CameraClearFlags.Nothing;
            ui.cullingMask = 1 << 5;
            ui.depth = 100;
            s.Canvas.renderMode = RenderMode.ScreenSpaceCamera;
            s.Canvas.worldCamera = ui;
            s.Canvas.planeDistance = 1f;

            for (int i = 0; i < times.Length; i++)
            {
                Shot(s, ui, times[i], 1024, 768, $"intro_{i}.png");
                if (wide) Shot(s, ui, times[i], 1366, 768, $"intro_wide_{i}.png");
            }
            s.Dispose();
        }

        static void Shot(IntroScene.Session s, Camera ui, float t, int w, int h, string file)
        {
            var rt = new RenderTexture(w, h, 24);
            s.Backdrop.targetTexture = rt;
            s.Camera.targetTexture = rt;
            ui.targetTexture = rt;
            s.Pose(t);
            Canvas.ForceUpdateCanvases();
            s.Backdrop.Render();
            s.Camera.Render();
            ui.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            File.WriteAllBytes(Path.Combine("Preview", file), tex.EncodeToPNG());
            RenderTexture.active = null;
            s.Backdrop.targetTexture = null;
            s.Camera.targetTexture = null;
            ui.targetTexture = null;
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(tex);
            var visible = s.Root.GetComponentsInChildren<MeshRenderer>()
                .Where(r => r.sharedMaterial && r.sharedMaterial.color.a > 0.01f && Mathf.Abs(r.bounds.center.y - s.Root.transform.position.y) < 45f)
                .Select(r => $"{r.transform.parent?.name}/{r.name}:{r.sharedMaterial.color.a:F2}");
            var lines = s.Canvas.GetComponentsInChildren<UnityEngine.UI.Text>().Where(x => x.enabled).Select(x => x.name);
            Debug.Log($"Drift City intro shots: {file} t={t:F2} subtitles [{string.Join(",", lines)}] visible [{string.Join(" ", visible)}]");
        }
    }
}
