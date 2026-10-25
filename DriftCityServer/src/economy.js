// Server-side progression: rewards are computed here (clients only report what happened), prices and level
// gates come from the game's own tables (src/gamedata.js), and reward claims are rate limited.
// Without the game data (DC_GAMEDATA missing) the old plausibility checks on client-quoted prices apply.
'use strict';
const fs = require('node:fs');
const path = require('node:path');
const { rewardExtras, ensureWorkshop } = require('./workshop');

const START_MITO = 50000;

function newProfile(name, vehicleIndex, color, avatar) {
  const created = new Date().toISOString().slice(0, 10).replace(/-/g, '.');
  return {
    Name: name, Avatar: Number.isInteger(avatar) && avatar >= 1 && avatar <= 8 ? avatar : 1, Created: created, Level: 1, Exp: 0, Mito: START_MITO,
    Cars: [newCar(vehicleIndex | 0, color)],
    CurrentCar: 0, City: 'cras', CompletedQuests: [], ActiveQuests: [], Inventory: [],
    HuvCaught: 0, BossesCaught: 0, RacesWon: 0, RacesEntered: 0, BestLapSeconds: 0, DistanceKm: 0,
    CarsBought: 0, CarUpgrades: 0, QuestBase: {},
    // a new driver takes the apprentice course (the client's Main.Apprentice bit) until the reward car is picked
    Apprentice: true, ApprenticeBest: 0,
  };
}

const expToNext = level => 1000 * level * level;

function addExp(p, amount) {
  p.Exp += amount;
  let levels = 0;
  while (p.Exp >= expToNext(p.Level)) { p.Exp -= expToNext(p.Level); p.Level++; levels++; }
  return levels;
}

// Same formulas the offline build uses, so online and offline progress feel the same.
const rewards = {
  chase: (lvl, boss) => ({ exp: (boss ? 1500 : 300) + 60 * lvl, mito: (boss ? 12000 : 2500) + 400 * lvl }),
  race: (lvl, place, field) => place === 1
    ? { exp: 400 + 50 * lvl + 100 * Math.max(0, field - 2), mito: 3000 + 300 * lvl + 1000 * Math.max(0, field - 2) }
    : place === 2 ? { exp: 200 + 25 * lvl, mito: 1200 } : { exp: 60, mito: 300 },
  quest: lvlReq => ({ exp: 800 + 120 * lvlReq, mito: 5000 + 900 * lvlReq }),
  // UndercityData.Reward: per HUB / red HUB (jump) / barrier HUB / Main HUB / HUV / boss, a clear bonus, minus the portal fee
  // Rush Time (RushTime.Reward): per driver who stayed, the lucky driver's prize (plus three Mittron Enhancers)
  rush: (lvl, hits, finalBlow) => ({ exp: 200 + 40 * lvl + 10 * Math.max(0, Math.min(40, hits)), mito: 2000 + 300 * lvl + (finalBlow ? 5000 : 0) }),
  // Virtual HUV (VirtualChase.Reward): a share per crash, half again for the winning team
  vhuv: (lvl, crashes, won) => {
    const exp = 300 + 50 * lvl + 30 * Math.max(0, Math.min(30, crashes)), mito = 2000 + 250 * lvl;
    return won ? { exp: Math.floor(exp * 3 / 2), mito: Math.floor(mito * 3 / 2) } : { exp, mito };
  },
  undercity: (lvl, s, cleared, fee = UC_ENTRY_FEE) => ({
    exp: 15 * s.hubs + 50 * s.jumps + 40 * s.barriers + 400 * s.mainHub + 120 * s.huvs + 1500 * s.boss + (cleared ? 1000 + 100 * lvl : 0),
    mito: 40 * s.hubs + 150 * s.jumps + 100 * s.barriers + 1500 * s.mainHub + 400 * s.huvs + 8000 * s.boss + (cleared ? 5000 + 300 * lvl : 0) - fee,
  }),
};
// the Undercity (UndercityData.cs): portal fee, the most a run can count, and the shortest believable cleared run
const UC_ENTRY_FEE = 2000;
// the portal fee by city number (client text 10772, net+0x3b4ec: 1 moonpalace .. 4 oros); UndercityData.FeeFor
const UC_FEES = { moonpalace: 600, koinonia: 1200, cras: 2100, oros: 4200 };
const ucFee = city => UC_FEES[city] || UC_ENTRY_FEE;
const UC_MAX = { hubs: 90, jumps: 10, barriers: 15, mainHub: 1, huvs: 40, boss: 1 };
const UC_MIN_CLEAR_SECONDS = 90;
// the Mita 500 drink (MitaDrink.cs, 0x4f83c0): 500 Mito a bottle, three a day; the effect 150/200/250 (bonus % + 100) for
// 1800 s on the EXP of these mission kinds (what the client's effect multiplied is not traced: the remake's rule)
const MITA = { price: 500, perDay: 3, seconds: 1800, values: [150, 200, 250], spots: 500, kinds: ['chase', 'delivery', 'instant', 'rush', 'vhuv'] };
const MITTRON_ENHANCER = 'i_n_00003';

// minimum seconds between claims of one kind (a chase lasts at least 8 s, see the 'chase too short' rule)
// combo: every 10th speed combo; the client's 3 s chaining window makes ten tricks take well over 10 s
// delivery / instant / patrol: the station missions (DeliveryService.cs, InstantMissions.cs, PatrolMission.cs)
// undercity: one run takes minutes (three phases with scripted briefings of over 40 s alone)
// rush / vhuv: Rush Time and the Virtual HUV (RushTime.cs, VirtualChase.cs; a Rush Time runs minutes, a V-HUV match 3 min)
// mita: a Mita 500 drink (MitaDrink.cs; at most three a day anyway)
// stepquest: a QuestClient.tdf step quest handed in (src/quests.js keeps its steps)
// apprentice: the apprentice course (ApprenticeCourse.cs): its start and its one reward car (the one-time flag bounds it)
const COOLDOWN = { chase: 8, race: 30, quest: 5, combo: 10, delivery: 30, instant: 60, patrol: 30, undercity: 120, rush: 120, vhuv: 90, mita: 5, stepquest: 5, apprentice: 0 };
// The apprentice course (DriftCity_RE/TUTORIAL.md): globalcontent ApprenticeLapResultGood / Normal / NotBad (Value_US), the
// reward cars by vehiclelist Sellable (10001 Advance .. 40001 Kicker; the fallback without game data), the shortest believable
// run of the ~4.6 km course and the time of a skipped course (the client's 0x57cdd0: grade 3, 1000 s).
const APPRENTICE = { grades: [130, 135, 140], fallbackCars: { 2: 10001, 3: 20001, 8: 30001, 0: 40001 }, minSeconds: 60, skipped: 1000 };
const apprenticeGrade = s => s >= APPRENTICE.skipped - 1 ? 3 : s < APPRENTICE.grades[0] ? 0 : s < APPRENTICE.grades[1] ? 1 : s < APPRENTICE.grades[2] ? 2 : 3;
// 0x747bb0: grade 0 takes any reward car, grade 1 not the 1xxxx car, grade 2 nor the 2xxxx, grade 3 only the 4xxxx car
const apprenticeAllowed = (grade, sellable) => sellable >= 10000 && sellable >= 10000 * (grade + 1);
// solo races vs AI: no field bonus (the offline build has none)
const SOLO_FIELD = 2;
// PvP arena EXP multipliers (2X Amateur .. 8X Superstar, Free, Real Match; race.js ARENA_EXP)
const ARENA_EXP = [2, 3, 4, 6, 8, 1, 1];

const PART_FIELD = {
  aero: 'Aero', hood: 'Hood', wing: 'Wing', wheels: 'Wheels', booster: 'Booster', neon: 'Neon',
  plate: 'Plate', duct: 'Duct', roof: 'Roof', tint: 'Tint', decal: 'Decal', horn: 'Horn', engine: 'Engine',
};
const PART_SLOTS = Object.keys(PART_FIELD);

// quest goal -> server counter that bounds the client's quest progress
const GOAL_STAT = { HuvCaught: 'HuvCaught', RaceWon: 'RacesWon', DistanceKm: 'DistanceKm', CarUpgraded: 'CarUpgrades', CarBought: 'CarsBought' };

function newCar(vehicleIndex, color, spent = 0) {
  return { VehicleIndex: vehicleIndex, Color: Math.max(0, Math.min(3, color | 0)), BodyVariant: 1, Grade: 0, PaintItem: -1,
           Parts: [], GiftParts: [], Aero: -1, Hood: -1, Wing: -1, Wheels: -1, Booster: -1, Neon: -1,
           Plate: -1, Duct: -1, Roof: -1, Tint: -1, Decal: -1, Horn: -1, Engine: -1, Spent: spent, PerfSlots: [-1, -1, -1, -1] };
}

/** Drivers saved before the custom shop existed get the new car fields. */
function ensureCarFields(car) {
  if (!Array.isArray(car.Parts)) car.Parts = [];
  if (!Array.isArray(car.GiftParts)) car.GiftParts = [];
  for (const f of Object.values(PART_FIELD)) if (!Number.isInteger(car[f])) car[f] = -1;
  if (typeof car.Spent !== 'number') car.Spent = 0;
  return car;
}

/** Profile fields added after release. */
function ensureProfile(p) {
  // expired period items turn into their expired form or go (src/items.js)
  try { require('./items').sweep(p); } catch (e) { /* items not ready: the next call sweeps */ }
  for (const k of ['CompletedQuests', 'ActiveQuests', 'Inventory', 'Cars']) if (!Array.isArray(p[k])) p[k] = [];
  for (const k of ['HuvCaught', 'BossesCaught', 'RacesWon', 'RacesEntered', 'BestLapSeconds', 'DistanceKm', 'CarsBought', 'CarUpgrades', 'Mito', 'Exp'])
    if (!Number.isFinite(p[k])) p[k] = 0;
  if (!p.QuestBase || typeof p.QuestBase !== 'object' || Array.isArray(p.QuestBase)) p.QuestBase = {};
  p.Cars.forEach(ensureCarFields);
  ensureWorkshop(p);
  require('./quests').ensureQuestFields(p);
  return p;
}

const isObj = x => !!x && typeof x === 'object' && !Array.isArray(x);
// workshop.js's give (bag stacks p.Items)
function giveItem(p, id, n = 1) {
  if (!Array.isArray(p.Items)) p.Items = [];
  let it = p.Items.find(i => isObj(i) && i.Id === id);
  if (!it) p.Items.push(it = { Id: id, Count: 0 });
  it.Count += n;
}
// 11059 (US): drinking too much
const GAME_TEXT_MITA_ENOUGH = 'Having too much Special Drink will cause serious health problem. Please wait until the effect wears off.';
const fail = error => ({ ok: false, error });

// ---------------- station missions: World/Data/ArbeitClient.tdf and Instant_Client.tdf ----------------

/** Rows of a client .tdf table (CityMapInfo.TdfRows): a BMP, then the header, cell offsets and UTF-16 cells. */
function tdfRows(buf) {
  const rows = [];
  if (!buf || buf.length < 32) return rows;
  const h = buf[0] === 0x42 && buf[1] === 0x4d ? buf.readInt32LE(2) : 0;
  if (h < 0 || h + 24 > buf.length) return rows;
  const end = buf.readInt32LE(h + 12), cols = buf.readInt32LE(h + 16), count = buf.readInt32LE(h + 20);
  if (cols <= 0 || count < 0 || h + 24 + cols * count * 4 > buf.length) return rows;
  const off = i => (i < cols * count ? buf.readInt32LE(h + 24 + i * 4) : end);
  for (let r = 0; r < count; r++) {
    const cells = [];
    for (let c = 0; c < cols; c++) {
      const a = h + off(r * cols + c), b = h + off(r * cols + c + 1);
      if (a < 0 || b > buf.length || b < a) { cells.push(''); continue; }
      const t = buf.toString('utf16le', a, a + ((b - a) & ~1));
      const nul = t.indexOf('\0');
      cells.push(nul >= 0 ? t.slice(0, nul) : t);
    }
    rows.push(cells);
  }
  return rows;
}

function readTdf(dir, name) {
  if (!dir) return [];
  for (const rel of [['data', 'world', 'data', name], ['world', 'data', name], ['data', name]]) {
    const file = path.join(dir, ...rel);
    try { if (fs.existsSync(file)) return tdfRows(fs.readFileSync(file)); } catch { /* next copy */ }
  }
  return [];
}

/** The table's reward formulas, e.g. INT(({Level}^2)/7+10) (InstantFormula.Eval); unknown terms count 0. */
function evalFormula(text, level) {
  text = String(text || '').trim();
  if (!text || text === 'n/a') return 0;
  if (/^-?\d+(\.\d+)?$/.test(text)) return Number(text);
  const s = text.replace(/\{Level\}/g, String(level)).replace(/\s+/g, '');
  if (s.includes('{')) return 0;   // e.g. the chase reward term, decided elsewhere
  let p = 0;
  const expr = () => { let v = term(); while (s[p] === '+' || s[p] === '-') { const o = s[p++]; const t = term(); v = o === '+' ? v + t : v - t; } return v; };
  const term = () => { let v = pow(); while (s[p] === '*' || s[p] === '/') { const o = s[p++]; const t = pow(); v = o === '*' ? v * t : (t === 0 ? 0 : v / t); } return v; };
  const pow = () => { let v = atom(); if (s[p] === '^') { p++; v = Math.pow(v, pow()); } return v; };
  const atom = () => {
    if (s.slice(p, p + 4).toUpperCase() === 'INT(') { p += 4; const v = expr(); p++; return Math.floor(v); }
    if (s[p] === '(') { p++; const v = expr(); p++; return v; }
    if (s[p] === '-') { p++; return -atom(); }
    const m = /^\d+(\.\d+)?/.exec(s.slice(p));
    if (!m) throw new Error('bad formula');
    p += m[0].length;
    return Number(m[0]);
  };
  try { const v = expr(); return p === s.length && Number.isFinite(v) ? Math.max(0, v) : 0; } catch { return 0; }
}

const OFFLINE_INSTANT_KINDS = ['knife', 'combo', 'Arrest_HUV', 'battle'];
// Team ROO patrols with move_ROO / party_move_ROO, OMD with move / party_move, both with move_event (0x6fb8d0)
const PATROL_KINDS = ['move', 'move_event', 'party_move', 'move_ROO', 'party_move_ROO'];
const toInt = (s, f = 0) => { const v = parseInt(String(s).trim(), 10); return Number.isFinite(v) ? v : f; };

/** Delivery jobs by id and instant/patrol rows by index, as the client's DeliveryService / InstantRow read them. */
function loadMissions(dir) {
  const jobs = new Map();
  for (const r of readTdf(dir, 'arbeitclient.tdf')) {
    if (r.length < 39 || !r[0]) continue;
    const id = r[0].trim(), title = (r[4] || '').trim();
    const maxLevel = toInt(r[1]);
    // DeliveryService.KindOf: event rows (e/g ids, "[Event]"), EX loops (col 1 = 0, "... Delivery EX", keyed "ex_" + id)
    const kind = /^[eg]/.test(id) || title.startsWith('[Event]') ? 'event' : maxLevel <= 0 && title.endsWith(' EX') ? 'ex' : maxLevel <= 0 ? 'event' : 'regular';
    const key = kind === 'ex' ? `ex_${id}` : id;
    const job = { id, key, kind, maxLevel, exp: [], mito: [], time: [], expNone: toInt(r[37]), mitoNone: toInt(r[38]) };
    for (let m = 0; m < 3; m++) { job.exp.push(toInt(r[28 + m * 3])); job.mito.push(toInt(r[29 + m * 3])); job.time.push(Number(r[30 + m * 3]) || 0); }
    if (job.time[2] <= 0) continue;
    if (!jobs.has(key)) jobs.set(key, job);
  }
  const instant = readTdf(dir, 'instant_client.tdf').map((r, index) => (r.length < 16 ? null : {
    index, type: r[0].trim(), minLevel: toInt(r[1]), maxLevel: toInt(r[2], 1000), kind: (r[9] || '').trim(), count: toInt(r[10]),
    expText: (r[12] || '').trim(), mitoText: (r[13] || '').trim(),
    postExp: toInt(r[12]), postMito: toInt(r[13]), completeExp: toInt(r[14]), maxPosts: toInt(r[15]),
  }));
  return { jobs, instant };
}

// without the game tables: the Easy delivery's medal rewards, small flat instant and patrol rewards
const FALLBACK_DELIVERY = [[600, 875], [400, 500], [200, 300]];

class Economy {
  /** @param gd game tables (src/gamedata.js) or null; @param clock ms clock (tests) */
  constructor(log = () => {}, gd = null, clock = Date.now) { this.log = log; this.gd = gd; this.clock = clock; }

  /** The delivery / instant / patrol tables (loaded on first use from the game data folder; empty without it). */
  missions() {
    if (!this.missionTables) this.missionTables = loadMissions(this.gd ? this.gd.dir : null);
    return this.missionTables;
  }

  claim(session, kind, data) {
    const p = session.profile;
    if (!isObj(data)) data = {};
    const now = this.clock() / 1000;
    session.lastClaim = session.lastClaim || {};
    if (typeof kind !== 'string' || !Object.hasOwn(COOLDOWN, kind)) return fail('unknown reward');
    if (now - (session.lastClaim[kind] || 0) < COOLDOWN[kind]) return fail('too soon');
    if (kind === 'apprentice') return this.apprentice(session, data, now);
    let r;
    if (kind === 'chase') {
      const duration = Number(data.seconds) || 0;
      if (duration < 8) return fail('chase too short');
      // a chase the server started (chase.begin, src/partyplay.js): the claim must match it and its time, and uses it up
      const sc = session.soloChase;
      if (sc) {
        if (!!data.boss !== sc.boss) return fail('not the chase that was started');
        if (this.clock() - sc.start < 8000) return fail('chase too short');
        session.soloChase = null;
      }
      r = rewards.chase(p.Level, !!data.boss);
      p.HuvCaught++; if (data.boss) p.BossesCaught++;
    } else if (kind === 'quest') {
      const id = data.questId | 0;
      if (p.CompletedQuests.includes(id)) return fail('quest already completed');
      if (this.gd) {
        const q = this.gd.quest(id);
        if (!q) return fail('unknown quest');
        const active = (p.ActiveQuests || []).find(a => isObj(a) && a.QuestId === id);
        if (!active) return fail('quest not accepted');
        // progress is what the server itself counted since the quest was accepted (the client's counter can lag a
        // message behind: it reports the catch before the chase reward that counts it)
        const prog = this.questProgress(p, q);
        if ((prog !== null ? prog : active.Counter | 0) < q.goalCount) return fail('quest objective not done');
        if (p.Level < q.reqLevel) return fail(`needs level ${q.reqLevel}`);
        r = { exp: q.rewardExp, mito: q.rewardMito };
      } else {
        r = rewards.quest(Math.max(1, Math.min(120, data.reqLevel | 0)));
      }
      p.CompletedQuests.push(id);
      p.ActiveQuests = (p.ActiveQuests || []).filter(q => isObj(q) && q.QuestId !== id);
      if (p.QuestBase) delete p.QuestBase[id];
    } else if (kind === 'stepquest') {
      // StepQuestRunner.Claim: the running QuestClient.tdf quest with every step reported (src/quests.js), columns 75 / 76,
      // the items of 77-79 and the unlock (a join quest sets the faction)
      const q = require('./quests').claimReward(this.gd ? this.gd.dir : null, p, data, this.clock());
      if (q.error) return fail(q.error);
      r = { exp: q.exp, mito: q.mito };
    } else if (kind === 'race') {
      // solo races vs AI; online races grant rewards from the race room instead
      const place = Math.max(1, Math.min(8, data.place | 0));
      r = rewards.race(p.Level, place, SOLO_FIELD);
      // the Battle Zone arena the AI race was set up in (PvpLobby tabs): same EXP factor as online rooms
      const arena = data.arena | 0;
      if (Number.isInteger(data.arena) && arena >= 0 && arena < ARENA_EXP.length) r = { exp: Math.round(r.exp * ARENA_EXP[arena] / 2), mito: r.mito };
      p.RacesEntered++; if (place === 1) p.RacesWon++;
      const lap = Number(data.bestLap);
      if (Number.isFinite(lap) && lap > 5 && lap < 3600 && (!p.BestLapSeconds || lap < p.BestLapSeconds)) p.BestLapSeconds = lap;
    } else if (kind === 'delivery') {
      // DeliveryService.Complete: the medal follows from the time and the job's own limits (ArbeitClient.tdf)
      const seconds = Number(data.seconds);
      if (!Number.isFinite(seconds) || seconds <= 0) return fail('bad time');
      const ms = this.missions();
      if (ms.jobs.size > 0) {
        const job = ms.jobs.get(String(data.job || ''));
        if (!job) return fail('unknown delivery');
        if (job.maxLevel > 0 && p.Level > job.maxLevel) return fail('level too high for this delivery');
        // the routes run several kilometres: a time far under the gold limit is no drive
        if (seconds < Math.max(10, job.time[0] * 0.4)) return fail('delivery too fast');
        const medal = seconds <= job.time[0] ? 1 : seconds <= job.time[1] ? 2 : seconds <= job.time[2] ? 3 : 0;
        // no medal: the fourth tier of cols 37/38 (the client's "Exp : %d Mito : %d" for medal 0), when the row has one
        if (!medal && !(job.expNone > 0 || job.mitoNone > 0)) return fail('no medal');
        r = medal ? { exp: job.exp[medal - 1], mito: job.mito[medal - 1] } : { exp: job.expNone, mito: job.mitoNone };
      } else {
        const medal = Math.max(1, Math.min(3, data.medal | 0));
        if (seconds < 30) return fail('delivery too fast');
        r = { exp: FALLBACK_DELIVERY[medal - 1][0], mito: FALLBACK_DELIVERY[medal - 1][1] };
      }
    } else if (kind === 'instant') {
      // InstantMissions.Complete: column 12 EXP and column 13 Mito of the row, formulas with the driver's level
      const ms = this.missions();
      if (ms.instant.length > 0) {
        const row = ms.instant[data.row | 0];
        if (!row || row.type !== 'instant' || !OFFLINE_INSTANT_KINDS.includes(row.kind)) return fail('unknown instant mission');
        if (p.Level < row.minLevel || p.Level > row.maxLevel) return fail('instant mission not for this level');
        r = { exp: Math.floor(evalFormula(row.expText, p.Level)), mito: Math.floor(evalFormula(row.mitoText, p.Level)) };
      } else {
        r = { exp: 30 + 10 * p.Level, mito: 30 + 5 * p.Level };
      }
    } else if (kind === 'patrol') {
      // PatrolMission.Finish: column 12 EXP per post, column 14 for the whole loop, column 13 Mito per post
      const ms = this.missions();
      const seconds = Number(data.seconds) || 0;
      let posts = Math.max(0, data.posts | 0);
      if (posts <= 0) return fail('no posts patrolled');
      // posts stand 100-600 m apart: three seconds each at the very least
      if (seconds < posts * 3) return fail('patrol too fast');
      if (ms.instant.length > 0) {
        const row = ms.instant[data.row | 0];
        if (!row || row.type !== 'patrol' || !PATROL_KINDS.includes(row.kind)) return fail('unknown patrol');
        if (row.kind !== 'move_event' && row.kind.endsWith('_ROO') !== (p.Faction === 1)) return fail('patrol not for your side');
        if (p.Level < row.minLevel || p.Level > row.maxLevel) return fail('patrol not for this level');
        posts = Math.min(posts, row.maxPosts);
        r = { exp: row.postExp * posts + (posts >= row.maxPosts ? row.completeExp : 0), mito: row.postMito * posts };
      } else {
        posts = Math.min(posts, 160);
        r = { exp: 5 * posts, mito: 0 };
      }
    } else if (kind === 'rush') {
      // RushTime.Win: the player's hits on the boss (capped), whether their hit stopped it
      const seconds = Number(data.seconds) || 0;
      if (seconds < 20) return fail('rush too short');
      const hits = Math.max(0, Math.min(40, data.hits | 0));
      const finalBlow = data.finalBlow === true;
      r = rewards.rush(p.Level, hits, finalBlow);
      p.HuvCaught++; p.BossesCaught++;
      if (finalBlow) { require('./workshop').ensureWorkshop(p); giveItem(p, MITTRON_ENHANCER, 3); }
    } else if (kind === 'vhuv') {
      // VirtualChase.Finish: the player's crashes into the other team's virtual HUV, the match result
      const seconds = Number(data.seconds) || 0;
      if (seconds < 60) return fail('match too short');
      const size = data.size | 0;
      if (size !== 4 && size !== 8) return fail('bad team size');
      r = rewards.vhuv(p.Level, Math.max(0, Math.min(30, data.crashes | 0)), data.won === true);
    } else if (kind === 'mita') {
      // MitaDrink.Buy: a bottle at one of the city's 500 qpos spots; at most three a day (UTC), 500 Mito
      const index = data.index | 0;
      if (!Number.isInteger(data.index) || index < 0 || index >= MITA.spots) return fail('no salesman there');
      const day = new Date(this.clock()).toISOString().slice(0, 10);
      if (p.MitaDay !== day) { p.MitaDay = day; p.MitaCount = 0; }
      if ((p.MitaCount | 0) >= MITA.perDay) return fail(GAME_TEXT_MITA_ENOUGH);
      if (p.Mito < MITA.price) return fail('not enough Mito');
      p.MitaCount = (p.MitaCount | 0) + 1;
      // the effect (none above level 70: Mita500_4 "it'll never work on you")
      if (p.Level <= 70) session.mita = { value: MITA.values[Math.min(MITA.values.length, p.MitaCount) - 1], until: now + MITA.seconds };
      session.lastClaim[kind] = now;
      p.Mito -= MITA.price;
      this.log(`${p.Name} bought a Mita 500 (${p.MitaCount} today)`);
      return { ok: true, exp: 0, mito: -MITA.price, levelUps: 0, value: session.mita && session.mita.until > now ? session.mita.value : 100 };
    } else if (kind === 'combo') {
      // the client reports every 10th speed combo (0x4804d0) and the server rolls an item; no exp or mito
      const count = data.count | 0;
      if (count < 10 || count % 10 !== 0) return fail('not a combo milestone');
      r = { exp: 0, mito: 0 };
    } else if (kind === 'undercity') {
      // UndercityStage.ResultAndLeave: the run's counts; the server bounds them and pays by UndercityData.Reward
      const s = {};
      for (const k of Object.keys(UC_MAX)) {
        const n = Number(data[k]);
        if (!Number.isInteger(n) || n < 0) return fail(`bad ${k}`);
        if (n > UC_MAX[k]) return fail(`too many ${k}`);
        s[k] = n;
      }
      const cleared = data.cleared === true;
      const seconds = Number(data.seconds) || 0;
      if (cleared && (s.mainHub !== 1 || s.boss !== 1)) return fail('not cleared');
      if (cleared && seconds < UC_MIN_CLEAR_SECONDS) return fail('undercity too fast');
      const fee = ucFee(session.channel ? session.channel.city : String(data.city || ''));
      if (p.Mito < fee && !cleared) return fail('not enough Mito for the portal');
      r = rewards.undercity(p.Level, s, cleared, fee);
      if (p.Mito + r.mito < 0) r.mito = -p.Mito;
      p.HuvCaught += s.huvs + s.boss; p.BossesCaught += s.boss;
    }
    session.lastClaim[kind] = now;
    if (session.mita && session.mita.until > now && MITA.kinds.includes(kind)) r = { exp: Math.floor(r.exp * session.mita.value / 100), mito: r.mito };
    const extra = rewardExtras(p, kind, r, data, Math.random, this.gd);
    r = { exp: r.exp + extra.exp, mito: r.mito + extra.mito };
    p.Mito += r.mito;
    const levels = addExp(p, r.exp);
    this.log(`${p.Name} reward ${kind}: +${r.exp} exp +${r.mito} mito${levels ? ` (level ${p.Level})` : ''}${extra.drop ? ` drop ${extra.drop}` : ''}`);
    return { ok: true, exp: r.exp, mito: r.mito, levelUps: levels, drop: extra.drop };
  }

  /**
   * The apprentice course (ApprenticeCourse.cs): {start: true} notes the start of a run; {seconds, vehicleIndex, color} claims
   * the reward car once per driver (the profile's Apprentice flag): the grade follows from the time (a run can't be shorter
   * than the server saw it last, nor than 60 s; 1000 s = skipped, grade 3), the car must be a reward car the grade reaches.
   */
  apprentice(session, data, now) {
    const p = session.profile;
    if (data.start === true) { session.apprenticeStart = now; return { ok: true, exp: 0, mito: 0, levelUps: 0 }; }
    if (p.Apprentice !== true) return fail('You have already received the tutorial reward.');
    const seconds = Number(data.seconds);
    if (!Number.isFinite(seconds) || seconds <= 0) return fail('bad time');
    const skipped = seconds >= APPRENTICE.skipped - 1;
    if (!skipped) {
      if (seconds < APPRENTICE.minSeconds) return fail('course too fast');
      if (!session.apprenticeStart) return fail('course not started');
      if (now - session.apprenticeStart + 2 < seconds) return fail('course too fast');
    }
    const vehicleIndex = data.vehicleIndex;
    if (!Number.isInteger(vehicleIndex)) return fail('unknown car');
    let sellable = 0;
    if (this.gd) { const car = this.gd.car(vehicleIndex); sellable = car ? car.sellable | 0 : 0; } else sellable = APPRENTICE.fallbackCars[vehicleIndex] || 0;
    if (sellable < 10000) return fail('That car is not a tutorial reward.');
    const grade = apprenticeGrade(seconds);
    if (!apprenticeAllowed(grade, sellable)) return fail('Please try again.');
    if (p.Cars.length >= 40) return fail('garage full');
    p.Cars.push(newCar(vehicleIndex, data.color));
    p.CurrentCar = p.Cars.length - 1;
    p.Apprentice = false;
    if (!skipped && (!(p.ApprenticeBest > 0) || seconds < p.ApprenticeBest)) p.ApprenticeBest = Math.round(seconds * 1000) / 1000;
    session.apprenticeStart = 0;
    session.lastClaim.apprentice = now;
    this.log(`${p.Name} apprentice reward: car ${vehicleIndex} (grade ${grade}, ${skipped ? 'skipped' : seconds.toFixed(1) + ' s'})`);
    return { ok: true, exp: 0, mito: 0, levelUps: 0, vehicleIndex, grade };
  }

  /** A party chase member's pay (src/partyplay.js): the chase formula for their level, the drink, the extras (party bonus). */
  grantChase(session, boss) {
    const p = session.profile;
    ensureProfile(p);
    const now = this.clock() / 1000;
    let base = rewards.chase(p.Level, !!boss);
    if (session.mita && session.mita.until > now) base = { exp: Math.floor(base.exp * session.mita.value / 100), mito: base.mito };
    const extra = rewardExtras(p, 'chase', base, { boss: !!boss }, Math.random, this.gd);
    const r = { exp: base.exp + extra.exp, mito: base.mito + extra.mito };
    p.Mito += r.mito;
    p.HuvCaught++; if (boss) p.BossesCaught++;
    session.lastClaim = session.lastClaim || {};
    session.lastClaim.chase = now;
    const levels = addExp(p, r.exp);
    return { exp: r.exp, mito: r.mito, levelUps: levels, drop: extra.drop };
  }

  /** Online race reward (from the race room). bestLap: the server's approximation (finish time / laps). */
  /** expFactor: the PvP arena's EXP multiplier over the Amateur arena (race.js arenaExpFactor; 1 = none). */
  grantRace(profile, place, field, city, bestLap = 0, expFactor = 1) {
    ensureProfile(profile);
    const base = rewards.race(profile.Level, place, field);
    if (Number.isFinite(expFactor) && expFactor > 0 && expFactor <= 4) base.exp = Math.round(base.exp * expFactor);
    const extra = rewardExtras(profile, 'race', base, { place, city }, Math.random, this.gd);
    const r = { exp: base.exp + extra.exp, mito: base.mito + extra.mito };
    profile.Mito += r.mito;
    profile.RacesEntered++;
    if (place === 1) profile.RacesWon++;
    if (Number.isFinite(bestLap) && bestLap > 5 && (!profile.BestLapSeconds || bestLap < profile.BestLapSeconds)) profile.BestLapSeconds = Math.round(bestLap * 1000) / 1000;
    const levels = addExp(profile, r.exp);
    return { exp: r.exp, mito: r.mito, levelUps: levels };
  }

  buyCar(p, m) {
    const vehicleIndex = m.vehicleIndex | 0;
    let price, reqLevel;
    if (this.gd) {
      const car = Number.isInteger(m.vehicleIndex) ? this.gd.car(vehicleIndex) : null;
      if (!car) return fail('unknown car');
      price = this.gd.carPrice(car); reqLevel = car.reqLevel;
    } else {
      // no game data: the client's quoted price within plausible bounds
      price = m.price | 0; reqLevel = m.reqLevel | 0;
      if (price < 500 || price > 50_000_000) return fail('bad price');
    }
    if (p.Level < reqLevel) return fail(`needs level ${reqLevel}`);
    if (p.Mito < price) return fail('not enough Mito');
    if (p.Cars.some(c => c.VehicleIndex === vehicleIndex && !(c.RentKind > 0))) return fail('already owned');   // a rental doesn't count
    if (p.Cars.length >= 40) return fail('garage full');
    p.Mito -= price;
    p.Cars.push(newCar(vehicleIndex, m.color, price));
    p.CurrentCar = p.Cars.length - 1;
    p.CarsBought = (p.CarsBought || 0) + 1;
    return { ok: true, price };
  }

  upgrade(p, m) {
    const car = p.Cars[m.slot | 0];
    if (!car) return fail('no such car');
    { const no = require('./rentals').refusal(car, 'upgrade'); if (no) return no; }   // rental cars (10952)
    const grade = m.grade | 0;
    if (grade !== car.Grade + 1) return fail('upgrade one grade at a time');
    let cost, reqLevel;
    if (this.gd) {
      const grades = this.gd.grades(this.gd.car(car.VehicleIndex));
      if (!grades.length) return fail('this car has no upgrades');
      if (grade >= grades.length) return fail('this car is already at its top grade');
      cost = Math.max(grades[grade - 1].upgradeMito, 500);
      reqLevel = grades[grade].reqLevel;
    } else {
      cost = m.cost | 0; reqLevel = m.reqLevel | 0;
      if (cost < 0 || cost > 20_000_000) return fail('bad cost');
    }
    if (p.Level < reqLevel) return fail(`needs level ${reqLevel}`);
    if (p.Mito < cost) return fail('not enough Mito');
    p.Mito -= cost;
    car.Grade = grade;
    car.Spent = (car.Spent || 0) + cost;
    p.CarUpgrades = (p.CarUpgrades || 0) + 1;
    return { ok: true, cost };
  }

  paint(p, m) {
    const car = p.Cars[m.slot | 0];
    const itemId = m.itemId | 0;
    if (!car) return fail('no such car');
    let price;
    if (this.gd) {
      if (!this.gd.paints.has(itemId)) return fail('unknown paint');
      price = this.gd.paints.get(itemId);
    } else {
      price = m.price | 0;
      if (price < 0 || price > 5_000_000) return fail('bad price');
    }
    if (p.Mito < price) return fail('not enough Mito');
    p.Mito -= price;
    car.PaintItem = itemId;
    const it = p.Inventory.find(i => isObj(i) && i.ItemId === itemId);
    if (it) it.Count++; else p.Inventory.push({ ItemId: itemId, Count: 1 });
    return { ok: true, price };
  }

  /** Custom-shop part: bought once per car, fitted straight away. Price and slot come from the parts table. */
  buyPart(p, m) {
    const car = p.Cars[m.slot | 0];
    const item = m.item | 0;
    if (!car) return fail('no such car');
    ensureCarFields(car);
    let price, slot;
    if (this.gd) {
      const part = this.gd.part(item);
      if (!part) return fail('unknown part');
      const info = this.gd.car(car.VehicleIndex);
      if (info && !this.gd.fits(part, info)) return fail("that part doesn't fit this car");
      price = part.price; slot = part.slot;
      // a part with period prices bought for keeps costs the unlimited price (src/vshop.js: 3 x its 30-day price)
      const p30 = (part.periods || []).find(([d]) => d === 30);
      if (p30) price = p30[1] * 3;
    } else {
      price = m.price | 0; slot = m.part;
      if (!PART_SLOTS.includes(slot)) return fail('bad part slot');
      if (item < 1 || price < 1000 || price > 2_000_000) return fail('bad part');
    }
    if (car.Parts.includes(item)) return fail('already owned');
    if (car.Parts.length >= 120) return fail('too many parts on this car');
    if (p.Mito < price) return fail('not enough Mito');
    p.Mito -= price;
    car.Parts.push(item);
    car.Spent += price;
    car[PART_FIELD[slot]] = item;
    return { ok: true, price };
  }

  /** Fits an owned part, or the stock part (-1). */
  fitPart(p, m) {
    const car = p.Cars[m.slot | 0];
    const item = m.item | 0, slot = m.part;
    if (!car) return fail('no such car');
    if (!PART_SLOTS.includes(slot)) return fail('bad part slot');
    ensureCarFields(car);
    if (item !== -1 && !car.Parts.includes(item)) return fail("you don't own that part");
    if (item !== -1 && this.gd) { const part = this.gd.part(item); if (part && part.slot !== slot) return fail("that part doesn't go there"); }
    car[PART_FIELD[slot]] = item;
    return { ok: true };
  }

  /** ShopData.SellPrice: the grade's resale value (or car price / 20) plus 30% of the parts bought for the car
   *  (parts granted by items, car.GiftParts, don't count). */
  sellPrice(car) {
    const info = this.gd.car(car.VehicleIndex);
    if (!info) return 0;
    const g = this.gd.grade(info, car.Grade | 0);
    const baseValue = g && g.sell > 0 ? g.sell : Math.trunc(this.gd.carPrice(info) / 20);
    const gifts = new Set(Array.isArray(car.GiftParts) ? car.GiftParts : []);
    let parts = 0;
    for (const id of car.Parts || []) {
      if (gifts.has(id)) continue;
      const part = this.gd.part(id);
      if (part) parts += part.price;
    }
    return Math.min(2147483647, baseValue + Math.trunc(parts * 3 / 10));
  }

  /** Reseller: the server computes the price (the client's quote is ignored when the game data is loaded). */
  sellCar(p, m) {
    const slot = m.slot | 0;
    const car = p.Cars[slot];
    if (!car) return fail('no such car');
    if (p.Cars.length <= 1) return fail("you can't sell your last car");
    { const no = require('./rentals').refusal(car, 'sell'); if (no) return no; }   // rental cars (10950)
    ensureCarFields(car);
    let price;
    if (this.gd) price = this.sellPrice(car);
    else {
      price = m.price | 0;
      const cap = Math.max(2000, Math.floor(car.Spent * 0.6));
      if (price < 0 || price > cap) return fail('resale price not accepted');
    }
    p.Cars.splice(slot, 1);
    if (p.CurrentCar === slot) p.CurrentCar = 0; else if (p.CurrentCar > slot) p.CurrentCar--;
    p.Mito += price;
    return { ok: true, mito: price };
  }

  /** A quest's progress by the server's own counters since it was accepted (null: no baseline yet). */
  questProgress(p, q) {
    const stat = this.goalStat(p, q.goal);
    const base = isObj(p.QuestBase) ? p.QuestBase[q.id] : undefined;
    return stat !== null && Number.isFinite(base) ? Math.min(q.goalCount, Math.max(0, Math.floor(stat - base))) : null;
  }

  /** Server counter that bounds the progress of a quest goal (null: no bound). */
  goalStat(p, goal) { const k = GOAL_STAT[goal]; return k ? (Number(p[k]) || 0) : null; }

  /** Quest list from the client: acceptance rule, at most three, progress capped by the goal and by how much the
   *  server's own counter grew since the quest was accepted (p.QuestBase, server-side only). */
  applyQuests(p, list) {
    const out = [];
    if (!isObj(p.QuestBase)) p.QuestBase = {};
    const base = p.QuestBase;
    const prev = new Map((p.ActiveQuests || []).filter(isObj).map(a => [a.QuestId | 0, a]));
    for (const q of list) {
      if (out.length >= 3) break;
      if (!isObj(q)) continue;
      const id = q.QuestId | 0;
      if (p.CompletedQuests.includes(id) || out.some(o => o.QuestId === id)) continue;
      let counter = Math.max(0, Math.min(1000, q.Counter | 0));
      if (this.gd) {
        const info = this.gd.quest(id);
        if (!info) continue;
        const old = prev.get(id);
        // the client's acceptance rule (QuestTracker.CanAccept): level reached and the previous story quest done
        if (!old && !(p.Level >= info.reqLevel && (info.requires < 0 || p.CompletedQuests.includes(info.requires)))) continue;
        const stat = this.goalStat(p, info.goal);
        if (stat !== null) {
          if (!Number.isFinite(base[id])) base[id] = old ? stat - Math.max(0, old.Counter | 0) : stat;
          counter = Math.min(counter, Math.max(0, Math.floor(stat - base[id])));
        }
        counter = Math.min(counter, info.goalCount);
      }
      out.push({ QuestId: id, Step: q.Step | 0, Counter: counter });
    }
    for (const k of Object.keys(base)) if (!out.some(o => String(o.QuestId) === k)) delete base[k];
    p.ActiveQuests = out;
  }

  // Fields a client may change freely (no economic value).
  applyUpdate(p, u) {
    if (!isObj(u)) return;
    if (Number.isInteger(u.CurrentCar) && u.CurrentCar >= 0 && u.CurrentCar < p.Cars.length) p.CurrentCar = u.CurrentCar;
    if (typeof u.City === 'string' && /^[a-z]{3,16}$/.test(u.City)) p.City = u.City;
    // distance first: distance quests are bounded by it
    if (typeof u.DistanceKm === 'number' && Number.isFinite(u.DistanceKm) && u.DistanceKm >= p.DistanceKm && u.DistanceKm < p.DistanceKm + 500) p.DistanceKm = u.DistanceKm;
    if (Array.isArray(u.ActiveQuests)) this.applyQuests(p, u.ActiveQuests);
    else if (Array.isArray(p.ActiveQuests)) p.ActiveQuests = p.ActiveQuests.filter(a => isObj(a) && !p.CompletedQuests.includes(a.QuestId));
    if (Number.isInteger(u.PaintSlotApply) && p.Cars[u.PaintSlotApply] && Number.isInteger(u.PaintItem) &&
        p.Inventory.some(i => isObj(i) && i.ItemId === u.PaintItem)) p.Cars[u.PaintSlotApply].PaintItem = u.PaintItem;
  }
}

module.exports = { APPRENTICE, apprenticeGrade, apprenticeAllowed, ucFee, Economy, newProfile, newCar, ensureCarFields, ensureProfile, expToNext, addExp, rewards, PART_FIELD, COOLDOWN, tdfRows, evalFormula, loadMissions, MITA };
