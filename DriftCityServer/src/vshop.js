// Custom-shop extras (DriftCity_RE\SHOPS.md section 2): gifts (SENDGIFT 2840000 / RECVGIFT 2850000), the buy history
// with its sent / received gift tabs (BUYHISTORY 2290000), the wish lists (VSHOP WISHLIST 2540000 and the car dealer's
// AUTOSHOPADV 2550000) and mileage (BUYMILEAGE 2630000, the dealer's MILLAGE line).
// The client sold these for G-Coin (paid currency) and mileage; this rebuild has no paid currency (remake rules):
//   - every Mito price stays a Mito price; a gift costs the sender the item's Mito price;
//   - mileage is earned at 1 point per 100 Mito spent in the dealer and the custom shop (economy.js), and buys a
//     custom-shop part or paint for a tenth of its Mito price (MILEAGE_RATE), rounded up to 10;
//   - gifts arrive as a note carrying the item (src/mail.js); accepting takes it (paints into the bag, parts onto the
//     receiver's current car, which they must fit), from the custom shop's received-gift list or the note.
// Period prices (SHOPS.md 2b, client Shops/ShopPeriods.cs; vshopitem "Period 7D / 30D / 90D / 365D" Mito prices, 0x64b1a0):
//   - a part sells for its table periods, plus unlimited at UNLIMITED_FACTOR x its 30-day price (the price parts were sold
//     for before); a part without period prices sells unlimited at its old price; gifts are sent unlimited;
//   - shop.buyPeriod {slot, item, days}: buying a period part again adds the days, an unlimited purchase makes it
//     permanent; a period part is a gift part (car.GiftParts: no resale value) and its expiry is car.TimedParts /
//     car.TimedUntil (Unix ms); shop.mileage takes the same days at a tenth of that period's price;
//   - expired period parts leave the car (taken off if fitted): sweepParts on the shop messages and shop.partsExpire.
'use strict';
const { cleanText } = require('./world');
const { ensureCarFields, PART_FIELD } = require('./economy');
const goods = require('./goods');

const GIFT_LEVEL = 5;            // remake rule (Etc.VShopGiftLevel came from the server's config)
const COMMENT_MAX = 80;
const MILEAGE_PER_MITO = 100;    // 1 point per 100 Mito spent
const MILEAGE_RATE = 10;         // mileage price = Mito price / 10
const WISH_MAX = 50;
const HISTORY_MAX = 200;
const UNLIMITED_FACTOR = 3;      // remake rule: unlimited = 3 x the 30-day price
const DAY_MS = 86400000;
const isObj = x => !!x && typeof x === 'object' && !Array.isArray(x);
const fail = error => ({ ok: false, error });

const mileagePrice = mito => Math.max(10, Math.ceil(mito / MILEAGE_RATE / 10) * 10);
const mileageFor = mito => Math.max(0, Math.floor(mito / MILEAGE_PER_MITO));

class VShop {
  /** deps: { store, world, social (src/social.js), log, clock, gamedata } */
  constructor(deps) {
    this.store = deps.store; this.world = deps.world; this.social = deps.social; this.log = deps.log || (() => {});
    this.clock = deps.clock || Date.now; this.gd = deps.gamedata || null;
    this.giftLevel = Number.isInteger(deps.giftLevel) ? deps.giftLevel : GIFT_LEVEL;
    this.store.db.exec(`
      CREATE TABLE IF NOT EXISTS shop_log (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        account_id INTEGER NOT NULL,
        kind TEXT NOT NULL,            -- buy | send | recv
        item TEXT NOT NULL,
        price INTEGER NOT NULL DEFAULT 0,
        currency TEXT NOT NULL DEFAULT 'mito',
        other TEXT NOT NULL DEFAULT '',
        at INTEGER NOT NULL
      );
      CREATE INDEX IF NOT EXISTS shop_log_account ON shop_log(account_id, kind);
    `);
    this.q = {
      add: this.store.db.prepare('INSERT INTO shop_log (account_id, kind, item, price, currency, other, at) VALUES (?, ?, ?, ?, ?, ?, ?)'),
      list: this.store.db.prepare('SELECT * FROM shop_log WHERE account_id = ? AND kind = ? ORDER BY id DESC LIMIT ?'),
    };
  }

  owns(t) { return typeof t === 'string' && (t.startsWith('gift.') || t.startsWith('wish.') || t === 'shop.history' || t === 'shop.mileage' || t === 'shop.buyPeriod' || t === 'shop.partsExpire'); }

  handle(session, m) {
    if (!isObj(m)) m = {};
    switch (m.t) {
      case 'gift.send': return this.send(session, m);
      case 'gift.list': return this.giftList(session);
      case 'gift.accept': return this.social.mail.take(session, m.id | 0);
      case 'wish.set': return this.setWish(session, m);
      case 'shop.history': return this.history(session);
      case 'shop.mileage': return this.buyWithMileage(session, m);
      case 'shop.buyPeriod': return this.buyPeriod(session, m);
      case 'shop.partsExpire': return this.expire(session);
    }
    return fail(`unknown message ${String(m.t).slice(0, 32)}`);
  }

  log_(accountId, kind, item, price, currency = 'mito', other = '') { this.q.add.run(accountId, kind, String(item).slice(0, 80), Math.max(0, price | 0), currency, String(other || '').slice(0, 32), this.clock()); }

  /** A shop purchase that went through (server.js calls it for shop.buyCar / buyPart / paint): history and mileage. */
  bought(session, m, r) {
    const p = session.profile;
    const price = Math.max(0, (r && r.price) | 0);
    let name = m.t;
    if (m.t === 'shop.buyCar' && this.gd) { const c = this.gd.car(m.vehicleIndex | 0); name = c ? c.name : 'car'; }
    else if (m.t === 'shop.buyPart') name = `custom part ${m.item | 0}`;
    else if (m.t === 'shop.paint') name = `paint ${m.itemId | 0}`;
    this.log_(session.accountId, 'buy', name, price);
    p.Mileage = Math.max(0, (Number(p.Mileage) || 0) + mileageFor(price));
  }

  /** The prices a custom part sells at: [{ days, price }], shortest period first, unlimited (days 0) last. Without game data
   *  the client's list is taken within bounds (tests / the built-in rules, like economy.buyPart). */
  periodsFor(item, ref = {}) {
    const part = this.gd ? this.gd.part(item) : null;
    let periods, base;
    const list = isObj(ref) && Array.isArray(ref.periods) ? ref.periods : [];
    if (part) { periods = (part.periods || []).map(([days, price]) => ({ days, price })); base = part.price; }
    else {
      periods = list.filter(x => Array.isArray(x) && (x[0] | 0) > 0 && (x[1] | 0) > 0)
        .slice(0, 4).map(x => ({ days: Math.min(365, x[0] | 0), price: Math.max(100, Math.min(2000000, x[1] | 0)) }));
      const unl = list.find(x => Array.isArray(x) && (x[0] | 0) === 0);
      const asked = unl ? Math.floor((unl[1] | 0) / (periods.length ? UNLIMITED_FACTOR : 1)) : (isObj(ref) ? ref.price | 0 : 0);
      base = Math.max(1000, Math.min(2000000, asked));
    }
    periods.push({ days: 0, price: periods.length ? base * UNLIMITED_FACTOR : base });
    return periods;
  }

  /** One period's price (null when the part has no such period). */
  periodPrice(item, days, ref = {}) { return this.periodsFor(item, ref).find(x => x.days === (days | 0)) || null; }

  /** What an item costs and what goods it becomes: a custom part (for a car) or a paint (vshopitem UniqueId). */
  priced(ref, gift = false) {
    if (!isObj(ref)) return null;
    if (ref.kind === 'vpart' && Number.isInteger(ref.item)) {
      const part = this.gd ? this.gd.part(ref.item) : null;
      if (this.gd && !part) return null;
      // the period asked for (days, 0 = unlimited); gifts always go unlimited
      const pick = this.periodPrice(ref.item, gift ? 0 : ref.days | 0, ref);
      if (!pick) return null;
      return { goods: { kind: 'vpart', item: ref.item, count: 1 }, price: pick.price, days: pick.days, name: ref.name ? String(ref.name).slice(0, 40) : `custom part ${ref.item}`, slot: part ? part.slot : ref.slot };
    }
    if (ref.kind === 'shop' && Number.isInteger(ref.itemId)) {
      let price;
      if (this.gd) { if (!this.gd.paints.has(ref.itemId)) return null; price = this.gd.paints.get(ref.itemId); }
      else price = Math.max(100, Math.min(5000000, ref.price | 0));
      return { goods: { kind: 'shop', itemId: ref.itemId, count: 1 }, price, name: ref.name ? String(ref.name).slice(0, 40) : `paint ${ref.itemId}` };
    }
    return null;
  }

  /** SENDGIFT BTN_GIFT: the sender pays the Mito price, the receiver gets a note with the item. */
  send(session, m) {
    const p = session.profile;
    if ((p.Level | 0) < this.giftLevel) return fail(`You need level ${this.giftLevel} to send gifts.`);
    const it = this.priced(m.item, true);
    if (!it) return fail('This item cannot be sent as a gift.');
    const to = this.social.driver(typeof m.to === 'string' ? m.to : '');
    if (!to) return fail(`${m.to || 'That driver'} does not exist.`);
    if (to.accountId === session.accountId) return fail('You cannot send a gift to yourself.');
    if (this.social.blocks(to.accountId, session.accountId)) return fail(`${to.name} cannot receive gifts.`);
    if ((p.Mito | 0) < it.price) return fail('You do not have enough Mito.');
    const comment = cleanText(m.comment || '', COMMENT_MAX + 1).slice(0, COMMENT_MAX);
    p.Mito -= it.price;
    p.Mileage = Math.max(0, (Number(p.Mileage) || 0) + mileageFor(it.price));
    session.save();
    const text = `${session.name} sent you a gift: ${it.name}.` + (comment ? ` "${comment}"` : '') + ' Accept it from the gift box of the custom shop (or take it here).';
    const noteId = this.social.mail.system(to.accountId, text, 0, session.name, it.goods);
    this.log_(session.accountId, 'send', it.name, it.price, 'mito', to.name);
    this.log_(to.accountId, 'recv', it.name, it.price, 'mito', session.name);
    const live = this.social.live(to.accountId);
    if (live) live.send({ t: 'gift.new', from: session.name, name: it.name, note: noteId });
    this.log(`gift: ${session.name} -> ${to.name}: ${it.name} (${it.price} Mito)`);
    return { ok: true, price: it.price, to: to.name, note: noteId, driver: p };
  }

  /** RECVGIFT list: notes that still carry an item, plus the sent / received history. */
  giftList(session) {
    const pending = this.social.mail.itemNotes(session.accountId).map(n => ({ id: n.id, sender: n.sender, sentAt: n.sentAt, content: n.content, item: n.item, itemName: n.itemName }));
    return { ok: true, pending, sent: this.rows(session.accountId, 'send'), received: this.rows(session.accountId, 'recv'), now: this.clock() };
  }

  rows(accountId, kind) { return this.q.list.all(accountId, kind, HISTORY_MAX).map(r => ({ item: r.item, price: r.price, currency: r.currency, other: r.other, at: r.at })); }

  history(session) {
    const p = session.profile;
    return { ok: true, bought: this.rows(session.accountId, 'buy'), sent: this.rows(session.accountId, 'send'), received: this.rows(session.accountId, 'recv'), mileage: Number(p.Mileage) || 0 };
  }

  /** Wish lists: custom-shop items (UniqueIds) and dealer cars (vehicle indexes), no economic value. */
  setWish(session, m) {
    const p = session.profile;
    const ints = (a, max) => (Array.isArray(a) ? [...new Set(a.filter(Number.isInteger))].slice(0, max) : null);
    const items = ints(m.items, WISH_MAX), cars = ints(m.cars, WISH_MAX);
    if (items) p.WishList = items;
    if (cars) p.WishCars = cars;
    session.save();
    return { ok: true, driver: p };
  }

  /** The custom shop's checkout (BUYWISHLIST 2620000 with the period buttons): a part for `days` (0 = unlimited) for Mito. */
  buyPeriod(session, m) {
    const p = session.profile;
    const car = p.Cars[m.slot | 0];
    const item = m.item | 0;
    if (!car) return fail('no such car');
    ensureCarFields(car);
    sweepParts(p, this.clock());
    let slot;
    if (this.gd) {
      const part = this.gd.part(item);
      if (!part) return fail('unknown part');
      const info = this.gd.car(car.VehicleIndex);
      if (info && !this.gd.fits(part, info)) return fail("that part doesn't fit this car");
      slot = part.slot;
    } else {
      slot = String(m.part || '');
      if (!PART_FIELD[slot] || item < 1) return fail('bad part');
    }
    const pick = this.periodPrice(item, m.days | 0, m);
    if (!pick) return fail('This item is not sold for that period.');
    ensureTimed(car);
    if (car.Parts.includes(item) && !car.TimedParts.includes(item)) return fail('already owned');
    if (car.Parts.length >= 120 && !car.Parts.includes(item)) return fail('too many parts on this car');
    if ((p.Mito | 0) < pick.price) return fail('not enough Mito');
    p.Mito -= pick.price;
    if (pick.days === 0) car.Spent = (Number(car.Spent) || 0) + pick.price;
    grantPart(car, item, slot, pick.days, this.clock());
    const name = (m.name ? String(m.name).slice(0, 40) : `custom part ${item}`) + (pick.days ? ` (${pick.days} D)` : '');
    this.log_(session.accountId, 'buy', name, pick.price);
    p.Mileage = Math.max(0, (Number(p.Mileage) || 0) + mileageFor(pick.price));
    session.save();
    if (this.world && this.world.refreshLook) this.world.refreshLook(session);
    const at = car.TimedParts.indexOf(item);
    return { ok: true, price: pick.price, days: pick.days, until: at >= 0 ? car.TimedUntil[at] : 0, driver: p };
  }

  /** shop.partsExpire: the expired period parts leave the cars; the driver comes back. */
  expire(session) {
    const gone = sweepParts(session.profile, this.clock());
    if (gone.length) { session.save(); if (this.world && this.world.refreshLook) this.world.refreshLook(session); }
    return { ok: true, expired: gone, driver: session.profile };
  }

  /** BUYMILEAGE BUY: a custom part for a car, or a paint, paid with mileage points. */
  buyWithMileage(session, m) {
    const p = session.profile;
    const it = this.priced(m.item);
    if (!it) return fail('This item cannot be bought with mileage.');
    const cost = mileagePrice(it.price);
    if ((Number(p.Mileage) || 0) < cost) return fail('You do not have enough mileage.');
    if (it.goods.kind === 'vpart') {
      const car = p.Cars[m.slot | 0];
      if (!car) return fail('no such car');
      ensureCarFields(car);
      sweepParts(p, this.clock());
      if (this.gd) {
        const part = this.gd.part(it.goods.item), info = this.gd.car(car.VehicleIndex);
        if (!part || (info && !this.gd.fits(part, info))) return fail("that part doesn't fit this car");
      }
      if (!grantPart(car, it.goods.item, it.slot, it.days | 0, this.clock())) return fail('already owned');
    } else {
      const r = goods.give(p, it.goods, this.gd);
      if (!r.ok) return r;
    }
    p.Mileage -= cost;
    session.save();
    this.log_(session.accountId, 'buy', it.name, cost, 'mileage');
    return { ok: true, mileage: cost, driver: p };
  }
}

/** Period-part fields of a car (TimedParts / TimedUntil, parallel arrays). */
function ensureTimed(car) {
  if (!Array.isArray(car.TimedParts)) car.TimedParts = [];
  if (!Array.isArray(car.TimedUntil)) car.TimedUntil = [];
  car.TimedUntil.length = car.TimedParts.length;
  for (let i = 0; i < car.TimedUntil.length; i++) if (!Number.isFinite(car.TimedUntil[i])) car.TimedUntil[i] = 0;
  if (!Array.isArray(car.GiftParts)) car.GiftParts = [];
  return car;
}

/** Adds a part for `days` (0 = for keeps) or more days to a period part; fits it. False when it is already permanent. */
function grantPart(car, item, slot, days, now) {
  ensureCarFields(car); ensureTimed(car);
  const i = car.TimedParts.indexOf(item), owned = car.Parts.includes(item);
  if (owned && i < 0) return false;
  if (days <= 0) {
    if (i >= 0) { car.TimedParts.splice(i, 1); car.TimedUntil.splice(i, 1); }
    car.GiftParts = car.GiftParts.filter(x => x !== item);
  } else if (i >= 0) car.TimedUntil[i] = Math.max(car.TimedUntil[i], now) + days * DAY_MS;
  else {
    car.TimedParts.push(item); car.TimedUntil.push(now + days * DAY_MS);
    if (!car.GiftParts.includes(item)) car.GiftParts.push(item);   // no resale value
  }
  if (!owned) car.Parts.push(item);
  if (slot && PART_FIELD[slot]) car[PART_FIELD[slot]] = item;
  return true;
}

/** Takes expired period parts off every car (a fitted one goes back to stock). Returns the part ids that went. */
function sweepParts(p, now) {
  const gone = [];
  for (const car of (p && Array.isArray(p.Cars) ? p.Cars : [])) {
    if (!Array.isArray(car.TimedParts) || !car.TimedParts.length) continue;
    ensureCarFields(car); ensureTimed(car);
    for (let i = car.TimedParts.length - 1; i >= 0; i--) {
      const until = car.TimedUntil[i];
      if (!(until > 0 && until <= now)) continue;
      const id = car.TimedParts[i];
      car.TimedParts.splice(i, 1); car.TimedUntil.splice(i, 1);
      car.Parts = car.Parts.filter(x => x !== id); car.GiftParts = car.GiftParts.filter(x => x !== id);
      for (const f of Object.values(PART_FIELD)) if (car[f] === id) car[f] = -1;
      gone.push(id);
    }
  }
  return gone;
}

module.exports = { VShop, mileagePrice, mileageFor, grantPart, sweepParts, GIFT_LEVEL, MILEAGE_RATE, MILEAGE_PER_MITO, UNLIMITED_FACTOR };
