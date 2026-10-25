'use strict';
// The Auction House (src/auction.js), note attachments (src/mail.js) and the custom-shop extras (src/vshop.js).
const { test, before, after } = require('node:test');
const assert = require('node:assert');
const os = require('node:os');
const path = require('node:path');
const fs = require('node:fs');
const WebSocket = require('ws');
const { createServer, PROTOCOL } = require('../server');
const items = require('../src/items');

let server, port, dir, offset = 0;
const clock = () => Date.now() + offset;

before(async () => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), 'dcauction-'));
  server = createServer({ port: 0, db: path.join(dir, 'test.db'), quiet: !process.env.DC_TEST_LOG, clock, maxConnPerIp: 200, gamedata: null, auctionNpc: 0 });
  port = await server.listen();
});
after(async () => { await server.close(); fs.rmSync(dir, { recursive: true, force: true }); });

function connect() {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(`ws://127.0.0.1:${port}`);
    const pending = new Map(), pushed = [], waiters = [];
    let rid = 1;
    ws.on('message', raw => {
      const m = JSON.parse(raw);
      if (m.t === 'reply' && pending.has(m.rid)) { pending.get(m.rid)(m); pending.delete(m.rid); return; }
      const w = waiters.find(x => x.pred(m));
      if (w) { waiters.splice(waiters.indexOf(w), 1); clearTimeout(w.timer); w.resolve(m); } else pushed.push(m);
    });
    ws.on('error', reject);
    const c = {
      req(t, body = {}) { const id = rid++; return new Promise(r => { pending.set(id, r); ws.send(JSON.stringify({ t, rid: id, ...body })); }); },
      wait(pred, ms = 3000) {
        const hit = pushed.find(pred);
        if (hit) { pushed.splice(pushed.indexOf(hit), 1); return Promise.resolve(hit); }
        return new Promise((res, rej) => { const w = { pred, resolve: res }; waiters.push(w); w.timer = setTimeout(() => { waiters.splice(waiters.indexOf(w), 1); rej(new Error('timeout')); }, ms); });
      },
      close() { ws.close(); },
    };
    ws.on('open', () => c.wait(m => m.t === 'welcome').then(() => resolve(c)));
  });
}

async function driver(user, name) {
  const c = await connect();
  assert.ok((await c.req('register', { protocol: PROTOCOL, user, pass: 'secret123' })).ok);
  assert.ok((await c.req('driver.create', { name, car: 0, color: 1 })).ok);
  return c;
}
const live = name => server.world.findByName(name);

test('auction: register (10% tax), list, buy-out paid by note, own listing refused', async () => {
  const a = await driver('aucseller', 'AucSeller'), b = await driver('aucbuyer', 'AucBuyer');
  const pa = live('AucSeller').profile, pb = live('AucBuyer').profile;
  items.give(pa, 'i_n_00003', 5);
  pa.PerfParts.push({ Uid: 41, Type: 1, Grade: 1, Enhance: 3 });
  const mito0 = pa.Mito;

  let r = await a.req('auction.register', { item: { kind: 'stack', id: 'i_n_00003' }, count: 3, price: 20000 });
  assert.ok(r.ok, r.error);
  assert.strictEqual(r.fee, 2000, 'registration tax 10% (0x5c9e61)');
  assert.strictEqual(r.driver.Mito, mito0 - 2000);
  assert.strictEqual(items.count(pa, 'i_n_00003'), 2, 'the quantity is held by the auction');
  const stackId = r.id;
  r = await a.req('auction.register', { item: { kind: 'stack', id: 'i_n_00003' }, count: 5, price: 1000 });
  assert.strictEqual(r.ok, false);
  assert.match(r.error, /more items than you own/, '10443');
  r = await a.req('auction.register', { item: { kind: 'part', uid: 41 }, price: 3000000000 });
  assert.strictEqual(r.ok, false, 'over 2 billion (0x5517ce)');
  r = await a.req('auction.register', { item: { kind: 'part', uid: 41 }, price: 150000 });
  assert.ok(r.ok, r.error);
  assert.ok(!pa.PerfParts.some(x => x.Uid === 41));

  r = await b.req('auction.list', {});
  assert.ok(r.ok);
  assert.strictEqual(r.total, 2);
  assert.strictEqual(r.rows[0].seller, 'AucSeller');
  r = await b.req('auction.list', { category: 3 });
  assert.deepStrictEqual(r.rows.map(x => x.name), ['Accel Part S+3'], 'category 3 = acceleration parts');
  assert.strictEqual(r.rows[0].level, 20);
  r = await b.req('auction.list', { levelFrom: 10 });
  assert.strictEqual(r.total, 1);
  r = await b.req('auction.list', { seller: 'nobody' });
  assert.strictEqual(r.total, 0);
  r = await b.req('auction.list', { vehicles: true });
  assert.strictEqual(r.total, 0, 'the vehicle tab lists cars only');

  r = await a.req('auction.buy', { id: stackId });
  assert.strictEqual(r.ok, false);
  assert.match(r.error, /registered/, '10499');

  const buyer0 = pb.Mito;
  r = await b.req('auction.buy', { id: stackId });
  assert.ok(r.ok, r.error);
  assert.strictEqual(r.driver.Mito, buyer0 - 20000);
  assert.strictEqual(items.count(pb, 'i_n_00003'), 3);
  r = await b.req('auction.buy', { id: stackId });
  assert.strictEqual(r.ok, false, 'sold once');
  await a.wait(m => m.t === 'auction.sold' && m.id === stackId);
  const note = await a.wait(m => m.t === 'note.new' && m.note.mito === 20000);
  const before = pa.Mito;
  r = await a.req('note.take', { id: note.note.id });
  assert.ok(r.ok, r.error);
  assert.strictEqual(r.driver.Mito, before + 20000, 'the seller takes the price from the note');

  r = await a.req('auction.mine', {});
  assert.deepStrictEqual(r.rows.map(x => x.status).sort(), ['open', 'sold']);
  r = await b.req('auction.list', { sort: 'user' });
  assert.strictEqual(r.rows[0].credit, 1, 'credit = the seller\'s sales');
  a.close(); b.close();
});

test('auction: cancel returns the goods; bids hold Mito, outbid by note, the best bid wins at expiry', async () => {
  const a = await driver('aucbid1', 'AucBidA'), b = await driver('aucbid2', 'AucBidB'), c = await driver('aucbid3', 'AucBidC');
  const pa = live('AucBidA').profile, pb = live('AucBidB').profile, pc = live('AucBidC').profile;
  items.give(pa, 'i_n_Seed_N', 4);
  let r = await a.req('auction.register', { item: { kind: 'stack', id: 'i_n_Seed_N' }, count: 2, price: 50000 });
  r = await a.req('auction.cancel', { id: r.id });
  assert.ok(r.ok && r.returned, r.error);
  assert.strictEqual(items.count(pa, 'i_n_Seed_N'), 4);

  r = await a.req('auction.register', { item: { kind: 'stack', id: 'i_n_Seed_N' }, count: 4, price: 90000, start: 10000, hours: 1 });
  assert.ok(r.ok, r.error);
  assert.strictEqual(r.fee, 9000, 'the tax is on the buy-out price');
  const id = r.id;
  const b0 = pb.Mito, c0 = pc.Mito;
  r = await b.req('auction.bid', { id, amount: 5000 });
  assert.strictEqual(r.ok, false, 'below the opening bid');
  r = await b.req('auction.bid', { id, amount: 10000 });
  assert.ok(r.ok, r.error);
  assert.strictEqual(pb.Mito, b0 - 10000, 'the bid is held');
  r = await c.req('auction.bid', { id, amount: 10200 });
  assert.strictEqual(r.ok, false, 'a new bid beats the old one by 5%');
  r = await c.req('auction.bid', { id, amount: 12000 });
  assert.ok(r.ok, r.error);
  await b.wait(m => m.t === 'auction.outbid' && m.id === id);
  const refund = await b.wait(m => m.t === 'note.new' && m.note.mito === 10000);
  assert.ok((await b.req('note.take', { id: refund.note.id })).ok);
  assert.strictEqual(pb.Mito, b0, 'the outbid driver gets the bid back');

  offset += 2 * 3600000;   // past the end
  r = await c.req('auction.mine', {});
  const won = r.rows.find(x => x.id === id);
  assert.strictEqual(won.status, 'won');
  assert.strictEqual(pc.Mito, c0 - 12000);
  r = await c.req('auction.take', { id });
  assert.ok(r.ok, r.error);
  assert.strictEqual(items.count(pc, 'i_n_Seed_N'), 4);
  r = await c.req('auction.take', { id });
  assert.strictEqual(r.ok, false, 'taken once');
  const paid = await a.wait(m => m.t === 'note.new' && m.note.mito === 12000);
  assert.match(paid.note.content, /AucBidC/);

  // an unsold listing expires and waits for its seller
  items.give(pa, 'i_n_00016', 1);
  r = await a.req('auction.register', { item: { kind: 'stack', id: 'i_n_00016' }, price: 7000, hours: 1 });
  assert.ok(r.ok, r.error);
  const lone = r.id;
  offset += 2 * 3600000;
  r = await a.req('auction.mine', {});
  assert.strictEqual(r.rows.find(x => x.id === lone).status, 'expired');
  assert.ok((await a.req('auction.take', { id: lone })).ok);
  assert.strictEqual(items.count(pa, 'i_n_00016'), 1);
  a.close(); b.close(); c.close();
});

test('auction: car rules (current car 10577, fitted parts 10578, resale 10586, level 10580)', async () => {
  const a = await driver('auccar1', 'AucCarA'), b = await driver('auccar2', 'AucCarB');
  const pa = live('AucCarA').profile, pb = live('AucCarB').profile;
  pa.Cars.push({ ...JSON.parse(JSON.stringify(pa.Cars[0])), VehicleIndex: 5 });
  pa.CurrentCar = 0;
  let r = await a.req('auction.register', { item: { kind: 'car', slot: 0 }, price: 100000 });
  assert.strictEqual(r.ok, false);
  assert.match(r.error, /currently driving/);
  pa.Cars[1].Wing = 1234;
  r = await a.req('auction.register', { item: { kind: 'car', slot: 1 }, price: 100000 });
  assert.match(r.error, /equipped parts/);
  pa.Cars[1].Wing = -1;
  r = await a.req('auction.register', { item: { kind: 'car', slot: 1 }, price: 100000 });
  assert.ok(r.ok, r.error);
  assert.strictEqual(pa.Cars.length, 1);
  const id = r.id;
  r = await b.req('auction.list', { vehicles: true });
  assert.strictEqual(r.total, 1);
  assert.strictEqual(r.rows[0].kind, 'car');
  r = await b.req('auction.buy', { id });
  assert.match(r.error, /Insufficient Mito/, '10500');
  pb.Mito = 500000;
  r = await b.req('auction.buy', { id });
  assert.ok(r.ok, r.error);
  assert.strictEqual(pb.Cars.length, 2);
  assert.strictEqual(pb.Cars[1].VehicleIndex, 5);
  assert.strictEqual(pb.Cars[1].FromAuction, true);
  pb.CurrentCar = 0;
  r = await b.req('auction.register', { item: { kind: 'car', slot: 1 }, price: 100000 });
  assert.match(r.error, /cannot be resold/, '10586');
  a.close(); b.close();
});

test('notes carry items; gifts, gift list, history, mileage and wish lists', async () => {
  const a = await driver('giftfrom', 'GiftFrom'), b = await driver('giftto', 'GiftTo');
  const pa = live('GiftFrom').profile, pb = live('GiftTo').profile;
  pa.Level = 10;
  let r = await a.req('gift.send', { to: 'GiftTo', item: { kind: 'shop', itemId: 10007, price: 3000, name: 'Vanilla white' }, comment: 'enjoy!' });
  assert.ok(r.ok, r.error);
  assert.strictEqual(r.price, 3000);
  const n = await b.wait(m => m.t === 'note.new' && m.note.item);
  assert.match(n.note.content, /GiftFrom sent you a gift: Vanilla white\. "enjoy!"/);
  await b.wait(m => m.t === 'gift.new');
  r = await b.req('gift.list', {});
  assert.strictEqual(r.pending.length, 1);
  assert.strictEqual(r.received[0].other, 'GiftFrom');
  r = await b.req('gift.accept', { id: r.pending[0].id });
  assert.ok(r.ok, r.error);
  assert.ok(pb.Inventory.some(i => i.ItemId === 10007));
  r = await b.req('gift.list', {});
  assert.strictEqual(r.pending.length, 0);
  r = await a.req('gift.send', { to: 'GiftFrom', item: { kind: 'shop', itemId: 10007, price: 3000 } });
  assert.strictEqual(r.ok, false, 'not to yourself');
  r = await b.req('gift.send', { to: 'GiftFrom', item: { kind: 'shop', itemId: 10007, price: 3000 } });
  assert.match(r.error, /level 5/, 'gift level');

  // a custom part gift lands on the receiver's current car
  r = await a.req('gift.send', { to: 'GiftTo', item: { kind: 'vpart', item: 5555, price: 20000, slot: 'wing' } });
  assert.ok(r.ok, r.error);
  const pn = await b.wait(m => m.t === 'note.new' && m.note.item && m.note.item.kind === 'vpart');
  r = await b.req('note.take', { id: pn.note.id });
  assert.ok(r.ok, r.error);
  assert.ok(pb.Cars[pb.CurrentCar].Parts.includes(5555));
  assert.ok(pb.Cars[pb.CurrentCar].GiftParts.includes(5555), 'given parts add nothing to the resale price');

  // mileage: 1 point per 100 Mito spent; a tenth of the Mito price buys the item
  pa.Mito = 1000000;
  r = await a.req('shop.buyPart', { slot: 0, item: 7001, part: 'wing', price: 50000 });
  assert.ok(r.ok, r.error);
  assert.strictEqual(r.driver.Mileage, 200 + 30 + 500, 'two gifts (30 + 200) and the part (500)');
  r = await a.req('shop.mileage', { slot: 0, item: { kind: 'vpart', item: 7002, price: 20000, slot: 'wing' } });
  assert.strictEqual(r.ok, false, '2,000 points needed, 730 held');
  pa.Mileage = 2500;
  r = await a.req('shop.mileage', { slot: 0, item: { kind: 'vpart', item: 7002, price: 20000, slot: 'wing' } });
  assert.ok(r.ok, r.error);
  assert.strictEqual(r.mileage, 2000);
  assert.strictEqual(pa.Mileage, 500);
  assert.strictEqual(pa.Cars[0].Wing, 7002);
  r = await a.req('shop.history', {});
  assert.deepStrictEqual(r.bought.map(x => x.currency), ['mileage', 'mito']);
  assert.deepStrictEqual(r.sent.map(x => x.other), ['GiftTo', 'GiftTo']);
  r = await a.req('wish.set', { items: [10007, 10007, 7001, 'x'], cars: [3] });
  assert.deepStrictEqual(r.driver.WishList, [10007, 7001]);
  assert.deepStrictEqual(r.driver.WishCars, [3]);
  a.close(); b.close();
});
