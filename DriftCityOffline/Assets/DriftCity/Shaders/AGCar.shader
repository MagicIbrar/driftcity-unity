// Port of the game's car body effect (shader\agc_shader.fx, "ActionGame Car effect shader - jc 2008"), used on
// every vehicle mesh whose NIF names the ActionGameCartoonFX shader. The texture's alpha picks between the
// texture and the paint (MaterialColor): shaded = paint*(1-a) + tex*a.
//  _DCCarShader 0  ActionGameCartoonFX / ActionGameRealBody: Gouraud, shaded*(La + Ld*N.L)
//  _DCCarShader 1  AGBody_cube_phong (the default: CarShader=2, CarSpecular on): half-Lambert
//                  shaded*0.5*(N.L+1) plus a phong highlight (exponent 512) and the cube environment map
// "*Ref" (the projected shadow map) becomes Unity's shadow attenuation scaled by the weather's shadowaffect.
Shader "DriftCity/AGCar"
{
    Properties
    {
        _MainTex ("Decal", 2D) = "white" {}
        _Color ("Material diffuse (alpha)", Color) = (1,1,1,1)
        _MaterialColor ("Paint (MaterialColor)", Color) = (1,1,1,1)
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
            #pragma target 3.0
            #include "DCOriginal.cginc"
            #include "AutoLight.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color, _MaterialColor;
            float _DCCarShader;

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normal : TEXCOORD1;
                float3 worldPos : TEXCOORD2;
                float diffIntense : TEXCOORD3;
                SHADOW_COORDS(4)
                UNITY_FOG_COORDS(5)
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.normal = UnityObjectToWorldNormal(v.normal);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                // the Gouraud path takes N.L per vertex from the unnormalised world normal, as VsGouraudBody does
                o.diffIntense = max(0, dot(o.normal, -normalize(_DCCarDir)));
                TRANSFER_SHADOW(o);
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 decal = tex2D(_MainTex, i.uv);
                float3 shaded = _MaterialColor.rgb * (1 - decal.a) + decal.rgb * decal.a;
                float3 L = normalize(_DCCarDir);
                float3 N = normalize(i.normal);
                float3 V = normalize(i.worldPos - _WorldSpaceCameraPos);   // camDir(): camera to surface
                float3 color;
                if (_DCCarShader < 0.5)
                {
                    color = shaded * (_DCCarAmb + i.diffIntense * _DCCarDiff);
                }
                else
                {
                    float mixer = 0.5 * (dot(-L, N) + 1);
                    color = shaded * mixer;
                    float phong = dot(normalize(reflect(N, L)), V);
                    if (phong > 0) color += pow(phong, 512) * _DCCarSpec;
                    float3 c = -V;
                    float3 r = 2 * dot(c, N) * N - c;
                    color += texCUBE(_DCEnvCube, DCToGamebryo(r)).rgb * _DCEnvStrength;
                }
                color *= lerp(1 - _DCShadowAffect, 1, SHADOW_ATTENUATION(i));
                fixed4 col = fixed4(color, _DCCarShader < 0.5 ? _Color.a : 1);
                UNITY_APPLY_FOG(i.fogCoord, col);
                return col;
            }
            ENDCG
        }
    }
    FallBack "Legacy Shaders/VertexLit"
}
