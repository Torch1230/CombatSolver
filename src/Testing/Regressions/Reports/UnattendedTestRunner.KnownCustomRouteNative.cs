using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private sealed class KnownCustomNativeSelector(Player player, IReadOnlyList<PlanCardChoice> choices)
        : ICardSelector
    {
        private int _index;

        public Task<IEnumerable<CardModel>> GetSelectedCards(
            IEnumerable<CardModel> options, int minSelect, int maxSelect)
        {
            if (_index >= choices.Count)
                throw new InvalidOperationException("已知路线原版请求了额外选牌。");
            PlanCardChoice choice = choices[_index];
            if (choice.Effect != PlanChoiceEffect.Exhaust || choice.SourcePile != PileType.Hand
                || choice.Timing != PlanChoiceTiming.Action || choice.Cards is not [var token]
                || minSelect > 1 || maxSelect < 1)
                throw new InvalidOperationException("已知路线原版请求不符合唯一手牌消耗选择。");
            List<CardModel> available = options.ToList();
            CardModel selected = available.Where(card => CardChoiceSupport.MatchesToken(card, token))
                .Skip(token.OptionOccurrence).FirstOrDefault()
                ?? throw new InvalidOperationException("已知路线原版选牌缺少准确候选游标。");
            IReadOnlyList<CardModel> hand = player.PlayerCombatState?.Hand.Cards
                ?? throw new InvalidOperationException("已知路线原版选牌时没有手牌。");
            if (!hand.Any(card => ReferenceEquals(card, selected))
                || hand.TakeWhile(card => !ReferenceEquals(card, selected))
                    .Count(card => CardChoiceSupport.MatchesToken(card, token)) != token.SourceOccurrence
                || !string.Equals(CardChoiceSupport.ChoiceCardKey(selected), token.StateKey, StringComparison.Ordinal))
                throw new InvalidOperationException("已知路线原版选牌实例状态或源牌堆游标与计划不同。");
            _index++;
            return Task.FromResult<IEnumerable<CardModel>>([selected]);
        }

        public CardRewardSelection GetSelectedCardReward(
            IReadOnlyList<CardCreationResult> options,
            IReadOnlyList<CardRewardAlternative> alternatives)
            => throw new InvalidOperationException("已知路线原版出现未计划的卡牌奖励选择。");

        public void AssertConsumed()
        {
            if (_index != choices.Count)
                throw new InvalidOperationException("已知路线原版仍有未执行的计划选牌。");
        }
    }

    // Explicit native replay establishes feasibility, not the ability of Solve to discover it.
    // All nineteen predictions are frozen before the first live action; the native selector
    // consumes exactly those semantic tokens and rejects unexpected requests even for no-choice cards.
    private async Task<int> RunKnownCustomRouteNativeAsync(CombatState combat, Player player)
    {
        if (_mercuryTerminalObservation != null || CardSelectCmd.Selector != null)
            throw new InvalidOperationException("已知路线原版对照不能覆盖现有终局观察者或选牌器。");
        List<(PlanAction Action, MoveStateSnapshot State)> prefixes = [];
        RunKnownCustomRouteReplay(combat, player, prefixes);
        if (prefixes.Count != 19)
            throw new InvalidOperationException("已知路线原版对照未获得十九个冻结的预测状态。");
        Creature enemy = combat.Enemies.Single();
        MethodInfo endCombat = typeof(CombatManager).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == "EndCombatInternal"
                && method.GetParameters() is [{ ParameterType.Name: "CombatTurnState" }]);
        PropertyInfo stateProperty = endCombat.GetParameters()[0].ParameterType.GetProperty("State")
            ?? throw new MissingMemberException("CombatTurnState.State");
        MethodInfo prefixMethod = typeof(UnattendedTestRunner).GetMethod(
            nameof(ObserveMercuryCombatEndPrefix), BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(ObserveMercuryCombatEndPrefix));
        Harmony patch = new("CombatSolver.Testing.KnownCustomNative." + _request.RunId);
        MercuryTerminalObservation observation = new(this, combat, player, enemy, stateProperty, "KnownCustomNative");
        _mercuryTerminalObservation = observation;
        try
        {
            CombatManager.Instance.CombatEnded += observation.ObserveCombatEnded;
            patch.Patch(endCombat, prefix: new HarmonyMethod(prefixMethod));
            for (int index = 0; index < prefixes.Count; index++)
            {
                EnsureWithinDeadline();
                if (!CombatManager.Instance.IsInProgress || observation.Snapshot != null
                    || player.PlayerCombatState is not { Phase: PlayerTurnPhase.Play, TurnNumber: 1 })
                    throw new InvalidOperationException($"已知路线原版第 {index} 步之前已经离开 T1 Play。");
                (PlanAction action, MoveStateSnapshot predicted) = prefixes[index];
                Creature? target = action.TargetCombatId is { } targetId
                    ? combat.Enemies.Single(candidate => candidate.CombatId == targetId)
                    : null;
                KnownCustomNativeSelector selector = new(player, action.GetActionChoicesInExecutionOrder());
                using (CardSelectCmd.PushSelector(selector))
                {
                    if (action.Kind == PlanActionKind.UsePotion)
                    {
                        PotionModel potion = player.GetPotionAtSlotIndex(action.PotionSlot)
                            ?? throw new InvalidOperationException("已知路线原版药水槽为空。");
                        if (potion.Id.Entry != action.PotionId || !potion.IsValidTarget(target))
                            throw new InvalidOperationException("已知路线原版药水身份或目标与计划不符。");
                        potion.EnqueueManualUse(target);
                    }
                    else if (action.Kind == PlanActionKind.PlayCard)
                    {
                        CardModel card = SolverController.FindCardForDeployment(
                            player.PlayerCombatState.Hand.Cards.ToList(), action);
                        if (!card.TryManualPlay(target))
                            throw new InvalidOperationException($"原版拒绝已知路线第 {index} 步 {action.CardId}。");
                    }
                    else
                        throw new InvalidOperationException("已知路线原版对照不接受合成的结束回合动作。");
                    await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
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
                    if (observation.Turn != 1 || actual.PlayerHp != 1 || actual.EnemyHp != 0)
                        throw new InvalidOperationException("已知路线原版终局未达到 T1、1 HP 和敌人全灭。");
                }
                else
                {
                    if (observation.Snapshot != null || !CombatManager.Instance.IsInProgress)
                        throw new InvalidOperationException($"已知路线原版在第 {index} 步提前结束。");
                    actual = CaptureActual(combat, player, enemy);
                }
                AssertSnapshotEqual(predicted, actual, "KnownCustomNative", $"prefix:{index}");
                _completedChecks.Add($"KnownCustomNative:StrictPrefix:{index + 1}/19");
                Entry.Logger.Info($"[CombatSolver/Test] KNOWN_CUSTOM_NATIVE index={index} " +
                    $"hp={actual.PlayerHp} enemy_hp={actual.EnemyHp} choices={action.GetActionChoicesInExecutionOrder().Count}");
            }
            _completedChecks.Add("KnownCustomNative:19NativeActions:3Primary:0Nested:VictoryT1:HP1");
            return observation.Turn;
        }
        finally
        {
            try
            {
                patch.Unpatch(endCombat, prefixMethod);
            }
            finally
            {
                CombatManager.Instance.CombatEnded -= observation.ObserveCombatEnded;
                _mercuryTerminalObservation = null;
            }
        }
    }
}
