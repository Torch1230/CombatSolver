using System.Text.Json;
using System.Text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static async Task SetBlockAsync(Creature creature, int targetBlock)
    {
        if (targetBlock < 0)
            throw new InvalidOperationException($"测试格挡不能为负数：{targetBlock}。");
        if (creature.Block > targetBlock)
        {
            await CreatureCmd.LoseBlock(
                new BlockingPlayerChoiceContext(),
                creature,
                creature.Block - targetBlock,
                null);
        }
        else if (creature.Block < targetBlock)
        {
            await CreatureCmd.GainBlock(
                creature,
                targetBlock - creature.Block,
                ValueProp.Unpowered,
                null,
                fast: true);
        }
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
    }

    private static void SetEnergy(Player player, int targetEnergy)
    {
        if (targetEnergy < 0)
            throw new InvalidOperationException($"测试能量不能为负数：{targetEnergy}。");
        PlayerCombatState state = player.PlayerCombatState
            ?? throw new InvalidOperationException("玩家没有 PlayerCombatState。");
        if (state.Energy > targetEnergy)
            state.LoseEnergy(state.Energy - targetEnergy);
        else if (state.Energy < targetEnergy)
            state.GainEnergy(targetEnergy - state.Energy);
    }

    private static void SetStars(Player player, int targetStars)
    {
        if (targetStars < 0)
            throw new InvalidOperationException($"测试星能不能为负数：{targetStars}。");
        PlayerCombatState state = player.PlayerCombatState
            ?? throw new InvalidOperationException("玩家没有 PlayerCombatState。");
        if (state.Stars > targetStars)
            state.LoseStars(state.Stars - targetStars);
        else if (state.Stars < targetStars)
            state.GainStars(targetStars - state.Stars);
    }

    private static MoveStateSnapshot CaptureActual(
        CombatState combatState,
        Player player,
        Creature enemy)
    {
        PlayerCombatState playerState = player.PlayerCombatState
            ?? throw new InvalidOperationException("玩家没有 PlayerCombatState。");
        IEnumerable<PowerModel> actualEnemyPowers = combatState.Enemies.Contains(enemy)
            ? enemy.Powers
            : [];
        string exactContinuationState = ContinuationStamp.CaptureLive(combatState).StateText;
        return new MoveStateSnapshot(
            player.Creature.CurrentHp,
            player.Creature.MaxHp,
            player.Creature.Block,
            player.Osty?.CurrentHp ?? 0,
            player.Osty?.MaxHp ?? 0,
            player.Osty?.IsHittable ?? false,
            player.Osty is { } actualOsty && combatState.Allies.Contains(actualOsty),
            NormalizePowers(player.Osty?.Powers ?? []),
            playerState.Energy,
            playerState.Stars,
            player.Gold,
            playerState.OrbQueue.Capacity,
            enemy.CurrentHp,
            enemy.MaxHp,
            enemy.Block,
            NormalizePowers(player.Creature.Powers),
            NormalizePowers(actualEnemyPowers),
            NormalizePowerStates(player.Creature.Powers),
            NormalizePowerStates(actualEnemyPowers),
            NormalizeActualPowerInternalStates(player.Creature.Powers),
            NormalizeActualPowerInternalStates(actualEnemyPowers),
            NormalizeActualPiles(player),
            NormalizeActualOrderedPiles(player),
            NormalizeActualPileCardDamageTotals(player),
            NormalizeActualPileCardDynamicVars(player),
            NormalizeActualCardStates(player),
            NormalizeActualCardCosts(player),
            NormalizeActualCardEnchantments(player),
            NormalizeActualCardEnchantmentStates(player),
            NormalizeActualCardUpgrades(player),
            NormalizeActualEnemyHps(combatState),
            NormalizeActualEnemyBlocks(combatState),
            NormalizeActualEnemyPowers(combatState),
            NormalizeActualMonsterAi(combatState),
            NormalizeActualMonsterState(combatState),
            NormalizeActualPotions(player),
            NormalizeOrderedOrbs(playerState.OrbQueue.Orbs),
            NormalizeOrbs(playerState.OrbQueue.Orbs),
            NormalizeRngCounters(
                combatState.RunState.Rng.Shuffle.ToSerializable().counter,
                combatState.RunState.Rng.CombatCardGeneration.ToSerializable().counter,
                combatState.RunState.Rng.CombatPotionGeneration.ToSerializable().counter,
                combatState.RunState.Rng.CombatCardSelection.ToSerializable().counter,
                combatState.RunState.Rng.CombatEnergyCosts.ToSerializable().counter,
                combatState.RunState.Rng.CombatTargets.ToSerializable().counter,
                combatState.RunState.Rng.CombatOrbGeneration.ToSerializable().counter,
                combatState.RunState.Rng.MonsterAi.ToSerializable().counter,
                combatState.RunState.Rng.Niche.ToSerializable().counter),
            exactContinuationState);
    }

    private static MoveStateSnapshot CaptureSimulated(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Player player,
        Creature enemy)
    {
        SimCreatureState simulatedPlayer = simulator.State.GetCreature(player.Creature);
        SimCreatureState simulatedEnemy = simulator.State.GetCreature(enemy);
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(player);
        IEnumerable<PowerModel> simulatedEnemyPowers = combat.EffectivePowers()
            .Where(power => ReferenceEquals(power.Owner, enemy));
        int turn = combat.GetPlayerTurnNumber(player);
        string exactContinuationState = ContinuationStamp.CapturePredicted(
            player,
            simulator,
            turn,
            new IntentForecast
            {
                Rounds = [],
                HasUnsupportedIntent = false,
                IsExactForModeledDamage = true,
                UnsupportedDetails = [],
                ApproximationDetails = [],
                MonsterAiCountersByRound = [],
            },
            turn).StateText;
        Creature? osty = combat.GetOsty(player);
        return new MoveStateSnapshot(
            simulatedPlayer.CurrentHp,
            simulatedPlayer.MaxHp,
            simulatedPlayer.Block,
            osty is null ? 0 : simulator.State.GetCreature(osty).CurrentHp,
            combat.GetOstyMaxHp(simulator, player),
            combat.IsOstyHittable(simulator, player),
            osty is not null && simulator.State.Allies.Contains(osty),
            NormalizePowers(combat.EffectivePowers().Where(power => ReferenceEquals(power.Owner, osty))),
            playerState.Energy,
            playerState.Stars,
            combat.GetPlayerGold(player),
            playerState.OrbQueue.Capacity,
            simulatedEnemy.CurrentHp,
            simulatedEnemy.MaxHp,
            simulatedEnemy.Block,
            NormalizePowers(combat.EffectivePowers().Where(power => ReferenceEquals(power.Owner, player.Creature))),
            NormalizePowers(simulatedEnemyPowers),
            NormalizePowerStates(combat.EffectivePowers().Where(power => ReferenceEquals(power.Owner, player.Creature))),
            NormalizePowerStates(simulatedEnemyPowers),
            NormalizeSimulatedPowerInternalStates(
                simulator,
                combat.EffectivePowers().Where(power => ReferenceEquals(power.Owner, player.Creature))),
            NormalizeSimulatedPowerInternalStates(simulator, simulatedEnemyPowers),
            NormalizeSimulatedPiles(simulator, player),
            NormalizeSimulatedOrderedPiles(simulator, player),
            NormalizeSimulatedPileCardDamageTotals(simulator, player),
            NormalizeSimulatedPileCardDynamicVars(simulator, player),
            NormalizeSimulatedCardStates(simulator, player),
            NormalizeSimulatedCardCosts(simulator, player),
            NormalizeSimulatedCardEnchantments(simulator, player),
            NormalizeSimulatedCardEnchantmentStates(simulator, player),
            NormalizeSimulatedCardUpgrades(simulator, player),
            NormalizeSimulatedEnemyHps(simulator, combat),
            NormalizeSimulatedEnemyBlocks(simulator, combat),
            NormalizeSimulatedEnemyPowers(simulator, combat),
            NormalizeSimulatedMonsterAi(combat),
            NormalizeSimulatedMonsterState(combat),
            NormalizeSimulatedPotions(combat, player),
            NormalizePredictedOrderedOrbs(simulator, playerState.OrbQueue.Orbs),
            NormalizeOrbs(playerState.OrbQueue.Orbs),
            NormalizeRngCounters(
                simulator.Rng.Shuffle.ToSerializable().counter,
                simulator.Rng.CombatCardGeneration.ToSerializable().counter,
                simulator.Rng.CombatPotionGeneration.ToSerializable().counter,
                simulator.Rng.CombatCardSelection.ToSerializable().counter,
                simulator.Rng.CombatEnergyCosts.ToSerializable().counter,
                simulator.Rng.CombatTargets.ToSerializable().counter,
                simulator.Rng.CombatOrbGeneration.ToSerializable().counter,
                simulator.Rng.MonsterAi.ToSerializable().counter,
                simulator.Rng.Niche.ToSerializable().counter),
            exactContinuationState);
    }

    private static Dictionary<string, int> NormalizePowers(IEnumerable<PowerModel> powers)
        => powers
            .Where(static power => power.Amount != 0)
            .GroupBy(static power => power.Id.Entry, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Sum(power => power.Amount),
                StringComparer.Ordinal);

    private static Dictionary<string, int> NormalizePowerStates(IEnumerable<PowerModel> powers)
        => powers
            .Where(static power => power.Amount != 0)
            .SelectMany(power => power.DynamicVars.Select(dynamicVar =>
                NormalizePowerState(power, dynamicVar.Key, dynamicVar.Value)))
            .GroupBy(static state => state, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);

    private static string NormalizePowerState(PowerModel power, string fieldName, DynamicVar value)
    {
        string state = $"{power.Id.Entry}.{fieldName}={value.BaseValue}";
        if (value is not StringVar stringVar)
            return state;

        return SemanticStateFieldPolicy.Classify(power, fieldName, value) ==
            SemanticStateFieldRole.PresentationOnly
                ? state
                : $"{state}:{stringVar.StringValue}";
    }

    private static Dictionary<string, int> NormalizeActualPiles(Player player)
    {
        PlayerCombatState state = player.PlayerCombatState
            ?? throw new InvalidOperationException("玩家没有 PlayerCombatState。");
        return NormalizePileEntries(
            state.DrawPile.Cards.Select(card => (PileType.Draw, card.Id.Entry))
                .Concat(state.Hand.Cards.Select(card => (PileType.Hand, card.Id.Entry)))
                .Concat(state.DiscardPile.Cards.Select(card => (PileType.Discard, card.Id.Entry)))
                .Concat(state.ExhaustPile.Cards.Select(card => (PileType.Exhaust, card.Id.Entry))));
    }

    private static Dictionary<string, int> NormalizeSimulatedPiles(
        CombatPredictionSimulator simulator,
        Player player)
    {
        SimPlayerCombatState state = simulator.State.GetPlayerCombatState(player);
        return NormalizePileEntries(
            state.DrawPile.Cards.Select(card => (PileType.Draw, card.Preview.Id.Entry))
                .Concat(state.Hand.Cards.Select(card => (PileType.Hand, card.Preview.Id.Entry)))
                .Concat(state.DiscardPile.Cards.Select(card => (PileType.Discard, card.Preview.Id.Entry)))
                .Concat(state.ExhaustPile.Cards.Select(card => (PileType.Exhaust, card.Preview.Id.Entry))));
    }

    private static Dictionary<string, int> NormalizePileEntries(
        IEnumerable<(PileType Pile, string CardId)> entries)
        => entries
            .GroupBy(static entry => $"{entry.Pile}:{entry.CardId}", StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);

    private static Dictionary<string, int> NormalizeActualPileCardDamageTotals(Player player)
    {
        PlayerCombatState state = player.PlayerCombatState
            ?? throw new InvalidOperationException("玩家没有 PlayerCombatState。");
        return NormalizePileCardDamageTotals(
            state.DrawPile.Cards.Select(card => (PileType.Draw, card))
                .Concat(state.Hand.Cards.Select(card => (PileType.Hand, card)))
                .Concat(state.DiscardPile.Cards.Select(card => (PileType.Discard, card)))
                .Concat(state.ExhaustPile.Cards.Select(card => (PileType.Exhaust, card))));
    }

    private static Dictionary<string, int> NormalizeActualPileCardDynamicVars(Player player)
    {
        PlayerCombatState state = player.PlayerCombatState
            ?? throw new InvalidOperationException("玩家没有 PlayerCombatState。");
        return NormalizePileCardDynamicVars(
            state.DrawPile.Cards.Select(card => (PileType.Draw, card))
                .Concat(state.Hand.Cards.Select(card => (PileType.Hand, card)))
                .Concat(state.DiscardPile.Cards.Select(card => (PileType.Discard, card)))
                .Concat(state.ExhaustPile.Cards.Select(card => (PileType.Exhaust, card))));
    }

    private static Dictionary<string, int> NormalizeSimulatedPileCardDynamicVars(
        CombatPredictionSimulator simulator,
        Player player)
    {
        SimPlayerCombatState state = simulator.State.GetPlayerCombatState(player);
        return NormalizePileCardDynamicVars(
            state.DrawPile.Cards.Select(card => (PileType.Draw, card.Preview))
                .Concat(state.Hand.Cards.Select(card => (PileType.Hand, card.Preview)))
                .Concat(state.DiscardPile.Cards.Select(card => (PileType.Discard, card.Preview)))
                .Concat(state.ExhaustPile.Cards.Select(card => (PileType.Exhaust, card.Preview))));
    }

    private static Dictionary<string, int> NormalizePileCardDynamicVars(
        IEnumerable<(PileType Pile, CardModel Card)> entries)
        => entries.SelectMany(entry => entry.Card.DynamicVars.Select(dynamicVar =>
                $"{entry.Pile}:{entry.Card.Id.Entry}:{dynamicVar.Key}={dynamicVar.Value.BaseValue}" +
                (dynamicVar.Value is StringVar stringVar ? $":{stringVar.StringValue}" : string.Empty)))
            .GroupBy(static value => value, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);

    private static Dictionary<string, int> NormalizeSimulatedPileCardDamageTotals(
        CombatPredictionSimulator simulator,
        Player player)
    {
        SimPlayerCombatState state = simulator.State.GetPlayerCombatState(player);
        return NormalizePileCardDamageTotals(
            state.DrawPile.Cards.Select(card => (PileType.Draw, card.Preview))
                .Concat(state.Hand.Cards.Select(card => (PileType.Hand, card.Preview)))
                .Concat(state.DiscardPile.Cards.Select(card => (PileType.Discard, card.Preview)))
                .Concat(state.ExhaustPile.Cards.Select(card => (PileType.Exhaust, card.Preview))));
    }

    private static Dictionary<string, int> NormalizePileCardDamageTotals(
        IEnumerable<(PileType Pile, CardModel Card)> entries)
        => entries
            .Where(static entry => entry.Card is Wither or SovereignBlade)
            .GroupBy(static entry => $"{entry.Pile}:{entry.Card.Id.Entry}", StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Sum(entry => entry.Card.DynamicVars.Damage.IntValue),
                StringComparer.Ordinal);

    private static string NormalizeActualOrderedPiles(Player player)
    {
        PlayerCombatState state = player.PlayerCombatState
            ?? throw new InvalidOperationException("玩家没有 PlayerCombatState。");
        return DescribeOrderedPiles(
            state.Hand.Cards.Select(DescribeCard),
            state.DrawPile.Cards.Select(DescribeCard),
            state.DiscardPile.Cards.Select(DescribeCard),
            state.ExhaustPile.Cards.Select(DescribeCard));
    }

    private static string NormalizeSimulatedOrderedPiles(
        CombatPredictionSimulator simulator,
        Player player)
    {
        SimPlayerCombatState state = simulator.State.GetPlayerCombatState(player);
        return DescribeOrderedPiles(
            state.Hand.Cards.Select(card => DescribeCard(card.Preview)),
            state.DrawPile.Cards.Select(card => DescribeCard(card.Preview)),
            state.DiscardPile.Cards.Select(card => DescribeCard(card.Preview)),
            state.ExhaustPile.Cards.Select(card => DescribeCard(card.Preview)));
    }

    private static string DescribeOrderedPiles(
        IEnumerable<string> hand,
        IEnumerable<string> draw,
        IEnumerable<string> discard,
        IEnumerable<string> exhaust)
        => $"H=[{string.Join(',', hand)}];D=[{string.Join(',', draw)}];" +
           $"C=[{string.Join(',', discard)}];X=[{string.Join(',', exhaust)}]";

    private static string DescribeCard(CardModel card)
        => $"{card.Id.Entry}+{card.CurrentUpgradeLevel}";

    private static Dictionary<string, int> NormalizeActualCardStates(Player player)
    {
        PlayerCombatState state = player.PlayerCombatState
            ?? throw new InvalidOperationException("玩家没有 PlayerCombatState。");
        return NormalizeCardStates(
            state.DrawPile.Cards.Select(card => (PileType.Draw, card, card.Keywords.Contains(CardKeyword.Ethereal)))
                .Concat(state.Hand.Cards.Select(card => (PileType.Hand, card, card.Keywords.Contains(CardKeyword.Ethereal))))
                .Concat(state.DiscardPile.Cards.Select(card => (PileType.Discard, card, card.Keywords.Contains(CardKeyword.Ethereal))))
                .Concat(state.ExhaustPile.Cards.Select(card => (PileType.Exhaust, card, card.Keywords.Contains(CardKeyword.Ethereal)))));
    }

    private static Dictionary<string, int> NormalizeSimulatedCardStates(
        CombatPredictionSimulator simulator,
        Player player)
    {
        SimPlayerCombatState state = simulator.State.GetPlayerCombatState(player);
        return NormalizeCardStates(
            state.DrawPile.Cards.Select(card => (PileType.Draw, card.Preview, card.HasKeyword(simulator.State, CardKeyword.Ethereal)))
                .Concat(state.Hand.Cards.Select(card => (PileType.Hand, card.Preview, card.HasKeyword(simulator.State, CardKeyword.Ethereal))))
                .Concat(state.DiscardPile.Cards.Select(card => (PileType.Discard, card.Preview, card.HasKeyword(simulator.State, CardKeyword.Ethereal))))
                .Concat(state.ExhaustPile.Cards.Select(card => (PileType.Exhaust, card.Preview, card.HasKeyword(simulator.State, CardKeyword.Ethereal)))));
    }

    private static Dictionary<string, int> NormalizeCardStates(
        IEnumerable<(PileType Pile, CardModel Card, bool Ethereal)> entries)
        => entries
            .GroupBy(
                static entry => $"{entry.Pile}:{entry.Card.Id.Entry}:" +
                    $"{entry.Card.Affliction?.Id.Entry ?? "-"}:{entry.Card.Affliction?.Amount ?? 0}:" +
                    $"{(entry.Ethereal ? "E" : "-")}:" +
                    $"R{entry.Card.BaseReplayCount}:" +
                    $"X{entry.Card.ExhaustOnNextPlay}:" +
                    $"S{entry.Card.IsSlyThisTurn}:" +
                    $"T{entry.Card.ShouldRetainThisTurn}:" +
                    $"D{entry.Card.DeckVersion != null}:" +
                    $"M{entry.Card.HasBeenRemovedFromState}",
                StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);

    private static Dictionary<string, int> NormalizeActualCardCosts(Player player)
    {
        PlayerCombatState state = player.PlayerCombatState
            ?? throw new InvalidOperationException("玩家没有 PlayerCombatState。");
        return NormalizeCardCosts(
            state.DrawPile.Cards.Select(card => (PileType.Draw, card))
                .Concat(state.Hand.Cards.Select(card => (PileType.Hand, card)))
                .Concat(state.DiscardPile.Cards.Select(card => (PileType.Discard, card)))
                .Concat(state.ExhaustPile.Cards.Select(card => (PileType.Exhaust, card))));
    }

    private static Dictionary<string, int> NormalizeSimulatedCardCosts(
        CombatPredictionSimulator simulator,
        Player player)
    {
        SimPlayerCombatState state = simulator.State.GetPlayerCombatState(player);
        return NormalizeCardCosts(
            state.DrawPile.Cards.Select(card => (PileType.Draw, card.Preview))
                .Concat(state.Hand.Cards.Select(card => (PileType.Hand, card.Preview)))
                .Concat(state.DiscardPile.Cards.Select(card => (PileType.Discard, card.Preview)))
                .Concat(state.ExhaustPile.Cards.Select(card => (PileType.Exhaust, card.Preview))));
    }

    private static Dictionary<string, int> NormalizeCardCosts(
        IEnumerable<(PileType Pile, CardModel Card)> entries)
        => entries
            .GroupBy(
                static entry => $"{entry.Pile}:{entry.Card.Id.Entry}:" +
                    $"{entry.Card.EnergyCost.GetWithModifiers(CostModifiers.Local)}",
                StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);

    private static Dictionary<string, int> NormalizeActualCardEnchantments(Player player)
    {
        PlayerCombatState state = player.PlayerCombatState
            ?? throw new InvalidOperationException("玩家没有 PlayerCombatState。");
        return NormalizeCardEnchantments(
            state.DrawPile.Cards.Select(card => (PileType.Draw, card))
                .Concat(state.Hand.Cards.Select(card => (PileType.Hand, card)))
                .Concat(state.DiscardPile.Cards.Select(card => (PileType.Discard, card)))
                .Concat(state.ExhaustPile.Cards.Select(card => (PileType.Exhaust, card))));
    }

    private static Dictionary<string, int> NormalizeSimulatedCardEnchantments(
        CombatPredictionSimulator simulator,
        Player player)
    {
        SimPlayerCombatState state = simulator.State.GetPlayerCombatState(player);
        return NormalizeCardEnchantments(
            state.DrawPile.Cards.Select(card => (PileType.Draw, card.Preview))
                .Concat(state.Hand.Cards.Select(card => (PileType.Hand, card.Preview)))
                .Concat(state.DiscardPile.Cards.Select(card => (PileType.Discard, card.Preview)))
                .Concat(state.ExhaustPile.Cards.Select(card => (PileType.Exhaust, card.Preview))));
    }

    private static Dictionary<string, int> NormalizeCardEnchantments(
        IEnumerable<(PileType Pile, CardModel Card)> entries)
        => entries
            .Where(static entry => entry.Card.Enchantment != null)
            .GroupBy(
                static entry => $"{entry.Pile}:{entry.Card.Id.Entry}:" +
                    $"{entry.Card.Enchantment!.Id.Entry}:{entry.Card.Enchantment.Amount}",
                StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);

    private static Dictionary<string, int> NormalizeActualCardEnchantmentStates(Player player)
    {
        PlayerCombatState state = player.PlayerCombatState
            ?? throw new InvalidOperationException("玩家没有 PlayerCombatState。");
        return NormalizeCardEnchantmentStates(
            state.DrawPile.Cards.Select(card => (PileType.Draw, card))
                .Concat(state.Hand.Cards.Select(card => (PileType.Hand, card)))
                .Concat(state.DiscardPile.Cards.Select(card => (PileType.Discard, card)))
                .Concat(state.ExhaustPile.Cards.Select(card => (PileType.Exhaust, card))));
    }

    private static Dictionary<string, int> NormalizeSimulatedCardEnchantmentStates(
        CombatPredictionSimulator simulator,
        Player player)
    {
        SimPlayerCombatState state = simulator.State.GetPlayerCombatState(player);
        return NormalizeCardEnchantmentStates(
            state.DrawPile.Cards.Select(card => (PileType.Draw, card.Preview))
                .Concat(state.Hand.Cards.Select(card => (PileType.Hand, card.Preview)))
                .Concat(state.DiscardPile.Cards.Select(card => (PileType.Discard, card.Preview)))
                .Concat(state.ExhaustPile.Cards.Select(card => (PileType.Exhaust, card.Preview))));
    }

    private static Dictionary<string, int> NormalizeCardEnchantmentStates(
        IEnumerable<(PileType Pile, CardModel Card)> entries)
        => entries
            .Where(static entry => entry.Card.Enchantment != null)
            .GroupBy(
                static entry => $"{entry.Pile}:{entry.Card.Id.Entry}:" +
                    EnchantmentStateSupport.Describe(entry.Card.Enchantment!),
                StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);

    private static Dictionary<string, int> NormalizeActualCardUpgrades(Player player)
    {
        PlayerCombatState state = player.PlayerCombatState
            ?? throw new InvalidOperationException("玩家没有 PlayerCombatState。");
        return NormalizeCardUpgrades(
            state.DrawPile.Cards.Select(card => (PileType.Draw, card))
                .Concat(state.Hand.Cards.Select(card => (PileType.Hand, card)))
                .Concat(state.DiscardPile.Cards.Select(card => (PileType.Discard, card)))
                .Concat(state.ExhaustPile.Cards.Select(card => (PileType.Exhaust, card))));
    }

    private static Dictionary<string, int> NormalizeSimulatedCardUpgrades(
        CombatPredictionSimulator simulator,
        Player player)
    {
        SimPlayerCombatState state = simulator.State.GetPlayerCombatState(player);
        return NormalizeCardUpgrades(
            state.DrawPile.Cards.Select(card => (PileType.Draw, card.Preview))
                .Concat(state.Hand.Cards.Select(card => (PileType.Hand, card.Preview)))
                .Concat(state.DiscardPile.Cards.Select(card => (PileType.Discard, card.Preview)))
                .Concat(state.ExhaustPile.Cards.Select(card => (PileType.Exhaust, card.Preview))));
    }

    private static Dictionary<string, int> NormalizeCardUpgrades(
        IEnumerable<(PileType Pile, CardModel Card)> entries)
        => entries
            .Where(static entry => entry.Card.CurrentUpgradeLevel > 0)
            .GroupBy(
                static entry => $"{entry.Pile}:{entry.Card.Id.Entry}:{entry.Card.CurrentUpgradeLevel}",
                StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);

    private static Dictionary<string, int> NormalizeActualEnemyHps(ICombatState state)
        => state.Enemies
            .Where(static enemy => enemy.Monster != null)
            .GroupBy(static enemy => enemy.Monster!.Id.Entry, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Sum(enemy => enemy.CurrentHp),
                StringComparer.Ordinal);

    private static Dictionary<string, int> NormalizeSimulatedEnemyHps(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat)
        => combat.Enemies
            .Where(static enemy => enemy.Monster != null)
            .GroupBy(static enemy => enemy.Monster!.Id.Entry, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                group => group.Sum(enemy => simulator.State.GetCreature(enemy).CurrentHp),
                StringComparer.Ordinal);

    private static Dictionary<string, int> NormalizeActualEnemyBlocks(ICombatState state)
        => state.Enemies
            .Where(static enemy => enemy.Monster != null)
            .GroupBy(static enemy => enemy.Monster!.Id.Entry, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Sum(enemy => enemy.Block),
                StringComparer.Ordinal);

    private static Dictionary<string, int> NormalizeSimulatedEnemyBlocks(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat)
        => combat.Enemies
            .Where(static enemy => enemy.Monster != null)
            .GroupBy(static enemy => enemy.Monster!.Id.Entry, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                group => group.Sum(enemy => simulator.State.GetCreature(enemy).Block),
                StringComparer.Ordinal);

    private static Dictionary<string, int> NormalizeActualEnemyPowers(ICombatState state)
        => NormalizeEnemyPowers(state.Enemies.SelectMany(static enemy => enemy.Powers));

    private static Dictionary<string, int> NormalizeSimulatedEnemyPowers(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat)
    {
        HashSet<Creature> enemies = combat.Enemies.ToHashSet();
        return NormalizeEnemyPowers(combat.EffectivePowers()
            .Where(power => enemies.Contains(power.Owner)));
    }

    private static Dictionary<string, int> NormalizeEnemyPowers(IEnumerable<PowerModel> powers)
        => powers
            .Where(static power => power.Amount != 0 && power.Owner?.Monster != null)
            .GroupBy(
                static power => $"{power.Owner!.Monster!.Id.Entry}:{power.Id.Entry}",
                StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Sum(power => power.Amount),
            StringComparer.Ordinal);

    private static Dictionary<string, int> NormalizeActualPowerInternalStates(IEnumerable<PowerModel> powers)
        => powers.SelectMany(power => power switch
            {
                HellraiserPower hellraiser => new[]
                {
                    new KeyValuePair<string, int>(
                        $"{power.Id.Entry}.InfiniteAutoPlaysThisTurn",
                        hellraiser.GetInternalData<HellraiserPower.Data>().infiniteAutoPlaysThisTurn),
                },
                AutomationPower automation => new[]
                {
                    new KeyValuePair<string, int>(
                        $"{power.Id.Entry}.CardsLeft",
                        automation.DisplayAmount),
                },
                VoidFormPower voidForm => new[]
                {
                    new KeyValuePair<string, int>(
                        $"{power.Id.Entry}.CardsPlayedThisTurn",
                        voidForm.GetInternalData<VoidFormPower.Data>().cardsPlayedThisTurn),
                },
                _ => [],
            })
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private static Dictionary<string, int> NormalizeSimulatedPowerInternalStates(
        CombatPredictionSimulator simulator,
        IEnumerable<PowerModel> powers)
        => powers.SelectMany(power => power switch
            {
                HellraiserPower hellraiser => new[]
                {
                    new KeyValuePair<string, int>(
                        $"{power.Id.Entry}.InfiniteAutoPlaysThisTurn",
                        simulator.StateStore.Peek(
                            hellraiser,
                            () => new HellraiserPredictionState(hellraiser)).InfiniteAutoPlaysThisTurn),
                },
                AutomationPower automation => new[]
                {
                    new KeyValuePair<string, int>(
                        $"{power.Id.Entry}.CardsLeft",
                        simulator.StateStore.Peek(
                            automation,
                            () => new AutomationPredictionState(automation)).CardsLeft),
                },
                VoidFormPower voidForm => new[]
                {
                    new KeyValuePair<string, int>(
                        $"{power.Id.Entry}.CardsPlayedThisTurn",
                        simulator.StateStore.Peek(
                            voidForm,
                            () => new VoidFormPredictionState(voidForm)).CardsPlayedThisTurn),
                },
                _ => [],
            })
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private static string NormalizeActualMonsterAi(CombatState combat)
    {
        StringBuilder text = new();
        SimulatedCombatState.AppendLiveMonsterAiContinuation(text, combat.Enemies);
        return text.ToString();
    }

    private static string NormalizeSimulatedMonsterAi(SimulatedCombatState combat)
    {
        StringBuilder text = new();
        combat.AppendPredictedMonsterAiContinuation(text);
        return text.ToString();
    }

    private static string NormalizeActualMonsterState(CombatState combat)
    {
        StringBuilder text = new();
        SimulatedCombatState.AppendLiveMonsterStateContinuation(text, combat.Enemies);
        return text.ToString();
    }

    private static string NormalizeSimulatedMonsterState(SimulatedCombatState combat)
    {
        StringBuilder text = new();
        combat.AppendPredictedMonsterStateContinuation(text);
        return text.ToString();
    }

    private static string NormalizeActualPotions(Player player)
        => string.Join(',', Enumerable.Range(0, player.PotionSlots.Count)
            .Select(slot => player.GetPotionAtSlotIndex(slot)?.Id.Entry ?? "-"));

    private static string NormalizeSimulatedPotions(SimulatedCombatState combat, Player player)
        => string.Join(',', Enumerable.Range(0, player.PotionSlots.Count)
            .Select(slot => combat.GetPotionAtSlot(player, slot)?.Id.Entry ?? "-"));

    private static Dictionary<string, int> NormalizeOrbs(IEnumerable<OrbModel> orbs)
        => orbs
            .GroupBy(static orb => orb.Id.Entry, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);

    private static string NormalizeOrderedOrbs(IEnumerable<OrbModel> orbs)
        => string.Join(',', orbs.Select(orb => $"{orb.Id.Entry}[{orb.PassiveVal}/{orb.EvokeVal}]"));

    private static string NormalizePredictedOrderedOrbs(
        CombatPredictionSimulator simulator,
        IEnumerable<OrbModel> orbs)
        => string.Join(',', orbs.Select(orb =>
            $"{orb.Id.Entry}[{Engine.InCombat.Mirrors.Orbs.OrbMirrors.GetPassiveValue(simulator, orb)}/" +
            $"{Engine.InCombat.Mirrors.Orbs.OrbMirrors.GetEvokeValue(simulator, orb)}]"));

    private static Dictionary<string, int> NormalizeRngCounters(
        int shuffle,
        int generation,
        int potionGeneration,
        int selection,
        int energy,
        int targets,
        int orbs,
        int monsterAi,
        int niche)
        => new(StringComparer.Ordinal)
        {
            ["Shuffle"] = shuffle,
            ["CombatCardGeneration"] = generation,
            ["CombatPotionGeneration"] = potionGeneration,
            ["CombatCardSelection"] = selection,
            ["CombatEnergyCosts"] = energy,
            ["CombatTargets"] = targets,
            ["CombatOrbGeneration"] = orbs,
            ["MonsterAi"] = monsterAi,
            ["Niche"] = niche,
        };

    private void AssertSnapshotEqual(
        MoveStateSnapshot predicted,
        MoveStateSnapshot actual,
        string monsterId,
        string moveId)
    {
        if (predicted.PlayerHp != actual.PlayerHp
            || predicted.PlayerMaxHp != actual.PlayerMaxHp
            || predicted.PlayerBlock != actual.PlayerBlock
            || predicted.OstyHp != actual.OstyHp
            || predicted.OstyMaxHp != actual.OstyMaxHp
            || predicted.OstyHittable != actual.OstyHittable
            || predicted.OstyInCombat != actual.OstyInCombat
            || !DictionaryEqual(predicted.OstyPowers, actual.OstyPowers)
            || predicted.PlayerEnergy != actual.PlayerEnergy
            || predicted.PlayerStars != actual.PlayerStars
            || predicted.PlayerGold != actual.PlayerGold
            || predicted.PlayerOrbCapacity != actual.PlayerOrbCapacity
            || predicted.EnemyHp != actual.EnemyHp
            || predicted.EnemyMaxHp != actual.EnemyMaxHp
            || predicted.EnemyBlock != actual.EnemyBlock
            || !DictionaryEqual(predicted.PlayerPowers, actual.PlayerPowers)
            || !DictionaryEqual(predicted.EnemyPowers, actual.EnemyPowers)
            || !DictionaryEqual(predicted.PlayerPowerStates, actual.PlayerPowerStates)
            || !DictionaryEqual(predicted.EnemyPowerStates, actual.EnemyPowerStates)
            || !DictionaryEqual(predicted.PlayerPowerInternalStates, actual.PlayerPowerInternalStates)
            || !DictionaryEqual(predicted.EnemyPowerInternalStates, actual.EnemyPowerInternalStates)
            || !DictionaryEqual(predicted.PlayerPileCards, actual.PlayerPileCards)
            || predicted.PlayerOrderedPiles != actual.PlayerOrderedPiles
            || !DictionaryEqual(predicted.PlayerPileCardDamageTotals, actual.PlayerPileCardDamageTotals)
            || !DictionaryEqual(predicted.PlayerPileCardDynamicVars, actual.PlayerPileCardDynamicVars)
            || !DictionaryEqual(predicted.PlayerCardStates, actual.PlayerCardStates)
            || !DictionaryEqual(predicted.PlayerCardCosts, actual.PlayerCardCosts)
            || !DictionaryEqual(predicted.PlayerCardEnchantments, actual.PlayerCardEnchantments)
            || !DictionaryEqual(predicted.PlayerCardEnchantmentStates, actual.PlayerCardEnchantmentStates)
            || !DictionaryEqual(predicted.PlayerCardUpgrades, actual.PlayerCardUpgrades)
            || !DictionaryEqual(predicted.EnemyHpsByModel, actual.EnemyHpsByModel)
            || !DictionaryEqual(predicted.EnemyBlocksByModel, actual.EnemyBlocksByModel)
            || !DictionaryEqual(predicted.AllEnemyPowers, actual.AllEnemyPowers)
            || predicted.MonsterAiState != actual.MonsterAiState
            || predicted.MonsterPrivateState != actual.MonsterPrivateState
            || predicted.PlayerPotions != actual.PlayerPotions
            || predicted.PlayerOrbState != actual.PlayerOrbState
            || !DictionaryEqual(predicted.PlayerOrbs, actual.PlayerOrbs)
            || !DictionaryEqual(predicted.RngCounters, actual.RngCounters)
            || predicted.ExactContinuationState != actual.ExactContinuationState)
        {
            string firstDifference = new ContinuationStamp(predicted.ExactContinuationState)
                .DescribeFirstDifference(new ContinuationStamp(actual.ExactContinuationState));
            Entry.Logger.Info(
                $"[CombatSolver/Unattended] MOVE_DIFF_MISMATCH run_id={_request.RunId} " +
                $"monster={monsterId} move={moveId} first_difference={firstDifference} " +
                $"predicted={Serialize(predicted)} actual={Serialize(actual)}");
            throw new InvalidOperationException(
                $"{monsterId}.{moveId} 一步模拟与真实行动不一致；首个差异：{firstDifference}");
        }
    }

    private static void AssertExpectedPowers(
        IReadOnlyDictionary<string, int> actual,
        IReadOnlyDictionary<string, int> expected,
        string target,
        string monsterId,
        string moveId)
    {
        foreach ((string powerId, int amount) in expected)
        {
            if (actual.GetValueOrDefault(powerId) != amount)
                throw new InvalidOperationException(
                    $"{monsterId}.{moveId} 的{target} Power {powerId}={actual.GetValueOrDefault(powerId)}，预期 {amount}。");
        }
    }

    private static void AssertAbsentPowers(
        IReadOnlyDictionary<string, int> actual,
        IReadOnlyList<string> expectedAbsent,
        string target,
        string monsterId,
        string moveId)
    {
        foreach (string powerId in expectedAbsent)
        {
            if (actual.ContainsKey(powerId))
                throw new InvalidOperationException(
                    $"{monsterId}.{moveId} 后{target}仍有 Power {powerId}={actual[powerId]}，预期已移除。");
        }
    }

    private static void AssertExpectedPiles(
        IReadOnlyDictionary<string, int> actual,
        IReadOnlyDictionary<string, int> expected,
        string monsterId,
        string moveId)
    {
        foreach ((string pileCard, int count) in expected)
        {
            if (actual.GetValueOrDefault(pileCard) != count)
                throw new InvalidOperationException(
                    $"{monsterId}.{moveId} 的玩家牌堆 {pileCard}={actual.GetValueOrDefault(pileCard)}，预期 {count}。");
        }
    }

    private static void AssertExpectedPileDamageTotals(
        IReadOnlyDictionary<string, int> actual,
        IReadOnlyDictionary<string, int> expected,
        string monsterId,
        string moveId)
    {
        foreach ((string pileCard, int damage) in expected)
        {
            if (actual.GetValueOrDefault(pileCard) != damage)
                throw new InvalidOperationException(
                    $"{monsterId}.{moveId} 的玩家牌堆 {pileCard} 伤害总和={actual.GetValueOrDefault(pileCard)}，预期 {damage}。");
        }
    }

    private static void AssertExpectedCardStates(
        IReadOnlyDictionary<string, int> actual,
        IReadOnlyDictionary<string, int> expected,
        string monsterId,
        string moveId)
    {
        foreach ((string cardState, int count) in expected)
        {
            if (actual.GetValueOrDefault(cardState) != count)
                throw new InvalidOperationException(
                    $"{monsterId}.{moveId} 的玩家卡牌状态 {cardState}={actual.GetValueOrDefault(cardState)}，预期 {count}；" +
                    $"实际状态=[{string.Join(",", actual.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}"))}]。");
        }
    }

    private static void AssertExpectedEnemyBlocks(
        IReadOnlyDictionary<string, int> actual,
        IReadOnlyDictionary<string, int> expected,
        string monsterId,
        string moveId)
    {
        foreach ((string modelId, int block) in expected)
        {
            if (actual.GetValueOrDefault(modelId) != block)
                throw new InvalidOperationException(
                    $"{monsterId}.{moveId} 后敌方 {modelId} 总格挡={actual.GetValueOrDefault(modelId)}，预期 {block}。");
        }
    }

    private static bool DictionaryEqual(
        IReadOnlyDictionary<string, int> left,
        IReadOnlyDictionary<string, int> right)
        => left.Count == right.Count && left.All(pair => right.GetValueOrDefault(pair.Key) == pair.Value);

    private static string Serialize(MoveStateSnapshot snapshot)
        => JsonSerializer.Serialize(snapshot, UnattendedTestFiles.JsonOptions);

    private sealed record MoveStateSnapshot(
        int PlayerHp,
        int PlayerMaxHp,
        int PlayerBlock,
        int OstyHp,
        int OstyMaxHp,
        bool OstyHittable,
        bool OstyInCombat,
        IReadOnlyDictionary<string, int> OstyPowers,
        int PlayerEnergy,
        int PlayerStars,
        int PlayerGold,
        int PlayerOrbCapacity,
        int EnemyHp,
        int EnemyMaxHp,
        int EnemyBlock,
        IReadOnlyDictionary<string, int> PlayerPowers,
        IReadOnlyDictionary<string, int> EnemyPowers,
        IReadOnlyDictionary<string, int> PlayerPowerStates,
        IReadOnlyDictionary<string, int> EnemyPowerStates,
        IReadOnlyDictionary<string, int> PlayerPowerInternalStates,
        IReadOnlyDictionary<string, int> EnemyPowerInternalStates,
        IReadOnlyDictionary<string, int> PlayerPileCards,
        string PlayerOrderedPiles,
        IReadOnlyDictionary<string, int> PlayerPileCardDamageTotals,
        IReadOnlyDictionary<string, int> PlayerPileCardDynamicVars,
        IReadOnlyDictionary<string, int> PlayerCardStates,
        IReadOnlyDictionary<string, int> PlayerCardCosts,
        IReadOnlyDictionary<string, int> PlayerCardEnchantments,
        IReadOnlyDictionary<string, int> PlayerCardEnchantmentStates,
        IReadOnlyDictionary<string, int> PlayerCardUpgrades,
        IReadOnlyDictionary<string, int> EnemyHpsByModel,
        IReadOnlyDictionary<string, int> EnemyBlocksByModel,
        IReadOnlyDictionary<string, int> AllEnemyPowers,
        string MonsterAiState,
        string MonsterPrivateState,
        string PlayerPotions,
        string PlayerOrbState,
        IReadOnlyDictionary<string, int> PlayerOrbs,
        IReadOnlyDictionary<string, int> RngCounters,
        string ExactContinuationState);
}
