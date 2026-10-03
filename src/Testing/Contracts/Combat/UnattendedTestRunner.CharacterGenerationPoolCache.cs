using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Extensions;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static void AssertRootCharacterAttackGenerationPoolCache(
        CombatPredictionSimulator simulator,
        Player player)
    {
        if (simulator.State.CombatState is not ICombatPredictionRunSnapshot runSnapshot
            || simulator.State.CombatState is not ICombatPredictionCardGenerationPoolSnapshot poolSnapshot)
        {
            throw new InvalidOperationException("角色生成牌根缓存测试缺少预测根状态接口。");
        }

        CardMultiplayerConstraint constraint = runSnapshot.CardMultiplayerConstraint;
        CardPoolModel characterPool = player.Character.CardPool;
        if (!RootCombatCardGenerationPoolSnapshot.CanCacheNativeCharacterPoolForTesting(
                characterPool)
            || !poolSnapshot.TryGetRootEligibleCharacterAttackCards(
                player,
                characterPool,
                constraint,
                out IReadOnlyList<CardModel>? rootEligible))
        {
            throw new InvalidOperationException("原生角色攻击牌池没有建立根级生成候选缓存。");
        }

        CardModel[] uncachedEligible = player.GetUnlockedCharacterCards(constraint)
            .Where(static card => card.Type == CardType.Attack)
            .FilterForCombatAndPlayerCount(constraint)
            .ToArray();
        if (rootEligible.Count != uncachedEligible.Length
            || rootEligible.Where((card, index) =>
                    !ReferenceEquals(card, uncachedEligible[index]))
                .Any())
        {
            throw new InvalidOperationException("根级角色攻击生成候选改变了原链路顺序或原型身份。");
        }

        CombatPredictionSimulator fork = simulator.Fork();
        if (fork.State.CombatState is not ICombatPredictionCardGenerationPoolSnapshot forkPoolSnapshot
            || !forkPoolSnapshot.TryGetRootEligibleCharacterAttackCards(
                player,
                characterPool,
                constraint,
                out IReadOnlyList<CardModel>? forkEligible)
            || !ReferenceEquals(rootEligible, forkEligible))
        {
            throw new InvalidOperationException("模拟 Fork 没有共享只读的根级角色攻击候选。");
        }

        CardMultiplayerConstraint mismatchedConstraint =
            constraint == CardMultiplayerConstraint.SingleplayerOnly
                ? CardMultiplayerConstraint.MultiplayerOnly
                : CardMultiplayerConstraint.SingleplayerOnly;
        if (poolSnapshot.TryGetRootEligibleCharacterAttackCards(
                player,
                characterPool,
                mismatchedConstraint,
                out _))
        {
            throw new InvalidOperationException("角色生成牌根缓存错误命中了不一致的多人约束。");
        }

        CardPoolModel mutablePool = characterPool.ToMutable();
        if (RootCombatCardGenerationPoolSnapshot.CanCacheNativeCharacterPoolForTesting(mutablePool)
            || poolSnapshot.TryGetRootEligibleCharacterAttackCards(
                player,
                mutablePool,
                constraint,
                out _))
        {
            throw new InvalidOperationException("角色生成牌根缓存错误命中了 mutable/identity 不一致的牌池。");
        }

        ModCharacterPoolProbe modPool = ModelDb.CardPool<ModCharacterPoolProbe>();
        if (RootCombatCardGenerationPoolSnapshot.CanCacheNativeCharacterPoolForTesting(modPool)
            || poolSnapshot.TryGetRootEligibleCharacterAttackCards(
                player,
                modPool,
                constraint,
                out _))
        {
            throw new InvalidOperationException("角色生成牌根缓存错误命中了 custom/mod 牌池。");
        }

        PredictionRngState sourceRng = simulator.Rng.CombatCardGeneration.CaptureState();
        foreach (int count in new[] { 0, 1, 3, 5 })
        {
            var baselineRng = simulator.Rng.CombatCardGeneration.Clone();
            var cachedRng = simulator.Rng.CombatCardGeneration.Clone();
            PredictedCard[] baseline = player.GetUnlockedCharacterCards(constraint)
                .Where(static card => card.Type == CardType.Attack)
                .GetForCombat(player, count, baselineRng, constraint)
                .ToArray();
            PredictedCard[] cached = simulator
                .GetUnlockedCharacterAttacksForCombat(
                    player,
                    count,
                    cachedRng,
                    constraint)
                .ToArray();
            PredictionRngState baselineState = baselineRng.CaptureState();
            PredictionRngState cachedState = cachedRng.CaptureState();
            if (baseline.Length != cached.Length
                || baseline.Where((card, index) =>
                        ReferenceEquals(card, cached[index])
                        || ReferenceEquals(card.Original, cached[index].Original)
                        || card.Preview.GetType() != cached[index].Preview.GetType()
                        || card.Preview.Id != cached[index].Preview.Id
                        || card.Preview.CurrentUpgradeLevel
                            != cached[index].Preview.CurrentUpgradeLevel
                        || !ReferenceEquals(card.Preview.Owner, player)
                        || !ReferenceEquals(cached[index].Preview.Owner, player)
                        || CombatBeamSolver.CaptureCardStateFingerprintForTesting(card)
                            != CombatBeamSolver.CaptureCardStateFingerprintForTesting(cached[index]))
                    .Any()
                || !SameFiveFieldRngState(baselineState, cachedState))
            {
                throw new InvalidOperationException(
                    $"角色攻击生成候选缓存改变了 count={count} 的顺序、卡牌或 RNG 五字段：" +
                    $"baseline_ids=[{string.Join(',', baseline.Select(card => card.Preview.Id.Entry))}] " +
                    $"cached_ids=[{string.Join(',', cached.Select(card => card.Preview.Id.Entry))}] " +
                    $"baseline_rng={FormatFiveFieldRngState(baselineState)} " +
                    $"cached_rng={FormatFiveFieldRngState(cachedState)}。");
            }
        }

        if (!SameFiveFieldRngState(
                simulator.Rng.CombatCardGeneration.CaptureState(),
                sourceRng))
        {
            throw new InvalidOperationException("角色生成牌缓存 shadow 测试推进了原模拟器 RNG。");
        }

        if (rootEligible.Count == 0)
            return;
        var firstRng = simulator.Rng.CombatCardGeneration.Clone();
        var secondRng = simulator.Rng.CombatCardGeneration.Clone();
        PredictedCard first = simulator
            .GetUnlockedCharacterAttacksForCombat(player, 1, firstRng, constraint)
            .Single();
        PredictedCard second = simulator
            .GetUnlockedCharacterAttacksForCombat(player, 1, secondRng, constraint)
            .Single();
        if (ReferenceEquals(first, second)
            || ReferenceEquals(first.Original, second.Original)
            || first.Preview.Id != second.Preview.Id
            || CombatBeamSolver.CaptureCardStateFingerprintForTesting(first)
                != CombatBeamSolver.CaptureCardStateFingerprintForTesting(second))
        {
            throw new InvalidOperationException("角色生成牌缓存没有创建隔离的等价分支卡牌。");
        }

        CardModel canonicalSelected = rootEligible.Single(card => card.Id == first.Preview.Id);
        int canonicalReplayCount = canonicalSelected.BaseReplayCount;
        int secondReplayCount = second.Preview.BaseReplayCount;
        first.MutablePreview.BaseReplayCount++;
        if (second.Preview.BaseReplayCount != secondReplayCount
            || canonicalSelected.BaseReplayCount != canonicalReplayCount)
        {
            throw new InvalidOperationException("角色生成牌缓存的突变污染了兄弟分支或 canonical 原型。");
        }
    }

    private static bool SameFiveFieldRngState(
        PredictionRngState first,
        PredictionRngState second)
        => first.Counter == second.Counter
           && first.State0 == second.State0
           && first.State1 == second.State1
           && first.State2 == second.State2
           && first.State3 == second.State3;

    private static string FormatFiveFieldRngState(PredictionRngState state)
        => $"(counter={state.Counter},s0=0x{state.State0:X16},s1=0x{state.State1:X16}," +
           $"s2=0x{state.State2:X16},s3=0x{state.State3:X16})";

    private sealed class ModCharacterPoolProbe : CardPoolModel
    {
        public override string Title => "combat_solver_mod_pool_probe";
        public override string EnergyColorName => "colorless";
        public override string CardFrameMaterialPath => "card_frame_colorless";
        public override Color DeckEntryCardColor => Colors.White;
        public override bool IsColorless => false;

        protected override CardModel[] GenerateAllCards()
            => [];
    }
}
