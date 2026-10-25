'use strict';
// The NDLG parts page's second row and option slots, and custom marks (src/carparts.js; DriftCity_RE/INVENTORY.md,
// PHYSICS.md section 13).
const { test, before, after } = require('node:test');
const assert = require('node:assert');
const os = require('node:os');
const path = require('node:path');
const fs = require('node:fs');
const zlib = require('node:zlib');
const WebSocket = require('ws');
const { createServer, PROTOCOL } = require('../server');
const carparts = require('../src/carparts');

let server, port, dir;

before(async () => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), 'dccarparts-'));
  server = createServer({ port: 0, db: path.join(dir, 'test.db'), quiet: true, maxConnPerIp: 200, gamedata: null });
  port = await server.listen();
  carparts.defineOption({ id: 'op_A_test', kind: 'op_A', level: 1, points: 6 });
  carparts.defineOption({ id: 'op_F_test', kind: 'op_F', level: 1, points: 0 });
  carparts.defineOption({ id: 'op_S_high', kind: 'op_S', level: 90, points: 9 });
});
after(async () => { await server.close(); fs.rmSync(dir, { recursive: true, force: true }); });

function connect() {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(`ws://127.0.0.1:${port}`);
    const pending = new Map(), pushed = [];
    let rid = 1;
    ws.on('message', raw => {
      const m = JSON.parse(raw);
      if (m.t === 'reply' && pending.has(m.rid)) { pending.get(m.rid)(m); pending.delete(m.rid); return; }
      pushed.push(m);
    });
    ws.on('error', reject);
    const c = {
      pushed,
      req(t, body = {}) { const id = rid++; return new Promise(r => { pending.set(id, r); ws.send(JSON.stringify({ t, rid: id, ...body })); }); },
      close() { ws.close(); },
    };
    ws.on('open', () => { const iv = setInterval(() => { if (pushed.some(m => m.t === 'welcome')) { clearInterval(iv); resolve(c); } }, 5); });
  });
}

async function driver(user, name) {
  const c = await connect();
  assert.ok((await c.req('register', { protocol: PROTOCOL, user, pass: 'secret123' })).ok);
  assert.ok((await c.req('driver.create', { name, car: 0, color: 1 })).ok);
  return c;
}
const live = name => server.world.findByName(name);

/** A w x h RGBA PNG (one colour). */
function png(w, h) {
  const crcTable = Array.from({ length: 256 }, (_, n) => { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; return c >>> 0; });
  const crc = b => { let c = 0xffffffff; for (const x of b) c = crcTable[(c ^ x) & 0xff] ^ (c >>> 8); return (c ^ 0xffffffff) >>> 0; };
  const chunk = (type, data) => { const len = Buffer.alloc(4); len.writeUInt32BE(data.length); const td = Buffer.concat([Buffer.from(type, 'ascii'), data]); const c = Buffer.alloc(4); c.writeUInt32BE(crc(td)); return Buffer.concat([len, td, c]); };
  const ihdr = Buffer.alloc(13); ihdr.writeUInt32BE(w, 0); ihdr.writeUInt32BE(h, 4); ihdr[8] = 8; ihdr[9] = 6;
  const raw = Buffer.alloc((w * 4 + 1) * h);
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) raw.writeUInt32BE(0xff8800ff, y * (w * 4 + 1) + 1 + x * 4);
  return Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), chunk('IHDR', ihdr), chunk('IDAT', zlib.deflateSync(raw)), chunk('IEND', Buffer.alloc(0))]);
}

test('second row: a part of its stat goes in, the bag part can not be fitted twice, unfit returns the option', async () => {
  const c = await driver('rowuser', 'RowUser');
  const p = live('RowUser').profile;
  p.PerfParts = [{ Uid: 1, Type: 1, Grade: 1, Enhance: 0 }, { Uid: 2, Type: 1, Grade: 0, Enhance: 0 }];
  p.Cars[0].PerfSlots = [-1, 2, -1, -1];
  let r = await c.req('parts.fit2', { slot: 0, uid: 2 });
  assert.strictEqual(r.ok, false, 'a part fitted in row 1 is refused');
  r = await c.req('parts.fit2', { slot: 0, uid: 1 });
  assert.ok(r.ok, r.error);
  assert.deepStrictEqual(r.driver.Cars[0].PerfSlots2, [-1, 1, -1, -1]);
  // an option part on the row-2 accel part (index 1 * 2 + 1 = 3)
  p.Items.push({ Id: 'op_A_test', Count: 1 });
  r = await c.req('opt.fit', { slot: 0, index: 3, id: 'op_A_test' });
  assert.ok(r.ok, r.error);
  assert.strictEqual(r.driver.Cars[0].OptSlots[3], 'op_A_test');
  assert.ok(!r.driver.Items.some(i => i.Id === 'op_A_test'), 'the stack left the bag');
  r = await c.req('parts.unfit2', { slot: 0, type: 1 });
  assert.ok(r.ok, r.error);
  assert.strictEqual(r.driver.Cars[0].OptSlots[3], '', 'the option part came off with the part');
  assert.strictEqual(r.driver.Items.find(i => i.Id === 'op_A_test').Count, 1);
  c.close();
});

test('option slots: stat letter, part under it, level; F fits anywhere; remove gives it back', async () => {
  const c = await driver('optuser', 'OptUser');
  const p = live('OptUser').profile;
  p.PerfParts = [{ Uid: 7, Type: 0, Grade: 2, Enhance: 0 }];
  p.Cars[0].PerfSlots = [7, -1, -1, -1];
  p.Items.push({ Id: 'op_A_test', Count: 1 }, { Id: 'op_F_test', Count: 1 }, { Id: 'op_S_high', Count: 1 });
  let r = await c.req('opt.fit', { slot: 0, index: 0, id: 'op_A_test' });
  assert.strictEqual(r.ok, false, 'an accel option does not go on the speed slot');
  r = await c.req('opt.fit', { slot: 0, index: 2, id: 'op_F_test' });
  assert.strictEqual(r.ok, false, 'no part in the accel slot');
  r = await c.req('opt.fit', { slot: 0, index: 0, id: 'op_S_high' });
  assert.strictEqual(r.ok, false, 'level 90 needed');
  r = await c.req('opt.fit', { slot: 0, index: 0, id: 'op_F_test' });
  assert.ok(r.ok, r.error);
  r = await c.req('opt.remove', { slot: 0, index: 0 });
  assert.ok(r.ok, r.error);
  assert.strictEqual(r.driver.Cars[0].OptSlots[0], '');
  assert.strictEqual(r.driver.Items.find(i => i.Id === 'op_F_test').Count, 1);
  c.close();
});

test('custom marks: PNG only, 128 px and 32 KB at most, others fetch them by name', async () => {
  const a = await driver('markuser', 'MarkUser');
  const b = await driver('markview', 'MarkView');
  let r = await a.req('mark.set', { png: Buffer.from('not a png at all, just text').toString('base64') });
  assert.strictEqual(r.ok, false);
  assert.match(r.error, /PNG/);
  r = await a.req('mark.set', { png: png(256, 64).toString('base64') });
  assert.strictEqual(r.ok, false);
  assert.match(r.error, /128/);
  r = await a.req('mark.set', { png: 'A'.repeat(50 * 1024) });
  assert.strictEqual(r.ok, false);
  assert.match(r.error, /too large/);
  const img = png(128, 128);
  r = await a.req('mark.set', { png: img.toString('base64') });
  assert.ok(r.ok, r.error);
  assert.strictEqual(r.driver.CustomMarkVer, 1);
  assert.strictEqual(r.driver.CustomMark, '', 'the profile keeps only the version');
  const g = await b.req('mark.get', { name: 'MarkUser' });
  assert.ok(g.ok);
  assert.strictEqual(g.ver, 1);
  assert.deepStrictEqual(Buffer.from(g.png, 'base64'), img);
  r = await a.req('mark.clear');
  assert.ok(r.ok);
  assert.strictEqual((await b.req('mark.get', { name: 'markuser' })).png, '');
  assert.deepStrictEqual(carparts.pngSize(img), { w: 128, h: 128 });
  a.close(); b.close();
});
