using System;
using DriftCity.Core;
using DriftCity.Core.Nif;
using UnityEngine;

namespace DriftCity
{
    /// <summary>
    /// Shared access to the installed game's data. The original files are read at runtime
    /// from the user's own install; nothing is copied into the Unity project.
    /// </summary>
    public static class DriftCityData
    {
        public static GameFileSystem Files { get; private set; }

        public static GameFileSystem Mount(string gameRoot)
        {
            if (Files != null && string.Equals(Files.Root, gameRoot, StringComparison.OrdinalIgnoreCase)) return Files;
            Files?.Dispose();
            Files = new GameFileSystem(gameRoot) { Log = Debug.Log };
            return Files;
        }

        /// <summary>
        /// Finds the installed client: "-gameRoot &lt;path&gt;" on the command line, the DRIFTCITY_ROOT environment variable,
        /// an imported copy, a gameroot.txt next to the executable (or project), then <paramref name="fallback"/>.
        /// Returns null if none contains a Data folder.
        /// </summary>
        public static string ResolveGameRoot(string fallback)
        {
            var candidates = new System.Collections.Generic.List<string>();
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], "-gameRoot", StringComparison.OrdinalIgnoreCase)) candidates.Add(args[i + 1]);
            var env = Environment.GetEnvironmentVariable("DRIFTCITY_ROOT");
            if (!string.IsNullOrEmpty(env)) candidates.Add(env.Trim().Trim('"'));
            // an imported copy (Drift City > Import Game Data) needs no installed client
            foreach (var imported in ImportedRoots())
                if (System.IO.File.Exists(System.IO.Path.Combine(imported, Core.GameFileSystem.ImportMarker))) candidates.Add(imported);
            try
            {
                var exeDir = System.IO.Path.GetDirectoryName(Application.dataPath);
                var file = System.IO.Path.Combine(exeDir ?? ".", "gameroot.txt");
                if (System.IO.File.Exists(file)) candidates.Add(System.IO.File.ReadAllText(file).Trim().Trim('"'));
            }
            catch { }
            candidates.Add(fallback);
            foreach (var c in candidates)
                if (!string.IsNullOrEmpty(c) && System.IO.Directory.Exists(System.IO.Path.Combine(c, "Data"))) return c;
            return null;
        }

        /// <summary>Where an imported copy lives: StreamingAssets in a build, the project's GameData folder in the editor.</summary>
        public static System.Collections.Generic.IEnumerable<string> ImportedRoots()
        {
            yield return System.IO.Path.Combine(Application.streamingAssetsPath, "DriftCityData");
            yield return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath) ?? ".", "GameData");
        }

        public static void Unmount()
        {
            Files?.Dispose();
            Files = null;
        }

        // Gamebryo is right-handed Z-up (X right, Y forward); Unity is left-handed Y-up (X right, Z forward).
        // Swapping Y and Z converts between them (it is a mirror, which also flips triangle winding).
        public static Vector3 ToUnity(Vec3 v) => new Vector3(v.X, v.Z, v.Y);

        /// <summary>Flip if rotated objects come out mirrored (file matrix convention check).</summary>
        public static bool TransposeRotations = false;

        public static Quaternion ToUnity(Mat3 m)
        {
            if (TransposeRotations)
                m = new Mat3 { M00 = m.M00, M01 = m.M10, M02 = m.M20, M10 = m.M01, M11 = m.M11, M12 = m.M21, M20 = m.M02, M21 = m.M12, M22 = m.M22 };
            // conjugate by the Y/Z swap: U[r][c] = M[p(r)][p(c)], p = {0, 2, 1}
            var forward = new Vector3(m.M01, m.M21, m.M11); // image of Unity Z (= Gamebryo Y column)
            var up = new Vector3(m.M02, m.M22, m.M12);      // image of Unity Y (= Gamebryo Z column)
            if (forward.sqrMagnitude < 1e-8f || up.sqrMagnitude < 1e-8f) return Quaternion.identity;
            return Quaternion.LookRotation(forward, up);
        }

        public static Color ToUnity(Color4 c) => new Color(c.R, c.G, c.B, c.A);
    }
}
