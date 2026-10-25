# Social layer: party, friends, notes, community, crews and channel battles

What DriftCity.exe does, and how the remake converts it (client `DriftCityOffline/Assets/DriftCity/Runtime/Game/Social/`,
server `DriftCityServer/src/social.js`, `mail.js`, `teams.js`, the party code in `world.js`, crew rooms in `race.js`).
[exact] = read from code or data, [inferred] = interpretation, "remake rule" = the original server decided and the remake's
server keeps it simple. The detailed RE notes with packet tables and control maps are in `DriftCity_RE/social/`
(`party_friends.md`, `notes_community.md`, `crews.md`). Texts are `localize\globaltext.xlt` ids (US column).

Many handlers in this layer aren't Ghidra functions (0x614680, 0x61c810, 0x66d8c0, 0x67d3a0, 0x58a070, 0x6117b0,
0x6747f0, 0x5448f0, 0x540240); they were read from `text.asm`.

## 0. Which UI the exe uses

Every party/friend/note handler branches on the UI type `game+0x24c & 0x18` (the setting COMBO.md describes) [exact]:
- ADV (0x10, the main bar 3010000 the remake uses): COMM 3970000 + friend manager 4000000 + NOTE 4010000 / 4020000 +
  MY 3980000 (crew). The community class is vtable 0x872090, init 0x6117b0, UI manager slot +0x1f0.
- Basic (main bar 3020000): the old friend window 2930000, inbox 2960000, saved box 2970000, read 2950000.
- Both: the send dialog 2940000 (class 0x873eb8).

The remake uses the ADV set. Not used: 2930000 / 2950000 / 2960000 / 2970000 (basic UI), ADDFRIEND 3710000-3730000 (the
Korean Hangame "invite an outside friend" event: server text commands invitef_1..3, handler 0x93 at 0x502d53, buttons
open hani.hangame.com), PARTYUC 2880000 (per-member HUV hit counter of the party chase, packet 0xc0 at 0x5012a0).

## 1. Party

**Client** [exact unless noted]
- At most 4 members (vector net+0x3b584, 0x48-byte elements, loops stop at 4; server text 20219). The leader is
  members[0]; MEMBERBAN shows on the other rows only when my id is members[0]'s (0x61c28c). No leader transfer code:
  after a leave the next member becomes leader [inferred].
- Invite sources: COMMAND 700006 (0x5bdef0), the friend list's PARTY (0x60e990), `/invite name` (10752). Client
  checks before 0xe4: Virtual HUV registration 10944, HUV chase 11061, auto-drive 11129; `/invite` also the Battle Zone
  11111.
- The server's 0xe5 reply decides the dialog (0x4fa280): target in no party -> PARTY 410000 (0x61def0; party name
  defaults to 10395 "Party", message 10396; the name field is enabled only while I have no party, labels 11266 / 11268;
  empty name 10470, bad word 10471). Target in my party -> 10748; in another party while I'm in one -> 10469; in a party
  while I'm not -> 430000 (10416 + 10417), whose OK sends the join request 0xf6.
- The invitee (0x4fa3a0 / 0x61e100) auto-refuses with 0xf1 when the inviter is blocked or the "refuse party requests"
  option is set (10412), in the Battle Zone (10413), on auto-drive (11130); else 420000 shows 10415 "%s of %s Party has
  requested..." with the leader's message. Decline sends 0xf1 with 10472; the inviter sees 450000 with the text.
- Join requests reach the leader as 440000 (10524); decline is 0xf1 with 10473.
- Updates: 0xf3 whole party (10825), 0xf2/0xf7 joined (10826), 0xf4 left (10827/10828), 0xf5 kicked (10829/10830); a
  party of one or less shows 10831 (no disband packet).
- Bar 2870000: party name, member count "%u", party combo total (packets 0x23c/0x242) or "Post %u" in a party patrol;
  TOGGLELIST = F12 (J=123) on bar and list. Bar and list hide with one member or less.
- List 2890000 per row: "Lv%u", "(V%u)" car grade (car +0xc), name, portrait by character type (8 portraits
  CHARIMG1-8 moved onto the row), P1-P4. MEMBERCOMBO is filled but stays hidden, MEMBERPOS is never filled,
  PARTYLEADER never bound, OFFLINE1-4 force-hidden (0x61c750). In the Undercity LEAVE / MEMBERBAN are disabled (0x61bff5).
- Entering the Battle Zone in a party asks 10171/10067 and leaves the party (0x561510); auto-drive refuses 11122.
- Party chat: prefix `&` (table 0x5b7a67) or "/party " (10751). Minimap P1-P4 are icon types 26-29 (0x5f757c).
- Party bonus 3870000 (0x4fe050): only with server flag net+0x288 & 0x1000 and more than one member; members at or
  below `PartyBonus_Level` (globalcontent, US 80) add 5% EXP each, members above add 5% Mito each (10% with flag 0x800);
  chat 10996-10998 "...in HUV gamemode". Recomputed on join / leave / kick / 0xf8 level updates.
- 10812 ("car level difference") is not a party rule: it's the PvP find-friends join (0x593b0e).

**Remake**
- Server (`world.js`): `party.invite {name, party, message}` -> push `party.invite {from, party, message}`;
  `party.accept`, `party.decline {message}` (push `party.declined`), `party.request {name}` (push `party.request` to the
  leader), `party.answer {name, accept}`, `party.kick {name}` (leader only), `party.leave`, `party.info`; pushes
  `party.update {id, name, leader, members[{name, level, grade, avatar, city, channel, leader}]}` (leader first) and
  `party.notice {type: joined|left|kicked|disbanded}`. Refusals: block list / refuse option (10412), in a race room
  (10413), inviting from a race room (11111). The refuse option is `social.options {refuseParty}` (kept in sqlite).
- Client `PartyWindow.cs`: 410000-450000, the bar and the list with the client's visibility rules; F12 toggles the
  list (SocialHost). Offline the party is made of the offline game's AI drivers, who accept at once.
- Not converted: party combo total (no party combo on the server), party bonus (needs the HUV game mode; see 7),
  PARTYUC, the 0xf8 member updates (the server re-sends the party when a member re-enters a channel).

## 2. Friends

**Client** [exact]
- Packets: 0xe6 refresh, 0xe8 add {name}, 0xeb remove {name}, 0xef answer {id, accept}, 0xe2 block, 0xdf unblock; server
  0xe7 list (records 0x70 bytes: name, crew name, charId, status, location, level, userId), 0xee request received,
  0xef answer, 0xed delete, 0xe3/0xe0 blocked/unblocked, 0x113 login notice.
- Status: 'F' friend (location shown), 'C' "<Friend not approved>" (10545), 'R' "<Friend approval request>" (10546),
  'D' "<Deleted>" (10547), 'B' blocked.
- Location (0x607f50): "Offline" (10504), "Channel %2d - <city>" (or "Gate %2d - ..."), Driver Dome, Undercity, the
  PvP arenas, "Spectating: %d", "Channel Battle in progress.".
- 4000000: 10 rows with pages, friend / deny tabs, sorting by column. Add = MSGBOX 3230000 (10305) -> 0xe8; Remove =
  10537 / confirm 11192; Deny = 11179 (11182 yourself, 11183 already); Deny-cancel = 11180; Whisper writes "*Name "
  into the chat input; Party sends 0xe4; New Message opens 2940000 with the name.
- Request: the receiver gets message box type 7 (name + 10569) Yes/No -> 0xef; the requester sees 10810 / 10811.
  Delete notices 10833 / 10832, block 11274, unblock 11181, `/block` `/unblock` (11271-11273), login 11412 (option).
- No client-side limit (the server's 20127/20131/20132, 20341 are "list full").

**Remake**
- Server (`social.js`, table `friends(owner, other, status, at)`): `friend.list`, `friend.add {name}` (request; a
  crossed request becomes a friendship at once), `friend.answer {name, accept}`, `friend.remove`, `friend.block`,
  `friend.unblock`, `friend.locate {name}`; pushes `friend.request`, `friend.answer`, `friend.removed`,
  `friend.online {name, online}` (login/logout of friends), `friend.changed`. Remake rules: 100 friends, 50 blocked.
  A blocked driver's whispers are dropped silently (the sender still sees the echo), notes refused (11035), party
  requests refused (10412).
- Client `Friends.cs`: the 4000000 manager in the Community window. Offline: the AI drivers (Battle Zone rivals) are
  the friends, with a steady level and channel each.

## 3. Notes (mail)

**Client** [exact]
- Inbox and saved box are maps by note id (game+0x3b538 / +0x3b544). Note: id, sender id + name, type, status
  'N' unread / 'R' read / 'K' saved, title (never shown by the ADV UI), content (wchar[201]), attached Mito, an
  attachment text (`"%s %d %d %d %d %s %s %s %d"`), send time, read time.
- Packets: server 0x084 one note (type 0 stored; 100 friend notice, 300 event reward, 500 Mito gift), 0x5c9 read ack,
  0x5cb deleted (11031), 0x5cd saved (11032 / 11023), 0x5cf send result (1 sent 11033, 0 "%s does not exist" 11038,
  -2 11034, -4 10500, else "cannot receive" 11035), 0x5d1 attachment taken (11036). Client 0x5c8 read, 0x5ca delete,
  0x5cc save, 0x5ce send (one per receiver), 0x5d0 take attachment.
- NOTE 4010000 (0x610e10; refresh 0x60efa0 / 0x60f5b0): 10 rows per page, PAGE labels, LEFT/RIGHT +-10 pages; the box
  buttons show "(unread/total)"; SEND_DATE "%d-%02d-%02d"; FREE_TIME = time left: unread 30 days from sending
  ("%d Day(s)"), read 24 hours from reading ("%d Hour(s)" / "%d Minute(s)"), saved "Unlimited" (11082); NEW marker;
  "get_giftbox" attachments show the EVENT icon. Clicking a row opens 4020000 and sends 0x5c8 if unread. Delete sends
  one 0x5ca per checked row (11190 if any carries an item). Save moves checked rows to the saved box, which holds 10
  (11023). No client inbox limit.
- 4020000 (0x6138c0): REPLY opens 2940000 with the sender, DELETE (11185 with an item), clicking the item slot takes the
  attachment (0x60bca0 -> 0x5d0). One attachment block per note.
- 2940000 (0x6713a0 / 0x670c50): content edit TL=80; cost 50 Mito per receiver (1000 with an item, 0x6704a0); up to 10
  receivers separated by ','. Checks in order: empty content 11010, no receiver 11009, item with several receivers
  11012, Mito 11013, duplicate 11037, yourself 11021, bad name 10285, more than 10 11022.
- Main bar (0x5ecfc0): while an inbox note is unread NOTE_NOTICE1 fades in 0-1 s, holds to 3 s, fades out to 4 s, then
  the COMM button blinks. NOTICE1/2 and COMM call 0x5ed580 (toggle the community window).
- "...sent to your Note Box" (11722-11728) are created by the server [inferred].

**Remake**
- Server (`mail.js`, table `notes`): `note.list` (expires notes first; returns `now` for the client's period column),
  `note.read {id}`, `note.delete {ids}`, `note.save {ids}`, `note.send {to, content}`, `note.take {id}`; push
  `note.new {note}`. Same limits as the client; inbox kept at 100 (remake rule: the oldest read notes make room). The
  game's own notes come from `mail.system(accountId, text, mito)`: a welcome note with 1000 Mito on the first login
  (remake rule) and the Channel Battle capture reward. Player notes carry no items (not converted).
- Client `Mail.cs`: 4010000 / 4020000 / 2940000; the gift slot pays the attached Mito. `SocialHost` shows the main
  bar's notice the client's way. Offline the inbox holds the game's own notes (the welcome note); sending needs the
  server.

## 4. Community window and whispers

- COMM 3970000 [exact]: opened by ADV_COMM (F1), NOTE_NOTICE1/2 (0x5ed580; basic bar 0x5e7ea0). Default tab: Messages
  while a note is unread (0x5ed3f0), else My Crew with a crew, else the friends. 0x60a3a0 switches tabs; the hidden
  *_BTN controls (Tab key) cycle and skip My Crew without a crew; My Crew without one shows 10425. The remake offers
  the Crew Center from there (the original's is in the Driver Dome).
- Whisper [exact]: no `/w` in the client; prefixes `!` channel, `&` party, `*` whisper, `-` private channel, `@` crew
  (0x5b7a67). `*Name message` (name up to 15 characters, 0x5bcf60) sends 0x094; the echo shows 10474 "Whispering to
  [%s]: %s", the receiver 10475 and gets "*sender " in its input (reply to the last whisperer).
- MSGBOX 3230000 is a generic name prompt (add / block a friend 0x60e520, recommend, delete character, shops).
- Remake: `SocialChat.TryHandle` (called first by `OnlineSession.SubmitChat`) handles `*Name text`, `&text`, `@text`,
  "/party ", "/crew ", "/crew join name", "/invite name" (410000), "/friend name", "/block", "/unblock", "/note name
  text". The older "/w", "/p" commands keep working.

## 5. Crews

**Client** [exact]
- "TC" is the Team Center (`teamcenter` zone 5 in the Driver Dome); its NPC menu 590000 ("Operator Erina", 10522,
  0x6756e0) has Create/Delete Crew (enabled without a crew or for the Master), Enter Channel Battle and Spectate,
  each gated by a server switch (net+0x27c bits 1/2/4, else 10745).
- Create (2020000, 0x6753f0): fee and level from `localize\globalcontent.xlt` `TEAMMAKE_NEEDMITO` (KR/US 2500, others
  10000) and `TEAMMAKE_NEEDLEVEL` (20); the client shows (OK)/(Impossible) (10624/10625), the server enforces. Terms
  `Data\aggrement_teammake.txt` must be scrolled before the box can be ticked (10325). MAKE checks agreed 10330 -> URL
  (KR only) 10634 -> name checked 10331 -> mark 10332. Names 4-12 letters, digits, - _ (10602/10603) + word filter
  (10607); availability 0x353/0x354 (10328/10329). 10 built-in marks `Data\GameUI\teammark\01..10` (32x32 BMP); a
  custom mark upload in 3990000 is Master only, at most 4000 bytes (0x6107c0). Result 2030000 (0x6774e0): 10333-10335,
  master, name, grade, today's date.
- Ranks char+0x78: 1 Master, 2 1st Class, 3 2nd Class (10384-10387); new members join at 3. Rank popup 2790000
  (0x606500): TEAMPOS1/2 = 1st/2nd Class (0x374), TEAMPOS3 = make Master (0x352: target level >= 20 10849 hardcoded
  0x14 at 0x606692, online 10869); the old Master drops to 2.
- Join: click a player (COMMAND 700000's BADBOY button sends 0x367: 10163 / 10164 when the player has no crew) or
  "/Join Crew"; the Master gets 10020 and approves 0x368 / denies 0x36c (10016 / 10017 / 10021). Leave (WITHDRAW): the
  Master gets 10378, others 10379 -> 0x350 (10018/10019). Kick (BAN): 10388 yourself, 10389 confirm -> 0x36b (10023).
- Crew notices 0x36d types 1-11: delete requested/cancelled/deleted 10814-10816, joined/left/kicked 10817-10819, new
  leader 10820, promoted 10838/10839, deletion countdown 10821/10822, renamed 11305, login 11413 (option).
- Delete: 2040000 -> 10336 -> 0x34c; while pending (state 'C') 2050000 shows 10341, 10740-10742 and cancels via 10337 ->
  0x355. 7-day grace from 10317 and the terms file. Rename once (char+0xf08, switch Etc.ChangeTeamName, 0xce4).
- MY 3980000 (0x6117b0 bind, 0x615330 handler, 0x615170 sub-page, 0x614090 member list): tabs Board (a web page),
  Members, Leave, Crew Message, Manage Crew, Crew Mark; 6 member cards per page (level, name, rank, date, OFFLINE);
  Members shows DETAILVIEW (10725 offline), Manage shows UPDATE (rank popup) and BAN except on the Master; search is a
  name substring; the two filters sort by rank or name, ascending / descending.
- Crew grade (0x514530): points = rec+0xf0 + rec+0xfc/10; S with any channel capture or >= 500000, A >= 100000,
  B >= 20000, else C. Crew info elsewhere: the name tag 1950000, character info 10684/10685, the friend lists' crew
  column and mark. PVP 2010000 is only ID_PVP_GHOSTEXIT (spectator exit).

**Remake**
- Server (`teams.js`, tables `crews`, `crew_members`, `crew_channels`): `crew.info`, `crew.check {name}`,
  `crew.create {name, mark}` (level 20, 2500 Mito = the US column, unique name, mark 1-10), `crew.request {name}` (a
  member's or the crew's name), `crew.answer {name, accept}`, `crew.leave`, `crew.kick`, `crew.rank {name, rank}`,
  `crew.mark`, `crew.rename` (once), `crew.disband` / `crew.cancelDisband` (7 days, swept hourly), `crew.ranking`;
  pushes `crew.request`, `crew.notice {type, crew, name}`. Crew chat is chat scope `team`. Remake rules: 30 members;
  1st Class members may approve requests too; rejoin lock 24 h after leaving / being kicked; join needs an approver
  online. The crew name is sent with each remote player (`publicInfo.crew`).
- Client `Teams.cs`: 590000, 2020000/2030000 (the built-in marks next to the boxes; no URL row, as outside Korea),
  2040000/2050000, 2790000, 3990000 (the remake cycles the 10 built-in marks instead of uploading a BMP), the My Crew
  page (owned channels on the 24 cells: 4 cities x channels 1-6, a remake mapping). Offline: a one-driver crew under
  the same create / delete rules.

## 6. Channel Battles (crew battles)

**Client** [exact] (details, packets and control maps: `social/crews.md` section 4)
- 10 major + 10 semi channels each owned by a crew (packet 0x35d -> 0x678c70; "Owned by", 10168, at the channel's
  Mittron station; the owner earns station and replay fees, server texts 20268/20288/20350). 0x35d entries: channel,
  max level, max car grade, owner and challenger crews, battle leader, t1 (ownership taken) .. t4, flags.
- Schedule: 0x35e -> 0x35f next start (TC+0xb8); MAIN TEXT 10319 countdown, 10320 for 7200 s (0x675ea0). The channel
  tabs 2060000 (major rows TIME_1-10, the frame) / 2070000 (semi) / 2090000; panes NOTICE 2100000 / CHANNELINFO 2110000
  (10322 "%u Ch: %s" / 10324, owner, master, 10570 members, battle leader, rank, points, the channel's room list with
  10321 "(%uCrew)"); ENTERCHANNEL only while t2 < now < t4; BATTLESETUP for the owner's Master on a major channel
  within 24 h of t1; BATTLENOTIFY when the maps are public.
- Entry (0x675250 / 0x676a50): level / car grade limits; Master or 1st Class 0x370, 2nd Class 0x371; major channels 1-2
  ask 10553 first (a lost attack costs an equipped part); the defender's client sends 0x384(ch, 1) by itself. The client
  never checks the coupon: server texts 20077 (Challenge Coupon `i_n_00037`, "redeemed when the battle starts"), 20076
  (crew rank B+), 20075 / 20081, 20079 / 20080 (one attack per channel), 20078, 20184 (equal sides), 20085, 20086.
- Lobbies ATK 2130000 (+ ATKCOVER 2140000 READY -> 0x384(ch, 1), CANCEL -> 0x384(ch, 0)) / DEF 2150000: 8 member rows of
  the own side (MEMBERCLOSE for rank < 3: 10571 / 10240 -> kick 0x1d6), the queue = the channel's room list (op 0x1d0;
  READYn_1/2, marks, 10321), CHANNELNAME 10347, 10318, STATUSTEXT 10343-10346 from 0x386's round flags, REMAINTIME
  10072 / 10074 "%02u : %02u" (prepEnd / roundEnd of 0x386), HISTORY 0x390 (10348-10351), VIEWING, ESC 10326 (an attacker
  under 3 waiting members drops from the list) / 10327 (no waiting defender = the channel is lost).
- Map setup CHANNELSETUP 2240000 (two tracks, traffic IDLE/NORMAL/JAM/RANDOM = rand % 3, 11387) -> 0x38a; MAPNOTIFY
  2250000 (0x38e -> 0x38f, 10342).
- The race is a normal Battle Zone team race with room flag 0x8 (0x724120); the server names the winning side.
- Rounds (0x674290): the attacker has to win 3 in a row (Round 1, Round 2, Final); one loss ends the attack
  (10371/10372) and the next crew attacks (10364); three wins take the channel (10376/10377; defender 10368-10370).
  RESULT2 2200000 shows PERFECT when every winner placed ahead of every loser; ROUNDNUMBER 1420000.
- Part stakes: 0x391 BATTLEITEMGET 2220000 (12 part slots) / 0x392 BATTLEITEMMISS 2230000 (the lost part: name, 10352-10359,
  10360).
- Spectating: 0x387 -> 0x388 SELCH_VIEWING 3390000 (10201 / 10199 / 11261; 10200 when nothing runs), 0x38b, VIEWING 1970000;
  cheering 2170000 (Mittron Fireworks `i_n_00011` -> 0x394, 5 s cooldown, the cheered car's fireworks); replays 3410000
  (0x395 -> 0x396; 11259, 11258, price 1000 / 500 / 0 by channel, 11256 / 11260; Ghost Battle tickets `ch_ghost_NN` 0x397).
- Crew ladder: MY 3980000 class from record byte +0xef (1-3 S, 4-10 A, 11-20 B, 21-30 C, 31-40 D, 41-50 E, else R) and rank
  +0xeb ("--" without), LADDER TABTEAM 3960000 ("T" query 0x518b00); the server ranked.

**Remake** (server `src/channelbattle.js` with `teams.js` and the crew parts of `race.js`; client `Social/ChannelBattle.cs`,
`TeamBattle.cs`, `Teams.cs`)
- 20 battle channels: battle channel i is the world channel `${cras|moonpalace|koinonia|oros}[i % 4]-${floor(i/4)+1}`
  (major 0-9 "Channel n", semi 10-19 "Semi n"), so a battle is raced in that city channel like the original's server
  channels; the client moves to the city channel (same city) or says where to travel.
- Schedule (remake rule): env `DC_CB_SCHEDULE` / opts `cbSchedule`: `daily@HH` (default `daily@20`), `weekly@D@HH`,
  `always` (a window every UTC day), `none`; 2-hour windows. Outside a window a crew room race is a friendly match.
  `crew.table` = 0x35d + 0x35f (owner / holder / attacker, since, setup deadline, window, maps, queue, room, viewers).
- Entry / queue: `crew.enter {channel}` (subscribes to `crew.lobby` pushes; the owner (or a vacant channel's first crew)
  joins / hosts the channel's room on the even side; the head of the queue on the odd side); `crew.ready {channel, on}`
  (0x384) queues an attack: Master / 1st Class (20081), a Challenge Coupon held (20077; spent from the queuer, else any
  attacking racer, when the crew's first round starts), crew rank B+ to attack an owned major channel (20076; semi and
  vacant channels open to every crew), one attack per channel per window (20079). Other queued crews wait in the ATK
  lobby and get `crew.turn` when the odd side frees up. Prep 60 s / round 10 min are shown (REMAINTIME); the room
  host's client starts a round once both sides are equal and ready after the preparation.
- Rounds: as before (`teams.battleResult`, 3 straight wins, a draw for the defender, 50000 Mito note for 10552); a lost
  attack ends it (the crew leaves the room, can't requeue this window), a capture makes the old owner leave and the new
  owner defend (sides swap, `races.swapCrewSides`); chat notice 20210 / 20289 "N Channel Tournament completed" on a
  capture and when a window closes (HANDLERS.md 0x1da hook).
- Vacant-channel rule (remake): the crew on the even side holds a vacant channel and defends it; if nobody captures it
  before the window closes, the holder takes it. An owner whose racers all leave the room while a challenger is in it
  loses the channel (10327): the challenger becomes the holder.
- Map setup: `crew.setup {channel, maps: [{track, traffic}] x 2}` by the owner's Master on a major channel within 24 h of
  the capture (20085 / 20086), tracks of the channel's city; `crew.maps` (0x38f). Round 1 and the final run on map 1,
  round 2 on map 2; without a choice a random track of the city's pvptrackinfo (11387). Traffic is stored and shown only.
- Part stakes: major channels 1-2: every attacker who lost a round loses one random fitted performance part; the parts go
  to the defending Master by note with the part attached; pushes `crew.items {kind: get | miss, parts}` open
  BATTLEITEMGET / BATTLEITEMMISS.
- Spectating: `crew.spectate` (0x388) lists the channels' rooms; a row observes the room through race.observe (the PvP
  spectate API; same city channel); `crew.cheer {target}` spends Mittron Fireworks (5 s apart) and pushes `crew.cheer`
  to the room (a chat line, the sound and the item effect on the cheered driver).
- Replays: every round is kept as a result record (`crew_replays`, 400 newest), `crew.replays {page}` (8 a page),
  `crew.replay {id}` charges 1000 / 500 / 0 Mito by channel, the owner's Master gets the fee by note (20350); the client
  shows the round's result board (ghost replays and Ghost Battle tickets are not rebuilt).
- Crew ladder: the place in the ranking (captures, then points) -> class S/A/B/C/D/E/R; `crew.info` / `crew.ranking` carry
  `ladderRank` / `ladderClass`; MY 3980000 shows them; `ChannelBattle.OpenCrewLadder()` fills LADDER TABTEAM 3960000.
- Client screens: TC MAIN schedule line (live countdown), the channel tabs and panes, entry with 10553, the ATK / DEF
  lobbies (members, queue, round text, REMAINTIME, HISTORY, READY / CANCEL, kick, ESC 10326 / 10327), CHANNELSETUP,
  MAPNOTIFY, BATTLEITEMGET / MISS, SELCH_VIEWING, the cheering panel (while spectating a crew room), the replay list,
  the crew ladder tab. The lobby tracks the room from `race.room` itself and hides WAIT16 while it is up (hook below).
  Offline: AI crews hold every third channel, the window is always open and Enter opens the Battle Zone team room with AI
  drivers (the earlier stand-in).
- Not converted: station fees (Mittron station ownership), ghost replays / Ghost Battle, the battle leader field, channel
  level / grade limits (none set), the traffic setting in the race itself, ROUNDNUMBER 1420000.

## 7. Hooks for other systems (what they would need)

- Party patrol (MissionCommon / PatrolMission): runs solo today. With a party it would need: the party's members and
  leader (`PartyWindow.Members`, `IAmLeader`), the leader starting the patrol for all (a server message
  `party.patrol {row}` relayed to members; 11156 start, 11159 leader quit), a shared post counter (0xc27 "Post %u" on
  the bar's COMBOCOUNT and the list's POST01-04) and the result 3890000 with up to 4 lines (names, posts, EXP).
- Party bonus: `PartyWindow.Members` levels against `PartyBonus_Level` (US 80): 5% EXP per member at or below it, 5%
  Mito per member above it, only for HUV game modes (the server would apply it in `economy.claim` for chase rewards).
- Undercity online party (`UndercityStage.cs`, offline with 3 AI partners): needs (a) an entry point
  `UndercityStage.Begin(session, partners)` taking the party instead of AI: e.g. `Begin(session, PartyWindow.Members)`
  with real members replacing `MakeParty()`; (b) the client's checks UC_Err_PlayTogether (party of 2-4) and
  UC_Err_SameCity (all members in this city: `Member.City`); (c) a server stage room (like a race room) that moves the
  party's members together, relays the WAITPLAYER answers, HUB / barrier / HUV-hit events (PARTYUC counters) and the
  shared score, and pays `UndercityData.Reward` per member; (d) remote members drawn as RemotePlayers in the stage.
  `StepPartners` / the AI partners would stay for missing slots.

### Channel Battle hooks (lines for the owners)

| where | line(s) |
|---|---|
| `Game/PvpWaitRoom.cs` `OnServerRoom` (first line after the null check) and `OnRaceOver` (before `Show(r)`) | `if (Social.ChannelBattle.LobbyView && FromServer(info).Crew) return;` resp. `if (Social.ChannelBattle.LobbyView && r.Crew) { racing = false; return; }`: the ATK / DEF lobby stands in for WAIT16 (today ChannelBattle hides WAIT16's GameObject while its lobby is up) |
| `Game/Ladder.cs` Open / hook | `ui.On("ID_LADDER_RANKTAB_TABBTN5TH", _ => Social.ChannelBattle.OpenCrewLadder());` (the crew tab 3960000; check which TABBTN is the crew tab in the frame art) and close it in `Ladder.Close()` with `Social.ChannelBattle.CloseLadder();` |
| `Game/PvpSpectate.cs` | `public static List<Driver> Order => viewer ? viewer.Order : new List<Driver>();` + `public List<Driver> Order => order;` in Viewer (PickEm reads the field by reflection until then) |
| `Game/Social/SocialCore.cs` | nothing: `crew.*` pushes already reach `Teams.OnPush`, which forwards crew.lobby / crew.turn / crew.items / crew.cheer to ChannelBattle |

## 9. Round 4: the basic bar's windows, custom crew marks, ghost replays

- **Basic UI windows** (`Social/BasicBarSocial.cs`) [exact controls; data shared with the ADV windows]: MYFRIEND_LIST 2930000
  with the Friends / Crew / Users / Block tabs over LB (grade, name, crew, location), WHISPER, PARTY, FRIEND_ADD (add, or
  block on the block tab), FRIEND_REMOVE (remove / un-block, 11192 / 11180), REFRESH; MYFRIEND_MSGBOX 2960000 / SAVE_MSGBOX
  2970000 (10 rows: check box, NEW mark, sender / date / start of the note beside it; pages, DELETE, SAVE (11032), CB_ALL,
  SEND -> 2940000, the box tabs); MSGREAD 2950000 (REPLY to player notes, DELETE). Users tab: online `online` (the server's
  drivers), offline the AI drivers. Section 0's "not used" is superseded.
- **Custom crew mark** (`Social/CrewMarkUpload.cs`, teams.js `crew.markImage` / `crew.markImageGet`, table `crew_marks`):
  MYTEAM_MARK's LOADFILE takes a picture (`CrewMarkUpload.ImportHook` = the car-visuals image import when it is there, else
  crewmark.png / .jpg / .bmp in the save folder), fits it to 32x32, checks the 4000 bytes (0x6107c0) and uploads it (Master
  only, 10383); the server keeps a 32x32 PNG; MY 3980000 and 3990000 draw it over the built-in mark. Without a picture LOADFILE
  still steps through the 10 built-in marks. Other places (name tags, friend lists) still show the built-in mark (hook below).
- **Ghost replays / tickets**: PVP.md 14.2 (replays play the recording) and 15.5 (ch_ghost_NN tickets with a bought replay,
  `crew.ghost`). The "not converted: ghost replays / Ghost Battle" and "ROUNDNUMBER" lines of section 6 are done.

Hook: `SocialUi.ShowMark(v, id, mark)` callers that know the crew name may add `CrewMarkUpload.Show(v, id, crewName)`.
