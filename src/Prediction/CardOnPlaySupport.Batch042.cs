using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static partial class CardOnPlaySupport
{
    private static void ApplyBatch042(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        PredictedCard playedCard,
        CardPlay cardPlay,
        Creature? target,
        ISet<uint> processedEnemyDeaths)
    {
        CardModel card = playedCard.Preview;
        Creature owner = card.Owner.Creature;
        switch (card)
        {
            case BouncingFlask:
                ApplyBouncingFlask(simulator, combat, card);
                break;
            case Brand:
                simulator.Damage(
                    [owner],
                    card.DynamicVars.HpLoss.BaseValue,
                    ValueProp.Unblockable | ValueProp.Unpowered | ValueProp.Move,
                    owner,
                    playedCard,
                    null);
                break;
            case CaptureSpirit when target != null:
                ApplyCaptureSpirit(
                    simulator,
                    combat,
                    playedCard,
                    target,
                    processedEnemyDeaths);
                break;
            case EchoingSlash:
                ApplyEchoingSlash(
                    simulator,
                    combat,
                    playedCard,
                    cardPlay,
                    processedEnemyDeaths);
                break;
            case EndOfDays:
                ApplyEndOfDays(
                    simulator,
                    combat,
                    card,
                    processedEnemyDeaths);
                break;
            case FranticEscape:
                combat.IncrementSandpitTargeting(owner);
                playedCard.MutablePreview.EnergyCost.AddThisCombat(1);
                break;
            case Inferno:
                combat.Apply<InfernoPower>(owner, card.DynamicVars["InfernoPower"].IntValue, owner);
                InfernoPower inferno = combat.GetPower<InfernoPower>(owner)
                    ?? throw new InvalidOperationException("炼狱状态施加后未找到对应 Power。");
                inferno.IncrementSelfDamage();
                break;
            case Omnislice when target != null:
                ApplyOmnislice(
                    simulator,
                    combat,
                    playedCard,
                    cardPlay,
                    target,
                    processedEnemyDeaths);
                break;
            case Outbreak:
                ApplyOutbreak(
                    simulator,
                    combat,
                    card,
                    processedEnemyDeaths);
                break;
            case PrimalForce:
            {
                SimPlayerCombatState player = simulator.State.GetPlayerCombatState(card.Owner);
                PredictedCard[] attacks = player.Hand.Cards
                    .Where(candidate => candidate.Preview.IsTransformable
                        && candidate.Preview.Type == CardType.Attack)
                    .ToArray();
                CardChoiceSupport.TransformCards(
                    simulator,
                    attacks,
                    CanonicalModels.Card<GiantRock>(),
                    card.IsUpgraded);
                break;
            }
            case Tracking:
                combat.Apply<TrackingPower>(owner, 50, owner);
                break;
        }
    }

    private static void ApplyBouncingFlask(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        CardModel card)
    {
        for (int repeat = 0; repeat < card.DynamicVars.Repeat.IntValue; repeat++)
        {
            List<Creature> candidates = AliveHittableEnemies(simulator, combat);
            if (candidates.Count == 0)
                break;
            Creature enemy = simulator.Rng.CombatTargets.NextItem(candidates)
                ?? throw new InvalidOperationException("弹跳药瓶随机目标列表非空但没有返回目标。");
            combat.Apply<PoisonPower>(enemy, card.DynamicVars.Poison.IntValue, card.Owner.Creature);
        }
    }

    private static void ApplyCaptureSpirit(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        PredictedCard playedCard,
        Creature target,
        ISet<uint> processedEnemyDeaths)
    {
        CardModel card = playedCard.Preview;
        simulator.Damage(
            [target],
            card.DynamicVars.Damage.BaseValue,
            card.DynamicVars.Damage.Props,
            card.Owner.Creature,
            playedCard,
            null);
        if (simulator.HasPendingChoice)
            return;
        CorePowerSupport.ApplyEnemyDeathPowers(
            simulator,
            combat,
            combat.Enemies,
            processedEnemyDeaths);
        if (simulator.HasPendingChoice)
            return;

        List<PredictedCard> souls = new(card.DynamicVars.Cards.IntValue);
        for (int index = 0; index < card.DynamicVars.Cards.IntValue; index++)
            souls.Add(PredictedCard.Create(CanonicalModels.Card<Soul>(), card.Owner));
        simulator.AddGeneratedCardsToCombat(
            souls,
            PileType.Draw,
            card.Owner,
            CardPilePosition.Random,
            CardGenerationResultKind.Fixed);
    }

    private static void ApplyEchoingSlash(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        PredictedCard playedCard,
        CardPlay cardPlay,
        ISet<uint> processedEnemyDeaths)
    {
        CardModel card = playedCard.Preview;
        var attackContext = simulator.BeginAttackContext(playedCard, cardPlay);
        bool completed = false;
        try
        {
            if (simulator.HasPendingChoice)
                return;

            int attackCount = 1;
            while (attackCount > 0)
            {
                attackCount--;
                List<Creature> targets = AliveHittableEnemies(simulator, combat);
                IReadOnlyList<DamageResult> results = simulator.Damage(
                    targets,
                    card.DynamicVars.Damage.BaseValue,
                    card.DynamicVars.Damage.Props,
                    card.Owner.Creature,
                    playedCard,
                    cardPlay);
                if (simulator.HasPendingChoice)
                    return;
                simulator.AddAttackContextHit(attackContext, results);
                attackCount += results.Count(result => result.WasTargetKilled);
                CorePowerSupport.ApplyEnemyDeathPowers(
                    simulator,
                    combat,
                    combat.Enemies,
                    processedEnemyDeaths);
                if (simulator.HasPendingChoice)
                    return;
            }
            completed = true;
        }
        finally
        {
            simulator.EndAttackContext(attackContext, completed);
        }
    }

    private static void ApplyEndOfDays(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        CardModel card,
        ISet<uint> processedEnemyDeaths)
    {
        List<Creature> targets = AliveHittableEnemies(simulator, combat);
        foreach (Creature enemy in targets)
            combat.Apply<DoomPower>(enemy, card.DynamicVars.Doom.IntValue, card.Owner.Creature);

        List<Creature> doomed = targets
            .Where(enemy => simulator.State.GetCreature(enemy).CurrentHp <= combat.GetAmount<DoomPower>(enemy))
            .ToList();
        combat.DoomKill(simulator, doomed);
        if (simulator.HasPendingChoice)
            return;
        CorePowerSupport.ApplyEnemyDeathPowers(
            simulator,
            combat,
            combat.Enemies,
            processedEnemyDeaths);
    }

    private static void ApplyOmnislice(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        PredictedCard playedCard,
        CardPlay cardPlay,
        Creature target,
        ISet<uint> processedEnemyDeaths)
    {
        CardModel card = playedCard.Preview;
        var attackContext = simulator.BeginAttackContext(playedCard, cardPlay);
        bool completed = false;
        try
        {
            if (simulator.HasPendingChoice)
                return;

            IReadOnlyList<DamageResult> firstResults = simulator.Damage(
                [target],
                card.DynamicVars.Damage.BaseValue,
                ValueProp.Move,
                card.Owner.Creature,
                playedCard,
                cardPlay);
            if (simulator.HasPendingChoice)
                return;
            simulator.AddAttackContextHit(attackContext, firstResults);
            DamageResult? first = firstResults.FirstOrDefault();
            CorePowerSupport.ApplyEnemyDeathPowers(
                simulator,
                combat,
                combat.Enemies,
                processedEnemyDeaths);
            if (simulator.HasPendingChoice)
                return;
            if (first == null)
            {
                completed = true;
                return;
            }

            List<Creature> otherTargets = combat.GetTeammatesOf(first.Receiver)
                .Where(enemy => !ReferenceEquals(enemy, target)
                    && simulator.State.IsHittable(enemy))
                .ToList();
            if (otherTargets.Count == 0)
            {
                completed = true;
                return;
            }
            IReadOnlyList<DamageResult> copiedResults = simulator.Damage(
                otherTargets,
                first.TotalDamage + first.OverkillDamage,
                ValueProp.Unpowered | ValueProp.Move,
                card.Owner.Creature,
                playedCard,
                cardPlay);
            if (simulator.HasPendingChoice)
                return;
            simulator.AddAttackContextHit(attackContext, copiedResults);
            CorePowerSupport.ApplyEnemyDeathPowers(
                simulator,
                combat,
                combat.Enemies,
                processedEnemyDeaths);
            if (simulator.HasPendingChoice)
                return;
            completed = true;
        }
        finally
        {
            simulator.EndAttackContext(attackContext, completed);
        }
    }

    private static void ApplyOutbreak(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        CardModel card,
        ISet<uint> processedEnemyDeaths)
    {
        List<Creature> targets = AliveHittableEnemies(simulator, combat);
        foreach (Creature enemy in targets)
            combat.Apply<PoisonPower>(enemy, card.DynamicVars.Poison.IntValue, card.Owner.Creature);
        foreach (Creature enemy in targets)
        {
            if (!CorePowerSupport.TriggerPoison(simulator, combat, [enemy]))
                return;
            CorePowerSupport.ApplyEnemyDeathPowers(
                simulator,
                combat,
                combat.Enemies,
                processedEnemyDeaths);
            if (simulator.HasPendingChoice)
                return;
        }
    }

    private static List<Creature> AliveHittableEnemies(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat)
        => combat.Enemies
            .Where(simulator.State.IsHittable)
            .ToList();

}
