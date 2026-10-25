'use strict';
// The client's word filter (src/namefilter.js, DriftCity_RE/AUDIO.md 6): driver names, room titles, chat.
const { test, before, after } = require('node:test');
const assert = require('node:assert');
const os = require('node:os');
const path = require('node:path');
const fs = require('node:fs');
const WebSocket = require('ws');
const { createServer, PROTOCOL } = require('../server');
const { loadGameData } = require('../src/gamedata');
const { NameFilter } = require('../src/namefilter');

const gd = loadGameData(undefined, () => {});
const needsData = gd ? {} : { skip: 'game data not found' };

test('name filter: the client tables and rules', needsData, () => {
  const f = new NameFilter(gd.dir);
  assert.strictEqual(f.slang.length, 6, 'slang.xlt: 6 rows');
  assert.strictEqual(f.operatorNames.length, 201, 'operatormisrepresent.xlt: 201 rows');
  assert.ok(f.operatorNames.includes('adm') && f.operatorNames.includes('gm') && f.operatorNames.includes('duffy'));
  // driver names: either list, case-insensitive substrings -> 10606
  assert.strictEqual(f.driverName('GM_Kim'), 10606);
  assert.strictEqual(f.driverName('xXAdminXx'), 10606);
  assert.strictEqual(f.driverName('Erina'), 10606, 'staff names are reserved');
  assert.strictEqual(f.driverName('Smile=)'), 10606, 'slang in a name');
  assert.strictEqual(f.driverName('Caré'), 10606, 'the Latin-1 letters of the table');
  assert.strictEqual(f.driverName('Speedy'), null);
  assert.strictEqual(f.driverName('Wolf99'), null);
  // room titles: slang only -> 10608
  assert.strictEqual(f.roomTitle('Fast race =w='), 10608);
  assert.strictEqual(f.roomTitle('GM room'), null, 'operator names are fine in a title (0x57a3ee checks slang)');
  assert.strictEqual(f.roomTitle('Night run'), null);
  // chat: each slang character replaced with one of !@#$%^&*
  const c = f.censor('hi =) there');
  assert.strictEqual(c.length, 'hi =) there'.length);
  assert.ok(c.startsWith('hi ') && c.endsWith(' there'));
  assert.match(c.slice(3, 5), /^[!@#$%^&*]{2}$/);
  assert.strictEqual(f.censor('plain words'), 'plain words');
});

let server, port, dir;
before(async () => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), 'dcnames-'));
  server = createServer({ port: 0, db: path.join(dir, 'test.db'), quiet: true, maxConnPerIp: 50, raceGates: false, nameFilter: true });
  port = await server.listen();
});
after(async () => { await server.close(); fs.rmSync(dir, { recursive: true, force: true }); });

function connect() {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(`ws://127.0.0.1:${port}`);
    const pending = new Map(); const pushed = []; let rid = 1;
    ws.on('message', raw => {
      const m = JSON.parse(raw);
      if (m.t === 'reply' && pending.has(m.rid)) { pending.get(m.rid)(m); pending.delete(m.rid); return; }
      pushed.push(m);
    });
    ws.on('error', reject);
    const c = {
      pushed,
      req(t, body = {}) { const id = rid++; return new Promise(r => { pending.set(id, r); ws.send(JSON.stringify({ t, rid: id, ...body })); }); },
      send(t, body = {}) { ws.send(JSON.stringify({ t, ...body })); },
      async waitFor(pred, ms = 3000) {
        const end = Date.now() + ms;
        while (Date.now() < end) { const i = pushed.findIndex(pred); if (i >= 0) return pushed.splice(i, 1)[0]; await new Promise(r => setTimeout(r, 20)); }
        throw new Error('timeout');
      },
      close() { ws.close(); },
    };
    ws.on('open', () => resolve(c));
  });
}

test('name filter: the server refuses names and titles and censors chat', needsData, async () => {
  const a = await connect();
  assert.ok((await a.req('register', { protocol: PROTOCOL, user: 'namesa', pass: 'secret123' })).ok);
  const bad = await a.req('driver.create', { name: 'GM_Speed', car: 0, color: 1 });
  assert.strictEqual(bad.ok, false);
  assert.strictEqual(bad.code, 10606);
  assert.match(bad.error, /not allowed/);
  assert.ok((await a.req('driver.create', { name: 'Speedy', car: 0, color: 1 })).ok, 'a clean name');
  const b = await connect();
  assert.ok((await b.req('register', { protocol: PROTOCOL, user: 'namesb', pass: 'secret123' })).ok);
  assert.ok((await b.req('driver.create', { name: 'Wolf99', car: 0, color: 1 })).ok);
  for (const c of [a, b]) assert.ok((await c.req('enter', { city: 'cras' })).ok);
  // room titles (CREATEROOM): slang -> 10608
  const host = await a.req('race.host', { track: { key: 'cras/track1', name: 'Track 1', laps: 2, length: 3000 }, room: { title: 'Come =w=', max: 8 } });
  assert.strictEqual(host.ok, false);
  assert.strictEqual(host.code, 10608);
  // chat: the slang reaches the others censored
  a.send('chat', { scope: 'channel', text: 'hello =) all' });
  const got = await b.waitFor(m => m.t === 'chat' && m.from === 'Speedy');
  assert.ok(!got.text.includes('=)'), 'censored: ' + got.text);
  assert.match(got.text, /^hello [!@#$%^&*]{2} all$/);
  a.close(); b.close();
});
