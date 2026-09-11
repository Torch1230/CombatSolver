using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Enchantments;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.ValueProps;

namespace CombatSolver;

// Exact native card admission and immutable instruction compilation.
internal static class CompactCardProgramCompiler
{
    private static readonly HashSet<Type> AdmittedTypes =
    [
        typeof(Acrobatics), typeof(Prepared), typeof(Backflip), typeof(StrikeSilent), typeof(StrikeNecrobinder),
        typeof(DefendSilent), typeof(DefendNecrobinder), typeof(Neutralize), typeof(Survivor), typeof(Finesse),
        typeof(UltimateDefend), typeof(Suppress), typeof(Footwork), typeof(Malaise), typeof(DeadlyPoison),
        typeof(Haze), typeof(Snakebite), typeof(Defy), typeof(EscapePlan), typeof(Outbreak), typeof(CalculatedGamble),
        typeof(BubbleBubble), typeof(Mirage), typeof(DodgeAndRoll), typeof(ToolsOfTheTrade), typeof(PiercingWail),
        typeof(CloakAndDagger), typeof(Shiv), typeof(BladeOfInk), typeof(Burn), typeof(Neurosurge), typeof(Bodyguard), typeof(Unleash), typeof(Afterlife), typeof(Cleanse), typeof(Dirge), typeof(Soul), typeof(CaptureSpirit), typeof(Graveblast), typeof(Defile), typeof(Wisp), typeof(AscendersBane), typeof(BorrowedTime), typeof(Veilpiercer), typeof(Hang), typeof(SculptingStrike), typeof(Snap)
    ];

    internal static ResumableDiscardProgram.Card Compile(CardModel card, bool includeAttacks, int shivTemplate = -1, int inkyShivTemplate = -1, int soulTemplate = -1, int upgradedSoulTemplate = -1)
    {
        if (!AdmittedTypes.Contains(card.GetType())
            || card is Neutralize or Suppress or Footwork or Malaise or DeadlyPoison or Haze or Snakebite or Defy or Outbreak or BubbleBubble or Mirage or DodgeAndRoll or ToolsOfTheTrade or PiercingWail or CloakAndDagger or Shiv or BladeOfInk or Burn or Neurosurge or Bodyguard or Unleash or Afterlife or Cleanse or Dirge or Soul or CaptureSpirit or Graveblast or Defile or Wisp or AscendersBane or BorrowedTime or Veilpiercer or Hang or SculptingStrike or Snap && !includeAttacks
            || card is Burn && (card.Enchantment != null || card.EnergyCost._base != -1 || card.IsUpgraded
                || !card.LocalKeywords.Contains(CardKeyword.Unplayable) || card.DynamicVars.Damage.Props != (ValueProp.Unpowered | ValueProp.Move))
            || card is AscendersBane && (card.Enchantment != null || card.EnergyCost._base != -1 || card.IsUpgraded
                || !card.LocalKeywords.Contains(CardKeyword.Unplayable))
            || card is CaptureSpirit && card.DynamicVars.Damage.Props != (ValueProp.Unblockable | ValueProp.Unpowered | ValueProp.Move)
            || card.Enchantment is { } enchantment && enchantment.GetType() != typeof(Inky) && enchantment.GetType() != typeof(Slither)
            || card.Enchantment != null && !(card is Shiv && card.Enchantment is Inky { Amount: 1, Status: EnchantmentStatus.Normal })
                && card.Enchantment is not Slither { Amount: 1, Status: EnchantmentStatus.Normal, TestEnergyCostOverride: -1 }
            || card.Affliction != null || card.BaseReplayCount != 0
            || card.ExhaustOnNextPlay || card.IsDupe || card.IsClone || card.HasBeenRemovedFromState
            || card.EnergyCost.CostsX && (card is not (Malaise or Dirge) || card.Enchantment is Slither)
            || card.EnergyCost._localModifiers.Count != 0 && (card.Enchantment is not Slither
                || card.EnergyCost._localModifiers.Any(modifier => modifier.GetType() != typeof(LocalCostModifier)
                    || modifier.Type != LocalCostType.Absolute || modifier.Expiration != LocalCostModifierExpiration.EndOfCombat
                    || modifier.IsReduceOnly || modifier.Amount is < 0 or > 3))
            || card.HasStarCostX || card.CurrentStarCost > 0 || card._temporaryStarCosts.Count != 0
            || card.CurrentTarget != null || card.CurrentPlayIndex != 0 || card.LastStarsSpent != 0
            || card.HasSingleTurnRetain || card.HasTurnEndInHandEffect && card is not (Burn or AscendersBane)
            || card.LocalKeywords.Any(k => k is not (CardKeyword.Sly or CardKeyword.Ethereal or CardKeyword.Retain) && !(card is Malaise or CalculatedGamble or Mirage or PiercingWail or Shiv or Afterlife or Dirge or Soul or Graveblast or Wisp && k == CardKeyword.Exhaust)
                && !(card is Suppress && k == CardKeyword.Innate)
                && !(card is Burn or AscendersBane && k == CardKeyword.Unplayable) && !(card is AscendersBane && k == CardKeyword.Eternal))
            || card.IsSlyThisTurn && card is not Prepared)
            throw new NotSupportedException($"Compact prototype cannot admit card state {card.Id.Entry}.");
        decimal draw = card is EscapePlan ? 1 : card is Acrobatics or Prepared or Backflip or Finesse or Neurosurge or Soul ? card.DynamicVars.Cards.BaseValue : 0;
        decimal energyGain = card is Neurosurge or Wisp or BorrowedTime ? card.DynamicVars.Energy.BaseValue : 0;
        decimal extraCost = card is BorrowedTime ? card.DynamicVars["ExtraCost"].BaseValue : 0;
        decimal neurosurge = card is Neurosurge ? card.DynamicVars["NeurosurgePower"].BaseValue : 0;
        decimal damage = card is Snap ? card.DynamicVars.OstyDamage.BaseValue : includeAttacks && card is StrikeSilent or StrikeNecrobinder or Neutralize or Suppress or Shiv or Burn or CaptureSpirit or Graveblast or Defile or Veilpiercer or Hang or SculptingStrike ? card.DynamicVars.Damage.BaseValue : 0;
        decimal block = card is DefendSilent or DefendNecrobinder or Backflip or Survivor or Finesse or UltimateDefend or Defy or EscapePlan or DodgeAndRoll or CloakAndDagger ? card.DynamicVars.Block.BaseValue : 0;
        decimal weak = card.Enchantment is Inky inky ? inky.DynamicVars.Weak.BaseValue
            : card is Neutralize or Suppress or Haze or Defy ? card.DynamicVars.Weak.BaseValue : 0;
        decimal poison = card is DeadlyPoison or Haze or Snakebite or Outbreak or BubbleBubble ? card.DynamicVars.Poison.BaseValue : 0;
        decimal generated = card is CloakAndDagger or BladeOfInk or CaptureSpirit ? card.DynamicVars.Cards.BaseValue : 0;
        decimal strengthLoss = card is PiercingWail ? card.DynamicVars["StrengthLoss"].BaseValue : 0;
        decimal dexterity = card is Footwork ? card.DynamicVars.Dexterity.BaseValue : 0;
        decimal summon = card is Bodyguard or Afterlife or Cleanse or Dirge ? card.DynamicVars.Summon.BaseValue : 0;
        decimal calculationBase = card is Mirage or Unleash ? card.DynamicVars.CalculationBase.BaseValue : 0;
        decimal calculationExtra = card is Mirage ? card.DynamicVars.CalculationExtra.BaseValue : card is Unleash ? card.DynamicVars.ExtraDamage.BaseValue : 0;
        if (draw != decimal.Truncate(draw) || draw < 0 || draw > 10 || card.EnergyCost._base < 0 && card is not (Burn or AscendersBane)
            || card is Acrobatics or Prepared or Backflip or Finesse && draw == 0
            || damage != decimal.Truncate(damage) || damage is < 0 or > 999_999_999m
            || block != decimal.Truncate(block) || block is < 0 or > 999_999_999m
            || weak != decimal.Truncate(weak) || weak is < 0 or > 999_999_999m
            || poison != decimal.Truncate(poison) || poison is < 0 or > 999_999_999m
            || generated != decimal.Truncate(generated) || generated is < 0 or > 999_999_999m
            || energyGain != decimal.Truncate(energyGain) || energyGain is < 0 or > 999_999_999m
            || extraCost != decimal.Truncate(extraCost) || extraCost is < 0 or > 999_999_999m
            || neurosurge != decimal.Truncate(neurosurge) || neurosurge is < 0 or > 999_999_999m
            || strengthLoss != decimal.Truncate(strengthLoss) || strengthLoss is < 0 or > 999_999_999m
            || dexterity != decimal.Truncate(dexterity) || dexterity is < 0 or > 999_999_999m
            || summon != decimal.Truncate(summon) || summon is < 0 or > 999_999_999m
            || calculationBase != decimal.Truncate(calculationBase) || calculationBase is < 0 or > 999_999_999m
            || calculationExtra != decimal.Truncate(calculationExtra) || calculationExtra is < 0 or > 999_999_999m)
            throw new NotSupportedException($"Compact prototype cannot admit card variables {card.Id.Entry}.");
        // These are ordered native commands, including meaningful zero-base effects. The
        // adapter admits exact types/instance state; the executor contains no card identities.
        CardEffectProgram effects = card switch
        {
            Burn or AscendersBane => CardEffectProgram.Empty,
            Bodyguard or Afterlife => new([new(CardInstructionKind.SummonPet, (int)summon)]),
            Cleanse => new([new(CardInstructionKind.SummonPet, (int)summon), new(CardInstructionKind.ExhaustFromDraw, 1)]),
            Dirge => new([new(CardInstructionKind.SummonPet, (int)summon, RepeatForEnergyX: true),
                new(CardInstructionKind.GenerateCards, 0, EnergyXMultiplier: 1,
                    CardTemplate: card.IsUpgraded ? upgradedSoulTemplate : soulTemplate, Placement: CardGenerationPlacement.RandomDraw)]),
            Soul => new([new(CardInstructionKind.Draw, (int)draw)]),
            BorrowedTime => new([new(CardInstructionKind.GainEnergy, (int)energyGain),
                new(CardInstructionKind.ApplyBasicPower, (int)extraCost, BasicPowerKind.BorrowedTime)]),
            SculptingStrike => new([new(CardInstructionKind.AttackTarget, (int)damage),
                new(CardInstructionKind.ApplyKeywordFromHand, 1, Keyword: CardKeywordFlags.Ethereal)]),
            Snap => new([new(CardInstructionKind.PetAttackTarget, (int)damage),
                new(CardInstructionKind.ApplyKeywordFromHand, 1, Keyword: CardKeywordFlags.Retain)]),
            Hang => new([new(CardInstructionKind.AttackTarget, (int)damage, AttackMultiplierPower: BasicPowerKind.Hang),
                new(CardInstructionKind.ApplyPowerAtLeastCurrent, 2, BasicPowerKind.Hang, CardInstructionTarget.ChosenEnemy)]),
            Veilpiercer => new([new(CardInstructionKind.AttackTarget, (int)damage),
                new(CardInstructionKind.ApplyBasicPower, 1, BasicPowerKind.Veilpiercer)]),
            Wisp => new([new(CardInstructionKind.GainEnergy, (int)energyGain)]),
            CaptureSpirit => new([new(CardInstructionKind.LoseEnemyHp, (int)damage),
                new(CardInstructionKind.GenerateCards, (int)generated, CardTemplate: soulTemplate, Placement: CardGenerationPlacement.RandomDraw)]),
            Graveblast => new([new(CardInstructionKind.AttackTarget, (int)damage), new(CardInstructionKind.RetrieveFromDiscard, 1)]),
            Defile => new([new(CardInstructionKind.AttackTarget, (int)damage)]),
            Unleash => new([new(CardInstructionKind.PetAttackTarget, (int)calculationBase, Multiplier: (int)calculationExtra)]),
            Acrobatics => new([new(CardInstructionKind.Draw, (int)draw), new(CardInstructionKind.Discard, 1)]),
            Prepared => new([new(CardInstructionKind.Draw, (int)draw), new(CardInstructionKind.Discard, (int)draw)]),
            Backflip or Finesse => new([new(CardInstructionKind.GainBlock, (int)block), new(CardInstructionKind.Draw, (int)draw)]),
            Survivor => new([new(CardInstructionKind.GainBlock, (int)block), new(CardInstructionKind.Discard, 1)]),
            DefendSilent or DefendNecrobinder or UltimateDefend => new([new(CardInstructionKind.GainBlock, (int)block)]),
            Shiv when card.Enchantment is Inky => new([new(CardInstructionKind.AttackTarget, (int)damage),
                new(CardInstructionKind.ApplyBasicPower, (int)weak, BasicPowerKind.Weak, CardInstructionTarget.ChosenEnemy)]),
            Shiv => new([new(CardInstructionKind.AttackTarget, (int)damage)]),
            CloakAndDagger => new([new(CardInstructionKind.GainBlock, (int)block), new(CardInstructionKind.GenerateCards, (int)generated, CardTemplate: shivTemplate)]),
            BladeOfInk => new([new(CardInstructionKind.GenerateCards, (int)generated, CardTemplate: inkyShivTemplate)]),
            StrikeSilent or StrikeNecrobinder when includeAttacks => new([new(CardInstructionKind.AttackTarget, (int)damage)]),
            StrikeSilent or StrikeNecrobinder => CardEffectProgram.Empty, // Inert metadata in the draw/discard-only fixture.
            Neutralize or Suppress => new([new(CardInstructionKind.AttackTarget, (int)damage),
                new(CardInstructionKind.ApplyBasicPower, (int)weak, BasicPowerKind.Weak, CardInstructionTarget.ChosenEnemy)]),
            PiercingWail => new([new(CardInstructionKind.ApplyTemporaryStrengthLoss, (int)strengthLoss, BasicPowerKind.PiercingWail, CardInstructionTarget.AllEnemies)]),
            Footwork => new([new(CardInstructionKind.ApplyBasicPower, (int)dexterity, BasicPowerKind.Dexterity)]),
            ToolsOfTheTrade => new([new(CardInstructionKind.ApplyBasicPower, 1, BasicPowerKind.ToolsOfTheTrade)]),
            Neurosurge => new([new(CardInstructionKind.GainEnergy, (int)energyGain), new(CardInstructionKind.Draw, (int)draw),
                new(CardInstructionKind.ApplyBasicPower, (int)neurosurge, BasicPowerKind.Neurosurge)]),
            DodgeAndRoll => new([new(CardInstructionKind.GainBlockAndApplyPower, (int)block, BasicPowerKind.BlockNextTurn)]),
            DeadlyPoison or Snakebite => new([new(CardInstructionKind.ApplyBasicPower, (int)poison, BasicPowerKind.Poison, CardInstructionTarget.ChosenEnemy)]),
            Haze => new([new(CardInstructionKind.ApplyBasicPower, (int)poison, BasicPowerKind.Poison, CardInstructionTarget.AllEnemies),
                new(CardInstructionKind.ApplyBasicPower, (int)weak, BasicPowerKind.Weak, CardInstructionTarget.AllEnemies)]),
            Defy => new([new(CardInstructionKind.GainBlock, (int)block),
                new(CardInstructionKind.ApplyBasicPower, (int)weak, BasicPowerKind.Weak, CardInstructionTarget.ChosenEnemy)]),
            EscapePlan => new([new(CardInstructionKind.Draw, 1), new(CardInstructionKind.SkipIfDrawnCardNotType, 1),
                new(CardInstructionKind.GainBlock, (int)block)]),
            Outbreak => new([new(CardInstructionKind.ApplyBasicPower, (int)poison, BasicPowerKind.Poison, CardInstructionTarget.AllEnemies),
                new(CardInstructionKind.TriggerBasicPower, 0, BasicPowerKind.Poison, CardInstructionTarget.AllEnemies)]),
            CalculatedGamble => new([new(CardInstructionKind.DiscardHandAndDraw, 0)]),
            BubbleBubble => new([new(CardInstructionKind.SkipIfTargetLacksPower, 1, BasicPowerKind.Poison, CardInstructionTarget.ChosenEnemy),
                new(CardInstructionKind.ApplyBasicPower, (int)poison, BasicPowerKind.Poison, CardInstructionTarget.ChosenEnemy)]),
            Mirage => new([new(CardInstructionKind.GainBlockFromPowerSum, (int)calculationBase, BasicPowerKind.Poison,
                CardInstructionTarget.AllEnemies, Multiplier: (int)calculationExtra)]),
            Malaise => new([new(CardInstructionKind.ApplyBasicPower, card.IsUpgraded ? -1 : 0, BasicPowerKind.Strength,
                    CardInstructionTarget.ChosenEnemy, -1),
                new(CardInstructionKind.ApplyBasicPower, card.IsUpgraded ? 1 : 0, BasicPowerKind.Weak, CardInstructionTarget.ChosenEnemy, 1)]),
            _ => throw new NotSupportedException("Card has no admitted compact instructions.")
        };
        return new(card.EnergyCost._base, effects, card.LocalKeywords.Contains(CardKeyword.Sly),
            card.Type == CardType.Power ? ResumableDiscardProgram.Pile.Removed : card.LocalKeywords.Contains(CardKeyword.Exhaust) ? ResumableDiscardProgram.Pile.Exhaust : ResumableDiscardProgram.Pile.Discard,
            card.EnergyCost.CostsX, card.EnergyCost.CostsX ? card.EnergyCost.CapturedXValue : 0,
            card.Type switch { CardType.Attack => CardCategory.Attack, CardType.Skill => CardCategory.Skill,
                CardType.Power => CardCategory.Power, CardType.Status => CardCategory.Status, _ => CardCategory.Other }, card.LocalKeywords.Contains(CardKeyword.Ethereal),
            card.Enchantment is Slither ? new RandomDrawCost(card.EnergyCost._localModifiers.Select(modifier => modifier.Amount).ToArray()) : null,
            card is Burn ? (int)damage : null, card is Burn or AscendersBane, card.LocalKeywords.Contains(CardKeyword.Retain), card.HasSingleTurnSly);
    }

}
