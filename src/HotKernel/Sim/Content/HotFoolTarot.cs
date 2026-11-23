using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.FoolArrows;

namespace CombatSolver.HotKernel;

/// <summary>
/// Clean STS2 0.113.1 zero-allocation simulation module for all Fool mechanics
/// (Arrows[32] shoot/impale/dislodge/destroy, Melodies Wah/Cho/Ki, ShowOff, Aim, Encore,
/// Yah, Lullaby, Stretch, Quietus), all Fool custom cards, all 20 Tarot cards,
/// Fool Power hooks, and all 12 Dread Dominions.
/// </summary>
internal static class HotFoolTarot
{
    private const int GrowthIdxShowOff = 10;

    private const ushort ArrowStatusMask = 0x000F;
    private const ushort ArrowDestroyBit = 0x0010;
    private const int ArrowEnemyShift = 5;
    private const ushort ArrowEnemyMask = 0x001F;

    private const byte MelodyWahBit = 1 << 0;
    private const byte MelodyChoBit = 1 << 1;
    private const byte MelodyKiBit = 1 << 2;

    // Transient per-thread arrow attack flag (true while executing a ValueProp.FromArrow attack).
    [ThreadStatic]
    private static bool t_isArrowAttackActive;

    [ThreadStatic]
    private static int t_activeArrowCardDefId;

    [ThreadStatic]
    private static int t_aimStartingAmount;

    public static bool TryExecuteCustomCard(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        ushort cardDefId,
        ref HotCard card,
        int targetEnemySlot,
        out HotStepResult result)
    {
        result = default;
        if (cardDefId == 0 || cardDefId >= rules.Cards.Length)
            return false;

        HotCardDef def = rules.Cards[cardDefId];
        HotCustomCardId cardId = def.CardId;

        HotRules? prevRules = HotPiles.PushActiveRules(rules);
        try
        {
            HotCard cardSnapshot = card;
            return TryExecuteCustomCardBody(ref s, ref cold, rules, scratch, cardDefId, def, cardId, cardSnapshot, targetEnemySlot, out result);
        }
        finally
        {
            HotPiles.PopActiveRules(prevRules);
        }
    }

    private static void ExecutePlayedCardSingleAttack(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        ushort cardUid,
        ushort cardDefId,
        int targetEnemySlot,
        int baseDamage,
        int hits = 1)
    {
        int playIdx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Play, cardUid);
        HotCombatEngine.ExecuteDealerSingleTargetAttack(
            ref s, ref cold, rules, scratch,
            dealerSlot: 0, cardIdx: playIdx, cardDefId: cardDefId,
            targetEnemySlot: targetEnemySlot, baseDamage: baseDamage, hits: hits, powered: true);
    }

    private static void ExecutePlayedCardAllEnemiesAttack(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        ushort cardUid,
        ushort cardDefId,
        int baseDamage,
        int hits = 1)
    {
        int playIdx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Play, cardUid);
        HotCombatEngine.ExecuteDealerAllEnemiesAttack(
            ref s, ref cold, rules, scratch,
            dealerSlot: 0, cardIdx: playIdx, cardDefId: cardDefId,
            baseDamage: baseDamage, hits: hits, powered: true);
    }

    private static void GainPlayedCardBlock(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        ushort cardUid,
        ushort cardDefId,
        int baseBlock)
    {
        int playIdx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Play, cardUid);
        HotCombatEngine.GainBlockInternal(
            ref s, ref cold, rules, scratch,
            targetSlot: 0, baseAmount: baseBlock, isCardBlock: true,
            cardIdx: playIdx, cardDefId: cardDefId);
    }

    private static bool TryExecuteCustomCardBody(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        ushort cardDefId,
        HotCardDef def,
        HotCustomCardId cardId,
        HotCard card,
        int targetEnemySlot,
        out HotStepResult result)
    {
        result = default;

        switch (cardId)
        {
            // =====================================================================
            // FOOL ARROW / SHOT CARDS
            // =====================================================================

            case HotCustomCardId.FoolishShot:
            case HotCustomCardId.QuickShot:
            case HotCustomCardId.SkyShot:
            case HotCustomCardId.DivineShot:
            case HotCustomCardId.HeavyShot:
            case HotCustomCardId.TrickShot:
            case HotCustomCardId.HobblingShot:
            case HotCustomCardId.RapidShot:
            case HotCustomCardId.RepeatShot:
            case HotCustomCardId.MelodicShot:
            case HotCustomCardId.TheShot:
            {
                int hits = cardId switch
                {
                    HotCustomCardId.RapidShot => def.GetHits(card.UpgradeLevel),
                    HotCustomCardId.RepeatShot => ResolveEnergyX(in s, rules, in card),
                    HotCustomCardId.MelodicShot =>
                        HotRegentNecro.GetVarInt(in card, def, "CalculationBase", 1)
                        + HotRegentNecro.GetVarInt(in card, def, "CalculationExtra", 1) * CountMelodies(in s),
                    _ => 1,
                };
                if (hits > 0 && HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int dmg = cardId == HotCustomCardId.TheShot
                        ? HotRegentNecro.GetVarInt(in card, def, "CalculationBase", 0)
                            + HotRegentNecro.GetVarInt(in card, def, "ExtraDamage", HotRegentNecro.GetVarInt(in card, def, "CalculationExtra", 10)) * card.CardPrivate
                            + card.Var0Delta
                        : def.GetDamage(card.UpgradeLevel, card.Var0Delta);

                    int playIdx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Play, card.CardUid);
                    bool shot = ExecuteShootArrowCard(ref s, ref cold, rules, scratch, cardDefId, dmg, hits, targetEnemySlot, playIdx);
                    if (scratch.NeedsChoice)
                        return PauseForChoice(in s, out result);

                    if (shot && cardId == HotCustomCardId.HobblingShot
                        && HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                    {
                        byte targetSlot = (byte)(3 + targetEnemySlot);
                        int pow = HotRegentNecro.GetVarInt(in card, def, "Power", Math.Max(1, def.GetMagic(card.UpgradeLevel)));
                        HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, targetSlot, 0, HotPowerId.WeakPower, pow, isCardSource: true);
                        if (!scratch.NeedsChoice)
                            HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, targetSlot, 0, HotPowerId.VulnerablePower, pow, isCardSource: true);
                    }
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.BlossomShot:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int arrowIdx = ShootArrowRaw(ref s, ref cold, rules, scratch, cardDefId);
                    if (scratch.NeedsChoice)
                        return PauseForChoice(in s, out result);
                    if (arrowIdx >= 0 && HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                    {
                        byte targetSlot = (byte)(3 + targetEnemySlot);
                        int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                        int playIdx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Play, card.CardUid);
                        int hpBefore = s.Enemies[targetEnemySlot].Vital.CurrentHp;
                        int blkBefore = s.Enemies[targetEnemySlot].Vital.Block;
                        ExecuteArrowDamageHits(ref s, ref cold, rules, scratch, cardDefId, targetEnemySlot, dmg, 1, playIdx);
                        if (scratch.NeedsChoice)
                            return PauseForChoice(in s, out result);

                        int dealt = Math.Max(0, (hpBefore - s.Enemies[targetEnemySlot].Vital.CurrentHp)
                            + (blkBefore - s.Enemies[targetEnemySlot].Vital.Block));
                        if (dealt > 0)
                            HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.StretchPower, dealt, isCardSource: true);
                        if (!scratch.NeedsChoice)
                            ImpaleArrow(ref s, ref cold, rules, scratch, targetEnemySlot, arrowIdx, cardDefId);
                    }
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.RainShot:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int arrowIdx = ShootArrowRaw(ref s, ref cold, rules, scratch, cardDefId);
                    if (scratch.NeedsChoice)
                        return PauseForChoice(in s, out result);
                    if (arrowIdx >= 0 && HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                    {
                        int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                        int playIdx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Play, card.CardUid);
                        int hpBefore = s.Enemies[targetEnemySlot].Vital.CurrentHp;
                        int blkBefore = s.Enemies[targetEnemySlot].Vital.Block;
                        ExecuteArrowDamageHits(ref s, ref cold, rules, scratch, cardDefId, targetEnemySlot, dmg, 1, playIdx);
                        if (scratch.NeedsChoice)
                            return PauseForChoice(in s, out result);

                        int spreadDmg = Math.Max(0, (hpBefore - s.Enemies[targetEnemySlot].Vital.CurrentHp)
                            + (blkBefore - s.Enemies[targetEnemySlot].Vital.Block));

                        // Spread damage (unpowered/capped copy) to all other living enemies.
                        if (spreadDmg > 0)
                        {
                            Span<byte> others = stackalloc byte[HotLimits.MaxEnemies];
                            int otherCount = 0;
                            for (int r = 0; r < s.RosterCount; r++)
                            {
                                byte slot = s.ActiveEnemyRoster[r];
                                if (slot != targetEnemySlot && (s.Enemies[slot].Vital.Flags & HotCreatureFlags.Alive) != 0)
                                    others[otherCount++] = slot;
                            }
                            for (int i = 0; i < otherCount && !scratch.NeedsChoice && !HotCombatEngine.IsOverOrEnding(in s); i++)
                            {
                                byte otherSlot = others[i];
                                if ((s.Enemies[otherSlot].Vital.Flags & HotCreatureFlags.Alive) != 0)
                                    HotCombatEngine.DealDamage(ref s, ref cold, rules, scratch, 0, (byte)(3 + otherSlot), spreadDmg, isPoweredAttack: false);
                            }
                        }

                        if (!scratch.NeedsChoice)
                            ImpaleArrow(ref s, ref cold, rules, scratch, targetEnemySlot, arrowIdx, cardDefId);
                    }
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Quiver:
            {
                int repeat = Math.Max(1, HotRegentNecro.GetVarInt(in card, def, "Repeat", def.GetMagic(card.UpgradeLevel)));
                GainArrow(ref s, repeat);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.ArrowStab:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                    ExecutePlayedCardSingleAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, targetEnemySlot, dmg);
                    if (scratch.NeedsChoice)
                        return PauseForChoice(in s, out result);
                }
                int maxHand = s.MaxHandSize > 0 ? s.MaxHandSize : 10;
                if (CountAvailableArrows(in s) > 0 && s.HandCount < maxHand && HasShootCardInPile(in s, rules, HotPileKind.Discard))
                {
                    if (!HotCombatEngine.RequestChoice(ref s, scratch, HotChoiceKind.DiscardPile, cardDefId, 1, 1, out HotChoiceSelection sel))
                        return PauseForChoice(in s, out result);
                    int idx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Discard, sel.CardUidOrIndex);
                    if (idx >= 0)
                        HotPiles.MoveCard(ref s, idx, HotPileKind.Hand, s.HandCount);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.LockedIn:
            {
                if (HasShootCardInPile(in s, rules, HotPileKind.Draw))
                {
                    if (!HotCombatEngine.RequestChoice(ref s, scratch, HotChoiceKind.DrawPile, cardDefId, 1, 1, out HotChoiceSelection sel))
                        return PauseForChoice(in s, out result);
                    int idx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Draw, sel.CardUidOrIndex);
                    if (idx >= 0)
                        MoveCardToHandOrDiscard(ref s, idx);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Recall:
            {
                bool wasImpaled = HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot)
                    && IsEnemyImpaledByPlayer(in s, rules, targetEnemySlot);
                if (wasImpaled)
                {
                    DislodgeAllArrowsFromEnemy(ref s, ref cold, rules, scratch, targetEnemySlot, cardDefId);
                    if (scratch.NeedsChoice)
                        return PauseForChoice(in s, out result);

                    if (s.DiscardCount > 0)
                    {
                        if (!HotCombatEngine.RequestChoice(ref s, scratch, HotChoiceKind.DiscardPile, cardDefId, 1, 1, out HotChoiceSelection sel))
                            return PauseForChoice(in s, out result);
                        int idx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Discard, sel.CardUidOrIndex);
                        if (idx >= 0)
                            HotPiles.MoveCard(ref s, idx, HotPileKind.Draw, 0);
                    }
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Uproot:
            case HotCustomCardId.Wrest:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot)
                    && IsEnemyImpaledByPlayer(in s, rules, targetEnemySlot))
                {
                    DislodgeAllArrowsFromEnemy(ref s, ref cold, rules, scratch, targetEnemySlot, cardDefId);
                    if (scratch.NeedsChoice)
                        return PauseForChoice(in s, out result);

                    if (cardId == HotCustomCardId.Uproot)
                    {
                        int vuln = HotRegentNecro.GetVarInt(in card, def, "VulnerablePower", HotRegentNecro.GetVarInt(in card, def, "Vulnerable", Math.Max(1, def.GetMagic(card.UpgradeLevel))));
                        if (vuln > 0)
                            HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, (byte)(3 + targetEnemySlot), 0, HotPowerId.VulnerablePower, vuln, isCardSource: true);
                    }
                    else
                    {
                        int drawCount = HotRegentNecro.GetVarInt(in card, def, "Cards", Math.Max(1, def.GetMagic(card.UpgradeLevel)));
                        if (drawCount > 0)
                            HotCombatEngine.DrawCards(ref s, ref cold, rules, scratch, drawCount, fromHandDraw: false);
                    }
                }
                return Complete(in s, scratch, out result);
            }

            // =====================================================================
            // FOOL MELODY & GENERAL CARDS
            // =====================================================================

            case HotCustomCardId.Anguish:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int calcBase = HotRegentNecro.GetVarInt(in card, def, "CalculationBase", def.GetDamage(card.UpgradeLevel, card.Var0Delta));
                    int extra = HotRegentNecro.GetVarInt(in card, def, "ExtraDamage", HotRegentNecro.GetVarInt(in card, def, "CalculationExtra", 6));
                    int sadnessCount = s.Counters[(int)HotCounterSlot.SadnessGeneratedBefore];
                    int dmg = calcBase + extra * sadnessCount + card.Var0Delta;
                    ExecutePlayedCardSingleAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, targetEnemySlot, dmg);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.BlotOutTheSky:
            {
                ReadOnlySpan<ushort> charPool = rules.UnlockedCharacterCardPool;
                Span<ushort> shootCandidates = charPool.Length <= HotLimits.MaxStackAllocElements
                    ? stackalloc ushort[charPool.Length]
                    : new ushort[charPool.Length];
                int shootCount = 0;
                for (int i = 0; i < charPool.Length; i++)
                {
                    ushort id = charPool[i];
                    if (id > 0 && id < rules.Cards.Length && rules.Cards[id].IsShootTag)
                        shootCandidates[shootCount++] = id;
                }
                // 0.113.1 BlotOutTheSky.cs:29 -> CardFactory.GetDistinctForCombat -> TakeRandom(1, rng) ->
                // ListExtensions.UnstableShuffle(rng): unconditionally consumes 1 NextULong even when empty,
                // then shuffles the candidate span and takes index 0.
                HotRng.UnstableShuffle(shootCandidates[..shootCount], ref s.Rng[(int)HotRngStream.CombatCardGeneration]);
                if (shootCount > 0)
                {
                    ushort chosenDefId = shootCandidates[0];
                    HotCard created = CreateFreshCard(ref s, rules, chosenDefId, upgraded: false);
                    HotCombatEngine.SetCardToFreeThisTurn(ref created, ref cold);
                    HotCombatEngine.AddGeneratedCardInstance(ref s, ref cold, rules, scratch, in created, HotPileKind.Hand, dstOffset: -1, createdByPlayer: true);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.BowSmack:
            case HotCustomCardId.KiHop:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    bool impaled = IsEnemyImpaledAny(in s, rules, targetEnemySlot);
                    int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                    ExecutePlayedCardSingleAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, targetEnemySlot, dmg);
                    if (scratch.NeedsChoice)
                        return PauseForChoice(in s, out result);

                    if (impaled)
                    {
                        if (cardId == HotCustomCardId.KiHop)
                        {
                            int drawCount = HotRegentNecro.GetVarInt(in card, def, "Cards", Math.Max(1, def.GetMagic(card.UpgradeLevel)));
                            HotCombatEngine.DrawCards(ref s, ref cold, rules, scratch, drawCount, fromHandDraw: false);
                        }
                        else
                        {
                            int energy = HotRegentNecro.GetVarInt(in card, def, "Energy", Math.Max(1, def.GetMagic(card.UpgradeLevel)));
                            HotCombatEngine.GainEnergy(ref s, rules, energy);
                        }
                    }
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Brace:
            {
                int x = ResolveEnergyX(in s, rules, in card);
                int aimAmt = HotRegentNecro.GetVarInt(in card, def, "AimPower", Math.Max(1, def.GetMagic(card.UpgradeLevel)));
                for (int i = 0; i < x && !scratch.NeedsChoice; i++)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.AimPower, aimAmt, isCardSource: true);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.BrightenUp:
            {
                int sadness = HotRegentNecro.GetVarInt(in card, def, "Sadness", 1);
                int joy = HotRegentNecro.GetVarInt(in card, def, "Joy", 1);
                AddFixedCardsToHand(ref s, ref cold, rules, scratch, HotCustomCardId.Sadness, sadness);
                if (!scratch.NeedsChoice)
                    AddFixedCardsToHand(ref s, ref cold, rules, scratch, HotCustomCardId.Joy, joy);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.ChoBarbs:
            {
                int blk = def.GetBlock(card.UpgradeLevel, card.Var1Delta);
                GainPlayedCardBlock(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, blk);
                if (!scratch.NeedsChoice)
                {
                    int barbs = HotRegentNecro.GetVarInt(in card, def, "ChoBarbsPower", Math.Max(1, def.GetMagic(card.UpgradeLevel)));
                    if (barbs > 0)
                    {
                        HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.ThornsPower, barbs, isCardSource: true);
                        if (!scratch.NeedsChoice)
                            HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.ChoBarbsPower, barbs, isCardSource: true);
                    }
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.ChoChorus:
            {
                int blk = def.GetBlock(card.UpgradeLevel, card.Var1Delta);
                GainPlayedCardBlock(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, blk);
                if (scratch.NeedsChoice)
                    return PauseForChoice(in s, out result);

                Span<ushort> melodyUids = stackalloc ushort[HotLimits.MaxCards];
                int count = 0;
                for (int i = 0; i < s.HandCount; i++)
                {
                    ushort id = s.Cards[i].CardDefId;
                    if (id < rules.Cards.Length && (rules.Cards[id].MelodyTagMask & (MelodyWahBit | MelodyKiBit)) != 0)
                        melodyUids[count++] = s.Cards[i].CardUid;
                }
                for (int i = 0; i < count && !scratch.NeedsChoice && !HotCombatEngine.IsOverOrEnding(in s); i++)
                {
                    int idx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Hand, melodyUids[i]);
                    if (idx >= 0)
                        HotCombatEngine.AutoPlayCard(ref s, ref cold, rules, scratch, idx, targetEnemySlot: -1);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.ChoFirm:
            case HotCustomCardId.ChoLimbs:
            {
                int blk = def.GetBlock(card.UpgradeLevel, card.Var1Delta);
                int times = cardId == HotCustomCardId.ChoLimbs || CountAvailableArrows(in s) > 0 ? 2 : 1;
                for (int i = 0; i < times && !scratch.NeedsChoice; i++)
                    GainPlayedCardBlock(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, blk);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.ChoKi:
            {
                int stretch = HotCombatEngine.GetPowerAmountById(in s, rules, 0, HotPowerId.StretchPower);
                int blk = def.GetBlock(card.UpgradeLevel, card.Var1Delta);
                GainPlayedCardBlock(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, blk);
                if (!scratch.NeedsChoice && stretch > 0)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.StretchPower, stretch, isCardSource: true);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.ChoReady:
            {
                int blk = def.GetBlock(card.UpgradeLevel, card.Var1Delta);
                GainPlayedCardBlock(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, blk);
                if (!scratch.NeedsChoice)
                {
                    int aim = HotRegentNecro.GetVarInt(in card, def, "AimPower", Math.Max(1, def.GetMagic(card.UpgradeLevel)));
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.AimPower, aim, isCardSource: true);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.DeepStretch:
            {
                int stretch = HotRegentNecro.GetVarInt(in card, def, "StretchPower", Math.Max(1, def.GetMagic(card.UpgradeLevel)));
                int cards = HotRegentNecro.GetVarInt(in card, def, "Cards", 1);
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.StretchPower, stretch, isCardSource: true);
                if (!scratch.NeedsChoice && cards > 0)
                    HotCombatEngine.DrawCards(ref s, ref cold, rules, scratch, cards, fromHandDraw: false);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Encore:
            {
                int energy = HotRegentNecro.GetVarInt(in card, def, "Energy", 1);
                int cards = HotRegentNecro.GetVarInt(in card, def, "Cards", 1);
                int encore = HotRegentNecro.GetVarInt(in card, def, "EncorePower", 1);
                if (energy > 0)
                    HotCombatEngine.GainEnergy(ref s, rules, energy);
                if (!scratch.NeedsChoice && cards > 0)
                    HotCombatEngine.DrawCards(ref s, ref cold, rules, scratch, cards, fromHandDraw: false);
                if (!scratch.NeedsChoice && encore > 0)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.EncorePower, encore, isCardSource: true);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Enrapture:
            {
                int vigor = HotRegentNecro.GetVarInt(in card, def, "VigorPower", card.UpgradeLevel > 0 ? 9 : 6);
                int stretch = HotRegentNecro.GetVarInt(in card, def, "StretchPower", card.UpgradeLevel > 0 ? 9 : 6);
                if (vigor > 0)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.VigorPower, vigor, isCardSource: true);
                if (!scratch.NeedsChoice && stretch > 0)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.StretchPower, stretch, isCardSource: true);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Florescence:
            {
                Span<ushort> shootUids = stackalloc ushort[HotLimits.MaxCards];
                int shootCount = 0;
                int total = s.TotalCardCount;
                int exhaustStart = s.ExhaustStart;
                int exhaustEnd = exhaustStart + s.ExhaustCount;
                for (int i = 0; i < total; i++)
                {
                    if (i >= exhaustStart && i < exhaustEnd)
                        continue;
                    ushort id = s.Cards[i].CardDefId;
                    if (id < rules.Cards.Length && rules.Cards[id].IsShootTag)
                        shootUids[shootCount++] = s.Cards[i].CardUid;
                }
                for (int i = 0; i < shootCount && !scratch.NeedsChoice; i++)
                {
                    int idx = HotCombatEngine.FindCardIndexByUidAnyPile(in s, shootUids[i]);
                    if (idx >= 0)
                        HotCombatEngine.ExhaustCard(ref s, ref cold, rules, scratch, idx);
                }
                if (!scratch.NeedsChoice)
                {
                    ushort theShotDefId = rules.GetCardDefId(HotCustomCardId.TheShot);
                    if (theShotDefId != 0)
                    {
                        HotCard shot = CreateFreshCard(ref s, rules, theShotDefId, upgraded: card.UpgradeLevel > 0);
                        shot.CardPrivate = shootCount;
                        HotCombatEngine.AddGeneratedCardInstance(ref s, ref cold, rules, scratch, in shot, HotPileKind.Hand, dstOffset: -1, createdByPlayer: true);
                    }
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Growth:
            {
                ushort sadnessId = rules.GetCardDefId(HotCustomCardId.Sadness);
                Span<ushort> sadUids = stackalloc ushort[HotLimits.MaxCards];
                int sadCount = 0;
                for (int i = 0; i < s.HandCount; i++)
                {
                    if (s.Cards[i].CardDefId == sadnessId)
                        sadUids[sadCount++] = s.Cards[i].CardUid;
                }
                for (int i = 0; i < sadCount && !scratch.NeedsChoice; i++)
                {
                    int idx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Hand, sadUids[i]);
                    if (idx >= 0)
                        HotCombatEngine.ExhaustCard(ref s, ref cold, rules, scratch, idx);
                }
                if (!scratch.NeedsChoice && sadCount > 0)
                {
                    int extra = HotRegentNecro.GetVarInt(in card, def, "CalculationExtra", Math.Max(1, def.GetMagic(card.UpgradeLevel)));
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.StretchPower, extra * sadCount, isCardSource: true);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.HarmonicShield:
            {
                int calcBase = HotRegentNecro.GetVarInt(in card, def, "CalculationBase", def.GetBlock(card.UpgradeLevel, card.Var1Delta));
                int calcExtra = HotRegentNecro.GetVarInt(in card, def, "CalculationExtra", card.UpgradeLevel > 0 ? 6 : 4);
                int blk = calcBase + calcExtra * CountMelodies(in s) + card.Var1Delta;
                GainPlayedCardBlock(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, blk);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Jest:
            {
                int blk = def.GetBlock(card.UpgradeLevel, card.Var1Delta);
                GainPlayedCardBlock(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, blk);
                if (!scratch.NeedsChoice)
                {
                    int sad = HotRegentNecro.GetVarInt(in card, def, "Cards", 1);
                    AddFixedCardsToHand(ref s, ref cold, rules, scratch, HotCustomCardId.Sadness, sad);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.KiChant:
            {
                int lookCount = HotRegentNecro.GetVarInt(in card, def, "Cards", 3);
                if (s.DrawCount == 0 && s.DiscardCount > 0)
                {
                    HotPiles.ShuffleDiscardIntoDraw(ref s, rules, scratch, cold);
                    if ((s.ActiveHookMask & (ulong)HotHookMask.AfterShuffle) != 0)
                    {
                        HotHookArgs shArgs = new() { ActorSlot = 0 };
                        HotCombatEngine.DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterShuffle, ref shArgs);
                        if (scratch.NeedsChoice)
                            return PauseForChoice(in s, out result);
                    }
                }
                else if (s.DrawCount < lookCount && s.DiscardCount > 0)
                {
                    // Preserve original top cards on top of Draw after shuffling Discard into Draw (KiChant.cs:45-47).
                    int origCount = s.DrawCount;
                    Span<ushort> origUids = stackalloc ushort[Math.Min(origCount, HotLimits.MaxCards)];
                    for (int i = 0; i < origUids.Length; i++)
                        origUids[i] = s.Cards[s.DrawStart + i].CardUid;
                    HotPiles.ShuffleDiscardIntoDraw(ref s, rules, scratch, cold);
                    if ((s.ActiveHookMask & (ulong)HotHookMask.AfterShuffle) != 0)
                    {
                        HotHookArgs shArgs = new() { ActorSlot = 0 };
                        HotCombatEngine.DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterShuffle, ref shArgs);
                        if (scratch.NeedsChoice)
                            return PauseForChoice(in s, out result);
                    }
                    for (int i = origUids.Length - 1; i >= 0; i--)
                    {
                        int idx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Draw, origUids[i]);
                        if (idx >= 0)
                            HotPiles.MoveCard(ref s, idx, HotPileKind.Draw, 0);
                    }
                }

                int topActual = Math.Min(lookCount, (int)s.DrawCount);
                if (topActual > 0)
                {
                    Span<ushort> topUids = stackalloc ushort[Math.Min(topActual, 16)];
                    for (int i = 0; i < topUids.Length; i++)
                        topUids[i] = s.Cards[s.DrawStart + i].CardUid;

                    if (!HotCombatEngine.RequestChoice(ref s, scratch, HotChoiceKind.DrawPile, cardDefId, 1, (byte)topActual, out HotChoiceSelection sel))
                        return PauseForChoice(in s, out result);

                    int chosenIdx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Draw, sel.CardUidOrIndex);
                    if (chosenIdx >= 0)
                        MoveCardToHandOrDiscard(ref s, chosenIdx);

                    for (int i = 0; i < topUids.Length && !scratch.NeedsChoice; i++)
                    {
                        if (topUids[i] == sel.CardUidOrIndex)
                            continue;
                        int remIdx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Draw, topUids[i]);
                        if (remIdx >= 0)
                            HotCombatEngine.DiscardCard(ref s, ref cold, rules, scratch, remIdx);
                    }
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.KiRest:
            {
                int energy = HotRegentNecro.GetVarInt(in card, def, "Energy", Math.Max(1, def.GetMagic(card.UpgradeLevel)));
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.EnergyNextTurnPower, energy, isCardSource: true);
                if (!scratch.NeedsChoice && (PlayedMelody(in s, 0) || PlayedMelody(in s, 1)))
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.RetainHandPower, 1, isCardSource: true);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.KiVerse:
            {
                int amt = HotRegentNecro.GetVarInt(in card, def, "KiVersePower", Math.Max(1, def.GetMagic(card.UpgradeLevel)));
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.KiVersePower, amt, isCardSource: true);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.KiWhip:
            case HotCustomCardId.WahKi:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int hits = cardId == HotCustomCardId.WahKi ? def.GetHits(card.UpgradeLevel) : 1;
                    int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                    ExecutePlayedCardSingleAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, targetEnemySlot, dmg, hits: hits);
                    if (scratch.NeedsChoice)
                        return PauseForChoice(in s, out result);
                }
                int energy = HotRegentNecro.GetVarInt(in card, def, "Energy", 1);
                if (energy > 0)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.EnergyNextTurnPower, energy, isCardSource: true);
                if (!scratch.NeedsChoice && cardId == HotCustomCardId.WahKi)
                {
                    int drawNext = HotRegentNecro.GetVarInt(in card, def, "Cards", 1);
                    if (drawNext > 0)
                        HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.DrawCardsNextTurnPower, drawNext, isCardSource: true);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.MourningBlow:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                    ExecutePlayedCardSingleAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, targetEnemySlot, dmg, hits: 1);
                    if (scratch.NeedsChoice)
                        return PauseForChoice(in s, out result);
                }
                // Transform self into Sadness after play (0.113.1 MourningBlow.OnPlay -> CardCmd.Transform).
                // Preserve CardUid so CardPlay.cs resolves the Play->Hand move after OnPlay,
                // while clearing HasDeckVersion/enchantment/affliction/cost/keyword deltas.
                if (!HotCombatEngine.IsOverOrEnding(in s))
                {
                    ushort sadnessDefId = rules.GetCardDefId(HotCustomCardId.Sadness);
                    int livePlayIdx = sadnessDefId != 0
                        ? HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Play, card.CardUid)
                        : -1;
                    if (livePlayIdx >= 0)
                    {
                        HotCardDef sadDef = rules.Cards[sadnessDefId];
                        s.Cards[livePlayIdx] = new HotCard
                        {
                            CardDefId = sadnessDefId,
                            CardUid = card.CardUid,
                            UpgradeLevel = 0,
                            BaseEnergyCost = sadDef.BaseCost,
                            BaseStarCost = sadDef.BaseStarCost,
                            LocalKeywordsMask = (HotCardKeywordMask)sadDef.BaseKeywords,
                            Flags = HotCombatEngine.GetInitialCardFlags(sadDef),
                        };
                        s.HistoryCardsGenerated++;
                        s.Counters[(int)HotCounterSlot.SadnessGeneratedBefore]++;
                        if (sadDef.HookMask != HotHookMask.None)
                            s.ActiveHookMask |= (ulong)sadDef.HookMask;
                        if ((s.ActiveHookMask & (ulong)HotHookMask.AfterCardEnteredCombat) != 0)
                        {
                            HotHookArgs enterArgs = new()
                            {
                                ActorSlot = 0,
                                CardIdx = livePlayIdx,
                                CardDefId = sadnessDefId,
                            };
                            HotCombatEngine.DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterCardEnteredCombat, ref enterArgs);
                        }
                    }
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.MythicForm:
            {
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.MythicFormPower, 1, isCardSource: true);
                if (card.UpgradeLevel > 0)
                {
                    ushort mfId = rules.GetPowerDefId(HotPowerId.MythicFormPower);
                    int idx = mfId != 0 ? HotCombatEngine.FindPowerIndex(in s, 0, mfId) : -1;
                    if (idx >= 0)
                        s.Powers[idx].Aux0++;
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Nail:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot) && IsEnemyImpaledAny(in s, rules, targetEnemySlot))
                {
                    byte targetSlot = (byte)(3 + targetEnemySlot);
                    int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                    ExecutePlayedCardSingleAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, targetEnemySlot, dmg, hits: 1);
                    if (scratch.NeedsChoice)
                        return PauseForChoice(in s, out result);

                    int pow = HotRegentNecro.GetVarInt(in card, def, "Power", Math.Max(1, def.GetMagic(card.UpgradeLevel)));
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, targetSlot, 0, HotPowerId.WeakPower, pow, isCardSource: true);
                    if (!scratch.NeedsChoice)
                        HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, targetSlot, 0, HotPowerId.VulnerablePower, pow, isCardSource: true);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.PunchOfJoy:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                    ExecutePlayedCardSingleAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, targetEnemySlot, dmg, hits: 1);
                    if (scratch.NeedsChoice)
                        return PauseForChoice(in s, out result);
                }
                ushort sadnessId = rules.GetCardDefId(HotCustomCardId.Sadness);
                ushort joyId = rules.GetCardDefId(HotCustomCardId.Joy);
                if (sadnessId != 0 && joyId != 0)
                {
                    Span<int> sadIndices = stackalloc int[HotLimits.MaxCards];
                    int sadCount = 0;
                    for (int i = 0; i < s.HandCount; i++)
                    {
                        if (s.Cards[i].CardDefId == sadnessId)
                            sadIndices[sadCount++] = i;
                    }
                    if (sadCount > 0)
                    {
                        HotRng.UnstableShuffle(sadIndices[..sadCount], ref s.Rng[(int)HotRngStream.CombatCardSelection]);
                        int chosenIdx = sadIndices[0];
                        int offset = chosenIdx;
                        HotPiles.RemoveCard(ref s, chosenIdx);
                        HotCombatEngine.AddGeneratedFixedCard(ref s, ref cold, rules, scratch, joyId, HotPileKind.Hand, offset, upgraded: false, createdByPlayer: true);
                    }
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.RhythmStrike:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int hits = HotRegentNecro.GetVarInt(in card, def, "CalculationBase", 1)
                        + HotRegentNecro.GetVarInt(in card, def, "CalculationExtra", 1) * CountMelodies(in s);
                    int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                    ExecutePlayedCardSingleAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, targetEnemySlot, dmg, hits: hits);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Rummage:
            {
                int drawCount = HotRegentNecro.GetVarInt(in card, def, "Cards", Math.Max(1, def.GetMagic(card.UpgradeLevel)));
                if (drawCount > 0)
                    HotCombatEngine.DrawCards(ref s, ref cold, rules, scratch, drawCount, fromHandDraw: false);
                if (!scratch.NeedsChoice)
                    AddFixedCardsToHand(ref s, ref cold, rules, scratch, HotCustomCardId.Sadness, 1);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.SelfDeprecate:
            {
                int sadness = HotRegentNecro.GetVarInt(in card, def, "Sadness", 1);
                AddFixedCardsToHand(ref s, ref cold, rules, scratch, HotCustomCardId.Sadness, sadness);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.ShowOff:
            {
                int currentAim = (short)(card.CardPrivate & 0xFFFF);
                if (currentAim <= 0)
                    currentAim = HotRegentNecro.GetVarInt(in card, def, "AimPower", HotRegentNecro.GetVarInt(in card, def, "BaseAim", Math.Max(1, def.GetMagic(card.UpgradeLevel))));

                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.AimPower, currentAim, isCardSource: true);
                if (!scratch.NeedsChoice && !HotCombatEngine.IsOverOrEnding(in s))
                {
                    ushort showOffPowerId = rules.GetPowerDefId(HotPowerId.ShowOffPower);
                    int growthAmt = HotRegentNecro.GetVarInt(in card, def, "ShowOffPower", 2);
                    if (showOffPowerId != 0)
                        HotCombatEngine.AddPowerInstance(ref s, rules, ownerSlot: 0, applierSlot: 0, powerDefId: showOffPowerId, amount: growthAmt, auxCardUid: card.CardUid);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.SorrowfulStrike:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                    ExecutePlayedCardSingleAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, targetEnemySlot, dmg, hits: 1);
                    if (scratch.NeedsChoice)
                        return PauseForChoice(in s, out result);
                }
                int sad = HotRegentNecro.GetVarInt(in card, def, "Cards", 1);
                AddFixedCardsToHand(ref s, ref cold, rules, scratch, HotCustomCardId.Sadness, sad);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.SteadyCadence:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                    ExecutePlayedCardSingleAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, targetEnemySlot, dmg, hits: 1);
                    if (scratch.NeedsChoice)
                        return PauseForChoice(in s, out result);
                }
                int drawCount = HotRegentNecro.GetVarInt(in card, def, "CalculationBase", 1)
                    + HotRegentNecro.GetVarInt(in card, def, "CalculationExtra", 1) * CountMelodies(in s);
                if (drawCount > 0)
                    HotCombatEngine.DrawCards(ref s, ref cold, rules, scratch, drawCount, fromHandDraw: false);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Timber:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    bool aliveBefore = (s.Enemies[targetEnemySlot].Vital.Flags & HotCreatureFlags.Alive) != 0;
                    int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                    ExecutePlayedCardSingleAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, targetEnemySlot, dmg, hits: 1);
                    if (scratch.NeedsChoice)
                        return PauseForChoice(in s, out result);

                    bool killed = aliveBefore && (s.Enemies[targetEnemySlot].Vital.Flags & HotCreatureFlags.Alive) == 0;
                    if (killed)
                    {
                        int onKill = HotRegentNecro.GetVarInt(in card, def, "OnKillDamage", dmg);
                        ExecutePlayedCardAllEnemiesAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, onKill, hits: 1);
                    }
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Vitriol:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int weak = HotRegentNecro.GetVarInt(in card, def, "WeakPower", HotRegentNecro.GetVarInt(in card, def, "Weak", Math.Max(1, def.GetMagic(card.UpgradeLevel))));
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, (byte)(3 + targetEnemySlot), 0, HotPowerId.WeakPower, weak, isCardSource: true);
                    if (scratch.NeedsChoice)
                        return PauseForChoice(in s, out result);
                }
                AddFixedCardsToHand(ref s, ref cold, rules, scratch, HotCustomCardId.Sadness, 1);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.WahBlast:
            {
                int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                int times = PlayedMelody(in s, 1) || PlayedMelody(in s, 2) ? 2 : 1;
                ExecutePlayedCardAllEnemiesAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, dmg, hits: times);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.WahCho:
            case HotCustomCardId.WahChoKi:
            {
                int blk = def.GetBlock(card.UpgradeLevel, card.Var1Delta);
                GainPlayedCardBlock(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, blk);
                if (scratch.NeedsChoice)
                    return PauseForChoice(in s, out result);

                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                    ExecutePlayedCardSingleAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, targetEnemySlot, dmg);
                    if (scratch.NeedsChoice)
                        return PauseForChoice(in s, out result);
                }
                if (cardId == HotCustomCardId.WahChoKi)
                {
                    int cards = HotRegentNecro.GetVarInt(in card, def, "Cards", 1);
                    if (cards > 0)
                        HotCombatEngine.DrawCards(ref s, ref cold, rules, scratch, cards, fromHandDraw: false);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.WahHammer:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                    if (IsEnemyImpaledAny(in s, rules, targetEnemySlot))
                        dmg *= 2;
                    ExecutePlayedCardSingleAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, targetEnemySlot, dmg);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.WahSweep:
            {
                int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                ExecutePlayedCardAllEnemiesAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, dmg);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.WahSymphony:
            {
                int calcBase = HotRegentNecro.GetVarInt(in card, def, "CalculationBase", def.GetDamage(card.UpgradeLevel, card.Var0Delta));
                int extra = HotRegentNecro.GetVarInt(in card, def, "ExtraDamage", HotRegentNecro.GetVarInt(in card, def, "CalculationExtra", 4));
                int melodyCards = CountMelodyCardsInCombat(in s, rules);
                int dmg = calcBase + extra * melodyCards + card.Var0Delta;
                ExecutePlayedCardAllEnemiesAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, dmg);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.WahTouch:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    byte targetSlot = (byte)(3 + targetEnemySlot);
                    int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                    ExecutePlayedCardSingleAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, targetEnemySlot, dmg);
                    if (scratch.NeedsChoice)
                        return PauseForChoice(in s, out result);
                    int vuln = HotRegentNecro.GetVarInt(in card, def, "VulnerablePower", HotRegentNecro.GetVarInt(in card, def, "Vulnerable", Math.Max(1, def.GetMagic(card.UpgradeLevel))));
                    if (vuln > 0)
                        HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, targetSlot, 0, HotPowerId.VulnerablePower, vuln, isCardSource: true);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Yoga:
            {
                int stretch = HotRegentNecro.GetVarInt(in card, def, "StretchPower", card.UpgradeLevel > 0 ? 8 : 6);
                if (stretch > 0)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.StretchPower, stretch, isCardSource: true);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Joy:
            {
                int energy = HotRegentNecro.GetVarInt(in card, def, "Energy", 1);
                int cards = HotRegentNecro.GetVarInt(in card, def, "Cards", 1);
                if (energy > 0)
                    HotCombatEngine.GainEnergy(ref s, rules, energy);
                if (!scratch.NeedsChoice && cards > 0)
                    HotCombatEngine.DrawCards(ref s, ref cold, rules, scratch, cards, fromHandDraw: false);
                return Complete(in s, scratch, out result);
            }

            // =====================================================================
            // TAROT CARDS (ALL 20 VANILLA TAROT + EVENT TAROT TOKENS)
            // =====================================================================

            case HotCustomCardId.GodByrd:
            {
                int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.GodByrdPower, dmg, isCardSource: true);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Judgement:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int threshold = HotRegentNecro.GetVarInt(in card, def, "Threshold", Math.Max(1, def.GetMagic(card.UpgradeLevel)));
                    if (s.Enemies[targetEnemySlot].Vital.CurrentHp <= threshold)
                        HotCombatEngine.KillCreature(ref s, ref cold, rules, scratch, (byte)(3 + targetEnemySlot));
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.MightyShield:
            {
                int blk = def.GetBlock(card.UpgradeLevel, card.Var1Delta);
                GainPlayedCardBlock(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, blk);
                if (!scratch.NeedsChoice)
                {
                    int dazedCount = HotRegentNecro.GetVarInt(in card, def, "Cards", 2);
                    ushort dazedId = rules.GetCardDefId(HotCustomCardId.Dazed);
                    for (int i = 0; i < dazedCount && !scratch.NeedsChoice; i++)
                        HotCombatEngine.AddGeneratedFixedCard(ref s, ref cold, rules, scratch, dazedId, HotPileKind.Draw, dstOffset: -2);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.MightyWeapon:
            {
                int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                ExecutePlayedCardAllEnemiesAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, dmg);
                if (!scratch.NeedsChoice)
                {
                    int dazedCount = HotRegentNecro.GetVarInt(in card, def, "Cards", 2);
                    ushort dazedId = rules.GetCardDefId(HotCustomCardId.Dazed);
                    for (int i = 0; i < dazedCount && !scratch.NeedsChoice; i++)
                        HotCombatEngine.AddGeneratedFixedCard(ref s, ref cold, rules, scratch, dazedId, HotPileKind.Draw, dstOffset: -2);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.TheDevil:
            {
                int handCount = s.HandCount;
                Span<ushort> handUids = stackalloc ushort[HotLimits.MaxCards];
                for (int i = 0; i < handCount; i++)
                    handUids[i] = s.Cards[i].CardUid;
                for (int i = 0; i < handCount && !scratch.NeedsChoice; i++)
                {
                    int idx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Hand, handUids[i]);
                    if (idx >= 0)
                        HotCombatEngine.ExhaustCard(ref s, ref cold, rules, scratch, idx);
                }
                if (!scratch.NeedsChoice && handCount > 0)
                {
                    if (card.UpgradeLevel > 0)
                    {
                        for (int k = 0; k < handCount && !scratch.NeedsChoice && !HotCombatEngine.IsOverOrEnding(in s); k++)
                        {
                            if (s.DrawCount == 0 && s.DiscardCount > 0)
                                HotPiles.ShuffleDiscardIntoDraw(ref s, rules, scratch, cold);
                            if (s.DrawCount == 0)
                                break;
                            int topIdx = s.DrawStart;
                            HotCombatEngine.UpgradeCard(ref s, rules, topIdx);
                            HotCombatEngine.AutoPlayCard(ref s, ref cold, rules, scratch, topIdx);
                        }
                    }
                    else
                    {
                        HotCombatEngine.AutoPlayFromDrawPile(ref s, ref cold, rules, scratch, handCount);
                    }
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.TheEmpress:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    byte targetSlot = (byte)(3 + targetEnemySlot);
                    int pow = HotRegentNecro.GetVarInt(in card, def, "Power", Math.Max(1, def.GetMagic(card.UpgradeLevel)));
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, targetSlot, 0, HotPowerId.WeakPower, pow, isCardSource: true);
                    if (!scratch.NeedsChoice)
                        HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, targetSlot, 0, HotPowerId.VulnerablePower, pow, isCardSource: true);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.TheFool:
            {
                int count = HotRegentNecro.GetVarInt(in card, def, "Cards", Math.Max(1, def.GetMagic(card.UpgradeLevel)));
                for (int i = 0; i < count && s.HandCount > 0 && !scratch.NeedsChoice && !HotCombatEngine.IsOverOrEnding(in s); i++)
                {
                    int chosenIdx = PickRandomHandCardForAutoPlay(ref s, rules);
                    if (chosenIdx < 0)
                        break;
                    HotCombatEngine.AutoPlayCard(ref s, ref cold, rules, scratch, chosenIdx);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.TheHangedMan:
            {
                int hpLoss = HotRegentNecro.GetVarInt(in card, def, "HpLoss", Math.Max(1, def.GetMagic(card.UpgradeLevel)));
                HotCombatEngine.DealDamage(ref s, ref cold, rules, scratch, 0, 0, hpLoss, isPoweredAttack: false, isUnblockable: true);
                if (!scratch.NeedsChoice && (s.Player.Flags & HotCreatureFlags.Alive) != 0)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.ExtraTurnPower, 1, isCardSource: true);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.TheHermit:
            {
                if (s.HandCount > 0)
                {
                    if (!HotCombatEngine.RequestChoice(ref s, scratch, HotChoiceKind.HandCard, cardDefId, 1, 1, out HotChoiceSelection sel))
                        return PauseForChoice(in s, out result);
                    int idx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Hand, sel.CardUidOrIndex);
                    if (idx >= 0)
                    {
                        ushort chosenDef = s.Cards[idx].CardDefId;
                        if (chosenDef < rules.Cards.Length && rules.Cards[chosenDef].Type == CardType.Curse)
                            HotCombatEngine.ExhaustCard(ref s, ref cold, rules, scratch, idx);
                        else
                            HotCombatEngine.DiscardCard(ref s, ref cold, rules, scratch, idx);
                    }
                    if (scratch.NeedsChoice)
                        return PauseForChoice(in s, out result);
                }
                int drawCount = HotRegentNecro.GetVarInt(in card, def, "Cards", Math.Max(1, def.GetMagic(card.UpgradeLevel)));
                if (drawCount > 0)
                    HotCombatEngine.DrawCards(ref s, ref cold, rules, scratch, drawCount, fromHandDraw: false);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.TheHierophant:
            {
                int count = HotRegentNecro.GetVarInt(in card, def, "Cards", Math.Max(1, def.GetMagic(card.UpgradeLevel)));
                ReadOnlySpan<ushort> pool = rules.CombatColorlessPool.Length > 0 ? rules.CombatColorlessPool : rules.UnlockedColorlessCardPool;
                Span<ushort> rolled = count <= 16 ? stackalloc ushort[count] : new ushort[count];
                int rolledCount = HotRegentNecro.RollDistinctPoolCards(ref s, pool, rolled, HotCustomCardId.None, rules);
                for (int i = 0; i < rolledCount && !scratch.NeedsChoice && !HotCombatEngine.IsOverOrEnding(in s); i++)
                {
                    HotCard generated = CreateFreshCard(ref s, rules, rolled[i], upgraded: false);
                    HotCostModifiers.AddEnergyModifierExact(
                        ref generated,
                        ref cold,
                        amount: -1,
                        isAbsolute: false,
                        expiration: (int)(LocalCostModifierExpiration.EndOfTurn | LocalCostModifierExpiration.WhenPlayed),
                        isReduceOnly: false);
                    HotCombatEngine.AddGeneratedCardInstance(ref s, ref cold, rules, scratch, in generated, HotPileKind.Hand, dstOffset: -1, createdByPlayer: true);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.TheLovers:
            {
                int maxHand = s.MaxHandSize > 0 ? s.MaxHandSize : 10;
                int count = Math.Max(0, maxHand - s.HandCount);
                ReadOnlySpan<ushort> pool = rules.OtherCharacterCardPool.Length > 0
                    ? rules.OtherCharacterCardPool
                    : rules.UnlockedCharacterCardPool;
                if (count > 0 && !pool.IsEmpty)
                {
                    ref HotRngState rng = ref s.Rng[(int)HotRngStream.CombatCardGeneration];
                    for (int i = 0; i < count && !scratch.NeedsChoice && !HotCombatEngine.IsOverOrEnding(in s); i++)
                    {
                        ushort pickDefId = pool[HotRng.NextInt(ref rng, pool.Length)];
                        HotCombatEngine.AddGeneratedFixedCard(ref s, ref cold, rules, scratch, pickDefId, HotPileKind.Hand, dstOffset: -1, upgraded: card.UpgradeLevel > 0, createdByPlayer: true);
                    }
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.TheMagician:
            {
                int repeat = Math.Max(1, HotRegentNecro.GetVarInt(in card, def, "Repeat", def.GetMagic(card.UpgradeLevel)));
                HotCombatEngine.AddOrbSlots(ref s, repeat);
                HotCombatEngine.ChannelRandomOrb(ref s, ref cold, rules, scratch, repeat);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.TheMoon:
            {
                int maxHand = s.MaxHandSize > 0 ? s.MaxHandSize : 10;
                int count = Math.Max(0, maxHand - s.HandCount);
                ushort shivId = rules.GetCardDefId(HotCustomCardId.Shiv);
                for (int i = 0; i < count && !scratch.NeedsChoice && !HotCombatEngine.IsOverOrEnding(in s); i++)
                    HotCombatEngine.AddGeneratedFixedCard(ref s, ref cold, rules, scratch, shivId, HotPileKind.Hand, dstOffset: -1, upgraded: card.UpgradeLevel > 0, createdByPlayer: true);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.TheStar:
            {
                int energy = HotRegentNecro.GetVarInt(in card, def, "Energy", 2);
                int cards = HotRegentNecro.GetVarInt(in card, def, "Cards", 2);
                if (energy > 0)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.EnergyNextTurnPower, energy, isCardSource: true);
                if (!scratch.NeedsChoice && cards > 0)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.DrawCardsNextTurnPower, cards, isCardSource: true);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.TheSun:
            {
                int cards = HotRegentNecro.GetVarInt(in card, def, "Cards", 3);
                int energy = HotRegentNecro.GetVarInt(in card, def, "Energy", 2);
                if (cards > 0)
                    HotCombatEngine.DrawCards(ref s, ref cold, rules, scratch, cards, fromHandDraw: false);
                if (!scratch.NeedsChoice && energy > 0)
                    HotCombatEngine.GainEnergy(ref s, rules, energy);
                if (!scratch.NeedsChoice)
                {
                    ushort sootId = rules.GetCardDefId(HotCustomCardId.Soot);
                    HotCombatEngine.AddGeneratedFixedCard(ref s, ref cold, rules, scratch, sootId, HotPileKind.Discard);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.TheTower:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int calcBase = HotRegentNecro.GetVarInt(in card, def, "CalculationBase", 0);
                    int extra = HotRegentNecro.GetVarInt(in card, def, "ExtraDamage", HotRegentNecro.GetVarInt(in card, def, "CalculationExtra", card.UpgradeLevel > 0 ? 3 : 2));
                    int dmg = calcBase + extra * rules.TotalFloor + card.Var0Delta;
                    ExecutePlayedCardSingleAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, targetEnemySlot, dmg);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.TheWorld:
            {
                int maxHand = s.MaxHandSize > 0 ? s.MaxHandSize : 10;
                int count = Math.Max(0, Math.Min(HotRegentNecro.GetVarInt(in card, def, "Cards", 3), maxHand - s.HandCount));
                int moveCount = Math.Min(count, (int)s.DiscardCount);
                if (moveCount > 0)
                {
                    Span<ushort> discardUids = stackalloc ushort[HotLimits.MaxCards];
                    int dCount = s.DiscardCount;
                    for (int i = 0; i < dCount; i++)
                        discardUids[i] = s.Cards[s.DiscardStart + i].CardUid;
                    HotRng.UnstableShuffle(discardUids[..dCount], ref s.Rng[(int)HotRngStream.CombatCardSelection]);
                    for (int i = 0; i < moveCount; i++)
                    {
                        int idx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Discard, discardUids[i]);
                        if (idx >= 0)
                            MoveCardToHandOrDiscard(ref s, idx);
                    }
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.WheelOfFortune:
            {
                int drawCount = HotRegentNecro.GetVarInt(in card, def, "Cards", 5);
                Span<ushort> handUids = stackalloc ushort[HotLimits.MaxCards];
                int hCount = s.HandCount;
                for (int i = 0; i < hCount; i++)
                    handUids[i] = s.Cards[i].CardUid;
                for (int i = 0; i < hCount && !scratch.NeedsChoice; i++)
                {
                    int idx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Hand, handUids[i]);
                    if (idx >= 0)
                        HotCombatEngine.DiscardCard(ref s, ref cold, rules, scratch, idx);
                }
                if (!scratch.NeedsChoice && drawCount > 0)
                    HotCombatEngine.DrawCards(ref s, ref cold, rules, scratch, drawCount, fromHandDraw: false);
                return Complete(in s, scratch, out result);
            }

            // =====================================================================
            // KNOWLEDGE DEMON / STATUS TOKEN CARDS
            // 0.113.1 Amnesia/Disintegration/MindRot/Sloth/WasteAway implement KnowledgeDemon.IChoosable.OnChosen()
            // ONLY and do NOT override CardModel.OnPlay() — direct PlayCard from hand is a no-op.
            // =====================================================================

            case HotCustomCardId.Amnesia:
            case HotCustomCardId.Disintegration:
            case HotCustomCardId.MindRot:
            case HotCustomCardId.Sloth:
            case HotCustomCardId.WasteAway:
                return Complete(in s, scratch, out result);

            default:
                return false;
        }
    }

    /// <summary>
    /// Dispatches Fool power hooks. Returns <c>true</c> if handled.
    /// </summary>
    public static bool TryDispatchPowerHook(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotHookMask hook,
        int powerIdx,
        ref HotHookArgs args)
    {
        if ((uint)powerIdx >= (uint)s.PowerCount)
            return false;

        ref HotPower power = ref s.Powers[powerIdx];
        if (power.Amount == 0 || power.PowerDefId >= rules.Powers.Length)
            return false;

        HotPowerId powerId = rules.Powers[power.PowerDefId].PowerId;
        byte ownerSlot = power.OwnerSlot;
        int amount = power.Amount;

        switch (powerId)
        {
            case HotPowerId.AimPower:
            {
                if (ownerSlot != 0 || amount <= 0)
                    return true;

                switch (hook)
                {
                    case HotHookMask.BeforeAttack:
                        if (args.ActorSlot == 0 && args.IsPoweredAttack && t_isArrowAttackActive && (power.Flags & HotPowerFlags.BoolAux) == 0)
                        {
                            power.Flags |= HotPowerFlags.BoolAux;
                            t_aimStartingAmount = amount;
                        }
                        return true;

                    case HotHookMask.ModifyDamageAdditive:
                        if (args.ActorSlot == 0 && args.IsPoweredAttack && t_isArrowAttackActive)
                        {
                            decimal bonus = amount;
                            ushort arrowCardId = t_activeArrowCardDefId != 0 ? (ushort)t_activeArrowCardDefId : args.CardDefId;
                            if (arrowCardId > 0 && arrowCardId < rules.Cards.Length
                                && rules.Cards[arrowCardId].CardId == HotCustomCardId.HeavyShot)
                            {
                                bonus *= 2m;
                            }
                            args.DecimalValue += bonus;
                        }
                        return true;

                    case HotHookMask.AfterAttack:
                        if (args.ActorSlot == 0 && (power.Flags & HotPowerFlags.BoolAux) != 0)
                        {
                            power.Flags &= ~HotPowerFlags.BoolAux;
                            int consumed = t_aimStartingAmount > 0 ? t_aimStartingAmount : amount;
                            t_aimStartingAmount = 0;
                            HotCombatEngine.SetPowerAmount(ref s, rules, 0, power.PowerDefId, Math.Max(0, amount - consumed));
                        }
                        return true;

                    default:
                        return true;
                }
            }

            case HotPowerId.ImpaledPower:
            {
                if (ownerSlot < 3)
                    return true;

                if (hook == HotHookMask.AfterDamageReceived
                    && args.TargetSlot == ownerSlot
                    && args.ActorSlot == power.ApplierSlot
                    && args.IsPoweredAttack
                    && !t_isArrowAttackActive
                    && amount > 0)
                {
                    int counter = power.Aux0 <= 0 ? 3 : power.Aux0;
                    counter--;
                    if (counter <= 0)
                    {
                        power.Aux0 = 3;
                        DislodgeFirstArrowFromPower(ref s, ref cold, rules, scratch, powerIdx, sourceCardDefId: args.CardDefId);
                    }
                    else
                    {
                        power.Aux0 = counter;
                    }
                    return true;
                }

                if (hook == HotHookMask.AfterDeath && args.TargetSlot == ownerSlot)
                {
                    DislodgeAllArrowsFromEnemy(ref s, ref cold, rules, scratch, ownerSlot - 3, sourceCardDefId: 0);
                    return true;
                }
                return true;
            }

            case HotPowerId.EncorePower:
            {
                if (ownerSlot != 0 || amount <= 0)
                    return true;

                switch (hook)
                {
                    case HotHookMask.BeforeCardPlayed:
                        if (args.ActorSlot == 0)
                            power.Flags |= HotPowerFlags.BoolAux;
                        return true;

                    case HotHookMask.AfterCardPlayed:
                        if (args.ActorSlot == 0 && args.PlayIndex + 1 >= args.PlayCount && (power.Flags & HotPowerFlags.BoolAux) != 0)
                        {
                            power.Flags &= ~HotPowerFlags.BoolAux;
                            power.Aux0++;
                        }
                        return true;

                    case HotHookMask.AfterTurnEnd:
                        if (args.Side == HotCombatSide.Player)
                            HotCombatEngine.SetPowerAmount(ref s, rules, 0, power.PowerDefId, 0);
                        return true;

                    default:
                        return true;
                }
            }

            case HotPowerId.YahPower:
            {
                if (hook == HotHookMask.BeforeSideTurnStart && ownerSlot == 0 && args.Side == HotCombatSide.Player)
                    power.Flags &= ~HotPowerFlags.BoolAux;
                return true;
            }

            case HotPowerId.LullabyPower:
            {
                if (ownerSlot != 0 || amount <= 0)
                    return true;

                if (hook == HotHookMask.BeforeSideTurnStart && args.Side == HotCombatSide.Player)
                {
                    power.Flags &= ~HotPowerFlags.BoolAux;
                    return true;
                }
                if (hook == HotHookMask.AfterCardPlayed
                    && args.ActorSlot == 0
                    && (power.Flags & HotPowerFlags.BoolAux) == 0
                    && CountMelodies(in s) >= 3)
                {
                    power.Flags |= HotPowerFlags.BoolAux;
                    HotCombatEngine.GainBlock(ref s, ref cold, rules, scratch, 0, amount, isCardBlock: false);
                    return true;
                }
                return true;
            }

            case HotPowerId.ShowOffPower:
            {
                if (ownerSlot != 0 || amount <= 0)
                    return true;

                if (hook == HotHookMask.AfterDeath && args.TargetSlot >= 3)
                {
                    GrowShowOffFromPower(ref s, rules, powerIdx);
                    return true;
                }
                if (hook == HotHookMask.AfterTurnEnd && args.Side == HotCombatSide.Player)
                {
                    HotCombatEngine.RemovePowerAt(ref s, powerIdx);
                    s.ActiveHookMask = HotCombatEngine.RecomputeActiveHookMask(in s, rules);
                    return true;
                }
                return true;
            }

            case HotPowerId.FlankingPower:
            {
                if (hook == HotHookMask.ModifyDamageMultiplicative
                    && args.TargetSlot == ownerSlot
                    && args.IsPoweredAttack
                    && args.ActorSlot != power.ApplierSlot
                    && amount > 0)
                {
                    args.DecimalMultiplier *= 2m;
                    return true;
                }
                if (hook == HotHookMask.AfterTurnEnd && args.Side == HotCombatSide.Player && amount > 0)
                {
                    HotCombatEngine.SetPowerAmount(ref s, rules, ownerSlot, power.PowerDefId, 0);
                    return true;
                }
                return true;
            }

            case HotPowerId.GodByrdPower:
            {
                if (hook == HotHookMask.AfterSideTurnStart
                    && ownerSlot == 0
                    && args.Side == HotCombatSide.Player
                    && amount > 0
                    && power.AmountOnTurnStart != 0)
                {
                    ushort defId = power.PowerDefId;
                    HotCombatEngine.DamageAllEnemies(ref s, ref cold, rules, scratch, 0, amount, isPoweredAttack: false);
                    if (!scratch.NeedsChoice)
                        HotCombatEngine.SetPowerAmount(ref s, rules, 0, defId, 0);
                }
                return true;
            }

            case HotPowerId.StretchPower:
            {
                if (ownerSlot != 0 || amount <= 0)
                    return true;

                switch (hook)
                {
                    case HotHookMask.BeforeCardPlayed:
                    {
                        bool cardGainsBlock = args.CardDefId < rules.Cards.Length && rules.Cards[args.CardDefId].GainsBlock;
                        if (!cardGainsBlock && args.CardIdx >= 0 && args.CardIdx < s.TotalCardCount)
                        {
                            byte enchId = s.Cards[args.CardIdx].EnchId;
                            if (enchId != 0 && enchId < rules.Enchantments.Length
                                && rules.Enchantments[enchId].EnchantmentId == HotEnchantmentId.Adroit)
                            {
                                cardGainsBlock = true;
                            }
                        }
                        if (args.ActorSlot == 0
                            && cardGainsBlock
                            && (power.Flags & HotPowerFlags.BoolAux) == 0)
                        {
                            power.Flags |= HotPowerFlags.BoolAux;
                            power.Aux0 = amount;
                            power.Aux1 = 0;
                        }
                        return true;
                    }

                    case HotHookMask.ModifyBlockAdditive:
                        if (args.TargetSlot == 0 && args.IsCardBlock && (power.Flags & HotPowerFlags.BoolAux) != 0)
                        {
                            args.DecimalValue += amount;
                            power.Aux1 = 1;
                        }
                        return true;

                    case HotHookMask.AfterCardPlayed:
                        if (args.ActorSlot == 0 && (power.Flags & HotPowerFlags.BoolAux) != 0)
                        {
                            int starting = power.Aux0;
                            bool gaveBlock = power.Aux1 != 0;
                            power.Flags &= ~HotPowerFlags.BoolAux;
                            power.Aux0 = 0;
                            power.Aux1 = 0;
                            if (gaveBlock)
                                HotCombatEngine.SetPowerAmount(ref s, rules, 0, power.PowerDefId, Math.Max(0, amount - starting));
                        }
                        return true;

                    default:
                        return true;
                }
            }

            case HotPowerId.QuietusPower:
            {
                if (ownerSlot != 0 || amount <= 0)
                    return true;

                switch (hook)
                {
                    case HotHookMask.BeforeAttack:
                        if (args.ActorSlot == 0 && args.IsPoweredAttack && t_isArrowAttackActive)
                            power.Flags |= HotPowerFlags.BoolAux;
                        return true;

                    case HotHookMask.ModifyDamageMultiplicative:
                        if (args.ActorSlot == 0 && args.IsPoweredAttack && t_isArrowAttackActive)
                            args.DecimalMultiplier *= amount;
                        return true;

                    case HotHookMask.AfterAttack:
                        if (args.ActorSlot == 0 && (power.Flags & HotPowerFlags.BoolAux) != 0)
                        {
                            power.Flags &= ~HotPowerFlags.BoolAux;
                            HotCombatEngine.SetPowerAmount(ref s, rules, 0, power.PowerDefId, 0);
                        }
                        return true;

                    case HotHookMask.AfterTurnEnd:
                        if (args.Side == HotCombatSide.Player)
                            HotCombatEngine.SetPowerAmount(ref s, rules, 0, power.PowerDefId, 0);
                        return true;

                    default:
                        return true;
                }
            }

            case HotPowerId.SerratedArrowsPower:
            {
                if (hook == HotHookMask.ModifyDamageMultiplicative
                    && ownerSlot == 0
                    && args.IsPoweredAttack
                    && (args.ActorSlot == 0 || args.ActorSlot == 1)
                    && args.TargetSlot >= 3
                    && amount > 0
                    && IsEnemyImpaledAny(in s, rules, args.TargetSlot - 3))
                {
                    args.DecimalMultiplier *= 1m + (decimal)amount / 100m;
                }
                return true;
            }

            case HotPowerId.AllIsWellPower:
            {
                if (hook == HotHookMask.AfterCardGenerated
                    && ownerSlot == 0
                    && args.ActorSlot == 0
                    && args.CardDefId < rules.Cards.Length
                    && rules.Cards[args.CardDefId].CardId == HotCustomCardId.Sadness
                    && amount > 0)
                {
                    HotCombatEngine.GainBlock(ref s, ref cold, rules, scratch, 0, amount, isCardBlock: false);
                }
                return true;
            }

            case HotPowerId.ArcheryPower:
            {
                if (hook == HotHookMask.AfterSideTurnStart && ownerSlot == 0 && args.Side == HotCombatSide.Player && amount > 0)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.AimPower, amount, isCardSource: false);
                return true;
            }

            case HotPowerId.EcstasyPower:
            {
                if (hook == HotHookMask.AfterSideTurnStart && ownerSlot == 0 && args.Side == HotCombatSide.Player && amount > 0)
                {
                    ushort sadnessId = rules.GetCardDefId(HotCustomCardId.Sadness);
                    ushort joyId = rules.GetCardDefId(HotCustomCardId.Joy);
                    if (sadnessId != 0 && joyId != 0)
                    {
                        Span<ushort> sadUids = stackalloc ushort[HotLimits.MaxCards];
                        int sadCount = 0;
                        for (int i = 0; i < s.HandCount; i++)
                        {
                            if (s.Cards[i].CardDefId == sadnessId)
                                sadUids[sadCount++] = s.Cards[i].CardUid;
                        }
                        if (sadCount > 0)
                        {
                            HotRng.UnstableShuffle(sadUids[..sadCount], ref s.Rng[(int)HotRngStream.CombatCardSelection]);
                            int toTransform = Math.Min(amount, sadCount);
                            for (int i = 0; i < toTransform && !scratch.NeedsChoice; i++)
                            {
                                int idx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Hand, sadUids[i]);
                                if (idx >= 0)
                                {
                                    HotPiles.RemoveCard(ref s, idx);
                                    HotCombatEngine.AddGeneratedFixedCard(ref s, ref cold, rules, scratch, joyId, HotPileKind.Hand, idx);
                                }
                            }
                        }
                    }
                }
                return true;
            }

            case HotPowerId.EnnuiPower:
            {
                if (ownerSlot != 0 || amount <= 0)
                    return true;

                if (hook == HotHookMask.ModifyHandDraw)
                {
                    args.HandDrawCount += amount;
                    return true;
                }
                if (hook == HotHookMask.BeforeHandDraw)
                {
                    int sadnessCount = power.Aux0 > 0 ? power.Aux0 : 1;
                    AddFixedCardsToHand(ref s, ref cold, rules, scratch, HotCustomCardId.Sadness, sadnessCount);
                    return true;
                }
                return true;
            }

            case HotPowerId.HiddenRootsPower:
            {
                if (hook == HotHookMask.AfterSideTurnStart && ownerSlot == 0 && args.Side == HotCombatSide.Player && amount > 0)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.StretchPower, amount, isCardSource: false);
                return true;
            }

            case HotPowerId.HoningPower:
            {
                if (hook == HotHookMask.ModifyDamageMultiplicative
                    && ownerSlot == 0
                    && args.ActorSlot == 0
                    && args.IsPoweredAttack
                    && t_isArrowAttackActive
                    && amount > 0)
                {
                    args.DecimalMultiplier *= 1m + (decimal)amount / 100m;
                }
                return true;
            }

            case HotPowerId.KiVersePower:
            {
                if (ownerSlot != 0 || amount <= 0)
                    return true;

                // 0.113.1 KiVersePower.cs:19-36: any melody tag (Wah/Cho/Ki), +1 play count, decrements on use.
                if (hook == HotHookMask.ModifyCardPlayCount
                    && args.CardDefId < rules.Cards.Length
                    && rules.Cards[args.CardDefId].MelodyTagMask != 0)
                {
                    args.PlayCount++;
                    HotCombatEngine.SetPowerAmount(ref s, rules, 0, power.PowerDefId, amount - 1);
                    return true;
                }
                if (hook == HotHookMask.AfterTurnEnd && args.Side == HotCombatSide.Player)
                {
                    HotCombatEngine.SetPowerAmount(ref s, rules, 0, power.PowerDefId, 0);
                    return true;
                }
                return true;
            }

            case HotPowerId.LostLutePower:
            case HotPowerId.MotifPower:
            case HotPowerId.SongPathPower:
            case HotPowerId.ViolentChordPower:
            {
                if (hook == HotHookMask.AfterCardPlayed
                    && ownerSlot == 0
                    && args.ActorSlot == 0
                    && args.CardDefId < rules.Cards.Length
                    && amount > 0)
                {
                    byte mask = rules.Cards[args.CardDefId].MelodyTagMask;
                    if (mask != 0)
                    {
                        switch (powerId)
                        {
                            case HotPowerId.LostLutePower:
                            case HotPowerId.MotifPower:
                            {
                                // 0.113.1 LostLutePower.cs:33-53 & MotifPower.cs:33-53:
                                // count Wah/Cho/Ki tags on this card with CardPlaysStarted count == 1 this turn.
                                int firstCount = 0;
                                for (int m = 0; m < 3; m++)
                                {
                                    if ((mask & (1 << m)) != 0 && s.Fool.MelodyStarted[m] == 1)
                                        firstCount++;
                                }
                                if (firstCount > 0)
                                {
                                    int totalAmt = firstCount * amount;
                                    if (powerId == HotPowerId.LostLutePower)
                                        HotCombatEngine.AutoPlayFromDrawPile(ref s, ref cold, rules, scratch, totalAmt, forceExhaust: false);
                                    else
                                        HotCombatEngine.DrawCards(ref s, ref cold, rules, scratch, totalAmt, fromHandDraw: false);
                                }
                                break;
                            }

                            case HotPowerId.SongPathPower:
                            {
                                // 0.113.1 SongPathPower.cs:27-31: Cho only, MelodyCmd.IsFirstPlayThisTurn (identity check).
                                if ((mask & MelodyChoBit) != 0 && IsFirstMelodyPlayIdentity(in s, in args, melodyIdx: 1))
                                    HotCombatEngine.GainBlock(ref s, ref cold, rules, scratch, 0, amount, isCardBlock: false);
                                break;
                            }

                            case HotPowerId.ViolentChordPower:
                            {
                                // 0.113.1 ViolentChordPower.cs:23-27: Wah only, MelodyCmd.IsFirstPlayThisTurn (identity check).
                                if ((mask & MelodyWahBit) != 0 && IsFirstMelodyPlayIdentity(in s, in args, melodyIdx: 0))
                                    HotCombatEngine.DamageAllEnemies(ref s, ref cold, rules, scratch, 0, amount, isPoweredAttack: false);
                                break;
                            }
                        }
                    }
                }
                return true;
            }

            case HotPowerId.MythicFormPower:
            {
                if (hook == HotHookMask.BeforeHandDraw && ownerSlot == 0 && amount > 0)
                {
                    ReadOnlySpan<ushort> tarotPool = rules.TarotCardPool;
                    if (!tarotPool.IsEmpty)
                    {
                        int upgradedCount = power.Aux0;
                        ref HotRngState rng = ref s.Rng[(int)HotRngStream.CombatCardSelection];
                        for (int i = 0; i < amount && !scratch.NeedsChoice && !HotCombatEngine.IsOverOrEnding(in s); i++)
                        {
                            ushort tarotId = tarotPool[HotRng.NextInt(ref rng, tarotPool.Length)];
                            HotCombatEngine.AddGeneratedFixedCard(ref s, ref cold, rules, scratch, tarotId, HotPileKind.Hand, dstOffset: -1, upgraded: i < upgradedCount, createdByPlayer: true);
                        }
                    }
                }
                return true;
            }

            case HotPowerId.TunePower:
            {
                if (hook == HotHookMask.AfterTurnEnd && ownerSlot == 0 && args.Side == HotCombatSide.Player && amount > 0 && PlayedMelody(in s, 2))
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.FreeCardPower, amount, isCardSource: false);
                return true;
            }

            case HotPowerId.DislikeYouPower:
            {
                if (hook == HotHookMask.ModifyDamageMultiplicative && args.ActorSlot == ownerSlot && args.IsPoweredAttack && amount > 0)
                {
                    args.DecimalMultiplier *= Math.Max(0m, 1m - (decimal)amount / 100m);
                    return true;
                }
                if (hook == HotHookMask.AfterTurnEnd && args.Side == HotCombatSide.Player && amount > 0)
                {
                    HotCombatEngine.SetPowerAmount(ref s, rules, ownerSlot, power.PowerDefId, 0);
                    return true;
                }
                return true;
            }

            case HotPowerId.CompostPower:
            {
                if (hook == HotHookMask.AfterDeath && ownerSlot == 0 && args.TargetSlot >= 3 && amount > 0)
                    HotCombatEngine.DrawCards(ref s, ref cold, rules, scratch, amount, fromHandDraw: false);
                return true;
            }

            case HotPowerId.ChoBarbsPower:
            {
                // TemporaryThornsPower: retires at end of opponent's turn (Enemy turn end).
                if (hook == HotHookMask.AfterTurnEnd && ownerSlot == 0 && args.Side == HotCombatSide.Enemy && amount > 0)
                {
                    ushort defId = power.PowerDefId;
                    HotCombatEngine.SetPowerAmount(ref s, rules, 0, defId, 0);
                    int curThorns = HotCombatEngine.GetPowerAmountById(in s, rules, 0, HotPowerId.ThornsPower);
                    if (curThorns > 0)
                    {
                        ushort thornsDefId = rules.GetPowerDefId(HotPowerId.ThornsPower);
                        HotCombatEngine.SetPowerAmount(ref s, rules, 0, thornsDefId, Math.Max(0, curThorns - amount));
                    }
                }
                return true;
            }

            case HotPowerId.SimpletonPower:
            {
                if (hook == HotHookMask.BeforeTurnEnd && ownerSlot == 0 && args.Side == HotCombatSide.Player && amount > 0)
                {
                    Span<ushort> cheapestUids = stackalloc ushort[HotLimits.MaxCards];
                    for (int k = 0; k < amount && !scratch.NeedsChoice && !HotCombatEngine.IsOverOrEnding(in s); k++)
                    {
                        int minCost = int.MaxValue;
                        int cheapestCount = 0;
                        for (int i = 0; i < s.HandCount; i++)
                        {
                            ref readonly HotCard hc = ref s.Cards[i];
                            if ((hc.LocalKeywordsMask & HotCardKeywordMask.Unplayable) != 0)
                                continue;
                            int cCost = (hc.Flags & HotCardFlags.CostsX) != 0
                                ? 999_999_999
                                : HotCombatEngine.GetEffectiveEnergyCost(in s, in hc, cold, rules, isInPlayablePile: true);
                            if (cCost < minCost)
                            {
                                minCost = cCost;
                                cheapestCount = 0;
                                cheapestUids[cheapestCount++] = hc.CardUid;
                            }
                            else if (cCost == minCost)
                            {
                                cheapestUids[cheapestCount++] = hc.CardUid;
                            }
                        }
                        if (cheapestCount == 0)
                            break;
                        ushort pickUid = cheapestUids[HotRng.NextInt(ref s.Rng[(int)HotRngStream.CombatCardSelection], cheapestCount)];
                        int idx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Hand, pickUid);
                        if (idx >= 0)
                            HotCombatEngine.AutoPlayCard(ref s, ref cold, rules, scratch, idx);
                    }
                }
                return true;
            }

            case HotPowerId.AeonglassDominionPower:
            {
                if (hook == HotHookMask.BeforeHandDraw && ownerSlot == 0 && amount > 0)
                {
                    ushort witherId = rules.GetCardDefId(HotCustomCardId.Wither);
                    HotCombatEngine.AddGeneratedFixedCard(ref s, ref cold, rules, scratch, witherId, HotPileKind.Draw, dstOffset: 0, upgraded: false, createdByPlayer: false);
                }
                return true;
            }

            case HotPowerId.InsatiableDominionPower:
            {
                if (ownerSlot != 0 || amount <= 0)
                    return true;

                // InsatiableDominionPower.cs:22,32-55: DisplayAmount = 2 + TurnNumber;
                // deals blockable Unpowered damage at player AfterSideTurnEndLate.
                if (hook == HotHookMask.AfterSideTurnStart && args.Side == HotCombatSide.Player)
                {
                    power.Aux0 = 2 + s.PlayerTurnNumber;
                    return true;
                }
                if (hook == HotHookMask.AfterTurnEnd && args.Side == HotCombatSide.Player)
                {
                    int dmg = 2 + s.PlayerTurnNumber;
                    power.Aux0 = dmg;
                    if (dmg > 0)
                        HotCombatEngine.DealDamage(ref s, ref cold, rules, scratch, 0xFF, 0, dmg, isPoweredAttack: false, isUnblockable: false);
                    return true;
                }
                return true;
            }

            case HotPowerId.LagavulinMatriarchDominionPower:
            {
                if (hook == HotHookMask.AfterTurnEnd && ownerSlot == 0 && args.Side == HotCombatSide.Player && amount > 0)
                {
                    ushort defId = power.PowerDefId;
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0xFF, HotPowerId.StrengthPower, -amount, isCardSource: false);
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0xFF, HotPowerId.DexterityPower, -amount, isCardSource: false);
                    HotCombatEngine.SetPowerAmount(ref s, rules, 0, defId, 0);
                }
                return true;
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// Dispatches all 12 Dread Dominions across combat hooks.
    /// </summary>
    public static void TryDispatchDominionHook(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotHookMask hook,
        ref HotHookArgs args)
    {
        HotDominionId dominion = rules.Dominion;
        if (dominion == HotDominionId.None)
            return;

        switch (dominion)
        {
            case HotDominionId.CeremonialBeastDominion:
            {
                // Newly added enemies gain 1 Ritual (also invoked on spawn via AfterEnemyAddedToCombat).
                break;
            }

            case HotDominionId.TestSubjectDominion:
            case HotDominionId.KaiserCrabDominion:
            case HotDominionId.VantomDominion:
            case HotDominionId.WaterfallGiantDominion:
                // Applied on enemy creation via ApplyDominionOnEnemyAdded.
                break;

            case HotDominionId.KnowledgeDemonDominion:
            {
                // On player turn 1 BeforeHandDraw, KnowledgeDemonDominion offers [Sloth, Amnesia]
                // and executes the chosen card's OnChosen() (SlothPower(3) or AmnesiaPower(1)).
                if (hook == HotHookMask.BeforeHandDraw && s.PlayerTurnNumber <= 1)
                {
                    Span<ushort> options = stackalloc ushort[2];
                    int count = 0;
                    ushort slothId = rules.GetCardDefId(HotCustomCardId.Sloth);
                    ushort amnesiaId = rules.GetCardDefId(HotCustomCardId.Amnesia);
                    if (slothId != 0) options[count++] = slothId;
                    if (amnesiaId != 0) options[count++] = amnesiaId;
                    if (count > 0)
                    {
                        HotCombatEngine.SetPendingGeneratedChoiceOptions(options[..count]);
                        if (HotCombatEngine.RequestChoice(ref s, scratch, HotChoiceKind.GeneratedCard, 0, 1, 1, out HotChoiceSelection sel)
                            && sel.CardDefId != 0)
                        {
                            if (sel.CardDefId == slothId)
                                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.SlothPower, 3, isCardSource: true);
                            else if (sel.CardDefId == amnesiaId)
                                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.AmnesiaPower, 1, isCardSource: true);
                        }
                    }
                }
                break;
            }

            case HotDominionId.AeonglassDominion:
            case HotDominionId.InsatiableDominion:
            case HotDominionId.KinDominion:
            case HotDominionId.LagavulinMatriarchDominion:
            case HotDominionId.QueenDominion:
            case HotDominionId.SoulFyshDominion:
                // Their combat effects are driven by their persistent combat-start powers
                // (AeonglassDominionPower, InsatiableDominionPower, LagavulinMatriarchDominionPower)
                // or combat-start setup already captured into CombatHotState at root.
                break;
        }
    }

    /// <summary>
    /// Applies active Dread Dominion effects whenever a new enemy spawns mid-combat
    /// (called by <see cref="HotMonsters"/> on enemy spawn).
    /// </summary>
    internal static void ApplyDominionOnEnemyAdded(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int enemySlot)
    {
        if (rules.Dominion == HotDominionId.None || !HotCombatEngine.IsValidEnemyTarget(in s, enemySlot))
            return;

        byte creatureSlot = (byte)(3 + enemySlot);
        switch (rules.Dominion)
        {
            case HotDominionId.CeremonialBeastDominion:
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, creatureSlot, 0xFF, HotPowerId.RitualPower, 1, isCardSource: false);
                break;
            case HotDominionId.TestSubjectDominion:
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, creatureSlot, 0xFF, HotPowerId.StrengthPower, 3, isCardSource: false);
                break;
            case HotDominionId.KaiserCrabDominion:
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, creatureSlot, 0xFF, HotPowerId.PlatingPower, 7, isCardSource: false);
                break;
            case HotDominionId.VantomDominion:
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, creatureSlot, 0xFF, HotPowerId.SlipperyPower, 1, isCardSource: false);
                break;
            case HotDominionId.WaterfallGiantDominion:
                if (HotCombatEngine.GetPowerAmountById(in s, rules, creatureSlot, HotPowerId.MinionPower) <= 0)
                {
                    int amount = rules.EncounterSlots.Length > 1 ? 3 : rules.IsEliteEncounter ? 7 : 5;
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, creatureSlot, 0xFF, HotPowerId.ExplosivePower, amount, isCardSource: false);
                }
                break;
        }
    }

    // =========================================================================
    // CROSS-MODULE FOOL ARROW / MELODY / DEATH HELPERS
    // =========================================================================

    internal static void GainArrow(ref CombatHotState s, int count = 1)
    {
        for (int i = 0; i < count && s.ArrowCount < HotLimits.MaxArrows; i++)
        {
            s.Fool.Arrows[s.ArrowCount] = PackArrow(ArrowStatus.Retrieved, destroyAfterImpaled: false, impaledEnemySlot: -1);
            s.ArrowCount++;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int CountAvailableArrows(in CombatHotState s)
    {
        int count = 0;
        int n = s.ArrowCount;
        for (int i = 0; i < n; i++)
        {
            if (GetArrowStatus(s.Fool.Arrows[i]) == ArrowStatus.Retrieved)
                count++;
        }
        return count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int CountMelodies(in CombatHotState s)
    {
        int count = 0;
        if (s.Fool.MelodyFinished[0] > 0) count++;
        if (s.Fool.MelodyFinished[1] > 0) count++;
        if (s.Fool.MelodyFinished[2] > 0) count++;
        return count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool PlayedMelody(in CombatHotState s, int melodyIdx)
        => (uint)melodyIdx < 3 && s.Fool.MelodyFinished[melodyIdx] > 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsFirstMelodyPlayIdentity(in CombatHotState s, in HotHookArgs args, int melodyIdx)
    {
        if ((uint)melodyIdx >= 3 || s.Fool.MelodyStarted[melodyIdx] == 0)
            return false;
        if ((uint)args.CardIdx < (uint)s.TotalCardCount && s.Fool.FirstPlayCardUid[melodyIdx] != 0)
            return s.Fool.FirstPlayCardUid[melodyIdx] == s.Cards[args.CardIdx].CardUid && args.PlayIndex == 0;
        return s.Fool.MelodyStarted[melodyIdx] == 1 && args.PlayIndex == 0;
    }

    internal static bool ShootArrow(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int baseDamage,
        int targetEnemySlot,
        bool isPowered = true,
        bool destroyAfterImpaled = false)
    {
        if (!HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
            return false;

        int arrowIdx = ShootArrowRaw(ref s, ref cold, rules, scratch, sourceCardDefId: 0);
        if (scratch.NeedsChoice || arrowIdx < 0)
            return false;

        if (destroyAfterImpaled)
            s.Fool.Arrows[arrowIdx] |= ArrowDestroyBit;

        if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
        {
            if (isPowered)
                ExecuteArrowDamageHits(ref s, ref cold, rules, scratch, sourceCardDefId: 0, targetEnemySlot, baseDamage, 1);
            else
                HotCombatEngine.DealDamage(ref s, ref cold, rules, scratch, 0, (byte)(3 + targetEnemySlot), baseDamage, isPoweredAttack: false);
            if (scratch.NeedsChoice)
                return false;
            ImpaleArrow(ref s, ref cold, rules, scratch, targetEnemySlot, arrowIdx, sourceCardDefId: 0);
        }
        return true;
    }

    internal static void DislodgeAllArrows(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch)
    {
        for (int e = 0; e < s.EnemyCount && !scratch.NeedsChoice; e++)
            DislodgeAllArrowsFromEnemy(ref s, ref cold, rules, scratch, e, sourceCardDefId: 0);
    }

    /// <summary>
    /// Invoked by <see cref="HotMonsters.HandleEnemyDeathEffect"/> on fatal enemy death:
    /// triggers <c>ShowOffPower</c> permanent growth and dislodges all arrows impaled in the dead enemy.
    /// </summary>
    internal static void OnEnemyFatalDeath(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int enemySlot)
    {
        // 1. Trigger ShowOffPower instances on player.
        ushort showOffPowerId = rules.GetPowerDefId(HotPowerId.ShowOffPower);
        if (showOffPowerId != 0)
        {
            for (int i = 0; i < s.PowerCount; i++)
            {
                if (s.Powers[i].OwnerSlot == 0 && s.Powers[i].PowerDefId == showOffPowerId && s.Powers[i].Amount > 0)
                    GrowShowOffFromPower(ref s, rules, i);
            }
        }

        // 2. Dislodge all arrows impaled on the dead enemy.
        DislodgeAllArrowsFromEnemy(ref s, ref cold, rules, scratch, enemySlot, sourceCardDefId: 0);
    }

    /// <summary>
    /// Retrieves any non-impaled <see cref="ArrowStatus.Away"/> arrows back to <see cref="ArrowStatus.Retrieved"/>.
    /// </summary>
    internal static void RetrieveArrowsForNewTurn(ref CombatHotState s)
    {
        int n = s.ArrowCount;
        for (int i = 0; i < n; i++)
        {
            ushort packed = s.Fool.Arrows[i];
            if (GetArrowStatus(packed) == ArrowStatus.Away && GetArrowImpaledEnemy(packed) < 0)
                s.Fool.Arrows[i] = PackArrow(ArrowStatus.Retrieved, destroyAfterImpaled: false, impaledEnemySlot: -1);
        }
    }

    // =========================================================================
    // ARROW CORE ENGINE
    // =========================================================================

    private static bool ExecuteShootArrowCard(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        ushort cardDefId,
        int baseDamage,
        int hits,
        int targetEnemySlot,
        int cardIdx = -1)
    {
        if (!HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
            return false;

        int arrowIdx = ShootArrowRaw(ref s, ref cold, rules, scratch, cardDefId);
        if (scratch.NeedsChoice || arrowIdx < 0)
            return false;

        if (!HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
            return true;

        ExecuteArrowDamageHits(ref s, ref cold, rules, scratch, cardDefId, targetEnemySlot, baseDamage, hits, cardIdx);
        if (scratch.NeedsChoice)
            return false;

        ImpaleArrow(ref s, ref cold, rules, scratch, targetEnemySlot, arrowIdx, cardDefId);
        return true;
    }

    private static int ShootArrowRaw(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        ushort sourceCardDefId)
    {
        // Matches CombatPredictionSimulator.Arrows: takes LAST available arrow!
        int arrowIdx = -1;
        for (int i = s.ArrowCount - 1; i >= 0; i--)
        {
            if (GetArrowStatus(s.Fool.Arrows[i]) == ArrowStatus.Retrieved)
            {
                arrowIdx = i;
                break;
            }
        }

        if (arrowIdx >= 0)
            s.Fool.Arrows[arrowIdx] = PackArrow(ArrowStatus.Away, destroyAfterImpaled: false, impaledEnemySlot: -1);

        // AfterArrowShot listeners:
        // 1. WhistlingArrows in Discard moves to Hand.
        if (arrowIdx >= 0)
        {
            ushort whistlingId = rules.GetCardDefId(HotCustomCardId.WhistlingArrows);
            if (whistlingId != 0)
            {
                int maxHand = s.MaxHandSize > 0 ? s.MaxHandSize : 10;
                int dStart = s.DiscardStart;
                int dEnd = dStart + s.DiscardCount;
                for (int i = dStart; i < dEnd && s.HandCount < maxHand; i++)
                {
                    if (s.Cards[i].CardDefId == whistlingId)
                    {
                        HotPiles.MoveCard(ref s, i, HotPileKind.Hand, s.HandCount);
                        dStart = s.DiscardStart;
                        dEnd = dStart + s.DiscardCount;
                    }
                }
            }

            // 2. QuietusPower sets DestroyAfterImpaled = true on shot arrow if from a card.
            if (sourceCardDefId != 0 && HotCombatEngine.GetPowerAmountById(in s, rules, 0, HotPowerId.QuietusPower) > 0)
                s.Fool.Arrows[arrowIdx] |= ArrowDestroyBit;
        }

        return arrowIdx;
    }

    private static void ExecuteArrowDamageHits(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        ushort sourceCardDefId,
        int targetEnemySlot,
        int baseDamage,
        int hits,
        int cardIdx = -1)
    {
        if (hits <= 0 || !HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
            return;

        bool prevActive = t_isArrowAttackActive;
        int prevCardDef = t_activeArrowCardDefId;
        t_isArrowAttackActive = true;
        t_activeArrowCardDefId = sourceCardDefId;

        try
        {
            // Snapshot AimPower & QuietusPower before arrow attack; ExecuteDealerSingleTargetAttack invokes
            // BeforeAttack/AfterAttack, enchantment damage modifiers (via cardIdx),
            // ModifyDamageAdditive (AimPower, incl. HeavyShot 2x via t_activeArrowCardDefId),
            // and ModifyDamageMultiplicative (QuietusPower, HoningPower, SerratedArrowsPower).
            ushort aimDefId = rules.GetPowerDefId(HotPowerId.AimPower);
            int aimIdx = aimDefId != 0 ? HotCombatEngine.FindPowerIndex(in s, 0, aimDefId) : -1;
            int aimAmount = aimIdx >= 0 ? s.Powers[aimIdx].Amount : 0;

            ushort quietusDefId = rules.GetPowerDefId(HotPowerId.QuietusPower);
            int quietusAmt = quietusDefId != 0 ? HotCombatEngine.GetPowerAmount(in s, 0, quietusDefId) : 0;

            HotCombatEngine.ExecuteDealerSingleTargetAttack(
                ref s,
                ref cold,
                rules,
                scratch,
                dealerSlot: 0,
                cardIdx: cardIdx,
                cardDefId: sourceCardDefId,
                targetEnemySlot: targetEnemySlot,
                baseDamage: baseDamage,
                hits: hits,
                powered: true);

            // Consume AimPower & QuietusPower after arrow attack completes.
            if (aimAmount > 0 && aimDefId != 0)
            {
                int curAim = HotCombatEngine.GetPowerAmount(in s, 0, aimDefId);
                HotCombatEngine.SetPowerAmount(ref s, rules, 0, aimDefId, Math.Max(0, curAim - aimAmount));
            }
            if (quietusAmt > 0 && quietusDefId != 0)
                HotCombatEngine.SetPowerAmount(ref s, rules, 0, quietusDefId, 0);
        }
        finally
        {
            t_isArrowAttackActive = prevActive;
            t_activeArrowCardDefId = prevCardDef;
        }
    }

    private static void ImpaleArrow(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int targetEnemySlot,
        int arrowIdx,
        ushort sourceCardDefId)
    {
        if ((uint)arrowIdx >= (uint)s.ArrowCount || (uint)targetEnemySlot >= (uint)s.EnemyCount)
            return;

        bool destroyAfter = (s.Fool.Arrows[arrowIdx] & ArrowDestroyBit) != 0;
        bool targetAlive = (s.Enemies[targetEnemySlot].Vital.Flags & HotCreatureFlags.Alive) != 0;
        byte targetSlot = (byte)(3 + targetEnemySlot);

        // Apply/update ImpaledPower if target alive and combat not ending.
        ushort impaledDefId = rules.GetPowerDefId(HotPowerId.ImpaledPower);
        int powerIdx = -1;
        if (targetAlive && !HotCombatEngine.IsOverOrEnding(in s) && impaledDefId != 0)
        {
            powerIdx = FindImpaledPowerByPlayer(in s, targetSlot, impaledDefId);
            if (powerIdx < 0)
            {
                HotCombatEngine.AddPowerInstance(ref s, rules, targetSlot, applierSlot: 0, powerDefId: impaledDefId, amount: 1, aux0: 3, aux1: 0);
                powerIdx = FindImpaledPowerByPlayer(in s, targetSlot, impaledDefId);
            }
            else
            {
                s.Powers[powerIdx].Amount++;
            }

            if (powerIdx >= 0)
                AppendArrowToImpaled(ref s.Powers[powerIdx], arrowIdx);

            s.Fool.Arrows[arrowIdx] = PackArrow(ArrowStatus.Away, destroyAfter, targetEnemySlot);
        }

        // AfterArrowImpaled: ExplosiveArrowsPower deals Unpowered AOE damage!
        int explosiveAmt = HotCombatEngine.GetPowerAmountById(in s, rules, 0, HotPowerId.ExplosiveArrowsPower);
        if (explosiveAmt > 0)
        {
            HotCombatEngine.DamageAllEnemies(ref s, ref cold, rules, scratch, 0, explosiveAmt, isPoweredAttack: false);
            if (scratch.NeedsChoice)
                return;
        }

        // Re-check destroy or dead-target auto-dislodge.
        bool aliveAfter = (s.Enemies[targetEnemySlot].Vital.Flags & HotCreatureFlags.Alive) != 0;
        if (destroyAfter)
        {
            if (impaledDefId != 0)
            {
                int liveIdx = FindImpaledPowerByPlayer(in s, targetSlot, impaledDefId);
                if (liveIdx >= 0)
                {
                    RemoveArrowFromImpaled(ref s, rules, liveIdx, arrowIdx);
                }
            }
            s.Fool.Arrows[arrowIdx] = PackArrow(ArrowStatus.Destroyed, destroyAfterImpaled: true, impaledEnemySlot: -1);
        }
        else if (!aliveAfter)
        {
            DislodgeAllArrowsFromEnemy(ref s, ref cold, rules, scratch, targetEnemySlot, sourceCardDefId);
            if (GetArrowStatus(s.Fool.Arrows[arrowIdx]) != ArrowStatus.Retrieved
                && GetArrowStatus(s.Fool.Arrows[arrowIdx]) != ArrowStatus.Destroyed)
            {
                FireAfterArrowDislodged(ref s, ref cold, rules, scratch, sourceCardDefId);
                s.Fool.Arrows[arrowIdx] = PackArrow(ArrowStatus.Retrieved, destroyAfterImpaled: false, impaledEnemySlot: -1);
            }
        }
    }

    private static void DislodgeAllArrowsFromEnemy(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int enemySlot,
        ushort sourceCardDefId)
    {
        if ((uint)enemySlot >= (uint)s.EnemyCount)
            return;

        byte targetSlot = (byte)(3 + enemySlot);
        ushort impaledDefId = rules.GetPowerDefId(HotPowerId.ImpaledPower);
        if (impaledDefId != 0)
        {
            int guard = 0;
            while (guard++ < HotLimits.MaxArrows)
            {
                int liveIdx = FindImpaledPowerByPlayer(in s, targetSlot, impaledDefId);
                if (liveIdx < 0)
                    break;
                DislodgeFirstArrowFromPower(ref s, ref cold, rules, scratch, liveIdx, sourceCardDefId);
                if (scratch.NeedsChoice)
                    return;
            }
        }

        // Sweep any orphan arrows tagged with this enemySlot.
        for (int a = 0; a < s.ArrowCount; a++)
        {
            ushort packed = s.Fool.Arrows[a];
            if (GetArrowStatus(packed) == ArrowStatus.Away && GetArrowImpaledEnemy(packed) == enemySlot)
            {
                bool destroyAfter = (packed & ArrowDestroyBit) != 0;
                if (destroyAfter)
                {
                    s.Fool.Arrows[a] = PackArrow(ArrowStatus.Destroyed, destroyAfterImpaled: true, impaledEnemySlot: -1);
                }
                else
                {
                    FireAfterArrowDislodged(ref s, ref cold, rules, scratch, sourceCardDefId);
                    s.Fool.Arrows[a] = PackArrow(ArrowStatus.Retrieved, destroyAfterImpaled: false, impaledEnemySlot: -1);
                }
            }
        }
    }

    private static void DislodgeFirstArrowFromPower(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int powerIdx,
        ushort sourceCardDefId)
    {
        if ((uint)powerIdx >= (uint)s.PowerCount)
            return;

        int arrowIdx = PopFirstArrowFromImpaled(ref s.Powers[powerIdx]);
        int remaining = s.Powers[powerIdx].Amount - 1;
        if (remaining <= 0)
        {
            HotCombatEngine.RemovePowerAt(ref s, powerIdx);
            s.ActiveHookMask = HotCombatEngine.RecomputeActiveHookMask(in s, rules);
        }
        else
        {
            s.Powers[powerIdx].Amount = remaining;
        }

        if (arrowIdx < 0 || arrowIdx >= s.ArrowCount)
            return;

        bool destroyAfter = (s.Fool.Arrows[arrowIdx] & ArrowDestroyBit) != 0;
        if (destroyAfter)
        {
            s.Fool.Arrows[arrowIdx] = PackArrow(ArrowStatus.Destroyed, destroyAfterImpaled: true, impaledEnemySlot: -1);
            return;
        }

        FireAfterArrowDislodged(ref s, ref cold, rules, scratch, sourceCardDefId);
        if (scratch.NeedsChoice)
            return;

        s.Fool.Arrows[arrowIdx] = PackArrow(ArrowStatus.Retrieved, destroyAfterImpaled: false, impaledEnemySlot: -1);
    }

    private static void FireAfterArrowDislodged(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        ushort sourceCardDefId)
    {
        // 1. EpicRetrieval source card grants energy when it dislodges an arrow.
        if (sourceCardDefId > 0 && sourceCardDefId < rules.Cards.Length
            && rules.Cards[sourceCardDefId].CardId == HotCustomCardId.EpicRetrieval)
        {
            int energy = Math.Max(1, (int)rules.Cards[sourceCardDefId].BaseMagic);
            HotCombatEngine.GainEnergy(ref s, rules, energy);
        }

        // 2. Twine relic grants block on arrow dislodge.
        HotRelics.OnArrowDislodged(ref s, ref cold, rules, scratch);
        if (scratch.NeedsChoice)
            return;

        // 3. YahPower triggers once per turn on arrow dislodge for Energy.
        ushort yahDefId = rules.GetPowerDefId(HotPowerId.YahPower);
        if (yahDefId != 0)
        {
            int yahIdx = HotCombatEngine.FindPowerIndex(in s, 0, yahDefId);
            if (yahIdx >= 0 && s.Powers[yahIdx].Amount > 0 && (s.Powers[yahIdx].Flags & HotPowerFlags.BoolAux) == 0)
            {
                s.Powers[yahIdx].Flags |= HotPowerFlags.BoolAux;
                HotCombatEngine.GainEnergy(ref s, rules, s.Powers[yahIdx].Amount);
            }
        }
    }

    private static void GrowShowOffFromPower(ref CombatHotState s, HotRules rules, int powerIdx)
    {
        int amt = s.Powers[powerIdx].Amount;
        ushort cardUid = s.Powers[powerIdx].AuxCardUid;
        if (amt <= 0 || cardUid == 0)
            return;

        int idx = HotCombatEngine.FindCardIndexByUidAnyPile(in s, cardUid);
        if (idx < 0)
            return;

        ref HotCard showOffCard = ref s.Cards[idx];
        HotCardDef def = rules.Cards[showOffCard.CardDefId];
        int baseAim = HotRegentNecro.GetVarInt(in showOffCard, def, "BaseAim", Math.Max(1, def.GetMagic(showOffCard.UpgradeLevel)));
        int increasedAim = (short)((showOffCard.CardPrivate >> 16) & 0xFFFF);
        increasedAim += amt;
        int currentAim = baseAim + increasedAim;
        showOffCard.CardPrivate = ((short)currentAim & 0xFFFF) | (((short)increasedAim & 0xFFFF) << 16);

        if ((showOffCard.Flags & HotCardFlags.HasDeckVersion) != 0)
        {
            s.LongTermResourceValue += amt;
            s.Growth[GrowthIdxShowOff]++;
            s.ShowOffRewards += amt;
        }
    }

    // =========================================================================
    // ARROW PACKING & UTILITY HELPERS
    // =========================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort PackArrow(ArrowStatus status, bool destroyAfterImpaled, int impaledEnemySlot)
    {
        int enemyField = impaledEnemySlot >= 0 ? ((impaledEnemySlot + 1) & ArrowEnemyMask) : 0;
        return (ushort)(((int)status & ArrowStatusMask)
            | (destroyAfterImpaled ? ArrowDestroyBit : 0)
            | (enemyField << ArrowEnemyShift));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ArrowStatus GetArrowStatus(ushort packed)
        => (ArrowStatus)(packed & ArrowStatusMask);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetArrowImpaledEnemy(ushort packed)
        => ((packed >> ArrowEnemyShift) & ArrowEnemyMask) - 1;

    private static void AppendArrowToImpaled(ref HotPower power, int arrowIdx)
    {
        int packed = power.Aux1;
        int count = (packed >> 25) & 0x07;
        if (count < 5)
        {
            packed |= (arrowIdx & 0x1F) << (count * 5);
            count++;
            power.Aux1 = (packed & 0x01FF_FFFF) | (count << 25);
        }
    }

    private static int PopFirstArrowFromImpaled(ref HotPower power)
    {
        int packed = power.Aux1;
        int count = (packed >> 25) & 0x07;
        if (count <= 0)
            return -1;
        int first = packed & 0x1F;
        int shifted = (packed & 0x01FF_FFFF) >> 5;
        count--;
        power.Aux1 = (shifted & 0x01FF_FFFF) | (count << 25);
        return first;
    }

    private static void RemoveArrowFromImpaled(ref CombatHotState s, HotRules rules, int powerIdx, int targetArrowIdx)
    {
        int packed = s.Powers[powerIdx].Aux1;
        int count = (packed >> 25) & 0x07;
        int newPacked = 0;
        int newCount = 0;
        bool removed = false;
        for (int i = 0; i < count; i++)
        {
            int idx = (packed >> (i * 5)) & 0x1F;
            if (!removed && idx == targetArrowIdx)
            {
                removed = true;
                continue;
            }
            newPacked |= (idx & 0x1F) << (newCount * 5);
            newCount++;
        }
        s.Powers[powerIdx].Aux1 = newPacked | (newCount << 25);
        int nextAmt = s.Powers[powerIdx].Amount - (removed ? 1 : 0);
        if (nextAmt <= 0)
        {
            HotCombatEngine.RemovePowerAt(ref s, powerIdx);
            s.ActiveHookMask = HotCombatEngine.RecomputeActiveHookMask(in s, rules);
        }
        else
        {
            s.Powers[powerIdx].Amount = nextAmt;
        }
    }

    private static int FindImpaledPowerByPlayer(in CombatHotState s, byte enemyCreatureSlot, ushort impaledDefId)
    {
        for (int i = 0; i < s.PowerCount; i++)
        {
            ref readonly HotPower p = ref s.Powers[i];
            if (p.OwnerSlot == enemyCreatureSlot && p.PowerDefId == impaledDefId && p.ApplierSlot == 0 && p.Amount > 0)
                return i;
        }
        return -1;
    }

    private static bool IsEnemyImpaledByPlayer(in CombatHotState s, HotRules rules, int enemySlot)
    {
        ushort impaledDefId = rules.GetPowerDefId(HotPowerId.ImpaledPower);
        return impaledDefId != 0 && FindImpaledPowerByPlayer(in s, (byte)(3 + enemySlot), impaledDefId) >= 0;
    }

    private static bool IsEnemyImpaledAny(in CombatHotState s, HotRules rules, int enemySlot)
    {
        ushort impaledDefId = rules.GetPowerDefId(HotPowerId.ImpaledPower);
        return impaledDefId != 0 && HotCombatEngine.GetPowerAmount(in s, (byte)(3 + enemySlot), impaledDefId) > 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Complete(in CombatHotState s, HotExecScratch scratch, out HotStepResult result)
    {
        result = new HotStepResult(Executed: true, NeedsChoice: scratch.NeedsChoice, Outcome: s.TerminalOutcome);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool PauseForChoice(in CombatHotState s, out HotStepResult result)
    {
        result = new HotStepResult(Executed: true, NeedsChoice: true, Outcome: s.TerminalOutcome);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ResolveEnergyX(in CombatHotState s, HotRules rules, in HotCard card)
    {
        int x = card.CapturedEnergy;
        if (rules.HasRelic(HotRelicId.ChemicalX))
            x += 2;
        return Math.Max(0, x);
    }

    private static void AddFixedCardsToHand(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotCustomCardId cardId,
        int count)
    {
        if (count <= 0)
            return;
        ushort defId = rules.GetCardDefId(cardId);
        if (defId == 0)
            return;
        for (int i = 0; i < count && !scratch.NeedsChoice && !HotCombatEngine.IsOverOrEnding(in s); i++)
        {
            HotCombatEngine.AddGeneratedFixedCard(ref s, ref cold, rules, scratch, defId, HotPileKind.Hand);
        }
    }

    private static HotCard CreateFreshCard(ref CombatHotState s, HotRules rules, ushort cardDefId, bool upgraded)
    {
        HotCardDef def = rules.Cards[cardDefId];
        byte upg = upgraded && def.MaxUpgradeLevel > 0 ? (byte)1 : (byte)0;
        return new HotCard
        {
            CardDefId = cardDefId,
            CardUid = s.NextCardUid++,
            UpgradeLevel = upg,
            BaseEnergyCost = upg > 0 ? def.UpgradedCost : def.BaseCost,
            BaseStarCost = def.BaseStarCost,
            BaseReplayCount = 0,
            ExtraReplayThisTurn = 0,
            LocalKeywordsMask = (HotCardKeywordMask)def.BaseKeywords,
            KeywordsAddedOutsideMask = HotCardKeywordMask.None,
            KeywordsRemovedOutsideMask = HotCardKeywordMask.None,
            Flags = HotCombatEngine.GetInitialCardFlags(def),
        };
    }

    private static void MoveCardToHandOrDiscard(ref CombatHotState s, int cardIdx)
    {
        int maxHand = s.MaxHandSize > 0 ? s.MaxHandSize : 10;
        if (s.HandCount < maxHand)
            HotPiles.MoveCard(ref s, cardIdx, HotPileKind.Hand, s.HandCount);
        else
            HotPiles.MoveCard(ref s, cardIdx, HotPileKind.Discard, s.DiscardCount);
    }

    private static bool HasShootCardInPile(in CombatHotState s, HotRules rules, HotPileKind pile)
    {
        int start = HotPiles.ZoneStart(in s, pile);
        int end = start + HotPiles.ZoneCount(in s, pile);
        for (int i = start; i < end; i++)
        {
            ushort id = s.Cards[i].CardDefId;
            if (id < rules.Cards.Length && rules.Cards[id].IsShootTag)
                return true;
        }
        return false;
    }

    private static int CountMelodyCardsInCombat(in CombatHotState s, HotRules rules)
    {
        int count = 0;
        int total = s.TotalCardCount;
        for (int i = 0; i < total; i++)
        {
            ushort id = s.Cards[i].CardDefId;
            if (id < rules.Cards.Length && rules.Cards[id].MelodyTagMask != 0)
                count++;
        }
        return count;
    }

    private static int PickRandomHandCardForAutoPlay(ref CombatHotState s, HotRules rules)
    {
        if (s.HandCount == 0)
            return -1;

        Span<int> playable = stackalloc int[HotLimits.MaxCards];
        int playableCount = 0;
        for (int i = 0; i < s.HandCount; i++)
        {
            if ((s.Cards[i].LocalKeywordsMask & HotCardKeywordMask.Unplayable) == 0)
                playable[playableCount++] = i;
        }
        if (playableCount > 0)
        {
            HotRng.UnstableShuffle(playable[..playableCount], ref s.Rng[(int)HotRngStream.CombatCardSelection]);
            return playable[0];
        }

        Span<int> all = stackalloc int[HotLimits.MaxCards];
        int allCount = s.HandCount;
        for (int i = 0; i < allCount; i++)
            all[i] = i;
        HotRng.UnstableShuffle(all[..allCount], ref s.Rng[(int)HotRngStream.CombatCardSelection]);
        return all[0];
    }
}
