using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Potions;

namespace CombatSolver.HotKernel;

/// <summary>
/// Zero-allocation STS2 0.113.1 potion execution covering all 67 vanilla potions
/// (<see cref="HotCustomPotionId"/> 1..67) plus automatic <c>FairyInABottle</c> death-save revival.
/// Delegates bytecode-compiled potions to <see cref="HotCombatEngine.ExecutePotionBytecode"/> when
/// <c>OpCount &gt; 0</c> and provides complete custom implementations for every potion so none fall through.
/// </summary>
internal static class HotPotions
{
    private const int MaxStackPoolCards = 512;
    private const int MaxStackPotions = 128;

    public static HotStepResult UsePotion(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int slot,
        int targetEnemySlot)
    {
        if (!HotCombatEngine.IsInProgress(in s) || s.TerminalOutcome != HotCombatOutcome.Ongoing)
            return new HotStepResult(Executed: false, NeedsChoice: false, Outcome: s.TerminalOutcome);

        if (s.CurrentSide != HotCombatSide.Player || (s.Player.Flags & HotCreatureFlags.Alive) == 0)
            return new HotStepResult(Executed: false, NeedsChoice: false, Outcome: s.TerminalOutcome);

        int slotCount = GetSlotCount(in s);
        if ((uint)slot >= (uint)slotCount)
            return new HotStepResult(Executed: false, NeedsChoice: false, Outcome: s.TerminalOutcome);

        ushort potionDefId = s.Potions[slot];
        if (potionDefId == 0 || potionDefId >= rules.Potions.Length)
            return new HotStepResult(Executed: false, NeedsChoice: false, Outcome: s.TerminalOutcome);

        HotPotionDef def = rules.Potions[potionDefId];
        HotCustomPotionId potionId = ResolvePotionId(def);
        if (!def.IsCombatUsable || potionId == HotCustomPotionId.None || potionId == HotCustomPotionId.FairyInABottle)
            return new HotStepResult(Executed: false, NeedsChoice: false, Outcome: s.TerminalOutcome);

        bool needsEnemyTarget = def.Target == TargetType.AnyEnemy || RequiresEnemyTarget(potionId);
        if (needsEnemyTarget && !HotCombatEngine.IsValidEnemyTarget(in s, targetEnemySlot))
            return new HotStepResult(Executed: false, NeedsChoice: false, Outcome: s.TerminalOutcome);

        // Consume slot BEFORE executing effect (matches STS2 PotionCmd.Use: frees slot for EntropicBrew
        // and updates potion belt occupancy for BeltBuckle; safe under restart-from-parent choice protocol).
        s.Potions[slot] = 0;
        s.CombatGeneratedPotionMask &= (ushort)~(1 << slot);

        if (def.OpCount > 0 && potionId != HotCustomPotionId.BeetleJuice)
        {
            HotCombatEngine.ExecutePotionBytecode(ref s, ref cold, rules, scratch, potionDefId, targetEnemySlot);
        }
        else
        {
            ExecutePotionEffect(ref s, ref cold, rules, scratch, potionId, def, targetEnemySlot);
        }

        if (scratch.NeedsChoice)
            return new HotStepResult(Executed: true, NeedsChoice: true, Outcome: s.TerminalOutcome);

        if ((s.Player.Flags & HotCreatureFlags.Alive) != 0)
            HotRelics.OnPotionUsed(ref s, ref cold, rules, scratch, potionDefId);

        if (!scratch.NeedsChoice)
            HotCombatEngine.CheckWinCondition(ref s, rules);

        return new HotStepResult(Executed: true, NeedsChoice: scratch.NeedsChoice, Outcome: s.TerminalOutcome);
    }

    /// <summary>
    /// Automatically consumes the first <c>FairyInABottle</c> potion in the player's belt to prevent
    /// player death, restoring <c>max(1, floor(MaxHp * 30%))</c> HP and recording death-save metrics.
    /// </summary>
    public static bool TryTriggerAutoFairy(ref CombatHotState s, HotRules rules)
    {
        int slotCount = GetSlotCount(in s);
        for (int i = 0; i < slotCount; i++)
        {
            ushort potionDefId = s.Potions[i];
            if (potionDefId == 0 || potionDefId >= rules.Potions.Length)
                continue;

            HotPotionDef def = rules.Potions[potionDefId];
            if (ResolvePotionId(def) != HotCustomPotionId.FairyInABottle)
                continue;

            s.Potions[i] = 0;
            s.CombatGeneratedPotionMask &= (ushort)~(1 << i);
            s.Player.Flags |= HotCreatureFlags.Alive;
            s.CombatFlags &= ~HotCombatFlags.IsAboutToLose;

            int healPct = def.PrimaryValue > 0 ? def.PrimaryValue : 30;
            int healAmt = Math.Max(1, (int)((decimal)s.Player.MaxHp * ((decimal)healPct / 100m)));
            int prevHp = Math.Max(0, s.Player.CurrentHp);
            int nextHp = Math.Min(s.Player.MaxHp, prevHp + healAmt);
            int healed = Math.Max(0, nextHp - prevHp);
            s.Player.CurrentHp = nextHp;

            s.DeathSavePotionHpRestored += healed;
            s.DeathSaveUseCount++;
            if (healed > 0)
                s.Counters[(int)HotCounterSlot.PlayerRecoveredHp] += healed;

            return true;
        }

        return false;
    }

    private static void ExecutePotionEffect(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotCustomPotionId potionId,
        HotPotionDef def,
        int targetEnemySlot)
    {
        byte enemyCreatureSlot = targetEnemySlot >= 0 && targetEnemySlot < s.EnemyCount
            ? (byte)(3 + targetEnemySlot)
            : (byte)0xFF;

        switch (potionId)
        {
            case HotCustomPotionId.Ambergris:
            {
                int healPct = def.PrimaryValue > 0 ? def.PrimaryValue : 50;
                int healAmt = (int)((decimal)s.Player.MaxHp * ((decimal)healPct / 100m));
                if (healAmt > 0)
                    HotCombatEngine.HealCreature(ref s, ref cold, rules, scratch, targetSlot: 0, amount: healAmt);
                if (!scratch.NeedsChoice)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.ExtraTurnPower, 1);
                break;
            }

            case HotCustomPotionId.Ashwater:
            {
                int maxPicks = Math.Min((int)s.HandCount, HotLimits.MaxChoicesPerAction);
                Span<ushort> pickedUids = stackalloc ushort[HotLimits.MaxChoicesPerAction];
                int pickedCount = 0;
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
                for (int i = 0; i < pickedCount && !HotCombatEngine.IsOverOrEnding(in s) && !scratch.NeedsChoice; i++)
                {
                    int idx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Hand, pickedUids[i]);
                    if (idx >= 0)
                        HotCombatEngine.ExhaustCard(ref s, ref cold, rules, scratch, idx, causedByEthereal: false);
                }
                break;
            }

            case HotCustomPotionId.AttackPotion:
                ExecuteChooseGeneratedFreeCard(ref s, ref cold, rules, scratch, rules.CombatCharacterAttackPool, optionCount: 3);
                break;

            case HotCustomPotionId.SkillPotion:
                ExecuteChooseGeneratedFreeCard(ref s, ref cold, rules, scratch, rules.CombatCharacterSkillPool, optionCount: 3);
                break;

            case HotCustomPotionId.PowerPotion:
                ExecuteChooseGeneratedFreeCard(ref s, ref cold, rules, scratch, rules.CombatCharacterPowerPool, optionCount: 3);
                break;

            case HotCustomPotionId.ColorlessPotion:
                ExecuteChooseGeneratedFreeCard(ref s, ref cold, rules, scratch, rules.CombatColorlessPool, optionCount: 3);
                break;

            case HotCustomPotionId.BeetleJuice:
                // STS2 0.113.1 BeetleJuice.cs: applies ShrinkPower with RepeatVar(4), not DamageDecrease(30).
                if (enemyCreatureSlot != 0xFF)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, enemyCreatureSlot, 0, HotPowerId.ShrinkPower, 4);
                break;

            case HotCustomPotionId.BlessingOfTheForge:
            {
                int handCount = s.HandCount;
                for (int i = 0; i < handCount; i++)
                    HotCombatEngine.UpgradeCard(ref s, rules, i);
                break;
            }

            case HotCustomPotionId.BlockPotion:
                HotCombatEngine.GainBlock(ref s, ref cold, rules, scratch, targetSlot: 0, baseAmount: PrimaryOrDefault(def, 12), isCardBlock: false);
                break;

            case HotCustomPotionId.BloodPotion:
            {
                int healPct = PrimaryOrDefault(def, 20);
                int healAmt = (int)((decimal)s.Player.MaxHp * ((decimal)healPct / 100m));
                if (healAmt > 0)
                    HotCombatEngine.HealCreature(ref s, ref cold, rules, scratch, targetSlot: 0, amount: healAmt);
                break;
            }

            case HotCustomPotionId.BoneBrew:
                HotRegentNecro.SummonOsty(ref s, ref cold, rules, scratch, PrimaryOrDefault(def, 15));
                break;

            case HotCustomPotionId.BottledPotential:
            {
                while (s.HandCount > 0)
                    HotPiles.MoveCard(ref s, 0, HotPileKind.Draw, s.DrawCount);

                if (!HotCombatEngine.IsOverOrEnding(in s))
                {
                    HotPiles.ShuffleDiscardIntoDraw(ref s, rules, scratch, cold);
                    if ((s.ActiveHookMask & (ulong)HotHookMask.AfterShuffle) != 0)
                    {
                        HotHookArgs shArgs = new() { ActorSlot = 0 };
                        HotCombatEngine.DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterShuffle, ref shArgs);
                        if (scratch.NeedsChoice)
                            return;
                    }
                    HotCombatEngine.DrawCards(ref s, ref cold, rules, scratch, PrimaryOrDefault(def, 5), fromHandDraw: false);
                }
                break;
            }

            case HotCustomPotionId.BottledSong:
            {
                // STS2 0.113.1 BottledSong.cs: scans ALL unlocked character card pools for Wah/Cho/Ki,
                // rolls all 3 melody cards first, then adds them free-this-turn to Hand.
                Span<ushort> candidates = stackalloc ushort[MaxStackPoolCards];
                Span<ushort> picked = stackalloc ushort[1];
                Span<ushort> rolledCards = stackalloc ushort[3];
                int rolledCount = 0;

                for (int m = 0; m < 3; m++)
                {
                    byte melodyBit = (byte)(1 << m);
                    int candCount = 0;
                    CollectMelodyCandidates(rules.UnlockedCharacterCardPool, rules, melodyBit, candidates, ref candCount);
                    CollectMelodyCandidates(rules.OtherCharacterCardPool, rules, melodyBit, candidates, ref candCount);

                    if (candCount == 0)
                        continue;

                    int got = TakeRandomDistinct(candidates[..candCount], 1, ref s.Rng[(int)HotRngStream.CombatCardGeneration], picked);
                    if (got > 0 && picked[0] != 0)
                        rolledCards[rolledCount++] = picked[0];
                }

                for (int i = 0; i < rolledCount && !HotCombatEngine.IsOverOrEnding(in s) && !scratch.NeedsChoice; i++)
                    AddGeneratedFreeThisTurnToHand(ref s, ref cold, rules, scratch, rolledCards[i]);
                break;
            }

            case HotCustomPotionId.Clarity:
                HotCombatEngine.DrawCards(ref s, ref cold, rules, scratch, 1, fromHandDraw: false);
                if (!scratch.NeedsChoice)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.ClarityPower, 3);
                break;

            case HotCustomPotionId.CosmicConcoction:
            {
                int count = PrimaryOrDefault(def, 3);
                Span<ushort> picked = stackalloc ushort[8];
                int cap = Math.Min(count, picked.Length);
                int got = TakeRandomDistinct(rules.CombatColorlessPool, cap, ref s.Rng[(int)HotRngStream.CombatCardGeneration], picked[..cap]);
                for (int i = 0; i < got && !HotCombatEngine.IsOverOrEnding(in s) && !scratch.NeedsChoice; i++)
                    HotCombatEngine.AddGeneratedFixedCard(ref s, ref cold, rules, scratch, picked[i], HotPileKind.Hand, dstOffset: -1, upgraded: true, createdByPlayer: true);
                break;
            }

            case HotCustomPotionId.CrystallineEuphoria:
            {
                int joyCount = PrimaryOrDefault(def, 3);
                ushort joyId = rules.GetCardDefId(HotCustomCardId.Joy);
                if (joyId != 0)
                {
                    for (int i = 0; i < joyCount && !HotCombatEngine.IsOverOrEnding(in s) && !scratch.NeedsChoice; i++)
                        HotCombatEngine.AddGeneratedFixedCard(ref s, ref cold, rules, scratch, joyId, HotPileKind.Hand, dstOffset: -1, upgraded: false, createdByPlayer: true);
                }

                if (scratch.NeedsChoice || HotCombatEngine.IsOverOrEnding(in s))
                    return;

                ushort sadnessId = rules.GetCardDefId(HotCustomCardId.Sadness);
                int maxHand = s.MaxHandSize > 0 ? s.MaxHandSize : 10;
                int fillCount = Math.Max(0, maxHand - s.HandCount);
                if (sadnessId != 0 && fillCount > 0)
                {
                    for (int i = 0; i < fillCount && !HotCombatEngine.IsOverOrEnding(in s) && !scratch.NeedsChoice; i++)
                        HotCombatEngine.AddGeneratedFixedCard(ref s, ref cold, rules, scratch, sadnessId, HotPileKind.Hand, dstOffset: -1, upgraded: false, createdByPlayer: true);
                }
                break;
            }

            case HotCustomPotionId.CunningPotion:
            {
                int shivCount = PrimaryOrDefault(def, 3);
                ushort shivId = rules.GetCardDefId(HotCustomCardId.Shiv);
                if (shivId != 0)
                {
                    for (int i = 0; i < shivCount && !HotCombatEngine.IsOverOrEnding(in s) && !scratch.NeedsChoice; i++)
                        HotCombatEngine.AddGeneratedFixedCard(ref s, ref cold, rules, scratch, shivId, HotPileKind.Hand, dstOffset: -1, upgraded: true, createdByPlayer: true);
                }
                break;
            }

            case HotCustomPotionId.CureAll:
                HotCombatEngine.GainEnergy(ref s, rules, 1);
                HotCombatEngine.DrawCards(ref s, ref cold, rules, scratch, 2, fromHandDraw: false);
                break;

            case HotCustomPotionId.DexterityPotion:
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.DexterityPower, PrimaryOrDefault(def, 2));
                break;

            case HotCustomPotionId.DistilledChaos:
                HotCombatEngine.AutoPlayFromDrawPile(ref s, ref cold, rules, scratch, PrimaryOrDefault(def, 3), forceExhaust: false);
                break;

            case HotCustomPotionId.DropletOfPrecognition:
            {
                if (s.DrawCount == 0)
                    break;
                if (!HotCombatEngine.RequestChoice(ref s, scratch, HotChoiceKind.DrawPile, sourceCardDefId: 0, minCount: 1, maxCount: 1, out HotChoiceSelection sel))
                    return;
                int idx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Draw, sel.CardUidOrIndex);
                if (idx >= 0)
                    HotCombatEngine.MoveCardToHandOrDiscard(ref s, idx);
                break;
            }

            case HotCustomPotionId.Duplicator:
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.DuplicationPower, 1);
                break;

            case HotCustomPotionId.EnergyPotion:
                HotCombatEngine.GainEnergy(ref s, rules, PrimaryOrDefault(def, 2));
                break;

            case HotCustomPotionId.EntropicBrew:
                ExecuteEntropicBrew(ref s, ref cold, rules, scratch);
                break;

            case HotCustomPotionId.EssenceOfDarkness:
            {
                int capacity = s.OrbCapacity;
                for (int i = 0; i < capacity && !HotCombatEngine.IsOverOrEnding(in s) && !scratch.NeedsChoice; i++)
                    HotCombatEngine.ChannelOrb(ref s, ref cold, rules, scratch, HotOrbId.Dark);
                break;
            }

            case HotCustomPotionId.ExplosiveAmpoule:
                HotCombatEngine.DamageAllEnemies(ref s, ref cold, rules, scratch, dealerSlot: 0, baseDamage: PrimaryOrDefault(def, 10), isPoweredAttack: false);
                break;

            case HotCustomPotionId.FirePotion:
                if (enemyCreatureSlot != 0xFF)
                    HotCombatEngine.DealDamage(ref s, ref cold, rules, scratch, dealerSlot: 0, targetSlot: enemyCreatureSlot, baseDamage: PrimaryOrDefault(def, 20), isPoweredAttack: false);
                break;

            case HotCustomPotionId.FlexPotion:
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.FlexPotionPower, PrimaryOrDefault(def, 5));
                break;

            case HotCustomPotionId.FocusPotion:
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.FocusPower, PrimaryOrDefault(def, 2));
                break;

            case HotCustomPotionId.Fortifier:
            {
                int extraBlock = s.Player.Block * 2;
                if (extraBlock > 0)
                    HotCombatEngine.GainBlock(ref s, ref cold, rules, scratch, targetSlot: 0, baseAmount: extraBlock, isCardBlock: false);
                break;
            }

            case HotCustomPotionId.FoulPotion:
            {
                int dmg = PrimaryOrDefault(def, 12);
                Span<byte> enemies = stackalloc byte[HotLimits.MaxEnemies];
                int enemyCount = HotCombatEngine.SnapshotLivingEnemies(in s, enemies);

                // STS2 FoulPotion hits all living non-pet creatures in CombatState.Creatures order:
                // Player first, then living enemies in roster order.
                HotCombatEngine.DealDamage(ref s, ref cold, rules, scratch, dealerSlot: 0, targetSlot: 0, baseDamage: dmg, isPoweredAttack: false);
                if (scratch.NeedsChoice)
                    return;

                for (int i = 0; i < enemyCount && !HotCombatEngine.IsOverOrEnding(in s) && !scratch.NeedsChoice; i++)
                {
                    byte eSlot = enemies[i];
                    if ((s.Enemies[eSlot].Vital.Flags & HotCreatureFlags.Alive) == 0)
                        continue;
                    HotCombatEngine.DealDamage(ref s, ref cold, rules, scratch, dealerSlot: 0, targetSlot: (byte)(3 + eSlot), baseDamage: dmg, isPoweredAttack: false);
                }
                break;
            }

            case HotCustomPotionId.FruitJuice:
                HotCombatEngine.GainMaxHp(ref s, ref cold, rules, scratch, targetSlot: 0, amount: PrimaryOrDefault(def, 5));
                break;

            case HotCustomPotionId.FyshOil:
            {
                int amt = PrimaryOrDefault(def, 1);
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.StrengthPower, amt);
                if (!scratch.NeedsChoice)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.DexterityPower, amt);
                break;
            }

            case HotCustomPotionId.GamblersBrew:
            {
                int maxPicks = Math.Min((int)s.HandCount, HotLimits.MaxChoicesPerAction);
                Span<ushort> pickedUids = stackalloc ushort[HotLimits.MaxChoicesPerAction];
                int pickedCount = 0;
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
                if (pickedCount > 0 && !HotCombatEngine.IsOverOrEnding(in s))
                    HotCombatEngine.DiscardCardsAndDraw(ref s, ref cold, rules, scratch, pickedUids[..pickedCount], drawCount: pickedCount);
                break;
            }

            case HotCustomPotionId.GhostInAJar:
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.IntangiblePower, PrimaryOrDefault(def, 1));
                break;

            case HotCustomPotionId.GigantificationPotion:
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.GigantificationPower, PrimaryOrDefault(def, 1));
                break;

            case HotCustomPotionId.GlowwaterPotion:
            {
                while (s.HandCount > 0 && !HotCombatEngine.IsOverOrEnding(in s) && !scratch.NeedsChoice)
                    HotCombatEngine.ExhaustCard(ref s, ref cold, rules, scratch, 0, causedByEthereal: false);
                if (!scratch.NeedsChoice && !HotCombatEngine.IsOverOrEnding(in s))
                    HotCombatEngine.DrawCards(ref s, ref cold, rules, scratch, PrimaryOrDefault(def, 10), fromHandDraw: false);
                break;
            }

            case HotCustomPotionId.HeartOfIron:
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.PlatingPower, PrimaryOrDefault(def, 7));
                break;

            case HotCustomPotionId.KingsCourage:
                HotRegentNecro.Forge(ref s, ref cold, rules, scratch, PrimaryOrDefault(def, 15));
                break;

            case HotCustomPotionId.LiquidBronze:
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.ThornsPower, PrimaryOrDefault(def, 3));
                break;

            case HotCustomPotionId.LiquidMemories:
            {
                if (s.DiscardCount == 0)
                    break;
                if (!HotCombatEngine.RequestChoice(ref s, scratch, HotChoiceKind.DiscardPile, sourceCardDefId: 0, minCount: 1, maxCount: 1, out HotChoiceSelection sel))
                    return;
                int idx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Discard, sel.CardUidOrIndex);
                if (idx >= 0)
                {
                    SetCardFreeThisTurn(ref s.Cards[idx], ref cold);
                    HotCombatEngine.MoveCardToHandOrDiscard(ref s, idx);
                }
                break;
            }

            case HotCustomPotionId.LuckyTonic:
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.BufferPower, PrimaryOrDefault(def, 1));
                break;

            case HotCustomPotionId.MazalethsGift:
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.RitualPower, PrimaryOrDefault(def, 1));
                break;

            case HotCustomPotionId.OrobicAcid:
            {
                Span<ushort> pick = stackalloc ushort[1];
                if (TakeRandomDistinct(rules.CombatCharacterAttackPool, 1, ref s.Rng[(int)HotRngStream.CombatCardGeneration], pick) > 0)
                    AddGeneratedFreeThisTurnToHand(ref s, ref cold, rules, scratch, pick[0]);
                if (!scratch.NeedsChoice && !HotCombatEngine.IsOverOrEnding(in s)
                    && TakeRandomDistinct(rules.CombatCharacterSkillPool, 1, ref s.Rng[(int)HotRngStream.CombatCardGeneration], pick) > 0)
                    AddGeneratedFreeThisTurnToHand(ref s, ref cold, rules, scratch, pick[0]);
                if (!scratch.NeedsChoice && !HotCombatEngine.IsOverOrEnding(in s)
                    && TakeRandomDistinct(rules.CombatCharacterPowerPool, 1, ref s.Rng[(int)HotRngStream.CombatCardGeneration], pick) > 0)
                    AddGeneratedFreeThisTurnToHand(ref s, ref cold, rules, scratch, pick[0]);
                break;
            }

            case HotCustomPotionId.PoisonPotion:
                if (enemyCreatureSlot != 0xFF)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, enemyCreatureSlot, 0, HotPowerId.PoisonPower, PrimaryOrDefault(def, 6));
                break;

            case HotCustomPotionId.PotOfGhouls:
            {
                int soulCount = PrimaryOrDefault(def, 2);
                ushort soulId = rules.GetCardDefId(HotCustomCardId.Soul);
                if (soulId != 0)
                {
                    for (int i = 0; i < soulCount && !HotCombatEngine.IsOverOrEnding(in s) && !scratch.NeedsChoice; i++)
                        HotCombatEngine.AddGeneratedFixedCard(ref s, ref cold, rules, scratch, soulId, HotPileKind.Hand, dstOffset: -1, upgraded: false, createdByPlayer: true);
                }
                break;
            }

            case HotCustomPotionId.PotionOfBinding:
            {
                int amt = PrimaryOrDefault(def, 1);
                HotCombatEngine.ApplyPowerToAllLivingEnemies(ref s, ref cold, rules, scratch, HotPowerId.WeakPower, amt);
                if (!scratch.NeedsChoice)
                    HotCombatEngine.ApplyPowerToAllLivingEnemies(ref s, ref cold, rules, scratch, HotPowerId.VulnerablePower, amt);
                break;
            }

            case HotCustomPotionId.PotionOfCapacity:
                HotCombatEngine.AddOrbSlots(ref s, PrimaryOrDefault(def, 2));
                break;

            case HotCustomPotionId.PotionOfDoom:
                if (enemyCreatureSlot != 0xFF)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, enemyCreatureSlot, 0, HotPowerId.DoomPower, PrimaryOrDefault(def, 33));
                break;

            case HotCustomPotionId.PotionShapedRock:
                if (enemyCreatureSlot != 0xFF)
                    HotCombatEngine.DealDamage(ref s, ref cold, rules, scratch, dealerSlot: 0, targetSlot: enemyCreatureSlot, baseDamage: PrimaryOrDefault(def, 15), isPoweredAttack: false);
                break;

            case HotCustomPotionId.PowderedDemise:
                if (enemyCreatureSlot != 0xFF)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, enemyCreatureSlot, 0, HotPowerId.DemisePower, PrimaryOrDefault(def, 9));
                break;

            case HotCustomPotionId.PrecisionPotion:
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.AimPower, PrimaryOrDefault(def, 21));
                break;

            case HotCustomPotionId.RadiantTincture:
                HotCombatEngine.GainEnergy(ref s, rules, 1);
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.RadiancePower, 3);
                break;

            case HotCustomPotionId.RegenPotion:
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.RegenPower, PrimaryOrDefault(def, 5));
                break;

            case HotCustomPotionId.ShacklingPotion:
                HotCombatEngine.ApplyPowerToAllLivingEnemies(ref s, ref cold, rules, scratch, HotPowerId.ShacklingPotionPower, PrimaryOrDefault(def, 7));
                break;

            case HotCustomPotionId.ShipInABottle:
            {
                int blk = PrimaryOrDefault(def, 10);
                HotCombatEngine.GainBlock(ref s, ref cold, rules, scratch, targetSlot: 0, baseAmount: blk, isCardBlock: false);
                if (!scratch.NeedsChoice)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.BlockNextTurnPower, blk);
                break;
            }

            case HotCustomPotionId.SneckoOil:
            {
                HotCombatEngine.DrawCards(ref s, ref cold, rules, scratch, PrimaryOrDefault(def, 7), fromHandDraw: false);
                if (scratch.NeedsChoice)
                    return;

                int handCount = s.HandCount;
                for (int i = 0; i < handCount; i++)
                {
                    ref HotCard c = ref s.Cards[i];
                    if ((c.Flags & HotCardFlags.CostsX) != 0 || c.BaseEnergyCost < 0)
                        continue;
                    int rolled = HotRng.NextInt(ref s.Rng[(int)HotRngStream.CombatEnergyCosts], 4);
                    SetCardEnergyThisTurnOrUntilPlayed(ref c, ref cold, (short)rolled);
                }
                break;
            }

            case HotCustomPotionId.SoldiersStew:
            {
                int total = s.TotalCardCount;
                for (int i = 0; i < total; i++)
                {
                    ushort cid = s.Cards[i].CardDefId;
                    if (cid != 0 && cid < rules.Cards.Length && rules.Cards[cid].IsStrikeTag)
                    {
                        if (s.Cards[i].BaseReplayCount < byte.MaxValue)
                            s.Cards[i].BaseReplayCount++;
                    }
                }
                break;
            }

            case HotCustomPotionId.SpeedPotion:
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.SpeedPotionPower, PrimaryOrDefault(def, 5));
                break;

            case HotCustomPotionId.StableSerum:
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.RetainHandPower, PrimaryOrDefault(def, 2));
                break;

            case HotCustomPotionId.StarPotion:
                HotCombatEngine.GainStars(ref s, ref cold, rules, scratch, PrimaryOrDefault(def, 3));
                break;

            case HotCustomPotionId.StrengthPotion:
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.StrengthPower, PrimaryOrDefault(def, 2));
                break;

            case HotCustomPotionId.SwiftPotion:
                HotCombatEngine.DrawCards(ref s, ref cold, rules, scratch, PrimaryOrDefault(def, 3), fromHandDraw: false);
                break;

            case HotCustomPotionId.TouchOfInsanity:
            {
                Span<ushort> eligibleUids = stackalloc ushort[HotLimits.MaxStackAllocElements];
                int eligibleCount = 0;
                int handCount = s.HandCount;
                for (int i = 0; i < handCount && eligibleCount < eligibleUids.Length; i++)
                {
                    ref readonly HotCard c = ref s.Cards[i];
                    bool costsLocalEnergy = (c.Flags & HotCardFlags.CostsX) == 0 && HotCostModifiers.GetLocalEnergyCost(in c, cold) > 0;
                    bool costsGlobalEnergy = (c.Flags & HotCardFlags.CostsX) == 0 && HotCombatEngine.GetEffectiveEnergyCost(in s, in c, cold, rules, isInPlayablePile: true) > 0;
                    bool costsLocalStars = (c.Flags & HotCardFlags.HasStarCostX) == 0 && HotCostModifiers.GetEffectiveStarCost(in c, cold) > 0;
                    bool costsGlobalStars = (c.Flags & HotCardFlags.HasStarCostX) == 0 && HotCombatEngine.GetEffectiveStarCost(in s, in c, cold, rules, isInPlayablePile: true) > 0;
                    if (costsLocalEnergy || costsGlobalEnergy || costsLocalStars || costsGlobalStars)
                        eligibleUids[eligibleCount++] = c.CardUid;
                }
                if (eligibleCount == 0)
                    break;
                if (!HotCombatEngine.RequestChoiceFiltered(ref s, scratch, HotChoiceKind.HandCard, sourceCardDefId: 0, minCount: 1, maxCount: 1, eligibleUids[..eligibleCount], out HotChoiceSelection sel))
                    return;
                int idx = HotCombatEngine.FindCardIndexByUid(in s, HotPileKind.Hand, sel.CardUidOrIndex);
                if (idx >= 0)
                    SetCardFreeThisCombat(ref s.Cards[idx], ref cold);
                break;
            }

            case HotCustomPotionId.VulnerablePotion:
                if (enemyCreatureSlot != 0xFF)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, enemyCreatureSlot, 0, HotPowerId.VulnerablePower, PrimaryOrDefault(def, 3));
                break;

            case HotCustomPotionId.WeakPotion:
                if (enemyCreatureSlot != 0xFF)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, enemyCreatureSlot, 0, HotPowerId.WeakPower, PrimaryOrDefault(def, 3));
                break;

            case HotCustomPotionId.FairyInABottle:
            default:
                break;
        }
    }

    private static void ExecuteChooseGeneratedFreeCard(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        ushort[] pool,
        int optionCount)
    {
        if (pool.Length == 0 || optionCount <= 0)
            return;

        Span<ushort> options = stackalloc ushort[8];
        int cap = Math.Min(optionCount, options.Length);
        int rolled = TakeRandomDistinct(pool, cap, ref s.Rng[(int)HotRngStream.CombatCardGeneration], options[..cap]);
        if (rolled == 0)
            return;

        HotCombatEngine.SetPendingGeneratedChoiceOptions(options[..rolled]);
        if (!HotCombatEngine.RequestChoice(ref s, scratch, HotChoiceKind.GeneratedCard, sourceCardDefId: 0, minCount: 0, maxCount: 1, out HotChoiceSelection sel))
            return;

        if (sel.Kind == HotChoiceKind.GeneratedCard && sel.CardDefId != 0 && sel.CardDefId < rules.Cards.Length)
            AddGeneratedFreeThisTurnToHand(ref s, ref cold, rules, scratch, sel.CardDefId);
    }

    private static void ExecuteEntropicBrew(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch)
    {
        ushort[] pool = rules.UnlockedPotionPool;
        if (pool.Length == 0)
            return;

        bool hasSozu = rules.HasRelic(HotRelicId.Sozu);
        int slotCount = GetSlotCount(in s);

        for (int slotIdx = 0; slotIdx < slotCount; slotIdx++)
        {
            if (s.Potions[slotIdx] != 0)
                continue;

            // Matches EntropicBrew.OnUse -> PotionFactory.CreateRandomPotionOutOfCombat (NonCombatGeneration RNG)
            // followed by PotionCmd.TryToProcure (breaks on failure such as Sozu AFTER rolling the first potion).
            ushort chosenDefId = RollRandomPotion(
                pool,
                rules,
                ref s.Rng[(int)HotRngStream.NonCombatGeneration],
                inCombatOnly: false);

            if (hasSozu || chosenDefId == 0)
                break;

            s.Potions[slotIdx] = chosenDefId;
            s.CombatGeneratedPotionMask |= (ushort)(1 << slotIdx);
            HotRelics.OnPotionProcured(ref s, ref cold, rules, scratch, chosenDefId);
            if (scratch.NeedsChoice)
                return;
        }
    }

    /// <summary>
    /// Exact STS2 0.113.1 <c>Alchemize</c>: rolls <c>PotionFactory.CreateRandomPotionInCombat</c>
    /// using <see cref="HotRngStream.CombatPotionGeneration"/> first, then attempts <c>PotionCmd.TryToProcure</c>
    /// into the first open slot (respecting <c>Sozu</c>, marking <c>CombatGeneratedPotionMask</c>,
    /// and firing <c>OnPotionProcured</c>). Returns true iff a potion was actually procured.
    /// </summary>
    internal static bool TryProcureRandomCombatPotion(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch)
    {
        ushort[] pool = rules.UnlockedPotionPool;
        if (pool.Length == 0)
            return false;

        ushort chosenDefId = RollRandomPotion(
            pool,
            rules,
            ref s.Rng[(int)HotRngStream.CombatPotionGeneration],
            inCombatOnly: true);

        if (chosenDefId == 0 || rules.HasRelic(HotRelicId.Sozu))
            return false;

        int slotCount = GetSlotCount(in s);
        for (int slotIdx = 0; slotIdx < slotCount; slotIdx++)
        {
            if (s.Potions[slotIdx] != 0)
                continue;

            s.Potions[slotIdx] = chosenDefId;
            s.CombatGeneratedPotionMask |= (ushort)(1 << slotIdx);
            HotRelics.OnPotionProcured(ref s, ref cold, rules, scratch, chosenDefId);
            return true;
        }

        return false;
    }

    private static ushort RollRandomPotion(
        ushort[] pool,
        HotRules rules,
        ref HotRngState rng,
        bool inCombatOnly)
    {
        // Matches PotionFactory.CreateRandomPotions: roll rarity (<=0.1 Rare, <=0.35 Uncommon, else Common),
        // then NextItem over options matching that rarity.
        float roll = HotRng.NextFloat(ref rng);
        PotionRarity rarity = roll <= 0.1f
            ? PotionRarity.Rare
            : (roll <= 0.35f ? PotionRarity.Uncommon : PotionRarity.Common);

        Span<ushort> rarityCandidates = stackalloc ushort[MaxStackPotions];
        int candCount = 0;
        for (int i = 0; i < pool.Length && candCount < rarityCandidates.Length; i++)
        {
            ushort pDefId = pool[i];
            if (pDefId == 0 || pDefId >= rules.Potions.Length)
                continue;
            HotPotionDef pDef = rules.Potions[pDefId];
            if (inCombatOnly && !pDef.CanBeGeneratedInCombat)
                continue;
            if (pDef.Rarity == rarity)
                rarityCandidates[candCount++] = pDefId;
        }

        if (candCount > 0)
        {
            int pick = HotRng.NextInt(ref rng, candCount);
            return rarityCandidates[pick];
        }

        // Fallback if filtered rarity bucket was empty (should not occur in vanilla pools)
        Span<ushort> allCandidates = stackalloc ushort[MaxStackPotions];
        int allCount = 0;
        for (int i = 0; i < pool.Length && allCount < allCandidates.Length; i++)
        {
            ushort pDefId = pool[i];
            if (pDefId == 0 || pDefId >= rules.Potions.Length)
                continue;
            if (inCombatOnly && !rules.Potions[pDefId].CanBeGeneratedInCombat)
                continue;
            allCandidates[allCount++] = pDefId;
        }
        if (allCount == 0)
            return 0;
        return allCandidates[HotRng.NextInt(ref rng, allCount)];
    }

    internal static void AddGeneratedFreeThisTurnToHand(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        ushort cardDefId)
    {
        if (cardDefId == 0 || cardDefId >= rules.Cards.Length || HotCombatEngine.IsOverOrEnding(in s))
            return;

        HotCardDef def = rules.Cards[cardDefId];
        HotCard card = new()
        {
            CardDefId = cardDefId,
            CardUid = s.NextCardUid++,
            UpgradeLevel = 0,
            BaseEnergyCost = def.BaseCost,
            BaseStarCost = def.BaseStarCost,
            BaseReplayCount = 0,
            ExtraReplayThisTurn = 0,
            LocalKeywordsMask = (HotCardKeywordMask)def.BaseKeywords,
            KeywordsAddedOutsideMask = HotCardKeywordMask.None,
            KeywordsRemovedOutsideMask = HotCardKeywordMask.None,
            Flags = HotCombatEngine.GetInitialCardFlags(def),
        };
        SetCardFreeThisTurn(ref card, ref cold);
        HotCombatEngine.AddGeneratedCardInstance(ref s, ref cold, rules, scratch, in card, HotPileKind.Hand, dstOffset: -1, createdByPlayer: true);
    }

    /// <summary>
    /// Exact STS2 0.113.1 <c>IEnumerableExtensions.TakeRandom(count, rng)</c>:
    /// copies <paramref name="pool"/> onto the stack, runs <see cref="HotRng.UnstableShuffle{T}"/>
    /// (consuming one <c>NextULong</c> from <paramref name="rng"/>), and returns the first <paramref name="count"/> items.
    /// Zero managed heap allocations.
    /// </summary>
    internal static int TakeRandomDistinct(
        ReadOnlySpan<ushort> pool,
        int count,
        ref HotRngState rng,
        Span<ushort> dst)
    {
        if (pool.IsEmpty || count <= 0 || dst.IsEmpty)
            return 0;

        int n = Math.Min(pool.Length, MaxStackPoolCards);
        Span<ushort> copy = stackalloc ushort[n];
        pool[..n].CopyTo(copy);
        HotRng.UnstableShuffle(copy, ref rng);

        int take = Math.Min(count, Math.Min(n, dst.Length));
        copy[..take].CopyTo(dst);
        return take;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetCardFreeThisTurn(ref HotCard card, ref HotColdSidecar? cold)
    {
        if ((card.Flags & HotCardFlags.CostsX) == 0 && card.BaseEnergyCost >= 0)
        {
            HotCostModifiers.AddEnergyModifierExact(
                ref card,
                ref cold,
                amount: 0,
                isAbsolute: true,
                expiration: (int)(LocalCostModifierExpiration.EndOfTurn | LocalCostModifierExpiration.WhenPlayed),
                isReduceOnly: false);
        }
        if ((card.Flags & HotCardFlags.HasStarCostX) == 0)
        {
            HotCostModifiers.AddTempStarCost(
                ref card,
                ref cold,
                cost: 0,
                clearsWhenTurnEnds: true,
                clearsWhenCardIsPlayed: true);
        }
    }

    internal static void CollectMelodyCandidates(
        ushort[] pool,
        HotRules rules,
        byte melodyMask,
        Span<ushort> candidates,
        ref int candCount)
    {
        for (int i = 0; i < pool.Length && candCount < candidates.Length; i++)
        {
            ushort cid = pool[i];
            if (cid == 0 || cid >= rules.Cards.Length)
                continue;
            HotCardDef cDef = rules.Cards[cid];
            if ((cDef.MelodyTagMask & melodyMask) == 0 || !IsCombatGeneratable(cDef))
                continue;

            bool duplicate = false;
            for (int j = 0; j < candCount; j++)
            {
                if (candidates[j] == cid)
                {
                    duplicate = true;
                    break;
                }
            }
            if (!duplicate)
                candidates[candCount++] = cid;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetCardFreeThisCombat(ref HotCard card, ref HotColdSidecar? cold)
    {
        if ((card.Flags & HotCardFlags.CostsX) == 0 && card.BaseEnergyCost >= 0)
        {
            HotCostModifiers.AddEnergyModifierExact(
                ref card,
                ref cold,
                amount: 0,
                isAbsolute: true,
                expiration: (int)LocalCostModifierExpiration.EndOfCombat,
                isReduceOnly: false);
        }
        if ((card.Flags & HotCardFlags.HasStarCostX) == 0)
        {
            HotCostModifiers.AddTempStarCost(
                ref card,
                ref cold,
                cost: 0,
                clearsWhenTurnEnds: false,
                clearsWhenCardIsPlayed: false);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetCardEnergyThisTurnOrUntilPlayed(ref HotCard card, ref HotColdSidecar? cold, short cost)
    {
        if ((card.Flags & HotCardFlags.CostsX) == 0 && card.BaseEnergyCost >= 0)
        {
            HotCostModifiers.AddEnergyModifierExact(
                ref card,
                ref cold,
                amount: cost,
                isAbsolute: true,
                expiration: (int)(LocalCostModifierExpiration.EndOfTurn | LocalCostModifierExpiration.WhenPlayed),
                isReduceOnly: false);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetSlotCount(in CombatHotState s)
        => s.PotionSlotCount > 0 ? Math.Min((int)s.PotionSlotCount, HotLimits.MaxPotions) : HotLimits.MaxPotions;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static HotCustomPotionId ResolvePotionId(HotPotionDef def)
    {
        if (def.PotionId != HotCustomPotionId.None)
            return def.PotionId;
        if (def.CustomPotionId != HotCustomPotionId.None)
            return def.CustomPotionId;
        return HotPotionCatalog.FindByEntry(def.Entry);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int PrimaryOrDefault(HotPotionDef def, int fallback)
        => def.PrimaryValue > 0 ? def.PrimaryValue : fallback;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsCombatGeneratable(HotCardDef def)
        => def.CanBeGeneratedInCombat
            && def.Rarity is not (CardRarity.Basic or CardRarity.Ancient or CardRarity.Event);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool RequiresEnemyTarget(HotCustomPotionId potionId)
        => potionId is HotCustomPotionId.BeetleJuice
            or HotCustomPotionId.FirePotion
            or HotCustomPotionId.PoisonPotion
            or HotCustomPotionId.PotionOfDoom
            or HotCustomPotionId.PotionShapedRock
            or HotCustomPotionId.PowderedDemise
            or HotCustomPotionId.VulnerablePotion
            or HotCustomPotionId.WeakPotion;
}
