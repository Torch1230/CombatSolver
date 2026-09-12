using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task PrepareCompactCostPowersAsync(CombatState combat, Player player, int mode)
    {
        await PrepareCompactOstyAsync(combat, player, mode, withTurnRelic: true);
        await ClearPlayerPilesAsync(player);
        foreach (string id in new[] { "BORROWED_TIME", "VEILPIERCER", "DEFILE" })
            for (int upgrade = 0; upgrade < 2; upgrade++)
                await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand", UpgradeLevels = upgrade });
        foreach (string id in new[] { "DEFY", "MALAISE", "SOUL", "ASCENDERS_BANE" })
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand" });
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFILE", Pile = "Draw", EnchantmentId = "SLITHER" });
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFY", Pile = "Draw", UpgradeLevels = 1 });
        foreach (string id in new[] { "DEFILE", "DEFEND_NECROBINDER" })
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Discard" });
        if (mode == 1)
        {
            await PowerCmd.Apply<BorrowedTimePower>(new BlockingPlayerChoiceContext(), player.Creature, 2, player.Creature, null);
            await PowerCmd.Apply<VeilpiercerPower>(new BlockingPlayerChoiceContext(), player.Creature, 2, player.Creature, null);
            await PowerCmd.Apply<ArtifactPower>(new BlockingPlayerChoiceContext(), player.Creature, 1, player.Creature, null);
            foreach (var power in player.Creature.Powers) power.AmountOnTurnStart = 37;
        }
        SetEnergy(player, 20);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
    }

    private async Task AssertCompactCostPowersAsync(CombatState combat, Player player)
    {
        for (int mode = _request.ScenarioId == "COMPACT-COST-POWERS-ROOT-STACKS" ? 1 : 0; mode < 2; mode++)
        {
            await PrepareCompactCostPowersAsync(combat, player, mode);
            int nativeFreeChoices = 0;
            await AssertCompactPetCardRouteAsync(combat, player, mode, "CompactCostPowers", "compact-cost-powers",
                [("BORROWED_TIME", 0), ("VEILPIERCER", 0), ("DEFILE", 1), ("BORROWED_TIME", 1),
                    ("VEILPIERCER", 1), ("DEFY", 0), ("SOUL", 0), ("DEFILE", 0), ("MALAISE", 0)],
                (lane, before, step) =>
                {
                    var events = Enumerable.Range(before.EventCount, lane.EventCount - before.EventCount).Select(lane.EventAt).ToArray();
                    if (step is 0 or 3)
                    {
                        int gain = step == 0 ? 4 : 6;
                        int paid = events.Single(item => item.Kind == ResumableDiscardProgram.EventKind.Pay).Value;
                        if (lane.Energy != before.Energy - paid + gain)
                            throw new InvalidOperationException("BorrowedTime failed to preserve payment before energy gain.");
                    }
                    if (step is 2 or 5 || mode == 1 && step == 7)
                    {
                        int consume = Array.FindIndex(events, item => item.Kind == ResumableDiscardProgram.EventKind.PowerChange
                            && item.Flags == (int)BasicPowerKind.Veilpiercer);
                        int start = Array.FindIndex(events, item => item.Kind == ResumableDiscardProgram.EventKind.Start);
                        if (lane.Energy != before.Energy || consume < 0 || consume >= start
                            || events[start].Value != 0 || events[consume].Value != -1)
                            throw new InvalidOperationException("Free ethereal payment or BeforeCardPlayed consumption order differs.");
                    }
                    if (step == 8 && (lane.Energy != 0 || events.Single(item => item.Kind == ResumableDiscardProgram.EventKind.Pay).Value != before.Energy))
                        throw new InvalidOperationException("X cost must spend the available energy independently of global modifiers.");
                    if (step >= 9 && Enumerable.Range(0, lane.PowerCount).Any(index => lane.PowerDefinition(index).Kind == BasicPowerKind.BorrowedTime
                        && lane.Power(index).Amount != 0))
                        throw new InvalidOperationException("BorrowedTime survived its owner's turn end.");
                }, verifyEnergyCosts: true, observeNativeChoice: (action, _) =>
                {
                    if (mode != 1 || action.Kind != PlanActionKind.EndTurn) return false;
                    var ethereal = player.PlayerCombatState!.Hand.Cards.Where(card => card.Keywords.Contains(CardKeyword.Ethereal)
                        && card.EnergyCost._base >= 0 && !card.EnergyCost.CostsX).ToArray();
                    if (ethereal.Length == 0) return false;
                    if (player.Creature.Powers.OfType<VeilpiercerPower>().SingleOrDefault()?.Amount != 1
                        || ethereal.Any(card => card.EnergyCost.GetWithModifiers(CostModifiers.All) != 0))
                        throw new InvalidOperationException("Native pending selector did not preserve the free ethereal cost.");
                    nativeFreeChoices++;
                    return true;
                });
            if (mode == 1 && nativeFreeChoices == 0)
                throw new InvalidOperationException("Native pending free-cost observation was not reached.");
            _completedChecks.Add($"CompactCostPowers:Mode{mode}:EarlyThenLateCosts:PaymentBeforeConsumption:AllPiles:NegativeAndXCosts:SlitherDraw:Artifact:TwoRounds");
        }
    }

    private async Task AssertCompactCostPowersTerminalAsync(CombatState combat, Player player)
    {
        await PrepareCompactCostPowersAsync(combat, player, 1);
        await ClearPlayerPilesAsync(player);
        foreach (string id in new[] { "VEILPIERCER", "DEFILE", "BORROWED_TIME" })
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand" });
        await CreatureCmd.SetCurrentHp(combat.Enemies.Single(), 1);
        await AssertCompactPetCardRouteAsync(combat, player, 2, "CompactCostPowersTerminal", "compact-cost-powers-terminal",
            [("VEILPIERCER", 0)], (lane, _, _) =>
            {
                if (!lane.Terminal || lane.Energy != 17 || lane.EnergyCost(1) != lane.LocalEnergyCost(1))
                    throw new InvalidOperationException("Terminal cost queries must skip global hooks after preserving payment.");
            }, rounds: 0, requirePending: false, verifyEnergyCosts: true);
        _completedChecks.Add("CompactCostPowersTerminal:PaidModifiedCost:LastHitRejectsNewPower:GlobalCostsStopAtEnding:NativeBeforeTeardown");
    }
}
