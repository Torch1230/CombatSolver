using System.Reflection;
using System.Runtime.ExceptionServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private sealed class KnownExoskeletonsNativeSelector(Player player, IReadOnlyList<PlanCardChoice> choices)
        : ICardSelector
    {
        private int _index;
        private ExceptionDispatchInfo? _failure;

        public Task<IEnumerable<CardModel>> GetSelectedCards(
            IEnumerable<CardModel> options, int minSelect, int maxSelect)
        {
            try
            {
                ThrowIfFailed();
                if (_index >= choices.Count)
                    throw new InvalidOperationException("外骨骼虫原版出现未计划的额外选牌。");
                PlanCardChoice choice = choices[_index];
                (int Min, int Max) expected = (choice.Effect, choice.SourcePile) switch
                {
                    (PlanChoiceEffect.MoveToHand, PileType.Discard) => (0, 2),
                    (PlanChoiceEffect.MoveToDrawTop, PileType.Hand) => (1, 1),
                    (PlanChoiceEffect.AutoPlayRepeated, PileType.Hand) => (1, 1),
                    (PlanChoiceEffect.GenerateToHand, PileType.None) => (0, 1),
                    _ => throw new InvalidOperationException("外骨骼虫原版选牌类型不属于冻结路线。"),
                };
                if (choice.Timing != PlanChoiceTiming.Action || minSelect != expected.Min
                    || maxSelect != expected.Max || choice.Cards.Count < minSelect || choice.Cards.Count > maxSelect)
                    throw new InvalidOperationException("外骨骼虫原版选牌时机或数量不符。");
                // ICardSelector does not expose native source/context arguments. The full
                // frozen execution order remains authoritative; also require its nonempty
                // outer source to still exist in Play during the nested native transaction.
                if (choice.SourceId.Length != 0 && !PileType.Play.GetPile(player).Cards
                        .Any(card => string.Equals(card.Id.Entry, choice.SourceId, StringComparison.Ordinal)))
                    throw new InvalidOperationException("外骨骼虫原版嵌套选择缺少仍在执行的来源牌。");
                List<CardModel> available = options.ToList();
                IReadOnlyList<CardModel> source = choice.SourcePile == PileType.None
                    ? available : choice.SourcePile.GetPile(player).Cards;
                List<CardModel> selected = [];
                foreach (PlanCardToken token in choice.Cards)
                {
                    if (token.SourceOccurrence < 0 || token.OptionOccurrence < 0 || string.IsNullOrEmpty(token.StateKey))
                        throw new InvalidOperationException("外骨骼虫冻结选择缺少完整实例身份。");
                    CardModel card = available.Where(candidate => CardChoiceSupport.MatchesToken(candidate, token))
                        .Skip(token.OptionOccurrence).FirstOrDefault()
                        ?? throw new InvalidOperationException("外骨骼虫原版选择缺少准确候选游标。");
                    if (selected.Any(previous => ReferenceEquals(previous, card))
                        || !source.Any(candidate => ReferenceEquals(candidate, card))
                        || source.TakeWhile(candidate => !ReferenceEquals(candidate, card))
                            .Count(candidate => CardChoiceSupport.MatchesToken(candidate, token)) != token.SourceOccurrence
                        || !string.Equals(CardChoiceSupport.ChoiceCardKey(card), token.StateKey, StringComparison.Ordinal))
                        throw new InvalidOperationException("外骨骼虫原版选择源实例、游标或完整语义状态不同。");
                    selected.Add(card);
                }
                _index++;
                return Task.FromResult<IEnumerable<CardModel>>(selected);
            }
            catch (InvalidOperationException error)
            {
                _failure = ExceptionDispatchInfo.Capture(error);
                throw;
            }
        }

        public CardRewardSelection GetSelectedCardReward(
            IReadOnlyList<CardCreationResult> options, IReadOnlyList<CardRewardAlternative> alternatives)
        {
            InvalidOperationException error = new("外骨骼虫原版出现未计划的卡牌奖励选择。");
            _failure = ExceptionDispatchInfo.Capture(error);
            throw error;
        }

        public void ThrowIfFailed() => _failure?.Throw();

        public void AssertConsumed()
        {
            ThrowIfFailed();
            if (_index != choices.Count)
                throw new InvalidOperationException($"外骨骼虫原版仍有未执行的选择：{_index}/{choices.Count}。");
        }
    }

    // This proves feasibility of the reconstructed v9 constraints + recorded v31 candidate,
    // not discovery by Solve, v9 selected PlanAction bytes or production UI deployment.
    private async Task<int> RunKnownExoskeletonsRouteNativeAsync(CombatState combat, Player player)
    {
        if (_knownExoskeletonsNativeObservation != null || _mercuryTerminalObservation != null
            || CardSelectCmd.Selector != null)
            throw new InvalidOperationException("外骨骼虫原版对照不能覆盖现有观察者或选牌器。");
        List<KnownExoskeletonsPrefix> prefixes = [];
        RunKnownExoskeletonsRouteReplay(combat, player, prefixes);
        Creature[] rootEnemies = combat.Enemies.ToArray();
        if (prefixes.Count != 24 || rootEnemies.Length != 4
            || prefixes.Sum(item => item.Prefix.Action.GetActionChoicesInExecutionOrder().Count) != 10
            || prefixes.Any(item => item.Enemies.Count != 4)
            || prefixes[^1].Prefix.TerminalStamp is not
                { Outcome: CombatTerminalOutcome.Victory, PlayerTurn: > 0 and <= 5 })
            throw new InvalidOperationException("外骨骼虫原版对照缺少完整 24 步、10 选择、四敌胜利冻结记录。");
        _completedChecks.Add("KnownExoskeletonsNative:All24FullPredictionsFrozenBeforeNativeActions");
        MethodInfo endCombat = typeof(CombatManager).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == "EndCombatInternal"
                && method.GetParameters() is [{ ParameterType.Name: "CombatTurnState" }]);
        PropertyInfo stateProperty = endCombat.GetParameters()[0].ParameterType.GetProperty("State")
            ?? throw new MissingMemberException("CombatTurnState.State");
        MethodInfo endPrefix = typeof(UnattendedTestRunner).GetMethod(
            nameof(ObserveKnownExoskeletonsCombatEndPrefix), BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(ObserveKnownExoskeletonsCombatEndPrefix));
        MethodInfo shuffle = typeof(CardPileCmd).GetMethods(BindingFlags.Static | BindingFlags.Public)
            .Single(method => method.Name == "Shuffle"
                && method.GetParameters() is [{ ParameterType.Name: "PlayerChoiceContext" }, { ParameterType: var type }]
                && type == typeof(Player));
        MethodInfo shufflePrefix = typeof(UnattendedTestRunner).GetMethod(
            nameof(ObserveKnownExoskeletonsShufflePrefix), BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(ObserveKnownExoskeletonsShufflePrefix));
        Harmony patch = new("CombatSolver.Testing.KnownExoskeletonsNative." + _request.RunId);
        KnownExoskeletonsNativeObservation observation = new(this, combat, player,
            Array.AsReadOnly(rootEnemies), stateProperty);
        _knownExoskeletonsNativeObservation = observation;
        try
        {
            CombatManager.Instance.CombatEnded += observation.ObserveCombatEnded;
            patch.Patch(endCombat, prefix: new HarmonyMethod(endPrefix));
            patch.Patch(shuffle, prefix: new HarmonyMethod(shufflePrefix));
            for (int index = 0; index < prefixes.Count; index++)
            {
                EnsureWithinDeadline();
                KnownExoskeletonsPrefix predicted = prefixes[index];
                PlanAction action = predicted.Prefix.Action;
                bool terminalStep = index == prefixes.Count - 1;
                if (!CombatManager.Instance.IsInProgress || observation.Terminal != null
                    || !ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), combat)
                    || player.PlayerCombatState is not { Phase: PlayerTurnPhase.Play } pcs
                    || pcs.TurnNumber != action.Turn)
                    throw new InvalidOperationException($"外骨骼虫原版第 {index + 1} 步之前不在同一战斗 T{action.Turn} Play。");
                KnownExoskeletonsNativeSelector selector = new(player, action.GetActionChoicesInExecutionOrder());
                KnownExoskeletonsActualPrefix actual;
                using (CardSelectCmd.PushSelector(selector))
                {
                    if (action.Kind == PlanActionKind.PlayCard)
                    {
                        Creature? target = action.TargetCombatId is { } targetId
                            ? combat.Enemies.Single(candidate => candidate.CombatId == targetId) : null;
                        if (target?.IsDead == true)
                            throw new InvalidOperationException("外骨骼虫原版目标已经死亡。");
                        CardModel card = SolverController.FindCardForDeployment(pcs.Hand.Cards.ToList(), action);
                        if (!card.TryManualPlay(target))
                            throw new InvalidOperationException($"外骨骼虫原版拒绝第 {index + 1} 步 {action.CardId}。");
                        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                    }
                    else if (action.Kind == PlanActionKind.EndTurn)
                    {
                        CombatManager.Instance.OnEndedTurnLocally();
                        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, action.Turn));
                        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                        // Keep the strict selector installed until the actual next Play phase,
                        // including async enemy/start-of-turn work outside the action queue.
                        while (player.PlayerCombatState is not { Phase: PlayerTurnPhase.Play } next
                            || next.TurnNumber <= action.Turn)
                        {
                            EnsureWithinDeadline();
                            observation.Failure?.Throw();
                            selector.ThrowIfFailed();
                            if (!CombatManager.Instance.IsInProgress || observation.Terminal != null)
                                throw new InvalidOperationException("外骨骼虫原版在计划结束回合时提前终局。");
                            await NextFrameAsync();
                        }
                    }
                    else
                        throw new InvalidOperationException("外骨骼虫原版路线不允许药水或未记录动作。");

                    observation.Failure?.Throw();
                    selector.ThrowIfFailed();
                    if (terminalStep)
                    {
                        while (observation.Terminal == null || !observation.CombatEnded || CombatManager.Instance.IsInProgress)
                        {
                            EnsureWithinDeadline();
                            observation.Failure?.Throw();
                            selector.ThrowIfFailed();
                            await NextFrameAsync();
                        }
                        actual = observation.Terminal;
                    }
                    else
                    {
                        if (observation.Terminal != null || !CombatManager.Instance.IsInProgress
                            || player.PlayerCombatState is not { Phase: PlayerTurnPhase.Play } next
                            || next.TurnNumber != predicted.Prefix.Turn)
                            throw new InvalidOperationException($"外骨骼虫原版第 {index + 1} 步后回合/阶段/终局不同。");
                        actual = observation.Capture();
                    }
                    selector.AssertConsumed();
                }
                AssertKnownExoskeletonsNativePrefix(predicted, actual, index + 1, terminalStep);
                _completedChecks.Add($"KnownExoskeletonsNative:StrictPrefix:{index + 1}/24:AllFourOriginalEnemiesAndContinuation");
                Entry.Logger.Info($"[CombatSolver/Test] KNOWN_EXOSKELETONS_NATIVE step={index + 1} " +
                    $"turn={actual.Turn} hp={actual.Enemies[0].State.PlayerHp} " +
                    $"enemy_hps=[{string.Join(',', actual.Enemies.Select(enemy => $"{enemy.CombatId}:{enemy.State.EnemyHp}"))}] " +
                    $"loss={actual.HpLost} potions={actual.PotionsUsed} shuffle_events={actual.ShuffleEvents} " +
                    $"shuffles_crossed={predicted.Prefix.ShufflesCrossed} choices={action.GetActionChoicesInExecutionOrder().Count}");
            }
            KnownExoskeletonsActualPrefix terminal = observation.Terminal
                ?? throw new InvalidOperationException("外骨骼虫原版未观察到终局。");
            _completedChecks.Add("KnownExoskeletonsNative:24NativeActions:6Primary:4Nested:4NativeEndTurns:0ExtraChoices:0Potions:Loss0:HP97:VictoryT5OrEarlier:CombatEndedObserved:NoSolve:NotProductionUiDeployment");
            return terminal.Turn;
        }
        finally
        {
            try { patch.Unpatch(endCombat, endPrefix); }
            finally
            {
                try { patch.Unpatch(shuffle, shufflePrefix); }
                finally
                {
                    CombatManager.Instance.CombatEnded -= observation.ObserveCombatEnded;
                    _knownExoskeletonsNativeObservation = null;
                }
            }
        }
    }

    private void AssertKnownExoskeletonsNativePrefix(
        KnownExoskeletonsPrefix predicted, KnownExoskeletonsActualPrefix actual, int step, bool terminal)
    {
        KnownRoutePrefix prefix = predicted.Prefix;
        if (actual.Turn != prefix.Turn || actual.HpLost != prefix.HpLost
            || actual.PotionsUsed != prefix.PotionsUsed || actual.ShuffleEvents != predicted.ShuffleEvents
            || actual.PlayerDead != prefix.PlayerDead || actual.Enemies.Count != predicted.Enemies.Count
            || prefix.AllEnemiesDead != terminal || (prefix.TerminalStamp != null) != terminal)
            throw new InvalidOperationException($"外骨骼虫原版第 {step} 步回合/累计损失/药水/洗牌/终局不符。");
        for (int index = 0; index < predicted.Enemies.Count; index++)
        {
            KnownExoskeletonsEnemyState expectedEnemy = predicted.Enemies[index], actualEnemy = actual.Enemies[index];
            if (expectedEnemy.CombatId != actualEnemy.CombatId || expectedEnemy.InRoster != actualEnemy.InRoster
                || expectedEnemy.IsDead != actualEnemy.IsDead || expectedEnemy.MoveId != actualEnemy.MoveId)
                throw new InvalidOperationException($"外骨骼虫原版第 {step} 步原始敌人 {expectedEnemy.CombatId} 身份/阵容/死亡/当前行动不同。");
            AssertSnapshotEqual(expectedEnemy.State, actualEnemy.State,
                "KnownExoskeletonsNative", $"prefix:{step}:CombatId={expectedEnemy.CombatId}");
        }
        if (terminal && (prefix.TerminalStamp?.PlayerTurn != actual.Turn || actual.Turn is not (> 0 and <= 5)
            || actual.PlayerDead || actual.HpLost != 0 || actual.PotionsUsed != 0
            || actual.Enemies.Any(enemy => !enemy.IsDead || enemy.State.EnemyHp != 0
                || enemy.State.PlayerHp != 97 || enemy.State.PlayerMaxHp != 103)))
            throw new InvalidOperationException("外骨骼虫原版终局不是四敌全灭、零累计战损、零药水、97/103 HP、T5 或更早。");
    }
}
