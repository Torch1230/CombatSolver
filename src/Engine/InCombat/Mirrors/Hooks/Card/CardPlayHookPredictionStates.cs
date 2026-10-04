using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Afflictions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using CombatSolver.Engine.Common;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;

// Shadow model state shared by card-play lifecycle hooks and their downstream value/predicate hook mirrors.
internal sealed class CounterPredictionState(int value) : IPredictionStateForkable
{
    public int Value { get; set; } = value;

    public object Fork(PredictionForkContext context) => MemberwiseClone();
}

internal sealed class ChainsOfBindingPredictionState : IPredictionStateForkable
{
    public bool BoundCardPlayed { get; set; }

    private int _afflictionTurn;
    private int _boundCardsAfflicted;

    public static ChainsOfBindingPredictionState CaptureRoot(ChainsOfBindingPower power)
        => new()
        {
            BoundCardPlayed = power.GetInternalData<ChainsOfBindingPower.Data>().boundCardPlayed,
            _afflictionTurn = power.Owner.Player?.PlayerCombatState?.TurnNumber ?? 0,
            _boundCardsAfflicted = CombatManager.Instance.History.Entries
                .OfType<CardAfflictedEntry>()
                .Count(entry => entry.HappenedThisTurn(power.CombatState)
                    && entry.Actor == power.Owner && entry.Affliction is Bound)
        };

    public int GetBoundCardsAfflictedThisTurn(int turn)
        => _afflictionTurn == turn ? _boundCardsAfflicted : 0;

    public void RecordBoundCardAfflicted(int turn)
    {
        _boundCardsAfflicted = GetBoundCardsAfflictedThisTurn(turn) + 1;
        _afflictionTurn = turn;
    }

    public object Fork(PredictionForkContext context) => MemberwiseClone();
}

internal sealed class SurroundedPredictionState(SurroundedPower power) : IPredictionStateForkable
{
    public SurroundedPower.Direction Facing { get; set; } = power.Facing;

    public object Fork(PredictionForkContext context) => MemberwiseClone();
}

internal sealed class PenNibPredictionState(PenNib relic) : IPredictionStateForkable, IPredictionForkBoundary
{
    public int AttacksPlayed { get; set; } = relic.AttacksPlayed;

    public CardModel? AttackToDouble { get; set; }

    public object Fork(PredictionForkContext context)
    {
        AssertForkable();
        return MemberwiseClone();
    }

    public void AssertForkable()
    {
        if (AttackToDouble is not null)
            throw new InvalidOperationException("Cannot fork Pen Nib during card-play resolution.");
    }
}

internal sealed class PaelsLegionPredictionState(PaelsLegion relic, bool affectedCardPlayFinished = false)
    : IPredictionStateForkable, IPredictionForkBoundary
{
    public int Cooldown { get; set; } = relic._cooldown;

    public bool TriggeredBlockLastTurn { get; set; } = relic._triggeredBlockLastTurn;

    // A finished native reference still prevents the relic from pairing with another play.
    public bool HasCompletedAffectedCardPlay { get; } = affectedCardPlayFinished;

    public CardPlay? AffectedCardPlay { get; set; } = affectedCardPlayFinished ? null : relic._affectedCardPlay;

    public bool HasAffectedCardPlay => HasCompletedAffectedCardPlay || AffectedCardPlay != null;

    public object Fork(PredictionForkContext context)
    {
        AssertForkable();
        return MemberwiseClone();
    }

    public void AssertForkable()
    {
        if (AffectedCardPlay is not null)
            throw new InvalidOperationException("Cannot fork Pael's Legion during card-play resolution.");
    }
}

internal sealed class VambracePredictionState(Vambrace relic) : IPredictionStateForkable
{
    public CardModel? TriggeringCard { get; set; } = relic._triggeringCard;

    public bool BlockGainedThisCombat { get; set; } = relic._blockGainedThisCombat;

    public object Fork(PredictionForkContext context) => MemberwiseClone();
}

internal sealed class VoidFormPredictionState(VoidFormPower power) : IPredictionStateForkable
{
    public int CardsPlayedThisTurn { get; set; } =
        power.GetInternalData<VoidFormPower.Data>().cardsPlayedThisTurn;

    public object Fork(PredictionForkContext context) => MemberwiseClone();
}
