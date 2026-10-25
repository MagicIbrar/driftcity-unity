'use strict';
// Step quests of QuestClient.tdf (src/quests.js, economy.js kind "stepquest"), the factions and the delivery rankings
// (DriftCity_RE/MISSIONS.md sections 11-12).
const { test } = require('node:test');
const assert = require('node:assert');
const os = require('node:os');
const path = require('node:path');
const fs = require('node:fs');
const { Economy, newProfile, ensureProfile, COOLDOWN } = require('../src/economy');
const { loadGameData } = require('../src/gamedata');
const { Store } = require('../src/db');
const { Quests, stepTable, refusal, branchOf, periodStart, OMD, ROO } = require('../src/quests');

const gd = loadGameData(undefined, () => {});

test('QuestClient.tdf: the loader layout, unlocks and the faction branch', { skip: !gd }, () => {
  const t = stepTable(gd.dir);
  assert.strictEqual(t.list.length, 320);
  const first = t.byNumber.get(1);
  assert.deepStrictEqual([first.id, first.kind, first.prereq, first.minLevel, first.startPost, first.steps, first.exp, first.mito],
    ['q_00004', 0, 0, 1, 'post_1st_00052', 3, 700, 300]);
  assert.deepStrictEqual(first.items, ['i_n_start01']);
  assert.strictEqual(t.byNumber.get(5).unlock, 2, 'Rookie Mission (3/3) unlocks the Delivery Service (ARBEIT 2)');
  const roo = t.byNumber.get(127), omd = t.byNumber.get(145);
  assert.deepStrictEqual([roo.id, roo.unlock, roo.prereq], ['q_00131', 10, 126]);
  assert.deepStrictEqual([omd.id, omd.unlock, omd.prereq], ['q_00150', 9, 126]);
  assert.strictEqual(branchOf(t, t.byNumber.get(144)), ROO, 'Infiltration! follows the ROO branch');
  assert.strictEqual(branchOf(t, t.byNumber.get(162)), OMD, 'Issue the No.9 Card follows the OMD branch');
  assert.strictEqual(branchOf(t, t.byNumber.get(126)), -1);
  assert.strictEqual(t.list.find(q => q.number === 60100).kind, 2);
});

test('step quests: accept, step order and pace, abandon, the reward and joining Team ROO', { skip: !gd }, () => {
  let now = 1.7e12;
  const eco = new Economy(() => {}, gd, () => now);
  const quests = new Quests({ store: null, clock: () => now, gamedata: gd });
  const t = quests.table();
  const p = ensureProfile(newProfile('Quester', 0, 1));
  p.Level = 2;
  const s = { profile: p };
  const h = (m) => quests.handle(s, m);
  assert.ok(COOLDOWN.stepquest > 0);

  // #2 needs #1 first; #1 needs nothing
  assert.match(h({ t: 'quest.accept', number: 2 }).error, /previous quest/);
  assert.ok(h({ t: 'quest.accept', number: 1 }).ok);
  assert.match(h({ t: 'quest.accept', number: 1 }).error, /in progress/);
  // steps in order, not faster than a second apart
  assert.match(h({ t: 'quest.step', number: 1, step: 2 }).error, /wrong step/);
  assert.match(h({ t: 'quest.step', number: 1, step: 1 }).error, /too fast/);
  now += 5000; assert.ok(h({ t: 'quest.step', number: 1, step: 1 }).ok);
  assert.match(eco.claim(s, 'stepquest', { number: 1 }).error, /not done/);
  now += 5000; assert.ok(h({ t: 'quest.step', number: 1, step: 2 }).ok);
  now += 5000; const last = h({ t: 'quest.step', number: 1, step: 3 });
  assert.ok(last.ok && last.done, 'three steps: done');
  assert.deepStrictEqual(p.StepQuest, { QuestId: 1, Step: 3, Counter: 1 });
  const exp0 = p.Exp + 0, mito0 = p.Mito;
  const r = eco.claim(s, 'stepquest', { number: 1 });
  assert.ok(r.ok, r.error);
  assert.ok(r.exp >= 700 && r.mito >= 300, JSON.stringify(r));
  assert.ok(p.Mito >= mito0 + 300);
  assert.ok(p.Level > 1 || p.Exp > exp0);
  assert.ok(p.StepQuestsDone.includes(1));
  assert.strictEqual((p.Items.find(i => i.Id === 'i_n_start01') || {}).Count, 1, 'column 77 item');
  assert.strictEqual(p.StepQuest.QuestId, 0);
  assert.match(eco.claim(s, 'stepquest', { number: 1 }).error, /too soon|not accepted/);
  now += 10000; assert.match(eco.claim(s, 'stepquest', { number: 1 }).error, /not accepted/);
  assert.match(h({ t: 'quest.accept', number: 1 }).error, /already completed/);

  // retry from the start and abandon
  assert.ok(h({ t: 'quest.accept', number: 2 }).ok);
  now += 2000; assert.ok(h({ t: 'quest.step', number: 2, step: 1 }).ok);
  assert.ok(h({ t: 'quest.step', number: 2, step: 0 }).ok);
  assert.strictEqual(p.StepQuest.Step, 0);
  assert.ok(h({ t: 'quest.abandon', number: 2 }).ok);
  assert.strictEqual(p.StepQuest.QuestId, 0);

  // a whole quest too fast: three seconds a step at least
  assert.ok(h({ t: 'quest.accept', number: 2 }).ok);
  for (let k = 1; k <= 4; k++) { now += 1100; assert.ok(h({ t: 'quest.step', number: 2, step: k }).ok, `step ${k}`); }
  assert.match(eco.claim(s, 'stepquest', { number: 2 }).error, /too fast/);
  now += 20000; assert.ok(eco.claim(s, 'stepquest', { number: 2 }).ok);

  // the choice at #126: Team ROO's #127 sets the faction and closes the OMD branch (#145)
  for (let n = 3; n <= 126; n++) if (t.byNumber.has(n)) p.StepQuestsDone.push(n);
  p.Level = 60;
  assert.strictEqual(refusal(t, p, t.byNumber.get(145)), null, 'both join quests open before the choice');
  assert.strictEqual(refusal(t, p, t.byNumber.get(127)), null);
  now += 10000; assert.ok(h({ t: 'quest.accept', number: 127 }).ok);
  const steps = t.byNumber.get(127).steps;
  for (let k = 1; k <= steps; k++) { now += 4000; assert.ok(h({ t: 'quest.step', number: 127, step: k }).ok); }
  now += 10000; assert.ok(eco.claim(s, 'stepquest', { number: 127 }).ok);
  assert.strictEqual(p.Faction, ROO, 'Team ROO Headquarters joins Team ROO');
  assert.match(refusal(t, p, t.byNumber.get(145)), /other side/);
  assert.match(refusal(t, p, t.byNumber.get(146)), /other side|previous/);
  assert.strictEqual(refusal(t, p, t.byNumber.get(128)), null);

  // repeat quests (kind 1) come back the next UTC day
  const rq = t.list.find(q => q.kind === 1 && q.minLevel <= 60 && q.maxLevel >= 60 && q.prereq === 0);
  p.StepQuestsDone.push(rq.number);
  assert.match(refusal(t, p, rq), /already completed/);
  now += 86400000; h({ t: 'quest.abandon', number: 0 });
  assert.strictEqual(refusal(t, p, rq), null, 'a new day resets the repeat quests');
});

test('patrols by side: move_ROO for Team ROO, move for OMD', { skip: !gd }, () => {
  let now = 1.7e12;
  const eco = new Economy(() => {}, gd, () => now);
  const m = eco.missions();
  const p = ensureProfile(newProfile('Scout', 0, 1));
  const s = { profile: p };
  const rooRow = m.instant.findIndex(r => r && r.type === 'patrol' && r.kind === 'move_ROO');
  const omdRow = m.instant.findIndex(r => r && r.type === 'patrol' && r.kind === 'move');
  assert.ok(rooRow >= 0 && omdRow >= 0);
  p.Level = m.instant[rooRow].minLevel;
  assert.match(eco.claim(s, 'patrol', { row: rooRow, posts: 3, seconds: 60 }).error, /not for your side/);
  p.Faction = ROO;
  const ok = eco.claim(s, 'patrol', { row: rooRow, posts: 3, seconds: 60 });
  assert.ok(ok.ok, ok.error);
  now += 60000;
  p.Level = m.instant[omdRow].minLevel;
  assert.match(eco.claim(s, 'patrol', { row: omdRow, posts: 3, seconds: 60 }).error, /not for your side/);
});

test('QSRANKLIST: best delivery time per driver this week / month / year and the period before', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'dcq-'));
  const store = new Store(path.join(dir, 'q.db'));
  let now = Date.UTC(2026, 9, 21, 12);   // a Wednesday
  const quests = new Quests({ store, clock: () => now });
  const sess = name => ({ profile: ensureProfile(newProfile(name, 0, 1)) });
  const a = sess('Ann'), b = sess('Bob'), c = sess('Cat');
  quests.recordDelivery(a, { job: 'a_00041', seconds: 90 });
  quests.recordDelivery(a, { job: 'a_00041', seconds: 85 });
  quests.recordDelivery(b, { job: 'a_00041', seconds: 80 });
  quests.recordDelivery(c, { job: 'a_00042', seconds: 70 });
  quests.recordDelivery(c, { job: 'a_00041', seconds: -1 });
  const r = quests.handle(a, { t: 'qs.rank', job: 'a_00041', period: 0 });
  assert.ok(r.ok);
  assert.deepStrictEqual(r.rows.map(x => [x.rank, x.name, x.seconds]), [[1, 'Bob', 80], [2, 'Ann', 85]]);
  assert.deepStrictEqual([r.myRank, r.myBest], [2, 85]);
  // next week: this week's list is empty, the week before has them
  now += 7 * 86400000;
  assert.strictEqual(quests.handle(a, { t: 'qs.rank', job: 'a_00041', period: 0 }).rows.length, 0);
  assert.strictEqual(quests.handle(a, { t: 'qs.rank', job: 'a_00041', period: 0, previous: true }).rows.length, 2);
  assert.strictEqual(quests.handle(a, { t: 'qs.rank', job: 'a_00041', period: 2 }).rows.length, 2, 'this year');
  assert.strictEqual(periodStart(Date.UTC(2026, 9, 21, 12), 0), Date.UTC(2026, 9, 19), 'weeks start on Monday');
  assert.strictEqual(periodStart(Date.UTC(2026, 9, 21), 1, 1), Date.UTC(2026, 8, 1));
  store.db.close();
  fs.rmSync(dir, { recursive: true, force: true });
});

test('unlocks (0x703d20): level, the CHASE / ARBEIT / CITY quests, the finales, the gates', { skip: !gd }, () => {
  const { unlocksOf, cityOpen } = require('../src/quests');
  const t = stepTable(gd.dir);
  const p = ensureProfile(newProfile('Rookie', 0, 1));
  p.Level = 1;
  let u = unlocksOf(t, p);
  assert.deepStrictEqual([u.rookie, u.arbeit, u.chase, u.city2, u.city3, u.city4, u.city5], [false, false, false, false, false, false, false]);
  p.Level = 4; u = unlocksOf(t, p);
  assert.ok(u.arbeit && !u.chase && !u.rookie, 'level 4 opens the Delivery Service, not the full station panel');
  p.StepQuestsDone.push(1, 2, 3, 4, 5);
  u = unlocksOf(t, p);
  assert.ok(u.rookie && u.arbeit, 'Rookie Mission (3/3) (ARBEIT) ends the beginner panel');
  assert.ok(!u.chase);
  p.StepQuestsDone.push(9); u = unlocksOf(t, p);
  assert.ok(u.chase, 'Start Chasing! (CHASE)');
  assert.ok(!cityOpen(u, 'koinonia', p.Level) && cityOpen(u, 'moonpalace', p.Level));
  p.StepQuestsDone.push(34); u = unlocksOf(t, p);
  assert.ok(u.city2 && cityOpen(u, 'koinonia', p.Level), 'Report (CITY_2ND) opens Koinonia');
  p.Level = 46; u = unlocksOf(t, p);
  assert.ok(u.city3 && u.city4 && !u.city5, 'Cras at 29, Oros at 46; Taipei only by quest');
  assert.ok(!cityOpen({ ...u, city5: true }, 'taipei', 70) && cityOpen({ ...u, city5: true }, 'taipei', 71), 'the Taipei gate needs level 71');
  p.StepQuestsDone.push(144); u = unlocksOf(t, p);
  assert.ok(u.rooFinale && !u.omdFinale, 'q_00149 Infiltration! is the Team ROO finale');
  p.StepQuestsDone.push(162); u = unlocksOf(t, p);
  assert.ok(u.omdFinale, 'q_00167 Issue the No.9 Card is the OMD finale');
  // [rebuild] an old save past level 5 without any step quest is past the rookie missions
  const old = ensureProfile(newProfile('Veteran', 0, 1)); old.Level = 30;
  assert.ok(unlocksOf(t, old).rookie);
  old.StepQuestsDone.push(1);
  assert.ok(!unlocksOf(t, old).rookie, 'a driver on the rookie chain finishes it first');
});

test('event step quests (kind 99) run with their season; the retry bonus count', { skip: !gd }, () => {
  let now = 1.7e12;
  const quests = new Quests({ store: null, clock: () => now, gamedata: gd });
  const t = quests.table();
  const p = ensureProfile(newProfile('Eventer', 0, 1));
  p.Level = 10;
  const indy = t.byNumber.get(10045), eid = t.byNumber.get(10046);
  assert.deepStrictEqual([indy.kind, indy.group, eid.group], [99, 'event_ini_1', 'event_ini_2']);
  assert.match(refusal(t, p, indy, []), /not running/);
  assert.strictEqual(refusal(t, p, indy, ['summer']), null, 'Independence Day with the summer festival');
  assert.match(refusal(t, p, eid, ['summer']), /not running/);
  assert.strictEqual(refusal(t, p, eid, ['harvest']), null, 'Idul Fitri with the autumn harvest');
  const s = { profile: p };
  assert.match(quests.handle(s, { t: 'quest.accept', number: 10045 }).error, /not running/);
  quests.events = { seasons: () => ['summer'] };
  assert.ok(quests.handle(s, { t: 'quest.accept', number: 10045 }).ok);
  assert.strictEqual(p.StepQuestTries, 0);
  now += 2000; assert.ok(quests.handle(s, { t: 'quest.step', number: 10045, step: 0 }).ok);
  now += 2000; assert.ok(quests.handle(s, { t: 'quest.step', number: 10045, step: 0 }).ok);
  assert.strictEqual(p.StepQuestTries, 2, 'each start-over raises the bonus count');
  for (let k = 0; k < 20; k++) quests.handle(s, { t: 'quest.step', number: 10045, step: 0 });
  assert.strictEqual(p.StepQuestTries, 10, 'capped');
  assert.ok(quests.handle(s, { t: 'quest.abandon', number: 10045 }).ok);
  assert.strictEqual(p.StepQuestTries, 0);
});

test('QSRANKLIST: the crew column and MYTEAM from the crews', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'dcq-'));
  const store = new Store(path.join(dir, 'q.db'));
  const now = Date.UTC(2026, 9, 21, 12);
  const quests = new Quests({ store, clock: () => now });
  const sess = name => ({ profile: ensureProfile(newProfile(name, 0, 1)) });
  const a = sess('Ann'), b = sess('Bob');
  quests.recordDelivery(a, { job: 'a_00041', seconds: 85 });
  quests.recordDelivery(b, { job: 'a_00041', seconds: 80 });
  // without the crew tables: no crews
  assert.deepStrictEqual(quests.handle(a, { t: 'qs.rank', job: 'a_00041', period: 0 }).rows.map(r => r.team), ['', '']);
  const db = store.db;
  db.exec(`CREATE TABLE IF NOT EXISTS crews (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL UNIQUE COLLATE NOCASE, mark INTEGER NOT NULL DEFAULT 1, master INTEGER NOT NULL, created INTEGER NOT NULL);
    CREATE TABLE IF NOT EXISTS crew_members (account_id INTEGER PRIMARY KEY, crew_id INTEGER NOT NULL, rank INTEGER NOT NULL DEFAULT 3, joined INTEGER NOT NULL);`);
  for (const [id, name] of [[1, 'Ann'], [2, 'Bob']]) {
    db.prepare('INSERT INTO accounts (id, username, salt, hash, created_at) VALUES (?, ?, ?, ?, ?)').run(id, name.toLowerCase(), 's', 'h', now);
    db.prepare('INSERT INTO drivers (account_id, name, profile, updated_at) VALUES (?, ?, ?, ?)').run(id, name, '{}', now);
  }
  db.prepare('INSERT INTO crews (id, name, master, created) VALUES (1, ?, 2, ?)').run('Night Owls', now);
  db.prepare('INSERT INTO crew_members (account_id, crew_id, joined) VALUES (2, 1, ?)').run(now);
  const fresh = new Quests({ store, clock: () => now });   // the crew query is prepared on first use
  const r = fresh.handle(a, { t: 'qs.rank', job: 'a_00041', period: 0 });
  assert.deepStrictEqual(r.rows.map(x => [x.name, x.team]), [['Bob', 'Night Owls'], ['Ann', '']]);
  assert.strictEqual(r.myTeam, '');
  assert.strictEqual(fresh.handle(b, { t: 'qs.rank', job: 'a_00041', period: 0 }).myTeam, 'Night Owls');
  store.db.close();
  fs.rmSync(dir, { recursive: true, force: true });
});
