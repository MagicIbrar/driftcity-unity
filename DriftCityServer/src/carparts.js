// The NDLG parts page's second row and option slots, and the drivers' custom marks (DriftCity_RE/INVENTORY.md "Parts page",
// PHYSICS.md section 13; client Game/Inventory/CarParts.cs and Game/CustomMarks.cs, same rules).
//  parts.fit2 { slot, uid }      a performance part from the bag into the car's second-row slot of its stat
//  parts.unfit2 { slot, type }   takes it off (the option part on it returns to the bag)
//  opt.fit { slot, index, id }   docks an option / docking part (ItemClient.tdf op_* / dock_*, a bag stack) on slot index 0..7
//  opt.remove { slot, index }    takes it off (back to the bag)
//  mark.set { png }              the driver's custom mark: a PNG (base64) of at most 128 x 128 px and MARK_MAX_BYTES
//  mark.clear                    removes it
//  mark.get { name }             another driver's mark { png, ver } (the effects\realmatch\real_%05d.dds marks above
//                                99999 were downloaded from the server, 0x4411e0 -> request 0x3f6)
// The marks live in the event_state table (key "mark:<name>"); the profile keeps only CustomMarkVer, so the driver
// message stays small. Remake rules: one mark per driver, PNG only, size limits as above.
'use strict';

const fs = require('node:fs');
const path = require('node:path');

const SLOTS = 8;
const MARK_MAX_BYTES = 32 * 1024;   // a base64 mark must fit the 64 KB WebSocket message (server.js maxPayload)
const MARK_MAX_PX = 128;
const fail = error => ({ ok: false, error });
const isObj = x => x && typeof x === 'object';

let optTable = null, dataDir = null;
function setDataDir(dir) { dataDir = dir || null; optTable = null; }

/** ItemClient.tdf op_* / dock_* rows: id -> { kind, stat (0..3, -1 any), level, points }. */
function loadOptions() {
  if (optTable) return optTable;
  optTable = new Map();
  let dir = dataDir || process.env.DC_GAMEDATA;
  if (!dir) { try { dir = require('./gamedata').DEFAULT_DIR; } catch { dir = null; } }
  if (!dir) return optTable;
  const { tdfRows } = require('./economy');
  const int = x => { const v = parseInt(String(x || '').trim(), 10); return Number.isFinite(v) ? v : 0; };
  for (const rel of [['data', 'world', 'data', 'itemclient.tdf'], ['world', 'data', 'itemclient.tdf'], ['data', 'itemclient.tdf']]) {
    const f = path.join(dir, ...rel);
    let buf;
    try { if (!fs.existsSync(f)) continue; buf = fs.readFileSync(f); } catch { continue; }
    for (const r of tdfRows(buf)) {
      const kind = String(r[0] || '').trim();
      if (r.length < 21 || !(kind.startsWith('op_') || kind.startsWith('dock_'))) continue;
      const id = String(r[2] || '').trim();
      if (!id || optTable.has(id)) continue;
      optTable.set(id, { id, kind, stat: statOf(kind), level: int(r[7]), points: int(r[9]) });
    }
    break;
  }
  return optTable;
}

function statOf(kind) { const c = kind.slice(-1).toUpperCase(); return c === 'S' ? 0 : c === 'A' ? 1 : c === 'C' ? 2 : c === 'B' ? 3 : -1; }
function option(id) { return typeof id === 'string' && id ? loadOptions().get(id) || null : null; }
/** Tests: define a row without the game data. */
function defineOption(row) { loadOptions().set(row.id, { stat: statOf(row.kind), level: 1, points: 0, ...row }); }

const count = (p, id) => ((p.Items || []).find(i => isObj(i) && i.Id === id) || { Count: 0 }).Count;
function give(p, id, n = 1) {
  if (!Array.isArray(p.Items)) p.Items = [];
  let it = p.Items.find(i => isObj(i) && i.Id === id);
  if (!it) p.Items.push(it = { Id: id, Count: 0 });
  it.Count += n;
  if (it.Count <= 0) p.Items.splice(p.Items.indexOf(it), 1);
}

function normalize(car) {
  if (!Array.isArray(car.PerfSlots) || car.PerfSlots.length !== 4) car.PerfSlots = [-1, -1, -1, -1];
  if (!Array.isArray(car.PerfSlots2) || car.PerfSlots2.length !== 4) car.PerfSlots2 = [-1, -1, -1, -1];
  if (!Array.isArray(car.OptSlots) || car.OptSlots.length !== SLOTS) car.OptSlots = Array(SLOTS).fill('');
  car.OptSlots = car.OptSlots.map(x => (typeof x === 'string' ? x : ''));
}

function carOf(p, slot) {
  const cars = Array.isArray(p.Cars) ? p.Cars : [];
  return Number.isInteger(slot) && slot >= 0 && slot < cars.length && isObj(cars[slot]) ? cars[slot] : null;
}

function partUnder(p, car, index) {
  const uid = (index % 2 === 0 ? car.PerfSlots : car.PerfSlots2)[Math.floor(index / 2)];
  return uid >= 0 ? (p.PerfParts || []).find(x => isObj(x) && x.Uid === uid) || null : null;
}

function returnOption(p, car, index) {
  const id = car.OptSlots[index];
  if (!id) return;
  give(p, id, 1);
  car.OptSlots[index] = '';
}

function fit2(p, m, now = Date.now()) {
  const car = carOf(p, m.slot);
  if (!car) return fail('No such car.');
  normalize(car);
  const part = (p.PerfParts || []).find(x => isObj(x) && x.Uid === m.uid);
  if (!part) return fail('You cannot equip this part.');                                   // 10933
  if (part.Until > 0 && part.Until <= now) return fail('This item has already expired.');  // 10454
  if ((p.Cars || []).some(c => isObj(c) && ((Array.isArray(c.PerfSlots) && c.PerfSlots.includes(part.Uid)) || (Array.isArray(c.PerfSlots2) && c.PerfSlots2.includes(part.Uid)))))
    return fail('That part is already fitted.');
  const t = part.Type | 0;
  if (t < 0 || t > 3) return fail('You cannot equip this part.');
  const old = car.PerfSlots2[t];
  car.PerfSlots2[t] = part.Uid;
  if (old >= 0) returnOption(p, car, t * 2 + 1);
  return { ok: true };
}

function unfit2(p, m) {
  const car = carOf(p, m.slot);
  const t = m.type;
  if (!car || !Number.isInteger(t) || t < 0 || t > 3) return fail('No such slot.');
  normalize(car);
  if (car.PerfSlots2[t] < 0) return fail('That slot is empty.');
  car.PerfSlots2[t] = -1;
  returnOption(p, car, t * 2 + 1);
  return { ok: true };
}

function optFit(p, m) {
  const car = carOf(p, m.slot);
  const i = m.index;
  if (!car || !Number.isInteger(i) || i < 0 || i >= SLOTS) return fail('No such slot.');
  normalize(car);
  const o = option(m.id);
  if (!o) return fail('This item is not equippable.');                                      // 10449
  if (count(p, o.id) <= 0) return fail("You don't have that.");
  if (o.level > (p.Level | 0)) return fail('Your level is too low to use this item.');        // 10451
  if (o.stat >= 0 && o.stat !== Math.floor(i / 2)) return fail('You cannot equip this part.'); // 10933
  if (!partUnder(p, car, i)) return fail('Fit a performance part in this slot first.');
  returnOption(p, car, i);
  give(p, o.id, -1);
  car.OptSlots[i] = o.id;
  return { ok: true };
}

function optRemove(p, m) {
  const car = carOf(p, m.slot);
  const i = m.index;
  if (!car || !Number.isInteger(i) || i < 0 || i >= SLOTS) return fail('No such slot.');
  normalize(car);
  if (!car.OptSlots[i]) return fail('That slot is empty.');
  returnOption(p, car, i);
  return { ok: true };
}

/** A PNG's width / height from its IHDR chunk, or null when it isn't a PNG. */
function pngSize(buf) {
  if (!Buffer.isBuffer(buf) || buf.length < 24) return null;
  const sig = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
  if (!sig.every((b, k) => buf[k] === b) || buf.toString('ascii', 12, 16) !== 'IHDR') return null;
  return { w: buf.readUInt32BE(16), h: buf.readUInt32BE(20) };
}

/** Checks a base64 mark: PNG, <= 128 px, <= MARK_MAX_BYTES. Returns the buffer or an error text. */
function checkMark(b64) {
  if (typeof b64 !== 'string' || !b64.length) return { error: 'No image.' };
  if (b64.length > Math.ceil(MARK_MAX_BYTES / 3) * 4 + 4) return { error: `The image is too large (at most ${MARK_MAX_BYTES / 1024} KB).` };
  let buf;
  try { buf = Buffer.from(b64, 'base64'); } catch { return { error: 'The image could not be read.' }; }
  if (buf.length > MARK_MAX_BYTES) return { error: `The image is too large (at most ${MARK_MAX_BYTES / 1024} KB).` };
  const s = pngSize(buf);
  if (!s) return { error: 'The mark must be a PNG image.' };
  if (s.w < 1 || s.h < 1 || s.w > MARK_MAX_PX || s.h > MARK_MAX_PX) return { error: `The mark may be at most ${MARK_MAX_PX} x ${MARK_MAX_PX} pixels.` };
  return { buf };
}

class CarParts {
  constructor({ store, world } = {}) { this.store = store; this.world = world; this.memory = new Map(); }

  owns(t) { return t === 'parts.fit2' || t === 'parts.unfit2' || t === 'opt.fit' || t === 'opt.remove' || t === 'mark.set' || t === 'mark.clear' || t === 'mark.get'; }

  markKey(name) { return 'mark:' + String(name || '').trim().toLowerCase(); }

  getMark(name) {
    const key = this.markKey(name);
    if (this.memory.has(key)) return this.memory.get(key);
    let v = null;
    try { const row = this.store && this.store.q && this.store.q.eventGet ? this.store.q.eventGet.get(key) : null; v = row ? JSON.parse(row.value) : null; } catch { v = null; }
    if (v) this.memory.set(key, v);
    return v;
  }

  putMark(name, value) {
    const key = this.markKey(name);
    if (value) this.memory.set(key, value); else this.memory.delete(key);
    try { if (this.store && this.store.q && this.store.q.eventSet) this.store.q.eventSet.run(key, JSON.stringify(value || null)); } catch { /* memory only */ }
  }

  handle(session, m) {
    const p = session.profile;
    let r;
    switch (m.t) {
      case 'parts.fit2': r = fit2(p, m); break;
      case 'parts.unfit2': r = unfit2(p, m); break;
      case 'opt.fit': r = optFit(p, m); break;
      case 'opt.remove': r = optRemove(p, m); break;
      case 'mark.set': {
        const c = checkMark(m.png);
        if (c.error) return { ...fail(c.error), driver: p };
        const ver = (p.CustomMarkVer | 0) + 1;
        this.putMark(session.name, { png: c.buf.toString('base64'), ver });
        p.CustomMarkVer = ver;
        p.CustomMark = '';
        r = { ok: true, ver };
        break;
      }
      case 'mark.clear':
        this.putMark(session.name, null);
        p.CustomMarkVer = (p.CustomMarkVer | 0) + 1;
        p.CustomMark = '';
        r = { ok: true, ver: p.CustomMarkVer };
        break;
      case 'mark.get': {
        const name = typeof m.name === 'string' ? m.name.trim() : '';
        if (!name) return fail('Enter the character name.');
        const v = this.getMark(name);
        return v ? { ok: true, name, png: v.png, ver: v.ver | 0 } : { ok: true, name, png: '', ver: 0 };
      }
      default: return fail('not available');
    }
    if (r.ok) { if (session.save) session.save(); if (this.world && m.t.startsWith('mark.')) this.world.refreshLook(session); }
    return { ...r, driver: p };
  }
}

module.exports = { CarParts, fit2, unfit2, optFit, optRemove, checkMark, pngSize, option, defineOption, setDataDir, MARK_MAX_BYTES, MARK_MAX_PX, SLOTS };
