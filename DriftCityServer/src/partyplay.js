// Party play (DriftCity_RE/PARTYPLAY.md): what a party plays together on the server.
//   - Undercity party runs: src/undercity.js (uc.*)
//   - party patrol (PARTYPATROL; client packets 0xc24 start, 0xc26 post, 0xc27 member posts + time, 0xc1e result 3890000):
//     patrol.party.start {row} (leader), patrol.party.post {index}, patrol.party.quit; pushes party.patrol.start / .post /
//     .left / .end
//   - party bonus (0x4fe050): +5 % EXP per member at or below globalcontent PartyBonus_Level (US 80), +5 % Mito per member
//     above it, in the HUV game modes only; given to workshop.rewardExtras through setPartyBonus
//   - party combo (0x514350 sends a member's speed combo, 0x23c / 0x242 bring the member's combo and the party total):
//     party.combo {count}; push party.combo {name, combo, total, rise} (rise: the tricks the member just chained, shown
//     with SPEEDCOMBO's PARTYNUM digits on the other members' HUDs, 0x4ed43c)
//   - party chase (BS_PktPartyHuv 0xc29 spawns the party's HUVs, packet 0x4f9dcd ends it with PARTYCHASERESULT 980000):
//     chase.party.start {boss, huv, hp} (leader), chase.party.hit {damage}, chase.party.quit; pushes chase.party.start /
//     .hp / .end {success, seconds, rows, driver}; every member is paid by economy.grantChase (party bonus included)
//     The HUV is shared like the client's party HUV (0x4f0940: the packet names an owner whose client runs the HUV's AI; the
//     others show a remote actor): the start push names the owner (the leader); the owner sends chase.party.huv {cars:
//     [[x, y, z, yaw, speed], ...]} (Unity space, the HUV then its escorts) and the server relays it to the other members;
//     when the owner drops out the next member still in it becomes the owner (push chase.party.owner {owner})
//     The HUVs and their speeds are the server's (src/huvrules.js, BS_PktChaseBeginConfirm 0x4f2500's units): the start
//     push carries units [{huv, kmh, top, boostSeconds, turbo, hp}] and each unit has its own health (chase.party.hit
//     {damage, unit}, push chase.party.hp {unit, hp, max}); all must be stopped. A boss with kept escorts (bossId 11001 /
//     11004) releases them at its damage threshold (0x4a1860 / 0x4a1d10): push chase.party.split {stage: 'start' | 'out'
//     | 'finish', unit, huv, hp}; Lil' Grimer is a unit of its own, and 10 s after it came out the truck is finished
//   - solo chase start (BS_PktChaseBeginConfirm): chase.begin {boss, huv, hp} -> {units, seconds, convoy}
//   - Rush Time and the Virtual HUV with real players: src/huvevents.js (rush.*, vhuv.*)
'use strict';
const { UndercityRooms } = require('./undercity');
const { addExp, ensureProfile } = require('./economy');
const { rewardExtras } = require('./workshop');
const { chaseUnits, splitDue, KEPT } = require('./huvrules');
const { HuvEvents } = require('./huvevents');

const PARTY_BONUS_LEVEL = 80;          // globalcontent PartyBonus_Level (US column)
const PARTY_BONUS_STEP = 5;            // % per member (10 with the server flag net+0x288 & 0x800, not used)
const HUV_KINDS = new Set(['chase', 'undercity']);   // reward kinds of the HUV game modes (10996-10998 "...HUV gamemode")
// party patrol clock (remake rule: the 3880000 timer counts down, every post adds bonus seconds, 11158 when it runs out)
const PATROL_START_SECONDS = 300, PATROL_BONUS_SECONDS = 15, PATROL_MAX_SECONDS = 900;
const PATROL_MIN_POST_MS = 3000;       // economy.js 'patrol': posts stand 100-600 m apart, three seconds each at the very least
const COMBO_GAP_MS = 150;
// party chase (remake rules, CHASES.md): the time limit of a chase (ChaseMission.TimeLimit; bosses get more), the health the
// leader's client quotes is bounded by the HUV's own rule (100 + Crash rating, bosses 300), hits are capped like the
// Undercity's (45 a hit, five hits a second per member)
const CHASE_SECONDS = 150, CHASE_BOSS_SECONDS = 180, CHASE_HIT_CAP = 45, CHASE_HITS_PER_SECOND = 5;
// the owner's HUV states: at most this many a second are relayed (the client sends five), at most this many cars each
const CHASE_HUV_RATE = 12, CHASE_HUV_CARS = 24;

class PartyPlay {
  /** deps: { world, economy, log, clock, rng, gamedata, ucTimeScale } */
  constructor(deps) {
    this.world = deps.world; this.economy = deps.economy; this.log = deps.log || (() => {}); this.clock = deps.clock || Date.now;
    this.gd = deps.gamedata || null;
    this.undercity = new UndercityRooms({ world: this.world, economy: this.economy, log: this.log, clock: this.clock, rng: deps.rng, timeScale: deps.ucTimeScale, gamedata: this.gd });
    this.patrols = new Map();
    this.nextPatrol = 1;
    this.chases = new Map();
    this.nextChase = 1;
    this.rng = deps.rng || Math.random;
    this.chaseRng = deps.chaseRng || Math.random;
    this.closed = false;
    this.events = new HuvEvents({ world: this.world, economy: this.economy, log: this.log, clock: this.clock, rng: deps.eventRng || Math.random, gamedata: this.gd, partyplay: this });
    this.undercity.inRush = s => this.events.inRush(s);   // 11146: an Undercity invite is refused during Rush Time
    this.world.onRemove(s => { try { this.undercity.removed(s); this.patrolLeft(s, false); this.chaseLeft(s); this.events.removed(s); } catch (err) { this.log(`partyplay remove: ${err.message}`); } });
    this.timer = setInterval(() => { try { this.tick(); } catch (err) { this.log(`partyplay tick: ${err.stack || err}`); } }, 500);
    this.timer.unref();
    // the party bonus reaches every reward through workshop.rewardExtras
    require('./workshop').setPartyBonus((p, kind) => (this.closed ? null : this.bonusFor(p, kind)));
  }

  close() { this.closed = true; clearInterval(this.timer); this.undercity.close(); this.events.close(); }

  owns(t) { return this.undercity.owns(t) || this.events.owns(t) || (typeof t === 'string' && (t.startsWith('patrol.party.') || t.startsWith('chase.party.') || t === 'chase.begin' || t === 'party.combo' || t === 'party.bonus')); }

  handle(session, m) {
    if (this.undercity.owns(m.t)) return this.undercity.handle(session, m);
    if (this.events.owns(m.t)) return this.events.handle(session, m);
    switch (m.t) {
      case 'patrol.party.start': return this.patrolStart(session, m);
      case 'patrol.party.post': return this.patrolPost(session, m);
      case 'patrol.party.quit': return this.patrolLeft(session, true);
      case 'party.combo': return this.combo(session, m);
      case 'chase.party.start': return this.chaseStart(session, m);
      case 'chase.party.hit': return this.chaseHit(session, m);
      case 'chase.party.huv': return this.chaseHuv(session, m);
      case 'chase.party.quit': this.chaseLeft(session); return { ok: true };
      case 'chase.begin': return this.chaseBegin(session, m);
      case 'party.bonus': { const b = this.bonusOf(session); return { ok: true, ...b }; }
      default: return { ok: false, error: 'unknown message' };
    }
  }

  tick() {
    this.undercity.tick();
    this.events.tick();
    const now = this.clock();
    for (const pt of [...this.patrols.values()]) {
      // members who left the party or went offline drop out of the patrol
      for (const m of pt.members.filter(x => !x.gone)) {
        const s = this.world.sessions.get(m.id);
        if (!s || s.closed || s.party !== pt.partyId) this.patrolDrop(pt, m, !!s && !s.closed);
      }
      if (!this.patrols.has(pt.id)) continue;
      if (now >= pt.deadline) this.patrolEnd(pt, false, 'time');
    }
    for (const ch of [...this.chases.values()]) {
      for (const m of ch.members.filter(x => !x.gone)) {
        const s = this.world.sessions.get(m.id);
        if (!s || s.closed || s.party !== ch.partyId) this.chaseDrop(ch, m);
      }
      if (this.chases.has(ch.id)) this.chaseSplitStep(ch, now);
      if (this.chases.has(ch.id) && now >= ch.deadline) this.chaseEnd(ch, false);
    }
  }

  // ---------------- party bonus (0x4fe050) ----------------

  /** {exp, mito} percent for the members' levels (0 outside a party of 2+). */
  bonusOf(session) {
    const party = session ? this.world.liveParty(session) : null;
    if (!party || party.members.size < 2) return { exp: 0, mito: 0, low: 0, high: 0 };
    let low = 0, high = 0;
    for (const id of party.members) {
      const s = this.world.sessions.get(id);
      if (!s || !s.profile) continue;
      if ((s.profile.Level | 0) > PARTY_BONUS_LEVEL) high++; else low++;
    }
    return { exp: PARTY_BONUS_STEP * low, mito: PARTY_BONUS_STEP * high, low, high };
  }

  /** workshop.rewardExtras provider: only the HUV game modes. */
  bonusFor(p, kind) {
    if (!HUV_KINDS.has(kind) || !p) return null;
    let session = null;
    for (const s of this.world.sessions.values()) if (s.profile === p && !s.closed) { session = s; break; }
    const b = this.bonusOf(session);
    return b.exp || b.mito ? { exp: b.exp, mito: b.mito } : null;
  }

  // ---------------- party combo (0x514350 / 0x23c) ----------------

  combo(session, m) {
    const party = this.world.liveParty(session);
    if (!party || party.members.size < 2) return { ok: false, error: 'You are not in a party.' };
    const now = this.clock();
    if (now - (session.comboAt || 0) < COMBO_GAP_MS) return { ok: false, error: 'too fast' };
    session.comboAt = now;
    const count = Math.max(0, Math.min(10000, m.count | 0));
    const last = session.partyComboLast | 0;
    // the party total counts every trick chained by a member: the rise of the chain, or a new chain from the start
    const add = count >= last ? count - last : count;
    session.partyComboLast = count;
    party.comboTotal = Math.min(1e9, (party.comboTotal | 0) + add);
    for (const id of party.members) this.world.sessions.get(id)?.send({ t: 'party.combo', name: session.name, combo: count, total: party.comboTotal, rise: add });
    return { ok: true, total: party.comboTotal };
  }

  // ---------------- party patrol ----------------

  patrolOf(session) { const p = session.partyPatrol ? this.patrols.get(session.partyPatrol) : null; if (session.partyPatrol && !p) session.partyPatrol = 0; return p; }

  /** The leader takes a PARTYPATROL row (party_move) from the officer; every member gets it (11156). */
  patrolStart(session, m) {
    const party = this.world.liveParty(session);
    if (!party || party.members.size < 2) return { ok: false, error: 'You are not in a party.' };
    if (party.leader !== session.id) return { ok: false, error: 'Not a group leader' };   // 20022
    if (this.patrolOf(session)) return { ok: false, error: 'You cannot perform selected action during the Party Patrol.' };   // 20315
    const ids = [party.leader, ...[...party.members].filter(id => id !== party.leader)];
    const members = ids.map(id => this.world.sessions.get(id));
    if (members.some(s => !s || s.closed || !s.channel || !session.channel || s.channel.city !== session.channel.city || s.race || s.uc))
      return { ok: false, error: 'Someone in your party is not ready to start the Party Patrol.' };   // 20314
    if (members.some(s => this.patrolOf(s))) return { ok: false, error: 'Selected Personnel is currently performing a Party Patrol Mode.' };   // 20312
    const ms = this.economy.missions();
    const rowIndex = m.row | 0;
    let row;
    if (ms.instant.length > 0) {
      const r = ms.instant[rowIndex];
      if (!r || r.type !== 'patrol' || r.kind !== 'party_move') return { ok: false, error: 'unknown party patrol' };
      if (session.profile.Level < r.minLevel || session.profile.Level > r.maxLevel) return { ok: false, error: 'patrol not for this level' };
      row = { maxPosts: r.maxPosts, postExp: r.postExp, postMito: r.postMito, completeExp: r.completeExp };
    } else {
      row = { maxPosts: Math.max(1, Math.min(160, m.posts | 0 || 40)), postExp: 5, postMito: 0, completeExp: 0 };
    }
    const now = this.clock();
    const pt = {
      id: this.nextPatrol++, row: rowIndex, ...row, partyId: party.id, leader: session.id, start: now,
      deadline: now + PATROL_START_SECONDS * 1000, done: new Set(),
      members: members.map(s => ({ id: s.id, name: s.name, posts: 0, lastPost: 0, gone: false, paid: false })),
    };
    this.patrols.set(pt.id, pt);
    for (const s of members) s.partyPatrol = pt.id;
    this.patrolPush(pt, { t: 'party.patrol.start', id: pt.id, row: rowIndex, leader: session.name, members: pt.members.map(x => x.name), posts: pt.maxPosts, seconds: PATROL_START_SECONDS });
    this.log(`party patrol ${pt.id}: ${session.name} with ${members.length - 1} member(s), row ${rowIndex}`);
    return { ok: true, id: pt.id };
  }

  /** A member reached a post of the loop (0xc26): the shared counter, the member's "Post %u" and bonus time (0xc27). */
  patrolPost(session, m) {
    const pt = this.patrolOf(session);
    if (!pt) return { ok: false, error: 'not in a party patrol' };
    const mem = pt.members.find(x => x.id === session.id && !x.gone);
    if (!mem) return { ok: false, error: 'not in a party patrol' };
    const index = m.index | 0;
    if (index < 0 || index >= pt.maxPosts) return { ok: false, error: 'no such post' };
    if (pt.done.has(index)) return { ok: false, error: 'already patrolled' };
    const now = this.clock();
    if (now - mem.lastPost < PATROL_MIN_POST_MS) return { ok: false, error: 'patrol too fast' };
    mem.lastPost = now;
    mem.posts++;
    pt.done.add(index);
    pt.deadline = Math.min(pt.deadline + PATROL_BONUS_SECONDS * 1000, now + PATROL_MAX_SECONDS * 1000);
    this.patrolPush(pt, { t: 'party.patrol.post', id: pt.id, index, by: session.name, posts: mem.posts, total: pt.done.size, max: pt.maxPosts,
                          seconds: this.patrolSeconds(pt), bonus: PATROL_BONUS_SECONDS, counts: pt.members.map(x => ({ name: x.name, posts: x.posts })) });
    if (pt.done.size >= pt.maxPosts) this.patrolEnd(pt, true, 'complete');   // 11157
    return { ok: true };
  }

  patrolSeconds(pt) { return Math.max(0, Math.round((pt.deadline - this.clock()) / 100) / 10); }

  /** The leader quits: the patrol ends for all (11159); a member quits: paid for their posts, the rest go on. */
  patrolLeft(session, ask) {
    const pt = this.patrolOf(session);
    if (!pt) return ask ? { ok: true } : undefined;
    if (pt.leader === session.id) this.patrolEnd(pt, false, 'leader');
    else { const mem = pt.members.find(x => x.id === session.id); if (mem && !mem.gone) this.patrolDrop(pt, mem, ask); }
    return ask ? { ok: true } : undefined;
  }

  patrolDrop(pt, mem, pay) {
    mem.gone = true;
    const s = this.world.sessions.get(mem.id);
    if (s && s.partyPatrol === pt.id) s.partyPatrol = 0;
    if (mem.id === pt.leader) return this.patrolEnd(pt, false, 'leader');
    if (pay && s) {
      const row = this.patrolPay(pt, mem, false);
      s.send({ t: 'party.patrol.end', id: pt.id, complete: false, reason: 'quit', seconds: Math.round((this.clock() - pt.start) / 1000), rows: [row], driver: s.profile });
    }
    this.patrolPush(pt, { t: 'party.patrol.left', id: pt.id, name: mem.name });
    if (pt.members.every(x => x.gone)) this.patrols.delete(pt.id);
  }

  patrolPay(pt, mem, complete) {
    mem.paid = true;
    const row = { name: mem.name, posts: mem.posts, exp: 0, mito: 0 };
    const s = this.world.sessions.get(mem.id);
    if (!s || !s.profile || (mem.posts === 0 && !complete)) return row;
    const p = ensureProfile(s.profile);
    let r = { exp: pt.postExp * mem.posts + (complete ? pt.completeExp : 0), mito: pt.postMito * mem.posts };
    const extra = rewardExtras(p, 'patrol', r, {}, Math.random, this.gd);
    r = { exp: r.exp + extra.exp, mito: r.mito + extra.mito };
    p.Mito += r.mito;
    const levels = addExp(p, r.exp);
    s.lastClaim = s.lastClaim || {};
    s.lastClaim.patrol = this.clock() / 1000;
    s.save();
    Object.assign(row, { exp: r.exp, mito: r.mito, levelUps: levels });
    return row;
  }

  /** 3890000 for everyone: name, posts, EXP per member; the completion EXP (column 14) to each member on a full loop. */
  patrolEnd(pt, complete, reason) {
    if (!this.patrols.has(pt.id)) return;
    this.patrols.delete(pt.id);
    const rows = pt.members.map(mem => (mem.paid || mem.gone ? { name: mem.name, posts: mem.posts, exp: 0, mito: 0, gone: true } : this.patrolPay(pt, mem, complete)));
    const seconds = Math.round((this.clock() - pt.start) / 1000);
    for (const mem of pt.members) {
      const s = this.world.sessions.get(mem.id);
      if (s && s.partyPatrol === pt.id) s.partyPatrol = 0;
      if (s && !mem.gone) s.send({ t: 'party.patrol.end', id: pt.id, complete, reason, seconds, left: Math.floor(this.patrolSeconds(pt)), total: pt.done.size, rows, driver: s.profile });
    }
    this.log(`party patrol ${pt.id}: ${reason} (${pt.done.size}/${pt.maxPosts} posts)`);
  }

  patrolPush(pt, msg) { for (const mem of pt.members) if (!mem.gone) this.world.sessions.get(mem.id)?.send(msg); }

  // ---------------- party chase (0xc29 / 0x4f9dcd / PARTYCHASERESULT 980000) ----------------

  chaseOf(session) { const c = session.partyChase ? this.chases.get(session.partyChase) : null; if (session.partyChase && !c) session.partyChase = 0; return c; }

  /**
   * A solo chase start (the client's request, BS_PktChaseBeginConfirm 0x4f2500): the server decides the HUVs and their
   * speeds (src/huvrules.js) and the time limit; the client spawns them. The reward is claimed as before (kind chase).
   */
  chaseBegin(session, m) {
    if (!session.profile) return { ok: false, error: 'no driver' };
    if (this.chaseOf(session)) return { ok: false, error: 'You cannot join a party while chasing HUVs.' };
    const boss = m.boss === true;
    const plan = chaseUnits({ level: session.profile.Level | 0, members: 1, boss, huv: m.huv, hp: m.hp, rng: this.chaseRng });
    session.soloChase = { id: this.nextChase++, start: this.clock(), boss, units: plan.units.length, convoy: plan.convoy };
    this.log(`chase ${session.soloChase.id}: ${session.name} ${plan.units.length} HUV(s)${plan.convoy ? ' (convoy)' : ''}${boss ? ' (boss)' : ''}`);
    return { ok: true, id: session.soloChase.id, boss, seconds: plan.seconds, convoy: plan.convoy, units: plan.units };
  }

  /** The leader accepted the HUV call: the chase starts for every member in the leader's city (each client spawns the HUV). */
  chaseStart(session, m) {
    const party = this.world.liveParty(session);
    if (!party || party.members.size < 2) return { ok: false, error: 'You are not in a party.' };
    if (party.leader !== session.id) return { ok: false, error: 'Not a group leader' };   // 20022
    const ids = [party.leader, ...[...party.members].filter(id => id !== party.leader)];
    const members = ids.map(id => this.world.sessions.get(id)).filter(s => s && !s.closed && s.channel && session.channel && s.channel.city === session.channel.city && !s.race && !s.uc);
    if (members.length < 2) return { ok: false, error: 'Someone in your party is not ready.' };
    if (members.some(s => this.chaseOf(s) || this.patrolOf(s) || this.events.busy(s))) return { ok: false, error: 'You cannot join a party while chasing HUVs.' };   // 11061
    const boss = m.boss === true;
    const plan = chaseUnits({ level: session.profile ? session.profile.Level | 0 : 1, members: members.length, boss, huv: m.huv, hp: m.hp, rng: this.chaseRng });
    const hp = plan.units[0].hp;
    const seconds = plan.seconds;
    const now = this.clock();
    const bossId = boss && KEPT[m.bossId | 0] ? m.bossId | 0 : 0;
    const ch = {
      id: this.nextChase++, partyId: party.id, leader: session.id, boss, bossId, huv: Math.max(0, m.huv | 0), hp, max: hp, start: now,
      deadline: now + seconds * 1000, owner: session.id, huvAt: 0, huvSent: 0, convoy: plan.convoy,
      units: plan.units.map(u => ({ ...u, max: u.hp, out: true })), split: null,
      members: members.map(s => ({ id: s.id, name: s.name, damage: 0, hits: 0, lastHit: 0, budget: CHASE_HIT_CAP * 2, gone: false })),
    };
    this.chases.set(ch.id, ch);
    for (const s of members) s.partyChase = ch.id;
    // BS_PktPartyHuv's owner (0x4f0940): the leader's client runs the HUV, the others mirror it
    this.chasePush(ch, { t: 'chase.party.start', id: ch.id, boss, bossId, huv: ch.huv, hp, max: hp, seconds, convoy: plan.convoy, units: plan.units,
                         leader: session.name, owner: session.name, members: ch.members.map(x => x.name) });
    this.log(`party chase ${ch.id}: ${session.name} with ${members.length - 1} member(s), ${plan.units.length} HUV(s)${boss ? ' (boss)' : ''}${plan.convoy ? ' (convoy)' : ''}`);
    return { ok: true, id: ch.id };
  }

  /** A member's hits (batched by the client) on one unit: its health; the last unit stopped ends the chase for all. */
  chaseHit(session, m) {
    const ch = this.chaseOf(session);
    if (!ch) return { ok: false, error: 'not in a party chase' };
    const mem = ch.members.find(x => x.id === session.id && !x.gone);
    if (!mem) return { ok: false, error: 'not in a party chase' };
    const k = m.unit | 0;
    const unit = ch.units[k];
    if (!unit || !unit.out) return { ok: false, error: 'no such HUV' };
    if (unit.hp <= 0) return { ok: true, hp: 0, unit: k };
    const now = this.clock();
    // a budget of five 45-damage hits a second, refilled with time
    mem.budget = Math.min(CHASE_HIT_CAP * CHASE_HITS_PER_SECOND, mem.budget + (now - (mem.lastHit || ch.start)) / 1000 * CHASE_HIT_CAP * CHASE_HITS_PER_SECOND);
    mem.lastHit = now;
    const damage = Math.max(0, Math.min(mem.budget, Number(m.damage) || 0));
    if (damage <= 0) return { ok: false, error: 'too fast' };
    mem.budget -= damage;
    mem.damage += damage; mem.hits++;
    unit.hp = Math.max(0, unit.hp - damage);
    if (k === 0) ch.hp = unit.hp;
    this.chasePush(ch, { t: 'chase.party.hp', id: ch.id, unit: k, hp: unit.hp, max: unit.max, by: session.name });
    // 0x4a1860 / 0x4a1d10: the boss's damage threshold (+0x88) releases the kept escorts
    if (k === 0 && ch.bossId && !ch.split && unit.hp > 0 && splitDue(ch.bossId, 1 - unit.hp / unit.max)) {
      const kept = KEPT[ch.bossId];
      ch.split = { at: now, outAt: now + kept.outMs, out: false, finishAt: 0, finished: false };
      this.chasePush(ch, { t: 'chase.party.split', id: ch.id, stage: 'start', bossId: ch.bossId, seconds: kept.outMs / 1000 });
    }
    if (this.chaseDone(ch)) this.chaseEnd(ch, true);
    return { ok: true, hp: unit.hp, unit: k };
  }

  /** Every unit that is out has been stopped (a kept unit not yet out does not count). */
  chaseDone(ch) { return ch.units.every(u => !u.out || u.hp <= 0); }

  /** The kept escorts' timeline (0x4a1860: trailer stages, Lil' Grimer out, the truck finished 10 s later). */
  chaseSplitStep(ch, now) {
    const sp = ch.split;
    if (!sp) return;
    const kept = KEPT[ch.bossId];
    // stopped before its kept HUVs came out: they stay in it
    if (!sp.out && ch.units[0].hp <= 0) { ch.split = null; return; }
    if (!sp.out && now >= sp.outAt) {
      sp.out = true;
      const boss = ch.units[0];
      const made = kept.units.map(u => {
        // the kept HUV's health against the boss's: BossHuvInfo 내구력 (11002 300 of 11001's 1400)
        const hp = Math.max(1, Math.round(boss.max * u.durability / kept.durability * 10) / 10);
        ch.units.push({ huv: u.id, kmh: 0, top: 0, boostSeconds: 0, turbo: 0, hp, max: hp, out: true, kept: u.id });
        return { unit: ch.units.length - 1, huv: u.id, hp };
      });
      this.chasePush(ch, { t: 'chase.party.split', id: ch.id, stage: 'out', bossId: ch.bossId, units: made, escorts: kept.escorts });
      if (kept.finishMs > 0) sp.finishAt = now + kept.finishMs;
    }
    if (sp.out && sp.finishAt && !sp.finished && now >= sp.finishAt) {
      // 0x4a1860 state 7: the truck is finished (damage = max + 10)
      sp.finished = true;
      const boss = ch.units[0];
      if (boss.hp > 0) {
        boss.hp = 0; ch.hp = 0;
        this.chasePush(ch, { t: 'chase.party.hp', id: ch.id, unit: 0, hp: 0, max: boss.max, by: '' });
      }
      this.chasePush(ch, { t: 'chase.party.split', id: ch.id, stage: 'finish', bossId: ch.bossId });
    }
    if (this.chases.has(ch.id) && this.chaseDone(ch)) this.chaseEnd(ch, true);
  }

  /** The owner's HUV states (the HUV, then its escorts): relayed to the other members (their remote copies). */
  chaseHuv(session, m) {
    const ch = this.chaseOf(session);
    if (!ch) return { ok: false, error: 'not in a party chase' };
    if (ch.owner !== session.id) return { ok: false, error: 'not the HUV owner' };
    const now = this.clock();
    // a budget of CHASE_HUV_RATE states a second
    if (now - ch.huvAt >= 1000) { ch.huvAt = now; ch.huvSent = 0; }
    if (++ch.huvSent > CHASE_HUV_RATE) return { ok: false, error: 'too fast' };
    const cars = (Array.isArray(m.cars) ? m.cars : []).slice(0, CHASE_HUV_CARS).map(c => (Array.isArray(c) && c.length >= 5 && c.slice(0, 5).every(v => Number.isFinite(v))
      ? c.slice(0, 5).map(v => Math.round(v * 100) / 100) : null));
    const msg = { t: 'chase.party.huv', id: ch.id, cars };
    for (const mem of ch.members) if (!mem.gone && mem.id !== session.id) this.world.sessions.get(mem.id)?.send(msg);
    return { ok: true };
  }

  chaseLeft(session) {
    const ch = this.chaseOf(session);
    if (!ch) return;
    const mem = ch.members.find(x => x.id === session.id);
    if (mem && !mem.gone) this.chaseDrop(ch, mem);
  }

  chaseDrop(ch, mem) {
    mem.gone = true;
    const s = this.world.sessions.get(mem.id);
    if (s && s.partyChase === ch.id) s.partyChase = 0;
    if (ch.members.every(x => x.gone)) { this.chases.delete(ch.id); return; }
    // the owner left: the next member still in it runs the HUV from where its copy is
    if (ch.owner === mem.id) {
      const next = ch.members.find(x => !x.gone);
      ch.owner = next.id;
      this.chasePush(ch, { t: 'chase.party.owner', id: ch.id, owner: next.name });
    }
  }

  /** 0x4f9dcd for everyone: each member still in it is paid (chase formula + party bonus) and sees every line. */
  chaseEnd(ch, success) {
    if (!this.chases.has(ch.id)) return;
    this.chases.delete(ch.id);
    const seconds = Math.round((this.clock() - ch.start) / 100) / 10;
    const rows = [];
    for (const mem of ch.members) {
      const s = this.world.sessions.get(mem.id);
      const row = { name: mem.name, level: s && s.profile ? s.profile.Level | 0 : 0, exp: 0, mito: 0, levelUps: 0, damage: Math.round(mem.damage) };
      if (success && !mem.gone && s && s.profile) {
        const r = this.economy.grantChase(s, ch.boss);
        Object.assign(row, { exp: r.exp, mito: r.mito, levelUps: r.levelUps, level: s.profile.Level | 0 });
        if (r.drop) row.drop = r.drop;
        s.save();
      }
      rows.push(row);
    }
    for (const mem of ch.members) {
      const s = this.world.sessions.get(mem.id);
      if (s && s.partyChase === ch.id) s.partyChase = 0;
      if (s && !mem.gone) s.send({ t: 'chase.party.end', id: ch.id, success, seconds, rows, driver: s.profile });
    }
    this.log(`party chase ${ch.id}: ${success ? 'caught' : 'failed'} after ${seconds} s`);
  }

  chasePush(ch, msg) { for (const mem of ch.members) if (!mem.gone) this.world.sessions.get(mem.id)?.send(msg); }
}

module.exports = { PartyPlay, PARTY_BONUS_LEVEL, PARTY_BONUS_STEP, PATROL_START_SECONDS, PATROL_BONUS_SECONDS, HUV_KINDS, CHASE_SECONDS, CHASE_BOSS_SECONDS, CHASE_HUV_RATE };
