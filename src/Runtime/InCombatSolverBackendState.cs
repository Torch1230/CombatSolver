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
    private static int _activeGeneration;
    private static int _latestGeneration;
    private static InCombatBackendKind _lastUsedBackend;
    private static int? _effectiveParallelism;
    private static string? _lastFallbackCode;
    private static string? _lastFallbackDetail;
    private static string? _lastExclusionCode;
    private static string? _lastExclusionDetail;
    private static CombatState? _cachedEligibilityCombat;
    private static bool _hasCachedEligibility;
    private static bool _cachedHostEligible;
    private static string? _cachedHostExclusionCode;
    private static string? _cachedHostExclusionDetail;
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

    public static void BeginActiveRustKernel(int generation, int requestedParallelism)
    {
        lock (Sync)
        {
            if (generation < _latestGeneration)
                return;
            _latestGeneration = generation;
            _activeGeneration = generation;
            _activeBackend = InCombatBackendKind.RustKernel;
            _effectiveParallelism = requestedParallelism;
            _lastExclusionCode = null;
            _lastExclusionDetail = null;
        }
    }

    public static void RecordRustKernelSuccess(int generation, int effectiveParallelism)
    {
        lock (Sync)
        {
            if (_activeGeneration != generation)
                return;
            _activeBackend = InCombatBackendKind.None;
            _activeGeneration = 0;
            _lastUsedBackend = InCombatBackendKind.RustKernel;
            _effectiveParallelism = effectiveParallelism;
            _lastFallbackCode = null;
            _lastFallbackDetail = null;
            _lastExclusionCode = null;
            _lastExclusionDetail = null;
        }
        Interlocked.Increment(ref _rustKernelSuccessCount);
    }

    public static void RecordHostEligibility(CombatState combatState, RustKernelEligibilityResult eligibility)
    {
        lock (Sync)
        {
            _cachedEligibilityCombat = combatState;
            _hasCachedEligibility = true;
            _cachedHostEligible = eligibility.IsEligible;
            _cachedHostExclusionCode = eligibility.FailureCode;
            _cachedHostExclusionDetail = eligibility.Detail;
        }
    }

    public static void RecordExclusion(int generation, string code, string? detail)
    {
        lock (Sync)
        {
            if (generation < _latestGeneration)
                return;
            _latestGeneration = generation;
            _lastExclusionCode = code;
            _lastExclusionDetail = detail;
        }
    }

    public static void ClearExclusion(int generation)
    {
        lock (Sync)
        {
            if (generation < _latestGeneration)
                return;
            _latestGeneration = generation;
            _lastExclusionCode = null;
            _lastExclusionDetail = null;
        }
    }

    public static void RecordFallback(int generation, string code, string? detail)
    {
        bool wasRustKernel;
        lock (Sync)
        {
            if (generation < _latestGeneration)
                return;
            _latestGeneration = generation;
            wasRustKernel = _activeGeneration == generation
                && _activeBackend == InCombatBackendKind.RustKernel;
            if (wasRustKernel)
            {
                _activeBackend = InCombatBackendKind.None;
                _activeGeneration = 0;
            }
            _lastFallbackCode = code;
            _lastFallbackDetail = detail;
        }
        if (wasRustKernel)
            Interlocked.Increment(ref _rustKernelFallbackCount);
    }

    public static void BeginActiveHotKernel(int generation, int requestedParallelism)
    {
        lock (Sync)
        {
            if (generation < _latestGeneration)
                return;
            _latestGeneration = generation;
            _activeGeneration = generation;
            _activeBackend = InCombatBackendKind.HotKernel;
            _effectiveParallelism = requestedParallelism;
        }
    }

    public static void RecordHotKernelCompleted(int generation, int effectiveParallelism)
    {
        lock (Sync)
        {
            if (_activeGeneration != generation)
                return;
            _activeBackend = InCombatBackendKind.None;
            _activeGeneration = 0;
            _lastUsedBackend = InCombatBackendKind.HotKernel;
            _effectiveParallelism = effectiveParallelism;
        }
    }

    public static void ClearActive(int? generation = null)
    {
        lock (Sync)
        {
            if (generation is int expected && _activeGeneration != expected)
                return;
            _activeBackend = InCombatBackendKind.None;
            _activeGeneration = 0;
        }
    }

    public static void ResetForCombat()
    {
        lock (Sync)
        {
            _latestGeneration++;
            _activeBackend = InCombatBackendKind.None;
            _activeGeneration = 0;
            _lastUsedBackend = InCombatBackendKind.None;
            _effectiveParallelism = null;
            _lastFallbackCode = null;
            _lastFallbackDetail = null;
            _lastExclusionCode = null;
            _lastExclusionDetail = null;
            _cachedEligibilityCombat = null;
            _hasCachedEligibility = false;
            _cachedHostEligible = false;
            _cachedHostExclusionCode = null;
            _cachedHostExclusionDetail = null;
        }
    }

    public static InCombatBackendDisplaySnapshot CaptureCachedDisplaySnapshot(bool useRustKernel)
    {
        bool hasInstalledRustKernel = InCombatRustKernelBridge.HasInstalledBackendArtifact();
        bool isRustKernelCorrupt = InCombatRustKernelBridge.IsCorrupt;

        lock (Sync)
        {
            bool hostEligible = !_hasCachedEligibility || _cachedHostEligible;
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
                UseRustKernel: useRustKernel,
                RustKernelHostEligible: hostEligible,
                RustKernelHostExclusionCode: _hasCachedEligibility ? _cachedHostExclusionCode : null,
                RustKernelHostExclusionDetail: _hasCachedEligibility ? _cachedHostExclusionDetail : null);
        }
    }

    public static InCombatBackendDisplaySnapshot CaptureDisplaySnapshot(
        CombatState? combatState,
        SolverSettingsSnapshot settings)
    {
        if (combatState is not null)
        {
            bool needsAssess;
            lock (Sync)
                needsAssess = !_hasCachedEligibility || !ReferenceEquals(_cachedEligibilityCombat, combatState);
            if (needsAssess)
                RecordHostEligibility(combatState, InCombatRustKernelEligibility.AssessHostEligibility(combatState));
        }

        return CaptureCachedDisplaySnapshot(settings.UseRustCombatKernel);
    }
}
