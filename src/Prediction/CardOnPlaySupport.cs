using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static partial class CardOnPlaySupport
{
    public static void Apply(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        PredictedCard playedCard,
        CardPlay cardPlay,
        Creature? target,
        ISet<uint> processedEnemyDeaths)
    {
        CardModel card = playedCard.Preview;
        Creature owner = card.Owner.Creature;
        CardPowerOnPlaySupport.Apply(combat, card);
        CardPileOnPlaySupport.Apply(simulator, playedCard);
        switch (card)
        {
            case Alignment:
                simulator.State.GetPlayerCombatState(card.Owner).GainEnergy(card.DynamicVars.Energy.IntValue);
                break;
            case BorrowedTime:
                simulator.State.GetPlayerCombatState(card.Owner).GainEnergy(card.DynamicVars.Energy.IntValue);
                combat.Apply<BorrowedTimePower>(owner, card.DynamicVars["ExtraCost"].IntValue, owner);
                break;
            case BubbleBubble when target != null && combat.GetAmount<PoisonPower>(target) > 0:
                combat.Apply<PoisonPower>(target, card.DynamicVars.Poison.IntValue, owner);
                break;
            case BulletTime:
                foreach (PredictedCard handCard in simulator.State.GetPlayerCombatState(card.Owner).Hand)
                {
                    if (!handCard.Preview.EnergyCost.CostsX)
                        handCard.MutablePreview.SetToFreeThisTurn();
                }
                combat.Apply<NoDrawPower>(owner, 1, owner);
                break;
            case Bloodletting:
                simulator.Damage(
                    [owner],
                    card.DynamicVars.HpLoss.IntValue,
                    ValueProp.Unblockable | ValueProp.Unpowered | ValueProp.Move,
                    owner,
                    playedCard,
                    null);
                simulator.State.GetPlayerCombatState(card.Owner).GainEnergy(card.DynamicVars.Energy.IntValue);
                break;
            case Conqueror when target != null:
                PersistentPowerSupport.Forge(simulator, card.Owner, card.DynamicVars.Forge.IntValue);
                combat.Apply<ConquerorPower>(target, 1, owner);
                break;
            case Convergence:
                combat.Apply<RetainHandPower>(owner, 1, owner);
                combat.Apply<EnergyNextTurnPower>(owner, card.DynamicVars.Energy.IntValue, owner);
                combat.Apply<StarNextTurnPower>(owner, card.DynamicVars.Stars.IntValue, owner);
                break;
            case Deathbringer:
                foreach (Creature enemy in combat.HittableEnemies)
                {
                    combat.Apply<DoomPower>(enemy, card.DynamicVars.Doom.IntValue, owner);
                    combat.Apply<WeakPower>(enemy, card.DynamicVars.Weak.IntValue, owner);
                }
                break;
            case DarkShackles when target != null:
                combat.ApplyTemporaryStrengthLoss<DarkShacklesPower>(
                    target,
                    card.DynamicVars["StrengthLoss"].IntValue,
                    owner);
                break;
            case DeadlyPoison when target != null:
                combat.Apply<PoisonPower>(target, card.DynamicVars.Poison.IntValue, owner);
                break;
            case Debris:
                break;
            case DoubleEnergy:
            {
                SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(card.Owner);
                playerState.GainEnergy(playerState.Energy);
                break;
            }
            case EnfeeblingTouch when target != null:
                combat.ApplyTemporaryStrengthLoss<EnfeeblingTouchPower>(
                    target,
                    card.DynamicVars["StrengthLoss"].IntValue,
                    owner);
                break;
            case Expose when target != null:
                SimCreatureState targetState = simulator.State.GetCreature(target);
                if (targetState.Block > 0)
                    targetState.DamageBlock(targetState.Block, ValueProp.Move);
                if (combat.GetAmount<ArtifactPower>(target) > 0)
                    combat.SetAmount<ArtifactPower>(target, 0);
                combat.Apply<VulnerablePower>(target, card.DynamicVars["Power"].IntValue, owner);
                break;
            case Feral:
                combat.Apply<FeralPower>(owner, card.DynamicVars["FeralPower"].IntValue, owner);
                combat.InitializeFeralAfterApplied(simulator, owner);
                break;
            case ForgottenRitual or Fuel or Luminesce:
                simulator.State.GetPlayerCombatState(card.Owner).GainEnergy(card.DynamicVars.Energy.IntValue);
                break;
            case Haze:
                foreach (Creature enemy in combat.HittableEnemies)
                    combat.Apply<PoisonPower>(enemy, card.DynamicVars.Poison.IntValue, owner);
                foreach (Creature enemy in combat.HittableEnemies)
                    combat.Apply<WeakPower>(enemy, card.DynamicVars.Weak.IntValue, owner);
                break;
            case HiddenCache:
                simulator.GainStars(card.Owner, card.DynamicVars.Stars.IntValue);
                combat.Apply<StarNextTurnPower>(
                    owner,
                    card.DynamicVars["StarNextTurnPower"].IntValue,
                    owner);
                break;
            case Juggling:
                combat.Apply<JugglingPower>(owner, 1, owner);
                combat.InitializeJugglingAfterApplied(simulator, owner);
                break;
            case NoEscape when target != null:
                int threshold = card.DynamicVars["DoomThreshold"].IntValue;
                int amount = card.DynamicVars.CalculationBase.IntValue
                    + card.DynamicVars.CalculationExtra.IntValue
                    * (combat.GetAmount<DoomPower>(target) / threshold);
                combat.Apply<DoomPower>(target, amount, owner);
                break;
            case Neutralize when target != null:
                combat.Apply<WeakPower>(target, card.DynamicVars.Weak.IntValue, owner);
                break;
            case NotYet:
                simulator.Heal(owner, card.DynamicVars.Heal.IntValue);
                break;
            case Oblivion when target != null:
                combat.Apply<OblivionPower>(target, card.DynamicVars.Doom.IntValue, owner);
                break;
            case Production:
                simulator.State.GetPlayerCombatState(card.Owner).GainEnergy(card.DynamicVars.Energy.IntValue);
                break;
            case Prolong:
                combat.Apply<BlockNextTurnPower>(owner, simulator.State.GetCreature(owner).Block, owner);
                break;
            case RoyalGamble:
                simulator.GainStars(card.Owner, card.DynamicVars.Stars.IntValue);
                break;
            case SeekingEdge:
                PersistentPowerSupport.Forge(simulator, card.Owner, card.DynamicVars.Forge.IntValue);
                break;
            case ShadowStep:
                simulator.Discard(simulator.State.GetPlayerCombatState(card.Owner).Hand.Cards.ToArray());
                combat.Apply<ShadowStepPower>(owner, 1, owner);
                break;
            case Snakebite when target != null:
                combat.Apply<PoisonPower>(target, card.DynamicVars.Poison.IntValue, owner);
                break;
            case SummonForth:
                PersistentPowerSupport.Forge(simulator, card.Owner, card.DynamicVars.Forge.IntValue);
                break;
            case Supercritical or Tactician or Wisp:
                simulator.State.GetPlayerCombatState(card.Owner).GainEnergy(card.DynamicVars.Energy.IntValue);
                break;
            case Synchronize:
            {
                int orbTypes = simulator.State.GetPlayerCombatState(card.Owner).OrbQueue.Orbs
                    .Select(orb => orb.Id)
                    .Distinct()
                    .Count();
                int synchronizeAmount = card.DynamicVars.CalculationBase.IntValue
                    + card.DynamicVars.CalculationExtra.IntValue * orbTypes;
                combat.ApplyTemporaryFocus<SynchronizePower>(owner, synchronizeAmount, owner);
                break;
            }
            case TheSmith:
                PersistentPowerSupport.Forge(simulator, card.Owner, card.DynamicVars.Forge.IntValue);
                break;
            case Turbo:
            {
                simulator.State.GetPlayerCombatState(card.Owner).GainEnergy(card.DynamicVars.Energy.IntValue);
                PredictedCard generated = PredictedCard.Create(
                    CanonicalModels.Card<MegaCrit.Sts2.Core.Models.Cards.Void>(),
                    card.Owner);
                simulator.AddGeneratedCardToCombat(
                    generated,
                    PileType.Discard,
                    card.Owner,
                    CardPilePosition.Bottom,
                    CardGenerationResultKind.Fixed);
                break;
            }
            case Venerate:
                simulator.GainStars(card.Owner, card.DynamicVars.Stars.IntValue);
                break;
        }
        ApplyBatch042(simulator, combat, playedCard, cardPlay, target, processedEnemyDeaths);
        ApplyBatch043(simulator, combat, playedCard, target, processedEnemyDeaths);
    }
}
