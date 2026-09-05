using MegaCrit.Sts2.Core.Entities.Orbs;
using MegaCrit.Sts2.Core.Models;
using CombatSolver.Engine.InCombat.Mirrors.Orbs;
using CombatSolver.Engine.Common;

namespace CombatSolver.Engine.InCombat.Simulation;

/// <summary>
/// Mirrors <see cref="OrbQueue"/> without mutating real orb queue.
/// </summary>
internal sealed class SimOrbQueue
{
    // The simulator clones the real orb queue at the start of a prediction and mutates the clone
    // during simulation. Since the number of orbs is typically small, this is not likely to be a
    // performance concern.
    private readonly List<OrbModel> _orbs;
    private Action? _mutationObserver;

    public IReadOnlyList<OrbModel> Orbs => _orbs;

    public int Capacity { get; private set; }

    public SimOrbQueue(OrbQueue liveOrbQueue)
    {
        _orbs = [.. liveOrbQueue.Orbs.Select(PredictionUtils.CloneModelForSimulation)];
        Capacity = liveOrbQueue.Capacity;
    }

    private SimOrbQueue(int capacity, List<OrbModel> orbs)
    {
        _orbs = orbs;
        Capacity = capacity;
    }

    public void Clear()
    {
        _orbs.Clear();
        Capacity = 0;
        _mutationObserver?.Invoke();
    }

    public void AddCapacity(int capacity)
    {
        Capacity += capacity;
        _mutationObserver?.Invoke();
    }

    public void RemoveCapacity(int capacity)
    {
        Capacity = Math.Max(0, Capacity - capacity);
        while (Orbs.Count > Capacity)
        {
            Remove(_orbs.Last());
        }
        _mutationObserver?.Invoke();
    }

    public bool Remove(OrbModel orb)
    {
        bool removed = _orbs.Remove(orb);
        if (removed)
            _mutationObserver?.Invoke();
        return removed;
    }

    public bool TryEnqueue(OrbModel orb)
    {
        if (Capacity == 0)
        {
            return false;
        }

        orb.AssertMutable();
        if (Orbs.Count >= Capacity)
        {
            throw new InvalidOperationException("OrbQueue is full");
        }

        _orbs.Add(orb);
        _mutationObserver?.Invoke();
        return true;
    }

    public void Insert(int idx, OrbModel orb)
    {
        if (idx >= Capacity)
        {
            throw new InvalidOperationException("idx cannot be greater than capacity");
        }

        _orbs.Insert(idx, orb);
        _mutationObserver?.Invoke();
    }

    internal void SetMutationObserver(Action mutationObserver)
        => _mutationObserver = mutationObserver;

    /// <summary>
    /// Mirrors the prediction-relevant orb snapshot and trigger order of <see cref="OrbQueue.BeforeTurnEnd"/>.
    /// </summary>
    public bool BeforeTurnEnd(CombatPredictionSimulator simulator)
    {
        if (simulator.HasPendingChoice)
            return false;

        HashSet<uint> processedEnemyDeaths = [];
        foreach (var orb in Orbs.ToList())
        {
            OrbMirrors.InvokeBeforeTurnEndOrbTrigger(simulator, orb, processedEnemyDeaths);
            if (simulator.HasPendingChoice)
                return false;
        }
        return true;
    }

    internal SimOrbQueue Fork(PredictionForkContext context)
    {
        List<OrbModel> orbs = [];
        foreach (OrbModel orb in _orbs)
        {
            OrbModel forkedOrb = PredictionUtils.CloneModelForSimulation(orb);
            context.Register(orb, forkedOrb);
            orbs.Add(forkedOrb);
        }
        SimOrbQueue fork = new(Capacity, orbs);
        context.Register(this, fork);
        return fork;
    }
}
