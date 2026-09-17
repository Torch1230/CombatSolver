using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private void AttachPowerCommitment(SearchNode child)
    {
        SearchNode? parent = child.Parent;
        if (parent == null)
        {
            child.PowerCommitment = null;
            return;
        }

        PowerCommitment? commitment = parent.PowerCommitment;
        if (child.Snapshot.PlayerDead || child.IsTerminal)
        {
            if (commitment != null)
                Interlocked.Increment(ref _run.PowerCommitmentsExpired);
            child.PowerCommitment = null;
            return;
        }

        PowerCommitmentDescriptor descriptor = default;
        PlanAction? action = child.Action;
        bool playedRegisteredPower = action?.Kind == PlanActionKind.PlayCard
            && PowerCardValuationModels.Registry.TryGetCommitmentDescriptor(
                action.CardId,
                out descriptor);
        int setupGain = SetupGain(parent.Snapshot, child.Snapshot);
        int progressEvidence = commitment == null
            ? 0
            : ProgressEvidenceGain(commitment, parent, child);
        int realizedEvidence = commitment == null
            ? 0
            : RealizedEvidenceGain(commitment, parent, child);

        if (playedRegisteredPower)
        {
            int investment = Math.Max(0, parent.Snapshot.Energy - child.Snapshot.Energy) * 8;
            investment = SaturatingAdd(
                investment,
                Math.Max(0, child.Snapshot.CumulativePlayerHpLost
                    - parent.Snapshot.CumulativePlayerHpLost));
            int projectedPotential = OpeningProjectionPotential(
                action!.CardId,
                parent,
                child);
            if (descriptor.Card == SilentPowerCardIdentity.MasterPlanner
                && projectedPotential == 0)
            {
                AdvanceExistingCommitment(
                    child,
                    parent,
                    commitment,
                    progressEvidence,
                    realizedEvidence);
                return;
            }

            int potential = Math.Max(1, SaturatingAdd(setupGain, projectedPotential));
            if (commitment == null)
            {
                Interlocked.Increment(ref _run.PowerCommitmentsCreated);
                child.PowerCommitment = PowerCommitmentLifecycle.Create(
                    descriptor,
                    child.Turn,
                    child.ActionCount,
                    child.Snapshot.HistoryEntryCount,
                    investment,
                    potential);
                return;
            }

            child.PowerCommitment = PowerCommitmentLifecycle.AddPower(
                commitment,
                descriptor,
                investment,
                potential,
                progressEvidence,
                realizedEvidence,
                child.Turn);
            return;
        }

        AdvanceExistingCommitment(
            child,
            parent,
            commitment,
            progressEvidence,
            realizedEvidence);
    }

    private void AdvanceExistingCommitment(
        SearchNode child,
        SearchNode parent,
        PowerCommitment? commitment,
        int progressEvidence,
        int realizedEvidence)
    {
        if (commitment == null)
            return;
        PowerCommitmentAdvanceResult advance = PowerCommitmentLifecycle.Advance(
            commitment,
            parent.Turn,
            child.Turn,
            _profile.AggressivePowerCommitment ? 3 : 2,
            progressEvidence,
            realizedEvidence,
            terminal: false);
        child.PowerCommitment = advance.Commitment;
        switch (advance.Disposition)
        {
            case PowerCommitmentDisposition.Active:
                return;
            case PowerCommitmentDisposition.Expired:
                Interlocked.Increment(ref _run.PowerCommitmentsExpired);
                return;
            case PowerCommitmentDisposition.Realized:
                Interlocked.Increment(ref _run.PowerCommitmentsRealized);
                return;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private static int SetupGain(SimulationSnapshot before, SimulationSnapshot after)
    {
        long gain = Math.Max(0, after.PersistentBuffValue - before.PersistentBuffValue);
        gain += Math.Max(0, after.StrategicEffects.RetentionValue
            - before.StrategicEffects.RetentionValue);
        gain += Math.Max(0, after.FutureResourceValue - before.FutureResourceValue);
        gain += Math.Max(0, after.LatentSetupValue - before.LatentSetupValue);
        return (int)Math.Min(int.MaxValue, gain);
    }

    private int ProgressEvidenceGain(
        PowerCommitment commitment,
        SearchNode parent,
        SearchNode child)
    {
        if (!commitment.Cards.HasFlag(SilentPowerCardIdentity.MasterPlanner))
            return 0;
        return Math.Max(
            0,
            SlySkillValue(child.Snapshot.Simulator)
                - SlySkillValue(parent.Snapshot.Simulator));
    }

    private int RealizedEvidenceGain(
        PowerCommitment commitment,
        SearchNode parent,
        SearchNode child)
    {
        long gain = 0;
        bool hasSpecializedEvidence = false;
        if (commitment.Cards.HasFlag(SilentPowerCardIdentity.Footwork))
        {
            hasSpecializedEvidence = true;
            if (child.Action is { Kind: PlanActionKind.PlayCard } action
                && IsBlockSkill(child.Snapshot.Simulator, action.CardId))
            {
                gain += Math.Max(1, child.Snapshot.PlayerBlock - parent.Snapshot.PlayerBlock);
                gain += Math.Max(
                    0,
                    child.Snapshot.ProjectedPlayerHp - parent.Snapshot.ProjectedPlayerHp);
            }
        }
        if (commitment.Cards.HasFlag(SilentPowerCardIdentity.WellLaidPlans))
        {
            hasSpecializedEvidence = true;
            if (child.Turn > parent.Turn)
                gain += Math.Max(1, child.Snapshot.ReachableHandValue);
        }
        if (commitment.Cards.HasFlag(SilentPowerCardIdentity.MasterPlanner))
        {
            hasSpecializedEvidence = true;
            gain += SlyAutoPlayValue(
                child.Snapshot.Simulator,
                parent.Snapshot.HistoryEntryCount);
        }
        if (!hasSpecializedEvidence)
            gain += GenericEvidenceGain(parent.Snapshot, child.Snapshot);
        return (int)Math.Min(int.MaxValue, gain);
    }

    private static int GenericEvidenceGain(SimulationSnapshot before, SimulationSnapshot after)
    {
        long gain = Math.Max(0, after.OffensiveProgressValue - before.OffensiveProgressValue);
        gain += Math.Max(0, after.ReachableHandValue - before.ReachableHandValue);
        gain += Math.Max(0, after.ZeroCostPlayableCount - before.ZeroCostPlayableCount) * 4L;
        gain += Math.Max(0, after.ProjectedPlayerHp - before.ProjectedPlayerHp);
        return (int)Math.Min(int.MaxValue, gain);
    }

    private int OpeningProjectionPotential(
        string cardId,
        SearchNode parent,
        SearchNode child)
        => cardId switch
        {
            "FOOTWORK" => FootworkProjectionPotential(parent, child),
            "MASTER_PLANNER" => MasterPlannerProjectionPotential(child),
            "WELL_LAID_PLANS" => WellLaidPlansProjectionPotential(child),
            _ => 0,
        };

    private int FootworkProjectionPotential(SearchNode parent, SearchNode child)
    {
        Interlocked.Increment(ref _run.PowerFrontierEvaluations);
        CombatPredictionSimulator parentSimulator = parent.Snapshot.Simulator;
        CombatPredictionSimulator childSimulator = child.Snapshot.Simulator;
        SimulatedCombatState parentCombat =
            (SimulatedCombatState)parentSimulator.State.CombatState;
        SimulatedCombatState childCombat =
            (SimulatedCombatState)childSimulator.State.CombatState;
        int dexterityGain = Math.Max(
            0,
            childCombat.GetAmount<DexterityPower>(_player.Creature)
                - parentCombat.GetAmount<DexterityPower>(_player.Creature));
        if (dexterityGain == 0)
            return 0;

        SimPlayerCombatState playerState = childSimulator.State.GetPlayerCombatState(_player);
        PowerTurnCardOption[] options = playerState.Hand.Cards
            .Where(card => !card.HasKeyword(childSimulator.State, CardKeyword.Unplayable))
            .Select(card =>
            {
                int energyCost = Math.Max(
                    0,
                    card.GetEnergyCostWithModifiers(childSimulator, playerState));
                int damage = card.Preview.Type == CardType.Attack
                    && card.Preview.DynamicVars.TryGetValue("Damage", out var damageVar)
                        ? Math.Max(0, damageVar.IntValue)
                        : 0;
                int block = card.Preview.Type == CardType.Skill
                    && card.Preview.DynamicVars.TryGetValue("Block", out var blockVar)
                        ? Math.Max(0, blockVar.IntValue)
                        : 0;
                return new PowerTurnCardOption(energyCost, damage, block);
            })
            .Where(option => option.Damage > 0 || option.Block > 0)
            .ToArray();
        if (options.Length == 0)
            return 0;
        int incomingDamage = Math.Max(
            0,
            child.Snapshot.PlayerHp - child.Snapshot.ProjectedPlayerHp);
        IReadOnlyList<PowerTurnFrontierState> baseline = PowerTurnFrontier.Build(
            child.Snapshot.Energy,
            incomingDamage,
            options);
        IReadOnlyList<PowerTurnFrontierState> powered = PowerTurnFrontier.Build(
            child.Snapshot.Energy,
            incomingDamage,
            options,
            dexterityGain);
        return SaturatingAdd(
            PowerTurnFrontier.DefensiveDamageUplift(baseline, powered),
            PowerTurnFrontier.DefensiveHpUplift(baseline, powered) * 8);
    }

    private int MasterPlannerProjectionPotential(SearchNode child)
    {
        Interlocked.Increment(ref _run.PowerFrontierEvaluations);
        CombatPredictionSimulator simulator = child.Snapshot.Simulator;
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        SimPlayerCombatState state = simulator.State.GetPlayerCombatState(_player);
        int drawPerTurn = PersistentPowerSupport.GetModifiedHandDraw(
            combat,
            _player,
            MegaCrit.Sts2.Core.Combat.CombatManager.baseHandDrawCount);
        if (drawPerTurn == 0)
            return 0;
        int remainingTurns = EstimateRemainingTurns(child.Snapshot, drawPerTurn);
        int discardWindows = state.Hand.Cards
            .Concat(state.DrawPile.Cards)
            .Concat(state.DiscardPile.Cards)
            .Sum(card => SilentDiscardWindowFacts.Capacity(
                card.Preview.Id.Entry,
                card.Preview.DynamicVars.TryGetValue("Cards", out var cardsVar)
                    ? cardsVar.IntValue
                    : 0,
                state.Hand.Cards.Count));
        discardWindows = SaturatingAdd(
            discardWindows,
            combat.GetAmount<ToolsOfTheTradePower>(_player.Creature)
                * Math.Max(0, remainingTurns - 1));
        int futureEnergy = PersistentPowerSupport.GetModifiedMaxEnergy(combat, _player);
        int cycleTurns = Math.Max(
            1,
            (child.Snapshot.LiveDeckSize + drawPerTurn - 1) / drawPerTurn);
        int turnsUntilShuffle = 1
            + (state.DrawPile.Cards.Count + drawPerTurn - 1) / drawPerTurn;
        List<MasterPlannerSkillFact> skills = [];
        void AddSkill(PredictedCard card, int turnsUntilSeed, int turnsUntilPayoff)
        {
            if (card.Preview.Type != CardType.Skill
                || card.Preview.IsSlyThisTurn
                || card.HasKeyword(simulator.State, CardKeyword.Unplayable))
            {
                return;
            }
            int energyCost = turnsUntilSeed == 0
                ? Math.Max(0, card.GetEnergyCostWithModifiers(simulator, state))
                : card.Preview.EnergyCost.CostsX
                    ? futureEnergy
                    : Math.Max(
                        0,
                        (int)Math.Ceiling((double)card.Preview.EnergyCost
                            .GetWithModifiers(CostModifiers.Local)));
            skills.Add(new MasterPlannerSkillFact(
                Math.Max(1, (int)Math.Round(CardChoiceSupport.CardValue(card.Preview))),
                energyCost,
                turnsUntilSeed,
                turnsUntilPayoff));
        }
        foreach (PredictedCard card in state.Hand.Cards)
            AddSkill(card, 0, turnsUntilShuffle);
        for (int index = 0; index < state.DrawPile.Cards.Count; index++)
        {
            int seedTurn = 1 + index / drawPerTurn;
            AddSkill(state.DrawPile.Cards[index], seedTurn, seedTurn + cycleTurns);
        }
        for (int index = 0; index < state.DiscardPile.Cards.Count; index++)
        {
            int seedTurn = turnsUntilShuffle + index / drawPerTurn;
            AddSkill(state.DiscardPile.Cards[index], seedTurn, seedTurn + cycleTurns);
        }
        MasterPlannerProjectionResult projection = MasterPlannerProjection.Evaluate(
            child.Snapshot.Energy,
            futureEnergy,
            remainingTurns,
            discardWindows,
            skills.ToArray());
        return projection.CardAccessValue;
    }

    private static int EstimateRemainingTurns(
        SimulationSnapshot snapshot,
        int drawPerTurn)
    {
        int damagePerTurn = Math.Max(
            1,
            snapshot.RetainedAttackValue * Math.Max(1, drawPerTurn)
                / Math.Max(1, snapshot.LiveDeckSize));
        return Math.Clamp(
            (snapshot.EnemyHp + damagePerTurn - 1) / damagePerTurn,
            1,
            SolverWeights.SetupValueHorizonTurns);
    }

    private int WellLaidPlansProjectionPotential(SearchNode child)
    {
        Interlocked.Increment(ref _run.PowerFrontierEvaluations);
        CombatPredictionSimulator simulator = child.Snapshot.Simulator;
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(_player);
        RetainedHandCardFact[] retainedCards = playerState.Hand.Cards
            .Select(card => new RetainedHandCardFact(
                Math.Max(0, (int)Math.Round(CardChoiceSupport.CardValue(card.Preview))),
                Math.Max(0, card.GetEnergyCostWithModifiers(simulator, playerState))))
            .ToArray();
        int drawCount = PersistentPowerSupport.GetModifiedHandDraw(
            combat,
            _player,
            MegaCrit.Sts2.Core.Combat.CombatManager.baseHandDrawCount);
        int[] nextDrawValues = playerState.DrawPile.Cards
            .Take(drawCount)
            .Select(card => Math.Max(
                0,
                (int)Math.Round(CardChoiceSupport.CardValue(card.Preview))))
            .ToArray();
        RetainedHandTransitionResult transition = RetainedHandTransition.Evaluate(
            PersistentPowerSupport.GetModifiedMaxEnergy(combat, _player),
            combat.GetMaxHandSize(_player),
            drawCount,
            retainedCards,
            nextDrawValues);
        return Math.Max(0, transition.NetValue);
    }

    private int SlySkillValue(CombatPredictionSimulator simulator)
    {
        SimPlayerCombatState state = simulator.State.GetPlayerCombatState(_player);
        return state.Hand.Cards
            .Concat(state.DrawPile.Cards)
            .Concat(state.DiscardPile.Cards)
            .Where(card => card.Preview.Type == CardType.Skill
                && card.Preview.IsSlyThisTurn)
            .Sum(card => Math.Max(
                1,
                (int)Math.Round(CardChoiceSupport.CardValue(card.Preview))));
    }

    private static int SlyAutoPlayValue(
        CombatPredictionSimulator simulator,
        int historyStart)
    {
        int value = 0;
        foreach (CombatPredictionHistoryEntry entry in simulator.History.EntriesFrom(historyStart))
        {
            if (entry is not CombatPredictionCardPlayStartedEntry started
                || !started.CardPlay.IsAutoPlay
                || started.CardPlay.Card.Type != CardType.Skill
                || !started.CardPlay.Card.IsSlyThisTurn)
            {
                continue;
            }
            value = SaturatingAdd(
                value,
                Math.Max(1, (int)Math.Round(
                    CardChoiceSupport.CardValue(started.CardPlay.Card))));
        }
        return value;
    }

    private bool IsBlockSkill(CombatPredictionSimulator simulator, string cardId)
    {
        SimPlayerCombatState state = simulator.State.GetPlayerCombatState(_player);
        PredictedCard? card = state.Hand.Cards
            .Concat(state.DrawPile.Cards)
            .Concat(state.DiscardPile.Cards)
            .Concat(state.ExhaustPile.Cards)
            .FirstOrDefault(candidate => candidate.Preview.Id.Entry == cardId);
        return card?.Preview.Type == CardType.Skill
            && card.Preview.DynamicVars._vars.Keys.Any(key =>
                key.Contains("Block", StringComparison.OrdinalIgnoreCase));
    }

    private static int SaturatingAdd(int left, int right)
        => (int)Math.Clamp((long)left + right, 0L, int.MaxValue);
}
