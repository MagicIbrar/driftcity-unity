'use strict';
// Reader for Drift City "NayaPack" archives (.agt, Patch.###): the Node twin of the client's Core/NayaPack.cs.
//   0x00 "NayaPack", 0x10 u32 entryCount, 0x20 entry table then file data; everything after the 32-byte header is
//   XORed with a 90-byte key indexed by (absolute file position % 90).
//   entry = u32 dataOffset, u32 chunkCount, u32 size, u32 nameLength, name
//   data  = u16 compressedSize[chunkCount], then chunks that inflate (zlib) to 16 KiB each, the last to the remainder;
//           a chunk stored as-is when it isn't zlib or its size already equals the expected size
const fs = require('fs');
const path = require('path');
const zlib = require('zlib');

const KEY = Buffer.from('abSSgLNw5ER4pUAqBrcYMsC44NMNuhvzaVBk79YGe4Ms6FUYyYzfy8sKwhLC39QfvupxteRbZ5JDAGm64A4w9kWD8A', 'ascii');
const CHUNK = 0x4000;

const normalize = p => String(p).replace(/\//g, '\\').replace(/^\\+/, '').toLowerCase();

class NayaPack {
  constructor(file) {
    this.file = file;
    this.fd = fs.openSync(file, 'r');
    const header = this.raw(0, 0x20);
    if (header.toString('ascii', 0, 8) !== 'NayaPack') { fs.closeSync(this.fd); throw new Error(`${file}: not a NayaPack archive`); }
    const count = header.readUInt32LE(0x10);
    this.entries = new Map();
    let pos = 0x20;
    for (let i = 0; i < count; i++) {
      const f = this.decrypted(pos, 16); pos += 16;
      const nameLength = f.readUInt32LE(12);
      const name = normalize(this.decrypted(pos, nameLength).toString('latin1')); pos += nameLength;
      this.entries.set(name, { name, offset: f.readUInt32LE(0), chunks: f.readUInt32LE(4), size: f.readUInt32LE(8) });
    }
  }

  raw(pos, n) {
    const b = Buffer.alloc(n);
    let got = 0;
    while (got < n) { const r = fs.readSync(this.fd, b, got, n - got, pos + got); if (r <= 0) throw new Error(`${this.file}: unexpected end`); got += r; }
    return b;
  }

  decrypted(pos, n) {
    const b = this.raw(pos, n);
    for (let i = 0; i < n; i++) b[i] ^= KEY[(pos + i) % 90];
    return b;
  }

  read(name) {
    const e = this.entries.get(normalize(name));
    if (!e) throw new Error(`${name}: not in ${this.file}`);
    const out = Buffer.alloc(e.size);
    let pos = e.offset;
    const sizes = this.decrypted(pos, e.chunks * 2); pos += e.chunks * 2;
    let written = 0;
    for (let c = 0; c < e.chunks; c++) {
      const compressed = sizes.readUInt16LE(c * 2);
      const expected = Math.min(CHUNK, e.size - written);
      const block = this.decrypted(pos, compressed); pos += compressed;
      const isZlib = compressed >= 2 && block[0] === 0x78 && compressed !== expected;
      if (!isZlib) block.copy(out, written, 0, Math.min(compressed, expected));
      else {
        const data = zlib.inflateRawSync(block.subarray(2));
        if (data.length < expected) throw new Error(`${e.name}: chunk ${c} inflated to ${data.length}, expected ${expected}`);
        data.copy(out, written, 0, expected);
      }
      written += expected;
    }
    return out;
  }

  close() { try { fs.closeSync(this.fd); } catch { /* already closed */ } }
}

/** Every archive of an installed client, in the game's order (later archives and patches win), as GameFileSystem.cs. */
function mountGame(root) {
  const data = path.join(root, 'Data');
  if (!fs.existsSync(data)) throw new Error(`No Data folder under ${root}`);
  const list = (dir, re) => fs.existsSync(dir) ? fs.readdirSync(dir).filter(n => re.test(n)).sort((a, b) => a.toLowerCase().localeCompare(b.toLowerCase())).map(n => path.join(dir, n)) : [];
  const archives = [
    ...list(data, /\.agt$/i), ...list(path.join(data, 'World'), /\.agt$/i), ...list(path.join(data, 'GameUI'), /\.agt$/i),
    ...list(path.join(root, 'Patch'), /^Patch\./i), ...list(path.join(root, 'Patch'), /^PatchMod\./i),
  ];
  const index = new Map(), packs = [];
  for (const file of archives) {
    let pack;
    try { pack = new NayaPack(file); } catch { continue; }
    packs.push(pack);
    for (const e of pack.entries.values()) index.set(e.name, pack);
  }
  return {
    root, data, index, packs,
    read(name) {
      const key = normalize(name);
      const loose = path.join(data, ...key.split('\\'));
      if (fs.existsSync(loose)) return fs.readFileSync(loose);
      const pack = index.get(key);
      if (!pack) throw new Error(`${name}: not found`);
      return pack.read(key);
    },
    close() { for (const p of packs) p.close(); },
  };
}

module.exports = { NayaPack, mountGame, normalize };
