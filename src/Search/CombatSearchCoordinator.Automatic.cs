using System.Diagnostics;

namespace CombatSolver;

internal static partial class CombatSearchCoordinator
{
    // One automatic entry owns the primary portfolio and its prefix refinements.
    // Legacy switches/layouts remain available only to reproducible test arms.
    private static SolverResult RunAutomaticSearch(
        CombatRootSnapshot root, SolverDisplayNames names, BattleDamageSnapshot damage,
        SearchPolicySnapshot policy, SolverSearchProfile profile, Stopwatch clock,
        SolverPotionPolicy? potionOverride, CancellationToken cancellation,
        Action<SolverProgress>? progress, Action<SolverResult>? publish,
        Func<SearchPolicySnapshot, SolverSearchProfile, Func<PlanAction?>, SolverResult> solveBeam,
        long expandedAtStart)
    {
        SearchRequestWorkTotals totals = policy.RequestWorkTotals
            ?? throw new InvalidOperationException("Automatic search requires request work totals.");
        RootOutcomeCache outcomes = new();
        OpeningActionCollector openings = new();
        SearchDiagnosticsSink original = policy.Diagnostics;
        SearchCompletedOutcomeObserver? external = original.CompletedOutcomeObserver;
        SearchDiagnosticsSink diagnostics = new(original.Info, original.Debug,
            original.PathObserver,
            new SearchCompletedOutcomeObserver(
                () => outcomes.Observer.WantsObservation() || external?.WantsObservation() == true,
                outcome =>
                {
                    if (outcomes.Observer.WantsObservation()) outcomes.Observe(outcome);
                    if (external?.WantsObservation() == true) external.Observe(outcome);
                }), openings.Observer);
        policy = policy with
        {
            UseBeamWidthPortfolio = true,
            UseNoveltyPortfolio = true,
            BeamWidthPortfolioWidths = null,
            BeamWidthPortfolioPlainBaselineMember = true,
            PortfolioExperiment = null,
            Diagnostics = diagnostics,
        };
        SolverSearchProfile? Remaining(long? nodeLimit = null, int divisor = 1)
            => AutomaticSearchBudget.Remaining(profile,
                clock.ElapsedMilliseconds, totals.Snapshot().ExpandedNodes - expandedAtStart,
                nodeLimit, divisor);
        SolverSearchProfile available = Remaining()
            ?? throw new PotionPolicyUnsatisfiedException("Automatic search exhausted its pass budget.");
        PlanAction? SelectPowerPrefix()
        {
            PlanAction[] powers = openings.Actions.Values.Where(a => a.CardId != null
                && PowerCardValuationModels.Registry.ContainsCardId(a.CardId)).ToArray();
            // The baseline already reached the known continuations. Give an
            // unobserved power a chance before replaying the same known opening.
            PlanAction[] unknown = powers.Where(a => !outcomes.Best.ContainsKey(RootOutcomeCache.ActionKey(a))).ToArray();
            return outcomes.Schedule(unknown, 1).FirstOrDefault();
        }
        SolverResult selected = RunNoveltyPortfolioPass(root, names, damage, policy, available,
            Stopwatch.StartNew(), potionOverride, cancellation, progress, publish,
            beamProfile => solveBeam(policy, beamProfile, SelectPowerPrefix));
        if (selected.ResultScope != SolverResultScope.SearchCompletion
            || ResolveTakeoverResult(selected, policy.Interaction) is { })
            return ResolveTakeoverResult(selected, policy.Interaction) ?? selected;
        if (CanFinishTargetPortfolio(root, policy, profile, selected)) return selected;

        long primaryExpanded = totals.Snapshot().ExpandedNodes - expandedAtStart;
        long refinementLimit = Math.Min(profile.MaxExpandedNodes, primaryExpanded * 2);
        PlanAction[] scheduled = outcomes.Schedule(openings.Actions.Values, 4);
        // Keep an opening power represented without multiplying every power prefix
        // by every width. Choices/targets still retain their exact action identity.
        PlanAction? power = scheduled.FirstOrDefault(a => a.CardId != null
            && PowerCardValuationModels.Registry.ContainsCardId(a.CardId))
            ?? openings.Actions.Values.FirstOrDefault(a => a.CardId != null
                && PowerCardValuationModels.Registry.ContainsCardId(a.CardId));
        if (power != null)
            scheduled = new[] { power }.Concat(scheduled)
                .DistinctBy(RootOutcomeCache.ActionKey).Take(4).ToArray();
        NoveltyPortfolioTelemetry? novelty = selected.NoveltyPortfolio;
        for (int i = 0; i < scheduled.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (ResolveTakeoverResult(selected, policy.Interaction) is { } adopted) return adopted;
            if (CanFinishTargetPortfolio(root, policy, profile, selected)) break;
            SolverSearchProfile? remaining = Remaining(refinementLimit, scheduled.Length - i);
            if (remaining == null) break;
            SolverSearchProfile member = remaining with
            {
                BeamWidth = Math.Min(8, profile.BeamWidth),
                AggressivePowerCommitment = false,
                SecondRankBand = false,
                BaseScoreOnly = false,
            };
            SearchRequestWorkSnapshot before = totals.Snapshot();
            long start = clock.ElapsedMilliseconds;
            // Fixed prefixes are ordinary legal replay. An unfulfilled mandatory
            // potion route is unknown; simulation errors and cancellation propagate.
            SolverResult? candidate = SolveOptionalPotionPosterior(new CombatBeamSolver(
                root, names, damage, policy with { NoveltySearch = null,
                    Diagnostics = new(original.Info, original.Debug, original.PathObserver) },
                cancellation, progress == null ? null
                    : p => progress(p with { Phase = "正在精炼路线" }),
                member, potionPolicyOverride: potionOverride,
                fixedPrefixActions: [scheduled[i]], resetFixedPrefixSchedulingBaseline: true),
                policy, "automatic_prefix");
            if (candidate != null && ResolveTakeoverResult(candidate, policy.Interaction) is { } takeover)
                return takeover;
            if (candidate != null && candidate.ResultScope != SolverResultScope.SearchCompletion)
                return candidate;
            bool improved = candidate != null && IsCompleteVictory(candidate)
                && !candidate.Snapshot.HasRisk
                && candidate.Snapshot.BoundaryReason == SearchBoundaryReason.None
                && IsBetterPotionPolicyResult(root, policy, candidate, selected);
            if (improved)
            {
                selected = candidate!;
                selected.NoveltyPortfolio = novelty;
                publish?.Invoke(selected);
            }
            policy.Diagnostics.Info($"[CombatSolver/Test] AUTOMATIC_PREFIX index={i} " +
                $"card={scheduled[i].CardId} nodes={member.MaxExpandedNodes} time_ms={member.SoftTimeBudgetMilliseconds} " +
                $"expanded={totals.Snapshot().ExpandedNodes - before.ExpandedNodes} " +
                $"elapsed_ms={clock.ElapsedMilliseconds - start} improved={improved}");
        }
        policy.Diagnostics.Info($"[CombatSolver/Test] AUTOMATIC_SEARCH " +
            $"openings={openings.Actions.Count} witnesses={outcomes.Best.Count} " +
            $"expanded={totals.Snapshot().ExpandedNodes - expandedAtStart} elapsed_ms={clock.ElapsedMilliseconds}");
        return selected;
    }
}

// Uses actual work, including earlier failed members, and never grants a fresh
// per-prefix reserve. Time is a soft solver deadline, not a process kill deadline.
internal static class AutomaticSearchBudget
{
    internal static SolverSearchProfile? Remaining(SolverSearchProfile profile,
        long elapsedMilliseconds, long expandedNodes, long? nodeLimit = null, int divisor = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elapsedMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegative(expandedNodes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(divisor);
        long nodes = (Math.Min(profile.MaxExpandedNodes, nodeLimit ?? profile.MaxExpandedNodes) - expandedNodes) / divisor;
        long milliseconds = (profile.SoftTimeBudgetMilliseconds - elapsedMilliseconds) / divisor;
        return nodes < 1 || milliseconds < 1 ? null : profile with
        {
            MaxExpandedNodes = (int)nodes,
            SoftTimeBudgetMilliseconds = (int)milliseconds,
        };
    }
}
