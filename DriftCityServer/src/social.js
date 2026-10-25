// Social layer: friends and block list (the client's friend manager 4000000), per-driver social options, and the
// dispatcher for the social messages (party.*, friend.*, note.*, crew.*). Notes live in mail.js, crews in teams.js.
// The client's rules and texts are in DriftCity_RE/SOCIAL.md; where the original server decided, the rule here is
// the remake's own (marked "remake rule").
'use strict';
const { Mail } = require('./mail');
const { Teams } = require('./teams');

const MAX_FRIENDS = 100;   // remake rule (the server's "list full" texts 20127/20131/20132 give no number)
const MAX_BLOCKED = 50;    // remake rule (20341)
const CITY_NAMES = { cras: 'Cras', moonpalace: 'Moon Palace', koinonia: 'Koinonia', oros: 'Oros' };

class Social {
  /** deps: { store, world, log, clock, races } */
  constructor(deps) {
    this.store = deps.store; this.world = deps.world; this.log = deps.log || (() => {}); this.clock = deps.clock || Date.now;
    const db = this.store.db;
    db.exec(`
      CREATE TABLE IF NOT EXISTS friends (
        owner INTEGER NOT NULL,
        other INTEGER NOT NULL,
        status TEXT NOT NULL,            -- from the owner's side: F friend, C asked (not approved yet), R asked by other, B blocked
        at INTEGER NOT NULL,
        PRIMARY KEY (owner, other)
      );
      CREATE TABLE IF NOT EXISTS social_meta (
        account_id INTEGER PRIMARY KEY,
        welcomed INTEGER NOT NULL DEFAULT 0,
        refuse_party INTEGER NOT NULL DEFAULT 0,
        crew_left_at INTEGER NOT NULL DEFAULT 0
      );
    `);
    this.q = {
      driverByName: db.prepare("SELECT account_id, name, json_extract(profile, '$.Level') AS level FROM drivers WHERE name = ?"),
      driverById: db.prepare("SELECT account_id, name, json_extract(profile, '$.Level') AS level FROM drivers WHERE account_id = ?"),
      row: db.prepare('SELECT status FROM friends WHERE owner = ? AND other = ?'),
      set: db.prepare('INSERT INTO friends (owner, other, status, at) VALUES (?, ?, ?, ?) ON CONFLICT(owner, other) DO UPDATE SET status = excluded.status, at = excluded.at'),
      del: db.prepare('DELETE FROM friends WHERE owner = ? AND other = ?'),
      list: db.prepare("SELECT f.other, f.status, d.name, json_extract(d.profile, '$.Level') AS level FROM friends f JOIN drivers d ON d.account_id = f.other WHERE f.owner = ? ORDER BY d.name COLLATE NOCASE"),
      count: db.prepare("SELECT COUNT(*) AS n FROM friends WHERE owner = ? AND status <> 'B'"),
      countBlocked: db.prepare("SELECT COUNT(*) AS n FROM friends WHERE owner = ? AND status = 'B'"),
      friendsOf: db.prepare("SELECT owner FROM friends WHERE other = ? AND status = 'F'"),
      meta: db.prepare('SELECT * FROM social_meta WHERE account_id = ?'),
      metaInit: db.prepare('INSERT OR IGNORE INTO social_meta (account_id) VALUES (?)'),
      setWelcomed: db.prepare('UPDATE social_meta SET welcomed = 1 WHERE account_id = ?'),
      setRefuse: db.prepare('UPDATE social_meta SET refuse_party = ? WHERE account_id = ?'),
      setCrewLeft: db.prepare('UPDATE social_meta SET crew_left_at = ? WHERE account_id = ?'),
    };
    this.mail = new Mail(this);
    this.teams = new Teams(this, deps.races);
    // world hooks: block checks for whispers/party requests, crew chat, online/offline notices
    this.world.social = this;
    this.world.teams = this.teams;
    this.world.onAdd(s => this.online(s));
    this.world.onRemove(s => this.offline(s));
  }

  // ---------------- helpers ----------------
  driver(name) {
    if (typeof name !== 'string' || !name.trim()) return null;
    const r = this.q.driverByName.get(name.trim());
    return r ? { accountId: r.account_id, name: r.name, level: r.level | 0 } : null;
  }
  driverById(id) { const r = this.q.driverById.get(id); return r ? { accountId: r.account_id, name: r.name, level: r.level | 0 } : null; }
  meta(accountId) { this.q.metaInit.run(accountId); return this.q.meta.get(accountId); }
  setCrewLeft(accountId, at) { this.meta(accountId); this.q.setCrewLeft.run(at, accountId); }
  status(owner, other) { const r = this.q.row.get(owner, other); return r ? r.status : null; }
  /** True when `owner` has `other` on the block list. */
  blocks(owner, other) { return !!owner && !!other && this.status(owner, other) === 'B'; }
  live(accountId) { return this.world.findByAccount(accountId); }
  push(accountId, msg) { const s = this.live(accountId); if (s) s.send(msg); return !!s; }

  /** Friend location like the client's 0x607f50: "Channel %2d - <city>", the Battle Zone, or offline (10504). */
  location(s) {
    if (!s) return { online: false, text: 'Offline' };
    if (s.race) return { online: true, city: s.channel ? s.channel.city : null, channel: s.channel ? s.channel.index : 0, text: 'Battle Zone' };
    if (!s.channel) return { online: true, city: null, channel: 0, text: 'Garage' };
    // the Undercity runs in a private channel 100 + room (src/undercity.js): text 10809
    if (s.channel.index >= 100) return { online: true, city: s.channel.city, channel: s.channel.index, text: 'Undercity' };
    return { online: true, city: s.channel.city, channel: s.channel.index, text: `Channel ${String(s.channel.index).padStart(2, ' ')} - ${CITY_NAMES[s.channel.city] || s.channel.city}` };
  }

  // ---------------- online / offline ----------------
  online(session) {
    if (!session.accountId) return;
    const meta = this.meta(session.accountId);
    session.refuseParty = !!meta.refuse_party;
    // 11412 "[Friend] %s has logged on." to friends who are online
    for (const r of this.q.friendsOf.all(session.accountId)) {
      if (this.status(session.accountId, r.owner) !== 'F') continue;
      this.push(r.owner, { t: 'friend.online', name: session.name, online: true });
    }
    this.teams.online(session);
    this.mail.online(session, !meta.welcomed);
    if (!meta.welcomed) this.q.setWelcomed.run(session.accountId);
  }

  offline(session) {
    if (!session.accountId) return;
    for (const r of this.q.friendsOf.all(session.accountId)) {
      if (this.status(session.accountId, r.owner) === 'F') this.push(r.owner, { t: 'friend.online', name: session.name, online: false });
    }
    this.teams.offline(session);
  }

  // ---------------- friends ----------------
  friendList(session) {
    const rows = this.q.list.all(session.accountId);
    const entry = r => {
      const s = r.status === 'F' ? this.live(r.other) : null;
      return { name: r.name, level: r.level | 0, status: r.status, crew: this.teams.crewNameOf(r.other), location: this.location(s), online: !!s };
    };
    return {
      ok: true,
      friends: rows.filter(r => r.status !== 'B').map(entry),
      blocked: rows.filter(r => r.status === 'B').map(r => ({ name: r.name, level: r.level | 0, status: 'B', crew: this.teams.crewNameOf(r.other) })),
    };
  }

  /** ADD_FRIEND (0xe8): a request the other driver approves (10569 -> 10810 / 10811); a crossed request makes friends at once. */
  friendAdd(session, name) {
    const other = this.driver(name);
    if (!other) return { ok: false, error: `${String(name || '').slice(0, 16)} does not exist.` };
    if (other.accountId === session.accountId) return { ok: false, error: 'You cannot add yourself.' };
    const mine = this.status(session.accountId, other.accountId), theirs = this.status(other.accountId, session.accountId);
    if (mine === 'F') return { ok: false, error: `${other.name} is already on your Friend List.` };
    if (mine === 'C') return { ok: false, error: `You have already sent ${other.name} a friend request.` };
    if (mine === 'B') return { ok: false, error: `${other.name} is on your deny list.` };
    if (theirs === 'B') return { ok: false, error: `${other.name} has declined your friend request.` };
    if (this.q.count.get(session.accountId).n >= MAX_FRIENDS) return { ok: false, error: 'Your Friend List is full.' };
    const now = this.clock();
    if (mine === 'R') return this.friendAnswer(session, other.name, true);
    if (this.q.count.get(other.accountId).n >= MAX_FRIENDS) return { ok: false, error: `${other.name}'s Friend List is full.` };
    this.q.set.run(session.accountId, other.accountId, 'C', now);
    this.q.set.run(other.accountId, session.accountId, 'R', now);
    this.push(other.accountId, { t: 'friend.request', from: session.name });
    return { ok: true, pending: true };
  }

  /** 0xef answer: accept makes both sides friends; decline removes the request. */
  friendAnswer(session, name, accept) {
    const other = this.driver(name);
    if (!other || this.status(session.accountId, other.accountId) !== 'R') return { ok: false, error: 'no friend request from that driver' };
    const now = this.clock();
    if (accept) {
      if (this.q.count.get(session.accountId).n > MAX_FRIENDS) return { ok: false, error: 'Your Friend List is full.' };
      this.q.set.run(session.accountId, other.accountId, 'F', now);
      this.q.set.run(other.accountId, session.accountId, 'F', now);
    } else {
      this.q.del.run(session.accountId, other.accountId);
      if (this.status(other.accountId, session.accountId) === 'C') this.q.del.run(other.accountId, session.accountId);
    }
    this.push(other.accountId, { t: 'friend.answer', from: session.name, accept: !!accept });
    this.push(other.accountId, { t: 'friend.changed' });
    return { ok: true };
  }

  /** REMOVE_FRIEND (0xeb, confirm 11192): both sides lose the entry (10833 to me, 10832 to them). */
  friendRemove(session, name) {
    const other = this.driver(name);
    const st = other ? this.status(session.accountId, other.accountId) : null;
    if (!st || st === 'B') return { ok: false, error: 'No friend selected.' };   // 10537
    this.q.del.run(session.accountId, other.accountId);
    if (this.status(other.accountId, session.accountId) !== 'B') this.q.del.run(other.accountId, session.accountId);
    if (st === 'F') this.push(other.accountId, { t: 'friend.removed', from: session.name });
    this.push(other.accountId, { t: 'friend.changed' });
    return { ok: true };
  }

  /** DENY_FRIEND (0xe2, 11179; 11182 yourself, 11183 already blocked). Blocking ends a friendship or request. */
  friendBlock(session, name) {
    const other = this.driver(name);
    if (!other) return { ok: false, error: `${String(name || '').slice(0, 16)} does not exist.` };
    if (other.accountId === session.accountId) return { ok: false, error: 'You cannot block yourself.' };
    if (this.status(session.accountId, other.accountId) === 'B') return { ok: false, error: `${other.name} is already blocked.` };
    if (this.q.countBlocked.get(session.accountId).n >= MAX_BLOCKED) return { ok: false, error: 'Your deny list is full.' };
    this.q.set.run(session.accountId, other.accountId, 'B', this.clock());
    if (this.status(other.accountId, session.accountId) !== 'B') this.q.del.run(other.accountId, session.accountId);
    this.push(other.accountId, { t: 'friend.changed' });
    return { ok: true };
  }

  /** DENYCANCEL_FRIEND (0xdf, confirm 11180). */
  friendUnblock(session, name) {
    const other = this.driver(name);
    if (!other || this.status(session.accountId, other.accountId) !== 'B') return { ok: false, error: 'not on your deny list' };
    this.q.del.run(session.accountId, other.accountId);
    return { ok: true };
  }

  /** "Find a Friend" (10220): where an online driver is. */
  locate(session, name) {
    const other = this.driver(name);
    if (!other) return { ok: false, error: `${String(name || '').slice(0, 16)} does not exist.` };
    const s = this.live(other.accountId);
    return { ok: true, name: other.name, level: other.level, crew: this.teams.crewNameOf(other.accountId), location: this.location(s) };
  }

  options(session, m) {
    if (m.refuseParty !== undefined) {
      session.refuseParty = !!m.refuseParty;
      this.meta(session.accountId);
      this.q.setRefuse.run(session.refuseParty ? 1 : 0, session.accountId);
    }
    return { ok: true, refuseParty: !!session.refuseParty };
  }

  // ---------------- dispatcher ----------------
  owns(t) { return /^(friend|note|crew|social)\./.test(t) || /^party\.(decline|request|answer|kick|info)$/.test(t) || t === 'party.invite'; }

  /** Returns the reply body (the caller sends it), or undefined for fire-and-forget messages. */
  handle(session, m) {
    const w = this.world;
    switch (m.t) {
      case 'party.invite': return w.partyInvite(session, m.name, { partyName: m.party, message: m.message });
      case 'party.decline': return w.partyDecline(session, m.message);
      case 'party.request': return w.partyRequest(session, m.name);
      case 'party.answer': return w.partyAnswer(session, m.name, !!m.accept);
      case 'party.kick': return w.partyKick(session, m.name);
      case 'party.info': { const p = w.liveParty(session); return { ok: true, party: p ? w.partyInfo(p) : null }; }
      case 'friend.list': return this.friendList(session);
      case 'friend.add': return this.friendAdd(session, m.name);
      case 'friend.answer': return this.friendAnswer(session, m.name, !!m.accept);
      case 'friend.remove': return this.friendRemove(session, m.name);
      case 'friend.block': return this.friendBlock(session, m.name);
      case 'friend.unblock': return this.friendUnblock(session, m.name);
      case 'friend.locate': return this.locate(session, m.name);
      case 'social.options': return this.options(session, m);
    }
    if (m.t.startsWith('note.')) return this.mail.handle(session, m);
    if (m.t.startsWith('crew.')) return this.teams.handle(session, m);
    return { ok: false, error: `unknown message ${String(m.t).slice(0, 32)}` };
  }

  /** Hourly housekeeping: expired notes, crews whose deletion time has come. */
  sweep() { this.mail.sweep(); this.teams.sweep(); }
}

module.exports = { Social, MAX_FRIENDS, MAX_BLOCKED, CITY_NAMES };
