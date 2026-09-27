using System.Diagnostics;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CombatSolver.Replay;

internal static class StrategySessionRunner
{
    private const int DefaultTimeoutSeconds = 180;
    private const string FixedInstance = "strategy-development";

    public static async Task<int> Run(string[] args)
    {
        if (args.Length < 2 || args[0] is not ("start" or "run" or "status" or "stop"))
            throw new ArgumentException("session start|run|status|stop NAME [ARCHIVE] [--options]");
        string command = args[0], name = args[1];
        if (name.Length is < 1 or > 40 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ArgumentException("Session name must use 1..40 ASCII letters, digits, '-' or '_'.");
        string project = FindProject();
        string session = Path.Combine(project, ".local", "strategy-sessions", name);
        string instance = FixedInstance;
        string ownerPath = Path.Combine(project, ".local", "strategy-sessions", "active-session.json");
        int index = 2;
        string? archive = command == "run" && index < args.Length && !args[index].StartsWith("--", StringComparison.Ordinal)
            ? Path.GetFullPath(args[index++]) : null;
        if (command == "run" && archive == null)
            throw new ArgumentException("session run requires an archive path.");
        Dictionary<string, string> options = new(StringComparer.Ordinal);
        for (; index < args.Length; index += 2)
        {
            if (args[index] == "--no-monitor")
            {
                options.Add("--no-monitor", "true");
                index--;
                continue;
            }
            if (index + 1 == args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Session options require --name VALUE pairs.");
            options.Add(args[index], args[index + 1]);
        }
        string[] allowed = command switch
        {
            "start" => ["--game-root", "--ritsu-root", "--no-monitor", "--host-memory-mib"],
            "run" => ["--script", "--params", "--policy", "--selector", "--early-turns", "--deadline-seconds"],
            _ => [],
        };
        foreach (string option in options.Keys)
            if (!allowed.Contains(option)) throw new ArgumentException("Unknown session option: " + option);
        using FileStream sessionLock = LockSession(session);
        using FileStream? fixedLock = command is "start" or "stop" ? LockFixedInstance(project) : null;
        JsonObject? owner = Read(ownerPath);
        if (owner?["name"]?.ToString() is { } activeName
            && activeName != name && IsRunning(project, instance, out _))
            throw new InvalidOperationException($"Fixed strategy instance belongs to active session {activeName}; stop it first.");
        if (command == "start")
        {
            JsonObject? existing = Read(Path.Combine(session, "session.json"));
            if (existing?["active"]?.GetValue<bool>() == true && IsRunning(project, instance, out int priorPid))
            {
                if (existing["monitorEnabled"]?.GetValue<bool>() == true)
                    await StartMonitor(project, session);
                Console.WriteLine($"SESSION_START name={name} status=already_running pid={priorPid}");
                return 0;
            }
            if (OperatingSystem.IsWindows() && !options.ContainsKey("--game-root")
                && existing?["gameRoot"] == null)
                throw new ArgumentException("Windows session start requires --game-root.");
            if (existing != null && IsRunning(project, instance, out _))
                throw new InvalidOperationException("Session process still runs; stop it first.");
            int hostMemoryMiB = options.TryGetValue("--host-memory-mib", out string? requestedMemory)
                ? int.Parse(requestedMemory)
                : existing?["hostMemoryMiB"]?.GetValue<int>() ?? 4096;
            if (hostMemoryMiB < 1024)
                throw new ArgumentOutOfRangeException(nameof(hostMemoryMiB), "Host reservation must be at least 1024 MiB.");
            Directory.CreateDirectory(session);
            JsonObject state = new()
            {
                ["instance"] = instance,
                ["gameRoot"] = options.TryGetValue("--game-root", out string? game) ? Path.GetFullPath(game) : existing?["gameRoot"]?.ToString(),
                ["ritsuRoot"] = options.TryGetValue("--ritsu-root", out string? ritsu) ? Path.GetFullPath(ritsu) : existing?["ritsuRoot"]?.ToString(),
                ["createdUtc"] = DateTimeOffset.UtcNow,
                ["active"] = false,
                ["monitorEnabled"] = !options.ContainsKey("--no-monitor"),
                ["hostMemoryMiB"] = hostMemoryMiB,
            };
            Save(Path.Combine(session, "session.json"), state);
            string evidence = NewEvidence(session, "start");
            Stopwatch startup = Stopwatch.StartNew();
            if (state["monitorEnabled"]?.GetValue<bool>() == true)
            {
                SaveMonitorState(session, new JsonObject
                {
                    ["state"] = "准备实例", ["phase"] = "启动固定游戏实例",
                    ["updatedUtc"] = DateTimeOffset.UtcNow,
                });
                await StartMonitor(project, session);
                Console.WriteLine($"SESSION_MONITOR_READY name={name} elapsed_ms={startup.Elapsed.TotalMilliseconds:F0}");
            }
            int exit = await BatchRunner.Launch(project, LauncherOptions(state), evidence,
                DefaultTimeoutSeconds, null, "start", "SessionStart", null, stop: false);
            JsonObject? result = Read(Path.Combine(evidence, "result.json"));
            bool running = IsRunning(project, instance, out int pid);
            bool passed = exit == 0 && result?["status"]?.ToString() == "Passed" && running;
            Save(Path.Combine(evidence, "session-result.json"), new JsonObject
            {
                ["status"] = passed ? "started" : "start_failed",
                ["exitCode"] = exit, ["processId"] = pid, ["wallMilliseconds"] = startup.Elapsed.TotalMilliseconds,
                ["result"] = result?.DeepClone(),
            });
            Console.WriteLine($"SESSION_START name={name} status={(passed ? "started" : "start_failed")} pid={pid} evidence={evidence}");
            if (!passed)
            {
                await BatchRunner.Launch(project, LauncherOptions(state), evidence,
                    DefaultTimeoutSeconds, null, "start", "RestoreOnly", null, stop: true);
                await StopMonitor(session);
                state["active"] = false;
                Save(Path.Combine(session, "session.json"), state);
                return 1;
            }
            state["active"] = true;
            Save(Path.Combine(session, "session.json"), state);
            Save(ownerPath, new JsonObject { ["name"] = name, ["instance"] = instance });
            if (state["monitorEnabled"]?.GetValue<bool>() == true)
            {
                SaveMonitorState(session, new JsonObject
                {
                    ["state"] = "idle", ["processId"] = pid,
                    ["phase"] = "等待问题包", ["updatedUtc"] = DateTimeOffset.UtcNow,
                });
            }
            return 0;
        }
        JsonObject saved = Read(Path.Combine(session, "session.json"))
            ?? throw new DirectoryNotFoundException("Unknown strategy session: " + name);
        if (saved["instance"]?.ToString() != instance)
            throw new InvalidDataException("Session identity mismatch.");
        if (command == "status")
        {
            bool running = IsRunning(project, instance, out int pid);
            string status = saved["active"]?.GetValue<bool>() == true
                ? running ? "running" : "needs_restart" : "stopped";
            Console.WriteLine($"SESSION_STATUS name={name} state={status} pid={pid}");
            Console.WriteLine($"SESSION_MONITOR state={(IsMonitorRunning(session, out _) ? "open" : "closed")}");
            return running ? 0 : 1;
        }
        if (command == "stop")
        {
            if (saved["active"]?.GetValue<bool>() == false
                && !IsRunning(project, instance, out _)
                && !IsMonitorRunning(session, out _))
            {
                if (Read(ownerPath)?["name"]?.ToString() == name)
                    File.Delete(ownerPath);
                Console.WriteLine($"SESSION_STOP name={name} status=stopped pid=0");
                return 0;
            }
            string evidence = NewEvidence(session, "stop");
            bool wasRunning = IsRunning(project, instance, out _);
            int exit = await BatchRunner.Launch(project, LauncherOptions(saved), evidence,
                DefaultTimeoutSeconds, null, "start", "RestoreOnly", null, stop: true);
            bool running = IsRunning(project, instance, out int pid);
            if (!running && (exit == 0 || !wasRunning))
            {
                await StopMonitor(session);
                saved["active"] = false;
                Save(Path.Combine(session, "session.json"), saved);
                if (Read(ownerPath)?["name"]?.ToString() == name)
                    File.Delete(ownerPath);
            }
            bool stopped = !running && saved["active"]?.GetValue<bool>() == false;
            Console.WriteLine($"SESSION_STOP name={name} status={(stopped ? "stopped" : "failed")} pid={pid}");
            return stopped ? 0 : 1;
        }
        if (saved["active"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("Session is stopped; start it before running an archive.");
        return await Search(project, session, saved, archive!, options);
    }

    private static async Task<int> Search(string project, string session,
        JsonObject state, string archive, Dictionary<string, string> options)
    {
        string evidence = NewEvidence(session, "run");
        string selector = options.GetValueOrDefault("--selector", CheckpointArchive.DefaultFixtureSelector);
        int earlyTurnDepth = options.TryGetValue("--early-turns", out string? requestedDepth)
            ? int.Parse(requestedDepth) : 0;
        if (earlyTurnDepth is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(earlyTurnDepth),
                "Early turn exploration depth must be 1 or 2.");
        if (options.ContainsKey("--deadline-seconds") && earlyTurnDepth == 0)
            throw new ArgumentException("--deadline-seconds requires --early-turns.");
        int timeoutSeconds = options.TryGetValue("--deadline-seconds", out string? requestedDeadline)
            ? int.Parse(requestedDeadline)
            : earlyTurnDepth == 0 ? DefaultTimeoutSeconds : 2400;
        if (earlyTurnDepth > 0 && timeoutSeconds is < 15 or > 2400)
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds),
                "Early turn exploration deadline must be 15..2400 seconds.");
        const string mode = "SearchOnly";
        Stopwatch watch = Stopwatch.StartNew();
        bool monitorEnabled = state["monitorEnabled"]?.GetValue<bool>() == true;
        string? monitorPath = monitorEnabled ? Path.Combine(session, "monitor-state.json") : null;
        JsonObject row = new()
        {
            ["archivePath"] = archive, ["selector"] = selector, ["mode"] = mode,
            ["timeoutSeconds"] = timeoutSeconds,
            ["earlyTurnExplorationDepth"] = earlyTurnDepth,
            ["performancePreset"] = "VeryHigh",
            ["searchMaxDegreeOfParallelism"] = 8, ["startedUtc"] = DateTimeOffset.UtcNow,
        };
        int exit = 1;
        try
        {
            string? assembly = null, scriptHash = null, parametersHash = null, parameters = null;
            string main = Path.Combine(project, ".godot", "mono", "temp", "bin", "Release", "CombatSolver.dll");
            row["mainAssemblyHash"] = HashFile(main);
            if (options.TryGetValue("--script", out string? script))
            {
                (assembly, scriptHash) = await CompileScript(session, script, main, evidence);
                string parameterText = options.TryGetValue("--params", out string? parameterFile)
                    ? File.ReadAllText(Path.GetFullPath(parameterFile)) : "{}";
                using (JsonDocument document = JsonDocument.Parse(parameterText))
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                        throw new InvalidDataException("Strategy parameters must be a JSON object.");
                parametersHash = HashBytes(Encoding.UTF8.GetBytes(parameterText));
                parameters = Path.Combine(evidence, "parameters.json");
                File.WriteAllText(parameters, parameterText, new UTF8Encoding(false));
            }
            else if (options.ContainsKey("--params"))
                throw new ArgumentException("--params requires --script.");
            row["scriptHash"] = scriptHash;
            row["parametersHash"] = parametersHash;
            if (monitorEnabled)
                SaveMonitorState(session, new JsonObject
                {
                    ["state"] = "正在提交", ["reportId"] = Path.GetFileNameWithoutExtension(archive),
                    ["processId"] = IsRunning(project, state["instance"]!.ToString(), out int runningPid)
                        ? runningPid : null,
                    ["scriptHash"] = scriptHash, ["parametersHash"] = parametersHash,
                    ["performancePreset"] = "VeryHigh", ["parallelism"] = 8,
                    ["requestTimeoutSeconds"] = timeoutSeconds,
                    ["phase"] = "常驻游戏接收问题包", ["updatedUtc"] = DateTimeOffset.UtcNow,
                });
            string? policy = null;
            if (options.TryGetValue("--policy", out string? policySource))
            {
                policy = Path.Combine(evidence, "policy.json");
                File.Copy(Path.GetFullPath(policySource), policy);
            }
            exit = await BatchRunner.Launch(project, LauncherOptions(state), evidence,
                timeoutSeconds, archive, selector, mode, policy, stop: false,
                assembly, parameters, scriptHash, parametersHash, monitorPath,
                reuseOnly: true, earlyTurnExplorationDepth: earlyTurnDepth);
            JsonObject? result = Read(Path.Combine(evidence, "result.json"));
            JsonObject? launcher = Read(Path.Combine(evidence, "launcher-result.json"));
            row["status"] = BatchRunner.Classify(result, exit, launcher);
            row["reason"] = launcher?["reason"]?.DeepClone() ?? result?["error"]?.DeepClone();
            row["processReusable"] = result?["processReusable"]?.DeepClone();
            row["processId"] = result?["processId"]?.DeepClone();
            row["reusedProcess"] = result?["reusedProcess"]?.DeepClone();
            row["resultMainAssemblyHash"] = result?["mainAssemblyHash"]?.DeepClone();
            row["resultScriptHash"] = result?["developmentStrategyScriptHash"]?.DeepClone();
            row["resultParametersHash"] = result?["developmentStrategyParametersHash"]?.DeepClone();
            row["solverMetrics"] = result?["solverMetrics"]?.DeepClone();
            row["restorationVerified"] = result?["replayVerification"]?["restorationVerified"]?.DeepClone();
            row["comparisonScope"] = result?["replayVerification"]?["comparisonScope"]?.DeepClone();
            if (result != null && row["status"]?.ToString() == "search_completed"
                && (!string.Equals(row["mainAssemblyHash"]?.ToString(), result["mainAssemblyHash"]?.ToString(), StringComparison.OrdinalIgnoreCase)
                    || row["scriptHash"]?.ToString() != result["developmentStrategyScriptHash"]?.ToString()
                    || row["parametersHash"]?.ToString() != result["developmentStrategyParametersHash"]?.ToString()))
                throw new InvalidDataException("Result strategy or main assembly identity differs from submitted inputs.");
            if (row["status"]?.ToString() == "timeout")
            {
                row["reason"] = $"exceeded_{timeoutSeconds}_seconds_package_discarded";
                int stopExit = await BatchRunner.Launch(project, LauncherOptions(state), evidence,
                    DefaultTimeoutSeconds, null, selector, mode, null, stop: true);
                row["timeoutStopExitCode"] = stopExit;
            }
            return row["status"]?.ToString() == "search_completed" ? 0 : 1;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException
            or ArgumentException or InvalidOperationException)
        {
            if (row["status"] == null)
                row["status"] = "strategy_or_input_failed";
            row["reason"] = error.ToString();
            return 1;
        }
        finally
        {
            row["wallMilliseconds"] = watch.Elapsed.TotalMilliseconds;
            row["launcherExitCode"] = exit;
            row["finishedUtc"] = DateTimeOffset.UtcNow;
            if (monitorEnabled)
                SaveMonitorState(session, new JsonObject
                {
                    ["state"] = row["status"]?.ToString() ?? "failed",
                    ["reportId"] = Path.GetFileNameWithoutExtension(archive),
                    ["processId"] = row["processId"]?.DeepClone(),
                    ["scriptHash"] = row["scriptHash"]?.DeepClone(),
                    ["parametersHash"] = row["parametersHash"]?.DeepClone(),
                    ["performancePreset"] = "VeryHigh", ["parallelism"] = 8,
                    ["requestTimeoutSeconds"] = DefaultTimeoutSeconds,
                    ["elapsedMilliseconds"] = row["wallMilliseconds"]?.DeepClone(),
                    ["expandedNodes"] = row["solverMetrics"]?["selectedExpanded"]?.DeepClone(),
                    ["bestProjectedHpLoss"] = row["solverMetrics"]?["projectedBattleHpLost"]?.DeepClone(),
                    ["bestPotionCount"] = row["solverMetrics"]?["potionCount"]?.DeepClone(),
                    ["reason"] = row["reason"]?.DeepClone(),
                    ["updatedUtc"] = DateTimeOffset.UtcNow,
                });
            Save(Path.Combine(evidence, "session-result.json"), row);
            File.AppendAllText(Path.Combine(session, "results.jsonl"), row.ToJsonString() + "\n", new UTF8Encoding(false));
            Console.WriteLine($"SESSION_RUN status={row["status"]} pid={row["processId"]} elapsed_ms={watch.Elapsed.TotalMilliseconds:F0} evidence={evidence}");
        }
    }

    private static async Task<(string Assembly, string Hash)> CompileScript(string session,
        string source, string main, string evidence)
    {
        string text = File.ReadAllText(Path.GetFullPath(source));
        string hash = HashText(text);
        string key = hash + "-" + HashFile(main)[..16];
        string directory = Path.Combine(session, "scripts", key);
        string assembly = Path.Combine(directory, "bin", "Release", "net9.0", "StrategyScript.dll");
        if (File.Exists(assembly) && File.Exists(Path.Combine(directory, "StrategyScript.cs"))
            && HashFile(Path.Combine(directory, "StrategyScript.cs")) == hash)
            return (assembly, hash);
        Directory.CreateDirectory(directory);
        string frozenSource = Path.Combine(directory, "StrategyScript.cs");
        if (File.Exists(frozenSource) && HashFile(frozenSource) != hash)
            throw new InvalidDataException("Frozen strategy source differs from its content hash.");
        if (!File.Exists(frozenSource))
            File.WriteAllText(frozenSource, text, new UTF8Encoding(false));
        string escapedMain = SecurityElement.Escape(Path.GetFullPath(main))!;
        File.WriteAllText(Path.Combine(directory, "StrategyScript.csproj"),
            $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework><LangVersion>13.0</LangVersion><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><EnableDynamicLoading>true</EnableDynamicLoading></PropertyGroup><ItemGroup><Reference Include=\"CombatSolver\"><HintPath>{escapedMain}</HintPath></Reference></ItemGroup></Project>");
        ProcessStartInfo start = new("dotnet")
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add("build");
        start.ArgumentList.Add("StrategyScript.csproj");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("Release");
        start.ArgumentList.Add("--nologo");
        using Process process = Process.Start(start) ?? throw new IOException("Strategy compiler failed to start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string log = await output + await errors;
        File.WriteAllText(Path.Combine(evidence, "script-build.log"), log);
        if (process.ExitCode != 0 || !File.Exists(assembly))
            throw new InvalidDataException("Strategy compilation failed; see script-build.log. " + log[^Math.Min(1200, log.Length)..]);
        return (assembly, hash);
    }

    private static Dictionary<string, string> LauncherOptions(JsonObject state)
    {
        Dictionary<string, string> options = new() { ["--instance"] = state["instance"]!.ToString() };
        if (state["gameRoot"] != null) options["--game-root"] = state["gameRoot"]!.ToString();
        if (state["ritsuRoot"] != null) options["--ritsu-root"] = state["ritsuRoot"]!.ToString();
        if (state["hostMemoryMiB"] != null)
            options["--headless-memory-reservation-mib"] = state["hostMemoryMiB"]!.ToString();
        return options;
    }

    private static bool IsRunning(string project, string instance, out int pid)
    {
        pid = 0;
        string marker = Path.Combine(project, ".local", "headless-instances", instance, "process.json");
        JsonObject? value = Read(marker);
        if (value == null || value["instance"]?.ToString() != instance
            || !int.TryParse(value["pid"]?.ToString(), out int recorded)
            || !DateTimeOffset.TryParse(value["processStartTimeUtc"]?.ToString(), out DateTimeOffset born))
            return false;
        try
        {
            using Process process = Process.GetProcessById(recorded);
            if (process.HasExited || Math.Abs((process.StartTime.ToUniversalTime() - born.UtcDateTime).TotalSeconds) > 1)
                return false;
            pid = recorded;
            return true;
        }
        catch (ArgumentException) { return false; }
    }

    private static string NewEvidence(string session, string kind)
    {
        string path = Path.Combine(session, "requests", DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffff") + "-" + kind);
        Directory.CreateDirectory(path);
        return path;
    }
    private static JsonObject? Read(string path) => File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : null;
    private static FileStream LockSession(string session)
    {
        Directory.CreateDirectory(session);
        return new FileStream(Path.Combine(session, "session.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }
    private static FileStream LockFixedInstance(string project)
    {
        string sessions = Path.Combine(project, ".local", "strategy-sessions");
        Directory.CreateDirectory(sessions);
        return new FileStream(Path.Combine(sessions, "fixed-instance.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }
    private static void SaveMonitorState(string session, JsonObject value)
    {
        value["schemaVersion"] = 1;
        Save(Path.Combine(session, "monitor-state.json"), value);
    }

    private static bool IsMonitorRunning(string session, out int pid)
    {
        pid = 0;
        JsonObject? marker = Read(Path.Combine(session, "monitor-process.json"));
        if (marker == null || !int.TryParse(marker["pid"]?.ToString(), out int recorded)
            || !DateTimeOffset.TryParse(marker["bornUtc"]?.ToString(), out DateTimeOffset born))
            return false;
        try
        {
            using Process process = Process.GetProcessById(recorded);
            if (process.HasExited || Math.Abs((process.StartTime.ToUniversalTime() - born.UtcDateTime).TotalSeconds) > 1)
                return false;
            pid = recorded;
            return true;
        }
        catch (ArgumentException) { return false; }
    }

    private static async Task StartMonitor(string project, string session)
    {
        if (IsMonitorRunning(session, out _)) return;
        foreach (string name in new[] { "monitor-process.json", "monitor-ready.json", "monitor.stop" })
            File.Delete(Path.Combine(session, name));
        bool windows = OperatingSystem.IsWindows();
        ProcessStartInfo start = new(windows ? "pwsh" : "bash")
        {
            WorkingDirectory = project, UseShellExecute = false, CreateNoWindow = true,
        };
        if (windows)
        {
            start.ArgumentList.Add("-STA");
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-File");
        }
        start.ArgumentList.Add(Path.Combine(project, "tools", windows
            ? "strategy-monitor.ps1" : "strategy-monitor.sh"));
        start.ArgumentList.Add(session);
        using Process process = Process.Start(start) ?? throw new IOException("Monitor failed to start.");
        Save(Path.Combine(session, "monitor-process.json"), new JsonObject
        {
            ["pid"] = process.Id, ["bornUtc"] = process.StartTime.ToUniversalTime(),
        });
        string ready = Path.Combine(session, "monitor-ready.json");
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (File.Exists(ready)) return;
            if (process.HasExited)
                throw new IOException("Monitor exited during startup; see monitor-error.log.");
            await Task.Delay(100);
        }
        throw new TimeoutException("Monitor window did not become ready within 10 seconds.");
    }

    private static async Task StopMonitor(string session)
    {
        if (!IsMonitorRunning(session, out int pid)) return;
        File.WriteAllText(Path.Combine(session, "monitor.stop"), "stop");
        using Process process = Process.GetProcessById(pid);
        for (int attempt = 0; attempt < 50; attempt++)
        {
            if (process.HasExited) return;
            await Task.Delay(100);
        }
        if (IsMonitorRunning(session, out int confirmed) && confirmed == pid)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }
    private static void Save(string path, JsonNode value) => BatchRunner.Save(path, value);
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string HashText(string value) => HashBytes(Encoding.UTF8.GetBytes(value));
    private static string HashBytes(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    private static string FindProject()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "CombatSolver.csproj"))) return directory.FullName;
        throw new DirectoryNotFoundException("CombatSolver project root not found.");
    }
}
