namespace CombatSolver;

internal readonly record struct PrimaryIncumbentBucket(
    int Stolen, int Potions, GrowthValues Growth = default, ulong RelicMask = 0,
    bool ForfeitsEncounterGold = false);

/// <summary>Witnessed victories shared only by searches with the same frozen root and policy.</summary>
internal sealed class PrimaryIncumbentTable
{
    internal static bool CanShareRoot(CombatRootSnapshot root)
        => root.CanCertifyRemainingHealing || root.UsesKnownNativeHealingPolicy;

    private readonly Dictionary<PrimaryIncumbentBucket, PrimarySearchIncumbent> _bounds = [];
    internal SolverResult? PotionFreeWitness { get; set; }

    internal bool TryGet(int stolen, int potions, out PrimarySearchIncumbent incumbent)
        => TryGet(new(stolen, potions), out incumbent);

    internal bool TryGet(PrimaryIncumbentBucket bucket, out PrimarySearchIncumbent incumbent)
    {
        lock (_bounds)
            return _bounds.TryGetValue(bucket, out incumbent);
    }

    internal bool Tighten(int stolen, int potions, PrimarySearchIncumbent candidate)
        => Tighten(new(stolen, potions), candidate);

    internal bool Tighten(PrimaryIncumbentBucket bucket, PrimarySearchIncumbent candidate)
    {
        lock (_bounds)
        {
            if (_bounds.TryGetValue(bucket, out var current)
                && (candidate.StrategicHpDeficit > current.StrategicHpDeficit
                    || candidate.StrategicHpDeficit == current.StrategicHpDeficit
                        && candidate.CombatEndedTurn >= current.CombatEndedTurn))
                return false;
            _bounds[bucket] = candidate;
            return true;
        }
    }
}
