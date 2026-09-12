"""Write a research candidate and reviewable patch without changing production source."""
from pathlib import Path
import difflib
import sys

root = Path(__file__).resolve().parents[2]
source = root / "src/Engine/Common/PredictionStateStore.cs"
destination = Path(sys.argv[1]).resolve()
if destination == source.resolve():
    raise SystemExit("Choose an experimental output path, not the production source.")
original = source.read_text()
candidate = original

def replace(before, after):
    global candidate
    if candidate.count(before) != 1:
        raise SystemExit(f"Source shape changed; expected one occurrence of: {before!r}")
    candidate = candidate.replace(before, after)

replace("private Dictionary<Type, int>? _countByType;", "private List<KeyValuePair<Type, int>>? _countByType;")
replace("""        => _countByType is not null
            && _countByType.TryGetValue(typeof(TState), out int count)
            && count != 0;""", """        => TypeCount(typeof(TState)) != 0;""")
replace("        _countByType![typeof(TState)]--;", "        AdjustTypeCount(typeof(TState), -1);")
replace("""    private void IncrementCount(Type stateType)
    {
        _countByType ??= [];
        _countByType[stateType] = _countByType.GetValueOrDefault(stateType) + 1;
    }""", """    private void IncrementCount(Type stateType) => AdjustTypeCount(stateType, 1);

    private int TypeCount(Type stateType)
    {
        if (_countByType is not null)
            foreach (var entry in _countByType)
                if (EqualityComparer<Type>.Default.Equals(entry.Key, stateType))
                    return entry.Value;
        return 0;
    }

    private void AdjustTypeCount(Type stateType, int delta)
    {
        _countByType ??= [];
        for (int index = 0; index < _countByType.Count; index++)
            if (EqualityComparer<Type>.Default.Equals(_countByType[index].Key, stateType))
            {
                _countByType[index] = new(stateType, _countByType[index].Value + delta);
                return;
            }
        if (delta < 0)
            throw new InvalidOperationException("Missing type count during removal.");
        _countByType.Add(new(stateType, delta));
    }""")
replace("                    (fork._countByType ??= [])[stateType] = count;",
        "                    (fork._countByType ??= []).Add(new(stateType, count));")
destination.parent.mkdir(parents=True, exist_ok=True)
destination.write_text(candidate)
patch = destination.with_suffix(".patch")
patch.write_text("".join(difflib.unified_diff(original.splitlines(True), candidate.splitlines(True),
    fromfile="a/src/Engine/Common/PredictionStateStore.cs", tofile="b/src/Engine/Common/PredictionStateStore.cs")))
print(f"Candidate: {destination}\nPatch: {patch}")
