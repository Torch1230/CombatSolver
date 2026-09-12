using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;

namespace CombatSolver;

internal sealed record SolverStrategyOutcome(string Text, bool Satisfied);

internal static class SolverStrategyOutcomeText
{
    internal static string? Format(RelicCounterEvaluation counters, GrowthValues rewards, bool victory)
    {
        var items = Capture(counters, rewards, victory);
        return items.Count == 0 ? null : string.Join("  │  ", items.Select(item => item.Text));
    }

    internal static IReadOnlyList<SolverStrategyOutcome> Capture(RelicCounterEvaluation counters, GrowthValues rewards, bool victory)
    {
        List<string> completed = [], pending = [], growth = [];
        foreach (var entry in RelicCounterCatalog.All)
        {
            ulong bit = 1UL << (int)entry.Id;
            if ((counters.TargetMask & bit) == 0) continue;
            string name = entry.Canonical().Title.GetFormattedText();
            int value = counters.Value(entry.Id);
            if (entry.Id == RelicCounterId.MeatOnTheBone)
            {
                ((counters.SatisfiedMask & bit) != 0 && victory ? completed : pending)
                    .Add(name + "（" + SolverText.Get("半血回血") + "）");
                continue;
            }
            if ((counters.SatisfiedMask & bit) != 0 && victory) completed.Add($"{name} {value}");
            else
            {
                int min = counters.Minimum(entry.Id), max = counters.Maximum(entry.Id);
                string target = min == max ? min.ToString() : $"{min}–{max}";
                pending.Add(SolverText.Format($"{name} {target} 实际 {value}"));
            }
        }
        foreach (GrowthSource source in Enum.GetValues<GrowthSource>())
        {
            int count = rewards.Get(source);
            if (count <= 0) continue;
            string name = source == GrowthSource.Goopy
                ? ModelDb.Enchantment<Goopy>().Title.GetFormattedText()
                : ModelDb.AllCards.Single(card => card.GetType().Name == source.ToString()).Title;
            growth.Add(count == 1 ? name : $"{name} ×{count}");
        }
        foreach (var entry in GrowthSourceMirrors.All)
        {
            int count = rewards.Get(new GrowthSourceHandle(entry.Id));
            if (count <= 0) continue;
            var card = entry.Card();
            string name = entry.Title?.Invoke(card) ?? card.Title;
            growth.Add(count == 1 ? name : $"{name} ×{count}");
        }
        List<SolverStrategyOutcome> parts = [];
        string Join(List<string> values) => string.Join(SolverText.IsEnglish ? ", " : "、", values);
        if (completed.Count > 0) parts.Add(new(SolverText.Format($"已卡：{Join(completed)}"), true));
        if (pending.Count > 0) parts.Add(new(SolverText.Format($"未达标：{Join(pending)}"), false));
        if (growth.Count > 0) parts.Add(new(SolverText.Format($"已获得：{Join(growth)}"), true));
        return parts;
    }
}
