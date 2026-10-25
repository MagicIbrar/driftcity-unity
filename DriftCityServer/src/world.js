// Shared world: city channels, player state relay with interest management, chat and parties.
'use strict';
const { PART_FIELD } = require('./economy');

const CITIES = ['cras', 'moonpalace', 'koinonia', 'oros'];
const CHANNEL_CAPACITY = 30;
const VIEW_RADIUS = 700;        // metres; players farther apart don't receive each other's updates
const MAX_SPEED_MS = 140;       // ~500 km/h, anything faster is a teleport
const PARTY_SIZE = 4;
// sane bounds for relayed car state
const MAX_XZ = 20000, MIN_Y = -2000, MAX_Y = 5000, MAX_V = 150, MAX_S = 600;
// movement check: speed over a window of at least SPEED_WINDOW_MS; warnings decay one per WARN_DECAY_MS
const SPEED_WINDOW_MS = 500, SAMPLE_KEEP_MS = 2000, WARN_DECAY_MS = 5000, WARN_KICK = 20;
const LOOK_INTERVAL_MS = 1000;  // player.look at most once a second per player
const INVITE_COOLDOWN_MS = 5000; // party.invite to the same player

const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));

class Channel {
  constructor(city, index) {
    this.city = city;
    this.index = index;
    this.id = `${city}-${index}`;
    this.players = new Map(); // sessionId -> session
  }
  get full() { return this.players.size >= CHANNEL_CAPACITY; }
}

class World {
  constructor(log = () => {}) {
    this.log = log;
    this.channels = new Map();   // id -> Channel
    this.sessions = new Map();   // sessionId -> session (logged-in drivers)
    this.parties = new Map();    // partyId -> { id, leader, members:Set<sessionId> }
    this.nextParty = 1;
    for (const c of CITIES) this.channelFor(c, 1);
  }

  channelFor(city, index) {
    const id = `${city}-${index}`;
    if (!this.channels.has(id)) this.channels.set(id, new Channel(city, index));
    return this.channels.get(id);
  }

  /** First channel of the city with room (new channels open on demand). */
  pickChannel(city, preferred) {
    if (preferred) { const ch = this.channels.get(preferred); if (ch && ch.city === city && !ch.full) return ch; }
    for (let i = 1; i < 100; i++) { const ch = this.channelFor(city, i); if (!ch.full) return ch; }
    return null;
  }

  channelList(city) {
    return [...this.channels.values()].filter(c => !city || c.city === city)
      .map(c => ({ id: c.id, city: c.city, index: c.index, players: c.players.size, capacity: CHANNEL_CAPACITY }));
  }

  join(session, city, channelId) {
    if (!CITIES.includes(city)) return { ok: false, error: 'unknown city' };
    const cur = session.channel;
    // re-entering the channel we are in: no leave/join (leaving first could delete the channel under us)
    if (cur && cur.city === city && (!channelId || channelId === cur.id)) {
      session.state = null;
      session.samples = [];
      const others = [...cur.players.values()].filter(s => s !== session).map(s => this.publicInfo(s));
      session.send({ t: 'channel.joined', channel: cur.id, city, players: others });
      return { ok: true, channel: cur.id };
    }
    const ch = this.pickChannel(city, channelId);
    if (!ch) return { ok: false, error: 'all channels full' };
    if (ch === cur) return { ok: true, channel: cur.id };
    this.leaveChannel(session);
    if (!this.channels.has(ch.id)) this.channels.set(ch.id, ch);
    ch.players.set(session.id, session);
    session.channel = ch;
    session.state = null;
    session.samples = [];
    // tell the newcomer who is here, and everyone else about the newcomer
    const others = [...ch.players.values()].filter(s => s !== session).map(s => this.publicInfo(s));
    session.send({ t: 'channel.joined', channel: ch.id, city, players: others });
    this.broadcast(ch, { t: 'player.joined', player: this.publicInfo(session) }, session);
    this.log(`${session.name} joined ${ch.id} (${ch.players.size})`);
    if (session.party) this.refreshParty(session);   // the party list shows each member's city
    return { ok: true, channel: ch.id };
  }

  leaveChannel(session) {
    const ch = session.channel;
    if (!ch) return;
    ch.players.delete(session.id);
    this.broadcast(ch, { t: 'player.left', id: session.id });
    session.channel = null;
    if (ch.players.size === 0 && ch.index > 1) this.channels.delete(ch.id);
  }

  publicInfo(s) {
    const car = s.profile.Cars[s.profile.CurrentCar] || s.profile.Cars[0];
    const parts = car ? Object.fromEntries(Object.entries(PART_FIELD).map(([k, f]) => [k, Number.isInteger(car[f]) ? car[f] : -1])) : null;
    return { id: s.id, name: s.name, level: s.profile.Level, car: car ? car.VehicleIndex : 0, color: car ? car.Color : 0,
             paint: car ? car.PaintItem : -1, grade: car ? car.Grade : 0, parts, party: s.party || 0, crew: s.crewName || "", state: s.state };
  }

  /** A driver's car changed (parts, paint, switched car): others in the channel rebuild it. Coalesced to 1/s. */
  refreshLook(session) {
    if (!session.channel) return;
    const now = Date.now();
    const wait = (session.lastLookAt || 0) + LOOK_INTERVAL_MS - now;
    if (wait <= 0) return this.sendLook(session);
    if (!session.lookTimer) session.lookTimer = setTimeout(() => { session.lookTimer = null; this.sendLook(session); }, wait);
  }

  sendLook(session) {
    const ch = session.channel;
    if (!ch || session.closed || !this.sessions.has(session.id)) return;
    session.lastLookAt = Date.now();
    this.broadcast(ch, { t: 'player.look', player: this.publicInfo(session) }, session);
  }

  /** Car state from a client; clamped to sane bounds, checked for plausibility, then relayed on the next tick. */
  updateState(session, m) {
    const p = m.p;
    if (!Array.isArray(p) || p.length !== 3 || !p.every(Number.isFinite) || !Number.isFinite(m.r)) return;
    const pos = [clamp(p[0], -MAX_XZ, MAX_XZ), clamp(p[1], MIN_Y, MAX_Y), clamp(p[2], -MAX_XZ, MAX_XZ)];
    const r = ((m.r % 360) + 360) % 360;
    const now = Date.now();

    // warnings decay so a legit player with the odd glitch is never kicked
    if (session.warnings > 0) {
      const decay = Math.floor((now - (session.warnAt || now)) / WARN_DECAY_MS);
      if (decay > 0) { session.warnings = Math.max(0, session.warnings - decay); session.warnAt = now; }
    }
    if (!Array.isArray(session.samples)) session.samples = [];
    if (m.teleport) {
      // a respawn/travel jump starts a new window; more than one a second counts as a warning
      if (now - (session.lastTeleport || 0) < 1000) this.warn(session, now);
      session.lastTeleport = now;
      session.samples = [];
    } else {
      // speed measured over the newest sample at least SPEED_WINDOW_MS old (single packets jitter too much)
      let ref = null;
      for (let i = session.samples.length - 1; i >= 0; i--) if (now - session.samples[i].at >= SPEED_WINDOW_MS) { ref = session.samples[i]; break; }
      if (ref) {
        const dt = (now - ref.at) / 1000;
        const dx = pos[0] - ref.p[0], dz = pos[2] - ref.p[2];
        if (Math.sqrt(dx * dx + dz * dz) / dt > MAX_SPEED_MS) {
          this.warn(session, now);
          session.samples = [];   // count one warning per jump, not one per packet after it
        }
      }
    }
    if (session.kicked) return;
    session.samples.push({ p: pos, at: now });
    while (session.samples.length > 1 && now - session.samples[0].at > SAMPLE_KEEP_MS) session.samples.shift();
    if (session.samples.length > 64) session.samples.splice(0, session.samples.length - 64);

    const v = Array.isArray(m.v) && m.v.length === 3 && m.v.every(Number.isFinite) ? m.v.map(x => Math.round(clamp(x, -MAX_V, MAX_V) * 100) / 100) : [0, 0, 0];
    session.state = {
      p: pos.map(x => Math.round(x * 100) / 100), r: Math.round(r * 10) / 10 % 360, v,
      s: Number.isFinite(m.s) ? Math.round(clamp(m.s, 0, MAX_S)) : 0, b: !!m.b, at: now,
    };
    session.dirty = true;
  }

  warn(session, now) {
    if (!session.warnings) session.warnAt = now;
    session.warnings = (session.warnings || 0) + 1;
    if (session.warnings > WARN_KICK) session.kick('movement check failed');
  }

  /** 10 Hz: each player receives the changed states of players within view. */
  tick() {
    for (const ch of this.channels.values()) {
      if (ch.players.size < 2) { for (const s of ch.players.values()) s.dirty = false; continue; }
      const list = [...ch.players.values()];
      for (const viewer of list) {
        if (!viewer.state) continue;
        const updates = [];
        for (const other of list) {
          if (other === viewer || !other.state || !other.dirty) continue;
          const dx = other.state.p[0] - viewer.state.p[0], dz = other.state.p[2] - viewer.state.p[2];
          if (dx * dx + dz * dz > VIEW_RADIUS * VIEW_RADIUS && !(viewer.party && viewer.party === other.party)) continue;
          updates.push([other.id, other.state.p, other.state.r, other.state.v, other.state.s, other.state.b ? 1 : 0]);
        }
        if (updates.length) viewer.send({ t: 's', u: updates });
      }
      for (const s of list) s.dirty = false;
    }
  }

  broadcast(ch, msg, except) {
    for (const s of ch.players.values()) if (s !== except) s.send(msg);
  }

  // ---------------- chat ----------------
  // scopes: channel, whisper (the client's "*Name text", 10474/10475), party ("&" / "/party "), team (crew: "@" /
  // "/crew ", sent to the online members of the speaker's crew by src/teams.js)
  chat(session, m) {
    let text = typeof m.text === 'string' ? m.text.replace(/[\u0000-\u001f]/g, '').trim().slice(0, 160) : '';
    if (!text) return;
    if (this.names) text = this.names.censor(text);   // 0x5bcf60: slang censored (src/namefilter.js)
    const now = Date.now();
    session.chatTimes = (session.chatTimes || []).filter(t => now - t < 10000);
    if (session.chatTimes.length >= 8) return session.send({ t: 'chat', scope: 'system', from: '', text: 'You are sending messages too fast.' });
    session.chatTimes.push(now);
    if (session.muted) return session.send({ t: 'chat', scope: 'system', from: '', text: 'You are currently blocked from chatting.' });   // server text 20102
    const msg = { t: 'chat', scope: m.scope, from: session.name, text };
    if (m.scope === 'whisper') {
      const target = this.findByName(m.to);
      if (!target) return session.send({ t: 'chat', scope: 'system', from: '', text: `${String(m.to).slice(0, 16)} is not online.` });
      // a whisper to someone who blocked you is dropped silently (the sender still sees the echo)
      if (!(this.social && this.social.blocks(target.accountId, session.accountId))) target.send(msg);
      session.send({ ...msg, to: target.name });
    } else if (m.scope === 'party') {
      const party = this.parties.get(session.party);
      if (!party) return session.send({ t: 'chat', scope: 'system', from: '', text: 'You are not in a party.' });
      for (const id of party.members) this.sessions.get(id)?.send(msg);
    } else if (m.scope === 'team') {
      const sent = this.teams ? this.teams.chat(session, msg) : false;
      if (!sent) return session.send({ t: 'chat', scope: 'system', from: '', text: "You don't have a Crew" });   // 10425
    } else if (session.channel) {
      msg.scope = 'channel';
      this.broadcast(session.channel, msg);
    }
  }

  announce(text) {
    for (const s of this.sessions.values()) s.send({ t: 'chat', scope: 'notice', from: 'Server', text });
  }

  findByName(name) {
    if (typeof name !== 'string') return null;
    const n = name.toLowerCase();
    for (const s of this.sessions.values()) if (!s.closed && s.name.toLowerCase() === n) return s;
    return null;
  }

  /** The live (not closed) session of an account, if its driver is online. */
  findByAccount(accountId) {
    for (const s of this.sessions.values()) if (s.accountId === accountId && !s.closed && s.profile) return s;
    return null;
  }

  // ---------------- parties (DriftCity_RE/SOCIAL.md section 1) ----------------
  // The client's party (0x61c0b0): up to 4 members, the leader is members[0], the inviter names the party when it is
  // created (PARTY 410000, default text 10395 "Party"), a member who leaves hands the lead to the next one, a party of
  // one is disbanded (10831). An invite to a player who is already in a party offers a join request instead (430000).

  /** Why `target` refuses party messages from `from` (block list, refuse option, Battle Zone), or null. */
  partyRefusal(target, from) {
    if (target.refuseParty || (this.social && this.social.blocks(target.accountId, from.accountId)))
      return `${target.name} is set to refuse all requests. Party Request was refused.`;   // 10412
    if (target.race) return `${target.name} is in the Battle Zone. Party Request was refused.`;   // 10413
    return null;
  }

  partyInvite(session, name, opts = {}) {
    const target = this.findByName(name);
    if (!target || target === session) return { ok: false, error: 'player not found' };
    const party = this.liveParty(session);
    if (this.liveParty(target)) {
      if (party && target.party === party.id) return { ok: false, error: 'Already in a party.', code: 'same' };                     // 10748
      // 10416 + 10417: the client offers to ask that party's leader instead
      if (!party) return { ok: false, error: `${target.name} is already a member of another party.`, code: 'inParty', canRequest: true };
      return { ok: false, error: `${target.name} is already a member of another Party.`, code: 'inParty' };                        // 10469
    }
    if (session.race) return { ok: false, error: 'You cannot send an invitation to a party member while being in the Battlezone.' };   // 11111
    const refusal = this.partyRefusal(target, session);
    if (refusal) return { ok: false, error: refusal, code: 'refused' };
    const now = Date.now();
    session.inviteTimes = session.inviteTimes || new Map();
    if (now - (session.inviteTimes.get(target.id) || 0) < INVITE_COOLDOWN_MS) return { ok: false, error: 'you just invited that player' };
    if (party && party.members.size >= PARTY_SIZE) return { ok: false, error: 'party is full' };
    session.inviteTimes.set(target.id, now);
    if (session.inviteTimes.size > 50) for (const [k, t] of session.inviteTimes) if (now - t >= INVITE_COOLDOWN_MS) session.inviteTimes.delete(k);
    // the party name is the inviter's choice only while the party doesn't exist yet (410000 disables the field after)
    const partyName = party ? party.name : cleanText(opts.partyName, 30) || 'Party';
    const message = cleanText(opts.message, 100) || 'Join us!';   // 10396
    target.pendingInvite = { from: session.id, at: now, partyName };
    target.send({ t: 'party.invite', from: session.name, fromId: session.id, party: partyName, message });
    return { ok: true };
  }

  /** Invite answered with No (0xf1): the inviter sees the refusal text (450000). */
  partyDecline(session, message) {
    const inv = session.pendingInvite;
    session.pendingInvite = null;
    if (!inv) return { ok: false, error: 'no pending invite' };
    const from = this.sessions.get(inv.from);
    if (from && !from.closed) from.send({ t: 'party.declined', from: session.name, message: cleanText(message, 100) || `${session.name} has refused your Party invitation.` });   // 10472
    return { ok: true };
  }

  partyAccept(session) {
    const inv = session.pendingInvite;
    session.pendingInvite = null;
    if (!inv || Date.now() - inv.at > 60000) return { ok: false, error: 'no pending invite' };
    if (this.liveParty(session)) return { ok: false, error: 'leave your party first' };
    const leader = this.sessions.get(inv.from);
    if (!leader || leader.closed) return { ok: false, error: 'inviter left' };
    let party = this.liveParty(leader);
    if (!party) {
      party = { id: this.nextParty++, name: inv.partyName || 'Party', leader: leader.id, members: new Set([leader.id]) };
      this.parties.set(party.id, party);
      leader.party = party.id;
    }
    return this.addMember(party, session);
  }

  /** Join request (430000 -> 0xf6): a player outside any party asks the leader of `name`'s party (440000, 10524). */
  partyRequest(session, name) {
    const target = this.findByName(name);
    if (!target || target === session) return { ok: false, error: 'player not found' };
    if (this.liveParty(session)) return { ok: false, error: 'Already in a party.' };
    const party = this.liveParty(target);
    if (!party) return { ok: false, error: `${target.name} is not in a party` };
    if (party.members.size >= PARTY_SIZE) return { ok: false, error: 'party is full' };
    const leader = this.sessions.get(party.leader);
    if (!leader || leader.closed) return { ok: false, error: 'The Party is unavailable.' };   // 10471
    const refusal = this.partyRefusal(leader, session);
    if (refusal) return { ok: false, error: refusal, code: 'refused' };
    leader.joinRequests = leader.joinRequests || new Map();
    leader.joinRequests.set(session.id, Date.now());
    leader.send({ t: 'party.request', from: session.name, fromId: session.id });
    return { ok: true };
  }

  /** The leader answers a join request (0xf7 accept / 0xf1 with 10473-style refusal). */
  partyAnswer(session, name, accept) {
    const asker = this.findByName(name);
    const at = asker && session.joinRequests ? session.joinRequests.get(asker.id) : undefined;
    if (!asker || at === undefined || Date.now() - at > 60000) return { ok: false, error: 'no pending request' };
    session.joinRequests.delete(asker.id);
    if (!accept) { asker.send({ t: 'party.declined', from: session.name, message: `${session.name} party request was declined.` }); return { ok: true }; }   // 10414
    if (this.liveParty(asker)) return { ok: false, error: `${asker.name} is already a member of another party.` };
    let party = this.liveParty(session);
    if (!party) {
      party = { id: this.nextParty++, name: 'Party', leader: session.id, members: new Set([session.id]) };
      this.parties.set(party.id, party);
      session.party = party.id;
    }
    if (party.leader !== session.id) return { ok: false, error: 'Not a group leader' };   // 20022
    return this.addMember(party, asker);
  }

  addMember(party, session) {
    if (party.members.size >= PARTY_SIZE) return { ok: false, error: 'party is full' };
    party.members.add(session.id);
    session.party = party.id;
    for (const id of party.members) { const s = this.sessions.get(id); if (s && s !== session) s.send({ t: 'party.notice', type: 'joined', name: session.name }); }   // 10826
    this.sendParty(party);
    return { ok: true };
  }

  /** MEMBERBAN (0xf5): the leader removes a member (10829 to them, 10830 to the rest). */
  partyKick(session, name) {
    const party = this.liveParty(session);
    if (!party) return { ok: false, error: 'You are not in a party.' };
    if (party.leader !== session.id) return { ok: false, error: 'Not a group leader' };
    const n = String(name || '').toLowerCase();
    const target = [...party.members].map(id => this.sessions.get(id)).find(s => s && s.name.toLowerCase() === n);
    if (!target || target === session) return { ok: false, error: 'player not found' };
    target.send({ t: 'party.notice', type: 'kicked', name: target.name, you: true });
    for (const id of party.members) { const s = this.sessions.get(id); if (s && s !== target) s.send({ t: 'party.notice', type: 'kicked', name: target.name }); }
    this.partyLeave(target, true);
    return { ok: true };
  }

  /** The member's party if it still lists them (a stale reference is cleared). */
  liveParty(session) {
    const party = this.parties.get(session.party);
    if (session.party && (!party || !party.members.has(session.id))) session.party = 0;
    return party && party.members.has(session.id) ? party : null;
  }

  partyLeave(session, kicked = false) {
    const party = this.parties.get(session.party);
    session.party = 0;
    if (!party) return;
    party.members.delete(session.id);
    session.send({ t: 'party.update', party: null });
    if (!kicked) for (const id of party.members) this.sessions.get(id)?.send({ t: 'party.notice', type: 'left', name: session.name });   // 10828
    if (party.members.size <= 1) {
      for (const id of party.members) {
        const s = this.sessions.get(id);
        if (s) { s.party = 0; s.send({ t: 'party.notice', type: 'disbanded' }); s.send({ t: 'party.update', party: null }); }   // 10831
      }
      this.parties.delete(party.id);
    } else {
      if (party.leader === session.id) party.leader = [...party.members][0];
      this.sendParty(party);
    }
  }

  /** 0xf3: the whole party, the leader first (the client treats members[0] as the leader). */
  partyInfo(party) {
    const ids = [party.leader, ...[...party.members].filter(id => id !== party.leader)];
    const members = ids.map(id => this.sessions.get(id)).filter(Boolean).map(s => {
      const car = s.profile.Cars && (s.profile.Cars[s.profile.CurrentCar] || s.profile.Cars[0]);
      return { id: s.id, name: s.name, level: s.profile.Level, grade: car ? (car.Grade | 0) + 1 : 1, avatar: s.profile.Avatar | 0,
               city: s.channel ? s.channel.city : null, channel: s.channel ? s.channel.index : 0, leader: s.id === party.leader };
    });
    // combo: the party combo total (src/partyplay.js, 0x23c / 0x242)
    return { id: party.id, name: party.name || 'Party', leader: party.leader, members, combo: party.comboTotal | 0 };
  }

  sendParty(party) {
    const info = this.partyInfo(party);
    for (const id of party.members) this.sessions.get(id)?.send({ t: 'party.update', party: info });
  }

  /** Re-sends the party of a member whose level, car or city changed. */
  refreshParty(session) { const party = this.liveParty(session); if (party) this.sendParty(party); }

  // ---------------- sessions ----------------
  // listeners (src/social.js, src/teams.js, src/mail.js): called when a driver comes online / goes offline
  onAdd(fn) { (this.addHooks = this.addHooks || []).push(fn); }
  onRemove(fn) { (this.removeHooks = this.removeHooks || []).push(fn); }

  add(session) {
    this.sessions.set(session.id, session);
    for (const fn of this.addHooks || []) { try { fn(session); } catch (err) { this.log(`add hook: ${err.message}`); } }
  }

  remove(session) {
    clearTimeout(session.lookTimer); session.lookTimer = null;
    this.leaveChannel(session);
    this.partyLeave(session);
    this.sessions.delete(session.id);
    for (const fn of this.removeHooks || []) { try { fn(session); } catch (err) { this.log(`remove hook: ${err.message}`); } }
  }

  online() { return [...this.sessions.values()].filter(s => !s.closed).map(s => ({ name: s.name, level: s.profile.Level, channel: s.channel ? s.channel.id : null })); }
}

/** Free text from a client: control characters removed, trimmed, cut to n characters. */
function cleanText(v, n) { return typeof v === 'string' ? v.replace(/[\u0000-\u001f]/g, '').trim().slice(0, n) : ''; }

module.exports = { World, CITIES, CHANNEL_CAPACITY, MAX_SPEED_MS, PARTY_SIZE, cleanText };
