using System.Collections.Generic;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// The client's distance window for the open city (DriftCity_RE/TRAFFIC.md section 12). DriftCity.exe keeps only the
    /// terrain cells (and the blocks, props and shadow decals hung under them) of a (2r+1) x (2r+1) window of 200 m grid
    /// cells around the camera's cell, r = the graphics option VisibleRange 1..5 (0x415e80 -> 0x40e9a0 sizes the window
    /// arrays (2r+1)^2), and draws cars only up to (r - 1) x 200 + 100 m in the open city (state 0x0e; 0x4baee0 sets that
    /// far plane on the actor cull frustum 0x4b4dd0 / 0x4b4e30; (r - 1) x 50 + 30 m in state 9).
    ///
    /// The remake keeps every city object loaded (no disk streaming; the whole city fits in memory) and switches the
    /// renderers outside the window off with <see cref="Renderer.forceRenderingOff"/> (independent of
    /// Renderer.enabled, which breakable props and effects use), so they cost no culling or draw work. Colliders stay:
    /// traffic far from the player still probes the ground. At the default r = 5 the window (+-1000..1100 m) is beyond
    /// the 512 m camera far plane, so nothing visible changes; lower settings draw less, as in the client.
    ///
    /// Fade-in (TRAFFIC.md 12): with VisibleRange &gt; 2 the client fades in what comes into view (0x41a610 turns alpha
    /// blending on and draws with the material alpha of a fade object: 0x40c840 starts it at 0 with rate 5 per second
    /// up to 1, 0x40c8a0 adds rate x frame time and restarts at 0 when the object was not drawn for more than 2 s,
    /// 0x40c860 stamps the last drawn time) [exact]. The remake fades per object like the client (<see cref="PerObject"/>,
    /// round 4): each renderer of a window cell keeps its own last-seen time; the cells in the camera frustum test their
    /// renderers' bounds (cached at load) and one back in view after more than 2 s, or in a cell entering the window,
    /// starts its own fade. A fading renderer gets a property block with _DCObjFadeOut = 1 - alpha (cleared to 0 at the
    /// end, other block values kept); the city shaders (DCOriginal.cginc DCFadeAlpha / DC_FADE_CLIP: NiStandard world
    /// meshes, AGTree, DCToon; the terrain and the cars do not fade) dither by it. The per-cell array stays for
    /// <see cref="PerObject"/> = false and the ForceFadeAlpha test hook. The lazy vertex-buffer build (at most 10 ms a
    /// frame) is not needed: meshes are built at city load.
    /// </summary>
    public sealed class CityStreaming : MonoBehaviour
    {
        public float CellSize = 200f;
        /// <summary>Grid origin (Unity x, z): the south-west corner of the terrain grid.</summary>
        public Vector2 Origin;
        /// <summary>The window radius in cells; -1 = read GameOptions.VisibleRange every frame.</summary>
        public int RangeOverride = -1;
        /// <summary>Global switch (tests measure with and without the window).</summary>
        public static bool Enabled = true;
        /// <summary>Global switch for the fade-in of cells coming into view (tests measure with and without it).</summary>
        public static bool FadeEnabled = true;
        /// <summary>Fade rate: alpha per second (0x40c840 sets +0x10 = 5.0; alpha starts at 0, capped at +0xc = 1.0).</summary>
        public const float FadeRate = 5f;
        /// <summary>Not drawn for longer than this (s) and the fade starts over (0x40c8a0 compares with 2.0 at 0x85b340).</summary>
        public const float FadeRestart = 2f;
        /// <summary>The client fades only with VisibleRange above 2 (0x41a610: 2 &lt; DAT_008d297c).</summary>
        public const int FadeMinRange = 3;

        public int Range => RangeOverride > 0 ? RangeOverride : GameOptions.VisibleRange;
        public int RendererCount { get; private set; }
        public int VisibleCount { get; private set; }
        public Vector2Int CurrentCell => current;
        /// <summary>Cells fading in right now (tests).</summary>
        public int FadingCount { get; private set; }
        /// <summary>Fades started since the city was collected (tests).</summary>
        public int FadesStarted { get; private set; }
        /// <summary>Test hook: hold every window cell at this fade alpha (worst case for the cost; -1 = off).</summary>
        public static float ForceFadeAlpha = -1f;
        /// <summary>Fade each object on its own timer (the client's fade objects) instead of whole 200 m cells.</summary>
        public static bool PerObject = true;
        /// <summary>Renderers fading on their own right now (tests).</summary>
        public int FadingObjects => fadingObjs.Count;

        sealed class Cell
        {
            public readonly List<Renderer> Renderers = new List<Renderer>();
            public bool On = true;
            public Bounds Bounds;
            public float LastSeen, FadeStart = -100f, OffSince;
            // per object: bounds cached at load, last time in the frustum, fade start
            public Bounds[] RBounds;
            public float[] RSeen, RFade;
        }
        readonly List<(Cell cell, int i)> fadingObjs = new List<(Cell, int)>();
        MaterialPropertyBlock mpb;
        static readonly int ObjFadeId = Shader.PropertyToID("_DCObjFadeOut");
        readonly Dictionary<Vector2Int, Cell> cells = new Dictionary<Vector2Int, Cell>();
        Vector2Int current = new Vector2Int(int.MinValue, int.MinValue);
        int appliedRange = -1;
        bool appliedEnabled = true;

        /// <summary>The cell a Unity position falls in.</summary>
        public Vector2Int CellOf(Vector3 p) =>
            new Vector2Int(Mathf.FloorToInt((p.x - Origin.x) / CellSize), Mathf.FloorToInt((p.z - Origin.y) / CellSize));

        /// <summary>Whether a cell is inside the window around <paramref name="center"/> (Chebyshev distance &lt;= r).</summary>
        public static bool InWindow(Vector2Int cell, Vector2Int center, int r) =>
            Mathf.Abs(cell.x - center.x) <= r && Mathf.Abs(cell.y - center.y) <= r;

        /// <summary>The client's car draw distance for the open city: (r - 1) x 200 + 100 m (0x4baee0).</summary>
        public static float ActorDrawDistance(int r) => (Mathf.Clamp(r, 1, 5) - 1) * 200f + 100f;

        /// <summary>Groups the active renderers under <paramref name="root"/> by the grid cell of their bounds centre.</summary>
        public static CityStreaming Attach(GameObject root, float cellSize, Vector2 origin)
        {
            var s = root.AddComponent<CityStreaming>();
            s.CellSize = cellSize > 1 ? cellSize : 200f;
            s.Origin = origin;
            s.Collect(root);
            return s;
        }

        public void Collect(GameObject root)
        {
            cells.Clear();
            RendererCount = 0;
            foreach (var r in root.GetComponentsInChildren<Renderer>(false))
            {
                var key = CellOf(r.bounds.center);
                if (!cells.TryGetValue(key, out var c)) cells[key] = c = new Cell { Bounds = r.bounds };
                else c.Bounds.Encapsulate(r.bounds);
                c.Renderers.Add(r);
                RendererCount++;
            }
            foreach (var c in cells.Values)
            {
                int n = c.Renderers.Count;
                c.RBounds = new Bounds[n]; c.RSeen = new float[n]; c.RFade = new float[n];
                for (int i = 0; i < n; i++) { c.RBounds[i] = c.Renderers[i].bounds; c.RFade[i] = -100f; }
            }
            ClearObjectFades();
            current = new Vector2Int(int.MinValue, int.MinValue);
            appliedRange = -1;
            first = true;
            FadesStarted = 0;
        }

        bool first = true, fadeUploaded;
        readonly Plane[] planes = new Plane[6];
        static readonly float[] fadeAlpha = new float[121];
        static readonly int FadeOnId = Shader.PropertyToID("_DCFadeOn"), FadeGridId = Shader.PropertyToID("_DCFadeGrid"),
                            FadeWinId = Shader.PropertyToID("_DCFadeWin"), FadeAlphaId = Shader.PropertyToID("_DCFadeAlpha");

        static float Now => Time.timeSinceLevelLoad;
        bool Fades => FadeEnabled && Enabled && appliedRange >= FadeMinRange;

        void StartFade(Cell c)
        {
            if (PerObject && c.RFade != null)
            {
                // a cell entering the window: each of its objects fades on its own timer, all from now
                float now = Now;
                for (int i = 0; i < c.Renderers.Count; i++) { c.RSeen[i] = now; StartObjectFade(c, i); }
                return;
            }
            c.FadeStart = Now; FadesStarted++;
        }

        void StartObjectFade(Cell c, int i)
        {
            if (c.RFade[i] > Now - 1f / FadeRate) return;   // already fading
            c.RFade[i] = Now;
            fadingObjs.Add((c, i));
            FadesStarted++;
            SetObjectFade(c.Renderers[i], 1f);
        }

        void SetObjectFade(Renderer r, float fadeOut)
        {
            if (!r) return;
            mpb ??= new MaterialPropertyBlock();
            r.GetPropertyBlock(mpb);
            mpb.SetFloat(ObjFadeId, fadeOut);
            r.SetPropertyBlock(mpb);
        }

        void ClearObjectFades()
        {
            foreach (var (c, i) in fadingObjs) if (i < c.Renderers.Count) SetObjectFade(c.Renderers[i], 0f);
            fadingObjs.Clear();
        }

        /// <summary>The fade alpha of a cell now (1 = fully drawn).</summary>
        static float AlphaOf(Cell c) => Mathf.Clamp01((Now - c.FadeStart) * FadeRate);

        /// <summary>Applies the window around a position now (the update does this when the camera changes cell).</summary>
        public void Apply(Vector3 eye)
        {
            current = CellOf(eye);
            appliedRange = Range;
            appliedEnabled = Enabled;
            int visible = 0;
            float now = Now;
            foreach (var kv in cells)
            {
                var c = kv.Value;
                bool on = !Enabled || InWindow(kv.Key, current, appliedRange);
                if (on) visible += c.Renderers.Count;
                if (first) c.LastSeen = now;   // what is there when the city opens is drawn at once
                if (on == c.On) continue;
                c.On = on;
                if (!on) c.OffSince = now;
                // entering the window after more than 2 s out of it: fade in (0x40c8a0 restarts at alpha 0)
                else if (Fades && now - c.OffSince > FadeRestart) { StartFade(c); c.LastSeen = now; }
                foreach (var r in c.Renderers) if (r) r.forceRenderingOff = !on;
            }
            first = false;
            VisibleCount = visible;
        }

        /// <summary>
        /// Cells of the window in the camera frustum count as drawn; one coming back into it after more than 2 s
        /// fades in. Then the window cells' alphas go to the shaders while any fade runs.
        /// </summary>
        void UpdateFade(Camera cam)
        {
            int fading = 0;
            float now = Now;
            if (Fades)
            {
                GeometryUtility.CalculateFrustumPlanes(cam, planes);
                foreach (var c in cells.Values)
                {
                    if (!c.On) continue;
                    if (GeometryUtility.TestPlanesAABB(planes, c.Bounds))
                    {
                        if (PerObject && c.RBounds != null)
                        {
                            // the client's fade objects: each renderer back in view after more than 2 s fades in
                            for (int i = 0; i < c.RBounds.Length; i++)
                            {
                                if (!GeometryUtility.TestPlanesAABB(planes, c.RBounds[i])) continue;
                                if (now - c.RSeen[i] > FadeRestart) StartObjectFade(c, i);
                                c.RSeen[i] = now;
                            }
                        }
                        else if (now - c.LastSeen > FadeRestart) StartFade(c);
                        c.LastSeen = now;
                    }
                    if (now - c.FadeStart < 1f / FadeRate) fading++;
                }
            }
            // the objects' own fades: alpha up at 5 a second, the block value back to 0 when done
            for (int k = fadingObjs.Count - 1; k >= 0; k--)
            {
                var (c, i) = fadingObjs[k];
                float a = Mathf.Clamp01((now - c.RFade[i]) * FadeRate);
                if (a >= 1f || !Fades) { SetObjectFade(c.Renderers[i], 0f); fadingObjs.RemoveAt(k); }
                else SetObjectFade(c.Renderers[i], 1f - a);
            }
            if (fadingObjs.Count > 0) fading = Mathf.Max(fading, 1);
            if (ForceFadeAlpha >= 0f && Fades) fading = Mathf.Max(fading, 1);
            FadingCount = fading;
            if (fading == 0)
            {
                if (fadeUploaded) { Shader.SetGlobalFloat(FadeOnId, 0f); fadeUploaded = false; }
                return;
            }
            int r = Mathf.Clamp(appliedRange, 0, 5), w = 2 * r + 1;
            for (int i = 0; i < fadeAlpha.Length; i++) fadeAlpha[i] = 1f;
            for (int z = 0; z < w; z++)
                for (int x = 0; x < w; x++)
                    if (cells.TryGetValue(new Vector2Int(current.x - r + x, current.y - r + z), out var c)) fadeAlpha[z * w + x] = ForceFadeAlpha >= 0f ? ForceFadeAlpha : AlphaOf(c);
            Shader.SetGlobalFloatArray(FadeAlphaId, fadeAlpha);
            Shader.SetGlobalVector(FadeGridId, new Vector4(Origin.x, Origin.y, 1f / CellSize, w));
            Shader.SetGlobalVector(FadeWinId, new Vector4(current.x - r, current.y - r, 0, 0));
            Shader.SetGlobalFloat(FadeOnId, 1f);
            fadeUploaded = true;
        }

        void LateUpdate()
        {
            var cam = Camera.main;
            if (!cam) return;
            var eye = cam.transform.position;
            if (CellOf(eye) != current || Range != appliedRange || Enabled != appliedEnabled) Apply(eye);
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            UpdateFade(cam);
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            FadeUpdateMs = FadeUpdateMs <= 0 ? ms : FadeUpdateMs * 0.95 + ms * 0.05;
        }

        /// <summary>CPU time of the fade's per-frame work (frustum tests + upload), smoothed (tests).</summary>
        public double FadeUpdateMs { get; private set; }

        void OnDisable()
        {
            ClearObjectFades();
            if (fadeUploaded) { Shader.SetGlobalFloat(FadeOnId, 0f); fadeUploaded = false; }
        }

        void OnDestroy()
        {
            foreach (var c in cells.Values)
                if (!c.On) foreach (var r in c.Renderers) if (r) r.forceRenderingOff = false;
        }
    }
}
