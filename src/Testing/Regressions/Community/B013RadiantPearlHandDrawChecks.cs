using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    // RadiantPearl.BeforeHandDraw generates its Luminesce cards into the hand on turn 1
    // before the opening draw. The turn-setup plan must model them, otherwise the plan
    // stamp is one hand card short and the setup state mismatches.
    private async Task AssertB013RadiantPearlHandDrawAsync(CombatState combat, Player player)
    {
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in combat.Creatures.SelectMany(creature => creature.Powers).ToArray())
            await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        await InjectRelicAsync(player, new UnattendedRelicInjection { RelicId = "RADIANT_PEARL" });

        var simulator = CombatRootSnapshot.Capture(combat).ForkSimulator();
        var shadow = (SimulatedCombatState)simulator.State.CombatState;
        shadow.PrepareRelicsBeforeHandDraw(simulator, player, new TurnStartChoiceCursor(null));

        var shadowHand = simulator.State.GetPlayerCombatState(player).Hand.Cards;
        if (shadowHand.Count != 1 || shadowHand[0].Preview.Id.Entry != "LUMINESCE")
        {
            throw new InvalidOperationException(
                $"RadiantPearl 影子手牌应为 [LUMINESCE]，实际 " +
                $"[{string.Join(',', shadowHand.Select(card => card.Preview.Id.Entry))}]。");
        }

        await Hook.BeforeHandDraw(combat, player, new BlockingPlayerChoiceContext());
        var liveHand = player.PlayerCombatState!.Hand.Cards;
        if (liveHand.Count != 1 || liveHand[0].Id.Entry != "LUMINESCE")
        {
            throw new InvalidOperationException(
                $"RadiantPearl 原生手牌应为 [LUMINESCE]，实际 " +
                $"[{string.Join(',', liveHand.Select(card => card.Id.Entry))}]。");
        }
        if (shadowHand[0].Preview.CurrentUpgradeLevel != liveHand[0].CurrentUpgradeLevel
            || shadowHand[0].Preview.Owner != player)
        {
            throw new InvalidOperationException("RadiantPearl 生成牌的升级级别或归属与原生不一致。");
        }
        _completedChecks.Add("RadiantPearlBeforeHandDraw:GeneratedLuminesce:HandTop:NativeMatched");
    }
}
