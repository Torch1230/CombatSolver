#!/usr/bin/env python3
"""Token-line clone candidates; no candidate is a semantic equivalence proof."""
import argparse
from collections import defaultdict, Counter
from hashlib import sha256
from itertools import combinations
import json
from pathlib import Path


def detect(token_files, minimum=12):
    files = []
    index = defaultdict(list)
    for row in token_files:
        lines = []
        for line in row['lines']:
            ts = line['tokens']
            if all(t['text'] in ('{', '}', ';') for t in ts):
                continue
            raw = tuple(t['text'] for t in ts)
            normalized = tuple('$id' if t['kind'] == 'IdentifierToken' else t['text'] for t in ts)
            lines.append((line['line'], raw, normalized))
        file_index = len(files)
        files.append((row['file'], lines))
        for start in range(len(lines) - minimum + 1):
            key = tuple(x[2] for x in lines[start:start + minimum])
            digest = sha256(repr(key).encode()).digest()
            index[digest].append((file_index, start))
    results = []
    for positions in index.values():
        if len(positions) < 2:
            continue
        for (fi, a), (fj, b) in combinations(positions, 2):
            fa, aa = files[fi]
            fb, bb = files[fj]
            if fi == fj and abs(a-b) < minimum:
                continue
            if a and b and aa[a-1][2] == bb[b-1][2]:
                continue  # A larger seed owns this maximal pair.
            length = 0
            while a+length < len(aa) and b+length < len(bb) and aa[a+length][2] == bb[b+length][2]:
                if fi == fj and a+length >= b:
                    break
                length += 1
            if length < minimum:
                continue
            left = [t for x in aa[a:a+length] for t in x[1]]
            right = [t for x in bb[b:b+length] for t in x[1]]
            exact = left == right
            renames, reverse = {}, {}
            bijective, member_change = True, False
            for k, (x, y) in enumerate(zip(left, right)):
                if x == y:
                    continue
                if (x in renames and renames[x] != y) or (y in reverse and reverse[y] != x):
                    bijective = False
                renames[x], reverse[y] = y, x
                if k and left[k-1] in ('.', '?.', 'new', 'typeof'):
                    member_change = True
            category = 'literal' if exact else 'identifier-only' if bijective and not member_change else 'structural-review'
            results.append(dict(leftFile=fa, leftStart=aa[a][0], leftEnd=aa[a+length-1][0],
                                rightFile=fb, rightStart=bb[b][0], rightEnd=bb[b+length-1][0],
                                codeLines=length, similarity=sum(x == y for x, y in zip(left, right))/len(left),
                                category=category, renames=renames,
                                semanticStatus='unproven; inspect ordering, state, type and call contracts'))
    return sorted(results, key=lambda r: (-r['codeLines'], r['leftFile'], r['leftStart'], r['rightFile'], r['rightStart']))


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--tokens', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    parser.add_argument('--minimum', type=int, default=12)
    args = parser.parse_args()
    rows = detect(json.loads(args.tokens.read_text()), args.minimum)
    args.out.write_text(json.dumps(rows, ensure_ascii=False, indent=2))
    print(json.dumps(dict(pairs=len(rows), categories=Counter(r['category'] for r in rows)), ensure_ascii=False))
