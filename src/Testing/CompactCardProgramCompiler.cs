using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Enchantments;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Enchantments;

namespace CombatSolver;

// Exact native card admission and immutable instruction compilation, shared by all test readers.
internal static class CompactCardProgramCompiler
{
    internal static ResumableDiscardProgram.Card Compile(CardModel card, bool includeAttacks, int shivTemplate = -1, int inkyShivTemplate = -1)
    {
        if (card is not (Acrobatics or Prepared or Backflip or StrikeSilent or StrikeNecrobinder or DefendSilent or DefendNecrobinder
                or Neutralize or Survivor or Finesse or UltimateDefend or Suppress or Footwork or Malaise
                or DeadlyPoison or Haze or Snakebite or Defy or EscapePlan or Outbreak or CalculatedGamble or BubbleBubble or Mirage or DodgeAndRoll or ToolsOfTheTrade or PiercingWail or CloakAndDagger or Shiv or BladeOfInk)
            || card is Neutralize or Suppress or Footwork or Malaise or DeadlyPoison or Haze or Snakebite or Defy or Outbreak or BubbleBubble or Mirage or DodgeAndRoll or ToolsOfTheTrade or PiercingWail or CloakAndDagger or Shiv or BladeOfInk && !includeAttacks
            || card.Enchantment != null && !(card is Shiv && card.Enchantment is Inky { Amount: 1, Status: EnchantmentStatus.Normal })
                && card.Enchantment is not Slither { Amount: 1, Status: EnchantmentStatus.Normal, TestEnergyCostOverride: -1 }
            || card.Affliction != null || card.BaseReplayCount != 0
            || card.ExhaustOnNextPlay || card.IsDupe || card.IsClone || card.HasBeenRemovedFromState
            || card.EnergyCost.CostsX && (card is not Malaise || card.Enchantment is Slither)
            || card.EnergyCost._localModifiers.Count != 0 && (card.Enchantment is not Slither
                || card.EnergyCost._localModifiers.Any(modifier => modifier.GetType() != typeof(LocalCostModifier)
                    || modifier.Type != LocalCostType.Absolute || modifier.Expiration != LocalCostModifierExpiration.EndOfCombat
                    || modifier.IsReduceOnly || modifier.Amount is < 0 or > 3))
            || card.HasStarCostX || card.CurrentStarCost > 0 || card._temporaryStarCosts.Count != 0
            || card.CurrentTarget != null || card.CurrentPlayIndex != 0 || card.LastStarsSpent != 0
            || card.HasSingleTurnRetain || card.HasTurnEndInHandEffect
            || card.LocalKeywords.Any(k => k != CardKeyword.Sly && !(card is Malaise or CalculatedGamble or Mirage or PiercingWail or Shiv && k == CardKeyword.Exhaust)
                && !(card is Suppress && k == CardKeyword.Innate) && !(card is Snakebite or CalculatedGamble && k == CardKeyword.Retain)
                && !(card is Defy && k == CardKeyword.Ethereal))
            || card.IsSlyThisTurn && card is not Prepared)
            throw new NotSupportedException($"Compact prototype cannot admit card state {card.Id.Entry}.");
        decimal draw = card is EscapePlan ? 1 : card is Acrobatics or Prepared or Backflip or Finesse ? card.DynamicVars.Cards.BaseValue : 0;
        decimal damage = includeAttacks && card is StrikeSilent or StrikeNecrobinder or Neutralize or Suppress or Shiv ? card.DynamicVars.Damage.BaseValue : 0;
        decimal block = card is DefendSilent or DefendNecrobinder or Backflip or Survivor or Finesse or UltimateDefend or Defy or EscapePlan or DodgeAndRoll or CloakAndDagger ? card.DynamicVars.Block.BaseValue : 0;
        decimal weak = card.Enchantment is Inky inky ? inky.DynamicVars.Weak.BaseValue
            : card is Neutralize or Suppress or Haze or Defy ? card.DynamicVars.Weak.BaseValue : 0;
        decimal poison = card is DeadlyPoison or Haze or Snakebite or Outbreak or BubbleBubble ? card.DynamicVars.Poison.BaseValue : 0;
        decimal generated = card is CloakAndDagger or BladeOfInk ? card.DynamicVars.Cards.BaseValue : 0;
        decimal strengthLoss = card is PiercingWail ? card.DynamicVars["StrengthLoss"].BaseValue : 0;
        decimal dexterity = card is Footwork ? card.DynamicVars.Dexterity.BaseValue : 0;
        decimal calculationBase = card is Mirage ? card.DynamicVars.CalculationBase.BaseValue : 0;
        decimal calculationExtra = card is Mirage ? card.DynamicVars.CalculationExtra.BaseValue : 0;
        if (draw != decimal.Truncate(draw) || draw < 0 || draw > 10 || card.EnergyCost._base < 0
            || card is Acrobatics or Prepared or Backflip or Finesse && draw == 0
            || damage != decimal.Truncate(damage) || damage is < 0 or > 999_999_999m
            || block != decimal.Truncate(block) || block is < 0 or > 999_999_999m
            || weak != decimal.Truncate(weak) || weak is < 0 or > 999_999_999m
            || poison != decimal.Truncate(poison) || poison is < 0 or > 999_999_999m
            || generated != decimal.Truncate(generated) || generated is < 0 or > 999_999_999m
            || strengthLoss != decimal.Truncate(strengthLoss) || strengthLoss is < 0 or > 999_999_999m
            || dexterity != decimal.Truncate(dexterity) || dexterity is < 0 or > 999_999_999m
            || calculationBase != decimal.Truncate(calculationBase) || calculationBase is < 0 or > 999_999_999m
            || calculationExtra != decimal.Truncate(calculationExtra) || calculationExtra is < 0 or > 999_999_999m)
            throw new NotSupportedException($"Compact prototype cannot admit card variables {card.Id.Entry}.");
        // These are ordered native commands, including meaningful zero-base effects. The
        // adapter admits exact types/instance state; the executor contains no card identities.
        CardEffectProgram effects = card switch
        {
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
                CardInstructionTarget.AllEnemies, PowerMultiplier: (int)calculationExtra)]),
            Malaise => new([new(CardInstructionKind.ApplyBasicPower, card.IsUpgraded ? -1 : 0, BasicPowerKind.Strength,
                    CardInstructionTarget.ChosenEnemy, -1),
                new(CardInstructionKind.ApplyBasicPower, card.IsUpgraded ? 1 : 0, BasicPowerKind.Weak, CardInstructionTarget.ChosenEnemy, 1)]),
            _ => throw new NotSupportedException("Card has no admitted compact instructions.")
        };
        return new(card.EnergyCost._base, effects, card.IsSlyThisTurn,
            card.Type == CardType.Power ? ResumableDiscardProgram.Pile.Removed : card.LocalKeywords.Contains(CardKeyword.Exhaust) ? ResumableDiscardProgram.Pile.Exhaust : ResumableDiscardProgram.Pile.Discard,
            card.EnergyCost.CostsX, card.EnergyCost.CostsX ? card.EnergyCost.CapturedXValue : 0,
            card.Type switch { CardType.Attack => CardCategory.Attack, CardType.Skill => CardCategory.Skill,
                CardType.Power => CardCategory.Power, _ => CardCategory.Other }, card.LocalKeywords.Contains(CardKeyword.Ethereal),
            card.Enchantment is Slither ? new RandomDrawCost(card.EnergyCost._localModifiers.Select(modifier => modifier.Amount).ToArray()) : null);
    }

}
