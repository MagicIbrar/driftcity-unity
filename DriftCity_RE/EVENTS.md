# Events, gacha, lotto, coupons

Recovered from `DriftCity.exe` (image base 0x400000), and how the Unity remake rebuilds it (`DriftCityOffline/Assets/DriftCity/Runtime/Game/Events/*`,
`Core/RewardGroups.cs`, server `DriftCityServer/src/events.js`). **[exact]** = read from code or data, **[inferred]** = interpretation.
Text ids are `localize\globaltext.xlt` keys (US text quoted). Config keys are `localize\globalcontent.xlt`. Helper scripts used:
`scratchpad\ev\asm.py`, `callers.py`, `binds.py`, `txt.py`, `tdf.py` (see `scratchpad\ev\RE_BRIEF.md`).

## 0. What the code map calls this subsystem

`codemap_functions.csv` puts 1,374 functions (311 KB) here, but its biggest entries belong to other systems: 0x5fbf00 / 0x6a8e30 /
0x6fb8d0 ("MinPatrolGrade", map and patrol code), 0x43a900 (the world loader, which also loads the event tables), 0x4bf750 (game update),
0x5f3fe0 (map view scroll), 0x6a51a0 ("i_post_%05u", delivery posts), 0x6a6d20 (Undercity lightning). The real event code is in these bands
[exact]:

| band | contents |
|---|---|
| 0x5e3b90-0x5e5300 | LOTTO 3860000, LUCKYBAG 4180000 |
| 0x5e9390 | NOTEBOOK 4060000 handler (coupon book / mission book / licences) |
| 0x62ee30-0x6336c0 | coupon book, coupon stamps, coupon notify, coupon NPC popup |
| 0x633990-0x634380 | TOKENBOOK 3700000 |
| 0x634930-0x63c900 | GACHA shop 4560000, popup 4620000, GACHAINVEN 4570000-4610000 |
| 0x672f00-0x673940 | OBEVENT 3330000, SEASONEVENT 3340000, DIALOG3360000 |
| 0x68f130-0x690100 | GACHA slot machine 4100000, GACHARESULT 4110000 |
| 0x690750-0x691330 | EVENT 4700000 (egg-basket gift), EVENT 4690000 ("+N EXP") |
| 0x691420-0x6930d0 | fall harvest: THANKSGIVING 4680000, AUTUMN 4670000 |
| 0x693810-0x694e99 | VALEN 4660000 (Easter baskets), XMASTREEEVENT 4440000 |
| 0x6ccc60-0x6ce4b0 | coupon counters and records (singleton) |
| 0x6f7a40, 0x6f90e0 | coupon threshold check and notice |
| 0x75bb90-0x75d830 | VIEWING 1980000 "Display Gotcha" (Pick'em) |
| 0x4fb280 | op 0x89 "event notice" (36 subtypes) |

Loaders inside 0x43a900: "Coupon Begin" -> `Init/Coupon` (0x62f6e0), "TokenBookMgr" -> `Init/TokenBook` (0x7d3be0). "EventView Begin" is not an
event: it builds the 256x256 render-to-texture picture-in-picture used by PVP GOALIN 1640000 [exact].

## 1. Shared infrastructure [exact]

- **Server config** (op 0x1e, handler 0x503e50) copies 21 dwords to `net+0x244`. The event bits used here: `+0x274 & 0x200`
  BattleZone.LottoPVPReward, `+0x248 & 0x2000000` coupon book (else the older stamp book), `+0x248 & 0x8000000` coupon-code tab,
  `+0x260 & 0x80` InstantMission.DayMission (the coupon NPC acts as Mission Girl), `+0x288 & 0x80` EventChristmasChase (EVENTCHASING
  880000 replaces CHASING 870000), `+0x288 & 0x200` EventHalloween, `+0x28c & 0x1` EventLuckyBag, `+0x290 & 0x2000` EventChristmas,
  `+0x290 & 0x4000` SnowEffectOn, `+0x290 & 0x10000` Event3Year, `+0x290 & 0x20000` EventValentine (also Easter), `+0x290` bits 19-24
  GotchaSeason, `+0x294 & 0x4000000` EventGoodHarvest.
- **Op 0x89 "event notice"** (0x4fb280, table 0x4fc13c): 18 bytes `{sub, a, b, c}`; 36 subtypes that fill the event popups (gacha
  results 9/10/23/24, wager token 11, Mito 15, recycling 16/17, Halloween 21, anniversary day 25, Easter / harvest results 26/27/30/36,
  level gifts 32-34, summer festival 35).
- **Generic item use** op 0x1a1 `{int slot, int count}` (0x78ce10); ack 0x1a2. Item type codes (map 0x6c4e70): giftbox 0x23, lotto 0x25,
  luckybag 0x2d, giftbox_lv 0x2e, giftbox_car 0x30, EventBox 0x32, coupon 0x12, day_mission 0x1a, gacha 0x2b, gacha_key 0x3f,
  reward_piece 0x4a.
- **Data**: `World/Data/UseItemClient.tdf` (1,092 consumables: id | type | name | reward pool | ... | desc | price | sell | value) and
  `init\rewardgroup.xlt` (3,132 rows, 580 pools: GroupId | Reward_Type | ItemId | Reward_Period | Reward_Count | Etc_Value | Etc_Str |
  Probability). The client only reads the pools to list them (gacha shop 0x63b9b0); the server drew from them.

## 2. Gacha ("Gotcha!")

### Capsule shop GACHA 4560000 + prize popup 4620000 [exact]
- Shop `ui+0x33c` (vtable 0x873054: create 0x6397a0, tick 0x63bf70, events 0x63bb70, Show 0x63c3a0); popup `ui+0x340` (vtable 0x87312c,
  SetResult 0x63c670).
- Modes (+0xa4): 0 Car/Coin, 1 Car/Mito, 2 Parts/Coin, 3 Parts/Mito; `PartsGotchaClose` = 1 (US) keeps 0-1. Prices `Gacha_Price_CarCash`
  100 (KR 900) / `CarMito` 20000 / `PartsCash` 100 / `PartsMito` 20000. Lists: pools `rw_gacha_car_{cash|mito}_%02d` by GotchaSeason (old:
  `rw_gacha_item_*`), 7 per page, owned counts "%d" (11330).
- Buy (0x639c80): funds (10300 "You do not have enough G-Coin." / 10293), a free slot (10269), sound, all buttons off, op **0xdac**
  `{isCash, price, category, coins, 0}`. The won item: op 0x89 sub 23/24 -> SetResult; the popup opens when the 3.3 s ANISTART animation ends
  (30 fps flip-book of the `gacha_00NN` frame controls, 11 chasing lights).
- Texts 11482 "Coin 'Gotcha!'", 11483 "Mito 'Gotcha!'", 11602 "Gotcha Select", 11863 (instant-buy warning).
- GACHAINVEN 4570000-4610000 (cars / tuning / set parts / badges): information pages over `rw_gacha_*` pools; the combine of Vehicle Parts
  A-D + Key Parts (`gacha_key`, column 5 `rw_<car>_compound`, not in the client data) into `i_n_<car>_box_vN`.

### Slot machine GACHA 4100000 + GACHARESULT 4110000 [exact]
- Object `ui+0x3f0` (creator 0x68fb70, handler 0x68f570, result handler 0x68f130, Start 0x68fff0). Opened by using a **Slot Ticket Grade
  1-10** (`gacha_observe_v1..v10`, won in the Battle Zone Pick'em, VIEWING 1980000); ITEMRANK0N highlights the grade.
- GIFTBUTTON sends 0x1a1 and hides itself. Op **0x83a** (0x509d90) carries the Mito / EXP gained and three symbols (0 bomb, 1 gold,
  2 silver, 3 bronze); the reels: lever STICK1 0-0.5 s, 2 -0.55, 3 -0.65, 4 -0.95, 3 -1.05, 2 -1.1; reel r runs 20+r random symbols,
  element i lasting max(0.1, 0.3(1-sin(i pi/n))) and crossing the clipped window from -120 to +120 px; reels stop 0, 1, 2; win flash frames
  2-5 at 0.06 s; LIGHT1-5 flicker in the first half of each second. Op 0x89 sub 10 shows GACHARESULT (item icon, name, 11393).
- BG and GACHARESULT_BG carry NATION F=18 but belong with the other controls at H (275, 47) / (305, 523).

### Remake
- `Game/Events/Gacha.cs`: `Gacha.Open()` (shop), `Gacha.OpenSlot(ticket, done)` (slot machine). The capsule animation, the prize popup, the
  reels and the result follow the client. Server: `gacha.buy {mode}`, `gacha.slot {item}`.
- **No real money**: capsules cost Mito. Mito tab = `Gacha_Price_CarMito` (20,000); the Coin tab's G-Coin price is charged at **1,000 Mito
  per coin** (100,000). The HUD shows Mito; the G-Coin field is hidden.
- Season (Etc.GotchaSeason): `(year*12 + month) % 11 + 1`, a new `rw_gacha_car_*_NN` pool each month.
- Slot: three random symbols, a prize from `rw_gacha_ob_vN`, EXP 100 x grade, Mito for three alike (gold 10,000 / silver 5,000 / bronze
  2,000 x grade).
- Limits (server): 100 capsules and 100 slot spins a day, 1.5 s between pulls.
- Remake rule: a capsule sometimes adds a piece of the season's Gotcha car (1 in 8 one of Vehicle Parts A-D, 1 in 40 its Key Parts V1-V4;
  `Gacha.Bonus` / server `gachaBonus`), the pieces GACHAINVEN counts (the client data has no pool that gives them).

### GACHAINVEN 4570000-4610000 [exact]
- Object `ui+0x344` (vtable 0x872bcc: create 0x638530, tick 0x637f10, base handler 0x638f90, Show 0x638f30); handlers 4580000 0x638300,
  4590000 0x638350, 4600000 0x638390, 4610000 0x6383d0. The shop's GACHAINVENNEW (0x63afb0) toggles it (the shop's lights stop meanwhile).
- Base: EXIT back to the shop, LEFT / RIGHT page -1 / +1 (index `shop+0x1b0`, not reset on open) over MAIN / TUNNING / SETPARTS / BADGE,
  wrapping at 2 with globalcontent `PartsGotchaClose` (1 in the US data: only MAIN and TUNNING), else 4. Sound 0x4e8b on every button.
- MAIN (0x634930 layout, 0x6370f0 s = 0, 0x636a20 s >= 1; s = GotchaSeason): one car per season, CARNAME / CARIMAGE[s] for s < 9
  (labels 1/5 Shadow, 2/6 Hunter, 3/4/7 Fate, 8 Monza; the keys add Winner _09). s 1-4: SEASON layout (coin row, Mito row, key parts),
  s >= 5: SEASON5TH (Parts A-D counts ITEMCNT0-3 + key parts ITEMCNT4); the PLUS / EQUAL row is art only. Texts gacha_inform_car_01/02,
  11482 / 11483 / 11609 / 11801, 11484 Rank, 10800-10803; ability = car `Gacha_inform_carstat` 140 "%d (V9)".
- TUNNING (0x637440): `gacha_inform_tune`, 2 coin + 2 Mito rows (air intake, decal) with "%d D" (10280) remaining days, hover tooltip
  (11485, 11481 + 10353 / 10357 / 10355 / 10359; s >= 5 titles 11718 / 11720). SETPARTS (0x6379f0): `gacha_inform_parts`, ability and
  count, 11486 / 11487. BADGE (0x637c20): `gacha_inform_badge`, count, resale price (11488), SELL -> quantity 10420 -> op 0x197
  `{item, qty, slot}` (10419 on a mismatch); hover shows licence 7062 "Gotcha DC Character Badge Collector 1".
- The combine is item use, not this screen: Key Parts (`gacha_key`, 0x5d687b) need every item of the key's compound pool (column 4), else
  chat `gacha_compound_failure`; the key's column 3 names the gift box (`i_n_<car>_box_vN` -> `rw_gacha_rewardcar_<car>_vN`).
- Data gap [exact]: none of the per-season pools the screen asks for (`rw_gacha_car_*_{parts|key|intake|decal}_NN`, `rw_<car>_compound*`,
  `rw_gacha_dcbadge_cash/mito`, ...) is in the client's rewardgroup; only `rw_gacha_dcbadge_*_99` and `rw_gacha_parts_*_{set|badge}_99`.

**Remake** (`Events/GachaInven.cs`): the four pages over remake pools: the car by season (`GachaInven.CarFor`), its parts
`i_n_shadow_A-D` (Shadow) or `i_n_comp_A-D` (the "New Vehicle Parts Set"), key parts `i_n_<car>_k_vN*`; TUNNING lists the first two
items of the season's capsule pools; SETPARTS / BADGE the `_99` pools (BADGE SELL pays the item table's sell price, `gacha.sell`). Key
Parts in the inventory combine with parts A-D of their car into the key's gift box (`GachaInven.Compound`, `gacha.compound`). The
install's `event_car_01.dds` carries newer art (Monza in the Shadow cell).

### Pick'em VIEWING 1980000 "Display Gotcha" [exact]
- Spectator object `game+0x54` (vtable 0x87d7ac; 1980000 handler 0x75bb90, per frame 0x75d830). Spectators only; with 4+ racers, before
  the leader reaches half distance and while holding a Pick'em Ticket (`gacha_observe`) USEITEMPROMPT asks for 8 s (gauge); an AI race
  gives 11398. YES sends 0x834 "init"; 0x835 carries one symbol per finishing place (1 gold, 2 silver, 3 bronze, else bomb) and the
  first three racers are picked (0x836 `{id, on}`); IDSELECTCHECK01-16 change the picks (3 at most, locked at half race); GOLD / SILVER /
  BRONZEnn mark the places; SYMBOL_BG runs three reels (300 px/s) under the picked racers' rows; 0x838 multiplier -> MULTIPLY_Xn (0.3 s
  pop); GAUGEBG: CAR1 before half, CAR2 after, gate icons; 0x837 reports the picks' ranks at each gate after half. HELPBUTTON toggles HELP.
  The banner 1980102 shows with the start. No grade logic in the client.

**Remake** (`Events/PickEm.cs`): the screen over the spectated field (PvpSpectate's order); the server keeps the ticket, symbols and
picks (`pickem.start` / `pickem.pick` / `pickem.finish`, src/events.js; it reads the room's final places, race.js `room.lastResults`).
Remake rules: max(1, n/5) each of gold, silver, bronze shuffled over the places; ticket grade = 1 + 3 per gold, 2 per silver, 1 per
bronze under the picks' final places (max 10) -> `gacha_observe_vN`; an AI race online refuses (11398), offline AI races are allowed
(the stand-in). The field is read from PvpSpectate by reflection until it exposes it (hook below).

## 3. Scratch card LOTTO 3860000 [exact]
- Object `ui+0x16c` (creator 0x5e4900, handler 0x5e4530, Show 0x5e3d80, Update 0x5e3f70, reveal 0x5e3b90). The server opens it (op
  **0x5ab** -> 0x4f8f70 `{slot, prizeA, prizeB, value}`): title 10976 "100% Success Instant Scratch Card", 10978 "What item will you win?" in
  the three lines, frame 1 of each column's scratch frames; with LottoPVPReward the server gauge and its current / next server prize
  (`lotto_00002..4`: "Battle Zone EXP +5/10/30 % for entire server").
- GETPRIZE disables both buttons and sends **0x5ac** `{slot}`; **0x5ad** (0x4f8ff0 -> 0x5e3c50) carries three prizes `{type 1 item / 2 part,
  index, count}`. Scratching: 2 s per column, a random frame at a random alpha each tick, 10979 "Scratching the card..."; each column revealed
  (`lotto_00001` = Bust, its text "Sorry, maybe next time!"); CANCEL back on; 10977 in the chat.
- Cards: `lotto_scratchies_<car>` (Natura, Dew, Serpens, SoraGT, Leopard, Widowmaker) and the Gotcha wings / wheels / aero cards; their
  pool (column 3), e.g. `lotto_Natura` = `lotto_usa_0902` + `lotto_usa_mito` + `lotto_usa_Natura` (one prize per column).

**Remake** (`Lotto.cs`): the same flow; `Lotto.BuyCard(card)` sells cards for the table price x 100 Mito (50,000), 20 a day online; the
gauge counts the cards scratched today (UTC) server-wide, goals 50 / 150 / 500 (offline 5 / 15 / 50); `Lotto.BattleExpBonusPct` /
server `battleExpBonusPct()` give the unlocked Battle Zone EXP bonus (hook below). Server: `lotto.buy`, `lotto.scratch`.

## 4. Lucky bags and gift boxes LUCKYBAG 4180000 [exact]
- Object `ui+0x170` (vtable 0x871858, creator 0x5e50e0, handler 0x5e4cb0). Using a `luckybag` item (needs 4 free slots) stores the bag slot
  and shows the dialog (0x5e4c20). GETPRIZE sends **0x5b4** `{slot}`; **0x5b5** (0x4fc5c0 -> 0x5e4f00) has up to 6 prizes; slot i is named
  at t > i+1 ("Obtained %d %s" 10096, or the Bust text) and fades in during [i, i+1]; CANCEL at t > 6 s. The background is the 2009 Japanese
  "Love fukubukuro". No luckybag item exists in the current data.
- Other boxes (`giftbox`, `giftbox_lv`, `giftbox_car`): level >= the value column (10297), then 0x1a1; the contents arrived as plain item
  packets.

**Remake** (`LuckyBag.cs`): every box with a reward pool opens in LUCKYBAG (Santa Gift Box `rw_xmas_giftbox`, Mysterious Gift Box
`rw_patrol_giftbox`, parts packages, car gift boxes, egg baskets...). `LuckyBag.Open(itemId, done)` / `LuckyBag.IsBag(itemId)` are the
inventory's entry point for every event item (scratch cards, slot tickets, coupons, tokens, recipes, pieces). Server: `event.box`.
Car keys in prizes (`pc_0063s`, Etc_Value = grade) become the car (grade V(Etc)); a key for a car already owned stays an item.

## 5. Reward pool rule (`Core/RewardGroups.cs`, `src/events.js` Tables.draw)
Rows with Probability <= 0 never drop. A pool whose rows are all at 100 gives every row (car box = car + trial part; scratch card = one
prize per column; parts package = all parts), except a pool of sub-pools with rising Etc_Value, which is a **level band** (`rw_rq_koi`: the
first band whose Etc_Value >= the level). Any other pool gives one row drawn by Probability as a weight. Periods (days) are not kept: event
items are permanent here.

## 6. Coupon book [exact]
- **Data** `Init/Coupon` (0x62f6e0; two copies: `data\init\coupon.xlt` 43 rows, newer, used here; `init\coupon.xlt` 36): row
  `"%d%d%d%d%s%u%s%d%d%d%s"` = index, Display, count (-1 = timed), minutes (-1 = counted), car name, car unique id, coupon item `i_n_c00NN`,
  car grade, DB Type (= category), DB Idx (= index). Sorted by grade (std::sort 0x62f890).
- **Categories** 0 Battle Zone, 1 Delivery (Time Attack when `char+0xed`), 2 Chase, 3 Combo, 4 Driving Time (minutes), 5 Drift, 6 Skill Pass,
  7 Jump, 8 Reckless (minutes), 9 Game Mode (Undercity), 10 quest coupons (never shown). Names 10245-10253 / 10853, units 10255 / 10257.
- **Counters** (singleton 0x6ce4b0, floats; time in seconds): battle result 0x50a040, delivery 0x4f4990, chase 0x4fa1fb, combo 0x4808dc,
  driving time 0x48110f, drift 0x48686f, jump 0x48698c, skill pass 0x4890c5, reckless 0x486415, game mode 0x71a265.
- **Packets**: C->S 0x3f2 counters (40 bytes), 0x3f7 `{dbType, dbIdx}` requirement met, 0x3f9 notify acknowledged, 0x3fb claim, 0x9d coupon
  code; S->C 0x3f4 counters, 0x3f6 records `{dbType, dbIdx, state}` (1 earned, 2 acknowledged, 3 claimed), 0x3f8 earned -> centre notice
  (10143-10151, 10790), blinking COUPON icon, coupon sound; 0x3fc claimed; 0x5dd code result (10754 / 10755).
- **COUPONBOOK 3690000** (`ui+0x1fc`, ctor 0x62faa0, creator 0x630ec0, handler 0x630860): pages V2..V9 (grade - 2), 5 coupons each:
  BTN (claimable), ALRAM, CARNAME, CONDOTION, CONDITIONNUM "%d", CURNUM "%d %s", STAMP; hover fills the detail panel (car model, NEEDLEVEL
  10484, gauges, price). Claim 0x630640 -> 0x3f2 + 0x3fb and 10847 "You've received a %s Coupon.". Opened by F5 (VK 0x74, 0x605f23),
  MAINBUTTON HOTKEY_COUPON / ADV_COUPON and NOTEBOOK 4060005.
- COUPONSTAMP 1910000 + COUPONPOPUP 1900000: the older book (10 rows x 5 stamps) and Coupon Manager Suyeon's prompt (10644 / 10645, Space)
  at the "Instructor Duffy Coupon" posts (`post_1st_00549`, `post_2nd_00481`, `post_3rd_00475`, `post_4rd_00513`; range 7, 0x6f73b0).
- COUPON 1850000 notify: icon blink (fmod(t,1) < 0.5), text 10270-10278 / 10792 "... Open the Coupon Book menu (F5) ...", OK -> 0x3f9.
- Coupon cars: AUTOSHOP check 0x59dfc0 returns 10489 "You need a Coupon to buy this vehicle." without the coupon item; label 10483.

- COUPONSTAMP 1910000 [exact] (`ui+0x1f8`, vtable 0x872b3c, creator 0x6336c0, refresh 0x633290, click 0x6334d0): one row per category
  with coupons (compacted): TITLE (10245-10253, 10853; 10665 with the Time Attack flag), RECORD "%8d %s" (10255 / 10257; the game-mode
  row "%7d %s & %4d %s", counter 11 unknown), five cells: TEXT need, CARIMGM, COUPON_POS + blinking HOVER while earned, STAMP when claimed;
  POS claims like the newer book (0x630640). **Remake** `Events/CouponStamp.cs`: the same rows over CouponBook's data; Suyeon's Space opens
  it with "couponbook=stamp" in events.cfg (env DC_COUPONBOOK=stamp), else COUPONBOOK (`CouponBook.OpenFromNpc`).

**Remake** (`CouponBook.cs`): the newer book, the notify and the NPC prompt; counters counted on the client (as the original trusted the
client): driving / reckless time, drifts, jumps, combos (DriftCombo milestones), chases and races (profile counters), Undercity runs; skill
passes and deliveries need hooks. Online they go up with `coupon.sync`; the server caps each category per second of play (`COUPON_RATE`:
1/30 battles, 1/30 deliveries, 1/8 chases, 10 combos, 1.05 s driving, 2 drifts, 2 skill passes, 1 jump, 1.05 s reckless, 1/90 game modes)
and checks the requirement on `coupon.claim`. Codes: env `DC_COUPON_CODES="CODE=item:count,..."` (offline: `code=CODE:item:count` in
events.cfg), once per driver.

## 7. Token book TOKENBOOK 3700000 [exact]
- Object `ui+0x3b0` (vtable 0x872b98, creator 0x633da0, handler vtable[10] 0x6339c0). `Init/TokenBook` (0x7d3be0): Exchange_ID, Count,
  Gift1..4. Mode 0 Mission Token (`i_n_00049`) rows 0-4 (Mission Girl), mode 1 rows 5-9 (Item Exchange Attendant: Mittron Crystal Powder /
  `repeat_token` in the newer copy). Button enabled when holding Count; 10916 confirm; op **0x48a** `{row}`.
- Daily mission: Mission Girl texts DayMission_Ing / _Complete / _Already; claim 0x3e2 -> 0x489; reward 10915 "%d Token" (1).

**Remake** (`TokenBook.cs`): the book, `TokenBook.MissionGirl()`; the daily mission is "catch 5 HUVs or finish 3 races today (UTC)", checked
by the server from its own counters (`token.daily`), one Mission Token a day. Server: `token.exchange`, `token.daily`.

## 8. Seasonal and world events

### Christmas tree XMASTREEEVENT 4440000 [exact]
`ui+0x428` (vtable 0x874fa4, tick 0x694ab0, handler 0x694de0). ICON1-10 only while holding a Santa Gift Box (`i_n_00088`); set bits show
SOLD_ICONn; the star (ICON10) needs the 9 others; notice 11437/11438/11439 every 10 s; click -> op **0xcee** `{decoration, box}`; 0xcef
`{?, citiesDone, mask}`. Trees at `post_N_xmas_01` (Cras (4445.8, 5398.9, 547.4), Moon (-868.1, -177.3, 85.6), Koinonia (5227.8, 6436.0, 7.1),
Oros (6032.8, 7887.5, 29.0)), within 7 m below 10 km/h, models `game\x_tree001\x_tree001_01..06.nif` by decoration count (2/4/6/8/10);
HUD badge EXP_MITO_XMAS1-4 = 30/40/50/60 % for 1-4 finished cities; snow (`effects\x_snow.nif`) instead of rain once a tree is done.

**Remake** (`XmasTree.cs`, `EventObjects.cs`): trees stand in the city during Christmas and grow with the decorations; one Santa Gift Box per
decoration, a Battle EXP 25 % ticket back (11438); online the masks are shared by the whole server (`xmas.donate`, event_state table).

### Fall harvest [exact]
Ingredient boxes `event_giftbox_<city>`; Box Recipe (AUTUMN 4670000): vegetable 12, fish 6, fruit 4, meat 3 + Normal / Ice Box ->
Regular / Medium / Large distribution box (11909-11911); restaurant (`post_N_harvest_01`, `<ghrestaurantimg>`, 0xe12 -> 0xe13
`{result, grade, receipts}`: GHRestaurantAccept1/2/3, 11899 / 11902); distribution centre (`post_N_harvest_02`, THANKSGIVING 4680000,
0xe14 / 0xe15 `{total}`, steps by GHEvent_MAXRECEIPT_LV1..4, exchange via 0x1a1, op 0x89 sub 36 `{item, rank}`: 11908 / 11907 / 11898).

AUTUMN [exact] (`ui+0x17c`, vtable 0x874d24, handler 0x692e30, tick 0x692c50): FOOD1-4 / BOX1-2 "%02d/%02d" (11580 brown, 11581 red),
MAKE (0x692810) sends 0x1a1 and turns off; sub 26 b -> after 1.4 s RESULT_BAD/GOOD/BEST, effect, sound 0x4f27, 4 s centre notice
11909-11911; the S/M/L stars step every frame. THANKSGIVING [exact] (`ui+0x180`, vtable 0x874a84, handler 0x692130, tick 0x692060):
GHDistribution -> 0xe14 -> 0xe15 `{total}`: GAGE width x the step ratio, PERCENT "%d%%", STAR_01-05 by step, 1TH-4TH 11903-11906 and
NAME_01-04 `reward_step_0{step}_{1st..4th}`; EXCHAGE (11912 without a receipt) -> 0x1a1; sub 36 `{item, rank}` spins three reels (rank 1
beef x3, 2 cherry x3, 3 fish x3, 4 vegetable x3, else one of 10 mixed rows; a step every 0.03 s, stops at 37 / 45 / 53), then 11907 +
11908 or 11898 and 0xe14 again. The restaurant has no screen (QUESTDIALOG mode 0x1d, 0xe12 -> 0xe13).

**Remake** (`Harvest.cs`, `HarvestScreens.cs`): restaurants and distribution centres as beacons in the city during the harvest; the Box
Recipe opens AUTUMN, the clerk's GHDistribution line opens THANKSGIVING (both as above over the server's `harvest.craft` /
`harvest.exchange`; the reels use the ingredient icons and the dialog a dark panel: `ui_thanksgiving.dds` / `ui_contents762.dds` aren't in
this install; the centre notice is the EMERGENCY line). Pools `box_recipe_*_result`,
`restaurant_evaluation` (receipts = its count x the box's value 1/2/4), `receipt_reward_step_0N`; the receipt total is server-wide online
(steps = GHEvent_MAXRECEIPT_LVn / 1,000; offline the driver's own total / 10,000). Boxes: Normal 500 / Ice 1,000 Mito at the restaurant (the
recipe free with the first box). Server: `harvest.buybox`, `harvest.craft`, `harvest.deliver`, `harvest.exchange`.

### Easter egg baskets ("Valentine") [exact]
VALEN 4660000: recipes g / h / s need a paint and 10 / 3+7 / 3+3+4 eggs; WRAP 0x1a1; result op 0x89 sub 26; EAT -> sub 27 EXP -> EVENT
4690000 "+N EXP"; SEND -> EVENT 4700000 gift (op 0x5ce note, 0xe10). **Remake**: `Harvest.CraftBasket` (pool `rw_{g|h|s}_recipe_{careful|
twinkling}`), baskets open as boxes (`rw_exp_choco_giftbox*`).
VALEN [exact] (`ui+0x178`, vtable 0x874ea8, handler 0x6942a0, tick 0x693f30): a recipe (type 60) opens the craft mode (egg / paint counts,
Sparkle Paint preferred), WRAP 0x1a1, sub 26 b = 1..24 (grade (b-1)%4+1, paint ((b-1)/4)&1, recipe (b-1)/8; the pool's Etc_Value) ->
after 1.4 s `<recipe>_CHOCOLET1/4/6/10` + 11578; a basket (type 61) opens the basket mode: EAT -> sub 27 exp -> EVENT 4690000 "+N EXP"
after 1 s (digit images, hundreds / thousands hidden when zero); SEND -> EVENT 4700000 (`ui+0x188`): EDIT_RECIEVER + SEARCHBUTTON
(0x51a -> 0x51b: 10107 / 10106), EDIT_CONTENT, ITEMINFO 11576 (11606 after the event), INFO 11577, the basket on SLOTX; GIFTBUTTON checks
10285, 10286, 11010, 10287, 10450, then the note 0x5ce with the basket attached (0x5cf: 11033 / 11038 / 11035 / 11034 / 10500) and 0xe10
`{basketType, rabbitDolls}` for the bonus; sub 30 = the basket is gone.
**Remake** (`HarvestScreens.cs`): VALEN in both modes, "+N EXP", the gift screen; the result code is kept per basket item
(`EventState.BasketCodes`). Gift: `valen.check` / `valen.gift` (src/events.js): the basket travels as "Gifted ... Basket"
(`g/h/s_get_giftbox`) in a note from the sender (50 Mito, the note fee); during the event both drivers get an Easter Rabbit Doll
(`lovelove_heart`; the receiver's in a system note). Offline a gift has no one to go to (the screen says so).

### Other events [exact]
- OBEVENT 3330000 (`ui+0x36c`): 39 hard-coded 2007-2008 Korean campaign prizes ("open beta"), KR text only; SEASONEVENT 3340000 (`ui+0x370`):
  the same popup used by the summer festival (op 0x89 sub 35, "You have used %s and obtained %s %d!"). **Remake**: `EventPopup.Show(icon, title,
  text)` on SEASONEVENT for every event reward. OBEVENT [exact] (vtable 0x8740d4, creator 0x673540, OK 0x673330): op 0x89 sub 1 / 4 / 5
  queues prize a (sub 2 -> 7, sub 3 -> 8), OK shows the next; the table at 0x8c4514 + n * 0xe4 (`{id, char[32] picture, char[64] title,
  char[128] body}`, CP949) was read out with `scratchpad\cbw\obdump.py`. **Remake** `Events/ObEvent.cs`: the 39 entries (Korean titles,
  English bodies), `ObEvent.Queue(n)` as the 0x89 entry, "obevent=n,m" in events.cfg (env DC_OBEVENT) for testing, the Indy4 props
  event shows 32-35.
- Summer festival: letters `i_n_2010_sumfest_Piece1-8` (`rw_2010_sumfest_droptable_cs` / `_bz`) combine (`rw_2010_sumfest_comp`, Etc_Str) into
  the festival box. **Remake**: `Harvest.Combine` for every `reward_piece` / `piece` item (also the snow piles).
- "Collect 3 props" NPC event [exact] (tick 0x6fcc00, switch `Etc.Indy4Event` net+0x284 bit 31; the Greenday / Sony keys are empty, use
  `Event_Indy4_*`): the NPC at `post_1st_00658` / `post_2nd_00595` / `post_3rd_00607` / `post_4rd_00618`, three props picked client-side by
  rand() % 80 from the city's pool (Moon `post_1st_00666-00745`, Koinonia odd `post_2nd_00603-00761`, Cras odd `post_3rd_00615-00773`, Oros
  odd `post_4rd_00185-00343`): fedora `i_n_00051` (Event_Indy4_01), whip `i_n_00052` (_03), crystal skull `i_n_00053` (_02); at the NPC
  _already / all found -> 0x494 -> 0x495 _clear (new props) / _ing. **Remake** (`Events/PropEvent.cs`, EventObjects kinds PropNpc / Prop):
  season `indy4` (only by list: DC_EVENTS / events.cfg); a found prop gives its item (`props.find`), the NPC takes the three for the raffle
  ticket `i_n_00054` + 5,000 Mito once a UTC day (`props.claim`); English names for the KR-only items (`EventItems.KrOnly`).
- Mita500 Halloween lines [exact] (0x4f83c0): `Mita500_Halloween_1..4` with EventHalloween (MitaDrink.LineKey already picked them; `_5`
  is unused); the expiry line is 11701 during Halloween, else 10894 (now in `MitaDrink.CheckExpiry`).
- Halloween Drink [exact]: `i_n_halloween` is a `buff_item` (type 39); use (0x5d620c) refuses with 11059 while the Special Drink runs at
  250 % or more, else 0x1a1; 0x481 sets the effect (11058, HUD icon by percent 150/200/250/500, ~30 min). **Remake**: `SpecialDrink`
  (Events/PropEvent.cs; one dispatch line in Inventory/ItemUse.cs): the drink starts MitaDrink's Special Drink effect at 200 % (Bocchus
  `i_n_buff` 150 %) for 30 minutes, never lowering a stronger one; server `event.drink` (sets economy.js `session.mita`).
- Login greetings (op 0xaa), EVENTCHASING 880000: documented, only the anniversary greeting is rebuilt (daily attendance, below).

## 9. Seasons and play drops (remake rules)

- `SeasonEvents.cs` / server `parseSeasons`: Christmas Dec 1 - Jan 6, Valentine/Easter Feb 1 - Apr 30, Summer Jul 1 - Aug 31, Harvest
  Sep 15 - Nov 15, Halloween Oct 15 - Nov 7, Anniversary Jun 1 - Jun 14 (UTC). Online the server's list (env `DC_EVENTS`: `auto`, a list
  `christmas,harvest`, `all`, `none`, or `date=2025-12-24`; `event.state` sends it with the gauge, the trees and the receipt total). Offline:
  `-events christmas` on the command line, env `DC_EVENTS`, or `events=...` / `date=...` in `events.cfg` next to the save or the exe.
- Drops (`EventDrops` / server `tick()`, max 10 a day): per HUV caught and per race finished, one drop per running season: Christmas a Santa
  Gift Box; harvest the current city's ingredient box (chases); Valentine eggs (`rw_valentain_bronze` chases / `_silver` races, the three
  recipes with the first drop, a Colorful Paint every 3rd); summer letters. Once a day: anniversary greeting + Small Gift Box, Halloween Drink.
- Server state: `event_state` table (`lotto`, `xmas`, `harvest`); per driver `profile.Events` (Unity `EventState`).
- The city side (`EventObjects`, self-starting, `EventObjects.Enabled` switches it off): posts open within 10 m below 10 km/h (the coupon
  prompt too, since Space also drifts); daily greetings and gifts go to the chat, nothing pops up while driving.

## 11. Tests

- Unity PlayMode `Tests/ChannelEventTests.cs`: `ChannelEventTests.EventScreens` (offline, no city: GACHAINVEN pages + badge sale + Key
  Parts compound, AUTUMN make + result, THANKSGIVING exchange + spin, VALEN wrap / basket / gift screen / EAT + "+N EXP", COUPONSTAMP
  earned cell + claim + stamp, OBEVENT queue, the props NPC, the Halloween Drink through ItemUse), `PickEmOverASpectatedField` (prompt,
  ticket, symbols, picks, the grade at the finish) and `ChannelEventOnlineTests` (the server's Halloween Drink and props); screenshots
  `Preview/cbev_*.png`. Server `test/events2.test.js`: GACHAINVEN parts / compound / sale / capsule extras, the Pick'em grade and layout,
  props and the drink, a wire Pick'em in a real 4-racer room and a wire egg-basket gift.

- Unity PlayMode `Tests/EventsTests.cs`: `EventsTests` (offline: pool rule, capsule shop + prize popup, slot machine + result, LUCKYBAG,
  LOTTO, buying a card, coupon notify / claim / stamp, token book, the Cras tree post with drops, dialog and donation, harvest posts;
  screenshots `Preview/events_*.png`) and `EventsOnlineTests` (the real server with `DC_EVENTS=christmas` and `DC_COUPON_CODES`: codes,
  server-charged capsule, box, slot, scratch card, a tree decoration seen in event.state).
- Server `test/events.test.js`: the draw rule, calendar, real tables, costs and consumption, cooldowns and daily limits, bounded coupon
  counters and claims, codes, token book and daily mission, Christmas drops / shared trees / star rule, harvest chain, a two-driver wire test.

## 10. Hooks for code owned elsewhere

| where | line(s) |
|---|---|
| `Game/Hud.cs` ~l.72 (ADV bar, today "not available") | `ui.On("ID_MAINBUTTON_ADV_COUPON", _ => CouponBook.Open()); ui.On("ID_MAINBUTTON_ADV_GOTCHA", _ => Gacha.Open());` and keep only `ID_MAINBUTTON_ADV_COMM` in the "not available" loop; F5 (VK 0x74) -> `CouponBook.Open()` |
| `Game/Hud.cs` MAINBUTTON (classic bar) | `ID_MAINBUTTON_HOTKEY_COUPON` -> `CouponBook.Open()`; badge `ID_MAINBUTTON_EXP_MITO_XMAS1..4` visible for `SeasonEvents.XmasCitiesDone` 1..4 |
| NOTEBOOK 4060000 owner | done: `Notebook` (HudPanels.cs, AUDIO.md 8) sends COUPONBOOK to `CouponBook.Open()` |
| dealer (Menus / OriginalMenus buy) | before buying: `if (!CouponBook.CanBuy(P, car)) { UiPopup.Message(MissionText.Get("10489")); return; }`; after: `CouponBook.SpendCoupon(P, car)`; label 10483 when `CouponBook.CouponFor(car) != null` (server: same check in economy.buyCar) |
| inventory coupon tab (3200000) | when the page opens: `CouponBook.BindCodeEntry(view)` |
| delivery completion (DeliveryService) | `CouponBook.Count(CouponBook.Cat.Delivery);` |
| DriftCombo near-miss (Graze) trick | `CouponBook.Count(CouponBook.Cat.SkillPass);` |
| race / PvP EXP (Progress.Extras race, race.js) | add `Lotto.BattleExpBonusPct` / `events.battleExpBonusPct()`; Christmas `SeasonEvents.XmasBonusPct` / `events.xmasBonusPct()` to chase and race EXP/Mito |
| weather (WeatherClock) | rain -> `effects\x_snow.nif` when `SeasonEvents.Is(EventSeason.Christmas) && SeasonEvents.XmasCitiesDone > 0` |
| `Game/PvpSpectate.cs` (Pick'em) | expose the field: `public static List<Driver> Order => viewer ? viewer.Order : new List<Driver>();` with `public List<Driver> Order => order;` in Viewer; then `PickEm.Order()` uses it instead of reflection |
| Hud / an NPC (GACHAINVEN) | nothing: the capsule shop's GACHAINVENNEW opens it |
| visual shop / an NPC counter | `Lotto.BuyCard(cardId)` for `Lotto.Cards`; slot tickets: grant `gacha_observe_vN` from a Battle Zone reward |
| Mission Girl (a station / OMD NPC) | `TokenBook.MissionGirl()`; Item Exchange Attendant `TokenBook.Open(1)` |

## 12. Round 4: Pick'em reels, missing art, 3rd_anniversary_fin

- **Pick'em reels** (VIEWING 1980000, SYMBOL_BG): each of the three reels is a clipped window over a strip of the four symbols
  (bomb, gold, silver, bronze); they run at 300 px/s while the pick may change (before half race) and slow onto the symbol
  under the picked racer's current row once the picks lock, re-landing when that row changes (`PickEm.ReelShows /
  ReelSpinning`) [remake for the stop rule; 300 px/s is the client's]. `PickEm.Order()` now reads `PvpSpectate.Order` (the
  reflection is gone; the hook in 10 is done).
- **Art missing from this install** (`Events/EventArtFallback.cs`): when a control's normal image doesn't load (ui_thanksgiving
  / ui_contents762 of THANKSGIVING 4680000 and AUTUMN, ...), big statics get a dark panel in the event's colour (harvest
  brown, Easter pink, Christmas green, else slate) and buttons a dark bar with the id's last word; small decorations stay
  empty. Applied when AUTUMN / THANKSGIVING / VALEN / EVENT (HarvestScreens), XMASTREEEVENT, LUCKYBAG, LOTTO, TOKENBOOK and
  COUPONSTAMP open; dialogs whose art exists are untouched.
- **3rd_anniversary_fin** (UI effect 13; no caller left in the exe): the remake plays it with the day's anniversary attendance
  gift (the `3rdanni_attendevent_01_omd` greeting; EventObjects' tick) instead of the item pickup.
