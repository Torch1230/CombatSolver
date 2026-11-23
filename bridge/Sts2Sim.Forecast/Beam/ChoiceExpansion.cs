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

    public static async Task<Expansion> ExpandAsync(SearchNode parent,
        PlannedAction action, BeamBudget budget, StateEvaluator evaluator,
        Func<bool> tryReserveNode, Stopwatch watch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tryReserveNode);
        var results = new List<SearchNode>();
        var rejections = new Dictionary<string, int>(StringComparer.Ordinal);
        int parentTurn = parent.Turn;
        int attempts = 0;
        bool truncated = false;
        bool hitPendingChoiceBoundary = false;
        await ExploreAsync([], [], 0);
        return new Expansion(results, attempts, truncated, rejections, hitPendingChoiceBoundary);

        async Task ExploreAsync(IReadOnlyList<IReadOnlyList<int>> script,
            IReadOnlyList<string> choiceCards, int choiceDepth)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await ExploreBranchAsync(script, choiceCards, choiceDepth);
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
                    error.Data["Beam.ChoiceScript"] = script;
                }
                throw;
            }
        }

        async Task ExploreBranchAsync(IReadOnlyList<IReadOnlyList<int>> script,
            IReadOnlyList<string> choiceCards, int choiceDepth)
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
            CombatState child = parent.State.Clone();
            var ledger = new PredictionLedgerSink();
            child.PredictionSink = ledger;
            try
            {
                IReadOnlyList<string> observed = await ActionExecutor.ExecuteAsync(child, action, script);
                if (!observed.SequenceEqual(choiceCards, StringComparer.Ordinal))
                    throw new InvalidOperationException("Choice replay did not match its parent script.");
                PlannedAction resolved = action with
                {
                    ChoiceIndices = script.Select(indices => (IReadOnlyList<int>)indices.ToArray()).ToArray(),
                    ChoiceCardIds = observed.ToArray(),
                };
                if (evaluator.AllowsMaxHpLoss(parent.BrightestFlameMaxHpLost
                    + ledger.BrightestFlameMaxHpLost))
                    results.Add(SearchNode.Create(child, parent, resolved, evaluator,
                        ledger.BrightestFlameMaxHpLost,
                        evaluator.CaptureGrowth
                            ? GrowthLedger.Delta(parent.State, child, resolved, ledger) : default,
                        ledger.DeathSaveHpRestored, ledger.DeathSaveUseCount));
            }
            catch (SolverSelectionRequiredException pending)
            {
                // fail-open: selection suspension is consumed by replaying each choice from the parent.
                // The exception may interrupt a Core action halfway through; only the untouched
                // parent can be used to explore the next scripted alternative.
                CardSelectionRequest request = pending.Request;
                int limit = IsHandChoice(request) ? budget.MaxHandChoiceBranches
                    : budget.MaxPileChoiceBranches;
                int enumeratedCount = 0;
                foreach (int[] indices in EnumerateSelections(request, limit))
                {
                    enumeratedCount++;
                    ct.ThrowIfCancellationRequested();
                    IReadOnlyList<IReadOnlyList<int>> next = script.Concat([indices]).ToArray();
                    IReadOnlyList<string> ids = choiceCards.Concat(indices.Select(index =>
                        request.Candidates[index].Id.Entry)).ToArray();
                    await ExploreAsync(next, ids, choiceDepth + 1);
                    if (results.Count >= budget.MaxCardBranches || truncated) break;
                }
                if (enumeratedCount == 0)
                {
                    hitPendingChoiceBoundary = true;
                }
            }
            catch (CandidateRejectedException rejected)
            {
                // fail-open: an explicit action rejection drops only this candidate and records its reason.
                rejections[rejected.Reason] = rejections.GetValueOrDefault(rejected.Reason) + 1;
            }
        }
    }

    private static bool IsHandChoice(CardSelectionRequest request) =>
        request.Candidates.Count > 0 && request.Player.PlayerCombatState is { } state
        && request.Candidates.All(state.Hand.Cards.Contains);

    private static IEnumerable<int[]> EnumerateSelections(CardSelectionRequest request, int limit)
    {
        int count = 0;
        int min = Math.Clamp(request.MinCount, 0, request.Candidates.Count);
        int max = Math.Clamp(request.MaxCount, min, request.Candidates.Count);
        for (int size = min; size <= max && count < limit; size++)
        {
            foreach (int[] selection in Combinations(size, 0, []))
            {
                yield return selection;
                if (++count >= limit) yield break;
            }
        }

        IEnumerable<int[]> Combinations(int remaining, int start, List<int> prefix)
        {
            if (remaining == 0)
            {
                yield return prefix.ToArray();
                yield break;
            }
            for (int index = start; index <= request.Candidates.Count - remaining; index++)
            {
                prefix.Add(index);
                foreach (int[] choice in Combinations(remaining - 1, index + 1, prefix))
                    yield return choice;
                prefix.RemoveAt(prefix.Count - 1);
            }
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
        var scripted = new SolverSelectionSource(script);
        var recording = new RecordingChoiceSource(scripted);
        state.CardSelectionSource = recording;
        Player player = state.Players[0];
        if ((player.PlayerCombatState?.TurnNumber ?? 0) != action.Turn)
            throw new InvalidOperationException("Route turn does not match the Core state.");
        switch (action.Kind)
        {
            case "PlayCard":
            {
                CardModel card = player.PlayerCombatState!.Hand.Cards
                    .Where(candidate => candidate.Id.Entry == action.CardId)
                    .Skip(action.CardOccurrence).FirstOrDefault()
                    ?? throw new CandidateRejectedException("card-unavailable", $"Route card {action.CardId} is unavailable.");
                if (!card.CanPlay(out _)) throw new CandidateRejectedException("card-unplayable", "Route card cannot be played.");
                if (!ActionGenerator.IsLegalTarget(state, player, card.TargetType, action.TargetIndex))
                    throw new CandidateRejectedException("card-target", "Route card target is illegal.");
                Creature? target = card.TargetType == TargetType.Self ? null
                    : ResolveTarget(state, player, card.TargetType, action.TargetIndex);
                if (await state.Engine!.PlayCardWithResultAsync(player, card, target) is null)
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
                await PotionCmd.Use(potion, player, target);
                break;
            }
            case "EndTurn":
                await state.Engine!.EndPlayerTurnAsync();
                break;
            default:
                throw new InvalidOperationException($"Unknown route action {action.Kind}.");
        }
        state.Engine!.CheckWinCondition();
        if (scripted.ChoicesConsumed != script.Count)
            throw new InvalidOperationException("Route has unconsumed card selections.");
        return recording.CardIds;
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

    private sealed class RecordingChoiceSource(SolverSelectionSource source) : ICardSelectionDecisionSource
    {
        public List<string> CardIds { get; } = [];
        public async Task<IReadOnlyList<CardModel>> ChooseCardsAsync(CardSelectionRequest request)
        {
            IReadOnlyList<CardModel> selected = await source.ChooseCardsAsync(request);
            CardIds.AddRange(selected.Select(card => card.Id.Entry));
            return selected;
        }
    }
}
