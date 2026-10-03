using System.Reflection;
using CombatSolver.Engine.InCombat.Mirrors.Orbs;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;
using HarmonyLib;

namespace CombatSolver;

/// <summary>
/// #182/T022 取证：球数值评估不得把预测态交回原生 Hook 分派。
///
/// 现场包 3e32722d（0.47.2，DEFECT／LIVING_FOG_NORMAL）两条 SEARCH_FAILURE 同构：
/// System.EntryPointNotFoundException 于 System.Collections.Generic.IEnumerator`1.get_Current()，
/// 下一帧是 MegaCrit.Sts2.Core.Hooks.Hook.ModifyOrbValue(ICombatState, OrbModel, Decimal)，
/// 调用者帧是 CombatSolver.CombatBeamSolver.OrbRetentionValue -> Snapshot -> Replay（后台搜索线程）。
/// v0.47.2 的 OrbMirrors.ModifyValue 正是 Hook.ModifyOrbValue(simulator.State.CombatState, orb, baseValue)，
/// 即把预测态 ICombatState 交给原生 yield 迭代器 IterateCombatHookListeners；仓库内没有任何
/// EntryPointNotFoundException 的抛出点，所以该异常出自原生分派层内部。
///
/// 本夹具把这条边界钉成可判定的合同：在原版内容（INFUSED_CORE 遗物 + FOCUS_POWER）让原生
/// ModifyOrbValue 真的有两个监听器生效的前提下，用 Harmony 观察 Hook.ModifyOrbValue 的进入次数，
/// 然后调用现场失败的那条路径 CombatBeamSolver.OrbRetentionValue(simulator, orbs, aliveEnemyCount)，
/// 断言 (a) 预测侧进入原生 Hook 的次数为 0，(b) 预测球值与实机原生球值差分一致，
/// 且 (c) 原生球值确实被两个监听器改过（否则夹具是空断言）。
/// 把 OrbMirrors.ModifyValue 回退成修复前形态时，(a) 必须失败——这就是同一命令、同一输入的修改前失败。
/// </summary>
internal sealed partial class UnattendedTestRunner
{
    private static int _orbValueNativeHookEntries;

    private static void ObserveOrbValueNativeHookPrefix() => _orbValueNativeHookEntries++;

    private async Task AssertOrbValueStaysOffNativeHookAsync(CombatState combat, Player player)
    {
        // 只保留本夹具需要的原版监听器：清掉非目标遗物、既有 Power、牌堆与球，避免无关来源改变计数。
        // INFUSED_CORE 由 -RelicsPath 在进战斗前注入，这里必须保留它，否则原生 ModifyOrbValue 只剩 Focus 一个监听器。
        RelicModel infusedCore = player.Relics.FirstOrDefault(
            relic => relic.Id.Entry.Equals("INFUSED_CORE", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("夹具要求通过 -RelicsPath 注入原版 INFUSED_CORE 遗物。");
        foreach (RelicModel relic in player.Relics.Where(r => !ReferenceEquals(r, infusedCore)).ToArray())
            await RelicCmd.Remove(relic);
        foreach (PowerModel existing in combat.Creatures.SelectMany(static c => c.Powers).ToArray())
            await PowerCmd.Remove(existing);
        await ClearPlayerPilesAsync(player);
        foreach (OrbModel orb in player.PlayerCombatState!.OrbQueue.Orbs.ToArray())
            player.PlayerCombatState.OrbQueue.Remove(orb);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();

        var choiceContext = new BlockingPlayerChoiceContext();
        // 再施加原版 Focus，使原生 ModifyOrbValue 有两个监听器生效（InfusedCore + FocusPower）。
        await PowerCmd.Apply<FocusPower>(choiceContext, player.Creature, 2, player.Creature, null);
        await OrbCmd.Channel(choiceContext, ResolveOrbForTest("LIGHTNING_ORB").ToMutable(), player);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();

        OrbModel actualOrb = player.PlayerCombatState!.OrbQueue.Orbs.Single();

        MethodInfo nativeHook = typeof(Hook).GetMethod(
            nameof(Hook.ModifyOrbValue), BindingFlags.Static | BindingFlags.Public)
            ?? throw new MissingMethodException(nameof(Hook.ModifyOrbValue));
        MethodInfo observer = typeof(UnattendedTestRunner).GetMethod(
            nameof(ObserveOrbValueNativeHookPrefix), BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(ObserveOrbValueNativeHookPrefix));
        MethodInfo orbRetention = typeof(CombatBeamSolver).GetMethod(
            "OrbRetentionValue", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("CombatBeamSolver.OrbRetentionValue");

        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        CombatPredictionSimulator simulator = root.ForkSimulator();
        SimPlayerCombatState predictedPlayer = simulator.State.GetPlayerCombatState(player);
        OrbModel predictedOrb = predictedPlayer.OrbQueue.Orbs.Single();
        string liveText = ContinuationStamp.CaptureLive(combat).StateText;

        decimal expectedFromNative = Hook.ModifyOrbValue(combat, actualOrb, 3m);
        if (expectedFromNative <= 3m)
            throw new InvalidOperationException(
                $"原生 ModifyOrbValue 未被 INFUSED_CORE/Focus 两个监听器改变（base=3 actual={expectedFromNative}），夹具将退化为空断言。");

        Harmony probe = new("CombatSolver.Testing.OrbValueNativeHook." + _request.RunId);
        probe.Patch(nativeHook, prefix: new HarmonyMethod(observer));
        int predictedRetention;
        int nativeEntriesDuringPrediction;
        decimal predictedPassive;
        try
        {
            // 现场失败的那条路径：搜索快照里的球值评估。
            _orbValueNativeHookEntries = 0;
            object? retentionResult = orbRetention.Invoke(null, new object?[]
            {
                simulator,
                predictedPlayer.OrbQueue.Orbs.ToArray(),
                combat.Enemies.Count(c => c.IsAlive),
            });
            predictedRetention = retentionResult is int retentionValue
                ? retentionValue
                : throw new InvalidOperationException(
                    $"CombatBeamSolver.OrbRetentionValue 返回 {retentionResult?.GetType().Name ?? "null"}，不是 Int32。");
            predictedPassive = OrbMirrors.GetPassiveValue(simulator, predictedOrb);
            nativeEntriesDuringPrediction = _orbValueNativeHookEntries;
        }
        finally
        {
            _orbValueNativeHookEntries = 0;
            probe.Unpatch(nativeHook, observer);
        }

        if (nativeEntriesDuringPrediction != 0)
            throw new InvalidOperationException(
                $"预测侧球值评估进入了原生 Hook.ModifyOrbValue {nativeEntriesDuringPrediction} 次；" +
                "原生实现会 foreach 它的 yield 迭代器 IterateCombatHookListeners，现场包 3e32722d 的 " +
                "EntryPointNotFoundException 正落在该枚举器的 IEnumerator<T>.get_Current() 上。");

        if (predictedPassive != expectedFromNative)
            throw new InvalidOperationException(
                $"预测球值与实机原生球值不一致：predicted={predictedPassive} native={expectedFromNative}。");

        int nativeRetention = 0;
        foreach (OrbModel orb in player.PlayerCombatState.OrbQueue.Orbs)
            nativeRetention += (int)Math.Ceiling(Math.Max(0m, Hook.ModifyOrbValue(combat, orb, 3m)));
        if (predictedRetention != nativeRetention)
            throw new InvalidOperationException(
                $"OrbRetentionValue 与实机同口径不一致：predicted={predictedRetention} live={nativeRetention}。");

        if (ContinuationStamp.CaptureLive(combat).StateText != liveText)
            throw new InvalidOperationException("球值评估改写了实机根状态。");

        _ = infusedCore;
        _completedChecks.Add($"OrbValueNativeHook:NoNativeEntries:MirrorsNative={expectedFromNative}:Retention={predictedRetention}");
    }
}