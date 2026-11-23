using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;

namespace CombatSolver;

internal sealed class PowerCardValuationRegistry
{
    private readonly Dictionary<string, PowerCommitmentDescriptor> _descriptorsByCardId;

    public PowerCardValuationRegistry(IEnumerable<PowerCommitmentDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        _descriptorsByCardId = new Dictionary<string, PowerCommitmentDescriptor>(StringComparer.Ordinal);
        foreach (PowerCommitmentDescriptor descriptor in descriptors)
        {
            if (string.IsNullOrEmpty(descriptor.CardId))
                throw new InvalidOperationException("能力牌估值登记需要非空 CardId。");
            if (!_descriptorsByCardId.TryAdd(descriptor.CardId, descriptor))
                throw new InvalidOperationException($"能力牌 ID 重复登记：{descriptor.CardId}。");
        }
    }

    public int Count => _descriptorsByCardId.Count;

    public bool ContainsCardId(string cardId)
        => !string.IsNullOrEmpty(cardId) && _descriptorsByCardId.ContainsKey(cardId);

    public bool TryGetPool(string cardId, out PowerCardPool pool)
    {
        if (!string.IsNullOrEmpty(cardId)
            && _descriptorsByCardId.TryGetValue(cardId, out PowerCommitmentDescriptor descriptor))
        {
            pool = descriptor.Pool;
            return true;
        }
        pool = default;
        return false;
    }

    public bool TryGetCommitmentDescriptor(
        string cardId,
        out PowerCommitmentDescriptor descriptor)
    {
        if (!string.IsNullOrEmpty(cardId)
            && _descriptorsByCardId.TryGetValue(cardId, out PowerCommitmentDescriptor found)
            && found.Family != PowerCommitmentFamily.None
            && !found.Admission.NoInCombatCommitment)
        {
            descriptor = found;
            return true;
        }
        descriptor = default;
        return false;
    }

    public bool HasReachableRegisteredPower(CombatState root)
    {
        ArgumentNullException.ThrowIfNull(root);
        PlayerCombatState? pcs = root.Players[0].PlayerCombatState;
        if (pcs is null)
            return false;

        return HasRegisteredInPile(pcs.Hand)
            || HasRegisteredInPile(pcs.DrawPile)
            || HasRegisteredInPile(pcs.DiscardPile);

        bool HasRegisteredInPile(CardPile pile)
        {
            IReadOnlyList<CardModel> cards = pile.Cards;
            for (int i = 0; i < cards.Count; i++)
            {
                if (_descriptorsByCardId.ContainsKey(cards[i].Id.Entry))
                    return true;
            }
            return false;
        }
    }
}
