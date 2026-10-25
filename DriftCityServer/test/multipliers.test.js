'use strict';
// Calendar multipliers (src/multipliers.js): golden time, Santa, the 3rd anniversary, through workshop.rewardExtras and
// event.state; the vshop period prices (src/vshop.js periodPrice).
const { test, before, after } = require('node:test');
const assert = require('node:assert');
const os = require('node:os');
const path = require('node:path');
const fs = require('node:fs');
const WebSocket = require('ws');
const { createServer, PROTOCOL } = require('../server');
const M = require('../src/multipliers');
const { rewards } = require('../src/economy');

test('golden hours: parsing, wrap-around, off', () => {
  assert.deepStrictEqual(M.parseHours('20-22'), { from: 20, to: 22 });
  assert.deepStrictEqual(M.parseHours(undefined), { from: 20, to: 22 }, 'default 20-22');
  assert.strictEqual(M.parseHours('off'), null);
  assert.deepStrictEqual(M.parseHours('nonsense'), { from: 20, to: 22 });
  const h = M.parseHours('22-2');
  assert.ok(M.inHours(23, h) && M.inHours(1, h) && !M.inHours(2, h) && !M.inHours(21, h));
  assert.ok(M.inHours(13, M.parseHours('0-24')), '0-24 = all day');
  assert.ok(!M.inHours(20, null));
});

test('bonus: golden time on race / chase / undercity, Santa on race / chase, anniversary on all but combo', () => {
  const at = (s, hour) => { const d = new Date(s); d.setHours(hour, 30, 0, 0); return d; };
  const h = M.parseHours('20-22');
  // golden time only
  assert.deepStrictEqual(M.bonus('race', [], at('2026-10-24T12:00:00Z', 20), h), { exp: 50, mito: 50, flags: 8 });
  assert.deepStrictEqual(M.bonus('undercity', [], at('2026-10-24T12:00:00Z', 21), h), { exp: 50, mito: 50, flags: 8 });
  assert.deepStrictEqual(M.bonus('quest', [], at('2026-10-24T12:00:00Z', 20), h), { exp: 0, mito: 0, flags: 0 });
  assert.deepStrictEqual(M.bonus('race', [], at('2026-10-24T12:00:00Z', 19), h), { exp: 0, mito: 0, flags: 0 });
  // Santa: 25 % in the season, 50 % on Dec 24-25 (UTC), EXP only
  assert.deepStrictEqual(M.bonus('chase', ['christmas'], new Date('2026-12-10T12:00:00Z'), null), { exp: 25, mito: 0, flags: 0x200 });
  assert.deepStrictEqual(M.bonus('race', ['christmas'], new Date('2026-12-24T12:00:00Z'), null), { exp: 50, mito: 0, flags: 0x400 });
  assert.deepStrictEqual(M.bonus('quest', ['christmas'], new Date('2026-12-24T12:00:00Z'), null), { exp: 0, mito: 0, flags: 0 });
  // the anniversary: x3 on every reward but the combo drop
  assert.deepStrictEqual(M.bonus('quest', ['anniversary'], new Date('2026-06-02T12:00:00Z'), null), { exp: 200, mito: 200, flags: 0x800 });
  assert.deepStrictEqual(M.bonus('combo', ['anniversary'], new Date('2026-06-02T12:00:00Z'), null), { exp: 0, mito: 0, flags: 0 });
  // everything at once
  const all = M.bonus('race', ['christmas', 'anniversary'], at('2026-12-25T12:00:00Z', 20), h);
  assert.deepStrictEqual(all, { exp: 300, mito: 250, flags: 8 | 0x400 | 0x800 });
  const st = M.state(['anniversary'], at('2026-06-03T12:00:00Z', 20), h);
  assert.strictEqual(st.golden, true); assert.strictEqual(st.goldenHours, '20-22'); assert.strictEqual(st.anniversary, 200); assert.strictEqual(st.flags, 8 | 0x800);
});

// ---------------- wire: a reward with golden time all day and the anniversary ----------------

let server, port, dir;
before(async () => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), 'dcmul-'));
  server = createServer({ port: 0, db: path.join(dir, 'mul.db'), quiet: true, gamedata: null, events: 'anniversary', golden: '0-24', maxConnPerIp: 50 });
  port = await server.listen();
});
after(async () => { await server.close(); fs.rmSync(dir, { recursive: true, force: true }); });

function connect() {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(`ws://127.0.0.1:${port}`);
    const pending = new Map();
    let rid = 1;
    ws.on('message', raw => {
      const m = JSON.parse(raw);
      if (m.t === 'welcome') resolve(c);
      if (m.t === 'reply' && pending.has(m.rid)) { pending.get(m.rid)(m); pending.delete(m.rid); }
    });
    ws.on('error', reject);
    const c = {
      req(t, body = {}) { const id = rid++; return new Promise((r, j) => { const tm = setTimeout(() => j(new Error('no reply to ' + t)), 4000); pending.set(id, x => { clearTimeout(tm); r(x); }); ws.send(JSON.stringify({ t, rid: id, ...body })); }); },
      close() { ws.close(); },
    };
  });
}

test('wire: the chase reward gets golden time + the anniversary; event.state carries the multipliers', async () => {
  const c = await connect();
  assert.ok((await c.req('register', { protocol: PROTOCOL, user: 'mul_a', pass: 'secret123' })).ok);
  assert.ok((await c.req('driver.create', { name: 'Golden', car: 0, color: 1 })).ok);
  assert.ok((await c.req('enter', { city: 'koinonia' })).ok);
  const st = await c.req('event.state');
  assert.ok(st.ok);
  assert.strictEqual(st.multipliers.golden, true);
  assert.strictEqual(st.multipliers.anniversary, 200);
  assert.strictEqual(st.multipliers.flags & 0x808, 0x808);
  const r = await c.req('reward', { kind: 'chase', data: { seconds: 40, boss: false } });
  assert.ok(r.ok, r.error);
  const base = rewards.chase(1, false);
  // +50 % golden time, +200 % anniversary (no licence, drink or party at level 1)
  assert.strictEqual(r.exp, base.exp + Math.floor(base.exp * 250 / 100));
  assert.strictEqual(r.mito, base.mito + Math.floor(base.mito * 250 / 100));
  c.close();
});

// ---------------- custom shop period prices (src/vshop.js) ----------------

test('period parts: sweepParts takes expired parts off and unfits them; grantPart extends and makes permanent', () => {
  const { grantPart, sweepParts } = require('../src/vshop');
  const car = { Parts: [], GiftParts: [], Wing: -1 };
  const day = 86400000;
  assert.ok(grantPart(car, 501, 'wing', 7, 1000));
  assert.deepStrictEqual([car.Parts, car.TimedParts, car.TimedUntil, car.GiftParts, car.Wing], [[501], [501], [1000 + 7 * day], [501], 501]);
  assert.ok(grantPart(car, 501, 'wing', 30, 2000), 'more days on a period part');
  assert.strictEqual(car.TimedUntil[0], 1000 + 37 * day);
  const p = { Cars: [car] };
  assert.deepStrictEqual(sweepParts(p, 1000 + 36 * day), []);
  assert.deepStrictEqual(sweepParts(p, 1000 + 37 * day), [501]);
  assert.deepStrictEqual([car.Parts, car.TimedParts, car.GiftParts, car.Wing], [[], [], [], -1]);
  assert.ok(grantPart(car, 502, 'wing', 0, 0));
  assert.strictEqual(grantPart(car, 502, 'wing', 7, 0), false, 'already permanent');
});

test('wire: shop.buyPeriod charges the period price, extends, sells unlimited at 3 x the 30-day price; mileage takes the period', async () => {
  const c = await connect();
  assert.ok((await c.req('register', { protocol: PROTOCOL, user: 'mul_b', pass: 'secret123' })).ok);
  assert.ok((await c.req('driver.create', { name: 'Periods', car: 0, color: 1 })).ok);
  assert.ok((await c.req('enter', { city: 'koinonia' })).ok);
  const live = server.world.findByName('Periods');
  live.profile.Mito = 1000000;
  const periods = [[7, 5000], [30, 15000], [0, 45000]];
  let r = await c.req('shop.buyPeriod', { slot: 0, item: 12345, part: 'wing', days: 7, periods, price: 5000 });
  assert.ok(r.ok, r.error);
  assert.strictEqual(r.price, 5000);
  assert.strictEqual(r.driver.Mito, 1000000 - 5000);
  const car = r.driver.Cars[0];
  assert.deepStrictEqual([car.TimedParts, car.GiftParts.includes(12345), car.Wing], [[12345], true, 12345]);
  const first = car.TimedUntil[0];
  r = await c.req('shop.buyPeriod', { slot: 0, item: 12345, part: 'wing', days: 30, periods });
  assert.ok(r.ok, r.error);
  assert.strictEqual(r.driver.Cars[0].TimedUntil[0], first + 30 * 86400000, 'the days are added');
  assert.match((await c.req('shop.buyPeriod', { slot: 0, item: 12345, part: 'wing', days: 90, periods })).error, /not sold for that period/);
  r = await c.req('shop.buyPeriod', { slot: 0, item: 12345, part: 'wing', days: 0, periods });
  assert.ok(r.ok, r.error);
  assert.strictEqual(r.price, 45000, 'unlimited = 3 x 15,000');
  assert.deepStrictEqual([r.driver.Cars[0].TimedParts, r.driver.Cars[0].GiftParts.includes(12345)], [[], false]);
  assert.match((await c.req('shop.buyPeriod', { slot: 0, item: 12345, part: 'wing', days: 7, periods })).error, /already owned/);
  // mileage: the 7-day period of another part costs a tenth of 5,000
  live.profile.Mileage = 1000;
  r = await c.req('shop.mileage', { slot: 0, item: { kind: 'vpart', item: 12346, slot: 'wing', days: 7, periods, price: 5000 } });
  assert.ok(r.ok, r.error);
  assert.strictEqual(r.mileage, 500);
  assert.deepStrictEqual(r.driver.Cars[0].TimedParts, [12346]);
  // expiry: the server takes it off when the time is up
  live.profile.Cars[0].TimedUntil[0] = Date.now() - 1;
  r = await c.req('shop.partsExpire');
  assert.ok(r.ok);
  assert.deepStrictEqual(r.expired, [12346]);
  assert.ok(!r.driver.Cars[0].Parts.includes(12346));
  c.close();
});
