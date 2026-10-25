using System;
using System.Collections;
using System.Collections.Generic;
using DriftCity.Core.Nif;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DriftCity.Editor")]

namespace DriftCity.Game
{
    /// <summary>
    /// The story intro a new account sees before its first licence (DriftCity_RE/INTRO.md), as the client's intro state
    /// (vtable 0x87d2e0) plays it:
    /// <list type="bullet">
    /// <item>enter 0x753470: load game\intro\newintro.nif at (0, 100, 0) (0x7529b0), time range = the lowest begin and
    /// highest end key time of every controller in the scene (0x4cbbc0: 0 .. 63.4 s), renderer background black;
    /// the US client (country code 1) starts no music here (only codes 4..10 play BGM 60001, the title track);</item>
    /// <item>camera: its own orthographic NiCamera at the origin looking down +Y (up +Z), frustum ±51.2 x ±38.4, near 0,
    /// far 512, viewport narrowed to a centred 4:3 strip on wider screens;</item>
    /// <item>update 0x752910: scene updated at start + elapsed; elapsed grows by the frame time from the second frame on;
    /// once past the end key time the state moves on;</item>
    /// <item>key-up of Esc, Space or Enter skips (0x752670);</item>
    /// <item>subtitles (0x7531f0, drawn by 0x752e70): globaltext Intro_01..12 at fixed times, Calibri 28 px weight 400,
    /// white, horizontally centred across the whole screen with word wrap, top at screen height - 768 + 590 (second line
    /// + 635), fading in and out over 1 s.</item>
    /// </list>
    /// </summary>
    public static class IntroScene
    {
        const string NifPath = @"game\intro\newintro.nif";
        /// <summary>Where the intro is built: far below any city so nothing else is inside its 512-unit box.</summary>
        static readonly Vector3 Origin = new Vector3(0f, -20000f, 0f);
        const float FadeTime = 1f;      // [this+0x38], set by 0x7531f0

        /// <summary>The subtitle table 0x7531f0 builds: key, start, duration, line top (y + screen height - 768).</summary>
        internal static readonly (string key, float start, float duration, int y)[] Lines =
        {
            ("Intro_01", 16.7f, 4f, 590), ("Intro_02", 19.9f, 4f, 590), ("Intro_03", 21.9f, 4f, 635),
            ("Intro_04", 26f, 4f, 590), ("Intro_05", 31f, 4f, 590), ("Intro_06", 35.5f, 3.5f, 590),
            ("Intro_07", 39f, 4f, 590), ("Intro_08", 41.2f, 5.5f, 635), ("Intro_09", 47.5f, 5.5f, 590),
            ("Intro_10", 47.5f, 5.5f, 635), ("Intro_11", 53.5f, 7f, 590), ("Intro_12", 53.5f, 7f, 635),
        };

        /// <summary>Builds the intro, plays it for the client's duration (or until skipped), removes it, then calls
        /// <paramref name="done"/>.</summary>
        public static IEnumerator Play(MonoBehaviour host, Action done)
        {
            Session s = null;
            try { s = Session.Build(overlayText: true); }
            catch (Exception ex) { Debug.LogWarning("Drift City intro: " + ex.Message); }
            if (s == null) { done?.Invoke(); yield break; }
            s.Watch(host);
            s.SuspendFog();
            float elapsed = 0f;
            bool first = true;
            var keys = new SkipKeys();
            try
            {
                while (true)
                {
                    s.Pose(elapsed);
                    yield return null;
                    if (keys.Released()) break;
                    // the first update only poses the scene (flag [this+0x3c]); later ones add the frame time
                    if (first) first = false;
                    else elapsed += Mathf.Min(Time.unscaledDeltaTime, 0.25f);
                    if (elapsed > s.End) break;
                }
            }
            finally { s.Dispose(); }
            done?.Invoke();
        }

        /// <summary>WM_KEYUP of VK_ESCAPE, VK_SPACE or VK_RETURN (0x752670). A release only counts when its press happened
        /// during the intro, so the key that opened it cannot skip it.</summary>
        sealed class SkipKeys
        {
            bool held;
            public bool Released()
            {
#if ENABLE_INPUT_SYSTEM
                var kb = Keyboard.current;
                if (kb == null) return false;
                bool down = kb.escapeKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame || kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame;
                bool up = kb.escapeKey.wasReleasedThisFrame || kb.spaceKey.wasReleasedThisFrame || kb.enterKey.wasReleasedThisFrame || kb.numpadEnterKey.wasReleasedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
                bool down = Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);
                bool up = Input.GetKeyUp(KeyCode.Escape) || Input.GetKeyUp(KeyCode.Space) || Input.GetKeyUp(KeyCode.Return) || Input.GetKeyUp(KeyCode.KeypadEnter);
#else
                bool down = false, up = false;
#endif
                if (down) held = true;
                return up && held;
            }
        }

        /// <summary>One built intro: the NIF, its cameras and the subtitle canvas. Also driven frame by frame by the
        /// editor's DriftCityIntroShots.</summary>
        internal sealed class Session : IDisposable
        {
            public GameObject Root;
            public Camera Camera, Backdrop;
            public Canvas Canvas;
            /// <summary>Highest end key time of the scene's controllers (lowest begin is 0).</summary>
            public float Start, End;

            NifAnimator animator;
            float lastElapsed;
            readonly List<Morph> morphs = new List<Morph>();
            readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
            readonly List<(Text text, float start, float duration)> texts = new List<(Text, float, float)>();
            NifFile nif;

            sealed class Morph
            {
                public Mesh Mesh;
                public Vector3[][] Targets;     // Unity space, target 0 = base
                public bool Relative;
                public KeyGroup<float>[] Weights;
                public float Frequency, Phase, StartTime, StopTime;
                public int Cycle;
                public Vector3[] Work;
            }

            public static Session Build(bool overlayText)
            {
                var fs = DriftCityData.Files;
                if (fs == null || !fs.Exists(NifPath)) return null;
                var data = fs.Read(NifPath);
                var s = new Session();
                s.Root = new GameObject("Drift City Intro");
                s.Root.transform.position = Origin;
                try { s.Create(data, overlayText); }
                catch { s.Dispose(); throw; }
                return s;
            }

            void Create(byte[] data, bool overlayText)
            {
                // ---- the scene: NiStream::Load, root translated to (0, 100, 0) (Gamebryo Y = Unity Z) ----
                var scene = new NifSceneBuilder().LoadBytes(data, NifPath, Root.transform);
                scene.transform.localPosition = new Vector3(0f, 0f, 100f);
                animator = scene.GetComponent<NifAnimator>();
                if (animator) animator.enabled = false;     // posed from the intro clock, not Time.time

                nif = NifFile.Parse(data);
                var morphData = ReadMorphData(data);
                Start = 0f; End = 0f;
                int i = 0;
                foreach (var o in nif.RootObjects())
                {
                    if (i >= scene.transform.childCount) break;
                    Walk(o, scene.transform.GetChild(i++), null, morphData);
                }
                FixMaterials(scene);

                // ---- cameras: the intro's NiCamera, and a full-screen black clear for the bars beside its viewport ----
                Backdrop = new GameObject("Intro Backdrop").AddComponent<Camera>();
                Backdrop.transform.SetParent(Root.transform, false);
                Backdrop.clearFlags = CameraClearFlags.SolidColor;
                Backdrop.backgroundColor = Color.black;
                Backdrop.cullingMask = 0;
                Backdrop.depth = 98;
                Backdrop.orthographic = true;

                Camera = new GameObject("Intro Camera").AddComponent<Camera>();
                Camera.transform.SetParent(Root.transform, false);
                Camera.transform.localRotation = Quaternion.identity;   // looks down Gamebryo +Y, up +Z
                Camera.clearFlags = CameraClearFlags.SolidColor;
                Camera.backgroundColor = Color.black;                   // renderer background set to (0,0,0,1) on enter
                Camera.orthographic = true;
                Camera.orthographicSize = 38.4f;                        // NiFrustum(-51.2, 51.2, 38.4, -38.4, 0, 512, ortho)
                Camera.nearClipPlane = 0.01f;
                Camera.farClipPlane = 512f;
                Camera.cullingMask = ~(1 << 5);                         // everything but UI
                Camera.depth = 99;
                Camera.transparencySortMode = TransparencySortMode.Orthographic;
                Camera.allowHDR = false;
                Camera.allowMSAA = false;
                ApplyViewport();

                BuildText(overlayText);
            }

            /// <summary>The client keeps the 102.4 x 76.8 frustum and, when the screen is wider than 4:3, narrows the viewport
            /// to a centred strip (0x7529b0): left = (w/h * 0.75 - 1) * 0.5, right = 1 - left (exactly 4:3 only near 4:3;
            /// at 16:9 the strip is 2/3 of the width, so the picture is a little narrower than 4:3). Narrower screens stretch.</summary>
            void ApplyViewport()
            {
                float aspect = Screen.height > 0 ? (float)Screen.width / Screen.height : 4f / 3f;
                if (Camera.targetTexture) aspect = (float)Camera.targetTexture.width / Camera.targetTexture.height;
                if (aspect > 4f / 3f)
                {
                    float left = (aspect * 0.75f - 1f) * 0.5f;
                    Camera.rect = new Rect(left, 0f, 1f - 2f * left, 1f);
                }
                else Camera.rect = new Rect(0f, 0f, 1f, 1f);
                Camera.aspect = 4f / 3f;
            }

            void BuildText(bool overlay)
            {
                var go = new GameObject("Intro Subtitles");
                go.transform.SetParent(Root.transform, false);
                go.layer = 5;
                Canvas = go.AddComponent<Canvas>();
                if (overlay)
                {
                    Canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                    Canvas.sortingOrder = 30000;
                }
                var scaler = go.AddComponent<CanvasScaler>();   // the remake's UI frame: 1024x768, scaled with height
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1024, 768);
                scaler.matchWidthOrHeight = 1f;
                var font = UiAssets.Font("Calibri 28 400", out int size, out FontStyle style);
                foreach (var (key, start, duration, y) in Lines)
                {
                    var t = new GameObject(key).AddComponent<Text>();
                    t.gameObject.layer = 5;
                    t.transform.SetParent(go.transform, false);
                    t.font = font; t.fontSize = size; t.fontStyle = style;
                    t.text = GameTables.Text(key).Trim();
                    t.color = new Color(1f, 1f, 1f, 0f);
                    t.alignment = TextAnchor.UpperCenter;          // DT_CENTER | DT_WORDBREAK | DT_NOCLIP, top at y
                    t.horizontalOverflow = HorizontalWrapMode.Wrap;
                    t.verticalOverflow = VerticalWrapMode.Overflow;
                    t.raycastTarget = false;
                    t.supportRichText = false;
                    var rt = t.rectTransform;
                    rt.anchorMin = new Vector2(0f, 0f);
                    rt.anchorMax = new Vector2(1f, 0f);
                    rt.pivot = new Vector2(0.5f, 1f);
                    rt.anchoredPosition = new Vector2(0f, 768 - y);   // y counts from screen height - 768
                    rt.sizeDelta = new Vector2(0f, 40f);
                    t.enabled = false;
                    texts.Add((t, start, duration));
                }
            }

            /// <summary>Puts the scene, the morphs and the subtitles at an intro time (seconds since it started).</summary>
            public void Pose(float elapsed)
            {
                if (!Root) return;
                float t = Start + elapsed;
                lastElapsed = elapsed;
                if (animator) { animator.Sample(t); FixEuler(); }
                foreach (var m in morphs) ApplyMorph(m, t);
                if (Camera) ApplyViewport();
                foreach (var (text, start, duration) in texts)
                {
                    if (!text) continue;
                    bool on = start <= elapsed && elapsed <= start + duration;
                    text.enabled = on;
                    if (!on) continue;
                    float since = elapsed - start, left = duration - since;
                    // alpha = 255 * fraction of the 1 s fade, as an integer, else opaque white (0x752e70)
                    float a = since < FadeTime ? Mathf.Floor(since / FadeTime * 255f) / 255f
                            : left < FadeTime ? Mathf.Floor(left / FadeTime * 255f) / 255f : 1f;
                    text.color = new Color(1f, 1f, 1f, a);
                }
            }

            /// <summary>Euler-angle rotation keys (the globe, its rings, the island card, the closing logo): Gamebryo's matrix
            /// for angles (x, y, z) is the transpose of NifAnimator's RotX*RotY*RotZ (checked against the static matrices
            /// these nodes store at their first key), so the animator turns them the wrong way; re-posed here.</summary>
            void FixEuler()
            {
                foreach (var ch in animator.Channels)
                {
                    if (!(ch is NifAnimator.TransformChannel tc) || !tc.Target || tc.Data == null) continue;
                    if (tc.Data.Rotations.Count > 0 || tc.Data.Euler == null) continue;
                    float lt = tc.LocalTime(Start + lastElapsed);
                    float A(int axis) => tc.Data.Euler[axis] != null && tc.Data.Euler[axis].Count > 0 ? Sample(tc.Data.Euler[axis], lt) : 0f;
                    float x = A(0), y = A(1), z = A(2);
                    float cx = Mathf.Cos(x), sx = Mathf.Sin(x), cy = Mathf.Cos(y), sy = Mathf.Sin(y), cz = Mathf.Cos(z), sz = Mathf.Sin(z);
                    // P = RotX(x) * RotY(y) * RotZ(z) with RotX = [1 0 0; 0 c s; 0 -s c], RotY = [c 0 -s; 0 1 0; s 0 c],
                    // RotZ = [c s 0; -s c 0; 0 0 1]; Gamebryo's matrix is P transposed
                    float p00 = cy * cz, p01 = cy * sz, p02 = -sy;
                    float p10 = sx * sy * cz - cx * sz, p11 = sx * sy * sz + cx * cz, p12 = sx * cy;
                    float p20 = cx * sy * cz + sx * sz, p21 = cx * sy * sz - sx * cz, p22 = cx * cy;
                    var m = new Mat3 { M00 = p00, M01 = p10, M02 = p20, M10 = p01, M11 = p11, M12 = p21, M20 = p02, M21 = p12, M22 = p22 };
                    tc.Target.localRotation = DriftCityData.ToUnity(m);
                }
            }

            /// <summary>Removes everything if the host that runs the intro goes away mid-way.</summary>
            public void Watch(MonoBehaviour host)
            {
                if (Root && host) Root.AddComponent<IntroWatch>().Init(host, this);
            }

            bool fogSuspended, fogWas;

            /// <summary>The client draws the intro without fog; NiStandard would otherwise apply the city's.</summary>
            public void SuspendFog()
            {
                if (fogSuspended) return;
                fogWas = RenderSettings.fog;
                fogSuspended = true;
                RenderSettings.fog = false;
            }

            public void Dispose()
            {
                if (fogSuspended) { RenderSettings.fog = fogWas; fogSuspended = false; }
                foreach (var o in owned) if (o) Kill(o);
                owned.Clear();
                if (Root) Kill(Root);
                Root = null;
            }

            static void Kill(UnityEngine.Object o)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(o);
                else UnityEngine.Object.DestroyImmediate(o);
            }

            // ---------------- scene walk: time range, morphs, texture names ----------------

            /// <summary>Walks the NIF tree beside the built GameObjects (the builder makes one per non-null object, in
            /// order): the begin/end key times as 0x4cbbc0 gathers them, geometry morphs, and textures resolved elsewhere.</summary>
            void Walk(NiAVObject o, Transform t, NiTexturingProperty tex, Dictionary<int, (bool rel, Vec3[][] targets)> morphData)
            {
                if (o == null || !t) return;
                Range(o);
                foreach (var p in o.Properties)
                {
                    var prop = nif.Get<NiProperty>(p);
                    if (prop is NiObjectNET pn) Range(pn);
                    if (prop is NiTexturingProperty tp) tex = tp;
                }
                if (o is NiGeometry geom)
                {
                    AddMorphs(geom, t, morphData);
                    FixTexture(tex, t);
                }
                if (o is NiNode node)
                {
                    int ci = 0;
                    foreach (var c in node.Children)
                    {
                        var child = nif.Get<NiAVObject>(c);
                        if (child == null) continue;
                        if (ci >= t.childCount) break;
                        Walk(child, t.GetChild(ci++), tex, morphData);
                    }
                }
            }

            void Range(NiObjectNET o)
            {
                int guard = 0;
                for (var c = nif.Get<NiTimeController>(o.Controller); c != null && guard++ < 16; c = nif.Get<NiTimeController>(c.Next))
                {
                    if (c.StartTime < Start) Start = c.StartTime;
                    if (c.StopTime > End) End = c.StopTime;
                }
            }

            /// <summary>The builder resolves "white.tga"/"red.tga" through same-named .dds files elsewhere in the
            /// archives (game\credits\white.dds, oros\obj\red.dds); the client reads the intro folder's own files.</summary>
            void FixTexture(NiTexturingProperty tex, Transform t)
            {
                var src = tex?.Base != null ? nif.Get<NiSourceTexture>(tex.Base.Source) : null;
                if (src == null || !src.External || string.IsNullOrEmpty(src.FileName)) return;
                if (!t.TryGetComponent<MeshRenderer>(out var mr) || !mr.sharedMaterial) return;
                string own = @"game\intro\" + System.IO.Path.GetFileName(src.FileName.Replace('/', '\\'));
                if (!DriftCityData.Files.Exists(own)) return;
                var cur = mr.sharedMaterial.mainTexture;
                if (cur && string.Equals(cur.name, System.IO.Path.GetFileName(own), StringComparison.OrdinalIgnoreCase)) return;
                var right = TextureLibrary.Get(own, @"game\intro");
                if (!right) return;
                var m = Own(mr);
                m.mainTexture = right;
            }

            void AddMorphs(NiGeometry geom, Transform t, Dictionary<int, (bool rel, Vec3[][] targets)> morphData)
            {
                if (!t.TryGetComponent<MeshFilter>(out var mf) || !mf.sharedMesh) return;
                int guard = 0;
                for (var c = nif.Get<NiTimeController>(geom.Controller); c != null && guard++ < 16; c = nif.Get<NiTimeController>(c.Next))
                {
                    if (!(c is NiGeomMorpherController gm) || (c.Flags & 0x8) == 0) continue;
                    if (!morphData.TryGetValue(gm.Data, out var md) || md.targets.Length == 0) continue;
                    if (md.targets[0].Length != mf.sharedMesh.vertexCount) continue;
                    var m = new Morph
                    {
                        Mesh = mf.sharedMesh,
                        Relative = md.rel,
                        Targets = new Vector3[md.targets.Length][],
                        Weights = new KeyGroup<float>[md.targets.Length],
                        Frequency = c.Frequency, Phase = c.Phase, StartTime = c.StartTime, StopTime = c.StopTime,
                        Cycle = (c.Flags >> 1) & 3,
                        Work = new Vector3[md.targets[0].Length],
                    };
                    for (int k = 0; k < md.targets.Length; k++)
                    {
                        var src = md.targets[k];
                        var dst = m.Targets[k] = new Vector3[src.Length];
                        for (int v = 0; v < src.Length; v++) dst[v] = DriftCityData.ToUnity(src[v]);
                        if (k < gm.Interpolators.Length && nif.Get<NiFloatInterpolator>(gm.Interpolators[k]) is NiFloatInterpolator fi
                            && nif.Get<NiFloatData>(fi.Data) is NiFloatData fd && fd.Keys.Count > 0)
                            m.Weights[k] = fd.Keys;
                    }
                    morphs.Add(m);
                }
            }

            static void ApplyMorph(Morph m, float time)
            {
                if (!m.Mesh) return;
                float t = time * m.Frequency + m.Phase, span = m.StopTime - m.StartTime;
                if (span > 1e-5f)
                    t = m.Cycle == 2 ? Mathf.Clamp(t, m.StartTime, m.StopTime)
                      : m.Cycle == 1 ? m.StartTime + Mathf.PingPong(t - m.StartTime, span)
                      : m.StartTime + Mathf.Repeat(t - m.StartTime, span);
                var w = m.Work;
                // relative morphs: base (target 0) plus weighted offsets; absolute ones: weighted sum
                for (int v = 0; v < w.Length; v++) w[v] = m.Relative ? m.Targets[0][v] : Vector3.zero;
                for (int k = m.Relative ? 1 : 0; k < m.Targets.Length; k++)
                {
                    float weight = m.Weights[k] != null ? Sample(m.Weights[k], t) : 0f;
                    if (weight == 0f) continue;
                    var d = m.Targets[k];
                    for (int v = 0; v < w.Length; v++) w[v] += d[v] * weight;
                }
                m.Mesh.vertices = w;
                m.Mesh.RecalculateBounds();
            }

            static float Sample(KeyGroup<float> g, float t)
            {
                int n = g.Count;
                if (n == 1 || t <= g.Times[0]) return g.Values[0];
                if (t >= g.Times[n - 1]) return g.Values[n - 1];
                int i = 1;
                while (i < n - 1 && g.Times[i] < t) i++;
                float span = g.Times[i] - g.Times[i - 1];
                return Mathf.Lerp(g.Values[i - 1], g.Values[i], span > 1e-6f ? (t - g.Times[i - 1]) / span : 1f);
            }

            /// <summary>NiMorphData targets (the shared parser skips them): block index -> (relative, target vertices).</summary>
            static Dictionary<int, (bool rel, Vec3[][] targets)> ReadMorphData(byte[] data)
            {
                var result = new Dictionary<int, (bool, Vec3[][])>();
                var r = new NifReader(data);
                r.Line();
                r.Version = r.U32();
                r.U8();
                r.UserVersion = r.U32();
                uint numBlocks = r.Count(1 << 20);
                if (r.UserVersion >= 3) { r.U32(); for (int k = 0; k < 3; k++) r.Skip(r.U8()); }
                int numTypes = r.U16();
                var types = new string[numTypes];
                for (int k = 0; k < numTypes; k++) types[k] = r.Str();
                var typeIndex = r.U16s((int)numBlocks);
                uint groups = r.U32();
                r.Skip((int)groups * 4);
                for (int b = 0; b < numBlocks; b++)
                {
                    string type = types[typeIndex[b] & 0x7FFF];
                    var block = NifTypes.Create(type);
                    if (block == null) break;
                    int start = r.Pos;
                    if (type == "NiMorphData")
                    {
                        var m = new NifReader(data) { Pos = start, Version = r.Version, UserVersion = r.UserVersion };
                        uint count = m.Count(4096), verts = m.Count();
                        bool rel = m.U8() != 0;
                        var targets = new Vec3[count][];
                        for (int k = 0; k < count; k++) { m.Str(); m.F32(); targets[k] = m.V3s((int)verts); }
                        result[b] = (rel, targets);
                    }
                    block.Read(r);
                }
                return result;
            }

            // ---------------- materials ----------------

            /// <summary>The intro has no lights: Gamebryo's lit colour is then the material's emissive alone (the textured
            /// panels are emissive white, the black covers emissive black). NiStandard is made to compute exactly that.</summary>
            void FixMaterials(GameObject scene)
            {
                foreach (var mr in scene.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var src = mr.sharedMaterial;
                    if (!src || !src.HasProperty("_Unlit") || src.GetFloat("_Unlit") > 0.5f) continue;
                    var m = Own(mr);
                    var emissive = m.HasProperty("_EmissionColor") ? m.GetColor("_EmissionColor") : Color.black;
                    var c = m.color;
                    m.color = new Color(0f, 0f, 0f, c.a);
                    m.SetColor("_AmbientColor", Color.black);
                    m.SetColor("_EmissionColor", new Color(emissive.r, emissive.g, emissive.b, 0f));
                }
            }

            /// <summary>A material copy for this intro only, keeping the animator's alpha/flip channels on it.</summary>
            Material Own(MeshRenderer mr)
            {
                var old = mr.sharedMaterial;
                if (owned.Contains(old)) return old;
                var m = new Material(old) { name = old.name };
                owned.Add(m);
                mr.sharedMaterial = m;
                if (animator)
                    foreach (var ch in animator.Channels)
                    {
                        if (ch is NifAnimator.AlphaChannel ac && ac.Material == old) ac.Material = m;
                        else if (ch is NifAnimator.FlipChannel fc && fc.Material == old) fc.Material = m;
                    }
                return m;
            }
        }

        /// <summary>Destroys an intro whose host was destroyed before it finished.</summary>
        sealed class IntroWatch : MonoBehaviour
        {
            MonoBehaviour host;
            Session session;
            public void Init(MonoBehaviour h, Session s) { host = h; session = s; }
            void LateUpdate()
            {
                if (host) return;
                session?.Dispose();
                session = null;
            }
        }
    }
}
