using CombatSolver.Engine.Common;

namespace CombatSolver;

internal static partial class CardChoiceSupport
{
    // 单个选取张数下完整枚举的语义组合上限。离线普查（72 根、16.5 万次多选生成）里单次最多 512 个；
    // 超过这个数就退回原来的截断枚举，避免异常大的牌堆把候选生成拖成热点。
    internal const int MaximumRankedCombinations = 4096;

    /// <summary>离线宿主用：每次生成后独立重做一次完整枚举，核对保留集恰好是现评分下的前 K。</summary>
    internal static bool VerifyTopCombinationsForTesting { get; set; }

    /// <summary>
    /// 取某个选取张数下、按 <see cref="EvaluateChoicePriority"/> 排名前 <paramref name="limit"/> 的语义组合。
    /// 遍历顺序和语义去重与 <see cref="BuildCombinations"/> 相同；字典序首个组合是该张数的代表，始终保留；
    /// 同分时字典序靠前者优先；输出仍按字典序。组合数不超过 <paramref name="limit"/> 时与原枚举逐项相同。
    /// </summary>
    /// <returns>组合数超过 <see cref="MaximumRankedCombinations"/> 时返回 false，调用方改用截断枚举。</returns>
    private static bool TryBuildTopCombinations(
        CardChoiceSpec spec,
        IReadOnlyList<PredictedCard> ordered,
        ReadOnlySpan<int> previousEqualIndex,
        int take,
        List<PredictedCard> combination,
        List<IReadOnlyList<PredictedCard>> output,
        int limit)
    {
        if (limit <= 0)
            return true;
        TopCombinationCollector collector = new(spec, limit);
        combination.Clear();
        CollectCombinations(ordered, previousEqualIndex, take, 0, combination, collector);
        combination.Clear();
        if (collector.Overflowed)
            return false;
        List<(int Ordinal, PredictedCard[] Cards)> kept = collector.InEnumerationOrder();
        if (VerifyTopCombinationsForTesting)
            VerifyTopCombinations(spec, ordered, previousEqualIndex, take, limit, kept);
        foreach ((int _, PredictedCard[] cards) in kept)
            output.Add(new ScoredCardSelection(cards));
        return true;
    }

    private static void CollectCombinations(
        IReadOnlyList<PredictedCard> options,
        ReadOnlySpan<int> previousEqualIndex,
        int count,
        int start,
        List<PredictedCard> current,
        TopCombinationCollector collector)
    {
        if (collector.Overflowed)
            return;
        if (current.Count == count)
        {
            collector.Offer(current);
            return;
        }
        for (int i = start; i <= options.Count - (count - current.Count); i++)
        {
            if (previousEqualIndex[i] >= start)
                continue;
            current.Add(options[i]);
            CollectCombinations(options, previousEqualIndex, count, i + 1, current, collector);
            current.RemoveAt(current.Count - 1);
            if (collector.Overflowed)
                return;
        }
    }

    private sealed class TopCombinationCollector(CardChoiceSpec spec, int limit)
    {
        private readonly List<(int Ordinal, double Priority, PredictedCard[] Cards)> _kept = [];
        private int _worst;
        private int _offered;

        internal bool Overflowed { get; private set; }

        internal void Offer(List<PredictedCard> current)
        {
            if (_offered >= MaximumRankedCombinations)
            {
                Overflowed = true;
                return;
            }
            int ordinal = _offered++;
            // 不做带界剪枝：跳过叶子会让序号不再等于字典序位次，同分次序就会和截断枚举分叉。
            double priority = ordinal == 0
                ? double.PositiveInfinity
                : EvaluateChoicePriority(spec, current);
            if (_kept.Count < limit)
            {
                _kept.Add((ordinal, priority, current.ToArray()));
                RefreshWorst();
                return;
            }
            // 序号单调递增，所以同分时新来的总是排在已保留者之后。
            if (priority <= _kept[_worst].Priority)
                return;
            _kept[_worst] = (ordinal, priority, current.ToArray());
            RefreshWorst();
        }

        private void RefreshWorst()
        {
            _worst = 0;
            for (int index = 1; index < _kept.Count; index++)
            {
                if (_kept[index].Priority < _kept[_worst].Priority
                    || (_kept[index].Priority == _kept[_worst].Priority
                        && _kept[index].Ordinal > _kept[_worst].Ordinal))
                {
                    _worst = index;
                }
            }
        }

        internal List<(int Ordinal, PredictedCard[] Cards)> InEnumerationOrder()
            => _kept.OrderBy(entry => entry.Ordinal)
                .Select(entry => (entry.Ordinal, entry.Cards))
                .ToList();
    }

    private static void VerifyTopCombinations(
        CardChoiceSpec spec,
        IReadOnlyList<PredictedCard> ordered,
        ReadOnlySpan<int> previousEqualIndex,
        int take,
        int limit,
        List<(int Ordinal, PredictedCard[] Cards)> kept)
    {
        List<(int Ordinal, double Priority)> all = [];
        List<PredictedCard> current = [];
        int[] prior = previousEqualIndex.ToArray();
        Visit(0);
        void Visit(int start)
        {
            if (current.Count == take)
            {
                all.Add((all.Count, all.Count == 0
                    ? double.PositiveInfinity
                    : EvaluateChoicePriority(spec, current)));
                return;
            }
            for (int i = start; i <= ordered.Count - (take - current.Count); i++)
            {
                if (prior[i] >= start)
                    continue;
                current.Add(ordered[i]);
                Visit(i + 1);
                current.RemoveAt(current.Count - 1);
            }
        }
        int[] expected = all
            .OrderByDescending(entry => entry.Priority)
            .ThenBy(entry => entry.Ordinal)
            .Take(limit)
            .Select(entry => entry.Ordinal)
            .Order()
            .ToArray();
        int[] actual = kept.Select(entry => entry.Ordinal).ToArray();
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(
                $"多选组合保留集与完整枚举前 {limit} 不符：take={take} " +
                $"spec={spec.Effect}/{spec.SourcePile} 组合数={all.Count} " +
                $"期望={string.Join(',', expected)} 实际={string.Join(',', actual)}。");
        }
    }
}
