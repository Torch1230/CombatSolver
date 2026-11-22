// Only game/process boundaries are replaced. Production Routing, Api, Idle, Bridge, and Contracts run directly.
using System.Collections.Concurrent;
using System.Text;
using CombatSolver.Api;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace MegaCrit.Sts2.Core.Rooms
{
    public enum RoomType { Monster, Elite, Boss, Event }
}
namespace MegaCrit.Sts2.Core.Map
{
    public enum MapPointType { Monster, Elite, Boss }
    public record struct MapCoord(int col, int row);
    public sealed class MapPoint
    {
        public MapCoord coord = new(0, 1);
        public MapPointType PointType;
        public List<MapPoint> Children = [];
    }
    public sealed class TestMap
    {
        public MapPoint BossMapPoint = new();
        public MapPoint? SecondBossMapPoint;
        public MapPoint GetPoint(MapCoord _) => new();
        public List<MapPoint> GetPointsInRow(int _) => [new()];
    }
}
namespace MegaCrit.Sts2.Core.Models
{
    public record ModelId(string Entry);
    public sealed class EncounterModel
    {
        public ModelId Id = new("TEST");
        public MegaCrit.Sts2.Core.Rooms.RoomType RoomType;
    }
    public static class ModelDb
    {
        public static T? GetByIdOrNull<T>(ModelId _) where T : new() => new();
    }
}
namespace MegaCrit.Sts2.Core.Runs
{
    public sealed class RunState
    {
        public string Token = Guid.NewGuid().ToString();
        public string? DirectIneligibility;
        public string? MandatoryExclusionCode;
        internal PreCombatExtraFieldsState ExtraFields = new(
            StartedWithNeow: true,
            FreedRepy: false,
            TestSubjectKills: 0,
            UsedDebugCommand: false,
            WongoPoints: 0,
            IsPlayingRotatingCharacter: false);
        public int CurrentActIndex;
        public int ActFloor = 1;
        public CombatSolver.SolverSettingsSnapshot? SettingsOverride;
        public MapCoord? CurrentMapCoord = new(0, 0);
        public MapPoint? CurrentMapPoint = new();
        public TestMap Map = new();
        public List<TestPlayer> Players = [new()];
        public TestRng Rng = new();
        public static RunState FromSerializable(SerializableRun _) => new();
    }
    public sealed class TestRng { public string StringSeed = "seed"; }
    public sealed class TestPlayer
    {
        public EncounterModel Character = new();
        public TestCreature Creature = new();
        public int NetId;
    }
    public sealed class TestCreature { public int MaxHp = 80; }
    public sealed class RunManager
    {
        public static RunManager Instance = new();
        public RunState Run = new();
        public RunState DebugOnlyGetState() => Run;
    }
}
namespace MegaCrit.Sts2.Core.Saves
{
    public sealed class SerializableRun
    {
        public SerializableRng SerializableRng = new();
        public object? PreFinishedRoom;
    }
    public sealed class SerializableRng { public string Seed = "seed"; }
}
namespace MegaCrit.Sts2.Core.Combat
{
    public sealed class CombatManager
    {
        public static CombatManager Instance = new();
        public bool IsInProgress;
    }
}
namespace MegaCrit.Sts2.Core.Nodes
{
    public static class NGame { public static bool IsMainThread() => true; }
}
namespace CombatSolver
{
    public sealed record SolverSettingsData;
    public sealed record SolverSettingsSnapshot(
        int SearchMaxDegreeOfParallelism = 1,
        bool UseBeamWidthPortfolio = true,
        bool UseNoveltyPortfolio = false,
        bool UseEarlyTurnExploration = false,
        bool ReallocatedRefinementPortfolio = true,
        bool OffensiveRefinementPortfolio = false,
        bool BoundedOffensiveRefinementPortfolio = false,
        bool StopPortfolioAtHpTarget = true,
        bool AdaptiveNoveltyRefinement = false);

    public static class Entry
    {
        public static bool Enabled { get; set; } = true;
        public static bool IsPreCombatWorker { get; set; }
        public static TestLogger Logger = new();
    }
    public sealed class TestLogger
    {
        public ConcurrentQueue<string> Errors { get; } = new();
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) => Errors.Enqueue(message);
    }
    public static class SolverDispatcher { public static void Post(Action action) => action(); }
}
namespace CombatSolver.Api
{
    internal static class OperatingSystem
    {
        public static bool WindowsOverride { get; set; } = true;
        public static bool IsWindows() => WindowsOverride;
    }

    internal readonly record struct PreCombatEligibilityResult(
        bool IsEligible,
        string? FailureCode,
        string? Detail,
        string Fingerprint)
    {
        public static PreCombatEligibilityResult Eligible(string fp) => new(true, null, null, fp);
        public static PreCombatEligibilityResult Ineligible(string code, string detail, string fp) => new(false, code, detail, fp);
    }

    internal static partial class PreCombatSts2SimEligibility
    {
        public static PreCombatEligibilityResult AssessMandatoryLiveHost(RunState run) =>
            run.MandatoryExclusionCode is { Length: > 0 } code
                ? PreCombatEligibilityResult.Ineligible(code, $"Mandatory host exclusion: {code}.", code)
                : PreCombatEligibilityResult.Eligible("eligible");
    }

    internal sealed record PreCombatLiveStateSnapshot(
        RunState LiveRun,
        byte[] SerializedRun,
        string StateToken,
        SolverSettingsData SolverSettingsData,
        SolverSettingsSnapshot SolverSettingsSnapshot,
        PreCombatEligibilityResult Eligibility,
        bool HasLiveSidecar,
        int LiveActFloor,
        int NextRoomId,
        Sts2SimSpoilsMapVerificationDto? SpoilsMapVerification,
        IReadOnlyList<(int Slot, string PotionId)> EffectivePotions,
        string GameRoot,
        byte[]? SerializedPlanningRun = null)
    {
        public static PreCombatLiveStateSnapshot Capture(RunState run)
        {
            PreCombatEligibilityResult eligibility = run.MandatoryExclusionCode is { Length: > 0 } code
                ? PreCombatEligibilityResult.Ineligible(code, $"Excluded by {code}.", code)
                : PreCombatSts2SimEligibility.HasUnsupportedExtraFields(run.ExtraFields)
                    ? PreCombatEligibilityResult.Ineligible(
                        "extra_field_unsupported",
                        "Run or player extra fields contain state outside the bridge cohort.",
                        "extra_field_unsupported")
                    : PreCombatEligibilityResult.Eligible("eligible");
            return new PreCombatLiveStateSnapshot(
                run,
                Encoding.UTF8.GetBytes("{\"schema_version\":21,\"game_mode\":\"standard\"}"),
                run.Token,
                new SolverSettingsData(),
                run.SettingsOverride ?? new SolverSettingsSnapshot(),
                eligibility,
                HasLiveSidecar: true,
                LiveActFloor: run.ActFloor,
                NextRoomId: 1,
                SpoilsMapVerification: null,
                EffectivePotions: Array.Empty<(int, string)>(),
                GameRoot: AppContext.BaseDirectory);
        }

        public static string CaptureToken(RunState run) => run.Token;
        public static string? TryDescribeDirectIneligibility(RunState run) => run.DirectIneligibility;
        public PreCombatLiveStateSnapshot WithPlanningRun(
            SerializableRun _, RunState __, int _targetActFloor, int _targetMapColumn, bool _isSecondBoss) => this;
    }

    internal static class PreCombatSts2SimPolicyMapper
    {
        public static string? NextUnsupportedPolicyReason { get; set; }

        public static bool TryBuildPolicy(
            SolverSettingsData settingsData,
            SolverSettingsSnapshot settingsSnapshot,
            int searchBudgetMilliseconds,
            int maxDegreeOfParallelism,
            IReadOnlyList<(int Slot, string PotionId)> effectivePotions,
            Sts2SimBridgeCapabilitiesDto capabilities,
            out Sts2SimPolicyDto? policy,
            out string? unsupportedReason)
        {
            if (NextUnsupportedPolicyReason is { } reason)
            {
                policy = null;
                unsupportedReason = reason;
                return false;
            }
            int effectiveSoftTimeMs = Math.Clamp(searchBudgetMilliseconds, 1_000, 30_000);
            var input = new Sts2SimPortfolioPolicyInput(
                UseBeamWidthPortfolio: settingsSnapshot.UseBeamWidthPortfolio,
                UseNoveltyPortfolio: settingsSnapshot.UseNoveltyPortfolio,
                EarlyTurnExplorationDepth: settingsSnapshot.UseEarlyTurnExploration ? 2 : 0,
                EarlyTurnExplorationBudgetMilliseconds: settingsSnapshot.UseEarlyTurnExploration ? effectiveSoftTimeMs : 0,
                BeamWidthPortfolioWidths: null,
                BeamWidthPortfolioPlainBaselineMember: true,
                ReallocatedRefinementPortfolio: settingsSnapshot.ReallocatedRefinementPortfolio,
                OffensiveRefinementPortfolio: settingsSnapshot.OffensiveRefinementPortfolio,
                BoundedOffensiveRefinementPortfolio: settingsSnapshot.BoundedOffensiveRefinementPortfolio,
                StopPortfolioAtHpTarget: settingsSnapshot.StopPortfolioAtHpTarget,
                AdaptiveNoveltyRefinement: settingsSnapshot.AdaptiveNoveltyRefinement,
                NoveltySearch: null,
                NoveltyBudget: settingsSnapshot.UseNoveltyPortfolio || settingsSnapshot.AdaptiveNoveltyRefinement
                    ? new Sts2SimNoveltyPortfolioBudgetDto(2, 5_000, 2_500)
                    : null,
                MaxDegreeOfParallelism: Math.Max(1, maxDegreeOfParallelism));
            if (!Sts2SimPortfolioPolicyBridge.TryResolvePortfolioPolicy(
                    in input,
                    capabilities,
                    out Sts2SimPortfolioPolicyFields resolved,
                    out unsupportedReason))
            {
                policy = null;
                return false;
            }
            policy = new Sts2SimPolicyDto(
                "Smart",
                Array.Empty<Sts2SimPotionDirectiveDto>(),
                true,
                true,
                Array.Empty<Sts2SimRelicCounterRuleDto>(),
                new Sts2SimGrowthBudgetsDto(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null),
                null,
                false,
                "PreserveHp",
                "MaximizeWinRate",
                0,
                false,
                settingsSnapshot.UseBeamWidthPortfolio,
                settingsSnapshot.UseNoveltyPortfolio,
                settingsSnapshot.UseEarlyTurnExploration,
                16,
                1200,
                8,
                6,
                6,
                effectiveSoftTimeMs,
                resolved.PortfolioOptionsVersion,
                resolved.BeamWidthPortfolioWidths,
                resolved.BeamWidthPortfolioPlainBaselineMember,
                resolved.ReallocatedRefinementPortfolio,
                resolved.OffensiveRefinementPortfolio,
                resolved.BoundedOffensiveRefinementPortfolio,
                resolved.StopPortfolioAtHpTarget,
                resolved.AdaptiveNoveltyRefinement,
                resolved.NoveltySearch,
                resolved.NoveltyBudget,
                resolved.EarlyTurnExplorationDepth,
                resolved.EarlyTurnExplorationBudgetMilliseconds,
                resolved.PortfolioSchedulingMode);
            PreCombatSts2SimWireValidator.ValidatePolicy(policy, capabilities);
            unsupportedReason = null;
            return true;
        }

        public static PreCombatForecastResult LocalizeResultOnDelivery(PreCombatForecastResult result) => result;
    }

    internal static partial class PreCombatForecastWorker
    {
        internal sealed class FakeLegacyProcess(int id)
        {
            public int Id { get; } = id;
            public bool HasExited { get; set; }
            public long WorkingSet64 => 1024L * 1024L;
            public long PrivateMemorySize64 => 2L * 1024L * 1024L;
            public long PeakWorkingSet64 => 2L * 1024L * 1024L;
            public void Refresh() { }
        }

        internal sealed record WorkerSession(FakeLegacyProcess Process, bool AudioMuted = true);

        internal sealed record LegacyCall(
            string RequestId,
            PreCombatLiveStateSnapshot Snapshot,
            string EncounterId,
            bool BusyUponEntry,
            bool InProcessClearedBeforeLegacy,
            CancellationToken RequestCancellationToken,
            CancellationToken TimeoutToken,
            CancellationToken DeadlineToken);

        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static readonly object RequestCancellationSync = new();
        private static CancellationTokenSource? _activeRequestCancellation;
        private static bool _shuttingDown;
        private static WorkerSession? _session;
        private static int _workerStartCount;
        private static int _workerReuseCount;
        private static int _workerBusy;
        private static int _nextLegacyPid = 9000;

        internal static bool ShuttingDownForTesting { get => _shuttingDown; set => _shuttingDown = value; }

        private static PreCombatWorkerStatus StoppedStatus(bool busy) => new()
        {
            IsRunning = false,
            IsBusy = busy,
            Backend = PreCombatBackendKind.None,
            AudioMuted = false,
            IdleTimeoutMilliseconds = GetIdleTimeoutMilliseconds(),
        };

        internal static Func<LegacyCall, Task<PreCombatForecastResult>>? LegacyHandler { get; set; }
        internal static ConcurrentQueue<LegacyCall> LegacyCalls { get; } = new();
        internal static ConcurrentQueue<int> StoppedLegacyPids { get; } = new();

        internal static void ResetForRoutingTests()
        {
            CancelIdleShutdown();
            StopCurrentSession();
            StopCurrentInProcessSession();
            LegacyHandler = null;
            LegacyCalls.Clear();
            StoppedLegacyPids.Clear();
            PreCombatSts2SimPolicyMapper.NextUnsupportedPolicyReason = null;
            PreCombatSts2SimBridge.ResetForTesting();
            PreCombatSts2SimBridge.ModDirectoryOverrideForTesting = null;
            SetIdleWorkerLifetime(PreCombatForecastApi.DefaultWorkerIdleTimeoutMilliseconds);
            Interlocked.Exchange(ref _inProcessStartCount, 0);
            Interlocked.Exchange(ref _inProcessReuseCount, 0);
            Interlocked.Exchange(ref _fallbackCount, 0);
            Interlocked.Exchange(ref _workerStartCount, 0);
            Interlocked.Exchange(ref _workerReuseCount, 0);
            Interlocked.Exchange(ref _workerBusy, 0);
            ResetBackendDisplayState();
        }

        private static void StopCurrentSession()
        {
            if (Interlocked.Exchange(ref _session, null) is { } session)
            {
                session.Process.HasExited = true;
                StoppedLegacyPids.Enqueue(session.Process.Id);
            }
        }

        private static string RuntimeRoot(string gameRoot) => Path.Combine(gameRoot, ".routing-test");
        private static void EnsureRuntimeRoot(string runtimeRoot, string gameRoot) { }
        private static string BuildSessionSignature(PreCombatLiveStateSnapshot snapshot) => "sig";

        private static WorkerSession CreateSession(
            PreCombatLiveStateSnapshot snapshot,
            string runtimeRoot,
            string sessionSignature,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WorkerSession session = new(new FakeLegacyProcess(Interlocked.Increment(ref _nextLegacyPid)));
            Volatile.Write(ref _session, session);
            Interlocked.Increment(ref _workerStartCount);
            return session;
        }

        private static async Task<PreCombatForecastResult> RunLegacyLockedAsync(
            string requestId,
            PreCombatLiveStateSnapshot snapshot,
            string encounterId,
            int targetActFloor,
            int targetMapColumn,
            PreCombatRoomKind roomKind,
            PreCombatMapPointKind mapPointKind,
            bool isSecondBoss,
            PreCombatForecastOptions options,
            CancellationTokenSource requestCancellation,
            CancellationToken timeoutToken,
            CancellationToken deadlineToken)
        {
            var call = new LegacyCall(
                requestId,
                snapshot,
                encounterId,
                BusyUponEntry: Volatile.Read(ref _workerBusy) == 1,
                InProcessClearedBeforeLegacy: Volatile.Read(ref _inProcessSession) is null,
                RequestCancellationToken: requestCancellation.Token,
                TimeoutToken: timeoutToken,
                DeadlineToken: deadlineToken);
            LegacyCalls.Enqueue(call);
            try
            {
                if (LegacyHandler is { } handler)
                    return await handler(call).ConfigureAwait(false);

                WorkerSession session = Volatile.Read(ref _session)
                    ?? CreateSession(snapshot, RuntimeRoot(snapshot.GameRoot), "sig", deadlineToken);
                if (!options.CloseWorkerAfterRequest)
                    ScheduleIdleShutdown(session);
                return new PreCombatForecastResult
                {
                    Status = PreCombatForecastStatus.Succeeded,
                    RequestId = requestId,
                    ProjectedHpLoss = 11,
                    FinalHp = 69,
                    CombatEndedTurn = 3,
                    Confidence = PreCombatForecastConfidence.Complete,
                    SearchBoundary = "None",
                    ProducingBackend = PreCombatBackendKind.LegacyProcess,
                };
            }
            catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
            {
                StopCurrentSession();
                return PreCombatForecastApi.Failure(PreCombatForecastStatus.Cancelled, requestId, "Legacy cancelled.");
            }
            catch (OperationCanceledException) when (timeoutToken.IsCancellationRequested)
            {
                StopCurrentSession();
                return PreCombatForecastApi.Failure(PreCombatForecastStatus.TimedOut, requestId, "Legacy timed out.");
            }
            catch (OperationCanceledException ex)
            {
                StopCurrentSession();
                return PreCombatForecastApi.Failure(PreCombatForecastStatus.Failed, requestId, ex.GetBaseException().Message);
            }
        }
    }
}
