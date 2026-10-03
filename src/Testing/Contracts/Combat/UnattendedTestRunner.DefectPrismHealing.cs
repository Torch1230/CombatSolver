using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task RunDefectPrismHealingProbeAsync(CombatState live, Player player)
    {
        string liveBefore = ContinuationStamp.CaptureLive(live).StateText;
        int bookBefore = player.Relics.OfType<BookOfFiveRings>().Single().CardsAdded;
        RelicCounterTarget[] zeroAllowance = [new(RelicCounterId.Pendulum, 2, 2, 0, 3)];
        RelicCounterTarget[] paidAllowance = [new(RelicCounterId.Pendulum, 2, 2, 1, 3)];
        if (!CombatBeamSolver.CanUseStrictHpRelicBound(true, false, zeroAllowance)
            || CombatBeamSolver.CanUseStrictHpRelicBound(true, false, paidAllowance)
            || CombatBeamSolver.CanUseStrictHpRelicBound(false, false, zeroAllowance)
            || CombatBeamSolver.CanUseStrictHpRelicBound(true, true, zeroAllowance)
            || !CombatBeamSolver.ShouldPruneByPrimaryIncumbent(29, 2, new(28, 3), allowTurnTieBound: false)
            || CombatBeamSolver.ShouldPruneByPrimaryIncumbent(28, 99, new(28, 3), allowTurnTieBound: false)
            || CombatBeamSolver.ShouldPruneByPrimaryIncumbent(27, 99, new(28, 3), allowTurnTieBound: false))
            throw new InvalidOperationException("A zero-allowance counter objective must preserve equal/better HP at every turn; growth, unknown healing and paid allowance disable this bound.");
        PotionFreePolicyBaseline eligibilityBaseline = new(true, 28, 59, 3);
        if (CombatBeamSolver.SmartPotionEligibilityHpCeiling(eligibilityBaseline, 1, 9, 0, BossHpRelief.None) != 19
            || CombatBeamSolver.SmartPotionEligibilityHpCeiling(eligibilityBaseline, 2, 9, 0, BossHpRelief.None) != 10
            || CombatBeamSolver.SmartPotionEligibilityHpCeiling(eligibilityBaseline, 2, 9, 9, BossHpRelief.None) != 19
            || CombatBeamSolver.SmartPotionEligibilityHpCeiling(eligibilityBaseline, 1, 9, 99, BossHpRelief.None) != 27
            || CombatBeamSolver.SmartPotionEligibilityHpCeiling(eligibilityBaseline, 1, 0, 0, BossHpRelief.None) != 28
            || CombatBeamSolver.SmartPotionEligibilityHpCeiling(eligibilityBaseline, 1, 9, 0, BossHpRelief.ActClearHeal) != -17
            || CombatBeamSolver.SmartPotionEligibilityHpCeiling(eligibilityBaseline with { Won = false }, 1, 9, 0, BossHpRelief.None) is not null
            || CombatBeamSolver.SmartPotionEligibilityHpCeiling(eligibilityBaseline with { DeathSaveUseCount = 1 }, 1, 9, 0, BossHpRelief.None) is not null
            || CombatBeamSolver.SmartPotionEligibilityHpCeiling(null, 1, 9, 0, BossHpRelief.None) is not null
            || CombatBeamSolver.SmartPotionEligibilityHpCeiling(eligibilityBaseline, 0, 9, 0, BossHpRelief.None) is not null)
            throw new InvalidOperationException("Smart eligibility must retain the exact saving threshold, apply replacement credit once, and require a complete surviving no-death-save baseline.");
        CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
        CombatPredictionSimulator parent = root.ForkSimulator();
        string parentBefore = DescribeContinuationContractState(parent, root, player);
        CombatPredictionSimulator prediction = parent.Fork();
        SimulatedCombatState combat = (SimulatedCombatState)prediction.State.CombatState;
        CardModel[] nativeCards = [
            player.PlayerCombatState!.Hand.Cards.Single(card => card is Turbo),
            player.PlayerCombatState.Hand.Cards.Single(card => card is SporeMind),
            player.PlayerCombatState.Hand.Cards.Single(card => card is Compact),
        ];
        using (SimulationNotificationIsolation.Enter())
        {
            if (!root.CanCertifyRemainingHealing || root.InitialRemainingHealingUpperBound != 0
                || !combat.RootHasOnlyNonHealingLoadoutSubscribers
                || !StrategicHpRecoveryBound.CanCertifyDefectPrismHealingEnvironment(prediction, player))
                throw new InvalidOperationException("The restored player Prism root did not satisfy the closed native healing certificate: "
                    + System.Text.Json.JsonSerializer.Serialize(new
                    {
                        root.CanCertifyRemainingHealing,
                        root.InitialRemainingHealingUpperBound,
                        combat.RootHasOnlyNonHealingLoadoutSubscribers,
                        combat.RootHasBaseLibCardModifiers,
                        adaptedOnPlay = combat.AdaptedOnPlay is not null,
                        bound = StrategicHpRecoveryBound.DefectPrismHealingUpperBound(prediction, player, 0),
                        runSubscribers = MegaCrit.Sts2.Core.Modding.ModHelper.IterateAllRunStateSubscribers((RunState)live.RunState)
                            .Select(model => model.GetType().FullName).ToArray(),
                        combatSubscribers = MegaCrit.Sts2.Core.Modding.ModHelper.IterateAllCombatStateSubscribers(live)
                            .Select(model => model.GetType().FullName).ToArray(),
                        cards = prediction.State.GetPlayerCombatState(player).AllCards.Select(card => new
                        {
                            type = card.Preview.GetType().FullName,
                            deckLinked = card.Preview.DeckVersion is not null,
                            enchantment = card.Preview.Enchantment?.GetType().FullName,
                            affliction = card.Preview.Affliction?.GetType().FullName,
                        }).ToArray(),
                        powers = combat.EffectivePowers().Select(power => power.GetType().FullName).ToArray(),
                        orbs = prediction.State.GetPlayerCombatState(player).OrbQueue.Orbs.Select(orb => orb.GetType().FullName).ToArray(),
                    }));
            foreach (CardModel native in nativeCards)
            {
                using (combat.BeginCardExecutionScope(new ForkableSet<uint>()))
                    prediction.ManualPlay(prediction.State.FindCard(native)!, null, out _);
                CombatBeamSolver.SettleReplayActionBoundary(prediction, combat);
                if (StrategicHpRecoveryBound.RemainingHealingUpperBound(prediction, player, 0) != 0
                    || StrategicHpRecoveryBound.RemainingHealingUpperBound(prediction.Fork(), player, 7) != 7)
                    throw new InvalidOperationException("The native Turbo/Spore/Compact closure lost its bound across Fork.");
            }
            if (!prediction.State.GetPlayerCombatState(player).DiscardPile.Cards.Any(card => card.Preview is MegaCrit.Sts2.Core.Models.Cards.Void)
                || prediction.State.GetPlayerCombatState(player).ExhaustPile.Cards.All(card => card.Preview is not SporeMind)
                || combat.RelicsOf(player).OfType<BookOfFiveRings>().Single().CardsAdded != bookBefore)
                throw new InvalidOperationException("Native fixed generation, curse exhaustion or Book of Five Rings behavior changed.");

            foreach (PileType pile in new[] { PileType.Hand, PileType.Exhaust })
            {
                CombatPredictionSimulator unknown = prediction.Fork();
                unknown.AddGeneratedCardToCombat(PredictedCard.Create(ModelDb.Card<NotYet>(), player),
                    pile, player, resultKind: CardGenerationResultKind.Fixed);
                if (StrategicHpRecoveryBound.RemainingHealingUpperBound(unknown, player, 0) != int.MaxValue)
                    throw new InvalidOperationException("An unaudited card, including in Exhaust, must disable the proof.");
            }
            CombatPredictionSimulator linkedStatus = prediction.Fork();
            CardModel status = ModelDb.Card<MegaCrit.Sts2.Core.Models.Cards.Void>().ToMutable();
            status.Owner = player;
            status.DeckVersion = ModelDb.Card<MegaCrit.Sts2.Core.Models.Cards.Void>().ToMutable();
            linkedStatus.AddGeneratedCardToCombat(PredictedCard.FromGenerated(status),
                PileType.Hand, player, resultKind: CardGenerationResultKind.Fixed);
            if (StrategicHpRecoveryBound.RemainingHealingUpperBound(linkedStatus, player, 0) != int.MaxValue)
                throw new InvalidOperationException("A deck-linked status must not prove that Compact cannot affect run Deck healing.");

            CombatPredictionSimulator regenerating = prediction.Fork();
            ((SimulatedCombatState)regenerating.State.CombatState).SetAmount<RegenPower>(player.Creature, 2);
            if (StrategicHpRecoveryBound.RemainingHealingUpperBound(regenerating, player, 0) != int.MaxValue)
                throw new InvalidOperationException("An active healing callback must disable the zero-healing proof.");
            CombatPredictionSimulator healingPotion = prediction.Fork();
            SimulatedCombatState potionCombat = (SimulatedCombatState)healingPotion.State.CombatState;
            potionCombat.ConsumePotion(player, 0);
            if (!potionCombat.TryProcurePotion(player, ModelDb.Potion<RegenPotion>(), false)
                || StrategicHpRecoveryBound.RemainingHealingUpperBound(healingPotion, player, 0) != int.MaxValue)
                throw new InvalidOperationException("An unaudited potion must disable the proof even in a disabled-potion member.");
            if (DescribeContinuationContractState(parent, root, player) != parentBefore
                || ContinuationStamp.CaptureLive(live).StateText != liveBefore
                || player.Relics.OfType<BookOfFiveRings>().Single().CardsAdded != bookBefore)
                throw new InvalidOperationException("Healing-bound inspection mutated its parent or the native root.");
        }

        string expected = ContinuationStamp.CapturePredicted(player, prediction, root.StartTurnNumber,
            root.Forecast, root.StartTurnNumber).StateText;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        foreach (CardModel native in nativeCards)
        {
            GameAction action = await SolverController.EnqueueAndCaptureActionAsync(
                queued => queued is PlayCardAction played
                    && ReferenceEquals(played.NetCombatCard.ToCardModelOrNull(), native),
                () =>
                {
                    if (!native.TryManualPlay(null))
                        throw new InvalidOperationException("The native player Prism card could not be played.");
                }, deadline.Token);
            await action.CompletionTask.WaitAsync(deadline.Token);
        }
        if (ContinuationStamp.CaptureLive(live).StateText != expected
            || DescribeContinuationContractState(parent, root, player) != parentBefore
            || player.Relics.OfType<BookOfFiveRings>().Single().CardsAdded != bookBefore)
            throw new InvalidOperationException("Native Turbo/Spore/Compact state differed or mutated the parent.");
        _completedChecks.Add("DefectPrismHealing:RestoredPlayerRoot:NativeTurboVoid:NativeSporeExhaust:NativeCompact:FullNativeState:BookCounter:Fork:UnknownCardsIncludingExhaust:DeckLinkedStatus:HealingPower:HealingPotion:ParentLiveIsolation");
    }
}
