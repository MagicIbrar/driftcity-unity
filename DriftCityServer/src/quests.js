// Step quests of World/Data/QuestClient.tdf and the Delivery Service rankings (DriftCity_RE/MISSIONS.md sections 11-12).
// The Unity client runs the steps (StepQuestRunner.cs) and reports them; the server keeps the running quest, checks
// every report against the table (prerequisite, level range, the faction branch, step order and pace) and pays the
// reward (economy.js kind "stepquest"). The original server sent the offered quest list itself; these are the remake's
// rules, the same as StepQuests.cs. QSRANKLIST ("qs.rank") ranks each driver's best delivery time per job for this
// week / month / year (or the period before), recorded when the server pays a delivery.
// Also the unlock flags of column 5 (unlocksOf, client QuestUnlocks: MISSIONS.md section 14), the event quests (kind 99)
// by season (quests.events), the retry bonus count StepQuestTries and the crews in the QSRANKLIST rows.
'use strict';
const fs = require('node:fs');
const path = require('node:path');

const OMD = 0, ROO = 1;
// 0x6c57a0: column 5 -> unlock
const UNLOCKS = { CHASE: 1, ARBEIT: 2, CITY: 3, CITY_2ND: 4, CITY_3RD: 5, CITY_4TH: 6, CITY_5TH: 7, BRANTON: 8, OMD: 9, ROO: 10 };
const UNLOCK_OMD = 9, UNLOCK_ROO = 10;
const UNLOCK_CHASE = 1, UNLOCK_ARBEIT = 2, UNLOCK_CITY2 = 4, UNLOCK_CITY3 = 5, UNLOCK_CITY4 = 6, UNLOCK_CITY5 = 7;
// 0x703d20: the level opens the Delivery Service above 3, the chases above 5, Koinonia / Cras / Oros above 13 / 28 / 45
const UNLOCK_LEVELS = { arbeit: 4, chase: 6, city2: 14, city3: 29, city4: 46 };
// [rebuild] a driver this far up who finished no step quest at all (an old save) is past the rookie missions
const LEGACY_ROOKIE_LEVEL = 6;
const TAIPEI_GATE_LEVEL = 71, TAIPEI_MOTHER_QUEST = 'mq_5th_003';
const ROO_FINALE = 'q_00149', OMD_FINALE = 'q_00167';
// the event quests (kind 99) by group: the season they run in (StepQuests.EventSeasonOf)
const EVENT_SEASONS = { event_ini_1: 'summer', event_ini_2: 'harvest' };
// the server's per-quest bonus count n (times started over): time n*3 s, crashes n-1, trail distance n*5 m
const MAX_TRIES = 10;
// a step report needs this long since the previous one (a post is at least a short drive away); a quest needs this
// long per step from accept to reward
const MIN_STEP_SECONDS = 1;
const MIN_SECONDS_PER_STEP = 3;
// without the game tables: a flat reward
const FALLBACK_REWARD = lvl => ({ exp: 100 + 20 * lvl, mito: 500 + 50 * lvl });

const toInt = (s, f = 0) => { const v = parseInt(String(s ?? '').trim(), 10); return Number.isFinite(v) ? v : f; };
const cell = (r, c) => { const s = String(r[c] ?? '').trim(); return s === '0' ? '' : s; };
const isObj = x => !!x && typeof x === 'object' && !Array.isArray(x);

function readTdf(dir, name) {
  const { tdfRows } = require('./economy');
  if (!dir) return [];
  for (const rel of [['data', 'world', 'data', name], ['world', 'data', name], ['data', name]]) {
    const file = path.join(dir, ...rel);
    try { if (fs.existsSync(file)) return tdfRows(fs.readFileSync(file)); } catch { /* next copy */ }
  }
  return [];
}

/** One QuestClient.tdf row as StepQuests.Parse reads it (loader 0x7093c0). */
function parseQuest(r) {
  if (r.length < 80 || !r[0]) return null;
  const id = String(r[0]).trim();
  const q = {
    id, number: toInt(r[1]), group: String(r[2] || '').trim(), kind: toInt(r[3]), prereq: toInt(r[4]),
    unlock: UNLOCKS[String(r[5] || '').trim().toUpperCase()] || 0, minLevel: toInt(r[6]), maxLevel: toInt(r[7], 1000) || 1000,
    startPost: cell(r, 9), title: String(r[10] || '').trim(), endPost: cell(r, 12),
    exp: toInt(r[75]), mito: toInt(r[76]), items: [77, 78, 79].map(c => cell(r, c)).filter(Boolean), steps: 0,
  };
  if (q.number <= 0 || !q.startPost) return null;
  const lower = id.toLowerCase();
  q.omdOnly = lower.includes('omd');
  q.rooOnly = !q.omdOnly && lower.includes('roo');
  for (let i = 0; i < 5; i++) { if (!cell(r, 19 + 2 * i)) break; q.steps++; }
  return q;
}

const tables = new Map();
/** The step quest table of a game data folder (cached): { byNumber, list }. */
function stepTable(dir) {
  const key = dir || '';
  if (tables.has(key)) return tables.get(key);
  const byNumber = new Map();
  for (const r of readTdf(dir, 'questclient.tdf')) {
    const q = parseQuest(r);
    if (q && !byNumber.has(q.number)) byNumber.set(q.number, q);
  }
  const t = { byNumber, list: [...byNumber.values()], branch: new Map() };
  tables.set(key, t);
  return t;
}

/** -1 none, 0 OMD, 1 ROO: the first join quest on the prerequisite chain (StepQuests.Branch). */
function branchOf(t, q) {
  if (!q) return -1;
  if (t.branch.has(q.number)) return t.branch.get(q.number);
  let b = -1;
  const seen = new Set();
  for (let x = q; x && !seen.has(x.number); x = x.prereq > 0 ? t.byNumber.get(x.prereq) : null) {
    seen.add(x.number);
    if (x.unlock === UNLOCK_OMD) { b = OMD; break; }
    if (x.unlock === UNLOCK_ROO) { b = ROO; break; }
  }
  t.branch.set(q.number, b);
  return b;
}

function chosenBranch(t, p) {
  for (const n of p.StepQuestsDone) {
    const q = t.byNumber.get(n);
    if (q && q.unlock === UNLOCK_ROO) return ROO;
    if (q && q.unlock === UNLOCK_OMD) return OMD;
  }
  return -1;
}

/** Profile fields for the step quests and the faction (PlayerProfile.cs). */
function ensureQuestFields(p) {
  if (!Array.isArray(p.StepQuestsDone)) p.StepQuestsDone = [];
  if (!isObj(p.StepQuest)) p.StepQuest = { QuestId: 0, Step: 0, Counter: 0 };
  if (typeof p.StepQuestDay !== 'string') p.StepQuestDay = '';
  if (p.Faction !== ROO) p.Faction = OMD;
  if (!Number.isInteger(p.StepQuestTries) || p.StepQuestTries < 0) p.StepQuestTries = 0;
  return p;
}

/** A new UTC day takes the repeat quests (kind 1) off the done list. */
function resetRepeats(t, p, now) {
  const day = new Date(now).toISOString().slice(0, 10);
  if (p.StepQuestDay === day) return;
  p.StepQuestDay = day;
  p.StepQuestsDone = p.StepQuestsDone.filter(n => { const q = t.byNumber.get(n); return !(q && q.kind === 1); });
}

/** null when the driver may take the quest (StepQuests.Refusal), else why not; `seasons` = the running event seasons. */
function refusal(t, p, q, seasons = []) {
  if (!q) return 'unknown quest';
  if (p.StepQuestsDone.includes(q.number)) return 'quest already completed';
  if (q.kind === 99) {
    const season = EVENT_SEASONS[String(q.group || '').toLowerCase()];
    if (!season || !seasons.includes(season)) return 'that event is not running';
  }
  if (p.Level < q.minLevel || p.Level > q.maxLevel) return 'quest not for this level';
  if (q.prereq > 0 && t.byNumber.has(q.prereq) && !p.StepQuestsDone.includes(q.prereq)) return 'previous quest not completed';
  if (q.omdOnly && p.Faction !== OMD) return 'OMD drivers only';
  if (q.rooOnly && p.Faction !== ROO) return 'Team ROO drivers only';
  const chosen = chosenBranch(t, p), b = branchOf(t, q);
  if (chosen >= 0 && b >= 0 && b !== chosen) return 'that is the other side\'s story';
  return null;
}

// ---------------- the reward (economy.js kind "stepquest") ----------------

/** The running quest's reward and its effects; returns { exp, mito } or { error }. */
function claimReward(dir, p, data, now) {
  ensureQuestFields(p);
  const number = data.number | 0;
  const run = p.StepQuest;
  if (!number || run.QuestId !== number) return { error: 'quest not accepted' };
  if ((run.Counter | 0) <= 0) return { error: 'quest steps not done' };
  const t = stepTable(dir);
  const q = t.byNumber.get(number);
  if (t.list.length > 0 && !q) return { error: 'unknown quest' };
  const steps = q ? q.steps : 1;
  const started = Number(p.StepQuestStarted) || 0;
  if (started && (now - started) / 1000 < steps * MIN_SECONDS_PER_STEP) return { error: 'quest finished too fast' };
  const r = q ? { exp: q.exp, mito: q.mito } : FALLBACK_REWARD(p.Level);
  if (q) {
    for (const it of q.items) giveItem(p, it);
    if (q.unlock === UNLOCK_ROO) p.Faction = ROO;
    if (q.unlock === UNLOCK_OMD) p.Faction = OMD;
  }
  if (!p.StepQuestsDone.includes(number)) p.StepQuestsDone.push(number);
  p.StepQuest = { QuestId: 0, Step: 0, Counter: 0 };
  p.StepQuestTries = 0;
  delete p.StepQuestStarted; delete p.StepQuestAt;
  return r;
}

/**
 * The quest manager's unlock flags (0x70b660 +0x108..+0x110) as 0x703d20 recomputes them (StepQuests.cs QuestUnlocks):
 * rookie (the ARBEIT quest done: the stations' full panel), arbeit (the Delivery Service), chase (the HUV calls),
 * city2..city5 (Koinonia, Cras, Oros, Taipei), rooFinale / omdFinale. `gd` (optional) resolves mq_5th_003.
 */
function unlocksOf(t, p, gd = null) {
  ensureQuestFields(p);
  const lvl = p.Level | 0;
  const u = { rookie: false, arbeit: lvl >= UNLOCK_LEVELS.arbeit, chase: lvl >= UNLOCK_LEVELS.chase, city2: lvl >= UNLOCK_LEVELS.city2,
    city3: lvl >= UNLOCK_LEVELS.city3, city4: lvl >= UNLOCK_LEVELS.city4, city5: false, rooFinale: false, omdFinale: false };
  for (const n of p.StepQuestsDone) {
    const q = t.byNumber.get(n);
    if (!q) continue;
    if (q.unlock === UNLOCK_ARBEIT) u.rookie = u.arbeit = true;
    if (q.unlock === UNLOCK_CHASE) u.chase = true;
    if (q.unlock === UNLOCK_CITY2) u.city2 = true;
    if (q.unlock === UNLOCK_CITY3) u.city3 = true;
    if (q.unlock === UNLOCK_CITY4) u.city4 = true;
    if (q.unlock === UNLOCK_CITY5) u.city5 = true;
    if (q.id === ROO_FINALE) u.rooFinale = true;
    if (q.id === OMD_FINALE) u.omdFinale = true;
  }
  if (!u.rookie && lvl >= LEGACY_ROOKIE_LEVEL && p.StepQuestsDone.length === 0) u.rookie = true;
  if (!u.city5 && gd && Array.isArray(gd.quests) && Array.isArray(p.CompletedQuests)) {
    const mq = gd.quests.find(q => q.key === TAIPEI_MOTHER_QUEST);
    if (mq && p.CompletedQuests.includes(mq.id)) u.city5 = true;
  }
  return u;
}

/** Whether the driver may enter a city by its gate (QuestUnlocks.CityOpen); Moon Palace always. */
function cityOpen(u, city, level) {
  switch (String(city || '').toLowerCase()) {
    case 'koinonia': return u.city2;
    case 'cras': case 'craspeda': return u.city3;
    case 'oros': return u.city4;
    case 'taipei': return u.city5 && (level | 0) >= TAIPEI_GATE_LEVEL;
    default: return true;
  }
}

function giveItem(p, id, n = 1) {
  if (!Array.isArray(p.Items)) p.Items = [];
  let it = p.Items.find(i => isObj(i) && i.Id === id);
  if (!it) p.Items.push(it = { Id: id, Count: 0 });
  it.Count += n;
}

// ---------------- the rankings ----------------

/** UTC start of the week (Monday) / month / year that holds `ms`; `back` periods earlier. */
function periodStart(ms, period, back = 0) {
  const d = new Date(ms);
  if (period === 0) {
    const day = (d.getUTCDay() + 6) % 7;   // Monday 0
    return Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), d.getUTCDate() - day - 7 * back);
  }
  if (period === 1) return Date.UTC(d.getUTCFullYear(), d.getUTCMonth() - back, 1);
  return Date.UTC(d.getUTCFullYear() - back, 0, 1);
}

class Quests {
  /** opts: store (src/db.js), log, clock (ms), gamedata */
  constructor({ store, log = () => {}, clock = Date.now, gamedata = null } = {}) {
    this.store = store; this.log = log; this.clock = clock; this.gd = gamedata;
    this.dir = gamedata ? gamedata.dir : null;
    if (store && store.db) {
      store.db.exec(`CREATE TABLE IF NOT EXISTS qs_records (
        id INTEGER PRIMARY KEY AUTOINCREMENT, job TEXT NOT NULL, driver TEXT NOT NULL, seconds REAL NOT NULL, at INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS qs_records_job ON qs_records (job, at);`);
      this.qInsert = store.db.prepare('INSERT INTO qs_records (job, driver, seconds, at) VALUES (?, ?, ?, ?)');
      this.qBest = store.db.prepare('SELECT driver, MIN(seconds) AS seconds FROM qs_records WHERE job = ? AND at >= ? AND at < ? GROUP BY driver ORDER BY seconds ASC, driver ASC LIMIT ?');
      this.qMine = store.db.prepare('SELECT MIN(seconds) AS seconds FROM qs_records WHERE job = ? AND driver = ? AND at >= ? AND at < ?');
      this.qAhead = store.db.prepare('SELECT COUNT(*) AS n FROM (SELECT driver, MIN(seconds) AS s FROM qs_records WHERE job = ? AND at >= ? AND at < ? GROUP BY driver) WHERE s < ?');
    }
  }

  table() { return stepTable(this.dir); }

  /** The running event seasons (src/events.js, set by server.js as quests.events). */
  seasons() { try { return this.events ? this.events.seasons() : []; } catch { return []; } }

  /** The driver's unlock flags (see unlocksOf). */
  unlocks(p) { return unlocksOf(this.table(), p, this.gd); }

  /** The crew name of a driver (by driver name) for the QSRANKLIST crew column; '' without a crew. */
  crewOf(name) {
    if (!this.store || !this.store.db || !name) return '';
    if (this.qCrew === undefined) {
      try {
        this.qCrew = this.store.db.prepare('SELECT c.name AS crew FROM drivers d JOIN crew_members m ON m.account_id = d.account_id JOIN crews c ON c.id = m.crew_id WHERE d.name = ?');
      } catch { this.qCrew = null; }   // the crew tables come with src/teams.js
    }
    if (!this.qCrew) return '';
    try { const r = this.qCrew.get(name); return r && r.crew ? r.crew : ''; } catch { return ''; }
  }

  owns(t) { return t === 'quest.accept' || t === 'quest.step' || t === 'quest.abandon' || t === 'qs.rank'; }

  handle(session, m) {
    const p = session.profile;
    ensureQuestFields(p);
    const now = this.clock();
    const t = this.table();
    resetRepeats(t, p, now);
    switch (m.t) {
      case 'quest.accept': {
        const number = m.number | 0;
        if (p.StepQuest.QuestId) return { ok: false, error: 'Mission in progress.' };
        const q = t.byNumber.get(number);
        if (t.list.length > 0) { const why = refusal(t, p, q, this.seasons()); if (why) return { ok: false, error: why }; }
        else if (number <= 0) return { ok: false, error: 'unknown quest' };
        p.StepQuest = { QuestId: number, Step: 0, Counter: 0 };
        p.StepQuestTries = 0;
        p.StepQuestStarted = now; p.StepQuestAt = now;
        this.log(`${p.Name} took step quest ${number}${q ? ` ${q.title}` : ''}`);
        return { ok: true };
      }
      case 'quest.step': {
        const number = m.number | 0, step = m.step | 0;
        const run = p.StepQuest;
        if (!number || run.QuestId !== number) return { ok: false, error: 'quest not accepted' };
        const q = t.byNumber.get(number);
        const steps = q ? q.steps : 5;
        if (step === 0) {   // RETRY / a failed step: from the start, one more for the bonuses
          p.StepQuest = { QuestId: number, Step: 0, Counter: 0 };
          p.StepQuestTries = Math.min(MAX_TRIES, (p.StepQuestTries | 0) + 1);
          p.StepQuestStarted = now; p.StepQuestAt = now;
          return { ok: true };
        }
        if ((run.Counter | 0) > 0) return { ok: false, error: 'quest steps already done' };
        if (step !== (run.Step | 0) + 1 || step > steps) return { ok: false, error: 'wrong step' };
        if ((now - (Number(p.StepQuestAt) || 0)) / 1000 < MIN_STEP_SECONDS) return { ok: false, error: 'step too fast' };
        const done = step >= steps;
        p.StepQuest = { QuestId: number, Step: step, Counter: done ? 1 : 0 };
        p.StepQuestAt = now;
        return { ok: true, done };
      }
      case 'quest.abandon': {
        if (p.StepQuest.QuestId && (m.number | 0) === p.StepQuest.QuestId) {
          p.StepQuest = { QuestId: 0, Step: 0, Counter: 0 };
          p.StepQuestTries = 0;
          delete p.StepQuestStarted; delete p.StepQuestAt;
        }
        return { ok: true };
      }
      case 'qs.rank': return this.rank(p, String(m.job || ''), Math.max(0, Math.min(2, m.period | 0)), m.previous === true);
    }
    return { ok: false, error: 'unknown message' };
  }

  /** A paid delivery's time for the rankings (server.js after economy.claim "delivery"). */
  recordDelivery(session, data) {
    if (!this.qInsert || !session.profile || !data) return;
    const seconds = Number(data.seconds);
    if (!Number.isFinite(seconds) || seconds <= 0 || typeof data.job !== 'string' || !data.job) return;
    this.qInsert.run(data.job.slice(0, 40), session.profile.Name, seconds, this.clock());
  }

  /** QSRANKLIST rows: rank, driver, crew, best seconds; my rank, crew and best in the period. */
  rank(p, job, period, previous) {
    if (!this.qBest || !job) return { ok: true, rows: [], myRank: 0, myBest: 0, myTeam: this.crewOf(p.Name) };
    const now = this.clock();
    const from = periodStart(now, period, previous ? 1 : 0), to = previous ? periodStart(now, period, 0) : now + 1;
    const crews = new Map();
    const crew = n => { if (!crews.has(n)) crews.set(n, this.crewOf(n)); return crews.get(n); };
    const rows = this.qBest.all(job, from, to, 100).map((r, i) => ({ rank: i + 1, name: r.driver, team: crew(r.driver), seconds: r.seconds }));
    const mine = this.qMine.get(job, p.Name, from, to);
    const myBest = mine && mine.seconds ? mine.seconds : 0;
    const myRank = myBest ? this.qAhead.get(job, from, to, myBest).n + 1 : 0;
    return { ok: true, job, period, previous, rows, myRank, myBest, myTeam: crew(p.Name) };
  }
}

module.exports = { Quests, stepTable, parseQuest, refusal, branchOf, claimReward, ensureQuestFields, resetRepeats, periodStart, unlocksOf, cityOpen,
  OMD, ROO, UNLOCK_OMD, UNLOCK_ROO, EVENT_SEASONS, MAX_TRIES };
