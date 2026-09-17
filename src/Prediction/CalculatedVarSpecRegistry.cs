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
        string key = card.Preview.DynamicVars
            .FirstOrDefault(pair => ReferenceEquals(pair.Value, calculatedVar)).Key;
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
            Squeeze => playerState.AllCards.Count(candidate =>
                candidate.Preview.Tags.Contains(CardTag.OstyAttack) && !candidate.References(model)),
            Mirage => combat.Enemies
                .Where(enemy => simulator.State.GetCreature(enemy).IsAlive)
                .Sum(enemy => combat.GetAmount<PoisonPower>(enemy)),
            Rattle => 1 + (simulator.State.GetOsty(model.Owner) is { } osty
                ? combat.GetCreatureAttacksThisTurn(osty)
                : 0),
            MindBlast => playerState.DrawPile.Cards.Count,
            GangUp => target == null ? 0 : combat.Creatures
                .Where(creature => creature != owner && creature.Side == owner.Side)
                .Sum(creature => combat.GetPoweredAttackHitsThisTurn(creature, target)),
            Mimic => target == null ? 0 : simulator.State.GetCreature(target).Block,
            KnifeTrap => playerState.ExhaustPile.Cards.Count(candidate => candidate.Preview.Tags.Contains(CardTag.Shiv)),
            Unleash => simulator.State.GetOsty(model.Owner) is { } unleashOsty
                && simulator.State.GetCreature(unleashOsty).IsAlive
                ? simulator.State.GetCreature(unleashOsty).CurrentHp
                : 0,
            Radiate => combat.GetStarsGainedThisTurn(model.Owner),
            PerfectedStrike => playerState.AllCards.Count(candidate => candidate.Preview.Tags.Contains(CardTag.Strike)),
            SovereignBlade => combat.GetAmount<ParryPower>(owner),
            Supermassive => CountGeneratedCards(simulator, model.Owner),
            Sacrifice => simulator.State.GetOsty(model.Owner) is { } sacrificeOsty
                && simulator.State.GetCreature(sacrificeOsty).IsAlive
                ? combat.GetOstyMaxHp(simulator, model.Owner) * 3
                : 0,
            TimesUp => target == null ? 0 : combat.GetAmount<DoomPower>(target),
            MementoMori => combat.GetCardsDiscardedThisTurn(owner),
            SoulStorm => playerState.ExhaustPile.Cards.Count(candidate => candidate.Preview is Soul),
            Voltaic => CountLightningChannels(simulator, model.Owner),
            TearAsunder => 1 + CountUnblockedDamageEvents(simulator, owner),
            ExpectAFight => Math.Max(0, combat.GetAmount<StrengthPower>(owner)),
            HelixDrill => Math.Max(0, combat.GetEnergySpentThisTurn(model.Owner)
                - card.GetEnergyCostWithModifiers(simulator, playerState)),
            PullFromBelow => CountEtherealPlays(simulator, model.Owner),
            Normality => Math.Min(3, combat.GetCardPlayStartsThisTurn(owner)),
            Synchronize or CompileDriver => playerState.OrbQueue.Orbs.Select(orb => orb.Id).Distinct().Count(),
            Protector => simulator.State.GetOsty(model.Owner) is { } protectorOsty
                && simulator.State.GetCreature(protectorOsty).IsAlive
                ? combat.GetOstyMaxHp(simulator, model.Owner)
                : 0,
            NoEscape => target == null
                ? 0
                : Math.Floor((decimal)combat.GetAmount<DoomPower>(target)
                    / model.DynamicVars["DoomThreshold"].BaseValue),
            Flechettes => playerState.Hand.Cards.Count(candidate => candidate.Preview.Type == CardType.Skill),
            Rend => target == null ? 0 : combat.EffectivePowers().Count(power =>
                power.Owner == target
                && power.TypeForCurrentAmount == PowerType.Debuff
                && power is not ITemporaryPower),
            GoldAxe => CountFinishedCardPlays(simulator),
            FlakCannon => playerState.AllCards.Count(candidate =>
                candidate.Preview.Type == CardType.Status
                && candidate.GetPile(simulator.State)?.Type != PileType.Exhaust),
            LunarBlast => combat.GetSkillCardsPlayedThisTurn(owner),
            Murder => CountDrawnCards(simulator, model.Owner),
            Finisher => combat.GetAttacksPlayedThisTurn(owner),
            Bully => target == null ? 0 : combat.GetAmount<VulnerablePower>(target),
            BeatIntoShape => target == null ? 0 : combat.GetPoweredAttackHitsThisTurn(owner, target),
            DeathMarch => combat.GetNonHandDrawsThisTurn(model.Owner),
            DemonicShield or BodySlam => simulator.State.GetCreature(owner).Block,
            Barrage => playerState.OrbQueue.Orbs.Count,
            CrescentSpear => playerState.AllCards.Count(candidate =>
                candidate.Preview.CanonicalStarCost >= 0 || candidate.Preview.HasStarCostX),
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

    private static int CountGeneratedCards(CombatPredictionSimulator simulator, Player player)
        => CombatManager.Instance.History.Entries.OfType<CardGeneratedEntry>().Count(entry => entry.Creator == player)
           + simulator.History.OfType<CombatPredictionCardGeneratedEntry>().Count(entry => entry.Creator == player);

    private static int CountLightningChannels(CombatPredictionSimulator simulator, Player player)
        => CombatManager.Instance.History.Entries.OfType<OrbChanneledEntry>()
               .Count(entry => entry.Actor.Player == player && entry.Orb is LightningOrb)
           + simulator.History.OfType<CombatPredictionOrbChanneledEntry>()
               .Count(entry => entry.Orb is LightningOrb && entry.Orb.Owner == player);

    private static int CountUnblockedDamageEvents(CombatPredictionSimulator simulator, Creature owner)
        => CombatManager.Instance.History.Entries.OfType<DamageReceivedEntry>()
               .Count(entry => entry.Receiver == owner && entry.Result.UnblockedDamage > 0)
           + simulator.History.OfType<CombatPredictionDamageReceivedEntry>()
               .Count(entry => entry.Receiver == owner && entry.Result.UnblockedDamage > 0);

    private static int CountEtherealPlays(CombatPredictionSimulator simulator, Player player)
        => CombatManager.Instance.History.CardPlaysFinished.Count(entry =>
               entry.CardPlay.Player == player && entry.WasEthereal)
           + simulator.History.OfType<CombatPredictionCardPlayFinishedEntry>()
               .Count(entry => entry.CardPlay.Player == player && entry.WasEthereal);

    private static int CountFinishedCardPlays(CombatPredictionSimulator simulator)
        => CombatManager.Instance.History.CardPlaysFinished.Count()
           + simulator.History.OfType<CombatPredictionCardPlayFinishedEntry>().Count();

    private static int CountDrawnCards(CombatPredictionSimulator simulator, Player player)
        => ((SimulatedCombatState)simulator.State.CombatState).GetCardsDrawnBeforePrediction(player)
           + simulator.History.OfType<CombatPredictionCardDrawnEntry>()
               .Count(entry => entry.Card.Owner == player);
}
