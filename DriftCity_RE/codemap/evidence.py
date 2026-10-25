"""Per-function evidence: UI dialog keys (game.nui ids among the constants), packet handlers (0x519120 table),
studied addresses (cited in DriftCity_RE/*.md), data files, text ids. Writes evidence.json."""
import json, re, os, glob, collections

HERE = os.path.dirname(os.path.abspath(__file__))
RE = r"D:\Drift_City_Remastered_Alpha_08-2025\Drift City Remastered Alpha\DriftCity_RE"
F = json.load(open(os.path.join(HERE, 'funcs.json')))
entries = [int(f['entry'], 16) for f in F]

dialogs = {}
for line in open(os.path.join(HERE, 'dialogs.tsv')):
    p = line.split('\t')
    if len(p) >= 2 and p[0].strip().isdigit(): dialogs[int(p[0])] = p[1].strip()

# packet handlers: FUN_0079f390(id, handler) in the registration function
handlers = {}
src = open(os.path.join(RE, 'decompiled', '519120_register_handlers.c')).read()
for m in re.finditer(r'FUN_0079f390\((0x[0-9a-f]+|\d+),\s*&?(?:LAB|FUN)_([0-9a-f]+)\)', src):
    handlers[int(m.group(2), 16)] = int(m.group(1), 0)

# addresses cited in our RE notes (functions studied for the port)
studied = collections.defaultdict(set)
for md in glob.glob(os.path.join(RE, '*.md')):
    doc = os.path.basename(md)[:-3]
    for m in re.finditer(r'0x([4-8][0-9a-f]{5})\b', open(md, encoding='utf-8', errors='ignore').read()):
        studied[int(m.group(1), 16)].add(doc)
for c in glob.glob(os.path.join(RE, 'decompiled', 'phys', '0x*.c')):
    studied[int(os.path.basename(c)[2:-2], 16)].add('decompiled')

import bisect
def owner(a):
    i = bisect.bisect_right(entries, a) - 1
    return i

ev = []
for i, f in enumerate(F):
    e = int(f['entry'], 16)
    keys = collections.Counter()
    for c in f['consts']:
        d = c // 10000 * 10000
        # text ids (10000-11999) are pushed as plain numbers too: LOGIN's controls only count when exact
        if d == 10000 and c != 10000: continue
        # 20000-21999 are server message text ids (globaltext "20001 No corresponding ticket found"), 13xxxx item/sound ids
        if 20000 <= c < 22000: keys['@servermsg'] += 1; continue
        if d == 130000: continue
        if d in dialogs and c - d < 2000 and c >= 20000:
            # small dialog ids collide with plain numbers (30000 ms, 60000 ...): only their control ids count there
            if d < 200000 and c == d: continue
            keys[dialogs[d]] += 1
    files = [s for s in f['strings'] if re.search(r'(?i)\.(nif|dds|xlt|ini|tdf|hit|kf|wav|mp3|nxt|txt|agt|lof|loi|lf|lbf|tcs)\b|^(Init|World|init|effects|vehicle|audio|game)[/\\]', s)]
    textids = [s for s in f['strings'] if re.fullmatch(r'1[01]\d{3}', s)]
    for k in [k for k in keys if not k.startswith('@')]:
        d = next((i for i, n in dialogs.items() if n == k), 10**9)
        if d < 200000 and keys[k] < 2: del keys[k]
    ev.append({'entry': f['entry'], 'size': f['size'], 'ui': dict(keys.most_common(4)), 'handler': handlers.get(e), 'files': files[:10], 'textids': len(textids)})

for a, docs in studied.items():
    i = owner(a)
    if i >= 0: ev[i].setdefault('studied', set()).update(docs)
for x in ev:
    if 'studied' in x: x['studied'] = sorted(x['studied'])
json.dump(ev, open(os.path.join(HERE, 'evidence.json'), 'w'))
print('dialogs', len(dialogs), 'handlers', len(handlers), 'studied addrs', len(studied),
      'funcs with UI', sum(1 for x in ev if x['ui']), 'studied funcs', sum(1 for x in ev if 'studied' in x))
