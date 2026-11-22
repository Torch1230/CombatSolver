using System.Reflection;
using System.Runtime.CompilerServices;
using CombatSolver.Api;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
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

        FieldInfo sealedField = typeof(AdaptedCardOnPlayMirrors).GetField(
                "_sealed",
                BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingFieldException(typeof(AdaptedCardOnPlayMirrors).FullName, "_sealed");
        bool wasSealedBefore = (bool)(sealedField.GetValue(null)
            ?? throw new InvalidOperationException("AdaptedCardOnPlayMirrors._sealed 为空。"));

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
            MethodInfo creatureDamageMethod = typeof(CreatureCmd)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(static m => string.Equals(m.Name, nameof(CreatureCmd.Damage), StringComparison.Ordinal)
                    && !m.ContainsGenericParameters);
            MethodInfo damageAttackMethod = typeof(DamageCmd)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(static m => string.Equals(m.Name, nameof(DamageCmd.Attack), StringComparison.Ordinal)
                    && !m.ContainsGenericParameters);
            MethodInfo modifyDamageMethod = typeof(Hook)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(static m => string.Equals(m.Name, nameof(Hook.ModifyDamage), StringComparison.Ordinal)
                    && !m.ContainsGenericParameters);
            MethodInfo afterCombatRoomLoadedMethod = AccessTools.DeclaredMethod(
                    typeof(CombatManager),
                    nameof(CombatManager.AfterCombatRoomLoaded))
                ?? throw new MissingMethodException(typeof(CombatManager).FullName, nameof(CombatManager.AfterCombatRoomLoaded));
            MethodInfo cardFromSerializableMethod = AccessTools.DeclaredMethod(
                    typeof(CardModel),
                    nameof(CardModel.FromSerializable))
                ?? throw new MissingMethodException(typeof(CardModel).FullName, nameof(CardModel.FromSerializable));
            MethodInfo personalHiveAfterDamageMethod = AccessTools.DeclaredMethod(
                    typeof(PersonalHivePower),
                    nameof(PersonalHivePower.AfterDamageReceived))
                ?? throw new MissingMethodException(typeof(PersonalHivePower).FullName, nameof(PersonalHivePower.AfterDamageReceived));
            foreach ((string label, MethodInfo targetMethod) in new (string, MethodInfo)[]
                     {
                         ("RunManager.EnterRoomInternal", enterRoomMethod),
                         ("CreatureCmd.Heal", healMethod),
                         ("CreatureCmd.Damage", creatureDamageMethod),
                         ("DamageCmd.Attack", damageAttackMethod),
                         ("Hook.ModifyDamage", modifyDamageMethod),
                         ("CombatManager.AfterCombatRoomLoaded", afterCombatRoomLoadedMethod),
                         ("CardModel.FromSerializable", cardFromSerializableMethod),
                         ("PersonalHivePower.AfterDamageReceived", personalHiveAfterDamageMethod),
                     })
            {
                harmony.Patch(
                    targetMethod,
                    prefix: new HarmonyMethod(typeof(ForeignCardPatch), nameof(ForeignCardPatch.Prefix)));
                PreCombatEligibilityResult utilityHost = PreCombatSts2SimEligibility.AssessMandatoryLiveHost(runState);
                PreCombatEligibilityResult utilityLive = PreCombatSts2SimEligibility.AssessLiveHostAndRun(runState);
                PreCombatBackendDisplaySnapshot utilityBadge = PreCombatForecastApi.CaptureBackendDisplaySnapshot();
                if (!utilityHost.IsEligible
                    || !utilityLive.IsEligible
                    || !utilityBadge.HostEligibleForInProcess
                    || utilityBadge.HostExclusionCode is not null)
                {
                    throw new InvalidOperationException(
                        $"按 BuiltIn 同口径应放行的 affects_gameplay=true 库/工具补丁 {label} 被错误拒绝：" +
                        $"host={utilityHost.FailureCode}:{utilityHost.Detail}, live={utilityLive.FailureCode}:{utilityLive.Detail}, " +
                        $"badge={utilityBadge.HostExclusionCode}:{utilityBadge.HostExclusionDetail}。");
                }
                harmony.UnpatchAll(harmony.Id);
            }
            AssemblyInfo.MockTypes.Remove(typeof(ForeignCardPatch));

            var gameplayAuditMod = new Mod
            {
                path = "test-gameplay-audit-probe",
                manifest = new ModManifest
                {
                    id = "GameplayAuditProbe",
                    name = "Gameplay Audit Probe",
                    affectsGameplay = true,
                },
            };
            AssemblyInfo.MockTypes[typeof(ForeignCardPatch)] = (gameplayAuditMod, false);
            MethodInfo attackIntentGetSingleDamageMethod = AccessTools.DeclaredMethod(
                    typeof(AttackIntent),
                    nameof(AttackIntent.GetSingleDamage))
                ?? throw new MissingMethodException(typeof(AttackIntent).FullName, nameof(AttackIntent.GetSingleDamage));
            MethodInfo defendOnPlayMethod = AdaptedCardOnPlayMirrors.ResolveOnPlay(typeof(DefendIronclad))
                ?? throw new MissingMethodException(typeof(DefendIronclad).FullName, "OnPlay");
            foreach ((string label, MethodInfo targetMethod) in new (string, MethodInfo)[]
                     {
                         ("DefendIronclad.OnPlay", defendOnPlayMethod),
                         ("AttackIntent.GetSingleDamage", attackIntentGetSingleDamageMethod),
                     })
            {
                harmony.Patch(
                    targetMethod,
                    prefix: new HarmonyMethod(typeof(ForeignCardPatch), nameof(ForeignCardPatch.Prefix)));
                PreCombatEligibilityResult rejectedHost = PreCombatSts2SimEligibility.AssessMandatoryLiveHost(runState);
                PreCombatEligibilityResult rejectedLive = PreCombatSts2SimEligibility.AssessLiveHostAndRun(runState);
                PreCombatBackendDisplaySnapshot badgeSnapshot = PreCombatForecastApi.CaptureBackendDisplaySnapshot();
                if (rejectedHost.IsEligible
                    || rejectedHost.FailureCode != "gameplay_patch"
                    || rejectedLive.IsEligible
                    || rejectedLive.FailureCode != "gameplay_patch"
                    || badgeSnapshot.HostExclusionCode != "gameplay_patch")
                {
                    throw new InvalidOperationException(
                        $"gameplay=true 补丁 {label} 未被战前审计拒绝：host={rejectedHost.FailureCode}, " +
                        $"live={rejectedLive.FailureCode}, badge={badgeSnapshot.HostExclusionCode}。");
                }
                harmony.UnpatchAll(harmony.Id);
            }
            AssemblyInfo.MockTypes.Remove(typeof(ForeignCardPatch));

            if (!wasSealedBefore && (bool)(sealedField.GetValue(null) ?? false))
            {
                throw new InvalidOperationException(
                    "战前资格审计或徽章快照提前封印了宿主 AdaptedCardOnPlayMirrors。");
            }

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
