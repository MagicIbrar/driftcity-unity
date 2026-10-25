// Drift City Offline multiplayer server.
// Its own JSON-over-WebSocket protocol for the Unity rebuild (it does not speak the original client protocol).
'use strict';
const http = require('node:http');
const path = require('node:path');
const readline = require('node:readline');
const crypto = require('node:crypto');
const { WebSocketServer } = require('ws');
const { Store } = require('./src/db');
const auth = require('./src/auth');
const { Economy, newProfile, ensureProfile } = require('./src/economy');
const { World, CITIES } = require('./src/world');
const { Races } = require('./src/race');
const { LapTime } = require('./src/laptime');
const { Tours } = require('./src/pvptour');
const { SmallScreens } = require('./src/smallscreens');
const { loadTracks, loadRewardGroups } = require('./src/pvptracks');
const { Workshop } = require('./src/workshop');
const { Trades } = require('./src/trade');
const { Social } = require('./src/social');
const { Events } = require('./src/events');
const { PartyPlay } = require('./src/partyplay');
const { loadGameData } = require('./src/gamedata');
const { Auction } = require('./src/auction');
const { VShop } = require('./src/vshop');
const { Quests } = require('./src/quests');
const { Notices } = require('./src/notices');
const { Fuel } = require('./src/fuel');                 // Mittron fuel: filling station / PvP charger (src/fuel.js)
const { DriverInfo } = require('./src/driverinfo');     // another driver's USERINFO2ND card (src/driverinfo.js)
const { CarParts } = require('./src/carparts');         // parts page row 2, option slots, custom marks (src/carparts.js)
const { Services } = require('./src/services');
const { NameFilter, TEXTS: NAME_TEXTS } = require('./src/namefilter');   // slang / operatormisrepresent word filter (src/namefilter.js)

const PROTOCOL = 1;
const config = {
  port: Number(process.env.DC_PORT || 7860),
  db: process.env.DC_DB || path.join(__dirname, 'data', 'driftcity.db'),
  name: process.env.DC_NAME || 'Drift City Offline',
  motd: process.env.DC_MOTD || 'Welcome to Drift City Offline multiplayer!',
  maxConnPerIp: Number(process.env.DC_MAX_CONN_PER_IP || 8),   // simultaneous sockets per IP (raise it to run many bots locally)
  loginTimeoutMs: 15000,       // sockets that haven't logged in by then are closed
  maxLoginFailsPerSocket: 5,   // then the socket is closed
  ipFailsBeforeBackoff: 5,     // failed logins per IP before the backoff starts (doubling, up to 5 min)
  floodLimit: 60,              // messages per second, sustained
  floodBurst: 240,             // messages allowed at once
};

/**
 * opts: port, db, name, motd, quiet, rng (workshop dice), clock (ms; races and reward cooldowns),
 * gamedata (a loaded GameData, or null to force the built-in rules), gamedataDir, raceCountdownMs, raceMinTrackLength,
 * maxConnPerIp, loginTimeoutMs, maxLoginFailsPerSocket, ipFailsBeforeBackoff, golden (golden hours "20-22" / "off", src/multipliers.js).
 */
function createServer(opts = {}) {
  const cfg = { ...config, ...opts };
  const log = cfg.quiet ? () => {} : (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
  const gd = opts.gamedata !== undefined ? opts.gamedata : loadGameData(opts.gamedataDir || undefined, (...a) => (cfg.quiet ? null : console.warn(...a)));
  if (gd) log(`game data: ${gd.carList.length} cars, ${gd.parts.size} parts, ${gd.quests.length} quests, ${gd.recipes.size} recipes, ${gd.licences.size} licences`);
  const clock = opts.clock || Date.now;
  const store = new Store(cfg.db);
  const economy = new Economy(log, gd, clock);
  const world = new World(log);
  // the client's word filter on driver names, crew names, room titles and chat: on, except under the test runners (node --test,
  // or env DC_NAMEFILTER=0, which the Unity PlayMode suite sets for the servers it starts); opts.nameFilter decides for a test
  const filterOn = opts.nameFilter !== undefined ? !!opts.nameFilter
    : process.env.DC_NAMEFILTER !== undefined ? process.env.DC_NAMEFILTER !== '0' : !process.env.NODE_TEST_CONTEXT;
  const names = filterOn ? new NameFilter(gd ? gd.dir : undefined) : null;
  world.names = names;
  const notices = new Notices({ world, log });   // server notices, level-up broadcast, clock, maintenance (src/notices.js)
  // Battle Zone: the pvptrackinfo gates (opts.raceGates = false keeps the old lap checks), lap-time battle (src/laptime.js)
  const pvpTracks = gd && opts.raceGates !== false ? loadTracks(gd.dir) : new Map();
  const races = new Races(world, economy, store, log, { clock, countdownMs: opts.raceCountdownMs, minTrackLength: opts.raceMinTrackLength, hostKickMs: opts.raceHostKickMs, maxAvgSpeed: opts.raceMaxSpeed, tracks: pvpTracks, gamedata: gd, aiSpeed: opts.raceAiSpeed, rmAlways: opts.raceRmAlways, pickMs: opts.racePickMs, rewardGroups: gd ? loadRewardGroups(gd.dir) : null });
  const laps = new LapTime(world, store, log, { clock, tracks: gd ? loadTracks(gd.dir) : new Map(), roundMs: opts.lapRoundMs, startMs: opts.lapStartMs, maxAvgSpeed: opts.raceMaxSpeed });
  races.laps = laps;
  const tours = new Tours(races, world, log, { clock, signupMs: opts.tourSignupMs, heatWaitMs: opts.tourHeatWaitMs, rewardGroups: gd ? loadRewardGroups(gd.dir) : null });   // GT Championship online (src/pvptour.js)
  const smallScreens = new SmallScreens(world, log, { clock, dataDir: gd ? gd.dir : null, autofinder: opts.autofinder, autofinderIntervalMs: opts.autofinderIntervalMs, autofinderAnswerMs: opts.autofinderAnswerMs, autofinderTickMs: opts.autofinderTickMs });   // AEROSET, AUTOFINDER (src/smallscreens.js)
  const workshop = new Workshop(opts.rng, gd);
  const trades = new Trades(world, log, { clock });
  const social = new Social({ store, world, log, clock, races });   // friends, notes, crews (src/social.js, mail.js, teams.js)
  const partyplay = new PartyPlay({ world, economy, log, clock, gamedata: gd, rng: opts.ucRng, ucTimeScale: opts.ucTimeScale });   // Undercity party runs, party patrol, party bonus (src/partyplay.js)
  // events, gacha, lotto, coupons (src/events.js); opts.events = season list for tests (else env DC_EVENTS / by date)
  // the Auction House and the custom-shop extras (gifts, wish lists, mileage): src/auction.js, src/vshop.js
  social.mail.gd = gd;
  const auction = new Auction({ store, world, mail: social.mail, log, clock, gamedata: gd, npc: opts.auctionNpc });
  const vshop = new VShop({ store, world, social, log, clock, gamedata: gd, giftLevel: opts.giftLevel });
  const quests = new Quests({ store, log, clock, gamedata: gd });   // step quests and the delivery rankings (src/quests.js)
  const fuel = new Fuel(), driverInfo = new DriverInfo({ store, world, social });
  const carParts = new CarParts({ store, world });
  if (gd && gd.dir) require('./src/carparts').setDataDir(gd.dir);
  const events = new Events({ store, log, clock, gamedata: gd, rng: opts.eventRng, seasons: opts.events, dir: opts.eventsDir || (gd && gd.dir) || undefined, codes: opts.couponCodes });
  // server notices (src/notices.js) for the Christmas tree / channel tournament lines; mail, races and crews for the
  // egg-basket gifts, Pick'em and the channel-battle stakes (src/events.js, src/teams.js)
  events.notices = notices; social.teams.notices = notices;
  events.social = social; events.races = races; social.teams.events = events;
  quests.events = events;   // the event step quests (kind 99) run with their season (src/quests.js)
  const services = new Services({ store, world, social, vshop, events, gamedata: gd, clock, log, switches: opts.switches });   // DriftCity_RE/SERVICES.md
  social.teams.setSchedule(opts.cbSchedule);   // channel battle windows (env DC_CB_SCHEDULE, src/channelbattle.js)
  // chase and race EXP/Mito: Christmas tree badge; race EXP: the scratch-card Battle EXP goals
  let closed = false;
  require('./src/workshop').setEventBonus(kind => {
    if (closed) return { exp: 0, mito: 0 };
    const xmas = kind === 'chase' || kind === 'race' ? events.xmasBonusPct() : 0;
    return { exp: xmas + (kind === 'race' ? events.battleExpBonusPct() : 0), mito: xmas };
  });
  // golden time, Santa and the 3rd anniversary (src/multipliers.js) with the event seasons
  // (off under the test runner unless a test asks for it: opts.multipliers / opts.golden, so reward checks don't depend on the hour)
  const calendar = opts.multipliers !== undefined ? !!opts.multipliers : (opts.golden !== undefined || !process.env.NODE_TEST_CONTEXT);
  const goldenHours = require('./src/multipliers').parseHours(opts.golden !== undefined ? opts.golden : process.env.DC_GOLDEN);
  events.goldenHours = calendar ? goldenHours : null;
  events.calendar = calendar;
  require('./src/workshop').setMultiplierBonus(kind => (closed || !calendar ? null : require('./src/multipliers').bonus(kind, events.seasons(), new Date(clock()), goldenHours)));

  const connsByIp = new Map();     // ip -> open sockets
  const ipFails = new Map();       // ip -> { fails, last, blockedUntil }
  const accounts = new Map();      // accountId -> the session logged in to it (with or without a driver)

  const httpServer = http.createServer((req, res) => {
    // tiny status endpoint for server lists / health checks
    if (req.url === '/status') {
      res.writeHead(200, { 'content-type': 'application/json' });
      return res.end(JSON.stringify({ name: cfg.name, protocol: PROTOCOL, online: world.sessions.size, channels: world.channelList() }));
    }
    res.writeHead(404); res.end();
  });
  const wss = new WebSocketServer({ server: httpServer, maxPayload: 64 * 1024 });

  wss.on('connection', (ws, req) => {
    const ip = req.socket.remoteAddress || '?';
    const open = connsByIp.get(ip) || 0;
    if (open >= cfg.maxConnPerIp) {
      try { ws.send(JSON.stringify({ t: 'kicked', reason: 'too many connections from your address' })); } catch { /* closing anyway */ }
      ws.close(4001, 'too many connections');
      setTimeout(() => ws.terminate(), 1000).unref();
      return;
    }
    connsByIp.set(ip, open + 1);

    const session = {
      id: crypto.randomInt(1, 2 ** 31),
      ws, ip, accountId: 0, username: null, name: null, profile: null,
      channel: null, party: 0, race: null, state: null, dirty: false, lastSeen: Date.now(), floodTokens: cfg.floodBurst, floodAt: Date.now(),
      closed: false, kicked: false, loggingIn: false, loginFails: 0,
      send(msg) { if (ws.readyState === ws.OPEN) ws.send(JSON.stringify(msg)); },
      /** Never writes for a closed session (a newer login or the close handler already saved it). */
      save() {
        if (session.closed || !session.accountId || !session.profile) return false;
        try { store.saveDriver(session.accountId, session.profile); return true; }
        catch (err) { log(`could not save ${session.name}: ${err.message}`); return false; }
      },
      kick(reason, hard = false) {
        if (session.kicked) return;
        session.kicked = true;
        session.send({ t: 'kicked', reason });
        log(`kicked ${session.name || session.username || session.ip}: ${reason}`);
        if (hard) ws.terminate();
        else { ws.close(4000, String(reason).slice(0, 100)); setTimeout(() => ws.terminate(), 1000).unref(); }
      },
    };
    const loginTimer = setTimeout(() => { if (!session.accountId) session.kick('login timed out'); }, cfg.loginTimeoutMs);
    loginTimer.unref();
    session.send({ t: 'welcome', protocol: PROTOCOL, server: cfg.name, motd: cfg.motd, cities: CITIES });

    ws.on('message', raw => {
      if (session.kicked || session.closed) return;
      session.lastSeen = Date.now();
      // flood guard: a token bucket refilled at floodLimit per second. The burst allowance absorbs the backlog a lag
      // spike delivers at once (the client sends about 10 messages a second)
      const now = Date.now();
      session.floodTokens = Math.min(cfg.floodBurst, session.floodTokens + (now - session.floodAt) * cfg.floodLimit / 1000);
      session.floodAt = now;
      if (--session.floodTokens < 0) return session.kick('flooding', true);
      let m;
      try { m = JSON.parse(raw); } catch { return; }
      if (!m || typeof m !== 'object' || typeof m.t !== 'string') return;
      const failed = err => {
        log('handler error', m.t, err && err.stack || err);
        if (m.rid !== undefined) reply(session, m, { ok: false, error: 'server error' });
        else session.send({ t: 'error', for: m.t, error: 'server error' });
      };
      try { const r = handle(session, m); if (r && typeof r.catch === 'function') r.catch(failed); }
      catch (err) { failed(err); }
    });
    ws.on('close', () => {
      clearTimeout(loginTimer);
      const n = (connsByIp.get(ip) || 1) - 1;
      if (n > 0) connsByIp.set(ip, n); else connsByIp.delete(ip);
      if (accounts.get(session.accountId) === session) accounts.delete(session.accountId);
      try {
        if (session.name) {
          races.leave(session);
          trades.leave(session);
          session.save();
          session.closed = true;
          world.remove(session);
          log(`${session.name} disconnected`);
        }
      } catch (err) { log(`close handler for ${session.name || session.ip}: ${err.message}`); }
      session.closed = true;
    });
  });

  const reply = (session, m, result) => session.send({ t: 'reply', rid: m.rid, for: m.t, ...result });
  const requireDriver = (session, m) => { if (!session.profile) { reply(session, m, { ok: false, error: 'not logged in' }); return false; } return true; };

  /** Per-IP failed-login backoff (doubling after a few failures, max 5 minutes, forgotten after 15 quiet minutes). */
  function ipBlocked(ip) {
    const e = ipFails.get(ip);
    if (!e) return 0;
    const now = Date.now();
    if (now - e.last > 15 * 60000) { ipFails.delete(ip); return 0; }
    return e.blockedUntil > now ? e.blockedUntil - now : 0;
  }
  function loginFailed(session) {
    const now = Date.now();
    const e = ipFails.get(session.ip) || { fails: 0, last: 0, blockedUntil: 0 };
    e.fails++; e.last = now;
    if (e.fails >= cfg.ipFailsBeforeBackoff) e.blockedUntil = now + Math.min(300000, 1000 * 2 ** (e.fails - cfg.ipFailsBeforeBackoff));
    ipFails.set(session.ip, e);
    if (++session.loginFails >= cfg.maxLoginFailsPerSocket) setImmediate(() => session.kick('too many failed logins'));
  }

  async function login(session, m, create) {
    if (session.accountId) return reply(session, m, { ok: false, error: 'already logged in' });
    if (session.loggingIn) return reply(session, m, { ok: false, error: 'login already in progress' });
    if (m.protocol !== PROTOCOL) return reply(session, m, { ok: false, error: `client protocol ${m.protocol} does not match server ${PROTOCOL}` });
    if (!auth.validUsername(m.user)) return reply(session, m, { ok: false, error: 'username must be 3-16 letters, digits or _' });
    if (!auth.validPassword(m.pass)) return reply(session, m, { ok: false, error: 'password must be 6-64 characters' });
    const wait = ipBlocked(session.ip);
    if (wait) return reply(session, m, { ok: false, error: `too many failed logins, try again in ${Math.ceil(wait / 1000)} s` });
    session.loggingIn = true;
    try {
      let acc = store.account(m.user);
      if (create) {
        if (acc) return reply(session, m, { ok: false, error: 'that username is taken' });
        const { salt, hash } = await auth.hashPassword(m.pass);
        if (session.closed) return;
        try { store.createAccount(m.user, salt, hash); }
        catch { return reply(session, m, { ok: false, error: 'that username is taken' }); }
        acc = store.account(m.user);
        log(`account created: ${m.user}`);
      } else {
        const good = !!acc && await auth.verifyPassword(m.pass, acc.salt, acc.hash);
        if (session.closed) return;
        if (!good) { loginFailed(session); return reply(session, m, { ok: false, error: 'wrong username or password' }); }
        acc = store.account(m.user) || acc;
      }
      if (acc.banned) return reply(session, m, { ok: false, error: 'this account is banned' });
      if (notices.loginBlocked(acc)) return reply(session, m, { ok: false, error: 'Server is currently undergoing maintenance.', key: '10983' });
      ipFails.delete(session.ip);
      // one socket per account, with or without a driver: the older one is saved first, then kicked
      const older = accounts.get(acc.id);
      const others = new Set([older, ...[...world.sessions.values()].filter(s => s.accountId === acc.id)]);
      for (const s of others) {
        if (!s || s === session || s.closed) continue;
        try { races.leave(s); } catch (err) { log(`race leave: ${err.message}`); }
        trades.leave(s);
        s.save();
        s.closed = true;
        if (s.name) world.remove(s);   // gone from the world now, not when its socket finally closes
        s.kick('logged in from another place');
      }
      session.accountId = acc.id;
      session.username = acc.username;
      session.admin = !!acc.admin;
      accounts.set(acc.id, session);
      const driver = store.driver(acc.id);
      if (driver) attachDriver(session, driver.name, driver.profile);
      reply(session, m, { ok: true, user: acc.username, driver: driver ? driver.profile : null });
    } finally {
      session.loggingIn = false;
    }
  }

  function attachDriver(session, name, profile) {
    session.name = name;
    if (profile && Array.isArray(profile.Cars)) ensureProfile(profile);
    session.profile = profile;
    world.add(session);
    log(`${name} logged in (level ${profile.Level})`);
  }

  function createDriver(session, m) {
    if (!session.accountId) return reply(session, m, { ok: false, error: 'not logged in' });
    if (session.profile) return reply(session, m, { ok: false, error: 'you already have a driver' });
    if (!auth.validDriverName(m.name)) return reply(session, m, { ok: false, error: 'driver name must be 2-16 letters, digits, _ or -' });
    if (names && names.driverName(m.name)) return reply(session, m, { ok: false, error: NAME_TEXTS[10606], code: 10606 });
    if (gd && !(Number.isInteger(m.car) && gd.isStarter(m.car))) return reply(session, m, { ok: false, error: 'pick one of the starter cars' });
    if (store.driver(session.accountId)) return reply(session, m, { ok: false, error: 'you already have a driver' });
    if (store.driverNameTaken(m.name)) return reply(session, m, { ok: false, error: 'that driver name is taken' });
    const profile = ensureProfile(newProfile(m.name, m.car, m.color, m.avatar));
    try { store.createDriver(session.accountId, m.name, profile); }
    catch (err) {
      log(`driver.create ${m.name}: ${err.message}`);
      return reply(session, m, { ok: false, error: /UNIQUE/i.test(err.message) ? 'that driver name is taken' : 'could not create the driver' });
    }
    attachDriver(session, m.name, profile);
    return reply(session, m, { ok: true, driver: profile });
  }

  /** Shop/workshop messages: save only when something changed. */
  function trade(session, m, r, look) {
    if (r && r.ok && (m.t === 'shop.buyCar' || m.t === 'shop.buyPart' || m.t === 'shop.paint')) vshop.bought(session, m, r);
    if (r && r.ok) { session.save(); if (look) world.refreshLook(session); }
    return reply(session, m, { ...(r || { ok: false, error: 'not available' }), driver: session.profile });
  }

  function handle(session, m) {
    switch (m.t) {
      case 'ping': return session.send({ t: 'pong', at: m.at });
      case 'register': return login(session, m, true);
      case 'login': return login(session, m, false);
      case 'driver.create': return createDriver(session, m);
    }
    if (!requireDriver(session, m)) return;
    if (races.owns(m.t) || laps.owns(m.t) || tours.owns(m.t)) { const r = (races.owns(m.t) ? races : laps.owns(m.t) ? laps : tours).handle(session, m); return r === undefined ? undefined : reply(session, m, r); }
    if (smallScreens.owns(m.t)) return reply(session, m, smallScreens.handle(session, m));
    if (social.owns(m.t)) return reply(session, m, social.handle(session, m));
    if (partyplay.owns(m.t)) return reply(session, m, partyplay.handle(session, m));
    if (auction.owns(m.t)) return reply(session, m, auction.handle(session, m));
    if (vshop.owns(m.t)) return reply(session, m, vshop.handle(session, m));
    if (fuel.owns(m.t)) return reply(session, m, fuel.handle(session, m));
    if (driverInfo.owns(m.t)) return reply(session, m, driverInfo.handle(session, m));
    if (carParts.owns(m.t)) return reply(session, m, carParts.handle(session, m));
    if (services.owns(m.t)) return reply(session, m, services.handle(session, m));
    if (events.owns(m.t)) { const r = events.handle(session, m); if (r.ok) session.save(); return reply(session, m, r); }
    if (quests.owns(m.t)) { const r = quests.handle(session, m); if (r.ok) session.save(); return reply(session, m, { ...r, driver: session.profile }); }
    const p = session.profile;
    switch (m.t) {
      case 'channels': return reply(session, m, { ok: true, channels: world.channelList(typeof m.city === 'string' ? m.city : undefined) });
      case 'enter': {
        const ch = session.channel, wanted = typeof m.channel === 'string' ? m.channel : undefined;
        // a race room belongs to its channel: leave it unless this re-enters the same channel
        if (session.race && !(ch && ch.city === m.city && (!wanted || wanted === ch.id))) races.leave(session);
        if (trades.active(session) && !(ch && ch.city === m.city && (!wanted || wanted === ch.id))) trades.leave(session);
        const r = world.join(session, m.city, wanted);
        if (session.race && session.race.channel !== session.channel) races.leave(session);
        if (r.ok) { p.City = m.city; session.save(); }
        return reply(session, m, r);
      }
      case 'state': return world.updateState(session, m);
      case 'chat':
        if (typeof m.text === 'string' && m.text.startsWith('/') && session.admin) return adminCommand(m.text.slice(1), out => session.send({ t: 'chat', scope: 'system', from: '', text: out }));
        return world.chat(session, m);
      case 'party.invite': return reply(session, m, world.partyInvite(session, m.name));
      case 'party.accept': return reply(session, m, world.partyAccept(session));
      case 'party.leave': world.partyLeave(session); return reply(session, m, { ok: true });
      case 'race.list': return reply(session, m, { ok: true, rooms: races.list(session, Number.isInteger(m.arena) ? m.arena : undefined) });
      case 'race.host': return reply(session, m, races.host(session, m));
      case 'race.join': return reply(session, m, races.join(session, { id: m.id, password: typeof m.password === 'string' ? m.password : '' }));
      case 'race.quick': return reply(session, m, races.quick(session, m));
      case 'race.settings': return reply(session, m, races.settings(session, m));
      case 'race.slot': return reply(session, m, races.slot(session, m));
      case 'race.team': return reply(session, m, races.teamChange(session, m.team));
      case 'race.kick': return reply(session, m, races.kickPlayer(session, m.id));
      case 'race.ladder': return reply(session, m, races.ladder(session, m));
      case 'race.leave': races.leave(session); return reply(session, m, { ok: true });
      case 'trade.request': case 'trade.accept': case 'trade.decline': case 'trade.offer': case 'trade.lock': case 'trade.confirm': case 'trade.cancel':
        return reply(session, m, trades.handle(m.t, session, m));
      case 'race.ready': return reply(session, m, races.ready(session, m.ready));
      case 'race.startRace': return reply(session, m, races.start(session));
      case 'race.progress': return races.progress(session, m);
      case 'reward': { const r = economy.claim(session, m.kind, m.data); if (r.ok) { session.save(); if (m.kind === 'delivery') quests.recordDelivery(session, m.data); } return reply(session, m, { ...r, driver: p }); }
      case 'shop.buyCar': {
        // coupon cars need their coupon in the bag (events.js coupons, client text 10489); the dealer takes it
        const car = gd && Number.isInteger(m.vehicleIndex) ? gd.car(m.vehicleIndex | 0) : null;
        const coupon = car ? events.t.coupons.find(c => c.carId === car.uniqueId && c.item) : null;
        const has = coupon ? ((p.Items || []).find(i => i.Id === coupon.item) || { Count: 0 }).Count > 0 : true;
        if (!has) return reply(session, m, { ok: false, error: 'You need the coupon for this car.' });
        const r = economy.buyCar(p, m);
        if (r.ok && coupon) { const it = p.Items.find(i => i.Id === coupon.item); if (it && --it.Count <= 0) p.Items.splice(p.Items.indexOf(it), 1); }
        return trade(session, m, r, false);
      }
      case 'shop.upgrade': return trade(session, m, economy.upgrade(p, m), false);
      case 'shop.paint': return trade(session, m, economy.paint(p, m), false);
      case 'shop.buyPart': return trade(session, m, economy.buyPart(p, m), true);
      case 'shop.fitPart': return trade(session, m, economy.fitPart(p, m), true);
      case 'shop.sellCar': return trade(session, m, economy.sellCar(p, m), true);
      case 'licence.earn': case 'licence.mount': case 'perf.buy': case 'perf.fit': case 'perf.enhance': case 'perf.grind':
      case 'perf.combine': case 'item.buy': case 'skill.learn': case 'craft': case 'item.use': case 'item.discard': case 'item.sell':
        return trade(session, m, workshop.handle(m.t, p, m), m.t === 'item.use');
      // period items and set parts (src/items.js, DriftCity_RE/INVENTORY.md)
      case 'item.expire': case 'set.fit': case 'set.restore': case 'set.buyLube':
        return trade(session, m, require('./src/items').handle(m.t, p, m), m.t !== 'set.buyLube');
      case 'profile.update': {
        const look = JSON.stringify([p.CurrentCar, p.Cars[p.CurrentCar]]);
        economy.applyUpdate(p, m.update);
        session.save();
        if (JSON.stringify([p.CurrentCar, p.Cars[p.CurrentCar]]) !== look) world.refreshLook(session);
        return reply(session, m, { ok: true, driver: p });
      }
      case 'leaderboard':
        return reply(session, m, { ok: true, drivers: store.topDrivers(20), times: m.track ? store.bestTimes(String(m.track).slice(0, 64)) : [] });
      case 'online': return reply(session, m, { ok: true, players: world.online() });
      default: return reply(session, m, { ok: false, error: `unknown message ${String(m.t).slice(0, 32)}` });
    }
  }

  // ---------------- admin ----------------
  function adminCommand(line, out) {
    const [cmd, ...rest] = line.trim().split(/\s+/);
    if (notices.admin(cmd, rest, out)) return;   // notice, scroll, popup, gm, event, maint, mute (src/notices.js)
    const arg = rest.join(' ');
    switch ((cmd || '').toLowerCase()) {
      case 'players': case 'who': out(world.online().map(p => `${p.name} (Lv ${p.level}) ${p.channel || '-'}`).join('\n') || 'nobody online'); break;
      case 'say': case 'announce': world.announce(arg); out('announced'); break;
      case 'kick': { const s = world.findByName(rest[0]); if (s) { s.kick(rest.slice(1).join(' ') || 'kicked by admin'); out('kicked'); } else out('not online'); break; }
      case 'ban': out(store.setBanned(rest[0], true) ? 'banned' : 'no such account'); world.findByName(rest[0])?.kick('banned'); break;
      case 'unban': out(store.setBanned(rest[0], false) ? 'unbanned' : 'no such account'); break;
      case 'admin': out(store.setAdmin(rest[0], true) ? 'admin granted' : 'no such account'); break;
      case 'mute': { const s = world.findByName(rest[0]); if (s) { s.muted = !s.muted; out(s.muted ? 'muted' : 'unmuted'); } else out('not online'); break; }
      case 'givemito': {
        const s = world.findByName(rest[0]); const n = parseInt(rest[1], 10);
        if (!s || !Number.isSafeInteger(n) || n === 0) { out('usage: givemito <driver> <amount>'); break; }
        const before = s.profile.Mito;
        s.profile.Mito = Math.max(0, Math.min(Number.MAX_SAFE_INTEGER, before + n));
        s.save(); s.send({ t: 'driver', driver: s.profile });
        out(before + n < 0 ? `done (Mito can't go below 0; now ${s.profile.Mito})` : 'done');
        break;
      }
      case 'setlevel': { const s = world.findByName(rest[0]); const n = parseInt(rest[1], 10); if (s && n > 0 && n <= 200) { s.profile.Level = n; s.profile.Exp = 0; s.save(); s.send({ t: 'driver', driver: s.profile }); out('done'); } else out('usage: setlevel <driver> <level>'); break; }
      case 'channels': out(world.channelList().map(c => `${c.id}: ${c.players}/${c.capacity}`).join('\n')); break;
      default: out('commands: players, say <text>, kick <driver>, ban/unban <account>, admin <account>, mute <driver>, givemito <driver> <n>, setlevel <driver> <n>, channels');
    }
  }

  const auctionSweep = setInterval(() => { try { auction.sweep(); } catch (err) { log(`auction sweep: ${err.message}`); } }, 60000);
  auctionSweep.unref();
  const socialSweep = setInterval(() => { try { social.sweep(); } catch (err) { log(`social sweep: ${err.message}`); } }, 3600000);
  socialSweep.unref();
  const tick = setInterval(() => { try { world.tick(); } catch (err) { log(`tick: ${err.message}`); } }, 100);
  // drop silent connections, autosave every 30 s
  const housekeeping = setInterval(() => {
    const now = Date.now();
    for (const client of wss.clients) if (client.readyState === client.OPEN) { try { client.ping(); } catch { /* closing */ } }
    for (const s of world.sessions.values()) {
      try { if (now - s.lastSeen > 60000) s.kick('timed out'); else s.save(); }
      catch (err) { log(`housekeeping ${s.name}: ${err.message}`); }
    }
    for (const [ip, e] of ipFails) if (now - e.last > 15 * 60000) ipFails.delete(ip);
  }, 30000);

  return {
    store, world, races, laps, tours, smallScreens, economy, workshop, trades, social, events, partyplay, auction, vshop, notices, services, gamedata: gd, adminCommand,
    listen() { return new Promise(resolve => httpServer.listen(cfg.port, () => { log(`${cfg.name} listening on ws://0.0.0.0:${cfg.port} (db ${cfg.db})`); resolve(httpServer.address().port); })); },
    async close() {
      closed = true;
      notices.close();
      social.teams.cb.close();
      services.close();
      partyplay.close();
      clearInterval(tick); clearInterval(housekeeping); clearInterval(socialSweep); clearInterval(auctionSweep);
      races.closeAll(); laps.closeAll(); tours.closeAll(); smallScreens.closeAll();
      for (const s of world.sessions.values()) { s.save(); s.closed = true; }
      for (const c of wss.clients) c.terminate();
      await new Promise(r => wss.close(() => httpServer.close(() => r())));
      store.close();
    },
  };
}

if (require.main === module) {
  // test hooks (Unity PlayMode tests): shorter race countdown / minimum track length
  const env = k => (process.env[k] !== undefined && process.env[k] !== '' ? Number(process.env[k]) : undefined);
  const server = createServer({ raceCountdownMs: env('DC_RACE_COUNTDOWN_MS'), raceMinTrackLength: env('DC_RACE_MIN_TRACK'), raceMaxSpeed: env('DC_RACE_MAX_SPEED'), raceAiSpeed: env('DC_RACE_AI_SPEED'), raceRmAlways: env('DC_RM_ALWAYS') === 1, racePickMs: env('DC_RACE_PICK_MS'), lapRoundMs: env('DC_LAP_ROUND_MS'), lapStartMs: env('DC_LAP_START_MS'), tourSignupMs: env('DC_TOUR_SIGNUP_MS'), ucTimeScale: env('DC_UC_TIMESCALE'), auctionNpc: env('DC_AUCTION_NPC'), giftLevel: env('DC_GIFT_LEVEL') });
  server.listen();
  const rl = readline.createInterface({ input: process.stdin });
  rl.on('line', line => line.trim() && server.adminCommand(line, out => console.log(out)));
  const stop = async () => { console.log('saving and shutting down...'); await server.close(); process.exit(0); };
  process.on('SIGINT', stop);
  process.on('SIGTERM', stop);
}

module.exports = { createServer, PROTOCOL };
