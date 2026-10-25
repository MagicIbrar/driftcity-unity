# Drift City speed combo ("주행콤보") and the SPEEDCOMBO HUD

Recovered from `DriftCity.exe` (image base 0x400000). Decompiled sources are in `decompiled/phys/0xADDR.c`.
Field names follow PHYSICS.md: `in[idx]` is the player car object (`XiDynamicControl`, 0x1c70 bytes) read as
`float[]`, byte offset = idx * 4. **[exact]** = read from code, **[inferred]** = interpretation.

The Unity port is `DriftCityOffline/Assets/DriftCity/Runtime/Game/DriftCombo.cs` (+ `DriftComboContacts.cs`).

## 1. Functions

| address | role |
|---|---|
| 0x4804d0 | `AddCombo(type, n, sendParty, unused, fromParty)`: gate, count += n, timer = 0, milestone packet, HUD, sound |
| 0x4787a0 | `ResetCombo()`: count = 0, `HudCombo.Reset()` (0x563930) |
| 0x4809c0 | car update: combo timeout, wrong-way / drift / jump triggers (around 0x48637e..0x4869c0) |
| 0x489030 | near-miss ("graze") trigger, called from the traffic car update 0x4a6460 |
| 0x4a6460 | traffic car: near-miss detection against the player |
| 0x47e510, 0x47c000, 0x47c2a0 | collision callbacks: any wall / vehicle hit resets the combo |
| 0x4ed43c | network handler: a party member's combo (`fromParty = 1`) |
| 0x514350 | sends the combo to party members (only when in a party of 2+) |
| 0x79d210 | sends the "every 10th combo" packet to the server |
| 0x564340 | HUD class init: binds dialogs 300000 / 310000 and their controls |
| 0x563990 | `HudCombo.Show(count, type, party)` |
| 0x563930 | `HudCombo.Reset()` |
| 0x563e90 | `HudCombo.Update()` (per frame: timers, AUTOREFILL notice) |
| 0x5646c0 | `HudCombo.DrawDigits(party)` (render callback 0x564230 / 0x564260 on message 0x2001) |
| 0x564290 | `HudCombo.ShowAutoRefill()` (from the Mitron auto-refill packet handler around 0x512830) |

## 2. Combo types, icons and sounds **[exact]**

`type` is the first argument of 0x4804d0. Sound ids are registered in 0x5232b0 (`SID_*.WAV` table, id before name):

| type | trick | trigger | icon control (basic / ADV) | sound |
|---|---|---|---|---|
| 0 | wrong-way driving (역주행) | 0x48647b, 0x486489 | `ID_SPEEDCOMBO_REVERSE` 300029 / 310029 | 0x4edf `SID_COMBO_REVERSE.WAV` |
| 1 | drift (드리프트) | 0x486858 | `ID_SPEEDCOMBO_DRIFT` 300031 / 310031 | 0x4ee1 `SID_COMBO_DRIFT.WAV` |
| 2 | jump (점프) | 0x486975 | `ID_SPEEDCOMBO_JUMP` 300030 / 310030 | 0x4ee2 `SID_COMBO_JUMP.WAV` |
| 3 | near miss (곡예주행 "stunt") | 0x4890ac | `ID_SPEEDCOMBO_STUNT` 300028 / 310028 | 0x4ee0 `SID_COMBO_GRAZE.WAV` |

The sound (0x52b980, 2D one-shot) plays once per `AddCombo` call, not once per counted unit.

## 3. Triggers (all in the car update unless noted)

Constants from 0x479510 / 0x4800f0: `in[0x3b]` = 1, `in[0x3c]` = 6, `in[0x3d]` = 0.4, `in[0x3e]` = 15, `in[0x3f]` = 15,
`in[0x40]` = 0.4, `in[0x41]` = 100, `in[0x42]` = 2, `in[0x43]` = 3, `in[0x44]` = 3, `in[0x54..0x5a]` = 0, 1, 10, 100,
1000, 0.1, 0.5, `in[0x5c]` = 3.6. The "content flag" below is bit 0x4000000 of the player/session record
(`0x780510() + 0x248`), a server-enabled feature; with it off every trick counts 1.

- **Drift [exact]** (0x48675x..0x48688e): `in[0x230]` (debug label "DT") accumulates dt while the drift state
  `in[0x161]` is FRSLIP or MTSLIP. When the state is NOSLIP again and `in[0x230] > 0.4`: gauge
  `+ min((t - 0.4) * 15, 15)` (already in DriftCar) and `AddCombo(1, n)` with n = 1; with the content flag
  n = 1 (t <= 0.5), 2 (0.5 < t <= 0.7), 3 (t > 0.7). Then `in[0x230] = 0`. Requires `0x780510()+0x3b4ec != 0`
  (a per-map value, see gates). A drift ended by a hit gives nothing (the hit zeroes the drift, PHYSICS.md 7).
- **Jump [exact]** (0x4868ac..0x4869d3): `in[0x231]` ("JT") accumulates while `in[0x191]` is 1 or 2 (front wheels
  up / airborne). On landing, if `t > 0.4` and (`in[0x70e] == 0` or the landing point is more than 10 m from the
  previous one): gauge `+100` (already in DriftCar) and `AddCombo(2, n)`, n = 1 (3 with the content flag). Then
  `in[0x70e] = 2` (s, counted down by dt early in 0x4809c0) and the position is remembered, whatever the air time was.
- **Near miss [exact]** (traffic car update 0x4a6460, award 0x489030): only cars whose owner record says "player
  nearby" (`+0x1bd0 > 0`). With `d2` = squared distance from the player's position to a reference point on the
  traffic car (below):
  - player within 10 m (`|p|^2 < 100`): nothing unless the player's speed `in[0x14d]` >= 27.78 m/s (100 km/h);
    then if `d2 < 4` (2 m) the pass is armed (`+0x1dc8 = 1`, minimum `+0x1dcc = d2`) or its minimum is lowered.
  - player beyond 15 m (`> 225`) with an armed pass that did not hit the car (`+0x1d5c == 0`): disarm, gauge
    `+ (2 - d) * in[0x3c] + in[0x3b]` = `(2 - d) * 6 + 1` (1..13) with `d = sqrt(min d2)`, and `AddCombo(3, n)`,
    n = 1; with the content flag n = 3 (d < 0.35), 2 (d < 1), 1 otherwise.
  - Reference point (in the traffic car's frame, x = length axis): x = `+-halfLength` (`+0x220`) on the player's
    side; y = the side face on the player's side (`ratio * width` or `-(1 - ratio) * width`, `+0x230`/`+0x21c`)
    or the centre line, whichever is closer to the player. So it is the nearest front/rear corner (or end centre)
    of the traffic car **[exact]**; which field is length/width is **[inferred]** from the axis use.
- **Wrong way [exact]** (0x485ee0..0x4866e5, debug "RT"): `in[0x229]` = 1 when the road map query (0x4d6a50, by
  position and heading) says the car drives against the lane; values 3/4 there set `in[0x22a]` ("SW",
  sidewalk/off-road) instead. Classic rule (content flag off): while `in[0x229] == 1` and speed > 100 km/h,
  `in[0x22f] += dt`; each time it passes 2 s: `-= 2`, gauge `+3`, and **two** `AddCombo(0, 1)` calls (+2, two sounds).
  While not wrong-way `in[0x22f]` decays at 4/s (not reset). With the content flag a different accumulator
  `in[0x235]` (speed thresholds 220/290 km/h scaled by player level, `_CIpow`, Boost rating) gives `+3 * k` gauge and
  one `AddCombo(0, 1)` per award (not ported). PHYSICS.md section 8 calls this the "special road type" reward; it is
  wrong-way driving.

## 4. AddCombo (0x4804d0) **[exact]**

1. Gates (return 0, nothing counted, timer not reset):
   - scene state `0x4386b0()+0xcc` must be 0xE, 0xF or 0x13; outside 0xF also the flag `0x8df2dc+0x110` must be
     clear (set by 0x6fe200 after a condition from 0x6faf80 held for 30 s; not identified);
   - Mitron (fuel) `double in[0x200]` (byte 0x800) must not be 0;
   - per-map value `0x780510()+0x3b4ec`: 1 -> car height `in[0xfe]` >= 19, 2 -> >= -2, 3 -> >= 544 unless a flag;
   - `in[0x1e6]` (byte 0x798) != 0.
   **[inferred]** 0xE/0xF/0x13 are the city-type scenes (free roam, an event city, battle zone); 0xF raises the
   combo ratings below, 0x13 feeds a battle gauge.
2. Statistics for the server packet (`+0x8c8` party/remote count, `+0x8cc` drift count, `+0x8d0` flags).
3. Combo timer `in[0x22e]` (byte 0x8b8) = 0.
4. `n` times: `count in[0x22d]` (byte 0x8b4) `+= 1`; if `count > 0 && count % P == 0` send packet 0x79d210
   (count, statistics) - the server rolls item drops for "consecutive combos" (alchemy item texts: enhancers,
   protectors **[inferred]**). P = 10 (`in[0x56]`), 20 with the content flag (30 when 0x4ccf20() returns 3).
   `sendParty` -> 0x514350 shares each unit with the party.
5. Scene 0xF only: every 20 combos (`count / (2 * 10)` crosses an integer) a combo level `+0x8d8` rises (max 11)
   and the ratings are re-read (0x47fde0 / 0x47fb90) - a stat buff in that mode.
6. `HudCombo.Show(count, type, fromParty)`, then the type's sound, then quest/achievement counters
   (0x6cf690(7, 1); 0xF: (0x29, n); 0x6ccc60(3, 1)).
7. Scene 0x13: every `0x450330()` combos `in[0x6b6] += 1` (cap 100).

**No booster gauge is given per combo or per combo level.** The gauge rewards belong to the tricks (section 3).
No EXP or money either; the only reward is the server's drop roll every 10th combo.

## 5. Combo end **[exact]**

- Timeout: `in[0x22e] += dt` every step; `> in[0x44]` = **3 s** since the last counted trick -> `ResetCombo()`
  (0x481901, called every step after that).
- Off-road: `in[0x22a] != 0` (sidewalk/off-road from the road map) -> reset every step (0x481910). The road
  map itself (the .tcs roads in the open cities, AREACHECK total.hit in taipei) is in ROADS.md.
- Collisions: 0x47e510 resets when the contact is static geometry (`puVar10 == null`) or an object whose type
  is not 0; 0x47c000 / 0x47c2a0 (vehicle hits, flag `+0x838 & 4`) reset. So any wall or vehicle hit ends it.
- Car reset / respawn (0x489850 zeroes `in[0x22d]`).

## 6. HUD (class at UI manager `0x4386b0()+0x160 -> +0x280`)

### Dialogs **[exact]**
300000 (basic) and 310000 ("ADV") have the same 32 controls; 310000 uses smaller digits (32x27 vs 49x41), other
offsets and a white tint on the icons. Choice (0x563990, same rule as SPEEDMETER 0x62a300):
`DAT_008d2940 == 0 && (0x780510()+0x24c & 0x18) == 0x10` -> 310000 (and SPEEDMETER 330000), else 300000 (with
SPEEDMETER 320000). **[inferred]** this is the main-UI type option. The port's Hud opens 320000, so the combo uses 300000.
Both dialogs: anchor 36 (bottom right). In scene 0xF (and in basic mode when the UI manager's vfunc 0xfc is
true) the dialog is moved 40 px down.

### Controls (init 0x564340, object offsets basic / ADV) **[exact]**
| controls | use |
|---|---|
| `NUMPOS0..3` (+0x8..+0x14 / +0xbc..) | digit slots: thousands, hundreds, tens, ones; their own image is never shown |
| `NUM0..9` (+0x38.. / +0xec..) | digit art; hidden, drawn by 0x5646c0 at the NUMPOS positions |
| `PARTYNUM0..9` (+0x60.. / +0x114..) | digit art for a party member's combo |
| `REVERSE/DRIFT/JUMP/STUNT` (+0x88/+0x8c/+0x90/+0x94) | trick icon, one at a time (all at H 125,45); flag 0x40 set at init |
| `COMBO` (+0xa8) | "COMBO" word |
| `PARTYCOMBO` (+0xac), `PARTYIMG` (+0xb0) | party variants, shown instead of COMBO when `fromParty` |
| `AUTOREFILL` (+0xb4) | "미트론충전완료!" (Mitron refill complete): shown for 5 s by 0x564290 on a server auto-refill, not combo-related |

Visibility is bit 1 of the control's flags (`|2` shown, `&~2` hidden).

### Behaviour **[exact]**
- `Show(count, type, party)`: dialog visible (the other one hidden); `hideAt = now + 6 s`, `labelUntil = now + 0.7 s`;
  all NUM/PARTYNUM/NUMPOS controls hidden (digits are custom-drawn); if the ones digit changed, `popUntil = now +
  0.3 s` (per-digit timers are written but the draw code only reads the ones-digit timer, so all digits pop
  together); the icon of `type` shown, the other three hidden; COMBO shown and PARTYCOMBO/PARTYIMG hidden
  (party: the reverse).
- `Update()`: past `hideAt` the dialog hides; past `labelUntil` the icon and COMBO/PARTY labels hide (type = 4).
  So the trick icon and "COMBO" flash for 0.7 s and the number stays until the combo ends (3 s timeout) or 6 s.
- `Reset()`: count = 0, both dialogs hidden, type = 4.
- `DrawDigits`: digits `count/1000 % 10, count/100 % 10, count/10 % 10, count % 10` in NUMPOS0..3, leading zeros
  skipped (so a 4-slot right-aligned field; 10000 wraps). Each digit is the NUM (or PARTYNUM) control moved to the
  NUMPOS position, `+5 px` x for the digit 1. While `popUntil` is ahead, with `r = (popUntil - now) / 0.3`
  (constant 3.3333): `grow = (int)(h * r * 0.5)`, y `-= grow`, element field `+0x10 = h + grow`, colour alpha
  `(int)((1 - r) * 255)`. **[inferred]** `+0x10` is the screen-element height, i.e. the digit starts 1.5x tall
  (bottom edge fixed) and transparent and settles in 0.3 s.

## 7. SPEEDITEM 340000 / 350000

A separate panel (vtable 0x87283c, init 0x6292a0: hidden at creation; `Show(bool)` 0x629170 with the same
basic/ADV rule, also SPEEDMETER render 0x62ac90 moves it). Six item cases (`BGITEM01..06`) and icons (`ITEM01..06`);
its update 0x6299d0 animates a queue of entries with a per-slot timing table (0x872820). **[inferred]** It is the
"item obtained while driving" ticker (drops from driving/combos/battle zone, server-sent). Nothing in the city
shows it without a drop, so the offline port keeps it closed in free roam. Its callers and timings are in AUDIO.md 8
(`SpeedItems.Add`, HudPanels.cs).

## 8. Port notes (`DriftCombo.cs`)

Exact: trick rules and thresholds (drift 0.4 s and the 0.5/0.7 s multi-count steps, jump 0.4 s / 2 s / 10 m,
near-miss 10 m arm radius, 2 m distance, 15 m release, 100 km/h, `(2 - d) * 6 + 1` gauge, wrong-way 100 km/h /
2 s / +3 gauge / two counts / 4x decay), 3 s timeout, reset on hits and respawn, milestone period, HUD timings
(6 s, 0.7 s, 0.3 s pop), icon/label/digit control use, digit layout and leading-zero rule, sounds.

Applied by the port, not by DriftCar: the near-miss and wrong-way gauge gains (DriftCar applies only drifts and
jumps). No per-combo gauge reward exists, so nothing is double-counted.

Different by necessity / not ported:
- Wrong-way uses the traffic lanes (`.tcs`) instead of the road map: the nearest lane segment within 4 m
  whose direction is more than 120 deg from the car's heading. The sidewalk/off-road reset (`in[0x22a]`) is not
  ported (no road-surface map).
- Near-miss reference point uses the traffic car's BoxCollider (longer side = length) and ignores height
  (contacts more than 3 m above/below are skipped instead).
- Gates: counted only while the HUD is up, not during races (RaceMission/OnlineRace, **[inferred]** the client's
  races run outside scenes 0xE/0xF/0x13) and not while the car is under ExternalControl. Mitron, map-height and
  the 30 s lock gates are not ported.
- Content flag 0x4000000 is `DriftCombo.MultiCount` (off by default); its wrong-way variant is not ported (the
  classic rule is used).
- Server drop roll: `DriftCombo.Milestone` event every 10th (20th) combo, no handler offline. Party combo,
  scene-0xF stat buff, battle gauge and AUTOREFILL are not used.
