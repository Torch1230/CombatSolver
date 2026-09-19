#!/usr/bin/env python3
import argparse
from collections import Counter, defaultdict
import json
from pathlib import Path
from urllib.parse import unquote, urlparse

parser=argparse.ArgumentParser()
parser.add_argument('--input',type=Path,required=True)
parser.add_argument('--out',type=Path,required=True)
parser.add_argument('--repo',type=Path,default=Path(__file__).resolve().parents[2])
args=parser.parse_args()
rows=[]
for run in json.loads(args.input.read_text())['runs']:
    for result in run['results']:
        location=result.get('locations',[{}])[0]
        physical=location.get('physicalLocation',location.get('resultFile',{}))
        uri=physical.get('artifactLocation',physical).get('uri','')
        path=unquote(urlparse(uri).path)
        file=path.removeprefix(str(args.repo.resolve())+'/')
        generated=not file.startswith('src/') or '.generated.' in file or file.endswith('.g.cs')
        suppressed=bool(result.get('suppressionStates') or result.get('suppressions'))
        level=result.get('level','warning')
        message=result['message']
        rows.append(dict(rule=result['ruleId'],file=file,line=physical.get('region',{}).get('startLine'),
                         level=level,generated=generated,suppressed=suppressed,
                         category='suppressed' if suppressed else 'generated' if generated else 'warning' if level=='warning' else level,
                         message=message if isinstance(message,str) else message['text']))
summary=defaultdict(Counter)
for r in rows: summary[r['rule']][r['category']]+=1
args.out.mkdir(parents=True,exist_ok=True)
(args.out/'diagnostics.json').write_text(json.dumps(rows,ensure_ascii=False,indent=2))
(args.out/'diagnostic-summary.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2,sort_keys=True))
print(json.dumps(dict(total=len(rows),categories=Counter(r['category'] for r in rows),rules=len(summary))))
