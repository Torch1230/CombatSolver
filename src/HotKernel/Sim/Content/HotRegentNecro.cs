using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Powers;

namespace CombatSolver.HotKernel;

/// <summary>
/// Clean STS2 0.113.1 zero-allocation simulation module for all Regent and Necrobinder
/// custom cards, core mechanics (Forge / Sovereign Blade, Osty summon/heal/attack, Doom kill,
/// Souls), and domain-routed Power hooks.
/// </summary>
internal static class HotRegentNecro
{
    private const int GrowthIdxRoyalties = 3;
    private const int GrowthIdxTheScythe = 6;
    private const int GrowthIdxForbiddenGrimoire = 8;

    private const int OrbitEnergyThreshold = 4;
    private const int PaleBlueDotPlayThreshold = 5;

    [ThreadStatic]
    private static bool t_requestPlayerTurnEnd;

    internal static void ClearPendingEndTurnRequest()
        => t_requestPlayerTurnEnd = false;

    internal static bool ConsumePendingEndTurnRequest()
    {
        bool requested = t_requestPlayerTurnEnd;
        t_requestPlayerTurnEnd = false;
        return requested;
    }

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
            // Value snapshot avoids dangling ref HotCard when card execution shifts s.Cards.
            HotCard cardSnapshot = card;
            return TryExecuteCustomCardBody(ref s, ref cold, rules, scratch, cardDefId, def, cardId, cardSnapshot, targetEnemySlot, out result);
        }
        finally
        {
            HotPiles.PopActiveRules(prevRules);
        }
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
            // REGENT CUSTOM CARDS
            // =====================================================================

            case HotCustomCardId.BeatIntoShape:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int hitsBefore = s.Enemies[targetEnemySlot].PoweredHitsFromPlayer;
                    int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                    ExecutePlayedCardSingleAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, targetEnemySlot, dmg, hits: 1);
                    if (scratch.NeedsChoice)
                        return PauseForChoice(in s, out result);

                    int calcBase = GetVarInt(in card, def, "CalculationBase", def.GetMagic(card.UpgradeLevel));
                    int calcExtra = GetVarInt(in card, def, "CalculationExtra", 1);
                    int forge = calcBase + calcExtra * hitsBefore;
                    Forge(ref s, ref cold, rules, scratch, forge);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Begone:
            {
                if (s.HandCount > 0)
                {
                    if (!HotCombatEngine.RequestChoice(ref s, scratch, HotChoiceKind.HandCard, cardDefId, 1, 1, out HotChoiceSelection sel))
                        return PauseForChoice(in s, out result);
                    int idx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Hand, sel.CardUidOrIndex);
                    if (idx >= 0)
                    {
                        ushort minionStrikeId = rules.GetCardDefId(HotCustomCardId.MinionStrike);
                        TransformCardAt(ref s, ref cold, rules, scratch, idx, minionStrikeId, upgraded: card.UpgradeLevel > 0);
                    }
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.BigBang:
            {
                // BigBang.cs:42-45: Draw(1) -> GainStars(1) -> GainEnergy(1) -> Forge(5).
                int cards = GetVarInt(in card, def, "Cards", 1);
                int stars = GetVarInt(in card, def, "Stars", 1);
                int energy = GetVarInt(in card, def, "Energy", 1);
                int forge = GetVarInt(in card, def, "Forge", 5);
                if (cards > 0)
                    HotCombatEngine.DrawCards(ref s, ref cold, rules, scratch, cards, fromHandDraw: false);
                if (!scratch.NeedsChoice && stars > 0)
                    HotCombatEngine.GainStars(ref s, ref cold, rules, scratch, stars);
                if (!scratch.NeedsChoice && energy > 0)
                    HotCombatEngine.GainEnergy(ref s, rules, energy);
                if (!scratch.NeedsChoice && forge > 0)
                    Forge(ref s, ref cold, rules, scratch, forge);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Bombardment:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                    int playIdx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Play, card.CardUid);
                    HotCombatEngine.ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, playIdx, targetEnemySlot, dmg, hits: 1, powered: true);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.BundleOfJoy:
            {
                int count = GetVarInt(in card, def, "Cards", Math.Max(1, def.GetMagic(card.UpgradeLevel)));
                GenerateDistinctCardsFromPool(
                    ref s,
                    ref cold,
                    rules,
                    scratch,
                    GetColorlessPool(rules),
                    count,
                    HotPileKind.Hand,
                    upgraded: false,
                    excludeCardId: HotCustomCardId.None);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Charge:
            {
                int count = GetVarInt(in card, def, "Cards", Math.Max(1, def.GetMagic(card.UpgradeLevel)));
                int picks = Math.Min(count, (int)s.DrawCount);
                if (picks > 0)
                {
                    ushort diveBombId = rules.GetCardDefId(HotCustomCardId.MinionDiveBomb);
                    Span<ushort> chosenUids = stackalloc ushort[Math.Min(picks, HotLimits.MaxChoicesPerAction)];
                    int actualPicks = Math.Min(picks, chosenUids.Length);
                    for (int i = 0; i < actualPicks; i++)
                    {
                        if (!HotCombatEngine.RequestChoice(ref s, scratch, HotChoiceKind.DrawPile, cardDefId, 1, 1, out HotChoiceSelection sel))
                            return PauseForChoice(in s, out result);
                        chosenUids[i] = sel.CardUidOrIndex;
                    }
                    for (int i = 0; i < actualPicks; i++)
                    {
                        int idx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Draw, chosenUids[i]);
                        if (idx >= 0)
                            TransformCardAt(ref s, ref cold, rules, scratch, idx, diveBombId, upgraded: card.UpgradeLevel > 0);
                        if (scratch.NeedsChoice)
                            return PauseForChoice(in s, out result);
                    }
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.CollisionCourse:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                    ExecutePlayedCardSingleAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, targetEnemySlot, dmg, hits: 1);
                }
                if (!scratch.NeedsChoice)
                {
                    ushort debrisId = rules.GetCardDefId(HotCustomCardId.Debris);
                    HotCombatEngine.AddGeneratedFixedCard(ref s, ref cold, rules, scratch, debrisId, HotPileKind.Hand);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Comet:
            case HotCustomCardId.FallingStar:
            case HotCustomCardId.GammaBlast:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    byte targetSlot = (byte)(3 + targetEnemySlot);
                    int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                    ExecutePlayedCardSingleAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, targetEnemySlot, dmg, hits: 1);
                    if (scratch.NeedsChoice)
                        return PauseForChoice(in s, out result);

                    int weak = GetVarInt(in card, def, "WeakPower", GetVarInt(in card, def, "Weak", def.GetMagic(card.UpgradeLevel)));
                    int vuln = GetVarInt(in card, def, "VulnerablePower", GetVarInt(in card, def, "Vulnerable", def.GetMagic(card.UpgradeLevel)));
                    if (weak > 0)
                        HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, targetSlot, 0, HotPowerId.WeakPower, weak, isCardSource: true);
                    if (!scratch.NeedsChoice && vuln > 0)
                        HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, targetSlot, 0, HotPowerId.VulnerablePower, vuln, isCardSource: true);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Constellation:
            {
                int cards = GetVarInt(in card, def, "Cards", 1);
                int energy = GetVarInt(in card, def, "Energy", 1);
                int block = def.GetBlock(card.UpgradeLevel, card.Var1Delta);
                if (cards > 0)
                    HotCombatEngine.DrawCards(ref s, ref cold, rules, scratch, cards, fromHandDraw: false);
                if (!scratch.NeedsChoice && energy > 0)
                    HotCombatEngine.GainEnergy(ref s, rules, energy);
                if (!scratch.NeedsChoice && block > 0)
                    GainPlayedCardBlock(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, block);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Convergence:
            {
                int energy = GetVarInt(in card, def, "Energy", 1);
                int stars = GetVarInt(in card, def, "Stars", 1);
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.RetainHandPower, 1, isCardSource: true);
                if (!scratch.NeedsChoice && energy > 0)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.EnergyNextTurnPower, energy, isCardSource: true);
                if (!scratch.NeedsChoice && stars > 0)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.StarNextTurnPower, stars, isCardSource: true);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.CosmicIndifference:
            {
                int block = def.GetBlock(card.UpgradeLevel, card.Var1Delta);
                GainPlayedCardBlock(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, block);
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
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.CrashLanding:
            {
                int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                int playIdx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Play, card.CardUid);
                HotCombatEngine.ExecuteAllEnemiesAttack(ref s, ref cold, rules, scratch, playIdx, dmg, hits: 1, powered: true);
                if (scratch.NeedsChoice)
                    return PauseForChoice(in s, out result);

                int maxHand = s.MaxHandSize > 0 ? s.MaxHandSize : 10;
                int count = Math.Max(0, maxHand - s.HandCount);
                ushort debrisId = rules.GetCardDefId(HotCustomCardId.Debris);
                for (int i = 0; i < count && !scratch.NeedsChoice && !HotCombatEngine.IsOverOrEnding(in s); i++)
                    HotCombatEngine.AddGeneratedFixedCard(ref s, ref cold, rules, scratch, debrisId, HotPileKind.Hand);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.CrescentSpear:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int calcBase = GetVarInt(in card, def, "CalculationBase", def.GetDamage(card.UpgradeLevel, card.Var0Delta));
                    int extra = GetVarInt(in card, def, "ExtraDamage", GetVarInt(in card, def, "CalculationExtra", 2));
                    int starCardCount = CountCardsWithStarCost(in s, rules);
                    int dmg = calcBase + extra * starCardCount + card.Var0Delta;
                    ExecutePlayedCardSingleAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, targetEnemySlot, dmg, hits: 1);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.CrushUnder:
            {
                int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                ExecutePlayedCardAllEnemiesAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, dmg, hits: 1);
                if (scratch.NeedsChoice)
                    return PauseForChoice(in s, out result);

                int loss = GetVarInt(in card, def, "StrengthLoss", def.GetMagic(card.UpgradeLevel));
                if (loss > 0)
                    ApplyTempStrengthLossAllEnemies(ref s, ref cold, rules, scratch, HotPowerId.CrushUnderPower, loss);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.DecisionsDecisions:
            {
                int drawCount = GetVarInt(in card, def, "Cards", 3);
                if (drawCount > 0)
                    HotCombatEngine.DrawCards(ref s, ref cold, rules, scratch, drawCount, fromHandDraw: false);
                if (scratch.NeedsChoice)
                    return PauseForChoice(in s, out result);

                if (HasPlayableSkillInHand(in s, rules))
                {
                    if (!HotCombatEngine.RequestChoice(ref s, scratch, HotChoiceKind.HandCard, cardDefId, 1, 1, out HotChoiceSelection sel))
                        return PauseForChoice(in s, out result);

                    int repeat = Math.Max(1, GetVarInt(in card, def, "Repeat", 3));
                    ushort chosenUid = sel.CardUidOrIndex;
                    for (int r = 0; r < repeat && !scratch.NeedsChoice && !HotCombatEngine.IsOverOrEnding(in s); r++)
                    {
                        int idx = HotCombatEngine.FindCardIndexByUidAnyPile(in s, chosenUid);
                        if (idx < 0)
                            break;
                        HotCombatEngine.AutoPlayCard(ref s, ref cold, rules, scratch, idx, targetEnemySlot: -1);
                    }
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.DyingStar:
            {
                int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                ExecutePlayedCardAllEnemiesAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, dmg, hits: 1);
                if (scratch.NeedsChoice)
                    return PauseForChoice(in s, out result);

                int loss = GetVarInt(in card, def, "StrengthLoss", def.GetMagic(card.UpgradeLevel));
                if (loss > 0)
                    ApplyTempStrengthLossAllEnemies(ref s, ref cold, rules, scratch, HotPowerId.DyingStarPower, loss);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Glimmer:
            {
                int drawCount = GetVarInt(in card, def, "Cards", 3);
                if (drawCount > 0)
                    HotCombatEngine.DrawCards(ref s, ref cold, rules, scratch, drawCount, fromHandDraw: false);
                if (scratch.NeedsChoice)
                    return PauseForChoice(in s, out result);

                int putBack = Math.Min(GetVarInt(in card, def, "PutBack", 1), (int)s.HandCount);
                if (!PutHandCardsOnDrawTop(ref s, scratch, cardDefId, putBack))
                    return PauseForChoice(in s, out result);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Glitterstream:
            {
                int block = def.GetBlock(card.UpgradeLevel, card.Var1Delta);
                GainPlayedCardBlock(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, block);
                if (scratch.NeedsChoice)
                    return PauseForChoice(in s, out result);

                int baseNextTurn = GetVarInt(in card, def, "BlockNextTurn", block);
                int modNextTurn = ComputeModifiedBlock(ref s, ref cold, rules, scratch, 0, baseNextTurn, isCardBlock: true);
                if (modNextTurn > 0)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.BlockNextTurnPower, modNextTurn, isCardSource: true);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Glow:
            {
                int stars = GetVarInt(in card, def, "Stars", card.UpgradeLevel > 0 ? 2 : 1);
                int cards = GetVarInt(in card, def, "Cards", 1);
                if (stars > 0)
                    HotCombatEngine.GainStars(ref s, ref cold, rules, scratch, stars);
                if (!scratch.NeedsChoice && cards > 0)
                    HotCombatEngine.DrawCards(ref s, ref cold, rules, scratch, cards, fromHandDraw: false);
                if (!scratch.NeedsChoice && cards > 0)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.DrawCardsNextTurnPower, cards, isCardSource: true);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Guards:
            {
                if (s.HandCount > 0)
                {
                    ushort sacrificeId = rules.GetCardDefId(HotCustomCardId.MinionSacrifice);
                    Span<ushort> selectedUids = stackalloc ushort[HotLimits.MaxChoicesPerAction];
                    int selectedCount = 0;
                    int maxPicks = Math.Min((int)s.HandCount, HotLimits.MaxChoicesPerAction);
                    for (int i = 0; i < maxPicks; i++)
                    {
                        if (!HotCombatEngine.RequestChoice(ref s, scratch, HotChoiceKind.HandCard, cardDefId, 0, (byte)maxPicks, out HotChoiceSelection sel))
                            return PauseForChoice(in s, out result);
                        if (sel.Kind == HotChoiceKind.None || sel.CardUidOrIndex == 0)
                            break;
                        selectedUids[selectedCount++] = sel.CardUidOrIndex;
                    }
                    for (int i = 0; i < selectedCount && !scratch.NeedsChoice; i++)
                    {
                        int idx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Hand, selectedUids[i]);
                        if (idx >= 0)
                            TransformCardAt(ref s, ref cold, rules, scratch, idx, sacrificeId, upgraded: card.UpgradeLevel > 0);
                    }
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.GuidingStar:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                    ExecutePlayedCardSingleAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, targetEnemySlot, dmg, hits: 1);
                }
                if (!scratch.NeedsChoice)
                {
                    int cards = GetVarInt(in card, def, "Cards", def.GetMagic(card.UpgradeLevel));
                    if (cards > 0)
                        HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.DrawCardsNextTurnPower, cards, isCardSource: true);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.HeavenlyDrill:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int hits = ResolveEnergyX(in s, rules, in card);
                    int threshold = GetVarInt(in card, def, "Energy", 4);
                    if (hits >= threshold)
                        hits *= 2;
                    int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                    ExecutePlayedCardSingleAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, targetEnemySlot, dmg, hits: hits);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.HeirloomHammer:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                    ExecutePlayedCardSingleAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, targetEnemySlot, dmg, hits: 1);
                    if (scratch.NeedsChoice)
                        return PauseForChoice(in s, out result);
                }

                if (HasColorlessCardInHand(in s, rules))
                {
                    if (!HotCombatEngine.RequestChoice(ref s, scratch, HotChoiceKind.HandCard, cardDefId, 1, 1, out HotChoiceSelection sel))
                        return PauseForChoice(in s, out result);
                    int idx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Hand, sel.CardUidOrIndex);
                    if (idx >= 0)
                    {
                        int repeat = Math.Max(1, GetVarInt(in card, def, "Repeat", 1));
                        HotCard template = s.Cards[idx];
                        for (int r = 0; r < repeat && !scratch.NeedsChoice; r++)
                            AddClonedCardInstance(ref s, ref cold, rules, scratch, in template, HotPileKind.Hand);
                    }
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.HiddenCache:
            {
                int stars = GetVarInt(in card, def, "Stars", 1);
                int nextStars = GetVarInt(in card, def, "StarNextTurnPower", 3);
                if (stars > 0)
                    HotCombatEngine.GainStars(ref s, ref cold, rules, scratch, stars);
                if (!scratch.NeedsChoice && nextStars > 0)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.StarNextTurnPower, nextStars, isCardSource: true);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.KnockoutBlow:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                    bool aliveBefore = (s.Enemies[targetEnemySlot].Vital.Flags & HotCreatureFlags.Alive) != 0;
                    ExecutePlayedCardSingleAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, targetEnemySlot, dmg, hits: 1);
                    if (scratch.NeedsChoice)
                        return PauseForChoice(in s, out result);

                    bool killed = aliveBefore && (s.Enemies[targetEnemySlot].Vital.Flags & HotCreatureFlags.Alive) == 0;
                    if (killed)
                    {
                        int stars = GetVarInt(in card, def, "Stars", def.GetMagic(card.UpgradeLevel));
                        if (stars > 0)
                            HotCombatEngine.GainStars(ref s, ref cold, rules, scratch, stars);
                    }
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.KnowThyPlace:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    byte targetSlot = (byte)(3 + targetEnemySlot);
                    int weak = GetVarInt(in card, def, "WeakPower", GetVarInt(in card, def, "Weak", def.GetMagic(card.UpgradeLevel)));
                    int vuln = GetVarInt(in card, def, "VulnerablePower", GetVarInt(in card, def, "Vulnerable", def.GetMagic(card.UpgradeLevel)));
                    if (weak > 0)
                        HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, targetSlot, 0, HotPowerId.WeakPower, weak, isCardSource: true);
                    if (!scratch.NeedsChoice && vuln > 0)
                        HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, targetSlot, 0, HotPowerId.VulnerablePower, vuln, isCardSource: true);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Largesse:
            {
                GenerateDistinctCardsFromPool(
                    ref s,
                    ref cold,
                    rules,
                    scratch,
                    GetColorlessPool(rules),
                    1,
                    HotPileKind.Hand,
                    upgraded: card.UpgradeLevel > 0,
                    excludeCardId: HotCustomCardId.None);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.LunarBlast:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int hits = s.Counters[(int)HotCounterSlot.SkillsPlayedThisTurn];
                    int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                    ExecutePlayedCardSingleAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, targetEnemySlot, dmg, hits: hits);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.ManifestAuthority:
            {
                int block = def.GetBlock(card.UpgradeLevel, card.Var1Delta);
                GainPlayedCardBlock(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, block);
                if (!scratch.NeedsChoice)
                {
                    GenerateDistinctCardsFromPool(
                        ref s,
                        ref cold,
                        rules,
                        scratch,
                        GetColorlessPool(rules),
                        1,
                        HotPileKind.Hand,
                        upgraded: card.UpgradeLevel > 0,
                        excludeCardId: HotCustomCardId.None);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.MeteorShower:
            {
                int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                ExecutePlayedCardAllEnemiesAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, dmg, hits: 1);
                if (scratch.NeedsChoice)
                    return PauseForChoice(in s, out result);

                int weak = GetVarInt(in card, def, "WeakPower", GetVarInt(in card, def, "Weak", 2));
                int vuln = GetVarInt(in card, def, "VulnerablePower", GetVarInt(in card, def, "Vulnerable", 2));
                if (weak > 0)
                    ApplyPowerAllEnemies(ref s, ref cold, rules, scratch, HotPowerId.WeakPower, weak, isCardSource: true);
                if (!scratch.NeedsChoice && vuln > 0)
                    ApplyPowerAllEnemies(ref s, ref cold, rules, scratch, HotPowerId.VulnerablePower, vuln, isCardSource: true);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Monologue:
            {
                ushort powerDefId = rules.GetPowerDefId(HotPowerId.MonologuePower);
                int strPerCard = GetVarInt(in card, def, "Power", Math.Max(1, def.GetMagic(card.UpgradeLevel)));
                if (powerDefId != 0)
                    HotCombatEngine.AddPowerInstance(ref s, rules, ownerSlot: 0, applierSlot: 0, powerDefId: powerDefId, amount: 1, aux0: strPerCard, aux1: 0);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.PaleBlueDot:
            {
                ushort powerDefId = rules.GetPowerDefId(HotPowerId.PaleBlueDotPower);
                int amt = GetVarInt(in card, def, "Cards", Math.Max(1, def.GetMagic(card.UpgradeLevel)));
                bool existed = powerDefId != 0 && HotCombatEngine.GetPowerAmount(in s, 0, powerDefId) > 0;
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.PaleBlueDotPower, amt, isCardSource: true);
                if (!existed && powerDefId != 0)
                {
                    int idx = HotCombatEngine.FindPowerIndex(in s, 0, powerDefId);
                    if (idx >= 0)
                        s.Powers[idx].Flags &= ~HotPowerFlags.BoolAux;
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.PhotonCut:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                    ExecutePlayedCardSingleAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, targetEnemySlot, dmg, hits: 1);
                    if (scratch.NeedsChoice)
                        return PauseForChoice(in s, out result);
                }
                int drawCount = GetVarInt(in card, def, "Cards", 1);
                if (drawCount > 0)
                    HotCombatEngine.DrawCards(ref s, ref cold, rules, scratch, drawCount, fromHandDraw: false);
                if (scratch.NeedsChoice)
                    return PauseForChoice(in s, out result);

                int putBack = Math.Min(GetVarInt(in card, def, "PutBack", 1), (int)s.HandCount);
                if (!PutHandCardsOnDrawTop(ref s, scratch, cardDefId, putBack))
                    return PauseForChoice(in s, out result);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.PillarOfCreation:
            {
                int blk = GetVarInt(in card, def, "Block", def.GetBlock(card.UpgradeLevel, 0));
                if (blk <= 0)
                    blk = Math.Max(1, def.GetMagic(card.UpgradeLevel));
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.PillarOfCreationPower, blk, isCardSource: true);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Plot:
            {
                int cards = GetVarInt(in card, def, "Cards", Math.Max(1, def.GetMagic(card.UpgradeLevel)));
                if (cards > 0)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.DrawCardsNextTurnPower, cards, isCardSource: true);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Quasar:
            {
                Span<ushort> options = stackalloc ushort[3];
                int optCount = RollDistinctPoolCards(ref s, GetColorlessPool(rules), options, excludeCardId: HotCustomCardId.None, rules);
                if (optCount > 0)
                {
                    HotCombatEngine.SetPendingGeneratedChoiceOptions(options[..optCount]);
                    if (!HotCombatEngine.RequestChoice(ref s, scratch, HotChoiceKind.GeneratedCard, cardDefId, 0, 1, out HotChoiceSelection sel))
                        return PauseForChoice(in s, out result);
                    if (sel.Kind == HotChoiceKind.GeneratedCard && sel.CardDefId != 0)
                    {
                        HotCombatEngine.AddGeneratedFixedCard(
                            ref s,
                            ref cold,
                            rules,
                            scratch,
                            sel.CardDefId,
                            HotPileKind.Hand,
                            dstOffset: -1,
                            upgraded: card.UpgradeLevel > 0,
                            createdByPlayer: true);
                    }
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Radiate:
            {
                int hits = s.Counters[(int)HotCounterSlot.StarsGainedThisTurn];
                int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                ExecutePlayedCardAllEnemiesAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, dmg, hits: hits);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.RefineBlade:
            {
                int forge = GetVarInt(in card, def, "Forge", 6);
                int energy = GetVarInt(in card, def, "Energy", 1);
                if (forge > 0)
                    Forge(ref s, ref cold, rules, scratch, forge);
                if (!scratch.NeedsChoice && energy > 0)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.EnergyNextTurnPower, energy, isCardSource: true);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Resonance:
            {
                int str = GetVarInt(in card, def, "StrengthPower", Math.Max(1, def.GetMagic(card.UpgradeLevel)));
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.StrengthPower, str, isCardSource: true);
                if (!scratch.NeedsChoice)
                    ApplyPowerAllEnemies(ref s, ref cold, rules, scratch, HotPowerId.StrengthPower, -1, isCardSource: true);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.SpoilsOfBattle:
            {
                int forge = GetVarInt(in card, def, "Forge", 10);
                int cards = GetVarInt(in card, def, "Cards", 1);
                if (forge > 0)
                    Forge(ref s, ref cold, rules, scratch, forge);
                if (!scratch.NeedsChoice && cards > 0)
                    HotCombatEngine.DrawCards(ref s, ref cold, rules, scratch, cards, fromHandDraw: false);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Stardust:
            {
                int hits = ResolveStarX(in s, rules, scratch);
                int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                ExecutePlayedCardRandomEnemyAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, dmg, hits: hits);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.SummonForth:
            {
                int forge = GetVarInt(in card, def, "Forge", 8);
                if (forge > 0)
                    Forge(ref s, ref cold, rules, scratch, forge);
                if (!scratch.NeedsChoice)
                    MoveSovereignBladesToHand(ref s, rules);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Supermassive:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int calcBase = GetVarInt(in card, def, "CalculationBase", def.GetDamage(card.UpgradeLevel, card.Var0Delta));
                    int extra = GetVarInt(in card, def, "ExtraDamage", GetVarInt(in card, def, "CalculationExtra", 3));
                    int dmg = calcBase + extra * s.HistoryCardsGenerated + card.Var0Delta;
                    ExecutePlayedCardSingleAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, targetEnemySlot, dmg, hits: 1);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Tutor:
            {
                if (s.DrawCount > 0)
                {
                    if (!HotCombatEngine.RequestChoice(ref s, scratch, HotChoiceKind.DrawPile, cardDefId, 1, 1, out HotChoiceSelection sel))
                        return PauseForChoice(in s, out result);
                    int idx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Draw, sel.CardUidOrIndex);
                    if (idx >= 0)
                        MoveCardToHandOrDiscard(ref s, idx);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.VoidForm:
            {
                ushort vfDefId = rules.GetPowerDefId(HotPowerId.VoidFormPower);
                int vfIdx = vfDefId != 0 ? HotCombatEngine.FindPowerIndex(in s, 0, vfDefId) : -1;
                if (vfIdx >= 0 && s.Powers[vfIdx].Amount > 0)
                    s.Powers[vfIdx].Aux0 = 999_999_999;

                int amt = GetVarInt(in card, def, "VoidFormPower", Math.Max(1, def.GetMagic(card.UpgradeLevel)));
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.VoidFormPower, amt, isCardSource: true);

                // VoidForm ends the player's turn after resolving. Mark newly created VoidFormPower's
                // Aux0 as already saturated this turn so no further cards get discounted if somehow played.
                if (vfDefId != 0)
                {
                    int postIdx = HotCombatEngine.FindPowerIndex(in s, 0, vfDefId);
                    if (postIdx >= 0)
                        s.Powers[postIdx].Aux0 = 999_999_999;
                }
                t_requestPlayerTurnEnd = true;
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.SovereignBlade:
            {
                bool allEnemies = HotCombatEngine.GetPowerAmountById(in s, rules, 0, HotPowerId.SeekingEdgePower) > 0;
                int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                int hits = def.GetHits(card.UpgradeLevel);
                if (allEnemies)
                {
                    ExecutePlayedCardAllEnemiesAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, dmg, hits: hits);
                }
                else if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    ExecutePlayedCardSingleAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, targetEnemySlot, dmg, hits: hits);
                }
                if (scratch.NeedsChoice)
                    return PauseForChoice(in s, out result);

                int parry = HotCombatEngine.GetPowerAmountById(in s, rules, 0, HotPowerId.ParryPower);
                if (parry > 0)
                    GainPlayedCardBlock(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, parry);
                return Complete(in s, scratch, out result);
            }

            // =====================================================================
            // NECROBINDER CUSTOM CARDS
            // =====================================================================

            case HotCustomCardId.BlightStrike:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    byte targetSlot = (byte)(3 + targetEnemySlot);
                    int hpBefore = s.Enemies[targetEnemySlot].Vital.CurrentHp;
                    int blkBefore = s.Enemies[targetEnemySlot].Vital.Block;
                    int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                    ExecutePlayedCardSingleAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, targetEnemySlot, dmg, hits: 1);
                    int unblocked = HotCombatEngine.t_lastAttackUnblockedDamage;
                    if (scratch.NeedsChoice)
                        return PauseForChoice(in s, out result);

                    int blockedUsed = Math.Max(0, blkBefore - s.Enemies[targetEnemySlot].Vital.Block);
                    int hpLost = Math.Max(0, hpBefore - s.Enemies[targetEnemySlot].Vital.CurrentHp);
                    int totalDealt = Math.Max(unblocked, hpLost) + blockedUsed;
                    if (totalDealt > 0 && (s.Enemies[targetEnemySlot].Vital.Flags & HotCreatureFlags.Alive) != 0)
                        HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, targetSlot, 0, HotPowerId.DoomPower, totalDealt, isCardSource: true);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.BoneShards:
            {
                if (IsOstyAlive(in s))
                {
                    int ostyDmg = GetVarInt(in card, def, "OstyDamage", def.GetDamage(card.UpgradeLevel, card.Var0Delta));
                    int playIdx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Play, card.CardUid);
                    HotCombatEngine.ExecuteDealerAllEnemiesAttack(ref s, ref cold, rules, scratch, dealerSlot: 1, cardIdx: playIdx, cardDefId: cardDefId, baseDamage: ostyDmg, hits: 1, powered: true);
                    s.Counters[(int)HotCounterSlot.OstyAttacksThisTurn]++;
                    NotifyOstyAttacked(ref s, ref cold, rules);
                    if (scratch.NeedsChoice)
                        return PauseForChoice(in s, out result);

                    int block = def.GetBlock(card.UpgradeLevel, card.Var1Delta);
                    if (block > 0)
                        HotCombatEngine.GainBlockInternal(ref s, ref cold, rules, scratch, 0, block, isCardBlock: true, cardIdx: playIdx, cardDefId: cardDefId);
                    if (scratch.NeedsChoice)
                        return PauseForChoice(in s, out result);

                    if (IsOstyAlive(in s))
                        HotCombatEngine.KillCreature(ref s, ref cold, rules, scratch, targetSlot: 1, force: true);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.BorrowedTime:
            {
                int energy = GetVarInt(in card, def, "Energy", 1);
                int extraCost = GetVarInt(in card, def, "ExtraCost", 1);
                if (energy > 0)
                    HotCombatEngine.GainEnergy(ref s, rules, energy);
                if (extraCost > 0)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.BorrowedTimePower, extraCost, isCardSource: true);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.CaptureSpirit:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                    HotCombatEngine.DealDamage(ref s, ref cold, rules, scratch, 0, (byte)(3 + targetEnemySlot), dmg, isPoweredAttack: false, isUnblockable: true);
                    if (scratch.NeedsChoice)
                        return PauseForChoice(in s, out result);
                }
                int souls = GetVarInt(in card, def, "Cards", 3);
                AddSoulsToPile(ref s, ref cold, rules, scratch, souls, HotPileKind.Draw, dstOffset: -2, upgraded: false);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Cleanse:
            {
                int summon = GetVarInt(in card, def, "Summon", def.GetMagic(card.UpgradeLevel));
                if (summon > 0)
                    SummonOsty(ref s, ref cold, rules, scratch, summon);
                if (!scratch.NeedsChoice && s.DrawCount > 0)
                {
                    if (!HotCombatEngine.RequestChoice(ref s, scratch, HotChoiceKind.DrawPile, cardDefId, 1, 1, out HotChoiceSelection sel))
                        return PauseForChoice(in s, out result);
                    int idx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Draw, sel.CardUidOrIndex);
                    if (idx >= 0)
                        HotCombatEngine.ExhaustCard(ref s, ref cold, rules, scratch, idx);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.DanseMacabre:
            {
                int amt = GetVarInt(in card, def, "DanseMacabrePower", Math.Max(1, def.GetMagic(card.UpgradeLevel)));
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.DanseMacabrePower, amt, isCardSource: true);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Deathbringer:
            {
                int doom = GetVarInt(in card, def, "DoomPower", GetVarInt(in card, def, "Doom", 21));
                int weak = GetVarInt(in card, def, "WeakPower", GetVarInt(in card, def, "Weak", 1));
                Span<byte> enemies = stackalloc byte[HotLimits.MaxEnemies];
                int enemyCount = SnapshotLivingEnemies(in s, enemies);
                for (int i = 0; i < enemyCount && !scratch.NeedsChoice; i++)
                {
                    byte enemySlot = enemies[i];
                    if ((s.Enemies[enemySlot].Vital.Flags & HotCreatureFlags.Alive) == 0)
                        continue;
                    byte creatureSlot = (byte)(3 + enemySlot);
                    if (doom > 0)
                        HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, creatureSlot, 0, HotPowerId.DoomPower, doom, isCardSource: true);
                    if (!scratch.NeedsChoice && weak > 0 && (s.Enemies[enemySlot].Vital.Flags & HotCreatureFlags.Alive) != 0)
                        HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, creatureSlot, 0, HotPowerId.WeakPower, weak, isCardSource: true);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.DeathMarch:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int calcBase = GetVarInt(in card, def, "CalculationBase", def.GetDamage(card.UpgradeLevel, card.Var0Delta));
                    int extra = GetVarInt(in card, def, "ExtraDamage", GetVarInt(in card, def, "CalculationExtra", 4));
                    int dmg = calcBase + extra * s.Counters[(int)HotCounterSlot.NonHandDrawsThisTurn] + card.Var0Delta;
                    ExecutePlayedCardSingleAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, targetEnemySlot, dmg, hits: 1);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.DeathsDoor:
            {
                int block = def.GetBlock(card.UpgradeLevel, card.Var1Delta);
                GainPlayedCardBlock(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, block);
                if (!scratch.NeedsChoice && s.Counters[(int)HotCounterSlot.WasDoomAppliedThisTurn] > 0)
                {
                    int repeat = Math.Max(1, GetVarInt(in card, def, "Repeat", 2));
                    for (int i = 0; i < repeat && !scratch.NeedsChoice; i++)
                        GainPlayedCardBlock(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, block);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Demesne:
            {
                int amt = GetVarInt(in card, def, "Cards", Math.Max(1, def.GetMagic(card.UpgradeLevel)));
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.DemesnePower, amt, isCardSource: true);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Dirge:
            {
                int x = ResolveEnergyX(in s, rules, in card);
                int summon = GetVarInt(in card, def, "Summon", 3);
                for (int i = 0; i < x && !scratch.NeedsChoice; i++)
                    SummonOsty(ref s, ref cold, rules, scratch, summon);
                if (!scratch.NeedsChoice && x > 0)
                    AddSoulsToPile(ref s, ref cold, rules, scratch, x, HotPileKind.Draw, dstOffset: -2, upgraded: card.UpgradeLevel > 0);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.DrainPower:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                    ExecutePlayedCardSingleAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, targetEnemySlot, dmg, hits: 1);
                    if (scratch.NeedsChoice)
                        return PauseForChoice(in s, out result);
                }
                int count = GetVarInt(in card, def, "Cards", 2);
                UpgradeRandomDiscardCards(ref s, rules, count);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Dredge:
            {
                int maxHand = s.MaxHandSize > 0 ? s.MaxHandSize : 10;
                int cards = GetVarInt(in card, def, "Cards", 3);
                int picks = Math.Min(Math.Min(cards, Math.Max(0, maxHand - s.HandCount)), (int)s.DiscardCount);
                if (picks > 0)
                {
                    Span<ushort> selectedUids = stackalloc ushort[Math.Min(picks, HotLimits.MaxChoicesPerAction)];
                    int actualPicks = Math.Min(picks, selectedUids.Length);
                    for (int i = 0; i < actualPicks; i++)
                    {
                        if (!HotCombatEngine.RequestChoice(ref s, scratch, HotChoiceKind.DiscardPile, cardDefId, 1, 1, out HotChoiceSelection sel))
                            return PauseForChoice(in s, out result);
                        selectedUids[i] = sel.CardUidOrIndex;
                    }
                    for (int i = 0; i < actualPicks; i++)
                    {
                        int idx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Discard, selectedUids[i]);
                        if (idx >= 0)
                            MoveCardToHandOrDiscard(ref s, idx);
                    }
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Eidolon:
            {
                Span<ushort> etherealUids = stackalloc ushort[HotLimits.MaxCards];
                int count = 0;
                int exhaustStart = s.ExhaustStart;
                int exhaustEnd = exhaustStart + s.ExhaustCount;
                for (int i = exhaustStart; i < exhaustEnd; i++)
                {
                    if (HotCombatEngine.IsCardEthereal(in s, in s.Cards[i], rules)
                        && (s.Cards[i].LocalKeywordsMask & HotCardKeywordMask.Unplayable) == 0)
                        etherealUids[count++] = s.Cards[i].CardUid;
                }
                for (int i = 0; i < count && !scratch.NeedsChoice && !HotCombatEngine.IsOverOrEnding(in s); i++)
                {
                    int idx = HotCombatEngine.FindCardIndexByUidAnyPile(in s, etherealUids[i]);
                    if (idx >= 0)
                        HotCombatEngine.AutoPlayCard(ref s, ref cold, rules, scratch, idx, targetEnemySlot: -1);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.EndOfDays:
            {
                int doom = GetVarInt(in card, def, "DoomPower", GetVarInt(in card, def, "Doom", 29));
                if (doom > 0)
                    ApplyPowerAllEnemies(ref s, ref cold, rules, scratch, HotPowerId.DoomPower, doom, isCardSource: true);
                if (scratch.NeedsChoice)
                    return PauseForChoice(in s, out result);

                Span<byte> doomedSlots = stackalloc byte[HotLimits.MaxEnemies];
                int doomedCount = 0;
                int rosterCount = s.RosterCount;
                for (int r = 0; r < rosterCount; r++)
                {
                    byte enemySlot = s.ActiveEnemyRoster[r];
                    if ((s.Enemies[enemySlot].Vital.Flags & HotCreatureFlags.Alive) == 0)
                        continue;
                    byte creatureSlot = (byte)(3 + enemySlot);
                    int enemyDoom = HotCombatEngine.GetPowerAmountById(in s, rules, creatureSlot, HotPowerId.DoomPower);
                    if (enemyDoom > 0 && s.Enemies[enemySlot].Vital.CurrentHp <= enemyDoom)
                        doomedSlots[doomedCount++] = creatureSlot;
                }
                if (doomedCount > 0)
                    DoomKillCreatures(ref s, ref cold, rules, scratch, doomedSlots[..doomedCount]);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Eradicate:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int hits = ResolveEnergyX(in s, rules, in card);
                    int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                    ExecutePlayedCardSingleAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, targetEnemySlot, dmg, hits: hits);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Fetch:
            {
                if (IsOstyAlive(in s) && HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int ostyDmg = GetVarInt(in card, def, "OstyDamage", def.GetDamage(card.UpgradeLevel, card.Var0Delta));
                    int playIdx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Play, card.CardUid);
                    HotCombatEngine.ExecuteDealerSingleTargetAttack(ref s, ref cold, rules, scratch, dealerSlot: 1, cardIdx: playIdx, cardDefId: cardDefId, targetEnemySlot: targetEnemySlot, baseDamage: ostyDmg, hits: 1, powered: true);
                    s.Counters[(int)HotCounterSlot.OstyAttacksThisTurn]++;
                    NotifyOstyAttacked(ref s, ref cold, rules);
                    if (scratch.NeedsChoice)
                        return PauseForChoice(in s, out result);

                    if ((card.Flags & HotCardFlags.FetchPlayedThisTurn) == 0)
                    {
                        int liveIdx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Play, card.CardUid);
                        if (liveIdx >= 0)
                            s.Cards[liveIdx].Flags |= HotCardFlags.FetchPlayedThisTurn;
                        int drawCount = GetVarInt(in card, def, "Cards", 1);
                        if (drawCount > 0)
                            HotCombatEngine.DrawCards(ref s, ref cold, rules, scratch, drawCount, fromHandDraw: false);
                    }
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Flatten:
            case HotCustomCardId.Poke:
            case HotCustomCardId.RightHandHand:
            {
                if (IsOstyAlive(in s) && HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int ostyDmg = GetVarInt(in card, def, "OstyDamage", def.GetDamage(card.UpgradeLevel, card.Var0Delta));
                    int playIdx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Play, card.CardUid);
                    HotCombatEngine.ExecuteDealerSingleTargetAttack(ref s, ref cold, rules, scratch, dealerSlot: 1, cardIdx: playIdx, cardDefId: cardDefId, targetEnemySlot: targetEnemySlot, baseDamage: ostyDmg, hits: 1, powered: true);
                    s.Counters[(int)HotCounterSlot.OstyAttacksThisTurn]++;
                    NotifyOstyAttacked(ref s, ref cold, rules);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Friendship:
            {
                int strLoss = GetVarInt(in card, def, "StrengthPower", 2);
                int energy = GetVarInt(in card, def, "Energy", 1);
                if (strLoss > 0)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.StrengthPower, -strLoss, isCardSource: true);
                if (!scratch.NeedsChoice && energy > 0)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.FriendshipPower, energy, isCardSource: true);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.GlimpseBeyond:
            {
                int souls = GetVarInt(in card, def, "Cards", 3);
                AddSoulsToPile(ref s, ref cold, rules, scratch, souls, HotPileKind.Draw, dstOffset: -2, upgraded: false);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Graveblast:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                    ExecutePlayedCardSingleAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, targetEnemySlot, dmg, hits: 1);
                    if (scratch.NeedsChoice)
                        return PauseForChoice(in s, out result);
                }
                if (s.DiscardCount > 0)
                {
                    if (!HotCombatEngine.RequestChoice(ref s, scratch, HotChoiceKind.DiscardPile, cardDefId, 1, 1, out HotChoiceSelection sel))
                        return PauseForChoice(in s, out result);
                    int idx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Discard, sel.CardUidOrIndex);
                    if (idx >= 0)
                        MoveCardToHandOrDiscard(ref s, idx);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.GraveWarden:
            {
                int block = def.GetBlock(card.UpgradeLevel, card.Var1Delta);
                GainPlayedCardBlock(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, block);
                if (!scratch.NeedsChoice)
                {
                    int souls = GetVarInt(in card, def, "Cards", 1);
                    AddSoulsToPile(ref s, ref cold, rules, scratch, souls, HotPileKind.Draw, dstOffset: -2, upgraded: false);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Hang:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    byte targetSlot = (byte)(3 + targetEnemySlot);
                    int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                    ExecutePlayedCardSingleAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, targetEnemySlot, dmg, hits: 1);
                    if (scratch.NeedsChoice)
                        return PauseForChoice(in s, out result);

                    if ((s.Enemies[targetEnemySlot].Vital.Flags & HotCreatureFlags.Alive) != 0)
                    {
                        int current = HotCombatEngine.GetPowerAmountById(in s, rules, targetSlot, HotPowerId.HangPower);
                        HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, targetSlot, 0, HotPowerId.HangPower, Math.Max(2, current), isCardSource: true);
                    }
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.HighFive:
            {
                if (IsOstyAlive(in s))
                {
                    int ostyDmg = GetVarInt(in card, def, "OstyDamage", def.GetDamage(card.UpgradeLevel, card.Var0Delta));
                    int playIdx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Play, card.CardUid);
                    HotCombatEngine.ExecuteDealerAllEnemiesAttack(ref s, ref cold, rules, scratch, dealerSlot: 1, cardIdx: playIdx, cardDefId: cardDefId, baseDamage: ostyDmg, hits: 1, powered: true);
                    s.Counters[(int)HotCounterSlot.OstyAttacksThisTurn]++;
                    NotifyOstyAttacked(ref s, ref cold, rules);
                    if (scratch.NeedsChoice)
                        return PauseForChoice(in s, out result);

                    int vuln = GetVarInt(in card, def, "VulnerablePower", GetVarInt(in card, def, "Vulnerable", 2));
                    if (vuln > 0)
                        ApplyPowerAllEnemies(ref s, ref cold, rules, scratch, HotPowerId.VulnerablePower, vuln, isCardSource: true);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Invoke:
            {
                int summon = GetVarInt(in card, def, "Summon", 2);
                int energy = GetVarInt(in card, def, "Energy", 2);
                if (summon > 0)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.SummonNextTurnPower, summon, isCardSource: true);
                if (!scratch.NeedsChoice && energy > 0)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.EnergyNextTurnPower, energy, isCardSource: true);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.LegionOfBone:
            {
                int summon = GetVarInt(in card, def, "Summon", Math.Max(1, def.GetMagic(card.UpgradeLevel)));
                if (summon > 0)
                    SummonOsty(ref s, ref cold, rules, scratch, summon);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Misery:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    byte targetSlot = (byte)(3 + targetEnemySlot);
                    Span<ushort> debuffDefIds = stackalloc ushort[HotLimits.MaxPowers];
                    Span<int> debuffAmounts = stackalloc int[HotLimits.MaxPowers];
                    Span<byte> debuffAppliers = stackalloc byte[HotLimits.MaxPowers];
                    int debuffCount = SnapshotTargetDebuffs(in s, rules, targetSlot, debuffDefIds, debuffAmounts, debuffAppliers);

                    int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                    ExecutePlayedCardSingleAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, targetEnemySlot, dmg, hits: 1);
                    if (scratch.NeedsChoice)
                        return PauseForChoice(in s, out result);

                    if (debuffCount > 0)
                    {
                        Span<byte> enemies = stackalloc byte[HotLimits.MaxEnemies];
                        int enemyCount = SnapshotLivingEnemies(in s, enemies);
                        for (int e = 0; e < enemyCount && !scratch.NeedsChoice; e++)
                        {
                            byte otherEnemySlot = enemies[e];
                            if (otherEnemySlot == targetEnemySlot || (s.Enemies[otherEnemySlot].Vital.Flags & HotCreatureFlags.Alive) == 0)
                                continue;
                            byte otherCreatureSlot = (byte)(3 + otherEnemySlot);
                            for (int d = 0; d < debuffCount && !scratch.NeedsChoice; d++)
                            {
                                if (debuffAmounts[d] == 0)
                                    continue;
                                HotCombatEngine.ApplyPower(ref s, ref cold, rules, scratch, otherCreatureSlot, debuffAppliers[d], debuffDefIds[d], debuffAmounts[d], isCardSource: false);
                            }
                        }
                    }
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.NegativePulse:
            {
                int block = def.GetBlock(card.UpgradeLevel, card.Var1Delta);
                GainPlayedCardBlock(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, block);
                if (!scratch.NeedsChoice)
                {
                    int doom = GetVarInt(in card, def, "DoomPower", GetVarInt(in card, def, "Doom", 7));
                    if (doom > 0)
                        ApplyPowerAllEnemies(ref s, ref cold, rules, scratch, HotPowerId.DoomPower, doom, isCardSource: true);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Neurosurge:
            {
                int energy = GetVarInt(in card, def, "Energy", 3);
                int cards = GetVarInt(in card, def, "Cards", 2);
                int doomPerTurn = GetVarInt(in card, def, "NeurosurgePower", 3);
                if (energy > 0)
                    HotCombatEngine.GainEnergy(ref s, rules, energy);
                if (!scratch.NeedsChoice && cards > 0)
                    HotCombatEngine.DrawCards(ref s, ref cold, rules, scratch, cards, fromHandDraw: false);
                if (!scratch.NeedsChoice && doomPerTurn > 0)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.NeurosurgePower, doomPerTurn, isCardSource: true);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.NoEscape:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    byte targetSlot = (byte)(3 + targetEnemySlot);
                    int threshold = Math.Max(1, GetVarInt(in card, def, "DoomThreshold", 10));
                    int calcBase = GetVarInt(in card, def, "CalculationBase", 10);
                    int calcExtra = GetVarInt(in card, def, "CalculationExtra", 5);
                    int currentDoom = HotCombatEngine.GetPowerAmountById(in s, rules, targetSlot, HotPowerId.DoomPower);
                    int amount = calcBase + calcExtra * (currentDoom / threshold);
                    if (amount > 0)
                        HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, targetSlot, 0, HotPowerId.DoomPower, amount, isCardSource: true);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Protector:
            {
                if (IsOstyAlive(in s) && HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int calcBase = GetVarInt(in card, def, "CalculationBase", 10);
                    int extra = GetVarInt(in card, def, "ExtraDamage", GetVarInt(in card, def, "CalculationExtra", 1));
                    int dmg = calcBase + extra * s.Osty.MaxHp + card.Var0Delta;
                    int playIdx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Play, card.CardUid);
                    HotCombatEngine.ExecuteDealerSingleTargetAttack(ref s, ref cold, rules, scratch, dealerSlot: 1, cardIdx: playIdx, cardDefId: cardDefId, targetEnemySlot: targetEnemySlot, baseDamage: dmg, hits: 1, powered: true);
                    s.Counters[(int)HotCounterSlot.OstyAttacksThisTurn]++;
                    NotifyOstyAttacked(ref s, ref cold, rules);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.PullFromBelow:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int hits = s.HistoryEtherealPlays;
                    int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                    ExecutePlayedCardSingleAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, targetEnemySlot, dmg, hits: hits);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Putrefy:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    byte targetSlot = (byte)(3 + targetEnemySlot);
                    int amt = GetVarInt(in card, def, "Power", Math.Max(1, def.GetMagic(card.UpgradeLevel)));
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, targetSlot, 0, HotPowerId.WeakPower, amt, isCardSource: true);
                    if (!scratch.NeedsChoice)
                        HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, targetSlot, 0, HotPowerId.VulnerablePower, amt, isCardSource: true);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Rattle:
            {
                if (IsOstyAlive(in s) && HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int hits = 1 + s.Counters[(int)HotCounterSlot.OstyAttacksThisTurn];
                    int ostyDmg = GetVarInt(in card, def, "OstyDamage", def.GetDamage(card.UpgradeLevel, card.Var0Delta));
                    int playIdx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Play, card.CardUid);
                    HotCombatEngine.ExecuteDealerSingleTargetAttack(ref s, ref cold, rules, scratch, dealerSlot: 1, cardIdx: playIdx, cardDefId: cardDefId, targetEnemySlot: targetEnemySlot, baseDamage: ostyDmg, hits: hits, powered: true);
                    s.Counters[(int)HotCounterSlot.OstyAttacksThisTurn]++;
                    NotifyOstyAttacked(ref s, ref cold, rules);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Reave:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                    ExecutePlayedCardSingleAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, targetEnemySlot, dmg, hits: 1);
                    if (scratch.NeedsChoice)
                        return PauseForChoice(in s, out result);
                }
                int souls = GetVarInt(in card, def, "Cards", 1);
                AddSoulsToPile(ref s, ref cold, rules, scratch, souls, HotPileKind.Draw, dstOffset: -2, upgraded: card.UpgradeLevel > 0);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Sacrifice:
            {
                if (IsOstyAlive(in s))
                {
                    int block = s.Osty.MaxHp * 3;
                    HotCombatEngine.KillCreature(ref s, ref cold, rules, scratch, targetSlot: 1, force: true);
                    if (scratch.NeedsChoice)
                        return PauseForChoice(in s, out result);
                    GainPlayedCardBlock(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, block);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Scourge:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int doom = GetVarInt(in card, def, "DoomPower", GetVarInt(in card, def, "Doom", 13));
                    if (doom > 0)
                        HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, (byte)(3 + targetEnemySlot), 0, HotPowerId.DoomPower, doom, isCardSource: true);
                    if (scratch.NeedsChoice)
                        return PauseForChoice(in s, out result);
                }
                int drawCount = GetVarInt(in card, def, "Cards", 1);
                if (drawCount > 0)
                    HotCombatEngine.DrawCards(ref s, ref cold, rules, scratch, drawCount, fromHandDraw: false);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.SculptingStrike:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                    int playIdx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Play, card.CardUid);
                    HotCombatEngine.ExecuteDealerSingleTargetAttack(ref s, ref cold, rules, scratch, dealerSlot: 0, cardIdx: playIdx, cardDefId: cardDefId, targetEnemySlot: targetEnemySlot, baseDamage: dmg, hits: 1, powered: true);
                    if (scratch.NeedsChoice)
                        return PauseForChoice(in s, out result);
                }
                if (HasHandCardWithoutKeyword(in s, HotCardKeywordMask.Ethereal))
                {
                    if (!HotCombatEngine.RequestChoice(ref s, scratch, HotChoiceKind.HandCard, cardDefId, 1, 1, out HotChoiceSelection sel))
                        return PauseForChoice(in s, out result);
                    int idx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Hand, sel.CardUidOrIndex);
                    if (idx >= 0)
                    {
                        s.Cards[idx].LocalKeywordsMask |= HotCardKeywordMask.Ethereal;
                        s.Cards[idx].KeywordsAddedOutsideMask |= HotCardKeywordMask.Ethereal;
                    }
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Seance:
            {
                int count = GetVarInt(in card, def, "Cards", 1);
                int picks = Math.Min(count, (int)s.DrawCount);
                if (picks > 0)
                {
                    ushort soulId = rules.GetCardDefId(HotCustomCardId.Soul);
                    Span<ushort> selectedUids = stackalloc ushort[Math.Min(picks, HotLimits.MaxChoicesPerAction)];
                    int actualPicks = Math.Min(picks, selectedUids.Length);
                    for (int i = 0; i < actualPicks; i++)
                    {
                        if (!HotCombatEngine.RequestChoice(ref s, scratch, HotChoiceKind.DrawPile, cardDefId, 1, 1, out HotChoiceSelection sel))
                            return PauseForChoice(in s, out result);
                        selectedUids[i] = sel.CardUidOrIndex;
                    }
                    for (int i = 0; i < actualPicks && !scratch.NeedsChoice; i++)
                    {
                        int idx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Draw, selectedUids[i]);
                        if (idx >= 0)
                            TransformCardAt(ref s, ref cold, rules, scratch, idx, soulId, upgraded: false);
                    }
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Severance:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                    ExecutePlayedCardSingleAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, targetEnemySlot, dmg, hits: 1);
                    if (scratch.NeedsChoice)
                        return PauseForChoice(in s, out result);
                }
                AddSoulsToPile(ref s, ref cold, rules, scratch, 1, HotPileKind.Draw, dstOffset: -2, upgraded: false);
                if (!scratch.NeedsChoice)
                    AddSoulsToPile(ref s, ref cold, rules, scratch, 1, HotPileKind.Discard, dstOffset: -1, upgraded: false);
                if (!scratch.NeedsChoice)
                    AddSoulsToPile(ref s, ref cold, rules, scratch, 1, HotPileKind.Hand, dstOffset: -1, upgraded: false);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.SharedFate:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    byte targetSlot = (byte)(3 + targetEnemySlot);
                    int playerLoss = GetVarInt(in card, def, "PlayerStrengthLoss", 2);
                    int enemyLoss = GetVarInt(in card, def, "EnemyStrengthLoss", 2);
                    if (playerLoss > 0)
                        HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.StrengthPower, -playerLoss, isCardSource: true);
                    if (!scratch.NeedsChoice && enemyLoss > 0)
                        HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, targetSlot, 0, HotPowerId.StrengthPower, -enemyLoss, isCardSource: true);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Shroud:
            {
                int blk = GetVarInt(in card, def, "Block", def.GetBlock(card.UpgradeLevel, 0));
                if (blk <= 0)
                    blk = Math.Max(1, def.GetMagic(card.UpgradeLevel));
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.ShroudPower, blk, isCardSource: true);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.SicEm:
            {
                if (IsOstyAlive(in s) && HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    byte targetSlot = (byte)(3 + targetEnemySlot);
                    int ostyDmg = GetVarInt(in card, def, "OstyDamage", def.GetDamage(card.UpgradeLevel, card.Var0Delta));
                    int playIdx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Play, card.CardUid);
                    HotCombatEngine.ExecuteDealerSingleTargetAttack(ref s, ref cold, rules, scratch, dealerSlot: 1, cardIdx: playIdx, cardDefId: cardDefId, targetEnemySlot: targetEnemySlot, baseDamage: ostyDmg, hits: 1, powered: true);
                    s.Counters[(int)HotCounterSlot.OstyAttacksThisTurn]++;
                    NotifyOstyAttacked(ref s, ref cold, rules);
                    if (scratch.NeedsChoice)
                        return PauseForChoice(in s, out result);

                    int sicAmt = GetVarInt(in card, def, "SicEmPower", Math.Max(1, def.GetMagic(card.UpgradeLevel)));
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, targetSlot, 0, HotPowerId.SicEmPower, sicAmt, isCardSource: true);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Snap:
            {
                if (IsOstyAlive(in s) && HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int ostyDmg = GetVarInt(in card, def, "OstyDamage", def.GetDamage(card.UpgradeLevel, card.Var0Delta));
                    int playIdx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Play, card.CardUid);
                    HotCombatEngine.ExecuteDealerSingleTargetAttack(ref s, ref cold, rules, scratch, dealerSlot: 1, cardIdx: playIdx, cardDefId: cardDefId, targetEnemySlot: targetEnemySlot, baseDamage: ostyDmg, hits: 1, powered: true);
                    s.Counters[(int)HotCounterSlot.OstyAttacksThisTurn]++;
                    NotifyOstyAttacked(ref s, ref cold, rules);
                    if (scratch.NeedsChoice)
                        return PauseForChoice(in s, out result);
                }
                if (HasHandCardWithoutKeyword(in s, HotCardKeywordMask.Retain))
                {
                    if (!HotCombatEngine.RequestChoice(ref s, scratch, HotChoiceKind.HandCard, cardDefId, 1, 1, out HotChoiceSelection sel))
                        return PauseForChoice(in s, out result);
                    int idx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Hand, sel.CardUidOrIndex);
                    if (idx >= 0)
                    {
                        s.Cards[idx].LocalKeywordsMask |= HotCardKeywordMask.Retain;
                        s.Cards[idx].KeywordsAddedOutsideMask |= HotCardKeywordMask.Retain;
                    }
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.SoulStorm:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int calcBase = GetVarInt(in card, def, "CalculationBase", def.GetDamage(card.UpgradeLevel, card.Var0Delta));
                    int extra = GetVarInt(in card, def, "ExtraDamage", GetVarInt(in card, def, "CalculationExtra", 4));
                    int dmg = calcBase + extra * CountSoulsInExhaust(in s, rules) + card.Var0Delta;
                    ExecutePlayedCardSingleAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, targetEnemySlot, dmg, hits: 1);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Spur:
            {
                int summon = GetVarInt(in card, def, "Summon", 3);
                int heal = GetVarInt(in card, def, "Heal", 5);
                if (summon > 0)
                    SummonOsty(ref s, ref cold, rules, scratch, summon);
                if (!scratch.NeedsChoice && heal > 0)
                    HealOsty(ref s, ref cold, rules, scratch, heal);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Squeeze:
            {
                if (IsOstyAlive(in s) && HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int calcBase = GetVarInt(in card, def, "CalculationBase", 25);
                    int extra = GetVarInt(in card, def, "ExtraDamage", GetVarInt(in card, def, "CalculationExtra", 5));
                    int otherOstyCards = CountOtherOstyAttackCards(in s, rules, card.CardUid);
                    int dmg = calcBase + extra * otherOstyCards + card.Var0Delta;
                    int playIdx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Play, card.CardUid);
                    HotCombatEngine.ExecuteDealerSingleTargetAttack(ref s, ref cold, rules, scratch, dealerSlot: 1, cardIdx: playIdx, cardDefId: cardDefId, targetEnemySlot: targetEnemySlot, baseDamage: dmg, hits: 1, powered: true);
                    s.Counters[(int)HotCounterSlot.OstyAttacksThisTurn]++;
                    NotifyOstyAttacked(ref s, ref cold, rules);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.TheScythe:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int dmg = def.GetDamage(card.UpgradeLevel, card.Var0Delta);
                    ExecutePlayedCardSingleAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, targetEnemySlot, dmg, hits: 1);
                    if (scratch.NeedsChoice)
                        return PauseForChoice(in s, out result);

                    int increase = GetVarInt(in card, def, "Increase", 3);
                    int liveIdx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Play, card.CardUid);
                    if (liveIdx >= 0)
                    {
                        s.Cards[liveIdx].CardPrivate += increase;
                        s.Cards[liveIdx].Var0Delta = (short)(s.Cards[liveIdx].Var0Delta + increase);
                    }
                    if ((card.Flags & HotCardFlags.HasDeckVersion) != 0)
                    {
                        s.LongTermResourceValue += increase;
                        s.Growth[GrowthIdxTheScythe]++;
                    }
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.TimesUp:
            {
                if (HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    byte targetSlot = (byte)(3 + targetEnemySlot);
                    int calcBase = GetVarInt(in card, def, "CalculationBase", 0);
                    int extra = GetVarInt(in card, def, "ExtraDamage", GetVarInt(in card, def, "CalculationExtra", 1));
                    int enemyDoom = HotCombatEngine.GetPowerAmountById(in s, rules, targetSlot, HotPowerId.DoomPower);
                    int dmg = calcBase + extra * enemyDoom + card.Var0Delta;
                    ExecutePlayedCardSingleAttack(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, targetEnemySlot, dmg, hits: 1);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Transfigure:
            {
                if (s.HandCount > 0)
                {
                    if (!HotCombatEngine.RequestChoice(ref s, scratch, HotChoiceKind.HandCard, cardDefId, 1, 1, out HotChoiceSelection sel))
                        return PauseForChoice(in s, out result);
                    int idx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Hand, sel.CardUidOrIndex);
                    if (idx >= 0)
                    {
                        ref HotCard targetCard = ref s.Cards[idx];
                        if ((targetCard.Flags & HotCardFlags.CostsX) == 0 && targetCard.BaseEnergyCost >= 0)
                        {
                            HotCostModifiers.AddEnergyModifierExact(
                                ref targetCard,
                                ref cold,
                                amount: 1,
                                isAbsolute: false,
                                expiration: 0, // EndOfCombat
                                isReduceOnly: false);
                        }
                        if (targetCard.BaseReplayCount < byte.MaxValue)
                            targetCard.BaseReplayCount++;
                    }
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Undeath:
            {
                int block = def.GetBlock(card.UpgradeLevel, card.Var1Delta);
                GainPlayedCardBlock(ref s, ref cold, rules, scratch, card.CardUid, cardDefId, block);
                if (!scratch.NeedsChoice)
                    AddClonedCardInstance(ref s, ref cold, rules, scratch, in card, HotPileKind.Discard);
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.Unleash:
            {
                if (IsOstyAlive(in s) && HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int calcBase = GetVarInt(in card, def, "CalculationBase", 6);
                    int extra = GetVarInt(in card, def, "ExtraDamage", GetVarInt(in card, def, "CalculationExtra", 1));
                    int dmg = calcBase + extra * s.Osty.CurrentHp + card.Var0Delta;
                    int playIdx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Play, card.CardUid);
                    HotCombatEngine.ExecuteDealerSingleTargetAttack(ref s, ref cold, rules, scratch, dealerSlot: 1, cardIdx: playIdx, cardDefId: cardDefId, targetEnemySlot: targetEnemySlot, baseDamage: dmg, hits: 1, powered: true);
                    s.Counters[(int)HotCounterSlot.OstyAttacksThisTurn]++;
                    NotifyOstyAttacked(ref s, ref cold, rules);
                }
                return Complete(in s, scratch, out result);
            }

            case HotCustomCardId.SweepingGaze:
            {
                if (IsOstyAlive(in s))
                {
                    int ostyDmg = GetVarInt(in card, def, "OstyDamage", def.GetDamage(card.UpgradeLevel, card.Var0Delta));
                    int playIdx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Play, card.CardUid);
                    HotCombatEngine.ExecuteDealerRandomEnemyAttack(ref s, ref cold, rules, scratch, dealerSlot: 1, cardIdx: playIdx, cardDefId: cardDefId, baseDamage: ostyDmg, hits: 1, powered: true);
                    s.Counters[(int)HotCounterSlot.OstyAttacksThisTurn]++;
                    NotifyOstyAttacked(ref s, ref cold, rules);
                }
                return Complete(in s, scratch, out result);
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// Dispatches Regent and Necrobinder power hooks. Returns <c>true</c> if handled so
    /// <see cref="HotCombatEngine"/> does not re-execute the same power in its Core fallback.
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
            // =====================================================================
            // REGENT POWERS
            // =====================================================================

            case HotPowerId.OrbitPower:
            {
                if (hook == HotHookMask.AfterEnergySpent && ownerSlot == 0 && args.EnergySpent > 0 && amount > 0)
                {
                    int total = power.Aux0 + args.EnergySpent;
                    int triggers = total / OrbitEnergyThreshold;
                    power.Aux0 = total % OrbitEnergyThreshold;
                    if (triggers > 0)
                        HotCombatEngine.GainEnergy(ref s, rules, amount * triggers);
                    return true;
                }
                return true;
            }

            case HotPowerId.ChildOfTheStarsPower:
            {
                if (hook == HotHookMask.AfterStarsSpent && ownerSlot == 0 && args.StarsSpent > 0 && amount > 0)
                {
                    HotCombatEngine.GainBlock(ref s, ref cold, rules, scratch, 0, amount * args.StarsSpent, isCardBlock: false);
                    return true;
                }
                return true;
            }

            case HotPowerId.BlackHolePower:
            {
                // 0.113.1 BlackHolePower.cs:21-27: fires in AfterCardPlayed (last in series) when StarsSpent > 0.
                // AfterStarsGained is handled directly in HotCombatEngine.Damage.cs:GainStars.
                if (ownerSlot == 0 && amount > 0
                    && hook == HotHookMask.AfterCardPlayed
                    && args.ActorSlot == 0
                    && args.StarsSpent > 0
                    && args.PlayIndex + 1 >= args.PlayCount)
                {
                    HotCombatEngine.DamageAllEnemies(ref s, ref cold, rules, scratch, 0, amount, isPoweredAttack: false);
                }
                return true;
            }

            case HotPowerId.VoidFormPower:
            {
                if (ownerSlot != 0 || amount <= 0)
                    return true;

                switch (hook)
                {
                    case HotHookMask.ModifyEnergyCost:
                    case HotHookMask.ModifyStarCost:
                        if (power.Aux0 < amount)
                            args.IntValue = 0;
                        return true;

                    case HotHookMask.AfterCardPlayed:
                        if (!args.IsAutoPlay && args.PlayIndex + 1 >= args.PlayCount)
                            power.Aux0++;
                        return true;

                    case HotHookMask.BeforeSideTurnStart:
                        if (args.Side == HotCombatSide.Player)
                            power.Aux0 = 0;
                        return true;

                    default:
                        return true;
                }
            }

            case HotPowerId.PaleBlueDotPower:
            {
                if (ownerSlot != 0 || amount <= 0)
                    return true;

                switch (hook)
                {
                    case HotHookMask.AfterCardPlayed:
                        if ((power.Flags & HotPowerFlags.BoolAux) == 0
                            && s.Counters[(int)HotCounterSlot.CardsPlayedThisTurn] >= PaleBlueDotPlayThreshold)
                        {
                            power.Flags |= HotPowerFlags.BoolAux;
                            HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.DrawCardsNextTurnPower, amount, isCardSource: false);
                        }
                        return true;

                    case HotHookMask.AfterTurnEnd:
                    case HotHookMask.AfterSideTurnStart:
                        if (args.Side == HotCombatSide.Player)
                            power.Flags &= ~HotPowerFlags.BoolAux;
                        return true;

                    default:
                        return true;
                }
            }

            case HotPowerId.GenesisPower:
            case HotPowerId.StarNextTurnPower:
            case HotPowerId.RadiancePower:
            {
                // Executed by HotCombatEngine.Turn.cs (TriggerAfterEnergyResetPowers) at the exact
                // STS2 AfterEnergyReset hook before opening hand draw; return true to avoid double-firing.
                return true;
            }

            case HotPowerId.FurnacePower:
            {
                if (hook == HotHookMask.AfterSideTurnStart && ownerSlot == 0 && args.Side == HotCombatSide.Player && amount > 0)
                    Forge(ref s, ref cold, rules, scratch, amount);
                return true;
            }

            case HotPowerId.SwordSagePower:
            {
                if (ownerSlot != 0)
                    return true;

                if (hook == HotHookMask.AfterPowerAmountChanged
                    && args.TargetSlot == 0
                    && args.PowerDefId == power.PowerDefId
                    && args.Amount != 0)
                {
                    TriggerSwordSageNormalization(ref s, rules, args.Amount);
                    return true;
                }

                if (hook == HotHookMask.AfterCardEnteredCombat && (uint)args.CardIdx < (uint)s.TotalCardCount)
                {
                    ref HotCard entered = ref s.Cards[args.CardIdx];
                    if ((entered.Flags & HotCardFlags.IsClone) == 0
                        && entered.CardDefId < rules.Cards.Length
                        && rules.Cards[entered.CardDefId].CardId == HotCustomCardId.SovereignBlade)
                    {
                        int nextReplay = entered.BaseReplayCount + amount;
                        entered.BaseReplayCount = (byte)Math.Clamp(nextReplay, 0, byte.MaxValue);
                    }
                    return true;
                }
                return true;
            }

            case HotPowerId.TheSealedThronePower:
            {
                if (hook == HotHookMask.BeforeCardPlayed && ownerSlot == 0 && args.ActorSlot == 0 && amount > 0)
                    HotCombatEngine.GainStars(ref s, ref cold, rules, scratch, amount);
                return true;
            }

            case HotPowerId.ConquerorPower:
            {
                if (hook == HotHookMask.ModifyDamageMultiplicative
                    && args.TargetSlot == ownerSlot
                    && args.IsPoweredAttack
                    && args.CardDefId < rules.Cards.Length
                    && rules.Cards[args.CardDefId].CardId == HotCustomCardId.SovereignBlade
                    && amount > 0)
                {
                    args.DecimalMultiplier *= 2m;
                    return true;
                }

                if (hook == HotHookMask.AfterTurnEnd && amount > 0)
                {
                    bool isOwnerSide = (ownerSlot == 0 && args.Side == HotCombatSide.Player)
                        || (ownerSlot >= 3 && args.Side == HotCombatSide.Enemy);
                    if (isOwnerSide)
                        HotCombatEngine.SetPowerAmount(ref s, rules, ownerSlot, power.PowerDefId, amount - 1);
                    return true;
                }
                return true;
            }

            case HotPowerId.SpectrumShiftPower:
            {
                if (hook == HotHookMask.BeforeHandDraw && ownerSlot == 0 && amount > 0)
                {
                    GenerateDistinctCardsFromPool(
                        ref s,
                        ref cold,
                        rules,
                        scratch,
                        GetColorlessPool(rules),
                        amount,
                        HotPileKind.Hand,
                        upgraded: false,
                        excludeCardId: HotCustomCardId.None);
                }
                return true;
            }

            case HotPowerId.TyrannyPower:
            {
                if (ownerSlot != 0 || amount <= 0)
                    return true;

                if (hook == HotHookMask.ModifyHandDraw)
                {
                    args.HandDrawCount += amount;
                    return true;
                }
                if (hook == HotHookMask.AfterSideTurnStart && args.Side == HotCombatSide.Player)
                {
                    int picks = Math.Min(amount, (int)s.HandCount);
                    for (int i = 0; i < picks && s.HandCount > 0; i++)
                    {
                        if (!HotCombatEngine.RequestChoice(ref s, scratch, HotChoiceKind.HandCard, 0, 1, 1, out HotChoiceSelection sel))
                            return true;
                        int idx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Hand, sel.CardUidOrIndex);
                        if (idx >= 0)
                            HotCombatEngine.ExhaustCard(ref s, ref cold, rules, scratch, idx);
                    }
                    return true;
                }
                return true;
            }

            case HotPowerId.ArsenalPower:
            {
                if (hook == HotHookMask.AfterCardGenerated && ownerSlot == 0 && args.ActorSlot == 0 && amount > 0)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.StrengthPower, amount, isCardSource: false);
                return true;
            }

            case HotPowerId.PillarOfCreationPower:
            {
                if (hook == HotHookMask.AfterCardGenerated && ownerSlot == 0 && args.ActorSlot == 0 && amount > 0)
                    HotCombatEngine.GainBlock(ref s, ref cold, rules, scratch, 0, amount, isCardBlock: false);
                return true;
            }

            case HotPowerId.MonarchsGazePower:
            {
                if (hook == HotHookMask.AfterDamageGiven
                    && args.ActorSlot == ownerSlot
                    && args.IsPoweredAttack
                    && args.TargetSlot != 0xFF
                    && amount > 0)
                {
                    HotCombatEngine.ApplyTemporaryStat(
                        ref s,
                        ref cold,
                        rules,
                        scratch,
                        args.TargetSlot,
                        ownerSlot,
                        HotPowerId.StrengthPower,
                        HotPowerId.MonarchsGazeStrengthDownPower,
                        -amount);
                }
                return true;
            }

            case HotPowerId.RoyaltiesPower:
            {
                if (hook == HotHookMask.AfterCombatEnd && ownerSlot == 0 && amount > 0)
                {
                    s.Gold += amount;
                    s.LongTermResourceValue += amount;
                    s.Growth[GrowthIdxRoyalties]++;
                }
                return true;
            }

            case HotPowerId.CrueltyPower:
            case HotPowerId.ParryPower:
            case HotPowerId.SeekingEdgePower:
            case HotPowerId.HammerTimePower:
                return true;

            // =====================================================================
            // NECROBINDER POWERS
            // =====================================================================

            case HotPowerId.CalcifyPower:
            {
                if (hook == HotHookMask.ModifyDamageAdditive
                    && ownerSlot == 0
                    && args.ActorSlot == 1
                    && args.IsPoweredAttack
                    && amount > 0)
                {
                    args.DecimalValue += amount;
                }
                return true;
            }

            case HotPowerId.CountdownPower:
            {
                if (hook == HotHookMask.AfterSideTurnStart && ownerSlot == 0 && args.Side == HotCombatSide.Player && amount > 0)
                {
                    int targetEnemy = HotCombatEngine.PickRandomLivingEnemySlot(ref s);
                    if (targetEnemy >= 0)
                        HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, (byte)(3 + targetEnemy), 0, HotPowerId.DoomPower, amount, isCardSource: false);
                }
                return true;
            }

            case HotPowerId.DanseMacabrePower:
            {
                if (hook == HotHookMask.BeforeCardPlayed
                    && ownerSlot == 0
                    && args.ActorSlot == 0
                    && args.EnergySpent >= 2
                    && amount > 0)
                {
                    HotCombatEngine.GainBlock(ref s, ref cold, rules, scratch, 0, amount, isCardBlock: false);
                }
                return true;
            }

            case HotPowerId.DebilitatePower:
            {
                if (hook == HotHookMask.AfterTurnEnd && amount > 0)
                {
                    bool ownerSide = (ownerSlot == 0 && args.Side == HotCombatSide.Player)
                        || (ownerSlot >= 3 && args.Side == HotCombatSide.Enemy);
                    if (ownerSide)
                        HotCombatEngine.SetPowerAmount(ref s, rules, ownerSlot, power.PowerDefId, amount - 1);
                }
                return true;
            }

            case HotPowerId.DemesnePower:
            {
                if (ownerSlot != 0 || amount <= 0)
                    return true;

                if (hook == HotHookMask.ModifyHandDraw)
                {
                    args.HandDrawCount += amount;
                    return true;
                }
                if (hook == HotHookMask.ModifyMaxEnergy)
                {
                    args.MaxEnergy += amount;
                    return true;
                }
                return true;
            }

            case HotPowerId.DoomPower:
            {
                if (amount <= 0)
                    return true;

                if (hook == HotHookMask.BeforeTurnEnd && args.Side == HotCombatSide.Enemy && ownerSlot >= 3)
                {
                    int enemyIdx = ownerSlot - 3;
                    if (enemyIdx < s.EnemyCount
                        && (s.Enemies[enemyIdx].Vital.Flags & HotCreatureFlags.Alive) != 0
                        && s.Enemies[enemyIdx].Vital.CurrentHp <= amount)
                    {
                        ReadOnlySpan<byte> single = stackalloc byte[1] { ownerSlot };
                        DoomKillCreatures(ref s, ref cold, rules, scratch, single);
                    }
                    return true;
                }

                if (hook == HotHookMask.AfterTurnEnd && args.Side == HotCombatSide.Player && ownerSlot == 0)
                {
                    if ((s.Player.Flags & HotCreatureFlags.Alive) != 0 && s.Player.CurrentHp <= amount)
                        HotCombatEngine.KillCreature(ref s, ref cold, rules, scratch, 0);
                    return true;
                }
                return true;
            }

            case HotPowerId.DevourLifePower:
            {
                if (hook == HotHookMask.AfterCardPlayed
                    && ownerSlot == 0
                    && args.ActorSlot == 0
                    && args.CardDefId < rules.Cards.Length
                    && rules.Cards[args.CardDefId].CardId == HotCustomCardId.Soul
                    && amount > 0)
                {
                    SummonOsty(ref s, ref cold, rules, scratch, amount);
                }
                return true;
            }

            case HotPowerId.HauntPower:
            {
                if (hook == HotHookMask.AfterCardPlayed
                    && ownerSlot == 0
                    && args.ActorSlot == 0
                    && args.CardDefId < rules.Cards.Length
                    && rules.Cards[args.CardDefId].CardId == HotCustomCardId.Soul
                    && amount > 0)
                {
                    HotCombatEngine.DamageRandomEnemy(ref s, ref cold, rules, scratch, 0xFF, amount, isPoweredAttack: false, isUnblockable: true);
                }
                return true;
            }

            case HotPowerId.LethalityPower:
            {
                if (hook == HotHookMask.ModifyDamageMultiplicative
                    && ownerSlot == 0
                    && args.IsPoweredAttack
                    && args.CardDefId != 0
                    && args.ActorSlot is 0 or 1
                    && amount > 0)
                {
                    if (scratch.CurrentPlayIndex == 0 && s.Counters[(int)HotCounterSlot.AttackPlayStartsThisTurn] <= 1)
                        args.DecimalMultiplier *= 1m + (decimal)amount / 100m;
                }
                return true;
            }

            case HotPowerId.NecroMasteryPower:
            {
                if (hook == HotHookMask.AfterHpLost
                    && ownerSlot == 0
                    && args.TargetSlot == 1
                    && args.Amount > 0
                    && amount > 0)
                {
                    HotCombatEngine.DamageAllEnemies(ref s, ref cold, rules, scratch, 0, args.Amount * amount, isPoweredAttack: false, isUnblockable: true);
                }
                return true;
            }

            case HotPowerId.NeurosurgePower:
            {
                if (hook == HotHookMask.AfterSideTurnStart && ownerSlot == 0 && args.Side == HotCombatSide.Player && amount > 0)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.DoomPower, amount, isCardSource: false);
                return true;
            }

            case HotPowerId.PagestormPower:
            {
                if (hook == HotHookMask.AfterCardDrawn
                    && ownerSlot == 0
                    && (uint)args.CardIdx < (uint)s.TotalCardCount
                    && HotCombatEngine.IsCardEthereal(in s, in s.Cards[args.CardIdx], rules)
                    && amount > 0)
                {
                    HotCombatEngine.DrawCards(ref s, ref cold, rules, scratch, amount, fromHandDraw: false);
                }
                return true;
            }

            case HotPowerId.ReaperFormPower:
            {
                if (hook == HotHookMask.AfterDamageGiven
                    && ownerSlot == 0
                    && (args.ActorSlot == 0 || args.ActorSlot == 1)
                    && args.IsPoweredAttack
                    && args.TargetSlot >= 3
                    && amount > 0)
                {
                    int totalDmg = args.UnblockedDamage + args.BlockedDamage;
                    if (totalDmg > 0)
                        HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, args.TargetSlot, 0, HotPowerId.DoomPower, totalDmg * amount, isCardSource: false);
                }
                return true;
            }

            case HotPowerId.ShroudPower:
            {
                if (hook == HotHookMask.AfterPowerAmountChanged
                    && ownerSlot == 0
                    && args.ActorSlot == 0
                    && args.PowerDefId == rules.GetPowerDefId(HotPowerId.DoomPower)
                    && args.Amount > 0
                    && amount > 0)
                {
                    HotCombatEngine.GainBlock(ref s, ref cold, rules, scratch, 0, amount, isCardBlock: false);
                }
                return true;
            }

            case HotPowerId.SleightOfFleshPower:
            {
                if (hook == HotHookMask.AfterPowerAmountChanged
                    && ownerSlot == 0
                    && args.ActorSlot == 0
                    && args.TargetSlot >= 3
                    && args.Amount != 0
                    && amount > 0
                    && args.PowerDefId < rules.Powers.Length)
                {
                    HotPowerDef changedDef = rules.Powers[args.PowerDefId];
                    bool isDebuff = (changedDef.Type == PowerType.Debuff && args.Amount > 0)
                        || (changedDef.Type == PowerType.Buff && args.Amount < 0);
                    if (isDebuff && !IsTemporaryPowerId(changedDef.PowerId))
                    {
                        HotCombatEngine.DealDamage(ref s, ref cold, rules, scratch, 0, args.TargetSlot, amount, isPoweredAttack: false);
                    }
                }
                return true;
            }

            case HotPowerId.SoulboundPower:
            {
                if (hook == HotHookMask.AfterCardGenerated
                    && ownerSlot == 0
                    && args.ActorSlot == power.ApplierSlot
                    && args.CardDefId < rules.Cards.Length
                    && rules.Cards[args.CardDefId].CardId == HotCustomCardId.Soul
                    && (power.Flags & HotPowerFlags.BoolAux) == 0
                    && amount > 0)
                {
                    power.Flags |= HotPowerFlags.BoolAux;
                    try
                    {
                        AddSoulsToPile(ref s, ref cold, rules, scratch, amount, HotPileKind.Draw, dstOffset: -2, upgraded: false);
                    }
                    finally
                    {
                        int liveIdx = HotCombatEngine.FindPowerIndex(in s, ownerSlot, power.PowerDefId);
                        if (liveIdx >= 0)
                            s.Powers[liveIdx].Flags &= ~HotPowerFlags.BoolAux;
                    }
                }
                return true;
            }

            case HotPowerId.SpiritOfAshPower:
            {
                if (hook == HotHookMask.BeforeCardPlayed
                    && ownerSlot == 0
                    && args.ActorSlot == 0
                    && (uint)args.CardIdx < (uint)s.TotalCardCount
                    && HotCombatEngine.IsCardEthereal(in s, in s.Cards[args.CardIdx], rules)
                    && amount > 0)
                {
                    HotCombatEngine.GainBlock(ref s, ref cold, rules, scratch, 0, amount, isCardBlock: false);
                }
                return true;
            }

            case HotPowerId.SummonNextTurnPower:
            {
                if (hook == HotHookMask.AfterSideTurnStart
                    && ownerSlot == 0
                    && args.Side == HotCombatSide.Player
                    && amount > 0)
                {
                    ushort defId = power.PowerDefId;
                    SummonOsty(ref s, ref cold, rules, scratch, amount);
                    if (!scratch.NeedsChoice)
                        HotCombatEngine.SetPowerAmount(ref s, rules, 0, defId, 0);
                }
                return true;
            }

            case HotPowerId.TheGambitPower:
            {
                if (hook == HotHookMask.AfterDamageReceived
                    && args.TargetSlot == ownerSlot
                    && args.IsPoweredAttack
                    && args.UnblockedDamage > 0
                    && amount > 0)
                {
                    HotCombatEngine.SetPowerAmount(ref s, rules, ownerSlot, power.PowerDefId, 0);
                    HotCombatEngine.KillCreature(ref s, ref cold, rules, scratch, ownerSlot);
                }
                return true;
            }

            case HotPowerId.VeilpiercerPower:
            {
                if (ownerSlot != 0 || amount <= 0)
                    return true;

                if (hook == HotHookMask.ModifyEnergyCost
                    && (uint)args.CardIdx < (uint)s.TotalCardCount
                    && HotCombatEngine.IsCardEthereal(in s, in s.Cards[args.CardIdx], rules))
                {
                    args.IntValue = 0;
                    return true;
                }
                if (hook == HotHookMask.BeforeCardPlayed
                    && args.ActorSlot == 0
                    && (uint)args.CardIdx < (uint)s.TotalCardCount
                    && HotCombatEngine.IsCardEthereal(in s, in s.Cards[args.CardIdx], rules))
                {
                    HotCombatEngine.SetPowerAmount(ref s, rules, 0, power.PowerDefId, amount - 1);
                    return true;
                }
                return true;
            }

            case HotPowerId.SandpitPower:
            {
                // The Insatiable's Sandpit: owned by enemy, targets player (TargetSlot == 0).
                // At start of enemy turn (AfterSideTurnStart) decrements by 1; if reaches 0 kills target (player).
                if (hook == HotHookMask.AfterSideTurnStart && args.Side == HotCombatSide.Enemy && ownerSlot >= 3 && amount > 0)
                {
                    int next = amount - 1;
                    HotCombatEngine.SetPowerAmount(ref s, rules, ownerSlot, power.PowerDefId, next);
                    if (next <= 0 && (s.Player.Flags & HotCreatureFlags.Alive) != 0)
                        HotCombatEngine.KillCreature(ref s, ref cold, rules, scratch, targetSlot: 0, force: true);
                }
                return true;
            }

            case HotPowerId.SicEmPower:
            {
                if (hook == HotHookMask.AfterDamageGiven
                    && args.ActorSlot == 1
                    && args.TargetSlot == ownerSlot
                    && amount > 0)
                {
                    SummonOsty(ref s, ref cold, rules, scratch, amount);
                    return true;
                }
                if (hook == HotHookMask.AfterTurnEnd && amount > 0)
                {
                    bool ownerSide = (ownerSlot == 0 && args.Side == HotCombatSide.Player)
                        || (ownerSlot >= 3 && args.Side == HotCombatSide.Enemy);
                    if (ownerSide)
                        HotCombatEngine.SetPowerAmount(ref s, rules, ownerSlot, power.PowerDefId, 0);
                    return true;
                }
                return true;
            }

            case HotPowerId.OblivionPower:
            {
                if (hook == HotHookMask.BeforeCardPlayed && args.ActorSlot == power.ApplierSlot && amount > 0)
                {
                    power.Aux0 = amount;
                    return true;
                }
                if (hook == HotHookMask.AfterCardPlayed && args.ActorSlot == power.ApplierSlot && power.Aux0 > 0)
                {
                    int doomAmt = power.Aux0;
                    power.Aux0 = 0;
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, ownerSlot, power.ApplierSlot, HotPowerId.DoomPower, doomAmt, isCardSource: false);
                    return true;
                }
                if (hook == HotHookMask.AfterTurnEnd && args.Side == HotCombatSide.Player && amount > 0)
                {
                    HotCombatEngine.SetPowerAmount(ref s, rules, ownerSlot, power.PowerDefId, 0);
                    return true;
                }
                return true;
            }

            case HotPowerId.ForbiddenGrimoirePower:
            {
                if (hook == HotHookMask.AfterCombatEnd && ownerSlot == 0 && amount > 0)
                {
                    s.LongTermResourceValue += 50;
                    s.Growth[GrowthIdxForbiddenGrimoire]++;
                }
                return true;
            }

            default:
                return false;
        }
    }

    // =========================================================================
    // SHARED CROSS-MODULE HELPERS (Forge, Osty, DoomKill, Fatal Death)
    // =========================================================================

    /// <summary>
    /// Exact match of STS2 0.113.1 <c>MakeItSo.AfterCardPlayedLate</c>: whenever the player
    /// finishes playing every 3rd Skill this turn, moves every <c>MakeItSo</c> card currently
    /// outside Hand (Draw, Discard, Exhaust) into Hand (or Discard if Hand is full).
    /// Called by <c>HotCombatEngine.CardPlay</c> after <c>SkillsPlayedThisTurn</c> is incremented.
    /// </summary>
    internal static void OnPlayerSkillFinished(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch)
    {
        if (HotCombatEngine.IsOverOrEnding(in s) || scratch.NeedsChoice)
            return;

        int skillsFinished = s.Counters[(int)HotCounterSlot.SkillsPlayedThisTurn];
        if (skillsFinished <= 0)
            return;

        ushort makeItSoDefId = rules.GetCardDefId(HotCustomCardId.MakeItSo);
        if (makeItSoDefId == 0)
            return;

        HotCardDef def = rules.Cards[makeItSoDefId];
        Span<ushort> candidateUids = stackalloc ushort[HotLimits.MaxCards];
        int candCount = 0;

        // Collect non-Hand, non-Play MakeItSo cards in Draw -> Discard -> Exhaust order.
        int drawStart = s.DrawStart;
        int drawEnd = drawStart + s.DrawCount;
        for (int i = drawStart; i < drawEnd; i++)
        {
            ref readonly HotCard c = ref s.Cards[i];
            if (c.CardDefId == makeItSoDefId)
            {
                int threshold = Math.Max(1, GetVarInt(in c, def, "Cards", 3));
                if (skillsFinished % threshold == 0)
                    candidateUids[candCount++] = c.CardUid;
            }
        }

        int discStart = s.DiscardStart;
        int discEnd = discStart + s.DiscardCount;
        for (int i = discStart; i < discEnd; i++)
        {
            ref readonly HotCard c = ref s.Cards[i];
            if (c.CardDefId == makeItSoDefId)
            {
                int threshold = Math.Max(1, GetVarInt(in c, def, "Cards", 3));
                if (skillsFinished % threshold == 0)
                    candidateUids[candCount++] = c.CardUid;
            }
        }

        int exhStart = s.ExhaustStart;
        int exhEnd = exhStart + s.ExhaustCount;
        for (int i = exhStart; i < exhEnd; i++)
        {
            ref readonly HotCard c = ref s.Cards[i];
            if (c.CardDefId == makeItSoDefId)
            {
                int threshold = Math.Max(1, GetVarInt(in c, def, "Cards", 3));
                if (skillsFinished % threshold == 0)
                    candidateUids[candCount++] = c.CardUid;
            }
        }

        for (int k = 0; k < candCount && !HotCombatEngine.IsOverOrEnding(in s) && !scratch.NeedsChoice; k++)
        {
            int liveIdx = HotCombatEngine.FindCardIndexByUidAnyPile(in s, candidateUids[k]);
            if (liveIdx < 0 || HotPiles.PileOfIndex(in s, liveIdx) == HotPileKind.Hand)
                continue;
            HotCombatEngine.MoveCardToHandOrDiscard(ref s, liveIdx);
        }
    }

    /// <summary>
    /// Exact match of STS2 0.113.1 <c>Bombardment.AfterAutoPrePlayPhaseEnteredEarly</c>:
    /// auto-plays every <c>Bombardment</c> currently in the Exhaust pile at player turn start
    /// (after opening hand draw and before <c>MayhemPower</c>).
    /// </summary>
    internal static void TriggerExhaustedBombardments(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch)
    {
        if (HotCombatEngine.IsOverOrEnding(in s) || scratch.NeedsChoice || s.ExhaustCount == 0)
            return;

        ushort bombDefId = rules.GetCardDefId(HotCustomCardId.Bombardment);
        if (bombDefId == 0)
            return;

        Span<ushort> bombUids = stackalloc ushort[HotLimits.MaxCards];
        int bombCount = 0;
        int exhStart = s.ExhaustStart;
        int exhEnd = exhStart + s.ExhaustCount;
        for (int i = exhStart; i < exhEnd; i++)
        {
            if (s.Cards[i].CardDefId == bombDefId)
                bombUids[bombCount++] = s.Cards[i].CardUid;
        }

        for (int k = 0; k < bombCount && !HotCombatEngine.IsOverOrEnding(in s) && !scratch.NeedsChoice; k++)
        {
            int liveIdx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Exhaust, bombUids[k]);
            if (liveIdx < 0)
                continue;
            HotCombatEngine.AutoPlayCard(ref s, ref cold, rules, scratch, liveIdx, targetEnemySlot: -1, forceExhaust: false);
        }
    }

    /// <summary>
    /// Exact match of <c>PersistentPowerSupport.Forge</c>: if no unexhausted non-dupe
    /// SovereignBlade exists, generates one into Hand (<c>SovereignBladeCreatedThroughForge</c>),
    /// then increments <c>Var0Delta</c> by <paramref name="amount"/> on all non-dupe SovereignBlades.
    /// </summary>
    internal static void Forge(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int amount)
    {
        if (scratch.NeedsChoice || HotCombatEngine.IsOverOrEnding(in s))
            return;

        ushort bladeDefId = rules.GetCardDefId(HotCustomCardId.SovereignBlade);
        if (bladeDefId == 0)
            return;

        bool hasUnexhaustedBlade = false;
        int total = s.TotalCardCount;
        int exhaustStart = s.ExhaustStart;
        int exhaustEnd = exhaustStart + s.ExhaustCount;
        for (int i = 0; i < total; i++)
        {
            if (i >= exhaustStart && i < exhaustEnd)
                continue;
            ref readonly HotCard c = ref s.Cards[i];
            if (c.CardDefId == bladeDefId && (c.Flags & HotCardFlags.IsDupe) == 0)
            {
                hasUnexhaustedBlade = true;
                break;
            }
        }

        if (!hasUnexhaustedBlade)
        {
            HotCardDef bladeDef = rules.Cards[bladeDefId];
            int swordSage = HotCombatEngine.GetPowerAmountById(in s, rules, 0, HotPowerId.SwordSagePower);
            HotCard created = new()
            {
                CardDefId = bladeDefId,
                CardUid = s.NextCardUid++,
                UpgradeLevel = 0,
                BaseEnergyCost = bladeDef.BaseCost,
                BaseStarCost = bladeDef.BaseStarCost,
                BaseReplayCount = (byte)Math.Clamp(swordSage, 0, byte.MaxValue),
                ExtraReplayThisTurn = 0,
                LocalKeywordsMask = (HotCardKeywordMask)bladeDef.BaseKeywords,
                KeywordsAddedOutsideMask = HotCardKeywordMask.None,
                KeywordsRemovedOutsideMask = HotCardKeywordMask.None,
                Flags = HotCardFlags.SovereignBladeCreatedThroughForge,
            };
            HotCombatEngine.AddGeneratedCardInstance(ref s, ref cold, rules, scratch, in created, HotPileKind.Hand, dstOffset: -1, createdByPlayer: true);
            if (scratch.NeedsChoice)
                return;
        }

        if (amount != 0)
        {
            int afterTotal = s.TotalCardCount;
            for (int i = 0; i < afterTotal; i++)
            {
                ref HotCard c = ref s.Cards[i];
                if (c.CardDefId == bladeDefId && (c.Flags & HotCardFlags.IsDupe) == 0)
                {
                    c.Var0Delta = (short)(c.Var0Delta + amount);
                    c.CardPrivate += amount;
                }
            }
        }
    }

    /// <summary>
    /// Exact match of <c>SimulatedCombatState.SummonOsty</c>: summons Osty (slot 1) with
    /// <c>DieForYouPower(1)</c> or raises existing live Osty's MaxHp &amp; CurrentHp by <paramref name="amount"/>.
    /// </summary>
    internal static void SummonOsty(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int amount)
    {
        if (amount <= 0)
            return;

        bool existed = (s.CombatFlags & HotCombatFlags.HasOsty) != 0;
        bool wasAlive = existed && (s.Osty.Flags & HotCreatureFlags.Alive) != 0;

        if (!existed)
        {
            s.CombatFlags |= HotCombatFlags.HasOsty;
            s.Osty = new HotCreature
            {
                CombatId = s.NextCreatureId++,
                CurrentHp = amount,
                MaxHp = amount,
                Block = 0,
                Flags = HotCreatureFlags.Alive | HotCreatureFlags.IsOsty | HotCreatureFlags.IsPet,
                SlotIdx = 1,
            };
            HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, ownerSlot: 1, applierSlot: 1, HotPowerId.DieForYouPower, 1, isCardSource: false);
        }
        else if (wasAlive)
        {
            s.Osty.MaxHp += amount;
            s.Osty.CurrentHp = Math.Min(s.Osty.MaxHp, s.Osty.CurrentHp + amount);
        }
        else
        {
            // Reviving dead Osty resets MaxHp and CurrentHp to amount and restores DieForYouPower.
            s.Osty.Flags |= HotCreatureFlags.Alive;
            s.Osty.MaxHp = amount;
            s.Osty.CurrentHp = amount;
            s.Osty.Block = 0;
            if (HotCombatEngine.GetPowerAmountById(in s, rules, 1, HotPowerId.DieForYouPower) <= 0)
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, ownerSlot: 1, applierSlot: 1, HotPowerId.DieForYouPower, 1, isCardSource: false);
        }
    }

    internal static void HealOsty(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int amount)
    {
        if (amount <= 0 || !IsOstyAlive(in s))
            return;
        HotCombatEngine.HealCreature(ref s, ref cold, rules, scratch, targetSlot: 1, amount: amount);
    }

    /// <summary>
    /// Exact match of <c>SimulatedCombatState.DoomKill</c>: counts fatal non-player kills
    /// before killing each doomed creature, then triggers <c>BookRepairKnife</c> healing.
    /// </summary>
    internal static void DoomKillCreatures(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        ReadOnlySpan<byte> creatureSlots)
    {
        if (creatureSlots.IsEmpty)
            return;

        int fatalCount = 0;
        for (int i = 0; i < creatureSlots.Length; i++)
        {
            byte slot = creatureSlots[i];
            if (slot != 0 && ShouldDeathTriggerFatal(in s, rules, slot))
                fatalCount++;
        }

        for (int i = 0; i < creatureSlots.Length; i++)
        {
            HotCombatEngine.KillCreature(ref s, ref cold, rules, scratch, creatureSlots[i]);
            if (scratch.NeedsChoice)
                return;
        }

        if (fatalCount > 0 && rules.HasRelic(HotRelicId.BookRepairKnife))
        {
            ushort relicDefId = rules.GetRelicDefId(HotRelicId.BookRepairKnife);
            int healPerKill = relicDefId != 0 && rules.Relics[relicDefId].PrimaryValue > 0
                ? rules.Relics[relicDefId].PrimaryValue
                : 7;
            HotCombatEngine.HealCreature(ref s, ref cold, rules, scratch, targetSlot: 0, amount: healPerKill * fatalCount);
        }
    }

    /// <summary>
    /// Invoked by <see cref="HotMonsters.HandleEnemyDeathEffect"/> on fatal enemy death to trigger
    /// Necrobinder card listeners such as <c>Melancholy</c>.
    /// </summary>
    internal static void OnEnemyFatalDeath(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int enemySlot)
        => OnAnyCreatureDeath(ref s, ref cold, rules);

    internal static void OnAnyCreatureDeath(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules)
    {
        ushort melancholyId = rules.GetCardDefId(HotCustomCardId.Melancholy);
        if (melancholyId == 0)
            return;

        int total = s.TotalCardCount;
        for (int i = 0; i < total; i++)
        {
            if (s.Cards[i].CardDefId == melancholyId)
                HotCombatEngine.CostAddThisCombat(ref s.Cards[i], ref cold, -1);
        }
    }

    internal static void TriggerSwordSageNormalization(ref CombatHotState s, HotRules rules, int delta)
    {
        if (delta == 0)
            return;
        ushort bladeDefId = rules.GetCardDefId(HotCustomCardId.SovereignBlade);
        if (bladeDefId == 0)
            return;

        int total = s.TotalCardCount;
        for (int i = 0; i < total; i++)
        {
            ref HotCard c = ref s.Cards[i];
            if (c.CardDefId == bladeDefId)
                c.BaseReplayCount = (byte)Math.Clamp(c.BaseReplayCount + delta, 0, byte.MaxValue);
        }
    }

    // =========================================================================
    // INTERNAL ZERO-ALLOC UTILITIES
    // =========================================================================

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
    private static bool IsOstyAlive(in CombatHotState s)
        => (s.CombatFlags & HotCombatFlags.HasOsty) != 0 && (s.Osty.Flags & HotCreatureFlags.Alive) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ResolveEnergyX(in CombatHotState s, HotRules rules, in HotCard card)
    {
        int x = card.CapturedEnergy;
        if (rules.HasRelic(HotRelicId.ChemicalX))
            x += 2;
        return Math.Max(0, x);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ResolveStarX(in CombatHotState s, HotRules rules, HotExecScratch scratch)
    {
        int x = scratch.LastStarsSpent;
        if (rules.HasRelic(HotRelicId.ChemicalX))
            x += 2;
        return Math.Max(0, x);
    }

    internal static int GetVarInt(in HotCard card, HotCardDef def, string name, int fallback = 0)
    {
        string[] names = def.OrderedVarNames;
        for (int i = 0; i < names.Length; i++)
        {
            if (string.Equals(names[i], name, StringComparison.Ordinal))
                return (int)HotRules.GetCardVarValue(in card, def, i);
        }
        return fallback;
    }

    private static void ExecutePlayedCardSingleAttack(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        ushort cardUid,
        ushort cardDefId,
        int targetEnemySlot,
        int dmg,
        int hits = 1)
    {
        if (hits <= 0 || !HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
            return;
        int playIdx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Play, cardUid);
        HotCombatEngine.ExecuteDealerSingleTargetAttack(
            ref s, ref cold, rules, scratch,
            dealerSlot: 0, cardIdx: playIdx, cardDefId: cardDefId,
            targetEnemySlot: targetEnemySlot, baseDamage: dmg, hits: hits, powered: true);
    }

    private static void ExecutePlayedCardAllEnemiesAttack(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        ushort cardUid,
        ushort cardDefId,
        int dmg,
        int hits = 1)
    {
        if (hits <= 0)
            return;
        int playIdx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Play, cardUid);
        HotCombatEngine.ExecuteDealerAllEnemiesAttack(
            ref s, ref cold, rules, scratch,
            dealerSlot: 0, cardIdx: playIdx, cardDefId: cardDefId,
            baseDamage: dmg, hits: hits, powered: true);
    }

    private static void ExecutePlayedCardRandomEnemyAttack(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        ushort cardUid,
        ushort cardDefId,
        int dmg,
        int hits = 1)
    {
        if (hits <= 0)
            return;
        int playIdx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Play, cardUid);
        HotCombatEngine.ExecuteDealerRandomEnemyAttack(
            ref s, ref cold, rules, scratch,
            dealerSlot: 0, cardIdx: playIdx, cardDefId: cardDefId,
            baseDamage: dmg, hits: hits, powered: true);
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

    private static void NotifyOstyAttacked(ref CombatHotState s, ref HotColdSidecar? cold, HotRules rules)
    {
        ushort flattenId = rules.GetCardDefId(HotCustomCardId.Flatten);
        if (flattenId == 0)
            return;

        int total = s.TotalCardCount;
        for (int i = 0; i < total; i++)
        {
            if (s.Cards[i].CardDefId == flattenId)
            {
                HotCostModifiers.AddEnergyModifierExact(
                    ref s.Cards[i],
                    ref cold,
                    amount: 0,
                    isAbsolute: true,
                    expiration: (int)LocalCostModifierExpiration.EndOfTurn,
                    isReduceOnly: false);
            }
        }
    }

    private static void ApplyPowerAllEnemies(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotPowerId powerId,
        int amount,
        bool isCardSource)
    {
        Span<byte> enemies = stackalloc byte[HotLimits.MaxEnemies];
        int count = SnapshotLivingEnemies(in s, enemies);
        for (int i = 0; i < count && !scratch.NeedsChoice; i++)
        {
            byte enemySlot = enemies[i];
            if ((s.Enemies[enemySlot].Vital.Flags & HotCreatureFlags.Alive) == 0)
                continue;
            HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, (byte)(3 + enemySlot), 0, powerId, amount, isCardSource: isCardSource);
        }
    }

    private static void ApplyTempStrengthLossAllEnemies(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotPowerId tempPowerId,
        int amount)
    {
        Span<byte> enemies = stackalloc byte[HotLimits.MaxEnemies];
        int count = SnapshotLivingEnemies(in s, enemies);
        for (int i = 0; i < count && !scratch.NeedsChoice; i++)
        {
            byte enemySlot = enemies[i];
            if ((s.Enemies[enemySlot].Vital.Flags & HotCreatureFlags.Alive) == 0)
                continue;
            HotCombatEngine.ApplyTemporaryStat(
                ref s,
                ref cold,
                rules,
                scratch,
                ownerSlot: (byte)(3 + enemySlot),
                applierSlot: 0,
                baseStatPowerId: HotPowerId.StrengthPower,
                tempPowerId: tempPowerId,
                signedDelta: -amount);
        }
    }

    private static int SnapshotLivingEnemies(in CombatHotState s, Span<byte> dst)
    {
        int count = 0;
        int rosterCount = s.RosterCount;
        for (int i = 0; i < rosterCount; i++)
        {
            byte enemySlot = s.ActiveEnemyRoster[i];
            if ((s.Enemies[enemySlot].Vital.Flags & HotCreatureFlags.Alive) != 0)
                dst[count++] = enemySlot;
        }
        return count;
    }

    private static int SnapshotTargetDebuffs(
        in CombatHotState s,
        HotRules rules,
        byte targetSlot,
        Span<ushort> defIds,
        Span<int> amounts,
        Span<byte> appliers)
    {
        int count = 0;
        int powerCount = s.PowerCount;
        for (int i = 0; i < powerCount; i++)
        {
            ref readonly HotPower p = ref s.Powers[i];
            if (p.OwnerSlot != targetSlot || p.Amount == 0 || p.PowerDefId >= rules.Powers.Length)
                continue;
            HotPowerDef def = rules.Powers[p.PowerDefId];
            bool isDebuff = (def.Type == PowerType.Debuff && p.Amount > 0)
                || (def.Type == PowerType.Buff && p.Amount < 0);
            if (!isDebuff)
                continue;
            defIds[count] = p.PowerDefId;
            amounts[count] = p.Amount;
            appliers[count] = p.ApplierSlot;
            count++;
        }
        return count;
    }

    private static bool ShouldDeathTriggerFatal(in CombatHotState s, HotRules rules, byte creatureSlot)
    {
        int powerCount = s.PowerCount;
        for (int i = 0; i < powerCount; i++)
        {
            ref readonly HotPower p = ref s.Powers[i];
            if (p.OwnerSlot != creatureSlot || p.Amount == 0 || p.PowerDefId >= rules.Powers.Length)
                continue;
            HotPowerId id = rules.Powers[p.PowerDefId].PowerId;
            if (id is HotPowerId.MinionPower or HotPowerId.ReattachPower or HotPowerId.AdaptablePower or HotPowerId.IllusionPower)
                return false;
        }
        return true;
    }

    private static bool IsTemporaryPowerId(HotPowerId id)
        => id is HotPowerId.CrushUnderPower
            or HotPowerId.DyingStarPower
            or HotPowerId.ManglePower
            or HotPowerId.DarkShacklesPower
            or HotPowerId.EnfeeblingTouchPower
            or HotPowerId.MonarchsGazeStrengthDownPower
            or HotPowerId.PiercingWailPower
            or HotPowerId.ShacklingPotionPower
            or HotPowerId.SetupStrikePower
            or HotPowerId.FlexPotionPower
            or HotPowerId.SpeedPotionPower
            or HotPowerId.ReptileTrinketPower
            or HotPowerId.FeedingFrenzyPower
            or HotPowerId.AnticipatePower
            or HotPowerId.FocusedStrikePower
            or HotPowerId.HotfixPower
            or HotPowerId.HyperbeamFocusDownPower
            or HotPowerId.SynchronizePower;

    private static void AddSoulsToPile(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int count,
        HotPileKind dstPile,
        int dstOffset,
        bool upgraded)
    {
        if (count <= 0)
            return;
        ushort soulId = rules.GetCardDefId(HotCustomCardId.Soul);
        if (soulId == 0)
            return;
        for (int i = 0; i < count && !scratch.NeedsChoice && !HotCombatEngine.IsOverOrEnding(in s); i++)
            HotCombatEngine.AddGeneratedFixedCard(ref s, ref cold, rules, scratch, soulId, dstPile, dstOffset, upgraded, createdByPlayer: true);
    }

    private static void TransformCardAt(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int cardIdx,
        ushort replacementDefId,
        bool upgraded)
    {
        if (replacementDefId == 0 || replacementDefId >= rules.Cards.Length)
            return;
        HotPileKind pile = HotPiles.PileOfIndex(in s, cardIdx);
        int offset = cardIdx - HotPiles.ZoneStart(in s, pile);
        HotPiles.RemoveCard(ref s, cardIdx);
        HotCombatEngine.AddGeneratedFixedCard(ref s, ref cold, rules, scratch, replacementDefId, pile, offset, upgraded, createdByPlayer: true);
    }

    private static void AddClonedCardInstance(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        in HotCard source,
        HotPileKind dstPile)
    {
        HotCard clone = source;
        clone.CardUid = s.NextCardUid++;
        clone.Flags &= ~(HotCardFlags.HasDeckVersion | HotCardFlags.RemovedFromState | HotCardFlags.ExhaustOnNextPlay);
        clone.Flags |= HotCardFlags.IsClone;
        HotCombatEngine.AddGeneratedCardInstance(ref s, ref cold, rules, scratch, in clone, dstPile, dstOffset: -1, createdByPlayer: true);
    }

    private static void MoveCardToHandOrDiscard(ref CombatHotState s, int cardIdx)
    {
        int maxHand = s.MaxHandSize > 0 ? s.MaxHandSize : 10;
        if (s.HandCount < maxHand)
            HotPiles.MoveCard(ref s, cardIdx, HotPileKind.Hand, s.HandCount);
        else
            HotPiles.MoveCard(ref s, cardIdx, HotPileKind.Discard, s.DiscardCount);
    }

    private static void MoveSovereignBladesToHand(ref CombatHotState s, HotRules rules)
    {
        ushort bladeDefId = rules.GetCardDefId(HotCustomCardId.SovereignBlade);
        if (bladeDefId == 0)
            return;

        Span<ushort> bladeUids = stackalloc ushort[HotLimits.MaxCards];
        int count = 0;
        int total = s.TotalCardCount;
        for (int i = s.HandCount; i < total; i++)
        {
            if (s.Cards[i].CardDefId == bladeDefId)
                bladeUids[count++] = s.Cards[i].CardUid;
        }
        int maxHand = s.MaxHandSize > 0 ? s.MaxHandSize : 10;
        for (int i = 0; i < count; i++)
        {
            if (s.HandCount >= maxHand)
                break;
            int idx = HotCombatEngine.FindCardIndexByUidAnyPile(in s, bladeUids[i]);
            if (idx >= s.HandCount)
                HotPiles.MoveCard(ref s, idx, HotPileKind.Hand, s.HandCount);
        }
    }

    private static bool PutHandCardsOnDrawTop(
        ref CombatHotState s,
        HotExecScratch scratch,
        ushort sourceCardDefId,
        int putBack)
    {
        if (putBack <= 0)
            return true;
        int actual = Math.Min(putBack, HotLimits.MaxChoicesPerAction);
        Span<ushort> uids = stackalloc ushort[actual];
        for (int i = 0; i < actual; i++)
        {
            if (!HotCombatEngine.RequestChoice(ref s, scratch, HotChoiceKind.HandCard, sourceCardDefId, 1, 1, out HotChoiceSelection sel))
                return false;
            uids[i] = sel.CardUidOrIndex;
        }
        for (int i = actual - 1; i >= 0; i--)
        {
            int idx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Hand, uids[i]);
            if (idx >= 0)
                HotPiles.MoveCard(ref s, idx, HotPileKind.Draw, 0);
        }
        return true;
    }

    private static void UpgradeRandomDiscardCards(ref CombatHotState s, HotRules rules, int count)
    {
        if (count <= 0 || s.DiscardCount == 0)
            return;

        Span<int> candidates = stackalloc int[HotLimits.MaxCards];
        int candCount = 0;
        int start = s.DiscardStart;
        int end = start + s.DiscardCount;
        for (int i = start; i < end; i++)
        {
            ushort defId = s.Cards[i].CardDefId;
            if (defId < rules.Cards.Length && s.Cards[i].UpgradeLevel < rules.Cards[defId].MaxUpgradeLevel)
                candidates[candCount++] = i;
        }
        if (candCount == 0)
            return;

        HotRng.UnstableShuffle(candidates[..candCount], ref s.Rng[(int)HotRngStream.CombatCardSelection]);
        int toUpgrade = Math.Min(count, candCount);
        for (int i = 0; i < toUpgrade; i++)
            HotCombatEngine.UpgradeCard(ref s, rules, candidates[i]);
    }

    private static int ComputeModifiedBlock(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte targetSlot,
        int baseAmount,
        bool isCardBlock)
    {
        if (baseAmount <= 0)
            return 0;

        decimal value = baseAmount;
        if (isCardBlock)
        {
            int dex = HotCombatEngine.GetPowerAmount(in s, targetSlot, rules.DexterityPowerDefId);
            value += dex;
            if (rules.FrailPowerDefId != 0 && HotCombatEngine.GetPowerAmount(in s, targetSlot, rules.FrailPowerDefId) > 0)
                value *= 0.75m;
        }
        return Math.Max(0, (int)Math.Floor(value));
    }

    private static ReadOnlySpan<ushort> GetColorlessPool(HotRules rules)
        => rules.CombatColorlessPool.Length > 0 ? rules.CombatColorlessPool : rules.UnlockedColorlessCardPool;

    internal static int RollDistinctPoolCards(
        ref CombatHotState s,
        ReadOnlySpan<ushort> pool,
        Span<ushort> dst,
        HotCustomCardId excludeCardId,
        HotRules rules)
    {
        if (pool.IsEmpty || dst.IsEmpty)
            return 0;

        Span<ushort> eligible = pool.Length <= HotLimits.MaxStackAllocElements
            ? stackalloc ushort[pool.Length]
            : new ushort[pool.Length];
        int eligCount = 0;
        for (int i = 0; i < pool.Length; i++)
        {
            ushort defId = pool[i];
            if (defId == 0 || defId >= rules.Cards.Length)
                continue;
            if (excludeCardId != HotCustomCardId.None && rules.Cards[defId].CardId == excludeCardId)
                continue;
            eligible[eligCount++] = defId;
        }

        if (eligCount == 0)
            return 0;

        ref HotRngState rng = ref s.Rng[(int)HotRngStream.CombatCardGeneration];
        HotRng.UnstableShuffle(eligible[..eligCount], ref rng);
        int take = Math.Min(dst.Length, eligCount);
        eligible[..take].CopyTo(dst);
        return take;
    }

    internal static void GenerateDistinctCardsFromPool(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        ReadOnlySpan<ushort> pool,
        int count,
        HotPileKind dstPile,
        bool upgraded,
        HotCustomCardId excludeCardId)
    {
        if (count <= 0)
            return;
        Span<ushort> rolled = count <= 16 ? stackalloc ushort[count] : new ushort[count];
        int rolledCount = RollDistinctPoolCards(ref s, pool, rolled, excludeCardId, rules);
        for (int i = 0; i < rolledCount && !scratch.NeedsChoice && !HotCombatEngine.IsOverOrEnding(in s); i++)
            HotCombatEngine.AddGeneratedFixedCard(ref s, ref cold, rules, scratch, rolled[i], dstPile, dstOffset: -1, upgraded: upgraded, createdByPlayer: true);
    }

    private static int CountCardsWithStarCost(in CombatHotState s, HotRules rules)
    {
        int count = 0;
        int total = s.TotalCardCount;
        for (int i = 0; i < total; i++)
        {
            ref readonly HotCard c = ref s.Cards[i];
            if (c.CardDefId < rules.Cards.Length)
            {
                if (rules.Cards[c.CardDefId].BaseStarCost >= 0 || (c.Flags & HotCardFlags.HasStarCostX) != 0)
                    count++;
            }
        }
        return count;
    }

    private static int CountSoulsInExhaust(in CombatHotState s, HotRules rules)
    {
        ushort soulId = rules.GetCardDefId(HotCustomCardId.Soul);
        if (soulId == 0)
            return 0;
        int count = 0;
        int start = s.ExhaustStart;
        int end = start + s.ExhaustCount;
        for (int i = start; i < end; i++)
        {
            if (s.Cards[i].CardDefId == soulId)
                count++;
        }
        return count;
    }

    private static int CountOtherOstyAttackCards(in CombatHotState s, HotRules rules, ushort selfCardUid)
    {
        int count = 0;
        int total = s.TotalCardCount;
        for (int i = 0; i < total; i++)
        {
            ref readonly HotCard c = ref s.Cards[i];
            if (c.CardUid != selfCardUid && c.CardDefId < rules.Cards.Length && rules.Cards[c.CardDefId].IsOstyAttackTag)
                count++;
        }
        return count;
    }

    private static bool HasPlayableSkillInHand(in CombatHotState s, HotRules rules)
    {
        for (int i = 0; i < s.HandCount; i++)
        {
            ref readonly HotCard c = ref s.Cards[i];
            if (c.CardDefId < rules.Cards.Length
                && rules.Cards[c.CardDefId].Type == CardType.Skill
                && (c.LocalKeywordsMask & HotCardKeywordMask.Unplayable) == 0)
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasColorlessCardInHand(in CombatHotState s, HotRules rules)
    {
        for (int i = 0; i < s.HandCount; i++)
        {
            ushort defId = s.Cards[i].CardDefId;
            if (defId < rules.Cards.Length && rules.Cards[defId].Domain == HotCardDomain.Colorless)
                return true;
        }
        return false;
    }

    private static bool HasHandCardWithoutKeyword(in CombatHotState s, HotCardKeywordMask kw)
    {
        for (int i = 0; i < s.HandCount; i++)
        {
            if ((s.Cards[i].LocalKeywordsMask & kw) == 0)
                return true;
        }
        return false;
    }
}
