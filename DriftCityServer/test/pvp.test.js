'use strict';
// Battle Zone extras (src/race.js, src/laptime.js, src/pvptracks.js): checkpoint gates, AI drivers, the reward pick,
// wager rooms, spectators, room / lobby chat, invites, find a friend, Real Match time and the lap-time battle.
const { test, before, after } = require('node:test');
const assert = require('node:assert');
const os = require('node:os');
const path = require('node:path');
const fs = require('node:fs');
const WebSocket = require('ws');
const { createServer, PROTOCOL } = require('../server');
const { loadGameData } = require('../src/gamedata');
const { loadTracks } = require('../src/pvptracks');
const { wagerPayouts, pickCards, Races } = require('../src/race');

const gd = loadGameData(undefined, () => {});
const needsData = gd ? {} : { skip: 'game data not found' };

let server, port, dir;
let offset = 0;
const clock = () => Date.now() + offset;
const sleep = ms => new Promise(r => setTimeout(r, ms));

before(async () => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), 'dcpvp-'));
  server = createServer({ port: 0, db: path.join(dir, 'pvp.db'), quiet: true, clock, raceCountdownMs: 200, maxConnPerIp: 200, raceAiSpeed: 1e6, racePickMs: 400, lapStartMs: 100, tourSignupMs: 300 });
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
const track = key => ({ key, name: key.replace('/', '_'), laps: 1, length: 1500 });

/** Drives every gate of the race, a minute of server clock apart (always plausible). */
async function driveGates(c, gt, laps, from = 1, step = 60000) {
  for (let n = from; n <= laps * gt.per; n++) { offset += step; c.send('race.gate', { n }); await sleep(15); }
}

test('pvptrackinfo: gates per track, lap counts, the lap-time tracks', needsData, () => {
  const t = loadTracks(gd.dir);
  const c1 = t.get('cras/track1');
  assert.ok(c1, 'cras/track1');
  assert.strictEqual(c1.gates.length, 5);
  assert.strictEqual(c1.per, 6, 'five gates and the finish line');
  assert.strictEqual(c1.laps, 2);
  assert.ok(c1.lapLength > 500);
  assert.strictEqual(t.get('koinonia/track3').city, 'koinonia', 'the "kononia" typo');
  const lap = [...t.values()].filter(x => x.cityType === 4).map(x => x.city).sort();
  assert.deepStrictEqual(lap, ['cras', 'koinonia', 'moonpalace', 'oros']);
});

test('rules: wager payouts, reward cards, Real Match timetable', () => {
  // 3 drivers x 1000 = 3000, 10% tax -> 2700: 60/30/10
  let p = wagerPayouts([{ key: 'a', place: 1 }, { key: 'b', place: 2 }, { key: 'c', place: 3 }], 1000, false, -1);
  assert.deepStrictEqual([p.get('a'), p.get('b'), p.get('c')], [1620, 810, 270]);
  // two drivers: the 3rd share goes to the winner; DNF gets nothing
  p = wagerPayouts([{ key: 'a', place: 2 }, { key: 'b', place: 1 }], 500, false, -1);
  assert.deepStrictEqual([p.get('a'), p.get('b')], [270, 630]);
  p = wagerPayouts([{ key: 'a', place: 0 }, { key: 'b', place: 1 }], 500, false, -1);
  assert.deepStrictEqual([p.get('a'), p.get('b')], [0, 900]);
  // team: the winners share; a draw refunds minus the tax
  p = wagerPayouts([{ key: 'a', place: 1, team: 0 }, { key: 'b', place: 2, team: 1 }, { key: 'c', place: 3, team: 0 }], 1000, true, 0);
  assert.deepStrictEqual([p.get('a'), p.get('b'), p.get('c')], [1350, 0, 1350]);
  const cards = pickCards(2, () => 0.5);
  assert.strictEqual(cards.length, 16);
  assert.ok(cards.every(c => (c.type === 3 || c.type === 5) && c.amount > 0));
  const r = new Races({ sessions: new Map() }, null, null, () => {}, {});
  clearInterval(r.rmTimer);
  assert.strictEqual(r.rmState(Date.UTC(2026, 0, 1, 3, 10)).open, true, '03:10 UTC is Real Match time');
  assert.strictEqual(r.rmState(Date.UTC(2026, 0, 1, 3, 10)).endsIn, 40 * 60000);
  assert.strictEqual(r.rmState(Date.UTC(2026, 0, 1, 3, 55)).open, false);
  assert.strictEqual(r.rmState(Date.UTC(2026, 0, 1, 4, 0)).startsIn, 2 * 3600000);
});

test('checkpoint gates: in order, plausible, the finish line closes the race', needsData, async () => {
  const a = await driver('gta', 'Gta'), b = await driver('gtb', 'Gtb');
  const h = await a.req('race.host', { track: track('cras/track1'), room: { title: 'Gates', max: 8, arena: 5 } });
  assert.ok(h.ok, h.error);
  assert.strictEqual(server.races.rooms.get(h.id).track.laps, 2, 'laps from the table');
  assert.ok((await b.req('race.join', { id: h.id })).ok);
  await sleep(1050);
  assert.ok((await b.req('race.ready', { ready: true })).ok);
  assert.ok((await a.req('race.startRace')).ok);
  const st = await b.wait(m => m.t === 'race.start');
  assert.strictEqual(st.gates, true);
  await b.wait(m => m.t === 'race.go');
  const gt = server.races.rooms.get(h.id).gt;
  // A reports its first gate at once: too fast for the distance
  a.send('race.gate', { n: 1 });
  await a.wait(m => m.t === 'race.rejected' && /gate/.test(m.reason));
  // a skipped gate is ignored; the old lap message cannot finish a gated race
  offset += 60000;
  b.send('race.gate', { n: 2 });
  b.send('race.progress', { lap: 2, idx: 0, n: 100, finished: true });
  await sleep(80);
  const rb = server.races.rooms.get(h.id).racers.get(live('Gtb').id);
  assert.strictEqual(rb.gate, 0);
  assert.strictEqual(rb.finished, false);
  await driveGates(b, gt, 2);
  const fin = await a.wait(m => m.t === 'race.finished' && m.name === 'Gtb');
  assert.strictEqual(fin.place, 1);
  const res = await a.wait(m => m.t === 'race.results', 5000);
  assert.strictEqual(res.results.find(r => r.name === 'Gtb').place, 1);
  assert.ok(res.results.find(r => r.name === 'Gta').dnf);
  a.close(); b.close();
});

test('replays: the racers poses are recorded from GO with places and times (crew rounds keep them)', needsData, async () => {
  const a = await driver('rpa', 'Rpa'), b = await driver('rpb', 'Rpb');
  const h = await a.req('race.host', { track: track('cras/track1'), room: { title: 'Replay', max: 8, arena: 5 } });
  assert.ok(h.ok, h.error);
  assert.ok((await b.req('race.join', { id: h.id })).ok);
  await sleep(1050);
  assert.ok((await b.req('race.ready', { ready: true })).ok);
  assert.ok((await a.req('race.startRace')).ok);
  const st = await b.wait(m => m.t === 'race.start');
  assert.strictEqual(st.round, 0, 'not a Channel Battle round');
  await b.wait(m => m.t === 'race.go');
  const room = server.races.rooms.get(h.id);
  assert.ok(room.rec, 'recording from GO');
  // B drives: car states go out as usual, the recorder keeps them
  for (let i = 0; i < 8; i++) { b.send('state', { p: [100 + i * 5, 10, 200], r: 90, v: [20, 0, 0], s: 72 }); await sleep(110); }
  await driveGates(b, room.gt, 2);
  await b.wait(m => m.t === 'race.finished' && m.name === 'Rpb');
  await a.req('race.leave');   // A retires: no 30 s grace
  const res = await b.wait(m => m.t === 'race.results', 5000);
  assert.strictEqual(res.results.find(r => r.name === 'Rpb').place, 1);
  const rec = room.lastRec;
  assert.ok(rec && !room.rec, 'the recording ends with the results');
  assert.strictEqual(rec.city, 'cras');
  assert.strictEqual(rec.track, 'track1');
  const cb = rec.cars.find(c => c.name === 'Rpb'), ca = rec.cars.find(c => c.name === 'Rpa');
  assert.ok(cb.f.length >= 5 * 3 && cb.f.length % 5 === 0, 'frames of t, x, y, z, yaw');
  assert.strictEqual(cb.f[1] >= 100, true);
  assert.strictEqual(cb.f[4], 90);
  assert.strictEqual(cb.place, 1);
  assert.ok(cb.time > 0);
  assert.strictEqual(ca.place, 0, 'DNF');
  assert.ok(rec.duration > 0);
  a.close(); b.close();
});

test('AI drivers fill slots, the server drives them; the reward pick after an EXP arena race', needsData, async () => {
  const a = await driver('aia', 'Aia');
  const h = await a.req('race.host', { track: track('cras/track1'), room: { title: 'AI', max: 8, arena: 2 } });
  assert.ok(h.ok);
  assert.ok((await a.req('race.slot', { slot: 1, op: 'ai' })).ok);
  assert.ok((await a.req('race.slot', { slot: 2, op: 'ai' })).ok);
  assert.ok((await a.req('race.slot', { slot: 2, op: 'noai' })).ok);
  let info = (await a.wait(m => m.t === 'race.room' && m.room && m.room.racers.length === 2)).room;
  const ai = info.racers.find(r => r.ai);
  assert.ok(ai && ai.id >= 0x10000 && ai.ready && info.slots[1] === ai.id, 'AI id in the slot array, always ready');
  assert.ok(Number.isInteger(ai.car) && ai.skill > 0.8);
  assert.strictEqual((await a.req('race.slot', { slot: 3, op: 'ai' })).ok, true);
  assert.ok((await a.req('race.slot', { slot: 3, op: 'noai' })).ok);
  // the free arena takes no AI (AI_BTN only in arena tabs 0..4)
  const free = await connect();
  void free; free.close();
  assert.ok((await a.req('race.startRace')).ok);
  const st = await a.wait(m => m.t === 'race.start');
  assert.ok(st.slots.some(s => s.ai && s.car !== undefined));
  await a.wait(m => m.t === 'race.go');
  // the AI finishes on its own (test speed), then the player
  await a.wait(m => m.t === 'race.finished' && m.name === ai.name, 3000);
  await a.wait(m => m.t === 'race.endTime');
  await driveGates(a, server.races.rooms.get(h.id).gt, 2);
  const res = await a.wait(m => m.t === 'race.results', 5000);
  assert.strictEqual(res.results[0].name, ai.name);
  assert.strictEqual(res.results[0].ai, true);
  const mine = res.results.find(r => r.name === 'Aia');
  assert.strictEqual(mine.place, 2);
  assert.ok(mine.reward && mine.reward.exp > 0, 'paid for the place');
  assert.strictEqual(mine.ladder, undefined, 'no ladder with one player');
  assert.ok(res.pick && res.pick.cards === 16, 'reward pick offered');
  const before = live('Aia').profile.Mito, beforeExp = live('Aia').profile.Exp, beforeLv = live('Aia').profile.Level;
  const pk = await a.req('race.pick', { slot: 5 });
  assert.ok(pk.ok, pk.error);
  assert.strictEqual((await a.req('race.pick', { slot: 6 })).ok, false, 'one card each');
  const shown = await a.wait(m => m.t === 'race.picked' && m.done);
  assert.strictEqual(shown.picks.length, 16, 'every card turned over');
  assert.strictEqual(shown.picks[5].name, 'Aia');
  const p = live('Aia').profile;
  if (pk.card.type === 3) assert.strictEqual(p.Mito, before + pk.card.amount);
  else if (pk.card.type === 5) assert.ok(p.Exp !== beforeExp || p.Level !== beforeLv);
  else assert.ok(p.Items.some(i => i.Id === pk.card.item), 'item card in the bag');
  // the cards come from rewardgroup.xlt Battle_Result (rw_battle_mid for the 4X arena)
  assert.ok(shown.picks.some(c => c.type === 1 && c.item), 'item cards from the table');
  info = (await a.wait(m => m.t === 'race.room' && m.room && m.room.state === 'lobby' && m.room.racers.some(r => r.ai))).room;
  assert.ok(info.racers.find(r => r.ai).ready, 'the AI driver stays ready');
  a.close();
});

test('wager room: entry rule, fees into the pot, payout by rank (0x1f3)', needsData, async () => {
  const a = await driver('wga', 'Wga'), b = await driver('wgb', 'Wgb');
  live('Wga').profile.Mito = 1000;
  let h = await a.req('race.host', { track: track('cras/track1'), room: { title: 'Bet', max: 8, arena: 5, wager: 4 } });
  assert.strictEqual(h.error, 'not enough mito');
  assert.strictEqual(h.need, 5000);
  live('Wga').profile.Mito = 10000; live('Wgb').profile.Mito = 100;
  h = await a.req('race.host', { track: track('cras/track1'), room: { title: 'Bet', max: 8, arena: 5, wager: 4 } });
  assert.ok(h.ok, h.error);
  assert.strictEqual((await b.req('race.join', { id: h.id })).code, 11067);
  live('Wgb').profile.Mito = 6000;
  assert.ok((await b.req('race.join', { id: h.id })).ok);
  assert.strictEqual((await a.req('race.slot', { slot: 3, op: 'ai' })).ok, false, 'no AI in wager rooms');
  await sleep(1050);
  assert.ok((await b.req('race.ready', { ready: true })).ok);
  assert.ok((await a.req('race.startRace')).ok);
  await a.wait(m => m.t === 'race.go');
  assert.strictEqual(live('Wga').profile.Mito, 9500, 'fee paid at the start');
  const gt = server.races.rooms.get(h.id).gt;
  await driveGates(b, gt, 2);
  await driveGates(a, gt, 2);
  const w = await a.wait(m => m.t === 'race.wager', 5000);
  assert.strictEqual(w.fee, 500);
  const wa = w.entries.find(e => e.name === 'Wga'), wb = w.entries.find(e => e.name === 'Wgb');
  assert.deepStrictEqual([wb.rank, wb.delta], [1, 130], 'winner: 630 of the 900 pot');
  assert.deepStrictEqual([wa.rank, wa.delta], [2, -230]);
  assert.strictEqual(live('Wga').profile.Mito, wa.mito, 'new Mito total (race reward and wager)');
  const res = await a.wait(m => m.t === 'race.results');
  assert.ok(!res.pick, 'no reward pick in wager rooms');
  a.close(); b.close();
});

test('spectators, room and lobby chat, invites and find a friend', async () => {
  const a = await driver('spa', 'Spa', 'oros'), b = await driver('spb', 'Spb', 'oros'), c = await driver('spc', 'Spc', 'oros'), d = await driver('spd', 'Spd', 'oros');
  const h = await a.req('race.host', { track: track('oros/zz1'), room: { title: 'Watch', max: 8, arena: 5 } });
  assert.ok(h.ok);
  // lobby chat: drivers looking at the same arena
  await c.req('race.list', { arena: 5 });
  await d.req('race.list', { arena: 3 });
  assert.ok((await b.req('race.chat', { text: 'anyone?', arena: 5 })).ok);
  const lc = await c.wait(m => m.t === 'race.chat' && m.scope === 'lobby');
  assert.strictEqual(lc.from, 'Spb');
  await sleep(50);
  assert.ok(!d.pushed.some(m => m.t === 'race.chat'), 'another arena does not hear it');
  // spectate from the lobby
  const ob = await c.req('race.observe', { id: h.id });
  assert.ok(ob.ok, ob.error);
  let info = (await a.wait(m => m.t === 'race.room' && m.room && m.room.observers === 1)).room;
  assert.deepStrictEqual(info.observerNames, ['Spc']);
  assert.strictEqual((await c.req('race.ready', { ready: true })).ok, false, 'spectators are not racers');
  // room chat reaches the spectator
  assert.ok((await a.req('race.chat', { text: '/Hi' })).ok);
  assert.strictEqual((await c.wait(m => m.t === 'race.chat' && m.scope === 'room')).text, '/Hi');
  // invite: the list, the push, joining without asking the password
  const list = await a.req('race.invitees');
  assert.ok(list.players.some(p => p.name === 'Spb'));
  assert.ok(!list.players.some(p => p.name === 'Spc'), 'the spectator is in a room');
  assert.ok((await a.req('race.invite', { name: 'Spb' })).ok);
  assert.strictEqual((await a.req('race.invite', { name: 'Spd' })).error, 'too fast', '5 s cooldown');
  const inv = await b.wait(m => m.t === 'race.invite');
  assert.strictEqual(inv.from, 'Spa');
  assert.ok((await b.req('race.join', { id: inv.id })).ok);
  // find a friend: where the driver is
  const f = await d.req('race.find', { name: 'Spb' });
  assert.ok(f.ok);
  assert.strictEqual(f.room.id, h.id);
  assert.strictEqual((await d.req('race.find', { name: 'Nobody' })).code, 10219);
  // the spectator watches the race: it gets the start, standings and results
  await sleep(1050);
  assert.ok((await b.req('race.ready', { ready: true })).ok);
  assert.ok((await a.req('race.startRace')).ok);
  const st = await c.wait(m => m.t === 'race.start');
  assert.ok(!st.slots.some(s => s.name === 'Spc'));
  await c.wait(m => m.t === 'race.go');
  offset += 60000;
  b.send('race.progress', { lap: 1, idx: 0, n: 50, finished: true });
  a.send('race.progress', { lap: 1, idx: 0, n: 50, finished: true });
  const res = await c.wait(m => m.t === 'race.results', 5000);
  assert.strictEqual(res.results.length, 2);
  // JOINGAME: the spectator becomes a player in the wait room; OBSERVE: a guest becomes a spectator
  await c.wait(m => m.t === 'race.room' && m.room && m.room.state === 'lobby');
  assert.ok((await c.req('race.join', { id: h.id })).ok);
  assert.ok((await b.req('race.observe', {})).ok);
  info = (await a.wait(m => m.t === 'race.room' && m.room && m.room.observerNames.includes('Spb'))).room;
  assert.ok(info.racers.some(r => r.name === 'Spc'));
  assert.strictEqual((await a.req('race.observe', {})).ok, false, 'the host stays a racer');
  // Real Match rooms only start in Real Match time
  server.races.rmAlways = false;
  const rm = server.races.rmState();
  const e = await d.req('race.host', { track: track('oros/zz2'), room: { title: 'RM', max: 8, arena: 6 } });
  assert.ok(e.ok);
  const mito = await d.req('race.rm');
  assert.strictEqual(mito.open, rm.open);
  a.close(); b.close(); c.close(); d.close();
});

test('lap-time battle: a session per channel, gate checks, lap echo with the reward, round results', needsData, async () => {
  const a = await driver('lpa', 'Lpa', 'koinonia'), b = await driver('lpb', 'Lpb', 'koinonia');
  const e = await a.req('lap.enter');
  assert.ok(e.ok, e.error);
  assert.strictEqual(e.track.laps, 5);
  assert.ok((await b.req('lap.enter')).ok);
  const s = await a.req('lap.start');
  assert.strictEqual(s.startIn, 100);
  await b.wait(m => m.t === 'race.lapStarting' && m.name === 'Lpa');
  await sleep(150);
  const ls = server.laps.sessions.get(live('Lpa').channel.id);
  const per = ls.track.per;
  const before = live('Lpa').profile.Mito;
  for (let n = 1; n <= per; n++) { offset += 30000; a.send('lap.gate', { n }); await sleep(10); }
  const lap = await b.wait(m => m.t === 'race.lapLap' && m.name === 'Lpa');
  assert.strictEqual(lap.lap, 1);
  assert.strictEqual(lap.record, true);
  assert.strictEqual(lap.rank, 1);
  assert.ok(live('Lpa').profile.Mito >= before + 50, 'lap reward');
  // B's run: the first gate at once is too fast
  await b.req('lap.start');
  await sleep(150);
  b.send('lap.gate', { n: 1 });
  await b.wait(m => m.t === 'race.lapRejected');
  server.laps.endRound(ls);
  const r = await a.wait(m => m.t === 'race.lapResults');
  assert.strictEqual(r.top[0].name, 'Lpa');
  assert.strictEqual(r.me.rank, 1);
  assert.strictEqual(r.top[0].rewards.length, 2);
  const rb = await b.wait(m => m.t === 'race.lapResults');
  assert.strictEqual(rb.me.rank, 0);
  // joining a race room leaves the lap-time battle
  const h = await b.req('race.host', { track: track('koinonia/zz'), room: { title: 'x', max: 8, arena: 5 } });
  assert.ok(h.ok);
  assert.strictEqual(live('Lpb').lap, null);
  a.close(); b.close();
});

test('GT Championship online: sign-up, AI entrants, heats as server races, the bracket and the reward', needsData, async () => {
  const a = await driver('tra', 'Tra', 'moonpalace');
  live('Tra').profile.Level = 50;
  const e = await a.req('tour.enter', { class: 3, tracks: ['moonpalace/track1'] });
  assert.ok(e.ok, e.error);
  assert.strictEqual(e.tour.state, 'signup');
  assert.strictEqual((await a.req('tour.enter', { class: 1 })).ok, false, 'one class at a time');
  const before = live('Tra').profile.Mito;
  let reward = null, races = 0;
  for (let guard = 0; guard < 8 && !reward; guard++) {
    // the next heat of mine
    const st = await a.wait(m => (m.t === 'race.tour' && m.tour.myHeat) || m.t === 'race.tourReward', 5000);
    if (st.t === 'race.tourReward') { reward = st; break; }
    assert.strictEqual(st.tour.entrants.length, 16, 'the field filled with AI entrants');
    const r = await a.req('tour.race');
    assert.ok(r.ok, r.error);
    const start = await a.wait(m => m.t === 'race.start', 3000);
    assert.ok(start.slots.some(x => x.ai), 'AI entrants race as AI drivers');
    await a.wait(m => m.t === 'race.go');
    // 15 s a gate: a lap well under the simulated AI laps (the preliminaries rank by lap)
    await driveGates(a, server.races.rooms.get(start.id).gt, start.track.laps, 1, 15000);
    const res = await a.wait(m => m.t === 'race.results', 5000);
    assert.strictEqual(res.tour, true);
    assert.strictEqual(res.results.find(x => x.name === 'Tra').place, 1, 'I win every heat');
    assert.strictEqual(res.results.find(x => x.name === 'Tra').reward, null, 'no race reward in the championship');
    races++;
  }
  if (!reward) reward = await a.wait(m => m.t === 'race.tourReward', 5000);
  assert.strictEqual(races, 5, 'preliminaries, quarter-final, two semi-final rounds, final');
  assert.strictEqual(reward.reached, 4, 'champion');
  assert.strictEqual(reward.rank, 1);
  assert.ok(reward.items.length >= 1, 'the GT Master rank 1 rewards from rewardgroup.xlt');
  const p = live('Tra').profile;
  assert.ok(p.Mito >= before + 75000, 'the stage reward');
  assert.strictEqual(p.TourBest, 4);
  assert.ok(p.Items.some(i => i.Id === 'i_n_00114'), 'reward table items in the bag');
  a.close();
});
