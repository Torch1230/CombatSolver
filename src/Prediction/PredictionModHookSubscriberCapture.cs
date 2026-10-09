using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Runs;
using CombatSolver.Engine.Common;
using STS2RitsuLib;

namespace CombatSolver;

internal sealed class PredictionModHookSubscriberCapture
{
    private const string LoadoutMaxHandSizeModifierTypeName =
        "Loadout.Services.TildeKey.LoadoutMaxHandSizeModifier";
    private const string LoadoutEveryCardFreeCombatHookTypeName =
        "Loadout.Services.TildeKey.LoadoutEveryCardFreeCombatHook";
    private const string LoadoutPowerGiverSummonHookTypeName =
        "Loadout.Services.PowerGiver.PowerGiverSummonHook";
    private static readonly HashSet<string> KnownPreRootSubscriberTypeNames =
    [
        LoadoutMaxHandSizeModifierTypeName,
        LoadoutEveryCardFreeCombatHookTypeName,
        "Loadout.Services.TildeKey.LoadoutKillAllMonstersCombatHook",
        "Loadout.Services.PowerGiver.PowerGiverCombatStartHook",
    ];

    public AbstractModel[] RunSubscribers { get; }
    public AbstractModel[] CombatSubscribers { get; }
    public IReadOnlyDictionary<Player, int> MaxHandSizes { get; }
    public IReadOnlySet<Player> EveryCardFreePlayers { get; }
    public bool HasBaseLibCardModifiers { get; }
    public bool HasInactiveLoadoutSummonPowers { get; }
    // Capture validated the source and settings of every subscriber. These
    // Loadout hooks only configure pre-root state, hand limits or free costs;
    // an empty summon-power configuration cannot add powers later in combat.
    internal bool HasOnlyNonHealingLoadoutSubscribers
        => HealingCallbackRejection is null && RunSubscribers.All(IsNonHealingLoadoutSubscriber)
            && CombatSubscribers.All(IsNonHealingLoadoutSubscriber);

    private bool IsNonHealingLoadoutSubscriber(AbstractModel subscriber)
    {
        Type type = subscriber.GetType();
        var source = AssemblyInfo.ModForType(type, out bool isBaseGame);
        return !isBaseGame && source?.manifest?.id == "Loadout"
            && type.FullName is { } name
            && (KnownPreRootSubscriberTypeNames.Contains(name)
                // This sealed hook only captures its snapshot in BeforeCombatStart.
                || name == "Loadout.Services.Morphing.BottledMonsterMorphCombatSnapshotHook"
                || name == LoadoutPowerGiverSummonHookTypeName && HasInactiveLoadoutSummonPowers);
    }
    public AdaptedOnPlaySnapshot? AdaptedOnPlay { get; private init; }

    private static readonly Guid AuditedLoadoutMvid = new("3f51fce1-7ec3-4116-b440-2c31eb754731");
    internal bool HasCertifiedNonHealingSubscribers { get; }
    internal string? HealingCallbackRejection { get; private init; }
    internal bool IsCertifiedNonHealingSubscriberSource(AbstractModel source)
        => HasCertifiedNonHealingSubscribers && IsAuditedNonHealingSubscriberType(source.GetType());
    private bool IsAuditedNonHealingSubscriberType(Type type)
        => type.Module.ModuleVersionId == AuditedLoadoutMvid
            && type.FullName is { } name
            && (name is "Loadout.Services.Morphing.BottledMonsterMorphCombatSnapshotHook"
                or "Loadout.Services.PowerGiver.PowerGiverCombatStartHook"
                or "Loadout.Services.TildeKey.LoadoutKillAllMonstersCombatHook"
                or "Loadout.Services.TildeKey.LoadoutEveryCardFreeCombatHook"
                || name == LoadoutPowerGiverSummonHookTypeName && HasInactiveLoadoutSummonPowers);
    public MirroredHookListenerFilter MirroredHookFilter { get; } = MirroredHookListenerFilter.Capture();

    private PredictionModHookSubscriberCapture(
        AbstractModel[] runSubscribers,
        AbstractModel[] combatSubscribers,
        IReadOnlyDictionary<Player, int> maxHandSizes,
        IReadOnlySet<Player> everyCardFreePlayers,
        bool hasBaseLibCardModifiers,
        bool hasInactiveLoadoutSummonPowers)
    {
        RunSubscribers = runSubscribers;
        CombatSubscribers = combatSubscribers;
        MaxHandSizes = maxHandSizes;
        EveryCardFreePlayers = everyCardFreePlayers;
        HasBaseLibCardModifiers = hasBaseLibCardModifiers;
        HasInactiveLoadoutSummonPowers = hasInactiveLoadoutSummonPowers;
        HasCertifiedNonHealingSubscribers = !hasBaseLibCardModifiers
            && runSubscribers.Concat(combatSubscribers).All(source =>
                IsAuditedNonHealingSubscriberType(source.GetType()));
    }

    public static PredictionModHookSubscriberCapture Capture(
        RunState runState,
        CombatState combat,
        IReadOnlySet<CardModel> floatingCards)
    {
        AbstractModel[] runSubscribers = ModHelper.IterateAllRunStateSubscribers(runState).ToArray();
        AbstractModel[] combatSubscribers = ModHelper.IterateAllCombatStateSubscribers(combat).ToArray();
        bool hasBaseLibCardModifiers = combatSubscribers.Any(PredictionModModelSupport.IsBaseLibCardModifier);
        PredictionModModelSupport.RegisterBaseLibCardModifierSources(combatSubscribers);

        foreach (AbstractModel subscriber in runSubscribers)
            ValidateSubscriber(subscriber, "run");
        foreach (AbstractModel subscriber in combatSubscribers)
            ValidateSubscriber(subscriber, "combat");
        AdaptedOnPlaySnapshot? onPlay = PredictionModPatchAudit.CaptureCardOnPlay(EnumerateAuditableCards(runState, combat));

        Dictionary<Player, int> maxHandSizes = [];
        foreach (Player player in combat.Players)
        {
            int maxHandSize = RitsuLibFramework.GetMaxHandSize(player);
            if (maxHandSize < 0)
                throw new InvalidOperationException($"RitsuLib returned max hand size {maxHandSize}.");
            maxHandSizes.Add(player, maxHandSize);
        }

        IReadOnlySet<Player> everyCardFreePlayers = CaptureEveryCardFreePlayers(
            combatSubscribers,
            combat.Players);

        return new PredictionModHookSubscriberCapture(
            runSubscribers,
            combatSubscribers,
            maxHandSizes,
            everyCardFreePlayers,
            hasBaseLibCardModifiers,
            combatSubscribers.Any(subscriber =>
                subscriber.GetType().FullName == LoadoutPowerGiverSummonHookTypeName))
            { AdaptedOnPlay = onPlay,
                HealingCallbackRejection = PredictionPlayerPotionCallbackAudit.Capture(combat.Players)
                    ?? PredictionHealthCallbackAudit.Capture(combat)
                    ?? PredictionCardCallbackAudit.Capture(
                        EnumerateAuditableCards(runState, combat).Concat(floatingCards)) };
    }

    public static string? CaptureLiveLoadoutSummonPowerState(CombatState combat)
    {
        AbstractModel? hook = ModHelper.IterateAllCombatStateSubscribers(combat)
            .SingleOrDefault(subscriber =>
                subscriber.GetType().FullName == LoadoutPowerGiverSummonHookTypeName);
        if (hook is null)
            return null;

        Type type = hook.GetType();
        var mod = AssemblyInfo.ModForType(type, out bool isBaseGame);
        var manifest = mod?.manifest;
        if (isBaseGame || manifest is null
            || !string.Equals(manifest.id, "Loadout", StringComparison.Ordinal))
            return "unknown_source";
        return ReadLoadoutMonsterPowerCounters(type).Count == 0 ? "empty" : "configured";
    }

    private static IReadOnlyDictionary<string, int> ReadLoadoutMonsterPowerCounters(Type hookType)
    {
        Assembly assembly = hookType.Assembly;
        Type scopeType = assembly.GetType("Loadout.Services.Targets.LoadoutTargetScope")
            ?? throw new PredictionUnsupportedException("Loadout PowerGiver target scope contract changed.");
        Type selectionType = assembly.GetType("Loadout.Services.Targets.LoadoutTargetSelection")
            ?? throw new PredictionUnsupportedException("Loadout PowerGiver target selection contract changed.");
        Type serviceType = assembly.GetType("Loadout.Services.PowerGiver.PowerGiverStateService")
            ?? throw new PredictionUnsupportedException("Loadout PowerGiver state service contract changed.");
        if (!scopeType.IsEnum || !Enum.IsDefined(scopeType, "AllMonsters"))
            throw new PredictionUnsupportedException("Loadout PowerGiver monster target contract changed.");
        ConstructorInfo constructor = selectionType.GetConstructor([scopeType, typeof(ulong?)])
            ?? throw new PredictionUnsupportedException("Loadout PowerGiver target selection contract changed.");
        MethodInfo snapshotMethod = serviceType.GetMethod(
            "GetCountersSnapshot", BindingFlags.Public | BindingFlags.Static,
            binder: null, types: [selectionType], modifiers: null)
            ?? throw new PredictionUnsupportedException("Loadout PowerGiver counter snapshot contract changed.");
        object target = constructor.Invoke([Enum.Parse(scopeType, "AllMonsters"), null]);
        return snapshotMethod.Invoke(null, [target]) as IReadOnlyDictionary<string, int>
            ?? throw new PredictionUnsupportedException("Loadout PowerGiver returned an unsupported counter snapshot.");
    }

    public void AppendCardAttachedListeners(
        IEnumerable<CardModel> cards,
        List<AbstractModel> listeners)
    {
        foreach (CardModel card in cards)
            PredictionModModelSupport.AppendCardAttachedListeners(card, listeners);
    }

    private static IReadOnlySet<Player> CaptureEveryCardFreePlayers(
        IReadOnlyList<AbstractModel> combatSubscribers,
        IReadOnlyList<Player> players)
    {
        AbstractModel? hook = combatSubscribers.SingleOrDefault(subscriber =>
            subscriber.GetType().FullName == LoadoutEveryCardFreeCombatHookTypeName);
        if (hook is null)
            return new HashSet<Player>();

        HashSet<Player> result = [];
        foreach (Player player in players)
        {
            CardModel probe = player.PlayerCombatState?.AllCards
                .FirstOrDefault(card => !card.IsCanonical)
                ?? throw new PredictionUnsupportedException(
                    $"Cannot capture Loadout every-card-free state for player {player.NetId} without a combat card.");
            const decimal originalCost = 1m;
            bool modified = hook.TryModifyEnergyCostInCombatLate(
                probe,
                originalCost,
                out decimal modifiedCost);
            if (modified)
            {
                if (modifiedCost != 0m)
                {
                    throw new PredictionUnsupportedException(
                        $"Loadout every-card-free hook returned unsupported cost {modifiedCost}.");
                }
                result.Add(player);
            }
            else if (modifiedCost != originalCost)
            {
                throw new PredictionUnsupportedException(
                    $"Loadout every-card-free hook changed an inactive cost to {modifiedCost}.");
            }
        }
        return result;
    }

    /// <summary>
    /// Initial cards plus a conservative native combat-generation superset.
    /// Unlock/player-count restrictions only narrow the native random pools;
    /// fixed status/curse sources also require audit independently of that filter.
    /// </summary>
    private static IEnumerable<CardModel> EnumerateAuditableCards(IRunState runState, CombatState combat)
    {
        foreach (Player player in combat.Players)
        {
            foreach (CardModel card in player.PlayerCombatState?.AllCards ?? [])
                yield return card;
        }
        foreach (Player player in runState.Players)
        {
            foreach (CardModel card in player.Deck.Cards)
                yield return card;
        }
        foreach (CardModel card in EnumerateAuditableNativeGenerationCards())
            yield return card;
    }

    internal static IEnumerable<CardModel> EnumerateAuditableNativeGenerationCards()
    {
        CardPoolModel[] pools = [ModelDb.CardPool<IroncladCardPool>(), ModelDb.CardPool<SilentCardPool>(),
            ModelDb.CardPool<RegentCardPool>(), ModelDb.CardPool<NecrobinderCardPool>(),
            ModelDb.CardPool<DefectCardPool>(), ModelDb.CardPool<ColorlessCardPool>()];
        foreach (CardPoolModel pool in pools)
            foreach (CardModel card in pool.AllCards)
                if (card.CanBeGeneratedInCombat && card.Rarity != CardRarity.Ancient && card.Rarity != CardRarity.Event)
                    yield return card;
        foreach (CardModel card in ModelDb.CardPool<StatusCardPool>().AllCards
            .Concat(ModelDb.CardPool<CurseCardPool>().AllCards))
            yield return card;
    }

    internal static void ValidateCardOnPlaySources(IRunState runState, CombatState combat)
        => PredictionModPatchAudit.ValidateCardOnPlay(EnumerateAuditableCards(runState, combat));

    private static void ValidateSubscriber(
        AbstractModel subscriber,
        string scope)
    {
        Type type = subscriber.GetType();
        var mod = AssemblyInfo.ModForType(type, out bool isBaseGame);
        var manifest = mod?.manifest;
        if (type.FullName == LoadoutPowerGiverSummonHookTypeName
            && !isBaseGame
            && manifest is not null
            && string.Equals(manifest.id, "Loadout", StringComparison.Ordinal))
        {
            if (ReadLoadoutMonsterPowerCounters(type).Count == 0)
                return;
            throw new IncompatibleGameplayModException(
                "Loadout", manifest.name ?? string.Empty,
                "PowerGiver summon powers are configured",
                scope);
        }
        if (PredictionModModelSupport.IsBaseLibCardModifier(subscriber)
            || KnownPreRootSubscriberTypeNames.Contains(type.FullName ?? string.Empty)
            || (!isBaseGame && mod?.manifest?.affectsGameplay is false))
        {
            return;
        }
        // 类型只覆写窗口外 hook 还不够：默认战斗回调也会分派实例的 ExecutionFinished。
        // 未知事件受众与战斗 hook 覆写（含继承来的）都继续原有拒绝路径。
        if (PredictionModHookSubscriberInertness.IsCombatInert(subscriber, out string overriddenHooks))
        {
            Entry.Logger.Info(
                $"[CombatSolver/Test] MOD_HOOK_SUBSCRIBER_COMBAT_INERT scope={scope} " +
                $"mod={mod?.manifest?.id ?? "-"} type={type.FullName} hooks={overriddenHooks}");
            return;
        }
        if (!isBaseGame
            && mod?.manifest?.id is { Length: > 0 } modId
            && !string.Equals(modId, Entry.ModId, StringComparison.OrdinalIgnoreCase))
        {
            throw new IncompatibleGameplayModException(
                modId,
                mod.manifest.name ?? string.Empty,
                $"ModHelper subscriber {type.FullName ?? type.Name}",
                scope);
        }
        throw new PredictionUnsupportedException(
            $"Unsupported gameplay ModHelper {scope} subscriber {type.FullName}.");
    }
}
