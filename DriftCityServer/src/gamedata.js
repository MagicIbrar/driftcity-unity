// The game's own tables (init\*.xlt from the extracted client data), loaded once at startup so the
// server decides prices, level gates, quest goals, recipes and licence conditions itself.
// The parsing rules mirror the Unity client exactly (DriftCityOffline\Assets\DriftCity\Core\XltTable.cs and
// Runtime\Game\GameTables.cs, ShopData.cs, PartsCatalog.cs, QuestTracker.cs, Workshop.cs).
'use strict';
const fs = require('node:fs');
const path = require('node:path');

// a release ships the tables beside the server (gamedata\), a checkout reads the client project's imported copy
const BUNDLED_DIR = path.join(__dirname, '..', 'gamedata');
const DEFAULT_DIR = require('fs').existsSync(BUNDLED_DIR) ? BUNDLED_DIR : path.join(__dirname, '..', '..', 'DriftCityOffline', 'GameData', 'Data');

// ---------------- .xlt reader (XltTable.cs) ----------------

function decode(buf) {
  if (buf.length >= 2 && buf[0] === 0xFF && buf[1] === 0xFE) return buf.subarray(2).toString('utf16le');
  if (buf.length >= 2 && buf[0] === 0xFE && buf[1] === 0xFF) {
    const b = Buffer.from(buf.subarray(2)); b.swap16(); return b.toString('utf16le');
  }
  return buf.toString('utf8');
}

/** Excel TSV split: quoted cells may hold tabs/newlines ("" escapes a quote); \r is dropped. */
function split(text) {
  const records = [];
  let cells = [], cell = '', quoted = false, atCellStart = true;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (quoted) {
      if (c === '"') {
        if (i + 1 < text.length && text[i + 1] === '"') { cell += '"'; i++; } else quoted = false;
      } else cell += c;
      continue;
    }
    if (c === '"' && atCellStart) { quoted = true; atCellStart = false; continue; }
    if (c === '\t') { cells.push(cell); cell = ''; atCellStart = true; continue; }
    if (c === '\r') continue;
    if (c === '\n') { cells.push(cell); cell = ''; atCellStart = true; records.push(cells); cells = []; continue; }
    cell += c; atCellStart = false;
  }
  if (cell.length > 0 || cells.length > 0) { cells.push(cell); records.push(cells); }
  return records;
}

/** C# int.TryParse on a trimmed string (Int32 range), else the fallback. */
function toInt(s, fallback = 0) {
  const t = String(s ?? '').trim();
  if (!/^[+-]?\d+$/.test(t)) return fallback;
  const v = Number(t);
  return v >= -2147483648 && v <= 2147483647 ? v : fallback;
}

class XltTable {
  constructor(buf, headerRow = 2) {
    const records = split(decode(buf));
    this.columns = [];
    this.rows = [];
    this.index = new Map();      // lower-case trimmed column name -> first position
    if (records.length > headerRow) {
      this.columns = records[headerRow];
      this.columns.forEach((name, i) => {
        const n = name.trim().toLowerCase();
        if (n.length && !this.index.has(n)) this.index.set(n, i);
      });
      for (let r = headerRow + 1; r < records.length; r++) {
        const rec = records[r];
        if (rec.length > 1 || (rec.length === 1 && rec[0].length > 0)) this.rows.push(rec);
      }
    }
  }
  col(name) { const i = this.index.get(name.toLowerCase()); return i === undefined ? -1 : i; }
  get(row, column, fallback = '') { const i = this.col(column); return i >= 0 && i < row.length ? row[i] : fallback; }
  int(row, column, fallback = 0) { return toInt(this.get(row, column), fallback); }
}

// ---------------- file existence (case-insensitive, like the Windows client) ----------------

function makeExists(root) {
  const dirs = new Map();   // lower-case relative dir -> Set of lower-case entries
  const list = rel => {
    if (dirs.has(rel)) return dirs.get(rel);
    let set = null;
    const parts = rel ? rel.split('/') : [];
    let actual = root;
    // resolve each component case-insensitively
    for (const part of parts) {
      let names;
      try { names = fs.readdirSync(actual); } catch { names = null; }
      const hit = names && names.find(n => n.toLowerCase() === part);
      if (!hit) { actual = null; break; }
      actual = path.join(actual, hit);
    }
    if (actual) { try { set = new Set(fs.readdirSync(actual).map(n => n.toLowerCase())); } catch { set = null; } }
    dirs.set(rel, set);
    return set;
  };
  return relative => {
    const norm = String(relative).replace(/\\/g, '/').toLowerCase();
    const i = norm.lastIndexOf('/');
    const set = list(i >= 0 ? norm.slice(0, i) : '');
    return !!set && set.has(norm.slice(i + 1));
  };
}

// ---------------- tables ----------------

const PART_SLOTS = ['aero', 'hood', 'wing', 'wheels', 'booster', 'neon', 'plate', 'duct', 'roof', 'tint', 'decal', 'horn', 'engine'];
const QUEST_GOALS = ['HuvCaught', 'RaceWon', 'DistanceKm', 'CarUpgraded', 'HuvCaught', 'CarBought'];

class GameData {
  constructor(dir) {
    this.dir = dir;
    const exists = makeExists(dir);
    const read = (rel, headerRow = 2) => new XltTable(fs.readFileSync(path.join(dir, ...rel.split('\\'))), headerRow);

    // init\vehiclelist.xlt (GameTables.Load): supported player cars that have a body model
    this.carList = [];
    this.cars = new Map();
    const vl = read('init\\vehiclelist.xlt', 3);
    for (const r of vl.rows) {
      if (vl.get(r, 'Desc') !== 'PlayerCar' || vl.get(r, 'Support') !== '1') continue;
      const file = vl.get(r, 'File Name');
      if (!file) continue;
      const modelBase = path.parse(file).name.toLowerCase();
      const v = {
        index: vl.int(r, 'Index'), uniqueId: vl.int(r, 'Unique Id'), reqLevel: vl.int(r, 'Req Level'), grade: vl.int(r, 'Lvl'),
        accel: vl.int(r, 'Accel'), speed: vl.int(r, 'Speed'), crash: vl.int(r, 'Crash'), boost: vl.int(r, 'Boost'),
        name: vl.get(r, 'UI Name'), modelBase,
        // Sellable 10001 / 20001 / 30001 / 40001: the apprentice course's reward cars (economy.js kind 'apprentice')
        sellable: vl.int(r, 'Sellable'),
      };
      const combo = modelBase.endsWith('c');
      const body = combo ? `vehicle\\player\\${modelBase}_b01.nif` : `vehicle\\player\\${modelBase}_01.nif`;
      if (!exists(body)) continue;
      this.carList.push(v);
      if (!this.cars.has(v.index)) this.cars.set(v.index, v);
    }
    this.starterCars = this.carList.filter(v => v.reqLevel <= 1).slice(0, 8);

    // init\aicarparam.xlt (GameTables.AiDrivers): the Battle Zone AI drivers - name, AI level (player scale) and the
    // Unique Ids of their cars, kept to the player cars we have (race.js addAi)
    this.aiDrivers = [];
    try {
      const ai = read('init\\aicarparam.xlt');
      for (const r of ai.rows) {
        const name = ai.get(r, 'AI Car Name').trim(), level = ai.int(r, 'AI Lv');
        if (!name || level <= 0) continue;
        const ids = ai.get(r, 'Car Model Unique').split(/[\s,]+/).map(s => toInt(s, -1)).filter(id => id > 0);
        const cars = this.carList.filter(c => ids.includes(c.uniqueId)).map(c => c.index);
        if (cars.length) this.aiDrivers.push({ name, level, cars });
      }
    } catch { /* table missing: race.js falls back to its own names */ }

    // init\vehicleupgrade.xlt (ShopData.LoadGrades): grade rows grouped by the car's Unique Id
    this.gradesById = new Map();
    const vu = read('init\\vehicleupgrade.xlt');
    for (const r of vu.rows) {
      const id = vu.int(r, 'ID', -1);
      if (id < 0) continue;
      const g = { price: vu.int(r, 'Price'), sell: vu.int(r, 'Sell'), upgradeMito: vu.int(r, 'UpgradeMito'), reqLevel: vu.int(r, 'Req Level') };
      if (!this.gradesById.has(id)) this.gradesById.set(id, []);
      this.gradesById.get(id).push(g);
    }

    // init\visualitem.xlt "Exist" columns and decal numbers (PartsCatalog.LoadFits)
    const fitsCars = new Map(), decalIndex = new Map();
    const vi = read('init\\visualitem.xlt');
    const carCols = [];
    for (let i = 7; i < vi.columns.length; i++) { const uid = toInt(vi.columns[i], NaN); if (Number.isInteger(uid)) carCols.push([i, uid]); }
    let category = '';
    for (const r of vi.rows) {
      if (r.length > 0 && r[0].trim().length > 0) category = r[0].trim();
      const id = r.length >= 8 ? toInt(r[4], NaN) : NaN;
      if (!Number.isInteger(id)) continue;
      const set = new Set();
      for (const [col, car] of carCols) if (col < r.length && r[col].trim() === '1') set.add(car);
      fitsCars.set(id, set);
      const di = toInt(r[2], NaN);
      if (category === 'Decal' && Number.isInteger(di)) decalIndex.set(id, di);
    }
    this.fitsCars = fitsCars;

    // init\vshopitem.xlt: custom-shop parts (PartsCatalog.All) and paints (ShopData.Paints)
    this.parts = new Map();
    this.paints = new Map();
    const vs = read('init\\vshopitem.xlt');
    for (const r of vs.rows) {
      if (vs.get(r, 'Support') !== '1') continue;
      const cat = vs.get(r, 'Category');
      if (cat === 'PAINT' || cat === 'PAINTHIGHGLOSS') {
        let price = vs.int(r, 'Mito Price');
        if (price <= 0) price = cat === 'PAINTHIGHGLOSS' ? 8000 : 3000;
        const pid = vs.int(r, 'UniqueId');
        if (!this.paints.has(pid)) this.paints.set(pid, price);
      }
      const item = vs.get(r, 'ItemName').trim(), sub = vs.get(r, 'Sub Catergory');
      const lower = item.toLowerCase();
      const p = { id: vs.int(r, 'UniqueId', -1), slot: null, price: 0, car: null, variant: 0 };
      if (p.id < 0 || this.parts.has(p.id)) continue;
      const price30 = vs.int(r, 'Mito Price30D'), price7 = vs.int(r, 'Mito Price7D');
      p.price = price30 > 0 ? price30 : price7 * 3;
      // the table's period prices [days, Mito] (7 / 30 / 90 / 365 days; src/vshop.js periodsFor, Shops/ShopPeriods.cs)
      p.periods = ['7D', '30D', '90D', '365D'].map(d => [vs.int(r, 'Period ' + d), vs.int(r, 'Mito Price' + d)]).filter(([d, m]) => d > 0 && m > 0);
      const dflt = n => { if (p.price <= 0) p.price = n; };
      let ok = false, m;
      switch (sub) {
        case 'AERO_SET': case 'AERO_COMBI_BUMPER':
          if (!(m = /^(pc_\d+[cs])_b?(\d\d)$/.exec(lower))) break;
          p.slot = 'aero'; p.car = m[1]; p.variant = parseInt(m[2], 10);
          ok = exists(`vehicle\\player\\${lower}.nif`); dflt(40000); break;
        case 'AERO_COMBI_INTERCOOLER':
          if (!(m = /^(pc_\d+c)_i\d\d$/.exec(lower))) break;
          p.slot = 'hood'; p.car = m[1]; ok = exists(`vehicle\\player\\${lower}.nif`); dflt(25000); break;
        case 'SPOILER_SUB':
          if (lower.startsWith('i_d_s_')) { p.slot = 'wing'; ok = exists(`vehicle\\spoiler\\${lower}.nif`); dflt(20000); break; }
          if (!(m = /^(pc_\d+[cs])_sp\d\d$/.exec(lower))) break;
          p.slot = 'wing'; p.car = m[1]; ok = exists(`vehicle\\player\\${lower}.nif`); dflt(15000); break;
        case 'TIRE_SUB': case 'GLOSSTIRE_SUB':
          p.slot = 'wheels';
          ok = (lower.startsWith('tire_ply') || lower.startsWith('gtire_ply')) && exists(`vehicle\\player\\wheel\\${lower}.nif`);
          dflt(sub === 'GLOSSTIRE_SUB' ? 45000 : 15000); break;
        case 'NUMBERPLATE_COLOR': case 'NUMBERPLATE_LINE': case 'NUMBERPLATE_GRAPHIC':
          p.slot = 'plate'; ok = exists(`vehicle\\nplate\\${lower}.nif`); dflt(3000); break;
        case 'Air Duct_SUB':
          p.slot = 'duct'; ok = exists(`vehicle\\airintake\\${lower}.nif`); dflt(60000); break;
        case 'ROOF_PSYCHY': case 'ROOF_BILLBOARD': case 'ROOF_ETC': case 'ROOF_EVENT':
          p.slot = 'roof'; ok = exists(`vehicle\\roof\\${lower}.nif`); dflt(80000); break;
        case 'WINDOWTINTING_SUB':
          p.slot = 'tint'; ok = true; dflt(10000); break;
        case 'SOUND_HORN':
          m = /^i_d_h_(\d+)$/.exec(lower);
          p.slot = 'horn'; ok = !!m && exists(`audio\\sfx\\SID_I_D_H_${m[1]}.wav`); dflt(5000); break;
        case 'SOUND_ENGINE':
          m = /^i_d_e_(\d+)$/.exec(lower);
          p.slot = 'engine'; ok = !!m; if (m) p.variant = parseInt(m[1], 10); dflt(25000); break;
        case 'BOOSTER_SUB':
          p.slot = 'booster'; ok = exists(`vehicle\\booster\\${lower}.nif`); dflt(45000); break;
        case 'NEON_COLOR':
          p.slot = 'neon'; ok = true; dflt(20000); break;
      }
      if (sub.startsWith('DECAL_') && decalIndex.has(p.id)) { p.slot = 'decal'; p.variant = decalIndex.get(p.id); dflt(20000); ok = true; }
      if (!ok) continue;
      this.parts.set(p.id, p);
    }

    // init\motherquest.xlt (QuestTracker.All)
    const mq = read('init\\motherquest.xlt');
    // the level-80 Mitro City storyline, played in story order with the level gate ramping 1..75 (QuestTracker.All)
    const quests = [];
    const keyToId = new Map(), prereqKey = new Map();
    const ordered = mq.rows.filter(r => mq.int(r, 'Index', -1) >= 0 && mq.get(r, 'Mother Quest Title'))
      .sort((a, b) => mq.int(a, 'Index') - mq.int(b, 'Index'));
    ordered.forEach((r, position) => {
      const id = mq.int(r, 'Index', -1);
      const tableLevel = r.length > 2 ? toInt(r[2], 1) : 1;
      const reqLevel = Math.max(1, Math.min(tableLevel, 1 + 2 * position));
      const goal = QUEST_GOALS[quests.length % QUEST_GOALS.length];
      const tier = 1 + Math.trunc(reqLevel / 15);
      const goalCount = goal === 'DistanceKm' ? 5 * tier : goal === 'CarBought' ? 1 : Math.min(1 + tier, 6);
      keyToId.set(mq.get(r, 'MotherQuest_ID').trim(), id);
      if (r.length > 4) prereqKey.set(id, String(r[4]).trim());
      quests.push({ id, reqLevel, goal, goalCount, requires: -1, rewardExp: 800 + 120 * reqLevel, rewardMito: 5000 + 900 * reqLevel });
    });
    for (const q of quests) { const before = keyToId.get(prereqKey.get(q.id)); if (before !== undefined && before !== q.id) q.requires = before; }
    this.quests = quests;
    this.questById = new Map();
    for (const q of this.quests) if (!this.questById.has(q.id)) this.questById.set(q.id, q);

    // init\alchemy_skillbook.xlt (Crafting.Recipes): by column position, the "Count" columns repeat
    this.recipes = new Map();
    const sb = read('init\\alchemy_skillbook.xlt');
    for (const r of sb.rows) {
      if (r.length < 28) continue;
      const rc = {
        id: r[1].trim(), reqLevel: toInt(r[2]), group: r[3].trim(), kit: r[12].trim(),
        result: r[14].trim(), resultCount: Math.max(1, toInt(r[15])), chance: toInt(r[25]), successExp: toInt(r[26]), failExp: toInt(r[27]),
        inputs: [],
      };
      if (!rc.kit.startsWith('user_create_kit') || rc.result.length === 0) continue;
      for (let i = 6; i <= 10; i += 2) if ((r[i] || '').trim().length > 0) rc.inputs.push([r[i].trim(), Math.max(1, toInt(r[i + 1]))]);
      if (!this.recipes.has(rc.id)) this.recipes.set(rc.id, rc);
    }

    // init\alchemy_levelmodify.xlt (Crafting.Level)
    const lm = read('init\\alchemy_levelmodify.xlt');
    this.craftLevelExp = lm.rows.map(r => lm.int(r, 'EXP_MAX')).filter(x => x > 0);

    // init\license.xlt (Licences.All)
    this.licences = new Map();
    const lt = read('init\\license.xlt');
    for (const r of lt.rows) {
      if (lt.get(r, 'Support') !== '1' || lt.get(r, 'Visible') === '0') continue;
      const l = { id: lt.int(r, 'LicenseID'), effect: lt.get(r, 'MountEffectID'), value: lt.int(r, 'MountEffectValue'), anyOne: false, needs: [] };
      l.anyOne = (lt.get(r, 'GainConditionText') + lt.get(r, 'Postfix')).includes('any one');
      const raw = [];
      for (let i = 1; i <= 12; i++) {
        const c = lt.get(r, 'Condition_' + i);
        if (!c || c === 'n/a') continue;
        const parts = c.split(':');
        const stat = mapCondition(parts[0]);
        if (!stat) continue;
        let v = toInt(parts.length > 1 ? parts[1] : '1', 0);
        if (parts[0] === 'GC_PartsEnhanceX') v = 10;
        if (parts[0] === 'GC_PartsEnhanceXII') v = 12;
        raw.push([stat, Math.max(1, v)]);
      }
      // the same counter listed per arena/track collapses to one requirement
      const grouped = new Map();
      for (const [stat, v] of raw) {
        if (!grouped.has(stat)) grouped.set(stat, v);
        else grouped.set(stat, l.anyOne ? Math.min(grouped.get(stat), v) : Math.max(grouped.get(stat), v));
      }
      l.needs = [...grouped].map(([stat, value]) => ({ stat, value }));
      if (!this.licences.has(l.id)) this.licences.set(l.id, l);
    }
  }

  car(index) { return this.cars.get(index) || null; }
  grades(car) { return (car && this.gradesById.get(car.uniqueId)) || []; }
  grade(car, i) { const g = this.grades(car); return g.length ? g[Math.max(0, Math.min(g.length - 1, i))] : null; }
  /** ShopData.CarPrice */
  carPrice(car) {
    const g = this.grade(car, 0);
    if (g && g.price > 0) return g.price;
    return 1000 + (car.accel + car.speed + car.crash + car.boost) * 250;
  }
  isStarter(index) { return this.starterCars.some(v => v.index === index); }
  part(id) { return this.parts.get(id) || null; }
  /** PartsCatalog.Fits without the decal-texture check (the server has no decal list). */
  fits(part, car) {
    if (!part || !car) return false;
    if (part.car) return part.car === car.modelBase;
    const cars = this.fitsCars.get(part.id);
    return !(cars && cars.size > 0 && !cars.has(car.uniqueId));
  }
  quest(id) { return this.questById.get(id) || null; }
  recipe(id) { return this.recipes.get(id) || null; }
  licence(id) { return this.licences.get(id) || null; }
  /** Crafting.Level */
  craftLevel(exp) {
    let lv = 1;
    for (const max of this.craftLevelExp) if (exp >= max) lv++;
    return Math.min(lv, this.craftLevelExp.length);
  }
}

function mapCondition(gc) {
  if (gc.startsWith('GC_LicenseGain_')) return 'Licence_' + gc.slice(15);
  if (gc.startsWith('GC_LadderPoint_')) return 'RacePoints';
  switch (gc) {
    case 'GC_ChasingClear': return 'HuvCaught';
    case 'GC_BattlePersonal': return 'RacesEntered';
    case 'GC_BattleTrackMoon': return 'RacesMoon';
    case 'GC_BattleTrackKoi': return 'RacesKoi';
    case 'GC_BattleTrackCras': return 'RacesCras';
    case 'GC_BattleTrackOros': return 'RacesOros';
    case 'GC_MQuestClear': return 'Quests';
    case 'GC_PartsEnhanceX': case 'GC_PartsEnhanceXII': return 'MaxEnhance';
    default: return null;
  }
}

const cache = new Map();

/**
 * Loads the tables from `dir` (env DC_GAMEDATA, else ..\DriftCityOffline\GameData\Data). Returns null and logs a
 * warning when the data is missing; the server then falls back to its built-in rules. Cached per directory.
 */
function loadGameData(dir = process.env.DC_GAMEDATA || DEFAULT_DIR, warn = console.warn) {
  dir = path.resolve(dir);
  if (cache.has(dir)) return cache.get(dir);
  let gd = null;
  // a fresh checkout or release: build the table cache from the player's own installed game (tools/extract-gamedata.js)
  if (!fs.existsSync(path.join(dir, 'init', 'vehiclelist.xlt')) && path.resolve(DEFAULT_DIR) === dir && !process.env.DC_GAMEDATA && !process.env.NODE_TEST_CONTEXT) {
    try {
      const { extract, findGameRoot } = require('../tools/extract-gamedata');
      const root = findGameRoot();
      if (root) { console.log(`building the server's game tables from ${root} ...`); extract(root, BUNDLED_DIR); dir = path.resolve(BUNDLED_DIR); }
    } catch (err) { warn(`could not read the game's archives: ${err.message}`); }
  }
  if (!fs.existsSync(path.join(dir, 'init', 'vehiclelist.xlt'))) {
    warn(`game data not found in ${dir}: prices and rules fall back to the built-in values (point the server at your Drift City folder with gameroot.txt or DRIFTCITY_ROOT)`);
  } else {
    try { gd = new GameData(dir); }
    catch (err) { warn(`game data in ${dir} could not be loaded (${err.message}): using the built-in values`); gd = null; }
  }
  cache.set(dir, gd);
  return gd;
}

module.exports = { loadGameData, GameData, XltTable, DEFAULT_DIR, PART_SLOTS, toInt };
