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
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Monsters;
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
    private readonly CardModel[] _identities, _definitionModels;
    private readonly PredictionRiskReason?[] _risks;
    internal readonly ResumableDiscardProgram Program;
    internal bool CardValuesInvariant { get; }
    internal bool HasMonsterMoves { get; }
    private readonly Creature[] _creatures;
    private readonly PowerModel[] _powerTemplates;
    internal int PlayerTurn => ((SimulatedCombatState)_root.State.CombatState).GetPlayerTurnNumber(_player);

    internal CompactDiscardProjection(CombatPredictionSimulator root, Player player, bool includeAttacks = false, bool includeHandEnd = false, bool includeMechaMoves = false)
    {
        HasMonsterMoves = includeMechaMoves;
        _root = root;
        _player = player;
        var combat = (SimulatedCombatState)root.State.CombatState;
        SimPlayerCombatState state = root.State.GetPlayerCombatState(player);
        var powers = combat.EffectivePowers();
        if (combat.Players.Count != 1 || powers.Any(p => !(p is StratagemPower && p.Owner == player.Creature && p.Amount is >= 1 and <= 10)
                && !(includeAttacks ? IsBasicPower(p) && (p is not (BlockNextTurnPower or ToolsOfTheTradePower) || p.Owner == player.Creature)
                    && (p is not PiercingWailPower || p.Owner != player.Creature)
                    : p is StrengthPower && p.Owner != player.Creature))
            || combat.RootRunModSubscriberCount != 0 || combat.RootCombatModSubscriberCount != 0
            || combat.RootHasBaseLibCardModifiers
            || state.OrbQueue.Orbs.Count != 0 || root.GetMaxHandSize(player) != 10
            || root.HasPendingChoice || root.IsOverOrEnding)
            throw new NotSupportedException("Compact prototype requires an idle root with admitted Powers and no mod subscribers.");
        var relics = combat.RelicsOf(player);
        if (relics.Any(r => r is not (ToughBandages or TheAbacus or RingOfTheSnake) || r.IsMelted)
            || relics.Select(r => r.GetType()).Distinct().Count() != relics.Count || powers.OfType<StratagemPower>().Count() > 1
            || combat.CurrentSide != player.Creature.Side)
            throw new NotSupportedException("Compact prototype requires admitted relics in player phase.");
        // Deck cards are a separate immutable listener prefix. Only run-scoped hooks
        // reach them; e.g. drawing a combat Slither must not invoke the deck copy.
        var runListeners = ((ICombatPredictionHookListenerSource)combat).RunHookListeners;
        for (int index = 0; index < combat.RootRunHookListenerCount; index++)
            AssertRepresentedHooks(runListeners[index], runPrefix: true, includeHandEnd);
        foreach (AbstractModel listener in combat.IterateHookListeners())
            AssertRepresentedHooks(listener, runPrefix: false, includeHandEnd);
        _creatures = includeAttacks ? [player.Creature, .. combat.Enemies] : [];
        if (includeAttacks && (combat.PlayerCreatures.Count != 1 || combat.KnownEnemies.Count != combat.Enemies.Count
            || _creatures.Any(c => root.State.GetCreature(c).IsDead || c.PetOwner != null)
            || combat.KnownEnemies.Any(c => combat.HasCompletedDeathEffects(c)
                || !((ICombatPredictionCreatureSemantics)combat).IsPrimaryEnemy(c)
                || !((ICombatPredictionCreatureSemantics)combat).ShouldRemoveAfterDeath(c))))
            throw new NotSupportedException("Compact attack requires living primary enemies without pending deaths or pets.");
        if (includeMechaMoves && (!includeAttacks || _creatures.Length != 2 || _creatures[1].Monster is not MechaKnight))
            throw new NotSupportedException("Captured Mecha commands require exactly one MechaKnight and creature values.");
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
        List<CardModel> generated = [];
        int shivTemplate = -1, inkyShivTemplate = -1, burnTemplate = -1;
        if (cards.Any(card => card.Preview is CloakAndDagger))
        {
            shivTemplate = cards.Length + generated.Count;
            generated.Add(PredictionUtils.CreateCard(CanonicalModels.Card<Shiv>(), player));
        }
        if (cards.Any(card => card.Preview is BladeOfInk))
        {
            inkyShivTemplate = cards.Length + generated.Count;
            CardModel template = PredictionUtils.CreateCard(CanonicalModels.Card<Shiv>(), player);
            PredictionUtils.EnchantCard(CanonicalModels.Enchantment<Inky>().ToMutable(), template, 1m);
            generated.Add(template);
        }
        if (includeMechaMoves)
        {
            burnTemplate = cards.Length + generated.Count;
            generated.Add(PredictionUtils.CreateCard(CanonicalModels.Card<Burn>(), player));
        }
        // Native BladeOfInk enchants after the entire generated batch. In this closed root
        // generation hooks have no observers, Inky has no OnEnchant/Modify effects and no
        // combat history event is emitted by enchanting. Capture the final immutable variant.
        _definitionModels = [.. cards.Select(card => card.Preview), .. generated];
        ResumableDiscardProgram.Card[] definitions = _definitionModels.Select(card => CompactCardProgramCompiler.Compile(card, includeAttacks, shivTemplate, inkyShivTemplate)).ToArray();
        _risks = _definitionModels.Select(card => card is Burn ? null : CardOnPlayMirrors.DescribeDispatch(card) switch
        {
            MirrorDispatchKind.Handled => (PredictionRiskReason?)null,
            MirrorDispatchKind.Inferred => PredictionRiskReason.MethodMirrorIncomplete,
            MirrorDispatchKind.Unsupported when CardOnPlayCompensationCatalog.Contains(card) => PredictionRiskReason.MethodNotMirrored,
            _ => throw new NotSupportedException($"Compact compatibility projection has no legacy OnPlay support for {card.Id.Entry}.")
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
        int[] comparisons = _definitionModels.SelectMany(left => _definitionModels.Select(left.CompareTo)).ToArray();
        PredictionRngState rng = root.Rng.Shuffle.CaptureState();
        PredictionRngState energyRng = root.Rng.CombatEnergyCosts.CaptureState();
        AbstractModel[] listeners = combat.IterateHookListeners().ToArray();
        int abacusIndex = Array.FindIndex(listeners, p => p is TheAbacus);
        int stratagemIndex = Array.FindIndex(listeners, p => p is StratagemPower);
        Program = new(definitions[..cards.Length], piles, state.Energy, root.State.GetCreature(player.Creature).Block,
            Block(relics.OfType<ToughBandages>().SingleOrDefault()),
            new(rng.Counter, rng.State0, rng.State1, rng.State2, rng.State3), comparisons,
            powers.OfType<StratagemPower>().SingleOrDefault()?.Amount ?? 0,
            Block(relics.OfType<TheAbacus>().SingleOrDefault()), abacusIndex >= 0 && abacusIndex < stratagemIndex,
            includeAttacks ? _creatures.Select(c => { var v = root.State.GetCreature(c); return new CreatureVitals(v.CurrentHp, v.MaxHp, v.Block); }).ToArray() : null, powerDefinitions, definitions[cards.Length..],
            new(energyRng.Counter, energyRng.State0, energyRng.State1, energyRng.State2, energyRng.State3), handEndAdmitted: includeHandEnd, monsterMoves: includeMechaMoves ? CaptureMechaCommands(root, burnTemplate) : null);
        CardValuesInvariant = Program.CardValuesInvariant;
    }

    internal static readonly string[] MechaMoveIds = ["CHARGE_MOVE", "FLAMETHROWER_MOVE", "WINDUP_MOVE", "HEAVY_CLEAVE_MOVE"];

    private MonsterEffectProgram[] CaptureMechaCommands(CombatPredictionSimulator root, int burnTemplate)
    {
        // Force only a disposable shadow's move. Attack parameters are already captured
        // by BranchMonsterStaticSnapshot; no worker consults live ascension or intents.
        var metadata = (SimulatedCombatState)root.Fork().State.CombatState;
        return MechaMoveIds.Select(id =>
        {
            metadata.ForceMonsterMove(_creatures[1], id);
            var move = metadata.CurrentMonsterMove(_creatures[1]);
            if (id == "WINDUP_MOVE")
            {
                if (move.AttackHits.Count != 0) throw new NotSupportedException("Mecha windup has unexpected attacks.");
                return new MonsterEffectProgram([new(MonsterInstructionKind.GainBlock, 15), new(MonsterInstructionKind.GainStrength, 5)]);
            }
            if (move.AttackHits.Count != 1) throw new NotSupportedException("Mecha command requires one captured attack.");
            MonsterInstruction attack = new(MonsterInstructionKind.AttackPlayer, move.AttackHits[0].BaseDamage);
            return id == "FLAMETHROWER_MOVE"
                ? new MonsterEffectProgram([attack, new(MonsterInstructionKind.GenerateCards, 4, burnTemplate)])
                : new MonsterEffectProgram([attack]);
        }).ToArray();
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
            // No admitted instruction creates Artifact. Only captured instances need slots.
            if (kind == BasicPowerKind.Artifact) continue;
            if (kind is BasicPowerKind.BlockNextTurn or BasicPowerKind.ToolsOfTheTrade && owner != _player.Creature) continue;
            if (kind == BasicPowerKind.PiercingWail && owner == _player.Creature) continue;
            if (result.Any(p => p.Owner == owner && BasicKind(p) == kind)) continue;
            PowerModel prototype = CanonicalPower(kind);
            PowerModel template = PredictionUtils.CloneModelForSimulation(prototype);
            template._owner = owner; template._target = null; template._applier = null; template._amount = 0;
            template.AmountOnTurnStart = 0;
            result.Add(template);
        }
        return result.ToArray();
    }

    private static bool IsBasicPower(PowerModel power) => power is StrengthPower or DexterityPower or WeakPower or VulnerablePower or FrailPower or PoisonPower or BlockNextTurnPower or ToolsOfTheTradePower or PiercingWailPower or ArtifactPower;
    private static PowerModel CanonicalPower(BasicPowerKind kind) => kind switch
    {
        BasicPowerKind.Strength => CanonicalModels.Power<StrengthPower>(),
        BasicPowerKind.Dexterity => CanonicalModels.Power<DexterityPower>(),
        BasicPowerKind.Weak => CanonicalModels.Power<WeakPower>(),
        BasicPowerKind.Vulnerable => CanonicalModels.Power<VulnerablePower>(),
        BasicPowerKind.Frail => CanonicalModels.Power<FrailPower>(),
        BasicPowerKind.Poison => CanonicalModels.Power<PoisonPower>(),
        BasicPowerKind.BlockNextTurn => CanonicalModels.Power<BlockNextTurnPower>(),
        BasicPowerKind.PiercingWail => CanonicalModels.Power<PiercingWailPower>(),
        BasicPowerKind.Artifact => CanonicalModels.Power<ArtifactPower>(),
        BasicPowerKind.ToolsOfTheTrade => CanonicalModels.Power<ToolsOfTheTradePower>(),
        _ => throw new InvalidOperationException("Unknown basic Power kind.")
    };
    private static BasicPowerKind BasicKind(PowerModel power) => power switch
    {
        StrengthPower => BasicPowerKind.Strength, DexterityPower => BasicPowerKind.Dexterity, WeakPower => BasicPowerKind.Weak,
        VulnerablePower => BasicPowerKind.Vulnerable, FrailPower => BasicPowerKind.Frail, PoisonPower => BasicPowerKind.Poison,
        PiercingWailPower => BasicPowerKind.PiercingWail, ArtifactPower => BasicPowerKind.Artifact,
        BlockNextTurnPower => BasicPowerKind.BlockNextTurn, ToolsOfTheTradePower => BasicPowerKind.ToolsOfTheTrade,
        _ => throw new InvalidOperationException("Power has no compact basic kind.")
    };
    internal SimulatedCombatState.CompletedPowerReadBinding CreatePowerReadBinding(CombatPredictionSimulator context)
        => new((SimulatedCombatState)context.State.CombatState, _powerTemplates, _powerTemplates.Select(power => CanonicalPower(BasicKind(power))).ToArray());
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
        => new(this, _root.Fork(), _player, _risks) { Invariants = reuseInvariantFeatures ? new() : null };

    private static PileType NativePile(ResumableDiscardProgram.Pile pile) => pile switch
    {
        ResumableDiscardProgram.Pile.Discard => PileType.Discard,
        ResumableDiscardProgram.Pile.Exhaust => PileType.Exhaust,
        ResumableDiscardProgram.Pile.Removed => PileType.None,
        _ => throw new InvalidOperationException("Unknown compact result location.")
    };

    internal int Identity(string cardId) => Array.FindIndex(_identities, c => c.Id.Entry == cardId);
    internal Creature Creature(int index) => _creatures[index];
    internal int CreatureIndex(Creature creature) => Array.IndexOf(_creatures, creature);
    internal int CardCount => _identities.Length;
    internal IReadOnlyList<CardModel> DefinitionModels => _definitionModels;
    internal PredictedCard CreateGeneratedCard(int definition) => definition >= _identities.Length && definition < _definitionModels.Length
        ? PredictedCard.FromGenerated(PredictionUtils.CloneCardStateForSimulation(_definitionModels[definition])) : throw new ArgumentOutOfRangeException(nameof(definition));
    internal CardModel Original(int identity) => _identities[identity];
    internal int IndexOf(CardModel original) => Array.IndexOf(_identities, original);
    internal Dictionary<CardModel, int> CaptureCardIdentities(CombatPredictionSimulator simulator)
    {
        var identities = _identities.Select((card, index) => (card, index)).ToDictionary(item => item.card, item => item.index);
        int generated = _identities.Length, prior = -2;
        foreach (var entry in simulator.History.Entries.OfType<CombatPredictionCardGeneratedEntry>())
            if (!identities.ContainsKey(entry.Card.Original))
                identities.Add(entry.Card.Original, entry.Index < _root.History.Entries.Count ? prior-- : generated++);
        return identities;
    }

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
        List<PredictedCard> cards = _identities.Select(c => projection.State.FindCard(c)
            ?? throw new InvalidOperationException("Projection lost a root instance.")).ToList();
        var damageResults = new Dictionary<int, DamageResult>();
        var stack = new Stack<(int Identity, CardPlay Play, PredictionTrace.TraceScope Scope, PredictionTrace.TraceScope? Method)>();
        PredictionTrace.TraceScope? handEndMethod = null;
        try
        {
            for (int index = 0; index < program.EventCount; index++)
            {
                ResumableDiscardProgram.Event item = program.EventAt(index);
                if (item.Kind == ResumableDiscardProgram.EventKind.Generated)
                {
                    if (item.Target == 0)
                    {
                        var creator = stack.Peek();
                        int creatorDefinition = program.DefinitionIndex(creator.Identity);
                        if (_definitionModels[creatorDefinition] is CloakAndDagger
                            && _risks[creatorDefinition] == PredictionRiskReason.MethodMirrorIncomplete && creator.Method != null)
                        {
                            // Legacy compensation emits generation after its inferred block mirror.
                            stack.Pop(); creator.Method?.Dispose();
                            stack.Push((creator.Identity, creator.Play, creator.Scope, null));
                        }
                    }
                    else if (item.Target != -1 || stack.Count != 0)
                        throw new InvalidOperationException("Generated event has an invalid creator or overlapping action.");
                    if (item.Card != cards.Count) throw new InvalidOperationException("Generated card identity is out of order.");
                    PredictedCard created = CreateGeneratedCard(item.Value);
                    cards.Add(created);
                    var generation = projection.History.CardGenerated(created, item.Target == 0 ? _player : null, CardGenerationResultKind.Fixed);
                    projection.AddToPile(created, PileType.Hand);
                    projection.History.CardGenerationResolved(generation, created);
                    continue;
                }
                // Creature commands share the committed damage and history representation.
                // Handle them before interpreting the source as a card instance.
                if (item.Kind == ResumableDiscardProgram.EventKind.Damage)
                {
                    AppendDamage(item, ref index);
                    continue;
                }
                if (item.Kind == ResumableDiscardProgram.EventKind.AttackFinish)
                {
                    Creature attacker = item.Card < 0 ? _creatures[-item.Card - 1] : _player.Creature;
                    projection.History.CreatureAttacked(attacker, [damageResults[item.Card]]);
                    combat.RecordCreatureAttacked(attacker);
                    damageResults.Remove(item.Card);
                    continue;
                }
                if (item.Kind == ResumableDiscardProgram.EventKind.Block)
                {
                    projection.State.GetCreature(item.Target < 0 ? _player.Creature : _creatures[item.Target]).GainBlock(item.Value);
                    continue;
                }
                if (item.Kind == ResumableDiscardProgram.EventKind.PowerChange) continue;
                if (item.Kind == ResumableDiscardProgram.EventKind.Death)
                {
                    if (item.Target == 0) projection.LoseCombat();
                    else projection.State.RemoveCreature(_creatures[item.Target]);
                    combat.CompleteDeathPhase(_creatures[item.Target]);
                    continue;
                }
                PredictedCard card = cards[item.Card];
                switch (item.Kind)
                {
                    case ResumableDiscardProgram.EventKind.HandEndMoved:
                        projection.AddToPile(card, PileType.Play);
                        break;
                    case ResumableDiscardProgram.EventKind.HandEndStart:
                        if (handEndMethod != null || stack.Count != 0) throw new InvalidOperationException("Hand-end method overlaps a card action.");
                        handEndMethod = projection.PushMethodSource(card.Original, OnTurnEndInHand);
                        break;
                    case ResumableDiscardProgram.EventKind.HandEndFinish:
                        if (handEndMethod == null) throw new InvalidOperationException("Hand-end method was not started.");
                        handEndMethod.Value.Dispose(); handEndMethod = null;
                        break;
                    case ResumableDiscardProgram.EventKind.Pay:
                        state.LoseEnergy(item.Value);
                        combat.RecordEnergySpent(_player, item.Value);
                        card.MutablePreview.LastStarsSpent = 0;
                        if (card.Preview.EnergyCost.CostsX) card.MutablePreview.EnergyCost.CapturedXValue = item.Value;
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
                            Card = preview, Player = _player, Target = preview.CurrentTarget, ResultPile = NativePile(program.ResultPile(item.Card)),
                            Resources = new ResourceInfo { EnergyValue = item.Value, EnergySpent = item.Automatic ? 0 : item.Value,
                                StarsSpent = 0, StarValue = 0 },
                            IsAutoPlay = item.Automatic, PlayIndex = 0, PlayCount = 1
                        };
                        projection.History.CardPlayStarted(card, play);
                        ((ICombatPredictionCardExecutionSink)combat).RecordCardPlayStarted(card, play);
                        PredictionTrace.TraceScope? method = projection.PushMethodSource(card.Original, OnPlay);
                        if (_risks[program.DefinitionIndex(item.Card)] is { } risk) projection.History.RecordRisk(risk);
                        if (_risks[program.DefinitionIndex(item.Card)] == PredictionRiskReason.MethodNotMirrored)
                        {
                            // Legacy compensation starts after the unsupported mirror scope.
                            // Its indirect history belongs to the enclosing card action.
                            method?.Dispose(); method = null;
                        }
                        stack.Push((item.Card, play, scope, method));
                        break;
                    }
                    case ResumableDiscardProgram.EventKind.Draw:
                        projection.AddToPile(card, PileType.Hand);
                        var entry = projection.History.CardDrawn(card, false);
                        combat.RecordCardDrawn(card, false);
                        projection.History.CardDrawResolved(entry, card);
                        break;
                    case ResumableDiscardProgram.EventKind.Shuffle:
                        break;
                    case ResumableDiscardProgram.EventKind.CostChanged:
                        card.MutablePreview.EnergyCost.SetThisCombat(item.Value);
                        card.InvalidateCaches();
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
                    case ResumableDiscardProgram.EventKind.Finish:
                    {
                        var active = stack.Pop();
                        if (active.Identity != item.Card) throw new InvalidOperationException("Unbalanced compact history.");
                        active.Method?.Dispose();
                        projection.History.CardPlayFinished(card, active.Play, (item.Flags & 1) != 0);
                        combat.RecordCardPlayed(card, item.Value != 0);
                        combat.RecordCardLifecycle(projection, card);
                        card.MutablePreview.CurrentTarget = null;
                        card.InvalidateCaches();
                        active.Scope.Dispose();
                        break;
                    }
                    case ResumableDiscardProgram.EventKind.ResultMoved:
                        if ((ResumableDiscardProgram.Pile)item.Value == ResumableDiscardProgram.Pile.Removed)
                        {
                            (card.GetPile(projection.State) ?? throw new InvalidOperationException("Removed card has no owning pile.")).Remove(card);
                            card.MutablePreview.HasBeenRemovedFromState = true;
                            combat.UnregisterGeneratedCombatCard(card);
                            card.NotifyHookListenerStructureChanged();
                        }
                        else
                        {
                            projection.AddToPile(card, NativePile((ResumableDiscardProgram.Pile)item.Value));
                            if ((ResumableDiscardProgram.Pile)item.Value == ResumableDiscardProgram.Pile.Exhaust)
                                combat.RecordCardExhausted(_player.Creature);
                        }
                        break;
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
            if (stack.Count != 0 || handEndMethod != null) throw new InvalidOperationException("Compact history did not finish.");
        }
        finally
        {
            handEndMethod?.Dispose();
            while (stack.TryPop(out var active)) { active.Method?.Dispose(); active.Scope.Dispose(); }
        }
        void AppendDamage(ResumableDiscardProgram.Event item, ref int index)
        {
            var blocked = program.EventAt(++index);
            var overkill = program.EventAt(++index);
            if (blocked.Kind != ResumableDiscardProgram.EventKind.DamageBlocked
                || overkill.Kind != ResumableDiscardProgram.EventKind.DamageOverkill
                || blocked.Card != item.Card || overkill.Card != item.Card
                || blocked.Target != item.Target || overkill.Target != item.Target)
                throw new InvalidOperationException("Malformed committed damage result.");
            Creature target = _creatures[item.Target];
            var traits = (ResumableDiscardProgram.DamageTraits)item.Flags;
            bool poison = (traits & ResumableDiscardProgram.DamageTraits.Poison) != 0;
            Creature? dealer = (traits & ResumableDiscardProgram.DamageTraits.NoDealer) != 0 ? null
                : item.Card < 0 ? _creatures[-item.Card - 1] : _player.Creature;
            PredictedCard? cardSource = (traits & ResumableDiscardProgram.DamageTraits.NoCard) != 0 ? null : cards[item.Card];
            ValueProp props = poison ? 0 : ValueProp.Move;
            if ((traits & ResumableDiscardProgram.DamageTraits.Unpowered) != 0) props |= ValueProp.Unpowered;
            if ((traits & ResumableDiscardProgram.DamageTraits.Unblockable) != 0) props |= ValueProp.Unblockable;
            DamageResult result = new(target, props)
            {
                UnblockedDamage = item.Value, BlockedDamage = blocked.Value, OverkillDamage = overkill.Value,
                WasTargetKilled = (item.Flags & 1) != 0, WasBlockBroken = (item.Flags & 2) != 0,
                WasFullyBlocked = (item.Flags & 4) != 0
            };
            CombatDamageSource source = poison ? CombatDamageSource.For(CombatDamageSourceKind.Poison, nameof(PoisonPower))
                : item.Card < 0 ? CombatDamageSource.For(CombatDamageSourceKind.MonsterMove, dealer!.Monster!.Id.Entry)
                : projection.ResolveDamageSource(cardSource);
            projection.History.DamageReceived(target, dealer, result, cardSource, source);
            combat.RecordDamageReceived(target, dealer, result);
            if ((traits & ResumableDiscardProgram.DamageTraits.Unpowered) == 0) damageResults[item.Card] = result;
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
            if (!projection.CheckWinCondition(PlayerTurn)
                || projection.TerminalStamp?.Outcome != (program.DefeatTerminal ? CombatTerminalOutcome.Defeat : CombatTerminalOutcome.Victory))
                throw new InvalidOperationException("Compact terminal outcome differs from its projected safe point.");
        }
        ValueRng rng = program.ShuffleRng;
        projection.Rng.Shuffle.LoadFromSerializable(new()
            { counter = rng.Counter, state0 = rng.State0, state1 = rng.State1, state2 = rng.State2, state3 = rng.State3 });
        if (program.EnergyCostRng is { } energyRng)
            projection.Rng.CombatEnergyCosts.LoadFromSerializable(new()
                { counter = energyRng.Counter, state0 = energyRng.State0, state1 = energyRng.State1, state2 = energyRng.State2, state3 = energyRng.State3 });
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
        if (program.ShuffleRng != new ValueRng(rng.Counter, rng.State0, rng.State1, rng.State2, rng.State3)
            || _root.ShuffleEventCount + program.ShuffleCount != simulator.ShuffleEventCount)
            throw new InvalidOperationException("Compact values disagree with shuffle state or event count.");
        if (program.EnergyCostRng is { } energyRng)
        {
            var expected = simulator.Rng.CombatEnergyCosts.CaptureState();
            if (energyRng != new ValueRng(expected.Counter, expected.State0, expected.State1, expected.State2, expected.State3))
                throw new InvalidOperationException("Compact values disagree with energy-cost RNG state.");
        }
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
        var identities = CaptureCardIdentities(simulator);
        CardModel[] originals = identities.Where(pair => pair.Value >= 0).OrderBy(pair => pair.Value).Select(pair => pair.Key).ToArray();
        if (originals.Length != program.CardCount) throw new InvalidOperationException("Generated card count differs.");
        for (int card = 0; card < program.CardCount; card++)
        {
            PredictedCard? actual = simulator.State.FindCard(originals[card]);
            if (program.CardRemoved(card) != (actual == null)
                || actual != null && actual.Preview.EnergyCost.CostsX && actual.Preview.EnergyCost.CapturedXValue != program.CapturedX(card))
                throw new InvalidOperationException("Compact removal or captured energy differs.");
            if (actual != null && !actual.Preview.EnergyCost.CostsX
                && (actual.Preview.EnergyCost.GetWithModifiers(CostModifiers.Local) != program.EnergyCost(card)
                    || !actual.Preview.EnergyCost._localModifiers.Select(modifier => modifier.Amount)
                        .SequenceEqual(Enumerable.Range(0, program.CostModifierCount(card)).Select(index => program.CostModifierAt(card, index)))))
                throw new InvalidOperationException("Compact energy cost or ordered modifier list differs.");
        }
        for (int pile = 0; pile < piles.Length; pile++)
        {
            if (program.Count((ResumableDiscardProgram.Pile)pile) != piles[pile].Cards.Count)
                throw new InvalidOperationException($"Compact pile {pile} count differs: {program.Count((ResumableDiscardProgram.Pile)pile)} / {piles[pile].Cards.Count}.");
            for (int i = 0; i < piles[pile].Cards.Count; i++)
                if (!ReferenceEquals(originals[program.CardAt((ResumableDiscardProgram.Pile)pile, i)], piles[pile].Cards[i].Original))
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
        "ModifyDamageAdditive", "ModifyDamageMultiplicative", "ModifyDamageCap", "BeforeCardAutoPlayed", "AfterCardChangedPiles", "AfterCardChangedPilesLate",
        "ModifyShuffleOrder", "AfterShuffle", "BeforeAttack", "AfterAttack", "ModifyAttackHitCount",
        "BeforeDamageReceived", "AfterBlockBroken", "AfterCurrentHpChanged", "AfterDamageGiven", "AfterDamageReceived", "AfterDamageReceivedLate",
        "AfterModifyingHpLostAfterOsty", "BeforeDeath", "ShouldDie", "ShouldDieLate", "AfterDeath", "ShouldCreatureBeRemovedFromCombatAfterDeath",
        "ShouldAllowHitting", "BeforePowerAmountChanged", "ModifyPowerAmountGiven", "ModifyPowerAmountReceived",
        "TryModifyPowerAmountGiven", "TryModifyPowerAmountReceived",
        "AfterModifyingPowerAmountGiven", "AfterModifyingPowerAmountReceived", "AfterPowerAmountChanged",
        "AfterCardExhausted", "AfterCardEnteredCombat", "AfterCardGeneratedForCombat", "ModifyXValue", "AfterModifyingDamageAmount", "AfterModifyingHpLostBeforeOsty"
    };
    // Only immutable CLR method/type metadata is shared. Every root still checks subscriber,
    // Power, relic, card-instance, resource, and lifecycle values independently.
    private static readonly HashSet<string> ReachedRunHooks = new(StringComparer.Ordinal)
    {
        "AfterCardChangedPiles", "AfterCardChangedPilesLate", "ModifyDamageAdditive", "ModifyDamageMultiplicative", "ModifyDamageCap",
        "AfterModifyingDamageAmount", "BeforeDamageReceived", "AfterCurrentHpChanged", "AfterDamageReceived", "AfterDamageReceivedLate",
        "ModifyHpLostBeforeOsty", "ModifyHpLostBeforeOstyLate", "ModifyHpLostAfterOsty", "ModifyHpLostAfterOstyLate",
        "AfterModifyingHpLostBeforeOsty", "AfterModifyingHpLostAfterOsty", "BeforeDeath", "ShouldDie", "ShouldDieLate", "AfterDeath"
    };
    private static readonly HashSet<string> HandEndHooks = new(StringComparer.Ordinal)
        { "AfterAutoPostPlayPhaseEntered", "BeforeSideTurnEnd", "ShouldEtherealTrigger", "BeforeFlush" };
    private static readonly ConcurrentDictionary<(Type Type, bool RunPrefix, bool HandEnd), string[]> HookAudit = new();

    private static void AssertRepresentedHooks(AbstractModel listener, bool runPrefix, bool includeHandEnd)
    {
        string[] unrepresented = HookAudit.GetOrAdd((listener.GetType(), runPrefix, includeHandEnd), static key => key.Type
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => ((key.RunPrefix ? ReachedRunHooks : ReachedHooks).Contains(method.Name)
                    || key.HandEnd && !key.RunPrefix && HandEndHooks.Contains(method.Name))
                && method.GetBaseDefinition().DeclaringType == typeof(AbstractModel)
                && method.DeclaringType != typeof(AbstractModel) && (key.RunPrefix || !RepresentedHook(key.Type, method.Name)))
            .Select(method => method.Name).ToArray());
        if (unrepresented.Length != 0)
            throw new NotSupportedException($"Compact prototype has no {(runPrefix ? "deck" : "combat")} effect program for {listener.Id.Entry}.{unrepresented[0]}.");
    }

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
            || type == typeof(PiercingWailPower) && method == nameof(AbstractModel.AfterPowerAmountChanged)
            || type == typeof(ArtifactPower) && method is nameof(AbstractModel.TryModifyPowerAmountReceived) or nameof(AbstractModel.AfterModifyingPowerAmountReceived)
            || type == typeof(Slither) && method == nameof(AbstractModel.AfterCardDrawn)
            || type == typeof(DebufferModel) && method == nameof(AbstractModel.AfterPowerAmountChanged)
            || type == typeof(MultiplayerScalingModel) && method == nameof(AbstractModel.ModifyBlockMultiplicative)
            || method == nameof(AbstractModel.AfterCardPlayed)
            && (type == typeof(CccComboModel) || type == typeof(Play20CardsSingleTurnAchievement)
                || type == typeof(SkillSilent1Achievement));

    private static readonly MirrorMethodSpec OnPlay = new(typeof(CardModel), "OnPlay",
        BindingFlags.Instance | BindingFlags.NonPublic, [typeof(PlayerChoiceContext), typeof(CardPlay)]);
    private static readonly MirrorMethodSpec OnTurnEndInHand = new(typeof(CardModel), "OnTurnEndInHand",
        BindingFlags.Instance | BindingFlags.NonPublic, [typeof(PlayerChoiceContext)]);
    private static readonly PropertyInfo ShuffleEvents = typeof(CombatPredictionSimulator)
        .GetProperty(nameof(CombatPredictionSimulator.ShuffleEventCount))!;
}
