#!/usr/bin/env node
'use strict';
// Builds the server's table cache (gamedata\) from YOUR installed Drift City client: the tables, road / path files and
// small data files it reads, plus empty placeholder files for the car-part models and horn sounds whose names the
// catalogue checks. Nothing is downloaded and the game folder is only read.
//
//   node tools/extract-gamedata.js "C:\Games\Drift City"
//   (or set DRIFTCITY_ROOT, or put the path into gameroot.txt next to server.js)
// server.js runs this by itself on first start when gamedata\ is missing and a game folder is found.
const fs = require('fs');
const path = require('path');
const { mountGame } = require('../src/nayapack');

const TABLE = /\.(xlt|tdf|chpath|ini|txt|tcs|signal|xipath2)$/i;
const PLACEHOLDER = name => (/^vehicle\\/.test(name) && /\.nif$/i.test(name)) || /^audio\\sfx\\sid_i_d_h_/i.test(name);
const SMALL_DAT = 2 * 1024 * 1024;

/** The game folder from an argument, DRIFTCITY_ROOT or gameroot.txt (server folder, then the client project's). */
function findGameRoot(arg) {
  const here = path.join(__dirname, '..');
  const fromFile = f => { try { return fs.readFileSync(f, 'utf8').trim().replace(/^"|"$/g, ''); } catch { return null; } };
  const candidates = [arg, process.env.DRIFTCITY_ROOT, fromFile(path.join(here, 'gameroot.txt')),
    fromFile(path.join(here, '..', 'DriftCityOffline', 'gameroot.txt'))];
  return candidates.find(c => c && fs.existsSync(path.join(c, 'Data'))) || null;
}

function extract(root, out = path.join(__dirname, '..', 'gamedata'), log = console.log) {
  const game = mountGame(root);
  let files = 0, holders = 0, bytes = 0;
  const write = (name, data) => {
    const target = path.join(out, ...name.split('\\'));
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.writeFileSync(target, data);
  };
  // archive entries plus loose files under Data (loose files win, as in the game)
  const names = new Set(game.index.keys());
  const walk = (dir, rel) => {
    for (const n of fs.readdirSync(dir, { withFileTypes: true })) {
      const r = rel ? `${rel}\\${n.name.toLowerCase()}` : n.name.toLowerCase();
      if (n.isDirectory()) walk(path.join(dir, n.name), r); else names.add(r);
    }
  };
  walk(game.data, '');
  for (const name of names) {
    try {
      if (TABLE.test(name)) { const d = game.read(name); write(name, d); files++; bytes += d.length; }
      else if (/\.dat$/i.test(name)) {
        const d = game.read(name);
        if (d.length <= SMALL_DAT) { write(name, d); files++; bytes += d.length; }
      } else if (PLACEHOLDER(name)) { write(name, Buffer.alloc(0)); holders++; }
    } catch (e) { log(`skip ${name}: ${e.message}`); }
  }
  game.close();
  fs.writeFileSync(path.join(out, 'EXTRACTED_FROM.txt'), `Extracted from ${root} on ${new Date().toISOString()}.\nThese are the game's own files: do not redistribute them.\n`);
  log(`game data: ${files} files (${(bytes / 1048576).toFixed(1)} MB) and ${holders} placeholders written to ${out}`);
  return { files, holders, bytes };
}

if (require.main === module) {
  const root = findGameRoot(process.argv[2]);
  if (!root) {
    console.error('Drift City game folder not found. Pass it as an argument, set DRIFTCITY_ROOT, or put it into gameroot.txt.');
    process.exit(1);
  }
  extract(root);
}

module.exports = { extract, findGameRoot };
