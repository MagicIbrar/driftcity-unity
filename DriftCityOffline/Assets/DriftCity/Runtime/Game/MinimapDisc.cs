using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.Game
{
    /// <summary>
    /// The client's minimap disc: a fan of 50 vertices (48 triangles) of radius 103.5 px. Vertex i at angle
    /// a = i * 2pi/50 sits on screen at angle a + theta (theta = the car's bearing when the map rotates) and samples the
    /// map at (u + sin a * R, v + cos a * R * W/H) and the mask at ((sin a, cos a) * 0.5 + 0.5) * 0.9159664.
    /// </summary>
    public sealed class MinimapDisc : MaskableGraphic
    {
        Texture map, mask;
        Vector2 centre;
        float radius = 0.115f, vScale = 1f, theta;

        public Texture MapTexture
        {
            get => map;
            set { map = value; SetMaterialDirty(); SetVerticesDirty(); }
        }

        public Texture MaskTexture
        {
            get => mask;
            set { mask = value; ApplyMask(); }
        }

        /// <summary>Centre texture coordinate (client convention, v down), u radius and rotation (radians).</summary>
        public Vector2 Centre => centre;
        public float UvRadius => radius;
        public float Rotation => theta;

        public override Texture mainTexture => map ? map : s_WhiteTexture;

        protected override void Awake()
        {
            base.Awake();
            var shader = Resources.Load<Shader>("DriftCityMinimapDisc");
            if (!shader) shader = Shader.Find("DriftCity/MinimapDisc");
            if (shader) material = new Material(shader) { name = "Minimap Disc" };
            ApplyMask();
        }

        void ApplyMask()
        {
            var m = material;
            if (m && m != defaultMaterial && m.HasProperty("_MaskTex")) { m.SetTexture("_MaskTex", mask ? mask : Texture2D.whiteTexture); SetMaterialDirty(); }
        }

        public void Set(Vector2 uv, float uvRadius, float vRadiusScale, float rotation)
        {
            if (uv == centre && uvRadius == radius && vRadiusScale == vScale && rotation == theta) return;
            centre = uv; radius = uvRadius; vScale = vRadiusScale; theta = rotation;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            var rect = rectTransform.rect;
            float r = rect.width * 0.5f;
            var mid = rect.center;
            Color32 c = color;
            const int n = CityMapInfo.DiscVertices;
            for (int i = 0; i < n; i++)
            {
                float a = i * (2f * Mathf.PI / n);
                float sa = Mathf.Sin(a), ca = Mathf.Cos(a);
                float sr = Mathf.Sin(a + theta), cr = Mathf.Cos(a + theta);
                // screen: the client's y grows downward, Unity UI's upward
                var pos = new Vector3(mid.x + sr * r, mid.y - cr * r, 0);
                // DDS rows are uploaded top-down, so Unity v equals the client's (downward) v
                var uv0 = new Vector4(centre.x + sa * radius, centre.y + ca * radius * vScale, 0, 0);
                var uv1 = new Vector4((sa * 0.5f + 0.5f) * CityMapInfo.MaskUvSpan, (ca * 0.5f + 0.5f) * CityMapInfo.MaskUvSpan, 0, 0);
                vh.AddVert(pos, c, uv0, uv1, Vector3.back, new Vector4(1, 0, 0, -1));
            }
            for (int i = 1; i < n - 1; i++) vh.AddTriangle(0, i, i + 1);
        }
    }
}
