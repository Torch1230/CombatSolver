using MegaCrit.Sts2.Core.Combat;
using System.Reflection;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
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
using MegaCrit.Sts2.Core.Entities.Enchantments;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Singleton;

namespace CombatSolver;

/// <summary>
/// Captured model admission and compatibility projection for the compact executor. It decodes committed events;
/// it never executes OnPlay, a choice resolver, or the discard hook again. Production selects
/// it only when the complete captured root is represented by this closed effect domain.
/// </summary>
internal sealed class CompactDiscardProjection
{
    private readonly CombatPredictionSimulator _root;
    private readonly object _rootForkGate = new();
    private readonly Player _player;
    private readonly CardModel[] _identities, _definitionModels;
    private readonly PredictionRiskReason?[] _risks;
    internal readonly ResumableDiscardProgram Program;
    internal bool CardValuesInvariant { get; }
    internal bool HasMonsterMoves { get; }
    private readonly Creature[] _creatures;
    private readonly PowerModel[] _powerTemplates;
    private readonly PanachePower? _panacheTemplate;
    private readonly MoveState[]? _aiMoves;
    private readonly bool[]? _aiAttacks;
    internal int PlayerTurn => ((SimulatedCombatState)_root.State.CombatState).GetPlayerTurnNumber(_player);

    internal CompactDiscardProjection(CombatPredictionSimulator root, Player player, bool includeAttacks = false, bool includeHandEnd = false, bool includeMechaMoves = false, bool includePowerPhases = false, bool includeMechaAi = false, bool includeRounds = false)
    {
        if (includePowerPhases && !includeAttacks) throw new NotSupportedException("Power phases require creature values.");
        if (includeMechaAi && !includeMechaMoves) throw new NotSupportedException("Mecha AI requires captured commands.");
        if (includeRounds && (!includeHandEnd || !includePowerPhases || !includeMechaAi))
            throw new NotSupportedException("Round closure requires hand, Power and monster AI phases.");
        HasMonsterMoves = includeMechaMoves;
        _root = root;
        _player = player;
        var combat = (SimulatedCombatState)root.State.CombatState;
        int potionSlots = ((ICombatPredictionPlayerLimits)combat).GetPotionSlotCount(player);
        if (Enumerable.Range(0, potionSlots).Any(slot => combat.GetPotionAtSlot(player, slot) != null))
            throw new NotSupportedException("Compact combat has unrepresented potion effects.");
        if (includeRounds) combat.AssertCompletedRoundRoot();
        SimPlayerCombatState state = root.State.GetPlayerCombatState(player);
        var powers = combat.EffectivePowers();
        Creature? osty = combat.GetOsty(player);
        if (osty != null && (!includeAttacks || osty.Monster?.GetType() != typeof(Osty) || osty.PetOwner != player
                || powers.Count(power => power.Owner == osty && power.GetType() == typeof(DieForYouPower) && power.Amount == 1) != 1
                || powers.Any(power => power.Owner == osty && power is not (DieForYouPower or StrengthPower))
                || root.State.GetCreature(osty).IsDead && powers.Any(power => power.Owner == osty && power is not DieForYouPower))
            || powers.Any(power => power is DieForYouPower && power.Owner != osty)
            || combat.Allies.Any(creature => creature != player.Creature && creature != osty))
            throw new NotSupportedException("Compact pet roots require one captured Osty with its persistent protection and admitted stats.");
        if (combat.Players.Count != 1 || powers.Any(p => !IsBasicPower(p) && p is not PanachePower)
            || powers.Any(p => !(includeAttacks && p is PanachePower && p.Owner == player.Creature)
                && !(p is StratagemPower && p.Owner == player.Creature && p.Amount is >= 1 and <= 10)
                && !(includeAttacks ? p is not StratagemPower && IsBasicPower(p) && (p is not (BlockNextTurnPower or ToolsOfTheTradePower or NeurosurgePower or BorrowedTimePower or VeilpiercerPower or SpiritOfAshPower or DanseMacabrePower or LethalityPower or PagestormPower) || p.Owner == player.Creature)
                    && (p is not (PiercingWailPower or HangPower) || p.Owner != player.Creature)
                    : p is StrengthPower && p.Owner != player.Creature))
            || combat.RootRunModSubscriberCount != 0 || combat.RootCombatModSubscriberCount != 0
            || combat.RootHasBaseLibCardModifiers
            || state.OrbQueue.Orbs.Count != 0 || root.GetMaxHandSize(player) != 10
            || root.HasPendingChoice || root.IsOverOrEnding)
            throw new NotSupportedException("Compact prototype requires an idle root with admitted Powers and no mod subscribers.");
        var relics = combat.RelicsOf(player);
        if (relics.Any(r => r.GetType() != typeof(ToughBandages) && r.GetType() != typeof(TheAbacus)
                && r.GetType() != typeof(RingOfTheSnake) && !(r.GetType() == typeof(BoundPhylactery) && includeRounds && osty != null) || r.IsMelted)
            || relics.Select(r => r.GetType()).Distinct().Count() != relics.Count || powers.OfType<StratagemPower>().Count() > 1
            || combat.CurrentSide != player.Creature.Side)
            throw new NotSupportedException("Compact prototype requires admitted relics in player phase.");
        // Deck cards are a separate immutable listener prefix. Only run-scoped hooks
        // reach them; e.g. drawing a combat Slither must not invoke the deck copy.
        var runListeners = ((ICombatPredictionHookListenerSource)combat).RunHookListeners;
        for (int index = 0; index < combat.RootRunHookListenerCount; index++)
            AssertRepresentedHooks(runListeners[index], runPrefix: true, includeHandEnd, includePowerPhases, includeRounds);
        foreach (AbstractModel listener in combat.IterateHookListeners())
            AssertRepresentedHooks(listener, runPrefix: false, includeHandEnd, includePowerPhases, includeRounds);
        _creatures = !includeAttacks ? [] : osty == null ? [player.Creature, .. combat.Enemies] : [player.Creature, .. combat.Enemies, osty];
        if (includeAttacks && (combat.PlayerCreatures.Count != 1 || combat.KnownEnemies.Count != combat.Enemies.Count
            || _creatures.Where(c => c != osty).Any(c => root.State.GetCreature(c).IsDead || c.PetOwner != null)
            || combat.KnownEnemies.Any(c => combat.HasCompletedDeathEffects(c)
                || !((ICombatPredictionCreatureSemantics)combat).IsPrimaryEnemy(c)
                || !((ICombatPredictionCreatureSemantics)combat).ShouldRemoveAfterDeath(c))))
            throw new NotSupportedException("Compact attack requires living primary enemies without pending deaths, plus an optional captured pet.");
        if (includeMechaMoves && (!includeAttacks || combat.Enemies.Count != 1 || _creatures[1].Monster?.GetType() != typeof(MechaKnight)))
            throw new NotSupportedException("Captured Mecha commands require exactly one MechaKnight and creature values.");
        PredictedCard[] cards = state.AllCards.ToArray();
        _powerTemplates = includeAttacks ? CapturePowerTemplates(powers, cards) : [];
        PowerModel[] rootPowerOrder = powers.ToArray();
        PanachePowerValues[] panache = powers.OfType<PanachePower>().Select(power => new PanachePowerValues(
            power.Amount, power.Applier == null ? -1 : CreatureIndex(power.Applier), Array.IndexOf(rootPowerOrder, power) + 1,
            power.AmountOnTurnStart, power.DynamicVars["CardsLeft"].IntValue,
            PowerPredictionStateSupport.PanacheAlreadyApplied(root, power), power.SkipNextDurationTick)).ToArray();
        if (powers.OfType<PanachePower>().Any(power => power.Applier != null && CreatureIndex(power.Applier) < 0
            || power.DynamicVars["CardsLeft"].BaseValue != power.DynamicVars["CardsLeft"].IntValue))
            throw new NotSupportedException("Panache instance ownership or counter is outside the captured domain.");
        BasicPowerDefinition[]? powerDefinitions = includeAttacks ? _powerTemplates.Select(power => new BasicPowerDefinition(
            BasicKind(power), CreatureIndex(power.Owner), power.Amount, power.Applier == null ? -1 : CreatureIndex(power.Applier),
            Array.IndexOf(rootPowerOrder, power) + 1,
            power is WeakPower ? power.DynamicVars["DamageDecrease"].BaseValue
                : power is VulnerablePower ? power.DynamicVars["DamageIncrease"].BaseValue : 1m,
            combat.IsCapturedRootPowerSlot(power), power.AmountOnTurnStart, power.SkipNextDurationTick,
            power is DanseMacabrePower ? power.DynamicVars.Energy.IntValue : 0)).ToArray() : null;
        _identities = cards.Select(c => c.Original).ToArray();
        List<CardModel> generated = [];
        int shivTemplate = -1, inkyShivTemplate = -1, burnTemplate = -1, soulTemplate = -1, upgradedSoulTemplate = -1;
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
        foreach (bool upgraded in new[] { false, true })
        {
            if (!cards.Any(card => card.Preview is Dirge && card.Preview.IsUpgraded == upgraded || !upgraded && card.Preview is CaptureSpirit)) continue;
            if (upgraded) upgradedSoulTemplate = cards.Length + generated.Count;
            else soulTemplate = cards.Length + generated.Count;
            CardModel template = PredictionUtils.CreateCard(CanonicalModels.Card<Soul>(), player);
            if (upgraded) PredictionUtils.UpgradeCard(template);
            generated.Add(template);
        }
        // Native BladeOfInk enchants after the entire generated batch. In this closed root
        // generation hooks have no observers, Inky has no OnEnchant/Modify effects and no
        // combat history event is emitted by enchanting. Capture the final immutable variant.
        _definitionModels = [.. cards.Select(card => card.Preview), .. generated];
        // Dirge upgrades all Souls before insertion; admitted generation/upgrade hooks
        // have no observers, so each template captures that final native variant.
        ResumableDiscardProgram.Card[] definitions = _definitionModels.Select(card => CompactCardProgramCompiler.Compile(
            card, includeAttacks, shivTemplate, inkyShivTemplate, soulTemplate, upgradedSoulTemplate)).ToArray();
        if (osty == null && definitions.Any(card => card.Effects.RequiresPet))
            throw new NotSupportedException("Compact summoning requires a captured pet identity; first creation is not represented.");
        _risks = _definitionModels.Select(card => card is Burn or AscendersBane ? null : CardOnPlayMirrors.DescribeDispatch(card) switch
        {
            MirrorDispatchKind.Handled => (PredictionRiskReason?)null,
            MirrorDispatchKind.Inferred => PredictionRiskReason.MethodMirrorIncomplete,
            MirrorDispatchKind.Unsupported when CardOnPlayCompensationCatalog.Contains(card) => PredictionRiskReason.MethodNotMirrored,
            _ => throw new NotSupportedException($"Compact compatibility projection has no legacy OnPlay support for {card.Id.Entry}.")
        }).ToArray();
        decimal turnSummon = relics.OfType<BoundPhylactery>().SingleOrDefault()?.DynamicVars.Summon.BaseValue ?? 0;
        if (turnSummon != decimal.Truncate(turnSummon) || turnSummon is < 0 or > 999_999_999m)
            throw new NotSupportedException("Compact turn summoning requires an integral captured amount.");
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
        DeterministicMonsterAi? ai = null;
        if (includeMechaAi)
        {
            var source = combat.RequireCapturedMonsterAi(_creatures[1]);
            int[] next = [1, 2, 3, 1];
            if (source.NeedsInitialRoll || source.KnowledgeDemonCurseCounter != 0 || source.Machine.States.Count != 4)
                throw new NotSupportedException("Mecha AI has unsupported root state.");
            _aiMoves = MechaMoveIds.Select(id => source.Machine.States.GetValueOrDefault(id) as MoveState
                ?? throw new NotSupportedException("Mecha AI has an unknown state.")).ToArray();
            for (int index = 0; index < _aiMoves.Length; index++)
            {
                var move = _aiMoves[index];
                if (move.GetType() != typeof(MoveState) || move.MustPerformOnceBeforeTransitioning
                    || (move.FollowUpState?.Id ?? move.FollowUpStateId) != MechaMoveIds[next[index]])
                    throw new NotSupportedException("Mecha AI graph is outside the deterministic captured domain.");
            }
            int current = Array.IndexOf(_aiMoves, source.Current);
            ai = new(1, current, next, source.StateLog.Select(id => Array.IndexOf(MechaMoveIds, id)).ToArray());
            _aiAttacks = _aiMoves.Select(move => source.Static.AttacksByMove[move.Id].Count > 0).ToArray();
        }
        Program = new(definitions[..cards.Length], piles, state.Energy, root.State.GetCreature(player.Creature).Block,
            Block(relics.OfType<ToughBandages>().SingleOrDefault()),
            new(rng.Counter, rng.State0, rng.State1, rng.State2, rng.State3), comparisons,
            powers.OfType<StratagemPower>().SingleOrDefault()?.Amount ?? 0,
            Block(relics.OfType<TheAbacus>().SingleOrDefault()), abacusIndex >= 0 && abacusIndex < stratagemIndex,
            includeAttacks ? _creatures.Select(c => { var v = root.State.GetCreature(c); return new CreatureVitals(v.CurrentHp, v.MaxHp, v.Block); }).ToArray() : null, powerDefinitions, definitions[cards.Length..],
            new(energyRng.Counter, energyRng.State0, energyRng.State1, energyRng.State2, energyRng.State3), handEndAdmitted: includeHandEnd, monsterMoves: includeMechaMoves ? CaptureMechaCommands(root, burnTemplate) : null, powerPhasesAdmitted: includePowerPhases, monsterAi: ai,
            round: includeRounds ? new(combat.RoundNumber, PlayerTurn, player.MaxEnergy, MegaCrit.Sts2.Core.Combat.CombatManager.baseHandDrawCount, (int)turnSummon) : null, pet: osty == null ? -1 : CreatureIndex(osty),
            // Idle roots contain completed plays; this getter uses frozen CardPlaysStarted history.
            attackCardStarts: combat.GetAttacksPlayedThisTurn(player.Creature), panache: panache);
        _panacheTemplate = Program.HasPanache ? CanonicalModels.Power<PanachePower>() : null;
        if (_panacheTemplate != null) _ = _panacheTemplate.DynamicVars;
        CardValuesInvariant = Program.CardValuesInvariant;
    }

    internal CompactMonsterAiReadBinding? CreateMonsterAiReadBinding(CombatPredictionSimulator context)
        => _aiMoves == null ? null : new((SimulatedCombatState)context.State.CombatState, _creatures[1], _aiMoves, _aiAttacks!);

    internal SimulatedCombatState.CompletedRoundReadBinding? CreateRoundReadBinding(CombatPredictionSimulator context)
        => !Program.HasRounds ? null : new((SimulatedCombatState)context.State.CombatState, _player, _creatures[1]);

    internal SimulatedCombatState.CompletedOstyReadBinding? CreateOstyReadBinding(CombatPredictionSimulator context)
        => Program.PetIndex < 0 ? null : new(context, _player);

    internal CompactCardMetadataReadBinding CreatePlanMetadata()
    {
        var owned = ForkRoot();
        return new(this, _identities.Select(card => owned.State.FindCard(card)!).ToArray());
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

    private PowerModel[] CapturePowerTemplates(IReadOnlyList<PowerModel> powers, IReadOnlyList<PredictedCard> cards)
    {
        List<PowerModel> result = powers.Where(IsBasicPower).ToList();
        if (result.GroupBy(p => (p.Owner, p.GetType())).Any(g => g.Count() != 1)
            || result.Any(p => CreatureIndex(p.Owner) < 0 || p.Applier != null && CreatureIndex(p.Applier) < 0
                || p is not (StrengthPower or DexterityPower) && p.Amount < 0))
            throw new NotSupportedException("Basic Power state has duplicate slots, unknown ownership or negative debuffs.");
        foreach (Creature owner in _creatures)
        foreach (BasicPowerKind kind in Enum.GetValues<BasicPowerKind>())
        {
            // No admitted instruction creates Artifact or Stratagem; capture existing slots only.
            if (kind is BasicPowerKind.Artifact or BasicPowerKind.Stratagem or BasicPowerKind.DieForYou) continue;
            if (kind == BasicPowerKind.BorrowedTime && (owner != _player.Creature || !cards.Any(card => card.Preview is BorrowedTime))) continue;
            if (kind == BasicPowerKind.Veilpiercer && (owner != _player.Creature || !cards.Any(card => card.Preview is Veilpiercer))) continue;
            if (kind == BasicPowerKind.SpiritOfAsh && (owner != _player.Creature || !cards.Any(card => card.Preview is SpiritOfAsh))) continue;
            if (kind == BasicPowerKind.Lethality && (owner != _player.Creature || !cards.Any(card => card.Preview is Lethality))) continue;
            if (kind == BasicPowerKind.Pagestorm && (owner != _player.Creature || !cards.Any(card => card.Preview is Pagestorm))) continue;
            if (kind == BasicPowerKind.DanseMacabre && (owner != _player.Creature || !cards.Any(card => card.Preview is DanseMacabre))) continue;
            if (kind == BasicPowerKind.Hang && (owner == _player.Creature || !cards.Any(card => card.Preview is Hang))) continue;
            if (owner.PetOwner != null && kind != BasicPowerKind.Strength) continue;
            if (kind is BasicPowerKind.Neurosurge or BasicPowerKind.Doom
                && (owner != _player.Creature || !cards.Any(card => card.Preview is Neurosurge) && !powers.Any(power => power is NeurosurgePower))) continue;
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

    private static readonly Dictionary<Type, BasicPowerKind> BasicKinds = new()
    {
        [typeof(StrengthPower)] = BasicPowerKind.Strength,
        [typeof(DexterityPower)] = BasicPowerKind.Dexterity,
        [typeof(WeakPower)] = BasicPowerKind.Weak,
        [typeof(VulnerablePower)] = BasicPowerKind.Vulnerable,
        [typeof(FrailPower)] = BasicPowerKind.Frail,
        [typeof(PoisonPower)] = BasicPowerKind.Poison,
        [typeof(BlockNextTurnPower)] = BasicPowerKind.BlockNextTurn,
        [typeof(ToolsOfTheTradePower)] = BasicPowerKind.ToolsOfTheTrade,
        [typeof(PiercingWailPower)] = BasicPowerKind.PiercingWail,
        [typeof(ArtifactPower)] = BasicPowerKind.Artifact,
        [typeof(StratagemPower)] = BasicPowerKind.Stratagem,
        [typeof(DoomPower)] = BasicPowerKind.Doom,
        [typeof(NeurosurgePower)] = BasicPowerKind.Neurosurge,
        [typeof(DieForYouPower)] = BasicPowerKind.DieForYou,
        [typeof(BorrowedTimePower)] = BasicPowerKind.BorrowedTime,
        [typeof(VeilpiercerPower)] = BasicPowerKind.Veilpiercer,
        [typeof(HangPower)] = BasicPowerKind.Hang,
        [typeof(SpiritOfAshPower)] = BasicPowerKind.SpiritOfAsh,
        [typeof(DanseMacabrePower)] = BasicPowerKind.DanseMacabre,
        [typeof(LethalityPower)] = BasicPowerKind.Lethality,
        [typeof(PagestormPower)] = BasicPowerKind.Pagestorm
    };
    private static bool IsBasicPower(PowerModel power) => BasicKinds.ContainsKey(power.GetType());
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
        BasicPowerKind.Stratagem => CanonicalModels.Power<StratagemPower>(),
        BasicPowerKind.ToolsOfTheTrade => CanonicalModels.Power<ToolsOfTheTradePower>(),
        BasicPowerKind.Doom => CanonicalModels.Power<DoomPower>(),
        BasicPowerKind.Neurosurge => CanonicalModels.Power<NeurosurgePower>(),
        BasicPowerKind.BorrowedTime => CanonicalModels.Power<BorrowedTimePower>(),
        BasicPowerKind.Hang => CanonicalModels.Power<HangPower>(),
        BasicPowerKind.SpiritOfAsh => CanonicalModels.Power<SpiritOfAshPower>(),
        BasicPowerKind.Lethality => CanonicalModels.Power<LethalityPower>(),
        BasicPowerKind.Pagestorm => CanonicalModels.Power<PagestormPower>(),
        BasicPowerKind.DanseMacabre => CanonicalModels.Power<DanseMacabrePower>(),
        BasicPowerKind.Veilpiercer => CanonicalModels.Power<VeilpiercerPower>(),
        BasicPowerKind.DieForYou => CanonicalModels.Power<DieForYouPower>(),
        _ => throw new InvalidOperationException("Unknown basic Power kind.")
    };
    private static BasicPowerKind BasicKind(PowerModel power) => BasicKinds[power.GetType()];
    internal SimulatedCombatState.CompletedPowerReadBinding CreatePowerReadBinding(CombatPredictionSimulator context)
        => new(context, _powerTemplates, _powerTemplates.Select(power => CanonicalPower(BasicKind(power))).ToArray(), _panacheTemplate);
    internal void CopyPowerReadValues(ResumableDiscardProgram program, Span<CompletedPowerReadValues> target)
    {
        if (target.Length != program.PowerCount + program.PanacheCount) throw new ArgumentException("Power read buffer has the wrong size.");
        for (int index = 0; index < program.PowerCount; index++)
        {
            var value = program.Power(index);
            target[index] = new(value.Amount, value.Applier < 0 ? null : _creatures[value.Applier], value.Order, value.Retired,
                value.AmountOnTurnStart, value.SkipNextDurationTick);
        }
        for (int index = 0; index < program.PanacheCount; index++)
        {
            var value = program.Panache(index);
            target[program.PowerCount + index] = new(value.Amount, value.Applier < 0 ? null : _creatures[value.Applier], value.Order, false,
                value.AmountOnTurnStart, value.SkipNextDurationTick, value.CardsLeft, value.AlreadyApplied);
        }
    }

    // Legacy read helpers contain simulator-owned scratch. Each lane borrows its own root fork;
    // this is setup once per reader, never a leaf projection or a second execution authority.
    internal CompactDiscardReadView CreateReadView(bool reuseInvariantFeatures = true)
        => new(this, ForkRoot(), _player, _risks) { Invariants = reuseInvariantFeatures ? new() : null };

    private CombatPredictionSimulator ForkRoot()
    {
        // Fork seals history tails and publishes COW sharing bits. Only root copying is
        // serialized; event projection and lane-owned evaluation run independently.
        lock (_rootForkGate) return _root.Fork();
    }

    private static PileType NativePile(ResumableDiscardProgram.Pile pile) => pile switch
    {
        ResumableDiscardProgram.Pile.Hand => PileType.Hand,
        ResumableDiscardProgram.Pile.Draw => PileType.Draw,
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
    internal string ChoicePowerId(bool draw)
        => _powerTemplates.Single(power => draw ? power is StratagemPower : power is ToolsOfTheTradePower).Id.Entry;
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

    internal static MegaCrit.Sts2.Core.Combat.PlayerTurnPhase NativePlayerPhase(int value)
        => (ResumableDiscardProgram.PlayerPhase)value switch
        {
            ResumableDiscardProgram.PlayerPhase.End => MegaCrit.Sts2.Core.Combat.PlayerTurnPhase.End,
            ResumableDiscardProgram.PlayerPhase.None => MegaCrit.Sts2.Core.Combat.PlayerTurnPhase.None,
            ResumableDiscardProgram.PlayerPhase.Start => MegaCrit.Sts2.Core.Combat.PlayerTurnPhase.Start,
            ResumableDiscardProgram.PlayerPhase.Play => MegaCrit.Sts2.Core.Combat.PlayerTurnPhase.Play,
            _ => throw new InvalidOperationException("Unknown compact player phase.")
        };

    internal CombatPredictionSimulator Materialize(ResumableDiscardProgram program, CompactPhaseProbe? probe = null)
    {
        if (!Program.State.HasSameRoot(program.State) || !program.Complete)
            throw new InvalidOperationException("Evaluation requires a completed candidate from this root.");
        var forkStart = probe?.Begin() ?? default;
        CombatPredictionSimulator projection = ForkRoot();
        probe?.End(CompactProfilePhase.RootFork, forkStart);
        var eventsStart = probe?.Begin() ?? default;
        var combat = (SimulatedCombatState)projection.State.CombatState;
        var doomAppliers = combat.CaptureCombatHistoryReadValues().DoomAppliers;
        SimPlayerCombatState state = projection.State.GetPlayerCombatState(_player);
        List<PredictedCard> cards = _identities.Select(c => projection.State.FindCard(c)
            ?? throw new InvalidOperationException("Projection lost a root instance.")).ToList();
        var damageResults = new Dictionary<int, List<DamageResult>>();
        var stack = new Stack<(int Identity, CardPlay Play, PredictionTrace.TraceScope Scope, PredictionTrace.TraceScope? Method)>();
        var draws = new Stack<(int Identity, CombatPredictionCardDrawnEntry Entry)>();
        var drawMethods = new Stack<PredictionTrace.TraceScope>();
        PredictionTrace.TraceScope? handEndMethod = null;
        PredictionTrace.TraceScope? panacheMethod = null;
        try
        {
            for (int index = 0; index < program.EventCount; index++)
            {
                ResumableDiscardProgram.Event item = program.EventAt(index);
                if (item.Kind == ResumableDiscardProgram.EventKind.PlayerPhaseChanged)
                {
                    projection.State.GetPlayerCombatState(_player).Phase = NativePlayerPhase(item.Value);
                    continue;
                }
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
                    PredictedCard created = CreateGeneratedCard(program.DefinitionIndex(item.Card));
                    cards.Add(created);
                    var generation = projection.History.CardGenerated(created, item.Target == 0 ? _player : null, CardGenerationResultKind.Fixed);
                    SimCardPile? destination = (ResumableDiscardProgram.Pile)item.Flags switch
                    {
                        ResumableDiscardProgram.Pile.Hand => state.Hand,
                        ResumableDiscardProgram.Pile.Draw => state.DrawPile,
                        ResumableDiscardProgram.Pile.Discard => state.DiscardPile,
                        ResumableDiscardProgram.Pile.Unplaced when item.Value == -1 => null,
                        _ => throw new InvalidOperationException("Generated card destination was not admitted.")
                    };
                    if (destination != null)
                    {
                        if ((uint)item.Value > (uint)destination.Cards.Count)
                            throw new InvalidOperationException("Generated card insertion position is outside its pile.");
                        projection.AddToPile(created, destination.Type);
                        if (!destination.Remove(created)) throw new InvalidOperationException("Generated card left its captured destination.");
                        destination.Insert(item.Value, created);
                    }
                    projection.History.CardGenerationResolved(generation, created);
                    continue;
                }
                if (item.Kind == ResumableDiscardProgram.EventKind.CommitPlayerTurnHistory)
                {
                    combat.CommitHistoryCourseTurn(_player);
                    continue;
                }
                if (item.Kind == ResumableDiscardProgram.EventKind.BeginSide)
                {
                    doomAppliers.Clear();
                    bool enemy = item.Card == -2;
                    combat.ResetPowerLifecycleTurn(enemy ? _player.Creature : _creatures[1]);
                    combat.CurrentSide = enemy ? CombatSide.Enemy : CombatSide.Player;
                    if (!enemy) { combat.RoundNumber++; combat.AdvancePlayerTurn(_player); }
                    combat.BeginSideTurn(_creatures[-item.Card - 1]);
                    continue;
                }
                if (item.Kind == ResumableDiscardProgram.EventKind.CleanupCards)
                {
                    foreach (var cleaned in cards)
                    {
                        if (!cleaned.Preview.HasSingleTurnSly) continue;
                        cleaned.MutablePreview.HasSingleTurnSly = false;
                        cleaned.InvalidateCaches();
                    }
                    continue;
                }
                if (item.Kind == ResumableDiscardProgram.EventKind.ResetEnergy)
                {
                    state.LoseEnergy(state.Energy); state.GainEnergy(item.Value);
                    continue;
                }
                if (item.Kind == ResumableDiscardProgram.EventKind.GainEnergy)
                {
                    state.GainEnergy(item.Value);
                    continue;
                }
                if (item.Kind == ResumableDiscardProgram.EventKind.DoomApplied)
                {
                    doomAppliers.Add(_creatures[item.Card]);
                    continue;
                }
                if (item.Kind == ResumableDiscardProgram.EventKind.Kill)
                {
                    projection.State.GetCreature(_creatures[item.Target]).LoseHp(item.Value, ValueProp.Unblockable | ValueProp.Unpowered);
                    continue;
                }
                if (item.Kind == ResumableDiscardProgram.EventKind.Shuffle
                    || item.Kind == ResumableDiscardProgram.EventKind.Select && item.Card < 0) continue;
                // Creature commands share the committed damage and history representation.
                // Handle them before interpreting the source as a card instance.
                if (item.Kind == ResumableDiscardProgram.EventKind.Damage)
                {
                    AppendDamage(item, ref index);
                    continue;
                }
                if (item.Kind == ResumableDiscardProgram.EventKind.AttackFinish)
                {
                    Creature attacker = _creatures[item.Dealer];
                    projection.History.CreatureAttacked(attacker, damageResults[item.Card]);
                    combat.RecordCreatureAttacked(attacker);
                    damageResults.Remove(item.Card);
                    continue;
                }
                if (item.Kind == ResumableDiscardProgram.EventKind.Block)
                {
                    projection.State.GetCreature(item.Target < 0 ? _player.Creature : _creatures[item.Target]).GainBlock(item.Value);
                    continue;
                }
                if (item.Kind is ResumableDiscardProgram.EventKind.PowerChange or ResumableDiscardProgram.EventKind.SummonPet) continue;
                if (item.Kind == ResumableDiscardProgram.EventKind.Death)
                {
                    if (item.Target == 0) projection.LoseCombat();
                    else if (item.Target != program.PetIndex) projection.State.RemoveCreature(_creatures[item.Target]);
                    if (item.Target != program.PetIndex) combat.CompleteDeathPhase(_creatures[item.Target]);
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
                        var entry = projection.History.CardDrawn(card, item.Value != 0);
                        combat.RecordCardDrawn(card, item.Value != 0);
                        if ((item.Flags & 1) != 0) draws.Push((item.Card, entry));
                        else projection.History.CardDrawResolved(entry, card);
                        break;
                    case ResumableDiscardProgram.EventKind.DrawResolved:
                        if (!draws.TryPop(out var pendingDraw) || pendingDraw.Identity != item.Card)
                            throw new InvalidOperationException("Draw completion lost its suspended parent history.");
                        projection.History.CardDrawResolved(pendingDraw.Entry, card);
                        break;
                    case ResumableDiscardProgram.EventKind.DrawPowerStart:
                        if (_powerTemplates[item.Value] is not PagestormPower || draws.Peek().Identity != item.Card)
                            throw new InvalidOperationException("Nested draw hook has no admitted Power and drawn card.");
                        drawMethods.Push(projection.PushMethodSource(_powerTemplates[item.Value], AfterCardDrawn));
                        break;
                    case ResumableDiscardProgram.EventKind.DrawPowerFinish:
                        if (!drawMethods.TryPop(out var drawMethod) || draws.Peek().Identity != item.Card)
                            throw new InvalidOperationException("Nested draw hook scope was not started.");
                        drawMethod.Dispose();
                        break;
                    case ResumableDiscardProgram.EventKind.EnchantmentStart:
                    {
                        var active = stack.Pop();
                        if (active.Identity != item.Card || card.Preview.Enchantment is not Swift)
                            throw new InvalidOperationException("One-shot enchantment has no active admitted owner.");
                        active.Method?.Dispose();
                        var enchantment = card.MutablePreview.Enchantment!;
                        enchantment._status = EnchantmentStatus.Disabled;
                        card.InvalidateCaches();
                        stack.Push((active.Identity, active.Play, active.Scope, projection.PushMethodSource(enchantment, EnchantmentOnPlay)));
                        break;
                    }
                    case ResumableDiscardProgram.EventKind.EnchantmentFinish:
                    {
                        var active = stack.Pop();
                        if (active.Identity != item.Card || active.Method == null)
                            throw new InvalidOperationException("One-shot enchantment scope was not started.");
                        active.Method.Value.Dispose();
                        stack.Push((active.Identity, active.Play, active.Scope, null));
                        break;
                    }
                    case ResumableDiscardProgram.EventKind.Shuffle:
                        break;
                    case ResumableDiscardProgram.EventKind.KeywordAdded:
                        card.MutablePreview.LocalKeywords.Add((CardKeywordFlags)item.Flags switch
                        {
                            CardKeywordFlags.Ethereal => CardKeyword.Ethereal,
                            CardKeywordFlags.Retain => CardKeyword.Retain,
                            _ => throw new InvalidOperationException("Unknown compact keyword change.")
                        });
                        card.InvalidateCaches();
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
                        if ((item.Flags & 2) != 0)
                            stack.Push((active.Identity, active.Play, active.Scope, null));
                        else
                        {
                            card.MutablePreview.CurrentTarget = null;
                            card.InvalidateCaches();
                            active.Scope.Dispose();
                        }
                        break;
                    }
                    case ResumableDiscardProgram.EventKind.PanacheStart:
                        if (panacheMethod != null || stack.Peek().Identity != item.Card)
                            throw new InvalidOperationException("Independent Power hook has no completed owner card.");
                        panacheMethod = projection.PushMethodSource(_panacheTemplate!, AfterCardPlayed);
                        break;
                    case ResumableDiscardProgram.EventKind.PanacheFinish:
                        if (panacheMethod == null) throw new InvalidOperationException("Independent Power hook was not started.");
                        panacheMethod.Value.Dispose(); panacheMethod = null;
                        break;
                    case ResumableDiscardProgram.EventKind.CardHooksFinished:
                    {
                        var active = stack.Pop();
                        if (active.Identity != item.Card || active.Method != null || panacheMethod != null)
                            throw new InvalidOperationException("Unbalanced after-card hook scope.");
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
            if (stack.Count != 0 || draws.Count != 0 || drawMethods.Count != 0 || handEndMethod != null || panacheMethod != null)
                throw new InvalidOperationException("Compact history did not finish.");
        }
        finally
        {
            while (drawMethods.TryPop(out var drawMethod)) drawMethod.Dispose();
            panacheMethod?.Dispose();
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
                : _creatures[item.Dealer];
            PredictedCard? cardSource = (traits & ResumableDiscardProgram.DamageTraits.NoCard) != 0 ? null : cards[item.Card];
            ValueProp props = poison || panacheMethod != null ? 0 : ValueProp.Move;
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
            if ((traits & ResumableDiscardProgram.DamageTraits.Unpowered) == 0)
            {
                if (!damageResults.TryGetValue(item.Card, out var results)) damageResults.Add(item.Card, results = []);
                results.Add(result);
            }
        }
        combat.ImportCompletedDoomAppliers(doomAppliers);
        CreateMonsterAiReadBinding(projection)?.Read(program);
        if (program.PowerCount > 0)
        {
            var binding = CreatePowerReadBinding(projection);
            var values = new CompletedPowerReadValues[program.PowerCount + program.PanacheCount];
            CopyPowerReadValues(program, values);
            binding.Read(values, Enumerable.Range(1, program.EnemyEnd - 1)
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
        if (program.PetIndex >= 0)
        {
            var pet = program.Creature(program.PetIndex);
            CreateOstyReadBinding(projection)!.Read(pet.CurrentHp, pet.MaxHp, pet.Block, program.PetSummoned);
        }
        if (program.Terminal)
        {
            if (!projection.CheckWinCondition(program.HasRounds ? program.TerminalPlayerTurn : PlayerTurn)
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
        if (program.HasRounds)
        {
            var clock = (SimulatedCombatState)simulator.State.CombatState;
            if (program.RoundNumber != clock.RoundNumber || program.PlayerTurn != clock.GetPlayerTurnNumber(_player)
                || program.EnemySide != (clock.CurrentSide == CombatSide.Enemy))
                throw new InvalidOperationException("Compact round clock differs from completed projection.");
        }
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
            // A retained pet never enters the model backend's enemy death-phase map.
            // At a completed boundary its reversible death flag follows its own HP.
            bool deathCompleted = index == program.PetIndex ? actual.CurrentHp == 0
                : ((SimulatedCombatState)simulator.State.CombatState).HasCompletedDeathEffects(creature);
            if (actual != new CreatureReadValues(expected.CurrentHp, expected.MaxHp, expected.Block, program.CreaturePresent(index))
                || deathCompleted != program.CreatureDeathCompleted(index))
                throw new InvalidOperationException($"Compact creature values or death lifecycle differ: index={index}, actual={actual}, expected={expected}, death={deathCompleted}/{program.CreatureDeathCompleted(index)}.");
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
        var panache = powers.OfType<PanachePower>().ToArray();
        int panacheIndex = 0;
        for (int index = 0; index < program.PanacheCount; index++)
        {
            var value = program.Panache(index);
            if (value.Amount == 0) continue;
            if (panacheIndex == panache.Length) throw new InvalidOperationException("Compact independent Power count differs.");
            var actual = panache[panacheIndex++];
            if (value.Amount != actual.Amount || value.CardsLeft != actual.DynamicVars["CardsLeft"].IntValue
                || value.AlreadyApplied != PowerPredictionStateSupport.PanacheAlreadyApplied(simulator, actual)
                || value.AmountOnTurnStart != actual.AmountOnTurnStart || value.SkipNextDurationTick != actual.SkipNextDurationTick
                || actual.Applier != (value.Applier < 0 ? null : _creatures[value.Applier]))
                throw new InvalidOperationException("Compact independent Power lifecycle differs.");
        }
        if (panacheIndex != panache.Length) throw new InvalidOperationException("Compact independent Power count differs.");
        SimCardPile[] piles = [state.Hand, state.DrawPile, state.DiscardPile, state.PlayPile, state.ExhaustPile];
        var identities = CaptureCardIdentities(simulator);
        CardModel[] originals = identities.Where(pair => pair.Value >= 0).OrderBy(pair => pair.Value).Select(pair => pair.Key).ToArray();
        if (originals.Length != program.CardCount) throw new InvalidOperationException("Generated card count differs.");
        for (int card = 0; card < program.CardCount; card++)
        {
            PredictedCard? actual = simulator.State.FindCard(originals[card]);
            if ((program.CardRemoved(card) || program.CardUnplaced(card)) != (actual == null))
                throw new InvalidOperationException("Compact card presence differs from its combat piles.");
            if (program.CardUnplaced(card))
            {
                var generated = simulator.History.Entries.OfType<CombatPredictionCardGeneratedEntry>()
                    .Single(entry => ReferenceEquals(entry.Card.Original, originals[card])).Card;
                var definition = _definitionModels[program.DefinitionIndex(card)];
                if (generated.Owner != _player || generated.Id != definition.Id.Entry || generated.Type != definition.Type
                    || generated.UpgradeLevel != definition.CurrentUpgradeLevel || originals[card].HasBeenRemovedFromState)
                    throw new InvalidOperationException("Unplaced generation must retain history without a combat pile or removal flag.");
                // Legacy history retains scalar snapshots, not an ownerless mutable
                // preview. Native differential checks cover the full template metadata.
                continue;
            }
            if (actual != null && actual.Preview.HasBeenRemovedFromState != program.CardRemoved(card)
                || actual != null && actual.Preview.HasSingleTurnSly != program.SingleTurnSly(card)
                || actual != null && actual.Preview.EnergyCost.CostsX && actual.Preview.EnergyCost.CapturedXValue != program.CapturedX(card))
                throw new InvalidOperationException("Compact removal or captured energy differs.");
            if (actual?.Preview.Enchantment is Swift swift
                && swift.Status != (program.EnchantmentDisabled(card) ? EnchantmentStatus.Disabled : EnchantmentStatus.Normal))
                throw new InvalidOperationException("Compact one-shot enchantment status differs.");
            if (actual != null && (actual.Preview.LocalKeywords.Contains(CardKeyword.Ethereal) != program.IsEthereal(card)
                || actual.Preview.LocalKeywords.Contains(CardKeyword.Retain) != program.IsRetained(card)))
                throw new InvalidOperationException("Compact local keywords differ.");
            if (actual != null && !actual.Preview.EnergyCost.CostsX
                && (actual.Preview.EnergyCost.GetWithModifiers(CostModifiers.Local) != program.LocalEnergyCost(card)
                    || !actual.Preview.EnergyCost._localModifiers.Select(modifier => modifier.Amount)
                        .SequenceEqual(Enumerable.Range(0, program.CostModifierCount(card)).Select(index => program.CostModifierAt(card, index)))))
                throw new InvalidOperationException("Compact energy cost or ordered modifier list differs.");
            if (actual != null && actual.GetEnergyCostValueWithModifiers(simulator) != program.EnergyCost(card))
                throw new InvalidOperationException($"Compact global cost differs for card {card}: {actual.GetEnergyCostValueWithModifiers(simulator)} / {program.EnergyCost(card)}.");
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
        "AfterCardExhausted", "AfterCardEnteredCombat", "AfterCardGeneratedForCombat", "ModifyXValue", "AfterModifyingDamageAmount", "AfterModifyingHpLostBeforeOsty",
        "ModifyEnergyGain", "AfterModifyingEnergyGain", "AfterDiedToDoom", "ModifySummonAmount", "AfterOstyRevived", "AfterSummon"
    };
    // Only immutable CLR method/type metadata is shared. Every root still checks subscriber,
    // Power, relic, card-instance, resource, and lifecycle values independently.
    private static readonly HashSet<string> ReachedRunHooks = new(StringComparer.Ordinal)
    {
        "AfterCardChangedPiles", "AfterCardChangedPilesLate", "ModifyDamageAdditive", "ModifyDamageMultiplicative", "ModifyDamageCap",
        "AfterModifyingDamageAmount", "BeforeDamageReceived", "AfterCurrentHpChanged", "AfterDamageReceived", "AfterDamageReceivedLate",
        "ModifyHpLostBeforeOsty", "ModifyHpLostBeforeOstyLate", "ModifyHpLostAfterOsty", "ModifyHpLostAfterOstyLate",
        "AfterModifyingHpLostBeforeOsty", "AfterModifyingHpLostAfterOsty", "BeforeDeath", "ShouldDie", "ShouldDieLate", "AfterDeath", "AfterDiedToDoom"
    };
    private static readonly HashSet<string> HandEndHooks = new(StringComparer.Ordinal)
        { "AfterAutoPostPlayPhaseEntered", "BeforeSideTurnEnd", "ShouldEtherealTrigger", "BeforeFlush" };
    private static readonly HashSet<string> PowerPhaseHooks = new(StringComparer.Ordinal)
        { "AfterSideTurnEnd", "AfterSideTurnEndLate", "AfterBlockCleared" };
    private static readonly HashSet<string> RoundHooks = new(StringComparer.Ordinal)
    {
        "ShouldFlush", "AfterFlush", "AfterCardRetained", "BeforeSideTurnStart", "ShouldClearBlock",
        "AfterSideTurnStart", "AfterSideTurnStartLate", "AfterPlayerTurnStart", "BeforeHandDraw", "ModifyHandDraw", "ModifyHandDrawLate", "AfterModifyingHandDraw",
        "ShouldPlayerResetEnergy", "ModifyMaxEnergy", "AfterEnergyReset", "AfterEnergyResetLate", "AfterPreventingBlockClear",
        "ShouldTakeExtraTurn", "AfterTakingExtraTurn",
        "AfterAutoPrePlayPhaseEntered", "AfterAutoPrePlayPhaseEnteredLate"
    };
    private static readonly ConcurrentDictionary<(Type Type, bool RunPrefix, bool HandEnd, bool PowerPhases, bool Rounds), string[]> HookAudit = new();

    private static void AssertRepresentedHooks(AbstractModel listener, bool runPrefix, bool includeHandEnd, bool includePowerPhases, bool includeRounds)
    {
        string[] unrepresented = HookAudit.GetOrAdd((listener.GetType(), runPrefix, includeHandEnd, includePowerPhases, includeRounds), static key => key.Type
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => ((key.RunPrefix ? ReachedRunHooks : ReachedHooks).Contains(method.Name)
                    || !key.RunPrefix && (key.HandEnd && HandEndHooks.Contains(method.Name)
                        || key.PowerPhases && PowerPhaseHooks.Contains(method.Name) || key.Rounds && RoundHooks.Contains(method.Name)))
                && method.GetBaseDefinition().DeclaringType == typeof(AbstractModel)
                && method.DeclaringType != typeof(AbstractModel) && (key.RunPrefix || !RepresentedHook(key.Type, method.Name)))
            .Select(method => method.Name).ToArray());
        if (unrepresented.Length != 0)
            throw new NotSupportedException($"Compact prototype has no {(runPrefix ? "deck" : "combat")} effect program for {listener.Id.Entry}.{unrepresented[0]}.");
    }

    // Keep exact method/type pairs. AfterCardPlayed badges match the ignored mirror registrations;
    // DebufferModel only increments the native run badge counter, outside combat equivalence.
    private static bool RepresentedHook(Type type, string method)
        => (type == typeof(CccComboModel) || type == typeof(Play20CardsSingleTurnAchievement)) && method == nameof(AbstractModel.AfterSideTurnStart)
            || type == typeof(DieForYouPower) && method is nameof(AbstractModel.ModifyUnblockedDamageTarget)
                or nameof(AbstractModel.ShouldAllowHitting) or nameof(AbstractModel.ShouldCreatureBeRemovedFromCombatAfterDeath)
            || type == typeof(PoisonPower) && method == nameof(AbstractModel.AfterSideTurnStart)
            || type == typeof(NeurosurgePower) && method == nameof(AbstractModel.AfterSideTurnStart)
            || type == typeof(BorrowedTimePower) && method is nameof(AbstractModel.TryModifyEnergyCostInCombat) or nameof(AbstractModel.AfterSideTurnEnd)
            || (type == typeof(HangPower) || type == typeof(LethalityPower)) && method == nameof(AbstractModel.ModifyDamageMultiplicative)
            || (type == typeof(SpiritOfAshPower) || type == typeof(DanseMacabrePower)) && method == nameof(AbstractModel.BeforeCardPlayed)
            || type == typeof(PanachePower) && method is nameof(AbstractModel.AfterCardPlayed) or nameof(AbstractModel.AfterSideTurnEnd)
            || type == typeof(VeilpiercerPower) && method is nameof(AbstractModel.TryModifyEnergyCostInCombatLate) or nameof(AbstractModel.BeforeCardPlayed)
            || type == typeof(DoomPower) && method is nameof(AbstractModel.BeforeSideTurnEnd) or nameof(AbstractModel.AfterSideTurnEnd)
            || type == typeof(ToolsOfTheTradePower) && method is nameof(AbstractModel.ModifyHandDraw) or nameof(AbstractModel.AfterPlayerTurnStart)
            || type == typeof(BoundPhylactery) && method == nameof(AbstractModel.AfterEnergyResetLate)
            || type == typeof(RingOfTheSnake) && method == nameof(AbstractModel.ModifyHandDraw)
            || (type == typeof(WeakPower) || type == typeof(VulnerablePower) || type == typeof(FrailPower) || type == typeof(PiercingWailPower))
                && method == nameof(AbstractModel.AfterSideTurnEnd)
            || type == typeof(BlockNextTurnPower) && method == nameof(AbstractModel.AfterBlockCleared)
            || type == typeof(ToughBandages) && method == nameof(AbstractModel.AfterCardDiscarded)
            || type == typeof(StratagemPower) && method == nameof(AbstractModel.AfterShuffle)
            || type == typeof(TheAbacus) && method == nameof(AbstractModel.AfterShuffle)
            || type == typeof(StrengthPower) && method == nameof(AbstractModel.ModifyDamageAdditive)
            || type == typeof(DexterityPower) && method == nameof(AbstractModel.ModifyBlockAdditive)
            || (type == typeof(WeakPower) || type == typeof(VulnerablePower)) && method == nameof(AbstractModel.ModifyDamageMultiplicative)
            || type == typeof(FrailPower) && method == nameof(AbstractModel.ModifyBlockMultiplicative)
            || type == typeof(PiercingWailPower) && method == nameof(AbstractModel.AfterPowerAmountChanged)
            || type == typeof(ArtifactPower) && method is nameof(AbstractModel.TryModifyPowerAmountReceived) or nameof(AbstractModel.AfterModifyingPowerAmountReceived)
            || type == typeof(Slither) && method == nameof(AbstractModel.AfterCardDrawn)
            || type == typeof(PagestormPower) && method == nameof(AbstractModel.AfterCardDrawn)
            || type == typeof(DebufferModel) && method == nameof(AbstractModel.AfterPowerAmountChanged)
            || type == typeof(MultiplayerScalingModel) && method == nameof(AbstractModel.ModifyBlockMultiplicative)
            || method == nameof(AbstractModel.AfterCardPlayed)
            && (type == typeof(CccComboModel) || type == typeof(Play20CardsSingleTurnAchievement)
                || type == typeof(SkillSilent1Achievement));

    private static readonly MirrorMethodSpec OnPlay = new(typeof(CardModel), "OnPlay",
        BindingFlags.Instance | BindingFlags.NonPublic, [typeof(PlayerChoiceContext), typeof(CardPlay)]);
    private static readonly MirrorMethodSpec OnTurnEndInHand = new(typeof(CardModel), "OnTurnEndInHand",
        BindingFlags.Instance | BindingFlags.NonPublic, [typeof(PlayerChoiceContext)]);
    private static readonly MirrorMethodSpec EnchantmentOnPlay = new(typeof(EnchantmentModel), nameof(EnchantmentModel.OnPlay),
        BindingFlags.Instance | BindingFlags.Public, [typeof(PlayerChoiceContext), typeof(CardPlay)]);
    private static readonly MirrorMethodSpec AfterCardPlayed = MirrorMethodSpec.Hook(nameof(AbstractModel.AfterCardPlayed),
        [typeof(PlayerChoiceContext), typeof(CardPlay)]);
    private static readonly MirrorMethodSpec AfterCardDrawn = MirrorMethodSpec.Hook(nameof(AbstractModel.AfterCardDrawn),
        [typeof(PlayerChoiceContext), typeof(CardModel), typeof(bool)]);
    private static readonly PropertyInfo ShuffleEvents = typeof(CombatPredictionSimulator)
        .GetProperty(nameof(CombatPredictionSimulator.ShuffleEventCount))!;
}
