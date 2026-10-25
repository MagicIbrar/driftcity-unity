# Progress and reward multipliers

Where the original client shows an EXP / Mito bonus, what it knows about it, and the remake's rules (code map "Progress &
rewards"). Tags: **[exact]** read from code or data, **[inferred]** meaning guessed from use, **[remake]** our server's rule
(the official server decided these amounts; the client only displays them).

## 1. What the client shows

### 1.1 Result rows (PVP.md 14.8) **[exact]**

0x588100 (single) / 0x58a070 (team): the entry dword `+0x8c` carries the server's bonus flags: 1 PC-room Mito, 2 PC-room EXP,
8 golden time (plays UI effect 43 goldentime), 0x10 / 0x20 EXP / Mito item, 0x200 / 0x400 Santa EXP 25 % / 50 % (both 75 %),
0x800 the 3rd anniversary; the tree icons (EXP+Mito 30-60 %) follow the finished Christmas trees (0x7733a0), not a flag.

### 1.2 The HUD badge strip MAINBUTTON_EXP_* (dialog 3030000) **[exact]**

Init 0x5e9920 (main bar object; the ADV copy 0x5f1300) binds the 21 badge controls 0x2e3bf1..0x2e3c05 (= the dialog's controls
in file order). Layout 0x5e6090 hides all, then shows each active one from right to left: x = its authored x - edi, where edi
starts at 40 - (screen width - 1024) / 2 and grows by each shown badge's width. Order and source:

| badge | art | shown when (object field) |
|---|---|---|
| ONLYEXP1 | "EXP+30%" | +8 (EXP drink) |
| ONLYMITO1 | "Mito+50%" | +0xc (Mito drink) |
| ITEM2X | item x2 | +0x18 (set by 0x5e5680 from packets 0x4f7436 / 0x4f7488: the item drink) |
| 2X_XMAS | "EXPX2" (Christmas) | +0x1c |
| EXP_MITO_XMAS1-4 | 30 / 40 / 50 / 60 % | +0x34 == 1, stage +0x218 (0x5e56b0 from 0x4f9509 / 0x4f95b8: the finished trees) |
| MITABASE + MITAX2/3/4/10, MITATEXT | Mita 500: X150 / X200 / X250 / X500 %, a "%2d" count | +0x10, value +0x164 (150 / 200 / 250 / 500), count +0x168 |
| ..._HALLO set | the Halloween drink art | +0x14 (replaces the plain one) |
| 3RD | "ExpMito300%" | the bag holds an active `a_em_300` item (0x6d9760 / 0x6dac20 / 0x6eb2f0) |

There is no golden-time badge on the HUD: golden time is only the result flag and its banner.

### 1.3 Other bonuses (done in earlier rounds)

Licence bonuses (`ME_*` effects, WorkshopMenus / workshop.js `bonus`), the party bonus (0x4fe050, PARTYPLAY.md 3), the drinks
(ItemUse / items.js `buffBonus`), the Christmas trees (XmasTree / events.js `xmasBonusPct`), the scratch-card Battle EXP goals
(Lotto / events.js `battleExpBonusPct`), the Mita 500 drink (MitaDrink, economy.js "mita"). `Etc.NewChar2ExpEvent` is a config
string in the exe (a new-character EXP event switch) whose use was not traced.

## 2. Multipliers (remake rules) **[remake]**

Client `Runtime/Game/Multipliers.cs`, server `src/multipliers.js`; they add to the percents above in `Progress.Extras`
(offline) and `workshop.rewardExtras` (online, provider `setMultiplierBonus`, installed in server.js with the event seasons).

| bonus | when | kinds | EXP | Mito | result flag | HUD |
|---|---|---|---|---|---|---|
| golden time | every day in the golden hours (default 20:00-22:00; offline machine time, online server time) | race, chase, undercity | +50 % | +50 % | 8 (goldentime banner on the results) | none (a chat line when it starts / ends) |
| Santa | Christmas season (Dec 1 - Jan 6) | race, chase | +25 % (Dec 24-25: +50 %) | - | 0x200 / 0x400 | - |
| 3rd anniversary | anniversary season (Jun 1-14) | every reward but the combo drop | +200 % (x3, the badge's "300%") | +200 % | 0x800 | 3RD |

- Golden hours: events.cfg `golden=20-22` or env `DC_GOLDEN` (client offline); server env `DC_GOLDEN` or `createServer({ golden })`;
  "off" disables; "22-2" wraps past midnight. Under `node --test` the calendar is off unless a test asks for it
  (`multipliers: true` or `golden`), so reward checks don't depend on the hour.
- `event.state` carries `multipliers {golden, goldenHours, goldenPct, santa, anniversary, flags}`; the client reads golden time
  from it online.
- `Pvp.BonusFlags` adds `Multipliers.ResultFlags()` to the driver's own result row (golden / Santa / anniversary icons,
  `PvpResults` already draws them and plays goldentime for flag 8).
- HUD: `BonusBadges` (in Multipliers.cs) opens 3030000 while the city HUD (3010000 / 3020000) is up and no full-screen menu is
  open, and lays out the EXP drink, Mito drink, item drink, tree stage, Mita 500 (Halloween art in its season; the count is the
  drink's minutes left) and the anniversary badges as 0x5e6090 does. EXPX2 has no remake rule and stays hidden.

## 3. Verification

- Server: `test/multipliers.test.js` (hours, kinds, flags, `event.state`, a chase reward with golden time + anniversary).
- Unity: `Tests/BannersPeriodsTests.cs` (`CalendarMultipliers`: the percents, flags and an offline chase reward;
  `CityBadgesAndPeriodCheckout`: the strip in the city, Preview/fx5_badges.png).
