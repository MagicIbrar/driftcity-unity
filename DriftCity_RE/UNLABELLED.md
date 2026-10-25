# The unlabelled code, and the map's Traffic / Data tables mislabels

The code map (CODEMAP.md) left 577 functions / 108.6 KB as "Unlabelled game code": functions without strings,
dialog ids or handler roles whose callers did not vote clearly. They were grouped into runs of neighbouring
functions (MSVC keeps an object file together), identified from decompiles (`decompiled/phys/0xADDR.c`), callers and
callees, and given subsystems in `classify.py` (`OVERRIDES += [...]`, appended after the earlier entries).
**[exact]** = read from the code, **[inferred]** = from callers, neighbours and data.

## 1. What the runs are

| range | size | what | subsystem |
|---|---|---|---|
| 0x70b7f0..0x70db30 | 9.1 KB | the NPC "ment" markup parser **[exact]**: element names (u16 strings 0x87a376..0x87a65c) `name pimg iimg timg text sub_text next nextlvunder clear licence select jump pending effect mycar gate ucname ucsec ucimg ucmsg ucenter_img vcenter_img vcimg daymissionimg ghrestaurantimg ghdistributionimg eventimg dormantimg mita500img btnAccept ending ...` and the `$name $level $money $pos $ranklevel` substitutions. `<ending ROO|OMD>` (0x70d320 / 0x70d370 -> 0x6f6fe0 / 0x6f7020 -> 0x4e1030) starts the story ending (section 2). 0x70b830 (123 callers) is a wide -> multibyte string helper (WideCharToMultiByte twice) | Missions (0x70b830: Platform) |
| 0x4dd4b0..0x4e11e0 | 7.3 KB + labelled neighbours | the story ending and credits (section 2) | Stages & flow |
| 0x50e410..0x5126e0 | 13.7 KB | remote actor spawn queue: 0x510750 applies an actor-create record (0x51d130 create, +0x1c50 appearance, 0x516490 / 0x513780 / 0x5102b0); 0x512520.. push records into a std::deque capped at 0x80 entries; 0x510830..0x512450 and 0x50ed90..0x510060 are that deque's insert / erase / copy instantiations; 0x50e410.. "PlayerEvent(" stream dumps | Networking |
| 0x4ce890..0x4d0460 | 7.2 KB | the developer profiler overlay ("%s: Min/Max/Avg/Rt/Tm", tree of named timers) | Platform & system |
| 0x6e7cd0..0x6e9250 | 7.1 KB | equipping parts ("NewDockingParts" switch) and re-applying the car's stats after the server changed the car (0x6e9250, called by 12 packet handlers) | Inventory & items |
| 0x73f8d0..0x741250 | 6.1 KB | a packet filling a PvP list (0x39-byte records -> 0x508450) and std::vector&lt;0x1dc-byte record&gt; code | PvP |
| 0x720e90..0x721720, 0x71bd40.., 0x720000.. | 4.1 KB | lap-time / reward pick (PVP.md 13.9 range 0x71bc50..0x7212d0) | PvP |
| 0x4edf50..0x4eec90 | 3.5 KB | std::sort of 0x7c-byte result rows (mode 3 compares the float time) | LIB C runtime |
| 0x43d710..0x43df30 | 2.2 KB | std::map instantiation ("invalid map/set&lt;T&gt; iterator") | LIB C runtime |
| 0x607380..0x6113f0 (5 runs) | 5.2 KB | friend / party list sorting and helpers (wcscmp, `+0x54` keys) | Party & friends |
| 0x6d89f0.., 0x6d5220.., 0x6d40d0.., 0x6d36f0.. | 3.9 KB | parts list sorting (record kind 5 compares `+0x240` / `+0x2f8`) | Parts shop |
| 0x6960e0 | 0.7 KB | 11712-11714 "License Reward Money" / "Amount Paid" / "License" | Parts shop |
| 0x76c560..0x76cb60 | 1.6 KB | server status texts 10758-10764 (Preparing / Processing / Inspecting / Failed to connect / Few / Many / Crowded), 11338 | Front end |
| 0x422d00..0x423240 | 1.4 KB | the per-frame cull **[exact]**: 0x423000 clears the visible set, culls the world (0x4baee0) and adds the four actor lists (`+0x140` cars, `+0x160`, `+0x180`, `+0x120`) | World streaming |
| 0x698aa0 | 1.0 KB | the 3D guide arrow in front of the camera toward the target point: re-routes (0x4e33c0) beyond 120 m (14400 squared) off the route or 300 m from the last point, blinks faster within 300 / 1000 m (0.333 / 0.666 s) | HUD |
| 0x69e2f0..0x69e530 | 1.7 KB | a marker NIF drawn over each listed car (net `+0x3b554` records of 0x84 bytes, mode 3) | Effects |
| 0x732730..0x7329d0 | 1.0 KB | ODE-driven pieces of the meteor / quake effects ("meteo", "Exodus Quake" neighbours) | Effects |
| 0x53a6b0.., 0x53b7f0.. | 2.3 KB | option apply ("10158 Applying settings") | Options & quit |
| 0x49d5b0..0x49dbc0 | 1.6 KB | std::map&lt;string, ...&gt; for tables | Data tables |
| 0x402d10..0x403150 | 1.1 KB | "CreateGeometryPool": a NiTriShapeDynamicData pool used by the vehicle visuals | Vehicle visuals |
| 0x440850..0x440bc0 | 1.0 KB | render-to-texture (CreateNiTextureFromD3DTexture) | Renderer |
| 0x448a70.., 0x4b8120.. (pool of 0x1ea8-byte actors), 0x4c7030.., 0x4a9630.. | 1.9 KB | vehicle actor pool and destructors | Traffic & HUV AI |
| the rest (about 60 small runs) | 9 KB | named by their callers and neighbours (see the comments in classify.py) | various |

After these overrides the call-vote / neighbour-smoothing pass in classify.py relabels about 330 KB of *unstudied*
functions as a side effect (labels from votes are chaotic: any change cascades), and leaves a few new runs
unlabelled; those 18 runs (12 KB) are pinned to their callers' subsystems in the same list. Unlabelled is now 0.2 KB.

## 2. The story ending (0x4dd9c0..0x4e11e0) **[exact]**, not in the remake

The last step of the ROO and OMD main quest lines in `World/Data/QuestClient.tdf` ends its dialogue with
`<ending ROO>` / `<ending OMD>`. The markup parser calls 0x6f6fe0 / 0x6f7020, which call the ending object's start
0x4e1030(team, ...):
- 0x4e0270: the closing lines, ROO 10670-10674 ("The race is not over yet." ... "The real race has just started!"),
  OMD 10675-10679 ("I've just realized the truth" ... ), at y 20 / 24 / 28 x 4 (two columns 0x24e / 0x28f).
- 0x4df290: `Game\credits\Credits_Main.dat`: line 2 `fade <seconds>` (0.5 when missing), then rows
  `start duration y font text` (y from the bottom: screen height - 768 + y); credits_main.dat holds
  `1 3 578 2 DriftCity Remastered`. Drawn by 0x4ddcb0 with a fade in and out over the fade time.
- 0x4df7e0: `Game\credits\Credits.dat`: sections `[SCROLL]`, `[SCROLL0]`, `[SCROLL1]`; rows `text <tab> font <tab>
  image`, "**********" spacers, staff photos `Game\credits\dds\NNN_xxx.dds` (20 of them) shown as NiScreenElements;
  drawn scrolling by 0x4ddf50 / 0x4de1f0.
- 0x4de690: `Game/credits/fade.nif`, `omd_and.nif` or `roo_and.nif` (the team's closing picture).
- 0x4dd4d0: the fonts (Tahoma, Calibri, MingLiU, Ms pGothic by language).
- Music 0xea79; the UI is hidden; the camera orbits the car 8 m out and 3 m up at 0.375 rad/s (0x4ddbd0) or cuts
  every 5 s between the `watch_end1..4` / `watch_end_rotate` camera tracks (0x4ded10).
- Packet 0x5dd (handler 0x4f7210 -> 0x4e0ef0 -> 0x4e0430) fills the "<Driver Information>" card 10680-10705:
  character name, creation date, user id, crew name / rank, total play / driving time (10703 "%d Hour(s) %d Min."),
  distance, current car, cars driven / owned, level, EXP, Mito (10705 "%I64d Mito"), Battle Zone wins/losses
  (individual, team), successful chases, deliveries, drifts, jumps, reckless driving time.

Phases (the ending's clock 0x4e0160 at `+0x70`, time `+0x6c`): 0 fade.nif (2.167 s; at 0.99 s the driver-information
request 0x799d80 goes out), 1 the closing lines (ends after 32 s), 2 the driver card (header, then one label / value
pair every 3 s from 7 s, 3 s each, labels at y 590, values at y 655), 3 Credits_Main.dat, 4 the Credits.dat roll
(0x4def80: rows start one screen down, y = 768 + offset, "**********" adds 45 px, every row 45 px, a row with a photo
128 px more and the photo 195 px above its text, 320 px wide; `[SCROLL0]` / `[SCROLL1]` = left / right columns, the
right one restarting at the left one's offset; the roll moves one pixel a frame and the phase ends when the last row is
40 px above the top), 5 nothing, 6 roo_and.nif / omd_and.nif (6.667 s), 7 (0x4deb70) clean up, UI back on, the city's
music (0xea6b/0xea6e/0xea71/0xea74/0xea7a by city) unless the player's own MP3s play. Timed lines fade in and out over
Credits_Main's `fade` (1.0 s) and are drawn four times one pixel off for the outline, then the text.

Remake: `Runtime/Game/EndingCredits.cs` (new): `EndingCredits.Play(roo, done, target)` runs the phases with the files
above, the team lines, a driver card filled from the PlayerProfile offline (fields the remake does not track show 0),
bgm/DriftCity_ending.mp3 and the orbit camera; IMGUI in the 1024 x 768 layout. (inferred) 60 px/s for the roll, the
fade.nif / team NIFs shown as fades with their .dds pictures. Tests `EndingCreditsTests`. Hook for the step quests
(`StepQuestRunner.cs`, being converted by another agent): on the markup tag `<ending ROO>` / `<ending OMD>` call
`EndingCredits.Play(tag == "ROO", next)`.

## 3. Mislabels fixed

Game code inside the "ODE" address band (the range rule called 0x7f0000..0x82ffff "LIB ODE physics"):
- 0x7e8190..0x7f3b40 (from Data tables and ODE): the **passerby system** (PCS reader 0x7e93a0, placement 0x7e9cf0,
  timetable 0x7ea2f0, looks 0x7eb0d0, the colour lookups 0x7eab00 / 0x7eac60, the PCS object 0x7ea7e0 / 0x7ec280)
  -> Traffic & HUV AI.
- 0x7f3b80..0x7f6f4f: OPCODE (RayCollider etc., called from ODE 0x81xxxx) -> LIB OPCODE collision.
- 0x7f6f80..0x80e21f: the **TCS road / traffic network** (road lookup 0x7fb4d0, `.tcs` reader 0x803e40 / 0x803640,
  segment records 0x80d440, "TcSStorage::GetVehicleInfo", chunk tags "TCS" / "NAC") -> Traffic & HUV AI. ODE itself
  starts at 0x80e220 (collision_kernel / quadtree asserts).
- 0x82a308..0x82b4cf: operator new / delete, CRT start-up and the unicows import stubs -> LIB C runtime.

Traffic & HUV AI (largest functions checked):
- skid marks / tyre smoke / surface splash / graze effects (EFFECTS.md: 0x6b5c90..0x6b6570, 0x6b7360..0x6b8d50,
  0x6ba160..0x6ba810, 0x69c840..0x69c900, 0x6a11f0, 0x490820, 0x6ae240..0x6aeab0) -> Effects;
- the PvP race object (0x71fc40.., 0x7238a0.., 0x729150.., 0x72cd26..0x72e820, 0x72edf0.., 0x6c0330..0x6c06e0;
  0x72d250 stays Traffic: it computes the PvP track traffic, TRAFFIC.md section 13), the PvP camera director
  0x4988b0 -> Camera, the path loader 0x49c410 -> PvP;
- Undercity HUV waves 0x717510, 0x71a170, 0x71a9b0.. -> Undercity; the instant-mission kind names 0x6c5e80
  ("Arrest_HUV", "Battle_*") -> Missions; QUEST 1780000 rising lines 0x560ac0, 0x5612f0..0x561350 -> Progress;
  std::map code 0x4b8b90 .. 0x4b9920 (CHASES.md section 8) -> LIB C runtime.
- Kept: 0x473c00 (the NPC AI debug line "MIND / ACTION / ATTACK / MOVE"), 0x4f0410 / 0x4f0940 / 0x4f0e90 (HUV
  spawns "MyHuv / OHuv"), 0x4c25e0..0x4c3140 (vehicle actor lists), 0x739e40..0x73ad90 (`World/Data/pvptrafficnum.dat`),
  0x460840 / 0x461940 / 0x464120 (car model / siren / shadow loaders; better "Vehicle visuals", left for the
  vehicle visuals review), 0x699320 (boss HUV explosion effects).

Data tables:
- 0x4d2470 "World create" (the game start-up), 0x439b40 (vehicle textures / game UI loading) -> Stages & flow;
  0x4d4370 (shutdown: Audio Terminate, SaveConfigFile ...) and 0x7e7cb1 (dbghelp symbol type names) -> Platform.

## 4. Effect on the map

With the overrides (classify.py run in a private copy): Unlabelled 108.6 -> 0.2 KB; Traffic & HUV AI 273 -> 397 KB
(+110 KB road network / +33 KB passerby code that was counted as replaced library code or tables, -28 KB effects,
-12 KB PvP); Data tables 140 -> 98 KB; LIB ODE physics 257 -> 132 KB. Re-run `classify.py`, `scorecard.py` and
`datasets.py` to publish.
