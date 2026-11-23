using MegaCrit.Sts2.Core.Combat;

namespace CombatSolver;

internal enum InCombatBackendKind
{
    None,
    HotKernel,
    RustKernel,
}

internal sealed record InCombatBackendDisplaySnapshot(
    InCombatBackendKind ActiveBackend,
    bool IsSearching,
    InCombatBackendKind LastUsedBackend,
    int? EffectiveParallelism,
    string? LastFallbackCode,
    string? LastFallbackDetail,
    string? LastExclusionCode,
    string? LastExclusionDetail,
    bool HasInstalledRustKernel,
    bool IsRustKernelCorrupt,
    bool UseRustKernel,
    bool RustKernelHostEligible,
    string? RustKernelHostExclusionCode,
    string? RustKernelHostExclusionDetail);

internal static class InCombatSolverBackendState
{
    private static readonly object Sync = new();
    private static InCombatBackendKind _activeBackend;
    private static InCombatBackendKind _lastUsedBackend;
    private static int? _effectiveParallelism;
    private static string? _lastFallbackCode;
    private static string? _lastFallbackDetail;
    private static string? _lastExclusionCode;
    private static string? _lastExclusionDetail;
    private static long _rustKernelSuccessCount;
    private static long _rustKernelFallbackCount;

    internal static InCombatBackendKind ActiveBackendForTesting
    {
        get
        {
            lock (Sync)
                return _activeBackend;
        }
    }

    internal static InCombatBackendKind LastUsedBackendForTesting
    {
        get
        {
            lock (Sync)
                return _lastUsedBackend;
        }
    }

    internal static int? LastEffectiveParallelismForTesting
    {
        get
        {
            lock (Sync)
                return _effectiveParallelism;
        }
    }

    internal static string? LastFallbackCodeForTesting
    {
        get
        {
            lock (Sync)
                return _lastFallbackCode;
        }
    }

    internal static string? LastFallbackDetailForTesting
    {
        get
        {
            lock (Sync)
                return _lastFallbackDetail;
        }
    }

    internal static string? LastExclusionCodeForTesting
    {
        get
        {
            lock (Sync)
                return _lastExclusionCode;
        }
    }

    internal static string? LastExclusionDetailForTesting
    {
        get
        {
            lock (Sync)
                return _lastExclusionDetail;
        }
    }

    internal static long RustKernelSuccessCountForTesting
        => Interlocked.Read(ref _rustKernelSuccessCount);

    internal static long RustKernelFallbackCountForTesting
        => Interlocked.Read(ref _rustKernelFallbackCount);

    public static void BeginActiveRustKernel(int requestedParallelism)
    {
        lock (Sync)
        {
            _activeBackend = InCombatBackendKind.RustKernel;
            _effectiveParallelism = requestedParallelism;
            _lastExclusionCode = null;
            _lastExclusionDetail = null;
        }
    }

    public static void RecordRustKernelSuccess(int effectiveParallelism)
    {
        lock (Sync)
        {
            _activeBackend = InCombatBackendKind.None;
            _lastUsedBackend = InCombatBackendKind.RustKernel;
            _effectiveParallelism = effectiveParallelism;
            _lastFallbackCode = null;
            _lastFallbackDetail = null;
            _lastExclusionCode = null;
            _lastExclusionDetail = null;
        }
        Interlocked.Increment(ref _rustKernelSuccessCount);
    }

    public static void RecordExclusion(string code, string? detail)
    {
        lock (Sync)
        {
            _lastExclusionCode = code;
            _lastExclusionDetail = detail;
        }
    }

    public static void ClearExclusion()
    {
        lock (Sync)
        {
            _lastExclusionCode = null;
            _lastExclusionDetail = null;
        }
    }

    public static void RecordFallback(string code, string? detail)
    {
        bool wasRustKernel;
        lock (Sync)
        {
            wasRustKernel = _activeBackend == InCombatBackendKind.RustKernel;
            if (_activeBackend == InCombatBackendKind.RustKernel)
                _activeBackend = InCombatBackendKind.None;
            _lastFallbackCode = code;
            _lastFallbackDetail = detail;
        }
        if (wasRustKernel)
            Interlocked.Increment(ref _rustKernelFallbackCount);
    }

    public static void BeginActiveHotKernel(int requestedParallelism)
    {
        lock (Sync)
        {
            _activeBackend = InCombatBackendKind.HotKernel;
            _effectiveParallelism = requestedParallelism;
        }
    }

    public static void RecordHotKernelCompleted(int effectiveParallelism)
    {
        lock (Sync)
        {
            _activeBackend = InCombatBackendKind.None;
            _lastUsedBackend = InCombatBackendKind.HotKernel;
            _effectiveParallelism = effectiveParallelism;
        }
    }

    public static void ClearActive()
    {
        lock (Sync)
            _activeBackend = InCombatBackendKind.None;
    }

    public static void ResetForCombat()
    {
        lock (Sync)
        {
            _activeBackend = InCombatBackendKind.None;
            _lastUsedBackend = InCombatBackendKind.None;
            _effectiveParallelism = null;
            _lastFallbackCode = null;
            _lastFallbackDetail = null;
            _lastExclusionCode = null;
            _lastExclusionDetail = null;
        }
    }

    public static InCombatBackendDisplaySnapshot CaptureDisplaySnapshot(
        CombatState? combatState,
        SolverSettingsSnapshot settings)
    {
        bool hasInstalledRustKernel = InCombatRustKernelBridge.HasInstalledBackendArtifact();
        bool isRustKernelCorrupt = InCombatRustKernelBridge.IsCorrupt;
        bool rustKernelHostEligible = false;
        string? rustKernelHostExclusionCode = null;
        string? rustKernelHostExclusionDetail = null;
        if (combatState is not null)
        {
            RustKernelEligibilityResult rustEligibility =
                InCombatRustKernelEligibility.AssessHostEligibility(combatState);
            rustKernelHostEligible = rustEligibility.IsEligible;
            rustKernelHostExclusionCode = rustEligibility.FailureCode;
            rustKernelHostExclusionDetail = rustEligibility.Detail;
        }

        lock (Sync)
        {
            return new InCombatBackendDisplaySnapshot(
                ActiveBackend: _activeBackend,
                IsSearching: _activeBackend != InCombatBackendKind.None || SolverController.IsSearching,
                LastUsedBackend: _lastUsedBackend,
                EffectiveParallelism: _effectiveParallelism,
                LastFallbackCode: _lastFallbackCode,
                LastFallbackDetail: _lastFallbackDetail,
                LastExclusionCode: _lastExclusionCode,
                LastExclusionDetail: _lastExclusionDetail,
                HasInstalledRustKernel: hasInstalledRustKernel,
                IsRustKernelCorrupt: isRustKernelCorrupt,
                UseRustKernel: settings.UseRustCombatKernel,
                RustKernelHostEligible: rustKernelHostEligible,
                RustKernelHostExclusionCode: rustKernelHostExclusionCode,
                RustKernelHostExclusionDetail: rustKernelHostExclusionDetail);
        }
    }
}
