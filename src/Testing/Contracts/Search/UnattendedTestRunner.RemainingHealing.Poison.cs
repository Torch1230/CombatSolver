using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertRemainingHealingPoisonAsync(CombatState live, Player player)
    {
        await ClearPlayerPilesAsync(player);
        await CreatureCmd.SetCurrentHp(player.Creature, 35);
        await CreatureCmd.SetCurrentHp(live.Enemies.Single(), 171);
        string[] cards = ["BACKFLIP", "OUTBREAK", "FOOTWORK", "BLADE_OF_INK", "SNAKEBITE",
            "PIERCING_WAIL", "CALCULATED_GAMBLE", "CLOAK_AND_DAGGER", "MALAISE", "SUPPRESS",
            "BUBBLE_BUBBLE", "HAZE", "DEADLY_POISON", "DODGE_AND_ROLL", "MIRAGE",
            "TOOLS_OF_THE_TRADE", "ULTIMATE_DEFEND", "SHIV"];
        foreach (string id in cards)
            await InjectCardAsync(live, player, new()
            {
                CardId = id, UpgradeLevels = 1,
                Pile = id == "OUTBREAK" ? "Draw"
                    : id is "BACKFLIP" or "BLADE_OF_INK" or "PIERCING_WAIL"
                        or "DODGE_AND_ROLL" or "TOOLS_OF_THE_TRADE" ? "Hand" : "Exhaust",
                EnchantmentId = id == "OUTBREAK" ? "SLITHER" : null,
                EnchantmentAmount = 1,
            });
        SetEnergy(player, 20);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
        if (!root.CanCertifyRemainingHealing)
            throw new InvalidOperationException("The audited native poison deck must certify remaining healing.");
        CombatPredictionSimulator parent = root.ForkSimulator();
        CombatPredictionSimulator prediction = parent.Fork();
        var combat = (SimulatedCombatState)prediction.State.CombatState;
        string parentBefore = DescribeContinuationContractState(parent, root, player);
        int Bound(CombatPredictionSimulator simulator, int postCombat = 0)
            => StrategicHpRecoveryBound.RemainingHealingUpperBound(simulator, player, postCombat);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        foreach (string id in new[] { "BACKFLIP", "BLADE_OF_INK", "SHIV", "DODGE_AND_ROLL",
                     "PIERCING_WAIL", "TOOLS_OF_THE_TRADE", "OUTBREAK" })
        {
            CardModel nativeCard = player.PlayerCombatState!.Hand.Cards.First(card => card.Id.Entry == id);
            using (SimulationNotificationIsolation.Enter())
            {
                PredictedCard simulatedCard = prediction.State.GetPlayerCombatState(player).Hand.Cards
                    .First(card => card.Preview.Id.Entry == id);
                using (combat.BeginCardExecutionScope(new ForkableSet<uint>()))
                    prediction.ManualPlay(simulatedCard,
                        simulatedCard.Preview.TargetType == TargetType.AnyEnemy ? live.Enemies.Single() : null, out _);
                CombatBeamSolver.SettleReplayActionBoundary(prediction, combat);
                if (Bound(prediction) != 0 || Bound(prediction.Fork()) != 0)
                    throw new InvalidOperationException($"Native poison closure was lost after {id} or Fork.");
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
                        throw new InvalidOperationException($"Native poison card could not play: {id}.");
                }, deadline.Token);
            await action.CompletionTask.WaitAsync(deadline.Token);
            if (ContinuationStamp.CaptureLive(live).StateText != expected)
                throw new InvalidOperationException($"Native poison play disagrees with full predicted state: {id}.");
        }
        using (SimulationNotificationIsolation.Enter())
        {
            SimCreatureState creature = prediction.State.GetCreature(player.Creature);
            creature.DamageBlock(creature.Block, ValueProp.Move);
            if (!CorePowerSupport.TriggerAfterBlockCleared(prediction, combat, player.Creature))
                throw new InvalidOperationException("Native delayed block unexpectedly suspended.");
            if (!CorePowerSupport.TriggerEnemySideTurnEndEffects(prediction, combat, live.Enemies))
                throw new InvalidOperationException("Native temporary strength expiry unexpectedly suspended.");
            CombatBeamSolver.SettleReplayActionBoundary(prediction, combat);
        }
        await SetBlockAsync(player.Creature, 0);
        await Hook.AfterBlockCleared(live, player.Creature);
        await TriggerActualSideTurnEndAsync(live, CombatSide.Enemy, live.Enemies);
        ContinuationStamp actualExpiry = ContinuationStamp.CaptureLive(live);
        ContinuationStamp expectedExpiry = ContinuationStamp.CapturePredicted(
            player, prediction, root.StartTurnNumber, root.Forecast, root.StartTurnNumber);
        if (actualExpiry.StateText != expectedExpiry.StateText)
            throw new InvalidOperationException("Native delayed block/temporary strength expiry differs from prediction: "
                + string.Join("; ", expectedExpiry.DescribeDifferences(actualExpiry, 8)));
        using (SimulationNotificationIsolation.Enter())
        {
            CombatPredictionSimulator unknown = prediction.Fork();
            unknown.AddGeneratedCardToCombat(PredictedCard.Create(ModelDb.Card<NotYet>(), player),
                PileType.Exhaust, player, resultKind: CardGenerationResultKind.Fixed);
            if (Bound(unknown) != int.MaxValue
                || StrategicHpRecoveryBound.CanCertifyRemainingHealingEnvironment(unknown, player))
                throw new InvalidOperationException("Unknown exhausted recovery cards must remain conservative.");
            CombatPredictionSimulator regen = prediction.Fork();
            ((SimulatedCombatState)regen.State.CombatState).SetAmount<RegenPower>(player.Creature, 3);
            if (Bound(regen, 7) != 13 || Bound(prediction) != 0)
                throw new InvalidOperationException("The poison closure must preserve regeneration and post-combat healing.");
            if (DescribeContinuationContractState(parent, root, player) != parentBefore)
                throw new InvalidOperationException("Native poison transitions modified their parent.");
        }
        _completedChecks.Add("RemainingHealing:Poison:18NativeCards:SlitherDrawRng:InkyShiv:SevenFullNativePlays:DelayedBlock:TemporaryStrengthExpiry:Fork:UnknownExhaustRecovery:RegenUpperBound:ParentIsolation");
    }
}
