# Packet handlers: the client's reactions to server replies

The original client registers **454 opcodes / 441 handler functions** in `0x519120` (`RegisterHandler 0x79f390`, dispatcher
`0x7ac330`, REPORT.md 4). This table names each one from its strings, text ids (globaltext / globaltext_server),
the UI objects it touches (`0x4386b0()+0x160` = the UI manager; its members mapped to game.nui dialogs through the
UI-manager constructor `0x532xxx`), its sounds / UI effects, and the RE notes that already studied it; then gives the
remake's status. Sources and helper scripts: scratchpad `hnd/` (`handlers.json`, `brief.txt`, `uimap.json`, `senders.json`).

Status: **covered** (our JSON message -> the C# that shows it), **covered (new)** (converted this round, `Net/ServerNotices.cs`
+ `DriftCityServer/src/notices.js`), **missing** (a reaction of a system the remake lacks, or not yet routed), **n/a**
(login / GameGuard / Hangame / crypto / keep-alive / debug / GM tools, or a mechanism the remake does differently by design).
"local" means the remake runs that rule on the client (offline rule, also online) instead of waiting for a server packet.

**Counts** (454 opcodes, 441 functions): covered 365, covered (new) 6, missing 22, n/a 61 (the services round: SERVICES.md).

| feature | covered | new | missing | n/a |
|---|---|---|---|---|
| Battle Zone | 58 | 0 | 0 | 3 |
| missions | 36 | 0 | 1 | 0 |
| crews / channel battle | 31 | 0 | 2 | 0 |
| network | 1 | 3 | 0 | 27 |
| city / remote cars | 28 | 0 | 0 | 2 |
| events | 25 | 1 | 4 | 0 |
| party / friends | 26 | 0 | 1 | 0 |
| custom shop | 9 | 0 | 12 | 0 |
| Undercity | 19 | 0 | 0 | 0 |
| items | 19 | 0 | 0 | 0 |
| parts / licences | 18 | 0 | 0 | 0 |
| auction | 16 | 0 | 0 | 0 |
| chat | 10 | 1 | 0 | 2 |
| characters | 11 | 0 | 0 | 2 |
| Rush Time | 2 | 0 | 0 | 11 |
| HUV chase | 10 | 0 | 0 | 2 |
| garage | 10 | 0 | 0 | 0 |
| Virtual HUV | 6 | 0 | 0 | 4 |
| notes (mail) | 8 | 0 | 0 | 0 |
| trade | 8 | 0 | 0 | 0 |
| progress | 5 | 1 | 1 | 0 |
| login | 0 | 0 | 0 | 6 |
| stages | 5 | 0 | 1 | 0 |
| car dealer | 3 | 0 | 0 | 0 |
| system | 0 | 0 | 0 | 2 |
| shop | 1 | 0 | 0 | 0 |

## 1. Server notices (converted this round) [exact]

| original | what DriftCity.exe does | remake message | client |
|---|---|---|---|
| op 0x093 chat, scope channel, text starting with `notice` (or the 4-byte 10037 prefix in KR) | the line in the chat, then `ui+0x284` EMERGENCY 2770000 `Show(12.0, text, 0)` (0x54d850): ID_EMERGENCY_NOTICE_TEXT visible (flag 1 = ID_EMERGENCY_NOTICE_LARGE_TEXT, the 48 pt banner on ui_button15.dds), hidden again by the update 0x54d7a0 when the time is up | `notice {kind:"emergency", text, seconds:12, large?}`; our `say`/`announce` chat (scope `notice`) too | `ServerNotices.Emergency` |
| op 0x093 scope `bcast` / `modify` (11416 "%s has acquired the [ %s ] [ %s ] option through parts converting.") | `ui+0x288` SCROLL 2780000 (0x552480): a queue of texts; ID_SCROLL_NOTICE (1024x14, Calibri 12 bold, FFCC00) goes from x0 + screen width to the left at **70 px/s** (`0x85e758`); past -screen width the next text starts, an empty queue hides the dialog (0x5523a0) | `notice {kind:"scroll", text}` | `ServerNotices.Scroll` |
| op 0x008 | text (+0xc) in the POPUP 3250000 message box and in the chat, scope system, colour 0xFF48F0FF (0x50c990) | `notice {kind:"system", text\|key}` | `ServerNotices.Popup` |
| op 0x093 scope `GM` / `system` | chat line, colour from the packet (+0x36) | `notice {kind:"gm"}` / `{kind:"chat"}` | ChatBox |
| op 0x168 | `0x697a90(name, level)`: 10054 "%s has reached level %d." chat line, colour 0xFFF29177 | `notice {kind:"level", name, level}` to the other drivers of the channel (remake rule: the server scans levels every second) | `ServerNotices.Handle` |
| op 0x09c `{len, wchar reason}` | len > 1: the reason in the message box, copied to `g_security+4`; else 10989 "Exit by server."; the client quits after OK (0x4fc330) | `kicked {reason}` (existing); our reasons map to the original texts: "logged in from another place" 20209, "timed out" 20335, "kicked by admin" 20308, "banned" 11417, "maintenance" 10983 | `ServerNotices.Kicked` (the remake keeps driving offline; no second "lost connection" popup) |
| op 0x003 `{serverMs, sentTick}` (and 0x21c) | RTT samples, server clock offset `net+0x118` (0x790300 -> 0x7806a0) used by PvP / Rush timers | `time {now}` once per driver and every 10 min | `ServerNotices.UtcNow` |
| op 0x089 generic subtypes 7/8/11-15/18/28/29 | see 2. | `notice {kind:"event", title, text, icon}` / `{kind:"obtain", name, count}` | EventPopup (SEASONEVENT 3340000) / chat + SID_ITEM_DROP_GET1 |
| 16/21 login replies 10983 / 11418 | maintenance / servers not in service at login | `login` reply `{ok:false, key:"10983"}` while closed for maintenance | login screen error |

Text keys: `notice.key` is looked up in globaltext.xlt, then in `localize\globaltext_server.xlt` (359 rows, 20000-21999: the
original server's own texts, e.g. 20209, 20305/20306 chat block, 20307 "You've received new Items!", 20210 / 20289 channel
tournament, 20319-20322 jackpot, 20357 Christmas tree), filled with `args` like the client's sprintf (`MissionText.Printf`).

Server console / admin chat (`/cmd` for admins): `notice <text>`, `bignotice <text>`, `scroll <text>`, `popup [@driver] <text>`,
`gm <text>`, `event <title> | <text>`, `maint <minutes> [text]` (emergency + scroll warnings at 30/15/10/5..1 min, then every
non-admin is kicked with "maintenance" and logins answer 10983 until `maint off`), `mute <driver>` (now also sends 20305 / 20306).
Other server code can call `server.notices.send(session, kind, body)`, `.broadcast(kind, body)`, `.toChannel(ch, kind, body)`.

## 2. Op 0x089 "event notice" subtypes (0x4fb280, `{u16 op, int sub, int a, int b, int c}`, 18 bytes) [exact]

| sub | client reaction | feature | status |
|---|---|---|---|
| 1 | OBEVENT prize popup a (0x673610(a)) | 2007-08 KR open-beta campaign prizes (OBEVENT 3330000, KR text only) | n/a: campaign table not used, EVENTS.md |
| 2 | OBEVENT prize 7 | same | n/a |
| 3 | OBEVENT prize 8 | same | n/a |
| 4 | OBEVENT prize a | same | n/a |
| 5 | OBEVENT prize a | same | n/a |
| 6 | GreenDay quest random reward: item a popup, title 10993, text 11303 (KR) | props event | missing: Greenday props event |
| 7 | item a x b dropped: SID_ITEM_DROP_GET1 (0x4eec), getitem.nif (0x69fdd0), chat "Obtained %d %s" (0x6f3ed0) | drops | covered (new): notice{kind:'obtain', name, count} |
| 8 | Wager jackpot: 11070 "Wager Jackpot Winner" popup with 11071 (Mito a), UI effect 55 | wager | covered (new): notice{kind:'event'} (no jackpot rule in race.js yet) |
| 9 | gacha result: car a (0x68f290 GACHA 4100000) | gacha | covered: gacha.buy reply -> Gacha.cs |
| 10 | gacha result: item a x b | gacha | covered: gacha.buy reply -> Gacha.cs |
| 11 | wager token: 11155 title, 11154 "You've received a Token as a Wager Battle Reward. %d / 10 (Daily Limit)" + chat | wager | covered (new): notice{kind:'event'} (token reward rule not in race.js) |
| 12 | item a x b popup (item name "%s x%d") | generic | covered (new): notice{kind:'event'} |
| 13 | part a x b popup | generic | covered (new): notice{kind:'event'} |
| 14 | part a x b chat line (not types 0x13/0x21) | generic | covered (new): notice{kind:'obtain'} |
| 15 | Mito a received popup (11197 "%d Mito Received") | generic | covered (new): notice{kind:'event'} |
| 16 | recycling: part b -> item a (11294 "By recycling %s, you've received %s.") | recycling | n/a: recycles expired G-Coin items; the remake sells none (SERVICES.md 10) |
| 17 | recycling: part b -> part a (11294) | recycling | n/a: recycling (SERVICES.md 10) |
| 18 | part a x b chat line | generic | covered (new): notice{kind:'obtain'} |
| 19 | "used X, obtained Y" popup (NGABUBURIT COIN in region 4) | event boxes | covered: event.box reply -> EventData.cs / EventPopup |
| 20 | same for a part | event boxes | covered: event.box reply -> EventPopup |
| 21 | Halloween reward: 11428 / 11427 popup, 10096 chat (region 1) | Halloween | covered: event.* (Halloween Drink gift) -> EventData.cs |
| 22 | ui+0x350 (PARTSSHOP 1200000) field +0x308 = b | parts shop counter [inferred] | missing: [inferred] |
| 23 | gacha SetResult item a (0x63c670) | gacha | covered: gacha.buy -> Gacha.cs |
| 24 | gacha SetResult part a (+0xe) | gacha | covered: gacha.buy -> Gacha.cs |
| 25 | net+0x3b524 = b (event day counter) [inferred] | anniversary | covered: event.state -> EventData.cs |
| 26 | Valentine / harvest object value b (0x693ac0 / 0x6928a0) | Easter / harvest | covered: event.basket / harvest.* -> Harvest.cs |
| 27 | Valentine object value b (0x693af0) | Easter | covered: event.basket -> Harvest.cs |
| 28 | item a x b popup, title 11563 "Event!!" | generic | covered (new): notice{kind:'event', title:'Event!!'} |
| 29 | part a x b popup, 11563 | generic | covered (new): notice{kind:'event'} |
| 30 | Valentine object value b (0x693b20) | Easter | covered: event.basket -> Harvest.cs |
| 31 | item b with period: 11716 "You've obtained %s (%d Days) Would you like to extend the items expiry date?" (Yes/No) | item expiry | n/a: period goods extend by buying again (11044; SERVICES.md 10) |
| 32 | level gift box: 11721 / 11723 (KR only), title 11729 "Issue Gift Box" | level gifts | covered: event.box (giftbox_lv) -> EventPopup |
| 33 | level gift box 11725 | level gifts | covered: event.box -> EventPopup |
| 34 | level gift box 11727 | level gifts | covered: event.box -> EventPopup |
| 35 | summer festival: sumfest_event1 "Summer Festival Event" / sumfest_event2 "You have used %s and obtained %s %d!" | summer | covered: event.combine -> Harvest.Combine / EventPopup |
| 36 | harvest restaurant result (0x6916f0(b, a)) | harvest | covered: harvest.deliver -> Harvest.cs |

Popups: `0x673610` OBEVENT prize, `0x672f40(item, title, text)` / `0x6730c0(title, text, part)` the item/part reward popup,
`0x68f290` / `0x63c670` gacha results, `0x6736e0` / `0x673670` summer festival, `0x693940` Valentine / `0x62efb0` harvest objects.

## 3. Missing reactions owned by other systems (exact lines for the owners)

- **Weather / clocks** (WeatherClock.cs lines 28 and 38): `OriginalLook.ScheduledWeather(System.DateTime.UtcNow)` ->
  `OriginalLook.ScheduledWeather(DriftCity.Net.ServerNotices.UtcNow)` so every client of a server shows the same weather even
  with a wrong PC clock (offline `UtcNow` is the local clock). Same for `SeasonEvents.cs:49` `ForDate(DateTime.UtcNow)` ->
  `ForDate(Net.ServerNotices.UtcNow)` and `PvpRoom.cs:187` `var now = DateTime.UtcNow;` -> `Net.ServerNotices.UtcNow`.
- **Christmas tree completed** (server `src/events.js` `xmasDonate`, after `if (completed) this.log(...)`; the client's 11440-11443
  "<city> Christmas Tree is now fully decorated" and server text 20357):
  `if (completed && this.notices) { this.notices.broadcast('chat', { key: '20357', args: [p.Name], text: 'Congratulations! ' + p.Name + ' has completed decorating the Christmas tree.' }); this.notices.broadcast('scroll', { key: { moonpalace: '11440', koinonia: '11441', cras: '11442', oros: '11443' }[XMAS_CITIES[c]] }); }`
  plus in server.js after `const events = new Events(...)`: `events.notices = notices;`
- **Channel tournament completed** (op 0x1da, server `src/teams.js` where a channel tournament / capture ends):
  `this.notices?.broadcast('chat', { key: semi ? '20289' : '20210', args: [channelIndex] });` and `teams.notices = notices;` in server.js.
- **Crew promotion text** (Teams.cs line 614, op 0x36d): `"promoted" => m.I("rank") == 2 ? SocialNet.T("10838", "%s has been promoted to First Class.", name) : m.I("rank") == 3 ? SocialNet.T("10839", "%s has been promoted to Second Class.", name) : $"{name} is now {m.S("rankName")} of the {crew} Crew.",`
- **Muted chat** (server `src/world.js` chat): `'You are muted.'` -> `'You are currently blocked from chatting.'` (20102, the original
  server text); the spam guard line is the client's 10476 "Your chat privileges are blocked for %d seconds.  Do not scroll text.".
- **Wager jackpot / token** (op 0x89 sub 8 / 11; race.js has neither rule): when added, `notices.send(s, 'event', { title: 'Wager Jackpot Winner', text: ... })`.
- Systems the remake does not have (all rows marked missing): custom sticker upload/sale (0x041-0x061, 0x324, 0x547-0x54c),
  channel-battle spectating/replays (0x388), Mitro faction reputation (0x7d4), red envelope and Greenday props events (0x48f,
  0x493/0x495), GW event box (0x5b7), server-driven NPC lines (0x009). Converted by the services round (SERVICES.md): private
  channels (0x08f-0x091, 0xa29-0xa2d), rental cars (0x06d), driver rename (0x06b), recommend-a-driver (0x097/0x099), crew-owned
  Mittron stations (0x2d4); recycling and item-period extension (0x89 subs 16/17/31) are not needed (SERVICES.md 10).

## 4. Codemap notes

- 292 of the 441 handler functions are `LAB_` targets in the Ghidra project (`519120_register_handlers.c`), not
  procedures: build_funcs.py merges each into the function before it, so the codemap's "Packet handlers" subsystem holds
  only 149 handler entries and the merged bodies are mislabelled by their neighbour. The list is in scratchpad
  `hnd/handler_entries.json`; adding it to `map/extra_entries.json` splits them (the classify.py OVERRIDES already name them).
- classify.py OVERRIDES: every handler whose feature is a known subsystem is mapped to it (block "packet handlers by feature",
  HANDLERS.md); pure acks / fixed-length replies stay "Packet handlers (server replies)".
- Request opcodes (client -> server, `mov word ptr [eax], op` in the senders 0x780000-0x79ffff) are in `hnd/senders.json`; a reply
  is usually request + 1 (0x1a1 item use -> 0x1a2, 0x5ac scratch -> 0x5ad, 0xfd/0xfe Undercity answer).

## 5. All handlers

| op | handler | name | feature | what the client shows / changes | remake |
|---|---|---|---|---|---|
| 0x001 | 0x50c920* | session blob / idle reset | network | {len, blob} to the net object (0x512c60); clears QUEST 460000's idle timer +0x38 | n/a: keep-alive |
| 0x002 | 0x4efd30* | keep-alive (6 bytes) | network | nothing (returns 6) | n/a: keep-alive |
| 0x003 | 0x50c960* | time sync reply | network | {serverMs, sentTick}: RTT sample into the 10-slot table (0x790300 -> 0x7806a0), server clock offset net+0x118 used by PvP/Rush timers | covered (new): pong.now -> ServerNotices (UtcNow offset) |
| 0x004 | 0x4efd30* | keep-alive (6 bytes) | network | nothing | n/a: keep-alive |
| 0x008 | 0x50c990 | system message box | chat | text (+0xc) in the POPUP 3250000 message box and the same line in chat scope "system" (colour 0xFF48F0FF) | covered (new): notice{kind:'system'} -> ServerNotices.Popup |
| 0x009 | 0x4f8310 | server dialog text by key | missions | GlobalText key (+6) shown in the NPC dialog QUESTDIALOG 600000 (0x688780) | missing: no server-driven NPC lines; offline NPC dialogs are local |
| 0x016 | 0x4ed8a0* | login reply (auth) | login | auth result: 10045-10050, 10983 maintenance, 10984 IP refused -> POPUP; ok -> server list | n/a: login (remake: login reply -> OnlineSession.ConnectAndLogin; maintenance text via ServerNotices |
| 0x017 | 0x4eef80 | server list | login | server rows with status 10758-10764 (Preparing/Processing/Inspecting/Failed/Few/Many/Crowded), 11338 Limit Exceeded (ServerSelectHandleUI) | n/a: login (servers.txt list in OnlineSession.Servers |
| 0x019 | 0x4edd60* | login step (net blob) | login | blob to 0x512c60 | n/a: login |
| 0x01a | 0x4ede80* | own user info | characters | character summary into USERINFO 1810000 | covered: login reply driver -> OnlineSession.ApplyDriver |
| 0x01c | 0x4edc40* | login reply (Hangame / alt) | login | same texts as 0x016, auction min level (AuctionMinLevel), opens the shop-open state | n/a: login/Hangame |
| 0x01e | 0x503e50 | server config (21 dwords) | network | copies 21 dwords to net+0x244: event/arena feature bits (EVENTS.md, PVP.md) | covered: welcome / event.state season list -> EventData.cs (feature flags are fixed in the remake) |
| 0x020 | 0x503ee0 | option/server flags | network | flags into the option base (OPTIONBASE 2050000 bits) | n/a: server config |
| 0x02a | 0x505cd0* | net value | network | stores a dword via 0x781260 | n/a: networking |
| 0x02c | 0x4f9a00* | GameGuard / security blob | system | {len, blob} to g_security vtable+0x18 | n/a: GameGuard |
| 0x02f | 0x505f50* | small ack | characters | stores two words in net (no UI) | n/a: networking |
| 0x032 | 0x5063a0* | character delete / quit question | characters | 10548 "Do you want to quit the game?", 11098 "Cancel the deletion of this character?"; refreshes SELECTCHARACTER | n/a: login (one driver per account |
| 0x03d | 0x506e20 | character list | characters | character rows (0x78 bytes each) into the select screen; region 3 with another account -> 10988 relogin popup | covered: login reply driver -> OnlineSession.ConnectAndLogin |
| 0x041 | 0x506a30* | sticker image list | custom shop | "image req count(" debug, sticker ids -> custom sticker images (STICKER DATA ERROR) | missing: custom sticker upload not rebuilt |
| 0x044 | 0x5069c0 | sticker data | custom shop | sticker record -> 0x506160 | missing: custom stickers |
| 0x046 | 0x505ec0* | ack 0x8a bytes | network | nothing (fixed length) | n/a |
| 0x048 | 0x505e20* | sticker registered | custom shop | POPUP 10627 "Sticker registration successful", VSHOP 2860000 refresh | missing: custom stickers |
| 0x04a | 0x505e70 | sticker list | custom shop | VSHOP 2280000 sticker list (effects\realmatch\real_%05d.dds) | missing: custom stickers |
| 0x04f | 0x505f80* | ack | network | fixed-length ack | n/a |
| 0x051 | 0x505d10* | ID check reply | characters | 10604 "This ID is already in use" / 10605 "This ID is available." | covered: driver.create reply error -> OnlineSession.CreateDriver |
| 0x053 | 0x505d60* | apprentice / tutorial flow | stages | ApprenticeFlowType (GlobalContent) and PVP 1070000 | missing: apprentice system not rebuilt |
| 0x054 | 0x506d60* | character data refresh | characters | character record (0x506d10) then state change 0x438790 | covered: driver push -> OnlineSession.ApplyDriver |
| 0x056 | 0x4ef1a0* | character + car data | characters | all character/car data, 10033 "You have purchased a car", next stage = parking lot (PARKINGLOT.md) | covered: login reply / driver push -> OnlineSession.ApplyDriver |
| 0x058 | 0x4ef370* | dealer list | car dealer | AUTOSHOP 3260000 lists, 11810 Recommended Items / 11811 Sold product information | covered: local catalogue (ShopData.cs); purchases via shop.buyCar |
| 0x05a | 0x4f14d0* | car changed | garage | current car switch, sound 0x4ea1, level-up clock reset 0x697a70 | covered: profile.update reply -> OnlineSession.ApplyDriver |
| 0x05c | 0x4f1550 | car upgraded | garage | 10501 "The vehicle has been upgraded to the next level." POPUP, VSHOP 2460000 refresh | covered: shop.upgrade reply -> Progress.cs / Workshop.cs |
| 0x05e | 0x505ed0* | sticker ack | custom shop | VSHOP 2860000 refresh | missing: custom stickers |
| 0x060 | 0x505f00* | sticker deleted | custom shop | POPUP 10654 "Sticker deleted." | missing: custom stickers |
| 0x061 | 0x506480 | sticker apply data | custom shop | sticker/decal data to the car (0x440a40) | missing: custom stickers |
| 0x062 | 0x4fcd60* | new car obtained | garage | 11279 "You've obtained %s. Do you wish to ride it right now?" (closes inventory) | covered: shop.buyCar / item.use reply -> Progress.cs (no ride-now question) |
| 0x063 | 0x4f1420 | character stats | characters | level/exp fields refresh (0x6cb4a0/0x6cb400) | covered: driver push -> OnlineSession.ApplyDriver |
| 0x065 | 0x505e10* | ack | network | nothing | n/a |
| 0x069 | 0x4f17a0* | car list refresh | garage | VSHOP 2280000 / AUTOSHOP 3260000 car list | covered: driver push -> OnlineSession.ApplyDriver |
| 0x06b | 0x4fc430* | name change reply | characters | NICNAME 3770000: 10604/10605 check, 10954 renamed, 10960 failed | covered (SERVICES.md): rename.check / rename.apply reply -> Services/DriverRename.cs (NICNAME 3770000) |
| 0x06d | 0x502850 | rental car expired | garage | 10968 "It's time to return your rental car..." (OK -> garage) or 10969 POPUP + chat line | covered (SERVICES.md): rental.returned push -> Services/RentalCars.OnReturned (10968 / 10969 + chat) |
| 0x06f | 0x50a590* | ladder page ack | Battle Zone | ladder state (no text) | covered: race.ladder reply -> Ladder.cs |
| 0x073 | 0x509ba0 | ladder ranking | Battle Zone | LADDER 3910000 rows ("%d %d") | covered: race.ladder / leaderboard reply -> Ladder.cs |
| 0x075 | 0x509d40* | ladder ack | Battle Zone | nothing visible | covered: race.ladder -> Ladder.cs |
| 0x079 | 0x4f13d0* | game server connect failed | login | 10013 "Failed to connect to the game server." | n/a: connection (NetClient connect error |
| 0x07c | 0x5057a0* | login init bundle | characters | quick slots ITEMQUICKSLOT 360000, tour flags, instant missions, 10627; ApprenticeFlowType | covered: login reply driver (QuickSlots in profile) -> OnlineSession.ApplyDriver |
| 0x07e | 0x4f1c30 | channel list | chat | 10034 "Channel: %s" lines; channel ids to net (0x514590) | covered: channels / enter reply -> OnlineSession.EnterCity |
| 0x080 | 0x4ef580* | player left channel | city / remote cars | <LeaveChannelPkt> remove remote player | covered: player.left -> OnlineSession -> RemotePlayers.Remove |
| 0x082 | 0x4ef680 | player entered | city / remote cars | remote player name (0x70b830) added | covered: player.joined -> OnlineSession -> RemotePlayers.Add |
| 0x084 | 0x5008b0* | note list | notes (mail) | COMM 3970000 / MYFRIEND 2960000-2970000 note rows, 11087-11089 days/hours/minutes left, 11082 Unlimited, 11152 red-envelope line | covered: note.list reply -> Mail.cs |
| 0x088 | 0x4f7090 | item obtained (event) | events | OBEVENT popup, 10096 "Obtained %d %s" | covered: event.* replies -> EventData.cs / EventPopup |
| 0x089 | 0x4fb280 | event notice (36 subtypes) | events | see the op 0x89 table below | covered (new): notice{kind:'event'/'obtain'} -> ServerNotices (generic subtypes); feature subtypes C via EventData.cs |
| 0x08a | 0x500c10* | red envelope / note sent | notes (mail) | 11152 system line; note record | covered: note.send reply -> Mail.cs |
| 0x08b | 0x4f71e0* | ack | network | fixed-length ack | n/a |
| 0x08c | 0x4ef720* | private chat window opened | chat | <OpenChatPkt> name | n/a: only a debug line in state 0x4c (no window) |
| 0x08d | 0x4ef7e0* | private chat window closed | chat | <CloseChatPkt> name | n/a: only a debug line in state 0x4c (no window) |
| 0x08f | 0x4f1ee0* | joined private channel | chat | 11166 "%s has entered a private channel." / 11169 | covered (SERVICES.md): pchan push / pchan.who -> Services/PrivateChannel.cs |
| 0x091 | 0x4f2000* | left private channel | chat | 11167 / 11170 "%s has exited the private channel %s." | covered (SERVICES.md): pchan push / pchan.who -> Services/PrivateChannel.cs |
| 0x093 | 0x502930 | chat message | chat | scopes room/area/channel/team/party/server/privatec/system/GM with colours; "bcast" -> SCROLL 2780000 marquee; "modify" -> 11416 parts-convert broadcast on the marquee; "notice..." / 10037 prefix on channel -> EMERGENCY 2770000 for 12 s; coolitem, LuckyBag winners, invitef_1..3, MQStartF, Token/LapInfo debug | covered: chat -> OnlineSession -> ChatBox; NEW notice{kind:'emergency'/'scroll'/'gm'} and chat scope 'notice' -> ServerNotices |
| 0x094 | 0x4ef9d0* | whisper | chat | whisper line | covered: chat scope whisper -> ChatBox |
| 0x096 | 0x4f2420 | chat system line | chat | name + text -> 0x5b4b70 | covered: chat scope system -> ChatBox |
| 0x097 | 0x4f74d0* | recommend allowed flag | party / friends | net+0x3b518 = 1 (the recommend-a-driver dialog may send) | covered (SERVICES.md): recommend.state -> Services/Recommend.cs (MSGBOX 10181 on the first city) |
| 0x099 | 0x4f74f0* | recommend reply | party / friends | POPUP 10042 "Recommended!" | covered (SERVICES.md): recommend.send reply -> Services/Recommend.cs (10042) |
| 0x09a | 0x4fc1d0 | popup by server | party / friends | POPUP (no fixed text) | missing: [inferred] |
| 0x09c | 0x4fc330* | exit by server (kick) | network | reason text (or 10989 "Exit by server.") in POPUP; the client quits after OK (reason kept in g_security+4) | covered (new): kicked{reason} -> ServerNotices.Kicked |
| 0x09e | 0x4f7690* | coupon code reply | events | 10754 "Coupon has been issued." / 10755 "does not exist or has already been used" | covered: coupon.code reply -> CouponBook.cs |
| 0x0a1 | 0x4ef550* | channel move ack | city / remote cars | 0x517a90 (channel switch) | covered: enter reply -> OnlineSession.EnterCity |
| 0x0a3 | 0x4f99b0 | PvP state flag | Battle Zone | PVP 3780000 flag | covered: race.* -> OnlineRace.cs |
| 0x0aa | 0x4f75a0* | login greeting | events | new / returning / attendance greeting (EVENTS.md) | covered: event.state (anniversary greeting / daily attendance) -> EventData.cs |
| 0x0b5 | 0x4f2500 | chase begin confirm | HUV chase | BS_PktChaseBeginConfirm: HUV units spawned (0x44a5d0), 10615 "Chase Started", QUEST 460000 timer | covered: offline chase rule (ChaseMission.cs); party: chase.party.start -> ChaseMission.MissionNet |
| 0x0b8 | 0x4f9da0* | chase ack | HUV chase | nothing visible | covered: ChaseMission.cs |
| 0x0b9 | 0x4f9b50* | HUV found / chase run | HUV chase | "found by", remain time | covered: ChaseMission.cs (offline rule) |
| 0x0ba | 0x500d90* | chase proposal | HUV chase | "chase propose", NewInstant mission kind, INSTANTMISSION 230000 | covered: ChaseMission.cs (CHASING call is local) |
| 0x0bb | 0x4f27a0* | remote HUV hit | HUV chase | "Remote Hit / Unknown Hit" by another player | covered: chase.party.hp -> ChaseMission.MissionNet |
| 0x0bc | 0x505200 | chase control switch | HUV chase | HUV control handover between players (Chase Switch) | n/a: HUVs are simulated locally; no authority switch |
| 0x0bf | 0x4f2c00 | HUV respawn / result begin | HUV chase | HuvRes Begin/End, UC boss HUV wave | covered: ChaseMission.cs / UndercityStage.Online.cs |
| 0x0c0 | 0x5012a0* | HUV hit | HUV chase | HuvHit: 10757 Damage, "%d Mito" rising line, UI effect 52, PARTYUC counter | covered: chase.party.hp / party.uc.event -> ChaseMission / UndercityStage.Online |
| 0x0c1 | 0x4f2e40* | HUV result | HUV chase | HuvResult (debug log) | covered: chase.party.end -> ChaseMission |
| 0x0c3 | 0x4f2a20* | boss time / penalty | Undercity | UC_HUVAgain1-3, boss time, penalty, UC_HUVTimeReduce (QUESTDIALOG) | covered: party.uc.phase -> UndercityStage.Online.cs |
| 0x0c4 | 0x4f2990* | boss time bonus | Undercity | UC_TimeSave "boss time bonus" | covered: party.uc.phase -> UndercityStage.Online.cs |
| 0x0c5 | 0x4f2ec0* | HUV health | HUV chase | HuvHealth -> CHARGERPVP 960000 gauge | covered: chase.party.hp -> ChaseMission |
| 0x0c9 | 0x4f2fc0* | chase ack | HUV chase | fixed length | n/a |
| 0x0de | 0x4faf20 | block list removed | party / friends | 11181 "%s has been removed from the blocked list." | covered: friend.unblock reply -> Friends.cs |
| 0x0e0 | 0x4f65b0* | unblock ack | party / friends | 11181 | covered: friend.unblock -> Friends.cs |
| 0x0e1 | 0x4f6630* | friend list | party / friends | MYFRIEND 2930000 rows (10504 Offline, 10545-10547 states) | covered: friend.list -> Friends.cs |
| 0x0e3 | 0x4f6330* | blocked | party / friends | 11274 "%s has been blocked." | covered: friend.block -> Friends.cs |
| 0x0e5 | 0x4fa280* | party invite answer / target state | party / friends | decides PARTY 410000 (name/message 11266-11268) or 10748/10469/10416 errors | covered: party.invite reply -> PartyWindow.cs |
| 0x0e7 | 0x4fad80 | friend state change | party / friends | friend row update, 11181 | covered: friend.changed / friend.online -> Friends.cs |
| 0x0ea | 0x4f62b0* | friend list page | party / friends | MYFRIEND rows | covered: friend.list -> Friends.cs |
| 0x0ed | 0x4f6400* | friend deleted | party / friends | 10832 "%s has deleted %s from the Friend List." / 10833 | covered: friend.removed -> Friends.cs |
| 0x0ee | 0x4f6600* | friend request | party / friends | 10569 "... has requested to add you as their friend" | covered: friend.request -> Friends.cs |
| 0x0ef | 0x4fb0b0 | friend request answered | party / friends | 10810 accepted / 10811 declined POPUP | covered: friend.answer -> Friends.cs |
| 0x0f0 | 0x4fa3a0 | party request received | party / friends | 10414 declined, 11129 auto-drive refusal; COMM 3970000 | covered: party.request -> PartyWindow.cs |
| 0x0f1 | 0x4f3070* | party popup ack | party / friends | POPUP close | covered: party.* -> PartyWindow.cs |
| 0x0f2 | 0x5046f0 | party member joined | party / friends | 10826 "%s has joined the party." + party bonus 10996-10998 | covered: party.notice joined / party.update -> PartyWindow.cs |
| 0x0f3 | 0x5047d0 | you joined a party | party / friends | 10825 "You've joined a party." + party bonus | covered: party.update -> PartyWindow.cs |
| 0x0f4 | 0x501570* | party left / disbanded | party / friends | 10827/10828/10831, MAINBUTTON party icon | covered: party.notice left/disbanded -> PartyWindow.cs |
| 0x0f5 | 0x501950* | party kick | party / friends | 10829/10830/10831 | covered: party.notice kicked -> PartyWindow.cs |
| 0x0f6 | 0x4f3040* | party join request (leader) | party / friends | 10524 "Allow %s to join the Party?" | covered: party.request -> PartyWindow.cs |
| 0x0f7 | 0x5046f0 | party member joined (alt) | party / friends | same as 0xf2 | covered: party.notice -> PartyWindow.cs |
| 0x0f8 | 0x501c90 | party bonus | party / friends | PartyBonus_level 10996-10998 next to the main bar | covered: party.bonus (partyplay.js) -> PartyWindow.cs |
| 0x0fb | 0x50e090* | Undercity phase clock | Undercity | {kind, ms} state changes (PARTYPLAY.md) | covered: party.uc.phase -> UndercityStage.Online.cs |
| 0x0fc | 0x50e1d0* | Undercity invite | Undercity | WAITPLAYER 3530000 / REQUESTINVITE 3540000, 11146 rush-time refusal | covered: party.uc.invite / party.uc.wait -> UndercityParty.cs |
| 0x0fd | 0x50e0f0* | Undercity accepted | Undercity | WAITPLAYER | covered: party.uc.answer -> UndercityParty.cs |
| 0x0fe | 0x50e320* | Undercity registration cancelled | Undercity | 10943 | covered: party.uc.cancel -> UndercityParty.cs |
| 0x0ff | 0x50e130* | Undercity start | Undercity | enter the city's Undercity stage (0x717250), fee taken | covered: party.uc.start -> UndercityStage.Online.cs |
| 0x100 | 0x50e1a0* | Undercity boss result | Undercity | "Undercity bosshuv success / fail" | covered: party.uc.result -> UndercityStage.Online.cs |
| 0x105 | 0x4f33b0* | mission data | missions | character mission field | covered: Progress.Reward('quest') / profile |
| 0x107 | 0x4f35c0* | quest accepted | missions | 10122 "[%s] mission accepted.", reward lines 11331/11341 | covered: QuestTracker.cs (local) + profile.update |
| 0x109 | 0x4feae0 | quest list / anniversary quest | missions | QUEST 460000, eq_thirdanni | covered: QuestTracker.cs |
| 0x10b | 0x4f3730* | quest completed | missions | 10123, level up 10053, Exp/Mito lines | covered: reward('quest') -> Progress.Reward |
| 0x10d | 0x4f3960* | quest failed | missions | 10709 failed, 10125 retry question, 10753 | covered: QuestTracker.cs (local rule) |
| 0x10f | 0x4f3a10 | quest abandoned | missions | 10124 abandoned, 10753 | covered: QuestTracker.cs |
| 0x111 | 0x4fa640* | quest reward info | missions | QUEST reward lines | covered: QuestTracker.cs |
| 0x113 | 0x4fafe0* | friend logged on | party / friends | 11412 "[Friend] %s has logged on." | covered: friend.online -> Friends.cs |
| 0x119 | 0x4f30a0* | Virtual HUV entry | Virtual HUV | VC_Enter dialog line (QUESTDIALOG) | covered: VirtualChase.cs (offline rule) + reward('vhuv') |
| 0x11b | 0x4f3110* | Virtual HUV registration | Virtual HUV | 10942 registered / 10943 cancelled | n/a: V-HUV runs against AI offline/online |
| 0x11c | 0x4fa510* | Virtual HUV invite | Virtual HUV | 10940/10941 invite, 11145 rush-time refusal | n/a: no V-HUV matchmaking |
| 0x11f | 0x4f3270* | Virtual HUV registration cancelled | Virtual HUV | 10943 | n/a: V-HUV matchmaking |
| 0x122 | 0x504910* | Virtual HUV battle start | Virtual HUV | ChasingBattleStart, VC_HUVNow/Next, UI effect 18, sound 0x4ecd | covered: VirtualChase.cs (local) |
| 0x124 | 0x4fe260 | Virtual HUV hit | Virtual HUV | [VC]SetPoint, 10757 Damage, Mito line | covered: VirtualChase.cs (local) |
| 0x126 | 0x4efd30* | ack | network | nothing | n/a |
| 0x127 | 0x4fe760 | Virtual HUV selection | Virtual HUV | 10975 "You will soon be selected to be the HUV.", vc_lightning.nif | covered: VirtualChase.cs (local) |
| 0x128 | 0x503380* | Virtual HUV result | Virtual HUV | 10973 won / 10974 lost, EXP/Mito, VC_Cancel_Msg | covered: reward('vhuv') -> Progress.Reward |
| 0x129 | 0x4fe580 | Virtual HUV points | Virtual HUV | [VC]SetPointA/B | covered: VirtualChase.cs |
| 0x12b | 0x4f3340 | Virtual HUV waiting count | Virtual HUV | 11003 "Virtual HUV %dvs%d Waiting Players : %d" | n/a: no V-HUV queue |
| 0x12d | 0x4f1e00* | join area ack | city / remote cars | JoinArea Ack (log) | covered: enter reply -> OnlineSession.EnterCity |
| 0x130 | 0x4f1e70 | parking lot floor | stages | floor switch 0x761c90 / camera | covered: ParkingLotStage.cs (local) |
| 0x142 | 0x4fa690* | delivery list | missions | QUEST/INSTANTMISSION delivery list (0x55d120) | covered: DeliveryService.cs (local) |
| 0x144 | 0x4f4800* | delivery accepted | missions | 10038/10655 go-to question, 10133 accepted, 10659 Time Attack | covered: DeliveryService.cs (local) |
| 0x146 | 0x4f4990 | delivery completed | missions | rank list QSRANKLIST 2830000, 10076 Rank, week/month/year records, new record effect, level up | covered: reward('delivery') -> Progress.Reward (records local) |
| 0x147 | 0x4f4b50* | delivery failed | missions | 10137 "[%s] failed." | covered: DeliveryService.cs (local) |
| 0x14a | 0x4f4ba0* | delivery abandoned | missions | 10124 | covered: DeliveryService.cs (local) |
| 0x14c | 0x4f4c00* | destination reached | missions | 10126 "[%s] Destination <%s> arrived." | covered: DeliveryService.cs (local) |
| 0x150 | 0x4f4ce0 | delivery ready / cancelled | missions | 10130/10131, 10656/10657 Time Attack | covered: DeliveryService.cs (local) |
| 0x151 | 0x4f4d50* | delivery state | missions | instant mission object update | covered: DeliveryService.cs |
| 0x154 | 0x4fa6e0* | delivery time record | missions | "%u:%02u:%02u" record into QUEST | covered: DeliveryService.cs (local) |
| 0x168 | 0x4f4d80 | level-up broadcast | progress | 10054 "%s has reached level %d." system chat line (colour 0xFFF29177) for another driver | covered (new): notice{kind:'level'} -> ServerNotices |
| 0x169 | 0x4f4e00* | own level up | progress | 10053 level-up text + levelup.nif + SID_LEVELUP | covered: reward/driver reply -> Progress.cs level-up |
| 0x17c | 0x4fa5e0* | main quest reward info | missions | reward lines (mq_5th_003) | covered: QuestTracker.cs |
| 0x17e | 0x4f33d0* | main quest accepted | missions | 10122, sound 0x4e92, mq_5th_003 | covered: QuestTracker.cs (local) |
| 0x180 | 0x4f3520* | main quest completed | missions | 10123, SID_QUEST_SUCCESS, UI effect 12 | covered: reward('quest') -> Progress.Reward |
| 0x182 | 0x4efa10* | main quest reward refresh | missions | QUEST reward lines | covered: QuestTracker.cs |
| 0x18d | 0x4efd10* | parts shop refresh | parts / licences | PARTSSHOP 1200000 list | covered: perf.* reply -> Workshop.cs |
| 0x18f | 0x4efc90* | option added to part | parts / licences | POPUP 11432 "The option has been successfully added to the part." | covered: perf.enhance reply -> Workshop.cs |
| 0x191 | 0x4fa720 | item list refresh | items | inventory item records (0x6e37c0) | covered: driver push -> OnlineSession.ApplyDriver / InventoryWindow |
| 0x192 | 0x4fa760 | item obtained (reward) | items | quick slots, SID_HUV_SUCCESS(0x4e90) + UI effect 55, 10039 inventory full | covered: reward reply -> Progress.Reward |
| 0x194 | 0x4f4ea0 | item info line | items | chat line via 0x6f3f10 ("%s%%%s") | covered: item.* reply -> ItemUse.cs |
| 0x196 | 0x4efba0 | item bought | items | chat line 0x6f3fe0, dealer refresh | covered: item.buy reply -> Progress.Op |
| 0x198 | 0x4f4ee0 | item sold | items | chat line 0x6f4040, Mito update | covered: item.sell reply -> Progress.Op |
| 0x19a | 0x4f4f60* | item equipped | items | 10100 "Equipped %s" | covered: perf.fit reply -> Workshop.cs |
| 0x19c | 0x4f4f90* | item unequipped | items | 10101 "Unequipped %s" | covered: perf.fit reply -> Workshop.cs |
| 0x19e | 0x4f4ff0 | part enhanced / upgraded | parts / licences | 10102-10104 results | covered: perf.enhance reply -> Workshop.cs |
| 0x1a0 | 0x4efc40* | ack | network | fixed length | n/a |
| 0x1a2 | 0x4f5160 | item used | items | item use result: quick slots, booster/repair items i_n_00055..00099 | covered: item.use reply -> ItemUse.cs |
| 0x1a4 | 0x4efc50* | item ack + dealer refresh | items | AUTOSHOPADVBG refresh | covered: item.* -> Progress.Op |
| 0x1a5 | 0x4fa8b0 | item count update | items | item record (0x6e1080) | covered: driver push -> ApplyDriver |
| 0x1a7 | 0x4efc40* | ack | network | fixed length | n/a |
| 0x1a9 | 0x4efd30* | ack | network | fixed length | n/a |
| 0x1ab | 0x4f53a0* | parts converted | parts / licences | 10732 "%s was successfully converted.", OptionCraft_Guarantee, sound 0x4f1d, UI effect 56 | covered: perf.combine/craft reply -> Workshop.cs |
| 0x1ad | 0x4f5580* | parts combined | parts / licences | 10734 / 10736 | covered: perf.combine reply -> Workshop.cs |
| 0x1af | 0x4f50d0 | item discarded (part) | items | chat line 0x6f5ff0 | covered: item.discard -> Progress.Op |
| 0x1b1 | 0x4efd40* | item ack | items | item lookup only | covered: item.* -> Progress.Op |
| 0x1b4 | 0x4f5060 | item expiry / grind | parts / licences | 10882 line | covered: perf.grind -> Workshop.cs |
| 0x1b7 | 0x4efd40* | item ack | items | item lookup only | covered: item.* |
| 0x1b9 | 0x507380 | arena limits | Battle Zone | MinBattle*/MaxBattle* globalcontent limits, Championship tooltips (PVP.md) | covered: race.list rooms / arenas -> PvpLobby.cs |
| 0x1be | 0x50a170* | room join failed | Battle Zone | 10027 / 10028 POPUP, sound 0x4ed7 | covered: race.join reply -> PvpLobby.cs |
| 0x1bf | 0x50b140 | room info / join | Battle Zone | room state, slots, players (PVP.md) | covered: race.room -> OnlineRace.cs / PvpWaitRoom.cs |
| 0x1c3 | 0x509af0 | battle invite list | Battle Zone | FRIENDINVITE 1840000 rows | covered: race.invitees -> PvpSocial.cs |
| 0x1c5 | 0x508280* | ack | network | fixed length | n/a |
| 0x1c7 | 0x5085c0 | room item | Battle Zone | item record (0x6e7070) | covered: race.room -> OnlineRace.cs |
| 0x1c8 | 0x508c00* | Real Match countdown | Battle Zone | {seconds, type}: 10840-10845 "Real Match will end in ..." on EMERGENCY 2770000 | covered: race.rm -> OnlineRace.cs (notice text) |
| 0x1ca | 0x508f20 | channel battle room | crews / channel battle | TC 590000 refresh | covered: crew battle (teams.js) -> TeamBattle (Teams.cs) |
| 0x1cb | 0x507a30* | spectator count | Battle Zone | 10244 "%d Spectators" | covered: race.room spectators -> PvpWaitRoom.cs |
| 0x1cd | 0x507930* | room sound | Battle Zone | sound 0x4ebe | covered: race.* -> OnlineRace.cs |
| 0x1d0 | 0x508f80* | room list | Battle Zone | TC/PVP room rows, 11067 Mito needed, 10321-10324 channel labels, 11391 event channel | covered: race.rooms -> PvpLobby.cs |
| 0x1d1 | 0x509260* | battle invite | Battle Zone | 10026 "You have been invited by %s to join a Battle Zone match." | covered: race.invite -> PvpSocial.cs |
| 0x1d2 | 0x509360* | battle invite (alt) | Battle Zone | 10026 | covered: race.invite -> PvpSocial.cs |
| 0x1d3 | 0x507990* | room player update | Battle Zone | slot/player change | covered: race.room -> PvpWaitRoom.cs |
| 0x1d4 | 0x509460* | player joined room | Battle Zone | sound 0x4ed7, slot fill | covered: race.room -> PvpWaitRoom.cs |
| 0x1d5 | 0x507a90* | player left room | Battle Zone | sound 0x4ed8 | covered: race.room -> PvpWaitRoom.cs |
| 0x1d6 | 0x507ae0 | kicked from room | Battle Zone | 10029 "You have been kicked from the room." | covered: race.kicked -> OnlineRace.cs |
| 0x1da | 0x5095d0 | channel tournament completed | crews / channel battle | 20210 "%d Channel Tournament completed." / 20289 semi; chat line (cset) + POPUP | missing: crew channel tournaments: teams.js has no tournament end broadcast) -> notices.broadcast hook listed |
| 0x1e2 | 0x507db0 | room setting | Battle Zone | room setting change | covered: race.settings -> PvpWaitRoom.cs |
| 0x1e4 | 0x509a40* | reward pick result | Battle Zone | picks "%d %d %d %d %d", 11197-11199 Mito/EXP/Mileage received | covered: race.picked -> PvpRewardPick.cs |
| 0x1e5 | 0x507e10* | race load | Battle Zone | race loading state | covered: race.start -> OnlineRace.cs |
| 0x1e7 | 0x507e80* | race ready state | Battle Zone | ready flag | covered: race.room -> PvpWaitRoom.cs |
| 0x1e8 | 0x507ed0* | race ack | Battle Zone | nothing visible | covered: race.* |
| 0x1ea | 0x509fe0 | Pvp start | Battle Zone | server start time -> countdown | covered: race.go -> OnlineRace.cs |
| 0x1eb | 0x508080 | player finished | Battle Zone | finish time | covered: race.finished -> OnlineRace.cs |
| 0x1ec | 0x50a040* | race result | Battle Zone | result table (normal / tournament), counters, sound 0x4ec2 | covered: race.results -> PvpResults.cs |
| 0x1ee | 0x507f00 | race standings | Battle Zone | positions, sound 0x4ec3 | covered: race.standings -> OnlineRace.cs |
| 0x1ef | 0x5080e0* | race end time | Battle Zone | state 9 + pvp_tencount | covered: race.endTime -> OnlineRace.cs |
| 0x1f0 | 0x508050* | race ack | Battle Zone | nothing visible | covered: race.* |
| 0x1f1 | 0x508130* | race player info | Battle Zone | PVP 1620000 player panel | covered: race.room -> PvpWaitRoom.cs |
| 0x1f2 | 0x5081f0* | race ghost / spectate | Battle Zone | 0x75a760 | covered: race.observe -> PvpSpectate.cs |
| 0x1f3 | 0x509810 | wager result | Battle Zone | 11083-11086 Mito issued/lost, RESULT_SINGLEBB/TEAMBB | covered: race.wager -> PvpWager.cs |
| 0x1f5 | 0x4fcf10 | trade window open | trade | "Trade Window Open", TRADE 1190000, MAINBUTTON | covered: trade.open -> TradeWindow.cs |
| 0x1f6 | 0x4fd160 | trade request | trade | 10726 "%s has requested a deal. Accept?" | covered: trade.invite -> TradeWindow.cs |
| 0x1f9 | 0x4f55d0* | trade item placed | trade | quick slot refresh | covered: trade.state -> TradeWindow.cs |
| 0x1fb | 0x4f5630* | trade Mito offer | trade | amount | covered: trade.state -> TradeWindow.cs |
| 0x1fd | 0x4f5680* | trade lock | trade | lock state | covered: trade.state -> TradeWindow.cs |
| 0x1ff | 0x4fcfe0* | trade closed / cancelled | trade | "Trade Window Close", 10995 age check | covered: trade.closed -> TradeWindow.cs |
| 0x201 | 0x4fd2c0* | trade completed | trade | window close | covered: trade.closed -> TradeWindow.cs |
| 0x203 | 0x4f56d0* | trade items moved | trade | quick slot refresh | covered: trade.closed + driver -> TradeWindow.cs |
| 0x20a | 0x4f2fd0* | quest UI refresh | missions | QUEST 1780000 | covered: QuestTracker.cs |
| 0x213 | 0x509ec0 | race track / stage | Battle Zone | stage load (0x761580) | covered: race.start -> OnlineRace.cs |
| 0x215 | 0x5083a0* | race ack | Battle Zone | 0x756690 | covered: race.* |
| 0x217 | 0x5083c0* | race ack | Battle Zone | fixed length | covered: race.* |
| 0x21b | 0x4ec480* | remote vehicle state | city / remote cars | car state of another player | covered: s snapshot -> RemotePlayers.ApplySnapshot |
| 0x21c | 0x50c960* | time sync (alt) | network | same handler as 0x003 | covered (new): pong.now -> ServerNotices |
| 0x21d | 0x4ec010* | remote vehicle move | city / remote cars | MoveVehicle | covered: s -> RemotePlayers |
| 0x21e | 0x4ec1e0* | remote vehicle flag | city / remote cars | state flag | covered: s -> RemotePlayers |
| 0x21f | 0x4ed6c0* | login reply (KR/region) | login | 10045-10051, 10983 maintenance, 10984, 10985, 11417 blocked, 11418 servers not in service | n/a: login (ban / maintenance texts via ServerNotices |
| 0x220 | 0x4ec330* | remote vehicle flag | city / remote cars | state flag | covered: s -> RemotePlayers |
| 0x221 | 0x4ebf30* | remote vehicle move ext | city / remote cars | MoveVehicleExt | covered: s -> RemotePlayers |
| 0x222 | 0x4eb560* | remote vehicle create | city / remote cars | spawn another player's car | covered: player.joined -> RemotePlayers.Add |
| 0x223 | 0x4ec2c0* | remote vehicle flag | city / remote cars | state flag | covered: s -> RemotePlayers |
| 0x224 | 0x4ebe50* | remote vehicle move simple | city / remote cars | MoveVehicleSimpl | covered: s -> RemotePlayers |
| 0x225 | 0x4ec100* | remote vehicle jump | city / remote cars | JumpVehicle | covered: s -> RemotePlayers |
| 0x226 | 0x4eb490* | remote vehicle removed | city / remote cars | RemoveVehicle | covered: player.left -> RemotePlayers.Remove |
| 0x228 | 0x4ecf50* | remote vehicle ack | city / remote cars | fixed length | covered: s |
| 0x233 | 0x4eb1a0* | enter area | city / remote cars | 10609-10613/11394 "Entering <city>", 10614 PvP, 10615 chase, gate/channel labels | covered: channel.joined -> OnlineSession (system line) |
| 0x23b | 0x4ec720 | chat (area scope) | chat | area chat line (cset) | covered: chat -> ChatBox |
| 0x23c | 0x4ed280* | party member combo | party / friends | AddCombo(fromParty) + booster gauge (COMBO.md) | covered: party.combo -> PartyWindow.cs / ChaseMission.cs |
| 0x23e | 0x4ece70* | remote vehicle move (gate status) | city / remote cars | MoveVehicle + gate status 10762-10764 | covered: s -> RemotePlayers |
| 0x241 | 0x50c500 | rush boss switch | Rush Time | Rush Switch (boss HUV control handover) | n/a: Rush Time simulated locally, RushTime.cs |
| 0x242 | 0x4eb880* | party member state | party / friends | party list refresh | covered: party.update -> PartyWindow.cs |
| 0x245 | 0x4fa9d0* | auction ack | auction | fixed | covered: auction.* -> AuctionMarket.cs |
| 0x247 | 0x4f5740 | auction registered | auction | 10117 "Registered %s for auction." | covered: auction.register reply -> AuctionMarket.cs |
| 0x249 | 0x4f57c0* | auction cancelled | auction | 10118 "Auction canceled." | covered: auction.cancel reply -> AuctionMarket.cs |
| 0x24b | 0x4f58a0 | auction item info | auction | item lookup | covered: auction.list -> AuctionMarket.cs |
| 0x24d | 0x4faa00* | auction bought | auction | 10505 | covered: auction.buy reply -> AuctionMarket.cs |
| 0x251 | 0x4f57f0 | auction list | auction | AUCTION 1300000 rows (AuctionCheckType) | covered: auction.list -> AuctionMarket.cs |
| 0x252 | 0x4f5860* | auction item sold | auction | 10120 "%s has been sold." (AuctionBuyedMsg) | covered: auction.sold -> AuctionMarket.cs |
| 0x254 | 0x4faa00* | auction bought (alt) | auction | 10505 | covered: auction.buy -> AuctionMarket.cs |
| 0x26d | 0x507950* | PvP lobby info | Battle Zone | lobby state | covered: race.lobby -> PvpLobby.cs |
| 0x26e | 0x507d70* | PvP lobby ack | Battle Zone | nothing visible | covered: race.lobby |
| 0x271 | 0x509550* | PvP room ack | Battle Zone | nothing visible | covered: race.room |
| 0x280 | 0x4f5900* | ranking ack | Battle Zone | ranking record | covered: leaderboard -> Ladder.cs |
| 0x295 | 0x4f59a0* | user info card | party / friends | USERINFO 1810000/1830000: 10159-10161 wins, 10729 km | covered: friend.locate / race.ladder -> Friends.cs / Ladder.cs |
| 0x297 | 0x4f5bc0* | ladder info | Battle Zone | PVP 1090000: wins, 11055 place/rounds | covered: race.ladder -> Ladder.cs |
| 0x299 | 0x4f9140* | ladder ack | Battle Zone | PVP 1090000 refresh | covered: race.ladder -> Ladder.cs |
| 0x29b | 0x4f1880* | licence list | parts / licences | licences "Total %d licenses", DIALOG 4710000, MAINBUTTON, sound 0x4e8a | covered: licence.earn/mount reply -> LicenceInfo.cs |
| 0x29c | 0x4f1b10* | licence conditions | parts / licences | GetCond lines | covered: licence.* -> LicenceInfo.cs |
| 0x2ab | 0x4ec560* | gate / channel capacity | city / remote cars | SELCH 3390000 / SELGATE 3620000 status 10762 Few / 10763 Many / 10764 Crowded | covered: channels reply -> ParkingLotStage.cs (gate status) |
| 0x2ad | 0x4eb5e0 | area status (debug) | city / remote cars | "------area status------" log lines | n/a: debug |
| 0x2ae | 0x4eb0f0* | area ack | city / remote cars | nothing visible | n/a |
| 0x2bd | 0x4ec250* | remote vehicle flag | city / remote cars | state | covered: s -> RemotePlayers |
| 0x2c2 | 0x4ec3a0* | remote vehicle flag | city / remote cars | state | covered: s -> RemotePlayers |
| 0x2c8 | 0x4ec8e0 | area player list | city / remote cars | players of the area (bulk) | covered: channel.joined players -> RemotePlayers |
| 0x2d0 | 0x4ec410* | remote vehicle flag | city / remote cars | state | covered: s -> RemotePlayers |
| 0x2d4 | 0x4ff020* | Mittron station owner | crews / channel battle | 10168 "Owned by: %s Crew" / 10169, "1L / %u Mito" (CHARGER 940000/950000) | covered (SERVICES.md): station.info -> Services/StationOwners.cs (CHARGER OWNTEAM 10168 / 10169); fees station.fill + hourly 20268 / 20288 |
| 0x2d5 | 0x4ec4f0* | remote vehicle flag | city / remote cars | state | covered: s -> RemotePlayers |
| 0x2e5 | 0x4f5ca0 | reward item / inventory full | items | 10039 / 10040 inventory full, getitem effect + SID_ITEM_DROP_GET1, gacha/event dialogs refresh | covered: reward reply -> Progress.Reward (full-inventory text from the server error) |
| 0x2f8 | 0x4efd60* | ack | network | fixed length | n/a |
| 0x2f9 | 0x4efd80* | ack | network | fixed length | n/a |
| 0x2fa | 0x4efda0* | character flag | characters | character field | covered: driver push |
| 0x30d | 0x504ef0* | channel select list | city / remote cars | SELCH 3390000 rows | covered: channels -> ParkingLotStage.cs |
| 0x30f | 0x4f5e80* | zone move | stages | zone 0 parking lot / 1-5 cities (PARKINGLOT.md), sound 0x4e8d | covered: enter reply -> OnlineSession.EnterCity / GameFlow |
| 0x311 | 0x4f61a0 | zone data | stages | zone record | covered: enter reply |
| 0x313 | 0x4f60f0 | zone name | stages | "Main" world name | covered: enter reply |
| 0x315 | 0x504f40* | gate list | city / remote cars | SELGATE 3620000 | covered: channels -> ParkingLotStage.cs |
| 0x316 | 0x4faa50 | gate details | city / remote cars | SELGATE / TC: 10834 "%2d Cap." | covered: channels -> ParkingLotStage.cs |
| 0x317 | 0x4f1dd0* | channel flags | city / remote cars | channel ids | covered: channels |
| 0x318 | 0x4f1db0* | channel flag | city / remote cars | net flag | covered: channels |
| 0x321 | 0x503ff0* | car parts / visual data | garage | car record (`mX) | covered: driver push -> ApplyDriver |
| 0x322 | 0x4f9a90 | car data ack | garage | car record | covered: driver push |
| 0x323 | 0x4eb080* | car data | garage | car record | covered: driver push |
| 0x324 | 0x504330 | sticker applied | custom shop | ApplySticker | missing: custom stickers |
| 0x326 | 0x4ef430 | character data | characters | record | covered: driver push |
| 0x328 | 0x4f69f0* | character data | characters | record | covered: driver push |
| 0x329 | 0x4f9a20 | car data | garage | record | covered: driver push |
| 0x32a | 0x4f1c10* | net flag | network | net+0x... dword | n/a |
| 0x32c | 0x5044b0 | licence list (other) | parts / licences | "license: %s(%d)=%d" | covered: licence.* -> LicenceInfo.cs |
| 0x32e | 0x5045a0 | licence conditions / reputation | parts / licences | GetCond, GC_Reputation_01..03 | covered: licence.* -> LicenceInfo.cs |
| 0x330 | 0x4fde60 | ME skills | parts / licences | ME_AcroComboRepair / ReverseComboRepair / UseBoost50Recovery | covered: skill.learn reply -> Workshop.cs |
| 0x331 | 0x4ef4a0 | new licence | parts / licences | LICENSE 1860000 "new license" | covered: licence.earn reply -> LicenceInfo.cs |
| 0x337 | 0x4eb8b0 | spectator count | Battle Zone | 10052 "%hd Spectators" | covered: race.room -> PvpSpectate.cs |
| 0x33f | 0x50ce40* | crew data | crews / channel battle | crew record | covered: crew.info -> Teams.cs |
| 0x341 | 0x50dd70* | crew data | crews / channel battle | crew record | covered: crew.info -> Teams.cs |
| 0x343 | 0x50cea0* | crew web address check | crews / channel battle | 10634 | covered: crew.check -> Teams.cs |
| 0x349 | 0x50cb20* | crew create check | crews / channel battle | 10313 fee / 10314 level, TC 590000 | covered: crew.create reply -> Teams.cs |
| 0x34b | 0x50d7a0 | crew created | crews / channel battle | 10813 "You've created %s Crew." + 10333-10335 certificate | covered: crew.create reply -> Teams.cs |
| 0x34d | 0x50cd10 | crew ack | crews / channel battle | fixed | covered: crew.* |
| 0x34f | 0x50d990* | crew join answer | crews / channel battle | 10016 joined / 10017 denied | covered: crew.notice joined/denied -> Teams.cs |
| 0x351 | 0x50db10* | crew left | crews / channel battle | 10018 / 10019 | covered: crew.leave reply -> Teams.cs |
| 0x354 | 0x50cd40 | crew name check | crews / channel battle | 10328 / 10329 | covered: crew.check -> Teams.cs |
| 0x356 | 0x50cd10 | crew ack | crews / channel battle | fixed | covered: crew.* |
| 0x358 | 0x50dc70 | crew info | crews / channel battle | TC / COMM / PVP crew panels | covered: crew.info -> Teams.cs |
| 0x359 | 0x50d5d0* | crew member page | crews / channel battle | COMM: 10378-10388 ranks, 10269 | covered: crew.info -> Teams.cs |
| 0x35b | 0x50ccc0 | crew user info | crews / channel battle | USERINFO2ND 1830000 | covered: crew.info -> Teams.cs |
| 0x35d | 0x50df20* | channel owners | crews / channel battle | 10 major + 10 semi channels owned by crews (0x678c70) | covered: crew battle state (teams.js) -> TeamBattle |
| 0x35f | 0x50d610* | channel battle state | crews / channel battle | TC 590000 | covered: teams.js -> TeamBattle |
| 0x365 | 0x50cd90* | crew web address | crews / channel battle | 10636 / 10637 | covered: crew.check -> Teams.cs |
| 0x367 | 0x50cf00 | crew join request | crews / channel battle | 10020 "Do you approve %s to join your crew?" | covered: crew.request -> Teams.cs |
| 0x369 | 0x50d4d0 | crew leave request | crews / channel battle | 10022 | covered: crew.request -> Teams.cs |
| 0x36b | 0x50dde0* | kicked from crew | crews / channel battle | 10023 "You have been kicked from %s's crew." | covered: crew.notice kicked -> Teams.cs |
| 0x36c | 0x50d020 | crew join denied | crews / channel battle | 10021 | covered: crew.notice denied -> Teams.cs |
| 0x36d | 0x50d0f0 | crew notices | crews / channel battle | 10814-10822 delete/join/leave/kick/leader, 10838/10839 promoted, 11305 renamed | covered: crew.notice -> Teams.cs ("promoted" uses its own wording instead of 10838/10839) |
| 0x375 | 0x50d5c0* | crew ack | crews / channel battle | fixed | covered: crew.* |
| 0x384 | 0x50d680* | channel battle ack | crews / channel battle | nothing visible | covered: teams.js |
| 0x386 | 0x50d640* | channel battle round | crews / channel battle | 10343-10347 Waiting/Round 1/2/Final/Channel, 11391 | covered: crew.notice battle -> TeamBattle.NoticeLine |
| 0x388 | 0x50dfe0 | channel battle spectate | crews / channel battle | 10199-10201, 11261 Replay (SELCH) | missing: channel battle spectating/replays |
| 0x38d | 0x50e020* | ack | network | fixed | n/a |
| 0x38f | 0x50d6a0* | channel battle state | crews / channel battle | TC | covered: teams.js -> TeamBattle |
| 0x390 | 0x50dfb0 | channel battle state | crews / channel battle | TC | covered: teams.js -> TeamBattle |
| 0x391 | 0x50e030 | channel battle state | crews / channel battle | TC | covered: teams.js -> TeamBattle |
| 0x392 | 0x50e060 | channel battle state | crews / channel battle | TC | covered: teams.js -> TeamBattle |
| 0x393 | 0x50d6c0* | channel battle popup | crews / channel battle | POPUP | covered: teams.js -> TeamBattle |
| 0x394 | 0x50a4b0* | booster refill | Battle Zone | booster gauge +, UI effect 42 | covered: local (car booster) |
| 0x396 | 0x50ac40* | room ack | Battle Zone | nothing visible | covered: race.* |
| 0x398 | 0x508290 | not enough Mito | shop | POPUP 10293 | covered: reply error -> UiPopup |
| 0x3df | 0x4f3b20 | instant mission offer | missions | NewInstant: 10462 EXP per post, 10914/10915 rewards, PARTY/INSTANTMISSION | covered: InstantMissions.cs (local) + reward('instant') |
| 0x3e1 | 0x4fec70* | party patrol start | missions | 11156 "Party Patrol Start!", sound 0x4ecd, UI effect 18 | covered: party.patrol.start -> PatrolMission.cs |
| 0x3e3 | 0x4f3da0* | party patrol completed | missions | 11157 | covered: party.patrol.end -> PatrolMission.cs |
| 0x3e5 | 0x4f3ff0* | patrol completed (reward) | missions | 10123, Exp/Mito, level up | covered: party.patrol.end -> PatrolMission.cs |
| 0x3e6 | 0x4efa30* | party patrol failed | missions | 11158 / 10992, sound 0x4e8f, UI effect 54 | covered: party.patrol.end -> PatrolMission.cs |
| 0x3e8 | 0x4f3ed0* | party leader quit patrol | missions | 11159 | covered: party.patrol.left -> PatrolMission.cs |
| 0x3ea | 0x4f4140* | patrol EXP | missions | "Exp. %d", level up | covered: party.patrol.post -> PatrolMission.cs |
| 0x3f4 | 0x4efe90* | coupon counter | events | counter singleton 0x6ce4b0 | covered: coupon.sync -> CouponBook.cs |
| 0x3f6 | 0x4fb1b0* | coupon counters | events | counters | covered: coupon.sync -> CouponBook.cs |
| 0x3f8 | 0x4f66b0* | coupon available | events | 10143-10151, 10660, 10790, 10846 "... Coupon now available." | covered: coupon.sync reply -> CouponBook.cs |
| 0x3fa | 0x4f6730* | coupon ack | events | counter | covered: coupon.* |
| 0x3fc | 0x4f6760* | coupon book refresh | events | COUPONSTAMP 1910000 / COUPONBOOK 3690000 | covered: coupon.* -> CouponBook.cs |
| 0x3fd | 0x4efe70* | coupon counter | events | counter | covered: coupon.sync |
| 0x410 | 0x4f67d0* | combo EXP | progress | 10041 "Combo Exp %u", level up | covered: reward('combo') -> Progress.Reward |
| 0x425 | 0x4f6840 | custom shop list | custom shop | VSHOP 2280000 | covered: local catalogue (CustomShop.cs) |
| 0x457 | 0x50c040* | rush boss explosion | Rush Time | huv_boss*_explo.nif | n/a: Rush Time local |
| 0x459 | 0x50c0d0* | rush ack | Rush Time | nothing visible | n/a |
| 0x45b | 0x50c110* | rush boss state | Rush Time | boss HUV | n/a: Rush Time local |
| 0x45c | 0x50be40* | rush notice | Rush Time | EMERGENCY 2770000 text (Rush Time start/end) | covered: RushTime.cs (local schedule, same notice) |
| 0x45d | 0x50b990* | rush state | Rush Time | counter | n/a: local |
| 0x45e | 0x50bab0 | rush boss create | Rush Time | boss HUV create (Unknown BossHuv) | n/a: local |
| 0x45f | 0x4efd30* | ack | network | fixed | n/a |
| 0x460 | 0x50bc40* | rush ack | Rush Time | state | n/a: local |
| 0x461 | 0x50c220 | rush boss switch | Rush Time | switch boss | n/a: local |
| 0x462 | 0x50bc70* | rush boss HP | Rush Time | boss state | n/a: local |
| 0x463 | 0x50bde0 | rush ack | Rush Time | state | n/a: local |
| 0x464 | 0x50be20* | rush ack | Rush Time | state | n/a: local |
| 0x467 | 0x50b8c0 | rush time update | Rush Time | UpdateRush(...) RUSHINFO 2900000 timetable | covered: RushTime.cs (local UTC schedule; server clock via ServerNotices.UtcNow) |
| 0x47e | 0x504fe0 | Mita spots | missions | today's purchase count + live spots | covered: MitaDrink.cs (local) |
| 0x480 | 0x4f83c0 | Mita drink reply | missions | Mita500_1-4 / Halloween lines (QUESTDIALOG) | covered: reward('mita') / drink -> MitaDrink.cs |
| 0x481 | 0x4f85e0 | special drink applied | missions | 11058 "[%d%% Special Drink effect] applied" | covered: drink -> ItemUse.cs |
| 0x488 | 0x4f8720 | day mission state | events | state | covered: event.state -> EventData.cs |
| 0x489 | 0x4f87b0* | day mission completed | events | DayMission_Complete, TOKENBOOK, 10269 | covered: event.* -> EventData.cs (DayMission) |
| 0x48c | 0x4f8920* | bonus mileage | events | 10917 "%dp Bonus Mileage has been issued." | covered: token.daily / shop.mileage -> TokenBook.cs |
| 0x48d | 0x4f8cb0* | day mission popup | events | POPUP / QUESTDIALOG | covered: event.* -> EventData.cs |
| 0x48f | 0x4fc690* | red envelope event item | events | 11151 + 10096 Obtained | missing: red envelope / hongbao event not rebuilt |
| 0x493 | 0x4f8e60 | Greenday prop event | events | Event_Greenday_01-03 dialog | missing: collect-3-props event documented only |
| 0x495 | 0x4f8f10* | Greenday event end | events | Event_Greenday_end | missing: props event |
| 0x4b1 | 0x4fa8f0 | item record | items | a_00190 item | covered: driver push |
| 0x4b2 | 0x4fa960* | dealer refresh | car dealer | AUTOSHOPADVBG | covered: driver push |
| 0x4b4 | 0x4f6ab0* | visual item bought | custom shop | 10108 "Purchased %s." | covered: shop.paint / item.buy reply -> CustomShop.cs |
| 0x4b6 | 0x4f6e90* | visual item equipped | custom shop | 10113 "%s has been equipped." | covered: shop.paint reply -> CustomShop.cs |
| 0x4b8 | 0x4f6ec0* | visual item unequipped | custom shop | 10114 | covered: CustomShop.cs |
| 0x4ba | 0x4f6f20* | item used | items | 10115 "%s has been used." | covered: item.use reply -> ItemUse.cs |
| 0x4bc | 0x4f6a50* | item discarded | items | 10116 "%s has been discarded." | covered: item.discard reply -> Progress.Op |
| 0x4be | 0x4f6fa0 | aeroset done | custom shop | 11064 "Aeroset Setting completed." | covered: perf.fit / profile.update -> Workshop.cs |
| 0x4c0 | 0x4f6fe0* | part data | parts / licences | part lookup | covered: driver push |
| 0x4c3 | 0x4efef0* | dealer ack | car dealer | AUTOSHOPADVBG | covered: driver push |
| 0x515 | 0x4fb230 | purchase history | custom shop | VSHOP 2290000: 10301 "%s (%d days)", 10302 G-Coin, Mileage/Mito | covered: shop.history -> ShopExtras.cs |
| 0x517 | 0x4f6d30* | visual item data | custom shop | part data | covered: driver push |
| 0x519 | 0x4f6df0 | visual item data | custom shop | part data | covered: driver push |
| 0x51b | 0x4efeb0 | character name check | party / friends | 10106 / 10107 | covered: gift.send / note.send reply (name check) -> ShopExtras.cs / Mail.cs |
| 0x51c | 0x4f6f60* | Mito value | items | "%ld" | covered: driver push |
| 0x51e | 0x4f75f0* | anti-macro quiz | system | AUTOFINDER 3420000 quiz, 10555 Sec. | n/a: anti-cheat |
| 0x528 | 0x505b80* | lap-time battle load | Battle Zone | city + lap track (PVP.md) | covered: lap.enter reply -> LapTimeBattle.cs |
| 0x52a | 0x505bf0* | lap-time state | Battle Zone | state | covered: lap.* -> LapTimeBattle.cs |
| 0x52c | 0x505c30* | lap-time state | Battle Zone | state | covered: lap.* -> LapTimeBattle.cs |
| 0x52d | 0x505c50* | lap-time counters | Battle Zone | counters | covered: lap.* -> LapTimeBattle.cs |
| 0x52e | 0x505c80* | lap-time result | Battle Zone | record | covered: race.lapResults -> LapTimeBattle.cs |
| 0x52f | 0x505c10* | lap-time ack | Battle Zone | state | covered: lap.* |
| 0x547 | 0x4fd670* | my sticker list | custom shop | MyStickerLIstAck, VSHOP 2360000 | missing: custom stickers |
| 0x549 | 0x4fd350* | sticker attached | custom shop | attachstickerAck, 10108 | missing: custom stickers |
| 0x54b | 0x4fd530 | sticker detached | custom shop | detachstickerAck | missing: custom stickers |
| 0x54c | 0x4f6c10 | sticker sales income | custom shop | 10640 "You have earned %I64d Mito from the sale of %s stickers." | missing: custom stickers |
| 0x551 | 0x4f7630* | ending record | Battle Zone | "ending record" log | n/a: debug |
| 0x579 | 0x4f7030* | visual item list | custom shop | VSHOP / dealer refresh | covered: driver push |
| 0x5ab | 0x4f8f70* | scratch card open | events | LOTTO 3860000: 10976/10978 | covered: lotto.scratch -> Lotto.cs |
| 0x5ad | 0x4f8ff0* | scratch card prizes | events | three prizes -> 0x5e3c50 | covered: lotto.scratch reply -> Lotto.cs |
| 0x5b5 | 0x4fc5c0 | lucky bag prizes | events | LUCKYBAG 4180000 (UseLuckyBagAck) | covered: event.box reply -> LuckyBag.cs |
| 0x5b7 | 0x4fc5f0 | GW event prizes | events | DIALOG 4200000 (UseGWEventAck) | missing: GW event box |
| 0x5bf | 0x4fc620 | Mito gift received | notes (mail) | HONGBAO 4210000, 10056 "%d Mito received." system line, sender 11153 | covered: note.take / gift.accept -> Mail.cs (Mito text from reply) |
| 0x5c9 | 0x501df0* | note arrived | notes (mail) | MYFRIEND 2960000 note list refresh (new note) | covered: note.new -> Mail.cs |
| 0x5cb | 0x501f10 | note deleted | notes (mail) | 11031 "Message deleted." | covered: note.delete reply -> Mail.cs |
| 0x5cd | 0x502010 | note saved | notes (mail) | 11032 "Message saved.", 11023 no space | covered: note.save reply -> Mail.cs |
| 0x5cf | 0x4f9040 | note sent | notes (mail) | 11033 sent / 11038 no such user / 11034 not attachable / 11035 cannot receive | covered: note.send reply -> Mail.cs |
| 0x5d1 | 0x502140 | note item taken | notes (mail) | 11036 "The item has been removed from the message." | covered: note.take reply -> Mail.cs |
| 0x5d3 | 0x4f93c0 | parking lot move | stages | 0x761e40 + stage 9 | covered: enter reply -> GameFlow (parking lot) |
| 0x5d5 | 0x4f9450* | ack | network | fixed | n/a |
| 0x5dd | 0x4f7210 | auto-drive items | items | i_autoquick / i_automitron / i_autodrive state, MAINBUTTON | covered: AutoDrive.cs (local item effect) |
| 0x5e6 | 0x505070 | daily counters | events | counter record | covered: event.state -> EventData.cs |
| 0x5e8 | 0x4f9320* | daily count | events | "%d / %d" | covered: event.state -> EventData.cs |
| 0x5ea | 0x4f9370* | daily count | events | "%d / %d" | covered: event.state -> EventData.cs |
| 0x6a5 | 0x4fc2b0* | car auction ack | auction | fixed | covered: auction.* -> AuctionMarket.cs |
| 0x6a7 | 0x4f7730 | car registered for auction | auction | 10117 | covered: auction.register -> AuctionMarket.cs |
| 0x6a9 | 0x4f77f0* | car auction cancelled | auction | 10118 | covered: auction.cancel -> AuctionMarket.cs |
| 0x6ab | 0x4f7910 | car auction item | auction | record | covered: auction.list -> AuctionMarket.cs |
| 0x6ad | 0x4fc2e0* | car auction bought | auction | 10505 | covered: auction.buy -> AuctionMarket.cs |
| 0x6af | 0x4f7820 | car bought at auction | auction | 10033 "You have purchased a car" POPUP | covered: auction.buy / auction.take reply -> AuctionMarket.cs |
| 0x6b0 | 0x4f78f0* | car auction ack | auction | fixed | covered: auction.* |
| 0x6b2 | 0x4fc2e0* | car auction bought (alt) | auction | 10505 | covered: auction.buy |
| 0x708 | 0x4f79d0* | ack | network | fixed | n/a |
| 0x76d | 0x4f7a00* | Undercity ack | Undercity | fixed | covered: uc.* |
| 0x7d1 | 0x4f7bb0* | EXP received | progress | 11198 "%d EXP Received", rising EXP line, level up | covered: reward reply -> Progress.Reward |
| 0x7d2 | 0x4f7e60* | Mito received | progress | 11197 "%d Mito Received", rising Mito line | covered: reward reply -> Progress.Reward |
| 0x7d3 | 0x4f7f80* | Mileage received | progress | 11199 "%d Mileage Received" | covered: shop.mileage / reward -> Progress.cs |
| 0x7d4 | 0x4f7ce0 | faction reputation | progress | 11352-11360 reputation with Mafia / Mitro City hall / Airport Base (11361-11363) | missing: Mitro factions not rebuilt |
| 0x835 | 0x508220* | ack | network | fixed | n/a |
| 0x836 | 0x508240* | ack | network | fixed | n/a |
| 0x838 | 0x508260* | ack | network | fixed | n/a |
| 0x839 | 0x509d60 | slot machine state | events | state | covered: gacha.slot -> Gacha.cs |
| 0x83a | 0x509d90 | slot machine result | events | GACHA 4100000 Mito/EXP + symbols, level up | covered: gacha.slot reply -> Gacha.cs |
| 0x9c4 | 0x4f8390 | ack | network | fixed | n/a |
| 0xa29 | 0x4f2150* | kicked from private channel | chat | 11174 / 11173 | covered (SERVICES.md): pchan push / pchan.who -> Services/PrivateChannel.cs |
| 0xa2b | 0x4ef8a0* | private channel admin | chat | 11177 / 11178 | covered (SERVICES.md): pchan push / pchan.who -> Services/PrivateChannel.cs |
| 0xa2d | 0x4f22a0 | private channel users | chat | 11171 "Total of %d users are in the private channel %s." | covered (SERVICES.md): pchan push / pchan.who -> Services/PrivateChannel.cs |
| 0xbb9 | 0x4f7a20* | Undercity stage check | Undercity | "Not the UnderCity Stage." guard | covered: party.uc.* -> UndercityStage.Online.cs |
| 0xbbb | 0x4f7a40* | Undercity ack | Undercity | state | covered: party.uc.* |
| 0xbbd | 0x4f7a60* | HUB destroyed | Undercity | per-player count, "Hub Exp %d", UC_Hurry3 at 80 | covered: party.uc.event -> UndercityStage.Online.cs |
| 0xbbe | 0x4f7aa0 | all HUBs destroyed | Undercity | UC_HUBSuccess1, EXP/Mito lines | covered: party.uc.phase hubsCleared -> UndercityStage.Online.cs |
| 0xbc3 | 0x4f7ff0 | Main HUB stage begin | Undercity | jamming clock, 0x719ae0 | covered: party.uc.phase main -> UndercityStage.Online.cs |
| 0xbc5 | 0x4f8030* | Undercity stage change | Undercity | 0x716410 | covered: party.uc.phase -> UndercityStage.Online.cs |
| 0xbc7 | 0x4f8050 | barrier destroyed | Undercity | "Boss hub. Exp %d" | covered: party.uc.event barrier -> UndercityStage.Online.cs |
| 0xbc8 | 0x4f80d0 | Main HUB destroyed | Undercity | UC_HUBSuccess2, UI effect 0x1d, sound 0x4ed3 | covered: party.uc.phase mainCleared -> UndercityStage.Online.cs |
| 0xbc9 | 0x4f7fd0* | Undercity stage over | Undercity | state 7 | covered: party.uc.phase -> UndercityStage.Online.cs |
| 0xbcc | 0x4f81f0 | Undercity reward | Undercity | "Reward Exp/Mito %d", 11114 120% item | covered: party.uc.result -> UndercityStage.Online.cs |
| 0xc1e | 0x4f4430* | party patrol result | missions | PARTYPATROL 3890000 records | covered: party.patrol.end -> PatrolMission.cs |
| 0xc24 | 0x4f4210* | party patrol begin | missions | patrol object, 11001/11002 alerts, UI effect 53 | covered: party.patrol.start -> PatrolMission.cs |
| 0xc26 | 0x4f42e0* | patrol post reached | missions | "Exp. %d", timer, sound 0x4ece | covered: party.patrol.post -> PatrolMission.cs |
| 0xc27 | 0x501d20* | member posts | missions | "Post %u" per member | covered: party.patrol.post -> PatrolMission.cs |
| 0xc29 | 0x4f45b0 | party HUV spawn | HUV chase | BS_PktPartyHuv: party HUV units, 10615 | covered: chase.party.start -> ChaseMission.MissionNet |
| 0xc81 | 0x4f9180 | mission post info | missions | post_5th_00042/43, "Main" | covered: QuestTracker.cs (local) |
| 0xcef | 0x4f9460* | Christmas tree state | events | XMASTREEEVENT 4440000 + MAINBUTTON icon | covered: xmas.donate reply -> XmasTree.cs |
| 0xcf2 | 0x4f9520* | Christmas tree decorated | events | XMASTREEEVENT icons | covered: xmas.donate reply -> XmasTree.cs (city-complete broadcast 11440-11443 missing: notices hook listed) |
| 0xd48 | 0x5085f0* | GT Championship notice | Battle Zone | Championship_msg8-27 on EMERGENCY 2770000 / POPUP, 11500 | covered: race.tour -> OnlineRace.cs / Tournament.cs |
| 0xd49 | 0x507700* | GT Championship status | Battle Zone | TOUR 1010000: 11508-11517 stages, 10076 Rank, 11582 points, 11500-11503 | covered: tour.state -> Tournament.cs |
| 0xd4a | 0x507b90* | GT Championship message | Battle Zone | Championship_msg3/8 on EMERGENCY | covered: race.tour -> Tournament.cs |
| 0xd4c | 0x5076c0 | GT Championship class result | Battle Zone | TOUR 4450000: classes, 11505 Passed / 11506 Disqualified, 11583/11584/11596 | covered: race.tour -> Tournament.cs |
| 0xd4e | 0x507870 | GT Championship ack | Battle Zone | TOUR 4450000 | covered: tour.* |
| 0xd51 | 0x508b20 | GT Championship bracket | Battle Zone | 11587-11592 group / standby / preliminary | covered: tour.state -> Tournament.cs |
| 0xd53 | 0x5078c0* | GM reward set result | Battle Zone | GM_Rewardset_Success / Fail | n/a: GM tool |
| 0xd55 | 0x5078f0 | GM reward set ack | Battle Zone | reward pick 4310000 | n/a: GM tool |
| 0xd57 | 0x508b90 | GT Championship bracket (alt) | Battle Zone | as 0xd51 | covered: tour.state -> Tournament.cs |
| 0xe11 | 0x4f16a0 | car upgrade result (boom) | garage | 11866 "Boom" / 10501 upgraded | covered: shop.upgrade reply -> Workshop.cs |
| 0xe13 | 0x4fc760* | harvest restaurant | events | GHRestaurantAccept1-3 / Fail, 11899, sounds 0x4f2c-0x4f2e | covered: harvest.deliver reply -> Harvest.cs |
| 0xe15 | 0x4f95d0* | harvest receipts | events | THANKSGIVING 4680000: 11903-11906 First..Fourth, cherry, reward steps | covered: harvest.exchange reply -> Harvest.cs |
| 0xe1b | 0x4efc40* | ack | network | fixed | n/a |
| 0xe1d | 0x4efc40* | ack | network | fixed | n/a |
| 0xe75 | 0x4fc8c0* | crafting list | parts / licences | MAKELIST 4920000: 10771 "Lv %d" | covered: craft reply -> Workshop.cs |
| 0xe79 | 0x4f9660* | crafting state | parts / licences | MAKELIST | covered: craft -> Workshop.cs |
| 0xe7b | 0x4f9720* | crafting state | parts / licences | MAKELIST | covered: craft -> Workshop.cs |
| 0xe7c | 0x4f96c0* | crafting state | parts / licences | MAKELIST | covered: craft -> Workshop.cs |
| 0xe7e | 0x4f9780* | crafting detail | parts / licences | MAKELIST 10771 | covered: craft -> Workshop.cs |

`*` = not a Ghidra procedure (LAB_ target, see 4.).
