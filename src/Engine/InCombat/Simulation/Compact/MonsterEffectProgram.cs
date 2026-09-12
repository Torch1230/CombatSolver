namespace CombatSolver.Engine.InCombat.Simulation.Compact;

internal enum MonsterInstructionKind { AttackPlayer, GainBlock, GainStrength, GenerateCards }
internal readonly record struct MonsterInstruction(MonsterInstructionKind Kind, int Amount, int CardTemplate = -1);

// Captured commands only. Monster identity, move selection and phase ordering belong to
// the root adapter/caller; this program cannot read model state or choose a next move.
internal sealed class MonsterEffectProgram
{
    private readonly MonsterInstruction[] _instructions;
    internal int Count => _instructions.Length;
    internal MonsterInstruction this[int index] => _instructions[index];
    internal bool GeneratesCards { get; }

    internal MonsterEffectProgram(ReadOnlySpan<MonsterInstruction> instructions)
    {
        _instructions = instructions.ToArray();
        foreach (var instruction in _instructions)
        {
            if (!Enum.IsDefined(instruction.Kind) || instruction.Amount is < 0 or > 999_999_999
                || (instruction.Kind == MonsterInstructionKind.GenerateCards ? instruction.CardTemplate < 0 : instruction.CardTemplate != -1))
                throw new ArgumentException("Monster command is outside the captured domain.");
            GeneratesCards |= instruction.Kind == MonsterInstructionKind.GenerateCards;
        }
    }
}
