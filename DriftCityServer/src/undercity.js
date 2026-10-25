// Undercity party runs (DriftCity_RE/PARTYPLAY.md, UNDERCITY.md): the server side of the client's party Undercity.
//
// The exe's flow, in our JSON protocol:
//   client 0xfc (UC request, the requester becomes the run leader, UC manager +0x3c)  -> uc.request {city}
//   server 0xfc to every member: the leader sees WAITPLAYER 3530000, the others REQUESTINVITE 3540000 (10770 / 10772,
//          15 s, handler 0x687e70)                                                    -> push party.uc.wait / party.uc.invite
//   client 0xfd accept / 0xfe deny (or the 15 s timeout)                              -> uc.answer {accept}
//   server 0xfd (a member accepted) / 0xfe (cancelled: 10943)                         -> push party.uc.answer / party.uc.cancel
//   server 0xff (enter the stage of the city, the fee taken, instant kind under_start)  -> push party.uc.start
//   server 0xfb (state timing: 1 -> state 2, 4 -> state 5, 7 -> state 8)             -> push party.uc.phase
//   client 0xbbc HUB hit / 0xbc6 barrier or Main HUB hit / red HUB jump (0x79b990)    -> uc.event {kind, slot}
//   server 0xbbd HUB down (per-member counter, PARTYUC), 0xbbe HUB phase over, 0xbc3 Main HUB stage (clock, attempt),
//          0xbc7 barrier down, 0xbc8 Main HUB down, 0xbc9 stage over                  -> push party.uc.event / party.uc.phase
//   client 0xbe / 0xc2 the wave's HUVs (0x718e40), HUV hits (server 0xc0 HuvHit)     -> uc.wave / uc.event {kind:'hit'}
//   the wave's HUVs are party HUVs (BS_PktPartyHuv 0x4f0940 names the owner whose client runs their AI; the run leader
//          asked for them): the run leader sends their states, the others show remote copies -> uc.huvpos {wave, cars}
//          relayed as push party.uc.huvpos {wave, cars, from}
//   server 0xbcc reward, 0x100 boss result                                            -> push party.uc.result
//
// Remake rules (the original server decided them): the party is 2-4 members of one party, all in the requester's city;
// AI partners fill the empty slots up to four (they take HUBs, barriers and HUV health on their own clock, as the offline
// partners do); the run moves the members into a private channel of the city (index 100 + room, the friend list's
// "Undercity" location 10809) so they see each other and nobody else; the shared score, the phase clock and the HUV
// health live here; each member is paid by the `undercity` reward rule of economy.js for their own counts.
'use strict';
const { rewards, addExp, ensureProfile, ucFee } = require('./economy');
const { rewardExtras } = require('./workshop');

const UC_CITIES = { moonpalace: 1, koinonia: 2, cras: 3, oros: 4 };   // UndercityData.Layout.CityIndex (post_1st_ .. post_4rd_)
const ENTRY_FEE = 2000;                                               // default only: the fee is economy.js ucFee(city) (UndercityData.FeeFor)
const UC_MAX = { hubs: 90, jumps: 10, barriers: 15, mainHub: 1, huvs: 40, boss: 1 };   // economy.js UC_MAX
const PARTY_MAX = 4;
const INVITE_MS = 15000;          // REQUESTINVITE +0xa0 = 15.0 s (15 dots)
const AI_NAMES = ['Rio', 'Duke', 'Mika'];   // UndercityStage.PartnerNames
// the client's scripted steps (0x7190c0), ms; scaled by opts.timeScale (tests)
const T = { ready: 19200 + 5600, accept: 60000, countdown: 6000, success: 3000, mainIntro: 5600 + 5000, ending: 6000, failWait: 3000, result: 7000 };
// UndercityData: phase clocks (s, not scaled)
const HUB_SECONDS = 300, MAIN_SECONDS = 180, WAVE_SECONDS = 240, BOSS_SECONDS = 120, BOSS_PER_JUMP = 6, UFO_BONUS = 10, MAIN_ATTEMPTS = 3, BOSS_STAGE = 5;
const MAX_HIT = 45, HIT_GAP_MS = 200;
const WAVE_PAR = 60, WAVE_CUT_MAX = 20;   // UC_HUVTimeReduce (remake rule): a slow HUV group costs half its overrun   // UndercityStage.OnHuvHit clamps a hit to 6..45

/**
 * UCRESULT's badges per row (0x716480 -> 0x684820: the record's bytes +0x4e / +0x4f show PARTYn_120 / PARTYn_SET; 0xbcc's
 * +2 == 1 brings chat 11114 "Undercity 120% item has been applied."). The server decided them (remake rules):
 * boost120 = the driver's EXP drink (items.js buff 'exp') was on for this reward; set = the driver's car runs a full
 * set (all four performance slots hold set parts, items.setRow).
 */
function ucBadges(p) {
  const items = require('./items');
  let boost120 = false, set = false;
  try { boost120 = items.buffBonus(p).exp > 0; } catch (_) { boost120 = false; }
  try {
    const car = (p.Cars || [])[p.CurrentCar | 0];
    const slots = car && Array.isArray(car.PerfSlots) ? car.PerfSlots : [];
    set = slots.length === 4 && slots.every(u => { const part = (p.PerfParts || []).find(x => x && x.Uid === u); return !!(part && items.setRow(part.Item)); });
  } catch (_) { set = false; }
  return { boost120, set };
}

const emptyScore = () => ({ hubs: 0, jumps: 0, barriers: 0, mainHub: 0, huvs: 0, boss: 0, hits: 0 });

class UndercityRooms {
  /** deps: { world, economy, log, clock, rng, timeScale, gamedata } */
  constructor(deps) {
    this.world = deps.world; this.economy = deps.economy; this.log = deps.log || (() => {});
    this.clock = deps.clock || Date.now; this.rng = deps.rng || Math.random; this.gd = deps.gamedata || null;
    this.scale = Number.isFinite(deps.timeScale) && deps.timeScale > 0 ? deps.timeScale : 1;
    this.rooms = new Map();
    this.nextId = 1;
    this.timer = setInterval(() => { try { this.tick(); } catch (err) { this.log(`undercity tick: ${err.stack || err}`); } }, 200);
    this.timer.unref();
  }

  close() { clearInterval(this.timer); for (const r of [...this.rooms.values()]) this.closeRoom(r); }

  ms(name) { return T[name] * this.scale; }

  owns(t) { return typeof t === 'string' && t.startsWith('uc.'); }

  handle(session, m) {
    switch (m.t) {
      case 'uc.request': return this.request(session, m);
      case 'uc.answer': return this.answer(session, m.accept === true);
      case 'uc.phase': return this.phaseReport(session, m);
      case 'uc.event': return this.event(session, m);
      case 'uc.wave': return this.waveReport(session, m);
      case 'uc.huvpos': return this.huvPositions(session, m);
      case 'uc.leave': return this.quit(session);
      case 'uc.info': { const r = this.roomOf(session); return r ? { ok: true, room: this.info(r, session) } : { ok: true, room: null }; }
      default: return { ok: false, error: 'unknown message' };
    }
  }

  roomOf(session) { const r = session.uc ? this.rooms.get(session.uc) : null; if (session.uc && !r) session.uc = 0; return r; }

  // ---------------- request, invite, answers ----------------

  /** UC_NeedMito's Accept at the entrance: the party is asked (UC_Err_PlayTogether / SameCity / MaxPlayerCnt / NeedMito). */
  request(session, m) {
    const city = String(m.city || (session.channel && session.channel.city) || '');
    if (!UC_CITIES[city]) return { ok: false, error: 'There is no Undercity here.', code: 'city' };
    if (!session.channel || session.channel.city !== city) return { ok: false, error: 'You are not in that city.', code: 'city' };
    if (this.roomOf(session)) return { ok: false, error: 'You are already in an Undercity run.', code: 'busy' };
    const party = this.world.liveParty(session);
    if (!party || party.members.size < 2) return { ok: false, error: 'You cannot complete this mission solo.', code: 'PlayTogether' };
    if (party.members.size > PARTY_MAX) return { ok: false, error: 'The portal can transport up to four at a time.', code: 'MaxPlayerCnt' };
    const members = this.partyOrder(party).map(id => this.world.sessions.get(id));
    if (members.some(s => !s || s.closed || !s.channel || s.channel.city !== city)) return { ok: false, error: 'All of your party members are not in this city.', code: 'SameCity' };
    if (members.some(s => s.race)) return { ok: false, error: 'A party member is in the Battle Zone.', code: 'busy' };
    if (members.some(s => this.roomOf(s) || s.partyPatrol)) return { ok: false, error: 'A party member is busy with another mission.', code: 'busy' };
    if (session.profile.Mito < ucFee(city)) return { ok: false, error: "You don't have enough Mito to activate the jamming device.", code: 'NeedMito' };
    const now = this.clock();
    const room = {
      id: this.nextId++, city, cityIndex: UC_CITIES[city], state: 'invite', leader: session.id, partyId: party.id, created: now,
      deadline: now + INVITE_MS + 2000, slots: [], phase: 'invite', phaseAt: now, phaseEnd: 0,
    };
    for (const s of members) {
      room.slots.push({ id: s.id, name: s.name, level: s.profile.Level, ai: false, answer: s === session ? true : null, score: emptyScore(), next: 0, lastHit: 0, home: null, gone: false, paid: false });
      s.uc = room.id;
    }
    this.rooms.set(room.id, room);
    for (const s of members) {
      if (s === session) s.send({ t: 'party.uc.wait', room: room.id, city, members: room.slots.map(x => x.name), seconds: INVITE_MS / 1000 });   // WAITPLAYER
      else s.send({ t: 'party.uc.invite', room: room.id, from: session.name, city, fee: ucFee(city), seconds: INVITE_MS / 1000 });   // REQUESTINVITE (10770 / 10772)
    }
    this.log(`undercity ${room.id}: ${session.name} asks ${members.length - 1} member(s) in ${city}`);
    // 0x50e1d0: a member in Rush Time (waiting in the zone or running) refuses at once (0xfe) with 11146 "You have cancelled
    // %s's Undercity invite because you are currently participating in Rush Time."
    const busy = this.inRush ? members.find(s => s !== session && this.inRush(s)) : null;
    if (busy) {
      busy.send({ t: 'party.uc.autorefuse', room: room.id, from: session.name, text: '11146' });
      this.answer(busy, false);
    }
    return { ok: true, room: room.id, fee: ucFee(city) };
  }

  partyOrder(party) { return [party.leader, ...[...party.members].filter(id => id !== party.leader)]; }

  /** REQUESTINVITE ACCEPT (0xfd) / DENY or the timeout (0xfe). */
  answer(session, accept) {
    const room = this.roomOf(session);
    if (!room || room.state !== 'invite') return { ok: false, error: 'no Undercity invitation' };
    const slot = room.slots.find(x => x.id === session.id);
    if (!slot || slot.answer !== null) return { ok: false, error: 'already answered' };
    if (accept && session.profile.Mito < ucFee(room.city)) {
      this.cancel(room, `${session.name} doesn't have enough Mito for the portal.`);
      return { ok: false, error: "You don't have enough Mito to activate the jamming device.", code: 'NeedMito' };
    }
    slot.answer = accept;
    this.push(room, { t: 'party.uc.answer', room: room.id, name: session.name, accept });
    if (!accept) { this.cancel(room, 'Registration canceled due to party being unable to accept or rejecting the invitation.'); return { ok: true }; }   // 10943
    if (room.slots.every(x => x.answer === true)) this.start(room);
    return { ok: true };
  }

  cancel(room, text) {
    this.push(room, { t: 'party.uc.cancel', room: room.id, text });
    this.closeRoom(room);
  }

  // ---------------- the run ----------------

  /** 0xff: everyone enters the stage of the city; AI partners fill the empty slots. */
  start(room) {
    const leader = room.slots[0] && this.world.sessions.get(room.leader);
    const lv = leader && leader.profile ? leader.profile.Level : 1;
    let ai = 0;
    while (room.slots.length < PARTY_MAX) {
      room.slots.push({ id: 0, name: AI_NAMES[ai], level: Math.max(1, lv + ai - 1), ai: true, answer: true, score: emptyScore(), next: 0, gone: false, paid: true });
      ai++;
    }
    room.state = 'run';
    room.started = this.clock();
    room.channelIndex = 100 + room.id;
    const ch = this.world.channelFor(room.city, room.channelIndex);
    room.channel = ch;
    room.hubSlots = null; room.hubTaken = new Set(); room.attempt = 0; room.ufo = 0; room.wave = 0; room.huvs = null; room.mainDown = false;
    room.barrierTotal = 5 * (room.slots.length - 1); room.barriersDown = new Set();
    for (const slot of this.humans(room)) {
      const s = this.world.sessions.get(slot.id);
      slot.home = s.channel ? s.channel.id : null;
      this.world.join(s, room.city, ch.id);
    }
    room.slots.forEach((slot, i) => {
      if (slot.ai) return;
      const s = this.world.sessions.get(slot.id);
      s.send({ t: 'party.uc.start', room: room.id, city: room.city, cityIndex: room.cityIndex, slot: i, leader: room.slots[0].name, fee: ucFee(room.city),
               members: room.slots.map((x, k) => ({ name: x.name, level: x.level, ai: x.ai, slot: k })), channel: ch.id });
    });
    this.log(`undercity ${room.id}: started in ${ch.id} with ${this.humans(room).length} driver(s)`);
    this.setPhase(room, 'ready', this.ms('ready'));
  }

  humans(room) { return room.slots.filter(x => !x.ai && !x.gone); }

  /** Moves the run to a phase; `wait` ms until it ends on its own (0 = only by events). */
  setPhase(room, phase, wait, extra = {}) {
    const now = this.clock();
    room.phase = phase; room.phaseAt = now; room.phaseEnd = wait > 0 ? now + wait : 0;
    for (const s of room.slots) if (s.ai) s.next = now + this.aiDelay(room);
    this.push(room, { t: 'party.uc.phase', room: room.id, phase, clock: this.clockOf(room), attempt: room.attempt, wave: room.wave, barriers: room.barrierTotal,
                      hubTotal: room.hubSlots ? room.hubSlots.length : 0, hubsDown: room.hubTaken.size, ...extra });
  }

  /** Seconds left on the phase's jamming clock (the HUD's), or 0. */
  clockOf(room) {
    const now = this.clock();
    if (room.clockEnd && ['hubs', 'main', 'wave', 'boss'].includes(room.phase)) return Math.max(0, Math.round((room.clockEnd - now) / 100) / 10);
    return 0;
  }

  aiDelay(room) {
    const r = (lo, hi) => (lo + this.rng() * (hi - lo)) * 1000;
    return room.phase === 'hubs' ? r(7, 11) : room.phase === 'main' ? r(14, 20) : r(4, 7);
  }

  /** Called every 200 ms (and by tests): deadlines, AI partners, members who left the stage. */
  tick() {
    const now = this.clock();
    for (const room of [...this.rooms.values()]) {
      if (room.state === 'invite') {
        // members who didn't answer in 15 s refused (0x687dd0 on the timeout)
        if (now >= room.deadline) this.cancel(room, 'Registration canceled due to party being unable to accept or rejecting the invitation.');
        else if (room.slots.some(x => { const s = this.world.sessions.get(x.id); return !s || s.closed; })) this.cancel(room, 'A party member left.');
        continue;
      }
      if (room.state === 'closing') { if (now >= room.closeAt) this.closeRoom(room); continue; }
      // a member who disconnected or drove out of the stage's channel has left the run
      for (const slot of this.humans(room)) {
        const s = this.world.sessions.get(slot.id);
        if (!s || s.closed || s.channel !== room.channel) this.dropMember(room, slot, false);
      }
      if (!this.rooms.has(room.id)) continue;
      if (this.humans(room).length === 0) { this.closeRoom(room); continue; }
      this.stepAi(room, now);
      this.advance(room, now);
    }
  }

  advance(room, now) {
    const due = room.phaseEnd && now >= room.phaseEnd;
    switch (room.phase) {
      case 'ready': if (due) this.setPhase(room, 'accept', this.ms('accept')); break;   // state 4: UC_HUB_NeedAccept, 60 s
      case 'accept': if (due) this.beginCountdown(room); break;
      case 'countdown': if (due) { room.clockEnd = now + HUB_SECONDS * 1000; this.setPhase(room, 'hubs', 0, { slots: room.hubSlots || [] }); } break;
      case 'hubs':
        if (room.hubSlots && room.hubSlots.length && room.hubTaken.size >= room.hubSlots.length) this.setPhase(room, 'hubsCleared', this.ms('success'));   // 0xbbe -> UC_HUBSuccess1
        else if (now >= room.clockEnd) this.beginMain(room);
        break;
      case 'hubsCleared': if (due) this.beginMain(room); break;
      case 'main':
        if (room.mainDown) this.setPhase(room, 'mainCleared', this.ms('success'));   // 0xbc8 -> UC_HUBSuccess2
        else if (now >= room.clockEnd) { if (room.attempt < MAIN_ATTEMPTS) this.beginMain(room); else this.fail(room); }
        break;
      case 'mainCleared': if (due) { room.wave = 0; room.waveClockEnd = now + WAVE_SECONDS * 1000; this.nextWave(room); } break;
      case 'wave': case 'boss': {
        if (room.huvs && room.huvs.length && room.huvs.every(h => h <= 0)) { this.nextWave(room); break; }
        // nobody reported the wave's HUVs (no HUV could be spawned): an empty wave
        if (!room.huvs && now - room.phaseAt > 10000) { this.nextWave(room); break; }
        if (now >= room.clockEnd) this.fail(room);
        break;
      }
      case 'ending': if (due) this.finish(room, true); break;
      case 'failed': if (due) this.finish(room, false); break;
    }
  }

  beginCountdown(room) {
    // state 5: SID_PVP_RACE_START + pvp_countdownstart, 6 s; the HUB phase's clock starts after it
    this.setPhase(room, 'countdown', this.ms('countdown'), { slots: room.hubSlots || [] });
  }

  /** 0xbc3: a Main HUB attempt (clock, attempt); barriers for the party size; the clock runs after the briefing. */
  beginMain(room) {
    room.attempt++;
    room.barriersDown = new Set();
    room.mainDown = false;
    room.ufoTaken = new Set();
    room.clockEnd = this.clock() + this.ms('mainIntro') + MAIN_SECONDS * 1000;
    this.setPhase(room, 'main', 0);
  }

  nextWave(room) {
    const now = this.clock();
    // UC_HUVTimeReduce (0x4f2a20, packet 0xc3 {clock +2, change +6}: a negative change shows the alert after UC_HUVAgain*):
    // (remake rule) a group that took longer than WAVE_PAR seconds costs half the overrun, at most WAVE_CUT_MAX seconds
    let change = 0;
    if (room.wave >= 1 && room.wave < BOSS_STAGE - 1 && room.waveAt) {
      const took = (now - room.waveAt) / 1000;
      change = -Math.min(WAVE_CUT_MAX, Math.max(0, Math.floor((took - WAVE_PAR) / 2)));
      if (change < 0) room.waveClockEnd = Math.max(now + 1000, room.waveClockEnd + change * 1000);
    }
    room.waveAt = now;
    room.wave++;
    room.huvs = null;
    if (room.wave > BOSS_STAGE) { room.clockEnd = 0; this.setPhase(room, 'ending', this.ms('ending')); return; }
    const boss = room.wave === BOSS_STAGE;
    if (boss) {
      // UC_TIP_JUMP: every red HUB taken adds to the boss clock
      const jumps = room.slots.reduce((n, s) => n + s.score.jumps, 0);
      room.clockEnd = this.clock() + (BOSS_SECONDS + BOSS_PER_JUMP * jumps) * 1000;
    } else room.clockEnd = room.waveClockEnd;
    this.setPhase(room, boss ? 'boss' : 'wave', 0, change < 0 ? { clockChange: change } : {});
  }

  fail(room) {
    room.clockEnd = 0;
    room.failed = true;
    this.setPhase(room, 'failed', this.ms('failWait'));
  }

  // ---------------- reports from the clients ----------------

  /** The run leader's Accept on UC_HUB_NeedAccept (with the HUB slots its stage placed). */
  phaseReport(session, m) {
    const room = this.roomOf(session);
    if (!room || room.state !== 'run') return { ok: false, error: 'not in an Undercity run' };
    if (Array.isArray(m.slots) && !room.hubSlots) {
      // the HUB layout is the same on every client (posts + the lanes of the city): slot numbers of towers and red HUBs
      const slots = [...new Set(m.slots.filter(n => Number.isInteger(n) && n >= 0 && n < 100))].slice(0, 100);
      if (slots.length) room.hubSlots = slots;
    }
    if (m.phase === 'accept') {
      if (room.slots[0].id !== session.id && !this.leaderGone(room, session)) return { ok: false, error: 'only the run leader starts the mission' };
      if (room.phase !== 'accept' && room.phase !== 'ready') return { ok: false, error: 'not waiting for Accept' };
      this.beginCountdown(room);
    }
    return { ok: true };
  }

  /** True when `session` is the first human left (the run leader quit). */
  leaderGone(room, session) { const h = this.humans(room); return h.length > 0 && h[0].id === session.id; }

  /** HUB / red HUB (jump) / barrier / Main HUB / UFO down, or a hit on an HUV of the wave. */
  event(session, m) {
    const room = this.roomOf(session);
    if (!room || room.state !== 'run') return { ok: false, error: 'not in an Undercity run' };
    const idx = room.slots.findIndex(x => x.id === session.id && !x.ai && !x.gone);
    if (idx < 0) return { ok: false, error: 'not in an Undercity run' };
    return this.apply(room, idx, m.kind, m.slot | 0, Number(m.damage) || 0);
  }

  apply(room, idx, kind, slotNo, damage) {
    const who = room.slots[idx], s = who.score, now = this.clock();
    const ev = { t: 'party.uc.event', room: room.id, kind, by: who.name, who: idx, slot: slotNo };
    switch (kind) {
      case 'hub': case 'jump': {
        if (room.phase !== 'hubs') return { ok: false, error: 'not the HUB phase' };
        if (room.hubSlots && !room.hubSlots.includes(slotNo)) return { ok: false, error: 'no such HUB' };
        if (slotNo < 0 || slotNo >= 100 || room.hubTaken.has(slotNo)) return { ok: false, error: 'already down' };
        // slots 0-9 are the red HUBs (0x716f80), taken from a jump
        const red = slotNo < 10;
        const field = red ? 'jumps' : 'hubs';
        if (s[field] >= UC_MAX[field]) return { ok: false, error: `too many ${field}` };
        room.hubTaken.add(slotNo);
        s[field]++;
        ev.kind = red ? 'jump' : 'hub';
        ev.hubsDown = room.hubTaken.size;
        break;
      }
      case 'barrier': {
        if (room.phase !== 'main' || now < room.phaseAt + this.ms('mainIntro') * 0.5) return { ok: false, error: 'not the Main HUB phase' };
        if (slotNo < 1 || slotNo > room.barrierTotal || room.barriersDown.has(slotNo)) return { ok: false, error: 'already down' };
        if (s.barriers >= UC_MAX.barriers) return { ok: false, error: 'too many barriers' };
        room.barriersDown.add(slotNo);
        s.barriers++;
        ev.barriersDown = room.barriersDown.size;
        break;
      }
      case 'ufo': {
        if (room.phase !== 'main') return { ok: false, error: 'not the Main HUB phase' };
        if (slotNo < 200 || slotNo >= 216 || room.ufoTaken.has(slotNo)) return { ok: false, error: 'already down' };
        room.ufoTaken.add(slotNo);
        room.clockEnd += UFO_BONUS * 1000;   // UC_TimeSave
        room.ufo += UFO_BONUS;
        break;
      }
      case 'main': {
        if (room.phase !== 'main' || room.mainDown) return { ok: false, error: 'not the Main HUB phase' };
        if (room.barriersDown.size < room.barrierTotal) return { ok: false, error: 'the shield is still up' };
        room.mainDown = true;
        s.mainHub = 1;
        break;
      }
      case 'hit': {
        if ((room.phase !== 'wave' && room.phase !== 'boss') || !room.huvs) return { ok: false, error: 'no HUV wave' };
        if (slotNo < 0 || slotNo >= room.huvs.length || room.huvs[slotNo] <= 0) return { ok: false, error: 'no such HUV' };
        if (!who.ai && now - (who.lastHit || 0) < HIT_GAP_MS) return { ok: false, error: 'too fast' };
        who.lastHit = now;
        const dmg = Math.max(0, Math.min(MAX_HIT, damage));
        if (dmg <= 0) return { ok: false, error: 'no damage' };
        room.huvs[slotNo] = Math.max(0, room.huvs[slotNo] - dmg);
        s.hits++;   // 0xc0 HuvHit: member +0x44 += 1 (PARTYUC)
        ev.index = slotNo;
        ev.health = Math.round(room.huvs[slotNo] * 10) / 10;
        if (room.huvs[slotNo] <= 0) {
          const boss = room.huvBoss[slotNo];
          const field = boss ? 'boss' : 'huvs';
          if (s[field] < UC_MAX[field]) s[field]++;
          ev.killed = true; ev.boss = boss;
        }
        break;
      }
      default: return { ok: false, error: 'unknown event' };
    }
    ev.clock = this.clockOf(room);
    ev.counts = room.slots.map(x => x.score.hubs + x.score.jumps + x.score.barriers + x.score.mainHub + x.score.hits);
    this.push(room, ev);
    return { ok: true };
  }

  /** The wave's HUVs as a member's stage spawned them (uchuvparam rows of the city): levels and the boss flag. */
  waveReport(session, m) {
    const room = this.roomOf(session);
    if (!room || room.state !== 'run') return { ok: false, error: 'not in an Undercity run' };
    if ((room.phase !== 'wave' && room.phase !== 'boss') || (m.wave | 0) !== room.wave) return { ok: false, error: 'not this wave' };
    if (room.huvs) return { ok: true, already: true };
    const list = (Array.isArray(m.huvs) ? m.huvs : []).slice(0, 12).filter(h => h && typeof h === 'object');
    // UC_TIP_HUV: the fewer HUBs destroyed, the more HUV health (UndercityStage.SpawnWave's formula)
    const placed = room.hubSlots ? room.hubSlots.length : 100;
    const missed = 1 - Math.min(1, room.hubTaken.size / Math.max(1, placed));
    room.huvBoss = list.map(h => h.boss === true);
    room.huvs = list.map(h => {
      const level = Math.max(1, Math.min(200, h.level | 0));
      return (h.boss === true ? 200 + 2 * level : 30 + level * 0.5) * (1 + 0.5 * missed);
    });
    if (!room.huvs.length) { room.huvs = null; this.nextWave(room); return { ok: true }; }
    this.push(room, { t: 'party.uc.huvs', room: room.id, wave: room.wave, health: room.huvs.map(h => Math.round(h * 10) / 10), boss: room.huvBoss });
    return { ok: true };
  }

  /**
   * The run leader's HUV states (x, y, z, yaw, speed per HUV of the wave, Unity space): the wave's HUVs run on the run
   * leader's client (the party HUV owner, 0x4f0940); the other members' copies follow them. At most 12 a second.
   */
  huvPositions(session, m) {
    const room = this.roomOf(session);
    if (!room || room.state !== 'run') return { ok: false, error: 'not in an Undercity run' };
    const h = this.humans(room);
    if (!h.length || h[0].id !== session.id) return { ok: false, error: 'not the HUV owner' };
    if ((room.phase !== 'wave' && room.phase !== 'boss') || (m.wave | 0) !== room.wave) return { ok: false, error: 'not this wave' };
    const now = this.clock();
    if (now - (room.huvPosAt || 0) >= 1000) { room.huvPosAt = now; room.huvPosSent = 0; }
    if (++room.huvPosSent > 12) return { ok: false, error: 'too fast' };
    const cars = (Array.isArray(m.cars) ? m.cars : []).slice(0, 16).map(c => (Array.isArray(c) && c.length >= 5 && c.slice(0, 5).every(v => Number.isFinite(v))
      ? c.slice(0, 5).map(v => Math.round(v * 100) / 100) : null));
    const msg = { t: 'party.uc.huvpos', room: room.id, wave: room.wave, cars, from: session.name };
    for (const slot of h) if (slot.id !== session.id) this.world.sessions.get(slot.id)?.send(msg);
    return { ok: true };
  }

  stepAi(room, now) {
    if (!['hubs', 'main', 'wave', 'boss'].includes(room.phase)) return;
    room.slots.forEach((slot, i) => {
      if (!slot.ai || now < slot.next) return;
      slot.next = now + this.aiDelay(room);
      if (room.phase === 'hubs') {
        const open = (room.hubSlots || []).filter(n => !room.hubTaken.has(n));
        if (open.length) this.apply(room, i, 'hub', open[Math.floor(this.rng() * open.length)], 0);
      } else if (room.phase === 'main') {
        if (now < room.phaseAt + this.ms('mainIntro')) return;
        for (let b = 1; b <= room.barrierTotal; b++) if (!room.barriersDown.has(b)) { this.apply(room, i, 'barrier', b, 0); break; }
      } else if (room.huvs) {
        const alive = room.huvs.map((h, k) => (h > 0 ? k : -1)).filter(k => k >= 0);
        if (alive.length) this.apply(room, i, 'hit', alive[Math.floor(this.rng() * alive.length)], 10);
      }
    });
  }

  // ---------------- leaving, the result ----------------

  /** PLAYEXIT (confirmed): the member's run fails; the others go on. */
  quit(session) {
    const room = this.roomOf(session);
    if (!room) return { ok: true };
    const slot = room.slots.find(x => x.id === session.id && !x.ai);
    if (room.state === 'invite') { this.cancel(room, `${session.name} left.`); return { ok: true }; }
    if (slot && !slot.gone) this.dropMember(room, slot, true);
    return { ok: true };
  }

  dropMember(room, slot, pay) {
    slot.gone = true;
    const s = this.world.sessions.get(slot.id);
    if (s) s.uc = 0;
    if (pay && s && !slot.paid) {
      const row = this.pay(room, slot, false);
      s.send({ t: 'party.uc.result', room: room.id, cleared: false, quit: true, seconds: this.seconds(room), attempts: Math.max(1, room.attempt), rows: [row], driver: s.profile });
      this.goHome(room, slot);
    }
    this.push(room, { t: 'party.uc.left', room: room.id, name: slot.name });
    // the run leader's duties (Accept, the wave reports) pass to the next driver
    if (this.humans(room).length === 0 && room.state === 'run') this.closeRoom(room);
  }

  seconds(room) { return Math.max(0, Math.round((this.clock() - (room.started || this.clock())) / 100) / 10); }

  /** 0xbcc: each driver's reward by economy.js's undercity rule (own counts, the clear bonus for everyone). */
  pay(room, slot, cleared) {
    const s = this.world.sessions.get(slot.id);
    slot.paid = true;
    const counts = {};
    for (const k of Object.keys(UC_MAX)) counts[k] = Math.min(UC_MAX[k], slot.score[k] | 0);
    const row = { name: slot.name, level: slot.level, ai: slot.ai, score: { ...slot.score }, exp: 0, mito: 0, item: null };
    if (!s || !s.profile) return row;
    const p = ensureProfile(s.profile);
    let r = rewards.undercity(p.Level, counts, cleared, ucFee(room.city));
    if (p.Mito + r.mito < 0) r = { exp: r.exp, mito: -p.Mito };
    const extra = rewardExtras(p, 'undercity', r, { cleared }, Math.random, this.gd);
    r = { exp: r.exp + extra.exp, mito: r.mito + extra.mito };
    p.Mito += r.mito;
    const levels = addExp(p, r.exp);
    p.HuvCaught += counts.huvs + counts.boss; p.BossesCaught += counts.boss;
    s.lastClaim = s.lastClaim || {};
    s.lastClaim.undercity = this.clock() / 1000;   // a solo claim right after a party run is refused (cooldown)
    s.save();
    this.log(`undercity ${room.id}: ${p.Name} +${r.exp} exp +${r.mito} mito${extra.drop ? ` drop ${extra.drop}` : ''}${levels ? ` (level ${p.Level})` : ''}`);
    Object.assign(row, { exp: r.exp, mito: r.mito, item: extra.drop || null, levelUps: levels }, ucBadges(p));
    return row;
  }

  finish(room, cleared) {
    const rows = room.slots.map(slot => (slot.ai || slot.gone ? { name: slot.name, level: slot.level, ai: slot.ai, gone: slot.gone, score: { ...slot.score }, ...this.aiReward(room, slot, cleared) } : this.pay(room, slot, cleared)));
    const seconds = this.seconds(room), attempts = Math.max(1, room.attempt);
    room.slots.forEach((slot, i) => {
      if (slot.ai || slot.gone) return;
      const s = this.world.sessions.get(slot.id);
      if (s) s.send({ t: 'party.uc.result', room: room.id, cleared, seconds, attempts, mainHub: room.mainDown, slot: i, rows, driver: s.profile });
    });
    this.log(`undercity ${room.id}: ${cleared ? 'cleared' : 'failed'} in ${seconds} s`);
    room.state = 'closing';
    room.phase = 'result';
    room.closeAt = this.clock() + this.ms('result');
  }

  /** What an AI partner's row shows (nobody is paid). */
  aiReward(room, slot, cleared) {
    const counts = {};
    for (const k of Object.keys(UC_MAX)) counts[k] = Math.min(UC_MAX[k], slot.score[k] | 0);
    const r = rewards.undercity(slot.level, counts, cleared, ucFee(room.city));
    return { exp: r.exp, mito: Math.max(0, r.mito) };
  }

  goHome(room, slot) {
    const s = this.world.sessions.get(slot.id);
    if (!s || s.closed || !room.channel || s.channel !== room.channel) return;
    this.world.join(s, room.city, slot.home || undefined);
  }

  closeRoom(room) {
    if (!this.rooms.has(room.id)) return;
    this.rooms.delete(room.id);
    for (const slot of room.slots) {
      if (slot.ai) continue;
      const s = this.world.sessions.get(slot.id);
      if (s && s.uc === room.id) s.uc = 0;
      if (room.state !== 'invite') this.goHome(room, slot);
    }
  }

  /** A driver went offline (world remove hook). */
  removed(session) {
    const room = this.roomOf(session);
    if (!room) return;
    if (room.state === 'invite') return this.cancel(room, `${session.name} left.`);
    const slot = room.slots.find(x => x.id === session.id && !x.ai);
    if (slot && !slot.gone) this.dropMember(room, slot, false);
  }

  push(room, msg) {
    for (const slot of room.slots) {
      if (slot.ai || slot.gone) continue;
      this.world.sessions.get(slot.id)?.send(msg);
    }
  }

  info(room, session) {
    return { id: room.id, city: room.city, state: room.state, phase: room.phase, clock: this.clockOf(room), attempt: room.attempt, wave: room.wave,
             slot: room.slots.findIndex(x => x.id === session.id), members: room.slots.map(x => ({ name: x.name, level: x.level, ai: x.ai, gone: x.gone, score: x.score })) };
  }
}

module.exports = { UndercityRooms, UC_CITIES, ENTRY_FEE, INVITE_MS, AI_NAMES, WAVE_PAR, WAVE_CUT_MAX, ucBadges };
