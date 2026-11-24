using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Powers;

namespace CombatSolver.HotKernel;

/// <summary>
/// Zero-allocation STS2 0.113.1 relic hook dispatcher covering all 22 stateful relics, all 24 tracked
/// mirror relics, and all combat-relevant passive/hook-bearing vanilla relics.
/// State encodings in <see cref="CombatHotState.RelicStates"/> match <c>HotRootBuilder</c> and
/// <c>HotContinuationStamp</c> bit-for-bit.
/// </summary>
internal static class HotRelics
{
    private const int MaxStackPoolCards = 512;
    private const int EmotionChipTaggedBit = 1 << 30;
    private const int EmotionChipTurnMask = 0x7FFF;
    private const int EmotionChipRootPrevSentinel = 0x7FFF;
    private const int PaelsEyeWasPartBit = 1;
    private const int PaelsEyeManualPlayedBit = 2;

    public static void DispatchRelicHook(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotHookMask hook,
        byte activeRelicIdx,
        ref HotHookArgs args)
    {
        if (activeRelicIdx >= rules.ActiveRelics.Length)
            return;

        HotActiveRelic ar = rules.ActiveRelics[activeRelicIdx];
        if (ar.RelicDefId == 0 || ar.RelicDefId >= rules.Relics.Length)
            return;

        HotRelicDef def = rules.Relics[ar.RelicDefId];
        HotRelicId relicId = def.RelicId != HotRelicId.None
            ? def.RelicId
            : HotRelicCatalog.FindByEntry(def.Entry);
        if (relicId == HotRelicId.None)
            return;

        byte stateSlot = ar.StateSlot;
        bool hasState = stateSlot < s.RelicStateCount;

        if (args.CardUid != 0)
            HotCombatEngine.RefreshHookCardIdx(in s, ref args);

        switch (hook)
        {
            case HotHookMask.ModifyMaxEnergy:
                HandleModifyMaxEnergy(in s, def, relicId, ref args);
                break;

            case HotHookMask.ModifyHandDraw:
                HandleModifyHandDraw(ref s, rules, relicId, stateSlot, hasState, ref args);
                break;

            case HotHookMask.ShouldDraw:
                HandleShouldDraw(in s, relicId, ref args);
                break;

            case HotHookMask.BeforeHandDraw:
                HandleBeforeHandDraw(ref s, ref cold, rules, scratch, relicId, stateSlot, hasState);
                break;

            case HotHookMask.ModifyEnergyCost:
                HandleModifyEnergyCost(in s, rules, relicId, stateSlot, hasState, ref args);
                break;

            case HotHookMask.ModifyStarCost:
                HandleModifyStarCost(in s, relicId, stateSlot, hasState, ref args);
                break;

            case HotHookMask.ModifyDamageAdditive:
                HandleModifyDamageAdditive(in s, rules, def, relicId, ref args);
                break;

            case HotHookMask.ModifyDamageMultiplicative:
                HandleModifyDamageMultiplicative(in s, rules, relicId, stateSlot, hasState, ref args);
                break;

            case HotHookMask.ModifyDamageCap:
                HandleModifyDamageCap(in s, relicId, stateSlot, hasState, ref args);
                break;

            case HotHookMask.ModifyBlockMultiplicative:
                HandleModifyBlockMultiplicative(ref s, rules, relicId, stateSlot, hasState, ref args);
                break;

            case HotHookMask.ModifyCardPlayCount:
                HandleModifyCardPlayCount(ref s, relicId, stateSlot, hasState, ref args);
                break;

            case HotHookMask.BeforeCardPlayed:
                HandleBeforeCardPlayed(ref s, ref cold, rules, scratch, relicId, stateSlot, hasState, ref args);
                break;

            case HotHookMask.AfterCardPlayed:
                HandleAfterCardPlayed(ref s, ref cold, rules, scratch, relicId, stateSlot, hasState, ref args);
                break;

            case HotHookMask.AfterAttack:
                HandleAfterAttack(ref s, ref cold, rules, scratch, relicId, ref args);
                break;

            case HotHookMask.AfterBlockBroken:
                HandleAfterBlockBroken(ref s, ref cold, rules, scratch, relicId, ref args);
                break;

            case HotHookMask.AfterDamageReceived:
                HandleAfterDamageReceived(ref s, ref cold, rules, scratch, relicId, stateSlot, hasState, ref args);
                break;

            case HotHookMask.AfterDamageGiven:
                HandleAfterDamageGiven(ref s, ref cold, rules, scratch, relicId, ref args);
                break;

            case HotHookMask.AfterHpLost:
                HandleAfterHpLost(ref s, ref cold, rules, scratch, relicId, stateSlot, hasState, ref args);
                break;

            case HotHookMask.AfterHeal:
                HandleAfterHeal(ref s, ref cold, rules, scratch, relicId, stateSlot, hasState, ref args);
                break;

            case HotHookMask.BeforePowerAmountChanged:
                HandleBeforePowerAmountChanged(ref s, rules, relicId, stateSlot, hasState, ref args);
                break;

            case HotHookMask.AfterCardExhausted:
                HandleAfterCardExhausted(ref s, ref cold, rules, scratch, relicId, stateSlot, hasState, ref args);
                break;

            case HotHookMask.AfterCardDiscarded:
                HandleAfterCardDiscarded(ref s, ref cold, rules, scratch, relicId, ref args);
                break;

            case HotHookMask.AfterCardGenerated:
                HandleAfterCardGenerated(ref s, ref cold, rules, scratch, relicId, stateSlot, hasState, ref args);
                break;

            case HotHookMask.AfterCardEnteredCombat:
                HandleAfterCardEnteredCombat(ref s, ref cold, rules, scratch, relicId, ref args);
                break;

            case HotHookMask.AfterShuffle:
                HandleAfterShuffle(ref s, ref cold, rules, scratch, relicId, ref args);
                break;

            case HotHookMask.AfterStarsSpent:
                HandleAfterStarsSpent(ref s, ref cold, rules, scratch, relicId, stateSlot, hasState, ref args);
                break;

            case HotHookMask.AfterOrbChanneled:
                HandleAfterOrbChanneled(ref s, ref cold, rules, scratch, relicId, stateSlot, hasState);
                break;

            case HotHookMask.ShouldClearBlock:
                HandleShouldClearBlock(ref s, ref cold, rules, scratch, relicId, ref args);
                break;

            case HotHookMask.ShouldDie:
                HandleShouldDie(ref s, ref cold, rules, scratch, relicId, stateSlot, hasState, ref args);
                break;

            case HotHookMask.AfterDeath:
                HandleAfterDeath(ref s, ref cold, rules, scratch, relicId, ref args);
                break;

            case HotHookMask.ShouldTakeExtraTurn:
                HandleShouldTakeExtraTurn(ref s, rules, relicId, stateSlot, hasState, ref args);
                break;

            case HotHookMask.BeforeTurnEnd:
                HandleBeforeTurnEnd(ref s, ref cold, rules, scratch, relicId, stateSlot, hasState, ref args);
                break;

            case HotHookMask.AfterTurnEnd:
                HandleAfterTurnEnd(ref s, ref cold, rules, scratch, relicId, stateSlot, hasState, ref args);
                break;

            case HotHookMask.BeforeSideTurnStart:
                HandleBeforeSideTurnStart(ref s, ref cold, rules, scratch, relicId, stateSlot, hasState, ref args);
                break;

            case HotHookMask.AfterSideTurnStart:
                HandleAfterSideTurnStart(ref s, ref cold, rules, scratch, relicId, stateSlot, hasState, ref args);
                break;

            case HotHookMask.AfterCombatEnd:
                HandleAfterCombatEnd(ref s, relicId, stateSlot, hasState);
                break;
        }
    }

    /// <summary>
    /// Fires relic hooks triggered immediately after a potion is consumed in combat
    /// (<c>BeltBuckle</c>, <c>ReptileTrinket</c>).
    /// </summary>
    internal static void OnPotionUsed(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        ushort potionDefId)
    {
        if (!HotCombatEngine.IsInProgress(in s) || (s.Player.Flags & HotCreatureFlags.Alive) == 0)
            return;

        HotActiveRelic[] active = rules.ActiveRelics;
        for (int i = 0; i < active.Length; i++)
        {
            HotActiveRelic ar = active[i];
            if (ar.RelicDefId == 0 || ar.RelicDefId >= rules.Relics.Length)
                continue;

            HotRelicDef def = rules.Relics[ar.RelicDefId];
            HotRelicId relicId = def.RelicId != HotRelicId.None
                ? def.RelicId
                : HotRelicCatalog.FindByEntry(def.Entry);

            switch (relicId)
            {
                case HotRelicId.ReptileTrinket:
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.ReptileTrinketPower, 3);
                    break;

                case HotRelicId.BeltBuckle:
                    if (ar.StateSlot < s.RelicStateCount)
                        SyncBeltBuckle(ref s, ref cold, rules, scratch, ar.StateSlot);
                    break;
            }

            if (scratch.NeedsChoice)
                return;
        }
    }

    /// <summary>
    /// Fires <c>EmotionChip.AfterPlayerTurnStart</c> (EmotionChip.cs:40-57) right after hand draw
    /// and BEFORE <c>AfterSideTurnStart</c> powers (e.g. <c>BiasedCognitionPower</c> Focus loss).
    /// </summary>
    internal static void TriggerEmotionChipAfterPlayerTurnStart(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch)
    {
        if (!HotCombatEngine.IsInProgress(in s) || (s.Player.Flags & HotCreatureFlags.Alive) == 0 || scratch.NeedsChoice)
            return;
        if (!rules.HasRelic(HotRelicId.EmotionChip))
            return;

        HotActiveRelic[] active = rules.ActiveRelics;
        for (int i = 0; i < active.Length; i++)
        {
            HotActiveRelic ar = active[i];
            if (ar.RelicDefId >= rules.Relics.Length || rules.Relics[ar.RelicDefId].RelicId != HotRelicId.EmotionChip)
                continue;
            if (ar.StateSlot >= s.RelicStateCount)
                continue;

            ref HotRelicState rs = ref s.RelicStates[ar.StateSlot];
            int raw = rs.PreviousOrAux;
            int curTurn = Math.Clamp(s.PlayerTurnNumber, 1, EmotionChipRootPrevSentinel - 1);
            int prevTurnNeeded = curTurn - 1;

            bool hitPrevTurn;
            bool hitCurTurn;
            if ((raw & EmotionChipTaggedBit) == 0)
            {
                hitPrevTurn = raw != 0 && prevTurnNeeded > 0;
                hitCurTurn = false;
            }
            else
            {
                int last = raw & EmotionChipTurnMask;
                int prev = (raw >> 15) & EmotionChipTurnMask;
                hitPrevTurn = prevTurnNeeded > 0
                    && (last == prevTurnNeeded || prev == prevTurnNeeded || prev == EmotionChipRootPrevSentinel);
                hitCurTurn = last == curTurn;
            }

            rs.Current = hitPrevTurn ? 1 : 0;
            rs.PreviousOrAux = hitCurTurn ? (EmotionChipTaggedBit | curTurn) : 0;
            if (rs.Current != 0)
            {
                int orbCount = s.OrbCount;
                for (int o = 0; o < orbCount && o < s.OrbCount && !scratch.NeedsChoice && !HotCombatEngine.IsOverOrEnding(in s); o++)
                    HotCombatEngine.TriggerOrbPassive(ref s, ref cold, rules, scratch, o, forcedTargetEnemySlot: -1, applyTriggerCountMods: true);
            }
        }
    }

    /// <summary>
    /// Records an EmotionChip-eligible player damage history entry at the <c>CreatureCmd.cs:364</c>
    /// <c>History.DamageReceived</c> boundary (before <c>AfterDamageReceived</c> / <c>Kill</c>, so
    /// fatal hits revived by LizardTail / FairyInABottle, 0-damage unblocked hits, unblockable hits,
    /// and <c>LoseMaxHp</c> overflow are all captured).
    /// </summary>
    internal static void RecordEmotionChipDamageIfEligible(
        ref CombatHotState s,
        HotRules rules,
        int targetSlot,
        bool wasFullyBlocked)
    {
        if (targetSlot != 0 || wasFullyBlocked || HotCombatEngine.IsOverOrEnding(in s))
            return;
        if (!rules.HasRelic(HotRelicId.EmotionChip))
            return;

        HotActiveRelic[] active = rules.ActiveRelics;
        for (int i = 0; i < active.Length; i++)
        {
            HotActiveRelic ar = active[i];
            if (ar.RelicDefId >= rules.Relics.Length || rules.Relics[ar.RelicDefId].RelicId != HotRelicId.EmotionChip)
                continue;
            if (ar.StateSlot >= s.RelicStateCount)
                continue;

            RecordEmotionChipDamage(ref s.RelicStates[ar.StateSlot], s.PlayerTurnNumber);
        }
    }

    private static void RecordEmotionChipDamage(ref HotRelicState rs, int playerTurnNumber)
    {
        int turn = Math.Clamp(playerTurnNumber, 1, EmotionChipRootPrevSentinel - 1);
        int raw = rs.PreviousOrAux;
        if ((raw & EmotionChipTaggedBit) == 0)
        {
            int prev = raw != 0 ? EmotionChipRootPrevSentinel : 0;
            rs.PreviousOrAux = EmotionChipTaggedBit | (prev << 15) | turn;
            return;
        }

        int last = raw & EmotionChipTurnMask;
        if (last == turn)
            return;
        rs.PreviousOrAux = EmotionChipTaggedBit | (last << 15) | turn;
    }

    internal static int DecodeEmotionChipThisTurn(in HotRelicState rs, int playerTurnNumber)
    {
        int raw = rs.PreviousOrAux;
        if ((raw & EmotionChipTaggedBit) == 0)
            return raw != 0 ? 1 : 0;

        int curTurn = Math.Clamp(playerTurnNumber, 1, EmotionChipRootPrevSentinel - 1);
        return (raw & EmotionChipTurnMask) == curTurn ? 1 : 0;
    }

    /// <summary>
    /// Fires player relics overriding <c>AfterEnergyReset</c> (<c>ArtOfWar</c>,
    /// <c>FakeVenerableTeaSet</c>, <c>VenerableTeaSet</c>) in <see cref="HotRules.ActiveRelics"/> order,
    /// immediately after energy reset and AfterEnergyReset player powers and BEFORE hand draw.
    /// </summary>
    internal static void HandleAfterEnergyResetRelics(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch)
    {
        if (!HotCombatEngine.IsInProgress(in s) || (s.Player.Flags & HotCreatureFlags.Alive) == 0 || scratch.NeedsChoice)
            return;

        HotActiveRelic[] active = rules.ActiveRelics;
        for (int i = 0; i < active.Length; i++)
        {
            HotActiveRelic ar = active[i];
            if (ar.RelicDefId == 0 || ar.RelicDefId >= rules.Relics.Length)
                continue;

            HotRelicDef def = rules.Relics[ar.RelicDefId];
            HotRelicId relicId = def.RelicId != HotRelicId.None
                ? def.RelicId
                : HotRelicCatalog.FindByEntry(def.Entry);
            byte stateSlot = ar.StateSlot;
            bool hasState = stateSlot < s.RelicStateCount;

            switch (relicId)
            {
                case HotRelicId.ArtOfWar:
                    if (hasState && s.PlayerTurnNumber > 1)
                    {
                        ref HotRelicState rs = ref s.RelicStates[stateSlot];
                        if (rs.PreviousOrAux == 0)
                            HotCombatEngine.GainEnergy(ref s, rules, 1);
                        rs.PreviousOrAux = 0;
                        rs.Current = 0;
                    }
                    break;

                case HotRelicId.FakeVenerableTeaSet:
                    if (hasState && s.RelicStates[stateSlot].Current != 0)
                    {
                        HotCombatEngine.GainEnergy(ref s, rules, 1);
                        s.RelicStates[stateSlot].Current = 0;
                    }
                    break;

                case HotRelicId.VenerableTeaSet:
                    if (hasState && s.RelicStates[stateSlot].Current != 0)
                    {
                        HotCombatEngine.GainEnergy(ref s, rules, 2);
                        s.RelicStates[stateSlot].Current = 0;
                    }
                    break;
            }

            if (scratch.NeedsChoice || HotCombatEngine.IsOverOrEnding(in s))
                return;
        }
    }

    /// <summary>
    /// Fires player relics overriding <c>AfterEnergyResetLate</c> (<c>BoundPhylactery</c>) in
    /// <see cref="HotRules.ActiveRelics"/> order, after <c>AfterEnergyReset</c> and BEFORE hand draw.
    /// </summary>
    internal static void HandleAfterEnergyResetLateRelics(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch)
    {
        if (!HotCombatEngine.IsInProgress(in s) || (s.Player.Flags & HotCreatureFlags.Alive) == 0 || scratch.NeedsChoice)
            return;

        HotActiveRelic[] active = rules.ActiveRelics;
        for (int i = 0; i < active.Length; i++)
        {
            HotActiveRelic ar = active[i];
            if (ar.RelicDefId == 0 || ar.RelicDefId >= rules.Relics.Length)
                continue;

            HotRelicDef def = rules.Relics[ar.RelicDefId];
            HotRelicId relicId = def.RelicId != HotRelicId.None
                ? def.RelicId
                : HotRelicCatalog.FindByEntry(def.Entry);

            if (relicId == HotRelicId.BoundPhylactery && s.PlayerTurnNumber != 1)
                HotRegentNecro.SummonOsty(ref s, ref cold, rules, scratch, 1);

            if (scratch.NeedsChoice || HotCombatEngine.IsOverOrEnding(in s))
                return;
        }
    }

    /// <summary>
    /// Fires relic hooks triggered immediately after end-of-turn hand flush
    /// (<c>Bookmark.AfterFlush</c>, after <c>WellLaidPlans</c> and before <c>EndOfTurnCleanup</c>).
    /// </summary>
    internal static void OnAfterFlush(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules)
    {
        if (!HotCombatEngine.IsInProgress(in s) || (s.Player.Flags & HotCreatureFlags.Alive) == 0)
            return;
        if (rules.HasRelic(HotRelicId.Bookmark))
            TriggerBookmark(ref s, ref cold, rules);
    }

    /// <summary>
    /// Fires relic hooks triggered after a potion is procured mid-combat (e.g. <c>EntropicBrew</c>,
    /// <c>PetrifiedToad</c>, <c>Alchemize</c>) so <c>BeltBuckle</c> removes its empty-belt Dexterity.
    /// </summary>
    internal static void OnPotionProcured(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        ushort potionDefId)
    {
        if (!HotCombatEngine.IsInProgress(in s) || (s.Player.Flags & HotCreatureFlags.Alive) == 0)
            return;

        byte slot = rules.GetRelicStateSlot(HotRelicId.BeltBuckle);
        if (slot < s.RelicStateCount && rules.HasRelic(HotRelicId.BeltBuckle))
            SyncBeltBuckle(ref s, ref cold, rules, scratch, slot);
    }

    /// <summary>
    /// Fires relic hooks triggered when a Fool arrow is dislodged (<c>Twine</c> grants 4 unpowered Block).
    /// </summary>
    internal static void OnArrowDislodged(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch)
    {
        if (HotCombatEngine.IsInProgress(in s) && rules.HasRelic(HotRelicId.Twine))
            HotCombatEngine.GainBlock(ref s, ref cold, rules, scratch, targetSlot: 0, baseAmount: 4, isCardBlock: false);
    }

    // =========================================================================
    // Individual Hook Handlers
    // =========================================================================

    private static void HandleModifyMaxEnergy(
        in CombatHotState s,
        HotRelicDef def,
        HotRelicId relicId,
        ref HotHookArgs args)
    {
        switch (relicId)
        {
            case HotRelicId.BlessedAntler:
            case HotRelicId.BloodSoakedRose:
            case HotRelicId.Ectoplasm:
            case HotRelicId.PhilosophersStone:
            case HotRelicId.PrismaticGem:
            case HotRelicId.Sozu:
            case HotRelicId.SpikedGauntlets:
            case HotRelicId.VelvetChoker:
            case HotRelicId.WhisperingEarring:
                args.MaxEnergy += 1;
                break;

            case HotRelicId.Bread:
                if (s.PlayerTurnNumber != 1)
                    args.MaxEnergy += 1;
                break;

            case HotRelicId.PaelsFlesh:
                if (s.PlayerTurnNumber >= 3)
                    args.MaxEnergy += 1;
                break;

            case HotRelicId.PumpkinCandle:
                if (def.PrimaryValue != 0 || def.SecondaryValue != 0)
                    args.MaxEnergy += 1;
                break;
        }
    }

    private static void HandleModifyHandDraw(
        ref CombatHotState s,
        HotRules rules,
        HotRelicId relicId,
        byte stateSlot,
        bool hasState,
        ref HotHookArgs args)
    {
        switch (relicId)
        {
            case HotRelicId.BagOfPreparation:
            case HotRelicId.RingOfTheSnake:
                if (s.PlayerTurnNumber <= 1)
                    args.HandDrawCount += 2;
                break;

            case HotRelicId.RingOfTheDrake:
                if (s.PlayerTurnNumber <= 3)
                    args.HandDrawCount += 2;
                break;

            case HotRelicId.BoomingConch:
                if (rules.IsEliteEncounter && s.PlayerTurnNumber <= 1)
                    args.HandDrawCount += 2;
                break;

            case HotRelicId.BigMushroom:
                if (s.PlayerTurnNumber == 1)
                    args.HandDrawCount = Math.Max(0, args.HandDrawCount - 2);
                break;

            case HotRelicId.Fiddle:
            case HotRelicId.SneckoEye:
                args.HandDrawCount += 2;
                break;

            case HotRelicId.PaelsBlood:
                args.HandDrawCount += 1;
                break;

            case HotRelicId.Pendulum:
                if (hasState && s.RelicStates[stateSlot].Current == 0)
                    args.HandDrawCount += 1;
                break;

            case HotRelicId.PollinousCore:
                if (hasState && s.RelicStates[stateSlot].Current >= 4)
                {
                    args.HandDrawCount += 2;
                    s.RelicStates[stateSlot].Current = 0;
                }
                break;

            case HotRelicId.Pocketwatch:
                if (hasState && s.PlayerTurnNumber > 1 && s.RelicStates[stateSlot].PreviousOrAux <= 3)
                    args.HandDrawCount += 3;
                break;
        }
    }

    private static void HandleShouldDraw(in CombatHotState s, HotRelicId relicId, ref HotHookArgs args)
    {
        if (relicId == HotRelicId.Fiddle && !args.FromHandDraw && s.CurrentSide == HotCombatSide.Player)
            args.BoolFlag = false;
    }

    private static void HandleBeforeHandDraw(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotRelicId relicId,
        byte stateSlot,
        bool hasState)
    {
        switch (relicId)
        {
            case HotRelicId.Pendulum:
                if (hasState)
                    s.RelicStates[stateSlot].Current = (s.RelicStates[stateSlot].Current + 1) % 3;
                break;

            case HotRelicId.PollinousCore:
                if (hasState)
                    s.RelicStates[stateSlot].Current++;
                break;

            case HotRelicId.NinjaScroll:
                if (s.PlayerTurnNumber <= 1)
                {
                    ushort shivId = rules.GetCardDefId(HotCustomCardId.Shiv);
                    if (shivId != 0)
                    {
                        for (int i = 0; i < 3 && !scratch.NeedsChoice && !HotCombatEngine.IsOverOrEnding(in s); i++)
                            HotCombatEngine.AddGeneratedFixedCard(ref s, ref cold, rules, scratch, shivId, HotPileKind.Hand, dstOffset: -1, upgraded: false, createdByPlayer: true);
                    }
                }
                break;

            case HotRelicId.RadiantPearl:
                if (s.PlayerTurnNumber == 1)
                {
                    ushort lumId = rules.GetCardDefId(HotCustomCardId.Luminesce);
                    if (lumId != 0)
                        HotCombatEngine.AddGeneratedFixedCard(ref s, ref cold, rules, scratch, lumId, HotPileKind.Hand, dstOffset: -1, upgraded: false, createdByPlayer: true);
                }
                break;

            case HotRelicId.BizarreLamp:
                if (s.PlayerTurnNumber <= 1)
                {
                    ushort joyId = rules.GetCardDefId(HotCustomCardId.Joy);
                    ushort sadId = rules.GetCardDefId(HotCustomCardId.Sadness);
                    if (joyId != 0)
                        HotCombatEngine.AddGeneratedFixedCard(ref s, ref cold, rules, scratch, joyId, HotPileKind.Hand, dstOffset: -1, upgraded: false, createdByPlayer: true);
                    if (sadId != 0 && !scratch.NeedsChoice && !HotCombatEngine.IsOverOrEnding(in s))
                        HotCombatEngine.AddGeneratedFixedCard(ref s, ref cold, rules, scratch, sadId, HotPileKind.Hand, dstOffset: -1, upgraded: false, createdByPlayer: true);
                }
                break;

            case HotRelicId.BlessedAntler:
                if (s.PlayerTurnNumber == 1)
                {
                    ushort dazedId = rules.GetCardDefId(HotCustomCardId.Dazed);
                    if (dazedId != 0)
                    {
                        for (int i = 0; i < 3 && !scratch.NeedsChoice && !HotCombatEngine.IsOverOrEnding(in s); i++)
                            HotCombatEngine.AddGeneratedFixedCard(ref s, ref cold, rules, scratch, dazedId, HotPileKind.Draw, dstOffset: -2, upgraded: false, createdByPlayer: true);
                    }
                }
                break;

            case HotRelicId.FuneraryMask:
                if (s.PlayerTurnNumber == 1)
                {
                    ushort soulId = rules.GetCardDefId(HotCustomCardId.Soul);
                    if (soulId != 0)
                    {
                        for (int i = 0; i < 3 && !scratch.NeedsChoice && !HotCombatEngine.IsOverOrEnding(in s); i++)
                            HotCombatEngine.AddGeneratedFixedCard(ref s, ref cold, rules, scratch, soulId, HotPileKind.Draw, dstOffset: -2, upgraded: false, createdByPlayer: true);
                    }
                }
                break;

            case HotRelicId.JeweledMask:
                if (s.PlayerTurnNumber <= 1 && s.DrawCount > 0)
                {
                    Span<int> allPowers = stackalloc int[HotLimits.MaxCards];
                    Span<int> nonInnatePowers = stackalloc int[HotLimits.MaxCards];
                    int allCount = 0;
                    int nonInnateCount = 0;
                    int drawStart = s.DrawStart;
                    int drawEnd = drawStart + s.DrawCount;
                    for (int i = drawStart; i < drawEnd; i++)
                    {
                        ushort cid = s.Cards[i].CardDefId;
                        if (cid == 0 || cid >= rules.Cards.Length || rules.Cards[cid].Type != CardType.Power)
                            continue;
                        allPowers[allCount++] = i;
                        if ((s.Cards[i].LocalKeywordsMask & HotCardKeywordMask.Innate) == 0)
                            nonInnatePowers[nonInnateCount++] = i;
                    }
                    if (allCount > 0)
                    {
                        ReadOnlySpan<int> chosenPool = nonInnateCount > 0 ? nonInnatePowers[..nonInnateCount] : allPowers[..allCount];
                        int pick = HotRng.NextInt(ref s.Rng[(int)HotRngStream.CombatCardSelection], chosenPool.Length);
                        int cardIdx = chosenPool[pick];
                        HotPotions.SetCardFreeThisCombat(ref s.Cards[cardIdx], ref cold);
                        HotCombatEngine.MoveCardToHandOrDiscard(ref s, cardIdx);
                    }
                }
                break;

            case HotRelicId.Ocarina:
                if (s.PlayerTurnNumber <= 1)
                {
                    Span<ushort> melodyPool = stackalloc ushort[MaxStackPoolCards];
                    int mCount = 0;
                    HotPotions.CollectMelodyCandidates(rules.UnlockedCharacterCardPool, rules, 0x07, melodyPool, ref mCount);
                    HotPotions.CollectMelodyCandidates(rules.OtherCharacterCardPool, rules, 0x07, melodyPool, ref mCount);
                    if (mCount > 0)
                    {
                        Span<ushort> picked = stackalloc ushort[1];
                        if (HotPotions.TakeRandomDistinct(melodyPool[..mCount], 1, ref s.Rng[(int)HotRngStream.CombatCardGeneration], picked) > 0)
                            HotCombatEngine.AddGeneratedFixedCard(ref s, ref cold, rules, scratch, picked[0], HotPileKind.Hand, dstOffset: -1, upgraded: false, createdByPlayer: true);
                    }
                }
                break;

            case HotRelicId.HornCleat:
                if (s.PlayerTurnNumber == 2)
                    HotCombatEngine.GainBlock(ref s, ref cold, rules, scratch, targetSlot: 0, baseAmount: 14, isCardBlock: false);
                break;

            case HotRelicId.CaptainsWheel:
                if (s.PlayerTurnNumber == 3)
                    HotCombatEngine.GainBlock(ref s, ref cold, rules, scratch, targetSlot: 0, baseAmount: 18, isCardBlock: false);
                break;

            case HotRelicId.SparklingRouge:
                if (s.PlayerTurnNumber == 3)
                {
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.StrengthPower, 1);
                    if (!scratch.NeedsChoice)
                        HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.DexterityPower, 1);
                }
                break;

            case HotRelicId.Toolbox:
                if (s.PlayerTurnNumber == 1 && rules.CombatColorlessPool.Length > 0)
                {
                    Span<ushort> options = stackalloc ushort[3];
                    int rolled = HotPotions.TakeRandomDistinct(rules.CombatColorlessPool, 3, ref s.Rng[(int)HotRngStream.CombatCardGeneration], options);
                    if (rolled > 0)
                    {
                        HotCombatEngine.SetPendingGeneratedChoiceOptions(options[..rolled]);
                        scratch.BeginChoiceGroup(HotChoiceGroupFlags.None);
                        if (!HotCombatEngine.RequestChoice(ref s, scratch, HotChoiceKind.GeneratedCard, sourceCardDefId: 0, minCount: 1, maxCount: 1, out HotChoiceSelection sel))
                            return;
                        scratch.EndChoiceGroup();
                        if (sel.Kind == HotChoiceKind.GeneratedCard && sel.CardDefId != 0)
                            HotCombatEngine.AddGeneratedFixedCard(ref s, ref cold, rules, scratch, sel.CardDefId, HotPileKind.Hand, dstOffset: -1, upgraded: false, createdByPlayer: true);
                    }
                }
                break;
        }
    }

    private static void HandleModifyEnergyCost(
        in CombatHotState s,
        HotRules rules,
        HotRelicId relicId,
        byte stateSlot,
        bool hasState,
        ref HotHookArgs args)
    {
        switch (relicId)
        {
            case HotRelicId.SpikedGauntlets:
                if (args.CardDefId != 0 && args.CardDefId < rules.Cards.Length
                    && rules.Cards[args.CardDefId].Type == CardType.Power)
                {
                    args.IntValue += 1;
                }
                break;

            case HotRelicId.BrilliantScarf:
                if (hasState && s.RelicStates[stateSlot].Current == 5 && IsInHandOrPlay(in s, args.CardIdx))
                    args.IntValue = 0;
                break;
        }
    }

    private static void HandleModifyStarCost(
        in CombatHotState s,
        HotRelicId relicId,
        byte stateSlot,
        bool hasState,
        ref HotHookArgs args)
    {
        if (relicId == HotRelicId.BrilliantScarf
            && hasState
            && s.RelicStates[stateSlot].Current == 5
            && IsInHandOrPlay(in s, args.CardIdx))
        {
            args.IntValue = 0;
        }
    }

    private static void HandleModifyDamageAdditive(
        in CombatHotState s,
        HotRules rules,
        HotRelicDef def,
        HotRelicId relicId,
        ref HotHookArgs args)
    {
        if (!args.IsPoweredAttack || args.CardDefId == 0 || args.CardDefId >= rules.Cards.Length)
            return;
        if (args.ActorSlot != 0 && args.ActorSlot != 1)
            return;

        HotCardDef cardDef = rules.Cards[args.CardDefId];
        switch (relicId)
        {
            case HotRelicId.StrikeDummy:
                if (cardDef.IsStrikeTag)
                    args.DecimalValue += def.PrimaryValue > 0 ? def.PrimaryValue : 3m;
                break;

            case HotRelicId.FakeStrikeDummy:
                if (cardDef.IsStrikeTag)
                    args.DecimalValue += def.PrimaryValue > 0 ? def.PrimaryValue : 1m;
                break;

            case HotRelicId.MiniatureCannon:
                if (args.CardIdx >= 0 && args.CardIdx < s.TotalCardCount && s.Cards[args.CardIdx].UpgradeLevel > 0)
                    args.DecimalValue += def.PrimaryValue > 0 ? def.PrimaryValue : 3m;
                break;

            case HotRelicId.MysticLighter:
                if (args.CardIdx >= 0 && args.CardIdx < s.TotalCardCount && s.Cards[args.CardIdx].EnchId != 0)
                    args.DecimalValue += def.PrimaryValue > 0 ? def.PrimaryValue : 9m;
                break;
        }
    }

    private static void HandleModifyDamageMultiplicative(
        in CombatHotState s,
        HotRules rules,
        HotRelicId relicId,
        byte stateSlot,
        bool hasState,
        ref HotHookArgs args)
    {
        switch (relicId)
        {
            case HotRelicId.PenNib:
            {
                if (!args.IsPoweredAttack || (args.ActorSlot != 0 && args.ActorSlot != 1) || args.CardIdx < 0 || args.CardIdx >= s.TotalCardCount || !hasState)
                    break;
                ref readonly HotRelicState rs = ref s.RelicStates[stateSlot];
                ushort activeUid = (ushort)rs.PreviousOrAux;
                if (activeUid == 0)
                {
                    if (rs.Current == 9 && HotPiles.PileOfIndex(in s, args.CardIdx) != HotPileKind.Play)
                        args.DecimalMultiplier *= 2m;
                }
                else if (s.Cards[args.CardIdx].CardUid == activeUid)
                {
                    args.DecimalMultiplier *= 2m;
                }
                break;
            }

            case HotRelicId.UndyingSigil:
            {
                if (!args.IsPoweredAttack || args.TargetSlot != 0 || args.ActorSlot < 3)
                    break;
                int enemySlot = args.ActorSlot - 3;
                if (enemySlot < s.EnemyCount)
                {
                    int doom = HotCombatEngine.GetPowerAmountById(in s, rules, args.ActorSlot, HotPowerId.DoomPower);
                    if (doom > 0 && s.Enemies[enemySlot].Vital.CurrentHp <= doom)
                        args.DecimalMultiplier *= 0.5m;
                }
                break;
            }

            case HotRelicId.VitruvianMinion:
            {
                if (args.ActorSlot == 0 && args.CardDefId != 0 && args.CardDefId < rules.Cards.Length
                    && rules.Cards[args.CardDefId].IsMinionTag)
                {
                    args.DecimalMultiplier *= 2m;
                }
                break;
            }
        }
    }

    private static void HandleModifyDamageCap(
        in CombatHotState s,
        HotRelicId relicId,
        byte stateSlot,
        bool hasState,
        ref HotHookArgs args)
    {
        // Post-block / post-Osty HP loss phases (BoolFlag == false):
        // Amount == 1 -> ModifyHpLostAfterOsty (BeatingRemnant.cs:42, TungstenRod.cs:19)
        // Amount == 2 -> ModifyHpLostAfterOstyLate (TheBoot.cs:26-49)
        if (args.BoolFlag)
            return;

        switch (relicId)
        {
            case HotRelicId.BeatingRemnant:
                if (args.Amount == 1 && args.TargetSlot == 0 && hasState)
                {
                    decimal remainingCap = Math.Max(0m, 20m - s.RelicStates[stateSlot].Current);
                    if (args.DecimalValue > remainingCap)
                        args.DecimalValue = remainingCap;
                }
                break;

            case HotRelicId.TungstenRod:
                if (args.Amount == 1 && args.TargetSlot == 0 && args.DecimalValue > 0m)
                    args.DecimalValue = Math.Max(0m, args.DecimalValue - 1m);
                break;

            case HotRelicId.TheBoot:
                if (args.Amount == 2
                    && args.IsPoweredAttack
                    && (args.ActorSlot == 0 || args.ActorSlot == 1)
                    && args.TargetSlot >= 3
                    && args.DecimalValue >= 1m
                    && args.DecimalValue < 5m)
                {
                    args.DecimalValue = 5m;
                }
                break;
        }
    }

    private static void HandleModifyBlockMultiplicative(
        ref CombatHotState s,
        HotRules rules,
        HotRelicId relicId,
        byte stateSlot,
        bool hasState,
        ref HotHookArgs args)
    {
        if (args.TargetSlot != 0)
            return;

        switch (relicId)
        {
            case HotRelicId.VitruvianMinion:
                if (args.CardDefId != 0 && args.CardDefId < rules.Cards.Length && rules.Cards[args.CardDefId].IsMinionTag)
                    args.DecimalMultiplier *= 2m;
                break;

            case HotRelicId.Vambrace:
                if (hasState && args.IsCardBlock && args.DecimalValue > 0m && args.CardIdx >= 0 && args.CardIdx < s.TotalCardCount)
                {
                    ref HotRelicState rs = ref s.RelicStates[stateSlot];
                    if (rs.Current == 0)
                    {
                        ushort uid = s.Cards[args.CardIdx].CardUid;
                        if (rs.PreviousOrAux == 0 || rs.PreviousOrAux == uid)
                        {
                            args.DecimalMultiplier *= 2m;
                            rs.PreviousOrAux = uid;
                        }
                    }
                }
                break;

            case HotRelicId.PaelsLegion:
                if (hasState && args.IsCardBlock && args.DecimalValue > 0m && args.CardIdx >= 0 && args.CardIdx < s.TotalCardCount)
                {
                    ref HotRelicState rs = ref s.RelicStates[stateSlot];
                    if (rs.Current <= 0)
                    {
                        bool hasAffected = (rs.PreviousOrAux & 2) != 0;
                        ushort affectedUid = (ushort)(rs.PreviousOrAux >> 2);
                        ushort uid = s.Cards[args.CardIdx].CardUid;
                        if (!hasAffected || affectedUid == 0 || affectedUid == uid)
                        {
                            args.DecimalMultiplier *= 2m;
                            rs.PreviousOrAux = (rs.PreviousOrAux & 1) | 2 | ((int)uid << 2);
                        }
                    }
                }
                break;
        }
    }

    private static void HandleModifyCardPlayCount(
        ref CombatHotState s,
        HotRelicId relicId,
        byte stateSlot,
        bool hasState,
        ref HotHookArgs args)
    {
        if (relicId == HotRelicId.ThrowingAxe && hasState && s.RelicStates[stateSlot].Current == 0)
        {
            args.PlayCount += 1;
            s.RelicStates[stateSlot].Current = 1;
        }
    }

    private static void HandleBeforeCardPlayed(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotRelicId relicId,
        byte stateSlot,
        bool hasState,
        ref HotHookArgs args)
    {
        if (args.ActorSlot != 0 || args.CardDefId == 0 || args.CardDefId >= rules.Cards.Length)
            return;

        HotCardDef cardDef = rules.Cards[args.CardDefId];
        switch (relicId)
        {
            case HotRelicId.ChemicalX:
                // Vanilla ChemicalX.BeforeCardPlayed (ChemicalX.cs:17-29) only calls Flash();
                // the +2 bonus is applied in ModifyXValue at X-cost resolution sites
                // (ResolveCoreEnergyX/ResolveCoreStarX, HotRegentNecro, HotFoolTarot),
                // never by mutating CapturedEnergy / LastStarsSpent.
                break;

            case HotRelicId.IntimidatingHelmet:
                if (args.EnergySpent >= 2)
                    HotCombatEngine.GainBlock(ref s, ref cold, rules, scratch, targetSlot: 0, baseAmount: 4, isCardBlock: false);
                break;

            case HotRelicId.MusicBox:
                if (hasState
                    && cardDef.Type == CardType.Attack
                    && s.RelicStates[stateSlot].Current == 0
                    && s.RelicStates[stateSlot].PreviousOrAux == 0)
                {
                    ushort playedUid = ResolveHookCardUid(in s, in args);
                    if (playedUid != 0)
                        s.RelicStates[stateSlot].PreviousOrAux = playedUid;
                }
                break;

            case HotRelicId.PaelsEye:
                // STS2 0.113.1 PaelsEye.cs:69,156: only non-auto card plays this turn disable Pael's Eye.
                if (hasState && !args.IsAutoPlay && s.RelicStates[stateSlot].Current == 0)
                    s.RelicStates[stateSlot].PreviousOrAux |= PaelsEyeManualPlayedBit;
                break;

            case HotRelicId.PenNib:
                if (hasState && cardDef.Type == CardType.Attack)
                {
                    ref HotRelicState rs = ref s.RelicStates[stateSlot];
                    rs.Current = (rs.Current + 1) % 10;
                    if (rs.Current == 0)
                    {
                        ushort playedUid = ResolveHookCardUid(in s, in args);
                        if (playedUid != 0)
                            rs.PreviousOrAux = playedUid;
                    }
                }
                break;
        }
    }

    private static void HandleAfterCardPlayed(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotRelicId relicId,
        byte stateSlot,
        bool hasState,
        ref HotHookArgs args)
    {
        if (args.ActorSlot != 0 || args.CardDefId == 0 || args.CardDefId >= rules.Cards.Length)
            return;

        HotCardDef cardDef = rules.Cards[args.CardDefId];

        switch (relicId)
        {
            case HotRelicId.ArtOfWar:
                if (hasState && cardDef.Type == CardType.Attack && s.RelicStates[stateSlot].PreviousOrAux == 0)
                    s.RelicStates[stateSlot].Current = 1;
                break;

            case HotRelicId.BrilliantScarf:
                if (hasState && !args.IsAutoPlay)
                    s.RelicStates[stateSlot].Current++;
                break;

            case HotRelicId.Byrdpip:
                if (hasState && cardDef.CardId == HotCustomCardId.ByrdSwoop && s.RelicStates[stateSlot].Current < 6)
                    s.RelicStates[stateSlot].Current++;
                break;

            case HotRelicId.ConductorsBaton:
                if (hasState && s.RelicStates[stateSlot].Current == 0 && HotFoolTarot.CountMelodies(in s) >= 3)
                {
                    s.RelicStates[stateSlot].Current = 1;
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.ConductorsBatonPower, 1);
                }
                break;

            case HotRelicId.DaughterOfTheWind:
                if (cardDef.Type == CardType.Attack)
                    HotCombatEngine.GainBlock(ref s, ref cold, rules, scratch, targetSlot: 0, baseAmount: 1, isCardBlock: false);
                break;

            case HotRelicId.GamePiece:
                if (cardDef.Type == CardType.Power)
                    HotCombatEngine.DrawCards(ref s, ref cold, rules, scratch, 1, fromHandDraw: false);
                break;

            case HotRelicId.HelicalDart:
                if (cardDef.IsShivTag)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.HelicalDartPower, 1);
                break;

            case HotRelicId.IronClub:
                if (hasState)
                {
                    ref HotRelicState rs = ref s.RelicStates[stateSlot];
                    rs.Current++;
                    if (rs.Current % 4 == 0)
                        HotCombatEngine.DrawCards(ref s, ref cold, rules, scratch, 1, fromHandDraw: false);
                }
                break;

            case HotRelicId.IvoryTile:
                if (args.EnergySpent >= 3)
                    HotCombatEngine.GainEnergy(ref s, rules, 1);
                break;

            case HotRelicId.Kunai:
                if (hasState && cardDef.Type == CardType.Attack)
                {
                    ref HotRelicState rs = ref s.RelicStates[stateSlot];
                    rs.Current++;
                    if (rs.Current % 3 == 0 && !HotCombatEngine.IsOverOrEnding(in s))
                        HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.DexterityPower, 1);
                }
                break;

            case HotRelicId.Kusarigama:
                if (hasState && cardDef.Type == CardType.Attack)
                {
                    ref HotRelicState rs = ref s.RelicStates[stateSlot];
                    rs.Current++;
                    if (rs.Current % 3 == 0 && !HotCombatEngine.IsOverOrEnding(in s))
                        HotCombatEngine.DamageRandomEnemy(ref s, ref cold, rules, scratch, dealerSlot: 0, baseDamage: 6, isPoweredAttack: false);
                }
                break;

            case HotRelicId.LetterOpener:
                if (hasState && cardDef.Type == CardType.Skill)
                {
                    ref HotRelicState rs = ref s.RelicStates[stateSlot];
                    rs.Current++;
                    if (rs.Current % 3 == 0 && !HotCombatEngine.IsOverOrEnding(in s))
                        HotCombatEngine.DamageAllEnemies(ref s, ref cold, rules, scratch, dealerSlot: 0, baseDamage: 5, isPoweredAttack: false);
                }
                break;

            case HotRelicId.LostWisp:
                if (cardDef.Type == CardType.Power)
                    HotCombatEngine.DamageAllEnemies(ref s, ref cold, rules, scratch, dealerSlot: 0, baseDamage: 8, isPoweredAttack: false);
                break;

            case HotRelicId.MummifiedHand:
                if (cardDef.Type == CardType.Power && s.HandCount > 0)
                    TriggerMummifiedHand(ref s, ref cold, rules);
                break;

            case HotRelicId.MusicBox:
                if (hasState && s.RelicStates[stateSlot].PreviousOrAux != 0)
                {
                    ushort trackedUid = (ushort)s.RelicStates[stateSlot].PreviousOrAux;
                    if (ResolveHookCardUid(in s, in args) == trackedUid && (uint)args.CardIdx < (uint)s.TotalCardCount)
                    {
                        s.RelicStates[stateSlot].Current = 1;
                        s.RelicStates[stateSlot].PreviousOrAux = 0;
                        HotCard clone = CloneCardWithCostOverflow(ref s, ref cold, args.CardIdx);
                        HotCombatEngine.ApplyCardKeywordOutsideUpgrade(ref clone, HotCardKeywordMask.Ethereal);
                        HotCombatEngine.AddGeneratedCardInstance(ref s, ref cold, rules, scratch, in clone, HotPileKind.Hand, dstOffset: -1, createdByPlayer: true);
                    }
                }
                break;

            case HotRelicId.Nunchaku:
                if (hasState && cardDef.Type == CardType.Attack)
                {
                    ref HotRelicState rs = ref s.RelicStates[stateSlot];
                    rs.Current++;
                    if (rs.Current % 10 == 0)
                        HotCombatEngine.GainEnergy(ref s, rules, 1);
                }
                break;

            case HotRelicId.OrnamentalFan:
                if (hasState && cardDef.Type == CardType.Attack)
                {
                    ref HotRelicState rs = ref s.RelicStates[stateSlot];
                    rs.Current++;
                    if (rs.Current % 3 == 0 && !HotCombatEngine.IsOverOrEnding(in s))
                        HotCombatEngine.GainBlock(ref s, ref cold, rules, scratch, targetSlot: 0, baseAmount: 4, isCardBlock: false);
                }
                break;

            case HotRelicId.PaelsLegion:
                if (hasState && (s.RelicStates[stateSlot].PreviousOrAux & 2) != 0)
                {
                    ref HotRelicState rs = ref s.RelicStates[stateSlot];
                    ushort affectedUid = (ushort)(rs.PreviousOrAux >> 2);
                    ushort playedUid = ResolveHookCardUid(in s, in args);
                    if (affectedUid == 0 || affectedUid == playedUid)
                    {
                        rs.Current = 2;
                        rs.PreviousOrAux = 1; // TriggeredBlockLastTurn = true, HasAffectedCardPlay = false
                    }
                }
                break;

            case HotRelicId.PenNib:
                if (hasState && s.RelicStates[stateSlot].PreviousOrAux != 0)
                {
                    ushort playedUid = ResolveHookCardUid(in s, in args);
                    if (playedUid != 0 && playedUid == (ushort)s.RelicStates[stateSlot].PreviousOrAux)
                        s.RelicStates[stateSlot].PreviousOrAux = 0;
                }
                break;

            case HotRelicId.Permafrost:
                if (hasState && cardDef.Type == CardType.Power && s.RelicStates[stateSlot].Current == 0)
                {
                    s.RelicStates[stateSlot].Current = 1;
                    HotCombatEngine.GainBlock(ref s, ref cold, rules, scratch, targetSlot: 0, baseAmount: 7, isCardBlock: false);
                }
                break;

            case HotRelicId.Pocketwatch:
                if (hasState)
                    s.RelicStates[stateSlot].Current++;
                break;

            case HotRelicId.RainbowRing:
                if (hasState && !HotCombatEngine.IsOverOrEnding(in s))
                {
                    ref HotRelicState rs = ref s.RelicStates[stateSlot];
                    int atk = rs.Current & 0xFFFF;
                    int skl = (rs.Current >> 16) & 0xFFFF;
                    int pwr = rs.PreviousOrAux & 0xFFFF;
                    int act = (rs.PreviousOrAux >> 16) & 0xFFFF;
                    if (act < 1)
                    {
                        if (cardDef.Type == CardType.Attack) atk++;
                        else if (cardDef.Type == CardType.Skill) skl++;
                        else if (cardDef.Type == CardType.Power) pwr++;

                        bool trigger = atk > 0 && skl > 0 && pwr > 0;
                        if (trigger)
                            act++;

                        rs.Current = (atk & 0xFFFF) | ((skl & 0xFFFF) << 16);
                        rs.PreviousOrAux = (pwr & 0xFFFF) | ((act & 0xFFFF) << 16);

                        if (trigger)
                        {
                            HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.StrengthPower, 1);
                            if (!scratch.NeedsChoice)
                                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.DexterityPower, 1);
                        }
                    }
                }
                break;

            case HotRelicId.RazorTooth:
                if (cardDef.Type is CardType.Attack or CardType.Skill && args.CardIdx >= 0 && args.CardIdx < s.TotalCardCount)
                    HotCombatEngine.UpgradeCard(ref s, rules, args.CardIdx, ref cold);
                break;

            case HotRelicId.Shuriken:
                if (hasState && cardDef.Type == CardType.Attack)
                {
                    ref HotRelicState rs = ref s.RelicStates[stateSlot];
                    rs.Current++;
                    if (rs.Current % 3 == 0 && !HotCombatEngine.IsOverOrEnding(in s))
                        HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.StrengthPower, 1);
                }
                break;

            case HotRelicId.TuningFork:
                if (hasState && cardDef.Type == CardType.Skill)
                {
                    ref HotRelicState rs = ref s.RelicStates[stateSlot];
                    rs.Current++;
                    if (rs.Current >= 10)
                    {
                        HotCombatEngine.GainBlock(ref s, ref cold, rules, scratch, targetSlot: 0, baseAmount: 7, isCardBlock: false);
                        rs.Current -= 10;
                    }
                }
                break;

            case HotRelicId.UnsettlingLamp:
                if (hasState && s.RelicStates[stateSlot].Current == 0 && s.RelicStates[stateSlot].PreviousOrAux != 0)
                {
                    s.RelicStates[stateSlot].Current = 1;
                    s.RelicStates[stateSlot].PreviousOrAux = 0;
                }
                break;

            case HotRelicId.Vambrace:
                if (hasState && s.RelicStates[stateSlot].Current == 0 && s.RelicStates[stateSlot].PreviousOrAux != 0)
                {
                    ushort playedUid = ResolveHookCardUid(in s, in args);
                    if (playedUid != 0 && playedUid == (ushort)s.RelicStates[stateSlot].PreviousOrAux)
                    {
                        s.RelicStates[stateSlot].Current = 1;
                        s.RelicStates[stateSlot].PreviousOrAux = 0;
                    }
                }
                break;
        }
    }

    private static void HandleAfterAttack(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotRelicId relicId,
        ref HotHookArgs args)
    {
        if (relicId == HotRelicId.BoneFlute && args.ActorSlot == 1)
            HotCombatEngine.GainBlock(ref s, ref cold, rules, scratch, targetSlot: 0, baseAmount: 2, isCardBlock: false);
    }

    private static void HandleAfterBlockBroken(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotRelicId relicId,
        ref HotHookArgs args)
    {
        if (relicId == HotRelicId.HandDrill
            && (args.ActorSlot == 0 || args.ActorSlot == 1)
            && args.TargetSlot >= 3)
        {
            HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, args.TargetSlot, 0, HotPowerId.VulnerablePower, 2);
        }
    }

    private static void HandleAfterDamageReceived(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotRelicId relicId,
        byte stateSlot,
        bool hasState,
        ref HotHookArgs args)
    {
        if (args.TargetSlot != 0)
            return;

        // STS2 0.113.1 EmotionChip.cs:19 & CreatureCmd.cs:339,364: LostHpInPreviousTurn checks
        // History DamageReceivedEntry (!WasFullyBlocked && HappenedLastPlayerTurn), which includes
        // 0-UnblockedDamage hits that were not fully blocked (e.g. TungstenRod/Buffer/0 dmg with 0 Block).
        if (relicId == HotRelicId.EmotionChip)
        {
            if (hasState && !args.WasFullyBlocked && !HotCombatEngine.IsOverOrEnding(in s))
                RecordEmotionChipDamage(ref s.RelicStates[stateSlot], s.PlayerTurnNumber);
            return;
        }

        if (args.UnblockedDamage <= 0)
            return;

        switch (relicId)
        {
            case HotRelicId.BeatingRemnant:
                if (hasState)
                    s.RelicStates[stateSlot].Current += args.UnblockedDamage;
                break;

            case HotRelicId.CentennialPuzzle:
                if (hasState && s.RelicStates[stateSlot].Current == 0)
                {
                    s.RelicStates[stateSlot].Current = 1;
                    HotCombatEngine.DrawCards(ref s, ref cold, rules, scratch, 3, fromHandDraw: false);
                }
                break;

            case HotRelicId.DemonTongue:
                if (hasState && s.CurrentSide == HotCombatSide.Player && s.RelicStates[stateSlot].Current == 0)
                {
                    s.RelicStates[stateSlot].Current = 1;
                    HotCombatEngine.HealCreature(ref s, ref cold, rules, scratch, targetSlot: 0, amount: args.UnblockedDamage);
                }
                break;

            case HotRelicId.SelfFormingClay:
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.SelfFormingClayPower, 3);
                break;
        }
    }

    private static void HandleAfterDamageGiven(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotRelicId relicId,
        ref HotHookArgs args)
    {
        if (relicId == HotRelicId.MightyWings
            && args.IsPoweredAttack
            && (args.ActorSlot == 0 || args.ActorSlot == 1)
            && args.Amount >= 30)
        {
            HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.StrengthPower, 2);
        }
    }

    private static void HandleAfterHpLost(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotRelicId relicId,
        byte stateSlot,
        bool hasState,
        ref HotHookArgs args)
    {
        switch (relicId)
        {
            case HotRelicId.RedSkull:
                if (hasState)
                    SyncRedSkull(ref s, ref cold, rules, scratch, stateSlot);
                break;

            case HotRelicId.Wuppy:
                if (hasState && args.TargetSlot == 2)
                    s.RelicStates[stateSlot].Current = s.Wuppy.CurrentHp;
                break;
        }
    }

    private static void HandleAfterHeal(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotRelicId relicId,
        byte stateSlot,
        bool hasState,
        ref HotHookArgs args)
    {
        if (relicId == HotRelicId.RedSkull && args.TargetSlot == 0 && hasState)
            SyncRedSkull(ref s, ref cold, rules, scratch, stateSlot);
    }

    private static void HandleBeforePowerAmountChanged(
        ref CombatHotState s,
        HotRules rules,
        HotRelicId relicId,
        byte stateSlot,
        bool hasState,
        ref HotHookArgs args)
    {
        if (args.PowerDefId == 0 || args.PowerDefId >= rules.Powers.Length)
            return;

        HotPowerDef pDef = rules.Powers[args.PowerDefId];
        switch (relicId)
        {
            case HotRelicId.SneckoSkull:
                if (args.ActorSlot == 0 && pDef.PowerId == HotPowerId.PoisonPower && args.Amount > 0)
                    args.Amount += 1;
                break;

            case HotRelicId.RuinedHelmet:
                if (hasState
                    && s.RelicStates[stateSlot].Current == 0
                    && args.TargetSlot == 0
                    && pDef.PowerId == HotPowerId.StrengthPower
                    && args.Amount > 0)
                {
                    args.Amount *= 2;
                    s.RelicStates[stateSlot].Current = 1;
                }
                break;

            case HotRelicId.UnsettlingLamp:
                if (hasState
                    && s.RelicStates[stateSlot].Current == 0
                    && args.IsCardBlock // carries isCardSource from ApplyPowerRaw
                    && args.ActorSlot == 0
                    && args.TargetSlot >= 3)
                {
                    bool allowNeg = pDef.PowerId is HotPowerId.StrengthPower or HotPowerId.DexterityPower or HotPowerId.FocusPower;
                    bool isDebuff = (pDef.Type == PowerType.Debuff && args.Amount > 0)
                        || (pDef.Type == PowerType.Buff && allowNeg && args.Amount < 0);
                    if (isDebuff && HotCombatEngine.GetPowerAmountById(in s, rules, args.TargetSlot, HotPowerId.ArtifactPower) == 0)
                    {
                        s.RelicStates[stateSlot].PreviousOrAux = 1;
                        args.Amount *= 2;
                    }
                }
                break;
        }
    }

    private static void HandleAfterCardExhausted(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotRelicId relicId,
        byte stateSlot,
        bool hasState,
        ref HotHookArgs args)
    {
        switch (relicId)
        {
            case HotRelicId.BurningSticks:
                if (hasState
                    && s.RelicStates[stateSlot].Current == 0
                    && args.CardDefId != 0
                    && args.CardDefId < rules.Cards.Length
                    && rules.Cards[args.CardDefId].Type == CardType.Skill
                    && args.CardIdx >= 0
                    && args.CardIdx < s.TotalCardCount)
                {
                    s.RelicStates[stateSlot].Current = 1;
                    HotCard clone = CloneCardWithCostOverflow(ref s, ref cold, args.CardIdx);
                    HotCombatEngine.AddGeneratedCardInstance(ref s, ref cold, rules, scratch, in clone, HotPileKind.Hand, dstOffset: -1, createdByPlayer: true);
                }
                break;

            case HotRelicId.CharonsAshes:
                HotCombatEngine.DamageAllEnemies(ref s, ref cold, rules, scratch, dealerSlot: 0, baseDamage: 3, isPoweredAttack: false);
                break;

            case HotRelicId.ForgottenSoul:
                HotCombatEngine.DamageRandomEnemy(ref s, ref cold, rules, scratch, dealerSlot: 0, baseDamage: 1, isPoweredAttack: false);
                break;

            case HotRelicId.JossPaper:
                if (hasState)
                {
                    ref HotRelicState rs = ref s.RelicStates[stateSlot];
                    if (args.CausedByEthereal)
                    {
                        rs.PreviousOrAux++;
                    }
                    else
                    {
                        rs.Current++;
                        if (rs.Current >= 5)
                        {
                            int draws = rs.Current / 5;
                            rs.Current %= 5;
                            HotCombatEngine.DrawCards(ref s, ref cold, rules, scratch, draws, fromHandDraw: false);
                        }
                    }
                }
                break;
        }
    }

    private static void HandleAfterCardDiscarded(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotRelicId relicId,
        ref HotHookArgs args)
    {
        if (s.CurrentSide != HotCombatSide.Player)
            return;

        switch (relicId)
        {
            case HotRelicId.Tingsha:
                HotCombatEngine.DamageRandomEnemy(ref s, ref cold, rules, scratch, dealerSlot: 0, baseDamage: 3, isPoweredAttack: false);
                break;

            case HotRelicId.ToughBandages:
                HotCombatEngine.GainBlock(ref s, ref cold, rules, scratch, targetSlot: 0, baseAmount: 3, isCardBlock: false);
                break;
        }
    }

    private static void HandleAfterCardGenerated(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotRelicId relicId,
        byte stateSlot,
        bool hasState,
        ref HotHookArgs args)
    {
        if (args.ActorSlot != 0)
            return;

        switch (relicId)
        {
            case HotRelicId.Fertilizer:
                if (args.CardDefId != 0 && args.CardDefId < rules.Cards.Length
                    && rules.Cards[args.CardDefId].CardId == HotCustomCardId.Sadness)
                {
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.StrengthPower, 1);
                }
                break;

            case HotRelicId.Regalite:
                if (hasState && s.RelicStates[stateSlot].Current == 0)
                {
                    s.RelicStates[stateSlot].Current = 1;
                    HotCombatEngine.GainBlock(ref s, ref cold, rules, scratch, targetSlot: 0, baseAmount: 4, isCardBlock: false);
                }
                break;
        }
    }

    private static void HandleAfterCardEnteredCombat(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotRelicId relicId,
        ref HotHookArgs args)
    {
        switch (relicId)
        {
            case HotRelicId.GhostSeed:
                if (args.CardIdx >= 0 && args.CardIdx < s.TotalCardCount && args.CardDefId < rules.Cards.Length)
                {
                    HotCardDef def = rules.Cards[args.CardDefId];
                    if (def.Rarity == CardRarity.Basic && (def.IsStrikeTag || def.IsDefendTag))
                        HotCombatEngine.ApplyCardKeywordOutsideUpgrade(ref s.Cards[args.CardIdx], HotCardKeywordMask.Ethereal);
                }
                break;
        }
    }

    private static void HandleAfterShuffle(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotRelicId relicId,
        ref HotHookArgs args)
    {
        if (args.ActorSlot != 0)
            return;

        switch (relicId)
        {
            case HotRelicId.TheAbacus:
                HotCombatEngine.GainBlock(ref s, ref cold, rules, scratch, targetSlot: 0, baseAmount: 6, isCardBlock: false);
                break;

            case HotRelicId.WhittlingKnife:
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.AimPower, 5);
                break;

            case HotRelicId.BiiigHug:
            {
                ushort sootId = rules.GetCardDefId(HotCustomCardId.Soot);
                if (sootId != 0)
                    HotCombatEngine.AddGeneratedFixedCard(ref s, ref cold, rules, scratch, sootId, HotPileKind.Draw, dstOffset: -2, upgraded: false, createdByPlayer: true);
                break;
            }
        }
    }

    private static void HandleAfterStarsSpent(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotRelicId relicId,
        byte stateSlot,
        bool hasState,
        ref HotHookArgs args)
    {
        if (!hasState || args.StarsSpent <= 0)
            return;

        switch (relicId)
        {
            case HotRelicId.MiniRegent:
                if (s.RelicStates[stateSlot].Current == 0)
                {
                    s.RelicStates[stateSlot].Current = 1;
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.StrengthPower, 1);
                }
                break;

            case HotRelicId.GalacticDust:
            {
                ref HotRelicState rs = ref s.RelicStates[stateSlot];
                rs.Current += args.StarsSpent;
                if (rs.Current >= 10)
                {
                    int triggers = rs.Current / 10;
                    rs.Current %= 10;
                    HotCombatEngine.GainBlock(ref s, ref cold, rules, scratch, targetSlot: 0, baseAmount: triggers * 10, isCardBlock: false);
                }
                break;
            }
        }
    }

    private static void HandleAfterOrbChanneled(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotRelicId relicId,
        byte stateSlot,
        bool hasState)
    {
        if (relicId == HotRelicId.Metronome && hasState)
        {
            ref HotRelicState rs = ref s.RelicStates[stateSlot];
            rs.Current++;
            if (rs.Current == 7)
                HotCombatEngine.DamageAllEnemies(ref s, ref cold, rules, scratch, dealerSlot: 0, baseDamage: 30, isPoweredAttack: false);
        }
    }

    private static void HandleShouldClearBlock(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotRelicId relicId,
        ref HotHookArgs args)
    {
        if (args.TargetSlot != 0)
            return;

        switch (relicId)
        {
            case HotRelicId.SturdyClamp:
            {
                // Keep up to 10 Block unless Barricade/Blur keeps all Block.
                bool fullRetainPower = HotCombatEngine.GetPowerAmountById(in s, rules, 0, HotPowerId.BarricadePower) > 0
                    || HotCombatEngine.GetPowerAmountById(in s, rules, 0, HotPowerId.BlurPower) > 0;
                if (!fullRetainPower)
                {
                    if (s.Player.Block > 10)
                        s.Player.Block = 10;
                    args.BoolFlag = false;
                }
                break;
            }

            case HotRelicId.HornCleat:
                // Applied via BlockNextTurnPower so TriggerAfterBlockClearedPowers grants 14 unpowered Block right after clear.
                if (s.PlayerTurnNumber == 2)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.BlockNextTurnPower, 14);
                break;

            case HotRelicId.CaptainsWheel:
                if (s.PlayerTurnNumber == 3)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.BlockNextTurnPower, 18);
                break;

            case HotRelicId.SparklingRouge:
                if (s.PlayerTurnNumber == 3)
                {
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.StrengthPower, 1);
                    if (!scratch.NeedsChoice)
                        HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.DexterityPower, 1);
                }
                break;
        }
    }

    private static void HandleShouldDie(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotRelicId relicId,
        byte stateSlot,
        bool hasState,
        ref HotHookArgs args)
    {
        if (relicId == HotRelicId.LizardTail
            && args.TargetSlot == 0
            && args.BoolFlag
            && hasState
            && s.RelicStates[stateSlot].Current == 0)
        {
            s.RelicStates[stateSlot].Current = 1;
            args.BoolFlag = false;

            int healAmt = Math.Max(1, (int)((decimal)s.Player.MaxHp * 0.5m));
            int healed = HotCombatEngine.HealCreature(ref s, ref cold, rules, scratch, targetSlot: 0, amount: healAmt);
            s.DeathSaveRelicHpRestored += healed;
            s.DeathSaveUseCount++;
        }
    }

    private static void HandleAfterDeath(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotRelicId relicId,
        ref HotHookArgs args)
    {
        if (args.TargetSlot < 3)
            return;

        switch (relicId)
        {
            case HotRelicId.GremlinHorn:
                HotCombatEngine.GainEnergy(ref s, rules, 1);
                HotCombatEngine.DrawCards(ref s, ref cold, rules, scratch, 1, fromHandDraw: false);
                break;
        }
    }

    /// <summary>
    /// Vanilla STS2 0.113.1 <c>Hook.ShouldTakeExtraTurn</c> (Hook.cs:2563-2583) Pass 1
    /// (<c>ShouldTakeExtraTurnEarly</c>) + <c>PaelsEye.AfterTakingExtraTurn</c> (PaelsEye.cs:109-140):
    /// consumes <c>PaelsEye</c> ONLY and leaves <c>ExtraTurnPower</c> untouched when triggered.
    /// </summary>
    internal static bool TryConsumePaelsEyeExtraTurn(ref CombatHotState s, HotRules rules)
    {
        if (!rules.HasRelic(HotRelicId.PaelsEye))
            return false;

        HotActiveRelic[] active = rules.ActiveRelics;
        for (int i = 0; i < active.Length; i++)
        {
            HotActiveRelic ar = active[i];
            if (ar.RelicDefId >= rules.Relics.Length || rules.Relics[ar.RelicDefId].RelicId != HotRelicId.PaelsEye)
                continue;
            if (ar.StateSlot >= s.RelicStateCount)
                continue;

            ref HotRelicState rs = ref s.RelicStates[ar.StateSlot];
            if (rs.Current == 0
                && (rs.PreviousOrAux & PaelsEyeWasPartBit) != 0
                && !AnyManualCardPlayedForPaelsEye(in s, rules, in rs))
            {
                rs.Current = 1;
                rs.PreviousOrAux &= ~PaelsEyeManualPlayedBit;
                return true;
            }
        }

        return false;
    }

    private static void HandleShouldTakeExtraTurn(
        ref CombatHotState s,
        HotRules rules,
        HotRelicId relicId,
        byte stateSlot,
        bool hasState,
        ref HotHookArgs args)
    {
        // PaelsEye is evaluated exclusively in Pass 1 via TryConsumePaelsEyeExtraTurn
        // (matching vanilla Hook.ShouldTakeExtraTurnEarly priority over ExtraTurnPower).
    }

    private static void HandleBeforeTurnEnd(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotRelicId relicId,
        byte stateSlot,
        bool hasState,
        ref HotHookArgs args)
    {
        if (args.Side != HotCombatSide.Player)
            return;

        switch (relicId)
        {
            case HotRelicId.Orichalcum:
                // STS2 0.113.1 Orichalcum.cs:44-66: latches Block == 0 in BeforeSideTurnEndVeryEarly
                // (carried via args.BoolFlag before PlatingPower/MetallicizePower run), grants +6 Block here.
                if (args.BoolFlag)
                    HotCombatEngine.GainBlock(ref s, ref cold, rules, scratch, targetSlot: 0, baseAmount: 6, isCardBlock: false);
                break;

            case HotRelicId.FakeOrichalcum:
                // STS2 0.113.1 FakeOrichalcum.cs:46-68: same BeforeSideTurnEndVeryEarly latch, grants +3 Block.
                if (args.BoolFlag)
                    HotCombatEngine.GainBlock(ref s, ref cold, rules, scratch, targetSlot: 0, baseAmount: 3, isCardBlock: false);
                break;

            case HotRelicId.CloakClasp:
                if (s.HandCount > 0)
                    HotCombatEngine.GainBlock(ref s, ref cold, rules, scratch, targetSlot: 0, baseAmount: s.HandCount, isCardBlock: false);
                break;

            case HotRelicId.ObsidianVice:
                if (s.HandCount > 0)
                    HotCombatEngine.DamageRandomEnemy(ref s, ref cold, rules, scratch, dealerSlot: 0, baseDamage: s.HandCount, isPoweredAttack: false);
                break;

            case HotRelicId.ScreamingFlagon:
                if (s.HandCount == 0)
                    HotCombatEngine.DamageAllEnemies(ref s, ref cold, rules, scratch, dealerSlot: 0, baseDamage: 20, isPoweredAttack: false);
                break;

            case HotRelicId.RippleBasin:
                if (s.Counters[(int)HotCounterSlot.AttacksPlayedThisTurn] == 0)
                    HotCombatEngine.GainBlock(ref s, ref cold, rules, scratch, targetSlot: 0, baseAmount: 4, isCardBlock: false);
                break;

            case HotRelicId.StoneCalendar:
                if (s.PlayerTurnNumber == 7)
                    HotCombatEngine.DamageAllEnemies(ref s, ref cold, rules, scratch, dealerSlot: 0, baseDamage: 52, isPoweredAttack: false);
                break;

            case HotRelicId.Storyteller:
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.AimPower, 3);
                break;

            case HotRelicId.PaelsTears:
                if (hasState)
                    s.RelicStates[stateSlot].Current = s.Energy > 0 ? 1 : 0;
                break;

            case HotRelicId.PaelsEye:
                if (hasState)
                {
                    ref HotRelicState rs = ref s.RelicStates[stateSlot];
                    if (rs.Current == 0
                        && (rs.PreviousOrAux & PaelsEyeWasPartBit) != 0
                        && !AnyManualCardPlayedForPaelsEye(in s, rules, in rs))
                    {
                        // STS2 0.113.1 PaelsEye.cs:124-127: snapshot Hand before exhausting so cards
                        // added/drawn mid-loop (e.g. BurningSticks, DarkEmbrace, JossPaper) are NOT exhausted.
                        int snapCount = Math.Min((int)s.HandCount, HotLimits.MaxCards);
                        if (snapCount > 0)
                        {
                            Span<ushort> handUids = stackalloc ushort[HotLimits.MaxCards];
                            for (int i = 0; i < snapCount; i++)
                                handUids[i] = s.Cards[i].CardUid;

                            for (int i = 0; i < snapCount && !HotCombatEngine.IsOverOrEnding(in s) && !scratch.NeedsChoice; i++)
                            {
                                int idx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Hand, handUids[i]);
                                if (idx >= 0)
                                    HotCombatEngine.ExhaustCard(ref s, ref cold, rules, scratch, idx, causedByEthereal: false);
                            }
                        }
                    }
                }
                break;
        }
    }

    private static void HandleAfterTurnEnd(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotRelicId relicId,
        byte stateSlot,
        bool hasState,
        ref HotHookArgs args)
    {
        if (args.Side != HotCombatSide.Player)
            return;

        switch (relicId)
        {
            case HotRelicId.ArtOfWar:
                if (hasState)
                {
                    ref HotRelicState rs = ref s.RelicStates[stateSlot];
                    rs.PreviousOrAux = rs.Current;
                    rs.Current = 0;
                }
                break;

            case HotRelicId.JossPaper:
                if (hasState)
                {
                    ref HotRelicState rs = ref s.RelicStates[stateSlot];
                    if (rs.PreviousOrAux > 0)
                    {
                        rs.Current += rs.PreviousOrAux;
                        rs.PreviousOrAux = 0;
                        if (rs.Current >= 5)
                        {
                            int draws = rs.Current / 5;
                            rs.Current %= 5;
                            HotCombatEngine.DrawCards(ref s, ref cold, rules, scratch, draws, fromHandDraw: false);
                        }
                    }
                }
                break;

            case HotRelicId.Kusarigama:
                if (hasState)
                    s.RelicStates[stateSlot].Current = 0;
                break;

            case HotRelicId.LunarPastry:
                HotCombatEngine.GainStars(ref s, ref cold, rules, scratch, 1);
                break;

            case HotRelicId.ParryingShield:
                if (s.Player.Block >= 10)
                    HotCombatEngine.DamageRandomEnemy(ref s, ref cold, rules, scratch, dealerSlot: 0, baseDamage: 6, isPoweredAttack: false);
                break;
        }
    }

    private static void HandleBeforeSideTurnStart(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotRelicId relicId,
        byte stateSlot,
        bool hasState,
        ref HotHookArgs args)
    {
        if (args.Side != HotCombatSide.Player)
            return;

        switch (relicId)
        {
            // Per-turn resets
            case HotRelicId.BeatingRemnant:
            case HotRelicId.BrilliantScarf:
            case HotRelicId.ConductorsBaton:
            case HotRelicId.DemonTongue:
            case HotRelicId.Kunai:
            case HotRelicId.MiniRegent:
            case HotRelicId.OrnamentalFan:
            case HotRelicId.Regalite:
            case HotRelicId.Shuriken:
                if (hasState)
                    s.RelicStates[stateSlot].Current = 0;
                break;

            case HotRelicId.MusicBox:
            case HotRelicId.RainbowRing:
                if (hasState)
                {
                    s.RelicStates[stateSlot].Current = 0;
                    s.RelicStates[stateSlot].PreviousOrAux = 0;
                }
                break;

            case HotRelicId.Pocketwatch:
                if (hasState)
                {
                    ref HotRelicState rs = ref s.RelicStates[stateSlot];
                    rs.PreviousOrAux = rs.Current;
                    rs.Current = 0;
                }
                break;

            // Turn 1 / combat-start triggers
            case HotRelicId.Anchor:
                if (s.PlayerTurnNumber <= 1)
                    HotCombatEngine.GainBlock(ref s, ref cold, rules, scratch, targetSlot: 0, baseAmount: 10, isCardBlock: false);
                break;

            case HotRelicId.FakeAnchor:
                if (s.PlayerTurnNumber <= 1)
                    HotCombatEngine.GainBlock(ref s, ref cold, rules, scratch, targetSlot: 0, baseAmount: 4, isCardBlock: false);
                break;

            case HotRelicId.BagOfMarbles:
                if (s.PlayerTurnNumber <= 1)
                    HotCombatEngine.ApplyPowerToAllLivingEnemies(ref s, ref cold, rules, scratch, HotPowerId.VulnerablePower, 1);
                break;

            case HotRelicId.RedMask:
                if (s.PlayerTurnNumber <= 1)
                    HotCombatEngine.ApplyPowerToAllLivingEnemies(ref s, ref cold, rules, scratch, HotPowerId.WeakPower, 1);
                break;

            case HotRelicId.TormentousCollar:
                if (s.PlayerTurnNumber <= 1 && rules.IsEliteEncounter)
                    HotCombatEngine.ApplyPowerToAllLivingEnemies(ref s, ref cold, rules, scratch, HotPowerId.WeakPower, 2);
                break;

            case HotRelicId.TwistedFunnel:
                if (s.PlayerTurnNumber <= 1)
                    HotCombatEngine.ApplyPowerToAllLivingEnemies(ref s, ref cold, rules, scratch, HotPowerId.PoisonPower, 4);
                break;

            case HotRelicId.CrackedCore:
                if (s.PlayerTurnNumber <= 1)
                    HotCombatEngine.ChannelOrb(ref s, ref cold, rules, scratch, HotOrbId.Lightning);
                break;

            case HotRelicId.BoundPhylactery:
                if (s.PlayerTurnNumber <= 1)
                    HotRegentNecro.SummonOsty(ref s, ref cold, rules, scratch, 1);
                break;

            case HotRelicId.PhylacteryUnbound:
                if (s.PlayerTurnNumber <= 1)
                    HotRegentNecro.SummonOsty(ref s, ref cold, rules, scratch, 5);
                break;

            case HotRelicId.SneckoEye:
            case HotRelicId.FakeSneckoEye:
                if (s.PlayerTurnNumber <= 1)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.ConfusedPower, 1);
                break;

            case HotRelicId.Pantograph:
                if (s.PlayerTurnNumber <= 1 && rules.IsBossEncounter)
                    HotCombatEngine.HealCreature(ref s, ref cold, rules, scratch, targetSlot: 0, amount: 25);
                break;

            case HotRelicId.BathRobes:
                if (s.PlayerTurnNumber <= 1 && rules.IsBossEncounter && s.Player.CurrentHp < s.Player.MaxHp)
                    HotCombatEngine.HealCreature(ref s, ref cold, rules, scratch, targetSlot: 0, amount: s.Player.MaxHp - s.Player.CurrentHp);
                break;

            case HotRelicId.BeltBuckle:
                if (s.PlayerTurnNumber <= 1 && hasState)
                    SyncBeltBuckle(ref s, ref cold, rules, scratch, stateSlot);
                break;

            case HotRelicId.TeaOfDiscourtesy:
                if (s.PlayerTurnNumber <= 1)
                {
                    ushort dazedId = rules.GetCardDefId(HotCustomCardId.Dazed);
                    if (dazedId != 0)
                    {
                        for (int i = 0; i < 2 && !scratch.NeedsChoice && !HotCombatEngine.IsOverOrEnding(in s); i++)
                            HotCombatEngine.AddGeneratedFixedCard(ref s, ref cold, rules, scratch, dazedId, HotPileKind.Draw, dstOffset: -2, upgraded: false, createdByPlayer: false);
                    }
                }
                break;

            case HotRelicId.PetrifiedToad:
                if (s.PlayerTurnNumber <= 1 && !rules.HasRelic(HotRelicId.Sozu))
                {
                    ushort rockPotionId = rules.GetPotionDefId(HotCustomPotionId.PotionShapedRock);
                    if (rockPotionId != 0)
                    {
                        int slotCount = s.PotionSlotCount > 0 ? Math.Min((int)s.PotionSlotCount, HotLimits.MaxPotions) : HotLimits.MaxPotions;
                        for (int i = 0; i < slotCount; i++)
                        {
                            if (s.Potions[i] == 0)
                            {
                                s.Potions[i] = rockPotionId;
                                s.CombatGeneratedPotionMask |= (ushort)(1 << i);
                                OnPotionProcured(ref s, ref cold, rules, scratch, rockPotionId);
                                break;
                            }
                        }
                    }
                }
                break;

            case HotRelicId.PowerCell:
                if (s.PlayerTurnNumber <= 1)
                    TriggerPowerCell(ref s, rules, cold);
                break;
        }
    }

    private static void HandleAfterSideTurnStart(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotRelicId relicId,
        byte stateSlot,
        bool hasState,
        ref HotHookArgs args)
    {
        if (args.Side != HotCombatSide.Player)
            return;

        switch (relicId)
        {
            case HotRelicId.Akabeko:
                if (s.PlayerTurnNumber <= 1)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.VigorPower, 8);
                break;

            case HotRelicId.BathTowel:
                if (s.PlayerTurnNumber <= 1)
                    HotCombatEngine.HealCreature(ref s, ref cold, rules, scratch, targetSlot: 0, amount: 8);
                break;

            case HotRelicId.BloodVial:
                if (s.PlayerTurnNumber <= 1)
                    HotCombatEngine.HealCreature(ref s, ref cold, rules, scratch, targetSlot: 0, amount: 2);
                break;

            case HotRelicId.FakeBloodVial:
                if (s.PlayerTurnNumber <= 1)
                    HotCombatEngine.HealCreature(ref s, ref cold, rules, scratch, targetSlot: 0, amount: 1);
                break;

            case HotRelicId.Bellows:
                if (s.PlayerTurnNumber <= 1)
                {
                    for (int i = 0; i < s.HandCount; i++)
                        HotCombatEngine.UpgradeCard(ref s, rules, i, ref cold);
                }
                break;

            case HotRelicId.BoneTea:
                if (hasState && s.PlayerTurnNumber <= 1 && s.RelicStates[stateSlot].Current > 0)
                {
                    for (int i = 0; i < s.HandCount; i++)
                        HotCombatEngine.UpgradeCard(ref s, rules, i, ref cold);
                    s.RelicStates[stateSlot].Current--;
                }
                break;

            case HotRelicId.BigHat:
                if (s.PlayerTurnNumber <= 1 && rules.UnlockedCharacterCardPool.Length > 0)
                {
                    Span<ushort> etherealPool = stackalloc ushort[MaxStackPoolCards];
                    int eCount = 0;
                    ushort[] pool = rules.UnlockedCharacterCardPool;
                    for (int i = 0; i < pool.Length && eCount < etherealPool.Length; i++)
                    {
                        ushort cid = pool[i];
                        if (cid != 0 && cid < rules.Cards.Length
                            && (rules.Cards[cid].BaseKeywords & (byte)HotCardKeywordMask.Ethereal) != 0
                            && IsCombatGeneratable(rules.Cards[cid]))
                        {
                            etherealPool[eCount++] = cid;
                        }
                    }
                    if (eCount > 0)
                    {
                        Span<ushort> picked = stackalloc ushort[2];
                        int got = HotPotions.TakeRandomDistinct(etherealPool[..eCount], 2, ref s.Rng[(int)HotRngStream.CombatCardGeneration], picked);
                        for (int i = 0; i < got && !scratch.NeedsChoice && !HotCombatEngine.IsOverOrEnding(in s); i++)
                            HotCombatEngine.AddGeneratedFixedCard(ref s, ref cold, rules, scratch, picked[i], HotPileKind.Hand, dstOffset: -1, upgraded: false, createdByPlayer: true);
                    }
                }
                break;

            case HotRelicId.PhylacteryUnbound:
                HotRegentNecro.SummonOsty(ref s, ref cold, rules, scratch, 2);
                break;

            case HotRelicId.Bread:
                if (s.PlayerTurnNumber == 1)
                    HotCombatEngine.LoseEnergy(ref s, 2);
                break;

            case HotRelicId.Brimstone:
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.StrengthPower, 2);
                if (!scratch.NeedsChoice)
                    HotCombatEngine.ApplyPowerToAllLivingEnemies(ref s, ref cold, rules, scratch, HotPowerId.StrengthPower, 1);
                break;

            case HotRelicId.Candelabra:
                if (s.PlayerTurnNumber == 2)
                    HotCombatEngine.GainEnergy(ref s, rules, 2);
                break;

            case HotRelicId.Chandelier:
                if (s.PlayerTurnNumber == 3)
                    HotCombatEngine.GainEnergy(ref s, rules, 3);
                break;

            case HotRelicId.Lantern:
                if (s.PlayerTurnNumber <= 1)
                    HotCombatEngine.GainEnergy(ref s, rules, 1);
                break;

            case HotRelicId.VeryHotCocoa:
                if (s.PlayerTurnNumber <= 1)
                    HotCombatEngine.GainEnergy(ref s, rules, 4);
                break;

            case HotRelicId.ChoicesParadox:
                if (s.PlayerTurnNumber == 1 && rules.UnlockedCharacterCardPool.Length > 0)
                {
                    Span<ushort> options = stackalloc ushort[5];
                    int rolled = HotPotions.TakeRandomDistinct(rules.UnlockedCharacterCardPool, 5, ref s.Rng[(int)HotRngStream.CombatCardGeneration], options);
                    if (rolled > 0)
                    {
                        HotCombatEngine.SetPendingGeneratedChoiceOptions(options[..rolled]);
                        scratch.BeginChoiceGroup(HotChoiceGroupFlags.None);
                        if (!HotCombatEngine.RequestChoice(ref s, scratch, HotChoiceKind.GeneratedCard, sourceCardDefId: 0, minCount: 1, maxCount: 1, out HotChoiceSelection sel))
                            return;
                        scratch.EndChoiceGroup();
                        if (sel.Kind == HotChoiceKind.GeneratedCard && sel.CardDefId != 0)
                        {
                            int addedIdx = HotCombatEngine.AddGeneratedFixedCard(ref s, ref cold, rules, scratch, sel.CardDefId, HotPileKind.Hand, dstOffset: -1, upgraded: false, createdByPlayer: true);
                            if (addedIdx >= 0)
                                HotCombatEngine.ApplyCardKeywordOutsideUpgrade(ref s.Cards[addedIdx], HotCardKeywordMask.Retain);
                        }
                    }
                }
                break;

            case HotRelicId.Crossbow:
                if (rules.CombatCharacterAttackPool.Length > 0)
                {
                    Span<ushort> picked = stackalloc ushort[1];
                    if (HotPotions.TakeRandomDistinct(rules.CombatCharacterAttackPool, 1, ref s.Rng[(int)HotRngStream.CombatCardGeneration], picked) > 0)
                        HotPotions.AddGeneratedFreeThisTurnToHand(ref s, ref cold, rules, scratch, picked[0]);
                }
                break;

            case HotRelicId.DiamondDiadem:
                if (s.PlayerTurnNumber <= 1)
                {
                    HotCombatEngine.GainBlock(ref s, ref cold, rules, scratch, targetSlot: 0, baseAmount: 20, isCardBlock: false);
                    if (!scratch.NeedsChoice)
                        HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.BlurPower, 1);
                }
                break;

            case HotRelicId.DivineDestiny:
                if (s.PlayerTurnNumber <= 1)
                    HotCombatEngine.GainStars(ref s, ref cold, rules, scratch, 7);
                break;

            case HotRelicId.ExerciseMat:
                if (s.PlayerTurnNumber <= 1)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.StretchPower, 6);
                break;

            case HotRelicId.FakeHappyFlower:
                if (hasState)
                {
                    ref HotRelicState rs = ref s.RelicStates[stateSlot];
                    rs.Current = (rs.Current + 1) % 5;
                    if (rs.Current == 0)
                        HotCombatEngine.GainEnergy(ref s, rules, 1);
                }
                break;

            case HotRelicId.HappyFlower:
                if (hasState)
                {
                    ref HotRelicState rs = ref s.RelicStates[stateSlot];
                    rs.Current = (rs.Current + 1) % 3;
                    if (rs.Current == 0)
                        HotCombatEngine.GainEnergy(ref s, rules, 1);
                }
                break;

            case HotRelicId.FencingManual:
                if (s.PlayerTurnNumber <= 1)
                    HotRegentNecro.Forge(ref s, ref cold, rules, scratch, 10);
                break;

            case HotRelicId.FestivePopper:
                if (s.PlayerTurnNumber == 1)
                    HotCombatEngine.DamageAllEnemies(ref s, ref cold, rules, scratch, dealerSlot: 0, baseDamage: 9, isPoweredAttack: false);
                break;

            case HotRelicId.GamblingChip:
                if (s.PlayerTurnNumber <= 1 && s.HandCount > 0)
                {
                    int maxPicks = Math.Min((int)s.HandCount, HotLimits.MaxChoicesPerAction);
                    Span<ushort> pickedUids = stackalloc ushort[HotLimits.MaxChoicesPerAction];
                    int pickedCount = 0;
                    scratch.BeginChoiceGroup(HotChoiceGroupFlags.Unordered | HotChoiceGroupFlags.Optional | HotChoiceGroupFlags.MultiPick);
                    for (int pick = 0; pick < maxPicks; pick++)
                    {
                        if (pickedCount >= s.HandCount || HotCombatEngine.IsOverOrEnding(in s))
                            break;
                        if (!HotCombatEngine.RequestChoiceExcluding(ref s, scratch, HotChoiceKind.HandCard, sourceCardDefId: 0, minCount: 0, maxCount: 1, pickedUids[..pickedCount], out HotChoiceSelection sel))
                            return;
                        if (sel.Kind == HotChoiceKind.None || sel.CardUidOrIndex == 0)
                            break;
                        pickedUids[pickedCount++] = sel.CardUidOrIndex;
                    }
                    scratch.EndChoiceGroup();
                    if (pickedCount > 0 && !HotCombatEngine.IsOverOrEnding(in s))
                        HotCombatEngine.DiscardCardsAndDraw(ref s, ref cold, rules, scratch, pickedUids[..pickedCount], drawCount: pickedCount);
                }
                break;

            case HotRelicId.HornCleat:
                if (s.PlayerTurnNumber == 2)
                    HotCombatEngine.GainBlock(ref s, ref cold, rules, scratch, targetSlot: 0, baseAmount: 14, isCardBlock: false);
                break;

            case HotRelicId.CaptainsWheel:
                if (s.PlayerTurnNumber == 3)
                    HotCombatEngine.GainBlock(ref s, ref cold, rules, scratch, targetSlot: 0, baseAmount: 18, isCardBlock: false);
                break;

            case HotRelicId.SparklingRouge:
                if (s.PlayerTurnNumber == 3)
                {
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.StrengthPower, 1);
                    if (!scratch.NeedsChoice)
                        HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.DexterityPower, 1);
                }
                break;

            case HotRelicId.InfusedCore:
                if (s.PlayerTurnNumber <= 1)
                {
                    for (int i = 0; i < 3 && !scratch.NeedsChoice && !HotCombatEngine.IsOverOrEnding(in s); i++)
                        HotCombatEngine.ChannelOrb(ref s, ref cold, rules, scratch, HotOrbId.Lightning);
                }
                break;

            case HotRelicId.LetterOpener:
                if (hasState && s.PlayerTurnNumber > 1)
                    s.RelicStates[stateSlot].Current = 0;
                break;

            case HotRelicId.MercuryHourglass:
                HotCombatEngine.DamageAllEnemies(ref s, ref cold, rules, scratch, dealerSlot: 0, baseDamage: 3, isPoweredAttack: false);
                break;

            case HotRelicId.MrStruggles:
                if (s.PlayerTurnNumber > 0)
                    HotCombatEngine.DamageAllEnemies(ref s, ref cold, rules, scratch, dealerSlot: 0, baseDamage: s.PlayerTurnNumber, isPoweredAttack: false);
                break;

            case HotRelicId.OrangeDough:
                if (s.PlayerTurnNumber <= 1 && rules.CombatColorlessPool.Length > 0)
                {
                    Span<ushort> picked = stackalloc ushort[2];
                    int got = HotPotions.TakeRandomDistinct(rules.CombatColorlessPool, 2, ref s.Rng[(int)HotRngStream.CombatCardGeneration], picked);
                    for (int i = 0; i < got && !scratch.NeedsChoice && !HotCombatEngine.IsOverOrEnding(in s); i++)
                        HotCombatEngine.AddGeneratedFixedCard(ref s, ref cold, rules, scratch, picked[i], HotPileKind.Hand, dstOffset: -1, upgraded: false, createdByPlayer: true);
                }
                break;

            case HotRelicId.PaelsEye:
                if (hasState)
                {
                    ref HotRelicState rs = ref s.RelicStates[stateSlot];
                    if (rs.Current == 0)
                        rs.PreviousOrAux = PaelsEyeWasPartBit;
                    else
                        rs.PreviousOrAux &= ~PaelsEyeManualPlayedBit;
                }
                break;

            case HotRelicId.PaelsLegion:
                if (hasState)
                {
                    ref HotRelicState rs = ref s.RelicStates[stateSlot];
                    rs.Current--;
                    rs.PreviousOrAux &= ~1; // TriggeredBlockLastTurn = false
                }
                break;

            case HotRelicId.PaelsTears:
                if (hasState && s.RelicStates[stateSlot].Current != 0)
                    HotCombatEngine.GainEnergy(ref s, rules, 2);
                break;

            case HotRelicId.RoyalPoison:
                if (s.PlayerTurnNumber <= 1)
                    HotCombatEngine.DealDamage(ref s, ref cold, rules, scratch, dealerSlot: 0xFF, targetSlot: 0, baseDamage: 4, isPoweredAttack: false, isUnblockable: true);
                break;

            case HotRelicId.RunicCapacitor:
                if (s.PlayerTurnNumber <= 1)
                    HotCombatEngine.AddOrbSlots(ref s, 3);
                break;

            case HotRelicId.Sai:
                HotCombatEngine.GainBlock(ref s, ref cold, rules, scratch, targetSlot: 0, baseAmount: 7, isCardBlock: false);
                break;

            case HotRelicId.SealOfGold:
                if (s.Gold >= 3)
                {
                    HotCombatEngine.GainEnergy(ref s, rules, 1);
                    s.Gold -= 3;
                }
                break;

            case HotRelicId.SymbioticVirus:
                if (s.PlayerTurnNumber <= 1)
                    HotCombatEngine.ChannelOrb(ref s, ref cold, rules, scratch, HotOrbId.Dark);
                break;

            case HotRelicId.ToastyMittens:
                if (s.HandCount > 0)
                {
                    scratch.BeginChoiceGroup(HotChoiceGroupFlags.None);
                    if (!HotCombatEngine.RequestChoice(ref s, scratch, HotChoiceKind.HandCard, sourceCardDefId: 0, minCount: 1, maxCount: 1, out HotChoiceSelection sel))
                        return;
                    scratch.EndChoiceGroup();
                    int idx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Hand, sel.CardUidOrIndex);
                    if (idx >= 0)
                        HotCombatEngine.ExhaustCard(ref s, ref cold, rules, scratch, idx, causedByEthereal: false);
                    if (scratch.NeedsChoice)
                        return;
                }
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.StrengthPower, 1);
                break;

            case HotRelicId.VexingPuzzlebox:
                if (s.PlayerTurnNumber == 1 && rules.UnlockedCharacterCardPool.Length > 0)
                {
                    Span<ushort> picked = stackalloc ushort[1];
                    if (HotPotions.TakeRandomDistinct(rules.UnlockedCharacterCardPool, 1, ref s.Rng[(int)HotRngStream.CombatCardGeneration], picked) > 0)
                        HotPotions.AddGeneratedFreeThisTurnToHand(ref s, ref cold, rules, scratch, picked[0]);
                }
                break;
        }
    }

    private static void HandleAfterCombatEnd(
        ref CombatHotState s,
        HotRelicId relicId,
        byte stateSlot,
        bool hasState)
    {
        if (!hasState)
            return;

        switch (relicId)
        {
            case HotRelicId.ArtOfWar:
            case HotRelicId.BeltBuckle:
            case HotRelicId.BrilliantScarf:
            case HotRelicId.BurningSticks:
            case HotRelicId.CentennialPuzzle:
            case HotRelicId.EmotionChip:
            case HotRelicId.Kunai:
            case HotRelicId.Kusarigama:
            case HotRelicId.LetterOpener:
            case HotRelicId.Metronome:
            case HotRelicId.MiniRegent:
            case HotRelicId.MusicBox:
            case HotRelicId.OrnamentalFan:
            case HotRelicId.PaelsEye:
            case HotRelicId.PaelsLegion:
            case HotRelicId.PaelsTears:
            case HotRelicId.Pocketwatch:
            case HotRelicId.RainbowRing:
            case HotRelicId.RedSkull:
            case HotRelicId.Regalite:
            case HotRelicId.RuinedHelmet:
            case HotRelicId.Shuriken:
            case HotRelicId.ThrowingAxe:
            case HotRelicId.UnsettlingLamp:
            case HotRelicId.Vambrace:
                s.RelicStates[stateSlot] = default;
                break;

            case HotRelicId.JossPaper:
                // CardsExhausted persists across combats; only transient ethereal queue resets.
                s.RelicStates[stateSlot].PreviousOrAux = 0;
                break;
        }
    }

    // =========================================================================
    // Helper Routines (all zero heap allocation on hot path)
    // =========================================================================

    private static void SyncBeltBuckle(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte stateSlot)
    {
        int slotCount = s.PotionSlotCount > 0 ? Math.Min((int)s.PotionSlotCount, HotLimits.MaxPotions) : HotLimits.MaxPotions;
        bool hasAnyPotion = false;
        for (int i = 0; i < slotCount; i++)
        {
            if (s.Potions[i] != 0)
            {
                hasAnyPotion = true;
                break;
            }
        }

        ref HotRelicState rs = ref s.RelicStates[stateSlot];
        bool dexApplied = rs.Current != 0;
        if (!hasAnyPotion && !dexApplied)
        {
            rs.Current = 1;
            HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.DexterityPower, 2);
        }
        else if (hasAnyPotion && dexApplied)
        {
            rs.Current = 0;
            HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.DexterityPower, -2);
        }
    }

    private static void SyncRedSkull(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte stateSlot)
    {
        bool aboveHalf = (decimal)s.Player.CurrentHp > (decimal)s.Player.MaxHp * 0.5m;
        ref HotRelicState rs = ref s.RelicStates[stateSlot];
        bool applied = rs.Current != 0;

        if (aboveHalf && applied)
        {
            rs.Current = 0;
            HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.StrengthPower, -3);
        }
        else if (!aboveHalf && !applied)
        {
            rs.Current = 1;
            HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.StrengthPower, 3);
        }
    }

    private static void TriggerMummifiedHand(ref CombatHotState s, ref HotColdSidecar? cold, HotRules rules)
    {
        int handCount = s.HandCount;
        if (handCount == 0)
            return;

        // Exact STS2 0.113.1 4-tier priority selection via CombatCardSelection RNG:
        // 1) base > 0 AND current (with global modifiers) > 0
        // 2) current (with global modifiers) > 0
        // 3) base > 0
        // 4) any card in hand
        Span<int> tier1 = stackalloc int[HotLimits.MaxCards];
        Span<int> tier2 = stackalloc int[HotLimits.MaxCards];
        Span<int> tier3 = stackalloc int[HotLimits.MaxCards];
        int c1 = 0, c2 = 0, c3 = 0;

        for (int i = 0; i < handCount; i++)
        {
            ref readonly HotCard c = ref s.Cards[i];
            bool baseCosts = c.BaseEnergyCost > 0 || c.BaseStarCost > 0;
            int effE = (c.Flags & HotCardFlags.CostsX) != 0
                ? 0
                : HotCombatEngine.GetEffectiveEnergyCost(in s, in c, cold, rules, isInPlayablePile: true);
            int effS = (c.Flags & HotCardFlags.HasStarCostX) != 0
                ? 0
                : HotCombatEngine.GetEffectiveStarCost(in s, in c, cold, rules, isInPlayablePile: true);
            bool curCosts = effE > 0 || effS > 0;

            if (baseCosts && curCosts) tier1[c1++] = i;
            if (curCosts) tier2[c2++] = i;
            if (baseCosts) tier3[c3++] = i;
        }

        int chosenIdx;
        ref HotRngState rng = ref s.Rng[(int)HotRngStream.CombatCardSelection];
        if (c1 > 0)
            chosenIdx = tier1[HotRng.NextInt(ref rng, c1)];
        else if (c2 > 0)
            chosenIdx = tier2[HotRng.NextInt(ref rng, c2)];
        else if (c3 > 0)
            chosenIdx = tier3[HotRng.NextInt(ref rng, c3)];
        else
            chosenIdx = HotRng.NextInt(ref rng, handCount);

        HotPotions.SetCardFreeThisTurn(ref s.Cards[chosenIdx], ref cold);
    }

    private static void TriggerBookmark(ref CombatHotState s, ref HotColdSidecar? cold, HotRules rules)
    {
        int handCount = s.HandCount;
        if (handCount == 0)
            return;

        bool retainWholeHand = rules.HasRelic(HotRelicId.RunicPyramid)
            || HotCombatEngine.GetPowerAmountById(in s, rules, 0, HotPowerId.RetainHandPower) > 0
            || HotCombatEngine.GetPowerAmountById(in s, rules, 0, HotPowerId.WellLaidPlansPower) > 0;

        Span<int> candidates = stackalloc int[HotLimits.MaxCards];
        int count = 0;
        for (int i = 0; i < handCount; i++)
        {
            ref readonly HotCard c = ref s.Cards[i];
            bool retained = retainWholeHand
                || (c.Flags & HotCardFlags.SingleTurnRetain) != 0
                || HotCombatEngine.HasEffectiveRetainKeyword(in s, in c, rules);
            if (!retained || (c.Flags & HotCardFlags.CostsX) != 0)
                continue;
            if (HotCostModifiers.GetLocalEnergyCost(in c, cold) > 0)
                candidates[count++] = i;
        }

        if (count == 0)
            return;

        int pick = HotRng.NextInt(ref s.Rng[(int)HotRngStream.CombatCardSelection], count);
        int chosenIdx = candidates[pick];
        HotCombatEngine.CostAddUntilPlayed(ref s.Cards[chosenIdx], ref cold, -1);
    }

    private static void TriggerPowerCell(ref CombatHotState s, HotRules rules, HotColdSidecar? cold)
    {
        int drawStart = s.DrawStart;
        int drawCount = s.DrawCount;

        Span<ushort> nonInnateUids = stackalloc ushort[HotLimits.MaxCards];
        Span<ushort> innateUids = stackalloc ushort[HotLimits.MaxCards];
        int nonInnateCount = 0;
        int innateCount = 0;

        for (int i = drawStart; i < drawStart + drawCount; i++)
        {
            ref readonly HotCard c = ref s.Cards[i];
            if ((c.Flags & HotCardFlags.CostsX) != 0)
                continue;
            if (HotCostModifiers.GetLocalEnergyCost(in c, cold) != 0)
                continue;
            if ((c.LocalKeywordsMask & HotCardKeywordMask.Innate) == 0)
                nonInnateUids[nonInnateCount++] = c.CardUid;
            else
                innateUids[innateCount++] = c.CardUid;
        }

        // STS2 0.113.1 PowerCell.cs:27-29: StableShuffle both nonInnate and innate lists unconditionally.
        if (nonInnateCount > 1)
            HotCombatEngine.StableSortUidsForShuffle(in s, rules, cold, nonInnateUids[..nonInnateCount]);
        HotRng.UnstableShuffle(nonInnateUids[..nonInnateCount], ref s.Rng[(int)HotRngStream.CombatCardSelection]);
        if (innateCount > 1)
            HotCombatEngine.StableSortUidsForShuffle(in s, rules, cold, innateUids[..innateCount]);
        HotRng.UnstableShuffle(innateUids[..innateCount], ref s.Rng[(int)HotRngStream.CombatCardSelection]);

        int moved = 0;
        for (int i = 0; i < nonInnateCount && moved < 2; i++)
        {
            int idx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Draw, nonInnateUids[i]);
            if (idx >= 0)
            {
                HotCombatEngine.MoveCardToHandOrDiscard(ref s, idx);
                moved++;
            }
        }
        for (int i = 0; i < innateCount && moved < 2; i++)
        {
            int idx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Draw, innateUids[i]);
            if (idx >= 0)
            {
                HotCombatEngine.MoveCardToHandOrDiscard(ref s, idx);
                moved++;
            }
        }
    }

    private static HotCard CloneCardWithCostOverflow(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        int srcIdx)
    {
        HotCard clone = s.Cards[srcIdx];
        ushort srcUid = clone.CardUid;
        ushort cloneUid = s.NextCardUid++;
        clone.CardUid = cloneUid;
        clone.Flags &= ~(HotCardFlags.HasDeckVersion | HotCardFlags.RemovedFromState | HotCardFlags.ExhaustOnNextPlay | HotCardFlags.IsDupe | HotCardFlags.FetchPlayedThisTurn);
        clone.Flags |= HotCardFlags.IsClone;
        clone.DampenOrigUpg = 0;

        if ((clone.Flags & HotCardFlags.HasCostOverflow) != 0 && cold?.CostOverflow is { Length: > 0 } overflows)
        {
            for (int i = 0; i < overflows.Length; i++)
            {
                if (overflows[i].CardUid == srcUid)
                {
                    HotCardCostOverflow dup = new(
                        cloneUid,
                        (HotEnergyCostMod[])overflows[i].EnergyMods.Clone(),
                        (HotTempStarCost[])overflows[i].TempStarCosts.Clone());
                    HotCardCostOverflow[] next = new HotCardCostOverflow[overflows.Length + 1];
                    overflows.CopyTo(next, 0);
                    next[overflows.Length] = dup;
                    cold = cold with { CostOverflow = next };
                    break;
                }
            }
        }

        return clone;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool AnyManualCardPlayedForPaelsEye(in CombatHotState s, HotRules rules, in HotRelicState rs)
        => (rs.PreviousOrAux & PaelsEyeManualPlayedBit) != 0
            || (s.PlayerTurnNumber == 1 && rules.HasRelic(HotRelicId.WhisperingEarring));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsInHandOrPlay(in CombatHotState s, int cardIdx)
    {
        if (cardIdx < 0 || cardIdx >= s.TotalCardCount)
            return false;
        HotPileKind pile = HotPiles.PileOfIndex(in s, cardIdx);
        return pile is HotPileKind.Hand or HotPileKind.Play;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort ResolveHookCardUid(in CombatHotState s, in HotHookArgs args)
        => args.CardUid != 0
            ? args.CardUid
            : ((uint)args.CardIdx < (uint)s.TotalCardCount ? s.Cards[args.CardIdx].CardUid : (ushort)0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsCombatGeneratable(HotCardDef def)
        => def.CanBeGeneratedInCombat
            && def.Rarity is not (CardRarity.Basic or CardRarity.Ancient or CardRarity.Event);
}
