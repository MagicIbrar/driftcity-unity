'use strict';
// The leftover event screens (src/events.js): GACHAINVEN parts / compound / badge sale, Pick'em, egg-basket gifts,
// the Indy4 props NPC, the Halloween Drink.
const { test, before, after } = require('node:test');
const assert = require('node:assert');
const os = require('node:os');
const path = require('node:path');
const fs = require('node:fs');
const WebSocket = require('ws');
const { createServer, PROTOCOL } = require('../server');
const { Events } = require('../src/events');
const { DEFAULT_DIR } = require('../src/gamedata');

const hasData = fs.existsSync(path.join(DEFAULT_DIR, 'init', 'rewardgroup.xlt'));
const seeded = seed => () => { seed = (seed * 1103515245 + 12345) & 0x7fffffff; return seed / 0x80000000; };
const profile = (extra = {}) => ({ Name: 'Tester', Level: 30, Exp: 0, Mito: 5000000, Items: [], Inventory: [], PerfParts: [], Cars: [], HuvCaught: 0, RacesEntered: 0, PvpRaces: 0, ...extra });
const cnt = (p, id) => (p.Items.find(i => i.Id === id) || { Count: 0 }).Count;
const add = (p, id, n = 1) => { const it = p.Items.find(i => i.Id === id); if (it) it.Count += n; else p.Items.push({ Id: id, Count: n }); };

test('GACHAINVEN: season car, compound of parts A-D + key, badge sale, capsule extras', { skip: !hasData && 'no game data' }, () => {
  const e = new Events({ seasons: 'all', rng: seeded(3) });
  assert.strictEqual(e.gachaCar(1), 'shadow'); assert.strictEqual(e.gachaCar(2), 'hunter'); assert.strictEqual(e.gachaCar(8), 'monza'); assert.strictEqual(e.gachaCar(10), 'shadow');
  assert.deepStrictEqual(e.gachaParts('shadow'), ['i_n_shadow_A', 'i_n_shadow_B', 'i_n_shadow_C', 'i_n_shadow_D']);
  assert.strictEqual(e.gachaKey('hunter', 3), 'i_n_hunter_k_v3_06');
  const p = profile(), s = { profile: p };
  add(p, 'i_n_shadow_k_v5_05');
  for (const x of ['A', 'B', 'C']) add(p, 'i_n_shadow_' + x);
  assert.match(e.handle(s, { t: 'gacha.compound', item: 'i_n_shadow_k_v5_05' }).error, /insufficient parts/);
  add(p, 'i_n_shadow_D');
  const r = e.handle({ profile: p }, { t: 'gacha.compound', item: 'i_n_shadow_k_v5_05' });
  assert.ok(r.ok, r.error); assert.strictEqual(r.grants[0].item, 'i_n_shadow_box_v5');
  assert.strictEqual(cnt(p, 'i_n_shadow_A'), 0); assert.strictEqual(cnt(p, 'i_n_shadow_k_v5_05'), 0); assert.strictEqual(cnt(p, 'i_n_shadow_box_v5'), 1);
  // the badge page's SELL (0x197): the item's resale price
  add(p, 'i_n_dcbadge_01', 2);
  const mito = p.Mito, sold = e.handle({ profile: p }, { t: 'gacha.sell', item: 'i_n_dcbadge_01', count: 2 });
  assert.ok(sold.ok, sold.error); assert.strictEqual(p.Mito, mito + sold.price); assert.strictEqual(cnt(p, 'i_n_dcbadge_01'), 0);
  assert.strictEqual(e.handle({ profile: p }, { t: 'gacha.sell', item: 'i_n_00003', count: 1 }).ok, false);
  // capsules sometimes add a Vehicle Part / Key Parts of the season car
  let extras = 0;
  for (let i = 0; i < 400; i++) extras += e.gachaBonus().length;
  assert.ok(extras > 20 && extras < 120, `extras ${extras}`);
});

test('Pick\'em grade: 1 + gold 3 / silver 2 / bronze 1 at the picks\' places, max 10; the layout', () => {
  const res = [{ name: 'a', place: 1 }, { name: 'b', place: 2 }, { name: 'c', place: 3 }, { name: 'd', place: 4 }, { name: 'e', place: 0 }];
  assert.strictEqual(Events.pickemGrade([1, 2, 3, 0, 0], ['a', 'b', 'c'], res).grade, 7);
  assert.strictEqual(Events.pickemGrade([1, 2, 3, 0, 0], ['d', 'e', 'c'], res).grade, 2, 'a DNF scores nothing');
  assert.strictEqual(Events.pickemGrade([1, 1, 1, 0], ['a', 'b', 'c'], res).grade, 10);
  const e = new Events({ seasons: 'none', rng: seeded(9) });
  const lay = e.pickemLayout(10);
  assert.strictEqual(lay.length, 10); assert.deepStrictEqual([1, 2, 3, 0].map(s => lay.filter(x => x === s).length), [2, 2, 2, 4]);
  assert.deepStrictEqual(e.pickemLayout(4).slice().sort(), [0, 1, 2, 3]);
});

test('props NPC (indy4) and the Halloween Drink', { skip: !hasData && 'no game data' }, () => {
  const e = new Events({ seasons: 'indy4', rng: seeded(5) });
  const p = profile();
  assert.strictEqual(e.handle({ profile: p }, { t: 'props.claim' }).error, 'ing');
  for (let i = 0; i < 3; i++) assert.ok(e.handle({ profile: p }, { t: 'props.find', idx: i }).ok);
  assert.strictEqual(cnt(p, 'i_n_00052'), 1);
  const r = e.handle({ profile: p }, { t: 'props.claim' });
  assert.ok(r.ok, r.error); assert.strictEqual(cnt(p, 'i_n_00054'), 1); assert.strictEqual(cnt(p, 'i_n_00051'), 0);
  for (let i = 0; i < 3; i++) e.handle({ profile: p, eventAt: {} }, { t: 'props.find', idx: i });
  assert.strictEqual(e.handle({ profile: p, eventAt: {} }, { t: 'props.claim' }).error, 'already', 'once a day');
  assert.strictEqual(new Events({ seasons: 'none' }).handle({ profile: p }, { t: 'props.find', idx: 0 }).ok, false);
  // the drink: the Special Drink effect, refused at 250 % (11059)
  add(p, 'i_n_halloween', 2);
  const s = { profile: p };
  const d = e.handle(s, { t: 'event.drink', item: 'i_n_halloween' });
  assert.ok(d.ok, d.error); assert.strictEqual(s.mita.value, 200); assert.strictEqual(cnt(p, 'i_n_halloween'), 1);
  s.mita.value = 250;
  assert.match(e.handle(s, { t: 'event.drink', item: 'i_n_halloween' }).error, /too much Special Drink/);
  assert.strictEqual(e.handle(s, { t: 'event.drink', item: 'i_n_00003' }).ok, false);
});

// ---------------- wire: Pick'em in a real room, an egg-basket gift between two drivers ----------------

let server, port, dir;
before(async () => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), 'dcev2-'));
  server = createServer({ port: 0, db: path.join(dir, 'ev2.db'), quiet: true, events: 'valentine', raceCountdownMs: 100, raceMaxSpeed: 1e9, raceMinTrackLength: 10, maxConnPerIp: 200 });
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
      req(t, body = {}) { const id = rid++; return new Promise((r, j) => { const tm = setTimeout(() => j(new Error('no reply to ' + t)), 4000); pending.set(id, x => { clearTimeout(tm); r(x); }); ws.send(JSON.stringify({ t, rid: id, ...body })); }); },
      send(t, body = {}) { ws.send(JSON.stringify({ t, ...body })); },
      wait(pred, ms = 3000) {
        const hit = pushed.find(pred);
        if (hit) { pushed.splice(pushed.indexOf(hit), 1); return Promise.resolve(hit); }
        return new Promise((res, rej) => { const w = { pred, resolve: res }; waiters.push(w); w.timer = setTimeout(() => { waiters.splice(waiters.indexOf(w), 1); rej(new Error('timeout')); }, ms); });
      },
      close() { ws.close(); },
    };
    ws.on('open', () => c.wait(m => m.t === 'welcome').then(() => resolve(c)));
  });
}
async function driver(user, name) {
  const c = await connect();
  assert.ok((await c.req('register', { protocol: PROTOCOL, user: 'e2_' + user, pass: 'secret123' })).ok);
  assert.ok((await c.req('driver.create', { name, car: 0, color: 1 })).ok);
  assert.ok((await c.req('enter', { city: 'koinonia' })).ok);
  c.name = name;
  return c;
}
const live = name => server.world.findByName(name);
const sleep = ms => new Promise(r => setTimeout(r, ms));

test('wire: Pick\'em ticket, picks, the Slot Ticket from the final places', { skip: !hasData && 'no game data' }, async () => {
  const racers = [];
  for (let i = 0; i < 4; i++) racers.push(await driver('r' + i, 'Racer' + i));
  const sp = await driver('sp', 'Picker');
  const track = { key: 'koinonia/t1', name: 'K1', laps: 1, length: 10 };
  const h = await racers[0].req('race.host', { track, room: { title: 'PK', max: 8 } });
  for (const r of racers.slice(1)) { assert.ok((await r.req('race.join', { id: h.id })).ok); await r.req('race.ready', { ready: true }); }
  assert.ok((await sp.req('race.observe', { id: h.id })).ok);
  assert.ok((await racers[0].req('race.startRace')).ok);
  assert.match((await sp.req('pickem.start', { room: h.id })).error, /Ticket/);
  live('Picker').profile.Items.push({ Id: 'gacha_observe', Count: 1 });
  const st = await sp.req('pickem.start', { room: h.id });
  assert.ok(st.ok, st.error); assert.strictEqual(st.symbols.length, 4); assert.deepStrictEqual(st.picks, ['Racer0', 'Racer1', 'Racer2'], 'the first three are picked');
  assert.ok(!(st.driver.Items.find(i => i.Id === 'gacha_observe') || { Count: 0 }).Count, 'the ticket is spent');
  assert.match((await sp.req('pickem.pick', { name: 'Racer3' })).error, /three/);
  assert.ok((await sp.req('pickem.pick', { name: 'Racer2', on: false })).ok);
  assert.deepStrictEqual((await sp.req('pickem.pick', { name: 'Racer3' })).picks, ['Racer0', 'Racer1', 'Racer3']);
  assert.match((await sp.req('pickem.finish')).error, /not over/);
  await racers[0].wait(q => q.t === 'race.go'); await sleep(30);
  for (const r of racers) { r.send('race.progress', { lap: 1, idx: 0, finished: true }); await sleep(15); }
  await sp.wait(q => q.t === 'race.results');
  const fin = await sp.req('pickem.finish');
  assert.ok(fin.ok, fin.error);
  assert.deepStrictEqual(fin.places, [1, 2, 4]);
  const expect = 1 + [0, 1, 3].reduce((a, i) => a + [0, 3, 2, 1][st.symbols[i]], 0);
  assert.strictEqual(fin.grade, expect);
  assert.ok(fin.driver.Items.some(i => i.Id === `gacha_observe_v${expect}`));
  for (const c of [...racers, sp]) c.close();
});

test('wire: an egg basket sent as a gift note, the Easter Rabbit Dolls', { skip: !hasData && 'no game data' }, async () => {
  const a = await driver('ga', 'GiftFrom'), b = await driver('gb', 'GiftTo');
  assert.strictEqual((await a.req('valen.check', { name: 'GiftTo' })).name, 'GiftTo');
  assert.strictEqual((await a.req('valen.check', { name: 'Nobody9' })).ok, false);
  live('GiftFrom').profile.Items.push({ Id: 'h_make_giftbox', Count: 1 });
  assert.match((await a.req('valen.gift', { item: 'h_make_giftbox', to: 'GiftFrom', message: 'hi' })).error, /yourself/);
  assert.match((await a.req('valen.gift', { item: 'h_make_giftbox', to: 'GiftTo', message: '' })).error, /message/);
  const r = await a.req('valen.gift', { item: 'h_make_giftbox', to: 'GiftTo', message: 'Happy Easter!' });
  assert.ok(r.ok, r.error); assert.strictEqual(r.fee, 50);
  assert.ok(r.driver.Items.some(i => i.Id === 'lovelove_heart'));
  assert.ok(!r.driver.Items.some(i => i.Id === 'h_make_giftbox' && i.Count > 0));
  const note = await b.wait(q => q.t === 'note.new' && q.note.sender === 'GiftFrom');
  assert.strictEqual(note.note.content, 'Happy Easter!'); assert.strictEqual(note.note.item.id, 'h_get_giftbox');
  const take = await b.req('note.take', { id: note.note.id });
  assert.ok(take.ok, take.error); assert.ok(take.driver.Items.some(i => i.Id === 'h_get_giftbox'));
  assert.strictEqual((await b.wait(q => q.t === 'note.new' && q.note.item && q.note.item.id === 'lovelove_heart')).note.item.count, 1);
  a.close(); b.close();
});
