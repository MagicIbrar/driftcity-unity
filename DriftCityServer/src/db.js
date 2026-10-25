// SQLite storage (node:sqlite, no native build needed): accounts and their driver profile.
'use strict';
const { DatabaseSync } = require('node:sqlite');
const path = require('node:path');
const fs = require('node:fs');

class Store {
  constructor(file) {
    fs.mkdirSync(path.dirname(file), { recursive: true });
    this.db = new DatabaseSync(file);
    this.db.exec(`
      PRAGMA journal_mode = WAL;
      CREATE TABLE IF NOT EXISTS accounts (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        username TEXT NOT NULL UNIQUE COLLATE NOCASE,
        salt TEXT NOT NULL,
        hash TEXT NOT NULL,
        banned INTEGER NOT NULL DEFAULT 0,
        admin INTEGER NOT NULL DEFAULT 0,
        created_at INTEGER NOT NULL
      );
      CREATE TABLE IF NOT EXISTS drivers (
        account_id INTEGER PRIMARY KEY REFERENCES accounts(id),
        name TEXT NOT NULL UNIQUE COLLATE NOCASE,
        profile TEXT NOT NULL,
        updated_at INTEGER NOT NULL
      );
      CREATE TABLE IF NOT EXISTS race_results (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        track TEXT NOT NULL,
        driver TEXT NOT NULL,
        place INTEGER NOT NULL,
        time REAL NOT NULL,
        at INTEGER NOT NULL
      );
      -- server-wide event state (src/events.js): scratch-card gauge, Christmas trees, harvest receipts
      CREATE TABLE IF NOT EXISTS event_state (
        key TEXT PRIMARY KEY,
        value TEXT NOT NULL
      );
    `);
    this.q = {
      accountByName: this.db.prepare('SELECT * FROM accounts WHERE username = ?'),
      insertAccount: this.db.prepare('INSERT INTO accounts (username, salt, hash, created_at) VALUES (?, ?, ?, ?)'),
      driverByAccount: this.db.prepare('SELECT * FROM drivers WHERE account_id = ?'),
      driverByName: this.db.prepare('SELECT * FROM drivers WHERE name = ?'),
      insertDriver: this.db.prepare('INSERT INTO drivers (account_id, name, profile, updated_at) VALUES (?, ?, ?, ?)'),
      saveDriver: this.db.prepare('UPDATE drivers SET profile = ?, updated_at = ? WHERE account_id = ?'),
      setBanned: this.db.prepare('UPDATE accounts SET banned = ? WHERE username = ?'),
      setAdmin: this.db.prepare('UPDATE accounts SET admin = ? WHERE username = ?'),
      insertResult: this.db.prepare('INSERT INTO race_results (track, driver, place, time, at) VALUES (?, ?, ?, ?, ?)'),
      bestTimes: this.db.prepare('SELECT driver, MIN(time) AS time FROM race_results WHERE track = ? GROUP BY driver ORDER BY time ASC LIMIT ?'),
      // PvP ladder (race.js): points kept in the driver profile; drivers who never raced a ladder race are left out
      ladder: this.db.prepare("SELECT name, json_extract(profile, '$.Level') AS level, json_extract(profile, '$.LadderPoints') AS points, json_extract(profile, '$.PvpRaces') AS races, json_extract(profile, '$.PvpWins') AS wins FROM drivers WHERE json_extract(profile, '$.PvpRaces') > 0 ORDER BY points DESC, name ASC LIMIT ? OFFSET ?"),
      ladderCount: this.db.prepare("SELECT COUNT(*) AS n FROM drivers WHERE json_extract(profile, '$.PvpRaces') > 0"),
      ladderRank: this.db.prepare("SELECT COUNT(*) AS n FROM drivers WHERE json_extract(profile, '$.PvpRaces') > 0 AND json_extract(profile, '$.LadderPoints') > ?"),
      eventGet: this.db.prepare('SELECT value FROM event_state WHERE key = ?'),
      eventSet: this.db.prepare('INSERT INTO event_state (key, value) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value'),
      topDrivers: this.db.prepare("SELECT name, json_extract(profile, '$.Level') AS level, json_extract(profile, '$.Exp') AS exp FROM drivers ORDER BY level DESC, exp DESC LIMIT ?"),
    };
  }

  account(username) { return this.q.accountByName.get(username); }
  createAccount(username, salt, hash) { return this.q.insertAccount.run(username, salt, hash, Date.now()).lastInsertRowid; }
  driver(accountId) {
    const row = this.q.driverByAccount.get(accountId);
    return row ? { name: row.name, profile: JSON.parse(row.profile) } : null;
  }
  driverNameTaken(name) { return !!this.q.driverByName.get(name); }
  createDriver(accountId, name, profile) { this.q.insertDriver.run(accountId, name, JSON.stringify(profile), Date.now()); }
  saveDriver(accountId, profile) { this.q.saveDriver.run(JSON.stringify(profile), Date.now(), accountId); }
  setBanned(username, banned) { return this.q.setBanned.run(banned ? 1 : 0, username).changes > 0; }
  setAdmin(username, admin) { return this.q.setAdmin.run(admin ? 1 : 0, username).changes > 0; }
  addResult(track, driver, place, time) { this.q.insertResult.run(track, driver, place, time, Date.now()); }
  bestTimes(track, limit = 10) { return this.q.bestTimes.all(track, limit); }
  ladder(limit = 10, offset = 0) { return this.q.ladder.all(limit, offset); }
  ladderCount() { return this.q.ladderCount.get().n; }
  ladderRank(points) { return this.q.ladderRank.get(points).n + 1; }
  topDrivers(limit = 20) { return this.q.topDrivers.all(limit); }
  eventGet(key, fallback = null) { const r = this.q.eventGet.get(key); if (!r) return fallback; try { return JSON.parse(r.value); } catch { return fallback; } }
  eventSet(key, value) { this.q.eventSet.run(key, JSON.stringify(value)); }
  close() { this.db.close(); }
}

module.exports = { Store };
