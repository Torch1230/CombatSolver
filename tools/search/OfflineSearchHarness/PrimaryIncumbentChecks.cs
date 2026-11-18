using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;

namespace OfflineSearchHarness;

internal static class PrimaryIncumbentChecks
{
    internal static void RunResources(CombatState combat)
    {
        int assertions = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            assertions++;
        }
        var root = CombatRootSnapshot.Capture(combat);
        var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null);
        var initial = root.ForkSimulator();
        var initialCombat = (SimulatedCombatState)initial.State.CombatState;
        Check(root.ExhaustingGrowthUpperBound is { Royalties: 1 },
            "plain exhausting royalty has a finite cap; native=" + root.UsesKnownNativeHealingPolicy
            + "; subscribers=" + root.CapturedRunModSubscriberCount + "/" + root.CapturedCombatModSubscriberCount
            + "; powers=" + string.Join(",", initialCombat.EffectivePowers().Select(p => p.Id.Entry))
            + "; cards=" + string.Join(",", initial.State.GetPlayerCombatState(root.PlayerIdentity).AllCards
                .Select(c => c.Preview.Id.Entry + ":" + string.Join("/", c.Preview.Keywords))));
        var copied = root.ForkSimulator();
        copied.AddToCombat<DualWield>(root.PlayerIdentity.Creature, PileType.Hand, 1, creator: null);
        Check(ResourceIncumbentPolicy.CaptureExhaustingGrowthUpperBound(copied, root.PlayerIdentity) == null,
            "copy cards invalidate the growth cap");
        var closedCopy = copied.Fork();
        var royalties = closedCopy.State.GetPlayerCombatState(root.PlayerIdentity).AllCards
            .Where(card => card.Preview is Royalties).ToArray();
        foreach (var card in royalties) closedCopy.RemoveFromCombat(card);
        Check(ResourceIncumbentPolicy.CaptureExhaustingGrowthUpperBound(closedCopy, root.PlayerIdentity)
            is { Royalties: 0 }, "copying ordinary cards cannot reopen a closed growth source");
        Check(ResourceIncumbentPolicy.CaptureExhaustingGrowthUpperBound(copied, root.PlayerIdentity) == null,
            "closing a copied sibling does not close its parent");
        var returned = root.ForkSimulator();
        returned.AddToCombat<Eidolon>(root.PlayerIdentity.Creature, PileType.Hand, 1, creator: null);
        Check(ResourceIncumbentPolicy.CaptureExhaustingGrowthUpperBound(returned, root.PlayerIdentity) == null,
            "exhaust replay invalidates the growth cap");
        Check(root.ExhaustingGrowthUpperBound is { Royalties: 1 }, "siblings leave the frozen root unchanged");
        PrimaryIncumbentTable table = new();
        PrimaryIncumbentBucket feed = new(0, 0, new(Feed: 1));
        PrimaryIncumbentBucket royalty = new(0, 0, new(Royalties: 1));
        Check(ResourceIncumbentPolicy.IsPlainBucket(new(0, 0)), "plain witness may use the scalar compatibility path");
        Check(!ResourceIncumbentPolicy.IsPlainBucket(royalty), "generated growth cannot leak into scalar witnesses");
        Check(table.Tighten(feed, new(-5, 3)), "feed witness stored");
        Check(!table.TryGet(royalty, out _), "equal total counts with different sources stay separate");
        Check(table.Tighten(royalty, new(0, 4)), "royalty witness stored independently");
        Check(!table.TryGet(0, 0, out _), "reward witnesses do not enter the plain bucket");
        ulong flower = 1UL << (int)RelicCounterId.HappyFlower;
        ulong nib = 1UL << (int)RelicCounterId.PenNib;
        Check(table.Tighten(new(0, 0, default, flower), new(-3, 4)), "flower witness stored");
        Check(!table.TryGet(new(0, 0, default, nib), out _), "equal satisfied counts do not merge relic masks");
        Check(!table.TryGet(new(0, 1, default, flower), out _), "potion tier remains isolated");
        var goals = policy with
        {
            GrowthOpportunityTargets = new(new(Royalties: 1), []),
            GrowthBudgets = new(Royalties: 5),
            RelicTargets = [new(RelicCounterId.HappyFlower, 2, 2, 3, 3)],
        };
        Check(ResourceIncumbentPolicy.TryOptimisticBucket(goals, new(Royalties: 1), default(GrowthValues),
            0, 0, out var optimistic, out int credit, forfeitsGold: false) && optimistic.Growth.Royalties == 1
            && optimistic.RelicMask == flower && credit == 8,
            "unfinished route reserves every future growth and relic reward");
        Check(!table.TryGet(optimistic, out _), "partial witnesses cannot bound the optimistic goal bucket");
        table.Tighten(optimistic, new(-8, 5));
        Check(table.TryGet(optimistic, out _), "matching full-goal witness supplies its bound");
        Check(!ResourceIncumbentPolicy.TryOptimisticBucket(goals, null, default(GrowthValues),
            0, 0, out _, out _, forfeitsGold: false), "unknown growth cap never means zero future reward");
        Check(!ResourceIncumbentPolicy.TryOptimisticBucket(goals, new(Royalties: 1), new GrowthValues(Royalties: 2),
            0, 0, out _, out _, forfeitsGold: false), "observed reward beyond cap refuses pruning");
        Check(!ResourceIncumbentPolicy.TryOptimisticBucket(goals with
            { GrowthOpportunityTargets = GrowthOpportunityTargets.UnboundedForTesting("repeatable") },
            new(Royalties: 1), default(GrowthValues), 0, 0, out _, out _, forfeitsGold: false),
            "unbounded goals refuse pruning even with a nominal cap");
        Check(!ResourceIncumbentPolicy.TryOptimisticBucket(goals with
            { GrowthOpportunityTargets = new(new GrowthValues { ThirdParty = new() { ["external"] = 1 } }, []) },
            new(Royalties: 1), default(GrowthValues), 0, 0, out _, out _, forfeitsGold: false),
            "native cap cannot certify a third-party growth target");
        Check(ResourceIncumbentPolicy.TryOptimisticBucket(goals with { IgnoreLongTermRewards = true },
            null, default(GrowthValues), 0, 0, out var ignored, out int ignoredCredit, forfeitsGold: false)
            && ignored.Growth == default && ignoredCredit == 3, "ignore growth retains relic policy");
        var spentFeed = root.ForkSimulator();
        spentFeed.AddToCombat<Feed>(root.PlayerIdentity.Creature, PileType.Exhaust, 1, creator: null);
        Check(ResourceIncumbentPolicy.CaptureExhaustingGrowthUpperBound(spentFeed, root.PlayerIdentity)
            is { Feed: 0 }, "unrecoverable exhausted feed has no future activation");
        Check(StrategicHpRecoveryBound.KnownNativeHealingPotential(spentFeed, root.PlayerIdentity, 6)
            == int.MaxValue, "uncertified exhausted feed keeps the original healing reserve");
        Check(StrategicHpRecoveryBound.KnownNativeHealingPotential(spentFeed, root.PlayerIdentity, 6,
            ignoreExhaustedFeed: true) == 6, "closed exhausting loadout can release the spent feed reserve");
        var growthOnly = goals with { RelicTargets = [] };
        PrimaryIncumbentTable targets = new();
        targets.Tighten(new(0, 0, new(Royalties: 1)), new(10, 3)); // 15 HP - 5 credit
        targets.Tighten(new(0, 0, new(Royalties: 2)), new(10, 3)); // 20 HP - 10 credit
        Check(ResourceIncumbentPolicy.PruneGrowthTargets(growthOnly, new(Royalties: 2),
            new(Royalties: 1), 0, 0, 30, targets, out int examined, out int excluded, forfeitsGold: false)
            && examined == 2 && excluded == 2, "each final target consumes its own positive HP bound");
        Check(!ResourceIncumbentPolicy.PruneGrowthTargets(growthOnly, new(Royalties: 2),
            new(Royalties: 1), 0, 0, 18, targets, out examined, out excluded, forfeitsGold: false)
            && examined == 2 && excluded == 1, "one excluded target leaves the other target searchable");
        Check(ResourceIncumbentPolicy.PruneGrowthTargets(growthOnly, new(Royalties: 1),
            new(Royalties: 1), 0, 0, 18, targets, out examined, out excluded, forfeitsGold: false)
            && examined == 1 && excluded == 1, "spent opportunities close the lower-growth bucket");
        Check(!ResourceIncumbentPolicy.PruneGrowthTargets(growthOnly, new(Royalties: 3),
            new(Royalties: 1), 0, 0, 40, targets, out examined, out excluded, forfeitsGold: false)
            && examined == 3 && excluded == 2, "an unseen higher target preserves shared expansion");
        Check(!ResourceIncumbentPolicy.PruneGrowthTargets(growthOnly, null,
            new(Royalties: 1), 0, 0, 40, targets, out _, out _, forfeitsGold: false), "unknown opportunity range never closes targets");
        Check(!ResourceIncumbentPolicy.PruneGrowthTargets(growthOnly, new(Royalties: 2),
            new(Royalties: 1), 0, 1, 40, targets, out _, out _, forfeitsGold: false), "target bounds do not cross potion tiers");
        Check(!ResourceIncumbentPolicy.PruneGrowthTargets(growthOnly, new(Royalties: 2),
            new(Royalties: 1), 1, 0, 40, targets, out _, out _, forfeitsGold: false), "target bounds do not cross theft buckets");
        Check(!ResourceIncumbentPolicy.PruneGrowthTargets(growthOnly, new(Royalties: 300),
            default, 0, 0, 40, targets, out examined, out excluded, forfeitsGold: false) && examined == 0,
            "large target domains fall back without truncating reachable targets");
        Check(!ResourceIncumbentPolicy.PruneGrowthTargets(growthOnly, new(Royalties: 2),
            new(Royalties: 1), 0, 0, 14, targets, out examined, out excluded, forfeitsGold: false) && excluded == 0,
            "future healing reducing the physical lower bound keeps improving routes");
        Console.WriteLine($"RESOURCE_BUCKET_CHECKS status=Passed assertions={assertions}");
    }

    internal static void RunTheft(CombatState combat)
    {
        int assertions = 0;
        var root = CombatRootSnapshot.Capture(combat);
        var thief = combat.Enemies.First();
        var held = root.ForkSimulator();
        var state = (SimulatedCombatState)held.State.CombatState;
        state.RecordStolenCard(held);
        state.Apply<SwipePower>(thief, 1, thief);
        held.AddToCombat<StrikeIronclad>(root.PlayerIdentity.Creature, PileType.Discard, 1, creator: null);
        state.GetMutablePower<SwipePower>(thief)!.StolenCard =
            held.State.GetPlayerCombatState(root.PlayerIdentity).DiscardPile.Cards.Last().Preview;
        PowerLifecycleSupport.ResolvePowerAmountChanges(held, state);
        Check(state.OutstandingStolenResource(held) == 1, "held card counts as current theft");
        Check(state.MinimumOutstandingStolenResource(held) == 0, "living thief can return the card");
        var escaped = held.Fork();
        var escapedState = (SimulatedCombatState)escaped.State.CombatState;
        escapedState.CreatureEscaped(thief);
        Check(escapedState.OutstandingStolenResource(escaped) == 1, "escaped card remains lost");
        Check(escapedState.MinimumOutstandingStolenResource(escaped) == 1, "escape closes the loss floor");
        Check(state.MinimumOutstandingStolenResource(held) == 0, "escaped sibling cannot change parent");
        var recovered = held.Fork();
        var recoveredState = (SimulatedCombatState)recovered.State.CombatState;
        recoveredState.RecoverStolenResources(recovered, thief);
        Check(recoveredState.OutstandingStolenResource(recovered) == 0, "recovery closes the zero-loss bucket");
        Check(state.OutstandingStolenResource(held) == 1, "recovered sibling cannot change parent");
        PrimaryIncumbentTable table = new();
        table.Tighten(1, 0, new(0, 3));
        Check(!table.TryGet(state.MinimumOutstandingStolenResource(held), 0, out _),
            "lost-card witness cannot prune a potentially saved-card route");
        Check(table.TryGet(escapedState.MinimumOutstandingStolenResource(escaped), 0, out _),
            "lost-card witness can bound the escaped-card bucket");
        var healing = held.Fork();
        healing.AddToCombat<NotYet>(root.PlayerIdentity.Creature, PileType.Discard, 1, creator: null);
        var healCard = healing.State.GetPlayerCombatState(root.PlayerIdentity).DiscardPile.Cards.Last();
        healing.RemoveFromCombat(healCard);
        var healingState = (SimulatedCombatState)healing.State.CombatState;
        healingState.GetMutablePower<SwipePower>(thief)!.StolenCard = healCard.Preview;
        Check(StrategicHpRecoveryBound.KnownNativeHealingPotential(healing, root.PlayerIdentity, 6)
            == int.MaxValue, "stolen healing card reserves future healing after recovery");
        Console.WriteLine($"THEFT_BUCKET_CHECKS status=Passed assertions={assertions}");

        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            assertions++;
        }
    }

    internal static int Run()
    {
        int assertions = 0;
        PrimaryIncumbentTable table = new();
        Require(table.Tighten(0, 0, new(8, 4)), "first witness");
        Require(table.Tighten(0, 1, new(3, 5)), "separate potion tier");
        Require(table.Tighten(1, 0, new(2, 3)), "separate theft class");
        Require(table.TryGet(0, 0, out var zero) && zero.StrategicHpDeficit == 8,
            "positive potion and theft witnesses do not overwrite zero tier");
        Require(!table.TryGet(0, 2, out _), "unknown tier remains unknown");
        Require(!table.Tighten(0, 0, new(9, 1)), "worse HP cannot replace witness");
        Require(table.Tighten(0, 0, new(8, 3)), "earlier equal HP replaces witness");
        Require(!table.Tighten(0, 0, new(8, 5)), "later equal HP cannot replace witness");
        Require(!CombatBeamSolver.CanUseSharedPotionTier(0, 1, false,
            SolverPotionPolicy.Smart, false), "future potion tier remains open");
        Require(CombatBeamSolver.CanUseSharedPotionTier(1, 1, false,
            SolverPotionPolicy.Smart, false), "exact tier closes at maximum");
        Require(CombatBeamSolver.CanUseSharedPotionTier(0, null, true,
            SolverPotionPolicy.Smart, true), "explicit disabled override closes tier");
        Require(!CombatBeamSolver.CanUseSharedPotionTier(0, null, false,
            SolverPotionPolicy.Disabled, true), "forced directives keep tier open");
        SolverCombatSession session = new();
        var first = session.AcquirePrimaryIncumbents("root-A/policy-A");
        first.Tighten(0, 0, new(0, 1));
        var second = session.AcquirePrimaryIncumbents("root-A/policy-A");
        Require(!second.TryGet(0, 0, out _), "bound without retained executable witness is discarded");
        second.Tighten(0, 0, new(0, 1));
        var changed = session.AcquirePrimaryIncumbents("root-B/policy-A");
        Require(!changed.TryGet(0, 0, out _), "changed root cannot inherit old bound");
        Require(!CombatBeamSolver.ShouldPruneByPrimaryIncumbent(8, 3, new(8, 3)),
            "equal HP can retain earlier victory");
        Require(CombatBeamSolver.ShouldPruneByPrimaryIncumbent(9, 1, new(8, 3)),
            "strictly worse HP is pruned");
        Require(CombatBeamSolver.ShouldPruneByPrimaryIncumbent(8, 1, new(8, 9),
            pruneEqualHp: true), "equal loss truncates even an earlier unfinished route");
        Require(!CombatBeamSolver.ShouldPruneByPrimaryIncumbent(7, 20, new(8, 9),
            pruneEqualHp: true), "a better optimistic loss bound survives");
        Require(CombatBeamSolver.ShouldPruneByPrimaryIncumbent(0, 1, new(0, 3),
            pruneEqualHp: true), "zero-loss victory immediately bounds equal-loss continuation");
        Require(!CombatBeamSolver.ShouldPruneByPrimaryIncumbent(-1, 1, new(0, 3),
            pruneEqualHp: true), "potential recovery below incumbent remains searchable");
        Console.WriteLine($"PRIMARY_INCUMBENT_CHECKS status=Passed assertions={assertions}");
        return 0;

        void Require(bool success, string message)
        {
            if (!success) throw new InvalidOperationException(message);
            assertions++;
        }
    }
}
