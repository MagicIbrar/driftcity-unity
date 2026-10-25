# Road map, off-road, wrong way and surface zones (sand / water)

Recovered from `DriftCity.exe` (image base 0x400000). Decompiled sources: `decompiled/phys/0xADDR.c`.
`in[idx]` is the player car object (`XiDynamicControl`) read as `float[]` (byte offset = idx * 4), as in
PHYSICS.md / COMBO.md. **[exact]** = read from code or data, **[inferred]** = interpretation.

Unity port: `Core/HitMesh.cs`, `Core/RoadMap.cs`, `Core/RoadNetwork.cs` (parsers + queries, also built by the
harness), `Runtime/Game/RoadMapQuery.cs` (per city), `Runtime/Game/CarRoadState.cs` (per car flags),
`Runtime/Game/CarEffects.cs` (splash), `Runtime/Game/DriftCombo.cs` (wrong way, off-road combo reset).
Harness check: `dotnet run -c Release --no-build -- "<install>" roads 0 [cities...]`.

## 1. Summary

- **`lane0.llf` is not a road map.** It is one of the world stream files (`\terrain0.lf`, `\blockObj0.lbf`,
  `\blockObj1.lbf`, `\lane0.llf`, `\guardrail0.lgf`, `\modeltable0.lof`, strings at 0x85b400..) opened by 0x40d550;
  0x41c0b0 streams it per cell ("No Lane Stream-jc" when missing) and the debug toggle "Render Lane" draws it.
  It holds the **road marking decals** (NIFs using `decal_line.dds` / `decal_marker.dds`) **[exact]**. Layout:
  `"LLF\0" "kjc\0" "ag\0\0"`, u32 stamp 0x01321c14, u32 ?, u32 count, u32 0, then count x {u32 offset, u32 size,
  u32 cell}, then the NIF blobs (empty cells are 256-byte empty NIFs) **[exact for offsets/sizes, cell = inferred]**.
  Not used by the remake yet (markings could be rendered from it like blockObj0.lbf).
- The wrong-way / off-road test in the car update (0x485e64..0x486209) has two sources **[exact]**:
  1. the **AREACHECK road map** `<city>\main\total.hit` when the terrain loaded one - only **taipei** (and taipei
     tracks) ship it, so it is not used in any city the remake plays;
  2. otherwise the **road network of `<city>.tcs`** (the traffic file): a strip per road along its centre line,
     with the lane layout deciding wrong way / off road. This is the road map of moonpalace, koinonia, cras, oros.
- Surface zones: `<city>\Main\exclude.HIT`, `sand.HIT`, `water.HIT` (flattened `.hit` meshes). **Only cras** has
  sand and water; all four open cities have exclude.
- Effects of the flags: off road resets the speed combo every step; off road / sand / water above 150 km/h cut the
  engine and add -5000 N; sand / water spawn splash particles (cras only); off road enables "type 4" ambient sound
  zones. **There is no automatic reset / respawn of an off-road car** (section 7).

## 2. Car update road test (0x4809c0, 0x485df6..0x486209) **[exact]**

Fields: `in[0x228]` raw query result, `in[0x229]` wrong way (`+0x8a4`), `in[0x22a]` off road (`+0x8a8`),
`in[0x22b]` sand (`+0x8ac`), `in[0x22c]` water (`+0x8b0`), `in[0x239]` re-check counter (`+0x8e4`).
Runs when the update's 2nd argument is 0. `T` = terrain object `*(0x4386b0() + 0x128)` (also `*(0x8ec004 + 0x128)`).

```
if map value (0x512ad0()+0x3b4ec) != 0 && T->roadMap.loaded (byte T+0x18):          // AREACHECK path, every step
    in[0x22a] = 0; in[0x229] = 1
    r = T->roadMap.Query(pos in[0xfc..0xfe], heading (cos in[0x148], sin in[0x148]))  // 0x4d6a50, ECX = T+0x18
    in[0x228] = r
    1   -> in[0x229] = 0                       // along the road
    2   -> in[0x229] = 1                       // against
    3,4 -> in[0x229] = 0, in[0x22a] = 1        // off road
    else: in[0x228] = 1; floor ray (game+0x11c, 0x4e5980) from z + 5 down, in[0x228] = w class of the hit
          triangle; if hit && 1 < in[0x228] < 4 -> in[0x229] = 0, in[0x22a] = 1
    (sand / water are not updated on this path)
elif map value != 0 && in[0x239] >= 10 && traffic system loaded (*(0x8ce8c8)+0x1bc):  // .tcs path
    road = TCS.Query(&out, heading, pos)                                              // 0x7fb4d0, ECX = *0x8ce8c8
    if !road:                         in[0x229] = 0, in[0x22a] = 1
    elif out.junction || out.outside: in[0x22a] = 0                    // in[0x229] keeps its value
    else:
        in[0x229] = (out.side == 0 && dist < median + laneW * ((lanesA + lanesB) / 2))     // integer /2
        in[0x22a] = (dist - (lanesA + lanesB + 1) * 0.5 * laneW - median >= 0)
    if in[0x22a] && <stack flag [esp+0x30] == 0>:                       // flag not identified, assumed 0
        exclude zone test (0x4e53d0 index 0); inside -> in[0x22a] = 0
    in[0x239] = (road && !out.junction && out.outside) ? 5 : 0
    in[0x22b] = sand zone test (index 1); in[0x22c] = water zone test (index 2)
    if map value == 0: in[0x22a] = in[0x22b] = in[0x22c] = 0
else:
    in[0x239]++
```
A change of map value (0x485df6) also clears `in[0x22a]` ("CDynamic_Update without Almap" log).

## 3. AREACHECK road map (`total.hit`)

Loaded by 0x754040 at 0x7545fa (`<map dir>\total.hit`, after `\floor_m.hit`, `\wall_m.hit`, `\area.hit`) into
`T+0x18` with 0x4d6cd0; queried by 0x4d6a50. Object at `T+0x18`: byte loaded, +4/+8/+0xc counts, +0x10 vertices
(12 bytes), +0x14 triangles (0x18 bytes), +0x18 nodes (0x28 bytes) **[exact]**.

File **[exact]**: `char[16] "AREACHECK"`, u32 version (0x1328f01), i32 vertexCount, triangleCount, nodeCount;
vertices {f32 x,y,z}; triangles {i32 type, i32 v0,v1,v2} + {f32 dirX, dirY} only when type == 1;
nodes {12 bytes, 12 bytes, i32 child[4], i32 count, i32 tri[count]} where the reader writes the two 12-byte
reads at node+0 and node+8, so memory keeps minX, minY, maxX, maxY (file: minX minY maxX | maxX maxY level?).
taipei: 84394 vertices, 97262 triangles (all type 1, unit dir), 909 nodes, x 8933..14412, y 3916..9229.

Query 0x4d6a50 **[exact]**: node bounds test on x/y; recurse into children > 0 first, the largest child result
wins (and its height); if none is non-zero test the node's own triangles in order with `D3DXIntersectTri`, ray
origin (x, y, z + 5 (0x86d570)), direction (0, 0, -1). First hit: height = barycentric z; type 0 -> 0,
type 1 -> `heading . dir <= 0 ? 2 : 1`, type 2 -> 3, type 3 -> 4, other -> -1. No hit -> 0.

Floor classes (taipei `floor_m.hit` w, version 0x1328f05): -1 (0.14 km2), 0 (0.08), 1 (105.8), 2 (17.1, mostly
inside the road map box), 3 (60.2). 2 and 3 are off road. Consequence of the exact logic: on the AREACHECK path a
spot with no road-map triangle and floor class -1/0/1 reads as **wrong way** (in[0x229] stays 1) **[exact, odd]**.

## 4. The .tcs road network (open cities)

### Lookup 0x7fb4d0 (`out` = {f32 dist, i32 side, f32 foot[3], f32 heading, u8 junction, u8 outside, road*})
1. **Strip hit [exact]**: 0x7f9f30 casts an ODE ray (length 100000, 0x7fb050) from (x, y, z + 7 (0x8603e0))
   down onto the road trimesh `ECX+0x40`; the contact's triangle index -> road whose triangle range contains it
   (0x7fa030, vfunc 0x3c). No contact or no road -> return 0.
   The trimesh (0x7fb050) is one quad strip per road (2 triangles per centre-line segment, segment =
   `(tri - first) / 2`) of total width `2*(median + margin) + (lanesA + lanesB)*laneW + shoulderL + sidewalkL +
   shoulderR + sidewalkR` (vfunc 0x34) **[exact width; centred on the centre line = inferred]**.
2. **Lane direction [exact]**: the segment record of the road's forward lane (0x80d440, 0x38-byte centre-line
   records: +4 pos, +0x10 length, +0x14 dir, +0x20.. tangent, +0x30/+0x34 median widths); if `dir . heading <= 0`
   the reversed lane is used.
3. **Distance [exact]**: 0x7fefd0, nearest perpendicular foot (3D, `0 <= t <= length`) on the centre line from
   up to 4 segments / 10 m (0x85e74c) behind the hit to 2 ahead; `dist = sqrt(min)`. No foot found -> outside.
4. **Side [exact formula]**: `side = ((pos - foot) x laneTangent).z > 0` i.e. right of the lane running the car's
   way (`dx*ty - dy*tx > 0`); `heading = atan2(ty, tx)`.
5. **Outside [exact]**: also when `2*median + (lanesA+lanesB)*laneW + sidewalks/shoulders < dist`, or node checks
   0x7f8a70 / 0x7f8a60 (not identified, assumed false).
6. **Junction [exact test, node choice inferred]**: the start or end node (end when the segment index * 2 >=
   point count), `|pos - node| < radius + 1` and node link count > 2, `radius` (0x7f7430) = max(1, setbacks of the
   roads at that node: startSetback for roads starting there, endSetback for roads ending there).

### Records (reader 0x803e40 -> 0x803640 for NDPATH chunk 0x3100, class table 0x8cb200) **[exact layout]**
Chunk 0x1000 holds 0x2100 nodes ("NDNODE") and 0x3100 roads ("NDPATH"); 0x20200 = ROAD, 0x50200 = CROAD,
0x70200 = PATH, 0x70300 = PATH4S (traffic lanes), 0x90020 dense lane samples. The .tcs stamp (0x13266c7 in all
cities) selects the newest field set. Values are read with format strings ("%d%d%d%f" ...).

| road field (after i32 index, i32 kind, [i32 extra if kind == 1]) | client param | cras road 0 |
|---|---|---|
| i32 ?, i32 startNode, i32 endNode, f32 length | - | 0, 0, 1, 932.0 |
| f32 ?, i32 ?, f32 ?, f32 speed limit (m/s) | - | 1.0, 1, 0, 27.78 |
| f32 margin | [1] | 4.0 |
| f32 shoulderL, sidewalkL, curbL | [0xc], [0xd], [0xe] | 2.0, 7.2, 0.2 |
| f32 shoulderR, sidewalkR, curbR | [0xf], [0x10], [0x11] | 2.0, 7.2, 0.2 |
| f32 lane width, f32 ? | [2], - | 4.0, 0.40 |
| i32 lanesA, i32 lanesB, i32 ? | [3], [4], [5] | 4, 4, 0 |
| f32 startSetback, endSetback (junction radius) | [0xa], [0xb] | 46.4, 58.8 |
| i32 n, n x {f32 x, y, z, f32 medianL, medianR} | [0] = max median | 80 points, 0.4 |

Kind 1 records (one-way roads, e.g. lanes 0+2) carry one more int before the body (0x803e00). Node body:
{i32 id, f32 x, y, z, i32 ?, i32 linkCount, ...} (link count matches the roads using the node) **[inferred names]**.
Roads run from node centre to node centre, so junction areas are covered by the overlapping strips.

### Numbers for a 4+4-lane cras road
strip half-width 29.6 m; wrong way while left of the centre line and closer than 16.4 m; off road from 18.4 m
(two metres beyond the kerb line, i.e. on the sidewalk) and wherever no strip is under the car; junction radius
46..59 m around nodes with 3+ roads (no off road, wrong-way flag frozen).

## 5. `.hit` meshes and the zone manager

File (reader 0x4e5650) **[exact]**: `"hit\0"`, i32 version, i32 indexCount, i32 indices[] (triangle list),
i32 vertexCount, vertices: version < 0x1321a30 -> {x,y,z,w}; 0x1321a30 <= version < 0x1328f05 -> {x,y,z}, w = 0;
version >= 0x1328f05 -> {x,y,z,w} and indices 1/2 of every triangle swapped. Second argument "flatten" sets z = 0.
Then `dGeomTriMeshDataBuildSimple` + `dCreateTriMesh`; HIT object: [0] geom, [1] data, [2] vertices, [3] indices,
[7] ray (created by 0x4e55b0, length 100000 (0x47c35000), `dGeomRaySetParams(0, 0)`, i.e. no back-face culling),
+0x20 file name (0x4e5d20 reloads only on a name change).
Ray test 0x4e5980(out z, point, up, out class): ray from (x, y, z + up) down, up to 10 contacts, keeps the highest
z; class = (int) w of the hit triangle's first vertex. Versions in the data: open cities 0x1321a30 (no w),
taipei floor 0x1328f05 (w classes).

Map loader 0x754040 **[exact]**: `floor_m.hit` -> loader+0x194, `wall_m.hit` -> +0x198, `area.hit` -> +0x19c
(game+0x11c is the floor used by the ground ray 0x475bd0, game+0x124 area **[inferred mapping]**), `total.hit`
-> T+0x18, then 0x4e54e0(map dir) loads `%s\Main\exclude.HIT`, `sand.HIT`, `water.HIT` flattened into T[0..2]
and sets T+0x34 = 1.
Zone test 0x4e53d0(pos, i) **[exact]**: point (x, y, 0), ray from z = 10 (0x41200000) down onto zone i;
`T+0xc+4i` = hit. Index 3 tests all three. Effectively a 2D point-in-triangles test.

Data: cras sand.hit 1809 triangles (x 2329..6612, y 6053..7055), water.hit 1154 (x 2360..6601, y 6080..7415)
along the north shore; exclude.hit in all four cities (oros 134k triangles).

## 6. What the flags do

- **Combo [exact]**: `in[0x22a] != 0` -> `ResetCombo()` every step (0x481910). Wrong way `in[0x229] == 1` above
  100 km/h feeds the reverse combo (COMBO.md 3).
- **Engine cut [exact]** (0x481cf5): if `in[0x22a] || in[0x22b] || in[0x22c]` and speed `in[0x14d] > in[0x5b] *
  150` (= 150 km/h, in[0x5b] = 1/3.6): `F = F - drive(0x476ec0) * throttle(in[0x20a]) + in[0x15]` with in[0x15] =
  -5000 N. So above 150 km/h on sand / water / off road there is no engine thrust (only the boost surplus) and an
  extra 5000 N drag: the car slows to 150 km/h. **Not applied in the port** (DriftCar is off limits here);
  `CarRoadState.SlowSurface` exposes the condition for DriftCar.
- **Network / effect flags [exact]** (0x488c5a): `in[0x22b]` -> vehicle flag 0x2000, `in[0x22c]` -> 0x4000
  (EFFECTS.md had the two names swapped: 0x2000 is sand.hit, 0x4000 water.hit).
- **Ambient sounds [exact gate]** (0x51f400): sound zones of type 4 only play while the local car is off road and
  neither on sand nor in water **[meaning inferred]**. Not ported.
- **Ghost bit [inferred]** (0x478fd0 from 0x483d65, maps where 0x476630(map) fails): off road within 10 m of one
  of 10 fixed points (table 0x860a60, x/y/z/heading, moonpalace coordinates) sets bit 2 of `+0x1ac0`, which skips
  the vehicle-collision setup at 0x483d8b. Not ported.

## 7. Off-road reset

None. The only things keyed on off road are the combo reset, the engine cut, the type-4 ambient sounds and the
ghost bit above. A manual reset/respawn exists elsewhere (DriftCombo notes the body re-creation 0x489850 zeroes
the combo), but it is not triggered by the road flags. Nothing implemented.

## 8. Surface splash (effect slot 6: ctor 0x6b5940, init 0x6b4280, spawn 0x6b61a0, particle 0x6b5c90, update 0x6b5f40, render 0x6b4db0) **[exact]**

- Per frame 0x6b6570: only when the current map name (`game+0xb0`) is `"Cras"` (0x86929c); for every vehicle with
  effects on (`DAT_008d2982` local / `DAT_008d2983` others), then the update.
- Kind: flag 0x2000 (sand) -> 1, else 0x4000 (water) -> 2, else stop (accumulator cleared). Needs speed >= 5 m/s
  (local car: `+0x534`). Interval `max(0.02 - speed * 0.0003, 0.003)` s; while the accumulator exceeds it, every
  wheel emits one particle at the wheel world position + car-rotated (-0.2, 0, -0.2) for even (left) / (0.2, 0,
  -0.2) for odd wheels, shifted back along the frame's movement for sub-frame spawns. Base velocity from 0x6b9610
  (same as tyre smoke).
- Particle (12 floats: life, remaining, alpha, radius, angle, spin, pos, vel):
  sand: life 0.4, radius rand(0.2, 0.8); water: life 0.3, radius rand(0.4, 0.8); both: angle rand(-3, 3), spin
  rand(-1, 1), pos + (rand(-0.1, 0.1), rand(-0.1, 0.1), rand(0, 0.1)), velocity = normalize(rand(-1, 1),
  rand(-0.5, 0.5), sand rand(0.3, 0.5) / water rand(1.0, 1.2)) + base. `rand(a, b) = a + (b - a) * (rand()%100)/100`.
  Cap `500 / 2^q` per kind (q = texture quality), oldest dropped.
- Update: alpha in over 10 % of life, 1 until 70 %, out over the last 30 %; pos += vel * 0.5 * dt; radius += 0.8 dt
  (sand) / 3.0 dt (water); angle += spin dt.
- Render: two 500-quad dynamic meshes, camera-facing quads (corners +-radius along camera right/up, rotated by the
  angle), UVs (0,0)(0,1)(1,0)(1,1), triangles (0,3,2)(0,1,3), vertex colour white * alpha, z-write off, unlit
  vertex colour; sand `effects/sand.dds` blend SRCALPHA/INVSRCALPHA, water `effects/water_tm.dds` SRCALPHA/ONE.
- No sound and no speed change in the effect code (the speed change is the engine cut in section 6).
- Ctor constants 0xa0..0xcc (10, 30, 0.02, 0.1, -0.004, 0.14, 18, 30, 0.01, 0.03, -1/600, 0.06) are copies of the
  tyre-smoke set; only 0.02 / 0.0003 / 0.003 above are used by the splash spawn.

## 9. Port

- `CarRoadState` (added to any car by CarEffects / DriftCombo): FixedUpdate re-evaluates like the client (every
  10 steps, 5 after an "outside" spot, every step with AREACHECK) at the car position with `transform.forward`;
  exposes `WrongWay`, `OffRoad`, `Sand`, `Water`, `SlowSurface`, per-wheel `Wheels[]` (sand/water by wheel position).
- `RoadMapQuery.Load(city)` from `CitySession.Begin` (one line); reads exact paths only (`GameFileSystem.Resolve`
  would substitute another city's `sand.hit`).
- `DriftCombo`: wrong way from `CarRoadState` when the city has road data, else the old lane stand-in; off road
  resets the combo.
- `CarEffects`: sand / water splash particle systems (`CreatePuffs`), emission ported from 0x6b61a0. Size growth is a
  per-system curve from the mean start radius (like the tyre smoke); the "Cras" scene gate is implicit (only cras
  has the zones).
- Harness `roads` results (traffic lane samples every 3rd dense point, heading along the lane): moonpalace 19677
  samples all on a road strip, 0 wrong way, 0 off road (3167 in junctions, 7578 "outside" because the samples sit
  exactly on the centre-line stations of bends); reversed heading: wrong way on 100 % of measurable samples. Same
  pattern for koinonia, cras, oros. AREACHECK: 4999/5000 taipei triangle centroids read "along" with their own
  direction and "against" reversed. Zone centroids test inside 200/200.
- Approximations: the strip hit takes the highest strip at or below z + 7 (the ODE contact order is not
  reproduced) with round joins at bends; the junction node is the nearer end; the stack flag gating the exclude
  test is assumed 0; the TCS centre-line tangent is the segment direction.
