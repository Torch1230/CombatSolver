using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Events;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertB015MadScienceAsync(CombatState live, Player player)
    {
        foreach (RelicModel relic in player.Relics.ToArray())
            await RelicCmd.Remove(relic);
        foreach (PowerModel power in live.Creatures.SelectMany(creature => creature.Powers).ToArray())
            await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        await SetBlockAsync(player.Creature, 0);
        SetEnergy(player, 3);
        await InjectCardAsync(live, player, new UnattendedCardInjection
        {
            CardId = "MAD_SCIENCE", Pile = "Hand", TreatAsDeckCard = true,
        });
        MadScience native = (MadScience)player.PlayerCombatState!.Hand.Cards.Single();
        native.TinkerTimeType = CardType.Skill;
        native.TinkerTimeRider = TinkerTime.RiderEffect.Chaos;
        CardCmd.Upgrade(native);

        static void RequireLegal(CardModel candidate, string stage)
        {
            if (candidate is not MadScience science
                || science.TinkerTimeType != CardType.Skill
                || science.TinkerTimeRider != TinkerTime.RiderEffect.Chaos
                || science.CurrentUpgradeLevel != 1)
                throw new InvalidOperationException("B015 Mad Science lost Skill/Chaos/+1 at " + stage + ".");
        }
        RequireLegal(native, "native upgrade");
        // Exercise the native saved-property restoration before the solver owns any state.
        MadScience restored = (MadScience)CardModel.FromSerializable(native.ToSerializable());
        RequireLegal(restored, "native saved-property roundtrip");

        CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
        CombatPredictionSimulator parent = root.ForkSimulator();
        PredictedCard Card(CombatPredictionSimulator simulator) => simulator.State.FindCard(native)
            ?? throw new InvalidOperationException("B015 Mad Science disappeared from the predicted piles.");
        RequireLegal(Card(parent).Preview, "root fork");
        string parentBefore = DescribeContinuationContractState(parent, root, player);
        string liveBefore = ContinuationStamp.CaptureLive(live).StateText;

        CombatPredictionSimulator sibling = parent.Fork();
        using (SimulationNotificationIsolation.Enter())
        {
            MadScience siblingCard = (MadScience)Card(sibling).MutablePreview;
            RequireLegal(siblingCard, "sibling materialization");
            siblingCard.TinkerTimeType = CardType.Attack;
            siblingCard.TinkerTimeRider = TinkerTime.RiderEffect.Violence;
        }
        RequireLegal(Card(parent).Preview, "parent after sibling mutation");
        RequireLegal(native, "live after sibling mutation");

        // A pre-existing invalid type must fail explicitly. Never turn None into Attack.
        CombatPredictionSimulator invalid = parent.Fork();
        bool rejectedNone = false;
        using (SimulationNotificationIsolation.Enter())
        {
            ((MadScience)Card(invalid).MutablePreview).TinkerTimeType = CardType.None;
            try
            {
                PlaySimulatedCard(invalid, (SimulatedCombatState)invalid.State.CombatState,
                    Card(invalid), null, live.Enemies);
            }
            catch (ArgumentOutOfRangeException error) when (
                error.ParamName == nameof(MadScience.TinkerTimeType)
                && error.ActualValue is CardType.None)
            {
                rejectedNone = true;
            }
        }
        if (!rejectedNone)
            throw new InvalidOperationException("B015 Mad Science accepted an invalid None type.");

        CombatPredictionSimulator predicted = parent.Fork();
        SimulatedCombatState shadow = (SimulatedCombatState)predicted.State.CombatState;
        using (SimulationNotificationIsolation.Enter())
        {
            RequireLegal(Card(predicted).MutablePreview, "action materialization");
            if (!predicted.CanPlay(Card(predicted)))
                throw new InvalidOperationException("B015 legal Mad Science was not playable in prediction.");
            PlaySimulatedCard(predicted, shadow, Card(predicted), null, live.Enemies);
        }
        if (predicted.HasPendingChoice)
            throw new InvalidOperationException("B015 Skill/Chaos unexpectedly suspended for a choice.");
        RequireLegal(Card(predicted).Preview, "completed predicted play");
        var expected = CaptureSimulated(predicted, shadow, player, live.Enemies.Single());
        if (expected.PlayerBlock != 8 || expected.PlayerEnergy != 2
            || predicted.State.GetPlayerCombatState(player).Hand.Cards.Count != 1)
            throw new InvalidOperationException("B015 Skill/Chaos did not gain 8 block and generate one card for one energy.");
        CombatPredictionSimulator completedFork = predicted.Fork();
        RequireLegal(Card(completedFork).Preview, "completed action fork");
        if (DescribeContinuationContractState(predicted, root, player)
            != DescribeContinuationContractState(completedFork, root, player))
            throw new InvalidOperationException("B015 completed Mad Science fork changed state/history/RNG.");
        if (DescribeContinuationContractState(parent, root, player) != parentBefore
            || ContinuationStamp.CaptureLive(live).StateText != liveBefore)
            throw new InvalidOperationException("B015 Mad Science branch work changed its parent or live combat.");

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(12));
        GameAction action = await SolverController.EnqueueAndCaptureActionAsync(
            queued => queued is PlayCardAction played
                && ReferenceEquals(played.NetCombatCard.ToCardModelOrNull(), native),
            () =>
            {
                if (!native.TryManualPlay(null))
                    throw new InvalidOperationException("B015 native Skill/Chaos could not be played.");
            }, deadline.Token);
        await action.CompletionTask.WaitAsync(deadline.Token);
        RequireLegal(native, "completed native play");
        AssertSnapshotEqual(expected, CaptureActual(live, player, live.Enemies.Single()),
            "B015MadScience", "SkillChaosUpgradeRootForkNativePlay");
        if (player.PlayerCombatState.Hand.Cards.Count != 1)
            throw new InvalidOperationException("B015 native Chaos did not leave one generated hand card.");
        _completedChecks.Add("B015MadScience:SkillChaos:NativeUpgrade:SavedProperties:Root:SiblingIsolation:CompletedFork:NoneRejected:NativeFullStateRng");
    }
}
