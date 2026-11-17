using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    internal static string AssertRetainedPrimaryEdgesForTesting(CombatRootSnapshot root,
        SolverDisplayNames names, BattleDamageSnapshot damage, SearchPolicySnapshot policy, CancellationToken token)
    {
        using var isolation = SimulationNotificationIsolation.Enter();
        var producer = new CombatBeamSolver(root, names, damage, policy with { PrimaryIncumbents = new() }, token,
            searchProfile: policy.Profile, potionPolicyOverride: SolverPotionPolicy.Disabled, maximumPotionUses: 0);
        var victory = producer.Solve();
        var witness = CombatSearchCoordinator.BuildRetainedPrimarySearchIncumbent(root, policy, victory);
        if (witness is not { StrategicHpDeficit: 0 })
            throw new InvalidOperationException("Retained bound requires an actual complete policy-qualified victory.");
        CombatBeamSolver Consumer(SearchPolicySnapshot? p = null, int? min = null, int? max = null,
            SolverPotionPolicy? potionOverride = null, bool supplyWitness = true)
            => new(root, names, damage, (p ?? policy) with { PrimaryIncumbents = new() }, token,
                searchProfile: policy.Profile, potionPolicyOverride: potionOverride, minimumPotionUses: min,
                maximumPotionUses: max, retainedPrimaryIncumbent: supplyWitness ? witness : null);
        var consumer = Consumer();
        var opening = CreateOpeningSearchSeed(consumer.Replay([]));
        SearchNode Card(SearchNode parent, string id)
            => consumer.CostChildForTesting(parent,
                consumer.PrepareCardActions(parent).First(a => a.Action.CardId == id).Action);
        var hurt = Card(Card(opening, "OFFERING"), "BLOODLETTING");
        if (hurt.IsTerminal || hurt.Snapshot.CumulativePlayerHpLost != 9
            || consumer.ApplyPrimaryIncumbentBound([hurt]).Count != 0
            || consumer.ApplyPrimaryIncumbentBound([opening]).Count != 1)
            throw new InvalidOperationException("Open-potion continuation lost equality or missed strict HP dominance.");
        var p1 = consumer.CostChildForTesting(hurt, consumer.PreparePotionActions(hurt).First().Action);
        var p2 = consumer.CostChildForTesting(p1, consumer.PreparePotionActions(p1).First().Action);
        if (ExplicitPotionUseCount(p1) != 1 || ExplicitPotionUseCount(p2) != 2
            || consumer.ApplyPrimaryIncumbentBound([p1, p2]).Count != 0)
            throw new InvalidOperationException("Already-paid potion counts changed the strict primary proof.");
        foreach (var member in new[]
        {
            Consumer(supplyWitness: false), Consumer(min: 1, max: 1), Consumer(max: 0),
            Consumer(potionOverride: SolverPotionPolicy.Disabled),
            Consumer(policy with { GrowthBudgets = new GrowthValues(HandOfGreed: 1) }),
            Consumer(policy with { RelicTargets = [new(RelicCounterId.MeatOnTheBone, 0, 1, 0, 2)] }),
            Consumer(policy with { TheftPolicy = SolverTheftPolicy.PreserveResources }),
            Consumer(policy with { PotionStrategy = new(SolverPotionPolicy.Smart,
                [new PotionSlotDirective(0, "SHACKLING_POTION", SolverPotionDirective.Force)]) }),
        })
            if (member.ApplyPrimaryIncumbentBound([hurt]).Count != 1)
                throw new InvalidOperationException("Retained-route proof escaped its policy or request scope.");
        if (CombatSearchCoordinator.BuildRetainedPrimarySearchIncumbent(root,
                policy with { DisableRefinementIncumbentForTesting = true }, victory) is not null
            || CombatSearchCoordinator.BuildRetainedPrimarySearchIncumbent(root,
                policy with { GrowthBudgets = new GrowthValues(HandOfGreed: 1) }, victory) is not null)
            throw new InvalidOperationException("Retained victory bypassed its publication gate.");
        Parallel.For(0, 16, _ =>
        {
            var member = Consumer();
            if (member.ApplyPrimaryIncumbentBound([hurt]).Count != 0)
                throw new InvalidOperationException("Immutable retained witness changed across consumers.");
        });
        var fork = ((CombatPredictionSimulator)hurt.Snapshot.Simulator).Fork();
        fork.AddGeneratedCardToCombat(PredictedCard.Create(ModelDb.Card<Feed>(), root.PlayerIdentity),
            PileType.Exhaust, root.PlayerIdentity, resultKind: CardGenerationResultKind.Fixed);
        var unknown = hurt with { Snapshot = consumer.Snapshot(fork, hurt.Turn, hurt.ActionCount,
            hurt.Snapshot.ShufflesCrossed, SearchBoundaryReason.None, hurt.Snapshot.ProcessedEnemyDeaths) };
        if (consumer.ApplyPrimaryIncumbentBound([unknown]).Count != 1
            || consumer._retainedPrimaryBoundUnknown == 0
            || consumer.ApplyPrimaryIncumbentBound([hurt]).Count != 0)
            throw new InvalidOperationException("Unknown exhausted source was pruned or leaked into its parent.");
        return "RetainedPrimary:ActualCompleteWinner:OpenP0P1P2StrictLossCut:EqualHpKept:" +
            "ExactQuotaDisabledForceGrowthRelicTheftRefused:UnknownExhaustedFeedKept:" +
            "16IndependentConsumers:ForkParentLiveRngIsolation:FullIncrementalReplay";
    }
}
