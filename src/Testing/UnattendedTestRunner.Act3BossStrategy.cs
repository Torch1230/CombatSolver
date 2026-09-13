using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Commands;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task<int> TraceAct3SubjectBufferAsync(CombatState combat, Player player)
    {
        if (combat.Encounter?.Id.Entry != "TEST_SUBJECT_BOSS"
            || player.PlayerCombatState?.TurnNumber != 1)
            throw new InvalidOperationException("Buffer setup tracing requires the recorded first-turn subject root.");
        var before = ContinuationStamp.CaptureLive(combat);
        var driver = new CombatBeamSolver(CombatRootSnapshot.Capture(combat), SolverDisplayNames.Capture(combat),
            BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        List<PlanAction> actions = [];
        List<KnownRoutePrefix> prefixes = [];
        var parent = driver.ReplayDiagnosticPrefix([]);
        try
        {
            foreach (uint physicalIndex in new uint[] { uint.MaxValue, 3, 4, 1, 19 })
            {
                PlanAction action;
                if (physicalIndex == uint.MaxValue)
                    action = new(PlanActionKind.UsePotion, 1, PotionSlot: 3, PotionId: "LUCKY_TONIC");
                else
                {
                    var state = parent.Simulator.State.GetPlayerCombatState(player);
                    var hand = state.Hand.Cards;
                    var original = MegaCrit.Sts2.Core.Entities.Multiplayer.NetCombatCard.ForTesting(physicalIndex).ToCardModel();
                    var card = hand.Single(candidate => ReferenceEquals(candidate.Original, original));
                    string key = CardChoiceSupport.ChoiceCardKey(card);
                    action = new(PlanActionKind.PlayCard, 1, CardId: card.Preview.Id.Entry,
                        CardOccurrence: hand.TakeWhile(candidate => !ReferenceEquals(candidate, card))
                            .Count(candidate => candidate.Preview.Id == card.Preview.Id),
                        CardStateKey: key,
                        CardStateOccurrence: hand.TakeWhile(candidate => !ReferenceEquals(candidate, card))
                            .Count(candidate => CardChoiceSupport.ChoiceCardKey(candidate) == key),
                        ReplayCount: Math.Max(0, card.Preview.GetEnchantedReplayCount()));
                    if (physicalIndex == 1)
                    {
                        var selectedOriginal = MegaCrit.Sts2.Core.Entities.Multiplayer.NetCombatCard.ForTesting(19).ToCardModel();
                        var selected = state.DrawPile.Cards.Single(candidate => ReferenceEquals(candidate.Original, selectedOriginal));
                        string selectedKey = CardChoiceSupport.ChoiceCardKey(selected);
                        int occurrence = state.DrawPile.Cards.TakeWhile(candidate => !ReferenceEquals(candidate, selected))
                            .Count(candidate => CardChoiceSupport.ChoiceCardKey(candidate) == selectedKey);
                        action = action with { Choice = new PlanCardChoice(PlanChoiceEffect.MoveToHand,
                            MegaCrit.Sts2.Core.Entities.Cards.PileType.Draw,
                            [new PlanCardToken(selected.Preview.Id.Entry, selected.Preview.CurrentUpgradeLevel,
                                selectedKey, occurrence, occurrence, "")]) };
                    }
                }
                actions.Add(action);
                parent.ReleaseSimulator();
                parent = driver.ReplayDiagnosticPrefix(actions);
                if (parent.HasRisk || parent.PlayerDead || parent.BoundaryReason != SearchBoundaryReason.None)
                    throw new InvalidOperationException("Recorded buffer setup failed simulated replay.");
                prefixes.Add(FreezeKnownRoutePrefix(action, CaptureSimulated(parent.Simulator,
                    (SimulatedCombatState)parent.Simulator.State.CombatState, player, combat.Enemies[0]), parent));
            }
            using var archive = System.IO.Compression.ZipFile.OpenRead(_request.CheckpointArchivePath!);
            using var reader = new StreamReader(archive.GetEntry(
                "replay/current/replay-state/000023-search_request_Deploy.json")!.Open());
            using var expected = System.Text.Json.JsonDocument.Parse(reader.ReadToEnd());
            string expectedState = expected.RootElement.GetProperty("exactContinuationState").GetString()!;
            var predicted = driver.CaptureDiagnosticContinuation(parent);
            if (!ReplayContinuationMatches(expectedState, predicted.StateText, allowLegacyZeroCounter: true))
                throw new InvalidOperationException("Buffer setup differs from the native-verified recorded endpoint: "
                    + new ContinuationStamp(expectedState).DescribeFirstDifference(predicted));
            _completedChecks.Add("Act3SubjectBuffer:FiveActionsMatchRecordedEndpoint");
            foreach (int[] hits in new int[][] { [1], [40], [40, 40], [1, 40, 40] })
            {
                var probe = driver.ReplayDiagnosticPrefix(actions);
                try
                {
                    var stamp = driver.CaptureDiagnosticContinuation(probe);
                    int projected = driver.ProjectDiagnosticHits(probe, combat.Enemies[0], hits);
                    if (driver.CaptureDiagnosticContinuation(probe) != stamp)
                        throw new InvalidOperationException("Threat projection changed its source branch.");
                    var simulator = (CombatPredictionSimulator)probe.Simulator;
                    List<string> hitStates = [];
                    foreach (int hit in hits)
                    {
                        var damageState = (SimulatedCombatState)simulator.State.CombatState;
                        MonsterMoveSemantics.DamagePlayer(simulator, damageState,
                            combat.Enemies[0], player.Creature, hit);
                        var osty = damageState.GetOsty(player);
                        hitStates.Add($"hit={hit},hp={simulator.State.GetCreature(player.Creature).CurrentHp},buffer={damageState.GetAmount<MegaCrit.Sts2.Core.Models.Powers.BufferPower>(player.Creature)},osty={(osty == null ? -1 : simulator.State.GetCreature(osty).CurrentHp)},enemy={simulator.State.GetCreature(combat.Enemies[0]).CurrentHp}");
                    }
                    int settled = simulator.State.GetCreature(player.Creature).CurrentHp;
                    if (projected != settled)
                        throw new InvalidOperationException($"Finite Buffer forecast differs for {string.Join(',', hits)}: {projected}/{settled}. {string.Join(';', hitStates)}");
                }
                finally { probe.ReleaseSimulator(); }
            }
            _completedChecks.Add("Act3SubjectBuffer:BlockedHit:OstyOverflow:FiniteMultiHit:ProjectionMatchesDamageSettlement");
            var endTurn = new PlanAction(PlanActionKind.EndTurn, 1);
            actions.Add(endTurn);
            parent.ReleaseSimulator();
            parent = driver.ReplayDiagnosticPrefix(actions);
            if (parent.HasRisk || parent.PlayerDead || parent.BoundaryReason != SearchBoundaryReason.None)
                throw new InvalidOperationException("Recorded setup could not reach its second turn.");
            prefixes.Add(FreezeKnownRoutePrefix(endTurn, CaptureSimulated(parent.Simulator,
                (SimulatedCombatState)parent.Simulator.State.CombatState, player, combat.Enemies[0]), parent));
            if (parent.PlayerHp != 48)
                throw new InvalidOperationException("Recorded buffer setup must reach turn two at 48 HP.");
            _completedChecks.Add("Act3SubjectBuffer:SetupEndsTurnAtFullStartingHp");
        }
        finally
        {
            parent.ReleaseSimulator();
            if (ContinuationStamp.CaptureLive(combat) != before)
                throw new InvalidOperationException("Buffer setup tracing changed the live root.");
        }
        return await RunKnownRoutePathTraceAsync(combat, player, prefixes,
            "Act3SubjectBuffer", "act3_subject_buffer_path", observedRetentionStep: 4,
            requirePotionFirstStep: true);
    }

    private async Task<int> TraceAct3HellraiserAsync(CombatState combat, Player player)
    {
        if (combat.Encounter?.Id.Entry != "QUEEN_BOSS"
            || player.PlayerCombatState?.TurnNumber != 1 || combat.Enemies.Count != 2)
            throw new InvalidOperationException("The recorded Hellraiser prefix requires its first-turn queen root.");
        var before = ContinuationStamp.CaptureLive(combat);
        var root = CombatRootSnapshot.Capture(combat);
        var driver = new CombatBeamSolver(root, SolverDisplayNames.Capture(combat),
            BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        var initial = driver.ReplayDiagnosticPrefix([]);
        SimulationSnapshot? result = null;
        List<KnownRoutePrefix> prefixes = [];
        List<PlanAction> actions = [];
        try
        {
            var hand = initial.Simulator.State.GetPlayerCombatState(player).Hand.Cards;
            var original = MegaCrit.Sts2.Core.Entities.Multiplayer.NetCombatCard.ForTesting(1).ToCardModel();
            var card = hand.Single(candidate => ReferenceEquals(candidate.Original, original));
            if (card.Preview.Id.Entry != "HELLRAISER")
                throw new InvalidOperationException("Recorded physical card 1 must be Hellraiser.");
            string key = CardChoiceSupport.ChoiceCardKey(card);
            var action = new PlanAction(PlanActionKind.PlayCard, 1, CardId: "HELLRAISER",
                CardOccurrence: hand.TakeWhile(candidate => !ReferenceEquals(candidate, card))
                    .Count(candidate => candidate.Preview.Id.Entry == "HELLRAISER"),
                CardStateKey: key,
                CardStateOccurrence: hand.TakeWhile(candidate => !ReferenceEquals(candidate, card))
                    .Count(candidate => CardChoiceSupport.ChoiceCardKey(candidate) == key),
                ReplayCount: Math.Max(0, card.Preview.GetEnchantedReplayCount()));
            result = driver.ReplayDiagnosticPrefix([action]);
            actions.Add(action);
            using var archive = System.IO.Compression.ZipFile.OpenRead(_request.CheckpointArchivePath!);
            using var reader = new StreamReader(archive.GetEntry(
                "replay/current/replay-state/000007-search_request_Manual.json")!.Open());
            using var expected = System.Text.Json.JsonDocument.Parse(reader.ReadToEnd());
            string expectedState = expected.RootElement.GetProperty("exactContinuationState").GetString()!;
            var predicted = driver.CaptureDiagnosticContinuation(result);
            if (!ReplayContinuationMatches(expectedState, predicted.StateText))
                throw new InvalidOperationException("Hellraiser simulation differs from its recorded native endpoint: "
                    + new ContinuationStamp(expectedState).DescribeFirstDifference(predicted));
            prefixes.Add(FreezeKnownRoutePrefix(action, CaptureSimulated(result.Simulator,
                (SimulatedCombatState)result.Simulator.State.CombatState, player, combat.Enemies[0]), result));
            _completedChecks.Add("Act3Hellraiser:PhysicalCard:FullTwoEnemyEndpointMatchesNative");
            // The package's selected 35-HP solver route starts at the verified post-card
            // root. Treat it as a witness to validate, never as additional player input.
            var recordedActions = new SortedDictionary<int, PlanAction>();
            var jsonOptions = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            jsonOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            foreach (var entry in archive.Entries.Where(entry =>
                         entry.FullName.StartsWith("diagnostics/logs/combat/", StringComparison.Ordinal)
                         && entry.FullName.EndsWith(".jsonl", StringComparison.Ordinal)))
            {
                using var logReader = new StreamReader(entry.Open());
                while (logReader.ReadLine() is { } line)
                {
                    using var row = System.Text.Json.JsonDocument.Parse(line);
                    string message = row.RootElement.GetProperty("Message").GetString()!;
                    if (!message.StartsWith("[CombatSolver/Evidence] ROUTE_ACTION ", StringComparison.Ordinal))
                        continue;
                    using var evidence = System.Text.Json.JsonDocument.Parse(message[message.IndexOf('{')..]);
                    if (evidence.RootElement.GetProperty("traceId").GetString() != "9e339ce790964f84b4aa84773d0ac459")
                        continue;
                    recordedActions.Add(evidence.RootElement.GetProperty("index").GetInt32(),
                        System.Text.Json.JsonSerializer.Deserialize<PlanAction>(
                            evidence.RootElement.GetProperty("action").GetRawText(), jsonOptions)!);
                }
            }
            if (recordedActions.Count != 20 || !recordedActions.Keys.SequenceEqual(Enumerable.Range(0, 20)))
                throw new InvalidDataException("The archived Hellraiser witness requires all 20 ordered actions.");
            foreach (PlanAction recordedAction in recordedActions.Values)
            {
                action = recordedAction;
                actions.Add(action);
                result.ReleaseSimulator();
                result = null;
                result = driver.ReplayDiagnosticPrefix(actions);
                if (result.HasRisk || result.PlayerDead || result.BoundaryReason != SearchBoundaryReason.None)
                    throw new InvalidOperationException("The known post-Hellraiser suffix failed simulated replay.");
                prefixes.Add(FreezeKnownRoutePrefix(action, CaptureSimulated(result.Simulator,
                    (SimulatedCombatState)result.Simulator.State.CombatState, player, combat.Enemies[0]), result));
            }
            if (!result.AllEnemiesDead || result.PlayerHp != 46)
                throw new InvalidOperationException($"Archived witness result differs: hp={result.PlayerHp}, enemy={result.EnemyHp}.");
            _completedChecks.Add("Act3Hellraiser:RecordedSolverWitness:FullSimulatedVictory:FinalHp46:NotNativeDeployed");
        }
        finally
        {
            initial.ReleaseSimulator();
            result?.ReleaseSimulator();
            if (ContinuationStamp.CaptureLive(combat) != before)
                throw new InvalidOperationException("Hellraiser tracing changed the live root.");
        }
        return await RunKnownRoutePathTraceAsync(combat, player, prefixes,
            "Act3Hellraiser", "act3_hellraiser_path", observedRetentionStep: 5);
    }

    private async Task<int> TraceAct3Subject0530Async(CombatState combat, Player player)
    {
        if (combat.Encounter?.Id.Entry != "TEST_SUBJECT_BOSS"
            || player.PlayerCombatState?.TurnNumber != 3 || combat.Enemies.Count != 1)
            throw new InvalidOperationException("The recorded subject prefix requires its restored third-turn root.");
        var root = CombatRootSnapshot.Capture(combat);
        var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null);
        var driver = new CombatBeamSolver(root, SolverDisplayNames.Capture(combat),
            BattleDamageTracker.Observe(combat), policy);
        List<PlanAction> actions = [];
        List<SimulationSnapshot> owned = [];
        List<KnownRoutePrefix> prefixes = [];
        var before = ContinuationStamp.CaptureLive(combat);
        uint[] recordedCardIndices = [14, 15, 38, 18, 12];
        int cardStep = 0;
        try
        {
            var parent = driver.ReplayDiagnosticPrefix(actions);
            owned.Add(parent);
            foreach (string id in new[] { "LETHALITY", "APPARITION", "SOUL", "DEMESNE", "POKE", "" })
            {
                PlanAction action;
                if (id.Length == 0) action = new(PlanActionKind.EndTurn, 3);
                else
                {
                    var hand = parent.Simulator.State.GetPlayerCombatState(player).Hand.Cards;
                    var original = MegaCrit.Sts2.Core.Entities.Multiplayer.NetCombatCard
                        .ForTesting(recordedCardIndices[cardStep++]).ToCardModel();
                    var card = hand.Single(candidate => ReferenceEquals(candidate.Original, original));
                    if (card.Preview.Id.Entry != id)
                        throw new InvalidOperationException($"Recorded card identity differs from {id}.");
                    var descriptor = new PlanAction(PlanActionKind.PlayCard, 3, CardId: id,
                        CardOccurrence: hand.TakeWhile(candidate => !ReferenceEquals(candidate, card))
                            .Count(candidate => candidate.Preview.Id.Entry == id));
                    string key = CardChoiceSupport.ChoiceCardKey(card);
                    action = descriptor with
                    {
                        TargetIndex = id == "POKE" ? 0 : -1,
                        TargetCombatId = id == "POKE" ? combat.Enemies[0].CombatId : null,
                        CardStateKey = key,
                        CardStateOccurrence = hand.TakeWhile(candidate => !ReferenceEquals(candidate, card))
                            .Count(candidate => CardChoiceSupport.ChoiceCardKey(candidate) == key),
                        ReplayCount = Math.Max(0, card.Preview.GetEnchantedReplayCount()),
                    };
                }
                actions.Add(action);
                parent = driver.ReplayDiagnosticPrefix(actions);
                owned.Add(parent);
                if (parent.HasRisk || parent.PlayerDead || parent.BoundaryReason != SearchBoundaryReason.None)
                    throw new InvalidOperationException("Recorded subject prefix failed strict simulated replay.");
                prefixes.Add(FreezeKnownRoutePrefix(action, CaptureSimulated(parent.Simulator,
                    (SimulatedCombatState)parent.Simulator.State.CombatState, player, combat.Enemies[0]), parent));
            }
            using var archive = System.IO.Compression.ZipFile.OpenRead(_request.CheckpointArchivePath!);
            using var reader = new StreamReader(archive.GetEntry(
                "replay/current/replay-state/000005-search_request_AutoTurnStart.json")!.Open());
            using var expected = System.Text.Json.JsonDocument.Parse(reader.ReadToEnd());
            string expectedState = expected.RootElement.GetProperty("exactContinuationState").GetString()!;
            var predicted = driver.CaptureDiagnosticContinuation(parent);
            if (!ReplayContinuationMatches(expectedState, predicted.StateText))
                throw new InvalidOperationException("Recorded player prefix simulation differs from its native endpoint: "
                    + new ContinuationStamp(expectedState).DescribeFirstDifference(predicted));
            _completedChecks.Add("Act3Subject0530:SimulatedPrefixMatchesRecordedNativeEndpoint");
        }
        finally
        {
            foreach (var snapshot in owned) snapshot.ReleaseSimulator();
            if (ContinuationStamp.CaptureLive(combat) != before)
                throw new InvalidOperationException("Prefix tracing changed the live root.");
        }
        return await RunKnownRoutePathTraceAsync(combat, player, prefixes,
            "Act3Subject0530", "act3_subject_0530_path", observedRetentionStep: 4);
    }

    private async Task<int> TraceAct3HourglassOpeningAsync(CombatState combat, Player player)
    {
        if (combat.Encounter?.Id.Entry != "AEONGLASS_BOSS"
            || player.PlayerCombatState?.TurnNumber != 1 || combat.Enemies.Count != 1)
            throw new InvalidOperationException("The recorded hourglass opening requires its first-turn root.");
        var root = CombatRootSnapshot.Capture(combat);
        var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null);
        var driver = new CombatBeamSolver(root, SolverDisplayNames.Capture(combat),
            BattleDamageTracker.Observe(combat), policy);
        List<PlanAction> actions = [];
        List<SimulationSnapshot> owned = [];
        List<KnownRoutePrefix> prefixes = [];
        var before = ContinuationStamp.CaptureLive(combat);
        uint[] indices = [3, 4, 1];
        string[] ids = ["PREP_TIME", "WELL_LAID_PLANS", "DEFEND_SILENT"];
        try
        {
            var parent = driver.ReplayDiagnosticPrefix(actions);
            owned.Add(parent);
            for (int step = 0; step <= ids.Length; step++)
            {
                PlanAction action = new(PlanActionKind.EndTurn, 1);
                if (step < ids.Length)
                {
                    var hand = parent.Simulator.State.GetPlayerCombatState(player).Hand.Cards;
                    var original = MegaCrit.Sts2.Core.Entities.Multiplayer.NetCombatCard
                        .ForTesting(indices[step]).ToCardModel();
                    var card = hand.Single(candidate => ReferenceEquals(candidate.Original, original));
                    if (card.Preview.Id.Entry != ids[step])
                        throw new InvalidOperationException("Recorded hourglass card identity differs.");
                    string key = CardChoiceSupport.ChoiceCardKey(card);
                    action = new(PlanActionKind.PlayCard, 1, CardId: ids[step],
                        CardOccurrence: hand.TakeWhile(candidate => !ReferenceEquals(candidate, card))
                            .Count(candidate => candidate.Preview.Id.Entry == ids[step]),
                        CardStateKey: key,
                        CardStateOccurrence: hand.TakeWhile(candidate => !ReferenceEquals(candidate, card))
                            .Count(candidate => CardChoiceSupport.ChoiceCardKey(candidate) == key),
                        ReplayCount: Math.Max(0, card.Preview.GetEnchantedReplayCount()));
                }
                actions.Add(action);
                parent = driver.ReplayDiagnosticPrefix(actions);
                owned.Add(parent);
                if (parent.HasRisk || parent.PlayerDead || parent.BoundaryReason != SearchBoundaryReason.None)
                    throw new InvalidOperationException("Recorded hourglass opening failed simulated replay.");
                prefixes.Add(FreezeKnownRoutePrefix(action, CaptureSimulated(parent.Simulator,
                    (SimulatedCombatState)parent.Simulator.State.CombatState, player, combat.Enemies[0]), parent));
            }
            using var archive = System.IO.Compression.ZipFile.OpenRead(_request.CheckpointArchivePath!);
            using var reader = new StreamReader(archive.GetEntry(
                "replay/current/replay-state/000003-search_request_AutoTurnStart.json")!.Open());
            using var expected = System.Text.Json.JsonDocument.Parse(reader.ReadToEnd());
            string expectedState = expected.RootElement.GetProperty("exactContinuationState").GetString()!;
            var predicted = driver.CaptureDiagnosticContinuation(parent);
            if (!ReplayContinuationMatches(expectedState, predicted.StateText))
                throw new InvalidOperationException("Hourglass opening differs from recorded native endpoint: "
                    + new ContinuationStamp(expectedState).DescribeFirstDifference(predicted));
            _completedChecks.Add("Act3HourglassOpening:SimulatedPrefixMatchesRecordedNativeEndpoint");
            var recordedActions = new SortedDictionary<int, PlanAction>();
            var jsonOptions = new System.Text.Json.JsonSerializerOptions();
            jsonOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            foreach (var entry in archive.Entries.Where(entry =>
                entry.FullName.StartsWith("diagnostics/logs/combat/", StringComparison.Ordinal)
                && entry.FullName.EndsWith(".jsonl", StringComparison.Ordinal)))
            {
                using var logReader = new StreamReader(entry.Open());
                while (logReader.ReadLine() is { } line)
                {
                    using var envelope = System.Text.Json.JsonDocument.Parse(line);
                    string message = envelope.RootElement.GetProperty("Message").GetString()!;
                    if (!message.Contains("ROUTE_ACTION ", StringComparison.Ordinal)) continue;
                    using var payload = System.Text.Json.JsonDocument.Parse(message[message.IndexOf('{')..]);
                    if (payload.RootElement.GetProperty("traceId").GetString()
                        != "9b3bec1e5d3241aeb8b5c8461b50b3ee") continue;
                    int index = payload.RootElement.GetProperty("index").GetInt32();
                    recordedActions.Add(index, System.Text.Json.JsonSerializer.Deserialize<PlanAction>(
                        payload.RootElement.GetProperty("action").GetRawText(), jsonOptions)!);
                }
            }
            if (recordedActions.Count != 30 || !recordedActions.Keys.SequenceEqual(Enumerable.Range(0, 30)))
                throw new InvalidOperationException("The recorded hourglass victory suffix is incomplete.");
            foreach (var action in recordedActions.Values)
            {
                actions.Add(action);
                parent = driver.ReplayDiagnosticPrefix(actions);
                owned.Add(parent);
                if (parent.HasRisk || parent.PlayerDead || parent.BoundaryReason != SearchBoundaryReason.None)
                    throw new InvalidOperationException("The recorded hourglass victory suffix failed simulated replay.");
                prefixes.Add(FreezeKnownRoutePrefix(action, CaptureSimulated(parent.Simulator,
                    (SimulatedCombatState)parent.Simulator.State.CombatState, player, combat.Enemies[0]), parent));
            }
            if (!parent.AllEnemiesDead || parent.PlayerHp != 48)
                throw new InvalidOperationException("The recorded hourglass winner must end at 48 HP.");
            _completedChecks.Add("Act3HourglassOpening:FullRecordedWinnerSimulated48Hp");
        }
        finally
        {
            foreach (var snapshot in owned) snapshot.ReleaseSimulator();
            if (ContinuationStamp.CaptureLive(combat) != before)
                throw new InvalidOperationException("Hourglass tracing changed the live root.");
        }
        if (_request.ScenarioId == "ACT3-HOURGLASS-POLICY-AB")
        {
            await RunKnownRoutePathTraceAsync(combat, player, prefixes,
                "HourglassProgressionFirst1", "hourglass_policy_p1", observedRetentionStep: 3,
                finalBossStrategyOverride: BossHpStrategy.ProgressionFirst);
            await RunKnownRoutePathTraceAsync(combat, player, prefixes,
                "HourglassMinimizeHp", "hourglass_policy_min", observedRetentionStep: 3,
                finalBossStrategyOverride: BossHpStrategy.MinimizeHpLoss);
            return await RunKnownRoutePathTraceAsync(combat, player, prefixes,
                "HourglassProgressionFirst2", "hourglass_policy_p2", observedRetentionStep: 3,
                finalBossStrategyOverride: BossHpStrategy.ProgressionFirst);
        }
        return await RunKnownRoutePathTraceAsync(combat, player, prefixes,
            "Act3HourglassOpening", "act3_hourglass_opening_path", observedRetentionStep: 3);
    }

    private async Task DescribeAct3OpeningEffectsAsync(CombatState combat, Player player)
    {
        var root = CombatRootSnapshot.Capture(combat);
        var names = SolverDisplayNames.Capture(combat);
        var damage = BattleDamageTracker.Observe(combat);
        ContinuationStamp before = ContinuationStamp.CaptureLive(combat);
        var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null) with
        {
            MaxDegreeOfParallelism = 1,
        };
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(
            Math.Max(1, _request.TimeoutSeconds - _stopwatch.Elapsed.TotalSeconds)));
        _completedChecks.AddRange(await Task.Run(() => new CombatBeamSolver(root, names, damage, policy,
            cancellation.Token, searchProfile: policy.Profile).DescribeOpeningActionEffectsForTesting()));
        if (ContinuationStamp.CaptureLive(combat) != before)
            throw new InvalidOperationException("Opening effect diagnostics changed the live combat.");
    }

    private async Task AssertAct3BossStrategyAsync(CombatState combat, Player player)
    {
        foreach (string id in new[] { "TEST_SUBJECT_BOSS", "AEONGLASS_BOSS", "QUEEN_BOSS" })
            if (!SearchPolicySnapshot.IsAct3BossEncounter(2, id) || SearchPolicySnapshot.IsAct3BossEncounter(1, id))
                throw new InvalidOperationException("Act 3 boss scope mismatch.");
        if (SearchPolicySnapshot.IsAct3BossEncounter(2, "FUZZY_WURM_CRAWLER_WEAK"))
            throw new InvalidOperationException("Ordinary encounters must keep their existing search.");
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in combat.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        foreach (string id in new[] { "BURNING_PACT", "FINESSE", "DEFEND_IRONCLAD", "STRIKE_IRONCLAD" })
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand" });
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "SHIV", Pile = "Draw", Count = 6 });
        SetEnergy(player, 1);
        await CreatureCmd.SetCurrentHp(combat.Enemies[0], 12);
        var root = CombatRootSnapshot.Capture(combat);
        var names = SolverDisplayNames.Capture(combat);
        var damage = BattleDamageTracker.Observe(combat);
        var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null) with
        {
            Act3BossStrategy = true, FixedBudget = true, VerifyIncrementalSearch = true,
            StopAtAcceptableBattleHpLoss = true, AcceptableBattleHpLoss = 0,
            BudgetOverrideMilliseconds = 3000, MaxDegreeOfParallelism = 1, PotionPolicy = SolverPotionPolicy.Disabled,
            Profile = new SolverSearchProfile(60, 256, 32, 18, 24, 3000),
        };
        var result = await Task.Run(() => new CombatBeamSolver(root, names, damage, policy,
            searchProfile: policy.Profile).Solve());
        if (!result.Snapshot.AllEnemiesDead || result.ProjectedBattleHpLost != 0
            || !result.BestNode.Actions.Any(action => action.CardId == "FINESSE")
            || !result.BestNode.Actions.Any(action => action.CardId == "BURNING_PACT")
            || result.BestNode.Actions.Where(action => action.CardId == "BURNING_PACT")
                .SelectMany(action => action.GetActionChoicesInExecutionOrder())
                .Where(choice => choice.Effect == PlanChoiceEffect.Exhaust)
                .SelectMany(choice => choice.Cards).Any(card => card.CardId == "FINESSE"))
            throw new InvalidOperationException("The actual search must keep and play the draw engine to win without damage.");

        await InjectPowerAsync(combat, player, new UnattendedPowerInjection { PowerId = "NO_DRAW_POWER", Target = "Player", Amount = 1 });
        await CreatureCmd.SetCurrentHp(combat.Enemies[0], 6);
        root = CombatRootSnapshot.Capture(combat); names = SolverDisplayNames.Capture(combat);
        result = await Task.Run(() => new CombatBeamSolver(root, names, damage, policy,
            searchProfile: policy.Profile).Solve());
        if (!result.Snapshot.AllEnemiesDead || result.ProjectedBattleHpLost != 0
            || result.BestNode.Actions.Any(action => action.CardId == "BURNING_PACT"))
            throw new InvalidOperationException("Blocked draw must not displace the immediate lethal attack.");
        foreach (var power in player.Creature.Powers.ToArray()) await PowerCmd.Remove(power);


        await ClearPlayerPilesAsync(player);
        foreach (string id in new[] { "WRAITH_FORM", "STRIKE_IRONCLAD" })
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand", UpgradeLevels = id == "STRIKE_IRONCLAD" ? 1 : 0 });
        await CreatureCmd.SetCurrentHp(combat.Enemies[0], 9);
        SetEnergy(player, 3);
        root = CombatRootSnapshot.Capture(combat); names = SolverDisplayNames.Capture(combat); damage = BattleDamageTracker.Observe(combat);
        result = await Task.Run(() => CombatSearchCoordinator.Solve(root, names, damage, policy, CancellationToken.None, null));
        if (!result.Snapshot.AllEnemiesDead || result.ProjectedBattleHpLost != 0
            || result.BestNode.Actions.Any(a => a.CardId == "WRAITH_FORM")
            || result.ExpandedNodes > policy.Profile.MaxExpandedNodes)
            throw new InvalidOperationException("An unnecessary costly power must lose to the ordinary zero-loss kill.");
        await ClearPlayerPilesAsync(player);
        foreach (string id in new[] { "DEFEND_IRONCLAD", "STRIKE_IRONCLAD", "WRAITH_FORM" })
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand" });
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "STRIKE_IRONCLAD", Pile = "Draw", UpgradeLevels = 1 });
        await CreatureCmd.SetCurrentHp(player.Creature, 1);
        await CreatureCmd.SetCurrentHp(combat.Enemies[0], 10);
        SetEnergy(player, 1);
        root = CombatRootSnapshot.Capture(combat); names = SolverDisplayNames.Capture(combat); damage = BattleDamageTracker.Observe(combat);
        result = await Task.Run(() => CombatSearchCoordinator.Solve(root, names, damage, policy, CancellationToken.None, null));
        if (!result.Snapshot.AllEnemiesDead || result.Snapshot.PlayerHp != 1
            || result.BestNode.Actions.First().CardId != "DEFEND_IRONCLAD")
            throw new InvalidOperationException("The actual search must defend before taking the next-turn lethal draw.");
        await AssertAct3BossInteractionsAsync(combat, player, policy);
        _completedChecks.Add("Act3Strategy:Scope:ExhaustKeepsDrawEngine:NoDrawLethal:UpgradedStrikeKill:EssentialDefend:IncrementalReplay:BossInteractions");
    }

    private async Task AssertAct3BossInteractionsAsync(CombatState combat, Player player, SearchPolicySnapshot policy)
    {
        async Task<StrategicEffectVector> Capture(bool specialized)
        {
            var root = CombatRootSnapshot.Capture(combat);
            var names = SolverDisplayNames.Capture(combat);
            var damage = BattleDamageTracker.Observe(combat);
            var frozen = policy with { Act3BossStrategy = specialized };
            return await Task.Run(() => new CombatBeamSolver(root, names, damage, frozen,
                searchProfile: frozen.Profile).CaptureStrategicEffectsForTesting());
        }

        foreach (var power in player.Creature.Powers.ToArray()) await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        SetEnergy(player, 3);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "STRIKE_IRONCLAD", Pile = "Draw", Count = 6 });
        await InjectPowerAsync(combat, player, new UnattendedPowerInjection { PowerId = "PAGESTORM_POWER", Target = "Player", Amount = 1 });
        var noSource = await Capture(true);
        var ordinary = await Capture(false);
        if (noSource.CardAccessPotential != 0 || ordinary.CardAccessPotential != 0)
            throw new InvalidOperationException("Pagestorm needs an actual future ethereal draw source.");
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DAZED", Pile = "Draw", Count = 2 });
        var source = await Capture(true);
        if (source.CardAccessPotential <= 0 || (await Capture(false)) != ordinary)
            throw new InvalidOperationException("The boss interaction must recognize ethereal draw while preserving the ordinary power value.");
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "STRIKE_IRONCLAD", Pile = "Draw", Count = 6 });
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DAZED", Pile = "Hand", Count = 2 });
        if ((await Capture(true)).CardAccessPotential != 0)
            throw new InvalidOperationException("Already-drawn ethereal cards must not create a future draw trigger.");

        foreach (var power in player.Creature.Powers.ToArray()) await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "STRIKE_IRONCLAD", Pile = "Hand", Count = 3 });
        await InjectPowerAsync(combat, player, new UnattendedPowerInjection { PowerId = "DANSE_MACABRE_POWER", Target = "Player", Amount = 6 });
        if ((await Capture(true)).PreventionPotential != 0)
            throw new InvalidOperationException("One-energy cards must not activate the high-energy block interaction.");
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "BASH", Pile = "Hand" });
        if ((await Capture(true)).PreventionPotential <= 0 || (await Capture(false)).PreventionPotential != 0)
            throw new InvalidOperationException("The boss interaction must recognize an affordable two-energy play without changing ordinary evaluation.");
        SetEnergy(player, 1);
        if ((await Capture(true)).PreventionPotential != 0)
            throw new InvalidOperationException("An unaffordable attack must not provide current-turn block potential.");
        SetEnergy(player, 3);
        await InjectPowerAsync(combat, player, new UnattendedPowerInjection { PowerId = "FREE_ATTACK_POWER", Target = "Player", Amount = 1 });
        if ((await Capture(true)).PreventionPotential != 0)
            throw new InvalidOperationException("A free attack must not trigger the two-energy block interaction merely because of its printed cost.");

        foreach (var power in player.Creature.Powers.ToArray()) await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "BACKFLIP", Pile = "Hand" });
        await InjectPowerAsync(combat, player, new UnattendedPowerInjection { PowerId = "FASTEN_POWER", Target = "Player", Amount = 3 });
        if ((await Capture(true)).PreventionPotential != 0)
            throw new InvalidOperationException("Fasten must recognize the Defend tag, not every card that grants block.");
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_IRONCLAD", Pile = "Hand", Count = 2 });
        if ((await Capture(true)) != (await Capture(false)))
            throw new InvalidOperationException("Powers outside the validated interaction set must keep their ordinary value.");

        foreach (var power in player.Creature.Powers.ToArray()) await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        await CreatureCmd.SetCurrentHp(combat.Enemies[0], 500);
        SetEnergy(player, 3);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "BASH", Pile = "Draw", Count = 8 });
        await InjectPowerAsync(combat, player, new UnattendedPowerInjection { PowerId = "DEMESNE_POWER", Target = "Player", Amount = 1 });
        var demesne = await Capture(true);
        var ordinaryDemesne = await Capture(false);
        if (demesne.ResourcePotential <= 0 || demesne.CardAccessPotential <= 0
            || ordinaryDemesne.ResourcePotential != 0 || ordinaryDemesne.CardAccessPotential != 0)
            throw new InvalidOperationException("Demesne must recognize useful future energy and draws only in boss specialization.");
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "SHIV", Pile = "Draw", Count = 8 });
        if ((await Capture(true)).ResourcePotential != 0)
            throw new InvalidOperationException("A zero-energy deck must not receive extra energy value from Demesne.");
        await ClearPlayerPilesAsync(player);
        var emptyDemesne = await Capture(true);
        if (emptyDemesne.ResourcePotential != 0 || emptyDemesne.CardAccessPotential != 0)
            throw new InvalidOperationException("An empty deck must not create future cards or energy demand.");

        foreach (var power in player.Creature.Powers.ToArray()) await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        await InjectPowerAsync(combat, player, new UnattendedPowerInjection
            { PowerId = "PREP_TIME_POWER", Target = "Player", Amount = 6 });
        if ((await Capture(true)).DamagePotential != 0)
            throw new InvalidOperationException("Recurring Vigor needs a remaining attack to realize damage.");
        var ordinaryPrepTime = await Capture(false);
        await InjectCardAsync(combat, player, new UnattendedCardInjection
            { CardId = "STRIKE_IRONCLAD", Pile = "Draw", Count = 6 });
        if ((await Capture(true)).DamagePotential <= 6 || (await Capture(false)) != ordinaryPrepTime)
            throw new InvalidOperationException("Recurring Vigor must value future attacks only in boss specialization.");

        foreach (var power in player.Creature.Powers.ToArray()) await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        await InjectPowerAsync(combat, player, new UnattendedPowerInjection
            { PowerId = "LETHALITY_POWER", Target = "Player", Amount = 75 });
        if ((await Capture(true)).DamagePotential != 0)
            throw new InvalidOperationException("Lethality requires an attack to realize its multiplier.");
        await InjectCardAsync(combat, player, new UnattendedCardInjection
            { CardId = "ERADICATE", Pile = "Draw", Count = 1 });
        if ((await Capture(true)).DamagePotential <= (await Capture(false)).DamagePotential)
            throw new InvalidOperationException("First-attack evaluation must account for payable Eradicate hits.");
    }
}
