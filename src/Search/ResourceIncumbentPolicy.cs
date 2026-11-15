using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CombatSolver;

internal static class ResourceIncumbentPolicy
{
    private static readonly GrowthSource[] Sources = Enum.GetValues<GrowthSource>();
    internal static bool IsPlainBucket(PrimaryIncumbentBucket bucket)
        => IsPrimaryHpBucket(bucket, allowZeroHpRelicGoals: false);

    internal static bool IsPrimaryHpBucket(PrimaryIncumbentBucket bucket, bool allowZeroHpRelicGoals)
        => bucket.Growth.Total == 0 && (bucket.RelicMask == 0 || allowZeroHpRelicGoals);

    internal static PrimaryIncumbentBucket CompletedBucket(SimulationSnapshot snapshot, int potions)
        => new(snapshot.OutstandingStolenResource, potions, snapshot.GrowthRewards,
            snapshot.RelicCounters.SatisfiedMask, snapshot.ForfeitsEncounterGold);

    internal static PrimaryIncumbentBucket CompletedBucket(SolverSnapshot snapshot, int potions)
        => new(snapshot.OutstandingStolenResource, potions, snapshot.GrowthRewards,
            snapshot.RelicCounters.SatisfiedMask, snapshot.ForfeitsEncounterGold);

    // A frozen opportunity target is not by itself a proof of a global reward cap.
    // Certify a small closed loadout: no generation, exhaust recovery, live
    // growth copying, replay powers/relics, callbacks, or reward-granting potions.
    internal static GrowthValues? CaptureExhaustingGrowthUpperBound(
        CombatPredictionSimulator simulator, Player player)
    {
        var combat = (SimulatedCombatState)simulator.State.CombatState;
        if (!StrategicHpRecoveryBound.CanUseKnownNativeHealingPolicy(simulator, player)
            || GrowthSourceMirrors.All.Count != 0
            || combat.RootRunModSubscriberCount != 0 || combat.RootCombatModSubscriberCount != 0
            || combat.HasPendingReturningCards
            || combat.PotionUses.Count != 0
            || Enumerable.Range(0, ((ICombatPredictionPlayerLimits)combat).GetPotionSlotCount(player))
                .Any(slot => combat.GetPotionAtSlot(player, slot) != null)
            || combat.RelicsOf(player).Any(relic => !relic.IsMelted && relic is not
                (BurningBlood or BlackBlood or RingOfTheSnake or BoundPhylactery
                or DivineRight or HappyFlower or PenNib or Nunchaku))
            || combat.EffectivePowers().Any(power => ReferenceEquals(power.Owner, player.Creature)
                && power is not (StrengthPower or DexterityPower or WeakPower or VulnerablePower
                    or FrailPower or ThornsPower or RoyaltiesPower))
            || combat.EffectivePowers().OfType<SwipePower>().Any(power => power.StolenCard != null))
            return null;

        GrowthValues upper = combat.GrowthRewards;
        if (!upper.Extras.IsEmpty) return null;
        bool hasCopyCard = false;
        foreach (var card in simulator.State.GetPlayerCombatState(player).AllCards)
        {
            CardModel model = card.Preview;
            if (model.Enchantment != null || model.Affliction != null) return null;
            // Once no growth card remains available, copying ordinary attacks
            // cannot reopen growth. Before that point DualWield remains unknown.
            if (model is DualWield)
            {
                hasCopyCard = true;
                continue;
            }
            GrowthSource? source = model switch
            {
                Feed => GrowthSource.Feed,
                TheHunt => GrowthSource.TheHunt,
                Royalties => GrowthSource.Royalties,
                GeneticAlgorithm when model.DeckVersion != null => GrowthSource.GeneticAlgorithm,
                TheScythe when model.DeckVersion != null => GrowthSource.TheScythe,
                _ => null,
            };
            if (source is { } growth)
            {
                if (!card.HasKeyword(simulator.State, CardKeyword.Exhaust)
                    && !(model is Royalties && model.Type == CardType.Power)) return null;
                if (!simulator.State.GetPlayerCombatState(player).ExhaustPile.Cards.Contains(card))
                    upper = upper.With(growth, checked(upper.Get(growth) + 1));
            }
            else if (model is not (StrikeIronclad or DefendIronclad or Bash
                or StrikeSilent or DefendSilent or Neutralize or Survivor
                or StrikeNecrobinder or DefendNecrobinder or Bodyguard or Unleash
                or StrikeRegent or DefendRegent)
                && model.Type is not (CardType.Status or CardType.Curse))
                return null;
        }
        return hasCopyCard && upper != combat.GrowthRewards ? null : upper;
    }

    // Each final growth vector has its own HP threshold. Shared expansion stops
    // only when every conservatively reachable target is bounded by a witness.
    // Missing witnesses and excessively large domains always preserve the node.
    internal static bool PruneGrowthTargets(SearchPolicySnapshot policy, GrowthValues? upperBound,
        GrowthValues realized, int stolen, int potions, int physicalHpLowerBound,
        PrimaryIncumbentTable table, out int examined, out int excluded, bool forfeitsGold = false)
    {
        examined = excluded = 0;
        if (policy.IgnoreLongTermRewards || policy.RelicTargets.Count != 0
            || !realized.Extras.IsEmpty || !policy.GrowthOpportunityTargets.RequiredRewards.Extras.IsEmpty
            || upperBound is not { } upper || !upper.Extras.IsEmpty || !upper.Satisfies(realized))
            return false;
        int size = 1;
        foreach (GrowthSource source in Sources)
        {
            long width = (long)upper.Get(source) - realized.Get(source) + 1;
            if (realized.Get(source) < 0 || width > 256 / size) return false;
            size *= (int)width;
        }
        int visited = 0, bounded = 0;
        Visit(0, realized);
        examined = visited;
        excluded = bounded;
        return visited != 0 && visited == bounded;

        void Visit(int index, GrowthValues target)
        {
            if (index == Sources.Length)
            {
                visited++;
                if (table.TryGet(new(stolen, potions, target, ForfeitsEncounterGold: forfeitsGold), out var witness)
                    && physicalHpLowerBound - policy.EffectiveGrowthBudgets.Credit(target)
                        >= witness.StrategicHpDeficit)
                    bounded++;
                return;
            }
            GrowthSource source = Sources[index];
            int count = realized.Get(source);
            while (true)
            {
                Visit(index + 1, target.With(source, count));
                if (count == upper.Get(source)) break;
                count++;
            }
        }
    }

    internal static bool TryOptimisticBucket(SearchPolicySnapshot policy, GrowthValues? growthUpper,
        SimulationSnapshot snapshot, int stolen, int potions,
        out PrimaryIncumbentBucket bucket, out int rewardCredit)
        => TryOptimisticBucket(policy, growthUpper, snapshot.GrowthRewards, stolen, potions,
            out bucket, out rewardCredit, snapshot.ForfeitsEncounterGold);

    internal static bool TryOptimisticBucket(SearchPolicySnapshot policy, GrowthValues? growthUpper,
        GrowthValues realized, int stolen, int potions,
        out PrimaryIncumbentBucket bucket, out int rewardCredit, bool forfeitsGold = false)
    {
        bucket = default;
        rewardCredit = 0;
        GrowthValues optimistic = default;
        if (!policy.IgnoreLongTermRewards && (policy.EffectiveHasGrowthTargets || realized.Total != 0))
        {
            if (!policy.GrowthOpportunityTargets.IsBounded
                || !policy.GrowthOpportunityTargets.RequiredRewards.Extras.IsEmpty
                || growthUpper is not { } upper
                || !upper.Satisfies(realized)) return false;
            optimistic = upper;
        }
        ulong relicMask = 0;
        foreach (var target in policy.RelicTargets)
        {
            relicMask |= 1UL << (int)target.Id;
            if (target.Id != RelicCounterId.MeatOnTheBone)
                rewardCredit = checked(rewardCredit + target.HpAllowance);
        }
        rewardCredit = checked(rewardCredit + policy.EffectiveGrowthBudgets.Credit(optimistic));
        bucket = new(stolen, potions, optimistic, relicMask, forfeitsGold);
        return true;
    }
}
