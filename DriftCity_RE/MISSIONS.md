# Station missions: Delivery Service, instant missions, patrols

What DriftCity.exe does for the Delivery Service ("ARBEIT", later called Quick Service), the instant missions
("INSTANTMISSION", "Call Mission") and the patrols ("PATROL", "PARTYPATROL"), and how the Unity rebuild plays them offline
(`Runtime/Game/DeliveryService.cs`, `InstantMissions.cs`, `PatrolMission.cs`, shared parts in `MissionCommon.cs`), the Mita 500
drink (`MitaDrink.cs`), the quest bar (`QuestTracker.cs` QuestPlayInfo) and the quest reward screens, the QuestClient.tdf step
quests (`StepQuests.cs`, `StepQuestRunner.cs`), the factions (`Factions` in `StepQuests.cs`) and the delivery rankings
(`QsRankList.cs`), the unlocks and the beginner panel (section 14). The HUV chases, Rush Time, the Virtual HUV and Auto-Drive are in CHASES.md.

**[exact]** = read from the exe or the data. **[inferred]** = the meaning follows from the data or the client's use but is not
proven. **[rebuild]** = the online server decided it, and the value is this rebuild's own.

Decompiles are in `decompiled/phys/0xADDR.c`. Text ids are `localize\globaltext.xlt` keys (US column).

## 1. Data

### World/Data/ArbeitClient.tdf (loader 0x7093c0, the ArbeitClient part at 0x70a361) **[exact layout]**

119 rows, 39 columns. The client loads `data\world\data\arbeitclient.tdf`. The older `data\arbeitclient.tdf` has other rewards.

| col | content | notes |
|---|---|---|
| 0 | id `a_00001` | first letter `e` gives loader type 1 (event time attacks), `g` gives type 2 (seasonal), anything else 0 |
| 1 | number: 43 Easy, 61 Medium, 110 Hard/Special, 0 events/EX | **[inferred]** the highest level the job is offered to (110 = everyone) |
| 2 | `a_i_0001` | cargo model, `game\a_i_%04u.nif` (format string at 0x875efc) |
| 3 | start post | the RQ house: `post_1st_00052` is "Station 1" of Moon Palace. Oros also uses Roo Outpost / Team Roo HQ |
| 4, 5 | title, short description | |
| 6 | the delivery worker's lines | `<pimg ch_1st_0005_01><name NHN Delivery Worker><text ...>` |
| 8-27 | 20 route posts | `post_Nth_yRRNN` "Delivery Service [n]". Unused columns repeat a default (`post_1st_y1101`) |
| 28-36 | gold, silver, bronze: EXP, Mito, seconds | 0x5582a0 prints each tier as `"%d:%02d:%02d %dExp %dMito"` (0x86dbb4) from record +0x226c.. (time), +0x224c.. (EXP), +0x225c.. (Mito) |
| 37-38 | the fourth tier: EXP, Mito for a finish without a medal (record +0x224c[3] / +0x225c[3]; 0 for the regular jobs, 100/30 .. 200/500 for the events; "10.5" reads 10) | **[exact layout, inferred meaning]** |

The regular jobs are a_00001-a_00067: five per station in Moon Palace and Koinonia, four in Cras, Oros at its three posts.

**[rebuild]** The route is the run of points that share the first point's `post_Nth_yRR` prefix, up to the first repeat.
This drops the padding. Jobs offered: col 1 > 0, not `[Event]`/`e`/`g`, every post present in the icon table.

The other rows (fork notes, `scratchpad/mc/missions_notes.md`) **[exact data]**:
- 67-70 `e_v_001..004` "[Event] MP ST1..4 Time-Attack" and 71-81 `e_v_005..016` "Deliver the Easter Egg" (Easter Rabbit
  NPC), 94-108 `g_event_autumn_01..15` "Fall Harvest Delivery" (Delivery Girl), 109-113 `a_halloween1..5`, 114-118
  `a_00115..119` "[Event] Vicki's Mittron Cleanup" ([OMD] / [ROO] parts; col 1 = 110 in the old data): col 1 = 0.
- 82-93 `a_00048..a_00059` "... Delivery EX" (MP / KO / CR stations 1-4): five-point loops "Delivery Service EX n-1-1..5",
  col 1 = 0, small rewards; **their ids repeat regular jobs a_00048-59**.
- The client's accept path (packet 0x144 -> 0x4f4800 -> 0x7051b0) needs col 1 >= the driver's level, so this build never
  started a col 1 = 0 row; the official server enabled them with a data patch [inferred]. Types e / g are sorted after the
  regular jobs (0x702800) and drawn with minimap icon 0xd for every stop still to reach (0x5fdf40 / 0x6005b2).
- "Time Attack" is Team ROO's name for the Delivery Service, not a separate mode: faction `0x6cb0c0()+0xed` (1 = ROO); every
  delivery text has a ROO twin (10659 accepted, 10661, 10662-10664, 10668, 10669, 10660, 11119), and the TIMEATTACK labels
  of 790000 / 800000 (TA_BASE) / 2810000 show for ROO drivers. ENTRANCE's four buttons ARBEIT / TIMEATTACK / EVENTARBEIT /
  EVENTTIMEATTACK all open the same list (0x561610); the event pair shows with net+0x290 & 0x20000, the NEW badges QSNEW1/2 or
  QSEVENT1/2 (net+0x294 & 0x4000000). Roo Outpost and Team Roo HQ (icon table column 4 "ROO") show SECRETMISSION and
  TIMEATTACK instead of QUEST and ARBEIT (0x562030); in Oros a station serves only its own faction.

**[rebuild]** Event and EX jobs (`DeliveryJob.Kind`): EX loops keyed `ex_` + id (records, server), offered for every level;
event jobs while their season runs (`SeasonEvents`: e_v Easter / Valentine, g_event_autumn Harvest, a_halloween Halloween,
Vicki's cleanup always); their route is the first five distinct points of the station's city (the times fit five stops);
regular jobs first in the list. Factions (section 12): Team ROO drivers see the TIMEATTACK labels and texts; the ROO-start rows
are offered at the ROO stations (their Time Attack serves Team ROO only).

### World/Data/Instant_Client.tdf (loader 0x6d1200) **[exact layout]**

299 rows, 217 columns. Kind strings are mapped to numbers at 0x6c5e40 (UTF-16 strings 0x877608-0x877ae0):
battle 1, auction 2, quick_rank 3, shop 4, knife 5, teambattle_win 6, combo 7, move 8, move_event 9, move_ROO 10,
mission_item 11, instant_start 12, instant_clear 13, chase_start 14, huv_kill 15, chase_clear 16, patrol_start 17,
patrol_clear 18, lap_clear 19, rush_start 20, rush_clear 21, under_start 22, under_clear 23, quick_start 24,
quick_grade 25, vchasing_* 26-31, party_move 0x20, party_move_ROO 0x21, Mission_Battle 0x22, Mission_Battle1-3 0x23-0x25,
Battle_Owner/Item/Knife/Combo/Crash/Retired 0x26-0x2b, Chasing_Notem/Drift/Boost/Side/Combo 0x2c-0x30, Arrest_HUV 0x31.

| col | content | record field |
|---|---|---|
| 0 | `instant`, `patrol`, `day_mission` | category |
| 1, 2 | level range | |
| 4 | NewInstant group: `City`, `Chasing`, `Battle` (`0` for the old rows) | |
| 5 | title | +0x14 |
| 6 | True/False | unknown flag |
| 7 | 25-35 for instants | **[inferred]** time limit in minutes |
| 8 | NPC lines (`<pimg>`, `<name>`, `<text>`; NewInstant rows use `<sub_text>`) | |
| 9 | kind | +0x1124 |
| 10 | count to reach | +0x1128 |
| 12 | instant EXP / patrol EXP per post | +0x1130 (0x5c36d0 `"%d Exp"`, 0x5c4f90 doubles it during an EXP event) |
| 13 | instant Mito (or a formula) / patrol: unknown per-post value | +0x1134 (`"%d Mito"`) |
| 14 | patrol: EXP for the whole loop | +0x1138 |
| 15 | patrol: number of posts | +0x113c (the confirm dialog's MAX) |
| 16.. | posts: the officer first, then the patrol loop | |

The reward formulas in column 13 are `INT(({Level}^1.7)/5+10)`, `INT(({Level}^2)/7+10)` and `{체이싱보상}*N` (the chase
reward). The client computes `Level*Level/7+10` itself for Mission_Battle and Arrest_HUV (0x5c3820, cases 0x22, 0x23 and
0x31; times 3 for 0x24, times 5 for 0x25). `NewInstant = 1` in globalcontent.xlt (US column) switches the client to the
NewInstant display (0x5c3820, `11455`/`11456` "Bonus Exp/Mito"). `MinPatrolGrade = 3` and `MinQuickGrade = 1` are in the same table.

### Posts

All posts are rows of `World/Data/Icon_<city>_Client.tdf` (`CityMapInfo.Icons`). Kinds: `post_Nth_y` delivery points,
`post_Nth_WP001-160` and `post_Nth_WPE_n` world patrol, plain `post_Nth_00xxx` for the patrol loops. Examples:
"Officer Heather's Patrol Instant Mission-1..160" and "Heather Mission_001..004", which stands beside Station 1..4.
`cras\mitaquest.qpos` and the rest are the Mittron (Mita500) quests and are not used here.

## 2. Delivery Service flow **[exact unless marked]**

| step | client | address |
|---|---|---|
| station ARBEIT button | opens QUESTDIALOG 600000 with the job's lines next to ARBEIT 2810000 | |
| ARBEIT list | `ID_ARBEIT_DESC` (TYPE 9 list, 22 px rows). Message 0x2001 draws the rows (0x557310); 0x702 = selection, which shows the medal lines and BEST0-2 (`10184` "%s (%dminute %dseconds)", the server's top 3) | 0x55c4e3, 0x5582a0 |
| ARBEIT close (control 2810007), rankings week/month/all (2810013-15) | | 0x55c509, 0x557540 |
| accept | QUESTDIALOG control 600009: the quest when the quest list is up, else the selected job (0x55b6a0). Refused with 10185 "Already processing." while one runs, 10186 while another mission runs, 10661 in time-attack mode, 10621 during a chase | 0x55e1e3 |
| accepted (server reply) | sound 0x4e93 SID_ARBEIT_ACCEPT, chat 10133 "[%s] Deliver Service accepted" | 0x6f6af0 |
| ready / cancelled | 10130 / 10131 | 0x6f6a90 |
| destination reached (server) | sound 0x4e99 SID_CALLSIGN_ACCEPT, 10126 "[%s] Destination <%s> arrived." | 0x6f4ef0 |
| completed (server) | sound 0x4e8e SID_QUEST_SUCCESS, 10123 "[%s] completed.", then 10134/10135/10136 "Exp. %d Mito. %d <Gold/Silver/Bronze Medal>" for medal 1/2/3, or "Exp : %d Mito : %d" for 0 | 0x6f6ba0 |
| failed (server) | sound 0x4e95 SID_QUEST_CANCEL, 10137 "[%s] failed." | 0x6f6db0 |
| failed, retry | 10709 "[%s] failed." and the question 10125 "Would you like to attempt this mission again?" | 0x6f6750 |
| run HUD | ARBEITPLAYINFO 790000 (labels ARBEIT/TIMEATTACK, title, best record, Restart 790008, Detail 790009) and 820000 (the clock as digit art: TIME_6..0 = mm ss . thousandths, NUM_0-9 are the digit images) | 0x55d3c0, 0x5560d0 |
| Restart | asks 10908, then restarts | 0x55d3c0 |
| Detail | MYARBEIT 2820000: Abandon 2820012 (0x55b830: 10187/10188/10189), Close 2820013 | 0x55d740 |
| station badge | `ID_ENTRANCE_RQHOUSE_ING_QUICKSVC` under the button while a delivery runs | |
| newer run HUD | ARBEITPLAYINFONEW 800000 with ARBEITPLAYINFOADV 810000 when net+0x267 & 1: QS_BASE (TA_BASE for ROO), TITLE, the cargo BASE box, RETRY 800006 (10908), DETAIL 800007 toggles MYARBEIT (SID 0x4e8a / 0x4e8b, 0x55bcb0); 810000 BEST_FRONT / BEST | 0x55d950, 0x55d250 |
| completion extras | packet 0x146 -> 0x4f4990 opens QSRANKLIST 2830000 (rank list); a rank < 2 shows QUEST 1770000 with effect 14 qs_newrecord (0x5531e0); 0x6f6430 / 0x6f6560 raise the 1780000 reward lines | 0x554cc0 |

Sound ids come from the SID table at 0x5232b0: an entry's name follows its id.

**Rebuild rules [rebuild]:** the server judged arrivals and paid the medal. Offline:
- a destination counts within 25 m (horizontal, and within 40 m of height);
- the medal comes from the table times;
- a run fails at twice the bronze time;
- no medal pays nothing;
- best times and medals are saved in `<save>.missions.json`, because the profile class is shared with the server;
- BEST0-2 show the driver's own three best runs, and the ranking buttons are hidden;
- a chase or race starting during a delivery abandons it;
- Restart puts the car back at the station.
- The station panel still opens during a delivery or a patrol, as the client's ING_QUICKSVC badge implies. Its Delivery
  Service button then opens MYARBEIT. Battle Zone answers 10623 (delivery) or 10622 (patrol).
- No medal: cols 37/38 are paid when the row has them ("Exp : %d Mito : %d"), else nothing as before.
- A new personal best (from the second run on) shows QUEST 1770000 + qs_newrecord; every paid finish shows 1780000.
- `DeliveryService.NewLayout` switches to ARBEITPLAYINFONEW / ADV (off by default: the flag was the server's). Its F-anchored
  controls need the dialog's G offset (`MissionUi.ApplyDialogOffset`).

## 3. Instant missions

| what | client | address |
|---|---|---|
| offer | "Call Mission" 860000: NOTIFY1-4 (art variants), Refuse (Esc), Accept (Tab, J=9) | |
| running list | 230000 (tabs Instant / Patrol / Daily / Virtual HUV) or ADV 240000 (five rows). Text 10461 "%d/%d achieved", 10938 "Completed", 10963/10964 party waiting | 0x5c4640 |
| click a row | instant: confirm 1800000 (TEXT, NUMBER1ST/2ND, FORGIVE, CLOSE) filled by 0x5c36d0 (old) or 0x5c3820 (NewInstant). Patrol: PATROL 3380000 (0x5c4f90 / 0x5c5330) | |
| description | 1790000 (DESC_TEXT, two reward lines) | |
| abandon | 10194 "Are you sure you want to abandon this mission?" | 0x55bc10 |
| failed | 10992 "Instant Mission Failed." | 0x6f4bc0 |
| server switches | `InstantMission.Combo/Stunt/QuickService/TeamBattle/Patrol/PartyPatrol/NewQuickRecord/DayMission/Mita500...` | 0x453950 |

**Rebuild rules [rebuild]:** the server picked and paid the missions. Offline:
- a random row for the driver's level is offered every 150 s of free driving (the first after 90 s), never during a mission, a menu or another offer;
- City rows come first (NewInstant);
- the offer lapses after 15 s;
- accepting starts the mission and shows 1790000 for 8 s;
- progress counts with the offline systems:
  - `knife`: a near miss in the speed combo (the STUNT icon);
  - `combo`: every speed-combo trick;
  - `Arrest_HUV`: `PlayerProfile.HuvCaught`;
  - `battle`: `PlayerProfile.RacesEntered`;
- column 7 is the time limit;
- the reward is column 12 EXP plus column 13 Mito (formula with the level).

Not offered offline: auction, quick_rank, shop, teambattle_win, mission_item, the Chasing_* variants (they change the next
chase), the Battle_*/Mission_Battle rooms and day_mission (the Mission Girl).

The running list: INSTANTMISSION 240000 with the ADV main UI, or **230000** with the basic one (goption MainUIType 0; the
client tests `**(ui+0x16c) == 0`, both slide in with 0x5b4140): one row (CONTENT1ST title, CONTENT2ND progress drawn by
0x5c4640, BTN 230002 -> the confirm of the shown mission 0x5c4f90) under the tabs TAB1ST Instant / TAB2ND Patrol / TAB3RD
Daily / TAB4TH Virtual HUV (handler 0x5c6270 -> 0x5c56d0 / 0x5c5810 / 0x5c5950 / 0x5c3df0). `MissionTracker` picks it from
the option (`MissionTracker.Classic` overrides), lifted above our chat window like the ADV list.

## 4. Patrols

- Each patrol row starts at an officer post near a station. Rows come in groups of four (one per station) per level bracket.
- The loop is column 15 posts long: 40, 80 or 160. 160 posts make about 26 km in Moon Palace.
- PATROL 3380000 shows: `ID_PATROL_CONFIRM_DESC` (title), the reward lines (10462 "%dEXP point per post, %dEXP when all
  completed." from +0x1130 / +0x1138), CUR / MAX (+0x1e0 / +0x1e4), Abandon and Esc.
- Each post: 10127 "[%s] Destination <%s> patrolled." (0x6fae40; debug lines "Quest Patrol Time : %d" and "Quest Distance : %d m").
- 3370000 is the day mission's description panel (reward lines "500 Mito/ 200 Exp", "1 Token").
- PARTYPATROL 3880000 is a time display: TIMENUM digit art, COMMA, BONUSTIME01-20. 3890000 is the result for four members:
  name, posts, rescues, EXP, items, clear time, wait time.
- Party texts: 11156 "Party Patrol Start!", 11157 "You've completed a Party Patrol.", 11158 failed, 11159 leader quit,
  20312-20315 party restrictions, 11120 no Auto-Drive during a patrol.
- The PARTYPATROL handlers (0x61ce90, 0x545dd0) are not procedures in the Ghidra project and were not decompiled.

**Rebuild rules [rebuild]:**
- The officer offers the patrol with the Call Mission notice when the car comes within 60 m of the officer's post. A refusal
  holds that officer for 120 s.
- The row is the highest bracket the driver meets.
- Posts count within 30 m, in any order. The beacon and the minimap show the next open posts along the loop, starting from
  the post nearest the officer.
- EXP is column 12 per post plus column 14 for the full loop. Column 13 is paid as Mito per post **[inferred]**.
- The reward is paid when the patrol ends: completed, abandoned, or when leaving the city.
- `move_ROO` / `party_move_ROO` rows are offered to Team ROO drivers only, `move` / `party_move` to OMD drivers (section 6).
- `party_move` rows (Officer Amy at level 120, Eugene's World Patrol at level 80) run solo with the same rules offline or
  outside a party, ending on 3890000 with one line. Online in a party the leader starts them for everyone: shared posts,
  the 3880000 timer with bonus times, 3890000 for every member (PARTYPLAY.md section 2).

## 5. Server mirror

`DriftCityServer/src/economy.js` kinds and cooldowns:

| kind | cooldown | rule |
|---|---|---|
| `delivery` | 30 s | medal from the time and the job's limits; a time under 40% of gold is refused; jobs above the driver's max level are refused |
| `instant` | 60 s | row must be an offline kind; level range checked; columns 12/13 with the formulas |
| `patrol` | 30 s | posts capped at column 15; at least 3 s per post; per-post EXP/Mito plus the completion EXP |
| `delivery` (events) | 30 s | event / EX rows keyed like the client (`ex_` + id), no level cap for col 1 = 0, a finish without a medal pays cols 37/38 when the row has them, else "no medal" |
| `stepquest` | 5 s | a QuestClient.tdf step quest handed in: the running quest with every step reported (`src/quests.js`), 3 s a step at least since the accept; columns 75 / 76, the items of 77-79, the unlock (a join quest sets `Faction`); an event quest (kind 99) is accepted only while its season runs; `quest.step` step 0 raises `StepQuestTries` (the bonus count, section 14.3) |
| `patrol` (sides) | 30 s | move_ROO / party_move_ROO rows need `Faction` 1, move / party_move need 0 (move_event both) |
| `mita` | 5 s | a Mita 500 bottle (index 0..499): 500 Mito, three a UTC day (p.MitaDay / MitaCount), the effect 150 / 200 / 250 % on the EXP of chase / delivery / instant / rush / vhuv for 1800 s (session only; none above level 70) |

The server reads the same .tdf files from `DC_GAMEDATA`. Without them it uses flat fallbacks. Test:
`station missions: delivery medals by time, instant and patrol rewards from the tables`.

## 6. Team ROO patrols [exact, rebuilt]

`beat_*` is not a patrol: beat_success / beat_fail are UI effects 45 / 46, Team ROO's chase result banners (CHASES.md).
The ROO patrols are the `move_ROO` rows (Monica's Scouting, level 46 / 56, the Oros loop post_4rd_00185..344) and the
`party_move_ROO` rows (Officer Min at 120; Eugene's Treasure Hunt IV World Patrol at 80). The offer (0x6fb8d0) needs the
server flag net+0x260 & 0x10 (0x20 for party rows) and MinPatrolGrade, and gives ROO drivers only kind 10 (solo) / 0x21
(party), OMD drivers only 8 / 0x20; a post counts within the object's radius +0x60 and |dz| < 7. Otherwise identical to the
OMD patrols. **[rebuild]** `PatrolMission.RowsFor(city, level, faction)` / `KindFor`: Team ROO drivers are offered only the ROO
rows (and move_event), OMD drivers only move / party_move; no patrol is offered while a step quest runs; the server's `patrol`
kind checks the side.

## 7. Mita 500, the "Mittron quests" [exact unless marked]

`<city>\mitaquest.qpos` (loader 0x6ed090, singleton 0x6ed860; "qpos\0agt\0kjc\0ef2", u8 1, u32 count, count x float3 -
500 points per city, the same format as pos4navi.qpos) is not a quest chain: the points are the Mita 500 "Special Drink"
salesman's spots, and the party patrol's rescue spots (0x6ff570 / 0x6fb8d0, "Quick Patrol Post", PARTYPLAY.md).
- The server sends today's purchase count and the live spots {qpos index, seconds left} (0x504fe0). The city's list is used
  only while the drink value is under 201 and the level suits the city: Moon Palace < 21, Koinonia < 31, Cras < 46, Oros and
  Taipei always (0x6eb6c0). Each frame (0x701850), with at most two bottles bought, a live spot within 10 m is removed and
  bought (0x78ad30); ID_MINIMAP_ICON_MITA marks the spots, game\mita_icon.nif stands on them.
- The reply 0x4f83c0: Mita500_1 for the first bottle, Mita500_4 above level 70, Mita500_2 when the value was 150, else
  Mita500_3 (the Mita500_Halloween_* lines with net+0x288 & 0x200); the value 150 / 200 / 250 / 500 (= bonus % + 100),
  11058 "[%d%% Special Drink effect] applied", 10894 / 11701 when it ends (0x6eba00), 11059 "too much"; car effect
  mita500_water / mita500_bott.nif fading over 1800 s. Server switches Mita500RegenCnt / RegenSec / BuffSec / LuckyHour.
- ENTRANCE MITRON (400007) is the fuel refill, not Mita 500.

**[rebuild]** (`MitaDrink.cs`, `MitaSpots` on the city session; server `mita`): five live spots at a time, 200 m - 2 km from
the car, 300 s each; 500 Mito a bottle (Mita500_1 says so), three a day; the n-th bottle sets 150 / 200 / 250 (none above
level 70) for 1800 s; the effect multiplies the EXP of the chase, delivery, instant mission, Rush Time and Virtual HUV
rewards (what the client's effect raised is not traced). The salesman's portrait ch_m_0028_00 is not in the data.

## 8. The quest screens [exact]

The HUD class 0x55e890 binds them: QUESTPLAYINFO 720000 (+0x184), 730000 (+0x19c), 740000 crash digits, 750000 speed
range, 760000 follow gauge, 770000 follow question. They belong to the step quests of **World/Data/QuestClient.tdf**
(320 quests, 80 columns, five steps each: step texts 14-18, step posts and markers, step types 34-38, crash limits 39-43,
time limits 44-48 (167 quests have one), speed ranges, follow distances, rewards 75 / 76 [column meanings inferred]).
- 720000 (per frame 0x5592d0, handler 0x55c100): CONTENT = the running step's text, or 10178 "Mission Completed."; RETRY
  720005: done -> 10193, else 10125 (0x554580); VIEWQUEST 720006 -> the quest log (0x55acc0). Hidden with no step running.
- 730000: the step's time left as digit art (0x5559b0, mm ' ss , ttt); 740000 crashes used / allowed (0x555600); 750000
  SPEED_MIN / MAX in red / yellow (15-30 km/h inside, blinking) / green; 760000 the follow gauge (0x555f30).
- QUEST 1770000 (new best time) and 1780000 (the rising reward lines): CHASES.md section 2.

**[rebuild]** `QuestPlayInfo` (attached by `MissionExtras.Attach`) shows 720000 for the running step quest (section 11: "(n/m)
step text", the title for the 11 steps without text, 10178 when done; RETRY 10193 / 10125 starts the quest over; VIEWQUEST opens
the quest log) or else for the first active motherquest quest ("(counter/goal) title: objective"); hidden while a delivery runs.

## 9. The ten big "mission" functions

- 0x5d52d0 (7.3 KB): the item use dispatcher (not a mission); it holds the Auto-Drive refusals (CHASES.md section 6).
- 0x6f1fa0 (7 KB): builds the quest-giver markers of the city from the QuestClient records (start post in this city, level
  and car grade, marker kind by quest type: MQ / SQ / RQ start icons).
- 0x6dbc40 (5.5 KB): per-vehicle physics tweaks of the player car (not a mission).
- 0x6ffa70 (5 KB): the station table per city (RQ houses with trigger quads, event NPC posts).
- 0x6ebb10 (3.7 KB): the Motor Show / Comic 2.0 spot-hunt event (EVENTS side).
- 0x70f7b0 (2.4 KB): the Init/RewardGroup loader.
- 0x6f17a0 (2 KB): the post enter / leave dispatcher (station and NPC zones, car flag +0x1ac0 & 0x10).
- 0x701120 (1.8 KB): per-frame station proximity, opens ENTRANCE (StationPanel).
- 0x6a3630 (1.6 KB): a ring cache of "floor" ground decals (Effects).
- 0x584d10 (1.5 KB): the PvP tournament HUD (11492-11496).

## 10. Not rebuilt

- The server's top three on the ARBEIT list (BEST0-2 show the driver's own runs; QSRANKLIST has the server's lists, section 13).
- Delivery coupons (10144) and the medal-set rewards (11313-11317).
- The cargo model on the car (`game\a_i_*.nif`), the `arbeit_com_*.nif` result effects.
- The minimum car grades (`MinQuickGrade`, `MinPatrolGrade`).
- Day missions (the Mission Girl).
- The station's instant mission button (ENTRANCE INSTANT, server switch net+0x24c & 0x100000, request 0x78a100 = packet
  0x3de) and the event Delivery Service pair (net+0x290 & 0x20000): server switches, left off (section 14.2).
- The Tutorial mode (the apprentice course on moonpalace\tutorial, section 14.7) and the Battle Zone tutorial lines.
- The VIEWMAP city pages' locks (0x5f3170 / 0x5f5c40) and the guide strip BEGINNERQUEST from the flag (hook lines, 14.8).

## 11. Step quests: World/Data/QuestClient.tdf [exact unless marked]

**The table** (loader 0x7093c0, QuestClient part 0x709639-0x709f80; 320 rows x 80 columns, 274 in the older
`data\questclient.tdf`). Record offsets are the runtime ones; the loader's stack copy is 4 lower.

| col | content | record |
|---|---|---|
| 0 | id: `q_` main, `rq_` repeat, `tq_` training, `nq_` Taipei, `eq_` event, `m_tat01`. An id with `omd` is for OMD drivers only, one with `roo` for Team ROO only (0x7096d3); no shipped id has either | +0 |
| 1 | the quest number: prerequisites and the server name quests by it | +0x3c |
| 2 | group (`main`, `rq_moon_1_1` ..) | +0x91c |
| 3 | kind: 0 main story (MQ), 1 repeat (RQ, `repeat_token` rewards), 2 side (SQ), 3 Taipei (NQ), 99 event | +0x914 |
| 4 | prerequisite quest number (0 = none) | +0x40 |
| 5 | unlock (0x6c57a0): CHASE 1, ARBEIT 2, CITY 3, CITY_2ND..CITY_5TH 4-7, BRANTON 8, OMD 9, ROO 10 | +0x44 |
| 6, 7 | level range (7: 1000 = none) | +0x48, +0x98c |
| 9 | start post: a station for the main story, a quest giver in the city for the others | +0x50 |
| 10, 11 | title; the start NPC's lines | +0x54; script +0x99c (type 2) |
| 12, 13 | end post; the end NPC's lines | +0xd4; +0x9a0 (type 3) |
| 14-18 | the five step texts (0x100 each) | +0xd8 + i*0x100 |
| 19-28 | per step the post and its marker (`i_post_0000n`, or a model name like `sq_pass_cityhall`) | +0x5d8 + i*4, +0x5ec + i*0x38 |
| 29-33 | per step the lines on arrival (0x6fc750) | +0x9a4 + i*4 (type 4) |
| 34-38 | step kind (below) | +0x704 + i*4 |
| 39-43 | crashes allowed (the count is capped at 9) | +0x718 |
| 44-48 | seconds for the step | +0x72c |
| 49-53, 54-58 | speed range in km/h | +0x740, +0x754 |
| 59-63, 64-68 | trail distance range in m | +0x768, +0x77c |
| 69 | the trail car's path in `<city>\path\path_<city>.chpath` (10142 "Path ID %d doesn't exist.") | +0x790 |
| 71 | vehiclelist Unique Id: the trail car (20xxx traffic, 10xxx HUV) or the chased HUV | +0x798 |
| 75, 76 | EXP, Mito | +0x7a4, +0x7a8 |
| 77-79 | reward items (useitem ids) | +0x8fc.. |

q_00159 / 160 / 161 / 163 / 166 (the OMD side's arrests of the ROO leaders) set a flag (+0x90c) that goes into the HUV
manager's Team ROO flag (+0xe0) when their chase starts, so those chases end on beat_success / beat_fail (CHASES.md section 1).
q_00149 (+0x90d) and q_00167 (+0x90e), the two finales, set flags that were not traced further.

**The running entry** (0x702f30, 0x5c bytes): +4 state (1 = done reported, 3 = failed), +8 record, +0xc the server's id, +0x10
step, +0x14 / +0x18 / +0x20 the server's time / crash / distance bonuses, +0x24 done, +0x30 time left, +0x34 / +0x38 the 10 s
grace, +0x3c last crash, +0x40 crashes, +0x44 / +0x45 trail started / in range, +0x48 trail distance, +0x4c chase spawned.

**Steps** (0x6fc7b0, called when the car's current post, manager +0x4c, is the step's post):

| kind | on arrival |
|---|---|
| 0 | 10126 "[%s] Destination <%s> arrived." (0x6fad00), the step's lines, the next step |
| 1 | the same, when the speed is under 11 km/h (0x6faf80) |
| 2 | 10127 "patrolled" (0x6fae40) |
| 3 | the trail car (col 71) starts on path col 69 (0x6f99b0), 10128 "start trailing", the next step; that step keeps the car between its col 59 / 64 distances (0x6f72b0) |
| 4 | a stake-out: the car is spawned and 10129; leaving the post fails (0x6fcf50); 2 s after the car came close (0x6f7150, +0x45) the step ends |
| 5 | the HUV of col 71 is spawned at the car (0x44a5d0), the HUV manager gets its target and the Team ROO flag, the chase request goes out (0x783bb0). The step after a chase step does not end by arrival: the chase result ends it |
| 6 | like 3, with the manager's longer range (+0x120) |

After a step, 0x702b30 / 0x702ad0 move to the next step (its time from col 44+i plus the bonus). After step 4, or before a step
without a post, the quest is done (+0x24, 10178 on the bar, report 0x788e50). Every step plays sound 0x4f06 and is reported
(0x7893a0). The frame check 0x6fcf50 fails the step (state 3, report 0x789100) when:
- the time runs out (0x555570);
- the speed stays outside the range for 10 s;
- the trail distance stays out of range for 10 s.

0x6ff1d0 counts a crash at most once per interval, fails at the limit, and fails at once when the car hits the trail car.
0x6f1fa0 builds the quest-giver markers from the server's offered list (manager +0x5c, 0x63aa-byte records: level +0x27, car
grade +0x2b, MQ / SQ / RQ start icons by type).

**The screens:**
- 720000: section 8.
- 730000: the time; TIME_6..0 copied from TIME_NUM0-9 (0x5559b0).
- 740000: crashes; CRASH_MAX_n for the limit, CRASH_n for the count (0x555600).
- 750000: SPEED_MIN / SPEED_MAX. 0x5592d0 colours the speed red within 15 km/h of the minimum (blinking every 1/6 s), yellow
  within 30 (blinking every 1/4 s), green beyond.
- 760000: the trail gauge: FOLLOW_DISTANCE "%dm", the magenta segments, the arrow (0x555f30).
- 770000: the "suspicious" bubble.
- QUESTPLAYEFFECT 780000: the crash count in large digits.
- QUESTDIALOG 600000: RECV takes a quest, REWARD hands it in, PREVNEXT pages through a station's quests.

Texts: 10121 "[%s] clear. Reward issued.", 10122 accepted, 10123, 10124 abandoned, 10125 again?, 10137 failed, 10183
"<Processing>", 10190 "You cannot accept missions.", 10191 "No missions are available at this Station.", 10192 "Mission in
progress.", 10194 abandon?, 10195 "<Completed>". Markers: `game\mq_{start,normal,pass,stop,end}_{omd,roo}.nif` and
`sq_*_{airport,cityhall,mafia}.nif`; minimap MQ_START / MQ_NORMAL / MQ_END, SQ_*, REPEATQUEST.

**[rebuild]** `StepQuests.cs` holds the table and the rules; `StepQuestRunner.cs` runs the steps (attached by
`MissionExtras.Attach` and `StationPanel.Attach`).
- Offered: an event quest (kind 99) only in its season (section 14.6), the level in range, the prerequisite done, not done
  yet (repeat quests come back each UTC day), the id's side test, and the storyline branch. After #126 both join quests are offered: #127 "Team ROO Headquarters" (unlock ROO)
  at Team Roo HQ and #145 "Patrol Oros City" (unlock OMD) at Oros Station 1. Finishing one closes every quest whose
  prerequisite chain reaches the other (`StepQuests.Branch`).
- Taken at the start post: a station's QUEST / SECRETMISSION button (QUESTNEW1/2 blink while it has one, ING_QUEST while one
  runs), or any other quest giver when the car stops within 18 m (refusing holds that giver for 60 s). One quest at a time;
  not during a chase, delivery, patrol or race.
- A post counts within 25 m (|dz| < 40). Step lines pause the step; a `<jump x y z>` tag in them moves the car (the tutorial).
- Trail cars play their path's time keys in real time (a kinematic body). A stake-out ends 2 s after the car came within 45 m
  (or finished its path).
- A chase step starts `ChaseMission` with the col 71 HUV, found by vehiclelist Unique Id; with no model the step is passed.
- Crashes: a contact with more than 3 m/s along the normal, at most one a second.
- A failure: 10137, quest_fail, then 10125: yes starts over from step 0, no abandons (10124).
- The end post (within 25 m, under 30 km/h, or the station's button) shows the end lines with REWARD: columns 75 / 76, the
  items, the unlock (OMD / ROO sets `PlayerProfile.Faction`), 10121, quest_success and the 1780000 reward lines.
- Saved in the profile: `StepQuestsDone`, `StepQuest` (QuestId = number, Step, Counter 1 = done), `StepQuestDay`, `Faction`.
- Online: `quest.accept`, `quest.step` (in order, 1 s apart; step 0 = start over) and `quest.abandon` (`src/quests.js`), and
  the reward kind `stepquest` (section 5); the server answers with the driver.
- Moon Palace's four stations (flagged not shown in the icon table) now open the panel too: they give the first story quests.

## 12. Factions: OMD and Team ROO [exact unless marked]

The faction is a dword in the character data, `0x6cb0c0()+0xed` (0 OMD, 1 Team ROO), set by packet 0x76d (0x4f7a00). 78
places read it; the ones rebuilt:
- ENTRANCE 400000 (0x562030, controls bound at 0x56248b). The station's +0x210 marks a ROO station (icon table column 4 "ROO":
  Roo Outpost post_4rd_00007, Team Roo HQ post_4rd_00009). A ROO station shows TEAMROO (its floor art), SECRETMISSION instead
  of QUEST and TIMEATTACK instead of ARBEIT. While a step quest runs, ING_QUEST shows and the delivery button is disabled. The
  NEW badges QUESTNEW1/2 and QSNEW1/2 (QSEVENT1/2 in an event, net+0x294 & 0x4000000) blink; in Oros (net+0x3b4ec == 4) the
  delivery badge only shows for the station's own side. The skip test uses manager +0x10f (ROO) / +0x110 (OMD).
- The Delivery Service is "Time Attack" for Team ROO: ARBEIT_TIMEATTACK_TITLE / MYARBEIT_TIMEATTACK_TITLE, ARBEITPLAYINFO
  TIMEATTACK instead of ARBEIT, ARBEITPLAYINFONEW TA_BASE instead of QS_BASE, QSRANKLIST TIMEATTACK_TITLE and TIMEATTACKSPACE,
  and the texts 10659 (for 10133), 10661 (10186), 10662-10664 (10187-10189), 10668 (10621), 10669 (10623), 10660 (10144),
  11119 (11118).
- Patrols: section 6. Chases: the beat_* banners (section 11, the ROO leaders).
- 10671-10674 and 10676-10679 are the two sides' mottos (the rebuild prints the first one on joining).

**[rebuild]** `Factions` (StepQuests.cs): `PlayerProfile.Faction` is set by a join quest's reward; `Factions.IsRooPost`,
`DeliveryText`. `StationPanel` adds the ROO stations to the city's stations and opens their buttons; a ROO station's Time
Attack refuses OMD drivers (10199 "Not permitted"). `DeliveryService` switches the labels and texts; `PatrolMission.KindFor`
picks the patrol rows; `StepQuestRunner.RooChaseBanner` plays the ROO banners (hook in ChaseMission, `TeamRoo`).

## 13. QSRANKLIST 2830000: the delivery rankings [exact screen, rebuild lists]

Packet 0x146 (0x4f4990) opens it when a job is finished; the ARBEIT list's week / month / all buttons (2810013-15, 0x557540)
open it on request. Rows (LIST, 36 px): rank, crew, driver, best record. PREVWEEK shows the period before, PREV / NEXT page.
The bottom line: MYRANK (10907 "Rank lower than %d"), MYTEAM, MYRECORD (this run), MYBESTRECORD. PERIOD is 10904-10906 "This
Week / Month / Year" (10901-10903 for the period before). SPACE closes it (TIMEATTACKSPACE for Team ROO). A rank < 2 also
brought QUEST 1770000 with qs_newrecord (section 2).

**[rebuild]** `QsRankList.cs`. Online it asks `qs.rank {job, period 0 week / 1 month / 2 year, previous}` (`src/quests.js`: the
best time per driver from the `qs_records` table, filled when the server pays a delivery; weeks start on Monday, UTC; the
first 100; my rank and best; the crew column and MYTEAM from the crews tables of `src/teams.js`, by driver name). Offline the
list holds the driver's own three best runs with the crew the client knows (`Teams.CrewName`).

## 14. Unlocks, the beginner panel, bonuses, event quests [exact unless marked]

### 14.1 The unlock flags (0x703d20)

The quest manager (0x70b660) keeps nine flags at +0x108..+0x110. 0x703d20 clears them and sets them again whenever the
server's quest list arrives (0x706420, from the packet handlers at 0x4f3487 / 0x4f382d / 0x4fa631) or the level changes
(0x6cac88 / 0x6cacc7):

| flag | from the level (char +0x38) | from a completed quest (list +0x88) | read by |
|---|---|---|---|
| +0x108 rookie | - | unlock ARBEIT (2) | 0x5625e0 picks the station panel; 0x6f5cb0 guide strip 0x10 BEGINNERQUEST; 0x763bd3 parking-lot Battle Zone (10066 unless net+0x24c & 0x80000) |
| +0x109 arbeit | > 3 | ARBEIT (2) | 0x5622dc the ARBEIT button enabled; 0x55b4c2 the list (10182 "You didn't complete the Delivery Service mission.") |
| +0x10a chase | > 5 | CHASE (1) | 0x6ca110 the HUV call (also needs no delivery +0xec and no step quest +0xe8) |
| +0x10b Koinonia | > 13 and net+0x258 & 2 | CITY_2ND (4) | gate 0x6f9630 (cases 5 / 10: 10139), VIEWMAP pages 0x5f3170 / 0x5f5c40 |
| +0x10c Cras | > 28 and net+0x258 & 4 | CITY_3RD (5) | gate (cases 6 / 9 / 14: 10140), VIEWMAP |
| +0x10d Oros | > 45 and net+0x258 & 8 | CITY_4TH (6) | gate (cases 7 / 12: 10641), VIEWMAP |
| +0x10e Taipei | - | CITY_5TH (7), or the mother quest `mq_5th_003` in the driver's list (+0x100) | gate case 15 (11379 "Moving to Mitro City."; nothing below level 71 or during a kind-1 quest), VIEWMAP, 0x4f9180 |
| +0x10f | - | record +0x90d (q_00149 "Infiltration!", the Team ROO finale) | 0x562030 |
| +0x110 | - | record +0x90e (q_00167 "Issue the No.9 Card", the OMD finale) | 0x562030 |

"CITY" (3), "car" and "1" in column 5 set nothing. The gates go through 0x6f5520(server bit, flag, ...): server bit off ->
10745 "This feature is currently unavailable.", flag off -> 10746 "This area is restricted.", else the confirm (type 6)
"Enter Koinonia?" etc. Cases 8 / 11 / 13 / 16 (10138 "Enter Moon Palace?") are always open. VIEWMAP (480000.., one page per
city) refuses a locked page with sound 0x4e8b. The client prints nothing when a flag opens.

The shipped unlocks: #3 / #20 / #27 / #37 / #45 "car" (nothing), **#5 Rookie Mission (3/3) ARBEIT**, **#9 Start Chasing!
CHASE** (#116, #131, #154 too), **#34 Report CITY_2ND**, **#71 New City CITY_3RD**, **#107 To Oros... CITY_4TH**, many "city".
No row has CITY_5TH, so Taipei opens through mq_5th_003.

**[rebuild]** `QuestUnlocks` (StepQuests.cs) computes the same flags (every server city bit on): `Of(p)` -> `QuestUnlockState`,
`ChaseOffer`, `CityOpen` / `CityRefusal` (10746; Taipei also needs level 71), `DeliveryRefusal` (10182), `BattleZoneRefusal`
(10066), `StoryOver` (the side's finale); `Enabled = false` opens everything (tests, tools). A driver at level 6 or more who
has finished no step quest at all (a save from before the step quests) counts as past the rookie missions; the original
server never had one. Server mirror `unlocksOf` / `cityOpen` / `Quests.unlocks(p)` in `src/quests.js`. Applied here: the
station panel and the Delivery Service list (StationPanel, DeliveryService.OpenBoard); the chase offer, the gates and the
parking-lot Battle Zone are hook lines (14.8).

### 14.2 The station's beginner panel (0x5625e0 / 0x561f80 / 0x562030)

ENTRANCE's controls are bound at 0x56248b: +8 TITLE (400003), +0xc BEGINNERQUEST (400023), +0x10 QUEST (400004), +0x14
INSTANT (400005), +0x18 ARBEIT (400006), +0x1c EVENTARBEIT (400012), +0x20 SECRETMISSION (400010), +0x24 TIMEATTACK (400011),
+0x28 EVENTTIMEATTACK (400013), +0x2c CHASING (400008), +0x30 / +0x34 QUESTNEW1 / 2, +0x38 / +0x3c QSNEW1 / 2, +0x40 / +0x44
QSEVENT1 / 2, +0x48 TEAMROO, +0x4c ING_BEGINNERQUES (400020), +0x50 ING_QUEST (400018), +0x54 ING_QUICKSVC (400019). Control
flags +0x3c: 2 shown, 4 enabled.
- 0x5625e0 (opening at a station, from 0x701475): hides the badges, INSTANT and the delivery buttons, shows TEAMROO /
  SECRETMISSION / TIMEATTACK (EVENTTIMEATTACK with net+0x290 & 0x20000) at a ROO station, QUEST / ARBEIT (EVENTARBEIT) at an
  OMD one, then calls **0x562030 when +0x108 is set, else 0x561f80**.
- 0x561f80, the beginner panel: hides QUEST, SECRETMISSION, every delivery button, ING_QUICKSVC and CHASING; shows
  BEGINNERQUEST (the 485 x 163 button over the three) disabled; with no quest running it is enabled unless 0x702060 (the
  completed count equals manager +0x44) [meaning inferred]; with one running ING_BEGINNERQUES shows. The handler 0x561610
  treats BEGINNERQUEST (0x61a97) like QUEST: sound 0x4e8d, the station's quest dialog (0x559f90).
- 0x562030, the full panel: QUEST / SECRETMISSION enabled unless the side's finale flag is set (OMD +0x110, ROO +0x10f) or the
  stage is type 0x11; the NEW badges blink when the server's first offered quest (0x703090) starts at this station and no
  delivery runs. With no offered quest and net+0x24c & 0x100000, the **INSTANT** button (QUEST's art) replaces QUEST for OMD
  drivers: enabled with no instant mission in slot 0 of the instant manager (0x6d19e0 +0x58), else disabled with ING_QUEST;
  clicking it closes the panel and sends 0x78a100(station, 1) = packet 0x3de (ask for an instant mission). The ARBEIT button
  shows disabled without +0x109 and while a step quest runs; QSNEW / QSEVENT blink with jobs (+0xf0), in Oros only for the
  station's side.
- The old note "BEGINNERQUEST = server switch net+0x24c & 0x100000" was wrong: that switch is the INSTANT button.

**[rebuild]** `StationPanel`: the beginner panel while `QuestUnlocks.Rookie` is off (BEGINNERQUEST enabled while no quest runs
and the station has one to give, ING_BEGINNERQUES while one runs; it opens the station's quests); the panel switches when
the rookie missions end; on the full panel ARBEIT / TIMEATTACK is disabled before `Arbeit`, QUEST / SECRETMISSION after the
side's finale. The beginner quests are the station's first story quests (#1 Gameplay Mechanics 1 .. #5 Rookie Mission
(3/3)); the `tq_` "TMA Training" rows have level 1000 and are never offered (the server gave them). INSTANT stays off.

### 14.3 The server's bonuses (0x706420 / 0x702f30)

The server's quest list entries are 14 bytes: quest number, state (0 running, 1 running with its steps done, 2 completed ->
list +0x88), the server's step, and a ushort n. The running entry gets the **time bonus n*3** (+0x14, added to a step's
col 44+i seconds when it has a limit: 0x702f30 / 0x702ad0), the **crash bonus n-1** (+0x18, added to col 39+i, the sum capped
at 9: 0x6ff1d0, the HUD 0x5592d0), n*5 at +0x1c (use not traced) and the **distance bonus n*5** (+0x20, added to the far
trail limit col 64+i; 0x553520 warns 10 m before it).

**[rebuild]** n = the times the running quest was started over (a failure's "yes" or RETRY), capped at 10:
`PlayerProfile.StepQuestTries`, reset on accept, abandon and reward; `StepQuests.StepSeconds / StepCrashes / StepFollowMax`;
the server raises it on `quest.step` step 0 and sends it back with the driver.

### 14.4 The finale flags

q_00149 (#144 Infiltration!, the ROO storyline's last quest, its script `<ending ROO>`) and q_00167 (#162 Issue the No.9 Card,
OMD, `<ending OMD>`) set record +0x90d / +0x90e (loader 0x709ce9), which 0x703d20 turns into manager +0x10f / +0x110; their
only reader is 0x562030: once the driver's side has finished its storyline, the station's QUEST / SECRETMISSION stays
disabled with no NEW badge. **[rebuild]** `QuestUnlocks.StoryOver`, StationPanel.

### 14.5 Switching sides

The faction is char +0xed, written only by packet 0x76d (0x4f7a00: `chardata+0xed = packet+2`, nothing else). No dialog or
request of the client changes it and no text mentions a change. **[rebuild]** Not offered: the side stays what the join quest
set (client and server refuse the other branch).

### 14.6 Event quests (kind 99)

Two rows, both from the Indonesian service: eq_00001 #10045 "Independence Day Event" (group event_ini_1, Operator Erina,
checkpoints post_1st_00943..947, start post post_1st_00942 "[INI Event Quest]", 800 EXP / 17000 Mito / i_flag_ini) and
eq_00002 #10046 "Moslem Event" (event_ini_2, Branton, Idul Fitri ketupat ingredients, 100 / 1000 / i_n_eventcoin). The client
has no special case for kind 99; the server offered them in season. **[rebuild]** `StepQuests.EventSeasonOf`: eq_00001 runs
with the summer festival (Jul 1 - Aug 31 holds August 17), eq_00002 with the autumn harvest (Idul Fitri fell on 2008-10-01,
2009-09-20, 2010-09-10); offered by their giver while `SeasonEvents` runs that season; the server checks the same seasons
(`quests.events`, server.js).

### 14.7 The tutorial

There is no separate tutorial stage in the city flow. The pieces in the client:
- **Tutorial mode** (the apprentice course): `World/Data/Apprentice_Client.tdf` (loader 0x706d70, 13 scripts: the
  walkie-talkie SYSTEM intro, Director Jason, Instructor Duffy's Drift and Booster tests with fail / retry lines, the full
  course, three results "Excellent / not as impressive / not everyone has talent" each followed by a reward car pick, and
  Operator Erina's "Congratulations on finishing the Tutorial mode... move to a city"). The course is the pvptrackinfo row
  `moonpalace Tutorial` (CityType 3, folder `moonpalace\tutorial`, name key moonpalace_tutorial); the timer is APPRENTICE
  830000; the lap grades are globalcontent ApprenticeLapResultGood / Normal / NotBad (130 / 135 / 140 s), read at 0x72b398 in
  0x72ab10 (not a Ghidra procedure) when net+0x24c & 0x80000. The PvP lobby refuses rooms during it (11285 "You cannot
  access during Tutorial", PVP.md); RMLOBBY has a TUTORIAL button; net+0x270 & 1 is "BattleZone.Tutorial".
- `World/Data/Tutorial.tdf` (loader 0x706ba0 into manager +0xcc): Instructor Duffy's Battle Zone lines keyed by gate
  power-ups (`<effect Repairgage / gate / repiar / Boost100 / Accel / Untouchable / Skidrush / Goal>`); only
  `data\tutorial.tdf` ships, the world path is missing, and nothing reads +0xcc.
- "tutorial" (0x8692fc) is also the sequence name of `Effects\sp_tcam.nif`, the arrival camera played on entering a zone of
  kind 0 (0x6fd2d0), and the scene-animation key that 0x4dc500 skips.
- The remake's new driver starts in Cras with Director Green, and the step quest "Gameplay Mechanics" (#1, `<jump>` to the
  practice posts) teaches the basics. The apprentice course is left for the PvP side (RaceMission on the tutorial track with
  the Apprentice lines and the car pick).

### 14.8 Hook lines for files this part does not own

- CitySession.OfferChase (0x6ca110): `if (offerOpen || !QuestUnlocks.ChaseOffer(PlayerProfile.Current)) return;`
- ParkingLotStage.ChooseGate (0x6f9630 / 0x6f5520), after computing `gate`:
  `var locked = QuestUnlocks.CityRefusal(Gates[gate].City); if (locked != null) { UiPopup.Message(locked); return; }`
  (the same in Menus' Travel rows before `GameFlow.Instance?.TravelTo(city)`).
- The parking lot's Battle Zone (0x763840): `var bz = QuestUnlocks.BattleZoneRefusal(PlayerProfile.Current); if (bz != null) { UiPopup.Message(bz); return; }`
- GuideText (0x6f5cb0): the BEGINNERQUEST strip while `!QuestUnlocks.Current.Rookie` (instead of the level-5 rule).
- Server economy.js `delivery`: `if (!quests.unlocks(p).arbeit) return fail("You didn't complete the Delivery Service mission.");`
  (needs the Quests instance in Economy; the existing tests pay deliveries for level-1 drivers).
