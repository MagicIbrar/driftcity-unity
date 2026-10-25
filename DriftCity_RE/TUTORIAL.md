# The Tutorial mode (the apprentice course)

How DriftCity.exe runs its "Tutorial mode" (the apprentice course, MISSIONS.md 14.7) and how the remake rebuilds it.
Addresses are VAs (image base 0x400000). **[exact]** = read from code or data, **[inferred]** = interpretation,
**[remake]** = the remake's own rule. Decompiled sources: `decompiled/phys/0xADDR.c`.

Unity: `Runtime/Game/Tutorial/ApprenticeCourse.cs` (`Apprentice` data and rules, `ApprenticeCourse` the run),
`Runtime/Game/Tutorial/ApprenticeHost.cs` (`ApprenticeHost` lines, parking-lot arrival, the trip; `ApprenticeRewardPick`
CREATECAR 4160000). Server: `src/economy.js` reward kind `apprentice`. Tests: `Tests/ApprenticeTests.cs`, `test/server.test.js`
("apprentice course").

## 1. Data [exact]

- `World/Data/Apprentice_Client.tdf` (loader 0x706d70 -> quest manager +0xdc; row n is picked by 0x57c2d0(n) and shown in
  QUESTDIALOG 600000 by 0x4fdd70). 13 rows, one markup cell each:

  | row | speaker | content |
  |---|---|---|
  | 0 | walkie-talkie, SYSTEM, Operator Erina | "Welcome to Mittron Island ... please go to the Battle Zone first", M opens the map |
  | 1 | Director Jason | "... Please press the Tutorial button." |
  | 2 | Instructor Duffy | the Drift lesson (Shift + direction), "Try to Drift at the second corner" |
  | 3 | Duffy | "You've failed the Drift test. Let's try it again." |
  | 4 / 5 | Duffy | drift passed + the Booster lesson (left Ctrl, the gauge, Reckless Driving / Skill Pass, Help) (same text) |
  | 6 | Duffy | "Good job ... Now, go drive the full course" |
  | 7 | Duffy | "You've failed the Booster test." |
  | 8 | Duffy | "Do you want to take the test again? ... Try your best this time." |
  | 9 / 10 / 11 | Duffy | "Excellent!" / "not as impressive as I heard" / "not everyone has a natural talent", each "pick one of these vehicles" |
  | 12 | Operator Erina, SYSTEM | "Congratulations on finishing the Tutorial mode ... use the exit outside the secret base" |

- The course: pvptrackinfo `moonpalace | Tutorial`, Id 74, CityType 3 (0x6cb790 and PVP.md: 3 = tutorial), folder
  `moonpalace\tutorial` (camera.nif, point.nif, spotcam.nxt, floor_m / wall_m / area .hit, object0.loi, terrain0.lif,
  shadow.dat; **no aipath.nac**), LapCount 1, start line (-3521,359)-(-3473,326), end line (819,-394)-(854,-396) (point to
  point, about 4.6 km), 7 gates gate01..gate07, name key `moonpalace_tutorial` ("Tutorial").
- Grades: globalcontent `ApprenticeLapResultGood / Normal / NotBad` = 130 / 135 / 140 s (US; TH 135 / 140 / 145), comment
  "튜토리얼 보상 기준" (tutorial reward thresholds). `ApprenticeFlowType` = 1 for US: "0 no beginner mode, 1 the tutorial must be
  done, 2 skip the tutorial and give the car first (Thailand)".
- Dialogs: APPRENTICE 830000 (time m'ss.fff in LAP6TH..1ST, a drift row DRIFT06..01 with DRIFTICON; NUMBER01..10 = digit
  art 0..9, NUMBERMINUS), 840000 (POS4TH..1ST, POINT, METER: the distance), 850000 (KEY_SHIFT / KEY_CTRL + KEY_PLUS +
  KEY_LEFT / RIGHT / UP, all at one spot). All controls K=0. Fields of the APPRENTICE object (init 0x568860): 850000's
  LEFT +0xd8, RIGHT +0xdc, UP +0xe0, SHIFT +0xe4, CTRL +0xe8, PLUS +0xec; 840000 POS1ST..4TH +0xbc..+0xc8, POINT +0xcc,
  METER +0xd0.
- Reward pick: CREATECAR 4160000 (`ID_CREATECAR_SELECT_*`, init 0x7488c0). The reward cars are the vehiclelist rows with
  Sellable >= 10000 (0x749530), sorted: **Advance 10001, Ironia 20001, Drifter 30001, Kicker 40001**.
- Texts: 11340 (confirm with reward), 11469 "Would you like to play the Tutorial?", 11446 (flow type 2), 11285 "You cannot
  access during Tutorial", 11298 "Please try again." (KR "%s ..."), 11299 / 11300 / 11301 "Tutorial Record: Less than 2
  minutes and 10 / 15 / 20 seconds", 11307 (shop: "a reward for completing the Tutorial mode"), 11321 "My Record".
- `World/Data/Tutorial.tdf` (0x706ba0, the Battle Zone gate power-up lines) does not ship and nothing reads it; not used.

## 2. The apprentice state [exact]

- The server's driver flag **net+0x24c & 0x80000 = "Main.Apprentice"** (flag names 0x453250). 0x6cb790 = the current car's
  record kind == 3 (`char + 0xd54 * 0x31 + 0x125`): the apprentice drives a tutorial car **[kind name inferred]**.
- The apprentice object `[game+0x160]+0x250`: +0x48 step, +0x4c / +0x50 times at gates 1 / 2, +0x54 the longest drift
  (from car +0x8c0), +0x58 / +0x5c failure flags, +0x60 grade 0..3, +0x64 course time. 0x592f70 clears +0x4c..+0x64.
- 0x72b600(n): step = n, then row n (0x57c2d0) into QUESTDIALOG.

## 3. How a new driver gets there [exact unless marked]

1. Character creation: the reply handler 0x505d60 only acts for flow type 2 (step data +0x60 = 3, +0x64 = 1000, stage 6:
   the car pick first). For the US (type 1) the driver logs in as an apprentice.
2. Parking lot arrival 0x762f70 with the apprentice flag and the tutorial car: the car stands at (0.5, -0.8, 2.5), heading
   0.165 pi (centre, facing the Battle Zone), a guide line (0x6bb660) runs (0.5,-0.8) -> (28.1,15) -> (42.6,23.2) ->
   (79.6,44.3) toward the Battle Zone building (102,57); step 0 -> **row 0** (walkie-talkie + Erina). Steps 3 / 7 put the car
   at (75, 45) instead. Step > 8 with the tutorial car -> **row 12**, step 0. While on the tutorial car the other buildings
   refuse with 11285 (0x763f30).
3. Battle Zone -> the PvP lobby: 0x57c420 (apprentice) closes VIEWMAP 570000 and, on the tutorial car with step < 8 and
   != 1, step 1 -> **row 1** (Jason).
4. RMLOBBY's TUTORIAL button (0x57ea5e):
   - apprentice + tutorial car: popup 11340; **Yes** (0x57cdd0 -> 0x578d90) starts the course; **No**: +0x60 = 3,
     +0x64 = 1000, stage 6 = the reward pick with the worst grade (only the Kicker);
   - apprentice, other car: flow 2 -> row 1; else 11469, Yes -> 0x578d90 as a **practice run** (step 6: the full course);
   - no apprentice flag: with net+0x270 & 1 ("BattleZone.Tutorial") a lobby call (+0x254 vfunc 0x28), else 10745.
   So **the client offers the course again** through the TUTORIAL button (11469), without the reward.
5. 0x578d90: track row 0xb of the track table, packet 0x787690 (start), a one-driver room, race start (vfunc 0xd0).

## 4. The run [exact unless marked]

- Race start (0x72f190, room id < 0 = local): apprentice: step 6 -> 8 (no line), else step 2 and **row 2**; while
  QUESTDIALOG is open the race waits, then the countdown (state 5, start = now + 10 s).
- Gates (0x72f3c0): a gate counts within 150 m when the car crosses its line; then `n = 0x72fe00(gates passed before)`,
  n != -1 -> sound 0x4ec3 (20163 = SID_PVP_GATEPASS; AUDIO.md 3) + 0x72b600(n). 0x72fe00 (only while step <= 7):
  - gate 1 (index 0): step != 3: +0x54 < 0.5001 -> +0x58 = 1, row 3; else +0x54 = 0, +0x4c = time;
  - gate 2 (index 1): step != 3: +0x54 < 0.5001 -> +0x5c = 1, row 3; else +0x54 = 0, +0x50 = time, the booster gauge
    (car +0x1a68) = **97.0**, row 5;
  - gate 3 (index 2): gauge < 97 -> row 6 (the booster was used), else row 7;
  - gate 4 (index 3): row 9 (only reachable while still in the lesson).
- Every frame (0x7300a0): step 2 with 0 gates and time >= 12 s, or 1 gate and >= 22 s -> failure flag, row 3; step 3:
  restart (vfunc 0x134, step 2) once time >= 14 s (gate 1 section) / 24 s (gate 2 section); step 7 / 6: 0.6 s after
  QUESTDIALOG closed -> restart (7: step 2; 6: vfunc 0x104 + 0xd0 = the full course); +0x54 follows car +0x8c0 (kept when
  >= 0.5001, else cleared when the drift ends); 840000 gets the distance to the goal (0x567e80).
- HUD 0x5680f0 / 0x568540 (while 0 or 1 gates are passed): the time, the drift row, Shift + Right before gate 1, Shift +
  Left before gate 2; the distance blinks within 1200.
- Finish (0x72ab10 -> 0x72b371): +0x64 = time, grade: < Good 0 (row 9), < Normal 1 (row 10), < NotBad 2 (row 10), else 3
  (row 11); race state 14.
- Leaving the race (0x725330): step >= 8 -> stage 6 (the reward pick) on the tutorial car, else stage 9 (parking lot).

## 5. The reward pick [exact]

- Stage 6 enter (0x749530, "CreateCar Stage Enter"): apprentice -> the list of Sellable >= 10000 cars; flow type != 2 shows
  PREV (0x3f7a0f) **[its use in reward mode not traced]**.
- Refresh 0x747bb0: gauges, plates, colours; "My Record m:ss" (11321, if time <= 999); by grade: 0 any car; 1 the 1xxxx car
  locked (11299); 2 also 2xxxx (11300); 3 also 3xxxx (11301), each with 11298; a locked car disables NEXT (flag 4).
- NEXT (0x747060) apprentice: packet 0x79f0a0(0, time, car, colour) (not the creation packet 0x781530). The server's car
  reply 0x4ef1a0 adds the car; in stage 6 with step >= 9 it sets step 0 and stage 9 (parking lot).

## 6. The remake

- **Who is an apprentice [remake]**: `PlayerProfile.Apprentice` (new field, with `ApprenticeBest`): set for a new driver
  (server `newProfile`; offline the creation hook below), cleared by the reward. Saves from before have it off. The remake
  has no tutorial car: the apprentice drives the starter car picked at creation, so "apprentice and tutorial car" is
  `Apprentice` alone (`Apprentice.OnTutorialCar`: an apprentice on his first car). The parking lot's refusals (11285 for
  every zone but the Battle Zone, 0x763f30) and the special arrival spots of 0x762f70 ((0.5, -0.8) facing the Battle Zone;
  (75, 45) after a failed lesson) are rebuilt: `Apprentice.Refuses` / `ArrivalSpot`, `ParkingLotStage` (AUDIO.md 10).
- **Entry**: `ApprenticeHost` (self-starting) shows row 0 on the first parking-lot arrival of a session and puts a beacon over
  the Battle Zone building (instead of the guide line); `ApprenticeCourse.BattleZone(thenAsk)` = row 1 (+ 11340 in the
  parking lot, whose Battle Zone building has no lobby in the remake); `ApprenticeCourse.TutorialButton()` = the lobby
  button (11340 / 11469); `Skip()` = 11340 No.
- **The course**: runs in the open Moon Palace (as all remake PvP tracks): `Begin` travels there (`GameFlow.TravelTo`) and
  restores the saved city afterwards. RaceMission.TracksFor now keeps CityType 3 tracks (line through the gates, like the
  lap-time tracks); the Battle Zone map list keeps CityType 1 only. City traffic is held off during the run **[remake]**.
  Steps, gate rules, timeouts and restarts as section 4; the retry after a failure shows row 8 **[inferred: the client's
  restart path to it was not traced]**; drift = seconds in DriftCar.DriftState != 0 (car +0x8c0 **[inferred]**); the
  countdown is 6 s with pvp_countdownstart. HUD: 830000 (time; the drift row while gates 1/2 are ahead), 840000 (metres to the
  finish line as four digits, POINT hidden, blinking within 1200 m **[unit inferred]**), 850000 (Shift + Right / Left; Ctrl +
  Up before gate 3 **[remake]**), plus the PvP HUD and minimap.
- **After the finish**: the grade's row, then an apprentice gets CREATECAR 4160000 (3D preview as the creation screen, NEXT
  claims), a practice run goes back to the parking lot. After the reward: back to the parking lot and row 12 there.
- **Reward [remake rule, server authoritative online]**: `Progress.Reward("apprentice", {seconds, vehicleIndex, color})`.
  Server `economy.apprentice`: `{start:true}` notes the start; a claim needs `Apprentice === true` (once per driver), a time
  >= 60 s and not shorter than the server saw since the start (1000 s = skipped, grade 3, no start needed), a reward car
  (vehiclelist Sellable, `gamedata.js` field `sellable`; fallback 2/3/8/0) the grade reaches (130 / 135 / 140 s). The car
  becomes the current car; `Apprentice = false`, `ApprenticeBest`. Offline `Apprentice.ClaimOffline` runs the same rule.

## 7. Hook lines (files the course does not own)

- GameFlow.cs `OnLoginComplete` (a new driver goes to the parking lot as the client does, not to Cras):
  `if (newDriver) { newDriver = false; if (ApprenticeCourse.SendNewDriversToLot) { StartCoroutine(EnterParkingLot()); return; } City = "cras"; StartCoroutine(EnterCity()); return; }`
  (the lead decides; with the line left as it is the course still works from the parking lot later).
- GameFlow.FrontEnd.cs `CreateOfflineDriver`: `var p = new PlayerProfile { ..., Apprentice = true };`
- GameFlow.cs `MissionActive`: `|| ApprenticeCourse.Active`.
- PvpLobby.cs `Hook`: `ui.On("ID_PVP_RMLOBBY_TUTORIAL", _ => ApprenticeCourse.TutorialButton());` (keep CHARGE on Unavailable);
  `Open` after `Load()`: `ApprenticeCourse.BattleZone(false);` (0x57c420, Jason's line in the lobby).
- ParkingLotStage.cs, in the area action before `default:`: `case Area.Battle when ApprenticeCourse.BattleZone(true): Stop(); return;`
- Social/ChannelBattle.cs setup tracks: `.Where(t => t.Line.Count >= 2 && t.CityType == 1)` (CityType 3 and 4 tracks are no
  channel battle tracks).
