# Parking lot ("Driver Dome") stage, gates and teleport flow

Recovered from `DriftCity.exe` (image base 0x400000). Decompiled sources: `decompiled/phys/0xADDR.c` (made with
`dec.py`). **[exact]** = read from code or data, **[inferred]** = interpretation. Text ids are `localize\globaltext.xlt`
keys (US column quoted); config keys are `localize\globalcontent.xlt`.

Unity port: `Runtime/Game/ParkingLotStage.cs` (stage, zones, SELGATE), `Runtime/Game/GameFlow.cs` (flow: `OnLoginComplete`,
`EnterParkingLot`, `EnterGate`, `ReturnToParkingLot`, `SkipParkingLot`), `Runtime/Game/Minimap.cs` (floor image),
`Runtime/Game/Menus.cs` (`CityTravel`, `WorldMap` -> VIEWMAP_P_1), `Core/HitMesh.cs` (accepts the "HIT" magic).
Test: `Tests/GameFlowTests.cs` `ParkingLotGateEntersCity`.

## 1. Summary

- The parking lot is **stage 9**: its stage object's enter handler 0x764320 runs when the new stage is 9
  (`cmp [esp+0x14], 9`, then logs "Parkinglot Stage Enter :%d") **[exact]**. The game calls it the **Driver Dome**:
  10540 = "Driver Dome" (KR 비밀기지 "secret base") is the location name for channel ids < 3 (0x607f50 / 0x6693f0) **[exact]**.
- **When it is entered** **[exact]**:
  - after login: packet 0x56's handler 0x4ef1a0 (character/car data, around ServerSelectHandleUI) sets the next stage to
    9 (0x4ef29d) when the character data is there; otherwise 0x59fd80 runs instead (tutorial, **[inferred]**);
  - by the zone-move reply, packet 0x30f's handler 0x4f5e80: zone 0 = parking lot -> 0x761e40 + stage 9; zones 1..5 =
    MoonPalace, Koinonia, Cras, Oros, Taipei -> world name "<City>" + "Main" (0x753ec0) -> stage 13 (0xd);
  - from a city: the RQ house's ENTRANCE dialog (400000), button `ID_ENTRANCE_RQHOUSE_PARKINGLOT` (control id 400009 =
    0x61a89) -> 0x561d92 .. 0x561e8d sets stage 9;
  - from the garage stage: text 10518 "Leave the Garage?" (KR "leave the garage and move to the secret base") and the
    garage code at 0x74fe79 sets stage 9.
- **Leaving it**: the exit zones ask the server for gates (packet 0x3ed), the SELGATE screen lists them, and picking one
  makes the server answer with the zone move 0x30f -> city (stage 13). So both ways between cities go through the
  parking lot: **city -> RQ house "Parking lot" -> parking lot -> exit -> gate -> city**. Stage ids: 7 = garage, 8 = team
  center, 9 = parking lot, 10 = autoshop (0x7438a0 compares 10), 13 = city gameplay **[exact for 7/8/9/10 as set by the
  zone code; 13 = the stage the zone move enters, inferred to be the city stage]**.

## 2. Files (World archive) **[exact]**

Loader 0x7620a0 keeps an array of floors (stride 0x10: node, `_a`, `_fx`, main) on the stage object (+4, count +8):

| floor | drawn | picked only | collision (0x764320) | minimap (0x5f2050) |
|---|---|---|---|---|
| 0 (F1) | `parkinglot.nif` + `parkinglot_fx.nif` | `parkinglot_a.nif` | `floor_p.hit`, `wall_p.hit` | `_parking.dds` |
| 1 (B1) | `b1_parkinglot.nif` + `b1_parkinglot_fx.nif` | `b1_parkinglot_a.nif` | `b1_floor_p.hit`, `b1_wall_p.hit` | `_parkingB1.dds` |

- `_fx.nif` / `_a.nif` names come from replacing ".nif" with "_fx.nif" / "_a.nif" (0x87d870 / 0x87d868). The main and fx
  NIFs are attached to the floor node; the `_a.nif` is only stored and updated, never attached (not drawn).
- `World/parkinglot/parkinglot_icon.nif` (billboard signs pi_0N / pib_0N) is attached to the stage root for all floors.
- Floor switching = `SetAppCulled` on the other floor (0x761c90; it also copies the floor's `_a`/`_fx` into +0x24/+0x28,
  the two pick targets).
- **This install has no B1 files** (no `b1_*.nif`, `b1_*.hit`), so only F1 exists. `parkinglot_a.nif` has no `area_b1`;
  its six ramps (the "The first basement" spots on the map) are named `area_f1`.
- The `.hit` files start with the magic "HIT\0" (uppercase), version 0x1321a30 (12-byte vertices). floor_p: 6837
  triangles, x/y +-350, z 0..2.5; wall_p: 14909 triangles, z up to 37. The visual floor and floor_p.hit agree (z = 0 at
  the sample points; checked with the NIF world transforms).
- `parkinglot_fx.nif` hidden NiTriStrips `parts` (-92,45), `autoshop` (-12,127), `auction` (0,-136), `battle` (102,57),
  `garage` (-88,-44), `teamcenter` (101,-59) are the building trigger zones; the rest is red light cylinders + particle
  emitters (beamsparkle03).
- `parkinglot_a.nif`: `area_exit` (six strips at the outer exits, at 0, +-60, 180, ... degrees), `area_f1` (six ramps at
  90, +-30 degrees), plus flat `area_garage` / `area_autoshop` / ... planes that the pick code does not name (no effect).
- Map images: `_parking.dds` (1024 px; image up = Gamebryo +y, about 1.165 px/m, so it covers about +-440 m) and the
  full map `VIEWMAP_P_1` (570000, `ID_VIEWMAP_P_1_DUMMY` 614x614 = the same image; B1: 580000) **[scale inferred from
  the building icon positions]**. MINIMAP 190000 holds `_parking.dds` / `_parkingB1.dds` as unnamed controls 190037/38.
- `init\camera.ini [camera2]` "parkinglot" (link, fov 60, zoom 25) is used by the PvP stage (0x765c5f calls 0x4dcd90 with
  "parkinglot"), not by stage 9, which never selects a camera, so it keeps the normal driving camera **[inferred]**.
- Music: `bgm\DriftCity_parkinglot.mp3` (also `DriftCity_parkinglot2.mp3`) **[file exists; which one the client plays
  was not traced]**.

## 3. Arrival (0x762f70) **[exact]**

- Sets the map index (`0x780510()+0x3b4ec`) to 0 = parking lot (1..5 = the cities, used by 0x5f2050 for the minimap).
- Loads the floors (0x7620a0); 0x761e40 picks the floor: B1 when the driver is level > 30 **and** permission bit 2 is
  set, else F1.
- Fresh arrival (2nd argument != 0): one of six points chosen with `rand() % 6`:
  (250,0) PI, (120,210) -0.66 PI, (-120,210) -0.33 PI, (-250,0) 0, (-120,-210) 0.33 PI, (120,-210) 0.66 PI
  (Gamebryo x, y, heading). With the forward = (cos h, sin h) convention that 0x7629b0 also uses, every point is on an
  exit axis facing the dome centre (arriving through a gate) **[direction inferred]**. Tutorial drivers (flag 0x80000)
  get special spots instead.
- Return from another stage (argument 0): 0x7629b0 puts the car at the last spot outside every zone (below).
- **ParkingLotBoost**: when config `ParkingLotBoost` (= 1; comment "after the first run, fill the booster to 100% once")
  is set and the static counter 0x8ebeb4 is 0: sound 0x4f0c (= `SID_PVP_VO_BOOST100.wav`, from the sound table at
  0x523000..), 0x47beb0(100.0, 1) adds 100 to the car's booster gauge (+0x1a68, clamped to its max), HUD effect
  0x69b900(0x20), then the counter goes up. So **once per client run, the first arrival fills the booster**.

## 4. Zones (pick 0x761a30, dispatch 0x763f30, actions 0x763840) **[exact]**

0x761a30 casts a ray from (car x, car y, z = 10000) straight down (NiPick, first record):
1. into the `_fx.nif` (+0x28): the hit geometry's own name `teamcenter`=5, `garage`=3, `battle`=6, `autoshop`=7,
   `auction`=4, `parts`=2;
2. then into the `_a.nif` (+0x24), overriding: the hit geometry's **parent** name `area_exit`=1, `area_f1`=8,
   `area_b1`=9. Names compare including the terminator (exact match).

Per frame (0x763f30, unless stage 11/8 or certain car states):
- area 0: 0x7628d0 records the car position and heading (+0x74..+0x80, +0xac) as the last free spot.
- area changed: if the previous area was not 0, sound 0x4e8d (= `SID_ENTER_SUCCESS.wav`); store the new area (+0xa8).
- areas 2..9 check a permission bit of the player (`[+0x268]`: 2->1, 3->2, 4->4, 5->8, 6->0x10, 7->0x20, 8->0x40,
  9->0x80). Missing bit -> text 11913 "Restricted Area", area reset to 0, car put back (0x7629b0).
- tutorial (0x6cb790) -> 11285 "You cannot access during Tutorial", same reset.
- otherwise 0x763840(area), then for areas other than 0, 6, 9 the car is re-placed at its own position (stopped).

0x763840:

| area | action |
|---|---|
| 1 exit | if no request is pending (0x30c): close the windows at +0x130/+0x134, SELGATE window (+0x3a8) +0x748 = 0, send packet **0x3ed** to the server; the reply fills SELGATE |
| 2 parts | "PartsShop_Open" (parts shop UI) |
| 3 garage | next stage 7 (garage) |
| 4 auction | age check (10995), level >= `AuctionMinLevel` (11828), else open the auction (0x598f60) |
| 5 teamcenter | within 60 s of something: sound 0x4e8d + stage 8 (team center); else UI 0x3eb |
| 6 battle | battle zone (party / car checks: 10066, 10067, 11101) |
| 7 autoshop | next stage 10 (autoshop) |
| 8 area_f1 | floor 0 (0x761c90), camera reset, 0x76cab0(9) |
| 9 area_b1 | level > 30: floor 1; else 10068 "Entry is limited to Level 31 or higher users." + reset |

0x7629b0 (put the car back): last free position + 2 m along its heading (heading + PI when +0xac > 0), same heading,
for stages 9/11/12/14.

`localize\parkinglot.xlt` has "press the space bar to enter the car shop / my garage / item exchange / parts shop / team
center" texts, but the code above enters on the zone change without a key **[exact for this build]**.

## 5. Gate selection (SELGATE)

Dialogs (game.nui): 3620000 full-screen backdrop (ui_contents52), 3630000 the gate list (`ID_SELGATE_MAIN_BTN1ST/2ND/3RD`
with the "1/2/3 GATE" art, `STATUS*` text, `INDICATOR_BASE*`, 15 `GRAIN*_00..14` bars each), 3650000 / 3660000 the
channel / team-capture info (major / semi channels, team names, captures, accumulated Mito), 3670000 tabs
`ID_SELGATE_TAB_MAJOR` / `ID_SELGATE_TAB_SEMI`, 3680000 `ID_SELGATE_PREVIOUS` **[exact]**.

- Packet 0x2ab (handler 0x4ec560) sums per-gate player counts and for each gate calls 0x689970(gate, status text,
  players) on the SELGATE window: the status is 10762 "Few" below 5%, 10763 "Many" below 50%, else 10764 "Crowded" of
  config `SelectServer_PlayerCapacity`; the bar shows 3 grains plus `players * 12 / SelectGate_PlayerCapacity` (= 700),
  at most 12 more (15 in all) **[exact]**.
- Clicking a gate (0x689d90) sends the choice (0x79aab0); the server's zone move (0x30f) then loads the city
  **[exact for the calls; the payload was not decoded]**.
- Location names: channel ids map to (city, number) and read "Gate %2d - Moon Palace" (10861), Koinonia (10862),
  Cras (10863), Oros (10864), Mitro (11395), or "Channel %2d - <city>" (10542..) without flag 0x200 **[exact]**. So each
  gate is a numbered entrance of one city; the server decides which gates are offered (`Main.OpenGate`, a 4-bit
  setting read by 0x453250) **[inferred]**.
- The unrelated "Main.*" flags of 0x453250 are server feature switches (SkidShop, Sticker, ..., OpenGate, NormalCh,
  SemiCh, ...).

## 6. What the remake does

- Login (offline or online, new or returning driver) -> `GameFlow.OnLoginComplete` -> parking lot (or the last city
  when `GameFlow.SkipParkingLot` is set; the existing tests set it).
- Parking lot: F1 NIFs drawn (no NIF colliders), floor_p/wall_p.hit as MeshColliders (floor faces turned up for the wheel
  rays), particles of the `_fx.nif`, icon billboards, weather lighting + sky like the cities, chase camera, speedometer,
  main bar, minimap showing `_parking.dds` around the car (heading up), M = VIEWMAP_P_1 with a marker. The roof and big
  shells don't cast Unity shadows (the client has no such shadow).
- Arrival at a random one of the six points; ParkingLotBoost once per run (booster full + SID_PVP_VO_BOOST100).
- Zones picked from the hidden meshes exactly as above (highest face under the car; vertical faces skipped).
  Exit -> SELGATE (3620000 + 3630000 + 3680000, tabs 3670000 page the four gates three at a time); a gate loads that city
  and remembers it as the profile's city. Garage -> garage menu, autoshop -> car dealer, parts -> parts shop (the car
  is held while the menu is open and put back at the last free spot afterwards, like returning from those stages).
  Auction, team center, battle zone (not in the remake) and B1 (no data) -> "Restricted Area" + put back. area_f1 ->
  floor 0 + stop.
- From a city: key C opens the travel list: "Driver Dome" (back to the parking lot, as the RQ house button does) and
  each "Gate N - City" as a shortcut. Online mode leaves the channel in the parking lot and enters the city's channel
  after a gate (`OnlineSession.EnterCity`).
- Differences: no gate population (the bar shows its 3 base grains and "Few"); no channel/team panels; the put-back uses
  the last free spot without the client's +2 m step (it would land back inside the zone); the gate rows carry a
  "Gate N - City" label because the art only has the numbers.
