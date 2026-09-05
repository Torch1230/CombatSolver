using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static async Task TriggerActualSideTurnStartAsync(
        CombatState combatState,
        CombatSide side,
        Creature participant)
    {
        participant.BeforeTurnStart(side);
        await Hook.BeforeSideTurnStart(combatState, side, [participant]);
        if (participant.Block > 0)
        {
            if (Hook.ShouldClearBlock(combatState, participant, out AbstractModel? preventer))
                await SetBlockAsync(participant, 0);
            else
                await Hook.AfterPreventingBlockClear(combatState, preventer!, participant);
        }
        await Hook.AfterBlockCleared(combatState, participant);

        IReadOnlyList<Creature> participants = [participant];
        foreach (PowerModel power in participant.Powers
                     .Where(power => power is BlurPower
                         or DrawCardsNextTurnPower
                         or PlatingPower
                         or SlowPower
                         or PoisonPower
                         or BiasedCognitionPower
                         or ClarityPower
                         or CoolantPower
                         or DemonFormPower
                         or FeralPower
                         or FurnacePower
                         or NeurosurgePower
                         or NoxiousFumesPower
                         or PrepTimePower
                         or ReflectPower
                         or CountdownPower
                         or SandpitPower
                         or ShadowStepPower
                         or WraithFormPower)
                     .ToArray())
        {
            await power.AfterSideTurnStart(side, participants, combatState);
        }
        foreach (SandpitPower power in combatState.Creatures
                     .SelectMany(static creature => creature.Powers)
                     .OfType<SandpitPower>()
                     .ToArray())
        {
            await power.AfterSideTurnStartLate(side, participants, combatState);
        }
        if (side == CombatSide.Player && participant.Player is { } player)
        {
            var choiceContext = new BlockingPlayerChoiceContext();
            foreach (RelicModel relic in player.Relics.Where(static relic => !relic.IsMelted))
            {
                await relic.AfterPlayerTurnStartEarly(choiceContext, player);
                await relic.AfterPlayerTurnStart(choiceContext, player);
                await relic.AfterPlayerTurnStartLate(choiceContext, player);
            }
            await player.PlayerCombatState!.OrbQueue.AfterTurnStart(
                new BlockingPlayerChoiceContext());
        }
        foreach (RelicModel relic in combatState.Players
                     .SelectMany(static player => player.Relics)
                     .Where(static relic => !relic.IsMelted))
        {
            await relic.AfterSideTurnStart(side, participants, combatState);
            await relic.AfterSideTurnStartLate(side, participants, combatState);
        }
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
    }

    private static void TriggerSimulatedPlayerSetup(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Player player,
        IReadOnlyList<string> choiceCardIds)
    {
        TurnStartChoiceCursor choices = choiceCardIds.Count == 0
            ? new TurnStartChoiceCursor(null)
            : TurnStartChoiceCursor.ForAutomaticPolicy(request =>
            {
                CardChoiceSpec spec = TurnStartChoiceSupport.BuildSpec(simulator, player, request);
                return CardChoiceSupport.BuildRequestedChoice(spec, choiceCardIds) with
                {
                    SourceId = request.SourceId,
                    ContextId = request.ContextId,
                };
            });
        SimPlayerCombatState state = simulator.State.GetPlayerCombatState(player);
        combat.AdvancePlayerTurn(player);
        combat.SnapshotPowerAmountsAtTurnStart([player.Creature]);
        if (!TurnStartRelicSupport.TriggerBeforeSideTurnStart(
                simulator,
                combat,
                [player.Creature]))
        {
            throw new InvalidOperationException("模拟玩家回合准备在回合开始遗物阶段遇到动态选择。");
        }
        if (TurnStartPowerSupport.TriggerBeforeSideTurnStart(
                simulator,
                combat,
                [player.Creature]))
        {
            throw new InvalidOperationException("模拟玩家回合准备遇到动态回合开始结算。");
        }
        SimCreatureState creatureState = simulator.State.GetCreature(player.Creature);
        if (creatureState.Block > 0)
        {
            if (combat.ShouldClearBlock(player.Creature, out AbstractModel? preventer))
                creatureState.DamageBlock(creatureState.Block, ValueProp.Move);
            else
                PersistentRelicSupport.TriggerAfterPreventingBlockClear(
                    simulator,
                    preventer,
                    player.Creature);
        }
        CorePowerSupport.TriggerAfterBlockCleared(simulator, combat, player.Creature);
        if (PersistentRelicSupport.ShouldPlayerResetEnergy(combat, player))
            state.LoseEnergy(state.Energy);
        state.GainEnergy(PersistentPowerSupport.GetModifiedMaxEnergy(combat, player)
            + combat.ConsumeEnergyNextTurn(player));
        TurnStartRelicSupport.TriggerAfterEnergyReset(simulator, combat, player);
        if (combat.HasPendingChoice
            || !PersistentPowerSupport.TriggerAfterEnergyReset(simulator, combat, player))
        {
            throw new InvalidOperationException("模拟玩家回合准备在能量重置后遇到动态选择。");
        }
        TurnStartRelicSupport.TriggerAfterEnergyResetLate(simulator, combat, player);
        if (combat.HasPendingChoice)
            throw new InvalidOperationException("模拟玩家回合准备在能量重置后期遇到动态选择。");
        bool sideTurnStartTriggeredEarly = false;
        using (choices.BeforeNextTake(() =>
               {
                   sideTurnStartTriggeredEarly = true;
                   return combat.TriggerSideTurnStart(
                       simulator,
                       CombatSide.Player,
                       [player.Creature],
                       decrementPlating: combat.GetPlayerTurnNumber(player) != 1);
               }))
        {
            if (combat.PrepareBeforeHandDraw(simulator, player, choices))
                throw new InvalidOperationException("模拟玩家回合准备遇到动态抽牌前结算。");
            int drawCount = PersistentPowerSupport.ConsumeModifiedHandDraw(
                combat,
                player,
                CombatManager.baseHandDrawCount);
            int historyEntryStart = simulator.History.Entries.Count;
            simulator.Draw(player, drawCount, fromHandDraw: true);
            if (combat.HasPendingChoice)
                throw new InvalidOperationException("模拟玩家回合准备在抽牌时遇到动态选择。");
            TriggeredPowerSupport.CompensateHistorySince(simulator, combat, historyEntryStart);
            if (combat.HasPendingChoice)
                throw new InvalidOperationException("模拟玩家回合准备在抽牌补偿时遇到动态选择。");
            if (combat.TriggerAfterPlayerTurnStart(
                    simulator,
                    player.Creature,
                    choices))
                throw new InvalidOperationException("模拟玩家回合准备遇到动态抽牌后结算。");
            if (!sideTurnStartTriggeredEarly)
            {
                if (!combat.TriggerSideTurnStart(
                        simulator,
                        CombatSide.Player,
                        [player.Creature],
                        decrementPlating: combat.GetPlayerTurnNumber(player) != 1))
                {
                    throw new InvalidOperationException("模拟玩家回合准备在阶段开始时遇到动态选择。");
                }
            }
        }
        choices.AssertConsumed();
    }

    private static async Task TriggerActualPlayerSetupAsync(
        CombatState combatState,
        Player player,
        IReadOnlyList<string> choiceCardIds)
    {
        PlayerCombatState state = player.PlayerCombatState
            ?? throw new InvalidOperationException("玩家没有 PlayerCombatState。");
        combatState.CurrentSide = CombatSide.Player;
        combatState.RoundNumber++;
        state.IncrementTurnNumber();
        player.Creature.BeforeTurnStart(CombatSide.Player);
        await Hook.BeforeSideTurnStart(combatState, CombatSide.Player, [player.Creature]);
        if (player.Creature.Block > 0)
        {
            if (Hook.ShouldClearBlock(combatState, player.Creature, out AbstractModel? preventer))
                await SetBlockAsync(player.Creature, 0);
            else
                await Hook.AfterPreventingBlockClear(combatState, preventer!, player.Creature);
        }
        await Hook.AfterBlockCleared(combatState, player.Creature);
        if (Hook.ShouldPlayerResetEnergy(combatState, player))
            state.ResetEnergy();
        else
            state.AddMaxEnergyToCurrent();
        await Hook.AfterEnergyReset(combatState, player);
        var choiceContext = new BlockingPlayerChoiceContext();
        bool sideTurnStartTriggeredEarly = choiceCardIds.Count > 0;
        if (sideTurnStartTriggeredEarly)
            await Hook.AfterSideTurnStart(combatState, CombatSide.Player, [player.Creature]);
        using (choiceCardIds.Count == 0
                   ? null
                   : CardSelectCmd.PushSelector(new UnattendedCardSelector(choiceCardIds)))
        {
            await Hook.BeforeHandDraw(combatState, player, choiceContext);
        }
        decimal drawCount = Hook.ModifyHandDraw(
            combatState,
            player,
            CombatManager.baseHandDrawCount,
            out IEnumerable<AbstractModel> modifiers);
        await Hook.AfterModifyingHandDraw(combatState, modifiers);
        await CardPileCmd.Draw(choiceContext, drawCount, player, fromHandDraw: true);
        await Hook.AfterPlayerTurnStart(combatState, choiceContext, player);
        if (!sideTurnStartTriggeredEarly)
            await Hook.AfterSideTurnStart(combatState, CombatSide.Player, [player.Creature]);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
    }

    private static async Task TriggerActualTransientSideTurnEndPowersAsync(
        CombatState combatState,
        CombatSide side,
        IEnumerable<Creature> participants)
    {
        Creature[] participantArray = participants.ToArray();
        foreach (PowerModel power in combatState.Creatures
                     .SelectMany(static creature => creature.Powers)
                     .Where(power => power is AnticipatePower
                         or BorrowedTimePower
                         or BurstPower
                         or ConquerorPower
                         or DuplicationPower
                         or FlameBarrierPower
                         or NoDrawPower
                         or NoEnergyGainPower
                         or OneTwoPunchPower
                         or RagePower
                         or ReboundPower
                         or RetainHandPower
                         or RitualPower
                         or ShadowmeldPower)
                     .ToArray())
        {
            await power.AfterSideTurnEnd(
                new BlockingPlayerChoiceContext(),
                side,
                participantArray);
        }
    }

    private static async Task TriggerActualSideTurnEndAsync(
        CombatState combatState,
        CombatSide side,
        IEnumerable<Creature> participants)
    {
        Creature[] participantArray = participants.ToArray();
        var choiceContext = new BlockingPlayerChoiceContext();
        foreach (AsleepPower asleep in combatState.Creatures
                     .SelectMany(static creature => creature.Powers.OfType<AsleepPower>())
                     .ToArray())
        {
            await asleep.BeforeSideTurnEndVeryEarly(choiceContext, side, participantArray);
        }
        foreach (RegenPower regen in participantArray
                     .SelectMany(static participant => participant.Powers.OfType<RegenPower>())
                     .ToArray())
        {
            await regen.BeforeSideTurnEndEarly(choiceContext, side, participantArray);
        }
        if (side == CombatSide.Enemy)
        {
            foreach (PlatingPower plating in participantArray
                         .SelectMany(static participant => participant.Powers.OfType<PlatingPower>())
                         .ToArray())
            {
                await plating.BeforeSideTurnEndEarly(choiceContext, side, participantArray);
            }
        }
        foreach (DoomPower doom in combatState.Creatures
                     .SelectMany(static creature => creature.Powers.OfType<DoomPower>())
                     .ToArray())
        {
            await doom.BeforeSideTurnEnd(choiceContext, side, participantArray);
        }

        foreach (PowerModel power in combatState.Creatures
                     .SelectMany(static creature => creature.Powers)
                     .Where(power => power is ColossusPower
                         or AsleepPower
                         or BattlewornDummyTimeLimitPower
                         or ConcoctPower
                         or CorrosiveWavePower
                         or DemisePower
                         or DarkEmbracePower
                         or DoomPower
                         or EscapeArtistPower
                         or GravityPower
                         or HatchPower
                         or HighVoltagePower
                         or TaintedPower
                         or TerritorialPower
                         or ConsumingShadowPower
                         or DebilitatePower
                         or HellraiserPower
                         or MagicBombPower
                         or MonologuePower
                         or NemesisPower
                         or OblivionPower
                         or PanachePower
                         or PaleBlueDotPower
                         or SicEmPower
                         or SkittishPower
                         or StranglePower
                         or JugglingPower
                         or TenderPower
                         or UnderworldPower
                         or ConstrictPower
                         or TemporaryStrengthPower
                         or HotfixPower
                         or SpeedPotionPower
                         or SynchronizePower
                         or ShrinkPower
                         or SlumberPower
                         or SmoggyPower
                         or TangledPower
                         or RingingPower
                         or DoubleDamagePower)
                     .ToArray())
        {
            await power.AfterSideTurnEnd(choiceContext, side, participantArray);
        }

        if (side == CombatSide.Enemy)
        {
            foreach (PowerModel duration in combatState.Creatures
                         .SelectMany(static creature => creature.Powers)
                         .Where(power => power is WeakPower
                             or VulnerablePower
                             or FrailPower
                             or IntangiblePower
                             or NoBlockPower)
                         .ToArray())
            {
                await duration.AfterSideTurnEnd(choiceContext, side, participantArray);
            }
        }

        await TriggerActualTransientSideTurnEndPowersAsync(combatState, side, participantArray);

        foreach (DisintegrationPower power in combatState.Creatures
                     .SelectMany(static creature => creature.Powers.OfType<DisintegrationPower>())
                     .ToArray())
        {
            await power.AfterSideTurnEndLate(choiceContext, side, participantArray);
        }
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
    }
}
