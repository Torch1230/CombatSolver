using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors;
using CombatSolver.Engine.InCombat.Mirrors.Hooks;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static void AssertAfterAttackSuspensionPreservesUnvisitedPowers(
        CombatState combat,
        Player player)
    {
        AssertAfterAttackPairedPowerOrder(combat, player, pairedPowersFirst: false);
        AssertAfterAttackPairedPowerOrder(combat, player, pairedPowersFirst: true);
    }

    private static void AssertAfterAttackPairedPowerOrder(
        CombatState combat,
        Player player,
        bool pairedPowersFirst)
    {
        using PendingEnemyDeathFixture fixture = CreatePendingEnemyDeathFixture(combat, player);
        PrepareBlockTriggeredPending(fixture, player);
        SkittishPower? suspendingPower = null;
        if (!pairedPowersFirst)
        {
            suspendingPower = fixture.Combat.AddPowerInstance<SkittishPower>(
                player.Creature, 1, player.Creature);
        }
        VigorPower vigor = fixture.Combat.AddPowerInstance<VigorPower>(
            player.Creature, 6, player.Creature);
        GigantificationPower gigantification = fixture.Combat.AddPowerInstance<GigantificationPower>(
            player.Creature, 1, player.Creature);
        if (pairedPowersFirst)
        {
            suspendingPower = fixture.Combat.AddPowerInstance<SkittishPower>(
                player.Creature, 1, player.Creature);
        }

        List<AbstractModel> listeners = fixture.Combat.IterateHookListeners().ToList();
        int pendingIndex = listeners.FindIndex(listener => ReferenceEquals(listener, suspendingPower));
        int vigorIndex = listeners.FindIndex(listener => ReferenceEquals(listener, vigor));
        int gigantificationIndex = listeners.FindIndex(listener => ReferenceEquals(listener, gigantification));
        if (pendingIndex < 0 || vigorIndex < 0 || gigantificationIndex < 0
            || (vigorIndex < pendingIndex) != pairedPowersFirst
            || (gigantificationIndex < pendingIndex) != pairedPowersFirst)
        {
            throw new InvalidOperationException("AfterAttack 挂起测试没有形成要求的监听器先后顺序。");
        }

        PredictedCard card = PredictedCard.Create(ModelDb.Card<StrikeIronclad>(), player);
        CardPlay play = CreatePendingTailCardPlay(card, player);
        AttackCommand command = new AttackCommand(1m)
            .FromCard(card.Preview, play)
            .Targeting(player.Creature);
        // This is a hook-order fixture, not a legal-action search: seed one already-resolved
        // damage result to make Skittish's block callback suspend deterministically. No real
        // creature HP is changed. The same owner lets us test both paired-listener orders.
        command.AddResultsInternal(
            [new DamageResult(player.Creature, ValueProp.Move) { UnblockedDamage = 1 }]);
        HookMirrors.BeforeAttack(fixture.Simulator, command);
        HookMirrors.AfterAttack(fixture.Simulator, command);
        AssertSuspended(fixture, completed: false, "AfterAttack paired-power cleanup");

        int expectedVigor = pairedPowersFirst ? 0 : 6;
        int expectedGigantification = pairedPowersFirst ? 0 : 1;
        if (vigor.Amount != expectedVigor
            || fixture.Simulator.StateStore.GetPowerAmount(gigantification).Amount != expectedGigantification)
        {
            throw new InvalidOperationException(
                "AfterAttack 挂起清理消费了尚未执行的能力，或重复消费了已执行能力：" +
                $"paired_first={pairedPowersFirst} vigor={vigor.Amount}/{expectedVigor} " +
                $"gigantification={fixture.Simulator.StateStore.GetPowerAmount(gigantification).Amount}/" +
                $"{expectedGigantification}。");
        }

        // Cleanup must be idempotent even when the containing action also unwinds.
        HookMirrors.AbortAttack(fixture.Simulator, command);
        if (vigor.Amount != expectedVigor
            || fixture.Simulator.StateStore.GetPowerAmount(gigantification).Amount != expectedGigantification)
        {
            throw new InvalidOperationException("AfterAttack 挂起后的二次 abort 再次消费了配对能力。");
        }
    }
}
