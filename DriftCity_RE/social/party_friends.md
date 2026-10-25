# Party & friends layer (DriftCity.exe)

Image base 0x400000. [exact] = read from code/data, [inferred] = interpretation. Text ids = localize\globaltext.xlt
(US text quoted). Control ids = dialog id + index (numbering from `Harness ids 0 full <dlg>`, saved in SOC\ids_full.txt;
NB the k_ID_*.txt order is NOT the index order: e.g. 2890001-2890006 are unnamed). "visible" = control flag +0x3c bit 2,
"enabled" = bit 4. Most dialog handlers here are not Ghidra functions; they were read as disassembly (SOC\fa.sh + ann.py).

Globals: net/char object = 0x512ad0()/0x780510() ("net"); UI root = 0x4386b0()+0x160 ("ui").

## 1. Party

### Data [exact]
- Party member vector: net+0x3b584 (begin) / +0x3b588 (end), element 0x48 bytes; count = (end-begin)/0x48.
  Loops stop at 0x120 = 4 members (0x61c0b0, 0x61c3d0). Server text 20219 "The number of group members has reached the
  maximum." -> **max 4**.
- Member element (built by 0x503be0 from packet record of 0x3e bytes = u32 userId + Info58):
  +0x00 u32 userId (compared with own u16 id net+0x3b3ec), +0x04 Info58 (+0x04 u32 charId lo, +0x08 u32 charId hi,
  +0x0c wchar name[21], +0x36 u16 character type 0..7 (portrait), +0x38 u16 level, +0x3a u32 car grade),
  +0x40 u32 member drift combo, +0x44 u32 HUV hit count (party chase).
- Info58 ("my info", 0x4f03a0 / inline at 0x5be2fa): u32 id, u32 id2, wchar name[21], u16 charType (char+0x36),
  u16 level (char+0x38), u32 carGrade (= current car +0xc, 0x6cab40()+0xc; PVP.md: car +0xc = grade).
- net+0x3b590 party name (wchar[0x40]); net+0x3b610 party id; net+0x3b614 party combo total; net+0x3b560 "registered
  for Virtual HUV" flag.
- Leader = members[0] [exact for the kick rule, 0x61c28c..0x61c2c0]. No leader transfer code on the client; after a
  leave the next element becomes [0] [inferred].

### Client -> server packets [exact]
| op | size | builder | body |
|---|---|---|---|
| 0xe4 | 0x30 | 0x79c2e0/0x795c50 | +2 u32 targetUserId, +6 wchar name[21] (zeroed when inviting by id) - "invite" request |
| 0xf0 | 0x148 | 0x784370 | +2 u32 targetId, +6 u32 (net+0x3b4dc), +0xa Info58 me, +0x44 wchar partyName[30], +0x80 wchar msg[100] |
| 0xf1 | 0x10c | 0x784560 | +2 u32 inviterId, +6 u32, +0xa Info58 me, +0x44 wchar msg[100] (refusal; text is composed by the refuser) |
| 0xf2 | 0x44 | 0x784710 | +2 u32 inviterId, +6 u32, +0xa Info58 me (accept invite) |
| 0xf4 | 6 | 0x784be0 (via 0x512da0) | +2 u32 my userId (leave) |
| 0xf5 | 6 | 0x784d50 | +2 u32 member userId (kick) |
| 0xf6 | 0xa8 | 0x7848a0 | +2 u32 target, +6 u32, +0xa Info58, +0x44 wchar msg[50] (const L"가입시켜도") (ask to join target's party) |
| 0xf7 | 0x44 | 0x784a50 | like 0xf2 (leader accepts a join request) |
| 0x236 | ? | 0x79a4a0 | +2 u32 partyId (sent after 0xf3) |

### Server -> client packets [exact] (handler table DriftCity_RE\opcodes.txt)
| op | handler | body / behaviour |
|---|---|---|
| 0xe5 | 0x4fa280 | reply to 0xe4: +2 u32 targetId, +0xe wchar targetName, +0x38 i32 targetPartyId. partyId<0 -> 410000 create dialog (0x61def0). Else if I am in a party (count>1): same party -> 10748 "Already in a party.", other -> 10469 "%s is already a member of another Party.". Else -> 430000 (0x61e4c0) |
| 0xf0 | 0x4fa3a0 | invite received (layout = client 0xf0). Inviter name on my block list (0x60b240) -> auto 0xf1 with 10412(my name); else 0x61e100 |
| 0xf1 | 0x4f3070 | refusal: 450000 with the free text at +0x44 |
| 0xf6 | 0x4f3040 | join request (+2 id, +0x12 name) -> 440000 (0x61e5a0) "10524 Allow %s to join the Party?" |
| 0xf2, 0xf7 | 0x5046f0 | member joined: +2 u32 userId, +0xa Info58 -> add, 10826 "%s has joined the party." |
| 0xf3 | 0x5047d0 | full party: +6 u32 partyId, +0xa wchar name (0x40), +0x46 u32 count, +0x4a records 0x3e (u32 userId + Info58). Size 0x88+(n-1)*0x3e. Resets combo total, 10825 "You've joined a party." if count>1, sends 0x236 |
| 0xf4 | 0x501570 | 6 bytes, u32 userId left: self -> 10827 "You've left the party."; other -> 10828 "%s has left the party."; if <=1 left -> 10831 "Party has been disbanned." |
| 0xf5 | 0x501950 | kicked: self 10829 / other 10830, then same <=1 -> 10831 |
| 0xf8 | 0x501c90 | 0xe bytes: +2 userId, +6 level, +0xa carGrade -> member update (refreshes party bonus when level crosses 51) |
| 0x23c | 0x4ed280 | 0x22 bytes: +2 userId, +0x1a member combo (-> member+0x40), +0x1e party combo total (-> net+0x3b614) |
| 0x242 | 0x4eb880 | 0xa bytes: +6 party combo total |
| 0xc0 | 0x5012a0 | HuvHit: member(+2 u16 userId)+0x44 += 1 -> PARTYUC counter |
| 0xc27 | 0x501d20 | party patrol: +2/+6 id, +0xa post count -> 0x61bf60 ("Post %u") |

### Dialog flows [exact]
- Party dialogs are owned by the party manager ui+0x13c (handlers bound at 0x5be510): 410000->0x5bda20,
  420000->0x5be450, 430000->0x5bdbc0, 440000->0x5be2c0, 450000->0x5bd880.
- **Invite** sources: COMMAND 700006 PARTYJOIN (0x5bdef0), friend list PARTY buttons (0x60e990 new, 0x6687c0 old), chat
  "/invite name" (10752, 0x5b57da..0x5b58d1). All send 0xe4. Checks before sending: 10944 (Virtual HUV registered,
  net+0x3b560), 11061 (HUV chase: ui+0x15c state==3), 11129 (auto-drive 0x8cdf40), /invite also 11111 in Battle Zone.
- **410000 create** (0x61def0): only if dialog exists and target != my own name. TARGETNAME = target (disabled),
  PARTYNAME default 10395 "Party", MESSAGE default 10396 "Join us!". Labels 11267 "Please enter a message."; if I have no
  party yet: 11266 "Please enter a party name." and PARTYNAME enabled; else 11268 "Party Name", field disabled.
  BUTTON (410005): empty name -> 10470 "Enter the name of Party."; bad-word filter -> 10471 "The Party is unavailable.";
  else send 0xf0. So the **inviter names the party** when the party is created; later invites reuse it.
- **420000 invite received** (0x61e100): auto-refusals (send 0xf1, show 10414 "%s party request was declined." locally):
  option DAT_008d28ec ("refuse party requests") -> 10412; in Battle Zone (room-manager vfunc +0xfc or PvP lobby open)
  -> 10413; auto-drive -> 11130 (+ local 11129). Otherwise TEXT = 10415 "%s of %s Party has requested..." (0x4cc400,
  arg order by nation), LEADERMSG = leader message, ACCEPT (420006) -> 0xf2, CANCEL (420007) -> 0xf1 with 10472
  "%s has refused your Party invitation." (0x5be1d0).
- **430000** (0x61e4c0): CURRSTATE 10416 "%s is already a member of another party.", TARGETMSG 10417 "Do you want to form a
  party with %s?"; BUTTON (430005) -> 0xf6.
- **440000** (leader side, 0x61e5a0): TEXT 10524; ACCEPT (440004) -> 0xf7; CANCEL (440005) -> 0xf1 with 10473 (US text
  is a Crew string - id reuse).
- **450000**: shows the refusal text; OK hides.
- Leave: PARTYLIST LEAVE (2890028) -> 0x512da0 -> 0xf4; blocked during HUV chase (11062). Entering the Battle Zone with a
  party asks 10171/10067 "...automatically be kicked from your party" (0x561c66, 0x763c6a), callback 0x561510 -> 0x512da0.
- Kick: MEMBERBAN1-4 (2890029-32) -> 0xf5(member[i].userId); 11063 during HUV chase. Button visible only when I am
  members[0] and the row is not me (0x61c28c). In Undercity (scene 0x13) LEAVE and MEMBERBAN are disabled (0x61bff5).
- Disband: implicit when the party drops to 1 member (10831).
- Other refusals: 11122 "cannot use Auto-Drive while being in a party" (0x59afb3, 0x5d5b20, 0x5d66f7), 11480 mission
  battles (0x5c6a99, 0x5d6180). **10812** ("difference in car level") is NOT used by the party code; its only use is PvP
  FINDFRIENDS result 1090025 (0x593b0e): join refused when `myCar.grade - 1 != target room grade field`.

### HUD 2870000 / PARTYUC 2880000 / PARTYLIST 2890000 [exact]
- Object ui+0x154 (binder 0x61c810, refresh 0x61c0b0, show 0x61c750 via 0x512d80 after every party change). Hidden when
  count <= 1.
- HUD: NAME (2870002) = party name; MEMBERCOUNT (2870003) = L"%u" member count (just the number); COMBOCOUNT (2870004) =
  "%u" party combo total (net+0x3b614) or, during party patrol, "Post %u" = sum of member posts.
  TOGGLELIST (2870005, J=123 = F12) opens the list and hides the button; list TOGGLELIST (2890027, F12) closes it.
- PARTYLIST rows i=0..3: MEMBERNAME = name; MEMBERLEVEL = "Lv%u" (level); LEVEL (2890015-18) = "(V%u)" car grade;
  CHARIMG = portrait chosen by character type +0x36 (8 portraits 2890019-26 moved onto row anchors); P1-P4 icon visible;
  POSTxx = "Post %u" only during party patrol. MEMBERCOMBO text is set ("%u" member+0x40, own row uses local combo)
  but the control is hidden; MEMBERPOS is hidden and never filled; PARTYLEADER (2890041) is never bound;
  OFFLINE1-4 are force-hidden in 0x61c750 (only used as row anchors). => no offline/location display in this build.
- PARTYUC 2880000: P1-P4 badges + "%d" counters (0x6850d0) = per-member HUV hit counts (member+0x44) [inferred: party chase].
- Minimap: P1..P4 controls 190045-48 bound at minimap+0x8c..+0x98 (0x6028bb..); icon types 26..29 (jump table 0x5f757c).
- Party chat prefix: 10751 "/party " (command list 0x5b745b).

### Party bonus (0x4fe050) [exact]
Only if net+0x288 & 0x1000 and count > 1. Per member: level > PartyBonus_Level (globalcontent, US 80) -> high, else low.
Step = 5 % (10 % if net+0x288 & 0x800). exp% = step*low, mito% = step*high. Chat: both -> 10998, only low -> 10996, only
high -> 10997 ("when playing HUV gamemode"). HUD 3870000 shown through ui+0x408 (0x5ea760) when (net+0x24c&0x18)==8 or
ui+0x404 (0x5e5690) when ==0x10. Called on join/leave/kick/level change (0xf2/0xf3/0xf4/0xf5/0xf8).

## 2. Friends

### Which UI [exact]
Every friend packet handler tests net+0x24c & 0x18 (UI-type option, see COMBO.md): non-zero -> new community window
ui+0x1f0 (class vtable 0x872090: COMM 3970000 + MYFRIEND manager 4000000 + MYTEAM + NOTE); zero -> old MYFRIEND 2930000
object ui+0x1dc (vtable 0x873e08). HUD button 0x5e7ea0 opens ui+0x1f0 on tab 2 (unread notes), 0 (has crew) or 1 (friends).
No external opener of the old 2930000 object was found [inferred: only reachable in the basic UI type].

### Packets
Client [exact]: 0xe6 (2 bytes) refresh; 0xe8 (0x2c) add {wchar name[21]}; 0xeb (0x2c) remove {name}; 0xef (0x36)
answer request {u32 idLo, u32 idHi, u16 accept}; 0xe2 (0x2c) block {name}; 0xdf (0x2c) unblock {name}.
Server [exact]: 0xe7 list {u32 count, u32 flags (0x40000 = clear first), records 0x70}; 0xde block list (same layout);
0xea upsert record; 0xe1 record update (status 'N' -> remove); 0xee request received {record}; 0xef answer {+0xa u16
1=accepted, +0xc name}; 0xed delete {+2/+6 id, +0xa i16 type, +0xc name, +0x36 name}; 0xe3 blocked {record};
0xe0 unblocked {+2/+6 id}; 0x113 login {+2 wchar name}.
Record 0x70 (0x612310 copies 0x1c dwords): +0x00 wchar name[21]; +0x2a wchar crew name; +0x44/+0x48 u64 char id (key);
+0x5c status char: 'F' friend, 'C' -> "<Friend not approved>" 10545, 'R' -> "<Friend approval request>" 10546,
'D' -> "<Deleted>" 10547, 'B' blocked, 'N' remove; +0x60 location (8 bytes); +0x68 / +0x6a u16 level (+0x68 when service
code DAT_008d2e10 == 3, else +0x6a; 0 -> "-"); +0x6c u32 userId (used for party invite 0xe4).
Location (0x607f50), default 10504 "Offline": byte kind, byte sub, u16 channel:
kind 1 city: ch 0..2/0xffff -> 10540 "Driver Dome", 3 -> 10541 "Beginner - Moon Palace", channel number >= 100 ->
10809 "Undercity", else city 1..5 -> "Channel %2d - Moon Palace/Koinonia/Cras/Oros/Mitro" (10542/10543/10544/10648/11396)
or "Gate %2d - ..." (10861-10864/11395) when net+0x244 & 0x200; kind 2/3 PvP arena by sub: 10306 "%d times Exp. Arena",
10307 "Free Arena", 10823 "RealMatch Arena"; kind 4 -> 10308 "Spectating: %d"; 5 -> 10309 "Channel Battle in progress.";
6 -> 10720 "Lap Time - %dClass Arena" (sub+1).

### 4000000 manager (handler 0x614680, list draw 0x60a870 friends / 0x60ab70 deny) [exact]
TABKEY_FRIENDLIST/DENYLIST (4000003/4) mode +0x5c0 = 0/1; REFRESH -> 0xe6; ADD_FRIEND -> MSGBOX 3230000 (10305 "Enter the
character name you wish to add...", FRIEND_ADD 3230010 or Enter -> 0xe8, empty -> 10539); REMOVE_FRIEND -> none
selected 10537 "No friend selected." else confirm 11192 "Removing %s from the buddy list." -> 0xeb; DENY_FRIEND ->
MSGBOX 11179 "Please enter an ID to block." (OK -> self 11182 / already 11183 / else 0xe2); DENYCANCEL -> confirm 11180 ->
0xdf; title buttons sort by level/name/crew/location (0x613e70/ef0/f70/ff0); 10 rows, LEFT/RIGHT/PAGE paging by 10;
row BTN double-click (0x103) -> 0x60a7b0. WHISPER -> chat edit "*Name " (0x60e914; chat parser checks '*' 0x5b70b1);
PARTY -> 10944 / 11129 checks then 0xe4(record+0x6c); SENDNOTE -> closes window, opens MSGSEND 2940000 with the name.
No client-side friend limit; server texts 20127/20132 "Cannot add friend", 20131 other list full, 20129 already added,
20130, 20341 block list full, 20342 friends can't be blocked, 20343 blocked can't be added, 20344 different server.

### Request flow [exact]
A: 0xe8 (MSGBOX or COMMAND 700004 -> 0x79f8f0 resolves target to name). B receives 0xee -> message box type 7
"<name>" + 10569 " has requested to add you as their friend..." (0x66cc00, old object even in new UI); Yes (control
3250021) -> 0xef(id,1), No -> 0xef(id,0) (0x6683e0). A receives 0xef -> "<name>" + 10810 accepted / 10811 declined
(chat + box). 10526 is not referenced (no string in the exe).
Remove notices (0xed): type 0 -> 10833 "%s has been deleted from your Friend List.", type 1 -> 10832 "%s has deleted %s
from the Friend List.". Block 0xe3 -> 11274 "%s has been blocked."; unblock 0xe0 -> 11181. Chat "/block " 11271 ->
0xe2, "/unblock " 11272 -> 0xdf (11273). Login 0x113 -> 11412 "[Friend] %s has logged on." only if option DAT_008d28f8.
Invites from blocked users are auto-refused (0x4fa3fa).

### Old 2930000 (handler 0x66d5c0) [exact]
Tabs 2930005-08 (keys 1-4) only switch +0x18 mode 0..3; TABKEY cycles; FRIEND_ADD (2930050, key 107) -> add MSGBOX;
FRIEND_REMOVE (2930051, 109) -> 10537 / 10538 confirm -> 0xeb; REFRESH (2930053, F5) -> 0xe6; WHISPER (2930047);
PARTY (2930048) -> 0x6687c0 -> 0xe4; column headers sort; INFOTEXT = 10304 "Add & Delete".

## 3. NICNAME 3770000 = driver rename [exact]
Handler 0x5434e0, binder 0x543420. Title 11020 "Input new driver's name here."; NEW_NAMECONFIRM (3770005) or Enter ->
0x5431c0: empty 10601, length 4..12 (0x74a680) 10602, characters (0x74a740) 10603, bad-word filters 10606, then packet
0x6a {u32 mode=1 check, wchar name[21]}; ACCEPT (3770006) -> 0x6a mode 0 (apply). Replies presumably 10604/10605.
Opener not traced (rename item) [inferred].

## 4. ADDFRIEND 3710000-3730000 = Hangame "invite an outside friend" event [exact]
Triggered by server text commands "invitef_1" (3710000 mission try), "invitef_2" (3720000 mission success; ID = my name),
"invitef_3 <date> <id>" (3730000 invite success with date + id) in handler 0x93 (0x502d53..0x502ee7), object ui+0x3ac.
INVITE buttons open http://hani.hangame.com/exfriend/popup/invitation.nhn?gameid=SKIDRUSH (alpha/beta variants,
0x672c86..). Korea-only; irrelevant for a remake.

## 5. COMMAND 700000 (0x5bdce0) [exact]
700002 USERINFO -> 0x79f790; 700003 TRADE (age check 10995) -> 0x79f800; 700004 ADDFRIEND -> 0x79f8f0 (0xe8); 700005
"BADBOY" button actually does Crew join request (10163/10164); 700006 PARTYJOIN -> 0xe4; 700007 SENDLETTER -> MSGSEND;
700008 OWNLICENSE -> 0x79ee50.
