'use strict';
// Events, gacha, lotto, coupons (src/events.js): server-side draws, rates, costs, cooldowns and limits.
const { test, before, after } = require('node:test');
const assert = require('node:assert');
const os = require('node:os');
const path = require('node:path');
const fs = require('node:fs');
const WebSocket = require('ws');
const { createServer, PROTOCOL } = require('../server');
const { Events, Tables, parseSeasons, seasonsForDate, slotMito, COUPON_RATE, LIMITS, MAX_DROPS_PER_DAY, MITO_PER_COIN } = require('../src/events');
const { DEFAULT_DIR } = require('../src/gamedata');

const hasData = fs.existsSync(path.join(DEFAULT_DIR, 'init', 'rewardgroup.xlt'));
const seeded = seed => () => { seed = (seed * 1103515245 + 12345) & 0x7fffffff; return seed / 0x80000000; };

function profile(extra = {}) {
  return { Name: 'Tester', Level: 30, Exp: 0, Mito: 5000000, Items: [], Inventory: [], PerfParts: [], Cars: [], HuvCaught: 0, RacesEntered: 0, PvpRaces: 0, ...extra };
}
const cnt = (p, id) => (p.Items.find(i => i.Id === id) || { Count: 0 }).Count;
const add = (p, id, n = 1) => { const it = p.Items.find(i => i.Id === id); if (it) it.Count += n; else p.Items.push({ Id: id, Count: n }); };

// ---------------- the draw rule on a small hand-made table ----------------

test('draw: all-100 pools give every row, level bands pick one, weighted pools give one', () => {
  const t = Object.create(Tables.prototype);
  t.pools = new Map(); t.items = new Map();
  const row = (group, type, item, p, count = 1, etc = 0) => {
    const k = group.toLowerCase(); if (!t.pools.has(k)) t.pools.set(k, []);
    t.pools.get(k).push({ group, type, item, p, count, etc, etcStr: '', period: 0 });
  };
  row('box', 'NormalItem', 'a', 100); row('box', 'SkidItem', 'b', 100, 3);
  row('band', 'RewardGroup', 'lo', 100, 1, 20); row('band', 'RewardGroup', 'hi', 100, 1, 1000);
  row('lo', 'Exp', '0', 100, 50); row('hi', 'Mito', '0', 100, 900);
  row('pick', 'NormalItem', 'x', 75); row('pick', 'NormalItem', 'y', 25); row('pick', 'NormalItem', 'never', 0);
  row('card', 'RewardGroup', 'pick', 100); row('card', 'n/a', 'lotto_00001', 100);
  assert.deepStrictEqual(t.draw('box', 1, Math.random).map(g => [g.item, g.count]), [['a', 1], ['b', 3]]);
  assert.deepStrictEqual(t.draw('band', 10, Math.random), [{ kind: 'exp', count: 50 }]);
  assert.deepStrictEqual(t.draw('band', 50, Math.random), [{ kind: 'mito', count: 900 }]);
  const tally = { x: 0, y: 0, never: 0 }, rng = seeded(7);
  for (let i = 0; i < 4000; i++) tally[t.draw('pick', 1, rng)[0].item]++;
  assert.strictEqual(tally.never, 0);
  assert.ok(tally.x > 2700 && tally.x < 3300, `75 % weight: ${tally.x}`);
  const card = t.draw('card', 1, Math.random);
  assert.strictEqual(card.length, 2); assert.strictEqual(card[1].kind, 'bust');
});

test('seasons: calendar, lists, date override', () => {
  assert.deepStrictEqual(seasonsForDate(new Date('2025-12-24T00:00:00Z')), ['christmas']);
  assert.deepStrictEqual(seasonsForDate(new Date('2026-10-24T00:00:00Z')).sort(), ['halloween', 'harvest']);
  assert.deepStrictEqual(parseSeasons('xmas, easter', Date.now()).sort(), ['christmas', 'valentine']);
  assert.deepStrictEqual(parseSeasons('date=2026-08-01', Date.now()), ['summer']);
  assert.deepStrictEqual(parseSeasons('none', Date.now()), []);
  assert.strictEqual(slotMito([1, 1, 1], 3), 30000);
  assert.strictEqual(slotMito([0, 0, 0], 3), 0, 'three bombs pay nothing');
  assert.strictEqual(slotMito([1, 2, 1], 3), 0);
});

// ---------------- the real tables ----------------

test('tables: rewardgroup, items, coupons and token book load', { skip: !hasData && 'no game data' }, () => {
  const e = new Events({ seasons: 'all' });
  assert.ok(e.t.pools.size > 500 && e.t.items.size > 1000);
  assert.ok(e.t.hasPool('rw_xmas_giftbox') && e.t.hasPool('lotto_Natura') && e.t.hasPool('rw_gacha_ob_v10'));
  assert.strictEqual(e.t.item('lotto_scratchies_Natura').pool, 'lotto_Natura');
  assert.ok(e.t.coupons.some(c => c.item === 'i_n_c0001' && c.cat === 5 && c.need === 1000), 'Panda: 1000 drifts');
  assert.strictEqual(e.t.tokens[0].currency, 'i_n_00049');
});

test('boxes, scratch cards, capsules and the slot: costs, draws and consumption', { skip: !hasData && 'no game data' }, () => {
  const e = new Events({ seasons: 'all', rng: seeded(42) });
  const p = profile(), s = { profile: p };
  // gift box: spent, prizes from its pool
  add(p, 'i_n_00085', 1);
  let r = e.handle(s, { t: 'event.box', item: 'i_n_00085' });
  assert.ok(r.ok, r.error); assert.strictEqual(cnt(p, 'i_n_00085'), 0); assert.strictEqual(r.grants.length, 1);
  assert.strictEqual(e.handle({ profile: p, eventAt: {} }, { t: 'event.box', item: 'i_n_00085' }).ok, false, 'nothing left to open');
  assert.strictEqual(e.handle(s, { t: 'event.box', item: 'i_n_00003' }).ok, false, 'an enhancer is no box');
  // level gate: Natura V8 box needs level 61
  add(p, 'giftbox_Natura_v8', 1); p.Level = 10;
  assert.match(e.handle({ profile: p }, { t: 'event.box', item: 'giftbox_Natura_v8' }).error, /level/);
  p.Level = 30;
  // scratch card: bought for Mito, three prizes, the server-wide gauge counts it
  const mito0 = p.Mito;
  r = e.handle({ profile: p }, { t: 'lotto.buy', item: 'lotto_scratchies_Natura' });
  assert.ok(r.ok, r.error); assert.strictEqual(mito0 - p.Mito, 50000);
  r = e.handle({ profile: p }, { t: 'lotto.scratch', item: 'lotto_scratchies_Natura' });
  assert.ok(r.ok, r.error); assert.strictEqual(r.grants.length, 3, 'one prize per column'); assert.strictEqual(e.lottoCards(), 1);
  assert.strictEqual(cnt(p, 'lotto_scratchies_Natura'), 0);
  // capsule: Mito tab 20,000; Coin tab 100 coins x 1000 Mito; one item; parts tabs closed (PartsGotchaClose)
  let m = p.Mito; r = e.handle({ profile: p }, { t: 'gacha.buy', mode: 1 });
  assert.ok(r.ok, r.error); assert.strictEqual(m - p.Mito, 20000); assert.strictEqual(r.grants.length, 1);
  m = p.Mito; r = e.handle({ profile: p }, { t: 'gacha.buy', mode: 0 });
  assert.ok(r.ok, r.error); assert.strictEqual(m - p.Mito, 100 * MITO_PER_COIN);
  assert.strictEqual(e.handle({ profile: p }, { t: 'gacha.buy', mode: 3 }).ok, false);
  const poor = profile({ Mito: 100 });
  assert.match(e.handle({ profile: poor }, { t: 'gacha.buy', mode: 1 }).error, /Mito/);
  // slot ticket: spent, three symbols, EXP 100 x grade, Mito only for three alike
  add(p, 'gacha_observe_v4', 1);
  r = e.handle({ profile: p }, { t: 'gacha.slot', item: 'gacha_observe_v4' });
  assert.ok(r.ok, r.error); assert.strictEqual(r.symbols.length, 3); assert.strictEqual(r.exp, 400);
  assert.strictEqual(r.mito, slotMito(r.symbols, 4));
  assert.strictEqual(e.handle({ profile: p }, { t: 'gacha.slot', item: 'gacha_observe_v4' }).ok, false, 'ticket spent');
});

test('cooldowns and daily limits', { skip: !hasData && 'no game data' }, () => {
  let now = Date.parse('2026-03-01T10:00:00Z');
  const e = new Events({ seasons: 'all', clock: () => now, rng: seeded(3) });
  const p = profile({ Mito: 1e9 }), s = { profile: p };
  assert.ok(e.handle(s, { t: 'gacha.buy', mode: 1 }).ok);
  assert.strictEqual(e.handle(s, { t: 'gacha.buy', mode: 1 }).error, 'too soon');
  for (let i = 1; i < LIMITS.gacha; i++) { now += 2000; assert.ok(e.handle(s, { t: 'gacha.buy', mode: 1 }).ok, `capsule ${i}`); }
  now += 2000; assert.match(e.handle(s, { t: 'gacha.buy', mode: 1 }).error, /a day/);
  now += 24 * 3600 * 1000; assert.ok(e.handle(s, { t: 'gacha.buy', mode: 1 }).ok, 'a new day');
  for (let i = 0; i < LIMITS.lottoBuy; i++) { now += 300; assert.ok(e.handle(s, { t: 'lotto.buy', item: 'lotto_scratchies_Dew' }).ok); }
  now += 300; assert.match(e.handle(s, { t: 'lotto.buy', item: 'lotto_scratchies_Dew' }).error, /a day/);
});

test('coupons: bounded counters, claim, no double claim', { skip: !hasData && 'no game data' }, () => {
  let now = Date.parse('2026-03-01T10:00:00Z');
  const e = new Events({ seasons: 'all', clock: () => now });
  const p = profile(), s = { profile: p, couponSyncAt: now };
  // ten minutes of play: at most 2 drifts a second can be counted
  now += 600 * 1000;
  e.handle(s, { t: 'coupon.sync', add: [0, 0, 0, 0, 0, 5000, 0, 0, 0, 0] });
  assert.ok(p.Events.Coupon[5] <= COUPON_RATE[5] * 600 + 1 && p.Events.Coupon[5] >= 1000, `drifts ${p.Events.Coupon[5]}`);
  // Panda (drifts 1000): claimable now
  let r = e.handle(s, { t: 'coupon.claim', cat: 5, idx: 0 });
  assert.ok(r.ok, r.error); assert.strictEqual(cnt(p, 'i_n_c0001'), 1);
  assert.strictEqual(e.handle(s, { t: 'coupon.claim', cat: 5, idx: 0 }).ok, false, 'claimed once');
  // Tiger (30,000 drifts): not yet
  assert.strictEqual(e.handle(s, { t: 'coupon.claim', cat: 5, idx: 1 }).ok, false);
  // a burst without elapsed time is capped
  const before = p.Events.Coupon[2];
  e.handle(s, { t: 'coupon.sync', add: [0, 0, 999, 0, 0, 0, 0, 0, 0, 0] });
  assert.ok(p.Events.Coupon[2] - before <= 1.01, 'chases cannot jump');
  // codes: configured, once per driver
  const e2 = new Events({ seasons: 'all', codes: 'HELLO=i_n_00088:3' });
  assert.ok(e2.handle({ profile: p }, { t: 'coupon.code', code: 'hello' }).ok);
  assert.strictEqual(cnt(p, 'i_n_00088'), 3);
  assert.strictEqual(e2.handle({ profile: p }, { t: 'coupon.code', code: 'HELLO' }).ok, false);
  assert.strictEqual(e2.handle({ profile: p }, { t: 'coupon.code', code: 'NOPE' }).ok, false);
});

test('token book and the daily mission', { skip: !hasData && 'no game data' }, () => {
  const e = new Events({ seasons: 'none' });
  const p = profile(), s = { profile: p };
  assert.strictEqual(e.handle(s, { t: 'token.daily' }).error, 'ing');
  p.HuvCaught += 5;
  const r = e.handle(s, { t: 'token.daily' });
  assert.ok(r.ok, r.error); assert.strictEqual(cnt(p, 'i_n_00049'), 1);
  assert.strictEqual(e.handle(s, { t: 'token.daily' }).error, 'already');
  assert.ok(e.handle(s, { t: 'token.exchange', row: 0 }).ok, 'row 0 costs one token');
  assert.strictEqual(cnt(p, 'i_n_00049'), 0); assert.ok(e.t.tokens[0].gifts.every(([id, n]) => cnt(p, id) >= n), 'row 0 gifts');
  assert.strictEqual(e.handle(s, { t: 'token.exchange', row: 1 }).ok, false, 'row 1 needs 5');
});

test('Christmas: drops, server-wide trees, star last; harvest; seasons gate', { skip: !hasData && 'no game data' }, () => {
  const e = new Events({ seasons: 'christmas,harvest', rng: seeded(9) });
  const p = profile(), s = { profile: p };
  e.handle(s, { t: 'event.tick', city: 'cras' });          // first tick: the base counters
  p.HuvCaught += 12;
  let r = e.handle(s, { t: 'event.tick', city: 'cras' });
  assert.ok(r.ok);
  assert.ok(cnt(p, 'i_n_00088') >= 1 && cnt(p, 'event_giftbox_cras') >= 1);
  assert.strictEqual(p.Events.DayDrops, MAX_DROPS_PER_DAY, 'capped per day');
  add(p, 'i_n_00088', 20);
  assert.match(e.handle({ profile: p }, { t: 'xmas.donate', city: 'cras', slot: 9 }).error, /9 other/);
  for (let i = 0; i < 9; i++) assert.ok(e.handle({ profile: p }, { t: 'xmas.donate', city: 'cras', slot: i }).ok);
  assert.match(e.handle({ profile: p }, { t: 'xmas.donate', city: 'cras', slot: 3 }).error, /already/);
  r = e.handle({ profile: p }, { t: 'xmas.donate', city: 'cras', slot: 9 });
  assert.ok(r.completed); assert.strictEqual(e.xmasBonusPct(), 30);
  assert.strictEqual(cnt(p, 'i_n_battleEXP_25'), 10);
  // another driver sees the same tree
  const other = profile(); add(other, 'i_n_00088', 1);
  assert.match(e.handle({ profile: other }, { t: 'xmas.donate', city: 'cras', slot: 0 }).error, /already/);
  // harvest: box from ingredients, delivered for receipts, exchanged
  for (const [id, n] of [['event_vegetable_n', 12], ['event_fish_n', 6], ['event_fruit_n', 4], ['event_meat_n', 3]]) add(p, id, n);
  assert.match(e.handle({ profile: p }, { t: 'harvest.craft' }).error, /Recipe/);
  assert.ok(e.handle({ profile: p }, { t: 'harvest.buybox' }).ok);
  r = e.handle({ profile: p }, { t: 'harvest.craft' });
  assert.ok(r.ok, r.error); assert.ok(/^eventbox_result_0[123]$/.test(r.grants[0].item));
  r = e.handle({ profile: p }, { t: 'harvest.deliver' });
  assert.ok(r.ok, r.error); assert.ok(r.receipts >= 1); assert.strictEqual(e.harvestReceipts(), r.receipts);
  assert.ok(e.handle({ profile: p }, { t: 'harvest.exchange' }).ok);
  // seasons gate
  const off = new Events({ seasons: 'none' });
  const q = profile(); add(q, 'i_n_00088', 1);
  assert.match(off.handle({ profile: q }, { t: 'xmas.donate', city: 'oros', slot: 0 }).error, /isn't running/);
});

// ---------------- over the wire ----------------

let server, port, dir;
before(async () => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), 'dcevents-'));
  server = createServer({ port: 0, db: path.join(dir, 'ev.db'), quiet: true, gamedata: null, events: 'christmas', maxConnPerIp: 50 });
  port = await server.listen();
});
after(async () => { await server.close(); fs.rmSync(dir, { recursive: true, force: true }); });

function connect() {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(`ws://127.0.0.1:${port}`);
    const pending = new Map(); let rid = 1;
    ws.on('message', raw => { const m = JSON.parse(raw); if (m.t === 'reply' && pending.has(m.rid)) { pending.get(m.rid)(m); pending.delete(m.rid); } else if (m.t === 'welcome') resolve(c); });
    ws.on('error', reject);
    const c = { req(t, body = {}) { const id = rid++; return new Promise(r => { pending.set(id, r); ws.send(JSON.stringify({ t, rid: id, ...body })); }); }, close() { ws.close(); } };
  });
}

test('wire: event.state, a donation shared by two drivers, the driver comes back', { skip: !hasData && 'no game data' }, async () => {
  const a = await connect(), b = await connect();
  for (const [c, u] of [[a, 'eva'], [b, 'evb']]) {
    assert.ok((await c.req('register', { protocol: PROTOCOL, user: 'ev_' + u, pass: 'secret123' })).ok);
    assert.ok((await c.req('driver.create', { name: 'Ev' + u, car: 0, color: 1 })).ok);
  }
  let st = await a.req('event.state');
  assert.ok(st.ok); assert.deepStrictEqual(st.seasons, ['christmas']); assert.deepStrictEqual(st.xmas, [0, 0, 0, 0]);
  server.world.findByName('Eveva').profile.Items.push({ Id: 'i_n_00088', Count: 1 });
  const r = await a.req('xmas.donate', { city: 'koinonia', slot: 4 });
  assert.ok(r.ok, r.error);
  assert.ok(r.driver.Items.some(i => i.Id === 'i_n_battleEXP_25'), 'the driver comes back with the ticket');
  st = await b.req('event.state');
  assert.deepStrictEqual(st.xmas, [0, 0, 16, 0], 'the other driver sees the decoration');
  assert.strictEqual((await b.req('gacha.buy', { mode: 1 })).ok, true);
  a.close(); b.close();
});
