using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

/// <summary>
/// 离线搜索宿主（<c>tools/search/OfflineSearchHarness</c>）的入口：
/// 按无人测试口径配置搜索预算开关，以及按生成场景顺序注入跑局装备。
/// </summary>
internal sealed partial class UnattendedTestRunner
{
    internal sealed record OfflineSessionOptions
    {
        public bool FixedSearchBudget { get; init; } = true;
        public bool VerifyIncrementalSearch { get; init; }
        public bool MeasureSearchPhases { get; init; }
        public int? SearchBudgetOverrideMilliseconds { get; init; }
        public int? SearchMaxDegreeOfParallelism { get; init; }
        public int TranspositionPruningDisabledMask { get; init; }
        public int? TranspositionEntryLimit { get; init; }
    }

    internal static IDisposable BeginOfflineSession(OfflineSessionOptions options)
        => Host.BeginOfflineSession(options);

    private sealed partial class ProtocolHost
    {
        public IDisposable BeginOfflineSession(OfflineSessionOptions options)
        {
            ConfigureSearchOverrides(new UnattendedTestRequest
            {
                FixedSearchBudget = options.FixedSearchBudget,
                VerifyIncrementalSearch = options.VerifyIncrementalSearch,
                MeasureSearchPhases = options.MeasureSearchPhases,
                SearchBudgetOverrideMilliseconds = options.SearchBudgetOverrideMilliseconds,
                SearchMaxDegreeOfParallelismForTest = options.SearchMaxDegreeOfParallelism,
                TranspositionPruningDisabledMaskForTest =
                    options.TranspositionPruningDisabledMask == 0
                        ? null
                        : options.TranspositionPruningDisabledMask,
                TranspositionEntryLimitForTest = options.TranspositionEntryLimit,
            });
            IsActive = true;
            AutomaticTurnSearchEnabled = false;
            return new OfflineSessionScope(this);
        }

        private sealed class OfflineSessionScope(ProtocolHost host) : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                host.Reset();
            }
        }
    }

    internal sealed class OfflineScenarioSession
    {
        private readonly UnattendedTestRunner _runner;

        private OfflineScenarioSession(UnattendedTestRunner runner) => _runner = runner;

        internal static OfflineScenarioSession Create(
            UnattendedTestRequest request, ResolvedGeneratedCombatScenario? resolved = null)
        {
            UnattendedTestRunner runner = new(host: null!, request, Host);
            runner._scenarioBuilder.OfflineGeneratedScenario = resolved;
            return new OfflineScenarioSession(runner);
        }

        internal UnattendedTestRequest Request => _runner._request;
        internal IReadOnlyList<string> CompletedChecks => _runner._completedChecks;
        internal string[][] SetupChoices
            => _runner._scenarioBuilder.OfflineGeneratedScenario?.Options.SetupChoices ?? [];
        internal void AddCompletedCheck(string check) => _runner._completedChecks.Add(check);
        internal void WriteGeneratedArtifact(string name, object value)
            => _runner._writer.WriteGeneratedArtifact(name, value);
        internal IDisposable? BeginSetupChoices() => _runner._scenarioBuilder.OfflineBeginSetupChoices();
        internal void PrepareStartingRelics(Player player)
            => _runner._scenarioBuilder.OfflinePrepareStartingRelics(player);
        internal Task PrepareAscendersBaneAsync(RunState runState, Player player)
            => _runner._scenarioBuilder.OfflinePrepareAscendersBaneAsync(runState, player);
        internal void PreparePotionSlots(Player player)
            => _runner._scenarioBuilder.OfflinePreparePotionSlots(player);
        internal void CaptureLoadout(RunState runState, Player player)
            => _runner._scenarioBuilder.OfflineCaptureLoadout(runState, player);
        internal void CaptureOpening(CombatState combat, Player player)
            => _runner._scenarioBuilder.OfflineCaptureOpening(combat, player);
        internal Task InjectInitialStateAsync(CombatState combat, Player player)
            => _runner._scenarioBuilder.InjectInitialStateAsync(combat, player);

        internal static Task InjectRelicAsync(Player player, UnattendedRelicInjection injection)
            => UnattendedTestRunner.InjectRelicAsync(player, injection);
        internal static void ClearRunDeck(RunState runState, Player player)
            => UnattendedTestRunner.ClearRunDeck(runState, player);
        internal static Task InjectRunCardAsync(
            RunState runState, Player player, UnattendedCardInjection injection)
            => UnattendedTestRunner.InjectRunCardAsync(runState, player, injection);
        internal static void InjectPotionForTest(Player player, string potionId)
            => UnattendedTestRunner.InjectPotionForTest(player, potionId);

        internal UnattendedSolverMetrics? CaptureSolverMetrics(SolverResult result)
        {
            _runner._writer.CaptureSolverResult(result);
            return _runner._writer.OfflineSolverMetrics;
        }
    }

    private sealed partial class ScenarioBuilder
    {
        internal ResolvedGeneratedCombatScenario? OfflineGeneratedScenario
        {
            get => _generatedScenario;
            set => _generatedScenario = value;
        }

        internal IDisposable? OfflineBeginSetupChoices() => BeginGeneratedSetupChoices();
        internal void OfflinePrepareStartingRelics(Player player) => PrepareGeneratedStartingRelics(player);
        internal Task OfflinePrepareAscendersBaneAsync(RunState runState, Player player)
            => PrepareGeneratedAscendersBaneAsync(runState, player);
        internal void OfflinePreparePotionSlots(Player player) => PrepareGeneratedPotionSlots(player);
        internal void OfflineCaptureLoadout(RunState runState, Player player)
            => CaptureGeneratedLoadout(runState, player);
        internal void OfflineCaptureOpening(CombatState combat, Player player)
            => CaptureGeneratedOpening(combat, player);
    }

    private sealed partial class Writer
    {
        internal UnattendedSolverMetrics? OfflineSolverMetrics => _solverMetrics;
    }
}
