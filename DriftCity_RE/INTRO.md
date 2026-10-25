# New-account story intro (game\intro\newintro.nif)

How DriftCity.exe plays the story intro shown before a new driver registers a licence, and how the remake
reproduces it (`DriftCityOffline/Assets/DriftCity/Runtime/Game/IntroScene.cs`, preview tool
`Assets/DriftCity/Editor/DriftCityIntroShots.cs`). Addresses are VAs (image base 0x400000).
"Exact" = read from code/data; "inferred" = reasoned, not proven.

## The intro state object

| What | Address | Notes |
|---|---|---|
| strings `newintro.nif` / `Game/Intro/` | 0x87d31c / 0x87d32c | file offsets 0x47c31c / 0x47c32c; only xref 0x752a47/0x752a5b |
| strings `Intro_01` .. `Intro_12` | 0x87d3bc .. 0x87d338 | globaltext keys, only xrefs in 0x7531f0 |
| vtable | 0x87d2e0 | slot 0 dtor 0x7530a0, +0x14 Enter 0x753470, +0x18 Leave 0x7530c0, +0x24 OnMessage 0x752670, +0x28 Update 0x752910, +0x30 Render 0x752990, +0x34 Draw2D 0x7530f0 |
| constructor | 0x752720 | object 0xf8 bytes, created first in the state table builder 0x770000 (`[esi+0]`) |
| destructor body | 0x752840 | |

Object fields: +0x0c scene root (NiNode), +0x10 NiCamera, +0x14 begin time, +0x18 end time, +0x1c elapsed,
+0x20 load ok, +0x24 saved renderer background colour, +0x38 subtitle fade time, +0x3c "first frame" flag,
+0x50 culling process, +0xe4/+0xec subtitle entry array/count.

### Enter (0x753470) - exact
1. `Load` (0x7529b0):
   * `NiStream::Load("Game/Intro/newintro.nif")` (mipmap skip level 0 while loading), root = object 0;
   * `SetTranslate(0, 100, 0)`, `Update(0)` + effects/properties (0x41aa80);
   * time range: 0x4cbbc0 walks the scene (controllers of every object, its properties, children, effects) and keeps the
     lowest `GetBeginKeyTime` and highest `GetEndKeyTime`, starting from 0/0. For newintro.nif: **0 .. 63.4 s**
     (the last controller is the `sr_Plane01` alpha, 52.067..63.4).
   * camera: a new NiCamera, rotation rows (0,0,1),(1,0,0),(0,1,0) -> looks along world +Y, up +Z, right +X, at the
     origin; `NiFrustum(-51.2, 51.2, 38.4, -38.4, near 0, far 512, ortho=true)`.
   * viewport: if screen w/h > 4/3, `left = (w/h * 0.75 - 1) * 0.5`, `right = 1 - left`, top 1, bottom 0. (At 16:9
     that is the middle 2/3 of the screen, i.e. the 4:3 frustum is drawn a little narrower than 4:3; only around 4:3
     is it exact.) Narrower screens keep the full viewport (stretched).
2. `+0x3c = 1` (first update does not advance time).
3. Music: only for country codes 4..10 it calls `SoundManager(0x8d79c8)->Play(60001, 0)` (0x52b030). BGM ids are
   60000 + track number of `Data\audio\bgm\music.txt`, so 60001 = `DriftCity_title.mp3` ("I Can't Stop").
   **The country code is always 1 (US)**: 0x8d2e10 is written only by the constructor at 0x4c7da7 (`= 1`) and by
   0x4cce50, whose two callers pass the current value back. So the remaster client starts no music for the intro;
   whatever was playing before keeps playing.
4. Subtitle table (0x7531f0), then saves the renderer background colour and sets it to (0,0,0,1);
   `0x4c7a80(0x12f)` is a Korean-only (code 0) logging call, no effect for code 1.

### Update (0x752910) - exact
`scene->Update(begin + elapsed)`; unless it is the first frame, `elapsed += frame time` (`[0x8bd764]+0xc`).
When `begin + elapsed > end` the game state changes: to state 2 for codes 4..10, else **state 3** (0x4386b0/0x438790).
No cycling: all 111 controllers are flags 0xc (active, CLAMP), so the scene holds its last pose.

### Skip (OnMessage 0x752670) - exact
`WM_KEYUP` (0x101) with `VK_ESCAPE` (0x1b), `VK_SPACE` (0x20) or `VK_RETURN` (0x0d) -> same state change as the end
(state 3 for code 1). Mouse clicks do nothing.

### Render (0x752990) - exact
`NiDrawScene(camera, scene, culler, 0)`. The renderer's global sorter is a plain `NiAlphaAccumulator`
(constructed 0x42f7c5, vtable 0x85cd18, only the destructor is the game's), so alpha-blended meshes draw back to front.
There is no NiLight in the NIF: lit materials get their emissive colour only.

### Subtitles (0x7531f0 builds, 0x753100 adds, 0x752e70 draws) - exact
Each entry: text = `GetText(key)` (0x7d2670, globaltext.xlt, `Value_US` for code 1) converted to wide chars,
start, duration, `y = screen height - 768 + line`. Fade time `+0x38 = 1.0`.

| key | start s | duration s | line (code 1) |
|---|---|---|---|
| Intro_01 | 16.7 | 4.0 | 590 |
| Intro_02 | 19.9 | 4.0 | 590 |
| Intro_03 | 21.9 | 4.0 | 635 (655 for codes 4, 7-10) |
| Intro_04 | 26.0 | 4.0 | 590 |
| Intro_05 | 31.0 | 4.0 | 590 |
| Intro_06 | 35.5 | 3.5 | 590 |
| Intro_07 | 39.0 | 4.0 | 590 |
| Intro_08 | 41.2 | 5.5 | 635 |
| Intro_09 | 47.5 | 5.5 | 590 |
| Intro_10 | 47.5 | 5.5 | 635 |
| Intro_11 | 53.5 | 7.0 | 590 |
| Intro_12 | 53.5 | 7.0 | 635 |

Draw (0x752e70, called from Draw2D 0x7530f0 with the elapsed time): font by country code (jump table 0x753078):
code 1 -> **"Calibri", height 28, weight 400** (0x52cef0 -> 0x434b60 -> 0x434860:
`D3DXCreateFontW(dev, -28, 0, 400, 1, 0, charset, ...)`, i.e. 28 px character height); code 2 Ms pGothic 30, 3 MingLiU 30,
4 Calibri 26, 5/6 Tahoma 22, 7-10 default face 26, others default face 30. An entry is drawn while
`start <= elapsed <= start + duration` with `ID3DXFont::DrawTextW(sprite, text, -1, {0, y, screenW, y},
DT_CENTER|DT_WORDBREAK|DT_NOCLIP (0x111), colour)`: white; alpha = `int(since/1.0*255)` during the first second,
`int(left/1.0*255)` during the last, else 0xFFFFFFFF. No shadow or outline.
English lines (localize\globaltext.xlt and data\localize\globaltext.xlt agree):
"On an island somewhere in the middle of the Northern Pacific Ocean, Mittron was discovered." ... "have journeyed to
Mittron Island to earn money and fame."

## newintro.nif content (data, exact)
726 blocks, all parsed (the file is zero-padded to 0x28000 after the footer at 0x264c8). 68 textured quads/text meshes
in the plane y ~ 0 (offsets -1..+42 in depth), all alpha-blended (SrcAlpha/InvSrcAlpha), z-test on, z-write off.
Controllers: 57 NiAlphaController (material alpha), 50 NiTransformController (mostly linear translation; Euler rotation
on the globe `GeoSphere01`, `Circle01`, `ingong`, `sr_Plane01`, `pattern_02`; uniform scale keys), 4
NiGeomMorpherController (relative, 2 targets: `Plane07`/`Plane08`/`black_bar01`/`black_bar02` black curtains widen
from 0.6 to ~52/45 units at 31.7-32.7 s / 16.4-17.0 s). Panels are hidden by moving them to z ~ -75 (outside the
±38.4 view). Textured materials are emissive white (full brightness); "black" covers are emissive black, diffuse
white, no lights -> black. No NiCamera, no texture-transform or flip controllers, no sound.
Timeline (from the keys): 0-9 s warning screen (ws3/warning/text.dds), 8.7-12 s Hangame/NHN logo planes slide in (their textures are solid black in this data, so nothing shows),
12.1-18.4 s globe with the island and red pulse rings, 16.9-24.4 s map zoom (map.dds), 18.5-24.4 s Mittron crystals,
21.7-24.7 s "MITTRON ISLAND" text, 24.4-33 s city panorama strip with curtains, 30-37 s OMD emblem + text,
35-38.3 s development panels, 38.7-47.4 s HUV panels/red flash/HUV text, 47.3-52.2 s Director Green (jc.dds) and letter,
52.1-63.4 s character line-up on dot patterns with the "Drift City Remastered" logo (sr.dds) zooming in, last fade
ends 63.4 s.

Preview frames: `DriftCityOffline/Preview/intro_<n>.png` (1024x768) and `intro_wide_<n>.png` (1366x768), written by
`DriftCityIntroShots.Render` at the times listed in that file (n = index; the Unity log prints n, time, subtitles and
visible meshes).

## Remake (IntroScene.Play) - what is exact and what is not
* Exact: NIF, placement (root at Gamebryo (0,100,0)), orthographic camera and frustum, client viewport formula, black
  background, controller clamp playback at `begin + elapsed`, first frame not advancing, end at 63.4 s, skip keys
  (key-up of Esc/Space/Enter), subtitle keys/times/lines/colour/fade/font face+size+weight, no music change.
* Geometry morphs are applied by IntroScene itself (it reads NiMorphData, which the shared parser skips) - no change to
  NifSceneBuilder/NifAnimator.
* Materials: the remake's NiStandard shader is set to "emissive only" per mesh to match the light-less scene; fog is
  switched off while the intro runs and restored afterwards.
* Euler rotation keys: Gamebryo's matrix for angles (x,y,z) is the transpose of NifAnimator's `RotX*RotY*RotZ`
  (verified: the static matrices of `ingong`, `GeoSphere01`, `Circle01`, `sr_Plane01` equal the transposed product of
  their first keys, max error 0.001; NifAnimator's product is off by up to 2.0). NifAnimator therefore turns every
  Euler-keyed node the wrong way (here: the globe showed America instead of the Pacific, and the closing
  "Drift City Remastered" logo was turned away and culled). IntroScene re-poses those nodes itself after
  `NifAnimator.Sample`; NifAnimator is unchanged, so other NIFs with Euler keys still have the bug.
* Textures `white.tga`/`red.tga`: the shared texture resolver prefers same-named .dds files elsewhere
  (`game\credits\white.dds`, `oros\obj\red.dds`); IntroScene re-points them at the intro folder's own files.
* Inexact: playback is linear between keys (TBC/quadratic tangents ignored, as everywhere in the remake);
  Unity's transparent sort (view-depth of the bounds centre) stands in for NiAlphaAccumulator (same rule, ties may
  differ); subtitles use the remake's 1024x768 height-scaled canvas (the client draws 28 px unscaled at
  `screenH - 768 + y`; identical at 768 px height); line wrapping is Unity's, not D3DX's; a skip key only counts if it
  was pressed during the intro (so the key that started it cannot skip it); frame time is clamped to 0.25 s.
* Not checked against the client: which client state enters the intro (the lead calls it before the first licence
  registration); the description of "about 55 s" in footage vs. 63.4 s from the data (the last 3 s are fades).
