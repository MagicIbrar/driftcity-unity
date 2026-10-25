# Crews ("teams") and crew battles ("Team Center" / Channel Battle) - DriftCity.exe

Read-only RE notes. [exact] = read from code/data, [inferred] = interpretation. Texts are globaltext keys (US column).
Decompiled sources: `DriftCity_RE\decompiled\phys\0xADDR.c`; annotated copies and asm dumps in this folder
(`a_*.c`, `h_*.c`, `x_*.c`, `asm_*.txt`). Several handlers do not decompile through the bridge (not Ghidra function
starts / static-init guard: 0x67d3a0, 0x58a070, 0x6117b0, 0x6747f0, 0x567730, 0x567d40, most 0x50cxxx..0x50exxx
handlers) - read as asm (`asm_crewhandlers.txt`, `asm_67d3a0.txt`, `asm_615170.txt`, `asm_6117b0.txt`).
Tools written here: `tools/dlgscan.py` (which functions touch which dialog's control ids), `tools/txref.py` (text key ->
referencing function / handler opcode), `tools/senders.py` (client sender -> opcode/size), `tools/hnd.py`, `tools/d.sh`.

"TC" = **Team Center**, a zone in the Driver Dome (parking lot): mesh `teamcenter` = zone 5 (PARKINGLOT.md 0x763840).
"Crew" is the US word for the Korean 팀 (team); code/control names say TEAM.

## 0. Objects and data

- **TC UI object** = game `+0x160 -> +0x2b8`. Binder 0x67d3a0 registers the dialog handlers [exact]:
  590000 MAIN -> 0x67a0b0, 2020000 TEAMMAKE -> 0x6762d0, 2030000 TEAMOK -> 0x674d50, 2040000 TEAMDESTROY -> 0x676840,
  2050000 TEAMDESTROYCANCEL -> 0x676920, 2060000/2070000/2090000/2100000/2110000 (notice/semi/selch-tab/channel notice/
  channel info) -> 0x676a50, 2130000/2140000 ATK lobby + ATKCOVER -> 0x67a420, 2150000 DEF lobby -> 0x67a7e0,
  2220000 BATTLEITEMGET -> 0x67cf00, 2230000 BATTLEITEMMISS -> 0x67cff0, 2240000 CHANNELSETUP -> 0x67a390,
  2250000 CH_MAPNOTIFY -> 0x6771d0. Results/viewing objects: 2180000-2210000 inited by 0x6747f0 (asm), filled by
  0x673eb0 / 0x674290; 1420000 ROUNDNUMBER 0x673aa0; viewing 1970000 / cheering 2170000 0x75c580 (init), 0x758410
  (camera buttons), 0x75b980 (cheering); replay 3410000 0x567730 / 0x567d40.
- **MY crew page** 3980000 lives in the social "My" object (game `+0x160 -> +0x1f0`, binder 0x6117b0): handler
  0x6153xx (inside the 0x615170 block), tab switch 0x615170(mode), member list 0x614090 / 0x614d30, open 0x606440,
  rank popup 2790000 0x606500, crew mark / rename 3990000 0x6107c0.
- **Character crew fields** (char = 0x6cb0c0/0x6cb1a0) [exact]: `+0x4e` int64 crew id (-1 = none), `+0x56` int64 crew mark
  id/version, `+0x5e` crew name (13 wchar), `+0x78` my rank (1 Master, 2 1st Class, 3 2nd Class, -1 none),
  `+0xd94` 0x178-byte **crew record** copy, `+0xf08` rename-used flag (0 = rename still allowed; set -1 by notice 10),
  `+0xf0c` time of the last crew-info refresh.
- **Crew record** (0x178 bytes, server packet body copied as is to char+0xd94) [exact offsets from use]:
  `+0x00` int64 crew id, `+0x10` crew name, `+0x120` master name, `+0xc9` close (delete) time (time_t),
  `+0xd1` 24 bytes owned-channel flags (channels 1..24), `+0xe9` state char ('C' = closing / delete pending),
  `+0xf0` int points A, `+0xf4` int total channel captures (10340), `+0xf8` member count, `+0xfc` int64 points B.
  Crew points (TEAMPOINT) = `+0xf0 + (+0xfc)/10`. Crew grade 0x514530 [exact]: **S** if points >= 500000 or captures > 0,
  **A** >= 100000, **B** >= 20000, else **C** (letters at 0x871d88 "CBAS", colours 0x871bc8). Shown as "Crew Rank"
  (10563); server needs rank B+ to attack (20076).
- Other players' crew: car object `+0x1c20 -> {+0x2c crew id, +0x34 mark, +0x3c name}` (refreshed by the crew packets);
  name tag 1950000 shows NAMETEAM_TEAMNAME + LADDERMARK.

## 1. Packets [exact opcodes, flow partly inferred]

Server -> client (handlers.txt):

| op | handler | meaning |
|---|---|---|
| 0x349 | 0x50cb20 | my crew info: `+2` ctx, `+0xe` rank -> char+0x78, `+0x12` 0x178 record, `+0x18a` word mark-cache version -> net+0x3b3ee. ctx 0x3eb -> stage 8 + open TC main (0x6756e0); 0x3ec -> 0x54b450; 0x3f3 -> MY page refresh |
| 0x34b | 0x50d7a0 | crew created: record `+6`, rank = 1, name `+0x16`, new Mito int64 `+0x180`; TEAMOK 2030000 (0x6774e0); chat 10813 |
| 0x34d / 0x356 | 0x50cd10 | crew record update (`+6`, 0x178 bytes) |
| 0x34f | 0x50d990 | join result `+2`==1: record copied, rank = 3, 10016; else 10017 |
| 0x351 | 0x50db10 | leave result `+2`==1: 10018, crew fields cleared; else 10019 |
| 0x354 | 0x50cd40 | crew name check: `+2`==1 -> 10328 available (TEAMMAKE / rename both), else 10329 |
| 0x365 | 0x50cd90 | URL check: 1 -> 10636 (TC+0x1f3c = 1), else 10637 |
| 0x33f | 0x50ce40 | crew mark changed: id `+6`, new mark int64 `+0xe`, cache version `+0x1a` |
| 0x341 | 0x50dd70 | crew mark image: crew `+6`, mark `+0xe`, size `+0x16`, bytes `+0x1a` -> mark cache 0x441fa0 |
| 0x343 | 0x50cea0 | mark upload result (fail -> 10634?) |
| 0x358 | 0x50dc70 | member list: ctx `+2` (0x3f1 battle setup 0x67d040, 0x3f2 / 0x3f4 MY page 0x614d30), count `+0x1a`, 0x4c-byte entries |
| 0x35b | 0x50ccc0 | ctx 0x3f0 -> 0x54c6d0 (crew info popup of another player) |
| 0x35d | 0x50df20 | **channel table**: 0x134-byte entries (channel index short 0..23, max level byte, max car grade byte, owner crew 0x75 bytes, second crew 0x75 bytes, 4 time_t (t1..t4), 2 shorts, 0x32-byte name) -> TC 0x678c70 + SELGATE 0x689b80; MY page 0x606130 |
| 0x35f | 0x50d610 | next channel-battle time -> TC+0xb8 |
| 0x367 | 0x50cf00 | (to master) join request from `+0x16`: Yes/No box 10020 -> 0x368 approve / 0x36c deny (callback 0x50ca40) |
| 0x369 | 0x50d4d0 | (to master) leave request 10022 -> 0x36a (callback 0x50cac0) |
| 0x36c | 0x50d020 | join denied 10021 (`+0x36` name) |
| 0x36b | 0x50dde0 | I was kicked: 10023, crew fields cleared, popup if `+0x46`==1 |
| 0x36d | 0x50d0f0 | crew notice `+2` type, `+6` crew name, `+0x20` player: 1 delete requested 10814*, 2 delete cancelled 10815*, 3 deleted 10816*, 4 joined 10817, 5 left 10818, 6 kicked 10819, 7 new leader 10820*, 8 promoted (`+0x4e` 2 -> 10838 1st Class, 3 -> 10839 2nd Class), 9 deletion countdown `+0x4a` s -> 10821 (days+hours) / 10822 (hours), 10 renamed 11305 (char+0xf08 = -1), 11 member logged on 11413 (only with option DAT_008d28f8 = "FriendTeamNotify"). * = also a popup |
| 0x359 | 0x50d5d0 | my rank := 2 (after handing over Master) + MY refresh |
| 0x386 | 0x50d640 | battle state: channel `+0xe`, def name `+0x12` / ids `+0x2c`, atk name `+0x3c` / ids `+0x56`, 4 round flags `+0x66..` -> lobby round text 10343 Waiting / 10344 Round 1 / 10345 Round 2 / 10346 Final Round (0x678fb0, 0x67b720) |
| 0x38f | 0x50d6a0 | channel maps (2 x track + traffic) -> CHANNELSETUP (ctx 0x3f1) or MAPNOTIFY (0x6780c0) |
| 0x390 | 0x50dfb0 | battle history (0x6a entries): type 0 = 10348/10349/10350 match start, 1 = 10351 round won (0x679040) |
| 0x393 | 0x50d6c0 | channel reward: if `+6` is me, Mito += int64 `+0x38`; box 10552 (master `+0xe`) |
| 0x388 | 0x50dfe0 | live spectate list (0x565cd0, SELCH_VIEWING 3390000: `+6` count, 0x10-byte `{ch, ?, ?, viewers}`) [corrected] |
| 0x391 / 0x392 | 0x50e030 / 0x50e060 | part stakes: BATTLEITEMGET 2220000 (0x680aa0) / BATTLEITEMMISS 2230000 (0x680b20): `+6` append, `+0xa` count, 100-byte part records (0x38d same format, ignored) [corrected] |
| 0x396 | 0x50ac40 | replay list 3410000 (0xec-byte entries, "%d %d %I64d %I64d %31s %31s") [corrected; request 0x395] |
| 0x384 | 0x50d680 | game+0x5c +0x18 = value |

Client -> server (sender -> op, size) [exact]: 0x79d2d0 0x348 (6, ctx) crew info; 0x79d360 **0x34a** (0x17a) create
{0x178 body: mark index int64, URL (0x21 char), name 13 wchar}; 0x79d3f0 0x34c (0xa, crew id) delete request;
0x79d480 0x355 delete cancel; 0x79d510 0x350 leave; 0x79d5a0 0x353 name check; 0x79d640 0x364 URL check;
0x79d700 0x357 (0x12: ctx, crew id, channel) member list; 0x79d7b0 0x352 (0x3c: crew, member id, name) hand over
Master; 0x79d870 0x33e mark upload; 0x79d930 0x366 join by crew name (chat "/Join Crew"); 0x79d9e0 0x367 join request
via a player; 0x79da80 0x368 approve join; 0x79db20 0x36c deny join; 0x79dc00 0x36a approve leave; 0x79dca0 0x36b kick;
0x79dd40 0x374 (member, rank 2/3, name) set rank; 0x79de00 **0xce4** rename; 0x79dec0 0x35e schedule request;
0x79df50 0x35c (ctx) channel table; 0x79dfe0 0x387 spectate list; 0x79e070 0x372 / 0x79e100 0x373 subscribe /
unsubscribe a channel's info; 0x79e180 0x370 (0x10) enter channel as Master/1st Class; 0x79e230 0x371 (0x1c) enter as
2nd Class; 0x79e2f0 0x384 (channel, 1/0) attack ready / cancel; 0x79e380 0x38a (0x4c) channel map setup;
0x79e4a0 0x38e (ctx, channel) map info request; 0x79e5c0 0x394 cheer; 0x79e530 0x38b / 0x79e670 0x395 / 0x79e700
0x397 replay-ghost related [inferred].

## 2. Creation (TC main 590000 -> 2020000) 

- Reached by driving into the Driver Dome `teamcenter` zone (stage 8 = team center; 0x763d11: if the crew info is older
  than 60 s it first asks 0x348 ctx 0x3eb, the 0x349 reply opens it). Opener 0x6756e0 [exact]: NPC **"Operator Erina"**
  (10522, model `ch_m_0009_01`), hover texts 10532 + 10533 / 10534 / 10535 for the three buttons; requests 0x35e.
- MAIN buttons (0x67a0b0) [exact]: TEAMMAKEDESTROY (create or delete; enabled only with no crew or as Master; server
  switch net+0x27c bit 1 = `TeamCenter.CreateDestroy`, else 10745), NOTICECHALLENGE (channel battle; bit 2
  `TeamCenter.ChannelBattle`; asks 0x35c ctx 0x3ed), VIEWING (spectate/replays; bit 4 `TeamCenter.ChannelBattleView`;
  0x35c 0x3ed + 0x387 0x3ee), ESC, 0x900bb (gate select: SELGATE +0x748 = 1, packet 0x30c).
- 0x6775f0 picks the dialog: no crew -> TEAMMAKE; Master with record state != 'C' -> TEAMDESTROY; 'C' -> TEAMDESTROYCANCEL.
- **Conditions** [exact]: config `localize\globalcontent.xlt` (read by 0x7d27b0 per service column) `TEAMMAKE_NEEDMITO`
  (KR 2500, **US 2500**, JP/TW/INI/TH/EU 10000) and `TEAMMAKE_NEEDLEVEL` (20 everywhere) -> 10313 / 10314 and the
  MITOCOND / LEVELCOND marks 10624 "(OK)" / 10625 "(Impossible)"; MYMITO 10338, MYLEVEL 10339. The client does not block
  on them (server: 20163 level 20, 20164 "10000 Mito is required", 20224 cooldown hours). Shipped
  `Data\aggrement_teammake.txt` says level 20 and 10,000 Mito.
- **Agreement**: scroll box with `Data\aggrement_teammake.txt` (10315 if missing); AGREEMENTCHECK refuses (10325) until the
  text was scrolled to the end. MAKE validation 0x6753f0: agreed (10330), URL checked (only service 0 = KR, 10634),
  name checked (10331), one of **10 built-in marks MARK1..MARK10** chosen (10332) - images
  `Data\GameUI\teammark\00..10` (32x32 24-bit BMP; 10312 says it can be changed later in My Crew).
- **Name** 0x74a680/0x74a740: 4..12 characters (10602), letters/digits/'-'/'_' (KR/JP double-byte ranges in those
  services) (10603), word filter (10607), then 0x353 -> 0x354 reply. **URL** (KR only; row hidden elsewhere): <= 16
  chars (10630), alphanumeric (10631), filter (10632), 0x364 -> 0x365.
- **TEAMOK 2030000** (0x6774e0): 10333 "%s's %s Crew has been" (master, crew) / 10334 / 10335, TEAMMASTER, TEAMNAME,
  TEAMLEVEL (grade letter), TEAMDATE = today "%04u.%02u.%02u" (client clock).

## 3. Members, ranks, management

- Ranks [exact]: 1 = **Master** (10385), 2 = **1st Class** (10386), 3 = **2nd Class** (10387). Creator = 1; joiner = 3.
  No client member cap (server 20171 "maximum number of members").
- **Join**: click another player in the world -> 0x367 with the target's crew id (10163; 10164 if that player has no
  crew) or chat "/Join Crew <crew>" (0x366). Master gets 10020 Yes/No (0x368 / 0x36c); requester gets 0x34f or 0x36c
  (10021). Server: master must be online (20199), 24 h after a kick (20242), one crew per account (20262).
- **Leave**: MY TAB_WITHDRAW: Master -> 10378; others 10379 confirm -> 0x350 (also "/Leave Crew"). Server may ask the
  Master (0x369 10022 -> 0x36a) [inferred], result 0x351.
- **Kick** (MY BAN): 10388 self, 10389 confirm -> 0x36b {crew id, name}; "/Crew forfeit <name>" too.
- **Rank popup 2790000** (UPDATE; target Master -> 10384): TEAMPOS1 = 1st Class, TEAMPOS2 = 2nd Class, TEAMPOS3 = make
  Master. OK: pos1/pos2 -> 0x374 rank 2/3; pos3 -> needs target level >= 20 (**hardcoded 0x14**, 10849) and online
  (10869) -> 0x352; then the list is re-requested (0x357 0x3f2). Old Master gets 0x359 (rank 2) [exact].
- **Disband**: TEAMDESTROY 2040000 shows crew name, member count, owned channels ("1, 2..." or 10523 "None"), points,
  grade, 10340 captures, ladder grade/rank (11332/11333, only with Main.LadderDisplay); agreement
  `aggrement_teamdestroy.txt` (Master loses 50 % of current EXP; channels become unowned); OK -> 10336 -> 0x34c.
  Record state 'C' -> TEAMDESTROYCANCEL 2050000: 10341 "%s Crew is applying for deletion", 10740 closing date (record
  +0xc9), 10741 remaining days/hours, 10742; OK -> 10337 -> 0x355. Notices 0x36d type 1/2/3/9. Grace = 7 days (10317,
  agreement text; server side).
- **Rename** (3990000 extra controls 3990009..12, title 11304): only if server switch `Etc.ChangeTeamName`
  (net+0x28c & 0x4000) and char+0xf08 == 0; same name checks -> 0x353, then 0xce4 {crew, char, name}; notice 11305.
- **Crew mark** 3990000: Master only (10383); LOADFILE opens a file dialog (filter 10380 "32x32 Crew mark image"), file
  <= 4000 bytes (10381) and decodable (10382); OK -> 0x33e; BEFORE/AFTER previews. Server `Main.TeamMark` switch.
- **Chat**: "/crew <text>" (10750) sends on scope `team` (0x5bcf60); system lines use colour 0xffff99ff "client".
- **MY 3980000** [exact]: tabs BOARD (web board `[%s]&teamid=%I64d&boardtype=C`, `Main.TeamBoard`), MEMBER (mode 1),
  WITHDRAW (leave), SENDNOTE (note to all members), MEMBERMANAGE (mode 4), TEAMMARK (mode 3); header TEAMNAME, MARK_POS,
  MEMBERCOUNT, TEAMPOINT, TEAMRECORD (grade), LADDER class/rank, TEAMURL, 24 OWNCHANNEL / CREATE_CHANNEL icons,
  CHANNEL / SEMI_CHANNEL toggles. Member slots: 6 per page (BG, OFFLINE overlay, 3D avatar, LEVEL, NAME, TEAMPOS,
  DATE "%04d.%02d.%02d" of member +0x54), PAGE00..09 + LEFT/RIGHT (+-10 pages). Mode 1 shows DETAILVIEW for everyone
  but me (open the player's info; offline -> 10725); mode 4 shows UPDATE + BAN for non-Masters. SEARCH /
  SEARCHTEXT filters by name substring (wcsstr); FILTER_TEAMPOS/NAME(2) toggle sort rank asc/desc, name asc/desc.
- Other places: friend window 2930000 RACINGTEAMLIST tab (car grade, name, crew, location) and crew name + mark
  columns; character info 0x4e0430 "My Crew Name" / "My Crew Rank"; 1950000 name tag; SELGATE crew-owned channel panels.
  **2010000 is ID_PVP_GHOSTEXIT** (spectator exit), not crew info.

## 4. Channel battles

- 24 channel slots in the table, used as **10 major channels** (index 0..9, "Channel n", MAJOR tab, 2060000 rows) and
  **10 semi channels** (index 10..19, "Semi n", 2070000 rows); JP-type service 2 shows semi 6..10 as 11391 "Event %d
  Channel" [exact 0x676a50 / 0x679560]. Switches `Main.NormalCh` / `Main.SemiCh` (net+0x248 bits 23/24).
- **Ownership**: each channel has an owning crew (record at TC+0xc0 + 0x75*i: id, mark, name +0x1d, member count,
  master, points); shown on gate selection and at the channel's **Mittron Filling Station** ("Owned by: %s Crew"
  10168, 0x54fec0); the owner master is paid the station fees (20268 / 20288) and replay fees (20350).
- **Schedule** [exact 0x675ea0]: TC+0xb8 = next battle start: "The next Channel Battle starts in %d days ..." (10319)
  before, "Channel Battle in progress." (10320) for **2 h** (7200 s) after. ENTERCHANNEL is visible only between the
  channel's t2 and t4 (0x679560).
- **Who may enter** (0x675250 / 0x676a50): level <= channel max level byte, car grade <= max grade byte, car grade >=
  `MinChBattleGrade` (major) / `MinSemi%dChBattleGrade` (semi n) (US 0, INI/EU 6 / 3,4,5,6,3). Master/1st Class ->
  0x370, 2nd Class -> 0x371. Major channels 0-1 when not owner and service != KR: 10553 warning (a lost attack costs
  an equipped part). Server: Challenge Coupon `i_n_00037` (20077), crew grade B+ (20076), Master/1st Class starts
  the attack (20075/20081), one try per channel (20079/80, 10372), equal team sizes (20184).
- **Lobbies**: ATK 2130000 / DEF 2150000 are a PvP wait room: 8 slots per side (**defender = even slots, attacker = odd
  slots** - MEMBERCLOSE_n -> 0x678b00(slot) kick with 10240 / 10571), ready list of up to 11 / 10 crews with marks
  (READYn_1/2, RESERVETEAMCOUNT), REMAINTIME, HISTORY (0x390), VIEWING, ATKCOVER_READY -> 0x384(ch, 1), CANCEL ->
  0x384(ch, 0). Leaving: attacker 10326 (crew under 3 waiting members drops from the list), defender 10327 (no waiting
  members = lose the channel).
- **Map setup** (owner Master, major tab, within 24 h after t1): CHANNELSETUP 2240000 picks 2 maps (city tab, track,
  random/reverse, traffic IDLE/NORMAL/JAM/RANDOM -> 0/1/2/rand%3) -> 0x38a; others see MAPNOTIFY 2250000 with 10342
  "%s Crew is defending Channel %d." (0x6780c0, 0x675bb0). Server rejects outside the window (20086).
- **Race** [exact]: the normal PvP room/race manager (game+0x170) with **room flag 0x8** (crew battle) on top of team
  mode; team bit `entry[0x1e] & 1` (0 = defender, 1 = attacker), results from 0x1ec type 1 (0xc4-byte client entries).
  0x724120: flag 8 -> TC wait-room screen 0x67ac00, results 0x673eb0 (RESULT1 16 rows: rank, DEF/ATK background,
  time "%02d:%02d:%02d" or DNF distance 10215/10216, name, **POINT = entry+0x7c**) then 0x674290 (RESULT2).
  The winning side comes from the room info (`+0x74`); the client does not compute it.
- **Rounds** [exact text logic 0x674290, param = attacker's wins so far]: the attacker must win **3 rounds in a row**
  (Round 1, Round 2, Final): attacker loses any round -> 10371 + 10372 (out; 10364 tells the defender the next
  challenger crew is waiting); attacker wins round 1/2 -> 10373 + 10374/10375 (defender sees 10365 + 10366/10367);
  attacker wins the final -> 10362 + 10376/10377 "ownership of Channel %d has become yours" (defender 10368 + 10369 +
  10370). RESULT2 shows PERFECT when every winner placed ahead of every loser (max winner rank < min loser rank),
  else WIN; LOSE for the loser. History lines 10350/10351 call it "Round %d of the Tournament %d in Channel %d".
- **Rewards**: owning the channel (station + replay fees), 0x393 Mito to the master (10552 "%I64d Mito"), crew points /
  captures (record +0xf0/+0xf4/+0xfc, server-side), crew grade S after any capture; BATTLEITEMGET 2220000 (12 part
  slots, tooltips 10352-10360 via 0x67bcd0) / BATTLEITEMMISS 2230000 (a lost part) [inferred: part stakes of
  channels 0-1].
- **Spectating** 1970000 (stage+0x6c0 = 18): crew names/marks, DEF/ATK team points, ROUNDTEXT, REMAINTIME, viewer
  count, 16-player list, 6 cameras. **Cheering** 2170000: uses a "Mittron Fireworks" item (`i_n_00011`) on the watched
  racer -> 0x394, 5 s cooldown. **Replays** 3410000: 8 rows (owner vs challenger names/marks, play count, Mito price);
  Ghost Battle with `ch_ghost_NN` tickets (Ch 01-10, Semi 01-10).

## 4b. Corrections from the second pass (2026-10)

- The "ready crews" list of the lobbies and CHANNELINFO is the channel's Battle Zone room list (op 0x1d0, room title = crew name),
  not a crew packet; the defender's client sends 0x384(ch, 1) by itself after 0x370; 0x386 is `{prepEnd, roundEnd, ?, channel, def
  name/id/mark, atk name/id/mark, 4 round flags}`; ATKCOVER_READY blinks 0.4 s / 0.8 s. Full spec in the RE notes of this round
  (SOCIAL.md section 6 sums it up).

## 5. Minimal remake (JSON server) [recommendation]

Per crew: `id, name (4-12, [A-Za-z0-9_-]), nameChanged (bool), url (optional), mark (0..10 built-in or uploaded
32x32 BMP <= 4000 B + version), masterId, created, state ("" / "closing" + closeAt = request + 7 d), pointsA, pointsB
(points = A + B/10), captures, ownedChannels[] (0..19), members[]`.
Per member: `charId, name, level, rank (1/2/3), joined (time_t), online` (offline flag drives the OFFLINE overlay).
Per player: `crewId, crewRank, kickedAt / leftAt` (24 h rejoin lock), rename flag.
Rules to enforce (client texts exist for all): create = level >= TEAMMAKE_NEEDLEVEL (20) and Mito >= TEAMMAKE_NEEDMITO
(pick the column the remake uses; data text says 10000, US column 2500), name unique, not in a crew; join = master
online approves; Master cannot leave; kick/rank/mark/rename/delete = Master only; hand-over target level >= 20 and
online; delete = 7-day cancellable grace, then channels released; one rename.
Messages: `crew.info`, `crew.create {name, mark, url}`, `crew.checkName`, `crew.join {crew | viaPlayer}`,
`crew.answer {requestId, yes}`, `crew.leave`, `crew.kick {name}`, `crew.rank {charId, rank}`, `crew.master {charId}`,
`crew.mark {bmp}`, `crew.rename`, `crew.delete`, `crew.undelete`, `crew.members {page}`; push `crew.notice {type 1..11,
crew, player, secs}` mirroring 0x36d.
Channel battle (only if wanted): per channel `index, kind (major/semi), ownerCrewId, maps[2] {track, traffic},
nextStart, windowSecs 7200, minGrade, maxGrade, maxLevel, queue [crewIds]`; a battle = the existing Battle Zone team
room (16 slots, defender even / attacker odd, flag "crew") with equal sides (>= 3), 3 rounds; per round the team with
more points (place p of n -> n - p + 1, DNF 0, as the remake's team rule) wins; attacker eliminated on its first loss,
takes the channel after 3 wins; then the next queued crew attacks. Result push = existing `race.results` + `{round,
attackerWins, winnerSide, perfect, ownershipChanged}` so the client shows RESULT2 texts 10361-10377.
