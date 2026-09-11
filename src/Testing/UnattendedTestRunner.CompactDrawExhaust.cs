using System.Text.Json;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task PrepareCompactDrawExhaustAsync(CombatState combat, Player player, int mode)
    {
        await PrepareCompactOstyAsync(combat, player, mode == 1 ? 2 : 0, withTurnRelic: true);
        await ClearPlayerPilesAsync(player);
        foreach (string id in new[] { "CLEANSE", "AFTERLIFE" })
        for (int upgrade = 0; upgrade < 2; upgrade++)
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand", UpgradeLevels = upgrade });
        string[] draw = mode == 0 ? ["DEFEND_NECROBINDER", "DEFEND_NECROBINDER", "BODYGUARD", "BURN"]
            : mode == 1 ? ["DEFEND_NECROBINDER"] : [];
        foreach (string id in draw)
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Draw" });
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
    }

    private async Task AssertCompactDrawExhaustAsync(CombatState combat, Player player)
    {
        for (int mode = 0; mode < 3; mode++) await AssertCompactDrawExhaustModeAsync(combat, player, mode);
    }

    private async Task AssertCompactDrawExhaustModeAsync(CombatState combat, Player player, int mode)
    {
        await PrepareCompactDrawExhaustAsync(combat, player, mode);
        await AssertCompactPetCardRouteAsync(combat, player, mode, "CompactDrawExhaust", "compact-draw-exhaust",
            [("CLEANSE", 0), ("CLEANSE", 1), ("AFTERLIFE", 0), ("AFTERLIFE", 1)], (lane, _, step) =>
            {
                if (step == 3 && lane.Count(ResumableDiscardProgram.Pile.Exhaust) != 2 + Math.Min(mode == 0 ? 4 : mode == 1 ? 1 : 0, 2))
                    throw new InvalidOperationException("Draw-exhaust fixture missed selected-card or self exhaustion.");
            }, observeDrawExhaust: true);
    }
}
