using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.ValueProps;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertRemainingHealingLouseAsync(CombatState live, Player player)
    {
        if (live.Enemies.Single().Monster?.GetType() != typeof(LouseProgenitor))
            throw new InvalidOperationException("The native Louse closure fixture requires Louse Progenitor.");
        await ClearPlayerPilesAsync(player);
        await CreatureCmd.SetCurrentHp(player.Creature, 35);
        await CreatureCmd.SetMaxHp(live.Enemies.Single(), 500);
        await CreatureCmd.SetCurrentHp(live.Enemies.Single(), 500);
        foreach (string id in new[] { "CAULDRON", "BURNING_STICKS", "KUSARIGAMA" })
            await InjectRelicAsync(player, new() { RelicId = id, AddWithoutObtainedEffects = true });
        foreach (string id in new[] { "ARSENAL", "REFINE_BLADE", "SEEKING_EDGE", "ULTIMATE_DEFEND",
                     "COMET", "GUIDING_STAR", "GLIMMER", "SECRET_WEAPON" })
            await InjectCardAsync(live, player, new()
            {
                CardId = id, UpgradeLevels = 1,
                Pile = id is "GLIMMER" or "SECRET_WEAPON" ? "Draw" : "Hand",
            });
        SetEnergy(player, 20);
        SetStars(player, 20);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
        if (!root.CanCertifyRemainingHealing || root.InitialRemainingHealingUpperBound != 0)
            throw new InvalidOperationException("The audited native Louse deck must certify remaining healing.");
        CombatPredictionSimulator parent = root.ForkSimulator();
        CombatPredictionSimulator prediction = parent.Fork();
        var combat = (SimulatedCombatState)prediction.State.CombatState;
        string parentBefore = DescribeContinuationContractState(parent, root, player);
        int Bound(CombatPredictionSimulator simulator, int postCombat = 0)
            => StrategicHpRecoveryBound.RemainingHealingUpperBound(simulator, player, postCombat);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        foreach (string id in new[] { "ARSENAL", "REFINE_BLADE", "SEEKING_EDGE", "ULTIMATE_DEFEND",
                     "SOVEREIGN_BLADE", "COMET", "GUIDING_STAR" })
        {
            CardModel nativeCard = player.PlayerCombatState!.Hand.Cards.First(card => card.Id.Entry == id);
            using (SimulationNotificationIsolation.Enter())
            {
                PredictedCard simulatedCard = prediction.State.GetPlayerCombatState(player).Hand.Cards
                    .First(card => card.Preview.Id.Entry == id);
                using (combat.BeginCardExecutionScope(new ForkableSet<uint>()))
                    if (!prediction.ManualPlay(simulatedCard,
                            simulatedCard.Preview.TargetType == TargetType.AnyEnemy ? live.Enemies.Single() : null, out _))
                        throw new InvalidOperationException($"Native Louse card unexpectedly suspended: {id}.");
                CombatBeamSolver.SettleReplayActionBoundary(prediction, combat);
                if (Bound(prediction) != 0 || Bound(prediction.Fork()) != 0)
                    throw new InvalidOperationException($"Native Louse closure was lost after {id} or Fork.");
            }
            string expected = ContinuationStamp.CapturePredicted(player, prediction, root.StartTurnNumber,
                root.Forecast, root.StartTurnNumber).StateText;
            GameAction action = await SolverController.EnqueueAndCaptureActionAsync(
                queued => queued is PlayCardAction played
                    && ReferenceEquals(played.NetCombatCard.ToCardModelOrNull(), nativeCard),
                () =>
                {
                    if (!nativeCard.TryManualPlay(nativeCard.TargetType == TargetType.AnyEnemy
                            ? live.Enemies.Single() : null))
                        throw new InvalidOperationException($"Native Louse card could not play: {id}.");
                }, deadline.Token);
            await action.CompletionTask.WaitAsync(deadline.Token);
            if (ContinuationStamp.CaptureLive(live).StateText != expected)
                throw new InvalidOperationException($"Native Louse play disagrees with full predicted state: {id}.");
        }
        using (SimulationNotificationIsolation.Enter())
        {
            CombatPredictionSimulator unknown = prediction.Fork();
            unknown.AddGeneratedCardToCombat(PredictedCard.Create(ModelDb.Card<BundleOfJoy>(), player),
                PileType.Exhaust, player, resultKind: CardGenerationResultKind.Fixed);
            if (Bound(unknown) != int.MaxValue
                || StrategicHpRecoveryBound.CanCertifyRemainingHealingEnvironment(unknown, player))
                throw new InvalidOperationException("Burning Sticks must reject the exhausted Bundle of Joy recovery shortcut.");
            CombatPredictionSimulator regen = prediction.Fork();
            ((SimulatedCombatState)regen.State.CombatState).SetAmount<RegenPower>(player.Creature, 3);
            if (Bound(regen, 7) != 13 || Bound(prediction) != 0)
                throw new InvalidOperationException("The Louse closure must preserve regeneration and post-combat healing.");
            if (DescribeContinuationContractState(parent, root, player) != parentBefore)
                throw new InvalidOperationException("Native Louse transitions modified their parent.");
        }
        await PowerCmd.Apply<RegenPower>(new BlockingPlayerChoiceContext(), player.Creature, 3,
            player.Creature, null);
        if (CombatRootSnapshot.Capture(live).InitialRemainingHealingUpperBound != 6
            || root.InitialRemainingHealingUpperBound != 0)
            throw new InvalidOperationException("Root healing metadata must reject remaining regeneration and remain immutable.");
        await PowerCmd.Apply<EntropyPower>(new BlockingPlayerChoiceContext(), player.Creature, 1,
            player.Creature, null);
        if (CombatRootSnapshot.Capture(live).InitialRemainingHealingUpperBound != int.MaxValue)
            throw new InvalidOperationException("Unknown root powers must prevent the zero-healing certificate.");
        _completedChecks.Add("RemainingHealing:Louse:SevenNewNativeCards:Cauldron:BurningSticksCopy:Kusarigama:CurlUp:ArsenalForge:SeekingEdge:SevenFullNativePlays:Fork:ExhaustedBundleGuard:RegenUpperBound:ParentIsolation:RootRegenGuard:RootUnknownPowerGuard:ImmutableRootMetadata");
    }
}
