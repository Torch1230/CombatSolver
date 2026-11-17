using System.Reflection;
using System.Runtime.CompilerServices;
using CombatSolver.Engine.Common;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

/// <summary>
/// Root-capture guard against third-party Harmony patches that replace gameplay behavior the engine mirrors.
/// </summary>
/// <remarks>
/// Mirrors read live model data, so third-party patches to canonical data (energy cost, dynamic vars, keywords,
/// rarity) are followed automatically. A replaced <see cref="CardModel.OnPlay"/>
/// is different in kind: <c>CardOnPlayInferrer</c> reads the original, unpatched IL by design, and the
/// bespoke mirrors are keyed on the vanilla card type. The engine therefore keeps executing the vanilla recipe it
/// was written against and silently produces a route for a card the game no longer plays that way, which the
/// project's "unknown semantics must fail explicitly" constraint forbids.
/// </remarks>
internal static class PredictionModPatchAudit
{
    internal readonly record struct ForeignPatch(string ModId, string ModName, string Description);

    private const string MonsterMachineMethodName = "GenerateMoveStateMachine";
    private static readonly object AdaptedMonsterMachineLock = new();
    private static readonly HashSet<(Type Monster, string ModId)> AdaptedMonsterMachines = [];

    /// <summary>
    /// 第三方适配层声明：<paramref name="modId"/> 对原版怪物 <paramref name="monsterType"/> 的
    /// <c>GenerateMoveStateMachine</c> 补丁已经适配。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 根捕获拒绝怪物行动状态机上的任何第三方玩法补丁（<see cref="ValidateMonsterModels" />），因为求解器无从知道
    /// 补丁换掉了什么。但出招表本身是求解器在模拟里直接读取的活状态机：换了招式顺序、条件或招式集合都会自动跟随；
    /// 会算错的是招式效果和写死的条件分支，那些要由适配层另行登记或补齐。适配层做完之后，用这一条声明
    /// 「这个 mod 对这个怪物出招表的补丁我负责」，审计就放行这一组合。
    /// </para>
    /// <para>
    /// 放行是逐组合的：只认登记的怪物类型（出招表的声明类型，可以是被多个怪物继承的抽象基类）和 mod id；
    /// 同一方法上混有其他 mod 的补丁，
    /// 其他 mod 照样被拒绝。攻击意图的构造函数、<c>GetSingleDamage</c> 等其他审计方法不受影响。
    /// 只接受原版怪物类型：第三方怪物另有整体门禁，这一条不能替代怪物 AI 与行动的完整合同。
    /// </para>
    /// </remarks>
    public static void RegisterAdaptedMonsterMachine(Type monsterType, string modId)
    {
        ArgumentNullException.ThrowIfNull(monsterType);
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        // 允许抽象基类：出招表可能声明在基类上由多个具体怪物继承（如 DecimillipedeSegment），
        // 审计拿到的方法的声明类型就是那个基类，按声明类型登记才对得上。
        if (!typeof(MonsterModel).IsAssignableFrom(monsterType))
            throw new ArgumentException($"{monsterType.FullName} is not a MonsterModel.", nameof(monsterType));
        // 适配层在 mod 初始化阶段登记，那时 AssemblyInfo.ModForType 还不能用，直接比程序集。
        if (monsterType.Assembly != typeof(MonsterModel).Assembly)
            throw new ArgumentException($"{monsterType.FullName} 不是原版怪物；第三方怪物没有这条放行。", nameof(monsterType));
        if (AccessTools.DeclaredMethod(monsterType, MonsterMachineMethodName) is null)
            throw new ArgumentException($"{monsterType.FullName} 没有声明 {MonsterMachineMethodName}。", nameof(monsterType));
        lock (AdaptedMonsterMachineLock)
        {
            if (!AdaptedMonsterMachines.Add((monsterType, modId.ToLowerInvariant())))
                throw new ArgumentException($"{monsterType.FullName} / {modId} 已经登记过。", nameof(modId));
        }
    }

    private static bool IsAdaptedMonsterMachine(MethodBase target, string modId)
    {
        if (target.Name != MonsterMachineMethodName || target.DeclaringType is not { } monsterType)
            return false;
        lock (AdaptedMonsterMachineLock)
            return AdaptedMonsterMachines.Contains((monsterType, modId.ToLowerInvariant()));
    }

    /// <summary>
    /// Throws when any card reachable from the captured root has a third-party patch on its mirrored OnPlay.
    /// </summary>
    /// <remarks>
    /// The root caller includes initial cards and the audited native generation
    /// superset. This does not certify arbitrary extension generation rules.
    /// </remarks>
    public static void ValidateCardOnPlay(IEnumerable<CardModel> cards)
        => CaptureCardOnPlay(cards);

    internal static AdaptedOnPlaySnapshot? CaptureCardOnPlay(IEnumerable<CardModel> cards)
    {
        bool adapted = AdaptedCardOnPlayMirrors.Seal();
        Dictionary<Type, AdaptedCardOnPlayMirrors.Registration?>? selections = adapted ? [] : null;
        HashSet<Type> checkedTypes = [];
        foreach (CardModel card in cards)
        {
            // Harmony patches can be installed or removed between root captures.
            Type type = card.GetType();
            if (!checkedTypes.Add(type)) continue;
            AdaptedCardOnPlayMirrors.Registration? selected =
                AuditCardOnPlay(type, adapted, out ForeignPatch? firstForeign);
            if (selected is null && firstForeign is { } unsupported)
                throw new IncompatibleGameplayModException(unsupported.ModId, unsupported.ModName,
                    unsupported.Description, "combat");
            selections?.Add(type, selected);
        }
        if (selections is null)
            return null;

        Dictionary<Type, string> deferredFailures = [];
        foreach (Type type in AdaptedCardOnPlayMirrors.RegisteredTypes())
        {
            if (!checkedTypes.Add(type)) continue;
            try
            {
                AdaptedCardOnPlayMirrors.Registration? selected =
                    AuditCardOnPlay(type, adapted: true, out ForeignPatch? firstForeign);
                if (selected is null && firstForeign is { } unsupported)
                    deferredFailures.Add(type, $"{unsupported.ModName} ({unsupported.ModId}) patches the OnPlay of "
                        + $"{type.FullName} without a matching adapter: {unsupported.Description}.");
                else
                    selections.Add(type, selected);
            }
            catch (PredictionUnsupportedException error)
            {
                // The type is not reachable from this root. Keep its exact rejection for first use.
                deferredFailures.Add(type, error.Message);
            }
        }
        HashSet<MethodInfo> patchedOnPlayTargets = [];
        string stamp = AdaptedCardOnPlayMirrors.CaptureLiveStamp(patchedOnPlayTargets)!;
        return new(selections, stamp, patchedOnPlayTargets, deferredFailures);
    }

    /// <summary>
    /// Audits one card type exactly the way root capture does, leaving the caller to decide what a foreign
    /// patch means at that point.
    /// </summary>
    internal static AdaptedCardOnPlayMirrors.Registration? AuditCardOnPlay(
        Type type, bool adapted, out ForeignPatch? firstForeign)
    {
        MethodInfo target = AdaptedCardOnPlayMirrors.ResolveOnPlay(type)
            ?? throw new PredictionUnsupportedException($"Missing OnPlay for {type.FullName}.");
        Patches? patches = Harmony.GetPatchInfo(target);
        firstForeign = null;
        if (patches is not null)
            foreach (var group in AdaptedCardOnPlayMirrors.Groups(patches))
                foreach (Patch patch in group.Patches)
                {
                    // Resolve every source even when the full combination is registered.
                    ForeignPatch? foreign = TryDescribeForeignPatch(patch, target);
                    firstForeign ??= foreign;
                }
        if (target.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType is { } stateMachine)
        {
            MethodInfo moveNext = AccessTools.Method(stateMachine, "MoveNext")
                ?? throw new PredictionUnsupportedException($"Async OnPlay state machine has no MoveNext: {type.FullName}.");
            if (Harmony.GetPatchInfo(moveNext) is { } asyncPatches)
                foreach (var group in AdaptedCardOnPlayMirrors.Groups(asyncPatches))
                    foreach (Patch patch in group.Patches)
                    {
                        if (TryDescribeForeignPatch(patch, moveNext) is not { } foreign) continue;
                        throw new IncompatibleGameplayModException(foreign.ModId, foreign.ModName, foreign.Description, "combat");
                    }
        }
        return adapted ? AdaptedCardOnPlayMirrors.Select(type, target, patches) : null;
    }

    internal static void ValidateMonsterModels(IEnumerable<MonsterModel> monsters)
    {
        foreach (MonsterModel monster in monsters)
        {
            _ = AssemblyInfo.ModForType(monster.GetType(), out bool isBaseGame);
            if (!isBaseGame)
                throw PredictionUnsupportedException.ForContent(
                    $"Monster AI and move effects require a prediction implementation: {monster.GetType().FullName}.", monster.GetType());
            RejectForeignPatches([AccessTools.Method(monster.GetType(), "GenerateMoveStateMachine")]);
        }
    }

    internal static void RejectForeignPatches(IEnumerable<MethodBase> methods)
    {
        foreach (MethodBase target in methods.Distinct())
            if (Harmony.GetPatchInfo(target) is { } patches)
                foreach (var group in AdaptedCardOnPlayMirrors.Groups(patches))
                    foreach (Patch patch in group.Patches)
                        if (TryDescribeForeignPatch(patch, target) is { } foreign
                            && !IsAdaptedMonsterMachine(target, foreign.ModId))
                            throw new IncompatibleGameplayModException(foreign.ModId, foreign.ModName, foreign.Description, "combat");
    }

    private static ForeignPatch? TryDescribeForeignPatch(Patch patch, MethodBase target)
    {
        Type? patchType = patch.PatchMethod.DeclaringType;
        if (patchType == null)
            throw new PredictionUnsupportedException(
                $"Unknown Harmony patch {patch.PatchMethod} (owner={patch.owner}) on {target}.");

        var mod = AssemblyInfo.ModForType(patchType, out bool isBaseGame);
        if (isBaseGame)
            return null;
        // Same policy as the ModHelper subscriber audit: mods that declare themselves gameplay-neutral are trusted.
        if (mod?.manifest?.affectsGameplay is false)
            return null;
        if (mod?.manifest?.id is not { Length: > 0 } modId)
            throw new PredictionUnsupportedException(
                $"Unknown Harmony patch {patchType.FullName}.{patch.PatchMethod.Name} " +
                $"(owner={patch.owner}) on mirrored {target.DeclaringType?.FullName}.{target.Name}.");
        if (string.Equals(modId, Entry.ModId, StringComparison.OrdinalIgnoreCase))
            return null;

        return new ForeignPatch(
            modId,
            mod.manifest.name ?? string.Empty,
            $"Harmony patch {patchType.FullName}.{patch.PatchMethod.Name} on mirrored "
            + $"{target.DeclaringType?.FullName}.{target.Name}");
    }
}
