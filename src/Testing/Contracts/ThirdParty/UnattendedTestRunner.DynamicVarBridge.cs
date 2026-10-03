using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    /// <summary>
    /// T014 最小边界：DynamicVarSet 访问桥在字段可用时必须给出与公开枚举一致的
    /// 键、值与枚举序列；强制公开回退时两条路径仍逐项一致。夹具不修改 live 战斗。
    /// </summary>
    private void AssertDynamicVarBridge(CombatState combat, Player player)
    {
        if (!DynamicVarSetAccess.HasFieldBridge)
            throw new InvalidOperationException("DynamicVarBridge 夹具需要当前构建存在 DynamicVarSet 内部字典字段。");

        string liveBefore = ContinuationStamp.CaptureLive(combat).StateText;
        CardModel card = ModelDb.Card<StrikeRegent>().ToMutable();
        PowerModel power = ModelDb.Power<WeakPower>();
        string cardSummary = AssertBridgeEquivalence(card.DynamicVars, "card");
        string powerSummary = AssertBridgeEquivalence(power.DynamicVars, "power");
        if (ContinuationStamp.CaptureLive(combat).StateText != liveBefore)
            throw new InvalidOperationException("DynamicVarBridge 夹具改变了 live 战斗。");
        _completedChecks.Add($"DynamicVarBridge:{cardSummary}:{powerSummary}");
    }

    private static string AssertBridgeEquivalence(DynamicVarSet set, string label)
    {
        List<KeyValuePair<string, DynamicVar>> fastEntries = DynamicVarSetAccess.Entries(set).ToList();
        List<string> fastKeys = DynamicVarSetAccess.Keys(set).ToList();
        List<DynamicVar> fastValues = DynamicVarSetAccess.Values(set).ToList();
        List<KeyValuePair<string, DynamicVar>> fastEnumerated = [];
        foreach (KeyValuePair<string, DynamicVar> entry in new DynamicVarSetAccess.EntryEnumerable(set))
            fastEnumerated.Add(entry);

        List<KeyValuePair<string, DynamicVar>> fallbackEntries;
        List<string> fallbackKeys;
        List<DynamicVar> fallbackValues;
        List<KeyValuePair<string, DynamicVar>> fallbackEnumerated = [];
        try
        {
            DynamicVarSetAccess.ForcePublicFallback = true;
            fallbackEntries = DynamicVarSetAccess.Entries(set).ToList();
            fallbackKeys = DynamicVarSetAccess.Keys(set).ToList();
            fallbackValues = DynamicVarSetAccess.Values(set).ToList();
            foreach (KeyValuePair<string, DynamicVar> entry in new DynamicVarSetAccess.EntryEnumerable(set))
                fallbackEnumerated.Add(entry);
        }
        finally
        {
            DynamicVarSetAccess.ForcePublicFallback = false;
        }

        if (!fastKeys.SequenceEqual(fallbackKeys, StringComparer.Ordinal)
            || !fastValues.SequenceEqual(fallbackValues, ReferenceEqualityComparer.Instance)
            || !fastEntries.Select(entry => entry.Key).SequenceEqual(fallbackEntries.Select(entry => entry.Key))
            || !fastEntries.Select(entry => entry.Value).SequenceEqual(
                fallbackEntries.Select(entry => entry.Value), ReferenceEqualityComparer.Instance)
            || !fastEnumerated.Select(entry => entry.Key).SequenceEqual(fallbackEnumerated.Select(entry => entry.Key))
            || !fastEnumerated.Select(entry => entry.Value).SequenceEqual(
                fallbackEnumerated.Select(entry => entry.Value), ReferenceEqualityComparer.Instance))
        {
            throw new InvalidOperationException(
                $"DynamicVarBridge {label}：字段快路径与公开回退不一致。");
        }
        return $"{label}=keys:{string.Join(',', fastKeys)}";
    }
}
