using System;
using System.Collections;
using System.Collections.Generic;
using DriftCity.Core;
using DriftCity.Core.Nif;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// Builds an open city (terrain grid, building blocks, placed props) from Data\World\&lt;city&gt;.agt.
    /// Runs as a coroutine so a loading screen can show progress.
    /// </summary>
    public sealed class CityLoader
    {
        public string City;
        public bool Colliders = true;
        public bool Props = true;
        public float FrameBudgetSeconds = 0.05f;
        /// <summary>main\shadow.dat: the baked shadow decals laid on the ground around the buildings.</summary>
        public bool GroundShadows = true;
        /// <summary>The client's VisibleRange window over the terrain grid (<see cref="CityStreaming"/>).</summary>
        public bool Streaming = true;
        public GameObject Root { get; private set; }
        public Bounds WorldBounds { get; private set; }
        public CityStreaming Window { get; private set; }
        public int TerrainCells, Blocks, PropInstances, GroundShadowPieces;

        public CityLoader(string city) { City = city; }

        /// <summary>
        /// &lt;dir&gt;\shadow.dat (0x40c410 -> 0x40c130): u32 stamp; when the stamp is above 0x20060620 two more u32 (0 and 208 in
        /// every file); u32 count; then one Gamebryo 20.0.0.4 stream whose first count top-level objects are NiNodes named
        /// "%d %d" (the second number is the block index; a negative one is skipped, an unknown one is placed by the node's
        /// bound centre via 0x40d1e0). Each holds one NiTriShape with its own embedded texture (NiPixelData), an alpha
        /// property and a z-buffer property: the shadows of the buildings, baked into textures and laid on the ground.
        /// The client attaches each to its block's node (so they stream with the block); the remake keeps them in one
        /// group. Returns the NIF offset or -1.
        /// </summary>
        public static int ShadowDatNifOffset(byte[] d, out uint stamp, out int count)
        {
            stamp = 0; count = 0;
            if (d == null || d.Length < 16) return -1;
            stamp = BitConverter.ToUInt32(d, 0);
            int p = 4;
            if (stamp > 0x20060620) p += 8;
            count = BitConverter.ToInt32(d, p); p += 4;
            return CityFormats.IsNif(d, p) ? p : -1;
        }

        /// <summary>Loads &lt;city&gt;\main\shadow.dat under <paramref name="parent"/> as "Ground Shadows" (null when absent).</summary>
        public static GameObject LoadGroundShadows(GameFileSystem fs, string city, Transform parent)
        {
            string path = $@"{city}\main\shadow.dat";
            if (fs == null || !fs.Exists(path)) return null;
            var d = fs.Read(path);
            int off = ShadowDatNifOffset(d, out _, out int count);
            if (off < 0) { Debug.LogWarning($"Drift City: {path} has no NIF"); return null; }
            var nif = new byte[d.Length - off];
            Buffer.BlockCopy(d, off, nif, 0, nif.Length);
            GameObject go;
            try { go = new NifSceneBuilder { AddColliders = false, LogErrors = false }.LoadBytes(nif, $@"{city}\main\shadow.nif", parent); }
            catch (Exception ex) { Debug.LogWarning($"Drift City: {path}: {ex.Message}"); return null; }
            go.name = "Ground Shadows";
            // decals on the ground: a hair above it (as the road markings) and never casting or taking shadows
            go.transform.localPosition += new Vector3(0, 0.03f, 0);
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                // the pieces carry no NiMaterialProperty: Gamebryo then lights them with its default material (ambient and
                // diffuse 0.5), so the grey (about 145) silhouettes at full alpha come out darker than the ground under them
                // (inferred: the builder's own default is white). Each piece has its own texture, so its material is its own.
                var m = r.sharedMaterial;
                if (m) { m.SetColor("_Color", new Color(0.5f, 0.5f, 0.5f, 1f)); m.SetColor("_AmbientColor", new Color(0.5f, 0.5f, 0.5f, 1f)); }
            }
            Debug.Log($"Drift City: {path}: {count} shadow pieces");
            return go;
        }

        public IEnumerator Load(Transform parent, Action<float, string> progress)
        {
            var fs = DriftCityData.Files;
            Root = new GameObject("City " + City);
            if (parent) Root.transform.SetParent(parent, false);
            var t0 = Time.realtimeSinceStartup;
            float frameStart = Time.realtimeSinceStartup;
            bool Yield() { if (Time.realtimeSinceStartup - frameStart < FrameBudgetSeconds) return false; frameStart = Time.realtimeSinceStartup; return true; }

            // ---------- terrain ----------
            progress?.Invoke(0.02f, "terrain");
            yield return null;
            var terrain = CityTerrain.Parse(fs.Read($@"{City}\terrain0.lf"));
            var terrainRoot = new GameObject("Terrain").transform;
            terrainRoot.SetParent(Root.transform, false);
            var builder = new NifSceneBuilder { AddColliders = Colliders, LogErrors = false };
            for (int i = 0; i < terrain.Cells.Count; i++)
            {
                var cell = terrain.Cells[i];
                var go = builder.LoadBytes(CityFormats.Slice(terrain.Data, cell.nif), $@"{City}\terrain\cell_{cell.index}.nif", terrainRoot);
                // cell NIFs are already in world coordinates (the grid only indexes them)
                TerrainCells++;
                if (Yield()) { progress?.Invoke(0.02f + 0.33f * i / terrain.Cells.Count, "terrain"); yield return null; }
            }
            terrain.Data = null;

            // ---------- building blocks ----------
            progress?.Invoke(0.35f, "buildings");
            yield return null;
            var lbf = fs.Read($@"{City}\blockobj0.lbf");
            var blocks = CityFormats.BlockNifs(lbf);
            var blockRoot = new GameObject("Blocks").transform;
            blockRoot.SetParent(Root.transform, false);
            for (int i = 0; i < blocks.Count; i++)
            {
                var data = CityFormats.Slice(lbf, blocks[i]);
                var go = builder.LoadBytes(data, $@"{City}\obj\block_{i}.nif", blockRoot);
                // blocks ("Block <cellIndex>") are also authored in world coordinates
                Blocks++;
                if (Yield()) { progress?.Invoke(0.35f + 0.35f * i / blocks.Count, "buildings"); yield return null; }
            }
            lbf = null;

            // ---------- baked ground shadows (main\shadow.dat; DriftCity_RE/TRAFFIC.md section 12) ----------
            if (GroundShadows)
            {
                var shadows = LoadGroundShadows(fs, City, Root.transform);
                if (shadows) GroundShadowPieces = shadows.transform.childCount;
                yield return null;
            }

            // ---------- road markings and guard rails (the client's lane0.llf / guardrail0.lgf cell streams) ----------
            foreach (var (file, name, colliders) in new[] { ("lane0.llf", "Road Markings", false), ("guardrail0.lgf", "Guard Rails", Colliders) })
            {
                string path = $@"{City}\{file}";
                if (!fs.Exists(path)) continue;
                var data = fs.Read(path);
                var root = new GameObject(name).transform;
                root.SetParent(Root.transform, false);
                // the markings are decals laid on the road surface: a hair above it so they don't flicker
                if (!colliders) root.localPosition = new Vector3(0, 0.02f, 0);
                var streamBuilder = new NifSceneBuilder { AddColliders = colliders, LogErrors = false };
                var cells = CityFormats.CellStream(data);
                for (int i = 0; i < cells.Count; i++)
                {
                    // cell NIFs are in world coordinates; textures (decal_line/decal_marker) sit in <city>\main
                    var go = streamBuilder.LoadBytes(CityFormats.Slice(data, cells[i]), $@"{City}\main\{System.IO.Path.GetFileNameWithoutExtension(file)}_{cells[i].Name}.nif", root);
                    foreach (var r in go.GetComponentsInChildren<Renderer>()) r.shadowCastingMode = colliders ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
                    if (Yield()) { progress?.Invoke(0.7f, name.ToLowerInvariant()); yield return null; }
                }
            }

            // ---------- invisible walls (main\hitwall.nif: untextured NRW_* barriers the client collides with) ----------
            if (Colliders && fs.Exists($@"{City}\main\hitwall.nif"))
            {
                var walls = new NifSceneBuilder { AddColliders = true, LogErrors = false }.Load($@"{City}\main\hitwall.nif", Root.transform);
                walls.name = "Invisible Walls";
                foreach (var r in walls.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
            }

            // ---------- props ----------
            if (Props && fs.Exists($@"{City}\modeltable0.lof") && fs.Exists($@"{City}\main\object0.loi"))
            {
                progress?.Invoke(0.7f, "objects");
                yield return null;
                var lof = fs.Read($@"{City}\modeltable0.lof");
                string listPath = fs.Exists($@"{City}\modeltable.nxt") ? $@"{City}\modeltable.nxt" : $@"{City}\{City}.xlt";
                var models = fs.Exists(listPath) ? CityFormats.ModelsById(lof, fs.Read(listPath)) : new Dictionary<int, EmbeddedNif>();
                var instances = CityFormats.Instances(fs.Read($@"{City}\main\object0.loi"), terrain.Cells.Count == 0 ? int.MaxValue : terrain.Cols * terrain.Rows);
                var templates = new Dictionary<int, GameObject>();
                var templateRoot = new GameObject("Prop Templates");
                templateRoot.SetActive(false);
                templateRoot.transform.SetParent(Root.transform, false);
                var propRoot = new GameObject("Props").transform;
                propRoot.SetParent(Root.transform, false);
                var propBuilder = new NifSceneBuilder { AddColliders = false, LogErrors = false };
                // palm trees and hydrants break when hit (modeltable.nxt EffectID 11/12/13 and 2)
                var breakable = Colliders ? GameEffects.PropEffectIds(City) : new Dictionary<int, int>();
                for (int i = 0; i < instances.Count; i++)
                {
                    var inst = instances[i];
                    if (!templates.TryGetValue(inst.Model, out var tpl))
                    {
                        tpl = null;
                        if (models.TryGetValue(inst.Model, out var m))
                        {
                            try { tpl = propBuilder.LoadBytes(CityFormats.Slice(lof, m), $@"{City}\obj\{m.Name}", templateRoot.transform); }
                            catch (Exception ex) { Debug.LogWarning($"{City} model {m.Name}: {ex.Message}"); }
                        }
                        templates[inst.Model] = tpl;
                    }
                    if (!tpl) continue;
                    var go = UnityEngine.Object.Instantiate(tpl, propRoot);
                    go.transform.localPosition = DriftCityData.ToUnity(inst.Translation);
                    go.transform.localRotation = DriftCityData.ToUnity(inst.Rotation);
                    go.transform.localScale = Vector3.one * (inst.Scale > 0 ? inst.Scale : 1f);
                    if (breakable.TryGetValue(inst.Model, out int fxId)) GameEffects.MakeBreakable(go, fxId);
                    PropInstances++;
                    if (Yield()) { progress?.Invoke(0.7f + 0.28f * i / instances.Count, "objects"); yield return null; }
                }
            }

            var renderers = Root.GetComponentsInChildren<Renderer>();
            if (renderers.Length > 0)
            {
                var b = renderers[0].bounds;
                foreach (var r in renderers) if (r.gameObject.activeInHierarchy) b.Encapsulate(r.bounds);
                WorldBounds = b;
            }

            // ---------- the client's distance window (VisibleRange cells of the terrain grid around the camera) ----------
            if (Streaming)
            {
                var tr = terrainRoot.GetComponentsInChildren<Renderer>();
                Vector2 origin = new Vector2(WorldBounds.min.x, WorldBounds.min.z);
                if (tr.Length > 0)
                {
                    float minX = float.MaxValue, minZ = float.MaxValue;
                    foreach (var r in tr) { minX = Mathf.Min(minX, r.bounds.min.x); minZ = Mathf.Min(minZ, r.bounds.min.z); }
                    origin = new Vector2(minX, minZ);
                }
                Window = CityStreaming.Attach(Root, terrain.CellSize, origin);
            }
            progress?.Invoke(1f, "done");
            Debug.Log($"Drift City: {City} loaded in {Time.realtimeSinceStartup - t0:F1}s: {TerrainCells} terrain cells, {Blocks} blocks, {PropInstances} props");
        }
    }
}
