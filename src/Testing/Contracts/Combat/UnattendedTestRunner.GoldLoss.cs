using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertGoldHealingCallbackAsync(CombatState combat, Player player)
    {
        if (!player.Relics.Any(relic => relic.Id.Entry == "DRAGON_FRUIT"))
            throw new InvalidOperationException("Gold healing differential requires held DragonFruit.");
        player.Gold = 137;
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        var parent = root.ForkSimulator();
        var child = parent.Fork();
        var shadow = (SimulatedCombatState)child.State.CombatState;
        var enemy = combat.Enemies.Single();
        string parentBefore = DescribeContinuationContractState(parent, root, player);
        string liveBefore = ContinuationStamp.CaptureLive(combat).StateText;
        int hpBefore = player.Creature.CurrentHp;
        int maxHpBefore = player.Creature.MaxHp;
        int knownPotential = StrategicHpRecoveryBound.KnownNativeHealingPotential(parent, player, 0);
        int certifiedBound = StrategicHpRecoveryBound.RemainingHealingUpperBound(parent, player, 0);
        shadow.GainPlayerGold(child, player, 20);
        if (DescribeContinuationContractState(parent, root, player) != parentBefore
            || ContinuationStamp.CaptureLive(combat).StateText != liveBefore)
            throw new InvalidOperationException("Gold prediction changed parent or live before native execution.");
        var predicted = CaptureSimulated(child, shadow, player, enemy);
        await PlayerCmd.GainGold(20, player);
        var actual = CaptureActual(combat, player, enemy);
        _writer.WriteGeneratedArtifact("gold-healing-difference.json", new
        {
            source = "PlayerCmd.GainGold -> Hook.AfterGoldGained -> DragonFruit.GainMaxHp",
            amount = 20, hpBefore, maxHpBefore, knownPotential, certifiedBound,
            root.CanCertifyRemainingHealing, root.UsesKnownNativeHealingPolicy,
            predicted, actual,
            nativeHpIncrease = player.Creature.CurrentHp - hpBefore,
            nativeMaxHpIncrease = player.Creature.MaxHp - maxHpBefore,
            parentIsolation = DescribeContinuationContractState(parent, root, player) == parentBefore,
        });
        AssertSnapshotEqual(predicted, actual, _request.ScenarioId, "GainGold:DragonFruit:20");
        _completedChecks.Add("GoldHealing:DragonFruit:NativeGainGold:FullState:ForkParentLiveIsolation");
    }

    private async Task AssertSignedGoldLossAsync(CombatState combat, Player player)
    {
        player.Gold = 137;
        var simulator = CombatRootSnapshot.Capture(combat).ForkSimulator();
        var shadow = (SimulatedCombatState)simulator.State.CombatState;
        var enemy = combat.Enemies.Single();
        foreach (int amount in new[] { -5, 0, 3, 200, -5 })
        {
            shadow.LosePlayerGold(player, amount);
            await PlayerCmd.LoseGold(amount, player);
            AssertSnapshotEqual(CaptureSimulated(simulator, shadow, player, enemy),
                CaptureActual(combat, player, enemy), _request.ScenarioId, $"LoseGold:{amount}");
        }
        _completedChecks.Add("SignedGoldLoss:NegativeZeroPositiveAndFloor:FullStateAndRng");
    }
}
