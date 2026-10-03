using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    /// <summary>
    /// T012 最小边界：GremlinMercNormal 的召唤槽位（sneaky/fat）只存在于原生场景，
    /// 不在 <see cref="MegaCrit.Sts2.Core.Models.EncounterModel.Slots" /> 中。根捕获之后，
    /// 预测召唤出的同类敌人进行目标名称枚举时，未知槽位必须仍能给出稳定位置，
    /// 不能中断展开。报告中的搜索状态同时存在两个 SneakyGremlin，因此这里构造
    /// 同类双实例边界；单实例会被 LINQ 的单元素快速路径掩盖。
    /// </summary>
    private async Task AssertDisplayNameSummonAsync(CombatState combat, Player player)
    {
        await ClearPlayerPilesAsync(player);
        await CreatureCmd.SetCurrentHp(player.Creature, player.Creature.MaxHp);

        Creature merc = combat.Enemies.Single(enemy => enemy.Monster is GremlinMerc);
        SolverDisplayNames displayNames = SolverDisplayNames.Capture(combat);
        await CreatureCmd.Kill(merc);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();

        Creature firstSneaky = combat.Enemies.SingleOrDefault(enemy => enemy.Monster is SneakyGremlin)
            ?? throw new InvalidOperationException("显示名召唤夹具：SurprisePower 未召唤 SneakyGremlin。");
        Creature fat = combat.Enemies.SingleOrDefault(enemy => enemy.Monster is FatGremlin)
            ?? throw new InvalidOperationException("显示名召唤夹具：SurprisePower 未召唤 FatGremlin。");

        // 与 CombatBeamSolver.EnumeratePlannedCardActions 的调用形态一致：预测敌人不在根捕获中，
        // 需要按同类型已知敌人确定显示位置。报告状态里的第二个同类实例按预测名单语义补足：
        // 它只进入 KnownEnemies，不加入实际战斗阵容，也不会触发视觉节点。
        Creature secondSneaky = combat.CreateCreature(
            ModelDb.Monster<SneakyGremlin>().ToMutable(), CombatSide.Enemy, "sneaky2");
        List<Creature> knownEnemies = [firstSneaky, secondSneaky, fat];
        string firstName = displayNames.Creature(firstSneaky, knownEnemies);
        string secondName = displayNames.Creature(secondSneaky, knownEnemies);
        string fatName = displayNames.Creature(fat, knownEnemies);
        if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(secondName)
            || string.IsNullOrWhiteSpace(fatName))
        {
            throw new InvalidOperationException("显示名召唤夹具：召唤敌人显示名为空。");
        }
        if (firstName == secondName)
            throw new InvalidOperationException("显示名召唤夹具：同类召唤敌人未区分显示位置。");
        _completedChecks.Add($"DisplayNameSummon:sneaky1={firstName}:sneaky2={secondName}:fat={fatName}");
    }
}
