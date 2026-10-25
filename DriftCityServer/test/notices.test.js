'use strict';
// Server notices (src/notices.js): the original client's server-text reactions, DriftCity_RE/HANDLERS.md
const { test, before, after } = require('node:test');
const assert = require('node:assert');
const os = require('node:os');
const path = require('node:path');
const fs = require('node:fs');
const WebSocket = require('ws');
const { createServer, PROTOCOL } = require('../server');
const { Notices } = require('../src/notices');

let server, port, dir;
before(async () => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), 'dcnotices-'));
  server = createServer({ port: 0, db: path.join(dir, 'test.db'), quiet: true, maxConnPerIp: 100, gamedata: null });
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
    const closed = new Promise(r => ws.on('close', code => r(code)));
    const c = {
      ws, pushed, closed,
      req(t, body = {}) { const id = rid++; return new Promise(r => { pending.set(id, r); ws.send(JSON.stringify({ t, rid: id, ...body })); }); },
      wait(pred, ms = 3000) {
        const hit = pushed.find(pred);
        if (hit) { pushed.splice(pushed.indexOf(hit), 1); return Promise.resolve(hit); }
        return new Promise((res, rej) => { const w = { pred, resolve: res }; waiters.push(w); w.timer = setTimeout(() => { waiters.splice(waiters.indexOf(w), 1); rej(new Error('timeout: ' + pred)); }, ms); });
      },
      none(pred) { return !pushed.some(pred); },
      close() { ws.close(); },
    };
    ws.on('open', () => c.wait(m => m.t === 'welcome').then(() => resolve(c)));
  });
}
async function driver(user, name) {
  const c = await connect();
  assert.ok((await c.req('register', { protocol: PROTOCOL, user, pass: 'secret123' })).ok);
  const d = await c.req('driver.create', { name, car: 0, color: 1 });
  assert.ok(d.ok, d.error);
  return c;
}
const lines = () => { const out = []; return { out, fn: s => out.push(s) }; };

test('build: kinds, defaults and clean-up', () => {
  assert.strictEqual(Notices.build('nope', { text: 'x' }), null);
  assert.strictEqual(Notices.build('scroll', {}), null, 'nothing to show');
  const e = Notices.build('emergency', { text: 'hello\nworld' });
  assert.deepStrictEqual(e, { t: 'notice', kind: 'emergency', text: 'hello world', seconds: 12 });
  assert.strictEqual(Notices.build('emergency', { text: 'x', seconds: 999, large: true }).seconds, 120);
  assert.strictEqual(Notices.build('system', { key: '20305' }).key, '20305');
  assert.strictEqual(Notices.build('system', { key: 'bad key!' }), null);
  assert.deepStrictEqual(Notices.build('level', { name: 'Bob', level: 7 }), { t: 'notice', kind: 'level', name: 'Bob', level: 7 });
  assert.strictEqual(Notices.build('obtain', { name: 'Booster', count: 0 }).count, 1);
});

test('admin notices reach every driver; popups to one driver', async () => {
  const a = await driver('na1', 'NoticeA'), b = await driver('na2', 'NoticeB');
  const o = lines();
  server.adminCommand('notice Server restart soon', o.fn);
  assert.match(o.out[0], /notice sent to 2/);
  for (const c of [a, b]) {
    const m = await c.wait(x => x.t === 'notice' && x.kind === 'emergency');
    assert.strictEqual(m.text, 'Server restart soon');
    assert.strictEqual(m.seconds, 12);
  }
  server.adminCommand('scroll Double EXP weekend!', o.fn);
  assert.strictEqual((await a.wait(x => x.kind === 'scroll')).text, 'Double EXP weekend!');
  server.adminCommand('popup @NoticeB Please read your mail', o.fn);
  assert.strictEqual((await b.wait(x => x.kind === 'system')).text, 'Please read your mail');
  assert.ok(a.none(x => x.kind === 'system'));
  server.adminCommand('event Halloween | You got a pumpkin', o.fn);
  const ev = await a.wait(x => x.kind === 'event');
  assert.strictEqual(ev.title, 'Halloween');
  assert.strictEqual(ev.text, 'You got a pumpkin');
  server.adminCommand('mute NoticeA', o.fn);
  assert.strictEqual((await a.wait(x => x.kind === 'system' && x.key === '20305')).text, 'Chat has been blocked.');
  assert.strictEqual(server.world.findByName('NoticeA').muted, true);
  server.adminCommand('mute NoticeA', o.fn);
  await a.wait(x => x.key === '20306');
  // unknown commands still reach server.js (help lists both)
  const h = lines();
  server.adminCommand('help', h.fn);
  assert.ok(h.out.some(s => /notice commands/.test(s)) && h.out.some(s => /commands: players/.test(s)));
  a.close(); b.close();
});

test('clock: each driver gets the server time', async () => {
  const a = await driver('nt1', 'Clocky');
  server.notices.tick();
  const m = await a.wait(x => x.t === 'time');
  assert.ok(Math.abs(m.now - Date.now()) < 5000);
  a.close();
});

test('level-up broadcast goes to the other drivers of the channel (op 0x168)', async () => {
  const a = await driver('nl1', 'Leveller'), b = await driver('nl2', 'Watcher'), c = await driver('nl3', 'Elsewhere');
  assert.ok((await a.req('enter', { city: 'cras' })).ok);
  assert.ok((await b.req('enter', { city: 'cras' })).ok);
  assert.ok((await c.req('enter', { city: 'oros' })).ok);
  server.notices.tick();                       // first look: no broadcast
  server.world.findByName('Leveller').profile.Level += 1;
  server.notices.tick();
  const m = await b.wait(x => x.t === 'notice' && x.kind === 'level');
  assert.strictEqual(m.name, 'Leveller');
  assert.strictEqual(m.level, server.world.findByName('Leveller').profile.Level);
  assert.ok(a.none(x => x.kind === 'level'), 'not to the driver himself');
  assert.ok(c.none(x => x.kind === 'level'), 'not to another city');
  a.close(); b.close(); c.close();
});

test('maintenance: countdown, kick with reason, logins closed until "maint off"', async () => {
  const a = await driver('nm1', 'Maint');
  const o = lines();
  server.adminCommand('maint 5 back at 10:00', o.fn);
  const warn = await a.wait(x => x.kind === 'emergency');
  assert.match(warn.text, /maintenance in 5 minutes\. back at 10:00/);
  server.adminCommand('maint 0', o.fn);
  const k = await a.wait(x => x.t === 'kicked');
  assert.strictEqual(k.reason, 'maintenance');
  await a.closed;
  const c = await connect();
  const r = await c.req('login', { protocol: PROTOCOL, user: 'nm1', pass: 'secret123' });
  assert.strictEqual(r.ok, false);
  assert.strictEqual(r.key, '10983');
  server.adminCommand('maint off', o.fn);
  assert.ok((await c.req('login', { protocol: PROTOCOL, user: 'nm1', pass: 'secret123' })).ok);
  c.close();
});
