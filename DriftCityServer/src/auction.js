// The Auction House (DriftCity_RE\SHOPS.md section 3): the client's AUCTION window (game.nui 1300000), its register
// dialog AUCTIONITEM (1760000) and the inventory's Auction page (3090000 All / Selling / Sold).
// What the client fixes [exact]: 13 rows a page (0x5990c0) and 10 page buttons, the category combo (All Items, Speed /
// Durability / Acceleration / Booster parts, General Items, 0x598930), a level range and a seller search, items or cars
// (CHK_ITEM / CHK_VEHICLE), a price up to 2,000,000,000 (0x5517ce) and a quantity, at most 60 listings per driver
// (0x5c9c62, 10440), a registration tax of 10% less the licence discount (ME_AuctionFeeDis / ME_FeeDis, 0x5c9e21,
// 10444), 7 days before an unsold item expires (10444), you can't buy your own listing (10499), the car rules
// 10577-10586 and AuctionMinLevel (globalcontent 1).
// The remake's own rules (the original server decided them): the listing's Mito and goods are held here (escrow);
// buy-out pays the seller by a note with the Mito attached (src/mail.js); a listing may also take bids (the API has
// start / bid; the client's window only shows the buy-out price): a bid holds the bidder's Mito, an outbid or cancelled
// bidder gets it back by note, and the best bid wins at expiry. Expired, won and cancelled goods wait in the listing
// until their owner takes them (auction.take, the inventory's Auction page). "Credit" is the seller's number of sales.
// NPC sellers keep a few listings of parts-shop goods up so an empty server still has an auction (opts.npc).
'use strict';
const goods = require('./goods');
const { cleanText } = require('./world');

const PAGE_ROWS = 13;
const MAX_LISTINGS = 60;
const MAX_PRICE = 2000000000;
const FEE_PCT = 10;
const DEFAULT_HOURS = 168;              // 7 days (10444)
const MIN_LEVEL = 1;                    // globalcontent AuctionMinLevel
const BID_STEP_PCT = 5;                 // remake rule: a new bid beats the old one by 5% (at least 1 Mito)
const NPC_SELLERS = ['Stacy', 'Eileen', 'Paul'];   // the auction house NPCs (globaltext B0/B1/B2_IntroName)
const PART_PRICE = [20000, 80000, 300000];         // parts shop (workshop.js SHOP_PRICE)
const PART_LEVEL = [1, 20, 45, 60];                // workshop.js REQ_LEVEL (+ U parts)
const NPC_STACKS = { i_n_00003: 5000, i_n_00003_plus: 10000, i_n_Seed_N: 10000, i_n_Seed_S: 40000, user_create_kit1: 3000, user_create_kit2: 6000, modipart_piece: 2000, i_n_00015: 1500, i_n_00016: 1500, i_n_00019: 2500 };
const isObj = x => !!x && typeof x === 'object' && !Array.isArray(x);
const fail = error => ({ ok: false, error });
const int = (x, d = 0) => (Number.isFinite(Number(x)) ? Math.trunc(Number(x)) : d);

class Auction {
  /** deps: { store, world, mail (src/mail.js Mail), log, clock, gamedata, rng, npc (NPC listing count, 0 = none) } */
  constructor(deps) {
    this.store = deps.store; this.world = deps.world; this.mail = deps.mail; this.log = deps.log || (() => {});
    this.clock = deps.clock || Date.now; this.gd = deps.gamedata || null; this.rng = deps.rng || Math.random;
    this.npc = deps.npc === undefined ? 12 : Math.max(0, deps.npc | 0);
    const db = this.store.db;
    db.exec(`
      CREATE TABLE IF NOT EXISTS auctions (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        seller_id INTEGER NOT NULL,            -- 0 = an NPC seller
        seller TEXT NOT NULL,
        goods TEXT NOT NULL,                   -- src/goods.js
        kind TEXT NOT NULL,                    -- stack | shop | part | car
        name TEXT NOT NULL,
        category INTEGER NOT NULL DEFAULT 5,   -- items: 1 speed, 2 durability, 3 accel, 4 booster parts, 5 general; cars: class 1-9
        level INTEGER NOT NULL DEFAULT 1,
        count INTEGER NOT NULL DEFAULT 1,
        price INTEGER NOT NULL DEFAULT 0,      -- buy-out (0 = bids only)
        start INTEGER NOT NULL DEFAULT 0,      -- opening bid (0 = buy-out only)
        bid INTEGER NOT NULL DEFAULT 0,
        bidder_id INTEGER NOT NULL DEFAULT 0,
        bidder TEXT NOT NULL DEFAULT '',
        fee INTEGER NOT NULL DEFAULT 0,
        created_at INTEGER NOT NULL,
        ends_at INTEGER NOT NULL,
        status TEXT NOT NULL DEFAULT 'open',   -- open | sold | expired | won | cancelled | taken
        holder_id INTEGER NOT NULL DEFAULT 0,  -- who may take the goods (expired / cancelled: seller, won: bidder)
        buyer TEXT NOT NULL DEFAULT '',
        sold_price INTEGER NOT NULL DEFAULT 0,
        closed_at INTEGER NOT NULL DEFAULT 0
      );
      CREATE INDEX IF NOT EXISTS auctions_status ON auctions(status, ends_at);
      CREATE INDEX IF NOT EXISTS auctions_seller ON auctions(seller_id);
    `);
    this.q = {
      get: db.prepare('SELECT * FROM auctions WHERE id = ?'),
      open: db.prepare("SELECT * FROM auctions WHERE status = 'open'"),
      due: db.prepare("SELECT * FROM auctions WHERE status = 'open' AND ends_at <= ?"),
      mine: db.prepare("SELECT * FROM auctions WHERE (seller_id = ? AND status IN ('open', 'sold', 'expired', 'cancelled', 'won')) OR (holder_id = ? AND status IN ('expired', 'cancelled', 'won')) ORDER BY id DESC LIMIT 200"),
      countOpen: db.prepare("SELECT COUNT(*) AS n FROM auctions WHERE seller_id = ? AND status = 'open'"),
      countNpc: db.prepare("SELECT COUNT(*) AS n FROM auctions WHERE seller_id = 0 AND status = 'open'"),
      credit: db.prepare("SELECT seller, COUNT(*) AS n FROM auctions WHERE status IN ('sold', 'won', 'taken') AND sold_price > 0 AND seller_id <> 0 GROUP BY seller"),
      insert: db.prepare('INSERT INTO auctions (seller_id, seller, goods, kind, name, category, level, count, price, start, fee, created_at, ends_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)'),
      setBid: db.prepare("UPDATE auctions SET bid = ?, bidder_id = ?, bidder = ? WHERE id = ? AND status = 'open'"),
      close: db.prepare("UPDATE auctions SET status = ?, holder_id = ?, buyer = ?, sold_price = ?, closed_at = ? WHERE id = ? AND status = 'open'"),
      taken: db.prepare("UPDATE auctions SET status = 'taken', holder_id = 0, closed_at = ? WHERE id = ? AND status IN ('expired', 'cancelled', 'won')"),
      dropNpc: db.prepare("DELETE FROM auctions WHERE seller_id = 0 AND status <> 'open'"),
    };
  }

  owns(t) { return typeof t === 'string' && t.startsWith('auction.'); }

  handle(session, m) {
    if (!isObj(m)) m = {};
    this.sweep();
    switch (m.t) {
      case 'auction.list': return this.list(session, m);
      case 'auction.mine': return this.mine(session);
      case 'auction.register': return this.register(session, m);
      case 'auction.buy': return this.buy(session, int(m.id));
      case 'auction.bid': return this.bid(session, int(m.id), int(m.amount));
      case 'auction.cancel': return this.cancel(session, int(m.id));
      case 'auction.take': return this.take(session, int(m.id));
    }
    return fail(`unknown message ${String(m.t).slice(0, 32)}`);
  }

  // ---------------- views ----------------
  static goodsOf(r) { try { return goods.normalize(JSON.parse(r.goods)); } catch { return null; } }

  view(r, credit) {
    const g = Auction.goodsOf(r);
    return {
      id: r.id, seller: r.seller, npc: r.seller_id === 0, kind: r.kind, name: r.name, category: r.category, level: r.level, count: r.count,
      price: r.price, start: r.start, bid: r.bid, bidder: r.bidder, fee: r.fee, createdAt: r.created_at, endsAt: r.ends_at,
      status: r.status, buyer: r.buyer, soldPrice: r.sold_price, closedAt: r.closed_at, credit: credit ? credit.get(r.seller) || 0 : 0, goods: g,
    };
  }

  credits() { const m = new Map(); for (const r of this.q.credit.all()) m.set(r.seller, r.n); return m; }

  /** AUCTION 1300000: the open listings of a page (13 rows), filtered like the window's search (0x598670). */
  list(session, m) {
    this.restock();
    const vehicles = !!m.vehicles;
    const category = Math.max(0, int(m.category));
    const from = Math.max(0, int(m.levelFrom)), to = int(m.levelTo) > 0 ? int(m.levelTo) : 0;
    const seller = typeof m.seller === 'string' ? m.seller.trim().toLowerCase().slice(0, 32) : '';
    let rows = this.q.open.all().filter(r => (r.kind === 'car') === vehicles);
    if (category > 0) rows = rows.filter(r => r.category === category);
    if (from > 0) rows = rows.filter(r => r.level >= from);
    if (to > 0) rows = rows.filter(r => r.level <= to);
    // USER_SEARCHTEXT reads "User ID/Item Name": the seller or the item's name
    if (seller) rows = rows.filter(r => r.seller.toLowerCase().includes(seller) || r.name.toLowerCase().includes(seller));
    const credit = this.credits();
    const key = { no: r => r.id, level: r => r.level, name: r => r.name.toLowerCase(), price: r => r.price || r.bid || r.start, count: r => r.count, user: r => r.seller.toLowerCase(), credit: r => credit.get(r.seller) || 0 }[m.sort] || (r => -r.id);
    const dir = m.desc ? -1 : 1;
    rows.sort((a, b) => { const x = key(a), y = key(b); return (x < y ? -1 : x > y ? 1 : 0) * dir || b.id - a.id; });
    const pages = Math.max(1, Math.ceil(rows.length / PAGE_ROWS));
    const page = Math.max(0, Math.min(pages - 1, int(m.page)));
    return { ok: true, page, pages, total: rows.length, rows: rows.slice(page * PAGE_ROWS, (page + 1) * PAGE_ROWS).map(r => this.view(r, credit)), now: this.clock() };
  }

  /** The inventory's Auction page: the driver's listings and the goods waiting for them. */
  mine(session) {
    const credit = this.credits();
    const rows = this.q.mine.all(session.accountId, session.accountId).map(r => this.view(r, credit));
    return { ok: true, rows, open: rows.filter(r => r.status === 'open').length, max: MAX_LISTINGS, now: this.clock() };
  }

  // ---------------- rules ----------------
  feePct(p) {
    let disc = 0;
    if (this.gd && p.MountedLicence >= 0 && Array.isArray(p.Licences) && p.Licences.includes(p.MountedLicence)) {
      const l = this.gd.licence(p.MountedLicence);
      if (l && (l.effect === 'ME_AuctionFeeDis' || l.effect === 'ME_FeeDis')) disc = Math.max(0, Math.min(100, l.value));
    } else if (p.MountedEffect && (p.MountedEffect.effect === 'ME_AuctionFeeDis' || p.MountedEffect.effect === 'ME_FeeDis')) disc = Math.max(0, Math.min(100, p.MountedEffect.value | 0));
    return FEE_PCT - FEE_PCT * disc / 100;   // 0x5c9e61: 10 - 10 * discount * 0.01
  }

  meta(g) {
    if (g.kind === 'car') {
      const c = this.gd && this.gd.car(g.car.VehicleIndex);
      return { name: goods.describe(g, this.gd), category: Math.max(1, Math.min(9, c ? c.grade || 1 : 1)), level: c ? c.reqLevel : 1 };
    }
    // the combo's order (10494-10497): Speed, Durability, Acceleration, Booster parts
    if (g.kind === 'part') return { name: goods.describe(g), category: [1, 3, 2, 4][g.type], level: PART_LEVEL[g.grade] };
    return { name: goods.describe(g), category: 5, level: 1 };
  }

  /** AUCTIONITEM OK (0x5c9c00): checks, tax, the goods into escrow. */
  register(session, m) {
    const p = session.profile;
    if ((p.Level | 0) < MIN_LEVEL) return fail(`Player with less than level ${MIN_LEVEL} cannot use Auction House`);   // 11828
    if (this.q.countOpen.get(session.accountId).n >= MAX_LISTINGS) return fail('You cannot register anymore items.');   // 10440
    const ref = isObj(m.item) ? m.item : null;
    if (!ref || !['stack', 'shop', 'part', 'car'].includes(ref.kind)) return fail('This item is not available for auction.');   // 10445
    const count = ref.kind === 'car' || ref.kind === 'part' ? 1 : int(m.count, int(ref.count, 1));
    if (count <= 0) return fail('Please enter a Quantity');   // 10441
    const price = int(m.price), start = Math.max(0, int(m.start));
    if (price <= 0 && start <= 0) return fail('Please enter a Price');   // 10442
    if (price < 0 || price > MAX_PRICE || start > MAX_PRICE) return fail('Price [Max. 2 billion Mito]');   // 10447
    if (price > 0 && start >= price) return fail('The opening bid must be lower than the buy-out price.');
    const hours = Math.max(1, Math.min(DEFAULT_HOURS, int(m.hours, DEFAULT_HOURS) || DEFAULT_HOURS));
    const fee = Math.floor((price || start) * this.feePct(p) / 100);
    if ((p.Mito | 0) < fee) return fail(ref.kind === 'car' ? 'You do not have enough Mito to pay the auction registration fee. The registration fee is 10% of the selling price.' : 'Insufficient Mito.');   // 10599 / 10500
    const out = goods.takeOut(p, ref.kind === 'car' ? { kind: 'car', slot: int(ref.slot) } : { ...ref, count });
    if (!out.ok) return out;
    p.Mito -= fee;
    const g = out.goods, meta = this.meta(g), now = this.clock();
    // the server has no item names: the client's label for a bag stack or shop item (display only)
    if ((g.kind === 'stack' || g.kind === 'shop') && typeof m.name === 'string' && m.name.trim()) meta.name = cleanText(m.name, 40) + (g.count > 1 ? ` x${g.count}` : '');
    const id = Number(this.q.insert.run(session.accountId, session.name, JSON.stringify(g), g.kind, meta.name, meta.category, meta.level, g.count || 1, price, start, fee, now, now + hours * 3600000).lastInsertRowid);
    session.save();
    this.log(`auction ${id}: ${session.name} lists ${meta.name} for ${price || '-'} (start ${start || '-'}), tax ${fee}`);
    return { ok: true, id, fee, name: meta.name, driver: p };
  }

  /** The window's Buy button (0x599ac0): the buy-out price, now. */
  buy(session, id) {
    const r = this.q.get.get(id);
    if (!r || r.status !== 'open') return fail('This item has already been sold.');
    if (r.seller_id === session.accountId) return fail('You cannot buy an Auction item that you registered.');   // 10499
    if (!(r.price > 0)) return fail('This item can only be bid on.');
    return this.settle(session, r, r.price);
  }

  settle(session, r, price) {
    const p = session.profile;
    const g = Auction.goodsOf(r);
    if (!g) return fail('This item is not available for auction.');
    if (g.kind === 'car' && (p.Level | 0) < r.level) return fail(`Cannot purchase because user level of the purchaser is too low for the car class. (V${r.category} Class ${r.level} Level or over can purchase)`);   // 10580
    if ((p.Mito | 0) < price) return fail('Insufficient Mito.');   // 10500
    const room = goods.room(p, g, this.gd);
    if (!room.ok) return room;
    if (this.q.close.run('sold', 0, session.name, price, this.clock(), r.id).changes !== 1) return fail('This item has already been sold.');
    p.Mito -= price;
    goods.give(p, g, this.gd);
    session.save();
    this.refundBidder(r, `The auction of ${r.name} was sold to another driver. Your bid of ${r.bid} Mito is returned.`);
    this.paySeller(r, price, session.name);
    this.log(`auction ${r.id}: ${session.name} buys ${r.name} for ${price}`);
    return { ok: true, id: r.id, price, name: r.name, driver: p };
  }

  /** Bids (API; the client's window has none): the bid is held until outbid, cancelled or won. */
  bid(session, id, amount) {
    const r = this.q.get.get(id);
    if (!r || r.status !== 'open') return fail('This item has already been sold.');
    if (r.seller_id === session.accountId) return fail('You cannot buy an Auction item that you registered.');
    if (!(r.start > 0)) return fail('This item can only be bought outright.');
    if (r.price > 0 && amount >= r.price) return this.buy(session, id);
    const min = r.bid > 0 ? r.bid + Math.max(1, Math.ceil(r.bid * BID_STEP_PCT / 100)) : r.start;
    if (amount < min) return fail(`The lowest bid is ${min} Mito.`);
    if (r.bidder_id === session.accountId) return fail('You already hold the highest bid.');
    const p = session.profile;
    if ((p.Mito | 0) < amount) return fail('Insufficient Mito.');
    const g = Auction.goodsOf(r);
    if (g && g.kind === 'car' && (p.Level | 0) < r.level) return fail(`Cannot purchase because user level of the purchaser is too low for the car class. (V${r.category} Class ${r.level} Level or over can purchase)`);
    if (this.q.setBid.run(amount, session.accountId, session.name, r.id).changes !== 1) return fail('This item has already been sold.');
    p.Mito -= amount;
    session.save();
    this.refundBidder(r, `You were outbid on ${r.name}. Your bid of ${r.bid} Mito is returned.`);
    this.log(`auction ${r.id}: ${session.name} bids ${amount}`);
    return { ok: true, id: r.id, bid: amount, driver: p };
  }

  /** The inventory page's cancel button: the goods come back (10118), the tax doesn't. */
  cancel(session, id) {
    const r = this.q.get.get(id);
    if (!r || r.seller_id !== session.accountId) return fail('no such listing');
    if (r.status !== 'open') return fail('This item has already been sold.');
    if (this.q.close.run('cancelled', session.accountId, '', 0, this.clock(), r.id).changes !== 1) return fail('This item has already been sold.');
    this.refundBidder(r, `The auction of ${r.name} was cancelled. Your bid of ${r.bid} Mito is returned.`);
    const t = this.take(session, r.id);
    this.log(`auction ${r.id}: cancelled by ${session.name}`);
    // a full bag keeps the goods in the listing until there is room (auction.take)
    return { ok: true, id: r.id, returned: t.ok, error: t.ok ? undefined : t.error, driver: session.profile };
  }

  /** Expired, cancelled or won goods into the bag / garage of the driver they wait for. */
  take(session, id) {
    const r = this.q.get.get(id);
    if (!r || !['expired', 'cancelled', 'won'].includes(r.status) || r.holder_id !== session.accountId) return fail('Nothing to take.');
    const g = Auction.goodsOf(r);
    const p = session.profile;
    const room = goods.room(p, g, this.gd);
    if (!room.ok) return room;
    if (this.q.taken.run(this.clock(), r.id).changes !== 1) return fail('Nothing to take.');
    goods.give(p, g, this.gd);
    session.save();
    return { ok: true, id: r.id, name: r.name, driver: p };
  }

  // ---------------- money by note ----------------
  paySeller(r, price, buyer) {
    if (!r.seller_id) return;
    this.mail.system(r.seller_id, `Your ${r.name} was sold to ${buyer} for ${price.toLocaleString('en-US')} Mito at the Auction House. Click the gift slot to take the Mito.`, price, 'Auction House');
    const s = this.world.findByAccount ? this.world.findByAccount(r.seller_id) : null;
    if (s) s.send({ t: 'auction.sold', id: r.id, name: r.name, price, buyer });
  }

  refundBidder(r, text) {
    if (!(r.bid > 0) || !r.bidder_id) return;
    this.mail.system(r.bidder_id, text, r.bid, 'Auction House');
    const s = this.world.findByAccount ? this.world.findByAccount(r.bidder_id) : null;
    if (s) s.send({ t: 'auction.outbid', id: r.id, name: r.name, refund: r.bid });
  }

  /** Listings past their end: the best bid wins, else the goods wait for the seller (expired). */
  sweep() {
    const now = this.clock();
    for (const r of this.q.due.all(now)) {
      if (r.seller_id === 0 && !(r.bid > 0)) { this.q.close.run('expired', 0, '', 0, now, r.id); continue; }
      if (r.bid > 0 && r.bidder_id) {
        if (this.q.close.run('won', r.bidder_id, r.bidder, r.bid, now, r.id).changes !== 1) continue;
        this.mail.system(r.bidder_id, `You won the auction of ${r.name} for ${r.bid.toLocaleString('en-US')} Mito. Take it from the Auction page of your inventory.`, 0, 'Auction House');
        this.paySeller(r, r.bid, r.bidder);
        continue;
      }
      if (this.q.close.run('expired', r.seller_id, '', 0, now, r.id).changes !== 1) continue;
      this.mail.system(r.seller_id, `Your auction of ${r.name} expired unsold. Take it back from the Auction page of your inventory.`, 0, 'Auction House');
    }
    this.q.dropNpc.run();
  }

  /** NPC sellers: parts-shop goods at the shop price give or take 30%, a day each. */
  restock() {
    if (!this.npc) return;
    let have = this.q.countNpc.get().n;
    const now = this.clock();
    while (have < this.npc) {
      let g, base;
      if (this.rng() < 0.55) {
        const grade = this.rng() < 0.6 ? 0 : this.rng() < 0.75 ? 1 : 2, enh = Math.floor(this.rng() * 6);
        g = { kind: 'part', type: Math.floor(this.rng() * 4), grade, enhance: enh, count: 1 };
        base = PART_PRICE[grade] * (1 + 0.15 * enh);
      } else {
        const ids = Object.keys(NPC_STACKS), id = ids[Math.floor(this.rng() * ids.length)], n = 1 + Math.floor(this.rng() * 10);
        g = { kind: 'stack', id, count: n };
        base = NPC_STACKS[id] * n;
      }
      const price = Math.max(100, Math.round(base * (0.7 + 0.6 * this.rng()) / 100) * 100);
      const meta = this.meta(g), seller = NPC_SELLERS[Math.floor(this.rng() * NPC_SELLERS.length)];
      this.q.insert.run(0, seller, JSON.stringify(g), g.kind, meta.name, meta.category, meta.level, g.count, price, 0, 0, now, now + 86400000);
      have++;
    }
  }
}

module.exports = { Auction, PAGE_ROWS, MAX_LISTINGS, MAX_PRICE, FEE_PCT, DEFAULT_HOURS };
