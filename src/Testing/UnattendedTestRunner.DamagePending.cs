using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static void AssertDamageReceivedChoiceSuspendsPostDamagePipeline(
        CombatState combat,
        Player player)
    {
        SimulatedCombatState simulatedCombat = new(combat);
        CombatPredictionSimulator simulator = new(simulatedCombat);
        CentennialPuzzle centennial = (CentennialPuzzle)PredictionUtils.CreateRelic(
            CanonicalModels.Relic<CentennialPuzzle>(),
            player);
        BeatingRemnant laterRelic = (BeatingRemnant)PredictionUtils.CreateRelic(
            CanonicalModels.Relic<BeatingRemnant>(),
            player);
        GremlinHorn untriggeredHorn = (GremlinHorn)PredictionUtils.CreateRelic(
            CanonicalModels.Relic<GremlinHorn>(),
            player);
        ReplaceRootRelicsForTurnBoundaryTest(
            simulatedCombat,
            player,
            centennial,
            laterRelic,
            untriggeredHorn);

        HellraiserPower hellraiser = PrepareDamagePendingChoiceFixture(
            simulator,
            simulatedCombat,
            player);
        SimCreatureState playerCreature = simulator.State.GetCreature(player.Creature);
        playerCreature.SetMaxHp(Math.Max(50, playerCreature.MaxHp));
        playerCreature.CurrentHp = playerCreature.MaxHp;
        int hpBefore = playerCreature.CurrentHp;
        BeatingRemnantPredictionState laterState = simulator.StateStore.Get(
            laterRelic,
            () => new BeatingRemnantPredictionState(laterRelic));
        decimal laterDamageBefore = laterState.DamageReceivedThisTurn;
        Creature dealer = simulatedCombat.Enemies.First();
        Creature victim = MonsterSpawnSupport.Spawn<GasBomb>(
            simulator,
            simulatedCombat,
            dealer,
            slot: null,
            minion: true);
        SimCreatureState victimState = simulator.State.GetCreature(victim);
        victimState.SetMaxHp(1);
        victimState.CurrentHp = 1;
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(player);
        int energyBefore = playerState.Energy;

        simulatedCombat.BeginActionChoices((IReadOnlyList<PlanCardChoice>?)null);
        try
        {
            simulator.Damage(
                [player.Creature, victim],
                1m,
                ValueProp.Unblockable | ValueProp.Unpowered,
                dealer);

            AssertPendingChoice(
                simulatedCombat,
                hellraiser.Id.Entry,
                PlanChoiceEffect.MoveToHand,
                "受伤后遗物抽牌");
            if (playerCreature.CurrentHp != hpBefore - 1)
                throw new InvalidOperationException("受伤后选择挂起前的生命损失没有恰好结算一次。");
            CentennialPuzzlePredictionState centennialState = simulator.StateStore.Get(
                centennial,
                () => new CentennialPuzzlePredictionState(centennial));
            if (!centennialState.UsedThisCombat)
                throw new InvalidOperationException("百年拼图触发选择后没有保留已触发状态。");
            if (laterState.DamageReceivedThisTurn != laterDamageBefore)
                throw new InvalidOperationException("受伤后选择挂起后仍执行了后续伤害 listener。");
            if (victimState.CurrentHp != 0 || !simulatedCombat.Enemies.Contains(victim))
            {
                throw new InvalidOperationException(
                    "批量伤害挂起没有保留已完成的 target body，或错误推进了后续 Kill 移除。");
            }
            if (playerState.Energy != energyBefore)
                throw new InvalidOperationException("批量伤害首个结果挂起后仍处理了后续死亡结果。");
            if (simulator.History.OfType<CombatPredictionCardDrawnEntry>().Count() != 1
                || simulator.History.OfType<CombatPredictionCardDrawResolvedEntry>().Any())
            {
                throw new InvalidOperationException("抽牌 listener 挂起后错误提交了 CardDrawResolved。");
            }
        }
        finally
        {
            simulatedCombat.EndActionChoices();
        }
    }

    private static void AssertDeathChoiceSuspendsKillPipeline(
        CombatState combat,
        Player player)
    {
        SimulatedCombatState simulatedCombat = new(combat);
        CombatPredictionSimulator simulator = new(simulatedCombat);
        GremlinHorn firstHorn = (GremlinHorn)PredictionUtils.CreateRelic(
            CanonicalModels.Relic<GremlinHorn>(),
            player);
        GremlinHorn laterHorn = (GremlinHorn)PredictionUtils.CreateRelic(
            CanonicalModels.Relic<GremlinHorn>(),
            player);
        ReplaceRootRelicsForTurnBoundaryTest(simulatedCombat, player, firstHorn, laterHorn);

        HellraiserPower hellraiser = PrepareDamagePendingChoiceFixture(
            simulator,
            simulatedCombat,
            player);
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(player);
        playerState.LoseEnergy(playerState.Energy);
        int energyBefore = playerState.Energy;
        Creature source = simulatedCombat.Enemies.First();
        Creature victim = MonsterSpawnSupport.Spawn<GasBomb>(
            simulator,
            simulatedCombat,
            source,
            slot: null,
            minion: true);
        SimCreatureState victimState = simulator.State.GetCreature(victim);
        victimState.SetMaxHp(1);
        victimState.CurrentHp = 1;

        simulatedCombat.BeginActionChoices((IReadOnlyList<PlanCardChoice>?)null);
        try
        {
            bool completed = simulator.Kill(victim, force: true);

            AssertPendingChoice(
                simulatedCombat,
                hellraiser.Id.Entry,
                PlanChoiceEffect.MoveToHand,
                "死亡后遗物抽牌");
            if (completed)
                throw new InvalidOperationException("死亡后选择挂起时 Kill 错误报告为已完成。");
            int expectedEnergy = energyBefore + (int)firstHorn.DynamicVars.Energy.BaseValue;
            if (playerState.Energy != expectedEnergy)
            {
                throw new InvalidOperationException(
                    $"死亡后选择挂起仍执行了后续号角：actual={playerState.Energy} expected={expectedEnergy}。");
            }
            if (!simulatedCombat.Enemies.Contains(victim) || victimState.CurrentHp != 0)
                throw new InvalidOperationException("死亡后选择挂起没有停在移除生物之前。");
            if (simulator.History.OfType<CombatPredictionCardDrawResolvedEntry>().Any())
                throw new InvalidOperationException("死亡后抽牌选择挂起仍提交了 CardDrawResolved。");
        }
        finally
        {
            simulatedCombat.EndActionChoices();
        }
    }

    private static HellraiserPower PrepareDamagePendingChoiceFixture(
        CombatPredictionSimulator simulator,
        SimulatedCombatState simulatedCombat,
        Player player)
    {
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(player);
        simulator.RemoveFromCombat(playerState.AllCards.ToArray());
        foreach (PowerModel power in simulatedCombat.EffectivePowers().ToArray())
            simulatedCombat.SetPowerAmount(power, 0);
        StabilizeForkBoundaryEnemies(simulator);

        HellraiserPower hellraiser = simulatedCombat.AddPowerInstance<HellraiserPower>(
            player.Creature,
            1,
            player.Creature);
        PredictedCard seeker = PredictedCard.Create(ModelDb.Card<SeekerStrike>(), player);
        seeker.MutablePreview.DynamicVars.Cards.BaseValue = 1;
        simulator.AddGeneratedCardToCombat(
            seeker,
            PileType.Draw,
            player,
            resultKind: CardGenerationResultKind.Fixed);
        simulator.AddGeneratedCardToCombat(
            PredictedCard.Create(ModelDb.Card<DefendDefect>(), player),
            PileType.Draw,
            player,
            resultKind: CardGenerationResultKind.Fixed);
        return hellraiser;
    }
}
