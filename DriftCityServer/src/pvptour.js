// GT Championship online (DriftCity_RE/PVP.md section 10 and "Unity port"): the client's tournament channels (types 7..10
// = classes Rookie / Touring B / Touring A / GT Master) run a server bracket. The remake's bracket matches the offline one
// (Tournament.cs): 16 entrants - the players who enter a class in the channel during the sign-up time, filled with AI
// entrants -, Preliminaries (time attack: each player's heat sets a best lap, AI laps are simulated, the 8 best go on),
// Quarter-finals (two heats of 4, top 2 each), Semi-finals (one heat of 4 over two rounds, 4/3/2/1 points, top 2) and
// the Final (1-on-1). Heats with players are real server races (src/race.js rooms with the heat's AI entrants as AI
// drivers); heats without players are simulated. The reward follows the stage reached (the offline amounts) plus the
// class's reward table (init\rewardgroup.xlt rw_gtc_<class>_a_<bracket>, TOUR_REWARD 4510000), paid by the server.
'use strict';

const { addExp } = require('./economy');
const items = require('./items');

const CLASSES = ['rookie', 'touringb', 'touringa', 'gtmaster'];
const CLASS_NAMES = ['Rookie', 'Touring B', 'Touring A', 'GT Master'];
const STAGES = ['prelim', 'quarter', 'semi', 'final', 'over'];
const SIGNUP_MS = 60000;           // remake rule: time to gather players before the bracket fills with AI entrants
const HEAT_WAIT_MS = 120000;       // a heat starts with whoever is ready after this long
const STAGE_EXP = [200, 500, 1000, 2000, 4000], STAGE_MITO = [1000, 3000, 6000, 12000, 25000], CLASS_FACTOR = [1, 1.5, 2, 3];
const AI_NAMES = ['Rio', 'Duke', 'Mika', 'Jin', 'Sora', 'Kai', 'Nova', 'Taro', 'Yuna', 'Leon', 'Hana', 'Max', 'Zed', 'Aki', 'Rex', 'Luna', 'Ivan', 'Mei', 'Toby', 'Ken'];

class Tours {
  /** opts: { clock, signupMs, heatWaitMs, rng, rewardGroups } */
  constructor(races, world, log = () => {}, opts = {}) {
    this.races = races; this.world = world; this.log = log;
    this.clock = opts.clock || Date.now;
    this.signupMs = opts.signupMs ?? SIGNUP_MS;
    this.heatWaitMs = opts.heatWaitMs ?? HEAT_WAIT_MS;
    this.rng = opts.rng || Math.random;
    this.rewardGroups = opts.rewardGroups || null;
    this.tours = new Map();   // `${channel id}/${class}` -> tour
    races.tourHook = (room, results) => { try { this.onResults(room, results); } catch (err) { this.log(`tour results: ${err.message}`); } };
  }

  owns(t) { return /^tour\./.test(t); }

  handle(session, m) {
    switch (m.t) {
      case 'tour.enter': return this.enter(session, m);
      case 'tour.state': { const t = session.tour; return t ? { ok: true, tour: this.info(t, session) } : { ok: true, tour: null }; }
      case 'tour.race': return this.race(session);
      case 'tour.leave': this.leave(session); return { ok: true };
    }
    return { ok: false, error: 'unknown message' };
  }

  /** Class rule (Tournament.ClassOpen, remake): Rookie up to level 40, Touring B car grade <= 6, Touring A <= 8, GT Master open. */
  classOpen(session, c) {
    const p = session.profile || {};
    const car = Array.isArray(p.Cars) ? p.Cars[p.CurrentCar | 0] : null;
    const info = car && this.races.gd ? this.races.gd.car(car.VehicleIndex) : null;
    const grade = info ? info.grade : 1;
    return c === 0 ? (p.Level | 0) <= 40 : c === 1 ? grade <= 6 : c === 2 ? grade <= 8 : true;
  }

  enter(session, m) {
    if (!session.channel) return { ok: false, error: 'not in a city' };
    const c = m.class | 0;
    if (c < 0 || c > 3) return { ok: false, error: 'bad class' };
    if (session.tour && session.tour.state !== 'over') {
      if (session.tour.cls !== c) return { ok: false, error: `You are entered in the ${CLASS_NAMES[session.tour.cls]} class.` };
      return { ok: true, tour: this.info(session.tour, session) };
    }
    if (!this.classOpen(session, c)) return { ok: false, error: `Your car or level does not meet the ${CLASS_NAMES[c]} class limits.` };
    const key = `${session.channel.id}/${c}`;
    let t = this.tours.get(key);
    if (!t || t.state !== 'signup' || t.entrants.filter(e => !e.ai).length >= 16) {
      const tracks = (Array.isArray(m.tracks) ? m.tracks : []).filter(k => typeof k === 'string' && this.races.tracks.has(k.toLowerCase()) && this.races.tracks.get(k.toLowerCase()).cityType === 1);
      const k = tracks.length ? tracks[Math.floor(this.rng() * tracks.length)] : (typeof m.track === 'string' ? m.track : `${session.channel.city}/track1`);
      const gt = this.races.tracks.get(String(k).toLowerCase());
      t = { key, cls: c, channel: session.channel, state: 'signup', stage: 0, round: 0, entrants: [], heats: [], track: { key: k, name: gt ? gt.name : k, laps: gt ? gt.laps : 1, length: Math.max(1500, gt ? Math.round(gt.lapLength) : 1500) }, signupEnds: this.clock() + this.signupMs };
      this.tours.set(key, t);
      t.timer = setTimeout(() => this.safe(() => this.closeSignup(t)), this.signupMs);
      if (t.timer.unref) t.timer.unref();
    }
    t.entrants.push({ name: session.name, accountId: session.accountId, session, ai: false, out: false, lap: 0, points: 0, level: (session.profile && session.profile.Level) | 0 });
    session.tour = t;
    this.push(t);
    return { ok: true, tour: this.info(t, session) };
  }

  safe(fn) { try { fn(); } catch (err) { this.log(`tour: ${err.message}`); } }

  /** Sign-up over: AI entrants fill the field to 16, the preliminaries begin. */
  closeSignup(t) {
    if (t.state !== 'signup') return;
    const used = new Set(t.entrants.map(e => e.name));
    const free = AI_NAMES.filter(n => !used.has(n));
    while (t.entrants.length < 16) {
      const name = free.length ? free.splice(Math.floor(this.rng() * free.length), 1)[0] : `AI ${t.entrants.length + 1}`;
      t.entrants.push({ name, ai: true, out: false, lap: 0, points: 0, level: 10, skill: Math.min(0.99, 0.82 + this.rng() * 0.15 + 0.02 * t.cls) });
    }
    t.state = 'running';
    this.startStage(t, 0);
  }

  alive(t) { return t.entrants.filter(e => !e.out); }

  /** A stage's heats: each player gets a heat; the AI entrants fill them (and the heats without players are simulated). */
  startStage(t, stage) {
    t.stage = stage;
    if (stage === 2 && t.round === 0) for (const e of t.entrants) e.points = 0;
    const alive = this.alive(t);
    const humans = alive.filter(e => !e.ai), ais = alive.filter(e => e.ai).sort(() => this.rng() - 0.5);
    t.heats = [];
    if (stage === 0) {
      for (const h of humans) t.heats.push({ members: [h, ...ais.splice(0, 3)], done: false });
    } else if (stage === 1) {
      const pool = [...humans, ...ais];
      t.heats.push({ members: pool.slice(0, 4), done: false });
      t.heats.push({ members: pool.slice(4, 8), done: false });
    } else {
      t.heats.push({ members: alive.slice(0, stage === 3 ? 2 : 4), done: false });
    }
    for (const heat of t.heats) {
      if (!heat.members.some(m => !m.ai)) { heat.done = true; heat.places = this.simulate(t, heat.members); continue; }
      heat.ready = new Set();
      heat.wait = setTimeout(() => this.safe(() => this.startHeat(t, heat, true)), this.heatWaitMs);
      if (heat.wait.unref) heat.wait.unref();
    }
    t.message = `${['Preliminaries', 'Quarter-finals', 'Semi-finals', 'Final'][stage]} on ${t.track.name}`;
    this.push(t);
    this.maybeNext(t);
  }

  /** A simulated heat: places by skill with a little luck; laps from the track length over 30 m/s x skill. */
  simulate(t, members) {
    for (const m of members) if (m.ai) m.lap = t.track.length / Math.max(5, 30 * m.skill * (0.95 + this.rng() * 0.1));
    return members.slice().sort((a, b) => (a.lap || 1e9) - (b.lap || 1e9));
  }

  heatOf(t, session) { return t.heats.find(h => !h.done && h.members.some(m => m.session === session)); }

  /** The player clicks the heat's row: the heat starts once all its players are ready. */
  race(session) {
    const t = session.tour;
    if (!t || t.state !== 'running') return { ok: false, error: 'no championship race for you now' };
    const heat = this.heatOf(t, session);
    if (!heat) return { ok: false, error: 'no championship race for you now' };
    if (heat.room) return { ok: true, waiting: false };
    heat.ready.add(session.id);
    const humans = heat.members.filter(m => !m.ai && m.session && !m.session.closed);
    if (humans.every(m => heat.ready.has(m.session.id))) this.startHeat(t, heat, false);
    return { ok: true, waiting: !heat.room };
  }

  startHeat(t, heat, timedOut) {
    if (heat.room || heat.done) return;
    clearTimeout(heat.wait);
    const players = heat.members.filter(m => !m.ai && m.session && !m.session.closed && (!timedOut || heat.ready.has(m.session.id)));
    if (players.length === 0) { heat.done = true; heat.places = this.simulate(t, heat.members.filter(m => m.ai)); this.maybeNext(t); return; }
    const hostSession = players[0].session;
    if (hostSession.race) this.races.leave(hostSession);
    const r = this.races.host(hostSession, { track: t.track, room: { title: `GT Championship ${CLASS_NAMES[t.cls]}`, team: false, max: 8, arena: 5, mapFlags: 0x24 } });
    if (!r.ok) { this.log(`tour heat: ${r.error}`); heat.done = true; heat.places = this.simulate(t, heat.members); this.maybeNext(t); return; }
    const room = this.races.rooms.get(r.id);
    room.tour = { tour: t, heat };
    for (const p of players.slice(1)) { if (p.session.race) this.races.leave(p.session); this.races.join(p.session, room.id, ''); const rr = room.racers.get(p.session.id); if (rr) rr.ready = true; }
    let slot = 1;
    for (const m of heat.members.filter(x => x.ai)) {
      while (slot < room.max && room.slots[slot] !== 0) slot++;
      const a = this.races.addAi(room, slot, true);
      if (a.ok) { const ar = room.racers.get(a.id); ar.name = m.name; ar.session.name = m.name; ar.skill = m.skill; }
    }
    heat.room = room;
    this.races.changed(room);
    const s = this.races.start(hostSession);
    if (!s.ok) this.log(`tour heat start: ${s.error}`);
  }

  /** race.js finishRoom for a championship room: the heat's places and laps go into the bracket. */
  onResults(room, results) {
    const { tour: t, heat } = room.tour || {};
    if (!t || !heat || heat.done) return;
    const byName = new Map(results.map(r => [r.name, r]));
    for (const m of heat.members) {
      const r = byName.get(m.name);
      const lap = r && r.place > 0 ? r.time / Math.max(1, t.track.laps) : 0;
      if (t.stage === 0) m.lap = lap || (m.ai ? t.track.length / (30 * m.skill) : 1e9);
    }
    heat.places = heat.members.slice().sort((a, b) => {
      const pa = (byName.get(a.name) || {}).place || 99, pb = (byName.get(b.name) || {}).place || 99;
      return (pa || 99) - (pb || 99);
    });
    heat.done = true;
    heat.room = null;
    this.maybeNext(t);
  }

  /** All heats done: the stage's rule picks who goes on (offline Tournament.OnRaceDone). */
  maybeNext(t) {
    if (t.state !== 'running' || t.heats.some(h => !h.done)) return;
    const stage = t.stage;
    if (stage === 0) {
      // simulate the laps of every AI entrant who had no heat, then the 8 best laps
      for (const e of t.entrants) if (e.ai && !e.lap) e.lap = t.track.length / Math.max(5, 30 * e.skill * (0.95 + this.rng() * 0.1));
      const ranked = t.entrants.slice().sort((a, b) => (a.lap || 1e9) - (b.lap || 1e9));
      ranked.forEach((e, i) => { e.out = i >= 8; if (e.out) this.eliminated(t, e, 0, 9 + Math.max(0, i - 8)); });
      return this.startStage(t, 1);
    }
    if (stage === 1) {
      for (const h of t.heats) h.places.forEach((e, i) => { if (i >= 2) { e.out = true; this.eliminated(t, e, 1, 5 + i - 2); } });
      return this.startStage(t, 2);
    }
    if (stage === 2) {
      const h = t.heats[0];
      h.places.forEach((e, i) => { e.points += 4 - i; });
      if (t.round === 0) { t.round = 1; return this.startStage(t, 2); }
      const ranked = this.alive(t).slice().sort((a, b) => b.points - a.points);
      ranked.forEach((e, i) => { if (i >= 2) { e.out = true; this.eliminated(t, e, 2, 3 + i - 2); } });
      t.round = 0;
      return this.startStage(t, 3);
    }
    if (stage === 3) {
      const [win, second] = t.heats[0].places;
      if (second) { second.out = true; this.eliminated(t, second, 3, 2); }
      if (win) this.eliminated(t, win, 4, 1);
      t.state = 'over';
      t.message = win ? `${win.name} is the ${CLASS_NAMES[t.cls]} class champion!` : 'Championship over';
      this.push(t);
      this.tours.delete(t.key);
    }
  }

  /** A player's championship ends: the stage reward (offline amounts x class) and the reward table of the rank. */
  eliminated(t, e, reached, rank) {
    e.rank = rank;
    if (e.ai || !e.accountId) return;
    const f = CLASS_FACTOR[t.cls];
    const exp = Math.round(STAGE_EXP[reached] * f), mito = Math.round(STAGE_MITO[reached] * f);
    const bracket = rank <= 1 ? '01' : rank === 2 ? '02' : rank === 3 ? '03' : rank <= 8 ? '0408' : '0916';
    const rows = this.rewardGroups ? (this.rewardGroups.get(`rw_gtc_${CLASSES[t.cls]}_${this.races.tourSet ? this.races.tourSet(t.cls) : 'a'}_${bracket}`) || []) : [];
    const given = [];
    this.races.withProfile(e.accountId, p => {
      p.Mito = (Number(p.Mito) || 0) + mito;
      addExp(p, exp);
      p.TourBest = Math.max(p.TourBest | 0, reached);
      for (const r of rows) if (r.item && r.type !== 'RewardGroup') { if (!Array.isArray(p.Items)) p.Items = []; items.give(p, r.item, Math.max(1, r.count)); given.push(r.desc); }
    });
    e.reward = { exp, mito, items: given, reached, rank };
    if (e.session && !e.session.closed) e.session.send({ t: 'race.tourReward', class: t.cls, reached, rank, exp, mito, items: given });
  }

  info(t, session) {
    const me = t.entrants.find(e => e.session === session);
    const heat = session ? this.heatOf(t, session) : null;
    return {
      class: t.cls, state: t.state, stage: STAGES[t.stage], stageIndex: t.stage, round: t.round, track: t.track, message: t.message || '',
      signupIn: t.state === 'signup' ? Math.max(0, t.signupEnds - this.clock()) : 0,
      entrants: t.entrants.map(e => ({ name: e.name, ai: e.ai, out: e.out, lap: Math.round((e.lap || 0) * 1000) / 1000, points: e.points, me: e.session === session })),
      heats: t.heats.map(h => ({ names: h.members.map(m => m.name), done: h.done, mine: h.members.some(m => m.session === session) })),
      myHeat: heat ? { names: heat.members.map(m => m.name), waiting: heat.ready ? heat.ready.has(session.id) : false } : null,
      me: me ? { out: me.out, rank: me.rank || 0, reward: me.reward || null } : null,
    };
  }

  /** race.tour to every player of the tournament. */
  push(t) { for (const e of t.entrants) if (!e.ai && e.session && !e.session.closed && e.session.tour === t) e.session.send({ t: 'race.tour', tour: this.info(t, e.session) }); }

  leave(session) {
    const t = session.tour;
    if (!t) return;
    session.tour = null;
    const e = t.entrants.find(x => x.session === session);
    if (e && t.state === 'signup') t.entrants.splice(t.entrants.indexOf(e), 1);
    else if (e) e.session = null;   // a no-show from here on loses its heats (Championship_infotext: disqualified)
  }

  closeAll() {
    for (const t of this.tours.values()) { clearTimeout(t.timer); for (const h of t.heats) clearTimeout(h.wait); }
    this.tours.clear();
  }
}

module.exports = { Tours, CLASSES, SIGNUP_MS };
