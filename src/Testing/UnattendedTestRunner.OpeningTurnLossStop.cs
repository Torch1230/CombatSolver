using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertOpeningTurnLossStopAsync(CombatState combat, Player player)
    {
        if (combat.Enemies.Count != 1 || player.PlayerCombatState?.TurnNumber != 1)
            throw new InvalidOperationException("首回合战损夹具要求单敌、玩家第一回合。");
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in combat.Creatures.SelectMany(creature => creature.Powers).ToArray())
            await PowerCmd.Remove(power);
        await PowerCmd.Apply<StrengthPower>(new BlockingPlayerChoiceContext(),
            combat.Enemies[0], 6, combat.Enemies[0], null);
        async Task<SolverResult> Search(int defendCount, int enemyHp = 35,
            string? openingCard = null, string? drawCard = null,
            bool stopAtHpTarget = true, int maxNodes = 512, int parallelism = 1)
        {
            await CreatureCmd.SetCurrentHp(combat.Enemies[0], enemyHp);
            await ClearPlayerPilesAsync(player);
            foreach (string cardId in Enumerable.Repeat("STRIKE_IRONCLAD",
                             5 - defendCount - (openingCard == null ? 0 : 1))
                         .Concat(Enumerable.Repeat("DEFEND_IRONCLAD", defendCount))
                         .Concat(openingCard == null ? [] : [openingCard]))
                await InjectCardAsync(combat, player,
                    new UnattendedCardInjection { CardId = cardId, Pile = "Hand" });
            if (drawCard != null)
                await InjectCardAsync(combat, player,
                    new UnattendedCardInjection { CardId = drawCard, Pile = "Draw" });
            SetEnergy(player, 3);
            CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
            int incoming = root.Forecast.Rounds[0].SelectMany(move => move.AttackHits)
                .Sum(hit => hit.Damage);
            if (incoming != 10 || root.HasVisibleHealingSource || root.SearchablePotionCount != 0)
                throw new InvalidOperationException($"首回合战损夹具根不匹配：incoming={incoming} " +
                    $"healing={root.HasVisibleHealingSource} potions={root.SearchablePotionCount}");
            SolverDisplayNames names = SolverDisplayNames.Capture(combat);
            SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
                SolverSettings.Capture(), combat, false, null) with
            {
                FixedBudget = true,
                BudgetOverrideMilliseconds = 8000,
                PotionPolicy = SolverPotionPolicy.Disabled,
                StopAtAcceptableBattleHpLoss = stopAtHpTarget,
                MaxDegreeOfParallelism = parallelism,
                DetailedDiagnostics = false,
                VerifyIncrementalSearch = false,
                UseBeamWidthPortfolio = true,
                Profile = SolverSearchProfile.Default with
                {
                    MaxExpandedNodes = maxNodes,
                    StopPortfolioAtHpTarget = true,
                },
            };
            return await Task.Run(() => CombatSearchCoordinator.Solve(root, names,
                new BattleDamageSnapshot(0, 0, 0, []), policy, CancellationToken.None, null));
        }

        SolverResult strikes = await Search(defendCount: 0);
        if (strikes.ProjectedBattleHpLost != 10
            || strikes.HpLostByTurn.GetValueOrDefault(1) != 10
            || strikes.ExhaustiveOpeningTurnHpLoss != 10)
            throw new InvalidOperationException("五张打击未建立首回合必损 10 的搜索证据。");
        _completedChecks.Add($"OpeningTurnLoss:FiveStrikes:loss={strikes.ProjectedBattleHpLost}:" +
            $"first={strikes.HpLostByTurn.GetValueOrDefault(1)}:" +
            $"nodes={strikes.TotalExpandedNodes}:" +
            $"members={strikes.PortfolioTelemetry?.Members.Count}");
        SolverResult serialTwin = await Search(defendCount: 0,
            openingCard: "TWIN_STRIKE");
        SolverResult parallel = await Search(defendCount: 0,
            openingCard: "TWIN_STRIKE", parallelism: 2);
        if (serialTwin.ProjectedBattleHpLost != 10
            || serialTwin.ExhaustiveOpeningTurnHpLoss != 10
            || parallel.ProjectedBattleHpLost != serialTwin.ProjectedBattleHpLost
            || parallel.ExhaustiveOpeningTurnHpLoss != serialTwin.ExhaustiveOpeningTurnHpLoss
            || parallel.MaxParallelExpansionConcurrency < 2)
            throw new InvalidOperationException("并行首回合纯攻击路线未保持 10 战损与早停边界。");
        _completedChecks.Add($"OpeningTurnLoss:ParallelPureAttack:loss={parallel.ProjectedBattleHpLost}:" +
            $"serial_nodes={serialTwin.TotalExpandedNodes}:parallel_nodes={parallel.TotalExpandedNodes}:" +
            $"max_concurrency={parallel.MaxParallelExpansionConcurrency}");
        SolverResult serialFixed = await Search(defendCount: 0,
            openingCard: "TWIN_STRIKE", stopAtHpTarget: false, maxNodes: 128);
        SolverResult parallelFixed = await Search(defendCount: 0,
            openingCard: "TWIN_STRIKE", stopAtHpTarget: false, maxNodes: 128,
            parallelism: 2);
        if (!serialFixed.BestNode.Actions.SequenceEqual(parallelFixed.BestNode.Actions)
            || serialFixed.BestNode.Score != parallelFixed.BestNode.Score
            || serialFixed.ProjectedBattleHpLost != parallelFixed.ProjectedBattleHpLost
            || serialFixed.ExpandedNodes != parallelFixed.ExpandedNodes
            || serialFixed.TransitionCount != parallelFixed.TransitionCount
            || serialFixed.DominatedActionsPruned != parallelFixed.DominatedActionsPruned
            || serialFixed.TopQueueActionsDropped != parallelFixed.TopQueueActionsDropped
            || serialFixed.TranspositionBranchesPruned != parallelFixed.TranspositionBranchesPruned
            || parallelFixed.MaxParallelExpansionConcurrency < 2)
            throw new InvalidOperationException("关闭早停后的固定节点 DOP1/DOP2 搜索不等价。");
        _completedChecks.Add($"OpeningTurnLoss:ParallelFixedEquivalent:nodes={serialFixed.ExpandedNodes}:" +
            $"transitions={serialFixed.TransitionCount}:max_concurrency={parallelFixed.MaxParallelExpansionConcurrency}");
        SolverResult defenses = await Search(defendCount: 2);
        if (defenses.ProjectedBattleHpLost != 0
            || defenses.HpLostByTurn.GetValueOrDefault(1) != 0
            || defenses.ExhaustiveOpeningTurnHpLoss != null)
            throw new InvalidOperationException("有防御牌时不应触发首回合必损早停。");
        _completedChecks.Add($"OpeningTurnLoss:TwoDefends:loss={defenses.ProjectedBattleHpLost}:" +
            $"first={defenses.HpLostByTurn.GetValueOrDefault(1)}:" +
            $"nodes={defenses.TotalExpandedNodes}:" +
            $"members={defenses.PortfolioTelemetry?.Members.Count}");
        SolverResult hemokinesis = await Search(defendCount: 0, enemyHp: 45,
            drawCard: "HEMOKINESIS");
        SolverResult strict = await Search(defendCount: 0, enemyHp: 45,
            drawCard: "HEMOKINESIS", stopAtHpTarget: false);
        if (hemokinesis.ExhaustiveOpeningTurnHpLoss != 10
            || hemokinesis.ProjectedBattleHpLost != 12
            || strict.ProjectedBattleHpLost != 10
            || hemokinesis.TotalExpandedNodes >= strict.TotalExpandedNodes)
            throw new InvalidOperationException("首回合必损下的两点后续容差未按预期收束。");
        _completedChecks.Add($"OpeningTurnLoss:Hemokinesis:target_loss={hemokinesis.ProjectedBattleHpLost}:" +
            $"target_nodes={hemokinesis.TotalExpandedNodes}:strict_loss={strict.ProjectedBattleHpLost}:" +
            $"strict_nodes={strict.TotalExpandedNodes}");
        SolverResult limited = await Search(defendCount: 0, maxNodes: 2);
        if (limited.ExhaustiveOpeningTurnHpLoss != null)
            throw new InvalidOperationException("首回合节点预算截断后不能建立必损证据。");
        _completedChecks.Add($"OpeningTurnLoss:Truncated:floor=none:nodes={limited.TotalExpandedNodes}");
        foreach ((string cardId, string? drawCard) in new[]
                 {
                     ("INFLAME", (string?)null),
                     ("POMMEL_STRIKE", "STRIKE_IRONCLAD"),
                     ("ANGER", (string?)null),
                 })
        {
            SolverResult complex = await Search(defendCount: 0, openingCard: cardId,
                drawCard: drawCard, maxNodes: 128);
            if (complex.ExhaustiveOpeningTurnHpLoss != null
                || !complex.OpeningTurnComplexCardEffectObserved
                || cardId == "POMMEL_STRIKE"
                    && complex.HpLostByTurn.GetValueOrDefault(1) != 10)
                throw new InvalidOperationException($"首回合 {cardId} 被错误纳入简单开局早停。");
            _completedChecks.Add($"OpeningTurnLoss:Complex:{cardId}:floor=none:" +
                $"loss={complex.ProjectedBattleHpLost}:nodes={complex.TotalExpandedNodes}");
        }
    }
}
