using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.UI
{
    /// <summary>
    /// Draws part of a texture as a pie wedge around a pivot inside the rect, like a RawImage with a radial
    /// fill whose centre doesn't have to be the rect's centre (the speedometer's booster arc).
    /// </summary>
    [ExecuteAlways, RequireComponent(typeof(CanvasRenderer))]
    public sealed class ArcFillImage : MaskableGraphic
    {
        public Texture Texture;
        public Rect UvRect = new Rect(0, 0, 1, 1);
        /// <summary>Pivot in pixels from the rect's top-left corner.</summary>
        public Vector2 CenterPx;
        /// <summary>Angles in degrees, counter-clockwise from +x; the fill runs from Start towards End.</summary>
        public float StartDeg, EndDeg;
        [Range(0, 1)] public float Fill = 1f;

        public override Texture mainTexture => Texture ? Texture : s_WhiteTexture;

        public void SetFill(float f)
        {
            f = Mathf.Clamp01(f);
            if (Mathf.Abs(f - Fill) < 0.002f) return;
            Fill = f;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (Fill <= 0f) return;
            var r = rectTransform.rect;
            var c = new Vector2(r.xMin + CenterPx.x, r.yMax - CenterPx.y);
            float end = Mathf.Lerp(StartDeg, EndDeg, Fill);
            int steps = Mathf.Max(1, Mathf.CeilToInt(Mathf.Abs(end - StartDeg)));
            vh.AddVert(Vert(c, r));
            for (int i = 0; i <= steps; i++)
            {
                float a = Mathf.Lerp(StartDeg, end, i / (float)steps) * Mathf.Deg2Rad;
                vh.AddVert(Vert(EdgePoint(c, new Vector2(Mathf.Cos(a), Mathf.Sin(a)), r), r));
                if (i > 0) vh.AddTriangle(0, i, i + 1);
            }
        }

        UIVertex Vert(Vector2 p, Rect r)
        {
            var v = UIVertex.simpleVert;
            v.position = p;
            v.color = color;
            // same corner mapping as RawImage (bottom-left = uvRect.min), which also holds for flipped rects
            v.uv0 = new Vector2(Mathf.Lerp(UvRect.xMin, UvRect.xMax, (p.x - r.xMin) / r.width), Mathf.Lerp(UvRect.yMin, UvRect.yMax, (p.y - r.yMin) / r.height));
            return v;
        }

        /// <summary>Where a ray from c (inside r) leaves the rect.</summary>
        static Vector2 EdgePoint(Vector2 c, Vector2 d, Rect r)
        {
            float t = float.MaxValue;
            if (d.x > 1e-5f) t = Mathf.Min(t, (r.xMax - c.x) / d.x);
            if (d.x < -1e-5f) t = Mathf.Min(t, (r.xMin - c.x) / d.x);
            if (d.y > 1e-5f) t = Mathf.Min(t, (r.yMax - c.y) / d.y);
            if (d.y < -1e-5f) t = Mathf.Min(t, (r.yMin - c.y) / d.y);
            return c + d * t;
        }
    }
}
