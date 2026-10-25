# Minimap and full map (DriftCity.exe, image base 0x400000)

All numbers below are read from the exe unless marked *inferred*. Decompiled sources: `decompiled/phys/0x5f2050.c`,
`0x5f6200.c`, `0x5f6840.c`, `0x5fe8c0.c`, `0x5f3e90.c`, `0x5f3fe0.c`, helpers `0x5f2d80.c`, `0x5f2290.c`, `0x5f2320.c`,
`0x5f21d0.c` .. `0x5f2260.c`.

## Class layout (minimap object, vtable 0x871ca8, ctor 0x605ba0, init 0x604300)

| member | meaning |
|---|---|
| +0x04 / +0x600 | skin dialog: 210000 MINIMAP DIGITAL (controls 0x33450..) or 200000 MINIMAP CLASSIC (0x30d40..); the 190000 dialog the remake uses has the same layout as CLASSIC |
| +0x08 | BASE control (ring frame, `ui_play01.dds 1,233~219,451`); its draw message 0x2001 runs the minimap draw 0x5fe8c0 (handlers 0x603cd2 / 0x603df2) |
| +0x10 | MASK control: its texture is stage 1 of the disc (alpha mask) |
| +0xac / +0xb0 | MAPROT / MAPFIX buttons |
| +0x1c.. | icon controls from dialog 220000 (MINIMAP ADV icons): +0x1c MINE (type 1), +0x20 PLAYER (type 2), +0x24 PARTY (type 3), +0x28 QUEST (5), +0x2c ARBEIT, +0x38 HUV (type 4) ... |
| +0x150 | zoom index |
| +0x154..+0x16c | zoom table **{1, 2, 3, 4, 5.5, 8, 10}**, +0x170 = 0 terminator (0x604526) |
| +0x174 | city index at open |
| +0x17c.. | the VIEWMAP (full map) dialogs 480000 MP, 490000 KO, 500000 CR, 510000 OR, 530000 TP, 570000 parking 1F, 580000 B1 |
| +0x51c | the CHANNEL label |

City index (`0x780510()+0x3b4ec`): 0 parking lot, 1 moonpalace, 2 koinonia, 3 cras, 4 oros, 5 taipei.

## Image (0x5f2050)
1 `_moonpalace.dds`, 2 `_koinonia.dds`, 3 `_cras.dds`, 4 `_oros.dds`, 5 `_taipei.dds`; parking lot by floor
(`0x761900()+100`): 0 `_parking.dds`, 1 `_parkingB1.dds`. Handle in `DAT_008d9d98`. Zoom index set to **1** in a city,
**3** in the parking lot (init default 2).

## Tables
World bounds, `0x871a48 + city*16` = {xmin, ymin, xmax, ymax} (Gamebryo units; Unity x = GB x, Unity z = GB y):

| city | xmin | ymin | xmax | ymax |
|---|---|---|---|---|
| 1 moonpalace | -8169 | -5379 | 8082 | 3980 |
| 2 koinonia | 0 | 0 | 10046 | 14511 |
| 3 cras | 0 | 0 | 10000 | 8063 |
| 4 oros | 644 | 390 | 12656 | 14786 |
| 5 taipei | 0 | 0 | 19900 | 15480 |
| 0 parking 1F (coded) | -461 | -461 | 461 | 461 |
| 0 parking B1 (coded) | -357 | -380 | 357 | 380 |

Image table, `0x871ac8 + city*24` = {texW, texH, x0, y0, x1, y1} (crop rectangle the world bounds map onto):
moonpalace 1024x512 full, koinonia/cras/oros 1024x1024 full, **taipei 1024x1024 crop 410,368 - 768,827**;
parking 1024 full (coded).

Verified: the `.tcs` road polylines of moonpalace, koinonia, cras and oros drawn through this mapping lie exactly on
the image's roads (overlay check; this install has no taipei.tcs).

## Disc (0x5f6200, drawn by 0x5fe8c0)
* 50 vertices at angle a = i * 0.1256637 (2pi/50), drawn as a triangle fan of 48 triangles
  (`DrawPrimitiveUP(6, 0x30, 0x8d9da0, 32)`), vertex = xyzrhw + uv0 + uv1.
* Screen: centre = BASE control centre (`0x8da3e0/e4`), position (cx + sin a' * 103.5 * sx, cy + cos a' * 103.5)
  with a' = a - (heading - pi/2) when rotating (`DAT_008d28dc`), else a' = a. sx = UI horizontal scale.
* uv0 (map): fx = (x - xmin)/W, fy = 1 - (y - ymin)/H (0x5f2d80); u = (fx*texW - x0)/(x1 - x0),
  v = (fy*texH - y0)/(y1 - y0); vertex uv = (u + sin a * R, v + cos a * R * W/H), **R = zoom * 0.0575**.
  Sampler clamp (address mode 3).
* uv1 (mask): ((sin a, cos a) * 0.5 + 0.5) * 0.9159664.
* Texture stages (when `(flags 0x24c & 0x18) < 0x10`, the normal UI type): colour = map.rgb * diffuse, alpha =
  mask alpha (the map's own alpha is not used: the image's transparent land shows its stored light grey).

## Icons (0x5f6840)
d = player - object (Gamebryo). px = (dx / W) * 90 / (zoom * 0.05), py = (dy / H) / (W / H) * 90 / (zoom * 0.05), then
times texW/(x1-x0) and texH/(y1-y0). Rotating mode: angle -= (pi/2 - heading) and (px, py) rotated by that angle.
Screen = (cx - px, cy + py) (i.e. east right, north up). Beyond 90 px: pinned to (90*sx + 5, 95) * dir with colour
0x55ffffff; types 9, 0xb, 0x26, 0x27 are hidden instead. Only types 1, 2, 3, 4 and 0x23 keep their rotation.
Type 2 (other players) is skipped unless `DAT_008d28e8` (ShowAnotherUserInMinimap).
Player arrow: type 1 at (0,0) with the Z Euler angle of the car's world rotation (a compass bearing, clockwise).
Others: pi/2 - car heading (+0x1bb4).

Note 1800 px per (offset / W) at zoom 1 = 103.5 / 0.0575: the icons and the disc use the same scale.

## Buttons / keys
* 210004 / 200004 PLUS -> 0x5f23b0(1): index-- (min 0) = zoom in; MINUS -> 0x5f23b0(0): index++ while the next entry
  is not 0.0. Keys (0x4e1ea1): DIK_ADD (numpad +) = in, DIK_SUBTRACT (numpad -) = out.
* MAPROT (x08) -> 0x5f3010(1) + 0x5368a0 (rotate = 1); MAPFIX (x07) -> 0x536900 (rotate = 0). Only the button for the
  other mode is visible.
* ENTIREMAP (x06) -> 0x5f4dd0 (full map).

## Frame label
The only label on the frame is CHANNEL (+0x51c, 0x604037): `sprintf(globaltext 10565 "CH.%d")` (10854 "GATE.%d" when
flag 0x200 at +0x244), "OMD" when the channel is < 0, empty when >= 100. No district/area-name source was found in the
minimap class; an area name in remaster footage would come from elsewhere (not recovered).

## Full map (0x5f3e90, icons by 0x5f3fe0)
x = ctrl.left + ctrl.width * fx, y = ctrl.top + ctrl.height * fy (same fx/fy as above, no crop). The control is the
VIEWMAP region box; its size matches the world bounds' aspect for ID_VIEWMAP_*_DUMMY in every city (CR 775x625 =
1.240 = 10000/8063, MP 1417x817, KO 694x1003, OR 576x690, TP 1295x1008, P_1 614x614), so DUMMY is the region
(*inferred from the aspect match*; the member that holds it was not traced). B1's DUMMY (624x614) does not match its
bounds (714x760).

## Icon tables (World/Data/Icon_<city>_Client.tdf, loaded by 0x709440)
Files: Icon_Moonpalace, Icon_Koinonia, Icon_Craspeda (Cras), Icon_Oros, Icon_Taipei (0x87a1c4..0x87a250); the archive
has `data\world\data\` copies (6 columns) and older `data\` copies (5 columns; only source for Taipei).
`.tdf` layout: a BMP (100x70, its file size at offset 2), then at that offset a header
`u16 1, u16 4, u32 date (0x1f0807d7), u16 0, u16 2, u32 data end, u32 columns, u32 rows`, then rows*columns u32 cell
offsets (relative to the header) and the cells, NUL-terminated UTF-16LE strings.
Icon columns: id ("post_3rd_00043"), name, "(x,y,z)" Gamebryo position, shown ("True"/"False"), int (always 0),
[empty]. Id prefixes: post_Nth (places, incl. "Station 1".."Station 4" = RQ houses and "*_rq" spots), post_Nth_y
(delivery service points), post_1st_WP (world patrol), post_1st_tQ / t00S / t2 (training), post_1st_HMUC (undercity),
yhe / yfe / harvest / xmas (events). No trigger radius is stored in the table.
RQHouseMent_<city>_Client.tdf: same container, 1 column, staff dialogue markup (`<pimg ..><name ..><text ..><br>`).

## Remake (DriftCityOffline)
`Runtime/Game/CityMapInfo.cs` holds the tables above, the zoom table, the icon-table reader (`Icons`, `Stations`,
`TdfRows`); `Runtime/Game/Minimap.cs` + `MinimapDisc.cs` + `Resources/DriftCityMinimapDisc.shader` draw the disc
exactly as above (50-vertex fan, uv0 map, uv1 mask alpha); `Menus.WorldMap` places the full-map marker on DUMMY.
`Tests/MinimapTests.cs` checks that the uv under the arrow and 400/400 traffic-lane midpoints land on drawn roads of
_cras.dds and writes Preview/minimap_cras*.png.
