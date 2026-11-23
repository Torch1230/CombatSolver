using System.Diagnostics;
using CombatSolver.Beam.Policy;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Combat.StateDescription;
using Sts2Sim.Core.Models;

namespace CombatSolver.Beam;

public sealed record OpeningChoiceStep(int Min, int Max, bool Cancelable, bool Bundle,
    string? Source, IReadOnlyList<string> CandidateIds, IReadOnlyList<int> Indices)
{
    internal static OpeningChoiceStep Capture(CardSelectionRequest request, IReadOnlyList<int> indices) =>
        new(request.MinCount, request.MaxCount, request.Cancelable, request.IsBundleSelection,
            request.Source?.Id.ToString(), request.Candidates.Select(c => c.Id.Entry).ToArray(), indices.ToArray());

    internal IReadOnlyList<CardModel> Apply(CardSelectionRequest request)
    {
        if (Min != request.MinCount || Max != request.MaxCount || Cancelable != request.Cancelable
            || Bundle != request.IsBundleSelection || Source != request.Source?.Id.ToString()
            || !CandidateIds.SequenceEqual(request.Candidates.Select(c => c.Id.Entry))
            || Indices.Count < Min || Indices.Count > Max || Indices.Distinct().Count() != Indices.Count
            || Indices.Any(i => i < 0 || i >= request.Candidates.Count))
            throw new InvalidOperationException("Opening choice script diverged from its exact request.");
        return Indices.Select(i => request.Candidates[i]).ToArray();
    }
}

public sealed record OpeningCandidateObservation(int Index, IReadOnlyList<OpeningChoiceStep> Choices,
    int NodeAllowance, long ExpandedNodes, bool Won, bool Ended, int HpLost, bool BudgetTruncated,
    int ValidationActions, double PolicyHpCost, int DeathSaveUses, int RelicTargetsSatisfied);

public sealed class SharedCombatBudgetExhaustedException : Exception
{
    public SharedCombatBudgetExhaustedException(long attempts, long nodeLimit)
        : base("Shared combat exploration budget was exhausted.")
    {
        if (nodeLimit <= 0 || attempts != nodeLimit)
            throw new ArgumentOutOfRangeException(nameof(attempts),
                "Shared budget exhaustion requires attempts equal to a positive node limit.");
        Data["Sts2Sim.SharedCombatBudgetExhausted.v1"] = true;
        Data["SharedBudget.Attempts"] = attempts;
        Data["SharedBudget.NodeLimit"] = nodeLimit;
    }
}

internal sealed class BeamOpeningPlanner(CombatOpeningContext context, BeamBudget budget,
    CombatPolicy policy, CancellationToken cancellationToken)
{
    internal sealed record Result(BeamResult Plan, IReadOnlyList<OpeningChoiceStep> Choices,
        string OpeningFingerprint, IReadOnlyList<CombatStateDescriptionDigest> Expected,
        BeamOpeningPlanner Owner);
    private sealed record Leaf(CombatState State, OpeningPolicyLedger Ledger,
        IReadOnlyList<OpeningChoiceStep> Choices);
    private sealed record Evaluated(Leaf Leaf, BeamResult Plan, SearchNode Ending,
        IReadOnlyList<CombatStateDescriptionDigest> Expected, string OpeningFingerprint);
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private readonly List<OpeningCandidateObservation> _observations = [];
    internal int OpeningAttempts { get; private set; }
    internal long SearchAttempts { get; private set; }
    internal int ValidationActions { get; private set; }
    internal int SearchCount { get; private set; }
    internal bool CoverageTruncated { get; private set; }
    internal int SelectedIndex { get; private set; } = -1;
    internal IReadOnlyList<OpeningCandidateObservation> Observations => _observations.AsReadOnly();
    internal long TotalAttempts => OpeningAttempts + SearchAttempts;
    internal TimeSpan RemainingTime => budget.TimeLimit - _watch.Elapsed;
    internal static bool IsSmallChoice(CardSelectionRequest request) =>
        request.MinCount == 1 && request.MaxCount == 1 && !request.Cancelable && !request.IsBundleSelection
        && request.Candidates.Count is >= 1 and <= 5;

    internal void CheckDeadline()
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (RemainingTime <= TimeSpan.Zero)
            throw new TimeoutException("Shared opening/combat search deadline was exhausted.");
    }

    // Effective budget is resolved once at the entry. Only its varying N/time
    // operands are removed from policy overrides before each bounded child solve.
    internal CombatPolicy BoundedPolicy => policy with
    {
        SearchMaxExpandedNodes = null, SearchTimeLimitSeconds = null,
        Profile = policy.Profile is null ? null : policy.Profile with
        { MaxExpandedNodes = null, SoftTimeBudgetMilliseconds = null },
    };
    internal BeamBudget RemainingBudget()
    {
        CheckDeadline();
        long remaining = budget.NodeLimit - TotalAttempts;
        if (remaining == 0) throw new SharedCombatBudgetExhaustedException(TotalAttempts, budget.NodeLimit);
        if (remaining < 0) throw new InvalidOperationException("Opening planner exceeded the shared exploration budget.");
        return budget with { NodeLimit = checked((int)remaining), TimeLimit = RemainingTime };
    }
    internal void RecordSearch(BeamResult result)
    {
        SearchCount++;
        SearchAttempts += result.ExpandedNodes;
        if (TotalAttempts > budget.NodeLimit)
            throw new InvalidOperationException("Opening planner exceeded the shared exploration budget.");
        CheckDeadline();
    }
    internal void RecordValidation() { ValidationActions++; CheckDeadline(); }

    internal async Task<Result> SolveAsync(IReadOnlyList<OpeningChoiceStep> livePrefix)
    {
        var baseline = new OpeningComparisonBaseline(context.CopyPreparedState());
        int openingCap = Math.Min(64, budget.NodeLimit / 16);
        var pending = new Stack<IReadOnlyList<OpeningChoiceStep>>();
        pending.Push(livePrefix.ToArray());
        var leaves = new List<Leaf>();
        while (pending.Count > 0 && leaves.Count < 16 && OpeningAttempts < openingCap)
        {
            CheckDeadline();
            OpeningAttempts++;
            var source = new ExploringSource(pending.Pop(), this);
            var ledger = new OpeningPolicyLedger();
            try
            {
                CombatState state = await context.ReplayObservedAsync(source, ledger, cancellationToken);
                source.AssertConsumed();
                ledger.Complete(state);
                CheckDeadline();
                leaves.Add(new Leaf(state, ledger, source.Seen.ToArray()));
            }
            catch (OpeningSelectionRequired required)
            {
                // fail-open: selection prompt; one pending script is pushed per candidate
                for (int i = required.Request.Candidates.Count - 1; i >= 0; i--)
                    pending.Push(source.Seen.Append(OpeningChoiceStep.Capture(required.Request, [i])).ToArray());
            }
        }
        CoverageTruncated |= pending.Count > 0;
        if (leaves.Count == 0)
            throw new InvalidOperationException("Opening budget produced no complete executable opening.");
        int allowance = (budget.NodeLimit - OpeningAttempts) / 2 / leaves.Count;
        if (allowance < 1) throw new InvalidOperationException("Opening leaves have no shared Beam allowance.");
        var evaluator = new StateEvaluator(baseline, leaves[0].State, policy);
        var evaluated = new List<Evaluated>();
        for (int i = 0; i < leaves.Count; i++)
        {
            CheckDeadline();
            Leaf leaf = leaves[i];
            if (!evaluator.AllowsMaxHpLoss(leaf.Ledger.BrightestFlameMaxHpLost))
                throw new InvalidOperationException("Opening violates the maximum HP-loss policy.");
            string openingFingerprint = CombatOpeningContext.Fingerprint(leaf.State);
            BeamResult plan = leaf.State.Engine is { IsInProgress: false } engine
                ? new BeamResult([], engine.Won, 0, 0, false)
                : BeamSearch.Solve(leaf.State, budget with
                    { NodeLimit = allowance, TimeLimit = RemainingTime }, BoundedPolicy, cancellationToken);
            RecordSearch(plan);
            int validationBefore = ValidationActions;
            SearchNode node = SearchNode.Create(leaf.State.Clone(), null, null, evaluator,
                leaf.Ledger.BrightestFlameMaxHpLost, leaf.Ledger.Growth,
                leaf.Ledger.DeathSaveHpRestored, leaf.Ledger.DeathSaveUseCount);
            foreach (PlannedAction used in leaf.Ledger.Potions)
                node = SearchNode.Create(node.State, node, used, evaluator);
            var expected = new List<CombatStateDescriptionDigest>();
            foreach (PlannedAction action in plan.Actions)
            {
                CheckDeadline();
                CombatState after = node.State.Clone();
                var sink = new PredictionLedgerSink();
                after.PredictionSink = sink;
                IReadOnlyList<string> observed = await ActionExecutor.ExecuteAsync(after, action, action.ChoiceIndices);
                if (!observed.SequenceEqual(action.ChoiceCardIds))
                    throw new InvalidOperationException("Opening candidate Beam choices failed strict replay.");
                node = SearchNode.Create(after, node, action, evaluator, sink.BrightestFlameMaxHpLost,
                    evaluator.CaptureGrowth ? GrowthLedger.Delta(node.State, after, action, sink) : default,
                    sink.DeathSaveHpRestored, sink.DeathSaveUseCount,
                    playedCards: sink.PlayedCards,
                    hasImpureHistory: sink.PotionsProcured > 0);
                if (!evaluator.AllowsMaxHpLoss(node.BrightestFlameMaxHpLost))
                    throw new InvalidOperationException("Opening plus Beam route violates the maximum HP-loss policy.");
                expected.Add(node.Fingerprint);
                RecordValidation();
            }
            if ((!node.Ended && !plan.BudgetTruncated) || node.Won != plan.Won
                || node.Ended && Math.Max(0, leaf.State.Players[0].Creature.CurrentHp
                    - node.State.Players[0].Creature.CurrentHp) != plan.HpLost)
                throw new InvalidOperationException("Opening candidate outcome failed strict replay.");
            _observations.Add(new OpeningCandidateObservation(i, leaf.Choices, allowance,
                plan.ExpandedNodes, node.Won, node.Ended,
                Math.Max(0, baseline.InitialHp - node.State.Players[0].Creature.CurrentHp),
                plan.BudgetTruncated, ValidationActions - validationBefore,
                evaluator.PolicyHpCost(node, baseline.InitialHp), node.DeathSaveUseCount,
                evaluator.SatisfiedRelicTargets(node)));
            evaluated.Add(new Evaluated(leaf, plan, node, expected, openingFingerprint));
        }
        SearchNode selected = PlanSelector.Select(evaluated.Select(e => e.Ending), baseline.InitialHp, evaluator);
        SelectedIndex = evaluated.FindIndex(e => ReferenceEquals(e.Ending, selected));
        Evaluated best = evaluated[SelectedIndex];
        CheckDeadline();
        return new Result(best.Plan, best.Leaf.Choices, best.OpeningFingerprint, best.Expected, this);
    }

    private sealed class OpeningSelectionRequired(CardSelectionRequest request) : Exception
    { internal CardSelectionRequest Request { get; } = request; }

    private sealed class ExploringSource(IReadOnlyList<OpeningChoiceStep> prefix, BeamOpeningPlanner owner)
        : ICardSelectionDecisionSource
    {
        internal List<OpeningChoiceStep> Seen { get; } = [];
        public Task<IReadOnlyList<CardModel>> ChooseCardsAsync(CardSelectionRequest request)
        {
            owner.CheckDeadline();
            OpeningChoiceStep step;
            if (Seen.Count < prefix.Count) step = prefix[Seen.Count];
            else if (Seen.Count < 8 && IsSmallChoice(request))
                throw new OpeningSelectionRequired(request);
            else
            {
                if (IsSmallChoice(request)) owner.CoverageTruncated = true;
                step = OpeningChoiceStep.Capture(request, Enumerable.Range(0, request.MinCount).ToArray());
            }
            IReadOnlyList<CardModel> selected = step.Apply(request);
            Seen.Add(step);
            return Task.FromResult(selected);
        }
        internal void AssertConsumed()
        {
            if (Seen.Count < prefix.Count)
                throw new InvalidOperationException("Opening script contains unconsumed choices.");
        }
    }
}
