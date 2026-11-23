using CombatSolver.HotKernel;
using MegaCrit.Sts2.Core.Combat;

namespace CombatSolver;

/// <summary>
/// 跨回合续用路线的逐项状态文本。它不是哈希；只有实际状态与预测状态文本完全相等才允许复用。
/// </summary>
internal sealed record ContinuationStamp(string StateText)
{
    public string DescribeFirstDifference(ContinuationStamp actual)
        => DescribeDifferences(actual, maximumDifferences: 1).FirstOrDefault() ?? "none";

    public IReadOnlyList<string> DescribeDifferences(
        ContinuationStamp actual,
        int maximumDifferences = int.MaxValue)
    {
        if (maximumDifferences <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumDifferences));
        List<string> differences = [];
        string[] expectedFields = StateText.Split(';');
        string[] actualFields = actual.StateText.Split(';');
        int sharedCount = Math.Min(expectedFields.Length, actualFields.Length);
        for (int index = 0; index < sharedCount; index++)
        {
            string expectedField = expectedFields[index];
            string actualField = actualFields[index];
            if (string.Equals(expectedField, actualField, StringComparison.Ordinal))
                continue;

            (string expectedName, string expectedValue) = SplitField(expectedField);
            (string actualName, string actualValue) = SplitField(actualField);
            if (!string.Equals(expectedName, actualName, StringComparison.Ordinal))
            {
                differences.Add($"field_order[{index}] expected={LogValue(expectedField)} " +
                                $"actual={LogValue(actualField)}");
            }
            else
            {
                differences.AddRange(DescribeValueDifference(expectedName, expectedValue, actualValue));
            }
            if (differences.Count >= maximumDifferences)
                return differences;
        }

        if (expectedFields.Length != actualFields.Length)
        {
            string expected = sharedCount < expectedFields.Length ? expectedFields[sharedCount] : "<end>";
            string current = sharedCount < actualFields.Length ? actualFields[sharedCount] : "<end>";
            differences.Add($"field_count expected={expectedFields.Length} actual={actualFields.Length} " +
                            $"first_extra_expected={LogValue(expected)} first_extra_actual={LogValue(current)}");
        }
        return differences;
    }

    public static ContinuationStamp CaptureLive(CombatState state)
    {
        HotRootBundle root = HotRootBuilder.BuildFromLive(state);
        CombatHotState rootState = root.RootState;
        return CaptureFromHot(in rootState, root.RootCold, root.Rules);
    }

    public static ContinuationStamp CaptureFromHot(
        in CombatHotState state,
        HotColdSidecar? cold,
        HotRules rules)
        => new(HotContinuationStamp.CaptureFromHot(in state, cold, rules));

    private static (string Name, string Value) SplitField(string field)
    {
        int separator = field.IndexOf('=');
        return separator < 0
            ? (field, string.Empty)
            : (field[..separator], field[(separator + 1)..]);
    }

    /// <summary>
    /// 单个状态字段的差异描述。牌堆 / Power 字段可能同时有多处不同，必须逐处报出；
    /// 超出上限时显式写出剩余数量，不做静默截断。
    /// </summary>
    private const int MaximumItemDifferencesPerField = 8;

    private static IReadOnlyList<string> DescribeValueDifference(string name, string expected, string actual)
    {
        string[]? partNames = name switch
        {
            "osty" => ["combat_id", "hp", "max_hp"],
            "O" => ["capacity", "orbs"],
            "R" => ["shuffle", "card_generation", "potion_generation", "card_selection", "energy_costs", "targets", "orbs", "monster_ai", "niche"],
            _ when name.StartsWith('E') => ["combat_id", "monster", "slot", "hp", "max_hp", "block", "move"],
            _ => null,
        };
        if (partNames != null)
        {
            char separator = name == "O" ? ':' : '/';
            string[] expectedParts = expected.Split(separator);
            string[] actualParts = actual.Split(separator);
            int count = Math.Max(expectedParts.Length, actualParts.Length);
            for (int index = 0; index < count; index++)
            {
                string expectedPart = index < expectedParts.Length ? expectedParts[index] : "<missing>";
                string actualPart = index < actualParts.Length ? actualParts[index] : "<missing>";
                if (string.Equals(expectedPart, actualPart, StringComparison.Ordinal))
                    continue;
                string partName = index < partNames.Length ? partNames[index] : $"part_{index}";
                return [$"field={name}.{partName} expected={LogValue(expectedPart)} actual={LogValue(actualPart)}"];
            }
        }

        if (name is "H" or "D" or "C" or "X" or "P")
        {
            IReadOnlyList<string> expectedItems = SplitTopLevelItems(expected);
            IReadOnlyList<string> actualItems = SplitTopLevelItems(actual);
            int count = Math.Max(expectedItems.Count, actualItems.Count);
            List<string> itemDifferences = [];
            int differingItems = 0;
            for (int index = 0; index < count; index++)
            {
                string expectedItem = index < expectedItems.Count ? expectedItems[index] : "<missing>";
                string actualItem = index < actualItems.Count ? actualItems[index] : "<missing>";
                if (string.Equals(expectedItem, actualItem, StringComparison.Ordinal))
                    continue;
                differingItems++;
                if (itemDifferences.Count >= MaximumItemDifferencesPerField)
                    continue;
                itemDifferences.Add($"field={name}[{index}] expected={LogValue(expectedItem)} actual={LogValue(actualItem)} " +
                                    $"expected_count={expectedItems.Count} actual_count={actualItems.Count}");
            }
            if (itemDifferences.Count == 0)
                return [$"field={name} expected={LogValue(expected)} actual={LogValue(actual)}"];
            if (differingItems > itemDifferences.Count)
                itemDifferences.Add($"field={name} additional_differences={differingItems - itemDifferences.Count} " +
                                    $"expected_count={expectedItems.Count} actual_count={actualItems.Count}");
            return itemDifferences;
        }

        return [$"field={name} expected={LogValue(expected)} actual={LogValue(actual)}"];
    }

    private static IReadOnlyList<string> SplitTopLevelItems(string value)
    {
        List<string> items = [];
        int start = 0;
        int bracketDepth = 0;
        for (int index = 0; index < value.Length; index++)
        {
            switch (value[index])
            {
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    bracketDepth--;
                    break;
                case ',' when bracketDepth == 0:
                    if (index > start)
                        items.Add(value[start..index]);
                    start = index + 1;
                    break;
            }
        }
        if (start < value.Length)
            items.Add(value[start..]);
        return items;
    }

    private static string LogValue(string value)
    {
        const int maxLength = 180;
        string normalized = value.Replace('\r', ' ').Replace('\n', ' ');
        return normalized.Length <= maxLength
            ? $"{{{normalized}}}"
            : $"{{{normalized[..maxLength]}…}}";
    }
}
