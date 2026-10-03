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
    public sealed class SerializableRun { public SerializableRng SerializableRng = new(); }
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
        public static bool Enabled = true;
        public static bool IsPreCombatWorker { get; set; }
        public static TestLogger Logger = new();
    }
    public sealed class TestLogger { public void Error(string message) => throw new Exception(message); }
    public static class SolverDispatcher { public static void Post(Action action) => action(); }
}
namespace CombatSolver.Api
{
    internal static class OperatingSystem { public static bool IsWindows() => true; }
    internal sealed record PreCombatLiveStateSnapshot(RunState LiveRun, string StateToken)
    {
        public static PreCombatLiveStateSnapshot Capture(RunState run) => new(run, run.Token);
        public static string CaptureToken(RunState run) => run.Token;
        public PreCombatLiveStateSnapshot WithPlanningRun(SerializableRun _) => this;
    }
    internal static class PreCombatForecastWorker
    {
        internal sealed record Work(PreCombatForecastOptions Options, CancellationToken Cancellation)
        {
            public TaskCompletionSource<PreCombatForecastResult> Completion = new();
            public void Succeed(string id) => Completion.SetResult(new()
            {
                Status = PreCombatForecastStatus.Succeeded, RequestId = id,
            });
        }
        public static Channel<Work> Started = Channel.CreateUnbounded<Work>();
        public static List<(bool Close, int? Idle)> AppliedLifetimes = [];
        public static Task<PreCombatForecastResult> RunAsync(
            PreCombatLiveStateSnapshot snapshot, string encounterId, int floor, int column,
            PreCombatRoomKind room, PreCombatMapPointKind point, bool isSecondBoss,
            PreCombatForecastOptions options, CancellationToken cancellationToken)
        {
            var work = new Work(options, cancellationToken);
            Started.Writer.TryWrite(work);
            return work.Completion.Task;
        }
        public static Task ApplyCachedRequestLifetimeAsync(bool close, int? idle, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            AppliedLifetimes.Add((close, idle));
            return Task.CompletedTask;
        }
        public static Task ConfigureIdleTimeoutAsync(int? idle) => Task.CompletedTask;
        public static Task StopSessionAsync() => Task.CompletedTask;
        public static PreCombatWorkerStatus GetStatus() => new() { IsRunning = false, IsBusy = false };
        public static Task<PreCombatWorkerStatus> RestartSessionAsync(
            PreCombatLiveStateSnapshot snapshot, int? idle, CancellationToken token) => Task.FromResult(GetStatus());
    }
}
