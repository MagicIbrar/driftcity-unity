'use strict';
// The HUV rules and events the server decides (src/huvrules.js, src/partyplay.js chase.*, src/huvevents.js rush.* / vhuv.*,
// the Undercity's UC_HUVTimeReduce / badges / 11146 in src/undercity.js).
const { test, before, after } = require('node:test');
const assert = require('node:assert');
const os = require('node:os');
const path = require('node:path');
const fs = require('node:fs');
const WebSocket = require('ws');
const { createServer, PROTOCOL } = require('../server');
const { rewards } = require('../src/economy');
const rules = require('../src/huvrules');
const { RUSH, VHUV } = require('../src/huvevents');
const { ucBadges } = require('../src/undercity');

let server, port, dir;
let offset = 0;
const clock = () => Date.now() + offset;
const sleep = ms => new Promise(r => setTimeout(r, ms));

before(async () => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), 'dchuv-'));
  server = createServer({ port: 0, db: path.join(dir, 'huv.db'), quiet: true, clock, gamedata: null, maxConnPerIp: 200, ucTimeScale: 0.001, ucRng: () => 0.999 });
  port = await server.listen();
  // no automatic Rush Time while the tests move the clock; the AI stand-ins act only when the tests say so
  server.partyplay.events.auto = false;
  server.partyplay.events.rng = () => 0.999;
  server.partyplay.chaseRng = () => 0.5;
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
      send(t, body = {}) { ws.send(JSON.stringify({ t, ...body })); },
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
  const r = await c.req('register', { protocol: PROTOCOL, user: 'hv_' + user, pass: 'secret123' });
  assert.ok(r.ok, r.error);
  assert.ok((await c.req('driver.create', { name: user, car: 0, color: 1 })).ok);
  const e = await c.req('enter', { city });
  assert.ok(e.ok, e.error);
  c.name = user;
  return c;
}
const admin = line => new Promise(r => server.adminCommand(line, r));
const ev = () => server.partyplay.events;

async function party(leader, ...others) {
  for (const o of others) {
    assert.ok((await leader.req('party.invite', { name: o.name, party: 'Huv' })).ok);
    await o.wait(m => m.t === 'party.invite');
    assert.ok((await o.req('party.accept')).ok);
    await leader.wait(m => m.t === 'party.update' && m.party && m.party.members.some(x => x.name === o.name));
  }
}

test('chase units: count by level and party, convoy of 16, speeds, health, time', () => {
  const fixed = () => 0.5;
  assert.deepStrictEqual([1, 29, 30, 59, 60, 90, 200].map(l => rules.unitCount(l)), [1, 1, 2, 2, 3, 4, 7]);
  assert.strictEqual(rules.unitCount(10, 3), 2, 'a party of 3-4 gets one more');
  assert.strictEqual(rules.unitCount(200, 1, true), 1, 'a boss chase is one boss');
  const a = rules.chaseUnits({ level: 1, hp: 9999, huv: 7, rng: fixed });
  assert.strictEqual(a.units.length, 1);
  assert.deepStrictEqual(a.units[0], { huv: 7, kmh: 86, top: 116, boostSeconds: 1, turbo: 10, hp: 250 });
  assert.strictEqual(a.seconds, 150);
  const b = rules.chaseUnits({ level: 65, hp: 150, rng: fixed });
  assert.strictEqual(b.units.length, 3);
  assert.strictEqual(b.seconds, 210, '150 s + 30 s per HUV beyond the first');
  assert.ok(b.units.every(u => u.kmh === 118 && u.top === 148));
  // one chase in ten from level 40 is a convoy: all 16 slots, one speed, 30 health, 240 s
  const c = rules.chaseUnits({ level: 45, rng: () => 0.05 });
  assert.ok(c.convoy);
  assert.strictEqual(c.units.length, 16);
  assert.ok(c.units.every(u => u.hp === 30 && u.kmh === c.units[0].kmh));
  assert.strictEqual(c.seconds, 240);
  assert.ok(!rules.chaseUnits({ level: 39, rng: () => 0.05 }).convoy, 'not below level 40');
  const boss = rules.chaseUnits({ level: 80, boss: true, huv: 3 });
  assert.deepStrictEqual([boss.units.length, boss.units[0].kmh, boss.units[0].hp, boss.seconds], [1, 0, 300, 180], 'the boss uses BossHuvInfo');
  // the kept escorts' threshold: 컨버팅시간 40 % of 내구력
  assert.ok(!rules.splitDue(11001, 0.39) && rules.splitDue(11001, 0.4) && rules.splitDue(11004, 0.5) && !rules.splitDue(11003, 0.99));
});

test('chase.begin: the server decides a solo chase (units, speeds, time)', async () => {
  const a = await driver('SoloHuv');
  try {
    await admin('setlevel SoloHuv 65');
    const r = await a.req('chase.begin', { boss: false, huv: 4, hp: 130 });
    assert.ok(r.ok, r.error);
    assert.strictEqual(r.units.length, 3);
    assert.strictEqual(r.units[0].huv, 4);
    assert.strictEqual(r.units[0].hp, 130);
    assert.strictEqual(r.seconds, 210);
    const b = await a.req('chase.begin', { boss: true, huv: 0 });
    assert.deepStrictEqual([b.units.length, b.units[0].hp, b.seconds], [1, 300, 180]);
  } finally { a.close(); }
});

test('party chase: several units with their own health; 11001 drops Lil\' Grimer at its threshold, the truck is finished 10 s later', async () => {
  const a = await driver('GrimeLead'), b = await driver('GrimeMate');
  try {
    await party(a, b);
    // a common chase of a level 30 leader: two HUVs, both must be stopped
    await admin('setlevel GrimeLead 30');
    assert.ok((await a.req('chase.party.start', { boss: false, huv: 1, hp: 100 })).ok);
    const st = await b.wait(m => m.t === 'chase.party.start');
    assert.strictEqual(st.units.length, 2);
    assert.strictEqual(st.seconds, 180);
    for (let i = 0; i < 3; i++) { offset += 1000; await a.req('chase.party.hit', { damage: 45, unit: 0 }); }
    const h0 = await b.wait(m => m.t === 'chase.party.hp' && m.unit === 0 && m.hp === 0);
    assert.strictEqual(h0.max, 100);
    assert.ok(!b.pushed.some(m => m.t === 'chase.party.end'), 'one HUV still runs');
    for (let i = 0; i < 3; i++) { offset += 1000; await b.req('chase.party.hit', { damage: 45, unit: 1 }); }
    const end1 = await a.wait(m => m.t === 'chase.party.end');
    assert.ok(end1.success);
    await b.wait(m => m.t === 'chase.party.end');

    // the boss 11001: at 40 % damage the trailer stages start, Lil' Grimer (300 / 1400 of the boss's health) comes out
    // 8.367 s later as a unit of its own, and 10 s after that the truck is finished
    assert.ok((await a.req('chase.party.start', { boss: true, huv: 0, bossId: 11001 })).ok);
    const sb = await b.wait(m => m.t === 'chase.party.start' && m.boss);
    assert.strictEqual(sb.bossId, 11001);
    for (let i = 0; i < 2; i++) { offset += 1000; await a.req('chase.party.hit', { damage: 45, unit: 0 }); }
    assert.ok(!b.pushed.some(m => m.t === 'chase.party.split'), '90 of 300 is below the threshold');
    offset += 1000; await a.req('chase.party.hit', { damage: 45, unit: 0 });
    const s1 = await b.wait(m => m.t === 'chase.party.split' && m.stage === 'start');
    assert.ok(Math.abs(s1.seconds - 8.367) < 1e-6);
    assert.match((await a.req('chase.party.hit', { damage: 10, unit: 1 })).error, /no such HUV/, 'Lil\' Grimer is not out yet');
    offset += 8400; server.partyplay.tick();
    const out = await b.wait(m => m.t === 'chase.party.split' && m.stage === 'out');
    assert.deepStrictEqual(out.units, [{ unit: 1, huv: 11002, hp: 64.3 }]);
    offset += 10100; server.partyplay.tick();
    await b.wait(m => m.t === 'chase.party.hp' && m.unit === 0 && m.hp === 0);
    await b.wait(m => m.t === 'chase.party.split' && m.stage === 'finish');
    assert.ok(!b.pushed.some(m => m.t === 'chase.party.end'), 'Lil\' Grimer still runs');
    for (let i = 0; i < 3; i++) { offset += 1000; await b.req('chase.party.hit', { damage: 45, unit: 1 }); }
    const end2 = await a.wait(m => m.t === 'chase.party.end');
    assert.ok(end2.success);
    const base = rewards.chase(30, true);
    assert.ok(end2.rows.find(r => r.name === 'GrimeLead').exp >= base.exp);
  } finally { a.close(); b.close(); }
});

test('rush time: real drivers in the zone, the owner runs the boss, AI stand-ins, the lucky driver\'s prize', async () => {
  const a = await driver('RushOne', 'koinonia'), b = await driver('RushTwo', 'koinonia'), out = await driver('RushOut', 'koinonia');
  try {
    const r = ev().announce('koinonia', { spot: 12, boss: 3, aiWaiting: 1 });
    const na = await a.wait(m => m.t === 'rush.notice');
    assert.deepStrictEqual([na.spot, na.boss, na.min, na.seconds], [12, 3, RUSH.min, RUSH.waitMs / 1000]);
    await out.wait(m => m.t === 'rush.notice');
    assert.ok((await a.req('rush.zone', { id: r.id, in: true })).ok);
    const z = await b.req('rush.zone', { id: r.id, in: true });
    assert.strictEqual(z.drivers, 3, 'two drivers and one stand-in');
    await a.wait(m => m.t === 'rush.wait' && m.drivers === 3);
    offset += RUSH.waitMs + 100; ev().tick();
    const sa = await a.wait(m => m.t === 'rush.start');
    assert.strictEqual(sa.owner, 'RushOne');
    assert.strictEqual(sa.hp, RUSH.hpBase + RUSH.hpPerDriver * 3);
    await b.wait(m => m.t === 'rush.start');
    await out.wait(m => m.t === 'rush.closed');
    // the owner's boss states reach the other drivers only
    assert.ok((await a.req('rush.huv', { cars: [[1, 2, 3, 4, 5]] })).ok);
    assert.deepStrictEqual((await b.wait(m => m.t === 'rush.huv')).cars, [[1, 2, 3, 4, 5]]);
    assert.match((await b.req('rush.huv', { cars: [] })).error, /owner/);
    // while in Rush Time a V-HUV registration is refused (10967) and a party chase cannot start
    assert.match((await a.req('vhuv.register', { size: 4 })).error, /Virtual HUV|register/i);
    // the stand-in rams too (rng 0.999: 12 damage every 4.5 s)
    offset += RUSH.aiMax + 10; ev().tick();
    const hp1 = await a.wait(m => m.t === 'rush.hp' && m.by === 'Rio');
    assert.ok(Math.abs(hp1.hp - (sa.hp - (RUSH.aiDmgMin + 0.999 * (RUSH.aiDmgMax - RUSH.aiDmgMin)))) < 0.2);
    let left = hp1.hp;
    for (let i = 0; left > 0 && i < 40; i++) {
      offset += 1000;
      const h = await (i % 2 ? b : a).req('rush.hit', { damage: 45 });
      assert.ok(h.ok, h.error);
      left = h.hp;
    }
    const ea = await a.wait(m => m.t === 'rush.end'), eb = await b.wait(m => m.t === 'rush.end');
    assert.ok(ea.success && eb.success);
    assert.ok(['RushOne', 'RushTwo'].includes(ea.lucky) && ea.luckyIsDriver);
    const lucky = ea.lucky === 'RushOne' ? ea : eb;
    const row = lucky.rows.find(x => x.name === ea.lucky);
    assert.ok(row.mito >= RUSH.prize, 'the lucky driver gets the prize');
    assert.ok((lucky.driver.Items || []).some(it => it.Id === RUSH.item && it.Count >= RUSH.items), 'three Mittron Enhancers');
    assert.ok(!(await out.wait(m => m.t === 'rush.end', 200).catch(() => null)), 'a driver outside the zone is not in it');
  } finally { a.close(); b.close(); out.close(); }
});

test('rush time: not enough drivers -> not held; the Undercity invite is refused during Rush Time (11146)', async () => {
  const a = await driver('UcAsk', 'moonpalace'), b = await driver('UcRush', 'moonpalace');
  try {
    const r = ev().announce('moonpalace', { spot: 1, aiWaiting: 0 });
    await b.wait(m => m.t === 'rush.notice');
    assert.ok((await b.req('rush.zone', { id: r.id, in: true })).ok);
    await party(a, b);
    await admin('givemito UcAsk 50000');
    const req = await a.req('uc.request', {});
    assert.ok(req.ok, req.error);
    const ar = await b.wait(m => m.t === 'party.uc.autorefuse');
    assert.deepStrictEqual([ar.from, ar.text], ['UcAsk', '11146']);
    await a.wait(m => m.t === 'party.uc.cancel');
    // one driver and no stand-ins: below the minimum of 3, the event is not held
    offset += RUSH.waitMs + 100; ev().tick();
    const e = await b.wait(m => m.t === 'rush.end');
    assert.strictEqual(e.cancelled, true);
  } finally { a.close(); b.close(); }
});

test('virtual HUV: two real drivers on opposite teams, the HUV roles, checked crashes, the result', async () => {
  const a = await driver('VhWhite', 'oros'), b = await driver('VhRed', 'oros');
  try {
    const ra = await a.req('vhuv.register', { size: 4 });
    assert.ok(ra.ok, ra.error);
    assert.strictEqual(ra.waiting, 1);
    await b.req('vhuv.register', { size: 4 });
    await a.wait(m => m.t === 'vhuv.wait' && m.waiting === 2);
    offset += VHUV.fillMs + 100; ev().tick();
    const sa = await a.wait(m => m.t === 'vhuv.start'), sb = await b.wait(m => m.t === 'vhuv.start');
    assert.deepStrictEqual([sa.team, sb.team], ['white', 'red']);
    assert.strictEqual(sa.members.length, 8, 'stand-ins fill both teams');
    assert.strictEqual(sa.channel, sb.channel, 'one private channel');
    assert.ok(/-2\d\d$/.test(sa.channel));
    // the red team's HUV (rng 0.999: its last member, a stand-in); a crash on anyone else does not count
    const redHuv = sa.huv.red;
    assert.match((await a.req('vhuv.crash', { target: 'nobody' })).error, /not the HUV/);
    const c1 = await a.req('vhuv.crash', { target: redHuv });
    assert.ok(c1.ok, c1.error);
    assert.strictEqual(c1.score.white, 1);
    assert.match((await a.req('vhuv.crash', { target: redHuv })).error, /too fast/, 'one crash per 1.5 s');
    // the owner (first driver) sends the stand-in HUV; a crash far from it is refused
    a.send('state', { p: [0, 0, 0], r: 0 });
    assert.ok((await a.req('vhuv.huv', { cars: [null, [500, 0, 500, 0, 20]] })).ok);
    await b.wait(m => m.t === 'vhuv.huv');
    offset += 2000;
    await sleep(30);
    assert.match((await a.req('vhuv.crash', { target: redHuv })).error, /too far/);
    assert.ok((await a.req('vhuv.huv', { cars: [null, [3, 0, 4, 0, 20]] })).ok);
    const c2 = await a.req('vhuv.crash', { target: redHuv });
    assert.ok(c2.ok, c2.error);
    offset += 2000;
    assert.ok((await a.req('vhuv.crash', { target: redHuv })).ok);
    const next = await b.wait(m => m.t === 'vhuv.huv.next' && m.team === 'red');
    assert.notStrictEqual(next.huv.red, redHuv, 'after 3 crashes the role passes on');
    offset += VHUV.matchMs; ev().tick();
    const ea = await a.wait(m => m.t === 'vhuv.end'), eb = await b.wait(m => m.t === 'vhuv.end');
    assert.strictEqual(ea.white, eb.white);
    const rowA = ea.rows.find(r => r.name === 'VhWhite');
    assert.strictEqual(rowA.crashes, 3);
    assert.ok(rowA.exp >= rewards.vhuv(1, 3, ea.won).exp);
    assert.ok(rowA.mito >= rewards.vhuv(1, 3, false).mito);
  } finally { a.close(); b.close(); }
});

test('undercity: UCRESULT badges (EXP drink -> 120%, a full set on the car -> SET)', () => {
  const p = { Buffs: [{ Id: 'exp', Until: Date.now() + 60000 }], Items: [], Cars: [{ PerfSlots: [-1, -1, -1, -1] }], CurrentCar: 0, PerfParts: [] };
  assert.deepStrictEqual(ucBadges(p), { boost120: true, set: false });
  assert.deepStrictEqual(ucBadges({ Buffs: [], Items: [], Cars: [], PerfParts: [] }), { boost120: false, set: false });
});

test('undercity: UC_HUVTimeReduce - a slow HUV group costs half its overrun beyond 60 s (at most 20 s)', () => {
  const uc = server.partyplay.undercity;
  const { WAVE_PAR, WAVE_CUT_MAX } = require('../src/undercity');
  const pushes = [];
  const room = { id: 999, slots: [], hubTaken: new Set(), hubSlots: [], barrierTotal: 0, attempt: 1, wave: 0, waveClockEnd: clock() + 240000, phase: 'mainCleared' };
  const orig = uc.push; uc.push = (r, m) => pushes.push(m);
  try {
    uc.nextWave(room);                       // wave 1: no change
    assert.strictEqual(pushes.pop().clockChange, undefined);
    offset += 50000; uc.nextWave(room);      // 50 s: within the par
    assert.strictEqual(pushes.pop().clockChange, undefined);
    const before = room.waveClockEnd;
    offset += 100000; uc.nextWave(room);     // 100 s: 40 s over -> 20 s off
    const p3 = pushes.pop();
    assert.strictEqual(p3.clockChange, -Math.min(WAVE_CUT_MAX, (100 - WAVE_PAR) / 2));
    assert.strictEqual(room.waveClockEnd, before - 20000);
    offset += 10000; uc.nextWave(room);      // wave 4 after a quick group: no change
    assert.deepStrictEqual([pushes[pushes.length - 1].phase, pushes.pop().clockChange], ['wave', undefined]);
    offset += 90000; uc.nextWave(room);      // the boss wave has its own clock: no change
    const p4 = pushes.pop();
    assert.strictEqual(p4.phase, 'boss');
    assert.strictEqual(p4.clockChange, undefined);
  } finally { uc.push = orig; }
});
