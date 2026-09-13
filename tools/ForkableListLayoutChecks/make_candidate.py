"""Write the rejected storage-layout candidate without changing production source."""
import argparse
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('output', type=Path)
args = parser.parse_args()
repo = Path(__file__).resolve().parents[2]
source = (repo / 'src/Search/ForkableCollections.cs').read_text()
head, tail = source.split('internal sealed class ForkableDictionary', 1)
for before, after in [
    ('private sealed class Storage(List<T> values)', 'private sealed class Storage : List<T>'),
    ('        public List<T> Values { get; } = values;',
     '        public Storage() { }\n        public Storage(IEnumerable<T> values) : base(values) { }'),
    ('        : this(new List<T>())', '        : this(new Storage())'),
    ('        : this(new List<T>(values))', '        : this(new Storage(values))'),
    ('    private ForkableList(List<T> values)\n        => _storage = new Storage(values);\n\n', ''),
]:
    assert head.count(before) == 1, f'Unexpected source shape: {before}'
    head = head.replace(before, after)
head = head.replace('_storage.Values', '_storage').replace('new Storage(new List<T>(_storage))', 'new Storage(_storage)')
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(head + 'internal sealed class ForkableDictionary' + tail)
