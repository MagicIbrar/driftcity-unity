using System.IO;
using System.Linq;
using DriftCity.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.EditorTools
{
    /// <summary>Batch-mode check: rebuilds selected game.nui dialogs and renders them to Preview/ui_*.png.</summary>
    public static class DriftCityUiPreview
    {
        static readonly string[] Keys =
        {
            "LOGIN", "SELECTSERVER", "SELECTCHARACTER", "CREATECHARACTER", "CREATECAR", "GAMELOADING",
            "MAINBUTTON", "SPEEDMETER", "MINIMAP", "GARAGE", "INVEN", "OPTION", "PARTSSHOP", "VSHOP", "QUEST", "QUIT",
        };

        public static void RenderDialogs()
        {
            var boot = new GameObject("tmp").AddComponent<OfflineBootstrap>();
            DriftCityData.Mount(boot.GameRoot);
            Object.DestroyImmediate(boot.gameObject);
            var doc = UiAssets.Layout;
            Directory.CreateDirectory("Preview");
            // DC_UI_IDS: groups of dialog ids drawn together, e.g. "4120000+4130000,4140000" -> Preview/ui_4120000+4130000.png
            string ids = System.Environment.GetEnvironmentVariable("DC_UI_IDS");
            if (!string.IsNullOrEmpty(ids))
            {
                foreach (var group in ids.Split(','))
                {
                    var set = group.Split('+').Select(int.Parse).ToList();
                    Render(set.Select(id => doc.Dialogs.FirstOrDefault(d => d.Id == id)).Where(d => d != null).ToList(), "ui_" + group + ".png");
                }
                return;
            }
            string only = System.Environment.GetEnvironmentVariable("DC_UI_KEYS");
            var keys = string.IsNullOrEmpty(only) ? Keys : only.Split(',');
            foreach (var key in keys)
            {
                var dialogs = doc.Dialogs.Where(d => d.Key == key).ToList();
                if (dialogs.Count == 0) { Debug.Log($"Drift City UI: no dialog for {key}"); continue; }
                Render(dialogs, "ui_" + key.ToLowerInvariant() + ".png");
            }
        }

        static void Render(System.Collections.Generic.List<DriftCity.Core.NuiDialog> dialogs, string file)
        {
            var camGo = new GameObject("ui cam");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.22f, 0.24f, 0.28f);
            var rt = new RenderTexture(1024, 768, 24);
            cam.targetTexture = rt;

            var canvasGo = new GameObject("canvas", typeof(RectTransform));
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = cam;
            canvas.planeDistance = 1;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1024, 768);

            int controls = 0;
            foreach (var d in dialogs) { NuiView.Create(d, canvas.transform); controls += d.Controls.Count; }
            Canvas.ForceUpdateCanvases();
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(1024, 768, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 1024, 768), 0, 0);
            tex.Apply();
            File.WriteAllBytes(Path.Combine("Preview", file), tex.EncodeToPNG());
            RenderTexture.active = null;
            Object.DestroyImmediate(canvasGo);
            Object.DestroyImmediate(camGo);
            Object.DestroyImmediate(rt);
            Debug.Log($"Drift City UI: wrote Preview/{file} ({dialogs.Count} dialogs, {controls} controls, ids {string.Join(",", dialogs.Select(x => x.Id))})");
        }
    }
}
