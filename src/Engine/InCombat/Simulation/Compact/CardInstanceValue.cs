namespace CombatSolver.Engine.InCombat.Simulation.Compact;

[Flags]
internal enum CardKeywordFlags { None = 0, Ethereal = 1, Retain = 2 }

// Nonnegative definition identity uses 31 bits; bit 31 marks the admitted one-shot
// enchantment as disabled. X uses 30 bits and keyword additions use the top two.
internal readonly record struct CardInstanceValue(int Definition, int CapturedX,
    CardKeywordFlags AddedKeywords = CardKeywordFlags.None, bool EnchantmentDisabled = false)
{
    internal long Data => Definition >= 0 && CapturedX is >= 0 and <= 999_999_999
        && (AddedKeywords & ~(CardKeywordFlags.Ethereal | CardKeywordFlags.Retain)) == 0
        ? (long)(uint)Definition | (EnchantmentDisabled ? 1L << 31 : 0) | (long)CapturedX << 32 | (long)AddedKeywords << 62
        : throw new ArgumentOutOfRangeException(nameof(CapturedX), "Compact card instance exceeds its admitted encoding.");

    internal static CardInstanceValue Decode(long data) => new((int)(data & int.MaxValue),
        (int)((ulong)data >> 32 & 0x3fff_ffff), (CardKeywordFlags)((ulong)data >> 62), (data & 1L << 31) != 0);
}
