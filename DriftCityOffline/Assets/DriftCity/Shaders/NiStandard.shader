// Gamebryo's fixed-function material, as the game draws every mesh without a shader of its own:
// per-vertex lighting  emissive + La*Ma + Ld*Md*max(0, N.L)  (clamped like D3D vertex colors), where
// NiVertexColorProperty can feed vertex colors in as emissive or as ambient+diffuse; then base map
// modulate, dark map multiply and glow map add. World meshes use the ground light, vehicles the car light.
Shader "DriftCity/NiStandard"
{
    Properties
    {
        _MainTex ("Base", 2D) = "white" {}
        _Color ("Diffuse", Color) = (1,1,1,1)
        _AmbientColor ("Ambient", Color) = (1,1,1,1)
        _DarkTex ("Dark (lightmap)", 2D) = "white" {}
        _DarkUV ("Dark map uses UV2", Float) = 0
        _GlowTex ("Glow", 2D) = "black" {}
        _EmissionColor ("Emissive", Color) = (0,0,0,0)
        _UseVertexColor ("Use vertex color", Float) = 0
        _VertexMode ("Vertex colors: 1 emissive, 2 ambient+diffuse", Float) = 2
        _Unlit ("Unlit (emissive-only lighting mode)", Float) = 0
        _CarLight ("Lit by the car light", Float) = 0
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
            #pragma target 3.0
            #include "DCOriginal.cginc"
            #include "AutoLight.cginc"

            sampler2D _MainTex, _DarkTex, _GlowTex;
            float4 _MainTex_ST;
            fixed4 _Color, _AmbientColor, _EmissionColor;
            float _DarkUV, _UseVertexColor, _VertexMode, _Unlit, _CarLight, _Cutoff, _DstBlend;

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; float2 uv2 : TEXCOORD1; fixed4 color : COLOR; };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 uvDark : TEXCOORD1;
                fixed4 lit : COLOR0;
                SHADOW_COORDS(2)
                UNITY_FOG_COORDS(3)
                float fade : TEXCOORD4;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.uvDark = lerp(v.uv, v.uv2, _DarkUV);
                fixed4 vc = v.color;
                bool useVc = _UseVertexColor > 0.5;
                bool vcAmbDiff = useVc && _VertexMode > 1.5;
                fixed4 matDiff = vcAmbDiff ? vc : _Color;
                fixed3 matAmb = vcAmbDiff ? vc.rgb : _AmbientColor.rgb;
                fixed3 matEmis = (useVc && _VertexMode > 0.5 && _VertexMode < 1.5) ? vc.rgb : _EmissionColor.rgb;
                DCLight l = DCGetLight(_CarLight);
                float3 n = normalize(UnityObjectToWorldNormal(v.normal));
                float ndl = max(0, dot(n, -l.dir));
                fixed3 lit = saturate(matEmis + l.amb * matAmb + l.diff * matDiff.rgb * ndl);
                // emissive-only lighting: baked vertex colors (or full brightness) pass straight through
                if (_Unlit > 0.5) lit = useVc ? vc.rgb : fixed3(1, 1, 1);
                o.lit = fixed4(lit, vcAmbDiff ? vc.a * _Color.a : _Color.a);
                TRANSFER_SHADOW(o);
                UNITY_TRANSFER_FOG(o, o.pos);
                o.fade = _CarLight > 0.5 ? 1 : DCFadeAlpha(mul(unity_ObjectToWorld, v.vertex).xyz);   // city fade-in (CityStreaming)
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv) * i.lit;
                clip(c.a - _Cutoff);
                DC_FADE_CLIP(i.fade, i.pos);
                c.rgb *= tex2D(_DarkTex, i.uvDark).rgb;
                c.rgb += tex2D(_GlowTex, i.uv).rgb;
                float shade = lerp(1 - lerp(_DCShadowDark, _DCShadowAffect, _CarLight), 1, SHADOW_ATTENUATION(i));
                c.rgb *= lerp(shade, 1, _Unlit);
                // opaque meshes cover what is behind them (car previews are composited by alpha)
                if (_DstBlend == 0) c.a = 1;
                UNITY_APPLY_FOG(i.fogCoord, c);
                return c;
            }
            ENDCG
        }
    }
    FallBack "Legacy Shaders/Transparent/Cutout/VertexLit"
}
