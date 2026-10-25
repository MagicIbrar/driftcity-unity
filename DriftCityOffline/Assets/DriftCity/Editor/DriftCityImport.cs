using System;
using System.IO;
using System.Linq;
using DriftCity.Core;
using UnityEditor;
using UnityEngine;

namespace DriftCity.EditorTools
{
    /// <summary>
    /// Imports the installed client's data into the project (DriftCityOffline/GameData): every archive entry
    /// is extracted (decrypted, decompressed, patches applied in order) to plain files, and the loose UI,
    /// table and audio files are copied. After that neither the editor nor builds need the installation.
    /// Builds copy GameData into StreamingAssets (see DriftCityBuild).
    /// </summary>
    public static class DriftCityImport
    {
        public static string ProjectGameData => Path.Combine(Path.GetDirectoryName(Application.dataPath), "GameData");

        [MenuItem("Drift City/Import Game Data Into Project")]
        public static void ImportMenu()
        {
            var source = DriftCityData.ResolveGameRoot(@"D:\Drift_City_Remastered_Alpha_08-2025\Drift City Remastered Alpha");
            if (source == null || File.Exists(Path.Combine(source, GameFileSystem.ImportMarker)))
                source = EditorUtility.OpenFolderPanel("Drift City installation (the folder with Data)", "", "");
            if (string.IsNullOrEmpty(source)) return;
            try { Import(source, ProjectGameData, (msg, t) => EditorUtility.DisplayProgressBar("Importing Drift City data", msg, t)); }
            finally { EditorUtility.ClearProgressBar(); }
            EditorUtility.DisplayDialog("Drift City", $"Game data imported into\n{ProjectGameData}\n\nThe editor and new builds now run without the installation.", "OK");
        }

        /// <summary>Batch mode: -executeMethod DriftCity.EditorTools.DriftCityImport.ImportBatch [-importSource path]</summary>
        public static void ImportBatch()
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "-importSource");
            var source = i >= 0 && i + 1 < args.Length ? args[i + 1] : @"D:\Drift_City_Remastered_Alpha_08-2025\Drift City Remastered Alpha";
            Import(source, ProjectGameData, (msg, t) => { if (Mathf.Abs(t * 20 - Mathf.Round(t * 20)) < 0.0005f) Debug.Log($"import {t:P0} {msg}"); });
        }

        public static void Import(string source, string dest, Action<string, float> progress)
        {
            var started = DateTime.Now;
            if (File.Exists(Path.Combine(source, GameFileSystem.ImportMarker))) throw new InvalidOperationException("source is already an imported copy");
            var dataOut = Path.Combine(dest, "Data");
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            Directory.CreateDirectory(dataOut);
            long bytes = 0; int files = 0;

            // 1. every archive entry, winning version only (later archives and patches override earlier ones)
            using (var fs = new GameFileSystem(source) { Log = Debug.Log })
            {
                var entries = fs.ArchiveEntries.ToList();
                for (int k = 0; k < entries.Count; k++)
                {
                    var name = entries[k];
                    var target = Path.Combine(dataOut, name.Replace('\\', Path.DirectorySeparatorChar));
                    if (!IsInside(target, dataOut)) continue;
                    var data = fs.Read(name);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.WriteAllBytes(target, data);
                    bytes += data.Length; files++;
                    progress?.Invoke(name, (float)k / entries.Count * 0.85f);
                }
            }

            // 2. loose files the client reads from disk (game.nui, .ntx sheets, audio, loose tables); they win
            //    over archive copies exactly as in the installed client
            var dataIn = Path.Combine(source, "Data");
            var loose = Directory.EnumerateFiles(dataIn, "*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".agt", StringComparison.OrdinalIgnoreCase) && !f.Contains(Path.DirectorySeparatorChar + "miles" + Path.DirectorySeparatorChar)).ToList();
            for (int k = 0; k < loose.Count; k++)
            {
                var rel = loose[k].Substring(dataIn.Length + 1);
                var target = Path.Combine(dataOut, rel.ToLowerInvariant());
                if (rel.StartsWith("audio", StringComparison.OrdinalIgnoreCase) || rel.StartsWith("GameUI", StringComparison.OrdinalIgnoreCase)) target = Path.Combine(dataOut, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(loose[k], target, true);
                bytes += new FileInfo(loose[k]).Length; files++;
                progress?.Invoke(rel, 0.85f + 0.15f * k / loose.Count);
            }

            File.WriteAllText(Path.Combine(dest, GameFileSystem.ImportMarker),
                $"Drift City data imported {DateTime.Now:yyyy-MM-dd HH:mm}\nsource: {source}\nfiles: {files}\nbytes: {bytes}\n" +
                "This folder holds the original game's assets extracted from the installation for the licensed fan remaster.\n");
            Debug.Log($"Drift City import: {files} files, {bytes / 1048576.0:F0} MB into {dest} in {(DateTime.Now - started).TotalSeconds:F0}s");
        }

        static bool IsInside(string path, string root) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
    }
}
