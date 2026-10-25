# Inventory, item screens, quick slots and trade

Code map subsystem "Inventory & items" (151 KB). Unity: `Assets/DriftCity/Runtime/Game/Inventory/` (InventoryWindow,
InventoryNdlg, ItemInfo, ItemUse, ItemPeriods, SetParts, QuickSlots, BattleQuickSlots, LicenceInfo, TradeWindow), server:
`src/items.js`, `src/trade.js` (+ hooks in `src/workshop.js`, `server.js`), tests `Tests/InventoryTests.cs`,
`test/trade.test.js`, `test/inventory2.test.js`, bot `tools/bot.js --trade`.
[exact] = read in the exe, [inferred] = deduced from layout / data, [remake] = this rebuild's own rule.

## 1. What the client has

### Two inventory classes [exact]
The UI manager (`0x532420..0x5324b0`) builds both:

| | classic | NDLG ("new dialog") |
|---|---|---|
| object | UI+0x164, ctor `0x5d3670`, vtable `0x8713a8` | UI+0x168, ctor `0x5dab50` (derives from classic), vtable `0x8715b8` |
| dialogs | INVENBASE 3060000, INVEN 3080000-3210000, MYINVEN 3190000, INVEN 3610000 | NDLGINVEN 4730000-4870000 |
| init / per frame | `0x5d38e0` (5.9 KB) / `0x5d92e0` (6.3 KB) | `0x5e1ba0` / `0x5df6a0` (6.6 KB), packets `0x5e1070` (9.3 KB) |

`0x4dd420` picks one: `(0x780510()->flags[0x294] & 0x600) ? NDLG : classic` - a service flag the server sends at login.
All dialogs are registered at layer 10000 by `0x530690`. The rebuild opens the NDLG set by default and keeps the classic
one selectable (section "Which set" below).

### Which set the client shows [exact, the era inferred]
- `net+0x294` is dword 20 of the server config that op 0x1e (0x503e50) copies to `net+0x244` (EVENTS.md section 1); the UI
  type (ADV / basic main bar) is a different field, `net+0x24c & 0x18`. Both main bars open the inventory through
  `0x4dd420`: the ADV bar's handler `0x5f0c40` (INVENTORY button 3010041: `0x4dd420()->0x5cca60` open? -> `0x5ee010` /
  vfunc +0x28(4)) and the basic bar's `0x5e8df0`; 33 call sites in all (quest dialog, chat, party, auction...) and the
  same test inlined at `0x4f1893 0x4f51f6 0x4fd5c1 0x503851 0x5bfad1 0x62d3c1 0x63461a 0x695900 0x7648e1`. So the set
  does not follow the main bar: it is a server switch. Inside the classic code `0x5ce730` / `0x5ce900` also test
  `(net+0x294 & 0x600) == 0x200` (a variant: the battle-slot icons drawn at 57 px, 0x5c9510 skipped).
- Which one the live service sent is not in the client. The NDLG set is the newest: its dialogs (4730000-4870000) come
  after the 2010/11 event dialogs (4660000-4680000), its US texts 11740-11780 ("[Part Specs after Modification]",
  "Vehicle Performance Information", "Assist Option", "Rented" ...) sit right before the ADV dealer's 11814-11824 (the
  dealer this client is configured for, SHOPS.md section 0), and its layout is the reworked one (car plan, two rows of
  four parts with set outlines, the performance table, the quick-slot panels). [inferred] It is the inventory of the
  ADV-era client, so the rebuild makes it the default.
- NDLG class: ctor `0x5dab50` calls the classic ctor and swaps the vtable; overrides init `0x5e1ba0`, update `0x5df6a0`,
  parts page `0x5db0a0`, packets `0x5e1070`, show `0x5dcaa0` (show(1) -> quick-slot panel 4760000 / 4770000), and keeps
  the classic licence page 3080000, OTHERLISENCEBASE 3070000, MSGBOX 3230000 and SETACTIVATE 3610000.

| NDLG dialog | is | classic counterpart |
|---|---|---|
| 4730000 NDLGINVEN | frame: CLOSE, TAB_ / TABKEY_ PARTSIMG, VISUALITEMIMG, STICKERIMG, EXCHANGEIMG, COUPON (no licence tab key) | 3060000 INVENBASE |
| 4740000 / 4750000 | "BATTLE" panel: battle quick slots 1-2 (set base, icons, covers, SEL, counts, BTN_NUM J=49) | 3800000 |
| 4760000 / 4770000 | "FIELD" panel: field quick slots 1-4 beside the window | 360000 / 390000 |
| 4780000 | performance table: TOP_INFO / POINT / SUM / CAR / PARTS / LEVEL / ASSIST, MAXSPEED, ACCELTIME, DAMAGE, BOOSTTIME, per-stat point columns, assist values, BTN_ rows for the tips | 3160000 / 3170000 points |
| 4790000 | TIP_TITLE / TEXT / DETAIL (the table rows' hover tip) | - |
| 4800000 | bag (MYINVEN_*, plus MYINVEN_EXPIRE_1) | 3190000 MYINVEN |
| 4810000 | parts: SLOTN_01..08 (01/03/05/07 row 1 speed / accel / durability / booster, 02/04/06/08 row 2), SLOTX labels, OPTSLOTN option slots, {S,A,D,B}{1,2}_GLINE / GSLOT / GOPSLOT in WHITE / GREEN / ORANGE / VIOLET / BLUE + GLINE_BLACK, RENTn, SELn, UPGRADEn, SET_PARTS_OUTLINE1/2 | 3130000 PARTSNEW |
| 4820000 / 4830000 / 4840000 / 4850000 / 4860000 / 4870000 | drift items, sticker, auction, modify (+ TXT labels 11742-11747), car info, set box | 3140000 / 3100000 / 3090000 / 3210000 / 3150000 / 3180000 |

Classic vtable entries: `0x5d38e0` init, `0x5d92e0` update (timers, mouse, drag & drop, popups; per-page refresh calls
`0x5cb3d0 0x5cdfa0 0x5ce250 0x5cdda0`), `0x5c7f10` parts page (3120000 / 3130000), `0x5d1da0` (17 KB decompiled: message
handler), `0x5d0c40` show/hide. Page functions by their control ids: tabs `0x5cc320` (3060005-16), bag sort + coupon
`0x5cc550` (3190026-29, 3190092-94, 3200004/7), drift items `0x5ceba0` (3140012-15), auction `0x5d0a60` (3090043-45),
points simple / detail `0x5cf2b0` (3160022 / 3160024 / 3160025; earlier listed as "set activate", wrong), battle slot
icons `0x5ce900` (vtable +0x68), modify `0x61a3b0` / `0x5ded50`. Licence page: `0x5c8ce0` shows the licence box when
POSITEM 3080006 is hovered (0x2001).

### INVENBASE tabs [exact layout]
Tab images read Parts / Drift Item / Sticker / Auction / Modify (TAB_PARTSIMG, VISUALITEMIMG, STICKERIMG, EXCHANGEIMG,
COUPON - the last one is drawn "Modify"). Invisible TABKEY_* buttons (J=9, Tab) sit over them, including
TABKEY_LISENCEIMG between Drift Item and Sticker: the licence page 3080000 has no tab image in this client.
Pages: Parts = 3130000 PARTSNEW (4 stat columns, two slots each) + 3150000 car info + 3160000 / 3170000 points (detail /
simple); 3120000 is the older 12-slot parts page; Drift Item = 3140000 (5-slot column with up/down, car view arrows);
Licence = 3080000 (mounted licence, "Drag and drop to equip a new License"); Sticker = 3100000; Auction = 3090000
(All / Selling / Sold lists of the auction house); Modify = 3210000 (part + Mittron enhancer, fee, before/after);
3180000 set-parts info, 3610000 set activate (Alpha/Beta restoration lube, buy / use), 3200000 coupon code input.
MYINVEN 3190000: 20 slots (72x72), +N badges (UPGRADE01-20), SLOT_USING "in use", SLOT_RENT, high-gloss marks,
sort buttons TYPE / DATE / MITO (J=121/122/123 = F10/F11/F12) and a sort combo box, page arrows, Mito.

### Tooltips: one manager for all hover boxes [exact]
`0x626920` (init of the tooltip/notice manager) grabs ITEMINFO 1320000 (+0x4.. controls 1320001-9), 1310000,
COUPONINFO 1330000, AUCTIONKEYINFO 1350000, LISENCEINFO 1360000 (earned, normal colours) and 1370000 (not earned,
grey), POPUP 4170000 (HDL yes/no notice) and VIRTUAL 3820000 ("Virtual HUV Battle Party." confirm, an HUV-chase notice
it only shows). Every text control gets a black outline (`+0x194 = 1, +0x198 = 0xff000000`). `0x626200` runs a timed
queue of these notices (0x220-byte entries). Licence boxes print "[Requirement]" (11263) and "[Bonus Effect]" (11264)
(`0x626f5e`, `0x626feb`). The BG controls are colour-only boxes (B4000000, 70 % black).
Line builders: parts `0x6239a0` ("%s %s + %d" grade letter + "%sSpeed Part" 10352/10354/10356/10358, "Speed + %u"
10353..10359, option/docking parts 10918-10922 / 11005-11008, "Equippable on N Parts" 10923-10930, "Required Level:
%d" 10360, "Required Grade: %d" 11275, "Usage period: %d hours" 10398, "Expired" 10795, "Broken" 10874); shop items
`0x659c20` ("%d D" 10280, "Usable for %d days" 10281, "Usage Period: Unlimited" 10282, "Unlimited" 10288) and
`0x622c00` ("%s%d days" 10410, "Infinite" 10411, "%d Mito" 11327, "%s%dDay(s)/Mileage %d" 10934).

**DIALOG130000** is not a screen: dialog 130000 has no controls. The "72 KB of code" are functions whose constants
are the 0x20000 bit (131072, 131073 ... e.g. `0x6c5640`, `0x7e0e20`, `0x6d29d0`) read as control ids. Nothing to build.

### Bag rules the client enforces (texts it uses) [exact texts, inferred places]
Full bag 10039 / 10269 / 10450; "This item is not equippable." 10449; level 10451 / 11276; vehicle 10453; expired
10454; "You cannot discard paid-for items that have expiration dates." 10455; "This item cannot be discarded." 10456;
"Currently selected item will be deleted." 10509; "Enter the quantity of item to discard. / Possession Amount %d"
11126; G-Coin items 10808; part box 10716 / 10717 / 10722 / 10739; broken parts 10877; "You cannot equip this part."
10933; "Having too much Special Drink..." 11059; "You cannot use this item at this time." 11100; Undercity 11478;
chases 11862; auto drive limits 11103-11132; recycle kit 11292/11293; extend 11887; grind 11888/11889. Category names
for the shop filter 10426-10437 (All, Paint, Neon, Car Window Tinting, ...). localize\inventory.xlt names the drag
cases (ItemDelete / ItemDeleteEA when a 1 / N stack is dropped outside the window, SlotwearNotPartsLevel, ExchageMito).

### Quick slots [exact]
Class vtable `0x86d7f0`. HUD: ITEMQUICKSLOT 360000 (BASE_TILE / ITEMQUICKSLOT_n frames, BASE_ITEMPOS_n icon) + 390000
(COVER_n cooldown cover, COVER_COUNTn count, BTN_NUMn number keys J=49/50, CHK_NUMn auto-use lock). `0x54f610`
registers 360000 / 390000 / 4770000 and calls `0x54e150` (show 360000 + 390000; 390000 only when not in a mode
flag), `0x54e3a0` is the NDLG panel 4760000 / 4770000 used while the inventory is open (`0x5dcaa0`: show(1) -> NDLG
panel, show(0) -> HUD tiles). Slot count = extension count (+0x194) + 2; panel widths 122 / 191 / 260 px for 2 / 3 / 4
slots. `0x54ea30` resets a slot, `0x54f120` clears slots whose item is gone, `0x54eab0` draws the cooldown cover
(below 100 % it scales the cover), `0x54e970` uses a slot on a left click (`0x54ded0` sends the use). "Permanent
effect items cannot be used in this area." 10170. Battle Zone items have their own slots (PVP 3780000-3800000).
- Handler of 390000 `0x54f180`: BTN_NUM1-4 (390013-16) -> `0x54ded0(slot, -1)` (use), CHK_NUM1-4 (390017-20, check 0x401)
  -> `0x54e8f0`: if the slot's item has a duration (+0x248) and the box is ticked, auto[slot] = 1 and it is used at once.
- Auto quick slot: `0x512730` / `0x4f7396` set char+0xf39 = "i_autoquick is in the bag" (also +0xf3a i_automitron, +0xf3b
  i_autodrive); `0x54de1f` shows the CHK boxes only then. Without it: 10970 "In order to enable the Auto Quick Slot option,
  please purchase the item in the Drift Shop.". i_autoquick: "allows you to continuously use the Quick-Slot (Activates by
  Left-Clicking on the A after placing an item)", 7 / 30 days for mileage.
- NDLG field panel (`0x54e3a0`): SET_BASE 122 / 191 / 260 px high for an extension of 0 / 1 / 2, SET_BASEBOTTOM at -158 /
  -89 / -20, slots 3-4 below slot 2 (68 px apart). The extension count +0x194 is never written by the quick-slot code and
  no product in vshopitem / useitemclient / vshopcategory extends it: the count came with the character data.
- Battle quick slot: server feature "BattleZone.BattleQuickSlot" (string 0x85f324, bit 0x10000 read at 0x453e44, tested at
  0x580820); items QUICKSLOT 9800-9820 (i_b_untouchable / skidrush / quickb _10 / _30) and useitemclient battle_quick
  (i_n_00055 Crash Capsule, 00056 Boost Recharger, 00058 Invincible Rush, 00059 DCR, 00060 Emergency Boost Kit, 00099 Mk.II
  repair 30 %); `0x5ce900` draws the two slot icons (`0x580810(i)` = the slot's item).

### Set parts [exact data, texts]
World/Data/ItemClient.tdf (924 rows) is the parts table. Columns: 0 stat (speed / accel / crash / boost), 1 set id, 2 item
id, 4 name, 5 set name, 6 grade letter, 7 required level, 9 points, 11 enhance limit, 12 / 13 price / sell, 14 the
counterpart, 19 the set bonus text, 20 assist id, 21 period in hours, 22 restoration lube (Jew_A / Jew_B).
- Sets: the girls' sets (kayla, yuki, hyeyoung, erina, duffy, mrh, branton, olivia, hunter ...: "EXP +50%, Boost on HUV
  hit +20%, Accel +36p, Speed +44p"), Shinoya / High GR / Double Alpha and Beta (AH/BH/AI/BI/AD/BD: Undercity reward pools
  UC_moon_s ...), Gotcha Alpha / Beta (AG, 180 h), the shop sets (AIRShop Duty Free, CITYShop, MAShop), the kickstarter
  set, the car-rental sets (rent_*, r_*: 168 h, rent_1 336 h, no counterpart). 720 h is the girls' / Undercity sets' period.
- "_to" rows are the expired form: same set, the assist n/a (no bonus), some with fewer points; `si_x` <-> `si_x_to`
  point at each other. Restoration lubes: i_d_set_ch01 "Alpha Restoration Lube" (Jew_A, 250 G-Coin) and i_d_set_ch02
  "Beta" (Jew_B, 350) - "Restores the bonus effect of ... Sets temporarily."; the box also has their Mito forms
  i_d_set_ch01_mito / ch02_mito (icons only).
- Dialogs: SETPARTS 3180000 / NDLG 4870000 (TITLE, DESC1ST bonus, IFYOUGET1ST, PARTSPOS / PARTSTEXT 1ST-4TH), SETACTIVATE
  3610000 (BASE, NAME, COUNT, POSITEM1ST-4TH = the four lube icons in one place, DESC, BUY, USE), PARTSNEW SETROW_1ST /
  2ND and NDLG SET_PARTS_OUTLINE1 / 2 around a row. Texts: 10773-10780 "<name>'s Set Part", 10798 "Can be restored using
  Restoration Lube", 10881 Restore, 10882 "%s has been restored.", 10883 "Restoration Successful", 10884 "Part will be
  restored.", 10886 "Restoration Kit", 11487 "[Set Parts]", 11490 "Remaining Period".

### Period items [exact texts]
"Usage period: %d hours" 10398, "Expired" 10795 / 10405, "Expiration Date: %d" 10549, "This item has already expired."
10454, "You cannot discard paid-for items that have expiration dates." 10455, "This item has expired." 10890, "Usage
period will be added to the same item." 11044, "Extend %s for %d day period." 11887, rental car returned 10969. The shop
sells 7 / 30 / 90-day periods (vshopitem Period 7D / 30D / 90D: i_autoquick 7 or 30 days, passives, channel passes).
Marks: MYINVEN SLOT_RENT00-19 per slot, NDLG MYINVEN_EXPIRE_1 (one mark the client moves), NDLG parts RENTn.

### Trade [exact]
TRADE 1190000 handler `0x62e710`: CLOSE (1190007) -> `0x78dd40` cancel; MY_LOCK (1190013) -> `0x78dbe0(lock = !locked)`;
MY_TRADE (1190014) -> if incoming items (`0x62d120`) exceed free slots (`0x6d35c0`) "Inventory is full" 10269, else
`0x78de90` confirm; 1190056/57 pages; 1190059-61 sort (sound `0x4ebd`); MY_MONEY_1 (1190106) -> count box 3240000 with
"Please enter the amount to exchange." 11042 (`0x551b30` / `0x551bf0`); drops (0x2001) on my slots -> `0x62de00`
(offer), on the bag -> `0x62d220` (take back). OPP_LOCK / OPP_TRADE show the other side. The request popup is "%s has
requested a deal. Accept?" 10726; server replies 20046 (failed to create), 20047 (no partner), 20055-20060 (trade /
cancel / lock / unlock / complete / amount failed), 20062 (not tradeable), 20113 / 20118 (not during a trade), 20114
(you may not trade with this user). The player menu COMMAND 700000 has the Trade entry.

### Items [data]
init\vshopitem.xlt (1,453 rows) carries the consumables: drinks i_d_drk_01 EXP (+30 %, except quest EXP), i_d_drk_02
MITO (+50 %), i_d_drk_03 Item (double drops), 3ddrink / i_package_02 sets; Gio i_d_repair01 ("fix your car"), Turbo
man, Mito cheques i_d_m_10k .. i_d_m_10g, enhancement / restoration kits, seed parts, passive assists, scratch cards,
gacha coins, event boxes, quick-slot Battle Zone capsules. Icons come from PARTSLIST 380000 / CMN 2760000.

## 2. What the rebuild does

- **Which set**: the NDLG set (`InventoryWindow.Classic = false`, the default; PlayerPrefs `DriftCity.Inventory.Classic`).
  The NDLG dialogs are opened with their control ids renamed to the classic names for the build (ID_NDLGINVEN_MYINVEN_* ->
  ID_MYINVEN_*, _CARINFO_, _VISUALITEM_, _STICKER_, _EXCHANGE_, _UPGRADE_, _SETPARTS_; frame -> ID_INVENBASE_), so one page
  code (and Auction/AuctionPage.cs) serves both; the views keep their own dialog ids (4730000 ...;
  `InventoryWindow.DialogFor(classicId)`). NDLG-only parts: the parts page 4810000 (row 1 = the car's four stat slots,
  grade lines white / green / orange / violet by grade, blue for set parts, black when empty, RENT on period parts, +N,
  set outline; row 2 = the car's PerfSlots2, option slots = OptSlots, section "Second row and option parts") and the performance table 4780000
  (total = vehicle (grade + custom parts) + parts (performance parts + set bonus) + level 0; max speed and boost time from
  the car model's curves, accel time a 0-100 km/h estimate from the engine curve, chase damage the chase's 6 - 45 range,
  assist: the assists' km/h x 0.8, the set or first assist's name, HUV damage %, boost seconds) [remake]. The rows' tips:
  section "Performance tips".
- **Set parts** (`SetParts`, `src/items.js`) [remake rules]: a set part from a reward is a bag stack (si_* / r_*) until it
  is fitted (drag it onto its stat slot, or use it); fitting (`set.fit`) turns it into a performance part (PerfPart.Item,
  PerfPart.Until) whose period starts then. Points: the table's points + 1 per enhance level per grade step. The set bonus
  counts when the four stat slots hold one set and none is expired; its "+Np" terms (Speed / Accel / Durability / Boost)
  add to the stats (PerfRules.Fitted), the rest of the text is shown. Hovering a fitted set part shows the set box (3180000
  / 4870000): bonus, state, the four parts lit when fitted. When the period ends the part turns into its "_to" form (kept,
  no bonus, "Can be restored using Restoration Lube"); a rental part is removed. Right click on an expired part (or using a
  lube from the bag) opens SETACTIVATE 3610000: lube name, count, price; BUY (`set.buyLube`, 25,000 / 35,000 Mito = 100 Mito
  per G-Coin), USE (`set.restore`: every expired part of that lube's sets on the car back for another period, 720 h when
  the row has none; 10884 confirm, 10882 per part, 10883). Double click takes any part off. Trades keep a part's set.
- **Period items** (`ItemPeriods`, `src/items.js`) [remake rules]: StackItem.Until / PerfPart.Until (Unix ms). The tooltip's
  period line shows hours left (10398), "Expired" (10795), a set part's period before it is fitted, else unlimited (10282);
  the bag marks period items (classic SLOT_RENTnn, NDLG EXPIRE mark copied per slot) and dims expired ones. A running
  period item can't be discarded (10455) or traded; using an expired one says 10454. A second grant adds its period
  (`items.giveTimed`, `ItemPeriods.GiveTimed`; 11044). The sweep (`item.expire`, sent when the inventory opens and every
  5 s while something is due; offline the same rule) removes expired stacks, converts set parts and removes rental parts,
  with "This item has expired." (10890) per item.
- **Quick slots**: 2 + extension slots (the driver's `qslot_ext` stack, 0..2 [remake id]: the client tables have no
  extension product); tiles 3-4 placed below; the NDLG panel 4760000 / 4770000 beside the NDLG window takes drops, shows
  icons, counts and the cooldown, its frame sized like 0x54e3a0. Auto quick slot: with i_autoquick (running) the CHK_NUM box
  shows on a drink's slot; ticking uses it now and re-uses it whenever the drink's effect has run out (cooldown, not in
  Undercity); without the item 10970. Flags kept per driver with the slots.
- **Battle quick slots** (`BattleQuickSlots`): 2 slots filled in the inventory (classic 3800000 / NDLG 4740000 + 4750000
  beside the window; battle items only), shown in a PvP race (3780000 + 3790000 while RANK 1380000 is open), keys 1 / 2
  or a click, 3 s cooldown, counts; item.use spends one (server: `items.use` effect "battle"). Effects here: the repair kit
  takes 30 % damage off, the recharger halves BoostFillSeconds for 40 s; every use fires `BattleQuickSlots.Used(kind,
  seconds)` and `IsActive(kind)`; Inventory/BattleItemEffects.cs applies crash / invincible / dcr / eboost to the car
  (PHYSICS.md 13.5) and the recharger sets the car's +0x1abc = 2 (Battle Zone) - see below;
  `InLowerHalf` gates the four "low position" items; the emergency kit needs 30 % gauge.
- **Licences in the bag**: earned licences that aren't mounted are bag entries (BagKind.Licence, key l:id, the licence's
  icon and LISENCEINFO box); drag one onto the licence page's icon (or use it) to mount it (`licence.mount`). They can't
  be traded, sold, discarded (10456) or put in quick slots, and only drop where a slot asks for them.

- **Window** (`InventoryWindow`): INVENBASE + MYINVEN as an OriginalMenus screen (Esc / MenuOpen work as before).
  Parts page (fitted parts as icons, drop a bag part on its stat column to fit it, right click takes it off, points
  detail / simple), Drift Item (3D car view in the page with the CARVIEW arrows, the car's custom-shop parts in the
  5-slot column; click fits / unfits), Licence (Tab), Sticker (the custom mark page, below), Auction, Modify
  (part + Mittron Enhancer -> perf.enhance with fee, chance, before / after; client texts 10765 / 10766 / 10888 / 10743).
  Tab / Shift+Tab cycle pages; F10 / F11 / F12 sort by type / date / Mito.
- **Bag**: unfitted performance parts, named stacks (enhancers, materials, drinks...), shop items (paints) in the
  order obtained; +N badges, "in use" paint badge. Hover = ITEMINFO box; double click = use; right click = the
  client's popup with Yes (use / equip) / Sell / Delete / Cancel; drag onto a part slot, a quick slot, the trade
  window or out of the window (= discard: 10509 for one item, the 3240000 count box with 11126 for a stack).
- **Tooltips** (`ItemTooltip`): ITEMINFO lines name (count), required level, "Usage Period: Unlimited", description,
  "%d Mito" (sale price), "Non-tradeable", stat line, drink time left; lines stack and the B4000000 box grows.
  LISENCEINFO 1360000 / 1370000 for licences with the conditions and this driver's progress.
- **Use** (`ItemUse`, mirrored in `src/items.js`) [remake]: drinks start a 60-minute effect (+30 % EXP / +50 % Mito on
  chase, race, combo and Undercity rewards, not quests; item drink doubles drops), one of a kind at a time (11059);
  Mito cheques pay their value; Gio repairs the car (CarEffects.Damage01 = 0); Mittron oil fills the booster gauge; the fuel items i_n_00004 / 00005 / 00006 add 2 / 10 / 300 litres to the Mittron tank (Game/Fuel/MittronFuel.cs);
  the crafted items keep WorkshopMenus.UseItem; paints recolour the shown car; parts are fitted (perf.fit).
  Sell [remake]: counter goods for half their price, performance parts 8k / 32k / 120k / 300k by grade +10 % per
  enhance level; other items can't be sold. Discard: everything in the bag.
- **Lucky bags, coupons, token books, scratch cards, event boxes**: `ItemUse.IsEventItem` / `OpenEventItem` look up
  `DriftCity.Game.LuckyBag` (static `Open(string itemId, Action<bool> done)`, optional `bool IsBag(string)`) by
  reflection. Not present yet: the item shows "opened by the event system, which isn't available here yet".
- **Quick slots** (`QuickSlots`): 360000 + 390000 with the city HUD, 2 slots (tiles 3-4 placed below for the
  extension); drag a consumable from the bag (10170 for others), keys 1 / 2 or a click use it (no popups, chat log
  line), right click empties, 3 s cooldown cover [remake], counts; hidden in PvP races; digits are left to the station
  panel when it is open; slots saved per driver in PlayerPrefs [remake]. Started without any scene hook
  (Canvas.willRenderCanvases creates a driver object; it also forwards trade.* messages to the trade window).
- **Trade** (`TradeWindow`, `src/trade.js`): offline "needs the online server". Online: `TradeWindow.Request(name)`
  -> trade.request; the partner gets the 10726 Accept / Deny popup; the window shows both offers (8 slots each, +N
  badges, Mito boxes), my bag on the right (pages, sort; a partly offered stack shows the rest); click / drop to offer
  (count box 10394 for stacks), click my slot to take it back, MY_MONEY_1 -> count box 11042, Lock toggles, Trade
  is enabled when both locked; the client's free-slot check before confirming (parts box limit 60, 10269).
  Server [remake]: same channel only, not in a race room, one trade per driver, invites expire after 30 s, an offer
  change clears both locks and confirms, fitted parts can't be offered, the second confirm re-checks both bags and the
  parts limit and swaps everything or nothing (parts get new uids, keep type / grade / enhance), both drivers saved
  and sent their driver; disconnect, another login or a channel change cancels.

### Second row and option parts (round "last car gaps")
- [exact] NDLGINVEN 4810000 has eight part slots, two per stat (SLOTN_01/03/05/07 row 1, 02/04/06/08 row 2), each with an
  option slot (OPTSLOTN_nn 35 px frame, OPTSLOT_nn 32 px icon place, OPSELn hover frame, {S,A,D,B}{1,2}_GOPSLOT_<grade>).
  ItemClient.tdf option rows: op_S / op_A / op_C / op_B / op_F (column 9 stat points, e.g. "Acceleration Points +3"; or an
  assist in column 20: Turbo Launch a_00002, Booster Extension a_00048, Engine Speeder a_00031 ...) and docking rows dock_S /
  A / C / B / D (assists docking_b "+N points", docking_p "+N %"); grade letters FN / FS / FH, required level column 7.
  Tooltip texts 10918-10922 / 11005-11008 (option / docking part names), "Equippable on N Parts" 10923-10930.
- [remake] (the client's server kept the slots; Inventory/CarParts.cs, server src/carparts.js): OwnedCar.PerfSlots2 is row 2
  (a second performance part of the column's stat, its points add like row 1), OwnedCar.OptSlots[stat * 2 + row] the option
  part (an ItemClient id). An option part is a bag stack until fitted, needs a performance part under it, takes the stat of
  its letter (F and D fit anywhere), checks the required level; removing it (right / double click) or taking a row-2 part off
  returns it to the bag (on row 1 it stays on the slot, inactive while the slot is empty). Its points and docking bonus add to the stat (CarParts.Points); its assist becomes active
  (CarAssists, PHYSICS.md 13). Messages: parts.fit2 {slot, uid}, parts.unfit2 {slot, type}, opt.fit {slot, index, id},
  opt.remove {slot, index}.
- Page: row 2 slots take parts by drag and drop; option frames show the option part's icon (dimmed without a part under it)
  and take option parts by drag and drop; the bag leaves row-2 parts out; option parts have their own tooltip lines.

### Performance tips (NDLGINVEN 4790000)
- [exact layout / inferred texts] TIP_TITLE / TIP_TEXT / TIP_DETAIL; the table's BTN_SPEED / ACCEL / DAMAGE / BOOST rows
  show it on hover. Texts: 11730-11733 "■ Max Speed / ■ Time to reach / ■ Crash Damage / ■ Boost Time", 11734-11737 the one-line
  explanation ("Your vehicle's max speed on a flat road." ...), 11738-11741 the detail (Speed / Acceleration / Durability /
  Boost, colour tags dropped). Remake: InventoryWindow.ShowTip / HideTip, placed under the row.

### Licence pages (LICENSE 710000, OTHERLISENCEBASE 3070000)
- [exact] LICENSE 710000 is the driver's licence card: binder 0x62ed90, show 0x62ee30 (the driver's portrait CHARnn of eight,
  ID = name, LEVEL = 10390 "Beginner", ISSUEDATE = today's "%d-%d-%d"); opened by a server reply (0x55aa60) and a mission
  step (0x70d270). OTHERLISENCEBASE 3070000 frames another driver's licence page 3080000 (CLOSE handler 0x5c8b80).
- Remake (Game/Licences.cs `LicencePages`): ShowCard(card) / ShowOther(name): the frame, the page with that driver's mounted
  licence (LISENCEINFO on hover) and the card; online driver.info, offline an AI driver's card (with a licence of its level).

### Custom marks (the Sticker page)
- The Sticker page (3100000 / 4830000) is the custom-mark page: the car turns in the view with the mark on its doors, the
  images in `<save folder>/Stickers` are listed (click = use), "Import image file..." takes a path (MsgBox 3230000), "Remove
  mark" clears it. Rules and server messages: PHYSICS.md section 14 and Game/CustomMarks.cs.

### Server messages
| message | body | reply / push |
|---|---|---|
| item.use | id, slot | + effect ("drink", "mito", "repair", "boost"); drinks fill driver.Buffs [{Id, Until ms}] |
| item.discard / item.sell | kind stack (id, count) / shop (itemId, count) / part (uid) | sell: mito |
| trade.request | name | id; partner gets trade.invite {id, from} |
| trade.accept / trade.decline | id | both get trade.open {id, with}; decline -> trade.closed {reason: declined, by} |
| trade.offer | items [entry refs], mito | both get trade.state {id, with, me, them: {items, mito, locked, confirmed}} |
| trade.lock / trade.confirm / trade.cancel | lock | trade.closed {id, reason done / cancelled / left / failed, error, done} + driver |
| item.expire | - | expired [ids] + driver (the sweep) |
| set.fit | slot, id (si_* / r_* stack) | + driver; level (10451), parts box 60 |
| set.restore | slot, lube (i_d_set_ch01 / 02) | restored n + driver |
| set.buyLube | lube | mito (negative) + driver |
| item.use (battle items) | id, slot | effect "battle" |

### Hooks for other modules (not edited here)
- `Game/Pvp*.cs` (race HUD): `BattleQuickSlots.InLowerHalf = () => myRank > field / 2;` when the race starts (null after).
  The item effects themselves are applied by Inventory/BattleItemEffects.cs (no race-code hook needed).
- `src/events.js` grant: period rewards (`g.days`) -> `require('./items').giveTimed(p, g.item, g.count, g.days * 24)` instead of
  `give(...)`; `src/economy.js` ensureProfile / login: `require('./items').sweep(p)` so expired items go at login too.
- `Game/Auction/*` and `src/goods.js`: listed parts copy Type / Grade / Enhance only, so a set part loses its set at the
  auction; copy `Item` (and refuse running period items) to keep it.
- Options screen (OPTIONS.md owner): a "classic inventory" switch can set `InventoryWindow.Classic`.

## 3. Left
- Coupon code input 3200000 and COUPONINFO 1330000 (events module); AUCTIONKEYINFO 1350000 (car keys
  of the auction house, info only); the 0x200 variant
  of the NDLG flag; period grants from events / shops (hook above).
