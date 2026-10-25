'use strict';
// The original server's remaining systems (src/services.js, DriftCity_RE/SERVICES.md): switches, private channels,
// rental cars, driver rename, recommend-a-driver, crew-owned Mittron stations, the personal shop, channel battle
// leader / limits.
const { test, before, after } = require('node:test');
const assert = require('node:assert');
const os = require('node:os');
const path = require('node:path');
const fs = require('node:fs');
const WebSocket = require('ws');
const { createServer, PROTOCOL } = require('../server');

let server, port, dir, now = Date.UTC(2026, 5, 10, 12);   // a date with no delivery event season

before(async () => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), 'dcsvc-'));
  server = createServer({ port: 0, db: path.join(dir, 'test.db'), quiet: true, maxConnPerIp: 200, raceGates: false, clock: () => now, events: 'none' });
  port = await server.listen();
});
after(async () => { await server.close(); fs.rmSync(dir, { recursive: true, force: true }); });

function connect() {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(`ws://127.0.0.1:${port}`);
    const pending = new Map();
    let rid = 1, welcomed = false;
    const c = {
      pushes: [],
      req(t, body = {}) { const id = rid++; return new Promise(r => { pending.set(id, r); ws.send(JSON.stringify({ t, rid: id, ...body })); }); },
      wait(pred, ms = 2000) {
        return new Promise((res, rej) => {
          const t0 = Date.now();
          const tick = () => { const i = c.pushes.findIndex(pred); if (i >= 0) return res(c.pushes.splice(i, 1)[0]); if (Date.now() - t0 > ms) return rej(new Error('no push')); setTimeout(tick, 10); };
          tick();
        });
      },
      close() { ws.close(); },
    };
    ws.on('message', raw => {
      const m = JSON.parse(raw);
      if (m.t === 'reply' && pending.has(m.rid)) { pending.get(m.rid)(m); pending.delete(m.rid); return; }
      if (m.t === 'welcome' && !welcomed) { welcomed = true; resolve(c); return; }
      c.pushes.push(m);
    });
    ws.on('error', reject);
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

test('features: the server switches, set from opts / env and at runtime', async () => {
  const a = await driver('featuser', 'FeatUser');
  let r = await a.req('features');
  assert.ok(r.ok);
  assert.strictEqual(r.features.privateChat, true);
  assert.strictEqual(r.features.rentals, true);
  assert.strictEqual(r.features.instant, true);
  assert.strictEqual(r.features.eventDelivery, false, "'season' with no delivery season running");
  server.services.switches.set('eventDelivery', 'on');
  r = await a.req('features');
  assert.strictEqual(r.features.eventDelivery, true);
  server.services.switches.set('eventDelivery', 'season');
  a.close();
});

test('private channels: join, say, who, admin, kick, leave, 11175 / 11168 / 11176, switch off 10745', async () => {
  const a = await driver('pcha', 'PchA'), b = await driver('pchb', 'PchB'), c = await driver('pchc', 'PchC');
  let r = await a.req('pchan.say', { text: 'hi' });
  assert.strictEqual(r.key, '11168');
  r = await a.req('pchan.join', { name: 'Room1' });
  assert.ok(r.ok, r.error); assert.strictEqual(r.admin, true);
  assert.strictEqual((await a.wait(m => m.t === 'pchan' && m.kind === 'joined')).name, 'PchA');
  r = await a.req('pchan.join', { name: 'Other' });
  assert.strictEqual(r.key, '11175', 'one private channel at a time');
  r = await b.req('pchan.join', { name: 'room1' });
  assert.ok(r.ok); assert.strictEqual(r.channel, 'Room1'); assert.strictEqual(r.admin, false);
  assert.strictEqual((await a.wait(m => m.t === 'pchan' && m.kind === 'joined')).name, 'PchB');
  await c.req('pchan.join', { name: 'Room1' });
  r = await b.req('pchan.say', { text: 'hello room' });
  assert.ok(r.ok);
  const line = await c.wait(m => m.t === 'chat' && m.scope === 'private');
  assert.strictEqual(line.from, 'PchB'); assert.strictEqual(line.text, 'hello room'); assert.strictEqual(line.channel, 'Room1');
  r = await b.req('pchan.say', { channel: 'nope', text: 'x' });
  assert.strictEqual(r.key, '11168', '-<channel> must be your channel');
  r = await a.req('pchan.who');
  assert.deepStrictEqual(r.names, ['PchA', 'PchB', 'PchC']); assert.strictEqual(r.admin, 'PchA');
  r = await b.req('pchan.kick', { name: 'PchC' });
  assert.strictEqual(r.key, '11176', 'only the admin kicks');
  r = await a.req('pchan.kick', { name: 'PchC' });
  assert.ok(r.ok);
  assert.strictEqual((await c.wait(m => m.t === 'pchan' && m.kind === 'kicked')).name, 'PchC');
  r = await c.req('pchan.who');
  assert.strictEqual(r.key, '11168');
  r = await a.req('pchan.auth', { name: 'PchB' });
  assert.ok(r.ok);
  assert.strictEqual((await b.wait(m => m.t === 'pchan' && m.kind === 'admin')).name, 'PchB');
  r = await b.req('pchan.leave');
  assert.ok(r.ok);
  const adm = await a.wait(m => m.t === 'pchan' && m.kind === 'admin' && m.name === 'PchA');
  assert.strictEqual(adm.name, 'PchA', 'the admin left: the next member leads');
  // a closed session leaves by the prune
  b.close();
  server.services.switches.set('privateChat', 'off');
  r = await a.req('pchan.join', { name: 'x' });
  assert.strictEqual(r.key, '10745');
  r = await a.req('pchan.state');
  assert.strictEqual(r.enabled, false); assert.strictEqual(r.channel, 'Room1');
  server.services.switches.set('privateChat', 'on');
  a.close(); c.close();
  await new Promise(res => setTimeout(res, 100));
  server.services.pchan.prune();
  assert.strictEqual(server.services.pchan.channels.size, 0, 'the channel ends with its last member');
});

test('rental cars: catalogue, rent, refusals, fuel return, expiry push (10968 / 10969 flag)', async () => {
  const rentals = server.services.rentals;
  if (!rentals.catalog.length) return;   // no game data here
  const a = await driver('renter', 'Renter');
  const p = live('Renter').profile;
  let r = await a.req('rental.list');
  assert.ok(r.ok && r.rentals.length >= 40);
  const time = r.rentals.find(x => x.kind === 1 && x.periods.some(q => q.days === 7));
  const usage = r.rentals.find(x => x.kind === 2);
  assert.strictEqual(time.periods.find(q => q.days === 7).mito, 600000, '$600 x 1000 Mito');
  r = await a.req('rental.rent', { id: time.id, days: 7 });
  assert.ok(!r.ok, 'not enough Mito');
  p.Mito = 5000000;
  r = await a.req('rental.rent', { id: time.id, days: 3 });
  assert.ok(!r.ok, 'no such period');
  r = await a.req('rental.rent', { id: time.id, days: 7 });
  assert.ok(r.ok, r.error);
  const car = r.driver.Cars[r.slot];
  assert.strictEqual(car.RentKind, 1); assert.strictEqual(car.RentUntil, now + 7 * 86400000); assert.strictEqual(car.Grade, time.grade);
  assert.strictEqual(r.driver.CurrentCar, r.slot);
  assert.strictEqual(r.driver.Mito, 4400000);
  const { refusal } = require('../src/rentals');
  assert.strictEqual(refusal(car, 'sell').key, '10950');
  assert.strictEqual(refusal(car, 'auction').key, '10951');
  assert.strictEqual(refusal(car, 'upgrade').key, '10952');
  assert.strictEqual(refusal(car, 'refuel'), null, 'a time-limited rental refuels (10959)');
  r = await a.req('rental.return', { slot: r.slot, reason: 'fuel' });
  assert.strictEqual(r.key, '10959');
  // a mileage-limited rental: no refuel at stations, returned when the tank is dry
  r = await a.req('rental.rent', { id: usage.id, days: usage.periods[0].days });
  assert.ok(r.ok, r.error);
  const slot = r.slot;
  r = await a.req('station.fill', { litres: 10 });
  assert.strictEqual(r.key, '10953');
  r = await a.req('rental.return', { slot, reason: 'fuel' });
  assert.ok(r.ok); assert.strictEqual(r.current, true);
  assert.ok(!r.driver.Cars[r.driver.CurrentCar].RentKind, 'back in an own car');
  // the time-limited one expires: swept and pushed
  now += 8 * 86400000;
  rentals.sweepAll();
  const back = await a.wait(m => m.t === 'rental.returned');
  assert.strictEqual(back.name, time.name);
  assert.ok(!p.Cars.some(c => c.RentKind > 0));
  server.services.switches.set('rentals', 'off');
  r = await a.req('rental.rent', { id: time.id, days: 7 });
  assert.strictEqual(r.key, '10745');
  server.services.switches.set('rentals', 'on');
  a.close();
});

test('driver rename: check 10604 / 10605, the kit, 60 days, the new name everywhere', async () => {
  const a = await driver('renamer', 'OldName'), b = await driver('renameother', 'TakenName');
  let r = await a.req('rename.check', { name: 'TakenName' });
  assert.strictEqual(r.key, '10604');
  r = await a.req('rename.check', { name: 'Fresh_Name' });
  assert.ok(r.ok); assert.strictEqual(r.key, '10605');
  r = await a.req('rename.check', { name: 'bad name!' });
  assert.strictEqual(r.key, '11296');
  r = await a.req('rename.apply', { name: 'Fresh_Name' });
  assert.ok(!r.ok, 'needs the kit');
  live('OldName').profile.Mito = 4000000;
  r = await a.req('rename.buyKit');
  assert.ok(r.ok, r.error);
  assert.strictEqual(r.driver.Mito, 500000);
  assert.ok(r.driver.Items.find(i => i.Id === 'i_changenick' && i.Until > now), 'a 7-day kit');
  r = await a.req('rename.apply', { name: 'Fresh_Name' });
  assert.ok(r.ok, r.error); assert.strictEqual(r.key, '10955');
  assert.strictEqual(r.driver.Name, 'Fresh_Name');
  assert.ok(!r.driver.Items.find(i => i.Id === 'i_changenick'), 'the kit is spent');
  assert.ok(live('Fresh_Name') && !live('OldName'));
  assert.strictEqual(server.store.driver(server.world.findByName('Fresh_Name').accountId).name, 'Fresh_Name');
  const info = await b.req('driver.info', { name: 'Fresh_Name' });
  assert.ok(info.ok, 'the new name is found');
  r = await a.req('rename.buyKit');
  assert.ok(!r.ok, 'not enough Mito');
  live('Fresh_Name').profile.Items.push({ Id: 'i_changenick', Count: 1 });
  r = await a.req('rename.apply', { name: 'Again' });
  assert.ok(!r.ok && /60|more days/.test(r.error), 'unchangeable for 60 days');
  now += 61 * 86400000;
  r = await a.req('rename.apply', { name: 'Again' });
  assert.ok(r.ok, r.error);
  a.close(); b.close();
});

test('recommend-a-driver: asked once while level 10 or below; the recommender gets a note', async () => {
  const old = await driver('recold', 'RecVeteran'), nw = await driver('recnew', 'RecRookie');
  live('RecVeteran').profile.Level = 25;
  let r = await nw.req('recommend.state');
  assert.strictEqual(r.ask, true);
  r = await nw.req('recommend.send', { name: 'RecRookie' });
  assert.ok(!r.ok, 'not yourself');
  r = await nw.req('recommend.send', { name: 'Nobody_here' });
  assert.ok(!r.ok);
  live('RecVeteran').profile.Level = 5;
  r = await nw.req('recommend.send', { name: 'RecVeteran' });
  assert.ok(!r.ok, 'the recommender needs level 10');
  live('RecVeteran').profile.Level = 25;
  server.world.findByName('RecVeteran').save();
  r = await nw.req('recommend.send', { name: 'recveteran' });
  assert.ok(r.ok, r.error); assert.strictEqual(r.key, '10042'); assert.strictEqual(r.name, 'RecVeteran');
  r = await nw.req('recommend.state');
  assert.strictEqual(r.ask, false);
  const notes = await old.req('note.list');
  assert.ok(JSON.stringify(notes).includes('RecRookie'), 'a note for the recommender');
  old.close(); nw.close();
});

test('crew-owned Mittron stations: owner on the charger, 20 % fee pool, hourly payout 20268; channel battle leader / limits', async () => {
  const m = await driver('stationmaster', 'StationMaster'), f = await driver('stationfill', 'StationFill');
  const mp = live('StationMaster').profile;
  mp.Level = 20; mp.Mito = 100000;
  let r = await m.req('crew.create', { name: 'Fuelers', mark: 3 });
  assert.ok(r.ok, r.error);
  const crew = server.social.teams.q.crewByName.get('Fuelers');
  const { TABLE } = require('../src/channelbattle');
  const ch = TABLE[0];   // Channel 1 = cras-1
  server.social.teams.capture(ch.key, crew.id, 'won');
  r = await f.req('enter', { city: ch.city, channel: ch.key });
  assert.ok(r.ok, r.error);
  r = await f.req('station.info');
  assert.strictEqual(r.owner, 'Fuelers'); assert.strictEqual(r.price, 10); assert.strictEqual(r.battle, 'Channel 1');
  live('StationFill').profile.Mito = 1000;
  r = await f.req('station.fill', { litres: 50 });
  assert.ok(r.ok, r.error); assert.strictEqual(r.litres, 50); assert.strictEqual(r.driver.Mito, 500); assert.strictEqual(r.fee, 100);
  r = await f.req('station.fill', { litres: 80 });
  assert.ok(r.ok); assert.strictEqual(r.litres, 50, 'short of Mito: what 500 Mito buys (10947)'); assert.strictEqual(r.short, true);
  const paid = server.services.stations.payout();
  assert.strictEqual(paid.length, 1); assert.strictEqual(paid[0].mito, 200);
  const notes = await m.req('note.list');
  assert.ok(JSON.stringify(notes).includes("1 channel's Mittron Filling Station usage fee, 200 Mitos"), '20268');
  assert.strictEqual(server.services.stations.payout().length, 0, 'the pool was emptied');
  r = await f.req('enter', { city: TABLE[1].city, channel: TABLE[1].key });
  assert.ok(r.ok && r.channel === TABLE[1].key, JSON.stringify(r));
  r = await f.req('station.info');
  assert.strictEqual(r.owner, '', 'a vacant channel: 10169');
  // channel table: the limits and the battle leader field
  r = await m.req('crew.table');
  assert.strictEqual(r.channels[0].maxLevel, 0);
  assert.strictEqual(r.channels[12].maxLevel, 60); assert.strictEqual(r.channels[12].maxGrade, 6);
  server.social.teams.cb.q.setLeader.run('StationMaster', ch.key);
  r = await m.req('crew.table');
  assert.strictEqual(r.channels[0].leader, 'StationMaster');
  mp.Level = 70;
  r = await m.req('crew.enter', { channel: 12 });
  assert.strictEqual(r.code, 'limit', 'a semi channel refuses a level 70 driver');
  m.close(); f.close();
});

test('personal shop: the goods another driver wears that fit your car', async () => {
  const a = await driver('pshopa', 'ShopSeller'), b = await driver('pshopb', 'ShopBuyer');
  const gd = server.gamedata;
  const sp = live('ShopSeller').profile;
  // a universal part (no car model) and a car-specific one of another model
  const universal = gd ? [...gd.parts.values()].find(x => x.slot === 'neon') : null;
  const specific = gd ? [...gd.parts.values()].find(x => x.slot === 'aero' && x.car && x.car !== gd.car(sp.Cars[0].VehicleIndex).modelBase) : null;
  if (universal) sp.Cars[0].Neon = universal.id;
  if (specific) sp.Cars[0].Aero = specific.id;
  let r = await b.req('pshop.list', { name: 'ShopSeller' });
  assert.ok(r.ok, r.error);
  if (universal) {
    const row = r.goods.find(g => g.item === universal.id);
    assert.ok(row && row.fits && row.periods.length > 0);
    live('ShopBuyer').profile.Mito = 10000000;
    const days = row.periods[0].days;
    const buy = await b.req('shop.buyPeriod', { slot: 0, item: universal.id, days });
    assert.ok(buy.ok, buy.error);
    r = await b.req('pshop.list', { name: 'ShopSeller' });
    if (days === 0) assert.ok(r.goods.find(g => g.item === universal.id).owned);
  }
  if (specific && specific.car !== gd.car(live('ShopBuyer').profile.Cars[0].VehicleIndex).modelBase) assert.strictEqual(r.goods.find(g => g.item === specific.id).fits, false);
  r = await b.req('pshop.list', { name: 'NoSuchDriver' });
  assert.ok(!r.ok);
  a.close(); b.close();
});
