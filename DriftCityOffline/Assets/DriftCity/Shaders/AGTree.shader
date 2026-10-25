// Port of shader\actiongametree.fx ("ActionGameTree"), the shader on most city buildings and props:
// base * (Ld*0.6 + La) from the ground light, with no N.L term at all, alpha from the base map.
Shader "DriftCity/AGTree"
{
    Properties
    {
        _MainTex ("Base", 2D) = "white" {}
        _Cutoff ("Alpha test threshold", Range(0,1)) = 0
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 0
        [Enum(Off,0,On,1)] _ZWrite ("ZWrite", Float) = 1
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]
            Cull [_Cull]

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdbase
            #pragma multi_compile_fog
            #include "DCOriginal.cginc"
            #include "AutoLight.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float _Cutoff, _DstBlend;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; SHADOW_COORDS(1) UNITY_FOG_COORDS(2) float fade : TEXCOORD3; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                TRANSFER_SHADOW(o);
                UNITY_TRANSFER_FOG(o, o.pos);
                o.fade = DCFadeAlpha(mul(unity_ObjectToWorld, v.vertex).xyz);   // city fade-in (CityStreaming)
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 base = tex2D(_MainTex, i.uv);
                clip(base.a - _Cutoff);
                DC_FADE_CLIP(i.fade, i.pos);
                fixed4 c = fixed4(base.rgb * (_DCGroundDiff * 0.6 + _DCGroundAmb), base.a);
                c.rgb *= lerp(1 - _DCShadowDark, 1, SHADOW_ATTENUATION(i));
                if (_DstBlend == 0) c.a = 1;
                UNITY_APPLY_FOG(i.fogCoord, c);
                return c;
            }
            ENDCG
        }
    }
    FallBack "Legacy Shaders/Transparent/Cutout/VertexLit"
}
