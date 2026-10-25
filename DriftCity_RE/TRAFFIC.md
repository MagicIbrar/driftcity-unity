# Drift City traffic / HUV cars: wheels and ground following

Recovered from `DriftCity.exe` (base 0x400000) with Ghidra; decompiled sources are in `decompiled/phys/0xADDR.c`.
Everything below is read from the code unless marked **(inferred)**. The Unity port is
`DriftCityOffline/Assets/DriftCity/Runtime/Game/TrafficRig.cs` (wheels, ground plane) driven by `TrafficCar` in
`TrafficSystem.cs`; the table data comes from `GameTables.TrafficWheels` / `GameTables.Tire`.

## 1. Big picture

Traffic cars, HUVs, NPCs and player cars are all the same vehicle actor class (the one whose model update is
0x4b0480). The actor holds a copy of its vehiclelist record at `actor+0xe0` (so record offset X is `actor+0xe0+X`;
`actor+0xe4` = Unique Id, `actor+0x250` = TireCnt, `actor+0x254` = RearCnt, `actor+0x258..` = Tire Id_01..Tr10).
Ranges by Unique Id: < 10001 player, 10001..20000 HUV, 20001..30000 traffic (`< 25001` loads from `vehicle\traffic\`,
above from `vehicle\npc\`, 0x46cd20 / 0x464cb0).

None of the wheel data is in the traffic body NIF. It comes from three places:

| what | where | code |
|---|---|---|
| wheel positions | `<model>_dm.nif` next to the body: groups `wheels` (tractor) and `wheels1` (trailer) | 0x464cb0, 0x46a040, 0x469ba0 |
| which tyre on which wheel | vehiclelist `Tire Id_01..Tr10` (`record+0x178 + 4i`), `TireCnt` (`+0x170`), `RearCnt` (`+0x174`) | 0x45fb70 |
| tyre model and size | tirelist (`Init/TireList`): file, `Diameter(mm)`, `Width(mm)` | 0x4b48e0, 0x46c460 |

`fRatio`, `flength`, `rlength`, `length/width/height` are not used for wheel placement (length/width/height are the
hit box, PHYSICS.md section 5).

## 2. Wheel nodes (0x464cb0 -> 0x46a040 -> 0x469ba0)

- 0x464cb0 builds `<dir>\<model>_dm.nif` by replacing ".nif" or ".kfm" with "_dm.nif" (string 0x860178) and
  passes the loaded root to 0x46a040, which creates `v_root` -> `v_body` -> `v_items` and calls 0x469ba0.
- 0x469ba0: `GetObjectByName("wheels")`; if missing logs "no wheels node %d". It creates `v_wheels0` under
  `v_body` and for k = 1..childCount looks up the child named `%02d` (0x860388) and adds a node with **only that
  child's translation** (the dummy nodes' own rotations are ignored; the `wheels` group's transform is ignored).
- `wheels1` (trailers): `v_wheels1` under `v_body` gets the group's translation **and rotation**; its children are
  named on from the tractor: `(TireCnt - RearCnt) + 1 + j`. `in[0x82]` keeps the group's z.
- Wheel index i -> node (0x45c3b0): i < TireCnt - RearCnt -> `v_wheels0[i]`, else `v_wheels1[i - (TireCnt-RearCnt)]`.
- Every traffic / HUV model has a `_dm.nif` (checked against the archive; only the human .kfm rows lack one).
  Example c_t_0_012: wheels at (+-0.579, 0.96, 0.062) and (+-0.577, -1.35, 0.062); c_t_0_010 (truck + trailer):
  tractor 4 wheels, `wheels1` at (0, -1.216, 0.668) with 4 wheels at y -7.40 / -8.54, z -0.578 (relative).

## 3. Tyres

- 0x45fb70: for each wheel i < TireCnt, requests resource type 6 with id `record[0x178 + 4i]`; 0x46c460 loads it
  from `vehicle\player\wheel\` (id < 1001), `vehicle\huv\wheel\` (< 2001), `vehicle\traffic\wheel\` (< 3001), else
  player, + the tirelist file name. The loaded model is attached under the wheel node (0x45fac0 detaches them).
- 0x4b48e0 parses tirelist with "%d %s %d %s %d %s %s %f %f %s %d": record +0 id, **+4 radius = Diameter(mm) *
  0.0005**, **+8 width = Width(mm) * 0.001**. 0x4acea0 / 0x4ad070 copy that 0x208-byte record plus the wheel node
  translation into a per-wheel struct (stride 0x214 at `actor+0x514`): radius of wheel i at `actor+0x518 + 0x214 i`,
  position at `actor+0x71c + 0x214 i`.
- Scale: 0x4ae2d0 scales a tyre (x by width, uniform by radius * 10) only when `actor+0x160 > 0` (0x4b2a10, the
  vehiclelist `Tire Scale Front` path used by player cars). Traffic and HUV rows have 0 there, so their tyre models
  are drawn **unscaled**; the traffic/HUV meshes are modelled at their tirelist size (tire_trf0003: r 0.27 m vs
  546 mm; tire_trf0011: r 0.34 / width 0.44 vs 683 / 435 mm; tire_huv0001: 0.32 * 1.16 vs 760 mm).
- Orientation (0x4b0480, 0x4ae2d0, 0x4ae120): even wheel index (left side: 01, 03, ...) gets `Rz(pi)` (half turn
  about the vertical), odd gets none; spin is an X rotation, negated on the left so both sides roll the same way.
- `vehicle\huv\<model>_tire.nif` files exist but the string "_tire.nif" is not in the exe: HUVs use tirelist id
  1001 (`vehicle\huv\wheel\tire_huv0001.nif`).

## 4. Body height (0x4ae640)

`v_root` at the actor position (ground level), `v_body` translated up by **`radius(wheel 0) - wheel0.z`**, wheel
nodes at their dummy positions inside `v_body`. So every hub is one tyre radius above the ground (artists set the
dummy z so front and rear radii both touch: c_t_0_001 front r 0.352 / z 0.084, rear r 0.342 / z 0.074).
For c_t_0_012: lift 0.273 - 0.062 = 0.211 m.

## 5. Per-frame model update (0x4b0fc0 -> 0x4b0480)

0x4b0fc0 sub-steps: remote actors (type 1, which traffic is in the online game) first dead-reckon their position
and heading from network state (0x4af700: position follows a predicted point, heading `in[0x71c]` SmoothCD, speed
`in[0x6ef] = |v_xy|`), then 0x4b0480 runs (a too-large dt is replaced by 0.05 at its entry; the local car is stepped in 0.01 / 0.02 s slices).

0x4b0480 (`in[]` = actor as float array):
- **Spin**: radius = `actor+0x518` (wheel 0) for HUV/traffic ids (player ids use 0x4b2a50); `d = speed / radius *
  dt`; `in[0x6f1]` (front) and `in[0x6f2]` (rear) += d (or -= d with flag 0x100 in `in[0x720]`, reverse).
- **Steer**: `in[0x71e] = SmoothCD(in[0x71e], half-float at actor+0x1c84, 0.1 s)` (0x476520). That 16-bit value
  is only written by the local player's dynamics (0x4809c0 at 0x488e49, from XiDynamicControl `in[0x20d]`); for
  other actors it can only come from network state. **(inferred)** offline traffic has no steering source; the port
  derives a steering angle from the turn rate (see section 8).
- Tractor wheels i < TireCnt-RearCnt: translation = (wheel.x, wheel.y, wheel.z - clamp(h_k, -0.1, 0.1)) where h_k is
  the suspension height of corner k = i for i <= 3, else 2 + (i odd); rotation = `Rz((even ? -pi : 0) - (i < 2 ?
  steer : 0)) * Rx(even ? -spin : spin)` (front spin for i < 2, rear spin otherwise).
- Trailer wheels: `Rz(even ? -pi : 0) * Rx(+-front spin)`, no suspension offset; then 0x497a30 swings the trailer
  (below).

## 6. Ground following

Two modes, picked per frame in 0x4b0480 (flag at [esp+0x10]); both tilt the car with the ground:

1. **Four-wheel suspension** (0x4a3730, struct at `actor+0x1a24`). Corner probes are wheels 0, 1, n-2, n-1
   (n = TireCnt - RearCnt, 0x4a2270), each moved **0.1 m outward** (`(x > 0 ? -0.1 : 0.1) - x` in the actor's
   forward/left frame), transformed by the car matrix and ray cast (0x475bd0, range 6.0). Per corner, with
   h = height above ground and v its rate (stride 6 floats at `+0x80`):
   - `h -= ground rise`, then `h += sin(pitch) * speed * dt` with pitch = `in[0x15]` = the **ground plane's** pitch
     from the previous frame (`actor in[0x687]`, from 0x4a23d0) in states 0/3, the body pitch `in[0x16]` in 1/2;
   - h >= 0.01 (off the ground): `a = -10 * 1.4` front, `-10` rear;
   - -0.1 < h < 0.01: spring m = 1, d = 10, k = 30 (and zeroing of tiny values below 10 km/h, 0x4768a0);
   - h <= -0.1: m = 1, d = 10, k = 45, floored at -0.2 (0x476840);
   - landing state 3: m = 1, d = 15, k = 500; whole-car states 0 grounded / 1 front up / 2 all four > 0.5 m /
     3 landing exactly as the player (PHYSICS.md section 9); velocities reset when dt > 0.1.
   - 0x4a23d0 builds the ground matrix from the four hit points (lines through the front pair, rear pair, left pair,
     right pair evaluated at the centre lines, then cross products); its pitch is `asin` of the forward z.
   - 0x4a2f70 builds the body matrix the same way from the corners at height `max(h, -0.2) + (radius - wheel0.z)`.
     `v_root` takes the ground matrix (z from `in[0x6d8]`), `v_body` the suspension matrix.
2. **Centre probe** (0x4a22a0): one query under the car centre returns height and ground normal, wheel heights are
   zeroed and the car is aligned to that normal with its heading (used e.g. in mode 9).

## 7. Trailers (0x4ad000, 0x497a30)

The trailer body is a separate node in the body NIF at the `wheels1` position (c_t_0_010 `LODGroup02` at
(0, -1.2, 0.7)). 0x4ad000 gives the trailer length = mean of `-y` of the trailer wheels in `v_wheels1` space;
0x497a30 drags the trailer axle point behind the hitch (re-pulled when it is more than length + 0.5 away) and turns
`v_wheels1` / the trailer by the acos of the hitch-to-axle direction against the tractor's **(the decompile of
0x497a30 is garbled; the pull rule is read from its shape, not traced exactly)**.

## 8. Unity port (TrafficRig.cs)

Exact: `_dm.nif` wheel nodes (translation only, `wheels1` with its own T/R, names `%02d`), tyre per wheel from
vehiclelist + tirelist folders, unscaled tyre models, left wheels turned 180 deg, body lifted by
`radius0 - wheel0.y`, spin `speed / radius0`, corner probes at wheels 0/1/n-2/n-1 moved 0.1 m out, ray range 6 m
(2 up, 4 down), the per-corner spring (10 / 14 m/s^2 off the ground, k 30 / 45, d 10, floor -0.2, 10 ms sub-steps),
the ground-pitch carry term, the axle/side-line plane construction, tractor wheel offsets `-clamp(h, +-0.1)` with
the corner mapping above.

Different / inferred:
- The car stays a kinematic lane follower: x/z and heading come from the lane, y and pitch/roll from the plane
  through the four corner heights (one transform instead of v_root + v_body; same result to first order).
- Whole-car air states (1/2/3, landing spring k 500) and the centre-probe mode are not modelled; traffic never leaves
  the lane graph.
- Steering: **(inferred)** `atan(wheelbase * yawRate / speed)`, clamped to 0.6 rad, smoothed with SmoothDamp 0.1 s
  (the client's SmoothCD time). The client code supports steering for every actor, but offline nothing writes the
  traffic steering value.
- Speed and turn rate for the wheels are taken from the car's own interpolated motion in LateUpdate, so crashed
  (physical) cars roll too.
- Trailer: the axle is kept exactly `length` behind the hitch every frame (simple tow-bar follower), yaw clamped to
  +-80 deg; the trailer does not pitch separately.
- Ground probes: cars more than 160 m from the player probe every 4th physics step and carry the ground along the
  lane slope in between. Hits on anything with a rigidbody (cars) are ignored; with no hit the lane's own slope is used.
- Chase HUVs: ChaseMission hands TrafficSystem a CarFactory visual; TrafficSystem rebuilds it from
  `vehicle\huv\<model>.nif` with the rig above (tyre 1001 on the `_dm` wheel nodes) instead of cloning it.


# Trains, convoys and passersby

Read from `DriftCity.exe` the same way (decompiles in `decompiled/phys/0xADDR.c`). **(inferred)** marks what is not
traced in the code. Unity: `Core/PasserbyFile.cs`, `Core/TrainPath.cs` (parsers and timetables, also run by the harness
mode `extras`), `Runtime/Game/Passerby.cs`, `Runtime/Game/Trains.cs`, `Runtime/Game/Convoys.cs`; tests in
`Tests/TrafficExtrasTests.cs`. Both timetables run on the server clock, so every player sees the same trains and people.

## 9. Trains (Taipei MRT)

- 0x449ac0 (city load) -> 0x4a9b40: when the city index is 5 (Taipei, the only city with the files) it loads
  `World/<city>/Path/trainPath.chpath` with the chpath reader 0x4e94b0 and `trainPath.signal` with 0x77fa40
  (the "trafficSignal" reader). No other city has a train path.
- `.chpath` ("CHPATH\0", version 5, chunk tree): 0x100 {path count}; per path a 0x20000 holding 0x21000 {20010710, 1975,
  0x101}, 0x21100 {2, 0, 0, ?, f32 end time} and 0x22000 {u32 n, n x (f32 t, x, y, z)}. **t is metres / 27.7778**, i.e.
  seconds at 100 km/h (the same unit as the pcs time keys; 0x7f3a60 multiplies by 27.7777786, speeds are scaled by
  0.036). trainpath: path 0 = 61 keys, 7181 m, end 258.97; path 1 = the opposite direction, 74 keys, 258.61. Both run
  between (10247, 8381) and (11604, 2804), z 5-45 m (on the viaduct).
- `.signal` (text): "trafficSignal", version 20050508, path count, then per path a count and lines `type path time`
  (the file's comment: signal type stop/run, path number, position as time). trainpath: path 0 stops at 33.38, 68.18,
  105.75; path 1 at 155.69, 193.39, 228.46 (all type 0 = stop).
- 0x4aa4b0: per path one line object with **3 trains** (0x4a9a00 each). 0x4a9420 sets the constants: cruise speed
  in[3] = 27.7778 m/s, dwell in[5] = 8 s, braking distance in[6] = 300 m, run time in[2] = the path's end key time; the
  path itself is an NiPathInterpolator (follow on, axis Y, smoothing 0.2). 0x4a99cf / 0x4a99d6: **6 cars, 12 m
  apart** along the path (0x4a92e0 puts car k at path time pos - k x 12 x 0.036).
- Timetable 0x4a9c90 (exact): game time = server milliseconds mod (period x 1000), / 1000; train k uses
  fmod(k x runTime / 3 + time, period) with period = stops x (2V/A + W) + runTime. Per stop: cruise at one time key per
  second up to (stop - 10.8), brake linearly for 2V/A = 21.6 s (speed = remaining / 21.6 x A, position =
  (300 - remaining x speed / 2) x 0.036 + stop - 10.8), wait 8 s, accelerate out the same way; after the last stop cruise
  to the path end, then the cycle restarts at t = 0.
- Cars: actor type 4 of vehiclelist **20505 "mrt" (`vehicle\traffic\c_t_5_mrt.nif`, 15.2 x 2.4 x 3.0 m)**, created when
  the train becomes visible (0x4a9140) and removed when it is not (0x4a90d0). Visibility (0x4a91c0): shown while
  2 x 6 x 12 m + distance(train, camera) <= view option x 200 m (DAT_008d297c, the graphics view-distance option, 1-4).
  Each car is dropped onto the ground within 7 m (0x4e5980); **(inferred)** the port uses the path height directly.
- Port: `Trains.Attach` builds the 6 trains of 6 kinematic, box-collider cars; `TrainSchedule.Position` is the
  timetable above (the test checks 10 s of cruise = 10 keys = 278 m and the 8 s stop at 33.38). A car hitting a train
  meets an immovable kinematic body **(inferred: the client's type-4 actor is not pushed)**.

## 10. Passersby (pedestrians)

"Passerby" is the city's **pedestrians**, not cars: actors of vehiclelist 25002 (`vehicle\npc\f00.kfm`, woman) and
25003 (`m00.kfm`, man), skinned bodies with .kf animations (walk01, idle01, run01, avoid01, hit, ...). The server feature
flag "Main.PasserBy" (string at 0x85ebd8, read at 0x45360b) and game state 14 (open city) gate them; Cras, Koinonia,
Moonpalace and Oros ship a `passerby.pcs`, Taipei does not.

- Loading (0x449ac0): `World/<city>/passerby.pcs` -> 0x7e93a0 (reader; its chunk table at 0x8caee8 names every chunk),
  then 0x49a140 -> 0x7e9cf0 creates the walkers.
- `.pcs` ("PCS\0" + "Passerby-CONTROL-SYSTEM", version 5): TIMEKEYDATA 0x12000 {id, n, n x (t, pos, 3 axes, quat)} =
  walking lines with time keys in the same 27.7778 m unit; PATHDATA 0x32000 {id, timekey, footway, joint a, joint b};
  FOOTWAYDATA 0x42000 {id, kind, path a, path b, link a, link b, owner, owner kind} (kinds 1-5; **4 and 5 are road
  crossings**, 0x7f3a90); CROSSWALKDATA 0x52000 {id, a, b, f, pos}; JOINTDATA, CROSSDATA, ROADDATA (junction tables);
  LCPATHDATA 0x82000 {kind, length m, n, n x (start, end, path)} = closed routes (along one pavement and back along the
  other, or over a crossing and back). Every route piece is exactly as long as its line. Cras: 2366 lines, 1183
  footways, 281 crosswalks, 587 routes (281 crossings).
- Placement 0x7e9cf0 (exact): density setting 0/1/2 -> spacing 1500/700/400 m; on every non-crossing route piece of at
  least 4 m, max(1, length / spacing) walkers at b x spacing + kind offset (kind 2: 7 m, kind 3: 14 m); then each crossing
  gets a walker at its start when the running count is even and one half way when it is odd (both when the crossing is
  over 40 m). Types alternate woman / man with the count's parity. At density 0: Cras 1830 walkers (562 on crossings),
  Koinonia 2794, Moonpalace 2738, Oros 754.
- Looks 0x7eb0d0 / 0x45f660: four parts (slots 0xc-0xf; **(inferred)** hair, face, upper body, lower body, from the
  part files' mesh names and the table: slot 1 is always part 1) picked from a 21-row round-robin table at 0x8cb078; part
  p is `<f00|m00>_parts0<(p-1)%3>.nif` (the exe's file list at 0x8605e0); hair index 1 -> 2 for women and 2 -> 0 for men
  (m00_parts02's hair is a helmet). Three colour indices per part come from rand() (0x7eac60, see the tints below).
- Colour tints (exact, 0x45f0a0 -> 0x45d8a0 -> 0x7eab00, shader `shader\agthuman.fx`, now in `DriftCity_RE/shaders`):
  every part NIF uses the techniques AgtHuman / AgtHuman_A / AgtHuman_RGB with a mask texture (`upper_mask.dds`,
  `lower_mask.dds`, `F01_hair_mask.dds` ...). PS_RGB tints the decal where the mask's R / G / B channel is above 0.1 with
  the material's ambient / diffuse / specular colour (times the mask value), then multiplies by the toon ramp looked up
  with `dot(N.L, (0.499, 0.787, 0.314))`. 0x45d8a0 walks the part's geometry and, when the card has vertex and pixel
  shaders 2.0 (0x42dfc0), sets each NiMaterialProperty's ambient / diffuse / specular from the colour table 0x8cee40:
  24 records of 0x80 bytes (part file x 4 + slot), each four NiTArrays of packed colours (0x7ea970 unpacks one): a
  shared list at +0x14 (count +0x18) used for all three, else separate lists at +0x34 / +0x54 / +0x74. 0x7eac60 picks
  `rand % count` (+100 and +200 for the second and third index), or 0xff when a list is empty, and 0xff gives black.
  **Nothing in the exe fills that table**: its only references are the ten lookups (`mov ecx, 0x8cee40`), it lies in
  the zero-filled part of the data section, and no initializer or file loader writes it (the colour lists are not in
  passerby.pcs, whose chunk table 0x8caee8 has no colour chunk). So in this build every index is 0xff and, on a
  shader-2.0 card, every masked region would be multiplied by black. Remake rule: the port keeps the parts untinted
  (white) and does not reproduce the black masks; a palette for the table would have to come from elsewhere.
- Speeds (0x7e97b0, per type, m/s): slot 1 stand 0, slot 2 walk 1.45, slot 3 run 10, slot 4 5 (x 0.036 = keys/s).
- Timetable 0x7ea2f0 (exact): time = server ms mod 36,000,000, / 1000 (0x49b6e0). Pavement walker with id i walks
  {20,25,...,80}[i%13] s then stands {5,6,7,8,9,10,10,5,5,10,10,10,10}[i%13] s; a crosser walks half its route (one way)
  and waits {15,36,27,48,22,12,19,15,55,5,8,13,21}[i%13] s. Position = phase x speed + cycle start x average speed +
  start offset, modulo the route length (state 2 walking, 1 standing).
- Actors (0x49a200, 0x49a500, 0x49aad0, 0x49a940): only walkers on the road pieces around the player within
  100 + 15 x view option metres become actors (type 3); the rest are dropped. **(inferred)** the port uses a plain
  radius and a cap of 48 actors.
- Cars: pedestrians never collide. 0x49aad0 state 0 checks the drivable cars: within the car's half length + 0.7 m, or
  ahead of it within half its width + 2 m sideways, the pedestrian jumps aside (state 1) perpendicular to the car's
  heading, away from it, at the slot-3 speed (10 m/s) with ground snapping (7 m); past 9 m it switches to state 2 and runs
  back to its timetable spot, then walks on. **(inferred)** the look-ahead term (actor fields 0x8c x 0x87) is taken as
  half a second at the car's speed; the animation per state (avoid01 / run01 / walk01 / idle01) is inferred.
- Port: `Passerby.Attach`. The bipeds face -Y, so the body is turned 180 degrees to its walking direction. The view
  option is goption.ini `[Graphic] VisibleRange` (DAT_008d297c = option object 0x8d2850 + 0x12c, read clamped to 1..5;
  `Trains.ViewOption` reads `GameOptions.VisibleRange`, default 5): pedestrians within 175 m, trains within 1000 m. Skinning from NiSkinData (4 strongest weights; bind poses from the part file's rest pose).
  .kf playback including compressed B-splines (`NiBSplineCompTransformInterpolator`: cubic open-uniform basis, short
  control points x half range + offset). The .kf quaternions are the **transpose** of the matrices the NIFs store
  (walk01 at t = 0 equals every bone's rest rotation once transposed; harness mode `kf`). The accumulation root (Bip01)
  track is a constant identity; Bip01 keeps its rest pose and the actor moves. NifBlocks now reads NiControllerSequence
  without the Bethesda-only priority byte (the KF files are 20.0.0.4, user version 0), the string palette, NiSkinData
  weights and the B-spline blocks (the NIF parse count over the archive is unchanged, 3529 / 3546).

## 11. Convoys (boss HUV escorts)

- 0x4a1470 handles the server's boss-HUV create: ids 10001-10004 are escorts of boss 11001 (0x2af9), ids 11005 / 11006
  (0x2afd / 0x2afe) of 11004 (0x2afc). 0x4a12e0 finds the leader (0x49ef60), creates the escort at the leader's position
  and rotation through 0x4a0f10 (BossHuvInfo table; failure logs "Create Convoy Fail: %u"), sets AI fields +0x158 = 5000
  (5000.0) and +0x154 (the chase clock), then 0x4912d0(v, time): 0x496550 stores v in AI +0x16c, 0x496560 the target
  +0x20, AI state +0x30 = 2, actor+0x1dc8 = 1 (AI-driven member), +0x1dc4 = the packet's time. v = 40, for the escort
  with id 11002 52.8 / cos 10 deg = 53.6.
- **Correction (this round)**: +0x16c is not a follow distance. The mover update 0x4967c0 integrates +0x16c as the
  current speed (accelerates by +0x30 x dt toward the target +0x20, brakes, never below 0), 0x496500 derives a time
  from it, and the party HUV spawn 0x4f0940 calls the same 0x4912d0 with 0 (a standing start). So an escort starts
  already moving at 40 (53.6 for 11002, which keeps up while driving 10 degrees off the leader's line: v / cos 10)
  in the units of the mover's speed (**(inferred)** m/s, 144 km/h). The follow logic is decoded in section 15.
- **Correction (this round)**: only 10001-10004 come at once (0x4a1470 -> 0x4a12e0). 11002 (Lil' Grimer), 11005 and
  11006 are *kept*: 0x4a1470 copies their packets into the boss (11001 +0x1e54; 11004 +0x1e54 / +0x1e78) and the boss's
  damage stages release them: 0x4a1860 (11001: damage +0x1db8 over the threshold +0x88 -> trailer stages -> 0x4a16c0
  spawns Lil' Grimer at the trailer node with v = 53.6; 10 s later the truck itself is finished, damage = max + 10) and
  0x4a1d10 (11004: the same threshold -> 0x4a16c0 twice, the "baby dozers"). 0x4a16c0 is 0x4a12e0's twin (create at a
  node's position and heading, +0x158 = 5000, +0x154 = the chase clock, 0x4912d0(40 or 53.6)).
- **The threshold +0x88 (exact, this round)**: the boss object sits at actor +0x1e14 (its update 0x4a1ed0 from the actor
  update 0x490c73; [0] = the actor), so +0x88 is actor +0x1e9c, written once by 0x4a0f10 (0x4a1120): `record+0x98 x
  record+0x54 x 0.01`. The BossHuvInfo record (reader 0x452610, format "%d %s %s %d %f ...": +0 ID, +4 string id, +0x14
  name, Support skipped, then +0x54 내구력, +0x58 경험치, +0x5c 속도 .. +0x7c 성공여부, +0x80 펄스간격, +0x84 이벤트,
  +0x88..+0x94 the APC laser / smoke columns, **+0x98 컨버팅시간** (column 22)): the threshold is 컨버팅시간 % of the
  boss's 내구력 as damage: 11001 and 11004 40 % (560 of 1400, 240 of 600), every other row 100 (never before the catch).
- **The kept escorts' timeline (exact)**: 11001 (0x4a1860): damage >= threshold -> stage 1 (0x49f2a0, the trailer packet,
  +3 s) -> stage 2 (0x49f340, +3 s) -> stage 3 -> 0x49f3c0 asks for Lil' Grimer with its own 내구력 (300; +2.367 s) -> it
  comes out at the trailer node (0x4a16c0, 52.8 / cos 10 deg) and the trailer explodes (effect 0x10) -> 10 s later the
  truck is finished (damage = max + 10, reported by 0x4e27c0) and Lil' Grimer's +0x724 (out of the trailer) is set.
  11004 (0x4a1d10): threshold -> 0x49f4d0 (+2 s) -> 0x49f570 asks for 11005 / 11006 (+3 s) -> both come out (0x4a16c0
  twice), effect 10, UI 0x697ad0(4); the dozer itself goes on.
- Port: `Convoy.Attach(traffic, leader, leaderId)` (section 15): 11001's four escorts at once, 11004's two at its
  threshold (`OnLeaderHealth` -> `ChaseRules.SplitDue`: BossHuvInfo 컨버팅시간, `HuvRoutes.BossInfo.ConvertPercent`) after
  the 2 s + 3 s of stages (`SplitNow`), each starting at `StartSpeedFor` (40 / 53.6 m/s), cruising at its BossHuvInfo speed
  and driving the boss's lanes; `Release()` when the boss is caught or escapes. `ChaseMission` runs 11001's timeline:
  Lil' Grimer 8.367 s after the threshold as a target of its own (health = the boss's x 300 / 1400), the truck finished
  10 s later (stopped, its escorts let go), Lil' Grimer then always in the close pose; caught before Lil' Grimer came out,
  it stays in the trailer **(remake rule)**. Party chases follow the server's `chase.party.split` (section 14).

## 12. World streaming: baked ground shadows and the distance window

- **shadow.dat** (exact; 0x40c410 -> 0x40c130, "Decal Shadow Loading" in the land thread 0x413690): `<city>\main\shadow.dat`
  (tracks: `<city>\trackN\shadow.dat`, plus `..\main\shadow.dat` when its stamp is above 0x20090404) = u32 stamp
  (0x20060621 in all files), two u32 (0, 208) when the stamp is above 0x20060620, u32 count, then one Gamebryo 20.0.0.4
  stream. Its first `count` objects are NiNodes named "%d %d" (second number = block index; negative skipped; unknown
  -> the block under the node's bound centre, 0x40d1e0), each one NiTriShape with its own embedded DXT5 texture
  (128 or 256 px; RGB white outside, grey about 145 inside a silhouette whose alpha is 255), NiAlphaProperty
  SRCALPHA / INVSRCALPHA, a z-buffer property and no material. The pieces are the receiving surfaces (ground,
  pavements, walls) under the buildings' and arches' shadows; they are attached to their block's node and stream with
  it. Cras 548 pieces, Koinonia 1688, Moon Palace 1757, Oros 1370. Not a lightmap and not a shadow map.
- Port: `CityLoader.LoadGroundShadows` loads them as "Ground Shadows" (no colliders, no shadow casting, 3 cm up like
  the road markings, material 0.5 grey = Gamebryo's default material **(inferred)**). Test `CityStreamingTests`.
- **The window** (exact): 0x415e80 creates the land manager and 0x40e9a0(VisibleRange) sizes its five cell arrays
  (2r + 1)^2 for r = VisibleRange (1..5) and sets the terrain detail (+0x25c = 1 for r = 1, else 2): terrain cells,
  blocks and their props / shadows are kept for the (2r + 1) x (2r + 1) window of 200 m cells around the camera.
  Cars: the per-frame cull 0x423000 culls the world (0x4baee0) and the actor lists; 0x4baee0 sets the far plane of the
  actor cull frustum (0x4b4dd0 / 0x4b4e30) to (r - 1) x 200 + 100 m in the open city (state 0x0e) and (r - 1) x 50 + 30 m
  in state 9; an actor outside it is not drawn. Objects that come into view fade in when r > 2 (0x41a610 / 0x40c8f0:
  blending on, material alpha ramps up, restarting when the object was not drawn for 2 s) and their vertex buffers are
  built lazily, at most 10 ms a frame. Vehicle LOD distances (0x46a600, **(inferred)** a detail option x <= 3):
  10 + 7x, then + 10x, then + 13x.
- Port: `CityStreaming` (attached by CityLoader): renderers are grouped by 200 m cell of the terrain grid and those
  outside the window get `forceRenderingOff` (colliders stay; Renderer.enabled is left to effects). It follows the
  game camera and `GameOptions.VisibleRange`. Fade-in (Data tables round, TABLES.md): fade object 0x40c840 = alpha 0,
  rate 5.0 / s, cap 1.0; 0x40c8a0 restarts at 0 when not drawn for > 2.0 s [exact]; the remake fades per 200 m cell
  (Round 4: per object like the client - each renderer of a window cell keeps its own last-seen time from a cached-bounds
  frustum test inside the cells in view; a fading renderer gets _DCObjFadeOut by property block, DCOriginal.cginc takes the
  minimum with the cell alpha. `CityStreaming.PerObject`, default on; cost in Preview/streaming_fade_perf.txt.)
  (entering the window, or the camera frustum after > 2 s away) with a screen-door dither in NiStandard (world),
  AGTree and DCToon driven by one global array (`CityStreaming.UpdateFade`, DCOriginal.cginc `DCFadeAlpha`); terrain
  and cars do not fade. Cost (Cras, r = 5): Camera.Render 2.34 ms fade off / 2.25 ms on (noise), bookkeeping
  0.06-0.08 ms a frame (`CityStreamingTests.CrasFadeIn`). Not reproduced: the lazy buffers, the actor far plane
  (`CityStreaming.ActorDrawDistance` gives it), terrain detail levels.
- Performance (Cras, PlayMode, the editor in batch mode with the GPU; 48,720 city renderers; frame pacing near 60 Hz,
  so the CPU cost of one synchronous `Camera.Render` of the game camera is the useful number):

  | case | Camera.Render median (two runs) | city renderers drawn |
  |---|---|---|
  | before (no ground shadows, no window) | 1.84 / 2.56 ms | 48,720 |
  | ground shadows, window off | 1.83 / 2.26 ms | 48,720 |
  | VisibleRange 5 (default) | 1.78 / 2.25 ms | 6,317 |
  | VisibleRange 3 | 1.79 / 1.02 ms | 2,502 |
  | VisibleRange 1 | 1.32 / 0.45 ms | 427 |

  The ground shadows cost nothing measurable. At the default r = 5 the window (+-1000..1100 m) lies beyond the 512 m
  camera far plane, so Unity's own frustum culling already skips the same objects and the window saves 0..0.05 ms; only
  the low settings gain (and look like the client's). The remake does not need disk streaming: the whole city is
  resident; memory, not draw cost, is what 48,720 renderers spend.

## 13. PvP track traffic and the path signals

- Count (exact; race object 0x72d250 state 6 -> 0x73ad90 -> 0x71fc40): density from the room's TRAFFIC buttons
  (RANDOM rand() % 3, NORMAL 1, JAM 2, anything else incl. IDLE 0); `data\pvptrafficnum.dat` (reader 0x73aaa0) holds
  per "Track &lt;city&gt; &lt;track&gt;" three rows (jam, normal, idle) of up to 32 counts indexed by the number of drivers in the
  room (low five bits). The rows are read with "%s %d x32", so each line's first number is eaten and column n is the
  (n + 2)-th number. An unlisted track gives (density + 1) x 100. The room's own count (+0x394) wins when set; the
  count is halved when the stage's option flag +0x1400 is 0; the host sends (city, track, count, path file, seed) to the
  server (0x79b500). Example: Track 1 1, 2 drivers: jam 102, normal 51, idle 25 cars.
- The cars are the server's: it drives them along `World/<city>/Path/PVP_map<city>_<NN>.chpath` (loader 0x49c410 also
  loads `path_<city>.chpath` for the open city and `Laptime<city>_<NN>.chpath`), and the client shows them as remote
  actors (dead reckoning, section 5). The `.signal` files next to the paths (`path_taipei.signal` 136 paths,
  `PVP_map5_01/02/03/05/07.signal`; format as trainPath.signal, types 0 = stop and 2, positions as path time) are
  **not read by the client**: the only `.signal` it opens is trainPath.signal (0x4a9b40). The `.index` files the
  networking code looks for (0x7801b0: `pathIndex_%d.txt`, `PVP_map%d_%02d.index`) are not shipped. So the path
  signals were server data.
- The PvP paths (exact data, ChPathFile): `cras\path\pvp_map3_01.chpath` holds 24 open polylines (0x20000 chunks): the
  roads of the track in both directions (pairs with swapped ends, 1-2.4 km, plus short links of 300-400 m), time keys in
  metres / 27.78 from 0.
- Count port: `PvpTraffic.Count(city, track, drivers, density)` and `PvpTraffic.DensityFor(mapFlags)` (test
  `TrafficExtrasTests.PvpTrafficCounts`); `PvpTraffic.CountFor(city, track, drivers, mapFlags, seed)` rolls RANDOM from
  the race seed (every client the same).
- Cars port: `PvpPathTraffic.Start(traffic, city, track, count, seed, clock)` (PvpTraffic.cs). **(remake rule)** No
  server drives them: every client runs one schedule from the seed and the race clock, so all clients see the same
  cars with no network traffic. Car i takes a path drawn by length, an offset on it and the path's speed (45-75 km/h,
  one per path so cars never run into each other), restarts at the path's start at its end, and is hidden for the
  first and last 25 m (where the server's cars came and went). Bodies (the city's traffic models, `PathCar`: kinematic,
  TrafficRig wheels and ground) exist only within 420 m of the player, at most 48 (the client drew remote actors only
  within its view). A car rammed over 9 m/s is knocked about for 4 s, then goes back to its scheduled place.
  `PvpPathTraffic.SeedFor(roomId, track, drivers)` is a seed all clients of an online race derive alike. The city's
  lane traffic is switched off for the race (hook lines in the report: RaceMission / OnlineRace are not this file's).

## 14. Party HUVs (exact; BS_PktPartyHuv 0xc29 -> 0x4f45b0 -> 0x4f0940)

The party chase HUV is simulated by **one** client: the packet names an owner. When the owner is me ("myhuv" in the
debug output) the client creates the HUV with its AI: it takes an AI from the pool (0x44a110 -> AI init 0x491420;
"Already huv have control" when the actor has one), copies the packet's 15 AI parameters (actor +0x1dd4.. -> AI +0x34..,
speeds km/h x 0.2778), places it at slot (packet & 15) of the current chase spawn point (0x8ce6f4 + point x 500: slot
positions +0x10, rotations +0xd0), plans the slot's route (0x44a940, section 15), AI +0x158 = the slot, +0x154 = slot
x 3600 / (speed x 1000) + 10 s, then 0x4912d0(0, now) (state 2 from a standing start). Otherwise ("Ohuv") the HUV is a
remote actor dead-reckoned from the owner's updates (0x4af980, actor+0x1dc8 = 1). The chase start 0x4f2500 (solo, up to
16 HUVs, section 1 of CHASES.md) goes through 0x4f0410 the same way, and 0x4f2c00 through 0x4f0e90; boss HUVs through
0x4a0f10 (owner test at +0x10 of the packet, "MyHuv" debug line).

- **The Undercity's HUV waves are party HUVs too** **(inferred)**: the run leader asks for the wave (0x718e40: 0xc2 +
  0xbe), so the server's party HUV packets name the run leader as their owner.
- Port (party chase): server `src/partyplay.js` names the owner in `chase.party.start {owner}` (the leader). The owner's
  `ChaseMission` runs the HUV (and a boss's escorts) and sends `chase.party.huv {cars: [[x, y, z, yaw, speed], ...]}` five
  times a second (Unity space; the HUV, then its escorts); the server relays it to the other members (at most 12 a second,
  24 cars). The others' copies are `TrafficCar.SetRemote(true)`: hidden until the first state, then carried forward at
  the owner's speed along its heading (at most 1.5 s), pulled in smoothly (snapped beyond 40 m), grounded by the rig,
  never knocked out of line; their hits still go to the server's shared health. When the owner leaves, the next member
  still in it becomes the owner (`chase.party.owner {owner}`) and its client takes the copies over from where they are
  (`SetRemote(false)`: the nearest lane, a new route). Several HUVs (the server's units, CHASES.md section 2) are shared
  the same way, each with its own health on the server (`chase.party.hit {unit}`); a boss's kept HUVs come out on the
  server's `chase.party.split` (Lil' Grimer appended as a unit, the dozers as escorts, the owner runs them).
- Port (Rush Time, the Virtual HUV): the boss of a server-run Rush Time is a party HUV of the first driver in the zone
  (`rush.huv`), a V-HUV stand-in's HUV car one of the match's first driver (`vhuv.huv`), CHASES.md sections 4-5.
- Port (Undercity): `UndercityStage.Online.cs`: the run leader's client (`HuvOwner`) runs the wave and sends
  `uc.huvpos {wave, cars}`; server `src/undercity.js` relays `party.uc.huvpos {wave, cars, from}` to the other drivers
  (only from the first driver still in the run, only for the current wave, at most 12 a second); their HUVs are remote
  copies; a new run leader takes them over.

## 15. The HUV AI (exact; DriftCity.exe 0x443000-0x44b000, 0x48a000-0x4a2000, 0x493000-0x498000)

- **Classes.** The AI driver base class (vtable 0x85e760; ctor 0x444bf0, reset 0x444170, base init 0x445410) is the NPC
  driver shared with racers and traffic; the HUV AI derives from it (vtable 0x861694, ctors 0x491130 / 0x491380, init
  0x491420, the per-frame state machine 0x492830 / 0x492be0 / 0x492d50). Its mover (the path follower, AI +0x334, class
  0x493000-0x498000) integrates the speed (+0x16c, section 11) toward a target (+0x20) and follows the current road route
  (+0x78: road element +0x7e, road +0x7f, lane +0x7a / +0x79). The HUV manager (0x449ac0 loads the city: Huv00.xipath2,
  passerby.pcs, trains) keeps the AI pool (+4 / +8; 0x44a110 takes one, vtable +0x14 gives it back 0x44a180).
- **AI parameters** (AI +0x34.., copied from actor +0x1dd4.. by 0x4a0f10 / 0x4f0940): +0x38 cruise (BossHuvInfo 속도 km/h
  x 0.2778), +0x3c top speed (최고속도), +0x40 (부딪혔을 때 가속, x 0.2778), +0x44 the burst time (부딪혔을 때 가속 시간),
  +0x48 = 10 s, +0x4c the turbo chance (터보사용빈도 % x 0.01), +0x50..+0x58 the three avoid levels (0 weak, 1 medium, 2
  strong). BossHuvInfo (`init\bosshuvinfo.xlt`, 0x452b10): bosses 11001-11008 and the escorts 10001-10004 (150 / 180 km/h,
  20 hits), plus three special blocks (11009 hovercraft, 11010 DoubleUpset, 11011 tank, 11012 cannon).
- **States** (AI +0x30, 0x492830): 1 idle; **2 started** (0x4912d0: v into the mover's speed, the target = +0x38,
  actor+0x1dc8 = 1) -> the next frame **3 driving** (the clock +0x574 = 0). In 1 / 3: every +0x48 = 10 s a roll of
  `rand % 1000 / 1000 < +0x4c` starts the burst (0x4934c0, +0x570 = +0x44 s) unless a check on the target car (vtable
  0x1dc) refuses; damage taken (actor +0x1dbc + +0x1dc0) is added to +0x1db8 and also starts the burst; when the HUV is
  the chase target and its health left (+0x1db0 - +0x1db8) is at or below the manager's +0xe4, or the damage reaches
  +0x1db0: caught (0x4e1fa0(actor, 1), packet 0x788e50(target, 10.0)), the followers are let go (0x444f30), state 5; the
  chase clock below 0: escaped (0x4e1fa0(actor, 0)), followers let go, state 4. 4 -> 1 (actor+0x1dc8 = 3); 5 -> 6 (mover
  stopped, 0x497690); 6 waits 3.333 s -> 7; 7 -> 1 with effects 0xe / 0xc.
- **Pose levels** (0x492be0, 0x492d50, state 3; **correction this round**: not a dodging driver): 0x492be0 picks the
  mode +0x728 from the squared distance between the HUV (AI +0x3a0) and the player's car, only while the frame time
  (0x8bd764 +0xc) is under 0.08 s: under 100 m (1e4) 2, 100-200 m (4e4) 1, else 0; 11002 with +0x724 set (out of the
  trailer) is always 2. 0x492d50 applies it (skipped for 11009 the hovercraft, actor states +0x2118 1-3 and a running
  +0x2140 timer, which keep mode 0): **mode 2** = 0x491720: 0x4a2960 probes the ground under the four wheels, 0x4a23d0
  builds the body matrix from the four hit points (section 6), pitch / roll by `asin`, the wheel nodes with their
  suspension (+-0.1) and steering; **mode 1** = 0x492180: one probe under the centre (0x4a22a0: a ray of 6 m, then 10000)
  and the body aligned to that normal with its heading (+0x38c), the body height from the tyre radius; **mode 0** =
  0x49ea00: the path follower's own frame (mover +0x6c / +0x78) with the body lift, no ground probe. The driving (the
  mover, 0x494b80) is the same in every mode.
- **Routes.** A chase HUV (0x44a5d0 -> 0x447ed0 -> 0x447ff0 -> 0x44a940): the chasespawnlist points sorted by distance
  from the player (500-byte records at manager +0x83c); the start is the first beyond 1000 m (`d^2 > 1e6`), the
  candidates the next ones up to 3000 m (`9e6`; `25e6` = 5000 m when that leaves fewer than two; fewer still: "Tool
  Small Spawn %d"); the goal (+0x85c) is a random candidate taken out of the pool, then 16 random waypoints (+0x864) from
  the rest. HUV k: from slot k's position and heading (column 1 of its rotation) to waypoint k's pivot, then to the
  goal's pivot, both legs planned on the road graph (0x7fd1d0 nearest road node, 0x7ff650 path with the start heading,
  0x7ff950 store: legs +0x930 / +0x974) and joined into +0x9b8 for the AI's route (+0xac). A boss (0x4a1470 -> 0x44ab40)
  drives route (packet path index % count) of `<city>\Huv00.xipath2` from its first point facing the second. The file:
  "XiPATH2\0" + stamp + "NHN-AG\0jc\0", u32 5, u32 size, chunk 0x1000 {route count}, per route 0x1100 {n, n x (x, y,
  z)}; the reader is 0x4e9000 with the chunk table 0x8be130 (0x1000 -> 0x4e8d90, 0x1100 -> 0x4e9120 -> 0x44cab0: each
  route becomes a road route named "%dth" in the list at 0x8ce6d0). Cras 5 routes (17 / 14 / 15 / 9 / 12 points),
  Koinonia, Moon Palace and Oros have theirs, Taipei none. 0x4442b0(point) plans from where the AI is to a point (used
  when followers are let go, and for 11002).
- **Follow (convoys).** 0x444fb0: the escort AI joins the first boss AI without a leader (+0x548 = leader, the leader's
  list +0x550 / +0x554); 0x445090: the follower copies the leader's route index (+0xac), its route lists (+0xb0 /
  +0xd0) and the leader's place on it (0x4955c0: road element, road, lane, side); the leader's update 0x445130 (states 3
  and 10) re-syncs every follower whenever it re-plans and sets each follower's target to its own cruise. There is no
  gap: a follower drives the boss's route at its own speed, starting at 40 m/s (53.6 for 11002).
- **Port.** `HuvRoutes` (xipath2 reader, BossHuvInfo, `PlanChase` = 0x44a5d0 / 0x447ed0, lane routes by shortest
  length through points, `LaneAt` with the heading); `TrafficCar.Route` / `Replan` (lanes planned ahead, re-planned after a
  crash or at a dead end), `Boost` (the burst: top speed for BossHuvInfo's burst time when hit and on the 10-second
  turbo roll); `HuvTarget.SpawnOnRoute`; `ChaseMission` spawns the chase's units (the server's count and speeds, CHASES.md
  section 2; up to 16, all must be stopped, CHASEPLAYINFO counts those left) on slot k
  with route slot -> waypoint k -> goal, then on to spawn points 1-3 km on (**(remake rule)** the exe's route ends at the
  goal); a boss on an xipath2 route passing 250-1200 m from the player (**(remake rule)**: offline there is no server
  to pick the index), looping round it. The HUVs, their speeds and the time limit are the server's (CHASES.md section 2:
  `src/huvrules.js` / `ChaseRules`; `ChaseMission.PackSize` only fixes the count for tests); a convoy chase sends all 16
  slots in a row, each HUV keeping out of the one ahead (`TrafficCar.Ahead`).
- **Pose port**: `TrafficCar.HuvPose` (set for every HUV and escort; `PathPoseOnly` for 11009, `ClosePose` for Lil' Grimer
  out of the trailer): `PoseModeFor(distance, frame time)` = 0x492be0, `HuvFollow` = 0x492d50: mode 2 the four-corner
  `TrafficRig` springs every step, mode 1 one centre ray (the body on its normal), mode 0 the lane's own point and slope;
  `PoseMode` tells the last one (test `HuvEventsTests`). Remote copies use the same levels.
