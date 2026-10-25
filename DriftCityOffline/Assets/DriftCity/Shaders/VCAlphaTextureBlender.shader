// Port of the "VCAlphaTextureBlender" technique in shader\agt_shader.fx (terrain and road blocks): two ground
// textures, both sampled with the first UV set, blended by vertex alpha and lit per vertex by the ground light
// (La + Ld*N.L). The vertex RGB and the DetailIndex texture are not read by the game's shader.
Shader "DriftCity/VCAlphaTextureBlender"
{
    Properties
    {
        _Tex1 ("Texture 1 (vertex alpha = 1)", 2D) = "white" {}
        _Tex2 ("Texture 2 (vertex alpha = 0)", 2D) = "white" {}
        _Detail ("Detail (unused by the game)", 2D) = "gray" {}
        _Tex1UV ("Texture 1 uses UV2 (unused by the game)", Float) = 0
        _Tex2UV ("Texture 2 uses UV2 (unused by the game)", Float) = 0
        _DetailUV ("Detail uses UV2", Float) = 1
        _HasDetail ("Has detail", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdbase
            #pragma multi_compile_fog
            #include "DCOriginal.cginc"
            #include "AutoLight.cginc"

            sampler2D _Tex1, _Tex2;

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                fixed blend : TEXCOORD1;
                fixed3 lit : COLOR0;
                SHADOW_COORDS(2)
                UNITY_FOG_COORDS(3)
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.blend = v.color.a;
                float3 n = UnityObjectToWorldNormal(v.normal);
                o.lit = _DCGroundDiff * max(0, dot(n, -normalize(_DCGroundDir))) + _DCGroundAmb;
                TRANSFER_SHADOW(o);
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed3 c = (tex2D(_Tex1, i.uv).rgb * i.blend + tex2D(_Tex2, i.uv).rgb * (1 - i.blend)) * i.lit;
                c *= lerp(1 - _DCShadowDark, 1, SHADOW_ATTENUATION(i));
                fixed4 col = fixed4(c, 1);
                UNITY_APPLY_FOG(i.fogCoord, col);
                return col;
            }
            ENDCG
        }
    }
    FallBack "Legacy Shaders/VertexLit"
}
