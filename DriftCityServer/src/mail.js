// Notes (the client's in-game mail, NOTE 4010000 / 4020000 and the send dialog 2940000; DriftCity_RE/SOCIAL.md
// section 3). Notes persist per driver account in sqlite. The client's rules: content up to 80 characters, up to
// 10 receivers separated by ',', 50 Mito per receiver (0x6704a0), a saved box of 10 notes, unread notes expire 30 days
// after sending, read notes 24 hours after reading, saved notes never (the FREE_TIME column, 0x610e10).
// System notes (sender "Drift City") can carry Mito that the receiver takes from the read panel (0x5d0).
// They can also carry one item (the client's attachment block, "%s %d %d %d %d %s %s %s %d"): auction returns and
// custom-shop gifts (src/auction.js, src/vshop.js) in the goods format of src/goods.js. Taking it puts it in the bag.
'use strict';
const { cleanText } = require('./world');
const goods = require('./goods');

const MAX_CONTENT = 80;
const MAX_SYSTEM_CONTENT = 200;
const MAX_RECEIVERS = 10;
const COST_PER_RECEIVER = 50;
const SAVED_MAX = 10;
const INBOX_MAX = 100;              // remake rule: the oldest read notes make room
const UNREAD_TTL_MS = 30 * 86400000;
const READ_TTL_MS = 86400000;
const SYSTEM_SENDER = 'Drift City';
const WELCOME_MITO = 1000;          // remake rule: the welcome note carries a small gift (shows how attachments work)

class Mail {
  constructor(social) {
    this.social = social;
    const db = social.store.db;
    db.exec(`
      CREATE TABLE IF NOT EXISTS notes (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        owner INTEGER NOT NULL,
        sender TEXT NOT NULL,
        sender_id INTEGER NOT NULL DEFAULT 0,
        type INTEGER NOT NULL DEFAULT 0,       -- 0 player note, 1 system note
        status TEXT NOT NULL DEFAULT 'N',      -- N unread, R read, K saved
        content TEXT NOT NULL,
        mito INTEGER NOT NULL DEFAULT 0,
        sent_at INTEGER NOT NULL,
        read_at INTEGER NOT NULL DEFAULT 0
      );
      CREATE INDEX IF NOT EXISTS notes_owner ON notes(owner);
    `);
    // the item attachment (JSON goods, src/goods.js); added after release
    if (!db.prepare('PRAGMA table_info(notes)').all().some(c => c.name === 'item')) db.exec("ALTER TABLE notes ADD COLUMN item TEXT NOT NULL DEFAULT ''");
    this.gd = null;   // game tables (server.js), for the custom-part fit check when an item is taken
    this.q = {
      list: db.prepare('SELECT * FROM notes WHERE owner = ? ORDER BY id ASC'),
      get: db.prepare('SELECT * FROM notes WHERE id = ? AND owner = ?'),
      insert: db.prepare('INSERT INTO notes (owner, sender, sender_id, type, status, content, mito, sent_at, item) VALUES (?, ?, ?, ?, \'N\', ?, ?, ?, ?)'),
      takeItem: db.prepare("UPDATE notes SET item = '' WHERE id = ? AND item <> ''"),
      withItems: db.prepare("SELECT * FROM notes WHERE owner = ? AND item <> '' ORDER BY id ASC"),
      setStatus: db.prepare('UPDATE notes SET status = ?, read_at = ? WHERE id = ?'),
      takeMito: db.prepare('UPDATE notes SET mito = 0 WHERE id = ? AND mito > 0'),
      del: db.prepare('DELETE FROM notes WHERE id = ? AND owner = ?'),
      savedCount: db.prepare("SELECT COUNT(*) AS n FROM notes WHERE owner = ? AND status = 'K'"),
      inboxCount: db.prepare("SELECT COUNT(*) AS n FROM notes WHERE owner = ? AND status <> 'K'"),
      oldestRead: db.prepare("SELECT id FROM notes WHERE owner = ? AND status <> 'K' ORDER BY (status = 'N') ASC, id ASC LIMIT 1"),
      expire: db.prepare("DELETE FROM notes WHERE mito = 0 AND item = '' AND ((status = 'N' AND sent_at < ?) OR (status = 'R' AND read_at < ?))"),
      expireOwner: db.prepare("DELETE FROM notes WHERE owner = ? AND mito = 0 AND item = '' AND ((status = 'N' AND sent_at < ?) OR (status = 'R' AND read_at < ?))"),
    };
  }

  get clock() { return this.social.clock; }

  static item(r) { if (!r || !r.item) return null; try { return goods.normalize(JSON.parse(r.item)); } catch { return null; } }

  static view(r) {
    const item = Mail.item(r);
    return { id: r.id, sender: r.sender, type: r.type, status: r.status, content: r.content, mito: r.mito, sentAt: r.sent_at, readAt: r.read_at, item, itemName: item ? goods.describe(item) : '' };
  }

  expireFor(accountId) { const now = this.clock(); this.q.expireOwner.run(accountId, now - UNREAD_TTL_MS, now - READ_TTL_MS); }
  sweep() { const now = this.clock(); this.q.expire.run(now - UNREAD_TTL_MS, now - READ_TTL_MS); }

  /** Stores a note for an account (making room in a full inbox) and pushes it if the driver is online. */
  deliver(owner, sender, senderId, type, content, mito = 0, item = null) {
    while (this.q.inboxCount.get(owner).n >= INBOX_MAX) {
      const old = this.q.oldestRead.get(owner);
      if (!old) break;
      this.q.del.run(old.id, owner);
    }
    const attached = goods.normalize(item);
    const id = Number(this.q.insert.run(owner, sender, senderId, type, content, Math.max(0, Math.floor(mito)), this.clock(), attached ? JSON.stringify(attached) : '').lastInsertRowid);
    const row = this.q.get.get(id, owner);
    this.social.push(owner, { t: 'note.new', note: Mail.view(row) });
    return id;
  }

  /** A note from the game itself (rewards, crew events, the welcome note, auction results); item = goods or null. */
  system(accountId, content, mito = 0, sender = SYSTEM_SENDER, item = null) {
    return this.deliver(accountId, sender, 0, 1, String(content).slice(0, MAX_SYSTEM_CONTENT), mito, item);
  }

  /** Notes of a driver that still carry an item (the custom shop's received-gift list). */
  itemNotes(accountId) { return this.q.withItems.all(accountId).map(Mail.view); }

  online(session, firstTime) {
    if (firstTime) this.system(session.accountId, `Welcome to Drift City, ${session.name}! Open this note and click the gift slot to take ${WELCOME_MITO} Mito. Send notes to friends from the Community window (F1).`, WELCOME_MITO);
  }

  list(session) {
    this.expireFor(session.accountId);
    const rows = this.q.list.all(session.accountId).map(Mail.view);
    return { ok: true, now: this.clock(), inbox: rows.filter(n => n.status !== 'K'), saved: rows.filter(n => n.status === 'K'), savedMax: SAVED_MAX };
  }

  /** 0x5c8: opening an unread note marks it read (0x5c9). */
  read(session, id) {
    const r = this.q.get.get(id | 0, session.accountId);
    if (!r) return { ok: false, error: 'note not found' };
    if (r.status === 'N') { this.q.setStatus.run('R', this.clock(), r.id); r.status = 'R'; r.read_at = this.clock(); }
    return { ok: true, note: Mail.view(r) };
  }

  ids(m) { return (Array.isArray(m.ids) ? m.ids : [m.id]).map(x => x | 0).filter(x => x > 0).slice(0, 100); }

  /** 0x5ca per checked note (11031). */
  remove(session, m) {
    let n = 0;
    for (const id of this.ids(m)) n += this.q.del.run(id, session.accountId).changes;
    return n ? { ok: true, deleted: n } : { ok: false, error: 'note not found' };
  }

  /** 0x5cc: checked inbox notes move to the saved box (10 at most, else 11023). */
  save(session, m) {
    const ids = this.ids(m).filter(id => { const r = this.q.get.get(id, session.accountId); return r && r.status !== 'K'; });
    if (!ids.length) return { ok: false, error: 'note not found' };
    if (this.q.savedCount.get(session.accountId).n + ids.length > SAVED_MAX) return { ok: false, error: 'The saved note box is full.' };
    for (const id of ids) this.q.setStatus.run('K', this.clock(), id);
    return { ok: true, saved: ids.length };
  }

  /** 0x5ce per receiver; 0x5cf results 11033 sent / 11038 "%s does not exist" / 11035 cannot receive. */
  send(session, m) {
    const content = cleanText(m.content, MAX_CONTENT + 1);
    if (!content) return { ok: false, error: 'Please enter the content of the note.' };                 // 11010
    if (content.length > MAX_CONTENT) return { ok: false, error: `A note can have up to ${MAX_CONTENT} characters.` };
    const raw = Array.isArray(m.to) ? m.to : String(m.to || '').split(/[,;]/);
    const names = raw.map(x => String(x).trim()).filter(Boolean);
    if (!names.length) return { ok: false, error: 'Please enter the receiver.' };                        // 11009
    if (names.length > MAX_RECEIVERS) return { ok: false, error: `You can send a note to up to ${MAX_RECEIVERS} people at once.` };   // 11022
    const seen = new Set();
    const targets = [];
    for (const n of names) {
      const key = n.toLowerCase();
      if (seen.has(key)) return { ok: false, error: `${n} is entered twice.` };                         // 11037
      seen.add(key);
      if (key === String(session.name).toLowerCase()) return { ok: false, error: 'You cannot send a note to yourself.' };   // 11021
      const d = this.social.driver(n);
      if (!d) return { ok: false, error: `${n} does not exist.` };                                      // 11038
      if (this.social.blocks(d.accountId, session.accountId)) return { ok: false, error: `${d.name} cannot receive notes.` };   // 11035
      targets.push(d);
    }
    const cost = COST_PER_RECEIVER * targets.length;
    const p = session.profile;
    if ((p.Mito | 0) < cost) return { ok: false, error: 'You do not have enough Mito.' };                // 11013
    p.Mito -= cost;
    session.save();
    for (const d of targets) this.deliver(d.accountId, session.name, session.accountId, 0, content, 0);
    return { ok: true, sent: targets.length, cost, driver: p };
  }

  /** 0x5d0: take what is attached to a note (11036): its Mito and its item (into the bag, the garage or the current car). */
  take(session, id) {
    const r = this.q.get.get(id | 0, session.accountId);
    if (!r) return { ok: false, error: 'note not found' };
    const item = Mail.item(r);
    if (!(r.mito > 0) && !item) return { ok: false, error: 'Nothing is attached to this note.' };
    const p = session.profile;
    if (item) {
      const room = goods.room(p, item, this.gd);
      if (!room.ok) return room;
      if (this.q.takeItem.run(r.id).changes !== 1) return { ok: false, error: 'Nothing is attached to this note.' };
      goods.give(p, item, this.gd);
    }
    let mito = 0;
    if (r.mito > 0 && this.q.takeMito.run(r.id).changes === 1) mito = r.mito;
    p.Mito = Math.min(Number.MAX_SAFE_INTEGER, (p.Mito || 0) + mito);
    if (r.status === 'N') this.q.setStatus.run('R', this.clock(), r.id);
    session.save();
    return { ok: true, mito, item, itemName: item ? goods.describe(item, this.gd) : '', driver: p };
  }

  handle(session, m) {
    switch (m.t) {
      case 'note.list': return this.list(session);
      case 'note.read': return this.read(session, m.id);
      case 'note.delete': return this.remove(session, m);
      case 'note.save': return this.save(session, m);
      case 'note.send': return this.send(session, m);
      case 'note.take': return this.take(session, m.id);
      default: return { ok: false, error: `unknown message ${String(m.t).slice(0, 32)}` };
    }
  }
}

module.exports = { Mail, MAX_CONTENT, MAX_RECEIVERS, COST_PER_RECEIVER, SAVED_MAX, UNREAD_TTL_MS, READ_TTL_MS, SYSTEM_SENDER, WELCOME_MITO };
