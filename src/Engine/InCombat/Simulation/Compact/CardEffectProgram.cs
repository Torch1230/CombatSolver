namespace CombatSolver.Engine.InCombat.Simulation.Compact;

internal enum CardInstructionKind
{
    AttackTarget, GainBlock, Draw, Discard, ApplyBasicPower, SkipIfDrawnCardNotType,
    TriggerBasicPower, DiscardHandAndDraw, SkipIfTargetLacksPower, GainBlockFromPowerSum,
    GainBlockAndApplyPower, ApplyTemporaryStrengthLoss, GenerateCards, GainEnergy, SummonPet, PetAttackTarget, ExhaustFromDraw,
    LoseEnemyHp, RetrieveFromDiscard, ApplyPowerAtLeastCurrent, ApplyKeywordFromHand, DrawOnce
}
internal enum CardInstructionTarget { Owner, ChosenEnemy, AllEnemies }
internal enum CardCategory { Other, Attack, Skill, Power, Status }
internal enum CardGenerationPlacement { Hand, RandomDraw }

internal readonly record struct CardInstruction(CardInstructionKind Kind, int Amount,
    BasicPowerKind Power = BasicPowerKind.Strength, CardInstructionTarget Target = CardInstructionTarget.Owner,
    int EnergyXMultiplier = 0, CardCategory RequiredCategory = CardCategory.Skill, int Multiplier = 0, int CardTemplate = -1,
    CardGenerationPlacement Placement = CardGenerationPlacement.Hand, bool RepeatForEnergyX = false,
    BasicPowerKind? AttackMultiplierPower = null, CardKeywordFlags Keyword = CardKeywordFlags.None);

/// <summary>
/// Immutable, fully admitted OnPlay instructions. Execution position belongs to the value
/// workspace, never this shared definition. Model-specific compilation remains in the adapter.
/// </summary>
internal sealed class CardEffectProgram
{
    private readonly CardInstruction[] _instructions;
    internal static CardEffectProgram Empty { get; } = new([]);
    internal int Count => _instructions.Length;
    internal CardInstruction this[int index] => _instructions[index];
    internal int TotalDraw { get; }
    internal bool RequiresTarget { get; }
    internal bool RequiresPowers { get; }
    internal bool RequiresEnergyX { get; }
    internal bool GeneratesCards { get; }
    internal bool ExhaustsCards { get; }
    internal bool RequiresPet { get; }
    internal bool ChangesKeywords { get; }
    internal bool HasOneShotEnchantment { get; }

    internal CardEffectProgram Append(CardInstruction instruction) => new([.. _instructions, instruction]);

    internal CardEffectProgram(ReadOnlySpan<CardInstruction> instructions)
    {
        _instructions = instructions.ToArray();
        for (int index = 0; index < _instructions.Length; index++)
        {
            CardInstruction instruction = _instructions[index];
            if (instruction.Amount is < -999_999_999 or > 999_999_999
                || instruction.Kind != CardInstructionKind.ApplyBasicPower && instruction.Amount < 0
                || instruction.Kind is not (CardInstructionKind.ApplyBasicPower or CardInstructionKind.GenerateCards) && instruction.EnergyXMultiplier != 0
                || instruction.EnergyXMultiplier is < -1 or > 1
                || instruction.Multiplier is < 0 or > 999_999_999
                || instruction.Kind is not (CardInstructionKind.GainBlockFromPowerSum or CardInstructionKind.PetAttackTarget) && instruction.Multiplier != 0)
                throw new ArgumentException("Compact instruction amount is outside the admitted range.");
            if (instruction.Kind != CardInstructionKind.GenerateCards && (instruction.CardTemplate != -1 || instruction.Placement != CardGenerationPlacement.Hand))
                throw new ArgumentException("Only generation instructions can reference card templates.");
            if (instruction.RepeatForEnergyX && instruction.Kind != CardInstructionKind.SummonPet)
                throw new ArgumentException("Only summoning admits repeated X commands.");
            if (instruction.Keyword != CardKeywordFlags.None && instruction.Kind != CardInstructionKind.ApplyKeywordFromHand)
                throw new ArgumentException("Only keyword selection instructions can carry a keyword.");
            if (instruction.AttackMultiplierPower != null
                && (instruction.Kind != CardInstructionKind.AttackTarget || instruction.AttackMultiplierPower != BasicPowerKind.Hang))
                throw new NotSupportedException("Card-specific damage multipliers require the admitted attack Power.");
            RequiresEnergyX |= instruction.EnergyXMultiplier != 0 || instruction.RepeatForEnergyX;
            RequiresPowers |= instruction.AttackMultiplierPower != null;
            switch (instruction.Kind)
            {
                case CardInstructionKind.GenerateCards:
                    if (instruction.CardTemplate < 0 || instruction.Target != CardInstructionTarget.Owner
                        || !Enum.IsDefined(instruction.Placement) || instruction.EnergyXMultiplier < 0)
                        throw new NotSupportedException("Generation requires an admitted owner card template.");
                    GeneratesCards = true;
                    break;
                case CardInstructionKind.SummonPet:
                    if (instruction.Target != CardInstructionTarget.Owner)
                        throw new NotSupportedException("Summoning requires the admitted pet owner.");
                    RequiresPet = true;
                    break;
                case CardInstructionKind.PetAttackTarget:
                    RequiresPet = true;
                    RequiresTarget = true;
                    RequiresPowers = true;
                    break;
                case CardInstructionKind.AttackTarget:
                case CardInstructionKind.LoseEnemyHp:
                    RequiresTarget = true;
                    break;
                case CardInstructionKind.GainBlock:
                    break;
                case CardInstructionKind.GainEnergy:
                    if (instruction.Target != CardInstructionTarget.Owner)
                        throw new NotSupportedException("Energy gain requires the admitted player owner.");
                    break;
                case CardInstructionKind.GainBlockFromPowerSum:
                    if (instruction.Power != BasicPowerKind.Poison || instruction.Target != CardInstructionTarget.AllEnemies)
                        throw new NotSupportedException("Calculated block requires the admitted living-enemy Power sum.");
                    RequiresPowers = true;
                    break;
                case CardInstructionKind.GainBlockAndApplyPower:
                    if (instruction.Power != BasicPowerKind.BlockNextTurn || instruction.Target != CardInstructionTarget.Owner)
                        throw new NotSupportedException("Block return requires the admitted owner Power.");
                    RequiresPowers = true;
                    break;
                case CardInstructionKind.Draw:
                    if (instruction.Amount > 10) throw new ArgumentException("Compact draw exceeds hand capacity.");
                    TotalDraw = checked(TotalDraw + instruction.Amount);
                    break;
                case CardInstructionKind.DrawOnce:
                    if (instruction.Amount > 10 || instruction.Target != CardInstructionTarget.Owner
                        || index != _instructions.Length - 1)
                        throw new NotSupportedException("One-shot enchantment draw must be the final owner instruction within hand capacity.");
                    TotalDraw = checked(TotalDraw + instruction.Amount);
                    HasOneShotEnchantment = true;
                    break;
                case CardInstructionKind.Discard:
                case CardInstructionKind.ExhaustFromDraw:
                case CardInstructionKind.RetrieveFromDiscard:
                    if (instruction.Amount > 10) throw new ArgumentException("Compact selection exceeds choice capacity.");
                    ExhaustsCards |= instruction.Kind == CardInstructionKind.ExhaustFromDraw;
                    break;
                case CardInstructionKind.ApplyKeywordFromHand:
                    if (instruction.Amount != 1 || instruction.Target != CardInstructionTarget.Owner
                        || instruction.Keyword is not (CardKeywordFlags.Ethereal or CardKeywordFlags.Retain))
                        throw new NotSupportedException("Keyword choice requires one admitted local keyword and one hand card.");
                    ChangesKeywords = true;
                    break;
                case CardInstructionKind.DiscardHandAndDraw:
                    if (instruction.Amount != 0) throw new ArgumentException("Hand discard/draw derives its count from the captured hand.");
                    TotalDraw = checked(TotalDraw + 10);
                    break;
                case CardInstructionKind.TriggerBasicPower:
                    if (instruction.Amount != 0 || instruction.Power != BasicPowerKind.Poison
                        || instruction.Target is not (CardInstructionTarget.ChosenEnemy or CardInstructionTarget.AllEnemies))
                        throw new NotSupportedException("Power trigger is outside the admitted domain.");
                    RequiresPowers = true;
                    RequiresTarget |= instruction.Target == CardInstructionTarget.ChosenEnemy;
                    break;
                case CardInstructionKind.SkipIfDrawnCardNotType:
                    if (instruction.Amount == 0 || instruction.Amount > _instructions.Length - index - 1
                        || !Enum.IsDefined(instruction.RequiredCategory))
                        throw new ArgumentException("Compact conditional branch exceeds its program or has an unknown type.");
                    break;
                case CardInstructionKind.SkipIfTargetLacksPower:
                    if (instruction.Amount == 0 || instruction.Amount > _instructions.Length - index - 1)
                        throw new ArgumentException("Compact Power predicate exceeds its program.");
                    if (instruction.Power != BasicPowerKind.Poison || instruction.Target != CardInstructionTarget.ChosenEnemy)
                        throw new NotSupportedException("Power predicate is outside the admitted target domain.");
                    RequiresTarget = true;
                    RequiresPowers = true;
                    break;
                case CardInstructionKind.ApplyTemporaryStrengthLoss:
                    if (instruction.Power != BasicPowerKind.PiercingWail
                        || instruction.Target is not (CardInstructionTarget.ChosenEnemy or CardInstructionTarget.AllEnemies))
                        throw new NotSupportedException("Temporary Strength is outside the admitted enemy Power domain.");
                    RequiresTarget |= instruction.Target == CardInstructionTarget.ChosenEnemy;
                    RequiresPowers = true;
                    break;
                case CardInstructionKind.ApplyBasicPower:
                    if (instruction.Target is not (CardInstructionTarget.Owner or CardInstructionTarget.ChosenEnemy or CardInstructionTarget.AllEnemies)
                        || instruction.Power is not (BasicPowerKind.Strength or BasicPowerKind.Dexterity or BasicPowerKind.Weak or BasicPowerKind.Poison or BasicPowerKind.ToolsOfTheTrade or BasicPowerKind.Neurosurge or BasicPowerKind.BorrowedTime or BasicPowerKind.Veilpiercer or BasicPowerKind.SpiritOfAsh or BasicPowerKind.DanseMacabre)
                        || instruction.Power is BasicPowerKind.Weak or BasicPowerKind.Poison && (instruction.Target == CardInstructionTarget.Owner
                            || instruction.Amount < 0 || instruction.EnergyXMultiplier < 0)
                        || instruction.Power is BasicPowerKind.ToolsOfTheTrade or BasicPowerKind.Neurosurge or BasicPowerKind.BorrowedTime or BasicPowerKind.Veilpiercer or BasicPowerKind.SpiritOfAsh or BasicPowerKind.DanseMacabre && (instruction.Target != CardInstructionTarget.Owner
                            || instruction.Amount < 0 || instruction.EnergyXMultiplier != 0))
                        throw new NotSupportedException("Power instruction is outside the admitted application domain.");
                    RequiresTarget |= instruction.Target == CardInstructionTarget.ChosenEnemy;
                    RequiresPowers = true;
                    break;
                case CardInstructionKind.ApplyPowerAtLeastCurrent:
                    if (instruction.Power != BasicPowerKind.Hang || instruction.Target != CardInstructionTarget.ChosenEnemy)
                        throw new NotSupportedException("Growing Power requests require the admitted enemy counter.");
                    RequiresTarget = true;
                    RequiresPowers = true;
                    break;
                default:
                    throw new NotSupportedException("Unknown compact card instruction.");
            }
        }
    }
}
