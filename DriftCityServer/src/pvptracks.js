// The Battle Zone tracks of init\pvptrackinfo.xlt (DriftCity_RE/PVP.md section 11): one start row per track, its end
// row and the gate rows. The server uses them for the checkpoint gates (the client's 0x72f3c0 counts a gate within 150 m
// of it when the car crosses its line; gates with reward = 1 are reported to the server, 0x787ce0) and for the lap-time
// battle tracks (CityType 4). Keys match the Unity client's Pvp.TrackKey: "<city>/<track lower case>".
'use strict';
const fs = require('node:fs');
const path = require('node:path');
const { XltTable } = require('./gamedata');

const cache = new Map();

/** Horizontal distance between two [x, y] points (game x / y = Unity x / z). */
function dist(a, b) { return Math.hypot(a[0] - b[0], a[1] - b[1]); }

function centre(g) { return [(g.a[0] + g.b[0]) / 2, (g.a[1] + g.b[1]) / 2]; }

/**
 * A track: { key, id, name, laps, cityType, city, reverse, ai, gates: [{ a, b, c, reward }], start, end, per, lapLength }.
 * `per` = gate reports per lap: the N gates and then the finish line (the end row; the start line on circuits).
 * `lapLength` = the straight-line length start -> gates -> finish, a lower bound of the driven distance.
 */
function loadTracks(dir) {
  if (!dir) return new Map();
  dir = path.resolve(dir);
  if (cache.has(dir)) return cache.get(dir);
  const tracks = new Map();
  try {
    const file = path.join(dir, 'init', 'pvptrackinfo.xlt');
    if (fs.existsSync(file)) {
      const t = new XltTable(fs.readFileSync(file));
      let cur = null;
      const seen = new Set();
      for (const r of t.rows) {
        if (t.get(r, 'Support').trim() !== '1') { cur = null; continue; }
        const type = t.int(r, 'type', -1);
        const num = c => Number(String(r[c] || '').trim());
        const posCol = t.col('pos_start'), endCol = t.col('pos_end');
        const line = posCol >= 0 && endCol >= 0 ? { a: [num(posCol), num(posCol + 1)], b: [num(endCol), num(endCol + 1)] } : null;
        if (type === 0) {
          let city = t.get(r, 'city').trim().toLowerCase();
          if (city === 'kononia') city = 'koinonia';   // a typo in two start rows (RaceMission.TracksFor)
          const track = t.get(r, 'track').trim();
          const key = `${city}/${track.toLowerCase()}`;
          if (!city || !track || seen.has(key)) { cur = null; continue; }
          seen.add(key);
          cur = {
            key, id: t.int(r, 'Id', -1), name: t.get(r, 'name').trim(), laps: Math.max(1, Math.min(5, t.int(r, 'LapCount', 1))),
            cityType: t.int(r, 'CityType', 1), city, reverse: t.get(r, 'Reverse').trim() === '1', ai: t.get(r, 'AI').trim() === '1',
            gates: [], start: line, end: line,
          };
          tracks.set(key, cur);
        } else if (cur && type === 1) cur.end = line;
        else if (cur && type === 2 && line) cur.gates.push({ ...line, reward: t.get(r, 'reward').trim() === '1' });
      }
      for (const tr of tracks.values()) {
        for (const g of tr.gates) g.c = centre(g);
        tr.start.c = centre(tr.start); tr.end.c = centre(tr.end);
        tr.per = tr.gates.length + 1;
        let len = 0, prev = tr.start.c;
        for (const g of tr.gates) { len += dist(prev, g.c); prev = g.c; }
        len += dist(prev, tr.end.c);
        tr.lapLength = len;
      }
    }
  } catch { /* no table: gates off, the old lap checks apply */ }
  cache.set(dir, tracks);
  return tracks;
}

/** The point of gate report n (1-based over the whole race): a gate centre, or the finish line after each lap. */
function gatePoint(track, n) {
  const k = ((n - 1) % track.per) + 1;
  return k === track.per ? track.end.c : track.gates[k - 1].c;
}

/** The point before gate report n: the previous gate, the finish line (start of a lap) or the start line. */
function prevPoint(track, n) {
  if (n <= 1) return track.start.c;
  return gatePoint(track, n - 1);
}

module.exports = { loadTracks, gatePoint, prevPoint, dist };

/**
 * init\rewardgroup.xlt groups (GroupId -> [{ type: Exp | Mito | NormalItem | SkidItem | RewardGroup, item, count, period,
 * prob, desc }]): rw_battle_low / _mid / _high ("Battle_Result", the post-race reward cards) and rw_gtc_<class>_<a..d>_<rank>.
 */
function loadRewardGroups(dir) {
  const groups = new Map();
  if (!dir) return groups;
  try {
    const file = path.join(path.resolve(dir), 'init', 'rewardgroup.xlt');
    if (!fs.existsSync(file)) return groups;
    const t = new XltTable(fs.readFileSync(file));
    for (const r of t.rows) {
      const g = t.get(r, 'GroupId').trim();
      if (!g) continue;
      if (!groups.has(g)) groups.set(g, []);
      groups.get(g).push({
        type: t.get(r, 'Reward_Type').trim(), desc: t.get(r, 'Reward_Description').trim(), item: t.get(r, 'ItemId').trim(),
        count: t.int(r, 'Reward_Count', 1), period: t.int(r, 'Reward_Period', 0), prob: Number(t.get(r, 'Probability')) || 0,
      });
    }
  } catch { /* no table */ }
  return groups;
}

module.exports.loadRewardGroups = loadRewardGroups;
