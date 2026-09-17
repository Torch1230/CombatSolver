using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private int AccuracyProjectionPotential(SearchNode parent, SearchNode child)
    {
        Interlocked.Increment(ref _run.PowerFrontierEvaluations);
        int amount = PowerAmountGain<AccuracyPower>(parent, child);
        if (amount == 0)
            return 0;
        int targets = CurrentShivTargets(child);
        PowerTurnCardOption[] options = BuildCurrentHandOptions(child);
        int incomingDamage = CurrentIncomingDamage(child);
        return MarginalFrontierValue(
            PowerTurnFrontier.Build(child.Snapshot.Energy, incomingDamage, options),
            PowerTurnFrontier.Build(
                child.Snapshot.Energy,
                incomingDamage,
                options,
                damagePerShiv: SaturatingProduct(amount, targets)));
    }

    private int FanOfKnivesProjectionPotential(SearchNode parent, SearchNode child)
    {
        Interlocked.Increment(ref _run.PowerFrontierEvaluations);
        int targets = Math.Max(1, child.Snapshot.AliveEnemyCount);
        PowerTurnCardOption[] baselineOptions = BuildCurrentHandOptions(
            child,
            shivTargetsOverride: 1);
        PowerTurnCardOption[] poweredOptions = BuildCurrentHandOptions(
            child,
            shivTargetsOverride: targets);
        int incomingDamage = CurrentIncomingDamage(child);
        int aoeUplift = MarginalFrontierValue(
            PowerTurnFrontier.Build(child.Snapshot.Energy, incomingDamage, baselineOptions),
            PowerTurnFrontier.Build(child.Snapshot.Energy, incomingDamage, poweredOptions));

        int generatedShivs = Math.Max(
            0,
            CountHandShivs(child) - CountHandShivs(parent));
        int singleTargetGeneratedDamage = child.Snapshot.Simulator.State
            .GetPlayerCombatState(_player)
            .Hand.Cards
            .Where(card => card.Preview.Tags.Contains(CardTag.Shiv))
            .Select(card => card.Preview.DynamicVars.TryGetValue("Damage", out var damageVar)
                ? Math.Max(0, damageVar.IntValue)
                : 0)
            .OrderBy(damage => damage)
            .Take(generatedShivs)
            .Sum();
        return Math.Min(
            child.Snapshot.EnemyHp,
            SaturatingPowerCommitmentAdd(singleTargetGeneratedDamage, aoeUplift));
    }

    private int InfiniteBladesProjectionPotential(SearchNode parent, SearchNode child)
    {
        Interlocked.Increment(ref _run.PowerFrontierEvaluations);
        int amount = PowerAmountGain<InfiniteBladesPower>(parent, child);
        if (amount == 0)
            return 0;
        CombatPredictionSimulator simulator = child.Snapshot.Simulator;
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        int drawPerTurn = PersistentPowerSupport.GetModifiedHandDraw(
            combat,
            _player,
            MegaCrit.Sts2.Core.Combat.CombatManager.baseHandDrawCount);
        int futureTurns = Math.Max(
            0,
            EstimateRemainingTurns(child.Snapshot, Math.Max(1, drawPerTurn)) - 1);
        int shivDamage = CanonicalModels.Card<Shiv>().DynamicVars.TryGetValue(
            "Damage",
            out var damageVar)
            ? Math.Max(0, damageVar.IntValue)
            : 0;
        return Math.Min(
            child.Snapshot.EnemyHp,
            SaturatingProduct(futureTurns, amount, shivDamage, CurrentShivTargets(child)));
    }

    private int PhantomBladesProjectionPotential(SearchNode parent, SearchNode child)
    {
        Interlocked.Increment(ref _run.PowerFrontierEvaluations);
        int amount = PowerAmountGain<PhantomBladesPower>(parent, child);
        if (amount == 0)
            return 0;
        PowerTurnCardOption[] options = BuildCurrentHandOptions(child);
        int incomingDamage = CurrentIncomingDamage(child);
        int damageUplift = MarginalFrontierValue(
            PowerTurnFrontier.Build(child.Snapshot.Energy, incomingDamage, options),
            PowerTurnFrontier.Build(
                child.Snapshot.Energy,
                incomingDamage,
                options,
                firstShivDamageBonus: SaturatingProduct(amount, CurrentShivTargets(child))));
        return SaturatingPowerCommitmentAdd(
            damageUplift,
            PhantomBladesRetentionPotential(parent, child));
    }

    private int PhantomBladesRetentionPotential(SearchNode parent, SearchNode child)
    {
        CombatPredictionSimulator parentSimulator = parent.Snapshot.Simulator;
        CombatPredictionSimulator childSimulator = child.Snapshot.Simulator;
        SimPlayerCombatState parentState = parentSimulator.State.GetPlayerCombatState(_player);
        SimPlayerCombatState childState = childSimulator.State.GetPlayerCombatState(_player);
        HashSet<CardModel> previouslyRetained = parentState.Hand.Cards
            .Where(card => card.Preview.ShouldRetainThisTurn)
            .Select(card => card.Original)
            .ToHashSet();
        RetainedHandCardFact[] baselineRetained = childState.Hand.Cards
            .Where(card => previouslyRetained.Contains(card.Original))
            .Select(card => RetainedFact(childSimulator, childState, card))
            .ToArray();
        RetainedHandCardFact[] poweredRetained = childState.Hand.Cards
            .Where(card => card.Preview.ShouldRetainThisTurn)
            .Select(card => RetainedFact(childSimulator, childState, card))
            .ToArray();
        if (poweredRetained.Length == baselineRetained.Length)
            return 0;

        SimulatedCombatState combat = (SimulatedCombatState)childSimulator.State.CombatState;
        int drawCount = PersistentPowerSupport.GetModifiedHandDraw(
            combat,
            _player,
            MegaCrit.Sts2.Core.Combat.CombatManager.baseHandDrawCount);
        int[] nextDrawValues = childState.DrawPile.Cards
            .Take(drawCount)
            .Select(card => Math.Max(
                0,
                (int)Math.Round(CardChoiceSupport.CardValue(card.Preview))))
            .ToArray();
        int energy = PersistentPowerSupport.GetModifiedMaxEnergy(combat, _player);
        int handLimit = combat.GetMaxHandSize(_player);
        int baseline = RetainedHandTransition.Evaluate(
            energy,
            handLimit,
            drawCount,
            baselineRetained,
            nextDrawValues).NetValue;
        int powered = RetainedHandTransition.Evaluate(
            energy,
            handLimit,
            drawCount,
            poweredRetained,
            nextDrawValues).NetValue;
        return Math.Max(0, powered - baseline);
    }

    private static RetainedHandCardFact RetainedFact(
        CombatPredictionSimulator simulator,
        SimPlayerCombatState state,
        PredictedCard card)
        => new(
            Math.Max(0, (int)Math.Round(CardChoiceSupport.CardValue(card.Preview))),
            Math.Max(0, card.GetEnergyCostWithModifiers(simulator, state)));

    private int CurrentShivTargets(SearchNode child)
    {
        SimulatedCombatState combat =
            (SimulatedCombatState)child.Snapshot.Simulator.State.CombatState;
        return combat.GetAmount<FanOfKnivesPower>(_player.Creature) > 0
            ? Math.Max(1, child.Snapshot.AliveEnemyCount)
            : 1;
    }

    private int CountHandShivs(SearchNode node)
        => node.Snapshot.Simulator.State.GetPlayerCombatState(_player)
            .Hand.Cards.Count(card => card.Preview.Tags.Contains(CardTag.Shiv));
}
