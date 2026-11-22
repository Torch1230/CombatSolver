using CombatSolver.Api;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal enum InCombatBackendKind
{
    None,
    BuiltIn,
    Sts2Sim,
}

internal sealed record InCombatBackendDisplaySnapshot(
    InCombatBackendKind ActiveBackend,
    bool IsSearching,
    InCombatBackendKind LastUsedBackend,
    int? EffectiveParallelism,
    string? LastFallbackCode,
    string? LastFallbackDetail,
    bool HasInstalledSts2Sim,
    bool IsBridgeCorrupt,
    bool UserEnabled,
    bool HostEligible,
    string? HostExclusionCode,
    string? HostExclusionDetail,
    string? LastExclusionCode = null,
    string? LastExclusionDetail = null);

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
    private static long _sts2SimSuccessCount;
    private static long _sts2SimFallbackCount;

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

    internal static long Sts2SimSuccessCountForTesting
        => Interlocked.Read(ref _sts2SimSuccessCount);

    internal static long Sts2SimFallbackCountForTesting
        => Interlocked.Read(ref _sts2SimFallbackCount);

    public static void BeginActiveSts2Sim(int requestedParallelism)
    {
        lock (Sync)
        {
            _activeBackend = InCombatBackendKind.Sts2Sim;
            _effectiveParallelism = requestedParallelism;
            _lastExclusionCode = null;
            _lastExclusionDetail = null;
        }
    }

    public static void RecordSts2SimSuccess(int effectiveParallelism)
    {
        lock (Sync)
        {
            _activeBackend = InCombatBackendKind.None;
            _lastUsedBackend = InCombatBackendKind.Sts2Sim;
            _effectiveParallelism = effectiveParallelism;
            _lastFallbackCode = null;
            _lastFallbackDetail = null;
            _lastExclusionCode = null;
            _lastExclusionDetail = null;
        }
        Interlocked.Increment(ref _sts2SimSuccessCount);
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
        lock (Sync)
        {
            if (_activeBackend == InCombatBackendKind.Sts2Sim)
                _activeBackend = InCombatBackendKind.None;
            _lastFallbackCode = code;
            _lastFallbackDetail = detail;
        }
        Interlocked.Increment(ref _sts2SimFallbackCount);
    }

    public static void BeginActiveBuiltIn(int requestedParallelism)
    {
        lock (Sync)
        {
            _activeBackend = InCombatBackendKind.BuiltIn;
            _effectiveParallelism = requestedParallelism;
        }
    }

    public static void RecordBuiltInCompleted(int effectiveParallelism)
    {
        lock (Sync)
        {
            _activeBackend = InCombatBackendKind.None;
            _lastUsedBackend = InCombatBackendKind.BuiltIn;
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
        bool hasInstalled = PreCombatSts2SimBridge.HasInstalledBackendArtifact();
        bool isCorrupt = PreCombatSts2SimBridge.IsCorrupt;
        bool hostEligible = false;
        string? hostExclusionCode = null;
        string? hostExclusionDetail = null;

        if (combatState?.RunState is RunState run)
        {
            PreCombatEligibilityResult hostResult = PreCombatSts2SimEligibility.AssessMandatoryLiveHost(run, forInCombat: true);
            hostEligible = hostResult.IsEligible;
            hostExclusionCode = hostResult.FailureCode;
            hostExclusionDetail = hostResult.Detail;
            if (hostEligible
                && InCombatSts2SimEligibility.TryFindActiveCombatModViolation(combatState, out string combatModReason))
            {
                hostEligible = false;
                hostExclusionCode = "gameplay_mod";
                hostExclusionDetail = combatModReason;
            }
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
                HasInstalledSts2Sim: hasInstalled,
                IsBridgeCorrupt: isCorrupt,
                UserEnabled: settings.UseSts2SimInCombat,
                HostEligible: hostEligible,
                HostExclusionCode: hostExclusionCode,
                HostExclusionDetail: hostExclusionDetail,
                LastExclusionCode: _lastExclusionCode,
                LastExclusionDetail: _lastExclusionDetail);
        }
    }
}
