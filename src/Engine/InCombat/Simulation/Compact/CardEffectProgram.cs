namespace CombatSolver.Engine.InCombat.Simulation.Compact;

internal enum CardInstructionKind { AttackTarget, GainBlock, Draw, Discard, ApplyBasicPower }
internal enum CardInstructionTarget { Owner, ChosenEnemy }

internal readonly record struct CardInstruction(CardInstructionKind Kind, int Amount,
    BasicPowerKind Power = BasicPowerKind.Strength, CardInstructionTarget Target = CardInstructionTarget.Owner,
    int EnergyXMultiplier = 0);

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
        foreach (CardInstruction instruction in _instructions)
        {
            if (instruction.Amount is < -999_999_999 or > 999_999_999
                || instruction.Kind != CardInstructionKind.ApplyBasicPower && (instruction.Amount < 0 || instruction.EnergyXMultiplier != 0)
                || instruction.EnergyXMultiplier is < -1 or > 1)
                throw new ArgumentException("Compact instruction amount is outside the admitted range.");
            switch (instruction.Kind)
            {
                case CardInstructionKind.AttackTarget:
                    RequiresTarget = true;
                    break;
                case CardInstructionKind.GainBlock:
                    break;
                case CardInstructionKind.Draw:
                    if (instruction.Amount > 10) throw new ArgumentException("Compact draw exceeds hand capacity.");
                    TotalDraw = checked(TotalDraw + instruction.Amount);
                    break;
                case CardInstructionKind.Discard:
                    if (instruction.Amount > 10) throw new ArgumentException("Compact discard exceeds choice capacity.");
                    break;
                case CardInstructionKind.ApplyBasicPower:
                    if (instruction.Target is not (CardInstructionTarget.Owner or CardInstructionTarget.ChosenEnemy)
                        || instruction.Power is not (BasicPowerKind.Strength or BasicPowerKind.Dexterity or BasicPowerKind.Weak)
                        || instruction.Power == BasicPowerKind.Weak && (instruction.Target != CardInstructionTarget.ChosenEnemy
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
