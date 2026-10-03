using System.Collections;
using System.Reflection;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertOpeningDiscardChoiceValueAsync(CombatState combat, Player player)
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Opening discard value: " + message);
        }
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in combat.Creatures.SelectMany(creature => creature.Powers).ToArray())
            await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        await CreatureCmd.SetCurrentHp(combat.Enemies.Single(), 1000);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "ACROBATICS", Pile = "Hand" });
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "FLICK_FLACK", Pile = "Draw" });
        await InjectCardAsync(combat, player, new UnattendedCardInjection
            { CardId = "FLICK_FLACK", Pile = "Draw", DynamicVars = new() { ["Damage"] = 17 } });
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "NEUTRALIZE", Pile = "Draw", UpgradeLevels = 1 });
        foreach (CardModel card in player.PlayerCombatState!.DrawPile.Cards.Where(card => card.Id.Entry == "FLICK_FLACK"))
            card.SetToFreeThisTurn();
        SetEnergy(player, 3);
        string liveBefore = ContinuationStamp.CaptureLive(combat).StateText;
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null) with
        {
            VerifyIncrementalSearch = true, MaxDegreeOfParallelism = 1,
            PotionPolicy = SolverPotionPolicy.Disabled, FixedBudget = true,
            Profile = SolverSearchProfile.Default with { BeamWidth = 8, MaxExpandedNodes = 100,
                MaxCardBranchesPerNode = 24, SoftTimeBudgetMilliseconds = 5000 },
        };
        CombatBeamSolver driver = new(root, names, damage, policy, searchProfile: policy.Profile);
        SimulationSnapshot seed = InvokeForcedTerminalReplay(driver, [], null, 0, null);
        PlanAction action = new(PlanActionKind.PlayCard, root.StartTurnNumber, CardId: "ACROBATICS");
        SimulationSnapshot probe = InvokeForcedTerminalReplay(driver, [action], seed, root.StartTurnNumber, null);
        SimulationSnapshot? nativeExpected = null;
        PlanCardChoice? nativeChoice = null;
        try
        {
            SimulatedCombatState pendingCombat = (SimulatedCombatState)probe.Simulator.State.CombatState;
            CardChoiceSpec spec = pendingCombat.PendingTurnStartChoice?.Spec
                ?? throw new InvalidOperationException("Opening discard fixture did not reach the primary choice.");
            PlanCardChoice[] choices = CardChoiceSupport.BuildChoices(spec, names, 24, 24).ToArray();
            Check(choices.Count(choice => choice.Cards.Single().CardId == "FLICK_FLACK") == 2,
                "duplicate-state choices must survive");
            MethodInfo valueMethod = typeof(CombatBeamSolver).GetMethod("OpeningDiscardChoiceCardValue",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            object cache = Activator.CreateInstance(valueMethod.GetParameters()[3].ParameterType)!;
            SearchNode seedNode = ForcedTerminalAnnotationNode(seed, null, null);
            int changedTokens = 0;
            foreach (PlanCardChoice choice in choices)
            {
                PlanCardToken token = choice.Cards.Single();
                PredictedCard selected = spec.Options.Where(card => CardChoiceSupport.MatchesToken(card, token))
                    .Skip(token.OptionOccurrence).First();
                double expectedValue = CardChoiceSupport.CardValue(selected.Preview);
                SimulationSnapshot completed = InvokeForcedTerminalReplay(driver,
                    [action with { Choice = choice }], seed, root.StartTurnNumber, null);
                try
                {
                    SearchNode child = ForcedTerminalAnnotationNode(completed, seedNode, action with { Choice = choice });
                    double value = (double)InvokeForcedTerminalMethod(driver, "OpeningDiscardChoiceCardValue",
                        [seedNode, child, choice, cache])!;
                    Check(value == expectedValue, "selection-time value differs for " + token.CardId);
                    SimPlayerCombatState after = completed.Simulator.State.GetPlayerCombatState(player);
                    bool retainedKey = after.Hand.Cards.Concat(after.DrawPile.Cards).Concat(after.DiscardPile.Cards)
                        .Concat(after.ExhaustPile.Cards).Any(card => CardChoiceSupport.ChoiceCardKey(card) == token.StateKey);
                    if (token.CardId == "FLICK_FLACK")
                    {
                        Check(!retainedKey, "temporary-free Sly card did not change state");
                        changedTokens++;
                        if (nativeExpected == null)
                        {
                            nativeExpected = completed;
                            nativeChoice = choice;
                        }
                    }
                    PlanCardChoice forged = choice with { Cards = [token with { StateKey = token.StateKey + "invalid" }] };
                    bool rejected = false;
                    try { InvokeForcedTerminalMethod(driver, "OpeningDiscardChoiceCardValue", [seedNode, child, forged, cache]); }
                    catch (InvalidOperationException error) when (error.Message.Contains("找不到选择时的卡牌", StringComparison.Ordinal)) { rejected = true; }
                    Check(rejected, "forged state key accepted");
                    rejected = false;
                    try { InvokeForcedTerminalMethod(driver, "OpeningDiscardChoiceCardValue",
                        [seedNode, child, choice with { ContextId = "foreign" }, cache]); }
                    catch (InvalidOperationException error) when (error.Message.Contains("选择上下文不一致", StringComparison.Ordinal)) { rejected = true; }
                    Check(rejected, "foreign choice context accepted");
                }
                finally { if (!ReferenceEquals(completed, nativeExpected)) completed.ReleaseSimulator(); }
            }
            Check(changedTokens == 2 && ((IDictionary)cache).Count == 1, "one immutable probe table per sibling action");
            foreach (int dop in new[] { 1, 2 })
            {
                CombatBeamSolver opening = new(root, names, damage, policy with
                    { MaxDegreeOfParallelism = dop, VerifyIncrementalSearch = dop == 1 }, searchProfile: policy.Profile);
                IReadOnlyList<PlanAction> candidates = await Task.Run(() => opening.BuildOpeningHandSetupActions());
                Check(candidates.Count > 0 && candidates.All(item => item.CardId == "ACROBATICS"),
                    "opening selection failed at DOP " + dop);
            }
            Check(ContinuationStamp.CaptureLive(combat).StateText == liveBefore, "probing changed live state");
            string expectedStamp = ContinuationStamp.CapturePredicted(player, nativeExpected!.Simulator,
                root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
            Check(ContinuationStamp.CapturePredicted(player, nativeExpected.Simulator.Fork(),
                root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText == expectedStamp, "completed fork state");
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(12));
            using NativeChoiceSession session = NativeChoiceRuntime.Begin(combat, player, "test:opening-discard-value");
            session.SetPlanAndStartDriving(NGame.Instance!, [nativeChoice! with { SourceId = "ACROBATICS" }], deadline.Token);
            CardModel playedCard = player.PlayerCombatState.Hand.Cards.Single();
            GameAction played = await SolverController.EnqueueAndCaptureActionAsync(
                queued => queued is PlayCardAction cardAction && ReferenceEquals(cardAction.NetCombatCard.ToCardModelOrNull(), playedCard),
                () => { if (!playedCard.TryManualPlay(null)) throw new InvalidOperationException("Native Acrobatics was not playable."); }, deadline.Token);
            await session.AwaitProducerAndCompleteAsync(played.CompletionTask).WaitAsync(deadline.Token);
            Check(ContinuationStamp.CaptureLive(combat).StateText == expectedStamp, "native complete state after Sly discard");
            _completedChecks.Add("OpeningDiscardValue:NewlyDrawn:TemporaryFreeSly:DuplicateDifferentDamage:FrozenSiblingValues:StrictTokenContext:OpeningDOP1DOP2:NativeFullStamp:ForkRootUnchanged");
        }
        finally
        {
            nativeExpected?.ReleaseSimulator();
            probe.ReleaseSimulator();
            seed.ReleaseSimulator();
        }
    }
}
