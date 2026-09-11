using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CombatSolver.Replay;

internal static class BatchRunner
{
    private static readonly HashSet<string> Success = new(StringComparer.Ordinal)
        { "materials_valid", "restored", "recorded_completed", "recorded_prefix_verified", "search_completed", "deployment_completed" };

    public static async Task<int> Run(string[] args)
    {
        if (args.Length == 0) throw new ArgumentException("batch requires INPUT");
        Dictionary<string, string> options = new(StringComparer.Ordinal);
        for (int index = 1; index < args.Length; index++)
        {
            string name = args[index];
            if (name is "--resume" or "--retry-failures") options.Add(name, "true");
            else if (index + 1 < args.Length) options.Add(name, args[++index]);
            else throw new ArgumentException("missing_option_value:" + name);
        }
        string[] allowed = ["--output", "--mode", "--selector", "--policy", "--manifest", "--game-root", "--ritsu-root", "--timeout", "--max-items", "--resume", "--retry-failures"];
        foreach (string name in options.Keys)
            if (!allowed.Contains(name)) throw new ArgumentException("unknown_option:" + name);
        string Option(string name, string fallback) => options.GetValueOrDefault(name, fallback);
        string mode = Option("--mode", "RestoreOnly");
        if (mode is not ("Preflight" or "RestoreOnly" or "ReplayRecorded" or "SearchOnly" or "DeploySolver"))
            throw new ArgumentException("invalid_mode:" + mode);
        int timeout = int.Parse(Option("--timeout", "120"));
        if (timeout is < 10 or > 120) throw new ArgumentException("timeout_must_be_10_to_120_seconds");
        string selector = Option("--selector", "latest");
        string output = Path.GetFullPath(Option("--output", Path.Combine(".local", "checkpoint-batch", DateTime.Now.ToString("yyyyMMdd-HHmmss"))));
        string project = FindProject();
        Directory.CreateDirectory(output);
        using FileStream batchLock = new(Path.Combine(output, "batch.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        JsonNode? overrides = options.TryGetValue("--policy", out string? policyPath)
            ? JsonNode.Parse(File.ReadAllText(Path.GetFullPath(policyPath))) : null;
        string? frozenPolicy = overrides == null ? null : Path.Combine(output, "policy-" + BatchInputs.HashText(overrides.ToJsonString()) + ".json");
        if (frozenPolicy != null) Save(frozenPolicy, overrides);
        JsonArray manifest = [];
        if (options.TryGetValue("--manifest", out string? manifestPath))
        {
            JsonNode parsed = JsonNode.Parse(File.ReadAllText(manifestPath))!;
            manifest = (parsed as JsonArray ?? parsed["manifest"] as JsonArray)
                ?? throw new InvalidDataException("expected_manifest_array");
        }
        JsonObject environment = CaptureEnvironment(project, options, mode);
        string journal = Path.Combine(output, "results.jsonl");
        List<JsonObject> history = ReadJournal(journal);
        if (history.Count > 0 && !options.ContainsKey("--resume"))
            throw new InvalidOperationException("output_has_results_use_resume_or_new_output");
        BatchInputs discovery = new(Path.Combine(output, "inputs"));
        IReadOnlyList<BatchInput> inputs = discovery.Discover(args[0]);
        List<(BatchInput Input, JsonObject Inspection, JsonObject? Manifest)> work = [];
        foreach (BatchInput input in inputs)
        {
            JsonObject inspection;
            try
            {
                inspection = input.Error == null ? CheckpointArchive.Inspect(input.ArchivePath!, mode == "ReplayRecorded" && selector == "latest" ? "recorded" : selector)
                    : new JsonObject { ["status"] = "invalid_archive", ["reason"] = input.Error };
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or InvalidOperationException or ArgumentException)
            { inspection = new JsonObject { ["status"] = "invalid_archive", ["reason"] = error.Message }; }
            JsonObject? entry = manifest.OfType<JsonObject>().SingleOrDefault(item =>
                item["archivePath"]?.GetValue<string>() == input.Source
                || item["reportId"]?.GetValue<string>() is { Length: > 0 } id && input.Source.Contains(id, StringComparison.OrdinalIgnoreCase));
            work.Add((input, inspection, entry));
        }
        work = work.OrderByDescending(item => !string.IsNullOrWhiteSpace(Note(item.Inspection, item.Manifest)))
            .ThenByDescending(item => Number(item.Manifest?["originalLoss"]) - Number(item.Manifest?["manualLoss"]))
            .ThenBy(item => item.Input.Source, StringComparer.Ordinal).ToList();
        int maximum = int.Parse(Option("--max-items", "0"));
        if (maximum < 0) throw new ArgumentException("max_items_must_be_nonnegative");
        if (maximum > 0) work = work.Take(maximum).ToList();
        Save(Path.Combine(output, "index.json"), new JsonObject
        {
            ["inputs"] = JsonSerializer.SerializeToNode(work.Select(item => item.Input)),
            ["duplicates"] = discovery.Duplicates, ["environment"] = environment.DeepClone(), ["mode"] = mode,
        });
        Console.WriteLine($"BATCH_INDEX inputs={work.Count} duplicates={discovery.Duplicates} mode={mode}");
        List<JsonObject> results = [];
        bool launched = false;
        try
        {
            foreach ((BatchInput input, JsonObject inspection, JsonObject? entry) in work)
            {
                string key = BatchInputs.HashText(new JsonObject
                {
                    ["input"] = input.Identity, ["selector"] = selector, ["mode"] = mode,
                    ["checkpoint"] = inspection["checkpoint"]?["checkpointId"]?.DeepClone(),
                    ["policy"] = overrides?.DeepClone(), ["environment"] = environment.DeepClone(), ["timeout"] = timeout,
                    ["comparison"] = entry?.DeepClone(),
                }.ToJsonString());
                JsonObject? prior = history.LastOrDefault(row => Text(row["key"]) == key
                    && (!options.ContainsKey("--retry-failures") || Success.Contains(Text(row["status"]))));
                if (prior != null)
                {
                    JsonObject reused = (JsonObject)prior.DeepClone();
                    reused["reused"] = true;
                    results.Add(reused);
                    Publish(output, results);
                    Console.WriteLine($"BATCH_REUSED {Path.GetFileName(input.Source)} {Text(prior["status"])}");
                    continue;
                }
                string evidence = Path.Combine(output, "requests", key, DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffff"));
                Directory.CreateDirectory(evidence);
                Save(Path.Combine(evidence, "preflight.json"), inspection);
                Save(Path.Combine(evidence, "request-intent.json"), new JsonObject
                {
                    ["archivePath"] = input.ArchivePath, ["selector"] = selector, ["mode"] = mode,
                    ["timeoutSeconds"] = timeout, ["policyOverrides"] = overrides?.DeepClone(),
                });
                JsonObject row = new()
                {
                    ["key"] = key, ["inputIdentity"] = input.Identity, ["source"] = input.Source,
                    ["mode"] = mode, ["checkpointId"] = inspection["checkpoint"]?["checkpointId"]?.DeepClone(),
                    ["status"] = inspection["status"]?.DeepClone(), ["reason"] = inspection["reason"]?.DeepClone(),
                    ["note"] = Note(inspection, entry), ["reportedOriginalPrediction"] = entry?["originalLoss"]?.DeepClone(),
                    ["reportedManualPrediction"] = entry?["manualLoss"]?.DeepClone(),
                    ["relativeToManual"] = null, ["betterThanManual"] = null, ["predictionGap"] = null,
                    ["evidence"] = Path.GetRelativePath(output, evidence).Replace('\\', '/'), ["testedAtUtc"] = DateTimeOffset.UtcNow,
                    ["environment"] = environment.DeepClone(), ["restorationVerified"] = false,
                };
                if (mode != "Preflight" && Text(inspection["status"]) == "materials_valid")
                {
                    launched = true;
                    string runtime = RuntimeDirectory();
                    string log = Path.Combine(runtime, "godot-headless.log");
                    long logStart = File.Exists(log) ? new FileInfo(log).Length : 0;
                    JsonObject? beforeProcess = ReadObjectIfExists(Path.Combine(runtime, "process.json"));
                    Stopwatch elapsed = Stopwatch.StartNew();
                    int exit = await Launch(project, options, evidence, timeout, input.ArchivePath!, selector, mode, frozenPolicy, stop: false);
                    JsonObject? result = ReadObjectIfExists(Path.Combine(evidence, "result.json"));
                    JsonObject? launcher = ReadObjectIfExists(Path.Combine(evidence, "launcher-result.json"));
                    CaptureLogSlice(log, evidence, result?["processId"]?.GetValue<int>() is int pid
                        && beforeProcess?["pid"]?.GetValue<int>() == pid ? logStart : 0);
                    row["launcherExitCode"] = exit;
                    row["wallMilliseconds"] = elapsed.Elapsed.TotalMilliseconds;
                    row["status"] = Classify(result, exit, launcher);
                    row["reason"] = launcher?["reason"]?.DeepClone() ?? result?["error"]?.DeepClone() ?? (exit == 0 ? null : JsonValue.Create("see_launcher_log"));
                    row["runId"] = result?["runId"]?.DeepClone();
                    row["processId"] = result?["processId"]?.DeepClone();
                    row["solverMetrics"] = result?["solverMetrics"]?.DeepClone();
                    row["verification"] = result?["replayVerification"]?.DeepClone();
                    row["restorationVerified"] = result?["replayVerification"]?["restorationVerified"]?.DeepClone() ?? JsonValue.Create(false);
                    row["comparisonScope"] = result?["replayVerification"]?["comparisonScope"]?.DeepClone();
                    row["elapsedMilliseconds"] = result?["elapsedMilliseconds"]?.DeepClone();
                    if (Success.Contains(Text(row["status"])) && result?["solverMetrics"] is JsonObject metrics
                        && metrics["combatEndedTurn"] != null && Number(metrics["finalHp"]) > 0 && Number(metrics["finalEnemyHp"]) <= 0
                        && entry?["manualLoss"] != null
                        && Text(row["comparisonScope"]) == "full_combat"
                        && Text(entry["comparisonCheckpointId"]) == Text(row["checkpointId"]))
                        row["predictionGap"] = Number(entry["manualLoss"]) - Number(metrics["projectedBattleHpLost"]);
                    CompareVerifiedRoute(row, history, inspection);
                    if (exit == 124) await Launch(project, options, evidence, timeout, null, selector, mode, null, stop: true);
                }
                Save(Path.Combine(evidence, "batch-result.json"), row);
                File.AppendAllText(journal, row.ToJsonString() + "\n", new UTF8Encoding(false));
                history.Add(row);
                results.Add(row);
                Publish(output, results);
                Console.WriteLine($"BATCH_RESULT {results.Count}/{work.Count} {Path.GetFileName(input.Source)} {Text(row["status"])}");
            }
        }
        finally
        {
            if (launched)
                await Launch(project, options, output, timeout, null, selector, mode, null, stop: true);
        }
        Publish(output, results);
        return results.All(row => Success.Contains(Text(row["status"]))) ? 0 : 1;
    }

    internal static string Classify(JsonObject? result, int exit, JsonObject? launcher = null)
    {
        if (exit == 124 || Text(launcher?["status"]) == "timeout") return "timeout";
        if (result == null) return "process_crash";
        string status = Text(result["replayVerification"]?["status"]);
        if (Text(result["status"]) != "Passed")
            return status is "environment_mismatch" or "materials_missing" or "restore_mismatch" or "recorded_action_mismatch" or "timeout"
                ? status : "execution_failed";
        if (exit != 0) return "process_cleanup_failed";
        return Text(result["replayVerification"]?["mode"]) == "SearchOnly" ? "search_completed" : status;
    }

    internal static void CompareVerifiedRoute(JsonObject row, List<JsonObject> history, JsonObject inspection)
    {
        if (Text(row["status"]) != "deployment_completed" || Text(row["comparisonScope"]) != "full_combat"
            || row["verification"]?["actualOutcome"] is not JsonObject actual
            || actual["combatEnded"]?.GetValue<bool>() != true || actual["survived"]?.GetValue<bool>() != true) return;
        JsonObject? recorded = history.LastOrDefault(prior => Text(prior["inputIdentity"]) == Text(row["inputIdentity"])
            && Text(prior["status"]) == "recorded_completed" && Text(prior["comparisonScope"]) == "full_combat"
            && JsonNode.DeepEquals(prior["environment"], row["environment"]));
        if (recorded?["verification"]?["recordedOutcome"] is not JsonObject manual
            || manual["survived"]?.GetValue<bool>() != true || manual["combatEnded"]?.GetValue<bool>() != true) return;
        if (!EquivalentPolicy(row["verification"]?["executedPolicy"], row["verification"]?["recordedPolicy"])) return;
        if (manual["hpLost"]?.GetValue<int>() is not int manualLoss || actual["hpLost"]?.GetValue<int>() is not int actualLoss) return;
        row["relativeToRecorded"] = manualLoss - actualLoss;
        // Mixed and solver recordings remain useful regression references, but are not human baselines.
        if (inspection["index"]?["recording"]?["inputOrigins"] is not JsonObject origins
            || Number(origins["player"]) <= 0 || Number(origins["solver"]) > 0) return;
        row["relativeToManual"] = row["relativeToRecorded"]!.DeepClone();
        row["betterThanManual"] = Number(row["relativeToManual"]) > 0;
    }

    private static async Task<int> Launch(string project, Dictionary<string, string> options, string evidence,
        int timeout, string? archive, string selector, string mode, string? policy, bool stop)
    {
        bool windows = OperatingSystem.IsWindows();
        ProcessStartInfo start = new(windows ? "pwsh" : "bash")
        { WorkingDirectory = project, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (windows) { start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-File"); }
        start.ArgumentList.Add(Path.Combine(project, "tools", windows ? "run-unattended-test.ps1" : "run-unattended-test.sh"));
        void Arg(string ps, string sh, string? value = null) { start.ArgumentList.Add(windows ? "-" + ps : "--" + sh); if (value != null) start.ArgumentList.Add(value); }
        if (options.TryGetValue("--game-root", out string? game)) Arg("Sts2GameRoot", "sts2-game-root", Path.GetFullPath(game));
        if (options.TryGetValue("--ritsu-root", out string? ritsu)) Arg("RitsuWorkshopRoot", "ritsu-workshop-root", Path.GetFullPath(ritsu));
        if (stop) Arg("StopOwnedProcess", "stop-owned-process");
        else
        {
            Arg("CheckpointArchivePath", "checkpoint-archive-path", archive!);
            Arg("CheckpointSelector", "checkpoint-selector", selector);
            Arg("ReplayMode", "replay-mode", mode);
            Arg("EvidenceDirectory", "evidence-directory", evidence);
            Arg("HeadlessFastModeForTest", "headless-fast-mode-for-test", "Instant");
            Arg("TimeoutSeconds", "timeout-seconds", timeout.ToString());
            Arg("KeepGameOpen", "keep-game-open");
            if (policy != null) Arg("ReplayPolicyOverridePath", "replay-policy-override-path", policy);
        }
        using Process process = Process.Start(start) ?? throw new IOException("launcher_start_failed");
        using StreamWriter stdout = new(Path.Combine(evidence, stop ? "cleanup.log" : "launcher.log"), append: false);
        using StreamWriter stderr = new(Path.Combine(evidence, stop ? "cleanup-error.log" : "launcher-error.log"), append: false);
        using CancellationTokenSource outputLifetime = new();
        Task output = Pump(process.StandardOutput, stdout, outputLifetime.Token);
        Task errors = Pump(process.StandardError, stderr, outputLifetime.Token);
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(stop ? 30 : timeout + 15)); }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            outputLifetime.CancelAfter(100);
            await Task.WhenAll(output, errors);
            return 124;
        }
        // Windows descendants may inherit unrelated pipe handles even when their
        // own stdout is redirected. Drain available launcher text without waiting
        // for the deliberately reusable game process to close those handles.
        outputLifetime.CancelAfter(100);
        await Task.WhenAll(output, errors);
        return process.ExitCode;
        static async Task Pump(StreamReader input, StreamWriter destination, CancellationToken cancellation)
        {
            try
            {
                while (await input.ReadLineAsync(cancellation) is { } line) await destination.WriteLineAsync(line);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
    }

    internal static bool EquivalentPolicy(JsonNode? actual, JsonNode? recorded)
    {
        string[] budgetFields = actual?["profile"] != null || recorded?["profile"] != null
            ? ["profile", "fixedBudget"]
            : ["shortProfile", "deepProfile", "forceShortOnly"];
        string[] fields = ["potionPolicy", "potionDirectives", "actTransitionBossHpStrategy", "finalBossHpStrategy",
            "acceptableBattleHpLoss", "searchMaxDegreeOfParallelism", .. budgetFields];
        return fields.All(field => actual?[field] != null && recorded?[field] != null && JsonNode.DeepEquals(actual[field], recorded[field]));
    }

    private static JsonObject CaptureEnvironment(string project, Dictionary<string, string> options, string mode)
    {
        JsonObject result = new()
        {
            ["tool"] = BatchInputs.HashFile(typeof(BatchRunner).Assembly.Location),
            ["launcher"] = BatchInputs.HashFile(Path.Combine(project, "tools", OperatingSystem.IsWindows() ? "run-unattended-test.ps1" : "run-unattended-test.sh")),
            ["platform"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        };
        if (mode == "Preflight") return result;
        string home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        string steam = System.Environment.GetEnvironmentVariable("COMBATSOLVER_STEAM_ROOT")
            ?? (Directory.Exists(Path.Combine(home, ".local/share/Steam")) ? Path.Combine(home, ".local/share/Steam") : Path.Combine(home, ".steam/steam"));
        if (OperatingSystem.IsWindows() && !options.ContainsKey("--game-root"))
            throw new ArgumentException("Windows batch execution requires --game-root (Preflight does not)");
        string game = Path.GetFullPath(options.GetValueOrDefault("--game-root", Path.Combine(steam, "steamapps/common/Slay the Spire 2")));
        string ritsu = Path.GetFullPath(options.GetValueOrDefault("--ritsu-root", Path.Combine(game, "..", "..", "workshop/content/2868840/3747602295")));
        options["--game-root"] = game;
        options["--ritsu-root"] = ritsu;
        JsonObject files = new();
        foreach (string root in new[] { game, ritsu })
        {
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
            foreach (string file in Directory.EnumerateFiles(root, "*.dll", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                if (!file.Contains(".combatsolver-headless-ritsulib", StringComparison.Ordinal)) files[file] = BatchInputs.HashFile(file);
        }
        result["files"] = files;
        return result;
    }

    private static void CaptureLogSlice(string path, string evidence, long start)
    {
        if (!File.Exists(path)) { Save(Path.Combine(evidence, "log-slice.json"), new JsonObject { ["status"] = "missing" }); return; }
        using FileStream source = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long end = source.Length;
        if (start > end) start = 0;
        long retainedStart = Math.Max(start, end - 2L * 1024 * 1024);
        source.Position = retainedStart;
        using FileStream destination = File.Create(Path.Combine(evidence, "game.log"));
        byte[] buffer = new byte[65536];
        long remaining = end - retainedStart;
        while (remaining > 0)
        {
            int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0) break;
            destination.Write(buffer, 0, read);
            remaining -= read;
        }
        Save(Path.Combine(evidence, "log-slice.json"), new JsonObject { ["start"] = start, ["retainedStart"] = retainedStart, ["end"] = end, ["truncated"] = retainedStart > start });
    }

    internal static List<JsonObject> ReadJournal(string path)
    {
        if (!File.Exists(path)) return [];
        string text = File.ReadAllText(path);
        if (!text.EndsWith('\n'))
        {
            int last = text.LastIndexOf('\n');
            File.WriteAllText(path + ".interrupted-tail", text[(last + 1)..]);
            text = text[..(last + 1)];
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!.AsObject()).ToList();
    }
    private static void Publish(string output, List<JsonObject> results)
    {
        Save(Path.Combine(output, "results.json"), JsonSerializer.SerializeToNode(results));
        string[] fields = ["source", "status", "reason", "mode", "checkpointId", "note", "comparisonScope", "relativeToManual", "betterThanManual", "predictionGap", "elapsedMilliseconds", "evidence"];
        string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        File.WriteAllText(Path.Combine(output, "results.csv.tmp"), string.Join(',', fields) + "\n" + string.Join('\n', results.Select(row => string.Join(',', fields.Select(field => Csv(Text(row[field])))))) + "\n", new UTF8Encoding(true));
        File.Move(Path.Combine(output, "results.csv.tmp"), Path.Combine(output, "results.csv"), true);
        string Cell(JsonNode? node) => Text(node).Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        StringBuilder markdown = new("# 汇总\n\n| 包 | 状态 | 模式 | 玩家备注 | 对照范围 | 优化后相对于人工 | 是否更优 | 预测差距 | 证据 |\n| --- | --- | --- | --- | --- | --- | --- | --- | --- |\n");
        foreach (JsonObject row in results.OrderBy(row => Success.Contains(Text(row["status"])) ? 1 : 0))
        {
            string relative = row["relativeToManual"] == null ? "—" : Number(row["relativeToManual"]).ToString("+0;-0;0");
            markdown.AppendLine($"| {Path.GetFileName(Cell(row["source"]))} | {Cell(row["status"])} | {Cell(row["mode"])} | {Cell(row["note"])} | {Cell(row["comparisonScope"])} | {relative} | {(row["betterThanManual"] == null ? "—" : row["betterThanManual"]!.GetValue<bool>() ? "是" : "否")} | {Cell(row["predictionGap"])} | [结果]({Cell(row["evidence"])}/batch-result.json) |");
        }
        File.WriteAllText(Path.Combine(output, "results.md.tmp"), markdown.ToString(), new UTF8Encoding(false));
        File.Move(Path.Combine(output, "results.md.tmp"), Path.Combine(output, "results.md"), true);
    }
    private static string Note(JsonObject inspection, JsonObject? entry) => Text(entry?["note"] ?? inspection["report"]?["playerDescription"]);
    private static double Number(JsonNode? node)
    {
        if (node == null) return 0;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<double>(out double number)) return number;
            if (value.TryGetValue<int>(out int integer)) return integer;
            if (value.TryGetValue<long>(out long longInteger)) return longInteger;
        }
        throw new InvalidDataException("expected_numeric_result_field");
    }
    private static string Text(JsonNode? node) => node?.ToString() ?? "";
    internal static void Save(string path, JsonNode? value)
    {
        File.WriteAllText(path + ".tmp", value?.ToJsonString(CheckpointArchive.JsonOptions) ?? "null", new UTF8Encoding(false));
        File.Move(path + ".tmp", path, true);
    }
    private static JsonObject? ReadObjectIfExists(string path) => File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : null;
    private static string FindProject()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "CombatSolver.csproj"))) return directory.FullName;
        throw new DirectoryNotFoundException("CombatSolver project root not found");
    }
    private static string RuntimeDirectory()
    {
        if (OperatingSystem.IsWindows()) return Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "CombatSolver", "headless-runtime");
        string home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        return System.Environment.GetEnvironmentVariable("COMBATSOLVER_HEADLESS_ROOT") ?? Path.Combine(
            System.Environment.GetEnvironmentVariable("XDG_STATE_HOME") ?? Path.Combine(home, ".local/state"), "CombatSolver/headless-runtime");
    }
}
