// Full-screen pass for the arcade toon look: ink outlines from depth/normal edges, punchy colour,
// a light vignette and speed lines at high speed / while boosting.
Shader "Hidden/DriftCity/ArcadePost"
{
    Properties { _MainTex ("Screen", 2D) = "white" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            sampler2D _CameraDepthNormalsTexture;
            float _Saturation, _Contrast, _EdgeStrength, _EdgeFar, _Speed, _Vignette, _InkWidth;
            fixed4 _InkColor;

            void Sample(float2 uv, out float depth, out float3 normal)
            {
                DecodeDepthNormal(tex2D(_CameraDepthNormalsTexture, uv), depth, normal);
            }

            float Hash(float n) { return frac(sin(n) * 43758.5453); }

            fixed4 frag(v2f_img i) : SV_Target
            {
                fixed4 col = tex2D(_MainTex, i.uv);
                float2 px = _MainTex_TexelSize.xy * _InkWidth;

                // Roberts cross on depth and normals
                float d0, d1, d2, d3; float3 n0, n1, n2, n3;
                Sample(i.uv + px * float2(-1, -1), d0, n0);
                Sample(i.uv + px * float2( 1,  1), d1, n1);
                Sample(i.uv + px * float2( 1, -1), d2, n2);
                Sample(i.uv + px * float2(-1,  1), d3, n3);
                float dc = (d0 + d1 + d2 + d3) * 0.25;
                float depthEdge = (abs(d0 - d1) + abs(d2 - d3)) / max(dc, 1e-4);
                float normalEdge = length(n0 - n1) + length(n2 - n3);
                float edge = saturate(smoothstep(0.08, 0.2, depthEdge) + smoothstep(0.45, 0.8, normalEdge));
                // the sky (depth ~1) has no outlines; far edges fade so the city doesn't turn grey
                edge *= step(dc, 0.995) * (1 - smoothstep(0.0, _EdgeFar, dc));
                col.rgb = lerp(col.rgb, _InkColor.rgb, edge * _EdgeStrength);

                // colour: saturation and contrast around mid grey
                float lum = dot(col.rgb, float3(0.299, 0.587, 0.114));
                col.rgb = lerp(lum.xxx, col.rgb, _Saturation);
                col.rgb = (col.rgb - 0.5) * _Contrast + 0.5;
                col.rgb = col.rgb > 0.8 ? 0.8 + (1 - exp(-(col.rgb - 0.8) * 3.0)) * 0.2 : col.rgb;
                col.rgb = saturate(col.rgb);

                // speed lines: thin radial streaks towards the screen edge
                float2 c = i.uv - 0.5; c.x *= _ScreenParams.x / _ScreenParams.y;
                float r = length(c);
                float ang = atan2(c.y, c.x);
                float slot = floor((ang + 3.14159) * 70.0);
                float flick = floor(_Time.y * 14.0);
                float streak = step(0.9, Hash(slot * 7.13 + flick)) * smoothstep(0.4, 0.8, r);
                col.rgb = lerp(col.rgb, 1, streak * _Speed * 0.4);

                col.rgb *= 1 - _Vignette * pow(saturate(r * 1.25), 2.6);
                return col;
            }
            ENDCG
        }
    }
}
