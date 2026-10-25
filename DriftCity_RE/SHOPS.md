# Shops: the advanced car dealer, the custom shop's extras and the Auction House

Sources: DriftCity.exe through the Ghidra bridge (`decompiled/phys/0x*.c`), the disassembly (`text.asm`), game.nui
(Harness `ids full <dialog>`), `localize\globaltext.xlt`, `localize\globalcontent.xlt`, `init\vehiclelist.xlt`,
`init\vehicledisplay.xlt`, `init\vshopitem.xlt`, `init\vshopcategory.xlt`. [exact] = read from the code or data,
[inferred] = from names, layout or behaviour. Remake files: `Game/Shops/*`, `Game/Auction/*`, `CustomShop.cs`, the
dealer part of `Menus.cs`, `Inventory/InventoryWindow.cs` (Auction page), server `src/auction.js`, `src/vshop.js`,
`src/goods.js`, `src/mail.js` (item attachments).

## 0. Which dealer the client uses [exact]

The autoshop stage's enter function `0x7438a0` (string "AutoShop Stage Enter :%d") reads globalcontent `CarShop`
(`0x7d27b0("CarShop")`): 0 shows the classic AUTOSHOP window (`0x5a10b0` on window +0x358, dialog 3260000), anything
else shows the advanced dealer (`0x5afa60` on window +0x35c, the AUTOSHOPADV set). `globalcontent.xlt` has
`CarShop = 1` in every column, so this client uses the advanced dealer. It is independent of the main bar type
(`Main.MainButtonAdvanced`), but it is the dealer that goes with the ADV main bar 3010000 the remake already uses.

Other globalcontent keys used here: `CarShopSellMaxGrade = 0` (the class buttons on the sell tab, `0x5afa60`),
`AuctionCheckType = 0` (level column instead of a running number, normal level instead of the "grade level"),
`AuctionMinLevel = 1`, `AuctionBuyedMsg = 1`.

## 1. The advanced car dealer "CAR SHOW ROOM"

Init `0x5b0500` grabs AUTOSHOPADVBG 2640000, AUTOSHOPADVDETAIL 2650000, AUTOSHOPADVMENU 2660000, AUTOSHOPADVMITO
2670000, AUTOSHOPADVLIST 2680000, the wish list AUTOSHOPADV 2550000, the compare dialog 3280000 and growth 3290000,
and labels the detail rows with 10800-10803 (Speed / Durability / Accel / Boost) and 11814-11824 (Level, Type, Size,
Car Bonus, Color, Total, Mito, My Mito, G-Coin, Mileage) and 11935 (My G-Coin) [exact].

- **BG 2640000**: a full-screen grey backdrop (`fullmap_moonpalace_00.dds` strip), MARK (maker emblem 107x58) and
  MARKNAME top left, LEVEL (the grade badge) and the V / R buttons top right [exact layout].
- **MENU 2660000** (show `0x5afa60`, handler `0x5afe50`) [exact]:
  - tabs BUY "Purchase", SELL, REWARD "Receive Reward" (TABKEY_* are invisible Tab-key twins). Each tab calls the list
    refresh `0x5af660(tab)`: tab 0 titles the list 11810 "Recommended Items" and enables NEW / STAR / GM when flags
    +0x6a0/+0x6a4/+0x6a8 say a car qualifies; tab 1 titles it 11811 "Sold product information" and enables the class
    buttons of the classes the driver owns.
  - filters set +0x38 and call `0x5af0b0`: NEW 0, STAR "Popular" 1, GM "Duffy's Picks" 2, ALL 3, V1..V9 4..12. New /
    Popular / Duffy's are the columns of `init\vehicledisplay.xlt` [exact for the codes, inferred for the columns].
  - a strip of nine cells BTN00..08 over LOGO1..9 (name plates), with badges BOARD "Equipped", HOLD "Inventory",
    NOTSELL "Locked" (ui_button22) and LOCK00..08 (a padlock); LEFTBTN / RIGHTBTN (VK 37/39) step the selection
    (+0x34). A cell click runs `0x5a2520`, `0x5aead0`, `0x5a40b0`, `0x5addd0` (select, price panel, detail).
  - MSGBG* + MSG_TXT1/2: the coupon notice ("Coupon required!" 11808, "Press F5 to check your Coupons." 11809).
  - CLOSE (VK 27) runs `0x65af10` and `0x5afa60(0)`.
- **DETAIL 2650000** (`0x5a7a30`, `0x5a87b0`): car name, four stats (number, bar, +N in FFDA3E00 / -N in FF5386E3
  against the current car), "Lv. %u+" (10484), type (11803 Basic / 11804 Coupon / 11805 Special / 11927 Scratch card
  vehicle, chosen by a per-car table field), size Small / Mid / Full, two car-bonus rows, four factory colours with
  check boxes, COMPARE (3280000), REMODEL "Modify", PRODUCT "Related Item Record" (VK 17 Ctrl) [exact layout; the
  type text choice exact, its source column inferred = vehiclelist MakeType].
- **MITO 2670000** (`0x5addd0`, `0x5aead0`): Mito price, My Mito, CC price and balance, Buy / Sell, Transaction
  History, CC recharge, total, mileage. `0x5addd0` also shows a 10,000-40,000 Mito notice by the price tier
  (`+0x250 +0x60` 0..3) when the flag `+0x24c & 0x80000` is set [exact, purpose inferred: a server promotion].
- **LIST 2680000** "Recommended items": six rows (image, name, Mito price, mileage price, CC price, check box,
  Purchase / Gift / Mileage), LEFT / RIGHT pages, a Ctrl "Close" [exact layout].
- **Wish list 2550000**: nine 42 px cells down the right edge, an X each, up / down arrows [exact layout].
- `globaltext` 10506 "Purchase the selected vehicle.", 10507, 10508 (sell back), 10491 (not the car you drive), 10492
  (equipped items), 11806 "You do not have enough money.", 11807 "Higher Level required!", 11825 "Garage Full",
  11813 "Attached item will be deleted when resell." [exact texts].
- The ADV dealer shows the car in the drift-shop scene (`0x640580` sets the shop scene mode 3 and places the camera at
  0.6 x the car's size) [exact]; the remake keeps the dealer's own showroom NIF `autoshop\autoshop.nif` behind the
  panels (a render texture layer between BG and the panels).

**Remake** (`Game/Shops/AdvancedDealer.cs`; `Menus.CarShop()` opens it when globalcontent CarShop != 0, the classic
screen stays as `Menus.ClassicCarShop()`):
- Purchase lists dealer cars with a vehiclelist Display slot (sorted by class and Display; 0 = not sold in the dealer,
  999 last), without the coupon / special cars; Receive Reward lists the coupon cars (coupon.xlt) and MakeType >= 1
  cars; Sell lists the driver's cars. Filters, strip, badges, arrows, coupon notice as above. The detail shows the
  stats against the car you drive, level, type, size by length (< 4.1 m small, < 4.6 m mid), colours; the car-bonus
  rows are hidden (vehiclelist "Car Assist" has tags like `a_spect` with no texts in this client).
- Buy: already owned, level (11807), Mito (11806), coupon (11808 + 11809, spent offline; the server checks online),
  garage (11825), confirm 10506, then `Progress.BuyCar` in the chosen colour. Sell: not the car you drive (10491), no
  performance parts fitted (10492), custom parts go with it (11813), confirm 10508, `Progress.SellCar` at
  `ShopData.SellPrice`.
- Compare opens 3280000 (current car left, shown car right). Modify opens the tuning panel when the shown car is the
  one you drive. Related Items (Ctrl) replaces the detail panel with the LIST: custom parts made for that model
  (Purchase -> `Progress.BuyPart`, Mileage -> mileage, Gift -> the gift dialog); on the sell tab the parts on the car.
- Wish list: a right click on a strip cell adds / removes the car (remake: the client's add control isn't
  identified); its icons jump to the car; the X removes it. Saved in the profile (`WishCars`; online `wish.set`).
- G-Coin is hidden everywhere (CC lines, recharge): there is no paid currency.

## 2. The custom shop's extras

Functions [exact]: send gift 2840000 init `0x65aae0`, received gift 2850000 `0x659790`, trade history 2290000
`0x6474b0` (10450), wish list 2540000 `0x667db0`, wish-list checkout 2620000 `0x64ce20`, mileage checkout 2630000
`0x649cc0`, the period / price lines `0x64b1a0` (10280 "%d D", 10281 "Usable for %d days", 10282 "Usage Period:
Unlimited", 10288 "Unlimited"), the item list handler `0x6533c0` (the 12-year age check 10995 for item 0x2378, the
`i_d_partsbox` level check with AuctionMinLevel / 11828). `vshopcategory.xlt` has "Allow Coin Gift" / "Allow Mito
Gift" per category; `vshopitem.xlt` has UseHancoin / Use Mileage and per-period $ / Mito / mileage prices, but in this
client every G-Coin and mileage price is 0 and no item has Use Mileage = 1 [exact data]. Config `Etc.VShopGiftLevel`,
`Main.Mileage`, `Etc.MileagePromotion` were server settings (strings only) [exact strings].

- SENDGIFT 2840000: sample, name, four period buttons, receiver edit + Search, comment edit (TL 1023), price, balance,
  missing amount, Gift (Enter) / Cancel (Esc) [exact layout].
- RECVGIFT 2850000: sample, name, sender, send date, comment, Get Gift (Enter) / Get Later (Esc) [exact layout].
- BUYHISTORY 2290000: tabs Purchase History / Sent Gifts / Received Gifts, a 13-row list, column headers per tab, a
  Get Gift button per received row, 8 page buttons [exact layout].
- BUYWISHLIST 2620000 / BUYMILEAGE 2630000: two items at a time with a scroll bar, check boxes, period buttons,
  totals, pay / balance / missing, Buy / Cancel [exact layout].

**Remake rules** (no paid currency; the original server decided these):
- Mito prices stay Mito prices. **Mileage**: 1 point per 100 Mito spent in the car dealer and the custom shop
  (online `src/vshop.js` `bought()` for shop.buyCar / buyPart / paint and gifts; offline `Mileage.Earn`); a product
  costs a tenth of its Mito price in mileage, rounded up to 10 (`Mileage.Price`, server `mileagePrice`).
- **Gifts** (online only, level 5): the sender pays the item's Mito price; the receiver gets a note from the sender
  carrying the item (`src/mail.js` item attachment); accepting it (the history's Get Gift / RECVGIFT, or taking it
  from the note) puts a paint in the bag, a part on the car the receiver drives (it must fit; the part counts as a
  gift part, so it adds nothing to the resale price). Gifts are sent unlimited (section 2b).
- **Wish list**: a right click on a product row adds / removes it (remake); the panel's icons open the Mito checkout
  of every wished item that fits the car; X removes. Saved in the profile (`WishList`; online `wish.set`).
- Product rows now show the mileage price and the Gift / Mileage buttons; the bottom bar's list button opens the
  trade history (online `shop.history` + `gift.list`, offline a local log `shops_<slot>.json`).

## 2b. Period prices (7 / 30 days) **[exact data and lines, remake rules]**

Client [exact]: vshopitem.xlt has five price blocks per product: `Period 7D / 30D / 90D / 365D / 0D`, each with `$Price`,
`Mito Price`, `Mile Price` and `Bonus Mito` (the block's first column is the number of days). 0x64b1a0 (checkout / gift
lines) fills a row's four period buttons in that order with the blocks the product has: 10280 "%d D" for the days blocks,
10288 "Unlimited" for 0D (only with the server type check 0x4ccf20); the chosen block (per row, `+0x120`) gives the price
(0x64a8d0 -> 0x5a27d0 days / 0x5a2830 Mito / 0x5a2890 G-Coin) and the line under it, 10281 "Usable for %d days" or 10282
"Usage Period: Unlimited". Rows: BUYWISHLIST / BUYMILEAGE `BTN_PERIOD{1ST,2ND}_{1ST..4TH}` + `TXT_PERIOD...`, SENDGIFT
`BTN_PERIOD_{1ST..4TH}`. The product list rows (2470000) have no period buttons, only the price.
Data [exact]: of 1427 supported rows, 747 of the remake's 1005 parts have Mito periods, all 7 D (+ 30 D for most): spoilers
5,000 / 15,000, aero kits 10,000-30,000 / 30,000-90,000, wheels 15,000 (7 D only), plates 1,000 / 3,000, boosters 15,000 /
45,000, air ducts 50,000 / 100,000; one row has a 90 D block, none a 365 D or 0D block. Paints have only the plain `Mito Price`.

Remake rules (client `Shops/ShopPeriods.cs`, server `src/vshop.js`):
- A part sells for its table periods plus "Unlimited" at 3 x its 30-day price (`UNLIMITED_FACTOR`; the remake sold parts for
  keeps at the 30-day price before). A part without period prices (horns, roof items, neon ...) and every paint sell
  unlimited at their old price.
- The custom shop's Buy button (and the row's Buy) opens BUYWISHLIST 2620000 with that product: the period buttons pick the
  price; the wish-list and mileage checkouts have the same buttons. Online `shop.buyPeriod {slot, item, days}` (the server
  takes the price from the parts table: `gamedata.js` parts carry `periods`), offline the same rule.
- Buying a period part again adds the days (11044 "Usage period will be added to the same item."); an unlimited purchase
  makes it permanent; a permanent part can't be bought again. A period part counts as a gift part (`OwnedCar.GiftParts`:
  no resale value, so a 7-day rental can't be resold for 30 % of the full price); its expiry is `OwnedCar.TimedParts` /
  `TimedUntil` (Unix ms).
- When the time is up the part leaves the car (a fitted one goes back to stock) with "name: This item has already expired."
  (10454): server `sweepParts` on the shop messages and `shop.partsExpire`; the client checks every 10 s and rebuilds the car
  when it is not racing / chasing. The product row shows "Owned (N D)" / "Fitted (N D)" and keeps its Buy button for more days.
- Mileage buys the chosen period for a tenth of its Mito price (`shop.mileage` with `days`); gifts are sent unlimited (one
  "Unlimited" button on SENDGIFT).
- The older message `shop.buyPart` (economy.js) still sells a part for keeps at its 30-day price; the client no longer sends
  it (left for the lead: route it to the unlimited price or retire it).

Tests: `Tests/BannersPeriodsTests.cs` (`PeriodPricesOffline`, `CityBadgesAndPeriodCheckout`: Preview/fx5_period_checkout.png,
fx5_period_owned.png), server `test/multipliers.test.js` (period parts, shop.buyPeriod, mileage, shop.partsExpire).

## 3. The Auction House

- Entry [exact, PARKINGLOT.md]: parking-lot area 4 -> `0x763840` case 4: age check (10995), level >= AuctionMinLevel
  (11828), then `0x598f60` shows the window (and on close puts the car back, `0x7629b0`). AUCTION 1660000 is not the
  auction: it is a password prompt ("Enter your password." 10217 / "Incorrect password..." 10218) whose OK sends the
  text to the server object and whose Cancel puts the car back (`0x5409c0` / `0x540a00`, init `0x540b70`) - a
  building password the server could ask for [inferred]. The remake's server never asks.
- **AUCTION 1300000** (init `0x59a25d`, handler `0x599c90`) [exact]:
  - CHK_ITEM / CHK_VEHICLE: items or cars (+0x90). CATEGORY combo (`0x598930`): All Items 10493, Speed 10494,
    Durability 10495, Acceleration 10496, Booster Parts 10497, (option parts 10918-10922 when `+0x248 & 0x10000000`,
    docking parts 11005-11008 when `& 0x20000000`), General Items 10498; cars: ten entries.
  - search `0x598670`: LEVELFROM / LEVELTO edits, USEABLEITEM caps the range at the driver's level, the
    "User ID/Item Name" edit; request with page and category, sound 0x4ebd.
  - ITEMLIST `0x5990c0` / `0x599420`: 13 rows a page (entries of 0xa4 bytes): required level (FF8F0700 when above the
    driver's, else FF301C08), name (+N, "Broken" 10874, the two option names), price, quantity, seller, credit;
    cars: level, "%s V%d", price, 1, seller, credit. 10 page buttons (`0x5988a0`), prev / next.
  - Buy `0x599ac0`: inventory space 10269 (items) / 10581 (car keys), Mito 10500, broken / expired parts
    10889 / 10890 / 10728, else 10505 "Purchase the selected item." with the callback.
  - hover `0x599930` -> `0x624320`: the row's tooltip (ITEMINFO lines 10352-10360, 10398, 10400, 10401) or
    AUCTIONKEYINFO 1350000 for cars.
- **Register** (inventory page `0x5d0490` -> AUCTIONITEM 1760000 `0x551600`, OK `0x5c9c00`) [exact]: not tradeable
  10445; car listing disabled by the server 10576; level 11828; the dialog: TEXT1 "Quantity" 10446 over COUNT (+/-),
  TEXT2 "Price [Max. 2 billion Mito]" 10447 over PRICE (max 0x77359400). OK: at most 60 listings (10440), quantity
  10441, price 10442, more than you own 10443, expired enhancers 11579, then the tax: 10% less the mounted licence's
  ME_AuctionFeeDis / ME_FeeDis percent (`10 - 10 * disc * 0.01`), 10444 "Registration Tax: %d Mito (%2.2f%%) The
  auction will expire if the item has not been sold after 7 days." Cars: 10572-10586, 10596, 10599, 10719, 10951
  (registered cars can't be driven, modified, resold or fitted; not the car you drive; no equipped parts; cars bought
  at the auction can't be listed again; 10% tax).
- **Inventory page INVEN 3090000** (`0x5d0a60`; INVENTORY.md): tabs See All / Selling / Sold with counts, five rows a
  view (icon, +N, name, price, date) and one button per row drawn as "Cancel Auction" (_C), "Expired" (_E) or "Sold"
  (_D) (ui_contents51), a scroll bar.
- Sounds: SID_AUCTION_ENTRANCE / REFRESH / APPLY / CANCEL.wav [exact names, uses inferred]. NPCs: globaltext
  B0..B2_IntroName / IntroText (Stacy, Eileen, Paul).

**Remake** (`Game/Auction/AuctionHouse.cs`, `AuctionMarket.cs`, `AuctionPage.cs`; server `src/auction.js`):
- The window as above (13 rows, columns under the header buttons, row stripes, NPC at the counter, double click or
  Purchase = 10505 / 10506 confirm). The search box matches the seller or the item name.
- Registering: drop a bag item on the inventory's Auction page (or a row) -> AUCTIONITEM with the shop value as the
  suggested price -> 10444 with the tax. Cars: the page's "Register a car..." link (remake: the remake's cars are not
  car-key items in the bag) with the 10577 / 10578 / 10586 rules.
- **Server rules** (`src/auction.js`): the server holds the goods and the Mito (escrow). Listing: level, 60 open
  listings, price 1..2e9, 10% tax less the licence discount, 7 days (`hours` 1..168). Buy-out: Mito 10500, own
  listing 10499, car class level 10580, garage 10582 / parts box 10269; the buyer pays, gets the goods at once; the
  seller gets the price by a note from "Auction House" with the Mito attached (and `auction.sold`). Bids (API only,
  the client's window has no bid control): a listing may carry an opening bid; a bid must beat the last by 5%, holds
  the bidder's Mito, an outbid / cancelled bidder gets it back by note (`auction.outbid`); at expiry the best bid wins
  ("won", the goods wait for the winner). Cancel returns the goods (the tax stays). Expired / cancelled-with-a-full-bag
  / won goods wait in the listing until `auction.take` (the page's "Expired" button). Credit = the seller's number of
  sales. NPC sellers (Stacy / Eileen / Paul) keep 12 listings of parts-shop goods up so an empty server still has an
  auction (`opts.auctionNpc`, env `DC_AUCTION_NPC`; 0 in tests).
- **Offline**: an AI market (`AuctionMarket.Offline`, `auction_<slot>.json` next to the profile): 36 listings from
  the game's tables (performance parts near the driver's level, parts-counter goods, chase materials, paints, cars
  from vehiclelist) at their shop value x 0.7-1.3 by the AI drivers of the Battle Zone; the player's listings are bought
  by an AI buyer after a delay that depends on price / value (<= 0.7: 1-5 min, <= 1.0: 5-45 min, <= 1.4: hours, above:
  never, it expires after 7 days); the Mito comes as an Auction House note (Mail, take it from the gift slot).

## 4. Server messages

| Message | Reply |
|---|---|
| `auction.list {vehicles, category, levelFrom, levelTo, seller, page, sort, desc}` | `{rows, page, pages, total, now}` |
| `auction.mine` | the driver's listings and waiting goods `{rows, open, max, now}` |
| `auction.register {item: {kind: stack|shop|part|car, id / itemId / uid / slot}, count, price, start?, hours?, name?}` | `{id, fee, name, driver}` |
| `auction.buy {id}` / `auction.bid {id, amount}` / `auction.cancel {id}` / `auction.take {id}` | `{ok, driver}` |
| push `auction.sold {id, name, price, buyer}`, `auction.outbid {id, name, refund}` | |
| `gift.send {to, item: {kind: vpart|shop, item|itemId, price, name}, comment}` | `{price, to, note, driver}`; push `gift.new` to the receiver |
| `gift.list` | `{pending (notes with items), sent, received}` |
| `gift.accept {id}` = `note.take {id}` | `{mito, item, itemName, driver}` |
| `shop.history` | `{bought, sent, received, mileage}` |
| `shop.mileage {slot, item}` | `{mileage, driver}` |
| `wish.set {items?, cars?}` | `{driver}` |

Notes (`src/mail.js`) gained an `item` column (JSON goods of `src/goods.js`): `deliver(..., item)`,
`system(account, text, mito, sender, item)`, `note.take` gives the item (room checks: garage 40, parts box 60, the
current car for a custom part), notes with items don't expire. Test env: `DC_AUCTION_NPC`, `DC_GIFT_LEVEL`.
Bot: `node tools/bot.js 1 --auction [max]` lists 3 Mittron Enhancers for 9,000 and buys real drivers' listings up to
`max`, taking the sale notes.

## 5. Not done / differences

- The client's Mail read panel (Social/Mail.cs, not in this module) shows only Mito attachments: notes carrying an item
  need the hook below to show / take it there; the custom shop's history (Get Gift) and RECVGIFT take gifts without it.
- G-Coin and the CC recharge are not rebuilt (no paid currency); the period prices are (section 2b). The dealer's car-bonus rows and the 10,000-40,000 promotion notice are hidden. The growth chart 3290000
  is not opened.
- The original server's real bidding rules are unknown (the window has no bid UI); the remake's bids are an API.
- The original's option / docking part categories are off (the flags were server feature bits; the remake has no such
  parts). Broken / expired parts don't exist in the remake.
