using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertBlockEventHistoryAsync(CombatState combat, Player player)
    {
        foreach (RelicModel relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (PowerModel power in combat.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        await SetBlockAsync(player.Creature, 0);
        SetEnergy(player, 3);
        foreach (string id in new[] { "STRIKE_IRONCLAD", "DEFEND_IRONCLAD" })
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand" });
        foreach (string id in new[] { "UNMOVABLE_POWER", "AFTERIMAGE_POWER" })
            await InjectPowerAsync(combat, player, new UnattendedPowerInjection { PowerId = id, Target = "Player", Amount = 1 });
        var simulator = CombatRootSnapshot.Capture(combat).ForkSimulator();
        var shadow = (SimulatedCombatState)simulator.State.CombatState;
        var enemy = combat.Enemies.Single();
        foreach (var card in player.PlayerCombatState!.Hand.Cards.ToArray())
        {
            var target = card.Type == MegaCrit.Sts2.Core.Entities.Cards.CardType.Attack ? enemy : null;
            PlaySimulatedCard(simulator, shadow, simulator.State.FindCard(card)!, target, [enemy]);
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
            GameAction action = await SolverController.EnqueueAndCaptureActionAsync(
                queued => queued is PlayCardAction played && ReferenceEquals(played.NetCombatCard.ToCardModelOrNull(), card),
                () => { if (!card.TryManualPlay(target)) throw new InvalidOperationException("Block history card was refused."); }, deadline.Token);
            await action.CompletionTask.WaitAsync(deadline.Token);
            AssertSnapshotEqual(CaptureSimulated(simulator, shadow, player, enemy), CaptureActual(combat, player, enemy),
                _request.ScenarioId, card.Id.Entry);
        }
        shadow.Apply<UnmovablePower>(player.Creature, 2, player.Creature);
        await PowerCmd.Apply<UnmovablePower>(new BlockingPlayerChoiceContext(), player.Creature, 2, player.Creature, null);
        CardModel source = player.PlayerCombatState.DiscardPile.Cards.Single(c => c.Id.Entry == "DEFEND_IRONCLAD");
        CardPlay Play() => new() { Card = source, Player = player, Target = null, ResultPile = PileType.Discard,
            Resources = new ResourceInfo { EnergySpent = 0, EnergyValue = 0, StarsSpent = 0, StarValue = 0 },
            IsAutoPlay = false, PlayIndex = 0, PlayCount = 1 };
        CardPlay samePlay = Play();
        foreach (CardPlay play in new[] { samePlay, samePlay, Play() })
        {
            simulator.GainBlock(player.Creature, 5, ValueProp.Move, simulator.State.FindCard(source), play);
            await CreatureCmd.GainBlock(player.Creature, 5, ValueProp.Move, play);
            AssertSnapshotEqual(CaptureSimulated(simulator, shadow, player, enemy), CaptureActual(combat, player, enemy),
                _request.ScenarioId, "RepeatedBlockEvents");
        }
        _completedChecks.Add("Unmovable:UnpoweredExcluded:SamePlayExcluded:EachPoweredEventCounted:FullStateAndRng");
    }
}
