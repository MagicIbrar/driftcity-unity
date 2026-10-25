// Events, gacha, lotto, coupons (Unity: Game/Events/*, DriftCity_RE\EVENTS.md). The original client asked its server for
// every draw, cost and limit; these are this rebuild's rules and mirror the offline twins in the Unity code:
//  - reward pools: init\rewardgroup.xlt drawn with one rule (draw()): a pool whose rows are all at 100 gives every row
//    (a pool of sub-pools with rising Etc_Value is a level band), any other pool gives one row weighted by Probability;
//  - no real money: capsules cost Mito (the Coin tab's G-Coin price x MITO_PER_COIN), scratch cards and boxes cost Mito,
//    event items come from play (tick()), tokens from the daily mission;
//  - server-wide state (scratch-card gauge, Christmas trees, harvest receipts) lives in the event_state table.
'use strict';
const fs = require('node:fs');
const path = require('node:path');
const { XltTable, DEFAULT_DIR } = require('./gamedata');
const { tdfRows, addExp, newCar } = require('./economy');
const { give, count, ensureItems } = require('./items');

const MITO_PER_COIN = 1000;
const GACHA_PRICE = { carCash: 100, carMito: 20000, partsCash: 100, partsMito: 20000 };   // globalcontent Gacha_Price_*
const LOTTO_GOALS = [50, 150, 500];
const LOTTO_GOAL_EXP = [5, 10, 30];
const MAX_DROPS_PER_DAY = 10;
const DAILY = { huvs: 5, races: 3 };
const LIMITS = { gacha: 100, lottoBuy: 20, slot: 100 };            // per driver per UTC day
const COOLDOWN_MS = { 'gacha.buy': 1500, 'gacha.slot': 1500, 'lotto.scratch': 1500, 'event.box': 300, 'lotto.buy': 200, 'xmas.donate': 400 };
// most a coupon counter may grow per second of play (counts; seconds for driving / reckless time)
const COUPON_RATE = [1 / 30, 1 / 30, 1 / 8, 10, 1.05, 2, 2, 1, 1.05, 1 / 90];
const XMAS_CITIES = ['cras', 'moonpalace', 'koinonia', 'oros'];
const CITY_SHORT = { cras: 'cras', moonpalace: 'moon', koinonia: 'koi', oros: 'oros' };
const SEASONS = ['christmas', 'valentine', 'halloween', 'harvest', 'summer', 'anniversary', 'indy4'];   // indy4: the props NPC event (only by list)
// GACHAINVEN 4580000: the Gotcha car of each season (the CARNAME01-08 labels: 1 Shadow, 2 Hunter, 3-4 Fate, 5 Shadow,
// 6 Hunter, 7 Fate, 8 Monza; the key items add Winner _09), its Vehicle Parts A-D and Key Parts (remake pools: the
// client data has no rw_gacha_car_*_parts / _key pools)
const GACHA_CARS = ['shadow', 'shadow', 'hunter', 'fate', 'fate', 'shadow', 'hunter', 'fate', 'monza', 'winner'];
const GACHA_KEY_SUFFIX = { shadow: '05', hunter: '06', fate: '07', monza: '08', winner: '09' };
const GACHA_PART_CHANCE = 1 / 8, GACHA_KEY_CHANCE = 1 / 40;   // remake rule: a capsule may add a part / key parts
// Pick'em (VIEWING 1980000): symbols per finishing place, 1 gold 3 / 2 silver 2 / 3 bronze 1 points, 0 bomb;
// the Slot Ticket grade = 1 + the points of the three picks' places (max 10)
const PICKEM_POINTS = [0, 3, 2, 1];
const BASKET_GIFT = { g_make_giftbox: 'g_get_giftbox', h_make_giftbox: 'h_get_giftbox', s_make_giftbox: 's_get_giftbox' };
const GIFT_FEE = 50;                       // the note fee (mail.js COST_PER_RECEIVER), 0x5cf's fee
const PROPS = ['i_n_00051', 'i_n_00052', 'i_n_00053'];   // Indy's fedora, whip, crystal skull (0x6fcc00)
const PROPS_REWARD = { item: 'i_n_00054', mito: 5000 };   // remake rule: the raffle ticket + Mito, once a day
const DRINKS = { i_n_halloween: 200, i_n_buff: 150 };      // buff_item drinks: the Special Drink effect (remake values)
const DRINK_SECONDS = 1800;
const HARVEST_STEPS = [1000000, 10000000, 50000000, 100000000];   // globalcontent GHEvent_MAXRECEIPT_LV1..4
const HARVEST_STEP_SCALE = 1000;
const BOX_INGREDIENTS = [['event_vegetable_n', 12], ['event_fish_n', 6], ['event_fruit_n', 4], ['event_meat_n', 3]];
const DIST_BOXES = ['eventbox_result_03', 'eventbox_result_02', 'eventbox_result_01'];
const BASKETS = { g_choco_recipe: [10, 0, 0], h_choco_recipe: [3, 7, 0], s_choco_recipe: [3, 3, 4] };
const BOX_TYPES = ['giftbox', 'giftbox_lv', 'giftbox_car', 'luckybag', 'chocolet_item'];

const fail = error => ({ ok: false, error });
const isObj = x => !!x && typeof x === 'object' && !Array.isArray(x);

/** Seasons running on a day (UTC): the Unity SeasonEvents.ForDate calendar. */
function seasonsForDate(d) {
  const md = (d.getUTCMonth() + 1) * 100 + d.getUTCDate();
  const s = [];
  if (md >= 1201 || md <= 106) s.push('christmas');
  if (md >= 201 && md <= 430) s.push('valentine');
  if (md >= 701 && md <= 831) s.push('summer');
  if (md >= 915 && md <= 1115) s.push('harvest');
  if (md >= 1015 && md <= 1107) s.push('halloween');
  if (md >= 601 && md <= 614) s.push('anniversary');
  return s;
}

function parseSeasons(text, now) {
  const t = String(text || 'auto').trim().toLowerCase();
  if (t === 'auto' || t === '') return seasonsForDate(new Date(now));
  if (t.startsWith('date=')) { const d = new Date(t.slice(5) + 'T12:00:00Z'); return isNaN(d) ? seasonsForDate(new Date(now)) : seasonsForDate(d); }
  if (t === 'all') return SEASONS.slice();
  if (t === 'none') return [];
  const alias = { xmas: 'christmas', easter: 'valentine', anni: 'anniversary' };
  return [...new Set(t.split(/[\s,;|]+/).map(w => alias[w] || w).filter(w => SEASONS.includes(w)))];
}

// ---------------- tables ----------------

function readFirst(dir, rels) {
  for (const rel of rels) {
    const f = path.join(dir, ...rel.split('/'));
    try { if (fs.existsSync(f)) return fs.readFileSync(f); } catch { /* next */ }
  }
  return null;
}

const num = (s, f = 0) => { const v = Number(String(s ?? '').trim()); return Number.isFinite(v) ? v : f; };

class Tables {
  constructor(dir) {
    this.dir = dir;
    this.pools = new Map();     // lower-case group id -> rows
    this.items = new Map();     // item id -> { id, type, name, pool, col4, col5, price, value }
    this.coupons = [];
    this.tokens = [];
    const rg = readFirst(dir, ['init/rewardgroup.xlt']);
    if (rg) {
      const t = new XltTable(rg);
      for (const r of t.rows) {
        const row = { group: t.get(r, 'GroupId').trim(), type: t.get(r, 'Reward_Type').trim(), item: t.get(r, 'ItemId').trim(), period: num(t.get(r, 'Reward_Period')),
          count: num(t.get(r, 'Reward_Count')), etc: num(t.get(r, 'Etc_Value')), etcStr: t.get(r, 'Etc_Str').trim(), p: num(t.get(r, 'Probability')) };
        if (!row.group) continue;
        const k = row.group.toLowerCase();
        if (!this.pools.has(k)) this.pools.set(k, []);
        this.pools.get(k).push(row);
      }
    }
    for (const rel of [['data', 'world', 'data', 'useitemclient.tdf'], ['world', 'data', 'useitemclient.tdf'], ['data', 'useitemclient.tdf']]) {
      const f = path.join(dir, ...rel);
      if (!fs.existsSync(f)) continue;
      const clean = s => (String(s || '').trim() === 'n/a' ? '' : String(s || '').trim());
      for (const r of tdfRows(fs.readFileSync(f))) {
        if (r.length < 10 || !r[0].trim()) continue;
        const id = r[0].trim();
        if (!this.items.has(id)) this.items.set(id, { id, type: r[1].trim(), name: clean(r[2]), pool: clean(r[3]), col4: clean(r[4]), col5: clean(r[5]), price: num(r[7]), sell: num(r[8]), value: num(r[9]) });
      }
      break;
    }
    const cp = readFirst(dir, ['data/init/coupon.xlt', 'init/coupon.xlt']);
    if (cp) {
      const t = new XltTable(cp);
      for (const r of t.rows) {
        if (r.length < 11 || !/^\d+$/.test(String(r[9]).trim()) || !/^\d+$/.test(String(r[1]).trim())) continue;
        const c = { cat: num(r[9]), idx: num(r[1]), display: String(r[2]).trim() === '1', count: num(r[3], -1), minutes: num(r[4], -1), car: String(r[5]).trim(), carId: num(r[6]), item: String(r[7]).trim(), grade: num(r[8]) };
        if (c.count < 0 && c.minutes < 0) continue;
        c.need = c.count >= 0 ? c.count : c.minutes;
        this.coupons.push(c);
      }
    }
    const tb = readFirst(dir, ['data/init/tokenbook.xlt', 'init/tokenbook.xlt']);
    if (tb) {
      const t = new XltTable(tb);
      for (const r of t.rows) {
        if (r.length < 8 || !/^\d+$/.test(String(r[0]).trim())) continue;
        const row = { currency: String(r[2]).trim(), count: num(r[3]), gifts: [] };
        for (let c = 5; c + 2 < r.length; c += 3) {
          const id = String(r[c + 1]).trim(), n = num(r[c + 2]);
          if (!id || id === '0' || /^reserved$/i.test(id) || n <= 0) continue;
          row.gifts.push([id, n]);
        }
        if (row.count > 0 && row.currency) this.tokens.push(row);
      }
    }
  }

  pool(id) { return this.pools.get(String(id || '').trim().toLowerCase()) || []; }
  hasPool(id) { return this.pool(id).length > 0; }
  item(id) { return this.items.get(String(id || '').trim()) || null; }
  type(id) { const i = this.item(id); return i ? i.type : ''; }

  /** RewardGroups.Draw (Core/RewardGroups.cs). */
  draw(id, level, rng, out = [], depth = 0) {
    if (depth > 8) return out;
    const live = this.pool(id).filter(r => r.p > 0);
    if (!live.length) return out;
    if (live.every(r => r.p >= 100)) {
      const band = live.length >= 2 && live.every((r, i) => /^rewardgroup$/i.test(r.type) && r.etc > 0 && (i === 0 || r.etc > live[i - 1].etc));
      if (band) { this.resolve(live.find(r => r.etc >= level) || live[live.length - 1], level, rng, out, depth); return out; }
      for (const r of live) this.resolve(r, level, rng, out, depth);
      return out;
    }
    const total = live.reduce((a, r) => a + r.p, 0);
    let roll = rng() * total;
    for (const r of live) { roll -= r.p; if (roll < 0) { this.resolve(r, level, rng, out, depth); return out; } }
    this.resolve(live[live.length - 1], level, rng, out, depth);
    return out;
  }

  resolve(r, level, rng, out, depth) {
    const t = r.type.toLowerCase();
    if (t === 'rewardgroup') this.draw(r.item, level, rng, out, depth + 1);
    else if (t === 'normalitem' || t === 'skiditem') {
      if (!r.item || r.item === '0') return;
      if (r.item.toLowerCase() === 'lotto_00001') out.push({ kind: 'bust', item: r.item, count: 0 });
      else out.push({ kind: 'item', item: r.item, count: Math.max(1, r.count), etc: r.etc, days: r.period });
    } else if (t === 'exp') { if (r.count > 0) out.push({ kind: 'exp', count: r.count }); }
    else if (t === 'mito') { if (r.count > 0) out.push({ kind: 'mito', count: r.count }); }
    else if (r.item.toLowerCase() === 'lotto_00001') out.push({ kind: 'bust', item: r.item, count: 0 });
  }

  /** Everything a pool can give (the gacha shop's list). */
  contents(id, depth = 0, out = []) {
    for (const r of this.pool(id)) {
      if (/^rewardgroup$/i.test(r.type)) { if (depth < 6) this.contents(r.item, depth + 1, out); }
      else if (r.p > 0 && /item$/i.test(r.type) && !out.includes(r.item)) out.push(r.item);
    }
    return out;
  }

  pieceSet(item) {
    const row = this.item(item);
    if (!row) return null;
    if (row.type === 'piece' && row.col4 && Number(row.col5) > 0) return { result: row.col4, needs: [[item, Number(row.col5)]] };
    if (row.type === 'reward_piece' && this.hasPool(row.col4)) {
      const rows = this.pool(row.col4);
      const result = rows.map(r => r.etcStr).find(s => s && s !== 'n/a');
      if (!result) return null;
      const needs = [];
      for (const r of rows) if (!needs.some(n => n[0] === r.item)) needs.push([r.item, Math.max(1, r.count)]);
      return { result, needs };
    }
    return null;
  }
}

const tableCache = new Map();
function loadTables(dir) {
  dir = path.resolve(dir);
  if (!tableCache.has(dir)) tableCache.set(dir, new Tables(dir));
  return tableCache.get(dir);
}

// ---------------- per-driver state (PlayerProfile.Events, Game/Events/EventData.cs EventState) ----------------

function ensureEvents(p) {
  ensureItems(p);
  if (!isObj(p.Events)) p.Events = {};
  const e = p.Events;
  if (!Array.isArray(e.Coupon) || e.Coupon.length !== 10) e.Coupon = Array.from({ length: 10 }, (_, i) => (Array.isArray(e.Coupon) && Number.isFinite(e.Coupon[i]) ? e.Coupon[i] : 0));
  for (const k of ['CouponClaimed', 'CouponSeen']) if (!Array.isArray(e[k])) e[k] = [];
  for (const k of ['Day', 'LottoDay', 'AttendDay', 'PropsDay']) if (typeof e[k] !== 'string') e[k] = '';
  for (const k of ['DayTokens', 'DayDrops', 'LottoCards', 'HarvestReceipts', 'DayGacha', 'DayLottoBuy', 'DaySlot']) if (!Number.isFinite(e[k])) e[k] = 0;
  for (const k of ['DayHuvBase', 'DayRaceBase', 'SeenHuv', 'SeenRaces']) if (!Number.isFinite(e[k])) e[k] = -1;
  if (typeof e.DayMission !== 'boolean') e.DayMission = false;
  if (!Array.isArray(e.XmasMask) || e.XmasMask.length !== 4) e.XmasMask = [0, 0, 0, 0];
  return e;
}

class Events {
  /** opts: store, log, clock, rng, dir (game data), seasons (string, else env DC_EVENTS / auto), gamedata (cars for car keys), codes */
  constructor(opts = {}) {
    this.store = opts.store;
    this.log = opts.log || (() => {});
    this.clock = opts.clock || Date.now;
    this.rng = opts.rng || Math.random;
    this.gd = opts.gamedata || null;
    this.t = loadTables(opts.dir || process.env.DC_GAMEDATA || DEFAULT_DIR);
    this.seasonText = opts.seasons !== undefined ? opts.seasons : (process.env.DC_EVENTS || 'auto');
    this.codes = parseCodes(opts.codes !== undefined ? opts.codes : process.env.DC_COUPON_CODES);
    this.handlers = {
      'event.state': (s) => this.state(),
      'event.box': (s, m) => this.openBox(s.profile, m.item),
      'event.tick': (s, m) => this.tick(s.profile, m.city),
      'event.basket': (s, m) => this.basket(s.profile, m.recipe),
      'event.combine': (s, m) => this.combine(s.profile, m.item),
      'lotto.buy': (s, m) => this.lottoBuy(s.profile, m.item),
      'lotto.scratch': (s, m) => this.scratch(s.profile, m.item),
      'gacha.buy': (s, m) => this.gachaBuy(s.profile, m.mode),
      'gacha.slot': (s, m) => this.slot(s.profile, m.item),
      'coupon.sync': (s, m) => this.couponSync(s, m.add),
      'coupon.claim': (s, m) => this.couponClaim(s.profile, m.cat, m.idx),
      'coupon.seen': (s, m) => this.couponSeen(s.profile, m.cat, m.idx),
      'coupon.code': (s, m) => this.couponCode(s.profile, m.code),
      'token.exchange': (s, m) => this.tokenExchange(s.profile, m.row),
      'token.daily': (s) => this.tokenDaily(s.profile),
      'xmas.donate': (s, m) => this.xmasDonate(s.profile, m.city, m.slot),
      'harvest.craft': (s, m) => this.harvestCraft(s.profile, m.ice === true),
      'harvest.buybox': (s, m) => this.harvestBuyBox(s.profile, m.ice === true),
      'harvest.deliver': (s) => this.harvestDeliver(s.profile),
      'harvest.exchange': (s) => this.harvestExchange(s.profile),
      'gacha.compound': (s, m) => this.gachaCompound(s.profile, m.item),
      'gacha.sell': (s, m) => this.gachaSell(s.profile, m.item, m.count),
      'pickem.start': (s, m) => this.pickemStart(s, m),
      'pickem.pick': (s, m) => this.pickemPick(s, m),
      'pickem.finish': (s, m) => this.pickemFinish(s, m),
      'valen.check': (s, m) => this.valenCheck(s, m.name),
      'valen.gift': (s, m) => this.valenGift(s, m),
      'props.find': (s, m) => this.propsFind(s.profile, m.idx),
      'props.claim': (s) => this.propsClaim(s.profile),
      'event.drink': (s, m) => this.drink(s, m.item),
    };
  }

  owns(t) { return Object.hasOwn(this.handlers, t); }

  /** Runs a message: a reply object with the driver when something changed (server.js saves the driver then). */
  handle(session, m) {
    const p = session.profile;
    if (!p) return fail('no driver');
    ensureEvents(p);
    const now = this.clock();
    session.eventAt = session.eventAt || {};
    const cd = COOLDOWN_MS[m.t] || 0;
    if (cd && now - (session.eventAt[m.t] || 0) < cd) return fail('too soon');
    let r;
    try { r = this.handlers[m.t](session, m) || fail('not available'); }
    catch (err) { this.log(`events ${m.t}: ${err.stack || err.message}`); r = fail('the event server had a problem'); }
    if (r.ok) session.eventAt[m.t] = now;
    return { ...r, driver: p };
  }

  // ---------------- shared state ----------------

  get today() { return new Date(this.clock()).toISOString().slice(0, 10); }
  seasons() { return parseSeasons(this.seasonText, this.clock()); }
  is(season) { return this.seasons().includes(season); }
  shared(key, fallback) { return this.store ? this.store.eventGet(key, fallback) : (this.mem = this.mem || {}, key in this.mem ? this.mem[key] : fallback); }
  setShared(key, value) { if (this.store) this.store.eventSet(key, value); else { this.mem = this.mem || {}; this.mem[key] = value; } }
  lottoCards() { const g = this.shared('lotto', { day: '', cards: 0 }); return g.day === this.today ? g.cards : 0; }
  xmasMasks() { const m = this.shared('xmas', [0, 0, 0, 0]); return Array.isArray(m) && m.length === 4 ? m : [0, 0, 0, 0]; }
  harvestReceipts() { return Number(this.shared('harvest', 0)) || 0; }
  gachaSeason() { const d = new Date(this.clock()); return ((d.getUTCFullYear() * 12 + d.getUTCMonth()) % 11) + 1; }

  /** The Battle Zone EXP bonus the scratch-card gauge has unlocked today (hook for race rewards). */
  battleExpBonusPct() { const n = this.lottoCards(); let pct = 0; LOTTO_GOALS.forEach((g, i) => { if (n >= g) pct = LOTTO_GOAL_EXP[i]; }); return pct; }
  /** The Christmas trees' EXP / Mito bonus (30/40/50/60 % for 1-4 finished cities; hook for rewards). */
  xmasBonusPct() { if (!this.is('christmas')) return 0; const n = this.xmasMasks().filter(m => (m & 0x3ff) === 0x3ff).length; return n ? 20 + 10 * n : 0; }

  state() {
    return { ok: true, seasons: this.seasons(), lottoCards: this.lottoCards(), lottoGoals: LOTTO_GOALS, xmas: this.xmasMasks(),
      harvestReceipts: this.harvestReceipts(), gachaSeason: this.gachaSeason(), battleExpBonus: this.battleExpBonusPct(), xmasBonus: this.xmasBonusPct(),
      multipliers: this.calendar === false ? null : require('./multipliers').state(this.seasons(), new Date(this.clock()), this.goldenHours === undefined ? undefined : this.goldenHours) };
  }

  // ---------------- grants ----------------

  carForKey(id) {
    if (!this.gd || !/^pc_\d{4}/i.test(String(id || ''))) return null;
    const stem = String(id).slice(0, 7).toLowerCase();
    return this.gd.carList.find(v => v.modelBase.startsWith(stem)) || null;
  }

  /** EventNet.Apply: items as stacks, car keys as cars (a key for a car already owned stays an item), EXP, Mito. */
  grant(p, grants) {
    let levelUps = 0;
    for (const g of grants) {
      if (g.kind === 'exp') levelUps += addExp(p, g.count);
      else if (g.kind === 'mito') p.Mito += g.count;
      else if (g.kind === 'item') {
        const car = this.carForKey(g.item);
        if (car && !p.Cars.some(c => c.VehicleIndex === car.index) && p.Cars.length < 40) {
          const c = newCar(car.index, 0, 0); c.Grade = Math.max(0, (g.etc | 0) - 1); p.Cars.push(c);
        } else if (g.days > 0) require('./items').giveTimed(p, g.item, Math.max(1, g.count | 0), g.days * 24);   // a reward with a period
        else give(p, g.item, Math.max(1, g.count | 0));
      }
    }
    return levelUps;
  }

  take(p, id, n = 1) { if (typeof id !== 'string' || count(p, id) < n) return false; give(p, id, -n); return true; }

  dayRoll(p) {
    const e = ensureEvents(p);
    const races = (p.RacesEntered | 0) + (p.PvpRaces | 0);
    if (e.SeenHuv < 0) { e.SeenHuv = p.HuvCaught | 0; e.SeenRaces = races; }
    if (e.Day === this.today) return e;
    Object.assign(e, { Day: this.today, DayDrops: 0, DayTokens: 0, DayMission: false, DayGacha: 0, DayLottoBuy: 0, DaySlot: 0, DayHuvBase: p.HuvCaught | 0, DayRaceBase: races });
    return e;
  }

  // ---------------- boxes, scratch cards, capsules, slot ----------------

  openBox(p, item) {
    const it = this.t.item(item);
    if (!it || !BOX_TYPES.includes(it.type) || !this.t.hasPool(it.pool)) return fail("This item can't be opened.");
    if (it.value > p.Level) return fail('Your current level is not high enough.');
    if (!this.take(p, item)) return fail("You don't have that item.");
    const grants = this.t.draw(it.pool, p.Level, this.rng);
    const levelUps = this.grant(p, grants);
    this.log(`${p.Name} opened ${item}: ${grants.map(g => g.item || g.kind).join(', ')}`);
    return { ok: true, grants, levelUps };
  }

  lottoBuy(p, item) {
    const it = this.t.item(item);
    if (!it || it.type !== 'lotto' || !this.t.hasPool(it.pool)) return fail("That isn't a scratch card.");
    const e = this.dayRoll(p);
    if (e.DayLottoBuy >= LIMITS.lottoBuy) return fail(`You can buy ${LIMITS.lottoBuy} scratch cards a day.`);
    const price = Math.max(1, it.price || 500) * 100;
    if (p.Mito < price) return fail('You do not have enough Mito for this transaction.');
    p.Mito -= price; give(p, item, 1); e.DayLottoBuy++;
    return { ok: true, price };
  }

  scratch(p, item) {
    const it = this.t.item(item);
    if (!it || it.type !== 'lotto') return fail("That isn't a scratch card.");
    if (!this.t.hasPool(it.pool)) return fail("This card can't be scratched.");
    if (!this.take(p, item)) return fail("You don't have that card.");
    const grants = this.t.draw(it.pool, p.Level, this.rng);
    const levelUps = this.grant(p, grants);
    const g = this.shared('lotto', { day: '', cards: 0 });
    const day = this.today;
    this.setShared('lotto', { day, cards: (g.day === day ? g.cards : 0) + 1 });
    return { ok: true, grants, levelUps, lottoCards: this.lottoCards() };
  }

  gachaModes() { return 2; }   // globalcontent PartsGotchaClose = 1: the two car modes

  gachaPool(mode) {
    const cash = mode === 0 || mode === 2;
    let id = mode < 2 ? `rw_gacha_car_${cash ? 'cash' : 'mito'}_${String(this.gachaSeason()).padStart(2, '0')}` : `rw_gacha_parts_${cash ? 'cash' : 'mito'}_99`;
    if (!this.t.hasPool(id)) id = `rw_gacha_item_${cash ? 'cash' : 'mito'}_99`;
    return id;
  }

  gachaPrice(mode) {
    const cash = mode === 0 || mode === 2;
    const v = mode < 2 ? (cash ? GACHA_PRICE.carCash : GACHA_PRICE.carMito) : (cash ? GACHA_PRICE.partsCash : GACHA_PRICE.partsMito);
    return cash ? v * MITO_PER_COIN : v;
  }

  gachaBuy(p, mode) {
    if (!Number.isInteger(mode) || mode < 0 || mode >= this.gachaModes()) return fail('That capsule machine is closed.');
    const e = this.dayRoll(p);
    if (e.DayGacha >= LIMITS.gacha) return fail(`The capsule machine takes ${LIMITS.gacha} capsules a day.`);
    const price = this.gachaPrice(mode);
    if (p.Mito < price) return fail('You do not have enough Mito for this transaction.');
    const pool = this.gachaPool(mode);
    if (!this.t.hasPool(pool)) return fail('The capsule machine is empty.');
    p.Mito -= price; e.DayGacha++;
    const grants = this.t.draw(pool, p.Level, this.rng).slice(0, 1);
    grants.push(...this.gachaBonus());
    this.grant(p, grants);
    this.log(`${p.Name} gacha ${pool}: ${grants.map(g => g.item).join(', ')}`);
    return { ok: true, grants, price, pool };
  }

  slot(p, item) {
    const m = /^gacha_observe_v(\d+)$/i.exec(String(item || ''));
    const grade = m ? Number(m[1]) : 0;
    if (grade < 1 || grade > 10) return fail("That isn't a slot ticket.");
    const e = this.dayRoll(p);
    if (e.DaySlot >= LIMITS.slot) return fail('The slot machine is closed for today.');
    if (!this.take(p, item)) return fail("You don't have that ticket.");
    e.DaySlot++;
    const symbols = [0, 0, 0].map(() => Math.floor(this.rng() * 4));
    const grants = this.t.draw(`rw_gacha_ob_v${grade}`, p.Level, this.rng).slice(0, 1);
    const exp = 100 * grade, mito = slotMito(symbols, grade);
    grants.push({ kind: 'exp', count: exp });
    if (mito > 0) grants.push({ kind: 'mito', count: mito });
    const levelUps = this.grant(p, grants);
    return { ok: true, symbols, exp, mito, grants, levelUps };
  }

  // ---------------- coupons ----------------

  couponSync(session, add) {
    const p = session.profile, e = ensureEvents(p);
    const now = this.clock();
    const last = session.couponSyncAt || now - 60000;
    const seconds = Math.max(0, Math.min(600, (now - last) / 1000));
    session.couponSyncAt = now;
    if (Array.isArray(add)) {
      for (let i = 0; i < 10; i++) {
        const v = Number(add[i]);
        if (!Number.isFinite(v) || v <= 0) continue;
        e.Coupon[i] += Math.min(v, COUPON_RATE[i] * seconds + (i === 3 ? 10 : 1));
      }
    }
    return { ok: true, counters: e.Coupon };
  }

  couponCurrent(p, cat) { const v = ensureEvents(p).Coupon[cat] || 0; return cat === 4 || cat === 8 ? Math.floor(v / 60) : Math.floor(v); }

  couponClaim(p, cat, idx) {
    const c = this.t.coupons.find(x => x.cat === cat && x.idx === idx);
    if (!c || !c.display) return fail('No coupons available.');
    const e = ensureEvents(p), key = `${cat}:${idx}`;
    if (e.CouponClaimed.includes(key)) return fail('Failed to obtain the coupon.');
    if (this.couponCurrent(p, cat) < c.need) return fail('Failed to obtain the coupon.');
    e.CouponClaimed.push(key);
    if (!e.CouponSeen.includes(key)) e.CouponSeen.push(key);
    const grants = [{ kind: 'item', item: c.item, count: 1 }];
    this.grant(p, grants);
    return { ok: true, grants };
  }

  couponSeen(p, cat, idx) {
    const e = ensureEvents(p), key = `${cat | 0}:${idx | 0}`;
    if (!e.CouponSeen.includes(key)) e.CouponSeen.push(key);
    return { ok: true };
  }

  couponCode(p, code) {
    code = String(code || '').trim().toUpperCase();
    const e = ensureEvents(p), used = 'code:' + code;
    const g = this.codes.get(code);
    if (!code || code.length > 10 || !g || e.CouponSeen.includes(used)) return fail('This coupon code does not exist or has already been used.');
    e.CouponSeen.push(used);
    const grants = [{ kind: 'item', item: g.item, count: g.count }];
    this.grant(p, grants);
    return { ok: true, grants };
  }

  // ---------------- tokens ----------------

  tokenExchange(p, row) {
    const r = Number.isInteger(row) ? this.t.tokens[row] : null;
    if (!r) return fail('No such exchange.');
    if (count(p, r.currency) < r.count) return fail(`You need ${r.count} ${r.currency}.`);
    give(p, r.currency, -r.count);
    const grants = r.gifts.map(([item, n]) => ({ kind: 'item', item, count: n }));
    this.grant(p, grants);
    return { ok: true, grants };
  }

  tokenDaily(p) {
    const e = this.dayRoll(p);
    if (e.DayMission) return fail('already');
    const huvs = (p.HuvCaught | 0) - e.DayHuvBase, races = (p.RacesEntered | 0) + (p.PvpRaces | 0) - e.DayRaceBase;
    if (huvs < DAILY.huvs && races < DAILY.races) return fail('ing');
    e.DayMission = true; e.DayTokens++;
    const grants = [{ kind: 'item', item: 'i_n_00049', count: 1 }];
    this.grant(p, grants);
    return { ok: true, grants };
  }

  // ---------------- Christmas trees ----------------

  xmasDonate(p, city, slot) {
    const c = XMAS_CITIES.indexOf(String(city || '').toLowerCase());
    if (!this.is('christmas')) return fail("The Christmas event isn't running.");
    if (c < 0 || !Number.isInteger(slot) || slot < 0 || slot > 9) return fail("There's no tree here.");
    const masks = this.xmasMasks();
    const mask = masks[c] | 0;
    if (mask & (1 << slot)) return fail('That decoration is already on the tree.');
    if (slot === 9 && bits(mask & 0x1ff) < 9) return fail('You can donate Star Tree Topper only after 9 other decorations are added to the tree.');
    if (!this.take(p, 'i_n_00088')) return fail('You need a Santa Gift Box.');
    masks[c] = mask | (1 << slot);
    this.setShared('xmas', masks);
    const grants = [{ kind: 'item', item: 'i_n_battleEXP_25', count: 1 }];
    this.grant(p, grants);
    const completed = masks[c] === 0x3ff;
    if (completed) this.log(`${p.Name} completed the ${XMAS_CITIES[c]} Christmas tree`);
    // 20357 to everyone, then the city's 11440-11443 "<city> Christmas Tree is now fully decorated" marquee (HANDLERS.md section 3)
    if (completed && this.notices) {
      this.notices.broadcast('chat', { key: '20357', args: [p.Name], text: `Congratulations! ${p.Name} has completed decorating the Christmas tree.` });
      this.notices.broadcast('scroll', { key: { moonpalace: '11440', koinonia: '11441', cras: '11442', oros: '11443' }[XMAS_CITIES[c]] });
    }
    return { ok: true, grants, completed, xmas: masks };
  }

  // ---------------- harvest ----------------

  harvestCraft(p, ice) {
    if (count(p, 'event_box_recipe_n') <= 0) return fail('You need the Box Recipe.');
    const box = ice ? 'event_icebox_n' : 'event_normalbox_n';
    if (count(p, box) <= 0) return fail(`You need a ${box}.`);
    for (const [id, n] of BOX_INGREDIENTS) if (count(p, id) < n) return fail(`Not enough ${id} (${count(p, id)}/${n}).`);
    for (const [id, n] of BOX_INGREDIENTS) give(p, id, -n);
    give(p, box, -1);
    const grants = this.t.draw(ice ? 'box_recipe_ice_result' : 'box_recipe_normal_result', p.Level, this.rng).slice(0, 1);
    this.grant(p, grants);
    return { ok: true, grants };
  }

  harvestBuyBox(p, ice) {
    const box = ice ? 'event_icebox_n' : 'event_normalbox_n';
    const price = Math.max(1, (this.t.item(box) || {}).price || (ice ? 1000 : 500));
    if (p.Mito < price) return fail('You do not have enough Mito for this transaction.');
    p.Mito -= price;
    const grants = [{ kind: 'item', item: box, count: 1 }];
    if (count(p, 'event_box_recipe_n') <= 0) grants.push({ kind: 'item', item: 'event_box_recipe_n', count: 1 });
    this.grant(p, grants);
    return { ok: true, grants };
  }

  harvestDeliver(p) {
    if (!this.is('harvest')) return fail("The harvest event isn't running.");
    const box = DIST_BOXES.find(b => count(p, b) > 0);
    if (!box) return fail('no box');
    give(p, box, -1);
    const value = Math.max(1, (this.t.item(box) || {}).value || 1);
    const ev = this.t.draw('restaurant_evaluation', p.Level, this.rng)[0];
    const grade = ev ? ev.count : 1;
    const receipts = grade * value;
    const grants = [{ kind: 'item', item: 'event_receipt_01', count: receipts }];
    this.grant(p, grants);
    ensureEvents(p).HarvestReceipts += receipts;
    this.setShared('harvest', this.harvestReceipts() + receipts);
    return { ok: true, grants, grade, receipts };
  }

  harvestStep() { let s = 1; for (const t of HARVEST_STEPS) if (this.harvestReceipts() >= t / HARVEST_STEP_SCALE) s++; return Math.min(s, 5); }

  harvestExchange(p) {
    if (!this.take(p, 'event_receipt_01')) return fail('Need more receipt item.');
    const top = this.t.pool(`receipt_reward_step_${String(this.harvestStep()).padStart(2, '0')}`).filter(r => r.p > 0);
    if (!top.length) return { ok: true, grants: [], rank: 0 };
    let roll = this.rng() * top.reduce((a, r) => a + r.p, 0), pick = top[top.length - 1];
    for (const r of top) { roll -= r.p; if (roll < 0) { pick = r; break; } }
    let grants = [];
    if (pick.etc >= 1 && pick.etc <= 4) { grants = this.t.draw(pick.item, p.Level, this.rng).slice(0, 1); this.grant(p, grants); }
    return { ok: true, grants, rank: pick.etc };
  }

  // ---------------- baskets, pieces ----------------

  basket(p, recipe) {
    const n = BASKETS[recipe];
    if (!n || count(p, recipe) <= 0) return fail('You need the recipe.');
    const paint = count(p, 'twinkling_wrapping') > 0 ? 'twinkling_wrapping' : count(p, 'careful_wrapping') > 0 ? 'careful_wrapping' : null;
    if (!paint) return fail('You need Colorful Paint.');
    if (count(p, 'black_chocolete') < n[0] || count(p, 'white_chocolete') < n[1] || count(p, 'pink_chocolete') < n[2]) return fail('Not enough eggs.');
    give(p, paint, -1); give(p, 'black_chocolete', -n[0]); if (n[1]) give(p, 'white_chocolete', -n[1]); if (n[2]) give(p, 'pink_chocolete', -n[2]);
    const grants = this.t.draw(`rw_${recipe[0]}_recipe_${paint === 'twinkling_wrapping' ? 'twinkling' : 'careful'}`, p.Level, this.rng).slice(0, 1);
    this.grant(p, grants);
    return { ok: true, grants };
  }

  combine(p, item) {
    const set = this.t.pieceSet(item);
    if (!set) return fail("This can't be combined.");
    for (const [id, n] of set.needs) if (count(p, id) < n) return fail('Combination Failed. Not all pieces are gathered.');
    for (const [id, n] of set.needs) give(p, id, -n);
    const grants = [{ kind: 'item', item: set.result, count: 1 }];
    this.grant(p, grants);
    return { ok: true, grants };
  }

  // ---------------- GACHAINVEN: season car parts, key parts, compound, badge sale ----------------

  gachaCar(season = this.gachaSeason()) { return GACHA_CARS[season <= 9 ? season : ((season - 1) % 9) + 1]; }
  gachaParts(car) { return ['A', 'B', 'C', 'D'].map(x => (car === 'shadow' ? `i_n_shadow_${x}` : `i_n_comp_${x}`)); }
  gachaKey(car, grade) { const k = `i_n_${car}_k_v${grade}_${GACHA_KEY_SUFFIX[car] || ''}`; return this.t.item(k) ? k : `i_n_${car}_k_v${grade}`; }

  /** A capsule's extra: sometimes one of the season car's Vehicle Parts A-D, rarely its Key Parts (V1-V4). */
  gachaBonus() {
    const car = this.gachaCar(), out = [];
    if (this.rng() < GACHA_PART_CHANCE) out.push({ kind: 'item', item: this.gachaParts(car)[Math.floor(this.rng() * 4)], count: 1 });
    if (this.rng() < GACHA_KEY_CHANCE) out.push({ kind: 'item', item: this.gachaKey(car, 1 + Math.floor(this.rng() * 4)), count: 1 });
    return out;
  }

  /** Using Key Parts (gacha_key, 0x5d687b): Vehicle Parts A-D of the car + the key -> the car's gift box (column 3). */
  gachaCompound(p, item) {
    const it = this.t.item(item);
    const m = /^i_n_([a-z]+)_k_v(\d)/i.exec(String(item || ''));
    if (!it || it.type !== 'gacha_key' || !m) return fail("That isn't a Key Parts item.");
    const parts = this.gachaParts(m[1].toLowerCase());
    if (count(p, item) <= 0 || parts.some(x => count(p, x) <= 0)) return fail('You have failed to combine a Gotcha vehicle due to insufficient parts.');   // gacha_compound_failure
    give(p, item, -1); for (const x of parts) give(p, x, -1);
    const box = it.pool || `i_n_${m[1].toLowerCase()}_box_v${m[2]}`;
    const grants = [{ kind: 'item', item: box, count: 1 }];
    this.grant(p, grants);
    return { ok: true, grants, used: [item, ...parts] };
  }

  /** BADGE 4610000 SELL (op 0x197): a badge back for its resale price (item column 8). */
  gachaSell(p, item, n) {
    const it = this.t.item(item);
    n = Math.max(1, n | 0);
    if (!it || !/dcbadge/i.test(item)) return fail('This item cannot be sold here.');
    if (count(p, item) < n) return fail('Registration information is not consistent with item information.');   // 10419
    const price = Math.max(0, it.sell | 0) * n;
    give(p, item, -n);
    p.Mito += price;
    return { ok: true, price, sold: n };
  }

  // ---------------- Pick'em (VIEWING 1980000, ops 0x834-0x839) ----------------

  /** The symbols by finishing place: max(1, n / 5) each of gold, silver and bronze, the rest bombs. */
  pickemLayout(n) {
    const each = Math.max(1, Math.floor(n / 5));
    const syms = [];
    for (const s of [1, 2, 3]) for (let k = 0; k < each; k++) syms.push(s);
    while (syms.length < n) syms.push(0);
    syms.length = n;
    for (let i = syms.length - 1; i > 0; i--) { const j = Math.floor(this.rng() * (i + 1)); [syms[i], syms[j]] = [syms[j], syms[i]]; }
    return syms;
  }

  pickemRoom(session, id) {
    const room = this.races ? this.races.rooms.get(id | 0) : null;
    if (!room || session.race !== room || !room.watchers.has(session.id)) return null;
    return room;
  }

  pickemLead(room) { let best = 0; for (const r of room.info().racers) best = Math.max(best, Number(r.p) || 0); return best; }

  /** YES on the prompt (0x834 "init"): a Pick'em Ticket for 4+ racers before half race; AI races refused (11398). */
  pickemStart(session, m) {
    const p = session.profile;
    const room = this.pickemRoom(session, m.room);
    if (!room) return fail("Pick'em is for spectators of a Battle Zone race.");
    if (room.state !== 'countdown' && room.state !== 'racing') return fail('The race is not running.');
    const racers = [...room.racers.values()];
    if (racers.length < 4) return fail("Pick'em needs four or more racers.");
    if (racers.some(r => r.ai)) return fail('Display Gotcha is unavailable during AI Battle play.', 11398);
    if (this.pickemLead(room) >= 0.5) return fail("Pick'em closes at half race.");
    if (!this.take(p, 'gacha_observe')) return fail("You don't have a Pick'em Ticket.");
    const order = racers.sort((a, b) => a.slot - b.slot).map(r => r.name);
    session.pickem = { room: room.id, symbols: this.pickemLayout(racers.length), picks: order.slice(0, 3), at: this.clock() };
    return { ok: true, symbols: session.pickem.symbols, picks: session.pickem.picks };
  }

  /** IDSELECTCHECK (0x836 {racer, on}): up to three picks, locked at half race. */
  pickemPick(session, m) {
    const st = session.pickem;
    const room = st ? this.pickemRoom(session, st.room) : null;
    if (!room) return fail("no Pick'em running");
    if (this.pickemLead(room) >= 0.5) return fail('The picks are locked.');
    const name = String(m.name || '');
    if (![...room.racers.values()].some(r => r.name === name)) return fail('no such racer');
    const i = st.picks.indexOf(name);
    if (m.on === false) { if (i >= 0) st.picks.splice(i, 1); }
    else if (i < 0) { if (st.picks.length >= 3) return fail('You can pick three racers.'); st.picks.push(name); }
    return { ok: true, picks: st.picks };
  }

  /** The race ended: the multiplier from the picks' places and the Slot Ticket of that grade (0x838). */
  pickemFinish(session) {
    const st = session.pickem;
    if (!st) return fail("no Pick'em running");
    const room = this.races ? this.races.rooms.get(st.room) : null;
    const done = room && room.lastResults && room.lastResults.at >= st.at ? room.lastResults.results : null;
    if (!done) return fail('The race is not over yet.');
    session.pickem = null;
    const r = Events.pickemGrade(st.symbols, st.picks, done);
    const grants = [{ kind: 'item', item: `gacha_observe_v${r.grade}`, count: 1 }];
    this.grant(session.profile, grants);
    return { ok: true, grade: r.grade, places: r.places, symbols: st.symbols, grants };
  }

  static pickemGrade(symbols, picks, results) {
    const places = picks.map(n => { const x = results.find(y => y.name === n); return x && x.place > 0 ? x.place : 0; });
    const pts = places.reduce((a, pl) => a + (pl > 0 ? PICKEM_POINTS[symbols[pl - 1] | 0] || 0 : 0), 0);
    return { grade: Math.max(1, Math.min(10, 1 + pts)), places };
  }

  // ---------------- egg-basket gifts (EVENT 4700000: 0x51a name check, 0x5ce note, 0xe10 bonus) ----------------

  valenCheck(session, name) {
    const d = this.social ? this.social.driver(String(name || '').trim()) : null;
    return d ? { ok: true, name: d.name } : fail('Incorrect character name.');   // 10107 / 10106
  }

  /** GIFTBUTTON: the basket travels as a "Gifted ... Basket" in a note; both drivers get an Easter Rabbit Doll during the event. */
  valenGift(session, m) {
    const p = session.profile;
    const item = String(m.item || '');
    const gift = BASKET_GIFT[item];
    if (!gift) return fail('This item cannot be sent as an attachment.');   // 11034
    const to = String(m.to || '').trim();
    if (to.length < 2) return fail('Incorrect character name.');                       // 10285
    const text = String(m.message || '').replace(/[\u0000-\u001f]/g, ' ').trim().slice(0, 80);
    if (!text) return fail('Type your message here.');                                  // 11010
    if (to.toLowerCase() === String(session.name).toLowerCase()) return fail('You cannot send a gift to yourself.');   // 10287
    const d = this.social ? this.social.driver(to) : null;
    if (!d) return fail(`${to.slice(0, 16)} does not exist.`);                         // 11038
    if (this.social.blocks && this.social.blocks(d.accountId, session.accountId)) return fail(`${d.name} cannot receive any messages at this time.`);   // 11035
    if (count(p, item) <= 0) return fail("You don't have that basket.");
    if ((p.Mito | 0) < GIFT_FEE) return fail('Insufficient Mito.');                   // 10500
    give(p, item, -1);
    p.Mito -= GIFT_FEE;
    this.social.mail.deliver(d.accountId, session.name, session.accountId, 0, text, 0, { kind: 'stack', id: gift, count: 1 });
    const grants = [];
    if (this.is('valentine')) {
      grants.push({ kind: 'item', item: 'lovelove_heart', count: 1 });
      this.grant(p, grants);
      this.social.mail.system(d.accountId, `${session.name} sent you an Egg Basket. Here is an Easter Rabbit Doll too!`, 0, undefined, { kind: 'stack', id: 'lovelove_heart', count: 1 });
    }
    return { ok: true, fee: GIFT_FEE, to: d.name, gift, grants };
  }

  // ---------------- the props NPC event (Indy4: 0x6fcc00, ops 0x493-0x495) ----------------

  propsFind(p, idx) {
    if (!this.is('indy4')) return fail("The event isn't running.");
    if (!Number.isInteger(idx) || idx < 0 || idx > 2) return fail('no such prop');
    const grants = count(p, PROPS[idx]) > 0 ? [] : [{ kind: 'item', item: PROPS[idx], count: 1 }];
    this.grant(p, grants);
    return { ok: true, grants };
  }

  /** At the NPC with all three props (0x494 -> 0x495): the props go back, the reward once a day. */
  propsClaim(p) {
    if (!this.is('indy4')) return fail("The event isn't running.");
    const e = this.dayRoll(p);
    if (e.PropsDay === this.today) return fail('already');
    if (PROPS.some(x => count(p, x) <= 0)) return fail('ing');
    for (const x of PROPS) give(p, x, -1);
    e.PropsDay = this.today;
    const grants = [{ kind: 'item', item: PROPS_REWARD.item, count: 1 }, { kind: 'mito', count: PROPS_REWARD.mito }];
    this.grant(p, grants);
    return { ok: true, grants };
  }

  // ---------------- buff_item drinks (Halloween Drink, 0x5d620c) ----------------

  /** The drink starts the Special Drink effect (economy.js session.mita); refused at 250 % or more (11059). */
  drink(session, item) {
    const value = DRINKS[item];
    if (!value) return fail('You cannot use this item at this time.');
    const now = this.clock() / 1000;
    const cur = session.mita && session.mita.until > now ? session.mita.value : 100;
    if (cur >= 250) return fail('Having too much Special Drink will cause serious health problem. Please wait until the effect wears off.');   // 11059
    if (!this.take(session.profile, item)) return fail("You don't have that.");
    session.mita = { value: Math.max(cur, value), until: now + DRINK_SECONDS };
    return { ok: true, value: session.mita.value, seconds: DRINK_SECONDS };
  }

  // ---------------- play drops (EventDrops.TickOffline) ----------------

  tick(p, city) {
    const e = this.dayRoll(p);
    const seasons = this.seasons();
    const races = (p.RacesEntered | 0) + (p.PvpRaces | 0);
    const chases = Math.max(0, (p.HuvCaught | 0) - e.SeenHuv), raced = Math.max(0, races - e.SeenRaces);
    e.SeenHuv = p.HuvCaught | 0; e.SeenRaces = races;
    const grants = [];
    const r = { ok: true, grants };
    for (let i = 0; i < Math.min(chases, 50); i++) this.drop(p, e, seasons, city, true, grants);
    for (let i = 0; i < Math.min(raced, 50); i++) this.drop(p, e, seasons, city, false, grants);
    if (e.AttendDay !== this.today) {
      e.AttendDay = this.today;
      if (seasons.includes('anniversary')) { grants.push({ kind: 'item', item: 'i_n_00100', count: 1 }); r.greeting = '3rdanni_attendevent_01_omd'; }
      if (seasons.includes('halloween')) { grants.push({ kind: 'item', item: 'i_n_halloween', count: 1 }); r.halloween = true; }
    }
    r.levelUps = this.grant(p, grants);
    return r;
  }

  drop(p, e, seasons, city, chase, out) {
    const cap = () => e.DayDrops < MAX_DROPS_PER_DAY;
    if (seasons.includes('christmas') && cap()) { e.DayDrops++; out.push({ kind: 'item', item: 'i_n_00088', count: 1 }); }
    const sh = CITY_SHORT[String(city || '').toLowerCase()];
    if (chase && seasons.includes('harvest') && cap() && sh) { e.DayDrops++; out.push(...this.t.draw(`rw_harvest_${sh}`, p.Level, this.rng)); }
    if (seasons.includes('valentine') && cap()) {
      e.DayDrops++;
      out.push(...this.t.draw(chase ? 'rw_valentain_bronze' : 'rw_valentain_silver', p.Level, this.rng));
      for (const rc of Object.keys(BASKETS)) if (count(p, rc) <= 0 && !out.some(g => g.item === rc)) out.push({ kind: 'item', item: rc, count: 1 });
      if (e.DayDrops % 3 === 0) out.push({ kind: 'item', item: 'careful_wrapping', count: 1 });
    }
    if (seasons.includes('summer') && cap()) { e.DayDrops++; out.push(...this.t.draw(chase ? 'rw_2010_sumfest_droptable_cs' : 'rw_2010_sumfest_droptable_bz', p.Level, this.rng)); }
  }
}

function bits(m) { let n = 0; for (let b = 0; b < 10; b++) if (m & (1 << b)) n++; return n; }
function slotMito(sym, grade) { return sym[0] === sym[1] && sym[1] === sym[2] && sym[0] !== 0 ? [0, 10000, 5000, 2000][sym[0]] * grade : 0; }

/** DC_COUPON_CODES="CODE=item:count,CODE2=item" -> Map(CODE -> { item, count }). */
function parseCodes(text) {
  const m = new Map();
  for (const part of String(text || '').split(',')) {
    const [code, rest] = part.split('=');
    if (!code || !rest) continue;
    const [item, n] = rest.split(':');
    if (item) m.set(code.trim().toUpperCase(), { item: item.trim(), count: Math.max(1, Number(n) || 1) });
  }
  return m;
}

module.exports = { Events, Tables, loadTables, ensureEvents, parseSeasons, seasonsForDate, slotMito, parseCodes,
  MITO_PER_COIN, GACHA_PRICE, LOTTO_GOALS, MAX_DROPS_PER_DAY, COUPON_RATE, LIMITS, DAILY, XMAS_CITIES, HARVEST_STEPS };
