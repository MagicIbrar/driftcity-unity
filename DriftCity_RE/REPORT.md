# DriftCity.exe: reverse-engineering notes

Target: `DriftCity.exe` (Drift City Remastered Alpha, 08-2025), 5,113,344 bytes,
SHA-256 `2007a80c365a1d1d0edc32641ae4c4ca04c5a6d363775cc724051b23c97095b0`.

Tooling: [REA 6.7.0](https://github.com/morluto/rea) on Ghidra 12.1.4 (headless) with JDK 21,
driven through REA's MCP server; plus pefile/capstone for raw scans. All addresses are
virtual addresses at the default image base `0x400000`. Ghidra found 15,890 functions
and 8,197 strings. Everything below is from static analysis; nothing was executed.

## 1. What the binary is

| Fact | Evidence |
|---|---|
| 32-bit PE GUI app, MSVC 7.1 (VS2003) | linker 7.10, `MSVCR71/MSVCP71` imports |
| Internal name **SkidRush** (Drift City's original Korean title) | PDB `f:\usa\Cebu_branch_usa\Dev\run\client\ReleaseDLL\skidrush.pdb`; export dir names `skidrush.exe`; mutex `Skidrush_mutex_9EC89FEE-F823-421…` |
| Built 2010-10-29 (PE timestamp) from the **US branch** | PDB path, `SOFTWARE\DriftCity` registry key |
| Engine: **Gamebryo 2.0** | 1,100+ imports from `NiMain/NiAnimation/NiDX9Renderer…20VC71.dll` |
| Audio: Miles (`mss32.dll`), Direct3D 9, DirectSound 8 | imports |
| **Dumped/unpacked** binary | rebuilt `.idata` at the end of the image, an unnamed BSS-like section, and a `.stolen` section that holds the obfuscated original entry prologue ("stolen bytes" restored by an unpacker) |

Sections: `.text 0x401000-0x859000`, `.data 0x859000-0x8bd000`, unnamed `0x8bd000-0x981000`,
`.rsrc`, `.edata` (one export: empty `DrawPivot` at `0x762e40`), `.stolen`, `.idata`.

## 2. Startup flow

`entry 0x82a8f4` (CRT) → **WinMain `0x4d4630`** ([decompiled](decompiled/4d4630_WinMain.c)).

- **Region**: one global `g_region @ 0x8d2e10`, read via `GetRegion() 0x4ccf20`. It is
  hardcoded to **1 (US)** in the config constructor `0x4c7d20`. Region values seen:
  0 = KR (Hangame), 1 = US, 2 = JP, 3 = TW (inferred from the hardcoded IPs and launcher DLLs); 4-10 are other licensees (TH/EU/ID builds; exact mapping not confirmed).
- **Launcher check** (per region). For US it is `0x4ea920`
  ([decompiled](decompiled/4ea920_us_runsw_check.c)), and unlike the other regions its result is
  not enforced:
  - `/dev` → dev flag (`this+0x2111`), `-alpha` → test-server flag (`this+0x2110`)
  - `-runsw <token>`: token must equal `MD5_hex(sprintf("%d", t - t % 300000))` with
    `t = timeGetTime()`, accepted for the current or previous 5-minute window. MD5 is at `0x4d6910`.
- Command-line flags: `ForceWindow`, `ForceFullScreen`, `OptimalOption`.
  Window mode is also read from `HKLM\SOFTWARE\DriftCity\EnableWindowMode` (US) or
  `HKLM\SOFTWARE\SkidRush\exe_window` (KR).
- Single-instance mutex only for regions 4-10.

## 3. Login server selection

`SelectAuthServer() 0x515580` ([decompiled](decompiled/515580_select_auth_server.c)), called from
the network init `0x519120`, which then connects with `Connect(addr, 0x961)` → **TCP port 2401**.

1. Default: `init/network.txt`, INI section `[Host]`, key `Auth`, default `10.12.14.75`.
2. Then overridden per region and server flavour (`IsTestServer 0x4ea770` / `IsLiveServer 0x4ea7b0`):

| Region | test | live |
|---|---|---|
| 0 KR | 119.205.253.65 | 222.122.195.150 |
| **1 US** | **188.166.2.168** | **188.166.2.168** |
| 2 JP | – | 61.206.174.148 |
| 3 TW | – | 210.242.70.191 / 61.93.187.77 |
| 4-10 | DNS lookup (`gethostbyname`, `0x512b20`) | same |

The two US entries (`0x86b974`, `0x86b984`) are the remaster's patch: both point to
`188.166.2.168`. The other regions still hold the original 2010 publisher IPs. Because the
region is fixed to US, the `network.txt` value is always overwritten.

## 4. Network layer

- Winsock wrapper class `BS_WinSocket` (`0x79f000-0x7b0000`): overlapped TCP
  (`WSARecv` `0x7a40c0`, `send` `0x7a4d50/0x7a4e20/0x7a4ed0`, `connect` `0x7adba0`) plus a
  separate **UDP** socket (`sendto 0x7a4480`, `recvfrom 0x7a4900`), probably peer-to-peer
  race sync (`/disableudprecv` switch exists).
- Receive path: completion vtable `0x87ee1c` → `OnRecv 0x7a45e0` → (decrypt) → framer →
  **dispatcher `0x7ac330`** ([decompiled](decompiled/7ac330_packet_dispatch.c)).

### 4.1 Message framing

After a 2-byte header, a buffer holds back-to-back messages, each starting with a
`u16 opcode` (`< 0x800`). `handler_table[opcode](session, msg)` returns the bytes it
consumed, and the dispatcher advances by that much. Opcodes with no handler log
`Wrong Command: %d`. A size mismatch logs `Wrong Packet Size. CMD(%hd)…` to
`DriftCity_Log/PacketAnalyzeLog*`.

### 4.2 Opcode table

`RegisterHandler(opcode, fn) = 0x79f390`; all **454 server→client handlers** are
registered in `0x519120` ([decompiled](decompiled/519120_register_handlers.c)). The full
list is in [`opcodes.txt`](opcodes.txt), each handler labelled by the strings it touches.
Examples:

| opcode | handler | hint |
|---|---|---|
| 0x017 / 0x051 | 0x4eef80 / 0x505d10 | server select UI |
| 0x080 | 0x4ef580 | `<LeaveChannelPkt>` |
| 0x08c / 0x08d | 0x4ef720 / 0x4ef7e0 | `<OpenChatPkt>` / `<CloseChatPkt>` |
| 0x093 / 0x094 | 0x502930 / 0x4ef9d0 | chat scopes (room/area/channel/team/party/server), whisper |
| 0x0ba-0x0c5 | 0x500d90… | HUV chase: propose, hit, result, health, boss timer |
| 0x0ff | 0x50e130 | world/area (Main, Cras, MoonPalace, Koinonia, Oros) |
| 0x122 / 0x128 | 0x504910 / 0x503380 | Virtual Chasing battle start / result |
| 0x12d | 0x4f1e00 | `JoinArea Ack` |
| 0x1b9 | 0x507380 | battle 2X/3X grade/level limits |

Numeric hints such as `10605` are `GlobalText` string IDs from the localization tables.

### 4.3 Stream encryption

`decompiled/stream_cipher.c` has the raw code; [`dc_cipher.py`](dc_cipher.py) is a tested
Python re-implementation (round-trip and MT19937 reference checks pass).

- **Handshake**: when the TCP connection opens, each side generates a random 56-byte hello
  (two 28-byte records `{iv0, iv1, key[4], mode}`, using MT19937 seeded with `GetTickCount`)
  and **sends it in clear** (`0x7a5260`). It then collects the peer's 56 bytes (`0x7a43a0`)
  and XORs the records together (`0x7a3e20`). Flag bit `2` also XORs in
  `0x400/0x300/800/600`. One side's config bits pick the mode for each direction.
- **Per-direction cipher** (`init 0x7ad920`, encrypt `0x7ad690`/`0x7ad580`, decrypt `0x7ad7f0`):
  - an 8-byte feedback register that holds **plaintext**;
  - an extra 8-byte mask per block from **MT19937** seeded with `init_by_array(key[4])`;
  - per byte: `out = in ^ reg[i] ^ mask[i]`;
  - every 8 bytes: mode 1 = XOR chain; mode 2 = **TEA** (32 rounds, `0x7ad480`) keyed with
    `{reg0, reg1, reg0, reg1}`.
- Consequence: both hellos cross the wire unencrypted, so anyone who can capture traffic can
  decrypt it. It's obfuscation, not real protection.

## 5. Anti-cheat and other security code

- **nProtect GameGuard** client library (NPGameLib, build string `Fri Jun 26 14:14:45 2009`)
  is statically linked at about `0x774000-0x77d000`. It has runtime-decrypted strings
  (`0x77c5c0`), `.erl` logs, and signature checks on GameGuard modules:
  - `0x77ac80`: reads a file's 16-byte trailer (magic `0x32812622 … 0x32812621`), hashes the
    file with MD5 through CryptoAPI and verifies the RSA signature with a public key embedded at
    `0x8c9aa4`. Then it compares the file name stored in the signed blob.
- The game reaches it through a "security" object (`g_security @ 0x8d25c0`, built in
  `0x4c4a60`) with region-specific vtables (`0x8623f4` default/US, `0x862418`, `0x86243c`).
  In the US vtable, the GameGuard start routine `0x4c4cb0` sits in slot +4, which nothing calls.
  The `Init` slot that WinMain calls (+8) is `0x6d1e20`, a bare `return 1`. **GameGuard never
  starts in this build**, which matches the missing `GameGuard\` folder.
  I can't tell from this file alone whether the remaster changed the vtable or the US build
  already shipped this way; that would need the original exe.
- Hangame KR auth (`HanAuthForClient.dll`, `HanReportForClient.dll`) is loaded only when region = 0.
- Patch archives: `\Patch\Patch.000-.010` and `\Patch\PatchMod.000` (loader near `0x4d35e3`).

## 6. The remaster's injected DLL (`HanDxWebForClient.dll`)

This is not the original Hangame DLL. It is a **Rust** DLL (crate `client`, project
"bibimbap", built with GitHub Actions; `HanDxWebForClient1.dll` is an older build
with a PDB path `D:\a\bibimbap\…\client.pdb`). The exe imports it by name
(`HanDxWebInit/Window/Billing/Term/GetLastError`), so it loads automatically. All five
exports point to one shared stub, and the real work happens in `DllMain`, which installs
`retour` detours from these modules:

| module | hooks / purpose (from strings) |
|---|---|
| `detours/network.rs` | `ws2_32!connect`: redirect/rewrite connections |
| `detours/machine.rs` | `HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid` for `driftcity.exe`: hardware ID |
| `detours/window.rs` | `SetWindowTextA`: window title |
| `detours/gdi.rs` | `gdi32full!GetCharABCWidthsI`: font metrics fix |
| `detours/randomizer.rs` | randomness override |
| `content.rs` | content/file handling (uses `glob`, `base64`) |

This DLL is where the remaster changes client behaviour at runtime. It is a separate binary,
and I haven't decompiled it here.

## 7. Files in this folder

- `REPORT.md`: this file
- `opcodes.txt`: all 454 opcode → handler mappings with string hints
- `dc_cipher.py`: stream cipher, handshake merge, MT19937, TEA (run it to self-test)
- `decompiled/*.c`: Ghidra pseudocode of the key functions
- `pe_info.txt`: sections, imports and version info

## 8. Reproducing

```powershell
# portable installs used here
$env:GHIDRA_INSTALL_DIR = "C:\Users\ibrah\tools\ghidra_12.1.4_PUBLIC"
$env:JAVA_HOME          = "C:\Users\ibrah\tools\jdk-21.0.12.1+1"
C:\Users\ibrah\tools\rea.ps1 decompile C:\Users\ibrah\tools\work\DriftCity.exe 0x515580 --provider ghidra
```

Each CLI call re-runs Ghidra's ~4-minute analysis. `C:\Users\ibrah\tools\rea-bridge.mjs`
keeps one REA MCP session alive on `127.0.0.1:47811` so repeated queries are instant.
Running `npx rea-agents setup` registers REA directly with Claude Code / other agents.
