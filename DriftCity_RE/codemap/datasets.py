"""Dashboard datasets (ds_summary / ds_subsystems / ds_missing_dialogs) from codemap.json and unity_usage.json."""
import json, os

HERE = os.path.dirname(os.path.abspath(__file__))
M = json.load(open(os.path.join(HERE, 'codemap.json')))
U = json.load(open(os.path.join(HERE, 'unity_usage.json')))
sm, rows = M['summary'], M['subsystems']
game = [r for r in rows if r['status'] != 'replaced']
summary = dict(sm)
summary['converted_kb'] = round(sum(r['kb'] * r['converted'] for r in game), 1)
summary = {k: summary[k] for k in ['total_kb', 'game_kb', 'replaced_kb', 'converted_pct', 'converted_kb', 'functions', 'studied_functions',
                                   'dialogs_total', 'dialogs_used', 'files_total', 'files_used']}
subs = []
for r in rows:
    c = r['converted']
    subs.append({'subsystem': r['subsystem'], 'status': r['status'], 'kb': r['kb'], 'functions': r['functions'],
                 'converted_pct': None if c is None else round(c * 100), 'done_kb': None if c is None else round(r['kb'] * c, 1),
                 'todo_kb': None if c is None else round(r['kb'] * (1 - c), 1), 'studied_pct': r['studied'], 'dialog_cov_pct': r['dialog_cov'],
                 'unity': r['unity'], 'gaps': r['gaps'], 'address_bands': ' '.join(r['ranges'])})
missing = []
for d in U['dialogs']:
    if d['exe_bytes'] and d['sub'] and not (d['opened'] or d['used_controls']):
        missing.append({'dialog': f"{d['key']} {d['id']}", 'key': d['key'], 'id': d['id'], 'subsystem': d['sub'],
                        'exe_kb': round(d['exe_bytes'] / 1024, 1), 'controls': d.get('controls', 0)})
missing.sort(key=lambda x: -x['exe_kb'])
json.dump([summary], open(os.path.join(HERE, 'ds_summary.json'), 'w'))
json.dump(subs, open(os.path.join(HERE, 'ds_subsystems.json'), 'w'))
json.dump(missing[:80], open(os.path.join(HERE, 'ds_missing_dialogs.json'), 'w'))
print(summary)
print(len(subs), len(missing))
