using System.Reflection;
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

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private sealed class KnownSoulNativeSelector(Player player, IReadOnlyList<PlanCardChoice> choices)
        : ICardSelector
    {
        private int _index;

        public Task<IEnumerable<CardModel>> GetSelectedCards(
            IEnumerable<CardModel> options, int minSelect, int maxSelect)
        {
            if (_index >= choices.Count)
                throw new InvalidOperationException("Soul 原版出现未计划的额外选牌。");
            PlanCardChoice choice = choices[_index];
            (int Min, int Max) expected = (choice.Effect, choice.SourcePile) switch
            {
                (PlanChoiceEffect.MoveToHand, PileType.Discard) => (0, 2),
                (PlanChoiceEffect.MoveToDrawTop, PileType.Hand) => (1, 1),
                (PlanChoiceEffect.GenerateToHand, PileType.None) => (0, 1),
                _ => throw new InvalidOperationException("Soul 原版选牌类型不属于已冻结路线。"),
            };
            if (choice.Timing != PlanChoiceTiming.Action || minSelect != expected.Min
                || maxSelect != expected.Max || choice.Cards.Count < minSelect || choice.Cards.Count > maxSelect)
                throw new InvalidOperationException("Soul 原版选牌时机或选择数量与计划不符。");
            List<CardModel> available = options.ToList();
            IReadOnlyList<CardModel> source = choice.SourcePile == PileType.None
                ? available : choice.SourcePile.GetPile(player).Cards;
            List<CardModel> selected = [];
            foreach (PlanCardToken token in choice.Cards)
            {
                CardModel card = available.Where(card => CardChoiceSupport.MatchesToken(card, token))
                    .Skip(token.OptionOccurrence).FirstOrDefault()
                    ?? throw new InvalidOperationException("Soul 原版选牌缺少准确候选游标。");
                if (selected.Any(previous => ReferenceEquals(previous, card))
                    || !source.Any(candidate => ReferenceEquals(candidate, card))
                    || source.TakeWhile(candidate => !ReferenceEquals(candidate, card))
                        .Count(candidate => CardChoiceSupport.MatchesToken(candidate, token)) != token.SourceOccurrence
                    || !string.Equals(CardChoiceSupport.ChoiceCardKey(card), token.StateKey, StringComparison.Ordinal))
                    throw new InvalidOperationException("Soul 原版选牌源实例、顺序游标或完整语义状态不同。");
                selected.Add(card);
            }
            _index++;
            return Task.FromResult<IEnumerable<CardModel>>(selected);
        }

        public CardRewardSelection GetSelectedCardReward(
            IReadOnlyList<CardCreationResult> options, IReadOnlyList<CardRewardAlternative> alternatives)
            => throw new InvalidOperationException("Soul 原版出现未计划的卡牌奖励选择。");

        public void AssertConsumed()
        {
            if (_index != choices.Count)
                throw new InvalidOperationException("Soul 原版仍有未执行的计划选择。");
        }
    }

    // This proves native feasibility, not search discovery or production UI deployment.
    // Freeze all 26 full-state predictions before touching live state. A selector remains
    // installed through each real turn transition so unexpected setup choices fail explicitly.
    private async Task<int> RunKnownSoulRouteNativeAsync(CombatState combat, Player player)
    {
        if (_mercuryTerminalObservation != null || CardSelectCmd.Selector != null)
            throw new InvalidOperationException("Soul 原版对照不能覆盖现有观察者或选牌器。");
        List<KnownRoutePrefix> prefixes = [];
        RunKnownSoulRouteReplay(combat, player, prefixes);
        if (prefixes.Count != 26)
            throw new InvalidOperationException("Soul 原版对照缺少全部 26 个冻结前缀。");
        Creature enemy = combat.Enemies.Single();
        MethodInfo endCombat = typeof(CombatManager).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == "EndCombatInternal"
                && method.GetParameters() is [{ ParameterType.Name: "CombatTurnState" }]);
        PropertyInfo stateProperty = endCombat.GetParameters()[0].ParameterType.GetProperty("State")
            ?? throw new MissingMemberException("CombatTurnState.State");
        MethodInfo prefixMethod = typeof(UnattendedTestRunner).GetMethod(
            nameof(ObserveMercuryCombatEndPrefix), BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(ObserveMercuryCombatEndPrefix));
        Harmony patch = new("CombatSolver.Testing.KnownSoulNative." + _request.RunId);
        MercuryTerminalObservation observation = new(this, combat, player, enemy, stateProperty, "KnownSoulNative");
        _mercuryTerminalObservation = observation;
        try
        {
            CombatManager.Instance.CombatEnded += observation.ObserveCombatEnded;
            patch.Patch(endCombat, prefix: new HarmonyMethod(prefixMethod));
            for (int index = 0; index < prefixes.Count; index++)
            {
                EnsureWithinDeadline();
                KnownRoutePrefix predicted = prefixes[index];
                PlanAction action = predicted.Action;
                if (!CombatManager.Instance.IsInProgress || observation.Snapshot != null
                    || player.PlayerCombatState is not { Phase: PlayerTurnPhase.Play } pcs
                    || pcs.TurnNumber != action.Turn)
                    throw new InvalidOperationException($"Soul 原版第 {index + 1} 步之前不在 T{action.Turn} Play。");
                KnownSoulNativeSelector selector = new(player, action.GetActionChoicesInExecutionOrder());
                using (CardSelectCmd.PushSelector(selector))
                {
                    if (action.Kind == PlanActionKind.PlayCard)
                    {
                        Creature? target = action.TargetCombatId is { } targetId
                            ? combat.Enemies.Single(candidate => candidate.CombatId == targetId) : null;
                        CardModel card = SolverController.FindCardForDeployment(pcs.Hand.Cards.ToList(), action);
                        if (!card.TryManualPlay(target))
                            throw new InvalidOperationException($"Soul 原版拒绝第 {index + 1} 步 {action.CardId}。");
                        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                    }
                    else if (action.Kind == PlanActionKind.EndTurn)
                    {
                        CombatManager.Instance.OnEndedTurnLocally();
                        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                            new EndPlayerTurnAction(player, action.Turn));
                        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                        while (player.PlayerCombatState is not { Phase: PlayerTurnPhase.Play } next
                            || next.TurnNumber <= action.Turn)
                        {
                            EnsureWithinDeadline();
                            observation.Failure?.Throw();
                            if (!CombatManager.Instance.IsInProgress || observation.Snapshot != null)
                                throw new InvalidOperationException("Soul 原版在计划结束回合时提前终局。");
                            await NextFrameAsync();
                        }
                    }
                    else
                        throw new InvalidOperationException("Soul 原版路线不允许药水或未记录动作。");
                    selector.AssertConsumed();
                }

                observation.Failure?.Throw();
                MoveStateSnapshot actual;
                if (index == prefixes.Count - 1)
                {
                    while (observation.Snapshot == null || !observation.CombatEnded || CombatManager.Instance.IsInProgress)
                    {
                        EnsureWithinDeadline();
                        observation.Failure?.Throw();
                        await NextFrameAsync();
                    }
                    actual = observation.Snapshot;
                    if (observation.Turn != 4 || actual.PlayerHp != 97 || actual.EnemyHp != 0)
                        throw new InvalidOperationException("Soul 原版终局不是 T4、97 HP、敌人全灭。");
                }
                else
                {
                    if (observation.Snapshot != null || !CombatManager.Instance.IsInProgress
                        || player.PlayerCombatState is not { Phase: PlayerTurnPhase.Play } next
                        || next.TurnNumber != predicted.Turn)
                        throw new InvalidOperationException($"Soul 原版第 {index + 1} 步后回合/阶段/终局不同。");
                    actual = CaptureActual(combat, player, enemy);
                }
                AssertSnapshotEqual(predicted.State, actual, "KnownSoulNative", $"prefix:{index + 1}");
                _completedChecks.Add($"KnownSoulNative:StrictPrefix:{index + 1}/26");
                Entry.Logger.Info($"[CombatSolver/Test] KNOWN_SOUL_NATIVE step={index + 1} " +
                    $"turn={predicted.Turn} hp={actual.PlayerHp} enemy_hp={actual.EnemyHp} " +
                    $"choices={action.GetActionChoicesInExecutionOrder().Count}");
            }
            _completedChecks.Add("KnownSoulNative:26NativeActions:5Primary:3NativeEndTurns:0ExtraChoices:0Potions:VictoryT4:HP97");
            return observation.Turn;
        }
        finally
        {
            try { patch.Unpatch(endCombat, prefixMethod); }
            finally
            {
                CombatManager.Instance.CombatEnded -= observation.ObserveCombatEnded;
                _mercuryTerminalObservation = null;
            }
        }
    }
}
