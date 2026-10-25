'use strict';
// Calendar EXP / Mito multipliers (DriftCity_RE/PROGRESS.md; client mirror Runtime/Game/Multipliers.cs).
//
// The client only shows what the official server decided: result-row bonus flags (8 golden time, 0x200 / 0x400 Santa
// EXP 25 / 50 %, 0x800 3rd anniversary; PVP.md 14.8) and the HUD badges (3030000). The remake's rules:
//  - golden time: every day between the golden hours (server clock, env DC_GOLDEN "20-22", "off" disables): race,
//    chase and Undercity rewards +50 % EXP and Mito, flag 8;
//  - Santa: in the Christmas season race and chase EXP +25 % (flag 0x200), +50 % on Dec 24-25 UTC (flag 0x400);
//  - 3rd anniversary: in the anniversary season every reward but the combo drop gets EXP and Mito x3 (+200 %), flag 0x800.
// workshop.rewardExtras adds them through setMultiplierBonus (server.js installs the provider with the event seasons).

const RULES = { golden: 50, santa: 25, santaDay: 50, anniversary: 200 };
const FLAGS = { golden: 8, santa25: 0x200, santa50: 0x400, anniversary: 0x800 };
const DEFAULT_HOURS = '20-22';

/** "from-to" (24 h, to exclusive) -> { from, to }, or null when switched off. */
function parseHours(text) {
  const t = String(text == null || text === '' ? DEFAULT_HOURS : text).trim().toLowerCase();
  if (t === 'off' || t === 'none' || t === '0') return null;
  const m = /^(\d{1,2})-(\d{1,2})$/.exec(t);
  if (m) {
    const a = +m[1], b = +m[2];
    if (a <= 24 && b <= 24 && a !== b) return { from: a % 24, to: b % 24 };
  }
  return parseHours(DEFAULT_HOURS);
}

function hoursText(h) { return h ? `${h.from}-${h.to}` : 'off'; }

function inHours(hour, h) {
  if (!h) return false;
  return h.from < h.to ? hour >= h.from && hour < h.to : hour >= h.from || hour < h.to;
}

const goldenKind = k => k === 'race' || k === 'chase' || k === 'undercity';
const santaKind = k => k === 'race' || k === 'chase';
const anniversaryKind = k => !!k && k !== 'combo';

function santaPct(seasons, date) {
  if (!seasons.includes('christmas')) return 0;
  return date.getUTCMonth() === 11 && (date.getUTCDate() === 24 || date.getUTCDate() === 25) ? RULES.santaDay : RULES.santa;
}

/**
 * The calendar's percents for a reward kind.
 * @param kind reward kind; @param seasons active event seasons; @param date Date; @param hours parseHours() result
 * @returns {{exp:number, mito:number, flags:number}}
 */
function bonus(kind, seasons = [], date = new Date(), hours = parseHours(process.env.DC_GOLDEN)) {
  const r = { exp: 0, mito: 0, flags: 0 };
  if (goldenKind(kind) && inHours(date.getHours(), hours)) { r.exp += RULES.golden; r.mito += RULES.golden; r.flags |= FLAGS.golden; }
  const santa = santaKind(kind) ? santaPct(seasons, date) : 0;
  if (santa) { r.exp += santa; r.flags |= santa >= RULES.santaDay ? FLAGS.santa50 : FLAGS.santa25; }
  if (anniversaryKind(kind) && seasons.includes('anniversary')) { r.exp += RULES.anniversary; r.mito += RULES.anniversary; r.flags |= FLAGS.anniversary; }
  return r;
}

/** event.state's "multipliers": what the client shows (golden hours and state, Santa %, anniversary, race flags). */
function state(seasons = [], date = new Date(), hours = parseHours(process.env.DC_GOLDEN)) {
  return {
    golden: inHours(date.getHours(), hours), goldenHours: hoursText(hours), goldenPct: RULES.golden,
    santa: santaPct(seasons, date), anniversary: seasons.includes('anniversary') ? RULES.anniversary : 0,
    flags: bonus('race', seasons, date, hours).flags,
  };
}

module.exports = { RULES, FLAGS, parseHours, inHours, bonus, state, santaPct };
