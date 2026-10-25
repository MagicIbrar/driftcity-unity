'use strict';
// The small screens' server side (DriftCity_RE/SCREENS.md 8):
//  - AEROSET 3900000 (packet 0x4be, 11064): the bonus points of a custom aero set on a car whose vehiclelist AeroSet
//    column is 1. The server checks the budget and keeps the points in the car (profile Cars[i].AeroPoints).
//    Remake rule: AEROSET_BUDGET points over Speed / Accel / Crash / Boost, at most AEROSET_STAT_MAX on one stat,
//    each point one rating level (the client's ShopData.Stats adds them; see Game/AeroSet.cs).
//  - AUTOFINDER 3420000 (op 0x51e, answer 0x799cf0): the anti-macro quiz from init\autofinder.xlt (59 rows: question,
//    four answers, the right one). Off unless opts.autofinder / env DC_AUTOFINDER=1. Remake rule: a driver in a city
//    channel (not racing) gets a question every 45 min +- 15 min of play; 30 s to answer (10555); right 10556, wrong or
//    no answer 10557; three misses in a row disconnect the driver ("macro check"). Admins can ask at once
//    (autofinder.test).

const fs = require('fs');
const path = require('path');
const { XltTable } = require('./gamedata');

const AEROSET_BUDGET = 6;
const AEROSET_STAT_MAX = 3;
const AF_INTERVAL_MS = 45 * 60 * 1000, AF_JITTER_MS = 15 * 60 * 1000, AF_ANSWER_MS = 30000, AF_MAX_MISSES = 3;

function loadAutofinder(dir) {
  try {
    const t = new XltTable(fs.readFileSync(path.join(dir, 'init', 'autofinder.xlt')), 2);
    return t.rows.map(r => ({ q: String(r[1] || '').trim(), a: [r[2], r[3], r[4], r[5]].map(x => String(x || '').trim()), right: (parseInt(r[6], 10) || 1) - 1 }))
      .filter(x => x.q && x.right >= 0 && x.right < 4);
  } catch { return []; }
}

function loadAeroCars(dir) {
  const set = new Set();
  try {
    const t = new XltTable(fs.readFileSync(path.join(dir, 'init', 'vehiclelist.xlt')), 3);
    for (const r of t.rows) if (t.get(r, 'AeroSet') === '1') set.add(parseInt(t.get(r, 'Index'), 10));
  } catch { /* no data */ }
  return set;
}

class SmallScreens {
  constructor(world, log = () => {}, opts = {}) {
    this.world = world; this.log = log;
    this.clock = opts.clock || Date.now;
    this.rng = opts.rng || Math.random;
    const dir = opts.dataDir || null;
    this.questions = opts.questions || (dir ? loadAutofinder(dir) : []);
    this.aeroCars = opts.aeroCars || (dir ? loadAeroCars(dir) : new Set());
    this.autofinder = opts.autofinder ?? process.env.DC_AUTOFINDER === '1';
    this.afInterval = opts.autofinderIntervalMs ?? AF_INTERVAL_MS;
    this.afJitter = opts.autofinderJitterMs ?? AF_JITTER_MS;
    this.afAnswer = opts.autofinderAnswerMs ?? AF_ANSWER_MS;
    this.timer = null;
    if (this.autofinder && this.questions.length) {
      this.timer = setInterval(() => { try { this.tick(); } catch (err) { this.log(`autofinder: ${err.message}`); } }, opts.autofinderTickMs || 5000);
      if (this.timer.unref) this.timer.unref();
    }
  }

  owns(t) { return /^(aero|autofinder)\./.test(t); }

  handle(session, m) {
    switch (m.t) {
      case 'aero.set': return this.aeroSet(session, m);
      case 'autofinder.answer': return this.answer(session, m);
      case 'autofinder.test':
        if (!session.admin) return { ok: false, error: 'not allowed' };
        return this.ask(session) ? { ok: true } : { ok: false, error: 'no questions' };
      default: return { ok: false, error: 'unknown message' };
    }
  }

  // ---------------- AEROSET ----------------
  aeroSet(session, m) {
    const p = session.profile;
    const cars = Array.isArray(p.Cars) ? p.Cars : [];
    const i = Number.isInteger(m.car) ? m.car : p.CurrentCar | 0;
    const car = cars[i];
    if (!car) return { ok: false, error: 'no such car' };
    if (this.aeroCars.size && !this.aeroCars.has(car.VehicleIndex | 0)) return { ok: false, error: 'This car takes no aero set.' };
    if (!((car.Aero | 0) >= 0)) return { ok: false, error: 'No aero set is fitted.' };
    const pts = Array.isArray(m.points) ? m.points.slice(0, 4).map(x => x | 0) : [];
    if (pts.length !== 4 || pts.some(x => x < 0 || x > AEROSET_STAT_MAX)) return { ok: false, error: 'bad points' };
    const used = pts.reduce((a, b) => a + b, 0);
    if (used !== AEROSET_BUDGET) return { ok: false, error: 'You still have unused points.', code: 11014 };
    car.AeroPoints = pts;
    if (typeof session.save === 'function') session.save();
    return { ok: true, car: i, points: pts, driver: p, code: 11064 };
  }

  // ---------------- AUTOFINDER ----------------
  tick() {
    const now = this.clock();
    for (const s of this.world.sessions.values()) {
      if (s.closed || !s.name || !s.channel) continue;
      if (s.af && s.af.pending) {
        if (now >= s.af.pending.until) this.miss(s, 'timeout');
        continue;
      }
      if (s.race || s.lap) continue;
      if (!s.af) { s.af = { next: now + this.afInterval + (this.rng() * 2 - 1) * this.afJitter, misses: 0, pending: null }; continue; }
      if (now >= s.af.next) this.ask(s);
    }
  }

  ask(s) {
    if (!this.questions.length) return false;
    const now = this.clock();
    const k = Math.floor(this.rng() * this.questions.length) % this.questions.length;
    const q = this.questions[k];
    if (!s.af) s.af = { next: 0, misses: 0, pending: null };
    s.af.pending = { id: (s.af.seq = (s.af.seq | 0) + 1), k, until: now + this.afAnswer };
    s.send({ t: 'autofinder.ask', id: s.af.pending.id, question: q.q, answers: q.a, seconds: Math.round(this.afAnswer / 1000) });
    return true;
  }

  answer(session, m) {
    const af = session.af;
    if (!af || !af.pending || (m.id | 0) !== af.pending.id) return { ok: false, error: 'no question' };
    const q = this.questions[af.pending.k];
    const right = (m.choice | 0) === q.right;
    af.pending = null;
    af.next = this.clock() + this.afInterval + (this.rng() * 2 - 1) * this.afJitter;
    if (right) { af.misses = 0; return { ok: true, right: true, code: 10556 }; }
    this.miss(session, 'wrong', true);
    return { ok: true, right: false, code: 10557, misses: af.misses };
  }

  miss(s, why, answered = false) {
    const af = s.af;
    af.pending = null;
    af.misses = (af.misses | 0) + 1;
    af.next = this.clock() + this.afInterval + (this.rng() * 2 - 1) * this.afJitter;
    if (!answered) s.send({ t: 'autofinder.result', right: false, code: 10557, misses: af.misses });
    this.log(`autofinder: ${s.name} missed (${why}), ${af.misses} in a row`);
    if (af.misses >= AF_MAX_MISSES && typeof s.kick === 'function') { af.misses = 0; s.kick('macro check'); }
  }

  closeAll() { if (this.timer) clearInterval(this.timer); this.timer = null; }
}

module.exports = { SmallScreens, AEROSET_BUDGET, AEROSET_STAT_MAX, loadAutofinder, loadAeroCars };
