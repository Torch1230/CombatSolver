using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Entities.Cards;

namespace CombatSolver.HotKernel;

internal static partial class HotCombatEngine
{
    /// <summary>
    /// Executes the compiled <see cref="HotCardEffectOp"/> bytecode program (or custom card handler)
    /// for the card currently in the Play pile at <c>s.PlayStart + playOffsetInPlay</c>.
    /// Zero managed heap allocations on the normal path.
    /// </summary>
    internal static void ExecuteCardEffects(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int playOffsetInPlay,
        int targetEnemySlot,
        int energySpent = 0)
    {
        int curPlayIdx = s.PlayStart + playOffsetInPlay;
        HotCard playedCard = s.Cards[curPlayIdx];
        HotCardDef cardDef = rules.Cards[playedCard.CardDefId];

        ReadOnlySpan<HotCardEffectOp> ops = rules.GetCardOps(playedCard.CardDefId);
        if (ops.IsEmpty)
        {
            HotCustomCardId customId = cardDef.CustomCardId != HotCustomCardId.None
                ? cardDef.CustomCardId
                : (cardDef.CustomHandlerId != 0 ? (HotCustomCardId)cardDef.CustomHandlerId : cardDef.CardId);
            if (customId != HotCustomCardId.None)
                ExecuteCoreCustomCard(ref s, ref cold, rules, scratch, customId, playOffsetInPlay, targetEnemySlot, energySpent);
            return;
        }

        ExecuteBytecodeInternal(ref s, ref cold, rules, scratch, ops, in playedCard, cardDef, playOffsetInPlay, targetEnemySlot, energySpent, potionPrimaryValue: 0);
    }

    /// <summary>
    /// Executes an arbitrary compiled <see cref="HotCardEffectOp"/> span against an optional card context.
    /// Exposed for <c>Sim/Content/*</c> custom cards and potions.
    /// </summary>
    internal static void ExecuteBytecode(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        ReadOnlySpan<HotCardEffectOp> ops,
        in HotCard card,
        HotCardDef? cardDef,
        int targetEnemySlot)
    {
        int playOffset = -1;
        if (card.CardUid != 0)
        {
            int idx = FindCardIndexByUid(in s, HotPileKind.Play, card.CardUid);
            if (idx >= 0)
                playOffset = idx - s.PlayStart;
        }
        ExecuteBytecodeInternal(ref s, ref cold, rules, scratch, ops, in card, cardDef, playOffset, targetEnemySlot, energySpent: 0, potionPrimaryValue: 0);
    }

    /// <summary>
    /// Executes compiled bytecode for a standard potion definition using its <see cref="HotPotionDef.PrimaryValue"/>.
    /// </summary>
    internal static void ExecutePotionBytecode(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        ushort potionDefId,
        int targetEnemySlot)
    {
        if (potionDefId == 0 || potionDefId >= rules.Potions.Length)
            return;
        ReadOnlySpan<HotCardEffectOp> ops = rules.GetPotionOps(potionDefId);
        if (ops.IsEmpty)
            return;
        HotCard dummyCard = default;
        ExecuteBytecodeInternal(ref s, ref cold, rules, scratch, ops, in dummyCard, cardDef: null, playOffsetInPlay: -1, targetEnemySlot, energySpent: 0, potionPrimaryValue: rules.Potions[potionDefId].PrimaryValue);
    }

    private static void ExecuteBytecodeInternal(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        ReadOnlySpan<HotCardEffectOp> ops,
        in HotCard playedCard,
        HotCardDef? cardDef,
        int playOffsetInPlay,
        int targetEnemySlot,
        int energySpent,
        int potionPrimaryValue)
    {
        int cardIdx = playOffsetInPlay >= 0 ? s.PlayStart + playOffsetInPlay : -1;
        ushort cardDefId = cardDef != null ? cardDef.DefId : (ushort)0;
        bool isCardSource = cardDef != null;

        for (int i = 0; i < ops.Length; i++)
        {
            if ((s.Player.Flags & HotCreatureFlags.Alive) == 0)
                break;

            HotCardEffectOp op = ops[i];
            HotCardOpFlags flags = (HotCardOpFlags)op.Flags;
            int liveCardIdx = playOffsetInPlay >= 0 && playOffsetInPlay < s.PlayCount ? s.PlayStart + playOffsetInPlay : cardIdx;

            switch (op.Op)
            {
                case HotCardOpKind.Nop:
                    break;

                case HotCardOpKind.Attack:
                {
                    if (IsOverOrEnding(in s) || !IsValidEnemyTarget(in s, targetEnemySlot))
                        break;

                    int dmg = ResolveOpAmount(op, in playedCard, cardDef, potionPrimaryValue);
                    int hits = cardDef != null && (flags & HotCardOpFlags.UseCardHits) != 0
                        ? cardDef.GetHits(playedCard.UpgradeLevel)
                        : 1;
                    bool powered = (flags & HotCardOpFlags.Unpowered) == 0;
                    ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, liveCardIdx, targetEnemySlot, dmg, hits, powered);
                    break;
                }

                case HotCardOpKind.AttackAll:
                {
                    if (IsOverOrEnding(in s))
                        break;

                    int dmg = ResolveOpAmount(op, in playedCard, cardDef, potionPrimaryValue);
                    int hits = cardDef != null && (flags & HotCardOpFlags.UseCardHits) != 0
                        ? cardDef.GetHits(playedCard.UpgradeLevel)
                        : 1;
                    bool powered = (flags & HotCardOpFlags.Unpowered) == 0;
                    ExecuteAllEnemiesAttack(ref s, ref cold, rules, scratch, liveCardIdx, dmg, hits, powered);
                    break;
                }

                case HotCardOpKind.AttackRandom:
                {
                    if (IsOverOrEnding(in s))
                        break;

                    int dmg = ResolveOpAmount(op, in playedCard, cardDef, potionPrimaryValue);
                    int hits = cardDef != null && (flags & HotCardOpFlags.UseCardHits) != 0
                        ? cardDef.GetHits(playedCard.UpgradeLevel)
                        : 1;
                    bool powered = (flags & HotCardOpFlags.Unpowered) == 0;
                    ExecuteRandomEnemiesAttack(ref s, ref cold, rules, scratch, liveCardIdx, dmg, hits, powered);
                    break;
                }

                case HotCardOpKind.AttackOsty:
                {
                    if (IsOverOrEnding(in s)
                        || (s.CombatFlags & HotCombatFlags.HasOsty) == 0
                        || (s.Osty.Flags & HotCreatureFlags.Alive) == 0
                        || !IsValidEnemyTarget(in s, targetEnemySlot))
                    {
                        break;
                    }

                    int dmg = ResolveOpAmount(op, in playedCard, cardDef, potionPrimaryValue);
                    int hits = cardDef != null && (flags & HotCardOpFlags.UseCardHits) != 0
                        ? cardDef.GetHits(playedCard.UpgradeLevel)
                        : 1;
                    bool powered = (flags & HotCardOpFlags.Unpowered) == 0;
                    ExecuteOstyAttack(ref s, ref cold, rules, scratch, liveCardIdx, targetEnemySlot, dmg, hits, powered);
                    break;
                }

                case HotCardOpKind.GainBlock:
                {
                    int blk = ResolveOpAmount(op, in playedCard, cardDef, potionPrimaryValue);
                    bool isCardBlock = (flags & HotCardOpFlags.Unpowered) == 0;
                    GainBlockInternal(ref s, ref cold, rules, scratch, targetSlot: 0, baseAmount: blk, isCardBlock: isCardBlock, cardIdx: liveCardIdx, cardDefId: cardDefId);
                    break;
                }

                case HotCardOpKind.Draw:
                {
                    int drawCount = ResolveOpAmount(op, in playedCard, cardDef, potionPrimaryValue);
                    DrawCards(ref s, ref cold, rules, scratch, drawCount, fromHandDraw: false);
                    break;
                }

                case HotCardOpKind.ApplyPowerSelf:
                case HotCardOpKind.ApplyTempStat:
                {
                    ushort powerDefId = (ushort)op.Arg0;
                    int amt = ResolvePowerOpAmount(op, in playedCard, cardDef, potionPrimaryValue);
                    int applied = ApplyPower(ref s, ref cold, rules, scratch, ownerSlot: 0, applierSlot: 0, powerDefId: powerDefId, amount: amt, isCardSource: isCardSource);
                    if (cardDef != null && (cardDef.CardId == HotCustomCardId.Inferno || cardDef.CardId == HotCustomCardId.Ennui))
                    {
                        int pIdx = FindPowerIndex(in s, 0, powerDefId);
                        if (pIdx >= 0)
                            s.Powers[pIdx].Aux0++;
                    }
                    else if (applied != 0 && cardDef != null && cardDef.CardId == HotCustomCardId.ToricToughness)
                    {
                        decimal blockDec = t_lastGainBlockDecimal;
                        int blockFloor = (int)blockDec;
                        int blockHundredths = (int)Math.Round(blockDec * 100m, MidpointRounding.AwayFromZero);
                        for (int pIdx = s.PowerCount - 1; pIdx >= 0; pIdx--)
                        {
                            if (s.Powers[pIdx].OwnerSlot == 0 && s.Powers[pIdx].PowerDefId == powerDefId)
                            {
                                s.Powers[pIdx].Aux0 = blockFloor;
                                s.Powers[pIdx].Aux1 = blockHundredths;
                                break;
                            }
                        }
                    }
                    break;
                }

                case HotCardOpKind.ApplyPowerTarget:
                {
                    if (!IsValidEnemyTarget(in s, targetEnemySlot))
                        break;
                    ushort powerDefId = (ushort)op.Arg0;
                    int amt = ResolvePowerOpAmount(op, in playedCard, cardDef, potionPrimaryValue);
                    ApplyPower(ref s, ref cold, rules, scratch, ownerSlot: (byte)(3 + targetEnemySlot), applierSlot: 0, powerDefId: powerDefId, amount: amt, isCardSource: isCardSource);
                    break;
                }

                case HotCardOpKind.ApplyPowerAllEnemies:
                {
                    ushort powerDefId = (ushort)op.Arg0;
                    int amt = ResolvePowerOpAmount(op, in playedCard, cardDef, potionPrimaryValue);
                    Span<byte> targets = stackalloc byte[HotLimits.MaxEnemies];
                    int targetCount = SnapshotLivingEnemies(in s, targets);
                    for (int t = 0; t < targetCount; t++)
                    {
                        byte enemySlot = targets[t];
                        if ((s.Enemies[enemySlot].Vital.Flags & HotCreatureFlags.Alive) == 0)
                            continue;
                        ApplyPower(ref s, ref cold, rules, scratch, ownerSlot: (byte)(3 + enemySlot), applierSlot: 0, powerDefId: powerDefId, amount: amt, isCardSource: isCardSource);
                        if (scratch.NeedsChoice)
                            return;
                    }
                    break;
                }

                case HotCardOpKind.GainEnergy:
                {
                    int amt = ResolveOpAmount(op, in playedCard, cardDef, potionPrimaryValue);
                    GainEnergy(ref s, rules, amt);
                    break;
                }

                case HotCardOpKind.GainStars:
                {
                    int amt = ResolveOpAmount(op, in playedCard, cardDef, potionPrimaryValue);
                    GainStars(ref s, ref cold, rules, scratch, amt);
                    break;
                }

                case HotCardOpKind.LoseHp:
                {
                    int amt = ResolveOpAmount(op, in playedCard, cardDef, potionPrimaryValue);
                    if (amt > 0)
                    {
                        DealDamageInternal(
                            ref s,
                            ref cold,
                            rules,
                            scratch,
                            dealerSlot: 0,
                            targetSlot: 0,
                            baseDamage: amt,
                            isPoweredAttack: false,
                            isUnblockable: true,
                            cardIdx: liveCardIdx,
                            cardDefId: cardDefId);
                    }
                    break;
                }

                case HotCardOpKind.Heal:
                {
                    int amt = ResolveOpAmount(op, in playedCard, cardDef, potionPrimaryValue);
                    HealCreature(ref s, ref cold, rules, scratch, targetSlot: 0, amount: amt);
                    break;
                }

                case HotCardOpKind.Forge:
                {
                    int amt = ResolveOpAmount(op, in playedCard, cardDef, potionPrimaryValue);
                    Forge(ref s, ref cold, rules, scratch, amt);
                    break;
                }

                case HotCardOpKind.ChannelOrb:
                {
                    HotOrbId orbId = (HotOrbId)op.Arg0;
                    int count = (flags & HotCardOpFlags.FixedOne) != 0
                        ? 1
                        : Math.Max(1, ResolvePowerOpAmount(op, in playedCard, cardDef, potionPrimaryValue));
                    for (int k = 0; k < count && !scratch.NeedsChoice; k++)
                        ChannelOrb(ref s, ref cold, rules, scratch, orbId);
                    break;
                }

                case HotCardOpKind.EvokeOrb:
                {
                    int count = ResolveOpAmount(op, in playedCard, cardDef, potionPrimaryValue);
                    EvokeOrb(ref s, ref cold, rules, scratch, orbIdx: 0, repeat: Math.Max(1, count), dequeue: true);
                    break;
                }

                case HotCardOpKind.SummonOsty:
                {
                    int amt = ResolveOpAmount(op, in playedCard, cardDef, potionPrimaryValue);
                    SummonOsty(ref s, ref cold, rules, scratch, amt);
                    break;
                }

                case HotCardOpKind.AddFixedCard:
                {
                    if ((flags & HotCardOpFlags.AddCloneToDiscard) != 0 && liveCardIdx >= 0)
                    {
                        ClonePlayedCardToDiscard(ref s, ref cold, rules, scratch, liveCardIdx);
                    }
                    else
                    {
                        ushort generatedDefId = (ushort)op.Arg0;
                        AddGeneratedFixedCard(ref s, ref cold, rules, scratch, generatedDefId, HotPileKind.Discard, dstOffset: -1, upgraded: false, createdByPlayer: true);
                    }
                    break;
                }

                case HotCardOpKind.ShootArrow:
                {
                    if (cardDef != null && liveCardIdx >= 0)
                        HotFoolTarot.TryExecuteCustomCard(ref s, ref cold, rules, scratch, cardDef.DefId, ref s.Cards[liveCardIdx], targetEnemySlot, out _);
                    break;
                }

                case HotCardOpKind.DiscardHand:
                {
                    while (s.HandCount > 0 && !IsOverOrEnding(in s) && !scratch.NeedsChoice)
                        DiscardCard(ref s, ref cold, rules, scratch, 0);
                    break;
                }

                case HotCardOpKind.Custom:
                {
                    HotCustomCardId customId = op.Arg0 > 0
                        ? (HotCustomCardId)op.Arg0
                        : (cardDef != null ? cardDef.CardId : HotCustomCardId.None);
                    if (customId != HotCustomCardId.None && playOffsetInPlay >= 0)
                        ExecuteCoreCustomCard(ref s, ref cold, rules, scratch, customId, playOffsetInPlay, targetEnemySlot, energySpent);
                    break;
                }
            }

            if (scratch.NeedsChoice)
                return;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ApplyModifyAttackHitCount(HotRules rules, ushort cardDefId, int hits)
    {
        // Thumbscrew relic: +1 hit on multi-hit Attack cards (Thumbscrew.cs:11-26)
        if (hits > 1
            && cardDefId != 0
            && cardDefId < rules.Cards.Length
            && rules.Cards[cardDefId].Type == CardType.Attack
            && rules.HasRelic(HotRelicId.Thumbscrew))
        {
            return hits + 1;
        }
        return hits;
    }

    internal static void ExecuteSingleTargetAttack(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int cardIdx,
        int targetEnemySlot,
        int baseDamage,
        int hits,
        bool powered)
    {
        t_lastAttackUnblockedDamage = 0;
        t_lastAttackTotalPlusOverkill = 0;
        if (IsOverOrEnding(in s) || (s.Player.Flags & HotCreatureFlags.Alive) == 0 || !IsValidEnemyTarget(in s, targetEnemySlot))
            return;
        if (hits < 0)
            hits = 0;

        ushort cardDefId = cardIdx >= 0 && cardIdx < s.TotalCardCount ? s.Cards[cardIdx].CardDefId : (ushort)0;
        hits = ApplyModifyAttackHitCount(rules, cardDefId, hits);
        byte targetCreatureSlot = (byte)(3 + targetEnemySlot);

        BeginAttackHooks(ref s, ref cold, rules, scratch, dealerSlot: 0, cardIdx, cardDefId, powered);
        scratch.AttackActive = true;
        int totalUnblocked = 0;
        int totalPlusOverkill = 0;
        for (int h = 0; h < hits; h++)
        {
            if ((s.Player.Flags & HotCreatureFlags.Alive) == 0
                || (s.Enemies[targetEnemySlot].Vital.Flags & HotCreatureFlags.Alive) == 0
                || scratch.NeedsChoice)
            {
                break;
            }
            totalUnblocked += DealDamageInternal(ref s, ref cold, rules, scratch, dealerSlot: 0, targetSlot: targetCreatureSlot, baseDamage: baseDamage, isPoweredAttack: powered, isUnblockable: false, cardIdx: cardIdx, cardDefId: cardDefId);
            totalPlusOverkill += t_lastHitTotalPlusOverkill;
            if (IsOverOrEnding(in s))
                break;
        }
        scratch.AttackActive = false;
        t_lastAttackUnblockedDamage = totalUnblocked;
        t_lastAttackTotalPlusOverkill = totalPlusOverkill;
        EndAttackHooks(ref s, ref cold, rules, scratch, dealerSlot: 0, cardIdx, cardDefId, powered);
        t_lastAttackUnblockedDamage = totalUnblocked;
        t_lastAttackTotalPlusOverkill = totalPlusOverkill;
    }

    internal static void ExecuteAllEnemiesAttack(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int cardIdx,
        int baseDamage,
        int hits,
        bool powered)
    {
        if (IsOverOrEnding(in s) || (s.Player.Flags & HotCreatureFlags.Alive) == 0)
            return;
        if (hits < 0)
            hits = 0;

        ushort cardDefId = cardIdx >= 0 && cardIdx < s.TotalCardCount ? s.Cards[cardIdx].CardDefId : (ushort)0;
        hits = ApplyModifyAttackHitCount(rules, cardDefId, hits);
        Span<byte> targets = stackalloc byte[HotLimits.MaxEnemies];

        BeginAttackHooks(ref s, ref cold, rules, scratch, dealerSlot: 0, cardIdx, cardDefId, powered);
        scratch.AttackActive = true;
        for (int h = 0; h < hits; h++)
        {
            if ((s.Player.Flags & HotCreatureFlags.Alive) == 0 || IsOverOrEnding(in s) || scratch.NeedsChoice)
                break;

            int targetCount = SnapshotLivingEnemies(in s, targets);
            if (targetCount == 0)
                break;

            for (int t = 0; t < targetCount; t++)
            {
                if ((s.Player.Flags & HotCreatureFlags.Alive) == 0 || scratch.NeedsChoice)
                    break;
                byte enemySlot = targets[t];
                if ((s.Enemies[enemySlot].Vital.Flags & HotCreatureFlags.Alive) == 0)
                    continue;
                DealDamageInternal(ref s, ref cold, rules, scratch, dealerSlot: 0, targetSlot: (byte)(3 + enemySlot), baseDamage: baseDamage, isPoweredAttack: powered, isUnblockable: false, cardIdx: cardIdx, cardDefId: cardDefId, deferKill: true);
            }
            for (int t = 0; t < targetCount; t++)
            {
                if (scratch.NeedsChoice)
                    break;
                byte enemySlot = targets[t];
                if ((s.Enemies[enemySlot].Vital.Flags & HotCreatureFlags.Alive) != 0
                    && s.Enemies[enemySlot].Vital.CurrentHp <= 0)
                {
                    KillCreature(ref s, ref cold, rules, scratch, (byte)(3 + enemySlot), force: false);
                }
            }
        }
        scratch.AttackActive = false;
        EndAttackHooks(ref s, ref cold, rules, scratch, dealerSlot: 0, cardIdx, cardDefId, powered);
    }

    internal static void ExecuteRandomEnemiesAttack(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int cardIdx,
        int baseDamage,
        int hits,
        bool powered)
    {
        if (IsOverOrEnding(in s) || (s.Player.Flags & HotCreatureFlags.Alive) == 0)
            return;
        if (hits < 0)
            hits = 0;

        ushort cardDefId = cardIdx >= 0 && cardIdx < s.TotalCardCount ? s.Cards[cardIdx].CardDefId : (ushort)0;
        hits = ApplyModifyAttackHitCount(rules, cardDefId, hits);

        BeginAttackHooks(ref s, ref cold, rules, scratch, dealerSlot: 0, cardIdx, cardDefId, powered);
        scratch.AttackActive = true;
        for (int h = 0; h < hits; h++)
        {
            if ((s.Player.Flags & HotCreatureFlags.Alive) == 0 || IsOverOrEnding(in s) || scratch.NeedsChoice)
                break;

            int enemySlot = PickRandomLivingEnemySlot(ref s);
            if (enemySlot < 0)
                break;

            DealDamageInternal(ref s, ref cold, rules, scratch, dealerSlot: 0, targetSlot: (byte)(3 + enemySlot), baseDamage: baseDamage, isPoweredAttack: powered, isUnblockable: false, cardIdx: cardIdx, cardDefId: cardDefId);
        }
        scratch.AttackActive = false;
        EndAttackHooks(ref s, ref cold, rules, scratch, dealerSlot: 0, cardIdx, cardDefId, powered);
    }

    internal static void ExecuteOstyAttack(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int cardIdx,
        int targetEnemySlot,
        int baseDamage,
        int hits,
        bool powered)
    {
        if (IsOverOrEnding(in s)
            || (s.CombatFlags & HotCombatFlags.HasOsty) == 0
            || (s.Osty.Flags & HotCreatureFlags.Alive) == 0
            || !IsValidEnemyTarget(in s, targetEnemySlot))
        {
            return;
        }
        if (hits < 0)
            hits = 0;

        ushort cardDefId = cardIdx >= 0 && cardIdx < s.TotalCardCount ? s.Cards[cardIdx].CardDefId : (ushort)0;
        hits = ApplyModifyAttackHitCount(rules, cardDefId, hits);
        byte targetCreatureSlot = (byte)(3 + targetEnemySlot);

        BeginAttackHooks(ref s, ref cold, rules, scratch, dealerSlot: 1, cardIdx, cardDefId, powered);
        scratch.AttackActive = true;
        for (int h = 0; h < hits; h++)
        {
            if ((s.Osty.Flags & HotCreatureFlags.Alive) == 0
                || (s.Enemies[targetEnemySlot].Vital.Flags & HotCreatureFlags.Alive) == 0
                || scratch.NeedsChoice)
            {
                break;
            }
            DealDamageInternal(ref s, ref cold, rules, scratch, dealerSlot: 1, targetSlot: targetCreatureSlot, baseDamage: baseDamage, isPoweredAttack: powered, isUnblockable: false, cardIdx: cardIdx, cardDefId: cardDefId);
            if (IsOverOrEnding(in s))
                break;
        }
        s.Counters[(int)HotCounterSlot.OstyAttacksThisTurn]++;
        scratch.AttackActive = false;
        EndAttackHooks(ref s, ref cold, rules, scratch, dealerSlot: 1, cardIdx, cardDefId, powered);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void BeginAttackHooks(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte dealerSlot,
        int cardIdx,
        ushort cardDefId,
        bool powered)
    {
        scratch.AttackUnblockedEnemyMask = 0;
        if ((s.ActiveHookMask & (ulong)HotHookMask.BeforeAttack) == 0)
            return;
        HotHookArgs args = new()
        {
            ActorSlot = dealerSlot,
            CardIdx = cardIdx,
            CardDefId = cardDefId,
            IsPoweredAttack = powered,
        };
        DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.BeforeAttack, ref args);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EndAttackHooks(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte dealerSlot,
        int cardIdx,
        ushort cardDefId,
        bool powered)
    {
        if ((s.ActiveHookMask & (ulong)HotHookMask.AfterAttack) == 0)
            return;
        HotHookArgs args = new()
        {
            ActorSlot = dealerSlot,
            CardIdx = cardIdx,
            CardDefId = cardDefId,
            IsPoweredAttack = powered,
        };
        DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterAttack, ref args);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ResolveOpAmount(HotCardEffectOp op, in HotCard card, HotCardDef? def, int potionPrimaryValue)
    {
        HotCardOpFlags flags = (HotCardOpFlags)op.Flags;
        if ((flags & HotCardOpFlags.FixedOne) != 0)
            return 1;
        if ((flags & HotCardOpFlags.LiteralAmount) != 0)
            return op.Arg0;
        if (def == null)
            return potionPrimaryValue != 0 ? potionPrimaryValue : op.Arg0;
        if ((flags & HotCardOpFlags.UseCardDamage) != 0)
            return def.GetDamage(card.UpgradeLevel, card.Var0Delta);
        if ((flags & HotCardOpFlags.UseCardBlock) != 0)
            return def.GetBlock(card.UpgradeLevel, card.Var1Delta);
        if ((flags & HotCardOpFlags.UseCardMagic) != 0)
            return def.GetMagic(card.UpgradeLevel);
        return op.Arg0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ResolvePowerOpAmount(HotCardEffectOp op, in HotCard card, HotCardDef? def, int potionPrimaryValue)
    {
        HotCardOpFlags flags = (HotCardOpFlags)op.Flags;
        if ((flags & HotCardOpFlags.FixedOne) != 0)
            return 1;
        if (def == null)
            return potionPrimaryValue != 0 ? potionPrimaryValue : 1;
        if ((flags & HotCardOpFlags.UseCardMagic) != 0)
            return def.GetMagic(card.UpgradeLevel);
        if ((flags & HotCardOpFlags.UseCardDamage) != 0)
            return def.GetDamage(card.UpgradeLevel, card.Var0Delta);
        if ((flags & HotCardOpFlags.UseCardBlock) != 0)
            return def.GetBlock(card.UpgradeLevel, card.Var1Delta);
        return 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int SnapshotLivingEnemies(in CombatHotState s, Span<byte> dst)
    {
        int rosterCount = s.RosterCount;
        int written = 0;
        for (int i = 0; i < rosterCount; i++)
        {
            byte enemySlot = s.ActiveEnemyRoster[i];
            if ((s.Enemies[enemySlot].Vital.Flags & HotCreatureFlags.Alive) != 0)
                dst[written++] = enemySlot;
        }
        return written;
    }
}
