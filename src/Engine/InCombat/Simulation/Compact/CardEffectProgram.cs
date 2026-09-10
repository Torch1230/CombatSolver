namespace CombatSolver.Engine.InCombat.Simulation.Compact;

internal enum CardInstructionKind { AttackTarget, GainBlock, Draw, Discard, ApplyBasicPower, SkipIfDrawnCardNotType, TriggerBasicPower, DiscardHandAndDraw, SkipIfTargetLacksPower, GainBlockFromPowerSum }
internal enum CardInstructionTarget { Owner, ChosenEnemy, AllEnemies }
internal enum CardCategory { Other, Attack, Skill, Power }

internal readonly record struct CardInstruction(CardInstructionKind Kind, int Amount,
    BasicPowerKind Power = BasicPowerKind.Strength, CardInstructionTarget Target = CardInstructionTarget.Owner,
    int EnergyXMultiplier = 0, CardCategory RequiredCategory = CardCategory.Skill, int PowerMultiplier = 0);

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

    internal CardEffectProgram(ReadOnlySpan<CardInstruction> instructions)
    {
        _instructions = instructions.ToArray();
        for (int index = 0; index < _instructions.Length; index++)
        {
            CardInstruction instruction = _instructions[index];
            if (instruction.Amount is < -999_999_999 or > 999_999_999
                || instruction.Kind != CardInstructionKind.ApplyBasicPower && (instruction.Amount < 0 || instruction.EnergyXMultiplier != 0)
                || instruction.EnergyXMultiplier is < -1 or > 1
                || instruction.PowerMultiplier is < 0 or > 999_999_999
                || instruction.Kind != CardInstructionKind.GainBlockFromPowerSum && instruction.PowerMultiplier != 0)
                throw new ArgumentException("Compact instruction amount is outside the admitted range.");
            switch (instruction.Kind)
            {
                case CardInstructionKind.AttackTarget:
                    RequiresTarget = true;
                    break;
                case CardInstructionKind.GainBlock:
                    break;
                case CardInstructionKind.GainBlockFromPowerSum:
                    if (instruction.Power != BasicPowerKind.Poison || instruction.Target != CardInstructionTarget.AllEnemies)
                        throw new NotSupportedException("Calculated block requires the admitted living-enemy Power sum.");
                    RequiresPowers = true;
                    break;
                case CardInstructionKind.Draw:
                    if (instruction.Amount > 10) throw new ArgumentException("Compact draw exceeds hand capacity.");
                    TotalDraw = checked(TotalDraw + instruction.Amount);
                    break;
                case CardInstructionKind.Discard:
                    if (instruction.Amount > 10) throw new ArgumentException("Compact discard exceeds choice capacity.");
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
                case CardInstructionKind.ApplyBasicPower:
                    if (instruction.Target is not (CardInstructionTarget.Owner or CardInstructionTarget.ChosenEnemy or CardInstructionTarget.AllEnemies)
                        || instruction.Power is not (BasicPowerKind.Strength or BasicPowerKind.Dexterity or BasicPowerKind.Weak or BasicPowerKind.Poison)
                        || instruction.Power is BasicPowerKind.Weak or BasicPowerKind.Poison && (instruction.Target == CardInstructionTarget.Owner
                            || instruction.Amount < 0 || instruction.EnergyXMultiplier < 0))
                        throw new NotSupportedException("Power instruction is outside the admitted application domain.");
                    RequiresTarget |= instruction.Target == CardInstructionTarget.ChosenEnemy;
                    RequiresPowers = true;
                    RequiresEnergyX |= instruction.EnergyXMultiplier != 0;
                    break;
                default:
                    throw new NotSupportedException("Unknown compact card instruction.");
            }
        }
    }
}
