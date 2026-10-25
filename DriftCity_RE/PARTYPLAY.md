# Party play: the Undercity with a party, party patrol, party bonus, party combo

What DriftCity.exe does when a party plays together, and how the remake converts it. Client
`DriftCityOffline/Assets/DriftCity/Runtime/Game/` (`UndercityParty.cs`, `UndercityStage.Online.cs`, `UndercityStage.cs`,
`UndercityHud.cs`, `UndercityEntrance.cs`, `PatrolMission.cs`, `Social/PartyWindow.cs`), server `DriftCityServer/src/undercity.js`,
`src/partyplay.js` (+ the party bonus hook in `src/workshop.js`, `combo` in `world.js` partyInfo). [exact] = read from the
exe or its data, [inferred] = interpretation, "remake rule" = the original server decided and the remake keeps it simple.
Background: `UNDERCITY.md` (the stage), `MISSIONS.md` section 4 (patrols), `SOCIAL.md` / `social/party_friends.md` (the party).

## 1. Undercity with a party

### Client flow [exact]

| step | exe | remake (JSON) |
|---|---|---|
| entrance: UC_Enter -> UC_NeedMito ("share your Rescue Mission with your party?") -> Accept | 0x6f8e40 | `UndercityEntrance.Fee` |
| party checks: no solo (`UC_Err_PlayTogether`), max 4 (`UC_Err_MaxPlayerCnt`), everyone in this city (`UC_Err_SameCity`), Mito (`UC_Err_NeedMito`) | texts 932-937 | `UndercityParty.CheckParty` + the server's `uc.request` codes |
| request; the requester becomes the run leader (UC manager +0x3c, set from the packet at 0x50e300) | client 0xfc (0x79ce90, 0x38 bytes) | `uc.request {city}` |
| server 0xfc (0x50e1d0): requester = me -> WAITPLAYER 3530000 (0x6878b0(1), 15-frame animation); in Rush Time -> auto 0xfe + 11146; else REQUESTINVITE 3540000 with the leader's name | 0x50e1d0 | push `party.uc.wait` / `party.uc.invite {from, fee, seconds}` |
| REQUESTINVITE (handler 0x687e70, mode +0x9c = 0; +0x9c = 1 is the Virtual HUV invite 10940/10941 of packet 0x11c): TEXT 10770 "Do you want to join the Undercity Rescue Mission? You've been invited by %s.", TEXTLOWER 10772 (missions cancelled, the portal's %d Mito), 15 dots for +0xa0 = 15.0 s | 0x687910 | `UndercityHud.ShowRequestInvite` |
| ACCEPT (0x360437, Enter) -> 0xfd {myId, time} (0x79cf30); DENY (0x360438, Esc) or the timeout -> 0xfe (0x79cfd0, 0x687dd0) | | `uc.answer {accept}` |
| server 0xfd (0x50e0f0): my id -> WAITPLAYER; 0xfe (0x50e320): 10943 "Registration canceled due to party being unable to accept or rejecting the invitation.", dialogs closed | | push `party.uc.answer` / `party.uc.cancel {text}` |
| server 0xff (0x50e130): enter the stage of the city (0x717250), Mito replaced by the server's (the fee), instant mission kind 0x16 under_start | | push `party.uc.start {room, slot, members, channel}` -> `UndercityStage.BeginOnline` |
| server 0xfb (0x50e090) {kind, ms}: 1 -> state 2 at ms+3000, 4 -> state 5 at ms+1000, 7 -> state 8 at ms+1000 (the server drives the state changes) | 0x716390 | push `party.uc.phase {phase, clock, attempt, wave, barriers, slots}` |
| client 0xfa {kind, state, arg} (0x79cdf0): state reports from 0x7190c0 states 0 (3,0,0), 1 (1,1,t), 8 (2,8,0), 10 (2,10,0, leader only) | | (the server keeps the phases itself) |
| leader at state 6 / 7: 0xbb8 {stage, idx} HUB stage begin (0x78b3f0), 0xbc2 {time, stage-1} stage cleared (0x78b800); "am I the leader" = 0x718210 | | the run leader's `uc.phase {phase:'accept', slots}` (UC_HUB_NeedAccept's Accept) |
| HUB hit: 0xbbc {group, slot, jumpFlag} (0x78b6a0, from the collision 0x7135f1); a red HUB from a jump also sends 0x79b990(10000) | 0x7137b0 | `uc.event {kind:'hub' or 'jump', slot}` |
| barrier / Main HUB hit (state 10): 0xbc6 {group, slot, time} (0x78bab0) | | `uc.event {kind:'barrier' / 'ufo' / 'main', slot}` |
| server 0xbbd (0x4f7a60) {group, slot, exp, charId, userId}: 0x718370 -> party member (0x5001c0) +0x44 += 1 -> PARTYUC (0x6850d0); "Hub Exp %d"; UC_Hurry3 at 80 | | push `party.uc.event {kind, slot, by, who, counts}` |
| server 0xbbe (0x4f7aa0) {.., success, exp, .., mito}: success -> 0x716aa0 (UC_HUBSuccess1, state 7 in 3 s), else 0x7163d0; "Hub clear Exp/Mito %d" | | phase `hubsCleared` |
| server 0xbc3 (0x4f7ff0) {clock, attempt}: jamming clock +0x34, 0x719ae0 Main HUB stage | | phase `main {attempt, clock, barriers}` |
| server 0xbc7 (0x4f8050) {group, slot, exp, charId, userId}: 0x718670, PARTYUC +1, "Boss hub. Exp %d", my own -> HUD +0x358 += 1 | | event `barrier` |
| server 0xbc8 (0x4f80d0) {success, exp, destroyer}: success -> 0x718590 (UC_HUBSuccess2, state 11 in 3 s), UI effect 0x1d + sound 0x4ed3 | | phase `mainCleared` |
| server 0xbc9 (0x4f7fd0): 0x716b50, state 7 at once (stage over); 0xbc5 (0x4f8030): 0x716410 | | phase changes |
| HUV wave: the leader asks for the wave's HUVs (0x718e40: 0xc2 + 0xbe with 1 200 000 ms); server 0xc0 HuvHit -> member +0x44 += 1 | 0x5012a0 | `uc.wave {wave, huvs}` (first report counts), `uc.event {kind:'hit', slot, damage}`, push `party.uc.huvs` |
| reward 0xbcc (0x4f81f0): "Reward Exp/Mito %d", 11114 "Undercity 120% item has been applied." | | push `party.uc.result {cleared, seconds, attempts, rows, driver}` |
| boss result 0x100 (0x50e1a0) "Undercity bosshuv success / fail" | | (in the result) |
| in the Undercity LEAVE and MEMBERBAN are disabled | 0x61bff5 | `PartyWindow.Refresh` |
| friend list location: channel >= 100 -> 10809 "Undercity" | 0x607f50 | the private channel is index 100 + room |

**PARTYUC 2880000 belongs here** [exact]: 0xbbd and 0xbc7 add 1 to the destroying member's +0x44 and redraw PARTYUC
(0x6850d0); 0xc0 HuvHit adds 1 per hit; 0x717400 (state 4) resets the counters. So PARTYUC shows one running counter per
party member (P1 = members[0]): HUBs + red HUBs + barriers + HUV hits. `UndercityData.Score.PartyUc` (offline and online).
SOCIAL.md's "party chase only" reading was incomplete.

**Portal fee shown to the members** [exact]: 10772's %d is chosen by net+0x3b4ec (the city number, 1-5): 1 -> 600, 2 ->
1200, 3 -> 2100, 4 and others -> 4200 (0x68797c, jump table 0x687ac0; the KR comment column lists the same per city).
The remake keeps its single fee of 2000 Mito (`UndercityData.EntryFee`, economy.js `UC_ENTRY_FEE`) - see "Left".

### Remake rules (server: `src/undercity.js`)

- `uc.request`: a party of 2-4 (the requester's live party), everyone online in the requester's city, nobody in the
  Battle Zone / another run / a party patrol, the requester has the fee. Members get 15 s (+2 s grace); a refusal, a
  timeout or a member going offline cancels for everyone (10943). A member short of the fee who accepts cancels too.
- Start: AI partners (Rio, Duke, Mika; level = leader's level + i - 1) fill the slots up to four, so the Main HUB always has
  15 barriers (5 per player beyond the first). The members are moved into the private channel `<city>-<100 + room>`: the
  world relays their cars to each other (the remote players in the stage) and nobody else sees them.
- Phases and clocks (the client's timings, scaled by `DC_UC_TIMESCALE` / `ucTimeScale` for tests; the jamming clocks are
  not scaled): `ready` 24.8 s (UC_Ready 19.2 + uc_startani 5.6) -> `accept` (UC_HUB_NeedAccept, 60 s or the run leader's
  Accept) -> `countdown` 6 s -> `hubs` (300 s, ends early when every reported HUB slot is down) -> `hubsCleared` 3 s ->
  `main` x up to 3 (10.6 s briefing + 180 s, +10 s per UFO) -> `mainCleared` 3 s -> `wave` 1-4 (240 s shared) -> `boss`
  (120 s + 6 s per red HUB taken) -> `ending` 6 s -> result; a clock running out -> `failed` 3 s -> result.
- HUB slots: the run leader's Accept carries the slot numbers its stage placed (the layout is the same on every client:
  red HUBs on the posts, the towers of the road map, UNDERCITY.md); slots 0-9 are red HUBs (jumps). Each slot falls once.
- HUV waves: a group slower than 60 s costs the next one half the overrun (at most 20 s): `party.uc.phase {clockChange}`
  -> UC_HUVTimeReduce. Result rows carry the badges `boost120` / `set`.
  Barriers 1..15 after half the briefing; the Main HUB only when all barriers are down (the shield).
- HUVs are party HUVs (TRAFFIC.md section 14): the run leader's client runs the wave and sends `uc.huvpos {wave, cars}`,
  relayed as `party.uc.huvpos`; the other members spawn the same rows and show them as dead-reckoned copies (a new run
  leader takes them over).
- HUV health: each client spawns the wave locally and reports it; the first report sets the shared health (the client's formula:
  30 + level / 2, bosses 200 + 2 x level, x (1 + 0.5 x share of HUB slots left)). Hits are capped at 45 (the client's
  clamp) and 5 per second per member; the kill counts for the driver who finishes the HUV. A wave nobody reports in 10 s
  is skipped (no HUV could be spawned).
- AI partners act on the server on the offline partners' clocks (HUB 7-11 s, barrier 14-20 s, 10 HUV health 4-7 s).
- Rewards: every member is paid by economy.js's `undercity` rule for their own counts (capped by UC_MAX) with the clear
  bonus for everyone on a cleared run, the fee taken out of the run's Mito, `rewardExtras` (the drop of a cleared run,
  drinks, the party bonus); `lastClaim.undercity` is set (a solo claim right after is refused). AI rows are shown, not paid.
- PLAYEXIT (`uc.leave`): that member's run fails and they are paid for their counts (`party.uc.result {quit:true}`); the
  others go on. A member who disconnects or leaves the stage's channel drops out unpaid. The first member left takes over
  the run leader's reports. After UCRESULT's 7 s the members are moved back to the channel they came from.

### Client (`UndercityParty.cs`, `UndercityStage.Online.cs`)

- Online the entrance's Accept checks the party (UC_Err_* in QUESTDIALOG) and sends `uc.request`; WAITPLAYER and the
  UC_Emer_WaitParty alert for the requester, REQUESTINVITE for the others (pre-stage dialogs on a small `UndercityHud`).
- `party.uc.start` gives up a running chase / patrol (UC_NeedMito) and starts `UndercityStage.BeginOnline`: same stage,
  screens and scripted steps; each driver starts on their own start post (0x718260: member n on Start n+1); the loops wait
  for the server's phases; touches are reported and HUBs fall on the server's events for whoever took them; HUV health
  and kills follow the server; UCRESULT shows the server's rows; the online session's channel switch draws the members.
- Offline: no change to the run (AI partners); the offline party's AI drivers (PartyWindow) take the first slots.

## 2. Party patrol

### Client [exact]

- Rows: `party_move` (0x20) / `party_move_ROO` (0x21) patrols of Instant_Client.tdf: Officer Amy (level 120, 80/160 posts)
  and Eugene's Treasure Hunt / World Patrol (level 80, 160 posts); column 12 is 0 EXP per post, 13 Mito per post, 14 the
  completion EXP.
- Packets: 0xc24 start (0x4f4210: four values into the patrol object 0x6ebab0, 11001 / 11002 alerts, UI effect 0x35),
  0xc26 post reached (0x4f42e0: +2 post index -> -1, +0xa EXP "Exp. %d", +0x1c seconds -> the timer +0x10, sound 0x4ece),
  0xc27 member posts (0x501d20: +2/+6 member id -> member index 0x500220, +0xa post count -> 0x61bf60 "Post %u",
  +0xe seconds: the difference to the running timer is the bonus time +0xa4, sound 0x4ece SID_PVP_RACE_LAPS),
  0xc1e result (0x4f4430: count at +6, records of 0x3c bytes -> 3890000 via ui+0x3e0, 0x545860/0x545880/0x545c40/0x5458a0).
- PARTYPATROL 3880000: TIME1ST/2ND minutes, TIME3RD/4TH seconds, COMMA, 5TH-7TH thousandths (digit art TIMENUM01-10 =
  0-9), BASE2ND + BONUSTIME01-10 (tens) / 11-20 (ones) the bonus seconds.
- 3890000 columns: Party Member, Post Cleared (POST), Quick Patrol Post (RESCUE), Time Bonus (item slots / NOITEM),
  Gained EXP; Play Time (CLEARTIME min / sec), Time Left (WAITTIME).
- Party bar COMBOCOUNT = "Post %u" (sum of the members' posts), list POST01-04 = "Post %u" per member (0x61bf60, 0x61c0b0).
- Texts 11156 "Party Patrol Start!", 11157 completed, 11158 failed, 11159 "Party Leader has quit the Party Patrol.",
  server 20312-20315 (busy / not ready / not during a party patrol), 11120 no Auto-Drive.

### Remake rules (server: `src/partyplay.js`)

- `patrol.party.start {row}`: the party leader only (20022), a party of 2+, everyone in the leader's city and not in the
  Battle Zone or the Undercity (20314), nobody already in a party patrol (20312); the row must be a `party_move` row for
  the leader's level (without the tables: `posts` from the client, 5 EXP per post).
- The clock: 300 s at the start, +15 s per post (the bonus time), at most 900 s ahead; at 0 the patrol fails (11158).
- `patrol.party.post {index}`: each post of the loop counts once, for the member who reached it, at least 3 s apart per
  member (economy.js's patrol rule); the push carries the member's count, the total and the clock.
- The end: all posts (complete), the clock, or the leader quitting (`patrol.party.quit`, 11159 for all). Every member is
  paid column 12 EXP + column 13 Mito per own post, and column 14 EXP each on a complete loop (`rewardExtras`, no party
  bonus: not a HUV mode). A member who quits (or leaves the party) is paid for their posts and drops out.
- Client: in a party online only the leader is offered the party rows; the leader's Accept sends the start; everyone runs
  the loop from the post nearest to them; posts are reported and marked done on the server's push; the timer, "Post %u"
  and 3890000 with every member's line. Offline or outside a party, party rows run solo (one line on 3890000).

## 3. Party bonus [exact, 0x4fe050]

- Only with the server flag net+0x288 & 0x1000 and more than one member. For every member (including me):
  level <= globalcontent `PartyBonus_Level` (US 80) counts "low", above it "high". Step 5 % (10 % with flag 0x800).
  EXP % = step x low, Mito % = step x high. Chat: both -> 10998 (EXP %, Mito %), only low -> 10996, only high -> 10997
  (its US text says "Exp." too). The strip 3870000 (BASE / DISABLED, EXP, MITO) through ui+0x408 (UI type 8) or ui+0x404
  (ADV, 0x5e5690 stores the two values, 0x5e6670 shows it next to the main bar). Recomputed on 0xf2/0xf3/0xf4/0xf5/0xf8.
- Server: `setPartyBonus` provider in `workshop.rewardExtras` (like the event bonus): kinds `chase` and `undercity`
  (the HUV game modes), the session's live party, 5 % steps, `PartyBonus_Level` 80. `party.bonus` returns the values.
- Client: `PartyWindow.UpdateBonus` on every party update (online only; offline there is no party bonus): the chat text
  when it changes and the 3870000 strip (placed left of the party bar; its runtime position is the main bar's).

## 4. Party combo [exact for the packets, inferred for the total]

- 0x514350 sends the player's speed combo to the party (only in a party of 2+); server 0x23c {userId, member combo, party
  total} -> member +0x40 and net+0x3b614, 0x242 the total alone; the bar's COMBOCOUNT prints the total ("%u"), the list's
  MEMBERCOMBO is filled but stays hidden. The client does not sum: the server does.
- Remake: `party.combo {count}` (PartyWindow polls DriftCombo.Count, at most 4 a second); the server adds each rise of a
  member's chain (a new chain adds from its start) to the party's total, pushes `party.combo {name, combo, total}`; the total
  is also in `party.update`'s `combo`. The SPEEDCOMBO PARTYCOMBO / PARTYNUM display of a member's combo is not converted.

## 5. Left

- The per-city portal fee (600 / 1200 / 2100 / 4200) - one fee of 2000 is kept everywhere (economy.js, UndercityData).
- The Virtual HUV invite mode of REQUESTINVITE; the per-event EXP lines ("Hub Exp %d"). (Done this round: Rush Time's
  auto-refusal 11146, 11114 and the 120% / SET badges, the HUD's +NN = my barriers, UC_HUVTimeReduce - UNDERCITY.md.)
- Party patrol: the Quick Patrol Post / Time Bonus columns of 3890000 (0 / no item), 11001 / 11002, the ROO rows.
- The friend list shows the private channel as "Channel 1xx - <city>": `social.js` location() could print 10809
  "Undercity" for channel indexes >= 100 (hook for the owner of social.js).
