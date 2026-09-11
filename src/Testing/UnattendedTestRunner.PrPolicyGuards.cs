using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Potions;
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
        _completedChecks.Add("PotionValueTiers:Thresholds:Free:Ambergris:Rescue:Forced:Root");
    }
}
