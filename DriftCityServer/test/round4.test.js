'use strict';
// Round 4 (DriftCity_RE/PVP.md 15, SCREENS.md 8, SOCIAL.md 9): gate effects, GM controls (mission battle, TOUR_REWARD
// sets), mission-battle rooms, the custom crew mark, Channel Battle ghost tickets, AEROSET and the AUTOFINDER quiz.
const { test, before, after } = require('node:test');
const assert = require('node:assert');
const os = require('node:os');
const path = require('node:path');
const fs = require('node:fs');
const WebSocket = require('ws');
const { createServer, PROTOCOL } = require('../server');
const { loadGameData } = require('../src/gamedata');
const { gateEffectFor } = require('../src/race');
const { AEROSET_BUDGET } = require('../src/smallscreens');

const gd = loadGameData(undefined, () => {});
const needsData = gd ? {} : { skip: 'game data not found' };

let server, port, dir;
let offset = 0;
const clock = () => Date.now() + offset;
const sleep = ms => new Promise(r => setTimeout(r, ms));

before(async () => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), 'dcr4-'));
  server = createServer({ port: 0, db: path.join(dir, 'r4.db'), quiet: true, clock, raceCountdownMs: 200, maxConnPerIp: 200, raceAiSpeed: 1e6, racePickMs: 400,
    autofinder: true, autofinderIntervalMs: 60 * 60000, autofinderAnswerMs: 400, autofinderTickMs: 50 });
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
      send(t, body = {}) { ws.send(JSON.stringify({ t, ...body })); },
      wait(pred, ms = 3000) {
        const hit = pushed.find(pred);
        if (hit) { pushed.splice(pushed.indexOf(hit), 1); return Promise.resolve(hit); }
        return new Promise((res, rej) => { const w = { pred, resolve: res }; waiters.push(w); w.timer = setTimeout(() => { waiters.splice(waiters.indexOf(w), 1); rej(new Error('timeout waiting for ' + pred)); }, ms); });
      },
      close() { ws.close(); },
    };
    ws.on('open', () => c.wait(m => m.t === 'welcome').then(() => resolve(c)));
  });
}

async function driver(user, name, city = 'cras') {
  const c = await connect();
  assert.ok((await c.req('register', { protocol: PROTOCOL, user, pass: 'secret123' })).ok);
  assert.ok((await c.req('driver.create', { name, car: 0, color: 1 })).ok);
  assert.ok((await c.req('enter', { city })).ok);
  c.name = name;
  return c;
}
const live = name => server.world.findByName(name);
const admin = line => new Promise(r => server.adminCommand(line, r));
const track = key => ({ key, name: key.replace('/', '_'), laps: 1, length: 1500 });

test('gate effect rule: leaders mostly nothing, the back strong effects, team half charge', () => {
  const count = (place, n, team) => { const h = {}; for (let i = 0; i < 1000; i++) { const t = gateEffectFor(place, n, team, i / 1000); h[t] = (h[t] || 0) + 1; } return h; };
  const lead = count(1, 8, false), last = count(8, 8, false), team = count(1, 8, true);
  assert.strictEqual(lead[0], 600); assert.strictEqual(lead[1], 250); assert.strictEqual(lead[3], 150);   // 1 = half charge (UI effect numbers)
  assert.ok(!lead[2] && !lead[5] && !lead[6], 'no strong effect for the leader');
  assert.strictEqual(last[0], 100); assert.strictEqual(last[2], 300); assert.strictEqual(last[6], 200); assert.strictEqual(last[5], 200);
  assert.strictEqual(team[7], 250); assert.ok(!team[1], 'team rooms: the half charge goes to the team');
  assert.strictEqual(gateEffectFor(1, 1, false, 0.99), 3, 'alone = leading');
});

test('gate effects online: every gate but the finish line pushes race.gateEffect to the racer', needsData, async () => {
  const a = await driver('gea', 'Gea'), b = await driver('geb', 'Geb');
  const h = await a.req('race.host', { track: track('cras/track1'), room: { title: 'Effects', max: 8, arena: 5 } });
  assert.ok(h.ok, h.error);
  assert.ok((await b.req('race.join', { id: h.id })).ok);
  await sleep(1050);
  assert.ok((await b.req('race.ready', { ready: true })).ok);
  assert.ok((await a.req('race.startRace')).ok);
  await b.wait(m => m.t === 'race.go');
  const gt = server.races.rooms.get(h.id).gt;
  for (let n = 1; n < gt.per; n++) { offset += 60000; b.send('race.gate', { n }); await sleep(15); }
  const effects = [];
  for (let i = 0; i < gt.per - 1; i++) effects.push(await b.wait(m => m.t === 'race.gateEffect' && m.name === 'Geb'));
  assert.ok(effects.every(e => [0, 1, 2, 3, 5, 6].includes(e.type)), 'single-room types');
  offset += 60000; b.send('race.gate', { n: gt.per });   // the lap's finish line: no effect
  await sleep(100);
  assert.ok(!b.pushed.some(m => m.t === 'race.gateEffect'), 'no effect on the finish line');
  assert.strictEqual(server.races.rooms.get(h.id).racers.get(live('Geb').id).gateEffects, gt.per - 1);
  a.close(); b.close();
});

test('GM controls: mission battle switch and mission rooms, TOUR_REWARD sets, admin only', needsData, async () => {
  const g = await driver('gmr', 'GmBoss'), p = await driver('gmp', 'Plain');
  let r = await p.req('race.gm', { op: 'check' });
  assert.ok(r.ok); assert.strictEqual(r.admin, false);
  assert.strictEqual((await p.req('race.gm', { op: 'missionbattle', mode: 2 })).ok, false, 'not for players');
  assert.strictEqual((await p.req('race.gm', { op: 'tourReward', class: 1, set: 2 })).ok, false);
  await admin('admin gmr');
  live('GmBoss').admin = true;   // the flag is read at login
  r = await g.req('race.gm', { op: 'check' });
  assert.strictEqual(r.admin, true); assert.deepStrictEqual(r.tourSets, [0, 0, 0, 0]);
  // a player's mission flag is ignored; the admin's room with the switch on is a mission-battle room
  let h = await p.req('race.host', { track: track('cras/track1'), room: { title: 'NoMission', max: 8, arena: 5, mission: 2 } });
  assert.strictEqual(server.races.rooms.get(h.id).mission, 0);
  await p.req('race.leave');
  assert.ok((await g.req('race.gm', { op: 'missionbattle', mode: 3 })).ok);
  h = await g.req('race.host', { track: track('cras/track1'), room: { title: 'Mission', max: 8, arena: 5, mission: 3 } });
  assert.ok(h.ok, h.error);
  const room = server.races.rooms.get(h.id);
  assert.strictEqual(room.mission, 3);
  assert.strictEqual(room.info().mission, 3);
  // not from the list; an invite lets a driver in
  let j = await p.req('race.join', { id: h.id });
  assert.strictEqual(j.ok, false); assert.strictEqual(j.code, 10030);
  room.invited.add(live('Plain').id);
  j = await p.req('race.join', { id: h.id });
  assert.ok(j.ok, j.error);
  assert.strictEqual(server.races.startMessage(room).mission, 3, 'race.start carries the mission kind');
  // TOUR_REWARD: the class's table set, used by the championship's reward
  r = await g.req('race.gm', { op: 'tourReward', class: 1, set: 2 });
  assert.ok(r.ok); assert.strictEqual(r.set, 2);
  assert.strictEqual(server.races.tourSet(1), 'c');
  assert.strictEqual(server.races.tourSet(0), 'a');
  g.close(); p.close();
});

test('custom crew mark: Master only, a 32x32 PNG of at most 4000 bytes, fetched by name', async () => {
  const m = await driver('cmm', 'MarkBoss'), o = await driver('cmo', 'MarkMate');
  await admin('setlevel MarkBoss 30'); live('MarkBoss').profile.Mito = 100000;
  assert.ok((await m.req('crew.create', { name: 'Painters', mark: 2 })).ok);
  // a minimal valid 32x32 PNG header (the server checks the signature and the IHDR size)
  const png = Buffer.alloc(64); png.writeUInt32BE(0x89504e47, 0); png.writeUInt32BE(0x0d0a1a0a, 4); png.writeUInt32BE(13, 8); png.write('IHDR', 12); png.writeUInt32BE(32, 16); png.writeUInt32BE(32, 20);
  let r = await o.req('crew.markImage', { data: png.toString('base64') });
  assert.strictEqual(r.ok, false, 'no crew / not the Master');
  const big = Buffer.concat([png, Buffer.alloc(5000)]);
  assert.strictEqual((await m.req('crew.markImage', { data: big.toString('base64') })).ok, false, 'over 4000 bytes');
  const wrong = Buffer.from(png); wrong.writeUInt32BE(64, 16);
  assert.strictEqual((await m.req('crew.markImage', { data: wrong.toString('base64') })).ok, false, 'not 32x32');
  r = await m.req('crew.markImage', { data: png.toString('base64') });
  assert.ok(r.ok, r.error);
  r = await o.req('crew.markImageGet', { name: 'Painters' });
  assert.ok(r.ok); assert.strictEqual(r.data, png.toString('base64'));
  m.close(); o.close();
});

test('Channel Battle replay: the channel ghost ticket comes with it and the first ghost join uses it up', async () => {
  const w = await driver('ght', 'GhostFan');
  live('GhostFan').profile.Mito = 5000;
  const cb = server.social.teams.cb;
  cb.q.addReplay.run(3, Date.now(), 1, 1, 'A', 'B', 1, 2, 'A', JSON.stringify({ results: [] }));
  const row = cb.q.replays.all(1, 0)[0];
  let r = await w.req('crew.replay', { id: row.id });
  assert.ok(r.ok, r.error);
  assert.strictEqual(r.ticket, 'ch_ghost_04', 'channel 3 -> Ch 04. Battle Ghost Ticket');
  const items = () => live('GhostFan').profile.Items;
  assert.strictEqual(items().find(i => i.Id === 'ch_ghost_04').Count, 1);
  assert.ok((await w.req('crew.ghost', { channel: 3 })).ok);
  assert.ok(!items().some(i => i.Id === 'ch_ghost_04'), 'used up');
  assert.strictEqual((await w.req('crew.ghost', { channel: 3 })).code, 11196, 'no ticket left');
  // the Ghost Pass needs no ticket and gets none
  items().push({ Id: 'ch_ghostpass', Count: 1 });
  r = await w.req('crew.replay', { id: row.id });
  assert.strictEqual(r.ticket, null);
  assert.ok((await w.req('crew.ghost', { channel: 3 })).pass);
  w.close();
});

test('AEROSET: the budget on an AeroSet car with an aero part fitted', needsData, async () => {
  const c = await driver('aer', 'AeroFan');
  const ss = server.smallScreens;
  const p = live('AeroFan').profile;
  const aeroIdx = [...ss.aeroCars][0];
  assert.ok(Number.isInteger(aeroIdx), 'vehiclelist has AeroSet cars');
  p.Cars[0].VehicleIndex = aeroIdx;
  p.Cars[0].Aero = -1;
  assert.strictEqual((await c.req('aero.set', { car: 0, points: [2, 2, 1, 1] })).ok, false, 'no aero part fitted');
  p.Cars[0].Aero = 1;
  let r = await c.req('aero.set', { car: 0, points: [2, 2, 1, 0] });
  assert.strictEqual(r.code, 11014, 'points left');
  assert.strictEqual((await c.req('aero.set', { car: 0, points: [4, 2, 0, 0] })).ok, false, 'over the stat cap');
  r = await c.req('aero.set', { car: 0, points: [3, 2, 1, 0] });
  assert.ok(r.ok, r.error); assert.strictEqual(r.code, 11064);
  assert.deepStrictEqual(p.Cars[0].AeroPoints, [3, 2, 1, 0]);
  assert.strictEqual([3, 2, 1, 0].reduce((a, b) => a + b, 0), AEROSET_BUDGET);
  c.close();
});

test('AUTOFINDER: the quiz from autofinder.xlt, right / wrong, three misses disconnect', needsData, async () => {
  const c = await driver('afq', 'QuizFan');
  const ss = server.smallScreens;
  assert.ok(ss.questions.length >= 50, 'init\\autofinder.xlt');
  assert.strictEqual((await c.req('autofinder.test')).ok, false, 'admins only');
  const s = live('QuizFan');
  ss.ask(s);
  let q = await c.wait(m => m.t === 'autofinder.ask');
  assert.strictEqual(q.answers.length, 4); assert.ok(q.question.length > 0); assert.strictEqual(q.seconds, 0);   // 400 ms in this test
  const right = ss.questions[s.af.pending.k].right;
  let r = await c.req('autofinder.answer', { id: q.id, choice: right });
  assert.ok(r.right); assert.strictEqual(r.code, 10556);
  ss.ask(s);
  q = await c.wait(m => m.t === 'autofinder.ask');
  r = await c.req('autofinder.answer', { id: q.id, choice: (ss.questions[s.af.pending.k].right + 1) % 4 });
  assert.strictEqual(r.right, false); assert.strictEqual(r.code, 10557);
  // two unanswered questions time out (the tick counts the miss): the third miss in a row kicks
  ss.ask(s); await c.wait(m => m.t === 'autofinder.result', 2000);
  ss.ask(s);
  await c.wait(m => m.t === 'kicked' && m.reason === 'macro check', 3000);
  c.close();
});
