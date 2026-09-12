using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task PrepareCompactDirgeAsync(CombatState combat, Player player, int mode)
    {
        await PrepareCompactOstyAsync(combat, player, mode == 1 ? 2 : 0, withTurnRelic: true);
        await ClearPlayerPilesAsync(player);
        for (int upgrade = 0; upgrade < 2; upgrade++)
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DIRGE", Pile = "Hand", UpgradeLevels = upgrade });
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "FINESSE", Pile = "Hand" });
        if (mode == 0)
            for (int upgrade = 0; upgrade < 2; upgrade++)
                await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "SOUL", Pile = "Draw", UpgradeLevels = upgrade });
        else
            for (int index = 0; index < 7; index++)
                await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_NECROBINDER", Pile = "Hand" });
        SetEnergy(player, 3);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
    }

    private async Task AssertCompactDirgeAsync(CombatState combat, Player player)
    {
        for (int mode = 0; mode < 2; mode++)
        {
            await PrepareCompactDirgeAsync(combat, player, mode);
            await AssertCompactPetCardRouteAsync(combat, player, mode, "CompactDirge", "compact-dirge",
                [("DIRGE", mode), ("FINESSE", 0), ("SOUL", null), ("DIRGE", 1 - mode)], (lane, before, step) =>
                {
                    if (step is not (0 or 3)) return;
                    int x = step == 0 ? 3 : 0;
                    var events = Enumerable.Range(before.EventCount, lane.EventCount - before.EventCount).Select(lane.EventAt).ToArray();
                    var summons = events.Where(item => item.Kind == ResumableDiscardProgram.EventKind.SummonPet).ToArray();
                    var generated = events.Where(item => item.Kind == ResumableDiscardProgram.EventKind.Generated).ToArray();
                    if (summons.Length != x || generated.Length != x || summons.Any(item => item.Value != 3 + mode)
                        || lane.ShuffleRng.Counter != before.ShuffleRng.Counter + x || lane.ShuffleCount != before.ShuffleCount
                        || generated.Any(item => item.Flags != (int)ResumableDiscardProgram.Pile.Draw)
                        || lane.Energy != 0)
                        throw new InvalidOperationException("Dirge lost separate X summons, zero-X behavior or random draw insertion.");
                });
            _completedChecks.Add($"CompactDirge:Mode{mode}:X3AndX0:SeparateSummons:RandomDrawInsertion:GenerationBeforeDraw:SelfExhaust");
        }
    }
}
