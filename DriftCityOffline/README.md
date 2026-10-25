# Drift City Offline (Unity)

A rebuild of Drift City built from **your own copy of the client's data**, offline or on your own
multiplayer server. Models, textures, UI layout, sounds and tables come either from the installed game
folder or from an imported copy inside the project (see [Game data](#game-data)).

Offline, everything is handled locally (profile, money, cars, quests, races, chases). Online, the
**Drift City Offline server** (`../DriftCityServer`, Node.js) holds accounts and drivers. It runs city
channels, chat, parties and PvP races, and it checks rewards and purchases. It uses its own
JSON/WebSocket protocol made for this rebuild. It is not the original game server and does not talk
to the original `DriftCity.exe`.

## Play

**Standalone build:** run `Build\DriftCityOffline\DriftCityOffline.exe`. With imported data, the build
carries everything in `DriftCityOffline_Data\StreamingAssets\DriftCityData` and needs no installation.
Without it, `gameroot.txt` next to the exe holds the game folder (the one with `DriftCity.exe` and
`Data`). You can also start with `-gameRoot "C:\path\to\Drift City"`.

## Game data

**Drift City → Import Game Data Into Project** extracts the installed client's archives into
`DriftCityOffline/GameData`. The archives are decrypted and decompressed, patches are applied in
order, and the loose UI, table and audio files are copied. Batch mode: `-executeMethod
DriftCity.EditorTools.DriftCityImport.ImportBatch [-importSource <path>]`. The result is about 11,150
plain files (NIF, DDS, XLT, WAV, MP3...), roughly 3.9 GB, and the import takes under a minute.

- The editor and tests prefer `GameData` when it exists, and every build copies it into the player, so
  neither needs the installation any more. Delete `GameData` to go back to reading the install.
- It sits next to `Assets/` rather than inside it on purpose. Unity can't open these formats, and
  ~11,000 extra assets would only slow the editor down.
- The copy contains the original game's own files. It is ignored by git and must never be committed or
  shared; everyone imports from their own installation.

## HD textures

- **Drift City → Textures → Enhance All Textures (HD)** upscales every texture in `GameData`:
  - Textures of 128 px or less grow ×4; larger ones grow ×2, up to 1024 px. Textures at 1024 px or more are left alone.
  - It uses premultiplied-alpha Lanczos-3 resampling and a light unsharp mask.
  - The results are DXT1/DXT5 DDS files with mipmaps in `GameData/Enhanced`, so they load as fast as the originals.
  - Batch mode: `-executeMethod DriftCity.EditorTools.DriftCityTextures.EnhanceBatch [-maxSize 1024] [-only vehicle/player]`.
- **Your own versions**: put a PNG at `GameData/Textures/Edit/<same path>.png`, for example
  `GameData/Textures/Edit/vehicle/player/wheel/tire_ply0005.png`, and run Enhance again. That image becomes
  the source for this texture: hand-painted, or upscaled with an AI tool such as Real-ESRGAN. Images
  already larger than the target are kept at their size.
- **Drift City → Textures → Export Originals as PNG** writes every original as an upright PNG to
  `GameData/Textures/Original`, as a starting point. **Remove HD Textures** deletes `GameData/Enhanced`.
- **In game:** Esc → Options → *HD textures* switches them; this takes effect at the next city load or travel.
  Every texture also uses trilinear filtering and 8× anisotropic filtering. Builds carry `Enhanced` along with the rest of `GameData`.

## The original look

The client's own shaders are ported from the game data (`shader\*.fx`, copies in `DriftCity_RE\shaders`):

- **Cars** (`Shaders/AGCar.shader`, the `ActionGameCartoonFX` meshes): the paint shows where the texture's alpha is
  clear, half-Lambert shading from the car light, a phong highlight, the vehicle cube map
  (`vehicle\texture\left/right/front/back/up/down.dds`) at the weather's `envmapstrength`, the black `OutlineMesh`
  hull and the additive gloss strips (`SpecularBand.shader`).
- **City**: `ActionGameTree` buildings (`AGTree.shader`), `VCAlphaTextureBlender` terrain, the toon-ramp lamps
  (`DCToon.shader`) and Gamebryo's fixed-function lighting for everything else (`NiStandard.shader`).
- **Lights, fog, sky** from `init\weather.ini` (the first weather, "cloud", as the client starts) and
  `init\light.ini` for the garage and shop screens (`Game/OriginalLook.cs`): a ground light for the world, a car
  light for vehicles, fog over the chase camera's 512 m view, the weather's sky dome and the lens flare
  (`init\flare.ini`).
- **Animated NIFs**: transform, alpha, visibility and texture flip-book controllers play (`NifAnimator.cs`), so
  booster flames, lamps and animated city props move as they do in the client.
- **Shops and garage** in their original 3D scenes: the dealer's `autoshop\autoshop.nif` showroom and the garage's
  `garage\garage.nif`, seen from the `init\camera.ini` cameras; the custom shop uses `init\vshop_camera.ini`.

## Arcade look

Off by default; switch it in Esc → Options. It adds on top of the original look:

- **Post effect** (`Shaders/ArcadePost.shader`): ink outlines on the city from depth and normal edges,
  stronger saturation, a soft highlight roll-off and a light vignette.
- **Speed lines and a wider field of view** at high speed and while boosting.
- **A cartoon sky** with flat clouds and a hard-edged sun (`Shaders/ToonSky.shader`).

**In the Unity Editor (6000.4.2f1):** open `Assets/Scenes/DriftCityGame.unity` and press Play.
**Drift City → Build Windows Player** rebuilds the exe.

Flow: server list → login → operator intro → car creation → loading → open city.
A returning driver goes straight to the city they were last in.

- **Offline**: the login takes just a driver name. The save is
  `%USERPROFILE%\AppData\LocalLow\DriftCityOffline\Drift City Offline\driftcity_offline_profile.json`.
- **Online**: the login takes an account name and password. An unknown name offers to create the account,
  and the driver lives on the server.

## Multiplayer

1. Install [Node.js](https://nodejs.org) 22.5 or newer.
2. In `DriftCityServer`, run `npm install` once, then `start-server.bat` (or `npm start`).
   It listens on port 7860 and stores everything in `DriftCityServer\data\driftcity.db`.
3. Start the game, enter an account name and password on the login screen, then pick **This PC** (or your own
   server) on the server list. An unknown account name offers to create the account.

To play with friends, run the server on one PC and open TCP port 7860 (firewall or router). Then add
the server to `servers.txt` next to `DriftCityOffline.exe`, one `Name|ws://host:7860` per line, or start
the game with `-server ws://host:7860`.

Server settings (environment variables): `DC_PORT`, `DC_DB`, `DC_NAME`, `DC_MOTD`.
`http://host:7860/status` shows who is online. Type these commands in the server window:

| Command | Effect |
|---|---|
| `players`, `channels` | who is online, channel load |
| `say <text>`, `notice <text>` | notice to everyone (chat line and the centre-screen EMERGENCY notice) |
| `bignotice <text>`, `scroll <text>`, `gm <text>` | large centre notice, the scrolling marquee, a GM-coloured chat line |
| `popup [@driver] <text>`, `event <title> \| <text>` | a message box for everyone (or one driver), an event popup |
| `maint <minutes> [text]`, `maint off` | maintenance: counts down with notices, then disconnects everyone but admins and refuses logins until switched off |
| `kick <driver>`, `mute <driver>` | remove or silence a driver |
| `ban <account>` / `unban <account>` | block an account |
| `admin <account>` | make an account an admin; admins can use these commands as `/kick ...` etc. in chat |
| `givemito <driver> <n>`, `setlevel <driver> <n>` | edit a driver |

**Test bots:** `node tools/bot.js 3` logs in three bots that join Cras and drive circles around you.
They answer whispers and accept party invites. Use `--city`, `--at x,y,z` and `--url` for other places. Test modes: `--race N` (Battle Zone guest), `--host N` (hosts a room), `--trade [mito]`, `--auction [max]`, `--social`, `--party [ms]` (accepts party invites and plays Undercity/patrol runs).

What the server does:

| Feature | Notes |
|---|---|
| Accounts | scrypt-hashed passwords, one driver per account (with its portrait and licence date), duplicate logins kick the older session |
| City channels | 30 drivers per channel (`cras-1`, `cras-2`, ...). Cars are relayed 10 times a second within 700 m and drawn with their real model, paint and a name tag. |
| Chat | channel, whisper (`*name text`, also `/w name text`), party (`&text`), crew (`@text`), server notices; rate limited; block lists drop whispers In the game the chat has the client's modes (area, channel, party, crew, whisper) with its prefixes, the whisper refill and reply-to-last; the driver info card (USERINFO) shows your records and opens for other drivers from the player menu. |
| Services | Private chat channels (`/cjoin`, `/csay`, `/cleave` ...), rental cars, driver rename with the name change kit, recommending the driver who brought you, crew-owned Mittron stations (the owner earns from fills), the personal shop on another driver's card, and the word filter on names, room titles and chat (`Game/Services/*`, `DriftCity_RE\SERVICES.md`). |
| Server notices | Notices in the client's own way: the centre-screen EMERGENCY notice, the scrolling marquee, popups, GM lines, other drivers' level-ups, item-obtained lines, kick reasons with the original texts, the server clock (the weather follows it) and maintenance mode (`Net/ServerNotices.cs`, server `src/notices.js`, `DriftCity_RE\HANDLERS.md`). |
| Parties | The client's party window and bar: named parties of up to 4 (leader first), invite, join requests to a full party's leader, decline, kick, leave, the refuse-party option; F12 toggles the list; `/invite name` still works. Party members always see each other on the map. |
| Friends and notes | Community window (main bar F1): friends with request/approval, online status and location, whisper, block list; notes (50 Mito per receiver, up to 10, 80 characters, saved box, expiry) kept in the server's database, the main-bar note notice; right click on a driver opens the player menu (whisper, friend, party, trade). |
| Crews | The Crew Center (Driver Dome building, or Community → My Crew): create (level 20, 2,500 Mito, 10 marks), join requests, ranks, Master hand-over, kick, leave, one rename, deletion after 7 days; crew battles are Battle Zone team rooms of two crews, and 3 straight wins take the channel (`Game/Social/*`, `DriftCity_RE\SOCIAL.md`). Channel Battles run on 20 battle channels at scheduled windows (default daily 20:00 for 2 hours, `DC_CB_SCHEDULE`): challenge coupons, a queue of attacking crews, the owner's map setup, part stakes on the major channels, spectating and cheering, result replays and the crew ladder. |
| Trade | `/trade name` or the player menu: both drivers offer items and Mito, lock, then confirm; the server checks both bags and swaps everything or nothing (`Game/Inventory/TradeWindow.cs`, server `src/trade.js`). |
| PvP races | T or a station's Battle Zone button opens the client's Battle Zone lobby (1000000). Online it lists the server's rooms: create one (track, laps, team or solo, password, max players) or join, then wait in the 16-slot wait room (1100000) until the host starts. The server keeps the slots and ready states, orders finishers, rejects impossible times, pays place-based rewards and ladder points, and returns everyone to the wait room. |
| Economy | chase, race and quest rewards are computed by the server with cooldowns. Car purchases, tuning, paint and custom-shop parts are checked for level, Mito and ownership. Resale is capped at 60% of what was paid into the car. Other drivers see your parts, and your car is rebuilt for them when you change it. |
| Anti-cheat basics | movement speed checks, finish-time checks, message flood limits |

If the connection drops you can keep driving, but nothing is saved until you reconnect
(Esc → Character select).

## Controls

| Key | Action |
|---|---|
| W/S or arrows | accelerate / brake (hold the brake at a standstill to reverse) |
| A/D | steer |
| Shift (or Space; pad: A) | drift: tap while steering above 70 km/h to start a slide |
| Ctrl (pad: RB) | booster: needs a full gauge, which fills slowly by itself, with every drift and on jumps |
| R | reset the car |
| Esc | game menu (options, character select, quit) |
| Enter | chat (online): type, Enter to send, Esc to cancel. In menus Enter and Backspace press the dialog's confirm and back buttons, as in the client |
| F2 / F3 / F4 / F7 / F9 | the button bar's keys: driver info, inventory, shops, quests, licences (K: manufacturing) |
| H / B | start an HUV chase / boss HUV chase |
| T | the Battle Zone (PvP lobby, wait room, tournament, ladder) |
| Q | quests |
| G | garage |
| P | shops (dealer, reseller, custom shop, tuning, paint) |
| I | inventory (Tab cycles its pages, F10-F12 sort the bag) |
| 1 / 2 | quick slots (items dragged from the inventory) |
| F12 | party list |
| M | city map |
| C | travel: back to the parking lot (Driver Dome) or straight to another city's gate |
| U | parts shop (performance parts, manufacturing, item bag) |
| L | licences |
| N | horn |

The bottom button bar (menu, driver info, inventory, shop, quest notebook) also works with the mouse.
HUV chase offers pop up on their own every so often.

## What's in

| System | Notes |
|---|---|
| Original UI | All 495 dialogs from `game.nui` rebuilt as uGUI with the original images, fonts and positions. Button tooltips (the main bar's labels and similar) appear on hover, and the client's keyboard bindings for confirm/back buttons (Enter, Backspace) work. |
| Front end | The client's own order and screens: login (account and password), server select (the 4120000/4130000 list with each server's load from its status page and the 15-grain crowd gauge; offline play is a row in the list), character select with three driver licences around Enter Game (portrait, name, level, date; delete with confirmation), licence registration as the remaster shows it (Operator Erina's backdrop with the OMD driver licence: eight portraits, name check, today's date), then Select Car. A new driver starts in Cras for the tutorial; returning drivers start in the parking lot. Offline drivers get three save slots; online, one driver per account (`Game/GameFlow.FrontEnd.cs`). |
| Tutorial | A new driver is an apprentice, as in the client: they start in the parking lot, Erina sends them to the Battle Zone building, Director Jason offers the Tutorial, and the course on Moon Palace's tutorial track teaches drifting through the gates and the booster, then grades the timed run (under 130 / 135 / 140 s) and lets them pick a reward car (Advance, Ironia, Drifter or Kicker; worse grades lock the better cars). It can be replayed later without the reward (`Game/Tutorial/*`, `DriftCity_RE\TUTORIAL.md`). |
| Parking lot | The Driver Dome (stage 9), where the client starts after login: the dome with its signs and particles, `.hit` collision, and the exe's zone test (garage, car shop and parts shop open their screens; auction, battle zone and team centre are restricted). The exits lead to the original SELGATE gate dialog and from there to a city; C or the travel list brings you back. Lot minimap and full map (`Game/ParkingLotStage.cs`, `DriftCity_RE\PARKINGLOT.md`). |
| Open cities | cras, moonpalace, koinonia, oros (terrain grid, building blocks, props) plus the road markings (`lane0.llf`), guard rails (`guardrail0.lgf`) and the invisible boundary walls (`main\hitwall.nif`), loaded in a few seconds. Each block's baked ground shadows (`shadow.dat`) are drawn, and the options' view range sets the client's drawing window of 200 m cells (`Game/CityStreaming.cs`). |
| Traffic | Each city's own traffic list and count from `init\trafficflowcontrol.xlt` (models weighted by their 1/3/6 frequency, 40 cars in Cras) on the city's lane network (`<city>.tcs`); they brake for cars ahead and get knocked around when hit. Every car has its own wheels from its `_dm.nif` and `tirelist` (spinning and steering, trailers swinging behind trucks), sits on them, and pitches and rolls with the road from four corner probes (`Game/TrafficRig.cs`, `DriftCity_RE\TRAFFIC.md`). |
| Pedestrians, trains, convoys | The pedestrians of `<city>\passerby.pcs` (about 1,800 in Cras, skinned with their walk/idle/run animations, on the client's 10-hour timetable so every player sees the same people; they jump aside from cars), Taipei's MRT on its viaduct line (3 trains per direction, 300 m braking into each station, 8 s stops) and the escort HUVs around boss HUVs (`Game/Passerby.cs`, `Game/Trains.cs`, `Game/Convoys.cs`, `DriftCity_RE\TRAFFIC.md`). |
| Camera | The client's driving cameras from the car code's presets (0x4793b0): the default is goption.ini's `DefaultPlayerCamera = 2`, normalview2, 8 m behind and 3 m up, aimed at the car about 1 m up (a ~14 degree downward look, matched against footage of the remaster); normalview1 (5.5 m, 1.85 m) when the file says 1. Boosting widens the view to 45 degrees. |
| Weather | The four weathers of `init\weather.ini` (clear, sunset, fog, heavy rain) change on a timetable that runs on UTC, so every player sees the same sky: lights, fog, shadows, the matching sky dome of `effects\skydome.nif`, the lens flare, and rain streaks (`effects\rain_02.tga`) at the file's rain rate. The client takes the weather from the server; the timetable is this rebuild's own (`Game/WeatherClock.cs`). |
| Oros ambience | Dust devils and tornadoes in Oros' desert while driving freely (`effects\bind.nif`, `tornado.nif`), at the client's timings and distances (`Game/OrosAmbience.cs`). |
| Stations | The four mission stations of each city ("Station 1".."Station 4" from `World/Data/Icon_<city>_Client.tdf`): driving up opens the client's ENTRANCE panel (400000) beside the road without stopping the car. Mission and Battle Zone open the quests and races, Delivery Service opens the job list, Fill up Mittron tops up the booster, Enter Driver Dome takes you to the parking lot (keys 1-5). The trigger distance (30 m) is this rebuild's own (`Game/StationPanel.cs`). |
| Missions | The Delivery Service (`World/Data/ArbeitClient.tdf`: 64 jobs, routes of delivery posts, gold/silver/bronze times and rewards, the run bar and clock, restart and abandon), instant missions (`Instant_Client.tdf`: the Call Mission banner offers one for your level while you drive; near-miss, combo, HUV and race kinds) and the officers' patrols beside each station (40/80/160 posts, EXP per post and for the loop), with the original dialogs, marker and minimap blips (`Game/DeliveryService.cs`, `Game/InstantMissions.cs`, `Game/PatrolMission.cs`, `DriftCity_RE\MISSIONS.md`). Party patrols (the leader starts, posts are shared, +15 s per post), the party bonus (+5% EXP or Mito per member in HUV modes), EX and event deliveries with a no-medal reward, the Mita 500 drink spots, and the quest bar (QUESTPLAYINFO). |
| Minimap | The client's own street image of each city (`_cras.dds` ...) under the round mask, with the exe's exact world-to-image mapping, its zoom steps (+/- and the numpad), north-up with a turning arrow or rotating (goption.ini RotateMinimap, MAPROT/MAPFIX buttons), and HUV, party and player blips pinned to the rim when far away (`Game/Minimap.cs`, `Game/CityMapInfo.cs`, `DriftCity_RE\MINIMAP.md`). |
| Name plates | The client's NAME plate (game.nui 1940000) over every car, the player's own included: OMD mark, driver name, a box stretched to the name and a stem pointing at the car's rear (`Game/NamePlate.cs`). |
| Driving | The client's own car model, recovered from the exe (`DriftCity_RE\PHYSICS.md`): one rigid body that faces its velocity plus a spring-driven slip angle, ratings mapped through `vehicle\levelmodifier.dat` (top speed, torque, boost duration and fill), a 5-speed box, tap-to-drift (held / released slide states), booster that needs a full gauge, wall and traffic hits, and the "outrun" chase camera. Skill items (assists from `Assist_Client.tdf`: mini boost, Turbo Start / Stop, the post-drift booster, crash saving), the Battle Zone's gauge and top-speed rules, the camera lifting on long jumps, and a durability bar from the Crash rating. |
| Mittron fuel | The client's fuel tank (separate from the booster): it drains with distance, an empty tank caps the car at 150 km/h and stops combos and Auto-Drive; stations fill it through the charger (10 Mito a litre), fuel items add 2 / 10 / 300 litres, and the Battle Zone offers them when the tank is low (`Game/Fuel/*`, `DriftCity_RE\SCREENS.md`). |
| Speed combo | The SPEEDCOMBO HUD (dialog 300000) with the client's four tricks: drifts, jumps, near misses past traffic and wrong-way driving, a 3 s window to chain them, broken by hits; near misses and wrong-way driving also fill the booster gauge (`Game/DriftCombo.cs`, `DriftCity_RE\COMBO.md`). |
| Original shop screens | Custom shop (VSHOP dialogs with category tabs, product pages, try-on and buy), parts shop (PARTSSHOP: buy, upgrade, combine, modify), manufacturing (MAKELIST/MAKEDETAIL) and licences (the NLICENSE grid with the original licence icons) use the game's own dialogs and item icons. |
| Roads and surfaces | The client's road test (`DriftCity_RE\ROADS.md`): the `.tcs` road strips (and Taipei's `total.hit` AREACHECK) decide wrong-way and off-road driving, the `sand.hit` / `water.hit` / `exclude.hit` zones mark sand, water and exempt areas. Off road, on sand or in water above 150 km/h the engine cuts and 5000 N of drag holds the car to 150; sand and water splash from the wheels in Cras (`Game/CarRoadState.cs`, `Game/RoadMapQuery.cs`, `Core/RoadNetwork.cs`, `Core/HitMesh.cs`). |
| Colours, plates, icons | Paint, neon, window tint, booster and decal colours from `effects\Vehicle*Color.nxt` (items name their colour id: `i_d_c_000NN`, `i_d_n_`, `i_d_w_`), each car's four factory colours from vehiclelist's Default_Color ids, the client's name plates, car keys, grade badges and maker emblems from its CMN image catalogues (keyed by the car's OldFileName or model file, grade and maker), and items without art get the client's "not available" icon. |
| Combo rewards | Every 10th speed combo rolls a material (the server's own combo table isn't in the client data; the chase material pool stands in, offline and on the server). |
| Effects | Skid marks and skid flames on the rear wheels, tyre smoke from hard cornering and launches, collision sparks, the `crash01-03` block/front/side bursts, damage smoke and the animated booster flame (`Game/CarEffects.cs`, `Game/NifParticleEffect.cs`, `DriftCity_RE\EFFECTS.md`). Brake and reversing lamps from each car's `_light.nif`. The event effects: the level-up pillar, the HUV arrest and race-winner banners, the item pickup, HUV explosions, palm trees and hydrants that break when hit, jump debris and the near-miss speed lines (`Game/GameEffects.cs`). |
| HUV chases | The game's 80 HUVs and 12 bosses flee along the lanes; ram them before time runs out. Original guide banners, beacon, minimap blip; EXP and Mito rewards. The HUV call (CHASING 870000: Tab starts), the chase map (CHASEINFO), the chase HUD and results (CHASEPLAYINFO, CHASERESULT / PARTYCHASERESULT), spawns on the city's `chasespawnlist`, party chases through the server, Rush Time, the Virtual HUV match and Auto-Drive (click a place on the city map, M) (`Game/ChaseScreens.cs`, `RushTime.cs`, `VirtualChase.cs`, `AutoDrive.cs`, `DriftCity_RE\CHASES.md`). HUVs drive the client's routes: a chase plans a start, waypoints and a goal on the road graph from the spawn list, bosses drive their recorded routes from `Huv00.xipath2` with their escorts on the boss's lanes, and in a party one member's client drives the HUV while the others see the same car (`Game/HuvRoutes.cs`). Online the server decides each chase's HUVs (count, speeds, health, time; from level 40 one chase in ten is a 16-HUV convoy), Rush Time and the Virtual HUV run with real players (the player can be the virtual HUV), and HUVs sit on the road as the client does at each distance. |
| Races | The Battle Zone with the client's own dialogs: the lobby with its arena tabs (opened by car grade and level), quick join, create room and password prompt; the 16-slot wait room (host START / READY, teams, slots that can be closed, filled with AI drivers or kicked, track browsing with random/reverse); the race on the original PvP tracks (`pvptrackinfo.xlt`) inside the open city with the pvpready fly-by, the 6 s countdown, lap / final-lap / wrong-way banners, the PvP HUD and the 10 s count after the first finisher; the single and team results screens, then back to the wait room. Also the ladder (3910000/3930000, also on the HUD's Ladder button) and an offline GT Championship (TOUR: preliminaries, quarter-finals, semi-finals, final). AI drivers follow the game's AI line (`aipath.nac`) (`Game/Pvp*.cs`, `Game/Ladder.cs`, `Game/Tournament.cs`, `DriftCity_RE\PVP.md`). Also wager rooms (fees into a pot, paid by rank), Real Match (50 minutes every 3 hours, everyone on the Duel), the lap-time battle, spectating (VIEWING with its 6 views), friend invites and Find a friend, emoticons and room chat, the PvP minimap, the scripted pvpready / pvpstart / pvpgoalin cameras, the reward pick after a race, gates checked by the server, AI drivers filling online rooms, and an online GT Championship. Battle Zone tracks have their own traffic on the track's paths, the same on every client. Spectators can join a race as a ghost of the driver they watch, races are recorded and can be replayed (also Channel Battle replays), and the result screen shows the bonus icons. AI drivers are the client's own 220 Battle Zone drivers (`init\aicarparam.xlt`: names, levels and cars), and Real Match cars wear their 16 looks with decals and neon. Mission battles (UFO / Ring / Rock objects), gate effects by place, and the AEROSET bonus points on aero kits. |
| Undercity | The client's UnderCity stage: Tatiana's gate (`uc_gate.nif`) on each city's entrance post opens UC_Enter (2000 Mito). The party's own city turns underground (`skydome_und_sky.nif`, `init\weatherunder.ini`), then the client's phases with their timings: the ready camera and countdown, the HUB phase (10 red HUBs that only fall to a jumping car, 90 roadside towers), the Main HUB behind its shield and barrier HUBs (3 attempts, the hurry warnings), the HUV waves from `init\uchuvparam.xlt` and the boss, then the UCRESULT board and the way back. Esc leaves (the run fails). Offline, 3 AI partners play beside you (`Game/Undercity*.cs`, `DriftCity_RE\UNDERCITY.md`). Online, a real party of 2-4 in the same city plays it together in a private channel: the server keeps the phase clock, the shared HUBs, barriers and HUV health, fills empty slots with AI and pays every member. The portal fee is the client's per city: 600 (Moon Palace), 1,200 (Koinonia), 2,100 (Cras), 4,200 (Oros). |
| Quests | The main quest chain (`motherquest.xlt`) with the operator's original script text and portraits in the quest dialog. Objectives use the offline systems (catch HUVs, win races, upgrade or buy cars, drive distance). Quests unlock in story order: each needs the one before it, and the level gate ramps (1, 3, 5, ...) because the table lists level 80 for most of them. The server applies the same rule. |
| Step quests and sides | The QuestClient.tdf story and repeat quests from the stations' Quest button: givers in the city, up to five steps each (arrive, stop, pass through, trail a car on its `chpath`, stake-out, chase), the crash, time, speed and distance panels (730000-780000), fail and retry, the end-post reward. After quest #126 the story splits: #127 joins Team ROO, #145 stays with OMD; ROO stations, \"Time Attack\" deliveries, ROO patrols and the beat banners follow the side. The storyline's last quest plays the ending and credits. QSRANKLIST shows the delivery rankings (`Game/StepQuests.cs`, `StepQuestRunner.cs`, `EndingCredits.cs`, `DriftCity_RE\MISSIONS.md`). Features unlock as in the client: deliveries above level 3, HUV calls above level 5, Koinonia / Cras / Oros above levels 13 / 28 / 45 (or their quests); until the rookie missions are done the stations show the beginner panel. |
| Cars | All 121 supported player cars from `vehiclelist.xlt` (classes V1-V9) with their stock wheels and every grade from `vehicleupgrade.xlt`. Grades and fitted parts change speed, acceleration, durability and booster. |
| Garage and shops | Garage (the original GARAGE dialogs and 3D garage: floors, car list, get on, tuning panel, stats), dealer (original car-shop screen with 3D preview, V1-V9 class filters and stat numbers), reseller (the dealer's Sell tab: table resale value plus 30% of the parts), tuning (buy the next grade), paint shop (solid, two-tone and glossy paints). The car dealer is the client's ADV dealer (Purchase / Sell / Receive Reward, NEW / Popular / Duffy's / V1-V9 filters, compare, wish list), as `globalcontent.xlt` selects (`Game/Shops/*`, `DriftCity_RE\SHOPS.md`). |
| Custom shop | The visual shop with the original models, textures and stat bonuses (1,005 items): aero kits (whole bodies of the "s" cars), front bumpers and hoods (the "c" cars), car-specific wings and universal spoilers, wheels including glossy ones, booster flames (shown on the exhaust while boosting), underbody neon, number plates (front and back), air ducts, roof items, window tint, decals, horns and engine sounds. Universal parts sit on the mount points in each car's `_dm.nif`. Decals come from `decal\decal.nxt`, which gives per-car stamp positions per body region; they are composited into the car's paint texture. Each item fits only the cars `visualitem.xlt` allows. Try a part on your parked car while the camera circles it, then buy it (the original 30-day Mito price, paid once) or swap back. Parts belong to the car they were bought for. Mileage points (1 per 100 Mito spent), gifts to other drivers (arrive as notes with the item), the wish list and checkout, the trade history. Parts sell for the table's periods (7 / 30 / 90 / 365 days) or unlimited, and a period part leaves the car when it expires. |
| Auction House | The Driver Dome's auction building: fixed-price listings with quantity, 13 rows a page, categories, level range and search; listing from the inventory's Auction page (up to 60, 7 days, 10% tax minus the licence discount); online the server holds goods and Mito and pays sellers by note; offline an AI market (`Game/Auction/*`, server `src/auction.js`). |
| Licences | The 63 licences from `license.xlt` with their names, conditions and bonus effects. 16 can be earned here: their conditions map onto tracked counters (HUV chases, races per city, race points, main quests, part enhancement, other licences). The rest need patrols, deliveries, team battles or auctions, which this rebuild doesn't have. Mount one for its bonus, e.g. +12% EXP and Mito from HUV chases or more EXP on a city's tracks. |
| Parts shop | Performance parts for speed, acceleration, durability and booster in Normal, Super and Hyper grades, fitted per car. Enhance them to +12 with Mittron Enhancers (PLUS and DELUXE raise the odds; failing from +5 drops a level), grind them into Parts Dust, or combine two with a Seed Part into the next grade, up to Ultra. The client never shipped the parts' stat table, so points, prices and odds are this rebuild's own (`Game/Workshop.cs`, mirrored in the server's `src/workshop.js`). |
| Manufacturing | The 28 skill books and recipes from `alchemy_skillbook.xlt` with their item names, inputs, kits, success chances and EXP, and the levels from `alchemy_levelmodify.xlt`. Materials drop from HUV chases; dust comes from grinding parts; enhancers, seeds and kits are sold at the counter. Crafted Vehicle Enhancers raise a car a grade, roof boxes give their roof item, and Bacchus drinks give EXP. The other results are Battle Zone items, kept as collectibles. |
| HUD | Original speedometer (speed digits, gear number, RPM needle, booster arc that fills and pulses when full or boosting), the ADV button bar the remaster shows (goption.ini MainUIType = 1: Esc and F1-F9 buttons with hover labels, F-keys work, a full-width EXP bar that fills with your progress), the chat window (CHAT ADV 260000) with its filter tabs, always on screen with the system log of rewards, items and level-ups (and chat online), the street-map minimap, the city map with your position on the exact spot. |
| Options | The client's options (goption.ini in the player's folder): resolution and window mode, graphics level, anti-aliasing, shadows, HDR, the four volumes, speaker mode, minimap rotation and other drivers, deny party, notices, chat balloons; the controller page rebinds pad buttons and keys. Also the help book (HELPADV), the car audio player (game music or your own MP3s from the `BGM` folder), the guide-text hints and chat balloons over cars (`Game/Options/*`, `HelpWindow.cs`, `CarAudio.cs`, `GuideText.cs`, `DriftCity_RE\OPTIONS.md`). |
| Audio | Title, city, chase, race and shop music; per-car engine sounds (`car.ini`); tyre skids, brakes, booster and impact sounds; UI clicks. The game's IMA ADPCM effects are decoded in the client. Music and effect volumes are in Options and are saved. |
| Ambience | The 155 positional sounds of `env.xlt` per city (loops, areas, off-road and timed sounds), rain, and the announcer's voice lines for places, gate effects, the lead and jumps (`Audio/CityAmbience.cs`, `Audio/VoiceLines.cs`, `DriftCity_RE\AUDIO.md`). |
| Inventory | The client's slot inventory (INVENBASE/MYINVEN): Parts (drag a part onto its slot, right click to take off), Drift Item (car view, custom parts), Licence, Modify (part + Mittron enhancer); item tooltips with the client's texts, double click to use, the Yes/Sell/Delete popup, count boxes, discard by dragging out; drinks (+30% EXP, +50% Mito or double drops for an hour), Mito cheques, repair and booster items; two quick slots on the HUD (`Game/Inventory/*`, `DriftCity_RE\INVENTORY.md`). The newer NDLG inventory is the default (the classic one stays selectable): set parts with their set bonus and 720-hour period, expiring items, up to four quick slots with the auto quick slot, and the two battle quick slots used in races. The second parts row and option parts, custom marks imported from an image file (shown to other drivers online), and the battle items' effects in races. |
| Events | Gacha (capsule shop and slot machine with the reward pools of `init\rewardgroup.xlt`), scratch cards, gift boxes and lucky bags, the coupon book (coupon cars at the dealer, coupon codes), the token book with its daily mission, and the seasonal city events: Christmas trees to decorate (snow once one is finished), the fall harvest, Halloween, Easter and summer drops. Events run by date; `-events`, `DC_EVENTS` or `events.cfg` pick them for testing, and online the server decides. No real money: everything costs Mito or event tokens (`Game/Events/*`, `DriftCity_RE\EVENTS.md`). Also the gacha parts inventory, Pick'em while spectating a race (slot tickets), the harvest / Thanksgiving / Valentine screens with basket gifts, the coupon stamp book, the open-beta event table, the prop-hunt event and the Halloween drink. |
| Profile | Level and EXP, Mito, cars with grade and paint, inventory, quests, stats (HUVs caught, races won, best lap, distance). |
| Bonuses | Golden time (daily 20:00-22:00, +50% EXP and Mito on races, chases and the Undercity), the Christmas Santa bonus and the anniversary's triple rewards, shown on the result screens and in the HUD's badge strip with the drink, tree and Mita bonuses (`Game/Multipliers.cs`, `DriftCity_RE\PROGRESS.md`). |

## Not included (online-only or not rebuilt)

- Custom sticker uploads and the newer NDLG inventory set.
- Taipei's props, `.hit` collision meshes and guard rails (collision uses the visible meshes).
- From the visual shop: custom stickers (players' own uploaded images) and rental cars.
- Licences that need the guardian system, because those game modes aren't rebuilt. Ladder points are approximated by race points.
- Many menus are a simple list window rather than the original slot-based dialog, because those
  dialogs were filled by server data. The dealer, quest dialog, popups, map, minimap and HUD use the
  original dialogs.

## Code layout

| Piece | File |
|---|---|
| NayaPack archives (`.agt`, `Patch.###`), virtual file system | `Assets/DriftCity/Core/NayaPack.cs`, `GameFileSystem.cs` |
| Gamebryo NIF 20.0.0.4 parser, DDS decoder | `Assets/DriftCity/Core/Nif/*`, `DdsImage.cs` |
| `.xlt` tables, CP949 text, `.ntx` UI images, `game.nui` layout | `Assets/DriftCity/Core/{XltTable,Cp949,NtxBundle,NuiDocument}.cs` |
| City formats (`.lf`, `.lbf`, `.lof` + `.nxt`/`.xlt`, `.loi`), traffic network (`.tcs`), race paths and NPC scripts | `Assets/DriftCity/Core/{CityMap,TrafficNetwork,GameScripts}.cs` |
| NIF → Unity, materials, shaders | `Assets/DriftCity/Runtime/NifSceneBuilder.cs`, `Shaders/*` |
| UI framework (dialogs, popups, list window, HUD text) | `Assets/DriftCity/Runtime/UI/*` |
| Game systems | `Assets/DriftCity/Runtime/Game/*` (GameFlow, CitySession, CityLoader, TrafficSystem, ChaseMission, RaceMission, QuestTracker, Menus, ShopData, Minimap, Hud, EngineAudio, PlayerProfile) |
| Arcade look (camera effect, sky, toon globals) | `Game/ArcadeLook.cs`, `Shaders/{ToonLighting.cginc,ArcadePost,ToonSky}.shader` |
| Game data import | `Editor/DriftCityImport.cs`, `Core/GameFileSystem.cs` (imported-copy mode) |
| Car physics and camera | `Assets/DriftCity/Runtime/DriftCar.cs`, `ChaseCamera.cs` |
| Online client (connection, session, remote cars, chat) and the reward/shop router | `Assets/DriftCity/Runtime/Net/*`, `Game/Progress.cs`, `Game/OnlineRace.cs` |
| Custom shop, paint/decals, licences, parts shop, manufacturing | `Game/{PartsCatalog,CustomShop,CarPaint,Workshop,WorkshopMenus}.cs` |
| Server | `../DriftCityServer/server.js`, `src/{db,auth,economy,world,race,workshop}.js` |

`Core/` has no Unity dependencies. `../DriftCityStaging/Harness` is a .NET 8 console app that runs the
same parsers over every game file (`dotnet run -c Release -- <gameRoot> <mode>`).

## Tests

```
unity test . --editor-version 6000.4.2f1 --mode PlayMode
```

- `GameFlowTests` (new driver: login → intro → car creation → city).
- `GameplayTests` (traffic moving, HUV chase and reward, race with AI driving the line, every menu,
  quest progress, upgrade/respawn, travel to moonpalace).
- `RegressionTests` keeps the full-review fixes fixed: HUD and minimap really added in the editor, ADPCM
  sounds, dealer preview removed however its dialog closes, popups answering only their own buttons, a frozen
  car holding its brakes, the collider measured in car space, no mesh growth over ten car rebuilds, no car
  swaps during a race, respawn on the racing line, an interrupted chase cleaning up, no duplicate car
  purchases, and a damaged save restored from its `.bak`.
- `DrivingTests` drives the original car model on a flat pad: 0-100 km/h and top speed, a tapped drift that
  slides and recovers while laying skid marks and smoke, the booster past top speed, and braking.
- `ContentAuditTests` builds all 121 cars and loads every part model, mount point, decal texture, licence and
  recipe. It writes `Logs/content_audit.txt`, a per-car report, plus pictures of a fully dressed car and its wheels.
- `OnlineTests` starts the real server (needs `node`) plus a bot. It creates an account on the login
  dialog, creates a driver, joins a channel, sees the bot's car, whispers to the bot and claims a
  server-checked reward.

Server tests: `cd ../DriftCityServer && npm test`. They cover accounts, channels and chat, parties, every
custom-shop slot, licences, performance parts (with fixed dice), the counter, manufacturing, item use,
the race lifecycle and economy checks. The server reads prices, quests, recipes and licences from the game
tables (`src/gamedata.js`, folder from `DC_GAMEDATA`), so the client can't name its own price.

The play-mode tests write screenshots to `Preview/`. Batch render tools: `DriftCitySetup.BatchCityPreview`
(env `DC_CITY`) and `DriftCityUiPreview.RenderDialogs` (env `DC_UI_KEYS`).
