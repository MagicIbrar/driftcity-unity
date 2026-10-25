// Port of shader\actionspecularband.fx ("ActionSpecularBand"), the additive gloss strips (gloss01/gloss02) on car
// bodies: the horizontal model-space normal against the horizontal direction to the camera, raised to the
// mesh's "Reflection" power, picks a column of gloss.bmp; added on top with blend One One.
Shader "DriftCity/SpecularBand"
{
    Properties
    {
        _MainTex ("Gloss", 2D) = "white" {}
        _Reflection ("Reflection power", Float) = 100
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            Blend One One
            ZWrite Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "DCOriginal.cginc"

            sampler2D _MainTex;
            float _Reflection;

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float4 nc : TEXCOORD1; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                // the effect works in Gamebryo model space (z up), where "horizontal" is x/y; here that is x/z
                o.nc.xy = normalize(float3(v.normal.x, v.normal.z, 0)).xy;
                float3 camModel = mul(unity_WorldToObject, float4(_WorldSpaceCameraPos, 1)).xyz;
                o.nc.zw = (camModel - v.vertex.xyz).xz;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 H = normalize(i.nc.zw);
                float2 N = normalize(i.nc.xy);
                float u = max(0.1, pow(max(0, dot(N, H)), _Reflection));
                fixed4 spec = tex2D(_MainTex, float2(u, i.uv.y));
                spec.rgb *= _DCCarSpec.r;
                return fixed4(spec.rgb, u);
            }
            ENDCG
        }
    }
}
