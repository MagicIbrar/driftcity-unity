'use strict';
// Period items, set parts, restoration lubes and battle quick-slot items (src/items.js; DriftCity_RE/INVENTORY.md),
// and that trades keep period items with their owner and set parts their set.
const { test, before, after } = require('node:test');
const assert = require('node:assert');
const os = require('node:os');
const path = require('node:path');
const fs = require('node:fs');
const WebSocket = require('ws');
const { createServer, PROTOCOL } = require('../server');
const items = require('../src/items');

let server, port, dir;

before(async () => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), 'dcinv2-'));
  server = createServer({ port: 0, db: path.join(dir, 'test.db'), quiet: true, maxConnPerIp: 200, gamedata: null });
  port = await server.listen();
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

test('set parts table: Kayla\'s Alpha S, its expired form and a rental part', () => {
  const r = items.setRow('si_s01_003');
  if (!r) return;   // no game data on this machine
  assert.strictEqual(r.set, 'kayla');
  assert.strictEqual(r.type, 0);
  assert.strictEqual(r.hours, 720);
  assert.strictEqual(r.lube, 'i_d_set_ch01');
  assert.strictEqual(r.counterpart, 'si_s01_003_to');
  assert.ok(items.setRow('si_s01_003_to').expired);
  assert.strictEqual(items.setRow('r_01_001').hours, 168);
});

test('period stacks: the period adds up, discard is refused while it runs, the sweep takes them', () => {
  const p = { Items: [], PerfParts: [], Cars: [] };
  const t0 = 1_000_000;
  items.giveTimed(p, 'i_autoquick', 1, 7 * 24, t0);
  items.giveTimed(p, 'i_autoquick', 1, 7 * 24, t0 + 1000);
  const it = p.Items.find(x => x.Id === 'i_autoquick');
  assert.strictEqual(it.Count, 2);
  assert.strictEqual(it.Until, t0 + 14 * 24 * 3600e3, '11044: the second period is added');
  const r = items.discard(p, { kind: 'stack', id: 'i_autoquick', count: 1 }, t0 + 5000);
  assert.strictEqual(r.ok, false);
  assert.match(r.error, /expiration dates/);
  assert.deepStrictEqual(items.sweep(p, it.Until - 1), []);
  assert.deepStrictEqual(items.sweep(p, it.Until + 1), ['i_autoquick']);
  assert.strictEqual(p.Items.length, 0);
});

test('set parts online: fit starts the period, expiry turns the part into its _to form, a lube restores it', async () => {
  if (!items.setRow('si_s01_003')) return;
  const c = await driver('setuser', 'SetUser');
  const p = live('SetUser').profile;
  p.Level = 60;
  for (const id of ['si_s01_001', 'si_s01_002', 'si_s01_003', 'si_s01_004']) items.give(p, id, 1);
  for (const id of ['si_s01_003', 'si_s01_004', 'si_s01_001', 'si_s01_002']) {
    const r = await c.req('set.fit', { slot: 0, id });
    assert.ok(r.ok, r.error);
  }
  const d = (await c.req('item.expire')).driver;
  const car = d.Cars[0];
  const fitted = car.PerfSlots.map(u => d.PerfParts.find(x => x.Uid === u));
  assert.deepStrictEqual(fitted.map(x => x.Item), ['si_s01_003', 'si_s01_004', 'si_s01_002', 'si_s01_001'], 'one part per stat slot');
  assert.ok(fitted.every(x => x.Until > Date.now() + 719 * 3600e3), 'the 720 h period runs from the fit');
  assert.strictEqual(d.Items.filter(x => x.Id.startsWith('si_')).length, 0, 'the stacks became parts');
  // time is up for two of them
  const lp = live('SetUser').profile;
  lp.PerfParts[0].Until = Date.now() - 1;
  lp.PerfParts[1].Until = Date.now() - 1;
  const ex = await c.req('item.expire');
  assert.strictEqual(ex.expired.length, 2);
  assert.ok(ex.driver.PerfParts[0].Item.endsWith('_to') && ex.driver.PerfParts[0].Until === 0, 'expired form, no period');
  assert.strictEqual(ex.driver.Cars[0].PerfSlots.filter(u => u >= 0).length, 4, 'still fitted');
  // no lube yet; buy one, then use it
  assert.strictEqual((await c.req('set.restore', { slot: 0, lube: 'i_d_set_ch01' })).ok, false);
  lp.Mito = 30000;
  const buy = await c.req('set.buyLube', { lube: 'i_d_set_ch01' });
  assert.ok(buy.ok, buy.error);
  assert.strictEqual(buy.driver.Mito, 5000);
  assert.strictEqual((await c.req('set.restore', { slot: 0, lube: 'i_d_set_ch02' })).ok, false, 'a Beta lube does not restore Alpha sets');
  const rs = await c.req('set.restore', { slot: 0, lube: 'i_d_set_ch01' });
  assert.ok(rs.ok, rs.error);
  assert.strictEqual(rs.restored, 2);
  assert.ok(rs.driver.PerfParts.every(x => !x.Item.endsWith('_to') && x.Until > Date.now()), 'both are back for another period');
  assert.strictEqual(rs.driver.Items.filter(x => x.Id === 'i_d_set_ch01').length, 0, 'the lube is spent');
  // a rental part leaves the car and the bag when its time is up
  items.give(lp, 'r_01_001', 1);
  assert.ok((await c.req('set.fit', { slot: 0, id: 'r_01_001' })).ok);
  const rental = lp.PerfParts.find(x => x.Item === 'r_01_001');
  rental.Until = Date.now() - 1;
  const gone = await c.req('item.expire');
  assert.ok(!gone.driver.PerfParts.some(x => x.Item === 'r_01_001'));
  assert.strictEqual(gone.driver.Cars[0].PerfSlots[3], -1);
  c.close();
});

test('set.fit checks the level and the part', async () => {
  if (!items.setRow('si_s01_003')) return;
  const c = await driver('setlow', 'SetLow');
  const p = live('SetLow').profile;
  items.give(p, 'si_s01_003', 1);
  const low = await c.req('set.fit', { slot: 0, id: 'si_s01_003' });
  assert.strictEqual(low.ok, false);
  assert.match(low.error, /level/);
  assert.strictEqual((await c.req('set.fit', { slot: 0, id: 'si_nothing' })).ok, false);
  c.close();
});

test('battle quick-slot items are spent by item.use', async () => {
  const c = await driver('battleitems', 'BattleItems');
  const p = live('BattleItems').profile;
  items.give(p, 'i_n_00099', 2);
  const r = await c.req('item.use', { id: 'i_n_00099', slot: 0 });
  assert.ok(r.ok, r.error);
  assert.strictEqual(r.effect, 'battle');
  assert.strictEqual(r.driver.Items.find(x => x.Id === 'i_n_00099').Count, 1);
  c.close();
});

test('trade: period items cannot be offered', async () => {
  const a = await driver('periodA', 'PeriodA');
  const b = await driver('periodB', 'PeriodB');
  assert.ok((await a.req('enter', { city: 'cras' })).ok);
  assert.ok((await b.req('enter', { city: 'cras' })).ok);
  const pa = live('PeriodA').profile;
  items.giveTimed(pa, 'i_autoquick', 1, 24);
  const inv = await a.req('trade.request', { name: 'PeriodB' });
  assert.ok(inv.ok, inv.error);
  assert.ok((await b.req('trade.accept', { id: inv.id })).ok);
  const off = await a.req('trade.offer', { items: [{ kind: 'stack', id: 'i_autoquick', count: 1 }], mito: 0 });
  assert.strictEqual(off.ok, false);
  await a.req('trade.cancel');
  a.close(); b.close();
});
