'use strict';
// Channel Battles (src/channelbattle.js + teams.js + the crew rooms of race.js): schedule, table, coupons, crew rank,
// the challenger queue and turns, part stakes, one attack per window, the owner's forfeit (10327), the vacant-channel
// holder, map setup, spectating / cheering, replays with the owner's fee, the crew ladder.
const { test, before, after } = require('node:test');
const assert = require('node:assert');
const os = require('node:os');
const path = require('node:path');
const fs = require('node:fs');
const WebSocket = require('ws');
const { createServer, PROTOCOL } = require('../server');
const { windowAt, parseSchedule, ladderClass, TABLE, channelIndex } = require('../src/channelbattle');
const items = require('../src/items');

let server, port, dir;
let offset = 0;
const clock = () => Date.now() + offset;
const sleep = ms => new Promise(r => setTimeout(r, ms));

before(async () => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), 'dccb-'));
  server = createServer({ port: 0, db: path.join(dir, 'cb.db'), quiet: true, clock, gamedata: null, raceCountdownMs: 100, raceMaxSpeed: 1e9, raceMinTrackLength: 10, maxConnPerIp: 200, cbSchedule: 'always' });
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
        return new Promise((res, rej) => { const w = { pred, resolve: res }; waiters.push(w); w.timer = setTimeout(() => { waiters.splice(waiters.indexOf(w), 1); rej(new Error('timeout: ' + pred)); }, ms); });
      },
      close() { ws.close(); },
    };
    ws.on('open', () => c.wait(m => m.t === 'welcome').then(() => resolve(c)));
  });
}

async function driver(user, name) {
  const c = await connect();
  assert.ok((await c.req('register', { protocol: PROTOCOL, user: 'cb_' + user, pass: 'secret123' })).ok);
  assert.ok((await c.req('driver.create', { name, car: 0, color: 1 })).ok);
  c.name = name;
  return c;
}
const admin = line => new Promise(r => server.adminCommand(line, r));
const live = name => server.world.findByName(name);
const teams = () => server.social.teams;

test('schedule windows, ladder classes, the 20 battle channels', () => {
  const day = Date.UTC(2026, 9, 24);
  const s = parseSchedule('daily@20');
  let w = windowAt(s, day + 19 * 3600000);
  assert.strictEqual(w.open, false); assert.strictEqual(w.next, day + 20 * 3600000);
  w = windowAt(s, day + 20.5 * 3600000);
  assert.strictEqual(w.open, true); assert.strictEqual(w.end, day + 22 * 3600000);
  w = windowAt(s, day + 22.5 * 3600000);
  assert.strictEqual(w.open, false); assert.strictEqual(w.next, day + 44 * 3600000, 'tomorrow 20:00');
  const wk = windowAt(parseSchedule('weekly@6@18'), day);   // 2026-10-24 is a Saturday
  assert.strictEqual(wk.next, day + 18 * 3600000);
  assert.strictEqual(windowAt(parseSchedule('always'), day + 5).open, true);
  assert.strictEqual(windowAt(parseSchedule('none'), day).open, false);
  assert.deepStrictEqual([1, 3, 4, 10, 11, 25, 35, 45, 51, 0].map(ladderClass), ['S', 'S', 'A', 'A', 'B', 'C', 'D', 'E', 'R', '-']);
  assert.strictEqual(TABLE.length, 20);
  assert.strictEqual(TABLE[0].key, 'cras-1'); assert.strictEqual(TABLE[2].key, 'koinonia-1'); assert.strictEqual(TABLE[10].label, 'Semi 1');
  assert.strictEqual(channelIndex('oros-5'), 19);
});

test('channel battle: coupons, rank, queue and turns, stakes, one attack, forfeit, holder, maps, cheering, replays, ladder', async () => {
  const o = await driver('own', 'OwnBoss'), a = await driver('atk', 'RaidBoss'), b = await driver('bnd', 'BandBoss'), sp = await driver('spc', 'Watcher');
  for (const c of [o, a, b, sp]) { await admin(`setlevel ${c.name} 30`); live(c.name).profile.Mito = 100000; }
  for (const [c, n] of [[o, 'Owners'], [a, 'Raiders'], [b, 'Bandits']]) assert.ok((await c.req('crew.create', { name: n, mark: 2 })).ok);
  const id = n => teams().q.crewByName.get(n).id;
  teams().capture('cras-1', id('Owners'), 'test');   // Channel 1 (a stake channel) belongs to the Owners
  assert.ok((await o.wait(q => q.t === 'note.new' && q.note.content.includes('Cras Channel 1'))).note.mito > 0);
  // the table (0x35d) and the schedule (0x35f)
  const tb = await sp.req('crew.table');
  assert.ok(tb.ok && tb.open);
  assert.strictEqual(tb.channels.length, 20);
  assert.strictEqual(tb.channels[0].owner.name, 'Owners'); assert.strictEqual(tb.channels[0].label, 'Channel 1'); assert.ok(tb.channels[0].stake);
  // map setup (0x38a): the owner's Master, tracks of the channel's city
  const t1 = { key: 'cras/t1', name: 'C1', laps: 1, length: 10 }, t2 = { key: 'cras/t2', name: 'C2', laps: 1, length: 10 };
  assert.match((await o.req('crew.setup', { channel: 0, maps: [{ track: { key: 'koinonia/x', laps: 1, length: 10 }, traffic: 1 }, { track: t2, traffic: 0 }] })).error, /Cras/);
  assert.match((await a.req('crew.setup', { channel: 0, maps: [{ track: t1 }, { track: t2 }] })).error, /do not own/);
  assert.ok((await o.req('crew.setup', { channel: 0, maps: [{ track: t1, traffic: 1 }, { track: t2, traffic: 2 }] })).ok);
  const mi = await sp.req('crew.maps', { channel: 0 });
  assert.strictEqual(mi.owner, 'Owners'); assert.strictEqual(mi.maps[1].track.key, 'cras/t2'); assert.strictEqual(mi.maps[0].traffic, 1);
  for (const c of [o, a, b, sp]) assert.ok((await c.req('enter', { city: 'cras' })).ok);
  // queueing an attack (0x384): a coupon (20077), crew rank B+ on an owned major channel (20076)
  assert.strictEqual((await a.req('crew.ready', { channel: 0 })).code, 20076);
  teams().q.addScore.run(30000, 0, 0, id('Raiders')); teams().q.addScore.run(30000, 0, 0, id('Bandits'));
  assert.strictEqual((await a.req('crew.ready', { channel: 0 })).code, 20077);
  items.give(live('RaidBoss').profile, 'i_n_00037', 1); items.give(live('BandBoss').profile, 'i_n_00037', 1);
  assert.strictEqual((await a.req('crew.ready', { channel: 0 })).position, 1);
  assert.strictEqual((await b.req('crew.ready', { channel: 0 })).position, 2);
  // the stake: a fitted part on the Raiders' Master's car
  const rp = live('RaidBoss').profile;
  rp.PerfParts.push({ Uid: 777, Type: 1, Grade: 2, Enhance: 3 });
  rp.Cars[rp.CurrentCar | 0].PerfSlots = [777, -1, -1, -1];
  // the defenders enter (0x370) and host the channel's room; the head of the queue joins, the next crew waits
  const eo = await o.req('crew.enter', { channel: 0 });
  assert.ok(eo.ok && eo.defending && eo.room, JSON.stringify(eo));
  const ea = await a.req('crew.enter', { channel: 0 });
  assert.strictEqual(ea.room, eo.room, JSON.stringify(ea));
  const eb = await b.req('crew.enter', { channel: 0 });
  assert.ok(eb.ok && !eb.room);
  assert.match((await b.req('race.join', { id: eo.room })).error, /Raiders Crew is attacking first/);
  const lob = await b.req('crew.lobby', { channel: 0 });
  assert.strictEqual(lob.def.name, 'Owners'); assert.strictEqual(lob.atk.name, 'Raiders'); assert.strictEqual(lob.queue.length, 2); assert.ok(lob.prepEnd > 0);
  // round 1: the coupon is spent, the defenders win -> the attack is over
  await a.req('race.ready', { ready: true });
  assert.ok((await o.req('race.startRace')).ok);
  assert.strictEqual(items.count(live('RaidBoss').profile, 'i_n_00037'), 0, 'the coupon was redeemed when the battle started');
  await o.wait(q => q.t === 'race.go'); await sleep(30);
  o.send('race.progress', { lap: 1, idx: 0, finished: true }); await sleep(30);
  a.send('race.progress', { lap: 1, idx: 0, finished: true });
  const res = await o.wait(q => q.t === 'race.results');
  assert.strictEqual(res.track.key, 'cras/t1', 'round 1 runs on the owner\'s map 1');
  assert.strictEqual(res.crew.attackerWon, false); assert.strictEqual(res.crew.round, 1);
  assert.strictEqual((await a.wait(q => q.t === 'race.kicked')).reason, 'Your Crew was defeated.');
  const miss = await a.wait(q => q.t === 'crew.items');
  assert.strictEqual(miss.kind, 'miss'); assert.strictEqual(miss.parts[0].grade, 2);
  assert.ok(!live('RaidBoss').profile.PerfParts.some(x => x.Uid === 777), 'the staked part is gone');
  assert.strictEqual((await o.wait(q => q.t === 'crew.items')).kind, 'get');
  assert.strictEqual((await o.wait(q => q.t === 'note.new' && q.note.item)).note.item.kind, 'part');
  // the Bandits' turn
  assert.strictEqual((await b.wait(q => q.t === 'crew.turn')).crew, 'Bandits');
  const eb2 = await b.req('crew.enter', { channel: 0 });
  assert.strictEqual(eb2.room, eo.room, JSON.stringify(eb2));
  // one attack per channel per window (20079)
  items.give(live('RaidBoss').profile, 'i_n_00037', 1);
  assert.strictEqual((await a.req('crew.ready', { channel: 0 })).code, 20079);
  // spectating (0x387 / 0x388) and cheering with Mittron Fireworks (0x394)
  const spl = await sp.req('crew.spectate');
  assert.strictEqual(spl.rows[0].room, eo.room); assert.strictEqual(spl.rows[0].owner, 'Owners'); assert.strictEqual(spl.rows[0].attacker, 'Bandits');
  assert.ok((await sp.req('race.observe', { id: eo.room })).ok);
  assert.match((await sp.req('crew.cheer', { target: 'BandBoss' })).error, /Fireworks/);
  items.give(live('Watcher').profile, 'i_n_00011', 2);
  assert.ok((await sp.req('crew.cheer', { target: 'BandBoss' })).ok);
  assert.strictEqual((await b.wait(q => q.t === 'crew.cheer')).from, 'Watcher');
  assert.strictEqual((await sp.req('crew.cheer', { target: 'BandBoss' })).error, 'too soon');
  // replays (0x395 / 0x396): channel 1 costs 1000 Mito, paid to the owner's Master (20350)
  const rl = await sp.req('crew.replays');
  assert.ok(rl.rows.length >= 1); assert.strictEqual(rl.rows[0].price, 1000); assert.strictEqual(rl.rows[0].challenger, 'Raiders');
  const mito = live('Watcher').profile.Mito;
  const rv = await sp.req('crew.replay', { id: rl.rows[0].id });
  assert.ok(rv.ok); assert.strictEqual(rv.replay.results.length, 2); assert.strictEqual(live('Watcher').profile.Mito, mito - 1000);
  assert.strictEqual((await o.wait(q => q.t === 'note.new' && q.note.content.includes('Replay Fees'))).note.mito, 1000);
  // 10327: the owner leaves the room while a challenger waits -> the channel is lost; the Bandits hold it
  await o.req('race.leave');
  await sleep(30);
  let t = await sp.req('crew.table');
  assert.strictEqual(t.channels[0].owner, null); assert.strictEqual(t.channels[0].holder.name, 'Bandits');
  // the window closes: the holder of a vacant channel takes it
  teams().cb.closeWindow();
  t = await sp.req('crew.table');
  assert.strictEqual(t.channels[0].owner.name, 'Bandits');
  assert.match((await sp.wait(q => q.t === 'notice')).text, /1 Channel Tournament completed/);
  // the crew ladder (0x608770): the Bandits captured a channel and rank first
  const info = await b.req('crew.info');
  assert.strictEqual(info.crew.ladderRank, 1); assert.strictEqual(info.crew.ladderClass, 'S');
  const rk = await b.req('crew.ranking');
  assert.strictEqual(rk.rows[0].ladderClass, 'S');
  // map setup only within 24 h of the capture (20086)
  offset += 25 * 3600000;
  assert.strictEqual((await b.req('crew.setup', { channel: 0, maps: [{ track: t1 }, { track: t2 }] })).code, 20086);
  for (const c of [o, a, b, sp]) c.close();
});
