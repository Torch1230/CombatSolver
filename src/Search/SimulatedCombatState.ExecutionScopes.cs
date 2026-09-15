using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CombatSolver;

internal sealed partial class SimulatedCombatState
{
    bool ICombatPredictionExecutionContinuationState.TryCaptureExecutionScopes(
        CombatPredictionSimulator simulator, out ICombatPredictionExecutionScopes? scopes)
    {
        scopes = null;
        if (_unsettlingLampTriggeringCards is { Count: > 0 }
            || _unsettlingLampInternalPowerTypes is { Count: > 0 }) return false;
        if (_cardExecutionScopeDepth == 0 && _powerCardSources is not { Count: > 0 }
            && _activeActionChoiceTiming == PlanChoiceTiming.Action) return true;
        List<PredictedCard?> sources = [];
        if (_powerCardSources != null)
        {
            foreach (CardModel? source in _powerCardSources)
            {
                if (source is null) { sources.Add(null); continue; }
                PredictedCard? card = simulator.State.FindCard(source);
                // Lamp's per-source transaction has its own lifetime across child effects.
                // Until that protocol is explicit, leave these actions on complete replay.
                if (card is null || RelicsOf(source.Owner).Any(relic => relic is UnsettlingLamp && !relic.IsMelted)) return false;
                sources.Add(card);
            }
        }
        if (_activeCardExecutionDeaths is not null && _activeCardExecutionDeaths is not (ForkableSet<uint> or HashSet<uint>)) return false;
        scopes = new ExecutionScopes(_cardExecutionScopeDepth, _activeCardExecutionDeaths, sources, _activeActionChoiceTiming);
        return true;
    }

    internal static ISet<uint> ForkExecutionDeaths(ISet<uint> source, PredictionForkContext context)
    {
        if (context.TryRemap(source, out ISet<uint>? found)) return found!;
        ISet<uint> result = source switch
        {
            ForkableSet<uint> forkable => forkable.Fork(),
            HashSet<uint> set => new HashSet<uint>(set, set.Comparer),
            _ => throw new InvalidOperationException("Execution continuation received an unqualified death set."),
        };
        context.Register(source, result);
        return result;
    }

    private sealed record ExecutionScopes(int CardDepth, ISet<uint>? Deaths, IReadOnlyList<PredictedCard?> PowerSources, PlanChoiceTiming Timing)
        : ICombatPredictionExecutionScopes
    {
        public void PrepareFork(PredictionForkContext context)
        {
            if (Deaths is not null) ForkExecutionDeaths(Deaths, context);
            foreach (PredictedCard? card in PowerSources)
                if (card != null && !context.TryRemap(card, out PredictedCard? _)) card.Fork(context);
        }
        public ICombatPredictionExecutionScopes Fork(PredictionForkContext context)
            => this with { Deaths = Deaths is null ? null : context.RequireRemap(Deaths),
                PowerSources = PowerSources.Select(card => card is null ? null : context.RequireRemap(card)).ToArray() };
        public IDisposable Enter(CombatPredictionSimulator simulator)
        {
            var combat = (SimulatedCombatState)simulator.State.CombatState;
            if (combat._cardExecutionScopeDepth != 0 || combat._activeCardExecutionDeaths != null
                || combat._powerCardSources is { Count: > 0 })
                throw new InvalidOperationException("Execution continuation scopes overlap an active transaction.");
            combat._cardExecutionScopeDepth = CardDepth;
            combat._activeCardExecutionDeaths = Deaths;
            combat._powerCardSources = PowerSources.Select(card => card?.Preview).ToList();
            PlanChoiceTiming previousTiming = combat._activeActionChoiceTiming;
            combat._activeActionChoiceTiming = Timing;
            return new RestoredExecutionScopes(combat, CardDepth, Deaths, combat._powerCardSources, previousTiming);
        }
    }

    private sealed class RestoredExecutionScopes(SimulatedCombatState owner, int cardDepth, ISet<uint>? deaths,
        List<CardModel?> sources, PlanChoiceTiming previousTiming) : IDisposable
    {
        private readonly int _sourceCount = sources.Count;
        public void Dispose()
        {
            if (owner._cardExecutionScopeDepth != cardDepth || !ReferenceEquals(owner._activeCardExecutionDeaths, deaths)
                || !ReferenceEquals(owner._powerCardSources, sources) || sources.Count != _sourceCount)
                throw new InvalidOperationException("Execution continuation scopes did not unwind to their saved depth.");
            sources.Clear();
            owner._cardExecutionScopeDepth = 0;
            owner._activeCardExecutionDeaths = null;
            owner._activeActionChoiceTiming = previousTiming;
        }
    }
}
