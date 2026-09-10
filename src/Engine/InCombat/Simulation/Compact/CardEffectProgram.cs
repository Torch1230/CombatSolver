namespace CombatSolver.Engine.InCombat.Simulation.Compact;

internal enum CardInstructionKind { AttackTarget, GainBlock, Draw, Discard, ApplyWeakToTarget }

internal readonly record struct CardInstruction(CardInstructionKind Kind, int Amount);

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

    internal CardEffectProgram(ReadOnlySpan<CardInstruction> instructions)
    {
        _instructions = instructions.ToArray();
        foreach (CardInstruction instruction in _instructions)
        {
            if (instruction.Amount < 0 || instruction.Amount > 999_999_999)
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
                case CardInstructionKind.ApplyWeakToTarget:
                    RequiresTarget = true;
                    RequiresPowers = true;
                    break;
                default:
                    throw new NotSupportedException("Unknown compact card instruction.");
            }
        }
    }
}
