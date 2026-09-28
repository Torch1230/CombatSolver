using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private sealed partial class Executor
    {
        private async Task<ExecutionOutcome> ExecuteMultiplayerProbeAsync(ScenarioContext scenario)
        {
            MultiplayerProbeInput input = runner._multiplayerProbe!;
            CombatState combat = scenario.CombatState;
            Creature enemy = combat.Enemies.Single();
            if (enemy.CurrentHp <= input.PlayerCount * 6)
                throw new InvalidOperationException("Probe requires an enemy surviving all scripted attacks.");
            if (CardSelectCmd.Selector != null || CardSelectCmd.LocalSelector != null)
                throw new InvalidOperationException("Probe requires exclusive ownership of the test selector.");
            ICardSelector selector = input.IsVirtual
                ? new UnattendedCardSelector(["DEFEND_IRONCLAD"])
                : new MultiplayerProbeNetworkSelector(combat);
            using (CardSelectCmd.UseSelector(selector, localOnly: !input.IsVirtual))
            {
                foreach (Player player in combat.Players)
                {
                    await PlayAsync(player, "DEFEND_IRONCLAD", null, () => player.PlayerCombatState!.Energy == 2 && player.Creature.Block == 5);
                    await runner.MultiplayerProbeBarrierAsync($"defend-{player.NetId}", combat);
                    int hp = enemy.CurrentHp;
                    await PlayAsync(player, "STRIKE_IRONCLAD", enemy, () => player.PlayerCombatState!.Energy == 1 && enemy.CurrentHp == hp - 6);
                    await runner.MultiplayerProbeBarrierAsync($"strike-{player.NetId}", combat);
                    await PlayAsync(player, "SURVIVOR", null, () => player.PlayerCombatState!.Energy == 0
                        && player.Creature.Block == 13 && player.PlayerCombatState.Hand.Cards.Count == 1
                        && player.PlayerCombatState.DiscardPile.Cards.Count == 4);
                    await runner.MultiplayerProbeBarrierAsync($"choice-{player.NetId}", combat);
                }
            }
            foreach (Player player in input.IsVirtual ? new[] { scenario.Player } : combat.Players)
            {
                if (input.IsVirtual || LocalContext.IsMe(player))
                {
                    var end = new EndPlayerTurnAction(player, 1);
                    RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(end);
                    if (input.Mode != "client")
                        await end.CompletionTask;
                }
                if (!input.IsVirtual && player != combat.Players[^1])
                {
                    await runner.WaitForMultiplayerProbeAsync(() => CombatManager.Instance.IsPlayerReadyToEndTurn(player));
                    if (combat.Players.Any(member => member.PlayerCombatState!.TurnNumber != 1))
                        throw new InvalidOperationException("Native multiplayer advanced before all players ended.");
                    await runner.MultiplayerProbeBarrierAsync($"ready-{player.NetId}", combat);
                }
            }
            runner.SetStage("multiplayer_second_turn");
            await runner.WaitForMultiplayerProbeAsync(() => combat.Players.All(player =>
                player.PlayerCombatState is { Phase: PlayerTurnPhase.Play, TurnNumber: 2 }));
            await runner.MultiplayerProbeBarrierAsync("second-turn", combat);
            runner._completedChecks.Add($"MultiplayerNativeProbe:Mode={input.Mode}:Players={input.PlayerCount}:Seat={input.Seat}:ScriptedCards:NativeChoice:EnemyTurn:NextDraw");
            return new ExecutionOutcome(false, 2, true, true, true, false);

            async Task PlayAsync(Player actor, string cardId, Creature? target, Func<bool> observed)
            {
                runner.SetStage($"multiplayer_play_{actor.NetId}_{cardId}");
                if (input.IsVirtual || LocalContext.IsMe(actor))
                {
                    CardModel card = actor.PlayerCombatState!.Hand.Cards.First(candidate => candidate.Id.Entry == cardId);
                    var action = new PlayCardAction(card, target);
                    RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(action);
                    if (input.Mode != "client")
                        await action.CompletionTask;
                }
                await runner.WaitForMultiplayerProbeAsync(observed);
            }
        }

        private sealed class MultiplayerProbeNetworkSelector(CombatState combat) : ICardSelector
        {
            private readonly UnattendedCardSelector _selector = new(["DEFEND_IRONCLAD"]);

            public async Task<IEnumerable<CardModel>> GetSelectedCards(
                IEnumerable<CardModel> options, int minSelect, int maxSelect)
            {
                CardModel[] selected = (await _selector.GetSelectedCards(options, minSelect, maxSelect)).ToArray();
                Player player = selected.Single().Owner;
                if (!LocalContext.IsMe(player))
                    throw new InvalidOperationException("Probe network selector received a remote player's choice.");
                int slot = combat.Players.ToList().IndexOf(player);
                var synchronizer = RunManager.Instance.PlayerChoiceSynchronizer;
                if (slot < 0 || slot >= synchronizer.ChoiceIds.Count || synchronizer.ChoiceIds[slot] == 0)
                    throw new InvalidOperationException($"Probe choice ID was not reserved for player {player.NetId}.");
                synchronizer.SyncLocalChoice(player, synchronizer.ChoiceIds[slot] - 1,
                    PlayerChoiceResult.FromMutableCombatCards(selected));
                return selected;
            }

            public CardRewardSelection GetSelectedCardReward(
                IReadOnlyList<CardCreationResult> options,
                IReadOnlyList<CardRewardAlternative> alternatives)
                => throw new InvalidOperationException("Probe does not select card rewards.");
        }
    }
}
