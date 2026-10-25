// Cartoon sky for the arcade look: a clean vertical gradient, a hard-edged sun and puffy flat clouds.
Shader "DriftCity/ToonSky"
{
    Properties
    {
        _Top ("Top", Color) = (0.20, 0.50, 0.95, 1)
        _Horizon ("Horizon", Color) = (0.70, 0.88, 1.00, 1)
        _Ground ("Below horizon", Color) = (0.62, 0.74, 0.86, 1)
        _SunDir ("Sun direction", Vector) = (0.4, 0.55, 0.6, 0)
        _SunColor ("Sun", Color) = (1, 0.96, 0.8, 1)
        _CloudColor ("Clouds", Color) = (1, 1, 1, 1)
    }
    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" }
        Cull Off ZWrite Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Top, _Horizon, _Ground, _SunColor, _CloudColor;
            float4 _SunDir;

            struct v2f { float4 pos : SV_POSITION; float3 dir : TEXCOORD0; };
            v2f vert(appdata_base v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.dir = v.vertex.xyz; return o; }

            float Noise(float2 p)
            {
                float2 i = floor(p), f = frac(p);
                float a = frac(sin(dot(i, float2(12.9898, 78.233))) * 43758.5);
                float b = frac(sin(dot(i + float2(1, 0), float2(12.9898, 78.233))) * 43758.5);
                float c = frac(sin(dot(i + float2(0, 1), float2(12.9898, 78.233))) * 43758.5);
                float d = frac(sin(dot(i + float2(1, 1), float2(12.9898, 78.233))) * 43758.5);
                f = f * f * (3 - 2 * f);
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 d = normalize(i.dir);
                float h = d.y;
                fixed3 col = h > 0 ? lerp(_Horizon.rgb, _Top.rgb, pow(saturate(h), 0.6)) : _Ground.rgb;
                // flat cartoon clouds: noise on a plane above the camera, cut hard
                if (h > 0.02)
                {
                    float2 p = d.xz / (h + 0.15) * 1.6 + float2(_Time.x * 0.6, 0);
                    float n = Noise(p) * 0.6 + Noise(p * 2.3) * 0.3 + Noise(p * 5.1) * 0.1;
                    float cloud = smoothstep(0.58, 0.62, n) * smoothstep(0.02, 0.2, h);
                    col = lerp(col, _CloudColor.rgb, cloud * 0.95);
                }
                float sun = smoothstep(0.9975, 0.998, dot(d, normalize(_SunDir.xyz)));
                col = lerp(col, _SunColor.rgb, sun);
                return fixed4(col, 1);
            }
            ENDCG
        }
    }
}
