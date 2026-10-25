'use strict';
// Missions and HUV chases added in the chase round (DriftCity_RE/CHASES.md, MISSIONS.md): event / EX deliveries and the
// no-medal tier, Rush Time, the Virtual HUV, the Mita 500 drink, the party chase (src/partyplay.js) and the party combo rise.
const { test, before, after } = require('node:test');
const assert = require('node:assert');
const os = require('node:os');
const path = require('node:path');
const fs = require('node:fs');
const WebSocket = require('ws');
const { createServer, PROTOCOL } = require('../server');
const { Economy, newProfile, loadMissions, rewards, COOLDOWN, MITA } = require('../src/economy');
const { loadGameData } = require('../src/gamedata');
const { CHASE_SECONDS } = require('../src/partyplay');

const gd = loadGameData(undefined, () => {});

test('rush, vhuv and mita reward kinds: formulas, bounds, cooldowns and the drink effect', () => {
  let now = 1e9;
  const eco = new Economy(() => {}, null, () => now);
  const p = newProfile('Rusher', 0, 1);
  p.Level = 20; p.Mito = 10000;
  const s = { profile: p };
  assert.ok(COOLDOWN.rush > 0 && COOLDOWN.vhuv > 0 && COOLDOWN.mita > 0);

  // Rush Time: hits capped at 40, the final blow adds 5000 Mito and three Mittron Enhancers
  assert.deepStrictEqual(rewards.rush(20, 99, false), { exp: 200 + 800 + 400, mito: 2000 + 6000 });
  assert.match(eco.claim(s, 'rush', { hits: 10, seconds: 5 }).error, /rush too short/);
  const exp0 = p.Exp, mito0 = p.Mito, caught0 = p.HuvCaught;
  const r = eco.claim(s, 'rush', { hits: 10, finalBlow: true, seconds: 120, drivers: 6 });
  assert.ok(r.ok, r.error);
  assert.ok(r.exp >= 200 + 800 + 100 && r.mito >= 2000 + 6000 + 5000, JSON.stringify(r));
  assert.strictEqual(p.HuvCaught, caught0 + 1);
  assert.strictEqual((p.Items.find(i => i.Id === 'i_n_00003') || {}).Count >= 3, true, 'three Mittron Enhancers for the lucky driver');
  assert.ok(p.Exp !== exp0 || p.Level > 20); assert.ok(p.Mito > mito0);
  assert.match(eco.claim(s, 'rush', { hits: 1, seconds: 60 }).error, /too soon/);

  // Virtual HUV: 4:4 or 8:8 only, at least a minute, the winners get half again
  assert.deepStrictEqual(rewards.vhuv(10, 5, false), { exp: 300 + 500 + 150, mito: 2000 + 2500 });
  assert.deepStrictEqual(rewards.vhuv(10, 5, true), { exp: Math.floor(950 * 1.5), mito: Math.floor(4500 * 1.5) });
  assert.match(eco.claim(s, 'vhuv', { won: true, crashes: 3, size: 5, seconds: 180 }).error, /bad team size/);
  assert.match(eco.claim(s, 'vhuv', { won: true, crashes: 3, size: 4, seconds: 20 }).error, /match too short/);
  assert.ok(eco.claim(s, 'vhuv', { won: false, crashes: 3, size: 8, seconds: 180 }).ok);

  // Mita 500: 500 Mito a bottle, three a day; the effect (150 % after the first) raises a chase's EXP
  const before = p.Mito;
  const d1 = eco.claim(s, 'mita', { index: 12 });
  assert.ok(d1.ok, d1.error);
  assert.deepStrictEqual([d1.mito, d1.value, p.Mito], [-MITA.price, 150, before - MITA.price]);
  now += 10000;
  const chase = eco.claim(s, 'chase', { boss: false, seconds: 30 });
  assert.ok(chase.ok, chase.error);
  assert.ok(chase.exp >= Math.floor(rewards.chase(p.Level, false).exp * 1.5) - 1, `drink effect on the chase EXP: ${chase.exp}`);
  now += 6000; assert.strictEqual(eco.claim(s, 'mita', { index: 13 }).value, 200);
  now += 6000; assert.strictEqual(eco.claim(s, 'mita', { index: 14 }).value, 250);
  now += 6000; assert.match(eco.claim(s, 'mita', { index: 15 }).error, /Special Drink/, 'a fourth bottle the same day');
  assert.match(eco.claim(s, 'mita', { index: 9999 }).error, /no salesman/);
  // the next day sells again; above level 70 the drink does nothing
  now += 86400000;
  p.Level = 75;
  const old = eco.claim(s, 'mita', { index: 1 });
  assert.ok(old.ok);
  assert.strictEqual(p.MitaCount, 1);
});

test('event and EX deliveries: keys, no level cap and the no-medal tier', { skip: !gd && 'game data not found' }, () => {
  let now = 1e9;
  const eco = new Economy(() => {}, gd, () => now);
  const m = loadMissions(gd.dir);
  const ex = m.jobs.get('ex_a_00048');
  assert.ok(ex, 'the EX loop keyed apart from the regular a_00048');
  assert.strictEqual(ex.kind, 'ex');
  assert.ok(m.jobs.get('a_00048').kind === 'regular');
  const ev = m.jobs.get('e_v_001');
  assert.ok(ev && ev.kind === 'event');
  assert.deepStrictEqual([ev.time, ev.expNone, ev.mitoNone], [[85, 95, 100], 100, 30]);
  const p = newProfile('Courier', gd.starterCars[0].index, 0);
  p.Level = 100;
  const s = { profile: p };
  const gold = eco.claim(s, 'delivery', { job: 'e_v_001', seconds: 80 });
  assert.ok(gold.ok, gold.error);
  assert.deepStrictEqual([gold.exp, gold.mito], [850, 1280], 'col 1 = 0: every level');
  now += 31000;
  const none = eco.claim(s, 'delivery', { job: 'e_v_001', seconds: 150 });
  assert.ok(none.ok, none.error);
  assert.deepStrictEqual([none.exp, none.mito], [100, 30], 'slower than bronze: cols 37/38');
  now += 31000;
  assert.match(eco.claim(s, 'delivery', { job: 'a_00041', seconds: 200 }).error, /no medal|level too high/, 'regular rows have no fourth tier');
});

// ---------------- party chase over the socket ----------------

let server, port, dir;
let offset = 0;
const clock = () => Date.now() + offset;

before(async () => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), 'dcmissions-'));
  server = createServer({ port: 0, db: path.join(dir, 'missions.db'), quiet: true, clock, gamedata: null, maxConnPerIp: 200, ucTimeScale: 0.001, ucRng: () => 0.999 });
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
      ws, pushed,
      req(t, body = {}) { const id = rid++; return new Promise(r => { pending.set(id, r); ws.send(JSON.stringify({ t, rid: id, ...body })); }); },
      wait(pred, ms = 3000) {
        const hit = pushed.find(pred);
        if (hit) { pushed.splice(pushed.indexOf(hit), 1); return Promise.resolve(hit); }
        return new Promise((res, rej) => { const w = { pred, resolve: res }; waiters.push(w); w.timer = setTimeout(() => { waiters.splice(waiters.indexOf(w), 1); rej(new Error('timeout: ' + pred)); }, ms); });
      },
      close() { ws.close(); },
    };
    ws.on('open', () => c.wait(m => m.t === 'welcome').then(() => resolve(c)));
  });
}

async function driver(user, city = 'cras') {
  const c = await connect();
  const r = await c.req('register', { protocol: PROTOCOL, user: 'mc_' + user, pass: 'secret123' });
  assert.ok(r.ok, r.error);
  assert.ok((await c.req('driver.create', { name: user, car: 0, color: 1 })).ok);
  assert.ok((await c.req('enter', { city })).ok);
  c.name = user;
  return c;
}

async function party(leader, ...others) {
  for (const o of others) {
    assert.ok((await leader.req('party.invite', { name: o.name, party: 'Chasers' })).ok);
    await o.wait(m => m.t === 'party.invite');
    assert.ok((await o.req('party.accept')).ok);
    await leader.wait(m => m.t === 'party.update' && m.party && m.party.members.some(x => x.name === o.name));
  }
}

test('party chase: the leader starts it for the party, shared health, everyone paid with the party bonus', async () => {
  const a = await driver('ChaseLead'), b = await driver('ChaseMate');
  try {
    assert.match((await b.req('chase.party.start', { boss: false, huv: 2, hp: 150 })).error, /not in a party/i);
    await party(a, b);
    assert.match((await b.req('chase.party.start', { boss: false, huv: 2, hp: 150 })).error, /leader/i, 'members cannot start it');
    const st = await a.req('chase.party.start', { boss: false, huv: 2, hp: 9999 });
    assert.ok(st.ok, st.error);
    const pa = await a.wait(m => m.t === 'chase.party.start'), pb = await b.wait(m => m.t === 'chase.party.start');
    assert.strictEqual(pa.id, pb.id);
    assert.deepStrictEqual([pb.huv, pb.hp, pb.seconds], [2, 250, CHASE_SECONDS], 'the quoted health is bounded (100 + Crash, at most 250)');
    assert.match((await a.req('chase.party.start', { boss: false, huv: 1 })).error, /chasing/i, 'one party chase at a time');

    // hits from both members wear the shared health down; a flood is capped by the hit budget
    const h1 = await b.req('chase.party.hit', { damage: 40 });
    assert.ok(h1.ok);
    const hp1 = await a.wait(m => m.t === 'chase.party.hp' && m.by === 'ChaseMate');
    assert.strictEqual(hp1.hp, 210);
    const flood = await a.req('chase.party.hit', { damage: 100000 });
    assert.ok(flood.ok && flood.hp > 0, 'one message cannot take more than the budget');
    const p0 = (await a.req('profile')).profile || null;
    for (let i = 0; i < 40; i++) {
      offset += 1000;
      const r = await (i % 2 ? a : b).req('chase.party.hit', { damage: 45 });
      if (!r.ok || r.hp <= 0) break;
    }
    const ea = await a.wait(m => m.t === 'chase.party.end'), eb = await b.wait(m => m.t === 'chase.party.end');
    assert.ok(ea.success && eb.success);
    assert.strictEqual(ea.rows.length, 2);
    for (const row of ea.rows) {
      const base = rewards.chase(1, false);
      assert.ok(row.exp >= base.exp + Math.floor(base.exp * 0.1) - 1, `party bonus on ${row.name}: ${row.exp}`);
      assert.ok(row.mito >= base.mito);
    }
    assert.ok(ea.driver && ea.driver.HuvCaught >= 1, 'the member\'s driver comes with the result');
    void p0;
  } finally { a.close(); b.close(); }
});

test('party chase: time runs out for all; a member who quits is not paid', async () => {
  const a = await driver('TimeLead'), b = await driver('TimeMate'), c = await driver('TimeThird');
  try {
    await party(a, b, c);
    assert.ok((await a.req('chase.party.start', { boss: true, huv: 0 })).ok);
    await b.wait(m => m.t === 'chase.party.start');
    assert.ok((await c.req('chase.party.quit')).ok);
    offset += (CHASE_SECONDS + 60) * 1000;
    server.partyplay.tick();
    const e = await b.wait(m => m.t === 'chase.party.end');
    assert.strictEqual(e.success, false);
    assert.ok(e.rows.every(r => r.exp === 0 && r.mito === 0));
    assert.ok(!(await c.wait(m => m.t === 'chase.party.end', 300).catch(() => null)), 'the member who quit gets no result');
  } finally { a.close(); b.close(); c.close(); }
});

test('party combo push carries the rise for the other members\' PARTYNUM display', async () => {
  const a = await driver('ComboLead'), b = await driver('ComboMate');
  try {
    await party(a, b);
    assert.ok((await a.req('party.combo', { count: 3 })).ok);
    const pb = await b.wait(m => m.t === 'party.combo' && m.name === 'ComboLead');
    assert.deepStrictEqual([pb.combo, pb.rise], [3, 3]);
    offset += 1000;
    assert.ok((await a.req('party.combo', { count: 5 })).ok);
    const pb2 = await b.wait(m => m.t === 'party.combo' && m.name === 'ComboLead');
    assert.strictEqual(pb2.rise, 2);
  } finally { a.close(); b.close(); }
});
