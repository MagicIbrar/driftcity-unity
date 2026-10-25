using System;
using System.Collections.Generic;
using System.IO;
using DriftCity.Core.Nif;
using UnityEngine;
using UnityEngine.Rendering;

namespace DriftCity
{
    /// <summary>Converts a parsed NIF scene graph into Unity GameObjects.</summary>
    public sealed class NifSceneBuilder
    {
        public bool AddColliders;
        public bool LogErrors = true;
        /// <summary>Also play controllers saved inactive: effects the client starts with
        /// NiTimeController::StartAnimations, which activates every controller (e.g. effects\ui\beat_fail.nif).</summary>
        public bool IncludeInactiveControllers;
        /// <summary>The Y/Z mirror inverts winding, so triangles are re-wound to keep Gamebryo's front faces
        /// front-facing in Unity (verified against the cras track and player car renders).</summary>
        public static bool FlipWinding = true;

        static readonly Dictionary<string, Material> materialCache = new Dictionary<string, Material>();
        static Shader shader;

        NifFile nif;
        string nifPath, nifDir;
        GameObject buildRoot;   // meshes and embedded textures made for this load are destroyed with it

        public static void ClearCaches()
        {
            foreach (var m in materialCache.Values) if (m) UnityEngine.Object.Destroy(m);
            materialCache.Clear();
        }

        public GameObject Load(string path, Transform parent = null)
        {
            var fs = DriftCityData.Files;
            nifPath = Core.NayaPack.NormalizePath(path);
            return LoadBytes(fs.Read(nifPath), nifPath, parent);
        }

        /// <summary>Builds a NIF held in memory (e.g. embedded in a city file). <paramref name="virtualPath"/>
        /// names the object and decides which archive folder textures are resolved near.</summary>
        public GameObject LoadBytes(byte[] data, string virtualPath, Transform parent = null)
        {
            nifPath = Core.NayaPack.NormalizePath(virtualPath);
            nifDir = Path.GetDirectoryName(nifPath);
            nif = NifFile.Parse(data);
            // embedded city NIFs carry trailing collision data; only a short parse is worth reporting
            if (nif.Error != null && LogErrors && nif.ParsedBlocks < nif.Blocks.Length)
                Debug.LogWarning($"{nifPath}: {nif.Error} (built {nif.ParsedBlocks}/{nif.Blocks.Length} blocks)");

            var root = new GameObject(Path.GetFileName(nifPath));
            buildRoot = root;
            if (parent) root.transform.SetParent(parent, false);
            foreach (var obj in nif.RootObjects())
                Build(obj, root.transform, new Dictionary<Type, NiProperty>());
            return root;
        }

        void Build(NiAVObject o, Transform parent, Dictionary<Type, NiProperty> inherited)
        {
            if (o == null) return;
            var go = new GameObject(string.IsNullOrEmpty(o.Name) ? o.TypeName : o.Name);
            var t = go.transform;
            t.SetParent(parent, false);
            t.localPosition = DriftCityData.ToUnity(o.Translation);
            t.localRotation = DriftCityData.ToUnity(o.Rotation);
            t.localScale = Vector3.one * o.Scale;

            // properties inherit down the tree; a child's property replaces the parent's of the same type
            var props = new Dictionary<Type, NiProperty>(inherited);
            foreach (var p in o.Properties)
                if (nif.Get<NiProperty>(p) is NiProperty prop) props[Normalize(prop.GetType())] = prop;

            if (o.Hidden) go.SetActive(false);
            AnimateNode(o, go);

            switch (o)
            {
                case NiGeometry geom when !(geom is NiParticleSystem):
                    BuildGeometry(geom, go, props);
                    break;
                case NiLODNode lod:
                    BuildChildren(lod, t, props, PickLod(lod));
                    break;
                case NiSwitchNode sw:
                    BuildChildren(sw, t, props, sw.ActiveIndex);
                    break;
                case NiBillboardNode bb:
                    go.AddComponent<NiBillboard>().Mode = bb.Mode;
                    BuildChildren(bb, t, props, -1);
                    break;
                case NiNode node:
                    BuildChildren(node, t, props, -1);
                    break;
            }
        }

        static Type Normalize(Type t) => t;

        // ---------------- keyframe controllers (played by NifAnimator on the load's root) ----------------
        NifAnimator animator;

        NifAnimator Animator
        {
            get
            {
                if (!animator && !buildRoot.TryGetComponent(out animator)) animator = buildRoot.AddComponent<NifAnimator>();
                return animator;
            }
        }

        /// <summary>The active controllers on an object's chain (Gamebryo flag 0x8 = active).</summary>
        IEnumerable<NiTimeController> Controllers(NiObjectNET o)
        {
            int guard = 0;
            for (var c = nif.Get<NiTimeController>(o.Controller); c != null && guard++ < 16; c = nif.Get<NiTimeController>(c.Next))
                if ((c.Flags & 0x8) != 0 || IncludeInactiveControllers) yield return c;
        }

        void AnimateNode(NiAVObject o, GameObject go)
        {
            foreach (var c in Controllers(o))
            {
                if (c is NiTransformController tc && nif.Get<NiTransformInterpolator>(tc.Interpolator) is NiTransformInterpolator ti
                    && nif.Get<NiTransformData>(ti.Data) is NiTransformData td)
                {
                    var ch = new NifAnimator.TransformChannel { Target = go.transform, Data = td };
                    ch.Init(c);
                    Animator.Add(ch, go.transform);
                }
                else if (c is NiVisController vc && nif.Get<NiBoolInterpolator>(vc.Interpolator) is NiBoolInterpolator bi
                         && nif.Get<NiBoolData>(bi.Data) is NiBoolData bd && bd.Keys.Count > 0)
                {
                    var ch = new NifAnimator.VisChannel { Target = go, Keys = bd.Keys };
                    ch.Init(c);
                    Animator.Add(ch, go.transform);
                }
            }
        }

        /// <summary>Alpha and texture flip-book controllers on a mesh's material and texturing properties: the mesh
        /// gets its own material copy for them to animate.</summary>
        void AnimateMaterial(Dictionary<Type, NiProperty> props, MeshRenderer mr)
        {
            Material own = null;
            Material Own() => own ? own : (own = mr.sharedMaterial = OwnedAssets.Track(buildRoot, new Material(mr.sharedMaterial)));
            if (props.TryGetValue(typeof(NiMaterialProperty), out var mp))
                foreach (var c in Controllers(mp))
                    if (c is NiAlphaController ac && nif.Get<NiFloatInterpolator>(ac.Interpolator) is NiFloatInterpolator fi
                        && nif.Get<NiFloatData>(fi.Data) is NiFloatData fd && fd.Keys.Count > 0)
                    {
                        var ch = new NifAnimator.AlphaChannel { Material = Own(), Keys = fd.Keys };
                        ch.Init(c);
                        Animator.Add(ch, mr.transform);
                    }
            if (props.TryGetValue(typeof(NiTexturingProperty), out var tp))
                foreach (var c in Controllers(tp))
                    if (c is NiFlipController fc && fc.Sources != null && fc.Sources.Length > 0 && nif.Get<NiFloatInterpolator>(fc.Interpolator) is NiFloatInterpolator fi
                        && nif.Get<NiFloatData>(fi.Data) is NiFloatData fd && fd.Keys.Count > 0)
                    {
                        var textures = new Texture2D[fc.Sources.Length];
                        for (int i = 0; i < textures.Length; i++) textures[i] = TexForSource(fc.Sources[i]);
                        var ch = new NifAnimator.FlipChannel { Material = Own(), Textures = textures, Keys = fd.Keys };
                        ch.Init(c);
                        Animator.Add(ch, mr.transform);
                    }
        }

        void BuildChildren(NiNode node, Transform t, Dictionary<Type, NiProperty> props, int onlyIndex)
        {
            for (int i = 0; i < node.Children.Length; i++)
            {
                if (onlyIndex >= 0 && i != onlyIndex) continue;
                Build(nif.Get<NiAVObject>(node.Children[i]), t, props);
            }
        }

        int PickLod(NiLODNode lod)
        {
            // highest detail = the child subtree with the most vertices (range data is unreliable across exports)
            int best = 0, bestCount = -1;
            for (int i = 0; i < lod.Children.Length; i++)
            {
                int count = CountVertices(nif.Get<NiAVObject>(lod.Children[i]), 0);
                if (count > bestCount) { best = i; bestCount = count; }
            }
            return best;
        }

        int CountVertices(NiAVObject o, int depth)
        {
            if (o == null || depth > 32) return 0;
            if (o is NiGeometry g) return nif.Get<NiGeometryData>(g.Data)?.NumVertices ?? 0;
            int total = 0;
            if (o is NiNode n) foreach (var c in n.Children) total += CountVertices(nif.Get<NiAVObject>(c), depth + 1);
            return total;
        }

        void BuildGeometry(NiGeometry geom, GameObject go, Dictionary<Type, NiProperty> props)
        {
            var data = nif.Get<NiGeometryData>(geom.Data);
            if (data?.Vertices == null || data.NumVertices == 0) return;
            ushort[] tris = data is NiTriShapeData ts ? ts.Triangles
                          : data is NiTriStripsData st ? st.ToTriangles() : null;
            if (tris == null || tris.Length < 3) return;

            props.TryGetValue(typeof(NiTexturingProperty), out var texP);
            var texturing = texP as NiTexturingProperty;
            int baseUV = (int)(texturing?.Base?.UVSet ?? 0);
            int darkUV = (int)(texturing?.Dark?.UVSet ?? 0);

            var mesh = OwnedAssets.Track(buildRoot, new Mesh { name = geom.Name });
            int n = data.NumVertices;
            var verts = new Vector3[n];
            for (int i = 0; i < n; i++) verts[i] = DriftCityData.ToUnity(data.Vertices[i]);
            mesh.vertices = verts;
            if (data.Normals != null)
            {
                var normals = new Vector3[n];
                for (int i = 0; i < n; i++) normals[i] = DriftCityData.ToUnity(data.Normals[i]);
                mesh.normals = normals;
            }
            bool blender = geom.ShaderName == "VCAlphaTextureBlender" && texturing != null;
            if (data.UVSets.Length > 0)
            {
                if (blender)
                {
                    // the blend shader picks channels per texture, so keep the raw channels
                    mesh.uv = ToUV(data.UVSets[0]);
                    if (data.UVSets.Length > 1) mesh.uv2 = ToUV(data.UVSets[1]);
                }
                else
                {
                    mesh.uv = ToUV(data.UVSets[Mathf.Clamp(baseUV, 0, data.UVSets.Length - 1)]);
                    if (data.UVSets.Length > 1) mesh.uv2 = ToUV(data.UVSets[Mathf.Clamp(darkUV, 0, data.UVSets.Length - 1)]);
                }
            }
            if (data.Colors != null)
            {
                var colors = new Color[n];
                for (int i = 0; i < n; i++) colors[i] = DriftCityData.ToUnity(data.Colors[i]);
                mesh.colors = colors;
            }
            var idx = new int[tris.Length];
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                idx[i] = tris[i];
                idx[i + 1] = FlipWinding ? tris[i + 2] : tris[i + 1];
                idx[i + 2] = FlipWinding ? tris[i + 1] : tris[i + 2];
            }
            mesh.triangles = idx;
            if (data.Normals == null) mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            bool transparent = false;
            var material = blender ? GetBlenderMaterial(geom, texturing)
                                   : GetGameShaderMaterial(geom, props, out transparent)
                                     ?? GetMaterial(props, data.Colors != null, data.UVSets.Length > 1, out transparent);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = material;
            mr.shadowCastingMode = transparent ? ShadowCastingMode.Off : ShadowCastingMode.On;
            if (!blender) AnimateMaterial(props, mr);

            if (AddColliders && !transparent)
                go.AddComponent<MeshCollider>().sharedMesh = mesh;
        }

        static Vector2[] ToUV(Vec2[] uv)
        {
            var r = new Vector2[uv.Length];
            for (int i = 0; i < uv.Length; i++) r[i] = new Vector2(uv[i].X, uv[i].Y);
            return r;
        }

        Material GetMaterial(Dictionary<Type, NiProperty> props, bool hasColors, bool hasUV2, out bool transparent)
        {
            props.TryGetValue(typeof(NiTexturingProperty), out var p0);
            props.TryGetValue(typeof(NiMaterialProperty), out var p1);
            props.TryGetValue(typeof(NiAlphaProperty), out var p2);
            props.TryGetValue(typeof(NiVertexColorProperty), out var p3);
            props.TryGetValue(typeof(NiStencilProperty), out var p4);
            props.TryGetValue(typeof(NiZBufferProperty), out var p5);
            var tex = p0 as NiTexturingProperty;
            var mat = p1 as NiMaterialProperty;
            var alpha = p2 as NiAlphaProperty;
            var vcol = p3 as NiVertexColorProperty;
            var stencil = p4 as NiStencilProperty;
            var zbuf = p5 as NiZBufferProperty;

            Texture2D baseTex = TexFor(tex?.Base), darkTex = TexFor(tex?.Dark), glowTex = TexFor(tex?.Glow);
            Color diffuse = mat != null ? DriftCityData.ToUnity(mat.Diffuse) : Color.white;
            diffuse.a = mat?.Alpha ?? 1f;
            Color emissive = mat != null ? DriftCityData.ToUnity(mat.Emissive) : Color.black;
            Color ambient = mat != null ? DriftCityData.ToUnity(mat.Ambient) : Color.white;
            // Gamebryo defaults: vertex colors feed ambient+diffuse, lighting = emissive+ambient+diffuse
            bool useVc = hasColors && (vcol == null || vcol.VertexMode != 0);
            int vertexMode = vcol == null ? 2 : (int)vcol.VertexMode;
            bool unlit = (vcol != null && vcol.LightingMode == 0) || (emissive.r + emissive.g + emissive.b) > 2.4f;
            if (unlit) emissive = Color.black;

            bool blend = alpha != null && alpha.Blend;
            bool test = alpha != null && alpha.Test;
            float cutoff = test ? Mathf.Max(alpha.Threshold / 255f, 0.01f) : 0f;
            int cull = stencil == null ? (int)CullMode.Back
                     : stencil.DrawMode == 3 ? (int)CullMode.Off
                     : stencil.DrawMode == 2 ? (int)CullMode.Front : (int)CullMode.Back;
            bool zwrite = zbuf != null ? zbuf.Write : !blend;
            int src = blend ? MapBlend(alpha.SrcBlend) : (int)BlendMode.One;
            int dst = blend ? MapBlend(alpha.DstBlend) : (int)BlendMode.Zero;
            transparent = blend;

            string matName = mat?.Name ?? "";
            string key = $"{matName}|{baseTex?.GetInstanceID()}|{darkTex?.GetInstanceID()}|{glowTex?.GetInstanceID()}|{diffuse}|{ambient}|{emissive}|{useVc}|{vertexMode}|{CarLit}|{unlit}|{cutoff}|{cull}|{zwrite}|{src}|{dst}|{hasUV2}|" +
                         $"{tex?.Base?.Tiling.X},{tex?.Base?.Tiling.Y},{tex?.Base?.Offset.X},{tex?.Base?.Offset.Y}";
            if (materialCache.TryGetValue(key, out var cached) && cached) return cached;

            if (!shader) shader = Shader.Find("DriftCity/NiStandard");
            // keep the Gamebryo material name ("body0", "window", ...) so car parts can find their glass
            var m = new Material(shader) { name = (matName.Length > 0 ? matName + ":" : "") + (baseTex ? baseTex.name : "NiMaterial") };
            if (baseTex) m.SetTexture("_MainTex", baseTex);
            if (tex?.Base != null)
            {
                m.SetTextureScale("_MainTex", new Vector2(tex.Base.Tiling.X, tex.Base.Tiling.Y));
                m.SetTextureOffset("_MainTex", new Vector2(tex.Base.Offset.X, tex.Base.Offset.Y));
            }
            if (darkTex) { m.SetTexture("_DarkTex", darkTex); m.SetFloat("_DarkUV", hasUV2 && tex.Dark.UVSet != tex.Base?.UVSet ? 1 : 0); }
            if (glowTex) m.SetTexture("_GlowTex", glowTex);
            m.SetColor("_Color", diffuse);
            m.SetColor("_EmissionColor", emissive);
            m.SetColor("_AmbientColor", ambient);
            m.SetFloat("_VertexMode", vertexMode);
            m.SetFloat("_CarLight", CarLit ? 1 : 0);
            m.SetFloat("_UseVertexColor", useVc ? 1 : 0);
            m.SetFloat("_Unlit", unlit ? 1 : 0);
            m.SetFloat("_Cutoff", cutoff);
            m.SetFloat("_SrcBlend", src);
            m.SetFloat("_DstBlend", dst);
            m.SetFloat("_ZWrite", zwrite ? 1 : 0);
            m.SetFloat("_Cull", cull);
            m.renderQueue = blend ? (int)RenderQueue.Transparent : test ? (int)RenderQueue.AlphaTest : (int)RenderQueue.Geometry;
            materialCache[key] = m;
            return m;
        }

        /// <summary>Vehicles (player cars, traffic, HUVs and their parts) are lit by the weather's car light.</summary>
        bool CarLit => nifPath != null && (nifPath.StartsWith("vehicle\\") || nifPath.StartsWith("data\\vehicle\\"));

        static readonly Dictionary<string, Shader> gameShaders = new Dictionary<string, Shader>();

        static Shader GameShader(string name)
        {
            if (!gameShaders.TryGetValue(name, out var s) || !s) gameShaders[name] = s = Shader.Find(name);
            return s;
        }

        /// <summary>
        /// Ports of the effects the NIFs name (DriftCity_RE/shaders): ActionGameCartoonFX on car bodies, ActionGameTree
        /// on most city buildings, ToonShading on lamps, ActionSpecularBand on the cars' gloss strips. Null for
        /// meshes without one, which the game draws with the fixed-function pipeline.
        /// </summary>
        Material GetGameShaderMaterial(NiGeometry geom, Dictionary<Type, NiProperty> props, out bool transparent)
        {
            transparent = false;
            string name = geom.ShaderName;
            if (name != "ActionGameCartoonFX" && name != "ActionGameTree" && name != "ToonShading" && name != "ActionSpecularBand") return null;
            props.TryGetValue(typeof(NiTexturingProperty), out var p0);
            props.TryGetValue(typeof(NiMaterialProperty), out var p1);
            props.TryGetValue(typeof(NiAlphaProperty), out var p2);
            props.TryGetValue(typeof(NiStencilProperty), out var p4);
            var tex = p0 as NiTexturingProperty;
            var mat = p1 as NiMaterialProperty;
            var alpha = p2 as NiAlphaProperty;
            var stencil = p4 as NiStencilProperty;
            var baseTex = TexFor(tex?.Base);
            Color paint = Color.white; float reflection = 100f;
            foreach (var e in geom.ExtraData)
            {
                var x = nif.Get<NiExtraData>(e);
                if (x is NiColorExtraData c && x.Name == "MaterialColor") paint = new Color(c.Value.R, c.Value.G, c.Value.B, 1f);
                else if (x is NiFloatExtraData f && x.Name == "Reflection") reflection = f.Value;
            }
            int cull = stencil == null ? (int)CullMode.Back : stencil.DrawMode == 3 ? (int)CullMode.Off : stencil.DrawMode == 2 ? (int)CullMode.Front : (int)CullMode.Back;
            bool blend = alpha != null && alpha.Blend, test = alpha != null && alpha.Test;
            float cutoff = test ? Mathf.Max(alpha.Threshold / 255f, 0.01f) : 0f;
            string matName = mat?.Name ?? "";
            string key = $"game|{name}|{matName}|{baseTex?.GetInstanceID()}|{paint}|{reflection}|{cull}|{blend}|{cutoff}|{CarLit}|{mat?.Alpha}";
            if (materialCache.TryGetValue(key, out var cached) && cached) { transparent = blend || name == "ActionSpecularBand"; return cached; }

            Material m;
            switch (name)
            {
                case "ActionGameCartoonFX":
                    m = new Material(GameShader("DriftCity/AGCar"));
                    m.SetColor("_MaterialColor", paint);
                    m.SetColor("_Color", new Color(1, 1, 1, mat?.Alpha ?? 1f));
                    break;
                case "ActionGameTree":
                    m = new Material(GameShader("DriftCity/AGTree"));
                    m.SetFloat("_Cutoff", cutoff);
                    m.SetFloat("_SrcBlend", blend ? MapBlend(alpha.SrcBlend) : (int)BlendMode.One);
                    m.SetFloat("_DstBlend", blend ? MapBlend(alpha.DstBlend) : (int)BlendMode.Zero);
                    m.SetFloat("_ZWrite", blend ? 0 : 1);
                    m.renderQueue = blend ? (int)RenderQueue.Transparent : test ? (int)RenderQueue.AlphaTest : (int)RenderQueue.Geometry;
                    transparent = blend;
                    break;
                case "ToonShading":
                    m = new Material(GameShader("DriftCity/Toon"));
                    m.SetFloat("_CarLight", CarLit ? 1 : 0);
                    break;
                default:
                    m = new Material(GameShader("DriftCity/SpecularBand"));
                    m.SetFloat("_Reflection", reflection);
                    transparent = true;
                    break;
            }
            // keep the Gamebryo material name ("body0", "map", ...) so paint and parts can find their meshes
            m.name = (matName.Length > 0 ? matName + ":" : "") + (baseTex ? baseTex.name : name);
            if (baseTex) m.SetTexture("_MainTex", baseTex);
            if (m.HasProperty("_Cull")) m.SetFloat("_Cull", cull);
            materialCache[key] = m;
            return m;
        }

        static Shader blenderShader;

        /// <summary>VCAlphaTextureBlender: shader texture slots chosen by per-mesh integer extra data.</summary>
        Material GetBlenderMaterial(NiGeometry geom, NiTexturingProperty tex)
        {
            int t1 = 0, t2 = 1, detail = -1;
            foreach (var e in geom.ExtraData)
                if (nif.Get<NiIntegerExtraData>(e) is NiIntegerExtraData x)
                {
                    if (x.Name == "Texture1Index") t1 = (int)x.Value;
                    else if (x.Name == "Texture2Index") t2 = (int)x.Value;
                    else if (x.Name == "DetailIndex") detail = (int)x.Value;
                }
            TexDesc Slot(int i) => i >= 0 && i < tex.ShaderTextures.Count ? tex.ShaderTextures[i] : null;
            TexDesc d1 = Slot(t1) ?? tex.Base, d2 = Slot(t2), dd = Slot(detail) ?? tex.Detail;
            Texture2D a = TexFor(d1), b = TexFor(d2), c = TexFor(dd);

            string key = $"blend|{a?.name}|{b?.name}|{c?.name}|{d1?.UVSet}|{d2?.UVSet}|{dd?.UVSet}|{d1?.Tiling.X},{d1?.Tiling.Y}|{d2?.Tiling.X},{d2?.Tiling.Y}|{dd?.Tiling.X},{dd?.Tiling.Y}";
            if (materialCache.TryGetValue(key, out var cached) && cached) return cached;
            if (!blenderShader) blenderShader = Shader.Find("DriftCity/VCAlphaTextureBlender");
            var m = new Material(blenderShader) { name = "TerrainBlend " + (a ? a.name : "") };
            void Set(string prop, Texture2D t, TexDesc d, string uvProp)
            {
                if (t) m.SetTexture(prop, t);
                if (d != null)
                {
                    m.SetTextureScale(prop, new Vector2(d.Tiling.X, d.Tiling.Y));
                    m.SetTextureOffset(prop, new Vector2(d.Offset.X, d.Offset.Y));
                    m.SetFloat(uvProp, d.UVSet > 0 ? 1 : 0);
                }
            }
            Set("_Tex1", a, d1, "_Tex1UV");
            Set("_Tex2", b ? b : a, d2 ?? d1, "_Tex2UV");
            Set("_Detail", c, dd, "_DetailUV");
            m.SetFloat("_HasDetail", c ? 1 : 0);
            materialCache[key] = m;
            return m;
        }

        Texture2D TexFor(TexDesc desc)
        {
            if (desc == null) return null;
            var t = TexForSource(desc.Source);
            if (t && !(nif.Get<NiSourceTexture>(desc.Source) is NiSourceTexture s && !s.External)) t.wrapMode = desc.ClampMode == 0 ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
            return t;
        }

        Texture2D TexForSource(int source)
        {
            var src = nif.Get<NiSourceTexture>(source);
            if (src == null) return null;
            if (!src.External && nif.Get<NiPixelData>(src.PixelData) is NiPixelData pd)
                return OwnedAssets.Track(buildRoot, TextureLibrary.FromPixelData(pd, nif));
            return TextureLibrary.Get(src.FileName, nifDir);
        }

        // Gamebryo NiAlphaProperty blend function -> Unity BlendMode
        static int MapBlend(int f)
        {
            switch (f)
            {
                case 0: return (int)BlendMode.One;
                case 1: return (int)BlendMode.Zero;
                case 2: return (int)BlendMode.SrcColor;
                case 3: return (int)BlendMode.OneMinusSrcColor;
                case 4: return (int)BlendMode.DstColor;
                case 5: return (int)BlendMode.OneMinusDstColor;
                case 6: return (int)BlendMode.SrcAlpha;
                case 7: return (int)BlendMode.OneMinusSrcAlpha;
                case 8: return (int)BlendMode.DstAlpha;
                case 9: return (int)BlendMode.OneMinusDstAlpha;
                case 10: return (int)BlendMode.SrcAlphaSaturate;
                default: return (int)BlendMode.One;
            }
        }
    }

    /// <summary>Keeps a NiBillboardNode facing the active camera.</summary>
    public sealed class NiBillboard : MonoBehaviour
    {
        public ushort Mode; // 0/2 = always face camera, 1/3/4 = rotate around up axis
        void LateUpdate()
        {
            var cam = Camera.main;
            if (!cam) return;
            Vector3 toCam = cam.transform.position - transform.position;
            if (Mode == 1 || Mode == 3 || Mode == 4) toCam.y = 0;
            if (toCam.sqrMagnitude < 1e-6f) return;
            // Gamebryo billboards face local -Y (Unity -Z after conversion)
            transform.rotation = Quaternion.LookRotation(-toCam.normalized, Vector3.up);
        }
    }
}
