using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    /// <summary>
    /// 报告包 21980d83（0.47.0 原版小啃兽遭遇）：方案路线里墨刃生成的墨染小刀打出后，
    /// 灯应记一次消费、目标身上应留下墨染的虚弱，并把它记成触发来源。搜索缓存的回合边界
    /// 状态就是下一次续用的期望值，必须与实机打出同一前缀后的状态逐字段一致。
    /// </summary>
    private async Task AssertLampInkyShivRouteContinuationAsync(CombatState combat, Player player)
    {
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in combat.Creatures.SelectMany(creature => creature.Powers).ToArray())
            await PowerCmd.Remove(power);
        player.AddRelicInternal(ModelDb.Relic<UnsettlingLamp>().ToMutable());
        foreach (Creature enemy in combat.Enemies) await CreatureCmd.SetCurrentHp(enemy, 200);
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "BLADE_OF_INK", Pile = "Hand" });
        SetEnergy(player, 3);

        int turn = player.PlayerCombatState!.TurnNumber;
        Creature target = combat.Enemies[0];
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null) with
        {
            FixedBudget = true,
            MaxDegreeOfParallelism = 1,
            BudgetOverrideMilliseconds = 20000,
            PotionPolicy = SolverPotionPolicy.Disabled,
            PotionStrategy = new PotionStrategySnapshot(SolverPotionPolicy.Disabled, []),
            Profile = SolverSearchProfile.Default with
            {
                BeamWidth = 8, MaxExpandedNodes = 300, SoftTimeBudgetMilliseconds = 20000,
            },
        };
        PlanAction[] prefix =
        [
            new(PlanActionKind.PlayCard, turn, CardId: "BLADE_OF_INK"),
            new(PlanActionKind.PlayCard, turn, CardId: "SHIV", TargetCombatId: target.CombatId),
        ];
        SolverResult result = await Task.Run(() => new CombatBeamSolver(root, names, damage, policy,
            searchProfile: policy.Profile, fixedPrefixActions: prefix).Solve());

        if (!FindActualHandCard(player, "BLADE_OF_INK", 0).TryManualPlay(null))
            throw new InvalidOperationException("Lamp route fixture BladeOfInk play failed.");
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        CardModel shiv = FindActualHandCard(player, "SHIV", 0);
        if (shiv.Enchantment?.GetType() != typeof(Inky))
            throw new InvalidOperationException("Lamp route fixture Shiv is missing its Inky enchantment.");
        if (!shiv.TryManualPlay(target))
            throw new InvalidOperationException("Lamp route fixture Shiv play failed.");
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        CombatManager.Instance.OnEndedTurnLocally();
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, turn));
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        while (player.PlayerCombatState is not { Phase: PlayerTurnPhase.Play } current || current.TurnNumber <= turn)
        {
            EnsureWithinDeadline();
            if (!CombatManager.Instance.IsInProgress)
                throw new InvalidOperationException("Lamp route fixture ended combat.");
            await NextFrameAsync();
        }

        // 来源身份：原版灯把触发它的那张牌记下来，预测侧必须用同一个生成卡实例。
        UnsettlingLamp lamp = player.Relics.OfType<UnsettlingLamp>().Single();
        CardModel? triggering = (CardModel?)typeof(UnsettlingLamp)
            .GetField("_triggeringCard", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lamp);
        if (!ReferenceEquals(triggering, shiv))
            throw new InvalidOperationException(
                $"Lamp triggering card identity mismatch: native={triggering?.Id.Entry ?? "null"} played={shiv.Id.Entry}.");

        int boundaryTurn = player.PlayerCombatState!.TurnNumber;
        CachedContinuation? cached = result.Continuations
            .FirstOrDefault(item => item.StartTurnNumber == boundaryTurn);
        if (cached == null)
            throw new InvalidOperationException(
                $"Lamp route continuation missing: boundaries=[{string.Join(',', result.Continuations.Select(item => item.StartTurnNumber))}] "
                + $"liveTurn={boundaryTurn}");
        ContinuationStamp actual = ContinuationStamp.CaptureLive(combat);
        if (cached.ExpectedState != actual)
            throw new InvalidOperationException("Lamp route continuation mismatch: "
                + string.Join("; ", cached.ExpectedState.DescribeDifferences(actual, 8)));
        _completedChecks.Add("LampInkyShiv:RouteContinuation:CardSourceIdentitySearchBoundaryMatchesLiveTurn");
    }
}
