using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertReportCardSequenceAsync(CombatState combat, Player player)
    {
        var enemy = combat.Enemies.Single();
        if (_request.ScenarioId == "REPORT-CARDS-SWORD-SAGE")
            await CardPileCmd.AddGeneratedCardToCombat(FindActualHandCard(player, "SOVEREIGN_BLADE", 0).CreateClone(), PileType.Hand, player);
        if (_request.ScenarioId == "REPORT-CARDS-PALE-BLUE-ROOT")
        {
            foreach (var setup in player.PlayerCombatState!.Hand.Cards.Take(5).ToArray())
            {
                if (!setup.TryManualPlay(null))
                    throw new InvalidOperationException("Pale Blue Dot root setup failed.");
                await MegaCrit.Sts2.Core.Runs.RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            }
        }
        var cards = player.PlayerCombatState!.Hand.Cards.ToArray();
        if (_request.ScenarioId == "REPORT-CARDS-SPOILS-FULL-HAND")
            cards = cards.Take(1).ToArray();
        var simulator = CombatRootSnapshot.Capture(combat).ForkSimulator();
        var shadow = (SimulatedCombatState)simulator.State.CombatState;
        int playedCount = 0;
        foreach (var card in cards)
        {
            await PlayHistorySensitiveFixtureCardAsync(simulator, shadow, combat, player, enemy, card, "ReportCard");
            if (_request.ScenarioId == "REPORT-CARDS-ORBIT-ENERGY-GATE" && ++playedCount == 4)
            {
                await PowerCmd.Remove(player.Creature.GetPower<NoEnergyGainPower>()
                    ?? throw new InvalidOperationException("Orbit fixture requires energy suppression."));
                shadow.SetAmount<NoEnergyGainPower>(player.Creature, 0);
                AssertSnapshotEqual(CaptureSimulated(simulator, shadow, player, enemy),
                    CaptureActual(combat, player, enemy), _request.ScenarioId, "RemoveEnergySuppression");
            }
            var fork = simulator.Fork();
            AssertSnapshotEqual(CaptureSimulated(simulator, shadow, player, enemy),
                CaptureSimulated(fork, (SimulatedCombatState)fork.State.CombatState, player, enemy),
                _request.ScenarioId, "Fork");
        }
    }
}
