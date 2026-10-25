// Password hashing (scrypt, async so a wrong password never blocks the event loop) and input rules
// for account and driver names.
'use strict';
const crypto = require('node:crypto');
const { promisify } = require('node:util');
const scrypt = promisify(crypto.scrypt);

const USER_RE = /^[A-Za-z0-9_]{3,16}$/;
const DRIVER_RE = /^[A-Za-z0-9_\-]{2,16}$/;

async function hashPassword(password, salt = crypto.randomBytes(16).toString('hex')) {
  const hash = (await scrypt(password, salt, 32)).toString('hex');
  return { salt, hash };
}

async function verifyPassword(password, salt, hash) {
  const candidate = await scrypt(password, salt, 32);
  const expected = Buffer.from(hash, 'hex');
  return candidate.length === expected.length && crypto.timingSafeEqual(candidate, expected);
}

function validUsername(u) { return typeof u === 'string' && USER_RE.test(u); }
function validPassword(p) { return typeof p === 'string' && p.length >= 6 && p.length <= 64; }
function validDriverName(n) { return typeof n === 'string' && DRIVER_RE.test(n); }

module.exports = { hashPassword, verifyPassword, validUsername, validPassword, validDriverName };
