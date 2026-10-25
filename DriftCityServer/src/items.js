// Bag items beyond the workshop's own (Inventory screens, DriftCity_RE\INVENTORY.md): timed drinks, Mito cheques,
// the repair and Mittron consumables, discarding and selling. The client's server decided these; the rules here are
// this rebuild's own and mirror the Unity side (Game/Inventory/ItemUse.cs).
'use strict';
const fs = require('node:fs');
const path = require('node:path');

const GRADES = ['N', 'S', 'H', 'U'];
/** Drinks (vshopitem MITOPRIZE DRINK_*): which timed effects they start. 3ddrink / i_package_02 are the 3-drink sets. */
const DRINKS = {
  i_d_drk_01: ['exp'], i_d_drk_02: ['mito'], i_d_drk_03: ['drop'],
  '3ddrink': ['exp', 'mito', 'drop'], i_package_02: ['exp', 'mito', 'drop'],
};
/** Effect strength from the item descriptions: EXP drink +30 %, MITO drink +50 %, Item drink doubles drops. */
const BUFF_PCT = { exp: 30, mito: 50 };
const BUFF_MS = 60 * 60 * 1000;            // one hour per drink (the client's period came from its server)
/** Consumed here; the effect is on the car the client drives (repair: damage gone, Mittron: booster gauge full). */
const INSTANT = { i_d_repair01: 'repair', mitron_oil: 'boost', mitron_oil2: 'boost', mitron_oil3: 'boost',
  i_n_00004: 'fuel', i_n_00005: 'fuel', i_n_00006: 'fuel' };   // Mittron fuel: the tank is the client's (Game/Fuel/MittronFuel.cs)
/** Battle Zone quick-slot items (Game/Inventory/BattleQuickSlots.cs): spent here, their effect is the race's. */
const BATTLE = new Set(['i_n_00055', 'i_n_00056', 'i_n_00058', 'i_n_00059', 'i_n_00060', 'i_n_00099', 'i_b_untouchable_10', 'i_b_untouchable_30',
  'i_b_skidrush_10', 'i_b_skidrush_30', 'i_b_quickb_10', 'i_b_quickb_30']);
/** Sale prices: the parts-shop counter pays half its price back; performance parts by grade (+10 % per enhance level). */
const COUNTER_SELL = { i_n_00003: 2500, i_n_00003_plus: 5000, i_n_00003_deluxe: 10000, i_n_Seed_N: 5000, i_n_Seed_S: 20000, i_n_Seed_H: 60000,
  user_create_kit1: 1500, user_create_kit2: 3000, user_create_kit3: 6000 };
const PART_SELL = [8000, 32000, 120000, 300000];

const isObj = x => !!x && typeof x === 'object' && !Array.isArray(x);
const fail = error => ({ ok: false, error });

function ensureItems(p) {
  if (!Array.isArray(p.Buffs)) p.Buffs = [];
  p.Buffs = p.Buffs.filter(b => isObj(b) && typeof b.Id === 'string' && Number.isFinite(b.Until));
  if (!Array.isArray(p.Inventory)) p.Inventory = [];
  if (!Array.isArray(p.Items)) p.Items = [];
  if (!Array.isArray(p.PerfParts)) p.PerfParts = [];
  return p;
}

const count = (p, id) => (p.Items.find(i => i.Id === id) || { Count: 0 }).Count;
function give(p, id, n = 1) {
  let it = p.Items.find(i => i.Id === id);
  if (!it) p.Items.push(it = { Id: id, Count: 0 });
  it.Count += n;
  if (it.Count <= 0) p.Items.splice(p.Items.indexOf(it), 1);
}

/** Value of a Mito cheque (i_d_m_10k ... i_d_m_10g), 0 for anything else. */
function chequeValue(id) {
  const m = /^i_d_m_(\d+)(k|m|g)$/.exec(String(id));
  if (!m) return 0;
  return Number(m[1]) * { k: 1e3, m: 1e6, g: 1e9 }[m[2]];
}

/** Active drink effects at `now` (expired ones are dropped). */
function buffs(p, now = Date.now()) {
  ensureItems(p);
  p.Buffs = p.Buffs.filter(b => b.Until > now);
  return p.Buffs;
}

/** Reward bonus of the active drinks: { exp: %, mito: %, drop: extra drop count }. */
function buffBonus(p, now = Date.now()) {
  const active = buffs(p, now).map(b => b.Id);
  return { exp: active.includes('exp') ? BUFF_PCT.exp : 0, mito: active.includes('mito') ? BUFF_PCT.mito : 0, drop: active.includes('drop') ? 1 : 0 };
}

/** item.use for the items this module knows; null for the others (the workshop decides those). */
function use(p, id, now = Date.now()) {
  ensureItems(p);
  const drink = DRINKS[id], cheque = chequeValue(id), instant = INSTANT[id] || (BATTLE.has(id) ? 'battle' : undefined);
  if (!drink && !cheque && !instant) return null;
  if (count(p, id) <= 0) return fail("you don't have that");
  if (drink) {
    const active = buffs(p, now).map(b => b.Id);
    // globaltext 11059: one drink of a kind at a time
    if (drink.some(k => active.includes(k))) return fail('Having too much Special Drink will cause serious health problem. Please wait until the effect wears off.');
    for (const k of drink) p.Buffs.push({ Id: k, Until: now + BUFF_MS });
    give(p, id, -1);
    return { ok: true, effect: 'drink' };
  }
  if (cheque) {
    p.Mito = Math.min(Number.MAX_SAFE_INTEGER, (Number(p.Mito) || 0) + cheque);
    give(p, id, -1);
    return { ok: true, effect: 'mito', mito: cheque };
  }
  give(p, id, -1);
  return { ok: true, effect: instant };
}

/** The bag entry a message names: { kind: 'stack', id, count } | { kind: 'shop', itemId, count } | { kind: 'part', uid }. */
function find(p, m) {
  ensureItems(p);
  if (!isObj(m)) return null;
  const n = Math.max(1, Math.min(9999, m.count | 0 || 1));
  if (m.kind === 'stack') {
    const id = String(m.id || '');
    const have = count(p, id);
    return have >= n ? { kind: 'stack', id, count: n, have } : null;
  }
  if (m.kind === 'shop') {
    const it = p.Inventory.find(i => isObj(i) && i.ItemId === (m.itemId | 0));
    return it && it.Count >= n ? { kind: 'shop', itemId: it.ItemId, count: n, entry: it } : null;
  }
  if (m.kind === 'part') {
    const part = p.PerfParts.find(x => isObj(x) && x.Uid === (m.uid | 0));
    return part ? { kind: 'part', part, count: 1 } : null;
  }
  return null;
}

function remove(p, e) {
  if (e.kind === 'stack') give(p, e.id, -e.count);
  else if (e.kind === 'shop') { e.entry.Count -= e.count; if (e.entry.Count <= 0) p.Inventory.splice(p.Inventory.indexOf(e.entry), 1); }
  else {
    for (const c of p.Cars || []) if (Array.isArray(c.PerfSlots)) c.PerfSlots = c.PerfSlots.map(x => (x === e.part.Uid ? -1 : x));
    p.PerfParts.splice(p.PerfParts.indexOf(e.part), 1);
  }
}

/** Sale price of a bag entry (0 = can't be sold). */
function sellPrice(e) {
  if (e.kind === 'stack') return (COUNTER_SELL[e.id] || 0) * e.count;
  if (e.kind === 'part') return Math.floor(PART_SELL[e.part.Grade] * (1 + 0.1 * (e.part.Enhance | 0)));
  return 0;
}

/** item.discard: throws a bag entry away (ItemDelete / ItemDeleteEA in localize\inventory.xlt). */
function discard(p, m, now = Date.now()) {
  const e = find(p, m);
  if (!e) return fail("you don't have that");
  // 10455: a period item can't be thrown away while its time runs (an expired one can)
  if (timed(p, e, now)) return fail('You cannot discard paid-for items that have expiration dates.');
  remove(p, e);
  return { ok: true };
}

/** item.sell: sells a bag entry for its sale price. */
function sell(p, m) {
  const e = find(p, m);
  if (!e) return fail("you don't have that");
  const price = sellPrice(e);
  if (price <= 0) return fail('This item cannot be sold.');
  remove(p, e);
  p.Mito = Math.min(Number.MAX_SAFE_INTEGER, (Number(p.Mito) || 0) + price);
  return { ok: true, mito: price };
}

// ---------------- period items (DriftCity_RE\INVENTORY.md "Expiring items") ----------------
// A stack's period is one expiry for the whole stack (Items[].Until, Unix ms; a new grant adds its period, client text
// 11044), parts carry their own (PerfParts[].Until). The sweep takes expired stacks away, turns expired set parts into
// their "_to" form (bonus gone, restorable with a lube) and removes expired rental parts. Mirrors Game/Inventory/ItemPeriods.cs.
const HOUR = 3600 * 1000;

/** Is a found bag entry a period item whose time still runs? */
function timed(p, e, now = Date.now()) {
  if (e.kind === 'stack') { const it = p.Items.find(i => i.Id === e.id); return !!it && Number(it.Until) > now; }
  if (e.kind === 'part') return Number(e.part.Until) > now;
  return false;
}

/** Gives a stack with a period in hours (0 = a plain grant): the period is added to what the stack has left. */
function giveTimed(p, id, n = 1, hours = 0, now = Date.now()) {
  ensureItems(p);
  give(p, id, n);
  if (!(hours > 0)) return;
  const it = p.Items.find(i => i.Id === id);
  if (it) it.Until = Math.max(Number(it.Until) || 0, now) + hours * HOUR;
}

/** Takes away / converts everything whose time is up; returns the ids that expired. */
function sweep(p, now = Date.now()) {
  ensureItems(p);
  const gone = [];
  p.Items = p.Items.filter(it => {
    const dead = Number(it.Until) > 0 && it.Until <= now;
    if (dead) gone.push(it.Id);
    return !dead;
  });
  for (const part of p.PerfParts.slice()) {
    if (!(Number(part.Until) > 0 && part.Until <= now)) continue;
    const row = setRow(part.Item);
    gone.push(part.Item || `part ${part.Uid}`);
    const to = row && setRow(row.counterpart);
    if (to && to.expired) { part.Item = to.id; part.Until = 0; continue; }
    for (const c of p.Cars || []) if (Array.isArray(c.PerfSlots)) c.PerfSlots = c.PerfSlots.map(x => (x === part.Uid ? -1 : x));
    p.PerfParts.splice(p.PerfParts.indexOf(part), 1);
  }
  return gone;
}

// ---------------- set parts (World/Data/ItemClient.tdf; Game/Inventory/SetParts.cs) ----------------
let setTable = null, dataDir = null;
const LUBE = { Jew_A: 'i_d_set_ch01', Jew_B: 'i_d_set_ch02' };
const LUBE_PRICE = { i_d_set_ch01: 25000, i_d_set_ch02: 35000 };   // 100 Mito per G-Coin of the shop price (250 / 350)
const MAX_PARTS = 60;
const STAT = { speed: 0, accel: 1, crash: 2, boost: 3 };

function setDataDir(dir) { dataDir = dir || null; setTable = null; }

function loadSets() {
  if (setTable) return setTable;
  setTable = new Map();
  let dir = dataDir || process.env.DC_GAMEDATA;
  if (!dir) { try { dir = require('./gamedata').DEFAULT_DIR; } catch { dir = null; } }
  if (!dir) return setTable;
  const { tdfRows } = require('./economy');
  const cell = x => { const v = String(x || '').trim(); return v === 'n/a' ? '' : v; };
  const int = x => { const v = parseInt(String(x || '').trim(), 10); return Number.isFinite(v) ? v : 0; };
  for (const rel of [['data', 'world', 'data', 'itemclient.tdf'], ['world', 'data', 'itemclient.tdf'], ['data', 'itemclient.tdf']]) {
    const f = path.join(dir, ...rel);
    let buf;
    try { if (!fs.existsSync(f)) continue; buf = fs.readFileSync(f); } catch { continue; }
    for (const r of tdfRows(buf)) {
      if (r.length < 23 || !cell(r[1])) continue;
      const type = STAT[cell(r[0]).toLowerCase()];
      const id = cell(r[2]);
      if (type === undefined || !id || setTable.has(id)) continue;
      const g = GRADES.indexOf(cell(r[6]));
      const counterpart = cell(r[14]) === '0' ? '' : cell(r[14]);
      setTable.set(id, { id, set: cell(r[1]), type, grade: g < 0 ? 2 : g, level: int(r[7]), points: int(r[9]), counterpart,
        hours: int(r[21]), lube: LUBE[cell(r[22])] || null, expired: id.endsWith('_to') });
    }
    break;
  }
  return setTable;
}

function setRow(id) { return id ? loadSets().get(String(id)) || null : null; }

/** set.fit: a set part from the bag (a stack) becomes a performance part on the car's slot of its stat; its period starts. */
function setFit(p, m, now = Date.now()) {
  ensureItems(p);
  const id = String((m && m.id) || ''), car = (p.Cars || [])[(m && m.slot) | 0];
  const r = setRow(id);
  if (!r || count(p, id) <= 0) return fail("you don't have that");
  if (!car) return fail('no such car');
  if ((p.Level | 0) < r.level) return fail('Your level is not high enough to equip this item.');
  if (p.PerfParts.length >= MAX_PARTS) return fail('Inventory is full.');
  if (!Number.isInteger(p.NextPartUid)) p.NextPartUid = 1;
  give(p, id, -1);
  const part = { Uid: p.NextPartUid++, Type: r.type, Grade: r.grade, Enhance: 0, Item: r.id, Until: r.hours > 0 ? now + r.hours * HOUR : 0 };
  p.PerfParts.push(part);
  if (!Array.isArray(car.PerfSlots) || car.PerfSlots.length !== 4) car.PerfSlots = [-1, -1, -1, -1];
  car.PerfSlots[r.type] = part.Uid;
  return { ok: true };
}

/** set.restore: one lube brings every expired part of its sets fitted on the car back for another period. */
function setRestore(p, m, now = Date.now()) {
  ensureItems(p);
  const lube = String((m && m.lube) || ''), car = (p.Cars || [])[(m && m.slot) | 0];
  if (!car) return fail('no such car');
  if (count(p, lube) <= 0) return fail('You need a Restoration Lube.');
  const list = (car.PerfSlots || []).map(u => p.PerfParts.find(x => isObj(x) && x.Uid === u)).filter(x => {
    const r = x && setRow(x.Item);
    return r && r.expired && r.lube === lube && setRow(r.counterpart);
  });
  if (!list.length) return fail('Nothing on this car needs that lube.');
  give(p, lube, -1);
  for (const part of list) {
    const back = setRow(setRow(part.Item).counterpart);
    part.Item = back.id;
    part.Until = now + (back.hours > 0 ? back.hours : 720) * HOUR;
  }
  return { ok: true, restored: list.length };
}

/** set.buyLube: the restore box's BUY. */
function buyLube(p, m) {
  ensureItems(p);
  const lube = String((m && m.lube) || '');
  const price = LUBE_PRICE[lube];
  if (!price) return fail('Not sold here.');
  if ((Number(p.Mito) || 0) < price) return fail('Not enough Mito.');
  p.Mito -= price;
  give(p, lube, 1);
  return { ok: true, mito: -price };
}

/** The inventory messages of this module (server.js routes them here). */
function handle(t, p, m, now = Date.now()) {
  switch (t) {
    case 'item.expire': return { ok: true, expired: sweep(p, now) };
    case 'set.fit': sweep(p, now); return setFit(p, m, now);
    case 'set.restore': sweep(p, now); return setRestore(p, m, now);
    case 'set.buyLube': return buyLube(p, m);
  }
  return null;
}

module.exports = { use, discard, sell, buffs, buffBonus, chequeValue, sellPrice, find, remove, ensureItems, give, count, DRINKS, BUFF_MS, BUFF_PCT, GRADES,
  timed, giveTimed, sweep, setRow, setFit, setRestore, buyLube, handle, setDataDir, LUBE_PRICE };
