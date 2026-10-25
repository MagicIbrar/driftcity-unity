// Goods that move between drivers outside a trade: auction listings held in escrow (src/auction.js), note
// attachments (src/mail.js) and custom-shop gifts (src/vshop.js). One format for all of them:
//   { kind: 'stack', id, count }            a named bag stack (PlayerProfile.Items: enhancers, materials, drinks...)
//   { kind: 'shop', itemId, count }         a vshopitem entry of PlayerProfile.Inventory (paints)
//   { kind: 'part', type, grade, enhance }  a performance part (PlayerProfile.PerfParts)
//   { kind: 'car', car }                    a whole car (an OwnedCar without fitted performance parts)
//   { kind: 'vpart', item }                 a custom-shop part (vshopitem UniqueId) for the receiver's current car
// The rules here are this rebuild's own; the client's servers decided them.
'use strict';
const items = require('./items');
const { newCar, ensureCarFields, PART_FIELD } = require('./economy');

const MAX_PARTS = 60;            // workshop.js MAX_PARTS
const MAX_CARS = 40;             // economy.js buyCar
const GRADES = ['N', 'S', 'H', 'U'];
const PART_TYPES = ['Speed', 'Accel', 'Durability', 'Boost'];   // PerfPart.Type (the client's icon letters s a c b)
const isObj = x => !!x && typeof x === 'object' && !Array.isArray(x);
const fail = error => ({ ok: false, error });

/** A clean copy of a goods object from a message or the database (null when it is not one). */
function normalize(g) {
  if (!isObj(g)) return null;
  const n = Math.max(1, Math.min(9999, g.count | 0 || 1));
  switch (g.kind) {
    case 'stack': return typeof g.id === 'string' && g.id && g.id.length <= 64 ? { kind: 'stack', id: g.id, count: n } : null;
    case 'shop': return Number.isInteger(g.itemId) ? { kind: 'shop', itemId: g.itemId, count: n } : null;
    case 'part': return { kind: 'part', type: Math.max(0, Math.min(3, g.type | 0)), grade: Math.max(0, Math.min(3, g.grade | 0)), enhance: Math.max(0, Math.min(12, g.enhance | 0)), count: 1 };
    case 'car': return isObj(g.car) && Number.isInteger(g.car.VehicleIndex) ? { kind: 'car', car: g.car, count: 1 } : null;
    case 'vpart': return Number.isInteger(g.item) ? { kind: 'vpart', item: g.item, count: 1 } : null;
  }
  return null;
}

/** A short name for notes and logs ("Accel Part S+3", "Mittron Enhancer x5", "car #12"). */
function describe(g, gd) {
  if (!g) return 'nothing';
  switch (g.kind) {
    case 'stack': return g.count > 1 ? `${g.id} x${g.count}` : g.id;
    case 'shop': return g.count > 1 ? `item ${g.itemId} x${g.count}` : `item ${g.itemId}`;
    case 'part': return `${PART_TYPES[g.type]} Part ${GRADES[g.grade]}${g.enhance ? '+' + g.enhance : ''}`;
    case 'car': { const c = gd && gd.car(g.car.VehicleIndex); return c ? `${c.name} V${(g.car.Grade | 0) + 1}` : `car #${g.car.VehicleIndex}`; }
    case 'vpart': return `custom part ${g.item}`;
  }
  return 'item';
}

/**
 * Takes a bag entry or a car out of a profile for escrow. ref: an items.find() reference
 * ({ kind: 'stack'|'shop'|'part', ... }) or { kind: 'car', slot }. Returns { ok, goods } or { ok: false, error }.
 */
function takeOut(p, ref, opts = {}) {
  items.ensureItems(p);
  if (!isObj(ref)) return fail('This item is not available for auction.');
  if (ref.kind === 'car') {
    const slot = ref.slot | 0;
    const car = p.Cars[slot];
    if (!car) return fail('no such car');
    { const no = require('./rentals').refusal(car, 'auction'); if (no) return no; }   // rental cars (10951)
    ensureCarFields(car);
    if (p.Cars.length <= 1) return fail("You can't sell your last car.");
    if (slot === (p.CurrentCar | 0)) return fail('You cannot put up for auction the car that you are currently driving. Please change cars and then proceed with the auction registration.');   // 10577
    const fitted = Object.values(PART_FIELD).some(f => Number.isInteger(car[f]) && car[f] >= 0) || (Array.isArray(car.PerfSlots) && car.PerfSlots.some(u => u >= 0));
    if (fitted) return fail('If there are any equipped parts, G-Coin items, and/or Drift items, you cannot register it for auction. Remove all items before registering.');   // 10578
    if (car.FromAuction && !opts.allowResale) return fail('Cars bought from the Auction House cannot be resold in the Auction House.');   // 10586
    p.Cars.splice(slot, 1);
    if (p.CurrentCar === slot) p.CurrentCar = 0; else if (p.CurrentCar > slot) p.CurrentCar--;
    const copy = JSON.parse(JSON.stringify(car));
    copy.PerfSlots = [-1, -1, -1, -1];
    return { ok: true, goods: { kind: 'car', car: copy, count: 1 } };
  }
  const e = items.find(p, ref);
  if (!e) return fail('You cannot register more items than you own.');   // 10443
  if (e.kind === 'part' && (p.Cars || []).some(c => Array.isArray(c.PerfSlots) && c.PerfSlots.includes(e.part.Uid))) return fail('This item is not available for auction.');   // 10445
  // running period items can't be sold (10455); a set part keeps its set (Item)
  const now = Date.now();
  const until = e.kind === 'part' ? e.part.Until : e.kind === 'stack' ? ((p.Items || []).find(i => i && i.Id === e.id) || {}).Until : 0;
  if (until && until > now) return fail('Items with a period cannot be registered.');
  const goods = e.kind === 'stack' ? { kind: 'stack', id: e.id, count: e.count }
    : e.kind === 'shop' ? { kind: 'shop', itemId: e.itemId, count: e.count }
      : { kind: 'part', type: e.part.Type | 0, grade: e.part.Grade | 0, enhance: e.part.Enhance | 0, item: e.part.Item || undefined, count: 1 };
  items.remove(p, e);
  return { ok: true, goods };
}

/** Whether a profile has room for the goods (garage 40 cars, parts box 60). */
function room(p, g, gd) {
  items.ensureItems(p);
  if (!g) return fail('nothing to give');
  if (g.kind === 'car') {
    if ((p.Cars || []).length >= MAX_CARS) return fail('Cannot purchase car because there is no more room in your garage.');   // 10582
    if (gd && !gd.car(g.car.VehicleIndex)) return fail('Failed to obtain vehicle data.');   // 10487
  }
  if (g.kind === 'part' && p.PerfParts.length >= MAX_PARTS) return fail('Inventory is full');   // 10269
  if (g.kind === 'vpart') {
    const car = p.Cars[p.CurrentCar | 0];
    if (!car) return fail('no car');
    ensureCarFields(car);
    if (car.Parts.includes(g.item)) return fail('Your current car already has this part.');
    if (gd) {
      const part = gd.part(g.item), info = gd.car(car.VehicleIndex);
      if (!part) return fail('unknown part');
      if (info && !gd.fits(part, info)) return fail("This part doesn't fit the car you are driving.");
    }
  }
  return { ok: true };
}

/** Puts goods into a profile (call room() first). */
function give(p, g, gd) {
  const r = room(p, g, gd);
  if (!r.ok) return r;
  switch (g.kind) {
    case 'stack': items.give(p, g.id, g.count); break;
    case 'shop': {
      const it = p.Inventory.find(i => isObj(i) && i.ItemId === g.itemId);
      if (it) it.Count += g.count; else p.Inventory.push({ ItemId: g.itemId, Count: g.count });
      break;
    }
    case 'part':
      if (!Number.isInteger(p.NextPartUid)) p.NextPartUid = 1;
      p.PerfParts.push({ Uid: p.NextPartUid++, Type: g.type, Grade: g.grade, Enhance: g.enhance, ...(g.item ? { Item: g.item } : {}) });
      break;
    case 'car': {
      const car = Object.assign(newCar(g.car.VehicleIndex | 0, g.car.Color), JSON.parse(JSON.stringify(g.car)));
      ensureCarFields(car);
      car.PerfSlots = [-1, -1, -1, -1];
      car.FromAuction = true;
      p.Cars.push(car);
      break;
    }
    case 'vpart': {
      const car = p.Cars[p.CurrentCar | 0];
      car.Parts.push(g.item);
      if (!Array.isArray(car.GiftParts)) car.GiftParts = [];
      car.GiftParts.push(g.item);   // given parts add nothing to the resale price (ShopData.SellPrice)
      if (gd) { const part = gd.part(g.item); if (part && PART_FIELD[part.slot]) car[PART_FIELD[part.slot]] = g.item; }
      break;
    }
    default: return fail('unknown goods');
  }
  return { ok: true };
}

module.exports = { normalize, describe, takeOut, room, give, GRADES, PART_TYPES };
