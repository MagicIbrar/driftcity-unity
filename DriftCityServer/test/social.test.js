'use strict';
// Social layer: parties (world.js), friends (social.js), notes (mail.js), crews and crew battles (teams.js + race.js).
const { test, before, after } = require('node:test');
const assert = require('node:assert');
const os = require('node:os');
const path = require('node:path');
const fs = require('node:fs');
const WebSocket = require('ws');
const { createServer, PROTOCOL } = require('../server');
const { crewGrade, CREATE_FEE } = require('../src/teams');
const { WELCOME_MITO } = require('../src/mail');

let server, port, dir, dbFile;
let offset = 0;
const clock = () => Date.now() + offset;
const sleep = ms => new Promise(r => setTimeout(r, ms));
const start = async () => {
  server = createServer({ port: 0, db: dbFile, quiet: true, clock, gamedata: null, raceCountdownMs: 100, raceMaxSpeed: 1e9, raceMinTrackLength: 10, maxConnPerIp: 200, cbSchedule: 'always' });
  port = await server.listen();
};

before(async () => { dir = fs.mkdtempSync(path.join(os.tmpdir(), 'dcsocial-')); dbFile = path.join(dir, 'social.db'); await start(); });
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

async function driver(user, name, existing = false) {
  const c = await connect();
  const r = await c.req(existing ? 'login' : 'register', { protocol: PROTOCOL, user: 'soc_' + user, pass: 'secret123' });
  assert.ok(r.ok, r.error);
  if (!existing) { const d = await c.req('driver.create', { name, car: 0, color: 1 }); assert.ok(d.ok, d.error); }
  c.name = name;
  return c;
}
const admin = line => new Promise(r => server.adminCommand(line, r));
const live = name => server.world.findByName(name);

test('party: named invite, decline, join request, kick, leader hand-over, disband', async () => {
  const a = await driver('pa', 'PartyA'), b = await driver('pb', 'PartyB'), c = await driver('pc', 'PartyC'), d = await driver('pd', 'PartyD');
  for (const x of [a, b, c, d]) assert.ok((await x.req('enter', { city: 'cras' })).ok);
  // 410000: the inviter names the party; 420000 shows the leader's message
  assert.ok((await a.req('party.invite', { name: 'PartyB', party: 'Night Riders', message: 'come drift' })).ok);
  const inv = await b.wait(m => m.t === 'party.invite');
  assert.strictEqual(inv.party, 'Night Riders'); assert.strictEqual(inv.message, 'come drift'); assert.strictEqual(inv.from, 'PartyA');
  // decline (0xf1): the inviter sees the refusal text (450000)
  assert.ok((await b.req('party.decline')).ok);
  assert.match((await a.wait(m => m.t === 'party.declined')).message, /refused your Party invitation/);
  await sleep(10);
  // the cooldown is per target: wait it out with a second invite later; accept this time
  server.world.findByName('PartyA').inviteTimes.clear();
  assert.ok((await a.req('party.invite', { name: 'PartyB', party: 'Night Riders' })).ok);
  await b.wait(m => m.t === 'party.invite');
  assert.ok((await b.req('party.accept')).ok);
  const upd = await a.wait(m => m.t === 'party.update' && m.party && m.party.members.length === 2);
  assert.strictEqual(upd.party.name, 'Night Riders');
  assert.strictEqual(upd.party.members[0].name, 'PartyA', 'leader first');
  assert.ok(upd.party.members[0].leader && upd.party.members.every(x => x.grade >= 1 && x.city === 'cras'));
  // C invites B (already in a party, C in none): the server offers a join request (430000 -> 440000)
  const r = await c.req('party.invite', { name: 'PartyB' });
  assert.strictEqual(r.ok, false); assert.strictEqual(r.canRequest, true);
  assert.ok((await c.req('party.request', { name: 'PartyB' })).ok);
  assert.strictEqual((await a.wait(m => m.t === 'party.request')).from, 'PartyC');
  assert.strictEqual((await b.req('party.answer', { name: 'PartyC', accept: true })).ok, false, 'only the leader got the request');
  assert.ok((await a.req('party.answer', { name: 'PartyC', accept: true })).ok);
  await c.wait(m => m.t === 'party.update' && m.party && m.party.members.length === 3);
  assert.strictEqual((await b.wait(m => m.t === 'party.notice' && m.type === 'joined')).name, 'PartyC');
  // kick: leader only
  assert.strictEqual((await b.req('party.kick', { name: 'PartyC' })).error, 'Not a group leader');
  assert.ok((await a.req('party.kick', { name: 'PartyC' })).ok);
  assert.ok((await c.wait(m => m.t === 'party.notice' && m.type === 'kicked')).you);
  await c.wait(m => m.t === 'party.update' && m.party === null);
  // refuse option (10412) and the Battle Zone (10413)
  assert.ok((await d.req('social.options', { refuseParty: true })).refuseParty);
  assert.match((await a.req('party.invite', { name: 'PartyD' })).error, /refuse all requests/);
  // the leader leaves: B leads a party of one -> disbanded (10831)
  await a.req('party.leave');
  await b.wait(m => m.t === 'party.notice' && m.type === 'disbanded');
  await b.wait(m => m.t === 'party.update' && m.party === null);
  for (const x of [a, b, c, d]) x.close();
});

test('friends: request and approval, list with location, whisper block, remove, deny list', async () => {
  const a = await driver('fa', 'FriendA'), b = await driver('fb', 'FriendB'), c = await driver('fc', 'FriendC');
  await a.req('enter', { city: 'oros' });
  assert.strictEqual((await a.req('friend.add', { name: 'Nobody' })).ok, false);
  assert.strictEqual((await a.req('friend.add', { name: 'FriendA' })).ok, false);
  const r = await a.req('friend.add', { name: 'FriendB' });
  assert.ok(r.ok && r.pending);
  assert.strictEqual((await b.wait(m => m.t === 'friend.request')).from, 'FriendA');
  let la = await a.req('friend.list');
  assert.strictEqual(la.friends[0].status, 'C', '<Friend not approved> (10545)');
  assert.strictEqual((await b.req('friend.list')).friends[0].status, 'R', '<Friend approval request> (10546)');
  assert.ok((await b.req('friend.answer', { name: 'FriendA', accept: true })).ok);
  assert.ok((await a.wait(m => m.t === 'friend.answer')).accept, '10810');
  const lb = await b.req('friend.list');
  assert.strictEqual(lb.friends[0].status, 'F');
  assert.ok(lb.friends[0].online);
  assert.strictEqual(lb.friends[0].location.text, 'Channel  1 - Oros');
  // a crossed request is a friendship at once; decline removes the request
  await c.req('friend.add', { name: 'FriendA' });
  assert.ok((await a.req('friend.answer', { name: 'FriendC', accept: false })).ok);
  assert.strictEqual((await c.wait(m => m.t === 'friend.answer')).accept, false, '10811');
  assert.strictEqual((await c.req('friend.list')).friends.length, 0);
  // login notice 11412
  b.close(); await a.wait(m => m.t === 'friend.online' && m.online === false);
  const b2 = await driver('fb', 'FriendB', true);
  assert.strictEqual((await a.wait(m => m.t === 'friend.online' && m.online)).name, 'FriendB');
  // block: whispers dropped, friendship ended, party refused
  assert.ok((await b2.req('friend.block', { name: 'FriendA' })).ok);
  assert.strictEqual((await b2.req('friend.block', { name: 'FriendA' })).ok, false, '11183 already blocked');
  la = await a.req('friend.list');
  assert.strictEqual(la.friends.length, 0);
  a.send('chat', { scope: 'whisper', to: 'FriendB', text: 'hello?' });
  assert.strictEqual((await a.wait(m => m.t === 'chat' && m.to === 'FriendB')).text, 'hello?', 'the sender still sees the echo');
  await sleep(100);
  assert.ok(!b2.pushed.some(m => m.t === 'chat' && m.scope === 'whisper'), 'blocked whisper not delivered');
  assert.strictEqual((await a.req('friend.add', { name: 'FriendB' })).ok, false);
  assert.strictEqual((await b2.req('friend.list')).blocked[0].name, 'FriendA');
  assert.ok((await b2.req('friend.unblock', { name: 'FriendA' })).ok);
  // remove
  await a.req('friend.add', { name: 'FriendB' });
  await b2.req('friend.answer', { name: 'FriendA', accept: true });
  assert.ok((await b2.req('friend.remove', { name: 'FriendA' })).ok);
  assert.strictEqual((await a.wait(m => m.t === 'friend.removed')).from, 'FriendB');
  assert.strictEqual((await a.req('friend.list')).friends.length, 0);
  for (const x of [a, b2, c]) x.close();
});

test('notes: welcome note, send cost and checks, read, save box, delete, take Mito, persistence', async () => {
  const a = await driver('na', 'NoteA'), b = await driver('nb', 'NoteB');
  // the welcome system note with a gift
  const welcome = await a.wait(m => m.t === 'note.new');
  assert.strictEqual(welcome.note.sender, 'Drift City');
  assert.strictEqual(welcome.note.mito, WELCOME_MITO);
  const mito0 = live('NoteA').profile.Mito;
  const take = await a.req('note.take', { id: welcome.note.id });
  assert.ok(take.ok); assert.strictEqual(take.driver.Mito, mito0 + WELCOME_MITO);
  assert.strictEqual((await a.req('note.take', { id: welcome.note.id })).ok, false, 'taken once');
  // send checks (11010, 11009, 11021, 11038, 11037) and the 50 Mito per receiver cost
  assert.strictEqual((await a.req('note.send', { to: 'NoteB', content: '' })).ok, false);
  assert.strictEqual((await a.req('note.send', { to: '', content: 'hi' })).ok, false);
  assert.strictEqual((await a.req('note.send', { to: 'NoteA', content: 'hi' })).ok, false);
  assert.match((await a.req('note.send', { to: 'NoteB,Ghost', content: 'hi' })).error, /Ghost does not exist/);
  assert.strictEqual((await a.req('note.send', { to: 'NoteB, noteb', content: 'hi' })).ok, false);
  assert.strictEqual((await a.req('note.send', { to: 'NoteB', content: 'x'.repeat(81) })).ok, false, '80 characters');
  const before = live('NoteA').profile.Mito;
  const s = await a.req('note.send', { to: 'NoteB', content: 'race me at the harbour' });
  assert.ok(s.ok, s.error); assert.strictEqual(s.cost, 50); assert.strictEqual(s.driver.Mito, before - 50);
  const got = await b.wait(m => m.t === 'note.new' && m.note.sender === 'NoteA');
  assert.strictEqual(got.note.content, 'race me at the harbour'); assert.strictEqual(got.note.status, 'N');
  // read marks it R; save moves it to the saved box (10 at most); delete removes
  assert.strictEqual((await b.req('note.read', { id: got.note.id })).note.status, 'R');
  let list = await b.req('note.list');
  assert.ok(list.inbox.some(n => n.id === got.note.id && n.status === 'R'));
  assert.ok((await b.req('note.save', { ids: [got.note.id] })).ok);
  list = await b.req('note.list');
  assert.ok(list.saved.some(n => n.id === got.note.id));
  // read notes expire 24 h after reading, unread 30 days after sending; saved notes never
  const other = await a.req('note.send', { to: 'NoteB', content: 'second' });
  assert.ok(other.ok);
  const n2 = await b.wait(m => m.t === 'note.new' && m.note.content === 'second');
  await b.req('note.read', { id: n2.note.id });
  offset += 25 * 3600000;
  list = await b.req('note.list');
  assert.ok(!list.inbox.some(n => n.id === n2.note.id), 'read note expired');
  assert.ok(list.saved.some(n => n.id === got.note.id), 'saved note kept');
  assert.ok((await b.req('note.delete', { ids: [got.note.id] })).ok);
  // a note to an offline driver persists across a server restart
  b.close();
  await sleep(50);
  assert.ok((await a.req('note.send', { to: 'NoteB', content: 'while you were away' })).ok);
  a.close();
  await sleep(50);
  await server.close();
  await start();
  const b2 = await driver('nb', 'NoteB', true);
  list = await b2.req('note.list');
  assert.ok(list.inbox.some(n => n.sender === 'NoteA' && n.content === 'while you were away' && n.status === 'N'));
  b2.close();
});

test('crews: create rules, join request, crew chat, ranks, kick, leave, rename once, deletion grace', async () => {
  const m = await driver('ca', 'CrewBoss'), x = await driver('cb', 'CrewMate'), y = await driver('cc', 'CrewNew');
  assert.match((await m.req('crew.create', { name: 'Speedsters', mark: 3 })).error, /level 20/);
  await admin('setlevel CrewBoss 25');
  await admin('setlevel CrewMate 22');
  live('CrewBoss').profile.Mito = 100;
  assert.match((await m.req('crew.create', { name: 'Speedsters', mark: 3 })).error, /fee of 2500 Mito/);
  live('CrewBoss').profile.Mito = 10000;
  assert.strictEqual((await m.req('crew.check', { name: 'ab' })).ok, false, '4-12 characters');
  assert.strictEqual((await m.req('crew.create', { name: 'Speedsters', mark: 0 })).ok, false, '10332 mark');
  const cr = await m.req('crew.create', { name: 'Speedsters', mark: 3 });
  assert.ok(cr.ok, cr.error);
  assert.strictEqual(cr.driver.Mito, 10000 - CREATE_FEE);
  assert.strictEqual(cr.crew.members[0].rank, 1);
  assert.strictEqual(cr.crew.grade, 'C');
  assert.strictEqual((await x.req('crew.check', { name: 'speedsters' })).ok, false, 'names are unique, any case');
  // join request by a member's name; the Master approves (10020 -> 10016 / 10817)
  assert.ok((await x.req('crew.request', { name: 'CrewBoss' })).ok);
  assert.strictEqual((await m.wait(q => q.t === 'crew.request')).from, 'CrewMate');
  assert.ok((await m.req('crew.answer', { name: 'CrewMate', accept: true })).ok);
  assert.strictEqual((await x.wait(q => q.t === 'crew.notice' && q.type === 'joined')).name, 'CrewMate');
  assert.strictEqual(live('CrewMate').crewName, 'Speedsters');
  // crew chat
  x.send('chat', { scope: 'team', text: 'hi crew' });
  assert.strictEqual((await m.wait(q => q.t === 'chat' && q.scope === 'team')).text, 'hi crew');
  y.send('chat', { scope: 'team', text: 'me too' });
  assert.match((await y.wait(q => q.t === 'chat' && q.scope === 'system')).text, /don't have a Crew/);
  // ranks: 1st Class; handing over the Master needs level 20 and online
  assert.ok((await m.req('crew.rank', { name: 'CrewMate', rank: 2 })).ok);
  assert.strictEqual((await x.req('crew.kick', { name: 'CrewBoss' })).error, 'Crew Master only.');
  assert.strictEqual((await m.req('crew.leave')).error, 'The Crew Master cannot leave the Crew.');
  // a 1st Class member can approve requests while the Master is around too (remake rule)
  assert.ok((await y.req('crew.request', { name: 'Speedsters' })).ok);
  await x.wait(q => q.t === 'crew.request');
  assert.ok((await x.req('crew.answer', { name: 'CrewNew', accept: true })).ok);
  await y.wait(q => q.t === 'crew.notice' && q.type === 'joined');
  assert.match((await m.req('crew.rank', { name: 'CrewNew', rank: 1 })).error, /level 20/);
  assert.ok((await m.req('crew.kick', { name: 'CrewNew' })).ok);
  assert.strictEqual((await y.wait(q => q.t === 'crew.notice' && q.type === 'kicked')).name, 'CrewNew');
  assert.match((await y.req('crew.request', { name: 'Speedsters' })).error, /24 hours/);
  // rename once
  assert.ok((await m.req('crew.rename', { name: 'Drifters' })).ok);
  assert.match((await m.req('crew.rename', { name: 'Again' })).error, /more than once/);
  assert.strictEqual(live('CrewMate').crewName, 'Drifters');
  // hand over the Master, then deletion with the 7-day grace and its cancel
  assert.ok((await m.req('crew.rank', { name: 'CrewMate', rank: 1 })).ok);
  const info = await x.req('crew.info');
  assert.strictEqual(info.crew.master, 'CrewMate'); assert.strictEqual(info.crew.myRank, 1);
  assert.ok((await m.req('crew.leave')).ok, 'the old Master is 1st Class now and may leave');
  assert.ok((await x.req('crew.disband')).ok);
  assert.strictEqual((await x.req('crew.info')).crew.state, 'C');
  assert.ok((await x.req('crew.cancelDisband')).ok);
  assert.ok((await x.req('crew.disband')).ok);
  offset += 8 * 86400000;
  server.social.sweep();
  assert.strictEqual((await x.req('crew.info')).crew, null, 'deleted after 7 days');
  assert.strictEqual(crewGrade(0, 1), 'S'); assert.strictEqual(crewGrade(20000, 0), 'B');
  for (const c of [m, x, y]) c.close();
});

test('crew battle: crew sides in a team room, rounds, channel capture after 3 straight wins', async () => {
  const a = await driver('ba', 'AttackLead'), d = await driver('bd', 'DefendLead'), o = await driver('bo', 'Outsider');
  await admin('setlevel AttackLead 30'); await admin('setlevel DefendLead 30'); await admin('setlevel Outsider 30');
  for (const [c, n] of [[a, 'Attackers'], [d, 'Defenders'], [o, 'Others']]) {
    live(c.name).profile.Mito = 50000;
    require('../src/items').give(live(c.name).profile, 'i_n_00037', 5);   // Challenge Coupons (20077)
    assert.ok((await c.req('crew.create', { name: n, mark: 1 })).ok);
    assert.ok((await c.req('enter', { city: 'koinonia' })).ok);
  }
  const track = { key: 'koinonia/t1', name: 'K1', laps: 1, length: 10 };
  // the defenders own the channel: let them win it first, then the attackers have to win 3 rounds in a row
  const runRound = async (host, guest, winner) => {
    const h = await host.req('race.host', { track, room: { title: 'CB', team: true, max: 8, crew: true } });
    assert.ok(h.ok, h.error);
    const j = await guest.req('race.join', { id: h.id });
    assert.ok(j.ok, j.error);
    await guest.req('race.ready', { ready: true });
    const st = await host.req('race.startRace');
    assert.ok(st.ok, st.error);
    await host.wait(q => q.t === 'race.go');
    await sleep(30);   // a timer may fire a hair before the server clock's start time
    winner.send('race.progress', { lap: 1, idx: 0, finished: true });
    await sleep(30);
    (winner === host ? guest : host).send('race.progress', { lap: 1, idx: 0, finished: true });
    const res = await host.wait(q => q.t === 'race.results');
    await guest.wait(q => q.t === 'race.results');
    await host.req('race.leave'); await guest.req('race.leave');
    return res;
  };
  // a third crew can't join a battle between two others
  const h0 = await a.req('race.host', { track, room: { team: true, max: 8, crew: true } });
  assert.ok((await d.req('race.join', { id: h0.id })).ok);
  assert.match((await o.req('race.join', { id: h0.id })).error, /Crew only/);
  assert.ok((await d.req('race.slot', { slot: 2, op: 'move' })).error, 'crew members stay on their side');
  await a.req('race.leave'); await d.req('race.leave');
  // vacant channel: side A (the host's crew) defends; 3 straight wins by the attackers (side B) take it
  let r;
  for (let i = 1; i <= 3; i++) {
    r = await runRound(a, d, d);
    assert.strictEqual(r.crew.attacker, 'Defenders'); assert.strictEqual(r.crew.round, i); assert.ok(r.crew.attackerWon);
    assert.ok(r.crew.perfect);
  }
  assert.ok(r.crew.captured);
  assert.strictEqual(r.crew.owner, 'Defenders');
  const rank = await a.req('crew.ranking');
  assert.strictEqual(rank.channels[0].channel, 'koinonia-1'); assert.strictEqual(rank.channels[0].crew, 'Defenders');
  // the capture reward note reaches the Defenders' Master
  assert.ok((await d.wait(q => q.t === 'note.new' && q.note.content.includes('Koinonia Channel 1'))).note.mito > 0);
  // the owners defend on the even side (the hosting attackers move over); an owned major channel needs crew rank B+
  assert.match((await a.req('race.host', { track, room: { team: true, max: 8, crew: true } })).error || '', /Rank B/);
  server.social.teams.q.addScore.run(30000, 0, 0, server.social.teams.q.crewByName.get('Attackers').id);
  // the attackers win twice, lose once -> the attack is over
  r = await runRound(a, d, a); assert.strictEqual(r.crew.attacker, 'Attackers'); assert.strictEqual(r.crew.round, 1); assert.ok(r.crew.attackerWon, JSON.stringify(r));
  r = await runRound(a, d, a); assert.strictEqual(r.crew.round, 2);
  r = await runRound(a, d, d); assert.strictEqual(r.crew.attackerWon, false); assert.strictEqual(r.crew.captured, false);
  // one attack per channel per window (20079); the next window (an "always" schedule opens one a day) allows another
  assert.match((await a.req('race.host', { track, room: { team: true, max: 8, crew: true } })).error || '', /only challenge a Channel once/);
  offset += 86400000;
  r = await runRound(a, d, a); assert.strictEqual(r.crew.round, 1, 'a lost round ends the attack');
  const info = await d.req('crew.info');
  assert.strictEqual(info.crew.grade, 'S', 'any capture makes the crew grade S');
  assert.deepStrictEqual(info.crew.channels, ['koinonia-1']);
  for (const c of [a, d, o]) c.close();
});
