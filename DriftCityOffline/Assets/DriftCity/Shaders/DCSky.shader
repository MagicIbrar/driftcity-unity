// The weather's sky dome (effects\skydome.nif): its texture drawn unlit behind everything, without fog.
Shader "DriftCity/Sky"
{
    Properties { _MainTex ("Sky", 2D) = "white" {} }
    SubShader
    {
        Tags { "Queue"="Background+10" "RenderType"="Background" }
        Pass
        {
            ZWrite Off
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _MainTex_ST;
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert(appdata_base v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = TRANSFORM_TEX(v.texcoord, _MainTex); return o; }
            fixed4 frag(v2f i) : SV_Target { return fixed4(tex2D(_MainTex, i.uv).rgb, 1); }
            ENDCG
        }
    }
}
