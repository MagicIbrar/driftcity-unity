// Channel Battles (crew battles) around the crew rooms of race.js: the schedule, the channel table, challenge coupons,
// challenger queues, the owner's map setup, part stakes, spectating / cheering, replays, the crew ladder and the vacant-
// channel rule (DriftCity_RE/SOCIAL.md section 6, social/crews.md section 4). The client only showed what its server
// sent; the rules below are this rebuild's, kept simple:
//  - 20 battle channels like the client's 10 major + 10 semi rows; battle channel i is the world channel
//    `${CITIES[i % 4]}-${floor(i / 4) + 1}` (major = 0..9 "Channel n", semi = 10..19 "Semi n"), so a battle is raced in
//    that city channel like the original's server channels;
//  - schedule: env DC_CB_SCHEDULE / opts: "daily@HH" (default daily@20), "weekly@D@HH" (D 0 = Sunday), "always" (a new
//    window every UTC day) or "none"; a window lasts 2 h (10320 for 7200 s, 0x675ea0). Outside a window a crew room race
//    is a friendly match (points only);
//  - entry (0x370 / 0x371): an active crew, level / car grade limits, a Challenge Coupon i_n_00037 to queue an attack
//    (20077; spent when the crew's first round starts, as its description says), crew grade B+ to attack an owned major
//    channel (20076; semi and vacant channels are open to every crew), Master / 1st Class to queue (20081), one attack
//    per channel per window (20079);
//  - one battle room per channel: the owner crew on the even slots, the crew at the head of the queue on the odd slots;
//    other queued crews wait in the ATK lobby and get crew.turn when their turn comes;
//  - rounds: the attacker must win 3 in a row (teams.js); a lost round ends the attack (the crew leaves the room) and
//    the next crew attacks; on major channels 1-2 every attacker who lost a round loses one fitted performance part
//    (10553), which goes to the defending Master by note (BATTLEITEMGET / BATTLEITEMMISS);
//  - vacant channel: the crew on the even side holds it and defends; if nobody captures it before the window closes,
//    the holder takes it. An owner whose racers all leave the room while a challenger is in it loses the channel
//    (10327): the challenger becomes the holder;
//  - map setup: the owner's Master picks 2 tracks of the channel's city + traffic within 24 h of the capture (11387,
//    20086); without a choice a random track is used. Round 1 and the final use map 1, round 2 map 2;
//  - cheering: a spectator spends Mittron Fireworks i_n_00011 on a racer (5 s apart); replays: every round is kept as
//    a record (result board, not a ghost) at 1000 / 500 / 0 Mito by channel (0x567730), the fee goes to the owner's
//    Master (20350);
//  - crew ladder: the crew's place in the ranking (captures, then points); class S 1-3, A 4-10, B 11-20, C 21-30,
//    D 31-40, E 41-50, else R (0x608770).
'use strict';

const CITIES = ['cras', 'moonpalace', 'koinonia', 'oros'];
const CITY_NAMES = { cras: 'Cras', moonpalace: 'Moon Palace', koinonia: 'Koinonia', oros: 'Oros' };
const CHANNELS = 20;
const WINDOW_MS = 7200 * 1000;
const SETUP_MS = 86400 * 1000;
const PREP_MS = 60 * 1000;            // REMAINTIME "Preparation:" (display; the room's host starts)
const ROUND_MS = 10 * 60 * 1000;      // REMAINTIME "Remaining time:" (display)
const COUPON = 'i_n_00037';
const FIREWORKS = 'i_n_00011';
const CHEER_MS = 5000;
const STAKE_CHANNELS = [0, 1];
const REPLAY_PRICE = ch => (ch < 2 ? 1000 : ch < 10 ? 500 : 0);
const MAX_HISTORY = 30;
const MAX_REPLAYS = 400;
// channel level / car grade limits of the 0x35d entries (0 = none; ENTERCHANNEL is disabled above them, 0x675250).
// Remake rule (SERVICES.md 6): the semi channels are a junior league (level 60, V6 at most), the major channels are open.
const LIMITS = i => (i >= 10 ? { maxLevel: 60, maxGrade: 6, minGrade: 0 } : { maxLevel: 0, maxGrade: 0, minGrade: 0 });

/** The Ghost Battle ticket of a battle channel (0..19): ch_ghost_01..ch_ghost_20. */
function ghostTicket(channel) { return channel >= 0 && channel < 20 ? `ch_ghost_${String(channel + 1).padStart(2, '0')}` : null; }
function giveItem(p, id, n) {
  let it = p.Items.find(i => i.Id === id);
  if (!it) p.Items.push(it = { Id: id, Count: 0 });
  it.Count += n;
  if (it.Count <= 0) p.Items.splice(p.Items.indexOf(it), 1);
}
const fail = (error, code) => (code ? { ok: false, error, code } : { ok: false, error });

function channelInfo(i) {
  const semi = i >= 10;
  const city = CITIES[i % 4];
  const number = semi ? i - 9 : i + 1;
  return { index: i, key: `${city}-${Math.floor(i / 4) + 1}`, city, semi, number, label: semi ? `Semi ${number}` : `Channel ${number}` };
}
const TABLE = Array.from({ length: CHANNELS }, (_, i) => channelInfo(i));
function channelIndex(key) { return TABLE.findIndex(c => c.key === key); }

/** Crew ladder class from the ladder rank (0x608770). */
function ladderClass(rank) {
  if (!(rank > 0)) return '-';
  return rank <= 3 ? 'S' : rank <= 10 ? 'A' : rank <= 20 ? 'B' : rank <= 30 ? 'C' : rank <= 40 ? 'D' : rank <= 50 ? 'E' : 'R';
}

/** "daily@20" -> { kind, hour, day }. */
function parseSchedule(text) {
  const s = String(text || 'daily@20').trim().toLowerCase();
  if (s === 'always' || s === 'none') return { kind: s };
  let m = /^daily@(\d{1,2})$/.exec(s);
  if (m) return { kind: 'daily', hour: Math.min(23, Number(m[1])) };
  m = /^weekly@([0-6])@(\d{1,2})$/.exec(s);
  if (m) return { kind: 'weekly', day: Number(m[1]), hour: Math.min(23, Number(m[2])) };
  return { kind: 'daily', hour: 20 };
}

/** The battle window around a moment: { open, start, end, next, id }. */
function windowAt(sched, now) {
  const day = Math.floor(now / 86400000) * 86400000;
  if (sched.kind === 'none') return { open: false, start: 0, end: 0, next: 0, id: '' };
  if (sched.kind === 'always') return { open: true, start: day, end: day + 86400000, next: day, id: new Date(day).toISOString().slice(0, 10) };
  const startOf = d => d + sched.hour * 3600000;
  const fits = d => sched.kind === 'daily' || new Date(d).getUTCDay() === sched.day;
  for (let k = -7; k <= 8; k++) {
    const d = day + k * 86400000;
    if (!fits(d)) continue;
    const st = startOf(d);
    if (now < st + WINDOW_MS) {
      const open = now >= st;
      return { open, start: st, end: st + WINDOW_MS, next: st, id: new Date(st).toISOString().slice(0, 13) };
    }
  }
  return { open: false, start: 0, end: 0, next: 0, id: '' };
}

class ChannelBattles {
  constructor(teams) {
    this.teams = teams;
    this.schedule = parseSchedule(process.env.DC_CB_SCHEDULE);
    const db = teams.social.store.db;
    for (const col of ["maps TEXT NOT NULL DEFAULT ''", 'holder INTEGER NOT NULL DEFAULT 0', "leader TEXT NOT NULL DEFAULT ''"]) {
      try { db.exec(`ALTER TABLE crew_channels ADD COLUMN ${col}`); } catch { /* already there */ }
    }
    db.exec(`
      CREATE TABLE IF NOT EXISTS crew_replays (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        channel INTEGER NOT NULL,
        at INTEGER NOT NULL,
        match INTEGER NOT NULL,
        round INTEGER NOT NULL,
        owner TEXT NOT NULL,
        challenger TEXT NOT NULL,
        owner_mark INTEGER NOT NULL DEFAULT 0,
        challenger_mark INTEGER NOT NULL DEFAULT 0,
        winner TEXT NOT NULL DEFAULT '',
        views INTEGER NOT NULL DEFAULT 0,
        data TEXT NOT NULL DEFAULT ''
      );
    `);
    this.q = {
      setMaps: db.prepare('UPDATE crew_channels SET maps = ? WHERE channel = ?'),
      setLeader: db.prepare('UPDATE crew_channels SET leader = ? WHERE channel = ?'),   // the battle leader (SERVICES.md 6)
      dropChannel: db.prepare('DELETE FROM crew_channels WHERE channel = ?'),
      addReplay: db.prepare('INSERT INTO crew_replays (channel, at, match, round, owner, challenger, owner_mark, challenger_mark, winner, data) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)'),
      replays: db.prepare('SELECT * FROM crew_replays ORDER BY id DESC LIMIT ? OFFSET ?'),
      replayCount: db.prepare('SELECT COUNT(*) AS n FROM crew_replays'),
      replay: db.prepare('SELECT * FROM crew_replays WHERE id = ?'),
      viewed: db.prepare('UPDATE crew_replays SET views = views + 1 WHERE id = ?'),
      trimReplays: db.prepare('DELETE FROM crew_replays WHERE id <= (SELECT id FROM crew_replays ORDER BY id DESC LIMIT 1 OFFSET ?)'),
      ladder: db.prepare('SELECT id FROM crews ORDER BY captures DESC, points DESC, name ASC'),
    };
    this.queues = new Map();     // channel index -> [{ crewId, crew, mark, by, at }]
    this.tried = new Set();      // `${windowId}|${channel}|${crewId}` (one attack per channel per window, 20079)
    this.history = new Map();    // channel index -> [{ at, type, match, round, winnerSide, def, atk }]
    this.matches = new Map();    // channel index -> { window, n } (match numbers of 10348-10351)
    this.turns = new Map();      // channel index -> { crewId, prepEnd, roundEnd }
    this.lobbies = new Map();    // channel index -> Set(session)
    this.lastOpen = null;
    this.timer = setInterval(() => { try { this.tick(); } catch (err) { this.teams.social.log(`channel battles: ${err.message}`); } }, 15000);
    if (this.timer.unref) this.timer.unref();
  }

  get clock() { return this.teams.clock; }
  get races() { return this.teams.races; }
  get q2() { return this.teams.q; }
  setSchedule(text) { if (text !== undefined && text !== null) this.schedule = parseSchedule(text); }
  window() { return windowAt(this.schedule, this.clock()); }
  close() { clearInterval(this.timer); }

  // ---------------- lookups ----------------
  ownerRow(i) { return this.q2.channelOwner.get(TABLE[i].key) || null; }
  crewSummary(id) {
    const c = id ? this.q2.crew.get(id) : null;
    if (!c) return null;
    const master = this.teams.social.driverById(c.master);
    return { id: c.id, name: c.name, mark: c.mark, points: c.points, members: this.q2.memberCount.get(c.id).n, master: master ? master.name : '', grade: this.teams.grade(c) };
  }
  queue(i) { if (!this.queues.has(i)) this.queues.set(i, []); return this.queues.get(i); }
  room(i) {
    if (!this.races) return null;
    for (const r of this.races.rooms.values()) if (r.crews && r.channel && r.channel.id === TABLE[i].key && r.state !== 'done') return r;
    return null;
  }
  maps(row) { try { const m = JSON.parse(row && row.maps || '[]'); return Array.isArray(m) ? m : []; } catch { return []; } }

  /** Ladder rank of every crew (1-based) by the ranking order. */
  ladderRanks() { const out = new Map(); this.q.ladder.all().forEach((r, i) => out.set(r.id, i + 1)); return out; }
  ladder(crewId) { const rank = this.ladderRanks().get(crewId) || 0; return { ladderRank: rank, ladderClass: ladderClass(rank) }; }

  /** Battle mode: an open window and one of the 20 battle channels (else a friendly match). */
  battleMode(key) { return channelIndex(key) >= 0 && this.window().open; }

  // ---------------- channel table (0x35d) and schedule (0x35f) ----------------
  table() {
    const w = this.window();
    const now = this.clock();
    const rows = TABLE.map(c => {
      const own = this.ownerRow(c.index);
      const room = this.room(c.index);
      const turn = this.turns.get(c.index);
      const atk = room && room.crews[1] ? this.crewSummary(room.crews[1]) : null;
      const maps = this.maps(own);
      return {
        index: c.index, key: c.key, city: c.city, cityName: CITY_NAMES[c.city], semi: c.semi, number: c.number, label: c.label,
        owner: own && own.crew_id ? this.crewSummary(own.crew_id) : null,
        holder: !own && room && room.crews[0] ? this.crewSummary(room.crews[0]) : null,
        attacker: atk, since: own ? own.since : 0, setupUntil: own ? own.since + SETUP_MS : 0,
        open: w.start, close: w.end, active: w.open, ...LIMITS(c.index), leader: own ? own.leader || '' : '',
        maps, mapsPublic: maps.length > 0, stake: STAKE_CHANNELS.includes(c.index),
        queue: this.queue(c.index).map(x => ({ crew: x.crew, mark: x.mark })), room: room ? room.id : 0,
        state: room ? (room.state === 'lobby' ? 'wait' : 'play') : '', viewers: room ? room.watchers.size : 0,
        prepEnd: turn ? turn.prepEnd : 0, roundEnd: turn ? turn.roundEnd : 0,
      };
    });
    return { ok: true, now, next: w.next, open: w.open, windowEnd: w.end, windowMs: WINDOW_MS, schedule: this.schedule.kind, channels: rows };
  }

  /** ATK / DEF lobby state (0x386 + 0x390 + the room list). */
  lobby(i) {
    const c = TABLE[i];
    const room = this.room(i);
    const own = this.ownerRow(i);
    const turn = this.turns.get(i);
    const defId = own ? own.crew_id : room ? room.crews[0] : 0;
    const atkId = room ? room.crews[1] : 0;
    const streak = atkId ? this.teams.streaks.get(`${c.key}|${atkId}`) || 0 : 0;
    return {
      t: 'crew.lobby', channel: i, key: c.key, label: c.label, room: room ? room.id : 0, state: room ? room.state : '',
      def: this.crewSummary(defId), atk: this.crewSummary(atkId), owned: !!own, round: atkId ? streak + 1 : 0, rounds: [streak >= 1, streak >= 2, false, false].map(Boolean),
      prepEnd: turn ? turn.prepEnd : 0, roundEnd: turn ? turn.roundEnd : 0,
      queue: this.queue(i).map(x => ({ crew: x.crew, mark: x.mark, members: this.q2.memberCount.get(x.crewId).n })),
      history: (this.history.get(i) || []).slice(-MAX_HISTORY), now: this.clock(),
    };
  }

  pushLobby(i) {
    const set = this.lobbies.get(i);
    if (!set || !set.size) return;
    const msg = this.lobby(i);
    for (const s of set) { if (s.closed || s.cbChannel !== i) set.delete(s); else s.send(msg); }
  }

  // ---------------- entry (0x370 / 0x371) and the queue (0x384) ----------------
  /** Checks a crew may attack channel i now; error or null. */
  attackBlock(i, crew, rank, profile, needCoupon) {
    const w = this.window();
    const own = this.ownerRow(i);
    if (!w.open) return fail('Channel Battle is not in progress.');
    if (this.tried.has(`${w.id}|${i}|${crew.id}`)) return fail("You may only challenge a Channel once. You've lost the last one.", 20079);
    if (own && !TABLE[i].semi && this.teams.grade(crew) !== 'S' && this.teams.grade(crew) !== 'A' && this.teams.grade(crew) !== 'B')
      return fail('Only racing with crews having Rank B or higher can participate.', 20076);
    if (needCoupon && rank > 2) return fail('Only the Crew Master or First Class Crew Member can initiate an attack.', 20081);
    if (needCoupon && profile && this.teams.count(profile, COUPON) <= 0) return fail('A Channel Battle Attack Ticket is required to participate in an attack.', 20077);
    return null;
  }

  enter(session, m) {
    const i = Number.isInteger(m.channel) ? m.channel : -1;
    if (i < 0 || i >= CHANNELS) return fail('no such channel');
    const me = this.teams.memberOf(session.accountId);
    if (!me) return fail("You don't have a Crew");
    const crew = this.q2.crew.get(me.crew_id);
    if (crew.state !== 'A') return fail(`${crew.name} Crew is applying for deletion`);
    const c = TABLE[i];
    const own = this.ownerRow(i);
    const defending = own && own.crew_id === crew.id;
    const lim = LIMITS(i), prof = session.profile;
    const carGrade = prof && Array.isArray(prof.Cars) && prof.Cars[prof.CurrentCar | 0] ? (prof.Cars[prof.CurrentCar | 0].Grade | 0) + 1 : 1;
    if (prof && ((lim.maxLevel > 0 && (prof.Level | 0) > lim.maxLevel) || (lim.maxGrade > 0 && carGrade > lim.maxGrade) || carGrade < lim.minGrade))
      return fail(`${c.label} is open to drivers up to level ${lim.maxLevel} with a V${lim.maxGrade} or lower car.`, 'limit');
    if (!defending) {
      const b = this.attackBlock(i, crew, me.rank, null, false);
      if (b) return b;
    }
    // subscribe to the lobby pushes (0x372)
    this.leaveLobby(session);
    session.cbChannel = i;
    if (!this.lobbies.has(i)) this.lobbies.set(i, new Set());
    this.lobbies.get(i).add(session);
    const inChannel = session.channel && session.channel.id === c.key;
    const out = { ok: true, channel: i, key: c.key, city: c.city, defending, inChannel, lobby: this.lobby(i) };
    if (!inChannel) return out;   // the client moves to the city channel first, then enters again
    const room = this.room(i);
    // the defenders go to the room at once (DEF lobby, auto ready 0x384); attackers wait for their turn
    const head = this.queue(i)[0];
    const myTurn = head && head.crewId === crew.id && (!room || !room.crews[1]);
    if (defending || myTurn || (room && room.crews[1] === crew.id) || (!room && !own) || (room && !own && room.crews[0] === crew.id)) {
      const j = this.joinRoom(session, i);
      if (!j.ok) return j;
      out.room = j.id;
    } else if (room && !room.crews[1] && this.queue(i).length === 0 && !this.tried.has(`${this.window().id}|${i}|${crew.id}`)) {
      out.canAttack = true;
    }
    out.lobby = this.lobby(i);
    return out;
  }

  leaveLobby(session) {
    if (session.cbChannel === undefined) return;
    const set = this.lobbies.get(session.cbChannel);
    if (set) set.delete(session);
    session.cbChannel = undefined;
  }

  /** The channel's battle room: joined, or hosted on the channel's map when there is none. */
  joinRoom(session, i) {
    if (!this.races) return fail('races are not available');
    const room = this.room(i);
    if (room) {
      if (room.racers.has(session.id)) return { ok: true, id: room.id };
      return this.races.join(session, { id: room.id });
    }
    const track = this.mapFor(i, 1) || { key: `${TABLE[i].city}/cb${i}`, name: TABLE[i].label, laps: 1, length: 1000 };
    return this.races.host(session, { track, room: { title: this.teams.crewNameOf(session.accountId) || TABLE[i].label, team: true, max: 16, crew: true } });
  }

  /** ATKCOVER_READY / CANCEL (0x384): queue the crew for an attack (needs a coupon) or leave the queue. */
  ready(session, m) {
    const i = Number.isInteger(m.channel) ? m.channel : -1;
    if (i < 0 || i >= CHANNELS) return fail('no such channel');
    const me = this.teams.memberOf(session.accountId);
    if (!me) return fail("You don't have a Crew");
    const crew = this.q2.crew.get(me.crew_id);
    const own = this.ownerRow(i);
    if (own && own.crew_id === crew.id) return { ok: true, defending: true };   // the defender is ready by itself
    const q = this.queue(i);
    const at = q.findIndex(x => x.crewId === crew.id);
    if (m.on === false) {
      if (me.rank > 2) return fail('Not a Crew Master or First Class Crew Member.', 20075);
      if (at >= 0) q.splice(at, 1);
      this.pushLobby(i);
      return { ok: true, queued: false };
    }
    if (at >= 0) return { ok: true, queued: true, position: at + 1 };
    const b = this.attackBlock(i, crew, me.rank, session.profile, true);
    if (b) return b;
    q.push({ crewId: crew.id, crew: crew.name, mark: crew.mark, by: session.accountId, at: this.clock() });
    this.callNext(i);
    this.pushLobby(i);
    return { ok: true, queued: true, position: q.findIndex(x => x.crewId === crew.id) + 1 };
  }

  /** The head of the queue may take the odd side when it is free: crew.turn to its members in the lobby. */
  callNext(i) {
    const room = this.room(i);
    const q = this.queue(i);
    if (!q.length || (room && room.crews[1] && room.crews[1] !== q[0].crewId)) return;
    const head = q[0];
    if (this.turns.get(i)?.crewId !== head.crewId) {
      const prepEnd = this.clock() + PREP_MS;
      this.turns.set(i, { crewId: head.crewId, prepEnd, roundEnd: prepEnd + ROUND_MS });
    }
    for (const s of this.lobbies.get(i) || []) {
      const mm = this.teams.memberOf(s.accountId);
      if (mm && mm.crew_id === head.crewId) s.send({ t: 'crew.turn', channel: i, key: TABLE[i].key, room: room ? room.id : 0, crew: head.crew });
    }
  }

  // ---------------- race.js hooks ----------------
  /**
   * race.join of a crew room: which side a crew takes (0 even, 1 odd), or an error. The owner always defends on the
   * even side (a challenger who hosted moves over), the head of the queue (or the first challenger when nobody waits)
   * attacks; returns { side, swap } where swap asks race.js to exchange the sides first.
   */
  sideFor(room, crew, session) {
    const key = room.channel ? room.channel.id : '';
    const i = channelIndex(key);
    const battle = this.battleMode(key);
    const owner = this.teams.channelOwner(key);
    const [a, b] = room.crews;
    if (crew.crewId === a) return { side: 0 };
    if (crew.crewId === b) return { side: 1 };
    if (owner && crew.crewId === owner) {
      // the owner arrives in a room a challenger opened: the challenger moves to the odd side
      if (!b) {
        const ch = this.q2.crew.get(a);
        const blk = battle && i >= 0 && ch ? this.attackBlock(i, ch, 1, null, false) : null;
        if (blk) return blk;
        return { side: 0, swap: true };
      }
      return fail(`${room.crewNames[0]} Crew vs ${room.crewNames[1]} Crew only`);
    }
    if (b) {
      if (i >= 0 && this.queue(i).some(x => x.crewId === crew.crewId)) return fail(`${room.crewNames[1]} Crew is attacking first. Please wait for your turn.`);
      return fail(`${room.crewNames[0]} Crew vs ${room.crewNames[1]} Crew only`);
    }
    if (battle && i >= 0) {
      const c = this.q2.crew.get(crew.crewId);
      const me = this.teams.memberOf(session.accountId);
      const blk = this.attackBlock(i, c, me ? me.rank : 3, null, false);
      if (blk) return blk;
      const q = this.queue(i);
      if (q.length && q[0].crewId !== crew.crewId) return fail(`${q[0].crew} Crew is attacking first. Please wait for your turn.`);
      if (!q.length) q.push({ crewId: crew.crewId, crew: crew.crew, mark: c ? c.mark : 0, by: session.accountId, at: this.clock() });
      const prepEnd = this.clock() + PREP_MS;
      this.turns.set(i, { crewId: crew.crewId, prepEnd, roundEnd: prepEnd + ROUND_MS });
      setImmediate(() => this.pushLobby(i));
    }
    return { side: 1 };
  }

  /** race.start of a crew room: round 1 of an attack spends a Challenge Coupon of the attacking crew (20077). */
  battleStart(room) {
    const key = room.channel ? room.channel.id : '';
    const i = channelIndex(key);
    if (!this.battleMode(key) || !room.crews[1]) return null;
    const owner = this.teams.channelOwner(key);
    const attacker = owner && owner === room.crews[1] ? room.crews[0] : room.crews[1];
    const streak = this.teams.streaks.get(`${key}|${attacker}`) || 0;
    room.cbRound = Math.min(3, streak + 1);   // ROUNDNUMBER 1420000 (race.start "round")
    // the round's map (round 1 and the final: map 1, round 2: map 2)
    const tr = this.mapFor(i, streak + 1);
    if (tr) room.track = tr;
    if (streak > 0) return null;
    const q = this.queue(i);
    const head = q.find(x => x.crewId === attacker);
    const atkRacers = [...room.racers.values()].filter(r => !r.ai && (r.slot & 1) === 1);
    const order = [head && head.by, ...atkRacers.map(r => r.accountId)].filter(Boolean);
    for (const acc of order) {
      const s = this.teams.social.live(acc);
      if (s && s.profile && this.teams.count(s.profile, COUPON) > 0) {
        this.teams.give(s.profile, COUPON, -1);
        s.save();
        s.send({ t: 'driver', driver: s.profile });
        return null;
      }
    }
    return fail('A Channel Battle Attack Ticket is required to participate in an attack.', 20077);
  }

  /** race.leave of a crew room racer (called after the racer left the room). */
  battleLeft(room, racer, crewId) {
    const key = room.channel ? room.channel.id : '';
    const i = channelIndex(key);
    if (i < 0) return;
    const sideA = [...room.racers.values()].filter(r => !(r.slot & 1));
    const sideB = [...room.racers.values()].filter(r => (r.slot & 1));
    const owner = this.teams.channelOwner(key);
    // 10327: the owner's last waiting racer left while a challenger is in the room -> the channel is lost
    if (room.state === 'lobby' && owner && crewId === owner && !sideA.length && sideB.length && this.battleMode(key)) {
      const loser = this.q2.crew.get(owner);
      this.q.dropChannel.run(key);
      this.teams.notify(owner, { type: 'battle', channel: key, forfeit: true, label: TABLE[i].label, defender: loser ? loser.name : '' });
      if (this.races) this.races.swapCrewSides(room);
      this.addHistory(i, { type: 2, def: loser ? loser.name : '', atk: room.crewNames[0] });
      this.teams.social.log(`channel ${key}: ${loser ? loser.name : owner} left the battle room and lost the channel`);
    }
    // the attacking crew left: the next one in the queue attacks
    if (room.state === 'lobby' && !sideB.length) {
      const q = this.queue(i);
      if (q.length && q[0].crewId === crewId) q.shift();
      this.turns.delete(i);
      this.callNext(i);
    }
    this.pushLobby(i);
  }

  /** After race.results of a crew room in battle mode: the queue, the stakes, history and the replay record. */
  afterRound(room, crew, results) {
    const key = crew.channel;
    const i = channelIndex(key);
    if (i < 0 || !crew.round) return;
    const w = this.window();
    const mt = this.matches.get(i);
    if (!mt || mt.window !== w.id) this.matches.set(i, { window: w.id, n: 1 });
    const match = this.matches.get(i).n;
    this.addHistory(i, { type: 1, match, round: crew.round, winnerSide: crew.attackerWon ? 1 : 0, def: crew.defender, atk: crew.attacker });
    const dc = this.q2.crewByName.get(crew.defender), ac = this.q2.crewByName.get(crew.attacker);
    const winner = crew.winnerSide === 2 ? '' : crew.attackerWon ? crew.attacker : crew.defender;
    this.q.addReplay.run(i, this.clock(), match, crew.round, crew.defender, crew.attacker, dc ? dc.mark : 0, ac ? ac.mark : 0, winner,
      JSON.stringify({ track: room.track, results: results.map(r => ({ name: r.name, place: r.place, time: r.time, team: r.team, points: r.points | 0 })), teamPoints: crew.points,
        rec: room.lastRec || undefined }));   // the round's recorded poses (race.js endReplay) for the replay player
    if (this.q.replayCount.get().n > MAX_REPLAYS) this.q.trimReplays.run(MAX_REPLAYS);
    if (crew.attackerWon && !crew.captured) return this.pushLobby(i);
    // the battle leader of a captured channel: the capturing crew's best-placed racer in the final (SERVICES.md 6)
    if (crew.captured) {
      const best = results.filter(r => r.team === crew.attackerSide && !r.ai).sort((a, b) => (a.place | 0) - (b.place | 0))[0];
      try { this.q.setLeader.run(best ? best.name : '', key); } catch { /* no row */ }
    }
    // the attack is over (lost, or the channel was taken): next match
    this.matches.get(i).n++;
    const attackerId = ac ? ac.id : 0;
    const q = this.queue(i);
    const at = q.findIndex(x => x.crewId === attackerId);
    if (at >= 0) q.splice(at, 1);
    this.turns.delete(i);
    if (!crew.attackerWon && attackerId) {
      this.tried.add(`${w.id}|${i}|${attackerId}`);
      if (STAKE_CHANNELS.includes(i) && dc) this.stakes(results.filter(r => r.team === crew.attackerSide && !r.ai), dc, i);
    }
    // the losing side leaves the room: a beaten attacker, or the old owner after a capture (the new owner defends)
    setImmediate(() => {
      const r = this.races && this.races.rooms.get(room.id);
      if (!r) return;
      const leaveSide = crew.captured ? crew.defenderSide : crew.attackerSide;
      for (const x of [...r.racers.values()]) if (!x.ai && (x.slot & 1) === leaveSide) this.races.leave(x.session, crew.captured ? 'Your Crew lost the Channel.' : 'Your Crew was defeated.');
      if (crew.captured && leaveSide === 0) this.races.swapCrewSides(r);
      this.callNext(i);
      this.pushLobby(i);
    });
    if (crew.captured && this.teams.notices) this.teams.notices.broadcast('chat', { key: TABLE[i].semi ? '20289' : '20210', args: [TABLE[i].number], text: TABLE[i].semi ? `Semi ${TABLE[i].number} Channel Tournament completed.` : `${TABLE[i].number} Channel Tournament completed.` });
  }

  /** Part stakes of major channels 1-2: each beaten attacker loses one fitted performance part to the defending Master. */
  stakes(losers, defender, i) {
    const got = [];
    for (const r of losers) {
      const acc = r.accountId || (this.teams.social.driver(r.name) || {}).accountId;
      if (!acc) continue;
      const s = this.teams.social.live(acc);
      const apply = p => {
        const car = Array.isArray(p.Cars) ? p.Cars[p.CurrentCar | 0] : null;
        const slots = car && Array.isArray(car.PerfSlots) ? car.PerfSlots.map((u, k) => [u, k]).filter(([u]) => u >= 0) : [];
        if (!slots.length) return null;
        const [uid, k] = slots[Math.floor(this.teams.rng() * slots.length)];
        const part = (p.PerfParts || []).find(x => x.Uid === uid);
        car.PerfSlots[k] = -1;
        if (!part) return null;
        p.PerfParts.splice(p.PerfParts.indexOf(part), 1);
        return { kind: 'part', type: part.Type | 0, grade: part.Grade | 0, enhance: part.Enhance | 0, count: 1 };
      };
      let lost = null;
      if (s && s.profile) { lost = apply(s.profile); if (lost) { s.save(); s.send({ t: 'driver', driver: s.profile }); } }
      else {
        const row = this.teams.social.store.driver(acc);
        if (row) { lost = apply(row.profile); if (lost) this.teams.social.store.saveDriver(acc, row.profile); }
      }
      if (!lost) continue;
      got.push(lost);
      this.teams.social.push(acc, { t: 'crew.items', kind: 'miss', channel: i, parts: [lost] });   // BATTLEITEMMISS 2230000
      this.teams.social.mail.system(defender.master, `${r.name} lost a part in the attack on ${TABLE[i].label}.`, 0, undefined, lost);
    }
    if (got.length) this.teams.social.push(defender.master, { t: 'crew.items', kind: 'get', channel: i, parts: got });   // BATTLEITEMGET 2220000
  }

  addHistory(i, e) {
    if (!this.history.has(i)) this.history.set(i, []);
    const h = this.history.get(i);
    h.push({ at: this.clock(), channel: i, ...e });
    while (h.length > MAX_HISTORY) h.shift();
  }

  // ---------------- map setup (0x38a / 0x38e / 0x38f) ----------------
  setup(session, m) {
    const i = Number.isInteger(m.channel) ? m.channel : -1;
    if (i < 0 || i >= CHANNELS) return fail('no such channel');
    const crew = this.teams.crewOf(session.accountId);
    const own = this.ownerRow(i);
    if (!crew || !own || own.crew_id !== crew.id) return fail('You do not own the corresponding Battle Channel.', 20085);
    if (crew.master !== session.accountId) return fail('Crew Master only.');
    if (TABLE[i].semi) return fail('Channel Tournament settings are not possible during this time period.', 20086);
    const now = this.clock();
    if (!(now > own.since && now < own.since + SETUP_MS)) return fail('Channel Tournament settings are not possible during this time period.', 20086);
    const list = Array.isArray(m.maps) ? m.maps.slice(0, 2) : [];
    if (list.length !== 2) return fail('Pick two tracks.');
    const maps = [];
    for (const x of list) {
      const tr = this.races ? this.races.parseTrack(x && x.track) : null;
      if (!tr || !tr.key.toLowerCase().startsWith(TABLE[i].city + '/')) return fail(`Pick tracks of ${CITY_NAMES[TABLE[i].city]}.`);
      const traffic = Number.isInteger(x.traffic) && x.traffic >= 0 && x.traffic <= 2 ? x.traffic : Math.floor(this.teams.rng() * 3);
      maps.push({ track: tr, traffic });
    }
    this.q.setMaps.run(JSON.stringify(maps), TABLE[i].key);
    this.pushLobby(i);
    return { ok: true, channel: i, maps };
  }

  mapInfo(m) {
    const i = Number.isInteger(m.channel) ? m.channel : -1;
    if (i < 0 || i >= CHANNELS) return fail('no such channel');
    const own = this.ownerRow(i);
    const oc = own ? this.q2.crew.get(own.crew_id) : null;
    return { ok: true, channel: i, owner: oc ? oc.name : '', maps: this.maps(own), setupUntil: own ? own.since + SETUP_MS : 0 };
  }

  /** The round's track: the owner's map, or a random track of the channel's city once the 24 h passed (11387). */
  mapFor(i, round) {
    const maps = this.maps(this.ownerRow(i));
    if (maps.length === 2) return maps[round === 2 ? 1 : 0].track;
    const tracks = this.races && this.races.tracks ? [...this.races.tracks.entries()].filter(([k]) => k.startsWith(TABLE[i].city + '/')) : [];
    if (!tracks.length) return null;
    const [k, gt] = tracks[Math.floor(this.teams.rng() * tracks.length)];
    return { key: k, name: gt.name || k, laps: gt.laps || 1, length: Math.max(100, Math.round(gt.lapLength || 1000)) };
  }

  // ---------------- spectating (0x387 / 0x388), cheering (0x394), replays (0x395 / 0x396) ----------------
  spectateList() {
    const w = this.window();
    const rows = TABLE.map(c => {
      const room = this.room(c.index);
      if (!room) return null;
      const own = this.ownerRow(c.index);
      return { channel: c.index, key: c.key, label: c.label, city: c.city, room: room.id, state: room.state === 'lobby' ? 'wait' : 'play', viewers: room.watchers.size, spectate: room.spectate,
        owner: room.crewNames[0] || (own ? (this.q2.crew.get(own.crew_id) || {}).name : ''), attacker: room.crewNames[1] || '' };
    }).filter(Boolean);
    return { ok: true, open: w.open, rows };
  }

  cheer(session, m) {
    const room = session.race;
    if (!room || !room.crews) return fail('You can cheer while watching a Channel Battle.');
    const target = typeof m.target === 'string' ? m.target : '';
    const racer = [...room.racers.values()].find(r => r.name === target);
    if (!racer) return fail('Pick a racer to cheer.');
    const now = this.clock();
    if (now - (session.cheerAt || 0) < CHEER_MS) return fail('too soon');
    const p = session.profile;
    if (!p || this.teams.count(p, FIREWORKS) <= 0) return fail("You don't have Mittron Fireworks.");
    this.teams.give(p, FIREWORKS, -1);
    session.cheerAt = now;
    session.save();
    room.send({ t: 'crew.cheer', target, from: session.name, room: room.id });
    return { ok: true, driver: p };
  }

  replays(m) {
    const page = Math.max(0, m.page | 0);
    const rows = this.q.replays.all(8, page * 8).map(r => ({ id: r.id, channel: r.channel, label: TABLE[r.channel] ? TABLE[r.channel].label : '', match: r.match, round: r.round,
      owner: r.owner, challenger: r.challenger, ownerMark: r.owner_mark, challengerMark: r.challenger_mark, winner: r.winner, views: r.views, price: REPLAY_PRICE(r.channel), at: r.at }));
    return { ok: true, page, total: this.q.replayCount.get().n, rows };
  }

  /** Watching a replay costs its price; the owner's Master gets the fee (20350). */
  replay(session, m) {
    const r = this.q.replay.get(m.id | 0);
    if (!r) return fail('replay not found');
    const price = REPLAY_PRICE(r.channel);
    const p = session.profile;
    if (price > 0) {
      if ((p.Mito | 0) < price) return fail(`You need ${price} Mito to watch the selected replay. Do you want to proceed?`, 20349);
      p.Mito -= price;
      const oc = this.q2.crewByName.get(r.owner);
      if (oc && oc.master !== session.accountId) this.teams.social.mail.system(oc.master, `You've received ${price} Mito from Channel Battle Replay Fees collected.`, price);
    }
    this.q.viewed.run(r.id);
    // the channel's Ghost Battle ticket comes with the replay (0x397; useitem ch_ghost_01..20 = Channel 1..10, Semi 1..10),
    // unless the driver has the Ghost Pass (ch_ghostpass 10825), which needs none
    const ticket = ghostTicket(r.channel);
    if (!Array.isArray(p.Items)) p.Items = [];
    const hasPass = p.Items.some(i => i.Id === 'ch_ghostpass' && i.Count > 0);
    if (ticket && !hasPass) giveItem(p, ticket, 1);
    session.save();
    let data = {};
    try { data = JSON.parse(r.data || '{}'); } catch { data = {}; }
    return { ok: true, price, driver: p, ticket: ticket && !hasPass ? ticket : null, replay: { id: r.id, channel: r.channel, label: TABLE[r.channel] ? TABLE[r.channel].label : '', match: r.match, round: r.round, owner: r.owner, challenger: r.challenger, winner: r.winner, views: r.views + 1, ...data } };
  }

  /** GHOSTJOIN in a replay (0x759060): the first join of a race uses up the channel's ticket (the pass needs none). */
  useGhostTicket(session, m) {
    const p = session.profile;
    if (!Array.isArray(p.Items)) p.Items = [];
    if (p.Items.some(i => i.Id === 'ch_ghostpass' && i.Count > 0)) return { ok: true, pass: true };
    const ticket = ghostTicket(m.channel | 0);
    const it = ticket && p.Items.find(i => i.Id === ticket && i.Count > 0);
    if (!it) return fail('You cannot join the Ghost Battle at this time.', 11196);
    giveItem(p, ticket, -1);
    session.save();
    return { ok: true, ticket, driver: p };
  }

  // ---------------- the window closes: vacant channels go to their holders ----------------
  tick() {
    const w = this.window();
    if (this.lastOpen === null) { this.lastOpen = w.open ? w.id : ''; return; }
    if (this.lastOpen && (!w.open || w.id !== this.lastOpen)) this.closeWindow();
    this.lastOpen = w.open ? w.id : '';
  }

  closeWindow() {
    for (const c of TABLE) {
      const room = this.room(c.index);
      const had = (this.history.get(c.index) || []).length > 0;
      if (!this.ownerRow(c.index) && room && room.crews[0]) {
        this.teams.capture(c.key, room.crews[0], 'held');
        const holder = [...room.racers.values()].find(r => !r.ai && !(r.slot & 1));   // the holder's racer leads (SERVICES.md 6)
        try { this.q.setLeader.run(holder ? holder.name : '', c.key); } catch { /* no row */ }
        this.teams.social.log(`channel ${c.key}: held by ${room.crewNames[0]} when the window closed`);
      }
      if ((had || room) && this.teams.notices) this.teams.notices.broadcast('chat', { key: c.semi ? '20289' : '20210', args: [c.number], text: c.semi ? `Semi ${c.number} Channel Tournament completed.` : `${c.number} Channel Tournament completed.` });
      this.queues.delete(c.index);
      this.turns.delete(c.index);
      this.history.delete(c.index);
    }
  }

  handle(session, m) {
    switch (m.t) {
      case 'crew.table': return this.table();
      case 'crew.enter': return this.enter(session, m);
      case 'crew.ready': return this.ready(session, m);
      case 'crew.lobby': { const i = m.channel | 0; if (i < 0 || i >= CHANNELS) return fail('no such channel'); const { t, ...l } = this.lobby(i); return { ok: true, ...l }; }
      case 'crew.leaveLobby': this.leaveLobby(session); return { ok: true };
      case 'crew.setup': return this.setup(session, m);
      case 'crew.maps': return this.mapInfo(m);
      case 'crew.spectate': return this.spectateList();
      case 'crew.cheer': return this.cheer(session, m);
      case 'crew.replays': return this.replays(m);
      case 'crew.replay': return this.replay(session, m);
      case 'crew.ghost': return this.useGhostTicket(session, m);
    }
    return null;
  }
}

module.exports = { ChannelBattles, TABLE, channelIndex, channelInfo, ladderClass, parseSchedule, windowAt, COUPON, FIREWORKS, REPLAY_PRICE, WINDOW_MS, SETUP_MS, LIMITS };
