using System.IO;
using System.Linq;
using DriftCity.Game;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace DriftCity.EditorTools
{
    /// <summary>Main game scene and standalone Windows build.</summary>
    public static class DriftCityBuild
    {
        public const string GameScene = "Assets/Scenes/DriftCityGame.unity";
        public const string BuildDir = "Build/DriftCityOffline";

        [MenuItem("Drift City/Create Game Scene (full game)")]
        public static void CreateGameScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var cam = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            cam.GetComponent<Camera>().farClipPlane = 4000f;
            cam.AddComponent<ChaseCamera>();
            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.transform.rotation = Quaternion.Euler(52f, -35f, 0f);
            sun.shadows = LightShadows.Soft;
            new GameObject("Drift City").AddComponent<GameFlow>();
            RenderSettings.ambientMode = AmbientMode.Trilight;
            Directory.CreateDirectory("Assets/Scenes");
            EditorSceneManager.SaveScene(scene, GameScene);
            // the full game first; the quick driving scene stays available as the second entry
            var scenes = EditorBuildSettings.scenes.Where(s => s.path != GameScene).ToList();
            scenes.Insert(0, new EditorBuildSettingsScene(GameScene, true));
            EditorBuildSettings.scenes = scenes.ToArray();
            IncludeShaders();
            Debug.Log("Drift City: created " + GameScene);
        }

        static void IncludeShaders()
        {
            var gs = AssetDatabase.LoadAssetAtPath<GraphicsSettings>("ProjectSettings/GraphicsSettings.asset");
            var so = new SerializedObject(gs);
            var list = so.FindProperty("m_AlwaysIncludedShaders");
            foreach (var name in new[] { "DriftCity/NiStandard", "DriftCity/VCAlphaTextureBlender", "DriftCity/AGCar", "DriftCity/AGTree", "DriftCity/Toon", "DriftCity/SpecularBand", "DriftCity/Sky", "DriftCity/Flare", "Legacy Shaders/VertexLit", "Hidden/DriftCity/ArcadePost", "DriftCity/ToonSky", "Sprites/Default", "Legacy Shaders/Transparent/Cutout/VertexLit", "Legacy Shaders/Diffuse", "Legacy Shaders/Particles/Alpha Blended", "Legacy Shaders/Particles/Additive" })
            {
                var shader = Shader.Find(name);
                if (!shader) continue;
                bool present = false;
                for (int i = 0; i < list.arraySize; i++) if (list.GetArrayElementAtIndex(i).objectReferenceValue == shader) present = true;
                if (present) continue;
                list.InsertArrayElementAtIndex(list.arraySize);
                list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = shader;
            }
            so.ApplyModifiedProperties();
        }

        /// <summary>Mirrors src into dst, copying only files that are new or changed in size/date.</summary>
        static int SyncFolder(string src, string dst)
        {
            int copied = 0;
            foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(dst, file.Substring(src.Length + 1));
                var si = new FileInfo(file);
                var ti = new FileInfo(target);
                if (ti.Exists && ti.Length == si.Length && ti.LastWriteTimeUtc >= si.LastWriteTimeUtc) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, true);
                copied++;
            }
            return copied;
        }

        [MenuItem("Drift City/Build Windows Player")]
        public static void BuildWindows()
        {
            CreateGameScene();
            PlayerSettings.productName = "Drift City Offline";
            PlayerSettings.companyName = "DriftCityOffline";
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.defaultScreenWidth = 1280;
            PlayerSettings.defaultScreenHeight = 960;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.runInBackground = true;
            Directory.CreateDirectory(BuildDir);
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { GameScene },
                locationPathName = Path.Combine(BuildDir, "DriftCityOffline.exe"),
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            });
            var s = report.summary;
            Debug.Log($"Drift City build: {s.result}, {s.totalErrors} errors, {s.totalSize / (1024 * 1024)} MB, {s.totalTime.TotalSeconds:F0}s -> {s.outputPath}");
            if (s.result == BuildResult.Succeeded)
            {
                var imported = DriftCityImport.ProjectGameData;
                var gameroot = Path.Combine(BuildDir, "gameroot.txt");
                if (File.Exists(Path.Combine(imported, Core.GameFileSystem.ImportMarker)))
                {
                    // standalone: the imported data ships inside the player, no installation needed
                    var dest = Path.Combine(BuildDir, "DriftCityOffline_Data", "StreamingAssets", "DriftCityData");
                    int copied = SyncFolder(imported, dest);
                    if (File.Exists(gameroot)) File.Delete(gameroot);
                    Debug.Log($"Drift City build: game data bundled ({copied} files updated) -> {dest}");
                }
                else
                {
                    var root = Object.FindAnyObjectByType<GameFlow>()?.GameRoot;
                    if (!string.IsNullOrEmpty(root)) File.WriteAllText(gameroot, root);
                }
            }
            else if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }
}
