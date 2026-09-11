using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Extensions;
using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    // A read-only inventory of the complete original input and generation roots.
    // Type membership is reported separately from full state/hook admission.
    private void AssertCompactGenerationClosureAudit(CombatState combat, Player player)
    {
        var encounter = combat.Encounter ?? throw new InvalidOperationException("Generation audit requires an encounter.");
        if (player.Character.Id.Entry != "NECROBINDER" || encounter.Id.Entry != "AEONGLASS_BOSS"
            || player.Deck.Cards.Count != 38 || _request.Relics.Length != 19
            || _request.Relics.Any(injection => !player.Relics.Any(relic => relic.Id.Entry == injection.RelicId))
            || player.PotionSlots.Count(potion => potion != null) != 2)
            throw new InvalidOperationException($"Generation closure audit input differs: character={player.Character.Id.Entry}; "
                + $"encounter={encounter.Id.Entry}; deck={player.Deck.Cards.Count}; relics={player.Relics.Count}; "
                + $"potions={player.PotionSlots.Count(potion => potion != null)}.");
        var enemy = combat.Enemies.Single();
        var before = CaptureActual(combat, player, enemy);
        var root = CombatRootSnapshot.Capture(combat);
        var simulator = root.ForkSimulator();
        var constraint = combat.RunState.CardMultiplayerConstraint;
        if (constraint != CardMultiplayerConstraint.SingleplayerOnly)
            throw new InvalidOperationException("Generation audit only supports its original single-player input.");
        var admitted = (HashSet<Type>)typeof(CompactCardProgramCompiler)
            .GetField("AdmittedTypes", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var callOptions = player.Character.CardPool.GetUnlockedCards(player.UnlockState, constraint)
            .Where(card => card.Rarity is not (CardRarity.Basic or CardRarity.Ancient)).ToArray();
        var colorlessOptions = ModelDb.CardPool<ColorlessCardPool>()
            .GetUnlockedCards(player.UnlockState, constraint).ToArray();
        var poolSnapshot = (ICombatPredictionCardGenerationPoolSnapshot)simulator.State.CombatState;
        var forkPoolSnapshot = (ICombatPredictionCardGenerationPoolSnapshot)simulator.Fork().State.CombatState;
        if (!poolSnapshot.TryGetRootEligibleCharacterCards(player, player.Character.CardPool, constraint, out var rootEligible)
            || !forkPoolSnapshot.TryGetRootEligibleCharacterCards(player, player.Character.CardPool, constraint, out var forkEligible)
            || !ReferenceEquals(rootEligible, forkEligible)
            || !simulator.TryGetRootEligibleCharacterCardsForCombat(player, constraint, out var productionEligible)
            || !ReferenceEquals(rootEligible, productionEligible)
            || !rootEligible.SequenceEqual(callOptions.FilterForCombatAndPlayerCount(constraint)))
            throw new InvalidOperationException("Full character pool lost its root order, identities or shared ownership.");
        foreach (var rejectedPool in new[] { player.Character.CardPool.ToMutable(), ModelDb.CardPool<ModCharacterPoolProbe>(), ModelDb.CardPool<ColorlessCardPool>() })
            if (poolSnapshot.TryGetRootEligibleCharacterCards(player, rejectedPool, constraint, out _))
                throw new InvalidOperationException("Full character pool accepted a mutable, custom or different pool.");
        if (poolSnapshot.TryGetRootEligibleCharacterCards(player, player.Character.CardPool, CardMultiplayerConstraint.MultiplayerOnly, out _))
            throw new InvalidOperationException("Full character pool accepted a different player-count constraint.");
        List<object> pools = [];
        foreach (var source in new[] { (Name: "CallOfTheVoid", Cards: callOptions), (Name: "ColorlessPotion", Cards: colorlessOptions) })
        {
            // Native factory filters player count, then combat eligibility, preserving order.
            var nativeEligible = CardFactory.FilterForCombat(source.Cards.Where(card =>
                card.MultiplayerConstraint != CardMultiplayerConstraint.MultiplayerOnly)).ToArray();
            var legacyEligible = source.Cards.FilterForCombatAndPlayerCount(constraint).ToArray();
            if (!nativeEligible.SequenceEqual(legacyEligible) || nativeEligible.Length == 0)
                throw new InvalidOperationException($"Generation pool order/filter mismatch: {source.Name}.");
            List<object> samples = [];
            using (SimulationNotificationIsolation.Enter())
            {
                var nativeRng = simulator.Rng.CombatCardGeneration.Clone();
                var modelRng = simulator.Rng.CombatCardGeneration.Clone();
                var cachedRng = simulator.Rng.CombatCardGeneration.Clone();
                var initial = nativeRng.CaptureState();
                var valueRng = new ValueRng(initial.Counter, initial.State0, initial.State1, initial.State2, initial.State3);
                int[] selectionScratch = new int[nativeEligible.Length];
                for (int sample = 0; sample < 12; sample++)
                {
                    int requested = source.Name == "ColorlessPotion" ? 3 : 1;
                    var start = nativeRng.CaptureState();
                    var selected = nativeEligible.TakeRandom(requested, nativeRng).ToArray();
                    valueRng = valueRng.TakeDistinctIndices(nativeEligible.Length, requested, selectionScratch, out int valueCount);
                    var predicted = source.Cards.GetDistinctForCombat(player, requested, modelRng, constraint).ToArray();
                    var cached = source.Name == "CallOfTheVoid"
                        ? rootEligible.GetDistinctForCombat(player, requested, cachedRng, constraint).ToArray()
                        : simulator.GetDistinctUnlockedColorlessForCombat(player, requested, cachedRng, constraint).ToArray();
                    if (!selected.Select(card => card.Id).SequenceEqual(predicted.Select(card => card.Preview.Id))
                        || !SameFiveFieldRngState(nativeRng.CaptureState(), modelRng.CaptureState())
                        || !SameFiveFieldRngState(nativeRng.CaptureState(), cachedRng.CaptureState())
                        || !SameFiveFieldRngState(nativeRng.CaptureState(),
                            new(valueRng.Counter, valueRng.State0, valueRng.State1, valueRng.State2, valueRng.State3))
                        || selected.Length != valueCount
                        || selected.Where((card, index) => !ReferenceEquals(card, nativeEligible[selectionScratch[index]])).Any()
                        || predicted.Length != cached.Length
                        || predicted.Where((card, index) => ReferenceEquals(card.Original, cached[index].Original)
                            || CombatBeamSolver.CaptureCardStateFingerprintForTesting(card)
                                != CombatBeamSolver.CaptureCardStateFingerprintForTesting(cached[index])).Any())
                        throw new InvalidOperationException($"Generation selection/RNG mismatch: {source.Name}/{sample}.");
                    if (sample == 0)
                    {
                        var canonical = selected[0];
                        int originalCount = canonical.BaseReplayCount, cachedCount = cached[0].Preview.BaseReplayCount;
                        predicted[0].MutablePreview.BaseReplayCount++;
                        if (canonical.BaseReplayCount != originalCount || cached[0].Preview.BaseReplayCount != cachedCount)
                            throw new InvalidOperationException("Generated card mutation escaped its branch.");
                    }
                    samples.Add(new { requested, ids = selected.Select(card => card.Id.Entry).ToArray(),
                        counterDelta = nativeRng.CaptureState().Counter - start.Counter });
                }
                AssertValueGenerationSelectionBoundaries(nativeEligible, simulator.Rng.CombatCardGeneration.CaptureState());
            }
            pools.Add(new { source.Name, unlockedSourceCount = source.Cards.Length,
                eligibleCount = nativeEligible.Length,
                exactTypeCount = nativeEligible.Count(card => admitted.Contains(card.GetType())),
                cards = nativeEligible.Select(card => new { id = card.Id.Entry, type = card.GetType().Name,
                    category = card.Type.ToString(), rarity = card.Rarity.ToString(),
                    currentExactType = admitted.Contains(card.GetType()) }).ToArray(), samples });
        }
        using (SimulationNotificationIsolation.Enter())
        {
            AssertRootCharacterAttackGenerationPoolCache(simulator, player);
            AssertRootColorlessGenerationPoolCache(simulator, player);
        }
        bool rootAdmitted;
        string rejection;
        using (SimulationNotificationIsolation.Enter())
            rootAdmitted = CompactCombatRoot.TryCreate(simulator, player, out _, out rejection);
        if (rootAdmitted) throw new InvalidOperationException("Audit unexpectedly admitted the full unrepresented generation closure.");
        AssertSnapshotEqual(before, CaptureActual(combat, player, enemy), "GenerationClosureAudit", "ActualUnchanged");
        if (string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
            throw new InvalidOperationException("Generation audit requires an evidence directory.");
        Directory.CreateDirectory(_request.EvidenceDirectory);
        File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "generation-closure-audit.json"),
            JsonSerializer.Serialize(new { character = player.Character.Id.Entry, encounter = encounter.Id.Entry,
                seed = _request.Seed, deckCount = player.Deck.Cards.Count,
                combatCards = player.PlayerCombatState!.AllCards.Select(card => card.Id.Entry).ToArray(),
                injectedRelicCount = _request.Relics.Length,
                actualRelicCount = player.Relics.Count,
                relics = player.Relics.Select(relic => relic.Id.Entry).ToArray(),
                potions = player.PotionSlots.Select(potion => potion?.Id.Entry).ToArray(),
                rootAdmitted, rejection, pools,
                limitation = "Exact type membership is not full state, hook, or transitive generated-pool admission." },
                new JsonSerializerOptions { WriteIndented = true }));
        _completedChecks.Add($"CompactGenerationClosureAudit:Original38Cards19InjectedRelics2Potions:ActualRelics{player.Relics.Count}:CompleteOrderedPools:24NativeLegacyCachedValueSamples:ValueSelectionEmptySingletonNegativeZeroOversize:FiveFieldRng:FullPoolForkOwnership:InvalidPoolsRejected:GeneratedCardIsolation:AttackAndColorlessRegression:ActualUnchanged:FullRootExplicitlyRejected");
    }

    private static void AssertValueGenerationSelectionBoundaries(CardModel[] fullPool, PredictionRngState initial)
    {
        foreach (int size in new[] { 0, 1, 2, fullPool.Length })
        foreach (int count in new[] { -1, 0, 1, 3, size, size + 5 }.Distinct())
        {
            var nativeRng = new MegaCrit.Sts2.Core.Random.Rng(0);
            nativeRng.LoadFromSerializable(new() { counter = initial.Counter, state0 = initial.State0,
                state1 = initial.State1, state2 = initial.State2, state3 = initial.State3 });
            var source = fullPool.Take(size).ToArray();
            var selected = source.TakeRandom(count, nativeRng).ToArray();
            int[] scratch = new int[size + 1];
            scratch[size] = 987;
            var valueRng = new ValueRng(initial.Counter, initial.State0, initial.State1, initial.State2, initial.State3)
                .TakeDistinctIndices(size, count, scratch, out int selectedCount);
            if (selected.Length != selectedCount || scratch[size] != 987
                || selected.Where((card, index) => !ReferenceEquals(card, source[scratch[index]])).Any()
                || !SameFiveFieldRngState(nativeRng.CaptureState(),
                    new(valueRng.Counter, valueRng.State0, valueRng.State1, valueRng.State2, valueRng.State3)))
                throw new InvalidOperationException($"Value generation selection mismatch for size={size}, count={count}.");
        }
        int[] invalidScratch = [71, 72];
        bool rejected = false;
        try { _ = new ValueRng().TakeDistinctIndices(3, 1, invalidScratch, out _); }
        catch (ArgumentException) { rejected = true; }
        if (!rejected || !invalidScratch.SequenceEqual(new[] { 71, 72 }))
            throw new InvalidOperationException("Value generation selection modified undersized scratch or failed to reject it.");
        // Every array the metering touches is allocated before the first metered call:
        // the reusable selection scratch plus one record array per phase. The phases
        // themselves only run the shipped selection primitive and its scratch.
        const int SelectionCount = 1;
        const int WarmupCalls = 100;
        const int BlockCalls = 5000;
        const int BlockCount = 5;
        int[] reusable = new int[fullPool.Length];
        long[] warmupAllocations = new long[1];
        long[] stabilizationAllocations = new long[BlockCount];
        long[] steadyStateAllocations = new long[BlockCount];
        var allocationRng = new ValueRng(initial.Counter, initial.State0, initial.State1, initial.State2, initial.State3);
        int firstCounter = allocationRng.Counter;
        allocationRng = RunValueGenerationSelectionBlocks(
            allocationRng, fullPool.Length, SelectionCount, WarmupCalls, warmupAllocations, reusable);
        // The stabilization phase repeats the complete measurement shape - same helper,
        // same block count, same calls per block - so one-time runtime work attached to a
        // measured batch (JIT stubs, tiering, host thread setup) lands before the evidence.
        // It is never passing evidence: its records are not read and its RNG calls are
        // outside both assertions below.
        allocationRng = RunValueGenerationSelectionBlocks(
            allocationRng, fullPool.Length, SelectionCount, BlockCalls, stabilizationAllocations, reusable);
        int beforeCounter = allocationRng.Counter;
        if (beforeCounter - firstCounter != (WarmupCalls + BlockCount * BlockCalls) * (fullPool.Length - 1))
            throw new InvalidOperationException($"Value generation selection warmup/stabilization changed RNG consumption: {beforeCounter - firstCounter}.");
        allocationRng = RunValueGenerationSelectionBlocks(
            allocationRng, fullPool.Length, SelectionCount, BlockCalls, steadyStateAllocations, reusable);
        if (allocationRng.Counter - beforeCounter != BlockCount * BlockCalls * (fullPool.Length - 1))
            throw new InvalidOperationException($"Value generation selection changed RNG consumption: {allocationRng.Counter - beforeCounter}.");
        // A real allocation in the selection would appear in every steady-state block; any
        // nonzero block fails immediately. The one-time host charges were already consumed
        // by the stabilization phase, so no tolerance or byte threshold applies.
        if (steadyStateAllocations.Any(allocated => allocated != 0))
            throw new InvalidOperationException("Value generation selection allocated in the steady state: "
                + $"{string.Join('/', steadyStateAllocations)} bytes; stabilization="
                + $"{string.Join('/', stabilizationAllocations)} bytes.");
    }

    // Pure loop helper. The measured region contains only the shipped selection primitive
    // and its caller-owned scratch: no LINQ, formatting, logging or array creation.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueRng RunValueGenerationSelectionBlocks(ValueRng rng, int population, int count,
        int callsPerBlock, Span<long> blockAllocations, Span<int> scratch)
    {
        for (int block = 0; block < blockAllocations.Length; block++)
        {
            long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
            for (int call = 0; call < callsPerBlock; call++)
                rng = rng.TakeDistinctIndices(population, count, scratch, out _);
            blockAllocations[block] = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
        }
        return rng;
    }
}
