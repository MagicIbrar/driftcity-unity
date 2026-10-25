# Drift City (SkidRush) player car physics

Recovered from `DriftCity.exe` (image base 0x400000) with Ghidra. Decompiled sources are in
`decompiled/phys/0xADDR.c` (float constants inlined as `/*value*/`). Everything below is read from the code unless it
is marked **(inferred)**.

The Unity port is `DriftCityOffline/Assets/DriftCity/Runtime/DriftCar.cs` and `ChaseCamera.cs`; section 12 lists where
it has to differ from the client.

## 1. Big picture

- The player car is driven by a class whose source file is `.\XiGame\Vehicle\XiDynamicControl.cpp` (assert string
  0x860b40). One object is 0x1c70 bytes; the code reads it as a `float[]`, so this document writes fields as
  `in[idx]` (byte offset = idx * 4).
- It is **not** a tyre-force simulation. ODE (statically linked) only holds one body, a sphere of mass 800 kg
  (`dMassSetSphereTotal(800, 4)`, 0x489850), used for integration and collisions. Every step the code
  1. computes a longitudinal force and a lateral acceleration from a steady-state cornering formula,
  2. adds them as forces on the body, steps ODE,
  3. reads the velocity back, sets the body rotation to the **velocity direction**, and makes the car's heading
     `heading = travel angle + slip angle`, where the slip angle is a spring-filtered target.
  The vertical position is not simulated by ODE either: the body is placed on the ground computed from four wheel
  ray casts with their own little spring model (section 9).
- The handling is the same for every player car. The only per-car inputs are the four ratings (Speed, Accel, Crash,
  Boost) mapped through `vehicle\levelmodifier.dat`, plus CamN/CamH for the camera. `weight`, `turboWeakenFactor`,
  `noSlipTime`, `jumpScale`, `maxHeightDiff`, `Accelation`, `DeAccelation` are identical (1 / 1000) for all 125
  player cars and are not read by the player model; they vary only on Traffic rows (traffic collision response, not
  traced).

## 2. Functions

| address | role |
|---|---|
| 0x4bf750 | vehicle manager frame update: fixed-step loop, calls 0x4809c0 once per 3 ms |
| 0x4809c0 | per-step car update (22 KB): input, drift state machine, forces, ODE step, read-back, gears, airborne, gauge, camera |
| 0x479c90 | input: keys/joystick -> throttle, brake, steer angle, drift arming/entry, booster start, reverse switch |
| 0x4800f0 | object init (mass/geometry defaults, gear ratio placeholders) |
| 0x479510 | default handling parameters (most `in[]` constants below), then 0x4793b0 camera presets |
| 0x47fb90 | re-apply ratings: 0x476ac0 / 0x475550 / 0x476c20 / 0x475710 (levelmodifier sections), 0x4763f0, 0x4758d0 |
| 0x47fde0 | rating levels from the player's car data |
| 0x47b370 | loads `Vehicle/LevelModifier.dat` (magic 0x132b6ce, "Dynamic abusing detected" if wrong) |
| 0x4758d0 | gear ratios from top speed |
| 0x475840 | engine torque for (gear, rpm) |
| 0x476e30 / 0x476ec0 | drive force with / without the boost multiplier |
| 0x476f90 | top speed on a slope (and special-mode caps) |
| 0x476f10 | rpm from speed |
| 0x476800, 0x4768f0 | second-order follower step / parameter setter |
| 0x476520 | SmoothCD (Game Programming Gems 4) for the steering visual |
| 0x47beb0 | add to the booster gauge |
| 0x47e510 | ODE near-callback for the player geom (walls, traffic, other cars) |
| 0x475fc0 / 0x475ff0 / 0x476030 | speed factor when hitting object types 0 / 1 / 2 |
| 0x489850 | (re)create the ODE body and reset the state |
| 0x4be510, 0x7e1c50 | `Init/VehicleList` loader (format string 0x887978) |

## 3. Time step

The step length lives at byte 0x1a54 of the object and is set to **3 ms** in 0x4800f0 (`mov [esi+0x1a54], 3` at
0x4802bb). 0x4bf750 accumulates real milliseconds (frame time delta; a backlog over 500 ms is handled specially)
and runs `while (accum >= 3) { update(dt = 0.003 s); accum -= 3; }`. `in[0x219]` is that dt. Integration is ODE's
(semi-implicit Euler for the velocity).

## 4. Ratings -> parameters (`vehicle\levelmodifier.dat`)

File layout ("DPDB", 116 256 bytes): magic 0x0132b6ce at +4, four section counts at +8 (3, 4, 19, 3), then the 29
parameter ids, then four sections of 1001 rows (levels 0..1000) of floats. Readers clamp the level to
`in[0x5f]..in[0x60]` = 1..999.

The level of each rating is computed in 0x47fde0:
`level = ((carData >> 8) & 0x3ff) * (base + parts + bonus) / 1000` (the 10-bit factor is normally 1000, so the level is
the summed rating: vehiclelist/vehicleupgrade value plus fitted parts).
`in[0x93]` = Speed level, `in[0x94]` = Accel, `in[0x95]` = Crash, `in[0x97]` = Boost.

| id | rating | field | meaning | level 1 | level 500 | level 999 |
|---|---|---|---|---|---|---|
| 2000 | Speed | in[0xa4] | top speed (km/h, stored m/s) | 236.4 | 374.6 | 440.2 |
| 2001 | Speed | in[0xa5] | top speed while boosting | 336.3 | 473.7 | 539.9 |
| 2002 | Speed | in[0xa6] | reverse top speed | 100.2 | 161 | 161 |
| 3000 | Accel | in[0x9f..0xa1] | engine torque | 192.0 | 459.2 | 607.3 |
| 3001 | Accel | in[0xa9] | cornering drag (N) | 4867 | 174.8 | 23.7 |
| 3002 | Accel | in[0xaa] | cornering drag while boosting | 1980 | 163.4 | 36.4 |
| 3003 | Accel | in[0xaf] | drift-exit speed gain | 0.884 | 1.103 | 1.17 |
| 4000 | Crash | in[0xb7] | wall speed loss rate | 99.4 | 28 | 20 |
| 4001/4002 | Crash | in[0xb8]/[0xb9] | speed loss vs other cars (boosting / not) | = 4000 | | |
| 4003-4011 | Crash | in[0xba..0xc3] | PvP attack/damage terms (0x47c6f0 etc.), not used for driving | | | |
| 4004 | Crash | in[0xbc] | gauge loss hitting cars | 59.8 | 41.6 | 40.4 |
| 4005 | Crash | in[0xbd] | gauge loss hitting walls | 75.8 | 54.0 | 50 |
| 4012 | Crash | in[0xd2] | uphill top-speed penalty reduction | 0 | 0.50 | 0.6 |
| 4013 | Crash | in[0xd3] | downhill bonus (always 0) | 0 | 0 | 0 |
| 4014 | Crash | in[0xa8] | speed kept when a drift is ended by a hit (km/h) | 100.2 | 193.8 | 250 |
| 4015-4018 | Crash | in[0xd4..0xd6], [0xe0] | not used by the driving code | | | |
| 5000 | Boost | in[0xac] | boost duration (s) | 2.86 | 7.85 | 12.83 |
| 5001 | Boost | in[0xab] | engine force multiplier while boosting | 2.95 | 3.50 | 4.05 |
| 5002 | Boost | in[0xad] | seconds to fill the gauge by just driving (`in[0xae] = 100 / in[0xad]`) | 69.9 | 44.2 | 40 |

(Readers: 0x476ac0 = 2000-2002, 0x475550 = 3000-3003, 0x476c20 = 4000-4018, 0x475710 = 5000-5002. 4014 is multiplied
by 1/3.6 on load. Row 1000 of the Crash section is all zero, which is why the clamp stops at 999.)

## 5. Vehicle table columns

`Init/VehicleList` comes from `vehicles.agt` with 75 columns (the extracted `vehiclelist.xlt` has one more,
`OldFileName`). 0x7e1c50 parses each row into a 0x200-byte record, keyed by Unique Id; a spawned car copies the record
to `actor + 0xe0`.

| column | record offset | note |
|---|---|---|
| Accel, Speed, Crash, Boost | 0x100, 0x104, 0x108, 0x10c | base ratings |
| length, width, height | 0x13c, 0x140, 0x144 | mm * 0.001 -> m (body box, `actor+0x21c` used in hit tests) |
| flength, rlength, fRatio | 0x148, 0x14c, 0x150 | |
| weight | 0x154 | |
| Tire Scale Front / Rear | 0x160 / 0x164 | mm * 0.0005 -> tyre radius in m |
| Tire Width Front / Rear | 0x168 / 0x16c | |
| turboWeakenFactor, noSlipTime, jumpScale, maxHeightDiff | 0x1c0, 0x1c4, 0x1c8, 0x1cc | traffic only (see 1) |
| CamN, CamH | 0x1d0, 0x1d4 | camera distance / height offsets (`actor+0x2b0/0x2b4`) |
| Accelation, DeAccelation | 0x1d8, 0x1dc | |
| JumpCar | 0x1f0 | |
| Car Flag | 0x1f4..0x1fc | G -> 0x1f4=1, M -> 0x1f8=1, C/B/H -> 0x1fc=1/2/3 |
| GradeType V/R/Q | 0x134 | 0/1/2 |
| MagicNumber | | must be 7777777 (0x76adf1) |

The tyre radius in the dynamics (`in[0xee]`, default 0.35) comes from the tyre list (0x4b2a50) and only affects rpm
display and wheel spin: it cancels out of the drive force (section 6).

## 6. Longitudinal model

Constants (0x479510 unless noted): mass `in[0]` = 800, wheelbase `in[1]` = 2.8, CG height `in[2]` = 0.25,
CG-axle `in[3]` = `in[4]` = 1.4, g = 9.8 (double 0x861298).

**Gears** (0x4758d0). Five forward gears (`in[7]`), final drive `in[0xed]` = 1:
- `ratio1 = 6500 rpm * 2pi/60 * r / (0.1838 * vmax)` (first gear reaches `in[0xb2]` = 6500 rpm at 18.38 % of top speed,
  constant 0x8609e4),
- `ratio5 = 7000 rpm * 2pi/60 * r / vmax` (`in[0xb3]` = 7000 at top speed),
- `q = (ratio1 / ratio5)^-0.25` (0x8609d8 = 0.25), `ratio_k = ratio1 * q^(k-1)`,
- reverse `ratio = 7000 * 2pi/60 * r / vreverse`.
- `rpm = ratio * final * |v| * 60 / (2 pi r)` clamped to `[in[0x9c], in[0x9e]]` = [0, 8000] (0x476f10).
- Upshift when `rpm > in[0xd7] + 400` = 6900 and gear < 5; downshift when `rpm < (int)(6500 q) - 400` and gear > 1 and
  the brake is not pressed (0x483c50). No shifting in the air; in the air rpm rises 15000/s and wraps to
  ~9200..9700 when it passes ~10300..11300 (random).

**Engine torque** (0x475840): `T = in[0x27c]` (3000 param). Above 7000 rpm (0x8609d0) it fades linearly:
`T * (1 - min(rpm - 7000, 3000) / 3000)`. While boosting **in top gear** there is no fade. Reverse/neutral use
`in[0xa2]` = 180.

**Drive force** (0x476e30): `F = T * ratio * final / r * (boosting ? in[0xab] : 1)`, i.e. `T * rpmPerMps * 2pi/60`.
For a level-1 car (vmax 65.7 m/s): 1st gear 10.8 kN (13.5 m/s^2), 5th 2.1 kN.

**Total force along the direction of travel** `F` (0x4809c0, around 0x481c00):
1. throttle `<= 0`: `F = in[0x13]` = -2000 N (coast); else `F = drive * throttle`.
2. not boosting and `speed > topSpeedOnSlope`: `F += in[0x14]` = -20000 N (limiter).
3. `F += -in[5] * brake` = -25000 N at full brake.
4. fully airborne (state 2): `F = -2000`.
5. NOSLIP and `|pitch| >= 1 deg`: `F -= 1000 * pitch * (pitch > 0 ? in[0xb5]=3 : in[0xb6]=10)` (forward gear).
   `pitch` is `in[0x1e7]`, the asin of the ground plane's forward slope; **(inferred)** positive = nose up, which is
   the only sign for which both this force and the slope top speed below slow the car uphill.
6. FRSLIP/MTSLIP: any positive `F` is dropped (no engine while drifting).
It is applied as `dBodyAddRelForce(+-F, 0, 0)`; the body x axis is the travel direction (reverse: the nose), so the
force always acts along the motion.

**Top speed on a slope** (0x476f90): `vmax * (1 - f)` with `f = pitch * (pitch > 0 ? 1 - in[0xd2] : 1 + in[0xd3])`
(while boosting, downhill counts half). There is no aerodynamic drag: top speed is held by the limiter and by the
rpm fade in fifth gear (7000 rpm exactly at vmax).

**Speed clamps after the ODE step**: reverse <= `max(topSpeedOnSlope(in[0xa6]), in[0xa6])`; boosting <=
`topSpeedOnSlope(in[0xa5])`; above `in[0xa7] + in[0xa5]` (100 km/h over the boost top speed) all forces are cleared.

**Stopping**: if `dt/m * F + speed < 0.1` and throttle < 0.1, velocity, angular velocity and all filters are zeroed and
the body is rotated to the heading.

**Reverse** (0x479c90): with brake held and no throttle at `speed < 0.1`, a 0.4 s timer (`in[8]`) starts; when it runs
out the gear flips to -1 with 1 m/s backwards (or back to 1 with 1 m/s forwards). In reverse the accelerate and brake
keys are swapped. Steering while reversing faster than 20 km/h adds a drag `v / (20/3.6) * 500 + in[0x22]` (4000 N).

## 7. Steering and slip

Inputs (0x479c90): `steerInput` in -1..1 (keys A/D/arrows, or joystick), `throttle = accel if accel >= 0.1`,
`brake = brakeKey` only while throttle < 0.1. Steering angle `in[0x20c] = -in[0x4f] * steerInput` with `in[0x4f]` = 45
deg: **keyboard steering is instant, no speed-dependent lock**. `in[0x20d]` is a SmoothCD (0.1 s, 0.3 s for wheel
controllers) copy used only for the visual front wheels.

Three spring followers (0x476800: `a = (-(x - target) k - d v) / m; v += a dt; x += v dt`), parameters set every step
(0x4768f0, `m = in[0x26f]`, values from 0x479510):

| state | slip filter (d, k*m) | lateral-accel filter | yaw-rate filter |
|---|---|---|---|
| NOSLIP | 15, 20 | 10, 35 | 15, 30 |
| FRSLIP / MTSLIP | 15, 30 | 10, 15 | 25, 10 |

(k is stored divided by m, so the effective stiffness is `k / m^2`.) `in[0x26f]` is 1 normally, 3 on drift entry, 6 on
FRSLIP->MTSLIP, 1.6 on drift exit, 2 after a hit; it decays at 6/s back to 1.

Outputs: slip angle `in[0x150]` (clamped to +-110 deg = 1.9198622), lateral acceleration `in[0xfa]`, yaw rate
`in[0xfb]`. The lateral acceleration is applied as `dBodyAddRelForce(0, in[0xfa] * m, 0)`; the yaw rate goes to
`dBodySetAngularVel` but is overwritten by the rotation reset, so the heading only comes from
`heading = travel angle + slip`.

### NOSLIP (grip), steering `|angle| >= 0.1 rad`
- `R = (K v^2 / g + L) / angle`, `K = in[0x11]` = 0.2 (understeer gradient), `|R| >= in[0xb]` = 3 m.
- targets: yaw `v / R`, lateral `v^2 / R`.
- axle loads `in[0xf6]/in[0xf7] = m g * 1.4 / 2.8`, cornering stiffness `in[0xf4]/in[0xf5]` = 10000;
  rear slip `in[0xf3] = v^2 * Wr / (R g) / 10000`, clamped to +-10 deg, is the slip target (nose points slightly in).
- cornering drag: forward gear and `|R| > in[0x16]` = 30 m: `AddRelForce(-in[0xa9])` (or `-in[0xaa]` while boosting).
  This is what makes low-Accel cars bleed speed in fast sweepers (4.9 kN = 6 m/s^2 at level 1).
- Asymptotic lateral grip: `a -> g * angle / K` = 38.5 m/s^2 at full lock.

### NOSLIP, no steering (`|angle| < 0.1 rad`)
Targets are zero, `R = 1e10`. The **straightening lock** (`in[0x26c..0x26e]`): releasing the steering after a turn
arms it (+-1 by the last turn direction); as soon as the heading starts swinging back, the heading is frozen
(`in[0x26d]`) and the lateral target becomes `slip * 60` (`in[0x26e]`), pulling the travel direction into the nose.
It ends when `|slip| < 2 deg`, after 1 s, when steering again, or within 300 ms of a hit.

### Drifting
- **Entry** (0x479c90, state set at 0x47a7ef): the drift button must be pressed fresh (it re-arms on release, `byte 0x9e0`) while
  `speed > in[0xf]` = 70 km/h, `|angle| >= 0.1`, NOSLIP, forward gear, recovery timer 0. Sets state = FRSLIP,
  direction = sign of the steering, entry steering `in[0x270]`, `in[0x26f]` = 3, camera lag 4, roll mass 4.
- **FRSLIP** (button held): steering locked to `1.2 * entry angle` (`in[9]`), `K = in[0x12]` = 0.03, no minimum
  radius; lateral/yaw targets `0.5 v^2/R`, `0.25 v/R` only once `|slip| >= in[0xe]` = 45 deg; slip target
  `rearSlip - 1.4/R` (unclamped rear slip, so it runs to the 110 deg limit at speed); engine off;
  drag `in[0x18]` = 8000 N above `in[0x17]` = 60 km/h, x3 while counter-steering (counter-steering also freezes the
  slip target and zeroes the lateral/yaw filters); below 60 km/h a sideways friction `300 * lateralSpeed`
  (x2 while boosting). (The table `in[0x18..0x1d], in[0x52..0x53]` describes a speed-dependent drag but compares km/h
  numbers against m/s, so only `in[0x18]` is ever used.)
- **FRSLIP -> MTSLIP**: button released (with any slip) or speed < `in[0x10]` = 40 km/h; `in[0x26f]` = 6, records the
  heading.
- **MTSLIP**: steering into the drift (`|angle| > 0.1` in the drift direction) holds it: slip target = current slip,
  targets `0.5 v^2/R`, `0.25 v/R` with the locked 1.2x angle. Centred or counter steering (or < 40 km/h) starts the
  recovery: the heading is pinned at the value it had (`in[0x271]`, it may not rotate further into the drift), the
  slip target becomes `heading0 - travel`, and `in[0x202] = clamp(|slip| / 45 deg, 0, 1)` is latched. In both phases:
  sideways friction `300 * 4 * lateralSpeed` (`in[0x20] * in[6]`), and while recovering a forward push
  `|friction| * in[0x202] * in[0xaf]` (Accel 3003) along the heading - this is the drift-exit speed gain; plus
  `in[0x1e]` = -500 N along the travel. Engine off.
- **Exit**: `|heading - travel| < 15 deg` (0.2617994) or speed < 1 m/s -> NOSLIP, `in[0x26f]` = 1.6, the heading is
  frozen through the straightening lock with lateral target `slip * 60`, and a recovery timer
  `in[0x274] = |lateralSpeed| * 0.2 / (40000 / m)` damps the sideways speed (`2000 * lateralSpeed` straight,
  `1200 * lateralSpeed` when steering) with the same forward push.
- Brakes do nothing while drifting. Any wall/vehicle contact flag (0x890/0x894/0x898) ends a drift immediately:
  velocity = nose direction * min(speed, Crash 4014), filters reset, `in[0x26f]` = 2, no gauge reward.
- Debug overlay strings: state names `NOSLIP/FRSLIP/MTSLIP` (table 0x8bd8f0), `fmass:%f sv:%3.2f av:%3.2f` prints
  `in[0x26f]`, speed, actual speed and the body box.

## 8. Booster

- Gauge `in[0x69a]`, 0..100 (`in[0x57]`).
- Passive fill (0x480f94): `+ dt * in[0xae]` per step, `in[0xae] = 100 / (Boost 5002)`, only while not boosting.
- Drift reward (call at 0x486804): at the end of a slide lasting `t > in[0x3d]` = 0.4 s: `+ min((t - 0.4) * in[0x3e], in[0x3f])`
  = up to 15.
- Jump reward (0x48694a): landing after more than `in[0x40]` = 0.4 s in the air: `+ in[0x41]` = 100 (full), unless another jump
  ended less than 2 s ago within 10 m.
- (0x48646d) driving faster than 100 km/h against the lane (`in[0x229]` is the wrong-way flag, see COMBO.md):
  `+ in[0x43]` = 3 every `in[0x42]` = 2 s. Applied by `DriftCombo.cs` together with the wrong-way combo, not by DriftCar.
- Wall hit: `- min(v / vmax, 1) * (Crash 4005)` at most once a second; other players' cars: Crash 4004.
- Start (0x47a504): booster key, gauge >= 100, throttle, no brake, forward gear, not already boosting: gauge -> 0
  (remembered in `in[0x6be]`), 0.2 s delay (`in[0x238]`), then `boost timer in[0x21a] = (Boost 5000) / 100 * used gauge`.
- While boosting: drive force x (Boost 5001), no limiter, no rpm fade in top gear, boost top speed (Speed 2001),
  cornering drag uses 3002. The timer only runs while the throttle is held - **lifting off ends the boost**.
- Camera: FOV 30 -> 38 deg half-angle, pull-back spring stiffer (section 10).

## 9. Ground, suspension, air

Each wheel has a height above the ground `in[0x195 + 6i]` with velocity/acceleration (FL, FR, RL, RR). Ground under
each wheel is ray cast (0x475bd0); the wheel height follows the ground change plus `sin(pitch) * v * dt` (so the car
keeps its own climb rate off a crest). Integration per wheel:
- airborne wheel (height > 0.01): `a = -10 * 1.4` front, `-10` rear (so the nose dips in the air),
- slightly below ground: springs (m=1, d=10, k=30/45) back to 0,
- landing state: spring m=1, d=15, k=500.

`in[0x191]`: 0 grounded, 1 when both front wheels are > 0.5 m up (no gravity on the wheels in this state), 2 when all
four are (jump; sound 0x4f12), 3 = landing (first wheel back <= 0; damage `in[0x25]`*2 once a second; sound 0x4eeb)
until all heights and speeds are below 0.03. The body position is set from the wheel contact plane every step
(0x483d08: `dBodySetPosition(x, y, groundZ)`), body pitch/roll come from the same plane (`in[0x1e7]` pitch,
`in[0x1e8]` roll). Gravity on the ODE body itself is not used.

Visual body tilt (0x477460 uses `in[0x1fd]`, `in[0x1fe]` in degrees): roll target
`(v/R) v / g / 1.28 * h * m / (0.5 m)` (x1.5, or x2 boosting, while drifting; /1.2 in MTSLIP), spring
(m = `in[0x276]`, d = 15 grip / 10 drifting, k = 50/m); pitch target `-(longitudinal weight transfer)/50` clamped +-3.5,
2.5 deg nose-down in FRSLIP, spring (1, 5, 20 or 10). Both are only one or two degrees in practice.

## 10. Driving camera ("outrun", camera.ini `normalview1`)

Computed inside 0x4809c0 (0x484390.. and 0x487c90..). Presets in 0x4793b0 (`in[0xc4..0xd1]`), chosen by `in[0x70c]`:
mode 0/1: F = 30, N = 5.5, H = 1.85, 1.0, FOV 30 deg / boost 38 deg; mode 2: 10, 8, 3, 1, FOV 30/45.
Unique id 0x53 multiplies N by 1.2 and H by 1.5.
- Yaw `in[0x6f2]` follows the car heading with the follower (m = `in[0x6f5]`, d = `in[0x36]` = 50,
  k = `in[0x37]`/m = 200/m), clamped to +-`in[0x38]` = 45 deg. `in[0x6f5]` = 4 at drift entry, decays 20/s.
- Distance N + CamN + speed pull-back; pull-back target `in[0x49] * min(v / vmax, 1)` (= up to 2 m), +5 m after 0.6 s
  of a full jump; follower (1, 10, 10), boosting (0.25, 10, 150), long jump (1, 5, 2).
- Height H + CamH (+ a jump lift follower).
- Pitch follows the body pitch through a (1, 10, 40) follower (only nose-down while airborne).
- FOV: follower (1, `in[0x39]` = 10, `in[0x3a]` = 10) toward 30 deg, 38 deg while boosting. The value is a horizontal
  half-angle for 4:3: the vertical FOV is `2 atan(tan(fov) * 0.75)` (0x860ca8 = 0.75) = 46.8 deg / 60.7 deg.
- A 0.3 m sphere geom (`in[0x6fc]`) keeps the camera out of the city mesh.
- **(inferred)** F is the distance of the aim point ahead of the car (it enters as `sin(cameraPitch) * F` in the look
  point); the exact look-point height could not be isolated from the reused stack slots.

The other camera.ini entries (chase: zoom 12, pitch 0.4, fov 45) are used outside driving (parking lot etc.).

## 11. Collisions (0x47e510)

- The player is pushed out of static geometry along the contact normals by `depth + 0.1` (no physical response).
- Wall slide: driving forward without a drift into a wall rotates the travel direction away from the wall at
  4.712 rad/s (270 deg/s) while in contact.
- Speed loss per 3 ms contact step, only above `0.7 * 0.1838 * vmax` (~13 % of top speed):
  wall `v *= max(0.1, 1 - dt * in[0x249] * (Crash 4000) * impact)`, `impact = -n . travelDir` (0 below 0.1);
  object type 0 `1 - dt * 2 * impact`; type 1 `1 - dt * (1 - in[0x25b]) * 2 * impact`; type 2 uses Crash 4001 (boosting)
  / 4002. So at Crash level 1 a head-on wall hit stops the car almost instantly; at level 999 a wall costs only 20 %
  per second of contact.
- Vehicle-vehicle contacts are real ODE contact joints (mode dContactApprox1, mu = 20, no bounce).
- **(inferred)** object type 1 is a traffic car (the handler 0x47d3d0 pushes it), type 2 an NPC/HUV or other player;
  only types 1, 2 and walls set the flags that cancel a drift.

## 12. Unity port (`DriftCar.cs`, `ChaseCamera.cs`)

Exact (same formulas and constants): 3 ms sub-steps (`round(fixedDt / 0.003)` per FixedUpdate), mass 800,
levelmodifier mapping (read from the mounted install; 50-level samples as fallback), gears/rpm/shift points, torque
fade, drive force, coast/limiter/brake forces, slope force and slope top speed, cornering radius and slip targets,
the three followers and their per-state parameters and mass multiplier, drift entry/hold/recovery/exit rules and
forces, counter-steer handling, straightening lock, recovery timer, booster start/delay/duration/lift-off cancel/
multiplier/top speed, passive fill, drift and jump rewards, wall speed loss and gauge loss, crash-ends-drift with the
4014 speed, reverse switching and swapped keys, steering visual SmoothCD, body tilt, camera yaw spring/clamp,
pull-back, pitch and FOV followers.

Different by necessity:
- **Vertical motion**: the client places the body on the four ray-cast wheel heights; the port keeps Unity gravity
  and the WheelCollider suspension (friction switched off while the model drives) and only writes the horizontal
  velocity and the yaw rate each step. Airborne state uses the same 0.5 m thresholds (ray casts from the wheels), and
  while fully airborne extra forces bring the axles to the client's 14 / 10 m/s^2. Because the wheels are
  frictionless, the along-slope part of Unity's gravity is cancelled while grounded so only the client's slope force acts.
- Collisions are Unity's: contacts with static/kinematic colliders count as walls, contacts with dynamic rigidbodies
  as traffic (type 1 rule). The wall slide turns the travel toward the wall tangent at 270 deg/s instead of a fixed
  rotation per contact. A hit that throws the car backwards in a forward gear has the reversed part dropped (the client
  would flip the heading).
- While `ExternalControl` is on and nobody calls `ApplyControls`, the wheels get their friction and brakes back so a
  parked car stays put.
- Not ported here: the wrong-way gauge reward (in DriftCombo.cs instead), the "reversed controls" effect, camera mode 2
  and the camera shake (13.3). Assists, the Battle Zone multipliers, the mini boost, Turbo Start / Stop, fuel, durability
  and the jump camera lift: sections 13 and 14.
- Keys follow the client defaults (Space also drifts in the port); the client defaults are
  Shift = drift and Ctrl = booster (DirectInput codes 0x2a/0x36 and 0x1d in 0x479c90).

### Resulting numbers (Python port of `DriftCar.Step`, flat ground)

| | level 1 car (Kicker V1) | level ~450 |
|---|---|---|
| 0-100 km/h | 3.1 s | 1.7 s |
| 0-200 km/h | 11.5 s | 5.5 s |
| top speed | 236 km/h after ~15 s (5th gear, 7000 rpm) | 364 km/h |
| full brake from 230 km/h | 1.9 s | |
| full lock at 120 km/h | 3.5 g, slip 10 deg, speed bleeds to 115 km/h (cornering drag) | |
| drift: 150 km/h, tap drift 0.3 s, hold steering | slip settles at ~16 deg, car turns 180 deg in 2 s while speed falls to ~105 km/h; centring the steering straightens in ~0.75 s and the speed climbs again | |

## 13. Assists, Battle Zone, items, durability (round "last car gaps")

### 13.1 Where the per-car adds come from [exact]
- **Assist options** ("skill items"): ItemClient.tdf column 20 names a row of `World/Data/Assist_Client.tdf` (5,446 rows:
  grade, id, kind name, name, description, value, unit). Option parts (op_S / op_A / op_C / op_B / op_F), docking parts
  (dock_*) and set parts carry them. 0x6c58e0 maps the kind name to a number (stop 1, qstart 2, criticalcrash 3, saving 4,
  gage 5, endurance 6, untouchable 7, partycrashup 8, bonusspeed 9 .. bonusboost 0xc, chance 0xd, speedup 0xe,
  crashsaving 0xf, partyspeedup 0x10, exp 0x11, overturbo 0x12, bonusreal 0x13, bonusdamage 0x14, bonustime 0x15,
  driftbooster 0x16, battle_c_b 0x17, battle_b_r 0x18, battle_c_fb 0x19, battle_c_fr 0x1a, field_h_d 0x1b, field_c_fb
  0x1c, field_b_r 0x1d, field_b_fr 0x1e, docking_b 0x1f, docking_p 0x20, booter_b_r 0x21, bbattle_b_r 0x22, BA_CS 0x27,
  BA_BC 0x28, BA_MS 0x29, CH_DA 0x2f, BA_NS 0x4f, BA_CA 0x35, BA_CB 0x36, BA_BM 0x37, CH_AD 0x41, CH_AB 0x42, CH_CC 0x45,
  CH_BT 0x48, CH_SD 0x4b, AL_QS 0x50, mito 0x52, expmito 0x53).
- 0x6d2580(dyn, row, .., divisor = in[0x57] = 100) writes the value into the dynamics object; 0x6dac70 resets those
  fields and 0x6dbc40 re-applies the car's assists. Fields (byte offsets): 0x918 stop (flag), 0x914 qstart / AL_QS (flag),
  0x908 criticalcrash, 0x91c untouchable (flag), 0x924 -= crashsaving / BA_CS % (starts at 1.0; it is in[0x249], "SR" of
  the debug line `ADD(MS PMNS PMTS SR TU UE)` at 0x860f60), 0x928 += overturbo / BA_BC seconds ("TU"), 0x92c += bonusreal %,
  0x930 += bonusdamage / CH_DA %, 0x934 driftbooster (flag), 0x938 field_h_d, 0x93c field_c_fb, 0x940 += field_b_r %,
  0x944 field_b_fr, 0x948 battle_c_b, 0x94c += battle_b_r / bbattle_b_r %, 0x950 battle_c_fb, 0x954 battle_c_fr,
  0x958 += BA_MS / BA_NS ("PMNS"), 0x95c += BA_MS / BA_BM ("PMTS"), 0x960 BA_CA, 0x964 CH_SD, 0x968 += CH_AB, 0x96c CH_AD,
  0x970 CH_CC, 0x974 += BA_CB, 0x978 += CH_BT %; booter_b_r adds to both 0x940 and 0x94c.
- Top speed add **car +0x920** (0x47fde0) = (two speed bonuses at +0xa9c and +0xac4) / 3.6, x 0.8 when the service nation
  (0x4ccf20 -> 0x8d2e10) is 1 or 4 ("Drift City"; 0 is "SkidRush"); +0x8f4 is a second add that only the reset writes
  (always 0 in this client). **[inferred]** the two km/h bonuses are the speedup / partyspeedup assists (the "Speed Up"
  rows are km/h; no other code writes +0xa9c / +0xac4).
- Fuel: 0x480050 / 0x47fde0 set the tank +0x2c0 = (100 + +0xaa8) / 100 x car data +0x28 and the mileage +0x2c4 =
  (100 + +0xaac) / 100 x car data +0x2c (`ADD(FE)` = in[0xb1]). **[inferred]** +0xaa8 / +0xaac are the "gage" ("Increases
  max fuel gauge by 2%") and "saving" ("Improves mileage by 1%") assists. The burn (near the `fuel:` debug line of
  0x4809c0) is `fuel -= distance / (in[0xb1] * 1000)`; while drifting it burns dt x in[0xa4] instead.

### 13.2 What the step does with them [exact]
- **Mini boost** in[0x21b] (+0x86c): the drive force takes the boost multiplier while it runs (0x476e30: +0x868 boost
  timer or +0x86c) and the limiter is skipped, but there is no boost top speed and the top-gear fade stays. It counts down
  and is capped at in[0x45] = 1 s.
- **Turbo Start** (0x479c90, +0x914): drift key armed and pressed less than 500 ms ago (0x85c - DAT_008d1924 < 500), the
  car standing (speed < 0.1) and the forward key fresh -> in[0x21b] = in[0x45] (1 s), gear 1, sound 0xd.
- **Turbo Stop** (+0x918): forward gear, moving, drift within 500 ms and the brake key fresh -> in[0x23e] = in[0x46]
  (1 s); while it runs above 10 km/h the force is in[0x712] (40) x in[0x13] (-2000) = -80 kN instead of the engine;
  releasing the brake ends it.
- **Post-drift booster** (+0x934, 0x486745 / 0x479c90): a slide that ended with its peak angle (DAT_008d193c) above 30 deg
  opens a 0.3 s window (+0x884); drift and forward pressed again in it start the 1 s mini boost. **[inferred]** the drift
  must have lasted long enough (+0x168 compare; the remake takes the drift reward's 0.4 s).
- **Boost duration**: `in[0x21a] = (in[0x24a] + Boost 5000) / 100 x used gauge` (overturbo adds seconds).
- **Boost end**: a city car (not +0x1a9c) gets the full gauge back with in[0x251] (0x944 field_b_fr) percent chance; the
  Battle Zone uses in[0x265] (+0x994, server-set; not rebuilt).
- **Passive fill** (0x480f94): `dt x in[0xae] x f`, f = (in[0x25e] (0x978) - 1) + in[0x250] (0x940) in the city and
  `((int)+0x8d8 battle combos x 0x974 + 0x94c) x +0x1abc` in the Battle Zone.
- **Gauge adds** 0x47beb0: the passive fill only while no boost runs or waits; in the Battle Zone (+0x1a9c) every change
  is multiplied by +0x1ab8; clamped to 0..100.
- **Walls**: `v *= max(0.1, 1 - dt x in[0x249] x Crash 4000 x impact)` (crashsaving lowers in[0x249]); 0x47c000 skips
  the loss while boosting with +0x91c (untouchable). Gauge loss from hits (Crash 4004 / 4005) only outside the Battle Zone.
- **Traffic hit** (0x47e510, first contact): +5 gauge with +0x1ad0 (the Crash Capsule); city: field_c_fb percent chance
  of a full gauge; Battle Zone: + battle_c_b gauge, battle_c_fb percent chance of a full gauge, battle_c_fr percent chance
  of a full repair (the stage's vfunc +0x1a0).
- **Reverse**: torque `(1 + +0x92c) x in[0xa2]` (bonusreal).
- **Battle Zone top speed** (0x476f90 with +0x1a9c): normal `slope x vmax + adds + 0x958 / 3.6 (x 0.8)`; boosting the
  larger of `slope x vboost + adds + 0x95c / 3.6 (x 0.8)` and `0x958 / 3.6 (x 0.8) + adds + slope x vmax`.
- **+0x1a9c** is set by the PvP stages (0x7254dd, 0x7616fd = 1; 0x71f389 / 0x72d079 clear it); +0x1ab8 by 0x4786c0,
  +0x1abc by 0x478750 (it keeps only 1 or 2: the Boost Recharger's "2x faster").

### 13.3 Durability
- [exact] The durability stat is the Crash rating: its levelmodifier rows set the wall / car speed loss (4000-4002), the
  gauge loss (4004 / 4005), the uphill penalty (4012) and the speed kept when a hit ends a drift (4014); 4003-4011 feed
  the HUV chase damage (0x47c6f0). The client's own tip says the same (11740: "[Dura Points] ... [Damage] Increases chase
  damage against HUVs. [Battlezone] Increases your resistance against crashes while racing."). The actor's +0x1db0 /
  +0x1db8 health belongs to the HUVs (set by their spawn packets); the damage smoke (0x69e590) shows above 60 %.
- [exact] FUN_00479050(t, amount) is not damage: it kicks a camera shake spring (in[0x6e3..0x6e8], m 1, d in[0x23] = 13,
  k in[0x24] = 2000) at most once a second, amount in[0x25] = 15000 x 2 on landing and x 3 when a hit ends a drift; the
  spring's angle swings the look point sideways. Not ported (the kick's size could not be reconciled with the units of
  the camera code).
- [remake] Every car has a durability 0..1 (DriftCar.Durability) for the repair items in the data (Gio i_d_repair01,
  Mk.II Battle Quick Repair Kit 30 %, battle_c_fr): a hit costs min(v / vmax, 1) x the Crash rating's gauge loss (4005
  walls, 4004 cars) / 1000, at most once a second, nothing during Invincible Rush; CarEffects.Damage01 = 1 - durability
  (smoke below 40 %). It does not change the handling, as in the client.

### 13.4 Camera: the jump lift [exact]
Inside 0x4809c0 (after the pull follower): while fully airborne (state 2) with the air time in[0x231] over 0.6 s the
height offset in[0x6ce] follows `(H + CamH) / max(0.1, N + CamN) x 22` on a (1, 5, 2) follower (0x860cb8 = 22);
otherwise it is set to 0 at once. The camera height is `H + CamH + in[0x6ce]` (normalview2 up to 8.25 m more,
normalview1 7.4 m).

### 13.5 Battle quick-slot items in the race
Crash Capsule = +0x1ad0 for 20 s (+5 gauge per traffic hit); Boost Recharger = +0x1abc = 2 for 40 s; Invincible Rush =
untouchable for 10 s (no speed loss, the rivals pass through, untouchable_shield.nif); DCR Capsule = the next gate within
10 s gives the pvp_skidrush effect ("Drift City will be initiated as you pass a gate"); Emergency Boost Kit = a boost now
with the current gauge (30 % or more). Remake: Game/Inventory/BattleItemEffects.cs, BattleQuickSlots.cs.

### 13.6 Remake (DriftCar.cs, ChaseCamera.cs, Game/CarAssists.cs, Game/Fuel/MittronFuel.cs)
- `CarAssists` holds the fields above; `CarAssists.For(profile, car)` collects option parts on fitted parts (not docking
  parts, whose assists are points) and an active set's parts (without their "+Np" kinds, which SetParts.Bonus counts).
  The player's car takes them in `ApplyStats`. Exact: mini boost, Turbo Start / Stop, the post-drift window, overturbo,
  crashsaving, untouchable, field / battle fill, the boost-end refill, the traffic-hit rules, reverse torque, the speed
  add x 0.8, Battle Zone top speeds, the gauge multiplier and no gauge loss in the Battle Zone.
- `DriftCar.PvpMode` (+0x1a9c) is set while the Battle Zone race HUD (RANK 1380000) is up (BattleQuickSlots driver);
  `PvpGaugeGain` (+0x1ab8) and `PvpFillRate` (+0x1abc) are public.
- Fuel: `MittronFuel.Capacity` / `Mileage` = 100 L / 5 km/L x (100 + gage / saving %) / 100 for the current car (the base
  stays the client's default: the per-car litres came from the server's car data).

## 14. Car models: simple model, custom marks

- **Simple model** `vehicle\player\<model>_s.nif` (179 files: one vertex-coloured NiTriShape, NiStringExtraData
  "LODDistance = 100.0") [exact]: built by 0x45cd60 (cached on the model record +0x194, cloned into the actor +0x18c), the
  fallback of 0x45fa60 when the car's model info can't be read ("carmodel GetInfo Fail"). The bodies carry their own
  NiLODNode (LODGroup01 / LOD03) with a single level. Remake (CarFactory): the simple model stands in when a body NIF is
  missing, and is the LOD1 of every built car past 100 m (LODGroup: the full car, then the simple body with the wheels;
  culled past the 512 m fog) [remake]. Damage models: the client has none for player cars; damage shows as the damage
  smoke (EFFECTS.md).
- **Custom marks** [exact]: 0x4411e0(id, flag, ..) loads `effects\realmatch\real_%05d.dds` (128 x 128 DXT3) for ids up to
  99999 with flag 0, else asks the server (0x3f6 through 0x43fd80): the players' own marks came from the server. Remake
  (Game/CustomMarks.cs, the inventory's Sticker page): a local image file (PNG / JPG, up to 4 MB / 4096 px) is fitted into
  128 x 128 and kept as a PNG of at most 32 KB; offline in the profile (CustomMark), online on the server (mark.set /
  mark.get / mark.clear, src/carparts.js, which checks the PNG signature, IHDR size <= 128 and 32 KB so the base64 fits the
  64 KB message); the mark is composited on both doors of the car's paint layout (CarPaint), on remote cars once fetched.
