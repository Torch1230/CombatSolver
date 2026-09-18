using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Rewards;
using HarmonyLib;
using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Modding;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static void AssertKnownGameplayModBoundary()
    {
        foreach (string modId in new[] { "WheelchairSpire", "PengoTarot", "BetterCharacterRelics" })
            AssertKnownGameplayModBoundary(modId);
        PredictionModPatchAudit.ValidateLoadedMods([]);
    }

    private static void AssertKnownGameplayModBoundary(string modId)
    {
        ModManifest manifest = new() { id = modId, name = modId, affectsGameplay = false };
        Mod byId = new() { path = "unattended-incompatible-mod", manifest = manifest };
        Mod byAssembly = new()
        {
            path = "unattended-incompatible-assembly",
            manifest = new ModManifest { id = "renamed-mod", name = "Renamed Mod", affectsGameplay = true },
        };
        byAssembly.assemblies.Add(System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(
            new System.Reflection.AssemblyName(modId),
            System.Reflection.Emit.AssemblyBuilderAccess.RunAndCollect));
        foreach (Mod mod in new[] { byId, byAssembly })
        {
            try
            {
                PredictionModPatchAudit.ValidateLoadedMods([mod]);
                throw new InvalidOperationException("Known incompatible gameplay mod was admitted.");
            }
            catch (IncompatibleGameplayModException exception)
            {
                if (exception.ModId != mod.manifest!.id
                    || !exception.Subject.Contains(modId, StringComparison.Ordinal))
                    throw new InvalidOperationException("Incompatible mod rejection lost source context.");
            }
        }
        PredictionModPatchAudit.ValidateLoadedMods([]);
    }

    private static class ForeignCardPatch
    {
        public static bool Prefix() => false;
    }

    private void AssertForeignCardPatchBoundary(CombatState combat)
    {
        CardModel[] cards = combat.Players.SelectMany(player => player.PlayerCombatState!.AllCards).ToArray();
        CardModel card = cards.First();
        var method = AccessTools.Method(card.GetType(), "OnPlay",
            [typeof(MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceContext),
             typeof(MegaCrit.Sts2.Core.Entities.Cards.CardPlay)]);
        var prefix = AccessTools.Method(typeof(ForeignCardPatch), nameof(ForeignCardPatch.Prefix));
        Harmony harmony = new("CombatSolver.Unattended.Pr18");
        var previousMocks = AssemblyInfo.MockTypes;
        ModManifest manifest = new() { id = "PR18-TEST", name = "PR18 Test", affectsGameplay = true };
        Mod mod = new() { path = "unattended-pr18", manifest = manifest };
        AssemblyInfo.MockTypes = previousMocks == null ? [] : new(previousMocks);
        AssemblyInfo.MockTypes[typeof(ForeignCardPatch)] = (mod, false);
        ContinuationStamp before = ContinuationStamp.CaptureLive(combat);
        try
        {
            _ = CombatRootSnapshot.Capture(combat);
            harmony.Patch(method, prefix: new HarmonyMethod(prefix));
            try
            {
                _ = CombatRootSnapshot.Capture(combat);
                throw new InvalidOperationException("首次根捕获之后新增的玩法补丁没有被拒绝。");
            }
            catch (IncompatibleGameplayModException ex)
            {
                if (ex.ModId != manifest.id || !ex.Subject.Contains("OnPlay", StringComparison.Ordinal))
                    throw new InvalidOperationException("补丁失败缺少 Mod 和方法上下文。", ex);
            }
            manifest.affectsGameplay = false;
            _ = CombatRootSnapshot.Capture(combat);
            AssemblyInfo.MockTypes[typeof(ForeignCardPatch)] = (null, false);
            try
            {
                PredictionModPatchAudit.ValidateCardOnPlay(cards);
                throw new InvalidOperationException("未知来源的玩法补丁被静默放行。");
            }
            catch (PredictionUnsupportedException ex)
            {
                if (!ex.Message.Contains("CombatSolver.Unattended.Pr18", StringComparison.Ordinal))
                    throw new InvalidOperationException("未知来源补丁失败缺少 owner 上下文。", ex);
            }
            manifest.affectsGameplay = true;
            AssemblyInfo.MockTypes[typeof(ForeignCardPatch)] = (mod, false);
            harmony.Unpatch(method, prefix);
            _ = CombatRootSnapshot.Capture(combat);
            if (ContinuationStamp.CaptureLive(combat) != before)
                throw new InvalidOperationException("补丁审计修改了真实战斗状态。");
            _completedChecks.Add("ForeignOnPlay:LatePatch:Neutral:Unknown:Unpatch:RootUnchanged");
        }
        finally
        {
            harmony.Unpatch(method, prefix);
            AssemblyInfo.MockTypes = previousMocks;
        }
    }

    private void AssertPotionValueTiers(CombatState combat)
    {
        if (PotionUsePolicy.StrategicHpCost("SWIFT_POTION") != 18
            || PotionUsePolicy.StrategicHpCost("CLARITY") != 14
            || PotionUsePolicy.StrategicHpCost("FIRE_POTION") != 9
            || PotionUsePolicy.StrategicHpCost("AMBERGRIS") != 9
            || PotionUsePolicy.StrategicHpCost(ModelDb.Potion<PotionShapedRock>(), true) != 0
            || ModelDb.AllPotions.Where(potion => potion.Rarity == PotionRarity.Token)
                .Any(potion => PotionUsePolicy.StrategicHpCost(potion) != 0))
            throw new InvalidOperationException("药水档位或免费药水例外不符合预期。");

        foreach (int threshold in new[] { 9, 14, 18 })
        {
            bool Eligible(int saved) => PotionUsePolicy.IsEligible(
                SolverPotionPolicy.Smart, 1, threshold, true, 30, true, true, 30 - saved);
            if (Eligible(threshold - 1) || !Eligible(threshold))
                throw new InvalidOperationException($"Smart 药水 {threshold} HP 门槛错误。");
        }
        if (PotionUsePolicy.EffectiveStrategicHpCost(9, 1, 80) != 32
            || !PotionUsePolicy.IsEligible(SolverPotionPolicy.Smart, 1, 18, false, 0, true, true, 0)
            || !PotionUsePolicy.IsEligible(SolverPotionPolicy.RequireAtLeastOne, 1, 18, true, 0, true, true, 0))
            throw new InvalidOperationException("药水分档改变了龙涎香、救命或强制用药规则。");

        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        if (!root.SearchablePotions.Any(potion => potion.PotionId == "SWIFT_POTION" && potion.StrategicHpCost == 18))
            throw new InvalidOperationException("搜索根没有捕获 Swift 药水的 18 HP 成本。");
        AssertPotionRewardOutlook(combat, root);
        _completedChecks.Add("PotionValueTiers:Thresholds:Free:Ambergris:Rescue:Forced:Root:RewardOutlook");
    }

    private static void AssertPotionRewardOutlook(CombatState combat, CombatRootSnapshot root)
    {
        // 掷骰阈值与原版 PotionRewardOdds.Roll 一致：保存的概率加半份精英加成，夹在 [0, 1]。
        if (Math.Abs(PotionRewardOutlook.DropChanceFor(0.4f, RoomType.Elite) - 0.525f) > 0.0001f
            || PotionRewardOutlook.DropChanceFor(1.2f, RoomType.Monster) != 1f
            || PotionRewardOutlook.DropChanceFor(0.4f, RoomType.Boss) != 0.4f)
            throw new InvalidOperationException("药水掉落概率镜像与原版掷骰阈值不符。");
        // 额度只在药水栏已满且能获得药水时存在，按基线档位乘概率四舍五入。
        if (new PotionRewardOutlook(1f, true, false).ReplacementHpCredit != SolverWeights.PotionMinimumHpSaved
            || new PotionRewardOutlook(0.4f, true, false).ReplacementHpCredit != 4
            || new PotionRewardOutlook(0.5f, true, true).ReplacementHpCredit != 0
            || new PotionRewardOutlook(1f, false, false).ReplacementHpCredit != 0
            || PotionRewardOutlook.None.ReplacementHpCredit != 0)
            throw new InvalidOperationException("药水补货额度计算错误。");
        // 额度按路线只扣一次，不按瓶数重复扣，也不会把成本扣成负数。
        if (PotionUsePolicy.ApplyReplacementCredit(18, 1, 9) != 9
            || PotionUsePolicy.ApplyReplacementCredit(9, 2, 9) != 0
            || PotionUsePolicy.ApplyReplacementCredit(18, 0, 9) != 18
            || PotionUsePolicy.ApplyReplacementCredit(18, 1, 0) != 18
            || PotionUsePolicy.ApplyReplacementCredit(4, 1, 9) != 0)
            throw new InvalidOperationException("药水补货额度的路线级扣减错误。");
        // 镜像出确定结果时按那瓶药的档位计价，镜像不出时才退回概率。
        if (new PotionRewardOutlook(0.4f, true, false) { Forecast = PotionRewardForecast.Drop, ForecastPotionId = "SWIFT_POTION", ForecastPotionStrategicHpCost = 18 }.ReplacementHpCredit != 18
            || new PotionRewardOutlook(0.9f, true, false) { Forecast = PotionRewardForecast.NoDrop }.ReplacementHpCredit != 0
            || new PotionRewardOutlook(0.9f, true, false) { Forecast = PotionRewardForecast.NoRewards }.ReplacementHpCredit != 0
            || new PotionRewardOutlook(1f, false, false) { Forecast = PotionRewardForecast.Drop, ForecastPotionId = "FIRE_POTION", ForecastPotionStrategicHpCost = 9 }.ReplacementHpCredit != 0)
            throw new InvalidOperationException("镜像掉落结果的额度计算错误。");
        // 根快照读到的是当前玩家保存的概率、真实房间类型与药水栏占用。
        Player player = combat.Players[0];
        RoomType? roomType = combat.Encounter?.RoomType;
        PotionRewardOutlook actual = root.PotionRewardOutlook;
        if (roomType is { } room && room.IsCombatRoom())
        {
            float expectedChance = PotionRewardOutlook.DropChanceFor(player.PlayerOdds.PotionReward.CurrentValue, room);
            bool expectedBeltFull = player.PotionSlots.Count > 0 && player.PotionSlots.All(potion => potion != null);
            if (actual.DropChance != expectedChance
                || actual.BeltFull != expectedBeltFull
                || actual.ProcureBlocked != player.Relics.OfType<Sozu>().Any()
                || actual.Forecast is PotionRewardForecast.Unknown or PotionRewardForecast.NoRewards
                || actual.Forecast == PotionRewardForecast.Drop != (actual.ForecastPotionId != null))
                throw new InvalidOperationException($"根快照的药水掉落前景 {actual} 与实况不符。");
        }
        else if (actual != PotionRewardOutlook.None)
        {
            throw new InvalidOperationException($"非战斗房间不应有药水掉落前景：{actual}。");
        }
    }

    /// <summary>
    /// 开战时镜像的掉落结论与药水身份，必须和原版奖励生成在同一条奖励 RNG 上给出的结果逐项一致。
    /// 这里直接让原版 <see cref="RewardsSet"/> 为当前房间生成并填充奖励（消耗真实奖励 RNG，只在测试实例里做）。
    /// </summary>
    private async Task AssertPotionRewardForecastAsync(CombatState combat, Player player)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        PotionRewardOutlook outlook = root.PotionRewardOutlook;
        if (outlook.Forecast is PotionRewardForecast.Unknown or PotionRewardForecast.NoRewards)
            throw new InvalidOperationException($"测试房间应能镜像掉落结论，实际 {outlook}。");
        if (player.RunState.CurrentRoom is not CombatRoom room)
            throw new InvalidOperationException("当前房间不是战斗房间，无法生成原版奖励。");
        int counterBefore = player.PlayerRng.Rewards._counter;
        RewardsSet rewards = new RewardsSet(player).WithRewardsFromRoom(room);
        await rewards.GenerateWithoutOffering();
        PotionReward? potionReward = rewards.Rewards.OfType<PotionReward>().FirstOrDefault();
        string? actualPotion = potionReward?.Potion?.Id.Entry;
        bool actualDrop = potionReward != null;
        bool expectedDrop = outlook.Forecast == PotionRewardForecast.Drop;
        if (actualDrop != expectedDrop || !string.Equals(actualPotion, outlook.ForecastPotionId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"药水掉落预测 {outlook.Forecast}/{outlook.ForecastPotionId ?? "-"} 与原版奖励 " +
                $"{(actualDrop ? "Drop" : "NoDrop")}/{actualPotion ?? "-"} 不符（room={room.RoomType} " +
                $"odds={player.PlayerOdds.PotionReward.CurrentValue:0.###} counter={counterBefore}→{player.PlayerRng.Rewards._counter}）。");
        Entry.Logger.Info(
            $"[CombatSolver/Test] POTION_REWARD_FORECAST_CHECK room={room.RoomType} forecast={outlook.Forecast} " +
            $"potion={outlook.ForecastPotionId ?? "-"} chance={outlook.DropChance:0.###} " +
            $"counter={counterBefore}->{player.PlayerRng.Rewards._counter} belt_full={outlook.BeltFull} credit={outlook.ReplacementHpCredit}");
        _completedChecks.Add($"PotionRewardForecast:{room.RoomType}:{outlook.Forecast}:{outlook.ForecastPotionId ?? "none"}");
    }
}
