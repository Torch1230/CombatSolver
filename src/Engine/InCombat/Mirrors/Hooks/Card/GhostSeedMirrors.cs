using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;

internal static class GhostSeedMirrors
{
    public static void AfterCardEnteredCombat(GhostSeed relic, PredictedCard card)
    {
        var preview = card.Preview;
        if (preview.Owner == relic.Owner && preview.Rarity == CardRarity.Basic
            && (preview.Tags.Contains(CardTag.Strike) || preview.Tags.Contains(CardTag.Defend))
            && !preview.GetKeywordsWithSources(KeywordSources.Local).Contains(CardKeyword.Ethereal))
            card.MutablePreview.AddKeyword(CardKeyword.Ethereal);
    }
}
