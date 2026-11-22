using System.Reflection;
using System.Runtime.CompilerServices;
using CombatSolver.Api;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static async Task VerifyPreCombatModEligibilityAuditAsync(
        RunState runState,
        SerializableRun baseSave,
        EncounterModel encounter,
        int targetFloor,
        int targetCol)
    {
        if (!PredictionModHookSubscriberInertness.IsPreCombatInert(typeof(MapOnlySubscriber), out _)
            || PredictionModHookSubscriberInertness.IsPreCombatInert(typeof(CombatStartOnlySubscriber), out _)
            || !PredictionModHookSubscriberInertness.IsCombatInert(typeof(CombatStartOnlySubscriber), out _))
        {
            throw new InvalidOperationException("IsPreCombatInert 与 IsCombatInert 对战外和开战前订阅器的区分不符合预期。");
        }

        AbstractModel mapOnlyInstance = (AbstractModel)RuntimeHelpers.GetUninitializedObject(typeof(MapOnlySubscriber));
        AbstractModel combatStartInstance = (AbstractModel)RuntimeHelpers.GetUninitializedObject(typeof(CombatStartOnlySubscriber));
        if (!PredictionModHookSubscriberInertness.IsPreCombatInert(mapOnlyInstance, out _)
            || PredictionModHookSubscriberInertness.IsPreCombatInert(combatStartInstance, out _))
        {
            throw new InvalidOperationException("IsPreCombatInert 实例级判定未能正确区分解锁前与战斗前订阅器。");
        }

        var previousMocks = AssemblyInfo.MockTypes;
        AssemblyInfo.MockTypes = previousMocks == null ? [] : new(previousMocks);
        var harmony = new Harmony("CombatSolver.Testing.Sts2SimModAudit." + Guid.NewGuid().ToString("N"));
        try
        {
            var visualHealMod = new Mod
            {
                path = "test-visual-heal-probe",
                manifest = new ModManifest
                {
                    id = "VisualHealProbe",
                    name = "Visual Heal Probe",
                    affectsGameplay = false,
                },
            };
            AssemblyInfo.MockTypes[typeof(ForeignCardPatch)] = (visualHealMod, false);
            MethodInfo healMethod = AccessTools.Method(
                    typeof(CreatureCmd),
                    nameof(CreatureCmd.Heal),
                    [typeof(Creature), typeof(decimal), typeof(bool)])
                ?? throw new MissingMethodException(typeof(CreatureCmd).FullName, nameof(CreatureCmd.Heal));
            harmony.Patch(
                healMethod,
                prefix: new HarmonyMethod(typeof(ForeignCardPatch), nameof(ForeignCardPatch.Prefix)));

            PreCombatEligibilityResult neutralHost = PreCombatSts2SimEligibility.AssessMandatoryLiveHost(runState);
            PreCombatEligibilityResult neutralLive = PreCombatSts2SimEligibility.AssessLiveHostAndRun(runState);
            if (!neutralHost.IsEligible || !neutralLive.IsEligible)
            {
                throw new InvalidOperationException(
                    $"宣称不影响玩法的辅助 Mod 补丁 CreatureCmd.Heal 被错误拒绝：host={neutralHost.FailureCode}:{neutralHost.Detail}, " +
                    $"live={neutralLive.FailureCode}:{neutralLive.Detail}。");
            }
            harmony.UnpatchAll(harmony.Id);
            AssemblyInfo.MockTypes.Remove(typeof(ForeignCardPatch));

            var utilityGameplayMod = new Mod
            {
                path = "test-utility-room-probe",
                manifest = new ModManifest
                {
                    id = "UtilityRoomProbe",
                    name = "Utility Room Probe",
                    affectsGameplay = true,
                },
            };
            AssemblyInfo.MockTypes[typeof(ForeignCardPatch)] = (utilityGameplayMod, false);
            MethodInfo enterRoomMethod = typeof(RunManager)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .First(static m => string.Equals(m.Name, "EnterRoomInternal", StringComparison.Ordinal));
            harmony.Patch(
                enterRoomMethod,
                prefix: new HarmonyMethod(typeof(ForeignCardPatch), nameof(ForeignCardPatch.Prefix)));

            PreCombatEligibilityResult utilityHost = PreCombatSts2SimEligibility.AssessMandatoryLiveHost(runState);
            PreCombatEligibilityResult utilityLive = PreCombatSts2SimEligibility.AssessLiveHostAndRun(runState);
            if (!utilityHost.IsEligible || !utilityLive.IsEligible)
            {
                throw new InvalidOperationException(
                    $"仅修改房间入口且无战斗订阅/AI/RNG/出牌补丁的 affects_gameplay=true 工具 Mod 被错误拒绝：" +
                    $"host={utilityHost.FailureCode}:{utilityHost.Detail}, live={utilityLive.FailureCode}:{utilityLive.Detail}。");
            }
            harmony.UnpatchAll(harmony.Id);
            AssemblyInfo.MockTypes.Remove(typeof(ForeignCardPatch));

            var monsterAiMod = new Mod
            {
                path = "test-monster-ai-probe",
                manifest = new ModManifest
                {
                    id = "MonsterAiGameplayProbe",
                    name = "Monster AI Probe",
                    affectsGameplay = true,
                },
            };
            AssemblyInfo.MockTypes[typeof(ForeignCardPatch)] = (monsterAiMod, false);
            MethodInfo mawlerAi = AccessTools.Method(typeof(Mawler), "GenerateMoveStateMachine")
                ?? throw new MissingMethodException(typeof(Mawler).FullName, "GenerateMoveStateMachine");
            harmony.Patch(
                mawlerAi,
                prefix: new HarmonyMethod(typeof(ForeignCardPatch), nameof(ForeignCardPatch.Prefix)));

            PreCombatEligibilityResult monsterAiRejected = PreCombatSts2SimEligibility.AssessMandatoryLiveHost(runState);
            if (monsterAiRejected.IsEligible
                || monsterAiRejected.FailureCode != "gameplay_patch"
                || string.IsNullOrWhiteSpace(monsterAiRejected.Detail))
            {
                throw new InvalidOperationException(
                    $"篡改怪物 AIGenerateMoveStateMachine 的补丁未被拒绝：eligible={monsterAiRejected.IsEligible} " +
                    $"code={monsterAiRejected.FailureCode} detail={monsterAiRejected.Detail}。");
            }

            await AssertPlanningUnsupportedAsync(
                runState,
                CloneSave(baseSave),
                encounter,
                targetFloor,
                targetCol,
                "gameplay_patch",
                new PreCombatSimulationOptions
                {
                    SearchBudgetMilliseconds = 3_000,
                    OverallTimeoutMilliseconds = 30_000,
                    MaxDegreeOfParallelism = 1,
                    SampleSeed = 0UL,
                });
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            AssemblyInfo.MockTypes = previousMocks;
        }
    }
}
