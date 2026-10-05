from pathlib import Path
import re, collections
root=Path(r'C:\Users\spnka\Desktop\ChatGPT Projects\Spankyho Optimizer\src\SpankyhoOptimizer')
files=list(root.rglob('*.cs'))
texts={p:p.read_text(encoding='utf-8',errors='replace') for p in files}
pat_id=re.compile(r'TweakFactory\.(?:Dword|Command)\(\s*"([^"]+)"')
ids=[]
for p,s in texts.items():
    for m in pat_id.finditer(s): ids.append((m.group(1),p.name,s.count('\n',0,m.start())+1))
byid=collections.defaultdict(list)
for x in ids: byid[x[0]].append(x[1:])
print('DUP_IDS')
for k,v in sorted(byid.items()):
    if len(v)>1: print(k,v)
pat_dw=re.compile(r'TweakFactory\.Dword\(\s*"([^"]+)"\s*,\s*"([^"]+)"\s*,\s*"([^"]+)"\s*,\s*"([^"]+)"\s*,\s*"[^"]*"\s*,\s*RegistryHive\.(\w+)\s*,\s*"([^"]+)"\s*,\s*"([^"]+)"', re.S)
targets=collections.defaultdict(list)
for p,s in texts.items():
    for m in pat_dw.finditer(s):
        key=(m.group(5),m.group(6).lower(),m.group(7).lower())
        targets[key].append((m.group(1),m.group(2),m.group(4),p.name,s.count('\n',0,m.start())+1))
print('\nDUP_REG_TARGETS')
for k,v in sorted(targets.items()):
    if len(v)>1:
        print(k)
        for x in v: print(' ',x)
print('\nCOUNT',len(ids),'tweak literals,',len(targets),'registry targets')
dup_ids={k:v for k,v in byid.items() if len(v)>1}
dup_targets={k:v for k,v in targets.items() if len(v)>1}
if dup_ids or dup_targets:
    raise SystemExit(2)
print('TWEAK_DUPLICATE_AUDIT_OK')