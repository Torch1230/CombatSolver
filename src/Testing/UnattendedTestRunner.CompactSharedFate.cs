using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task PrepareCompactSharedFateAsync(CombatState combat, Player player, int mode)
    {
        await PrepareCompactOstyAsync(combat, player, 1, withTurnRelic: true);
        await ClearPlayerPilesAsync(player);
        foreach (var power in player.Creature.Powers.OfType<StrengthPower>().ToArray()) await PowerCmd.Remove(power);
        var enemy = combat.Enemies.Single();
        var choice = new BlockingPlayerChoiceContext();
        await PowerCmd.Apply<StrengthPower>(choice, player.Creature, mode == 2 ? -3 : 2, enemy, null);
        await PowerCmd.Apply<StrengthPower>(choice, enemy, mode == 2 ? -4 : 2, player.Creature, null);
        if (mode == 1)
        {
            await PowerCmd.Apply<ArtifactPower>(choice, player.Creature, 1, player.Creature, null);
            await PowerCmd.Apply<ArtifactPower>(choice, enemy, 1, enemy, null);
        }
        string[] ids = ["SHARED_FATE", "SHARED_FATE", "STRIKE_NECROBINDER", "UNLEASH", "SURVIVOR", "PREPARED"];
        for (int index = 0; index < ids.Length; index++)
            await InjectCardAsync(combat, player, new UnattendedCardInjection
            {
                CardId = ids[index], Pile = "Hand", UpgradeLevels = index is 1 or 5 ? 1 : 0,
                EnchantmentId = index == 1 ? "SWIFT" : null, EnchantmentAmount = index == 1 ? 3 : 0
            });
        for (int index = 0; index < 5; index++)
            await InjectCardAsync(combat, player, new UnattendedCardInjection
            { CardId = "DEFEND_NECROBINDER", Pile = index < 2 ? "Draw" : "Discard" });
        await CreatureCmd.SetMaxHp(enemy, 10_000);
        await CreatureCmd.SetCurrentHp(enemy, 10_000);
        SetEnergy(player, 20);
    }

    private async Task AssertCompactSharedFateAsync(CombatState combat, Player player)
    {
        for (int mode = 0; mode < 3; mode++)
        {
            await PrepareCompactSharedFateAsync(combat, player, mode);
            int nativePending = 0;
            await AssertCompactPetCardRouteAsync(combat, player, mode, "CompactSharedFate", "compact-shared-fate",
                [("SHARED_FATE", 0), ("SHARED_FATE", 1), ("STRIKE_NECROBINDER", 0), ("UNLEASH", 0), ("SURVIVOR", 0)],
                (lane, before, step) =>
                {
                    if (step > 1) return;
                    int Amount(int owner, BasicPowerKind kind) => Enumerable.Range(0, lane.PowerCount)
                        .Where(index => lane.PowerDefinition(index).Owner == owner && lane.PowerDefinition(index).Kind == kind)
                        .Select(index => lane.Power(index).Amount).DefaultIfEmpty().Single();
                    var expected = SharedFateStrengths(mode, step);
                    var applications = Enumerable.Range(before.EventCount, lane.EventCount - before.EventCount).Select(lane.EventAt)
                        .Where(item => item.Kind == ResumableDiscardProgram.EventKind.PowerChange
                            && item.Flags is (int)BasicPowerKind.Strength or (int)BasicPowerKind.Artifact).Select(item => item.Target);
                    if (!applications.SequenceEqual([0, 1])) throw new InvalidOperationException("Shared Fate must apply to its owner before its chosen enemy.");
                    if (Amount(0, BasicPowerKind.Strength) != expected.Player || Amount(1, BasicPowerKind.Strength) != expected.Enemy
                        || Amount(lane.PetIndex, BasicPowerKind.Strength) != 2
                        || Amount(0, BasicPowerKind.Artifact) != 0 || Amount(1, BasicPowerKind.Artifact) != 0)
                        throw new InvalidOperationException("Shared Fate lost a separate owner/target application or changed pet Strength.");
                }, chooseBranch: (action, step) => step != 4 || action.GetActionChoicesInExecutionOrder()
                    .SelectMany(choice => choice.Cards).Any(token => token.CardId == "PREPARED"), observeNativeChoice: (action, _) =>
                {
                    if (action.CardId != "SHARED_FATE") return false;
                    var expected = SharedFateStrengths(mode, 1);
                    if (player.Creature.GetPowerAmount<StrengthPower>() != expected.Player
                        || combat.Enemies.Single().GetPowerAmount<StrengthPower>() != expected.Enemy)
                        throw new InvalidOperationException("Swift's native selector must observe both completed Strength applications.");
                    nativePending++;
                    return true;
                });
            if (nativePending == 0) throw new InvalidOperationException("Shared Fate did not reach its native post-effect selector.");
            _completedChecks.Add($"CompactSharedFate:Mode{mode}:BothStrengthApplications:ArtifactBothOwners:RootRetirementAndNegativeReacquisition:NativeSwiftChoice:PlayerAndPetAttacks:TwoRounds");
        }
    }

    private static (int Player, int Enemy) SharedFateStrengths(int mode, int step) => (mode, step) switch
    {
        (0, 0) => (0, 0), (0, 1) => (-2, -3), (1, 0) => (2, 2), (1, 1) => (0, -1),
        (2, 0) => (-5, -6), (2, 1) => (-7, -9),
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
}
