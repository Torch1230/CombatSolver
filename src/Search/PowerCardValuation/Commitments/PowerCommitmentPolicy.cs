using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private void AttachPowerCommitment(SearchNode child)
    {
        SearchNode? parent = child.Parent;
        if (parent == null || child.Snapshot.PlayerDead || child.IsTerminal)
        {
            if (parent?.PowerCommitment != null)
                Interlocked.Increment(ref _run.PowerCommitmentsExpired);
            child.PowerCommitment = null;
            return;
        }

        PowerCommitment? commitment = parent.PowerCommitment;
        PowerCommitmentFamily playedFamily = PowerCommitmentFamily.None;
        PlanAction? action = child.Action;
        bool playedRegisteredPower = action?.Kind == PlanActionKind.PlayCard
            && PowerCardValuationModels.Registry.TryGetCommitmentFamily(
                action.CardId,
                out playedFamily);
        int setupGain = SetupGain(parent.Snapshot, child.Snapshot);
        int evidenceGain = EvidenceGain(parent.Snapshot, child.Snapshot);

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
            int potential = Math.Max(1, SaturatingAdd(setupGain, projectedPotential));
            if (commitment == null)
            {
                Interlocked.Increment(ref _run.PowerCommitmentsCreated);
                child.PowerCommitment = new(
                    playedFamily,
                    child.Turn,
                    child.ActionCount,
                    0,
                    child.Turn,
                    investment,
                    potential,
                    0,
                    1);
                return;
            }

            child.PowerCommitment = commitment with
            {
                Family = commitment.Family | playedFamily,
                Investment = SaturatingAdd(commitment.Investment, investment),
                ProvisionalPotential = SaturatingAdd(commitment.ProvisionalPotential, potential),
                RealizedEvidence = SaturatingAdd(commitment.RealizedEvidence, evidenceGain),
                LastEvidenceTurn = evidenceGain > 0 ? child.Turn : commitment.LastEvidenceTurn,
                PowerCardsPlayed = commitment.PowerCardsPlayed + 1,
            };
            return;
        }

        if (commitment == null)
            return;
        int roundTransitions = commitment.RoundTransitions
            + (child.Turn > parent.Turn ? child.Turn - parent.Turn : 0);
        int maximumTransitions = _profile.AggressivePowerCommitment ? 3 : 2;
        if (roundTransitions > maximumTransitions
            || roundTransitions > 0 && child.Turn - commitment.LastEvidenceTurn > 1)
        {
            Interlocked.Increment(ref _run.PowerCommitmentsExpired);
            child.PowerCommitment = null;
            return;
        }

        int remainingPotential = Math.Max(
            0,
            commitment.ProvisionalPotential - evidenceGain);
        if (remainingPotential == 0 && evidenceGain > 0)
        {
            Interlocked.Increment(ref _run.PowerCommitmentsRealized);
            child.PowerCommitment = null;
            return;
        }

        child.PowerCommitment = commitment with
        {
            RoundTransitions = roundTransitions,
            LastEvidenceTurn = evidenceGain > 0 ? child.Turn : commitment.LastEvidenceTurn,
            RealizedEvidence = SaturatingAdd(commitment.RealizedEvidence, evidenceGain),
            ProvisionalPotential = remainingPotential,
        };
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

    private static int EvidenceGain(SimulationSnapshot before, SimulationSnapshot after)
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
            childCombat.GetAmount<MegaCrit.Sts2.Core.Models.Powers.DexterityPower>(_player.Creature)
                - parentCombat.GetAmount<MegaCrit.Sts2.Core.Models.Powers.DexterityPower>(_player.Creature));
        if (dexterityGain == 0)
            return 0;

        SimPlayerCombatState playerState = childSimulator.State.GetPlayerCombatState(_player);
        PowerTurnCardOption[] options = playerState.Hand.Cards
            .Where(card => !card.HasKeyword(
                childSimulator.State,
                MegaCrit.Sts2.Core.Entities.Cards.CardKeyword.Unplayable))
            .Select(card =>
            {
                int energyCost = Math.Max(
                    0,
                    card.GetEnergyCostWithModifiers(childSimulator, playerState));
                int damage = card.Preview.Type == MegaCrit.Sts2.Core.Entities.Cards.CardType.Attack
                    && card.Preview.DynamicVars.TryGetValue("Damage", out var damageVar)
                        ? Math.Max(0, damageVar.IntValue)
                        : 0;
                int block = card.Preview.Type == MegaCrit.Sts2.Core.Entities.Cards.CardType.Skill
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

    private static int SaturatingAdd(int left, int right)
        => (int)Math.Clamp((long)left + right, 0L, int.MaxValue);
}
