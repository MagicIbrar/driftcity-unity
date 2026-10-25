// USERINFO2ND BUY_GOODS (DriftCity_RE/SERVICES.md section 7). The control 1830022 is named "착용물품구매" ("buy the goods
// being worn") in game.nui; the card's handler 0x54d0b0 has no case for it (the click does nothing in this build), so
// the rule is the remake's [inferred from the name]: BUY_GOODS lists the custom-shop parts fitted on the other
// driver's current car (and its bought paint) that fit your current car, at the custom shop's prices; a row is bought
// with the shop's own shop.buyPeriod {slot, item, days} (src/vshop.js) for your current car, so nothing passes between
// the drivers. Switch `personalShop`.
'use strict';

const { PART_FIELD } = require('./economy');
const fail = (error, key) => (key ? { ok: false, error, key } : { ok: false, error });

class PersonalShop {
  constructor({ store, world, vshop, switches, gamedata }) {
    this.store = store; this.world = world; this.vshop = vshop; this.switches = switches; this.gd = gamedata;
  }

  owns(t) { return t === 'pshop.list'; }

  profileOf(name) {
    const live = this.world.findByName(name);
    if (live && live.profile) return { name: live.name, p: live.profile };
    const row = this.store.q.driverByName.get(name);
    if (!row) return null;
    try { return { name: row.name, p: JSON.parse(row.profile) }; } catch { return null; }
  }

  list(session, m) {
    if (!this.switches.on('personalShop')) return fail('This feature is currently unavailable.', '10745');
    const name = typeof m.name === 'string' ? m.name.trim() : '';
    if (!name) return fail('Enter the character name.', '10539');
    const other = this.profileOf(name);
    if (!other) return fail(`${name.slice(0, 16)} does not exist.`);
    const cars = Array.isArray(other.p.Cars) ? other.p.Cars : [];
    const car = cars[Math.max(0, Math.min(cars.length - 1, other.p.CurrentCar | 0))];
    const me = session.profile;
    const mine = me.Cars[me.CurrentCar | 0];
    if (!car || !mine) return { ok: true, name: other.name, goods: [] };
    const myInfo = this.gd ? this.gd.car(mine.VehicleIndex) : null;
    const goods = [];
    for (const [slot, field] of Object.entries(PART_FIELD)) {
      const id = car[field];
      if (!Number.isInteger(id) || id < 0) continue;
      const part = this.gd ? this.gd.part(id) : null;
      if (this.gd && !part) continue;
      const fits = !this.gd || !myInfo || this.gd.fits(part, myInfo);
      const owned = Array.isArray(mine.Parts) && mine.Parts.includes(id) && !(Array.isArray(mine.TimedParts) && mine.TimedParts.includes(id));
      goods.push({ item: id, slot, fits, owned, periods: this.vshop.periodsFor(id) });
    }
    return { ok: true, name: other.name, vehicle: car.VehicleIndex | 0, goods };
  }

  handle(session, m) { return this.list(session, m); }
}

module.exports = { PersonalShop };
