// Lighting shared by the ports of Drift City's own shaders (DriftCity_RE/shaders): the game lights the world
// with a "ground" directional light and every vehicle with a separate "car" light, both set from
// init/weather.ini (or init/light.ini in the garage and shop screens). OriginalLook.cs sets these globals.
#ifndef DC_ORIGINAL_INCLUDED
#define DC_ORIGINAL_INCLUDED

#include "UnityCG.cginc"

// direction the light travels (Unity world space), ambient, diffuse and specular, already times the dimmer
float3 _DCGroundDir, _DCGroundAmb, _DCGroundDiff, _DCGroundSpec;
float3 _DCCarDir, _DCCarAmb, _DCCarDiff, _DCCarSpec;
float _DCShadowDark;     // weather "shadowdark": how dark the world gets in shadow
float _DCShadowAffect;   // weather "shadowaffect": how much shadows darken cars
float _DCEnvStrength;    // weather "envmapstrength"
samplerCUBE _DCEnvCube;  // vehicle/texture/{left,right,front,back,up,down}.dds
sampler2D _DCToonRampVehicle, _DCToonRampObject;

struct DCLight { float3 dir; float3 amb; float3 diff; float3 spec; };

DCLight DCGetLight(float carSet)
{
    DCLight l;
    l.dir = normalize(lerp(_DCGroundDir, _DCCarDir, carSet));
    l.amb = lerp(_DCGroundAmb, _DCCarAmb, carSet);
    l.diff = lerp(_DCGroundDiff, _DCCarDiff, carSet);
    l.spec = lerp(_DCGroundSpec, _DCCarSpec, carSet);
    return l;
}

// Unity world (y up) to Gamebryo world (z up): the space the game's cube map is looked up in
float3 DCToGamebryo(float3 v) { return float3(v.x, v.z, v.y); }

// The client's fade-in of city objects that come into view (VisibleRange > 2; 0x41a610 / 0x40c8a0: alpha 0 -> 1 at
// 5 per second). CityStreaming.cs sets these globals while a fade runs: the alpha of each 200 m cell of the
// (2r+1)^2 window, looked up by world position; the pixel shader dithers by it (a screen-door alpha, no sorting).
float _DCFadeOn;              // 1 while any cell fades
float4 _DCFadeGrid;           // grid origin x, z (Unity), 1 / cell size, window width in cells
float4 _DCFadeWin;            // window's first cell x, z
float _DCFadeAlpha[121];      // alpha per window cell, row-major
float _DCObjFadeOut;          // per renderer (property block while it fades on its own): 1 - its alpha; 0 = drawn

float DCFadeAlpha(float3 worldPos)
{
    if (_DCFadeOn < 0.5) return 1;
    float obj = 1 - _DCObjFadeOut;
    float2 c = floor((worldPos.xz - _DCFadeGrid.xy) * _DCFadeGrid.z) - _DCFadeWin.xy;
    if (c.x < 0 || c.y < 0 || c.x >= _DCFadeGrid.w || c.y >= _DCFadeGrid.w) return obj;
    return min(obj, _DCFadeAlpha[(int)(c.y * _DCFadeGrid.w + c.x)]);
}

// discards the pixel when it falls under the fade alpha's dither threshold (interleaved gradient noise)
#define DC_FADE_CLIP(fade, screenPos) if (fade < 0.999) clip(fade - frac(52.9829189 * frac(dot(screenPos.xy, float2(0.06711056, 0.00583715)))) - 0.001)

#endif
