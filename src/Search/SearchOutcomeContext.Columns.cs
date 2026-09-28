namespace CombatSolver;

internal static partial class SearchOutcomeContext
{
    // Compiled once per immutable predictor. Only names and numeric column
    // indices are retained; no card, simulator, branch value or global intern.
    internal sealed class Selection
    {
        internal readonly Dictionary<string, int> Columns;
        internal readonly HashSet<string> Prefixes;
        internal readonly ColumnNode Root = new();
        internal readonly int RequiredLength;

        internal Selection(IReadOnlyDictionary<string, int> columns)
        {
            Columns = new(columns, StringComparer.Ordinal);
            Prefixes = RequiredPrefixes(columns.Keys);
            foreach (var (name, index) in columns)
            {
                if (index < 0) throw new ArgumentException("Negative feature column.");
                RequiredLength = Math.Max(RequiredLength, checked(index + 1));
                ColumnNode node = Root;
                foreach (string segment in name.Split('/'))
                {
                    if (!node.Children.TryGetValue(segment, out var child))
                        node.Children.Add(segment, child = new());
                    node = child;
                }
                node.Index = index;
            }
        }
    }

    internal sealed class ColumnNode
    {
        internal readonly Dictionary<string, ColumnNode> Children = new(StringComparer.Ordinal);
        internal int Index = -1;

        internal ColumnNode? Find(ReadOnlySpan<char> path)
        {
            ColumnNode node = this;
            while (true)
            {
                int separator = path.IndexOf('/');
                var segment = separator < 0 ? path : path[..separator];
                if (!node.Children.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(segment, out var next))
                    return null;
                node = next;
                if (separator < 0) return node;
                path = path[(separator + 1)..];
            }
        }
    }

    // Sparse collection and numeric inference share the same traversal. The
    // latter follows compiled scopes, avoiding per-card path concatenations.
    private readonly struct FeatureScope
    {
        private readonly Dictionary<string, double>? _sparse;
        private readonly string? _prefix;
        private readonly ColumnNode? _node;
        private readonly double[]? _values;
        internal FeatureScope(Dictionary<string, double> sparse, string prefix)
        { _sparse = sparse; _prefix = prefix; }
        internal FeatureScope(ColumnNode? node, double[] values)
        { _node = node; _values = values; }
        internal bool Needed => _sparse != null || _node is { Children.Count: > 0 };
        internal bool Wants(string name) => _sparse != null || _node?.Find(name)?.Index >= 0;
        internal FeatureScope Scope(string name) => _sparse != null
            ? new(_sparse, _prefix + name + "/") : new(_node?.Find(name), _values!);
        internal double this[string name]
        {
            set
            {
                if (_sparse != null) _sparse[_prefix + name] = value;
                else if (_node?.Find(name)?.Index is >= 0 and var index) _values![index] = value;
            }
        }
        internal void Add(string name, double value)
        {
            if (_sparse != null)
            {
                string key = _prefix + name;
                _sparse[key] = _sparse.GetValueOrDefault(key) + value;
            }
            else if (_node?.Find(name)?.Index is >= 0 and var index) _values![index] += value;
        }
    }
}
