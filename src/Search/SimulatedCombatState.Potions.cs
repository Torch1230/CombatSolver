using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Relics;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal readonly record struct PredictedPotionUse(
    int Slot,
    string PotionId,
    int StrategicHpCost,
    bool Automatic);

internal sealed partial class SimulatedCombatState
{
    private ForkableDictionary<(Player Player, int Slot), PotionModel?>? _potionSlots;
    private ForkableList<PredictedPotionUse>? _potionUses;

    internal IReadOnlyList<PredictedPotionUse> PotionUses
        => _potionUses is { } uses ? uses : Array.Empty<PredictedPotionUse>();

    public PotionModel? GetPotionAtSlot(Player player, int slot)
    {
        if (slot < 0 || slot >= PotionSlotCount(player))
            return null;
        (Player, int) key = (player, slot);
        if (_potionSlots?.TryGetValue(key, out PotionModel? potion) == true)
            return potion;
        throw new InvalidOperationException($"Potion slot {slot} is outside the captured inventory.");
    }

    public bool IsPotionAvailable(Player player, int slot)
        => GetPotionAtSlot(player, slot) != null;

    public bool HasOpenPotionSlot(Player player)
    {
        for (int slot = 0; slot < PotionSlotCount(player); slot++)
        {
            if (GetPotionAtSlot(player, slot) == null)
                return true;
        }
        return false;
    }

    public void ConsumePotion(Player player, int slot)
        => ConsumePotion(player, slot, automatic: false);

    private void ConsumePotion(Player player, int slot, bool automatic)
    {
        PotionModel potion = GetPotionAtSlot(player, slot)
            ?? throw new InvalidOperationException($"药水槽位 {slot} 已不可用。");
        (_potionSlots ??= [])[(player, slot)] = null;
        bool renewablePotionShapedRock = potion is PotionShapedRock
            && RelicsOf(player).OfType<PetrifiedToad>().Any(static relic => !relic.IsMelted);
        (_potionUses ??= []).Add(new PredictedPotionUse(
            slot,
            potion.Id.Entry,
            PotionUsePolicy.StrategicHpCost(potion, renewablePotionShapedRock),
            automatic));
        InvalidateBaseHookListeners();
    }

    public void ConsumePotion(PotionModel potion)
    {
        Player player = potion.Owner;
        for (int slot = 0; slot < PotionSlotCount(player); slot++)
        {
            if (!ReferenceEquals(GetPotionAtSlot(player, slot), potion))
                continue;
            ConsumePotion(player, slot, automatic: true);
            return;
        }
        throw new InvalidOperationException($"预测药水槽中找不到实例 {potion.Id.Entry}。");
    }

    public void BeforePotionUsed(
        CombatPredictionSimulator simulator,
        PotionModel potion,
        MegaCrit.Sts2.Core.Entities.Creatures.Creature? target)
        => PowerLifecycleSupport.UpdateSurroundedForTarget(
            simulator,
            this,
            potion.Owner,
            target ?? (potion.IsValidTarget(potion.Owner.Creature) ? potion.Owner.Creature : null));

    public void AfterPotionUsed(
        CombatPredictionSimulator simulator,
        PotionModel potion,
        MegaCrit.Sts2.Core.Entities.Creatures.Creature? target)
    {
        TriggerRelicsAfterPotionUsed(simulator, potion);
        TriggerRelicsAfterHandEmptied(simulator, potion.Owner);
    }

    public bool TryProcurePotion(Player player, PotionModel canonical)
    {
        PotionModel potion = PredictionUtils.CloneModelForSimulation(canonical);
        potion.Owner = player;
        foreach (AbstractModel listener in GetEffectiveRunHookListeners())
        {
            if (!listener.ShouldProcurePotion(potion, player))
                return false;
        }
        for (int slot = 0; slot < PotionSlotCount(player); slot++)
        {
            if (GetPotionAtSlot(player, slot) != null)
                continue;
            (_potionSlots ??= [])[(player, slot)] = potion;
            InvalidateBaseHookListeners();
            TriggerRelicsAfterPotionProcured(player);
            return true;
        }
        return false;
    }

    private void AppendPotionFingerprint(ref StateFingerprintBuilder fingerprint)
    {
        fingerprint.Add('p');
        // 单人战斗是唯一支持的模式；只有多名玩家时才需要按 NetId 排序，避免每次快照都走 LINQ。
        if (Players.Count == 1)
        {
            AppendPlayerPotionFingerprint(ref fingerprint, Players[0]);
            return;
        }
        foreach (Player player in Players.OrderBy(player => player.NetId))
            AppendPlayerPotionFingerprint(ref fingerprint, player);
    }

    private void AppendPlayerPotionFingerprint(ref StateFingerprintBuilder fingerprint, Player player)
    {
        fingerprint.Add((long)player.NetId);
        int slotCount = PotionSlotCount(player);
        fingerprint.Add(slotCount);
        for (int slot = 0; slot < slotCount; slot++)
            fingerprint.Add(GetPotionAtSlot(player, slot)?.Id.Entry ?? "-");
    }
}
