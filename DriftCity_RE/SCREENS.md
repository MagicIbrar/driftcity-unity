# Driver info, chat leftovers, chargers and small screens - DriftCity.exe

Round notes for the "still open" dialogs of `codemap.json`: driver info (USERINFO 1810000 / USERINFO2ND 1830000), the
chat modes and whispers, MSGBOX 3230000, the Mittron fuel chargers (CHARGER 940000 / CHARGER4GATE 950000 / CHARGERPVP
960000) and the small screens (AEROSET 3900000, VIDEOADV 2990000, OPTION 2980000, AUTOFINDER 3420000).
[exact] = read from the code; [inferred] = from names / data; "remake rule" = the original server decided it.

## 1. Driver info

### USERINFO 1810000 (MYINFO) [exact]
- The main bar's MYINFO (3010006 / 3020014) runs 0x5edaf0: it closes the other windows and toggles USERINFO (0x54c560).
  MY 3980000 is the crew page (`Social/Teams.cs`, done earlier), not a driver page.
- Binder 0x54bfe0: +8 IMG_POS, +0xc NAME, +0x10 RACINGTEAM, +0x14 LEVEL, +0x18 EXP, +0x1c MITO, +0x20 BATTLERECORD_1ST,
  +0x24 _2ND, +0x28 QUICKSERVICE_REC, +0x2c TOTALKM, +0x30 LADDERRECORD, +0x34 LADDERRECORD_COVER, +0x38 LICENSE,
  +0x3c CARNAME_POS, +0x40 CARGRADE_POS, +0x44..+0x90 the 20 CARINFO cells, +0x94 TEAMMARK, +0xc0 TEAMROO, +0xc4 TIMEATTACK.
- Fill 0x54b660 (character record): NAME = +8; LEVEL `"%3u"` (+0x38); EXP / MITO with separators (0x7b6300, +0x3a / +0x46);
  BATTLERECORD_1ST 10159 `"%5u [ %5u Wins] (Ind.)"` (+0x7d, +0x81); _2ND 10160 `"... (Team)"` (+0x89, +0x8d);
  QUICKSERVICE_REC 10161 `"%5dx"` (+0x9d: Quick Service = the Delivery Service); TOTALKM 10729 `"%u Km"` (+0xa1, x a
  constant for nation 1); in a crew (0x512ec0) LADDERRECORD = the crew's ladder entry (+0x50 of its 0x308 record) and the
  cover hidden, else `"-"` with the cover shown; the CAR / PARTS / USER / TOTAL grid and the POINT row.
- 0x54b450: RACINGTEAM = crew name in the crew class colour (0x514530 -> table 0x871bc8: C white, B 0x0099ff, A yellow,
  S 0xff2e00); 0x54b250 draws the crew mark at TEAMMARK (0x442130) when the crew id is not -1.
- Show 0x54b4a0: TEAMROO and TIMEATTACK only for a Team ROO driver (+0xed == 1); LICENSE with flag 0x24c & 4. The
  portrait is the USERCHAR 1890000 cell (ch_u_001.dds, eight 128 px cells) drawn at IMG_POS.

### USERINFO2ND 1830000 (someone else) [exact]
- Packet 0x295 (0x54c770) fills the same card for another driver; MITO shows `"***,***,***"`; no stat grid; the car is
  drawn in 3D with CAMERA_LEFT / RIGHT / RESTORE.
- Handler 0x54d0b0: CLOSE; ADDFRIEND -> confirm 10162 "Add to Friend List?"; JOIN_RANCINGTEAM (enabled when the driver
  has a crew) -> 0x367 and 10163 "You have requested to join %s Crew.", else 10164; BUY_GOODS (the personal shop);
  the crew mark at TEAMMARK and the licence (0x54cfa0) are drawn by their controls' draw events.
- The player menu (COMMAND 700000) USERINFO -> 0x79f790 asks the server for this card.

### Remake (`Game/DriverInfoScreen.cs`)
- `OriginalMenus.DriverInfo()` -> `DriverInfoScreen.Own()`: every field above; crew from `Teams.Mine` (loaded on demand),
  ladder entry `"<class> Class  <rank>"`; Ind. record = Battle Zone races / wins (`PvpRaces` / `PvpWins`), team record
  0 / 0 (the remake keeps none), deliveries = `MissionRecords.DeliveriesCompleted`, km = `DistanceKm`.
- `DriverInfoScreen.Other(name)`: online `driver.info` (server `src/driverinfo.js`: live session or saved profile; Mito
  never sent), offline an AI driver's card (SocialStore's seeded level / grade / portrait, a starter car). BUY_GOODS
  disabled (no personal shop); the driver's car turns in the dark panel (the inventory's CarPreview, cropped to the
  panel; CAMERA_LEFT / RIGHT turn it, RESTORE resets it). The portrait is USERCHAR's INDEX(N-1) image laid over IMG_POS.

## 2. Chat

- Modes 0x5bcdd0 [exact]: 0/1 room or area, 2 channel, 3 party, 4 team (crew), 5 whisper, 6 private; scope bits
  0x5bcbe0 (channel 2, team 4, party 8, server 0x10, whisper 0x20, private 0x40, area/room 1, system/client 0x80).
- CHAT ADV 260000: FIX_SELECT .. FIX_PRIVATE are the mode label in front of the input (all at the same place), the six
  DROPDOWN_* rows the list above it; 0x5bc400 keeps the list open while the focus is on a label / row and closes it
  otherwise; 0x5bc7a0 handles CLOSE (260015), INPUTOPEN (260029) and MENUOPEN (260030, the tab menu).
- Prefixes 0x5b7a67 [exact]: `!` channel, `&` party, `*` whisper, `-` private channel, `@` crew; no `/w`. Whisper
  `*Name text` (0x5bcf60, name `%15s`) -> 0x094; after sending the input is refilled with `"*Name "`; a received whisper
  (0x5bd360) shows 10475 "Whispering from [%s]: %s" and writes `"*sender "` into the input; the echo 10474.
- There is no separate whisper window in game.nui: whispers are the chat's mode 5 (and the PRIVATE filter tab).
- Remake (`Net/ChatBox.cs`, public API kept; new `Mode`, `SetMode`, `Outgoing`, `Submit`, `WhisperTarget`, `Prefill`):
  the label opens the list, a row picks the mode, Esc closes the list; an unprefixed line goes out with the mode's
  prefix; `!` is stripped to channel; whispers show 10474 / 10475 and the next input opens with `"*Name "`. Remake
  rules: Normal = channel (the server has no area range); Private and `-` answer 10745 "This feature is currently
  unavailable." (no private channels on the server).

## 3. MSGBOX 3230000

- A text prompt, not a message box [exact]: INFO, TEXT on TEXTPANEL (Enter = OK), OK / CANCEL, or DELETE /
  FRIEND_ADD / FRIEND_REMOVE in OK's place (all at the same spot). Users: friend add / block 0x60e520, recommend
  0x554240, character / licence delete 0x767240 / 0x769590, parts shop 0x61b410, events 0x5d38e0 (23 references).
- The client's message / confirm box is POPUP 3250000, which `UI/UiPopup.cs` already draws: UiPopup stays as it is.
- Remake `UI/MsgBox.cs`: `MsgBox.Prompt(info, ok, prefill, Confirm.Ok|Delete|FriendAdd|FriendRemove, maxLength, cancel)`.
  `Social/Friends.cs` has its own copy (`Friends.Prompt`), which can call MsgBox (hook below).

## 4. Mittron fuel and the chargers

### The fuel tank [exact]
- Each car has a fuel tank: litres (double, car +0x800), capacity (+0x2c0) and km per litre (+0x2c4); 0x479510 sets
  100 L and 5 km/L, the server's car data replaces them (+0x18 fuel, +0x28 capacity, +0x2c efficiency, part bonuses
  0x480050). This is not the booster gauge (+0x9fc, the dial's arc), which the remake also calls "Mittron".
- Burn 0x475ad0 / 0x484196: `fuel -= distance / (efficiency * 1000)`, clamped at the minimum.
- Empty: top speed capped at 150 km/h (0x476f90), AddCombo refuses (0x4804d0), Auto-Drive and items refuse (11116
  "You cannot use this feature when your fuel is running low." in 0x5d52d0).
- Items: Mittron Fuel i_n_00004 / 00005 / 00006 = 2 L / 10 L / 300 L ("Fills fuel tank by ..."; sold in the parts
  shop list PARTSLIST_0103-0105). Auto refill (0x512990): with i_automitron in the bag and the tank at 20 % or less,
  every 2 s a refill request (0x799980 with flag 1); 10947 "Only %d Mito amount of Mittron refueled due to insufficient Mito.".
- The speedometers carry MITRONWARNING (320000) / MITRON_GAGE / _EMPTY / _FULL / MITRONWARNING (ADV 330000).

### CHARGER 940000 / CHARGER4GATE 950000 [exact]
- A station's MITRON button (400007, handler 0x561e9b) hides the station panel and opens CHARGER (UI slot +0x28c), or
  CHARGER4GATE (+0x290) on the "Gate" servers (net+0x244 & 0x200, the flag that also renames channels "Gate %2d").
  The two dialogs share the code (show 0x550230 / 0x550f20, gauge 0x54f9f0 / 0x550870).
- Show: litres to add = 0, asks the server for the station's prices (0x799980 with 0 litres; reply 0x54fec0 sets
  list / sale price and capacity), employee portrait ch_1st_0004_01 with 10166 "Mittron Filling Station Employee".
- Handler 0x54faf0: 20L / 50L / 100L / 200L add, FULL = capacity, - / + one litre (held: repeats), the amount clamped
  to [0, capacity - fuel]; GAUGE = fuel + amount, GAUGE_DISABLE (24 % white) = fuel, GAUGETEXT `"%dL (+%dL) / %dL"`,
  LITER, SELL_PRICE `"%I64d"` or `"%I64d(%I64d)"` with a sale; MITRONPRICE `"1L / %u Mito"`; OWNTEAM 10168 "Owned by:
  %s Crew" / 10169 "Owned by: None" (crew-owned stations, packet 0x2d4); CHRAGE enabled only with an amount (0x54f900),
  buys (0x54f950: 10502 with 0 L, else 0x799980 {car, price, litres}); sounds SID_CHARGINGSTATION_GAGE / PREGAGE / FINISH.
- CHARGER4GATE adds SALEPERCENT, SALE_PRICE and SALEINFO (a sale banner). In the remake its base panel
  (ui_contents36 4,428~474,1020) does not draw (Preview/screens_charger4gate.png): the skin is kept behind
  `MittronCharger.GateLayout` only; CHARGER is the default, as on the client's non-Gate servers.

### CHARGERPVP 960000 (Battle Zone) [exact]
- Kept by the guide object (0x5c3030). The PvP stages (0x71e0b3, 0x71ef4f, 0x726697, 0x72d92d) open it when the tank
  is at 20 % or less (car +0xa00 <= 0.2), it was not closed before (+0xb8) and the car is not kind 2.
- 0x5c2c30 counts the fuel stacks (table 0x8c3fe8: {index, litres, code} = {0, 2, i_n_00004}, {1, 10, i_n_00005},
  {2, 300, i_n_00006}) into COUNT_S 10463 "Small X %u", COUNT_L 10464 "Large X %u", COUNT_XL 10465 "Super large X %u",
  COUNT_XXL 10466 "Super X %u" (0); with none it does not open and shows FUELWARNING.
- USE (0x5c2f00) -> 0x5c2a10(capacity - fuel): smallest kind first; of a kind `need / litres + 1` when that many are
  there (then stop), else all of it and the rest of the need; a bigger kind only while its litres fit what is still
  missing (or nothing was spent yet). The items are used one stack at a time (0x78ce10). ESC sets +0xb8 (no more offers).

### Remake (`Game/Fuel/`)
- `MittronFuel`: per-car litres (keyed by vehicle) in `<save>.fuel.json` / `online_<driver>.fuel.json`; capacity 100 L,
  5 km/L for every car (the server's per-car values are not known); a new car comes full; `Consume(metres)`,
  `Add`, `Set`, `Empty`, `Low` (20 %), `TopSpeedCapKmh(car)` (150 with an empty tank), `ItemLitres(id)`.
  `MittronFuelMeter` burns fuel while the player drives in a city (not the parking lot; teleports ignored), flashes
  FUELWARNING at 20 % and FUELMEMPTY at empty, saves every 15 s.
- `MittronCharger.Open()`: CHARGER 940000 (CHARGER4GATE with `GateLayout`), every control above; remake rules: 10
  Mito a litre (the 4GATE's default text "1L/10 Mito"), no crew-owned stations (10169), no sale, no employee portrait;
  short of Mito it fills what the Mito pays for and shows 10947. Online `fuel.buy` (server `src/fuel.js`) charges.
- `PvpCharger.OfferIfLow()` / `Open()` / `Use()` / `Plan()`: CHARGERPVP as above; online `fuel.use` spends the items.
- Server authority: the server charges the Mito and spends the items; the litres themselves are a client-side physics
  state like the booster gauge (not sent to the server).

## 5. Small screens

- AEROSET 3900000 [exact, partial]: an item's bonus-point screen ("Aeroset Setting", packet 0x4be 11064 "Aeroset Setting
  completed." finds the item by its uid). Free points (FREE_POINT, +0x7c) over four stats OPTION1-4 (+0x80..+0x8c):
  UP moves a free point in (with none left it pulls all of that stat's points back), DOWN the reverse; ETC_OPTION1-4 and
  FIELD_OPTION1-4 are one-of-four choices shown when the item has them (+0x98 / +0x99, chosen +0x90 / +0x94) with COVER
  plates over the rest; OK: points left -> 11014 "You still have unused points.", a needed choice missing -> 11015,
  else 11041 "Do you want to proceed with this option?" -> the server. Which item opens it and the point budget came
  from the server (AeroSet column = 1 on 106 cars of vehiclelist): not built (no data for the remake).
- VIDEOADV 2990000 / OPTION 2980000: the classic option pages (setup 0x540240 / 0x538fa0, handler 0x538a20). The
  remaster opens OPTIONBASE 4250000 + OPTIONDETAIL 4300000, which the remake has (`Options/`). The classic pages belong
  to the basic main bar (MainUIType 0); the remake always shows the ADV bar (3010000, Hud.cs), so they are not reachable
  and were not built.
- AUTOFINDER 3420000 [exact]: the server's anti-macro quiz (op 0x51e). Init 0x681050 / show 0x681150: a random
  question from `init\autofinder.xlt` (59 rows: question, four answers, the right one; e.g. "What is the fuel in Drift
  City Called?"), a random base pattern (BASE_PATTERN1ST-4TH), the answers shuffled, the box at a random spot; a 30 s
  clock `"%d %s"` 10555 "Sec." (0x680f50; at 0 it closes); an answer goes to the server (0x799cf0) with 10556
  "Correct." / 10557 "Wrong.". Not built: it only makes sense with server-side macro detection and a penalty, which the
  remake's server does not have (and offline there is nobody to cheat); a dialog nothing opens would be dead code.

## 6. Hooks (files outside this round's ownership; exact lines in the round report)

- StationPanel.cs MITRON (lines 104-112): hide the panel and open `MittronCharger.Open(() => show it again)`.
- PlayerMenu.cs `Info(t)` (USERINFO and OWNLICENSE, lines 27 / 33): `DriverInfoScreen.Other(t)`.
- DriftCar.cs lines 561 / 660 / 710: cap the limits with `MittronFuel.TopSpeedCapKmh(this)` (0x476f90).
- DriftCombo.cs `Add` (line 213): no combo with `MittronFuel.Empty` (0x4804d0).
- AutoDrive.cs `Refusal()` (line 43): `if (MittronFuel.Empty) return "11116";`.
- GuideText.cs lines 256-264: drop the booster-based FUELWARNING / FUELMEMPTY (MittronFuelMeter flashes them now).
- Hud.cs line 50: show MITRONWARNING / MITRONWARNING_L while `MittronFuel.Low` instead of always hiding them.
- ItemUse.cs `Instant` (line 42) + `ApplyEffect`: i_n_00004/5/6 -> "fuel" (`MittronFuel.Add(MittronFuel.ItemLitres(id))`);
  server src/items.js `INSTANT` (line 16): the same three ids -> 'fuel'.
- PvpWaitRoom.cs `Show` (line 149): `PvpCharger.OfferIfLow();` (the PvP stages' 20 % check).
- Friends.cs `Prompt` (line 180): may become `MsgBox.Prompt(info, ok, prefill)`.

## 7. Left

- The per-car base capacity / efficiency (the server's car data; the part bonuses are done: PHYSICS.md 13.1), crew-owned stations and their prices, the sale skin's
  banner, the employee portrait, the auto refill item (i_automitron), selling the fuel items in the parts shop.
- USERINFO2ND's 3D car view and the personal shop (BUY_GOODS); team battle records.
- AEROSET, AUTOFINDER, the classic OPTION / VIDEOADV pages (reasons above).

## 8. Round 4: AEROSET, AUTOFINDER, the classic option pages, the basic bar's friend windows

### 8.1 AEROSET 3900000 (`Game/AeroSet.cs`, server `src/smallscreens.js` aero.set)
The screen as in 5 [exact]. Globaltext 11064 reads "Custom Aeroset Setting completed." in other languages ("the paid aeroset"):
the screen belongs to the custom aero set items (vshop SELECT_AEROSET Pc_Aero_A..D, Pc_Aeroset / Pc_Aeromk2). **Remake rule**:
a car with vehiclelist AeroSet = 1 and an aero part fitted has 6 points over Speed / Accel / Crash / Boost (OPTION1-4), at most
3 on one stat, each point one rating level (`AeroSet.Bonus`, hook in ShopData.Stats below). UP / DOWN as the client (UP with
none left pulls that stat back); OK: 11014 with points left, 11041, then `aero.set {car, points}` (the server checks the car,
the part, the budget and keeps `Cars[i].AeroPoints`; offline the profile) and 11064 in the chat. The remake's aero parts have
no ETC / FIELD choices: those rows stay under COVER5-8. `AeroSet.Offer(car)` is the workshop's entry after an aero part is
fitted (hook below). New field `OwnedCar.AeroPoints` (PlayerProfile.cs).
The panel (unnamed control 3900001) points at `ui_contents534.dds`, which this install lacks; the same cell of
`ui_contents53.dds` holds it ("Item Option Setting": Points Left, Speed / Accel / Crash / Boost, "Additional Options"),
so `EventArtFallback` now tries the sibling sheets (534 -> 533 / 532 / 53) before a stand-in panel. The art names the
choices [exact, from the picture]: ETC_OPTION1-4 "For Battle Zone Only": boost +20 % when crashing into a traffic car, base
boost recharge +25 %, 10 % chance of +100 % boost on a traffic crash, 15 % chance of a full repair on a traffic crash;
FIELD_OPTION1-4 "Field or Undercity Only": critical hit chance +10 %, boost +5 % when hitting a HUV, base boost recharge
+100 %, Turbo Stop Assist (Shift + Back). Not offered here (no item carries them; their effects would need car hooks).

### 8.2 AUTOFINDER 3420000 (`Game/AutoFinder.cs`, server `src/smallscreens.js`)
The client shows what the server asks: `autofinder.ask {id, question, answers[4], seconds}` -> a random base pattern, the
answers shuffled over BTN1ST-4TH, the box at a random spot, "%d Sec." (10555); a pick sends `autofinder.answer {id, choice}`
(the server's answer order) -> 10556 / 10557. **Server, off by default** (`opts.autofinder` / env `DC_AUTOFINDER=1`): the
questions from `init\autofinder.xlt` (59); a driver in a city channel, not racing, gets one every 45 +- 15 min; 30 s to
answer (no answer = `autofinder.result` wrong); three misses in a row disconnect ("macro check"); admins can ask at once
(`autofinder.test`). Offline nothing opens.

### 8.3 OPTION 2980000 / VIDEOADV 2990000 (`Options/OriginalMenus.ClassicOptions.cs`)
With the basic main UI (VIDEO tab MAINUI = Basic, goption MainUIType 0) `OriginalMenus.Options()` opens the classic window:
VIDEO (resolution list under STATIC_RESOLUTION, quality slider with four stops on Unity's quality levels, the 5th box =
custom, VIDEOADV), AUDIO (sound on / mute, BGM / 3D / SFX / engine sliders, speaker list), INPUT = the controller page
3000000, GAME = the game page 4280000 over the window; VIDEOADV: texture sliders (low / HD), view distance, anti-aliasing
(4 stops), background building shadows, car shadow none / flat / real, background animation, PvP intro, my / others'
effects. Same goption keys as the remaster's pages; APPLY saves, CANCEL / CLOSE / Esc restore.

### 8.4 The basic bar's friend and note windows (`Social/BasicBarSocial.cs`, SOCIAL.md 9)

### 8.5 Hooks
- Workshop / custom shop after fitting an aero part: `AeroSet.Offer(PlayerProfile.Current.Car);`
- `ShopData.Stats(info, car)` (or GameFlow.ApplyCarStats line 136): `var a = AeroSet.Bonus(car); s.speed += a.speed; s.accel += a.accel; s.crash += a.crash; s.boost += a.boost;`
- Basic main bar (MAINBUTTON 3020000, Hud.cs when MainUIType == 0): FRIEND -> `Social.BasicBarSocial.OpenFriends()`, MESSAGE -> `Social.BasicBarSocial.OpenInbox()`, OPTION -> `OriginalMenus.Options()`.

Section 7's "AEROSET, AUTOFINDER, the classic OPTION / VIDEOADV pages" are now built.
