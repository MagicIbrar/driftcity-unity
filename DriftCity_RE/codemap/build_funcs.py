"""Function table of DriftCity.exe: entries from Ghidra (procedures_raw.json), bodies from the linear disassembly
(text.asm). For each function: size, direct callees, referenced data addresses (strings), immediate constants."""
import json, re, bisect, os, sys, pefile

HERE = os.path.dirname(os.path.abspath(__file__))
SCR = os.path.dirname(HERE)
EXE = r"D:\Drift_City_Remastered_Alpha_08-2025\Drift City Remastered Alpha\DriftCity.exe"

procs = json.load(open(os.path.join(HERE, 'procedures_raw.json')))
entries = {int(p['address'], 16) for p in procs if not p['address'].startswith('EXTERNAL') and p['address'].startswith('0x')}
if os.path.exists(os.path.join(HERE, 'extra_entries.json')):
    entries |= {int(a, 16) for a in json.load(open(os.path.join(HERE, 'extra_entries.json')))}
entries = sorted(entries)
names = {int(p['address'], 16): p['value'] for p in procs if p['address'].startswith('0x')}
externals = {p['address']: p['value'] for p in procs if p['address'].startswith('EXTERNAL')}

pe = pefile.PE(EXE, fast_load=True)
pe.parse_data_directories(directories=[pefile.DIRECTORY_ENTRY['IMAGE_DIRECTORY_ENTRY_IMPORT']])
img = pe.get_memory_mapped_image()
BASE = 0x400000
imports = {}
for e in getattr(pe, 'DIRECTORY_ENTRY_IMPORT', []):
    for i in e.imports:
        imports[i.address] = f"{e.dll.decode()}::{(i.name or b'ord').decode()}"


def cstr(va, maxlen=200):
    o = va - BASE
    if o < 0 or o >= len(img): return None
    end = img.find(b'\0', o, o + maxlen)
    if end <= o: return None
    s = img[o:end]
    if len(s) >= 3 and all(32 <= c < 127 or c in (9, 10, 13) for c in s): return s.decode()
    # UTF-16
    e2 = o
    while e2 + 1 < len(img) and e2 - o < maxlen * 2 and not (img[e2] == 0 and img[e2 + 1] == 0): e2 += 2
    s2 = img[o:e2]
    if len(s2) >= 6 and all(s2[k + 1] == 0 and 32 <= s2[k] < 127 for k in range(0, len(s2), 2)): return s2.decode('utf-16le')
    return None


TEXT_END = 0x859000
funcs = {e: {'entry': e, 'name': names.get(e, ''), 'insns': 0, 'calls': set(), 'icalls': set(), 'strings': set(), 'consts': set(), 'data': set()} for e in entries}
hexre = re.compile(r'0x([0-9a-f]+)')
cur = None
with open(os.path.join(SCR, 'text.asm')) as f:
    for line in f:
        sp = line.find(' ')
        a = int(line[:sp], 16)
        i = bisect.bisect_right(entries, a) - 1
        if i < 0: continue
        fn = funcs[entries[i]]
        nxt = entries[i + 1] if i + 1 < len(entries) else TEXT_END
        if a >= nxt: continue
        rest = line[sp + 1:].strip()
        if rest.startswith('int3'): continue
        fn['insns'] += 1
        mn = rest.split(' ', 1)[0]
        for h in hexre.findall(rest):
            v = int(h, 16)
            if mn == 'call' and 0x401000 <= v < TEXT_END and '[' not in rest:
                fn['calls'].add(v); continue
            if 'dword ptr [' + '0x' + h + ']' in rest and mn == 'call':
                if v in imports: fn['icalls'].add(imports[v])
                continue
            if 0x859000 <= v < 0x981000:
                s = cstr(v)
                if s: fn['strings'].add(s)
                else: fn['data'].add(v)
            elif v >= 0x100 and not (0x401000 <= v < TEXT_END):
                fn['consts'].add(v)

out = []
for k, e in enumerate(entries):
    fn = funcs[e]
    end = entries[k + 1] if k + 1 < len(entries) else TEXT_END
    out.append({'entry': hex(e), 'size': end - e, 'insns': fn['insns'], 'name': fn['name'],
                'calls': sorted(hex(c) for c in fn['calls']), 'icalls': sorted(fn['icalls']),
                'strings': sorted(fn['strings'])[:60], 'consts': sorted(fn['consts'])[:200], 'data': sorted(hex(d) for d in fn['data'])[:80]})
json.dump(out, open(os.path.join(HERE, 'funcs.json'), 'w'))
print(len(out), 'functions;', sum(o['size'] for o in out), 'bytes;', sum(1 for o in out if o['insns'] > 0), 'with code')
