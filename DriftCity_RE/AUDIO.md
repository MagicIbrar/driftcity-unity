# Audio, ambience, word filter, shop tabs, HUD panels and front-end leftovers

What DriftCity.exe does and how the Unity remake follows it. Addresses are VAs (image base 0x400000). **[exact]** = read
from the code or data, **[inferred]** = interpretation, **[remake]** = the remake's own rule. Unity code under
`DriftCityOffline/Assets/DriftCity/Runtime/` (Audio/, Game/), server under `DriftCityServer/src/`. Tests:
`Tests/AudioHudTests.cs`, `test/namefilter.test.js`.

## 1. env.xlt: the positional ambience [exact]

**Table.** `init\env.xlt`, loader 0x7d4360 (called from the game init 0x43a29a with the env manager at game+0x154),
rows read by 0x7d3d00 with `"%d%s %s%s %d %f%f%f %f%f %d%d%d %d"` into 0xb8-byte records: +0 group index, +4 SID, +0x24
local name, +0x64 file, +0x84 city, +0x88/+0x8c/+0x90 point (Gamebryo x y z), +0xa4 radMIN, +0xa8 radMAX, +0xac type,
+0xb0 misc, +0xb4 Interval (+0x94..+0xa0 are set to 10.0 and not read). 155 rows; city 1 Moon Palace, 2 Koinonia,
3 Cras, 4 Oros (the city number net+0x3b4ec, MINIMAP.md). All 21 wavs (`audio\env\SID_ENV_0001..0021.wav`) are used;
`SID_ENV_1000.wav` (8.8 MB) is in no row and the exe never names it.

**Manager.** The sound manager at 0x8d79c8 (its env part 0x5204e0..0x521fd0, update 0x51f400):
- City stage enter 0x753870 -> **0x521fd0(city)**: when the city changes, drops the old city's sounds (0x5204e0 points,
  0x521d20 groups), then 0x5209f0 and 0x521a70 for the new one, stores the city at +0x170.
- **0x5209f0** (type 1 rows of the city): 0x520920 registers sound id 0x9c41 + index (40001 + group) from the row's file:
  mode 1 (3D) with min / max distance = radMIN / radMAX, looped. One emitter per row at its point.
- **0x521a70** (types 2, 3, 4): one sound per group index (0x520920: mode 2 = 2D; type 2 and 4 not looped, type 3 looped);
  the group's rows are gathered into clusters by misc (0x5212c0), each cluster a list of points. 0x51f0f0 turns every
  cluster with three or more points into a triangle fan (point 0, i - 1, i) on the ground plane (an OPCODE mesh with
  z = 0); 0x51e6f0 tests the listener against it (a cluster without a mesh is never "inside").
- **0x51f400** every frame, volume = manager +0x168 (0 when the option byte 0x8d28c6, sound on, is off):
  - type 1: inside radMAX and not playing -> set the volume, play; playing and outside -> fade to 0 over 1 s (vfunc 0xc4
    with 1.0), stop once below 5 % of the volume; back inside while fading -> the full volume again.
  - type 2: inside any cluster -> a timer counts down; below 0 and not playing -> timer = Interval, rewind, play once.
    Outside -> timer = 0 (so entering plays it at once).
  - type 3: inside and not playing -> fade in to the volume over 1.5 s and play; outside while playing -> fade out over 3 s,
    stop below 5 %; back inside while fading -> fade back in over 1.5 s.
  - type 4: as type 2, but "inside" = the local car is off the road (car +0x8a8) and on neither sand (+0x8b0) nor water
    (+0x8ac) (also ROADS.md 6). The four type 4 rows (one per city, SID_ENV_0001, point 0, interval 5) are the
    countryside ambience between the roads.
- The env groups of the table (32 point sounds: 30 in Cras, one each in Koinonia and Moon Palace): Cras point sounds (SID_ENV_0008 / 0013 / 0014 / 0016 / 0018 / 0019) and areas
  (0002 type 2 seagulls by the harbour, 0006 / 0012 / 0015 / 0017 type 3), Koinonia (0004, 0006, 0007, 0009, 0011 type 3,
  0010 point), Moon Palace (0003 / 0004 / 0006 / 0007 / 0008 type 3, 0005 point); the type 2 rows 150 / 151 (Cras,
  SID_ENV_0020 / 0021) have a single point each, so they never play.

**Remake** `Audio/CityAmbience.cs`: `EnvTable` (rows, `Points(city)`, `Groups(city)`, `EnvCluster.Contains` = the fan
test on (x, z)) and `CityAmbience`, a self-starting DontDestroyOnLoad host that follows `GameFlow.InCity` / `City` (not
the parking lot) and runs the rules above every frame (`Tick`). Type 1 = an AudioSource at the point with a linear
roll-off from radMIN to radMAX **[remake: the client's stop at radMAX then makes no jump]**; groups = 2D sources. The
listener is the AudioListener (the camera), as 0x51f400 uses NiAudioListener; the type 4 gate reads the car's
`CarRoadState` (off road, not sand / water). Volume = `AudioLibrary.SfxVolume`, 0 with the sound off.

## 2. Other environment sounds [exact]

- **Rain**: the weather object loads SID_RAIN (effect.txt 196, exe id 0x4ee4) at 0x433cea when its rain effect starts
  (+0x128 handle; 0x431938 / 0x434686 stop / restart it). Remake: `CityAmbience` loops `sfx/SID_RAIN` while
  `OriginalLook.Current.Rain` and the `WeatherClock` is not indoors.
- SID_STREET_AMBIENT (149) is registered (0x524a02) but nothing plays it; the parking lot has no env rows; the Oros dust
  devils / tornadoes carry no sound (EFFECTS.md, `OrosAmbience`).
- City music stays `CityMusic` / `CarAudio` (OPTIONS.md).

## 3. Voice lines [exact]

`Data\audio\sfx\effect.txt` (read by 0x5232b0) gives SOUND_ID -> file; the exe plays **20000 + SOUND_ID** through
0x52b980 on the manager 0x8d79c8 (0x4f0c = 20236 = SID_PVP_VO_BOOST100). (TUTORIAL.md's gate sound 0x4ec3 is 20163 =
SID_PVP_GATEPASS, not SID_PVP_ALERT.) The SID_PVP_VO_* lines (231..243):

| line | id | where the client plays it |
|---|---|---|
| 1ST / 2ND / 3RD | 0x4f07..0x4f09 | own finish 0x723fe0 (0x72407c) by the finish rank, skipped in a mission battle (feature 0x10000 of 0x8c7f88); then SID_PVP_RACE_WINNER / FINISH |
| NOTHING, BOOST50, BOOST100, SKIDRUSH, REPAIR, UNTOUCHABLE, BOOOOST | 0x4f0a, 0x4f0b, 0x4f0c, 0x4f10, 0x4f13, 0x4f0f, 0x4f0e | gate effect types 0..6 of the local driver (PlayerEvent -> 0x726f20, jump table 0x7275c8), each with its UI effect 36, 33, 32, 40, 34, 37, 38 |
| TEAMBOOST | 0x4f0d | gate effect type 7 (team boost, 0x7271c9; UI effect 35) |
| FIRST | 0x4f11 | the local car's rank became 1 during a race (0x72b8ea) |
| JUMP | 0x4f12 | take-off while a PvP stage runs (0x4851dc, inside the jump code 0x4809c0) |
| BOOST100 | 0x4f0c | also the first parking-lot arrival of a run (ParkingLotBoost, 0x762f70) |

Note: the UI effect numbers show that **type 1 is the 50 % charge and type 2 the 100 % charge** (ScreenTable 33 =
pvp_boostcharge50, 32 = pvp_boostcharge100), and type 4 is the repair (34 pvp_crash_recover100). The remake's
`GameEffects.GateEffectNames` / `PvpGateEffects` (Charge100 = 1, Charge50 = 2, type 4 without a banner) have 1 and 2
swapped; see the hook list.

**Remake** `Audio/VoiceLines.cs`: `Rank(rank, missionBattle)`, `GateEffect(type)`, `TookLead()`, `Jumped()`,
`ParkingLotBoost()` (used by the parking lot), `RankFile` / `GateEffectFile` for checks. `AudioLibrary.SoundFile(id)` /
`PlayId(id)` play by the exe's id.

## 4. Sounds the US data lacks [exact names, remake choices]

The exe names 30 SID_* sounds that `audio\sfx` does not ship. The four the remake plays and their stand-ins
(`AudioLibrary.Substitutes`, used by every `PlaySfx` of a missing file):

| missing | played at | stand-in | why |
|---|---|---|---|
| SID_PATROL_PARTY_COUNT | party patrol, 10.5 s left (0x61d829) | the last 10.5 s of SID_PVP_RACE_COUNT | the same countdown (one beep every 2 s); the full clip is the 20 s race-over count (19 s) |
| SID_GUARD | part conversion, 10732 (0x4f53a0) | SID_UPGRADE_SUCCESS | the parts remodelling success, the same kind of operation |
| SID_VALENTAIN_COMPLETE | basket / harvest box revealed (0x694098, 0x692d00) | SID_LUCKY | a reward-reveal jingle (1.7 s; the Mittron enhancer drop) |
| SID_XMAS_EVENT_SUCCESS | tree decoration given (0x694aa4) | SID_QUEST_SUCCESS | the event-style success jingle (2.1 s) |

The others (SID_GACHA_EFFECT, SID_GOODHARVEST_BOX1-3 / SLOT / WOW1-5, SID_VALENTAIN_EATING / EXP / SEND / WRAPPING,
SID_XMAS_EVENT_FAIL, SID_RING, SID_ROCK, SID_METEOR, SID_UFO, SID_UFOCRASH) belong to screens the remake does not drive
(the harvest slot machine, mission battles); they get the nearest shipped sound in the same table.

## 5. Sound ids

`AudioLibrary.SoundFile(exeId)` reads effect.txt (149 rows, ids 101..245 and 10303..10308 for the horn items).
Columns AMBIENT / MIN_DIST / MAX_DIST / LOOP are 2D flags and defaults the env rows override.

## 6. The word filter: operatormisrepresent + slang [exact]

- Object at game+0x13c (made at 0x43b3d1, vtable 0x887780): [1] 0x7e0200 loads `init\slang` (6 rows: `=w=)`, `=w=`,
  `=)`, `=/`, DEL, `=w=)/`) into +8, [2] 0x7e0340 `init\operatormisrepresent` (201 rows: ADM, DEV, BA, iiii, `[`, `]`,
  admin, GM, staff names erina / duffy / jason ..., driftcity, ijji, the Latin-1 letters U+00C0..U+00FF) into +0x18; each
  word is converted with MultiByteToWideChar and lowercased (both lists, and the text checked).
- +0xc 0x7df5d0 / +0x14 0x7df6c0: "contains" (case-insensitive substring) over slang / operator names.
  +0x10 0x7dfcd0 / +0x18 0x7dfed0: censor: every character of every match is replaced with a random one of `!@#$%^&*`.
- Callers: **character creation** 0x749f02 (slang, then operator names -> **10606** "That ID is not allowed. Please use a
  different name."), **room titles** CREATEROOM OK 0x57a3ee (slang -> **10608** "That Room name is not allowed..."),
  **chat** sent 0x5bcf60 (slang censored), a received system chat line with slang is dropped (0x5b4b70), notes 0x670c50
  and party invites 0x61e100 (censored), custom-shop gifts 0x64f9c0 (operator names + both censors), crew names (10607,
  0x543xxx / 0x610xxx / 0x676xxx, same pattern as creation).
- **Remake**: `Game/NameFilter.cs` (`CheckDriverName`, `CheckCrewName`, `CheckRoomTitle`, `Chat` / `CensorSlang`);
  the offline creation screen (`GameFlow.FrontEnd.cs NameProblem`) refuses with 10606. **Server** (authoritative online):
  `src/namefilter.js`; `driver.create` -> `{ok:false, code:10606}`, `race.host` title -> `{code:10608}`, `world.chat`
  censors slang, `crew.create` / `crew.rename` -> `{code:10607}` (teams.js checkName). The filter is on in the game and
  off under the test runners (their drivers are "Tester", "InfoTarget", "auctioneer", which the list refuses: `test`,
  `info`, `auction`): `node --test` (NODE_TEST_CONTEXT) and env `DC_NAMEFILTER=0`, which the Unity PlayMode suite sets
  (`NameFilterDefaults`) so the Node servers its online tests start inherit it. `NameFilter.Enabled = true` /
  `createServer({nameFilter:true})` turn it on for the filter's own tests; production needs no setting.

## 7. vshopcategory: the custom shop's tabs [exact]

- Loader 0x7deea0 (game init 0x43a239, object game+0x140): 0x44-byte records +0 top, +4 main, +8 sub, +0xc Top Menu
  Enable, +0x10 Enable, +0x14 Multiple Selection, +0x18 Stackable, +0x1c / +0x20 Coin / Mito gift, +0x24 name.
- Readers: 0x7dea10 (by top, first row) Top Menu Enable -> 0x6600f0 sets the enabled bit (flag 4) of the five top tabs
  of both VSHOP bars; 0x7de980 / 0x7de9b0 (by sub) Multiple / Stackable (product list 0x5a5440, checkout 0x642d20,
  0x651670, 0x653cf0); 0x7de9e0 (by sub) the name. The Enable column has no reader.
- The US table: TOP_COLOR's Top Menu Enable is 0 (the remaster's ADV shop shows paint / neon / tint under Decoration,
  2400000); DECORATION, TUNNING, ETC, RENT are 1.
- **Remake**: `Game/Shops/VShopCategories.cs` (rows, `TopMenuEnabled`, `Multiple`, `Stackable`, `Name`, `SubOf(item)`,
  `MainOfTab(control)`); `CustomShop`: the top tabs follow Top Menu Enable except COLOR, which the classic layout keeps
  (its Decoration page has no neon / tint) **[remake]**; a sub tab needs its main category in the table; the product
  list's header shows the table's sub category of the listed parts ("Bumper", "Intercooler", "Aero set", "Flame" ...).

## 8. HUD: main bars, NOTEBOOK, ITEMSHOP, SPEEDITEM [exact]

- **Main bar by MainUIType**: 1 = ADV 3010000 (handler 0x5f11a1: MYINFO 0x5edaf0, INVENTORY 0x5ee010, SHOP 0x5ee1d0 =
  the custom shop, COUPON 0x5ee900, LADDER 0x5eee60, QUEST 0x5ef380, GOTCHA 0x5ef880, LICENSE 0x5effe0), 0 = basic 3020000
  (handler 0x5e8e0b..0x5e92ef): SHOP 3020016 toggles **ITEMSHOP 4070000**, NOTEBOOK 3020017 toggles **NOTEBOOK
  4060000** (open SID_SYSWIN_CREATE 0x4e8a, close SID_SYSWIN_DESTROY 0x4e8b), the F-key buttons HOTKEY_COMM 3020001 ->
  0x5e7ea0, MYINFO 0x5e7fa0, INVENTORY, SHOP 0x5e81e0 (custom shop), COUPON 0x5e84a0, LADDER 0x5e8590, QUEST 0x5e8640,
  GACHA 0x5e86d0, LICENCE 0x5e89d0, SKILLBOOK 0x5e8ad0.
- **NOTEBOOK 4060000** (handler 0x5e9390): COUPONBOOK 4060005 -> 0x5e84a0, MISSIONBOOK 4060006 -> 0x5e8640 (quests),
  LICENSE 4060007 -> 0x5e89d0 (licences; content bit 0x2000000 of net+0x290), 4060008 -> 0x5e8ad0 (skill book; no control
  in the US layout), CLOSE 4060004; every button hides the notebook (0x7b22d0). NATION J: 116 F5, 118 F7, 120 F9.
- **ITEMSHOP 4070000** (handler 0x5e9470): VSHOP 4070005 -> 0x5e81e0, CAPSULEBOX 4070006 -> 0x5e86d0 (gacha), CLOSE.
- **SPEEDITEM 340000 / 350000** (vtable 0x87283c; COMBO.md 7): add 0x62a1b0 (content bit net+0x28c & 4) takes the item
  record's icon (+0x74); callers: drop pick-up 0x4fb280, event obtain 0x6f3df0, item popup 0x6f4c30, quest rewards
  0x6f6ba0. A queue of at most six (the seventh drops the oldest); update 0x6299d0 moves them on an arc of radius 155
  around ORIGIN (angles 210, 210, 186, 171, 161, 151, 141 deg, table 0x872820) with 0.2 s steps and fades an entry
  after 3 s over 1 s.
- **Remake**: `Game/Hud.cs` opens the bar of `GameOptions.MainUIType` (and swaps it when the option changes) and wires
  both bars; `Game/HudPanels.cs`: `Notebook`, `ItemShop` (`VShop()` = the custom shop in a city, the shop chooser in the
  parking lot), `SpeedItems.Add(icon)` (entries laid into ITEM01..06, newest first, 0.2 s pop-in, 3 s, 1 s fade
  **[remake: the slots instead of the arc]**). The ADV SHOP keeps the remake's shop chooser (`Menus.Shop`): the remake
  has no city buildings for the dealer / tuning / parts shops the client reaches there **[remake]**.

## 9. Character deletion timer [exact]

- SELECTCHARACTER 4140000: DEL_BTN -> MSGBOX 3230000 (0x767c80) 10530 "Enter the name of character to delete." with
  DELETE; the server marks the row: state `'R'` (+0x3e = 0x52) and the deletion time (+0x42, seconds).
- Refresh 0x766304: a marked row shows DEL_MARK / DEL_COVER / DEL_CANCEL and TIME = the time left as the largest unit:
  11087 "%d Day(s)" (>= 1 day), 11088 "%d Hour(s)", 11089 "%d Minute(s)", else 11097 "%d Second(s)" (0x7664dd..0x766589).
- DEL_CANCEL -> 11098 "Cancel the deletion of this character?" (0x766953). When a marked row's time has passed,
  0x768010 asks 11099 "Are you sure you want to delete this ID : %s ?" once (row kept at +0x2c4); Yes -> 0x767b70 deletes.
- **Remake** (`Game/CharacterDeletion.cs`, `GameFlow.FrontEnd.cs`): offline only (a server driver is not deleted from the
  game). The typed name must match the driver (a different name leaves it **[inferred]**); the mark is a file beside the
  slot's save (`*.json.delete`, Unix seconds); the wait is **24 hours [remake]** (the server's in the client); the TIME
  counts down every second; the final question follows the client.

## 10. The tutorial car in the parking lot [exact unless marked]

- 0x6cb790: the current car's record kind is 3 (the tutorial car the server lends an apprentice, net+0x24c & 0x80000).
  **Remake**: `Apprentice.OnTutorialCar(p)` = an apprentice driving his first car (the one picked at creation) **[remake:
  no separate loan car]**.
- **Refusals** 0x763f30: on the tutorial car, entering any zone but 0 (none) and 6 (the Battle Zone building) shows 11285
  "You cannot access during Tutorial" and puts the car back at its last free spot (0x7629b0). Remake:
  `Apprentice.Refuses(p, area)`, checked first in `ParkingLotStage.Enter`.
- **Arrival spots** 0x762f70 (fresh arrival with the apprentice flag and the tutorial car): (0.5, -0.8, 2.5) heading
  0.165 pi (the dome centre facing the Battle Zone at about (102, 57)) with the guide line; with step 3 or 7 (a failed
  lesson) 0x762ca0 puts the car at (75, 45) by the Battle Zone (heading toward it **[inferred]**). Remake:
  `Apprentice.ArrivalSpot(p, ApprenticeCourse.ReturnStep)` in `ParkingLotStage.ArrivalPose`; `ReturnStep` is the step of
  an aborted course (0 after a finished one).

## 11. Hook lines for files this round does not own

- `Game/GameEffects.cs` `SfxOr("SID_PATROL_PARTY_COUNT", ...)`, `SfxOr("SID_GUARD", ...)`, `SfxOr("SID_XMAS_EVENT_SUCCESS",
  ...)`, `SfxOr("SID_VALENTAIN_COMPLETE", ...)` (l. 421, 457, 505, 513, 522): `Sfx("SID_...")` (AudioLibrary's stand-ins).
- `Game/GameEffects.cs` `RaceFinished(bool won, bool newRecord = false)`: add `int rank = 0` and, before the banner,
  `VoiceLines.Rank(rank);` (0x72407c); callers `RaceMission.cs:573` `GameEffects.RaceFinished(r.Place == 1, record, r.Place)`,
  `OnlineRace.cs:510` `GameEffects.RaceFinished(place == 1, ..., place)`.
- `Game/PvpGateEffects.cs` `Apply` (local driver), after `GameEffects.PlayGateEffect(type);`: `VoiceLines.GateEffect(type);`
  and the 1 / 2 swap: `Charge50 = 1, Charge100 = 2`, `GateEffectNames[1] = "pvp_boostcharge50"`, `[2] = "pvp_boostcharge100"`,
  `[4] = "pvp_crash_recover100"` (0x7275c8).
- `Game/GameEffects.cs` `JumpUp`: `if (RaceMission.Active || OnlineRace.Active) VoiceLines.Jumped();` (0x4851dc).
- `RaceMission.cs` / `OnlineRace.cs` where the local place is updated: `if (place == 1 && lastPlace != 1 && lastPlace != 0)
  VoiceLines.TookLead();` (0x72b8ea).
- Drops: `GameEffects.GetItem()` callers (and event obtain popups): `SpeedItems.Add(icon);` (0x62a1b0).
- `Game/CitySession.cs:42-49`: drop the `ID_MAINBUTTON_*` lines (the HUD owns both bars now; today they send NOTEBOOK to
  the quest list and HOTKEY_SHOP to the shop chooser, over the HUD's NOTEBOOK 4060000 / custom shop).
- Room titles, `PvpLobby` / `PvpWaitRoom` create-room OK (0x57a3ee): `var bad = NameFilter.CheckRoomTitle(title); if (bad
  != null) { UiPopup.Message(NameFilter.Text(bad)); return; }`.
- Chat send (`Net/ChatBox.cs`, before sending / echoing): `text = NameFilter.Chat(text);`; a received system line:
  `if (NameFilter.HasSlang(text)) return;` (0x5b4b70).
- Crew names (`Social/Teams.cs` create / rename, server `src/teams.js checkName`): `NameFilter.CheckCrewName(name)` ->
  10607 / `world.names && world.names.crewName(name)`.
- Driver rename (server `src/services.js` rename.*): `names.driverName(name)` -> 10606.
