"""What the Unity port already uses: game.nui dialogs (opened by id or their ID_ control names referenced) and data
files (named in C#), against what the exe uses. Writes unity_usage.json."""
import json, os, re, glob, collections, subprocess

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = r"D:\Drift_City_Remastered_Alpha_08-2025\DriftCityOffline\Assets\DriftCity"
HARN = r"D:\Drift_City_Remastered_Alpha_08-2025\DriftCityStaging\Harness\bin\Release\net8.0\Harness.exe"
ROOT = r"D:\Drift_City_Remastered_Alpha_08-2025\Drift City Remastered Alpha"

code = ""
for p in glob.glob(os.path.join(SRC, '**', '*.cs'), recursive=True):
    if os.sep + 'Tests' + os.sep in p or os.sep + 'Editor' + os.sep in p: continue
    code += open(p, encoding='utf-8', errors='ignore').read() + "\n"
code_l = code.lower()

# dialog -> control id names
ctl = collections.defaultdict(list)
out = subprocess.run([HARN, ROOT, 'control', '0', 'ID_'], capture_output=True, text=True, stdin=subprocess.DEVNULL).stdout
for line in out.splitlines():
    m = re.match(r'(\d+) (ID_[A-Z0-9_]+):', line)
    if m: ctl[int(m.group(1))].append(m.group(2))
dialogs = {}
for line in open(os.path.join(HERE, 'dialogs.tsv')):
    p = line.split('\t')
    if p[0].strip().isdigit(): dialogs[int(p[0])] = p[1].strip()

F = json.load(open(os.path.join(HERE, 'funcs.json')))
C = json.load(open(os.path.join(HERE, 'classified.json')))
exe_bytes = collections.Counter(); exe_sub = collections.defaultdict(collections.Counter)
for f, c in zip(F, C):
    for k in set(v // 10000 * 10000 for v in f['consts'] if v >= 20000 and v // 10000 * 10000 in dialogs and v - v // 10000 * 10000 < 2000):
        exe_bytes[k] += c['size']; exe_sub[k][c['sub']] += c['size']

dl = []
for d, key in sorted(dialogs.items()):
    names = ctl.get(d, [])
    opened = re.search(r'Open\(\s*%d\s*\)' % d, code) is not None or re.search(r'\b%d\b' % d, code) is not None
    used_ctl = [n for n in names if n in code]
    dl.append({'id': d, 'key': key, 'controls': len(names), 'used_controls': len(used_ctl), 'opened': opened,
               # a dialog without controls (DIALOG130000) only matches bit-flag constants: no exe code drives it
               'exe_bytes': exe_bytes.get(d, 0) if names else 0, 'sub': exe_sub[d].most_common(1)[0][0] if exe_sub[d] else None})

# data files named in the exe vs in our code
files = set()
for f in F:
    for s in f['strings']:
        m = re.search(r'([A-Za-z0-9_\-\.%]+)\.(nif|dds|xlt|ini|tdf|hit|kf|wav|mp3|nxt|txt|agt|lof|loi|lf|lbf|tcs|chpath|qpos|pcs|dat|llf|lgf)\b', s, re.I)
        if m: files.add(m.group(0).lower())
fl = [{'file': f, 'used': (f in code_l) or (('%' not in f) and f.split('.')[0] in code_l)} for f in sorted(files)]
json.dump({'dialogs': dl, 'files': fl}, open(os.path.join(HERE, 'unity_usage.json'), 'w'))
used_d = [x for x in dl if x['opened'] or x['used_controls']]
drv = [x for x in dl if x['exe_bytes']]
print('dialogs', len(dl), 'used by Unity', len(used_d), '| driven by exe code', len(drv), 'of which used', sum(1 for x in drv if x['opened'] or x['used_controls']))
print('exe-weighted dialog coverage', sum(x['exe_bytes'] for x in drv if x['opened'] or x['used_controls']) / max(1, sum(x['exe_bytes'] for x in drv)))
print('data files named in exe', len(fl), 'named in Unity code', sum(1 for x in fl if x['used']))
