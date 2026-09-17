namespace CombatSolver;

internal readonly record struct PowerTurnCardOption(
    int EnergyCost,
    int Damage,
    int Block,
    int CardAccess = 0,
    int Draws = 0,
    bool IsShiv = false,
    int UnblockedAttackHits = 0);

internal readonly record struct PowerTurnFrontierState(
    int HpLost,
    int Damage,
    int RemainingEnergy,
    int CardAccess);

/// <summary>
/// 对一手已经冻结的牌做有界子集动态规划。它比较“能否少出一张防、把省下的能量转成输出”，
/// 不启动 Simulator，也不枚举抽牌树。
/// </summary>
internal static class PowerTurnFrontier
{
    internal const int MaximumStates = 64;

    internal static IReadOnlyList<PowerTurnFrontierState> Build(
        int energy,
        int incomingDamage,
        ReadOnlySpan<PowerTurnCardOption> cards,
        int blockPerSkillBonus = 0,
        int blockPerCardBonus = 0,
        int damagePerDraw = 0,
        int damageTargets = 1,
        int damagePerShiv = 0,
        int firstShivDamageBonus = 0,
        int damagePerCard = 0,
        int damagePerUnblockedAttackHit = 0,
        int damagePerAttack = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(energy);
        ArgumentOutOfRangeException.ThrowIfNegative(incomingDamage);
        ArgumentOutOfRangeException.ThrowIfNegative(blockPerSkillBonus);
        ArgumentOutOfRangeException.ThrowIfNegative(blockPerCardBonus);
        ArgumentOutOfRangeException.ThrowIfNegative(damagePerDraw);
        ArgumentOutOfRangeException.ThrowIfNegative(damageTargets);
        ArgumentOutOfRangeException.ThrowIfNegative(damagePerShiv);
        ArgumentOutOfRangeException.ThrowIfNegative(firstShivDamageBonus);
        ArgumentOutOfRangeException.ThrowIfNegative(damagePerCard);
        ArgumentOutOfRangeException.ThrowIfNegative(damagePerUnblockedAttackHit);
        ArgumentOutOfRangeException.ThrowIfNegative(damagePerAttack);
        List<(int Spent, int Damage, int Block, int CardAccess, bool ShivPlayed)> states =
            [(0, 0, 0, 0, false)];
        foreach (PowerTurnCardOption card in cards)
        {
            int priorCount = states.Count;
            for (int index = 0; index < priorCount; index++)
            {
                var prior = states[index];
                int spent = prior.Spent + card.EnergyCost;
                if (spent > energy)
                    continue;
                int shivDamage = card.IsShiv
                    ? SaturatingAdd(
                        damagePerShiv,
                        prior.ShivPlayed ? 0 : firstShivDamageBonus)
                    : 0;
                int triggerDamage = SaturatingAdd(
                    SaturatingProduct(card.Draws, damagePerDraw, damageTargets),
                    SaturatingAdd(
                        shivDamage,
                        SaturatingAdd(
                            card.Damage > 0 ? damagePerAttack : 0,
                            SaturatingAdd(
                                damagePerCard,
                                SaturatingProduct(
                                    card.UnblockedAttackHits,
                                    damagePerUnblockedAttackHit,
                                    1)))));
                states.Add((
                    spent,
                    SaturatingAdd(
                        prior.Damage,
                        SaturatingAdd(card.Damage, triggerDamage)),
                    SaturatingAdd(
                        prior.Block,
                        card.Block
                            + (card.Block > 0 ? blockPerSkillBonus : 0)
                            + blockPerCardBonus),
                    SaturatingAdd(prior.CardAccess, card.CardAccess),
                    prior.ShivPlayed || card.IsShiv));
            }
            Prune(states, energy, incomingDamage);
        }

        return states
            .Select(state => new PowerTurnFrontierState(
                Math.Max(0, incomingDamage - state.Block),
                state.Damage,
                energy - state.Spent,
                state.CardAccess))
            .OrderBy(state => state.HpLost)
            .ThenByDescending(state => state.Damage)
            .ThenByDescending(state => state.RemainingEnergy)
            .ThenByDescending(state => state.CardAccess)
            .Take(MaximumStates)
            .ToArray();
    }

    internal static int DefensiveDamageUplift(
        IReadOnlyList<PowerTurnFrontierState> baseline,
        IReadOnlyList<PowerTurnFrontierState> powered)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(powered);
        int best = 0;
        foreach (PowerTurnFrontierState poweredState in powered)
        {
            int baselineDamage = baseline
                .Where(state => state.HpLost <= poweredState.HpLost)
                .Select(state => state.Damage)
                .DefaultIfEmpty(int.MinValue)
                .Max();
            if (baselineDamage != int.MinValue)
                best = Math.Max(best, poweredState.Damage - baselineDamage);
        }
        return best;
    }

    internal static int DefensiveHpUplift(
        IReadOnlyList<PowerTurnFrontierState> baseline,
        IReadOnlyList<PowerTurnFrontierState> powered)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(powered);
        int best = 0;
        foreach (PowerTurnFrontierState poweredState in powered)
        {
            int baselineLoss = baseline
                .Where(state => state.Damage >= poweredState.Damage)
                .Select(state => state.HpLost)
                .DefaultIfEmpty(int.MaxValue)
                .Min();
            if (baselineLoss != int.MaxValue)
                best = Math.Max(best, baselineLoss - poweredState.HpLost);
        }
        return best;
    }

    private static void Prune(
        List<(int Spent, int Damage, int Block, int CardAccess, bool ShivPlayed)> states,
        int energy,
        int incomingDamage)
    {
        states.Sort((left, right) =>
        {
            int comparison = Math.Max(0, incomingDamage - left.Block)
                .CompareTo(Math.Max(0, incomingDamage - right.Block));
            if (comparison != 0) return comparison;
            comparison = right.Damage.CompareTo(left.Damage);
            if (comparison != 0) return comparison;
            comparison = left.Spent.CompareTo(right.Spent);
            return comparison != 0 ? comparison : right.CardAccess.CompareTo(left.CardAccess);
        });
        List<(int Spent, int Damage, int Block, int CardAccess, bool ShivPlayed)> kept =
            new(MaximumStates);
        foreach (var candidate in states)
        {
            if (kept.Any(other =>
                    Math.Max(0, incomingDamage - other.Block) <= Math.Max(0, incomingDamage - candidate.Block)
                    && other.Damage >= candidate.Damage
                    && other.Spent <= candidate.Spent
                    && other.CardAccess >= candidate.CardAccess
                    && (other.ShivPlayed == candidate.ShivPlayed || !other.ShivPlayed)))
            {
                continue;
            }
            kept.Add(candidate);
            if (kept.Count == MaximumStates)
                break;
        }
        states.Clear();
        states.AddRange(kept);
    }

    private static int SaturatingAdd(int left, int right)
        => (int)Math.Clamp((long)left + right, 0L, int.MaxValue);

    private static int SaturatingProduct(int first, int second, int third)
        => (int)Math.Clamp((long)first * second * third, 0L, int.MaxValue);
}
