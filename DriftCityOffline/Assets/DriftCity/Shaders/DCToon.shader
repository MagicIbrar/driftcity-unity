// Port of Gamebryo's ToonShading sample shader (shader\toonshading.nsb, dx9\toonshading.vsh/.psh) that the game
// puts on lamps and a few props: luminance of max(0, N.L) looks up a 1D toon ramp,
// color = (ramp*Ld + La) * base, alpha 1. Vehicles use ToonRamp_Vehicle.bmp and the car light; world
// objects ToonRamp_Object.bmp and the ground light.
Shader "DriftCity/Toon"
{
    Properties
    {
        _MainTex ("Base", 2D) = "white" {}
        _CarLight ("Lit by the car light", Float) = 0
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            Tags { "LightMode"="ForwardBase" }
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
            float _CarLight;

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float lum : TEXCOORD1; SHADOW_COORDS(2) UNITY_FOG_COORDS(3) float fade : TEXCOORD4; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                DCLight l = DCGetLight(_CarLight);
                float ndl = max(0, dot(UnityObjectToWorldNormal(v.normal), -l.dir));
                o.lum = dot(float3(ndl, ndl, ndl), float3(0.299, 0.587, 0.114));
                TRANSFER_SHADOW(o);
                UNITY_TRANSFER_FOG(o, o.pos);
                o.fade = _CarLight > 0.5 ? 1 : DCFadeAlpha(mul(unity_ObjectToWorld, v.vertex).xyz);   // city fade-in (CityStreaming)
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                DC_FADE_CLIP(i.fade, i.pos);
                DCLight l = DCGetLight(_CarLight);
                float2 rampUV = float2(saturate(i.lum), 0.5);
                fixed3 ramp = lerp(tex2D(_DCToonRampObject, rampUV).rgb, tex2D(_DCToonRampVehicle, rampUV).rgb, _CarLight);
                fixed3 c = (ramp * l.diff + l.amb) * tex2D(_MainTex, i.uv).rgb;
                c *= lerp(1 - lerp(_DCShadowDark, _DCShadowAffect, _CarLight), 1, SHADOW_ATTENUATION(i));
                fixed4 col = fixed4(c, 1);
                UNITY_APPLY_FOG(i.fogCoord, col);
                return col;
            }
            ENDCG
        }
    }
    FallBack "Legacy Shaders/VertexLit"
}
