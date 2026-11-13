using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models.Cards;

namespace CombatSolver;

// Freeze the three native title stems on the main thread. Upgrade levels are
// formatted from immutable text, so future branch upgrades need no model mutation.
internal readonly record struct DingyDaggerDisplayNames(string Dingy, string Fine, string Pristine)
{
    internal static DingyDaggerDisplayNames Capture() => new(
        new LocString("cards", "DINGY_DAGGER.title.dingy").GetFormattedText(),
        new LocString("cards", "DINGY_DAGGER.title.fine").GetFormattedText(),
        new LocString("cards", "DINGY_DAGGER.title.pristine").GetFormattedText());

    internal string Format(int level)
    {
        string stem = level >= DingyDagger.pristineUpgradeLevel ? Pristine
            : level >= DingyDagger.fineUpgradeLevel ? Fine : Dingy;
        return level > 0 ? $"{stem}+{level}" : stem;
    }
}
