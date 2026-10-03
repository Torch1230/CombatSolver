using System.Text.Json;
using CombatSolver;

namespace OfflineSearchHarness;

// OFFLINE_HARNESS_ANCILLARY_CHECKS=1：在一次真实求解结果上注入路线缓存的磁盘故障，
// 确认每种故障都只表现为缓存未命中或未写入，结果对象不受影响。
internal static class AncillaryFailureChecks
{
    internal static void Run(SolverResult result, string output)
    {
        string directory = Path.Combine(output, "ancillary-checks");
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(directory);
        int assertions = 0;
        void Check(bool condition, string name)
        {
            if (!condition)
                throw new InvalidOperationException(name);
            assertions++;
            Console.WriteLine($"ANCILLARY_CHECK Passed {name}");
        }

        byte[] original = SolvedRouteCache.SerializeRoute(result);

        string healthy = Path.Combine(directory, "healthy.json");
        new SolvedRouteCache(healthy).StoreFirst(result);
        Check(new SolvedRouteCache(healthy).Read(result.Forecast) is { WasRestoredFromCache: true }, "healthy_roundtrip");
        Check(!File.Exists(healthy + ".tmp"), "temporary_file_removed");

        string bad = Path.Combine(directory, "bad.json");
        File.WriteAllText(bad, "{broken");
        Check(new SolvedRouteCache(bad).Read(result.Forecast) == null, "bad_json_miss");
        Check(!File.Exists(bad) && File.Exists(bad + ".bad"), "bad_json_quarantined");
        new SolvedRouteCache(bad).StoreFirst(result);
        Check(new SolvedRouteCache(bad).Read(result.Forecast) != null, "bad_json_slot_reusable");

        string empty = Path.Combine(directory, "empty.json");
        File.WriteAllText(empty, "null");
        Check(new SolvedRouteCache(empty).Read(result.Forecast) == null, "empty_route_miss");
        Check(File.Exists(empty + ".bad"), "empty_route_quarantined");

        string locked = Path.Combine(directory, "locked.json");
        File.WriteAllBytes(locked, original);
        using (FileStream held = new(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check(new SolvedRouteCache(locked).Read(result.Forecast) == null, "locked_file_miss");
        Check(File.Exists(locked) && !File.Exists(locked + ".bad"), "locked_file_not_quarantined");
        Check(new SolvedRouteCache(locked).Read(result.Forecast) != null, "locked_file_recovers");

        string blockedDirectory = Path.Combine(directory, "blocked-directory");
        File.WriteAllText(blockedDirectory, "a file where the cache directory should be");
        new SolvedRouteCache(Path.Combine(blockedDirectory, "route.json")).StoreFirst(result);
        Check(File.Exists(blockedDirectory), "directory_unavailable_store_skipped");

        if (!OperatingSystem.IsWindows())
        {
            string readOnly = Path.Combine(directory, "readonly");
            Directory.CreateDirectory(readOnly);
            File.SetUnixFileMode(readOnly, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                new SolvedRouteCache(Path.Combine(readOnly, "route.json")).StoreFirst(result);
                Check(!File.Exists(Path.Combine(readOnly, "route.json")), "read_only_directory_store_skipped");
            }
            finally
            {
                File.SetUnixFileMode(readOnly,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        string occupied = Path.Combine(directory, "occupied.json");
        Directory.CreateDirectory(occupied);
        new SolvedRouteCache(occupied).StoreFirst(result);
        Check(Directory.Exists(occupied), "destination_occupied_store_skipped");
        Check(new SolvedRouteCache(occupied).Read(result.Forecast) == null, "destination_occupied_miss");

        Check(original.SequenceEqual(SolvedRouteCache.SerializeRoute(result)), "result_unchanged");

        List<string> logs = [];
        try
        {
            AncillaryWork.Run("cancel", () => throw new OperationCanceledException(), logs.Add);
            throw new InvalidOperationException("cancellation_swallowed");
        }
        catch (OperationCanceledException)
        {
            Check(logs.Count == 0, "cancellation_propagates");
        }
        try
        {
            AncillaryWork.Run("programming", () => throw new InvalidOperationException("injected"), logs.Add);
            throw new Exception("programming_error_swallowed");
        }
        catch (InvalidOperationException error) when (error.Message == "injected")
        {
            Check(logs.Count == 0, "programming_error_propagates");
        }
        AncillaryWork.Run("failure", () => throw new IOException("injected"), logs.Add);
        Check(logs.Count == 1 && logs[0].StartsWith("failure ", StringComparison.Ordinal), "failure_logged_once");

        File.WriteAllText(Path.Combine(directory, "checks.json"),
            JsonSerializer.Serialize(new { assertions, status = "Passed" }));
    }
}
