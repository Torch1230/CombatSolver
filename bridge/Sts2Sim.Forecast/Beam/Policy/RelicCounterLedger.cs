using System.Reflection;
using System.Collections.Concurrent;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Entities.Relics;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Relics;

namespace CombatSolver.Beam.Policy;

internal readonly record struct RelicCounterEvaluation(
    int Satisfied, int TargetCount, int HpCredit, int PriorityScore, int Distance)
{
    public bool AllSatisfied => Satisfied == TargetCount;
}

// RelicCounterPolicy.cs and RelicCounterCatalog.cs @ d231e9e5.
// Core clones preserve these counters in their exact fingerprint but expose no
// typed read API. This read-only adapter is provisional pending #269's D4 ruling;
// missing fields fail loudly so game updates cannot silently corrupt targets.
internal sealed class RelicCounterLedger
{
    private static readonly ConcurrentDictionary<(Type Type, string Name), FieldInfo> CounterFields = new();
    private static readonly Dictionary<RelicCounterId, int> Periods = new()
    {
        [RelicCounterId.HappyFlower] = 3,
        [RelicCounterId.FakeHappyFlower] = 5,
        [RelicCounterId.Pendulum] = 3,
        [RelicCounterId.PollinousCore] = 4,
        [RelicCounterId.PenNib] = 10,
        [RelicCounterId.Nunchaku] = 10,
        [RelicCounterId.TuningFork] = 10,
        [RelicCounterId.JossPaper] = 5,
        [RelicCounterId.IronClub] = 4,
        [RelicCounterId.GalacticDust] = 10,
        [RelicCounterId.MeatOnTheBone] = 2,
    };

    private readonly RelicCounterTarget[] _targets;

    private RelicCounterLedger(RelicCounterTarget[] targets) => _targets = targets;
    public bool HasTargets => _targets.Length > 0;

    public static RelicCounterLedger Capture(CombatState root, CombatPolicy policy)
    {
        if (!policy.RelicStrategyEnabled || policy.IgnoreLongTermRewards)
            return new RelicCounterLedger([]);
        IReadOnlyList<RelicCounterTarget> configured = policy.RelicTargets.Count > 0
            ? policy.RelicTargets
            : policy.RelicCounterRules.Where(rule => rule.Enabled)
                .Select(rule => new RelicCounterTarget(rule.Id, rule.Minimum, rule.Maximum,
                    rule.HpAllowance, Periods[rule.Id], rule.Priority)).ToArray();
        var owned = root.Players[0].Relics.Where(relic => !relic.IsMelted)
            .Select(Identify).OfType<RelicCounterId>().ToHashSet();
        RelicCounterTarget[] targets = configured.Where(target => owned.Contains(target.Id))
            .OrderBy(target => target.Id).ToArray();
        foreach (RelicCounterTarget target in targets)
        {
            if (target.Period != Periods[target.Id] || target.Maximum >= target.Period)
                throw new InvalidDataException($"Invalid {target.Id} relic counter period or range.");
        }
        return new RelicCounterLedger(targets);
    }

    public RelicCounterEvaluation Evaluate(CombatState state)
    {
        if (!HasTargets) return default;
        IReadOnlyList<RelicModel> playerRelics = state.Players[0].Relics;
        int targetCount = _targets.Length;
        Span<int> matchedIndices = stackalloc int[targetCount];
        matchedIndices.Fill(-1);
        for (int r = 0; r < playerRelics.Count; r++)
        {
            RelicModel relic = playerRelics[r];
            if (relic.IsMelted) continue;
            RelicCounterId? id = Identify(relic);
            if (!id.HasValue) continue;
            for (int t = 0; t < targetCount; t++)
            {
                if (_targets[t].Id == id.Value)
                {
                    if (matchedIndices[t] >= 0)
                        throw new ArgumentException($"Duplicate relic counter id {id.Value}.");
                    matchedIndices[t] = r;
                    break;
                }
            }
        }

        int satisfied = 0;
        int credit = 0;
        int priority = 0;
        int distance = 0;
        for (int t = 0; t < targetCount; t++)
        {
            RelicCounterTarget target = _targets[t];
            int relicIndex = matchedIndices[t];
            if (relicIndex < 0)
                throw new KeyNotFoundException($"Missing relic counter target {target.Id}.");
            RelicModel relic = playerRelics[relicIndex];
            int value = ReadCounter(relic, state) % target.Period;
            bool hit = value >= target.Minimum && value <= target.Maximum;
            if (hit)
            {
                satisfied++;
                if (target.Id != RelicCounterId.MeatOnTheBone)
                {
                    credit += target.HpAllowance;
                    priority += 1 << ((target.Priority - 1) * 4);
                }
            }
            else
            {
                distance += value < target.Minimum ? target.Minimum - value
                    : target.Period - value + target.Minimum;
            }
        }
        return new RelicCounterEvaluation(satisfied, targetCount, credit, priority, distance);
    }

    private static RelicCounterId? Identify(RelicModel relic) => relic switch
    {
        HappyFlower => RelicCounterId.HappyFlower,
        FakeHappyFlower => RelicCounterId.FakeHappyFlower,
        Pendulum => RelicCounterId.Pendulum,
        PollinousCore => RelicCounterId.PollinousCore,
        PenNib => RelicCounterId.PenNib,
        Nunchaku => RelicCounterId.Nunchaku,
        TuningFork => RelicCounterId.TuningFork,
        JossPaper => RelicCounterId.JossPaper,
        IronClub => RelicCounterId.IronClub,
        GalacticDust => RelicCounterId.GalacticDust,
        MeatOnTheBone => RelicCounterId.MeatOnTheBone,
        _ => null,
    };

    private static int ReadCounter(RelicModel relic, CombatState state) => relic switch
    {
        HappyFlower => Field(relic, "_turnCounter"),
        FakeHappyFlower flower => flower.TurnsSeen,
        Pendulum => Field(relic, "_turnCounter"),
        PollinousCore => Field(relic, "_handsSeen"),
        PenNib => Field(relic, "_attacksPlayed"),
        Nunchaku => Field(relic, "_attacksPlayed"),
        TuningFork => Field(relic, "_skillsPlayed"),
        JossPaper => Field(relic, "_cardsExhausted"),
        IronClub => Field(relic, "_cardsPlayed"),
        GalacticDust => Field(relic, "_starsSpent"),
        MeatOnTheBone => state.Players[0].Creature.CurrentHp * 2
            <= state.Players[0].Creature.MaxHp ? 1 : 0,
        _ => throw new ArgumentOutOfRangeException(nameof(relic)),
    };

    private static int Field(RelicModel relic, string name)
    {
        FieldInfo info = CounterFields.GetOrAdd((relic.GetType(), name), key =>
            key.Type.GetField(key.Name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(key.Type.FullName, key.Name));
        return info.GetValue(relic) is int value ? value
            : throw new InvalidDataException($"{relic.GetType().Name}.{name} is not an integer.");
    }
}
