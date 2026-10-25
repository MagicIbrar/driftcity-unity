using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>One sticker stamp of a decal on a car's paint layout (decal\decal.nxt).</summary>
    public struct DecalLayer
    {
        public string Texture;
        /// <summary>Body region: 0 roof (top), 1 side, 2 side_copy (other side), 3 bumper (front), 4 trunk (rear), 5 all.</summary>
        public int Part;
        public bool FlipX, FlipY;
        public float ScaleX, ScaleY, Rotation, X, Y;   // percent of the layout / degrees / layout units (0..1000)
        public Color Color;
        /// <summary>Drawn in its own colours (a player's custom mark), never darkened on light paint.</summary>
        public bool Exact;
    }

    /// <summary>
    /// The car's paint texture: paint colour plus decal stamps composited into the body's paint layout
    /// (a 1000x1000 top-down unwrap shared by "body0" meshes), and window tint on the glass.
    /// </summary>
    public static class CarPaint
    {
        static Dictionary<(int car, int decal), List<DecalLayer>> decals;
        static Dictionary<int, string> stickerTextures;
        static Material stamp;

        static void Load()
        {
            if (decals != null) return;
            decals = new Dictionary<(int, int), List<DecalLayer>>();
            stickerTextures = new Dictionary<int, string>();
            var fs = DriftCityData.Files;
            if (fs.Exists(@"decal\sticker.nxt"))
                foreach (var line in Text(fs.Read(@"decal\sticker.nxt")).Split('\n'))
                {
                    var p = line.TrimEnd('\r').Split('\t');
                    if (p.Length >= 3 && int.TryParse(p[0], out int id)) stickerTextures[id] = p[2].Trim().ToLowerInvariant();
                }
            if (!fs.Exists(@"decal\decal.nxt")) return;
            int car = -1, decal = -1;
            foreach (var raw in Text(fs.Read(@"decal\decal.nxt")).Split('\n'))
            {
                var p = raw.TrimEnd('\r').Split('\t');
                if (p.Length < 12) continue;
                if (p[0].Length > 0) { int.TryParse(p[0], out car); int.TryParse(p[1], out decal); }
                if (car < 0 || decal < 0 || !int.TryParse(p[6], out int sticker) || !stickerTextures.TryGetValue(sticker, out var tex)) continue;
                var flip = p[7].Split(':');
                var scale = p[8].Split(':');
                var pos = p[10].Split(':');
                var rgb = p[11].Split(':');
                int.TryParse(p[5], out int part);
                var layer = new DecalLayer
                {
                    Texture = tex, Part = Mathf.Clamp(part, 0, 5),
                    FlipX = flip.Length > 0 && flip[0] == "1", FlipY = flip.Length > 1 && flip[1] == "1",
                    ScaleX = F(scale, 0), ScaleY = F(scale, 1), Rotation = F(new[] { p[9] }, 0), X = F(pos, 0), Y = F(pos, 1),
                    Color = rgb.Length >= 3 ? new Color(F(rgb, 0) / 255f, F(rgb, 1) / 255f, F(rgb, 2) / 255f) : Color.white,
                };
                if (!decals.TryGetValue((car, decal), out var list)) decals[(car, decal)] = list = new List<DecalLayer>();
                list.Add(layer);
            }
        }

        static float F(string[] a, int i) => i < a.Length && float.TryParse(a[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;

        static string Text(byte[] d) => d.Length >= 2 && d[0] == 0xFF && d[1] == 0xFE ? System.Text.Encoding.Unicode.GetString(d, 2, d.Length - 2) : Core.Cp949.Decode(d);

        public static bool HasDecal(VehicleInfo car, int index) { Load(); return car != null && decals.ContainsKey((car.UniqueId, index)); }

        public static List<DecalLayer> Layers(VehicleInfo car, int index) { Load(); return car != null && decals.TryGetValue((car.UniqueId, index), out var l) ? l : null; }

        /// <summary>Paint colour of an owned car: a bought paint wins over the creation colour.</summary>
        public static Color PaintColor(OwnedCar owned)
        {
            if (owned == null) return CarFactory.Paints[0];
            var paint = owned.PaintItem >= 0 ? ShopData.Paints.Find(x => x.UniqueId == owned.PaintItem) : null;
            return paint != null ? paint.Color : CarFactory.FactoryColor(GameTables.Car(owned.VehicleIndex), owned.Color);
        }

        /// <summary>
        /// Paint, decal and window tint for a built car (local, remote or a shop preview). <paramref name="markOwner"/>: the
        /// driver whose custom mark (CustomMarks) is stamped on both sides; the player's own car takes the player's mark.
        /// </summary>
        public static void Apply(GameObject car, VehicleInfo v, OwnedCar owned, Color? paintOverride = null, string markOwner = null)
        {
            var color = paintOverride ?? PaintColor(owned);
            var decal = PartsCatalog.Effective(v, owned, PartSlot.Decal);
            var layers = decal != null ? Layers(v, decal.Variant) : null;
            var p = PlayerProfile.Current;
            if (markOwner == null && p != null && owned != null && owned == p.Car && CustomMarks.Own()) markOwner = p.Name;
            if (markOwner != null && CustomMarks.Resolve(CustomMarks.Key(markOwner)))
            {
                layers = layers != null ? new List<DecalLayer>(layers) : new List<DecalLayer>();
                layers.AddRange(MarkLayers(markOwner));
            }
            PaintWith(car, v, color, decal?.Variant ?? 0, layers, false);
            var tint = PartsCatalog.Effective(v, owned, PartSlot.Tint);
            if (tint != null)
                foreach (var r in car.GetComponentsInChildren<Renderer>(true))
                {
                    var m = r.sharedMaterial;
                    if (!m || !m.name.StartsWith("window:")) continue;
                    var copy = m.name.EndsWith(" tint") ? m : OwnedAssets.Track(car, new Material(m) { name = m.name + " tint" });
                    // VehicleWindowColor.nxt gives the glass its alpha (the client sets it as the window's MaterialProperty alpha)
                    var c = tint.Tint;
                    if (c.a >= 0.999f) c.a = Mathf.Max(m.color.a, 0.85f);
                    copy.SetColor("_Color", c);
                    r.sharedMaterial = copy;
                }
        }

        /// <summary>A custom mark on both doors: the middle of each side region, 14 % of its length, own colours (cars whose sides the layout scan misses get it once in the middle).</summary>
        public static List<DecalLayer> MarkLayers(string driver)
        {
            var list = new List<DecalLayer>();
            foreach (int part in new[] { 1, 2 })
                list.Add(new DecalLayer { Texture = CustomMarks.Key(driver), Part = part, ScaleX = 14f, ScaleY = 14f, X = 500f, Y = 520f, Color = Color.white, Exact = true });
            return list;
        }

        /// <summary>
        /// Paints the body panels: plain paint, or the paint with decal stamps composited into the paint layout.
        /// <paramref name="exactDecalColor"/>: the stamps take their own colour as is (a decal-colour id such as
        /// visual.xlt's DecalColor), instead of being darkened on light paint.
        /// </summary>
        static void PaintWith(GameObject car, VehicleInfo v, Color color, int decalVariant, List<DecalLayer> layers, bool exactDecalColor)
        {
            if (layers == null || layers.Count == 0 || v == null) { CarFactory.ApplyPaint(car, color); return; }
            var painted = new List<Renderer>();
            foreach (var r in car.GetComponentsInChildren<Renderer>(true))
            {
                var m = r.sharedMaterial;
                var main = m ? m.GetTexture("_MainTex") : null;
                if (main && (main.name.ToLowerInvariant().Contains("car_color") || main is RenderTexture)) painted.Add(r);
            }
            var tex = OwnedAssets.Track(car, Compose(color, v.UniqueId, decalVariant, layers, Regions(car, painted), exactDecalColor));
            var oldTextures = new HashSet<RenderTexture>();
            foreach (var r in painted)
            {
                var m = r.sharedMaterial;
                if (m.GetTexture("_MainTex") is RenderTexture old && old != tex) oldTextures.Add(old);
                var copy = m.name.EndsWith(" paint") ? m : OwnedAssets.Track(car, new Material(m) { name = m.name + " paint" });
                copy.SetTexture("_MainTex", tex);
                copy.SetTextureScale("_MainTex", Vector2.one);
                copy.SetTextureOffset("_MainTex", Vector2.zero);
                copy.SetColor("_Color", Color.white);
                if (copy.HasProperty("_MaterialColor")) copy.SetColor("_MaterialColor", color);
                r.sharedMaterial = copy;
            }
            // the previous decal texture of this car is no longer used by any of its panels
            foreach (var old in oldTextures) OwnedAssets.Release(car, old);
        }

        /// <summary>
        /// A Real Match look (PVP.md 14.4; effects/realmatch/visual.xlt, read by 0x70de00 into the 16 records of the
        /// character's car table): Color = VehicleColor paint id, Decal = the car's decal layout (decal\decal.nxt, the
        /// same index the custom shop's decals use), DecalColor = VehicleDecalColor id the stamps are drawn in, Neon =
        /// VehicleNeonColor id of the underbody neon. Applied to a built Real Match car (the Duel) or its skin.
        /// </summary>
        public static void ApplyRealMatch(GameObject car, VehicleInfo v, Pvp.RealMatchLook look)
        {
            if (!car) return;
            var paint = VehicleColors.TryGet(VehicleColors.Table.Paint, look.Color, out var pc) ? pc : CarFactory.Paints[Mathf.Abs(look.Color) % CarFactory.Paints.Length];
            var layers = look.Decal > 0 ? Layers(v, look.Decal) : null;
            bool exact = false;
            if (layers != null && VehicleColors.TryGet(VehicleColors.Table.Decal, look.DecalColor, out var dc))
            {
                exact = true;
                layers = layers.Select(l => { l.Color = new Color(dc.r, dc.g, dc.b, 1f); return l; }).ToList();
            }
            PaintWith(car, v, paint, look.Decal, layers, exact);
            if (VehicleColors.TryGet(VehicleColors.Table.Neon, look.Neon, out var nc)) AttachNeon(car, v, nc);
        }

        /// <summary>Underbody neon (vehicle\neon\neon_&lt;model&gt;.nif tinted, plus a small point light), as
        /// CarFactory fits a bought neon; replaces a neon the car already has.</summary>
        public static GameObject AttachNeon(GameObject car, VehicleInfo v, Color color)
        {
            var fs = DriftCityData.Files;
            string nif = v != null ? $@"vehicle\neon\neon_{v.ModelBase}.nif" : null;
            if (!car || nif == null || fs == null || !fs.Exists(nif)) return null;
            var existing = car.transform.Find("Neon");
            if (existing) Object.Destroy(existing.gameObject);
            var n = new NifSceneBuilder().Load(nif, car.transform);
            n.name = "Neon";
            foreach (var r in n.GetComponentsInChildren<Renderer>(true))
            {
                var m = OwnedAssets.Track(car, new Material(r.sharedMaterial));
                m.SetColor("_Color", color);
                // the neon mesh is drawn emissive-only (NiStandard _Unlit ignores the material colour): light it with the
                // neon colour as its emissive instead, so the glow takes the colour
                if (m.HasProperty("_Unlit") && m.GetFloat("_Unlit") > 0.5f)
                {
                    m.SetFloat("_Unlit", 0f);
                    m.SetFloat("_UseVertexColor", 0f);
                    m.SetColor("_EmissionColor", color);
                    m.SetColor("_AmbientColor", Color.black);
                    m.SetColor("_Color", new Color(0, 0, 0, color.a));
                }
                r.sharedMaterial = m;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            var glow = new GameObject("Neon Light").AddComponent<Light>();
            glow.transform.SetParent(n.transform, false);
            glow.transform.localPosition = new Vector3(0, 0.15f, 0);
            glow.type = LightType.Point; glow.color = color; glow.range = 4.5f; glow.intensity = 2.2f;
            return n;
        }

        static readonly Dictionary<int, Rect[]> regionCache = new Dictionary<int, Rect[]>();

        /// <summary>
        /// Where each body region sits in the paint layout: triangles of the painted body meshes are grouped by
        /// the way they face in car space (up = roof, +x / -x = the two sides, +z = front, -z = rear).
        /// </summary>
        static Rect[] Regions(GameObject car, List<Renderer> painted)
        {
            var mf = painted.Select(r => r.GetComponent<MeshFilter>()).Where(f => f && f.sharedMesh).OrderByDescending(f => f.sharedMesh.vertexCount).FirstOrDefault();
            var regions = new Rect[6];
            regions[5] = new Rect(0, 0, 1, 1);
            if (!mf) { for (int i = 0; i < 5; i++) regions[i] = regions[5]; return regions; }
            var mesh = mf.sharedMesh;
            if (regionCache.TryGetValue(mesh.GetInstanceID(), out var cached)) return cached;
            var verts = mesh.vertices; var uvs = mesh.uv; var tris = mesh.triangles;
            var min = new Vector2[5]; var max = new Vector2[5]; var any = new bool[5];
            var toCar = car.transform.worldToLocalMatrix * mf.transform.localToWorldMatrix;
            for (int t = 0; t + 2 < tris.Length && uvs.Length == verts.Length; t += 3)
            {
                Vector3 a = toCar.MultiplyPoint3x4(verts[tris[t]]), b = toCar.MultiplyPoint3x4(verts[tris[t + 1]]), c = toCar.MultiplyPoint3x4(verts[tris[t + 2]]);
                var n = Vector3.Cross(b - a, c - a);
                if (n.sqrMagnitude < 1e-12f) continue;
                n.Normalize();
                float ax = Mathf.Abs(n.x), ay = Mathf.Abs(n.y), az = Mathf.Abs(n.z);
                int region = ay > 0.75f && n.y > 0 ? 0 : ax > 0.75f ? (n.x > 0 ? 1 : 2) : az > 0.75f ? (n.z > 0 ? 3 : 4) : -1;
                if (region < 0) continue;
                for (int k = 0; k < 3; k++)
                {
                    var uv = uvs[tris[t + k]];
                    if (!any[region]) { min[region] = max[region] = uv; any[region] = true; }
                    else { min[region] = Vector2.Min(min[region], uv); max[region] = Vector2.Max(max[region], uv); }
                }
            }
            for (int i = 0; i < 5; i++) regions[i] = any[i] ? Rect.MinMaxRect(min[i].x, min[i].y, max[i].x, max[i].y) : regions[5];
            regionCache[mesh.GetInstanceID()] = regions;
            return regions;
        }

        static RenderTexture Compose(Color paint, int car, int decal, List<DecalLayer> layers, Rect[] regions, bool exactColor = false)
        {
            string key = $"{car}:{decal}:{ColorUtility.ToHtmlStringRGB(paint)}";
            // each car owns its decal texture (released with the car), so nothing piles up in a global cache
            if (!stamp) stamp = new Material(Shader.Find("Sprites/Default"));
            var rt = new RenderTexture(1024, 1024, 0, RenderTextureFormat.ARGB32) { name = "car_color decal " + key, useMipMap = true, autoGenerateMips = true, wrapMode = TextureWrapMode.Clamp };
            rt.Create();
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(true, true, paint);
            GL.PushMatrix();
            GL.LoadPixelMatrix(0, 1, 0, 1);   // draw straight into the body's UV space
            // decal sheets are white masks: dark stamps on light paint, light stamps on dark paint
            bool lightPaint = paint.r * 0.299f + paint.g * 0.587f + paint.b * 0.114f > 0.55f;
            foreach (var l in layers)
            {
                var tex = CustomMarks.Resolve(l.Texture) ?? UiAssets.Texture(l.Texture);
                if (!tex) continue;
                stamp.mainTexture = tex;
                stamp.SetPass(0);
                var region = regions[l.Part];
                // positions are 0..1000 inside the region (y top-down); sizes are a percent of the region's long
                // side, keeping the sheet's own proportions; tall regions (bumper faces) turn the stamp upright
                bool upright = region.height > region.width * 1.2f && l.Part != 5;
                float length = upright ? region.height : region.width;
                float hw = l.Part == 5 ? 0.5f : l.ScaleX / 100f * length * 0.5f;
                float hh = l.Part == 5 ? 0.5f : hw * tex.height / Mathf.Max(1f, tex.width);
                var rot = Quaternion.Euler(0, 0, l.Rotation + (upright ? 90f : 0f));
                Vector2 center = l.Part == 5 ? new Vector2(0.5f, 0.5f)
                    : new Vector2(region.xMin + l.X / 1000f * region.width, region.yMax - l.Y / 1000f * region.height);
                Vector2 P(float x, float y) { var v3 = rot * new Vector3(x, y, 0); return center + new Vector2(v3.x, v3.y); }
                // texture rows were uploaded top-down, so v=1 is the image's top edge
                float u0 = l.FlipX ? 1 : 0, u1 = l.FlipX ? 0 : 1, vTop = l.FlipY ? 0 : 1, vBottom = l.FlipY ? 1 : 0;
                GL.Begin(GL.QUADS);
                GL.Color(!exactColor && !l.Exact && l.Part != 5 && lightPaint ? l.Color * new Color(0.13f, 0.13f, 0.15f, 1f) : l.Color);
                var a = P(-hw, hh); GL.TexCoord2(u0, vTop); GL.Vertex3(a.x, a.y, 0);
                var b = P(hw, hh); GL.TexCoord2(u1, vTop); GL.Vertex3(b.x, b.y, 0);
                var c = P(hw, -hh); GL.TexCoord2(u1, vBottom); GL.Vertex3(c.x, c.y, 0);
                var d = P(-hw, -hh); GL.TexCoord2(u0, vBottom); GL.Vertex3(d.x, d.y, 0);
                GL.End();
            }
            GL.PopMatrix();
            RenderTexture.active = prev;
            return rt;
        }
    }
}
