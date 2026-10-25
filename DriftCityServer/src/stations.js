// Crew-owned Mittron filling stations (DriftCity_RE/SERVICES.md section 6). Client [exact]: opening CHARGER 940000
// asks the station's prices (0x799980 with 0 litres); the reply op 0x2d4 (0x4ff020 -> 0x54fec0) sets the list / sale
// price per litre ("1L / %u Mito", SELL_PRICE "%I64d" or "%I64d(%I64d)") and OWNTEAM: the channel's index
// (0x476630(net+0x3b4e8) - 1, 0..23) picks the Team Center's channel record (TC +0xc0 + 0x75 * i, the crew name at
// +0x1d) -> 10168 "Owned by: %s Crew", else 10169 "Owned by: None". The owner's Master was paid the station's usage
// fees by the server (server texts 20268 "%d channel's Mittron Filling Station usage fee, %d Mitos, has been deposited
// into your account." / 20288 for semi channels).
// Remake rules: a station belongs to the crew owning the Channel Battle channel that is the driver's world channel
// (src/channelbattle.js TABLE: battle channel i = `${city}-${n}`); every station sells at 10 Mito a litre (src/fuel.js);
// 20 % of what is paid at an owned station goes into that channel's fee pool, which is paid to the owner crew's Master by
// note every hour (and when ownership changes hands the pool goes to whoever owns it at payout). station.fill refuses a
// mileage-limited rental car (10953). Short of Mito it fills what the Mito pays for (10947), like fuel.buy.
'use strict';

const { PRICE_PER_LITRE } = require('./fuel');
const { refusal } = require('./rentals');

const CAPACITY = 100;
const FEE_PCT = 20;
const PAYOUT_MS = 3600000;
const fail = (error, key) => (key ? { ok: false, error, key } : { ok: false, error });

class Stations {
  constructor({ store, social, log }) {
    this.social = social; this.log = log || (() => {});
    const db = store.db;
    db.exec('CREATE TABLE IF NOT EXISTS station_fees (channel TEXT PRIMARY KEY, mito INTEGER NOT NULL DEFAULT 0)');
    this.q = {
      add: db.prepare('INSERT INTO station_fees (channel, mito) VALUES (?, ?) ON CONFLICT(channel) DO UPDATE SET mito = mito + excluded.mito'),
      all: db.prepare('SELECT channel, mito FROM station_fees WHERE mito > 0'),
      get: db.prepare('SELECT mito FROM station_fees WHERE channel = ?'),
      clear: db.prepare('UPDATE station_fees SET mito = 0 WHERE channel = ?'),
    };
    this.timer = setInterval(() => { try { this.payout(); } catch (err) { this.log(`station fees: ${err.message}`); } }, PAYOUT_MS);
    if (this.timer.unref) this.timer.unref();
  }

  close() { clearInterval(this.timer); }
  owns(t) { return t === 'station.info' || t === 'station.fill'; }
  get cb() { return this.social && this.social.teams ? this.social.teams.cb : null; }

  /** The battle channel row of a world channel id (or null), with its owner crew summary. */
  ownerOf(channelId) {
    const cb = this.cb;
    if (!cb || !channelId) return null;
    const { channelIndex, TABLE } = require('./channelbattle');
    const i = channelIndex(channelId);
    if (i < 0) return null;
    const own = cb.ownerRow(i);
    const crew = own && own.crew_id ? cb.crewSummary(own.crew_id) : null;
    return { index: i, info: TABLE[i], crew };
  }

  info(session) {
    const o = this.ownerOf(session.channel ? session.channel.id : '');
    return { ok: true, price: PRICE_PER_LITRE, sale: 0, owner: o && o.crew ? o.crew.name : '', ownerMark: o && o.crew ? o.crew.mark | 0 : 0,
      battle: o ? o.info.label : '', pool: o ? (this.q.get.get(o.info.key) || { mito: 0 }).mito : 0, feePct: FEE_PCT };
  }

  fill(session, m) {
    const p = session.profile;
    const car = Array.isArray(p.Cars) ? p.Cars[p.CurrentCar | 0] : null;
    const no = refusal(car, 'refuel');
    if (no) return no;
    const want = Math.floor(Number(m.litres));
    if (!Number.isFinite(want) || want <= 0 || want > CAPACITY) return fail('Choose how much Mittron to fill.');
    const mito = Number(p.Mito) || 0;
    const litres = Math.min(want, Math.floor(mito / PRICE_PER_LITRE));
    if (litres <= 0) return fail('Only 0 Mito amount of Mittron refueled due to insufficient Mito.', '10947');
    const cost = litres * PRICE_PER_LITRE;
    p.Mito = mito - cost;
    const o = this.ownerOf(session.channel ? session.channel.id : '');
    let fee = 0;
    if (o && o.crew) { fee = Math.floor(cost * FEE_PCT / 100); if (fee > 0) this.q.add.run(o.info.key, fee); }
    session.save();
    return { ok: true, litres, mito: -cost, price: PRICE_PER_LITRE, owner: o && o.crew ? o.crew.name : '', fee, short: litres < want, driver: p };
  }

  /** Pays every channel's pool to its owner crew's Master (20268 / 20288); returns [{ channel, mito, master }]. */
  payout() {
    const cb = this.cb, out = [];
    if (!cb) return out;
    const { channelIndex, TABLE } = require('./channelbattle');
    for (const row of this.q.all.all()) {
      const i = channelIndex(row.channel);
      if (i < 0) continue;
      const own = cb.ownerRow(i);
      const crew = own && own.crew_id ? this.social.teams.q.crew.get(own.crew_id) : null;
      if (!crew) continue;   // a vacant channel keeps its pool for the next owner
      const c = TABLE[i];
      const text = c.semi ? `Semi ${c.number} channel's Mittron Filling Station usage fee, ${row.mito} Mitos, has been deposited into your account.`
        : `${c.number} channel's Mittron Filling Station usage fee, ${row.mito} Mitos, has been deposited into your account.`;
      this.social.mail.system(crew.master, text, row.mito);
      this.q.clear.run(row.channel);
      out.push({ channel: row.channel, mito: row.mito, master: crew.master });
    }
    return out;
  }

  handle(session, m) { return m.t === 'station.info' ? this.info(session) : this.fill(session, m); }
}

module.exports = { Stations, FEE_PCT };
