using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Creatures;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    internal sealed class ReadViewInvariantCache
    {
        internal EnemyEvaluationValues? Enemies;
        internal ThreatFocus? Focus;
        internal (StrategicEffectRequirements Requirements, int EnemyHp, int Damage, int Hits, StrategicEffectContext Value)? Strategy;
        internal readonly Dictionary<PredictedCard, double> CardValues = new(ReferenceEqualityComparer.Instance);
        internal int EnemyBuilds, FocusBuilds, StrategyBuilds;
    }

    internal readonly record struct EnemyEvaluationValues(int Hp, int Block, int RawHp, int MaxHp,
        int Reviving, int Alive, ulong AliveMask, StateFingerprint Distribution, EnemyDurabilityVector Durability);

    private static EnemyEvaluationValues GetEnemyEvaluation(CombatPredictionSimulator simulator,
        SimulatedCombatState combat, CompletedStateReadView? view)
    {
        ReadViewInvariantCache? cache = view?.EnemyValuesInvariant == true ? view.Invariants : null;
        if (cache?.Enemies is { } cached) return cached;
        int hp = 0, block = 0, rawHp = 0, maxHp = 0, reviving = 0, alive = 0;
        ulong aliveMask = 0;
        EnemyDurabilityVectorBuilder durability = new(combat.KnownEnemies.Count);
        StateFingerprintBuilder distribution = new();
        for (int index = 0; index < combat.KnownEnemies.Count; index++)
        {
            var creature = combat.KnownEnemies[index];
            CreatureReadValues enemy = ReadCreatureValues(simulator, creature, view);
            int effectiveHp = combat.EffectiveEnemyHp(creature, enemy.CurrentHp, enemy.MaxHp);
            distribution.Add(creature.CombatId ?? uint.MaxValue);
            distribution.Add(enemy.CurrentHp);
            distribution.Add(enemy.Block);
            distribution.Add(effectiveHp);
            distribution.Add(enemy.Present);
            durability.Set(index, new EnemyDurabilityEntry(creature.CombatId ?? uint.MaxValue,
                Math.Max(0, effectiveHp) + Math.Max(0, enemy.Block)));
            hp += effectiveHp;
            if (effectiveHp > 0 && enemy.Present) block += Math.Max(0, enemy.Block);
            rawHp += Math.Max(0, enemy.CurrentHp);
            maxHp = Math.Max(maxHp, Math.Max(0, enemy.CurrentHp));
            if (enemy.CurrentHp <= 0 && effectiveHp > 0) reviving++;
            if (effectiveHp > 0 && enemy.Present) { alive++; aliveMask |= 1UL << index; }
        }
        EnemyEvaluationValues result = new(hp, block, rawHp, maxHp, reviving, alive, aliveMask, distribution.Finish(), durability.Build());
        if (cache != null) { cache.Enemies = result; cache.EnemyBuilds++; }
        return result;
    }

    private static ThreatFocus GetThreatFocus(CombatPredictionSimulator simulator,
        SimulatedCombatState combat, CompletedStateReadView? view)
    {
        ReadViewInvariantCache? cache = view?.EnemyValuesInvariant == true ? view.Invariants : null;
        if (cache?.Focus is { } cached) return cached;
        ThreatFocus result = BuildThreatFocus(simulator, combat, view);
        if (cache != null) { cache.Focus = result; cache.FocusBuilds++; }
        return result;
    }

    private static StrategicEffectContext GetStrategicContext(IReadOnlyList<PredictedCard> cards,
        int enemyHp, int damage, int hits, StrategicEffectRequirements requirements, ReadViewInvariantCache? cache)
    {
        if (cache?.Strategy is { } cached && cached.Requirements == requirements
            && cached.EnemyHp == enemyHp && cached.Damage == damage && cached.Hits == hits) return cached.Value;
        StrategicEffectContext result = StrategicEffectContext.Build(cards, enemyHp, damage, hits, requirements);
        if (cache != null) { cache.Strategy = (requirements, enemyHp, damage, hits, result); cache.StrategyBuilds++; }
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static CreatureReadValues ReadCreatureValues(CombatPredictionSimulator simulator,
        Creature creature, CompletedStateReadView? view)
        => view is null ? CreatureReadValues.Capture(simulator, creature) : view.ReadCreature(creature);

    internal static int ReadSandpitRemaining(CombatPredictionSimulator simulator,
        IReadOnlyList<PowerModel> powers, Creature target, CompletedStateReadView? view)
    {
        int remaining = 0;
        for (int index = 0; index < powers.Count; index++)
        {
            if (powers[index] is SandpitPower power && ReferenceEquals(power.Target, target)
                && ReadCreatureValues(simulator, power.Owner, view).IsAlive)
            {
                // Enumerable.Sum uses checked addition. Keep that rule without capturing the
                // reader in the per-snapshot closure, including when no Sandpit exists.
                remaining = checked(remaining + Math.Max(0, power.Amount));
            }
        }
        return remaining;
    }

    private static double ReadCardValue(PredictedCard card, ReadViewInvariantCache? cache)
    {
        if (cache == null) return CardChoiceSupport.CardValue(card.Preview);
        if (cache.CardValues.TryGetValue(card, out double value)) return value;
        value = CardChoiceSupport.CardValue(card.Preview);
        cache.CardValues.Add(card, value);
        return value;
    }

    private StateFingerprint BuildPileValuesFingerprint(IReadOnlyList<PredictedCard> cards)
    {
        StateFingerprintBuilder key = new();
        key.Add(cards.Count);
        for (int index = 0; index < cards.Count; index++)
        {
            PredictedCard card = cards[index];
            StateFingerprint item = BuildCardStateFingerprint(card);
            key.Add(item.First);
            key.Add(item.Second);
        }
        return key.Finish();
    }

    private void AppendPileValues(ref StateFingerprintBuilder key, IReadOnlyList<PredictedCard> cards, char marker)
    {
        key.Add(marker);
        key.Add(cards.Count);
        StateFingerprint pile = BuildPileValuesFingerprint(cards);
        key.Add(pile.First);
        key.Add(pile.Second);
    }

    private static StateFingerprint BuildCyclePileShapeKey(IReadOnlyList<PredictedCard> hand,
        IReadOnlyList<PredictedCard> draw, IReadOnlyList<PredictedCard> discard, IReadOnlyList<PredictedCard> exhaust)
    {
        StateFingerprintBuilder key = new();
        AppendCyclePileValues(ref key, hand, 'H');
        AppendCyclePileValues(ref key, draw, 'D');
        AppendCyclePileValues(ref key, discard, 'C');
        AppendCyclePileValues(ref key, exhaust, 'X');
        return key.Finish();
    }

    private static (ulong First, ulong Second) CyclePileValues(IReadOnlyList<PredictedCard> cards)
    {
        ulong first = 0, second = 0;
        for (int index = 0; index < cards.Count; index++)
        {
            PredictedCard card = cards[index];
            StateFingerprintBuilder item = new();
            item.Add(card.Preview.Id.Entry);
            item.Add(card.Preview.CurrentUpgradeLevel);
            StateFingerprint value = item.Finish();
            first += StateFingerprintBuilder.MixFirst(value.First);
            second += StateFingerprintBuilder.MixSecond(value.Second);
        }
        return (first, second);
    }

    private static void AppendCyclePileValues(ref StateFingerprintBuilder key, IReadOnlyList<PredictedCard> cards, char marker)
    {
        var (first, second) = CyclePileValues(cards);
        key.Add(marker);
        key.Add(cards.Count);
        key.Add(first);
        key.Add(second);
    }
}
