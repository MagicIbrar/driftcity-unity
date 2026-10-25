// Crews (the client's TEAM / "Crew", Team Center dialogs 590000 / 2020000-2050000, my-crew page 3980000, rank popup
// 2790000) and crew battles over Battle Zone team races (DriftCity_RE/SOCIAL.md sections 4-5).
// Client rules kept: create needs level 20 and the globalcontent TEAMMAKE_NEEDMITO fee (US 2500 Mito), names of 4-12
// letters/digits/-/_, 10 built-in marks, ranks 1 Master / 2 1st Class / 3 2nd Class (new members join at 3), the
// Master can't leave, Master-only kick / rank / mark / rename (once) / delete, handing over the Master needs a target
// at level 20 who is online, deletion waits 7 days and can be cancelled meanwhile, crew grade C/B/A/S from points.
// Crew battles: crews own city channels; a challenger has to win 3 rounds in a row against the owner to take it.
'use strict';
const { cleanText } = require('./world');
const items = require('./items');
const { ChannelBattles, channelIndex, TABLE: CB_TABLE, ladderClass } = require('./channelbattle');

const CREATE_LEVEL = 20;          // globalcontent TEAMMAKE_NEEDLEVEL (every column)
const CREATE_FEE = 2500;          // globalcontent TEAMMAKE_NEEDMITO (US / KR column)
const MASTER_LEVEL = 20;          // 10849, hardcoded 0x14 at 0x606692
const MAX_MEMBERS = 30;           // remake rule (the server's 20171 gives no number)
const DELETE_GRACE_MS = 7 * 86400000;   // 10317
const REJOIN_LOCK_MS = 86400000;  // remake rule: 24 h after leaving / being kicked
const MARKS = 10;                 // Data\GameUI\teammark\01..10 (00 = none)
const ROUNDS_TO_CAPTURE = 3;      // Round 1, Round 2, Final (0x674290)
const CAPTURE_REWARD = 50000;     // remake rule for the Master reward of 10552 (the amount came from the server)
const RANKS = { 1: 'Crew Master', 2: '1st Class', 3: '2nd Class' };   // 10384-10387

const NAME_RE = /^[A-Za-z0-9_-]{4,12}$/;   // 0x74a680 / 0x74a740 (10602 / 10603)

/** "koinonia-1" -> "Koinonia Channel 1". */
function channelLabel(id) {
  const [city, n] = String(id).split('-');
  const names = { cras: 'Cras', moonpalace: 'Moon Palace', koinonia: 'Koinonia', oros: 'Oros' };
  return `${names[city] || city} Channel ${n || 1}`;
}

/** Crew grade (0x514530): S with any channel capture or 500000 points, A 100000, B 20000, else C. */
function crewGrade(points, captures) {
  if (captures > 0 || points >= 500000) return 'S';
  if (points >= 100000) return 'A';
  if (points >= 20000) return 'B';
  return 'C';
}

class Teams {
  constructor(social, races) {
    this.social = social;
    this.races = races || null;
    const db = social.store.db;
    db.exec(`
      CREATE TABLE IF NOT EXISTS crews (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        name TEXT NOT NULL UNIQUE COLLATE NOCASE,
        mark INTEGER NOT NULL DEFAULT 1,
        master INTEGER NOT NULL,
        created INTEGER NOT NULL,
        state TEXT NOT NULL DEFAULT 'A',     -- A active, C closing (deletion requested)
        close_at INTEGER NOT NULL DEFAULT 0,
        points INTEGER NOT NULL DEFAULT 0,
        wins INTEGER NOT NULL DEFAULT 0,
        losses INTEGER NOT NULL DEFAULT 0,
        captures INTEGER NOT NULL DEFAULT 0,
        renamed INTEGER NOT NULL DEFAULT 0
      );
      CREATE TABLE IF NOT EXISTS crew_members (
        account_id INTEGER PRIMARY KEY,
        crew_id INTEGER NOT NULL,
        rank INTEGER NOT NULL DEFAULT 3,
        joined INTEGER NOT NULL
      );
      CREATE TABLE IF NOT EXISTS crew_marks (
        crew_id INTEGER PRIMARY KEY,
        data TEXT NOT NULL,
        at INTEGER NOT NULL
      );
      CREATE TABLE IF NOT EXISTS crew_channels (
        channel TEXT PRIMARY KEY,
        crew_id INTEGER NOT NULL,
        since INTEGER NOT NULL
      );
    `);
    this.q = {
      crew: db.prepare('SELECT * FROM crews WHERE id = ?'),
      crewByName: db.prepare('SELECT * FROM crews WHERE name = ?'),
      insertCrew: db.prepare('INSERT INTO crews (name, mark, master, created) VALUES (?, ?, ?, ?)'),
      delCrew: db.prepare('DELETE FROM crews WHERE id = ?'),
      setState: db.prepare('UPDATE crews SET state = ?, close_at = ? WHERE id = ?'),
      setMaster: db.prepare('UPDATE crews SET master = ? WHERE id = ?'),
      setMark: db.prepare('UPDATE crews SET mark = ? WHERE id = ?'),
      markImage: db.prepare('SELECT data FROM crew_marks WHERE crew_id = ?'),
      setMarkImage: db.prepare('INSERT INTO crew_marks (crew_id, data, at) VALUES (?, ?, ?) ON CONFLICT(crew_id) DO UPDATE SET data = excluded.data, at = excluded.at'),
      rename: db.prepare('UPDATE crews SET name = ?, renamed = 1 WHERE id = ?'),
      addScore: db.prepare('UPDATE crews SET points = points + ?, wins = wins + ?, losses = losses + ? WHERE id = ?'),
      addCapture: db.prepare('UPDATE crews SET captures = captures + 1 WHERE id = ?'),
      closing: db.prepare("SELECT id FROM crews WHERE state = 'C' AND close_at <= ?"),
      ranking: db.prepare('SELECT * FROM crews ORDER BY captures DESC, points DESC, name ASC LIMIT ? OFFSET ?'),
      crewCount: db.prepare('SELECT COUNT(*) AS n FROM crews'),
      member: db.prepare('SELECT * FROM crew_members WHERE account_id = ?'),
      members: db.prepare("SELECT m.account_id, m.rank, m.joined, d.name, json_extract(d.profile, '$.Level') AS level FROM crew_members m JOIN drivers d ON d.account_id = m.account_id WHERE m.crew_id = ? ORDER BY m.rank ASC, d.name COLLATE NOCASE"),
      memberCount: db.prepare('SELECT COUNT(*) AS n FROM crew_members WHERE crew_id = ?'),
      addMember: db.prepare('INSERT INTO crew_members (account_id, crew_id, rank, joined) VALUES (?, ?, ?, ?)'),
      delMember: db.prepare('DELETE FROM crew_members WHERE account_id = ?'),
      delMembers: db.prepare('DELETE FROM crew_members WHERE crew_id = ?'),
      setRank: db.prepare('UPDATE crew_members SET rank = ? WHERE account_id = ?'),
      channelOwner: db.prepare('SELECT * FROM crew_channels WHERE channel = ?'),
      setChannel: db.prepare('INSERT INTO crew_channels (channel, crew_id, since) VALUES (?, ?, ?) ON CONFLICT(channel) DO UPDATE SET crew_id = excluded.crew_id, since = excluded.since'),
      channelsOf: db.prepare('SELECT channel, since FROM crew_channels WHERE crew_id = ? ORDER BY channel'),
      allChannels: db.prepare('SELECT c.channel, c.since, c.crew_id, t.name FROM crew_channels c JOIN crews t ON t.id = c.crew_id ORDER BY c.channel'),
      dropChannels: db.prepare('DELETE FROM crew_channels WHERE crew_id = ?'),
    };
    this.requests = new Map();   // crewId -> Map(accountId -> { name, at })
    this.streaks = new Map();    // `${channel}|${attackerCrewId}` -> rounds won in a row
    if (this.races) this.races.teams = this;   // crew battle rooms (race.js)
    this.rng = Math.random;
    this.cb = new ChannelBattles(this);          // schedule, table, queues, stakes, spectating, replays (src/channelbattle.js)
  }

  grade(crew) { return crew ? crewGrade(crew.points, crew.captures) : 'C'; }
  count(p, id) { return items.count(p, id); }
  give(p, id, n) { items.give(p, id, n); }
  setSchedule(text) { this.cb.setSchedule(text); }

  /** A crew takes a channel (3 straight wins, or a vacant channel held when the window closed): Master reward 10552. */
  capture(channelId, crewId, why) {
    this.q.setChannel.run(channelId, crewId, this.clock());
    this.q.addCapture.run(crewId);
    try { this.cb.q.setMaps.run('', channelId); } catch { /* no row */ }
    const ac = this.q.crew.get(crewId);
    if (ac) this.social.mail.system(ac.master, `You've succeeded in taking ${channelLabel(channelId)}. The OMD government sends Crew Master ${this.social.driverById(ac.master)?.name || ''} a reward.`, CAPTURE_REWARD);   // 10552
    if (ac) this.notify(crewId, { type: 'captured', crew: ac.name, channel: channelId, label: channelLabel(channelId), why: why || 'won' });
  }

  get clock() { return this.social.clock; }

  // ---------------- lookups ----------------
  memberOf(accountId) { return accountId ? this.q.member.get(accountId) || null : null; }
  crewOf(accountId) { const m = this.memberOf(accountId); return m ? this.q.crew.get(m.crew_id) || null : null; }
  crewNameOf(accountId) { const c = this.crewOf(accountId); return c ? c.name : ''; }
  onlineMembers(crewId) { return this.q.members.all(crewId).map(m => this.social.live(m.account_id)).filter(Boolean); }

  notify(crewId, notice, except) {
    for (const s of this.onlineMembers(crewId)) if (s !== except) s.send({ t: 'crew.notice', ...notice });
  }

  /** Name over the car (1950000) and in the friend list: kept on the live session. */
  refreshSession(accountId) {
    const s = this.social.live(accountId);
    if (!s) return;
    const name = this.crewNameOf(accountId);
    if (s.crewName !== name) { s.crewName = name; this.social.world.refreshLook(s); }
  }

  online(session) {
    this.sweep();
    const crew = this.crewOf(session.accountId);
    session.crewName = crew ? crew.name : '';
    if (crew) this.notify(crew.id, { type: 'login', crew: crew.name, name: session.name }, session);   // 11413
  }

  offline(session) { if (session) this.cb.leaveLobby(session); }

  view(crew, accountId) {
    if (!crew) return null;
    const members = this.q.members.all(crew.id).map(m => {
      const s = this.social.live(m.account_id);
      return { name: m.name, level: m.level | 0, rank: m.rank, rankName: RANKS[m.rank] || '', joined: m.joined, online: !!s, location: this.social.location(s), master: m.account_id === crew.master };
    });
    const me = accountId ? this.memberOf(accountId) : null;
    const reqs = this.requests.get(crew.id);
    return {
      id: crew.id, name: crew.name, mark: crew.mark, created: crew.created, state: crew.state, closeAt: crew.close_at,
      master: (members.find(m => m.master) || {}).name || '', points: crew.points, wins: crew.wins, losses: crew.losses,
      captures: crew.captures, grade: crewGrade(crew.points, crew.captures), renamed: !!crew.renamed, members, ...this.cb.ladder(crew.id),
      channels: this.q.channelsOf.all(crew.id).map(c => c.channel),
      myRank: me && me.crew_id === crew.id ? me.rank : 0,
      requests: me && me.crew_id === crew.id && me.rank <= 2 && reqs ? [...reqs.values()].map(r => r.name) : [],
    };
  }

  info(session) { return { ok: true, crew: this.view(this.crewOf(session.accountId), session.accountId), fee: CREATE_FEE, level: CREATE_LEVEL }; }

  /** Crew chat ("@text" / "/crew text", scope team). False when the speaker has no crew. */
  chat(session, msg) {
    const crew = this.crewOf(session.accountId);
    if (!crew) return false;
    for (const s of this.onlineMembers(crew.id)) s.send(msg);
    return true;
  }

  // ---------------- create / delete ----------------
  checkName(name, exceptId = 0) {
    if (typeof name !== 'string' || !name) return { ok: false, error: 'Enter the desired Crew name.' };
    if (!NAME_RE.test(name)) return { ok: false, error: 'A Crew name has 4-12 letters, digits, - or _.' };
    const names = this.social.world && this.social.world.names;   // the client's word filter (src/namefilter.js)
    if (names && names.crewName(name)) return { ok: false, error: 'That Crew name is not allowed.  Please use a different name.', code: 10607 };
    const taken = this.q.crewByName.get(name);
    if (taken && taken.id !== exceptId) return { ok: false, error: 'Unavailable Crew name.' };   // 10329
    return { ok: true, available: true };
  }

  /** MAKE (0x6753f0 -> 0x34a): level, fee, unique name, mark. */
  create(session, m) {
    const p = session.profile;
    if (this.memberOf(session.accountId)) return { ok: false, error: 'You are already in a Crew.' };
    if ((p.Level | 0) < CREATE_LEVEL) return { ok: false, error: `To start a new Crew, the Crew Master's level should be level ${CREATE_LEVEL} or higher.` };   // 10314
    if ((p.Mito | 0) < CREATE_FEE) return { ok: false, error: `To start a new Crew, a fee of ${CREATE_FEE} Mito is required.` };   // 10313
    const name = typeof m.name === 'string' ? m.name.trim() : '';
    const check = this.checkName(name);
    if (!check.ok) return check;
    const mark = Number.isInteger(m.mark) && m.mark >= 1 && m.mark <= MARKS ? m.mark : 0;
    if (!mark) return { ok: false, error: 'Select a Crew Mark.' };   // 10332
    const now = this.clock();
    let id;
    try { id = Number(this.q.insertCrew.run(name, mark, session.accountId, now).lastInsertRowid); }
    catch { return { ok: false, error: 'Unavailable Crew name.' }; }
    this.q.addMember.run(session.accountId, id, 1, now);
    p.Mito -= CREATE_FEE;
    session.save();
    this.refreshSession(session.accountId);
    return { ok: true, crew: this.view(this.q.crew.get(id), session.accountId), driver: p, fee: CREATE_FEE };
  }

  /** 2040000 -> 0x34c: the Master asks to delete; the crew closes after 7 days unless cancelled (2050000 -> 0x355). */
  disband(session, cancel) {
    const crew = this.crewOf(session.accountId);
    if (!crew) return { ok: false, error: "You don't have a Crew" };
    if (crew.master !== session.accountId) return { ok: false, error: 'Crew Master only.' };   // 10383
    if (cancel) {
      if (crew.state !== 'C') return { ok: false, error: 'The Crew is not being deleted.' };
      this.q.setState.run('A', 0, crew.id);
      this.notify(crew.id, { type: 'deleteCancelled', crew: crew.name, name: session.name });   // 10815
      return { ok: true, crew: this.view(this.q.crew.get(crew.id), session.accountId) };
    }
    if (crew.state === 'C') return { ok: false, error: `${crew.name} Crew is applying for deletion` };   // 10341
    const closeAt = this.clock() + DELETE_GRACE_MS;
    this.q.setState.run('C', closeAt, crew.id);
    this.notify(crew.id, { type: 'deleteRequested', crew: crew.name, name: session.name, closeAt });   // 10814
    return { ok: true, crew: this.view(this.q.crew.get(crew.id), session.accountId) };
  }

  deleteCrew(crew) {
    const members = this.q.members.all(crew.id);
    this.notify(crew.id, { type: 'deleted', crew: crew.name });   // 10816
    this.q.delMembers.run(crew.id);
    this.q.dropChannels.run(crew.id);
    this.q.delCrew.run(crew.id);
    this.requests.delete(crew.id);
    for (const m of members) this.refreshSession(m.account_id);
  }

  sweep() { for (const r of this.q.closing.all(this.clock())) { const c = this.q.crew.get(r.id); if (c) this.deleteCrew(c); } }

  // ---------------- membership ----------------
  /** Join request (COMMAND 700000 BADBOY / "/Join Crew", 0x367): by a member's name or the crew name (10163 / 10164). */
  request(session, m) {
    if (this.memberOf(session.accountId)) return { ok: false, error: 'You are already in a Crew.' };
    const meta = this.social.meta(session.accountId);
    if (meta.crew_left_at && this.clock() - meta.crew_left_at < REJOIN_LOCK_MS) return { ok: false, error: 'You can join a Crew 24 hours after leaving one.' };
    const name = typeof m.name === 'string' ? m.name.trim() : '';
    let crew = this.q.crewByName.get(name);
    if (!crew) {
      const d = this.social.driver(name);
      if (!d) return { ok: false, error: `${name.slice(0, 16)} does not exist.` };
      crew = this.crewOf(d.accountId);
      if (!crew) return { ok: false, error: `${d.name} is not a member of the Crew so you cannot join.` };   // 10164
    }
    if (crew.state !== 'A') return { ok: false, error: `${crew.name} Crew is applying for deletion` };
    if (this.q.memberCount.get(crew.id).n >= MAX_MEMBERS) return { ok: false, error: 'The Crew is full.' };
    // the Master approves (10020); 1st Class members may answer too while the Master is away (remake rule)
    const approvers = this.q.members.all(crew.id).filter(x => x.rank <= 2).map(x => this.social.live(x.account_id)).filter(Boolean);
    if (!approvers.length) return { ok: false, error: 'No Crew Master or 1st Class member is online.' };
    if (!this.requests.has(crew.id)) this.requests.set(crew.id, new Map());
    this.requests.get(crew.id).set(session.accountId, { name: session.name, at: this.clock() });
    for (const s of approvers) s.send({ t: 'crew.request', from: session.name, crew: crew.name });
    return { ok: true, crew: crew.name };   // 10163 "You have requested to join %s Crew."
  }

  /** 0x368 approve / 0x36c deny: the requester gets 10016 / 10017; members see 10817. */
  answer(session, m) {
    const me = this.memberOf(session.accountId);
    if (!me || me.rank > 2) return { ok: false, error: 'Crew Master only.' };
    const crew = this.q.crew.get(me.crew_id);
    const reqs = this.requests.get(crew.id);
    const d = this.social.driver(m.name);
    const req = d && reqs ? reqs.get(d.accountId) : null;
    if (!req) return { ok: false, error: 'no request from that driver' };
    reqs.delete(d.accountId);
    if (!m.accept) {
      this.social.push(d.accountId, { t: 'crew.notice', type: 'denied', crew: crew.name, name: d.name, you: true });   // 10017
      return { ok: true };
    }
    if (this.memberOf(d.accountId)) return { ok: false, error: `${d.name} is already in a Crew.` };
    if (this.q.memberCount.get(crew.id).n >= MAX_MEMBERS) return { ok: false, error: 'The Crew is full.' };
    this.q.addMember.run(d.accountId, crew.id, 3, this.clock());
    this.refreshSession(d.accountId);
    this.notify(crew.id, { type: 'joined', crew: crew.name, name: d.name });   // 10016 to them, 10817 to the rest
    return { ok: true, crew: this.view(crew, session.accountId) };
  }

  /** WITHDRAW (0x350): the Master can't leave (10378). */
  leave(session) {
    const me = this.memberOf(session.accountId);
    if (!me) return { ok: false, error: "You don't have a Crew" };
    const crew = this.q.crew.get(me.crew_id);
    if (crew.master === session.accountId) return { ok: false, error: 'The Crew Master cannot leave the Crew.' };
    this.notify(crew.id, { type: 'left', crew: crew.name, name: session.name });   // 10018 / 10818
    this.q.delMember.run(session.accountId);
    this.social.setCrewLeft(session.accountId, this.clock());
    this.refreshSession(session.accountId);
    return { ok: true };
  }

  /** BAN (0x36b, confirm 10389): Master only, not yourself (10388). */
  kick(session, name) {
    const crew = this.crewOf(session.accountId);
    if (!crew) return { ok: false, error: "You don't have a Crew" };
    if (crew.master !== session.accountId) return { ok: false, error: 'Crew Master only.' };
    const d = this.social.driver(name);
    const tm = d ? this.memberOf(d.accountId) : null;
    if (!tm || tm.crew_id !== crew.id) return { ok: false, error: 'not a member of your Crew' };
    if (d.accountId === session.accountId) return { ok: false, error: 'You cannot kick yourself.' };
    this.notify(crew.id, { type: 'kicked', crew: crew.name, name: d.name });   // 10023 / 10819
    this.q.delMember.run(d.accountId);
    this.social.setCrewLeft(d.accountId, this.clock());
    this.refreshSession(d.accountId);
    return { ok: true, crew: this.view(this.q.crew.get(crew.id), session.accountId) };
  }

  /** 2790000: TEAMPOS1/2 = 1st / 2nd Class (0x374), TEAMPOS3 = make Master (0x352: level 20, online). */
  setRank(session, name, rank) {
    const crew = this.crewOf(session.accountId);
    if (!crew) return { ok: false, error: "You don't have a Crew" };
    if (crew.master !== session.accountId) return { ok: false, error: 'Crew Master only.' };
    const d = this.social.driver(name);
    const tm = d ? this.memberOf(d.accountId) : null;
    if (!tm || tm.crew_id !== crew.id || d.accountId === session.accountId) return { ok: false, error: 'not a member of your Crew' };
    rank = rank | 0;
    if (rank === 1) {
      if (d.level < MASTER_LEVEL) return { ok: false, error: `The user has to be level ${MASTER_LEVEL} or above in order to become a Crew Leader.` };   // 10849
      if (!this.social.live(d.accountId)) return { ok: false, error: 'The user must be online in order to become the Crew Leader.' };   // 10869
      this.q.setMaster.run(d.accountId, crew.id);
      this.q.setRank.run(1, d.accountId);
      this.q.setRank.run(2, session.accountId);
      this.notify(crew.id, { type: 'leader', crew: crew.name, name: d.name });   // 10820
    } else if (rank === 2 || rank === 3) {
      this.q.setRank.run(rank, d.accountId);
      this.notify(crew.id, { type: 'promoted', crew: crew.name, name: d.name, rank, rankName: RANKS[rank] });   // 10838 / 10839
    } else return { ok: false, error: 'bad rank' };
    return { ok: true, crew: this.view(this.q.crew.get(crew.id), session.accountId) };
  }

  /** Crew mark (3990000, Master only 10383); the remake keeps the 10 built-in marks. */
  setMark(session, mark) {
    const crew = this.crewOf(session.accountId);
    if (!crew) return { ok: false, error: "You don't have a Crew" };
    if (crew.master !== session.accountId) return { ok: false, error: 'Crew Master only.' };
    if (!(Number.isInteger(mark) && mark >= 1 && mark <= MARKS)) return { ok: false, error: 'Select a Crew Mark.' };
    this.q.setMark.run(mark, crew.id);
    return { ok: true, crew: this.view(this.q.crew.get(crew.id), session.accountId) };
  }

  /**
   * The custom crew mark (3990000 LOADFILE, 0x6107c0: Master only, at most 4000 bytes): a 32x32 PNG kept per crew and
   * drawn over the built-in mark by the clients (CrewMarkUpload.cs).
   */
  setMarkImage(session, data) {
    const crew = this.crewOf(session.accountId);
    if (!crew) return { ok: false, error: "You don't have a Crew" };
    if (crew.master !== session.accountId) return { ok: false, error: 'Crew Master only.', code: 10383 };
    if (typeof data !== 'string' || data.length > 6000) return { ok: false, error: 'The mark is over 4000 bytes.' };
    let buf;
    try { buf = Buffer.from(data, 'base64'); } catch { buf = null; }
    if (!buf || buf.length < 8 || buf.length > 4000 || buf.readUInt32BE(0) !== 0x89504e47) return { ok: false, error: 'The mark must be a PNG of at most 4000 bytes.' };
    // 32x32 (the IHDR width / height)
    if (buf.length < 24 || buf.readUInt32BE(16) !== 32 || buf.readUInt32BE(20) !== 32) return { ok: false, error: 'The mark must be 32x32.' };
    this.q.setMarkImage.run(crew.id, buf.toString('base64'), Date.now());
    return { ok: true, crew: this.view(this.q.crew.get(crew.id), session.accountId) };
  }

  markImage(name) {
    const crew = typeof name === 'string' ? this.q.crewByName.get(name) : null;
    if (!crew) return { ok: false, error: 'no such crew' };
    const row = this.q.markImage.get(crew.id);
    return { ok: true, name: crew.name, data: row ? row.data : null };
  }

  /** Rename (0xce4, 11304): once per crew. */
  rename(session, name) {
    const crew = this.crewOf(session.accountId);
    if (!crew) return { ok: false, error: "You don't have a Crew" };
    if (crew.master !== session.accountId) return { ok: false, error: 'Crew Master only.' };
    if (crew.renamed) return { ok: false, error: 'Team names cannot be changed more than once.' };
    name = typeof name === 'string' ? name.trim() : '';
    const check = this.checkName(name, crew.id);
    if (!check.ok) return check;
    try { this.q.rename.run(name, crew.id); } catch { return { ok: false, error: 'Unavailable Crew name.' }; }
    this.notify(crew.id, { type: 'renamed', crew: name, old: crew.name, name: session.name });   // 11305
    for (const m of this.q.members.all(crew.id)) this.refreshSession(m.account_id);
    return { ok: true, crew: this.view(this.q.crew.get(crew.id), session.accountId) };
  }

  ranking(m) {
    const page = Math.max(0, m.page | 0);
    const rows = this.q.ranking.all(10, page * 10).map((c, i) => ({
      rank: page * 10 + i + 1, ladderClass: ladderClass(page * 10 + i + 1), name: c.name, mark: c.mark, points: c.points, wins: c.wins, losses: c.losses, captures: c.captures,
      grade: crewGrade(c.points, c.captures), members: this.q.memberCount.get(c.id).n,
    }));
    return { ok: true, page, total: this.q.crewCount.get().n, rows, channels: this.q.allChannels.all().map(c => ({ channel: c.channel, crew: c.name, since: c.since })) };
  }

  // ---------------- crew battles (src/race.js crew rooms) ----------------
  /** Hosting a crew battle room: the Master or a 1st Class member of an active crew (0x370). */
  battleHost(session) {
    const me = this.memberOf(session.accountId);
    if (!me) return { ok: false, error: "You don't have a Crew" };
    const crew = this.q.crew.get(me.crew_id);
    if (crew.state !== 'A') return { ok: false, error: `${crew.name} Crew is applying for deletion` };
    if (me.rank > 2) return { ok: false, error: 'Only the Crew Master or 1st Class members can start a Channel Battle.' };
    // attacking an owned channel in a battle window: crew rank B+ on major channels, one attack per window
    const key = session.channel ? session.channel.id : '', i = channelIndex(key), owner = this.channelOwner(key);
    if (i >= 0 && this.cb.battleMode(key) && owner && owner !== crew.id) { const b = this.cb.attackBlock(i, crew, me.rank, null, false); if (b) return b; }
    return { ok: true, crewId: crew.id, crew: crew.name };
  }

  battleCrew(session) { const c = this.crewOf(session.accountId); return c && c.state === 'A' ? { crewId: c.id, crew: c.name } : null; }

  channelOwner(channelId) { const r = this.q.channelOwner.get(channelId); return r ? r.crew_id : 0; }

  /**
   * A crew battle round ended. crews = [crewIdOfSideA (even slots), crewIdOfSideB]; winnerSide 0/1/2 (draw); results
   * carry each racer's side and team points. The channel owner (if one of the two crews) defends, else side A defends
   * a vacant channel. The attacker takes the channel after ROUNDS_TO_CAPTURE wins in a row; a draw counts for the
   * defender (the attacker has to win). Points: each racer's team points x 10 for their crew (remake rule).
   */
  battleResult(channelId, crews, winnerSide, results) {
    const [a, b] = crews;
    const owner = this.channelOwner(channelId);
    // a channel owned by a third crew: a friendly match (points only, no rounds)
    const ownerIsPlaying = owner === a || owner === b;
    // outside a battle window (or off the 20 battle channels) a crew room race is a friendly match
    const battle = this.cb.battleMode(channelId) || (channelIndex(channelId) < 0 && this.cb.window().open);
    const defenderSide = owner === b ? 1 : 0;
    const attackerSide = 1 - defenderSide;
    const attacker = crews[attackerSide], defender = crews[defenderSide];
    const attackerWon = winnerSide === attackerSide;
    const pts = [0, 0];
    for (const r of results) pts[r.team] += (r.points | 0) * 10;
    this.q.addScore.run(pts[0], winnerSide === 0 ? 1 : 0, winnerSide === 1 ? 1 : 0, a);
    this.q.addScore.run(pts[1], winnerSide === 1 ? 1 : 0, winnerSide === 0 ? 1 : 0, b);
    const key = `${channelId}|${attacker}`;
    let round = 0, captured = false;
    if (battle && (!owner || ownerIsPlaying)) {
      round = (this.streaks.get(key) || 0) + 1;
      if (attackerWon) {
        this.streaks.set(key, round);
        if (round >= ROUNDS_TO_CAPTURE) {
          captured = true;
          this.streaks.delete(key);
          this.capture(channelId, attacker, 'won');
        }
      } else this.streaks.delete(key);
    }
    const ac = this.q.crew.get(attacker), dc = this.q.crew.get(defender);
    const out = {
      channel: channelId, round, roundsToCapture: ROUNDS_TO_CAPTURE, attackerSide, defenderSide, winnerSide, attackerWon,
      captured, attacker: ac ? ac.name : '', defender: dc ? dc.name : '', owner: (() => { const o = this.q.channelOwner.get(channelId); const c = o && this.q.crew.get(o.crew_id); return c ? c.name : ''; })(),
      points: { a: pts[0], b: pts[1] }, friendly: !battle || (!!owner && !ownerIsPlaying), label: channelLabel(channelId),
    };
    // crews hear about it like the client's 10361-10377 notices
    for (const id of [a, b]) this.notify(id, { type: 'battle', ...out });
    return out;
  }

  // ---------------- dispatcher ----------------
  handle(session, m) {
    switch (m.t) {
      case 'crew.info': return this.info(session);
      case 'crew.check': return this.checkName(typeof m.name === 'string' ? m.name.trim() : '');
      case 'crew.create': return this.create(session, m);
      case 'crew.request': return this.request(session, m);
      case 'crew.answer': return this.answer(session, m);
      case 'crew.leave': return this.leave(session);
      case 'crew.kick': return this.kick(session, m.name);
      case 'crew.rank': return this.setRank(session, m.name, m.rank);
      case 'crew.mark': return this.setMark(session, m.mark);
      case 'crew.markImage': return this.setMarkImage(session, m.data);
      case 'crew.markImageGet': return this.markImage(m.name);
      case 'crew.rename': return this.rename(session, m.name);
      case 'crew.disband': return this.disband(session, false);
      case 'crew.cancelDisband': return this.disband(session, true);
      case 'crew.ranking': return this.ranking(m);
      default: { const r = this.cb.handle(session, m); if (r) return r; } return { ok: false, error: `unknown message ${String(m.t).slice(0, 32)}` };
    }
  }
}

module.exports = { Teams, crewGrade, CREATE_LEVEL, CREATE_FEE, MASTER_LEVEL, DELETE_GRACE_MS, ROUNDS_TO_CAPTURE, CAPTURE_REWARD, MARKS, RANKS, cleanText };
