// Driver rename and recommend-a-driver (DriftCity_RE/SERVICES.md sections 4 and 5).
// Rename [exact client]: the Driver's Name Change Kit i_changenick (visualitem type 36 "ChangeNick", vshopitem 10510,
// 3,500,000 Mito for 7 days, "Unchangeable for 60 days after a change") is used from the bag (0x5d52d0 case 0x24) and
// opens NICNAME 3770000: EDIT, NEW_NAMECONFIRM (the name check, 10604 "This ID is already in use." / 10605 "This ID is
// available."), ACCEPT, CLOSE; op 0x06b answers 10954 / 10955 "Character name has been successfully changed to %s. It
// cannot be changed for the next 60 days." or 10960 "Name change failed. Please try again.".
// Remake rules: rename.check {name} / rename.apply {name} (needs the kit, which is spent; the driver-name rules of
// driver.create; 60 days between changes, NameChangedAt); the name changes at once (no relogin); rename.buyKit sells the
// kit for its table price (the custom shop's Etc tab is not rebuilt) as a 7-day period item.
// Recommend [exact client]: op 0x097 sets net+0x3b518; on entering a city (0x74ca28 / 0x76dbf9, net+0x3b500 == 1) the
// client opens MSGBOX 3230000 with 10181 "Name of the recommended character." (0x554240); OK sends the name, op 0x099
// answers 1 -> 10042 "Recommended!" (the prompt closes, the flag clears) or another value -> 10043 (empty; the prompt
// stays). Remake rules: recommend.state says whether to ask (switch `recommend`, the driver has not named anybody, level
// 10 or below); recommend.send {name}: an existing driver of another account at level 10 or more; the named driver gets
// a note with 10,000 Mito, the driver is not asked again (RecommendDone).
'use strict';

const auth = require('./auth');
const items = require('./items');

const KIT = 'i_changenick';
const KIT_PRICE = 3500000;
const KIT_DAYS = 7;
const RENAME_LOCK_MS = 60 * 86400000;
const RECOMMEND_MAX_LEVEL = 10;
const RECOMMENDER_MIN_LEVEL = 10;
const RECOMMEND_MITO = 10000;
const fail = (error, key) => (key ? { ok: false, error, key } : { ok: false, error });

class Drivers {
  constructor({ store, world, social, switches, clock, log }) {
    this.store = store; this.world = world; this.social = social; this.switches = switches; this.clock = clock || Date.now; this.log = log || (() => {});
    this.q = {
      rename: store.db.prepare('UPDATE drivers SET name = ? WHERE account_id = ?'),
      byName: store.db.prepare("SELECT account_id, name, json_extract(profile, '$.Level') AS level FROM drivers WHERE name = ?"),
    };
  }

  owns(t) { return typeof t === 'string' && (t.startsWith('rename.') || t.startsWith('recommend.')); }

  // ---------------- rename ----------------
  check(session, name) {
    if (!auth.validDriverName(name)) return fail('The name you\'ve entered is not allowed. Please enter a different name.', '11296');
    if (this.world && this.world.names && this.world.names.driverName(name)) return fail('This name cannot be used.', '10606');   // the word filter (src/namefilter.js)
    const row = this.q.byName.get(name);
    if (row && row.account_id !== session.accountId) return fail('This ID is already in use.  Please create another ID.', '10604');
    if (row && row.name === session.name) return fail('This ID is already in use.  Please create another ID.', '10604');
    return { ok: true, key: '10605', text: 'This ID is available.' };
  }

  apply(session, m) {
    const p = session.profile;
    const name = typeof m.name === 'string' ? m.name.trim() : '';
    if (items.count(p, KIT) <= 0) return fail("You need a Driver's Name Change Kit.");
    const until = (Number(p.NameChangedAt) || 0) + RENAME_LOCK_MS;
    if (until > this.clock()) return fail(`The name cannot be changed for ${Math.ceil((until - this.clock()) / 86400000)} more days.`);
    const c = this.check(session, name);
    if (!c.ok) return c;
    const old = session.name;
    try { this.q.rename.run(name, session.accountId); }
    catch (err) { this.log(`rename ${old}: ${err.message}`); return fail('Name change failed. Please try again.', '10960'); }
    items.give(p, KIT, -1);
    p.Name = name;
    p.NameChangedAt = this.clock();
    session.name = name;
    session.save();
    if (this.world && this.world.refreshLook) this.world.refreshLook(session);
    this.log(`${old} is now ${name}`);
    return { ok: true, name, old, key: '10955', text: `Character name has been successfully changed to ${name}. It cannot be changed for the next 60 days.` };
  }

  buyKit(session) {
    const p = session.profile;
    if ((Number(p.Mito) || 0) < KIT_PRICE) return fail('not enough Mito');
    p.Mito -= KIT_PRICE;
    items.giveTimed(p, KIT, 1, KIT_DAYS * 24, this.clock());
    session.save();
    return { ok: true, mito: -KIT_PRICE };
  }

  // ---------------- recommend ----------------
  askRecommend(p) { return this.switches.on('recommend') && !p.RecommendDone && (p.Level | 0) <= RECOMMEND_MAX_LEVEL; }

  recommend(session, m) {
    const p = session.profile;
    if (!this.switches.on('recommend')) return fail('This feature is currently unavailable.', '10745');
    if (!this.askRecommend(p)) return fail('You have already named the driver who recommended you.');
    const name = typeof m.name === 'string' ? m.name.trim() : '';
    if (!name) return fail('Enter the character name.', '10539');
    const row = this.q.byName.get(name);
    if (!row) return fail(`${name.slice(0, 16)} does not exist.`);
    if (row.account_id === session.accountId) return fail('You cannot recommend yourself.');
    if ((row.level | 0) < RECOMMENDER_MIN_LEVEL) return fail(`Only a driver of level ${RECOMMENDER_MIN_LEVEL} or higher can be recommended.`);
    p.RecommendDone = true;
    session.save();
    if (this.social && this.social.mail) this.social.mail.system(row.account_id, `${session.name} named you as the driver who recommended them to Drift City. Thank you!`, RECOMMEND_MITO);
    this.log(`${session.name} recommended ${row.name}`);
    return { ok: true, name: row.name, key: '10042', text: 'Recommended!' };
  }

  handle(session, m) {
    switch (m.t) {
      case 'rename.check': return this.check(session, typeof m.name === 'string' ? m.name.trim() : '');
      case 'rename.apply': return { ...this.apply(session, m), driver: session.profile };
      case 'rename.buyKit': return { ...this.buyKit(session), driver: session.profile };
      case 'recommend.state': return { ok: true, ask: this.askRecommend(session.profile) };
      case 'recommend.send': return this.recommend(session, m);
      case 'recommend.skip': return { ok: true };
    }
    return fail(`unknown message ${m.t}`);
  }
}

module.exports = { Drivers, KIT, KIT_PRICE, RENAME_LOCK_MS, RECOMMEND_MITO };
