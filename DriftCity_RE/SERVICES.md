# The original server's remaining systems - DriftCity.exe

The systems HANDLERS.md still listed as missing that the remake now has, client and server: private channels and the
chat's Private mode, rental cars, driver rename, recommend-a-driver, crew-owned Mittron stations, the Channel Battle
leader / level and grade limits, the station's INSTANT button and the event delivery pair, the VIEWMAP city-page locks
and the personal shop; plus the decision on recycling and item-period extension.
[exact] = read from the code; [inferred] = from names / data; "remake rule" = the original server decided it.

Client: `Assets/DriftCity/Runtime/Game/Services/` (ServicesNet + ServerSwitches + ServicesHost, PrivateChannel,
RentalCars, DriverRename, Recommend, StationOwners, PersonalShop, WorldMapPages), edits in `Net/ChatBox.cs`,
`Game/Fuel/MittronCharger.cs`, `Game/StationPanel.cs`, `Game/Menus.cs` (WorldMap), `Game/Social/ChannelBattle.cs`,
`Game/PlayerProfile.cs` (fields). Server: `src/services.js` (one registration in server.js) with `src/switches.js`,
`src/privchan.js`, `src/rentals.js`, `src/drivers.js`, `src/stations.js`, `src/pshop.js`; `src/channelbattle.js` (leader,
limits). Tests: `test/services.test.js`, `Tests/ServicesTests.cs` (offline + online with a bot).

## 1. Server switches

The client showed or refused these features by bits of its server's config (net+0x244 .. +0x294, op 0x01e / 0x020) and
op 0x097; a refused feature answers 10745 "This feature is currently unavailable." [exact]. The remake's server answers
`features` -> `{ privateChat, rentals, instant, eventDelivery, recommend, personalShop }` (`src/switches.js`), set by opts
(tests) or env `DC_PRIVATE_CHAT`, `DC_RENTALS`, `DC_INSTANT_BUTTON`, `DC_EVENT_DELIVERY` (on / off / season, default season),
`DC_RECOMMEND`, `DC_PERSONAL_SHOP` (default on). The client (`ServerSwitches`) asks once per connection; offline: private
chat / recommend off (they need a server), INSTANT / rentals / personal shop on, the event pair with the delivery seasons.

## 2. Private channels and the Private chat mode [exact client]

- The chat input 0x5b6490 reads globaltext 11160-11165: `/cjoin <name>` -> 0x5bcd00 (refused with 11175 "You cannot be in
  multiple private channels." while ui+0x160+0x138 holds a channel), `/csay <text>` -> 0x5bcd40, `/ckick <name>` -> 0x5bd190,
  `/cauth <name>` -> 0x5bd1f0 (both skip your own name), `/cwho` -> 0x5bcd90, `/cleave` -> 0x5bcd20 (the last two must be
  the whole line); without a channel each answers 11168 "Not in a private channel." (colour 0xffaaaaff). `-<channel> text`
  (case '-') sends to that channel; mode 6 ("private", 0x5bcf60) sends to the current one.
- Replies: 0x08f `{chan[10 wchar], name}` sets the channel, 11166 (self, arg = channel) / 11169 "%s has entered the private
  channel %s."; 0x091 11167 / 11170 (self: the channel is cleared); 0xa29 kicked 11174 / 11173; 0xa2b admin 11177 / 11178
  (by a flag at +2; the texts are equal); 0xa2d the names one per line, then 11171 "Total of %d users are in the private
  channel %s.". 0x08c / 0x08d (`<OpenChatPkt>` / `<CloseChatPkt>`) only print debug lines in state 0x4c: n/a.
- Remake rules (`src/privchan.js`): pchan.join / say / leave / kick / auth / who / state; a channel name is 1-9 letters,
  digits, _ -; the first /cjoin makes it (admin), 30 members, the admin's leaving hands it to the longest member, only the
  admin kicks / hands over (11176), server-wide (any city or channel), the session's end leaves (pruned every 5 s); lines
  are `chat {scope: 'private', from, text, channel}` (8 per 10 s, muted -> 20102). Switch off -> 10745.
- Client (`PrivateChannel.TryHandle` from ChatBox.Submit; pushes `pchan`): the commands, `-chan`, the Private mode (now
  allowed with the switch), the lines in colour #aaaaff under the PRIVATE tab.

## 3. Rental cars

- Data [exact]: visualitem.xlt type 30 "RENTCAR" (50 rows): value `"<car unique id> <V level> <kind> [set ids]"`, kind 1 =
  time-limited (pc_rent_*, "Rent a V9 Comet for 168 hours. Time not stackable, buying again will give you a new car!"), 2 =
  mileage-limited (pc_rent0_*, "until you run out of Mittron Fuel"); vshopitem sells them (Support 1, $600 7 days / $1800
  30 days, G-Coin); vshopcategory Top 4 TOP_RENT, the VSHOP 2450000 RENT tabs SPD / ACC / COL / BST / ETC / SPECIAL, which
  0x6611f0 / 0x663600 serve only with net+0x294 & 0x3c00000 (no RENT top tab exists in 2360000 / 2370000 of this build).
- Texts [exact]: 10950 / 10951 / 10952 no dealer sale / auction / upgrade, 10953 / 10958 a mileage-limited car is never
  refilled, 10959 a time-limited one refills, 10956 (rentals alone in the cart). Op 0x06d (0x502850): the car leaves the
  list; when the returned car equals char+0xc1 -> POPUP 10969, else 10968 with the OK callback 0x5027c0 (the garage); both
  also as a chat line 0xfff29177. The remake shows 10968 (moved to the garage = the parking lot) for the car being driven and
  10969 otherwise [inferred meaning of +0xc1].
- Remake rules (`src/rentals.js`, `RentalCars`): no G-Coin -> dollar price x 1000 Mito (600,000 / 1,800,000); each rental is
  a new car (Grade = V level - 1, `RentKind`, `RentUntil`, `RentItem`, `RentName`), the current car; both kinds end at
  RentUntil; a mileage-limited one also when its tank is dry (`rental.return {reason: 'fuel'}`) and the filling station
  refuses it (10953; station.fill too). Swept on rental.* and every 30 s (push `rental.returned`); offline every 5 s. The
  rental set parts (r_s01 ...) are not fitted. Shop: a list of the RENT categories, then the cars with 7 / 30-day rows.

## 4. Driver rename [exact client]

- i_changenick, visualitem type 36 "ChangeNick", vshopitem 10510 (TOP_ETC / EXTEND / CHANGENICK_SUB, 3,500,000 Mito, 7 D,
  "Unchangeable for 60 days after a change"); using it (0x5d52d0 item kind 0x24) shows NICNAME 3770000 (ui+0x3cc, ctor
  0x543110): EDIT (160 px input), NEW_NAMECONFIRM (check: 10604 / 10605), ACCEPT, CLOSE; op 0x06b: 10954 / 10955 or 10960.
- Remake rules (`src/drivers.js`, `DriverRename`): rename.check (driver-name rules 2-16 letters, digits, _ - else 11296;
  taken 10604, free 10605), rename.apply (needs the kit, spends it; 60 days between changes `NameChangedAt`; the database row,
  the session and the profile change at once: 10955, no relogin), rename.buyKit (3,500,000 Mito, a 7-day bag item: the custom
  shop's Etc tab is not rebuilt). ACCEPT is enabled only for the name that passed the check.

## 5. Recommend-a-driver [exact client]

- Op 0x097 sets net+0x3b518; on a stage change into a city with net+0x3b500 == 1 (0x74ca28, 0x76dbf9) the client opens MSGBOX
  3230000 via 0x554240: INFO 10181 "Name of the recommended character.", OK sends the name; op 0x099 `{int}`: 1 -> POPUP 10042
  "Recommended!", the MSGBOX closes and the flag clears; else POPUP 10043 (empty) and the flag stays.
- Remake rules: recommend.state `ask` = switch on, `RecommendDone` false, level <= 10; asked on the first city of a session;
  recommend.send: an existing driver of another account at level >= 10; the named driver gets a note with 10,000 Mito; a
  refusal shows the server's reason and asks again; Cancel = next session.

## 6. Crew-owned Mittron stations, Channel Battle fees / leader / limits

- [exact] CHARGER's prices: op 0x2d4 (0x4ff020 -> 0x54fec0): list price +0x1c, sale +0x20, capacity +0x24, "1L / %u Mito";
  OWNTEAM: `0x476630(net+0x3b4e8) - 1` (0..23) indexes TC's channel records (+0xc0 + 0x75 * i, crew name +0x1d / mark) ->
  10168 "Owned by: %s Crew" else 10169. Fees to the owner Master: server texts 20268 / 20288; replay fees 20350 (already in
  the remake).
- Remake rules (`src/stations.js`, `StationOwners`): the owner = the crew owning the battle channel whose key is the world
  channel (`<city>-<n>`); 10 Mito a litre everywhere; 20 % of each fill at an owned station into `station_fees`, paid hourly by
  note to the owner Master (20268 / 20288 text; a vacant channel keeps its pool); station.fill replaces fuel.buy for the
  station (fuel.use stays for CHARGERPVP). Offline the AI crews of the offline table own their channels' stations.
- Channel limits [exact 0x675250]: ENTERCHANNEL off when level > the entry's max level byte, car grade > max grade byte (0 =
  none), grade < `MinChBattleGrade` / `MinSemi%dChBattleGrade` (US 0); 11288 "Only V6 or high vehicle can enter Channel
  Battle" exists. Remake rule (`LIMITS`): semi channels max level 60 / max grade V6, majors open; crew.table sends
  maxLevel / maxGrade / minGrade; crew.enter refuses (code 'limit'); the client greys ENTERCHANNEL and refuses offline.
- Battle leader (0x35d field, CHANNELINFO TEAMBATTLELEADER): remake rule - the capturing crew's best-placed racer of the final
  round, or the holder's racer when a vacant channel is held at the window's end (`crew_channels.leader`, crew.table `leader`);
  the pane shows it (the Master before any capture).

## 7. Personal shop (USERINFO2ND BUY_GOODS)

- [exact] 1830022 ID_USERINFO2ND_BUY_GOODS is named "착용물품구매" (buy the goods being worn); the card handler 0x54d0b0 has no
  case for it: the click does nothing in this build.
- Remake rule [inferred from the name] (`src/pshop.js`, `PersonalShop`): pshop.list {name} lists the custom parts fitted on that
  driver's current car with `fits` (your current car), `owned` and the shop's periods; a row is bought with shop.buyPeriod
  (ShopPeriods.Buy) for your current car. Nothing passes between drivers. Offline the AI drivers wear no parts.

## 8. The station's INSTANT button and the event delivery pair [exact client, MISSIONS.md 14.2]

- 0x562030: with no offered quest and net+0x24c & 0x100000, INSTANT (400005, QUEST's art) replaces QUEST for OMD drivers,
  enabled with no instant mission in slot 0, else disabled with ING_QUEST; it asks 0x3de (an instant mission).
- 0x5625e0: EVENTARBEIT (OMD) / EVENTTIMEATTACK (ROO) replace ARBEIT / TIMEATTACK with net+0x290 & 0x20000.
- Remake: switches `instant` / `eventDelivery`; INSTANT calls `InstantMissions.Offer()` (a mission of the driver's level, the
  Call Mission 860000 offer); the event buttons open the same Delivery Service board.

## 9. VIEWMAP city pages [exact 0x5f5c40 / 0x5f3170]

Page 1..5 = Moon Palace 480000, Koinonia 490000, Cras 500000, Oros 510000, Taipei 530000; RIGHT checks the next city's quest
flag (+0x10b..+0x10e): locked -> sound 0x4e8b (the remake plays SID_CLICK_NO), stays; past 5 the same; LEFT down to page 1 without a check;
arrows enabled accordingly (stage 0x13 all on). Remake: `Menus.WorldMapPage(city)` + `WorldMapPages` with
`QuestUnlocks.CityOpen`; the player marker only on the page of the city being driven.

## 10. Recycling and item-period extension: not needed

- Recycling [exact]: item kind 0x27 (useitemclient item_recycle i_n_00114, vshopitem i_itemrecycle_10 "A kit you need in
  order to recycle an expired coined Drift Item"), gated by globalcontent ItemRecycle (1); op 0x89 subs 16 / 17 show 11294.
  It recycles expired **G-Coin** items. The remake sells nothing for G-Coin (Mito and mileage only), so no coined item can
  expire: there is nothing to recycle. Not built.
- Period extension [exact]: op 0x89 sub 31, 11716 "You've obtained %s (%d Days) Would you like to extend the items expiry
  date?" leads to the billing page; 11887 "Extend %s for %d day period.". The remake's period goods already extend by buying
  again (ShopPeriods / ItemPeriods: 11044 "Usage period will be added to the same item."), so the question has nothing to add.
  Not built.

## 11. Hook lines for files outside this round's ownership

| file | line |
|---|---|
| `Game/Inventory/ItemUse.cs` (use from the bag) | `if (id == Services.DriverRename.Kit) { Services.DriverRename.Open(); return; }` (0x5d52d0 case 0x24) |
| `Game/DriverInfoScreen.cs` (USERINFO2ND) | `ui.On("ID_USERINFO2ND_BUY_GOODS", _ => Services.PersonalShop.Open(Shown.Name));` and enable it (`SetEnabled(..., Services.ServerSwitches.PersonalShop)`) instead of disabling it |
| `Game/CustomShop.cs` | an entry for the rental cars and the kit: e.g. the Etc tab's EXTEND -> `Services.DriverRename.BuyKit()`; a RENT entry -> `Services.RentalCars.Open()` (the build has no RENT top tab) |
| `Game/Garage*.cs` / inventory car info | show `ID_GARAGE_SELCAR_RENT` / `ID_GARAGE_GETON_PANEL_RENT` / `ID_INVEN_CARINFO_RENT` while `Services.RentalCars.IsRental(car)` (text `RentalCars.TimeLeft(car)`) |
| dealer sell / upgrade, auction car listing (client) | `var no = Services.RentalCars.SellRefusal(car) / UpgradeRefusal(car) / AuctionRefusal(car); if (no != null) { UiPopup.Message(no); return; }` |
| server `src/economy.js` sellCar / upgrade | `const no = require('./rentals').refusal(p.Cars[m.slot | 0], 'sell' /* 'upgrade' */); if (no) return no;` |
| server `src/auction.js` / `src/goods.js` (car goods) | `require('./rentals').refusal(car, 'auction')` before a car leaves the driver |
| server `src/economy.js` buyCar | `p.Cars.some(c => c.VehicleIndex === vehicleIndex && !(c.RentKind > 0))` so a rental doesn't block buying the car |

## 12. Tests and what is left

- `npm test` (125 pass; `test/services.test.js` 7: switches, private channels, rentals, rename, recommend, stations + channel
  limits / leader, personal shop). PlayMode `DriftCity.Tests.Services` (ServicesTests offline in Cras, ServicesOnlineTests
  against the local server + bot): 2 / 2 pass; screenshots Preview/services_*.png (charger owned by an AI crew, rental list,
  rental returned, NICNAME, VIEWMAP Koinonia page, recommend MSGBOX, private chat lines).
- Left: the hook lines of section 11 (bag use of the kit, BUY_GOODS, shop entries, garage RENT marks, server refusals);
  NICNAME's two buttons draw as plain grey boxes in the capture (their disabled art; worth a dialog preview); the rental set
  parts; a RENT top tab (none in this build's VSHOP).
