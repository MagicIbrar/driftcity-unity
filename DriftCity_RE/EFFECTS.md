# Driving effects in the original client (DriftCity.exe, Gamebryo 2.0)

How the client drives skid marks, skid flames, tyre smoke, collision sparks, crash bursts and damage smoke,
recovered from the exe, and how the Unity remake reproduces them
(`DriftCityOffline/Assets/DriftCity/Runtime/Game/CarEffects.cs` and `NifParticleEffect.cs`).

Confidence tags: **[exact]** = read directly from code or constants; **[inferred]** = meaning of a field or
value guessed from how it is used.

Decompiled sources are in `decompiled/phys/0x<addr>.c` (constants are inlined in comments).

## Effect manager

`FUN_006984d0` creates the effect singletons once (slots of one manager object) and calls each init:

| slot | ctor | init | what |
|---|---|---|---|
| [0] | 0x6c3160 (0x18c bytes) | 0x6c1090 | collision sparks (`effects/sparkle.dds`) + crash NIFs |
| [1] | 0x6b9dc0 (0xe8) | 0x6b9090 | tyre smoke (`effects/tiresmoke.dds`) |
| [2] | 0x6b66a0 (0x1c) | 0x6b7330 -> 0x6b6750, 0x6b6d70 | skid marks (`skidmark.dds`) + skid flames (`skidflame.dds`) |
| [3] | 0x6bb520 | 0x6ba920 | lap-time trail (`Green_trail_laptime1st.dds`, not a driving effect) |
| [5] | 0x6adf50 | 0x6ad210 | `tire_fire.dds` effect (not traced) |
| [6] | 0x6b5940 | 0x6b4280 | surface splash: `water_tm.dds` / `sand.dds` |

Per frame (dispatcher around 0x74b3a0..0x74b8c7, not a defined Ghidra function): 0x6c34f0 (crash/spark
update), 0x6c2950, 0x6b8d50 (skid generation), 0x6b7a00 (skid mesh rebuild), 0x6ba810 (smoke), 0x6b9730 (smoke
mesh), 0x6b6570 (surface splash), 0x6b4db0.

Global switches: `DAT_008d2982` = effects for the local car, `DAT_008d2983` = effects for other cars **[exact]**.
Per-vehicle effect state lives at `vehicle + 0x1ce0` **[exact]**.

All effect meshes use: NiAlphaProperty blend on, src SRCALPHA (6), dst INVSRCALPHA (7) (sparkle: dst ONE (0)),
ZBuffer write off, NiVertexColorProperty SRC_EMISSIVE + LIGHTING_E (unlit, colour = vertex colour),
NiStencilProperty draw mode 3 (both faces), texture apply MODULATE **[exact]**.

## Skid marks — FUN_006b8a90 / FUN_006b80b0 / FUN_006b8900 / FUN_006b7530

Manager constants (ctor 0x6b66a0) **[exact]**: `[2]=0.04` flame height per speed, `[3]=5.0` flame shrink rate,
`[4]=3.0` flame length, `[5]=2.0` flame lifetime, `[6]` = length multiplier (1.0, or `vehicle+0x518 * 1.25`
when that is > 0.8; field unknown).

Mesh: one shared NiTriShapeDynamicData, 2000 quads, UVs (0,0)(1,0)(0,1)(1,1), triangles (0,3,2)(0,1,3),
skidmark.dds clamp mode WRAP_S_WRAP_T **[exact]**.

**When marks are laid** (0x6b8a90) **[exact]**: for wheels index 2.. (rear wheels only) every frame while
`(vehicle+0x1c80 & 0x1000) || effect+0x14 != 0 || vehicle+0x508 != 0`; `effect+0x14` is the tyre-smoke state
(1/2, set by 0x6ba2a0 below), `vehicle+0x508` the boost mode. Flag 0x1000 comes from an item/state check
(`list->vfunc_0x1ac(3)`, 0x488bee) **[inferred: a skid item; not reproduced]**. When the gate is off,
`effect+0x18` is cleared so the next mark starts a new strip.

Point position: wheel node world X/Y, Z = wheel ground-contact height + 0.03 (`0x860ea8`) **[exact]**.
Half-width: wheel field `vehicle+0x944+k*0x214` * 0.5 (local player: `* 10 * FUN_004b2a50()`), doubled in
mode 2 **[exact formula; field = tyre width is inferred]**.

Boost mode `vehicle+0x508` (0x6b8d50, local player) **[exact]**: 0 if `player+0x584 == 0`, else 1, or 2 when
`vehicle+0x1c80 & 0x10000000`. `player+0x584` is set to 1 when the boost key path fires (0x47a7ef: requires
forward gear, sets `0x9bc=3.0`, starts sound 0xb) and selects the booster force `+0x48` instead of the engine
force `+0x44` (0x481ea7) **[inferred: = booster active]**. `0x10000000` = `player+0x590 & 1 && player+0x594 >= 1.5`
(`0x85e73c`) **[inferred: a second, stronger boost state]**.

Point record (0x13 dwords) **[exact]**: `[0]` life = 5.0, `[1]` remaining, `[2]` alpha, `[3..5]` centre,
`[6..8]` left edge, `[9..11]` right edge, `[12..13]` uv left, `[14..15]` uv right, `[16]` length since the
previous point, `[17]` strip length, `[18]` connected.

Adding a point (0x6b80b0) **[exact]**:
- Continue the strip only if not a new strip and the last point used the same texture half
  (`last.uv.v == 0.0` normally, `== 0.5` in mode 2). Otherwise start: uv (0,0)/(0,0.5) or (0,0.5)/(0,1).
- perp = segment direction rotated 90° in the ground plane; left = c - perp*hw, right = c + perp*hw.
- u = strip length / (4 * hw) + (0.3 for odd wheel index); v = 0 / 0.5 (mode 2: 0.5 / 1.0).
  So skidmark.dds top half = the faint normal mark, bottom half = the heavy black mark.
- If `segment + last.segment < 0.5` the last point is overwritten (keeps its connected flag);
  otherwise the last point's edges are re-aimed with the new perp and the point is pushed.
- Max points per wheel = `500 / 2^q` (`0x85d404`, `0x85b190`, q = texture quality `DAT_008d2a14`).
- On a pushed, connected point, and not in mode 2, a skid flame is added (below).

Fade (0x6b8900) **[exact]**: remaining -= dt; removed at 0; alpha = 1 while remaining >= 0.7*life, else
remaining / (0.7*life). So full for 1.5 s then linear fade over 3.5 s.

Rendering (0x6b7530): quad per connected point k: (p[k-1].left, p[k].left, p[k-1].right, p[k].right), vertex
alpha = point alpha, RGB white **[exact]**.

### Skid flames **[exact]**
Mesh: 400 quads, skidflame.dds (256x512 DXT3, four flame rows), clamp. Created on each pushed skid point:
`p0` = wheel point, `p1 = p0 + normalize(previous - newest) * 3.0 * [6]` (trails back along the mark),
height `h = 0.04 * [6] * player+0x534` (0x534 = speed, **[inferred]** m/s), life 2.0 s,
uv row `r = rand()&3`: v0 (1,(r+1)/4) v1 (0,(r+1)/4) v2 (1,r/4+0.01) v3 (0,r/4+0.01).
Per frame: `h -= 5.0*dt` (min 0), alpha = min(h, 1); top vertices = p0/p1 + (0,0,h + rand(-0.1,0.1)) re-rolled
every frame (flicker). Max `400 / 2^q`.

## Tyre smoke — FUN_006ba2a0 (spawn), FUN_006b9fb0 (particle), FUN_006ba160 (update), FUN_006b9730 (render)

Manager constants (ctor 0x6b9dc0) **[exact]**: 0xa0 = 10, 0xa4 = 30, 0xb0 = -0.004, 0xb4 = 0.14, 0xb8 = 18,
0xbc = 30, 0xc8 = -1/600, 0xcc = 0.06 (0xa8/0xac/0xc0/0xc4 unused here).

Inputs: half-floats synced in `vehicle+0x1c86/0x1c88/0x1c8a` = `player+0x3e4` (longitudinal force / mass),
`player+0x3e8` (lateral, = `+0x5c8`), `player+0x540` (packed at 0x488e72) **[exact]**; 0x3e4/0x3e8 are the tyre
model's longitudinal/lateral acceleration **[inferred from 0x481e9b, 0x482df5]**, 0x540 a slip value used through
`asin` **[inferred: sin of the side-slip angle]**.

Spawn logic **[exact]**:
- `effect+0x14` (smoke state) = 0; skip if `flags & 4` (boost), `flags & 0x7000` (item states), race not active,
  or `flags & 0x10000000`.
- a = min(|lat|, 30): if a >= 18 -> state 2, interval = 0.06 - a/600 (0.03..0.01 s).
  Else b = min(|long|, 30): if b >= 10 -> state 1, interval = 0.14 - 0.004 b (0.10..0.02 s).
- Accumulate dt; if it moved since last frame and spawns per unit distance < 3, shorten the interval to give
  3 per unit; min interval 0.01.
- While the accumulator exceeds the interval: for every wheel (all four) emit one puff at wheel centre +
  car-rotated (-0.2, 0, -0.2) for even (left) / (0.2, 0, -0.2) for odd (right) wheels, shifted back along the
  car's movement for sub-frame spawns. In state 2 (a > 18) the even wheels skip when lat > 0 and the odd
  wheels when lat < 0.
- Base velocity (0x6b9610): `x = 0x540`; if asin(x) != 0: car-rotated normalize((x<0 ? +1 : -1), asin(x), 0).

Particle (0x6b9fb0) **[exact]**: life 0.7; alpha 1; radius rand(0.1,0.4); angle rand(-3,3) rad;
spin rand(-1,1) rad/s; pos += (rand(-0.1,0.1), rand(-0.1,0.1), rand(0,0.1)); velocity =
normalize(rand(-1,1), rand(-0.5,0.5), rand(0.3,0.5)) + base. Max `500 / 2^q`. `rand(a,b)` = a + (b-a)*(rand()%100)/100.

Update (0x6ba160) **[exact]**: alpha = age/(0.1 L) in the first 10%, 1 until 70%, then 1 - (age-0.7L)/(0.3L);
pos += vel * 0.5 * dt; radius += 0.5 * dt; angle += spin * dt. Rendered as camera-facing quads (corners
±radius along camera right/up, rotated by angle), white, tiresmoke.dds alpha blended.

## Surface splash (sand/water) — FUN_006b61a0, FUN_006b5c90 **[exact]**
Same structure as tyre smoke, for vehicles with `flags & 0x2000` (sand.hit zone, `player+0x8ac`, `effects/sand.dds`)
or else `& 0x4000` (water.hit zone, `player+0x8b0`, `effects/water_tm.dds`, additive), speed >= 5, interval
`0.02 - speed*0.0003` (min 0.003), all wheels, same wheel offsets; only in the map named "Cras". Particle values,
zones and the port (CarEffects `UpdateSplash`, `CarRoadState`): ROADS.md sections 5 and 8.

## Collision sparks and crash bursts — FUN_006c34f0, FUN_006c2d30, FUN_006c1090

Contacts are queued in a vector at `DAT_008d19f0` (32-byte entries: `[0]` type, `[1]` int impact value,
`[2..4]` point, `[5..7]` normal) by the physics callbacks (0x47eb74, 0x4bfff6, 0x73890e) and consumed once per
frame for the local car only **[exact]**.

Per contact **[exact]**:
- strength = (int)(entry[1] * 3.6) (`0x86074c`) **[inferred: impact speed m/s -> km/h]**; 0 = ignored.
- duplicates (same x within 0.001) ignored.
- burst height clamped to car z + [0.3, 0.9].
- strength >= 50: one spark (0x6c2d30).
- kind: type 1 -> block; else front when `-dot(car forward, normal) >= 0.7`, otherwise side.
- no new burst if one of the same kind is younger than 0.5 s.
- NIF by strength: < 100 `crash01_*`, < 200 `crash02_*`, else `crash03_*` (slots 0x168..0x188), cloned, placed
  at the contact with the car's rotation, moved with the car's translation every frame.
- lifetime: front 0.5 s (`0x8649a8`), side 2.0 s (`0x85b190`), block 5.0 s (`0x876a38`).
  Type 1 = world geometry **[inferred]**.

Spark (0x6c2d30; constants from ctor 0x6c3160: 0x108 0.5, 0x10c 0.4, 0x110 0.04, 0x114 0.07, 0x118 2.0,
0x11c 0.8, 0x120 20, 0x124 1.0, 0x128 0.7, 0x12c 0.05, 0x130..0x13c) **[exact constants]**:
life 0.5 + rand(0,0.4); half-thickness 0.04 + rand(0,0.07); length 2.0 + rand(0,0.8); velocity =
car-rotated (rand(-0.05,0.11), gear >= 0 ? -1 : +1, rand(-0.06,0.2)) * 20; two crossed quads (car up and car
right) from the contact along the velocity direction, UVs (0,0)(0,1)(1,0)(1,1) so sparkle.dds's bright end leads.
Update: follows the car's translation, pos += vel*dt, the 4 far corners sink 1.0/s, alpha = remaining /
(0.95 life); max 100; additive (SRCALPHA/ONE) with the texture also as glow map (double brightness).
Vertical velocity decay 0.7/s **[inferred from a mis-decompiled line]**.

Crash NIFs are 3ds Max SuperSpray NiParticleSystems (NiPSysBoxEmitter with zero box, NiPSysEmitterCtlr birth
rate 30..346/s for 0.13..0.67 s (clamped cycle), grow/fade, colour fade, rotation, gravity, spawn-on-death,
spak_000..003 flip (0.033 s/frame), fire001 flash of radius 2.5). Particle systems are scaled by 0.0022..0.0025
(speeds 3300..6000 -> 7..15 m/s, radii 25..600 -> 0.06..1.5 m) and simulated in world space.

## Damage smoke — 0x69e590 (per vehicle), damage_smoke.nif loaded by FUN_006b23e0 **[exact]**
Shown when `(max durability - durability) / max > 0.6` (`0x85b3f4`; +0x1db0/+0x1db8), placed every frame at
car position + car-rotated (0, 1.5, 0.38) with the car's rotation. The NIF loops (controller cycle LOOP, 1.667 s):
"end smoke" (alpha, speed 7.5, gravity 13.5 along the NIF's -Y = backwards), a red additive "fire" with a 15-frame
flip and spawn-on-death, and a white SuperSpray.

## Booster
Already present in the remake (`CarFactory.AttachBoosterFlames`, shown while `Boosting`). The booster NIFs'
meshes are drawn; their `PArray01` mesh-emitter particle system is not (NiPSysMeshEmitter is not supported by
`NifParticleEffect` either).

## Remake mapping (CarEffects.cs)

| original | remake |
|---|---|
| tyre model accelerations 0x3e4/0x3e8 | Rigidbody velocity derivative in the car frame (smoothed 60 ms, ignored 0.1 s after a collision) |
| boost mode 1 (`+0x584`) | `DriftCar.Boosting` |
| boost mode 2 (`0x10000000`) | `CarEffects.HeavyBoost` (no remake equivalent, off) |
| wheel node / contact Z | `DriftCar.WheelVisuals[i]` + downward raycast, point + normal * 0.03 |
| wheel width field | measured tyre mesh width (clamped 0.12..0.45 m) |
| 0x534 speed | `DriftCar.SpeedKmh / 3.6` |
| 0x540 slip | sin of the body's side-slip angle |
| contact list | `OnCollisionEnter`, strength = (int)relativeVelocity * 3.6, world (no/kinematic non-car rigidbody) = block |
| durability | `CarEffects.Damage01` (nothing sets it yet) |
| quality caps | q = 0 (500 points/wheel, 400 flames, 500 smoke, 100 sparks, 500 splash per kind) |
| sand/water flags 0x2000/0x4000 | `CarRoadState.Sand` / `.Water` (zones from sand.hit / water.hit) |

`NifParticleEffect` plays any NiParticleSystem effect NIF: it re-walks the NIF blocks to read the emitter,
emitter-controller, grow/fade, spawn, rotation, gravity and colour fields that the shared parser skips, emits
particles by script with NiPSysEmitter's formulas (direction from declination/planar angle around the emitter
object's Z, value + variation * (rand - 0.5) for speed/radius/life), maps the modifiers onto Unity modules, and
swaps the material texture for NiFlipController frames.

## One-shot effect NIFs (event effects) — effect manager `FUN_004386b0()+0x108`

The client keeps a second set of effect NIFs on the stage object it reaches as `FUN_004386b0()->+0x108` (called the
effect manager here; loaded by 0x69ab10 / 0x6af0f0 / 0x6af9e0 / 0x6b2c60 / 0x6b23e0). When an event fires, the NIF is
cloned (or a preloaded one re-posed) and drawn with `NiAVObject::Update(time)` from a clock that starts at 0
(`StartAnimations(0)`), so the NIF's own keyframes do the animation. Remake: `Runtime/Game/GameEffects.cs`. `NifEffect`
plays one built NIF on such a clock: meshes and controllers come from NifSceneBuilder and are posed with
`NifAnimator.Sample(time)`, and particle systems come from NifParticleEffect.

Clock and lifetime **[exact]**: an "animated NIF holder" (load 0x4cbd00, step 0x4cbf20) keeps `{node, animated, loop,
time, begin, end}`, where `begin/end` are the min/max of `GetBeginKeyTime/GetEndKeyTime` over every controller in the tree (0x4cbbc0).
Both start at 0, so `begin` stays 0 unless a key time is negative. Without the loop flag the clock just keeps running.
The UI table entries use `end - begin` as their screen time (0x6b2c60, entry +4).

### Slots and spawners **[exact]**

| NIF | manager slot | spawned by | placement | lifetime |
|---|---|---|---|---|
| `levelup.nif` | +0x1f0, clock +0x1ec | 0x69c040 (level-up reply handler, which also plays SID_LEVELUP and prints text 10053) sets +0x1ec = 0; so does packet handler 0x5a (0x4f14d0) via 0x697a70 | local car's world translation every frame, rotation identity (0x698e90) | while clock < 2.467 s (`0x8750d4`) |
| `effects/ui/*.nif` (60 entries, table below) | +0xf78, 0x54-byte entries | 0x69b900(index, seconds) | (0, 100, 0) in front of the UI camera +0xdc (0x69bf40) | clock += dt * speed until `end - begin`; speed is 1, or (end - begin) / seconds when seconds > 0; then the queued `next` index plays |
| `getitem.nif` | +0xf5c, list +0xf48 | 0x69fdd0 from the drop handlers 0x4f5ca0 / 0x4fb280 (after SID_ITEM_DROP_GET1) | UI camera +0xe0: (41, 20, z0) until 1.0 s, then linear to (-40, 20, -35) by 1.5 s; z0 = 10 (5 / 2 / -3 when +0xf60, a UI layout flag, is set) | 1.5 s (`0x85e73c`), then SID_ITEM_DROP_GET2 (0x6a3ca0) |
| `palmtree_explosion01/02/03.nif` | +0xf08/+0xf0c/+0xf10, list +0xef8 | 0x69f870(x, y, z, type 0xb/0xc/0xd) from 0x4035f0 | the object's placement point (geom position minus its table centre height), rotation identity | 3.333 s (`0x8616b4`), at most 10 |
| `hydrant_explosion.nif` | +0xf28, list +0xf18 | 0x69f9f0(x, y, z) from 0x403660 | same | 8.333 s (`0x8756c8`), at most 10 |
| `jump_up.nif` | +0xf40, list +0xf30 | 0x69fc90(vehicle) at 0x4851ae, when the jump state goes 1 -> 2 (all four wheel air heights `+0x654/66c/684/69c` > 0.5) | vehicle translation + rot * (0,0,0), no rotation (0x69f3d0) | 1.333 s (`0x3faa9fbe`), at most 10, drawn only while `vehicle+0x1bd0 > 0` |
| `jump_down.nif` | +0xf44, same list | 0x69fb40(vehicle) at 0x485299, on the first touch-down from state 1 or 2 (-> 3); followed by SID_JUMP_END | same | 1.333 s |
| `graze_l.nif` / `graze_r.nif` | +0xec4 / +0xedc (holders without loop), list +0xeb4 | 0x6a11f0(player, side) from the near-miss check 0x4a6460, when a pass is first armed (< 2 m at > 100 km/h) and the local-car effect switch 0x8d2982 is on | player pos + rot * (∓0.6·v[0x220], 0.4·v[0x21c], 0.4·v[0x224]), with the player's rotation (0x69c900) | the clock runs on without looping; a repeat for the same car and side only resets the first child's material alpha to 1; at most 10 |
| `huv_explosion.nif` | +0x10c8 holder, cloned per HUV into vehicle+0x1d84 (0x4909a9) | HUV state `+0x1dc8 == 2` (destroyed / caught) | HUV node rotation and translation every frame (0x6f87e0) | plays once (holder without loop) while the HUV stays in state 2 |
| `damage_smoke.nif` | +0x1190 -> vehicle+0x1d80 | see "Damage smoke" above (already in CarEffects) | | |
| `bind.nif` (loop), `bind_hit.nif`, `tornado.nif` / `TORNADO02.nif` (loop), `tornado_hit`, `jungchal.nif` | +0x114 / +0x140 / +0x16c / +0x184 / +0x1b0 | 0x6af9e0 loads them and 0x6a6d20 runs them while the city number is 4 (Oros) in free roam (stage 0xE) - **(fix)** not PvP item races (PVP.md 13.1) | dust devils 150 m + speed/2 ahead + rand(-20, 20) per world axis, on the ground + radius; tornado 170 m ahead + rand(-30, 30); bind_hit at a dust devil the car touched | dust devils until hit, or 3 s old and 250 m away (at most 3, every 5 s); tornado to the end of its animation (one, every 15 s); bind_hit once. See "Oros ambience" below |

Side pick for the graze **[exact]**: let `x` be the player's position along the traffic car's local X and `b = x < 0`.
`b` is inverted when the two cars' Y columns point opposite ways. Side = `(b + 1) & 1`: 0 = graze_r at +X, 1 = graze_l at -X.
So the effect appears on the side of the player where the traffic car is. The vehicle frame is X right, Y forward, Z up
(GetCol(1) is the heading in 0x4a6460, and the tyre smoke offsets use X for left/right). `v[0x21c]`, `v[0x220]` and `v[0x224]` are
taken as length, width and height, the vehiclelist length/width/height order **[inferred]**: with a half width the
graze would sit inside the body, and the frames show the 5 m speed-line sheet belongs just outside the side. The remake
derives width, height and length from the chassis BoxCollider (`size.x / 0.9`, `size.y + 0.4`, `size.z / 0.95`,
undoing CarFactory.AddPhysics) and places the graze at Unity (-/+0.6 * width, 0.4 * height, 0.4 * length).

Map objects **[exact]**: the knockable objects are the `<city>\modeltable.nxt` rows whose third column ("이펙트ID",
EffectID) is non-zero:
- 11/12/13 = palm trees (야자나무) -> palmtree_explosion01/02/03. `obj->+8->+0x84->+0x24` is this id (0x4035f0).
- 2 = fire hydrant (소화전) -> hydrant_explosion (0x405170 -> 0x403660).
- 3 = lamp posts, signs and traffic lights; 5/6 = signals and construction signs. None of these has an explosion NIF.
- 1000 = jump ramp.

A hit (0x405010: object timer `+0xc <= 0` and `dCollide` with the car) does four things:
- plays the vehicle's 3D sound slot 3 (SID_HIT_TREE) or 4 (SID_HIT_HYDRANT) (`vehicle+0x1cac` slots, 0x4c4070);
- spawns the NIF;
- sets the object timer to 10 s (`0x41200000`);
- leaves the body to 0x403fc0, which puts it back afterwards.

### UI effect table (0x6b3b00) **[exact]**

60 entries `{time, end-begin, speed, next, node, name, ..., mode flags +0x50}`:

0 ui_levelup, 1 arbeit_com, 2 arbeit_com_gold, 3 arbeit_com_silver, 4 arbeit_com_bronze, 5 arbeit_fail, 6 timeattack_com,
7 timeattack_fail, 8 im_success, 9 arrest_fail, 10 arrest_success, 11 quest_fail, 12 quest_success, 13 3rd_anniversary_fin,
14 qs_newrecord, 15-17 ui_upgrade_s/f/d, 18 pvp_countdownstart, 19 pvp_finallap, 20 pvp_finish, 21-26 pvp_lap2..7,
27 pvp_raceover, 28 pvp_retire, 29 pvp_tencount, 30 pvp_winner, 31 pvp_wrongway, 32 pvp_boostcharge100,
33 pvp_boostcharge50, 34 pvp_crash_recover100, 35 pvp_boostcharget50, 36 pvp_null, 37 pvp_untouchable, 38 pvp_booost,
39 pvp_overheat, 40 pvp_skidrush, 41 channel_count, 42 mitbomb_ui, 43 goldentime, 44 pvp_repair_girl_ui, 45 beat_success,
46 beat_fail, 47 pvp_repair_girl_plane, 48/49 i_d_repair02_5s_left/right, 50 t_ob_event_04, 51 undercity/changLR,
52 critical, 53 party_soonchal, 54 im_failed, 55 congratulation, 56 transform_success, 57 Game/X_tree001/X_tree001_button,
58 ChocoletWrap/valen_choco_fx_01, 59 GoodHarvest/goodharvest_fx_01.

The flags at +0x50 (4 = city, 0x20/0x10 = PvP, ...) choose which entries 0x6b2c60 loads for a stage. 0x69b900 adds
these rules:
- In the alternate arbeit mode (`0x6cb0c0()+0xed == 1`: the faction dword, 1 = Team ROO, whose Delivery Service is "Time Attack"),
  id 1 becomes 6 and id 5 becomes 7.
- Ids 8, 54, 10 and 9 (im_success, im_failed, arrest_success, arrest_fail) exclude each other. While one plays, a new id
  is stored as its `next` and plays when it ends.
- Re-playing an id restarts it.

UI camera **[exact]** (0x698140): the rotation rows are (0,0,1), (1,0,0), (0,1,0), so it looks along world +Y with +Z up
and +X right. The frustum is `NiFrustum(-51.2, 51.2, 38.4, -38.4, 0, 512, ortho)` for +0xdc, with far 1000 for +0xe0 (getitem).
The remake uses an orthographic camera far below the city (layer 29, `GameEffects.ScreenCamera`) looking along Unity +Z
with half-height 38.4. Its width follows the screen aspect, the way the 1024x768 HUD canvas matches height, instead of
stretching 4:3 **[remake choice]**.

### Event callers and sounds **[exact]**

| event | client | effect calls | sound |
|---|---|---|---|
| level up | 0x69c040 (reward replies 0x4f3800, 0x4f407e, ...) | 0x69b900(0) ui_levelup + levelup.nif | SID_LEVELUP (0x4e97) |
| HUV caught | 0x6f7d80 | 0x69b900(10) arrest_success, or 45 beat_success when `0x4386b0()+0x168 -> +0xe0 == 1`; HUV state 2 -> huv_explosion | SID_HUV_SUCCESS (0x4e90) |
| HUV escaped | 0x6f7d80 | 0x69b900(9) arrest_fail / 46 beat_fail | SID_HUV_FAIL (0x4e91) |
| PvP finish (local player) | 0x723fe0 | 30 pvp_winner (winner) or 20 pvp_finish; 14 qs_newrecord when the time beats the record | rank voice SID_PVP_VO_1ST/2ND/3RD, then SID_PVP_RACE_WINNER (0x4ed1) / SID_PVP_RACE_FINISH (0x4ed2) |
| quest result | 0x6f63b0 / 0x6f6750 | 12 quest_success / 11 quest_fail | |
| arbeit (part-time job) | 0x558800, 0x6f6db0 | 1 arbeit_com, 4 arbeit_com_bronze, 5 arbeit_fail | |
| item picked up | 0x4f5ca0 / 0x4fb280 | getitem | SID_ITEM_DROP_GET1 (0x4eec), then SID_ITEM_DROP_GET2 (0x4eed) at the end |
| palm tree / hydrant hit | 0x405010 / 0x405170 | palmtree_explosion0N / hydrant_explosion | SID_HIT_TREE (0x4eb0) / SID_HIT_HYDRANT (0x4eaf), 3D at the car |
| take-off / landing | 0x4809c0 (0x4851ae / 0x485299) | jump_up / jump_down | landing: SID_JUMP_END (0x4eeb); take-off: SID_PVP_VO_JUMP in PvP only |
| near miss armed | 0x4a6460 | graze_l / graze_r | none of its own (the combo award brings SID_COMBO_GRAZE) |
| HUV destroyed | vehicle slot `+0x1cac[12]` = SID_HUV_EXPLOSION (0x4ea5) | huv_explosion | the play call was not found; the remake plays it with the explosion **[inferred]** |

The sound table (static initializers around 0x523000-0x526400, entries `{..., 12.0, 240.0, ..., id, name[0x64]}`) maps
ids 0x4e8a..0x4f33 to `SID_*.WAV`. `0x52b980(id, flag)` on the sound manager 0x8d79c8 plays a 2D one-shot.

Notes from the frames: beat_success / beat_fail read "ROO MEMBER Chase Successful/Failed" (the ROO gang chase, so
`+0x168 -> +0xe0 == 1` marks that chase type **[inferred]**). graze_l/r is a sheet of white speed lines about 5 m long
that streams backwards along the car's side in 0.5 s. levelup is a blue light pillar (8.6 m) with rising rings and
sparkles. getitem is the "Get Item" caption. The remake parses the beat_fail and channel_count controllers, which are
saved inactive, because StartAnimations starts every controller (`NifSceneBuilder.IncludeInactiveControllers`)
**[inferred from Gamebryo's NiTimeController::Start]**.

### Remake status

`GameEffects` (Runtime/Game/GameEffects.cs) offers:
- `LevelUp(car)`, `ChaseEnded(caught, huv)`, `HuvExplosion(huv)`, `RaceFinished(won, newRecord)`, `GetItem()`;
- `PropHit(effectId, basePosition)`, `MakeBreakable(prop, effectId)` (a 10 s trigger zone, `PropHitZone`) and `PropEffectIds(city)`;
- `JumpUp/JumpDown(car)`, `TrackJump(car)`, `Graze(car, left)`;
- `ArbeitDone(rank)` (0x558800: arbeit_com, then arbeit_com_gold / _silver / _bronze for ranking place 1 / 2 / 3) and
  `ArbeitFailed()` (0x6f6db0: arbeit_fail);
- `FactionVariant(name, roo)` - 0x69b900's Team ROO rule, applied inside `Screen`: arbeit_com -> timeattack_com, arbeit_fail ->
  timeattack_fail (character dword +0xed == 1 = ROO; `RooOverride` for tests);
- `GateEffect(type)` / `PlayGateEffect(type)` (PvP gate effects, PVP.md 14.5: 0 pvp_null, 1 pvp_boostcharge100, 2
  pvp_boostcharge50, 3 pvp_skidrush 30 s, 5 pvp_untouchable 5 s, 6 pvp_booost 30 s, 7 pvp_boostcharget50) and `GoldenTime()`
  (result bonus flag 8, PVP.md 14.8);
- generic `Screen(name | table index, seconds)`, `At(name, pos)` and `Attach(name, target, offset, rotate)`.

Wired:
- `ChaseMission.End` calls `ChaseEnded`, Team ROO chases `StepQuestRunner.RooChaseBanner` (beat_success / beat_fail +
  huv_explosion); `RushTime` and `VirtualChase` play arrest_success / arrest_fail;
- `RaceMission.Finish` / `OnlineRace` call `RaceFinished`, the PvP lap / countdown / wrong-way / ten-count / race-over banners;
- `DriftCombo` calls `TrackJump` every physics step and `Graze` when a pass is armed;
- `Progress` plays the level-up and the item pickup; `CityLoader` makes the palm trees and hydrants breakable;
- `StepQuestRunner` plays quest_success / quest_fail; `UndercityStage` critical, congratulation, changLR, the countdowns;
- `PvpResults` plays goldentime (bonus flag 8); `OrosAmbience` the dust devils, bind_hit and the tornadoes (below).

Left to the owners of those files (hook lines in the PvP round 3 report), and not wired:
- `QsRankList` close after a finished run: `GameEffects.ArbeitDone(myRank)` (0x558800 plays it when QSRANKLIST closes on a
  run's result); `DeliveryService.Fail`: `GameEffects.ArbeitFailed()` (ROO gets the time-attack banners by itself);
- congratulation on items (0x4fa760 / 0x4fb280) and 3rd_anniversary_fin (13: no `push 0xd` before a 0x69b900 call was
  found; the anniversary quest would play it).
- t_ob_event_04 (50): 0x734a00 (from 0x73875a) is a Mission Battle object's hit (effect manager `+0x1c`, the GM "MissionBattle
  On (UFO / Ring / Rock)" objects, PVP.md 14.6): an object of type 4 hit by the local car plays the ink splats over the screen
  and sets vehicle `+0x590 |= 1`, `+0x594 = 2.0` (a 2 s state) [inferred]. The remake has no Mission Battle objects, so it
  is not wired (`Screen("t_ob_event_04")` plays it).
- Round 4 (PVP.md 15.1, EVENTS.md 12): t_ob_event_04 is wired to the mission-battle ink objects (PvpMissionBattle), and
  3rd_anniversary_fin plays with the anniversary attendance gift (EventObjects).

### Wired this round (round 5) **[exact callers]**

Sound ids from the sound table 0x523000-0x528000 (entries `{..., id, name}`; dumped to the scratchpad's `fx5\sids.txt`):
0x4e8e SID_QUEST_SUCCESS, 0x4e8f SID_QUEST_FAIL, 0x4eb6/7/8 SID_UPGRADE_SUCCESS/FAIL/DESTROY, 0x4ef9 SID_PVP_REPAIR,
0x4f15 SID_PVP_REPAIR_GIRL, 0x4f1c SID_PATROL_PARTY_COUNT, 0x4f1d SID_GUARD, 0x4f1e SID_XMAS_EVENT_SUCCESS, 0x4f27
SID_VALENTAIN_COMPLETE. The last four are not in the US data: `GameEffects.SfxOr` falls back to a close sound.

| banner | client caller | remake |
|---|---|---|
| 8 im_success "Instant Mission Complete" | packet 0x3e5 -> 0x4f3ff0 -> 0x6f6560 (SID_QUEST_SUCCESS, 10123, "Exp : %d Mito : %d") | `GameEffects.InstantMission(true)`: `InstantMissions.Complete`, `PatrolMission.Finish(true)` (solo), `PatrolMission.EndParty` (complete) |
| 54 im_failed | packet 0x3e6 -> 0x4efa30 (11158 when a party patrol was running, SID_QUEST_FAIL) | `InstantMission(false)`: `InstantMissions.Fail` (time out), `PatrolMission.EndParty` (failed; not when the leader quit, 11159 0x4f3ed0 plays nothing) |
| 53 party_soonchal "EMERGENCY PATROL REQUESTED!" | 0xc24 start 0x4f4210 (2.5 s, 0x40200000); patrol tick 0x61d8ae: while any of the patrol object's four posts is open and the banner is not playing (0x698980) it starts again | `PatrolAlert()` from `PatrolMission.BeginParty` and every party-patrol frame while posts are open |
| 29 pvp_tencount (10 s) | patrol tick 0x61d829: under 10.5 s (0x2904 ms) left, once: SID_PATROL_PARTY_COUNT + the count; back above: stop (0x698840) | `PatrolCount()` / `PatrolCountStop()` in `PatrolMission.PatrolBanners` |
| 15/16/17 ui_upgrade_s/f/d "Modification successful / unsuccessful", "Parts Destroyed" | 0x19e -> 0x6f4100 (0x30000/1/2: 10102/10103/10104), the parts shop 0x5c8fd0 / 0x616060 (10765/10766), restore 0x6f42b0 (0x30003/4, 10882: success pair) | `GameEffects.Upgrade(result)`: `WorkshopMenus.Enhance` (s/f), a failed `Combine` (d: the weaker part is lost), `InventoryWindow` modify (s/f), `SetParts.Restore` (s) |
| 56 transform_success "Converting Successful" | 0x1ab -> 0x4f53a0: status 0 = 10732 "%s was successfully converted." + SID_GUARD, status 1 with feature OptionCraft_Guarantee = the banner | `GameEffects.Converted()`: a successful `Combine` and a successful manufacturing (`Craft`) |
| 41 channel_count (3-2-1) | 0x723d60 (race start; crew room) -> Team Center object 0x674e10: the crews' names, the banner, then stage 0x12 | `GameEffects.ChannelRound()`: `OnlineRace.Begin` when race.start carries a round, `RaceMission.StartRoom` for an offline Channel Battle room |
| 42 mitbomb_ui "MITTRON FIREWORK" | 0x394 -> 0x50a4b0 when the target is me: 0x476340(2), booster 0x47beb0(5.0, 1), the banner, race manager `+0x1b8(0, 1.8)`; the car side is entry 42's second NIF mitbomb_car (state bit 0x8000, 0x72e9c0) | `GameEffects.MittronFirework()` from `ChannelBattle.OnCheer` (target = me): gauge filled, mitbomb_car on the car 5 s, banner |
| 44 pvp_repair_girl_ui (+ car NIFs 44 / 47 / 48 / 49) | 0x4791a0: a repair with char flag `+0xf32` -> banner 44 + SID_PVP_REPAIR_GIRL, else 39 pvp_overheat + SID_PVP_REPAIR; 0x72e820 per racer: state bit 0x80000 -> entry 44's second NIF pvp_repair_girl 3.5 s, local racer + 47 pvp_repair_girl_plane, bit 0x200000 -> 48/49 i_d_repair02_5s_left/right 4 s (bit 0x800: 39's pvp_overheat_repair 5.2 s, + 48/49 6 s) | `GameEffects.RepairGirl(car, fiveSecond)`: `BattleQuickSlots` repair kit, `ItemUse` Gio (i_d_repair02 = the 5 s one) |
| 57 X_tree001_button | 0x694a5b -> 0x694aa4: SID_XMAS_EVENT_SUCCESS + the effect after a decoration (0xcee) | `GameEffects.XmasDecoration()` in `XmasTree.Donate` |
| 58 valen_choco_fx_01 | VALEN reveal 0x694098: SID_VALENTAIN_COMPLETE, the effect, 11578 | `GameEffects.ChocolateWrapped()` in `HarvestScreens.ValenTick` |
| 59 goodharvest_fx_01 | AUTUMN result 0x692d00: the effect + SID_VALENTAIN_COMPLETE | `GameEffects.HarvestBoxMade()` in `HarvestScreens.AutumnTick` |

The UI table's entries carry a second NIF name (+0x34, loaded into +0x30 when +0x48 is set, 0x6b2c60): 2a mitbomb_car,
27 pvp_overheat_repair, 2c pvp_repair_girl, 2f/30/31 pvp_repair_girl_plane / i_d_repair02_5s_left / _right (these three have
no screen NIF). 0x6a7bb0(index, ?, vehicle, seconds, ...) attaches that NIF to a vehicle shown while `vehicle+0x1bd0 > 0`.
Frames: `DC_FX_ONLY` now takes any screen name (`fx_party_soonchal_*`, `fx_mitbomb_ui_*`, `fx_t_ob_event_04_*`,
`fx_transform_success_*`, `fx_ui_upgrade_s/f/d_*`, `fx_X_tree001_button_*`, `fx_valen_choco_fx_01_*`, `fx_goodharvest_fx_01_*`,
`fx_pvp_repair_girl_ui_*`). pvp_repair_girl_ui and X_tree001_button draw almost nothing on the UI camera in those frames (2
controllers / no meshes on screen) [not resolved]. Tests: `Tests/BannersPeriodsTests.cs` (`BannersOfTheGameScreens`).

Not ported:
- the PvP gate effects' rules (what a gate gives is the server's; only the banners exist);
- effect NIFs outside the UI table that no traced code of this round reaches: `c_p_siren`, `c_p_siren02`, `huv_booster`,
  `huv_gauge(_part)` (HUV chase), `c_p_siren_laptime1st` + `Green_trail_laptime1st.dds` (effect manager slot [3], the lap-time
  battle's leader), `mita500_bott` / `mita500_water` (the Mita drink), `pvp_damage_l/m/s`, `untouchable_shield`,
  `c_p_boostershield`, `pvp_booost_nos`, `mitbomb_car`, `pvp_overheat_repair`, `pvp_repair_girl`, `pvp_countdown_base` /
  `_cam00..02`, `rt_start`, `christ_gift` / `christ_girl`, `bossscene_*`, `ac_tcam` / `pw_tcam` / `sp_tcam`, `tornado_hit`,
  `tire_fire.dds` (slot [5]).

### Oros ambience (0x6a6d20) - corrected **[exact]**

The dust devils carry no sound. 0x697af0(x, y, z, radius, record) creates a **collision sphere** (0x80fcb0 -> 0x80fc20: a
sphere geom of the given radius, "Bad argument" when <= 0) in the effect manager's collision space +0x1198 (0x80ec30 ->
0x80e7a0: an octree of depth 7 over 20000 x 20000 x 2000, made at 0x6a6296), places it (0x80f260) and stores the record as
its data (0x80f0a0); 0x697b30 moves it every frame. 0x6a4e50 collides the local car's two geoms (vehicle +0x19f8 / +0x19fc)
with that space every frame (0x814fe0, callback 0x6a4db0 checking the geom classes); a touch calls 0x6a3e50: bind_hit.nif
(holder +0x140, no loop) is cloned into the hit list +0x158 at the dust devil's world x / y and its height, and the dust
devil is marked hit (+0x34) - removed on the next update together with its sphere (0x80f070). The radius is the record's
+0x2c = scale x 0.63, and the dust devil stands on the ground raised by it; the NIF's own scale is 0.7..1.7 (the earlier
note "dust devils x 0.63" applied it to the NIF). Offsets are rand(-20, 20) (devils) / rand(-30, 30) (tornado) on the world
X and Y axes, not sideways from the camera; one tornado at a time (+0x1ac), placed with a pick straight down. Remake:
`OrosAmbience` (sphere test against the chassis box, `HitCount`, `Tick` for tests).

The remake plays beat_success / beat_fail for Team ROO chases (`StepQuestRunner.RooChaseBanner`) and the ROO time-attack
banners through `FactionVariant`; every UI table entry is reachable through `Screen`.

Batch frames: `-executeMethod DriftCity.EditorTools.DriftCityEffectShots.Render` (no `-nographics`) writes
`Preview/fx_<name>_<t>.png`. Tests: `Tests/EffectsTests.cs`, `Tests/PvpReplayTests.cs` (faction / arbeit / gate banners, the
dust devil hit).
