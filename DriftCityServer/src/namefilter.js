// The client's word filter (DriftCity_RE/AUDIO.md 6, Unity Game/NameFilter.cs): init\slang.xlt and
// init\operatormisrepresent.xlt, both lowercased, matched as case-insensitive substrings.
//   driver names (character creation 0x749f02): neither list  -> 10606 "That ID is not allowed."
//   crew names: neither list                                  -> 10607
//   room titles (CREATEROOM 0x57a3ee): slang                  -> 10608 "That Room name is not allowed."
//   chat lines (0x5bcf60): slang censored, each matched character replaced with one of "!@#$%^&*"
// The server is authoritative online: the client checks first, the server refuses or censors again.
'use strict';
const fs = require('node:fs');
const path = require('node:path');
const { XltTable, DEFAULT_DIR } = require('./gamedata');

const SYMBOLS = '!@#$%^&*';

/** The second cell of each row, first token ("%s"), lowercased. */
function readWords(file) {
  try {
    if (!fs.existsSync(file)) return [];
    const t = new XltTable(fs.readFileSync(file));
    const out = [];
    for (const r of t.rows) {
      if (r.length < 2) continue;
      const w = String(r[1]).trim().split(/[ \t]+/)[0];
      if (w) out.push(w.toLowerCase());
    }
    return out;
  } catch { return []; }
}

class NameFilter {
  constructor(dir = process.env.DC_GAMEDATA || DEFAULT_DIR) {
    const init = path.join(path.resolve(dir), 'init');
    this.slang = readWords(path.join(init, 'slang.xlt'));
    this.operatorNames = readWords(path.join(init, 'operatormisrepresent.xlt'));
  }

  static has(words, text) {
    if (typeof text !== 'string' || !text) return false;
    const low = text.toLowerCase();
    return words.some(w => low.includes(w));
  }

  hasSlang(text) { return NameFilter.has(this.slang, text); }
  hasOperatorName(text) { return NameFilter.has(this.operatorNames, text); }

  /** Returns the refusal text id or null. */
  driverName(name) { return this.hasSlang(name) || this.hasOperatorName(name) ? 10606 : null; }
  crewName(name) { return this.hasSlang(name) || this.hasOperatorName(name) ? 10607 : null; }
  roomTitle(title) { return this.hasSlang(title) ? 10608 : null; }

  /** Every character of a slang match replaced with a random symbol (0x7dfcd0). */
  censor(text, words = this.slang) {
    if (typeof text !== 'string' || !text) return text;
    const chars = text.split('');
    const low = text.toLowerCase();
    if (low.length !== text.length) return text;   // a case change altered the length: leave it
    for (const w of words) {
      if (!w) continue;
      let i = low.indexOf(w);
      while (i >= 0) {
        for (let k = 0; k < w.length && i + k < chars.length; k++) chars[i + k] = SYMBOLS[Math.floor(Math.random() * SYMBOLS.length)];
        i = low.indexOf(w, i + w.length);
      }
    }
    return chars.join('');
  }
}

const TEXTS = {
  10606: 'That ID is not allowed.  Please use a different name.',
  10607: 'That Crew name is not allowed.  Please use a different name.',
  10608: 'That Room name is not allowed.  Please use a different name.',
};

let shared = null;
/** One filter per process (the tables are read once). */
function nameFilter() { return shared || (shared = new NameFilter()); }

module.exports = { NameFilter, nameFilter, TEXTS };
