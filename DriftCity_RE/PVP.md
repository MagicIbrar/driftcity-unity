# PvP ("Battle Zone"): lobby, rooms, wait room, race, results, tournament, ladder

Recovered from `DriftCity.exe` (image base 0x400000). Decompiled sources: `decompiled/phys/0xADDR.c` (made with
`dec.py`; functions that start with a static-init guard `test byte ptr [guard],1` do not decompile through the bridge and
were read as disassembly). **[exact]** = read from code or data, **[inferred]** = interpretation. Text ids are
`localize\globaltext.xlt` keys (US column quoted; KR quoted when US is empty); config keys are `localize\globalcontent.xlt`.
Control ids are dialog id + index (`allctl.tsv`); "visible" = control flag `+0x3c` bit 2, "enabled" = bit 4, bit 8 =
highlight/blink frame **[exact from usage]**.

## 1. Objects, stages and packets

- **PvP UI object** (game `+0x160` -> `+0x298`, ctor 0x57e160, vtable 0x86e908). Its binder 0x57f200 registers the
  dialog handlers **[exact]**: 990000 LOBBY -> 0x57e480, 1000000 RMLOBBY -> 0x57e750, 1020000 CREATEROOM -> 0x57c810,
  1100000 WAIT16 -> 0x57ecd0 (falls through to 0x5790a0 -> 0x592540 map/traffic, then 0x57d9f0 slots/buttons),
  1140000 RMCAR -> 0x575840, 1450000 EXIT -> 0x5758c0, 4550000 RETRY -> 0x574a90. It also caches 1990000 (observer
  popup), 1110000 instant mission-battle map buttons, and the RMLOBBY "CS" (Championship) buttons.
- **Room/race manager** (game `+0x170`, vtable 0x87b7b8) **[exact]**: `+0x5c` room info (flags at `+0x10`, player count
  `+0x1c`), `+0x64` slot array (32 dwords), `+0x78(id)` is-host, `+0x13c` am-I-ready, `+0xc4` 0x7234c0 start request,
  `+0xc8` 0x72cfd0 exit race, `+0xe0` 0x726360 race start, `+0xf0` 0x7236a0 race end time, `+0x124` 0x723fe0 player
  finished, `+0x128` 0x729cc0 race result, `+0xfc/+0x100` get/set race state.
- **Stages** **[exact]**: 9 parking lot (see PARKINGLOT.md); **11 and 12 = WaitRoom** (enter 0x76f250 accepts both, logs
  "WaitRoom Stage Enter"; BGM sound 0xea76; channel types 7..10 open the tournament wait room 0x56e200/0x572990,
  others the PvP wait room 0x579950/0x57be30); 13 = world loading; **15 = "PvpPlay"** (0x7658b0, loads resources
  0x136/0x137/0x140); **17 = PvP race** (enter 0x75f3d0, update 0x75fc10). Joining/starting a running room goes
  0x50ad70 -> world name + next stage 17 (0x753ec0) -> stage 13 (0x50b0ac..0x50b0be).
- **Server packets** (handler table, `scratchpad\handlers.txt`) **[exact]**:

| opcode | handler | meaning |
|---|---|---|
| 0x1bf | 0x50b140 | room info / join reply. `+2` result: 1 ok, 2 -> 10030 "You cannot enter the room anymore.", 3 or no players -> 10031 "This room no longer exists.", else 10032. Body: `+6` room id, `+0xe` track id, `+0x12` room flags, `+0x16` room state (0 waiting, 1/2 racing, 4 = ?), `+0x24` title, `+0x122` 32-dword slot array, `+0x1a2` player count, `+0x1a6` players (0x3d bytes each). State 1/2 -> load race (0x50ad70); state 0 while a result timer runs -> next stage = 11 (0x7566c0(0xb)) |
| 0x1ea | 0x509fe0 | "Pvp Start": `+2` server ms start time -> manager `+0xe0(startSec, +6)` |
| 0x1eb | 0x508080 | a player finished (id, time) -> `+0x124` |
| 0x1ef | 0x5080e0 | race end time (server ms) -> `+0xf0`: state 9 + "pvp_tencount" effect |
| 0x1ec | 0x50a040 | race result: `+2` type 1 = normal (`+0x128`), 2 = tournament (0x75e1a0); `+0x16` entry count, entries 0x34 bytes from `+0x4e` |
| 0x1f3 | 0x509810 | wager result: `+2` bit0 team; `+0xa` count; entries 0x42 bytes {mito delta, new mito (i64), player id, ..., `+0x12` rank} |

Server time: `seconds = (server_ms - net[+0x118]) * 0.001` (net = `[0x8efff8]`, getter 0x780510/0x512ad0) **[exact]**.

## 2. Which lobby, and the arenas

- 0x57ce50 (open PvP) shows **RMLOBBY 1000000 when net flag `+0x270 & 0x200` is set, else LOBBY 990000** **[exact]**
  (0x57d029). The flag comes from the server **[inferred]**. Both have 10 room rows A..J, PREV/NEXT, CREATEROOM,
  FINDFRIENDS (1080000), LAPTIMEBATTLE (enabled only if net `+0x270 & 4`), CLOSE. 990000 has 6 arena tabs
  (no RM); 1000000 adds CHN_EXP_RM, TUTORIAL, CHARGE (charger 960000 or 950000/940000), MISSION_BTN, CS_ENTER/CS_INFO
  (GT Championship entry/info) and F1_EVENT.
- **Arena tab index** (`ui+0x1400`) **[exact]** (0x57e750): 0 = CHN_EXP_2X "Amateur Arena" (11090), 1 = 3X "Rookie Arena"
  (11091), 2 = 4X "Pro Arena" (11092), 3 = 6X "Star Arena" (11093), 4 = 8X "Superstar Arena" (11094), 5 = FREE "Free
  Arena" (10307), 6 = RM "RealMatch Arena" (10823). The tab names are the EXP multipliers (2x/3x/4x/6x/8x; generic text
  10306 "%d times Exp. Arena") - the multiplier itself is applied by the server **[inferred]**. Clicking a tab sends the
  channel/list request 0x518a50(tab, page `+0x140c`, 0) -> 0x784ec0. TABKEY_* (Tab key) cycles to the next *enabled* tab.
- **Tab enable rules** 0x575970 **[exact]**: car grade = current car `+0xc`, level = character `+0x38`. Tab N enabled iff
  `MinBattleNXGrade <= grade <= MaxBattleNXGrade` and `level <= MaxBattleNXLevel` (globalcontent), AND the server arena
  bit in net `+0x270`: 0x10 = 2X, 0x20 = 3X, 0x40 = 4X, 0x80 = 6X, 0x100 = 8X, 0x08 = FREE (Free: `MinBattleFreeGrade..
  MaxBattleFreeGrade`, `level <= MaxBattleFreeLevel`). Net flag 0x4000 opens all five EXP tabs regardless of grade/level.
  US column values:

| arena | grade min..max | max level | | arena | grade | level |
|---|---|---|---|---|---|---|
| 2X Amateur | 1..3 | 20 | | 8X Superstar | 8..9 | 1000 |
| 3X Rookie | 3..5 | 40 | | Free | 1..10 | 1..1000 |
| 4X Pro | 4..6 | 70 | | Real Match | 1..10 | 1..1000 (EU: min level 30) |
| 6X Star | 6..8 | 1000 | | | | |

  Other keys: `BattleRoomAI` (US 1) allows AI drivers in rooms; `Tournament` (1) enables GT Championship;
  `PvpGT_TouringA` 387 / `PvpGT_TouringB` 341 (US km/h top-speed caps); `PVPReward8X2` (US 1) reward grid layout.

## 3. Lobby room list and its buttons

- Room list reply -> 0x57cc30(total, page, list): PREV enabled iff page > 0, NEXT iff page < total; an empty page > 0
  re-requests page 0; rows filled by 0x57a880 (990000) / **0x57abb0 (1000000)** **[exact]**. NEXT = `(page+1) % 200`.
- **Room list entry** (0x90 bytes): `+0` room id, `+4` track id, `+0x10` flags, `+0x14` state (0 waiting), `+0x18` max
  players, `+0x1c` players, `+0x24` title **[exact]**.
- **Room flags** (create 0x57a3a0, row fill 0x57abb0) **[exact]**: 0x1 team mode, 0x4 password, 0x80 spectating allowed,
  0x100 Real-Match room (channel type 6/7), 0x200 special (row text drawn in colour 0xff0000c8; START/ready/kick
  rules relaxed - GM/event room **[inferred]**), 0x400 no gate, 0x7000 wager grade `(flags>>12)`: 1 S = 10000,
  2 A = 5000, 3 B = 1000, 4 C = 500, 5 D = 100 Mito (0x507330), 0x10000 mission-battle room (cannot be joined from the
  list; WAIT16 shows MISSIONBATTLE_TEXT 11479).
- **Row controls** (index r = A..J): SINGLEMODE_r or TEAMMODE_r background (by flag 1; enabled only while state 0 and
  not 0x10000), TITLE "Room name: %s" (10242), MAP "Map: %s" (10243, track name), PLAYERCNT "%2d/%2d" (players/max),
  SECRETENABLE_r (single) / SECRETENABLE_GROUP (team) when password and waiting, SECRETDISABLE_r when password and
  racing, NOGATE_r for 0x400, GRADE_S/A/B/C/D for the wager grade, OBSERVE_r (+ OBSERVE_GROUP for team) unless
  0x10000 or 0 players.
- **Join** (click TEAMMODE/SINGLEMODE/TITLE row; 0x579e00 / 0x579ff0) **[exact]**: blocked during tutorial (11285 "You
  cannot access during Tutorial"); if the row shows a password icon -> PASSWORD 1650000 (password typed 1..8 chars);
  if wager room and Mito < **wager x 10** -> 11067 "Not enough Mito. In order to participate, you must have %d Mito.";
  else join request 0x785e30(my id, room id, ..., car).
- **Spectate** (OBSERVE_r, 0x57a250): password rooms ask for the password (unless room flag byte sign bit clear and the
  character's authority byte `+0x7c` > 'M' - GM bypass **[inferred]**), else observe request 0x79ca10.
- **Quick join**: FILTER_SINGLE_CHK / FILTER_TEAM_CHK send 0x786200(my id, mode 1 single / 2 team, car, room -1)
  (0x574e90, 0x575060/0x575110) - they are quick-join buttons, not list filters **[exact]**.
- CREATEROOM -> 0x576c00 -> 0x576620 (blocked in tutorial). CLOSE -> leave PvP (0x57c540). FINDFRIENDS -> 1080000.

## 4. Create room (1020000)

0x576620 (open) / 0x57c810 (handler) / 0x57a3a0 (OK) **[exact]**:
- Defaults: mode single (`+0x640`=1, practice `+0x644`=0), max players **16** (CHK16 checked, CHK8 unchecked;
  TEXT8 "8" 10233, TEXT16 "16" 10234), no-gate off, **spectating allowed** checked (and disabled while there is no
  password), no password, no wager (`+0x1410` = -1). Title edit max 24 chars, prefilled with a random one of 10223..10232:
  "Drift King!", "Full Speed!", "Illegal Streetracing!", "Traffic Jam!", "No Braking!", "Snake City!", "Go Go Go!",
  "Untouchable!", "Into the Storm", "Get Away!". Password edit max 8.
- SELECTSINGLE / SELECTTEAM set team mode; SELECTPRACTICE (enabled only if net `+0x270 & 0x2000`) = single + practice,
  forces no-gate off and max players 1. CHK8/CHK16 are exclusive. Typing a password unchecks+disables ALLOWOBSERVE;
  clearing it re-enables it.
- GRADE_A..D (wager 5000/1000/500/100) need net `+0x270 & 0x20000`, else 10745 "This feature is currently
  unavailable."; clicking the selected grade again clears it.
- OK (or Enter in title/password): title through the slang filter -> 10608 "That Room name is not allowed..."; password
  must be 1..8 chars -> 10503 (KR "re-enter the password"); wager needs Mito >= grade x 10 -> 11066 "If you want to
  select this wager level, you must have %d Mito.". Flags as in section 3 (grade bits = `(index+1+1) << 12`, so A..D =
  2..5); a second word = 0x800|4 normally, 0x20|4 for RM/practice in the RM lobby, practice clears 0x800. Request
  0x7851c0(title, password, mode word, flags, max players, wager).

## 5. Wait room (1100000 WAIT16, 1990000 observer, 1130000 emoticons, 1140000 RM car, 1160000 SELMAP)

- Init 0x578070(maxPlayers, isHost, singleMode) **[exact]**: observer dialog 1990000 text 10235 (KR "waiting to spectate")
  or 10870 "You can only spectate in this room."; MAP_RECOMMEND "Recommend" (10236), MAP_RANDOM "Random" (10237),
  MAP_REVERSE "Reverse" (10238). Single mode shows SINGLE and hides TEAM, SELECT_A/SELECT_B disabled; team mode the
  opposite. MAP_RECOMMEND enabled only for the host in a non-RM, non-practice room; MAP_RANDOM for a non-practice host.
- Refresh 0x57b060 **[exact]**: isHost = manager `+0x78(my id)`; ready = `+0x13c`. **canStart** (`ui+0x13a6`) = (real
  players, id < 0x10000, > 1) AND every listed player's ready byte (`player+0x68`) set. The host's entry is checked
  too, so the server must keep the host flagged ready **[inferred]**; the host's slot shows SLOTxx_HOST/START. START (host) and READY (guest) are the same position: host -> START visible,
  READY hidden; guest -> the reverse (0x576c60). QUIT is disabled for a guest who is ready. BEGINNERCOVER shows in
  the Amateur (tab 0) arena. SECRET icon = flag 4, NOGATE icon = flag 0x400. Player label " Lv.%02d %s"; crew "%s
  Crew" (10222). Player entry 0xc4 bytes: `+0` id, `+6` name, `+0x46` level, `+0x47` team/marks, `+0x68` ready.
- **Slots**: 16 visual slots (SLOTSINGLE00..15 / SLOTTEAM00..15, BASE, LOCK_BTN, AI_BTN, AI_CLOSED, CLOSED, SLOTxx_ID/
  PT, HOST, ME, READY, START, CLOSE(kick), INPRATICE, LICENSEPOS, TEAMMARKPOS). Slot array values **[exact]**: 0 = open,
  -1 (0xffffffff) = closed, 0xffff = AI requested, id >= 0x10000 = AI driver, else player id. **Team = slot parity**:
  even slot -> team A (SELECT_A, blue), odd -> team B (SELECT_B, red) **[exact parity, colours inferred]**.
- Slot clicks (0x57d9f0) **[exact]**: SLOTSINGLE/SLOTTEAM -> player info (0x579250, "%s (Lv %d)"); SLOTxx_CLOSE ->
  kick confirm 10240 (KR "%s ...?") -> kick; BASE/LOCK_BTN on an open slot: host -> close it (-1), guest -> move to it
  (0x785740); AI_BTN (only drawn when `BattleRoomAI` != 0 **and arena tab <= 4** and slot open and host): host -> 0xffff
  (add AI); AI_CLOSED (host) -> remove AI (0x786750); CLOSED (not practice, room-info flag 0x1000 clear) -> reopen (0).
  Every slot change is sent as a settings update 0x7853f0(track, flags, slot array). SELECT_A/SELECT_B -> team change
  0x7855b0 (guests only while not ready). INVITE (cooldown) 0x79c7c0; QUIT -> leave room; DETAILVIEW -> car detail.
- **START** (0x579560) **[exact]**: 1.0 s debounce; wager room needs >= 2 players (11107 "You must have at least 2
  players to start the Wager Battle.") and Mito >= wager x 10 (11067); RM room outside RM time -> 10850 "Real Match Time
  has Ended."; then manager `+0xc4` = 0x7234c0: if the chosen track's AI column is 0, all AI drivers are removed and
  11344 "The map does not support AI Battle..." is posted; send start 0x787690(track, 0, clientTime). The client
  itself does not check canStart; START/READY just blink (0x57a6c0).
- **READY** (0x579740): same wager/RM checks, then toggle ready 0x787510.
- **Host kick timer** (0x57a6c0) **[exact]**: while the host can start, START blinks (on for 0.25 s of every 1 s) and a
  1 s counter runs; at counts 11..15 the system line 10241 is shown with `16 - count` seconds (KR/EU "If the race
  doesn't start, you'll be kicked in %d second(s)."). Not in rooms with flag 0x200. The kick itself is server side
  (~16 s after everyone is ready) **[inferred]**. A guest who is not ready sees READY blinking the same way.
- **Map selection** (0x592540 + helpers; edits only if host or `+200` override, 0x5901d0) **[exact]**:
  map-settings word (wait-room map object `+0xc0 -> +8`): 0x1 random map, 0x2 reverse, 0x4/0x8/0x10 traffic IDLE/
  NORMAL/JAM (none = RANDOM), 0x20 CITY_ALL, 0x40/0x80/0x100/0x200 city 1..4, 0x400 city 5, 0x800 recommended.
  City tabs are visible/usable only when the server city bit is set in net `+600` (bit0..4 = Moon Palace, Koinonia,
  Cras, Oros, Taipei); a city with no tracks -> 10204 "No maps available." and the selection reverts. MAP_LEFT/RIGHT/
  SLIDER browse the filtered list (0x5912a0): rows with city 1..5, CityType != 4 (no lap-time tracks), city enabled,
  and `Reverse` == the REVERSE checkbox; forward/reverse rows are paired by name minus "_r" (10568). RECOMMEND clears
  random/reverse, selects CITY_ALL and restricts to recommended tracks; RANDOM/REVERSE clear RECOMMEND. SELMAP 1160000
  shows the track picture (`PVP_SELMAP_<city>_TRACKnn`, random pictures per city / reverse), track name, AI mark
  (AIMARK / AIMARK_NO from the AI column), NEWMARK (New column) and BONUS100/BONUS200 (source not traced).

## 6. Race start

Timeline relative to the server start time T (0x75fc10, 0x756d60, 0x760d09) **[exact]**:
1. Room state -> racing: world load (stage 13) -> stage 17. 0x725560 (vtable entry, "pvpready"): each player is put on
   its grid slot (`+0xf8` 32 ids -> 0x725fd0(slot)), AI cars are spawned for empty slots when AI is on
   (`id = slot - 0x3cb0`), team colour = `(flag & 1) + 1` in team mode, camera **"pvpready"** (camera.ini camera23:
   watch/program, fov 60).
2. now < T - 19.5: "pvpready" grid fly-by (0x75ae60/0x764f90): the camera dollies along the grid between slots 14/15
   and 6/7 at 3 units/s, then reverses.
3. Packet 0x1ea: manager `+0xe0` (0x726360) stores T, camera **"pvpstart"** (camera9: effect/animation, fov 45), state 6,
   shows the rank HUD. T - 19.5 <= now < T - 6: camera "pvpstart" is (re)selected.
4. **T - 6 s**: countdown begins: state `+0x71c` 1 -> 2, UI effect 18 `effects\ui\pvp_countdownstart.nif`, and the
   overlay `effects\ui\pvp_countdown_base.nif` (3 child nodes) with the camera animations
   `pvp_countdown_cam00..02.nif` (ortho 51.2 x 38.4 camera, 0x6bec00) drawn while T - 6 <= now < T + 1 (option flag
   0x8d2981). So the countdown is **6 seconds** long and the overlay stays 1 s after GO.
5. T: control released (state change by the manager), normal driving camera.

## 7. In race

- HUD dialogs (UI manager slots) **[exact]**: 1380000 rank list (`+0x220`, 0x583a20; RANK_MY1ST/2ND digits, ID_ON/OFF
  1..8, CHKSPEC9..16, ALL1ST), 1400000 race time (`+0x228`), 1390000 laps (`+0x224`; TOGO/TOTAL digits), 1410000 best
  lap (`+0x22c`), 1420000 (`+0x230`), 1430000 team gauge RED/BLUE (`+0x234`), 1440000 rear info YOURTEAM/MYTEAM (`+0x238`),
  1450000 EXIT, 1640000 goal-in banner (`+0x270`; GOALIN_1/ST/ID = rank + name), 1170000 / 1180000 minimap (normal /
  "ADV"; icons MINE, GHOST, PLAYER1/2, MINE_A/B team).
- **Checkpoints / laps** (0x72f3c0, every frame) **[exact]**: gates are the pvptrackinfo rows; a gate counts when the car
  is within 150 m (`d^2 < 22500`) of it and crosses its line (sign change of the dot product), height band permitting.
  Progress = gates passed (`car+0x1bcc`); lap = progress / gatesPerLap. Gates with `reward` = 1 (or every gate when net
  `+0x270 & 0x20000000`) are reported to the server (0x787ce0: gate, time). On a lap change within 30 m of the line:
  laps 2..total-1 play `pvp_lap2..7` (effect 20 + lap, sound 0x4ece), the last lap plays `pvp_finallap` (19, 0x4ecf);
  the laps HUD updates.
- **Wrong way** (0x72f3c0 tail) **[exact]**: compares the car's horizontal velocity with the course direction; if the
  cosine stays >= 0.5 against the course for > 5 s, effect 31 `pvp_wrongway` + sound 0x4ed0. Debug log "pvp_course %d".
- **Finish**: 0x1eb -> 0x723fe0: own finish plays 30 `pvp_winner` (1st) or 20 `pvp_finish`, rank voice SID_PVP_VO_1ST..3RD,
  14 `qs_newrecord` on a record (EFFECTS.md). Other finishers show the 1640000 banner and the "pvpgoalin" camera
  (camera21) after a short delay (0x6bf490: up to 3 finishers, 0.6 s each).
- **Time limit after the first finisher**: server-driven. Packet 0x1ef gives the end time; 0x7236a0 sets state 9 and
  plays effect 29 `pvp_tencount` started at (end - now) + sound 0x4ed3 (not in mission-battle rooms). The effect is a
  10 s count, so the server uses **first finish + 10 s** **[inferred]**.
- **Result / retire**: 0x1ec type 1 -> 0x729cc0: state 10, EXP/level-up (10056 "%d Mito received."), stops the ten count,
  plays 28 `pvp_retire` (sound 0x4ed5) if I did not finish, else 27 `pvp_raceover` (0x4ed4); state 12.
- **EXIT** (1450000, 0x5758c0): leaves the race (`+0xc8`) except in tournament channels (net `+0x3b4f0` 7..10).
- Spectators (`stage+0x818`) can cycle the watched car with keys 0x79/0x7e (prev/next) and 0x7b/0x7c (0x75fc10).

## 8. Results and return to the wait room

- The result screens are driven by a **13.0 s timer** (`stage+0x6f8`, set by 0x75e1a0 when the result list arrives -
  directly for tournament results (0x1ec type 2), through the callback 0x50e600 (table 0x85fa28) otherwise **[inferred]**;
  winner `+0x708` = team A pts > B pts ? 0 : B > A ? 1 : draw) **[exact]**:
  - at 10 s left (3 s after the results): single race -> **1470000 RESULT16** (`+0x20c`); team -> **1540000 OURRESULT16**
    (`+0x240`) + **1630000 TEAMPOINT** (`+0x23c`, team totals); tournament channel -> tournament result (`+0x214`);
    spectator -> observer result (0x673eb0).
  - at 5 s left: team -> hide 1540000, show **1550000 team RESULT16** (WIN / LOSE / PERFECT, outlines A1..B3); tournament
    -> next tournament page (0x58e480(channel - 7)); single keeps 1470000.
  - at 0: close all result dialogs and change to the stored next stage (`+0x6fc`, default 17). The room-info packet
    0x1bf with state 0 sets it to **11 = wait room** (0x50b73e), i.e. **everyone returns to the wait room 13 s after
    the results** **[exact]**.
- **1470000 RESULT16 single** (0x588100) **[exact]**: one row per rank (`entry[0x1b]` = rank): name ("Lv.%d" + ID, own
  row uses *MINE controls), time "%2d:%02d.%03d" for finishers (`entry+0x5c` == 0x7fffffff marks finished), otherwise
  RETIRE icon + "DNF - %4dm" (10215) / "DNF - %4.1fkm" (10216) from the distance; own row EXP and Mito ("%d", 10214),
  LVLUP icon, bonus icons from `entry[0x23]` (1/2 PC-room EXP/Mito, 8 golden-time effect 43, 0x10/0x20 EXP-bonus event,
  0x200/0x400 tree event, 0x800 3rd anniversary; santa by event id). LADDERRANK change arrow (up/down/same) when the
  ladder is active (0x512ec0) and the entry has a ladder value (`[0x2c]` delta), except Free and RM rooms.
  1460000 RESULT_EFFECT = rank 1..8 upper/lower/mark effects (`+0x200`).
- **Wager results** (0x1f3): single 4080000 RESULT_SINGLEBB (0x588f10; grade S..D, Mito per rank) / team 4090000
  RESULT_TEAMBB (0x58bce0); chat lines 11083 "%s(%dlevel): %d Place,%d Mito Issued", own 11084 "%d Mito Issued." /
  11085 "No Changes in Mito" / 11086 "%d Mito Lost"; the new Mito total is written to the character **[exact]**.
- **4310000 PVP_REWARD** (`+0x418`, 0x58bf70..0x58d9b0) is the GT Championship reward pick: 16 slots (SLOT/SEL/NAME
  01..16; 8x2 layout when `PVPReward8X2`), class tables `rw_gtc_rookie / touringb / touringa / gtmaster`, 11799 "If you
  don't choose a reward item within a given time, it will be automatically selected."; 4910000 = my rank/team/name.

## 9. Ladder and rankings (short)

- LADDER 3930000 (`+0x3e4`, 0x68be60 search, 0x68e2f0/0x68e990 list): server ranking per car class; request
  0x518b00(class "X%c", start, **10 rows**, name filter) - class letter from the current car (0x68b280), "X8" when
  none; PAGE0..9, LEFT/RIGHT, SEARCH edit, GRADE combo, rows labelled "C%d" **[exact]**. LADDERRE 3750000 (`+0x3b8`) is
  a post-race list box with LEFT/RIGHT. Ladder points are computed by the server; the client only shows the delta.
- QSRANKLIST 2830000 (`+0x158`, 0x558800): time-attack/quest weekly ranking (MYRANK, MYBESTRECORD, PREVWEEK, PERIOD),
  not PvP specific.

## 10. GT Championship (tournament, short)

Text-backed structure **[exact text]**, flow **[inferred]**: tournament channels are net channel types 7..10 = classes
Rookie / Touring B / Touring A / GT Master (`Championship_*`; index = channel - 7, used by 0x58e480 and the wait-room
switch). Stages: Preliminaries (11492, "Time Attack" 11538: rank by total matches and best lap 11543/11544; join until
3 min before the end) -> Quarter-finals 1 and 2 (11493, "Normal Battle Mode" 11540) -> Semi-finals (11494, "2 Round Point
System" 11541, round 1/2 scores) -> Finals (11495). Rules (Championship_infotext2..6): max 2 classes, no-shows from the
quarter-finals are disqualified, no items, a class runs only with >= 8 preliminary winners. Class limits: level
(11523), Real Match vehicle (11524), Untouchable (11525), speed cap (11527/11528, `PvpGT_TouringA/B`). Dialogs: TOUR
lobby 1010000 (CH_ROOKIE/TOUR_B/TOUR_A/MASTER, STEP_1/2/SEMI_FINAL/FINAL, 10 rows), 1050000 tooltips, TOUR wait room
1110000 (slots with points `SLOTnn_PT`). Exit is disabled in these channels.

## 11. Tables: `init\pvptrackinfo.xlt`

UTF-16 TSV, header rows "Preset/Count 1281/TrackCnt 107" **[exact]**. One **start row** per track followed by its
`end` row and gate rows. Columns:

| column | meaning |
|---|---|
| Index | row number |
| Support | 1 = usable (taipei_track2 = 0) |
| city / track | text names (start row only) |
| Id | track id (start row), 0..106; used by rooms and packets |
| type | 0 start line, 1 end (finish) line, 2 gate |
| New | NEW mark in SELMAP |
| AI | 1 = AI drivers supported (moonpalace track1-6, 9, 10 and _r, track13/_r) |
| TrackCnt | gate count (start row) |
| gate / num | gate name ("start", "end", "gate01".."gateNN") / gate number |
| reward | 1 = checkpoint reported to the server |
| pos_start x,y,z / pos_end x,y,z | gate line endpoints (world units) |
| minx, miny, maxx, maxy | minimap bounds (start row) |
| LapCount | laps (1, 2, 3; 5 for lap-time tracks) |
| Traffic Preset | traffic preset index |
| ReversePoint | reverse marker |
| CityType | 1 battle track, 3 tutorial, 4 lap-time track |
| city / tracknum | 1 Moon Palace, 2 Koinonia, 3 Cras, 4 Oros, 5 Taipei / number in city |
| Reverse | 1 = reverse variant ("_r") |
| Recommend | 6 flags "111111" - one per arena tab 0..5 **[inferred]** |
| MissionBattle | 8 flags - mission-battle map slots **[inferred]** |
| name | globaltext key of the track name ("moonpalace_track1" -> "Moon Palace Downtown") |
| Magic Number | 7777777 on every row (sanity marker) |

Track record in memory (0x10c bytes, game `+0x148`): `[0]` Id, `[0x22]` Reverse, `[0x25]` CityType, `[0x26]` city,
`+0x104` AI, `[0x37]` name **[exact]**.

## 12. Lap-time battle (short)

LAPTIMEBATTLE (lobby, net `+0x270 & 4`) -> lap-time channel ("Lap Time - %dClass Arena" 10720). Tracks are the CityType
4 rows (`<city>_Laptime_01`, 5 laps). HUD LAPTIMEPLAY 3440000 (`+0x384`): best time, player count, 1st player name/
time, own rank, a 6-row list; result LAPTIMERET 3460000 (`+0x38c`): top 4 with level/name/record and 3 reward icons each,
best lap, my lap, my rank. Tutorial lap rewards use `ApprenticeLapResultGood/Normal/NotBad` (130/135/140 s US).

## Unity port

Files (`DriftCityOffline/Assets/DriftCity/Runtime/Game/`): `PvpRoom.cs` (room model, slot array, rules and tables:
`Pvp`), `PvpLobby.cs` (RMLOBBY 1000000, CREATEROOM 1020000, PASSWORD 1650000), `PvpWaitRoom.cs` (WAIT16 1100000 +
SELMAP 1160000), `RaceMission.cs` (offline race: grid, fly-by, countdown, laps, finish, payout), `OnlineRace.cs` (server
race), `PvpHud.cs` (1380000 / 1390000 / 1400000 / 1410000 / 1640000), `PvpResults.cs` (1470000, 1540000 + 1630000,
1550000), `Ladder.cs` (3910000 + 3930000), `Tournament.cs` (TOUR 1010000). Server: `DriftCityServer/src/race.js`
(+ `server.js` messages, `db.js` ladder queries, `economy.js` arena EXP, `tools/bot.js --race`). Tests:
`Tests/PvpTests.cs` (PlayMode, offline and online), `DriftCityServer/test/server.test.js` ("battle zone rooms").
`NuiView.HonorHidden` += 1000000, 1020000, 1100000, 1380000; `NuiControlView.Selected` draws tabs / mode buttons in
their selected look; `NuiControlView.CheckMarkOnly` (1100000, 1020000) hides bare check marks while unchecked.

### Flow (as the client)

T key / station "Battle Zone" -> `RaceMission.Offer` -> `PvpLobby.Open` (RMLOBBY, the remake always has the RM-lobby
flag) -> arena tab (enable rule 0x575970 from globalcontent US values) -> room rows (10 per page, title 10242, map 10243,
"(%2d/%2d)", password / no-gate icons) -> join (password prompt) or CREATEROOM (random title 10223..10232, 24 / 8 char
limits, single / team / practice, 8 / 16, spectate off with a password, no gate) or quick join (FILTER_SINGLE / TEAM;
no room -> the create dialog with that mode) -> wait room: START for the host, READY for guests, QUIT locked for a ready
guest, team = slot parity, host slot buttons (LOCK_BTN close, CLOSED reopen, AI_BTN add AI, AI_CLOSED remove AI,
SLOTxx_CLOSE kick with 10240), map LEFT / RIGHT, RANDOM / REVERSE / RECOMMEND (RECOMMEND clears the other two and
selects all cities; REVERSE switches to the paired `_r` track), TRAFFIC buttons, SELMAP picture by the pvptrackinfo
name with AI / NEW marks, a track without AI support removes the AI drivers (11344) -> START -> race: grid by slot,
"pvpready" grid fly-by, countdown 6 s with `pvp_countdownstart` (+ SID_PVP_RACE_START), laps `pvp_lap2..7` /
`pvp_finallap`, wrong way after 5 s (`pvp_wrongway`), goal-in banner 1640000 for other finishers, own finish
`pvp_winner` / `pvp_finish` (+ `qs_newrecord`), `pvp_tencount` when the first driver finishes, `pvp_raceover` /
`pvp_retire` at the end -> results 3 s later (RESULT16 single; team OURRESULT16 + TEAMPOINT, WIN / LOSE / PERFECT at
8 s) -> back to the wait room at 13 s.
Lobby -> GT Championship (CS_ENTER) -> `Tournament.Open`; `Ladder.Open()` opens the ranking.

### Remake rules (the client leaves these to the server; all [remake])

| rule | value |
|---|---|
| offline rooms | made up per city and arena: 4-7 AI drivers' rooms (AI host + 1-5 AI, some team / no-gate, one mid-race); the player's own rooms start with 3 AI drivers. AI drivers are always ready; an AI host starts 3 s after the player is ready |
| AI drivers online | not offered in server rooms (no server-side AI); practice rooms are local in both modes and take AI drivers (the client's practice is one player) |
| AI support per track | the client allows AI only where pvptrackinfo AI = 1 (18 Moon Palace rows); our AI drives the track's `aipath.nac`, so every listed track (all have a line) takes AI drivers |
| maps | only the open city's battle tracks (CityType 1): races run inside the city, so its city tab is the only one enabled (the client's server city bits) |
| traffic | IDLE 0 cars, NORMAL 8, JAM 20, RANDOM one of them (cars kept during the race) |
| start (online) | wait-room rooms (`room` settings sent by `race.host`) need everyone ready and two drivers; legacy rooms (no settings) keep the old rule (two ready, others moved out) |
| idle host | removed 16 s after everyone is ready (the client warns at 11..15 s, text 10241) |
| countdown | server 6 s (`COUNTDOWN_MS`), offline 3 s fly-by + 6 s countdown (the client's pvpready / pvpstart cameras run from about T-20 s) |
| end of race | offline 10 s after the first finisher (pvp_tencount length); online the server's 30 s grace, the ten count plays for its last 10 s (`race.endTime`) |
| rewards | the race reward formula (economy.js `rewards.race`) times the arena's EXP factor = multiplier / 2 (Amateur 1x, Rookie 1.5x, Pro 2x, Star 3x, Superstar 4x, Free / Real Match 0.5x); team mode pays the winning team as 1st and the other as 3rd |
| team points | place p of n scores n - p + 1, DNF 0; more points wins (equal = draw, both paid as winners) |
| ladder | +10 for the winner down to -10 for the last (`round(10 (n + 1 - 2p) / (n - 1))`, DNF = last), start 1000, EXP arenas only (not Free / Real Match / practice), the 8X arena only with >= LadderCalcNumX8 (US 12) drivers. Kept in the profile (`LadderPoints`, `PvpRaces`, `PvpWins`), on the server for online drivers |
| ladder list | 3930000 columns: Ranking, Name, Crew ("-"), Battle Count (races and wins), License = ladder points. Online `race.ladder` (10 per page); offline the player and 20 AI rivals with fixed points |
| GT Championship | offline only (online: 10745). 16 entrants; preliminaries = a race whose best lap ranks against simulated AI laps (track length / 30 m/s x skill), the 8 best go on; quarter-finals 2 heats of 4 (player's raced, the other simulated), top 2 each; semi-final 2 rounds of the 4 (4/3/2/1 points), top 2; final 1-on-1. Reward by stage reached: EXP 200/500/1000/2000/4000, Mito 1000/3000/6000/12000/25000 x 1 / 1.5 / 2 / 3 by class; `TourBest` keeps the best. Class limits: Rookie level <= MaxBattle3XLevel, Touring B grade <= 6, Touring A grade <= 8, GT Master open |
| not converted (after round 3) | mission battle rooms (flag 0x10000: 14.6, the client has no objective data), the battle quickslot (3780000 / 3790000, inventory's), the TOUR_REWARD GM controls (GM only, 14.7), the PvP gate effects' rules (14.5: banners only), (the Real Match look of the player's own and remote cars, 14.4, converted in the Data tables round: RealMatchSkin). Round 3 (section 14) converted the ghost battle, replays, ROUNDNUMBER, the bonus icons and the Real Match looks of AI cars |

### Protocol additions (race.js; protocol 1 messages unchanged)

`race.host {track, room: {title, team, max 8|16|1, password, spectate, noGate, arena, mapFlags}}` (with `room` the
room persists and returns to the wait room after its race), `race.join {id, password}`, `race.quick {team, arena}`,
`race.settings {track?, mapFlags?}` (host), `race.slot {slot, op: close|open|move}`, `race.team {team}`,
`race.kick {id}`, `race.ladder {page}`, `race.list {arena?}`. Pushed: `race.room` (adds title, team, max, locked,
spectate, noGate, arena, mapFlags, persist, slots[16], racers[].slot/team/level), `race.start` (adds team, arena,
slots[].grid/team), `race.finished {id, name, place, time}`, `race.endTime {in}`, `race.kicked {reason}`,
`race.results` (adds level, slot, team, points, ladder, ladderPoints, teamPoints {a, b}, winner, arena). Test hooks:
`DC_RACE_COUNTDOWN_MS`, `DC_RACE_MIN_TRACK`, `DC_RACE_MAX_SPEED`.

## 13. Round 2: the rest of the Battle Zone

Recovered from the exe in this round (decompiled files in `decompiled/phys`, see 13.9), then converted. Corrections to the
sections above are marked **(fix)**.

### 13.1 The server feature word and corrections

- **net `+0x270` bit names** (flag deserializer 0x453c40, strings 0x85f27c..0x85f4b8) **[exact]**: 0x1 Tutorial, 0x2 Friend,
  0x4 LapTime, 0x8 ChannelFree, 0x10..0x100 Channel2X/3X/4X/6X/8X, **0x200 RealMatch** (the RMLOBBY flag), 0x400
  RealMatchTimeFree, 0x800 RealMatchGate, 0x1000 RealMatchSystem, 0x2000 PracticeMode, 0x4000 ObserveAllMode, 0x8000
  NoGateSupport, **0x10000 BattleQuickSlot**, **0x20000 BattingJackpot** (wager rooms), 0x40000 NewGateEffect, 0x1ff80000
  NewGateEffectFactor, 0x20000000 AllGateReport, 0x40000000 SafeCGameCheckIn, 0x80000000 ObserveGacha.
- **(fix) ALLOWOBSERVE** (0x57ca12..0x57cad3) **[exact]**: without a password the box is checked and disabled (a public room
  can always be spectated); typing a password enables it and leaves it checked. "A password disables spectating" in
  sections 4 / "Unity port" is wrong.
- **(fix) `stage+0x818`** is set by 0x75e320 from room flag **0x20** of 0x1bf: the Channel-Battle broadcast mode (1970000
  TC_VIEWING, 2170000 cheering); **0x673eb0 is the TC result** (2210000 + 2190000), not a generic observer result.
- **(fix) 4310000 PVP_REWARD is the post-race card pick of normal races**, not GT-only; the `rw_gtc_*` tables belong to
  **4510000 TOUR_REWARD** (same ui +0x418 object). **(fix) 0x58e480(ch - 7)** switches TOUR_REWARD's class tab.
- **(fix) `ch_ghost_%02d` / `ch_ghostpass`** are spectator ticket items (visualitem "ChannelGhost", text 10825 "Ch. Replay
  Ghost Pass") checked by 0x75e520 when the own id is 0xffff (spectating); the lap-time battle has no ghosts (other drivers
  are live cars). The Ghost Battle itself: 14.1.
- **(fix) PvP item races do not exist in this client**: 0x6af9e0 / 0x6a6d20 switch on the **city index**, not a stage mode.
  `bind` (dust devils every 5 s, at most 3, 150 + speed / 2 m ahead) and `tornado` / `TORNADO02` (every 15 s) are **Oros
  desert ambience** in free roam (stage 0xE), culled beyond 250 m. No room flag, item box or text exists for item races;
  the GT Championship rules say "no items". What exists is the **battle quickslot** (3780000 / 3790000, shown with feature
  0x10000, 0x580820; set up in 3800000): 2 slots with hotkeys, counts and a cooldown, using the normal item-use request
  0x78ce10. EFFECTS.md's "PvP item effects (bind, tornado)" line needs the same fix.

### 13.2 Wager rooms ("Batting")

- WAIT16 BATTINGTEXT 1100011: 11077 "%s Rank, %d Mito Entry Fee" (0x57bb60) **[exact]**.
- 0x724120 with room flags 0x7000: RESULT_SINGLEBB 4080000 (ui +0x3f4) or RESULT_TEAMBB 4090000 (+0x3f8) by flag 1;
  0x58aa20(grade - 1) shows GRADE_S..D and the MITO_S..D picture; team: 0x58ab20 swaps BLUEWIN / REDLOSE / VICTORY,
  0x58b520 draws the lists **[exact]**.
- **0x1f3 (0x509810)**: `+2` bit0 team, `+0xa` count, entries 0x42 bytes from `+0xe` {int Mito delta, i64 new Mito, id, u16
  level, u16 rank, bit0 team, name}; chat 11083 "%s(%dlevel): %d Place,%d Mito Issued"; own 11084 / 11085 / 11086 by the
  delta's sign; the new Mito is written to the character **[exact]**.
- 4080000 (0x588e10 / 0x588f10): titles 11078 "Rank", 11079 "Name", 11080 "Wager Winning Amount", 11081 "(Wager Tax:
  10%)"; row = rank - 1: BASEMINE, MYNAME / OTHERNAME, RANK / RANKRETIRE, MITOMARK, MITO "%d" (10214) of the **signed
  delta**. 4090000: BLUE_* / RED_* rows (8 each), 12 VICTORY marks, BLUE / RED WIN / LOSE.
- Payout: the client only shows the server's deltas. Hints: fee = the grade's amount, a driver needs fee x 10, 10% tax.

### 13.3 Real Match

- **0x1c8 (0x508c00)** {int seconds, type 0..5} **[exact]**: 0 countdown to the start (10840 / 10841, US empty), 1 start
  (10842, US empty; 50 min; manager `+0x85` = RM time), 2 in an RM room or the PvP UI: 10843 "Real Match will end in %d
  seconds." / 10844 "...%d minutes." (remaining = value - elapsed + 3000 s: a 50-minute session), 3 off + 10845 "Real Match
  Finished!", 4 / 5 manager `+0x8c` (a lobby control's enable). START / READY in a 0x100 room outside RM time -> 10850
  (flag 0x200 rooms bypass, 0x5796b7). RM tab limits: MinBattleRealMatchGrade 1..10, level US 1 (INI / TH / EU 30)..1000.
- RM car: 0x57be30 shows RMCAR 1140000 and hides DETAILVIEW; SHOWINFO toggles RMCARINFO 1150000 (0x575840): maker
  "Mitron Motors", logo sp07_02_a = vehiclelist row 62 "**Duel**" (grade 7, 149 / 155 / 84 / 149), set parts, options by
  part type (0x58ee80: 9 SPD, 10 ACC, 11 COL, 12 BST, 15 10852 "Crash Sustaining %2.0f%%", 18 10851 "Over Booster
  %2.1fsec"). One preset car for everyone **[inferred]**.

### 13.4 Lap-time battle (lap manager game+0x174, vtable 0x87b650, ctor 0x71bc50; state machine 0x71eae0)

- **0x528** (0x505b80): city `([+2] % 4) + 1`, 0x720740 loads the lap track (CityType 4, laps x gates); session `[+6]`;
  stage 15 via 13. **0x529** {server ms, charId} after 30 synced frames. **0x52a** (0x71dee0) {start ms, laps done,
  countdown flag}: the lap clock starts at start + 10000 ms; at T - 6 s state 5 with sound 0x4ecd and the countdown dialog
  0x12 for 7.5 s. Gates as in the race (150 units + crossing) -> **0x52b** {charId, ms, ms since gate, s}; a lap within 30
  units of the start -> **0x52c** {charId, ms, lap ms, lap s, lap no, max laps} and HUD 0x682ec0; after LapCount (5) laps
  the run ends (0x71fb50, "pvp end cheat detect" on a gate mismatch). **0x52f** another driver's countdown; **0x52d** {end
  ms}: timer dialog 0x1d + effect 0x13 (the round's end); **0x52c echo** (0x7209d0) {lap time, lap count, EXP, stats, level,
  i64 Mito}: best list (0x8ebbc4), ranks, own record -> "Exp +%d Mito +%d", MYBESTRECORD, LAPTIMENOTICE 3450000 for 5 s;
  **0x52e** (0x71c920) {count, 20-byte entries charId / item1..3}: the results, sound 0x4ed4, LAPTIMERET 1 s later for 7 s,
  then 0x71fdf0 resets the cars for the next round **[exact]**. EXIT 3440001 -> 10724 "Exit Lap Time?".
- LAPTIMEPLAY 3440000 (0x71c220): LAPCNT_CUR / TOT glyphs, CURTIME (the BESTTIME_NUM glyph set), PLAYERCNT, 1STPLAYER_NAME
  / TIME / MARK, RANKTEN1..3 / RANKONE0..9 + RANK_ST / ND / RD / TH or NORANK (sound 0x4f16 on a change), MYBESTRECORD, the
  best list window from max(0, myRank - 3) (rows 4-5 forced hidden by 0x681be0 / 0x681c30, so 4 show), MYREC1ST..5TH /
  MYBEST* "%2dLap %2d'%2d.%3d", MARKPOOR / BAD / GOOD / COOL / GREAT by the change from the previous lap (> 1 s, (0, 1], 0 or
  first, [-1, 0), < -1). Times "%2d'%2d.%3d", list "%02d'%02d.%03d" **[exact]**.
- LAPTIMENOTICE: UPPERRECORD + 10711 "(Ranking Unavailable)" / 10712 new 1st record / 10714 "%d Place No Ranking Change" /
  10713 "%d Place Ranking Increase". LAPTIMERET: rows 1-3 = the top three, **row 4 = me**, 3 reward icons each (380000 item
  icons), MYRANK digits.

### 13.5 Reward pick 4310000 and TOUR_REWARD 4510000 (ui +0x418, vtable 0x86ed7c, binder 0x58cac0)

- Race end 0x72ea60: net `+0x274 & 2`, arena tab < 5, not spectating, not channels 7..10 -> state 11; 0x58c450(1): the cursor
  on a random one of slots {5, 6, 9, 10}, the 4910000 panel hidden, OK 4310073 disabled. Lights blue < 2.5 s, yellow < 5 s,
  red after. DESC 11799. Keys 4 x 4 (8 x 2 with `PVPReward8X2`), Enter / Space. A click (0x58ca80) works once: **0x1e3**
  {slot}. **0x1e4** {count; 24-byte entries slot, picker, type, item, amount} -> 0x58e1b0: NAME = picker, ITEM by type 1
  item / 2 visual / 3 11197 "%d Mito Received" (i_d_m_1m) / 4 11199 mileage (i_n_00050) / 5 11198 "%d EXP Received"
  (exp_icon); OK enables when all 16 are shown -> the result screens **[exact]**.
- TOUR_REWARD: opened from TOUR_INFO VIEW_BOX, PvP state 16 and the tournament result; **0xd54** {channel} -> **0xd55** {type
  id, 16 x 32-byte names}; class tabs; 16 rows (brackets 01, 02, 03, 04-08, 09-16) with ITEM_NAME1..3; GM controls (0xd52 /
  0xd53). Table `init\rewardgroup.xlt` (GroupId, Reward_Type, Reward_Description, ItemId, Reward_Period, Reward_Count,
  Probability): `rw_gtc_<class>_<a..d>_<01|02|03|0408|0916>`; **no rw_gtc_rookie rows**, and Touring A b-d point at
  `rw_gtc_touringf_*` (data bugs). The same table holds **`rw_battle_low / _mid / _high`** ("Battle_Result": Exp / Mito /
  NormalItem rows with probabilities): the cards of the reward pick **[inferred from the group name]**.

### 13.6 Spectating, invites, emoticons, chat

- Observe: lobby OBSERVE (1000000 0x57a250 / 990000 0x579cf0): no password -> **0x1de** {room id}; password + 0x80 ->
  spectate without it; else PASSWORD in observe mode. Reply **0x1bf** (manager+0x58(.., 1): room-info `+0x78` "I spectate");
  0x1be is the player join reply. Wait room (0x57b060): OBSERVE_CNT 10244 "%d Spectators"; a spectator gets 1990000 (10870
  "You can only spectate in this room." when spectate-only - 0x1b9 sets it for drivers outside the arena limits -, 10858 in
  practice rooms, else 10235) and JOINGAME (**0x1dc**, only while waiting; wager rooms need 10x the fee); a guest who is not
  ready gets OBSERVE (**0x1db**). Spectator count **0x1cb**. No spectator cap in the client **[exact]**.
- VIEWING 1960000 (handler 0x756fa0; fill 0x50ad70 / 0x75e520): ROOMNAME, ROOMMEMBER "%d/%d", ROOMTYPE_SINGLE / TEAM,
  VIEWERCOUNT; RANK1..16 by progress (team colours 0xff3399ff / 0xffff3300), RANKNUMn "%d" + st / nd / rd / th, PLAYER_SEL
  beside the watched car (`stage+0xa0`), TP_A / B points; a row click watches it (0x75b450); ESC leaves (0x1d5). Keys Up /
  Down = previous / next car (clamped), Left / Right = the view (wrapping 0..5). Views (0x756e60): 0 NORMAL auto director, 1
  TPVIEW externalcam mode 4, 2 TOPVIEW mode 3, 3 QUARTER mode 2, 4 SIDE mode 5, 5 FPVIEW `frontview`. The director (0x4988b0)
  uses `spotwatch` (camera20) within 80 of a `<track>\spotcam.nxt` point (`id x y z n`), else a random externalcam mode
  1..7, never the same twice. externalcam presets (F, N, H): m2 (30, 5.5, 1.85), m3 (10, 8, 3), m4 (20, 16, 6), m5 (-20,
  -16, 6), m6 / m7 at the side **[exact]**.
- INVITE (WAIT16 1100020, 0x576d10, **5.0 s cooldown**, not for spectators / practice): FRIENDINVITE 1840000 + **0x1c2**
  {arena tab} -> **0x1c3** {count, entries id, name, grade}; LISTBOX rows CARGRADE "%d" + USERNAME; INVITE: 10467 "No friend
  selected." / 10468 confirm -> **0x1d1** {target, my name}. The invited driver gets 0x1d1 / 0x1d2 -> 10026 "You have been
  invited by %s to join a Battle Zone match.  Do you want to join?" -> wager check 11067 -> join **0x1d4** (0x1bc) **[exact]**.
- FINDFRIENDS 1080000 (0x57fc90): tabs 10220 "Find a Friend" / 10221 "Search Crew Members", COMBOBOX "name - status"
  (10504 "Offline") fills EDITBOX; FIND: offline -> 10219, else **0x296** {name} -> **0x297** opens USERINFO 1090000 (name,
  crew, level, EXP, Mito, records, km, ladder, location); ADDFRIEND 10203; the unnamed button 1090025 joins by the friend's
  location (2 a room -> 0x1d7, 3 password, 6 same car class else 10812, else 10202) **[exact]**.
- Emoticons (1130000, ui +0x260): no button - chat lines starting with `/` while WAIT16 is open go to 0x594120 (not
  printed); `init\emoticon.xlt` (command, category, icon) -> `ch_e_00NN`; drawn over the sender's slot (EMOTICON_00..15) for
  **5.0 s** (0x5940a0); ch_e_0007..0022 original, 0023..0038 Remastered additions; they travel as chat **[exact]**.
- Chat (binder 0x5b3340, 12 views): log CHAT_LIST 1000147 / CHATBOX 1100034, input 1000149 / 1100035, filter tabs and FIX /
  DROPDOWN scope; NORMAL sends "room" (0x5bcdd0); receive **0x093** {scope, name, colour, text}, "[%s]: %s"; 10891 blocked,
  10871 GlobalChatCycle **[exact]**.

### 13.7 PvP minimap 1170000 / 1180000 and the scripted cameras

- Minimap object (binder 0x628440; draw 0x628110 -> 0x627c60): 1180000 only with the ADV UI outside races (0x628370); a
  normal race uses 1170000. The track picture is the control named like the pvptrackinfo `name` (+ "<name>_guide",
  0x628130); mapping 0x6278b0 with the start row's minx..maxy: `px = imgX + (x - minx) / (maxx - minx) * imgW`, `py = imgY +
  (1 - (y - miny) / (maxy - miny)) * imgH`; no rotation / zoom; icon rotation pi/2 - heading; me MINE (MINE_A / MINE_B by
  team 1 / 2), the others PLAYER2 for team 1 else PLAYER1 **[exact]**.
- camera.ini loader 0x4da210 **[exact]**: stored fov = 2 atan(0.75 tan(fov / 2)) (ini values are horizontal 4:3); zoom / yaw
  / pitch are read only for some types (pvpready's are never read).
  - **pvpready** (0x75ae60, while now < T - 19.5): slots A -> B = 14 -> 15 (6 -> 7 when stage `+0x94`), camera = A + 7 Rz(yaw)
    (1, 1, 1) - dir x dist with dist += 3 dt, looking at the grid point that moves with it, turning round at |A - B|.
  - **pvpstart** (T - 19.5 .. T - 6): `<track>\camera.nif` (0x754040 -> 0x4d75c0; NiCamera Cam01 / Cam03, under a Group01 at
    the world position in most cities), played forward at 1x, then backwards at 7x (0x4dc500), about 12 s + 1.7 s.
    **Checked here**: at t = 0 the camera node's **-X axis** (the NIF's Euler keys as Rz Ry Rx) points straight at the start
    line's centre on the tracks tried (moonpalace track1, cras track2): the exported node looks down -X with Y up.
  - **pvpgoalin** (0x6bf490, after the goal-in banner): `init\camera_gi.dat` lines `<M|K|C|O|T>_<track> x y z tx ty tz`, a
    static camera looking at the target (0x4d9390).

### 13.8 Unity port (round 2)

New files (`Runtime/Game/`): `PvpGates.cs` (gate lines, GateTracker, lap-time lines), `PvpCameras.cs` (PvpCameraRig:
pvpready / pvpstart / pvpgoalin and the spectator views), `PvpMinimap.cs` (1170000), `PvpChat.cs` (lobby / wait-room /
VIEWING chat + emoticons), `PvpSocial.cs` (1840000 / 1080000 / 1090000, invites), `PvpSpectate.cs` (observe, 1990000,
VIEWING 1960000), `PvpWager.cs` (payout rule, 4080000 / 4090000), `PvpRewardPick.cs` (4310000), `LapTimeBattle.cs`
(3440000 / 3450000 / 3460000). Changed: `PvpRoom.cs` (wager, Real Match time and car, emoticon and rewardgroup tables),
`PvpLobby.cs` (wager grades, row icons, OBSERVE, FINDFRIENDS, LAPTIMEBATTLE, chat, the online GT Championship),
`PvpWaitRoom.cs` (spectators, INVITE, emoticons, chat, BATTINGTEXT, RMCAR / RMCARINFO, the server's AI slots, START / READY
checks 11107 / 11067 / 10850, the 10241 kick countdown), `RaceMission.cs` (gates for the player, cameras, minimap,
spectator races with VIEWING, Real Match ratings, wager settlement, reward pick), `OnlineRace.cs` (gates reported to the
server, cameras, minimap, local cars for the server's AI drivers, spectating, race.chat / invite / picked / wager / rm / tour
/ lap routing), `PvpResults.cs` (the wager screens), `Tournament.cs` (TOUR_REWARD table and its rewards offline, the server
bracket online). Server: `src/race.js` (gates, wager, Real Match time, AI drivers, spectators, chat, invites, find, reward
pick), new `src/pvptracks.js` (pvptrackinfo + rewardgroup readers), `src/laptime.js`, `src/pvptour.js`; `tools/bot.js
--host`; tests `test/pvp.test.js`, `Tests/PvpTests.cs`.

Remake rules (all **[remake]**: the client leaves them to the server):

| rule | value |
|---|---|
| checkpoint gates | every gate is reported (AllGateReport): `race.gate {n}`, n = gates passed over the race (each lap = the track's gates, then the finish line). The server takes them in order, each no sooner than its straight-line distance from the previous one at 75 m/s; the last one finishes the race; laps and the finish no longer come from the client's line index (`race.progress` only orders drivers between two gates). No-gate rooms (0x400) only need the finish line each lap and draw no posts. Wait-room rooms only (legacy rooms keep the old lap checks); `raceGates: false` turns them off |
| countdown | the server's T is 22 s after race.start (offline the same): pvpready until T - 19.5, pvpstart until T - 6, then the countdown; tests shorten it |
| wager | grades A..D from CREATEROOM (S exists in the data), fee x 10 to host / join / ready / start, two players to start, the fee is paid at the start (a driver who leaves loses it); pot = all fees - 10%: single 60 / 30 / 10% to the first three finishers (missing shares go to the winner), team: the winners share (a draw: everyone, minus the tax); DNF nothing. No AI drivers in wager rooms online (offline the AI drivers wager as rivals) |
| Real Match | a 50-minute session every 3 hours on the UTC clock (00:00 - 00:50, 03:00 - 03:50, ...); `race.rm` pushes start / ending (10, 5, 1 min) / end; RM rooms can be created any time but start only in RM time (10850). Everyone races the Duel's ratings (the player's car keeps its look); no ladder (as before) |
| AI drivers online | the host adds them to open slots (AI_BTN, arena tabs 0..4, not wager / crew rooms): slot value >= 0x10000, always ready, with a car and a skill. The server draws each AI driver's finish from its skill (track length x laps / (30 m/s x skill x 0.93..1.07)) and announces it like a player's (0x1eb); clients draw them as local AI cars on the line (positions are not synchronised; their places are the server's). AI places count in the race; rewards and the ladder need two players |
| spectators | up to 16 per room; a password room that allows spectators needs no password to watch; a running race is watched from where it is (race.start + race.go + standings) |
| chat | `race.chat` up to 70 characters, 8 lines per 10 s; lobby lines go to the channel's drivers looking at the same arena (`race.list {arena}` / `race.lobby`) |
| invites | to the channel's drivers who are not in a room (`race.invitees`), 5 s cooldown; the invited driver joins without the password. Offline an online friend of the social store comes over as an AI driver |
| reward pick | EXP arenas (0..4), wait-room rooms of 2+ drivers, not wager / crew / championship rooms: 16 cards from rw_battle_low (2X-3X arenas) / _mid (4X-6X) / _high (8X) by probability; each player picks one (`race.pick`), the server picks for the others after 10 s (11799) and then shows every card (`race.picked done`). Offline the AI drivers pick too |
| lap-time battle | one session per channel of a city with a lap track (CityType 4); 10-minute rounds (offline 5), 8 s between rounds; a run is 5 laps from a 10 s start, a new run starts 3 s after one ends; per lap EXP 10 + level and 50 Mito; the round's top three get Mito 3000 / 2000 / 1000 and EXP 500 / 300 / 200 (LAPTIMERET shows Mito / EXP icons). The track's line runs through its gates (no aipath; heights from the city's race lines). Offline 5-8 AI rivals post simulated laps |
| GT Championship online | `tour.enter {class}`: the channel's players of a class within a 60 s sign-up, filled with AI entrants to 16; the offline bracket (preliminaries by best lap - each player's heat is a server race with 3 AI entrants, the other laps simulated -, quarter-finals 2 x 4, semi-final 2 rounds 4/3/2/1, final). A heat starts when its players are ready (`tour.race`), or after 2 min with those who are. Reward by the stage reached (the offline amounts) plus the class's reward table items (rw_gtc_<class>_a_<bracket>; Rookie has none), paid by the server (`race.tourReward`); offline the same table items are given |

Protocol additions: `race.observe {id?, password?}`, `race.chat {text, arena?}` -> push `race.chat {from, text, scope}`,
`race.lobby {arena}`, `race.invitees` -> `{players [{name, level, grade}]}`, `race.invite {name}` -> push `race.invite {from,
id, title, wager, team, arena}`, `race.find {name}` -> `{name, level, exp, races, wins, ladder, bestLap, location, room}`,
`race.slot {op: ai | noai}`, `race.gate {n}`, `race.pick {slot}` -> push `race.picked {picks [{slot, name, type, amount,
item}], done}`, `race.rm` and push `race.rm {open, endsIn, startsIn, notice}`, push `race.wager {team, grade, fee, winner,
entries [{name, level, rank, team, delta, mito}]}`; room info adds `wager, observers, observerNames`, racers `ai, car, skill,
gate, p`; race.start adds `realMatch, wager, noGate, gates, gatesPerLap`, slots `ai, car, skill, level`; results add `ai`,
`wager`, `pick {ms, cards}`, `tour`. Lap-time: `lap.enter / lap.start / lap.gate {n} / lap.leave`, pushes `race.lapPlayers /
lapRound / lapStarting / lapLap / lapResults / lapRejected` (the client routes only `race.*` pushes). Championship:
`tour.enter {class, tracks} / tour.state / tour.race / tour.leave`, pushes `race.tour`, `race.tourReward`. Test hooks:
`DC_RACE_AI_SPEED`, `DC_RACE_PICK_MS`, `DC_RM_ALWAYS`, `DC_LAP_ROUND_MS`, `DC_LAP_START_MS`, `DC_TOUR_SIGNUP_MS`.

Tests: `Tests/PvpTests.cs` - `OfflineRoomRacePickSpectate` (lobby / room chat, emoticon, invite dialog, pvpready / pvpstart /
countdown, minimap, gate counter, reward pick, results, find a friend, spectating an AI room with VIEWING, JOINGAME),
`OfflineWagerRealMatchLapTimeTournament` (wager room and 4080000, Real Match room / RMCARINFO / Duel ratings, lap-time
battle HUD / notice / results, ladder, TOUR_REWARD and the table items), `OnlineRoomRaceWithBot` (server room with
`bot.js --race`: gates, room chat, reward pick from the server, results, ladder; spectating a `bot.js --host` room with a
server AI driver; the online championship sign-up); `DriftCityServer/test/pvp.test.js` (gates, rules, AI drivers + pick,
wager, spectators / chat / invites / find, lap-time battle, the online championship to the final). Known: the remake's
pvpstart orientation was verified numerically on two tracks only; online, spectated players' cars are where their
clients put them (a bot does not drive the track).

### 13.9 Decompiled this round

Lap-time and reward pick: 0x49c410, 0x7801b0, 0x720740, 0x754040, 0x681300..0x683f60, 0x71bc50..0x7212d0, 0x58bf70..0x58e4b0,
0x586510, 0x586560, 0x5871d0, 0x72ea60, 0x759060, 0x75a0d0, 0x6b2aa0 (disassembly: 0x683020, 0x683c40, 0x683650, 0x71c220,
0x71eae0, 0x58cac0, 0x58c450, 0x58d9b0, 0x58e570). Wager / RM / minimap / cameras / Oros effects / quickslot: 0x588f10,
0x58bce0, 0x58bb60, 0x58b870, 0x58a9b0, 0x58aa20, 0x58ab20, 0x58b520, 0x58ee80, 0x6af9e0, 0x6a6d20, 0x4da210, 0x4da990,
0x4db0f0, 0x4dc500, 0x4d75c0, 0x4d9390, 0x627c60, 0x627990, 0x6278b0, 0x627750, 0x628370, 0x628130, 0x5809f0, 0x580980,
0x580c20 (disassembly: 0x726360, 0x628110, 0x580f00, 0x508c00 ...). Spectating / invites / emoticons / chat: 0x79ca10,
0x79c7c0, 0x57e160, 0x756fa0, 0x756e60, 0x7583b0, 0x72c140, 0x75b450, 0x75b650, 0x758330, 0x75a7d0, 0x75a400, 0x756a10,
0x75d830, 0x757500, 0x509af0, 0x5095d0, 0x507db0, 0x507f00, 0x5c18a0, 0x57f9f0, 0x57f840, 0x57f7a0, 0x782b50, 0x594050,
0x594490, 0x5bd610, 0x5bcbe0. Extracted data: scratchpad `pvpx\` (camera_gi.dat, camera.nif files, tables as text).

## 14. Round 3: ghost battle, replays, ROUNDNUMBER, bonus icons, Real Match looks

Recovered from the exe in this round (decompiled files in `decompiled/phys`, list in 14.9), then converted where the data
allows. **(fix)** marks corrections to earlier sections.

### 14.1 Ghost Battle (GHOSTJOIN 2000000 / GHOSTEXIT 2010000)

- **Feature word net `+0x274`** (deserializer 0x453c40, second dword) **[exact]**: bit 0 BattleZone.RoomGradeLimit, bit 1
  BattleZone.BattleReward (the reward pick, 13.5), **bit 2 BattleZone.GhostBattle**, **bit 3 BattleZone.PvpGhostBattle**,
  **bit 4 BattleZone.GhostBattleOnOff**, bits 5..8 BattleZone.FrontAi, then LottoPVPReward, PreMatchTime, MatchWaitTime,
  MainMatchTime ... Bit 4 shows lobby / wait-room controls (RMLOBBY `+0x838` at 0x5776bc / 0x578d70 / 0x578f73 /
  0x5793e1; WAIT16 slot control `+0x484` at 0x57b6f2 when bits 3 and 4, "I spectate" and the slot is used).
- **What it is** (texts 11194-11200, items `ch_ghostpass` vshopitem 10825 "Ch. Replay Ghost Pass: ... initiate the Ghost
  Battle Mode during Channel Battle replay at anytime" and `ch_replaypass` 10824, both 7-day cash items) **[exact]**: a
  spectator of a running race (a Channel Battle broadcast or replay with bit 2, or a Battle Zone race with bit 3) jumps
  into it as a "ghost" of the watched driver and races that "original" car: 11194 "You've entered the Ghost Battle. If
  you win the battle with selected user, Mito and EXP will be rewarded.", 11195 exit, 11196 "You cannot join the Ghost
  Battle at this time.", 11200 "Selected Ghost User has been disconnected. Moving back to the lobby."
- **Button** (0x75e520) **[exact]**: GHOSTJOIN (J = 71, G) is drawn beside the watched driver's VIEWING row (`+0x6c4`:
  y = row - 0x26) when the room is not a 0x100 (Real Match) room, the mode is not 1, bit 2 is on (bit 3 too outside a
  broadcast), the ghost state `+0x7e4` is 0 and the watched slot is used; a spectator (own id 0xffff) also needs
  `ch_ghostpass` or a `ch_ghost_%02d` ticket of the channel (0x17c+0x24) with bit 2 (0x75ed30).
- **Join** 0x759060 (GHOSTJOIN click 0x75a5b0) **[exact]**: the watched car must be racing (`+0x1bd0 > 0`) and the race
  started; the first join of a race (`+0x7f0`) uses the pass, else uses up a `ch_ghost_%02d` ticket (0x78ce10 item use),
  else nothing happens; state 1, time `+0x7e8`, view 3 and the camera mode 3. 0x759360 (state 1 -> 2) sends **0x212**
  {spectator flag, watched car id `+0x1be0`, `+0x1bcc`, time, ms, 80 + 40 + 128 bytes of the watched car's look from
  0x17c+0x44} (0x787e80). Reply **0x213** (0x761580): the 80 + 40 bytes become the own car's parts (the ghost wears the
  original's look), state 3, the own car is placed on the original's position, heading and speed (0x489df0 with
  `+0x1b9c..+0x1bb4`), player state `+0x838 = 7`, the race clock synced, the camera follows the own car. 0x75fc10 gives up
  a request unanswered for 10 s (0x757500). **Exit** 0x757500 (GHOSTEXIT J = 27, 0x7583b0): removes the own car
  (0x4c2770), back to the spectator camera, GHOSTJOIN shown again when allowed. While in state 3 0x75e520 draws the own
  car in the ranking (0x756b10 / 0x75cf20); 1170000 has a ghost icon `ID_PVP_MINIMAP_ICON_GHOST`.
- **Remake** (`Game/PvpGhost.cs`; hooked in `PvpSpectate` VIEWING, offline AI races, online spectating and replays) **[remake]**:
  the ghost is local (no 0x212 to the server; online the car's `state` goes out as usual); the player's own car is put on
  the original (position, heading, speed) and drives through the race cars (collisions ignored), keeping its own look;
  laps are counted on the racing line from the original's lap; the ghost wins by finishing before the original (the
  race reward of a Free-arena 1st place through `Progress.Reward("race")`) and goes back to watching 3 s after its
  finish, on GHOSTEXIT / ESC, at the end of the race, or with 11200 when the original disappears. Allowed with
  `ch_ghostpass` anywhere, or with the ticket that comes with a replay bought from the replay list (the client's per-channel
  `ch_ghost_NN` tickets are not in the item tables); the first join uses it, later joins in the same race are free.

### 14.2 Replays

- **The client records its own race** **[exact]**: the replay manager (vtable 0x87cc28, ctor 0x73d47b) slot 0 = 0x73e1e0 opens
  `play_record.r0` ("wb") and writes a 0x8c-byte header (0x26d with the room info and 32 dwords of settings), the room reply
  0x1be with every racer (0x3d bytes each: id, slot, name, car, ...) and one 0x329 car record (0xd6 bytes) per racer, then
  slot 13 (0x73d5e0) appends the race's packets into a 256 KB buffer (written out when full) and slot 12 (0x73d530) flushes
  and closes it ("replay size=%d"). A real 194 KB `play_record.r0` sits in the game folder.
- **Channel Battle replays** **[exact]**: the replay list 3410000 (0x395 -> 0x396); buying (slot 14, 0x73d7b0): price 1000 /
  500 / 0 by channel kind (`+0x20` field < 2 / < 10 / else), free with `ch_replaypass`, confirm 11256 "You need %d Mito to see
  the selected replay" / 11260 "Do you want to watch the selected replay?", the record copied to game `+0x3c880`; the replay
  arrives in chunks (slot 11, 0x742af0: {total, offset, length, data}; 0 -> "replay file size=0"; "ReplayData(%d,%d,%d)"),
  once complete 0x742f50 starts the replay stage (stage 0x11, mode 0xd, the "replay" label, text 10057 "Replaying.") whose
  player (0x73d9c0) reads the packets back on their recorded clock (`+0x3b624` race start rebased) - a spectator's view of
  the race. 11257 / 11262 / 11390 "Channel / Semi Channel / Event Channel %d Replay".
- **Remake** (`Game/PvpReplay.cs`) **[remake]**: poses instead of packets. `PvpReplay.Recorder` samples every driver's position
  and heading 5 times a second from GO (RaceMission and OnlineRace; remote cars as the client sees them, the server's AI
  drivers as drawn); at the results it takes places and times and becomes `PvpReplay.Last` (saved as
  `persistentDataPath/play_record.r0.json`); offline Channel Battle rounds (the stand-in room, `PvpRoom.IsChannelBattle`)
  also go to `PvpReplay.Local`, the offline replay list (free). The server records every wait-room race the same way
  (race.js `startReplay` / `sampleReplay` / `endReplay`: each racer's last car `state`, 4 a second, 15 minutes at most;
  the server's AI drivers have no pose, their finish time places them) and a crew round's recording goes into its replay
  row (`crew_replays.data.rec`, channelbattle.js `afterRound`). `ReplayPlayer` draws one car per driver (frameless
  drivers run along the line at their average speed), opens VIEWING 1960000 (room name + "Replaying.", the six views,
  Up / Down, ESC), shows ROUNDNUMBER for crew rounds, offers the Ghost Battle (a bought replay carries one ticket) and ends
  with the race's result board. `ChannelBattle.WatchReplay` plays a server replay that has a recording when its city is
  the current one (else the old result-board popup with the reason); offline rows (ids < 0) play the local rounds.

### 14.3 ROUNDNUMBER 1420000

**[exact]**: object vtable 0x87412c; binder 0x673aa0 binds 1420002 / 1420003 / 1420004 (ROUNDNUMBER1..3, ui_contents65
glyphs); 0x673af0(round) shows only the round's glyph; 0x673b40 refreshes it from the room info `+0x74` while the race
stage (game `+0xcc` == 0xf) runs and the dialog is up. Rounds as SOCIAL.md 6 (10344 Round 1, 10345 Round 2, 10346 Final).
**Remake**: `channelbattle.js battleStart` sets `room.cbRound` = attack streak + 1 (max 3), `race.start` carries `round`
(0 outside crew rounds), `OnlineRace` shows it through `PvpHud.ShowRound` for the race, the replay player for crew rounds.
The offline stand-in has no rounds.

### 14.4 Real Match looks (effects/realmatch/visual.xlt)

**[exact]**: 0x70de00 reads the 16 rows (Color, Decal, DecalColor, Neon) into the character's car table `+0x74c` (records
0x38 bytes); packet 0x1c7 (0x6e7070) sets the RM car's parts and 0x70e240 copies three fields of the room's car record into
all 16 entries: the 16 identical RM cars (the Duel, 13.3) are told apart by colour, decal, decal colour and neon. Slot ->
row is **[inferred]** (16 rows, 16 slots). (fix) `effects\realmatch\real_%05d.dds` is not Real Match: 0x4411e0 / 0x6419b0
load `real_<id+10000>.dds` (90000..90060, `real_test.dds` fallback) for ids below 100000 and ask the server (0x3f6) for
others - custom marks **[inferred]**. **Remake**: `Pvp.RealMatchLookFor(slot)` / `RealMatchPaint(slot)` / `RealMatchCar()`;
AI drivers in a Real Match room (offline and the server's AI online) drive the Duel painted with their slot's colour.
Data tables round: `CarPaint.ApplyRealMatch(car, duel, look)` paints the Color, composites the Duel's decal.nxt layout
`Decal` with its stamps in VehicleDecalColor `DecalColor`, and fits the underbody neon in VehicleNeonColor `Neon` (AI
cars via `RaceMission.SpawnAi`); `RealMatchSkin.Apply(car, slot)` lays a Duel in the slot's look over the player's own
car (RaceMission / OnlineRace, removed after the race) and over remote players' cars of the room (OnlineRace, by the
race.start slot of each name), hiding the car's own meshes; the skin's wheels follow the car's. Test
`RealMatchLookTests` (Preview/rm_looks.png, rm_skin_on.png). Earlier: decal / decal colour / neon were missing and the player's own and remote players' cars kept their look (the
remake's player car is built once with its physics; swapping its body for the race is left out).

### 14.5 PvP gate effects (NewGateEffect, feature 0x40000)

**[exact]**: packet PlayerEvent (handler 0x50e530, table 0x85fa30: {charId, type, value}) -> stage vtable 0x726f20 for the
local player (counts per type at `+0x67`, NewGateEffectFactor bits 19..28 scale the times) and 0x757750 for the watched car
of a spectator: type 0 pvp_null, 1 pvp_boostcharge100, 2 pvp_boostcharge50, 3 pvp_skidrush (30 s), 5 pvp_untouchable (5 s),
6 pvp_booost (30 s), 7 pvp_boostcharget50 (4 none); 0x727b70 plays pvp_repair_girl_ui / pvp_overheat (0x2c / 0x27, also
0x4791a0). What each gate gives is the server's (not in the client). **Remake**: `GameEffects.GateEffect(type)` /
`PlayGateEffect(type)` give the banner and its time; no gate-effect rule is run (the remake's gates only count laps).

### 14.6 Mission-battle rooms (room flag 0x10000)

**[exact]**: rows with 0x10000 cannot be joined from the list and show no OBSERVE (section 3); WAIT16 MISSIONBATTLE_TEXT
11479 "You will be performing the mission with fixed stats that are not affected by your vehicle performance."; 11480 "You
cannot perform Mission Battles while in a Party." (0x5c69c0 / 0x5d52d0); a GM chat command (0x5b8200) toggles the effect
manager's `+0x1c` object: "MissionBattle Off / On / On(UFO) / On(Ring) / On(Rock)" (modes 0..4 in `+0x58`); pvptrackinfo
has 8 MissionBattle flags per track. The rooms are made by the server for a mission; the client has no objectives, object
placement or rewards for them. Not converted (nothing to drive them).

### 14.7 TOUR_REWARD GM controls

**[exact]**: 0x58e430 (TOUR_REWARD open) shows the GM panel (`+0x5c0`, 0x58c370(1)) only when the character's authority byte
`+0x7c` < 'N'; its button sends **0xd52** {int, int} (0x787210) and **0xd53** (in 0x519120) answers. GM only: not converted.

### 14.8 Result bonus icons (1480000..1610000)

**[exact]** (0x588100 single / 0x58a070 team, binder 0x588910..0x588d6a; entry dword `+0x8c` = flags): own row: flag 1 with
Mito -> PCROOMBONUS 1490017 + row, flag 2 with EXP -> 1490001 + row; 0x10 -> EXPBONUS 1500001 + row, 0x20 -> 1500017 + row,
both -> 1500033 + row (EXP30% / Mito30% / both art); 0x200 / 0x400 / both -> SANTABONUS 1510001 / 1510017 / 1510033 + row
(EXP 25 / 50 / 75%); the Christmas-tree stage 1..4 (0x7733a0) -> TREEBONUS 1520001 / 1520017 / 1520033 / 1520049 + row
(EXP+Mito 30..60%); 0x800 -> 3RDBONUS 1530001 + row; flag 8 plays UI effect 43 goldentime. Another driver's row: flag 1 ->
PCROOMBONUS 1480001 + row. 1620000 (PCROOMEVENT: UPCOUNT, MITRONMIDGET) is a PC-room event counter. Team dialogs
1560000..1610000 have the same lists. **Remake**: `PvpResultEntry.Bonus / Tree`; the driver's own row gets 0x10 / 0x20 from
the EXP / Mito drinks (ItemUse buffs), 8 from the scratch-card Battle EXP goal (golden time), the tree stage from
`SeasonEvents.XmasCitiesDone` (`Pvp.BonusFlags`, `Pvp.TreeStage`); online a `bonus` field of the server's result row wins
when present. No PC-room, Santa or anniversary bonuses exist in the remake. `PvpResults` opens the icon dialogs beside the
rows (the team list uses 1560000.. with the single layout [inferred]).

### 14.9 Unity port (round 3)

New files (`Runtime/Game/`): `PvpReplay.cs` (ReplayRecording / ReplayCar / Recorder / ReplayPlayer), `PvpGhost.cs` (GHOSTJOIN /
GHOSTEXIT, GhostRunner). Changed: `RaceMission.cs` (recorder from GO, `PvpResultEntry.Bonus / Tree`, Real Match AI cars,
ghost on spectated races), `OnlineRace.cs` (recorder, `round` -> ROUNDNUMBER, bonus flags, RM AI cars, ghost),
`PvpSpectate.cs` (`Driver.Fraction`, `WatchedTrack`, `ResumeWatching`, the GHOSTJOIN hook, `running` callback),
`PvpResults.cs` (bonus icon dialogs, golden time), `PvpHud.cs` (`ShowRound` / `HideRound`), `PvpRoom.cs` (bonus flags,
`IsChannelBattle`, Real Match looks), `Social/ChannelBattle.cs` (replays: play the recording, the offline list),
`GameEffects.cs` / `OrosAmbience.cs` (EFFECTS.md). Server: `src/race.js` (replay recording, `round` in race.start),
`src/channelbattle.js` (two lines: `room.cbRound`, `rec` in the replay row). Tests: `Tests/PvpReplayTests.cs`
(`OfflineReplayGhostRoundBonusEffects`), `test/pvp.test.js` ("replays: the racers poses are recorded ...").

Decompiled this round: 0x73d530, 0x73e1e0, 0x742f50, 0x73dfd0, 0x742c40, 0x742e60, 0x61e670, 0x61e900, 0x6253c0, 0x625a60,
0x454120, 0x759360, 0x508330, 0x787e80, 0x761580, 0x5076c0, 0x70de00, 0x4411e0, 0x6419b0, 0x6e7070, 0x70e240, 0x588100,
0x6a3e50, 0x80fc20, 0x80ec30, 0x80e7a0, 0x80fcb0 (disassembly: 0x73d7b0, 0x742af0, 0x75a5b0, 0x757750, 0x50e530, 0x726f20,
0x673aa0..0x673b80, 0x588910..0x588d6a, 0x58e430, 0x787210, 0x5b8200, 0x5c69c0, 0x6a4db0, 0x6a4e50, 0x6a6200, 0x697af0,
0x697b30, 0x69b900). Extracted: scratchpad `pvp3\` (visual.xlt, globaltext.xlt).

## 15. Round 4: mission battles, gate effects, TOUR_REWARD GM, the last Real Match bits, crew ghost tickets

Recovered this round (decompiled `0x734a00.c`, `0x5b8200.c`; disassembly 0x737b97..0x7383e6), then converted. Earlier "not
converted" lines in the Unity port table and 14.5 / 14.6 / 14.7 are superseded by this section.

### 15.1 Mission-battle rooms (room flag 0x10000)

- **GM command** (0x5b8200) **[exact]**: `/missionbattle %d %d` (the first 14 characters compared with `_wcsnicmp`): the first
  number sets the effect manager's object set `+0x1c` on (`+0x54`), the second its kind `+0x58`: 1 "MissionBattle On(UFO)", 2
  "On(Ring)", 3 "On(Rock)", anything else "MissionBattle On"; 0 prints "MissionBattle Off".
- **Objects** (loader 0x737b97..0x7382ee, one block per NIF) **[exact for the files, inferred for the kinds' meaning]**:
  `game/pvp/t_ob_00012.nif` (kind +0x18 = 0, weight +0x1c 3.5, +0x20 2), `t_ob_event_01.nif` (kind 3, weight 3.5),
  `t_ob_event_02.nif` (weight 100, +0x20 4), `t_ob_event_03.nif` (**kind 4**, weight 50, +0x20 2, once-only +0x24 3),
  `effects/undercity/ufo.nif` (+0x14 8, weight 50; sounds SID_UFO / SID_UFOCRASH), `traffic_check_light_b/r.nif` (kind 5,
  +0x20 8, +0x24 2), `traffic_rock.nif` (kind 6, +0x20 5), `traffic_meteor.nif`.
- **Hit** 0x734a00 **[exact]**: weight > 0 slows the local car (0x479050 with weight x k); kind 1 plays the object's own
  animation (0x8168f0 / 0x8169a0); kind 4 plays UI effect 50 `t_ob_event_04` (the ink splats), lowers the object by its size
  and turns it along the hit direction (knocked over), and puts the car in a 2 s state (vehicle `+0x590 |= 1`, `+0x594 = 2.0`
  unless one is already running); a +0x24 of 2 / 3 disables the object after the hit.
- **Remake** (`Game/PvpMissionBattle.cs`) **[remake]**: `ChatCommand` parses the GM line (offline at once; online only for an
  admin: `race.gm {op: missionbattle}`; hook in ChatBox below). While the switch is on, CREATEROOM makes mission-battle
  rooms (`PvpRoom.Mission`, `room.mission` online: only from an admin with the switch on). Rows with the flag are refused from
  the list (10030; an invite lets a driver in), a driver in a party gets 11480, WAIT16 shows MISSIONBATTLE_TEXT 11479, and
  everyone races the fixed stats (the Real Match Duel's ratings). From GO, objects stand every 90 m of the racing line from
  150 m on (left / centre / right, 3 m apart, the same on every client): UFO = `ufo.nif`, Ring = `t_ob_00012.nif`, Rock =
  `traffic_rock.nif` (kind 4), plain On = `t_ob_event_01` / `_02` with every 4th `t_ob_event_03` (kind 4). **Objective**: a
  collected object (within 3.5 m) gives +25 % booster gauge and is gone for the lap; a kind-4 object is the ink hit
  (`t_ob_event_04`, speed x 0.6, no booster for 2 s, the object falls over and stays down). The HUD line counts collected /
  total and hits. Nothing is sent to the server (the client's effect manager was local too).

### 15.2 Gate effects (NewGateEffect 0x40000, PlayerEvent types)

**[remake]** (`Game/PvpGateEffects.cs`, race.js `gateEffectFor` / `gateEffect`): every passed gate except a lap's finish line
draws a type by the driver's place p of n, f = (p - 1) / (n - 1): leaders (f < 0.34) 60 % 0 pvp_null, 25 % 2 boostcharge50,
15 % 3 skidrush; the middle 30 % null, 30 % charge50, 20 % 1 boostcharge100, 20 % skidrush; the back (f > 0.66) 10 % null,
20 % charge50, 30 % charge100, 20 % 6 booost, 20 % 5 untouchable. Team rooms give 7 boostcharget50 (the team's half charge)
to every member of the side instead of 2. Not in no-gate, practice or wager rooms. Effects: 1 the gauge full, 2 / 7 +50 %,
3 skid rush 30 s (drifting fills the gauge 3x), 6 booost 30 s (the gauge fills 3x while driving), 5 untouchable 5 s
(Physics.IgnoreCollision with the other race cars). Online the server draws and pushes `race.gateEffect {id, name, type}` to
the racer (and to spectators: the watched car's banner, 0x757750); offline RaceMission draws for the player. Server switch
`gateEffects: false` / env `DC_GATE_EFFECTS=0`.

### 15.3 TOUR_REWARD GM controls

**[exact]** controls: GM_BACK1-3, GM_ITEMNAME, GM_LEFTBTN / GM_RIGHTBTN, GM_APPLY (0xd52 {int, int}). **[inferred]**: the
pair is {class, reward type}, the type id that 0xd55 returns being the class's reward table letter (`rw_gtc_<class>_<a..d>_*`).
**Remake** (`Game/PvpTourRewardGm.cs`, two lines in Tournament.cs): the panel shows for admins (`race.gm {op: check}`; offline
with `DC_GM=1` / `ForceGm`); LEFT / RIGHT cycle "Reward type A..D", APPLY sets the shown class's table: online
`race.gm {op: tourReward, class, set}` (race.js `tourSets`, read by pvptour.js `eliminated`), offline locally; TOUR_REWARD and
the offline championship reward read `PvpTourRewardGm.Letter(class)`.

### 15.4 Real Match skin: lamps, flames, the hidden car's particles

`RealMatchSkin` now drives the skin's own `_light.nif` lamps (tail_light while the car brakes - a remote car: a slow-down over
4 m/s^2 -, reversing_light backwards) and its booster flames (the car's `Boosting` / the remote's flag), and switches the hidden
car's `BoosterFlames` off (their NIF particle part was still emitted at the hidden body's exhausts), restoring it on Remove.

### 15.5 Channel Battle ghost tickets

**[exact]** useitem `ch_ghost_01..20` ("Ch 01..10 / Semi 01..10. Battle Ghost Ticket", 7 days) - the items exist after all
(14.1's "not in the item tables" is wrong). **Remake**: `crew.replay` gives the bought replay's channel ticket (none with the
Ghost Pass), `PvpGhost.SetChannelTicket(channel)` allows the Ghost Battle while it is held and the first join uses it up
(`crew.ghost {channel}` online). Server replays no longer carry the free ticket (offline local replays still do).

### 15.6 Unity port (round 4)

New: `PvpMissionBattle.cs`, `PvpGateEffects.cs`, `PvpTourRewardGm.cs`, `Social/CrewMarkUpload.cs` (SOCIAL.md 9). Changed:
`RaceMission.cs` (gate effects, mission objects and fixed stats, cleanup), `OnlineRace.cs` (`race.gateEffect`, `mission`),
`PvpLobby.cs` (mission flag, list refusal, 11480), `PvpWaitRoom.cs` (`mission` from the server, 11479), `PvpRoom.cs`
(`Mission`), `PvpGhost.cs` (channel tickets), `RealMatchSkin.cs`, `Tournament.cs` (two lines), `Social/ChannelBattle.cs`.
Server: `race.js` (`room.mission`, `gateEffect`, `race.gm`, `tourSets`, `gateEffectFor`), `pvptour.js` (one line),
`channelbattle.js` (`ghostTicket`, `crew.ghost`). Tests: `Tests/Round4Tests.cs`, `test/round4.test.js`.
