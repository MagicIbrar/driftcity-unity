// Server switches the original server sent as feature bits (DriftCity_RE/SERVICES.md section 1): the client showed or
// refused a feature by them (10745 "This feature is currently unavailable." when a bit was off). The remake keeps
// them as one `features` reply the client reads once per session (Game/Services/ServerSwitches.cs):
//   privateChat   private channels + the chat's Private mode (0x5b6490 /cjoin.. and mode 6; client 10745 when off)
//   rentals       the custom shop's rental cars (VSHOP 2450000 RENT tabs, net+0x294 & 0x3c00000 at 0x6611f0)
//   instant       the station's INSTANT button (net+0x24c & 0x100000, 0x562030)
//   eventDelivery the station's EVENTARBEIT / EVENTTIMEATTACK pair (net+0x290 & 0x20000, 0x5625e0); 'season' = on while
//                 an event season with delivery events runs (Valentine / harvest / Halloween, the QSEVENT badges)
//   recommend     the recommend-a-driver prompt (op 0x097 sets net+0x3b518; the MSGBOX 0x554240 on entering a city)
//   personalShop  USERINFO2ND BUY_GOODS (buy the goods another driver wears)
// Each comes from opts (tests) or the environment (DC_PRIVATE_CHAT, DC_RENTALS, DC_INSTANT_BUTTON, DC_EVENT_DELIVERY,
// DC_RECOMMEND, DC_PERSONAL_SHOP): "on" / "off" (1 / 0, true / false); eventDelivery also "season" (the default).
'use strict';

const NAMES = ['privateChat', 'rentals', 'instant', 'eventDelivery', 'recommend', 'personalShop'];
const ENV = { privateChat: 'DC_PRIVATE_CHAT', rentals: 'DC_RENTALS', instant: 'DC_INSTANT_BUTTON', eventDelivery: 'DC_EVENT_DELIVERY', recommend: 'DC_RECOMMEND', personalShop: 'DC_PERSONAL_SHOP' };
const DELIVERY_SEASONS = ['valentine', 'harvest', 'halloween'];

function parse(v, dflt) {
  if (v === undefined || v === null || v === '') return dflt;
  if (typeof v === 'boolean') return v;
  const s = String(v).trim().toLowerCase();
  if (s === 'season') return 'season';
  if (['0', 'off', 'false', 'no'].includes(s)) return false;
  if (['1', 'on', 'true', 'yes'].includes(s)) return true;
  return dflt;
}

class Switches {
  /** opts: { privateChat, rentals, ... } (tests); events: the Events instance (seasons) for eventDelivery 'season'. */
  constructor(opts = {}, events = null) {
    this.events = events;
    this.values = {};
    for (const n of NAMES) this.values[n] = parse(opts[n] !== undefined ? opts[n] : process.env[ENV[n]], n === 'eventDelivery' ? 'season' : true);
  }

  set(name, value) { if (NAMES.includes(name)) this.values[name] = parse(value, this.values[name]); }

  /** The switch now (eventDelivery 'season' resolved against the running event seasons). */
  on(name) {
    const v = this.values[name];
    if (v !== 'season') return !!v;
    const seasons = this.events && typeof this.events.seasons === 'function' ? this.events.seasons() : [];
    return seasons.some(s => DELIVERY_SEASONS.includes(s));
  }

  features() { const out = {}; for (const n of NAMES) out[n] = this.on(n); return out; }
}

module.exports = { Switches, NAMES, parse };
