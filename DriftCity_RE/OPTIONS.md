# Options, controller keys, help, car audio, guide texts, chat balloons

What DriftCity.exe does and how the Unity remake follows it. [exact] = read from the exe or the data files,
[inferred] = from names/layout/behaviour, "remake rule" = the remake's own choice where the client gives nothing.

## goption.ini (the option object)

- One option object at **0x8d2850** (fields addressed as `DAT_008d28xx` elsewhere: +0x76 Audio Enable = 0x8d28c6,
  +0x88 UseUserMP3 = 0x8d28d8, +0x8c RotateMinimap = 0x8d28dc, +0x98 ShowAnotherUserInMinimap = 0x8d28e8). [exact]
- **Reader 0x4c9440**: `init\goption.ini`, else `init\def_option.ini` (flag +0x75 says which). Keys and reader
  defaults [exact]:
  - `[Graphic]` Fullscreen, MainUIType, ScreenWidth/Height, ColorDepth, DepthFormat, MultiSampling (forced 0 when
    +0x111), ShadowQuality (raised to at least 3), CarDetail, EnvironmentDetail, EffectDetail, CarSpecular, FogEnable,
    MotionBlur, ShadowAffectCar, ShowObjectShadow, VehicleShadow (out of 0..4 -> DynamicShadow ? 2 : 1), VisibleRange
    (clamped to 5), Car/Object/TerrainTextureQuality, ObjectAnimation(1), BattleAnimation(1), ShowEffectMine(1),
    ShowEffectUsers(1), VideoGradeSet(-1), CustomVideoGrade (true forces VideoGradeSet -1; -1 runs the auto grade
    0x4c80d0), Hdr (read then forced 0), ShadowRange(1), CarShader(2, forced 2), ShadowMapType, EnvMapType(1), Occ
    (forced 0), AniVisibleRange(1), DofType, FovType.
  - `[Audio]` Enable, MusicVol, EngineVol, FXSoundVol, UISoundVol, UseUserMP3, RandomPlay.
  - `[Game]` RotateMinimap(0), ShowDialogBallon(1), ShowInstanceMSG(1), ShowAnotherUserInMinimap(1), DenyParty(0),
    DenyInvite(0), PublicMyPos(1), DefaultPlayerCamera(2), FriendTeamNotify(1), StrainNotify(1), Navigation(1),
    GhostBattleMode(1); `[RoomMirror]` PosX(500) PosY(100).
  - `[Controller]` only when 0x4ccf20() is 2..10: Use, Index, Type, ButtonAccel(1) ButtonBrake(2) ButtonBooster(3)
    ButtonDrift(4) ButtonEnter(5) ButtonESC(6) ButtonSpace(7) ButtonTab(8) ButtonItem1(9) ButtonItem2(10).
  - `init\ndev_option.ini [Developer]` debug switches (not rebuilt).
- **Writer 0x4ca130**: same keys in the same order, plus ShowPassenger; stores DefaultPlayerCamera = the camera in
  use. Called by the OPTION apply paths and by the car audio channel switches (0x5b21d0 / 0x5b27f0). [exact]
- Remake: `Game/Options/GameOptions.cs` keeps the file as ordered sections (round-trips unknown keys) and writes it to
  `Application.persistentDataPath\goption.ini` (remake rule: the installed client is not modified; the first run copies
  the installed goption.ini, or def_option.ini, and keeps the volumes earlier remake builds stored in PlayerPrefs).
  Tests that set `PlayerProfile.SaveDirOverride` get their own goption.ini there. Remake keys: `[Audio] SpeakerMode`,
  `[Keyboard] Key<Action>`. The shipped `[Controller] Button*=0` reads as the reader's defaults 1..10.

### What is applied in Unity

| Key | Remake effect |
|---|---|
| Fullscreen, ScreenWidth/Height | `Screen.SetResolution` (player builds; the editor's game view owns its size) |
| MultiSampling | `QualitySettings.antiAliasing` 0/2/4/8 |
| ShowObjectShadow | object shadows: the sun's shadow pass (`QualitySettings.shadows`) |
| VehicleShadow | 2-3 cars cast shadows, 0-1 their renderers stop casting (the flat blob is not rebuilt) |
| Hdr | `Camera.allowHDR` |
| VideoGradeSet | Unity quality level index; -1 / CustomVideoGrade = "Custom" |
| VisibleRange 1..5 | stored; the slider also sets traffic density as before |
| FovType | 0 arcade / 1 real: drives the remake's arcade look switch as before |
| Car/Object/TerrainTextureQuality | stored 4; the combo switches the HD textures (next city load) |
| Enable, MusicVol, FXSoundVol, UISoundVol, EngineVol | `AudioLibrary` SoundOn / MusicVolume / SfxVolume / UiVolume (UI clicks) / EngineVolume (EngineAudio); FXSoundVol also sets the env ambience (`CityAmbience`, AUDIO.md 1) |
| SpeakerMode (remake) | `AudioSettings.Reset` with the speaker mode; the music restarts |
| RotateMinimap, ShowAnotherUserInMinimap | `Minimap.SetRotate` / `ShowOtherUsers`, also on every new minimap (OptionsHost) |
| DenyParty | online: `social.options { refuseParty }` (server keeps it per account); the tab shows the server's value |
| ShowDialogBallon | chat balloons (ChatBalloon) |
| UseUserMP3, RandomPlay | car audio channel / shuffle |
| MainUIType | the HUD's main bar: 1 ADV 3010000, 0 basic 3020000 with ITEMSHOP / NOTEBOOK (Hud.cs, AUDIO.md 8); swapped when changed |
| ShowInstanceMSG, FriendTeamNotify, Navigation, AniVisibleRange, EnvMapType, DofType, ShowEffectMine/Users, BattleAnimation | stored (hooks listed in the lead report) |

## OPTION dialogs

- OPTIONBASE 4250000 + tabs: the manager binds 4250000 -> 0x534670 and the tabs to thunks 0x53a770/0x53a780/0x53a790/
  0x53a7a0 (vtable calls +0x2c..+0x38); both 4290000 entries go to the same INPUT handler. Setup 0x53b210 caches the
  tab controls (4260005 MAINUI ... 4290003 COMBO). [exact]
- OPTIONDETAIL 4300000 handler 0x539c00: each combo's selection change (0x201) stores the new index and sets the
  "changed" flag +0xc8; EXIT/CLOSE play sound 0x4e8b, APPLY 0x4eb9 and, when changed, applies (0x627690). GAMMA
  (4300029) is caught but does nothing; HDR on with DOF off also turns DOF on (0x539f78). [exact]
- The classic OPTION 2980000 (handler 0x538a20, setup 0x538fa0) and VIDEOADV 2990000 are the pre-ADV pages; the
  remaster opens 4250000 (not rebuilt: 2980000, 2990000).
- Remake (`Game/Options/OriginalMenus.Options.cs`, partial of OriginalMenus): every control edits GameOptions and
  applies at once; APPLY saves goption.ini, CLOSE / EXIT / Esc restore the snapshot taken at opening (the detail and
  controller pages keep their own snapshots). Combos drop a list under the field (the client's combo list, drawn in
  the field's font). VIDEO: ratio 4:3 / Wide filters the resolution list (Screen.resolutions), screen mode, main UI
  Basic / Advanced, graphics grade (Unity levels + Custom), ADVANCED. AUDIO: sound on/off, BGM, 3D effects, UI, engine
  sliders, speaker. GAME: minimap rotation, deny party, users on the map, instant messages, balloons, friend/crew
  notify, navigation. INPUT: device combo (Keyboard / pads) opens OPTION 3000000.

## OPTION 3000000: controller / keys

- Setup 0x538fa0 (handler 0x535010): the dialog is enabled only when 0x4ccf20() is 1..10 (pad count) [inferred];
  controls USE / USENOT, STICK_NO / TYPE1 / TYPE2, KEY_* (the "Change" buttons) and TEXT_* (the current button number
  shown in the row). [exact layout, inferred semantics]
- Remake (`Game/Options/KeyBindings.cs`): pad buttons in `[Controller]` with the client's names, DirectInput order
  1 A, 2 B, 3 X, 4 Y, 5 LB, 6 RB, 7 Back, 8 Start, 9 LS, 10 RS (+ 11 LT, 12 RT, remake). Stick type 0 = d-pad
  steering, 1 = left stick, 2 = left stick also drives (Y axis) [inferred]. With the pad not in use the remake's own pad
  layout stays (RT/LT, left stick, A drift, RB boost). Keyboard rebinding is a remake extension: with "None" ticked the
  Change buttons capture a keyboard key (`[Keyboard] KeyAccel=UpArrow` ...); defaults are the client's fixed keys
  (arrows, Ctrl booster, Shift drift) and W/S, right Ctrl/Shift and Space keep working while an action has its default
  key. DriftCar's ReadInput / ReadBoost read KeyBindings.ReadDriving. Esc cancels a capture.

## HELPADV 1930000 (help)

- Setup 0x5448f0 [exact]: binds the tree controls (CONTENTS +0x94, CHAPTER +0x98, IMG_KEYCTRL +0x9c, IMG_COMBO +0xa0,
  INDEX01..06 headers, 21 chapter buttons, LINE0-4) and labels them from `localize\helpindex.xlt` (`Category0-5`,
  `Index0-20`, `IndexCount`). Chapter n is `localize\h{n+1}.xlt`: Title, ShowPicture, PictureIndex (0 key chart,
  1 combo chart), MaxLine, Line0... The classic HELP 1920000 uses the same data.
- Chapter order (button -> Index): CAR_CONTROL 0, CAR_CHANGE 1, COMM 2, COMM_CHAT 3, COMM_EMOT 4, COMM_PARTY 5,
  COMM_TMCREATE 6, COMM_TEAMJOIN 7, MISSION_QUEST 8, MISSION_QUICK 9, MISSION_CHASE 10, GAME_COMBO 11,
  GAME_BATTLEZONE 12, GAME_CHANNELPLAY 13, ITEM_CHANNELBATTLE 14, GAME_AUCTION 15, ITEM_REMODEL 16, ITEM_PART 17,
  SHOP_USE 18, SHOP_CAR 19, SHOP_USE_PARTSHOP 20 (field order of 0x5448f0).
- Remake `Game/HelpWindow.cs`: tree with MINUS/PLUS collapse (laid out with the dialog's own spacing), PREV/NEXT,
  chapter text in the CONTENTS box or the picture. The quit menu's Help opens it (hook below).

## CARAUDIO 3040000 / 3050000 (car audio)

- 0x5b21d0 "My MP3" channel: starts the user MP3 stream (0x52b5f0: stops the game BGM, plays the user playlist),
  UseUserMP3 = 1, writes goption.ini. 0x5b27f0 back to the game: stops the user stream (0x52b550), UseUserMP3 = 0,
  writes goption.ini, title = text 10482 "DriftCity BGM Mode...". 0x5b2740 sets the title text; 0x5b2320 slides the
  title strip in over 600 ms (sine). 0x5b1fd0 shows a button's tooltip while hovered, only with sound on and My MP3.
  Text 10481 "Add music to the BGM folder.". The install's `BGM\CopyMP3Here.txt` marks the user folder. [exact]
- `Data\audio\bgm\music.txt`: TrackNo, file, TurnOver (track that follows), English title. [exact data]
- Remake `Game/CarAudio.cs`: Skid Rush (game music) / My MP3 (install BGM\*.mp3) channels, play / pause / stop /
  prev / next / shuffle (RandomPlay), the title strip with the music.txt title. A finished city track follows
  TurnOver (a looping clip's clock wrap is the end); My MP3 goes to the next file. While My MP3 plays, game music
  requests are remembered and played again when switching back (AudioLibrary.MusicIntercept). Shown by the HUD
  (hook below); the basic main bar's ID_MAINBUTTON_CARAUDIO_OPEN (3020000) maps to CarAudio.Toggle().

## GUIDETEXT 930000 (ADV) / 920000

- Guide object (ctor 0x5c2450, vtable 0x870ae0, at UI manager +0x194; create 0x5c3030) caches the 18 strips of both
  dialogs and CHARGERPVP 960000. Update 0x5c26b0 blinks GPSLOADING while the flag set by 0x5c2950 is on and hides
  BOSSHP when its timer (+0xc4) runs out; 0x5c28c0 hides all (called from stage changes 0x53cd0f, 0x74c865 ...).
  0x5c2c30 opens the PvP charger and shows FUELWARNING when the player has none of the three charger items. The strips
  are pre-drawn text images (ui_contents52.dds), one place on screen. [exact]
- The strips' texts (read off the remake's capture Preview/guidetext_all.png): QUEST_GET "Go to the Station to receive
  quests.", REQUESTHOUSE_GO "Follow the arrow to the Station.", REQUESTHOUSE_SELECT "Go to the Station menu.",
  QUEST_ING "Mission In Progress.", ARBEIT_ING "Delivery Service In Progress.", TIMEATTACK_ING "Time Attack In
  Progress.", HUV_START "Press Tab to start chasing.", HUV_FIND "HUV found! Arrest the enemy car by hitting it.",
  HUV_CRASH "Enemy car found! Arrest the enemy car by colliding with it.", FUELWARNING / FUELMEMPTY (one image) "You are
  low on Mittron fuel. Fill up soon.", MOONPALACE / KOINONIA / CRAS / OROS "Go to <city> to receive Missions.",
  GPSLOADING "Receiving city's GPS data...", BEGINNERQUEST "Follow the arrow to begin a rookie mission.", BOSSHP "HUVs
  have restored the Boss HUV's HP.". In the ADV layout they sit right of centre above the speedometer.
- Remake `Game/GuideText.cs` (GuideTextDriver polls every 0.25 s while driving in a city) [triggers inferred from the
  texts]: BEGINNERQUEST for a driver up to level 5 with no quest done or taken, else QUEST_GET with no quest taken,
  QUEST_ING while one runs, REQUESTHOUSE_GO when one is complete; REQUESTHOUSE_SELECT at a station (ENTRANCE 400000 or
  the delivery board up); ARBEIT_ING during a delivery; TIMEATTACK_ING in the lap time battle or a race; HUV_START while
  the chase offer (INSTANTMISSION 860000, Tab) is up; HUV_FIND (HUV_CRASH for a boss) for 4 s when the HUV comes within
  80 m; FUELWARNING when the Mittron gauge drops under 10 %, FUELMEMPTY when boosting on empty; GPSLOADING blinks on
  arrival until the minimap has its map. The city strips and BOSSHP wait for their callers. One strip at a time: the
  newest flash, else the highest state. `GuideText.Flash / Hold / SetGpsLoading / HideAll` for other code.

## BALLOON 3300000 (chat balloons)

- Nine pieces + NAME + TEXT (layout in game.nui). Remake `Game/ChatBalloon.cs`: `ChatBalloon.Say(car, name, text)`
  builds the frame around the wrapped text (max 200 px) with the tail on the car's screen point, 6 s (remake rule),
  one per car, only while ShowDialogBallon is on.

## Not rebuilt

(Round 4: AEROSET 3900000 and the classic OPTION 2980000 / VIDEOADV 2990000 pages are built, SCREENS.md 8.) The PvP charger 960000,
the client's flat car shadow, DOF / environment maps / CPU detail levels (stored only).
