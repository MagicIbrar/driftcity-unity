// The lap-time battle (DriftCity_RE/PVP.md "Lap-time battle"; the client's lap manager game+0x174, 0x71bc50..0x7212d0):
// every channel of a city with a lap-time track (pvptrackinfo CityType 4, "<city>_Laptime_01", 5 laps) runs one session.
// Drivers join at any time (0x528), start their own run with a 10 s countdown (0x529 -> 0x52a / 0x52f to the others),
// report every gate (0x52b) and lap (0x52c); the server checks them, keeps each driver's best lap of the round, echoes
// the laps with the lap reward (0x52c), announces the round's end time (0x52d) and at the end the top three with their
// rewards (0x52e), then a new round starts (the client resets the cars 8 s later).
'use strict';

const { gatePoint, prevPoint, dist } = require('./pvptracks');
const { addExp } = require('./economy');

const ROUND_MS = 10 * 60 * 1000;     // remake rule (the server's 0x52d end time)
const BREAK_MS = 8000;               // results -> new round (client: 1.0 s to LAPTIMERET, 7.0 s shown)
const START_MS = 10000;              // 0x52a: the lap clock starts 10 s after the start time
const MAX_AVG_SPEED = 75;
// remake amounts: per completed lap, and the round's top three (LAPTIMERET shows 3 reward icons per row)
const LAP_REWARD = level => ({ exp: 10 + level, mito: 50 });
const TOP_REWARDS = [
  [{ type: 3, amount: 3000 }, { type: 5, amount: 500 }],
  [{ type: 3, amount: 2000 }, { type: 5, amount: 300 }],
  [{ type: 3, amount: 1000 }, { type: 5, amount: 200 }],
];

class LapSession {
  constructor(channel, track) {
    this.channel = channel; this.track = track;
    this.players = new Map();   // sessionId -> { session, name, level, best, laps: [], gate, gateAt, lapAt, running, startAt }
    this.endsAt = 0; this.timer = null; this.state = 'running';
  }
  send(msg) { for (const p of this.players.values()) p.session.send(msg); }
  /** Best list sorted (0x8ebbc4): drivers with a lap, fastest first. */
  ranking() { return [...this.players.values()].filter(p => p.best > 0).sort((a, b) => a.best - b.best); }
  rankOf(p) { const i = this.ranking().indexOf(p); return i < 0 ? 0 : i + 1; }
  info(p) { return { name: p.name, level: p.level, best: p.best, laps: p.laps.slice(-5), rank: this.rankOf(p), running: p.running }; }
}

class LapTime {
  /** opts: { clock, tracks (src/pvptracks.js), roundMs, breakMs, startMs, maxAvgSpeed } */
  constructor(world, store, log = () => {}, opts = {}) {
    this.world = world; this.store = store; this.log = log;
    this.clock = opts.clock || Date.now;
    this.tracks = opts.tracks || new Map();
    this.roundMs = opts.roundMs ?? ROUND_MS;
    this.breakMs = opts.breakMs ?? BREAK_MS;
    this.startMs = opts.startMs ?? START_MS;
    this.maxAvgSpeed = opts.maxAvgSpeed ?? MAX_AVG_SPEED;
    this.sessions = new Map();  // channel id -> LapSession
  }

  owns(t) { return /^lap\./.test(t); }

  handle(session, m) {
    switch (m.t) {
      case 'lap.enter': return this.enter(session);
      case 'lap.start': return this.start(session);
      case 'lap.gate': this.gate(session, m); return undefined;
      case 'lap.leave': this.leave(session); return { ok: true };
    }
    return { ok: false, error: 'unknown message' };
  }

  /** The city's lap-time track (CityType 4); a fallback track without the table. */
  trackFor(city) {
    for (const t of this.tracks.values()) if (t.city === city && t.cityType === 4) return t;
    return null;
  }

  enter(session) {
    if (!session.channel) return { ok: false, error: 'not in a city' };
    if (session.race) return { ok: false, error: 'already in a race' };
    const track = this.trackFor(session.channel.city);
    if (!track) return { ok: false, error: 'This feature is currently unavailable.', code: 10745 };
    if (session.lap && session.lap.channel !== session.channel) this.leave(session);
    let ls = this.sessions.get(session.channel.id);
    if (!ls) { ls = new LapSession(session.channel, track); this.sessions.set(session.channel.id, ls); this.newRound(ls); }
    if (!ls.players.has(session.id)) {
      ls.players.set(session.id, { session, name: session.name, level: (session.profile && session.profile.Level) | 0, best: 0, laps: [], gate: 0, gateAt: 0, lapAt: 0, running: false, startAt: 0 });
      session.lap = ls;
      ls.send({ t: 'race.lapPlayers', players: [...ls.players.values()].map(p => ls.info(p)) });
    }
    return { ok: true, track: { key: track.key, name: track.name, laps: track.laps, gates: track.gates.length }, endsIn: Math.max(0, ls.endsAt - this.clock()), state: ls.state, players: [...ls.players.values()].map(p => ls.info(p)) };
  }

  newRound(ls) {
    clearTimeout(ls.timer);
    ls.state = 'running';
    ls.endsAt = this.clock() + this.roundMs;
    for (const p of ls.players.values()) { p.best = 0; p.laps = []; p.running = false; p.gate = 0; }
    ls.send({ t: 'race.lapRound', endsIn: this.roundMs });
    ls.timer = setTimeout(() => { try { this.endRound(ls); } catch (err) { this.log(`lap time ${ls.channel.id}: ${err.message}`); } }, this.roundMs);
    if (ls.timer.unref) ls.timer.unref();
  }

  /** 0x52e: the top three of the round with their rewards, every driver's rank; a new round after the break. */
  endRound(ls) {
    clearTimeout(ls.timer);
    ls.state = 'results';
    const ranked = ls.ranking();
    const top = ranked.slice(0, 3).map((p, i) => {
      const rewards = TOP_REWARDS[i];
      this.pay(p.session, rewards.reduce((a, r) => ({ exp: a.exp + (r.type === 5 ? r.amount : 0), mito: a.mito + (r.type === 3 ? r.amount : 0) }), { exp: 0, mito: 0 }));
      return { name: p.name, level: p.level, time: p.best, rewards };
    });
    for (const p of ls.players.values()) {
      const rank = ls.rankOf(p);
      p.session.send({ t: 'race.lapResults', top, me: { rank, time: p.best, rewards: rank >= 1 && rank <= 3 ? TOP_REWARDS[rank - 1] : [] } });
      p.running = false;
    }
    this.log(`lap time ${ls.channel.id}: round over, ${top.map(t => `${t.name} ${t.time}`).join(', ') || 'no laps'}`);
    if (ls.players.size === 0) { this.sessions.delete(ls.channel.id); return; }
    ls.timer = setTimeout(() => { try { this.newRound(ls); } catch (err) { this.log(`lap time ${ls.channel.id}: ${err.message}`); } }, this.breakMs);
    if (ls.timer.unref) ls.timer.unref();
  }

  /** 0x529 -> 0x52a: a run of the track's laps; the lap clock starts startMs after now. */
  start(session) {
    const ls = session.lap;
    if (!ls) return { ok: false, error: 'not in the lap-time battle' };
    if (ls.state !== 'running') return { ok: false, error: 'the round is over' };
    const p = ls.players.get(session.id);
    const now = this.clock();
    p.running = true; p.gate = 0; p.startAt = now + this.startMs; p.gateAt = p.startAt; p.lapAt = p.startAt;
    // 0x52f: the others see this driver's countdown
    for (const o of ls.players.values()) if (o !== p) o.session.send({ t: 'race.lapStarting', name: p.name, startIn: this.startMs });
    return { ok: true, startIn: this.startMs, laps: ls.track.laps };
  }

  /** 0x52b / 0x52c: gate n of the run (each lap = the gates then the line); a lap closes on its line. */
  gate(session, m) {
    const ls = session.lap;
    if (!ls || ls.state !== 'running') return;
    const p = ls.players.get(session.id);
    if (!p || !p.running) return;
    const t = ls.track, per = t.per, total = t.laps * per;
    const n = m.n | 0;
    if (n !== p.gate + 1 || n > total) return;
    const now = this.clock();
    if (now < p.startAt) return;
    const minMs = dist(prevPoint(t, n), gatePoint(t, n)) / this.maxAvgSpeed * 1000;
    if (now - p.gateAt < minMs * 0.9) { p.running = false; session.send({ t: 'race.lapRejected', reason: 'gate time not plausible' }); return; }
    p.gate = n; p.gateAt = now;
    if (n % per !== 0) return;
    const time = Math.round(now - p.lapAt) / 1000;
    p.lapAt = now;
    if (time < t.lapLength / this.maxAvgSpeed) { p.running = false; session.send({ t: 'race.lapRejected', reason: 'lap time not plausible' }); return; }
    const lap = n / per;
    p.laps.push(time);
    const record = !p.best || time < p.best;
    if (record) p.best = time;
    const reward = this.pay(session, LAP_REWARD(p.level));
    if (lap >= t.laps) p.running = false;
    try { if (record) this.store.addResult(t.key, p.name, 0, time); } catch { /* ranking only */ }
    ls.send({ t: 'race.lapLap', name: p.name, lap, laps: t.laps, time, best: p.best, record, rank: ls.rankOf(p), exp: reward.exp, mito: reward.mito, done: lap >= t.laps });
  }

  pay(session, r) {
    const p = session.profile;
    if (!p || session.closed) return { exp: 0, mito: 0 };
    p.Mito = (Number(p.Mito) || 0) + r.mito;
    const levels = addExp(p, r.exp);
    session.save(); session.send({ t: 'driver', driver: p });
    return { ...r, levelUps: levels };
  }

  leave(session) {
    const ls = session.lap;
    if (!ls) return;
    session.lap = null;
    ls.players.delete(session.id);
    ls.send({ t: 'race.lapPlayers', players: [...ls.players.values()].map(p => ls.info(p)) });
    if (ls.players.size === 0) { clearTimeout(ls.timer); this.sessions.delete(ls.channel.id); }
  }

  closeAll() { for (const ls of this.sessions.values()) clearTimeout(ls.timer); this.sessions.clear(); }
}

module.exports = { LapTime, ROUND_MS, START_MS, TOP_REWARDS, LAP_REWARD };
