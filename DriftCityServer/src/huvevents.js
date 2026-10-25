// Rush Time and the Virtual HUV with real players (DriftCity_RE/CHASES.md sections 4-5): the city events the original
// server ran (switch City.RushTime, packets 0x4e2270 states 2-4; the V-HUV registration 0x11c, the match 900000 /
// 910000, the result 3810000). The client screens are RushTime.cs / VirtualChase.cs; offline they run with AI stand-ins.
//
// Rush Time (per city; remake rules where the server decided):
//   - announced every 900 s (the first 240 s after the city got its first driver) at chasespawnlist point `spot` of the
//     city (the clients map spot % point count), zone radius 150 m: push rush.notice {id, spot, boss, seconds, min} to
//     every driver of the city (RUSHWAIT 2910000, 11137 / 11138);
//   - the clients report the zone (rush.zone {id, in}); 120 s of waiting (push rush.wait {drivers, min, seconds});
//     AI stand-ins drift into the zone like the offline game's (so a quiet server still gets its event), the minimum is
//     3 drivers (WAIT_INFO "%2u/%2u");
//   - the start: every driver still in the zone and free (not in the Undercity, a race, a chase, a V-HUV) joins; the boss
//     gets 400 + 80 per driver health and 300 s; the first driver is the boss's owner (party HUV, TRAFFIC.md section 14):
//     its client runs the boss and sends rush.huv {cars}, relayed to the others; hits rush.hit {damage} (five 45-damage
//     hits a second per driver), the AI stand-ins ram it every 2.5-4.5 s for 5-12; push rush.hp {hp, max, by, drivers};
//   - the hit that stops it names the lucky driver (RUSHRESULT 2920000, 10893): every driver still in it is paid by
//     economy.js's rush rule for their hits, the lucky one + 5000 Mito and three Mittron Enhancers (i_n_00003); push
//     rush.end {success, lucky, prize, rows, driver}; rush.leave (RUSHINFO ESC, 10566) drops out unpaid.
// The Virtual HUV (per city and team size):
//   - vhuv.register {size: 4 | 8} (QUESTDIALOG VC44 / VC88, 10942), vhuv.cancel; push vhuv.wait {size, waiting}
//     (VIRTUAL 3830000, 11003); refused while busy (10966) or in a Rush Time zone (10967);
//   - the match starts when both teams are full of real drivers, or 12 s after the first registration with AI stand-ins
//     filling the rest; the drivers are split White / Red in registration order and moved into a private channel of the
//     city (index 200 + match) so they see each other;
//   - each team has one virtual HUV at a time (10971 "You've been selected to be the HUV" for a driver): the other team
//     crashes it; the role passes to the next member of the team after 3 crashes taken or 45 s (NEXT_HUV / MY_HUV);
//     an AI stand-in's HUV car is run by the match's first driver (vhuv.huv {cars: [white, red]}, relayed);
//   - vhuv.crash {target}: the reporter's team scores when the target is the other team's HUV, at most one crash per
//     1.5 s per driver, within 25 m of the target's last known position; AI stand-ins score every 9-16 s;
//   - 180 s, more crashes wins; every driver is paid by economy.js's vhuv rule (x 1.5 for the winners); push vhuv.end.
'use strict';
const { rewards, addExp, ensureProfile } = require('./economy');
const { rewardExtras } = require('./workshop');

const RUSH = { firstMs: 240000, intervalMs: 900000, waitMs: 120000, runMs: 300000, min: 3, hpBase: 400, hpPerDriver: 80,
  aiMin: 2500, aiMax: 4500, aiDmgMin: 5, aiDmgMax: 12, prize: 5000, item: 'i_n_00003', items: 3, maxAiWaiting: 8 };
const VHUV = { fillMs: 12000, matchMs: 180000, crashGapMs: 1500, passAfter: 3, passMs: 45000, aiMin: 9000, aiMax: 16000, crashRange: 25 };
const HIT_CAP = 45, HITS_PER_SECOND = 5, HUV_RATE = 12, HUV_CARS = 4;
const AI_NAMES = ['Rio', 'Duke', 'Mika', 'Sora', 'Kane', 'Lexa', 'Bram', 'Nova', 'Ivo', 'Tess', 'Jun', 'Ryn', 'Oda', 'Pim', 'Zed', 'Ula'];
const VHUV_CHANNEL_BASE = 200;

class HuvEvents {
  constructor(deps) {
    this.world = deps.world; this.economy = deps.economy; this.log = deps.log || (() => {}); this.clock = deps.clock || Date.now;
    this.rng = deps.rng || Math.random; this.gd = deps.gamedata || null;
    this.rushes = new Map();       // city -> rush
    this.nextRush = new Map();     // city -> ms of the next announcement
    this.nextRushId = 1;
    this.queues = new Map();       // `${city}:${size}` -> queue
    this.matches = new Map();      // id -> match
    this.nextMatch = 1;
    this.auto = deps.auto !== false;
  }

  close() {}

  owns(t) { return typeof t === 'string' && (t.startsWith('rush.') || t.startsWith('vhuv.')); }

  handle(session, m) {
    switch (m.t) {
      case 'rush.zone': return this.rushZone(session, m);
      case 'rush.hit': return this.rushHit(session, m);
      case 'rush.huv': return this.rushHuv(session, m);
      case 'rush.leave': return this.rushLeave(session);
      case 'rush.info': { const r = this.rushOf(session); return { ok: true, rush: r ? this.rushInfo(r) : null }; }
      case 'rush.gm': {
        // a GM's Rush Time now (the switch City.RushTime's event; admin sessions, or any session with DC_TEST_EVENTS=1 for tests)
        if (!session.admin && process.env.DC_TEST_EVENTS !== '1') return { ok: false, error: 'not allowed' };
        if (!session.channel) return { ok: false, error: 'not in a city' };
        const r = this.announce(session.channel.city, { spot: Number.isInteger(m.spot) ? m.spot : undefined, waitMs: Math.max(1, Number(m.seconds) || RUSH.waitMs / 1000) * 1000,
                                                       aiWaiting: Number.isInteger(m.ai) ? Math.max(0, Math.min(RUSH.maxAiWaiting, m.ai)) : undefined });
        return { ok: true, id: r.id };
      }
      case 'vhuv.register': return this.vhuvRegister(session, m);
      case 'vhuv.gm': {
        // tests / GMs: the match ends in `seconds`; `huv` makes that driver their team's virtual HUV now
        if (!session.admin && process.env.DC_TEST_EVENTS !== '1') return { ok: false, error: 'not allowed' };
        const v = this.matchOf(session);
        if (!v) return { ok: false, error: 'not in a Virtual HUV match' };
        if (Number(m.seconds) > 0) v.deadline = this.clock() + Number(m.seconds) * 1000;
        const k = typeof m.huv === 'string' ? v.members.findIndex(x => x.name === m.huv && !x.gone) : -1;
        if (k >= 0) {
          const team = v.members[k].team;
          v.huv[team] = k; v.taken[team] = 0; v.huvSince[team] = this.clock(); v.huvPos[team] = null;
          this.vhuvPush(v, { t: 'vhuv.huv.next', id: v.id, team, huv: this.huvNames(v) });
        }
        return { ok: true };
      }
      case 'vhuv.cancel': this.vhuvUnqueue(session); return { ok: true };
      case 'vhuv.crash': return this.vhuvCrash(session, m);
      case 'vhuv.huv': return this.vhuvHuv(session, m);
      case 'vhuv.leave': return this.vhuvLeave(session);
      default: return { ok: false, error: 'unknown message' };
    }
  }

  /** In a running Rush Time or a V-HUV match / queue (other modes refuse them). */
  busy(s) { return !!(s && (s.rush || s.vhuv || s.vhuvQueue)); }
  /** In a Rush Time zone (waiting) or running one: the Undercity invite is refused (11146), the V-HUV too (11145). */
  inRush(s) {
    if (!s) return false;
    if (s.rush) return true;
    for (const r of this.rushes.values()) if (r.state === 'waiting' && r.zone.has(s.id)) return true;
    return false;
  }

  cityDrivers(city) {
    const out = [];
    for (const s of this.world.sessions.values()) if (!s.closed && s.profile && s.channel && s.channel.city === city && !s.uc) out.push(s);
    return out;
  }

  tick() {
    const now = this.clock();
    this.rushTick(now);
    this.vhuvTick(now);
  }

  removed(s) {
    const r = this.rushOf(s);
    if (r) this.rushDrop(r, s.id);
    for (const r2 of this.rushes.values()) r2.zone.delete(s.id);
    this.vhuvUnqueue(s);
    const v = this.matchOf(s);
    if (v) this.vhuvDrop(v, s.id);
  }

  // ---------------- Rush Time ----------------

  rushOf(s) { const r = s.rush ? [...this.rushes.values()].find(x => x.id === s.rush) : null; if (s.rush && !r) s.rush = 0; return r; }

  rushTick(now) {
    const cities = new Set();
    for (const s of this.world.sessions.values()) if (!s.closed && s.channel && !s.uc) cities.add(s.channel.city);
    if (this.auto) for (const city of cities) {
      if (this.rushes.has(city)) continue;
      if (!this.nextRush.has(city)) this.nextRush.set(city, now + RUSH.firstMs);
      if (now >= this.nextRush.get(city)) this.announce(city);
    }
    for (const r of [...this.rushes.values()]) {
      if (r.state === 'waiting') {
        // the AI stand-ins drift into the zone during the countdown (0..1 a tick of 0.5 s, about one in 12 s)
        if (r.aiWaiting < RUSH.maxAiWaiting && this.rng() < 0.5 / 12) { r.aiWaiting++; this.rushWaitPush(r); }
        if (now >= r.deadline) this.rushStart(r);
        continue;
      }
      if (r.state !== 'running') continue;
      for (const m of r.members.filter(x => !x.gone)) {
        const s = this.world.sessions.get(m.id);
        if (!s || s.closed || !s.channel || s.channel.city !== r.city) this.rushDrop(r, m.id);
      }
      if (!this.rushes.has(r.city)) continue;
      for (const ai of r.ai) {
        if (now < ai.next) continue;
        ai.next = now + RUSH.aiMin + this.rng() * (RUSH.aiMax - RUSH.aiMin);
        this.rushDamage(r, RUSH.aiDmgMin + this.rng() * (RUSH.aiDmgMax - RUSH.aiDmgMin), ai.name, null);
        if (!this.rushes.has(r.city)) break;
      }
      if (this.rushes.has(r.city) && now >= r.deadline) this.rushEnd(r, false, null);
    }
  }

  /** Announces a Rush Time in `city` now (also the GM / tests). spot / boss: the point and the boss index (random). */
  announce(city, opts = {}) {
    if (this.rushes.has(city)) return this.rushes.get(city);
    const now = this.clock();
    const r = {
      id: this.nextRushId++, city, spot: Number.isInteger(opts.spot) ? opts.spot : Math.floor(this.rng() * 1000),
      boss: Number.isInteger(opts.boss) ? opts.boss : Math.floor(this.rng() * 1000), state: 'waiting', deadline: now + (opts.waitMs ?? RUSH.waitMs),
      zone: new Set(), aiWaiting: Number.isInteger(opts.aiWaiting) ? opts.aiWaiting : Math.floor(this.rng() * 2), members: [], ai: [], hp: 0, max: 0, owner: 0,
      huvAt: 0, huvSent: 0, start: 0,
    };
    this.rushes.set(city, r);
    for (const s of this.cityDrivers(city)) s.send({ t: 'rush.notice', id: r.id, city, spot: r.spot, boss: r.boss, seconds: Math.round((r.deadline - now) / 1000), min: RUSH.min });
    this.log(`rush time ${r.id}: announced in ${city} (spot ${r.spot})`);
    return r;
  }

  rushZone(session, m) {
    const r = session.channel ? this.rushes.get(session.channel.city) : null;
    if (!r || r.id !== (m.id | 0)) return { ok: false, error: 'no Rush Time' };
    if (r.state !== 'waiting') return { ok: true, state: r.state };
    const was = r.zone.has(session.id);
    if (m.in === true) {
      if (session.vhuv || session.vhuvQueue) return { ok: false, error: 'You cannot participate in the Rushtime while being registered for the Virtual HUV.' };   // 10967
      r.zone.add(session.id);
    } else r.zone.delete(session.id);
    if (was !== r.zone.has(session.id)) this.rushWaitPush(r);
    return { ok: true, drivers: this.rushWaiting(r), min: RUSH.min };
  }

  rushWaiting(r) { return r.zone.size + r.aiWaiting; }

  rushWaitPush(r) {
    const msg = { t: 'rush.wait', id: r.id, drivers: this.rushWaiting(r), humans: r.zone.size, min: RUSH.min, seconds: Math.max(0, Math.round((r.deadline - this.clock()) / 1000)) };
    for (const s of this.cityDrivers(r.city)) s.send(msg);
  }

  rushStart(r) {
    const now = this.clock();
    const humans = [...r.zone].map(id => this.world.sessions.get(id))
      .filter(s => s && !s.closed && s.channel && s.channel.city === r.city && !s.uc && !s.race && !s.partyChase && !s.vhuv && !s.vhuvQueue);
    const total = humans.length + r.aiWaiting;
    if (humans.length === 0 || total < RUSH.min) {
      for (const s of this.cityDrivers(r.city)) s.send({ t: 'rush.end', id: r.id, cancelled: true });
      this.rushes.delete(r.city);
      this.nextRush.set(r.city, now + RUSH.intervalMs);
      this.log(`rush time ${r.id}: not started (${humans.length} driver(s), ${r.aiWaiting} stand-in(s))`);
      return;
    }
    r.state = 'running';
    r.start = now;
    r.deadline = now + RUSH.runMs;
    r.max = r.hp = RUSH.hpBase + RUSH.hpPerDriver * total;
    r.members = humans.map(s => ({ id: s.id, name: s.name, hits: 0, damage: 0, budget: HIT_CAP * 2, lastHit: 0, gone: false }));
    r.ai = Array.from({ length: r.aiWaiting }, (_, i) => ({ name: AI_NAMES[i % AI_NAMES.length], next: now + RUSH.aiMin + this.rng() * (RUSH.aiMax - RUSH.aiMin) }));
    r.owner = humans[0].id;
    for (const s of humans) s.rush = r.id;
    const names = r.members.map(x => x.name);
    for (const s of humans) s.send({ t: 'rush.start', id: r.id, boss: r.boss, spot: r.spot, hp: r.hp, max: r.max, seconds: RUSH.runMs / 1000, owner: humans[0].name, members: names, drivers: total });
    for (const s of this.cityDrivers(r.city)) if (!s.rush) s.send({ t: 'rush.closed', id: r.id });
    this.log(`rush time ${r.id}: started in ${r.city} with ${humans.length} driver(s) and ${r.aiWaiting} stand-in(s), ${r.hp} hp`);
  }

  rushHit(session, m) {
    const r = this.rushOf(session);
    if (!r || r.state !== 'running') return { ok: false, error: 'not in a Rush Time' };
    const mem = r.members.find(x => x.id === session.id && !x.gone);
    if (!mem) return { ok: false, error: 'not in a Rush Time' };
    const now = this.clock();
    mem.budget = Math.min(HIT_CAP * HITS_PER_SECOND, mem.budget + (now - (mem.lastHit || r.start)) / 1000 * HIT_CAP * HITS_PER_SECOND);
    mem.lastHit = now;
    const damage = Math.max(0, Math.min(mem.budget, Number(m.damage) || 0));
    if (damage <= 0) return { ok: false, error: 'too fast' };
    mem.budget -= damage;
    mem.damage += damage;
    mem.hits += Math.max(1, Math.min(10, m.hits | 0 || 1));
    this.rushDamage(r, damage, session.name, mem);
    return { ok: true, hp: r.hp };
  }

  rushDamage(r, amount, by, mem) {
    r.hp = Math.max(0, r.hp - amount);
    this.rushPush(r, { t: 'rush.hp', id: r.id, hp: Math.round(r.hp * 10) / 10, max: r.max, by, drivers: r.members.filter(x => !x.gone).length + r.ai.length });
    if (r.hp <= 0) this.rushEnd(r, true, by, mem);
  }

  rushHuv(session, m) {
    const r = this.rushOf(session);
    if (!r || r.state !== 'running') return { ok: false, error: 'not in a Rush Time' };
    if (r.owner !== session.id) return { ok: false, error: 'not the HUV owner' };
    const cars = this.relayCars(r, m);
    if (!cars) return { ok: false, error: 'too fast' };
    const msg = { t: 'rush.huv', id: r.id, cars };
    for (const x of r.members) if (!x.gone && x.id !== session.id) this.world.sessions.get(x.id)?.send(msg);
    return { ok: true };
  }

  /** At most HUV_RATE states a second, finite numbers only (x, y, z, yaw, speed per car). */
  relayCars(holder, m) {
    const now = this.clock();
    if (now - holder.huvAt >= 1000) { holder.huvAt = now; holder.huvSent = 0; }
    if (++holder.huvSent > HUV_RATE) return null;
    return (Array.isArray(m.cars) ? m.cars : []).slice(0, HUV_CARS).map(c => (Array.isArray(c) && c.length >= 5 && c.slice(0, 5).every(v => Number.isFinite(v))
      ? c.slice(0, 5).map(v => Math.round(v * 100) / 100) : null));
  }

  rushLeave(session) {
    const r = this.rushOf(session);
    if (r) this.rushDrop(r, session.id);
    for (const x of this.rushes.values()) if (x.zone.delete(session.id)) this.rushWaitPush(x);
    return { ok: true };
  }

  rushDrop(r, id) {
    const mem = r.members.find(x => x.id === id);
    const s = this.world.sessions.get(id);
    if (s && s.rush === r.id) s.rush = 0;
    if (!mem || mem.gone) return;
    mem.gone = true;
    if (r.members.every(x => x.gone)) { this.rushEnd(r, false, null); return; }
    if (r.owner === id) {
      const next = r.members.find(x => !x.gone);
      r.owner = next.id;
      this.rushPush(r, { t: 'rush.owner', id: r.id, owner: next.name });
    }
  }

  rushEnd(r, success, lucky, luckyMem) {
    if (this.rushes.get(r.city) !== r) return;
    this.rushes.delete(r.city);
    this.nextRush.set(r.city, this.clock() + RUSH.intervalMs);
    const seconds = Math.round((this.clock() - r.start) / 100) / 10;
    const rows = [];
    for (const mem of r.members) {
      const s = this.world.sessions.get(mem.id);
      const row = { name: mem.name, hits: mem.hits, damage: Math.round(mem.damage), exp: 0, mito: 0, levelUps: 0, lucky: mem === luckyMem };
      if (success && !mem.gone && s && s.profile) Object.assign(row, this.rushPay(s, mem, mem === luckyMem));
      rows.push(row);
    }
    for (const mem of r.members) {
      const s = this.world.sessions.get(mem.id);
      if (s && s.rush === r.id) s.rush = 0;
      if (s && !mem.gone) s.send({ t: 'rush.end', id: r.id, success, lucky: lucky || '', luckyIsDriver: !!luckyMem, prize: RUSH.prize, seconds, rows, driver: s.profile });
    }
    this.log(`rush time ${r.id}: ${success ? `stopped by ${lucky}` : 'failed'} after ${seconds} s`);
  }

  /** economy.js's rush rule for one driver (their hits, the lucky driver's prize), the extras, the drink. */
  rushPay(s, mem, lucky) {
    const p = ensureProfile(s.profile);
    const now = this.clock() / 1000;
    let base = rewards.rush(p.Level, Math.min(40, mem.hits), lucky);
    if (s.mita && s.mita.until > now) base = { exp: Math.floor(base.exp * s.mita.value / 100), mito: base.mito };
    const extra = rewardExtras(p, 'rush', base, {}, Math.random, this.gd);
    const r = { exp: base.exp + extra.exp, mito: base.mito + extra.mito };
    p.Mito += r.mito;
    p.HuvCaught++; p.BossesCaught++;
    if (lucky) { const items = require('./items'); items.ensureItems(p); items.give(p, RUSH.item, RUSH.items); }
    const levels = addExp(p, r.exp);
    s.lastClaim = s.lastClaim || {};
    s.lastClaim.rush = now;
    s.save();
    return { exp: r.exp, mito: r.mito, levelUps: levels, drop: lucky ? RUSH.item : extra.drop || null };
  }

  rushPush(r, msg) { for (const mem of r.members) if (!mem.gone) this.world.sessions.get(mem.id)?.send(msg); }

  rushInfo(r) {
    return { id: r.id, city: r.city, state: r.state, spot: r.spot, boss: r.boss, hp: r.hp, max: r.max, drivers: this.rushWaiting(r),
             seconds: Math.max(0, Math.round((r.deadline - this.clock()) / 1000)), members: r.members.map(x => x.name) };
  }

  // ---------------- the Virtual HUV ----------------

  matchOf(s) { const v = s.vhuv ? this.matches.get(s.vhuv) : null; if (s.vhuv && !v) s.vhuv = 0; return v; }

  vhuvRegister(session, m) {
    const size = (m.size | 0) >= 8 ? 8 : 4;
    if (!session.channel || session.uc || session.race || session.partyChase || session.rush || session.partyPatrol)
      return { ok: false, error: 'You cannot register for the Virtual HUV here. Please register in the city.' };   // 10966
    if (this.inRush(session)) return { ok: false, error: 'You cannot participate in the Rushtime while being registered for the Virtual HUV.' };   // 10967
    if (session.vhuv) return { ok: false, error: 'already in a Virtual HUV match' };
    this.vhuvUnqueue(session);
    const key = `${session.channel.city}:${size}`;
    let q = this.queues.get(key);
    if (!q) this.queues.set(key, q = { key, city: session.channel.city, size, ids: [], firstAt: this.clock() });
    q.ids.push(session.id);
    session.vhuvQueue = key;
    this.queuePush(q);
    return { ok: true, size, waiting: q.ids.length };   // 10942
  }

  vhuvUnqueue(session) {
    const key = session.vhuvQueue;
    if (!key) return;
    session.vhuvQueue = null;
    const q = this.queues.get(key);
    if (!q) return;
    q.ids = q.ids.filter(id => id !== session.id);
    if (!q.ids.length) this.queues.delete(key); else this.queuePush(q);
  }

  queuePush(q) { for (const id of q.ids) this.world.sessions.get(id)?.send({ t: 'vhuv.wait', size: q.size, waiting: q.ids.length }); }

  vhuvTick(now) {
    for (const q of [...this.queues.values()]) {
      q.ids = q.ids.filter(id => { const s = this.world.sessions.get(id); return s && !s.closed && s.channel && s.channel.city === q.city; });
      if (!q.ids.length) { this.queues.delete(q.key); continue; }
      if (q.ids.length >= q.size * 2 || now - q.firstAt >= VHUV.fillMs) this.vhuvStart(q);
    }
    for (const v of [...this.matches.values()]) {
      for (const mem of v.members.filter(x => !x.ai && !x.gone)) {
        const s = this.world.sessions.get(mem.id);
        if (!s || s.closed || s.channel !== v.channel) this.vhuvDrop(v, mem.id);
      }
      if (!this.matches.has(v.id)) continue;
      // the AI stand-ins score on their clocks; the HUV role passes after 45 s
      for (const mem of v.members) {
        if (!mem.ai || now < mem.next) continue;
        mem.next = now + VHUV.aiMin + this.rng() * (VHUV.aiMax - VHUV.aiMin);
        this.vhuvScore(v, mem, this.other(mem.team));
        if (!this.matches.has(v.id)) break;
      }
      if (!this.matches.has(v.id)) continue;
      for (const team of ['white', 'red']) if (now - v.huvSince[team] >= VHUV.passMs) this.vhuvPass(v, team);
      if (now >= v.deadline) this.vhuvEnd(v);
    }
  }

  other(team) { return team === 'white' ? 'red' : 'white'; }

  /** Starts a match from the queue: real drivers split White / Red, AI stand-ins fill both teams. */
  vhuvStart(q) {
    this.queues.delete(q.key);
    const now = this.clock();
    const id = this.nextMatch++;
    const ch = this.world.channelFor(q.city, VHUV_CHANNEL_BASE + id);
    const humans = q.ids.slice(0, q.size * 2).map(x => this.world.sessions.get(x)).filter(Boolean);
    const members = [];
    humans.forEach((s, i) => members.push({ id: s.id, name: s.name, team: i % 2 === 0 ? 'white' : 'red', ai: false, crashes: 0, lastCrash: 0, gone: false, home: s.channel ? s.channel.id : null }));
    let ai = 0;
    for (const team of ['white', 'red'])
      while (members.filter(x => x.team === team).length < q.size)
        members.push({ id: 0, name: AI_NAMES[ai++ % AI_NAMES.length], team, ai: true, crashes: 0, next: now + VHUV.aiMin + this.rng() * (VHUV.aiMax - VHUV.aiMin), gone: false });
    const v = { id, city: q.city, size: q.size, channel: ch, start: now, deadline: now + VHUV.matchMs, members, score: { white: 0, red: 0 },
                huv: {}, taken: { white: 0, red: 0 }, huvSince: { white: now, red: now }, owner: humans[0].id, huvAt: 0, huvSent: 0, huvPos: { white: null, red: null } };
    // each team's first virtual HUV: a random member of the team
    for (const team of ['white', 'red']) {
      const list = members.filter(x => x.team === team);
      v.huv[team] = members.indexOf(list[Math.floor(this.rng() * list.length)]);
    }
    this.matches.set(id, v);
    for (const s of humans) { s.vhuvQueue = null; s.vhuv = id; this.world.join(s, q.city, ch.id); }
    for (const s of humans) {
      const me = members.find(x => x.id === s.id);
      s.send({ t: 'vhuv.start', id, size: q.size, team: me.team, seconds: VHUV.matchMs / 1000, owner: humans[0].name, channel: ch.id,
               members: members.map(x => ({ name: x.name, team: x.team, ai: x.ai })), huv: this.huvNames(v) });
    }
    this.log(`vhuv ${id}: ${q.size}:${q.size} in ${q.city} with ${humans.length} driver(s)`);
  }

  huvNames(v) { return { white: v.members[v.huv.white].name, red: v.members[v.huv.red].name, whiteAi: v.members[v.huv.white].ai, redAi: v.members[v.huv.red].ai }; }

  /** The HUV role of `team` passes to its next member (NEXT_HUV). */
  vhuvPass(v, team) {
    const list = v.members.map((x, i) => (x.team === team && !x.gone ? i : -1)).filter(i => i >= 0);
    if (!list.length) return;
    const at = list.indexOf(v.huv[team]);
    v.huv[team] = list[(at + 1) % list.length];
    v.taken[team] = 0;
    v.huvSince[team] = this.clock();
    v.huvPos[team] = null;
    this.vhuvPush(v, { t: 'vhuv.huv.next', id: v.id, team, huv: this.huvNames(v) });
  }

  vhuvCrash(session, m) {
    const v = this.matchOf(session);
    if (!v) return { ok: false, error: 'not in a Virtual HUV match' };
    const mem = v.members.find(x => x.id === session.id && !x.gone);
    if (!mem) return { ok: false, error: 'not in a Virtual HUV match' };
    const now = this.clock();
    if (now - mem.lastCrash < VHUV.crashGapMs) return { ok: false, error: 'too fast' };
    const target = this.other(mem.team);
    const huv = v.members[v.huv[target]];
    if (String(m.target || '') !== huv.name) return { ok: false, error: 'not the HUV' };
    // the target's last known position: the driver's own car, or the stand-in HUV the owner sends
    const at = huv.ai ? v.huvPos[target] : (this.world.sessions.get(huv.id)?.state?.p || null);
    const me = session.state && session.state.p;
    if (at && me) {
      const dx = at[0] - me[0], dz = at[2] - me[2];
      if (dx * dx + dz * dz > VHUV.crashRange * VHUV.crashRange) return { ok: false, error: 'too far' };
    }
    mem.lastCrash = now;
    mem.crashes++;
    this.vhuvScore(v, mem, target);
    return { ok: true, score: v.score };
  }

  vhuvScore(v, mem, target) {
    v.score[mem.team]++;
    v.taken[target]++;
    this.vhuvPush(v, { t: 'vhuv.score', id: v.id, by: mem.name, team: mem.team, white: v.score.white, red: v.score.red, target: v.members[v.huv[target]].name });
    if (v.taken[target] >= VHUV.passAfter) this.vhuvPass(v, target);
  }

  /** The owner's stand-in HUV cars: [white HUV state or null, red HUV state or null], relayed to the others. */
  vhuvHuv(session, m) {
    const v = this.matchOf(session);
    if (!v) return { ok: false, error: 'not in a Virtual HUV match' };
    if (v.owner !== session.id) return { ok: false, error: 'not the HUV owner' };
    const cars = this.relayCars(v, m);
    if (!cars) return { ok: false, error: 'too fast' };
    if (cars[0]) v.huvPos.white = cars[0];
    if (cars[1]) v.huvPos.red = cars[1];
    const msg = { t: 'vhuv.huv', id: v.id, cars, huv: this.huvNames(v) };
    for (const x of v.members) if (!x.ai && !x.gone && x.id !== session.id) this.world.sessions.get(x.id)?.send(msg);
    return { ok: true };
  }

  vhuvLeave(session) {
    this.vhuvUnqueue(session);
    const v = this.matchOf(session);
    if (v) this.vhuvDrop(v, session.id);
    return { ok: true };
  }

  vhuvDrop(v, id) {
    const mem = v.members.find(x => x.id === id && !x.ai);
    const s = this.world.sessions.get(id);
    if (s && s.vhuv === v.id) { s.vhuv = 0; if (mem) this.goHome(v, s, mem); }
    if (!mem || mem.gone) return;
    mem.gone = true;
    // a driver who leaves is replaced by a stand-in (the team stays whole); the role passes on when it was theirs
    mem.ai = true; mem.next = this.clock() + VHUV.aiMin;
    if (v.members.every(x => x.ai)) { this.matches.delete(v.id); this.log(`vhuv ${v.id}: everyone left`); return; }
    if (v.owner === id) {
      const next = v.members.find(x => !x.ai && !x.gone);
      v.owner = next.id;
      this.vhuvPush(v, { t: 'vhuv.owner', id: v.id, owner: next.name });
    }
  }

  vhuvEnd(v) {
    if (!this.matches.has(v.id)) return;
    this.matches.delete(v.id);
    const seconds = Math.round((this.clock() - v.start) / 1000);
    const rows = v.members.map(mem => ({ name: mem.name, team: mem.team, ai: mem.ai, crashes: mem.crashes, exp: 0, mito: 0 }));
    v.members.forEach((mem, i) => {
      if (mem.ai || mem.gone) return;
      const s = this.world.sessions.get(mem.id);
      if (!s || !s.profile) return;
      const won = v.score[mem.team] > v.score[this.other(mem.team)];
      Object.assign(rows[i], this.vhuvPay(s, mem, won, v.size));
    });
    for (const mem of v.members) {
      if (mem.ai || mem.gone) continue;
      const s = this.world.sessions.get(mem.id);
      if (!s) continue;
      if (s.vhuv === v.id) s.vhuv = 0;
      const won = v.score[mem.team] > v.score[this.other(mem.team)];
      s.send({ t: 'vhuv.end', id: v.id, won, team: mem.team, white: v.score.white, red: v.score.red, seconds, rows, driver: s.profile });
      this.goHome(v, s, mem);
    }
    this.log(`vhuv ${v.id}: white ${v.score.white} red ${v.score.red}`);
  }

  vhuvPay(s, mem, won, size) {
    const p = ensureProfile(s.profile);
    const now = this.clock() / 1000;
    let base = rewards.vhuv(p.Level, Math.min(30, mem.crashes), won);
    if (s.mita && s.mita.until > now) base = { exp: Math.floor(base.exp * s.mita.value / 100), mito: base.mito };
    const extra = rewardExtras(p, 'vhuv', base, { size }, Math.random, this.gd);
    const r = { exp: base.exp + extra.exp, mito: base.mito + extra.mito };
    p.Mito += r.mito;
    const levels = addExp(p, r.exp);
    s.lastClaim = s.lastClaim || {};
    s.lastClaim.vhuv = now;
    s.save();
    return { exp: r.exp, mito: r.mito, levelUps: levels, drop: extra.drop || null };
  }

  goHome(v, s, mem) {
    if (!s || s.closed || s.channel !== v.channel) return;
    this.world.join(s, v.city, mem.home || undefined);
  }

  vhuvPush(v, msg) { for (const mem of v.members) if (!mem.ai && !mem.gone) this.world.sessions.get(mem.id)?.send(msg); }
}

module.exports = { HuvEvents, RUSH, VHUV, VHUV_CHANNEL_BASE };
