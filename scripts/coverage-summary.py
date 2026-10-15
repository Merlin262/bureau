"""Resumo de cobertura (Cobertura XML) em Markdown — usado na pipeline (GITHUB_STEP_SUMMARY) e localmente.

Uso: python scripts/coverage-summary.py <coverage.cobertura.xml> [--title Servidor] [--min 80]
Sai com código 1 se a cobertura de linhas ficar abaixo do mínimo.
"""
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict


def option(args, name, default):
    if name not in args:
        return default
    i = args.index(name)
    value = args[i + 1]
    del args[i:i + 2]
    return value


args = sys.argv[1:]
title = option(args, '--title', 'Servidor')
minimum = float(option(args, '--min', '0'))

root = ET.parse(args[0]).getroot()
per_file = defaultdict(lambda: [0, 0])  # arquivo → [linhas cobertas, linhas]
for cls in root.iter('class'):
    name = cls.get('filename', '').replace('\\', '/')
    name = name.split('/server/')[-1].split('/web/')[-1]
    seen = set()
    for line in cls.iter('line'):
        n = line.get('number')
        if n in seen:
            continue
        seen.add(n)
        per_file[name][1] += 1
        if int(line.get('hits', '0')) > 0:
            per_file[name][0] += 1

covered = sum(c for c, _ in per_file.values())
total = sum(t for _, t in per_file.values())
pct = 100.0 * covered / total if total else 0.0

print(f'### Cobertura · {title}: **{pct:.1f}%** ({covered}/{total} linhas)')
print()
print('<details><summary>Por arquivo</summary>')
print()
print('| Arquivo | Linhas | Cobertura |')
print('|---|---:|---:|')
for name, (c, t) in sorted(per_file.items(), key=lambda kv: kv[1][0] / max(kv[1][1], 1)):
    if t:
        print(f'| {name} | {c}/{t} | {100.0 * c / t:.0f}% |')
print()
print('</details>')
print()

if pct < minimum:
    print(f'**Abaixo do mínimo de {minimum:.0f}%.**')
    sys.exit(1)
