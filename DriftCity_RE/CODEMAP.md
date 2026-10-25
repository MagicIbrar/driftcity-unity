# DriftCity.exe code map

Every function of DriftCity.exe sorted into subsystems and compared with the Unity remake. The scripts and outputs live in
`DriftCity_RE/codemap/`; the dashboard "Drift City conversion map" shows the same numbers.

## Results (2026-10-24)

- 18,647 functions (the 292 packet handlers Ghidra merged into their neighbours are split out), 4,448 KB of code. 729 KB is replaced on purpose rather than converted: the C runtime, ODE/OPCODE
  physics (Unity PhysX + `DriftCar.cs`), GameGuard/Hangame/crypto/system code and the original TCP protocol (our own
  WebSocket server).
- Estimated conversion of the remaining 3,719 KB of game code: about 94% (42% at the first map, 48% after the delivery
  service, instant missions, patrols, event effects, pedestrians, trains and convoys, 53% after the Battle Zone
  lobby/wait room/ladder/tournament and the Undercity, 64% after party/friends/notes/crews, events/gacha/coupons and
  the inventory/trade screens, 72% after party play, the Battle Zone extras, chases/Rush Time/V-HUV/Auto-Drive, the
  ADV dealer and Auction House, options/help/car audio and the exe's UI control states, 81% after the handler audit, step quests and sides, the
  Channel Battle cycle, the remaining event screens, ground shadows and streaming, the ending, and the unlabelled code
  identified, 85% after HUV routes and shared party HUVs, the NDLG inventory and set parts, Mittron fuel, driver
  info and chat modes, ghost battles and replays, and the quest unlocks, 87.5% after the apprentice tutorial, the effect banners, reward
  bonuses, period prices, the table audit, streaming fade-in and Real Match looks, 94% after the last mile: HUV pose modes and server chase
  units, Rush Time / V-HUV online, assists and PvP car rules, city ambience and voice lines, the server services,
  mission battles and the small screens).
- 295 of the 384 game.nui dialogs the exe drives are used by the remake.
- 3,032 functions are cited in our RE notes (studied function by function).

What remains is spread thin and largely cannot be converted 1:1: features of the original company's services
(G-Coin cash shop, PC-room bonuses, Hangame friend invites), art missing from this install (stand-ins are drawn),
GM-only and debug replies, and rules the original server held (the remake's own rules are documented per system).

## Address layout

| Range | Contents |
|---|---|
| 0x401000-0x76ffff | game code: world streaming, renderer wrapper (Xr*), vehicles (XiGame\Vehicle), traffic/HUV AI, tables, UI screens, quests, stages, PvP, Undercity, effects |
| 0x770000-0x77ffff | platform: crypto, CPU detection, registry, crash dumps |
| 0x780000-0x7affff | networking: XiGameClientNet, Auth/Game/Area TCP sessions |
| 0x7b0000-0x7cffff | UI framework: controls, IME, XML parser |
| 0x7d0000-0x7effff | table loaders (Init/*), logging |
| 0x7f0000-0x82ffff | OPCODE collision and ODE physics |
| 0x830000- | C runtime and compiler-generated stubs (SEH unwind blocks, thunks) |

## How it is built (re-run after converting something)

1. `procedures_raw.json`: Ghidra's procedure list (REA bridge `list_procedures`). `extra_entries.json`: entries Ghidra
   missed (direct call targets and .data pointers to code that starts after a ret/int3).
2. `build_funcs.py`: per function its size, direct callees, imports, strings and constants from `text.asm` (the full
   linear disassembly in the scratchpad) -> `funcs.json`.
3. `evidence.py`: game.nui dialog/control ids among the constants (ids below 200000 need two control hits; 10000-11999
   are text ids, 20000-21999 server message ids), packet handlers from 0x519120's table, addresses cited in
   `DriftCity_RE/*.md` and `decompiled/` -> `evidence.json`.
4. `classify.py`: manual corrections from function-by-function study first (`OVERRIDES`), then rules over strings, dialog keys and handler roles, address-range fallbacks, call-graph votes (handlers
   and generic layers don't vote) and neighbour smoothing -> `classified.json`.
5. `unity_usage.py`: dialogs our C# opens or wires, data files it names -> `unity_usage.json`.
6. `scorecard.py`: per-subsystem status and conversion estimate (the status table in the script is maintained by
   hand from what the remake does) -> `codemap.json`, `codemap_functions.csv`.
7. `datasets.py`: the dashboard's datasets (`ds_summary.json`, `ds_subsystems.json`, `ds_missing_dialogs.json`).

Labels are heuristic: sizes per subsystem are approximate, and about 100 KB is still unlabelled.
