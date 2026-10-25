// The original server's remaining systems (DriftCity_RE/SERVICES.md): server switches (`features`), private channels
// (pchan.*), rental cars (rental.*), driver rename (rename.*), recommend-a-driver (recommend.*), crew-owned Mittron
// stations (station.*) and the personal shop (pshop.list). One registration in server.js: services.owns / handle.
'use strict';

const { Switches } = require('./switches');
const { PrivateChannels } = require('./privchan');
const { Rentals } = require('./rentals');
const { Drivers } = require('./drivers');
const { Stations } = require('./stations');
const { PersonalShop } = require('./pshop');

class Services {
  /** deps: store, world, social, vshop, events, gamedata, clock, log, switches (opts for Switches). */
  constructor(deps) {
    this.switches = new Switches(deps.switches || {}, deps.events);
    const d = { ...deps, switches: this.switches };
    this.pchan = new PrivateChannels(d);
    this.rentals = new Rentals(d);
    this.drivers = new Drivers(d);
    this.stations = new Stations(d);
    this.pshop = new PersonalShop(d);
    this.parts = [this.pchan, this.rentals, this.drivers, this.stations, this.pshop];
  }

  owns(t) { return t === 'features' || this.parts.some(x => x.owns(t)); }

  handle(session, m) {
    if (m.t === 'features') return { ok: true, features: this.switches.features() };
    const part = this.parts.find(x => x.owns(m.t));
    return part ? part.handle(session, m) : { ok: false, error: 'not available' };
  }

  close() { this.pchan.close(); this.rentals.close(); this.stations.close(); }
}

module.exports = { Services };
