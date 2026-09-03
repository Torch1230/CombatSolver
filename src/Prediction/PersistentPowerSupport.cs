using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static class PersistentPowerSupport
{
    public static int ConsumeModifiedHandDraw(
        SimulatedCombatState combat,
        Player player,
        int baseDraw)
    {
        DrawCardsNextTurnPower? delayedDraw = combat.GetPower<DrawCardsNextTurnPower>(player.Creature);
        int drawNotYetSnapshotted = delayedDraw is { Amount: > 0, AmountOnTurnStart: 0 }
            ? delayedDraw.Amount
            : 0;
        int result = GetModifiedHandDraw(
            combat,
            player,
            baseDraw + combat.ConsumeDrawNextTurn(player) + drawNotYetSnapshotted);
        combat.CompleteRelicHandDraw(player);
        return result;
    }

    public static int GetModifiedHandDraw(
        SimulatedCombatState combat,
        Player player,
        int baseDraw)
    {
        decimal result = Hook.ModifyHandDraw(combat, player, baseDraw, out _);
        result = AdjustTurnBasedRelicHandDraw(combat, player, result);
        return Math.Max(0, (int)result);
    }

    public static int GetModifiedMaxEnergy(SimulatedCombatState combat, Player player)
    {
        decimal result = Hook.ModifyMaxEnergy(combat, player, player.MaxEnergy);
        result = AdjustTurnBasedRelicMaxEnergy(combat, player, result);
        return Math.Max(0, (int)result);
    }

    private static decimal AdjustTurnBasedRelicHandDraw(
        SimulatedCombatState combat,
        Player player,
        decimal result)
    {
        int rootTurn = combat.GetRootPlayerTurnNumber(player);
        int simulatedTurn = combat.GetPlayerTurnNumber(player);
        foreach (RelicModel relic in combat.RelicsOf(player).Where(static relic => !relic.IsMelted))
        {
            result -= GetTurnBasedHandDrawContribution(relic, combat, rootTurn);
            result -= SimulatedCombatState.GetLiveStatefulRelicHandDrawContribution(
                relic,
                player,
                rootTurn);
            result += GetTurnBasedHandDrawContribution(relic, combat, simulatedTurn);
            result += combat.GetStatefulRelicHandDrawContribution(relic, player, simulatedTurn);
        }
        return result;
    }

    private static decimal GetTurnBasedHandDrawContribution(
        RelicModel relic,
        SimulatedCombatState combat,
        int turn)
        => relic switch
        {
            BagOfPreparation when turn <= 1 => relic.DynamicVars.Cards.BaseValue,
            BigMushroom when turn == 1 => -relic.DynamicVars.Cards.BaseValue,
            BoomingConch when turn <= 1
                && combat.CurrentRoomType == RoomType.Elite
                => relic.DynamicVars.Cards.BaseValue,
            RingOfTheDrake when turn <= relic.DynamicVars["Turns"].IntValue
                => relic.DynamicVars.Cards.BaseValue,
            RingOfTheSnake when turn <= 1 => relic.DynamicVars.Cards.BaseValue,
            _ => 0m,
        };

    private static decimal AdjustTurnBasedRelicMaxEnergy(
        SimulatedCombatState combat,
        Player player,
        decimal result)
    {
        int rootTurn = combat.GetRootPlayerTurnNumber(player);
        int simulatedTurn = combat.GetPlayerTurnNumber(player);
        if (rootTurn == simulatedTurn)
            return result;

        foreach (RelicModel relic in combat.RelicsOf(player).Where(static relic => !relic.IsMelted))
        {
            result -= GetTurnBasedMaxEnergyContribution(relic, rootTurn);
            result += GetTurnBasedMaxEnergyContribution(relic, simulatedTurn);
        }
        return result;
    }

    private static decimal GetTurnBasedMaxEnergyContribution(RelicModel relic, int turn)
        => relic switch
        {
            Bread when turn != 1 => relic.DynamicVars["GainEnergy"].BaseValue,
            PaelsFlesh when turn >= 3 => relic.DynamicVars.Energy.BaseValue,
            _ => 0m,
        };

    public static void TriggerAfterEnergyReset(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Player player)
    {
        Creature owner = player.Creature;
        SimPlayerCombatState state = simulator.State.GetPlayerCombatState(player);

        int genesis = combat.GetAmount<GenesisPower>(owner);
        if (genesis > 0)
            simulator.GainStars(player, genesis);

        int lightningRod = combat.GetAmount<LightningRodPower>(owner);
        if (lightningRod > 0)
        {
            simulator.OrbChannel<LightningOrb>(player);
            combat.SetAmount<LightningRodPower>(owner, lightningRod - 1);
        }

        RadiancePower? radiance = combat.GetPower<RadiancePower>(owner);
        if (radiance is { Amount: > 0 })
        {
            if (combat.GetAmount<NoEnergyGainPower>(owner) <= 0)
                state.GainEnergy(radiance.DynamicVars.Energy.IntValue);
            combat.SetAmount<RadiancePower>(owner, radiance.Amount - 1);
        }

        int spinner = combat.GetAmount<SpinnerPower>(owner);
        if (spinner > 0)
            simulator.OrbChannel<GlassOrb>(player, spinner);

        int starsNextTurn = combat.GetAmount<StarNextTurnPower>(owner);
        if (starsNextTurn > 0)
        {
            simulator.GainStars(player, starsNextTurn);
            combat.SetAmount<StarNextTurnPower>(owner, 0);
        }
    }

    public static void TriggerAfterSideTurnStart(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        CombatSide side,
        IReadOnlyList<Creature> participants,
        bool isExtraTurn = false)
    {
        foreach (Creature owner in participants)
            TriggerOwnerAfterSideTurnStart(simulator, combat, owner);

        if (side == CombatSide.Player && !isExtraTurn)
            TriggerRampart(simulator, combat);
    }

    public static void TriggerRitual(SimulatedCombatState combat, Creature owner)
    {
        int amount = combat.GetAmount<RitualPower>(owner);
        if (amount <= 0 || combat.ConsumeRitualApplicationDelay(owner))
            return;
        combat.Apply<StrengthPower>(owner, amount, owner);
    }

    private static void TriggerOwnerAfterSideTurnStart(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Creature owner)
    {
        int biasedCognition = combat.GetAmount<BiasedCognitionPower>(owner);
        if (biasedCognition > 0)
            combat.Apply<FocusPower>(owner, -biasedCognition, owner);

        int coolant = combat.GetAmount<CoolantPower>(owner);
        if (coolant > 0 && owner.Player is { } coolantPlayer)
        {
            int distinctOrbs = simulator.State.GetPlayerCombatState(coolantPlayer).OrbQueue.Orbs
                .Select(static orb => orb.Id)
                .Distinct()
                .Count();
            simulator.GainBlock(owner, distinctOrbs * coolant, ValueProp.Unpowered);
        }

        int demonForm = combat.GetAmount<DemonFormPower>(owner);
        if (demonForm > 0)
            combat.Apply<StrengthPower>(owner, demonForm, owner);

        FeralPower? feral = combat.GetPower<FeralPower>(owner);
        if (feral is { Amount: > 0 })
            simulator.StateStore.Get(feral, () => new FeralPredictionState(feral)).ZeroCostAttacksPlayed = 0;

        int furnace = combat.GetAmount<FurnacePower>(owner);
        if (furnace > 0 && owner.Player is { } furnacePlayer)
            Forge(simulator, furnacePlayer, furnace);

        int neurosurge = combat.GetAmount<NeurosurgePower>(owner);
        if (neurosurge > 0)
            combat.Apply<DoomPower>(owner, neurosurge, owner);

        int noxiousFumes = combat.GetAmount<NoxiousFumesPower>(owner);
        if (noxiousFumes > 0)
        {
            foreach (Creature opponent in combat.GetOpponentsOf(owner))
            {
                if (simulator.State.IsHittable(opponent))
                    combat.Apply<PoisonPower>(opponent, noxiousFumes, owner);
            }
        }

        int prepTime = combat.GetAmount<PrepTimePower>(owner);
        if (prepTime > 0)
            combat.Apply<VigorPower>(owner, prepTime, owner);

        int reflect = combat.GetAmount<ReflectPower>(owner);
        if (reflect > 0)
            combat.SetAmount<ReflectPower>(owner, reflect - 1);

        int shadowStep = combat.GetAmount<ShadowStepPower>(owner);
        if (shadowStep > 0)
        {
            combat.Apply<DoubleDamagePower>(owner, shadowStep, owner);
            combat.SetAmount<ShadowStepPower>(owner, 0);
        }

        int wraithForm = combat.GetAmount<WraithFormPower>(owner);
        if (wraithForm > 0)
            combat.Apply<DexterityPower>(owner, -wraithForm, owner);

        int clarity = combat.GetAmount<ClarityPower>(owner);
        if (clarity > 0)
            combat.SetAmount<ClarityPower>(owner, clarity - 1);
    }

    private static void TriggerRampart(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat)
    {
        foreach (RampartPower rampart in combat.EffectivePowers().OfType<RampartPower>().ToArray())
        {
            if (rampart.Amount <= 0)
                continue;
            foreach (Creature enemy in combat.Enemies)
            {
                if (enemy.Monster is TurretOperator && simulator.State.GetCreature(enemy).IsAlive)
                    simulator.GainBlock(enemy, rampart.Amount, ValueProp.Unpowered);
            }
        }
    }

    public static void Forge(
        CombatPredictionSimulator simulator,
        Player player,
        int amount)
    {
        SimPlayerCombatState state = simulator.State.GetPlayerCombatState(player);
        bool hasUnexhaustedBlade = state.AllCards.Any(card =>
            card.Preview is SovereignBlade
            && !card.Preview.IsDupe
            && !state.ExhaustPile.Cards.Contains(card));
        if (!hasUnexhaustedBlade)
        {
            PredictedCard created = PredictedCard.Create(ModelDb.Card<SovereignBlade>(), player);
            ((SovereignBlade)created.MutablePreview).CreatedThroughForge = true;
            simulator.AddGeneratedCardToCombat(
                created,
                PileType.Hand,
                player,
                CardPilePosition.Bottom,
                CardGenerationResultKind.Fixed);
        }
        foreach (PredictedCard card in state.AllCards)
        {
            if (card.Preview is not SovereignBlade preview || preview.IsDupe)
                continue;
            ((SovereignBlade)card.MutablePreview).AddDamage(amount);
        }
    }
}
