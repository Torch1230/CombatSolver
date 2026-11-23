using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Entities.Cards;

namespace CombatSolver.HotKernel;

internal static class HotKeywords
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static HotCardKeywordMask ToMask(CardKeyword kw) => kw switch
    {
        CardKeyword.None => HotCardKeywordMask.None,
        CardKeyword.Exhaust => HotCardKeywordMask.Exhaust,
        CardKeyword.Ethereal => HotCardKeywordMask.Ethereal,
        CardKeyword.Innate => HotCardKeywordMask.Innate,
        CardKeyword.Unplayable => HotCardKeywordMask.Unplayable,
        CardKeyword.Retain => HotCardKeywordMask.Retain,
        CardKeyword.Sly => HotCardKeywordMask.Sly,
        CardKeyword.Eternal => HotCardKeywordMask.Eternal,
        _ => throw new ArgumentOutOfRangeException(nameof(kw), kw, "Unsupported CardKeyword value."),
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Has(HotCardKeywordMask mask, CardKeyword kw)
        => kw != CardKeyword.None && (mask & ToMask(kw)) != 0;

    public static HotCardKeywordMask FromEnumerable(IEnumerable<CardKeyword>? keywords)
    {
        if (keywords == null)
            return HotCardKeywordMask.None;

        HotCardKeywordMask mask = HotCardKeywordMask.None;
        foreach (CardKeyword kw in keywords)
            mask |= ToMask(kw);
        return mask;
    }
}
