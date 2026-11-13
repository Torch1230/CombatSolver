using System.Text;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace CombatSolver;

internal static class CardInstanceStateSupport
{
    internal static void Append(StringBuilder text, CardModel card, CardModel? dupeSource = null)
    {
        text.Append("|keywordsAdded=").AppendJoin(',', card._keywordsAddedFromOutside.Order());
        text.Append("|keywordsRemoved=").AppendJoin(',', card._keywordsRemovedFromOutside.Order());
        if (card is TheShot shot) text.Append("|florescence=").Append(shot._cardsExhaustedByFlorescence);
        if (card is ShowOff showOff) text.Append("|showOff=").Append(showOff.CurrentAim).Append(':').Append(showOff.IncreasedAim);
        text.Append("|dupe=").Append(card.IsDupe);
        if (card.IsDupe)
            text.Append("|dupeSource={").Append(CardChoiceSupport.ChoiceCardKey(dupeSource ?? card.DupeOf
                ?? throw new InvalidOperationException("Dupe has no source card."))).Append('}');
    }
}
