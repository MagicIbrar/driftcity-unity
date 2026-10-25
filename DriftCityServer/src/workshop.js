// Licences, performance parts (parts shop), the item counter, manufacturing and item use.
// The dice are rolled here; the rules match the client's Workshop.cs (PerfRules / Crafting / Licences).
// With the game tables loaded (src/gamedata.js) licences, skill books and recipes are looked up by id only;
// without them the old checks on the client-sent values apply.
'use strict';

const BASE_POINTS = [4, 8, 14, 22], POINTS_PER_LEVEL = [1, 2, 3, 4];
const SHOP_PRICE = [20000, 80000, 300000], REQ_LEVEL = [1, 20, 45];
const GRADES = ['N', 'S', 'H', 'U'];
const MAX_ENHANCE = 12, MAX_PARTS = 60;
const COUNTER = { i_n_00003: 5000, i_n_Seed_N: 10000, i_n_Seed_S: 40000, i_n_Seed_H: 120000, user_create_kit1: 3000, user_create_kit2: 6000, user_create_kit3: 12000 };
const SKILLBOOK_PRICE = { low: 20000, med: 60000, high: 150000 };
const CRAFT_LEVEL_EXP = [8610, 66420, 221430, 521640];   // init\alchemy_levelmodify.xlt
const CHASE_DROPS = ['modipart_piece', 'i_n_00015', 'i_n_00016', 'i_n_00019', 'i_n_00020', 'i_n_00021', 'i_n_00031', 'i_n_00032', 'i_n_00033',
  'i_n_00040', 'i_n_00041', 'i_n_00042', 'i_n_00096', 'i_n_warpengine', 'i_n_gacha_A', 'i_n_gacha_D', 'i_n_00025',
  'pvp_roof_box_1', 'pvp_roof_box_2', 'pvp_roof_box_3'];
const CITY_COUNTER = { cras: 'RacesCras', moonpalace: 'RacesMoon', koinonia: 'RacesKoi', oros: 'RacesOros' };
const TRACK_EFFECT = { cras: 'ME_CrasTrackExp', moonpalace: 'ME_MoonTrackExp', koinonia: 'ME_KoiTrackExp', oros: 'ME_OrosTrackExp' };
const STATS = ['HuvCaught', 'RacesEntered', 'RacesWon', 'RacePoints', 'RacesCras', 'RacesMoon', 'RacesKoi', 'RacesOros', 'MaxEnhance'];

const points = p => BASE_POINTS[p.Grade] + p.Enhance * POINTS_PER_LEVEL[p.Grade];
const enhanceChance = (level, enh) => {
  let c = level === 0 ? 100 : Math.max(10, Math.min(100, 100 - 8 * level));
  if (enh === 'i_n_00003_plus') c += 10;
  if (enh === 'i_n_00003_deluxe') c += 20;
  return Math.min(level === 0 ? 100 : 95, c);
};
const enhanceFee = (p, discount) => Math.floor(1000 * (p.Enhance + 1) * (1 << p.Grade) * (100 - Math.max(0, Math.min(90, discount))) / 100);
const craftLevel = (exp, gd) => gd ? gd.craftLevel(exp) : Math.min(CRAFT_LEVEL_EXP.length, 1 + CRAFT_LEVEL_EXP.filter(x => exp >= x).length);
const isObj = x => !!x && typeof x === 'object' && !Array.isArray(x);
const racePoints = place => place === 1 ? 100 : place === 2 ? 60 : place === 3 ? 40 : 20;

/** Drivers saved before these systems existed get the fields. */
function ensureWorkshop(p) {
  for (const k of ['Licences', 'PerfParts', 'Items', 'SkillBooks']) if (!Array.isArray(p[k])) p[k] = [];
  for (const k of STATS) if (!Number.isFinite(p[k])) p[k] = 0;
  if (!Number.isInteger(p.MountedLicence)) p.MountedLicence = -1;
  if (!Number.isInteger(p.NextPartUid)) p.NextPartUid = 1;
  if (!Number.isFinite(p.CraftExp)) p.CraftExp = 0;
  if (!p.MountedEffect) p.MountedEffect = null;
  for (const car of p.Cars || []) {
    if (!Array.isArray(car.PerfSlots) || car.PerfSlots.length !== 4) car.PerfSlots = [-1, -1, -1, -1];
    if (!Array.isArray(car.GiftParts)) car.GiftParts = [];
  }
  p.Items = p.Items.filter(i => isObj(i) && typeof i.Id === 'string' && Number.isFinite(i.Count));
  p.PerfParts = p.PerfParts.filter(isObj);
  return p;
}

const count = (p, id) => (p.Items.find(i => i.Id === id) || { Count: 0 }).Count;
function give(p, id, n = 1) {
  let it = p.Items.find(i => i.Id === id);
  if (!it) p.Items.push(it = { Id: id, Count: 0 });
  it.Count += n;
  if (it.Count <= 0) p.Items.splice(p.Items.indexOf(it), 1);
}

function stat(p, name) {
  if (typeof name !== 'string') return 0;
  if (name.startsWith('Licence_')) return p.Licences.includes(parseInt(name.slice(8), 10)) ? 1 : 0;
  if (name === 'Quests') return p.CompletedQuests.length;
  return STATS.includes(name) ? p[name] : 0;
}

/** Bonus percent of the mounted licence for any of the given effects (capped at 20); from the licence table when loaded. */
function bonus(p, gd, ...effects) {
  if (gd) {
    const l = p.MountedLicence >= 0 && p.Licences.includes(p.MountedLicence) ? gd.licence(p.MountedLicence) : null;
    return l && effects.includes(l.effect) ? Math.max(0, Math.min(20, l.value)) : 0;
  }
  const m = p.MountedEffect;
  return m && p.Licences.includes(m.id) && effects.includes(m.effect) ? Math.max(0, Math.min(20, m.value | 0)) : 0;
}

/** Reward extras shared by chases, solo races, online races and quests. Returns extra exp/mito and a drop. */
// event bonuses (src/events.js: scratch-card Battle EXP goals, finished Christmas trees); server.js installs the provider
let eventBonus = () => ({ exp: 0, mito: 0 });
function setEventBonus(f) { eventBonus = typeof f === 'function' ? f : () => ({ exp: 0, mito: 0 }); }
// the party bonus (src/partyplay.js, 0x4fe050): provider (profile, kind) -> { exp, mito } percent or null
let partyBonus = () => null;
function setPartyBonus(f) { partyBonus = typeof f === 'function' ? f : () => null; }
// the calendar (src/multipliers.js: golden time, Santa, the 3rd anniversary): provider (kind) -> { exp, mito, flags } or null
let multiplierBonus = () => null;
function setMultiplierBonus(f) { multiplierBonus = typeof f === 'function' ? f : () => null; }

function rewardExtras(p, kind, base, data = {}, rng = Math.random, gd = null) {
  ensureWorkshop(p);
  if (!isObj(data)) data = {};
  let expPct = 0, mitoPct = 0, drop = null;
  if (kind === 'chase') expPct = mitoPct = bonus(p, gd, 'ME_ChasingExpMito', 'ME_AllExpMito');
  if (kind === 'quest') expPct = mitoPct = bonus(p, gd, 'ME_AllExpMito');
  if (kind === 'race') {
    const city = String(data.city || '');
    p.RacePoints += racePoints(data.place | 0);
    if (CITY_COUNTER[city]) p[CITY_COUNTER[city]]++;
    expPct = bonus(p, gd, TRACK_EFFECT[city], 'ME_AllExpMito');
    mitoPct = bonus(p, gd, 'ME_BattlePersonalMito', 'ME_AllExpMito');
  }
  // timed drinks from the item bag (src/items.js): +30 % EXP, +50 % Mito, item drink doubles drops
  const drink = require('./items').buffBonus(p);
  if (drink.exp && kind !== 'quest') expPct += drink.exp;
  if (drink.mito && kind !== 'quest') mitoPct += drink.mito;
  const ev = eventBonus(kind) || {};
  expPct += ev.exp | 0; mitoPct += ev.mito | 0;
  const pb = partyBonus(p, kind) || {};
  expPct += pb.exp | 0; mitoPct += pb.mito | 0;
  const cal = multiplierBonus(kind) || {};
  expPct += cal.exp | 0; mitoPct += cal.mito | 0;
  const exp = Math.floor(base.exp * expPct / 100), mito = Math.floor(base.mito * mitoPct / 100);
  if (kind === 'chase' && rng() < 0.6) {
    drop = rng() < 0.35 ? 'i_n_00003' : CHASE_DROPS[Math.floor(rng() * CHASE_DROPS.length)];
    give(p, drop);
  }
  // the official server's combo drop table isn't in the client data: the chase material pool stands in
  if (kind === 'combo') {
    drop = rng() < 0.35 ? 'i_n_00003' : CHASE_DROPS[Math.floor(rng() * CHASE_DROPS.length)];
    give(p, drop);
  }
  // a cleared Undercity run brings an item (UCRESULT's item slots; the server's own table isn't in the client data)
  if (kind === 'undercity' && data.cleared === true) {
    drop = rng() < 0.35 ? 'i_n_00003' : CHASE_DROPS[Math.floor(rng() * CHASE_DROPS.length)];
    give(p, drop);
  }
  if (drop && drink.drop) give(p, drop);   // Item drink: one more of the same drop
  return { exp, mito, drop, bonus: cal.flags | 0 };
}

const fail = error => ({ ok: false, error });

class Workshop {
  /** @param rng dice (tests pass a fixed one); @param gd game tables or null */
  constructor(rng = Math.random, gd = null) { this.rng = rng; this.gd = gd; }

  handle(type, p, m) {
    ensureWorkshop(p);
    if (!isObj(m)) m = {};
    const gd = this.gd;
    switch (type) {
      case 'licence.earn': {
        const id = m.id | 0;
        let needs, any;
        if (gd) {
          const l = gd.licence(id);
          if (!l) return fail('no such licence');
          needs = l.needs; any = l.anyOne;
        } else {
          needs = (Array.isArray(m.needs) ? m.needs.slice(0, 12) : []).filter(isObj).map(n => ({ stat: n.stat, value: Math.max(1, n.value | 0) }));
          any = !!m.any;
        }
        if (!needs.length) return fail('this licence cannot be earned here');
        const met = needs.map(n => stat(p, n.stat) >= n.value);
        if (!(any ? met.some(Boolean) : met.every(Boolean))) return fail('conditions not met');
        if (!p.Licences.includes(id)) p.Licences.push(id);
        return { ok: true };
      }
      case 'licence.mount': {
        const id = m.id | 0;
        if (!p.Licences.includes(id)) return fail('licence not earned');
        if (gd) {
          const l = gd.licence(id);
          if (!l) return fail('no such licence');
          p.MountedEffect = { id, effect: String(l.effect).slice(0, 40), value: Math.max(0, Math.min(20, l.value)) };
        } else {
          p.MountedEffect = { id, effect: String(m.effect || '').slice(0, 40), value: Math.max(0, Math.min(20, m.value | 0)) };
        }
        p.MountedLicence = id;
        return { ok: true };
      }
      case 'perf.buy': {
        const t = m.type | 0, g = m.grade | 0;
        if (t < 0 || t > 3 || g < 0 || g > 2) return fail('no such part');
        if (p.Level < REQ_LEVEL[g]) return fail(`needs level ${REQ_LEVEL[g]}`);
        if (p.Mito < SHOP_PRICE[g]) return fail('not enough Mito');
        if (p.PerfParts.length >= MAX_PARTS) return fail('parts box full');
        p.Mito -= SHOP_PRICE[g];
        p.PerfParts.push({ Uid: p.NextPartUid++, Type: t, Grade: g, Enhance: 0 });
        return { ok: true };
      }
      case 'perf.fit': {
        const car = p.Cars[m.slot | 0], t = m.type | 0, uid = m.uid | 0;
        if (!car || t < 0 || t > 3) return fail('no such slot');
        const part = p.PerfParts.find(x => x.Uid === uid);
        if (uid !== -1 && (!part || part.Type !== t)) return fail('no such part');
        for (const c of p.Cars) c.PerfSlots = c.PerfSlots.map(x => (x === uid && uid !== -1 ? -1 : x));
        car.PerfSlots[t] = uid;
        return { ok: true };
      }
      case 'perf.enhance': {
        const part = p.PerfParts.find(x => x.Uid === (m.uid | 0));
        const enh = String(m.enhancer || '');
        if (!part) return fail('no such part');
        if (!['i_n_00003', 'i_n_00003_plus', 'i_n_00003_deluxe'].includes(enh) || count(p, enh) <= 0) return fail('no enhancer');
        if (part.Enhance >= MAX_ENHANCE) return fail('already at the top level');
        const fee = enhanceFee(part, bonus(p, gd, 'ME_EnhanceFeeDis', 'ME_FeeDis'));
        if (p.Mito < fee) return fail('not enough Mito');
        give(p, enh, -1); p.Mito -= fee;
        const ok = this.rng() * 100 < enhanceChance(part.Enhance, enh);
        if (ok) { part.Enhance++; p.MaxEnhance = Math.max(p.MaxEnhance, part.Enhance); } else if (part.Enhance >= 5) part.Enhance--;
        return ok ? { ok: true } : { ok: true, error: 'failed', failed: true };
      }
      case 'perf.grind': {
        const part = p.PerfParts.find(x => x.Uid === (m.uid | 0));
        if (!part) return fail('no such part');
        for (const c of p.Cars) c.PerfSlots = c.PerfSlots.map(x => (x === part.Uid ? -1 : x));
        p.PerfParts.splice(p.PerfParts.indexOf(part), 1);
        give(p, 'mitrondust_' + GRADES[part.Grade], 1 + part.Enhance);
        return { ok: true };
      }
      case 'perf.combine': {
        const a = p.PerfParts.find(x => x.Uid === (m.a | 0)), b = p.PerfParts.find(x => x.Uid === (m.b | 0));
        const seed = String(m.seed || '');
        if (!a || !b || a === b || a.Type !== b.Type || a.Grade !== b.Grade || a.Grade >= 3) return fail('those parts cannot be combined');
        if (p.Cars.some(c => c.PerfSlots.includes(a.Uid) || c.PerfSlots.includes(b.Uid))) return fail('take the parts off first');
        const plus = seed === 'i_n_Seedplus_' + GRADES[a.Grade];
        if (!plus && seed !== 'i_n_Seed_' + GRADES[a.Grade]) return fail('wrong seed part');
        if (count(p, seed) <= 0) return fail('no seed part');
        give(p, seed, -1);
        const ok = this.rng() * 100 < (plus ? 75 : 50);
        if (ok) {
          p.PerfParts = p.PerfParts.filter(x => x !== a && x !== b);
          p.PerfParts.push({ Uid: p.NextPartUid++, Type: a.Type, Grade: a.Grade + 1, Enhance: 0 });
          return { ok: true };
        }
        const weaker = a.Enhance <= b.Enhance ? a : b;
        p.PerfParts.splice(p.PerfParts.indexOf(weaker), 1);
        return { ok: true, error: 'failed', failed: true };
      }
      case 'item.buy': {
        const id = String(m.id || ''), n = Math.max(1, Math.min(99, m.count | 0));
        if (!COUNTER[id]) return fail('not sold here');
        if (p.Mito < COUNTER[id] * n) return fail('not enough Mito');
        p.Mito -= COUNTER[id] * n; give(p, id, n);
        return { ok: true };
      }
      case 'skill.learn': {
        const id = String(m.id || '').slice(0, 40);
        let price, reqLv;
        if (gd) {
          const rc = gd.recipe(id);
          if (!rc) return fail('no such skill book');
          price = SKILLBOOK_PRICE[rc.group] || 400000; reqLv = rc.reqLevel;
        } else {
          price = SKILLBOOK_PRICE[m.group] || 400000; reqLv = m.reqLv | 0;
        }
        if (!id || p.SkillBooks.includes(id)) return fail('already learned');
        if (craftLevel(p.CraftExp, gd) < reqLv) return fail(`needs manufacturing level ${reqLv}`);
        if (p.Mito < price) return fail('not enough Mito');
        p.Mito -= price; p.SkillBooks.push(id);
        return { ok: true };
      }
      case 'craft': {
        const sent = isObj(m.recipe) ? m.recipe : {};
        const id = String(sent.id || m.id || '');
        if (!p.SkillBooks.includes(id)) return fail('learn the skill book first');
        let r;
        if (gd) {
          const rc = gd.recipe(id);
          if (!rc) return fail('bad recipe');
          r = { inputs: rc.inputs, kit: rc.kit, result: rc.result, count: rc.resultCount, chance: Math.max(0, Math.min(100, rc.chance)),
                exp: Math.max(0, rc.successExp), failExp: Math.max(0, rc.failExp) };
        } else {
          const inputs = (Array.isArray(sent.inputs) ? sent.inputs : []).slice(0, 3).filter(x => Array.isArray(x) && x.length >= 2)
            .map(x => [String(x[0]), Math.max(1, x[1] | 0)]);
          r = { inputs, kit: String(sent.kit || ''), result: String(sent.result || '').slice(0, 40), count: Math.max(1, Math.min(5, sent.count | 0)),
                chance: Math.max(0, Math.min(70, sent.chance | 0)), exp: Math.max(0, Math.min(1000, sent.exp | 0)), failExp: Math.max(0, Math.min(1000, sent.failExp | 0)) };
          if (!/^user_create_kit[123]$/.test(r.kit) || !r.inputs.length || !r.result) return fail('bad recipe');
        }
        if (r.inputs.some(([i, n]) => count(p, i) < n) || count(p, r.kit) <= 0) return fail('missing materials');
        for (const [i, n] of r.inputs) give(p, i, -n);
        give(p, r.kit, -1);
        const ok = this.rng() * 100 < r.chance;
        if (ok) give(p, r.result, r.count);
        p.CraftExp += ok ? r.exp : r.failExp;
        return ok ? { ok: true } : { ok: true, error: 'failed', failed: true };
      }
      case 'item.discard': return require('./items').discard(p, m);
      case 'item.sell': return require('./items').sell(p, m);
      case 'item.use': {
        const id = String(m.id || ''), car = p.Cars[m.slot | 0];
        const bag = require('./items').use(p, id);   // drinks, Mito cheques, repair / Mittron items
        if (bag) return bag;
        if (!car || count(p, id) <= 0) return fail("you don't have that");
        if (id === 'i_n_upgrade') {
          const top = gd ? gd.grades(gd.car(car.VehicleIndex)).length - 1 : 8;
          if (car.Grade >= top) return fail('this car is already at its top grade');
          car.Grade++;
        } else if (id === 'i_n_roof_jackbox' || id === 'i_n_roof_xcatbox') {
          const roof = id === 'i_n_roof_jackbox' ? 10890 : 20776;
          if (!Array.isArray(car.Parts)) car.Parts = [];
          if (!Array.isArray(car.GiftParts)) car.GiftParts = [];
          // an item-granted part: it never counts toward the resale price (ShopData.SellPrice)
          if (!car.Parts.includes(roof)) { car.Parts.push(roof); car.GiftParts.push(roof); }
          car.Roof = roof;
        } else if (id === 'i_bacchus_f' || id === 'i_bacchus_d') {
          const { addExp } = require('./economy');
          addExp(p, id === 'i_bacchus_f' ? 2000 : 6000);
        } else return fail("that item can't be used here");
        give(p, id, -1);
        return { ok: true };
      }
    }
    return null;
  }
}

module.exports = { Workshop, ensureWorkshop, rewardExtras, setEventBonus, setPartyBonus, setMultiplierBonus, points, enhanceChance, craftLevel, CHASE_DROPS, SKILLBOOK_PRICE };
