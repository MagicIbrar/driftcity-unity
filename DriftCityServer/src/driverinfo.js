// Another driver's info card (game.nui USERINFO2ND 1830000, DriftCity_RE/SCREENS.md "Driver info"): the player menu's
// User Info (COMMAND 700000) and the friend list ask for it. The client's server filled packet 0x295 (0x54c770):
// name, crew (+ mark), portrait, level, EXP, Mito (shown masked "***,***,***"), the individual and team battle records
// (10159 / 10160), the delivery count (10161), km driven (10729), the car and its grade, the mounted licence and
// Team ROO. The remake answers from the live session's profile, else the saved one; Ind. records are the Battle Zone
// races / wins (the ladder's PvpRaces / PvpWins), team records and the delivery count are not kept on the server (0).
'use strict';

const fail = error => ({ ok: false, error });
/** Crew class letter (client 0x514530; the remake's Teams.Grade): S with a capture or 500000 points, A 100000, B 20000. */
const grade = (points, captures) => (captures > 0 || points >= 500000 ? 'S' : points >= 100000 ? 'A' : points >= 20000 ? 'B' : 'C');

class DriverInfo {
  constructor({ store, world, social }) { this.store = store; this.world = world; this.social = social; }

  owns(t) { return t === 'driver.info'; }

  card(name) {
    if (typeof name !== 'string' || !name.trim()) return fail('Enter the character name.');
    const live = this.world.findByName(name.trim());
    let p = live && live.profile, accountId = live && live.accountId, shown = live && live.name;
    if (!p) {
      const row = this.store.q.driverByName.get(name.trim());
      if (!row) return fail(`${String(name).slice(0, 16)} does not exist.`);
      try { p = JSON.parse(row.profile); } catch { return fail(`${String(name).slice(0, 16)} does not exist.`); }
      accountId = row.account_id; shown = row.name;
    }
    const cars = Array.isArray(p.Cars) ? p.Cars : [];
    const car = cars.length ? cars[Math.max(0, Math.min(cars.length - 1, p.CurrentCar | 0))] : null;
    const teams = this.social && this.social.teams;
    const crew = teams && accountId ? teams.crewOf(accountId) : null;
    return {
      ok: true, name: shown, online: !!live, avatar: Math.max(1, Math.min(8, p.Avatar | 0 || 1)),
      level: p.Level | 0, exp: Number(p.Exp) || 0,
      crew: crew ? crew.name : '', crewMark: crew ? crew.mark | 0 : 0, crewGrade: crew ? grade(crew.points | 0, crew.captures | 0) : 'C',
      races: p.PvpRaces | 0, wins: p.PvpWins | 0, teamRaces: 0, teamWins: 0, deliveries: 0,
      km: Number(p.DistanceKm) || 0, ladder: Number.isFinite(p.LadderPoints) ? p.LadderPoints : null,
      licence: Number.isInteger(p.MountedLicence) ? p.MountedLicence : -1, roo: (p.Faction | 0) === 1,
      car: car ? { vehicle: car.VehicleIndex | 0, grade: car.Grade | 0 } : null,
    };
  }

  handle(session, m) { return this.card(m.name); }
}

module.exports = { DriverInfo };
