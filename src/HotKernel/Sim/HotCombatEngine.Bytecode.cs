using System.Runtime.CompilerServices;

namespace CombatSolver.HotKernel;

internal static partial class HotCombatEngine
{
    /// <summary>
    /// Executes the compiled <see cref="HotCardEffectOp"/> bytecode program (and optional custom handler)
    /// for the card currently in the Play pile at <c>s.PlayStart + playOffsetInPlay</c>.
    /// Zero managed heap allocations on the normal path.
    /// </summary>
    internal static void ExecuteCardEffects(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int playOffsetInPlay,
        int targetEnemySlot)
    {
        int curPlayIdx = s.PlayStart + playOffsetInPlay;
        HotCard playedCard = s.Cards[curPlayIdx];
        HotCardDef cardDef = rules.Cards[playedCard.CardDefId];

        ReadOnlySpan<HotCardEffectOp> ops = rules.GetCardOps(playedCard.CardDefId);
        if (ops.IsEmpty)
        {
            if (cardDef.CustomHandlerId != 0)
                ExecuteCustomCardHandler(ref s, ref cold, rules, scratch, cardDef.CustomHandlerId, playOffsetInPlay, targetEnemySlot);
            return;
        }

        for (int i = 0; i < ops.Length; i++)
        {
            if ((s.Player.Flags & HotCreatureFlags.Alive) == 0)
                break;

            HotCardEffectOp op = ops[i];
            HotCardOpFlags flags = (HotCardOpFlags)op.Flags;

            switch (op.Op)
            {
                case HotCardOpKind.Nop:
                    break;

                case HotCardOpKind.Attack:
                {
                    if (IsOverOrEnding(in s) || !IsValidEnemyTarget(in s, targetEnemySlot))
                        break;

                    int dmg = ResolveOpAmount(op, in playedCard, cardDef);
                    int hits = (flags & HotCardOpFlags.UseCardHits) != 0 ? cardDef.GetHits(playedCard.UpgradeLevel) : 1;
                    bool powered = (flags & HotCardOpFlags.Unpowered) == 0;
                    byte targetCreatureSlot = (byte)(3 + targetEnemySlot);

                    scratch.AttackActive = true;
                    for (int h = 0; h < hits; h++)
                    {
                        if ((s.Player.Flags & HotCreatureFlags.Alive) == 0
                            || (s.Enemies[targetEnemySlot].Vital.Flags & HotCreatureFlags.Alive) == 0)
                        {
                            break;
                        }
                        DealDamage(ref s, ref cold, rules, scratch, dealerSlot: 0, targetSlot: targetCreatureSlot, baseDamage: dmg, isPoweredAttack: powered);
                        if (IsOverOrEnding(in s))
                            break;
                    }
                    scratch.AttackActive = false;
                    break;
                }

                case HotCardOpKind.AttackAll:
                {
                    if (IsOverOrEnding(in s))
                        break;

                    int dmg = ResolveOpAmount(op, in playedCard, cardDef);
                    int hits = (flags & HotCardOpFlags.UseCardHits) != 0 ? cardDef.GetHits(playedCard.UpgradeLevel) : 1;
                    bool powered = (flags & HotCardOpFlags.Unpowered) == 0;

                    Span<byte> targets = stackalloc byte[HotLimits.MaxEnemies];
                    scratch.AttackActive = true;
                    for (int h = 0; h < hits; h++)
                    {
                        if ((s.Player.Flags & HotCreatureFlags.Alive) == 0 || IsOverOrEnding(in s))
                            break;

                        int targetCount = SnapshotLivingEnemies(in s, targets);
                        if (targetCount == 0)
                            break;

                        for (int t = 0; t < targetCount; t++)
                        {
                            if ((s.Player.Flags & HotCreatureFlags.Alive) == 0)
                                break;
                            byte enemySlot = targets[t];
                            if ((s.Enemies[enemySlot].Vital.Flags & HotCreatureFlags.Alive) == 0)
                                continue;
                            DealDamage(ref s, ref cold, rules, scratch, dealerSlot: 0, targetSlot: (byte)(3 + enemySlot), baseDamage: dmg, isPoweredAttack: powered);
                        }
                    }
                    scratch.AttackActive = false;
                    break;
                }

                case HotCardOpKind.AttackRandom:
                {
                    if (IsOverOrEnding(in s))
                        break;

                    int dmg = ResolveOpAmount(op, in playedCard, cardDef);
                    int hits = (flags & HotCardOpFlags.UseCardHits) != 0 ? cardDef.GetHits(playedCard.UpgradeLevel) : 1;
                    bool powered = (flags & HotCardOpFlags.Unpowered) == 0;

                    Span<byte> targets = stackalloc byte[HotLimits.MaxEnemies];
                    scratch.AttackActive = true;
                    for (int h = 0; h < hits; h++)
                    {
                        if ((s.Player.Flags & HotCreatureFlags.Alive) == 0 || IsOverOrEnding(in s))
                            break;

                        int targetCount = SnapshotLivingEnemies(in s, targets);
                        if (targetCount == 0)
                            break;

                        int pick = HotRng.NextInt(ref s.Rng[(int)HotRngStream.CombatTargets], targetCount);
                        byte enemySlot = targets[pick];
                        DealDamage(ref s, ref cold, rules, scratch, dealerSlot: 0, targetSlot: (byte)(3 + enemySlot), baseDamage: dmg, isPoweredAttack: powered);
                    }
                    scratch.AttackActive = false;
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

                    int dmg = ResolveOpAmount(op, in playedCard, cardDef);
                    int hits = (flags & HotCardOpFlags.UseCardHits) != 0 ? cardDef.GetHits(playedCard.UpgradeLevel) : 1;
                    bool powered = (flags & HotCardOpFlags.Unpowered) == 0;
                    byte targetCreatureSlot = (byte)(3 + targetEnemySlot);

                    scratch.AttackActive = true;
                    for (int h = 0; h < hits; h++)
                    {
                        if ((s.Osty.Flags & HotCreatureFlags.Alive) == 0
                            || (s.Enemies[targetEnemySlot].Vital.Flags & HotCreatureFlags.Alive) == 0)
                        {
                            break;
                        }
                        DealDamage(ref s, ref cold, rules, scratch, dealerSlot: 1, targetSlot: targetCreatureSlot, baseDamage: dmg, isPoweredAttack: powered);
                        if (IsOverOrEnding(in s))
                            break;
                    }
                    s.Counters[(int)HotCounterSlot.OstyAttacksThisTurn]++;
                    scratch.AttackActive = false;
                    break;
                }

                case HotCardOpKind.GainBlock:
                {
                    int blk = ResolveOpAmount(op, in playedCard, cardDef);
                    bool isCardBlock = (flags & HotCardOpFlags.Unpowered) == 0;
                    GainBlock(ref s, rules, targetSlot: 0, baseAmount: blk, isCardBlock: isCardBlock);
                    break;
                }

                case HotCardOpKind.Draw:
                {
                    int drawCount = ResolveOpAmount(op, in playedCard, cardDef);
                    DrawCards(ref s, ref cold, rules, scratch, drawCount, fromHandDraw: false);
                    break;
                }

                case HotCardOpKind.ApplyPowerSelf:
                {
                    ushort powerDefId = (ushort)op.Arg0;
                    int amt = ResolvePowerOpAmount(op, in playedCard, cardDef);
                    ApplyPower(ref s, rules, ownerSlot: 0, applierSlot: 0, powerDefId: powerDefId, amount: amt);
                    break;
                }

                case HotCardOpKind.ApplyPowerTarget:
                {
                    if (!IsValidEnemyTarget(in s, targetEnemySlot))
                        break;
                    ushort powerDefId = (ushort)op.Arg0;
                    int amt = ResolvePowerOpAmount(op, in playedCard, cardDef);
                    ApplyPower(ref s, rules, ownerSlot: (byte)(3 + targetEnemySlot), applierSlot: 0, powerDefId: powerDefId, amount: amt);
                    break;
                }

                case HotCardOpKind.ApplyPowerAllEnemies:
                {
                    ushort powerDefId = (ushort)op.Arg0;
                    int amt = ResolvePowerOpAmount(op, in playedCard, cardDef);
                    Span<byte> targets = stackalloc byte[HotLimits.MaxEnemies];
                    int targetCount = SnapshotLivingEnemies(in s, targets);
                    for (int t = 0; t < targetCount; t++)
                    {
                        byte enemySlot = targets[t];
                        if ((s.Enemies[enemySlot].Vital.Flags & HotCreatureFlags.Alive) == 0)
                            continue;
                        ApplyPower(ref s, rules, ownerSlot: (byte)(3 + enemySlot), applierSlot: 0, powerDefId: powerDefId, amount: amt);
                    }
                    break;
                }

                case HotCardOpKind.ApplyTempStat:
                {
                    ushort powerDefId = (ushort)op.Arg0;
                    int amt = ResolvePowerOpAmount(op, in playedCard, cardDef);
                    ApplyPower(ref s, rules, ownerSlot: 0, applierSlot: 0, powerDefId: powerDefId, amount: amt);
                    break;
                }

                case HotCardOpKind.GainEnergy:
                {
                    int amt = ResolveOpAmount(op, in playedCard, cardDef);
                    if (amt > 0)
                        s.Energy += amt;
                    break;
                }

                case HotCardOpKind.GainStars:
                {
                    int amt = ResolveOpAmount(op, in playedCard, cardDef);
                    if (amt > 0)
                    {
                        s.Stars += amt;
                        s.Counters[(int)HotCounterSlot.StarsGainedThisTurn] += amt;
                    }
                    break;
                }

                case HotCardOpKind.LoseHp:
                {
                    int amt = ResolveOpAmount(op, in playedCard, cardDef);
                    if (amt > 0)
                    {
                        DealDamage(
                            ref s,
                            ref cold,
                            rules,
                            scratch,
                            dealerSlot: 0,
                            targetSlot: 0,
                            baseDamage: amt,
                            isPoweredAttack: false,
                            isUnblockable: true);
                    }
                    break;
                }

                case HotCardOpKind.Heal:
                {
                    int amt = ResolveOpAmount(op, in playedCard, cardDef);
                    HealCreature(ref s, targetSlot: 0, amount: amt);
                    break;
                }

                case HotCardOpKind.AddFixedCard:
                {
                    if ((flags & HotCardOpFlags.AddCloneToDiscard) != 0)
                    {
                        int livePlayIdx = s.PlayStart + playOffsetInPlay;
                        ClonePlayedCardToDiscard(ref s, ref cold, rules, livePlayIdx);
                    }
                    else
                    {
                        ushort generatedDefId = (ushort)op.Arg0;
                        AddGeneratedFixedCard(ref s, rules, generatedDefId, HotPileKind.Discard, s.DiscardCount);
                    }
                    break;
                }

                case HotCardOpKind.DiscardHand:
                {
                    while (s.HandCount > 0 && !IsOverOrEnding(in s))
                    {
                        HotPiles.MoveCard(ref s, 0, HotPileKind.Discard, s.DiscardCount);
                        s.Counters[(int)HotCounterSlot.CardsDiscardedThisTurn]++;
                    }
                    break;
                }

                case HotCardOpKind.Custom:
                {
                    ushort handlerId = op.Arg0 > 0 ? (ushort)op.Arg0 : cardDef.CustomHandlerId;
                    ExecuteCustomCardHandler(ref s, ref cold, rules, scratch, handlerId, playOffsetInPlay, targetEnemySlot);
                    break;
                }

                // Reserved Phase 2 character mechanics (Forge, Orbs, Osty summon, Fool arrows)
                case HotCardOpKind.Forge:
                case HotCardOpKind.ChannelOrb:
                case HotCardOpKind.EvokeOrb:
                case HotCardOpKind.SummonOsty:
                case HotCardOpKind.ShootArrow:
                    break;
            }

            if (scratch.NeedsChoice)
                return;
        }
    }

    private static void ExecuteCustomCardHandler(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        ushort customHandlerId,
        int playOffsetInPlay,
        int targetEnemySlot)
    {
        switch (customHandlerId)
        {
            case 0:
                break;

            default:
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ResolveOpAmount(HotCardEffectOp op, in HotCard card, HotCardDef def)
    {
        HotCardOpFlags flags = (HotCardOpFlags)op.Flags;
        if ((flags & HotCardOpFlags.FixedOne) != 0)
            return 1;
        if ((flags & HotCardOpFlags.LiteralAmount) != 0)
            return op.Arg0;
        if ((flags & HotCardOpFlags.UseCardDamage) != 0)
            return def.GetDamage(card.UpgradeLevel, card.Var0Delta);
        if ((flags & HotCardOpFlags.UseCardBlock) != 0)
            return def.GetBlock(card.UpgradeLevel, card.Var1Delta);
        if ((flags & HotCardOpFlags.UseCardMagic) != 0)
            return def.GetMagic(card.UpgradeLevel);
        return op.Arg0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ResolvePowerOpAmount(HotCardEffectOp op, in HotCard card, HotCardDef def)
    {
        HotCardOpFlags flags = (HotCardOpFlags)op.Flags;
        if ((flags & HotCardOpFlags.FixedOne) != 0)
            return 1;
        if ((flags & HotCardOpFlags.UseCardMagic) != 0)
            return def.GetMagic(card.UpgradeLevel);
        if ((flags & HotCardOpFlags.UseCardDamage) != 0)
            return def.GetDamage(card.UpgradeLevel, card.Var0Delta);
        if ((flags & HotCardOpFlags.UseCardBlock) != 0)
            return def.GetBlock(card.UpgradeLevel, card.Var1Delta);
        return 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SnapshotLivingEnemies(in CombatHotState s, Span<byte> dst)
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
