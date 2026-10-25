// Server notices: the original client's reactions to server-originated texts (DriftCity_RE/HANDLERS.md), sent as
//   { t: 'notice', kind, text?, key?, args?, ... }
// The client (Net/ServerNotices.cs) shows each kind the way DriftCity.exe did:
//   emergency  EMERGENCY 2770000 centre notice for `seconds` (12 s, op 0x093 "notice..." on the channel scope, 0x54d850)
//              plus the line in the chat; `large` uses ID_EMERGENCY_NOTICE_LARGE_TEXT
//   scroll     SCROLL 2780000 marquee, 70 px/s right to left, one pass per queued text (op 0x093 scope "bcast", 0x5523a0)
//   system     POPUP 3250000 message box + the same line in the system chat (op 0x008, 0x50c990)
//   chat       a system chat line only (0x5b5160, colour 0xFFF29177)
//   gm         a GM chat line (op 0x093 scope "GM")
//   level      10054 "%s has reached level %d." for another driver (op 0x168, 0x697a90), sent to the channel
//   event      the event reward popup SEASONEVENT 3340000 {title, text, icon} (op 0x089 generic subtypes 8/12/13/15/28/29)
//   obtain     10096 "Obtained %d %s" + SID_ITEM_DROP_GET1 (op 0x089 subtypes 7/14/18)
// `key` names a globaltext / globaltext_server text (20000-21999 are the server's own texts) that the client fills with
// `args` like the client's sprintf; `text` is used when there is no key (or the key is unknown to the client).
// Also: { t: 'time', now } (server UTC ms) once per driver and every 10 minutes, the clock op 0x003 kept in net+0x118.
// Remake rules (the original server's own logic is not in the client): level-ups are noticed by a 1 s scan of the
// drivers; maintenance counts down with emergency notices, then kicks everyone but admins and refuses logins.
'use strict';

const KINDS = new Set(['emergency', 'scroll', 'system', 'chat', 'gm', 'level', 'event', 'obtain']);
const TIME_RESYNC_MS = 10 * 60000;
const MAINT_WARN_MIN = [30, 15, 10, 5, 4, 3, 2, 1];

const clean = (s, max = 300) => (typeof s === 'string' ? s.replace(/[\u0000-\u001f]/g, ' ').trim().slice(0, max) : '');

class Notices {
  /** opts: world, log, clock (ms, default Date.now), tickMs (tests) */
  constructor(opts = {}) {
    this.world = opts.world;
    this.log = opts.log || (() => {});
    this.clock = opts.clock || Date.now;
    this.levels = new WeakMap();      // session -> last level seen
    this.timeSent = new WeakMap();    // session -> when the clock was last sent
    this.maint = null;                // { at, text, warned:Set<minutes> }
    this.closedForMaintenance = false;
    this.timer = setInterval(() => { try { this.tick(); } catch (err) { this.log(`notices tick: ${err.message}`); } }, opts.tickMs || 1000);
    if (this.timer.unref) this.timer.unref();
  }

  close() { clearInterval(this.timer); }

  /** The message for one kind; null when the kind is unknown or there is nothing to show. */
  static build(kind, body = {}) {
    if (!KINDS.has(kind)) return null;
    const m = { t: 'notice', kind };
    const text = clean(body.text);
    if (text) m.text = text;
    if (typeof body.key === 'string' && /^[A-Za-z0-9_]{1,40}$/.test(body.key)) m.key = body.key;
    if (Array.isArray(body.args)) m.args = body.args.slice(0, 6).map(a => (typeof a === 'number' ? a : clean(String(a), 60)));
    if (kind === 'emergency') { m.seconds = Number.isFinite(body.seconds) ? Math.max(1, Math.min(120, body.seconds)) : 12; if (body.large) m.large = true; }
    if (kind === 'gm' && Number.isInteger(body.color)) m.color = body.color >>> 0;
    if (kind === 'level') { m.name = clean(body.name, 32); m.level = body.level | 0; }
    if (kind === 'event') { m.title = clean(body.title, 80); if (typeof body.icon === 'string') m.icon = clean(body.icon, 40); }
    if (kind === 'obtain') { m.name = clean(body.name, 60); m.count = Math.max(1, body.count | 0); if (typeof body.icon === 'string') m.icon = clean(body.icon, 40); }
    if (!m.text && !m.key && kind !== 'level' && kind !== 'obtain') return null;
    return m;
  }

  /** To one session; returns false when nothing was sent. */
  send(session, kind, body) {
    const m = Notices.build(kind, body);
    if (!m || !session || session.closed) return false;
    session.send(m);
    return true;
  }

  /** To every logged-in driver (filter(session) may exclude some); returns the count. */
  broadcast(kind, body, filter) {
    const m = Notices.build(kind, body);
    if (!m) return 0;
    let n = 0;
    for (const s of this.world.sessions.values()) if (!s.closed && (!filter || filter(s))) { s.send(m); n++; }
    return n;
  }

  /** To the drivers of one channel (except one session). */
  toChannel(ch, kind, body, except) {
    const m = Notices.build(kind, body);
    if (!m || !ch) return 0;
    let n = 0;
    for (const s of ch.players.values()) if (s !== except && !s.closed) { s.send(m); n++; }
    return n;
  }

  announce(text, opts = {}) { return this.broadcast('emergency', { text, seconds: opts.seconds, large: opts.large }); }
  scroll(text) { return this.broadcast('scroll', { text }); }

  /** Op 0x168: the other drivers of the channel read "<name> has reached level <n>." */
  levelUp(session, level) {
    if (!session || !session.channel) return 0;
    return this.toChannel(session.channel, 'level', { name: session.name, level }, session);
  }

  /** Op 0x003 kept a server clock; the remake sends UTC ms so the clients' weather/rush clocks agree. */
  sendTime(session) {
    session.send({ t: 'time', now: Date.now() });
    this.timeSent.set(session, this.clock());
  }

  tick() {
    const now = this.clock();
    for (const s of this.world.sessions.values()) {
      if (s.closed || !s.profile) continue;
      const lvl = s.profile.Level | 0;
      const before = this.levels.get(s);
      if (before !== undefined && lvl > before) this.levelUp(s, lvl);
      this.levels.set(s, lvl);
      const sent = this.timeSent.get(s);
      if (sent === undefined || now - sent >= TIME_RESYNC_MS) this.sendTime(s);
    }
    if (this.maint) this.maintenanceTick(now);
  }

  // ---------------- maintenance ----------------
  /** Starts the countdown; at 0 everyone but admins is kicked with "maintenance" (client text 10983) and logins wait. */
  maintenance(minutes, text) {
    const m = Math.max(0, Math.min(24 * 60, Number(minutes) || 0));
    this.maint = { at: this.clock() + m * 60000, text: clean(text, 200), warned: new Set() };
    this.log(`maintenance in ${m} min`);
    this.maintenanceTick(this.clock());
    return m;
  }

  /** Ends the countdown or the maintenance; logins are allowed again. */
  maintenanceOff() {
    const was = !!this.maint || this.closedForMaintenance;
    this.maint = null;
    this.closedForMaintenance = false;
    return was;
  }

  maintenanceTick(now) {
    const mt = this.maint;
    const left = mt.at - now;
    if (left <= 0) {
      this.maint = null;
      this.closedForMaintenance = true;
      for (const s of [...this.world.sessions.values()]) if (!s.admin && s.kick) s.kick('maintenance');
      this.log('maintenance: drivers disconnected, logins closed');
      return;
    }
    const minutes = Math.ceil(left / 60000);
    for (const w of MAINT_WARN_MIN) {
      if (minutes <= w && !mt.warned.has(w)) {
        for (const x of MAINT_WARN_MIN) if (x >= minutes) mt.warned.add(x);
        const line = `The server will go down for maintenance in ${minutes} minute${minutes === 1 ? '' : 's'}.` + (mt.text ? ` ${mt.text}` : '');
        this.broadcast('emergency', { text: line });
        this.broadcast('scroll', { text: line });
        break;
      }
    }
  }

  /** server.js login: refused while closed for maintenance (admins may still log in). */
  loginBlocked(acc) { return this.closedForMaintenance && !(acc && acc.admin); }

  // ---------------- admin console / admin chat ("/notice ...") ----------------
  /** Handles the notice commands; returns true when the command was one of them. */
  admin(cmd, rest, out) {
    const arg = rest.join(' ');
    switch ((cmd || '').toLowerCase()) {
      case 'notice': case 'emergency': if (!arg) { out('usage: notice <text>'); return true; } out(`notice sent to ${this.announce(arg)}`); return true;
      case 'bignotice': if (!arg) { out('usage: bignotice <text>'); return true; } out(`notice sent to ${this.announce(arg, { large: true })}`); return true;
      case 'scroll': case 'bcast': if (!arg) { out('usage: scroll <text>'); return true; } out(`scroll sent to ${this.scroll(arg)}`); return true;
      case 'popup': {
        if (!arg) { out('usage: popup <text>  |  popup @<driver> <text>'); return true; }
        if (rest[0].startsWith('@')) {
          const s = this.world.findByName(rest[0].slice(1));
          out(s && this.send(s, 'system', { text: rest.slice(1).join(' ') }) ? 'sent' : 'not online');
        } else out(`popup sent to ${this.broadcast('system', { text: arg })}`);
        return true;
      }
      case 'gm': if (!arg) { out('usage: gm <text>'); return true; } out(`gm line sent to ${this.broadcast('gm', { text: arg })}`); return true;
      case 'event': {
        const [title, text] = arg.split('|').map(x => x.trim());
        if (!title || !text) { out('usage: event <title> | <text>'); return true; }
        out(`event popup sent to ${this.broadcast('event', { title, text })}`);
        return true;
      }
      case 'maint': case 'maintenance': {
        if ((rest[0] || '').toLowerCase() === 'off') { out(this.maintenanceOff() ? 'maintenance cancelled, logins open' : 'no maintenance scheduled'); return true; }
        const n = parseInt(rest[0], 10);
        if (!Number.isFinite(n) || n < 0) { out('usage: maint <minutes> [text]  |  maint off'); return true; }
        out(`maintenance in ${this.maintenance(n, rest.slice(1).join(' '))} min`);
        return true;
      }
      case 'mute': {
        // the original server's chat block notices: 20305 "Chat has been blocked.." / 20306 "Chat block has been removed."
        const s = this.world.findByName(rest[0]);
        if (!s) { out('not online'); return true; }
        s.muted = !s.muted;
        this.send(s, 'system', { key: s.muted ? '20305' : '20306', text: s.muted ? 'Chat has been blocked.' : 'Chat block has been removed.' });
        out(s.muted ? 'muted' : 'unmuted');
        return true;
      }
      case 'help':
        out('notice commands: notice <text>, bignotice <text>, scroll <text>, popup [@driver] <text>, gm <text>, event <title> | <text>, maint <minutes> [text], maint off');
        return false;   // server.js prints its own list too
      default: return false;
    }
  }
}

module.exports = { Notices, KINDS };
