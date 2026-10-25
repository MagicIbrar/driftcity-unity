# Data tables: what DriftCity.exe loads and what the remake reads

Round "Data tables" (CODEMAP subsystem, 98 KB). Sources: the table names among the strings of `codemap/funcs.json` /
`text.asm`, the archive listing (Harness `files`), the loaders' disassembly, and a grep of the Unity project
(`DriftCityOffline/Assets/DriftCity`) and the server (`DriftCityServer/src`) for each file name. [exact] = read from the
code or data, [inferred] = from names / layout, **remake** = the remake's own rule.

## 1. How the client loads tables [exact]

- `0x4d2470` (game start) mounts `../Data/Init.agt`, `World.agt`, `game.agt`, `effects.agt`, `vehicles.agt`,
  `gameui/uitex.agt` and walks them for `*.xlt` and `*.tdf`.
- The xlt manager: `0x7e1310` opens a table by name (the usage text at 0x7e1310 says "Test.xlt -> Test (No .xlt!!!)",
  so callers pass `Init/VehicleList` without the extension; `0x7e1080` tries `.xlt` then `.txt`); `0x7e09f0` reads the row
  count, `0x7e0980` skips header rows, `0x7e1860` reads one row, `0x7e1600` closes. A loader that cannot open its table
  logs `"<name> - Open FAILED!!!"` (0x402c40) and goes on with an empty table.
- Generic row reader `0x7e1a40(name, sink)`: opens, then hands every row to `sink->vtable[1]`; its error text says
  `Init/PvpAiInfo` because it was written for that table (no `pvpaiinfo.xlt` ships; `Init/AICarParam` uses it).
- The `Init/*` loaders run from the game's init `0x43a900` (calls at 0x43a1f7..0x43a2a6: 0x4b4b10 colours, 0x452fa0
  BossHuvInfo, 0x4ac5e0 UCHuvParam, 0x62f880 Coupon, 0x7deea0 VShopCategory, 0x7df090 VShopItem, 0x7de470 License,
  0x7dddc0 License_GC, 0x7de120 License_ME, 0x7d7990 PVPTrackInfo, 0x7d4dd0 Emoticon, 0x7d49a0 AutoFinder, 0x7d4360 env,
  0x7d3cf0 TokenBook); the vehicle tables come from the vehicle manager (0x4bdae0..0x4be670), the `World/Data/*.tdf`
  from the mission managers (0x7093c0 and friends).
- `.tdf` = a BMP thumbnail followed by the table (header at the BMP size; `CityMapInfo.TdfRows` reads them). Most
  `World/Data/*` tables also ship an older copy under `data\` (the remake falls back to it, `MissionTables.Read`).
- Localize: `0x4d0e20` reads `./Localize/hq_localize` (the section -> file index), `hq_globaltext`, `hq_globalContent`,
  `hq_localizehelp`; GlobalText / GlobalContent lookups 0x7d2670..0x7d2e70 / 0x7d27b0; help pages `H%d` (0x543c00,
  0x543de0) and `HelpIndex` (0x5448f0). Options: `option\hq.xlt` indexes the option sections (Log, Video, Sound,
  EventPopup, FtpMiniDump, IEMinimize).

## 2. The tables

"Remake" = where the Unity client (U) or the server (S) reads it. Paths are under `DriftCityOffline/Assets/DriftCity/`
(Runtime/Game unless given) and `DriftCityServer/src/`.

### Init (init\*.xlt)

| table | loader | holds | remake |
|---|---|---|---|
| vehiclelist | 0x4be510 | every car: player cars (ratings, size, tyres, default colours, camera), traffic, HUV, HUV boss rows | U `GameTables.Load` / `TrafficWheels` / `TireNif`, `VehicleColors`, `Shops/AdvancedDealer`, `StepQuestRunner`, `Tutorial/ApprenticeCourse`; S `gamedata.js` |
| vehicleupgrade | 0x4bdbc0 | grade rows per car (V1.. ratings, price, upgrade Mito, efficiency, capacity, level) | U `ShopData`; S `gamedata.js` |
| vehicledisplay | 0x4bdae0 | dealer list: car, UI name, New / Popular / Duffy's flags | U `Shops/AdvancedDealer` |
| upgradecondlist | 0x4bdd60 | 717 upgrade conditions (Quick_*, HUVKill_*, Distance, hits, battle counts) named by vehicleupgrade `UpgradeCon` | not read: every vehicleupgrade row has `UpgradeCon = n/a`, so no condition is in use |
| tirelist | 0x4b48e0 | wheel models: id, file, diameter / width (radius = Diameter x 0.0005) | U `GameTables.Tire` / `TireNif` |
| spoilerlist | 0x4b4a10 | 74 spoilers: id, name, listing, group, animation, type, icon, New | U `PartsCatalog` |
| airintakelist | 0x4b4710 | 5 air intakes: id, group, animation flag | not read: the custom shop's ducts come from vshopitem (`PartsCatalog`, `vehicle\airintake\*.nif`); group / anim are not used |
| trafficflowcontrol | 0x4be670 | per city: traffic models, spawn weight, cars at once | U `GameTables.TrafficFor` |
| aicarparam | 0x49dfe0 (rows via 0x7e1a40, 0xc4-byte records at AI manager +0x60) | 220 Battle Zone AI drivers: name "[AI] Elio", AI level 1..99, the cars' Unique Ids, speeds (km/h), cornering, boost numbers | **now read** (this round): U `GameTables.AiDrivers` / `AiCars` -> `Pvp.NewAi` (PvpRoom.cs); S `gamedata.aiDrivers` -> `race.js addAi`. Driving numbers not used (the remake's AI takes a skill) |
| bosshuvinfo | 0x452fa0 | 25 boss HUVs: id, string id, speed (km/h), damage / escape settings, rewards | U `HuvRoutes`, `ChaseMission` |
| chasespawn | 0x4b86a0 | 174 spawn presets with a count per city (1st..4th) | U `ChaseSpawns` |
| uchuvparam | 0x4ac5e0 | 34 Undercity waves: city, stage, HUV id, level, count | U `UndercityData`, `UndercityStage` |
| pvptrackinfo | 0x7d7990 (name at 0x439b40) | Battle Zone tracks: gates, start / end, bounds, laps, traffic preset, AI flag, mission flags | U `RaceMission`; S `pvptracks.js`, `race.js` |
| emoticon | 0x7d4dd0 | "/command" -> ch_e_00NN emoticon | U `PvpRoom` (Pvp emoticons), `PvpChat`; S `race.js` |
| rewardgroup | 0x7e4770 (also 0x7093c0) | 3132 reward rows by group: type, item, period, count, probability (PvP cards, gacha, events, quests) | U `Core/RewardGroups`, `PvpRoom`, `PvpRewardPick`, `Tournament`, `Events/EventData`, `Gacha`, `Harvest`; S `events.js`, `pvptour.js`, `pvptracks.js` |
| motherquest | 0x7e3b40 (MotherQuest.xlt also 0x7093c0) | 38 main quests: level, conditions, child quests, posts, scripts, rewards | U `QuestTracker`, `Core/GameScripts`; S `gamedata.js` |
| license | 0x7de470 | licences: name, info, conditions GC_*, mount effect ME_* and value | U `Workshop` (`Licences`), `Inventory/LicenceInfo`, `DriverInfoScreen`; S `gamedata.js`, `workshop.js` |
| license_gc | 0x7dddc0 | 203 gain-condition ids: name, InstantCheck, description, condition format ("Amateur Arena Ladder Points %d%%") | not read: license.xlt's own texts are shown; which GC ids count is the remake's map (`Licences.MapCondition`) |
| license_me | 0x7de120 | 57 mount-effect ids with descriptions | not read (license.xlt `MountEffectText` is shown; the ids drive `Licences.Bonus`) |
| alchemy_levelmodify | 0x6c8a30 | 4 manufacturing levels: EXP_MAX, level | U `Workshop`; S `gamedata.js`, `workshop.js` |
| alchemy_skillbook | 0x6c8a30 | 32 skill books (recipes): level, ingredients, kit, result, assist ranges, success chance | U `Workshop`; S `gamedata.js` |
| coupon | 0x62f6e0 (from 0x62f880) | the coupon book (37 rows) | U `Events/CouponBook` (+ EventObjects, LuckyBag, InventoryWindow, AdvancedDealer); S `events.js` |
| tokenbook | 0x7d3be0 (from 0x7d3cf0) | 10 token exchanges: what is handed in, up to 4 gifts | U `Events/TokenBook`, `EventData`, `Inventory/ItemUse`; S `events.js` |
| visualitem | 0x7e2430 / 0x71b6b0 | custom-shop part availability per car (1593 parts x car ids) | U `PartsCatalog`; S `gamedata.js` |
| vshopitem | 0x7df090 | 1453 custom-shop products: unique id, item name, categories, VisualItem ref, stages, prices | U `PartsCatalog`, `ShopData`, `Inventory/ItemInfo`; S `gamedata.js` |
| vshopcategory | 0x7deea0 | 106 shop categories: top / main / sub ids and names, menu enable, multiple selection, stackable, Coin / Mito gift | **now read**: U `Shops/VShopCategories` -> `CustomShop` (top tabs by Top Menu Enable, sub tabs, the list header's sub category name; Multiple / Stackable API), AUDIO.md 7; gifting flags not used |
| env | 0x7d4360 | 155 positional ambient sounds: SID, wav, city, point (Gamebryo x y z), radius min / max, type, interval | **now read**: U `Audio/CityAmbience` (`EnvTable`, the four sound types of 0x51f400), AUDIO.md 1 |
| autofinder | 0x7d49a0 | 59 anti-macro quiz questions (question, 4 answers, the right one) | not read: AUTOFINDER 3420000 is the server's anti-macro quiz, not rebuilt (CHASES.md 6) |
| operatormisrepresent | 0x7e0340 | 201 strings a name may not contain (ADM, DEV, GM...) | **now read**: U `NameFilter` (driver names 10606, crew names 10607, room titles 10608, chat censor); S `namefilter.js` (driver.create, race.host, chat, crews), AUDIO.md 6 |
| slang | 0x7e0200 (called inside 0x7e0340) | 6 text smileys (=w=, =) ...) of the same filter | **now read** with operatormisrepresent (same filter) |

### World/Data (*.tdf)

| table | loader | holds | remake |
|---|---|---|---|
| QuestClient | 0x7093c0 | step quests (kinds, givers, steps, rewards, ending markup) | U `StepQuests`, `StepQuestRunner`, `QuestTracker`, `StationPanel`, `MissionCommon`, `EndingCredits`, `PlayerProfile`; S `quests.js`, `economy.js` |
| ArbeitClient | 0x7093c0 (rows 0x70a361) | delivery jobs | U `DeliveryService`; S `economy.js` |
| Apprentice_Client | 0x7093c0 | apprentice course | U `Tutorial/ApprenticeCourse` |
| Icon_<city>_Client (5) | 0x7093c0 / 0x709440 | map icons, posts, event posts | U `CityMapInfo`, `MissionCommon`, `Events/EventObjects` |
| RQHouseMent_<city>_Client (4) | 0x7093c0 | quest-house staff lines (markup) | not read (MINIMAP.md 101: same container; the remake's quest houses use QuestClient texts) |
| StartMent_Client | 0x7093c0 | start-up NPC lines | not read |
| Tutorial | 0x706ba0 (into manager +0xcc) | Instructor Duffy's Battle Zone gate lines | not read: only `data\tutorial.tdf` ships, the world path is missing and nothing reads +0xcc (MISSIONS.md, TUTORIAL.md) |
| HuvTable | 0x6cc560 | HUV mission table | not read (chases use vehiclelist HUV rows + bosshuvinfo + chasespawn) |
| Instant_Client | 0x6d1200 | call missions and patrols | U `InstantMissions`, `PatrolMission`; S `economy.js` |
| InstantDrop_Client | 0x6d0040 | call-mission drops | not read |
| ItemClient | 0x6e5720 | parts table (set / rental parts) | U `Inventory/SetParts`, `ItemPeriods`; S `items.js` |
| UseItemClient | 0x6e5720 | consumables | U `Events/EventData`; S `events.js` |
| Assist_Client | 0x6e5720 | assist items | not read |
| PartModify_Client | 0x6e5720 | part remodelling | not read (remodelling greyed out, SHOPS.md) |
| ItemUpgradeClient | 0x6e2990 | item upgrade (enhance) table | not read (`Workshop` enhance uses its own rule) |

### Others

| table | loader | holds | remake |
|---|---|---|---|
| effects\realmatch\visual.xlt | 0x70de00 (0x70dcf0 tries the misspelt `cts/realmatch/visual.xlt`) | 16 Real Match looks: Color, Decal, DecalColor, Neon | U `Pvp.RealMatchLookFor` -> `CarPaint.ApplyRealMatch`, `RealMatchSkin` (this round, PVP.md 14.4) |
| <city>\<city>.xlt (cras, koinonia, moonpalace / moon, oros / or, taipei) | world loader (`modeltable0.lof` 0x40d550) | map object list: model id, category, NIF, price | U `CityLoader` (fallback when `modeltable.nxt` is missing), `GameEffects` |
| localize\globaltext, globaltext_server, globalcontent | 0x4d0e20, 0x7d2670.. | texts / content switches | U `GameTables.Text`, `MissionCommon`, `UndercityData`, `ItemInfo`, `SocialCore`, `ServerNotices`; globalcontent `PvpRoom`, `ShopExtras`, `InstantMissions`, `ApprenticeCourse` |
| localize\helpindex, h1..h21 | 0x5448f0, 0x543c00 | help window chapters | U `HelpWindow` |
| localize\<screen>.xlt (agentname, autoshop, cargo, charcreate, charger, charsel, chasing, chat, common, etc, exchange, friendlist, game, inventory, login, parkinglot, partsshop, party, pvp, racingteam, rqhouse, teamcenter, visualshop) and hq_* | 0x4d0e20 via hq_localize | Korean-only tooltip / popup strings (Key, Value KR, Comment) | not read: KR only; the US client's texts are in globaltext |
| option\*.xlt (hq, log, sound, video, eventpopup, ftpminidump, ieminimize) | option index, EnableEventPopup 0x4d4f60 | developer / launcher switches | not read (the remake keeps its options in goption.ini, OPTIONS.md) |

Non-table data files the same init reads (for completeness): `init\camera.ini`, `vshop_camera.ini` (0x43a900),
`camera_gi.dat` (0x4da990, U `PvpCameras`), `def_option / goption / ndev_option.ini` (0x4c9440.., U Options), `flare.ini`
(0x401130, U `LensFlare`), `weather.ini` / `light.ini` (U `OriginalLook`), `<city>\main\shadow.dat` (0x40c410, U
`CityLoader`), `Vehicle/LevelModifier.dat` (0x47bbb0, U `DriftCar`), `World/Data/pvptrafficnum.dat` (0x73aaa0, U
`PvpTraffic`), `Game\credits\Credits*.dat` (0x4df290 / 0x4df7e0, U `EndingCredits`), `music.txt` (0x522a90, U
`CarAudio`), `effects\ShaderConstants.dat` (0x42a540, not read: the remake's shaders carry the constants).

## 3. Switched to the table this round

- **AI drivers** (`init\aicarparam.xlt`): the Battle Zone AI drivers had invented names ("Rio", "Duke"...) and a random
  car up to 8 levels above. Now an AI driver is a row of the client's table whose AI level is nearest the player's
  (within 3, or the nearest available), with its name ("[AI] Rapunzel") and one of its listed cars that is a player car
  here (Unique Ids of 10000+ are event / traffic models and are skipped; rows without a usable car are skipped: 199 of 220
  remain). Level = the row's AI level. Offline `Pvp.NewAi` (PvpRoom.cs), online `race.js addAi` with
  `gamedata.aiDrivers`; both fall back to the old names without the table. Which AI the original server put in a room
  was the server's: **remake** rule "nearest AI level". The tournament (pvptour.js) and Undercity partners keep their
  own names.

## 4. Gaps (not switched; larger than a table switch)

- Done since (AUDIO.md): `env.xlt` (the ambience player, consumer 0x51f400 traced), `operatormisrepresent` + `slang` (the
  word filter, client and server), `vshopcategory` (the custom shop's tabs; gift flags unused).
- `license_gc` / `license_me` texts: the licence screens show license.xlt's own texts already.
- Not read because the feature is not rebuilt: autofinder, upgradecondlist (unused by the data), Tutorial.tdf, the
  mission tdfs InstantDrop / Assist / PartModify / ItemUpgrade / HuvTable / StartMent / RQHouseMent.

## 5. Map overrides

`classify.py` (scratchpad map) gets the loaders that belong to a subsystem: the vehicle tables to Vehicle visuals &
parts / Car dealer, the HUV tables to HUV chases / Undercity, licences / alchemy to Parts shop, coupon / tokenbook to
Events, emoticon / pvptrackinfo / aicarparam to PvP, motherquest / rewardgroup to Missions, visual.xlt to PvP, env to
Audio, autofinder to HUV chases (the AUTOFINDER screen sits with Auto-Drive), the name filter to Chat & messages; the
generic xlt manager (0x7e0980..0x7e1a40) stays Data tables.
