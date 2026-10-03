using CombatSolver;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.Length > 0 && args[0] == "--startup-probe")
{
    RuntimeGcProfileSelection current = RuntimeGcProfile.Current;
    string status = RuntimeGcStartupConfig.Apply(args[1], bool.Parse(args[2]));
    Console.WriteLine(JsonSerializer.Serialize(new { current.IsServerGc, current.IsActive, status,
        unchanged = current == RuntimeGcProfile.Current }));
    return;
}


int checks = 0;
void Check(bool condition, string contract)
{
    checks++;
    if (!condition)
        throw new InvalidOperationException(contract);
}

// Inject the reported CLR mode instead of depending on this test process's GC
// or editing its environment. No game, mutable settings, or file IO is needed.
foreach (bool serverGc in new[] { false, true })
{
    foreach (string? requested in new string?[] { null, "", "  " })
    {
        RuntimeGcProfileSelection profile = RuntimeGcProfile.Resolve(requested, serverGc);
        Check(profile.Status == RuntimeGcProfileStatus.Default && !profile.IsActive,
            "No explicit profile must preserve ordinary launch behavior.");
        Check(profile.ResolveEnableNoGcRegion(true) && !profile.ResolveEnableNoGcRegion(false),
            "CLR ServerGC alone must never override either saved NoGC choice.");
    }

    RuntimeGcProfileSelection unknown = RuntimeGcProfile.Resolve("future-profile", serverGc);
    Check(unknown.Status == RuntimeGcProfileStatus.UnknownProfile && !unknown.IsActive,
        "An unknown profile must remain observable and inactive.");
    Check(unknown.RequestedProfile == "future-profile" && unknown.IsServerGc == serverGc,
        "Diagnostics must retain the requested profile and actual CLR mode.");
    Check(unknown.ResolveEnableNoGcRegion(true) && !unknown.ResolveEnableNoGcRegion(false),
        "An unknown profile must preserve saved settings.");
}

RuntimeGcProfileSelection unavailable = RuntimeGcProfile.Resolve(
    RuntimeGcProfile.ServerGenerational, isServerGc: false);
Check(unavailable.Status == RuntimeGcProfileStatus.ServerGcUnavailable && !unavailable.IsActive,
    "A requested profile cannot claim activation when CLR ServerGC is unavailable.");
Check(unavailable.ResolveEnableNoGcRegion(true) && !unavailable.ResolveEnableNoGcRegion(false),
    "An unavailable profile must preserve both saved NoGC choices.");

foreach (string requested in new[] { RuntimeGcProfile.ServerGenerational, " SERVER-GENERATIONAL " })
{
    RuntimeGcProfileSelection active = RuntimeGcProfile.Resolve(requested, isServerGc: true);
    Check(active.Status == RuntimeGcProfileStatus.Active && active.IsActive,
        "An explicit profile with actual ServerGC must activate.");
    Check(!active.ResolveEnableNoGcRegion(true) && !active.ResolveEnableNoGcRegion(false),
        "The active profile must use generational GC for either saved choice.");
}

// Applying one immutable process selection cannot alter a subsequent ordinary
// selection or the caller-owned saved value. Persistence remains outside this API.
bool savedNoGc = true;
RuntimeGcProfileSelection selected = RuntimeGcProfile.Resolve(RuntimeGcProfile.ServerGenerational, true);
_ = selected.ResolveEnableNoGcRegion(savedNoGc);
Check(RuntimeGcProfile.Resolve(null, true).ResolveEnableNoGcRegion(savedNoGc),
    "A normal launch must still honor the original saved setting after a profile launch.");
Console.WriteLine($"RUNTIME_GC_PROFILE_CHECKS_OK checks={checks}");

Check(RuntimeGcProfile.ResolveRequest("", RuntimeGcProfile.ServerGenerational) == RuntimeGcProfile.ServerGenerational
    && RuntimeGcProfile.ResolveRequest(" ", RuntimeGcProfile.ServerGenerational) == RuntimeGcProfile.ServerGenerational,
    "Empty launcher environment must not hide startup configuration.");
Check(RuntimeGcProfile.ResolveRequest("future-profile", RuntimeGcProfile.ServerGenerational) == "future-profile",
    "An explicit environment profile must override startup configuration, even when unknown.");
string directory = Path.Combine(Path.GetTempPath(), "combatsolver-gc-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    string config = Path.Combine(directory, "test.runtimeconfig.json");
    foreach (string? initial in new string?[] { null, "false", "true" })
    {
        JsonObject props = new() { ["unrelated"] = new JsonObject { ["number"] = 17 } };
        if (initial != null) props["System.GC.Server"] = bool.Parse(initial);
        JsonObject document = new() { ["runtimeOptions"] = new JsonObject { ["configProperties"] = props },
            ["unrelatedRoot"] = "keep" };
        File.WriteAllText(config, document.ToJsonString());
        Check(RuntimeGcStartupConfig.Apply(config, true) == "Prepared", "Prepare mode.");
        byte[] prepared = File.ReadAllBytes(config);
        Check(RuntimeGcStartupConfig.Apply(config, true) == "AlreadyConfigured"
            && prepared.SequenceEqual(File.ReadAllBytes(config)), "Repeated launch must not rewrite config.");
        Check(RuntimeGcStartupConfig.Apply(config, false) == "Restored", "Restore mode.");
        Check(JsonNode.DeepEquals(document, JsonNode.Parse(File.ReadAllText(config))),
            "Restore original GC field and preserve all unrelated configuration.");
    }
    foreach (string invalid in new[] { "broken", "[]", "{}", "{\"runtimeOptions\":{\"configProperties\":[]}}",
        "{\"runtimeOptions\":{\"configProperties\":{\"System.GC.Server\":\"true\"}}}",
        "{\"runtimeOptions\":{\"configProperties\":{\"CombatSolver.RuntimeProfile\":\"unknown\"}}}" })
    {
        File.WriteAllText(config, invalid);
        bool rejected = false;
        try { RuntimeGcStartupConfig.Apply(config, true); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException) { rejected = true; }
        Check(rejected && File.ReadAllText(config) == invalid, "Reject malformed or foreign ownership without writing.");
    }
    File.WriteAllText(config, "{\"runtimeOptions\":{}}");
    Check(RuntimeGcStartupConfig.Apply(config, false) == "Unchanged", "Disabled mode must not add configProperties.");
    RuntimeGcStartupConfig.Apply(config, true);
    JsonObject edited = JsonNode.Parse(File.ReadAllText(config))!.AsObject();
    edited["runtimeOptions"]!["configProperties"]!["System.GC.Server"] = false;
    File.WriteAllText(config, edited.ToJsonString());
    bool conflict = false;
    try { RuntimeGcStartupConfig.Apply(config, false); }
    catch (InvalidDataException) { conflict = true; }
    Check(conflict && JsonNode.DeepEquals(edited, JsonNode.Parse(File.ReadAllText(config))),
        "External changes must not be overwritten during restore.");

    // Three fresh real CLRs: ordinary first launch prepares next launch; second
    // activates with no GC/profile environment and restores; third is ordinary.
    string assembly = typeof(RuntimeGcProfile).Assembly.Location;
    string target = Path.Combine(directory, Path.GetFileName(assembly));
    foreach (string extension in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
        File.Copy(Path.ChangeExtension(assembly, extension), Path.ChangeExtension(target, extension));
    string actualConfig = Path.ChangeExtension(target, ".runtimeconfig.json");
    for (int launch = 0; launch < 3; launch++)
    {
        ProcessStartInfo start = new("dotnet") { UseShellExecute = false, RedirectStandardOutput = true };
        foreach (string key in new[] { "DOTNET_gcServer", "COMPlus_gcServer", RuntimeGcProfile.EnvironmentVariable })
            start.Environment.Remove(key);
        start.Environment[RuntimeGcProfile.EnvironmentVariable] = "";
        foreach (string argument in new[] { target, "--startup-probe", actualConfig, (launch == 0).ToString() })
            start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Check(process.ExitCode == 0, "Child startup probe must succeed.");
        using JsonDocument result = JsonDocument.Parse(output);
        Check(result.RootElement.GetProperty("IsServerGc").GetBoolean() == (launch == 1)
            && result.RootElement.GetProperty("IsActive").GetBoolean() == (launch == 1)
            && result.RootElement.GetProperty("unchanged").GetBoolean(),
            "Only next startup may activate/restore actual GC and profile.");
        Console.WriteLine($"STARTUP_PROBE launch={launch} {output.Trim()}");
    }
    Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "No abandoned temporary files.");
}
finally { Directory.Delete(directory, recursive: true); }
Console.WriteLine($"RUNTIME_GC_STARTUP_CHECKS_OK total_checks={checks}");
