using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DriftCity.Game;
using DriftCity.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace DriftCity.Tests
{
    /// <summary>
    /// Goes through every car, part, decal, licence and recipe imported from the installed client and checks
    /// each one builds or resolves. Writes Logs/content_audit.txt and a contact sheet Preview/audit_cars.png.
    /// </summary>
    public class ContentAuditTests
    {
        [UnityTest]
        public IEnumerator EveryCarPartDecalLicenceAndRecipe()
        {
            var root = DriftCityData.ResolveGameRoot(@"D:\Drift_City_Remastered_Alpha_08-2025\Drift City Remastered Alpha");
            Assert.IsNotNull(root, "installed client found");
            DriftCityData.Mount(root);
            Debug.Log($"Drift City audit: data from {root} (imported copy: {DriftCityData.Files.Imported})");
            if (File.Exists(Path.Combine(Path.GetDirectoryName(Application.dataPath), "GameData", Core.GameFileSystem.ImportMarker)))
                Assert.IsTrue(DriftCityData.Files.Imported, "the imported copy in GameData is used instead of the installation");
            UiAssets.EnsureCp949();
            UiManager.Ensure();
            GameTables.Load();
            var report = new StringBuilder();
            var problems = new List<string>();
            var at = new Vector3(0, -4000, 0);

            // ---- cars ----
            int cars = 0, synthWheels = 0, noPaint = 0, noBoost = 0, noGrades = 0;
            foreach (var v in GameTables.PlayerCars)
            {
                cars++;
                var go = CarFactory.BuildVisual(v, null, at, Quaternion.identity, out var wheels, out var tires);
                int bodyRenderers = go.transform.Find("Body").GetComponentsInChildren<Renderer>().Length;
                bool realWheels = wheels.All(w => w && !w.name.StartsWith("wheel "));
                bool paintable = go.GetComponentsInChildren<Renderer>().Any(r => r.sharedMaterial && r.sharedMaterial.GetTexture("_MainTex") && r.sharedMaterial.GetTexture("_MainTex").name.ToLowerInvariant().Contains("car_color"));
                bool boost = CarFactory.FindNode(go, "boost01") || CarFactory.FindNode(go, "boost02");
                int grades = ShopData.Grades(v).Count;
                if (bodyRenderers == 0) problems.Add($"car {v.UiName} ({v.ModelBase}): body has no meshes");
                if (tires.Count(t => t) != 4) problems.Add($"car {v.UiName}: {tires.Count(t => t)} tires");
                if (!realWheels) synthWheels++;
                if (!paintable) noPaint++;
                if (!boost) noBoost++;
                if (grades == 0) noGrades++;
                report.AppendLine($"car {v.Index,3} {v.UiName,-14} {v.ModelBase,-10} class V{v.Grade} lv{v.ReqLevel,-3} price {ShopData.CarPrice(v),9:N0} grades {grades} body {bodyRenderers} wheels {(realWheels ? "file" : "GUESSED")} paint {(paintable ? "yes" : "NO")} boost {(boost ? "yes" : "no")} tire r {v.TireRadius:F2}");
                Object.Destroy(go);
                if (cars % 10 == 0) yield return null;
            }
            report.AppendLine($"CARS {cars}: guessed wheels {synthWheels}, no paint layer {noPaint}, no exhaust nodes {noBoost}, no grades {noGrades}");

            // ---- every part model loads once ----
            var parts = PartsCatalog.All;
            int partModels = 0, badModels = 0;
            foreach (var group in parts.Where(p => p.Nif != null).GroupBy(p => p.Nif))
            {
                partModels++;
                var holder = new GameObject("audit part");
                var model = new NifSceneBuilder().Load(group.Key, holder.transform);
                bool hasMesh = model && model.GetComponentsInChildren<Renderer>(true).Length > 0;
                // a couple of original "counterpart" hoods are deliberately empty (the hood is part of that body)
                if (!hasMesh) { badModels++; report.AppendLine($"  note: empty model {group.Key} ({group.First().Name})"); }
                Object.Destroy(holder);
                if (partModels % 25 == 0) yield return null;
            }
            var bySlot = parts.GroupBy(p => p.Slot).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}");
            report.AppendLine($"PARTS {parts.Count} ({string.Join(", ", bySlot)}); models {partModels}, without meshes {badModels}");

            // ---- mount points for universal parts, per car ----
            int mountMissing = 0;
            foreach (var v in GameTables.PlayerCars)
            {
                var go = CarFactory.BuildVisual(v, null, at, Quaternion.identity, out _, out _);
                foreach (var node in new[] { "num_front", "num_back", "spoiler", "fc", "light01" })
                    if (!CarFactory.FindNode(go, node)) { mountMissing++; report.AppendLine($"  {v.UiName}: no '{node}' node"); }
                Object.Destroy(go);
            }
            report.AppendLine($"MOUNT POINTS missing: {mountMissing} (of {GameTables.PlayerCars.Count * 5})");

            // ---- decals: textures present, composite renders ----
            int decalCars = 0, decalItems = parts.Count(p => p.Slot == PartSlot.Decal), missingTex = 0;
            var checkedTex = new HashSet<string>();
            foreach (var v in GameTables.PlayerCars)
            {
                bool any = false;
                foreach (var d in parts.Where(p => p.Slot == PartSlot.Decal))
                {
                    var layers = CarPaint.Layers(v, d.Variant);
                    if (layers == null) continue;
                    any = true;
                    foreach (var l in layers)
                        if (checkedTex.Add(l.Texture) && !UiAssets.Texture(l.Texture)) { missingTex++; problems.Add($"decal texture missing: {l.Texture}"); }
                }
                if (any) decalCars++;
            }
            report.AppendLine($"DECALS {decalItems} items; cars with decal layouts {decalCars}/{GameTables.PlayerCars.Count}; textures {checkedTex.Count}, missing {missingTex}");

            // ---- a fully dressed car: one of every slot that fits ----
            var showCar = GameTables.PlayerCars.Where(c => !c.ComboBody && PartsCatalog.For(c, PartSlot.Decal).Any())
                .OrderByDescending(c => System.Enum.GetValues(typeof(PartSlot)).Cast<PartSlot>().Count(s => PartsCatalog.For(c, s).Any())).First();
            var owned = new OwnedCar { VehicleIndex = showCar.Index, Color = 2, Parts = null };
            foreach (PartSlot slot in System.Enum.GetValues(typeof(PartSlot)))
            {
                var pick = PartsCatalog.For(showCar, slot).Where(p => !p.Stock).Skip(slot == PartSlot.Decal ? 2 : 0).FirstOrDefault() ?? PartsCatalog.For(showCar, slot).FirstOrDefault();
                if (pick != null) owned.Fit(slot, pick.Id);
            }
            var cam = new GameObject("audit cam").AddComponent<Camera>();
            cam.transform.position = at + new Vector3(4.2f, 2.2f, -4.6f);
            cam.transform.LookAt(at + Vector3.up * 0.7f);
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.35f, 0.38f, 0.42f);
            var sun = new GameObject("audit sun").AddComponent<Light>(); sun.type = LightType.Directional; sun.transform.rotation = Quaternion.Euler(50, -30, 0);
            var dressed = CarFactory.BuildDressed(showCar, owned, at, Quaternion.Euler(0, 200, 0), out _);
            var fitted = new List<string>();
            foreach (PartSlot slot in System.Enum.GetValues(typeof(PartSlot)))
            {
                var p = PartsCatalog.Effective(showCar, owned, slot);
                if (p != null) fitted.Add($"{slot}={p.Name}");
            }
            report.AppendLine($"DRESSED {showCar.UiName}: {string.Join(", ", fitted)}");
            foreach (var (slot, node) in new[] { (PartSlot.Wing, "Wing"), (PartSlot.Duct, "Duct"), (PartSlot.Roof, "Roof"), (PartSlot.Plate, "Plate num_front"), (PartSlot.Plate, "Plate num_back"), (PartSlot.Neon, "Neon") })
                if (PartsCatalog.Effective(showCar, owned, slot) != null && !dressed.GetComponentsInChildren<Transform>(true).Any(t => t.name == node)) problems.Add($"dressed car is missing {node}");
            bool decalTex = dressed.GetComponentsInChildren<Renderer>().Any(r => r.sharedMaterial && r.sharedMaterial.GetTexture("_MainTex") is RenderTexture);
            if (!decalTex) problems.Add("dressed car has no composited decal paint");
            foreach (var r in dressed.GetComponentsInChildren<MeshRenderer>())
            {
                if (!(r.sharedMaterial && r.sharedMaterial.GetTexture("_MainTex") is RenderTexture rtex)) continue;
                var mesh = r.GetComponent<MeshFilter>().sharedMesh;
                var uvs = mesh.uv;
                if (uvs.Length == 0) continue;
                report.AppendLine($"  decal body {r.name}: uv x {uvs.Min(u => u.x):F2}..{uvs.Max(u => u.x):F2}  y {uvs.Min(u => u.y):F2}..{uvs.Max(u => u.y):F2}  scale {r.sharedMaterial.GetTextureScale("_MainTex")}");
                RenderTexture.active = rtex;
                var dump = new Texture2D(rtex.width, rtex.height, TextureFormat.RGB24, false);
                dump.ReadPixels(new Rect(0, 0, rtex.width, rtex.height), 0, 0); dump.Apply();
                RenderTexture.active = null;
                File.WriteAllBytes("Preview/audit_decal_texture.png", dump.EncodeToPNG());
                // the body's UV layout drawn over the composite, to line the two up
                var lines = new Texture2D(512, 512, TextureFormat.RGB24, false);
                var tris = mesh.triangles;
                for (int t = 0; t < tris.Length; t += 3)
                    for (int e = 0; e < 3; e++)
                    {
                        var u0 = uvs[tris[t + e]]; var u1 = uvs[tris[t + (e + 1) % 3]];
                        for (int k = 0; k <= 20; k++) { var q = Vector2.Lerp(u0, u1, k / 20f); lines.SetPixel((int)(Mathf.Repeat(q.x, 1f) * 511), (int)(Mathf.Repeat(q.y, 1f) * 511), Color.white); }
                    }
                lines.Apply();
                File.WriteAllBytes("Preview/audit_uv_layout.png", lines.EncodeToPNG());
                break;
            }
            bool tinted = dressed.GetComponentsInChildren<Renderer>().Any(r => r.sharedMaterial && r.sharedMaterial.name.EndsWith(" tint"));
            if (!tinted) problems.Add("dressed car has no window tint");
            yield return null;
            Directory.CreateDirectory("Preview");
            Shoot(cam, "Preview/audit_dressed_car.png");
            cam.transform.position = at + new Vector3(-4.6f, 2.0f, 4.0f); cam.transform.LookAt(at + Vector3.up * 0.7f);
            Shoot(cam, "Preview/audit_dressed_car_rear.png");
            // wheel close-ups, both sides of the car
            foreach (var (side, file) in new[] { (1f, "Preview/audit_wheel_right.png"), (-1f, "Preview/audit_wheel_left.png") })
            {
                var wheel = dressed.GetComponentsInChildren<Transform>().Where(t => t.parent == dressed.transform && t.name.ToLowerInvariant().Contains("tire")).OrderBy(t => side * dressed.transform.InverseTransformPoint(t.position).x).LastOrDefault()
                            ?? dressed.transform;
                var wp = wheel.position;
                cam.transform.position = wp + dressed.transform.right * side * 1.6f + Vector3.up * 0.3f;
                cam.transform.LookAt(wp);
                Shoot(cam, file);
                report.AppendLine($"  wheel shot {file}: {wheel.name} at {dressed.transform.InverseTransformPoint(wp)}");
                foreach (var r in wheel.GetComponentsInChildren<Renderer>(true))
                    report.AppendLine($"    tire renderer {r.name} uv={UvRange(r)} normals={r.GetComponent<MeshFilter>()?.sharedMesh?.normals.Length} verts={r.GetComponent<MeshFilter>()?.sharedMesh?.vertexCount} parent={r.transform.parent?.name} enabled={r.enabled} active={r.gameObject.activeInHierarchy} mat={r.sharedMaterial?.name} tex={r.sharedMaterial?.GetTexture("_MainTex")?.name} color={r.sharedMaterial?.color} shader={r.sharedMaterial?.shader.name} bounds={r.bounds.size}");
            }
            Object.Destroy(dressed);

            // HD textures: loaded when GameData\Enhanced exists; render the same car without them for comparison
            bool hdAvailable = Directory.Exists(Path.Combine(DriftCityData.Files.Root, "Enhanced"));
            report.AppendLine($"HD TEXTURES available {hdAvailable}, loaded so far {TextureLibrary.EnhancedLoaded}");
            if (hdAvailable && TextureLibrary.UseEnhanced && TextureLibrary.EnhancedLoaded == 0) problems.Add("HD textures exist but none were loaded");
            if (hdAvailable)
            {
                cam.transform.position = at + new Vector3(4.2f, 2.2f, -4.6f); cam.transform.LookAt(at + Vector3.up * 0.7f);
                yield return null;
                bool was = TextureLibrary.UseEnhanced;
                TextureLibrary.UseEnhanced = false; TextureLibrary.Clear();
                var sd = CarFactory.BuildDressed(showCar, owned, at, Quaternion.Euler(0, 200, 0), out _);
                yield return null;
                Shoot(cam, "Preview/audit_dressed_car_sd.png");
                Object.Destroy(sd);
                TextureLibrary.UseEnhanced = was; TextureLibrary.Clear();
                var hd = CarFactory.BuildDressed(showCar, owned, at, Quaternion.Euler(0, 200, 0), out _);
                yield return null;
                Shoot(cam, "Preview/audit_dressed_car_hd.png");
                Object.Destroy(hd);
            }

            // ---- licences and recipes ----
            var lic = Licences.All;
            report.AppendLine($"LICENCES {lic.Count}: obtainable here {lic.Count(l => l.Obtainable)}, with a bonus effect {lic.Count(l => l.Effect != "n/a" && !string.IsNullOrEmpty(l.Effect))}");
            foreach (var l in lic) report.AppendLine($"  {l.Id} {l.Name}: {(l.Obtainable ? string.Join(l.AnyOne ? " or " : " and ", l.Needs.Select(n => $"{n.stat}>={n.value}")) : "n/a here")}   [{l.Effect} {l.Value}]");
            var recipes = Crafting.Recipes;
            int unnamed = recipes.Sum(r => r.Inputs.Count(i => Crafting.Name(i.id) == i.id) + (Crafting.Name(r.Result) == r.Result ? 1 : 0));
            report.AppendLine($"RECIPES {recipes.Count}; item ids without a readable name {unnamed}");
            foreach (var r in recipes) report.AppendLine($"  {r.Name}: {string.Join(" + ", r.Inputs.Select(i => $"{Crafting.Name(i.id)}x{i.count}"))} + {Crafting.Name(r.Kit)} -> {Crafting.Name(r.Result)} ({r.Chance}%)");

            report.AppendLine("PROBLEMS " + problems.Count);
            foreach (var p in problems) report.AppendLine("  " + p);
            Directory.CreateDirectory("Logs");
            File.WriteAllText("Logs/content_audit.txt", report.ToString());
            Debug.Log("Drift City audit:\n" + report.ToString().Split('\n').Where(l => l.StartsWith("CARS") || l.StartsWith("PARTS") || l.StartsWith("MOUNT") || l.StartsWith("DECALS") || l.StartsWith("LICENCES") || l.StartsWith("RECIPES") || l.StartsWith("PROBLEMS")).Aggregate((a, b) => a + "\n" + b));

            Assert.AreEqual(121, cars, "all supported cars load");
            Assert.AreEqual(0, problems.Count, "content problems:\n" + string.Join("\n", problems.Take(30)));
        }

        static string UvRange(Renderer r)
        {
            var uv = r.GetComponent<MeshFilter>()?.sharedMesh?.uv;
            if (uv == null || uv.Length == 0) return "none";
            return $"{uv.Min(u => u.x):F2}..{uv.Max(u => u.x):F2} x {uv.Min(u => u.y):F2}..{uv.Max(u => u.y):F2}";
        }

        static void Shoot(Camera cam, string path)
        {
            var rt = new RenderTexture(1024, 768, 24);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(1024, 768, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 1024, 768), 0, 0); tex.Apply();
            RenderTexture.active = null; cam.targetTexture = null;
            File.WriteAllBytes(path, tex.EncodeToPNG());
        }
    }
}
