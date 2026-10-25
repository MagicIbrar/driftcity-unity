'use strict';
// Player-to-player trade (src/trade.js) and the bag items of src/items.js (item.use drinks / cheques, discard, sell).
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
  dir = fs.mkdtempSync(path.join(os.tmpdir(), 'dctrade-'));
  server = createServer({ port: 0, db: path.join(dir, 'test.db'), quiet: true, maxConnPerIp: 200, gamedata: null });
  port = await server.listen();
});
after(async () => { await server.close(); fs.rmSync(dir, { recursive: true, force: true }); });

function connect() {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(`ws://127.0.0.1:${port}`);
    const pending = new Map(), pushed = [], waiters = [];
    let rid = 1;
    ws.on('message', raw => {
      const m = JSON.parse(raw);
      if (m.t === 'reply' && pending.has(m.rid)) { pending.get(m.rid)(m); pending.delete(m.rid); return; }
      const w = waiters.find(x => x.pred(m));
      if (w) { waiters.splice(waiters.indexOf(w), 1); clearTimeout(w.timer); w.resolve(m); } else pushed.push(m);
    });
    ws.on('error', reject);
    const c = {
      ws,
      req(t, body = {}) { const id = rid++; return new Promise(r => { pending.set(id, r); ws.send(JSON.stringify({ t, rid: id, ...body })); }); },
      wait(pred, ms = 3000) {
        const hit = pushed.find(pred);
        if (hit) { pushed.splice(pushed.indexOf(hit), 1); return Promise.resolve(hit); }
        return new Promise((res, rej) => { const w = { pred, resolve: res }; waiters.push(w); w.timer = setTimeout(() => { waiters.splice(waiters.indexOf(w), 1); rej(new Error('timeout: ' + pred)); }, ms); });
      },
      drain(t) { for (let i = pushed.length - 1; i >= 0; i--) if (pushed[i].t === t) pushed.splice(i, 1); },
      close() { ws.close(); },
    };
    ws.on('open', () => c.wait(m => m.t === 'welcome').then(() => resolve(c)));
  });
}

async function driver(user, name) {
  const c = await connect();
  assert.ok((await c.req('register', { protocol: PROTOCOL, user, pass: 'secret123' })).ok);
  assert.ok((await c.req('driver.create', { name, car: 0, color: 1 })).ok);
  return c;
}
const live = name => server.world.findByName(name);

test('items: drinks, cheques, repair, discard and sell', async () => {
  const c = await driver('itemuser', 'ItemUser');
  const p = live('ItemUser').profile;
  items.give(p, 'i_d_drk_01', 2); items.give(p, 'i_d_m_50k', 1); items.give(p, 'i_d_repair01', 1); items.give(p, 'i_n_00003', 3);
  p.PerfParts.push({ Uid: 77, Type: 0, Grade: 1, Enhance: 2 });

  let r = await c.req('item.use', { id: 'i_d_drk_01', slot: 0 });
  assert.ok(r.ok, r.error);
  assert.deepStrictEqual(r.driver.Buffs.map(b => b.Id), ['exp']);
  r = await c.req('item.use', { id: 'i_d_drk_01', slot: 0 });
  assert.strictEqual(r.ok, false, 'one EXP drink at a time (11059)');
  assert.match(r.error, /Special Drink/);
  // the drink adds 30 % to chase EXP (not to quests)
  const extra = require('../src/workshop').rewardExtras(p, 'chase', { exp: 1000, mito: 1000 }, {}, () => 0.99, null);
  assert.strictEqual(extra.exp, 300);

  const before = p.Mito;
  r = await c.req('item.use', { id: 'i_d_m_50k', slot: 0 });
  assert.ok(r.ok);
  assert.strictEqual(r.driver.Mito, before + 50000);
  r = await c.req('item.use', { id: 'i_d_repair01', slot: 0 });
  assert.ok(r.ok);
  assert.strictEqual(r.effect, 'repair');
  assert.ok(!r.driver.Items.some(i => i.Id === 'i_d_repair01'));

  r = await c.req('item.discard', { kind: 'stack', id: 'i_n_00003', count: 2 });
  assert.ok(r.ok);
  assert.strictEqual(r.driver.Items.find(i => i.Id === 'i_n_00003').Count, 1);
  r = await c.req('item.discard', { kind: 'stack', id: 'i_n_00003', count: 5 });
  assert.strictEqual(r.ok, false, "can't throw away more than you have");
  const m0 = p.Mito;
  r = await c.req('item.sell', { kind: 'part', uid: 77 });
  assert.ok(r.ok, r.error);
  assert.strictEqual(r.driver.Mito, m0 + Math.floor(32000 * 1.2));
  assert.ok(!r.driver.PerfParts.some(x => x.Uid === 77));
  r = await c.req('item.sell', { kind: 'stack', id: 'modipart_piece', count: 1 });
  assert.strictEqual(r.ok, false);
  c.close();
});

test('trade: request, offers, lock, confirm and an atomic swap', async () => {
  const a = await driver('trader1', 'TraderA');
  const b = await driver('trader2', 'TraderB');
  assert.ok((await a.req('enter', { city: 'cras' })).ok);
  // not in the same channel yet
  let r = await a.req('trade.request', { name: 'TraderB' });
  assert.strictEqual(r.ok, false);
  assert.ok((await b.req('enter', { city: 'cras' })).ok);

  const pa = live('TraderA').profile, pb = live('TraderB').profile;
  items.give(pa, 'i_n_00003', 5);
  pa.PerfParts.push({ Uid: 5, Type: 2, Grade: 1, Enhance: 3 }, { Uid: 6, Type: 0, Grade: 0, Enhance: 0 });
  pa.Cars[0].PerfSlots[0] = 6;   // fitted: not tradeable
  items.give(pb, 'mitron_cristal', 2);
  const mitoA = pa.Mito, mitoB = pb.Mito;

  r = await a.req('trade.request', { name: 'TraderB' });
  assert.ok(r.ok, r.error);
  const inv = await b.wait(m => m.t === 'trade.invite');
  assert.strictEqual(inv.from, 'TraderA');
  r = await b.req('trade.accept', { id: inv.id });
  assert.ok(r.ok, r.error);
  await a.wait(m => m.t === 'trade.open');
  await b.wait(m => m.t === 'trade.open');
  // a second trade while this one is open is refused
  const c3 = await driver('trader3', 'TraderC');
  assert.ok((await c3.req('enter', { city: 'cras' })).ok);
  r = await c3.req('trade.request', { name: 'TraderA' });
  assert.strictEqual(r.ok, false);

  r = await a.req('trade.offer', { items: [{ kind: 'part', uid: 6 }], mito: 0 });
  assert.strictEqual(r.ok, false, 'fitted part');
  r = await a.req('trade.offer', { items: [{ kind: 'stack', id: 'i_n_00003', count: 9 }], mito: 0 });
  assert.strictEqual(r.ok, false, 'more than owned');
  r = await a.req('trade.offer', { items: [{ kind: 'stack', id: 'i_n_00003', count: 3 }, { kind: 'part', uid: 5 }], mito: 1000 });
  assert.ok(r.ok, r.error);
  r = await b.req('trade.offer', { items: [{ kind: 'stack', id: 'mitron_cristal', count: 2 }], mito: 2500 });
  assert.ok(r.ok, r.error);
  const st = await a.wait(m => m.t === 'trade.state' && m.them.mito === 2500);
  assert.strictEqual(st.me.items.length, 2);
  assert.strictEqual(st.them.items[0].id, 'mitron_cristal');

  r = await a.req('trade.confirm');
  assert.strictEqual(r.ok, false, 'lock first');
  assert.ok((await a.req('trade.lock', { lock: true })).ok);
  assert.ok((await b.req('trade.lock', { lock: true })).ok);
  // changing the offer clears both locks
  assert.ok((await b.req('trade.offer', { items: [{ kind: 'stack', id: 'mitron_cristal', count: 2 }], mito: 2000 })).ok);
  r = await a.req('trade.confirm');
  assert.strictEqual(r.ok, false, 'locks were cleared by the new offer');
  assert.ok((await a.req('trade.lock', { lock: true })).ok);
  assert.ok((await b.req('trade.lock', { lock: true })).ok);
  r = await a.req('trade.confirm');
  assert.ok(r.ok && !r.done, 'first confirm waits for the other driver');
  r = await b.req('trade.confirm');
  assert.ok(r.ok && r.done, r.error);
  const closedA = await a.wait(m => m.t === 'trade.closed');
  assert.ok(closedA.done);
  await b.wait(m => m.t === 'trade.closed');

  assert.strictEqual(pa.Mito, mitoA - 1000 + 2000);
  assert.strictEqual(pb.Mito, mitoB - 2000 + 1000);
  assert.strictEqual(items.count(pa, 'i_n_00003'), 2);
  assert.strictEqual(items.count(pb, 'i_n_00003'), 3);
  assert.strictEqual(items.count(pa, 'mitron_cristal'), 2);
  assert.strictEqual(items.count(pb, 'mitron_cristal'), 0);
  assert.ok(!pa.PerfParts.some(x => x.Uid === 5));
  const got = pb.PerfParts.find(x => x.Type === 2 && x.Grade === 1);
  assert.ok(got && got.Enhance === 3, 'the part keeps its grade and enhance level');
  assert.ok(pa.PerfParts.some(x => x.Uid === 6), 'the fitted part stayed');
  // the swap was saved for both drivers
  assert.strictEqual(server.store.driver(server.store.account('trader2').id).profile.Mito, pb.Mito);
  a.close(); b.close(); c3.close();
});

test('trade: decline, cancel and a partner who leaves', async () => {
  const a = await driver('trader4', 'TraderD');
  const b = await driver('trader5', 'TraderE');
  assert.ok((await a.req('enter', { city: 'oros' })).ok);
  assert.ok((await b.req('enter', { city: 'oros' })).ok);
  let r = await a.req('trade.request', { name: 'TraderE' });
  let inv = await b.wait(m => m.t === 'trade.invite');
  assert.ok((await b.req('trade.decline', { id: inv.id })).ok);
  const dec = await a.wait(m => m.t === 'trade.closed');
  assert.strictEqual(dec.reason, 'declined');

  r = await a.req('trade.request', { name: 'TraderE' });
  inv = await b.wait(m => m.t === 'trade.invite');
  assert.ok((await b.req('trade.accept', { id: inv.id })).ok);
  assert.ok((await a.req('trade.cancel')).ok);
  const c1 = await b.wait(m => m.t === 'trade.closed');
  assert.strictEqual(c1.reason, 'cancelled');
  assert.ok(!c1.done);

  const pa = live('TraderD').profile;
  const mito = pa.Mito;
  r = await a.req('trade.request', { name: 'TraderE' });
  inv = await b.wait(m => m.t === 'trade.invite');
  assert.ok((await b.req('trade.accept', { id: inv.id })).ok);
  assert.ok((await a.req('trade.offer', { items: [], mito: 500 })).ok);
  b.close();
  const gone = await a.wait(m => m.t === 'trade.closed' && m.reason !== 'cancelled');
  assert.strictEqual(gone.reason, 'left');
  assert.strictEqual(pa.Mito, mito, 'nothing moved');
  assert.strictEqual(server.trades.active(live('TraderD')), false);
  a.close();
});
