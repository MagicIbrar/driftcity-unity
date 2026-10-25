// Player-vs-player race rooms (the client's Battle Zone, DriftCity_RE/PVP.md). The server owns the room lifecycle:
// lobby (wait room) -> countdown -> racing -> results -> back to the wait room (persistent rooms) or closed (legacy
// rooms). It orders finishers by server receipt time, checks the checkpoint gates (init\pvptrackinfo.xlt, src/pvptracks.js)
// and the lap / finish times against the track length, pays place-based rewards, keeps the ladder points, settles wager
// rooms, runs the AI drivers it adds to rooms, the spectators, the room and lobby chat, invites and the post-race reward
// pick (4310000).
//
// Rooms carry the client's room settings (CREATEROOM 1020000): title, single/team mode, 8 or 16 slots, password,
// spectating, no-gate, arena (lobby tab), wager grade and the wait room's map settings word. Slots follow the client's
// slot array (0 open, -1 closed, else the player's id; AI drivers >= 0x10000); team = slot parity (even = team A, odd = B).
'use strict';

const { gatePoint, prevPoint, dist } = require('./pvptracks');
const items = require('./items');
const { addExp } = require('./economy');
const { cleanText } = require('./world');

const MAX_SLOTS = 16;
const LEGACY_MAX = 8;              // rooms hosted without settings (protocol 1 clients)
// the server start time T of 0x1ea: the client runs the "pvpready" grid camera until T-19.5 s, "pvpstart" until T-6 s and
// the countdown (pvp_countdownstart) for the last 6 s (0x75fc10)
const COUNTDOWN_MS = 20000;
const MAX_RACE_MS = 15 * 60 * 1000;
const GRACE_MS = 30000;
const MAX_AVG_SPEED = 75;          // m/s (~270 km/h average) - faster finishes/laps/gates are rejected
const MIN_TRACK_LENGTH = 1500;     // m per lap; without the track table a declared length is never shorter
const REWARD_COOLDOWN_MS = 60000;  // per account
const READY_COOLDOWN_MS = 1000;
const HOST_KICK_MS = 16000;        // the client warns the host at 11..15 s (text 10241) once everyone is ready
// arena tabs 0..6 = 2X Amateur, 3X Rookie, 4X Pro, 6X Star, 8X Superstar, Free, Real Match (0x57e750)
const ARENA_EXP = [2, 3, 4, 6, 8, 1, 1];
const ARENA_RM = 6;
const LADDER_START = 1000;
const LADDER_MIN_X8 = 12;          // globalcontent LadderCalcNumX8 (US): fewest racers for ladder points in the 8X arena
// wager grades (room flags 0x7000 >> 12, 0x507330): 1 S .. 5 D; a driver needs 10x the fee (11066 / 11067)
const WAGER_FEES = [0, 10000, 5000, 1000, 500, 100];
const WAGER_TAX_PCT = 10;          // RESULT_SINGLEBB 11081 "(Wager Tax: 10%)"
const WAGER_SHARES = [60, 30, 10];   // remake rule: percent of the pot after tax for the 1st / 2nd / 3rd finisher
const MAX_SPECTATORS = 16;         // remake rule (the client has no limit)
// replays (PVP.md 14.2): the client records its race as a packet stream (play_record.r0, 0x73e1e0); the remake keeps
// each driver's pose from GO, 4 a second, for at most 15 minutes, and a crew round's recording goes with its replay row
const REPLAY_MS = 250, REPLAY_MAX_S = 900;
const INVITE_COOLDOWN_MS = 5000;   // WAIT16 INVITE (0x576d10)
const PICK_MS = 10000;             // 4310000: unpicked cards are chosen by the server (11799)
const PICK_CARDS = 16;
const AI_BASE_ID = 0x10000;        // the client's slot array marks AI drivers with ids >= 0x10000
// Real Match (packet 0x1c8): a 50-minute session (type 2 "remaining = value - elapsed + 3000 s"); the remake opens one
// every RM_EVERY_H hours on the UTC clock
const RM_MINUTES = 50, RM_EVERY_H = 3;
const AI_NAMES = ['Rio', 'Duke', 'Mika', 'Jin', 'Sora', 'Kai', 'Nova', 'Taro', 'Yuna', 'Leon', 'Hana', 'Max', 'Zed', 'Aki', 'Rex',
  'Luna', 'Ivan', 'Mei', 'Toby', 'Ken', 'Sasha', 'Bo', 'Remi', 'Gus'];

/**
 * The gate-effect draw (PVP.md 15.2, the client's PvpGateEffects.Draw): place p of n, r uniform in [0, 1). Types are the
 * PlayerEvent ones: 0 pvp_null, 1 boostcharge100, 2 boostcharge50, 3 skidrush, 5 untouchable, 6 booost, 7 team half charge.
 */
function gateEffectFor(place, n, team, r) {
  const f = n > 1 ? Math.min(1, Math.max(0, (place - 1) / (n - 1))) : 0;
  let t;
  // types as the client numbers them (UI effects): 1 half charge, 2 full charge, 3 skid rush, 5 untouchable, 6 booost, 7 team half
  if (f < 0.34) t = r < 0.6 ? 0 : r < 0.85 ? 1 : 3;
  else if (f <= 0.66) t = r < 0.3 ? 0 : r < 0.6 ? 1 : r < 0.8 ? 2 : 3;
  else t = r < 0.1 ? 0 : r < 0.3 ? 1 : r < 0.6 ? 2 : r < 0.8 ? 6 : 5;
  if (team && t === 1) t = 7;
  return t;
}

/** EXP factor of an arena: the tab's multiplier over the Amateur arena's 2X (no arena = 1). */
function arenaExpFactor(arena) {
  return Number.isInteger(arena) && arena >= 0 && arena < ARENA_EXP.length ? ARENA_EXP[arena] / 2 : 1;
}

/** Ladder points for a place in a field: +10 for the winner down to -10 for the last (remake rule, PVP.md). */
function ladderDelta(place, field) {
  if (field < 2) return 0;
  return Math.round(10 * (field + 1 - 2 * Math.min(place, field)) / (field - 1));
}

/**
 * Wager payouts (remake rule; the client only shows the server's deltas, 0x1f3): the pot is every driver's fee, the
 * house keeps 10%. Single: 60 / 30 / 10% to the first three finishers (a share without a finisher goes to the winner).
 * Team: the winning team shares the pot evenly; a draw refunds everyone minus the tax. DNF gets nothing.
 * entries: [{ key, place (0 = DNF), team }] -> Map key -> payout.
 */
function wagerPayouts(entries, fee, team, winner) {
  const pot = Math.floor(fee * entries.length * (100 - WAGER_TAX_PCT) / 100);
  const out = new Map(entries.map(e => [e.key, 0]));
  if (team) {
    const win = winner === 2 ? entries.filter(e => e.team >= 0) : entries.filter(e => e.team === winner);
    if (win.length) for (const e of win) out.set(e.key, Math.floor(pot / win.length));
    return out;
  }
  const fin = entries.filter(e => e.place > 0).sort((a, b) => a.place - b.place);
  if (!fin.length) return out;
  let paid = 0;
  for (let i = 1; i < Math.min(3, fin.length); i++) { const v = Math.floor(pot * WAGER_SHARES[i] / 100); out.set(fin[i].key, v); paid += v; }
  out.set(fin[0].key, pot - paid);
  return out;
}

/**
 * The post-race cards (4310000): drawn by probability from init
ewardgroup.xlt "Battle_Result" groups rw_battle_low /
 * _mid / _high (remake rule: 2X-3X arenas low, 4X-6X mid, 8X high). Card types as the client draws them (0x58e1b0):
 * 1 item (11197.. item name), 3 Mito (11197 "%d Mito Received"), 5 EXP (11198 "%d EXP Received"). Without the table:
 * Mito and EXP cards scaled by the arena.
 */
function pickCards(arena, rng = Math.random, groups = null) {
  const cards = [];
  const group = groups && groups.get(arena >= 4 ? 'rw_battle_high' : arena >= 2 ? 'rw_battle_mid' : 'rw_battle_low');
  if (group && group.length) {
    const total = group.reduce((a, r) => a + Math.max(0, r.prob), 0);
    for (let i = 0; i < PICK_CARDS; i++) {
      let x = rng() * total, row = group[group.length - 1];
      for (const r of group) { x -= Math.max(0, r.prob); if (x < 0) { row = r; break; } }
      if (row.type === 'Exp') cards.push({ type: 5, amount: row.count });
      else if (row.type === 'Mito') cards.push({ type: 3, amount: row.count });
      else cards.push({ type: 1, item: row.item, amount: Math.max(1, row.count) });
    }
    return cards;
  }
  const f = arenaExpFactor(arena);
  const mito = [100, 200, 300, 500, 1000, 2000, 5000], exp = [50, 100, 150, 200, 300, 500];
  for (let i = 0; i < PICK_CARDS; i++) {
    const r = rng();
    // the big cards are rare: index from a squared random number
    if (i % 2 === 0) cards.push({ type: 3, amount: mito[Math.min(mito.length - 1, Math.floor(r * r * mito.length))] });
    else cards.push({ type: 5, amount: Math.round(exp[Math.min(exp.length - 1, Math.floor(r * r * exp.length))] * f) });
  }
  for (let i = cards.length - 1; i > 0; i--) { const j = Math.floor(rng() * (i + 1)); [cards[i], cards[j]] = [cards[j], cards[i]]; }
  return cards;
}

/** A stand-in session for an AI driver: it never receives anything. */
function aiSession(id, name) { return { id, name, ai: true, send() {}, profile: null }; }

class RaceRoom {
  constructor(id, host, track, s) {
    this.id = id;
    this.host = host.id;
    this.channel = host.channel;
    this.track = track;            // { key, name, laps, length }
    this.title = s.title;
    this.team = s.team;
    this.max = s.max;
    this.password = s.password;
    this.spectate = s.spectate;
    this.noGate = s.noGate;
    this.arena = s.arena;
    this.mapFlags = s.mapFlags;
    this.persist = s.persist;
    this.wager = s.wager || 0;     // grade 1..5 (S..D), 0 = no wager
    this.mission = s.mission || 0; // mission-battle room (flag 0x10000): the GM's MissionBattle mode 1..4, 0 = none
    // crew battle (SOCIAL.md section 5): [crew id of side A (even slots), crew id of side B (odd slots)], 0 = open
    this.crews = s.crew ? [s.crew.crewId, 0] : null;
    this.crewNames = s.crew ? [s.crew.crew, ''] : null;
    this.slots = new Array(MAX_SLOTS).fill(0).map((_, i) => (i < this.max ? 0 : -1));
    this.state = 'lobby';
    this.racers = new Map();       // sessionId (AI: its id) -> { session, accountId, name, level, ready, slot, lap, maxLap, lapAt, idx, n, gate, gateAt, finished, time, dnf, ai }
    this.watchers = new Map();     // sessionId -> { session, name } (spectators, 0x1de / 0x1bf)
    this.invited = new Set();      // session ids invited by a member (join without the password)
    this.startAt = 0;
    this.startCount = 0;
    this.finishOrder = [];
    this.gt = null;                // the pvptrackinfo track of the running race (checkpoint gates)
  }

  get humans() { return [...this.racers.values()].filter(r => !r.ai); }
  get realMatch() { return this.arena === ARENA_RM; }

  info() {
    return {
      id: this.id, host: this.host, state: this.state, track: this.track,
      title: this.title, team: this.team, max: this.max, locked: !!this.password, spectate: this.spectate,
      noGate: this.noGate, arena: this.arena, mapFlags: this.mapFlags, persist: this.persist, wager: this.wager, mission: this.mission,
      crew: this.crews ? { a: this.crewNames[0], b: this.crewNames[1] } : null,
      observers: this.watchers.size, observerNames: [...this.watchers.values()].map(w => w.name),
      slots: this.slots.slice(),
      racers: [...this.racers.values()].map(r => this.racerInfo(r)),
    };
  }

  racerInfo(r) {
    const o = {
      id: r.session.id, name: r.name, level: r.level, ready: r.ready || r.session.id === this.host, slot: r.slot,
      team: r.slot & 1, lap: r.lap, idx: r.idx, finished: r.finished, time: r.time, gate: r.gate | 0, p: this.progressOf(r),
    };
    if (r.ai) { o.ai = true; o.car = r.car; o.skill = r.skill; }
    return o;
  }

  /** Race progress 0..1 (finished = 1): the gates passed, else the client's lap and line index; AI from their clock. */
  progressOf(r) {
    if (r.finished && !r.dnf) return 1;
    const laps = Math.max(1, this.track.laps);
    if (r.ai) return r.finishAt && this.startAt ? Math.max(0, Math.min(0.999, (Date.now() - this.goAt) / (r.finishAt - this.goAt))) : 0;
    const frac = r.n > 0 ? Math.max(0, Math.min(0.999, r.idx / r.n)) : 0;
    if (this.gt) {
      // between the last gate passed and the next one, ordered by the line position
      const total = laps * this.gt.per, lo = r.gate / total, hi = (r.gate + 1) / total;
      return Math.min(0.999, Math.max(lo, Math.min(hi - 1e-6, (r.lap + frac) / laps)));
    }
    return Math.min(0.999, (r.lap + frac) / laps);
  }

  /** Everyone in the room: racers (not AI) and spectators. */
  send(msg) {
    for (const r of this.racers.values()) if (!r.ai) r.session.send(msg);
    for (const w of this.watchers.values()) w.session.send(msg);
  }

  clearTimers() {
    clearTimeout(this.timer); clearTimeout(this.deadline); clearTimeout(this.grace); clearTimeout(this.kick);
    clearInterval(this.recTimer); this.recTimer = null;
    for (const r of this.racers.values()) if (r.aiTimer) { clearTimeout(r.aiTimer); r.aiTimer = null; }
    this.timer = this.deadline = this.grace = this.kick = null;
  }

  /**
   * First open slot. Team mode: on the given team (0 = even slots, 1 = odd slots), or with no team given on the side
   * with fewer players (ties: team A), falling back to any open slot. -1 when nothing is open.
   */
  openSlot(team) {
    const free = parity => { for (let i = 0; i < this.max; i++) if (this.slots[i] === 0 && (parity === undefined || (i & 1) === parity)) return i; return -1; };
    if (!this.team) return free(undefined);
    if (team === 0 || team === 1) return free(team);
    const a = [...this.racers.values()].filter(r => (r.slot & 1) === 0).length;
    const side = a <= this.racers.size - a ? 0 : 1;
    const i = free(side);
    return i >= 0 ? i : free(undefined);
  }
}

class Races {
  /**
   * opts: { clock, countdownMs, minTrackLength, rewardCooldownMs, hostKickMs, maxAvgSpeed, tracks (Map of src/pvptracks.js,
   * empty = no gate checks), gamedata, aiSpeed (m/s, tests), rmAlways, pickMs, rng } (tests shorten the waits)
   */
  constructor(world, economy, store, log = () => {}, opts = {}) {
    this.world = world; this.economy = economy; this.store = store; this.log = log;
    this.clock = opts.clock || Date.now;
    this.countdownMs = opts.countdownMs ?? COUNTDOWN_MS;
    this.minTrackLength = opts.minTrackLength ?? MIN_TRACK_LENGTH;
    this.rewardCooldownMs = opts.rewardCooldownMs ?? REWARD_COOLDOWN_MS;
    this.hostKickMs = opts.hostKickMs ?? HOST_KICK_MS;
    this.maxAvgSpeed = opts.maxAvgSpeed ?? MAX_AVG_SPEED;   // tests raise it to finish races at once
    this.tracks = opts.tracks || new Map();
    this.gd = opts.gamedata || null;
    this.aiSpeed = opts.aiSpeed || 0;
    this.rmAlways = !!opts.rmAlways;
    this.pickMs = opts.pickMs ?? PICK_MS;
    this.replayMs = opts.replayMs || 0;            // tests sample the replay poses faster
    this.replays = opts.replays !== false;
    this.rng = opts.rng || Math.random;
    this.rewardGroups = opts.rewardGroups || null;
    this.gateEffects = opts.gateEffects !== false && process.env.DC_GATE_EFFECTS !== '0';
    this.tourSets = [0, 0, 0, 0];   // TOUR_REWARD GM: reward table a..d per championship class
    this.rooms = new Map();
    this.nextId = 1;
    this.nextAi = AI_BASE_ID;
    this.lastReward = new Map();   // accountId -> ms
    this.rmWasOpen = null; this.rmWarned = 0;
    this.rmTimer = setInterval(() => { try { this.rmTick(); } catch (err) { this.log(`real match tick: ${err.message}`); } }, 15000);
    if (this.rmTimer.unref) this.rmTimer.unref();
  }

  /** The new messages (server.js passes them here); race.host/join/... keep their own switch lines. */
  owns(t) { return /^race\.(observe|chat|lobby|invitees|invite|find|pick|gate|rm|gm)$/.test(t); }

  handle(session, m) {
    switch (m.t) {
      case 'race.observe': return this.observe(session, m);
      case 'race.chat': return this.chat(session, m);
      case 'race.lobby': session.pvpLobby = Number.isInteger(m.arena) ? m.arena : -1; return { ok: true };
      case 'race.invitees': return this.invitees(session);
      case 'race.invite': return this.invite(session, m.name);
      case 'race.find': return this.find(session, m.name);
      case 'race.pick': return this.pick(session, m.slot);
      case 'race.gate': this.gate(session, m); return undefined;
      case 'race.gm': return this.gm(session, m);
      case 'race.rm': return { ok: true, ...this.rmState() };
    }
    return { ok: false, error: 'unknown message' };
  }

  list(session, arena) {
    if (Number.isInteger(arena)) session.pvpLobby = arena;
    return [...this.rooms.values()]
      .filter(r => r.channel === session.channel && (!Number.isInteger(arena) || r.arena === arena))
      .map(r => r.info());
  }

  broadcastList(session) { if (session.channel) this.world.broadcast(session.channel, { t: 'race.rooms', rooms: this.list({ channel: session.channel }) }); }

  parseTrack(t) {
    t = t && typeof t === 'object' ? t : {};
    if (typeof t.key !== 'string' || !/^[a-z]+\/[a-z0-9_-]{1,32}$/i.test(t.key)) return null;
    const declared = Number(t.length);
    const gt = this.tracks.get(t.key.toLowerCase());
    const out = {
      key: t.key, name: String(t.name || t.key).slice(0, 40), laps: Math.max(1, Math.min(5, t.laps | 0)),
      length: Math.max(this.minTrackLength, Math.min(20000, Number.isFinite(declared) ? declared : 0)),
    };
    // the table knows the track: its lap count, and its gates bound the length from below
    if (gt) { out.laps = gt.laps; out.length = Math.max(out.length, Math.round(gt.lapLength)); }
    return out;
  }

  /** The profile's Mito (0 without one). */
  static mito(session) { return session.profile ? Number(session.profile.Mito) || 0 : 0; }

  host(session, m) {
    if (!session.channel) return { ok: false, error: 'not in a city' };
    if (session.race) return { ok: false, error: 'already in a race' };
    const track = this.parseTrack(m.track);
    if (!track) return { ok: false, error: 'bad track' };
    // CREATEROOM rules (0x57a3a0): title up to 24 characters, password 1..8, 8 or 16 players (practice: 1)
    const s = m.room && typeof m.room === 'object' ? m.room : null;
    const settings = { title: '', team: false, max: LEGACY_MAX, password: '', spectate: true, noGate: false, arena: -1, mapFlags: 0, persist: false, wager: 0, mission: 0 };
    if (s) {
      settings.title = String(s.title || '').replace(/[\u0000-\u001f]/g, '').slice(0, 24);
      // 0x57a3ee: a title with a slang entry -> 10608 "That Room name is not allowed." (src/namefilter.js)
      if (settings.title && this.world.names && this.world.names.roomTitle(settings.title)) return { ok: false, error: 'That Room name is not allowed.  Please use a different name.', code: 10608 };
      settings.team = !!s.team;
      settings.max = s.max === 1 ? 1 : s.max === 8 ? 8 : 16;
      if (s.password !== undefined && s.password !== null && s.password !== '') {
        if (typeof s.password !== 'string' || s.password.length > 8) return { ok: false, error: 'bad password' };
        settings.password = s.password;
      }
      // ALLOWOBSERVE (0x57ca12): a room without a password can always be spectated; with one the host chooses
      settings.spectate = !settings.password || s.spectate !== false;
      settings.noGate = !!s.noGate;
      settings.arena = Number.isInteger(s.arena) && s.arena >= 0 && s.arena < ARENA_EXP.length ? s.arena : -1;
      settings.mapFlags = (s.mapFlags | 0) & 0xfff;
      settings.persist = true;
      if (Number.isInteger(s.wager) && s.wager >= 1 && s.wager <= 5) {
        // 11066 "If you want to select this wager level, you must have %d Mito."
        const need = WAGER_FEES[s.wager] * 10;
        if (Races.mito(session) < need) return { ok: false, error: 'not enough mito', need, code: 11066 };
        settings.wager = s.wager;
      }
      // mission-battle rooms (flag 0x10000): only while the host (an admin) has the GM MissionBattle switch on (race.gm)
      if (Number.isInteger(s.mission) && s.mission >= 1 && s.mission <= 4 && session.admin && session.missionBattle) settings.mission = s.mission;
      if (s.crew) {
        // a crew battle is a team race between two crews; the host's crew takes side A
        const c = this.teams ? this.teams.battleHost(session) : { ok: false, error: 'crews are not available' };
        if (!c.ok) return c;
        settings.team = true;
        settings.crew = c;
      }
    }
    if (!settings.title) settings.title = `${session.name}'s race`;
    const room = new RaceRoom(this.nextId++, session, track, settings);
    this.rooms.set(room.id, room);
    this.join(session, room.id, settings.password);
    this.broadcastList(session);
    return { ok: true, id: room.id };
  }

  /** m: a room id (protocol 1) or { id, password, invite }. A spectator of the room joins as a player (JOINGAME, 0x1dc). */
  join(session, m, password) {
    const id = m && typeof m === 'object' ? m.id : m;
    if (m && typeof m === 'object') password = m.password;
    const room = this.rooms.get(id | 0);
    if (!room || room.channel !== session.channel) return { ok: false, error: 'race not found' };
    if (room.state !== 'lobby') return { ok: false, error: 'race already started' };
    if (room.racers.has(session.id)) return { ok: true, id: room.id };
    const watching = room.watchers.has(session.id);
    if (room.password && password !== room.password && !watching && !room.invited.has(session.id)) return { ok: false, error: 'wrong password' };
    // mission-battle rows can't be joined from the list (an invite brings a driver in); 11480 refuses a party member
    if (room.mission && !room.invited.has(session.id)) return { ok: false, error: 'mission battle room', code: 10030 };
    if (room.mission && session.party && this.world && this.world.parties && (this.world.parties.get(session.party)?.members?.size || 0) > 1) return { ok: false, error: 'in a party', code: 11480 };
    if (room.wager && Races.mito(session) < WAGER_FEES[room.wager] * 10) return { ok: false, error: 'not enough mito', need: WAGER_FEES[room.wager] * 10, code: 11067 };
    let side;
    if (room.crews) {
      // crew battle sides (src/channelbattle.js sideFor): the channel owner defends on the even slots, the crew at the
      // head of the challenger queue attacks on the odd slots
      const c = this.teams ? this.teams.battleCrew(session) : null;
      if (!c) return { ok: false, error: "You don't have a Crew" };
      const sf = this.teams.cb.sideFor(room, c, session);
      if (sf.ok === false) return sf;
      if (sf.swap) this.swapCrewSides(room);
      side = sf.side;
      if (room.openSlot(side) < 0) return { ok: false, error: 'team is full' };
      if (!room.crews[side]) { room.crews[side] = c.crewId; room.crewNames[side] = c.crew; }
    }
    const slot = room.openSlot(side);
    if (slot < 0 || room.racers.size >= room.max) return { ok: false, error: 'race is full' };
    if ((session.race && session.race !== room) || session.lap) this.leave(session);
    room.watchers.delete(session.id);
    room.invited.delete(session.id);
    room.slots[slot] = session.id;
    room.racers.set(session.id, {
      session, accountId: session.accountId, name: session.name, level: (session.profile && session.profile.Level) | 0,
      ready: false, readyAt: 0, slot, lap: 0, maxLap: 0, lapAt: 0, idx: 0, n: 0, gate: 0, gateAt: 0, finished: false, time: 0,
    });
    session.race = room;
    this.changed(room);
    return { ok: true, id: room.id };
  }

  /** Quick join (lobby FILTER_SINGLE/TEAM, 0x574e90): the first open room of the mode in the arena. */
  quick(session, m) {
    if (!session.channel) return { ok: false, error: 'not in a city' };
    const team = !!m.team, arena = Number.isInteger(m.arena) ? m.arena : undefined;
    const room = [...this.rooms.values()].find(r => r.channel === session.channel && r.state === 'lobby' && r.persist && !r.password &&
      r.team === team && (arena === undefined || r.arena === arena) && r.racers.size < r.max && r.openSlot(undefined) >= 0 &&
      (!r.wager || Races.mito(session) >= WAGER_FEES[r.wager] * 10));
    if (!room) return { ok: false, error: 'no room' };
    return this.join(session, room.id, '');
  }

  /**
   * Spectating (0x1de from the lobby, 0x1db from the wait room). From the lobby: a room without a password, or a
   * password room that allows spectators, is watched at once; otherwise the password is needed. A guest of the room who
   * is not ready becomes a spectator. A running race is watched from where it is.
   */
  observe(session, m) {
    if (!session.channel) return { ok: false, error: 'not in a city' };
    const cur = session.race;
    if (cur && cur.racers.has(session.id) && (m.id === undefined || (m.id | 0) === cur.id)) {
      const r = cur.racers.get(session.id);
      if (cur.state !== 'lobby') return { ok: false, error: 'race already started' };
      if (cur.host === session.id) return { ok: false, error: 'the host cannot spectate' };
      if (r.ready) return { ok: false, error: 'not while ready' };
      if (cur.watchers.size >= MAX_SPECTATORS) return { ok: false, error: 'too many spectators' };
      cur.racers.delete(session.id);
      if (cur.slots[r.slot] === session.id) cur.slots[r.slot] = 0;
      cur.watchers.set(session.id, { session, name: session.name });
      this.changed(cur);
      this.broadcastList(session);
      return { ok: true, id: cur.id };
    }
    const room = this.rooms.get(m.id | 0);
    if (!room || room.channel !== session.channel) return { ok: false, error: 'race not found' };
    if (room.watchers.has(session.id)) return { ok: true, id: room.id };
    if (room.password && !room.spectate && m.password !== room.password) return { ok: false, error: 'wrong password' };
    if (room.watchers.size >= MAX_SPECTATORS) return { ok: false, error: 'too many spectators' };
    if (session.race || session.lap) this.leave(session);
    room.watchers.set(session.id, { session, name: session.name });
    session.race = room;
    this.changed(room);
    if (room.state === 'countdown' || room.state === 'racing') {
      session.send(this.startMessage(room));
      if (room.state === 'racing') session.send({ t: 'race.go', id: room.id });
      session.send({ t: 'race.standings', racers: room.info().racers });
    }
    this.broadcastList(session);
    return { ok: true, id: room.id, state: room.state };
  }

  leave(session, reason) {
    if (this.laps && session.lap) this.laps.leave(session);   // the lap-time battle ends with a room, a disconnect or a channel change
    const room = session.race;
    if (!room) return;
    session.race = null;
    if (room.watchers.delete(session.id)) {
      this.changed(room);
      if (reason) session.send({ t: 'race.kicked', id: room.id, reason });
      session.send({ t: 'race.room', room: null });
      if (session.channel) this.broadcastList(session);
      return;
    }
    const r = room.racers.get(session.id);
    room.racers.delete(session.id);
    if (r && room.slots[r.slot] === session.id) room.slots[r.slot] = 0;
    if (room.state === 'racing' && r && !r.finished) r.dnf = true;
    if (room.crews && room.state === 'lobby' && ![...room.racers.values()].some(x => x.slot & 1)) { room.crews[1] = 0; room.crewNames[1] = ''; }
    if (room.crews && r && this.teams) {
      const c = this.teams.memberOf(session.accountId);
      try { this.teams.cb.battleLeft(room, r, c ? c.crew_id : 0); } catch (err) { this.log(`race ${room.id}: crew leave failed: ${err.message}`); }
    }
    if (room.humans.length === 0) {
      // nobody left: no timer may fire later and write results for this room
      room.clearTimers();
      if (room.state !== 'done' && room.finishOrder.some(x => !x.ai) && (room.state === 'racing')) this.finishRoom(room, true);
      room.state = 'done';
      for (const w of room.watchers.values()) { if (w.session.race === room) w.session.race = null; w.session.send({ t: 'race.room', room: null }); }
      room.watchers.clear();
      this.rooms.delete(room.id);
    } else {
      if (room.host === session.id) room.host = room.humans[0].session.id;
      this.changed(room);
      this.maybeFinish(room);
    }
    if (reason) session.send({ t: 'race.kicked', id: room.id, reason });
    session.send({ t: 'race.room', room: null });
    if (session.channel) this.broadcastList(session);
  }

  /** Room changed: everyone gets the new room info; the idle-host timer restarts. */
  changed(room) {
    room.send({ t: 'race.room', room: room.info() });
    this.armHostKick(room);
  }

  /** Persistent rooms: once everyone is ready the host has HOST_KICK_MS to start, else the host is removed. */
  armHostKick(room) {
    clearTimeout(room.kick); room.kick = null;
    if (!room.persist || room.state !== 'lobby' || room.racers.size < 2 || !this.hostKickMs) return;
    if (![...room.racers.values()].every(r => r.ready || r.ai || r.session.id === room.host)) return;
    room.kick = setTimeout(() => {
      room.kick = null;
      const h = room.racers.get(room.host);
      if (h && room.state === 'lobby') { this.log(`race ${room.id}: idle host ${h.name} removed`); this.leave(h.session, 'host did not start'); }
    }, this.hostKickMs + Math.max(0, (room.kickHoldUntil || 0) - Date.now()));
    if (room.kick.unref) room.kick.unref();
  }

  /** The wager and Real Match checks of START / READY (0x579560 / 0x579740). */
  startBlock(room, sessions) {
    if (room.realMatch && !this.rmState().open) return { ok: false, error: 'Real Match Time has Ended.', code: 10850 };
    if (room.wager) {
      const need = WAGER_FEES[room.wager] * 10;
      const poor = sessions.find(s => Races.mito(s) < need);
      if (poor) return { ok: false, error: 'not enough mito', need, code: 11067, name: poor.name };
    }
    return null;
  }

  ready(session, value) {
    const room = session.race;
    if (!room || room.state !== 'lobby') return { ok: false, error: 'not in a race lobby' };
    const r = room.racers.get(session.id);
    if (!r) return { ok: false, error: 'spectators cannot get ready' };
    const now = Date.now();
    if (now - r.readyAt < READY_COOLDOWN_MS) return { ok: false, error: 'too fast' };
    if (value) { const b = this.startBlock(room, [session]); if (b) return b; }
    r.readyAt = now;
    r.ready = !!value;
    this.changed(room);
    return { ok: true };
  }

  /** Wait-room settings from the host: track and the map-settings word (0x7853f0). */
  settings(session, m) {
    const room = session.race;
    if (!room || room.state !== 'lobby') return { ok: false, error: 'not in a race lobby' };
    if (room.host !== session.id) return { ok: false, error: 'only the host can change the room' };
    if (m.track !== undefined) {
      const track = this.parseTrack(m.track);
      if (!track) return { ok: false, error: 'bad track' };
      room.track = track;
    }
    if (m.mapFlags !== undefined) room.mapFlags = (m.mapFlags | 0) & 0xfff;
    this.changed(room);
    this.broadcastList(session);
    return { ok: true };
  }

  /** An AI driver for a slot (AI_BTN, 0x57d9f0: BattleRoomAI and arena tab <= 4; not in wager rooms in the remake). */
  addAi(room, slot, force = false) {
    if (!force && !(room.arena >= 0 && room.arena <= 4) && room.persist) return { ok: false, error: 'no AI drivers in this arena' };
    if (!force && room.wager) return { ok: false, error: 'no AI drivers in wager rooms' };
    if (!force && room.crews) return { ok: false, error: 'no AI drivers in a Channel Battle' };
    if (room.slots[slot] !== 0) return { ok: false, error: 'slot not open' };
    const id = this.nextAi++;
    if (this.nextAi > 0x7fffffff) this.nextAi = AI_BASE_ID;
    const used = new Set([...room.racers.values()].map(x => x.name));
    const host = room.racers.get(room.host);
    const hostLevel = (host && host.level) || 1;
    // the client's AI drivers (init\aicarparam.xlt, gamedata.aiDrivers): one whose AI level is near the host's, in one
    // of its cars; without the table the remake's own names and a car up to 8 levels above
    const table = this.gd && Array.isArray(this.gd.aiDrivers) ? this.gd.aiDrivers.filter(a => !used.has(a.name)) : [];
    let name, level, car;
    if (table.length) {
      const best = Math.min(...table.map(a => Math.abs(a.level - hostLevel)));
      const near = table.filter(a => Math.abs(a.level - hostLevel) <= Math.max(3, best));
      const ai = near[Math.floor(this.rng() * near.length)];
      name = ai.name;
      level = Math.max(1, Math.min(99, ai.level));
      car = ai.cars[Math.floor(this.rng() * ai.cars.length)];
    } else {
      const free = AI_NAMES.filter(n => !used.has(n));
      name = free.length ? free[Math.floor(this.rng() * free.length)] : `AI ${room.racers.size + 1}`;
      level = Math.max(1, Math.min(99, hostLevel + Math.floor(this.rng() * 7) - 3));
      car = [0, 2, 3][Math.floor(this.rng() * 3)];
      if (this.gd && Array.isArray(this.gd.carList)) {
        const cars = this.gd.carList.filter(c => c.reqLevel <= level + 8);
        if (cars.length) car = cars[Math.floor(this.rng() * cars.length)].index;
      }
    }
    const skill = Math.round((0.82 + this.rng() * 0.15) * 1000) / 1000;
    room.slots[slot] = id;
    room.racers.set(id, {
      session: aiSession(id, name), accountId: 0, name, level, ready: true, readyAt: 0, slot, lap: 0, maxLap: 0, lapAt: 0,
      idx: 0, n: 0, gate: 0, gateAt: 0, finished: false, time: 0, ai: true, car, skill,
    });
    return { ok: true, id };
  }

  /** Slot clicks (0x57d9f0): the host closes / reopens open slots, adds or removes AI; a guest who is not ready moves. */
  slot(session, m) {
    const room = session.race;
    if (!room || room.state !== 'lobby') return { ok: false, error: 'not in a race lobby' };
    const i = m.slot | 0, op = m.op;
    if (i < 0 || i >= room.max) return { ok: false, error: 'bad slot' };
    const isHost = room.host === session.id;
    if (op === 'close' || op === 'open') {
      if (!isHost) return { ok: false, error: 'only the host can do that' };
      if (op === 'close' && room.slots[i] !== 0) return { ok: false, error: 'slot not open' };
      if (op === 'open' && room.slots[i] !== -1) return { ok: false, error: 'slot not closed' };
      room.slots[i] = op === 'close' ? -1 : 0;
    } else if (op === 'ai') {
      if (!isHost) return { ok: false, error: 'only the host can do that' };
      const r = this.addAi(room, i);
      if (!r.ok) return r;
    } else if (op === 'noai') {
      if (!isHost) return { ok: false, error: 'only the host can do that' };
      const ai = room.racers.get(room.slots[i]);
      if (!ai || !ai.ai) return { ok: false, error: 'no AI driver there' };
      room.racers.delete(ai.session.id);
      room.slots[i] = 0;
    } else if (op === 'move') {
      const r = room.racers.get(session.id);
      if (!r) return { ok: false, error: 'spectators cannot do that' };
      if (r.ready && !isHost) return { ok: false, error: 'not while ready' };
      if (room.slots[i] !== 0) return { ok: false, error: 'slot not open' };
      if (room.crews && (i & 1) !== (r.slot & 1)) return { ok: false, error: 'crew members stay on their crew side' };
      room.slots[r.slot] = 0; room.slots[i] = session.id; r.slot = i;
    } else return { ok: false, error: 'bad op' };
    this.changed(room);
    if (op === 'ai' || op === 'noai') this.broadcastList(session);
    return { ok: true };
  }

  /** SELECT_A / SELECT_B (0x7855b0): move to the first open slot of a team. */
  teamChange(session, team) {
    const room = session.race;
    if (!room || room.state !== 'lobby' || !room.team) return { ok: false, error: 'not in a team lobby' };
    const r = room.racers.get(session.id);
    if (!r) return { ok: false, error: 'spectators cannot do that' };
    team = team ? 1 : 0;
    if ((r.slot & 1) === team) return { ok: true };
    if (r.ready && room.host !== session.id) return { ok: false, error: 'not while ready' };
    const i = room.openSlot(team);
    if (i < 0) return { ok: false, error: 'team is full' };
    return this.slot(session, { slot: i, op: 'move' });
  }

  /** SLOTxx_CLOSE (host kick, confirm 10240). */
  kickPlayer(session, id) {
    const room = session.race;
    if (!room || room.state !== 'lobby' || room.host !== session.id) return { ok: false, error: 'only the host can do that' };
    const r = room.racers.get(id);
    if (!r || id === session.id) return { ok: false, error: 'not in this room' };
    if (r.ai) return this.slot(session, { slot: r.slot, op: 'noai' });
    this.leave(r.session, 'kicked by the host');
    return { ok: true };
  }

  /** Crew battle: the two crews change sides (even <-> odd slots), e.g. when the channel owner arrives or changes. */
  swapCrewSides(room) {
    if (!room.crews) return;
    const racers = [...room.racers.values()].sort((a, b) => a.slot - b.slot);
    for (const r of racers) if (room.slots[r.slot] === r.session.id) room.slots[r.slot] = 0;
    room.crews.reverse(); room.crewNames.reverse();
    for (const r of racers) {
      const side = 1 - (r.slot & 1);
      const slot = room.openSlot(side);
      r.slot = slot >= 0 ? slot : room.openSlot(undefined);
      room.slots[r.slot] = r.session.id;
    }
    this.changed(room);
  }

  startMessage(room) {
    const order = [...room.racers.values()].sort((a, b) => a.slot - b.slot);
    return {
      t: 'race.start', id: room.id, track: room.track, startIn: Math.max(0, room.startAt - this.clock()), team: room.team, arena: room.arena,
      realMatch: room.realMatch, wager: room.wager, noGate: room.noGate, gates: !!room.gt, gatesPerLap: room.gt ? room.gt.per : 0,
      mission: room.mission | 0,
      // ROUNDNUMBER 1420000: the Channel Battle round (1, 2, 3 = Final; src/channelbattle.js battleStart sets it)
      round: room.crews ? room.cbRound | 0 : 0,
      slots: order.map(r => {
        const o = { id: r.session.id, name: r.name, slot: room.persist ? r.slot : r.grid, grid: r.grid, team: r.slot & 1, level: r.level };
        if (r.ai) { o.ai = true; o.car = r.car; o.skill = r.skill; }
        return o;
      }),
    };
  }

  /**
   * Host starts. Persistent rooms (the wait room) need every player ready and at least two drivers (the client's
   * canStart, 0x57b060; AI drivers count in the remake); legacy rooms start once two racers are ready and move everyone
   * else out. Wager rooms need two players with 10x the fee each (11107 / 11067), Real Match rooms the RM time (10850).
   */
  start(session) {
    const room = session.race;
    if (!room || room.host !== session.id || room.state !== 'lobby') return { ok: false, error: 'only the host can start' };
    const ready = [...room.racers.values()].filter(r => r.ready || r.ai || r.session.id === room.host);
    if (room.persist) {
      if (room.racers.size < 2) return { ok: false, error: 'need at least two racers' };
      if (ready.length < room.racers.size) return { ok: false, error: 'not everyone is ready' };
      if (room.wager && room.humans.length < 2) return { ok: false, error: 'You must have at least 2 players to start the Wager Battle.', code: 11107 };
      const b = this.startBlock(room, room.humans.map(r => r.session));
      if (b) return b;
      if (room.crews) {
        // equal sides (server text 20184), both crews present
        const a = [...room.racers.values()].filter(r => !(r.slot & 1)).length, bb = room.racers.size - a;
        if (!a || !bb) return { ok: false, error: 'a Channel Battle needs both crews' };
        if (a !== bb) return { ok: false, error: 'both crews need the same number of racers' };
        // round 1 of an attack spends the Challenge Coupon; the round's map (src/channelbattle.js)
        const cb = this.teams ? this.teams.cb.battleStart(room) : null;
        if (cb) return cb;
      }
    } else {
      if (ready.length < 2) return { ok: false, error: 'need at least two ready racers' };
      for (const r of [...room.racers.values()]) if (!ready.includes(r)) this.leave(r.session);
    }
    clearTimeout(room.kick); room.kick = null;
    room.state = 'countdown';
    room.startAt = this.clock() + this.countdownMs;
    room.startCount = room.racers.size;
    room.finishOrder = [];
    // checkpoint gates from the track table (only rooms of the wait room; the legacy protocol keeps the lap checks)
    room.gt = room.persist ? this.tracks.get(room.track.key.toLowerCase()) || null : null;
    // grid: the slot order (0x725560 puts each player on its slot); legacy rooms number the grid in join order
    const order = [...room.racers.values()].sort((a, b) => a.slot - b.slot);
    order.forEach((r, i) => {
      r.grid = i; r.lap = 0; r.maxLap = 0; r.lapAt = room.startAt; r.idx = 0; r.n = 0; r.gate = 0; r.gateAt = room.startAt;
      r.finished = false; r.time = 0; r.dnf = false; r.finishAt = 0;
    });
    // wager: every player's fee goes into the pot now (a driver who leaves loses it)
    room.pot = 0;
    if (room.wager) {
      const fee = WAGER_FEES[room.wager];
      for (const r of room.humans) {
        this.withProfile(r.accountId, p => { p.Mito = Math.max(0, (Number(p.Mito) || 0) - fee); });
        r.paid = fee; room.pot += fee;
      }
    }
    room.send(this.startMessage(room));
    room.timer = setTimeout(() => this.go(room), this.countdownMs);
    room.deadline = setTimeout(() => this.safeFinish(room, true), this.countdownMs + MAX_RACE_MS);
    this.log(`race ${room.id} on ${room.track.key}: ${room.racers.size} racers${room.gt ? `, ${room.gt.gates.length} gates` : ''}`);
    this.broadcastList(session);
    return { ok: true };
  }

  /** T: the race runs; the AI drivers' finish times are drawn from their skill (remake rule: the server drives them). */
  go(room) {
    if (room.state !== 'countdown') return;
    room.state = 'racing';
    room.goAt = Date.now();
    room.send({ t: 'race.go', id: room.id });
    this.startReplay(room);
    const len = room.track.length * room.track.laps;
    for (const r of room.racers.values()) {
      if (!r.ai) continue;
      // about 100 km/h average for a good driver (the tournament's simulated laps use the same 30 m/s x skill)
      const speed = this.aiSpeed || 30 * r.skill * (0.93 + this.rng() * 0.14);
      const ms = Math.max(1000, len / speed * 1000);
      r.finishAt = room.goAt + ms;
      r.aiTimer = setTimeout(() => {
        r.aiTimer = null;
        if (room.state !== 'racing' || r.finished || !room.racers.has(r.session.id)) return;
        r.finished = true; r.lap = room.track.laps; r.time = Math.round((this.clock() - room.startAt)) / 1000;
        if (room.gt) r.gate = room.track.laps * room.gt.per;
        room.finishOrder.push(r);
        room.send({ t: 'race.finished', id: r.session.id, name: r.name, place: room.finishOrder.length, time: r.time });
        this.maybeFinish(room);
      }, ms);
      if (r.aiTimer.unref) r.aiTimer.unref();
    }
  }

  /** Starts recording the race's poses (wait-room rooms): every racer's last car state, REPLAY_MS apart. */
  startReplay(room) {
    clearInterval(room.recTimer);
    room.recTimer = null;
    if (!room.persist || this.replays === false) { room.rec = null; return; }
    const [city, track] = String(room.track.key || '').split('/');
    room.rec = { city, track: track || '', title: room.title || '', laps: room.track.laps | 0, team: !!room.team, round: room.cbRound | 0, cars: [], byId: new Map() };
    this.sampleReplay(room);
    room.recTimer = setInterval(() => { try { this.sampleReplay(room); } catch (err) { this.log(`race ${room.id}: replay sample failed: ${err.message}`); } }, this.replayMs || REPLAY_MS);
    if (room.recTimer.unref) room.recTimer.unref();
  }

  sampleReplay(room) {
    const rec = room.rec;
    if (!rec || room.state !== 'racing') return;
    const now = Date.now(), t = Math.round((now - room.goAt) / 10) / 100;
    if (t > REPLAY_MAX_S) { clearInterval(room.recTimer); room.recTimer = null; return; }
    for (const r of room.racers.values()) {
      let c = rec.byId.get(r.session.id);
      if (!c) {
        const p = r.session && r.session.profile, own = p && Array.isArray(p.Cars) ? p.Cars[p.CurrentCar | 0] : null;
        c = { name: r.name, team: r.slot & 1, car: r.ai ? (r.car | 0) : own ? own.VehicleIndex | 0 : -1, paint: own ? own.Color | 0 : 0, ai: !!r.ai, place: 0, time: 0, f: [] };
        rec.byId.set(r.session.id, c); rec.cars.push(c);
      }
      // the server's AI drivers have no pose (the clients draw them on the line): their finish time places them
      const st = !r.ai && r.session.state;
      if (!st || now - (st.at || 0) > 2000 || r.finished) continue;
      c.f.push(t, st.p[0], st.p[1], st.p[2], st.r);
    }
  }

  /** Ends the recording with the race's places and times; it stays on the room as lastRec. */
  endReplay(room, results) {
    clearInterval(room.recTimer); room.recTimer = null;
    const rec = room.rec;
    room.rec = null;
    if (!rec) return null;
    this.sampleReplay(room);
    for (const x of results) {
      const c = rec.byId.get(x.id);
      if (c) { c.place = x.place | 0; c.time = Number(x.time) || 0; }
    }
    delete rec.byId;
    rec.duration = Math.round((Date.now() - (room.goAt || Date.now())) / 10) / 100;
    room.lastRec = rec;
    return rec;
  }

  /** Marks a racer out for an implausible report. */
  reject(room, r, reason) {
    r.dnf = true; r.finished = true; r.time = (this.clock() - room.startAt) / 1000;
    r.session.send({ t: 'race.rejected', reason });
    this.maybeFinish(room);
  }

  /** A finisher: in the order the server learns it (0x1eb: everyone sees the goal-in banner 1640000). */
  finishRacer(room, r, now) {
    const laps = room.track.laps;
    const elapsed = (now - room.startAt) / 1000;
    const minTime = (room.track.length * laps) / this.maxAvgSpeed;
    if (elapsed < minTime) return this.reject(room, r, 'finish time not plausible');
    r.finished = true; r.time = Math.round(elapsed * 1000) / 1000; room.finishOrder.push(r);
    room.send({ t: 'race.finished', id: r.session.id, name: r.name, place: room.finishOrder.length, time: r.time });
    this.maybeFinish(room);
  }

  /**
   * A checkpoint (0x787ce0 {gate, time}): n = the racer's gate count after this gate, over the whole race (each lap is
   * the track's gates and then the finish line). Gates come in order, each no sooner than its straight-line distance
   * from the last one allows; no-gate rooms (flag 0x400) only need the finish line once a lap. The last one finishes.
   */
  gate(session, m) {
    const room = session.race;
    if (!room || room.state !== 'racing' || !room.gt) return;
    const r = room.racers.get(session.id);
    if (!r || r.finished) return;
    const gt = room.gt, per = gt.per, laps = room.track.laps, total = laps * per;
    const n = m.n | 0;
    if (n <= r.gate || n > total) return;   // repeated or stale report
    const lapEnd = (Math.floor(r.gate / per) + 1) * per;
    if (n !== r.gate + 1 && !(room.noGate && n === lapEnd)) return;   // skipped a gate
    const now = this.clock();
    const from = room.noGate && n === lapEnd && n !== r.gate + 1 ? null : prevPoint(gt, n);
    const minMs = from ? dist(from, gatePoint(gt, n)) / this.maxAvgSpeed * 1000 : gt.lapLength / this.maxAvgSpeed * 1000;
    if (now - r.gateAt < minMs * 0.9) return this.reject(room, r, 'gate time not plausible');
    r.gate = n; r.gateAt = now;
    const lap = Math.floor(n / per);
    if (lap > r.lap) {
      // a lap is only plausible after a lap's worth of driving
      if ((now - r.lapAt) / 1000 < room.track.length / this.maxAvgSpeed) return this.reject(room, r, 'lap time not plausible');
      r.lap = lap; r.maxLap = lap; r.lapAt = now; r.idx = 0;
    }
    if (n === total) this.finishRacer(room, r, now);
    else if (n % per !== 0) this.gateEffect(room, r);
    this.standings(room);
  }

  /**
   * The PvP gate effects (NewGateEffect 0x40000, PlayerEvent 0x50e530; PVP.md 15.2). The remake's rule (mirrored by the
   * client's PvpGateEffects.Draw): by the driver's place p of n when it passes a gate (not a lap's finish line),
   * f = (p - 1) / (n - 1): f < 0.34 -> 60 % nothing, 25 % half charge, 15 % skid rush; f <= 0.66 -> 30 / 30 % / 20 %
   * full charge / 20 % skid rush; else 10 % nothing, 20 % half, 30 % full, 20 % booost, 20 % untouchable. Team rooms
   * give the team's half charge (7) to every member of the side. Not in no-gate, practice or wager rooms.
   */
  gateEffect(room, r) {
    if (!this.gateEffects || room.noGate || room.wager || room.max === 1) return;
    const order = [...room.racers.values()].sort((a, b) => room.progressOf(b) - room.progressOf(a));
    const place = order.indexOf(r) + 1, n = order.length;
    const type = gateEffectFor(place, n, room.team, this.rng());
    const send = (who, t) => {
      const msg = { t: 'race.gateEffect', id: room.id, name: who.name, type: t };
      if (!who.ai) who.session.send(msg);
      for (const w of room.watchers.values()) w.session.send(msg);
    };
    if (type === 7) { for (const m of room.racers.values()) if ((m.slot & 1) === (r.slot & 1) && !m.finished) send(m, 7); }
    else send(r, type);
    r.gateEffects = (r.gateEffects || 0) + 1;
  }

  progress(session, m) {
    const room = session.race;
    if (!room || room.state !== 'racing') return;
    const r = room.racers.get(session.id);
    if (!r || r.finished) return;
    r.n = Math.max(0, Math.min(100000, m.n | 0));
    if (room.gt) {
      // the gates decide laps and the finish; the line index only orders drivers between two gates
      r.idx = Math.max(0, m.idx | 0);
      this.standings(room);
      return;
    }
    const laps = room.track.laps;
    const now = this.clock();
    const lap = Math.max(0, Math.min(laps, m.lap | 0));
    const minLapTime = room.track.length / this.maxAvgSpeed;
    // progress must be monotonic: one lap at a time (the client steps back one lap when it reverses over the line)
    if (lap > r.lap + 1 || lap < r.lap - 1) return;
    if (lap > r.maxLap) {
      // a new lap is only plausible after a lap's worth of driving
      if ((now - r.lapAt) / 1000 < minLapTime) return this.reject(room, r, 'lap time not plausible');
      r.maxLap = lap; r.lapAt = now;
    }
    if (lap === r.lap) r.idx = Math.max(r.idx, Math.max(0, m.idx | 0));
    else r.idx = Math.max(0, m.idx | 0);
    r.lap = lap;
    if (m.finished && r.lap >= laps && r.maxLap >= laps) this.finishRacer(room, r, now);
    this.standings(room);
  }

  /** Relays standings at most ~4 Hz per room. */
  standings(room) {
    if (room.state === 'racing' && (!room.lastStandings || Date.now() - room.lastStandings > 250)) {
      room.lastStandings = Date.now(); room.send({ t: 'race.standings', racers: room.info().racers });
    }
  }

  maybeFinish(room) {
    if (room.state !== 'racing') return;
    const active = [...room.racers.values()].filter(r => !r.finished && !r.dnf);
    // once someone wins, the rest get 30 s to cross the line (0x1ef: the client shows the end time with pvp_tencount)
    if (room.finishOrder.length >= 1 && active.length && !room.grace) {
      room.grace = setTimeout(() => this.safeFinish(room, true), GRACE_MS);
      room.send({ t: 'race.endTime', id: room.id, in: GRACE_MS });
    }
    // only AI drivers still out there: their timers finish the race
    if (active.length === 0) this.finishRoom(room, false);
  }

  /** Applies fn to a driver profile: the live session of the account if online, else the saved driver row. */
  withProfile(accountId, fn) {
    if (!accountId) return null;
    const live = this.world.findByAccount(accountId);
    if (live) {
      const out = fn(live.profile);
      live.save();
      live.send({ t: 'driver', driver: live.profile });
      return out;
    }
    const row = this.store.driver(accountId);
    if (!row) return null;
    const out = fn(row.profile);
    this.store.saveDriver(accountId, row.profile);
    return out;
  }

  /** Reward a finisher: the live session of the account if online, else the saved driver row. */
  reward(r, place, field, city, bestLap, expFactor = 1) {
    return this.withProfile(r.accountId, p => this.economy.grantRace(p, place, field, city, bestLap, expFactor));
  }

  /** From a timer: an exception must never take the server down. */
  safeFinish(room, timedOut) {
    try { this.finishRoom(room, timedOut); }
    catch (err) { this.log(`race ${room.id}: finish failed: ${err.message}`); this.rooms.delete(room.id); }
  }

  finishRoom(room, timedOut) {
    if (room.state === 'done' || room.state === 'lobby') return;
    room.state = 'done';
    room.clearTimers();
    const humans = [...room.racers.values()].filter(r => !r.ai);
    const field = Math.max(2, room.startCount);
    const humanField = humans.length + (room.startCount - room.racers.size > 0 ? room.startCount - room.racers.size : 0);
    const city = room.track.key.split('/')[0];
    const now = this.clock();
    const expFactor = arenaExpFactor(room.arena);
    const ranked = room.finishOrder.filter(r => !r.dnf);
    const dnfs = [...room.racers.values()].filter(r => !ranked.includes(r));
    // team points (remake rule): place p of n scores n - p + 1, DNF 0; the team with more points wins (2 = draw)
    const teamPoints = [0, 0];
    if (room.team) ranked.forEach((r, i) => { teamPoints[r.slot & 1] += field - i; });
    const winner = !room.team ? -1 : teamPoints[0] > teamPoints[1] ? 0 : teamPoints[1] > teamPoints[0] ? 1 : 2;
    // ladder: EXP arenas only (not Free / Real Match / legacy rooms); the 8X arena needs LadderCalcNumX8 racers; AI
    // drivers take places but the ladder needs two players
    const ladderOn = !room.tour && room.persist && room.arena >= 0 && room.arena <= 4 && humanField >= 2 && (room.arena !== 4 || room.startCount >= LADDER_MIN_X8);
    const results = [];
    const entry = (r, place) => {
      let reward = null, noReward, ladder = 0, ladderPoints;
      if (!r.ai && room.tour) noReward = 'tournament';   // the championship pays by the stage reached (src/pvptour.js)
      else if (!r.ai) {
        try {
          if (place > 0) {
            if (room.startCount < 2) noReward = 'not enough racers';
            else if (now - (this.lastReward.get(r.accountId) || -Infinity) < this.rewardCooldownMs) noReward = 'cooldown';
            else {
              // team mode: the winning team (or both on a draw) is paid as 1st, the other team as 3rd
              const paid = room.team ? ((r.slot & 1) === winner || winner === 2 ? 1 : 3) : place;
              reward = this.reward(r, paid, field, city, r.time / room.track.laps, expFactor);
              this.lastReward.set(r.accountId, now);
            }
            this.store.addResult(room.track.key, r.name, place, r.time);
          }
          if (ladderOn) {
            ladder = ladderDelta(place > 0 ? place : field, field);
            ladderPoints = this.withProfile(r.accountId, p => {
              if (!Number.isFinite(p.LadderPoints)) p.LadderPoints = LADDER_START;
              p.LadderPoints = Math.max(0, p.LadderPoints + ladder);
              p.PvpRaces = (p.PvpRaces | 0) + 1;
              if (place === 1) p.PvpWins = (p.PvpWins | 0) + 1;
              return p.LadderPoints;
            });
          }
        } catch (err) { this.log(`race ${room.id}: could not save the result of ${r.name}: ${err.message}`); }
      }
      const out = { id: r.session.id, name: r.name, level: r.level, place, time: r.time, reward, slot: r.slot, team: r.slot & 1 };
      if (r.ai) out.ai = true;
      if (place === 0) out.dnf = true;
      if (noReward) out.noReward = noReward;
      if (room.team) out.points = place > 0 ? field - place + 1 : 0;
      if (ladderOn && !r.ai) { out.ladder = ladder; if (ladderPoints !== undefined && ladderPoints !== null) out.ladderPoints = ladderPoints; }
      results.push(out);
    };
    ranked.forEach((r, i) => entry(r, i + 1));
    dnfs.forEach(r => entry(r, 0));
    // wager settlement first (0x1f3 arrives before the result list)
    if (room.wager && room.pot > 0) this.settleWager(room, results, winner);
    this.endReplay(room, results);
    const msg = { t: 'race.results', id: room.id, track: room.track, results, timedOut, arena: room.arena };
    room.lastResults = { at: now, results };   // Pick'em (src/events.js) reads the final places
    if (room.team) { msg.teamPoints = { a: teamPoints[0], b: teamPoints[1] }; msg.winner = winner; }
    if (room.wager) msg.wager = room.wager;
    if (room.crews && room.crews[0] && room.crews[1] && this.teams && room.channel) {
      try {
        msg.crew = this.teams.battleResult(room.channel.id, room.crews, winner, results);
        // RESULT2 2200000 PERFECT: every racer of the winning side placed ahead of every racer of the other side
        const places = side => results.filter(x => x.team === side && x.place > 0).map(x => x.place);
        const w = places(winner), l = results.filter(x => x.team !== winner).map(x => x.place || 99);
        msg.crew.perfect = winner < 2 && w.length > 0 && l.every(p => p > Math.max(...w));
        const crewMsg = msg.crew, accts = new Map([...room.racers.values()].map(r => [r.session.id, r.accountId]));
        const withAccounts = results.map(x => ({ ...x, accountId: accts.get(x.id) }));
        setImmediate(() => { try { this.teams.cb.afterRound(room, crewMsg, withAccounts); } catch (err) { this.log(`race ${room.id}: crew round failed: ${err.message}`); } });
      } catch (err) { this.log(`race ${room.id}: crew result failed: ${err.message}`); }
    }
    // the reward pick (4310000, 0x72ea60): EXP arenas, not wager / crew rooms, every player who raced picks a card
    const pickers = humans.filter(r => room.racers.has(r.session.id));
    if (!room.tour && room.persist && room.arena >= 0 && room.arena <= 4 && !room.wager && !room.crews && room.startCount >= 2 && pickers.length) {
      this.openPick(room, pickers);
      msg.pick = { ms: this.pickMs, cards: PICK_CARDS };
    }
    if (room.tour) msg.tour = true;
    room.send(msg);
    if (room.tour && this.tourHook) this.tourHook(room, results);
    this.log(`race ${room.id} done: ${results.map(r => `${r.place || 'DNF'}.${r.name}`).join(' ')}`);
    if (room.persist && room.humans.length > 0 && !room.tour) {
      // back to the wait room (the client returns 13 s after the results, 0x50b73e; after the reward pick when there is
      // one): the idle-host clock only starts once the host can be back
      room.kickHoldUntil = Date.now() + 13000 + (msg.pick ? this.pickMs + 3000 : 0);
      room.state = 'lobby';
      room.finishOrder = [];
      room.gt = null;
      for (const r of room.racers.values()) {
        r.ready = r.ai; r.readyAt = 0; r.lap = 0; r.maxLap = 0; r.idx = 0; r.gate = 0; r.finished = false; r.time = 0; r.dnf = false; r.finishAt = 0;
      }
      this.changed(room);
      const any = room.humans[0];
      if (any) this.broadcastList(any.session);
    } else {
      for (const r of room.racers.values()) if (r.session.race === room) r.session.race = null;
      for (const w of room.watchers.values()) if (w.session.race === room) w.session.race = null;
      this.rooms.delete(room.id);
    }
    if (this.lastReward.size > 1000) for (const [k, t] of this.lastReward) if (now - t >= this.rewardCooldownMs) this.lastReward.delete(k);
  }

  /** Pays the wager pot and tells the room (race.wager = 0x1f3 {team, entries: mito delta, new mito, rank, ...}). */
  settleWager(room, results, winner) {
    const fee = WAGER_FEES[room.wager];
    const paid = [...room.racers.values()].filter(r => r.paid);
    const entries = paid.map(r => ({ key: r.session.id, place: (results.find(x => x.id === r.session.id) || {}).place || 0, team: r.slot & 1 }));
    // players who left mid-race paid into the pot too
    const pot = room.pot;
    const payout = wagerPayouts(entries.concat(Array.from({ length: Math.max(0, pot / fee - entries.length) }, (_, i) => ({ key: `left${i}`, place: 0, team: -1 }))), fee, room.team, winner);
    const list = [];
    for (const r of paid) {
      const win = payout.get(r.session.id) || 0;
      const mito = this.withProfile(r.accountId, p => { p.Mito = (Number(p.Mito) || 0) + win; return p.Mito; });
      const res = results.find(x => x.id === r.session.id);
      list.push({ id: r.session.id, name: r.name, level: r.level, rank: res ? res.place : 0, team: r.slot & 1, delta: win - fee, mito: mito ?? 0 });
      if (res) res.wager = win - fee;
    }
    room.send({ t: 'race.wager', id: room.id, team: room.team, grade: room.wager, fee, winner, entries: list });
  }

  // ---------------- the reward pick (4310000: 0x1e3 pick / 0x1e4 revealed cards) ----------------
  openPick(room, pickers) {
    const cards = pickCards(room.arena, this.rng, this.rewardGroups);
    const pick = { cards, taken: new Map(), pickers: new Map(pickers.map(r => [r.session.id, r])), timer: null, done: false };
    room.pick = pick;
    pick.timer = setTimeout(() => { try { this.closePick(room, pick); } catch (err) { this.log(`race ${room.id}: pick failed: ${err.message}`); } }, this.pickMs);
    if (pick.timer.unref) pick.timer.unref();
  }

  pickInfo(pick, slot) {
    const t = pick.taken.get(slot), c = pick.cards[slot];
    return { slot, name: t ? t.name : '', type: c.type, amount: c.amount, item: c.item };
  }

  /** A player's card: the reward is paid at once and everyone sees the card turn over. */
  pick(session, slot) {
    const room = session.race;
    const pick = room && room.pick;
    if (!pick || pick.done) return { ok: false, error: 'no reward to pick' };
    const r = pick.pickers.get(session.id);
    if (!r) return { ok: false, error: 'no reward to pick' };
    if ([...pick.taken.values()].some(t => t.id === session.id)) return { ok: false, error: 'already picked' };
    slot |= 0;
    if (slot < 0 || slot >= PICK_CARDS || pick.taken.has(slot)) return { ok: false, error: 'card taken' };
    this.takeCard(room, pick, r, slot);
    if (pick.taken.size >= pick.pickers.size) this.closePick(room, pick);
    return { ok: true, card: this.pickInfo(pick, slot) };
  }

  takeCard(room, pick, r, slot) {
    const c = pick.cards[slot];
    pick.taken.set(slot, { id: r.session.id, name: r.name });
    this.withProfile(r.accountId, p => {
      if (c.type === 3) p.Mito = (Number(p.Mito) || 0) + c.amount;
      else if (c.type === 5) addExp(p, c.amount);
      else if (c.type === 1 && c.item) { if (!Array.isArray(p.Items)) p.Items = []; items.give(p, c.item, c.amount); }
    });
    room.send({ t: 'race.picked', id: room.id, picks: [this.pickInfo(pick, slot)], done: false });
  }

  /** Time is up (11799): the server picks for whoever has not, then every card is shown. */
  closePick(room, pick) {
    if (pick.done) return;
    pick.done = true;
    clearTimeout(pick.timer);
    for (const r of pick.pickers.values()) {
      if ([...pick.taken.values()].some(t => t.id === r.session.id)) continue;
      const free = pick.cards.map((_, i) => i).filter(i => !pick.taken.has(i));
      if (free.length) this.takeCard(room, pick, r, free[Math.floor(this.rng() * free.length)]);
    }
    room.send({ t: 'race.picked', id: room.id, picks: pick.cards.map((_, i) => this.pickInfo(pick, i)), done: true });
    if (room.pick === pick) room.pick = null;
  }

  // ---------------- chat, invites, find a friend ----------------
  /**
   * Lobby and wait-room chat (the chat component of 1000000 / 1100000: send scope "room", 0x5bcdd0): in a room to its
   * players and spectators, in the lobby to the channel's drivers looking at the same arena. Emoticons ("/Hi") travel
   * as chat text and are drawn by the clients (emoticon.xlt, 1130000).
   */
  chat(session, m) {
    const text = cleanText(m.text, 70);
    if (!text) return { ok: false, error: 'empty' };
    if (session.muted) return { ok: false, error: 'You are currently blocked from chatting.', code: 10891 };
    const now = Date.now();
    session.pvpChat = (session.pvpChat || []).filter(t => now - t < 10000);
    if (session.pvpChat.length >= 8) return { ok: false, error: 'You are sending messages too fast.' };
    session.pvpChat.push(now);
    const msg = { t: 'race.chat', from: session.name, text };
    const room = session.race;
    if (room) { msg.scope = 'room'; room.send(msg); return { ok: true }; }
    if (!session.channel) return { ok: false, error: 'not in a city' };
    msg.scope = 'lobby';
    const arena = Number.isInteger(m.arena) ? m.arena : session.pvpLobby;
    for (const s of session.channel.players.values()) if (!s.race && s.pvpLobby === arena) s.send(msg);
    if (session.pvpLobby !== arena) session.send(msg);
    return { ok: true };
  }

  /** FRIENDINVITE 1840000 list (0x1c2 -> 0x1c3): the channel's drivers who are not in a room, with level and car grade. */
  invitees(session) {
    if (!session.channel) return { ok: false, error: 'not in a city' };
    const list = [];
    for (const s of session.channel.players.values()) {
      if (s === session || s.race || !s.profile) continue;
      const p = s.profile;
      const car = Array.isArray(p.Cars) ? p.Cars[p.CurrentCar | 0] : null;
      const info = car && this.gd ? this.gd.car(car.VehicleIndex) : null;
      list.push({ id: s.id, name: s.name, level: p.Level | 0, grade: info ? info.grade : 1 });
      if (list.length >= 50) break;
    }
    return { ok: true, players: list };
  }

  /** INVITE in 1840000 (0x1d1, 5 s cooldown): the invited driver sees 10026 and may join without the password. */
  invite(session, name) {
    const room = session.race;
    if (!room || room.state !== 'lobby' || !room.racers.has(session.id)) return { ok: false, error: 'not in a race lobby' };
    const now = Date.now();
    if (now - (session.lastInvite || 0) < INVITE_COOLDOWN_MS) return { ok: false, error: 'too fast' };
    const target = this.world.findByName(name);
    if (!target || target === session) return { ok: false, error: 'No friend selected.', code: 10467 };
    if (target.channel !== room.channel) return { ok: false, error: `${target.name} is not in this channel.` };
    if (target.race) return { ok: false, error: `${target.name} is in a Battle Zone room.` };
    session.lastInvite = now;
    room.invited.add(target.id);
    target.send({ t: 'race.invite', from: session.name, id: room.id, title: room.title, wager: room.wager, team: room.team, arena: room.arena });
    return { ok: true };
  }

  /** FINDFRIENDS 1080000 -> USERINFO 1090000 (0x296 / 0x297): an online driver's card and where they are. */
  find(session, name) {
    const s = this.world.findByName(name);
    if (!s || !s.profile) return { ok: false, error: 'Offline', code: 10219 };
    const p = s.profile;
    const room = s.race;
    return {
      ok: true, name: s.name, level: p.Level | 0, exp: Number(p.Exp) || 0, races: p.RacesEntered | 0, wins: p.RacesWon | 0,
      ladder: Number.isFinite(p.LadderPoints) ? p.LadderPoints : null, bestLap: Number(p.BestLapSeconds) || 0,
      location: room ? 'Battle Zone' : s.channel ? `Channel ${String(s.channel.index).padStart(2, ' ')}` : 'Garage',
      city: s.channel ? s.channel.city : null,
      room: room && room.channel === session.channel ? { id: room.id, title: room.title, state: room.state, locked: !!room.password, wager: room.wager } : null,
    };
  }

  // ---------------- Real Match time (0x1c8) ----------------
  /** { open, endsIn (ms), startsIn (ms) } on the remake's UTC timetable. */
  rmState(now = this.clock()) {
    if (this.rmAlways) return { open: true, endsIn: RM_MINUTES * 60000, startsIn: 0 };
    const d = new Date(now);
    const intoHour = (d.getUTCMinutes() * 60 + d.getUTCSeconds()) * 1000 + d.getUTCMilliseconds();
    const h = d.getUTCHours();
    if (h % RM_EVERY_H === 0 && intoHour < RM_MINUTES * 60000) return { open: true, endsIn: RM_MINUTES * 60000 - intoHour, startsIn: 0 };
    const hoursToNext = RM_EVERY_H - (h % RM_EVERY_H);
    return { open: false, endsIn: 0, startsIn: hoursToNext * 3600000 - intoHour };
  }

  /** The RM notices (10842 start, 10843/10844 "Real Match will end in %d ...", 10845 "Real Match Finished!"). */
  rmTick() {
    const st = this.rmState();
    const push = notice => { for (const s of this.world.sessions.values()) s.send({ t: 'race.rm', ...st, notice }); };
    if (this.rmWasOpen !== null && st.open !== this.rmWasOpen) { push(st.open ? 'start' : 'end'); this.rmWarned = 0; }
    if (st.open) {
      const mins = Math.ceil(st.endsIn / 60000);
      const due = [1, 5, 10].find(w => mins <= w);
      if (due && (this.rmWarned === 0 || due < this.rmWarned)) { this.rmWarned = due; push('ending'); }
    }
    this.rmWasOpen = st.open;
  }

  /** LADDER 3930000: 10 rows per page (0x518b00), plus the caller's own rank. */
  ladder(session, m) {
    const page = Math.max(0, Math.min(199, m.page | 0));
    const rows = this.store.ladder(10, page * 10);
    const total = this.store.ladderCount();
    const pts = session.profile && Number.isFinite(session.profile.LadderPoints) ? session.profile.LadderPoints : null;
    const me = pts !== null ? { name: session.name, points: pts, rank: this.store.ladderRank(pts) } : null;
    return {
      ok: true, page, total, me,
      rows: rows.map((r, i) => ({ rank: page * 10 + i + 1, name: r.name, level: r.level | 0, points: r.points | 0, races: r.races | 0, wins: r.wins | 0 })),
    };
  }

  /**
   * GM controls (admin accounts only; the client's authority byte +0x7c < 'N'):
   * - check -> { admin } (the client shows the GM panels);
   * - missionbattle { mode 0..4 } (0x5b8200 "/missionbattle a b"): rooms this admin hosts become mission-battle rooms;
   * - tourReward { class 0..3, set 0..3 } (TOUR_REWARD GM APPLY 0xd52 / 0xd53): the championship of that class pays the
   *   reward table rw_gtc_<class>_<a..d>_<bracket> from now on (src/pvptour.js reads tourSet); without set: the current sets.
   */
  gm(session, m) {
    if (m.op === 'check') return { ok: true, admin: !!session.admin, tourSets: this.tourSets.slice() };
    if (!session.admin) return { ok: false, error: 'not allowed', code: 10745 };
    switch (m.op) {
      case 'missionbattle': {
        const mode = Number.isInteger(m.mode) && m.mode >= 0 && m.mode <= 4 ? m.mode : 0;
        session.missionBattle = mode;
        return { ok: true, mode };
      }
      case 'tourReward': {
        const cls = m.class | 0;
        if (cls < 0 || cls > 3) return { ok: false, error: 'bad class' };
        if (Number.isInteger(m.set)) {
          if (m.set < 0 || m.set > 3) return { ok: false, error: 'bad set' };
          this.tourSets[cls] = m.set;
          this.log(`gm ${session.name}: GT Championship class ${cls} rewards -> set ${'abcd'[m.set]}`);
        }
        return { ok: true, class: cls, set: this.tourSets[cls], tourSets: this.tourSets.slice() };
      }
      default: return { ok: false, error: 'unknown gm op' };
    }
  }

  /** The reward table letter (a..d) the GT Championship of a class pays (TOUR_REWARD GM, default a). */
  tourSet(cls) { return 'abcd'[this.tourSets[cls | 0] | 0] || 'a'; }

  /** Server shutdown: no timer fires after the store is closed. */
  closeAll() {
    clearInterval(this.rmTimer);
    for (const room of this.rooms.values()) { room.clearTimers(); if (room.pick) clearTimeout(room.pick.timer); room.state = 'done'; }
    this.rooms.clear();
  }
}

module.exports = {
  Races, MAX_AVG_SPEED, MIN_TRACK_LENGTH, MAX_SLOTS, ARENA_EXP, arenaExpFactor, ladderDelta, LADDER_START, WAGER_FEES,
  wagerPayouts, pickCards, AI_BASE_ID, COUNTDOWN_MS, gateEffectFor,
};
