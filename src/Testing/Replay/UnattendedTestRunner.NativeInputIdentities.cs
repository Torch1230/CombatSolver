using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private sealed record NativeInputCardIdentity(
        int HandIndex, string CardId, int Upgrade, string StateKey, int CardOccurrence,
        int StateOccurrence, int ReplayCount, string EnchantmentId, string Title);

    private sealed record NativeInputEnemyIdentity(int Index, uint? CombatId, string Name);

    private sealed record NativeInputIdentity(
        int EventCursor, int Turn, string ActionType, string RecordedDescription,
        int? SelectedHandIndex, NativeInputCardIdentity[] Hand, NativeInputEnemyIdentity[] Enemies);

    private static NativeInputIdentity CaptureNativeInputIdentity(
        GameAction action, RecordedCombatEvent recorded, int cursor, Player player)
    {
        CombatState combat = player.Creature.CombatState as CombatState
            ?? throw new InvalidDataException("native_input_identity_missing_combat");
        ContinuationStamp before = ContinuationStamp.CaptureLive(combat);
        var hand = player.PlayerCombatState!.Hand.Cards.ToArray();
        string[] keys = hand.Select(CardChoiceSupport.ChoiceCardKey).ToArray();
        NativeInputCardIdentity[] cards = hand.Select((card, index) => new NativeInputCardIdentity(
            index, card.Id.Entry, card.CurrentUpgradeLevel, keys[index],
            hand.Take(index).Count(prior => prior.Id == card.Id),
            keys.Take(index).Count(key => string.Equals(key, keys[index], StringComparison.Ordinal)),
            Math.Max(0, card.GetEnchantedReplayCount()), card.Enchantment?.Id.Entry ?? "", card.Title)).ToArray();
        int? selectedIndex = null;
        if (action is PlayCardAction play)
        {
            var selected = play.NetCombatCard.ToCardModelOrNull()
                ?? throw new InvalidDataException("native_input_identity_missing_selected_card");
            int index = Array.FindIndex(hand, card => ReferenceEquals(card, selected));
            if (index < 0)
                throw new InvalidDataException("native_input_identity_selected_card_not_in_hand");
            selectedIndex = index;
        }
        NativeInputEnemyIdentity[] enemies = combat.Enemies.Select((enemy, index) =>
            new NativeInputEnemyIdentity(index, enemy.CombatId, enemy.Name)).ToArray();
        if (ContinuationStamp.CaptureLive(combat) != before)
            throw new InvalidDataException("native_input_identity_capture_changed_live_state");
        return new(cursor, player.PlayerCombatState.TurnNumber, action.GetType().Name,
            recorded.Description ?? "", selectedIndex, cards, enemies);
    }
}
