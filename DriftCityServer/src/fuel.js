// Mittron fuel (DriftCity_RE/SCREENS.md "Mittron fuel"): the filling station CHARGER 940000 and the Battle Zone's
// charger CHARGERPVP 960000. The client keeps each car's litres (the remake's Game/Fuel/MittronFuel.cs); the server
// charges the Mito for station fuel and spends the Mittron Fuel items. The price per litre is the remake's rule
// (the client's server sent it, 0x54fec0; CHARGER4GATE's default text says "1L/10 Mito").
'use strict';

const PRICE_PER_LITRE = 10;
const CAPACITY = 100;                       // 0x479510: the client's default tank (car +0x2c0)
// the car's tank grows with its "gage" assists ((100 + pct) / 100 x the litres, 0x47fde0; src/carparts.js): one
// fill may take up to twice the default tank
const MAX_FILL = CAPACITY * 2;
/** Mittron Fuel items (useitem.xlt): litres each. */
const FUEL_ITEMS = { i_n_00004: 2, i_n_00005: 10, i_n_00006: 300 };

const fail = error => ({ ok: false, error });
const count = (p, id) => ((p.Items || []).find(i => i && i.Id === id) || { Count: 0 }).Count;
function give(p, id, n) {
  if (!Array.isArray(p.Items)) p.Items = [];
  let it = p.Items.find(i => i && i.Id === id);
  if (!it) p.Items.push(it = { Id: id, Count: 0 });
  it.Count += n;
  if (it.Count <= 0) p.Items.splice(p.Items.indexOf(it), 1);
}

class Fuel {
  owns(t) { return t === 'fuel.buy' || t === 'fuel.use'; }

  /** fuel.buy { litres }: charges litres x price; short of Mito only what the Mito covers (client text 10947). */
  buy(p, m) {
    const want = Math.floor(Number(m.litres));
    if (!Number.isFinite(want) || want <= 0 || want > MAX_FILL) return fail('Choose how much Mittron to fill.');
    const mito = Number(p.Mito) || 0;
    const litres = Math.min(want, Math.floor(mito / PRICE_PER_LITRE));
    if (litres <= 0) return fail('Only 0 Mito amount of Mittron refueled due to insufficient Mito.');
    const cost = litres * PRICE_PER_LITRE;
    p.Mito = mito - cost;
    return { ok: true, litres, mito: -cost, price: PRICE_PER_LITRE };
  }

  /** fuel.use { items: { id: count } }: spends Mittron Fuel items (CHARGERPVP USE); returns the litres. */
  use(p, m) {
    const items = m.items && typeof m.items === 'object' && !Array.isArray(m.items) ? m.items : null;
    if (!items) return fail('Nothing to use.');
    let litres = 0;
    const plan = [];
    for (const [id, n0] of Object.entries(items)) {
      const n = Math.floor(Number(n0));
      if (!FUEL_ITEMS[id] || !Number.isFinite(n) || n <= 0 || n > 9999) return fail("that item can't be used here");
      if (count(p, id) < n) return fail("you don't have that");
      plan.push([id, n]);
      litres += FUEL_ITEMS[id] * n;
    }
    if (!plan.length) return fail('Nothing to use.');
    for (const [id, n] of plan) give(p, id, -n);
    return { ok: true, litres };
  }

  handle(session, m) {
    const p = session.profile;
    const r = m.t === 'fuel.buy' ? this.buy(p, m) : this.use(p, m);
    if (r.ok) session.save();
    return { ...r, driver: p };
  }
}

module.exports = { Fuel, PRICE_PER_LITRE, FUEL_ITEMS };
