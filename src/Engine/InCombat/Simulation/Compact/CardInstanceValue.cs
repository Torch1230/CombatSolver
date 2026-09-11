namespace CombatSolver.Engine.InCombat.Simulation.Compact;

[Flags]
internal enum CardKeywordFlags { None = 0, Ethereal = 1, Retain = 2 }

// Definition identity uses the low 32 bits. The admitted X bound fits in 30 bits,
// leaving two bits for persistent keyword additions without another per-card buffer.
internal readonly record struct CardInstanceValue(int Definition, int CapturedX,
    CardKeywordFlags AddedKeywords = CardKeywordFlags.None)
{
    internal long Data => Definition >= 0 && CapturedX is >= 0 and <= 999_999_999
        && (AddedKeywords & ~(CardKeywordFlags.Ethereal | CardKeywordFlags.Retain)) == 0
        ? (long)(uint)Definition | (long)CapturedX << 32 | (long)AddedKeywords << 62
        : throw new ArgumentOutOfRangeException(nameof(CapturedX), "Compact card instance exceeds its admitted encoding.");

    internal static CardInstanceValue Decode(long data) => new(unchecked((int)data),
        (int)((ulong)data >> 32 & 0x3fff_ffff), (CardKeywordFlags)((ulong)data >> 62));
}
