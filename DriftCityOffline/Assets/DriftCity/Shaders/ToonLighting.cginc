// Shared lighting for Drift City's surface shaders. With the global _DCToon at 0 it is plain Lambert
// (the original fixed-function look); at 1 it is a cel-shaded arcade look: two hard light bands,
// banded shadows, a bright rim on the lit side and cool tinted shade.
#ifndef DC_TOON_LIGHTING
#define DC_TOON_LIGHTING

#include "UnityCG.cginc"

float _DCToon;            // 0 = original lighting, 1 = toon
float _DCToonShade;       // brightness of the shaded band (0..1)
float _DCToonRim;         // rim light strength
fixed4 _DCToonShadeTint;  // colour pushed into shaded areas

half4 DCLight(half3 albedo, half alpha, half3 normal, half3 lightDir, half3 viewDir, half atten, half unlit)
{
    half ndl = dot(normal, lightDir);
    half lambert = max(0, ndl) * atten;
    if (_DCToon < 0.5)
        return half4(albedo * _LightColor0.rgb * lambert * (1 - unlit), alpha);

    // two crisp bands with a thin soft edge, shadows folded into the dark band
    half lit = smoothstep(0.02, 0.10, ndl) * smoothstep(0.35, 0.55, atten);
    half3 shade = lerp(_DCToonShadeTint.rgb * _DCToonShade, 1, lit);
    // rim: a bright fresnel band on the side facing the light
    half fres = 1 - saturate(dot(normal, viewDir));
    half rim = smoothstep(0.62, 0.72, fres) * saturate(ndl + 0.3) * _DCToonRim;
    half3 c = albedo * _LightColor0.rgb * shade + _LightColor0.rgb * rim * 0.35;
    return half4(c * (1 - unlit), alpha);
}

#endif
