using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
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

    private PredictionModHookSubscriberCapture(
        AbstractModel[] runSubscribers,
        AbstractModel[] combatSubscribers,
        IReadOnlyDictionary<Player, int> maxHandSizes,
        IReadOnlySet<Player> everyCardFreePlayers,
        bool hasBaseLibCardModifiers)
    {
        RunSubscribers = runSubscribers;
        CombatSubscribers = combatSubscribers;
        MaxHandSizes = maxHandSizes;
        EveryCardFreePlayers = everyCardFreePlayers;
        HasBaseLibCardModifiers = hasBaseLibCardModifiers;
    }

    public static PredictionModHookSubscriberCapture Capture(
        RunState runState,
        CombatState combat)
    {
        AbstractModel[] runSubscribers = ModHelper.IterateAllRunStateSubscribers(runState).ToArray();
        AbstractModel[] combatSubscribers = ModHelper.IterateAllCombatStateSubscribers(combat).ToArray();
        bool hasBaseLibCardModifiers = combatSubscribers.Any(PredictionModModelSupport.IsBaseLibCardModifier);
        PredictionModModelSupport.RegisterBaseLibCardModifierSources(combatSubscribers);

        foreach (AbstractModel subscriber in runSubscribers)
            ValidateSubscriber(subscriber, "run");
        foreach (AbstractModel subscriber in combatSubscribers)
            ValidateSubscriber(subscriber, "combat");

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
            hasBaseLibCardModifiers);
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

    private static void ValidateSubscriber(
        AbstractModel subscriber,
        string scope)
    {
        Type type = subscriber.GetType();
        var mod = AssemblyInfo.ModForType(type, out bool isBaseGame);
        if (PredictionModModelSupport.IsBaseLibCardModifier(subscriber)
            || KnownPreRootSubscriberTypeNames.Contains(type.FullName ?? string.Empty)
            || (!isBaseGame && mod?.manifest?.affectsGameplay is false))
        {
            return;
        }
        // 只覆写了地图/进幕/事件/休息处/商店这类战斗外 hook 的订阅器不可能改变战斗模拟结果，
        // 不必把整个 Mod 判为不兼容。任何战斗 hook 覆写（含继承来的）仍走下面的拒绝路径。
        if (PredictionModHookSubscriberInertness.IsCombatInert(type, out string overriddenHooks))
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
                type.FullName ?? type.Name,
                scope);
        }
        throw new PredictionUnsupportedException(
            $"Unsupported gameplay ModHelper {scope} subscriber {type.FullName}.");
    }
}
