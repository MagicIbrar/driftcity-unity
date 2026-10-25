# UI framework: game.nui controls, element states, check boxes

Recovered from `DriftCity.exe` (image base 0x400000), UI framework band 0x7af000-0x7cffff. Decompiled sources in
`decompiled/phys/0xADDR.c` (`dec.py`); small functions read as disassembly (`text.asm`, `fn.py`). **[exact]** = read
from code or data, **[inferred]** = interpretation. Remake: `DriftCityOffline/Assets/DriftCity/Runtime/UI/NuiView.cs`
(`NuiControlView.Refresh`).

## 1. Control kinds (TYPE)

Factory `0x7b1e60` (switch on TYPE-1, table 0x7b2124) **[exact]**:

| TYPE | ctor | size | kind | elements (editor table) |
|---|---|---|---|---|
| 1 | 0x7b61e0 | 0x134 | static (base class, vtable 0x87f5b4) | 1, NORMAL only |
| 2 | 0x7c9430 | 0x140 | button (vtable 0x885430) | 2, all six states |
| 3 | 0x7c90c0 | 0x164 | check box, derives from button (vtable 0x8853f4) | 1 = box (six states), 2 = mark ("Checked" / "UnChecked") |
| 4 | - | - | none: logs the error string 0x87f408 | - |
| 5 | 0x7c3010 | 0x2b8 | combo box (field, arrow button, list) | 4 |
| 6 | 0x7c1650 | 0x100 | slider (track, thumb) | 2 |
| 7 | 0x7b99a0 | 0x1d8 | nine-image frame (9 elements, NORMAL only); not used in game.nui | 9 |
| 8 | 0x7bbf50 | 0x238 | edit box (10 elements: frame pieces + caret) | 10 |
| 9 | 0x7c0cc0 | 0x270 | list box (body, row) | 2 |
| 10 | 0x7c3100 | 0x148 | scroll bar (track, up, down, thumb) | 4 |
| 11 | 0x7c5440 | 0x280 | tree / multi-level list (MOTHERQUEST_LIST) | 2 |

Counts in game.nui: 11889 static, 2829 button, 368 check, 31 combo, 34 slider, 59 edit, 48 list, 9 scroll, 1 tree.

## 2. Element state slots

The NUI editor's label table at **0x87fc10** is `char name[TYPE 0..11][element 0..9][state 0..5][30]`; the property
readers/writers `0x7c66d0..0x7c6f70` index it (`((type*10+elem)*6+state)*30`) and only touch a key whose label is not
empty **[exact]**. Its labels give the meaning of the `I{k}_{s}` / `UV{k}_{s}` / `C{k}_{s}` / `FC{k}_{s}` / `S{k}_{s}`
slots (s = state + 1):

| slot | state | label |
|---|---|---|
| 1 | 0 | NORMAL (check box element 2: "Checked") |
| 2 | 1 | OVER (mouse over) |
| 3 | 2 | PRESSED |
| 4 | 3 | FOCUS |
| 5 | 4 | DISABLED |
| 6 | 5 | HIDDEN (check box element 2: "UnChecked") |

So the remake's earlier reading (2 hover, 3 disabled, 4 pressed, 5 checked) was wrong: slot 5 is DISABLED, which is why
its colour is dim in 1360 of 1527 buttons and in every check box.

Element loader `0x7baab0` (element object 0x1a8 bytes, ctor 0x7ba680) **[exact]**: `+0` current state, `+4/+8` POS,
`+0xc/+0x10` SZ, `+0x20+s*4` image handle per state (-1 when the key is empty), `+0x38+s*0x20` UV corners,
`+0xf8+s*4` colour, `+0x110..+0x11c` the current colour as floats (animated), `+0x120+s*4` sound `S{k}_{s}`,
`+0x13c+s*4` font colour `FC`, `+0x164`/`+0x17c` outline colour/flag `FOC`/`FO`.

Colour defaults `0x7c6ae0` **[exact]**: the value is read with `sscanf("%X")` into ARGB, so a **six-digit value such as
`FFFFFF` has alpha 0**; a missing key is FFFFFFFF, except PRESSED C8FFFFFF and HIDDEN 00FFFFFF. Font colours default to
FFFFFFFF (`0x7c6dc0`).

Drawing `0x7ba050` (vertex fill, per texture batch from `0x7b2840`) **[exact]**: an element is drawn with
`image[state]` and only while `colour[state]` has a non-zero alpha. An empty image (-1) is still drawn, as a plain
colour quad: e.g. HELP 1920000 menu rows have no images, colour 25221B (invisible) normally, FF534537 on hover and
FFFFBA00 (the gold bar) when selected. The drawn colour eases towards `colour[state]` (`0x7b4e80`, `1 - pow(...)`)
except for PRESSED, which is instant; the text colour eases the same way towards `FC1_{state}` of element 1
(`0x7b7033`) **[exact]**.

## 3. Control flags and the state machine

Control flags `+0x3c` (base init `0x7baf10`: 0x27) **[exact]**: 0x01 alive, 0x02 visible (NATION K), 0x04 enabled,
0x08 mouse over (set/cleared by the dialog's hover tracker `0x7b2190`, kept in dialog `+0x1ac`), 0x10 focus (vtable
+0x14 `0x7b62d0` sets it, +0x18 `0x7b62e0` clears it; the dialog's focus owner is changed by `0x7af2e0`), 0x20 from
the layout. Button `+0x134` = pressed (mouse captured).

**Button** update `0x7c9270`, for every element **[exact]**:

1. not visible -> HIDDEN; else
2. not enabled -> DISABLED; else
3. pressed -> PRESSED; else
4. mouse over -> OVER; else
5. focus -> FOCUS; else NORMAL.

A state change plays that state's sound. The elements are also moved: +1,+1 px while pressed, -1,-1 px while hovered
(delta against the last offset, `+0x138/+0x13c`). Messages `0x7c9180`: WM_LBUTTONDOWN/DBLCLK inside -> pressed,
SetCapture, focus; WM_LBUTTONUP -> release, drop focus (`0x7af320`), inside -> notify 0x101 (click); a double click
notifies 0x103. While held down with the mouse over it the button notifies 0x102 every frame (auto-repeat).

**Check box** update `0x7c8f70` **[exact]**: element 1 (the box) follows the same order without the offset (NORMAL, then
HIDDEN / DISABLED / PRESSED / OVER / FOCUS). Element 2 (the mark) is **slot 1 while checked and slot 6 while unchecked**;
while checked and disabled its colour is `C2_1` with alpha 0x70 (both saved at init `0x7c8f30`). Elements 3+ stay
NORMAL. So:

- with `I2_6` (CREATEROOM NOGATE, wait room MAP_RANDOM/REVERSE, Crew Center, auction, wish list, dealer colours) the
  unchecked look is the I2_6 image;
- without `I2_6` (note list, options) `C2_6` defaults to 00FFFFFF and the mark is simply not drawn while unchecked.

There is no separate checked image set and element 1 never carries the check state. Toggle: mouse up inside or
Space up -> vtable +0x38 `0x7c8db0(!checked, notify)` sets `+0x140` and notifies 0x401.

**Selected tabs / rows / filters.** The game shows the current tab by clearing the control's hover flag and calling
its vtable +0x14 every frame, and +0x18 on the others: dealer tabs `0x5a2ba0` (`+0x11c/+0x120/+0x124` by tab `+0x20`),
dealer filters `0x5ae9f0` (13 buttons at `+0x134` by `+0x38`) **[exact]**. So "selected" = **FOCUS, slot 4**: tab art has
NORMAL / OVER / PRESSED / FOCUS cuts side by side (e.g. PARTSSHOP_TAB_BUYIMG x 585/666/747/828) and DISABLED = the
normal cut dimmed (C8C8C8C8); list rows (MYFRIEND_MANAGER_BTN, NOTE_BTN, VSHOP_BASE_OVER_LIST) have no normal image
and show their I1_2 / I1_4 art only on hover / when selected.

Statics have no update: they draw their NORMAL slot. Many statics carry six-digit `C1_1` (alpha 0): text labels laid
over the editor's `default.dds` sheet, icon/logo placeholders that the code fills at runtime. **[exact]** for the rule;
the remake still draws statics opaque (see section 5).

## 4. The remake's rule (NuiControlView)

- `State`: DISABLED (Interactable false) > PRESSED > FOCUS (`Selected`) > OVER > NORMAL. `Selected` keeps hover from
  showing, as the client's per-frame hover clear does.
- Buttons: every element takes `I{k}_s` / `UV{k}_s` and `C{k}_s` with the client's defaults and alpha rule; a state
  without an image draws a plain colour quad; elements shift by 1 px on hover / press; text colour = `FC1_s`.
- Check boxes: element 1 as a button (no shift); element 2 slot 1 when checked (alpha 0x70 when disabled), slot 6 when
  unchecked; elements 3+ normal.
- Combo / slider / edit / list / scroll / tree: their own updates are not rebuilt; element 1 takes the state slot with a
  fallback to slot 1, other elements slot 1 (as before, with the slot numbers corrected).
- Pointer events go to a transparent "hit" rect at element 1 (the client hit-tests element rects whatever their colour,
  `0x7b9e80`), so hover-only rows and invisible hotkey twins stay clickable.
- Not reproduced: the colour easing (states switch at once), per-state sounds `S{k}_{s}`, outline colours per state.

## 5. Left as is

- Statics keep the remake's opaque reading of six-digit colours: several screens copy art from static template controls
  or fill placeholders at runtime and some already hide the `default.dds` backs themselves (`OriginalMenus.HideDefaultBacks`).
  Following the client here (alpha 0 = not drawn) is a separate pass over those screens.
- PvP wait room (`PvpWaitRoom.Tab` / city tabs) makes the host-only tabs non-interactable for guests; the client keeps them
  enabled and ignores a guest's click (`0x592540` checks `0x5901d0` per click), so with the client's DISABLED-first order a
  guest sees them dimmed instead of selected.
