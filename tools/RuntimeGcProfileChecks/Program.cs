using CombatSolver;

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
