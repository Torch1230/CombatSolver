
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver.Engine.Common;

internal readonly record struct PredictionRngState(
    int Counter,
    ulong State0,
    ulong State1,
    ulong State2,
    ulong State3);

internal static class PredictionExtensions
{
    public static Rng Clone(this Rng rng)
    {
        Rng clone = new(0UL);
        clone._counter = rng._counter;
        clone._random._s0 = rng._random._s0;
        clone._random._s1 = rng._random._s1;
        clone._random._s2 = rng._random._s2;
        clone._random._s3 = rng._random._s3;
        return clone;
    }

    public static Rng ToRng(this PredictionRngState state)
    {
        Rng rng = new(0UL);
        rng._counter = state.Counter;
        rng._random._s0 = state.State0;
        rng._random._s1 = state.State1;
        rng._random._s2 = state.State2;
        rng._random._s3 = state.State3;
        return rng;
    }

    public static int Counter(this Rng rng) => rng._counter;

    public static PredictionRngState CaptureState(this Rng rng)
        => new(
            rng._counter,
            rng._random._s0,
            rng._random._s1,
            rng._random._s2,
            rng._random._s3);

    public static void Advance(this Rng rng, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        // StS2 v0.108.0 FastForwardCounter advanced MegaRandom once per discarded value.
        // v0.109.0 removed counter-based reconstruction, so discard raw draws directly.
        for (var i = 0; i < count; i++)
        {
            _ = rng.NextUnsignedLong();
        }
    }

    public static RelicGrabBag Clone(this RelicGrabBag grabBag)
    {
        return RelicGrabBag.FromSerializable(grabBag.ToSerializable());
    }

    public static IEnumerable<CardModel> GetUnlockedCards(
        this Player player,
        CardPoolModel cardPool,
        CardMultiplayerConstraint multiplayerConstraint)
    {
        return cardPool.GetUnlockedCards(player.UnlockState, multiplayerConstraint);
    }

    public static IEnumerable<CardModel> GetUnlockedCharacterCards(
        this Player player,
        CardMultiplayerConstraint multiplayerConstraint)
    {
        return player.GetUnlockedCards(player.Character.CardPool, multiplayerConstraint);
    }

    public static IEnumerable<CardModel> GetUnlockedColorlessCards(
        this Player player,
        CardMultiplayerConstraint multiplayerConstraint)
    {
        return player.GetUnlockedCards(ModelDb.CardPool<ColorlessCardPool>(), multiplayerConstraint);
    }

    public static IEnumerable<CardModel> GetUnlockedCurseCards(
        this Player player,
        CardMultiplayerConstraint multiplayerConstraint)
    {
        return player.GetUnlockedCards(ModelDb.CardPool<CurseCardPool>(), multiplayerConstraint);
    }

    public static string GetTitle(this AbstractModel model)
    {
        try
        {
            return model switch
            {
                CardModel card => card.Title,
                RelicModel relic => relic.Title.GetFormattedText(),
                PowerModel power => power.Title.GetFormattedText(),
                PotionModel potion => potion.Title.GetFormattedText(),
                ModifierModel modifier => modifier.Title.GetFormattedText(),
                AfflictionModel affliction => affliction.Title.GetFormattedText(),
                EnchantmentModel enchantment => enchantment.Title.GetFormattedText(),
                OrbModel orb => orb.Title.GetFormattedText(),
                MonsterModel monster => monster.Title.GetFormattedText(),
                _ => model.Id.Entry
            };
        }
        catch
        {
            return model.Id.Entry;
        }
    }
}
