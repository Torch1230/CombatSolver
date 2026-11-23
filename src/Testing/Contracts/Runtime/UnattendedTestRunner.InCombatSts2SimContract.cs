using System.Diagnostics;
using System.Globalization;
using CombatSolver.Api;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    internal async Task RunInCombatSts2SimContractAsync(
        CombatState combatState,
        Player player)
    {
        if (_request.ScenarioId == "INCOMBAT-STS2SIM-PILE-CHOICE")
        {
            await VerifyExternalPileEffectsAsync(combatState, player);
            return;
        }
        await VerifyExternalImplicitChoicesAsync(combatState, player);
        _completedChecks.Add("ExternalImplicitChoices:NativeOrder:ManualCursor:StrictWire");
        if (_request.ScenarioId == "INCOMBAT-STS2SIM-IMPLICIT-CHOICE")
            return;

        NGame host = NGame.Instance
            ?? throw new InvalidOperationException("战斗内 Sts2Sim 合同测试要求活动宿主 NGame。");
        if (!PreCombatSts2SimBridge.HasInstalledBackendArtifact())
            throw new InvalidOperationException("战斗内 Sts2Sim 合同测试要求已安装 sts2sim/ 后端。");

        VerifyLiveDecimalPrivateFields(combatState);

        int startedTurn = player.PlayerCombatState?.TurnNumber
            ?? throw new InvalidOperationException("玩家缺少战斗状态。");

        SolverSettingsData savedSettings = SolverSettings.Current;
        bool prevAutoTurn = _protocolHost.AutomaticTurnSearchEnabledForTesting;
        try
        {
            SolverSettingsData contractSettings = savedSettings with
            {
                SolverDisabled = false,
                AutomaticCalculationEnabled = false,
                AutoEnableFullAuto = false,
                UseSts2SimInCombat = true,
                SearchMaxDegreeOfParallelism = 4,
                PerformancePreset = SolverPerformancePreset.Custom,
                SearchBeamWidth = 24,
                SearchMaxExpandedNodes = 4_000,
                SearchMaxCardBranchesPerNode = 32,
                SearchMaxPileChoiceBranchesPerAction = 18,
                SearchMaxHandChoiceBranchesPerAction = 24,
                SearchTimeLimitSeconds = 15d,
                UseBeamWidthPortfolio = true,
                UseNoveltyPortfolio = false,
                UseEarlyTurnExploration = false,
            };
            SolverSettings.ApplyForTesting(contractSettings);

            SetStage("verify_in_combat_sts2sim_turn1");
            long successBeforeTurn1 = InCombatSolverBackendState.Sts2SimSuccessCountForTesting;
            SolverController.RequestSearch(host, combatState, SearchReason.Manual);
            SolverResult turn1Result = await WaitForManualSearchResultAsync(host, TimeSpan.FromSeconds(25));

            if (turn1Result.StartTurnNumber != startedTurn || turn1Result.BestNode.Actions.Count == 0)
            {
                throw new InvalidOperationException(
                    $"第 {startedTurn} 回合 Sts2Sim 返回无效路线：turn={turn1Result.StartTurnNumber}, actions={turn1Result.BestNode.Actions.Count}。");
            }
            turn1Result.AssertCompleteTurnOutcomes();
            VerifyInCombatP2Contracts(turn1Result);
            if (InCombatSolverBackendState.LastUsedBackendForTesting != InCombatBackendKind.Sts2Sim
                || InCombatSolverBackendState.Sts2SimSuccessCountForTesting != successBeforeTurn1 + 1)
            {
                throw new InvalidOperationException(
                    $"第 {startedTurn} 回合未使用 Sts2Sim 后端：last={InCombatSolverBackendState.LastUsedBackendForTesting}, " +
                    $"exclusion={InCombatSolverBackendState.LastExclusionCodeForTesting}({InCombatSolverBackendState.LastExclusionDetailForTesting}), " +
                    $"fallback={InCombatSolverBackendState.LastFallbackCodeForTesting}({InCombatSolverBackendState.LastFallbackDetailForTesting})。");
            }
            int expectedMinDop = System.Environment.ProcessorCount >= 2 ? 2 : 1;
            int actualDop = InCombatSolverBackendState.LastEffectiveParallelismForTesting ?? 0;
            if (actualDop < expectedMinDop || actualDop > 4)
            {
                throw new InvalidOperationException(
                    $"第 {startedTurn} 回合 Sts2Sim 有效并行度超出预期：{actualDop}（期望 {expectedMinDop}..4）。");
            }
            SolverOverlay.RefreshControls();
            if (!SolverOverlay.InCombatBackendBadgeTextForTesting.Contains("Sts2Sim", StringComparison.Ordinal)
                || !SolverOverlay.InCombatBackendBadgeTooltipForTesting.Contains("Sts2Sim", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"战斗内 Sts2Sim 徽章文本未显示 Sts2Sim：text='{SolverOverlay.InCombatBackendBadgeTextForTesting}'");
            }

            SetStage("verify_in_combat_sts2sim_deploy_turn1");
            int cachedContinuationCount = turn1Result.Continuations.Count;
            if (turn1Result.SearchedTurns >= 2 && cachedContinuationCount == 0)
            {
                throw new InvalidOperationException(
                    $"第 {startedTurn} 回合 Sts2Sim 搜索规划了 {turn1Result.SearchedTurns} 回合，但未标注跨回合续用点。");
            }
            await SolverController.DeployResolvedTurnForTesting(host, combatState, turn1Result);
            await WaitForPlayableTurnAsync(host, combatState, player, startedTurn + 1, TimeSpan.FromSeconds(25));
            if (SolverController.UnexpectedReplanCountForTesting != 0)
            {
                throw new InvalidOperationException(
                    $"Turn 1 Sts2Sim 部署发生计划外重算：count={SolverController.UnexpectedReplanCountForTesting}。");
            }

            SetStage("verify_in_combat_sts2sim_turn2_transplant");
            long successBeforeTurn2 = InCombatSolverBackendState.Sts2SimSuccessCountForTesting;
            SolverController.RequestSearch(host, combatState, SearchReason.Manual);
            SolverResult turn2Result = await WaitForManualSearchResultAsync(host, TimeSpan.FromSeconds(25));
            if (turn2Result.StartTurnNumber != startedTurn + 1 || turn2Result.BestNode.Actions.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Turn 2 中途移植 Sts2Sim 返回无效路线：turn={turn2Result.StartTurnNumber}, actions={turn2Result.BestNode.Actions.Count}。");
            }
            turn2Result.AssertCompleteTurnOutcomes();
            if (InCombatSolverBackendState.LastUsedBackendForTesting != InCombatBackendKind.Sts2Sim
                || InCombatSolverBackendState.Sts2SimSuccessCountForTesting != successBeforeTurn2 + 1)
            {
                throw new InvalidOperationException(
                    $"Turn 2 中途移植未使用 Sts2Sim 后端：last={InCombatSolverBackendState.LastUsedBackendForTesting}, " +
                    $"exclusion={InCombatSolverBackendState.LastExclusionCodeForTesting}({InCombatSolverBackendState.LastExclusionDetailForTesting}), " +
                    $"fallback={InCombatSolverBackendState.LastFallbackCodeForTesting}({InCombatSolverBackendState.LastFallbackDetailForTesting})。");
            }

            SetStage("verify_in_combat_user_disabled_fallback");
            long successBeforeDisabled = InCombatSolverBackendState.Sts2SimSuccessCountForTesting;
            SolverSettings.ApplyForTesting(contractSettings with { UseSts2SimInCombat = false });
            SolverController.RequestSearch(host, combatState, SearchReason.Manual);
            SolverResult builtInResult = await WaitForManualSearchResultAsync(host, TimeSpan.FromSeconds(25));
            builtInResult.AssertCompleteTurnOutcomes();
            if (InCombatSolverBackendState.LastUsedBackendForTesting != InCombatBackendKind.BuiltIn
                || InCombatSolverBackendState.Sts2SimSuccessCountForTesting != successBeforeDisabled)
            {
                throw new InvalidOperationException(
                    $"关闭 UseSts2SimInCombat 后未切回内置模拟器：last={InCombatSolverBackendState.LastUsedBackendForTesting}。");
            }
            SolverOverlay.RefreshControls();
            if (SolverOverlay.InCombatBackendBadgeTextForTesting.Contains("Sts2Sim", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"关闭 Sts2Sim 后界面徽章仍显示 Sts2Sim：'{SolverOverlay.InCombatBackendBadgeTextForTesting}'");
            }

            SetStage("verify_in_combat_policy_fallback");
            PreCombatSts2SimBridge.EnsureActivated(out _);
            bool v2NoveltySupported = PreCombatSts2SimBridge.GetActiveCapabilities().SupportsNoveltyPortfolio;
            if (v2NoveltySupported)
            {
                SolverSettings.ApplyForTesting(contractSettings with
                {
                    UseSts2SimInCombat = true,
                    UseNoveltyPortfolio = true,
                });
                SolverController.RequestSearch(host, combatState, SearchReason.Manual);
                SolverResult v2NoveltyResult = await WaitForManualSearchResultAsync(host, TimeSpan.FromSeconds(25));
                v2NoveltyResult.AssertCompleteTurnOutcomes();
                if (InCombatSolverBackendState.LastUsedBackendForTesting != InCombatBackendKind.Sts2Sim)
                {
                    throw new InvalidOperationException(
                        $"v2 后端支持 UseNoveltyPortfolio 但未在 Sts2Sim 上运行：last={InCombatSolverBackendState.LastUsedBackendForTesting}。");
                }
            }

            PreCombatSts2SimBridgeCapabilitiesOverrideGuard capabilitiesGuard = v2NoveltySupported
                ? new PreCombatSts2SimBridgeCapabilitiesOverrideGuard(Sts2SimBridgeCapabilitiesDto.DefaultV1)
                : default;
            using (capabilitiesGuard)
            {
                SolverSettings.ApplyForTesting(contractSettings with
                {
                    UseSts2SimInCombat = true,
                    UseNoveltyPortfolio = true,
                });
                SolverController.RequestSearch(host, combatState, SearchReason.Manual);
                SolverResult policyFallbackResult = await WaitForManualSearchResultAsync(host, TimeSpan.FromSeconds(25));
                policyFallbackResult.AssertCompleteTurnOutcomes();
                if (InCombatSolverBackendState.LastUsedBackendForTesting != InCombatBackendKind.BuiltIn)
                {
                    throw new InvalidOperationException(
                        $"开启 UseNoveltyPortfolio 且能力为 v1 时未回退内置模拟器：last={InCombatSolverBackendState.LastUsedBackendForTesting}。");
                }
            }

            _completedChecks.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"InCombatSts2SimContract:Turn1Dop={actualDop}:Turn1Continuations={cachedContinuationCount}:Turn2Transplant=Sts2Sim:DisabledFallback=BuiltIn:PolicyFallback=BuiltIn:V2Novelty={v2NoveltySupported}"));
        }
        finally
        {
            SolverSettings.ApplyForTesting(savedSettings);
            _protocolHost.SetAutomaticTurnSearchEnabledForTesting(prevAutoTurn);
        }
    }

    private async Task VerifyExternalPileEffectsAsync(CombatState live, Player player)
    {
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var potion in player.PotionSlots.ToArray()) potion?.Discard();
        foreach (var (id, pile, count, manual) in new[]
        {
            ("LIQUID_MEMORIES", PileType.Discard, 1, false),
            ("DROPLET_OF_PRECOGNITION", PileType.Draw, 1, false),
            ("TOUCH_OF_INSANITY", PileType.Hand, 1, false),
            ("LIQUID_MEMORIES", PileType.Discard, 0, false),
            ("LIQUID_MEMORIES", PileType.Discard, 2, true),
            ("ASHWATER", PileType.Hand, 1, true),
        })
        {
            await ClearPlayerPilesAsync(player);
            for (int i = 0; i < count; i++)
                await InjectCardAsync(live, player, new() { CardId = "STRIKE_IRONCLAD", Pile = pile.ToString() });
            var potion = InjectPotionForTest(player, id);
            var slot = player.GetPotionSlotIndex(potion);
            var root = CombatRootSnapshot.Capture(live);
            var names = SolverDisplayNames.Capture(live);
            var damage = BattleDamageTracker.Observe(live);
            var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), live, false, null);
            var spec = PotionChoiceSupport.GetSpec(root.ForkSimulator(), potion);
            var selected = new PlanCardToken("STRIKE_IRONCLAD", 0, "", Math.Max(0, count - 1), Math.Max(0, count - 1), "");
            var explicitChoice = new PlanCardChoice(spec.Effect, pile, id == "ASHWATER" ? [] : [selected]);
            var wire = new PlanAction(PlanActionKind.UsePotion, root.StartTurnNumber, PotionSlot: slot, PotionId: id,
                Choice: manual ? explicitChoice : null);
            var pcs = player.PlayerCombatState!;
            var turn = root.StartTurnNumber;
            var expectedHand = pcs.Hand.Cards.Count + (pile != PileType.Hand && count > 0 ? 1 : 0);
            var terminal = new ExternalRouteTerminalExpectation(turn, false, false, player.Creature.CurrentHp,
                player.Creature.MaxHp, player.Creature.Block, live.Enemies.Sum(enemy => enemy.CurrentHp),
                live.Enemies.Count, pcs.Energy, pcs.Stars, expectedHand, damage.HpLostSoFar, null, null);
            ExternalRouteVerificationResult Verify(PlanAction action) => CombatBeamSolver.VerifyAndAnnotateExternalRoute(
                root, names, damage, policy, [action], new Dictionary<int, ExternalRouteTurnExpectation>
                { [turn] = new(turn, 0, 0, 0, pcs.Energy, false) }, terminal, [new(turn, slot, id)], false, default);

            var valid = Verify(wire);
            if (valid.Status != ExternalRouteVerificationStatus.Annotated)
                throw new InvalidOperationException($"{id}/{count} rejected: {valid.FailureReason}");
            var annotated = valid.AnnotatedActions.Single();
            if (count == 0 ? annotated.Choice != null : annotated.Choice is null
                || !annotated.Choice.Cards.Select(c => c.SourceOccurrence)
                    .SequenceEqual(explicitChoice.Cards.Select(c => c.SourceOccurrence)))
                throw new InvalidOperationException($"{id}/{count} annotated the wrong instance.");
            var invalid = Verify(wire with { Choice = manual ? null : explicitChoice });
            if (invalid.Status != ExternalRouteVerificationStatus.IllegalRoute)
                throw new InvalidOperationException($"{id}/{count} accepted a missing or extra explicit decision.");

            var driver = new CombatBeamSolver(root, names, damage, policy);
            var replay = driver.ReplayDiagnosticPrefix([annotated]);
            MoveStateSnapshot predicted;
            try
            {
                var sim = (CombatPredictionSimulator)replay.Simulator;
                predicted = CaptureSimulated(sim, (SimulatedCombatState)sim.State.CombatState, player, live.Enemies[0]);
            }
            finally { replay.ReleaseSimulator(); }
            var nativeSelector = new PlannedCardSelector(manual ? [explicitChoice] : []);
            using (NativeTestCardSelectors.Push(nativeSelector))
            {
                potion.EnqueueManualUse(null);
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            }
            nativeSelector.AssertConsumed();
            AssertSnapshotEqual(predicted, CaptureActual(live, player, live.Enemies[0]), id, $"ExternalPileChoice/{count}");
            _completedChecks.Add($"ExternalPileChoice:{id}:{count}");
        }

        foreach (var (id, drawCount) in new[] { ("HIDDEN_DAGGERS", 0), ("SEEKER_STRIKE", 1), ("SEEKER_STRIKE", 2) })
        {
            await ClearPlayerPilesAsync(player);
            SetEnergy(player, 3);
            await InjectCardAsync(live, player, new() { CardId = id, Pile = "Hand" });
            for (int i = 0; i < drawCount; i++)
                await InjectCardAsync(live, player, new() { CardId = "STRIKE_IRONCLAD", Pile = "Draw" });
            var enemy = live.Enemies[0];
            var target = id == "SEEKER_STRIKE" ? enemy : null;
            var actualCard = FindActualHandCard(player, id, 0);
            var sim = CombatRootSnapshot.Capture(live).ForkSimulator();
            var combat = (SimulatedCombatState)sim.State.CombatState;
            List<PlanCardChoice> bound = [];
            var external = new TurnStartChoiceCursor([], true, new ExternalChoiceBinding(id, bound.Add));
            combat.BeginActionChoices(external);
            try
            {
                using (combat.BeginCardExecutionScope(new HashSet<uint>()))
                    sim.ManualPlay(sim.State.FindCard(actualCard)!, target, out _);
            }
            finally { combat.EndActionChoices(); }
            if (drawCount == 2)
            {
                if (combat.PendingTurnStartChoice?.Spec?.Options.Count != 2 || bound.Count != 0)
                    throw new InvalidOperationException("Two Seeker Strike options require a decision.");
                _completedChecks.Add("ExternalPileChoice:SEEKER_STRIKE:TwoOptionsPending");
                continue;
            }
            if (sim.HasPendingChoice || bound.Count != 1 || bound[0].Cards.Count != drawCount)
                throw new InvalidOperationException($"{id}/{drawCount} failed to bind its native selection.");
            var predicted = CaptureSimulated(sim, combat, player, enemy);
            using (NativeTestCardSelectors.Push(new PlannedCardSelector(Array.Empty<PlanCardChoice>())))
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var action = await SolverController.EnqueueAndCaptureActionAsync(
                    queued => queued is PlayCardAction play && ReferenceEquals(play.NetCombatCard.ToCardModelOrNull(), actualCard),
                    () => { if (!actualCard.TryManualPlay(target)) throw new InvalidOperationException($"{id} refused native play."); },
                    deadline.Token);
                await action.CompletionTask.WaitAsync(deadline.Token);
            }
            AssertSnapshotEqual(predicted, CaptureActual(live, player, enemy), id, "ExternalPileChoice");
            _completedChecks.Add($"ExternalPileChoice:{id}:{drawCount}");
        }
    }

    private static async Task VerifyExternalImplicitChoicesAsync(CombatState combat, Player player)
    {
        // Three equal-looking instances make source and filtered-option occurrences distinguishable.
        PredictedCard[] source = Enumerable.Range(0, 3)
            .Select(_ => PredictedCard.Create(CanonicalModels.Card<StrikeIronclad>(), player)).ToArray();
        var simulator = CombatRootSnapshot.Capture(combat).ForkSimulator();
        var discard = simulator.State.GetPlayerCombatState(player).DiscardPile;
        var hologram = PredictedCard.Create(CanonicalModels.Card<Hologram>(), player);
        discard.Clear();
        if (CardChoiceSupport.GetSpec(simulator, hologram) is not null)
            throw new InvalidOperationException("Hologram offered an empty discard choice.");
        discard.Add(source[0]);
        if (CardChoiceSupport.GetSpec(simulator, hologram) is not { IsImplicitAllSelection: true, Options.Count: 1 })
            throw new InvalidOperationException("Hologram did not mark its only discard as implicit.");
        discard.Add(source[1]);
        if (CardChoiceSupport.GetSpec(simulator, hologram) is not { IsImplicitAllSelection: false, Options.Count: 2 })
            throw new InvalidOperationException("Hologram inferred a choice from two discards.");

        const string sourceId = "HOLOGRAM";
        const PlanChoiceEffect effect = PlanChoiceEffect.MoveToHand;
        const PileType pile = PileType.Discard;
        TurnStartChoiceRequest Request(PredictedCard[] options, int min, int max, bool implicitAll = false)
            => new(sourceId, effect, pile, min,
                new CardChoiceSpec(effect, pile, min, max, options, source, 0, IsImplicitAllSelection: implicitAll));

        var one = Request([source[1]], 1, 1, implicitAll: true);
        var both = Request([source[1], source[2]], 2, 2, implicitAll: true);
        var multiple = Request(source, 1, 1);
        var confirmOne = Request([source[1]], 1, 1);
        var optional = Request([source[1]], 0, 1);
        var pickThird = new PlanCardChoice(effect, pile,
            [new PlanCardToken(source[2].Preview.Id.Entry, 0, "", 2, 2, "")], SourceId: sourceId);
        var confirm = pickThird with { Cards = [pickThird.Cards[0] with { SourceOccurrence = 1, OptionOccurrence = 0 }] };
        var skip = pickThird with { Cards = [] };
        PlanCardChoice[] wire = [pickThird, confirm, skip];
        List<PlanCardChoice> bound = [];
        var cursor = new TurnStartChoiceCursor(wire, validateSelectionAgainstSpec: true,
            externalBinding: new ExternalChoiceBinding(sourceId, bound.Add));
        var nativeSelector = new PlannedCardSelector(wire);
        using var selectorScope = NativeTestCardSelectors.Push(nativeSelector);

        async Task<CardModel[]> SelectNative(TurnStartChoiceRequest request, bool manual = false)
        {
            CardChoiceSpec spec = request.Spec!;
            var prefs = new CardSelectorPrefs(CardSelectorPrefs.DiscardSelectionPrompt, spec.MinCount, spec.MaxCount);
            if (manual)
                prefs = prefs with { RequireManualConfirmation = true };
            return (await CardSelectCmd.FromSimpleGrid(new ThrowingPlayerChoiceContext(),
                spec.Options.Select(card => card.Preview).ToArray(), player, prefs)).ToArray();
        }

        if ((await SelectNative(Request([], 1, 1))).Length != 0)
            throw new InvalidOperationException("Native zero-option selection returned a card.");

        async Task CheckStep(TurnStartChoiceRequest request, int consumed, int[] optionIndices, int[] sourceIndices,
            bool matchIfPresent = false, bool manual = false)
        {
            CardModel[] actual = await SelectNative(request, manual);
            PlanCardChoice? choice;
            bool resolved = matchIfPresent ? cursor.TryTakeIfMatches(request, out choice) : cursor.TryTake(request, out choice);
            if (!resolved || choice is null || choice.SourceId != sourceId || choice.Timing != request.Timing
                || cursor.ConsumedExplicitChoiceCount != consumed
                || !choice.Cards.Select(card => card.OptionOccurrence).SequenceEqual(optionIndices)
                || !choice.Cards.Select(card => card.SourceOccurrence).SequenceEqual(sourceIndices)
                || !actual.SequenceEqual(sourceIndices.Select(i => source[i].Preview)))
                throw new InvalidOperationException($"External choice differs from native selection at bound step {bound.Count}.");
            TurnStartChoiceCursor.ValidateChoiceMatchesSpec(choice, request.Spec!);
        }

        await CheckStep(one, 0, [0], [1]);
        await CheckStep(both, 0, [0, 1], [1, 2], matchIfPresent: true);
        await CheckStep(multiple, 1, [2], [2]);
        await CheckStep(confirmOne, 2, [0], [1], matchIfPresent: true, manual: true);
        await CheckStep(optional, 3, [], []);
        cursor.AssertConsumed();
        nativeSelector.AssertConsumed();
        if (bound.Count != 5
            || !bound.SelectMany(choice => choice.Cards).Select(card => card.SourceOccurrence).SequenceEqual([1, 1, 2, 2, 1]))
            throw new InvalidOperationException("External action annotation lost or reordered a choice.");

        var missing = new TurnStartChoiceCursor([], validateSelectionAgainstSpec: true,
            externalBinding: new ExternalChoiceBinding(sourceId));
        if (missing.TryTake(multiple, out _) || missing.TryTakeIfMatches(confirmOne, out _) || missing.TryTake(optional, out _))
            throw new InvalidOperationException("A missing manual or optional decision was inferred.");
        var builtIn = new TurnStartChoiceCursor([]);
        if (builtIn.TryTake(one, out _) || builtIn.TryTakeIfMatches(both, out _))
            throw new InvalidOperationException("The built-in search cursor consumed an unscripted choice.");

        var extra = new TurnStartChoiceCursor([confirm], validateSelectionAgainstSpec: true,
            externalBinding: new ExternalChoiceBinding(sourceId));
        if (!extra.TryTake(one, out _) || extra.ConsumedExplicitChoiceCount != 0)
            throw new InvalidOperationException("An implicit request consumed an extra explicit choice.");
        try
        {
            extra.AssertConsumed();
        }
        catch (InvalidPlannedChoiceBranchException)
        {
            return;
        }
        throw new InvalidOperationException("An extra wire decision for an implicit choice was accepted.");
    }

    private void VerifyLiveDecimalPrivateFields(CombatState combatState)
    {
        var cardHandles = new Dictionary<CardModel, int>();
        var shot = (TheShot)ModelDb.Card<TheShot>().ToMutable();
        shot._cardsExhaustedByFlorescence = 1.75m;
        var cardFields = InCombatLiveStateCapturer.CaptureCardPrivateFields(
            shot, combatState, cardHandles, [], new Dictionary<CardModel, int>());

        var monologue = (MonologuePower)ModelDb.Power<MonologuePower>().ToMutable();
        monologue.DynamicVars["StrengthApplied"].BaseValue = 2.5m;
        monologue.DynamicVars.Strength.BaseValue = 3.25m;
        var powerFields = InCombatLiveStateCapturer.CapturePowerPrivateFields(
            monologue, new Dictionary<Creature, int>(), cardHandles, []);
        if (cardFields["TheShot._cardsExhaustedByFlorescence"] != new InCombatFieldValueDto("Decimal", DecimalValue: 1.75m)
            || powerFields["MonologuePower._strengthApplied"] != new InCombatFieldValueDto("Decimal", DecimalValue: 2.5m)
            || powerFields["MonologuePower._strength"] != new InCombatFieldValueDto("Decimal", DecimalValue: 3.25m))
        {
            throw new InvalidOperationException("战斗内快照未保留原生卡牌与能力的小数值。");
        }
        _completedChecks.Add("InCombatPrivateDecimalFields");
    }

    private static async Task<SolverResult> WaitForManualSearchResultAsync(NGame host, TimeSpan timeout)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (SolverController.LastSearchFailureForTesting is { } failure)
                throw new InvalidOperationException($"战斗内搜索失败：{failure}");
            if (!SolverController.IsSearching)
            {
                if (SolverController.LastCompletedResultForTesting is { } result)
                    return result;
                throw new InvalidOperationException(
                    $"战斗内搜索提前结束但未产生结果：exclusion={InCombatSolverBackendState.LastExclusionCodeForTesting}({InCombatSolverBackendState.LastExclusionDetailForTesting}), " +
                    $"fallback={InCombatSolverBackendState.LastFallbackCodeForTesting}({InCombatSolverBackendState.LastFallbackDetailForTesting})。");
            }
            await host.ToSignal(host.GetTree(), Godot.SceneTree.SignalName.ProcessFrame);
        }
        throw new TimeoutException(
            $"等待战斗内搜索完成超时（{timeout.TotalSeconds:0.#} 秒）：active={InCombatSolverBackendState.ActiveBackendForTesting}, " +
            $"last={InCombatSolverBackendState.LastUsedBackendForTesting}, " +
            $"exclusion={InCombatSolverBackendState.LastExclusionCodeForTesting}, " +
            $"fallback={InCombatSolverBackendState.LastFallbackCodeForTesting}({InCombatSolverBackendState.LastFallbackDetailForTesting})。");
    }

    private static async Task WaitForPlayableTurnAsync(
        NGame host,
        CombatState combatState,
        Player player,
        int expectedTurn,
        TimeSpan timeout)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (!CombatManager.Instance.IsInProgress || CombatManager.Instance.IsOverOrEnding)
                throw new InvalidOperationException($"等待第 {expectedTurn} 回合期间战斗提前结束。");
            if (ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), combatState)
                && combatState.CurrentSide == CombatSide.Player
                && player.PlayerCombatState?.Phase == MegaCrit.Sts2.Core.Combat.PlayerTurnPhase.Play
                && player.PlayerCombatState.TurnNumber == expectedTurn
                && !SolverController.IsDeploying
                && !CombatManager.Instance.PlayerActionsDisabled
                && !RunManager.Instance.ActionExecutor.IsRunning
                && RunManager.Instance.ActionExecutor.FinishedExecutingActions().IsCompleted)
            {
                return;
            }
            await host.ToSignal(host.GetTree(), Godot.SceneTree.SignalName.ProcessFrame);
        }
        throw new TimeoutException($"等待进入第 {expectedTurn} 回合超时（{timeout.TotalSeconds:0.#} 秒）。");
    }

    private readonly struct PreCombatSts2SimBridgeCapabilitiesOverrideGuard : IDisposable
    {
        private readonly bool _active;

        public PreCombatSts2SimBridgeCapabilitiesOverrideGuard(Sts2SimBridgeCapabilitiesDto capabilities)
        {
            PreCombatSts2SimBridge.CapabilitiesOverrideForTesting = capabilities;
            _active = true;
        }

        public void Dispose()
        {
            if (_active)
                PreCombatSts2SimBridge.CapabilitiesOverrideForTesting = null;
        }
    }

    private static void VerifyInCombatP2Contracts(SolverResult turn1Result)
    {
        // ApplyCurrentTurn derives expected attempt strictly from RenderedProgress and rejects unpublished attempts.
        SearchInteractionState interaction = new(generation: 1);
        SolverCurrentTurnPreview preview0 = SolverCurrentTurnPreview.FromResult(turn1Result, candidateVersion: 1);
        long renderTick = Environment.TickCount64 + 10_000;
        interaction.PublishProgress(new SolverProgress(
            StartTurnNumber: turn1Result.StartTurnNumber,
            CurrentTurnNumber: turn1Result.StartTurnNumber,
            CompletedTurnLayers: 0,
            PlayDepth: 0,
            ExpandedNodes: turn1Result.ExpandedNodes,
            ReviewedWorldlines: turn1Result.TotalExpandedNodes,
            MaxNodes: 100,
            FrontierNodes: 0,
            EndedNodes: 0,
            ElapsedMilliseconds: 10,
            Phase: "Sts2Sim",
            CurrentTurnPreview: preview0,
            BackendAttempt: 0));
        if (interaction.CurrentAttemptTurnPreviewBackendAttempt is not null
            || interaction.RequestApplyCurrentTurn(expectedBackendAttempt: 0)
            || interaction.RequestApplyCurrentTurn())
        {
            throw new InvalidOperationException("Unpublished-to-UI attempt-0 preview allowed ApplyCurrentTurn before TryCreateDisplayProgress.");
        }
        if (!interaction.TryCreateDisplayProgress(renderTick, out _)
            || interaction.CurrentAttemptTurnPreviewBackendAttempt != 0)
        {
            throw new InvalidOperationException("Expected attempt-0 rendered turn preview before fallback.");
        }
        if (!interaction.PrepareFallbackToBuiltIn(generation: 1, failedBackendAttempt: 0))
            throw new InvalidOperationException("Fallback transition to attempt 1 unexpectedly failed.");
        if (interaction.CurrentAttemptTurnPreviewBackendAttempt is not null
            || interaction.RequestApplyCurrentTurn(expectedBackendAttempt: 0)
            || interaction.RequestApplyCurrentTurn())
        {
            throw new InvalidOperationException("Stale attempt-0 ApplyCurrentTurn was accepted before attempt-1 preview arrived.");
        }
        interaction.PublishProgress(new SolverProgress(
            StartTurnNumber: turn1Result.StartTurnNumber,
            CurrentTurnNumber: turn1Result.StartTurnNumber,
            CompletedTurnLayers: 0,
            PlayDepth: 0,
            ExpandedNodes: 1,
            ReviewedWorldlines: 1,
            MaxNodes: 100,
            FrontierNodes: 0,
            EndedNodes: 0,
            ElapsedMilliseconds: 12,
            Phase: "BuiltIn",
            CurrentTurnPreview: preview0,
            BackendAttempt: 1));
        if (interaction.CurrentAttemptTurnPreviewBackendAttempt is not null
            || interaction.RequestApplyCurrentTurn(expectedBackendAttempt: 1)
            || interaction.RequestApplyCurrentTurn(expectedBackendAttempt: 0)
            || interaction.RequestApplyCurrentTurn())
        {
            throw new InvalidOperationException("Unrendered attempt-1 Progress allowed ApplyCurrentTurn before TryCreateDisplayProgress.");
        }
        if (!interaction.TryCreateDisplayProgress(renderTick + 10_000, out _)
            || interaction.CurrentAttemptTurnPreviewBackendAttempt != 1
            || interaction.RequestApplyCurrentTurn(expectedBackendAttempt: 0)
            || !interaction.RequestApplyCurrentTurn(expectedBackendAttempt: 1))
        {
            throw new InvalidOperationException("Rendered attempt-1 ApplyCurrentTurn validation failed.");
        }

        // ExternalChoiceBinding binds empty ContextId and repeated manual card SourceId at replay time.
        var seekerToken = new PlanCardToken("STRIKE_IRONCLAD", 0, string.Empty, 0, 0, string.Empty);
        var seekerTraceChoice = new PlanCardChoice(
            PlanChoiceEffect.MoveToHand,
            PileType.Draw,
            [seekerToken],
            SourceId: "SEEKER_STRIKE",
            ContextId: string.Empty,
            Timing: PlanChoiceTiming.Action);
        List<PlanCardChoice> seekerBound = [];
        var seekerCursor = new TurnStartChoiceCursor(
            [seekerTraceChoice],
            externalBinding: new ExternalChoiceBinding("SEEKER_STRIKE", seekerBound.Add));
        var seekerRequest = new TurnStartChoiceRequest(
            string.Empty,
            PlanChoiceEffect.MoveToHand,
            PileType.Draw,
            1,
            ContextId: "seeker:3:STRIKE_IRONCLAD+0",
            Timing: PlanChoiceTiming.Action);
        if (!seekerCursor.TryTake(seekerRequest, out PlanCardChoice? boundSeeker)
            || boundSeeker is null
            || boundSeeker.SourceId != string.Empty
            || boundSeeker.ContextId != "seeker:3:STRIKE_IRONCLAD+0"
            || seekerBound.Count != 1)
        {
            throw new InvalidOperationException("ExternalChoiceBinding failed to bind SeekerStrike context.");
        }
        seekerCursor.AssertConsumed();

        var repeatedChoice = new PlanCardChoice(
            PlanChoiceEffect.Exhaust,
            PileType.Hand,
            [seekerToken],
            SourceId: "BURNING_PACT",
            ContextId: string.Empty,
            Timing: PlanChoiceTiming.Action);
        List<PlanCardChoice> repeatedBound = [];
        var repeatedCursor = new TurnStartChoiceCursor(
            [repeatedChoice, repeatedChoice],
            externalBinding: new ExternalChoiceBinding("BURNING_PACT", repeatedBound.Add));
        var manualRequest = new TurnStartChoiceRequest(
            string.Empty,
            PlanChoiceEffect.Exhaust,
            PileType.Hand,
            1,
            ContextId: string.Empty,
            Timing: PlanChoiceTiming.Action);
        if (!repeatedCursor.TryTake(manualRequest, out PlanCardChoice? firstManual)
            || !repeatedCursor.TryTake(manualRequest, out PlanCardChoice? secondManual)
            || firstManual?.SourceId != string.Empty
            || secondManual?.SourceId != string.Empty
            || repeatedBound.Count != 2)
        {
            throw new InvalidOperationException("ExternalChoiceBinding failed on repeated manual card choices.");
        }
        repeatedCursor.AssertConsumed();

        var mismatchCursor = new TurnStartChoiceCursor(
            [seekerTraceChoice with { ContextId = "wrong-context" }],
            externalBinding: new ExternalChoiceBinding("SEEKER_STRIKE"));
        bool mismatchRejected = false;
        try
        {
            mismatchCursor.TryTake(seekerRequest, out _);
        }
        catch (InvalidPlannedChoiceBranchException)
        {
            mismatchRejected = true;
        }
        if (!mismatchRejected)
            throw new InvalidOperationException("ExternalChoiceBinding failed to reject mismatched non-empty ContextId.");

        // Materialized potion and battle-loss summaries match verified replay.
        int explicitPotions = turn1Result.BestNode.Actions.Count(static a => a.Kind == PlanActionKind.UsePotion);
        if (turn1Result.PotionCount != turn1Result.PotionUses.Count
            || turn1Result.PlannedPotionIds.Length != turn1Result.PotionUses.Count
            || turn1Result.ExplicitPotionCount != explicitPotions
            || turn1Result.PotionCountByTurn.Values.Sum() != turn1Result.PotionUses.Count
            || turn1Result.PotionStrategicCostByTurn.Values.Sum() != turn1Result.PotionHpRequired
            || turn1Result.ProjectedBattleHpLost < turn1Result.BattleHpLostSoFar + turn1Result.Snapshot.CumulativePlayerHpLost)
        {
            throw new InvalidOperationException("Materialized SolverResult potion or battle-loss summary is inconsistent with verified replay.");
        }
    }
}
