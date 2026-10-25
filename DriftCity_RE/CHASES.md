# HUV chases: the call, the chase screens, party chases, Rush Time, the Virtual HUV, Auto-Drive

What DriftCity.exe does for the HUV chases and the modes built on them, and how the Unity rebuild plays them
(`Runtime/Game/ChaseMission.cs` with `HuvTarget`, `ChaseScreens.cs`, `ChaseSpawns.cs`, `RushTime.cs`, `VirtualChase.cs`,
`AutoDrive.cs`; server `src/partyplay.js` party chases, `src/economy.js` kinds `chase` / `rush` / `vhuv`).

**[exact]** = read from the exe or its data. **[inferred]** = the meaning follows from the data or the client's use.
**[rebuild]** = the server decided it; the value is the rebuild's own. Decompiles: `decompiled/phys/0xADDR.c`; for
addresses Ghidra has no procedure for, the annotated disassembly (`scratchpad/mc/asm.py`). Text ids: globaltext.xlt (US).

## 1. The client's chase state [exact]

The game mode object `0x4386b0()+0x15c` holds the chase state at `+0x14`, the time left (float seconds) at `+0x38`, the
target point at `+0x1c..+0x24` and the drivers in a Rush Time zone at `+0x54`; the HUV manager `0x4386b0()+0x168` holds the
target HUV id `+0xdc`, the Team ROO flag `+0xe0`, its health `+0xe4` and the HUV count `+0xf8`.

| mode | meaning | set by |
|---|---|---|
| 0 | free | 0x554850 (quit), result handlers |
| 1 | chase requested: CHASEINFO's map is drawn (0x6fd500) | |
| 2 | started (BS_PktChaseBeginConfirm) | 0x4f2500, 0x4f2c00 |
| 3 | chasing: CHASEPLAYINFO drawn (0x556780), 0x55d950 shows it and runs 0x6f7c10 | |
| 6 | caught | 0x4f9dcd |
| 7 | failed | 0x4f9dcd, 0x500870 |

Frame 0x6f7d80 (called from the mission HUD frame 0x55d950): mode 6 / 7 hides the GUIDETEXT strips 8 / 9 (HUV_FIND /
HUV_CRASH), plays UI effect 10 (`arrest_success`, Team ROO 0x2d `beat_success`) + sound 0x4e90 SID_HUV_SUCCESS, or effect 9
(Team ROO 0x2e `beat_fail`) + 0x4e91 SID_HUV_FAIL + chat 10619 "Chase Failed", and switches the music to the city's
(0xea6b.. by city); the banners go after 15 s (success) / 7 s (fail). Mode 3 alternates HUV_FIND / HUV_CRASH by the target
HUV's state (2 s). Sound ids: the SID table 0x5232b0 stores each id after its name (0x4e98 SID_CALLSIGN_RING,
0x4e99 SID_CALLSIGN_ACCEPT, 0x4e9a SID_CALLSIGN_DENY).

## 2. The chase screens [exact]

| dialog | code | content |
|---|---|---|
| CHASING 870000 / EVENTCHASING 880000 | handler 0x74c2f0, blink 0x6f5a40 | the HUV call: TABBUTTON (Tab, J=9) sends the chase request with the car's position (0x74c280 -> 0x783a20(1, pos)), CANCEL refuses (0x74be20), 870008 toggles a flag. NOTIFY1-4 cycle with `fmod(t, 0.8)`: <= 0.08 NOTIFY2, <= 0.16 NOTIFY3, <= 0.24 NOTIFY4, else NOTIFY1; SID_CALLSIGN_RING when it comes up. EVENTCHASING (Christmas art) while the server flag net+0x288 bit 7 is set |
| CHASEINFO 610000 / 620000 / 630000 / 640000 | 0x6f7c10, handler 0x5546d0 -> 0x6fd500 | one per city (net+0x3b4ec): the city's chase map; the IMG control is the icon region (Moon Palace's carries chasing_map_moonpalace.dds, the others lie under the popup art). 0x5f3fe0 places icon 1 (me, heading), 4 (the target point) and 3 (party members, headings) |
| CHASEPLAYINFO 890000 | draw 0x556780 (message 0x2001), handler 0x557480 | the time left as digit art: TIME_6 (minutes' tens, only when not 0) TIME_5 " TIME_4 TIME_3 . TIME_2 TIME_1 TIME_0 (thousandths) from TIME_NUM_0-9; the HUV count 0..99 (HUVCOUNT0-9, HUVCOUNT_TEN0-9 only when the tens are not 0). CANCEL (Esc) asks 10727 "Do you really want to quit the Chase?" -> 0x554850: music back, packet 0x783bb0, mode 1 -> 0 |
| CHASERESULT 970000 | handler 0x554970 | TIME / EXP / MITO. The one-driver filler is an empty stub (0x62ef70 `ret 8`): a solo chase ends with chat and the rising lines only |
| PARTYCHASERESULT 980000 | 0x5539f0, handler 0x5549d0 | TIME `%d"%d"%d` (minutes, seconds, thousandths x 60 / 1000), per row USER (the name), EXP `%d`, MITO `%d Mito`, each the base + bonus pair of the record; LVUP icons hidden; empty rows "0.0" |
| QUEST 1780000 | 0x5612a0 / 0x561350, 0x560ac0, update 0x5613a0 | "%d EXP" (10172) / "%d Mito" (10173) rising from LOW to HI (y -220) over 1.4 s; called by the chase, delivery, instant, Undercity, PvP and event rewards |
| QUEST 1770000 | 0x5531e0, 0x563710 | a new best time in large digit art (POS0-6) with UI effect 14 `qs_newrecord` |

The chase result packet 0x4f9dcd: +2 success, +6 time (ms), +0xa member count, records of 0x7c bytes (name at +0,
member id, Mito base/bonus, EXP base/bonus, totals, level, items). For my record: chat 10617 "Chase Successful" and
10618 "Earned %d Mitos. Earned %d Exp." (or only 10618), 1780000, the stats; then mode 6 / 7; more than one member ->
PARTYCHASERESULT (0x5539f0), one -> the stub. BS_PktChaseBeginConfirm 0x4f2500 (scenes 0xe / 0x13): chat 10615 "Chase
Started", mode 2, up to 16 HUV units (0x30 bytes each, `huv (` debug lines) spawned by 0x44a5d0, the time limit into
+0x38, instant mission kind chase_start. BS_PktPartyHuv 0xc29 (0x4f45b0) is the party's HUV spawn.

### Spawn points [exact data]

`<city>\chasespawnlist.text` (text, "jc / nhn-agt / 20070902"): the point count, then per point `posID`, `x y z //pivot`,
a name, the slot count (16) and per slot `x y z //pos` + `rx ry rz //rad` - 16 HUV places in a row. `init\chasespawn.xlt`
names them per city (CityIdx 0-3, PointID, "pos name": Moon Palace 37 "Gold Wing Memorial Airport" .., Koinonia 47, Cras 41,
Oros 47).

### Rebuild rules [rebuild]

- The call: `ChaseMission.Offer(session)` shows CHASING (EVENTCHASING in the Christmas season); Tab accepts. Today
  CitySession still offers chases with INSTANT_CONFIRMADV 4400000 (hook below).
- **The units are the server's** (BS_PktChaseBeginConfirm 0x4f2500: +0x1b unit count, at most 16, units of 0x30 bytes;
  0x4f0410 copies each unit's +0xc speed / +0x10 top speed (km/h x 0.2778), +0x14 / +0x18 the burst and its time, +0x1c the
  turbo % x 0.01, +0x24..+0x2c the avoid levels; +0x17 the time limit plus an item bonus) [exact]. The remake's rule
  (`src/huvrules.js`, mirrored offline by `ChaseRules.cs`) [rebuild]: **count** 1 + level / 30 (1 / 2 / 3 / 4 from level
  1 / 30 / 60 / 90), +1 for a party of 3-4, at most 16; a boss chase is one boss; **convoy**: from level 40 one chase in
  ten sends 16 HUVs from all 16 slots of the spawn point in a row at one speed, 30 health each, 240 s; **speed** 85 +
  level / 2 km/h (at most 140) +-5, top speed +30, a 1 s burst when hit, 10 % turbo (bosses: kmh 0 = their BossHuvInfo
  row, 0x4a0f10); **health** the quoted 100 + Crash bounded to 100..250, bosses 300; **time** 150 s + 30 s per HUV beyond
  the first (at most 300 s), bosses 180 s. Online a solo chase asks `chase.begin {boss, huv, hp}` (mode 1 until the reply
  `{units, seconds, convoy}`); a party chase gets the units in `chase.party.start`. `ChaseMission.Units`, `HuvTarget.ApplyUnit`.
- The HUVs start as the client placed them (TRAFFIC.md section 15, 0x44a5d0): on the slots of the nearest spawn point
  beyond 1 km, HUV k on slot k driving the road route to its own waypoint (a convoy: one shared waypoint, in a row) and
  then the shared goal 1-3 km on; a boss on a route of Huv00.xipath2 passing 250-1200 m away, with its convoy; the older
  rule (a point 250-900 m away, random turns) only when no route can be laid. CHASEINFO is up for the 5 s briefing (mode
  2), then mode 3 with CHASEPLAYINFO; HUV count = HUVs left to catch. A hit is the impact km/h x 0.45, 6..45; escaping
  counts only after the HUV was found (within 300 m): 12 s beyond 650 m.
- The kept HUVs (TRAFFIC.md section 11, exact): at the boss's threshold (BossHuvInfo 컨버팅시간 40 % of its health taken
  off) 11001's trailer stages run 3 + 3 + 2.367 s, Lil' Grimer leaves as a target of its own (health = the boss's x 300 /
  1400, 내구력) and 10 s later the truck is finished; 11004's baby dozers come out 5 s after the threshold.
- The end: as the client (chat, 1780000, banners), plus CHASERESULT for a solo chase with the same formats as the party
  screen. Reward: (boss ? 1500 : 300) + 60 x level EXP, (boss ? 12000 : 2500) + 400 x level Mito (unchanged).
- Animated boss HUVs list a .kfm; the model is the .nif of the same name (`HuvTarget.BodyNif`).

## 3. Party chases [rebuild, server `src/partyplay.js`]

- `chase.party.start {boss, huv, hp, bossId}`: the party leader (20022), 2+ members in the leader's city, not racing, in
  the Undercity, another party chase, a party patrol, Rush Time or a V-HUV (11061). The units by the rule above (the
  leader's level, the party size); push `chase.party.start {id, boss, bossId, huv, hp, seconds, convoy, units, owner,
  members}` to every member. The HUVs are shared as BS_PktPartyHuv shared them (TRAFFIC.md section 14): the owner (the
  leader) runs them and sends `chase.party.huv {cars}` (every HUV, each followed by its escorts), relayed to the others,
  whose copies are dead-reckoned; the next member takes over when the owner leaves (`chase.party.owner`).
- `chase.party.hit {damage, unit}`: a budget of five 45-damage hits a second per member; push `chase.party.hp {unit, hp,
  max, by}`; all units must be stopped. A boss with kept HUVs (bossId 11001 / 11004) releases them on the server:
  `chase.party.split {stage: 'start'}` at the threshold, `{stage: 'out', units: [{unit, huv: 11002, hp}], escorts}` when
  they come out (Lil' Grimer a unit of its own; the clients spawn it and the dozers, the owner runs them), `{stage:
  'finish'}` when the truck is finished (its unit's health 0); a boss stopped before that keeps them.
- The end (every unit stopped, or the clock): `chase.party.end {success, seconds, rows [{name, level, exp, mito, levelUps}], driver}`:
  every member still in it is paid by `economy.grantChase` (the chase formula, the Mita drink, `rewardExtras` with the
  party bonus 0x4fe050). `chase.party.quit` drops a member unpaid.
- Client: the leader's Tab on the call sends the start (`ChaseMission.StartFromCall`); `MissionNet` hooks the session's
  NetClient.Message and routes `chase.party.*` to `ChaseMission.OnPartyPush`; hits are batched (4 a second); the result
  shows my line in the chat and PARTYCHASERESULT with every line.

## 4. Rush Time [exact screens, rebuild event]

| state (0x4e2270 +0x58) | client |
|---|---|
| 3 "Waiting Rush Time" | RUSHWAIT 2910000 (0x562ec0): the boss image HUVIMG01/03/04/07/08 matched by name, name and description; while the zone lacks drivers WAIT_INFO "%2u/%2u" (net+600 >> 13 = the minimum) under WAITCOUNT 11136 "Participant" and 11138; with enough drivers TIME "%02u:%02u" under FREETIME 11135 "Time Left"; 11139 once one runs; title 11137 "Rush Time Notice" |
| 2 "Rush Time" | RUSHINFO 2900000 (0x5629e0 on 0x2001, digit groups 0x5304a0 / 0x531eb0): the time m m " s s . t t t on HOURPOS_10 .. SECONDPOS_0, the HUV count (HUVCOUNT_POS), the drivers (DRIVERCOUNTPOS_100/10/1), HUVHP_GAUGE scaled by the health fraction; ESC asks 10566 "Abandon Rush Time?" -> packet 0x79ba90, UI effect 9 |
| 4 result | within 6 s of the end: RUSHRESULT 2920000 (0x562800 init, ui+0x34c): TITLE "Boss HUV Lucky Driver", TEAM "%s %s" (the lucky driver), TEXT 10893 "3 Mittron Enhancers, %d Mito."; 10994 / 11151 when it is me |

Also: `game\cylinder_rushtime.nif`, ID_MINIMAP_ICON_RUSHTIME, 11121 (no Auto-Drive), 11145 / 11146 (Virtual HUV / Undercity
invitations refused during Rush Time), server switch `City.RushTime`.

Rebuild, online (server `src/huvevents.js`, the switch City.RushTime's event, with real drivers) [rebuild]: per city one
is announced every 900 s (the first 240 s after the city got its first driver; GM / tests `rush.gm {seconds, spot, ai}`):
push `rush.notice {id, spot, boss, seconds, min}` to the city's drivers - the zone is chasespawnlist point `spot % count`
(radius 150 m), the boss index `boss`. The clients report `rush.zone {id, in}`; push `rush.wait {drivers, min, seconds}`;
AI stand-ins drift into the zone during the 120 s (a quiet server still gets its event); the minimum is 3 drivers. At the
start every driver still in the zone and free joins (`rush.start {hp, max, seconds, owner, members, drivers}`; the others
get `rush.closed`): health 400 + 80 per driver, 300 s; the first driver in the zone runs the boss (party HUV) and sends
`rush.huv {cars}`, relayed; hits `rush.hit {damage, hits}` (five 45-damage hits a second per driver), the stand-ins ram
every 2.5-4.5 s for 5-12; push `rush.hp {hp, max, by, drivers}`, `rush.owner` when the owner leaves. The hit that stops it
names the lucky driver: `rush.end {success, lucky, prize, rows, driver}`, every driver still in it paid by economy.js's
rush rule for their hits (+ the drink, `rewardExtras`), the lucky one + 5000 Mito and three Mittron Enhancers; `rush.leave`
(RUSHINFO ESC) drops out unpaid. During Rush Time (waiting in the zone or running) an Undercity invite is refused at once
(11146: client `UndercityParty`, server `party.uc.autorefuse`) and a V-HUV registration too (10967).
Offline (`RushTime.cs`): one every 900 s (the first after 240 s) at a chase spawn point 300-1500 m away, AI drivers drift
in, the same health, clock and AI hits; the hit that stops it names the lucky driver. Pay (economy.js `rush`): 200 + 40 x
level + 10 per hit (40 at most) EXP, 2000 + 300 x level Mito, the lucky driver + 5000 Mito and three Mittron Enhancers
(i_n_00003); at least 20 s, cooldown 120 s.

## 5. The Virtual HUV [exact screens, rebuild match]

V-HUV 4:4 / 8:8: players are made the "virtual HUV" (10971 "You've been selected to be the HUV") and the other team
crashes them. Registration: QUESTDIALOG's VC44 / VC88, 10940 / 10941 / 10945, 10942, the waiting counts VIRTUAL 3830000
(WAIT44_TEXT / WAIT88_TEXT, 11003); the party notice 3820000 (TEXT "Virtual HUV Battle Party.", OK; REQUESTINVITE mode 1
for packet 0x11c). The match: VIRTUALCHASEPLAYINFO 900000 (0x556c30, the time like CHASEPLAYINFO) and VIRTUALCHASING
910000 (0x547d10): five distance digits + "m" (LENGTH0) in the white art within 500 m, red art (RED_NUMPOS, LENGTH1)
beyond, the NEXT_HUV / MY_HUV marker; minimap VIRTUAL_CHASING circles. The end: 10973 / 10974, VIRTUAL 3810000 (0x548350:
RESULT_EXP / RESULT_MITO, ITEM0-3 or SLOTX, OK), VC 3840000 (team points, not used). Refusals: 10944, 10948, 10965, 10966,
10967, 11109, 11131, 11132. 10961: ten 8:8 wins give a coupon.

Rebuild, online (server `src/huvevents.js`, real drivers) [rebuild]: `vhuv.register {size}` (10942; refused while busy
10966, in a Rush Time zone 10967), push `vhuv.wait {size, waiting}` (3830000); the match starts when both teams are full of
real drivers or 12 s after the first registration, AI stand-ins filling the rest; drivers split White / Red in registration
order and moved into a private channel of the city (index 200 + match, so they see each other's cars); `vhuv.start {team,
members, huv: {white, red, whiteAi, redAi}, seconds, owner}`. Each team has one virtual HUV at a time - a random member
first; a driver made the HUV gets 10971 and MY_HUV (their own car is the HUV the other team crashes into), the others see
NEXT_HUV; a stand-in's HUV car is run by the match's first driver (`vhuv.huv {cars: [white, red]}`, relayed). The role
passes to the team's next member after 3 crashes taken or 45 s (`vhuv.huv.next`). `vhuv.crash {target}`: the reporter's
team scores when the target is the other team's HUV, one crash per 1.5 s per driver, within 25 m of the target's last
known position (a driver's car, or the stand-in HUV the owner sent); the client reports touching a driver HUV (within
5.5 m) or ramming a stand-in's car; stand-ins score every 9-16 s; push `vhuv.score {white, red, by, target}`. After 180 s
(GM / tests `vhuv.gm {seconds, huv}`) more crashes wins: `vhuv.end {won, white, red, rows, driver}`, every driver paid by
economy.js's vhuv rule (x 1.5 for the winners, + extras) and moved back to their channel; a driver who leaves is replaced by
a stand-in (unpaid).
Offline (`VirtualChase.cs`): `VirtualChase.Register(4|8)`; AI drivers fill both teams in 12 s, the notice shows 3 s, then
a 180 s match: the Red team's virtual HUV is an AI car the player rams (a crash at most every 1.5 s), the AI drivers of both
teams score every 9-16 s; more crashes wins. The player is never made the HUV offline. Pay (economy.js `vhuv`):
300 + 50 x level + 30 per crash (30 at most) EXP, 2000 + 250 x level Mito, x 1.5 for the winners; at least 60 s, 4 or 8.

## 6. Auto-Drive and the AUTOFINDER quiz

AUTODRIVING 550000 (0x59b370 / 0x59b3a0): "Auto-Drive In Progress" and STOP -> 11072 (0x59b2c0); 11073 on arrival;
refusals in the item use dispatcher 0x5d52d0 (11103 dome, 11104 battle zone, 11108 Undercity, 11109 / 11131 / 11132 V-HUV,
11110, 11116 fuel, 11117 mission, 11118 / 11119 delivery, 11120 patrol, 11121 Rush Time, 11122 party, 11124 station, 11397
city 5); 11112 "Please use the "Go" button". Rebuild (`AutoDrive.cs`): `AutoDrive.Start(destination)` plans the shortest way
over the lane graph (Dijkstra) and drives it (pure pursuit, 70 km/h, slower in bends), giving the car back on arrival
(25 m), STOP or 8 s stuck. The map's Go button is not wired (hook below).

AUTOFINDER 3420000 (0x681050 init, 0x681150 show, 0x680f50 clock, 0x680ea0 answer) is the server's anti-macro quiz: a
random question of four answers from a table (ui+0x150, 0x104-byte records), a random pattern and position, a 30 s
clock ("%d %s" 10555), the answer to the server (0x799cf0), 10556 "Correct." / 10557 "Wrong.". Not rebuilt.

## 7. CHARGER / CHARGER4GATE / CHARGERPVP

940000 / 950000 / 960000 (0x54f9f0, 0x550120, 0x550870, 0x550e00, 0x550230, 0x550f20, 0x5c2a10, 0x5c2c30, 0x5c3030) are
the Mittron fuel chargers (S / L / XL / XXL charger items, "미트론사용" = use Mittron; 0x5c2a10 spends chargers for an amount),
not a chase mode. Not rebuilt here.

## 8. The ten chase functions studied

- 0x54f9f0, 0x550870, 0x550f20, 0x550230: the CHARGER / CHARGER4GATE dialogs (section 7).
- 0x556780: CHASEPLAYINFO draw; 0x556c30: VIRTUALCHASEPLAYINFO draw.
- 0x562ec0: RUSHWAIT; 0x5c2c30: CHARGERPVP show (counts the charger stacks by size).
- 0x4b95c0 / 0x4b9920 (and 0x4b8b90 / 0x4b8d70 / 0x4b8f50): std::map / std::set code (red-black tree erase and insert:
  "invalid map/set<T> iterator", "map/set<T> too long") instantiated for maps keyed by car records (`+0x1aa9` is the
  node's nil flag) - library code, nothing to convert.
- 0x6f7c10 (CHASEINFO / CHASING setup), 0x6f7d80 (chase frame), 0x5539f0 (PARTYCHASERESULT), 0x4f9dcd (result packet),
  0x4f2500 (chase start), 0x562bb0 / 0x5629e0 / 0x562800 (Rush Time dialogs), 0x547d10 (VIRTUALCHASING / VC init) as above.

## 9. SPEEDCOMBO's party digits [exact]

A member's combo packet (handler 0x4ed280, 0x4ed43c) calls `AddCombo(type, 1, sendParty 0, userId, fromParty 1)` on the
local car: +1 to the local chain, its timer reset, and `HudCombo.Show(count, type, party = 1)` (0x563990) shows PARTYCOMBO +
PARTYIMG instead of COMBO and draws the count with the PARTYNUM0-9 art (0x5646c0). So PARTYNUM shows the local chain right
after a member's trick; the party total (net+0x3b614) is the party bar's COMBOCOUNT. Rebuild (`DriftCombo.AddFromParty`):
the server's `party.combo` push carries `rise` (the tricks the member chained); MissionNet adds them to the shown chain
(`DisplayCount`) with the party labels and digits. `Count` stays the driver's own chain, so the count PartyWindow sends never
echoes the members' tricks back.

## 10. Left

- Team ROO's banners (beat_success / beat_fail): `StepQuestRunner.RooChaseBanner` plays them for the ROO leader arrests
  (MISSIONS.md sections 11-12) once ChaseMission carries the `TeamRoo` flag (hook line pending).
- VC 3840000's team points, the V-HUV coupons (10961), the V-HUV party notice (3820000 / REQUESTINVITE mode 1) online.
- The solo chase reward still follows the client's claim (kind chase): the server does not yet check it against its
  `chase.begin` record (economy.js is not this module's).
- AUTOFINDER, the chargers.
