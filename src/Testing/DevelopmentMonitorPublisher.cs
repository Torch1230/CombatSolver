using System.Text.Json.Nodes;

namespace CombatSolver;

/// <summary>Copies scalar progress for the out-of-process development window at most once per second.</summary>
internal sealed class DevelopmentMonitorPublisher : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    private DevelopmentMonitorPublisher(UnattendedTestRequest request)
    {
        _loop = Task.Run(async () =>
        {
            using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
            try
            {
                do
                {
                    SolverProgress? progress = SolverController.CaptureDevelopmentMonitorProgress();
                    SolverInterimResult? best = progress?.CurrentBestResult;
                    JsonObject snapshot = new()
                    {
                        ["schemaVersion"] = 1,
                        ["state"] = progress == null ? "preparing" : "searching",
                        ["reportId"] = Path.GetFileNameWithoutExtension(request.CheckpointArchivePath ?? ""),
                        ["runId"] = request.RunId,
                        ["processId"] = Environment.ProcessId,
                        ["scriptHash"] = request.DevelopmentStrategyScriptHash,
                        ["parametersHash"] = request.DevelopmentStrategyParametersHash,
                        ["performancePreset"] = request.PerformancePresetForTest?.ToString(),
                        ["parallelism"] = request.SearchMaxDegreeOfParallelismForTest,
                        ["requestTimeoutSeconds"] = request.TimeoutSeconds,
                        ["phase"] = progress?.Phase,
                        ["elapsedMilliseconds"] = progress?.ElapsedMilliseconds,
                        ["remainingMilliseconds"] = progress == null ? null
                            : Math.Max(0, progress.RequestBudgetMilliseconds - progress.ElapsedMilliseconds),
                        ["expandedNodes"] = progress?.ExpandedNodes,
                        ["reviewedWorldlines"] = progress?.ReviewedWorldlines,
                        ["worldlinesPerSecond"] = progress is { ElapsedMilliseconds: > 0 }
                            ? Math.Round(progress.ReviewedWorldlines * 1000d / progress.ElapsedMilliseconds, 1) : null,
                        ["frontierNodes"] = progress?.FrontierNodes,
                        ["bestProjectedHpLoss"] = best?.ProjectedBattleHpLost,
                        ["bestPotionCount"] = best?.ProjectedBattlePotionCount,
                        ["updatedUtc"] = DateTimeOffset.UtcNow,
                    };
                    string path = request.DevelopmentMonitorStatePath!;
                    string temp = path + ".game.tmp";
                    File.WriteAllText(temp, snapshot.ToJsonString());
                    File.Move(temp, path, true);
                } while (await timer.WaitForNextTickAsync(_stop.Token));
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (IOException error)
            {
                Entry.Logger.Error($"[CombatSolver/Unattended] MONITOR_WRITE_FAILED {error}");
            }
        });
    }

    public static DevelopmentMonitorPublisher? Start(UnattendedTestRequest request)
        => request.DevelopmentMonitorStatePath == null ? null : new(request);

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _loop;
        _stop.Dispose();
    }
}
