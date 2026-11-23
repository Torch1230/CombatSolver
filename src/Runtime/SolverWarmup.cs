using System.Diagnostics;
using System.Runtime.CompilerServices;
using CombatSolver.HotKernel;
using CombatSolver.HotSearch;

namespace CombatSolver;

/// <summary>
/// Idempotent, low-priority background warmup for pure-C# static catalogs, reflection caches,
/// state packing/hashing, and JSON serializer metadata so first-combat root capture and search
/// startup do not pay cold-init/JIT latency on the Godot main thread.
/// Strictly NEVER touches Godot Nodes, SceneTree, ResourceLoader, ModelDb, LocManager, or live combat state.
/// </summary>
internal static class SolverWarmup
{
    private static readonly Type[] PureStaticTypes =
    [
        // 1. All pure-C# HotCatalog static constructors (Cards, Powers, Relics, Potions, Monsters + Orbs/Enchantments/Afflictions/Dominions)
        typeof(HotCardCatalog),
        typeof(HotPowerCatalog),
        typeof(HotRelicCatalog),
        typeof(HotPotionCatalog),
        typeof(HotMonsterCatalog),
        // 2. Pure-CLR reflection caches (FieldInfo / dictionary init only; ModelDb is lazy and untouched here)
        typeof(HotRootBuilder),
        typeof(HotImportSupport),
        typeof(VanillaRootGate),
        typeof(HotContinuationStamp),
        // 3. HotKernel state & simulation static ctors
        typeof(CombatHotState),
        typeof(HotColdSidecar),
        typeof(HotStatePacker),
        typeof(HotHash),
        typeof(HotPiles),
        typeof(HotCostModifiers),
        typeof(HotRng),
        typeof(HotCombatEngine),
        // 4. HotSearch static ctors
        typeof(HotSearchConstants),
        typeof(HotRouteQuality),
        typeof(HotPotionPolicy),
        typeof(HotEvaluator),
        typeof(HotBeamRetention),
        typeof(HotTranspositionTable),
        typeof(HotNodeArena),
        typeof(HotParallelExpander),
        typeof(HotPlanMaterializer),
        typeof(HotEndTurnRiskEvaluator),
        typeof(HotSearchCoordinator),
        // 5. Runtime / UI pure-C# static ctors
        typeof(CombatRootSnapshot),
        typeof(SolvedRouteCache),
        typeof(SolverText),
    ];

    private static int _started;

    public static void StartBackgroundWarmup()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            return;

        _ = Task.Run(RunWarmup);
    }

    private static void RunWarmup()
    {
        Thread current = Thread.CurrentThread;
        ThreadPriority previousPriority = current.Priority;
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            current.Priority = ThreadPriority.BelowNormal;

            int warmedTypes = 0;
            for (int i = 0; i < PureStaticTypes.Length; i++)
            {
                RuntimeHelpers.RunClassConstructor(PureStaticTypes[i].TypeHandle);
                warmedTypes++;
            }

            // 6. Lightweight 0-entity pure-C# Pack / Hash / Unpack JIT dry run
            CombatHotState dummy = default;
            HotColdSidecar? cold = null;
            int packedSize = HotStatePacker.ComputePackedSize(in dummy);
            byte[] buffer = new byte[packedSize];
            int packedLength = HotStatePacker.Pack(ref dummy, ref cold, buffer);
            HotHash128 hash = HotHash.ComputeHash(buffer.AsSpan(0, packedLength), cold);
            CombatHotState unpacked = default;
            HotStatePacker.Unpack(buffer.AsSpan(0, packedLength), ref unpacked);
            bool roundTripEqual = HotHash.PackedEquals(
                buffer.AsSpan(0, packedLength),
                null,
                buffer.AsSpan(0, packedLength),
                null);

            // 7. Pre-compile System.Text.Json metadata for SolvedRouteCache + English UI dictionary
            SolvedRouteCache.WarmUpSerializer();
            SolverText.WarmUp();

            stopwatch.Stop();
            Entry.Logger?.Info(
                $"[CombatSolver/Warmup] DONE elapsed_ms={stopwatch.Elapsed.TotalMilliseconds:F1} " +
                $"types={warmedTypes} pack_bytes={packedLength} hash={hash.Lo:X8} roundtrip={roundTripEqual}");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Entry.Logger?.Warn(
                $"[CombatSolver/Warmup] FAILED elapsed_ms={stopwatch.Elapsed.TotalMilliseconds:F1} error={ex}");
        }
        finally
        {
            current.Priority = previousPriority;
        }
    }
}
