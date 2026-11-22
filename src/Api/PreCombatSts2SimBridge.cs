using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

namespace CombatSolver.Api;

internal enum PreCombatBridgeAvailability
{
    Absent,
    Ready,
    CorruptOrIncompatible,
}

internal sealed record Sts2SimBridgeCapabilitiesDto(
    int PortfolioOptionsVersion,
    bool SupportsBeamWidthPortfolioFull,
    bool SupportsNoveltyPortfolio,
    bool SupportsAdaptiveNoveltyRefinement,
    bool SupportsEarlyTurnExploration,
    int MaxEarlyTurnExplorationDepth,
    int MaxEarlyTurnExplorationBudgetMilliseconds,
    bool SupportsParallelPortfolioScheduling,
    bool SupportsExtendedMetrics,
    bool SupportsCosmeticTestSubjectKills)
{
    public static Sts2SimBridgeCapabilitiesDto DefaultV1 { get; } = new(
        PortfolioOptionsVersion: 1,
        SupportsBeamWidthPortfolioFull: false,
        SupportsNoveltyPortfolio: false,
        SupportsAdaptiveNoveltyRefinement: false,
        SupportsEarlyTurnExploration: false,
        MaxEarlyTurnExplorationDepth: 0,
        MaxEarlyTurnExplorationBudgetMilliseconds: 0,
        SupportsParallelPortfolioScheduling: false,
        SupportsExtendedMetrics: false,
        SupportsCosmeticTestSubjectKills: false);

    public static Sts2SimBridgeCapabilitiesDto V2Default { get; } = new(
        PortfolioOptionsVersion: 2,
        SupportsBeamWidthPortfolioFull: true,
        SupportsNoveltyPortfolio: true,
        SupportsAdaptiveNoveltyRefinement: true,
        SupportsEarlyTurnExploration: true,
        MaxEarlyTurnExplorationDepth: 2,
        MaxEarlyTurnExplorationBudgetMilliseconds: 2_400_000,
        SupportsParallelPortfolioScheduling: true,
        SupportsExtendedMetrics: true,
        SupportsCosmeticTestSubjectKills: true);
}

internal static class PreCombatSts2SimBridge
{
    private const string BridgeFolderName = "sts2sim";
    private const string ManifestFileName = "sts2sim-bundle.manifest.json";
    private const string BridgeAssemblySimpleName = "Sts2Sim.PreCombatBridge";
    private const string SolverAssemblySimpleName = "Sts2Sim.Solver";
    private const string CoreAssemblySimpleName = "Sts2Sim.Core";
    private const string HashingAssemblySimpleName = "System.IO.Hashing";
    private const string BridgeEntryTypeName = "Sts2Sim.PreCombatBridge.BridgeEntry";
    private static readonly Version MinimumHashingVersion = new(9, 0, 0, 0);

    private static readonly string[] RequiredCoreBundleFiles =
    [
        "Sts2Sim.PreCombatBridge.dll",
        "Sts2Sim.Solver.dll",
        "Sts2Sim.Core.dll",
        "Sts2Sim.PreCombatBridge.deps.json",
    ];

    private static readonly object Sync = new();
    private static ActivatedBridge? _activated;
    private static bool _corruptCached;
    private static string? _corruptReason;
    private static int _activationAttemptCount;

    internal static string? ModDirectoryOverrideForTesting { get; set; }

    internal static Action? PrewarmHookForTesting { get; set; }

    internal static Func<Sts2SimBridgeRequestDto, CancellationToken, Task<Sts2SimBridgeResponseDto>>? RunHookForTesting { get; set; }

    internal static Func<Sts2SimBridgeRequestDto, CancellationToken, Task<byte[]>>? RunRawJsonHookForTesting { get; set; }

    internal static Func<InCombatSts2SimRequestDto, Action<InCombatSts2SimProgressDto>?, CancellationToken, Task<InCombatSts2SimResponseDto>>? RunInCombatHookForTesting { get; set; }

    internal static PreCombatBridgeAvailability? AvailabilityOverrideForTesting { get; set; }

    internal static Sts2SimBridgeCapabilitiesDto? CapabilitiesOverrideForTesting { get; set; }

    internal static int ActivationAttemptCountForTesting => Volatile.Read(ref _activationAttemptCount);

    internal static bool IsCorrupt
    {
        get
        {
            lock (Sync)
            {
                if (AvailabilityOverrideForTesting is { } forced)
                    return forced == PreCombatBridgeAvailability.CorruptOrIncompatible;
                return _corruptCached;
            }
        }
    }

    internal static void ResetForTesting()
    {
        lock (Sync)
        {
            _activated = null;
            _corruptCached = false;
            _corruptReason = null;
        }
        Interlocked.Exchange(ref _activationAttemptCount, 0);
        PrewarmHookForTesting = null;
        RunHookForTesting = null;
        RunRawJsonHookForTesting = null;
        RunInCombatHookForTesting = null;
        AvailabilityOverrideForTesting = null;
        CapabilitiesOverrideForTesting = null;
    }

    public static Sts2SimBridgeCapabilitiesDto GetActiveCapabilities()
    {
        lock (Sync)
        {
            if (CapabilitiesOverrideForTesting is { } forced)
                return forced;
            return _activated?.Capabilities ?? Sts2SimBridgeCapabilitiesDto.DefaultV1;
        }
    }

    internal static Sts2SimBridgeCapabilitiesDto DeserializeAndValidateCapabilities(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidDataException("Bridge GetCapabilitiesJson returned empty JSON.");
        Sts2SimBridgeCapabilitiesDto capabilities = JsonSerializer.Deserialize<Sts2SimBridgeCapabilitiesDto>(
            json,
            PreCombatSts2SimAbi.JsonOptions)
            ?? throw new InvalidDataException("Bridge GetCapabilitiesJson deserialized to null.");
        ValidateCapabilities(capabilities);
        return capabilities;
    }

    internal static void ValidateCapabilities(Sts2SimBridgeCapabilitiesDto capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        if (capabilities.PortfolioOptionsVersion < 1)
        {
            throw new InvalidDataException(
                $"Bridge capabilities reported invalid PortfolioOptionsVersion={capabilities.PortfolioOptionsVersion}.");
        }
        if (capabilities.MaxEarlyTurnExplorationDepth is < 0 or > 8)
        {
            throw new InvalidDataException(
                $"Bridge capabilities reported out-of-range MaxEarlyTurnExplorationDepth={capabilities.MaxEarlyTurnExplorationDepth}.");
        }
        if (capabilities.MaxEarlyTurnExplorationBudgetMilliseconds < 0)
        {
            throw new InvalidDataException(
                $"Bridge capabilities reported negative MaxEarlyTurnExplorationBudgetMilliseconds={capabilities.MaxEarlyTurnExplorationBudgetMilliseconds}.");
        }
        if (capabilities.SupportsEarlyTurnExploration
            && (capabilities.MaxEarlyTurnExplorationDepth < 1
                || capabilities.MaxEarlyTurnExplorationBudgetMilliseconds < 1))
        {
            throw new InvalidDataException(
                "Bridge capabilities advertised SupportsEarlyTurnExploration=true with non-positive depth or budget limit.");
        }
    }

    public static bool HasInstalledBackendArtifact()
    {
        if (AvailabilityOverrideForTesting is { } forced)
            return forced != PreCombatBridgeAvailability.Absent;
        string? bridgeDir = ResolveBridgeDirectory();
        if (bridgeDir is null)
            return false;
        return Directory.Exists(bridgeDir) || File.Exists(Path.Combine(bridgeDir, "Sts2Sim.PreCombatBridge.dll"));
    }

    public static PreCombatBridgeAvailability EnsureActivated(out string? error)
    {
        lock (Sync)
        {
            if (AvailabilityOverrideForTesting is { } forced)
            {
                error = forced switch
                {
                    PreCombatBridgeAvailability.Absent => "The optional sts2sim backend directory is not installed.",
                    PreCombatBridgeAvailability.CorruptOrIncompatible => "The installed sts2sim backend is corrupt or incompatible.",
                    _ => null,
                };
                return forced;
            }
            if (_activated is not null)
            {
                error = null;
                return PreCombatBridgeAvailability.Ready;
            }
            if (_corruptCached)
            {
                error = _corruptReason;
                return PreCombatBridgeAvailability.CorruptOrIncompatible;
            }

            string? bridgeDir = ResolveBridgeDirectory();
            if (bridgeDir is null || !Directory.Exists(bridgeDir))
            {
                error = "The optional sts2sim backend directory is not installed.";
                return PreCombatBridgeAvailability.Absent;
            }

            Interlocked.Increment(ref _activationAttemptCount);
            try
            {
                _activated = LoadAndVerifyLocked(bridgeDir);
                error = null;
                return PreCombatBridgeAvailability.Ready;
            }
            catch (Exception ex)
            {
                _corruptCached = true;
                _corruptReason = $"The installed sts2sim backend could not be activated: {ex.GetBaseException().Message}";
                error = _corruptReason;
                Entry.Logger.Error($"[CombatSolver/PreCombatApi] STS2SIM_BRIDGE_ACTIVATION_FAILED error={ex}");
                return PreCombatBridgeAvailability.CorruptOrIncompatible;
            }
        }
    }

    public static void Prewarm()
    {
        PrewarmHookForTesting?.Invoke();
        PreCombatBridgeAvailability state = EnsureActivated(out string? error);
        if (state == PreCombatBridgeAvailability.Absent)
            throw new InvalidOperationException(error ?? "Sts2Sim backend is absent.");
        if (state == PreCombatBridgeAvailability.CorruptOrIncompatible)
            throw new InvalidOperationException(error ?? "Sts2Sim backend is corrupt or incompatible.");
        if (_activated is not null)
            _activated.Prewarm();
        else if (AvailabilityOverrideForTesting != PreCombatBridgeAvailability.Ready)
            throw new InvalidOperationException(error ?? "Sts2Sim backend is corrupt or incompatible.");
    }

    public static async Task<Sts2SimBridgeResponseDto> RunAsync(
        Sts2SimBridgeRequestDto request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (RunRawJsonHookForTesting is { } rawHook)
        {
            PreCombatSts2SimWireValidator.ValidatePolicy(request.Policy, CapabilitiesOverrideForTesting);
            byte[] rawBytes = await rawHook(request, cancellationToken).ConfigureAwait(false);
            return PreCombatSts2SimWireValidator.DeserializeAndValidateResponse(rawBytes);
        }
        if (RunHookForTesting is { } runHook)
        {
            PreCombatSts2SimWireValidator.ValidatePolicy(request.Policy, CapabilitiesOverrideForTesting);
            Sts2SimBridgeResponseDto hooked = await runHook(request, cancellationToken).ConfigureAwait(false);
            PreCombatSts2SimWireValidator.ValidateResponse(hooked);
            return hooked;
        }
        PreCombatBridgeAvailability state = EnsureActivated(out string? error);
        if (state != PreCombatBridgeAvailability.Ready || _activated is null)
            throw new InvalidOperationException(error ?? $"Sts2Sim backend is not ready ({state}).");

        PreCombatSts2SimWireValidator.ValidatePolicy(request.Policy, CapabilitiesOverrideForTesting ?? _activated.Capabilities);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(request, PreCombatSts2SimAbi.JsonOptions);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] responseBytes = await _activated.RunJsonAsync(payload, cancellationToken).ConfigureAwait(false);
        return PreCombatSts2SimWireValidator.DeserializeAndValidateResponse(responseBytes);
    }

    public static async Task<InCombatSts2SimResponseDto> RunInCombatAsync(
        InCombatSts2SimRequestDto request,
        Action<InCombatSts2SimProgressDto>? onProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (RunInCombatHookForTesting is { } hook)
        {
            InCombatSts2SimWireValidator.ValidatePolicy(request.Policy, CapabilitiesOverrideForTesting);
            InCombatSts2SimResponseDto hooked = await hook(request, onProgress, cancellationToken).ConfigureAwait(false);
            InCombatSts2SimWireValidator.ValidateResponse(hooked);
            return hooked;
        }

        PreCombatBridgeAvailability state = EnsureActivated(out string? error);
        if (state != PreCombatBridgeAvailability.Ready || _activated is null)
            throw new InvalidOperationException(error ?? $"Sts2Sim backend is not ready ({state}).");

        InCombatSts2SimWireValidator.ValidatePolicy(request.Policy, CapabilitiesOverrideForTesting ?? _activated.Capabilities);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(request, PreCombatSts2SimAbi.JsonOptions);
        cancellationToken.ThrowIfCancellationRequested();
        Action<byte[]>? wireProgress = onProgress is null
            ? null
            : bytes =>
            {
                InCombatSts2SimProgressDto progress = InCombatSts2SimWireValidator.DeserializeAndValidateProgress(bytes);
                onProgress(progress);
            };
        byte[] responseBytes = await _activated.RunInCombatJsonAsync(payload, wireProgress, cancellationToken).ConfigureAwait(false);
        return InCombatSts2SimWireValidator.DeserializeAndValidateResponse(responseBytes);
    }

    private static string? ResolveBridgeDirectory()
    {
        if (!string.IsNullOrWhiteSpace(ModDirectoryOverrideForTesting))
            return Path.Combine(ModDirectoryOverrideForTesting, BridgeFolderName);
        string location = typeof(PreCombatSts2SimBridge).Assembly.Location;
        if (string.IsNullOrWhiteSpace(location))
            return null;
        string? modDir = Path.GetDirectoryName(location);
        return string.IsNullOrWhiteSpace(modDir) ? null : Path.Combine(modDir, BridgeFolderName);
    }

    private static ActivatedBridge LoadAndVerifyLocked(string bridgeDir)
    {
        string manifestPath = Path.Combine(bridgeDir, ManifestFileName);
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"Missing bridge bundle manifest: {manifestPath}", manifestPath);

        Sts2SimBundleManifest manifest = JsonSerializer.Deserialize<Sts2SimBundleManifest>(
            File.ReadAllText(manifestPath),
            PreCombatSts2SimAbi.JsonOptions)
            ?? throw new InvalidDataException("Sts2Sim bundle manifest is empty.");
        if (manifest.AbiVersion != PreCombatSts2SimAbi.Version)
            throw new InvalidDataException($"Unsupported bridge bundle ABI version {manifest.AbiVersion}.");
        if (!string.Equals(manifest.SupportedGameVersion, PreCombatSts2SimAbi.SupportedGameVersion, StringComparison.Ordinal)
            || !string.Equals(manifest.SupportedGameCommit, PreCombatSts2SimAbi.SupportedGameCommit, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Bridge bundle targets {manifest.SupportedGameVersion}@{manifest.SupportedGameCommit}, " +
                $"expected {PreCombatSts2SimAbi.SupportedGameVersion}@{PreCombatSts2SimAbi.SupportedGameCommit}.");
        }
        if (manifest.Files is null || manifest.Files.Count == 0)
            throw new InvalidDataException("Sts2Sim bundle manifest contains no file entries.");

        Dictionary<string, Sts2SimBundleFileManifestEntry> manifestEntries = new(StringComparer.Ordinal);
        foreach (Sts2SimBundleFileManifestEntry file in manifest.Files)
        {
            if (string.IsNullOrWhiteSpace(file.Name)
                || file.Name.Contains('/') || file.Name.Contains('\\') || file.Name.Contains(".."))
            {
                throw new InvalidDataException($"Invalid file entry in bundle manifest: '{file.Name}'.");
            }
            if (!manifestEntries.TryAdd(file.Name, file))
                throw new InvalidDataException($"Duplicate file entry in bundle manifest: '{file.Name}'.");
        }

        foreach (string requiredFile in RequiredCoreBundleFiles)
        {
            if (!manifestEntries.ContainsKey(requiredFile))
                throw new InvalidDataException($"Bridge bundle manifest omits required file '{requiredFile}'.");
        }

        foreach (Sts2SimBundleFileManifestEntry entry in manifest.Files)
        {
            string fullPath = Path.Combine(bridgeDir, entry.Name);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException($"Bridge bundle payload missing: {fullPath}", fullPath);
            string actualSha256 = ComputeFileSha256Hex(fullPath);
            if (!string.Equals(actualSha256, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Bridge bundle SHA-256 mismatch for '{entry.Name}': expected {entry.Sha256}, actual {actualSha256}.");
            }
        }

        string bridgeDllPath = Path.Combine(bridgeDir, $"{BridgeAssemblySimpleName}.dll");
        AssemblyLoadContext hostContext = AssemblyLoadContext.GetLoadContext(typeof(PreCombatSts2SimBridge).Assembly)
            ?? AssemblyLoadContext.Default;
        Sts2SimBundleFileManifestEntry? stagedHashing = manifestEntries.GetValueOrDefault($"{HashingAssemblySimpleName}.dll");
        if (File.Exists(Path.Combine(bridgeDir, $"{HashingAssemblySimpleName}.dll")) && stagedHashing is null)
            throw new InvalidDataException("Unmanifested System.IO.Hashing.dll is present in sts2sim directory.");

        var loadContext = new IsolatedBridgeLoadContext(bridgeDir, bridgeDllPath, hostContext, stagedHashing);
        Assembly bridgeAssembly = loadContext.LoadFromAssemblyPath(bridgeDllPath);
        Type entryType = bridgeAssembly.GetType(BridgeEntryTypeName, throwOnError: true)
            ?? throw new InvalidOperationException($"Missing entry type {BridgeEntryTypeName}.");

        Func<int> getAbiVersion = CreateDelegate<Func<int>>(entryType, "GetAbiVersion");
        Func<int> getInCombatAbiVersion = CreateDelegate<Func<int>>(entryType, "GetInCombatAbiVersion");
        Func<string> getBundleIdentityJson = CreateDelegate<Func<string>>(entryType, "GetBundleIdentityJson");
        Action prewarm = CreateDelegate<Action>(entryType, "Prewarm");
        Func<byte[], CancellationToken, Task<byte[]>> runJsonAsync =
            CreateDelegate<Func<byte[], CancellationToken, Task<byte[]>>>(entryType, "RunJsonAsync");
        Func<byte[], Action<byte[]>?, CancellationToken, Task<byte[]>> runInCombatJsonAsync =
            CreateDelegate<Func<byte[], Action<byte[]>?, CancellationToken, Task<byte[]>>>(entryType, "RunInCombatJsonAsync");

        int actualAbi = getAbiVersion();
        if (actualAbi != PreCombatSts2SimAbi.Version)
        {
            throw new InvalidDataException(
                $"Bridge runtime ABI mismatch: expected {PreCombatSts2SimAbi.Version}, got {actualAbi}.");
        }
        int actualInCombatAbi = getInCombatAbiVersion();
        if (actualInCombatAbi != InCombatSts2SimAbi.Version)
        {
            throw new InvalidDataException(
                $"Bridge in-combat ABI mismatch: expected {InCombatSts2SimAbi.Version}, got {actualInCombatAbi}.");
        }

        string identityJson = getBundleIdentityJson();
        Sts2SimBundleIdentityDto identity = JsonSerializer.Deserialize<Sts2SimBundleIdentityDto>(
            identityJson,
            PreCombatSts2SimAbi.JsonOptions)
            ?? throw new InvalidDataException("Bridge GetBundleIdentityJson returned null.");
        if (identity.AbiVersion != PreCombatSts2SimAbi.Version
            || !string.Equals(identity.SupportedGameVersion, PreCombatSts2SimAbi.SupportedGameVersion, StringComparison.Ordinal)
            || !string.Equals(identity.SupportedGameCommit, PreCombatSts2SimAbi.SupportedGameCommit, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Bridge identity metadata disagrees with supported host build.");
        }

        VerifyComponentMvid(manifestEntries, $"{BridgeAssemblySimpleName}.dll", identity.BridgeMvid);
        VerifyComponentMvid(manifestEntries, $"{SolverAssemblySimpleName}.dll", identity.SolverMvid);
        VerifyComponentMvid(manifestEntries, $"{CoreAssemblySimpleName}.dll", identity.CoreMvid);

        MethodInfo? getCapabilitiesMethod = entryType.GetMethod(
            "GetCapabilitiesJson",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);
        Sts2SimBridgeCapabilitiesDto capabilities = getCapabilitiesMethod is null
            ? Sts2SimBridgeCapabilitiesDto.DefaultV1
            : DeserializeAndValidateCapabilities(
                ((Func<string>)Delegate.CreateDelegate(typeof(Func<string>), getCapabilitiesMethod))());

        return new ActivatedBridge(loadContext, prewarm, runJsonAsync, runInCombatJsonAsync, capabilities);
    }

    private static void VerifyComponentMvid(
        IReadOnlyDictionary<string, Sts2SimBundleFileManifestEntry> manifestEntries,
        string dllName,
        string actualMvid)
    {
        if (!manifestEntries.TryGetValue(dllName, out Sts2SimBundleFileManifestEntry? expected)
            || string.IsNullOrWhiteSpace(expected.Mvid)
            || !Guid.TryParse(expected.Mvid, out Guid expectedGuid)
            || !Guid.TryParse(actualMvid, out Guid actualGuid)
            || expectedGuid != actualGuid)
        {
            throw new InvalidDataException(
                $"Bridge runtime MVID mismatch for '{dllName}': expected '{expected?.Mvid}', actual '{actualMvid}'.");
        }
    }

    private static TDelegate CreateDelegate<TDelegate>(Type entryType, string methodName)
        where TDelegate : Delegate
    {
        MethodInfo method = entryType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMethodException(entryType.FullName, methodName);
        return (TDelegate)Delegate.CreateDelegate(typeof(TDelegate), method);
    }

    private static string ComputeFileSha256Hex(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private sealed class ActivatedBridge(
        IsolatedBridgeLoadContext loadContext,
        Action prewarm,
        Func<byte[], CancellationToken, Task<byte[]>> runJsonAsync,
        Func<byte[], Action<byte[]>?, CancellationToken, Task<byte[]>> runInCombatJsonAsync,
        Sts2SimBridgeCapabilitiesDto capabilities)
    {
        public IsolatedBridgeLoadContext LoadContext { get; } = loadContext;
        public Action Prewarm { get; } = prewarm;
        public Func<byte[], CancellationToken, Task<byte[]>> RunJsonAsync { get; } = runJsonAsync;
        public Func<byte[], Action<byte[]>?, CancellationToken, Task<byte[]>> RunInCombatJsonAsync { get; } = runInCombatJsonAsync;
        public Sts2SimBridgeCapabilitiesDto Capabilities { get; } = capabilities;
    }

    private sealed class IsolatedBridgeLoadContext(
        string bridgeDir,
        string bridgeDllPath,
        AssemblyLoadContext hostContext,
        Sts2SimBundleFileManifestEntry? stagedHashing)
        : AssemblyLoadContext(name: "CombatSolver.PreCombatSts2Sim", isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(bridgeDllPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            string? simpleName = assemblyName.Name;
            if (string.IsNullOrEmpty(simpleName))
                return null;

            if (simpleName.StartsWith("Sts2Sim.", StringComparison.Ordinal))
            {
                string? resolvedPath = _resolver.ResolveAssemblyToPath(assemblyName);
                if (string.IsNullOrWhiteSpace(resolvedPath) || !File.Exists(resolvedPath))
                {
                    throw new FileNotFoundException(
                        $"Private Sts2Sim dependency '{simpleName}' could not be resolved inside '{bridgeDir}'.",
                        resolvedPath ?? simpleName);
                }
                return LoadFromAssemblyPath(resolvedPath);
            }

            if (string.Equals(simpleName, HashingAssemblySimpleName, StringComparison.Ordinal))
            {
                try
                {
                    Assembly hostHashing = hostContext.LoadFromAssemblyName(assemblyName);
                    Version? hostVersion = hostHashing.GetName().Version;
                    if (hostVersion is not null && hostVersion >= MinimumHashingVersion)
                        return hostHashing;
                }
                catch (FileNotFoundException)
                {
                    // Host lacks System.IO.Hashing; fall back to verified staged private payload.
                }
                catch (BadImageFormatException ex)
                {
                    throw new InvalidDataException("Host System.IO.Hashing assembly is unreadable.", ex);
                }

                string privatePath = Path.Combine(bridgeDir, $"{HashingAssemblySimpleName}.dll");
                if (stagedHashing is null || !File.Exists(privatePath))
                {
                    throw new FileNotFoundException(
                        "Compatible System.IO.Hashing (>= 9.0.0) was not found in host context or sts2sim manifest.",
                        privatePath);
                }
                string actualSha = ComputeFileSha256Hex(privatePath);
                if (!string.Equals(actualSha, stagedHashing.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Private System.IO.Hashing.dll SHA-256 mismatch: expected {stagedHashing.Sha256}, actual {actualSha}.");
                }
                Assembly privateAssembly = LoadFromAssemblyPath(privatePath);
                string actualMvid = privateAssembly.ManifestModule.ModuleVersionId.ToString("D");
                if (string.IsNullOrWhiteSpace(stagedHashing.Mvid)
                    || !string.Equals(actualMvid, stagedHashing.Mvid, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Private System.IO.Hashing.dll MVID mismatch: expected {stagedHashing.Mvid}, actual {actualMvid}.");
                }
                return privateAssembly;
            }

            return null;
        }
    }
}
