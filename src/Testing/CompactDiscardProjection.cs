using System.Reflection;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.Common.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Simulation.Compact;
using CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Achievements;
using MegaCrit.Sts2.Core.Models.Badges;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Models.Singleton;

namespace CombatSolver;

/// <summary>
/// Test-only compatibility projection for the compact experiment. It decodes committed events;
/// it never executes OnPlay, a choice resolver, or the discard hook again. Production search
/// cannot select this adapter. Full legacy fork/materialization/evaluation costs must be measured.
/// </summary>
internal sealed class CompactDiscardProjection
{
    private readonly CombatPredictionSimulator _root;
    private readonly Player _player;
    private readonly CardModel[] _identities;
    private readonly bool[] _inferred;
    internal readonly ResumableDiscardProgram Program;

    internal CompactDiscardProjection(CombatPredictionSimulator root, Player player)
    {
        _root = root;
        _player = player;
        var combat = (SimulatedCombatState)root.State.CombatState;
        SimPlayerCombatState state = root.State.GetPlayerCombatState(player);
        if (combat.Players.Count != 1 || combat.EffectivePowers().Count != 0
            || combat.RootRunModSubscriberCount != 0 || combat.RootCombatModSubscriberCount != 0
            || combat.RootHasBaseLibCardModifiers || combat.RootRunHookListenerCount != 0
            || state.OrbQueue.Orbs.Count != 0 || root.GetMaxHandSize(player) != 10
            || root.HasPendingChoice || root.IsOverOrEnding)
            throw new NotSupportedException("Compact prototype requires an idle root without Powers, deck listeners, or mod subscribers.");
        var relics = combat.RelicsOf(player);
        if (relics.Any(r => r is not ToughBandages) || relics.Count > 1
            || combat.CurrentSide != player.Creature.Side)
            throw new NotSupportedException("Compact prototype admits only the discard-block relic in player phase.");
        foreach (AbstractModel listener in combat.IterateHookListeners())
        {
            // Native listeners can add effects even when no Power exists. Reject any override
            // on a hook reached by this program, independently of encounter/model identity.
            foreach (MethodInfo method in listener.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                if (ReachedHooks.Contains(method.Name) && method.GetBaseDefinition().DeclaringType == typeof(AbstractModel)
                    && method.DeclaringType != typeof(AbstractModel) && !RepresentedHook(listener.GetType(), method.Name))
                    throw new NotSupportedException($"Compact prototype has no effect program for {listener.Id.Entry}.{method.Name}.");
            }
        }
        PredictedCard[] cards = state.AllCards.ToArray();
        _identities = cards.Select(c => c.Original).ToArray();
        ResumableDiscardProgram.Card[] definitions = cards.Select(card => Capture(card.Preview)).ToArray();
        _inferred = cards.Select(card => CardOnPlayMirrors.DescribeDispatch(card.Preview) switch
        {
            MirrorDispatchKind.Handled => false,
            MirrorDispatchKind.Inferred => true,
            _ => throw new NotSupportedException($"Compact compatibility projection has no legacy OnPlay support for {card.Preview.Id.Entry}.")
        }).ToArray();
        int Index(PredictedCard card) => Array.IndexOf(_identities, card.Original);
        IReadOnlyList<int>[] piles = [state.Hand.Cards.Select(Index).ToArray(), state.DrawPile.Cards.Select(Index).ToArray(),
            state.DiscardPile.Cards.Select(Index).ToArray(), state.PlayPile.Cards.Select(Index).ToArray(),
            state.ExhaustPile.Cards.Select(Index).ToArray()];
        decimal block = relics.Count == 0 ? 0 : relics[0].DynamicVars.Block.BaseValue;
        if (block != decimal.Truncate(block) || block < 0 || block > 999_999_999m)
            throw new NotSupportedException("Compact prototype requires integral discard block.");
        Program = new(definitions, piles, state.Energy, root.State.GetCreature(player.Creature).Block, (int)block);
    }

    private static ResumableDiscardProgram.Card Capture(CardModel card)
    {
        if (card is not (Acrobatics or Prepared or StrikeSilent or DefendSilent)
            || card.Enchantment != null || card.Affliction != null || card.BaseReplayCount != 0
            || card.ExhaustOnNextPlay || card.IsDupe || card.IsClone || card.HasBeenRemovedFromState
            || card.EnergyCost.CostsX || card.EnergyCost._localModifiers.Count != 0
            || card.HasStarCostX || card.CurrentStarCost > 0 || card._temporaryStarCosts.Count != 0
            || card.CurrentTarget != null || card.CurrentPlayIndex != 0 || card.LastStarsSpent != 0
            || card.LocalKeywords.Any(k => k != CardKeyword.Sly)
            || card.IsSlyThisTurn && card is not Prepared)
            throw new NotSupportedException($"Compact prototype cannot admit card state {card.Id.Entry}.");
        decimal draw = card is Acrobatics or Prepared ? card.DynamicVars.Cards.BaseValue : 0;
        if (draw != decimal.Truncate(draw) || draw < 0 || draw > 10 || card.EnergyCost._base < 0
            || card is Acrobatics or Prepared && draw == 0)
            throw new NotSupportedException($"Compact prototype cannot admit card variables {card.Id.Entry}.");
        return new(card.EnergyCost._base, (int)draw, card is Acrobatics ? 1 : (int)draw, card.IsSlyThisTurn);
    }

    internal int Identity(string cardId) => Array.FindIndex(_identities, c => c.Id.Entry == cardId);
    internal int CardCount => _identities.Length;
    internal CardModel Original(int identity) => _identities[identity];
    internal int IndexOf(CardModel original) => Array.IndexOf(_identities, original);

    internal CombatPredictionSimulator Materialize(ResumableDiscardProgram program, CompactPhaseProbe? probe = null)
    {
        if (!Program.State.HasSameRoot(program.State) || !program.Complete)
            throw new InvalidOperationException("Evaluation requires a completed candidate from this root.");
        var forkStart = probe?.Begin() ?? default;
        CombatPredictionSimulator projection = _root.Fork();
        probe?.End(CompactProfilePhase.RootFork, forkStart);
        var eventsStart = probe?.Begin() ?? default;
        var combat = (SimulatedCombatState)projection.State.CombatState;
        SimPlayerCombatState state = projection.State.GetPlayerCombatState(_player);
        PredictedCard[] cards = _identities.Select(c => projection.State.FindCard(c)
            ?? throw new InvalidOperationException("Projection lost a root instance.")).ToArray();
        var stack = new Stack<(int Identity, CardPlay Play, PredictionTrace.TraceScope Scope, PredictionTrace.TraceScope? Method)>();
        try
        {
            for (int index = 0; index < program.EventCount; index++)
            {
                ResumableDiscardProgram.Event item = program.EventAt(index);
                PredictedCard card = cards[item.Card];
                switch (item.Kind)
                {
                    case ResumableDiscardProgram.EventKind.Pay:
                        state.LoseEnergy(item.Value);
                        combat.RecordEnergySpent(_player, item.Value);
                        card.MutablePreview.LastStarsSpent = 0;
                        break;
                    case ResumableDiscardProgram.EventKind.Start:
                    {
                        var scope = projection.PushActionSource(card.Original, PredictionActionKind.CardPlay);
                        CardModel preview = card.MutablePreview;
                        preview.CurrentTarget = null;
                        preview.CurrentPlayIndex = 0;
                        preview.LastStarsSpent = 0;
                        projection.AddToPile(card, PileType.Play);
                        CardPlay play = new()
                        {
                            Card = preview, Player = _player, Target = null, ResultPile = PileType.Discard,
                            Resources = new ResourceInfo { EnergyValue = item.Value, EnergySpent = item.Automatic ? 0 : item.Value,
                                StarsSpent = 0, StarValue = 0 },
                            IsAutoPlay = item.Automatic, PlayIndex = 0, PlayCount = 1
                        };
                        projection.History.CardPlayStarted(card, play);
                        ((ICombatPredictionCardExecutionSink)combat).RecordCardPlayStarted(card, play);
                        stack.Push((item.Card, play, scope, projection.PushMethodSource(card.Original, OnPlay)));
                        if (_inferred[item.Card]) projection.History.RecordRisk(PredictionRiskReason.MethodMirrorIncomplete);
                        break;
                    }
                    case ResumableDiscardProgram.EventKind.Draw:
                        projection.AddToPile(card, PileType.Hand);
                        var entry = projection.History.CardDrawn(card, false);
                        combat.RecordCardDrawn(card, false);
                        projection.History.CardDrawResolved(entry, card);
                        break;
                    case ResumableDiscardProgram.EventKind.Discard:
                        projection.AddToPile(card, PileType.Discard);
                        combat.RecordCardDiscarded(_player.Creature);
                        break;
                    case ResumableDiscardProgram.EventKind.Block:
                        projection.State.GetCreature(_player.Creature).GainBlock(item.Value);
                        break;
                    case ResumableDiscardProgram.EventKind.Finish:
                    {
                        var active = stack.Pop();
                        if (active.Identity != item.Card) throw new InvalidOperationException("Unbalanced compact history.");
                        active.Method?.Dispose();
                        projection.History.CardPlayFinished(card, active.Play, false);
                        combat.RecordCardPlayed(card, item.Value != 0);
                        combat.RecordCardLifecycle(projection, card);
                        projection.AddToPile(card, PileType.Discard);
                        card.InvalidateCaches();
                        active.Scope.Dispose();
                        break;
                    }
                    case ResumableDiscardProgram.EventKind.Select:
                        var selecting = stack.Pop();
                        selecting.Method?.Dispose();
                        stack.Push((selecting.Identity, selecting.Play, selecting.Scope, null));
                        break;
                    case ResumableDiscardProgram.EventKind.SelectedCard:
                        // Choice metadata is part of the compact execution tape. Ordinary native
                        // hand-discard selections do not add prediction history entries.
                        break;
                    default:
                        throw new InvalidOperationException("Unknown compact projection event.");
                }
            }
            if (stack.Count != 0) throw new InvalidOperationException("Compact history did not finish.");
        }
        finally
        {
            while (stack.TryPop(out var active)) { active.Method?.Dispose(); active.Scope.Dispose(); }
        }
        probe?.End(CompactProfilePhase.ProjectEvents, eventsStart);
        return projection;
    }

    internal void AssertValues(ResumableDiscardProgram program, CombatPredictionSimulator simulator)
    {
        SimPlayerCombatState state = simulator.State.GetPlayerCombatState(_player);
        if (program.Energy != state.Energy || program.Block != simulator.State.GetCreature(_player.Creature).Block)
            throw new InvalidOperationException("Compact values disagree with projected resources.");
        SimCardPile[] piles = [state.Hand, state.DrawPile, state.DiscardPile, state.PlayPile, state.ExhaustPile];
        for (int pile = 0; pile < piles.Length; pile++)
        {
            if (program.Count((ResumableDiscardProgram.Pile)pile) != piles[pile].Cards.Count)
                throw new InvalidOperationException("Compact values disagree with projected pile count.");
            for (int i = 0; i < piles[pile].Cards.Count; i++)
                if (!ReferenceEquals(_identities[program.CardAt((ResumableDiscardProgram.Pile)pile, i)], piles[pile].Cards[i].Original))
                    throw new InvalidOperationException("Compact values disagree with projected instance order.");
        }
    }

    private static readonly HashSet<string> ReachedHooks = new(StringComparer.Ordinal)
    {
        "ShouldPlay", "ShouldDraw", "ShouldPayExcessEnergyCostWithStars", "TryModifyEnergyCostInCombat",
        "TryModifyEnergyCostInCombatLate", "TryModifyStarCost", "AfterEnergySpent", "AfterStarsSpent", "ModifyCardPlayResultLocation",
        "AfterModifyingCardPlayResultLocation", "ModifyCardPlayCount", "AfterModifyingCardPlayCount",
        "BeforeCardPlayed", "AfterCardPlayed", "AfterCardPlayedLate", "AfterCardDrawn", "AfterCardDiscarded", "AfterHandEmptied",
        "BeforeBlockGained", "ModifyBlockAdditive", "ModifyBlockMultiplicative", "AfterModifyingBlockAmount", "AfterBlockGained",
        "TryModifyKeywordsInCombat", "ModifyMaxHandSize", "BeforeCardAutoPlayed", "AfterCardChangedPiles"
    };

    // Keep these exact method/type pairs aligned with the explicitly ignored registrations in
    // AfterCardPlayedMirrors. Badge/achievement progress is outside combat equivalence.
    private static bool RepresentedHook(Type type, string method)
        => type == typeof(ToughBandages) && method == nameof(AbstractModel.AfterCardDiscarded)
            || type == typeof(MultiplayerScalingModel) && method == nameof(AbstractModel.ModifyBlockMultiplicative)
            || method == nameof(AbstractModel.AfterCardPlayed)
            && (type == typeof(CccComboModel) || type == typeof(Play20CardsSingleTurnAchievement)
                || type == typeof(SkillSilent1Achievement));

    private static readonly MirrorMethodSpec OnPlay = new(typeof(CardModel), "OnPlay",
        BindingFlags.Instance | BindingFlags.NonPublic, [typeof(PlayerChoiceContext), typeof(CardPlay)]);
}
