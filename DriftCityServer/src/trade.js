// Player-to-player trade, the client's TRADE window (game.nui 1190000, DriftCity_RE\INVENTORY.md):
// a request ("%s has requested a deal. Accept?"), then both drivers put up to 8 bag entries and some Mito on the
// table, press Lock, then Trade. The original window sends lock / confirm / cancel packets (0x78dbe0 / 0x78de90 /
// 0x78dd40) and its server did the swap; here the server owns the whole state and swaps atomically.
// This rebuild's rules: both drivers in the same channel, one trade at a time, changing an offer clears both locks,
// fitted performance parts can't be offered, the swap re-checks both bags and the parts-box limit.
'use strict';
const items = require('./items');

const MAX_SLOTS = 8;            // ID_TRADE_MY_SLOT1..8
const MAX_PARTS = 60;           // workshop.js MAX_PARTS
const INVITE_MS = 30000;
const isObj = x => !!x && typeof x === 'object' && !Array.isArray(x);
const fail = error => ({ ok: false, error });

class Trades {
  /** @param world src/world.js (channels, findByName); opts.clock ms */
  constructor(world, log = () => {}, opts = {}) {
    this.world = world;
    this.log = log;
    this.clock = opts.clock || Date.now;
    this.byId = new Map();        // id -> trade
    this.bySession = new Map();   // session -> trade
    this.invites = new Map();     // id -> { id, from, to, at }
    this.nextId = 1;
  }

  handle(type, s, m) {
    if (!isObj(m)) m = {};
    switch (type) {
      case 'trade.request': return this.request(s, m.name);
      case 'trade.accept': return this.accept(s, m.id | 0);
      case 'trade.decline': return this.decline(s, m.id | 0);
      case 'trade.offer': return this.offer(s, m.items, m.mito);
      case 'trade.lock': return this.lock(s, m.lock !== false);
      case 'trade.confirm': return this.confirm(s);
      case 'trade.cancel': this.cancel(s, 'cancelled'); return { ok: true };
    }
    return fail('unknown trade message');
  }

  /** The other driver must be online, in the same channel and not trading. */
  request(s, name) {
    if (typeof name !== 'string' || !name) return fail('Cannot find a trading partner.');
    const t = this.world.findByName(name);
    if (!t || t === s || !t.profile || t.closed) return fail('Cannot find a trading partner.');
    if (!s.channel || t.channel !== s.channel) return fail('You may not trade with this user.');
    if (this.bySession.has(s) || this.bySession.has(t)) return fail('This is not allowed during a trade.');
    if (s.race || t.race) return fail('You may not trade with this user.');
    const now = this.clock();
    for (const [k, inv] of this.invites) if (now - inv.at > INVITE_MS || inv.from.closed || inv.to.closed) this.invites.delete(k);
    const id = this.nextId++;
    this.invites.set(id, { id, from: s, to: t, at: now });
    t.send({ t: 'trade.invite', id, from: s.name });
    this.log(`${s.name} asks ${t.name} to trade`);
    return { ok: true, id };
  }

  accept(s, id) {
    const inv = this.invites.get(id);
    if (!inv || inv.to !== s) return fail('Failed to create a trade.');
    this.invites.delete(id);
    const a = inv.from;
    if (a.closed || !a.profile || this.clock() - inv.at > INVITE_MS) return fail('Cannot find a trading partner.');
    if (!s.channel || a.channel !== s.channel) return fail('You may not trade with this user.');
    if (this.bySession.has(a) || this.bySession.has(s)) return fail('This is not allowed during a trade.');
    const side = who => ({ s: who, items: [], mito: 0, locked: false, confirmed: false });
    const trade = { id, a: side(a), b: side(s) };
    this.byId.set(id, trade);
    this.bySession.set(a, trade);
    this.bySession.set(s, trade);
    a.send({ t: 'trade.open', id, with: s.name });
    s.send({ t: 'trade.open', id, with: a.name });
    this.broadcast(trade);
    return { ok: true, id };
  }

  decline(s, id) {
    const inv = this.invites.get(id);
    if (!inv || inv.to !== s) return fail('no such request');
    this.invites.delete(id);
    inv.from.send({ t: 'trade.closed', id, reason: 'declined', by: s.name });
    return { ok: true };
  }

  sides(s) {
    const trade = this.bySession.get(s);
    if (!trade) return null;
    return trade.a.s === s ? { trade, me: trade.a, them: trade.b } : { trade, me: trade.b, them: trade.a };
  }

  /** Replaces the driver's offer: up to 8 entries ({ kind: 'stack', id, count } | { kind: 'shop', itemId, count } | { kind: 'part', uid }) and Mito. */
  offer(s, list, mito) {
    const x = this.sides(s);
    if (!x) return fail('Cannot find a trading partner.');
    if (!Array.isArray(list)) list = [];
    if (list.length > MAX_SLOTS) return fail('You cannot trade anymore.');
    const p = s.profile;
    const clean = [];
    const seen = new Set();
    for (const raw of list) {
      if (!isObj(raw)) return fail('This item cannot be traded.');
      const e = items.find(p, raw);
      if (!e) return fail('This item cannot be traded.');
      const key = e.kind === 'stack' ? 's:' + e.id : e.kind === 'shop' ? 'i:' + e.itemId : 'p:' + e.part.Uid;
      if (seen.has(key)) return fail('This item cannot be traded.');
      seen.add(key);
      if (e.kind === 'part' && (p.Cars || []).some(c => Array.isArray(c.PerfSlots) && c.PerfSlots.includes(e.part.Uid))) return fail('This item cannot be traded.');
      if (items.timed(p, e)) return fail('This item cannot be traded.');   // period items stay with their owner (remake rule)
      clean.push(e.kind === 'stack' ? { kind: 'stack', id: e.id, count: e.count }
        : e.kind === 'shop' ? { kind: 'shop', itemId: e.itemId, count: e.count }
          : { kind: 'part', uid: e.part.Uid, type: e.part.Type, grade: e.part.Grade, enhance: e.part.Enhance | 0 });
    }
    const money = Math.floor(Number(mito) || 0);
    if (money < 0 || money > (Number(p.Mito) || 0)) return fail('Failed to suggest the item trade amount.');
    x.me.items = clean;
    x.me.mito = money;
    // a changed offer needs both drivers to look again
    x.me.locked = x.them.locked = false;
    x.me.confirmed = x.them.confirmed = false;
    this.broadcast(x.trade);
    return { ok: true };
  }

  lock(s, on) {
    const x = this.sides(s);
    if (!x) return fail('Failed to lock the trade items.');
    x.me.locked = !!on;
    if (!on) x.me.confirmed = x.them.confirmed = false;
    this.broadcast(x.trade);
    return { ok: true };
  }

  /** Trade button: needs both locks; the second confirm swaps. */
  confirm(s) {
    const x = this.sides(s);
    if (!x) return fail('Failed to complete the item trade.');
    if (!x.me.locked || !x.them.locked) return fail('Lock the trade first.');
    x.me.confirmed = true;
    if (!x.them.confirmed) { this.broadcast(x.trade); return { ok: true }; }
    const r = this.swap(x.trade);
    if (!r.ok) { this.close(x.trade, 'failed', r.error); return r; }
    return { ok: true, done: true };
  }

  /** Re-checks both offers against the bags as they are now, then moves everything or nothing. */
  swap(trade) {
    const A = trade.a, B = trade.b;
    const pa = A.s.profile, pb = B.s.profile;
    if (!pa || !pb || A.s.closed || B.s.closed) return fail('Cannot find a trading partner.');
    if (!A.s.channel || A.s.channel !== B.s.channel) return fail('You may not trade with this user.');
    const resolve = (p, side) => {
      const out = [];
      for (const o of side.items) {
        const e = items.find(p, o);
        if (!e) return null;
        if (e.kind === 'part' && (p.Cars || []).some(c => Array.isArray(c.PerfSlots) && c.PerfSlots.includes(e.part.Uid))) return null;
        if (items.timed(p, e)) return null;
        out.push(e);
      }
      return out;
    };
    const ea = resolve(pa, A), eb = resolve(pb, B);
    if (!ea || !eb) return fail('Failed to trade items.');
    if (A.mito > (Number(pa.Mito) || 0) || B.mito > (Number(pb.Mito) || 0)) return fail('Failed to suggest the item trade amount.');
    const partsIn = list => list.filter(e => e.kind === 'part').length;
    if (pa.PerfParts.length - partsIn(ea) + partsIn(eb) > MAX_PARTS || pb.PerfParts.length - partsIn(eb) + partsIn(ea) > MAX_PARTS) return fail('Inventory is full');
    // take everything out first, then hand it over
    const moved = (from, to, list) => {
      const gifts = list.map(e => (e.kind === 'part' ? { kind: 'part', part: { ...e.part } } : { ...e }));
      for (const e of list) items.remove(from, e);
      return () => {
        for (const g of gifts) {
          if (g.kind === 'stack') items.give(to, g.id, g.count);
          else if (g.kind === 'shop') {
            const it = to.Inventory.find(i => isObj(i) && i.ItemId === g.itemId);
            if (it) it.Count += g.count; else to.Inventory.push({ ItemId: g.itemId, Count: g.count });
          } else {
            if (!Number.isInteger(to.NextPartUid)) to.NextPartUid = 1;
            const np = { Uid: to.NextPartUid++, Type: g.part.Type, Grade: g.part.Grade, Enhance: g.part.Enhance | 0 };
            if (g.part.Item) np.Item = String(g.part.Item);   // set parts keep their set (an expired "_to" form can be traded)
            to.PerfParts.push(np);
          }
        }
      };
    };
    const giveB = moved(pa, pb, ea), giveA = moved(pb, pa, eb);
    giveB(); giveA();
    pa.Mito = pa.Mito - A.mito + B.mito;
    pb.Mito = pb.Mito - B.mito + A.mito;
    for (const side of [A, B]) {
      side.s.save();
      side.s.send({ t: 'driver', driver: side.s.profile });
    }
    this.log(`trade ${trade.id}: ${A.s.name} <-> ${B.s.name} (${ea.length} + ${A.mito} Mito for ${eb.length} + ${B.mito} Mito)`);
    this.close(trade, 'done');
    return { ok: true };
  }

  view(side, other, trade) {
    const pub = x => ({ items: x.items, mito: x.mito, locked: x.locked, confirmed: x.confirmed });
    return { t: 'trade.state', id: trade.id, with: other.s.name, me: pub(side), them: pub(other) };
  }

  broadcast(trade) {
    trade.a.s.send(this.view(trade.a, trade.b, trade));
    trade.b.s.send(this.view(trade.b, trade.a, trade));
  }

  close(trade, reason, error) {
    if (!this.byId.has(trade.id)) return;
    this.byId.delete(trade.id);
    for (const side of [trade.a, trade.b]) {
      if (this.bySession.get(side.s) === trade) this.bySession.delete(side.s);
      side.s.send({ t: 'trade.closed', id: trade.id, reason, error: error || null, done: reason === 'done' });
    }
  }

  cancel(s, reason) {
    const trade = this.bySession.get(s);
    if (trade) this.close(trade, reason);
    for (const [k, inv] of this.invites) if (inv.from === s || inv.to === s) this.invites.delete(k);
  }

  /** Disconnect, channel change or another login: the trade ends. */
  leave(s) { this.cancel(s, 'left'); }

  active(s) { return this.bySession.has(s); }
}

module.exports = { Trades, MAX_SLOTS };
