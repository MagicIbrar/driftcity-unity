"""Reference re-implementation of DriftCity.exe's TCP stream cipher.

Reconstructed from (VAs in DriftCity.exe, sha256 2007a80c...95b0):
  0x7a3f20  handshake key setup (consumes the server's 56-byte hello)
  0x7a3e20  XOR-merge of one 28-byte hello record into the local defaults
  0x7ad920  per-direction cipher init
  0x7ad690  encrypt (byte path)      0x7ad580  encrypt (dword fast path)
  0x7ad7f0  decrypt
  0x7ad480  TEA block encrypt (32 rounds, delta 0x9E3779B9)
  0x79f960 / 0x79f9b0 / 0x79fa70   MT19937 init_genrand / init_by_array / genrand_int32

Each direction keeps an 8-byte feedback register R, a TEA input/output block O,
and an MT19937 that yields an extra 8-byte mask per block. For every byte:
    out = in ^ R[pos] ^ mask[pos];  R[pos] = plaintext byte
After 8 bytes the block is refreshed according to `mode` (1 = XOR chain, 2 = TEA).

Handshake (0x7a5260 / 0x7a3e90 / 0x7a3de0 / 0x7a43a0): on connect each peer builds a
random 56-byte hello = two 28-byte records {iv0, iv1, key[4], mode} (MT19937 seeded with
GetTickCount), sends it UNENCRYPTED, then reads the peer's 56 bytes and XOR-merges each
record (merge_hello_record). The side with the role flag (ctx+0x147c) dictates the modes
from its config bits (0x2/0x4 -> record 0 mode 1/2, 0x10/0x20 -> record 1 mode 1/2); the
role flag also decides which record is send vs recv. Because both hellos cross the wire
in clear, a passive capture is enough to derive both keys.

This is a static reading; it has not been checked against live traffic.
"""
import struct

M32 = 0xFFFFFFFF


class MT19937:
    def __init__(self):
        self.mt = [0] * 624
        self.idx = 625

    def seed(self, s):  # 0x79f960
        self.mt[0] = s & M32
        for i in range(1, 624):
            self.mt[i] = ((self.mt[i - 1] ^ (self.mt[i - 1] >> 30)) * 0x6C078965 + i) & M32
        self.idx = 624

    def seed_by_array(self, key):  # 0x79f9b0
        self.seed(19650218)
        i, j = 1, 0
        for _ in range(max(624, len(key))):
            prev = self.mt[i - 1]
            self.mt[i] = (((prev ^ (prev >> 30)) * 0x19660D ^ self.mt[i]) + key[j] + j) & M32
            i += 1
            j += 1
            if i >= 624:
                self.mt[0] = self.mt[623]
                i = 1
            if j >= len(key):
                j = 0
        for _ in range(623):
            prev = self.mt[i - 1]
            self.mt[i] = (((prev ^ (prev >> 30)) * 0x5D588B65 ^ self.mt[i]) - i) & M32
            i += 1
            if i >= 624:
                self.mt[0] = self.mt[623]
                i = 1
        self.mt[0] = 0x80000000
        self.idx = 624

    def next(self):  # 0x79fa70
        if self.idx >= 624:
            if self.idx == 625:
                self.seed(5489)
            mag = (0, 0x9908B0DF)
            for k in range(624):
                y = (self.mt[k] & 0x80000000) | (self.mt[(k + 1) % 624] & 0x7FFFFFFF)
                self.mt[k] = self.mt[(k + 397) % 624] ^ (y >> 1) ^ mag[y & 1]
            self.idx = 0
        y = self.mt[self.idx]
        self.idx += 1
        y ^= y >> 11
        y ^= (y << 7) & 0x9D2C5680
        y ^= (y << 15) & 0xEFC60000
        return y ^ (y >> 18)


def tea_encrypt(v0, v1, k):  # 0x7ad480
    s = 0
    for _ in range(32):
        s = (s + 0x9E3779B9) & M32
        v0 = (v0 + ((((v1 >> 5) + k[1]) & M32) ^ ((v1 << 4) + k[0] & M32) ^ ((s + v1) & M32))) & M32
        v1 = (v1 + ((((v0 >> 5) + k[3]) & M32) ^ ((v0 << 4) + k[2] & M32) ^ ((s + v0) & M32))) & M32
    return v0, v1


class StreamCipher:
    """One direction. The client keeps one of these for send and one for recv."""

    def __init__(self, iv0, iv1, key4, mode):  # 0x7ad920
        self.mode = mode
        self.reg = bytearray(struct.pack("<II", iv0, iv1))  # ctx[0..1]
        self.out = [iv0, iv1]  # ctx[4..5]
        self.pos = 0
        self.mt = MT19937()
        self.mt.seed_by_array(list(key4))
        self._new_mask()

    def _new_mask(self):
        self.mask = struct.pack("<II", self.mt.next(), self.mt.next())

    def _refresh(self):
        r0, r1 = struct.unpack("<II", self.reg)
        if self.mode == 2:
            # TEA key is the context head {R0, R1, R0, R1} (ctx[2..3] = copy of R)
            o = tea_encrypt(self.out[0], self.out[1], (r0, r1, r0, r1))
        else:  # mode 1
            o = (self.out[0] ^ r0, self.out[1] ^ r1)
        self.out = list(o)
        self.reg = bytearray(struct.pack("<II", *o))
        self.pos = 0
        self._new_mask()

    def _xcrypt(self, data, encrypt):
        if not self.mode:
            return bytes(data)
        buf = bytearray(data)
        for i, b in enumerate(buf):
            o = b ^ self.reg[self.pos] ^ self.mask[self.pos]
            buf[i] = o
            self.reg[self.pos] = b if encrypt else o  # register always holds plaintext
            self.pos += 1
            if self.pos == 8:
                self._refresh()
        return bytes(buf)

    def encrypt(self, data):
        return self._xcrypt(data, True)

    def decrypt(self, data):
        return self._xcrypt(data, False)


def merge_hello_record(local7, remote7):  # 0x7a3e20
    """XOR one 28-byte server hello record {iv0, iv1, key[4], flags} into local defaults."""
    out = [a ^ b for a, b in zip(local7[:6], remote7[:6])] + [local7[6]]
    if (remote7[6] | local7[6]) & 2:
        out[2] ^= 0x400
        out[3] ^= 0x300
        out[4] ^= 800
        out[5] ^= 600
    return out


if __name__ == "__main__":
    import os

    for mode in (1, 2):
        key = (0x11111111, 0x22222222, 0x33333333, 0x44444444)
        tx = StreamCipher(0xDEADBEEF, 0x01234567, key, mode)
        rx = StreamCipher(0xDEADBEEF, 0x01234567, key, mode)
        msgs = [os.urandom(n) for n in (1, 7, 8, 13, 64, 3, 500)]
        for m in msgs:
            assert rx.decrypt(tx.encrypt(m)) == m
    # MT19937 sanity check against the reference first output for seed 5489
    mt = MT19937()
    assert mt.next() == 3499211612
    print("round-trip OK for modes 1 and 2; MT19937 matches reference")
