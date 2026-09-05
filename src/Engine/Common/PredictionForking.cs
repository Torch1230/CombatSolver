using System.Buffers;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Combat;

namespace CombatSolver.Engine.Common;

internal interface ICombatPredictionForkableState : ICombatState
{
    ICombatState Fork(PredictionForkContext context);
}

internal interface ICombatPredictionHookListenerSource
{
    IReadOnlyList<MegaCrit.Sts2.Core.Models.AbstractModel> HookListeners { get; }

    IReadOnlyList<MegaCrit.Sts2.Core.Models.AbstractModel> RunHookListeners { get; }
}

/// <summary>
/// 由两段只读列表逐条同序拼接而成的监听表。hook 分发按段迭代，避免在最热循环里为每个元素
/// 多付一次接口调用和一次分支（视图自己的索引器每次都要判断落在前缀还是后缀）。
/// </summary>
internal interface ISegmentedModelList
{
    IReadOnlyList<MegaCrit.Sts2.Core.Models.AbstractModel> Prefix { get; }

    IReadOnlyList<MegaCrit.Sts2.Core.Models.AbstractModel> Suffix { get; }
}

internal interface ICombatPredictionRunSnapshot
{
    MegaCrit.Sts2.Core.Entities.Cards.CardMultiplayerConstraint CardMultiplayerConstraint { get; }

    CombatSolver.Engine.InCombat.Simulation.CombatPredictionRngSet CreatePredictionRngSet();
}

internal interface ICombatPredictionCardGenerationPoolSnapshot
{
    bool TryGetRootEligibleCards(
        MegaCrit.Sts2.Core.Entities.Players.Player player,
        MegaCrit.Sts2.Core.Models.CardPoolModel cardPool,
        MegaCrit.Sts2.Core.Entities.Cards.CardMultiplayerConstraint multiplayerConstraint,
        out IReadOnlyList<MegaCrit.Sts2.Core.Models.CardModel> cards);

    bool TryGetRootEligibleCharacterAttackCards(
        MegaCrit.Sts2.Core.Entities.Players.Player player,
        MegaCrit.Sts2.Core.Models.CardPoolModel cardPool,
        MegaCrit.Sts2.Core.Entities.Cards.CardMultiplayerConstraint multiplayerConstraint,
        out IReadOnlyList<MegaCrit.Sts2.Core.Models.CardModel> cards);
}

internal interface ICombatPredictionPlayerLimits
{
    int GetMaxHandSize(MegaCrit.Sts2.Core.Entities.Players.Player player);

    int GetPotionSlotCount(MegaCrit.Sts2.Core.Entities.Players.Player player);
}

internal interface ICombatPredictionPlayerCardRules
{
    bool AreCardsFree(MegaCrit.Sts2.Core.Entities.Players.Player player);
}

internal interface ICombatPredictionPetState
{
    MegaCrit.Sts2.Core.Entities.Creatures.Creature? GetOsty(
        MegaCrit.Sts2.Core.Entities.Players.Player player);
}

internal interface ICombatPredictionStateOwner
{
    void AttachPredictionState(
        CombatSolver.Engine.InCombat.Simulation.CombatPredictionState predictionState);
}

internal interface ICombatPredictionRootCaptureBoundary
{
    void AssertCanCaptureCreature(MegaCrit.Sts2.Core.Entities.Creatures.Creature creature);

    void AssertCanCapturePlayer(MegaCrit.Sts2.Core.Entities.Players.Player player);
}

internal interface ICombatPredictionRootMaterializable
{
    void MaterializeRoot(CombatSolver.Engine.InCombat.Simulation.CombatPredictionSimulator simulator);
}

internal interface ICombatPredictionCardEventSink
{
    void RecordCardExhausted(MegaCrit.Sts2.Core.Entities.Creatures.Creature actor);

    void RecordDamageReceived(
        MegaCrit.Sts2.Core.Entities.Creatures.Creature receiver,
        MegaCrit.Sts2.Core.Entities.Creatures.Creature? dealer,
        MegaCrit.Sts2.Core.Entities.Creatures.DamageResult result);

    void RecordCardDiscarded(MegaCrit.Sts2.Core.Entities.Creatures.Creature actor);

    void RecordCreatureAttacked(MegaCrit.Sts2.Core.Entities.Creatures.Creature actor);

    void RecordEnergySpent(MegaCrit.Sts2.Core.Entities.Players.Player player, int amount);

    void AfterEnergySpent(
        CombatSolver.Engine.InCombat.Simulation.CombatPredictionSimulator simulator,
        CombatSolver.Engine.Common.PredictedCard card,
        int amount);

    void AfterStarsSpent(
        CombatSolver.Engine.InCombat.Simulation.CombatPredictionSimulator simulator,
        CombatSolver.Engine.Common.PredictedCard card,
        int amount);

    void RecordStarsGained(MegaCrit.Sts2.Core.Entities.Players.Player player, int amount);

    void RecordCardDrawn(
        CombatSolver.Engine.Common.PredictedCard card,
        bool fromHandDraw);

    int GetStatusCardsDrawnThisTurn(MegaCrit.Sts2.Core.Entities.Players.Player player);

    void AfterCardEnteredCombat(
        CombatSolver.Engine.InCombat.Simulation.CombatPredictionSimulator simulator,
        CombatSolver.Engine.Common.PredictedCard card);

    void AfterCardRemovedFromCombat(CombatSolver.Engine.Common.PredictedCard card);

    void AfterHandEmptied(
        CombatSolver.Engine.InCombat.Simulation.CombatPredictionSimulator simulator,
        MegaCrit.Sts2.Core.Entities.Players.Player player);
}

internal interface ICombatPredictionCardExecutionSink
{
    IDisposable BeginCardExecutionScope();

    IDisposable BeginHistorySensitiveCardModifierScope(
        CombatSolver.Engine.Common.PredictedCard card);

    IDisposable BeginCardPowerApplication(CombatSolver.Engine.Common.PredictedCard card);

    void RecordCardPlayStarted(
        CombatSolver.Engine.Common.PredictedCard card,
        MegaCrit.Sts2.Core.Entities.Cards.CardPlay cardPlay);

    void ApplyCardPlayEffects(
        CombatSolver.Engine.InCombat.Simulation.CombatPredictionSimulator simulator,
        CombatSolver.Engine.Common.PredictedCard card,
        MegaCrit.Sts2.Core.Entities.Cards.CardPlay cardPlay,
        MegaCrit.Sts2.Core.Entities.Creatures.Creature? target,
        int ownerBlockBefore,
        decimal cardBlockGained,
        int historyEntryStart);

    void CompleteCardPlayEffects(
        CombatSolver.Engine.InCombat.Simulation.CombatPredictionSimulator simulator,
        CombatSolver.Engine.Common.PredictedCard card,
        int ownerBlockBefore,
        int historyEntryStart);

    void CompleteCardExecution(
        CombatSolver.Engine.InCombat.Simulation.CombatPredictionSimulator simulator);
}

internal interface ICombatPredictionEnemyDeathSink
{
    void ResolvePendingEnemyDeaths(
        CombatSolver.Engine.InCombat.Simulation.CombatPredictionSimulator simulator,
        ISet<uint> processedEnemyDeaths);
}

internal interface ICombatPredictionEffectSink
{
    void SummonOsty(
        CombatSolver.Engine.InCombat.Simulation.CombatPredictionSimulator simulator,
        MegaCrit.Sts2.Core.Entities.Players.Player player,
        int amount);

    void ApplyPower(
        Type powerType,
        MegaCrit.Sts2.Core.Entities.Creatures.Creature target,
        int amount,
        MegaCrit.Sts2.Core.Entities.Creatures.Creature? applier = null);

    void SetPowerAmount(MegaCrit.Sts2.Core.Models.PowerModel power, int amount);

    void SetPowerDynamicVar(
        CombatSolver.Engine.InCombat.Simulation.CombatPredictionSimulator simulator,
        MegaCrit.Sts2.Core.Models.PowerModel power,
        string key,
        int value);

    bool TryProcurePotion(
        MegaCrit.Sts2.Core.Entities.Players.Player player,
        MegaCrit.Sts2.Core.Models.PotionModel potion);

    void ConsumePotion(MegaCrit.Sts2.Core.Models.PotionModel potion);

    void BeforePotionUsed(
        CombatSolver.Engine.InCombat.Simulation.CombatPredictionSimulator simulator,
        MegaCrit.Sts2.Core.Models.PotionModel potion,
        MegaCrit.Sts2.Core.Entities.Creatures.Creature? target);

    void AfterPotionUsed(
        CombatSolver.Engine.InCombat.Simulation.CombatPredictionSimulator simulator,
        MegaCrit.Sts2.Core.Models.PotionModel potion,
        MegaCrit.Sts2.Core.Entities.Creatures.Creature? target);

    void LosePlayerGold(MegaCrit.Sts2.Core.Entities.Players.Player player, int amount);

    void ApplyPowerSkippingNextDurationTick(
        Type powerType,
        MegaCrit.Sts2.Core.Entities.Creatures.Creature target,
        int amount,
        MegaCrit.Sts2.Core.Entities.Creatures.Creature? applier = null);

    void ApplyTemporaryStrengthLoss(
        Type powerType,
        MegaCrit.Sts2.Core.Entities.Creatures.Creature target,
        int amount,
        MegaCrit.Sts2.Core.Entities.Creatures.Creature? applier = null);

    void ApplyTemporaryDexterity(
        Type powerType,
        MegaCrit.Sts2.Core.Entities.Creatures.Creature target,
        int amount,
        MegaCrit.Sts2.Core.Entities.Creatures.Creature? applier = null);

    bool GetRedSkullStrengthApplied(MegaCrit.Sts2.Core.Models.Relics.RedSkull relic);

    void SetRedSkullStrengthApplied(MegaCrit.Sts2.Core.Models.Relics.RedSkull relic, bool value);

    void DoomKill(
        CombatSolver.Engine.InCombat.Simulation.CombatPredictionSimulator simulator,
        IReadOnlyList<MegaCrit.Sts2.Core.Entities.Creatures.Creature> creatures);

    void StunNextMove(MegaCrit.Sts2.Core.Entities.Creatures.Creature creature);

    void ForceStunnedMove(
        MegaCrit.Sts2.Core.Entities.Creatures.Creature creature,
        string? nextMoveId = null);

}

internal interface ICombatPredictionRosterSink
{
    void RemoveCreatureFromPrediction(MegaCrit.Sts2.Core.Entities.Creatures.Creature creature);
}

internal interface ICombatPredictionChoiceSink
{
    bool ResolvePileChoice(
        CombatSolver.Engine.InCombat.Simulation.CombatPredictionSimulator simulator,
        string sourceId,
        MegaCrit.Sts2.Core.Entities.Players.Player player,
        MegaCrit.Sts2.Core.Entities.Cards.PileType sourcePile,
        int count);
}

internal interface ICombatPredictionPendingChoiceState
{
    bool HasPendingChoice { get; }
}

internal interface ICombatPredictionNestedChoiceSink
{
    bool ResolveNestedCardChoice(
        CombatSolver.Engine.InCombat.Simulation.CombatPredictionSimulator simulator,
        CombatSolver.Engine.Common.PredictedCard card,
        string sourceId);
}

internal interface ICombatPredictionManualCardChoiceSink
{
    bool ResolveManualCardChoice(
        CombatSolver.Engine.InCombat.Simulation.CombatPredictionSimulator simulator,
        CombatSolver.Engine.Common.PredictedCard card);
}

internal interface ICombatPredictionCreatureSemantics
{
    bool IsPrimaryEnemy(MegaCrit.Sts2.Core.Entities.Creatures.Creature creature);

    bool IsHittable(MegaCrit.Sts2.Core.Entities.Creatures.Creature creature);

    bool ShouldRemoveAfterDeath(MegaCrit.Sts2.Core.Entities.Creatures.Creature creature);
}

internal interface ICombatPredictionMonsterStateSink
{
    bool GetMonsterBool(MegaCrit.Sts2.Core.Entities.Creatures.Creature creature, string name);

    int GetAeonglassWitherUpgradeCount(MegaCrit.Sts2.Core.Entities.Creatures.Creature creature);

    void SetMonsterBool(MegaCrit.Sts2.Core.Entities.Creatures.Creature creature, string name, bool value);

    string GetPredictedMoveId(MegaCrit.Sts2.Core.Entities.Creatures.Creature creature);

    string GetNextMoveIdFromStateLog(
        MegaCrit.Sts2.Core.Entities.Creatures.Creature creature,
        MegaCrit.Sts2.Core.Random.Rng rng);

    void ForceMonsterMove(MegaCrit.Sts2.Core.Entities.Creatures.Creature creature, string moveId);
}

internal interface IPredictionStateForkable
{
    object Fork(PredictionForkContext context);
}

internal interface IPredictionForkBoundary
{
    void AssertForkable();
}

internal sealed class PredictionForkContext : IDisposable
{
    private const int IndexedLookupThreshold = 64;
    private const int HashLoadNumerator = 3;
    private const int HashLoadDenominator = 4;
    private const int MinimumEntryCapacity = 40;

    // 同一条搜索线程上相邻两次 Fork 登记的条目数几乎相同。用上一次的实际条目数预估容量，
    // 让第一次 Register 就落在最终大小的条目数组与身份哈希索引上：既省掉线性扫描阶段的
    // O(n²) 比较，也省掉跨过阈值后按 3/4 装载率反复重建索引的开销。命中与否只影响容量，
    // 查找结果只由引用身份决定，因此映射结果与原实现逐条相同。
    [ThreadStatic]
    private static int _entryCountHint;

    private object[] _sources;
    private object[] _forks;
    private int[]? _buckets;
    private int _bucketResizeThreshold;
    private int _count;

    public PredictionForkContext()
    {
        int capacity = Math.Max(MinimumEntryCapacity, _entryCountHint);
        _sources = ArrayPool<object>.Shared.Rent(capacity);
        _forks = ArrayPool<object>.Shared.Rent(capacity);
        if (capacity >= IndexedLookupThreshold)
            BuildHashIndex(BucketCountFor(capacity));
    }

    private static int BucketCountFor(int entryCapacity)
        => checked((int)((long)entryCapacity * HashLoadDenominator / HashLoadNumerator) + 1);

    public void Register<T>(T source, T fork)
        where T : class
    {
        if (ReferenceEquals(source, fork))
            return;
        int existingIndex = Find(source);
        if (existingIndex >= 0)
        {
            object existing = _forks[existingIndex];
            if (!ReferenceEquals(existing, fork))
                throw new InvalidOperationException($"Prediction object {source.GetType().FullName} was forked twice.");
            return;
        }
        if (_count == _sources.Length)
        {
            object[] sources = ArrayPool<object>.Shared.Rent(_count * 2);
            object[] forks = ArrayPool<object>.Shared.Rent(_count * 2);
            Array.Copy(_sources, sources, _count);
            Array.Copy(_forks, forks, _count);
            ArrayPool<object>.Shared.Return(_sources, clearArray: true);
            ArrayPool<object>.Shared.Return(_forks, clearArray: true);
            _sources = sources;
            _forks = forks;
        }
        EnsureHashCapacityForAdd();
        _sources[_count] = source;
        _forks[_count] = fork;
        _count++;
        if (_buckets is not null)
            InsertHashEntry(_count - 1);
        else if (_count >= IndexedLookupThreshold)
            BuildHashIndex(IndexedLookupThreshold * 2);
    }

    public T RemapOrSelf<T>(T value)
        where T : class
    {
        int index = Find(value);
        return index >= 0 ? (T)_forks[index] : value;
    }

    public bool TryRemap<T>(T value, out T? fork)
        where T : class
    {
        int index = Find(value);
        if (index >= 0)
        {
            fork = (T)_forks[index];
            return true;
        }
        fork = null;
        return false;
    }

    public T RequireRemap<T>(T value)
        where T : class
    {
        int index = Find(value);
        return index >= 0
            ? (T)_forks[index]
            : throw new InvalidOperationException(
                $"Prediction object {value.GetType().FullName} has no fork mapping.");
    }

    private int Find(object source)
    {
        if (_buckets is not null)
            return FindIndexed(source);
        for (int index = _count - 1; index >= 0; index--)
        {
            if (ReferenceEquals(_sources[index], source))
                return index;
        }
        return -1;
    }

    private int FindIndexed(object source)
    {
        int[] buckets = _buckets!;
        int bucket = GetBucket(source, buckets.Length);
        while (true)
        {
            int entry = buckets[bucket] - 1;
            if (entry < 0)
                return -1;
            if (ReferenceEquals(_sources[entry], source))
                return entry;
            bucket++;
            if (bucket == buckets.Length)
                bucket = 0;
        }
    }

    private void EnsureHashCapacityForAdd()
    {
        if (_buckets is null || _count + 1 <= _bucketResizeThreshold)
            return;
        BuildHashIndex(checked(_buckets.Length * 2));
    }

    private void BuildHashIndex(int minimumLength)
    {
        int[] buckets = ArrayPool<int>.Shared.Rent(minimumLength);
        Array.Clear(buckets);
        int[]? previousBuckets = _buckets;
        _buckets = buckets;
        _bucketResizeThreshold = checked(
            buckets.Length * HashLoadNumerator / HashLoadDenominator);
        for (int index = 0; index < _count; index++)
            InsertHashEntry(index);
        if (previousBuckets is not null)
            ArrayPool<int>.Shared.Return(previousBuckets);
    }

    private void InsertHashEntry(int entry)
    {
        int[] buckets = _buckets!;
        int bucket = GetBucket(_sources[entry], buckets.Length);
        while (buckets[bucket] != 0)
        {
            bucket++;
            if (bucket == buckets.Length)
                bucket = 0;
        }
        buckets[bucket] = entry + 1;
    }

    private static int GetBucket(object source, int bucketCount)
        => (int)((uint)RuntimeHelpers.GetHashCode(source) % (uint)bucketCount);

    public void Dispose()
    {
        _entryCountHint = _count;
        object[] sources = _sources;
        object[] forks = _forks;
        int[]? buckets = _buckets;
        _sources = [];
        _forks = [];
        _buckets = null;
        _bucketResizeThreshold = 0;
        _count = 0;
        ArrayPool<object>.Shared.Return(sources, clearArray: true);
        ArrayPool<object>.Shared.Return(forks, clearArray: true);
        if (buckets is not null)
            ArrayPool<int>.Shared.Return(buckets);
    }
}
