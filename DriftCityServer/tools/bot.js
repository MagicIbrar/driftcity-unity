// Test bots for the Drift City Offline server: they log in (creating their account and driver the first
// time), enter a city and drive circles around the nearest real player, or around the city start.
//
// More than 7 bots from one machine need DC_MAX_CONN_PER_IP raised on the server (default 8 sockets per IP).
//   node tools/bot.js [count] [--url ws://127.0.0.1:7860] [--city cras] [--at x,y,z] [--name Bot] [--quiet]
// --race N: Battle Zone guest. The bot joins the first waiting race room in its channel, gets ready, and N seconds
// after the start reports every lap and the finish (the server still checks the times; tests shorten its limits).
// --trade [mito]: trade partner. The bot accepts every trade request, puts that much Mito on the table (default 1000),
// locks once the other driver has locked and confirms once both are locked (src/trade.js).
// --host N: Battle Zone host. The bot hosts a wait-room room (arena --arena, default 0, on --track, default cras/track1) with one AI driver;
// once a spectator watches (or another driver is ready) it starts after N seconds and drives every gate N seconds after the
// start. For the spectating tests.
// --social: social partner (src/social.js). The bot accepts friend requests and party join requests, answers a note
// with a note, and takes whispered orders: "crew <name>" asks to join that driver's crew, "leavecrew" leaves it,
// "unfriend" removes the whisperer from its friend list.
// --party [ms]: party member for party play (src/partyplay.js). The bot accepts party invites and Undercity invitations
// (REQUESTINVITE), then plays a scripted member: a HUB of the phase's slot list every [ms] (default 1500), barrier HUBs
// after the Main HUB briefing, reports each HUV wave (two HUVs of its level, the boss alone) and hits the wave's HUVs;
// in a party patrol it reports an open post of the loop every [ms] x 2.5 (at least 3.2 s). In a party chase it shows
// that the shared HUV's states reach it: on the first chase.party.huv from the owner it hits the HUV once (damage 1).
// --partylead NAME: party leader for the shared party HUV (src/partyplay.js chase.party.*). The bot invites NAME to a
// party, starts a party chase once NAME is in it and, as the HUV's owner, sends the HUV's state five times a second: a
// car circling NAME's position (radius 30 m, 15 m/s).
// --auction [max]: Auction House partner (src/auction.js). The bot buys three Mittron Enhancers at the parts counter
// and lists them for 9,000 Mito, buys every listing of a real driver priced up to [max] Mito (default 100000), and
// takes the Mito notes of its sales.
'use strict';
const WebSocket = require('ws');

const args = process.argv.slice(2);
const opt = (name, def) => { const i = args.indexOf('--' + name); return i >= 0 ? args[i + 1] : def; };
const count = Math.max(1, Math.min(20, parseInt(args.find(a => /^\d+$/.test(a)) || '1', 10)));
const url = opt('url', 'ws://127.0.0.1:7860');
const city = opt('city', 'cras');
const baseName = opt('name', 'Bot');
const quiet = args.includes('--quiet');
const raceAfter = opt('race', null) !== null ? Number(opt('race', '0')) : null;
const social = args.includes('--social');
const hostAfter = opt('host', null) !== null ? Number(opt('host', '3')) : null;
const hostTrack = opt('track', 'cras/track1');
const partyPace = args.includes('--party') ? Math.max(200, parseInt(/^\d+$/.test(opt('party', '') || '') ? opt('party') : '1500', 10)) : null;
const auctionMax = args.includes('--auction') ? Math.max(0, parseInt(/^\d+$/.test(opt('auction', '') || '') ? opt('auction') : '100000', 10)) : null;
const tradeMito = args.includes('--trade') ? Math.max(0, parseInt(/^\d+$/.test(opt('trade', '') || '') ? opt('trade') : '1000', 10)) : null;
const partyLead = opt('partylead', null);
// where GameFlow spawns a new driver in Cras; other cities need --at
const at = (opt('at', '6747.67,598.66,4661.38')).split(',').map(Number);
const log = (...a) => { if (!quiet) console.log(new Date().toISOString().slice(11, 19), ...a); };

function startBot(n) {
  const name = `${baseName}${n}`;
  const user = `${baseName.toLowerCase()}${n}`;
  const pass = 'botpass' + n;
  const ws = new WebSocket(url);
  let rid = 1;
  const pending = new Map();
  const players = new Map();       // id -> { name, p }
  const req = (t, body = {}) => new Promise(res => { const id = rid++; pending.set(id, res); ws.send(JSON.stringify({ t, rid: id, ...body })); });
  let angle = n * 1.3, timer = null, last = null;

  ws.on('message', raw => {
    const m = JSON.parse(raw);
    if (m.t === 'reply') { const cb = pending.get(m.rid); pending.delete(m.rid); return cb && cb(m); }
    if (m.t === 'channel.joined') for (const p of m.players) players.set(p.id, { name: p.name, p: p.state ? p.state.p : null });
    if (m.t === 'player.joined') players.set(m.player.id, { name: m.player.name, p: m.player.state ? m.player.state.p : null });
    if (m.t === 'player.left') players.delete(m.id);
    if (m.t === 's') for (const [id, p] of m.u) { const e = players.get(id); if (e) e.p = p; }
    if (m.t === 'chat' && m.scope === 'whisper' && m.from !== name) ws.send(JSON.stringify({ t: 'chat', scope: 'whisper', to: m.from, text: `beep boop, I'm ${name}` }));
    if (m.t === 'party.invite') req('party.accept');
    if (m.t === 'kicked') log(`${name} kicked: ${m.reason}`);
    if (raceAfter !== null) raceGuest(m);
    if (tradeMito !== null) tradePartner(m);
    if (social) socialPartner(m);
    if (hostAfter !== null) raceHost(m);
    if (partyPace !== null) partyMember(m);
    if (partyPace !== null || partyLead) partyHuv(m);
    if (auctionMax !== null && m.t === 'note.new' && m.note && m.note.mito > 0 && m.note.sender === 'Auction House')
      req('note.take', { id: m.note.id }).then(r => log(`${name} takes ${m.note.mito} Mito from a sale: ${r.ok ? 'ok' : r.error}`));
  });

  // ---- Battle Zone host (--host) ----
  let hosting = false, starting = false, hostGates = 0, hostLaps = 1, restAt = 0;
  function raceHost(m) {
    if (m.t === 'race.room' && m.room && m.room.state === 'lobby') {
      const others = m.room.racers.filter(r => r.name !== name && !r.ai);
      const go = m.room.observers > 0 || (others.length > 0 && others.every(r => r.ready));
      if (go && !starting && Date.now() > restAt) {
        starting = true;
        setTimeout(() => req('race.startRace').then(r => { starting = false; log(`${name} starts the race: ${r.ok ? 'ok' : r.error}`); }), hostAfter * 1000);
      }
    }
    if (m.t === 'race.start') { hostGates = m.gatesPerLap | 0; hostLaps = (m.track && m.track.laps) || 1; }
    if (m.t === 'race.go') {
      setTimeout(() => {
        if (hostGates > 0) for (let n = 1; n <= hostLaps * hostGates; n++) setTimeout(() => ws.send(JSON.stringify({ t: 'race.gate', n })), 100 * n);
        else for (let l = 1; l <= hostLaps; l++) setTimeout(() => ws.send(JSON.stringify({ t: 'race.progress', lap: l, idx: 0, finished: l >= hostLaps })), 150 * l);
      }, hostAfter * 1000);
    }
    if (m.t === 'race.results') { restAt = Date.now() + 15000; log(`${name} results: ${(m.results || []).map(r => `${r.place || 'DNF'}.${r.name}`).join(' ')}`); }
  }
  async function hostRoom() {
    if (hosting) return;
    hosting = true;
    const key = hostTrack, trackName = key.replace('/', '_');
    const room = { title: `${name}'s room`, team: false, max: 8, arena: Number(opt('arena', '0')), mapFlags: 0x20 };
    const h = await req('race.host', { track: { key, name: trackName, laps: 1, length: 1500 }, room });
    log(`${name} hosts room ${h.id}: ${h.ok ? 'ok' : h.error}`);
    if (h.ok) { const a = await req('race.slot', { slot: 2, op: 'ai' }); log(`${name} adds an AI driver: ${a.ok ? 'ok' : a.error}`); }
  }

  // ---- Auction House partner (--auction) ----
  const bought = new Set();
  async function auctionPartner() {
    const mine = await req('auction.mine');
    if (mine.ok && !mine.rows.some(r => r.status === 'open')) {
      const b = await req('item.buy', { id: 'i_n_00003', count: 3 });
      const r = await req('auction.register', { item: { kind: 'stack', id: 'i_n_00003' }, count: 3, price: 9000 });
      log(`${name} lists 3 Mittron Enhancers: ${b.ok && r.ok ? 'listing ' + r.id : (r.error || b.error)}`);
    }
    setInterval(async () => {
      for (const vehicles of [false, true]) {
        const l = await req('auction.list', { vehicles });
        if (!l.ok) continue;
        for (const row of l.rows) {
          if (row.npc || row.seller === name || row.seller.startsWith(baseName) || bought.has(row.id) || !(row.price > 0) || row.price > auctionMax) continue;
          bought.add(row.id);
          const r = await req('auction.buy', { id: row.id });
          log(`${name} buys ${row.name} from ${row.seller} for ${row.price}: ${r.ok ? 'ok' : r.error}`);
        }
      }
    }, 1500);
  }

  // ---- Battle Zone guest (--race) ----
  let inRoom = false, readySent = false;
  function raceGuest(m) {
    if (m.t === 'race.rooms' && !inRoom) {
      const room = (m.rooms || []).find(r => r.state === 'lobby' && !r.locked && r.racers.length < r.max);
      if (room) { inRoom = true; req('race.join', { id: room.id }).then(r => { if (!r.ok) inRoom = false; log(`${name} join race ${room.id}: ${r.ok ? 'ok' : r.error}`); }); }
    }
    if (m.t === 'race.room') {
      if (!m.room) { inRoom = false; readySent = false; return; }
      const me = m.room.racers.find(r => r.name === name);
      if (m.room.state === 'lobby' && me && !me.ready && !readySent) { readySent = true; setTimeout(() => req('race.ready', { ready: true }).then(r => { readySent = r.ok; log(`${name} ready: ${r.ok ? 'ok' : r.error}`); }), 1100); }
    }
    if (m.t === 'race.start') readySent = false;
    if (m.t === 'race.go') {
      const laps = (m.track && m.track.laps) || 0;
      setTimeout(() => {
        const total = lastTrack ? lastTrack.laps : 1;
        if (lastGates > 0) {
          // checkpoint gates (pvptrackinfo): every gate of every lap in order, 100 ms apart (the server checks each one)
          for (let n = 1; n <= total * lastGates; n++) setTimeout(() => ws.send(JSON.stringify({ t: 'race.gate', n })), 100 * n);
        } else {
          // one lap per message, 150 ms apart (the server takes one lap at a time and checks each lap's time)
          for (let l = 1; l <= total; l++) setTimeout(() => ws.send(JSON.stringify({ t: 'race.progress', lap: l, idx: 0, finished: l >= total })), 150 * l);
        }
        log(`${name} finished the race`);
      }, raceAfter * 1000);
      void laps;
    }
    if (m.t === 'race.start') { lastTrack = m.track; lastGates = m.gatesPerLap | 0; }
    if (m.t === 'race.results') log(`${name} results: ${(m.results || []).map(r => `${r.place || 'DNF'}.${r.name}`).join(' ')}`);
  }
  let lastTrack = null, lastGates = 0;

  // ---- social partner (--social) ----
  function socialPartner(m) {
    if (m.t === 'friend.request') req('friend.answer', { name: m.from, accept: true }).then(r => log(`${name} accepts ${m.from} as a friend: ${r.ok ? 'ok' : r.error}`));
    if (m.t === 'party.request') req('party.answer', { name: m.from, accept: true });
    if (m.t === 'note.new' && m.note && m.note.type === 0 && m.note.sender !== name)
      req('note.send', { to: m.note.sender, content: `Got your note, ${m.note.sender}!` }).then(r => log(`${name} answers a note from ${m.note.sender}: ${r.ok ? 'ok' : r.error}`));
    if (m.t === 'chat' && m.scope === 'whisper' && m.from !== name) {
      const text = String(m.text || '');
      if (text.startsWith('crew ')) req('crew.request', { name: text.slice(5).trim() }).then(r => log(`${name} asks to join a crew: ${r.ok ? r.crew : r.error}`));
      if (text === 'leavecrew') req('crew.leave').then(r => log(`${name} leaves its crew: ${r.ok ? 'ok' : r.error}`));
      if (text === 'unfriend') req('friend.remove', { name: m.from });
    }
  }

  // ---- party member (--party) ----
  const uc = { phase: null, slots: [], down: new Set(), barriers: 0, barrierDown: new Set(), wave: 0, huvs: null, mainAt: 0, timer: null };
  const patrol = { on: false, posts: 0, done: new Set(), timer: null };
  function partyMember(m) {
    if (m.t === 'party.uc.invite') setTimeout(() => req('uc.answer', { accept: true }).then(r => log(`${name} joins ${m.from}'s Undercity run: ${r.ok ? 'ok' : r.error}`)), 400);
    if (m.t === 'party.uc.start') {
      clearInterval(uc.timer);
      Object.assign(uc, { phase: 'ready', slots: [], down: new Set(), barrierDown: new Set(), wave: 0, huvs: null });
      uc.timer = setInterval(ucAct, partyPace);
    }
    if (m.t === 'party.uc.phase') {
      uc.phase = m.phase;
      if (Array.isArray(m.slots) && m.slots.length) uc.slots = m.slots;
      if (m.phase === 'main') { uc.barriers = m.barriers; uc.barrierDown = new Set(); uc.mainAt = Date.now(); }
      if (m.phase === 'wave' || m.phase === 'boss') {
        uc.wave = m.wave; uc.huvs = null;
        const boss = m.phase === 'boss';
        setTimeout(() => req('uc.wave', { wave: m.wave, huvs: boss ? [{ level: 50, boss: true }] : [{ level: 30 }, { level: 30 }] }), 300);
      }
    }
    if (m.t === 'party.uc.huvs') uc.huvs = m.health.slice();
    if (m.t === 'party.uc.event') {
      if (m.kind === 'hub' || m.kind === 'jump') uc.down.add(m.slot);
      if (m.kind === 'barrier') uc.barrierDown.add(m.slot);
      if (m.kind === 'hit' && uc.huvs) uc.huvs[m.index] = m.health;
    }
    if (m.t === 'party.uc.result') { clearInterval(uc.timer); uc.phase = null; log(`${name} Undercity ${m.cleared ? 'cleared' : 'failed'}: ${(m.rows || []).map(r => `${r.name} ${r.exp}exp`).join(', ')}`); }
    if (m.t === 'party.uc.cancel') { clearInterval(uc.timer); uc.phase = null; }
    if (m.t === 'party.patrol.start') {
      clearInterval(patrol.timer);
      Object.assign(patrol, { on: true, posts: m.posts, done: new Set() });
      // the bot takes the posts from the far end of the loop
      patrol.timer = setInterval(() => {
        if (!patrol.on) return;
        for (let i = patrol.posts - 1; i >= 0; i--) if (!patrol.done.has(i)) { req('patrol.party.post', { index: i }); break; }
      }, Math.max(3200, partyPace * 2.5));
    }
    if (m.t === 'party.patrol.post') patrol.done.add(m.index);
    if (m.t === 'party.patrol.end') { patrol.on = false; clearInterval(patrol.timer); log(`${name} party patrol over: ${m.reason}`); }
  }
  function ucAct() {
    if (uc.phase === 'hubs') {
      const open = uc.slots.filter(s => !uc.down.has(s));
      if (open.length) { const s = open[Math.floor(Math.random() * open.length)]; req('uc.event', { kind: s < 10 ? 'jump' : 'hub', slot: s }); }
    } else if (uc.phase === 'main' && Date.now() - uc.mainAt > 1000) {
      for (let b = uc.barriers; b >= 1; b--) if (!uc.barrierDown.has(b)) { req('uc.event', { kind: 'barrier', slot: b }); break; }
    } else if ((uc.phase === 'wave' || uc.phase === 'boss') && uc.huvs) {
      const i = uc.huvs.findIndex(h => h > 0);
      if (i >= 0) req('uc.event', { kind: 'hit', slot: i, damage: 20 });
    }
  }

  // ---- the shared party HUV (--party member, --partylead owner) ----
  const huv = { timer: null, seen: false, angle: 0, started: false };
  function partyHuv(m) {
    if (partyLead && m.t === 'party.update' && m.party && !huv.started && m.party.members.some(x => x.name === partyLead)) {
      huv.started = true;
      setTimeout(() => req('chase.party.start', { boss: false, huv: 1, hp: 100 }).then(r => log(`${name} starts a party chase: ${r.ok ? 'ok' : r.error}`)), 1500);
    }
    if (m.t === 'chase.party.start') {
      huv.seen = false;
      clearInterval(huv.timer); huv.timer = null;
      if (m.owner === name) startHuv();
    }
    if (m.t === 'chase.party.owner') { clearInterval(huv.timer); huv.timer = null; if (m.owner === name) startHuv(); }
    if (m.t === 'chase.party.huv' && !huv.seen) {
      huv.seen = true;
      log(`${name} sees the shared HUV at ${JSON.stringify(m.cars && m.cars[0])}`);
      req('chase.party.hit', { damage: 1 });
    }
    if (m.t === 'chase.party.end') { clearInterval(huv.timer); huv.timer = null; log(`${name} party chase over: ${m.success ? 'caught' : 'failed'}`); }
  }
  function startHuv() {
    huv.timer = setInterval(() => {
      // the HUV circles the named player (or the nearest real player)
      let centre = null;
      for (const p of players.values()) if (p.p && (p.name === partyLead || (!centre && !p.name.startsWith(baseName)))) centre = p.p;
      if (!centre) return;
      const radius = 30, speed = 15;
      huv.angle += (speed / radius) * 0.2;
      const x = centre[0] + Math.cos(huv.angle) * radius, z = centre[2] + Math.sin(huv.angle) * radius;
      const yaw = (((-huv.angle * 180 / Math.PI) % 360) + 360) % 360;
      ws.send(JSON.stringify({ t: 'chase.party.huv', cars: [[x, centre[1], z, yaw, speed]] }));
    }, 200);
  }
  async function inviteLoop() {
    for (let i = 0; i < 40 && !huv.started; i++) {
      if ([...players.values()].some(p => p.name === partyLead)) {
        const r = await req('party.invite', { name: partyLead, party: 'BotChase' });
        log(`${name} invites ${partyLead}: ${r.ok ? 'ok' : r.error}`);
        if (r.ok) return;
      }
      await new Promise(res => setTimeout(res, 1000));
    }
  }

  // ---- trade partner (--trade) ----
  let offered = false, locking = false, confirming = false;
  function tradePartner(m) {
    if (m.t === 'trade.invite') req('trade.accept', { id: m.id }).then(r => log(`${name} accepts a trade from ${m.from}: ${r.ok ? 'ok' : r.error}`));
    if (m.t === 'trade.open') { offered = locking = confirming = false; req('trade.offer', { items: [], mito: tradeMito }).then(r => { offered = r.ok; }); }
    if (m.t === 'trade.state') {
      if (m.them.locked && !m.me.locked && !locking) { locking = true; setTimeout(() => req('trade.lock', { lock: true }).then(() => { locking = false; }), 300); }
      if (m.me.locked && m.them.locked && !m.me.confirmed && !confirming) { confirming = true; setTimeout(() => req('trade.confirm').then(r => { confirming = false; log(`${name} confirms the trade: ${r.ok ? (r.done ? 'done' : 'waiting') : r.error}`); }), 300); }
    }
    if (m.t === 'trade.closed') log(`${name} trade closed: ${m.reason}`);
    void offered;
  }

  ws.on('open', async () => {
    let r = await req('login', { protocol: 1, user, pass });
    if (!r.ok) r = await req('register', { protocol: 1, user, pass });
    if (!r.ok) { log(`${name}: ${r.error}`); return ws.close(); }
    if (!r.driver) {
      const c = await req('driver.create', { name, car: [0, 2, 3][n % 3], color: n % 4 });   // starter cars (vehiclelist Req Level 1)
      if (!c.ok) { log(`${name}: ${c.error}`); return ws.close(); }
    }
    const e = await req('enter', { city });
    if (!e.ok) { log(`${name}: ${e.error}`); return ws.close(); }
    log(`${name} is in ${e.channel}`);
    // --race: rooms made before we arrived are not broadcast again, so look at the list until we are in one
    if (auctionMax !== null) auctionPartner();
    if (hostAfter !== null) hostRoom();
    if (partyLead) inviteLoop();
    if (raceAfter !== null) setInterval(() => { if (!inRoom) req('race.list').then(r => r.ok && raceGuest({ t: 'race.rooms', rooms: r.rooms })); }, 1500);
    ws.send(JSON.stringify({ t: 'chat', scope: 'channel', text: `${name} here, driving circles. Whisper me with /w ${name} hi` }));
    timer = setInterval(() => {
      // orbit the nearest real player we have a position for, else the start point
      let centre = at;
      let best = Infinity;
      for (const p of players.values()) {
        if (!p.p || p.name.startsWith(baseName)) continue;
        const d = Math.hypot(p.p[0] - at[0], p.p[2] - at[2]);
        if (d < best) { best = d; centre = p.p; }
      }
      const radius = 16 + 7 * n, speed = 13;
      angle += (speed / radius) * 0.1;
      const pos = [centre[0] + Math.cos(angle) * radius, centre[1], centre[2] + Math.sin(angle) * radius];
      const heading = (((-angle * 180 / Math.PI) % 360) + 360) % 360;   // Unity yaw of the tangent (-sin, cos)
      const teleport = !last || Math.hypot(pos[0] - last[0], pos[2] - last[2]) > 40;
      last = pos;
      ws.send(JSON.stringify({ t: 'state', p: pos, r: heading, v: [-Math.sin(angle) * speed, 0, Math.cos(angle) * speed], s: Math.round(speed * 3.6), b: false, teleport }));
    }, 100);
  });

  ws.on('close', () => { clearInterval(timer); log(`${name} disconnected`); });
  ws.on('error', err => log(`${name}: ${err.message}`));
  return ws;
}

const bots = [];
for (let i = 1; i <= count; i++) bots.push(startBot(i));
process.on('SIGINT', () => { for (const b of bots) b.close(); setTimeout(() => process.exit(0), 300); });
