#!/usr/bin/env python3
"""Join read-only Roslyn evidence with tracked fixtures, documentation and tools."""
import argparse
from collections import Counter, defaultdict
import json
from pathlib import Path
import re
import subprocess
from urllib.parse import unquote


def main(repo, data):
    tracked = subprocess.check_output(['git', '-C', str(repo), 'ls-files', '-z']).decode().split('\0')
    texts = {p: (repo/p).read_text(errors='replace') for p in tracked if p and Path(p).suffix in ('.cs', '.md', '.json', '.py', '.sh', '.ps1', '.csproj') and (repo/p).is_file()}
    load = lambda name: json.loads((data/(name+'.json')).read_text())
    def write(name, rows):
        (data/(name+'.json')).write_text(json.dumps(rows, ensure_ascii=False, indent=2))
    refs = load('references')
    by_symbol = defaultdict(list)
    for r in refs:
        by_symbol[r['symbol']].append(r)
    graph = Counter()
    evidence = defaultdict(list)
    for r in refs:
        if r['kind'] != 'NamedType':
            continue
        source = r['file'].split('/')[1]
        for target in sorted({p.split('/')[1] for p in r['definitions']}):
            if source == target:
                continue
            graph[source, target] += 1
            evidence[source, target].append(dict(file=r['file'], line=r['line'], type=r['symbol']))
    write('dependencies', [dict(source=s, target=t, references=c, evidence=evidence[s,t]) for (s,t),c in graph.most_common()])
    write('namespace-usings', [u for u in load('usings') if (u['text'] or '').startswith('CombatSolver')])
    docs = {p:t for p,t in texts.items() if p.startswith('docs/') and p.endswith('.md')}
    switches = []
    for item in load('switches'):
        name = item['name']
        hits = by_symbol[item['symbol']]
        mentions = lambda prefix: [p for p,t in texts.items() if p.startswith(prefix) and re.search(r'\b'+re.escape(name)+r'\b', t)]
        item.update(reads=[r for r in hits if r['role'] == 'read-or-name' and not r['nameofUse']],
                    writes=[r for r in hits if r['role'] in ('write','out')],
                    testing=mentions('src/Testing/'), tools=mentions('tools/'), ui=mentions('src/UI/'), docs=mentions('docs/'),
                    reachability='constant value' if item['constant'] else 'runtime dependent; static references do not prove both branches executed')
        switches.append(item)
    write('switch-audit', switches)
    executor = texts['src/Testing/UnattendedTestRunner.Executor.cs']
    scenario_ids = set()
    branches = []
    for match in re.finditer(r'if\s*\((.*?)(?=\n\s*\{)', executor, re.S):
        condition = match.group(1)
        if 'request.ScenarioId' not in condition:
            continue
        ids = re.findall(r'"([A-Z][A-Z0-9_-]+)"', condition)
        scenario_ids.update(ids)
        branches.append(dict(line=executor.count('\n',0,match.start())+1, ids=ids, condition=condition.strip()))
    fixtures = []
    all_keys = Counter()
    def walk(value):
        if isinstance(value, dict):
            for key, child in value.items():
                all_keys[key.casefold()] += 1
                walk(child)
        elif isinstance(value, list):
            for child in value: walk(child)
    for p,t in texts.items():
        if p.startswith('coverage/') and p.endswith('.json'):
            payload = json.loads(t)
            walk(payload)
            if p.startswith('coverage/unattended/'):
                sid = payload.get('scenarioId') if isinstance(payload,dict) else None
                fixtures.append(dict(file=p, scenarioId=sid, executorSpecialBranch=sid in scenario_ids,
                                     otherTestingMentions=[q for q,s in texts.items() if sid and q.startswith('src/Testing/') and q != 'src/Testing/UnattendedTestProtocol.cs' and sid in s]))
    scenarios = []
    for sid in sorted(scenario_ids):
        scenarios.append(dict(id=sid, coverage=[p for p,t in texts.items() if p.startswith('coverage/') and sid in t],
                              matrix=sid in texts['docs/TEST_MATRIX.md'], tools=[p for p,t in texts.items() if p.startswith('tools/') and sid in t]))
    fields = []
    for m in load('members'):
        if m['owner'] != 'UnattendedTestRequest': continue
        name = m['name']
        fields.append(dict(name=name, line=m['line'], fixtureKeyOccurrences=all_keys[name.casefold()],
                           assignments=[r for r in by_symbol[m['symbol']] if r['role']=='write'],
                           tools=[p for p,t in texts.items() if p.startswith('tools/') and re.search(r'\b'+re.escape(name)+r'\b', t, re.I)]))
    write('testing', dict(executorBranches=branches, scenarios=scenarios, fixtures=fixtures, requestFields=fields,
                         warning='ScenarioId has a generic executor fallback: absence of a special branch does not mean an invalid fixture. Tool mentions and assignment references are static coverage, not executed assertions.'))
    def slugs(text):
        seen = Counter(); found = set(); fenced = False
        for line in text.splitlines():
            if line.startswith(('```','~~~')): fenced = not fenced
            if fenced: continue
            match = re.match(r'^#{1,6}\s+(.+?)(?:\s+#+)?$', line)
            if match:
                title = re.sub(r'<[^>]+>', '', match.group(1)).lower()
                slug = ''.join(c for c in title if c.isalnum() or c in '_- ' or '\u4e00' <= c <= '\u9fff').replace(' ','-')
                suffix = '' if not seen[slug] else '-'+str(seen[slug])
                seen[slug] += 1; found.add(slug+suffix)
        found.update(re.findall(r'(?:id|name)=["\']([^"\']+)',text))
        return found
    links = []; anchor_cache = {}
    for p,t in docs.items():
        for m in re.finditer(r'\[[^\]\n]*\]\((<?[^\s)]+>?)(?:\s+"[^"]*")?\)', t):
            link = m.group(1).strip('<>')
            if re.match(r'^[a-zA-Z]+:',link) or link.startswith('//'): continue
            path,_,anchor = unquote(link).partition('#')
            target = (repo/p).parent/path if path else repo/p
            target = target.resolve()
            if not target.exists(): status = 'missing-file'
            elif anchor and target.suffix=='.md':
                if target not in anchor_cache: anchor_cache[target]=slugs(target.read_text(errors='replace'))
                status = 'ok' if anchor in anchor_cache[target] else 'anchor-review'
            else: status='ok'
            links.append(dict(file=p,line=t.count('\n',0,m.start())+1,link=link,status=status))
    write('links', links)
    tool_dirs = sorted({p.split('/')[1] for p in tracked if p.startswith('tools/') and p.count('/')>=2})
    write('tool-directories', [dict(directory=d,
        projects=[p for p in texts if p.startswith('tools/'+d+'/') and p.endswith('.csproj')],
        python=[p for p in texts if p.startswith('tools/'+d+'/') and p.endswith('.py')],
        docs=[p for p,t in docs.items() if d in t],
        lastCommit=subprocess.check_output(['git','-C',str(repo),'log','-1','--format=%cs %h','--','tools/'+d]).decode().strip()) for d in tool_dirs])
    keys = json.loads(texts['src/UI/English.json'])
    string_rows = load('strings')
    # Preserve escaped strings via the Roslyn token spelling; interpolation/wrappers need review.
    literal_hits = defaultdict(list)
    for row in string_rows:
        if row['value'] in keys:
            literal_hits[row['value']].append(row['file'])
    get_calls = []
    for p,t in texts.items():
        if not p.startswith('src/') or not p.endswith('.cs'): continue
        for key in keys:
            escaped = json.dumps(key, ensure_ascii=False)[1:-1]
            if key in t or escaped in t:
                literal_hits[key].append(p)
        for m in re.finditer(r'SolverText\.Get\(\s*"((?:\\.|[^"\\])*)"',t):
            value = json.loads('"'+m.group(1)+'"')
            get_calls.append(dict(file=p,line=t.count('\n',0,m.start())+1,key=value,english=value in keys))
    write('localization', dict(keys=len(keys), noLiteralCaller=[k for k in keys if not literal_hits[k]], getCalls=get_calls,
                               literalReferences=literal_hits, warning='No literal caller is a candidate only: Format interpolation and forwarding helpers must be traced before deleting a key.'))
    all_filenames={Path(p).name for p in tracked if p}
    comment_rows=load('comments')
    for c in comment_rows:
        filenames=re.findall(r'\b[\w.-]+\.(?:cs|json|md|sh|ps1)\b',c['text'])
        c['missingFilenames']=[f for f in filenames if f not in all_filenames]
        c['numbers']=re.findall(r'\b\d[\d_,.]*\b',c['text'])
        c['temporal']=bool(re.search(r'当前|默认|现在',c['text']))
        c['verdict']='manual-review' if c['missingFilenames'] or c['temporal'] else 'reference inventory; not automatically false'
    write('comment-audit',comment_rows)
    summary=dict(methods=len(load('methods')), types=len(load('types')), catches=len(load('catches')),
                 suspiciousCatches=sum(c['suspicious'] for c in load('catches')), switches=len(switches),
                 comments=len(comment_rows), commentMissingFile=sum(bool(c['missingFilenames']) for c in comment_rows),
                 scenarios=len(scenarios), executorBranches=len(branches), fixtures=len(fixtures), requestFields=len(fields),
                 fieldsWithoutFixture=sum(not f['fixtureKeyOccurrences'] for f in fields),
                 links=dict(Counter(l['status'] for l in links)), toolDirectories=len(tool_dirs),
                 localizationKeys=len(keys), noLiteralCaller=len([k for k in keys if not literal_hits[k]]))
    write('summary',summary); print(json.dumps(summary,ensure_ascii=False))


if __name__=='__main__':
    parser=argparse.ArgumentParser(); parser.add_argument('--repo',type=Path,default=Path(__file__).resolve().parents[2]); parser.add_argument('--data',type=Path,required=True)
    args=parser.parse_args(); main(args.repo.resolve(),args.data.resolve())
