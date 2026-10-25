'use strict';
// Mittron fuel (src/fuel.js) and the other driver's info card (src/driverinfo.js): DriftCity_RE/SCREENS.md.
const { test, before, after } = require('node:test');
const assert = require('node:assert');
const os = require('node:os');
const path = require('node:path');
const fs = require('node:fs');
const WebSocket = require('ws');
const { createServer, PROTOCOL } = require('../server');

let server, port, dir;

before(async () => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), 'dcinfo-'));
  server = createServer({ port: 0, db: path.join(dir, 'test.db'), quiet: true, maxConnPerIp: 200, raceGates: false });
  port = await server.listen();
});
after(async () => { await server.close(); fs.rmSync(dir, { recursive: true, force: true }); });

function connect() {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(`ws://127.0.0.1:${port}`);
    const pending = new Map();
    let rid = 1, welcomed = false;
    ws.on('message', raw => {
      const m = JSON.parse(raw);
      if (m.t === 'reply' && pending.has(m.rid)) { pending.get(m.rid)(m); pending.delete(m.rid); return; }
      if (m.t === 'welcome' && !welcomed) { welcomed = true; resolve(c); }
    });
    ws.on('error', reject);
    const c = {
      req(t, body = {}) { const id = rid++; return new Promise(r => { pending.set(id, r); ws.send(JSON.stringify({ t, rid: id, ...body })); }); },
      close() { ws.close(); },
    };
  });
}

async function driver(user, name) {
  const c = await connect();
  const reg = await c.req('register', { protocol: PROTOCOL, user, pass: 'secret123' });
  assert.ok(reg.ok, reg.error);
  const d = await c.req('driver.create', { name, car: 0, color: 1 });
  assert.ok(d.ok, d.error);
  return c;
}
const live = name => server.world.findByName(name);

test('fuel.buy charges 10 Mito a litre and only what the Mito covers', async () => {
  const a = await driver('fuelbuyer', 'FuelBuyer');
  const p = live('FuelBuyer').profile;
  p.Mito = 1000;
  let r = await a.req('fuel.buy', { litres: 30 });
  assert.ok(r.ok, r.error);
  assert.strictEqual(r.litres, 30);
  assert.strictEqual(r.driver.Mito, 700);
  p.Mito = 55;
  r = await a.req('fuel.buy', { litres: 20 });
  assert.ok(r.ok);
  assert.strictEqual(r.litres, 5, 'short of Mito: 10947, only what 55 Mito buys');
  assert.strictEqual(r.driver.Mito, 5);
  r = await a.req('fuel.buy', { litres: 20 });
  assert.ok(!r.ok, 'no Mito left for a litre');
  r = await a.req('fuel.buy', { litres: 0 });
  assert.ok(!r.ok);
  a.close();
});

test('fuel.use spends Mittron Fuel items and returns their litres', async () => {
  const a = await driver('fueluser', 'FuelUser');
  const p = live('FuelUser').profile;
  p.Items = [{ Id: 'i_n_00004', Count: 3 }, { Id: 'i_n_00005', Count: 1 }, { Id: 'i_d_drk_01', Count: 1 }];
  let r = await a.req('fuel.use', { items: { i_n_00004: 2, i_n_00005: 1 } });
  assert.ok(r.ok, r.error);
  assert.strictEqual(r.litres, 14);
  assert.deepStrictEqual(r.driver.Items.find(i => i.Id === 'i_n_00004').Count, 1);
  assert.ok(!r.driver.Items.find(i => i.Id === 'i_n_00005'));
  r = await a.req('fuel.use', { items: { i_n_00004: 5 } });
  assert.ok(!r.ok, 'more than the bag holds');
  r = await a.req('fuel.use', { items: { i_d_drk_01: 1 } });
  assert.ok(!r.ok, 'not a fuel item');
  a.close();
});

test('driver.info returns the USERINFO2ND card for an online or saved driver', async () => {
  const a = await driver('infoa', 'InfoAsker');
  const b = await driver('infob', 'InfoTarget');
  const p = live('InfoTarget').profile;
  p.Level = 12; p.Exp = 3456; p.PvpRaces = 9; p.PvpWins = 4; p.DistanceKm = 12.5; p.Avatar = 5; p.Faction = 1;
  let r = await a.req('driver.info', { name: 'infotarget' });
  assert.ok(r.ok, r.error);
  assert.strictEqual(r.name, 'InfoTarget');
  assert.strictEqual(r.online, true);
  assert.strictEqual(r.level, 12);
  assert.strictEqual(r.races, 9);
  assert.strictEqual(r.wins, 4);
  assert.strictEqual(r.avatar, 5);
  assert.strictEqual(r.roo, true);
  assert.strictEqual(r.km, 12.5);
  assert.ok(r.car && r.car.vehicle === 0);
  assert.strictEqual(r.mito, undefined, 'another driver\'s Mito is never sent (the card masks it)');
  // saved, offline
  live('InfoTarget').save();
  b.close();
  await new Promise(res => setTimeout(res, 200));
  r = await a.req('driver.info', { name: 'InfoTarget' });
  assert.ok(r.ok, r.error);
  assert.strictEqual(r.online, false);
  assert.strictEqual(r.level, 12);
  r = await a.req('driver.info', { name: 'NobodyHere' });
  assert.ok(!r.ok);
  a.close();
});
