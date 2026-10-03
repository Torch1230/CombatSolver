#!/usr/bin/env python3
"""Validate maintained Markdown links, anchors and document size budgets."""
from pathlib import Path
from collections import defaultdict
from urllib.parse import unquote
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]


def prose(text):
    result = []
    fence = None
    for line in text.splitlines():
        match = re.match(r'^\s*(`{3,}|~{3,})', line)
        if match:
            marker = match[1][0]
            fence = marker if fence is None else None if fence == marker else fence
            continue
        if fence is None:
            result.append(line)
    return result


def anchors(text):
    counts = defaultdict(int)
    result = set()
    for line in prose(text):
        match = re.match(r'^#{1,6} (.+?)\s*#*$', line)
        if match:
            heading = re.sub(r'!?\[([^]]*)\]\([^)]*\)', r'\1', match[1])
            heading = re.sub(r'<[^>]*>', '', heading).lower()
            slug = ''.join(c for c in heading if c.isalnum() or c in '_- ').replace(' ', '-')
            n = counts[slug]
            counts[slug] += 1
            result.add(slug if n == 0 else f'{slug}-{n}')
        result.update(re.findall(r'<a\s+(?:id|name)=["\']([^"\']+)', line))
    return result


def verify():
    listed = subprocess.check_output(
        ['git', 'ls-files', '--cached', '--others', '--exclude-standard', '-z'], cwd=ROOT
    ).decode('utf-8').split('\0')
    names = sorted({n for n in listed if n.endswith('.md') and (ROOT / n).is_file()})
    errors = []
    cache = {}
    links = 0
    # Full datasets and generated inventories keep all records. These files have
    # explicit scope, while their navigation lives in concise separate entries.
    inventories = {'docs/COMBAT_HOOK_COVERAGE.md', 'docs/MULTIPLAYER_CONTENT_INVENTORY.md'}
    for name in names:
        path = ROOT / name
        text = path.read_text(encoding='utf-8-sig')
        historical = name.startswith(('docs/archive/', 'docs/releases/'))
        data = name in inventories or bool(re.fullmatch(
            r'docs/strategy/power-card-valuation/(ironclad|silent|defect|regent|necrobinder|colorless|player-review-20260917)\.md', name
        ))
        limit_lines, limit_bytes = (200, 32768) if name in {
            'docs/DEVELOPMENT_NOTES.md', 'docs/TEST_MATRIX.md'
        } else (500, 65536)
        if not historical and not data and (len(text.splitlines()) > limit_lines or len(text.encode('utf-8')) > limit_bytes):
            errors.append(f'{name}: size {len(text.splitlines())} lines / {len(text.encode("utf-8"))} bytes exceeds {limit_lines} / {limit_bytes}')
        if path.name == 'AGENTS.md':
            for n, line in enumerate(text.splitlines(), 1):
                if re.match(r'^>.*(当前批次|本轮追加|当前工作重点|批量扩展完成|本轮任务|本批|当前阶段|当前任务)', line):
                    errors.append(f'{name}:{n}: temporary task note in rules')
        for line in prose(text):
            line = re.sub(r'(`+).*?\1', '', line)
            destinations = re.findall(r'!?\[[^\]\n]*\]\((<?)([^\s)>]+)>?(?:\s+[^)]*)?\)', line)
            ref = re.match(r'^\s*\[(?!\^)[^]]+\]:\s*<?([^\s>]+)>?', line)
            if ref:
                destinations.append(('', ref[1]))
            for _, raw in destinations:
                if re.match(r'^[a-z][a-z0-9+.-]*:', raw, re.I) or raw.startswith('//'):
                    continue
                target, sep, anchor = raw.partition('#')
                if target.startswith('/'):
                    continue  # Local delivery links are outside this repository.
                resolved = (path.parent / unquote(target)).resolve() if target else path
                links += 1
                if not resolved.exists():
                    errors.append(f'{name}: missing link {raw}')
                elif sep and anchor and resolved.suffix == '.md':
                    if resolved not in cache:
                        cache[resolved] = anchors(resolved.read_text(encoding='utf-8-sig'))
                    if unquote(anchor) not in cache[resolved]:
                        errors.append(f'{name}: missing anchor {raw}')
    for error in errors:
        print(error)
    print(f'DOCUMENTATION_{"FAILED" if errors else "OK"} files={len(names)} links={links} errors={len(errors)}')
    return 1 if errors else 0


if __name__ == '__main__':
    sys.exit(verify())
