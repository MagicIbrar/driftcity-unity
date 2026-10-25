# Notes (in-game mail), community window, whisper, MSGBOX — DriftCity.exe

Tags: **[exact]** = read from code or dialog data; **[inferred]** = deduced. Scratch work is in `social/nt/` (asm dumps `a_*.txt`, annotated decompiles `nt/dec/*.c`).
"game" = `thunk_FUN_00780510()` / `0x512ad0()` (player/session record). "UIM" = `0x4386b0()+0x160` (UI manager).
Event codes: `0x101` click, `0x401` checkbox toggled, `0x601` edit Enter, `0x602` edit changed, `0x2001` per-frame/draw. The sender control id is at `ctl+0x54`, the dialog id at `dlg+0x3c`. Control flag bits at `ctl+0x3c`: 2 = visible, 4 = enabled, 8 = highlight/blink.

Note on the "top functions" list: **0x5448f0 / 0x544020 are HELP / HELPADV (1920000/1930000) init and page select, and 0x540240 is VIDEOADV (2990000) settings init. None of them touch notes** [exact]. Ghidra has no function at 0x5448f0, 0x6117b0, 0x540240, 0x60e410, 0x610e10 (SEH-heavy); they were read from `text.asm`.

---------------------------------------------------------------------------------------------------

## 1. Which UI is live: two complete UIs, chosen by the main-UI type option

The client holds both UIs, and each one runs depending on the main-UI type: `(game+0x24c & 0x18)` (`0x10` = "ADV" UI; the same switch COMBO.md and MINIMAP.md describe).

| UI | Main bar | Notes UI | Code |
|---|---|---|---|
| ADV (new) | MAINBUTTON 3010000 (`ID_MAINBUTTON_ADV_*`) | COMM 3970000 + NOTE 4010000 (list) + NOTE 4020000 (read panel) | community class, vtable 0x872090, UIM+0x1f0 |
| basic (old) | MAINBUTTON 3020000 (`ID_MAINBUTTON_HOTKEY_COMM`, `ID_MAINBUTTON_NOTE_NOTICE1-3`) | MSG 2960000 inbox / 2970000 saved / 2950000 read | inbox class vtable 0x873e4c (UIM+0x1e0), saved class vtable 0x873f4c (UIM+0x1e4) |
| both | — | **2940000 send-note dialog is shared** | send class vtable 0x873eb8 (UIM+0x1e8) |

Dialog id constants in code [exact]:
* 3970000/3980000/4000000/4010000/4020000/3990000/2790000/3230000: loaded and their handlers registered in the community init **0x6117b0** (vtable[1]). The registration table at 0x530690 also lists all of them.
* 2950000+2960000: old inbox init 0x66e260, handlers 0x66f510 (2960000) and 0x66fa00 (2950000), list fill 0x66e5a0.
* 2950000+2970000: old saved-box init 0x671ad0, handlers 0x6724b0 (2970000) and 0x672770 (2950000), list fill 0x671d50.
* 2940000: send init 0x6701b0, handler 0x6713a0, send action 0x670c50, open/reset 0x66ff40.
* Every note packet handler refreshes **all three** list views: 0x610760 (community), 0x66e5a0 (old inbox) and 0x671d50 (old saved), for example 0x501f10 and 0x502010 [exact].
* After a send or a send-dialog close, the client returns to the community window when `game+0x24c & 0x18` is set, otherwise to the old inbox (origin 1) or old saved box (origin 2) (0x671228..0x671285 and 0x671453..0x6714b1) [exact].

### Community frame COMM 3970000 (community class, vtable 0x872090)
vtable [exact]: [0]=0x613c00 dtor, [1]=0x6117b0 init, +0x28=0x60e410 COMM handler, +0x2c=0x615330 MY (crew page) handler, +0x30=0x614680 MYFRIEND 4000000 handler, +0x34=0x610e10 NOTE 4010000 handler, +0x38=0x6138c0 NOTE 4020000 handler, MYTEAM 3990000 → 0x6107c0, 2790000 → 0x606500, MSGBOX 3230000 → 0x60e520. vt+0x3c(show) and vt+0x40(isOpen).

* **Open** [exact]: main-bar `ID_MAINBUTTON_ADV_COMM` (control J=112 = **F1**), `ADV_NOTE_NOTICE1` and `ADV_NOTE_NOTICE2` all call 0x5ed580 (handler 0x5f0c40 at 0x5f11a1..0x5f12f5). The basic bar does the same: `HOTKEY_COMM` (J=112) and `NOTE_NOTICE1/2` call 0x5e7ea0 (0x5e90ae..0x5e90dd).
* 0x5ed580: if the community window is open it closes it (sound 0x4e8b). Otherwise it closes a long list of other windows (inventory, shops, events, send dialog, ...), plays sound 0x4e8a, opens the window (vt+0x3c(1)) and picks the **default tab**:
  * **NOTE (2)** if any inbox note has status 'N' (0x5ed3f0);
  * else **MYTEAM (0)** if `game+0x248 >= 0` (crew flag word) [exact test, meaning inferred = "in a crew"];
  * else **MYFRIEND (1)**.
* Tab switch **0x60a3a0(tab)** (current tab at `+0x5b8`) [exact]:
  * Tab 3 maps to 2.
  * Tab 0 without a crew (`0x6cb0c0()+0x52 < 0`) shows 10425 "You don't have a Crew".
  * It hides the 5 sub-dialogs and plays sound 0x4e8a.
  * Tab 0 → 0x606440 (crew page), tab 1 → 0x6083d0 (friend manager), tab 2 → shows NOTE 4010000, resets selection (`+0x5b0/+0x5b4/+0x564 = -1`) and calls 0x606e40(`+0x5bc`), which reopens the box that was shown last.
* COMM handler 0x60e410 [exact]:
  * The MYTEAM/MYFRIEND/NOTE tab buttons call 0x60a3a0(0/1/2) unless that tab is already shown.
  * CLOSE (J=27 Esc) sets `+0x5b8=3` and calls vt+0x3c(0).
  * The hidden `*_BTN` controls (J=9, **Tab key**) cycle to the next tab (+1 mod 3) and skip MYTEAM when the player has no crew.
* MY 3980000 (crew page; another agent covers it): controls are bound in 0x6117b0 (dialog at `+0x20`, PAGE00-09 at `+0x134..`, member slots from `+0x15c`). 0x615330 is the handler, **0x615170(mode)** sets the sub-page mode `+0x5e0` (0 = hide, 1/4 = member list via **0x614090**, 3 = board), and 0x606440 opens the tab.

---------------------------------------------------------------------------------------------------

## 2. Notes

### 2.1 Client storage [exact]
* Inbox: `std::map<__int64 id, Note>` at **game+0x3b538** (head `+0x3b53c`, size `+0x3b540`).
* Saved box: the same type at **game+0x3b544** (head `+0x3b548`, size `+0x3b54c`).
* Map node: key i64 at node+0x10, Note at node+0x18. The comparator is `less` (0x4ff190 = find), so iteration (the display order) is ascending by note id, i.e. oldest first [inferred].
* Note record = **0x278 bytes, packed**:

| off | size | field | source |
|---|---|---|---|
| 0x00 | i64 | ? (probably receiver char id) [inferred] | 0x084 +0x12 |
| 0x08 | i64 | **note id** (same as the map key) | 0x084 +0x02 |
| 0x10 | i64 | sender char id [inferred; used for friend notes] | 0x084 +0x0a |
| 0x18 | wchar[21] | **sender name** | 0x084 +0x1a |
| 0x42 | u32 | **type** (low word); 0x44 = subtype word | 0x084 +0x4c |
| 0x46 | char | **status**: 'N' new/unread, 'R' read, 'K' kept (saved box) | 0x084 +0x50 |
| 0x47 | wchar[21] | title (the new UI never shows it) | 0x084 +0x54 |
| 0x71 | wchar[201] | **content** | 0x084 +0xef (len at +0xeb) |
| 0x203 | i64 | **attached Mito** | 0x084 +0x7e |
| 0x20b | char[0x65] | **attachment / extra string** (item descriptor; event data for system types) | 0x084 +0x86 |
| 0x270 | u32 time_t | **send time** | 0x084 +0x44 |
| 0x274 | u32 time_t | **read time** (set when read) | 0x084 +0x48; 0x5c9 sets it to now |

### 2.2 Packets [exact]
Server → client:
* **0x084** (0x5008b0) "note": size 0xef + 2*len. Type 400 calls 0x51cd80 and is not queued. Type 500 adds Mito (+0x7e) to the player at once. Otherwise the note is copied into a ring queue (0x8d5c10) and processed by **0x500360**:
  * type 0 → stored: status 'K' goes to the saved box, any other status to the inbox (0x500424..0x50049d);
  * type 100 with subtype 0xee → friend notification 0x66cc00(sender);
  * type 300 → event or coupon reward, `sscanf(+0x20b,"%d %d %I64d %d %ld")`;
  * type 500 / other → popup with the content, and the next queued note shows after OK (0x4fdfb0);
  * type 800 → event dialog 0x694fe0;
  * for popup types, a status-'N' note is acknowledged through 0x799aa0(id,1).
  * After processing, all three list views refresh.
* **0x08a** (0x500c10): announcement. +2 kind: 2 = system chat line; else a queued popup with type 800/500/1. It also sets player Mito from +0x12.
* **0x5c9** (0x501df0) read ack: +2 result==1, +6 id → status 'R' (inbox) or 'K' (saved), read time = now.
* **0x5cb** (0x501f10) delete ack → 11031 "Message deleted.", the note is erased from whichever box holds it.
* **0x5cd** (0x502010) save ack → 11032 "Message saved.", the note moves inbox → saved. On failure: 11023.
* **0x5cf** (0x4f9040) send result: +2 receiver name, +0x2c result, +0x30 cost.
  * 1 → 11033 "The message has been sent to %s.", Mito -= cost.
  * 0 → 11038 "%s does not exist."
  * -2 → 11034 "This item cannot be sent as an attachment."
  * -3 → "DB Server Error!!"
  * -4 → 10500 "Insufficient Mito."
  * other → 11035 "%s cannot receive any messages at this time."
* **0x5d1** (0x502140) attachment taken: +2 result >= 0, +6 id, +0xe new 0x65-byte attachment block written over note+0x20b → 11036 "The item has been removed from the message."

Client → server:
* **0x5c8** read {i64 id} (0x78e7e0)
* **0x5ca** delete {i64 id} (0x78e940)
* **0x5cc** save {i64 id} (0x78eaa0)
* **0x5ce** send (0x78ec00, 0x29c bytes): +2 receiver (wcsncpy), +0x2c content (300 wchar buffer), +0x284..+0x298 = 6 dwords = attached item {count, inventory id} + 4 zero dwords [layout of the tail inferred]
* **0x5d0** take attachment {i64 id, u32 arg} (0x78edc0)
* 0x5d2 (0x798040, 18 bytes) is unidentified.

Debug chat commands in 0x5b8200 (Korean, delete/read/save/send the Nth note): `/쪽지삭제 N`, `/쪽지읽음 N`, `/쪽지보관 N`, `/쪽지보냄 name text n`.

### 2.3 NOTE 4010000 list (handler 0x610e10, refresh 0x60efa0 inbox / 0x60f5b0 saved) [exact]
* Members: CB `+0x3cc[10]`, NEW `+0x3f8[10]`, EVENT `+0x420[10]`, SENDER `+0x448`, SEND_DATE `+0x470`, FREE_TIME `+0x498`, PAGE `+0x4c0`, BTN rows `+0x4ec`, MSGBOX `+0x4e8`, SAVE_MSGBOX `+0x514`, CB_ALL `+0x3f4`, DELETE `+0x518`, SAVE `+0x51c`, LEFT `+0x520`, RIGHT `+0x524`.
* Box tabs: MSGBOX → 0x606e40(0) inbox, SAVE_MSGBOX → 0x606e40(1) saved box. This clears the checkboxes, sets page 1 and keeps the box in `+0x5bc`.
* Box labels "(unread/total)": format `"(%d/%d)"` (nation 2 uses a padded variant), unread counts from 0x60be80 (`+0x55c` inbox, `+0x560` saved).
* **Page size 10**, pages = ceil(count/10) with a minimum of 1 (`+0x558`), current page `+0x554` (1-based).
  * PAGE00..09 labels show "%d" page numbers.
  * PAGEn click → page n+1.
  * LEFT/RIGHT jump ±10 pages (page groups).
* Row content:
  * SENDER = the name string as stored. There is no client-side rename, so a system note shows whatever sender name the server put in it [inferred].
  * SEND_DATE = `"%d-%02d-%02d"` (YYYY-MM-DD, localtime of +0x270).
  * **FREE_TIME = time left before the note expires:**
    * unread 'N': `30 - floor((now - sendTime)/86400)` → 11087 "%d Day(s)";
    * read: `m = (now - readTime)/60`; if `1440 - m < 60` → 11089 "%d Minute(s)" (1440 - m), else 11088 "%d Hour(s)" (24 - m/60);
    * saved box: 11082 "Unlimited".
    * So unread notes last 30 days, read notes 24 h, saved notes never expire. The client only displays this; the server must do the deleting [inferred].
  * Unread row: NEW marker shown. If the attachment string contains "get_giftbox", the EVENT (Christmas gift) icon is shown instead.
  * The selected row is highlighted. Headers TAB_SEND_USER/TAB_DATE/TAB_FREE_TIME have no handler, so there is no sorting.
* Row click (BTN0n): select index `+0x564`, show the read panel 4020000 (0x606f20 resets the slots), then 0x60b3b0 (inbox) / 0x60b540 (saved):
  * store the id at `+0x5b0`;
  * parse the attachment into `+0x568` (0x606fa0; `+0x56c` = item ptr or 0);
  * set CONTENT = content;
  * if status 'N', send **0x5c8 read** (that is how "mark read" happens).
* Checkboxes (0x401): CB_ALL toggles all visible rows; a row toggle updates CB_ALL.
* **DELETE** (inbox 0x60b6d0/0x60b8b0, saved 0x60b7d0/0x60b9e0): only checked rows of the current page. If any checked note carries an item → prompt 11190 "There is message containing an item. Do you want to proceed?" (callbacks 0x60ef20/0x60ef50). Sends one 0x5ca per note.
* **SAVE** (0x60bb10): checked inbox rows. **Saved box capacity = 10**: if `checked > 10 - savedCount` → 11023 (the generic "Not enough space" text). Sends one 0x5cc per note.
* **SEND**: closes COMM, opens the 2940000 send dialog (0x66ff40, vt+0x2c(1)), origin `+0x44 = 1`.
* Inbox capacity: no client check. The 100 is only the old dialog placeholder "0 / 100". A full box is presumably the server's 11035 [inferred].

### 2.4 NOTE 4020000 read panel (handler 0x6138c0) [exact]
* CONTENT `+0x52c`, SLOT `+0x530[3]`, SLOTX `+0x53c[3]`, SLOT_UPGRADE ("+NN") `+0x548[3]`.
* **REPLY**: closes COMM, opens the send dialog with the receiver = sender of the selected note (0x5b80c0 index lookup → 0x66fd30), origin 1 (inbox) or 2 (saved).
* **DELETE**: if the note has an item → prompt 11185 "If you delete this message, the item in the message will also be deleted..." (callback 0x606da0); else hide the panel and send 0x5ca.
* **Taking the item** (0x60bca0, mouse on the slot):
  * if the item code (item+0x74) contains "get_giftbox" (gift-box event), it needs a free inventory slot (else 10450) and also calls 0x792340(0/1/2 for g_/h_/other);
  * then it sends **0x5d0**.
* One attachment block per note → only slot 1 can be filled. The 3 slots are layout leftovers [inferred].
* Attachment string format: `swscanf(L"%s %d %d %d %d %s %s %s %d")` (0x606fa0); the first token is the item code [field meaning inferred].

### 2.5 Send dialog MYFRIEND 2940000 (class vtable 0x873eb8) [exact]
* Members:
  * `+0x24` EDIT_RECIEVER, `+0x28` TXT_INFO, `+0x2c` EDIT_CONTENT, `+0x30` unnamed 2940016;
  * `+0x34` TXT_CNT, `+0x38` MITO (cost), `+0x3c` TEXTLEN, `+0x40` BTN_SEND;
  * item slots SLOT/SLOTX 01-03 at `+0x0c/+0x18`;
  * `+0x5c/+0x60` = attached item, `+0x64` = item count, `+0x44` = origin, `+0x54` = cost.
* Open 0x66ff40 clears the edits and sets the texts:
  * TXT_INFO = msgsend_info "Enter up to 10 names, separated by (,)";
  * 11076 "Cost:";
  * 11075 "/ 80 Characters" (TXT_CNT reads "1 Message(s) / 80 Characters").
* **Content limit 80 characters**: EDIT_CONTENT has `TL=80` in the dialog file. TEXTLEN shows the current MBCS byte length "%d" (0x670500, on change 0x602). The "40" is only a placeholder. The packet buffer holds 300 wchar.
* **Cost** (0x6704a0, recomputed when the receiver changes) = **50 Mito × recipients** (min 1); **1000** when an item is attached (`+0x60`) or `+0x58` is set. The 5000 is only a placeholder.
* Send action 0x670c50, checks in order:
  1. empty content → 11010;
  2. empty receiver / 0 parsed names → 11009 ("User does not exist");
  3. more than 1 recipient + item → 11012;
  4. cost > player Mito (char+0x46, i64) → 11013.
  * Names are split on ',' (';' for nation 2) and trimmed. Then: duplicate → 11037, own name → 11021, invalid name → 10285, more than 10 → 11022.
  * Then **one 0x5ce per recipient**, close, return to the origin window.
* **Receiver must exist: the server checks** (result 0 → 11038).
* Item drag/drop: 0x670a40. Rejects show 11018 "You cannot send this item"; a stack-count prompt shows 11011.

### 2.6 New-note notification on the main bar [exact]
0x5ecfc0 (ADV, from 0x5f1bd0) and 0x5e7970 (basic, from 0x5ea2d0) run every frame:
* While **any inbox note has status 'N'**:
  * timer `t += dt`;
  * NOTICE1 (ADV `+0x5c`) fades and slides in for t<1, is fully visible for t<3, fades out for t<4;
  * after that, NOTICE1 is hidden and the **COMM button blinks** (flag 8 toggled, ADV `+0x98` = ADV_COMM).
* With no unread notes: NOTICE1-3 are hidden, the timer resets and the blink stops.
* NOTICE2/3 are always hidden by this code.
* Clicking NOTICE1/2 opens COMM. Because COMM opens on the NOTE tab while unread notes exist, the click lands on the NOTE list.

### 2.7 System / reward notes
* Texts 11722/11724/11726/11728 ("...reward has been sent to your Note Box") are **not referenced by any literal in the exe** (only 11721/11723 in level-up handler 0x089 = 0x4fb280). The server must create those notes [inferred].
* The client gives a system note no special treatment except for its type (2.2) and the "get_giftbox" marker. Its sender column shows the stored sender name [inferred: server-chosen, e.g. "GM"/"System"].
* A status-'N' note of a popup type is acknowledged right after display.

---------------------------------------------------------------------------------------------------

## 3. Whisper (chat) [exact]
* Chat-line prefixes, from the jump table at 0x5b7a67 (byte map 0x5b7c90, targets 0x5b7c78):
  * `!` → channel;
  * `&` → party;
  * **`*` → whisper**;
  * `-` → private channel (`%35s`);
  * `@` → crew/team;
  * anything else → the current chat mode.
  * Slash commands: `/all `, `/crew `, `/party `, `/cjoin` `/csay` `/ckick` `/cauth` `/cwho` `/cleave`, `/block `, `/unblock ` (texts 10749-10751, 11160-11165, 11271-11272).
  * **There is no `/w`.**
* **Whisper syntax: `*Name message`.** 0x5bcf60 runs `swscanf("%15s")`, so the name is at most 15 chars, then the rest is the message. It is sent with **0x094** (0x782800): +2 target name (wcsncpy 20), +0x2e u16 byte length, +0x30 message (≤255 wchar).
* After sending, the chat edit buffer (`0x4e6d30()+0x50a`) is refilled with `"*Name "` (`%35s` + space), so you keep whispering the same person.
* Receive **0x094** (0x4ef9d0): +2 name, +0x2a mode, +0x2e len, +0x30 text → channel "whisper", colour 0xFFFF0000 → 0x5bd360:
  * mode 0 (echo to the sender) → **10474 "Whispering to [%s]: %s"**;
  * mode 1 → **10475 "Whispering from [%s]: %s"**, and it writes `"*%s "` (sender) into the edit buffer `+0x50a` = **reply to last whisperer** (the next chat line goes to them).
* Chat mode 5 = "whisper" (0x5bcdd0); mode 6 = "private".

## 4. MSGBOX 3230000 [exact]
A generic **name/text input prompt**: INFO text, TEXT edit (Enter = OK), OK/CANCEL, or a DELETE / FRIEND_ADD / FRIEND_REMOVE button variant. It is not chat. Users:
* community class (`+0x800`, handler 0x60e520): add friend or block a character by name. 10539 "Enter the character name.", own name → 11182, already blocked → 11183.
* recommend a character: 0x554240 (10180/10181).
* license / character delete confirm: front end 0x767240 / 0x769590.
* old friend UI 0x66d8c0, parts shop 0x61b410, events 0x5d38e0, and others (23 references).

## 5. Minimal JSON note for a server
```json
{ "id": 123456789, "type": 0, "status": "N",
  "senderId": 42, "sender": "Name(<=20)", "title": "", "content": "<=80 chars (buffer 200)",
  "mito": 0, "item": null,
  "sendTime": 1215000000, "readTime": 0 }
```
* `status`: N unread / R read / K saved (saved box = status K).
* `item`: null or { code, count, upgrade, ... } → packed into the 0x65-byte attachment string.
* `type` values: 0 normal, 100+0xee friend, 300 event reward, 500 Mito gift popup, 800 event popup.
* Box limits: saved 10 (client-enforced); inbox unbounded client-side.
* Expiry rules for the server: unread 30 d after sendTime, read 24 h after readTime, saved never.
* Flow:
  * login → push 0x084 per note;
  * 0x5c8 → set R + readTime, reply 0x5c9;
  * 0x5ca → 0x5cb;
  * 0x5cc → 0x5cd (status K);
  * 0x5ce → 0x5cf to the sender + 0x084 to an online receiver;
  * 0x5d0 → 0x5d1 + inventory add.
