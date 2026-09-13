using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors;

namespace CombatSolver.Engine.InCombat.Simulation;

internal sealed partial class CombatPredictionSimulator
{
    // Execution-local: choices replay from a stable root; completed plays release their entries.
    private readonly Dictionary<CardPlay, (decimal Amount, int PoweredEvents)> _blockGainedByCardPlay = [];

    public int GetPoweredBlockEvents(CardPlay? cardPlay)
        => cardPlay == null ? 0 : _blockGainedByCardPlay.GetValueOrDefault(cardPlay).PoweredEvents;

    /// <summary>
    /// Mirrors <see cref="CreatureCmd.GainBlock(Creature, BlockVar, CardPlay?, bool)"/>.
    /// Convenience overload for when a <see cref="BlockVar"/> is supplied and the block source is not a card play.
    /// </summary>
    public decimal GainBlock(Creature creature, BlockVar blockVar)
    {
        return GainBlock(creature, blockVar.BaseValue, blockVar.Props, cardSource: null, cardPlay: null);
    }

    /// <summary>
    /// Mirrors <see cref="CreatureCmd.GainBlock(Creature, BlockVar, CardPlay?, bool)"/>.
    /// Convenience overload for when a <see cref="BlockVar"/> is supplied.
    /// </summary>
    public decimal GainBlock(Creature creature, BlockVar blockVar, PredictedCard? cardSource, CardPlay? cardPlay)
    {
        return GainBlock(creature, blockVar.BaseValue, blockVar.Props, cardSource, cardPlay);
    }

    /// <summary>
    /// Mirrors <see cref="CreatureCmd.GainBlock(Creature, decimal, ValueProp, CardPlay?, bool)"/>.
    /// Convenience overload for when the block source is not a card play.
    /// </summary>
    public decimal GainBlock(Creature creature, decimal amount, ValueProp props)
    {
        return GainBlock(creature, amount, props, cardSource: null, cardPlay: null);
    }

    /// <summary>
    /// Mirrors <see cref="CreatureCmd.GainBlock(Creature, decimal, ValueProp, CardPlay?, bool)"/>.
    /// </summary>
    public decimal GainBlock(
        Creature creature,
        decimal amount,
        ValueProp props,
        PredictedCard? cardSource,
        CardPlay? cardPlay)
    {
        if (IsOverOrEnding || State.GetCreature(creature).IsDead)
        {
            return 0m;
        }

        HookMirrors.BeforeBlockGained(this, creature, amount, props, cardSource);

        var modifiedBlock = HookMirrors.ModifyBlock(
            this,
            creature,
            amount,
            props,
            cardSource,
            cardPlay,
            out var modifiers);
        modifiedBlock = Math.Max(modifiedBlock, 0m);
        HookMirrors.AfterModifyingBlockAmount(this, modifiedBlock, cardSource, cardPlay, modifiers);

        if (modifiedBlock > 0m)
        {
            if (cardPlay != null)
            {
                var previous = _blockGainedByCardPlay.GetValueOrDefault(cardPlay);
                bool powered = props.IsCardOrMonsterMove();
                _blockGainedByCardPlay[cardPlay] = (previous.Amount + modifiedBlock,
                    previous.PoweredEvents + (powered ? 1 : 0));
                if (powered && State.CombatState is ICombatPredictionCardEventSink sink)
                    sink.RecordPoweredCardBlockGained(cardPlay.Player.Creature);
            }
            State.GetCreature(creature).GainBlock(modifiedBlock);
        }

        // Vanilla records BlockGained history before AfterBlockGained. Preview does not mutate
        // run/combat history, but it still scans AfterBlockGained through HookMirrors below so
        // known block-triggered state changes can be mirrored or marked as risk.
        HookMirrors.AfterBlockGained(this, creature, modifiedBlock, props, cardSource);
        return modifiedBlock;
    }

    private decimal GetBlockGained(CardPlay cardPlay)
        => _blockGainedByCardPlay.GetValueOrDefault(cardPlay).Amount;
}
