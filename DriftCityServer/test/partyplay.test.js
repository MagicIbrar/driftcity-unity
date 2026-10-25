'use strict';
// Party play (src/partyplay.js, src/undercity.js): Undercity party runs, party patrol, party bonus, party combo.
const { test, before, after } = require('node:test');
const assert = require('node:assert');
const os = require('node:os');
const path = require('node:path');
const fs = require('node:fs');
const WebSocket = require('ws');
const { createServer, PROTOCOL } = require('../server');
const { rewards } = require('../src/economy');
const { ucFee } = require('../src/economy');
const { PATROL_START_SECONDS, PATROL_BONUS_SECONDS } = require('../src/partyplay');

let server, port, dir;
let offset = 0;
const clock = () => Date.now() + offset;
const sleep = ms => new Promise(r => setTimeout(r, ms));

before(async () => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), 'dcparty-'));
  // scripted waits 1000x shorter; the AI partners never act (rng 0.999 -> the longest delay, and they are tested apart)
  server = createServer({ port: 0, db: path.join(dir, 'party.db'), quiet: true, clock, gamedata: null, maxConnPerIp: 200, ucTimeScale: 0.001, ucRng: () => 0.999 });
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
      drop(pred) { for (let i = pushed.length - 1; i >= 0; i--) if (pred(pushed[i])) pushed.splice(i, 1); },
      close() { ws.close(); },
    };
    ws.on('open', () => c.wait(m => m.t === 'welcome').then(() => resolve(c)));
  });
}

async function driver(user, city = 'cras') {
  const c = await connect();
  const r = await c.req('register', { protocol: PROTOCOL, user: 'pp_' + user, pass: 'secret123' });
  assert.ok(r.ok, r.error);
  const d = await c.req('driver.create', { name: user, car: 0, color: 1 });
  assert.ok(d.ok, d.error);
  const e = await c.req('enter', { city });
  assert.ok(e.ok, e.error);
  c.name = user;
  c.channel = e.channel;
  return c;
}
const admin = line => new Promise(r => server.adminCommand(line, r));
const pp = () => server.partyplay;
const tick = () => pp().tick();

async function party(leader, ...others) {
  for (const o of others) {
    assert.ok((await leader.req('party.invite', { name: o.name, party: 'Rescue' })).ok);
    await o.wait(m => m.t === 'party.invite');
    assert.ok((await o.req('party.accept')).ok);
    await leader.wait(m => m.t === 'party.update' && m.party && m.party.members.some(x => x.name === o.name));
  }
}

/** Advances the fake clock and runs the rooms' tick until `pred` holds for a push on `c`. */
async function until(c, pred, stepMs = 50, rounds = 80) {
  for (let i = 0; i < rounds; i++) {
    const hit = c.pushed.find(pred);
    if (hit) { c.pushed.splice(c.pushed.indexOf(hit), 1); return hit; }
    offset += stepMs; tick(); await sleep(5);
  }
  return c.wait(pred, 500);
}

test('undercity: party checks, WAITPLAYER answers, the shared run, AI partner slots, per-member rewards', async () => {
  const a = await driver('UcLead'), b = await driver('UcTwo'), c = await driver('UcThree'), far = await driver('UcFar', 'oros');
  // no solo (UC_Err_PlayTogether)
  let r = await a.req('uc.request', { city: 'cras' });
  assert.strictEqual(r.code, 'PlayTogether');
  // a member in another city (UC_Err_SameCity)
  await party(a, far);
  r = await a.req('uc.request', { city: 'cras' });
  assert.strictEqual(r.code, 'SameCity');
  assert.ok((await far.req('party.leave')).ok);
  await party(a, b, c);
  // the request: WAITPLAYER for the requester, REQUESTINVITE (10770 / 10772) for the others; a refusal cancels (10943)
  r = await a.req('uc.request', { city: 'cras' });
  assert.ok(r.ok, r.error);
  assert.strictEqual(r.fee, ucFee('cras'));   // 2100: the third city's portal (10772)
  await a.wait(m => m.t === 'party.uc.wait');
  const inv = await b.wait(m => m.t === 'party.uc.invite');
  assert.strictEqual(inv.from, 'UcLead');
  assert.strictEqual(inv.seconds, 15);
  await c.wait(m => m.t === 'party.uc.invite');
  assert.ok((await b.req('uc.answer', { accept: false })).ok);
  const cancel = await a.wait(m => m.t === 'party.uc.cancel');
  assert.match(cancel.text, /Registration canceled/);
  // nobody answers in 15 s: cancelled too
  assert.ok((await a.req('uc.request', { city: 'cras' })).ok);
  offset += 18000; tick();
  await c.wait(m => m.t === 'party.uc.cancel');
  for (const x of [a, b, c]) x.drop(() => true);

  // everyone accepts: the stage (0xff) in a private channel of the city, an AI partner in slot 4
  assert.ok((await a.req('uc.request', { city: 'cras' })).ok);
  await b.wait(m => m.t === 'party.uc.invite'); await c.wait(m => m.t === 'party.uc.invite');
  assert.ok((await b.req('uc.answer', { accept: true })).ok);
  await a.wait(m => m.t === 'party.uc.answer' && m.name === 'UcTwo' && m.accept);
  assert.ok((await c.req('uc.answer', { accept: true })).ok);
  const startA = await a.wait(m => m.t === 'party.uc.start');
  const startB = await b.wait(m => m.t === 'party.uc.start');
  assert.strictEqual(startA.slot, 0);
  assert.strictEqual(startB.slot, 1);
  assert.deepStrictEqual(startA.members.map(m => m.name), ['UcLead', 'UcTwo', 'UcThree', 'Rio']);
  assert.ok(startA.members[3].ai);
  assert.match(startA.channel, /^cras-1\d\d$/);
  const joined = await b.wait(m => m.t === 'channel.joined' && m.channel === startA.channel);
  assert.strictEqual(joined.players.length >= 1, true, 'the party sees each other in the stage channel');
  await a.wait(m => m.t === 'party.uc.phase' && m.phase === 'ready');

  // state 4: only the run leader accepts; it brings the HUB slots its stage placed
  await until(a, m => m.t === 'party.uc.phase' && m.phase === 'accept');
  const slots = [...Array(10).keys(), 10, 11, 12, 13, 14];
  r = await b.req('uc.phase', { phase: 'accept', slots });
  assert.ok(!r.ok, 'a member cannot accept');
  assert.ok((await a.req('uc.phase', { phase: 'accept', slots })).ok);
  await a.wait(m => m.t === 'party.uc.phase' && m.phase === 'countdown');
  const hubs = await until(b, m => m.t === 'party.uc.phase' && m.phase === 'hubs');
  assert.deepStrictEqual(hubs.slots, slots);
  assert.ok(hubs.clock > 299, 'the HUB phase clock (300 s)');

  // HUBs: one driver takes a HUB, the same slot can't fall twice, red HUBs (slots 0-9) count as jumps
  assert.ok((await a.req('uc.event', { kind: 'hub', slot: 10 })).ok);
  const ev = await c.wait(m => m.t === 'party.uc.event' && m.slot === 10);
  assert.strictEqual(ev.by, 'UcLead'); assert.strictEqual(ev.kind, 'hub'); assert.strictEqual(ev.who, 0);
  assert.deepStrictEqual(ev.counts, [1, 0, 0, 0], 'PARTYUC counters');
  assert.ok(!(await b.req('uc.event', { kind: 'hub', slot: 10 })).ok, 'already down');
  assert.ok(!(await b.req('uc.event', { kind: 'hub', slot: 77 })).ok, 'not a placed slot');
  assert.ok((await b.req('uc.event', { kind: 'jump', slot: 3 })).ok);
  assert.ok(!(await b.req('uc.event', { kind: 'barrier', slot: 1 })).ok, 'no barriers in the HUB phase');
  for (const s of slots) if (s !== 10 && s !== 3) assert.ok((await (s % 2 ? b : c).req('uc.event', { kind: 'hub', slot: s })).ok, 'slot ' + s);
  await until(a, m => m.t === 'party.uc.phase' && m.phase === 'hubsCleared');
  const main = await until(a, m => m.t === 'party.uc.phase' && m.phase === 'main');
  assert.strictEqual(main.attempt, 1);
  assert.strictEqual(main.barriers, 15, 'barriers for a party of four (with the AI partner)');

  // Main HUB: the shield holds while barriers stand; UFOs add 10 s
  offset += 100;
  assert.ok(!(await a.req('uc.event', { kind: 'main', slot: 0 })).ok, 'the shield is still up');
  const before = (await a.req('uc.info')).room.clock;
  assert.ok((await c.req('uc.event', { kind: 'ufo', slot: 201 })).ok);
  const ufo = await a.wait(m => m.t === 'party.uc.event' && m.kind === 'ufo');
  assert.ok(ufo.clock >= before + 9, 'UC_TimeSave adds 10 s');
  for (let n = 1; n <= 15; n++) assert.ok((await (n % 3 === 0 ? a : n % 3 === 1 ? b : c).req('uc.event', { kind: 'barrier', slot: n })).ok, 'barrier ' + n);
  assert.ok((await a.req('uc.event', { kind: 'main', slot: 0 })).ok);
  await until(b, m => m.t === 'party.uc.phase' && m.phase === 'mainCleared');

  // HUV waves: the wave's HUVs as a stage spawned them; shared health, kills count for the driver who finishes them
  for (let wave = 1; wave <= 5; wave++) {
    const ph = await until(a, m => m.t === 'party.uc.phase' && (m.phase === 'wave' || m.phase === 'boss') && m.wave === wave);
    const boss = wave === 5;
    assert.strictEqual(ph.phase, boss ? 'boss' : 'wave');
    assert.ok((await a.req('uc.wave', { wave, huvs: boss ? [{ level: 50, boss: true }] : [{ level: 30 }, { level: 40 }] })).ok);
    assert.ok((await b.req('uc.wave', { wave, huvs: [{ level: 1 }] })).already, 'the first report counts');
    const hv = await c.wait(m => m.t === 'party.uc.huvs' && m.wave === wave);
    // 15 of 15 HUBs down: no extra health; 30 + level / 2, bosses 200 + 2 x level
    assert.deepStrictEqual(hv.health, boss ? [300] : [45, 50]);
    if (wave === 1) {
      // the wave's HUVs run on the run leader's client (party HUV owner, 0x4f0940): its states go to the others
      assert.ok((await a.req('uc.huvpos', { wave, cars: [[10.123, 2, 30, 90, 22.5], null, ['x']] })).ok);
      const hp = await b.wait(m => m.t === 'party.uc.huvpos');
      assert.deepStrictEqual(hp.cars, [[10.12, 2, 30, 90, 22.5], null, null]);
      assert.strictEqual(hp.from, 'UcLead');
      await c.wait(m => m.t === 'party.uc.huvpos');
      assert.ok(!a.pushed.some(m => m.t === 'party.uc.huvpos'), 'not echoed to the owner');
      assert.match((await b.req('uc.huvpos', { wave, cars: [] })).error, /owner/, 'only the run leader sends them');
      assert.match((await a.req('uc.huvpos', { wave: 3, cars: [] })).error, /wave/);
    }
    for (let i = 0; i < hv.health.length; i++) {
      let hp = hv.health[i], hitter = 0;
      while (hp > 0) {
        offset += 250;
        const who = [a, b, c][hitter++ % 3];
        assert.ok((await who.req('uc.event', { kind: 'hit', slot: i, damage: 100 })).ok);
        const e = await a.wait(m => m.t === 'party.uc.event' && m.kind === 'hit' && m.index === i);
        assert.ok(e.health <= hp - 44.9 && e.health >= hp - 45.1 || e.health === 0, 'a hit is capped at 45');
        hp = e.health;
        if (hp === 0) assert.ok(e.killed);
      }
    }
  }
  await until(a, m => m.t === 'party.uc.phase' && m.phase === 'ending');
  const resA = await until(a, m => m.t === 'party.uc.result');
  const resB = await b.wait(m => m.t === 'party.uc.result');
  assert.ok(resA.cleared);
  assert.strictEqual(resA.attempts, 1);
  assert.strictEqual(resA.rows.length, 4);
  assert.strictEqual(resB.slot, 1);
  const rowA = resA.rows[0];
  // the reward is economy.js's undercity rule for the driver's own counts, plus the party bonus (three members at
  // or below PartyBonus_Level: +15 % EXP)
  const counts = { hubs: rowA.score.hubs, jumps: rowA.score.jumps, barriers: rowA.score.barriers, mainHub: rowA.score.mainHub, huvs: rowA.score.huvs, boss: rowA.score.boss };
  const base = rewards.undercity(1, counts, true, ucFee('cras'));
  assert.strictEqual(rowA.exp, base.exp + Math.floor(base.exp * 15 / 100));
  assert.strictEqual(rowA.mito, base.mito);
  assert.ok(rowA.item, 'a cleared run brings an item');
  assert.strictEqual(resA.driver.Mito, 50000 + rowA.mito, 'paid by the server');
  assert.ok(resA.rows[3].ai);
  // back in the city channel after UCRESULT's 7 s
  await until(a, m => m.t === 'channel.joined' && m.channel === a.channel);
  assert.strictEqual((await a.req('uc.info')).room, null);
  for (const x of [a, b, c, far]) x.close();
});

test('undercity: a member quits (paid for their counts), the Main HUB clock runs out three times (failed)', async () => {
  const a = await driver('FailLead'), b = await driver('FailTwo');
  await party(a, b);
  assert.ok((await a.req('uc.request', { city: 'cras' })).ok);
  await b.wait(m => m.t === 'party.uc.invite');
  assert.ok((await b.req('uc.answer', { accept: true })).ok);
  const st = await a.wait(m => m.t === 'party.uc.start');
  assert.deepStrictEqual(st.members.map(m => m.ai), [false, false, true, true], 'two AI partners');
  await until(a, m => m.t === 'party.uc.phase' && m.phase === 'accept');
  assert.ok((await a.req('uc.phase', { phase: 'accept', slots: [10, 11] })).ok);
  await until(a, m => m.t === 'party.uc.phase' && m.phase === 'hubs');
  assert.ok((await b.req('uc.event', { kind: 'hub', slot: 11 })).ok);
  // PLAYEXIT: B's run fails alone, paid for the HUB
  assert.ok((await b.req('uc.leave')).ok);
  const quit = await b.wait(m => m.t === 'party.uc.result');
  assert.ok(quit.quit && !quit.cleared);
  assert.strictEqual(quit.rows[0].exp, 15 + 1, '15 EXP per HUB, +10 % party bonus (two members)');
  await a.wait(m => m.t === 'party.uc.left' && m.name === 'FailTwo');
  // the HUB clock (300 s), then three Main HUB attempts of 180 s
  offset += 301000; tick();
  for (let n = 1; n <= 3; n++) {
    const m = await until(a, x => x.t === 'party.uc.phase' && x.phase === 'main' && x.attempt === n);
    assert.strictEqual(m.barriers, 15);
    offset += 191000; tick();
  }
  await until(a, m => m.t === 'party.uc.phase' && m.phase === 'failed');
  const res = await until(a, m => m.t === 'party.uc.result');
  assert.ok(!res.cleared);
  assert.strictEqual(res.attempts, 3);
  a.close(); b.close();
});

test('undercity: AI partners take HUBs and barriers on their own clock', async () => {
  const rooms = pp().undercity;
  const a = await driver('AiLead'), b = await driver('AiTwo');
  await party(a, b);
  assert.ok((await a.req('uc.request', { city: 'cras' })).ok);
  await b.wait(m => m.t === 'party.uc.invite');
  assert.ok((await b.req('uc.answer', { accept: true })).ok);
  await a.wait(m => m.t === 'party.uc.start');
  await until(a, m => m.t === 'party.uc.phase' && m.phase === 'accept');
  assert.ok((await a.req('uc.phase', { phase: 'accept', slots: [10, 11, 12, 13] })).ok);
  await until(a, m => m.t === 'party.uc.phase' && m.phase === 'hubs');
  const saved = rooms.rng;
  rooms.rng = () => 0;   // the shortest delay (7 s) and the first open slot
  offset += 12000; tick();
  const ev = await a.wait(m => m.t === 'party.uc.event' && m.by === 'Rio');
  assert.strictEqual(ev.who, 2);
  rooms.rng = saved;
  assert.ok((await a.req('uc.leave')).ok);
  assert.ok((await b.req('uc.leave')).ok);
  a.close(); b.close();
});

test('party patrol: the leader starts it for all, shared posts with bonus time, 3890000 rows for every member', async () => {
  const a = await driver('PatLead'), b = await driver('PatTwo'), c = await driver('PatThree');
  await party(a, b, c);
  assert.ok(!(await b.req('patrol.party.start', { row: 0, posts: 3 })).ok, 'only the leader');
  const r = await a.req('patrol.party.start', { row: 0, posts: 3 });
  assert.ok(r.ok, r.error);
  const st = await b.wait(m => m.t === 'party.patrol.start');
  assert.deepStrictEqual(st.members, ['PatLead', 'PatTwo', 'PatThree']);
  assert.strictEqual(st.posts, 3);
  assert.strictEqual(st.seconds, PATROL_START_SECONDS);
  assert.ok(!(await a.req('patrol.party.start', { row: 0, posts: 3 })).ok, 'one party patrol at a time');
  offset += 3000;
  assert.ok((await b.req('patrol.party.post', { index: 0 })).ok);
  const post = await c.wait(m => m.t === 'party.patrol.post');
  assert.strictEqual(post.by, 'PatTwo'); assert.strictEqual(post.posts, 1); assert.strictEqual(post.total, 1);
  assert.strictEqual(post.bonus, PATROL_BONUS_SECONDS);
  assert.ok(post.seconds > PATROL_START_SECONDS, 'bonus time');
  assert.ok(!(await c.req('patrol.party.post', { index: 0 })).ok, 'already patrolled');
  assert.ok(!(await b.req('patrol.party.post', { index: 1 })).ok, 'too fast');
  offset += 3000;
  assert.ok((await b.req('patrol.party.post', { index: 1 })).ok);
  assert.ok((await a.req('patrol.party.post', { index: 2 })).ok);
  const end = await c.wait(m => m.t === 'party.patrol.end');
  assert.ok(end.complete);
  assert.deepStrictEqual(end.rows.map(x => [x.name, x.posts]), [['PatLead', 1], ['PatTwo', 2], ['PatThree', 0]]);
  assert.strictEqual(end.rows[1].exp, 10, 'fallback 5 EXP per post without the tables');
  // the leader quits: 11159 for everyone
  assert.ok((await a.req('patrol.party.start', { row: 0, posts: 40 })).ok);
  await b.wait(m => m.t === 'party.patrol.start');
  assert.ok((await a.req('patrol.party.quit')).ok);
  const quit = await b.wait(m => m.t === 'party.patrol.end' && m.reason !== 'complete');
  assert.strictEqual(quit.reason, 'leader');
  // the clock runs out: failed (11158)
  assert.ok((await a.req('patrol.party.start', { row: 0, posts: 40 })).ok);
  offset += (PATROL_START_SECONDS + 1) * 1000; tick();
  const late = await c.wait(m => m.t === 'party.patrol.end' && m.reason === 'time');
  assert.strictEqual(late.reason, 'time');
  assert.ok(!late.complete);
  for (const x of [a, b, c]) x.close();
});

test('party chase: the owner (leader) runs the shared HUV, its states reach the others, the next member takes over', async () => {
  const a = await driver('HuvOwner'), b = await driver('HuvTwo'), c = await driver('HuvThree');
  await party(a, b, c);
  assert.ok((await a.req('chase.party.start', { boss: true, huv: 0 })).ok);
  const st = await b.wait(m => m.t === 'chase.party.start');
  assert.strictEqual(st.owner, 'HuvOwner', 'BS_PktPartyHuv names the owner');
  await a.wait(m => m.t === 'chase.party.start'); await c.wait(m => m.t === 'chase.party.start');
  // the owner's cars (the boss, then its escorts) go to the others, cleaned
  assert.ok((await a.req('chase.party.huv', { cars: [[1, 2, 3, 45, 30.456], [4, 5, 6, 46, 31], 'junk'] })).ok);
  const hb = await b.wait(m => m.t === 'chase.party.huv');
  assert.deepStrictEqual(hb.cars, [[1, 2, 3, 45, 30.46], [4, 5, 6, 46, 31], null]);
  assert.strictEqual(hb.id, st.id);
  await c.wait(m => m.t === 'chase.party.huv');
  assert.ok(!a.pushed.some(m => m.t === 'chase.party.huv'), 'not echoed to the owner');
  assert.match((await b.req('chase.party.huv', { cars: [] })).error, /owner/, 'only the owner sends');
  // a flood is cut at the rate cap
  let refused = 0;
  for (let i = 0; i < 20; i++) if (!(await a.req('chase.party.huv', { cars: [[0, 0, 0, 0, 0]] })).ok) refused++;
  assert.ok(refused >= 20 - 12, 'at most 12 states a second');
  // the owner quits: the next member runs the HUV
  assert.ok((await a.req('chase.party.quit')).ok);
  const ow = await b.wait(m => m.t === 'chase.party.owner');
  assert.strictEqual(ow.owner, 'HuvTwo');
  await c.wait(m => m.t === 'chase.party.owner');
  offset += 1100;
  assert.ok((await b.req('chase.party.huv', { cars: [[7, 8, 9, 10, 11]] })).ok);
  const hc = await c.wait(m => m.t === 'chase.party.huv' && m.cars[0] && m.cars[0][0] === 7);
  assert.deepStrictEqual(hc.cars, [[7, 8, 9, 10, 11]]);
  assert.ok(!(await a.wait(m => m.t === 'chase.party.huv' && m.cars[0][0] === 7, 200).catch(() => null)), 'the member who quit gets nothing');
  assert.ok((await b.req('chase.party.quit')).ok);
  assert.ok((await c.req('chase.party.quit')).ok);
  for (const x of [a, b, c]) x.close();
});

test('party bonus: +5 % EXP per member at or below PartyBonus_Level, +5 % Mito above it, HUV modes only; party combo', async () => {
  const a = await driver('BonLead'), b = await driver('BonTwo'), c = await driver('BonThree');
  await party(a, b, c);
  await admin('setlevel BonLead 90');
  let r = await a.req('party.bonus');
  assert.deepStrictEqual([r.exp, r.mito], [10, 5], 'two low members, one high');
  offset += 60000;
  r = await a.req('reward', { kind: 'chase', data: { seconds: 40, boss: false } });
  assert.ok(r.ok, r.error);
  const base = rewards.chase(90, false);
  assert.strictEqual(r.exp, base.exp + Math.floor(base.exp * 10 / 100));
  assert.strictEqual(r.mito, base.mito + Math.floor(base.mito * 5 / 100));
  r = await a.req('reward', { kind: 'race', data: { place: 1 } });
  assert.deepStrictEqual([r.exp, r.mito], [rewards.race(90, 1, 2).exp, rewards.race(90, 1, 2).mito], 'no party bonus outside the HUV modes');
  // party combo: the member's chain and the party total (0x23c)
  r = await b.req('party.combo', { count: 5 });
  assert.strictEqual(r.total, 5);
  let pc = await a.wait(m => m.t === 'party.combo' && m.combo === 5);
  assert.strictEqual(pc.name, 'BonTwo');
  offset += 200;
  r = await b.req('party.combo', { count: 8 });
  assert.strictEqual(r.total, 8);
  offset += 200;
  r = await b.req('party.combo', { count: 3 });
  assert.strictEqual(r.total, 11, 'a new chain adds from the start');
  // solo: nothing
  assert.ok((await c.req('party.leave')).ok);
  assert.ok((await b.req('party.leave')).ok);
  r = await a.req('party.bonus');
  assert.deepStrictEqual([r.exp, r.mito], [0, 0]);
  for (const x of [a, b, c]) x.close();
});
