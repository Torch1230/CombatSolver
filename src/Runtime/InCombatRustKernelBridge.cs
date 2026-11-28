using System.Runtime.InteropServices;
using System.Text.Json;

namespace CombatSolver;

internal static class InCombatRustKernelBridge
{
    private const string KernelFolderName = "rust-kernel";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint AbiVersionDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CapabilitiesDelegate(out nint outPtr, out nuint outLen);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint CancelTokenCreateDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CancelTokenCancelDelegate(nint token);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CancelTokenFreeDelegate(nint token);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SolveInCombatHostV1Delegate(
        nint reqPtr,
        nuint reqLen,
        nint cancelToken,
        nint progressCb,
        nint userData,
        out nint outPtr,
        out nuint outLen);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeBufferDelegate(nint ptr, nuint len);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NativeProgressCallback(nint ptr, nuint len, nint userData);

    private sealed class ActivatedRustKernel(
        nint libraryHandle,
        AbiVersionDelegate abiVersion,
        CapabilitiesDelegate capabilities,
        CancelTokenCreateDelegate cancelTokenCreate,
        CancelTokenCancelDelegate cancelTokenCancel,
        CancelTokenFreeDelegate cancelTokenFree,
        SolveInCombatHostV1Delegate solveHostV1,
        FreeBufferDelegate freeBuffer)
    {
        public nint LibraryHandle { get; } = libraryHandle;
        public AbiVersionDelegate AbiVersion { get; } = abiVersion;
        public CapabilitiesDelegate Capabilities { get; } = capabilities;
        public CancelTokenCreateDelegate CancelTokenCreate { get; } = cancelTokenCreate;
        public CancelTokenCancelDelegate CancelTokenCancel { get; } = cancelTokenCancel;
        public CancelTokenFreeDelegate CancelTokenFree { get; } = cancelTokenFree;
        public SolveInCombatHostV1Delegate SolveHostV1 { get; } = solveHostV1;
        public FreeBufferDelegate FreeBuffer { get; } = freeBuffer;
    }

    private sealed record KernelAvailabilitySnapshot(
        ActivatedRustKernel? Activated,
        bool IsCorrupt,
        string? CorruptReason)
    {
        public static readonly KernelAvailabilitySnapshot Initial = new(null, false, null);
    }

    private sealed record NativeCapabilitiesProbeDto(
        int AbiVersion,
        string? EngineId,
        string? SupportedGameVersion);

    private static readonly object Sync = new();
    private static volatile KernelAvailabilitySnapshot _snapshot = KernelAvailabilitySnapshot.Initial;

    internal static string? ModDirectoryOverrideForTesting { get; set; }

    internal static Func<RustKernelRequestDto, Action<RustKernelProgressDto>?, CancellationToken, Task<RustKernelResponseDto>>?
        RunInCombatHookForTesting { get; set; }

    internal static bool IsCorrupt => _snapshot.IsCorrupt;

    internal static string NativeLibraryFileName =>
        OperatingSystem.IsWindows() ? "sts2_combat_rs.dll" : "libsts2_combat_rs.so";

    internal static void ResetForTesting()
    {
        lock (Sync)
        {
            if (_snapshot.Activated is { LibraryHandle: not 0 } active)
                NativeLibrary.Free(active.LibraryHandle);
            _snapshot = KernelAvailabilitySnapshot.Initial;
        }
        RunInCombatHookForTesting = null;
    }

    public static bool HasInstalledBackendArtifact()
    {
        if (RunInCombatHookForTesting is not null)
            return true;
        string? kernelDir = ResolveKernelDirectory();
        if (kernelDir is null)
            return false;
        return Directory.Exists(kernelDir) || File.Exists(Path.Combine(kernelDir, NativeLibraryFileName));
    }

    public static RustKernelAvailability EnsureActivated(out string? error)
    {
        if (RunInCombatHookForTesting is not null)
        {
            error = null;
            return RustKernelAvailability.Ready;
        }

        if (TryGetFastAvailability(out RustKernelAvailability fastState, out error))
            return fastState;

        lock (Sync)
        {
            if (TryGetFastAvailability(out RustKernelAvailability lockedState, out error))
                return lockedState;

            string? kernelDir = ResolveKernelDirectory();
            if (kernelDir is null || !Directory.Exists(kernelDir))
            {
                error = "The rust-kernel directory is not installed.";
                return RustKernelAvailability.Absent;
            }

            string libPath = Path.Combine(kernelDir, NativeLibraryFileName);
            nint handle = 0;
            try
            {
                if (!File.Exists(libPath))
                    throw new FileNotFoundException($"Missing Rust combat kernel library: {libPath}", libPath);

                if (!NativeLibrary.TryLoad(libPath, out handle) || handle == 0)
                    throw new DllNotFoundException($"NativeLibrary.TryLoad failed for '{libPath}'.");

                var abiVersion = ResolveExport<AbiVersionDelegate>(handle, "sts2_rs_abi_version");
                var capabilities = ResolveExport<CapabilitiesDelegate>(handle, "sts2_rs_capabilities");
                var cancelCreate = ResolveExport<CancelTokenCreateDelegate>(handle, "sts2_rs_cancel_token_create");
                var cancelCancel = ResolveExport<CancelTokenCancelDelegate>(handle, "sts2_rs_cancel_token_cancel");
                var cancelFree = ResolveExport<CancelTokenFreeDelegate>(handle, "sts2_rs_cancel_token_free");
                var solveHostV1 = ResolveExport<SolveInCombatHostV1Delegate>(handle, "sts2_rs_solve_in_combat_host_v1");
                var freeBuffer = ResolveExport<FreeBufferDelegate>(handle, "sts2_rs_free_buffer");

                uint actualAbi = abiVersion();
                if (actualAbi != RustKernelAbi.Version)
                {
                    throw new InvalidDataException(
                        $"Rust combat kernel ABI mismatch: expected {RustKernelAbi.Version}, got {actualAbi}.");
                }

                nint capPtr = 0;
                nuint capLen = 0;
                int capRc;
                try
                {
                    capRc = capabilities(out capPtr, out capLen);
                    if (capRc != 0 || capPtr == 0 || capLen == 0)
                        throw new InvalidDataException($"Rust combat kernel capabilities probe failed with rc={capRc}.");
                    byte[] capBytes = new byte[checked((int)capLen)];
                    Marshal.Copy(capPtr, capBytes, 0, capBytes.Length);
                    NativeCapabilitiesProbeDto? probe = JsonSerializer.Deserialize<NativeCapabilitiesProbeDto>(
                        capBytes, RustKernelAbi.JsonOptions);
                    if (probe is null
                        || probe.AbiVersion != RustKernelAbi.Version
                        || !string.Equals(probe.EngineId, "sts2-combat-rs", StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("Rust combat kernel reported incompatible capabilities metadata.");
                    }
                }
                finally
                {
                    if (capPtr != 0)
                        freeBuffer(capPtr, capLen);
                }

                var activated = new ActivatedRustKernel(
                    handle,
                    abiVersion,
                    capabilities,
                    cancelCreate,
                    cancelCancel,
                    cancelFree,
                    solveHostV1,
                    freeBuffer);
                _snapshot = new KernelAvailabilitySnapshot(activated, false, null);
                error = null;
                return RustKernelAvailability.Ready;
            }
            catch (Exception ex)
            {
                if (handle != 0)
                    NativeLibrary.Free(handle);
                string reason = $"The installed rust-kernel library could not be activated: {ex.GetBaseException().Message}";
                _snapshot = new KernelAvailabilitySnapshot(null, true, reason);
                error = reason;
                Entry.Logger.Error($"[CombatSolver/InCombatRustKernel] RUST_KERNEL_ACTIVATION_FAILED error={ex}");
                return RustKernelAvailability.CorruptOrIncompatible;
            }
        }
    }

    public static async Task<RustKernelResponseDto> RunInCombatAsync(
        RustKernelRequestDto request,
        Action<RustKernelProgressDto>? onProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (RunInCombatHookForTesting is { } hook)
        {
            RustKernelWireValidator.ValidatePolicy(request.Policy);
            RustKernelResponseDto hooked = await hook(request, onProgress, cancellationToken).ConfigureAwait(false);
            RustKernelWireValidator.ValidateResponse(hooked);
            return hooked;
        }

        RustKernelAvailability state = EnsureActivated(out string? error);
        if (state != RustKernelAvailability.Ready || _snapshot.Activated is not { } activated)
            throw new InvalidOperationException(error ?? $"Rust combat kernel is not ready ({state}).");

        RustKernelWireValidator.ValidatePolicy(request.Policy);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(request, RustKernelAbi.JsonOptions);
        cancellationToken.ThrowIfCancellationRequested();

        nint cancelToken = activated.CancelTokenCreate();
        if (cancelToken == 0)
            throw new InvalidOperationException("Failed to allocate Rust combat kernel cancellation token.");

        int tokenFreed = 0;
        CancellationTokenRegistration cancelReg = default;
        Exception? callbackException = null;
        NativeProgressCallback? progressDelegate = null;
        nint progressPtr = 0;

        if (onProgress is not null)
        {
            progressDelegate = (ptr, len, _) =>
            {
                if (Volatile.Read(ref callbackException) is not null || ptr == 0 || len == 0)
                    return;
                try
                {
                    byte[] bytes = new byte[checked((int)len)];
                    Marshal.Copy(ptr, bytes, 0, bytes.Length);
                    RustKernelProgressDto progress =
                        RustKernelWireValidator.DeserializeAndValidateProgress(bytes);
                    onProgress(progress);
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref callbackException, ex, null);
                }
            };
            progressPtr = Marshal.GetFunctionPointerForDelegate(progressDelegate);
        }

        nint outPtr = 0;
        nuint outLen = 0;
        byte[] responseBytes;
        try
        {
            cancelReg = cancellationToken.Register(() =>
            {
                if (Volatile.Read(ref tokenFreed) == 0)
                    activated.CancelTokenCancel(cancelToken);
            });
            if (cancellationToken.IsCancellationRequested)
                activated.CancelTokenCancel(cancelToken);

            int rc;
            unsafe
            {
                fixed (byte* reqPtr = payload)
                {
                    rc = activated.SolveHostV1(
                        (nint)reqPtr,
                        (nuint)payload.Length,
                        cancelToken,
                        progressPtr,
                        0,
                        out outPtr,
                        out outLen);
                }
            }
            GC.KeepAlive(progressDelegate);
            if (rc != 0 || outPtr == 0 || outLen == 0)
                throw new InvalidOperationException($"Rust combat kernel solve call failed with rc={rc}.");

            responseBytes = new byte[checked((int)outLen)];
            Marshal.Copy(outPtr, responseBytes, 0, responseBytes.Length);
        }
        finally
        {
            if (outPtr != 0)
                activated.FreeBuffer(outPtr, outLen);
            cancelReg.Dispose();
            Interlocked.Exchange(ref tokenFreed, 1);
            activated.CancelTokenFree(cancelToken);
        }

        if (callbackException is not null)
        {
            throw new InvalidDataException(
                $"Rust combat kernel progress callback failed: {callbackException.Message}",
                callbackException);
        }

        return RustKernelWireValidator.DeserializeAndValidateResponse(responseBytes);
    }

    private static bool TryGetFastAvailability(
        out RustKernelAvailability availability,
        out string? error)
    {
        KernelAvailabilitySnapshot snapshot = _snapshot;
        if (snapshot.Activated is not null)
        {
            error = null;
            availability = RustKernelAvailability.Ready;
            return true;
        }
        if (snapshot.IsCorrupt)
        {
            error = snapshot.CorruptReason;
            availability = RustKernelAvailability.CorruptOrIncompatible;
            return true;
        }
        error = null;
        availability = default;
        return false;
    }

    private static TDelegate ResolveExport<TDelegate>(nint libraryHandle, string exportName)
        where TDelegate : Delegate
    {
        if (!NativeLibrary.TryGetExport(libraryHandle, exportName, out nint addr) || addr == 0)
            throw new MissingMethodException("sts2_combat_rs", exportName);
        return Marshal.GetDelegateForFunctionPointer<TDelegate>(addr);
    }

    private static string? ResolveKernelDirectory()
    {
        if (!string.IsNullOrWhiteSpace(ModDirectoryOverrideForTesting))
            return Path.Combine(ModDirectoryOverrideForTesting, KernelFolderName);
        string location = typeof(InCombatRustKernelBridge).Assembly.Location;
        if (string.IsNullOrWhiteSpace(location))
            return null;
        string? modDir = Path.GetDirectoryName(location);
        return string.IsNullOrWhiteSpace(modDir) ? null : Path.Combine(modDir, KernelFolderName);
    }
}
