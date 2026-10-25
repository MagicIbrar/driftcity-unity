// Mounts every NayaPack archive of an installed Drift City client and resolves
// file names the way the game does: loose files and later patches win.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DriftCity.Core
{
    public sealed class GameFileSystem : IDisposable
    {
        readonly List<NayaPack> packs = new List<NayaPack>();
        readonly Dictionary<string, (NayaPack pack, NayaPack.Entry entry)> index =
            new Dictionary<string, (NayaPack, NayaPack.Entry)>(StringComparer.OrdinalIgnoreCase);
        // file name only -> full paths, for texture lookups that only store a bare name
        readonly Dictionary<string, List<string>> byFileName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // files extracted into the Data folder (an imported copy of the game has only these)
        readonly Dictionary<string, string> loose = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string Root { get; }
        /// <summary>True when this is an imported, extracted copy (no archives).</summary>
        public bool Imported { get; }
        public IReadOnlyCollection<string> AllFiles => Imported ? (IReadOnlyCollection<string>)loose.Keys : index.Keys;

        /// <summary>Marker written by the importer at the root of an extracted copy.</summary>
        public const string ImportMarker = "drift_city_import.txt";
        public Action<string> Log = _ => { };

        public GameFileSystem(string gameRoot)
        {
            Root = gameRoot;
            var data = System.IO.Path.Combine(gameRoot, "Data");
            if (!Directory.Exists(data))
                throw new DirectoryNotFoundException($"No Data folder under {gameRoot}");

            var archives = new List<string>();
            archives.AddRange(Directory.GetFiles(data, "*.agt").OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
            foreach (var sub in new[] { "World", "GameUI" })
            {
                var dir = System.IO.Path.Combine(data, sub);
                if (Directory.Exists(dir))
                    archives.AddRange(Directory.GetFiles(dir, "*.agt").OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
            }
            var patchDir = System.IO.Path.Combine(gameRoot, "Patch");
            if (Directory.Exists(patchDir))
            {
                archives.AddRange(Directory.GetFiles(patchDir, "Patch.*").OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
                archives.AddRange(Directory.GetFiles(patchDir, "PatchMod.*").OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
            }

            foreach (var path in archives)
            {
                NayaPack pack;
                try { pack = new NayaPack(path); }
                catch (Exception ex) { Log($"skip {path}: {ex.Message}"); continue; }
                packs.Add(pack);
                foreach (var e in pack.Entries.Values)
                {
                    index[e.Name] = (pack, e);
                    var file = System.IO.Path.GetFileName(e.Name);
                    if (!byFileName.TryGetValue(file, out var list)) byFileName[file] = list = new List<string>();
                    if (!list.Contains(e.Name)) list.Add(e.Name);
                }
            }
            Imported = packs.Count == 0 && File.Exists(System.IO.Path.Combine(gameRoot, ImportMarker));
            if (Imported)
            {
                // every archive entry was extracted to Data\<path>; index them the same way
                foreach (var full in Directory.EnumerateFiles(data, "*", SearchOption.AllDirectories))
                {
                    var rel = NayaPack.NormalizePath(full.Substring(data.Length + 1));
                    loose[rel] = full;
                    var file = System.IO.Path.GetFileName(rel);
                    if (!byFileName.TryGetValue(file, out var list)) byFileName[file] = list = new List<string>();
                    list.Add(rel);
                }
                Log($"mounted imported game data: {loose.Count} files under {data}");
            }
            else Log($"mounted {packs.Count} archives, {index.Count} files");
        }

        public bool Exists(string name)
        {
            var key = NayaPack.NormalizePath(name);
            return Imported ? loose.ContainsKey(key) : index.ContainsKey(key) || File.Exists(LoosePath(name));
        }

        string LoosePath(string name) => System.IO.Path.Combine(Root, "Data", name.Replace('\\', System.IO.Path.DirectorySeparatorChar));

        public byte[] Read(string name)
        {
            var key = NayaPack.NormalizePath(name);
            if (Imported) return loose.TryGetValue(key, out var path) ? File.ReadAllBytes(path) : throw new FileNotFoundException(name);
            var loosePath = LoosePath(key);
            if (File.Exists(loosePath)) return File.ReadAllBytes(loosePath);
            if (index.TryGetValue(key, out var hit)) return hit.pack.Read(hit.entry);
            throw new FileNotFoundException(name);
        }

        public bool TryRead(string name, out byte[] data)
        {
            try { data = Read(name); return true; }
            catch (FileNotFoundException) { data = null; return false; }
        }

        /// <summary>Find a file by bare name, preferring candidates under <paramref name="nearDirectory"/>.</summary>
        public string Resolve(string fileName, string nearDirectory = null)
        {
            var norm = NayaPack.NormalizePath(fileName);
            if (index.ContainsKey(norm) || loose.ContainsKey(norm)) return norm;
            var bare = System.IO.Path.GetFileName(norm);
            if (!byFileName.TryGetValue(bare, out var list) || list.Count == 0) return null;
            if (list.Count == 1 || string.IsNullOrEmpty(nearDirectory)) return list[0];
            var near = NayaPack.NormalizePath(nearDirectory);
            // longest shared directory prefix wins
            return list.OrderByDescending(p => SharedPrefix(p, near)).First();
        }

        static int SharedPrefix(string a, string b)
        {
            int n = Math.Min(a.Length, b.Length), i = 0;
            while (i < n && a[i] == b[i]) i++;
            return i;
        }

        public IEnumerable<string> List(string prefix) =>
            AllFiles.Where(k => k.StartsWith(NayaPack.NormalizePath(prefix), StringComparison.OrdinalIgnoreCase));

        /// <summary>Entry names in archive order with later archives (patches) winning, for the importer.</summary>
        public IEnumerable<string> ArchiveEntries => index.Keys;

        public void Dispose()
        {
            foreach (var p in packs) p.Dispose();
            packs.Clear();
        }
    }
}
