namespace CombatSolver;

internal readonly record struct PrimaryIncumbentBucket(
    int Stolen, int Potions, GrowthValues Growth = default, ulong RelicMask = 0);

/// <summary>Witnessed victories shared only by searches with the same frozen root and policy.</summary>
internal sealed class PrimaryIncumbentTable
{
    internal static bool CanShareRoot(CombatRootSnapshot root)
        => root.CanCertifyRemainingHealing || root.UsesKnownNativeHealingPolicy
            || root.UsesPruningComponentHealingCertificate;

    private readonly Dictionary<PrimaryIncumbentBucket, PrimarySearchIncumbent> _bounds = [];
    internal SolverResult? PotionFreeWitness { get; set; }

    internal bool TryGet(int stolen, int potions, out PrimarySearchIncumbent incumbent)
        => TryGet(new(stolen, potions), out incumbent);

    internal bool TryGet(PrimaryIncumbentBucket bucket, out PrimarySearchIncumbent incumbent)
    {
        lock (_bounds)
            return _bounds.TryGetValue(bucket, out incumbent);
    }

    // The caller proves zero growth credit and no relic/theft objective. Rewards
    // can still break HP ties, so this witness is consumed only for strictly worse
    // HP. Paid potion cost cannot improve on the selected complete route either.
    internal bool TryGetStrictHpWitness(int stolen, int potions, int paidPotionCost,
        out PrimarySearchIncumbent incumbent)
    {
        incumbent = default;
        bool found = false;
        lock (_bounds)
        {
            foreach (var entry in _bounds)
            {
                if (entry.Key.Stolen != stolen || entry.Key.Potions != potions || entry.Key.RelicMask != 0
                    || entry.Value.ExplicitPotionStrategicCost is not { } cost || cost > paidPotionCost)
                    continue;
                if (!found || entry.Value.StrategicHpDeficit < incumbent.StrategicHpDeficit)
                {
                    incumbent = entry.Value;
                    found = true;
                }
            }
        }
        return found;
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
