#!/usr/bin/env python3
"""Temporary (Simple Mode string migration): folds lib/l10n/parts/*.arb into
lib/l10n/app_en.arb, then the parts are deleted. Keys already in app_en.arb are kept."""
import glob, json, os, sys, tempfile

root = os.path.join(os.path.dirname(__file__), '..', 'lib', 'l10n')
template = os.path.join(root, 'app_en.arb')
with open(template) as f:
    merged = json.load(f)
for part in sorted(glob.glob(os.path.join(root, 'parts', '*.arb'))):
    try:
        with open(part) as f:
            data = json.load(f)
    except (OSError, ValueError) as e:
        print(f'skipped {part}: {e}', file=sys.stderr)
        continue
    for k, v in data.items():
        if k.startswith('@@'):
            continue
        if k in merged and merged[k] != v and not k.startswith('@'):
            print(f'{part}: key {k} differs from one already merged', file=sys.stderr)
        merged[k] = v
fd, tmp = tempfile.mkstemp(dir=root, suffix='.tmp')
with os.fdopen(fd, 'w') as f:
    json.dump(merged, f, indent=2, ensure_ascii=False)
    f.write('\n')
os.replace(tmp, template)
