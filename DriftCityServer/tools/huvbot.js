// Test bot for the server-run HUV events (src/huvevents.js): Rush Time and the Virtual HUV with real players.
//   node tools/huvbot.js [--url ws://127.0.0.1:7860] [--city cras] [--name Hv] [--rush] [--rushgm SECONDS] [--vhuv 4|8]
// --rush: when a Rush Time is announced the bot reports itself in the zone (2 s after the notice, so a real driver who is
// already there runs the boss), then rams the boss: rush.hit 20 damage every 1.5 s while it runs.
// --rushgm SECONDS: once in the city it asks for a Rush Time now with that much waiting (rush.gm; the server needs
// DC_TEST_EVENTS=1) and two AI stand-ins in the zone.
// --vhuv N: registers for an N:N Virtual HUV match and, once it runs, crashes the other team's HUV every 2 s (vhuv.crash;
// the server checks the target, the 1.5 s gap and - when it knows both cars - the distance).
'use strict';
const WebSocket = require('ws');

const args = process.argv.slice(2);
const opt = (name, def) => { const i = args.indexOf('--' + name); return i >= 0 ? args[i + 1] : def; };
const url = opt('url', 'ws://127.0.0.1:7860');
const city = opt('city', 'cras');
const name = opt('name', 'Hv');
const rush = args.includes('--rush') || args.includes('--rushgm');
const rushGm = opt('rushgm', null);
const vhuv = opt('vhuv', null);
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), name, ...a);

const ws = new WebSocket(url);
let rid = 1;
const pending = new Map();
const req = (t, body = {}) => new Promise(res => { const id = rid++; pending.set(id, res); ws.send(JSON.stringify({ t, rid: id, ...body })); });
let rushId = 0, hitTimer = null, crashTimer = null, team = null, huv = null, gotHuv = false;

ws.on('message', raw => {
  const m = JSON.parse(raw);
  if (m.t === 'reply') { const cb = pending.get(m.rid); pending.delete(m.rid); return cb && cb(m); }
  if (m.t === 'party.invite') req('party.accept');
  if (rush) onRush(m);
  if (vhuv) onVhuv(m);
});

function onRush(m) {
  if (m.t === 'rush.notice') {
    rushId = m.id;
    log(`rush time ${m.id} announced (spot ${m.spot}, ${m.seconds} s)`);
    setTimeout(() => req('rush.zone', { id: rushId, in: true }).then(r => log(`in the zone: ${r.ok ? r.drivers + ' waiting' : r.error}`)), 2000);
  }
  if (m.t === 'rush.start' && m.id === rushId) {
    log(`rush time started, owner ${m.owner}, ${m.hp} hp`);
    clearInterval(hitTimer);
    hitTimer = setInterval(() => req('rush.hit', { damage: 20 }).then(r => { if (r.ok) log(`hit: ${r.hp} hp left`); }), 1500);
  }
  if (m.t === 'rush.huv' && !gotHuv) { gotHuv = true; log('got the owner\'s boss states'); }
  if (m.t === 'rush.end' && m.id === rushId) { clearInterval(hitTimer); log(`rush time over: ${m.cancelled ? 'not held' : m.success ? 'lucky ' + m.lucky : 'failed'}`); }
}

function onVhuv(m) {
  if (m.t === 'vhuv.start') {
    team = m.team; huv = m.huv;
    log(`vhuv ${m.id} started, team ${team}, HUVs ${huv.white} / ${huv.red}`);
    clearInterval(crashTimer);
    crashTimer = setInterval(() => {
      const target = team === 'white' ? huv.red : huv.white;
      req('vhuv.crash', { target }).then(r => log(`crash on ${target}: ${r.ok ? `white ${r.score.white} red ${r.score.red}` : r.error}`));
    }, 2000);
  }
  if (m.t === 'vhuv.huv.next' || m.t === 'vhuv.huv') huv = m.huv || huv;
  if (m.t === 'vhuv.end') { clearInterval(crashTimer); log(`vhuv over: white ${m.white} red ${m.red}`); }
}

ws.on('open', async () => {
  await new Promise(r => { const f = raw => { if (JSON.parse(raw).t === 'welcome') { ws.off('message', f); r(); } }; ws.on('message', f); });
  const user = ('hv_' + name.toLowerCase()).slice(0, 16), pass = 'botpass1';
  let l = await req('login', { protocol: 1, user, pass });
  if (!l.ok) l = await req('register', { protocol: 1, user, pass });
  if (!l.ok) { log('login failed: ' + l.error); process.exit(1); }
  if (!l.driver) { const d = await req('driver.create', { name, car: 0, color: 1 }); if (!d.ok) { log('driver: ' + d.error); process.exit(1); } }
  const e = await req('enter', { city });
  log(`entered ${e.channel}`);
  if (rushGm !== null) setTimeout(() => req('rush.gm', { seconds: Number(rushGm), ai: 2 }).then(r => log(`rush.gm: ${r.ok ? 'event ' + r.id : r.error}`)), 1500);
  if (vhuv) setTimeout(() => req('vhuv.register', { size: Number(vhuv) }).then(r => log(`vhuv.register: ${r.ok ? r.waiting + ' waiting' : r.error}`)), 1000);
});
ws.on('close', () => process.exit(0));
