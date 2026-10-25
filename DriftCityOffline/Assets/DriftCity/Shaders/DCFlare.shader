// Additive screen-space sprite for the weather's lens flare (init\flare.ini, effects\flare0-4.dds).
Shader "DriftCity/Flare"
{
    Properties { _MainTex ("Flare", 2D) = "white" {} }
    SubShader
    {
        Tags { "Queue"="Overlay" "RenderType"="Transparent" }
        Pass
        {
            Blend One One
            ZWrite Off
            ZTest Always
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
            v2f vert(appdata_full v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.texcoord.xy; o.color = v.color; return o; }
            fixed4 frag(v2f i) : SV_Target { return tex2D(_MainTex, i.uv) * i.color * i.color.a; }
            ENDCG
        }
    }
}
