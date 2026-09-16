using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

/// <summary>
/// 离线搜索宿主（<c>tools/OfflineSearchHarness</c>）的入口。宿主是一个不启动 Godot 的普通
/// .NET 进程，需要做两件在游戏内由无人测试协议主机和 <c>ScenarioBuilder</c> 做的事：
/// 按无人测试的口径设一次搜索预算开关，以及按生成场景的顺序注入跑局装备。
/// 这里把这两件事转出去，方法体仍然是原来那些——宿主不复刻、也不再用反射去写私有成员。
/// 本文件只新增成员：不给它们传值时，游戏内的每一条路径与改动前完全一致。
/// </summary>
internal sealed partial class UnattendedTestRunner
{
    /// <summary>
    /// 离线会话要覆盖的搜索口径。字段与 <see cref="UnattendedTestRequest"/> 上的同名项一一对应，
    /// 真正的解析与校验仍由 <c>ProtocolHost.ConfigureSearchOverrides</c> 做。
    /// </summary>
    internal sealed record OfflineSessionOptions
    {
        /// <summary>固定搜索预算（无人测试的默认口径）。</summary>
        public bool FixedSearchBudget { get; init; } = true;

        public bool VerifyIncrementalSearch { get; init; }

        public bool MeasureSearchPhases { get; init; }

        /// <summary>软时间预算毫秒；不给就沿用 profile 自己的。</summary>
        public int? SearchBudgetOverrideMilliseconds { get; init; }

        /// <summary>搜索并行度；不给就沿用设置里的值。</summary>
        public int? SearchMaxDegreeOfParallelism { get; init; }

        /// <summary>开宽度组合（生产协调器的组合成员通道）。</summary>
        public bool UseBeamWidthPortfolio { get; init; }

        /// <summary>组合成员宽度；不给就用协调器自己的默认成员集。</summary>
        public int[]? BeamWidthPortfolioWidths { get; init; }
    }

    /// <summary>
    /// 开一段离线会话：把 <paramref name="options"/> 按无人测试请求的同一套映射写进协议主机，
    /// 让 <c>SolverController.CaptureSearchPolicy</c> 读到与游戏内无人测试一致的口径。
    /// 释放返回值即还原（等价于一次无人测试请求结束）。
    /// </summary>
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
                UseBeamWidthPortfolioForTest = options.UseBeamWidthPortfolio ? true : null,
                BeamWidthPortfolioWidthsForTest = options.BeamWidthPortfolioWidths,
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

    /// <summary>
    /// 离线宿主的生成场景会话：建一个不挂在 <c>NGame</c> 上的 runner，只用它的请求、产物写入器
    /// 和 <c>ScenarioBuilder</c>。注入顺序由宿主按 <c>ScenarioBuilder.BuildAsync</c> 复述，
    /// 每一步调的都是下面这些原方法。
    /// </summary>
    internal sealed class OfflineScenarioSession
    {
        private readonly UnattendedTestRunner _runner;

        private OfflineScenarioSession(UnattendedTestRunner runner) => _runner = runner;

        /// <summary>
        /// <paramref name="resolved"/> 为 null 时只建请求与写入器（宿主的「不走生成场景」那条路径）。
        /// </summary>
        internal static OfflineScenarioSession Create(
            UnattendedTestRequest request, ResolvedGeneratedCombatScenario? resolved = null)
        {
            // 离线没有 Godot 节点树。_host 只被「跑完退出游戏」「切 UI」这些路径用到，
            // 宿主一条都不走；构造函数本身不碰它。
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

        // 装备注入是 UnattendedTestRunner 自己的静态方法，宿主按 BuildAsync 的顺序调。
        internal static Task InjectRelicAsync(Player player, UnattendedRelicInjection injection)
            => UnattendedTestRunner.InjectRelicAsync(player, injection);

        internal static void ClearRunDeck(RunState runState, Player player)
            => UnattendedTestRunner.ClearRunDeck(runState, player);

        internal static Task InjectRunCardAsync(
            RunState runState, Player player, UnattendedCardInjection injection)
            => UnattendedTestRunner.InjectRunCardAsync(runState, player, injection);

        internal static void InjectPotionForTest(Player player, string potionId)
            => UnattendedTestRunner.InjectPotionForTest(player, potionId);

        /// <summary>用游戏自己的写入器把 <see cref="SolverResult"/> 折成 result.json 里的那份指标。</summary>
        internal UnattendedSolverMetrics? CaptureSolverMetrics(SolverResult result)
        {
            _runner._writer.CaptureSolverResult(result);
            return _runner._writer.OfflineSolverMetrics;
        }
    }

    private sealed partial class ScenarioBuilder
    {
        /// <summary>生成场景解析结果；宿主自己做 Resolve/Apply，所以要能写进来。</summary>
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
