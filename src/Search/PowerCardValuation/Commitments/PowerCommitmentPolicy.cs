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

        IReadOnlyList<PowerCardPlayOccurrence> playedPowers = RegisteredPowerPlays(parent, child);
        int setupGain = PowerCommitmentSetupGain(parent.Snapshot, child.Snapshot);
        int progressEvidence = commitment == null
            ? 0
            : PowerCommitmentProgressEvidence(commitment, parent, child);
        int realizedEvidence = commitment == null
            ? 0
            : PowerCommitmentRealizedEvidence(commitment, parent, child);

        bool attachedPower = false;
        bool appliedPriorEvidence = false;
        foreach (PowerCardPlayOccurrence playedPower in playedPowers)
        {
            int spentEnergy = playedPower.IsAutoPlay
                ? 0
                : Math.Max(0, parent.Snapshot.Energy - child.Snapshot.Energy);
            int investment = spentEnergy * 8;
            if (!playedPower.IsAutoPlay)
            {
                investment = SaturatingPowerCommitmentAdd(
                    investment,
                    Math.Max(0, child.Snapshot.CumulativePlayerHpLost
                        - parent.Snapshot.CumulativePlayerHpLost));
            }
            int projectedPotential = SilentPowerOpeningProjectionPotential(
                playedPower.CardId,
                parent,
                child);
            if (!TryBuildSilentPowerCommitmentPotential(
                    playedPower,
                    parent,
                    child,
                    spentEnergy,
                    setupGain,
                    projectedPotential,
                    investment,
                    out int potential))
                continue;

            if (commitment == null)
            {
                Interlocked.Increment(ref _run.PowerCommitmentsCreated);
                commitment = PowerCommitmentLifecycle.Create(
                    playedPower.Descriptor,
                    child.Turn,
                    child.ActionCount,
                    child.Snapshot.HistoryEntryCount,
                    investment,
                    potential);
            }
            else
            {
                commitment = PowerCommitmentLifecycle.AddPower(
                    commitment,
                    playedPower.Descriptor,
                    investment,
                    potential,
                    appliedPriorEvidence ? 0 : progressEvidence,
                    appliedPriorEvidence ? 0 : realizedEvidence,
                    child.Turn);
                appliedPriorEvidence = true;
            }
            setupGain = 0;
            attachedPower = true;
        }

        if (attachedPower)
        {
            child.PowerCommitment = commitment;
            return;
        }

        AdvanceExistingPowerCommitment(
            child,
            parent,
            commitment,
            progressEvidence,
            realizedEvidence);
    }

    private bool TryBuildSilentPowerCommitmentPotential(
        in PowerCardPlayOccurrence playedPower,
        SearchNode parent,
        SearchNode child,
        int spentEnergy,
        int setupGain,
        int projectedPotential,
        int investment,
        out int potential)
    {
        bool hasTriggerEvidence = SilentPowerHasTriggerEvidence(
            playedPower.Descriptor.Card,
            parent,
            child);
        SilentPowerRouteAdmissionResult result = SilentPowerRouteAdmission.Evaluate(new(
            playedPower.Descriptor.Card,
            playedPower.IsAutoPlay,
            spentEnergy,
            child.Snapshot.Energy,
            hasTriggerEvidence,
            Math.Max(
                0,
                child.Snapshot.ProjectedPlayerHp - parent.Snapshot.ProjectedPlayerHp),
            setupGain,
            projectedPotential,
            SilentPowerTriggerProjectionFloor(playedPower.Descriptor.Card, child),
            investment));
        potential = result.Potential;
        return result.Admitted;
    }

    private void AdvanceExistingPowerCommitment(
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

    private static int PowerCommitmentSetupGain(
        SimulationSnapshot before,
        SimulationSnapshot after)
    {
        long gain = Math.Max(0, after.PersistentBuffValue - before.PersistentBuffValue);
        gain += Math.Max(0, after.StrategicEffects.RetentionValue
            - before.StrategicEffects.RetentionValue);
        gain += Math.Max(0, after.FutureResourceValue - before.FutureResourceValue);
        gain += Math.Max(0, after.LatentSetupValue - before.LatentSetupValue);
        return (int)Math.Min(int.MaxValue, gain);
    }

    private static int SaturatingPowerCommitmentAdd(int left, int right)
        => (int)Math.Clamp((long)left + right, 0L, int.MaxValue);
}
