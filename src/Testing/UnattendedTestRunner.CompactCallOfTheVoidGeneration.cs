using System.Text.Json;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Extensions;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    // Native CallOfTheVoid selects one card at a time from the frozen 78-candidate
    // character pool. This fixture replays three native batches and proves the compact
    // value-layer GenerateFromPool reproduces their selection order, ethereal batch,
    // per-instance identity, hand overflow, five-field RNG and undo behavior. It also
    // drives the production compiler and records the complete pool closure census.
    private async Task AssertCompactCallOfTheVoidGenerationAsync(CombatState combat, Player player)
    {
        if (player.Character.Id.Entry != "NECROBINDER")
            throw new InvalidOperationException("Compact CallOfTheVoid generation requires its native character pool.");
        var enemy = combat.Enemies.Single();
        var power = await PowerCmd.Apply<CallOfTheVoidPower>(
            new BlockingPlayerChoiceContext(), player.Creature, 4, player.Creature, null)
            ?? throw new InvalidOperationException("Compact CallOfTheVoid fixture did not apply its power.");
        var originalCards = player.PlayerCombatState!.AllCards.ToHashSet();
        var root = CombatRootSnapshot.Capture(combat);
        var simulator = root.ForkSimulator();
        if (!simulator.TryGetRootEligibleCharacterCardsForCombat(player, combat.RunState.CardMultiplayerConstraint, out var rootEligible)
            || rootEligible.Count != 78)
            throw new InvalidOperationException("Compact CallOfTheVoid fixture expected the full frozen 78-candidate pool; "
                + $"got {rootEligible.Count}.");
        object compilation = AssertCallOfTheVoidCompilation(player, rootEligible);
        var initial = simulator.Rng.CombatCardGeneration.CaptureState();
        ResumableDiscardProgram lane;
        ResumableDiscardProgram.Candidate initialFreeze;
        ReversibleValueState.Checkpoint mark;
        using (SimulationNotificationIsolation.Enter())
        {
            // Card 0 generates; cards 1 and 2 are retrievers that return the generator to
            // hand so batches 2 and 3 replay the same generation path. Template definition
            // index i + 3 binds rootEligible[i].
            var generator = new ResumableDiscardProgram.Card(0,
                new([new CardInstruction(CardInstructionKind.GenerateFromPool, 4, GenerationPool: 0)]));
            var retriever = new ResumableDiscardProgram.Card(0,
                new([new CardInstruction(CardInstructionKind.RetrieveFromDiscard, 1)]));
            var templates = Enumerable.Range(0, rootEligible.Count)
                .Select(_ => new ResumableDiscardProgram.Card(0, CardEffectProgram.Empty, Ethereal: true)).ToArray();
            int definitionCount = 3 + templates.Length;
            var comparisons = Enumerable.Range(0, definitionCount).SelectMany(left => Enumerable.Range(0, definitionCount)
                .Select(right => left.CompareTo(right))).ToArray();
            lane = new ResumableDiscardProgram([generator, retriever, retriever], [[0, 1, 2], [], [], [], []], 0, 0, 0,
                new ValueRng(0, 1, 2, 3, 4), comparisons,
                creatures: [new CreatureVitals(41, 76, 0), new CreatureVitals(526, 526, 0)],
                generationPools: [Enumerable.Range(3, templates.Length).ToArray()],
                cardGenerationRng: new ValueRng(initial.Counter, initial.State0, initial.State1, initial.State2, initial.State3),
                generatedCards: templates);
            mark = lane.State.Mark();
            initialFreeze = lane.Freeze();
        }
        MoveStateSnapshot? firstExpected = null;
        List<int> generatedInstances = [];
        List<int> destinations = [];
        List<object> batches = [];
        for (int batch = 0; batch < 3; batch++)
        {
            string[] compactIds;
            int[] compactDestinations, compactInstanceIds;
            bool compactEthereal;
            using (SimulationNotificationIsolation.Enter())
            {
                int eventStart = lane.EventCount;
                if (batch > 0)
                {
                    // The retriever is plumbing: it returns the generator and consumes no
                    // generation RNG, so only the generated deltas enter the comparison.
                    lane.Begin(batch); lane.Run(); lane.SupplyChoice([0]); lane.Run();
                }
                lane.Begin(0); lane.Run();
                var events = Enumerable.Range(eventStart, lane.EventCount - eventStart).Select(lane.EventAt)
                    .Where(item => item.Kind == ResumableDiscardProgram.EventKind.Generated).ToArray();
                if (events.Length != 4)
                    throw new InvalidOperationException($"Compact CallOfTheVoid batch {batch} generated {events.Length} cards.");
                compactIds = events.Select(item => rootEligible[lane.DefinitionIndex(item.Card) - 3].Id.Entry).ToArray();
                compactDestinations = events.Select(item => item.Flags).ToArray();
                compactInstanceIds = events.Select(item => item.Card).ToArray();
                compactEthereal = events.All(item => lane.IsEthereal(item.Card));
                generatedInstances.AddRange(compactInstanceIds);
                destinations.AddRange(compactDestinations);
            }
            MoveStateSnapshot expected;
            string[] legacyIds;
            using (SimulationNotificationIsolation.Enter())
            {
                var legacyPlayer = simulator.State.GetPlayerCombatState(player);
                int handBefore = legacyPlayer.Hand.Cards.Count, discardBefore = legacyPlayer.DiscardPile.Cards.Count;
                Advance(simulator);
                var shadowPlayer = simulator.State.GetPlayerCombatState(player);
                legacyIds = shadowPlayer.Hand.Cards.Skip(handBefore).Select(card => card.Preview.Id.Entry)
                    .Concat(shadowPlayer.DiscardPile.Cards.Skip(discardBefore).Select(card => card.Preview.Id.Entry)).ToArray();
                expected = CaptureSimulated(simulator, (SimulatedCombatState)simulator.State.CombatState, player, enemy);
                firstExpected ??= expected;
                var fork = simulator.Fork();
                AssertSnapshotEqual(expected,
                    CaptureSimulated(fork, (SimulatedCombatState)fork.State.CombatState, player, enemy),
                    "CompactCallOfTheVoidGeneration", $"Fork{batch}");
            }
            int liveHandBefore = player.PlayerCombatState.Hand.Cards.Count;
            int liveDiscardBefore = player.PlayerCombatState.DiscardPile.Cards.Count;
            await power.BeforeHandDraw(player, new BlockingPlayerChoiceContext(), combat);
            var nativeIds = player.PlayerCombatState.Hand.Cards.Skip(liveHandBefore).Select(card => card.Id.Entry)
                .Concat(player.PlayerCombatState.DiscardPile.Cards.Skip(liveDiscardBefore).Select(card => card.Id.Entry)).ToArray();
            AssertSnapshotEqual(expected, CaptureActual(combat, player, enemy),
                "CompactCallOfTheVoidGeneration", $"NativeBatch{batch}");
            var generationRng = lane.CardGenerationRng
                ?? throw new InvalidOperationException("Compact CallOfTheVoid lane lost its generation stream.");
            var compactRng = new PredictionRngState(generationRng.Counter, generationRng.State0,
                generationRng.State1, generationRng.State2, generationRng.State3);
            var legacyRng = simulator.Rng.CombatCardGeneration.CaptureState();
            var liveRng = player.RunState.Rng.CombatCardGeneration.CaptureState();
            if (!compactIds.SequenceEqual(legacyIds) || !compactIds.SequenceEqual(nativeIds))
                throw new InvalidOperationException($"Compact CallOfTheVoid batch {batch} selection order differs: "
                    + $"compact=[{string.Join(',', compactIds)}] legacy=[{string.Join(',', legacyIds)}] "
                    + $"native=[{string.Join(',', nativeIds)}].");
            if (!SameFiveFieldRngState(legacyRng, compactRng) || !SameFiveFieldRngState(legacyRng, liveRng))
                throw new InvalidOperationException($"Compact CallOfTheVoid batch {batch} five-field RNG differs: "
                    + $"compact={FormatFiveFieldRngState(compactRng)} legacy={FormatFiveFieldRngState(legacyRng)} "
                    + $"native={FormatFiveFieldRngState(liveRng)}.");
            if (compactRng.Counter - initial.Counter != (batch + 1) * 4 * (rootEligible.Count - 1))
                throw new InvalidOperationException($"Compact CallOfTheVoid batch {batch} consumed a different RNG stream.");
            if (!compactEthereal || compactDestinations.Any(destination => destination != (int)ResumableDiscardProgram.Pile.Hand
                    && destination != (int)ResumableDiscardProgram.Pile.Discard))
                throw new InvalidOperationException($"Compact CallOfTheVoid batch {batch} lost its ethereal template or pile destination.");
            batches.Add(new { batch, requested = 4, compactIds, legacyIds, nativeIds, destinations = compactDestinations,
                instanceIds = compactInstanceIds, rngCounterDelta = compactRng.Counter - initial.Counter });
        }
        if (generatedInstances.Count != 12
            || generatedInstances.Where((id, index) => index > 0 && id <= generatedInstances[index - 1]).Any())
            throw new InvalidOperationException("Compact CallOfTheVoid instances are not strictly ascending per-generation identities.");
        if (destinations.Count(destination => destination == (int)ResumableDiscardProgram.Pile.Discard) == 0)
            throw new InvalidOperationException("Compact CallOfTheVoid batches never spilled a generated card into the discard pile.");
        var generated = player.PlayerCombatState.AllCards.Where(card => !originalCards.Contains(card)).ToArray();
        if (generated.Length != 12 || generated.Any(card => !card.Keywords.Contains(CardKeyword.Ethereal))
            || !generated.Any(card => card.Pile?.Type == PileType.Hand)
            || !generated.Any(card => card.Pile?.Type == PileType.Discard))
            throw new InvalidOperationException("Compact CallOfTheVoid fixture did not cover its complete ethereal batch and hand overflow.");
        using (SimulationNotificationIsolation.Enter())
        {
            if (lane.Count(ResumableDiscardProgram.Pile.Hand) != 10
                || lane.Count(ResumableDiscardProgram.Pile.Discard) < 2)
                throw new InvalidOperationException("Compact CallOfTheVoid hand did not cap at ten and spill the rest.");
            var finalFreeze = lane.Freeze();
            lane.State.Rollback(mark);
            if (!lane.State.Freeze().ContentEquals(initialFreeze.Open().State.Freeze()))
                throw new InvalidOperationException("Compact CallOfTheVoid rollback did not restore all five RNG slots, instances and piles.");
            ReplayBatches(lane);
            if (!lane.Freeze().Open().State.Freeze().ContentEquals(finalFreeze.Open().State.Freeze()))
                throw new InvalidOperationException("Compact CallOfTheVoid replay did not reproduce the pre-rollback final state.");
        }
        using (SimulationNotificationIsolation.Enter())
        {
            var frozenReplay = root.ForkSimulator();
            Advance(frozenReplay);
            AssertSnapshotEqual(firstExpected!,
                CaptureSimulated(frozenReplay, (SimulatedCombatState)frozenReplay.State.CombatState, player, enemy),
                "CompactCallOfTheVoidGeneration", "FrozenRootAfterNativeGeneration");
        }
        _completedChecks.Add("CompactCallOfTheVoidGeneration:FullPool78:ThreeBatchesTwelveEthereal:NativeLegacyCompactOrderedIds:FiveFieldRngParity:HandOverflowRetrieveReplay:PerInstanceIdentity:RollbackDeterministicReplay:FrozenRootAfterNative:CompiledPowerInstruction:InnateUpgradeAdmitted:UnrepresentedStateRejected:PoolClosureCensus");
        if (!string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
        {
            Directory.CreateDirectory(_request.EvidenceDirectory);
            File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "compact-call-of-the-void-generation.json"),
                JsonSerializer.Serialize(new { character = player.Character.Id.Entry, encounter = combat.Encounter?.Id.Entry,
                    seed = _request.Seed, poolSize = rootEligible.Count, poolIds = rootEligible.Select(card => card.Id.Entry).ToArray(),
                    compilation, batches }, new JsonSerializerOptions { WriteIndented = true }));
        }

        void Advance(CombatPredictionSimulator target)
        {
            if (TurnStartPowerSupport.TriggerBeforeHandDraw(
                target, (SimulatedCombatState)target.State.CombatState, player, new TurnStartChoiceCursor([])))
                throw new InvalidOperationException("CallOfTheVoid unexpectedly opened a choice.");
        }

        static void ReplayBatches(ResumableDiscardProgram target)
        {
            for (int batch = 0; batch < 3; batch++)
            {
                if (batch > 0) { target.Begin(batch); target.Run(); target.SupplyChoice([0]); target.Run(); }
                target.Begin(0); target.Run();
            }
        }
    }

    // The production compiler must turn the native OnPlay into one ordered value command,
    // admit only the native Innate upgrade and refuse every other instance state. The pool
    // census records exactly how many of the complete frozen candidates are representable,
    // so this fixture never implies that the 78-candidate closure already passes.
    private static object AssertCallOfTheVoidCompilation(Player player, IReadOnlyList<CardModel> pool)
    {
        CardModel canonical = CanonicalModels.Card<CallOfTheVoid>();
        ResumableDiscardProgram.Card program = CompactCardProgramCompiler.Compile(canonical, includeAttacks: true);
        if (program.Effects.Count != 1 || program.Effects[0] is not { Kind: CardInstructionKind.ApplyBasicPower, Power: BasicPowerKind.CallOfTheVoid,
                Target: CardInstructionTarget.Owner, Amount: 1, EnergyXMultiplier: 0 } || !program.Effects.RequiresPowers
            || program.Effects.GeneratesCards || program.Effects.RequiresGenerationRng)
            throw new InvalidOperationException("CallOfTheVoid did not compile to its single ordered Power command.");
        CardModel upgraded = PredictionUtils.CreateCard(canonical, player);
        PredictionUtils.UpgradeCard(upgraded);
        if (!upgraded.IsUpgraded || !upgraded.LocalKeywords.Contains(CardKeyword.Innate))
            throw new InvalidOperationException("CallOfTheVoid upgrade fixture did not add its native Innate keyword.");
        ResumableDiscardProgram.Card upgradedProgram = CompactCardProgramCompiler.Compile(upgraded, includeAttacks: true);
        if (upgradedProgram.Effects.Count != 1 || upgradedProgram.Effects[0] != program.Effects[0])
            throw new InvalidOperationException("CallOfTheVoid upgrade changed its compiled command.");
        CardModel unrepresented = PredictionUtils.CreateCard(canonical, player);
        unrepresented.AddKeyword(CardKeyword.Eternal);
        ExpectCompileRejected(unrepresented);
        ExpectCompileRejected(canonical, includeAttacks: false);

        int admitted = 0, unsupported = 0;
        string firstUnsupported = "";
        List<string> unsupportedIds = [];
        foreach (CardModel candidate in pool)
        {
            try { _ = CompactCardProgramCompiler.Compile(candidate, includeAttacks: true); admitted++; }
            catch (NotSupportedException)
            {
                unsupported++;
                if (firstUnsupported.Length == 0) firstUnsupported = candidate.Id.Entry;
                unsupportedIds.Add(candidate.Id.Entry);
            }
        }
        if (admitted == 0 || !pool.Any(card => card is CallOfTheVoid && CompactIsAdmitted(card)) || unsupported == 0)
            throw new InvalidOperationException("CallOfTheVoid pool census lost its admitted or unsupported candidates.");
        return new { instruction = "ApplyBasicPower(CallOfTheVoid)", powerAmount = program.Effects[0].Amount,
            innateUpgradeAdmitted = true, unrepresentedStateRejected = true, drawDiscardOnlyRejected = true,
            poolSize = pool.Count, admitted, unsupported, firstUnsupported, unsupportedIds };

        static bool CompactIsAdmitted(CardModel card)
        {
            try { _ = CompactCardProgramCompiler.Compile(card, includeAttacks: true); return true; }
            catch (NotSupportedException) { return false; }
        }

        static void ExpectCompileRejected(CardModel card, bool includeAttacks = true)
        {
            try { _ = CompactCardProgramCompiler.Compile(card, includeAttacks); }
            catch (NotSupportedException) { return; }
            throw new InvalidOperationException($"CallOfTheVoid state {card.LocalKeywords.Count} was not explicitly rejected.");
        }
    }

    // The complete production root closure. The control uses the original complete Silent
    // opening that already passes every other admission check; applying the native Power
    // afterwards must keep the same root rejected at the first unrepresented candidate of
    // the frozen character pool instead of shrinking that pool to the supported types.
    private async Task AssertCompactCallOfTheVoidAdmissionAsync(CombatState combat, Player player)
    {
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        var constraint = combat.RunState.CardMultiplayerConstraint;
        var control = CombatRootSnapshot.Capture(combat);
        using (SimulationNotificationIsolation.Enter())
        {
            if (!CompactCombatRoot.TryCreate(control.ForkSimulator(), player, out _, out string controlRejection))
                throw new InvalidOperationException($"CallOfTheVoid admission control root was rejected: {controlRejection}");
        }
        var censusSimulator = control.ForkSimulator();
        if (!censusSimulator.TryGetRootEligibleCharacterCardsForCombat(player, constraint, out var pool) || pool.Count == 0)
            throw new InvalidOperationException("CallOfTheVoid admission fixture lost the frozen character pool.");
        int admitted = 0, unsupported = 0, firstUnsupported = -1;
        for (int index = 0; index < pool.Count; index++)
        {
            try { _ = CompactCardProgramCompiler.Compile(pool[index], includeAttacks: true); admitted++; }
            catch (NotSupportedException)
            {
                unsupported++;
                if (firstUnsupported < 0) firstUnsupported = index;
            }
        }
        if (firstUnsupported < 0)
            throw new InvalidOperationException("CallOfTheVoid admission fixture requires an incomplete character pool closure.");
        var power = await PowerCmd.Apply<CallOfTheVoidPower>(
            new BlockingPlayerChoiceContext(), player.Creature, 4, player.Creature, null)
            ?? throw new InvalidOperationException("CallOfTheVoid admission fixture did not apply its power.");
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        var captured = CombatRootSnapshot.Capture(combat);
        var liveAfterPower = CaptureActual(combat, player, combat.Enemies.Single());
        bool admittedRoot;
        string rejection;
        using (SimulationNotificationIsolation.Enter())
            admittedRoot = CompactCombatRoot.TryCreate(captured.ForkSimulator(), player, out _, out rejection);
        if (admittedRoot)
            throw new InvalidOperationException("CallOfTheVoid root was admitted while its frozen character pool is not closed.");
        string rejectedId = ParseCompileRejection(rejection);
        if (rejectedId != pool[firstUnsupported].Id.Entry)
            throw new InvalidOperationException($"CallOfTheVoid root rejected {rejectedId} instead of the first unrepresented frozen candidate "
                + $"{pool[firstUnsupported].Id.Entry}.");
        CardModel[] rootCards = captured.ForkSimulator().State.GetPlayerCombatState(player).AllCards.Select(card => card.Original).ToArray();
        if (rootCards.Any(card => card.Id.Entry == rejectedId))
            throw new InvalidOperationException("CallOfTheVoid rejection attributed the pool candidate to a root card.");
        // The generation pool only exists with the admitted round closure, so a single-turn
        // projection refuses the same root before it can silently drop the turn-start effect.
        using (SimulationNotificationIsolation.Enter())
        {
            try
            {
                _ = new CompactDiscardProjection(captured.ForkSimulator(), player, includeAttacks: true);
                throw new InvalidOperationException("Non-round CallOfTheVoid projection did not reject its unmodeled turn start.");
            }
            catch (NotSupportedException error) when (error.Message.Contains("round closure", StringComparison.Ordinal)) { }
        }
        using (SimulationNotificationIsolation.Enter())
            AssertSnapshotEqual(liveAfterPower, CaptureActual(combat, player, combat.Enemies.Single()),
                "CompactCallOfTheVoidAdmission", "ActualUnchanged");
        _completedChecks.Add($"CompactCallOfTheVoidAdmission:ControlRootAdmitted:Pool{pool.Count}:Admitted{admitted}:Unsupported{unsupported}"
            + $":FirstUnsupported{pool[firstUnsupported].Id.Entry}:PoolCandidateRejection:RootCardNotBlamed:NonRoundRejected:ActualUnchanged");
        if (!string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
        {
            Directory.CreateDirectory(_request.EvidenceDirectory);
            File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "compact-call-of-the-void-admission.json"),
                JsonSerializer.Serialize(new { character = player.Character.Id.Entry, encounter = combat.Encounter?.Id.Entry,
                    seed = _request.Seed, poolSize = pool.Count, admitted, unsupported,
                    firstUnsupported = pool[firstUnsupported].Id.Entry, rejection,
                    // The native owner selects from its own character pool, so a Silent caster
                    // must not silently fall back to the Necrobinder card list.
                    poolContainsCallOfTheVoid = pool.Any(card => card is CallOfTheVoid),
                    rootAdmittedWithPower = admittedRoot, nonRoundProjectionRejected = true },
                    new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static string ParseCompileRejection(string rejection)
    {
        const string prefix = "Compact prototype cannot admit card ";
        if (!rejection.StartsWith(prefix, StringComparison.Ordinal) || rejection[^1] != '.')
            throw new InvalidOperationException($"CallOfTheVoid root was rejected before its generation pool: {rejection}");
        return rejection[(rejection.LastIndexOf(' ') + 1)..^1];
    }
}
