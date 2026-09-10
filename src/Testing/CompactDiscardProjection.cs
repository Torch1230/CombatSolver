using System.Reflection;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.ValueProps;
using System.Collections.Concurrent;
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
using MegaCrit.Sts2.Core.Models.Powers;
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
    private readonly Creature[] _creatures;
    private readonly PowerModel[] _powerTemplates;
    internal int PlayerTurn => ((SimulatedCombatState)_root.State.CombatState).GetPlayerTurnNumber(_player);

    internal CompactDiscardProjection(CombatPredictionSimulator root, Player player, bool includeAttacks = false)
    {
        _root = root;
        _player = player;
        var combat = (SimulatedCombatState)root.State.CombatState;
        SimPlayerCombatState state = root.State.GetPlayerCombatState(player);
        var powers = combat.EffectivePowers();
        if (combat.Players.Count != 1 || powers.Any(p => !(p is StratagemPower && p.Owner == player.Creature && p.Amount is >= 1 and <= 10)
                && !(includeAttacks ? IsBasicPower(p) : p is StrengthPower && p.Owner != player.Creature))
            || combat.RootRunModSubscriberCount != 0 || combat.RootCombatModSubscriberCount != 0
            || combat.RootHasBaseLibCardModifiers || combat.RootRunHookListenerCount != 0
            || state.OrbQueue.Orbs.Count != 0 || root.GetMaxHandSize(player) != 10
            || root.HasPendingChoice || root.IsOverOrEnding)
            throw new NotSupportedException("Compact prototype requires an idle root with only player Stratagem, without deck listeners or mod subscribers.");
        var relics = combat.RelicsOf(player);
        if (relics.Any(r => r is not (ToughBandages or TheAbacus) || r.IsMelted)
            || relics.Select(r => r.GetType()).Distinct().Count() != relics.Count || powers.OfType<StratagemPower>().Count() > 1
            || combat.CurrentSide != player.Creature.Side)
            throw new NotSupportedException("Compact prototype admits only the discard-block relic in player phase.");
        foreach (AbstractModel listener in combat.IterateHookListeners())
        {
            // Native listeners can add effects even when no Power exists. Reject any override
            // on a hook reached by this program, independently of encounter/model identity.
            string[] unrepresented = HookAudit.GetOrAdd(listener.GetType(), static type => type
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(method => ReachedHooks.Contains(method.Name) && method.GetBaseDefinition().DeclaringType == typeof(AbstractModel)
                    && method.DeclaringType != typeof(AbstractModel) && !RepresentedHook(type, method.Name))
                .Select(method => method.Name).ToArray());
            if (unrepresented.Length != 0)
                throw new NotSupportedException($"Compact prototype has no effect program for {listener.Id.Entry}.{unrepresented[0]}.");
        }
        _creatures = includeAttacks ? [player.Creature, .. combat.Enemies] : [];
        if (includeAttacks && (combat.PlayerCreatures.Count != 1 || combat.KnownEnemies.Count != combat.Enemies.Count
            || _creatures.Any(c => root.State.GetCreature(c).IsDead || c.PetOwner != null)
            || combat.KnownEnemies.Any(c => combat.HasCompletedDeathEffects(c)
                || !((ICombatPredictionCreatureSemantics)combat).IsPrimaryEnemy(c)
                || !((ICombatPredictionCreatureSemantics)combat).ShouldRemoveAfterDeath(c))))
            throw new NotSupportedException("Compact attack requires living primary enemies without pending deaths or pets.");
        _powerTemplates = includeAttacks ? CapturePowerTemplates(combat, powers) : [];
        PowerModel[] rootPowerOrder = powers.ToArray();
        BasicPowerDefinition[]? powerDefinitions = includeAttacks ? _powerTemplates.Select(power => new BasicPowerDefinition(
            BasicKind(power), CreatureIndex(power.Owner), power.Amount, power.Applier == null ? -1 : CreatureIndex(power.Applier),
            Array.IndexOf(rootPowerOrder, power) + 1,
            power is WeakPower ? power.DynamicVars["DamageDecrease"].BaseValue
                : power is VulnerablePower ? power.DynamicVars["DamageIncrease"].BaseValue : 1m,
            combat.IsCapturedRootPowerSlot(power))).ToArray() : null;
        PredictedCard[] cards = state.AllCards.ToArray();
        _identities = cards.Select(c => c.Original).ToArray();
        ResumableDiscardProgram.Card[] definitions = cards.Select(card => Capture(card.Preview, includeAttacks)).ToArray();
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
        static int Block(RelicModel? relic)
        {
            decimal value = relic?.DynamicVars.Block.BaseValue ?? 0;
            if (value != decimal.Truncate(value) || value < 0 || value > 999_999_999m)
                throw new NotSupportedException("Compact prototype requires integral relic block.");
            return (int)value;
        }
        int[] comparisons = cards.SelectMany(left => cards.Select(left.CompareTo)).ToArray();
        PredictionRngState rng = root.Rng.Shuffle.CaptureState();
        AbstractModel[] listeners = combat.IterateHookListeners().ToArray();
        int abacusIndex = Array.FindIndex(listeners, p => p is TheAbacus);
        int stratagemIndex = Array.FindIndex(listeners, p => p is StratagemPower);
        Program = new(definitions, piles, state.Energy, root.State.GetCreature(player.Creature).Block,
            Block(relics.OfType<ToughBandages>().SingleOrDefault()),
            new(rng.Counter, rng.State0, rng.State1, rng.State2, rng.State3), comparisons,
            powers.OfType<StratagemPower>().SingleOrDefault()?.Amount ?? 0,
            Block(relics.OfType<TheAbacus>().SingleOrDefault()), abacusIndex >= 0 && abacusIndex < stratagemIndex,
            includeAttacks ? _creatures.Select(c => { var v = root.State.GetCreature(c); return new CreatureVitals(v.CurrentHp, v.MaxHp, v.Block); }).ToArray() : null, powerDefinitions);
    }

    private static ResumableDiscardProgram.Card Capture(CardModel card, bool includeAttacks)
    {
        if (card is not (Acrobatics or Prepared or Backflip or StrikeSilent or DefendSilent or Neutralize)
            || card is Neutralize && !includeAttacks
            || card.Enchantment != null || card.Affliction != null || card.BaseReplayCount != 0
            || card.ExhaustOnNextPlay || card.IsDupe || card.IsClone || card.HasBeenRemovedFromState
            || card.EnergyCost.CostsX || card.EnergyCost._localModifiers.Count != 0
            || card.HasStarCostX || card.CurrentStarCost > 0 || card._temporaryStarCosts.Count != 0
            || card.CurrentTarget != null || card.CurrentPlayIndex != 0 || card.LastStarsSpent != 0
            || card.ShouldRetainThisTurn || card.HasTurnEndInHandEffect
            || card.LocalKeywords.Any(k => k != CardKeyword.Sly)
            || card.IsSlyThisTurn && card is not Prepared)
            throw new NotSupportedException($"Compact prototype cannot admit card state {card.Id.Entry}.");
        decimal draw = card is Acrobatics or Prepared or Backflip ? card.DynamicVars.Cards.BaseValue : 0;
        decimal damage = includeAttacks && card is StrikeSilent or Neutralize ? card.DynamicVars.Damage.BaseValue : 0;
        decimal block = card is DefendSilent or Backflip ? card.DynamicVars.Block.BaseValue : 0;
        decimal weak = card is Neutralize ? card.DynamicVars.Weak.BaseValue : 0;
        if (draw != decimal.Truncate(draw) || draw < 0 || draw > 10 || card.EnergyCost._base < 0
            || card is Acrobatics or Prepared or Backflip && draw == 0
            || damage != decimal.Truncate(damage) || damage is < 0 or > 999_999_999m
            || block != decimal.Truncate(block) || block is < 0 or > 999_999_999m
            || weak != decimal.Truncate(weak) || weak is < 0 or > 999_999_999m)
            throw new NotSupportedException($"Compact prototype cannot admit card variables {card.Id.Entry}.");
        return new(card.EnergyCost._base, (int)draw, card is Acrobatics ? 1 : card is Prepared ? (int)draw : 0,
            card.IsSlyThisTurn, (int)block, (int)damage, includeAttacks && card is StrikeSilent or Neutralize,
            (int)weak, card is DefendSilent or Backflip);
    }

    private PowerModel[] CapturePowerTemplates(SimulatedCombatState combat, IReadOnlyList<PowerModel> powers)
    {
        List<PowerModel> result = powers.Where(IsBasicPower).ToList();
        if (result.GroupBy(p => (p.Owner, p.GetType())).Any(g => g.Count() != 1)
            || result.Any(p => CreatureIndex(p.Owner) < 0 || p.Applier != null && CreatureIndex(p.Applier) < 0
                || p is not (StrengthPower or DexterityPower) && p.Amount < 0))
            throw new NotSupportedException("Basic Power state has duplicate slots, unknown ownership or negative debuffs.");
        foreach (Creature owner in _creatures)
        foreach (BasicPowerKind kind in Enum.GetValues<BasicPowerKind>())
        {
            if (result.Any(p => p.Owner == owner && BasicKind(p) == kind)) continue;
            PowerModel prototype = kind switch
            {
                BasicPowerKind.Strength => CanonicalModels.Power<StrengthPower>(),
                BasicPowerKind.Dexterity => CanonicalModels.Power<DexterityPower>(),
                BasicPowerKind.Weak => CanonicalModels.Power<WeakPower>(),
                BasicPowerKind.Vulnerable => CanonicalModels.Power<VulnerablePower>(),
                BasicPowerKind.Frail => CanonicalModels.Power<FrailPower>(),
                _ => throw new InvalidOperationException("Unknown basic Power kind.")
            };
            PowerModel template = PredictionUtils.CloneModelForSimulation(prototype);
            template._owner = owner; template._target = null; template._applier = null; template._amount = 0;
            template.AmountOnTurnStart = 0;
            result.Add(template);
        }
        return result.ToArray();
    }

    private static bool IsBasicPower(PowerModel power) => power is StrengthPower or DexterityPower or WeakPower or VulnerablePower or FrailPower;
    private static BasicPowerKind BasicKind(PowerModel power) => power switch
    {
        StrengthPower => BasicPowerKind.Strength, DexterityPower => BasicPowerKind.Dexterity, WeakPower => BasicPowerKind.Weak,
        VulnerablePower => BasicPowerKind.Vulnerable, FrailPower => BasicPowerKind.Frail,
        _ => throw new InvalidOperationException("Power has no compact basic kind.")
    };
    internal SimulatedCombatState.CompletedPowerReadBinding CreatePowerReadBinding(CombatPredictionSimulator context)
        => new((SimulatedCombatState)context.State.CombatState, _powerTemplates);
    internal void CopyPowerReadValues(ResumableDiscardProgram program, CompletedPowerReadValues[] target)
    {
        if (target.Length != program.PowerCount) throw new ArgumentException("Power read buffer has the wrong size.");
        for (int index = 0; index < target.Length; index++)
        {
            var value = program.Power(index);
            target[index] = new(value.Amount, value.Applier < 0 ? null : _creatures[value.Applier], value.Order, value.Retired);
        }
    }

    // Legacy read helpers contain simulator-owned scratch. Each lane borrows its own root fork;
    // this is setup once per reader, never a leaf projection or a second execution authority.
    internal CompactDiscardReadView CreateReadView(bool reuseInvariantFeatures = true)
        => new(this, _root.Fork(), _player, _inferred) { Invariants = reuseInvariantFeatures ? new() : null };

    internal int Identity(string cardId) => Array.FindIndex(_identities, c => c.Id.Entry == cardId);
    internal Creature Creature(int index) => _creatures[index];
    internal int CreatureIndex(Creature creature) => Array.IndexOf(_creatures, creature);
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
        var damageResults = new Dictionary<int, DamageResult>();
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
                        preview.CurrentTarget = item.Target < 0 ? null : _creatures[item.Target];
                        preview.CurrentPlayIndex = 0;
                        preview.LastStarsSpent = 0;
                        projection.AddToPile(card, PileType.Play);
                        CardPlay play = new()
                        {
                            Card = preview, Player = _player, Target = preview.CurrentTarget, ResultPile = PileType.Discard,
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
                    case ResumableDiscardProgram.EventKind.PowerChange:
                    case ResumableDiscardProgram.EventKind.Shuffle:
                        break;
                    case ResumableDiscardProgram.EventKind.ShuffleCard:
                        projection.AddToPile(card, PileType.Draw);
                        break;
                    case ResumableDiscardProgram.EventKind.Retrieve:
                        projection.AddToPile(card, PileType.Hand);
                        break;
                    case ResumableDiscardProgram.EventKind.Discard:
                        projection.AddToPile(card, PileType.Discard);
                        combat.RecordCardDiscarded(_player.Creature);
                        break;
                    case ResumableDiscardProgram.EventKind.Block:
                        projection.State.GetCreature(_player.Creature).GainBlock(item.Value);
                        break;
                    case ResumableDiscardProgram.EventKind.Damage:
                    {
                        var blocked = program.EventAt(++index);
                        var overkill = program.EventAt(++index);
                        if (blocked.Kind != ResumableDiscardProgram.EventKind.DamageBlocked
                            || overkill.Kind != ResumableDiscardProgram.EventKind.DamageOverkill
                            || blocked.Target != item.Target || overkill.Target != item.Target)
                            throw new InvalidOperationException("Malformed committed damage result.");
                        Creature target = _creatures[item.Target];
                        DamageResult result = new(target, ValueProp.Move)
                        {
                            UnblockedDamage = item.Value, BlockedDamage = blocked.Value, OverkillDamage = overkill.Value,
                            WasTargetKilled = (item.Flags & 1) != 0, WasBlockBroken = (item.Flags & 2) != 0,
                            WasFullyBlocked = (item.Flags & 4) != 0
                        };
                        projection.History.DamageReceived(target, _player.Creature, result, card, projection.ResolveDamageSource(card));
                        combat.RecordDamageReceived(target, _player.Creature, result);
                        damageResults[item.Card] = result;
                        break;
                    }
                    case ResumableDiscardProgram.EventKind.Death:
                        projection.State.RemoveCreature(_creatures[item.Target]);
                        combat.CompleteDeathPhase(_creatures[item.Target]);
                        break;
                    case ResumableDiscardProgram.EventKind.AttackFinish:
                        projection.History.CreatureAttacked(_player.Creature, [damageResults[item.Card]]);
                        combat.RecordCreatureAttacked(_player.Creature);
                        damageResults.Remove(item.Card);
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
                        card.MutablePreview.CurrentTarget = null;
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
        if (program.PowerCount > 0)
        {
            var binding = CreatePowerReadBinding(projection);
            var values = new CompletedPowerReadValues[program.PowerCount];
            CopyPowerReadValues(program, values);
            binding.Read(values, Enumerable.Range(1, program.CreatureCount - 1)
                .Where(program.CreaturePresent).Select(Creature).ToArray());
        }
        for (int index = 0; index < program.CreatureCount; index++)
        {
            CreatureVitals values = program.Creature(index);
            SimCreatureState target = projection.State.GetCreature(_creatures[index]);
            target.CurrentHp = values.CurrentHp;
            target.DamageBlock(target.Block, ValueProp.Unpowered);
            target.GainBlock(values.Block);
        }
        if (program.Terminal)
        {
            Terminal.SetValue(projection, new CombatTerminalStamp(PlayerTurn, CombatTerminalOutcome.Victory));
            InProgress.SetValue(projection, false);
        }
        ValueShuffleRng rng = program.ShuffleRng;
        projection.Rng.Shuffle.LoadFromSerializable(new()
            { counter = rng.Counter, state0 = rng.State0, state1 = rng.State1, state2 = rng.State2, state3 = rng.State3 });
        ShuffleEvents.SetValue(projection, _root.ShuffleEventCount + program.ShuffleCount);
        probe?.End(CompactProfilePhase.ProjectEvents, eventsStart);
        return projection;
    }

    internal void AssertValues(ResumableDiscardProgram program, CombatPredictionSimulator simulator)
    {
        SimPlayerCombatState state = simulator.State.GetPlayerCombatState(_player);
        if (program.Energy != state.Energy || program.Block != simulator.State.GetCreature(_player.Creature).Block)
            throw new InvalidOperationException("Compact values disagree with projected resources.");
        PredictionRngState rng = simulator.Rng.Shuffle.CaptureState();
        if (program.ShuffleRng != new ValueShuffleRng(rng.Counter, rng.State0, rng.State1, rng.State2, rng.State3)
            || _root.ShuffleEventCount + program.ShuffleCount != simulator.ShuffleEventCount)
            throw new InvalidOperationException("Compact values disagree with shuffle state or event count.");
        for (int index = 0; index < program.CreatureCount; index++)
        {
            Creature creature = _creatures[index];
            CreatureReadValues actual = CreatureReadValues.Capture(simulator, creature);
            CreatureVitals expected = program.Creature(index);
            if (actual != new CreatureReadValues(expected.CurrentHp, expected.MaxHp, expected.Block, program.CreaturePresent(index))
                || ((SimulatedCombatState)simulator.State.CombatState).HasCompletedDeathEffects(creature) != program.CreatureDeathCompleted(index))
                throw new InvalidOperationException("Compact creature values or death lifecycle differ.");
        }
        IReadOnlyList<PowerModel> powers = ((SimulatedCombatState)simulator.State.CombatState).EffectivePowers();
        for (int index = 0; index < program.PowerCount; index++)
        {
            var definition = program.PowerDefinition(index);
            var expected = program.Power(index);
            PowerModel? actual = powers.SingleOrDefault(power => ReferenceEquals(power.Owner, _creatures[definition.Owner])
                && power.GetType() == _powerTemplates[index].GetType());
            if ((actual?.Amount ?? 0) != expected.Amount || actual != null
                && !ReferenceEquals(actual.Applier, expected.Applier < 0 ? null : _creatures[expected.Applier]))
                throw new InvalidOperationException($"Compact Power values differ: owner={definition.Owner}, kind={definition.Kind}.");
        }
        SimCardPile[] piles = [state.Hand, state.DrawPile, state.DiscardPile, state.PlayPile, state.ExhaustPile];
        for (int pile = 0; pile < piles.Length; pile++)
        {
            if (program.Count((ResumableDiscardProgram.Pile)pile) != piles[pile].Cards.Count)
                throw new InvalidOperationException($"Compact pile {pile} count differs: {program.Count((ResumableDiscardProgram.Pile)pile)} / {piles[pile].Cards.Count}.");
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
        "TryModifyKeywordsInCombat", "ModifyMaxHandSize", "ModifyUnblockedDamageTarget",
        "ModifyHpLostBeforeOsty", "ModifyHpLostBeforeOstyLate", "ModifyHpLostAfterOsty", "ModifyHpLostAfterOstyLate",
        "ModifyDamageAdditive", "ModifyDamageMultiplicative", "BeforeCardAutoPlayed", "AfterCardChangedPiles",
        "ModifyShuffleOrder", "AfterShuffle", "BeforeAttack", "AfterAttack", "ModifyAttackHitCount",
        "BeforeDamageReceived", "AfterBlockBroken", "AfterCurrentHpChanged", "AfterDamageGiven", "AfterDamageReceived",
        "AfterModifyingHpLostAfterOsty", "BeforeDeath", "ShouldDie", "AfterDeath", "ShouldCreatureBeRemovedFromCombatAfterDeath",
        "ShouldAllowHitting", "BeforePowerAmountChanged", "ModifyPowerAmountGiven", "ModifyPowerAmountReceived",
        "AfterModifyingPowerAmountGiven", "AfterModifyingPowerAmountReceived", "AfterPowerAmountChanged"
    };
    // Only immutable CLR method/type metadata is shared. Every root still checks subscriber,
    // Power, relic, card-instance, resource, and lifecycle values independently.
    private static readonly ConcurrentDictionary<Type, string[]> HookAudit = new();

    // Keep exact method/type pairs. AfterCardPlayed badges match the ignored mirror registrations;
    // DebufferModel only increments the native run badge counter, outside combat equivalence.
    private static bool RepresentedHook(Type type, string method)
        => type == typeof(ToughBandages) && method == nameof(AbstractModel.AfterCardDiscarded)
            || type == typeof(StratagemPower) && method == nameof(AbstractModel.AfterShuffle)
            || type == typeof(TheAbacus) && method == nameof(AbstractModel.AfterShuffle)
            || type == typeof(StrengthPower) && method == nameof(AbstractModel.ModifyDamageAdditive)
            || type == typeof(DexterityPower) && method == nameof(AbstractModel.ModifyBlockAdditive)
            || (type == typeof(WeakPower) || type == typeof(VulnerablePower)) && method == nameof(AbstractModel.ModifyDamageMultiplicative)
            || type == typeof(FrailPower) && method == nameof(AbstractModel.ModifyBlockMultiplicative)
            || type == typeof(DebufferModel) && method == nameof(AbstractModel.AfterPowerAmountChanged)
            || type == typeof(MultiplayerScalingModel) && method == nameof(AbstractModel.ModifyBlockMultiplicative)
            || method == nameof(AbstractModel.AfterCardPlayed)
            && (type == typeof(CccComboModel) || type == typeof(Play20CardsSingleTurnAchievement)
                || type == typeof(SkillSilent1Achievement));

    private static readonly MirrorMethodSpec OnPlay = new(typeof(CardModel), "OnPlay",
        BindingFlags.Instance | BindingFlags.NonPublic, [typeof(PlayerChoiceContext), typeof(CardPlay)]);
    private static readonly PropertyInfo Terminal = typeof(CombatPredictionSimulator).GetProperty(nameof(CombatPredictionSimulator.TerminalStamp))!;
    private static readonly PropertyInfo InProgress = typeof(CombatPredictionSimulator).GetProperty(nameof(CombatPredictionSimulator.IsInProgress))!;
    private static readonly PropertyInfo ShuffleEvents = typeof(CombatPredictionSimulator)
        .GetProperty(nameof(CombatPredictionSimulator.ShuffleEventCount))!;
}
