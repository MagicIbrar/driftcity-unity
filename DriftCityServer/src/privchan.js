// Private channels (DriftCity_RE/SERVICES.md section 2). The client's chat input (0x5b6490) knows six commands from
// globaltext 11160-11165: "/cjoin <name>" (0x5bcd00, refused with 11175 while in one), "/csay <text>" (0x5bcd40),
// "/ckick <name>" (0x5bd190), "/cauth <name>" (0x5bd1f0, hand the admin over), "/cwho" (0x5bcd90), "/cleave"
// (0x5bcd20); without a channel each answers 11168 "Not in a private channel.". "-<channel> <text>" and the chat's
// Private mode (6) send to the channel too. The replies: 0x08f joined (11166 self / 11169 others), 0x091 left
// (11167 / 11170), 0xa29 kicked (11174 self / 11173 others), 0xa2b new admin (11177 / 11178), 0xa2d user list (the
// names, then 11171), all in colour 0xFFAAAAFF; a channel name fits 9 characters (the 10-wchar field at +2).
// Remake rules (the original server's own): a channel is made by its first /cjoin (that driver is its admin) and
// disappears with its last member; 30 members at most; the admin's leaving hands the admin to the longest member;
// only the admin kicks and hands over (11176 "You don't have power to use such command."); private channels are
// server-wide (any city, any channel) and end with the session. Switch `privateChat` off: every request -> 10745.
'use strict';

const MAX_MEMBERS = 30;
const NAME_RE = /^[A-Za-z0-9_\-]{1,9}$/;
const fail = (error, key) => (key ? { ok: false, error, key } : { ok: false, error });

class PrivateChannels {
  constructor({ world, switches, log }) {
    this.world = world; this.switches = switches; this.log = log || (() => {});
    this.channels = new Map();   // lower-case name -> { name, admin: sessionId, members: [sessionId] }
    this.of = new Map();         // sessionId -> lower-case channel name
    this.timer = setInterval(() => { try { this.prune(); } catch { /* next round */ } }, 5000);
    if (this.timer.unref) this.timer.unref();
  }

  close() { clearInterval(this.timer); }
  owns(t) { return typeof t === 'string' && t.startsWith('pchan.'); }

  session(id) { const s = this.world.sessions.get(id); return s && !s.closed ? s : null; }
  channelOf(session) { const k = this.of.get(session.id); return k ? this.channels.get(k) || null : null; }
  names(ch) { return ch.members.map(id => this.session(id)).filter(Boolean).map(s => s.name); }

  /** Sends to every live member. */
  push(ch, msg) { for (const id of ch.members) { const s = this.session(id); if (s) s.send(msg); } }

  /** Members whose session closed leave (with the left notice to the others). */
  prune() {
    for (const [id, key] of [...this.of]) {
      const s = this.world.sessions.get(id);
      if (s && !s.closed) continue;
      const ch = this.channels.get(key);
      this.drop(id, ch, s ? s.name : '');
    }
  }

  drop(id, ch, name) {
    this.of.delete(id);
    if (!ch) return;
    ch.members = ch.members.filter(x => x !== id);
    if (!ch.members.length) { this.channels.delete(ch.name.toLowerCase()); return; }
    this.push(ch, { t: 'pchan', kind: 'left', channel: ch.name, name });
    if (ch.admin === id) {
      ch.admin = ch.members[0];
      const a = this.session(ch.admin);
      this.push(ch, { t: 'pchan', kind: 'admin', channel: ch.name, name: a ? a.name : '' });
    }
  }

  join(session, m) {
    const name = typeof m.name === 'string' ? m.name.trim() : '';
    if (this.channelOf(session)) return fail('You cannot be in multiple private channels.', '11175');
    if (!NAME_RE.test(name)) return fail('Enter a channel name of up to 9 letters or digits.');
    const key = name.toLowerCase();
    let ch = this.channels.get(key);
    if (!ch) { ch = { name, admin: session.id, members: [] }; this.channels.set(key, ch); }
    if (ch.members.length >= MAX_MEMBERS) return fail('That private channel is full.');
    ch.members.push(session.id);
    this.of.set(session.id, key);
    this.push(ch, { t: 'pchan', kind: 'joined', channel: ch.name, name: session.name });
    return { ok: true, channel: ch.name, admin: ch.admin === session.id, names: this.names(ch) };
  }

  leave(session) {
    const ch = this.channelOf(session);
    if (!ch) return fail('Not in a private channel.', '11168');
    session.send({ t: 'pchan', kind: 'left', channel: ch.name, name: session.name });
    this.drop(session.id, ch, session.name);
    return { ok: true, channel: ch.name };
  }

  say(session, m) {
    const ch = this.channelOf(session);
    if (!ch) return fail('Not in a private channel.', '11168');
    if (typeof m.channel === 'string' && m.channel.trim() && m.channel.trim().toLowerCase() !== ch.name.toLowerCase()) return fail('Not in a private channel.', '11168');
    const text = typeof m.text === 'string' ? m.text.replace(/[\u0000-\u001f]/g, '').trim().slice(0, 160) : '';
    if (!text) return fail('Nothing to say.');
    if (session.muted) return fail('You are currently blocked from chatting.', '20102');
    const now = Date.now();
    session.pchanTimes = (session.pchanTimes || []).filter(t => now - t < 10000);
    if (session.pchanTimes.length >= 8) return fail('You are sending messages too fast.');
    session.pchanTimes.push(now);
    this.push(ch, { t: 'chat', scope: 'private', from: session.name, text, channel: ch.name });
    return { ok: true, channel: ch.name };
  }

  member(ch, name) {
    const n = typeof name === 'string' ? name.trim().toLowerCase() : '';
    return ch.members.map(id => this.session(id)).find(s => s && s.name.toLowerCase() === n) || null;
  }

  kick(session, m) {
    const ch = this.channelOf(session);
    if (!ch) return fail('Not in a private channel.', '11168');
    if (ch.admin !== session.id) return fail("You don't have power to use such command.", '11176');
    const target = this.member(ch, m.name);
    if (!target) return fail(`${String(m.name || '').slice(0, 16)} is not in the private channel.`);
    if (target === session) return fail("You don't have power to use such command.", '11176');
    this.push(ch, { t: 'pchan', kind: 'kicked', channel: ch.name, name: target.name });
    this.of.delete(target.id);
    ch.members = ch.members.filter(x => x !== target.id);
    return { ok: true, channel: ch.name };
  }

  auth(session, m) {
    const ch = this.channelOf(session);
    if (!ch) return fail('Not in a private channel.', '11168');
    if (ch.admin !== session.id) return fail("You don't have power to use such command.", '11176');
    const target = this.member(ch, m.name);
    if (!target) return fail(`${String(m.name || '').slice(0, 16)} is not in the private channel.`);
    if (target === session) return { ok: true, channel: ch.name };
    ch.admin = target.id;
    this.push(ch, { t: 'pchan', kind: 'admin', channel: ch.name, name: target.name });
    return { ok: true, channel: ch.name };
  }

  who(session) {
    const ch = this.channelOf(session);
    if (!ch) return fail('Not in a private channel.', '11168');
    const a = this.session(ch.admin);
    return { ok: true, channel: ch.name, names: this.names(ch), admin: a ? a.name : '' };
  }

  state(session) {
    const ch = this.channelOf(session);
    return { ok: true, enabled: this.switches.on('privateChat'), channel: ch ? ch.name : '', admin: !!ch && ch.admin === session.id };
  }

  handle(session, m) {
    if (m.t === 'pchan.state') return this.state(session);
    if (!this.switches.on('privateChat')) return fail('This feature is currently unavailable.', '10745');
    switch (m.t) {
      case 'pchan.join': return this.join(session, m);
      case 'pchan.leave': return this.leave(session);
      case 'pchan.say': return this.say(session, m);
      case 'pchan.kick': return this.kick(session, m);
      case 'pchan.auth': return this.auth(session, m);
      case 'pchan.who': return this.who(session);
    }
    return fail(`unknown message ${m.t}`);
  }
}

module.exports = { PrivateChannels, MAX_MEMBERS };
