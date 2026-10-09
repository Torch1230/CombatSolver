using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertZeroCreditGrowthProofAsync(CombatState live, Player player, bool regen)
    {
        foreach (var relic in player.Relics.ToArray())
            if (relic is not BoundPhylactery) await RelicCmd.Remove(relic);
        foreach (var power in live.Creatures.SelectMany(creature => creature.Powers).ToArray())
            if (power is not DieForYouPower) await PowerCmd.Remove(power);
        foreach (var relic in new RelicModel[] { ModelDb.Relic<LetterOpener>().ToMutable(),
            ModelDb.Relic<BowlerHat>().ToMutable(), ModelDb.Relic<WhiteStar>().ToMutable(),
            ModelDb.Relic<SparklingRouge>().ToMutable(), ModelDb.Relic<RainbowRing>().ToMutable(),
            ModelDb.Relic<CrackedCore>().ToMutable(), ModelDb.Relic<BurningSticks>().ToMutable(),
            ModelDb.Relic<Kusarigama>().ToMutable(), ModelDb.Relic<HornCleat>().ToMutable(),
            ModelDb.Relic<Bread>().ToMutable(), ModelDb.Relic<CaptainsWheel>().ToMutable(),
            ModelDb.Relic<GnarledHammer>().ToMutable() })
            player.AddRelicInternal(relic);
        await ClearPlayerPilesAsync(player);
        for (int index = 0; index < 2; index++)
            await InjectCardAsync(live, player, new() { CardId = "STRIKE_NECROBINDER", Pile = "Hand" });
        foreach (string id in new[] { "BODYGUARD", "DEFEND_NECROBINDER", "DEFY", "DELAY",
            "DEVOUR_LIFE", "DIRGE", "DREDGE", "NECRO_MASTERY", "NO_ESCAPE", "SEVERANCE",
            "SHARED_FATE", "SIC_EM", "TRANSFIGURE", "UNLEASH", "JACK_OF_ALL_TRADES", "JACKPOT",
            "VEILPIERCER", "ARSENAL", "DEFEND_DEFECT", "BUNDLE_OF_JOY" })
            await InjectCardAsync(live, player, new() { CardId = id, Pile = "Draw" });
        foreach (string id in new[] { "BODYGUARD", "DEFEND_NECROBINDER", "DRAIN_POWER", "DREDGE", "HAND_OF_GREED", "JACKPOT", "NEGATIVE_PULSE", "NO_ESCAPE", "PAGESTORM", "PARSE", "PUTREFY", "SOUL_STORM", "SPIRIT_OF_ASH", "SQUEEZE", "UNLEASH", "VEILPIERCER", "BEGONE", "BUNDLE_OF_JOY", "CRASH_LANDING", "DEFEND_REGENT", "ENTROPY", "FALLING_STAR", "GLOW", "HEGEMONY", "I_AM_INVINCIBLE", "MAKE_IT_SO", "NEUTRON_AEGIS", "SEVEN_STARS", "SOLAR_STRIKE", "STRIKE_REGENT", "THINKING_AHEAD", "VENERATE", "VOID_FORM", "BOOST_AWAY", "BULK_UP", "CALAMITY", "CLAW", "COMPACT", "DARKNESS", "DEFEND_DEFECT", "DRAMATIC_ENTRANCE", "DUALCAST", "MODDED", "NULL", "OVERCLOCK", "REFRACT", "SCRAPE", "STRIKE_DEFECT", "VOLTAIC", "ZAP", "ARSENAL", "COMET", "GLIMMER", "GUIDING_STAR", "REFINE_BLADE", "SECRET_WEAPON", "SEEKING_EDGE", "ULTIMATE_DEFEND" })
            await InjectCardAsync(live, player, new() { CardId = id, Pile = "Draw" });
        await InjectCardAsync(live, player, new() { CardId = "BLOODLETTING", Pile = "Hand", Count = 1 });
        await InjectCardAsync(live, player, new() { CardId = "OFFERING", Pile = "Hand" });
        foreach (var potion in player.PotionSlots.ToArray()) potion?.Discard();
        InjectPotionForTest(player, "SHACKLING_POTION");
        InjectPotionForTest(player, "CURE_ALL");
        SetEnergy(player, 100); SetStars(player, 100);
        await CreatureCmd.SetCurrentHp(live.Enemies.Single(), 5);
        if (regen) await PowerCmd.Apply<RegenPower>(new ThrowingPlayerChoiceContext(), player.Creature,
            3, player.Creature, null);
        await InjectCardAsync(live, player, new() { CardId = "HAND_OF_GREED", Pile = "Hand" });
        var root = CombatRootSnapshot.Capture(live);
        if (root.CanCertifyRemainingHealing || root.UsesComponentHealingCertificate
            || !root.UsesPruningComponentHealingCertificate || root.InitialPruningHealingUpperBound != (regen ? 6 : 0))
            throw new InvalidOperationException("New fixture did not take the reviewed proof path.");
        var parent = root.ForkSimulator();
        string before = DescribeContinuationContractState(parent, root, player);
        string liveBefore = ContinuationStamp.CaptureLive(live).StateText;
        var table = new PrimaryIncumbentTable();
        var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), live, false, null) with
        {
            VerifyIncrementalSearch = true, FixedBudget = false, MaxDegreeOfParallelism = 2,
            DetailedDiagnostics = false, MeasurePhasePerformance = false, BudgetOverrideMilliseconds = null,
            StopAtAcceptableBattleHpLoss = false, UseBeamWidthPortfolio = true, UseNoveltyPortfolio = false,
            PotionPolicy = SolverPotionPolicy.Smart, PotionStrategy = new(SolverPotionPolicy.Smart, []),
            RelicTargets = [], GrowthBudgets = default,
            GrowthOpportunityTargets = GrowthOpportunityTargets.UnboundedForTesting("zero_credit_native_growth"),
            PrimaryIncumbents = table,
        };
        policy = policy with { Profile = policy.Profile with
            { BeamWidth = 32, MaxExpandedNodes = 500, SoftTimeBudgetMilliseconds = 15000 } };
        var names = SolverDisplayNames.Capture(live);
        var damage = BattleDamageTracker.Observe(live);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var checks = await Task.Run(() => CombatBeamSolver.AssertZeroCreditGrowthEdgesForTesting(
            root, names, damage, policy, deadline.Token), deadline.Token);
        var retainedChecks = await Task.Run(() => CombatBeamSolver.AssertRetainedPrimaryEdgesForTesting(
            root, names, damage, policy, deadline.Token), deadline.Token);
        if (DescribeContinuationContractState(parent, root, player) != before
            || ContinuationStamp.CaptureLive(live).StateText != liveBefore)
            throw new InvalidOperationException("Goal/cost proof changed frozen parent/live/RNG.");
        _completedChecks.Add(checks);
        _completedChecks.Add(retainedChecks);
    }
}

internal sealed partial class CombatBeamSolver
{
    internal static string AssertZeroCreditGrowthEdgesForTesting(CombatRootSnapshot root,
        SolverDisplayNames names, BattleDamageSnapshot damage, SearchPolicySnapshot policy, CancellationToken token)
    {
        using var isolation = SimulationNotificationIsolation.Enter();
        SearchNode Opening(CombatBeamSolver solver) => CreateOpeningSearchSeed(solver.Replay([]));
        SearchNode Card(CombatBeamSolver solver, SearchNode parent, string id)
            => solver.CostChildForTesting(parent,
                solver.PrepareCardActions(parent).First(a => a.Action.CardId == id).Action);
        SearchNode Hurt(CombatBeamSolver solver, SearchNode opening)
            => Card(solver, Card(solver, opening, "OFFERING"), "BLOODLETTING");
        int Kept(CombatBeamSolver solver, SearchNode node) => solver.ApplyPrimaryIncumbentBound([node]).Count;
        if (CanUseReviewedGrowthHpProof(root, policy with { GrowthOpportunityTargets = GrowthOpportunityTargets.Empty }))
            throw new InvalidOperationException("A no-growth root replaced its original HP proof consumer.");
        var table = policy.PrimaryIncumbents!;
        var publisher = new CombatBeamSolver(root, names, damage, policy, token,
            potionPolicyOverride: SolverPotionPolicy.Disabled, maximumPotionUses: 0);
        var opening = Opening(publisher);
        var grownWinner = Card(publisher, opening, "HAND_OF_GREED");
        if (!grownWinner.Snapshot.AllEnemiesDead || grownWinner.Snapshot.HasRisk
            || grownWinner.Snapshot.GrowthRewards.HandOfGreed != 1
            || grownWinner.Snapshot.StrategicHpCredit != 0)
            throw new InvalidOperationException("Actual complete zero-credit growth route was not produced.");
        publisher.TightenPrimarySearchIncumbentAtTurnLayer([grownWinner], 1);
        var grownBucket = ResourceIncumbentPolicy.CompletedBucket(grownWinner.Snapshot, 0);
        if (!table.TryGet(grownBucket, out var grownBound) || grownBucket.Growth.HandOfGreed != 1
            || table.TryGet(0, 0, out _))
            throw new InvalidOperationException("Complete growth winner was not published to its actual resource bucket.");
        var consumer = new CombatBeamSolver(root, names, damage, policy, token,
            potionPolicyOverride: SolverPotionPolicy.Disabled, maximumPotionUses: 0);
        var otherOpening = Opening(consumer);
        var hurt = Hurt(consumer, otherOpening);
        if (hurt.IsTerminal || hurt.Snapshot.CumulativePlayerHpLost != 9
            || hurt.Snapshot.GrowthRewards != default || Kept(consumer, hurt) != 0
            || Kept(consumer, otherOpening) != 1 || Kept(publisher, otherOpening) != 1)
            throw new InvalidOperationException("Cross-growth strict HP proof or equal-HP preservation failed.");
        Parallel.For(0, 16, _ =>
        {
            if (!table.TryGetStrictHpWitness(0, 0, 0, out var read) || read != grownBound)
                throw new InvalidOperationException("Parallel actual-growth witness read was inconsistent.");
        });
        foreach (var guarded in new[]
        {
            policy with { GrowthBudgets = new GrowthValues(HandOfGreed: 1) },
            policy with { RelicTargets = [new(RelicCounterId.MeatOnTheBone, 0, 1, 0, 2)] },
            policy with { TheftPolicy = SolverTheftPolicy.PreserveResources },
        })
        {
            var guardedMember = new CombatBeamSolver(root, names, damage, guarded, token,
                potionPolicyOverride: SolverPotionPolicy.Disabled, maximumPotionUses: 0);
            if (guardedMember.CanUseExpandedHpDominance || Kept(guardedMember, hurt) != 1)
                throw new InvalidOperationException("Credit/relic/theft objective was ignored by the new proof.");
        }
        var forced = policy with { PotionStrategy = new(SolverPotionPolicy.Smart,
            [new PotionSlotDirective(0, "SHACKLING_POTION", SolverPotionDirective.Force)]) };
        var forcedMember = new CombatBeamSolver(root, names, damage,
            forced with { PrimaryIncumbents = new() }, token);
        if (forcedMember.TightenPrimarySearchIncumbentAtTurnLayer([grownWinner], 1)
            || Kept(forcedMember, hurt) != 1)
            throw new InvalidOperationException("An unsatisfied forced-potion route published or pruned.");
        var damagedWinner = Card(publisher, hurt, "STRIKE_NECROBINDER");
        if (!damagedWinner.Snapshot.AllEnemiesDead || damagedWinner.Snapshot.CumulativePlayerHpLost != 9)
            throw new InvalidOperationException("Complete P0 potion audit baseline was not produced.");
        var baseline = new PotionFreePolicyBaseline(true, 9, damagedWinner.Snapshot.PlayerHp,
            damagedWinner.Snapshot.CombatEndedTurn);
        var potionTable = new PrimaryIncumbentTable();
        var potionPolicy = policy with { PrimaryIncumbents = potionTable };
        var potionMember = new CombatBeamSolver(root, names, damage, potionPolicy, token,
            potionPolicyOverride: SolverPotionPolicy.Smart, potionFreePolicyBaseline: baseline,
            minimumPotionUses: 1, maximumPotionUses: 1);
        var potionOpening = Opening(potionMember);
        var potionStarts = potionMember.PreparePotionActions(potionOpening)
            .Select(a => potionMember.CostChildForTesting(potionOpening, a.Action))
            .GroupBy(n => n.Action!.PotionSlot).Select(g => g.First())
            .OrderBy(n => n.Snapshot.ExplicitPotionStrategicCost).ToArray();
        if (potionStarts.Length != 2 || potionStarts[0].Snapshot.ExplicitPotionStrategicCost
                >= potionStarts[1].Snapshot.ExplicitPotionStrategicCost)
            throw new InvalidOperationException("Fixture needs two legal unequal-cost nonhealing potions.");
        var cheap = potionStarts[0]; var expensive = potionStarts[1];
        var expensiveWinner = Card(potionMember, expensive, "STRIKE_NECROBINDER");
        if (!expensiveWinner.Snapshot.AllEnemiesDead || expensiveWinner.Snapshot.ProjectedDeathSaveUseCount != 0)
            throw new InvalidOperationException("P1 exact-layer complete winner was not produced.");
        potionMember.TightenPrimarySearchIncumbentAtTurnLayer([expensiveWinner], 1);
        var expensiveBucket = ResourceIncumbentPolicy.CompletedBucket(expensiveWinner.Snapshot, 1);
        if (!potionTable.TryGet(expensiveBucket, out var potionWitness)
            || potionWitness.ExplicitPotionStrategicCost != expensive.Snapshot.ExplicitPotionStrategicCost)
            throw new InvalidOperationException("P1 complete winner did not retain actual paid cost.");
        var cheapHurt = Hurt(potionMember, cheap);
        var expensiveHurt = Hurt(potionMember, expensive);
        var sharedPotionMember = new CombatBeamSolver(root, names, damage, potionPolicy, token,
            potionPolicyOverride: SolverPotionPolicy.Smart, potionFreePolicyBaseline: baseline,
            minimumPotionUses: 1, maximumPotionUses: 1);
        if (cheapHurt.IsTerminal || expensiveHurt.IsTerminal
            || Kept(potionMember, cheapHurt) != 1 || Kept(sharedPotionMember, cheapHurt) != 1
            || Kept(potionMember, expensiveHurt) != 0 || Kept(sharedPotionMember, expensiveHurt) != 0
            || Kept(sharedPotionMember, expensive) != 1)
            throw new InvalidOperationException("Strict HP proof lost a cheaper/equal route or missed the paid-cost proof.");
        var missingCost = new PrimaryIncumbentTable();
        missingCost.Tighten(expensiveBucket, potionWitness with { ExplicitPotionStrategicCost = null });
        var unknownCostMember = new CombatBeamSolver(root, names, damage,
            potionPolicy with { PrimaryIncumbents = missingCost }, token,
            potionPolicyOverride: SolverPotionPolicy.Smart, potionFreePolicyBaseline: baseline,
            minimumPotionUses: 1, maximumPotionUses: 1);
        if (Kept(unknownCostMember, expensiveHurt) != 1)
            throw new InvalidOperationException("Lost cost metadata became a dominance proof.");
        return $"ZeroCreditGrowth:CompleteGrowthWinner={grownBucket.Growth.HandOfGreed}:CrossBucketStrictLossCut:" +
            "EqualHpKept:NoGrowthConsumerPreserved:16ParallelWitnessReaders:PositiveCreditRelicTheftRefused:UnsatisfiedForceRefused:" +
            $"CompleteP0AuditBaseline=9:CompleteP1Cost={potionWitness.ExplicitPotionStrategicCost}:" +
            $"CheaperCost={cheap.Snapshot.ExplicitPotionStrategicCost}:LocalAndSharedCheaperKept:" +
            "LocalAndSharedExpensiveCut:MissingCostKept:FullIncrementalReplay:ParentLiveRngIsolation";
    }
}
