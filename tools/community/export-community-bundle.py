"""Create a public diagnostic copy; preserve every replay entry byte for byte."""
import argparse
import json
from pathlib import Path, PurePosixPath
import re
import zipfile

PRIVATE_KEYS = {'submittername','runstatistics','contact','contactinformation','username','playername','nickname','profileid','readtoken','writetoken','uploadtoken','password','authorization'}
ABSOLUTE_PATH = re.compile(r'(?<!\w)(?:[A-Za-z]:[\\/]|/(?:home|Users|tmp)/)[^\r\n"<>|]*')


def redact_text(value):
    def replacement(match):
        path=match.group(0).rstrip()
        return '<LOCAL>/' + path.replace('\\','/').rsplit('/',1)[-1]
    return ABSOLUTE_PATH.sub(replacement,value)


def redact_json(value):
    if isinstance(value,dict):
        return {k:redact_json(v) for k,v in value.items() if k.lower() not in PRIVATE_KEYS}
    if isinstance(value,list):
        return [redact_json(x) for x in value]
    if isinstance(value,str):
        return redact_text(value)
    return value


def export(source,destination):
    destination.parent.mkdir(parents=True,exist_ok=True)
    with zipfile.ZipFile(source) as original,zipfile.ZipFile(destination,'x',compression=zipfile.ZIP_DEFLATED) as output:
        entries=original.infolist()
        if sum(x.file_size for x in entries)>256*1024*1024:
            raise ValueError('Archive expanded size exceeds 256 MiB')
        for info in entries:
            path=PurePosixPath(info.filename.replace('\\','/'))
            if path.is_absolute() or '..' in path.parts or re.match(r'^[A-Za-z]:',str(path)) or info.flag_bits&1 or (info.external_attr>>16)&0o170000==0o120000:
                raise ValueError('Unsupported archive entry: '+info.filename)
            if path.suffix.lower() in {'.dll','.exe','.pck','.so','.dylib'}:
                raise ValueError('Executable/game asset entry: '+info.filename)
            if not (str(path).startswith(('diagnostics/','replay/')) or str(path) in {'report.json','README.txt'}):
                raise ValueError('Unknown top-level archive entry: '+info.filename)
            data=original.read(info)
            if str(path)=='report.json' or str(path).startswith('diagnostics/'):
                if path.suffix=='.json':
                    data=json.dumps(redact_json(json.loads(data)),ensure_ascii=False,indent=2).encode('utf-8')
                elif path.suffix=='.jsonl':
                    data=('\n'.join(json.dumps(redact_json(json.loads(line)),ensure_ascii=False) for line in data.decode('utf-8-sig').splitlines())+'\n').encode('utf-8')
                else:
                    data=redact_text(data.decode('utf-8-sig')).encode('utf-8')
            elif str(path)=='README.txt':
                data=redact_text(data.decode('utf-8-sig')).encode('utf-8')
            output.writestr(info.filename,data)
        output.writestr('PUBLIC_COPY.txt','Community diagnostic copy. report/diagnostics identity fields and local paths were removed. replay/* entries are unchanged. No game replay or restoration was performed during publication.\n')


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source',type=Path)
    parser.add_argument('destination',type=Path)
    args=parser.parse_args()
    export(args.source,args.destination)
    print(args.destination)


if __name__=='__main__':
    main()
