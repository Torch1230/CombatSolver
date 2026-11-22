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

    private sealed record BridgeAvailabilitySnapshot(
        ActivatedBridge? Activated,
        bool IsCorrupt,
        string? CorruptReason)
    {
        public static readonly BridgeAvailabilitySnapshot Initial = new(null, false, null);
    }

    private static readonly object Sync = new();
    private static volatile BridgeAvailabilitySnapshot _snapshot = BridgeAvailabilitySnapshot.Initial;
    private static int _availabilityOverrideForTesting = -1;
    private static int _activationAttemptCount;

    internal static string? ModDirectoryOverrideForTesting { get; set; }

    internal static Action? ActivationHookForTesting { get; set; }

    internal static Action? PrewarmHookForTesting { get; set; }

    internal static Func<Sts2SimBridgeRequestDto, CancellationToken, Task<Sts2SimBridgeResponseDto>>? RunHookForTesting { get; set; }

    internal static Func<Sts2SimBridgeRequestDto, CancellationToken, Task<byte[]>>? RunRawJsonHookForTesting { get; set; }

    internal static PreCombatBridgeAvailability? AvailabilityOverrideForTesting
    {
        get
        {
            int raw = Volatile.Read(ref _availabilityOverrideForTesting);
            return raw >= 0 ? (PreCombatBridgeAvailability)raw : null;
        }
        set => Volatile.Write(ref _availabilityOverrideForTesting, value is { } forced ? (int)forced : -1);
    }

    internal static int ActivationAttemptCountForTesting => Volatile.Read(ref _activationAttemptCount);

    internal static bool IsCorrupt
    {
        get
        {
            if (AvailabilityOverrideForTesting is { } forced)
                return forced == PreCombatBridgeAvailability.CorruptOrIncompatible;
            return _snapshot.IsCorrupt;
        }
    }

    internal static void ResetForTesting()
    {
        lock (Sync)
        {
            _snapshot = BridgeAvailabilitySnapshot.Initial;
        }
        Interlocked.Exchange(ref _activationAttemptCount, 0);
        ActivationHookForTesting = null;
        PrewarmHookForTesting = null;
        RunHookForTesting = null;
        RunRawJsonHookForTesting = null;
        AvailabilityOverrideForTesting = null;
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
        if (TryGetFastAvailability(out PreCombatBridgeAvailability fastState, out error))
            return fastState;

        lock (Sync)
        {
            if (TryGetFastAvailability(out PreCombatBridgeAvailability lockedState, out error))
                return lockedState;

            string? bridgeDir = ResolveBridgeDirectory();
            if (bridgeDir is null || !Directory.Exists(bridgeDir))
            {
                error = "The optional sts2sim backend directory is not installed.";
                return PreCombatBridgeAvailability.Absent;
            }

            Interlocked.Increment(ref _activationAttemptCount);
            try
            {
                ActivationHookForTesting?.Invoke();
                ActivatedBridge activated = LoadAndVerifyLocked(bridgeDir);
                _snapshot = new BridgeAvailabilitySnapshot(activated, false, null);
                error = null;
                return PreCombatBridgeAvailability.Ready;
            }
            catch (Exception ex)
            {
                string reason = $"The installed sts2sim backend could not be activated: {ex.GetBaseException().Message}";
                _snapshot = new BridgeAvailabilitySnapshot(null, true, reason);
                error = reason;
                Entry.Logger.Error($"[CombatSolver/PreCombatApi] STS2SIM_BRIDGE_ACTIVATION_FAILED error={ex}");
                return PreCombatBridgeAvailability.CorruptOrIncompatible;
            }
        }
    }

    private static bool TryGetFastAvailability(
        out PreCombatBridgeAvailability availability,
        out string? error)
    {
        if (AvailabilityOverrideForTesting is { } forced)
        {
            error = forced switch
            {
                PreCombatBridgeAvailability.Absent => "The optional sts2sim backend directory is not installed.",
                PreCombatBridgeAvailability.CorruptOrIncompatible => "The installed sts2sim backend is corrupt or incompatible.",
                _ => null,
            };
            availability = forced;
            return true;
        }

        BridgeAvailabilitySnapshot snapshot = _snapshot;
        if (snapshot.Activated is not null)
        {
            error = null;
            availability = PreCombatBridgeAvailability.Ready;
            return true;
        }
        if (snapshot.IsCorrupt)
        {
            error = snapshot.CorruptReason;
            availability = PreCombatBridgeAvailability.CorruptOrIncompatible;
            return true;
        }

        error = null;
        availability = default;
        return false;
    }

    public static void Prewarm()
    {
        PrewarmHookForTesting?.Invoke();
        PreCombatBridgeAvailability state = EnsureActivated(out string? error);
        if (state == PreCombatBridgeAvailability.Absent)
            throw new InvalidOperationException(error ?? "Sts2Sim backend is absent.");
        if (state == PreCombatBridgeAvailability.CorruptOrIncompatible)
            throw new InvalidOperationException(error ?? "Sts2Sim backend is corrupt or incompatible.");
        if (_snapshot.Activated is { } activated)
            activated.Prewarm();
        else if (AvailabilityOverrideForTesting != PreCombatBridgeAvailability.Ready)
            throw new InvalidOperationException(error ?? "Sts2Sim backend is corrupt or incompatible.");
    }

    public static async Task<Sts2SimBridgeResponseDto> RunAsync(
        Sts2SimBridgeRequestDto request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (RunRawJsonHookForTesting is { } rawHook)
        {
            byte[] rawBytes = await rawHook(request, cancellationToken).ConfigureAwait(false);
            return PreCombatSts2SimWireValidator.DeserializeAndValidateResponse(rawBytes);
        }
        if (RunHookForTesting is { } runHook)
        {
            Sts2SimBridgeResponseDto hooked = await runHook(request, cancellationToken).ConfigureAwait(false);
            PreCombatSts2SimWireValidator.ValidateResponse(hooked);
            return hooked;
        }
        PreCombatBridgeAvailability state = EnsureActivated(out string? error);
        if (state != PreCombatBridgeAvailability.Ready || _snapshot.Activated is not { } activated)
            throw new InvalidOperationException(error ?? $"Sts2Sim backend is not ready ({state}).");

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(request, PreCombatSts2SimAbi.JsonOptions);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] responseBytes = await activated.RunJsonAsync(payload, cancellationToken).ConfigureAwait(false);
        return PreCombatSts2SimWireValidator.DeserializeAndValidateResponse(responseBytes);
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

        Dictionary<string, byte[]> verifiedAssemblies = new(StringComparer.OrdinalIgnoreCase);
        foreach (Sts2SimBundleFileManifestEntry entry in manifest.Files)
        {
            string fullPath = Path.Combine(bridgeDir, entry.Name);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException($"Bridge bundle payload missing: {fullPath}", fullPath);
            byte[] fileBytes = File.ReadAllBytes(fullPath);
            string actualSha256 = Convert.ToHexString(SHA256.HashData(fileBytes));
            if (!string.Equals(actualSha256, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Bridge bundle SHA-256 mismatch for '{entry.Name}': expected {entry.Sha256}, actual {actualSha256}.");
            }
            if (entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                verifiedAssemblies[Path.GetFileNameWithoutExtension(entry.Name)] = fileBytes;
            }
        }

        AssemblyLoadContext hostContext = AssemblyLoadContext.GetLoadContext(typeof(PreCombatSts2SimBridge).Assembly)
            ?? AssemblyLoadContext.Default;
        Sts2SimBundleFileManifestEntry? stagedHashing = manifestEntries.GetValueOrDefault($"{HashingAssemblySimpleName}.dll");
        if (File.Exists(Path.Combine(bridgeDir, $"{HashingAssemblySimpleName}.dll")) && stagedHashing is null)
            throw new InvalidDataException("Unmanifested System.IO.Hashing.dll is present in sts2sim directory.");

        var loadContext = new IsolatedBridgeLoadContext(bridgeDir, verifiedAssemblies, hostContext, stagedHashing);
        Assembly bridgeAssembly = loadContext.LoadFromAssemblyName(new AssemblyName(BridgeAssemblySimpleName));
        Type entryType = bridgeAssembly.GetType(BridgeEntryTypeName, throwOnError: true)
            ?? throw new InvalidOperationException($"Missing entry type {BridgeEntryTypeName}.");

        Func<int> getAbiVersion = CreateDelegate<Func<int>>(entryType, "GetAbiVersion");
        Func<string> getBundleIdentityJson = CreateDelegate<Func<string>>(entryType, "GetBundleIdentityJson");
        Action prewarm = CreateDelegate<Action>(entryType, "Prewarm");
        Func<byte[], CancellationToken, Task<byte[]>> runJsonAsync =
            CreateDelegate<Func<byte[], CancellationToken, Task<byte[]>>>(entryType, "RunJsonAsync");

        int actualAbi = getAbiVersion();
        if (actualAbi != PreCombatSts2SimAbi.Version)
        {
            throw new InvalidDataException(
                $"Bridge runtime ABI mismatch: expected {PreCombatSts2SimAbi.Version}, got {actualAbi}.");
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

        return new ActivatedBridge(loadContext, prewarm, runJsonAsync);
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

    private sealed class ActivatedBridge(
        IsolatedBridgeLoadContext loadContext,
        Action prewarm,
        Func<byte[], CancellationToken, Task<byte[]>> runJsonAsync)
    {
        public IsolatedBridgeLoadContext LoadContext { get; } = loadContext;
        public Action Prewarm { get; } = prewarm;
        public Func<byte[], CancellationToken, Task<byte[]>> RunJsonAsync { get; } = runJsonAsync;
    }

    private sealed class IsolatedBridgeLoadContext(
        string bridgeDir,
        IReadOnlyDictionary<string, byte[]> verifiedAssemblies,
        AssemblyLoadContext hostContext,
        Sts2SimBundleFileManifestEntry? stagedHashing)
        : AssemblyLoadContext(name: "CombatSolver.PreCombatSts2Sim", isCollectible: false)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            string? simpleName = assemblyName.Name;
            if (string.IsNullOrEmpty(simpleName))
                return null;

            if (simpleName.StartsWith("Sts2Sim.", StringComparison.Ordinal))
            {
                if (simpleName.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
                    return null;
                if (!verifiedAssemblies.TryGetValue(simpleName, out byte[]? verifiedBytes))
                {
                    throw new FileNotFoundException(
                        $"Private Sts2Sim dependency '{simpleName}' could not be resolved from verified manifest in '{bridgeDir}'.",
                        Path.Combine(bridgeDir, $"{simpleName}.dll"));
                }
                return LoadFromStream(new MemoryStream(verifiedBytes, writable: false));
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
                if (stagedHashing is null || !verifiedAssemblies.TryGetValue(HashingAssemblySimpleName, out byte[]? privateBytes))
                {
                    throw new FileNotFoundException(
                        "Compatible System.IO.Hashing (>= 9.0.0) was not found in host context or sts2sim manifest.",
                        privatePath);
                }
                Assembly privateAssembly = LoadFromStream(new MemoryStream(privateBytes, writable: false));
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
