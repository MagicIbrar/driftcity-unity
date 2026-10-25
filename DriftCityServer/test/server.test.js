'use strict';
const { test, before, after } = require('node:test');
const assert = require('node:assert');
const os = require('node:os');
const path = require('node:path');
const fs = require('node:fs');
const WebSocket = require('ws');
const { createServer, PROTOCOL } = require('../server');
const { loadGameData, XltTable } = require('../src/gamedata');

// Tests run with the real game tables when the extracted data exists (DC_GAMEDATA or ..\DriftCityOffline\GameData\Data).
const gd = loadGameData(undefined, () => {});
const needsData = gd ? {} : { skip: 'game data not found' };

let server, port, dir;
let offset = 0;                                   // advances the server clock (races, reward cooldowns)
const clock = () => Date.now() + offset;
const sleep = ms => new Promise(r => setTimeout(r, ms));

before(async () => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), 'dcserver-'));
  server = createServer({ port: 0, db: path.join(dir, 'test.db'), quiet: true, clock, raceCountdownMs: 300, maxConnPerIp: 200, raceGates: false });
  port = await server.listen();
});
after(async () => { await server.close(); fs.rmSync(dir, { recursive: true, force: true }); });

/** Minimal test client: request/reply by rid plus a queue of pushed messages. */
function connect(p = port) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(`ws://127.0.0.1:${p}`);
    const pending = new Map();
    const pushed = [];
    const waiters = [];
    let rid = 1;
    ws.on('message', raw => {
      const m = JSON.parse(raw);
      if (m.t === 'reply' && pending.has(m.rid)) { pending.get(m.rid)(m); pending.delete(m.rid); return; }
      // a waiter consumes the message; otherwise it is queued for a later wait()
      const w = waiters.find(x => x.pred(m));
      if (w) { waiters.splice(waiters.indexOf(w), 1); clearTimeout(w.timer); w.resolve(m); } else pushed.push(m);
    });
    ws.on('error', reject);
    const closed = new Promise(r => ws.on('close', (code) => r(code)));
    const c = {
      ws, pushed, closed,
      req(t, body = {}) { const id = rid++; return new Promise(r => { pending.set(id, r); ws.send(JSON.stringify({ t, rid: id, ...body })); }); },
      send(t, body = {}) { ws.send(JSON.stringify({ t, ...body })); },
      wait(pred, ms = 3000) {
        const hit = pushed.find(pred);
        if (hit) { pushed.splice(pushed.indexOf(hit), 1); return Promise.resolve(hit); }
        return new Promise((resolve, rej) => { const w = { pred, resolve }; waiters.push(w); w.timer = setTimeout(() => { waiters.splice(waiters.indexOf(w), 1); rej(new Error('timeout waiting for message: ' + pred)); }, ms); });
      },
      close() { ws.close(); },
    };
    ws.on('open', () => c.wait(m => m.t === 'welcome').then(() => resolve(c)));
  });
}

async function driver(user, name, car = 0) {
  const c = await connect();
  const reg = await c.req('register', { protocol: PROTOCOL, user, pass: 'secret123' });
  assert.ok(reg.ok, reg.error);
  const d = await c.req('driver.create', { name, car, color: 1 });
  assert.ok(d.ok, d.error);
  return c;
}
const live = name => server.world.findByName(name);

// ---------------------------------------------------------------------------------------------------------------

test('game data: tables parsed with the client rules', needsData, () => {
  assert.strictEqual(gd.starterCars.length, 8, 'GameTables.StarterCars takes 8');
  assert.ok(gd.starterCars.every(c => c.reqLevel <= 1));
  const kicker = gd.car(0);
  assert.strictEqual(kicker.uniqueId, 1);
  assert.strictEqual(gd.carPrice(kicker), 1000, 'first grade price');
  assert.strictEqual(gd.grades(kicker)[0].upgradeMito, 2500);
  // quests: sorted by ReqLevel then Id, goals assigned cyclically in table order
  const qs = gd.quests;
  for (let i = 1; i < qs.length; i++) assert.ok(qs[i - 1].reqLevel < qs[i].reqLevel || (qs[i - 1].reqLevel === qs[i].reqLevel && qs[i - 1].id < qs[i].id));
  const q1 = gd.quest(1);
  assert.strictEqual(q1.goal, 'HuvCaught');
  assert.strictEqual(q1.goalCount, Math.min(1 + 1 + Math.trunc(q1.reqLevel / 15), 6));
  assert.strictEqual(gd.quest(3).goal, 'DistanceKm');
  assert.strictEqual(gd.quest(6).goal, 'CarBought');
  assert.strictEqual(gd.quest(6).goalCount, 1);
  // recipes by column position (the "Count" columns repeat)
  const rc = gd.recipe('enhanplus_skb');
  assert.deepStrictEqual(rc.inputs, [['i_n_00003', 1], ['mitron_cristal', 1]]);
  assert.strictEqual(rc.kit, 'user_create_kit1');
  assert.strictEqual(rc.result, 'i_n_00003_plus');
  assert.strictEqual(rc.chance, 45);
  assert.deepStrictEqual(gd.craftLevelExp, [8610, 66420, 221430, 521640]);
  // licences: GC ids mapped, duplicates collapsed (any one -> min)
  const l = gd.licence(7001);
  assert.strictEqual(l.anyOne, true);
  assert.deepStrictEqual(l.needs, [{ stat: 'RacePoints', value: 500 }]);
  assert.strictEqual(l.effect, 'ME_BattlePersonalMito');
  // parts: 30D price else 7D*3 else per-slot default
  assert.strictEqual(gd.part(10186).slot, 'wheels');
  assert.ok(gd.part(10186).price > 0);
  assert.strictEqual(gd.paints.get(10000), 3000, 'paint default price');
});

test('game data: xlt reader and missing data fallback', () => {
  const text = 'Preset\tx\r\nValue\t1\r\nA\tB\tB\r\n1\t"multi\r\nline ""q"""\t3\r\n\r\n2\tz\t4\r\n';
  const buf = Buffer.concat([Buffer.from([0xFF, 0xFE]), Buffer.from(text, 'utf16le')]);
  const t = new XltTable(buf);
  assert.deepStrictEqual(t.columns, ['A', 'B', 'B']);
  assert.strictEqual(t.col('b'), 1, 'first of duplicate names, case-insensitive');
  assert.strictEqual(t.rows.length, 2, 'empty rows dropped');
  assert.strictEqual(t.rows[0][1], 'multi\r\nline "q"', 'quoted cells keep their \\r, like XltTable.Split');
  assert.strictEqual(t.int(t.rows[1], 'A'), 2);
  const warnings = [];
  assert.strictEqual(loadGameData(path.join(os.tmpdir(), 'no-such-dc-data'), w => warnings.push(w)), null);
  assert.strictEqual(warnings.length, 1);
});

test('register, login, driver rules', async () => {
  const c = await connect();
  assert.strictEqual((await c.req('register', { protocol: PROTOCOL, user: 'ab', pass: 'secret123' })).ok, false, 'short username rejected');
  assert.strictEqual((await c.req('register', { protocol: 99, user: 'alice', pass: 'secret123' })).ok, false, 'protocol mismatch rejected');
  assert.ok((await c.req('register', { protocol: PROTOCOL, user: 'alice', pass: 'secret123' })).ok);
  if (gd) assert.strictEqual((await c.req('driver.create', { name: 'Alice', car: 1, color: 3 })).ok, false, 'only starter cars');
  const d = await c.req('driver.create', { name: 'Alice', car: 2, color: 3, avatar: 6 });
  assert.ok(d.ok);
  assert.strictEqual(d.driver.Level, 1);
  assert.strictEqual(d.driver.Avatar, 6, 'portrait from the character screen');
  assert.match(d.driver.Created, /^\d{4}\.\d{2}\.\d{2}$/, 'licence date');
  assert.strictEqual(d.driver.Cars[0].VehicleIndex, 2);
  assert.deepStrictEqual(d.driver.Cars[0].PerfSlots, [-1, -1, -1, -1]);
  assert.deepStrictEqual(d.driver.Cars[0].GiftParts, []);
  c.close();

  const c2 = await connect();
  assert.strictEqual((await c2.req('login', { protocol: PROTOCOL, user: 'alice', pass: 'wrongpass' })).ok, false);
  const ok = await c2.req('login', { protocol: PROTOCOL, user: 'ALICE', pass: 'secret123' });
  assert.ok(ok.ok, 'usernames are case-insensitive');
  assert.strictEqual(ok.driver.Name, 'Alice', 'driver persisted');
  const dup = await connect();
  await dup.req('register', { protocol: PROTOCOL, user: 'alice2', pass: 'secret123' });
  assert.strictEqual((await dup.req('driver.create', { name: 'alice', car: 0 })).ok, false, 'driver names are unique');
  c2.close(); dup.close();
});

test('a second login kicks the older socket even before a driver exists', async () => {
  const a = await connect();
  assert.ok((await a.req('register', { protocol: PROTOCOL, user: 'twin', pass: 'secret123' })).ok);
  const b = await connect();
  assert.ok((await b.req('login', { protocol: PROTOCOL, user: 'twin', pass: 'secret123' })).ok);
  const kicked = await a.wait(m => m.t === 'kicked');
  assert.match(kicked.reason, /another place/);
  await a.closed;
  const d = await b.req('driver.create', { name: 'Twin', car: 0 });
  assert.ok(d.ok, d.error);
  b.close();
});

test('handler errors reply to the request; bad inputs do not throw', async () => {
  const a = await driver('err', 'Err');
  const orig = server.economy.buyCar;
  server.economy.buyCar = () => { throw new Error('boom'); };
  try {
    const r = await a.req('shop.buyCar', { vehicleIndex: 2 });
    assert.strictEqual(r.ok, false);
    assert.strictEqual(r.error, 'server error');
  } finally { server.economy.buyCar = orig; }
  const n = await a.req('reward', { kind: 'chase', data: null });
  assert.strictEqual(n.ok, false, 'data:null is just a bad claim');
  assert.strictEqual(n.error, 'chase too short');
  const u = await a.req('profile.update', { update: { ActiveQuests: [null, 5, { QuestId: 'x' }] } });
  assert.ok(u.ok);
  assert.ok(u.driver.ActiveQuests.every(q => q && typeof q === 'object'));
  for (const body of [{ id: 7035, needs: [null] }, { recipe: { id: null, inputs: [null] } }, { recipe: null }])
    assert.strictEqual((await a.req(body.recipe !== undefined ? 'craft' : 'licence.earn', body)).ok, false);
  a.close();
});

test('channels relay nearby players and chat; state is clamped', async () => {
  const a = await driver('bob', 'Bob'), b = await driver('cara', 'Cara');
  assert.ok((await a.req('enter', { city: 'cras' })).ok);
  const enterB = await b.req('enter', { city: 'cras' });
  assert.ok(enterB.ok);
  const joined = await a.wait(m => m.t === 'player.joined');
  assert.strictEqual(joined.player.name, 'Cara');

  a.send('state', { p: [100, 5, 100], r: 90, v: [10, 0, 0], s: 36 });
  b.send('state', { p: [120, 5, 110], r: 0, v: [0, 0, 0], s: 0 });
  const snap = await b.wait(m => m.t === 's' && m.u.some(u => u[0] && u[1][0] === 100));
  assert.ok(snap.u[0][1][0] === 100, 'B receives A position');

  // NaN / huge values are rejected or clamped before they are relayed
  a.send('state', { p: [100, 5, 100], r: 'x' });
  a.send('state', { p: [101, 1e9, 100], r: 725, v: [1e6, -1e6, 3], s: 1e5 });
  const clamped = await b.wait(m => m.t === 's' && m.u.some(u => u[1][0] === 101));
  const u = clamped.u.find(x => x[1][0] === 101);
  assert.strictEqual(u[1][1], 5000); assert.strictEqual(u[2], 5);
  assert.deepStrictEqual(u[3], [150, -150, 3]); assert.strictEqual(u[4], 600);

  // far away players are not relayed
  b.send('state', { p: [3000, 5, 3000], r: 0, teleport: true });
  await sleep(150);
  b.pushed.length = 0;
  a.send('state', { p: [102, 5, 100], r: 90 });
  await sleep(250);
  assert.ok(!b.pushed.some(m => m.t === 's'), 'out-of-range updates are filtered');

  a.send('chat', { scope: 'channel', text: 'hello cras' });
  const msg = await b.wait(m => m.t === 'chat' && m.scope === 'channel');
  assert.strictEqual(msg.from, 'Bob');
  a.send('chat', { scope: 'whisper', to: 'cara', text: 'psst' });
  assert.strictEqual((await b.wait(m => m.t === 'chat' && m.scope === 'whisper')).text, 'psst');

  // re-entering the channel you are in keeps it (it used to be deleted and orphaned when you were alone in it)
  server.world.channelFor('cras', 2);
  assert.strictEqual((await a.req('enter', { city: 'cras', channel: 'cras-2' })).channel, 'cras-2');
  assert.strictEqual((await a.req('enter', { city: 'cras', channel: 'cras-2' })).channel, 'cras-2');
  const ch = server.world.channels.get('cras-2');
  assert.ok(ch && ch.players.has(live('Bob').id), 'channel still listed and holds the player');
  a.close(); b.close();
});

test('movement check: speed over a window, warnings decay', () => {
  const { World } = require('../src/world');
  const w = new World();
  let now = 1_000_000;
  const realNow = Date.now;
  Date.now = () => now;
  try {
    let kicked = null;
    const s = { id: 1, name: 'm', kick: r => { kicked = r; s.kicked = true; } };
    let x = 0;
    const drive = (secs, speed) => { for (let i = 0; i < secs * 10; i++) { now += 100; x += speed / 10; w.updateState(s, { p: [x, 0, 0], r: 0 }); } };
    drive(3, 100);
    assert.strictEqual(s.warnings || 0, 0, 'fast but legal driving');
    // one big jitter packet followed by normal packets: one warning at most
    now += 100; x += 400; w.updateState(s, { p: [x, 0, 0], r: 0 });
    drive(1, 50);
    assert.strictEqual(s.warnings, 1);
    drive(11, 50);
    assert.strictEqual(s.warnings, 0, 'warnings decay');
    drive(30, 400);
    assert.strictEqual(kicked, 'movement check failed', 'a sustained speed hack is still kicked');
    // non-finite input is ignored
    const t = { id: 2, kick() {} };
    w.updateState(t, { p: [NaN, 0, 0], r: 0 });
    w.updateState(t, { p: [0, 0, 0], r: Infinity });
    assert.strictEqual(t.state, undefined);
  } finally { Date.now = realNow; }
});

test('parties', async () => {
  const a = await driver('dan', 'Dan'), b = await driver('eve', 'Eve'), c = await driver('fin', 'Fin');
  assert.ok((await a.req('party.invite', { name: 'Eve' })).ok);
  assert.strictEqual((await a.req('party.invite', { name: 'Eve' })).ok, false, 'invite cooldown per target');
  await b.wait(m => m.t === 'party.invite' && m.from === 'Dan');
  assert.ok((await b.req('party.accept')).ok);
  const upd = await a.wait(m => m.t === 'party.update' && m.party);
  assert.strictEqual(upd.party.members.length, 2);
  b.send('chat', { scope: 'party', text: 'gg' });
  assert.strictEqual((await a.wait(m => m.t === 'chat' && m.scope === 'party')).text, 'gg');
  // Fin invites Eve, who is already in a party: refused at invite time, and at accept time
  assert.strictEqual((await c.req('party.invite', { name: 'Eve' })).ok, false);
  live('Eve').pendingInvite = { from: live('Fin').id, at: Date.now() };
  assert.strictEqual((await b.req('party.accept')).ok, false, 'no zombie parties');
  assert.strictEqual(server.world.parties.size, 1);
  await b.req('party.leave');
  await a.wait(m => m.t === 'party.update' && m.party === null);
  a.close(); b.close(); c.close();
});

test('race lifecycle: lap checks, plausibility, rewards and the reward cooldown', async () => {
  const a = await driver('fay', 'Fay'), b = await driver('gus', 'Gus');
  await a.req('enter', { city: 'oros' }); await b.req('enter', { city: 'oros' });
  const host = await a.req('race.host', { track: { key: 'oros/track1', name: 'Oros 1', laps: 1, length: 300 } });
  assert.ok(host.ok, host.error);
  assert.strictEqual(server.races.rooms.get(host.id).track.length, 1500, 'declared length clamped');
  assert.ok((await b.req('race.join', { id: host.id })).ok);
  assert.ok((await b.req('race.ready', { ready: true })).ok);
  assert.strictEqual((await b.req('race.ready', { ready: false })).ok, false, 'ready toggle cooldown');
  assert.ok((await a.req('race.startRace')).ok);
  await a.wait(m => m.t === 'race.start');
  await a.wait(m => m.t === 'race.go');
  // B "finishes" instantly: a 1500 m lap in under a second is rejected
  b.send('race.progress', { lap: 1, idx: 0, finished: true });
  await b.wait(m => m.t === 'race.rejected');
  // "finished" before the last lap is ignored
  a.send('race.progress', { lap: 0, idx: 5, finished: true });
  await sleep(150);
  assert.ok(!a.pushed.some(m => m.t === 'race.results'));
  offset += 25000;   // 1500 m at <= 75 m/s needs 20 s
  const before = live('Fay').profile.Mito;
  a.send('race.progress', { lap: 1, idx: 0, finished: true });
  const res = await a.wait(m => m.t === 'race.results', 5000);
  const mine = res.results.find(r => r.name === 'Fay');
  assert.strictEqual(mine.place, 1);
  assert.strictEqual(mine.reward.exp, 450, 'field of 2: no field bonus');
  assert.ok(res.results.find(r => r.name === 'Gus').dnf, 'implausible finisher is DNF');
  const p = live('Fay').profile;
  assert.ok(p.Mito > before && p.RacesWon === 1 && p.RacesEntered === 1, 'winner rewarded by the server');
  assert.ok(p.BestLapSeconds >= 25 && p.BestLapSeconds < 30, 'best lap approximated from the finish time');

  // a second race right away: results, but no reward (60 s per account)
  await sleep(1050);
  const h2 = await a.req('race.host', { track: { key: 'oros/track1', laps: 1, length: 300 } });
  assert.ok((await b.req('race.join', { id: h2.id })).ok);
  assert.ok((await b.req('race.ready', { ready: true })).ok);
  assert.ok((await a.req('race.startRace')).ok);
  await a.wait(m => m.t === 'race.go');
  offset += 25000;
  a.send('race.progress', { lap: 1, idx: 0, finished: true });
  await sleep(100);
  await b.req('race.leave');   // the last active racer leaves: results now instead of after the 30 s grace
  const res2 = await a.wait(m => m.t === 'race.results', 5000);
  const mine2 = res2.results.find(r => r.name === 'Fay');
  assert.strictEqual(mine2.place, 1);
  assert.strictEqual(mine2.reward, null);
  assert.strictEqual(mine2.noReward, 'cooldown');
  assert.strictEqual(live('Fay').profile.RacesWon, 1);
  a.close(); b.close();
});

test('race: a finisher who disconnected is rewarded in the database; empty rooms clear their timers', async () => {
  const c = await driver('hank', 'Hank'), d = await driver('dee', 'Dee');
  await c.req('enter', { city: 'koinonia' }); await d.req('enter', { city: 'koinonia' });
  const host = await c.req('race.host', { track: { key: 'koinonia/t2', laps: 1, length: 1500 } });
  await d.req('race.join', { id: host.id });
  await d.req('race.ready', { ready: true });
  assert.ok((await c.req('race.startRace')).ok);
  await d.wait(m => m.t === 'race.go');
  offset += 25000;
  d.send('race.progress', { lap: 1, idx: 0, finished: true });
  await sleep(100);
  d.close();
  await d.closed;
  await sleep(100);
  const room = server.races.rooms.get(host.id);
  assert.ok(room && room.grace, 'grace timer running for the others');
  await c.req('race.leave');
  assert.ok(!server.races.rooms.has(host.id), 'room removed');
  assert.strictEqual(room.grace, null, 'timers cleared');
  const row = server.store.driver(server.store.account('dee').id);
  assert.strictEqual(row.profile.RacesWon, 1, 'offline finisher rewarded in the db');
  c.close();
});

test('economy: rewards are server-computed and rate limited; purchases use the tables', async () => {
  const a = await driver('hal', 'Hal');
  assert.strictEqual((await a.req('reward', { kind: 'chase', data: { seconds: 2 } })).ok, false, 'too-short chase rejected');
  const r = await a.req('reward', { kind: 'chase', data: { seconds: 40, exp: 999999 } });
  assert.ok(r.ok);
  assert.strictEqual(r.exp, 360, 'client-supplied amounts are ignored');
  assert.strictEqual((await a.req('reward', { kind: 'chase', data: { seconds: 40 } })).ok, false, 'cooldown');
  offset += 8100;
  assert.ok((await a.req('reward', { kind: 'chase', data: { seconds: 9 } })).ok, '8 s cooldown');
  const solo = await a.req('reward', { kind: 'race', data: { place: 1, bestLap: 61.5 } });
  assert.strictEqual(solo.exp, 400 + 50 * solo.driver.Level - (solo.levelUps ? 50 * solo.levelUps : 0), 'solo race: no field bonus');
  assert.strictEqual(solo.mito, 3000 + 300 * (solo.driver.Level - solo.levelUps));
  assert.strictEqual(solo.driver.BestLapSeconds, 61.5);
  // speed combo milestones: only every 10th combo, one material roll, no exp or mito
  assert.strictEqual((await a.req('reward', { kind: 'combo', data: { count: 7 } })).ok, false, 'not a milestone');
  const combo = await a.req('reward', { kind: 'combo', data: { count: 10 } });
  assert.ok(combo.ok && combo.drop && combo.exp === 0 && combo.mito === 0, 'combo milestone rolls a material');
  assert.ok(combo.driver.Items.some(it => it.Id === combo.drop), 'the material lands in the bag');
  assert.strictEqual((await a.req('reward', { kind: 'combo', data: { count: 20 } })).ok, false, 'combo cooldown');
  if (!gd) { a.close(); return; }

  assert.strictEqual((await a.req('shop.buyCar', { vehicleIndex: 99999, price: 1000, reqLevel: 1 })).ok, false, 'unknown car');
  const rare = gd.carList.find(c => c.reqLevel > 1);
  assert.match((await a.req('shop.buyCar', { vehicleIndex: rare.index, price: 1000, reqLevel: 1 })).error, /needs level/, 'level from the table');
  const car = gd.carList.find(c => c.index !== 0 && c.reqLevel <= 1);
  const mito = live('Hal').profile.Mito;
  const buy = await a.req('shop.buyCar', { vehicleIndex: car.index, price: 1, reqLevel: 0, color: 2 });
  assert.ok(buy.ok, buy.error);
  assert.strictEqual(buy.driver.Mito, mito - gd.carPrice(car), 'price from the table, not the client');
  assert.strictEqual((await a.req('shop.buyCar', { vehicleIndex: car.index })).ok, false, 'already owned');

  assert.strictEqual((await a.req('shop.upgrade', { slot: 0, grade: 3, cost: 10 })).ok, false, 'one grade at a time');
  assert.match((await a.req('shop.upgrade', { slot: 0, grade: 1, cost: 1, reqLevel: 0 })).error, /needs level/, 'level gate from the table');
  live('Hal').profile.Level = gd.grades(gd.car(0))[1].reqLevel;
  const before = live('Hal').profile.Mito;
  const up = await a.req('shop.upgrade', { slot: 0, grade: 1, cost: 1, reqLevel: 0 });
  assert.ok(up.ok, up.error);
  assert.strictEqual(up.driver.Mito, before - Math.max(500, gd.grades(gd.car(0))[0].upgradeMito));
  const prof = live('Hal').profile;
  prof.Cars[0].Grade = gd.grades(gd.car(0)).length - 1; prof.Level = 120; prof.Mito = 1e9;
  assert.match((await a.req('shop.upgrade', { slot: 0, grade: prof.Cars[0].Grade + 1 })).error, /top grade/);

  const paint = [...gd.paints][0];
  const m0 = prof.Mito;
  const pr = await a.req('shop.paint', { slot: 0, itemId: paint[0], price: 0 });
  assert.ok(pr.ok);
  assert.strictEqual(pr.driver.Mito, m0 - paint[1], 'paint price from the table');
  assert.strictEqual((await a.req('shop.paint', { slot: 0, itemId: 1, price: 0 })).ok, false, 'unknown paint');

  // givemito can't make Mito negative
  server.adminCommand('givemito Hal -99999999999', () => {});
  assert.strictEqual(prof.Mito, 0);
  a.close();
});

test('quests: accepted by the client rule, progress bounded by the server, reward from the table', needsData, async () => {
  const a = await driver('quinn', 'Quinn');
  const q = gd.quests.find(x => x.goal === 'HuvCaught');
  const p = live('Quinn').profile;
  assert.strictEqual((await a.req('reward', { kind: 'quest', data: { questId: q.id, reqLevel: 1 } })).error, 'quest not accepted');
  let u = await a.req('profile.update', { update: { ActiveQuests: [{ QuestId: q.id, Step: 0, Counter: 0 }] } });
  if (q.reqLevel > 10) assert.strictEqual(u.driver.ActiveQuests.length, 0, 'level too low to accept');
  p.Level = q.reqLevel;
  u = await a.req('profile.update', { update: { ActiveQuests: [{ QuestId: q.id, Step: 0, Counter: 99 }, { QuestId: q.id }, { QuestId: 999999 }] } });
  assert.deepStrictEqual(u.driver.ActiveQuests, [{ QuestId: q.id, Step: 0, Counter: 0 }], 'no progress without catching HUVs; dupes/unknown dropped');
  assert.strictEqual((await a.req('reward', { kind: 'quest', data: { questId: q.id } })).error, 'quest objective not done');
  for (let i = 0; i < q.goalCount + 2; i++) { offset += 8100; assert.ok((await a.req('reward', { kind: 'chase', data: { seconds: 20 } })).ok); }
  u = await a.req('profile.update', { update: { ActiveQuests: [{ QuestId: q.id, Step: 0, Counter: 999 }] } });
  assert.strictEqual(u.driver.ActiveQuests[0].Counter, q.goalCount, 'capped at the goal');
  // the client's counter may lag (it reports a catch before the reward that counts it): the server's count decides
  u = await a.req('profile.update', { update: { ActiveQuests: [{ QuestId: q.id, Step: 0, Counter: 0 }] } });
  assert.strictEqual(u.driver.ActiveQuests[0].Counter, 0);
  offset += 6000;
  const lvl = p.Level, exp = p.Exp;
  const r = await a.req('reward', { kind: 'quest', data: { questId: q.id, reqLevel: 1 } });
  assert.ok(r.ok, r.error);
  assert.strictEqual(r.exp, q.rewardExp);
  assert.strictEqual(r.mito, q.rewardMito);
  assert.ok(r.driver.Level > lvl || r.driver.Exp > exp);
  assert.strictEqual(r.driver.ActiveQuests.length, 0);
  offset += 6000;
  assert.strictEqual((await a.req('reward', { kind: 'quest', data: { questId: q.id } })).ok, false, 'quest rewards once');
  u = await a.req('profile.update', { update: { ActiveQuests: [{ QuestId: q.id, Counter: 6 }] } });
  assert.strictEqual(u.driver.ActiveQuests.length, 0, 'completed quests cannot be re-activated');
  a.close();
});

test('custom shop parts, fitting, resale and car looks', needsData, async () => {
  const a = await driver('ivy', 'Ivy'), b = await driver('jon', 'Jon');
  assert.ok((await a.req('enter', { city: 'cras' })).ok);
  assert.ok((await b.req('enter', { city: 'cras' })).ok);
  await a.wait(m => m.t === 'player.joined');
  const kicker = gd.car(0);
  const wheels = [...gd.parts.values()].find(p => p.slot === 'wheels' && gd.fits(p, kicker) && p.price <= 30000);
  const alien = [...gd.parts.values()].find(p => p.car && p.car !== kicker.modelBase);

  assert.strictEqual((await a.req('shop.buyPart', { slot: 0, item: 1, part: 'wheels', price: 15000 })).ok, false, 'unknown part');
  assert.strictEqual((await a.req('shop.buyPart', { slot: 0, item: alien.id, part: alien.slot, price: 1000 })).ok, false, 'part for another car');
  const buy = await a.req('shop.buyPart', { slot: 0, item: wheels.id, part: 'jetpack', price: 5 });
  assert.ok(buy.ok, buy.error);
  assert.strictEqual(buy.driver.Cars[0].Wheels, wheels.id, 'bought part is fitted in its table slot');
  // bought for keeps: the unlimited price (3 x the 30-day price) when the part has period prices (src/vshop.js)
  const p30 = (wheels.periods || []).find(([d]) => d === 30);
  assert.strictEqual(buy.driver.Mito, 50000 - (p30 ? p30[1] * 3 : wheels.price), 'table price');
  const look = await b.wait(m => m.t === 'player.look');
  assert.strictEqual(look.player.parts.wheels, wheels.id, 'others see the new wheels');
  assert.strictEqual((await a.req('shop.buyPart', { slot: 0, item: wheels.id })).ok, false, 'already owned');

  assert.strictEqual((await a.req('shop.fitPart', { slot: 0, part: 'wing', item: 10163 })).ok, false, 'cannot fit unowned part');
  assert.strictEqual((await a.req('shop.fitPart', { slot: 0, part: 'wing', item: wheels.id })).ok, false, 'wrong slot');
  const stock = await a.req('shop.fitPart', { slot: 0, part: 'wheels', item: -1 });
  assert.ok(stock.ok);
  assert.strictEqual(stock.driver.Cars[0].Wheels, -1, 'back to stock');

  // resale: grade Sell (or price/20) + 30% of the parts bought for the car; item-granted parts don't count
  const p = live('Ivy').profile;
  p.Mito = 1_000_000;
  assert.strictEqual((await a.req('shop.sellCar', { slot: 0, price: 100 })).ok, false, 'cannot sell the last car');
  const car = gd.carList.find(c => c.index !== 0 && c.reqLevel <= 1);
  assert.ok((await a.req('shop.buyCar', { vehicleIndex: car.index })).ok);
  const part = [...gd.parts.values()].find(x => x.slot === 'wheels' && gd.fits(x, car));
  assert.ok((await a.req('shop.buyPart', { slot: 1, item: part.id })).ok);
  p.Items.push({ Id: 'i_n_roof_jackbox', Count: 1 });
  const used = await a.req('item.use', { id: 'i_n_roof_jackbox', slot: 1 });
  assert.ok(used.ok, used.error);
  assert.deepStrictEqual(used.driver.Cars[1].GiftParts, [10890]);
  const g0 = gd.grade(car, 0);
  const expected = (g0.sell > 0 ? g0.sell : Math.trunc(gd.carPrice(car) / 20)) + Math.trunc(part.price * 3 / 10);
  const mito = p.Mito;
  const sold = await a.req('shop.sellCar', { slot: 1, price: 50_000_000 });
  assert.ok(sold.ok, sold.error);
  assert.strictEqual(sold.mito, expected, 'server-computed resale price');
  assert.strictEqual(sold.driver.Mito, mito + expected);
  assert.strictEqual(sold.driver.Cars.length, 1);
  assert.strictEqual(sold.driver.CurrentCar, 0);
  a.close(); b.close();
});

test('visual slots are shown to others; looks are coalesced', needsData, async () => {
  const a = await driver('kim', 'Kim'), b = await driver('lee', 'Lee');
  assert.ok((await a.req('enter', { city: 'cras' })).ok);
  assert.ok((await b.req('enter', { city: 'cras' })).ok);
  await a.wait(m => m.t === 'player.joined');
  live('Kim').profile.Mito = 10_000_000;
  const kicker = gd.car(0);
  const bought = {};
  for (const slot of ['plate', 'roof', 'tint', 'decal', 'horn', 'engine']) {
    const part = [...gd.parts.values()].find(x => x.slot === slot && gd.fits(x, kicker));
    const r = await a.req('shop.buyPart', { slot: 0, item: part.id, part: slot });
    assert.ok(r.ok, `${slot}: ${r.error}`);
    bought[slot] = part.id;
  }
  const look = await b.wait(m => m.t === 'player.look' && m.player.parts.engine === bought.engine, 3000);
  assert.strictEqual(look.player.parts.plate, bought.plate);
  assert.ok(b.pushed.filter(m => m.t === 'player.look').length <= 2, 'at most one look per second');
  a.close(); b.close();
});

test('rate limits and saves: workshop failures do not save; flooding terminates the socket', async () => {
  const a = await driver('sam', 'Sam');
  const orig = server.store.saveDriver.bind(server.store);
  let saves = 0;
  server.store.saveDriver = (...x) => { saves++; return orig(...x); };
  try {
    assert.strictEqual((await a.req('perf.buy', { type: 9, grade: 0 })).ok, false);
    assert.strictEqual(saves, 0, 'no save on failure');
    assert.ok((await a.req('perf.buy', { type: 0, grade: 0 })).ok);
    assert.strictEqual(saves, 1);
  } finally { server.store.saveDriver = orig; }
  for (let i = 0; i < 400; i++) a.send('ping', { at: i });   // past the 240-message burst allowance
  await a.wait(m => m.t === 'kicked' && m.reason === 'flooding');
  await a.closed;
});

test('login hardening: connection cap, login timeout, attempt limits with backoff', async () => {
  const d2 = fs.mkdtempSync(path.join(os.tmpdir(), 'dcserver-'));
  const s2 = createServer({ port: 0, db: path.join(d2, 't.db'), quiet: true, gamedata: null, maxConnPerIp: 3, loginTimeoutMs: 400, maxLoginFailsPerSocket: 2, ipFailsBeforeBackoff: 3 });
  const p2 = await s2.listen();
  try {
    const three = [await connect(p2), await connect(p2), await connect(p2)];
    const fourth = new WebSocket(`ws://127.0.0.1:${p2}`);
    const code = await new Promise(r => fourth.on('close', c => r(c)));
    assert.strictEqual(code, 4001, 'fourth socket from one address refused');
    // not logged in within the timeout: closed
    for (const c of three) await c.closed;

    const reg = await connect(p2);
    assert.ok((await reg.req('register', { protocol: PROTOCOL, user: 'vic', pass: 'secret123' })).ok);
    const t0 = Date.now();
    const x = await connect(p2);
    assert.strictEqual((await x.req('login', { protocol: PROTOCOL, user: 'vic', pass: 'nopenope' })).ok, false);
    assert.strictEqual((await x.req('login', { protocol: PROTOCOL, user: 'vic', pass: 'nopenope' })).ok, false);
    await x.wait(m => m.t === 'kicked');
    await x.closed;
    assert.ok(Date.now() - t0 < 2000, 'scrypt runs off the event loop');
    const y = await connect(p2);
    assert.strictEqual((await y.req('login', { protocol: PROTOCOL, user: 'vic', pass: 'nopenope' })).ok, false);
    const blocked = await y.req('login', { protocol: PROTOCOL, user: 'vic', pass: 'secret123' });
    assert.strictEqual(blocked.ok, false);
    assert.match(blocked.error, /too many failed logins/, 'per-address backoff');
    reg.close(); y.close();
  } finally {
    await s2.close();
    fs.rmSync(d2, { recursive: true, force: true });
  }
});

test('without game data the built-in rules apply', async () => {
  const d3 = fs.mkdtempSync(path.join(os.tmpdir(), 'dcserver-'));
  const s3 = createServer({ port: 0, db: path.join(d3, 't.db'), quiet: true, gamedata: null });
  const p3 = await s3.listen();
  try {
    const c = await connect(p3);
    await c.req('register', { protocol: PROTOCOL, user: 'olga', pass: 'secret123' });
    assert.ok((await c.req('driver.create', { name: 'Olga', car: 1 })).ok);
    assert.ok((await c.req('shop.buyCar', { vehicleIndex: 5, price: 20000, reqLevel: 1 })).ok);
    assert.strictEqual((await c.req('shop.buyPart', { slot: 0, item: 10186, part: 'wheels', price: 5 })).ok, false, 'implausible price');
    assert.ok((await c.req('shop.buyPart', { slot: 0, item: 10186, part: 'wheels', price: 15000 })).ok);
    assert.strictEqual((await c.req('shop.sellCar', { slot: 1, price: 50000 })).ok, false, 'resale capped by what was paid');
    assert.ok((await c.req('shop.sellCar', { slot: 1, price: 10000 })).ok);
    c.close();
  } finally {
    await s3.close();
    fs.rmSync(d3, { recursive: true, force: true });
  }
});

test('workshop: licences, performance parts, counter, manufacturing, item use', () => {
  const { Workshop, ensureWorkshop, rewardExtras } = require('../src/workshop');
  const { newProfile } = require('../src/economy');
  let roll = 0.0;
  const w = new Workshop(() => roll);
  const p = ensureWorkshop(newProfile('Max', 0, 1));
  p.Mito = 1_000_000; p.Level = 30;

  // licences: conditions are checked against the server's own counters
  assert.strictEqual(w.handle('licence.earn', p, { id: 7038, needs: [{ stat: 'HuvCaught', value: 5 }] }).ok, false);
  p.HuvCaught = 5;
  assert.ok(w.handle('licence.earn', p, { id: 7038, needs: [{ stat: 'HuvCaught', value: 5 }] }).ok);
  assert.strictEqual(w.handle('licence.mount', p, { id: 9999, effect: 'ME_AllExpMito', value: 15 }).ok, false, 'unearned');
  assert.ok(w.handle('licence.mount', p, { id: 7038, effect: 'ME_ChasingExpMito', value: 99 }).ok);
  assert.strictEqual(p.MountedEffect.value, 20, 'bonus capped');
  const extra = rewardExtras(p, 'chase', { exp: 1000, mito: 1000 }, {}, () => 0.9);
  assert.strictEqual(extra.exp, 200, 'mounted licence bonus applies to chases');
  const race = rewardExtras(p, 'race', { exp: 100, mito: 100 }, { place: 1, city: 'oros' }, () => 0.9);
  assert.strictEqual(p.RacesOros, 1); assert.strictEqual(p.RacePoints, 100); assert.strictEqual(race.exp, 0);

  // parts: buy, fit, enhance (dice on the server), grind, combine
  assert.strictEqual(w.handle('perf.buy', p, { type: 0, grade: 2 }).ok, false, 'level gate');
  assert.ok(w.handle('perf.buy', p, { type: 0, grade: 1 }).ok);
  assert.ok(w.handle('perf.buy', p, { type: 0, grade: 1 }).ok);
  const [a, b] = p.PerfParts;
  assert.ok(w.handle('perf.fit', p, { slot: 0, type: 0, uid: a.Uid }).ok);
  assert.strictEqual(p.Cars[0].PerfSlots[0], a.Uid);
  assert.strictEqual(w.handle('perf.enhance', p, { uid: a.Uid, enhancer: 'i_n_00003' }).ok, false, 'needs an enhancer');
  assert.ok(w.handle('item.buy', p, { id: 'i_n_00003', count: 3 }).ok);
  assert.strictEqual(w.handle('item.buy', p, { id: 'gold_bar', count: 1 }).ok, false, 'counter sells only its own items');
  roll = 0.0; assert.strictEqual(w.handle('perf.enhance', p, { uid: a.Uid, enhancer: 'i_n_00003' }).error, undefined);
  assert.strictEqual(a.Enhance, 1); assert.strictEqual(p.MaxEnhance, 1);
  roll = 0.999; assert.strictEqual(w.handle('perf.enhance', p, { uid: a.Uid, enhancer: 'i_n_00003' }).error, 'failed');
  assert.strictEqual(a.Enhance, 1, 'low levels do not drop on failure');
  assert.strictEqual(w.handle('perf.combine', p, { a: a.Uid, b: b.Uid, seed: 'i_n_Seed_S' }).ok, false, 'fitted part');
  w.handle('perf.fit', p, { slot: 0, type: 0, uid: -1 });
  assert.ok(w.handle('item.buy', p, { id: 'i_n_Seed_S', count: 1 }).ok);
  roll = 0.1; assert.ok(w.handle('perf.combine', p, { a: a.Uid, b: b.Uid, seed: 'i_n_Seed_S' }).ok);
  assert.strictEqual(p.PerfParts.length, 1); assert.strictEqual(p.PerfParts[0].Grade, 2, 'combined into a Hyper part');
  assert.ok(w.handle('perf.grind', p, { uid: p.PerfParts[0].Uid }).ok);
  assert.strictEqual(p.Items.find(i => i.Id === 'mitrondust_H').Count, 1);

  // manufacturing
  const recipe = { id: 'h_mcristal_skb', inputs: [['mitrondust_H', 1]], kit: 'user_create_kit2', result: 'mitron_cristal', count: 1, chance: 45, exp: 100, failExp: 10 };
  assert.strictEqual(w.handle('craft', p, { recipe }).ok, false, 'skill book first');
  assert.ok(w.handle('skill.learn', p, { id: 'h_mcristal_skb', group: 'low', reqLv: 1 }).ok);
  assert.strictEqual(w.handle('craft', p, { recipe }).ok, false, 'needs the kit');
  assert.ok(w.handle('item.buy', p, { id: 'user_create_kit2', count: 1 }).ok);
  roll = 0.1; assert.ok(w.handle('craft', p, { recipe: { ...recipe, chance: 999 } }).ok);
  assert.strictEqual(p.Items.find(i => i.Id === 'mitron_cristal').Count, 1); assert.strictEqual(p.CraftExp, 100);

  // item use
  p.Items.push({ Id: 'i_n_roof_jackbox', Count: 1 });
  assert.ok(w.handle('item.use', p, { id: 'i_n_roof_jackbox', slot: 0 }).ok);
  assert.strictEqual(p.Cars[0].Roof, 10890);
  assert.deepStrictEqual(p.Cars[0].GiftParts, [10890], 'item-granted part recorded');
  assert.strictEqual(w.handle('item.use', p, { id: 'i_n_roof_jackbox', slot: 0 }).ok, false, 'used up');
});

test('workshop with the game tables: licences, skill books and recipes by id only', needsData, () => {
  const { Workshop, ensureWorkshop, rewardExtras } = require('../src/workshop');
  const { newProfile } = require('../src/economy');
  let roll = 0.0;
  const w = new Workshop(() => roll, gd);
  const p = ensureWorkshop(newProfile('Tab', 0, 1));
  p.Mito = 10_000_000; p.Level = 30;

  const lic = gd.licence(7035);   // HuvCaught 500, ME_ChasingExpMito 3
  assert.strictEqual(w.handle('licence.earn', p, { id: 7035, needs: [{ stat: 'HuvCaught', value: 1 }], any: true }).ok, false, 'client needs ignored');
  p.HuvCaught = lic.needs[0].value;
  assert.ok(w.handle('licence.earn', p, { id: 7035 }).ok);
  assert.ok(w.handle('licence.mount', p, { id: 7035, effect: 'ME_AllExpMito', value: 20 }).ok);
  assert.deepStrictEqual(p.MountedEffect, { id: 7035, effect: lic.effect, value: lic.value }, 'effect from the table');
  assert.strictEqual(rewardExtras(p, 'chase', { exp: 1000, mito: 1000 }, {}, () => 0.9, gd).exp, 10 * lic.value);
  assert.strictEqual(w.handle('licence.earn', p, { id: 123456, needs: [{ stat: 'Quests', value: 1 }] }).ok, false, 'unknown licence');

  // skill books: price by the table's group, level from the CraftExp thresholds
  assert.strictEqual(w.handle('skill.learn', p, { id: 'enhandeluxe_skb', group: 'low', reqLv: 1 }).error, 'needs manufacturing level 3');
  const m0 = p.Mito;
  assert.ok(w.handle('skill.learn', p, { id: 'enhanplus_skb', group: 'very high', reqLv: 99 }).ok);
  assert.strictEqual(p.Mito, m0 - 20000, "group 'low' price");
  assert.strictEqual(w.handle('skill.learn', p, { id: 'made_up_skb' }).ok, false);

  // craft: inputs, kit, odds, result and exp from the table; the client's recipe is ignored
  const fake = { id: 'enhanplus_skb', inputs: [], kit: 'user_create_kit1', result: 'gold', count: 5, chance: 999, exp: 1000 };
  p.Items.push({ Id: 'user_create_kit1', Count: 2 }, { Id: 'i_n_00003', Count: 2 }, { Id: 'mitron_cristal', Count: 2 });
  roll = 0.5; assert.strictEqual(w.handle('craft', p, { recipe: fake }).error, 'failed', '45% chance from the table');
  assert.strictEqual(p.CraftExp, 10);
  roll = 0.1; assert.ok(w.handle('craft', p, { recipe: fake }).ok);
  assert.strictEqual(p.Items.find(i => i.Id === 'i_n_00003_plus').Count, 1);
  assert.ok(!p.Items.some(i => i.Id === 'gold'));
  assert.strictEqual(p.CraftExp, 110);
  assert.strictEqual(w.handle('craft', p, { recipe: fake }).error, 'missing materials');

  // item use: the top grade comes from the car's grade table
  const top = gd.grades(gd.car(0)).length - 1;
  p.Items.push({ Id: 'i_n_upgrade', Count: 2 });
  p.Cars[0].Grade = top;
  assert.strictEqual(w.handle('item.use', p, { id: 'i_n_upgrade', slot: 0 }).ok, false);
  p.Cars[0].Grade = top - 1;
  assert.ok(w.handle('item.use', p, { id: 'i_n_upgrade', slot: 0 }).ok);
});

test('main quests run in story order with a ramping level gate', { skip: !gd }, () => {
  const { Economy, newProfile } = require('../src/economy');
  const q = gd.quests;
  assert.ok(q.length >= 30);
  assert.strictEqual(q[0].reqLevel, 1, 'first story quest is open to new drivers');
  assert.ok(q.every((x, i) => i === 0 || x.reqLevel >= q[i - 1].reqLevel), 'gate only rises');
  assert.ok(q[q.length - 1].reqLevel <= 80);
  assert.strictEqual(q[1].requires, q[0].id, 'second quest needs the first');
  const eco = new Economy(() => {}, gd);
  const p = newProfile('Quinn', gd.starterCars[0].index, 0);
  p.Level = 10;
  eco.applyUpdate(p, { ActiveQuests: [{ QuestId: q[1].id, Step: 0, Counter: 0 }] });
  assert.strictEqual(p.ActiveQuests.length, 0, 'second quest refused before the first is done');
  eco.applyUpdate(p, { ActiveQuests: [{ QuestId: q[0].id, Step: 0, Counter: 0 }] });
  assert.strictEqual(p.ActiveQuests.length, 1, 'first quest accepted at level 10');
});

test('undercity: counts bounded, the clear checked, UndercityData.Reward paid minus the portal fee, cooldown', () => {
  const { Economy, newProfile, rewards } = require('../src/economy');
  let now = 2e9;
  const eco = new Economy(() => {}, null, () => now);
  const p = newProfile('Tatiana', 0, 0);
  p.Level = 40; p.Mito = 10000;
  const s = { profile: p };
  const run = { hubs: 87, jumps: 10, barriers: 15, mainHub: 1, huvs: 28, boss: 1 };
  // the C# formula: UndercityData.Reward(40, ...) for a cleared run
  const want = { exp: 15 * 87 + 50 * 10 + 40 * 15 + 400 + 120 * 28 + 1500 + 1000 + 100 * 40,
                 mito: 40 * 87 + 150 * 10 + 100 * 15 + 1500 + 400 * 28 + 8000 + 5000 + 300 * 40 - 2000 };
  assert.deepStrictEqual(rewards.undercity(40, run, true), want);
  assert.match(eco.claim(s, 'undercity', { ...run, hubs: 91, cleared: true, seconds: 600 }).error, /too many hubs/);
  assert.match(eco.claim(s, 'undercity', { ...run, huvs: -1, cleared: true, seconds: 600 }).error, /bad huvs/);
  assert.match(eco.claim(s, 'undercity', { ...run, boss: 0, cleared: true, seconds: 600 }).error, /not cleared/);
  assert.match(eco.claim(s, 'undercity', { ...run, cleared: true, seconds: 20 }).error, /too fast/);
  const ok = eco.claim(s, 'undercity', { ...run, cleared: true, seconds: 600, exp: 1e9 });
  assert.ok(ok.ok, ok.error);
  assert.deepStrictEqual([ok.exp, ok.mito], [want.exp, want.mito], 'the server computes the amounts');
  assert.ok(ok.drop, 'a cleared run brings an item');
  assert.strictEqual(p.Mito, 10000 + want.mito);
  assert.strictEqual(p.HuvCaught, 29); assert.strictEqual(p.BossesCaught, 1);
  assert.match(eco.claim(s, 'undercity', { ...run, cleared: true, seconds: 600 }).error, /too soon/);
  // a failed run: the counts so far, no clear bonus, the fee comes out (never below zero Mito)
  now += 121000;
  p.Mito = 2500;
  const failed = eco.claim(s, 'undercity', { hubs: 5, jumps: 0, barriers: 0, mainHub: 0, huvs: 0, boss: 0, cleared: false, seconds: 200 });
  assert.ok(failed.ok, failed.error);
  assert.deepStrictEqual([failed.exp, failed.mito, failed.drop], [75, 200 - 2000, null]);
  assert.strictEqual(p.Mito, 700);
});

test('station missions: delivery medals by time, instant and patrol rewards from the tables', { skip: !gd }, () => {
  const { Economy, newProfile, loadMissions, evalFormula } = require('../src/economy');
  let now = 1e9;
  const eco = new Economy(() => {}, gd, () => now);
  const p = newProfile('Dina', gd.starterCars[0].index, 0);
  p.Level = 30;
  const s = { profile: p };
  const m = loadMissions(gd.dir);
  assert.ok(m.jobs.size >= 60, 'the regular delivery jobs of ArbeitClient.tdf');
  // a_00041: Cras Station 1 "[Medium] Blueprint Delivery": gold 80 s 900 EXP 1100 Mito, silver 85 s, bronze 95 s, up to level 61
  const job = m.jobs.get('a_00041');
  assert.deepStrictEqual([job.maxLevel, job.time, job.exp, job.mito], [61, [80, 85, 95], [900, 600, 400], [1100, 700, 400]]);
  assert.match(eco.claim(s, 'delivery', { job: 'a_00041', seconds: 5 }).error, /too fast/);
  assert.match(eco.claim(s, 'delivery', { job: 'nope', seconds: 80 }).error, /unknown delivery/);
  assert.match(eco.claim(s, 'delivery', { job: 'a_00041', seconds: 200 }).error, /no medal/);
  const gold = eco.claim(s, 'delivery', { job: 'a_00041', seconds: 79.5, medal: 3, exp: 99999 });
  assert.ok(gold.ok, gold.error);
  assert.deepStrictEqual([gold.exp, gold.mito], [900, 1100], 'the medal follows the time, not the client');
  assert.match(eco.claim(s, 'delivery', { job: 'a_00041', seconds: 90 }).error, /too soon/);
  now += 31000;
  const bronze = eco.claim(s, 'delivery', { job: 'a_00041', seconds: 90 });
  assert.deepStrictEqual([bronze.exp, bronze.mito], [400, 400], 'bronze between 85 and 95 s');
  now += 31000;
  p.Level = 62;
  assert.match(eco.claim(s, 'delivery', { job: 'a_00041', seconds: 82 }).error, /level too high/);
  p.Level = 30;

  // instant: Arrest_HUV for levels 21-31 pays column 12 EXP and INT(({Level}^2)/7+10) Mito
  const arrest = m.instant.findIndex(r => r && r.type === 'instant' && r.kind === 'Arrest_HUV' && r.minLevel <= 30 && r.maxLevel >= 30);
  assert.ok(arrest >= 0);
  assert.strictEqual(evalFormula('INT(({Level}^2)/7+10)', 30), 138);
  const inst = eco.claim(s, 'instant', { row: arrest });
  assert.ok(inst.ok, inst.error);
  assert.deepStrictEqual([inst.exp, inst.mito], [Number(m.instant[arrest].expText), 138]);
  now += 61000;
  const patrolRow = m.instant.findIndex(r => r && r.type === 'patrol' && r.kind === 'move' && r.minLevel <= 30 && r.minLevel >= 28);
  assert.match(eco.claim(s, 'instant', { row: patrolRow }).error, /unknown instant/, 'a patrol row is no instant mission');
  p.Level = 50;
  assert.match(eco.claim(s, 'instant', { row: arrest }).error, /not for this level/);
  p.Level = 30;

  // patrol: Officer Heather (Cras, level 28): 6 EXP and 2 Mito per post, 150 EXP for all 160 posts
  const row = m.instant[patrolRow];
  assert.deepStrictEqual([row.postExp, row.postMito, row.completeExp, row.maxPosts], [6, 2, 150, 160]);
  assert.match(eco.claim(s, 'patrol', { row: patrolRow, posts: 10, seconds: 5 }).error, /too fast/);
  const three = eco.claim(s, 'patrol', { row: patrolRow, posts: 3, seconds: 60 });
  assert.deepStrictEqual([three.exp, three.mito], [18, 6]);
  now += 31000;
  const all = eco.claim(s, 'patrol', { row: patrolRow, posts: 999, seconds: 5000 });
  assert.deepStrictEqual([all.exp, all.mito], [6 * 160 + 150, 2 * 160], 'posts capped at the loop, completion bonus');
  assert.match(eco.claim(s, 'mission', {}).error, /unknown reward/);
});

test('battle zone rooms: settings, password, slots, teams, wait-room start and return, ladder', async () => {
  const a = await driver('pva', 'Pva'), b = await driver('pvb', 'Pvb'), c = await driver('pvc', 'Pvc');
  for (const x of [a, b, c]) await x.req('enter', { city: 'cras' });
  const track = { key: 'cras/track1', name: 'cras_track1', laps: 1, length: 1500 };
  // CREATEROOM: title, team mode, 8 players, password, arena (Rookie 3X)
  const h = await a.req('race.host', { track, room: { title: 'Drift King!', team: true, max: 8, password: 'pw', spectate: true, arena: 1, mapFlags: 0x20 } });
  assert.ok(h.ok, h.error);
  let info = (await a.wait(m => m.t === 'race.room' && m.room)).room;
  assert.strictEqual(info.title, 'Drift King!');
  assert.strictEqual(info.max, 8);
  assert.strictEqual(info.locked, true);
  assert.strictEqual(info.spectate, true, 'a password room may allow spectators (ALLOWOBSERVE, 0x57ca12)');
  assert.strictEqual(info.slots.filter(s => s === -1).length, 8, 'slots past the room size are closed');
  assert.strictEqual(info.racers[0].slot, 0);
  assert.strictEqual(info.racers[0].ready, true, 'the host counts as ready');
  const list = await b.req('race.list', { arena: 1 });
  assert.strictEqual(list.rooms.length, 1);
  assert.strictEqual((await b.req('race.list', { arena: 0 })).rooms.length, 0, 'arena filter');
  // password
  assert.strictEqual((await b.req('race.join', { id: h.id, password: 'nope' })).error, 'wrong password');
  assert.ok((await b.req('race.join', { id: h.id, password: 'pw' })).ok);
  info = (await b.wait(m => m.t === 'race.room' && m.room && m.room.racers.length === 2)).room;
  const rb = info.racers.find(r => r.name === 'Pvb');
  assert.strictEqual(rb.slot & 1, 1, 'team mode: the newcomer goes to the smaller team (odd slots = team B)');
  // quick join only finds open rooms without a password
  assert.strictEqual((await c.req('race.quick', { team: true })).ok, false);
  // slots: the host closes an open slot, a guest cannot
  assert.ok((await a.req('race.slot', { slot: 2, op: 'close' })).ok);
  assert.strictEqual((await b.req('race.slot', { slot: 4, op: 'close' })).ok, false);
  assert.ok((await a.req('race.slot', { slot: 2, op: 'open' })).ok);
  // team change: B to team A
  assert.ok((await b.req('race.team', { team: 0 })).ok);
  info = (await a.wait(m => m.t === 'race.room' && m.room && m.room.racers.some(r => r.name === 'Pvb' && (r.slot & 1) === 0))).room;
  assert.ok((await b.req('race.team', { team: 1 })).ok);
  // host settings: track and map word
  assert.strictEqual((await b.req('race.settings', { mapFlags: 1 })).ok, false, 'only the host');
  assert.ok((await a.req('race.settings', { track: { key: 'cras/track2', name: 'cras_track2', laps: 1, length: 1500 }, mapFlags: 0x22 })).ok);
  // the wait room needs everyone ready
  assert.strictEqual((await a.req('race.startRace')).error, 'not everyone is ready');
  assert.ok((await b.req('race.ready', { ready: true })).ok);
  assert.ok((await a.req('race.startRace')).ok);
  const st = await b.wait(m => m.t === 'race.start');
  assert.strictEqual(st.track.key, 'cras/track2');
  assert.strictEqual(st.team, true);
  assert.deepStrictEqual(st.slots.map(s => s.team).sort(), [0, 1]);
  await b.wait(m => m.t === 'race.go');
  offset += 25000;
  b.send('race.progress', { lap: 1, idx: 0, finished: true });
  await a.wait(m => m.t === 'race.finished' && m.name === 'Pvb');
  await a.wait(m => m.t === 'race.endTime');
  await sleep(50);
  a.send('race.progress', { lap: 1, idx: 0, finished: true });
  const res = await a.wait(m => m.t === 'race.results', 5000);
  assert.deepStrictEqual(res.teamPoints, { a: 1, b: 2 }, 'place p of n scores n - p + 1');
  assert.strictEqual(res.winner, 1);
  const rB = res.results.find(r => r.name === 'Pvb'), rA = res.results.find(r => r.name === 'Pva');
  assert.strictEqual(rB.points, 2);
  assert.strictEqual(rB.ladder, 10); assert.strictEqual(rA.ladder, -10);
  assert.strictEqual(rB.ladderPoints, 1010);
  assert.strictEqual(rB.reward.exp, Math.round(rB.reward.exp), 'integer EXP');
  // persistent room: back to the wait room with nobody ready
  info = (await a.wait(m => m.t === 'race.room' && m.room && m.room.state === 'lobby')).room;
  assert.strictEqual(info.racers.find(r => r.name === 'Pvb').ready, false);
  assert.strictEqual(live('Pvb').profile.LadderPoints, 1010);
  // ladder list
  const lad = await c.req('race.ladder', { page: 0 });
  assert.ok(lad.ok);
  assert.strictEqual(lad.rows[0].name, 'Pvb');
  assert.strictEqual(lad.rows[0].points, 1010);
  assert.ok(lad.total >= 2);
  // kick: the host removes a guest
  assert.ok((await a.req('race.kick', { id: rb.id })).ok);
  await b.wait(m => m.t === 'race.kicked');
  // an idle host is removed once everyone is ready (persistent rooms)
  const h2 = await c.req('race.host', { track, room: { title: 'Go Go Go!', team: false, max: 16, arena: 5 } });
  assert.ok(h2.ok);
  assert.ok((await c.req('race.quick', { team: false })).ok === true, 'quick join: already in that room is fine');
  assert.ok((await b.req('race.quick', { team: false, arena: 5 })).ok, 'quick join finds the open single room');
  server.races.hostKickMs = 200;
  await sleep(1050);
  assert.ok((await b.req('race.ready', { ready: true })).ok);
  await c.wait(m => m.t === 'race.kicked' && /host/.test(m.reason), 3000);
  server.races.hostKickMs = 16000;
  const left = server.races.rooms.get(h2.id);
  assert.ok(left && left.host === live('Pvb').id, 'host passed on');
  a.close(); b.close(); c.close();
});

test('apprentice course: one reward car per driver, the grade from the time, the car from the grade', async () => {
  const { apprenticeGrade, apprenticeAllowed } = require('../src/economy');
  assert.deepStrictEqual([129.9, 130, 134.9, 139.9, 140, 1000].map(apprenticeGrade), [0, 1, 1, 2, 3, 3]);
  assert.ok(apprenticeAllowed(0, 10001) && !apprenticeAllowed(1, 10001) && apprenticeAllowed(1, 20001));
  assert.ok(!apprenticeAllowed(2, 20001) && apprenticeAllowed(2, 30001) && !apprenticeAllowed(3, 30001) && apprenticeAllowed(3, 40001));
  if (gd) assert.deepStrictEqual([2, 3, 8, 0].map(i => gd.car(i).sellable), [10001, 20001, 30001, 40001], 'vehiclelist Sellable');
  const claim = (c, data) => c.req('reward', { kind: 'apprentice', data });

  // a fast run: Advance (10001) for grade 0
  const a = await driver('appr1', 'Appr1');
  assert.strictEqual(live('Appr1').profile.Apprentice, true, 'a new driver is an apprentice');
  assert.strictEqual((await claim(a, { seconds: 125, vehicleIndex: 2, color: 1 })).error, 'course not started');
  assert.ok((await claim(a, { start: true })).ok);
  assert.strictEqual((await claim(a, { seconds: 125, vehicleIndex: 2, color: 1 })).error, 'course too fast', 'the server saw no 125 s run');
  offset += 126000;
  assert.strictEqual((await claim(a, { seconds: 40, vehicleIndex: 2 })).error, 'course too fast', 'under a minute');
  assert.strictEqual((await claim(a, { seconds: 125, vehicleIndex: 1 })).error, 'That car is not a tutorial reward.');
  const r = await claim(a, { seconds: 125, vehicleIndex: 2, color: 1 });
  assert.ok(r.ok, r.error);
  assert.strictEqual(r.grade, 0);
  const car = r.driver.Cars[r.driver.CurrentCar];
  assert.strictEqual(car.VehicleIndex, 2, 'the reward car is the current car');
  assert.strictEqual(car.Color, 1);
  assert.strictEqual(r.driver.Apprentice, false);
  assert.strictEqual(r.driver.ApprenticeBest, 125);
  assert.ok((await claim(a, { start: true })).ok);
  offset += 126000;
  assert.strictEqual((await claim(a, { seconds: 125, vehicleIndex: 0 })).error, 'You have already received the tutorial reward.', 'once per driver');
  a.close();

  // grade 2 (137 s): not Advance, not Ironia; Drifter (30001)
  const b = await driver('appr2', 'Appr2');
  assert.ok((await claim(b, { start: true })).ok);
  offset += 140000;
  assert.strictEqual((await claim(b, { seconds: 137, vehicleIndex: 2 })).error, 'Please try again.');
  assert.strictEqual((await claim(b, { seconds: 137, vehicleIndex: 3 })).error, 'Please try again.');
  const rb = await claim(b, { seconds: 137, vehicleIndex: 8, color: 3 });
  assert.ok(rb.ok, rb.error);
  assert.strictEqual(rb.grade, 2);
  b.close();

  // a skipped course (11340 "No"): grade 3, only the Kicker (40001), no run needed
  const c = await driver('appr3', 'Appr3');
  assert.strictEqual((await claim(c, { seconds: 1000, vehicleIndex: 8 })).error, 'Please try again.');
  const rc = await claim(c, { seconds: 1000, vehicleIndex: 0 });
  assert.ok(rc.ok, rc.error);
  assert.strictEqual(rc.driver.ApprenticeBest, 0, 'a skipped course sets no record');
  assert.strictEqual(rc.driver.Cars.length, 2, 'starter car + reward car');
  c.close();

  // drivers saved before the course existed are no apprentices
  const d = await driver('appr4', 'Appr4');
  delete live('Appr4').profile.Apprentice;
  assert.strictEqual((await claim(d, { seconds: 1000, vehicleIndex: 0 })).error, 'You have already received the tutorial reward.');
  d.close();
});
