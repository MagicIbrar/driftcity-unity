// The HUV rules the original server decided and the remake keeps simple (DriftCity_RE/CHASES.md section 2, TRAFFIC.md
// section 11). Mirrored on the client by Runtime/Game/ChaseRules.cs (the offline rule) - keep the two in step.
//
// The chase start (BS_PktChaseBeginConfirm 0x4f2500): the server sent up to 16 HUV units of 0x30 bytes each, every one
// with its own AI parameters (0x4f0410 copies them into the HUV: +0xc speed km/h, +0x10 top speed, +0x14 the burst on a
// hit, +0x18 its time, +0x1c the turbo chance %, the three avoid levels) and the time limit (+0x17 + the item bonus).
// The remake's rule (remake rule, not the original server's which is lost):
//   count    1 + floor(level / 30) (level 1-29: 1, 30-59: 2, 60-89: 3, 90+: 4), + 1 for a party of 3 or 4, at most 16;
//            a boss chase is one boss (its escorts are its convoy, not units);
//   convoy   from level 40, one chase in ten is a convoy: 16 HUVs leave all 16 slots of the spawn point in a row, all at
//            one speed, each with the convoy health; 240 s;
//   speed    85 + level / 2 km/h (at most 140), +-5 at random, top speed + 30, a 1 s burst when hit, a 10 % turbo
//            roll (BossHuvInfo's escorts use the same burst / turbo); a boss unit sends 0: the client's BossHuvInfo row
//            (0x4a0f10 reads it there);
//   health   the HP the client quoted (100 + the HUV's Crash rating) bounded to 100..250, bosses 300, convoy HUVs 30;
//   time     150 s + 30 s per HUV beyond the first (at most 300 s), bosses 180 s, convoys 240 s.
'use strict';

const MAX_UNITS = 16;
const CONVOY_LEVEL = 40, CONVOY_CHANCE = 0.1, CONVOY_HP = 30, CONVOY_SECONDS = 240;
const BASE_SECONDS = 150, PER_UNIT_SECONDS = 30, MAX_SECONDS = 300, BOSS_SECONDS = 180, BOSS_HP = 300;

/**
 * The kept escorts of a boss (0x4a1470 copies their packets into the boss; 0x4a1860 / 0x4a1d10 release them):
 * share = the boss object's damage threshold +0x88 = BossHuvInfo 컨버팅시간 (column 22, +0x98) x 내구력 (+0x54) x 0.01
 * (0x4a0f10), i.e. the share of the boss's health that must be taken off; out = ms from the threshold until they come
 * out (11001: trailer stages 3 s + 3 s + 2.367 s, 0x49f2a0 / 0x49f340 / 0x49f3c0; 11004: 2 s + 3 s, 0x49f4d0 / 0x49f570);
 * finish = ms after that until the boss itself is finished (0x4a1860 state 7: damage = max + 10), 0 = never.
 * Durabilities (내구력) give Lil' Grimer's health against the boss's: 300 / 1400 of it.
 */
const KEPT = {
  11001: { pct: 40, durability: 1400, outMs: 8367, finishMs: 10000, units: [{ id: 11002, durability: 300 }], escorts: [] },
  11004: { pct: 40, durability: 600, outMs: 5000, finishMs: 0, units: [], escorts: [11005, 11006] },
};

const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));

/** How many HUVs a chase has: level, party size (1 solo), boss. */
function unitCount(level, members = 1, boss = false) {
  if (boss) return 1;
  return clamp(1 + Math.floor((level | 0) / 30) + (members >= 3 ? 1 : 0), 1, MAX_UNITS);
}

/** One HUV unit's AI parameters (km/h, s, %): what 0x4f0410 copied from the packet. */
function unitSpeeds(level, rng = Math.random) {
  const kmh = Math.round(clamp(85 + (level | 0) / 2, 85, 140) + (rng() * 10 - 5));
  return { kmh, top: kmh + 30, boostSeconds: 1, turbo: 10 };
}

/**
 * The units of a chase: { units: [{huv, kmh, top, boostSeconds, turbo, hp}], seconds, convoy }.
 * huv: the vehiclelist index the client picks the model with (the first is the caller's own pick).
 */
function chaseUnits({ level = 1, members = 1, boss = false, huv = 0, hp = 100, rng = Math.random } = {}) {
  const lv = clamp(level | 0, 1, 200);
  if (boss) return { units: [{ huv: Math.max(0, huv | 0), kmh: 0, top: 0, boostSeconds: 0, turbo: 0, hp: BOSS_HP }], seconds: BOSS_SECONDS, convoy: false };
  const quoted = clamp(Number(hp) || 100, 100, 250);
  const convoy = lv >= CONVOY_LEVEL && rng() < CONVOY_CHANCE;
  const count = convoy ? MAX_UNITS : unitCount(lv, members, false);
  const shared = unitSpeeds(lv, rng);
  const units = [];
  for (let k = 0; k < count; k++) {
    const s = convoy ? shared : (k === 0 ? shared : unitSpeeds(lv, rng));
    units.push({ huv: k === 0 ? Math.max(0, huv | 0) : Math.floor(rng() * 1000), ...s, hp: convoy ? CONVOY_HP : quoted });
  }
  const seconds = convoy ? CONVOY_SECONDS : Math.min(MAX_SECONDS, BASE_SECONDS + PER_UNIT_SECONDS * (count - 1));
  return { units, seconds, convoy };
}

/** The kept escorts' threshold reached? damage share of the boss (0..1). */
function splitDue(bossId, damageShare) {
  const k = KEPT[bossId | 0];
  return !!k && damageShare >= k.pct / 100;
}

module.exports = { chaseUnits, unitCount, unitSpeeds, splitDue, KEPT, MAX_UNITS, CONVOY_LEVEL, CONVOY_CHANCE, CONVOY_HP, CONVOY_SECONDS, BASE_SECONDS, BOSS_SECONDS, BOSS_HP };
