// Only game/process boundaries are replaced. Request selection and completion use the production API.
using System.Threading.Channels;
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
        public int CurrentActIndex;
        public int ActFloor = 1;
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
    public static class Entry
    {
        public static bool Enabled { get; set; } = true;
        public static bool IsPreCombatWorker { get; set; }
        public static TestLogger Logger = new();
    }
    public sealed class TestLogger
    {
        public bool ThrowOnError { get; set; } = true;
        public List<string> Errors { get; } = [];
        public void Info(string message) { }
        public void Error(string message)
        {
            Errors.Add(message);
            if (ThrowOnError)
                throw new Exception(message);
        }
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
        string Fingerprint);
    internal static class PreCombatSts2SimEligibility
    {
        public static PreCombatEligibilityResult AssessMandatoryLiveHost(RunState run) =>
            run.DirectIneligibility is { Length: > 0 } code
                ? new PreCombatEligibilityResult(false, code, $"Direct ineligibility: {code}.", code)
                : new PreCombatEligibilityResult(true, null, null, "stub");
    }
    internal sealed record PreCombatLiveStateSnapshot(RunState LiveRun, string StateToken)
    {
        public static PreCombatLiveStateSnapshot Capture(RunState run) => new(run, run.Token);
        public static string CaptureToken(RunState run) => run.Token;
        public static string? TryDescribeDirectIneligibility(RunState run) => run.DirectIneligibility;
        public PreCombatLiveStateSnapshot WithPlanningRun(
            SerializableRun _, RunState __, int _targetActFloor, int _targetMapColumn, bool _isSecondBoss) => this;
    }
    internal static partial class PreCombatForecastWorker
    {
        internal sealed record Work(
            PreCombatForecastOptions Options,
            long AdmissionEpoch,
            CancellationToken Cancellation)
        {
            public TaskCompletionSource<PreCombatForecastResult> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public void Succeed(string id, params PreCombatPotionUse[] potions) =>
                SucceedWithBackend(id, PreCombatBackendKind.LegacyProcess, potions);
            public void SucceedWithBackend(string id, PreCombatBackendKind backend, params PreCombatPotionUse[] potions) =>
                Completion.TrySetResult(new()
                {
                    Status = PreCombatForecastStatus.Succeeded,
                    RequestId = id,
                    PotionUses = potions,
                    ProducingBackend = backend,
                });
            public void Cancel() => Completion.TrySetResult(new()
            {
                Status = PreCombatForecastStatus.Cancelled,
                RequestId = idOrEmpty(),
                Error = "Cancelled",
            });
            private static string idOrEmpty() => "cancelled";
        }

        public static bool InstalledInProcessBackend { get; set; }
        public static bool HasInstalledInProcessBackend => InstalledInProcessBackend;
        public static string LocalePrefix { get; set; } = "EN";
        public static Channel<Work> Started = Channel.CreateUnbounded<Work>();
        public static Channel<long> RestartEpochs = Channel.CreateUnbounded<long>();
        public static List<(PreCombatBackendKind Backend, bool Close, int? Idle)> AppliedLifetimes = [];
        public static long StopEpochValue;
        public static TaskCompletionSource<bool>? DelayStopAfterEpochBump { get; set; }

        public static long CaptureAdmissionEpoch() => Volatile.Read(ref StopEpochValue);

        public static Task<PreCombatForecastResult> RunAsync(
            PreCombatLiveStateSnapshot snapshot, string encounterId, int floor, int column,
            PreCombatRoomKind room, PreCombatMapPointKind point, bool isSecondBoss,
            PreCombatForecastOptions options, long admissionEpoch, CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref StopEpochValue) != admissionEpoch)
            {
                return Task.FromResult(new PreCombatForecastResult
                {
                    Status = PreCombatForecastStatus.Cancelled,
                    RequestId = "stopped-before-start",
                    Error = "Stopped before starting.",
                });
            }
            var work = new Work(options, admissionEpoch, cancellationToken);
            Started.Writer.TryWrite(work);
            return work.Completion.Task;
        }

        public static Task ApplyCachedRequestLifetimeAsync(
            PreCombatBackendKind backend,
            bool close,
            int? idle,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            AppliedLifetimes.Add((backend, close, idle));
            return Task.CompletedTask;
        }
        public static Task ConfigureIdleTimeoutAsync(int? idle) => Task.CompletedTask;
        public static async Task StopSessionAsync()
        {
            Interlocked.Increment(ref StopEpochValue);
            if (DelayStopAfterEpochBump is { } gate)
                await gate.Task;
        }
        public static PreCombatWorkerStatus GetStatus() => new() { IsRunning = false, IsBusy = false };
        public static PreCombatBackendDisplaySnapshot CaptureBackendDisplaySnapshot(RunState? liveRun)
        {
            PreCombatEligibilityResult? eligibility = liveRun is null ? null : PreCombatSts2SimEligibility.AssessMandatoryLiveHost(liveRun);
            return new PreCombatBackendDisplaySnapshot(
                PreCombatBackendKind.None,
                IsActiveRunning: false,
                IsBusy: false,
                PreCombatBackendKind.None,
                LastEffectiveParallelism: null,
                LastFallbackCode: null,
                HasInstalledInProcessBackend: HasInstalledInProcessBackend,
                IsBridgeCorrupt: PreCombatSts2SimBridge.IsCorrupt,
                HostEligibleForInProcess: eligibility is { IsEligible: true },
                HostExclusionCode: eligibility?.FailureCode,
                IsWindowsHost: OperatingSystem.IsWindows(),
                HostExclusionDetail: eligibility?.Detail);
        }
        public static Task<PreCombatWorkerStatus> RestartSessionAsync(
            PreCombatLiveStateSnapshot snapshot, int? idle, long admissionEpoch, CancellationToken token)
        {
            RestartEpochs.Writer.TryWrite(admissionEpoch);
            if (Volatile.Read(ref StopEpochValue) != admissionEpoch)
                throw new OperationCanceledException("Restart stopped before starting.");
            return Task.FromResult(GetStatus());
        }
        public static PreCombatForecastResult LocalizeResultForDelivery(PreCombatForecastResult result)
        {
            if (result.Status != PreCombatForecastStatus.Succeeded || result.PotionUses.Count == 0)
                return result;
            return result with
            {
                PotionUses = result.PotionUses
                    .Select(use => use with { Title = $"{LocalePrefix}:{use.Id}" })
                    .ToArray(),
            };
        }
    }
}
