using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task RunPotionDifferentialAsync(
        CombatState combatState,
        Player player,
        UnattendedPotionCheck check)
    {
        Creature enemy = ResolveEnemyByIndex(combatState, check.TargetIndex);
        if (check.ClearPlayerHandBeforeUse)
        {
            await CardCmd.Discard(
                new BlockingPlayerChoiceContext(),
                player.PlayerCombatState!.Hand.Cards.ToArray());
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
        foreach (UnattendedCardInjection injection in check.Cards)
            await InjectCardAsync(combatState, player, injection);
        if (check.PlayerHpBefore is { } playerHp)
            await CreatureCmd.SetCurrentHp(player.Creature, Math.Clamp(playerHp, 1, player.Creature.MaxHp));
        if (check.PlayerBlockBefore is { } playerBlock)
            await SetBlockAsync(player.Creature, playerBlock);
        if (check.PlayerEnergyBefore is { } energy)
            SetEnergy(player, energy);
        if (check.PlayerStarsBefore is { } stars)
            SetStars(player, stars);
        if (check.EnemyHpBefore is { } enemyHp)
            await CreatureCmd.SetCurrentHp(enemy, Math.Clamp(enemyHp, 1, enemy.MaxHp));
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();

        PotionModel potion;
        if (check.ProcureThroughGame)
        {
            PotionModel canonical = ResolveUnique(ModelDb.AllPotions, check.PotionId, "药水");
            var result = await PotionCmd.TryToProcure(canonical.ToMutable(), player);
            if (!result.success)
                throw new InvalidOperationException($"无法通过原生路径取得测试药水 {check.PotionId}。");
            potion = result.potion;
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
        else
        {
            potion = InjectPotionForTest(player, check.PotionId);
        }
        int slot = player.GetPotionSlotIndex(potion);
        if (slot < 0)
            throw new InvalidOperationException($"注入药水 {check.PotionId} 后找不到槽位。");

        Creature? target = check.Target switch
        {
            "Player" => null,
            "Enemy" => enemy,
            "None" => null,
            _ => throw new InvalidOperationException($"不支持的药水测试目标 {check.Target}。"),
        };
        Creature? validationTarget = target ?? (potion.IsValidTarget(player.Creature) ? player.Creature : null);
        if (!potion.IsValidTarget(validationTarget))
            throw new InvalidOperationException($"药水 {check.PotionId} 不接受测试目标 {check.Target}。");
        bool automaticDeath = check.TriggerAutomaticDeath && potion is MegaCrit.Sts2.Core.Models.Potions.FairyInABottle;
        if (!automaticDeath && !PotionOnUseSupport.CanSearch(potion))
            throw new InvalidOperationException($"药水 {check.PotionId} 尚未进入求解器确定性支持表。");

        MoveStateSnapshot before = CaptureActual(combatState, player, enemy);
        SimulatedCombatState simulatedCombat = new(combatState);
        CombatPredictionSimulator simulator = new(simulatedCombat);
        PlanCardChoice? choice = null;
        bool requiresChoice = PotionChoiceSupport.RequiresChoice(potion);
        if (requiresChoice && check.ChoiceCardIds.Length == 0)
            throw new InvalidOperationException($"药水 {potion.Id.Entry} 的差分测试没有指定选牌结果。");

        if (automaticDeath)
        {
            simulator.Damage(
                player.Creature,
                player.Creature.CurrentHp + 10,
                ValueProp.Unblockable | ValueProp.Unpowered,
                enemy);
        }
        else
        {
            TurnStartChoiceCursor? nestedChoiceCursor = check.NestedChoiceCardIds.Length == 0
                ? null
                : TurnStartChoiceCursor.ForAutomaticPolicy(request => request.Spec == null
                    ? null
                    : CardChoiceSupport.BuildRequestedChoice(request.Spec, check.NestedChoiceCardIds));
            if (nestedChoiceCursor != null)
                simulatedCombat.BeginActionChoices(nestedChoiceCursor);
            try
            {
                simulatedCombat.ConsumePotion(player, slot);
                simulatedCombat.BeforePotionUsed(simulator, potion, target);
                if (!PotionOnUseSupport.Use(simulator, simulatedCombat, potion, target))
                    throw new InvalidOperationException($"药水 {potion.Id.Entry} 差分测试遇到未提供的挂起选择。");
                if (requiresChoice)
                {
                    choice = CardChoiceSupport.BuildRequestedChoice(
                        PotionChoiceSupport.GetSpec(simulator, potion),
                        check.ChoiceCardIds);
                }
                if (choice != null && !PotionChoiceSupport.Apply(simulator, potion, choice))
                    throw new InvalidOperationException($"药水 {potion.Id.Entry} 选牌差分测试遇到未提供的内层选择。");
                if (simulator.State.GetCreature(potion.Owner.Creature).IsAlive)
                    simulatedCombat.AfterPotionUsed(simulator, potion, target);
                CorePowerSupport.ApplyEnemyDeathPowers(
                    simulator,
                    simulatedCombat,
                    combatState.Enemies,
                    new HashSet<uint>());
            }
            finally
            {
                if (nestedChoiceCursor != null)
                    simulatedCombat.EndActionChoices();
            }
            if (check.TriggerPlayerSideTurnEndAfterUse)
            {
                if (potion is MegaCrit.Sts2.Core.Models.Potions.RegenPotion)
                {
                    if (!PlayerTurnEndLifecycle.RunPhaseOne(
                            simulator,
                            simulatedCombat,
                            player,
                            [player.Creature]))
                    {
                        throw new InvalidOperationException(
                            "药水回合结束测试遇到未提供的挂起选择。");
                    }
                }
                if (!CorePowerSupport.TriggerPlayerSideTurnEndEffects(
                        simulator,
                        simulatedCombat,
                        [player.Creature]))
                {
                    throw new InvalidOperationException("药水玩家回合结束测试遇到未提供的挂起选择。");
                }
            }
            if (check.TriggerEnemySideTurnEndAfterUse)
            {
                if (!CorePowerSupport.TriggerEnemySideTurnEndEffects(
                        simulator,
                        simulatedCombat,
                        combatState.Enemies))
                {
                    throw new InvalidOperationException("药水敌方回合结束测试遇到未提供的挂起选择。");
                }
            }
        }
        MoveStateSnapshot predicted = CaptureSimulated(simulator, simulatedCombat, player, enemy);
        if (check.ExpectedSurroundedFacing is { } expectedFacingName)
        {
            SurroundedPower.Direction expectedFacing = Enum.Parse<SurroundedPower.Direction>(
                expectedFacingName,
                ignoreCase: true);
            SurroundedPower simulatedPower = simulatedCombat.EffectivePowers()
                .OfType<SurroundedPower>()
                .Single(power => ReferenceEquals(power.Owner, player.Creature));
            SurroundedPredictionState simulatedFacing = simulator.StateStore
                .Get(simulatedPower, () => new SurroundedPredictionState(simulatedPower));
            if (simulatedFacing.Facing != expectedFacing)
            {
                throw new InvalidOperationException(
                    $"药水 {potion.Id.Entry} 后模拟包围朝向 {simulatedFacing.Facing}，预期 {expectedFacing}。");
            }
        }

        if (automaticDeath)
        {
            await CreatureCmd.Damage(
                new BlockingPlayerChoiceContext(),
                player.Creature,
                player.Creature.CurrentHp + 10,
                ValueProp.Unblockable | ValueProp.Unpowered,
                enemy,
                null,
                null);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
        else
        {
            using IDisposable? selector = choice != null
                ? CardSelectCmd.PushSelector(new PlannedCardSelector(choice))
                : check.NestedChoiceCardIds.Length > 0
                    ? CardSelectCmd.PushSelector(new UnattendedCardSelector(check.NestedChoiceCardIds))
                    : null;
            potion.EnqueueManualUse(target);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            if (check.TriggerPlayerSideTurnEndAfterUse)
                await TriggerActualSideTurnEndAsync(combatState, CombatSide.Player, [player.Creature]);
            if (check.TriggerEnemySideTurnEndAfterUse)
                await TriggerActualSideTurnEndAsync(combatState, CombatSide.Enemy, combatState.Enemies);
        }
        MoveStateSnapshot actual = CaptureActual(combatState, player, enemy);
        if (check.ExpectedSurroundedFacing is { } expectedActualFacingName)
        {
            SurroundedPower.Direction expectedFacing = Enum.Parse<SurroundedPower.Direction>(
                expectedActualFacingName,
                ignoreCase: true);
            SurroundedPower actualPower = player.Creature.GetPower<SurroundedPower>()
                ?? throw new InvalidOperationException("真实状态缺少包围 Power。");
            if (actualPower.Facing != expectedFacing)
            {
                throw new InvalidOperationException(
                    $"药水 {potion.Id.Entry} 后真实包围朝向 {actualPower.Facing}，预期 {expectedFacing}。");
            }
        }
        Entry.Logger.Info(
            $"[CombatSolver/Unattended] POTION_DIFF run_id={_request.RunId} potion={potion.Id.Entry} " +
            $"slot={slot} choice={string.Join(',', check.ChoiceCardIds)} automatic_death={automaticDeath} " +
            $"nested_choice={string.Join(',', check.NestedChoiceCardIds)} " +
            $"before={Serialize(before)} predicted={Serialize(predicted)} actual={Serialize(actual)}");

        AssertSnapshotEqual(predicted, actual, "Potion", potion.Id.Entry);
        if (ReferenceEquals(player.GetPotionAtSlotIndex(slot), potion))
            throw new InvalidOperationException($"药水 {potion.Id.Entry} 使用后原实例仍留在槽位 {slot}。");
        if (check.ExpectedPlayerHp is { } expectedPlayerHp && actual.PlayerHp != expectedPlayerHp)
            throw new InvalidOperationException($"药水 {potion.Id.Entry} 后玩家生命 {actual.PlayerHp}，预期 {expectedPlayerHp}。");
        if (check.ExpectedPlayerBlock is { } expectedPlayerBlock && actual.PlayerBlock != expectedPlayerBlock)
            throw new InvalidOperationException($"药水 {potion.Id.Entry} 后玩家格挡 {actual.PlayerBlock}，预期 {expectedPlayerBlock}。");
        if (check.ExpectedPlayerEnergy is { } expectedEnergy && actual.PlayerEnergy != expectedEnergy)
            throw new InvalidOperationException($"药水 {potion.Id.Entry} 后玩家能量 {actual.PlayerEnergy}，预期 {expectedEnergy}。");
        if (check.ExpectedPlayerStars is { } expectedStars && actual.PlayerStars != expectedStars)
            throw new InvalidOperationException($"药水 {potion.Id.Entry} 后玩家星能 {actual.PlayerStars}，预期 {expectedStars}。");
        if (check.ExpectedPlayerOrbCapacity is { } expectedCapacity && actual.PlayerOrbCapacity != expectedCapacity)
            throw new InvalidOperationException($"药水 {potion.Id.Entry} 后球槽 {actual.PlayerOrbCapacity}，预期 {expectedCapacity}。");
        if (check.ExpectedEnemyHp is { } expectedEnemyHp && actual.EnemyHp != expectedEnemyHp)
            throw new InvalidOperationException($"药水 {potion.Id.Entry} 后敌方生命 {actual.EnemyHp}，预期 {expectedEnemyHp}。");
        AssertExpectedPowers(actual.PlayerPowers, check.ExpectedPlayerPowers, "玩家", "Potion", potion.Id.Entry);
        AssertExpectedPowers(actual.EnemyPowers, check.ExpectedEnemyPowers, "敌方", "Potion", potion.Id.Entry);
        AssertAbsentPowers(actual.PlayerPowers, check.ExpectedAbsentPlayerPowers, "玩家", "Potion", potion.Id.Entry);
        AssertAbsentPowers(actual.EnemyPowers, check.ExpectedAbsentEnemyPowers, "敌方", "Potion", potion.Id.Entry);
        foreach ((string key, int expected) in check.ExpectedPlayerCardUpgrades)
        {
            int actualCount = actual.PlayerCardUpgrades.GetValueOrDefault(key);
            if (actualCount != expected)
                throw new InvalidOperationException($"药水 {potion.Id.Entry} 后升级状态 {key}={actualCount}，预期 {expected}。");
        }
    }

    private static PotionModel InjectPotionForTest(Player player, string potionId)
    {
        PotionModel canonical = ResolveUnique(ModelDb.AllPotions, potionId, "药水");
        PotionModel potion = canonical.ToMutable();
        int slot = Enumerable.Range(0, player.PotionSlots.Count)
            .FirstOrDefault(index => player.GetPotionAtSlotIndex(index) == null, -1);
        if (slot < 0)
        {
            player.GetPotionAtSlotIndex(0)!.Discard();
            slot = 0;
        }
        if (!player.AddPotionInternal(potion, slot, silent: false).success)
            throw new InvalidOperationException($"无法把测试药水 {potionId} 注入槽位 {slot}。");
        return potion;
    }
}
