// Rental cars (DriftCity_RE/SERVICES.md section 3). The data: init\visualitem.xlt type 30 "RENTCAR" (50 rows,
// pc_rent_* "Period" and pc_rent0_* "Usage") whose value is "<car unique id> <V level> <kind> [rental set ids]" (kind 1
// = time-limited, 2 = mileage-limited "until you run out of Mittron Fuel"), sold through init\vshopitem.xlt (Top 4
// TOP_RENT in vshopcategory, the VSHOP 2450000 RENT tabs SPD / ACC / COL / BST / ETC / SPECIAL, shown when the server
// bits net+0x294 & 0x3c00000 are set, 0x6611f0) for G-Coin ($600 7 days / $1800 30 days). The client's texts: 10950 no
// sale to the dealer, 10951 no auction, 10952 no upgrade, 10953 / 10958 a mileage-limited car can't be refilled,
// 10959 a time-limited one refills like any car, 10956 a rental is bought on its own (the cart empties); op 0x06d
// (0x502850) returns an expired rental: 10968 "It's time to return your rental car. You will automatically be moved
// to your garage." (OK -> garage) or 10969 "Your rental car has been returned because the time expired..." + the same
// line in the chat (colour 0xFFF29177).
// Remake rules: no G-Coin, so a rental costs its dollar price x 1000 in Mito (7 days 600,000, 30 days 1,800,000); each
// rental is a new car at its V level ("Time not stackable, buying again will give you a new car!"), RentKind 1 / 2,
// RentUntil (Unix ms) = now + the period for both kinds; a mileage-limited car is also returned when its tank runs dry
// (the client's fuel is local: it sends rental.return {slot, reason: 'fuel'}). The rental set parts are not fitted.
// Expired rentals are swept on every rental.* request and every 30 s for online drivers (push rental.returned).
'use strict';

const fs = require('node:fs');
const path = require('node:path');
const { XltTable } = require('./gamedata');
const { newCar } = require('./economy');

const MITO_PER_DOLLAR = 1000;
const DAY_MS = 86400000;
const MAX_CARS = 40;
const fail = (error, key) => (key ? { ok: false, error, key } : { ok: false, error });
const TAB_OF = { s: 'SPD', a: 'ACC', c: 'COL', b: 'BST', n: 'ETC' };

function readFirst(dir, rels) {
  for (const rel of rels) {
    const f = path.join(dir, ...rel.split('/'));
    try { if (fs.existsSync(f)) return fs.readFileSync(f); } catch { /* next */ }
  }
  return null;
}

/** The rental catalogue from visualitem (type 30) + vshopitem prices; [] without game data. */
function loadCatalog(gd) {
  if (!gd || !gd.dir) return [];
  const vi = readFirst(gd.dir, ['init/visualitem.xlt', 'data/init/visualitem.xlt']);
  const vs = readFirst(gd.dir, ['init/vshopitem.xlt', 'data/init/vshopitem.xlt']);
  if (!vi || !vs) return [];
  const shop = new Map();
  const t = new XltTable(vs, 2);
  for (const r of t.rows) {
    if (t.get(r, 'Support') !== '1') continue;
    const item = t.get(r, 'ItemName').trim().toLowerCase();
    if (!item.startsWith('pc_rent') || shop.has(item)) continue;
    const periods = [['7D', 'Period 7D', '$Price 7D'], ['30D', 'Period 30D', '$Price 30D']]
      .map(([, d, pr]) => [t.int(r, d), t.int(r, pr)]).filter(([d, pr]) => d > 0 && pr > 0)
      .map(([d, pr]) => ({ days: d, mito: pr * MITO_PER_DOLLAR }));
    shop.set(item, { id: t.int(r, 'UniqueId'), name: t.get(r, 'MarketName').trim(), desc: t.get(r, 'Desc (Tooltip)').trim(), periods });
  }
  const out = [];
  const v = new XltTable(vi, 1);
  for (const r of v.rows) {
    if (String(r[1]).trim() !== '30') continue;
    const item = String(r[3] || '').trim().toLowerCase();
    const s = shop.get(item);
    if (!s || !s.periods.length) continue;
    const val = String(r[6] || '').trim().split(/\s+/);
    const uid = parseInt(val[0], 10), vlevel = parseInt(val[1], 10), kind = parseInt(val[2], 10);
    const car = gd.carList.find(c => c.uniqueId === uid);
    if (!car || !(kind === 1 || kind === 2)) continue;
    const grades = gd.grades(car).length || 1;
    const m = /_([sacbn])$/.exec(item);
    out.push({ id: s.id, item, name: s.name, desc: s.desc, vehicle: car.index, carName: car.name, grade: Math.max(0, Math.min(grades - 1, (vlevel || 1) - 1)),
      vlevel: vlevel || 1, kind, tab: m ? TAB_OF[m[1]] : 'ETC', sets: val.slice(3), periods: s.periods });
  }
  return out;
}

class Rentals {
  constructor({ world, switches, gamedata, clock, log }) {
    this.world = world; this.switches = switches; this.gd = gamedata; this.clock = clock || Date.now; this.log = log || (() => {});
    this.catalog = loadCatalog(gamedata);
    this.timer = setInterval(() => { try { this.sweepAll(); } catch (err) { this.log(`rentals: ${err.message}`); } }, 30000);
    if (this.timer.unref) this.timer.unref();
  }

  close() { clearInterval(this.timer); }
  owns(t) { return typeof t === 'string' && t.startsWith('rental.'); }
  row(id) { return this.catalog.find(r => r.id === id) || null; }

  /** Removes expired rentals of a profile; returns [{ name, current }]. */
  sweep(p) {
    const now = this.clock(), gone = [];
    if (!p || !Array.isArray(p.Cars)) return gone;
    for (let i = p.Cars.length - 1; i >= 0; i--) {
      const c = p.Cars[i];
      if (!(c && c.RentKind > 0 && c.RentUntil > 0 && c.RentUntil <= now)) continue;
      gone.push(this.remove(p, i));
    }
    return gone;
  }

  /** Takes car slot i off the profile; the current car moves to the first other car. */
  remove(p, i) {
    const c = p.Cars[i];
    const current = (p.CurrentCar | 0) === i;
    p.Cars.splice(i, 1);
    if (current) p.CurrentCar = Math.max(0, p.Cars.findIndex(x => !(x.RentKind > 0)));
    else if ((p.CurrentCar | 0) > i) p.CurrentCar = (p.CurrentCar | 0) - 1;
    if (p.CurrentCar >= p.Cars.length) p.CurrentCar = Math.max(0, p.Cars.length - 1);
    const r = this.gd && this.gd.car(c.VehicleIndex);
    return { name: c.RentName || (r ? r.name : 'rental car'), current };
  }

  notify(session, gone) {
    if (!gone.length) return;
    session.save();
    for (const g of gone) session.send({ t: 'rental.returned', name: g.name, current: g.current });
    session.send({ t: 'driver', driver: session.profile });
    if (this.world && this.world.refreshLook) this.world.refreshLook(session);
  }

  sweepAll() { for (const s of this.world.sessions.values()) if (!s.closed && s.profile) this.notify(s, this.sweep(s.profile)); }

  list(session) {
    return { ok: true, enabled: this.switches.on('rentals'), rentals: this.catalog.map(r => ({ id: r.id, item: r.item, name: r.name, desc: r.desc, vehicle: r.vehicle, carName: r.carName,
      grade: r.grade, kind: r.kind, tab: r.tab, periods: r.periods })) };
  }

  rent(session, m) {
    const p = session.profile;
    const row = this.row(m.id | 0);
    if (!row) return fail('That rental car is not offered.');
    const per = row.periods.find(x => x.days === (m.days | 0));
    if (!per) return fail('This item is not sold for that period.');
    if (p.Cars.length >= MAX_CARS) return fail('garage full');
    if ((Number(p.Mito) || 0) < per.mito) return fail('not enough Mito');
    p.Mito -= per.mito;
    const car = newCar(row.vehicle, m.color | 0, 0);
    car.Grade = row.grade;
    car.RentKind = row.kind; car.RentUntil = this.clock() + per.days * DAY_MS; car.RentItem = row.id; car.RentName = row.name;
    p.Cars.push(car);
    p.CurrentCar = p.Cars.length - 1;
    this.log(`${session.name} rented ${row.item} for ${per.days} days`);
    return { ok: true, slot: p.CurrentCar, until: car.RentUntil, kind: row.kind, mito: -per.mito };
  }

  /** rental.return {slot, reason}: a mileage-limited car whose tank ran dry (or a driver giving a rental back). */
  giveBack(session, m) {
    const p = session.profile;
    const i = m.slot | 0;
    const c = p.Cars[i];
    if (!c || !(c.RentKind > 0)) return fail('That is not a rental car.');
    if (m.reason === 'fuel' && c.RentKind !== 2) return fail('Time-Limited Rental Cars need to be refilled with Mittron like a normal car.', '10959');
    const g = this.remove(p, i);
    return { ok: true, returned: g.name, current: g.current };
  }

  handle(session, m) {
    const gone = this.sweep(session.profile);
    if (gone.length) this.notify(session, gone);
    if (m.t === 'rental.list') return this.list(session);
    if (!this.switches.on('rentals')) return fail('This feature is currently unavailable.', '10745');
    let r;
    switch (m.t) {
      case 'rental.rent': r = this.rent(session, m); break;
      case 'rental.return': r = this.giveBack(session, m); break;
      default: return fail(`unknown message ${m.t}`);
    }
    if (r.ok) { session.save(); if (this.world && this.world.refreshLook) this.world.refreshLook(session); }
    return { ...r, driver: session.profile };
  }
}

/** The client's refusals for a rental car (hooks for economy.js / auction.js / goods.js): text and key, or null. */
function refusal(car, action) {
  if (!car || !(car.RentKind > 0)) return null;
  switch (action) {
    case 'sell': return fail('You cannot sell the rental car to the dealership.', '10950');
    case 'auction': case 'trade': case 'gift': return fail('You cannot register the rental car in the auction.', '10951');
    case 'upgrade': return fail('You cannot upgrade the rental car.', '10952');
    case 'refuel': return car.RentKind === 2 ? fail('Mileage-Limited Rental Cars cannot be refilled with Mittron.', '10953') : null;
  }
  return null;
}

module.exports = { Rentals, loadCatalog, refusal, MITO_PER_DOLLAR };
