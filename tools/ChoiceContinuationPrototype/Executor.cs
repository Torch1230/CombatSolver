// Experimental build only. build.py injects this into a disposable source checkout.
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace CombatSolver.Engine.Common
{
    internal sealed partial class PredictionStateStore
    {
        // Deliberately exclude every transaction-bearing state, even an empty one.
        // No exception from a production AssertForkable is reclassified as eligibility.
        internal string ChoicePrototypeStateNames => _states is null ? "empty" : string.Join(",", _states.Values.Select(s => s.GetType().Name));
        internal bool SupportsChoicePrototype => _states is null || _states.Values.All(state =>
            state.GetType().Assembly == typeof(PredictionStateStore).Assembly
            && state is not IPredictionForkBoundary);
    }

    internal sealed partial class PredictionTrace
    {
        internal TraceScope ResumeChoicePrototype(PredictionTraceFrame frame)
        {
            if (_depth != 0 || frame.Parent != null || frame.Invocation.Action != PredictionActionKind.CardPlay)
                throw new InvalidOperationException("Prototype requires an inactive trace and one root CardPlay frame.");
            _slots[0] = new Slot { Source = frame.Source, Invocation = frame.Invocation, Materialized = frame };
            _depth = 1;
            return new TraceScope(this, 1);
        }
    }
}

namespace CombatSolver
{
    internal sealed partial class SimulatedCombatState
    {
        // Only for disposing an unreachable failed prototype child. Never establishes a fork boundary.
        internal void AbandonChoicePrototype()
        {
            _activeActionChoices = null;
            _activeActionChoiceTiming = PlanChoiceTiming.Action;
            ClearPendingTurnStartChoice();
        }
        internal bool SupportsChoicePrototype => _activeActionChoices is { IsEmptyChoicePrototype: true }
            && _activeActionChoiceTiming == PlanChoiceTiming.Action
            && _cardExecutionScopeDepth == 1 && _activeCardExecutionDeaths != null
            && !_playerTurnEndRequested && _powerCardSources is not { Count: > 0 }
            && PendingKnowledgeDemonChoice is null && _pendingPowerAmountChanges is not { Count: > 0 }
            && _unsettlingLampTriggeringCards is not { Count: > 0 }
            && _unsettlingLampInternalPowerTypes is not { Count: > 0 }
            && PendingTurnStartChoice is { SourceId: "", Effect: PlanChoiceEffect.Discard,
                SourcePile: PileType.Hand, Timing: PlanChoiceTiming.Action, Spec: not null };
    }
}

namespace CombatSolver.Engine.InCombat.Simulation
{
    // The only program counter in this prototype is "own manual choice, before AfterCardPlayed".
    // The seed is private, never resumed in place, and released with the checkpoint. No closures/tasks.
    internal sealed class ChoicePrototypeCheckpoint : IDisposable
    {
        private readonly object _gate = new();
        private CombatPredictionSimulator? _seed;
        private ChoicePrototypeFrame? _frame;
        private readonly uint[] _deaths;
        internal ChoicePrototypeCheckpoint(CombatPredictionSimulator seed, ChoicePrototypeFrame frame, uint[] deaths)
        { _seed = seed; _frame = frame; _deaths = deaths; }

        internal (CombatPredictionSimulator Simulator, HashSet<uint> Deaths, bool Completed) Resume(
            IReadOnlyList<PlanCardChoice> choices, CancellationToken cancellationToken, Action? afterForkForTesting = null)
        {
            CombatPredictionSimulator child;
            ChoicePrototypeFrame frame;
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_seed is null, this);
                child = _seed.ForkChoicePrototype(_frame!, out frame);
            }
            HashSet<uint> deaths = new(_deaths);
            SimulatedCombatState combat = (SimulatedCombatState)child.State.CombatState;
            combat.BeginActionChoices(choices);
            bool returned = false;
            try
            {
                using (combat.BeginCardExecutionScope(deaths))
                {
                    afterForkForTesting?.Invoke();
                    cancellationToken.ThrowIfCancellationRequested();
                    bool completed = child.ResumeChoicePrototype(frame);
                    returned = true;
                    return (child, deaths, completed);
                }
            }
            finally
            {
                if (returned) combat.EndActionChoices();
                else combat.AbandonChoicePrototype();
            }
        }
        public void Dispose()
        {
            lock (_gate) { _seed = null; _frame = null; }
        }
    }

    internal sealed record ChoicePrototypeFrame(PredictedCard Card, Creature? Target, CardPlay Play,
        CardLocation Result, int OwnerBlockBefore, PredictionTraceFrame Trace, int HistoryStart);

    internal sealed partial class CombatPredictionSimulator
    {
        private static bool SupportsChoicePrototypeCard(CardModel card)
            => card is DaggerThrow or Acrobatics or Prepared;

        private bool _captureChoicePrototype;
        private bool _ownedByChoiceCheckpoint;
        private void GuardOrdinaryChoicePrototypeFork()
        {
            if (_ownedByChoiceCheckpoint)
                throw new InvalidOperationException("Suspended prototype seed requires its owning checkpoint fork.");
        }
        private ChoicePrototypeFrame? _capturedChoicePrototype;
        private int _choicePrototypeHistoryStart;
        internal string ChoicePrototypeRejection { get; private set; } = "own choice not reached";

        // Caller transfers an independent action fork, as normal replay does. On decline, discard
        // this attempt (untouched or at a legacy boundary) and fully replay from the retained parent.
        internal ChoicePrototypeCheckpoint? PauseChoicePrototype(PredictedCard card, Creature? target,
            ISet<uint> processedDeaths)
        {
            GuardOrdinaryChoicePrototypeFork();
            AssertForkable();
            if (!SupportsChoicePrototypeCard(card.Preview) || card.Preview.Enchantment != null || card.Preview.Affliction != null)
                return null;
            SimulatedCombatState combat = (SimulatedCombatState)State.CombatState;
            _choicePrototypeHistoryStart = History.PrepareChoicePrototype();
            _captureChoicePrototype = true;
            combat.BeginActionChoices((IReadOnlyList<PlanCardChoice>?)null);
            bool returned = false;
            try
            {
                using (combat.BeginCardExecutionScope(processedDeaths))
                    ManualPlay(card, target, out _);
                returned = true;
            }
            finally
            {
                _captureChoicePrototype = false;
                if (returned) combat.EndActionChoices();
                else combat.AbandonChoicePrototype();
            }
            ChoicePrototypeFrame? frame = _capturedChoicePrototype;
            _capturedChoicePrototype = null;
            if (frame is null) return null;
            // The request is reconstructed through the existing spec/cursor on each child.
            // Execution depth and death-set ownership leave the stack explicitly at this point.
            combat.ClearPendingTurnStartChoice();
            AssertForkable();
            _ownedByChoiceCheckpoint = true;
            return new ChoicePrototypeCheckpoint(this, frame, processedDeaths.ToArray());
        }

        private bool TryCaptureChoicePrototype(PredictedCard card, Creature? target, CardPlay play,
            CardLocation result, int ownerBlockBefore)
        {
            // Legacy discovery must not execute rejection diagnostics or materialize extra history.
            if (!_captureChoicePrototype) return false;
            if (_capturedChoicePrototype != null
                || !SupportsChoicePrototypeCard(card.Preview) || play.IsAutoPlay || play.PlayCount != 1 || play.PlayIndex != 0
                || card.Preview.Enchantment != null || card.Preview.Affliction != null
                || CurrentFrame is not { Parent: null } trace
                || _damageSource != null || _activeDrawDepth != 0 || ActionRelicTriggers != null
                || _blockGainedByCardPlay.Count != 0 || !StateStore.SupportsChoicePrototype
                || State.CombatState is not SimulatedCombatState { SupportsChoicePrototype: true }
                || !History.SupportsChoicePrototype(_choicePrototypeHistoryStart, trace, play))
            {
                ChoicePrototypeRejection = $"capture={_captureChoicePrototype},play={play.PlayIndex}/{play.PlayCount},"
                    + $"traceRoot={CurrentFrame?.Parent is null},damage={_damageSource},draw={_activeDrawDepth},"
                    + $"block={_blockGainedByCardPlay.Count},store={StateStore.SupportsChoicePrototype}:{StateStore.ChoicePrototypeStateNames},"
                    + $"combat={((SimulatedCombatState)State.CombatState).SupportsChoicePrototype},"
                    + $"history={History.SupportsChoicePrototype(_choicePrototypeHistoryStart, CurrentFrame!, play)}:"
                    + string.Join(";", History.Select(e => e.GetType().Name + ":" + string.Join(",", e.Trace?.Ancestors().Select(f => f.Source.Id.Entry) ?? [])));
                return false;
            }
            _capturedChoicePrototype = new(card, target, play, result, ownerBlockBefore,
                trace, _choicePrototypeHistoryStart);
            return true;
        }

        internal CombatPredictionSimulator ForkChoicePrototype(ChoicePrototypeFrame source, out ChoicePrototypeFrame frame)
        {
            if (!_ownedByChoiceCheckpoint)
                throw new InvalidOperationException("Prototype fork requires an owned suspended seed.");
            AssertForkable(); // Normal action/choice/death/power/draw assertions remain intact.
            using PredictionForkContext context = new();
            PredictionTrace trace = new();
            CombatPredictionState state = State.Fork(context);
            PredictedCard card = context.RequireRemap(source.Card);
            CardPlay play = new()
            {
                Card = card.MutablePreview, Player = source.Play.Player, Target = source.Play.Target,
                ResultPile = source.Play.ResultPile, Resources = source.Play.Resources,
                IsAutoPlay = source.Play.IsAutoPlay, PlayIndex = source.Play.PlayIndex, PlayCount = source.Play.PlayCount,
            };
            context.Register(source.Play, play);
            PredictionTraceFrame action = new() { Source = source.Trace.Source,
                Invocation = source.Trace.Invocation, Parent = null };
            context.Register(source.Trace, action);
            frame = source with { Card = card, Play = play, Trace = action };
            PredictionStateStore store = StateStore.Fork(context);
            CombatPredictionHistory history = History.ForkChoicePrototype(trace, context, source.HistoryStart);
            return new CombatPredictionSimulator(trace, state, Rng.Fork(), store, history,
                IsInProgress, IsAboutToLose, TerminalStamp, ShuffleEventCount, null);
        }

        internal bool ResumeChoicePrototype(ChoicePrototypeFrame frame)
        {
            using (_trace.ResumeChoicePrototype(frame.Trace))
            {
                if (!((ICombatPredictionManualCardChoiceSink)State.CombatState).ResolveManualCardChoice(this, frame.Card))
                    return false;
                if (CompleteChoicePlayTail(frame.Card, frame.Target, frame.Play, frame.OwnerBlockBefore))
                    CompleteChoiceResultTail(frame.Card, frame.Play.Player, frame.Result);
            }
            if (History.HasCardPlayStartedSince(frame.HistoryStart, frame.Trace) && !HasPendingChoice)
                ((ICombatPredictionCardExecutionSink)State.CombatState).CompleteCardExecution(this);
            return !HasPendingChoice;
        }
    }

    internal sealed partial class CombatPredictionHistory
    {
        internal int PrepareChoicePrototype() { AssertForkable(); SealTail(); return EntryCount; }
        internal bool SupportsChoicePrototype(int start, PredictionTraceFrame action, CardPlay play)
        {
            if (_pendingDeferredEntries != 0 || (_prefix?.Count ?? 0) != start || _tail is null)
                return false;
            int starts = 0;
            foreach (var entry in _tail)
            {
                if (entry is CombatPredictionCardPlayStartedEntry started)
                {
                    if (!ReferenceEquals(started.CardPlay, play) || !ReferenceEquals(entry.Trace, action)) return false;
                    starts++;
                }
                else if (entry is not (CombatPredictionRiskEntry or CombatPredictionDamageReceivedEntry or CombatPredictionCreatureAttackedEntry
                    or CombatPredictionCardDrawnEntry or CombatPredictionCardDrawResolvedEntry))
                    return false;
                if (entry.Trace is null) return false;
                bool foundRoot = false;
                foreach (var trace in entry.Trace.Ancestors())
                {
                    // This narrow prefix permits only the played card's stable Original as a source.
                    if (!ReferenceEquals(trace.Source, action.Source)) return false;
                    if (ReferenceEquals(trace, action)) foundRoot = true;
                }
                if (!foundRoot) return false;
            }
            return starts == 1;
        }

        internal CombatPredictionHistory ForkChoicePrototype(PredictionTrace trace, PredictionForkContext context, int start)
        {
            AssertForkable();
            if ((_prefix?.Count ?? 0) != start || _tail is null)
                throw new InvalidOperationException("Prototype active history suffix changed.");
            var fork = new CombatPredictionHistory(trace, _prefix, _riskSignatureFirst, _riskSignatureSecond,
                _riskEntryCount, _cardDrawnEntryCount, _orbChanneledEntryCount, _tailCapacityHint);
            fork._tail = new(_tail.Count + 4);
            PredictionTraceFrame RemapTrace(PredictionTraceFrame source)
            {
                if (context.TryRemap(source, out PredictionTraceFrame? mapped)) return mapped!;
                var result = new PredictionTraceFrame { Source = source.Source, Invocation = source.Invocation,
                    Parent = source.Parent is null ? null : RemapTrace(source.Parent) };
                context.Register(source, result);
                return result;
            }
            DamageResult CopyDamage(DamageResult source)
            {
                if (context.TryRemap(source, out DamageResult? found)) return found!;
                var copy = new DamageResult(source.Receiver, source.Props)
                {
                    BlockedDamage = source.BlockedDamage, UnblockedDamage = source.UnblockedDamage,
                    OverkillDamage = source.OverkillDamage, WasBlockBroken = source.WasBlockBroken,
                    WasFullyBlocked = source.WasFullyBlocked, WasTargetKilled = source.WasTargetKilled,
                };
                context.Register(source, copy);
                return copy;
            }
            foreach (var entry in _tail)
            {
                CombatPredictionHistoryEntry copy = entry switch
                {
                    CombatPredictionRiskEntry e => new CombatPredictionRiskEntry { Reason = e.Reason },
                    CombatPredictionCardPlayStartedEntry e => new CombatPredictionCardPlayStartedEntry
                        { Card = e.Card, CardPlay = context.RequireRemap(e.CardPlay) },
                    CombatPredictionDamageReceivedEntry e => new CombatPredictionDamageReceivedEntry
                        { Receiver = e.Receiver, Dealer = e.Dealer, Result = CopyDamage(e.Result), CardSource = e.CardSource, Source = e.Source },
                    CombatPredictionCreatureAttackedEntry e => new CombatPredictionCreatureAttackedEntry
                        { Attacker = e.Attacker, HitResults = e.HitResults.Select(CopyDamage).ToArray() },
                    CombatPredictionCardDrawnEntry e => new CombatPredictionCardDrawnEntry
                        { Card = e.Card, FromHandDraw = e.FromHandDraw },
                    CombatPredictionCardDrawResolvedEntry e => new CombatPredictionCardDrawResolvedEntry
                        { Card = e.Card, OriginalEntry = context.RequireRemap(e.OriginalEntry) },
                    _ => throw new InvalidOperationException("Unqualified prototype history entry: " + entry.GetType().Name),
                };
                copy.Index = entry.Index;
                copy.Trace = RemapTrace(entry.Trace!);
                context.Register(entry, copy);
                fork._tail.Add(copy);
            }
            if (_tailCompletions != null)
            {
                fork._tailCompletions = new(ReferenceEqualityComparer.Instance);
                foreach (var (original, resolved) in _tailCompletions)
                    fork._tailCompletions.Add(context.RequireRemap(original), context.RequireRemap(resolved));
            }
            return fork;
        }
    }
}
