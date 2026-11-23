using System.Diagnostics;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Commands;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using CombatSolver;
using CombatSolver.Beam.Policy;

namespace CombatSolver.Beam;

/// <summary>Replays each selection branch from its parent; a suspended clone is discarded. Method: CardChoiceSupport.cs and CombatBeamSolver.CardChoiceContinuation.cs @ d231e9e5.</summary>
internal static class ChoiceExpansion
{
    private static readonly IReadOnlyList<IReadOnlyList<int>> NoChoices = [];
    private static readonly IReadOnlyList<string> NoChoiceIds = [];

    [ThreadStatic]
    private static List<string>? _observedCardIdScratch;

    [ThreadStatic]
    private static List<(int[] Indices, string[] CardIds)>? _selectionScratch;

    internal sealed record Expansion(
        IReadOnlyList<SearchNode> Children,
        int Attempts,
        bool BudgetTruncated,
        IReadOnlyDictionary<string, int> Rejections,
        bool HitPendingChoiceBoundary = false);

    public static Task<Expansion> ExpandAsync(SearchNode parent,
        PlannedAction action, BeamBudget budget, StateEvaluator evaluator,
        int remainingNodes, Stopwatch watch, CancellationToken ct)
    {
        int localRemaining = remainingNodes;
        return ExpandAsync(
            parent,
            action,
            budget,
            evaluator,
            tryReserveNode: () =>
            {
                if (localRemaining <= 0) return false;
                localRemaining--;
                return true;
            },
            watch,
            ct);
    }

    public static Task<Expansion> ExpandAsync(SearchNode parent,
        PlannedAction action, BeamBudget budget, StateEvaluator evaluator,
        Func<bool> tryReserveNode, Stopwatch watch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tryReserveNode);
        if (SynchronizationContext.Current is null)
            return Task.FromResult(ExpandSync(parent, action, budget, evaluator, tryReserveNode, watch, ct));

        return ExpandAsyncFallback(parent, action, budget, evaluator, tryReserveNode, watch, ct);
    }

    internal static Expansion ExpandSync(SearchNode parent,
        PlannedAction action, BeamBudget budget, StateEvaluator evaluator,
        Func<bool> tryReserveNode, Stopwatch watch, CancellationToken ct,
        Action? onAttempt = null, Action? onTransition = null)
    {
        ArgumentNullException.ThrowIfNull(tryReserveNode);
        var results = new List<SearchNode>();
        var rejections = new Dictionary<string, int>(StringComparer.Ordinal);
        int parentTurn = parent.Turn;
        int attempts = 0;
        bool truncated = false;
        bool hitPendingChoiceBoundary = false;
        List<string> observedScratch = _observedCardIdScratch ?? new List<string>(8);
        _observedCardIdScratch = null;
        try
        {
            Explore(script: null, choiceDepth: 0);
        }
        finally
        {
            observedScratch.Clear();
            _observedCardIdScratch = observedScratch;
        }
        return new Expansion(results, attempts, truncated, rejections, hitPendingChoiceBoundary);

        void Explore(ChoiceScriptNode? script, int choiceDepth)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                ExploreBranch(script, choiceDepth);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Keep the deepest scripted branch's context when recursive exploration fails.
                if (!error.Data.Contains("Beam.Action"))
                {
                    error.Data["Beam.Action"] = action;
                    error.Data["Beam.ParentFingerprint"] = parent.Fingerprint;
                    error.Data["Beam.ParentDepth"] = parent.Depth;
                    error.Data["Beam.ParentTurn"] = parentTurn;
                    error.Data["Beam.ChoiceScript"] = script?.MaterializeIndices() ?? NoChoices;
                }
                throw;
            }
        }

        void ExploreBranch(ChoiceScriptNode? script, int choiceDepth)
        {
            ct.ThrowIfCancellationRequested();
            if (results.Count >= budget.MaxCardBranches) return;
            if (choiceDepth > 12)
            {
                hitPendingChoiceBoundary = true;
                return;
            }
            if (watch.Elapsed >= budget.TimeLimit)
            {
                truncated = true;
                return;
            }
            if (!tryReserveNode())
            {
                truncated = true;
                return;
            }
            attempts++;
            onAttempt?.Invoke();
            CombatState child = parent.State.Clone();
            var ledger = new PredictionLedgerSink();
            child.PredictionSink = ledger;
            observedScratch.Clear();
            try
            {
                using (CardSelectCmd.IsolateWorkerScope())
                {
                    ActionExecutor.ExecuteSyncCore(child, action, script, observedScratch);
                }

                bool matches = script is null
                    ? observedScratch.Count == 0
                    : script.MatchesObserved(observedScratch);
                if (!matches)
                    throw new InvalidOperationException("Choice replay did not match its parent script.");

                PlannedAction resolved = MaterializeResolvedAction(action, script);
                if (evaluator.AllowsMaxHpLoss(parent.BrightestFlameMaxHpLost + ledger.BrightestFlameMaxHpLost))
                {
                    SearchNode childNode = SearchNode.Create(
                        child,
                        parent,
                        resolved,
                        evaluator,
                        ledger.BrightestFlameMaxHpLost,
                        evaluator.CaptureGrowth
                            ? GrowthLedger.Delta(parent.State, child, resolved, ledger)
                            : default,
                        ledger.DeathSaveHpRestored,
                        ledger.DeathSaveUseCount,
                        playedCards: ledger.PlayedCards,
                        hasImpureHistory: ledger.PotionsProcured > 0);
                    results.Add(childNode);
                    onTransition?.Invoke();
                }
            }
            catch (SolverSelectionRequiredException pending)
            {
                // fail-open: selection suspension is consumed by replaying each choice from the parent.
                // The exception may interrupt a Core action halfway through; only the untouched
                // parent can be used to explore the next scripted alternative.
                CardSelectionRequest request = pending.Request;
                int limit = IsHandChoice(request) ? budget.MaxHandChoiceBranches
                    : budget.MaxPileChoiceBranches;
                List<(int[] Indices, string[] CardIds)> selections = AcquireSelectionScratch();
                try
                {
                    ExtractSelectionsInto(request, limit, selections);
                    if (selections.Count == 0)
                    {
                        hitPendingChoiceBoundary = true;
                    }
                    else
                    {
                        int count = selections.Count;
                        for (int idx = 0; idx < count; idx++)
                        {
                            ct.ThrowIfCancellationRequested();
                            (int[] indices, string[] cardIds) = selections[idx];
                            var next = new ChoiceScriptNode(script, indices, cardIds, idx);
                            Explore(next, choiceDepth + 1);
                            if (results.Count >= budget.MaxCardBranches || truncated) break;
                        }
                    }
                }
                finally
                {
                    ReleaseSelectionScratch(selections);
                }
            }
            catch (CandidateRejectedException rejected)
            {
                // fail-open: an explicit action rejection drops only this candidate and records its reason.
                rejections[rejected.Reason] = rejections.GetValueOrDefault(rejected.Reason) + 1;
            }
        }
    }

    private static async Task<Expansion> ExpandAsyncFallback(SearchNode parent,
        PlannedAction action, BeamBudget budget, StateEvaluator evaluator,
        Func<bool> tryReserveNode, Stopwatch watch, CancellationToken ct,
        Action? onAttempt = null, Action? onTransition = null)
    {
        var results = new List<SearchNode>();
        var rejections = new Dictionary<string, int>(StringComparer.Ordinal);
        int parentTurn = parent.Turn;
        int attempts = 0;
        bool truncated = false;
        bool hitPendingChoiceBoundary = false;
        await ExploreAsync(null, 0).ConfigureAwait(false);
        return new Expansion(results, attempts, truncated, rejections, hitPendingChoiceBoundary);

        async Task ExploreAsync(ChoiceScriptNode? script, int choiceDepth)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await ExploreBranchAsync(script, choiceDepth).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                if (!error.Data.Contains("Beam.Action"))
                {
                    error.Data["Beam.Action"] = action;
                    error.Data["Beam.ParentFingerprint"] = parent.Fingerprint;
                    error.Data["Beam.ParentDepth"] = parent.Depth;
                    error.Data["Beam.ParentTurn"] = parentTurn;
                    error.Data["Beam.ChoiceScript"] = script?.MaterializeIndices() ?? NoChoices;
                }
                throw;
            }
        }

        async Task ExploreBranchAsync(ChoiceScriptNode? script, int choiceDepth)
        {
            ct.ThrowIfCancellationRequested();
            if (results.Count >= budget.MaxCardBranches) return;
            if (choiceDepth > 12)
            {
                hitPendingChoiceBoundary = true;
                return;
            }
            if (watch.Elapsed >= budget.TimeLimit)
            {
                truncated = true;
                return;
            }
            if (!tryReserveNode())
            {
                truncated = true;
                return;
            }
            attempts++;
            onAttempt?.Invoke();
            CombatState child = parent.State.Clone();
            var ledger = new PredictionLedgerSink();
            child.PredictionSink = ledger;
            try
            {
                IReadOnlyList<IReadOnlyList<int>> matScript = script?.MaterializeIndices() ?? NoChoices;
                IReadOnlyList<string> expectedIds = script?.MaterializeCardIds() ?? NoChoiceIds;
                IReadOnlyList<string> observed = await ActionExecutor.ExecuteAsync(child, action, matScript)
                    .ConfigureAwait(false);
                if (!observed.SequenceEqual(expectedIds, StringComparer.Ordinal))
                    throw new InvalidOperationException("Choice replay did not match its parent script.");
                PlannedAction resolved = MaterializeResolvedAction(action, script);
                if (evaluator.AllowsMaxHpLoss(parent.BrightestFlameMaxHpLost + ledger.BrightestFlameMaxHpLost))
                {
                    results.Add(SearchNode.Create(child, parent, resolved, evaluator,
                        ledger.BrightestFlameMaxHpLost,
                        evaluator.CaptureGrowth
                            ? GrowthLedger.Delta(parent.State, child, resolved, ledger)
                            : default,
                        ledger.DeathSaveHpRestored, ledger.DeathSaveUseCount,
                        playedCards: ledger.PlayedCards,
                        hasImpureHistory: ledger.PotionsProcured > 0));
                    onTransition?.Invoke();
                }
            }
            catch (SolverSelectionRequiredException pending)
            {
                // fail-open: selection suspension is consumed by replaying each choice from the parent.
                CardSelectionRequest request = pending.Request;
                int limit = IsHandChoice(request) ? budget.MaxHandChoiceBranches
                    : budget.MaxPileChoiceBranches;
                List<(int[] Indices, string[] CardIds)> selections = [];
                ExtractSelectionsInto(request, limit, selections);
                if (selections.Count == 0)
                {
                    hitPendingChoiceBoundary = true;
                    return;
                }
                for (int idx = 0; idx < selections.Count; idx++)
                {
                    ct.ThrowIfCancellationRequested();
                    (int[] indices, string[] cardIds) = selections[idx];
                    await ExploreAsync(new ChoiceScriptNode(script, indices, cardIds, idx), choiceDepth + 1)
                        .ConfigureAwait(false);
                    if (results.Count >= budget.MaxCardBranches || truncated) break;
                }
            }
            catch (CandidateRejectedException rejected)
            {
                // fail-open: an explicit action rejection drops only this candidate and records its reason.
                rejections[rejected.Reason] = rejections.GetValueOrDefault(rejected.Reason) + 1;
            }
        }
    }

    internal static PlannedAction MaterializeResolvedAction(PlannedAction baseAction, ChoiceScriptNode? script) =>
        script is null
            ? baseAction with { ChoiceIndices = NoChoices, ChoiceCardIds = NoChoiceIds }
            : baseAction with { ChoiceIndices = script.MaterializeIndices(), ChoiceCardIds = script.MaterializeCardIds() };

    internal static bool IsHandChoice(CardSelectionRequest request)
    {
        IReadOnlyList<CardModel> candidates = request.Candidates;
        if (candidates.Count == 0 || request.Player.PlayerCombatState is not { } state)
            return false;
        IReadOnlyList<CardModel> handCards = state.Hand.Cards;
        for (int i = 0; i < candidates.Count; i++)
        {
            CardModel candidate = candidates[i];
            bool found = false;
            for (int h = 0; h < handCards.Count; h++)
            {
                if (ReferenceEquals(handCards[h], candidate))
                {
                    found = true;
                    break;
                }
            }
            if (!found)
                return false;
        }
        return true;
    }

    internal static List<(int[] Indices, string[] CardIds)> AcquireSelectionScratch()
    {
        List<(int[] Indices, string[] CardIds)>? list = _selectionScratch;
        _selectionScratch = null;
        return list ?? new List<(int[] Indices, string[] CardIds)>(16);
    }

    internal static void ReleaseSelectionScratch(List<(int[] Indices, string[] CardIds)> list)
    {
        list.Clear();
        _selectionScratch = list;
    }

    internal static void ExtractSelectionsInto(
        CardSelectionRequest request,
        int limit,
        List<(int[] Indices, string[] CardIds)> destination)
    {
        destination.Clear();
        if (limit <= 0)
            return;

        IReadOnlyList<CardModel> candidates = request.Candidates;
        int n = candidates.Count;
        int min = Math.Clamp(request.MinCount, 0, n);
        int max = Math.Clamp(request.MaxCount, min, n);
        int[] stack = new int[max];

        for (int size = min; size <= max && destination.Count < limit; size++)
        {
            if (size == 0)
            {
                destination.Add((Array.Empty<int>(), Array.Empty<string>()));
                continue;
            }

            EmitCombinations(candidates, n, size, start: 0, depth: 0, stack, limit, destination);
        }
    }

    private static void EmitCombinations(
        IReadOnlyList<CardModel> candidates,
        int candidateCount,
        int size,
        int start,
        int depth,
        int[] stack,
        int limit,
        List<(int[] Indices, string[] CardIds)> destination)
    {
        if (depth == size)
        {
            var indices = new int[size];
            var ids = new string[size];
            for (int i = 0; i < size; i++)
            {
                int idx = stack[i];
                indices[i] = idx;
                ids[i] = candidates[idx].Id.Entry;
            }
            destination.Add((indices, ids));
            return;
        }

        int remaining = size - depth;
        int maxStart = candidateCount - remaining;
        for (int index = start; index <= maxStart; index++)
        {
            stack[depth] = index;
            EmitCombinations(candidates, candidateCount, size, index + 1, depth + 1, stack, limit, destination);
            if (destination.Count >= limit)
                return;
        }
    }
}

internal sealed class CandidateRejectedException(string reason, string message)
    : InvalidOperationException(message)
{
    public string Reason { get; } = reason;
}

/// <summary>Strict Core action replay; method adapted from CombatBeamSolver.Expansion.cs @ d231e9e5.</summary>
internal static class ActionExecutor
{
    public static async Task<IReadOnlyList<string>> ExecuteAsync(CombatState state,
        PlannedAction action, IReadOnlyList<IReadOnlyList<int>> script)
    {
        var recordedIds = new List<string>(script.Count);
        var source = new ScriptedRecordingChoiceSource(script, null, recordedIds);
        state.CardSelectionSource = source;
        Player player = state.Players[0];
        if ((player.PlayerCombatState?.TurnNumber ?? 0) != action.Turn)
            throw new InvalidOperationException("Route turn does not match the Core state.");
        switch (action.Kind)
        {
            case "PlayCard":
            {
                CardModel card = FindHandCardByOccurrence(player.PlayerCombatState!.Hand.Cards, action.CardId, action.CardOccurrence)
                    ?? throw new CandidateRejectedException("card-unavailable", $"Route card {action.CardId} is unavailable.");
                if (!card.CanPlay(out _)) throw new CandidateRejectedException("card-unplayable", "Route card cannot be played.");
                if (!ActionGenerator.IsLegalTarget(state, player, card.TargetType, action.TargetIndex))
                    throw new CandidateRejectedException("card-target", "Route card target is illegal.");
                Creature? target = card.TargetType == TargetType.Self ? null
                    : ResolveTarget(state, player, card.TargetType, action.TargetIndex);
                if (await state.Engine!.PlayCardWithResultAsync(player, card, target).ConfigureAwait(false) is null)
                    throw new CandidateRejectedException("card-rejected", "Core rejected route card play.");
                break;
            }
            case "UsePotion":
            {
                if (action.PotionSlot < 0 || action.PotionSlot >= player.PotionSlots.Count
                    || player.PotionSlots[action.PotionSlot] is not { } potion
                    || potion.Id.Entry != action.PotionId)
                    throw new CandidateRejectedException("potion-unavailable", "Route potion slot is unavailable.");
                if (!ActionGenerator.IsLegalTarget(state, player, potion.TargetType, action.TargetIndex))
                    throw new CandidateRejectedException("potion-target", "Route potion target is illegal.");
                Creature? target = ResolveTarget(state, player, potion.TargetType, action.TargetIndex);
                if (!PotionCmd.CanUseManually(state, player, potion, target))
                    throw new CandidateRejectedException("potion-unusable", "Route potion is illegal.");
                await PotionCmd.Use(potion, player, target).ConfigureAwait(false);
                break;
            }
            case "EndTurn":
                await state.Engine!.EndPlayerTurnAsync().ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException($"Unknown route action {action.Kind}.");
        }
        state.Engine!.CheckWinCondition();
        if (source.ChoicesConsumed != script.Count)
            throw new InvalidOperationException("Route has unconsumed card selections.");
        return recordedIds;
    }

    internal static void ExecuteSyncCore(
        CombatState state,
        PlannedAction action,
        ChoiceScriptNode? script,
        List<string> recordedIds)
    {
        recordedIds.Clear();
        var source = new ScriptedRecordingChoiceSource(null, script, recordedIds);
        state.CardSelectionSource = source;
        Player player = state.Players[0];
        if ((player.PlayerCombatState?.TurnNumber ?? 0) != action.Turn)
            throw new InvalidOperationException("Route turn does not match the Core state.");
        switch (action.Kind)
        {
            case "PlayCard":
            {
                CardModel card = FindHandCardByOccurrence(player.PlayerCombatState!.Hand.Cards, action.CardId, action.CardOccurrence)
                    ?? throw new CandidateRejectedException("card-unavailable", $"Route card {action.CardId} is unavailable.");
                if (!card.CanPlay(out _)) throw new CandidateRejectedException("card-unplayable", "Route card cannot be played.");
                if (!ActionGenerator.IsLegalTarget(state, player, card.TargetType, action.TargetIndex))
                    throw new CandidateRejectedException("card-target", "Route card target is illegal.");
                Creature? target = card.TargetType == TargetType.Self ? null
                    : ResolveTarget(state, player, card.TargetType, action.TargetIndex);
                if (state.Engine!.PlayCardWithResultAsync(player, card, target).GetAwaiter().GetResult() is null)
                    throw new CandidateRejectedException("card-rejected", "Core rejected route card play.");
                break;
            }
            case "UsePotion":
            {
                if (action.PotionSlot < 0 || action.PotionSlot >= player.PotionSlots.Count
                    || player.PotionSlots[action.PotionSlot] is not { } potion
                    || potion.Id.Entry != action.PotionId)
                    throw new CandidateRejectedException("potion-unavailable", "Route potion slot is unavailable.");
                if (!ActionGenerator.IsLegalTarget(state, player, potion.TargetType, action.TargetIndex))
                    throw new CandidateRejectedException("potion-target", "Route potion target is illegal.");
                Creature? target = ResolveTarget(state, player, potion.TargetType, action.TargetIndex);
                if (!PotionCmd.CanUseManually(state, player, potion, target))
                    throw new CandidateRejectedException("potion-unusable", "Route potion is illegal.");
                PotionCmd.Use(potion, player, target).GetAwaiter().GetResult();
                break;
            }
            case "EndTurn":
                state.Engine!.EndPlayerTurnAsync().GetAwaiter().GetResult();
                break;
            default:
                throw new InvalidOperationException($"Unknown route action {action.Kind}.");
        }
        state.Engine!.CheckWinCondition();
        int expectedCount = script?.Depth ?? 0;
        if (source.ChoicesConsumed != expectedCount)
            throw new InvalidOperationException("Route has unconsumed card selections.");
    }

    internal static CardModel? FindHandCardByOccurrence(
        IReadOnlyList<CardModel> hand,
        string? cardId,
        int occurrence)
    {
        if (cardId is null || occurrence < 0)
            return null;
        int matchIndex = 0;
        for (int i = 0; i < hand.Count; i++)
        {
            CardModel candidate = hand[i];
            if (string.Equals(candidate.Id.Entry, cardId, StringComparison.Ordinal))
            {
                if (matchIndex == occurrence)
                    return candidate;
                matchIndex++;
            }
        }
        return null;
    }

    public static Creature? ResolveTarget(CombatState state, Player player, TargetType type, int index) =>
        type switch
        {
            TargetType.Self or TargetType.AnyPlayer => player.Creature,
            TargetType.AnyAlly when index <= -2 && -2 - index < state.Allies.Count =>
                state.Allies[-2 - index],
            TargetType.AnyEnemy or TargetType.Osty when index >= 0 && index < state.Enemies.Count =>
                state.Enemies[index],
            TargetType.Osty when index == -1 => player.Creature,
            _ => null,
        };

    private sealed class ScriptedRecordingChoiceSource : ICardSelectionDecisionSource
    {
        private readonly IReadOnlyList<IReadOnlyList<int>>? _listScript;
        private readonly IReadOnlyList<int>[]? _chainSteps;
        private readonly int _stepCount;
        private readonly List<string> _recordedIds;
        private int _cursor;

        public ScriptedRecordingChoiceSource(
            IReadOnlyList<IReadOnlyList<int>>? listScript,
            ChoiceScriptNode? chain,
            List<string> recordedIds)
        {
            _recordedIds = recordedIds;
            if (listScript is not null)
            {
                _listScript = listScript;
                _stepCount = listScript.Count;
            }
            else if (chain is not null)
            {
                int depth = chain.Depth;
                _stepCount = depth;
                var steps = new IReadOnlyList<int>[depth];
                ChoiceScriptNode? cur = chain;
                for (int i = depth - 1; i >= 0; i--)
                {
                    steps[i] = cur!.StepIndices;
                    cur = cur.Parent;
                }
                _chainSteps = steps;
            }
        }

        public int ChoicesConsumed => _cursor;

        public Task<IReadOnlyList<CardModel>> ChooseCardsAsync(CardSelectionRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (_cursor >= _stepCount)
                throw new SolverSelectionRequiredException(request);

            IReadOnlyList<int> indices = _chainSteps is not null
                ? _chainSteps[_cursor++]
                : _listScript![_cursor++];
            if (indices.Count == 0)
                return Task.FromResult<IReadOnlyList<CardModel>>(Array.Empty<CardModel>());

            IReadOnlyList<CardModel> candidates = request.Candidates;
            var cards = new CardModel[indices.Count];
            for (int i = 0; i < indices.Count; i++)
            {
                int index = indices[i];
                if ((uint)index >= (uint)candidates.Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(indices), index,
                        "Choice index is outside the current Core request.");
                }
                CardModel selected = candidates[index];
                cards[i] = selected;
                _recordedIds.Add(selected.Id.Entry);
            }
            return Task.FromResult<IReadOnlyList<CardModel>>(cards);
        }
    }
}
