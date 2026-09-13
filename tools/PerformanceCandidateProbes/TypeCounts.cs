namespace CombatSolver.Engine.Common;

// Local prototype. Does not enlarge an empty PredictionStateStore.
internal sealed class SmallTypeCounts
{
    private Type? _first, _second, _third;
    private int _firstCount, _secondCount, _thirdCount;
    private Dictionary<Type,int>? _overflow;

    public int Get(Type type)
    {
        if (_overflow is { } map) return map.GetValueOrDefault(type);
        if (ReferenceEquals(type,_first)) return _firstCount;
        if (ReferenceEquals(type,_second)) return _secondCount;
        return ReferenceEquals(type,_third) ? _thirdCount : 0;
    }
    public void Increment(Type type) => Set(type, Get(type) + 1);
    public void Decrement(Type type)
    {
        if (_overflow is { } map) { map[type]--; return; }
        if (ReferenceEquals(type,_first)) { _firstCount--; return; }
        if (ReferenceEquals(type,_second)) { _secondCount--; return; }
        if (ReferenceEquals(type,_third)) { _thirdCount--; return; }
        throw new KeyNotFoundException();
    }
    private void Set(Type type, int count)
    {
        if (_overflow is { } map) { map[type]=count; return; }
        if (ReferenceEquals(type,_first) || _first == null) { _first=type;_firstCount=count;return; }
        if (ReferenceEquals(type,_second) || _second == null) { _second=type;_secondCount=count;return; }
        if (ReferenceEquals(type,_third) || _third == null) { _third=type;_thirdCount=count;return; }
        _overflow=new(4) { [_first]=_firstCount, [_second]=_secondCount, [_third]=_thirdCount, [type]=count };
        _first=_second=_third=null;
    }
    public SmallTypeCounts? Fork()
    {
        SmallTypeCounts? copy=null;
        if (_overflow is { } map)
        {
            foreach (var (type,count) in map)
                if(count != 0) (copy ??= new()).Set(type,count);
        }
        else
        {
            if (_firstCount != 0) (copy ??= new()).Set(_first!,_firstCount);
            if (_secondCount != 0) (copy ??= new()).Set(_second!,_secondCount);
            if (_thirdCount != 0) (copy ??= new()).Set(_third!,_thirdCount);
        }
        return copy;
    }
}
