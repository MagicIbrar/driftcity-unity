# Undercity

The client's Undercity mode ("UnderCity", Rescue Mission of Undercity Admin Tatiana) and how the Unity remake rebuilds
it offline (`DriftCityOffline/Assets/DriftCity/Runtime/Game/Undercity*.cs`). **Exact** = read from the exe or the data.
**Inferred** = deduced from code shape, texts or art. **Rule** = this rebuild's own choice where the server decided.

## What it is

- A party mission (2-4 drivers, all in the same city) played in an underground version of **that city's own map**:
  there is no separate Undercity map. Stage 0x13 (`0x76d770` "UnderCity Stage Enter", `0x76dc70` "Leave", update
  `0x76d120` "Gsb UnderCity Update") loads the party's city with `effects\skydome_und_sky.nif` (`0x433f39`; nodes
  `skybox_01..04` = 1st..4th city) or `skydome_und_nosky.nif` (`0x4341b6`) and the lights of `init\weatherunder.ini`
  (`0x43bcc2`, loaded next to `weather.ini`). **Exact.**
- Lore (globaltext `UC_Intro`): the Undercity lies below Mittron Island; HUBs (devices that spy on the civilians) and
  HUVs are destroying it. Enabled per city by flags in the game's config (`0x453780`: `City.UnderCity`,
  bits 0x40 global, 0x80/0x100/0x200/0x400/0x800 per city, checked in `0x6f8e40` and `0x715680`). **Exact.**
- Not to confuse with **Rush Time** (`0x4e2270`, "Rush Time" / "Waiting Rush Time", `City.RushTime`,
  `effects/ui/pvp_skidrush`, `Game/cylinder_rushtime.nif`): a separate timed city event that blocks Undercity
  invitations (text 11146). Not part of this conversion.

## Entry (city)

- `0x6f8e40`: near the city's Undercity entrance post (`+0x44`, near-post test `0x6f55f0`) the client shows
  `UC_Enter` (QUESTDIALOG class `0x688780`, dialog `0x546520(1)`). Refused with text 10965 while registered for a
  Virtual HUV, and with 11290 "Only %d or higher vehicle can exercise mission" when the car's grade is below
  globalcontent `MinUnderGrade` (0 in the US column). **Exact.**
- Entrance posts (strings `0x879cc8..0x879e2c`): Moon Palace `post_1st_00784`, Koinonia `post_2nd_00789`, Cras
  `post_3rd_00791`, Oros `post_4rd_00626`. Marker model `effects\undercity\uc_gate.nif` (`0x6b0da3`). Minimap icon
  `ID_MINIMAP_ICON_UCENTRY`. **Exact.**
- QUESTDIALOG buttons `ID_QUESTDIALOG_WHATISUC` (shows `UC_Intro`) and `ID_QUESTDIALOG_UCACCEPT`. Texts: `UC_NeedMito`
  (all missions except instant ones are cancelled; the portal needs %d Mito; share with the party?), `UC_Err_NeedMito`,
  `UC_Err_PlayTogether` (no solo), `UC_Err_SameCity`, `UC_Err_MaxPlayerCnt` (max 4), `UC_Emer_WaitParty`. Party
  screens: UC WAITPLAYER 3530000 (15-frame animation), UC REQUESTINVITE 3540000, PARTYUC 2880000. **Exact** (texts);
  the fee amount came from the server.

## The manager (game +0x16c, object of 0x98 bytes)

Built in the world loader (`0x43bf94`: ctor `0x716e60`, init `0x716030`, log "UnderCity Begin/Done"). Fields: `+0`
active ("Not the UnderCity Stage." otherwise), `+4` city id 1-4, `+0xc` state, `+0x24/25/2c/2d/2e` warning flags,
`+0x28` Main HUB attempt, `+0x34` jamming clock (float s, 3600 at reset), `+0x38` HUVs in the wave, `+0x88` deadline
(ms), `+0x8c` next state, `+0x30` CCenter manager (`0x714010`, 0x8c bytes). `0x71a3b0` moves to `+0x8c` when the clock
passes `+0x88`; `0x71a420` runs every frame. **Exact.**

State machine `0x7190c0` (all **exact**; timings in ms):

| State | What happens |
|---|---|
| 0, 1 | packets to the server (ready) |
| 2 | `UC_Ready` alert; +19200 -> 3 |
| 3 | camera `uc_startani` (camera.ini camera30, `effects\UnderCity\uc_cam%d.nif` per city, `0x75489a`), `0x717de0`; +5600 -> 4 |
| 4 | `0x717400` resets the party counters, `0x716200` UI; leader sees `UC_HUB_NeedAccept` (with Accept, 60 s), others `UC_HUB`; +60000 -> 5 |
| 5 | sound 0x4ecd **SID_PVP_RACE_START**, UI effect 0x12 **pvp_countdownstart** 7.5 s; +6000 -> 6 |
| 6 | HUB phase running (`0x7137b0` collisions); packet with the stage |
| 7 | stage cleared packet (`0x78b800`) |
| 8 | party teleported to the start posts (`0x718260`), `uc_startani`; +5600 -> 9 |
| 9 | PLAY2ND; `UC_MAINHUB` / `UC_Fail` / `UC_Fail2` by attempt `+0x28`; +5000 -> 10 |
| 10 | Main HUB phase: clock `+0x34` counts down; `UC_Hurry4` under 60 s, `UC_Hurry5` under 15 s, under 11 s UI effect 0x1d **pvp_tencount** 10 s + sound 0x4ed3 **SID_PVP_RACE_COUNT** |
| 11 | `0x718e40`: asks the server for the wave's HUVs (init\uchuvparam rows of the city, 1 200 000 ms) |
| 12 | HUV wave (PLAY3RD), `0x71a170` sets `+0x38` = count, `UC_HUV` (`0x717510`) |
| 13 | boss wave (PLAY4TH) when the wave's stage is 5: `UC_BOSSHUV`; `UC_TimeCiritical` < 60 s, `UC_TimeCiritical2` < 15 s, countdown < 11 s |
| 14 | +6000 -> 15 |
| 15 | `0x716480` fills UCRESULT, `+0x34` = 7 s; at 0 `0x716bc0` loads the city the party came from and puts the car back at the saved spot |

Server packet handlers (`0x4f7a00..0x4f81f0`, opcodes 0xbb9..0xbcc, 0x7d1..0x7d4): HUB destroyed `0xbbd` ->
`0x718370` (per-player count `+0x44` of the party table, "Hub Exp %d", `UC_Hurry3` when the destroyed count reaches 80),
all HUBs `0xbbe` -> `0x716aa0` (`UC_HUBSuccess1`, next state 7 in 3 s), stage begin `0xbc3` -> `0x719ae0`, barrier
destroyed `0xbc7` -> `0x718670` ("Boss hub. Exp %d"), Main HUB destroyed `0xbc8` -> `0x718590` (`UC_HUBSuccess2`, next
state 11 in 3 s), HUB stage begin -> `0x716f60` -> `0x716f80`, HUV wave begin -> `0x71a170`. **Exact.**

## HUBs (CCenter objects, 0x44 bytes each)

Models (`0x714380`, vector of model lists per type) and collision boxes (`0x711a10` lane objects, `0x711b40` placed
objects, `0x80fec0(box, x, y, z)`; centre at z+4) — **exact**:

| Type | Model | Box (x, y, z) | Use |
|---|---|---|---|
| 0 | `ob_h_uc_00001a_01_idle01/02/03.nif` (variant = slot % 3) | 4 x 4 x 8 | roadside HUB tower |
| 1 | `ob_h_uc_00001a_02.nif` (red texture) | 4 x 4 x 8 | red HUB on a `Red_HUB_n` post |
| 2 | `ob_h_uc_00001a_03.nif` (rings) | 4 x 4 x 8 | barrier HUB |
| 3 | `ufo.nif` | 6 x 6 x 8 | lane object of the Main HUB stage (minimap `UCTOWER_TRAFFIC`) |
| 4 | `ob_h_uc_main_00001a_01.nif` (+ `_fx.nif`) | 22 x 16 x 8, turned | Main HUB |
| 5 | `track_jump01_01.nif` | box from its collision data | jump ramp, faces its red HUB |

Effects loaded with them: `ob_h_uc_00001a_01_explo.nif`, `explo_car_center/left/right.nif`, `ex_cone.nif`,
`uc_lightning.nif` (above red HUBs, +7 m, `0x716f80`), `effects/undercity/changLR` (UI effect 51), `critical` (52),
`huv_boss06_capsule_explo.nif`.

- **HUB phase** (`0x716f80`): 110 slots. Slots 0-9 red HUBs on `Red_HUB_1..10`, slots 100-109 their ramps on
  `Jump_1..10` turned towards the HUB; slots 10-99 the towers of the road map. PLAY1ST shows remaining / total (100).
  **Exact.** Red HUBs need a jump (art and tip `UC_TIP_JUMP` "Jump Attack will increase the BossHUV time!") — **inferred**.
- **The tower walk** (`0x716890(set, slot, side, start)`, **exact**): over the road map (DAT_008ce8c8) entries start,
  start + 2, ... of its list +0xd8 (into +0x98); per entry its links (+0x58 / +0x5c, link +0x10 = the road); a road whose
  id (+8) is on the city's list is skipped (1st 9 / 0x5a, 2nd 0xaa / 0xab, 3rd 0x12 / 0x34 / 0x7d, 4th 0x55 / 0x54 / 0x15);
  side 1 takes the road's lane list +0x14, side 0 +0x20; the piece "slot % count" of that list gets the tower
  (`0x711ca0` -> `0x711a10`: at the point (t0 + t1) / 2 of the piece's path, box 4 x 4 x 8); the walk stops at slot 100.
  `0x716f80` calls it from slot 10 as (side 1, start 1), (side 0, start 0) and, while slots are left, (side 0, start 0),
  (side 1, start 1) again. The .tcs data: 0x12200 junctions {idx, node, croad count, croads, paths}, 0x50200 CROADs
  (road ends at a junction), 0x20200 ROADs {idx, lane count, lanes A, lanes B, lane ids = 0x70300 ids}; the k-th ROAD
  record is the k-th NDPATH record (0x3100 {node, kind, [extra], n, start node, end node, ...}; ROAD indexes have gaps:
  Cras has no 122 / 125). **Inferred**: the list is the 0x12200 junctions, a junction's links its CROADs (each tied to the ROAD ending there
  next to its lanes), the road id its index, side 1 = lanes A, a piece = a whole lane (the tower in its middle).
  Port: `UndercityRoads` (parser + `TowerPicks`), `UndercityStage.BuildHubPhase` (90 towers in every city; the even lane
  sampling only without a road map).
- **Main HUB stage** (`0x719ae0`): Main HUB at the city's MCC post facing MCC_Direction, barrier HUBs on the
  `Barrier_Np` posts, UFOs on lane segments (city lists of road ids, spacing 300 or 700 x 0.036). While barriers
  stand, touching the Main HUB pushes the car away (`0x7137b0`, 20000 N from the HUB). **Exact.** Barrier count by party
  size (5 per player beyond the first: 2p, 3p, 4p sets) — **inferred** from the post names.
- HUD fields (`0x684f00..0x6850d0`, object at game+0x160+0x394): `+0x33c/0x340` HUBs, `+0x350/0x354` barriers,
  `+0x358` (+NN), `+0x35c` clock, `+0x36c/0x370/0x374` HUVs, `+0x39c/0x3a0`, `+0x384/0x3ac` HP, `+0x3b0[4]` party
  counts (PARTYUC). **Exact.** `+0x358` = **my barriers**: the barrier-destroyed handler 0xbc7 (0x4f8050) adds 1 when the
  packet's user id (+0x16) is mine (0x684f80 clamps at 0), 0x717400 (state 4) resets it; PLAY2ND draws it with the digit
  group +0x104 (REMAINNUM, after REMAINPLUS "+"). Port: `Hud.SetMainHub(..., Score.Barriers)` (offline and online).

## Posts (World/Data/Icon_<city>_Client.tdf)

Per city (prefix `post_1st_` Moon Palace, `post_2nd_` Koinonia, `post_3rd_` Cras, `post_4rd_` Oros; ids **exact**
from `0x715f60`, `0x715680` and its tables at `0x87b088` / `0x87b0c8`):

| | Moon Palace | Koinonia | Cras | Oros |
|---|---|---|---|---|
| Start1-4, Direction1-4 | 785-792 | 793-800 | 792-799 | 627-634 |
| Main HUB, direction | 793, 794 | 801, 802 | 800, 801 | 635, 636 |
| Barriers (2p 1-5, 3p 1-5, 4p 1-5) | 795-809 | 803-817 | 802-816 | 637-651 |
| Red HUBs 1-10 | 810-819 | 818-827 | 817-826 | 652-661 |
| Jumps 1-10 | 820-829 | 828-837 | 827-836 | 662-671 |

Unused by this exe: the `post_*_HMUC*` rows (a later layout), `MCC_Start/End`, Cras `MCC_Bounce` (851/852, named in
the exe next to the MCC), Oros's 100 `Communication Station` posts (an older layout).

## HUV waves (init\uchuvparam.xlt, "Init/UCHuvParam" 0x4ac5fa)

Columns Idx, CityID (1-4 as above), Stage (1-4 groups, 5 boss), HuvID (vehiclelist unique id), Level, Num. The
`init\` copy (34 rows, HUVs 10001-10040, levels 30-70, bosses 11009-11012 = Boss Huv077-080) matches this vehiclelist;
the `data\init\` copy names HUVs the list lacks. Wave alerts: `UC_HUV`, `UC_HUVAgain`, `UC_HUVAgain2`,
`UC_HUVAgain3`, `UC_BossHUV`. Cras's boss 11012 is `huv_boss09.kfm` with no NIF body in this install. **Exact.**

## Screens

UC PLAY1ST 3480000 (HUB icon, remaining / total), PLAY2ND 3490000 (clock, HUB icon cur/total +NN), PLAY3RD 3500000
(clock, HUV icon cur/total +NN, HP bar), PLAY4TH 3510000 (clock, boss count, HP gauge), PLAYEXIT 3520000, UC_HP
3550000 (HUV name + bar), UC_TIP 3560000 (JUMP / BARRIER / HUV images), UCRESULT 3570000 (success/fail art with
Tatiana, class D-S, mission time, Main HUB attempts, per player HUB / Jump / HUV / Boss / EXP / Mito / 3 items, best
driver, 120% / SET badges), UCHUVKILL 3580000 ("HUV Destroyed"), UCMAINHUVKILL 3590000 ("MAIN HUB Destroyed").
Digits are copied from hidden template controls (`*NUM0..9`). Alerts `<ucimg><ucname><ucmsg><ucsec n><btnAccept>` are
drawn by the QUESTDIALOG class (`0x688780`); the `ch_uc_*` portraits are not in this install.

## The rebuild

Files: `UndercityData.cs` (posts, uchuvparam, texts, rules), `UndercityStage.cs` (the run), `UndercityHud.cs`
(screens), `UndercityEntrance.cs` (city entrance), `Tests/UndercityTests.cs`. Server: `DriftCityServer/src/economy.js`
reward kind `undercity` (+ `workshop.js` drop), test "undercity: ..." in `test/server.test.js`.

Follows the exe: entrance post, gate model, dialogs and buttons; the state order and every scripted timing above;
the start posts and headings; red HUBs, ramps, 90 lane towers (100 slots), Hurry3 at 80; Main HUB, barriers, shield
push, three attempts with their alerts; uchuvparam waves; warnings and sounds of states 10 and 13; UCRESULT layout;
the return to the saved spot; models, effects and minimap icon names.

Rules (server decided; mirrored in economy.js):

- Offline party: the player plus up to 3 AI partners (default 3, party of four: all 15 barriers). Partners have no
  cars; on their own clock they take a HUB (every 7-11 s), a barrier HUB (14-20 s) or 10 HUV health (4-7 s), counted
  in PARTYUC and UCRESULT. The Main HUB is the player's.
- Portal fee 2000 Mito, taken out of the run's Mito at the end (the server stays the only one moving money).
- Clocks: HUB phase 300 s (then the run goes on; `UC_Hurry` at 60 s, `UC_Hurry2` at 20 s left), Main HUB 180 s per
  attempt (fail after the third), HUV waves 240 s in all, boss 120 s + 6 s per red HUB taken (tip JUMP). UFOs add 10 s
  (`UC_TimeSave`). HUV health 30 + level/2, bosses 200 + 2 x level, both x (1 + 0.5 x share of HUBs left) (tip HUV).
  Red HUBs fall to a car that is airborne or landed within 2.5 s.
- Rewards: EXP 15/HUB, 50/red HUB, 40/barrier, 400 Main HUB, 120/HUV, 1500 boss, + 1000 + 100 x level when cleared;
  Mito 40, 150, 100, 1500, 400, 8000, + 5000 + 300 x level, minus the fee (never below 0 Mito). A cleared run brings an
  item (chase material pool). Class S: cleared on the first attempt within 12 min; A: two attempts within 16 min;
  B: cleared; C: failed after the Main HUB; D: failed earlier. Server: cooldown 120 s, counts capped (90 HUBs, 10
  jumps, 15 barriers, 1 Main HUB, 40 HUVs, 1 boss), a cleared run needs the Main HUB and the boss and 90 s.
- Cras's boss (no NIF) is drawn as another Undercity boss (11009-11011).

## UC_HUVTimeReduce, the UCRESULT badges (this round)

- **UC_HUVTimeReduce** (**exact** trigger): the server's next-wave packet 0xc3 (handler 0x4f2a20, dispatch 0x51958b) picks
  UC_HUVAgain / 2 / 3 by the wave counter (UC manager +0x6c, then +1), sets the jamming clock +0x34 = packet +2 (ms) x
  0.001 and, when the signed change at +6 is negative, shows UC_HUVTimeReduce (portrait only, no text). The UFO packet
  0xc4 (0x4f2990) shows UC_TimeSave. Why the server cut the time is not in the client; **rule** (server
  `src/undercity.js` nextWave, offline `UndercityData.WaveTimeCut`): an HUV group that took longer than 60 s costs the
  next group half the overrun, at most 20 s (`party.uc.phase {clockChange}`); the alert follows the UC_HUVAgain* one
  after 2.5 s.
- **"120%" / "SET"** (**exact** display): 0x716480 -> 0x684820(row, byte +0x4e, byte +0x4f) toggles PARTYn_120 (controls
  at +0x17c..) and PARTYn_SET (+0x18c..) of UCRESULT; the reward packet 0xbcc (0x4f81f0) with +2 == 1 prints 11114
  "Undercity 120% item has been applied.". The server decided the flags; **rules** (server `ucBadges`, offline
  `UndercityData.Badges`): 120% = the driver's EXP drink (item buff `exp`) was on for the reward; SET = the driver's car
  runs a full set in its four performance slots (items.js set parts / `SetParts.CarSet`). The result rows carry
  `boost120` / `set`; 11114 for my row.
- **Rush Time's auto-refusal** (11146, **exact** client flow 0x50e1d0: in Rush Time the invite is answered 0xfe at once):
  `UndercityParty` refuses a `party.uc.invite` while `RushTime.Participating` (waiting in the zone or running) and prints
  11146 with the inviter; the server-run Rush Time answers for the member itself (`party.uc.autorefuse`), the others get
  10943.

Not rebuilt: the `ch_uc_*` portraits (not in this install).
