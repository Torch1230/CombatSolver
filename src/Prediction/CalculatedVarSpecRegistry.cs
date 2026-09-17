using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

/// <summary>
/// Exact branch-local multipliers for vanilla calculated card variables. This replaces invocation
/// of delegates bound to the live combat graph, which is not valid after a prediction branch diverges.
/// </summary>
internal static class CalculatedVarSpecRegistry
{
    public static IReadOnlyCollection<Type> SupportedTypes { get; } =
    [
        typeof(PreciseCut), typeof(Stack), typeof(Squeeze), typeof(Mirage), typeof(Rattle), typeof(MindBlast),
        typeof(GangUp), typeof(Mimic), typeof(KnifeTrap), typeof(Unleash), typeof(Radiate), typeof(PerfectedStrike),
        typeof(SovereignBlade), typeof(Supermassive), typeof(Sacrifice), typeof(TimesUp), typeof(MementoMori),
        typeof(SoulStorm), typeof(Voltaic), typeof(TearAsunder), typeof(ExpectAFight), typeof(HelixDrill),
        typeof(PullFromBelow), typeof(Normality), typeof(Synchronize), typeof(Protector), typeof(NoEscape),
        typeof(Flechettes), typeof(Rend), typeof(GoldAxe), typeof(FlakCannon), typeof(LunarBlast), typeof(Murder),
        typeof(CompileDriver), typeof(Finisher), typeof(Bully), typeof(BeatIntoShape), typeof(DeathMarch),
        typeof(DemonicShield), typeof(Barrage), typeof(CrescentSpear), typeof(BodySlam), typeof(AshenStrike),
    ];

    public static IReadOnlyDictionary<Type, string> EvidenceByType { get; }
        = SupportedTypes.ToDictionary(type => type, type => type == typeof(Murder)
            ? "MURDER-ROOT-HISTORY"
            : "CALCULATED-CARD-BATCH-136");

    public static bool TryCalculate(
        CalculatedVar calculatedVar,
        CombatPredictionSimulator simulator,
        PredictedCard card,
        Creature? target,
        out decimal value)
    {
        string? key = null;
        // DynamicVarSet.GetEnumerator boxes this same dictionary's enumerator.
        foreach (KeyValuePair<string, DynamicVar> pair in card.Preview.DynamicVars._vars)
        {
            if (!ReferenceEquals(pair.Value, calculatedVar))
                continue;
            key = pair.Key;
            break;
        }
        if (string.IsNullOrEmpty(key)
            || !TryMultiplier(simulator, card, target, out decimal multiplier))
        {
            value = 0m;
            return false;
        }
        DynamicVar baseVar = card.Preview.DynamicVars.CalculationBase;
        DynamicVar extraVar = card.Preview.DynamicVars.TryGetValue("CalculationExtra", out DynamicVar? extra)
            ? extra
            : card.Preview.DynamicVars.ExtraDamage;
        value = baseVar.BaseValue + extraVar.BaseValue * multiplier;
        return true;
    }

    private static bool TryMultiplier(
        CombatPredictionSimulator simulator,
        PredictedCard card,
        Creature? target,
        out decimal multiplier)
    {
        CardModel model = card.Preview;
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(model.Owner);
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        Creature owner = card.Preview.Owner.Creature;
        multiplier = model switch
        {
            PreciseCut => -playerState.Hand.Cards.Count,
            Stack => playerState.DiscardPile.Cards.Count,
            Squeeze => CountOstyAttacks(playerState, model),
            Mirage => SumLivingEnemyPoison(simulator, combat),
            Rattle => 1 + (simulator.State.GetOsty(model.Owner) is { } osty
                ? combat.GetCreatureAttacksThisTurn(osty)
                : 0),
            MindBlast => playerState.DrawPile.Cards.Count,
            GangUp => target == null ? 0 : SumAlliedAttackHits(combat, owner, target),
            Mimic => target == null ? 0 : simulator.State.GetCreature(target).Block,
            KnifeTrap => CountExhaustedShivs(playerState),
            Unleash => simulator.State.GetOsty(model.Owner) is { } unleashOsty
                && simulator.State.GetCreature(unleashOsty).IsAlive
                ? simulator.State.GetCreature(unleashOsty).CurrentHp
                : 0,
            Radiate => combat.GetStarsGainedThisTurn(model.Owner),
            PerfectedStrike => CountStrikes(playerState),
            SovereignBlade => combat.GetAmount<ParryPower>(owner),
            Supermassive => CountGeneratedCards(simulator, model.Owner),
            Sacrifice => simulator.State.GetOsty(model.Owner) is { } sacrificeOsty
                && simulator.State.GetCreature(sacrificeOsty).IsAlive
                ? combat.GetOstyMaxHp(simulator, model.Owner) * 3
                : 0,
            TimesUp => target == null ? 0 : combat.GetAmount<DoomPower>(target),
            MementoMori => combat.GetCardsDiscardedThisTurn(owner),
            SoulStorm => CountExhaustedSouls(playerState),
            Voltaic => CountLightningChannels(simulator, model.Owner),
            TearAsunder => 1 + CountUnblockedDamageEvents(simulator, owner),
            ExpectAFight => Math.Max(0, combat.GetAmount<StrengthPower>(owner)),
            HelixDrill => Math.Max(0, combat.GetEnergySpentThisTurn(model.Owner)
                - card.GetEnergyCostWithModifiers(simulator, playerState)),
            PullFromBelow => CountEtherealPlays(simulator, model.Owner),
            Normality => Math.Min(3, combat.GetCardPlayStartsThisTurn(owner)),
            Synchronize or CompileDriver => CountDistinctOrbs(playerState.OrbQueue.Orbs),
            Protector => simulator.State.GetOsty(model.Owner) is { } protectorOsty
                && simulator.State.GetCreature(protectorOsty).IsAlive
                ? combat.GetOstyMaxHp(simulator, model.Owner)
                : 0,
            NoEscape => target == null
                ? 0
                : Math.Floor((decimal)combat.GetAmount<DoomPower>(target)
                    / model.DynamicVars["DoomThreshold"].BaseValue),
            Flechettes => CountHandSkills(playerState),
            Rend => target == null ? 0 : CountPermanentDebuffs(combat, target),
            GoldAxe => CountFinishedCardPlays(simulator),
            FlakCannon => CountUnexhaustedStatuses(simulator, playerState),
            LunarBlast => combat.GetSkillCardsPlayedThisTurn(owner),
            Murder => CountDrawnCards(simulator, model.Owner),
            Finisher => combat.GetAttacksPlayedThisTurn(owner),
            Bully => target == null ? 0 : combat.GetAmount<VulnerablePower>(target),
            BeatIntoShape => target == null ? 0 : combat.GetPoweredAttackHitsThisTurn(owner, target),
            DeathMarch => combat.GetNonHandDrawsThisTurn(model.Owner),
            DemonicShield or BodySlam => simulator.State.GetCreature(owner).Block,
            Barrage => playerState.OrbQueue.Orbs.Count,
            CrescentSpear => CountStarCards(playerState),
            AshenStrike => playerState.ExhaustPile.Cards.Count,
            _ => 0m,
        };
        return model is PreciseCut or Stack or Squeeze or Mirage or Rattle or MindBlast or GangUp
            or Mimic or KnifeTrap or Unleash or Radiate or PerfectedStrike or SovereignBlade
            or Supermassive or Sacrifice or TimesUp or MementoMori or SoulStorm or Voltaic
            or TearAsunder or ExpectAFight or HelixDrill or PullFromBelow or Normality
            or Synchronize or Protector or NoEscape or Flechettes or Rend or GoldAxe or FlakCannon
            or LunarBlast or Murder or CompileDriver or Finisher or Bully or BeatIntoShape
            or DeathMarch or DemonicShield or Barrage or CrescentSpear or BodySlam or AshenStrike;
    }

    // Count(predicate) and Sum(int) use checked int arithmetic, before conversion to decimal.
    private static int CountOstyAttacks(SimPlayerCombatState playerState, CardModel model)
    {
        int count = 0;
        foreach (PredictedCard candidate in playerState.AllCards)
            if (candidate.Preview.Tags.Contains(CardTag.OstyAttack) && !candidate.References(model))
                count = checked(count + 1);
        return count;
    }

    private static int SumLivingEnemyPoison(CombatPredictionSimulator simulator, SimulatedCombatState combat)
    {
        int sum = 0;
        IReadOnlyList<Creature> enemies = combat.Enemies;
        for (int i = 0; i < enemies.Count; i++)
        {
            Creature enemy = enemies[i];
            if (simulator.State.GetCreature(enemy).IsAlive)
                sum = checked(sum + combat.GetAmount<PoisonPower>(enemy));
        }
        return sum;
    }

    private static int SumAlliedAttackHits(SimulatedCombatState combat, Creature owner, Creature target)
    {
        int sum = 0;
        IReadOnlyList<Creature> creatures = combat.Creatures;
        for (int i = 0; i < creatures.Count; i++)
        {
            Creature creature = creatures[i];
            if (creature != owner && creature.Side == owner.Side)
                sum = checked(sum + combat.GetPoweredAttackHitsThisTurn(creature, target));
        }
        return sum;
    }

    private static int CountExhaustedShivs(SimPlayerCombatState playerState)
    {
        int count = 0;
        foreach (PredictedCard candidate in playerState.ExhaustPile.Cards)
            if (candidate.Preview.Tags.Contains(CardTag.Shiv))
                count = checked(count + 1);
        return count;
    }

    private static int CountStrikes(SimPlayerCombatState playerState)
    {
        int count = 0;
        foreach (PredictedCard candidate in playerState.AllCards)
            if (candidate.Preview.Tags.Contains(CardTag.Strike))
                count = checked(count + 1);
        return count;
    }

    private static int CountExhaustedSouls(SimPlayerCombatState playerState)
    {
        int count = 0;
        foreach (PredictedCard candidate in playerState.ExhaustPile.Cards)
            if (candidate.Preview is Soul)
                count = checked(count + 1);
        return count;
    }

    private static int CountDistinctOrbs(IReadOnlyList<OrbModel> orbs)
    {
        int count = 0;
        EqualityComparer<ModelId> comparer = EqualityComparer<ModelId>.Default;
        // Id is immutable; scan the preceding small orb queue instead of allocating a HashSet.
        for (int i = 0; i < orbs.Count; i++)
        {
            ModelId id = orbs[i].Id;
            bool seen = false;
            for (int j = 0; j < i; j++)
            {
                if (!comparer.Equals(orbs[j].Id, id))
                    continue;
                seen = true;
                break;
            }
            if (!seen)
                count = checked(count + 1);
        }
        return count;
    }

    private static int CountHandSkills(SimPlayerCombatState playerState)
    {
        int count = 0;
        foreach (PredictedCard candidate in playerState.Hand.Cards)
            if (candidate.Preview.Type == CardType.Skill)
                count = checked(count + 1);
        return count;
    }

    private static int CountPermanentDebuffs(SimulatedCombatState combat, Creature target)
    {
        int count = 0;
        IReadOnlyList<PowerModel> powers = combat.EffectivePowers();
        for (int i = 0; i < powers.Count; i++)
        {
            PowerModel power = powers[i];
            if (power.Owner == target
                && power.TypeForCurrentAmount == PowerType.Debuff
                && power is not ITemporaryPower)
                count = checked(count + 1);
        }
        return count;
    }

    private static int CountUnexhaustedStatuses(CombatPredictionSimulator simulator, SimPlayerCombatState playerState)
    {
        int count = 0;
        foreach (PredictedCard candidate in playerState.AllCards)
            if (candidate.Preview.Type == CardType.Status
                && candidate.GetPile(simulator.State)?.Type != PileType.Exhaust)
                count = checked(count + 1);
        return count;
    }

    private static int CountStarCards(SimPlayerCombatState playerState)
    {
        int count = 0;
        foreach (PredictedCard candidate in playerState.AllCards)
            if (candidate.Preview.CanonicalStarCost >= 0 || candidate.Preview.HasStarCostX)
                count = checked(count + 1);
        return count;
    }

    // The live Entries property exposes this list; CardPlaysFinished is its OfType view.
    // Keep the two checked counts separate: their final addition was unchecked in the original.
    private static int CountGeneratedCards(CombatPredictionSimulator simulator, Player player)
    {
        int live = 0;
        foreach (var item in CombatManager.Instance.History._entries)
            if (item is CardGeneratedEntry entry && entry.Creator == player)
                live = checked(live + 1);
        int predicted = 0;
        foreach (var item in simulator.History.EntriesFrom(0))
            if (item is CombatPredictionCardGeneratedEntry entry && entry.Creator == player)
                predicted = checked(predicted + 1);
        return live + predicted;
    }

    private static int CountLightningChannels(CombatPredictionSimulator simulator, Player player)
    {
        int live = 0;
        foreach (var item in CombatManager.Instance.History._entries)
            if (item is OrbChanneledEntry entry && entry.Actor.Player == player && entry.Orb is LightningOrb)
                live = checked(live + 1);
        int predicted = 0;
        foreach (var item in simulator.History.EntriesFrom(0))
            if (item is CombatPredictionOrbChanneledEntry entry && entry.Orb is LightningOrb && entry.Orb.Owner == player)
                predicted = checked(predicted + 1);
        return live + predicted;
    }

    private static int CountUnblockedDamageEvents(CombatPredictionSimulator simulator, Creature owner)
    {
        int live = 0;
        foreach (var item in CombatManager.Instance.History._entries)
            if (item is DamageReceivedEntry entry && entry.Receiver == owner && entry.Result.UnblockedDamage > 0)
                live = checked(live + 1);
        int predicted = 0;
        foreach (var item in simulator.History.EntriesFrom(0))
            if (item is CombatPredictionDamageReceivedEntry entry && entry.Receiver == owner && entry.Result.UnblockedDamage > 0)
                predicted = checked(predicted + 1);
        return live + predicted;
    }

    private static int CountEtherealPlays(CombatPredictionSimulator simulator, Player player)
    {
        int live = 0;
        foreach (var item in CombatManager.Instance.History._entries)
            if (item is CardPlayFinishedEntry entry && entry.CardPlay.Player == player && entry.WasEthereal)
                live = checked(live + 1);
        int predicted = 0;
        foreach (var item in simulator.History.EntriesFrom(0))
            if (item is CombatPredictionCardPlayFinishedEntry entry && entry.CardPlay.Player == player && entry.WasEthereal)
                predicted = checked(predicted + 1);
        return live + predicted;
    }

    private static int CountFinishedCardPlays(CombatPredictionSimulator simulator)
    {
        int live = 0;
        foreach (var item in CombatManager.Instance.History._entries)
            if (item is CardPlayFinishedEntry)
                live = checked(live + 1);
        int predicted = 0;
        foreach (var item in simulator.History.EntriesFrom(0))
            if (item is CombatPredictionCardPlayFinishedEntry)
                predicted = checked(predicted + 1);
        return live + predicted;
    }

    private static int CountDrawnCards(CombatPredictionSimulator simulator, Player player)
    {
        int live = ((SimulatedCombatState)simulator.State.CombatState).GetCardsDrawnBeforePrediction(player);
        int predicted = 0;
        foreach (var item in simulator.History.EntriesFrom(0))
            if (item is CombatPredictionCardDrawnEntry entry && entry.Card.Owner == player)
                predicted = checked(predicted + 1);
        return live + predicted;
    }
}
